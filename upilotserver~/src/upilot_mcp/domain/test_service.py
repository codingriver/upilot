from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import json
import logging
import os
import shlex
import subprocess
import sys
import time
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from ..config import CONFIG, diagnose_client_configs
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import REGISTRY, REGISTRY_VERSION, dispatch_public_tool
from ..source_identity import source_identity
from ..test_job_context import TEST_JOB_CONTEXT, checkpoint

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="


def _response_summary(response: ToolResponse) -> dict:
    return {
        "ok": response.ok, "data": response.data or {},
        "error": ({"code": response.error.code, "message": response.error.message, "detail": response.error.detail} if response.error else None),
    }


def _normalize_reflection_parameters(parameters: list | None) -> list:
    if not parameters:
        return []
    normalized = []
    for value in parameters:
        if value is None:
            normalized.append(None)
        elif isinstance(value, (list, dict)):
            normalized.append(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
        else:
            normalized.append(str(value))
    return normalized


def _json_dumps_or_empty(value: object | None) -> str:
    if value is None:
        return ""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))

class TestDomainService:
    # This is an application service, not a pytest test class.  Its public
    # methods intentionally mirror MCP tool names such as ``test_run``.
    __test__ = False

    async def test_run(
        self, test_mode: str = "EditMode", test_filter: str = "",
        test_names: list[str] | None = None, fixtures: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        try:
            selector_payload = self._test_selector_payload(test_filter, test_names, fixtures)
        except ValueError as exc:
            return fail(request_id, "TEST_SELECTORS_INVALID", str(exc))
        scene_state = await self.dispatcher.call(
            new_id("req"), "scene.list", {}, timeout_ms=30000
        )
        if not scene_state.ok:
            source_error = scene_state.error
            return fail(
                request_id,
                "TEST_PREFLIGHT_FAILED",
                "Could not verify whether open Unity scenes have unsaved changes.",
                {
                    "blockedReason": "SceneStateUnavailable",
                    "sourceError": (
                        {
                            "code": source_error.code,
                            "message": source_error.message,
                            "detail": source_error.detail,
                        }
                        if source_error
                        else None
                    ),
                    "nextAction": "Restore the Unity Bridge connection, call unity_scene_list, and retry without saving or discarding scenes automatically.",
                },
            )

        dirty_scenes = [
            {
                "scenePath": str(scene.get("scenePath") or ""),
                "sceneName": str(scene.get("sceneName") or ""),
                "isActive": bool(scene.get("isActive", False)),
            }
            for scene in ((scene_state.data or {}).get("scenes") or [])
            if bool(scene.get("isDirty", False))
        ]
        dirty_scene_policy = CONFIG.unsaved_scene_policy
        dirty_scene_action = "none"
        if dirty_scenes:
            if dirty_scene_policy == "autoSave":
                untitled_scenes = [scene for scene in dirty_scenes if not scene["scenePath"]]
                if untitled_scenes:
                    return fail(
                        request_id,
                        "UNSAVED_SCENES",
                        "Unity tests were not started because an untitled scene cannot be saved automatically.",
                        {
                            "blockedReason": "UntitledUnsavedScenes",
                            "dirtyScenePolicy": dirty_scene_policy,
                            "dirtySceneCount": len(dirty_scenes),
                            "dirtyScenes": dirty_scenes,
                            "untitledScenes": untitled_scenes,
                            "requestedTestMode": test_mode,
                            "requestedTestFilter": test_filter,
                            "nextAction": "Save each untitled scene to an explicit Assets/*.unity path, then retry.",
                        },
                    )

                for scene in dirty_scenes:
                    saved = await self.dispatcher.call(
                        new_id("req"),
                        "scene.save",
                        {"scenePath": scene["scenePath"]},
                        timeout_ms=30000,
                    )
                    if not saved.ok:
                        return fail(
                            request_id,
                            "UNSAVED_SCENES",
                            "Unity tests were not started because UPilot could not save every dirty scene.",
                            {
                                "blockedReason": "AutoSaveFailed",
                                "dirtyScenePolicy": dirty_scene_policy,
                                "dirtySceneCount": len(dirty_scenes),
                                "dirtyScenes": dirty_scenes,
                                "failedScene": scene,
                                "saveError": _response_summary(saved)["error"],
                                "requestedTestMode": test_mode,
                                "requestedTestFilter": test_filter,
                                "nextAction": "Resolve the reported scene save error, then retry.",
                            },
                        )

                verified_scene_state = await self.dispatcher.call(
                    new_id("req"), "scene.list", {}, timeout_ms=30000
                )
                if not verified_scene_state.ok:
                    return fail(
                        request_id,
                        "TEST_PREFLIGHT_FAILED",
                        "UPilot saved dirty scenes but could not verify their final state.",
                        {
                            "blockedReason": "SceneStateUnavailableAfterAutoSave",
                            "dirtyScenePolicy": dirty_scene_policy,
                            "sourceError": _response_summary(verified_scene_state)["error"],
                            "nextAction": "Restore the Unity Bridge connection and verify scene state before retrying.",
                        },
                    )
                remaining_dirty_scenes = [
                    {
                        "scenePath": str(scene.get("scenePath") or ""),
                        "sceneName": str(scene.get("sceneName") or ""),
                        "isActive": bool(scene.get("isActive", False)),
                    }
                    for scene in ((verified_scene_state.data or {}).get("scenes") or [])
                    if bool(scene.get("isDirty", False))
                ]
                if remaining_dirty_scenes:
                    return fail(
                        request_id,
                        "UNSAVED_SCENES",
                        "Unity tests were not started because some scenes remained unsaved after auto-save.",
                        {
                            "blockedReason": "AutoSaveIncomplete",
                            "dirtyScenePolicy": dirty_scene_policy,
                            "dirtySceneCount": len(remaining_dirty_scenes),
                            "dirtyScenes": remaining_dirty_scenes,
                            "requestedTestMode": test_mode,
                            "requestedTestFilter": test_filter,
                            "nextAction": "Save the remaining scenes manually and retry.",
                        },
                    )
                dirty_scene_action = "autoSaved"
            elif dirty_scene_policy == "ignore":
                dirty_scene_action = "ignored"
            else:
                return fail(
                    request_id,
                    "UNSAVED_SCENES",
                    "Unity tests were not started because one or more open scenes have unsaved changes.",
                    {
                        "blockedReason": "UnsavedScenes",
                        "dirtyScenePolicy": dirty_scene_policy,
                        "dirtySceneCount": len(dirty_scenes),
                        "dirtyScenes": dirty_scenes,
                        "requestedTestMode": test_mode,
                        "requestedTestFilter": test_filter,
                        "nextAction": "Set the unsaved-scene policy to autoSave or ignore in UPilot advanced settings, or save/discard scenes manually.",
                    },
                )

        normalized_filter = test_filter
        filter_diagnostics: dict[str, object] = {
            "requestedTestFilter": test_filter,
            "normalizedTestFilter": test_filter,
            "filterResolution": "unchanged",
            "dirtyScenePolicy": dirty_scene_policy,
            "dirtySceneAction": dirty_scene_action,
            "initialDirtySceneCount": len(dirty_scenes),
        }
        if self._is_short_test_class_filter(test_filter):
            listed = await self.test_list(test_mode=test_mode)
            if not listed.ok:
                source_error = listed.error
                return fail(
                    request_id,
                    "TEST_FILTER_DISCOVERY_FAILED",
                    "Could not resolve the short test class filter against Unity Test Runner discovery.",
                    {
                        **filter_diagnostics,
                        "sourceError": (
                            {
                                "code": source_error.code,
                                "message": source_error.message,
                                "detail": source_error.detail,
                            }
                            if source_error
                            else None
                        ),
                        "nextAction": "Call unity_test_list without a filter, inspect discovered test names, then retry with a fully qualified class or test name.",
                    },
                )

            listed_data = listed.data or {}
            tests = [str(item) for item in (listed_data.get("tests") or []) if item]
            class_names = sorted({self._test_class_name(item) for item in tests if self._test_class_name(item)})
            matches = [
                class_name
                for class_name in class_names
                if class_name.rsplit(".", 1)[-1] == test_filter
            ]
            filter_diagnostics.update(
                {
                    "discoveredTestCount": int(listed_data.get("discoveredCount") or len(tests)),
                    "discoveredClassCount": len(class_names),
                    "filterCandidates": matches,
                }
            )
            if not matches:
                return fail(
                    request_id,
                    "TEST_FILTER_NO_MATCH",
                    f"No discovered test class matches short name '{test_filter}'.",
                    {
                        **filter_diagnostics,
                        "noTestsReason": "FilterSyntaxOrScopeMismatch",
                        "nextAction": "Call unity_test_list without a filter and use a returned fully qualified class or test name.",
                    },
                )
            if len(matches) > 1:
                return fail(
                    request_id,
                    "TEST_FILTER_AMBIGUOUS",
                    f"Short test class name '{test_filter}' matches multiple discovered classes.",
                    {
                        **filter_diagnostics,
                        "nextAction": "Retry with one fully qualified class name from filterCandidates.",
                    },
                )
            normalized_filter = matches[0]
            filter_diagnostics.update(
                {
                    "normalizedTestFilter": normalized_filter,
                    "filterResolution": "short_class_to_fully_qualified_class",
                }
            )

        payload: dict = {"testMode": test_mode, **selector_payload}
        if normalized_filter:
            payload["testFilter"] = normalized_filter
        checkpoint("starting", startIntentSent=True)
        result = await self.dispatcher.call(
            request_id, "test.run", payload, timeout_ms=300000
        )
        checkpoint("observing", runGuid=str((result.data or {}).get("runGuid") or ""))
        if result.data is not None:
            result.data.update(filter_diagnostics)
        elif result.error is not None:
            result.error.detail.update(filter_diagnostics)
        return result

    @staticmethod
    def _test_selector_payload(test_filter, test_names, fixtures) -> dict:
        if test_names is None and fixtures is None:
            return {}
        if test_filter.strip():
            raise ValueError("testFilter cannot be combined with testNames or fixtures.")
        for values in (test_names, fixtures):
            if values is not None and (
                not isinstance(values, list)
                or any(not isinstance(value, str) or not value.strip() for value in values)
            ):
                raise ValueError("Selectors must be arrays of non-empty exact names.")
        if not 1 <= len(test_names or []) + len(fixtures or []) <= 256:
            raise ValueError("Provide 1 to 256 selectors; empty arrays never mean all tests.")
        return {key: value for key, value in (("testNames", test_names), ("fixtures", fixtures)) if value is not None}

    @staticmethod
    def _is_short_test_class_filter(test_filter: str) -> bool:
        value = test_filter.strip()
        return bool(value) and "." not in value and not value.lower().startswith("regex:")

    @staticmethod
    def _test_class_name(test_name: str) -> str:
        value = test_name.strip()
        if "." not in value:
            return ""
        return value.rsplit(".", 1)[0]

    async def test_results(self, run_guid: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"runGuid": run_guid} if run_guid else {}
        return await self.dispatcher.call(request_id, "test.results", payload)

    async def test_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.status", {})

    async def test_cancel(self, run_guid: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"runGuid": run_guid} if run_guid else {}
        return await self.dispatcher.call(
            request_id, "test.cancel", payload, timeout_ms=30000
        )

    async def test_force_reset(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "test.force_reset", {}, timeout_ms=30000
        )

    async def test_force_cleanup(self, run_guid: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"runGuid": run_guid} if run_guid else {}
        return await self.dispatcher.call(
            request_id, "test.force_cleanup", payload, timeout_ms=30000
        )

    async def test_list(
        self, test_mode: str = "EditMode", test_filter: str = "",
        test_names: list[str] | None = None, fixtures: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        try:
            selectors = self._test_selector_payload(test_filter, test_names, fixtures)
        except ValueError as exc:
            return fail(request_id, "TEST_SELECTORS_INVALID", str(exc))
        return await self.dispatcher.call(
            request_id, "test.list", {"testMode": test_mode, "testFilter": test_filter, **selectors}
        )

    async def upilot_acceptance_run(
        self,
        test_mode: str = "EditMode",
        test_filter: str = "",
        timeout_sec: float = 900,
        stop_active_captures: bool = True,
        require_tests: bool = True,
        write_artifact: bool = True,
        test_names: list[str] | None = None,
        fixtures: list[str] | None = None,
    ) -> ToolResponse:
        """Run package acceptance only in explicitly supported repository test projects."""
        request_id = new_id("req")
        started_at = now_ms()
        repository_root = Path(__file__).resolve().parents[4]
        expected_project = (repository_root / "Tests~" / "UPilotTest").resolve()
        accepted_projects = {
            os.path.normcase(str((repository_root / "Tests~" / name).resolve())): name
            for name in ("UPilotTest", "UPilotTest2022")
        }
        report: dict[str, object] = {
            "schemaVersion": 1,
            "workflow": "UPilotPackageAcceptance",
            "startedAt": started_at,
            "testMode": test_mode,
            "testFilter": test_filter,
            "testNames": test_names,
            "fixtures": fixtures,
            "expectedProject": str(expected_project),
            "stoppedConsoleCaptures": [],
            "steps": {},
            "requestId": request_id,
            "writeArtifact": write_artifact,
            "requireTests": require_tests,
            "deadlineAt": started_at + int(max(10, timeout_sec) * 1000),
            "sourceIdentity": source_identity(expected_project.parents[1]),
        }
        context = TEST_JOB_CONTEXT.get()
        if context:
            report["deadlineAt"] = min(report["deadlineAt"], context[0]["deadlineAt"])

        response_summary = _response_summary

        async def finish(passed: bool, code: str = "", message: str = "") -> ToolResponse:
            return self._finish_acceptance_report(report, passed, code, message)

        checkpoint("preflight", acceptanceReport=report)
        try:
            self._test_selector_payload(test_filter, test_names, fixtures)
        except ValueError as exc:
            return await finish(False, "TEST_SELECTORS_INVALID", str(exc))

        status = await self.mcp_status(force_fresh=True, include_capabilities=False)
        report["steps"]["mcpStatus"] = response_summary(status)
        status_data = status.data or {}
        actual_project_text = str((status_data.get("paths") or {}).get("unityProjectAbsolute") or "")
        try:
            actual_project = Path(actual_project_text).resolve()
        except OSError:
            actual_project = Path(actual_project_text)
        if not status.ok or not status_data.get("connected") or not status_data.get("serverReady"):
            return await finish(False, "UPILOT_ACCEPTANCE_NOT_CONNECTED", "Unity MCP is not connected and ready.")
        project_key = os.path.normcase(str(actual_project))
        if project_key not in accepted_projects:
            report["actualProject"] = str(actual_project)
            return await finish(False, "UPILOT_ACCEPTANCE_PROJECT_MISMATCH", "Connected Unity project is not a supported repository acceptance project.")
        expected_project = actual_project
        report["expectedProject"] = str(expected_project)
        report["acceptanceProject"] = accepted_projects[project_key]
        report["unityVersion"] = str((status_data.get("session") or {}).get("unityVersion") or "")

        ready = await self.ensure_ready(timeout_s=min(120, max(10, timeout_sec)))
        report["steps"]["ensureReady"] = response_summary(ready)
        if not ready.ok or not bool((ready.data or {}).get("ready", False)):
            return await finish(False, "UPILOT_ACCEPTANCE_NOT_READY", "Unity Editor did not become ready for acceptance.")

        captures = await self.console_capture_list(count=200, include_active=True)
        report["steps"]["consoleCaptureList"] = response_summary(captures)
        if not captures.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_CAPTURE_STATE_UNKNOWN", "Could not verify active Console captures.")
        active_sessions = [item for item in ((captures.data or {}).get("sessions") or []) if item.get("active")]
        if active_sessions and not stop_active_captures:
            return await finish(False, "UPILOT_ACCEPTANCE_ACTIVE_CAPTURE", "Persistent Console capture is active; self-tests require capture-safe execution.")
        for session in active_sessions:
            checkpoint("capture_cleanup")
            session_id = str(session.get("sessionId") or "")
            stopped = await self.console_capture_stop(session_id=session_id)
            report["stoppedConsoleCaptures"].append({"sessionId": session_id, **response_summary(stopped)})
            if not stopped.ok:
                return await finish(False, "UPILOT_ACCEPTANCE_CAPTURE_STOP_FAILED", f"Could not stop Console capture {session_id}.")

        checkpoint("compile")
        execution = (ready.data or {}).get("executionState") or {}
        csharp_paths = [
            path
            for folder in (expected_project.parents[1] / "Editor", expected_project.parents[1] / "Runtime",
                           expected_project.parents[1] / "Tests", expected_project / "Assets")
            if folder.exists()
            for path in folder.rglob("*")
            if path.is_file() and path.suffix.lower() in {".cs", ".asmdef", ".asmref", ".rsp"}
        ]
        latest_input_ms = max((int(path.stat().st_mtime * 1000) for path in csharp_paths), default=0)
        if (
            execution.get("authoritative") is True and execution.get("isStale") is False
            and execution.get("terminal") is True and execution.get("errorsVerified") is True
            and execution.get("compilePhase") == "completed" and execution.get("compileErrorCount") == 0
            and int(execution.get("lastCompileVerifiedAt") or 0) >= latest_input_ms > 0
        ):
            compiled = ok(request_id, {**execution, "status": "success", "errorTotal": 0, "reusedVerifiedCompile": True})
        else:
            compiled = await self.safe_compile_and_wait(timeout_s=min(timeout_sec, 600))
        report["steps"]["compile"] = response_summary(compiled)
        compile_data = compiled.data or {}
        if (
            not compiled.ok
            or compile_data.get("errorsVerified") is not True
            or str(compile_data.get("status") or "").lower() not in {"success", "completed"}
            or compile_data.get("errorTotal") != 0
        ):
            return await finish(False, "UPILOT_ACCEPTANCE_COMPILE_FAILED", "UPilot package compilation failed.")

        checkpoint("discovery", acceptanceReport=report)
        selection_args = {"test_mode": test_mode, "test_names": test_names, "fixtures": fixtures}
        listed = await self.test_list(**selection_args, test_filter="" if self._is_short_test_class_filter(test_filter) else test_filter)
        report["steps"]["testList"] = response_summary(listed)
        tests = (listed.data or {}).get("tests") or []
        report["discoveredTestCount"] = len(tests)
        if not listed.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_DISCOVERY_FAILED", "Unity Test Runner discovery failed.")
        if require_tests and not tests:
            return await finish(False, "UPILOT_ACCEPTANCE_NO_TESTS", "No matching tests were discovered.")

        checkpoint("test_preflight", acceptanceReport=report)
        run = await self.test_run(**selection_args, test_filter=test_filter)
        report["steps"]["testRun"] = response_summary(run)
        if not run.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_TEST_START_FAILED", "Unity Test Runner could not start.")
        run_guid = str((run.data or {}).get("runGuid") or "")
        report["runGuid"] = run_guid
        if not run_guid:
            return await finish(False, "UPILOT_ACCEPTANCE_RUN_ID_MISSING", "Test start did not return a runGuid; do not retry an uncertain start.")
        checkpoint("observing", runGuid=run_guid, acceptanceReport=report)
        return await self._complete_acceptance_report(report)

    @staticmethod
    def _finish_acceptance_report(report: dict, passed: bool, code: str = "", message: str = "") -> ToolResponse:
        report["acceptancePassed"] = bool(passed)
        report["endedAt"] = now_ms()
        report["elapsedMs"] = report["endedAt"] - report["startedAt"]
        if code:
            report["failureCode"], report["failureMessage"] = code, message
        if report.get("writeArtifact"):
            artifact_dir = Path(report["expectedProject"]) / "Log" / "UPilotAcceptance" / (str(report["startedAt"]) + "_" + report["requestId"])
            artifact_dir.mkdir(parents=True, exist_ok=True)
            artifact_path = artifact_dir / "summary.json"
            report.pop("artifact", None)
            content = json.dumps(report, ensure_ascii=False, indent=2, default=str).encode("utf-8")
            temporary_path = artifact_path.with_suffix(".json.tmp")
            temporary_path.write_bytes(content)
            os.replace(temporary_path, artifact_path)
            report["artifact"] = {"path": str(artifact_path), "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        checkpoint("finalized", acceptanceReport=report)
        return ok(report["requestId"], report) if passed else fail(report["requestId"], code or "UPILOT_ACCEPTANCE_FAILED", message or "UPilot acceptance failed.", report)

    @staticmethod
    def _test_cleanup_verified(data: dict) -> bool:
        return (
            data.get("cleanupPending") is False and data.get("cleanupSucceeded") is True
            and data.get("cleanupStatus") == "completed" and data.get("cleanupErrors") == []
            and data.get("unresolvedResources") == []
        )

    async def _wait_for_test_result(self, run_guid: str, deadline_at: int) -> ToolResponse:
        context = TEST_JOB_CONTEXT.get()
        state = context[0] if context else {}
        cleanup_deadline = int(state.get("cleanupDeadlineAt") or 0)
        while True:
            if context and state.get("projectPath") != self.server.state._project_path:
                return fail(new_id("req"), "TEST_RECOVERY_REQUIRED", "Connected project changed; no cross-project test observation or cancellation was attempted.")
            final_status = await self.test_results(run_guid=run_guid)
            data = final_status.data or {}
            if final_status.ok and data.get("runGuid") != run_guid:
                return fail(new_id("req"), "TEST_RECOVERY_REQUIRED", "Test result identity does not match the established runGuid.", data)
            terminal = str(data.get("status") or "").lower() in {"completed", "failed", "aborted", "no_tests"}
            if final_status.ok and terminal and data.get("cleanupPending") is not True:
                return final_status
            timed_out = now_ms() >= deadline_at
            if (timed_out or state.get("cancelRequested")) and not cleanup_deadline:
                cleanup_deadline = now_ms() + 60000
                checkpoint("cancelling", cleanupDeadlineAt=cleanup_deadline, timedOut=timed_out)
                cancelled = await self.test_cancel(run_guid=run_guid)
                checkpoint("cleaning_up", cancelResponse=_response_summary(cancelled))
            if cleanup_deadline and now_ms() >= cleanup_deadline:
                return fail(new_id("req"), "TEST_RECOVERY_REQUIRED", "No authoritative cleaned terminal result before the cleanup deadline.", {
                    "runGuid": run_guid, "lastObservation": _response_summary(final_status), "cleanupDeadlineAt": cleanup_deadline,
                })
            await asyncio.sleep(1.0)

    async def _complete_acceptance_report(self, report: dict) -> ToolResponse:
        run_guid = report["runGuid"]
        final_status = await self._wait_for_test_result(run_guid, report["deadlineAt"])
        report["steps"]["testStatus"] = _response_summary(final_status)
        if not final_status.ok:
            return self._finish_acceptance_report(report, False, "UPILOT_ACCEPTANCE_RECOVERY_REQUIRED", "Test outcome or cleanup could not be proven.")
        checkpoint("finalizing", acceptanceReport=report)
        compile_errors = await self.compile_errors()
        console_errors = await self.console_search_logs(count=200, log_type="Error", include_stack_trace=True, exclude_upilot=False, max_message_length=4000)
        report["steps"]["compileErrors"] = _response_summary(compile_errors)
        report["steps"]["consoleErrors"] = _response_summary(console_errors)
        test_data = final_status.data or {}
        cleanup_verified = self._test_cleanup_verified(test_data)
        report["cleanupVerified"] = cleanup_verified
        report["testIdentityVerified"] = (
            test_data.get("runGuid") == run_guid
            and test_data.get("resultAuthoritative") is True
        )
        report["sourceIdentityAfter"] = source_identity(Path(report["expectedProject"]).parents[1])
        report["sourceUnchanged"] = report["sourceIdentityAfter"] == report["sourceIdentity"]
        context = TEST_JOB_CONTEXT.get()
        job = context[0] if context else {}
        passed = (
            final_status.ok
            and report["testIdentityVerified"]
            and cleanup_verified
            and str(test_data.get("status") or "").lower() == "completed"
            and test_data.get("failed") == 0
            and (not report["requireTests"] or int(test_data.get("total") or 0) > 0)
            and not bool(test_data.get("noTests"))
            and compile_errors.ok
            and (compile_errors.data or {}).get("total") == 0
            and console_errors.ok
            and report["sourceUnchanged"]
            and not job.get("cancelRequested")
            and not job.get("timedOut")
        )
        if passed:
            return self._finish_acceptance_report(report, True)
        return self._finish_acceptance_report(report, False, "UPILOT_ACCEPTANCE_FAILED", "Compile, test, cleanup, cancellation or source identity acceptance criteria were not met.")

    async def editor_e2e_run(
        self,
        spec_path: str,
        artifact_dir: str | None = None,
        stop_on_first_failure: bool = True,
        export_zip: bool = False,
        webhook_on_failure: bool = False,
    ) -> ToolResponse:
        """Run M26 YAML E2E spec from disk (orchestrates screenshot/console; UIToolkit steps disabled)."""
        from .editor_e2e.runner import run_editor_e2e_from_path

        return await run_editor_e2e_from_path(
            self,
            spec_path,
            artifact_dir=artifact_dir,
            stop_on_first_failure=stop_on_first_failure,
            export_zip=export_zip,
            webhook_on_failure=webhook_on_failure,
        )

    async def batch_diagnostics(self) -> ToolResponse:
        """Fetch window diagnostics, console summary, and editor state in one call."""
        request_id = new_id("req")
        results = await asyncio.gather(
            self.resource_window_diagnostics(),
            self.resource_console_summary(),
            self.resource_editor_state(),
            return_exceptions=True,
        )
        combined: dict = {}
        labels = ["windowDiagnostics", "consoleSummary", "editorState"]
        for label, r in zip(labels, results):
            if isinstance(r, Exception):
                combined[label] = {"error": str(r)}
            elif not r.ok:
                combined[label] = {"error": r.error.message if r.error else "unknown"}
            else:
                combined[label] = r.data
        return ok(request_id, combined)

    async def verify_window(
        self,
        window_title: str = "upilot",
        include_screenshot: bool = True,
        screenshot_degrade: str | None = None,
    ) -> ToolResponse:
        """All-in-one verification: compile wait → open window → screenshot + diagnostics + console."""
        request_id = new_id("req")

        compile_r = await self.compile_wait(timeout_s=60, poll_interval_s=0.5)
        compile_data = (
            compile_r.data
            if compile_r.ok
            else {"error": compile_r.error.message if compile_r.error else "unknown"}
        )

        diag_results = await asyncio.gather(
            self.editor_windows_list(title_filter=window_title),
            self.resource_window_diagnostics(),
            self.resource_console_summary(),
            return_exceptions=True,
        )

        window_list_result = diag_results[0]
        if (
            not isinstance(window_list_result, Exception)
            and window_list_result.ok
            and not (window_list_result.data or {}).get("windows")
            and window_title
        ):
            # If no title match exists, try the same token as a type filter so
            # verify_window follows the same title/type matching intent as the
            # editor-window screenshot and window mutation tools.
            try:
                type_match = await self.editor_windows_list(type_filter=window_title)
                if type_match.ok and (type_match.data or {}).get("windows"):
                    window_list_result = type_match
            except Exception:
                pass

        window_match: dict = {
            "windowOpen": False,
            "requestedWindowTitle": window_title,
            "source": "editor.windows.list",
        }
        if isinstance(window_list_result, Exception):
            window_match.update({"error": str(window_list_result)})
        elif not window_list_result.ok:
            window_match.update(
                {
                    "error": window_list_result.error.message if window_list_result.error else "unknown",
                    "code": window_list_result.error.code if window_list_result.error else "",
                }
            )
        else:
            data = window_list_result.data or {}
            windows = data.get("windows") or []
            if windows:
                first = windows[0]
                window_match.update(
                    {
                        "windowOpen": True,
                        "matchedTitle": first.get("title", ""),
                        "matchedTypeName": first.get("typeName", ""),
                        "matchedFullTypeName": first.get("fullTypeName", ""),
                        "instanceId": first.get("instanceId", 0),
                        "posX": first.get("posX", 0),
                        "posY": first.get("posY", 0),
                        "width": first.get("width", 0),
                        "height": first.get("height", 0),
                        "docked": first.get("docked", False),
                        "hasFocus": first.get("hasFocus", False),
                        "hasUIToolkit": first.get("hasUIToolkit", False),
                        "multipleMatches": len(windows) > 1,
                        "matchCount": len(windows),
                    }
                )
            else:
                window_match.update({"matchCount": 0})

        screenshot_data = None
        if include_screenshot:
            try:
                deg = screenshot_degrade or getenv(
                    "UPILOT_VERIFY_SCREENSHOT_DEGRADE"
                )
                ss_r = await self.screenshot_editor_window(window_title, degrade=deg)
                if ss_r.ok:
                    screenshot_data = ss_r.data
                else:
                    screenshot_data = {
                        "error": ss_r.error.message if ss_r.error else "unknown",
                        "code": ss_r.error.code if ss_r.error else "",
                    }
            except Exception as e:
                screenshot_data = {"error": str(e)}

        combined: dict = {"compileWait": compile_data, "windowMatch": window_match}
        labels = ["windowDiagnostics", "consoleSummary"]
        for label, r in zip(labels, diag_results[1:]):
            if isinstance(r, Exception):
                combined[label] = {"error": str(r)}
            elif not r.ok:
                combined[label] = {"error": r.error.message if r.error else "unknown"}
            else:
                combined[label] = r.data

        combined["legacyWindowDiagnostics"] = combined.get("windowDiagnostics", {})

        if screenshot_data is not None:
            combined["screenshot"] = screenshot_data

        return ok(request_id, combined)

    async def wait_condition(
        self,
        target_window: str,
        condition_type: str = "element_exists",
        element_name: str = "",
        text_contains: str = "",
        value_equals: str = "",
        type_filter: str = "",
        timeout_s: float = 30,
        poll_interval_s: float = 0.5,
    ) -> ToolResponse:
        """Disabled together with UIToolkit MCP (previously polled uitoolkit.query)."""
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "wait_condition depends on UIToolkit; disabled in this build.",
            {},
        )

    # Optional UPilot Flow test operations.
    @staticmethod
    def _flow_disabled() -> ToolResponse | None:
        if not CONFIG.flow_enabled:
            return fail(new_id("upilot_flow"), "FEATURE_DISABLED", "UPilot Flow is disabled by project configuration.")
        return None

    async def _wait_for_upilot_flow(self, execution_id: str, run_data: dict, timeout_s: float) -> ToolResponse:
        deadline = time.monotonic() + timeout_s
        last_data = run_data
        last_status = ""
        while time.monotonic() < deadline:
            await asyncio.sleep(0.5)
            response = await self.upilot_flow_results(execution_id)
            if not response.ok:
                return response
            last_data = response.data or {}
            status = str(last_data.get("status") or "")
            if status != last_status:
                logger.info("[UPilot Flow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
                last_status = status
            if status in {"completed", "failed", "aborted"}:
                return response
        await self.upilot_flow_cancel(execution_id)
        return fail(
            new_id("upilot_flow"), "UIFLOW_WAIT_TIMEOUT",
            f"Timed out waiting for upilot_flow execution: {execution_id}",
            {"executionId": execution_id, "lastStatus": last_data.get("status")},
        )

    async def upilot_flow_run_file(
        self, yaml_path: str, headed: bool = True, report_output_path: str = "",
        screenshot_path: str = "", screenshot_on_failure: bool = True,
        stop_on_first_failure: bool = True, continue_on_step_failure: bool = False,
        default_timeout_ms: int = 10000, pre_step_delay_ms: int = 0,
        enable_verbose_log: bool = True, debug_on_failure: bool = False,
    ) -> ToolResponse:
        disabled = self._flow_disabled()
        if disabled is not None:
            return disabled
        resolved_yaml = str(Path(yaml_path).expanduser().resolve())
        if not Path(resolved_yaml).is_file():
            return fail(new_id("upilot_flow"), "UIFLOW_YAML_NOT_FOUND",
                        f"YAML file not found: {resolved_yaml}", {"yamlPath": resolved_yaml})
        report_root = report_output_path.strip() or "Reports/UPilot/Flow"
        run = await self.upilot_flow_run(
            yaml_paths=[resolved_yaml], headed=headed, stop_on_first_failure=stop_on_first_failure,
            continue_on_step_failure=continue_on_step_failure, screenshot_on_failure=screenshot_on_failure,
            default_timeout_ms=default_timeout_ms, enable_verbose_log=enable_verbose_log,
            report_path=report_root, debug_on_failure=debug_on_failure,
        )
        if not run.ok:
            return run
        execution_id = str((run.data or {}).get("executionId") or "")
        if not execution_id:
            return fail(new_id("upilot_flow"), "UIFLOW_EXECUTION_ID_MISSING",
                        "upilot_flow.run did not return executionId", {"response": run.data or {}})
        result = await self._wait_for_upilot_flow(
            execution_id, run.data or {}, max(60.0, default_timeout_ms / 1000.0 + 180.0),
        )
        if not result.ok:
            return result
        data = result.data or {}
        case = ((data.get("cases") or [None])[0]) or {}
        return ok(new_id("upilot_flow"), {
            "yamlPath": resolved_yaml,
            "reportOutputPath": str(data.get("reportPath") or report_root),
            "screenshotPath": screenshot_path.strip() or str((Path(report_root) / execution_id / "Screenshots").as_posix()),
            "result": {
                "executionId": execution_id, "status": str(data.get("status") or ""),
                "caseName": case.get("caseName") or data.get("currentCaseName") or Path(resolved_yaml).stem,
                "errorCode": case.get("errorCode") or data.get("errorCode") or "",
                "errorMessage": case.get("errorMessage") or data.get("errorMessage") or "",
                "reportPath": data.get("reportPath") or report_root, "raw": data,
            },
        })

    async def upilot_flow_run_suite(
        self, directory_path: str, headed: bool = True, report_output_path: str = "",
        screenshot_path: str = "", screenshot_on_failure: bool = True,
        stop_on_first_failure: bool = False, continue_on_step_failure: bool = False,
        default_timeout_ms: int = 10000, pre_step_delay_ms: int = 0, enable_verbose_log: bool = True,
    ) -> ToolResponse:
        disabled = self._flow_disabled()
        if disabled is not None:
            return disabled
        resolved_dir = str(Path(directory_path).expanduser().resolve())
        if not Path(resolved_dir).is_dir():
            return fail(new_id("upilot_flow"), "UIFLOW_SUITE_DIR_NOT_FOUND",
                        f"Suite directory not found: {resolved_dir}", {"directoryPath": resolved_dir})
        report_root = report_output_path.strip() or "Reports/UPilot/Flow"
        run = await self.upilot_flow_run(
            yaml_directory=resolved_dir, headed=headed, stop_on_first_failure=stop_on_first_failure,
            continue_on_step_failure=continue_on_step_failure, screenshot_on_failure=screenshot_on_failure,
            default_timeout_ms=default_timeout_ms, enable_verbose_log=enable_verbose_log, report_path=report_root,
        )
        if not run.ok:
            return run
        execution_id = str((run.data or {}).get("executionId") or "")
        if not execution_id:
            return fail(new_id("upilot_flow"), "UIFLOW_EXECUTION_ID_MISSING",
                        "upilot_flow.run did not return executionId", {"response": run.data or {}})
        result = await self._wait_for_upilot_flow(
            execution_id, run.data or {}, max(120.0, default_timeout_ms / 1000.0 + 360.0),
        )
        if not result.ok:
            return result
        data = result.data or {}
        report_path = str(data.get("reportPath") or report_root)
        status = str(data.get("status") or "")
        failed, errors = int(data.get("failed") or 0), int(data.get("errors") or 0)
        return ok(new_id("upilot_flow"), {
            "directoryPath": resolved_dir, "reportOutputPath": report_path,
            "screenshotPath": screenshot_path.strip() or str((Path(report_path) / "Screenshots").as_posix()),
            "result": {
                "executionId": execution_id, "status": status, "total": int(data.get("total") or 0),
                "passed": int(data.get("passed") or 0), "failed": failed, "errors": errors,
                "skipped": int(data.get("skipped") or 0),
                "exitCode": 0 if status == "completed" and failed == 0 and errors == 0 else 1, "raw": data,
            },
        })

    async def upilot_flow_run_async(
        self, yaml_paths: list[str], batch_size: int = 10, batch_offset: int = 0,
        headed: bool = True, report_output_path: str = "", screenshot_path: str = "",
        screenshot_on_failure: bool = True, stop_on_first_failure: bool = False,
        continue_on_step_failure: bool = False, default_timeout_ms: int = 10000,
        pre_step_delay_ms: int = 0, enable_verbose_log: bool = True, debug_on_failure: bool = False,
    ) -> ToolResponse:
        disabled = self._flow_disabled()
        if disabled is not None:
            return disabled
        resolved = []
        for path in yaml_paths:
            path = str(Path(path).expanduser().resolve())
            if not Path(path).is_file():
                return fail(new_id("upilot_flow"), "UIFLOW_YAML_NOT_FOUND",
                            f"YAML file not found: {path}", {"yamlPath": path})
            resolved.append(path)
        report_root = report_output_path.strip() or "Reports/UPilot/Flow"
        run = await self.upilot_flow_run(
            yaml_paths=resolved, headed=headed, stop_on_first_failure=stop_on_first_failure,
            continue_on_step_failure=continue_on_step_failure, screenshot_on_failure=screenshot_on_failure,
            default_timeout_ms=default_timeout_ms, enable_verbose_log=enable_verbose_log,
            report_path=report_root, debug_on_failure=debug_on_failure,
            batch_size=batch_size, batch_offset=batch_offset,
        )
        if not run.ok:
            return run
        data = run.data or {}
        execution_id = str(data.get("executionId") or "")
        if not execution_id:
            return fail(new_id("upilot_flow"), "UIFLOW_EXECUTION_ID_MISSING",
                        "upilot_flow.run did not return executionId", {"response": data})
        return ok(run.request_id, {
            "executionId": execution_id, "status": data.get("status", "queued"),
            "total": int(data.get("total") or 0), "hasMore": bool(data.get("hasMore")),
            "nextOffset": int(data.get("nextOffset") or 0), "totalAll": int(data.get("totalAll") or 0),
            "reportOutputPath": report_root,
            "screenshotPath": screenshot_path.strip() or str((Path(report_root) / "Screenshots").as_posix()),
        })

    async def upilot_flow_run(
        self,
        yaml_paths: list[str] | None = None,
        yaml_directory: str = "",
        headed: bool = False,
        stop_on_first_failure: bool = False,
        continue_on_step_failure: bool = False,
        screenshot_on_failure: bool = True,
        default_timeout_ms: int = 10000,
        enable_verbose_log: bool = False,
        report_path: str = "Reports/UPilot/Flow",
        debug_on_failure: bool = False,
        batch_size: int = 10,
        batch_offset: int = 0,
        total_all: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict[str, object] = {
            "headed": headed,
            "stopOnFirstFailure": stop_on_first_failure,
            "continueOnStepFailure": continue_on_step_failure,
            "screenshotOnFailure": screenshot_on_failure,
            "defaultTimeoutMs": default_timeout_ms,
            "enableVerboseLog": enable_verbose_log,
            "debugOnFailure": debug_on_failure,
            "reportPath": report_path,
            "batchSize": batch_size,
            "batchOffset": batch_offset,
            "totalAll": total_all,
        }
        if yaml_paths:
            payload["yamlPaths"] = yaml_paths
        if yaml_directory:
            payload["yamlDirectory"] = yaml_directory
        return await self.dispatcher.call(
            request_id, "upilot_flow.run", payload, timeout_ms=180000
        )

    async def upilot_flow_validate(self, yaml_path: str) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "upilot_flow.validate",
            {"yamlPath": yaml_path},
            timeout_ms=30000,
        )

    async def upilot_flow_migrate(
        self,
        yaml_paths: list[str] | None = None,
        yaml_directory: str = "",
        target_directory: str = "",
        dry_run: bool = True,
    ) -> ToolResponse:
        payload: dict[str, object] = {"dryRun": dry_run}
        if yaml_paths:
            payload["yamlPaths"] = yaml_paths
        if yaml_directory:
            payload["yamlDirectory"] = yaml_directory
        if target_directory:
            payload["targetDirectory"] = target_directory
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.migrate", payload, timeout_ms=180000
        )

    async def upilot_flow_results(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.results",
            {"executionId": execution_id},
            timeout_ms=30000,
        )

    async def upilot_flow_status(self, execution_id: str = "") -> ToolResponse:
        payload = {"executionId": execution_id} if execution_id else {}
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.status", payload, timeout_ms=30000
        )

    async def upilot_flow_executions(self) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.executions", {}, timeout_ms=30000
        )

    async def upilot_flow_list(self) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.list", {}, timeout_ms=30000
        )

    async def upilot_flow_pause(self, execution_id: str) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.pause", {"executionId": execution_id}, timeout_ms=30000
        )

    async def upilot_flow_resume(self, execution_id: str) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.resume", {"executionId": execution_id}, timeout_ms=30000
        )

    async def upilot_flow_stop(self, execution_id: str) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.stop", {"executionId": execution_id}, timeout_ms=30000
        )

    async def upilot_flow_cancel(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.cancel",
            {"executionId": execution_id},
            timeout_ms=30000,
        )

    async def upilot_flow_force_reset(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.force_reset",
            {},
            timeout_ms=30000,
        )

    async def upilot_flow_force_cleanup(self, execution_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"executionId": execution_id} if execution_id else {}
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.force_cleanup",
            payload,
            timeout_ms=30000,
        )

    async def monohook_tracing_status(self) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"), "monohook.tracing.status", {}, timeout_ms=30000
        )

    async def monohook_tracing_configure(
        self,
        point_ids: list[str] | None = None,
        update_point_enabled: bool = False,
        enabled: bool = False,
        set_stack_trace_capture_mode: bool = False,
        stack_trace_capture_mode: str = "Disabled",
        update_point_stack_trace_selection: bool = False,
        update_capture_stack_trace: bool = False,
        capture_stack_trace: bool = False,
        update_per_object_rate_limit: bool = False,
        enable_per_object_rate_limit: bool = False,
        max_events_per_object_per_second: int = 100,
        update_duplicate_suppression: bool = False,
        suppress_duplicate_events: bool = False,
        duplicate_event_window_milliseconds: int = 100,
        set_master_enabled: bool = False,
        master_enabled: bool = True,
        update_auto_inject_enabled: bool = False,
        auto_inject_enabled: bool = False,
        set_global_filter_profile: bool = False,
        global_filter_profile_id: str = "",
        update_point_filter_overrides_enabled: bool = False,
        point_filter_overrides_enabled: bool = False,
        update_point_filter_profile: bool = False,
        point_filter_profile_id: str = "",
        replace_filter_profiles: bool = False,
        filter_profiles: list[dict] | None = None,
        reset_filter_statistics: bool = False,
        apply: bool = False,
    ) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "monohook.tracing.configure",
            {
                "pointIds": point_ids or [],
                "updatePointEnabled": update_point_enabled,
                "enabled": enabled,
                "setStackTraceCaptureMode": set_stack_trace_capture_mode,
                "stackTraceCaptureMode": stack_trace_capture_mode,
                "updatePointStackTraceSelection": update_point_stack_trace_selection,
                "updateCaptureStackTrace": update_capture_stack_trace,
                "captureStackTrace": capture_stack_trace,
                "updatePerObjectRateLimit": update_per_object_rate_limit,
                "enablePerObjectRateLimit": enable_per_object_rate_limit,
                "maxEventsPerObjectPerSecond": max_events_per_object_per_second,
                "updateDuplicateSuppression": update_duplicate_suppression,
                "suppressDuplicateEvents": suppress_duplicate_events,
                "duplicateEventWindowMilliseconds": duplicate_event_window_milliseconds,
                "setMasterEnabled": set_master_enabled,
                "masterEnabled": master_enabled,
                "updateAutoInjectEnabled": update_auto_inject_enabled,
                "autoInjectEnabled": auto_inject_enabled,
                "setGlobalFilterProfile": set_global_filter_profile,
                "globalFilterProfileId": global_filter_profile_id,
                "updatePointFilterOverridesEnabled": update_point_filter_overrides_enabled,
                "pointFilterOverridesEnabled": point_filter_overrides_enabled,
                "updatePointFilterProfile": update_point_filter_profile,
                "pointFilterProfileId": point_filter_profile_id,
                "replaceFilterProfiles": replace_filter_profiles,
                "filterProfiles": filter_profiles or [],
                "resetFilterStatistics": reset_filter_statistics,
                "apply": apply,
            },
            timeout_ms=30000,
        )

    async def monohook_tracing_events(
        self, max_count: int = 100, consume: bool = False
    ) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "monohook.tracing.events",
            {"maxCount": max_count, "consume": consume},
            timeout_ms=30000,
        )
