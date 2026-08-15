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

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="


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
        self, test_mode: str = "EditMode", test_filter: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"testMode": test_mode}
        if test_filter:
            payload["testFilter"] = test_filter
        return await self.dispatcher.call(
            request_id, "test.run", payload, timeout_ms=300000
        )

    async def test_results(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.results", {})

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

    async def test_list(self, test_mode: str = "EditMode") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "test.list", {"testMode": test_mode}
        )

    async def upilot_acceptance_run(
        self,
        test_mode: str = "EditMode",
        test_filter: str = "",
        timeout_sec: float = 900,
        stop_active_captures: bool = True,
        require_tests: bool = True,
        write_artifact: bool = True,
    ) -> ToolResponse:
        """Run canonical package acceptance without an active persistent Console capture."""
        request_id = new_id("req")
        started_at = now_ms()
        expected_project = (Path(__file__).resolve().parents[4] / "Tests~" / "UPilotTest").resolve()
        report: dict[str, object] = {
            "schemaVersion": 1,
            "workflow": "UPilotPackageAcceptance",
            "startedAt": started_at,
            "testMode": test_mode,
            "testFilter": test_filter,
            "expectedProject": str(expected_project),
            "stoppedConsoleCaptures": [],
            "steps": {},
        }

        def response_summary(response: ToolResponse) -> dict:
            return {
                "ok": response.ok,
                "data": response.data or {},
                "error": ({"code": response.error.code, "message": response.error.message, "detail": response.error.detail} if response.error else None),
            }

        async def finish(passed: bool, code: str = "", message: str = "") -> ToolResponse:
            report["acceptancePassed"] = passed
            report["endedAt"] = now_ms()
            report["elapsedMs"] = int(report["endedAt"]) - started_at
            if code:
                report["failureCode"] = code
                report["failureMessage"] = message
            if write_artifact:
                artifact_dir = expected_project / "Log" / "UPilotAcceptance" / time.strftime("%Y%m%d-%H%M%S")
                artifact_dir.mkdir(parents=True, exist_ok=True)
                artifact_path = artifact_dir / "summary.json"
                content = json.dumps(report, ensure_ascii=False, indent=2, default=str).encode("utf-8")
                artifact_path.write_bytes(content)
                report["artifact"] = {
                    "path": str(artifact_path), "bytes": len(content),
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
            return ok(request_id, report) if passed else fail(request_id, code or "UPILOT_ACCEPTANCE_FAILED", message or "UPilot acceptance failed.", report)

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
        if os.path.normcase(str(actual_project)) != os.path.normcase(str(expected_project)):
            report["actualProject"] = str(actual_project)
            return await finish(False, "UPILOT_ACCEPTANCE_PROJECT_MISMATCH", "Connected Unity project is not the canonical UPilot acceptance project.")

        ready = await self.ensure_ready(timeout_s=min(120, max(10, timeout_sec)))
        report["steps"]["ensureReady"] = response_summary(ready)
        if not ready.ok or not bool((ready.data or {}).get("ready", False)):
            return await finish(False, "UPILOT_ACCEPTANCE_NOT_READY", "Unity Editor did not become ready for acceptance.")

        captures = await self.console_capture_list(count=200, include_active=True)
        report["steps"]["consoleCaptureList"] = response_summary(captures)
        active_sessions = [item for item in ((captures.data or {}).get("sessions") or []) if item.get("active")]
        if active_sessions and not stop_active_captures:
            return await finish(False, "UPILOT_ACCEPTANCE_ACTIVE_CAPTURE", "Persistent Console capture is active; self-tests require capture-safe execution.")
        for session in active_sessions:
            session_id = str(session.get("sessionId") or "")
            stopped = await self.console_capture_stop(session_id=session_id)
            report["stoppedConsoleCaptures"].append({"sessionId": session_id, **response_summary(stopped)})
            if not stopped.ok:
                return await finish(False, "UPILOT_ACCEPTANCE_CAPTURE_STOP_FAILED", f"Could not stop Console capture {session_id}.")

        compiled = await self.safe_compile_and_wait(timeout_s=min(timeout_sec, 600))
        report["steps"]["compile"] = response_summary(compiled)
        if not compiled.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_COMPILE_FAILED", "UPilot package compilation failed.")

        listed = await self.test_list(test_mode=test_mode)
        report["steps"]["testList"] = response_summary(listed)
        tests = (listed.data or {}).get("tests") or []
        report["discoveredTestCount"] = len(tests)
        if not listed.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_DISCOVERY_FAILED", "Unity Test Runner discovery failed.")
        if require_tests and not tests:
            return await finish(False, "UPILOT_ACCEPTANCE_NO_TESTS", "No matching tests were discovered.")

        run = await self.test_run(test_mode=test_mode, test_filter=test_filter)
        report["steps"]["testRun"] = response_summary(run)
        if not run.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_TEST_START_FAILED", "Unity Test Runner could not start.")
        deadline = time.monotonic() + max(10, timeout_sec)
        terminal = {"completed", "failed", "aborted", "no_tests"}
        final_status = run
        while time.monotonic() < deadline:
            final_status = await self.test_status()
            data = final_status.data or {}
            if str(data.get("status") or "").lower() in terminal and not data.get("cleanupPending"):
                break
            await asyncio.sleep(1.0)
        else:
            cleanup = await self.test_force_cleanup(str((run.data or {}).get("runGuid") or ""))
            report["steps"]["timeoutCleanup"] = response_summary(cleanup)
            return await finish(False, "UPILOT_ACCEPTANCE_TEST_TIMEOUT", "Unity tests did not reach a cleaned terminal state before timeout.")
        report["steps"]["testStatus"] = response_summary(final_status)

        compile_errors = await self.compile_errors()
        console_errors = await self.console_search_logs(count=200, log_type="Error", include_stack_trace=True, exclude_upilot=False, max_message_length=4000)
        report["steps"]["compileErrors"] = response_summary(compile_errors)
        report["steps"]["consoleErrors"] = response_summary(console_errors)
        test_data = final_status.data or {}
        passed = (
            final_status.ok
            and str(test_data.get("status") or "").lower() == "completed"
            and int(test_data.get("failed") or 0) == 0
            and not bool(test_data.get("noTests"))
            and compile_errors.ok
            and int((compile_errors.data or {}).get("total") or 0) == 0
        )
        return await finish(passed, "UPILOT_ACCEPTANCE_FAILED", "Compile, test, or cleanup acceptance criteria were not met.")

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
