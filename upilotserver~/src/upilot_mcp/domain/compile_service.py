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
from ..console_evidence import begin_console_evidence, finish_console_evidence
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

class CompileDomainService:
    def _compile_waiting_diagnostics(self, execution: dict | None = None) -> dict:
        """Return a read-only diagnosis for the currently observed compile.

        This deliberately reports observation state only.  In particular, an
        attention flag is never a timeout, cancellation, mode change, or a
        reason to submit another compilation.
        """
        execution = execution or self.server.state.execution_state(
            stale_after_ms=CONFIG.context_stale_ms
        )
        compile_state = self.server.state.compile
        get_batch = getattr(self.server.state, "get_write_batch", None)
        batch_id = str(compile_state.write_batch_id or execution.get("pendingWriteBatchId") or "")
        batch = get_batch(batch_id) if batch_id and callable(get_batch) else None
        terminal = bool(execution.get("terminal"))
        created_at = int((batch or {}).get("writeBatchCreatedAt") or compile_state.write_batch_created_at or 0)
        last_progress_at = int(execution.get("lastProgressAt") or created_at or 0)
        now = now_ms()
        progress_age_ms = max(0, now - last_progress_at) if last_progress_at and not terminal else 0
        phase = str(execution.get("compilePhase") or "").lower()

        if terminal:
            reason = "none"
        elif str((batch or {}).get("status") or "") == "recovery_required":
            reason = "recovery_required"
        elif execution.get("unityConnected") is False:
            reason = "disconnected"
        elif phase in {"domain_reload", "verifying"}:
            reason = "reload"
        # A stale snapshot can retain a raw isPlaying flag.  Only an
        # authoritative snapshot may ask the caller to leave PlayMode.
        elif execution.get("authoritative") and str(execution.get("playModeState") or "") in {"play", "pause"}:
            reason = "playmode"
        elif not execution.get("authoritative") or execution.get("isStale"):
            reason = "stale"
        elif phase in {"queued", "compiling", "compiler_finished"} or bool(execution.get("isCompiling")):
            reason = "compile_in_progress"
        else:
            reason = "none"

        actions = {
            "playmode": "Exit PlayMode only after user confirmation, then observe the same batch.",
            "reload": "Wait for the same batch to recover; do not switch PlayMode.",
            "disconnected": "Reconnect the intended Unity project and observe the same batch; do not recompile.",
            "stale": "Wait for a fresh authoritative Editor snapshot; do not change PlayMode or recompile.",
            "recovery_required": "Observe the original compile identity; do not trigger a replacement compile for this batch.",
            "compile_in_progress": "Continue observing the current compile; do not submit a second compile.",
        }
        return {
            "waitingReason": reason,
            "pendingAgeMs": max(0, now - created_at) if created_at and not terminal else 0,
            "lastProgressAt": int(execution.get("lastProgressAt") or 0),
            "attentionRequired": bool(not terminal and progress_age_ms > 30000),
            "waitingNextAction": actions.get(reason, "No compile wait is currently required."),
        }

    def _automatic_compile_reuse_diagnostics(self) -> dict:
        """Describe whether the current automatic compile may cover a write batch.

        Unity's automatic compiler callbacks identify their own operation, but do
        not expose a complete input manifest.  Do not turn timing or a matching
        current file hash into proof that an unregistered change was compiled.
        This is deliberately a rejection diagnosis, not a fallback compiler.
        """
        compile_state = self.server.state.compile
        if str(compile_state.compile_origin or "") != "unity_auto":
            return {}
        observed_batch_id = str(compile_state.write_batch_id or "")
        missing_evidence = [
            "complete_input_manifest",
            "verified_input_coverage",
            "no_later_related_changes",
        ]
        if observed_batch_id:
            # A batch identity arriving with an automatic pipeline event is not
            # proof that the batch was registered before this compilation began,
            # nor that every write/delete/asmdef input was in scope.  The state
            # contract currently has no complete input manifest, so it cannot
            # satisfy the P2 reuse admission rule.
            missing_evidence.insert(0, "batch_registered_before_automatic_compile")
        else:
            missing_evidence.insert(0, "registered_write_batch")
        return {
            "reuseDecision": "unattributed_auto_compile",
            "inputCoverageVerified": False,
            "replayStartAttempted": False,
            "observedWriteBatchId": observed_batch_id,
            "missingEvidence": missing_evidence,
            "reuseNextAction": "Register the saved change batch and use its correlated compile result; do not treat this automatic compile as reusable evidence.",
        }

    @staticmethod
    def _response_compile_request_id(data: dict) -> str:
        """Read the compile identity from either current or legacy Unity payloads."""
        return str(data.get("compileRequestId") or data.get("requestId") or "")

    def _compile_diagnostics(self) -> dict:
        compile_state = self.server.state.compile
        execution = self.server.state.execution_state(
            stale_after_ms=CONFIG.context_stale_ms
        )
        return {
            "executionState": execution,
            "status": compile_state.status,
            "phase": execution["compilePhase"],
            "compileRequestId": compile_state.compile_request_id,
            "commandQueuedAt": compile_state.command_queued_at,
            "unityAcceptedAt": compile_state.unity_accepted_at,
            "startedAt": compile_state.started_at,
            "finishedAt": compile_state.finished_at,
            "lastProgressAt": execution["lastProgressAt"],
            "lastEditorUpdateAt": execution["lastMainThreadPumpAt"],
            "editorPumpAgeMs": execution["editorPumpAgeMs"],
            "editorNotPumping": bool(
                execution["lastMainThreadPumpAt"]
                and execution["editorPumpAgeMs"] > 10000
            ),
            "mainThreadQueueDepth": execution["mainThreadQueueDepth"],
            "lastDequeuedCommandId": execution["lastDequeuedCommandId"],
            "suspectedStuck": execution["suspectedStuck"],
            "errorCount": compile_state.error_count,
            "warningCount": compile_state.warning_count,
            "currentCompileWarningCount": compile_state.warning_count,
            "historicalWarningCount": 0,
            "importerWarningCount": 0,
            "diagnosticSource": "compile-request-snapshot",
            "blocked": execution["blocked"],
            "blockedReason": execution["blockedReason"],
            "nextAction": execution["nextAction"],
            **self._compile_waiting_diagnostics(execution),
            **self._automatic_compile_reuse_diagnostics(),
        }

    async def _refresh_execution_state(self) -> tuple[ToolResponse, dict]:
        result = await self.dispatcher.call(
            new_id("req"), "resource.editorState", {}, timeout_ms=15000
        )
        return result, self.server.state.execution_state(
            stale_after_ms=CONFIG.context_stale_ms
        )

    @staticmethod
    def _compile_precondition_error(request_id: str, execution: dict) -> ToolResponse | None:
        reason = str(execution.get("blockedReason") or "")
        if reason == "PlayMode":
            return fail(
                request_id,
                "EDITOR_IN_PLAY_MODE",
                "Unity is in PlayMode or paused; script compilation is blocked.",
                {**execution, "playModeBlocked": True, "dispatchAttempted": False},
            )
        if not execution.get("authoritative") or execution.get("isStale"):
            return fail(
                request_id,
                "EDITOR_CONTEXT_NOT_READY",
                "Unity Editor context is stale, unknown, or recovering after Domain Reload.",
                {**execution, "dispatchAttempted": False},
            )
        if str(execution.get("playModeState") or "") != "edit":
            return fail(
                request_id,
                "EDITOR_CONTEXT_NOT_READY",
                "Unity Editor mode is not authoritatively known to be EditMode.",
                {**execution, "dispatchAttempted": False},
            )
        return None

    def _detect_library_dll_mtime(self) -> int:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return 0
        library_dir = Path(session.project_path) / "Library"
        if not library_dir.exists() or not library_dir.is_dir():
            return 0
        latest = 0.0
        patterns = ["**/*.dll", "**/*.dll.mdb", "**/*.pdb"]
        for pattern in patterns:
            for p in library_dir.glob(pattern):
                try:
                    ts = p.stat().st_mtime
                    if ts > latest:
                        latest = ts
                except OSError:
                    continue
        return int(latest * 1000)

    def _sync_workspace_root_from_session(self) -> None:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return
        self.patch_service.set_workspace_root(session.project_path)
        self.fix_planner.workspace_root = self.patch_service.workspace_root

    async def _post_patch_sync_after_write(self) -> None:
        logger = logging.getLogger("upilot.facade")
        r = await self.sync_after_disk_write(delay_s=2.0, trigger_compile=True)
        if not r.ok:
            logger.warning(
                "post_patch_sync_after_write: %s",
                r.error.message if r.error else "unknown",
            )

    async def compile(
        self,
        *,
        write_batch_id: str = "",
        write_batch_created_at: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        state_r, execution = await self._refresh_execution_state()
        if not state_r.ok:
            if state_r.error is not None:
                state_r.error.detail["dispatchAttempted"] = False
            return state_r
        precondition_error = self._compile_precondition_error(request_id, execution)
        if precondition_error is not None:
            return precondition_error
        if bool(execution.get("isCompiling")):
            return fail(
                request_id,
                "EDITOR_BUSY",
                "Unity compilation is already active.",
                execution,
            )
        compile_state = self.server.state.compile
        uses_v2_state = bool(self.server.state.producer_epoch)
        if not uses_v2_state:
            compile_state.phase = "queued"
            compile_state.status = "queued"
        manager = getattr(self.server, "session_manager", None)
        active_session = getattr(manager, "active", None)
        compile_state.initial_session_id = str(
            getattr(active_session, "session_id", "") or ""
        )
        compile_state.command_queued_at = now_ms()
        compile_state.unity_accepted_at = 0
        if not uses_v2_state:
            compile_state.started_at = 0
            compile_state.finished_at = 0
            compile_state.last_progress_at = compile_state.command_queued_at
            compile_state.write_batch_id = write_batch_id
            compile_state.write_batch_created_at = max(0, int(write_batch_created_at))
            compile_state.terminal = False
            compile_state.errors_verified = False
            compile_state.verification_pending = True
        console_evidence: dict | None = None
        if not bool(getattr(self, "_suppress_compile_console_evidence", False)):
            try:
                console_evidence = await begin_console_evidence(self.dispatcher, self.server.state)
            except Exception as exc:
                console_evidence = {
                    "source": "unavailable",
                    "coverage": "unavailable",
                    "gapReason": f"start_boundary_failed:{type(exc).__name__}",
                    "logs": [],
                }

        async def attach_console_evidence(response: ToolResponse, *, close_boundary: bool = False) -> ToolResponse:
            nonlocal console_evidence
            if console_evidence is None:
                return response
            if close_boundary and console_evidence.get("coverage") == "pending":
                try:
                    console_evidence = await finish_console_evidence(
                        self.dispatcher, self.server.state, console_evidence
                    )
                except Exception as exc:
                    console_evidence = {
                        **console_evidence,
                        "coverage": "partial",
                        "gapReason": f"end_boundary_failed:{type(exc).__name__}",
                    }
            response_identity = (
                response.data
                if isinstance(response.data, dict)
                else response.error.detail if response.error is not None else {}
            )
            operation_id = str(
                response_identity.get("compileOperationId")
                or self.server.state.compile.compile_operation_id
                or ""
            )
            if operation_id:
                self.server.state.save_console_evidence(
                    "compileOperationId", operation_id, console_evidence
                )
            if response.data is not None:
                response.data["consoleEvidence"] = console_evidence
            elif response.error is not None:
                response.error.detail["consoleEvidence"] = console_evidence
            return response
        result = await self.dispatcher.call(
            request_id,
            "compile.request",
            {
                "requestId": request_id,
                "writeBatchId": write_batch_id,
                "writeBatchCreatedAt": max(0, int(write_batch_created_at)),
            },
            timeout_ms=180000,
        )

        # If compile failed due to domain reload disconnect, wait for reconnect then return status
        if (
            not result.ok
            and result.error
            and result.error.code in ("CONNECTION_LOST", "DOMAIN_RELOAD_TIMEOUT")
        ):
            import asyncio

            # Wait up to 60s for Unity to reconnect after domain reload
            for _ in range(30):
                await asyncio.sleep(2)
                if self.server.session_manager.is_connected():
                    # Re-query compile errors after reconnect
                    errors_result = await self.dispatcher.call(
                        new_id("req"), "compile.errors.get", {}, timeout_ms=15000
                    )
                    compile_state = self.server.state.compile
                    return await attach_console_evidence(ok(
                        request_id,
                        {
                            "accepted": True,
                            "compileRequestId": request_id,
                            "triggerMode": "incremental",
                            "requestIssued": True,
                            "cleanBuildCache": False,
                            "attachedToExistingCompile": False,
                            "status": "finished_after_reload",
                            "reconnected": True,
                            "errors": errors_result.data if errors_result.ok else {},
                            "compileState": {
                                "status": compile_state.status,
                                "errorCount": compile_state.error_count,
                            },
                        },
                    ), close_boundary=True)
            return await attach_console_evidence(fail(
                request_id,
                "COMPILE_RECONNECT_TIMEOUT",
                "编译触发域重载后 Unity 未能重连",
                {"requestId": request_id},
            ))

        if result.ok:
            compile_state.unity_accepted_at = now_ms()
            if result.data:
                compile_state.compile_operation_id = str(
                    result.data.get("compileOperationId") or compile_state.compile_operation_id
                )
            terminal = bool(compile_state.terminal) if uses_v2_state else bool(
                compile_state.finished_at
                or compile_state.status in ("finished", "completed")
                or compile_state.phase == "completed"
            )
            if not terminal and not uses_v2_state:
                compile_state.status = (
                    "accepted"
                    if compile_state.status in ("idle", "queued")
                    else compile_state.status
                )
                compile_state.phase = "accepted"
                compile_state.last_progress_at = compile_state.unity_accepted_at
            execution = self.server.state.execution_state(
                stale_after_ms=CONFIG.context_stale_ms
            )
            return await attach_console_evidence(ok(
                request_id,
                {
                    **(result.data or {}),
                    "triggerMode": "incremental",
                    "requestIssued": True,
                    "cleanBuildCache": False,
                    "attachedToExistingCompile": False,
                    "status": str(execution.get("compilePhase") or "queued"),
                    "phase": str(execution.get("compilePhase") or "queued"),
                    "terminal": bool(execution.get("terminal")),
                    "executionState": execution,
                },
                context=result.context,
                timing=result.timing,
            ), close_boundary=bool(execution.get("terminal")))
        return await attach_console_evidence(result, close_boundary=True)

    async def compile_status(self, compile_request_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        compile_state = self.server.state.compile
        data = self._compile_diagnostics()
        data["compileRequestId"] = compile_request_id or compile_state.compile_request_id
        return ok(request_id, data, context=data["executionState"])

    async def compile_errors(self, compile_request_id: str = "", include_warnings: bool = False) -> ToolResponse:
        request_id = new_id("req")
        # Do not use Python truthiness here: `{}`/`[]`/`"true"` must not turn
        # into an opt-in request for potentially large warning details.
        if not isinstance(include_warnings, bool):
            return fail(
                request_id,
                "INVALID_PAYLOAD",
                "includeWarnings must be a boolean.",
                {
                    "includeWarnings": include_warnings,
                    "dispatchAttempted": False,
                },
            )
        # Force a live persisted error query. Transport failure is unknown, never "no errors".
        result = await self.dispatcher.call(
            request_id, "compile.errors.get", {"includeWarnings": include_warnings}, timeout_ms=45000
        )
        if result.ok:
            if not isinstance(result.data, dict):
                return fail(
                    request_id,
                    "INVALID_COMPILE_DIAGNOSTICS",
                    "Unity returned a non-object compile diagnostics payload.",
                    {"compileRequestId": compile_request_id, "diagnosticType": type(result.data).__name__},
                )
            data = dict(result.data)
            source_compile_request_id = self._response_compile_request_id(data)
            if (compile_request_id and source_compile_request_id
                    and source_compile_request_id != compile_request_id):
                # A query scoped to A must not overwrite the current B snapshot
                # merely because B happened to finish while it was in flight.
                return fail(
                    request_id,
                    "COMPILE_IDENTITY_MISMATCH",
                    "Unity returned diagnostics for a different compile request.",
                    {
                        "expectedCompileRequestId": compile_request_id,
                        "actualCompileRequestId": source_compile_request_id,
                        "stateUpdated": False,
                    },
                )
            if source_compile_request_id:
                data["compileRequestId"] = source_compile_request_id

            if include_warnings:
                warnings = data.get("warnings")
                details_available = data.get("warningDetailsAvailable")
                if warnings is not None and not isinstance(warnings, list):
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity returned warning details in a non-array form.",
                        {"compileRequestId": source_compile_request_id or compile_request_id,
                         "warningsType": type(warnings).__name__},
                    )
                if details_available is not None and not isinstance(details_available, bool):
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity returned warningDetailsAvailable in a non-boolean form.",
                        {"compileRequestId": source_compile_request_id or compile_request_id,
                         "warningDetailsAvailableType": type(details_available).__name__},
                    )
                warning_count = data.get("warningCount")
                if (warning_count is not None
                        and (not isinstance(warning_count, int) or isinstance(warning_count, bool)
                             or warning_count < 0)):
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity returned warningCount in an invalid form.",
                        {"compileRequestId": source_compile_request_id or compile_request_id,
                         "warningCountType": type(warning_count).__name__},
                    )
                warnings_truncated = data.get("warningsTruncated")
                if warnings_truncated is not None and not isinstance(warnings_truncated, bool):
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity returned warningsTruncated in a non-boolean form.",
                        {"compileRequestId": source_compile_request_id or compile_request_id,
                         "warningsTruncatedType": type(warnings_truncated).__name__},
                    )
                if isinstance(warnings, list) and any(not isinstance(item, dict) for item in warnings):
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity returned a warning detail that was not an object.",
                        {"compileRequestId": source_compile_request_id or compile_request_id,
                         "warningDetailType": next(type(item).__name__ for item in warnings if not isinstance(item, dict))},
                    )
                details_available = bool(details_available) if details_available is not None else isinstance(warnings, list)
                if details_available and warnings is None:
                    return fail(
                        request_id,
                        "INVALID_COMPILE_DIAGNOSTICS",
                        "Unity reported available warning details without a warnings array.",
                        {"compileRequestId": source_compile_request_id or compile_request_id},
                    )
                if details_available and isinstance(warnings, list):
                    limited_warnings = warnings[:1000]
                    data["warnings"] = limited_warnings
                    data["warningsTruncated"] = bool(data.get("warningsTruncated")) or len(warnings) > 1000 or (
                        isinstance(warning_count, int) and not isinstance(warning_count, bool)
                        and warning_count > len(limited_warnings)
                    )
                else:
                    # An old persisted record may know the count but have lost
                    # its per-warning payload.  Absence is deliberately not an
                    # empty warning list.  An explicit unavailable response is
                    # also authoritative: it must clear, rather than leak, any
                    # contradictory warnings array supplied alongside it.
                    data.pop("warnings", None)
                    data["warningsTruncated"] = False
                data["warningDetailsAvailable"] = details_available
            else:
                data.pop("warnings", None)

            state = self.server.state
            if not state.matches_authoritative_compile_identity(data):
                current = state.compile
                return fail(
                    request_id,
                    "COMPILE_IDENTITY_MISMATCH",
                    "Unity returned diagnostics for a different authoritative compilation.",
                    {
                        "expectedCompileRequestId": current.compile_request_id,
                        "actualCompileRequestId": source_compile_request_id,
                        "expectedCompileOperationId": current.compile_operation_id,
                        "actualCompileOperationId": str(data.get("compileOperationId") or ""),
                        "expectedWriteBatchId": current.write_batch_id,
                        "actualWriteBatchId": str(data.get("writeBatchId") or ""),
                        "stateUpdated": False,
                    },
                )
            if not state.update_compile_errors(data):
                return fail(
                    request_id,
                    "COMPILE_IDENTITY_MISMATCH",
                    "Unity diagnostics were rejected because the authoritative compilation changed.",
                    {"stateUpdated": False},
                )
            if include_warnings:
                current = state.compile
                # Never attribute an automatic compile to a batch registered
                # after it began.  The current state contract has no input
                # manifest sufficient to turn that event into evidence.
                can_persist = bool(
                    state.producer_epoch
                    and state.editor.authoritative
                    # Only an explicitly request-scoped origin is enough to
                    # bind diagnostics to a durable batch.  Unknown is not a
                    # harmless legacy default: it is insufficient evidence.
                    and current.compile_origin == "mcp"
                    and current.write_batch_id
                    and current.compile_operation_id
                    and current.compile_request_id
                    and source_compile_request_id == current.compile_request_id
                )
                if can_persist:
                    data["warningDetailsPersisted"] = state.persist_write_batch_warnings(
                        write_batch_id=current.write_batch_id,
                        compile_operation_id=current.compile_operation_id,
                        compile_request_id=current.compile_request_id,
                        details_available=bool(data.get("warningDetailsAvailable")),
                        warnings_truncated=bool(data.get("warningsTruncated")),
                        warnings=data.get("warnings") if isinstance(data.get("warnings"), list) else None,
                    )
                else:
                    # State persistence is an evidence claim.  Report an
                    # explicit false rather than leaving callers to mistake an
                    # omitted field for successful association.
                    data["warningDetailsPersisted"] = False
            data.setdefault("source", "live")
            data.setdefault("mode", "strict_live")
            data.setdefault("compileRequestId", compile_request_id)
            data["includeWarnings"] = include_warnings
            data["currentCompileWarningCount"] = int(data.get("warningCount") or 0)
            data.setdefault("historicalWarningCount", 0)
            data.setdefault("importerWarningCount", 0)
            data.setdefault("diagnosticSource", "compile.errors.get")
            if not include_warnings:
                data.pop("warnings", None)
            data["status"] = "failed" if int(data.get("total") or 0) > 0 else str(
                data.get("status") or self.server.state.compile.status or "completed"
            )
            data["phase"] = "failed" if int(data.get("total") or 0) > 0 else str(
                data.get("phase") or self.server.state.compile.phase or "completed"
            )
            data["terminal"] = bool(data.get("terminal", self.server.state.compile.terminal))
            data["executionState"] = self.server.state.execution_state(
                stale_after_ms=CONFIG.context_stale_ms
            )
            return ok(request_id, data, context=data["executionState"])

        # A failed live query cannot prove a compile result.
        dll_mtime_ms = self._detect_library_dll_mtime()
        return fail(
            request_id,
            result.error.code if result.error else "COMPILE_ERRORS_UNAVAILABLE",
            result.error.message if result.error else "Could not query Unity's persisted compile errors.",
            {
                "compileRequestId": compile_request_id,
                "source": "live",
                "mode": "strict_live",
                "diagnosticSource": "compile.errors.get-unavailable",
                "diagnostics": {
                    "libraryDllLatestWriteMs": dll_mtime_ms,
                    "libraryDllExists": dll_mtime_ms > 0,
                },
            },
        )

    async def auto_fix_start(
        self, max_iterations: int = 20, stop_when_no_error: bool = True
    ) -> ToolResponse:
        self._sync_workspace_root_from_session()
        request_id = new_id("req")
        loop = await self.auto_fix_loop.start(
            max_iterations=max_iterations, stop_when_no_error=stop_when_no_error
        )
        return ok(request_id, asdict(loop))

    async def auto_fix_stop(self, loop_id: str) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.stop(loop_id)
        if not loop:
            return fail(
                request_id, "INVALID_PAYLOAD", "loopId 不存在", {"loopId": loop_id}
            )
        return ok(request_id, asdict(loop))

    async def auto_fix_status(self) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.status()
        if not loop:
            return ok(request_id, {"status": "idle"})
        return ok(request_id, asdict(loop))

    async def compile_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
    ) -> ToolResponse:
        response = await self._compile_wait(timeout_s, poll_interval_s, prefer_events)
        operation_id = str(self.server.state.compile.compile_operation_id or "")
        if not operation_id:
            return response
        evidence = self.server.state.get_console_evidence("compileOperationId", operation_id)
        if not evidence:
            return response
        data = response.data if isinstance(response.data, dict) else {}
        execution = self.server.state.execution_state(stale_after_ms=CONFIG.context_stale_ms)
        terminal = bool(
            data.get("terminal")
            or execution.get("terminal")
            or str(data.get("status") or "").lower() in {"ready", "failed", "completed"}
        )
        if terminal and evidence.get("coverage") == "pending":
            try:
                evidence = await finish_console_evidence(self.dispatcher, self.server.state, evidence)
            except Exception as exc:
                evidence = {
                    **evidence,
                    "coverage": "partial",
                    "gapReason": f"end_boundary_failed:{type(exc).__name__}",
                }
            self.server.state.save_console_evidence("compileOperationId", operation_id, evidence)
        if response.data is not None:
            response.data["consoleEvidence"] = evidence
        elif response.error is not None:
            response.error.detail["consoleEvidence"] = evidence
        return response

    async def _compile_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
    ) -> ToolResponse:
        """Wait until editor reports not compiling: compile.* WebSocket events, then exponential backoff poll."""
        import time

        request_id = new_id("req")
        deadline = time.monotonic() + timeout_s
        polls = 0
        reconnect_waited = False
        modes: list[str] = []
        last_wake = 0.0

        async def poll_editor_state() -> ToolResponse:
            return await self.dispatcher.call(new_id("req"), "resource.editorState", {})

        while True:
            polls += 1
            # Wake Unity every ~5 seconds to avoid background throttling when unfocused
            if time.monotonic() - last_wake >= 5.0:
                if self._wake_unity_editor():
                    last_wake = time.monotonic()

            r = await poll_editor_state()
            if not r.ok:
                err_code = r.error.code if r.error else ""
                if err_code in (
                    "UNITY_NOT_CONNECTED",
                    "CONNECTION_LOST",
                    "DOMAIN_RELOAD_TIMEOUT",
                    "COMMAND_TIMEOUT",
                ):
                    if time.monotonic() >= deadline:
                        observation_timed_out = err_code == "COMMAND_TIMEOUT"
                        return ok(
                            request_id,
                            {
                                **self._compile_diagnostics(),
                                "status": "timeout",
                                "isCompiling": True,
                                "pollCount": polls,
                                "elapsedS": timeout_s,
                                "note": (
                                    "Unity state observation timed out; the original compile outcome remains unknown."
                                    if observation_timed_out
                                    else "Unity disconnected (likely domain reload)"
                                ),
                                "lastObservationError": err_code,
                                "reconnectedDuringWait": reconnect_waited,
                                "waitMode": "observation_timeout" if observation_timed_out else "disconnect_timeout",
                                "completed": False,
                                "timedOut": True,
                            },
                        )
                    reconnect_waited = True
                    await asyncio.sleep(poll_interval_s * 2)
                    continue
                return r

            execution = self.server.state.execution_state(
                stale_after_ms=CONFIG.context_stale_ms
            )
            editor_is_compiling = bool(r.data.get("isCompiling", False)) if r.data else False
            compile_phase = str(execution.get("compilePhase") or "idle")

            if execution.get("blockedReason") == "PlayMode":
                terminal_execution = self.server.state.execution_state(
                    stale_after_ms=CONFIG.context_stale_ms
                )
                return ok(
                    request_id,
                    {
                        **self._compile_diagnostics(),
                        "status": "blocked",
                        "blocked": True,
                        "blockedReason": "PlayMode",
                        "playModeBlocked": True,
                        "isCompiling": False,
                        "pollCount": polls,
                        "elapsedS": round(timeout_s - (deadline - time.monotonic()), 2),
                        "waitMode": "blocked",
                        "completed": False,
                    },
                )

            context_ready = bool(execution.get("authoritative")) and not bool(
                execution.get("isStale")
            )
            if (
                context_ready
                and compile_phase == "verifying"
            ):
                errors_result = await self.compile_errors(
                    self.server.state.compile.compile_request_id
                )
                if errors_result.ok and errors_result.data:
                    error_total = int(errors_result.data.get("total") or 0)
                    if error_total > 0:
                        failed_execution = self.server.state.execution_state(
                            stale_after_ms=CONFIG.context_stale_ms
                        )
                        return ok(
                            request_id,
                            {
                                **self._compile_diagnostics(),
                                "status": "failed",
                                "phase": "failed",
                                "terminal": True,
                                "pollCount": polls,
                                "elapsedS": round(timeout_s - (deadline - time.monotonic()), 2),
                                "reconnectedDuringWait": reconnect_waited,
                                "errors": errors_result.data.get("errors") or [],
                            },
                            context=failed_execution,
                        )
                    execution = self.server.state.execution_state(
                        stale_after_ms=CONFIG.context_stale_ms
                    )
                    compile_phase = str(execution.get("compilePhase") or "completed")

            if (
                context_ready
                and not editor_is_compiling
                and compile_phase == "failed"
            ):
                failed_execution = self.server.state.execution_state(
                    stale_after_ms=CONFIG.context_stale_ms
                )
                return ok(
                    request_id,
                    {
                        **self._compile_diagnostics(),
                        "status": "failed",
                        "phase": "failed",
                        "terminal": True,
                        "pollCount": polls,
                        "elapsedS": round(timeout_s - (deadline - time.monotonic()), 2),
                        "reconnectedDuringWait": reconnect_waited,
                    },
                    context=failed_execution,
                )

            terminal_idle = (
                context_ready
                and not editor_is_compiling
                and compile_phase not in ("queued", "compiling", "compiler_finished", "domain_reload", "verifying")
            )
            if terminal_idle:
                if polls == 1:
                    wm = "immediate"
                elif modes:
                    wm = "+".join(modes) + "+poll"
                else:
                    wm = "poll"
                return ok(
                    request_id,
                    {
                        **self._compile_diagnostics(),
                        "status": "ready",
                        "isCompiling": False,
                        "pollCount": polls,
                        "elapsedS": round(timeout_s - (deadline - time.monotonic()), 2),
                        "reconnectedDuringWait": reconnect_waited,
                        "waitMode": wm,
                    },
                )

            if polls == 1:
                self.server.reconcile_editor_compile_busy(
                    editor_is_compiling or compile_phase in ("compiling", "domain_reload")
                )

            if polls == 1 and prefer_events and self.server.is_ready():
                remaining = deadline - time.monotonic()
                if remaining > 0:
                    ev_budget = min(45.0, max(5.0, timeout_s * 0.35))
                    ev_budget = min(ev_budget, remaining)
                    if await self.server.wait_for_compile_idle(ev_budget):
                        modes.append("event")
                        continue

            interval = (
                min(poll_interval_s, 0.25)
                if polls <= 2
                else min(poll_interval_s * (1.5 ** min(polls - 3, 8)), 2.0)
            )
            if time.monotonic() >= deadline:
                return ok(
                    request_id,
                    {
                        **self._compile_diagnostics(),
                        "status": "timeout",
                        "isCompiling": bool(editor_is_compiling),
                        "pollCount": polls,
                        "elapsedS": timeout_s,
                        "reconnectedDuringWait": reconnect_waited,
                        "waitMode": "timeout+" + "+".join(modes)
                        if modes
                        else "timeout",
                        "completed": False,
                        "timedOut": True,
                    },
                )
            if editor_is_compiling:
                self.server.sync_compile_state_from_editor(True)
            await asyncio.sleep(interval)

    async def compile_wait_editor(self, timeout_ms: int = 300000) -> ToolResponse:
        """Single Bridge command: block in Unity until EditorApplication.isCompiling is false."""
        request_id = new_id("req")
        tw = int(timeout_ms) + 90000
        if tw > 660000:
            tw = 660000
        return await self.dispatcher.call(
            request_id,
            "compile.wait",
            {"timeoutMs": int(timeout_ms)},
            timeout_ms=tw,
        )

    async def safe_compile_and_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
        post_compile_delay_s: float = 3.0,
        attach_compile_request_id: str = "",
        write_batch_id: str = "",
        write_batch_created_at: int = 0,
        compile_operation_id: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        # The batch identity is an authorization and correlation boundary, not
        # a hint.  Reject malformed values before the workflow can attach to
        # or dispatch any Unity compilation.
        if not isinstance(write_batch_id, str):
            return fail(
                request_id,
                "INVALID_WRITE_BATCH_ID",
                "writeBatchId must be a string.",
                {"writeBatchId": write_batch_id, "dispatchAttempted": False},
            )
        if not isinstance(write_batch_created_at, int) or isinstance(write_batch_created_at, bool) or write_batch_created_at < 0:
            return fail(
                request_id,
                "INVALID_WRITE_BATCH_TIMESTAMP",
                "writeBatchCreatedAt must be a non-negative integer.",
                {"writeBatchCreatedAt": write_batch_created_at, "dispatchAttempted": False},
            )
        if not isinstance(compile_operation_id, str):
            return fail(
                request_id,
                "INVALID_COMPILE_OPERATION_ID",
                "compileOperationId must be a string.",
                {"compileOperationId": compile_operation_id, "dispatchAttempted": False},
            )
        if write_batch_id and not write_batch_id.strip():
            return fail(
                request_id,
                "INVALID_WRITE_BATCH_ID",
                "writeBatchId must not be whitespace only.",
                {"writeBatchId": write_batch_id, "dispatchAttempted": False},
            )
        identity = {
            "compileRequestId": attach_compile_request_id,
            "compileOperationId": compile_operation_id,
            "writeBatchId": write_batch_id,
            "writeBatchCreatedAt": write_batch_created_at,
        }
        in_flight_batches = getattr(self, "_write_batch_safe_waits", None)
        if in_flight_batches is None:
            in_flight_batches = self._write_batch_safe_waits = set()
        if write_batch_id:
            if write_batch_id in in_flight_batches:
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "The original batch is already being dispatched or observed.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe the original write batch; do not dispatch another compile."})
            in_flight_batches.add(write_batch_id)
        try:
            console_evidence = await begin_console_evidence(self.dispatcher, self.server.state)
        except asyncio.CancelledError:
            if write_batch_id:
                in_flight_batches.discard(write_batch_id)
            raise
        except Exception as exc:
            console_evidence = {
                "source": "unavailable", "coverage": "unavailable",
                "gapReason": f"start_boundary_failed:{type(exc).__name__}", "logs": [],
            }
        try:
            response = await self._safe_compile_and_wait(
                timeout_s, poll_interval_s, prefer_events, post_compile_delay_s,
                attach_compile_request_id, write_batch_id, write_batch_created_at,
                compile_operation_id, request_id, identity,
            )
        except Exception as exc:
            response = fail(
                request_id, "COMPILE_WORKFLOW_EXCEPTION", str(exc),
                {**identity, "exceptionType": type(exc).__name__,
                 "errorsVerified": False, "correlationVerified": False,
                 "nextAction": "Observe the original compile or write batch; do not trigger another compile."},
            )
        finally:
            if write_batch_id:
                in_flight_batches.discard(write_batch_id)
        try:
            console_evidence = await finish_console_evidence(self.dispatcher, self.server.state, console_evidence)
        except Exception as exc:
            console_evidence = {**console_evidence, "coverage": "partial", "gapReason": f"end_boundary_failed:{type(exc).__name__}"}
        response_identity = response.data if isinstance(response.data, dict) else response.error.detail if response.error is not None else {}
        resolved_operation_id = str(response_identity.get("compileOperationId") or compile_operation_id or self.server.state.compile.compile_operation_id or "")
        if resolved_operation_id:
            self.server.state.save_console_evidence("compileOperationId", resolved_operation_id, console_evidence)
        if response.data is not None:
            response.data["consoleEvidence"] = console_evidence
        elif response.error is not None:
            response.error.detail["consoleEvidence"] = console_evidence
        return response

    async def _safe_compile_and_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
        post_compile_delay_s: float = 3.0,
        attach_compile_request_id: str = "",
        write_batch_id: str = "",
        write_batch_created_at: int = 0,
        compile_operation_id: str = "",
        request_id: str = "",
        identity: dict | None = None,
    ) -> ToolResponse:
        """Robust compile wait with post-compile cooldown and double-verification.

        Workflow:
        1. Trigger compile via compile.request
        2. Wait for compile idle (events + poll fallback)
        3. Cooldown period to allow domain reload to complete
        4. Reconnect if disconnected by domain reload
        5. Query compile.errors.get for persistent errors
        6. Return success only if errors.total == 0

        This avoids false-positive "compile success" when domain reload resets
        error state in memory. Unity side now persists errors to disk.
        """
        import time

        workflow_started = time.monotonic()

        if write_batch_id:
            resume_task = getattr(self, "_write_batch_resume_task", None)
            if (not attach_compile_request_id
                    and getattr(self, "_write_batch_active_id", "") == write_batch_id
                    and resume_task is not asyncio.current_task()):
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "The original batch is already being dispatched or observed.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe unity_write_batch_status for the original batch."})
            get_write_batch = getattr(self.server.state, "get_write_batch", None)
            stored_batch = get_write_batch(write_batch_id) if callable(get_write_batch) else None
            if stored_batch is None:
                return fail(
                    request_id,
                    "WRITE_BATCH_NOT_FOUND",
                    "No write batch with this identity exists for the connected project.",
                    {
                        "writeBatchId": write_batch_id,
                        "dispatchAttempted": False,
                        "nextAction": "Register the saved changes for the connected project before compiling.",
                    },
                )
            stored_created_at = int(stored_batch.get("writeBatchCreatedAt") or 0)
            if write_batch_created_at and stored_created_at != int(write_batch_created_at):
                return fail(
                    request_id,
                    "WRITE_BATCH_TIMESTAMP_MISMATCH",
                    "The supplied writeBatchCreatedAt does not match the persisted batch.",
                    {
                        "writeBatchId": write_batch_id,
                        "expected": stored_created_at,
                        "actual": int(write_batch_created_at),
                        "nextAction": "Query unity_write_batch_status and keep the original batch identity.",
                    },
                )
            write_batch_created_at = stored_created_at
            identity["writeBatchCreatedAt"] = stored_created_at
            recorded_request = str(stored_batch.get("compileRequestId") or "")
            recorded_operation = str(stored_batch.get("compileOperationId") or "")
            if attach_compile_request_id and not recorded_operation:
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "The batch has no persisted compile operation to attach to.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe the original batch; do not attach another operation."})
            if attach_compile_request_id and not recorded_request:
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "The batch has no persisted compile request to attach to.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe the original batch; do not attach another request."})
            if attach_compile_request_id and recorded_request and attach_compile_request_id != recorded_request:
                return fail(request_id, "COMPILE_OPERATION_MISMATCH", "The requested compile identity differs from the persisted batch.", identity)
            if compile_operation_id and recorded_operation and compile_operation_id != recorded_operation:
                return fail(request_id, "COMPILE_OPERATION_MISMATCH", "The requested compile identity differs from the persisted batch.", identity)
            attach_compile_request_id = recorded_request or attach_compile_request_id
            compile_operation_id = recorded_operation or compile_operation_id
            terminal_snapshot = stored_batch.get("terminalSnapshot")
            if stored_batch.get("correlationVerified") and isinstance(terminal_snapshot, dict):
                outcome = str(stored_batch.get("outcome") or "unknown")
                return ok(
                    request_id,
                    {
                        **terminal_snapshot,
                        "status": "success" if outcome == "passed" else "failed",
                        "phase": str(terminal_snapshot.get("compilePhase") or outcome),
                        "terminal": True,
                        "compileRequestId": str(stored_batch.get("compileRequestId") or ""),
                        "compileOperationId": str(stored_batch.get("compileOperationId") or ""),
                        "writeBatchId": write_batch_id,
                        "writeBatchCreatedAt": stored_created_at,
                        "errorsVerified": True,
                        "correlationVerified": True,
                        "errorTotal": int(terminal_snapshot.get("errorCount") or 0),
                        "attachedToExistingCompile": False,
                        "reusedVerifiedBatch": True,
                    },
                    context=terminal_snapshot,
                )
            if stored_batch.get("disposition") or stored_batch.get("compileWhenEditMode") is not True:
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "This batch has no authorization for an automatic compile.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe or release the original batch through its existing manual workflow."})
            if str(stored_batch.get("status") or "") in {
                "verified", "failed", "canceled", "recovery_required"
            }:
                return fail(
                    request_id,
                    "COMPILE_CORRELATION_NOT_VERIFIED",
                    "The persisted write batch is terminal or requires recovery without correlated compile evidence.",
                    {
                        "writeBatchId": write_batch_id,
                        "writeBatchCreatedAt": stored_created_at,
                        "batchStatus": stored_batch.get("status"),
                        "outcome": stored_batch.get("outcome", "unknown"),
                        "errorsVerified": bool(stored_batch.get("errorsVerified")),
                        "correlationVerified": False,
                        "nextAction": "Query unity_write_batch_status for the original batch; do not trigger a replacement compile.",
                    },
                )
            if str(stored_batch.get("status") or "") in {"syncing", "compiling"} and not attach_compile_request_id:
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "The batch has a dispatch intent but no confirmed request identity.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe the original batch; do not start another compile."})

        def active_session_id() -> str:
            manager = getattr(self.server, "session_manager", None)
            session = getattr(manager, "active", None)
            return str(getattr(session, "session_id", "") or "")

        invocation_session_id = active_session_id()

        # Step 1: attach to an automatic/current compile, otherwise trigger one.
        attached_to_existing = False
        compile_state = self.server.state.compile
        initial_compile_phase = str(compile_state.phase or "").lower()
        editor_state = getattr(self.server.state, "editor", None)
        observed_active_compile = bool(
            initial_compile_phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"}
            or bool(getattr(editor_state, "is_compiling", False))
        )
        initial_session_id = invocation_session_id
        if (
            (attach_compile_request_id or observed_active_compile)
            and getattr(compile_state, "initial_session_id", "")
        ):
            initial_session_id = str(compile_state.initial_session_id)
        if attach_compile_request_id:
            if compile_operation_id and compile_state.compile_operation_id != compile_operation_id and not write_batch_id:
                return fail(
                    request_id,
                    "COMPILE_OPERATION_MISMATCH",
                    "The requested compileOperationId does not match the active Unity compilation.",
                    {"requestedCompileOperationId": compile_operation_id, "activeCompileOperationId": compile_state.compile_operation_id},
                )
            attached_to_existing = True
            compile_r = ok(
                request_id,
                {
                    "status": "attached",
                    "compileRequestId": attach_compile_request_id,
                    "phase": initial_compile_phase,
                    "attachmentSource": "tool_invocation_snapshot",
                },
            )
        elif observed_active_compile:
            if write_batch_id and (compile_state.write_batch_id != write_batch_id
                                   or not compile_state.compile_request_id
                                   or compile_state.write_batch_created_at < write_batch_created_at):
                return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                            "An active compile is not proven to cover this batch.",
                            {**identity, "dispatchAttempted": False,
                             "nextAction": "Observe the active compile and original batch; do not substitute its result."})
            attached_to_existing = True
            compile_r = ok(
                request_id,
                {
                    "status": "attached",
                    "compileRequestId": compile_state.compile_request_id,
                    "phase": initial_compile_phase,
                },
            )
        else:
            state_r = await self.dispatcher.call(new_id("req"), "resource.editorState", {})
            if not state_r.ok:
                return state_r
            if state_r.ok and state_r.data and bool(state_r.data.get("isCompiling", False)):
                if write_batch_id and (self.server.state.compile.write_batch_id != write_batch_id
                                       or not self.server.state.compile.compile_request_id
                                       or not self.server.state.compile.compile_operation_id
                                       or self.server.state.compile.write_batch_created_at < write_batch_created_at):
                    return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                                "A concurrent compile has no verified batch identity.",
                                {**identity, "dispatchAttempted": False})
                attached_to_existing = True
                compile_r = ok(
                    request_id,
                    {
                        "status": "attached",
                        "compileRequestId": self.server.state.compile.compile_request_id,
                        "phase": str(self.server.state.compile.phase or "compiling"),
                    },
                )
            else:
                suppress_before = bool(getattr(self, "_suppress_compile_console_evidence", False))
                self._suppress_compile_console_evidence = True
                try:
                    compile_r = await (
                        self.compile(
                            write_batch_id=write_batch_id,
                            write_batch_created_at=write_batch_created_at,
                        )
                        if write_batch_id
                        else self.compile()
                    )
                finally:
                    self._suppress_compile_console_evidence = suppress_before
                if not compile_r.ok and compile_r.error and compile_r.error.code == "EDITOR_BUSY":
                    verify_r = await self.dispatcher.call(new_id("req"), "resource.editorState", {})
                    if verify_r.ok and verify_r.data and bool(verify_r.data.get("isCompiling", False)):
                        current = self.server.state.compile
                        if write_batch_id and (current.write_batch_id != write_batch_id
                                               or not current.compile_request_id
                                               or not current.compile_operation_id
                                               or current.write_batch_created_at < write_batch_created_at):
                            return fail(request_id, "COMPILE_RECOVERY_REQUIRED",
                                        "A concurrent compile has no verified batch identity.",
                                        {**identity, "dispatchAttempted": False,
                                         "nextAction": "Observe the original batch and active compile; do not substitute its result."})
                        attached_to_existing = True
                        compile_r = ok(
                            request_id,
                            {
                                "status": "attached",
                                "compileRequestId": self.server.state.compile.compile_request_id,
                                "phase": str(self.server.state.compile.phase or "compiling"),
                            },
                        )
                if not compile_r.ok:
                    return compile_r

        compile_request_id = (
            compile_r.data.get("compileRequestId", "") if compile_r.data else ""
        )

        expected_compile_operation_id = str(
            compile_operation_id
            or (compile_r.data or {}).get("compileOperationId")
            or self.server.state.compile.compile_operation_id
            or ""
        )
        identity.update(
            compileRequestId=compile_request_id,
            compileOperationId=expected_compile_operation_id,
        )
        # Keep the attached identity before waiting; another batch can become current.
        # Step 2: Wait for compile idle
        wait_r = await self.compile_wait(
            timeout_s=max(0.1, timeout_s - (time.monotonic() - workflow_started)),
            poll_interval_s=poll_interval_s,
            prefer_events=prefer_events,
        )
        if write_batch_id:
            persisted_batch = self.server.state.get_write_batch(write_batch_id)
            snapshot = persisted_batch.get("terminalSnapshot") if persisted_batch else None
            if (persisted_batch and persisted_batch.get("correlationVerified")
                    and isinstance(snapshot, dict)
                    and persisted_batch.get("compileRequestId") == compile_request_id
                    and persisted_batch.get("compileOperationId") == expected_compile_operation_id):
                outcome = persisted_batch["outcome"]
                return ok(request_id, {**snapshot,
                    "status": "success" if outcome == "passed" else "failed",
                    "phase": snapshot["compilePhase"], "terminal": True,
                    "errorsVerified": True, "correlationVerified": True,
                    "compileRequestId": compile_request_id,
                    "compileOperationId": expected_compile_operation_id,
                    "writeBatchId": write_batch_id,
                    "writeBatchCreatedAt": write_batch_created_at,
                    "errorTotal": snapshot.get("errorCount", 0),
                    "attachedToExistingCompile": attached_to_existing,
                    "reusedVerifiedBatch": True}, context=snapshot)

        # A just-finished compilation can briefly expose the pre-reload
        # hasCompileErrors value.  The safe workflow must still perform its
        # post-reload persistent error query before deciding that compilation
        # failed.  Transport and other wait failures remain terminal here.
        wait_reported_compile_error = bool(
            not wait_r.ok
            and wait_r.error
            and wait_r.error.code == "COMPILE_ERROR"
        )
        wait_interrupted_by_reload = bool(
            not wait_r.ok
            and wait_r.error
            and wait_r.error.code in {"UNITY_NOT_CONNECTED", "COMMAND_TIMEOUT"}
            and (
                not self.server.is_ready()
                or str(self.server.state.compile.phase or "").lower() in {"domain_reload", "verifying"}
            )
        )
        if not wait_r.ok and not wait_reported_compile_error and not wait_interrupted_by_reload:
            return wait_r

        if wait_r.data and wait_r.data.get("status") == "timeout":
            return fail(
                request_id,
                "COMPILE_TIMEOUT",
                "编译等待超时",
                {
                    "compileRequestId": compile_request_id,
                    "compileWaitResult": wait_r.data,
                },
            )

        # Step 3: Cooldown period to allow domain reload to complete
        # This is critical: compile may finish just before domain reload starts
        wait_detail = (
            wait_r.data
            if wait_r.data
            else (wait_r.error.detail if wait_r.error else {})
        )
        remaining_after_wait = max(0.0, timeout_s - (time.monotonic() - workflow_started))
        actual_delay = min(post_compile_delay_s, remaining_after_wait * 0.1)
        if actual_delay > 0:
            await asyncio.sleep(actual_delay)

        # Step 4: If disconnected (likely domain reload), wait for reconnect
        reconnected_after_reload = False
        if not self.server.is_ready():
            reconnect_deadline = time.monotonic() + min(60.0, remaining_after_wait)
            reconnected = False
            while time.monotonic() < reconnect_deadline:
                if self.server.is_ready():
                    reconnected = True
                    reconnected_after_reload = True
                    break
                await asyncio.sleep(1.0)
            if not reconnected:
                return fail(
                    request_id,
                    "RECONNECT_FAILED",
                    "编译完成后 Unity 未能重连（可能 Domain Reload 卡住）",
                    {"compileRequestId": compile_request_id},
                )

        if wait_interrupted_by_reload:
            remaining_wait = max(0.0, timeout_s - (time.monotonic() - workflow_started))
            if remaining_wait <= 0:
                return fail(request_id, "COMPILE_TIMEOUT", "Compilation observation exhausted its original deadline.", identity)
            wait_r = await self.compile_wait(
                timeout_s=remaining_wait,
                poll_interval_s=poll_interval_s,
                prefer_events=prefer_events,
            )
            if not wait_r.ok and not (
                wait_r.error and wait_r.error.code == "COMPILE_ERROR"
            ):
                return wait_r
            wait_reported_compile_error = wait_reported_compile_error or bool(
                not wait_r.ok and wait_r.error and wait_r.error.code == "COMPILE_ERROR"
            )

        # Step 5: Double-verify compile errors (reads from disk on Unity side)
        persisted_batch = self.server.state.get_write_batch(write_batch_id) if write_batch_id else None
        if persisted_batch and persisted_batch.get("correlationVerified"):
            snapshot = persisted_batch["terminalSnapshot"]
            if (persisted_batch.get("compileRequestId") != compile_request_id
                    or persisted_batch.get("compileOperationId") != expected_compile_operation_id):
                return fail(request_id, "COMPILE_OPERATION_MISMATCH",
                            "Persisted terminal belongs to another compile identity.", identity)
            outcome = persisted_batch["outcome"]
            return ok(request_id, {**snapshot,
                "status": "success" if outcome == "passed" else "failed",
                "phase": snapshot["compilePhase"], "terminal": True, "errorsVerified": True,
                "correlationVerified": True, "compileRequestId": compile_request_id,
                "compileOperationId": expected_compile_operation_id,
                "writeBatchId": write_batch_id, "writeBatchCreatedAt": write_batch_created_at,
                "errorTotal": snapshot.get("errorCount", 0),
                "attachedToExistingCompile": attached_to_existing,
                "reusedVerifiedBatch": True}, context=snapshot)
        errors_r = await self.compile_errors(compile_request_id)
        if not errors_r.ok:
            return fail(
                request_id, "COMPILE_ERRORS_NOT_VERIFIED",
                "The compile error query failed; the compile outcome is not verified.",
                {
                    **self._compile_diagnostics(),
                    "compileRequestId": compile_request_id,
                    "compileOperationId": expected_compile_operation_id,
                    "writeBatchId": write_batch_id,
                    "errorQuery": asdict(errors_r),
                    "nextAction": "Observe the original compile identity; do not trigger a replacement compile.",
                },
            )
        terminal_execution = self.server.state.execution_state(stale_after_ms=CONFIG.context_stale_ms)
        correlation_verified = bool(
            write_batch_id
            and terminal_execution.get("writeBatchId") == write_batch_id
            and compile_request_id
            and terminal_execution.get("compileRequestId") == compile_request_id
            and terminal_execution.get("compileOperationId") == expected_compile_operation_id
            and expected_compile_operation_id
            and terminal_execution.get("terminal")
            and terminal_execution.get("errorsVerified")
            and terminal_execution.get("lastCompileVerifiedAt", 0) >= int(write_batch_created_at) > 0
        )
        if write_batch_id and not correlation_verified:
            return fail(
                request_id, "COMPILE_CORRELATION_NOT_VERIFIED",
                "The terminal snapshot does not verify the requested write batch.",
                {
                    "compileRequestId": compile_request_id,
                    "compileOperationId": expected_compile_operation_id,
                    "writeBatchId": write_batch_id,
                    "writeBatchCreatedAt": write_batch_created_at,
                    "correlationVerified": False,
                    "executionState": terminal_execution,
                    "nextAction": "Query unity_write_batch_status for the original writeBatchId.",
                },
            )
        if (not isinstance(errors_r.data, dict)
                or type(errors_r.data.get("total")) is not int or errors_r.data["total"] < 0):
            return fail(request_id, "COMPILE_ERRORS_NOT_VERIFIED",
                        "The error query returned no verified error count.",
                        {**identity, "executionState": terminal_execution})
        if expected_compile_operation_id and terminal_execution.get("compileOperationId") != expected_compile_operation_id:
            return fail(request_id, "COMPILE_OPERATION_MISMATCH",
                        "The terminal snapshot belongs to another compile operation.",
                        {**identity, "executionState": terminal_execution,
                         "nextAction": "Observe the original compile identity; do not recompile."})
        if (not terminal_execution.get("terminal") or not terminal_execution.get("errorsVerified")
                or (compile_request_id and terminal_execution.get("compileRequestId") != compile_request_id)):
            return fail(request_id, "COMPILE_ERRORS_NOT_VERIFIED",
                        "Unity has not published a verified terminal for this compile request.",
                        {**identity, "executionState": terminal_execution,
                         "nextAction": "Observe the original compile request; do not recompile."})
        if errors_r.data["total"] == 0 and terminal_execution.get("compilePhase") != "completed":
            return fail(request_id, "COMPILE_TERMINAL_CONFLICT",
                        "The zero-error query conflicts with Unity's terminal compilation phase.",
                        {**identity, "executionState": terminal_execution})
        if errors_r.data:
            error_total = errors_r.data["total"]
            if error_total > 0:
                return ok(
                    request_id,
                    {
                        "status": "failed",
                        "phase": "failed",
                        "terminal": True,
                        "compileRequestId": compile_request_id,
                        "compileOperationId": expected_compile_operation_id,
                        "writeBatchId": write_batch_id,
                        "writeBatchCreatedAt": write_batch_created_at,
                        "errorsVerified": terminal_execution.get("errorsVerified", False),
                        "correlationVerified": correlation_verified,
                        "errors": errors_r.data.get("errors", []),
                        "errorTotal": error_total,
                        "source": errors_r.data.get("source", "unknown"),
                        "mode": errors_r.data.get("mode", "unknown"),
                        "executionState": terminal_execution,
                    },
                    context=terminal_execution,
                )

        # Assemble both outcomes from the same snapshot validated above.
        final_session_id = active_session_id()
        session_changed_during_compile = bool(
            initial_session_id
            and final_session_id
            and initial_session_id != final_session_id
        )
        reconnected_after_reload = reconnected_after_reload or session_changed_during_compile
        return ok(
            request_id,
            {
                **terminal_execution,
                "status": "success",
                "compileRequestId": compile_request_id,
                "compileOperationId": expected_compile_operation_id,
                "writeBatchId": write_batch_id or terminal_execution.get("writeBatchId", ""),
                "writeBatchCreatedAt": write_batch_created_at or terminal_execution.get("writeBatchCreatedAt", 0),
                "correlationVerified": correlation_verified,
                "postCompileDelayS": actual_delay,
                "reconnectedAfterReload": reconnected_after_reload,
                "sessionChangedDuringCompile": session_changed_during_compile,
                "initialSessionId": initial_session_id,
                "finalSessionId": final_session_id,
                "waitInterruptedByReload": wait_interrupted_by_reload,
                "errorsVerified": bool(terminal_execution.get("errorsVerified")),
                "errorTotal": 0,
                "attachedToExistingCompile": attached_to_existing,
                "waitReportedCompileError": wait_reported_compile_error,
            },
            context=terminal_execution,
        )
