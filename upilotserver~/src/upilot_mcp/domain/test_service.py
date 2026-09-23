from __future__ import annotations
from ..queue_audit import audited, observed_test

import asyncio
import base64
import binascii
import hashlib
import hmac
import json
import logging
import os
import shlex
import subprocess
import sys
import time
import secrets
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from ..config import CONFIG, diagnose_client_configs
from ..console_evidence import begin_console_evidence, finish_console_evidence
from ..automation_authorization import authorization_status, has_scope
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import REGISTRY, REGISTRY_VERSION, dispatch_public_tool
from ..source_identity import IMPORT_INPUT_SUFFIXES, acceptance_import_inputs, diff_acceptance_import_inputs, source_identity
from ..test_job_context import TEST_JOB_CONTEXT, checkpoint

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
_TEST_RESULT_CURSOR_KEY = secrets.token_bytes(32)


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


def _latest_acceptance_import_input_mtime_ms(source_root: Path, project: Path) -> int:
    """Return the newest importer-sensitive input timestamp without asking Unity to import it."""
    candidates = []
    for folder in (source_root / "Editor", source_root / "Runtime", source_root / "Tests", project / "Assets", project / "Packages"):
        if folder.is_dir():
            candidates.extend(
                path for path in folder.rglob("*")
                if path.is_file() and path.suffix.lower() in IMPORT_INPUT_SUFFIXES
            )
    return max((int(path.stat().st_mtime * 1000) for path in candidates), default=0)

class TestDomainService:
    # This is an application service, not a pytest test class.  Its public
    # methods intentionally mirror MCP tool names such as ``test_run``.
    __test__ = False

    async def test_run(
        self, test_mode: str = "EditMode", test_filter: str | None = None,
        test_names: list[str] | None = None, fixtures: list[str] | None = None,
        assemblies: list[str] | None = None, categories: list[str] | None = None, match_mode: str = "union",
        require_all_selectors_match: bool = True, expected_selection_domain: str = "",
        expected_selection_snapshot_id: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        try:
            test_filter, selector_payload = self._test_selector_payload(
                test_filter, test_names, fixtures, assemblies, categories, match_mode, require_all_selectors_match,
            )
            expected_selection = self._expected_selection_identity(
                expected_selection_domain, expected_selection_snapshot_id,
            )
        except ValueError as exc:
            return fail(request_id, "TEST_SELECTORS_INVALID", str(exc))
        has_selector_arrays = any(key in selector_payload for key in ("testNames", "fixtures", "assemblies", "categories"))
        if has_selector_arrays and require_all_selectors_match:
            discovered = await self.test_list(
                test_mode=test_mode,
                test_filter=test_filter,
                test_names=test_names,
                fixtures=fixtures,
                assemblies=assemblies,
                categories=categories,
                match_mode=match_mode,
                require_all_selectors_match=True,
            )
            if not discovered.ok:
                return fail(
                    request_id,
                    "TEST_SELECTOR_DISCOVERY_FAILED",
                    "Could not verify every requested test selector before starting the Runner.",
                    {
                        "runnerStartAttempted": False,
                        "sourceError": _response_summary(discovered)["error"],
                        "nextAction": "Restore the Unity Bridge connection, call unity_test_list with the same selectors, then retry.",
                    },
                )
            selection = discovered.data or {}
            if not bool(selection.get("selectionValid", True)):
                return fail(
                    request_id,
                    "TEST_SELECTOR_UNMATCHED",
                    "One or more requested test selectors did not match discovery; TestRunnerApi.Execute was not called.",
                    {
                        **selection,
                        "runnerStartAttempted": False,
                        "nextAction": "Call unity_test_list with the same selectors and choose exact discovered values before retrying.",
                    },
                )
            # A selector intersection can be completely valid while resolving to
            # no leaves.  Do not turn that precise request into an unfiltered
            # Runner invocation (or even a scene-preparation write).  The Bridge
            # includes matchedCount for authoritative discovery responses; retain
            # compatibility with older Bridges that did not expose the field.
            matched_count = selection.get("matchedCount")
            if isinstance(matched_count, int) and not isinstance(matched_count, bool) and matched_count == 0:
                return ok(
                    request_id,
                    {
                        **selection,
                        "status": "no_tests",
                        "phase": "no_tests",
                        "noTests": True,
                        "terminal": True,
                        "runnerStartAttempted": False,
                        "terminalReason": "The requested selectors matched no common test leaf; TestRunnerApi.Execute was not called.",
                    },
                )
            snapshot_identity = self._selection_snapshot_identity(selection)
            if snapshot_identity is None:
                return self._selection_stale_response(
                    request_id, selection, expected_selection,
                    "Discovery did not return a complete selection snapshot identity; TestRunnerApi.Execute was not called.",
                )
            if expected_selection is not None and expected_selection != snapshot_identity:
                return self._selection_stale_response(
                    request_id, selection, expected_selection,
                    "The supplied selection snapshot no longer matches discovery; TestRunnerApi.Execute was not called.",
                )
            expected_selection = snapshot_identity
        dirty_scene_policy = CONFIG.unsaved_scene_policy
        # Legacy explicit autoSave/ignore remains effective until granular scopes are used.
        if dirty_scene_policy != "block" and CONFIG.automation_authorization_scopes and not has_scope("scenePolicyExecution", CONFIG.automation_authorization_scopes):
            return fail(request_id, "AUTOMATION_AUTHORIZATION_REQUIRED", "The selected scene policy is disabled by Advanced Settings.", {
                "requiredScope": "scenePolicyExecution", "automationAuthorization": authorization_status(CONFIG.automation_authorization_scopes, CONFIG.automation_authorization_catalog_hash, CONFIG.automation_authorization_scope_version), "runnerStartAttempted": False,
            })
        preparation = await self.dispatcher.call(new_id("req"), "scene.prepareForAutomation", {"policy": dirty_scene_policy}, timeout_ms=45000)
        if not preparation.ok or not bool((preparation.data or {}).get("prepared", False)):
            detail = preparation.data or {}
            return fail(request_id, "UNSAVED_SCENES" if preparation.ok else "TEST_PREFLIGHT_FAILED", "Unity tests were not started because scene preparation did not complete.", {
                "blockedReason": "UnsavedScenes" if dirty_scene_policy == "block" and preparation.ok else "ScenePreparationFailed",
                "dirtyScenePolicy": dirty_scene_policy, "scenePreparation": detail,
                "runnerStartAttempted": False, "sourceError": _response_summary(preparation)["error"],
            })
        dirty_scene_action = str((preparation.data or {}).get("action") or "none")

        normalized_filter = test_filter
        filter_diagnostics: dict[str, object] = {
            "requestedTestFilter": test_filter,
            "normalizedTestFilter": test_filter,
            "filterResolution": "unchanged",
            "dirtyScenePolicy": dirty_scene_policy,
            "dirtySceneAction": dirty_scene_action,
            "initialDirtySceneCount": len((preparation.data or {}).get("items") or []),
            "scenePreparation": preparation.data or {},
        }
        if dirty_scene_action != "none":
            filter_diagnostics["automationAuthorization"] = {
                "scopeKey": "scenePolicyExecution", "source": "advanced-settings" if CONFIG.automation_authorization_scopes else "legacy-explicit-scene-policy",
                "target": {"project": "current", "sceneCount": len((preparation.data or {}).get("items") or [])},
                "preview": {"policy": dirty_scene_policy}, "action": dirty_scene_action, "result": "prepared",
                "sideEffectsMayHaveOccurred": bool((preparation.data or {}).get("sideEffectsMayHaveOccurred", False)),
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

        payload: dict = {
            "testMode": test_mode,
            "dirtyScenePolicy": dirty_scene_policy,
            **selector_payload,
        }
        if expected_selection is not None:
            payload.update({
                "expectedSelectionDomain": expected_selection[0],
                "expectedSelectionSnapshotId": expected_selection[1],
            })
        if normalized_filter:
            payload["testFilter"] = normalized_filter
        # Persist the send boundary before entering the dispatcher.  A thrown
        # transport call leaves this at ``sent_unknown`` so restart recovery
        # observes the original run rather than issuing a second Execute.
        try:
            console_evidence = await begin_console_evidence(self.dispatcher, self.server.state)
        except Exception as exc:
            console_evidence = {
                "source": "unavailable", "coverage": "unavailable",
                "gapReason": f"start_boundary_failed:{type(exc).__name__}", "logs": [],
            }
        checkpoint("starting", startIntentSent=True, startSendState="sent_unknown")
        result = await self.dispatcher.call(
            request_id, "test.run", payload, timeout_ms=300000
        )
        checkpoint(
            "observing",
            startSendState="response_received",
            runGuid=str((result.data or {}).get("runGuid") or ""),
        )
        if result.data is not None:
            result.data.update(filter_diagnostics)
            result.data["consoleEvidence"] = console_evidence
            run_guid = str(result.data.get("runGuid") or "")
            state_store = getattr(getattr(self, "server", None), "state", None)
            if run_guid and hasattr(state_store, "save_console_evidence"):
                state_store.save_console_evidence("runGuid", run_guid, console_evidence)
        elif result.error is not None:
            result.error.detail.update(filter_diagnostics)
        return result

    @staticmethod
    def _test_selector_payload(test_filter, test_names, fixtures, assemblies=None, categories=None,
                               match_mode="union", require_all_selectors_match=True) -> tuple[str, dict]:
        if test_filter is None:
            test_filter = ""
        if not isinstance(test_filter, str):
            raise ValueError("testFilter must be a string or null.")
        if not isinstance(match_mode, str) or match_mode.lower() not in ("union", "intersection"):
            raise ValueError("matchMode must be union or intersection.")
        match_mode = match_mode.lower()
        if not isinstance(require_all_selectors_match, bool):
            raise ValueError("requireAllSelectorsMatch must be a boolean.")
        groups = (("testNames", test_names), ("fixtures", fixtures), ("assemblies", assemblies), ("categories", categories))
        if all(values is None for _, values in groups):
            return test_filter, {"requireAllSelectorsMatch": require_all_selectors_match}
        if test_filter.strip():
            raise ValueError("testFilter cannot be combined with selector arrays.")
        for _, values in groups:
            if values is not None and (
                not isinstance(values, list) or not values
                or any(not isinstance(value, str) or not value.strip() for value in values)
            ):
                raise ValueError("Selectors must be arrays of non-empty exact names.")
        if not 1 <= sum(len(values or []) for _, values in groups) <= 256:
            raise ValueError("Provide 1 to 256 selectors; empty arrays never mean all tests.")
        return test_filter, {
            **{key: value for key, value in groups if value is not None},
            "matchMode": match_mode,
            "requireAllSelectorsMatch": require_all_selectors_match,
        }

    @staticmethod
    def _expected_selection_identity(
        expected_selection_domain: str, expected_selection_snapshot_id: str,
    ) -> tuple[str, str] | None:
        values = (expected_selection_domain, expected_selection_snapshot_id)
        if any(not isinstance(value, str) for value in values):
            raise ValueError("expectedSelectionDomain and expectedSelectionSnapshotId must be strings.")
        supplied = tuple(bool(value.strip()) for value in values)
        if supplied[0] != supplied[1]:
            raise ValueError("expectedSelectionDomain and expectedSelectionSnapshotId must be supplied together.")
        return values if supplied[0] else None

    @staticmethod
    def _selection_snapshot_identity(selection: dict) -> tuple[str, str] | None:
        domain = selection.get("selectionDomain")
        snapshot_id = selection.get("selectionSnapshotId")
        if (
            not isinstance(domain, str) or not domain.strip()
            or not isinstance(snapshot_id, str) or not snapshot_id.strip()
        ):
            return None
        return domain, snapshot_id

    @staticmethod
    def _selection_stale_response(
        request_id: str, selection: dict, expected_selection: tuple[str, str] | None,
        message: str,
    ) -> ToolResponse:
        actual = TestDomainService._selection_snapshot_identity(selection)
        return fail(
            request_id,
            "TEST_SELECTION_STALE",
            message,
            {
                **selection,
                "expectedSelectionDomain": expected_selection[0] if expected_selection else "",
                "expectedSelectionSnapshotId": expected_selection[1] if expected_selection else "",
                "actualSelectionDomain": actual[0] if actual else "",
                "actualSelectionSnapshotId": actual[1] if actual else "",
                "runnerStartAttempted": False,
                "nextAction": "Call unity_test_list again and use its current selectionDomain and selectionSnapshotId before retrying.",
            },
        )

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

    def _test_result_cursor_project(self) -> str:
        return os.path.normcase(os.path.abspath(str(getattr(self.server.state, "_project_path", "") or "")))

    @staticmethod
    def _encode_test_result_cursor(project_path: str, run_guid: str, stream_version: int, sequence: int) -> str:
        payload = json.dumps({"v": 1, "projectPath": project_path, "runGuid": run_guid,
                              "resultStreamVersion": stream_version, "sequence": sequence},
                             separators=(",", ":"), sort_keys=True).encode("utf-8")
        body = base64.urlsafe_b64encode(payload).decode("ascii").rstrip("=")
        signature = hmac.new(_TEST_RESULT_CURSOR_KEY, payload, hashlib.sha256).digest()
        return body + "." + base64.urlsafe_b64encode(signature).decode("ascii").rstrip("=")

    @staticmethod
    def _decode_test_result_cursor(cursor: str) -> dict | None:
        try:
            body, encoded_signature = cursor.split(".", 1)
            padded_body = body + "=" * (-len(body) % 4)
            payload = base64.urlsafe_b64decode(padded_body.encode("ascii"))
            padded_signature = encoded_signature + "=" * (-len(encoded_signature) % 4)
            signature = base64.urlsafe_b64decode(padded_signature.encode("ascii"))
            expected = hmac.new(_TEST_RESULT_CURSOR_KEY, payload, hashlib.sha256).digest()
            if not hmac.compare_digest(signature, expected):
                return None
            value = json.loads(payload.decode("utf-8"))
        except (ValueError, UnicodeDecodeError, binascii.Error, json.JSONDecodeError):
            return None
        if not isinstance(value, dict) or value.get("v") != 1:
            return None
        if (not isinstance(value.get("projectPath"), str) or not isinstance(value.get("runGuid"), str)
                or not isinstance(value.get("resultStreamVersion"), int)
                or not isinstance(value.get("sequence"), int) or value["sequence"] < 0):
            return None
        return value

    @observed_test
    async def test_results(self, run_guid: str = "", cursor: str = "", count: int = 100) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(cursor, str):
            return fail(
                request_id,
                "TEST_RESULT_CURSOR_INVALID",
                "cursor must be a string.",
                {"sideEffectsMayHaveOccurred": False},
            )
        if not cursor:
            payload = {"runGuid": run_guid} if run_guid else {}
            response = await self.dispatcher.call(request_id, "test.results", payload)
            resolved_run_guid = str(run_guid or (response.data or {}).get("runGuid") or "")
            await self._attach_test_console_evidence(response, resolved_run_guid)
            return response
        if not run_guid:
            return fail(request_id, "TEST_RESULT_CURSOR_INVALID",
                        "Incremental test results require runGuid; the cursor never selects a run implicitly.",
                        {"sideEffectsMayHaveOccurred": False})
        if not isinstance(count, int) or isinstance(count, bool) or not 1 <= count <= 1000:
            return fail(request_id, "TEST_RESULT_CURSOR_INVALID", "count must be an integer from 1 through 1000.",
                        {"sideEffectsMayHaveOccurred": False})
        project_path = self._test_result_cursor_project()
        if not project_path:
            return fail(request_id, "TEST_RESULT_CURSOR_PROJECT_UNKNOWN",
                        "Connect the intended Unity project before using an incremental test-result cursor.",
                        {"sideEffectsMayHaveOccurred": False})
        if cursor == "begin":
            after_sequence, stream_version = 0, 0
        else:
            decoded = self._decode_test_result_cursor(cursor)
            if decoded is None or decoded["projectPath"] != project_path or decoded["runGuid"] != run_guid:
                return fail(request_id, "TEST_RESULT_CURSOR_MISMATCH",
                            "The test-result cursor belongs to another project or runGuid.",
                            {"sideEffectsMayHaveOccurred": False})
            after_sequence = decoded["sequence"]
            stream_version = decoded["resultStreamVersion"]
        response = await self.dispatcher.call(request_id, "test.results", {
            "runGuid": run_guid,
            "incremental": True,
            "afterEventSequence": after_sequence,
            "expectedResultStreamVersion": stream_version,
            "eventCount": count,
        })
        if not response.ok:
            # Results are read-only.  Preserve the Bridge's diagnostic while
            # making the no-write contract explicit for cursor callers.
            error = response.error
            if error is None:
                return fail(request_id, "TEST_RESULT_CURSOR_INVALID_RESPONSE",
                            "The Unity Bridge rejected an incremental result request without an error.",
                            {"sideEffectsMayHaveOccurred": False})
            return fail(request_id, error.code, error.message, {
                **(error.detail or {}), "sideEffectsMayHaveOccurred": False,
            })
        data = dict(response.data or {})
        if data.get("runGuid") != run_guid:
            return fail(request_id, "TEST_RESULT_CURSOR_MISMATCH",
                        "The Unity Bridge returned results for a different runGuid.",
                        {**data, "sideEffectsMayHaveOccurred": False})
        if str(data.get("phase") or "").lower() == "not_found":
            return ok(request_id, data)
        actual_version = data.get("resultStreamVersion")
        delivered = data.get("lastDeliveredEventSequence")
        events = data.get("events")
        if (
            not isinstance(actual_version, int) or isinstance(actual_version, bool) or actual_version < 1
            or not isinstance(delivered, int) or isinstance(delivered, bool) or delivered < after_sequence
            or not isinstance(events, list)
        ):
            return fail(request_id, "TEST_RESULT_CURSOR_INVALID_RESPONSE",
                        "The Unity Bridge returned an invalid incremental result cursor state.",
                        {**data, "sideEffectsMayHaveOccurred": False})
        # A cursor is bound to a persisted result-stream version.  Do not
        # silently rebind it when a stale/reloaded Bridge returns a different
        # stream: callers would otherwise have no way to tell a replacement
        # stream from a continuation of the original run.
        if stream_version and actual_version != stream_version:
            return fail(request_id, "TEST_RESULT_CURSOR_STREAM_MISMATCH",
                        "The Unity Bridge returned a different result stream version for this cursor.",
                        {**data, "sideEffectsMayHaveOccurred": False})
        sequences: list[int] = []
        for event in events:
            sequence = event.get("sequence") if isinstance(event, dict) else None
            if not isinstance(sequence, int) or isinstance(sequence, bool):
                return fail(request_id, "TEST_RESULT_CURSOR_INVALID_RESPONSE",
                            "The Unity Bridge returned an incremental event without a valid sequence.",
                            {**data, "sideEffectsMayHaveOccurred": False})
            sequences.append(sequence)
        if (
            any(sequence != after_sequence + index + 1 for index, sequence in enumerate(sequences))
            or (sequences and delivered != sequences[-1])
            or (not sequences and delivered != after_sequence)
        ):
            return fail(request_id, "TEST_RESULT_CURSOR_INVALID_RESPONSE",
                        "The Unity Bridge returned non-contiguous incremental cursor progress.",
                        {**data, "sideEffectsMayHaveOccurred": False})
        data["nextCursor"] = self._encode_test_result_cursor(project_path, run_guid, actual_version, delivered)
        response = ok(request_id, data)
        await self._attach_test_console_evidence(response, run_guid)
        return response

    async def _attach_test_console_evidence(self, response: ToolResponse, run_guid: str) -> None:
        state_store = getattr(getattr(self, "server", None), "state", None)
        if not run_guid or not hasattr(state_store, "get_console_evidence"):
            return
        evidence = state_store.get_console_evidence("runGuid", run_guid)
        data = response.data if isinstance(response.data, dict) else None
        terminal = bool(data and (
            data.get("terminal")
            or str(data.get("status") or "").lower()
            in {"completed", "failed", "aborted", "cancelled", "canceled", "no_tests"}
        ))
        if evidence and terminal and evidence.get("coverage") == "pending":
            try:
                evidence = await finish_console_evidence(self.dispatcher, self.server.state, evidence)
            except Exception as exc:
                evidence = {
                    **evidence,
                    "coverage": "partial",
                    "gapReason": f"end_boundary_failed:{type(exc).__name__}",
                }
            state_store.save_console_evidence("runGuid", run_guid, evidence)
        if data is not None and evidence:
            data["consoleEvidence"] = evidence
        elif response.error is not None and evidence:
            response.error.detail["consoleEvidence"] = evidence

    async def test_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.status", {})

    @audited("Test", "cancel", "run_guid")
    async def test_cancel(self, run_guid: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"runGuid": run_guid} if run_guid else {}
        return await self.dispatcher.call(
            request_id, "test.cancel", payload, timeout_ms=30000
        )

    @audited("Test", "cleanup", "run_guid")
    async def test_force_reset(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "test.force_reset", {}, timeout_ms=30000
        )

    @audited("Test", "cleanup", "run_guid")
    async def test_force_cleanup(self, run_guid: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload = {"runGuid": run_guid} if run_guid else {}
        return await self.dispatcher.call(
            request_id, "test.force_cleanup", payload, timeout_ms=30000
        )

    async def test_list(
        self, test_mode: str = "EditMode", test_filter: str | None = None,
        test_names: list[str] | None = None, fixtures: list[str] | None = None,
        assemblies: list[str] | None = None, categories: list[str] | None = None, match_mode: str = "union",
        require_all_selectors_match: bool = True,
    ) -> ToolResponse:
        request_id = new_id("req")
        try:
            test_filter, selectors = self._test_selector_payload(
                test_filter, test_names, fixtures, assemblies, categories, match_mode, require_all_selectors_match,
            )
        except ValueError as exc:
            return fail(request_id, "TEST_SELECTORS_INVALID", str(exc))
        return await self.dispatcher.call(
            request_id, "test.list", {"testMode": test_mode, "testFilter": test_filter, **selectors}
        )

    async def upilot_acceptance_run(
        self,
        test_mode: str = "EditMode",
        test_filter: str | None = None,
        timeout_sec: float = 900,
        stop_active_captures: bool = True,
        require_tests: bool = True,
        write_artifact: bool = True,
        test_names: list[str] | None = None,
        fixtures: list[str] | None = None,
        assemblies: list[str] | None = None,
        categories: list[str] | None = None,
        match_mode: str = "union",
        require_all_selectors_match: bool = True,
        expected_selection_domain: str = "",
        expected_selection_snapshot_id: str = "",
        preflight_only: bool = False,
    ) -> ToolResponse:
        """Return a durable task identity before starting package acceptance."""
        args = dict(test_mode=test_mode, test_filter=test_filter, timeout_sec=timeout_sec,
                    stop_active_captures=stop_active_captures, require_tests=require_tests,
                    write_artifact=write_artifact, test_names=test_names, fixtures=fixtures,
                    assemblies=assemblies, categories=categories, match_mode=match_mode,
                    require_all_selectors_match=require_all_selectors_match,
                    expected_selection_domain=expected_selection_domain,
                    expected_selection_snapshot_id=expected_selection_snapshot_id,
                    preflight_only=preflight_only)
        if preflight_only:
            return await self._execute_upilot_acceptance_run(**args)
        return await self.task_start(
            task_name="UPilot package acceptance", tool_name="unity_upilot_acceptance_run",
            tool_args=args, timeout_s=timeout_sec, retry_count=0,
        )

    async def _execute_upilot_acceptance_run(
        self,
        test_mode: str = "EditMode", test_filter: str | None = None, timeout_sec: float = 900,
        stop_active_captures: bool = True, require_tests: bool = True, write_artifact: bool = True,
        test_names: list[str] | None = None, fixtures: list[str] | None = None,
        assemblies: list[str] | None = None, categories: list[str] | None = None,
        match_mode: str = "union", require_all_selectors_match: bool = True,
        expected_selection_domain: str = "", expected_selection_snapshot_id: str = "",
        preflight_only: bool = False,
    ) -> ToolResponse:
        """Execute the original acceptance flow under its persisted Task context."""
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
            "assemblies": assemblies,
            "categories": categories,
            "matchMode": match_mode,
            "requireAllSelectorsMatch": require_all_selectors_match,
            "expectedSelectionDomain": expected_selection_domain,
            "expectedSelectionSnapshotId": expected_selection_snapshot_id,
            "expectedProject": str(expected_project),
            "stoppedConsoleCaptures": [],
            "steps": {},
            "requestId": request_id,
            "runnerStartAttempted": False,
            "writeArtifact": write_artifact,
            "requireTests": require_tests,
            "preflightOnly": preflight_only,
            "deadlineAt": started_at + int(timeout_sec * 1000),
            "taskId": context[0]["taskId"] if (context := TEST_JOB_CONTEXT.get()) else "",
            "sourceIdentity": source_identity(expected_project.parents[1]),
        }
        if context:
            report["deadlineAt"] = min(report["deadlineAt"], context[0]["deadlineAt"])

        response_summary = _response_summary

        def capture_summary(response: ToolResponse) -> dict:
            """Keep capability/report evidence from ever retaining an owner secret."""
            def redact(value):
                if isinstance(value, dict):
                    return {
                        str(key): redact(child)
                        for key, child in value.items()
                        if "token" not in str(key).lower()
                    }
                if isinstance(value, list):
                    return [redact(item) for item in value]
                return value
            return redact(response_summary(response))

        async def finish(passed: bool, code: str = "", message: str = "") -> ToolResponse:
            return self._finish_acceptance_report(report, passed, code, message)

        checkpoint("preflight", acceptanceReport=report)
        try:
            test_filter, _ = self._test_selector_payload(
                test_filter, test_names, fixtures, assemblies, categories, match_mode, require_all_selectors_match,
            )
            expected_selection = self._expected_selection_identity(
                expected_selection_domain, expected_selection_snapshot_id,
            )
        except ValueError as exc:
            return await finish(False, "TEST_SELECTORS_INVALID", str(exc))
        report["testFilter"] = test_filter

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
        if context and os.path.normcase(str(Path(context[0]["projectPath"]).resolve())) != project_key:
            return await finish(False, "UPILOT_ACCEPTANCE_PROJECT_MISMATCH", "Connected Unity project changed since the Task was queued.")
        if not preflight_only:
            from ..config import CONFIG, refresh_config_if_changed
            refresh_config_if_changed()
            if not CONFIG.write_access_approved:
                return await finish(False, "WRITE_ACCESS_NOT_APPROVED", "Project write access was revoked before acceptance execution.")
        expected_project = actual_project
        report["expectedProject"] = str(expected_project)
        report["acceptanceProject"] = accepted_projects[project_key]
        report["unityVersion"] = str((status_data.get("session") or {}).get("unityVersion") or "")
        report["importInputsBefore"] = acceptance_import_inputs(expected_project.parents[1], expected_project)
        if preflight_only:
            editor = (status_data.get("executionState") or {})
            import_before = report["importInputsBefore"]
            import_after = acceptance_import_inputs(expected_project.parents[1], expected_project)
            import_changes = diff_acceptance_import_inputs(import_before, import_after)
            last_compile_verified_at = editor.get("lastCompileVerifiedAt")
            latest_import_input_mtime = _latest_acceptance_import_input_mtime_ms(
                expected_project.parents[1], expected_project,
            )
            # Hash stability says only that the inputs did not move while this
            # observation ran; it is not import evidence.  Require an already
            # verified terminal compile snapshot as the current Bridge's
            # strongest authoritative import/readiness fact.  preflight never
            # requests a compile to manufacture that evidence.
            import_ready = (
                editor.get("ready") is True
                and editor.get("authoritative") is True
                and editor.get("isStale") is False
                and editor.get("terminal") is True
                and editor.get("errorsVerified") is True
                and str(editor.get("compilePhase") or "").lower() == "completed"
                and isinstance(last_compile_verified_at, int)
                and not isinstance(last_compile_verified_at, bool)
                and last_compile_verified_at >= latest_import_input_mtime > 0
            )
            import_state = "ready" if import_ready and not import_changes["changeCount"] else "unknown"
            blocking_reasons = [] if import_state == "ready" else [
                "ImportInputsChangedDuringPreflight" if import_changes["changeCount"] else "ImportNotVerified"
            ]

            # This path must remain observational: scene.prepareForAutomation
            # can save or discard scenes under non-block policies, while a
            # direct scene.list and capture list are read-only.  Without these
            # observations a preflight could say it was clear even though the
            # real acceptance path would be blocked before TestRunner starts.
            captures = await self.console_capture_list(count=200, include_active=True)
            report["steps"]["consoleCaptureList"] = capture_summary(captures)
            if not captures.ok:
                blocking_reasons.append("CaptureStateUnknown")
            else:
                active_captures = [
                    {
                        "sessionId": str(item.get("sessionId") or ""),
                        "ownerId": str(item.get("ownerId") or ""),
                    }
                    for item in ((captures.data or {}).get("sessions") or [])
                    if isinstance(item, dict) and item.get("active")
                ]
                if active_captures:
                    report["activeConsoleCaptures"] = active_captures
                    blocking_reasons.append("ActiveConsoleCapture")

            scenes = await self.dispatcher.call(new_id("req"), "scene.list", {})
            report["steps"]["sceneList"] = response_summary(scenes)
            if not scenes.ok:
                blocking_reasons.append("DirtySceneStateUnknown")
            else:
                dirty_scenes = [
                    item for item in ((scenes.data or {}).get("scenes") or [])
                    if isinstance(item, dict) and item.get("isDirty")
                ]
                if dirty_scenes:
                    report["dirtyScenes"] = dirty_scenes
                    blocking_reasons.append("DirtyScenes")
            report.update(
                acceptancePassed=False,
                preflightPassed=not blocking_reasons,
                importState=import_state,
                importInputsBefore=import_before,
                importInputsAfter=import_after,
                importInputChanges=import_changes,
                latestImportInputMtime=latest_import_input_mtime,
                blockingReasons=blocking_reasons,
                runnerStartAttempted=False,
                artifactWritten=False,
            )
            return await finish(False, "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY", "Preflight completed without starting compilation, capture cleanup, or TestRunner.")

        ready = await self.ensure_ready(timeout_s=min(120, max(10, timeout_sec)))
        report["steps"]["ensureReady"] = response_summary(ready)
        if not ready.ok or not bool((ready.data or {}).get("ready", False)):
            return await finish(False, "UPILOT_ACCEPTANCE_NOT_READY", "Unity Editor did not become ready for acceptance.")

        captures = await self.console_capture_list(count=200, include_active=True)
        report["steps"]["consoleCaptureList"] = capture_summary(captures)
        if not captures.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_CAPTURE_STATE_UNKNOWN", "Could not verify active Console captures.")
        active_sessions = [
            item for item in ((captures.data or {}).get("sessions") or [])
            if isinstance(item, dict) and item.get("active")
        ]
        if active_sessions:
            report["activeConsoleCaptures"] = [
                {"sessionId": str(item.get("sessionId") or ""), "ownerId": str(item.get("ownerId") or "")}
                for item in active_sessions
            ]
            # This workflow has no human-supplied exact sessionId and therefore
            # must never convert an active-capture listing into force-stop
            # authority.  A user may only recover one exact session via the
            # dedicated capture-stop tool after the persistent Advanced Settings
            # scopes authorize that separately; acceptance observes only.
            report["captureDisposition"] = {
                "stopActiveCapturesRequested": bool(stop_active_captures),
                "automaticForceStop": False,
                "nextAction": "Do not stop these captures from acceptance. Use the dedicated exact-session capture stop only with the required persistent Advanced Settings authorization.",
            }
            return await finish(
                False,
                "UPILOT_ACCEPTANCE_CAPTURE_OWNERSHIP_REQUIRED" if stop_active_captures else "UPILOT_ACCEPTANCE_ACTIVE_CAPTURE",
                "Persistent Console capture is active. Acceptance did not stop it; resolve the exact session through the authorized owner/force-stop workflow before retrying.",
            )

        checkpoint("compile")
        if now_ms() >= report["deadlineAt"]:
            return await finish(False, "UPILOT_ACCEPTANCE_DEADLINE_EXCEEDED", "Acceptance budget elapsed before compilation; no test was started.")
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
        if now_ms() >= report["deadlineAt"]:
            return await finish(False, "UPILOT_ACCEPTANCE_DEADLINE_EXCEEDED", "Acceptance budget elapsed during compilation; no test was started.")
        selection_args = {
            "test_mode": test_mode, "test_names": test_names, "fixtures": fixtures,
            "assemblies": assemblies, "categories": categories, "match_mode": match_mode,
            "require_all_selectors_match": require_all_selectors_match,
        }
        listed = await self.test_list(**selection_args, test_filter="" if self._is_short_test_class_filter(test_filter) else test_filter)
        report["steps"]["testList"] = response_summary(listed)
        tests = (listed.data or {}).get("tests") or []
        report["discoveredTestCount"] = len(tests)
        if not listed.ok:
            return await finish(False, "UPILOT_ACCEPTANCE_DISCOVERY_FAILED", "Unity Test Runner discovery failed.")
        if require_all_selectors_match and not bool((listed.data or {}).get("selectionValid", True)):
            return await finish(False, "TEST_SELECTOR_UNMATCHED", "One or more requested test selectors did not match discovery; TestRunnerApi.Execute was not called.")
        selection = listed.data or {}
        if test_names is not None or fixtures is not None or assemblies is not None or categories is not None:
            snapshot_identity = self._selection_snapshot_identity(selection)
            if snapshot_identity is None:
                stale = self._selection_stale_response(
                    request_id, selection, expected_selection,
                    "Discovery did not return a complete selection snapshot identity; TestRunnerApi.Execute was not called.",
                )
                report["steps"]["testRun"] = _response_summary(stale)
                return await finish(False, "TEST_SELECTION_STALE", stale.error.message if stale.error else "Test selection is stale.")
            if expected_selection is not None and expected_selection != snapshot_identity:
                stale = self._selection_stale_response(
                    request_id, selection, expected_selection,
                    "The supplied selection snapshot no longer matches discovery; TestRunnerApi.Execute was not called.",
                )
                report["steps"]["testRun"] = _response_summary(stale)
                return await finish(False, "TEST_SELECTION_STALE", stale.error.message if stale.error else "Test selection is stale.")
            expected_selection = snapshot_identity
        if require_tests and not tests:
            return await finish(False, "UPILOT_ACCEPTANCE_NO_TESTS", "No matching tests were discovered.")

        checkpoint("test_preflight", acceptanceReport=report)
        if now_ms() >= report["deadlineAt"]:
            return await finish(False, "UPILOT_ACCEPTANCE_DEADLINE_EXCEEDED", "Acceptance budget elapsed during discovery; no test was started.")
        report["runnerStartAttempted"] = True
        run = await self.test_run(
            **selection_args,
            test_filter=test_filter,
            expected_selection_domain=expected_selection[0] if expected_selection else "",
            expected_selection_snapshot_id=expected_selection[1] if expected_selection else "",
        )
        report["steps"]["testRun"] = response_summary(run)
        if not run.ok:
            if run.error and run.error.code == "TEST_SELECTION_STALE":
                return await finish(False, "TEST_SELECTION_STALE", run.error.message)
            return await finish(False, "UPILOT_ACCEPTANCE_TEST_START_FAILED", "Unity Test Runner could not start.")
        run_guid = str((run.data or {}).get("runGuid") or "")
        report["runGuid"] = run_guid
        if not run_guid:
            return await finish(False, "UPILOT_ACCEPTANCE_RUN_ID_MISSING", "Test start did not return a runGuid; do not retry an uncertain start.")
        checkpoint("observing", runGuid=run_guid, acceptanceReport=report)
        return await self._complete_acceptance_report(report)

    @staticmethod
    def _finish_acceptance_report(report: dict, passed: bool, code: str = "", message: str = "") -> ToolResponse:
        if passed:
            if report.get("failureCode") or report.get("failureMessage"):
                history = report.setdefault("recoveryDiagnostics", [])
                history.append({"code": str(report.get("failureCode") or "")[:120],
                                "message": str(report.get("failureMessage") or "")[:500]})
                report["recoveryDiagnostics"] = history[-8:]
            report.pop("failureCode", None)
            report.pop("failureMessage", None)
            report.pop("nextAction", None)
        report["acceptancePassed"] = bool(passed)
        report["endedAt"] = now_ms()
        report["elapsedMs"] = report["endedAt"] - report["startedAt"]
        if code:
            report["failureCode"], report["failureMessage"] = code, message
        if report.get("writeArtifact"):
            temporary_path = None
            try:
                artifact_dir = Path(report["expectedProject"]) / "Log" / "UPilotAcceptance" / (str(report["startedAt"]) + "_" + report["requestId"])
                artifact_dir.mkdir(parents=True, exist_ok=True)
                artifact_path = artifact_dir / "summary.json"
                report.pop("artifact", None)
                report["artifactWritten"] = True
                content = json.dumps(report, ensure_ascii=False, indent=2, default=str).encode("utf-8")
                temporary_path = artifact_path.with_suffix(".json.tmp")
                temporary_path.write_bytes(content)
                os.replace(temporary_path, artifact_path)
                report["artifact"] = {"path": str(artifact_path), "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
            except (OSError, ValueError, TypeError) as exc:
                report["artifactWritten"] = False
                report.pop("artifact", None)
                report["artifactError"] = str(exc)
                if not code:
                    code, message = "UPILOT_ACCEPTANCE_ARTIFACT_FAILED", "Acceptance summary could not be written."
                    report["failureCode"], report["failureMessage"] = code, message
                passed = False
                report["acceptancePassed"] = False
            finally:
                if temporary_path is not None:
                    try:
                        temporary_path.unlink(missing_ok=True)
                    except OSError:
                        pass
        else:
            report["artifactWritten"] = False
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
            if final_status.ok and data.get("phase") == "recovery_required":
                return fail(new_id("req"), "TEST_RECOVERY_REQUIRED",
                            "Runner recovery requires evidence; the original run was not restarted or cancelled.", data)
            terminal = str(data.get("status") or "").lower() in {"completed", "failed", "aborted", "no_tests"}
            if final_status.ok and terminal and data.get("cleanupPending") is not True:
                return final_status
            timed_out = now_ms() >= deadline_at
            if (timed_out or state.get("cancelRequested")) and not cleanup_deadline:
                # A recovered legacy task or an interrupted send has no
                # response boundary.  Preserve that uncertainty instead of
                # sending a second cancellation for the established run.
                if state.get("cancelSendState") == "sent_unknown":
                    return fail(new_id("req"), "TEST_RECOVERY_REQUIRED",
                                "Cancellation transport state is unknown; the original cancel was not replayed.", {
                                    "runGuid": run_guid,
                                    "cancelSendState": "sent_unknown",
                                    "sideEffectsMayHaveOccurred": True,
                                })
                cleanup_deadline = now_ms() + 60000
                # As with start, never turn a transport interruption into a
                # retry.  The cleanup deadline also prevents a recovered
                # observer from dispatching a second cancel for this runGuid.
                checkpoint(
                    "cancelling",
                    cleanupDeadlineAt=cleanup_deadline,
                    timedOut=timed_out,
                    cancelSendState="sent_unknown",
                )
                cancelled = await self.test_cancel(run_guid=run_guid)
                checkpoint(
                    "cleaning_up",
                    cancelSendState="response_received",
                    cancelResponse=_response_summary(cancelled),
                )
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
        source_root = Path(report["expectedProject"]).parents[1]
        report["sourceIdentityAfter"] = source_identity(source_root)
        report["importInputsAfter"] = acceptance_import_inputs(source_root, Path(report["expectedProject"]))
        report["importInputChanges"] = diff_acceptance_import_inputs(
            report.get("importInputsBefore") if isinstance(report.get("importInputsBefore"), dict) else {},
            report["importInputsAfter"],
        )
        # Change detection does not establish who wrote the changed files.
        for changed in report["importInputChanges"]["changed"]:
            changed["attribution"] = "unknown/concurrent-unattributed"
        report["sourceChanges"] = {
            "changed": report["sourceIdentityAfter"] != report["sourceIdentity"],
            "attribution": "unknown/concurrent-unattributed" if report["sourceIdentityAfter"] != report["sourceIdentity"] else "none",
        }
        report["importInputsUnchanged"] = report["importInputChanges"]["changeCount"] == 0
        report["sourceUnchanged"] = (
            report["sourceIdentityAfter"] == report["sourceIdentity"]
            and report["importInputsUnchanged"]
        )
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
        window_title: str = "",
        include_screenshot: bool = True,
        screenshot_degrade: str | None = None,
        instance_id: str = "",
        domain_generation: str = "",
        full_type_name: str = "",
    ) -> ToolResponse:
        """Verify one resolved EditorWindow and capture that same exact instance."""
        request_id = new_id("req")

        compile_r = await self.compile_wait(timeout_s=60, poll_interval_s=0.5)
        compile_data = (
            compile_r.data
            if compile_r.ok
            else {"error": compile_r.error.message if compile_r.error else "unknown"}
        )

        explicit_title = window_title.strip()
        explicit_type = full_type_name.strip()
        exact_id = str(instance_id).strip()
        requested_title = explicit_title or ("" if (exact_id or explicit_type) else "upilot")

        if exact_id:
            window_list_result = await self.editor_windows_list()
        elif explicit_type:
            window_list_result = await self.editor_windows_list(type_filter=explicit_type)
        else:
            window_list_result = await self.editor_windows_list(title_filter=requested_title)

        if (
            not exact_id
            and not explicit_type
            and not isinstance(window_list_result, Exception)
            and window_list_result.ok
            and not (window_list_result.data or {}).get("windows")
            and requested_title
        ):
            # Compatibility for callers which historically passed a type token in
            # windowTitle. The selected instance is still captured by exact ID.
            try:
                type_match = await self.editor_windows_list(type_filter=requested_title)
                if type_match.ok and (type_match.data or {}).get("windows"):
                    window_list_result = type_match
            except Exception:
                pass

        diag_results = await asyncio.gather(
            self.resource_window_diagnostics(),
            self.resource_console_summary(),
            return_exceptions=True,
        )

        window_match: dict = {
            "windowOpen": False,
            "requestedWindowTitle": requested_title,
            "requestedInstanceId": exact_id,
            "requestedDomainGeneration": domain_generation,
            "requestedFullTypeName": explicit_type,
            "identityScope": "domainBound" if domain_generation else "currentDomain",
            "source": "editor.windows.list",
        }
        resolved: dict | None = None
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
            windows = [item for item in data.get("windows") or [] if isinstance(item, dict)]
            if exact_id:
                windows = [item for item in windows if str(item.get("instanceId") or "") == exact_id]
            if explicit_type:
                windows = [item for item in windows if str(item.get("fullTypeName") or "") == explicit_type]
            if explicit_title and (exact_id or explicit_type):
                windows = [item for item in windows if str(item.get("title") or "") == explicit_title]

            if len(windows) == 1:
                resolved = windows[0]
                if domain_generation and str(resolved.get("domainGeneration") or "") != domain_generation:
                    window_match.update({
                        "code": "WINDOW_DOMAIN_MISMATCH",
                        "error": "The exact EditorWindow belongs to a different domainGeneration.",
                        "actualDomainGeneration": resolved.get("domainGeneration", ""),
                    })
                    resolved = None
                else:
                    first = resolved
                    window_match.update(
                        {
                            "windowOpen": True,
                            "matchedTitle": first.get("title", ""),
                            "matchedTypeName": first.get("typeName", ""),
                            "matchedFullTypeName": first.get("fullTypeName", ""),
                            "instanceId": first.get("instanceId", 0),
                            "domainGeneration": first.get("domainGeneration", ""),
                            "posX": first.get("posX", 0),
                            "posY": first.get("posY", 0),
                            "width": first.get("width", 0),
                            "height": first.get("height", 0),
                            "docked": first.get("docked", False),
                            "hasFocus": first.get("hasFocus", False),
                            "hasUIToolkit": first.get("hasUIToolkit", False),
                            "multipleMatches": False,
                            "matchCount": 1,
                        }
                    )
            elif len(windows) > 1:
                window_match.update({
                    "code": "WINDOW_AMBIGUOUS",
                    "error": "Multiple Unity EditorWindow instances matched; use instanceId.",
                    "matchCount": len(windows),
                    "multipleMatches": True,
                })
            else:
                mismatch_code = "WINDOW_NOT_FOUND"
                if exact_id and explicit_title:
                    mismatch_code = "EDITORWINDOW_TITLE_MISMATCH"
                elif exact_id and explicit_type:
                    mismatch_code = "EDITORWINDOW_TYPE_MISMATCH"
                window_match.update({"matchCount": 0, "code": mismatch_code})

        screenshot_data = None
        if include_screenshot and resolved is not None:
            try:
                deg = screenshot_degrade or getenv(
                    "UPILOT_VERIFY_SCREENSHOT_DEGRADE"
                )
                ss_r = await self.screenshot_editor_window(
                    window_title=str(resolved.get("title") or ""),
                    degrade=deg,
                    instance_id=str(resolved.get("instanceId") or ""),
                    domain_generation=str(resolved.get("domainGeneration") or ""),
                    full_type_name=str(resolved.get("fullTypeName") or ""),
                )
                if ss_r.ok:
                    screenshot_data = ss_r.data
                else:
                    screenshot_data = {
                        "error": ss_r.error.message if ss_r.error else "unknown",
                        "code": ss_r.error.code if ss_r.error else "",
                    }
            except Exception as e:
                screenshot_data = {"error": str(e)}
        elif include_screenshot:
            screenshot_data = {
                "code": window_match.get("code", "WINDOW_NOT_FOUND"),
                "error": "Screenshot was not dispatched because exact window selection failed.",
                "sideEffectsMayHaveOccurred": False,
            }

        combined: dict = {"compileWait": compile_data, "windowMatch": window_match}
        labels = ["windowDiagnostics", "consoleSummary"]
        for label, r in zip(labels, diag_results):
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
