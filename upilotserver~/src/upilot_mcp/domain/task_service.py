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


_UPILOT_RULES_VERSION = 2
_UPILOT_BLOCK_START = "<!-- upilot:start -->"
_UPILOT_BLOCK_END = "<!-- upilot:end -->"
_DEFAULT_OPERATION_SUCCESS = {"succeeded", "success", "complete", "completed", "passed", "ok"}
_DEFAULT_OPERATION_FAILURE = {"failed", "failure", "canceled", "cancelled", "timeout", "timedout", "error"}
_TERMINAL_STATUSES = _DEFAULT_OPERATION_SUCCESS | _DEFAULT_OPERATION_FAILURE


def _utc_iso() -> str:
    return datetime.utcnow().replace(microsecond=0).isoformat() + "Z"


def _coerce_json_dict(value: object) -> dict:
    if isinstance(value, dict):
        return value
    if isinstance(value, str):
        text = value.strip()
        if text and text != "(null)":
            try:
                parsed = json.loads(text)
                if isinstance(parsed, dict):
                    return parsed
            except json.JSONDecodeError:
                return {}
    return {}


def _extract_operation_payload(result: ToolResponse) -> dict:
    """Return a normalized dict from direct tool data or reflection result text."""
    if not result.ok or not isinstance(result.data, dict):
        return {}

    data = result.data
    for key in ("result", "raw", "data", "payload"):
        parsed = _coerce_json_dict(data.get(key))
        if parsed:
            return parsed

    return data


def _normalize_status(value: object) -> str:
    text = str(value or "").strip()
    return text or "Running"


def _is_terminal_status(status: str, mapping: dict | None) -> bool:
    key = status.strip().lower()
    success = {str(item).lower() for item in (mapping or {}).get("success", [])}
    failure = {str(item).lower() for item in (mapping or {}).get("failure", [])}
    return key in (_DEFAULT_OPERATION_SUCCESS | _DEFAULT_OPERATION_FAILURE | success | failure)


def _is_success_status(status: str, mapping: dict | None) -> bool:
    key = status.strip().lower()
    success = {str(item).lower() for item in (mapping or {}).get("success", [])}
    return key in (_DEFAULT_OPERATION_SUCCESS | success)


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _read_text_tail(path: Path, lines: int) -> str:
    if lines <= 0:
        return ""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    return "\n".join(text.splitlines()[-lines:])


class TaskDomainService:
    async def operation_list(self, status: str = "", limit: int = 50) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "operation.list",
            {"status": status, "limit": max(1, min(limit, 200))},
        )

    async def operation_get(self, command_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "operation.get", {"commandId": command_id})

    def _project_root(self) -> Path:
        session_manager = getattr(getattr(self, "server", None), "session_manager", None)
        session = getattr(session_manager, "active", None)
        if session and getattr(session, "project_path", ""):
            return Path(session.project_path).resolve()
        return Path.cwd().resolve()

    @staticmethod
    def _upilot_package_version() -> str:
        package_json = Path(__file__).resolve().parents[4] / "package.json"
        try:
            data = json.loads(package_json.read_text(encoding="utf-8"))
            return str(data.get("version") or "unknown")
        except (OSError, json.JSONDecodeError):
            return "unknown"

    def _build_agent_rules_block(self, project_root: Path) -> str:
        project_path = str(project_root)
        version = self._upilot_package_version()
        return f"""<!-- upilot:start -->
# UPilot Unity MCP

rulesVersion: {_UPILOT_RULES_VERSION}
upilotPackageVersion: {version}
projectPath: {project_path}
generatedAt: {_utc_iso()}

This Unity project has the `io.github.codingriver.upilot` UPM package installed.
Project-specific business rules outside this block take precedence over these generic UPilot rules.

## Connection

- Streamable HTTP: `http://127.0.0.1:8011/mcp`
- Health check: `http://127.0.0.1:8011/health`
- Never configure an MCP client with the internal Unity Bridge WebSocket port.
1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches `{project_path}` (allow equivalent slash normalization).
4. Stop and report the mismatch if another Unity project is connected.

## Capabilities

- Distinguish server registration, client tool-list injection, and a successful real call; they are different states.
- If a native tool is not visible in the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable.
- Use the narrowest dedicated semantic tool. Use `unity_reflection_call` for existing compiled entry points.
- Only after `unity_reflection_call` actually fails may you fall back to one bounded `reflection_eval` expression.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Compile

- Call `unity_ensure_ready` before Editor mutations and inspect the exact target before destructive changes.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- Compile only after C# or assembly-related changes.
- Use `unity_compile_wait` and `unity_compile_errors`; `unity_compile_errors_get` is only a compatibility alias.
- After compilation, read structured compile errors and relevant Console errors before editing again.

## Project Workflows

- When a project exposes an authoritative compiled orchestration entry point for a test, build, or workflow, call that entry point.
- Keep business orchestration in project code. MCP should start, poll, diagnose, capture logs, and collect artifacts.
- For long jobs, prefer `unity_operation_start`, `unity_operation_status`, `unity_operation_wait`, `unity_operation_cancel`, and `unity_operation_collect_artifacts`.
- Starting a job is not success; poll until a terminal result.
- During polling, report only phase, status, error, failureSignature, suspected-stuck, or important artifact changes.
- Do not reconstruct project workflows with shell commands, temporary scripts, menu calls, or UI automation when a bridge entry point exists.

## Persistent Console Capture

- For long-running or audit-sensitive operations, enable operation Console capture or call `unity_console_capture_start` before the operation.
- Use incremental reads with `nextSequence`; avoid repeatedly reading full Console output.
- Always stop capture on success, failure, timeout, or cancel.
- Keep raw Console capture separate from domain-specific reports.

## Artifacts And Screenshots

- Prefer dedicated project-relative artifact paths returned by the project bridge.
- Prefer `unity_screenshot_save` for screenshots and report `path`, `bytes`, `width`, `height`, and `sha256`.
- UPilot records artifact metadata and hashes; business code decides whether an artifact proves the workflow passed.

## Retry And Failure Protection

- Retry automatically only when the operation is idempotent and non-destructive.
- If the same `failureSignature` repeats, stop blind reruns and fix project logic, test configuration, or acceptance criteria first.
- On timeout, inspect operation timing, phase elapsed time, last progress, Console capture, and artifact summary before choosing one bounded retry.
<!-- upilot:end -->"""

    def _parse_rules_metadata(self, block: str) -> dict[str, str]:
        metadata: dict[str, str] = {}
        for line in block.splitlines():
            if ":" not in line:
                continue
            key, value = line.split(":", 1)
            key = key.strip()
            if key in {"rulesVersion", "upilotPackageVersion", "projectPath", "generatedAt"}:
                metadata[key] = value.strip()
        return metadata

    async def agent_rules_check(self) -> ToolResponse:
        request_id = new_id("req")
        project_root = self._project_root()
        rules_path = project_root / "AGENTS.md"
        recommended_block = self._build_agent_rules_block(project_root)
        diff_summary: list[str] = []
        current_block = ""
        text = ""
        has_block = False
        marker_error = ""

        if rules_path.exists():
            text = rules_path.read_text(encoding="utf-8", errors="replace")
            start_count = text.count(_UPILOT_BLOCK_START)
            end_count = text.count(_UPILOT_BLOCK_END)
            if start_count == 1 and end_count == 1:
                start = text.index(_UPILOT_BLOCK_START)
                end = text.index(_UPILOT_BLOCK_END) + len(_UPILOT_BLOCK_END)
                if start < end:
                    has_block = True
                    current_block = text[start:end]
            elif start_count or end_count:
                marker_error = "Expected exactly one upilot:start and one upilot:end marker."
        else:
            diff_summary.append("AGENTS.md missing; install will create it")

        current_meta = self._parse_rules_metadata(current_block)
        recommended_meta = self._parse_rules_metadata(recommended_block)
        if marker_error:
            diff_summary.append(marker_error)
        if not has_block:
            diff_summary.append("upilot block missing")
        else:
            for key in ("rulesVersion", "upilotPackageVersion", "projectPath"):
                if current_meta.get(key) != recommended_meta.get(key):
                    diff_summary.append(f"{key} differs")
            if "unity_operation_start" not in current_block:
                diff_summary.append("operation runner rules missing")
            if "unity_compile_errors" not in current_block:
                diff_summary.append("compile error tool guidance missing")
            if current_block.strip() != recommended_block.strip():
                diff_summary.append("upilot controlled block differs from recommended template")

        needs_import = not has_block
        needs_update = bool(diff_summary) and not marker_error
        return ok(
            request_id,
            {
                "action": "CheckAgentRules",
                "rulesPath": str(rules_path),
                "hasUpilotBlock": has_block,
                "needsImport": needs_import,
                "needsUpdate": needs_update,
                "currentRulesVersion": current_meta.get("rulesVersion", ""),
                "recommendedRulesVersion": recommended_meta.get("rulesVersion", str(_UPILOT_RULES_VERSION)),
                "currentUpilotPackageVersion": current_meta.get("upilotPackageVersion", ""),
                "recommendedUpilotPackageVersion": recommended_meta.get("upilotPackageVersion", self._upilot_package_version()),
                "projectPathMatches": current_meta.get("projectPath", str(project_root)) == str(project_root),
                "diffSummary": diff_summary,
                "recommendedBlock": recommended_block,
                "markerError": marker_error,
            },
        )

    async def agent_rules_install(self, apply: bool = False) -> ToolResponse:
        request_id = new_id("req")
        check = await self.agent_rules_check()
        if not check.ok or not check.data:
            return check
        data = dict(check.data)
        data["dryRun"] = not apply
        data["applied"] = False
        if not apply:
            return ok(request_id, data)
        if data.get("markerError"):
            return fail(request_id, "AGENT_RULES_MARKER_ERROR", str(data["markerError"]), data)

        rules_path = Path(str(data["rulesPath"]))
        recommended_block = str(data["recommendedBlock"])
        if rules_path.exists():
            text = rules_path.read_text(encoding="utf-8", errors="replace")
            if _UPILOT_BLOCK_START in text and _UPILOT_BLOCK_END in text:
                start = text.index(_UPILOT_BLOCK_START)
                end = text.index(_UPILOT_BLOCK_END) + len(_UPILOT_BLOCK_END)
                text = text[:start] + recommended_block + text[end:]
            else:
                text = text.rstrip() + "\n\n" + recommended_block + "\n"
        else:
            rules_path.parent.mkdir(parents=True, exist_ok=True)
            text = recommended_block + "\n"

        rules_path.write_text(text, encoding="utf-8", newline="\n")
        data["applied"] = True
        data["fileSha256"] = _sha256_file(rules_path)
        data["installedRulesVersion"] = str(_UPILOT_RULES_VERSION)
        data["installedUpilotPackageVersion"] = self._upilot_package_version()
        return ok(request_id, data)

    async def operation_start(self, job_spec: dict | None) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(job_spec, dict):
            return fail(request_id, "INVALID_JOB_SPEC", "jobSpec must be an object.", {"jobSpec": job_spec})
        start_call = job_spec.get("startCall")
        status_call = job_spec.get("statusCall")
        if not isinstance(start_call, dict) or not isinstance(status_call, dict):
            return fail(request_id, "INVALID_JOB_SPEC", "startCall and statusCall are required.", {"jobSpec": job_spec})

        operation_id = new_id("op")
        now = now_ms()
        timeout_sec = float(job_spec.get("timeoutSec") or 300)
        state = {
            "operationId": operation_id,
            "displayName": str(job_spec.get("displayName") or operation_id),
            "jobSpec": job_spec,
            "status": "Starting",
            "phase": "Starting",
            "error": "",
            "detail": "",
            "progress": 0,
            "failureSignature": "",
            "repeatFailure": False,
            "startedAt": now,
            "updatedAt": now,
            "endedAt": 0,
            "timeoutSec": timeout_sec,
            "pollIntervalSec": float(job_spec.get("pollIntervalSec") or 3),
            "lastStatusData": {},
            "lastStatusAt": 0,
            "changes": [],
            "artifacts": {},
            "consoleCapture": {},
            "timing": {
                "totalWallMs": 0,
                "mcpQueueMs": 0,
                "bridgeMs": 0,
                "unityMainThreadMs": 0,
                "projectElapsedMs": 0,
                "agentPollGapMs": 0,
                "artifactReadMs": 0,
            },
            "_startedMono": time.monotonic(),
            "_lastPollMono": 0.0,
        }
        self._operations[operation_id] = state

        capture = job_spec.get("consoleCapture") if isinstance(job_spec.get("consoleCapture"), dict) else {}
        if capture.get("enabled"):
            capture_result = await self.console_capture_start(
                title=str(capture.get("title") or state["displayName"]),
                path=str(capture.get("path") or ""),
                include_stack_trace=bool(capture.get("includeStackTrace", True)),
                exclude_upilot=bool(capture.get("excludeUPilot", True)),
                clear_unity_console=bool(capture.get("clearUnityConsole", False)),
            )
            state["consoleCapture"]["start"] = self._tool_response_summary(capture_result)
            if capture_result.ok and isinstance(capture_result.data, dict):
                state["consoleCapture"]["sessionId"] = str(capture_result.data.get("sessionId") or "")
                state["consoleCapture"]["nextSequence"] = int(capture_result.data.get("nextSequence") or -1)

        start_result = await self._operation_invoke(start_call)
        self._accumulate_timing(state, start_result)
        state["startResult"] = self._tool_response_summary(start_result)
        if not start_result.ok:
            state["status"] = "Failed"
            state["phase"] = "StartFailed"
            state["error"] = start_result.error.message if start_result.error else "startCall failed"
            state["failureSignature"] = start_result.error.code if start_result.error else "StartCallFailed"
            state["endedAt"] = now_ms()
            self._finalize_operation_timing(state)
            await self._operation_stop_console_capture(state)
            return fail(request_id, "OPERATION_START_FAILED", state["error"], self._public_operation_state(state))

        payload = _extract_operation_payload(start_result)
        if payload:
            self._merge_operation_status(state, payload)
        mapping = job_spec.get("terminalStatusMapping")
        if _is_terminal_status(str(state["status"]), mapping):
            if _is_success_status(str(state["status"]), mapping):
                state["status"] = "Succeeded"
            elif str(state["status"]).strip().lower() in {"canceled", "cancelled"}:
                state["status"] = "Canceled"
            elif str(state["status"]).strip().lower() in {"timeout", "timedout"}:
                state["status"] = "Timeout"
            else:
                state["status"] = "Failed"
            state["endedAt"] = now_ms()
            self._mark_repeat_failure(state)
            await self._operation_stop_console_capture(state)
            await self.operation_collect_artifacts(operation_id)
        else:
            state["status"] = "Running"
            state["phase"] = state.get("phase") or "Running"
        state["updatedAt"] = now_ms()
        self._finalize_operation_timing(state)
        return ok(request_id, self._public_operation_state(state))

    async def operation_status(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
        if state.get("endedAt"):
            self._finalize_operation_timing(state)
            return ok(request_id, self._public_operation_state(state))

        status_call = state["jobSpec"].get("statusCall")
        result = await self._operation_invoke(status_call)
        self._accumulate_timing(state, result)
        state["lastStatusAt"] = now_ms()
        if not result.ok:
            state["status"] = "Failed"
            state["phase"] = "StatusCallFailed"
            state["error"] = result.error.message if result.error else "statusCall failed"
            state["failureSignature"] = result.error.code if result.error else "StatusCallFailed"
            state["endedAt"] = now_ms()
            await self._operation_stop_console_capture(state)
            await self.operation_collect_artifacts(operation_id)
            self._finalize_operation_timing(state)
            return fail(request_id, "OPERATION_STATUS_FAILED", state["error"], self._public_operation_state(state))

        payload = _extract_operation_payload(result)
        if payload:
            self._merge_operation_status(state, payload)
        mapping = state["jobSpec"].get("terminalStatusMapping")
        if _is_terminal_status(str(state["status"]), mapping):
            if _is_success_status(str(state["status"]), mapping):
                state["status"] = "Succeeded"
            elif str(state["status"]).strip().lower() in {"canceled", "cancelled"}:
                state["status"] = "Canceled"
            elif str(state["status"]).strip().lower() in {"timeout", "timedout"}:
                state["status"] = "Timeout"
            else:
                state["status"] = "Failed"
            state["endedAt"] = now_ms()
            self._mark_repeat_failure(state)
            await self._operation_stop_console_capture(state)
            await self.operation_collect_artifacts(operation_id)
        state["updatedAt"] = now_ms()
        self._finalize_operation_timing(state)
        return ok(request_id, self._public_operation_state(state))

    async def operation_wait(
        self,
        operation_id: str,
        timeout_s: float | None = None,
        poll_interval_s: float | None = None,
        return_on_suspected_stuck: bool = True,
        timeout_sec: float | None = None,
        poll_interval_sec: float | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})

        if timeout_s is None and timeout_sec is not None:
            timeout_s = timeout_sec
        if poll_interval_s is None and poll_interval_sec is not None:
            poll_interval_s = poll_interval_sec

        timeout = float(timeout_s if timeout_s is not None and timeout_s > 0 else state.get("timeoutSec", 300))
        interval = float(poll_interval_s if poll_interval_s is not None and poll_interval_s > 0 else state.get("pollIntervalSec", 3))
        suspected_stuck_sec = float(state["jobSpec"].get("suspectedStuckSec") or 0)
        deadline = time.monotonic() + timeout
        changes: list[dict] = []
        last_key = self._operation_change_key(state)

        while True:
            status_result = await self.operation_status(operation_id)
            state = self._operations.get(operation_id, state)
            current_key = self._operation_change_key(state)
            if current_key != last_key:
                event = {
                    "at": now_ms(),
                    "status": state.get("status", ""),
                    "phase": state.get("phase", ""),
                    "error": state.get("error", ""),
                    "failureSignature": state.get("failureSignature", ""),
                    "artifacts": sorted((state.get("artifacts") or {}).keys()),
                }
                changes.append(event)
                state["changes"].append(event)
                last_key = current_key

            await self._operation_read_console_capture(state)
            if state.get("endedAt"):
                await self.operation_collect_artifacts(operation_id)
                self._finalize_operation_timing(state)
                payload = self._public_operation_state(state)
                payload["changes"] = changes
                return ok(request_id, payload) if status_result.ok else status_result

            if suspected_stuck_sec > 0:
                phase_elapsed = float((state.get("lastStatusData") or {}).get("phaseElapsedSec") or 0)
                if phase_elapsed >= suspected_stuck_sec and return_on_suspected_stuck:
                    state["suspectedStuck"] = True
                    self._finalize_operation_timing(state)
                    payload = self._public_operation_state(state)
                    payload["changes"] = changes
                    payload["recommendation"] = "Inspect operation status, Console capture, and artifacts before retrying."
                    return ok(request_id, payload)

            if time.monotonic() >= deadline:
                state["status"] = "Timeout"
                state["phase"] = state.get("phase") or "Timeout"
                state["error"] = f"Operation timed out after {timeout:.0f}s"
                state["failureSignature"] = state.get("failureSignature") or "OperationTimeout"
                state["endedAt"] = now_ms()
                self._mark_repeat_failure(state)
                await self._operation_stop_console_capture(state)
                await self.operation_collect_artifacts(operation_id)
                self._finalize_operation_timing(state)
                payload = self._public_operation_state(state)
                payload["changes"] = changes
                payload["recommendation"] = "Inspect phaseElapsedSec, timing, Console capture, and artifact summary before retrying."
                return fail(request_id, "OPERATION_TIMEOUT", state["error"], payload)

            last_poll = float(state.get("_lastPollMono") or 0)
            if last_poll:
                gap = max(0, int((time.monotonic() - last_poll - interval) * 1000))
                state["timing"]["agentPollGapMs"] += gap
            state["_lastPollMono"] = time.monotonic()
            await asyncio.sleep(min(interval, max(0.1, deadline - time.monotonic())))

    async def operation_cancel(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
        cancel_call = state["jobSpec"].get("cancelCall")
        if not isinstance(cancel_call, dict):
            return fail(request_id, "CANCEL_UNSUPPORTED", "This operation has no cancelCall.", self._public_operation_state(state))
        result = await self._operation_invoke(cancel_call)
        self._accumulate_timing(state, result)
        state["cancelResult"] = self._tool_response_summary(result)
        state["status"] = "Canceled" if result.ok else "Failed"
        state["phase"] = "Canceled" if result.ok else "CancelFailed"
        state["error"] = "" if result.ok else (result.error.message if result.error else "cancelCall failed")
        state["endedAt"] = now_ms()
        await self._operation_stop_console_capture(state)
        await self.operation_collect_artifacts(operation_id)
        self._finalize_operation_timing(state)
        return ok(request_id, self._public_operation_state(state)) if result.ok else fail(request_id, "OPERATION_CANCEL_FAILED", state["error"], self._public_operation_state(state))

    async def operation_collect_artifacts(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})

        started = time.monotonic()
        artifacts: dict[str, object] = {}
        errors: list[dict] = []
        source_artifacts = {}
        status_data = state.get("lastStatusData") or {}
        if isinstance(status_data.get("artifacts"), dict):
            source_artifacts.update(status_data["artifacts"])
        rules = state["jobSpec"].get("artifactRules") if isinstance(state["jobSpec"].get("artifactRules"), dict) else {}
        for field in rules.get("fromStatusFields") or []:
            if field in status_data:
                source_artifacts.setdefault(str(field), status_data.get(field))
        tail_lines = int(rules.get("readReportTailLines") or 0)

        for name, raw_value in source_artifacts.items():
            path_text = ""
            if isinstance(raw_value, str):
                path_text = raw_value
            elif isinstance(raw_value, dict):
                path_text = str(raw_value.get("path") or raw_value.get("file") or "")
            if not path_text:
                continue
            path = Path(path_text)
            if not path.is_absolute():
                path = self._project_root() / path
            item = {"path": str(path), "exists": path.exists()}
            if path.exists() and path.is_file():
                try:
                    stat = path.stat()
                    item.update({
                        "bytes": stat.st_size,
                        "sha256": _sha256_file(path),
                        "modifiedAt": int(stat.st_mtime * 1000),
                    })
                    if tail_lines and path.suffix.lower() in {".txt", ".log", ".md", ".json", ".csv"}:
                        item["tail"] = _read_text_tail(path, tail_lines)
                except OSError as ex:
                    item["error"] = str(ex)
                    errors.append({"artifact": name, "path": str(path), "error": str(ex)})
            elif not path.exists():
                errors.append({"artifact": name, "path": str(path), "error": "missing"})
            artifacts[str(name)] = item

        state["artifacts"] = artifacts
        state["artifactErrors"] = errors
        state["timing"]["artifactReadMs"] += int((time.monotonic() - started) * 1000)
        self._finalize_operation_timing(state)
        return ok(request_id, {"operationId": operation_id, "artifacts": artifacts, "artifactErrors": errors})

    async def _operation_invoke(self, call: dict | None) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(call, dict):
            return fail(request_id, "INVALID_OPERATION_CALL", "Operation call must be an object.", {"call": call})
        kind = str(call.get("kind") or call.get("type") or "").strip().lower()
        timeout_ms = int(float(call.get("timeoutSec") or 0) * 1000) if call.get("timeoutSec") else None
        if kind == "reflection":
            return await self.reflection_call(
                type_name=str(call.get("typeName") or call.get("type_name") or ""),
                method_name=str(call.get("methodName") or call.get("method_name") or ""),
                parameters=call.get("parameters") if isinstance(call.get("parameters"), list) else [],
                is_static=bool(call.get("isStatic", call.get("is_static", True))),
                target_instance_path=str(call.get("targetInstancePath") or call.get("target_instance_path") or ""),
                target_static_type_name=str(call.get("targetStaticTypeName") or call.get("target_static_type_name") or ""),
                target_static_member_path=str(call.get("targetStaticMemberPath") or call.get("target_static_member_path") or ""),
            )
        if kind in {"mcp", "tool", "mcp_tool"}:
            tool_name = str(call.get("toolName") or call.get("name") or "")
            tool_args = call.get("toolArgs") or call.get("args") or call.get("arguments") or {}
            if not isinstance(tool_args, dict):
                return fail(request_id, "INVALID_OPERATION_CALL", "toolArgs must be an object.", {"call": call})
            return await self._dispatch_tool(tool_name, tool_args)
        if kind == "menu":
            return await self.editor_execute_command(command_name=str(call.get("commandName") or call.get("menuPath") or ""))
        if kind in {"native", "route", "bridge"}:
            route = str(call.get("route") or call.get("command") or "")
            payload = call.get("payload") or {}
            if not isinstance(payload, dict):
                return fail(request_id, "INVALID_OPERATION_CALL", "native payload must be an object.", {"call": call})
            return await self.dispatcher.call(request_id, route, payload, timeout_ms=timeout_ms)
        return fail(request_id, "UNSUPPORTED_OPERATION_CALL", f"Unsupported operation call kind: {kind}", {"call": call})

    @staticmethod
    def _tool_response_summary(result: ToolResponse) -> dict:
        data = result.data if isinstance(result.data, dict) else {}
        return {
            "ok": result.ok,
            "requestId": result.request_id,
            "error": {
                "code": result.error.code,
                "message": result.error.message,
                "detail": result.error.detail,
            } if result.error else None,
            "data": data,
            "timing": result.timing or {},
        }

    @staticmethod
    def _accumulate_timing(state: dict, result: ToolResponse) -> None:
        timing = result.timing or {}
        target = state.setdefault("timing", {})
        target["mcpQueueMs"] = int(target.get("mcpQueueMs", 0)) + int(timing.get("queueMs") or 0)
        target["bridgeMs"] = int(target.get("bridgeMs", 0)) + int(timing.get("bridgeMs") or 0)
        target["unityMainThreadMs"] = int(target.get("unityMainThreadMs", 0)) + int(timing.get("unityExecutionMs") or 0)

    def _merge_operation_status(self, state: dict, payload: dict) -> None:
        state["lastStatusData"] = payload
        if payload.get("ok") is False and not payload.get("status"):
            state["status"] = "Failed"
        elif "status" in payload:
            state["status"] = _normalize_status(payload.get("status"))
        if "phase" in payload:
            state["phase"] = str(payload.get("phase") or "")
        if "error" in payload:
            state["error"] = str(payload.get("error") or "")
        if "detail" in payload:
            state["detail"] = str(payload.get("detail") or "")
        if "progress" in payload:
            try:
                state["progress"] = max(0, min(100, int(float(payload.get("progress") or 0) * (100 if float(payload.get("progress") or 0) <= 1 else 1))))
            except (TypeError, ValueError):
                state["progress"] = 0
        if payload.get("failureSignature"):
            state["failureSignature"] = str(payload.get("failureSignature"))
        metrics = payload.get("metrics") if isinstance(payload.get("metrics"), dict) else {}
        project_elapsed = metrics.get("projectElapsedSec", payload.get("elapsedSec"))
        try:
            if project_elapsed is not None:
                state["timing"]["projectElapsedMs"] = int(float(project_elapsed) * 1000)
        except (TypeError, ValueError):
            pass
        if isinstance(payload.get("artifacts"), dict):
            state["artifactHints"] = payload["artifacts"]

    def _mark_repeat_failure(self, state: dict) -> None:
        if str(state.get("status", "")).lower() not in _DEFAULT_OPERATION_FAILURE:
            self._operation_failure_history.clear()
            setattr(self, "_operation_last_failure_signature", "")
            return
        signature = str(state.get("failureSignature") or "")
        if not signature:
            return
        last = str(getattr(self, "_operation_last_failure_signature", ""))
        state["repeatFailure"] = last == signature
        setattr(self, "_operation_last_failure_signature", signature)
        self._operation_failure_history[signature] = self._operation_failure_history.get(signature, 0) + 1
        if state["repeatFailure"]:
            state["recommendation"] = "Stop retrying; fix project logic, test configuration, or acceptance criteria before rerun."

    @staticmethod
    def _operation_change_key(state: dict) -> tuple:
        artifacts = state.get("artifacts") or state.get("artifactHints") or {}
        return (
            state.get("status", ""),
            state.get("phase", ""),
            state.get("error", ""),
            state.get("failureSignature", ""),
            tuple(sorted(artifacts.keys())) if isinstance(artifacts, dict) else (),
        )

    async def _operation_read_console_capture(self, state: dict) -> None:
        capture = state.get("consoleCapture") or {}
        session_id = capture.get("sessionId")
        if not session_id:
            return
        after_sequence = int(capture.get("nextSequence", -1))
        result = await self.console_capture_read(
            session_id=session_id,
            after_sequence=after_sequence,
            count=500,
            include_stack_trace=False,
        )
        capture["lastRead"] = self._tool_response_summary(result)
        if result.ok and isinstance(result.data, dict):
            if "nextSequence" in result.data:
                capture["nextSequence"] = int(result.data.get("nextSequence") or after_sequence)
            capture["lastReadCount"] = len(result.data.get("entries") or result.data.get("logs") or [])

    async def _operation_stop_console_capture(self, state: dict) -> None:
        capture = state.get("consoleCapture") or {}
        session_id = capture.get("sessionId")
        if not session_id or capture.get("stopped"):
            return
        result = await self.console_capture_stop(session_id=session_id)
        capture["stop"] = self._tool_response_summary(result)
        capture["stopped"] = True

    @staticmethod
    def _finalize_operation_timing(state: dict) -> None:
        started = float(state.get("_startedMono") or time.monotonic())
        state["timing"]["totalWallMs"] = int(max(0, time.monotonic() - started) * 1000)

    def _public_operation_state(self, state: dict) -> dict:
        public = {
            "operationId": state.get("operationId"),
            "displayName": state.get("displayName"),
            "status": state.get("status"),
            "phase": state.get("phase"),
            "error": state.get("error"),
            "detail": state.get("detail"),
            "progress": state.get("progress"),
            "failureSignature": state.get("failureSignature"),
            "repeatFailure": state.get("repeatFailure", False),
            "startedAt": state.get("startedAt"),
            "updatedAt": state.get("updatedAt"),
            "endedAt": state.get("endedAt"),
            "elapsedMs": max(0, (state.get("endedAt") or now_ms()) - state.get("startedAt", now_ms())),
            "terminal": bool(state.get("endedAt")),
            "timeoutSec": state.get("timeoutSec"),
            "pollIntervalSec": state.get("pollIntervalSec"),
            "lastStatusData": state.get("lastStatusData") or {},
            "artifacts": state.get("artifacts") or {},
            "artifactErrors": state.get("artifactErrors") or [],
            "consoleCapture": state.get("consoleCapture") or {},
            "timing": state.get("timing") or {},
        }
        status_data = state.get("lastStatusData") or {}
        if isinstance(status_data.get("metrics"), dict):
            public["metrics"] = status_data["metrics"]
        if isinstance(status_data.get("domain"), dict):
            public["domain"] = status_data["domain"]
        if state.get("suspectedStuck"):
            public["suspectedStuck"] = True
        if state.get("recommendation"):
            public["recommendation"] = state["recommendation"]
        return public

    async def ensure_ready(self, timeout_s: float = 300) -> ToolResponse:
        """Pre-test environment check: connection + compile wait + edit mode."""
        import time

        request_id = new_id("req")
        checks: dict = {}

        # 1. Wait for connection
        deadline = time.monotonic() + timeout_s
        connected = False
        for _ in range(int(timeout_s / 0.5)):
            if self.server.session_manager.is_connected():
                connected = True
                break
            await asyncio.sleep(0.5)
            if time.monotonic() >= deadline:
                break
        checks["connected"] = connected
        if not connected:
            checks["ready"] = False
            checks["failReason"] = "Unity not connected within timeout"
            return ok(request_id, checks)

        # 2. Wait for compilation to finish
        remaining = max(1, deadline - time.monotonic())
        compile_r = await self.compile_wait(timeout_s=remaining, poll_interval_s=0.5)
        if compile_r.ok and compile_r.data:
            checks["compileStatus"] = compile_r.data.get("status", "unknown")
        else:
            checks["compileStatus"] = "error"

        # 3. Check editor state
        state_r = await self.dispatcher.call(new_id("req"), "resource.editorState", {})
        if state_r.ok and state_r.data:
            checks["isCompiling"] = state_r.data.get("isCompiling", False)
            checks["playModeState"] = state_r.data.get("playModeState", "unknown")
            in_edit = state_r.data.get("playModeState", "") in ("edit", "Edit", "")
            checks["inEditMode"] = in_edit
        else:
            checks["inEditMode"] = False

        checks["ready"] = (
            checks["connected"]
            and checks.get("compileStatus") == "ready"
            and checks.get("inEditMode", False)
        )
        return ok(request_id, checks)

    @staticmethod
    def _task_execute_tool_succeeded(tool_name: str, result: ToolResponse) -> bool:
        """True when the tool transport succeeded *and* the tool-specific outcome is success."""
        data = result.data
        if tool_name == "wait_condition" and isinstance(data, dict):
            return bool(data.get("met"))
        return True

    @staticmethod
    def _task_execute_logical_error(tool_name: str, result: ToolResponse) -> str:
        data = result.data if isinstance(result.data, dict) else {}
        if tool_name == "wait_condition":
            return str(data.get("lastError") or "wait_condition not met (met=false)")
        return "logical failure"

    async def task_execute(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        max_total_s: float = 1200,
        retry_count: int = 1,
        restart_unity_on_timeout: bool = True,
    ) -> ToolResponse:
        """Execute an MCP tool call with timeout/watchdog.

        Workflow (per user spec):
        1. Run tool with timeout_s (default 10min).
        2. On timeout, if restart_unity_on_timeout: attempt to close/reopen Unity.
        3. Retry up to retry_count times.
        4. If total time exceeds max_total_s (default 20min), skip.
        """
        import time

        request_id = new_id("req")
        start = time.monotonic()
        attempts = 0
        last_error = ""
        events: list[dict] = []

        for attempt in range(retry_count + 1):
            attempts += 1
            elapsed_total = time.monotonic() - start
            if elapsed_total >= max_total_s:
                events.append(
                    {"event": "max_total_exceeded", "elapsed": round(elapsed_total, 1)}
                )
                break

            remaining_total = max_total_s - elapsed_total
            effective_timeout = min(timeout_s, remaining_total)

            try:
                result = await asyncio.wait_for(
                    self._dispatch_tool(tool_name, tool_args or {}),
                    timeout=effective_timeout,
                )
                if result.ok and self._task_execute_tool_succeeded(tool_name, result):
                    return ok(
                        request_id,
                        {
                            "taskName": task_name,
                            "status": "completed",
                            "attempt": attempts,
                            "elapsedS": round(time.monotonic() - start, 1),
                            "events": events,
                            "result": result.data,
                        },
                    )
                if result.ok:
                    last_error = self._task_execute_logical_error(tool_name, result)
                    events.append(
                        {
                            "event": "tool_logical_failure",
                            "attempt": attempts,
                            "tool": tool_name,
                            "error": last_error,
                        }
                    )
                else:
                    last_error = (
                        result.error.message if result.error else "tool returned error"
                    )
                    events.append(
                        {
                            "event": "tool_error",
                            "attempt": attempts,
                            "error": last_error,
                        }
                    )
            except asyncio.TimeoutError:
                last_error = (
                    f"Timeout after {effective_timeout:.0f}s on attempt {attempts}"
                )
                events.append(
                    {
                        "event": "timeout",
                        "attempt": attempts,
                        "timeoutS": round(effective_timeout, 1),
                    }
                )

                if restart_unity_on_timeout and attempt < retry_count:
                    events.append(
                        {"event": "restart_unity_requested", "attempt": attempts}
                    )
                    try:
                        await self._restart_unity_connection(events)
                    except Exception as e:
                        events.append({"event": "restart_failed", "error": str(e)})
            except Exception as e:
                last_error = str(e)
                events.append(
                    {"event": "exception", "attempt": attempts, "error": last_error}
                )

        return fail(
            request_id,
            "TASK_FAILED",
            last_error or "Task did not complete successfully",
            {
                "taskName": task_name,
                "status": "failed",
                "attempts": attempts,
                "elapsedS": round(time.monotonic() - start, 1),
                "events": events,
            },
        )

    async def _restart_unity_connection(self, events: list[dict]) -> None:
        """Wait for Unity to disconnect and reconnect (soft restart via domain reload)."""
        import time

        start = time.monotonic()
        # Wait up to 90s for Unity to reconnect (it may already be reconnecting)
        for i in range(45):
            if self.server.session_manager.is_connected():
                events.append(
                    {
                        "event": "unity_reconnected",
                        "waitS": round(time.monotonic() - start, 1),
                    }
                )
                ready_r = await self.ensure_ready(timeout_s=60)
                if ready_r.ok and ready_r.data and ready_r.data.get("ready"):
                    events.append({"event": "unity_ready_after_restart"})
                    return
            await asyncio.sleep(2)
        events.append(
            {
                "event": "unity_reconnect_timeout",
                "waitS": round(time.monotonic() - start, 1),
            }
        )

    async def _dispatch_tool(self, tool_name: str, tool_args: dict) -> ToolResponse:
        """Route a public MCP tool name through the shared registry."""
        return await dispatch_public_tool(self, tool_name, tool_args)

    async def task_start(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        retry_count: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        task_id = new_id("task")
        state = {
            "taskId": task_id,
            "taskName": task_name,
            "toolName": tool_name,
            "status": "queued",
            "phase": "queued",
            "startedAt": now_ms(),
            "updatedAt": now_ms(),
            "endedAt": 0,
            "result": None,
            "error": None,
        }
        self._async_tasks[task_id] = state

        async def run() -> None:
            state["status"] = "running"
            state["phase"] = "executing"
            state["updatedAt"] = now_ms()
            result = await self.task_execute(
                task_name=task_name,
                tool_name=tool_name,
                tool_args=tool_args,
                timeout_s=timeout_s,
                max_total_s=timeout_s * max(1, retry_count + 1),
                retry_count=retry_count,
                restart_unity_on_timeout=False,
            )
            state["updatedAt"] = now_ms()
            state["endedAt"] = now_ms()
            if result.ok:
                state["status"] = "completed"
                state["phase"] = "completed"
                state["result"] = result.data
            else:
                state["status"] = "failed"
                state["phase"] = "failed"
                state["error"] = {
                    "code": result.error.code if result.error else "TASK_FAILED",
                    "message": result.error.message if result.error else "Task failed",
                    "detail": result.error.detail if result.error else {},
                }

        self._async_task_handles[task_id] = asyncio.create_task(run(), name=task_id)
        return ok(request_id, state.copy())

    async def task_status(self, task_id: str) -> ToolResponse:
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        result = state.copy()
        result["elapsedMs"] = max(0, (result["endedAt"] or now_ms()) - result["startedAt"])
        return ok(new_id("req"), result)

    async def task_cancel(self, task_id: str) -> ToolResponse:
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        handle = self._async_task_handles.get(task_id)
        if handle and not handle.done():
            handle.cancel()
        state["status"] = "cancelled"
        state["phase"] = "cancelled"
        state["updatedAt"] = now_ms()
        state["endedAt"] = now_ms()
        return ok(new_id("req"), state.copy())
