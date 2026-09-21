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
_PREDISPATCH_STALE_ERROR = "Unity Editor context is stale, unknown, or recovering after Domain Reload."


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


def _serialized_property_value(value: object) -> str:
    if isinstance(value, str):
        return value
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return ""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _normalize_component_property_writes(properties: dict) -> list[dict[str, str]]:
    if not isinstance(properties, dict) or not properties:
        raise ValueError("properties must be a non-empty object keyed by SerializedProperty path.")
    return [
        {"propertyPath": str(path), "value": _serialized_property_value(value)}
        for path, value in properties.items()
    ]


def _require_mutation_success(response: ToolResponse, tool_name: str) -> ToolResponse:
    if not response.ok:
        return response
    data = response.data if isinstance(response.data, dict) else {}
    if data.get("ok") is True and data.get("verified") is True:
        return response
    return fail(
        response.request_id,
        "RESULT_CONTRACT_VIOLATION",
        f"{tool_name} returned an outer success without verified data.ok=true.",
        {"tool": tool_name, "bridgeData": data},
        context=response.context,
        timing=response.timing,
    )


def _with_read_only_observation_evidence(response: ToolResponse) -> ToolResponse:
    """Keep Unity's state evidence verbatim and make an absent observation explicit.

    Dependency/reference queries are read-only *requests*, but an isolated Prefab
    load can still expose a Unity-side state change.  Older Bridge payloads did
    not carry the observation fields for every dependency mode, so never turn a
    missing field into ``false`` merely because this tool did not request a write.
    """
    if not response.ok or not isinstance(response.data, dict):
        return response
    data = dict(response.data)
    has_read_only = isinstance(data.get("readOnly"), bool)
    has_changed_state = isinstance(data.get("changedEditorState"), bool)
    if has_read_only and has_changed_state:
        data.setdefault("changeStateEvidence", "unityPayload")
    else:
        # ``null`` means the Bridge did not make an observation.  It is distinct
        # from a verified ``false`` and keeps old Unity packages wire-compatible.
        data.setdefault("readOnly", None)
        data.setdefault("changedEditorState", None)
        data.setdefault("changeStateEvidence", "unknown")
    return ToolResponse(
        ok=response.ok,
        data=data,
        error=response.error,
        request_id=response.request_id,
        timestamp=response.timestamp,
        context=response.context,
        timing=response.timing,
    )


class ResourceDomainService:
    _CODE_WRITE_EXTENSIONS = {".cs", ".asmdef", ".asmref", ".rsp"}

    def _write_batch_waiting_diagnostics(self, batch: dict, execution: dict | None = None) -> dict:
        """Derive a non-mutating wait diagnosis from the current authoritative state."""
        execution = execution or self._current_execution_state()
        terminal = bool(batch.get("terminal"))
        now = now_ms()
        created_at = int(batch.get("writeBatchCreatedAt") or 0)
        last_progress_at = int(execution.get("lastProgressAt") or created_at)
        pending_age_ms = max(0, now - created_at) if created_at and not terminal else 0
        progress_age_ms = max(0, now - last_progress_at) if last_progress_at and not terminal else 0
        phase = str(execution.get("compilePhase") or "").lower()

        if terminal:
            reason = "none"
        elif str(batch.get("status") or "") == "recovery_required":
            reason = "recovery_required"
        elif execution.get("unityConnected") is False:
            reason = "disconnected"
        elif phase in {"domain_reload", "verifying"}:
            reason = "reload"
        # A raw play flag from a stale/recovering snapshot is not authorization
        # to tell a caller to leave PlayMode.
        elif execution.get("authoritative") and str(execution.get("playModeState") or "") in {"play", "pause"}:
            reason = "playmode"
        elif not execution.get("authoritative") or execution.get("isStale"):
            reason = "stale"
        elif phase in {"queued", "compiling", "compiler_finished"} or bool(execution.get("isCompiling")):
            reason = "compile_in_progress"
        else:
            reason = "none"

        actions = {
            "playmode": "Leave PlayMode only after user confirmation; UPilot will resume the same authorized batch after authoritative EditMode.",
            "reload": "Wait for the same batch to recover; do not switch PlayMode.",
            "disconnected": "Reconnect the intended Unity project and observe the same batch; do not recompile.",
            "stale": "Wait for the same authorized batch to receive a fresh authoritative Editor snapshot.",
            "recovery_required": "Observe the original compile identity; do not trigger a replacement compile for this batch.",
            "compile_in_progress": "Continue observing the current compile; do not submit a second compile.",
        }
        return {
            "waitingReason": reason,
            "pendingAgeMs": pending_age_ms,
            "lastProgressAt": int(execution.get("lastProgressAt") or 0),
            "attentionRequired": bool(not terminal and progress_age_ms > 30000),
            "waitingNextAction": actions.get(reason, "No compile wait is currently required."),
        }

    def _current_execution_state(self) -> dict:
        server = getattr(self, "server", None)
        state = getattr(server, "state", None)
        factory = getattr(state, "execution_state", None)
        if callable(factory):
            return factory(stale_after_ms=CONFIG.context_stale_ms)
        compile_state = getattr(state, "compile", None)
        editor_state = getattr(state, "editor", None)
        return {
            "compilePhase": str(getattr(compile_state, "phase", "idle") or "idle"),
            "isCompiling": bool(getattr(editor_state, "is_compiling", False)),
            "authoritative": False,
            "isStale": True,
            "playModeState": "unknown",
            "snapshotId": "",
        }

    def _active_project_root(self) -> Path | None:
        session = self.server.session_manager.active
        if session and session.project_path:
            return Path(session.project_path).expanduser().resolve()
        return None

    def _reject_write_if_unapproved(self, request_id: str, tool_name: str) -> ToolResponse | None:
        if CONFIG.write_access_approved:
            return None
        return fail(
            request_id,
            "WRITE_ACCESS_NOT_APPROVED",
            "UPilot is in safe mode. Enable project write access in the Unity UPilot first setup or .upilot/config.json before using this tool.",
            {"tool": tool_name, "configKey": "safety.writeAccessApproved"},
        )

    def _code_write_roots(self, project_root: Path) -> list[Path]:
        roots = [project_root]
        manifest = project_root / "Packages" / "manifest.json"
        try:
            dependencies = json.loads(manifest.read_text(encoding="utf-8-sig")).get("dependencies", {})
            for value in dependencies.values():
                if isinstance(value, str) and value.startswith("file:"):
                    roots.append((manifest.parent / value[5:]).resolve())
        except (OSError, ValueError, json.JSONDecodeError):
            pass
        return list(dict.fromkeys(roots))

    async def write_batch_status(self, write_batch_id: str) -> ToolResponse:
        request_id = new_id("req")
        batch = self.server.state.get_write_batch(write_batch_id)
        if batch is None:
            return fail(request_id, "WRITE_BATCH_NOT_FOUND", "No batch exists in this project.", {"writeBatchId": write_batch_id})
        execution = self._current_execution_state()
        waiting = self._write_batch_waiting_diagnostics(batch, execution)
        return ok(request_id, {
            **batch,
            **waiting,
            "nextAction": waiting["waitingNextAction"],
            "executionState": execution,
        }, context=execution)

    async def write_batch_register(
        self,
        paths: list[str],
        compile_when_edit_mode: bool,
        deleted_paths: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")

        rejected = self._reject_write_if_unapproved(request_id, "unity_write_batch_register")
        if rejected is not None:
            return rejected
        project_root = self._active_project_root()
        if project_root is None:
            return fail(request_id, "UNITY_NOT_CONNECTED", "No active Unity project is connected.")
        if (
            not isinstance(paths, list)
            or (deleted_paths is not None and not isinstance(deleted_paths, list))
            or any(not isinstance(path, str) or not path.strip() for path in [*paths, *(deleted_paths or [])])
            or not (paths or deleted_paths)
        ):
            return fail(request_id, "INVALID_WRITE_BATCH", "Provide paths and/or deletedPaths arrays of non-empty assembly-related paths.")

        roots = self._code_write_roots(project_root)
        changes: dict[str, dict] = {}
        newest_mtime = 0
        has_code_change = False
        for kind, entries in (("write", paths), ("delete", deleted_paths or [])):
            for raw in entries:
                try:
                    original = Path(raw).expanduser()
                    if not original.is_absolute():
                        original = project_root / original
                    candidate = original.resolve()
                    is_code_change = candidate.suffix.lower() in self._CODE_WRITE_EXTENSIONS
                    if kind == "write" and not is_code_change:
                        return fail(request_id, "UNSUPPORTED_WRITE_BATCH_FILE", f"Unsupported assembly-related file: {raw}")
                    if not any(candidate == root or root in candidate.parents for root in roots):
                        return fail(request_id, "WRITE_BATCH_PATH_OUTSIDE_PROJECT", f"Path is outside the Unity project and resolved local UPM roots: {raw}")
                    normalized_path = str(candidate)
                    key = os.path.normcase(normalized_path)
                    if key in changes and changes[key]["kind"] != kind:
                        return fail(request_id, "WRITE_BATCH_PATH_CONFLICT", f"Path is both written and deleted: {raw}")
                    if kind == "delete":
                        if os.path.lexists(original) or candidate.exists():
                            return fail(request_id, "WRITE_BATCH_DELETED_PATH_EXISTS", f"Deleted path still exists: {raw}")
                        content_hash = ""
                    else:
                        if not candidate.is_file():
                            return fail(request_id, "WRITE_BATCH_FILE_NOT_FOUND", f"Write batch file does not exist: {candidate}")
                        before = candidate.stat()
                        content_hash = hashlib.sha256(candidate.read_bytes()).hexdigest()
                        after = candidate.stat()
                        if (before.st_mtime_ns, before.st_size) != (after.st_mtime_ns, after.st_size):
                            return fail(request_id, "WRITE_BATCH_FILE_CHANGED", f"File changed while registering: {raw}")
                        newest_mtime = max(newest_mtime, int(after.st_mtime_ns // 1_000_000))
                    changes[key] = {"path": normalized_path, "kind": kind, "contentSha256": content_hash}
                    has_code_change = has_code_change or is_code_change
                except (OSError, ValueError, RuntimeError) as exc:
                    return fail(request_id, "WRITE_BATCH_PATH_INVALID", f"Could not inspect {raw}: {exc}")

        if not has_code_change:
            return fail(
                request_id,
                "WRITE_BATCH_CODE_CHANGE_REQUIRED",
                "Asset deletions may accompany an assembly-related change but cannot form a compile batch by themselves.",
                {"sideEffectsMayHaveOccurred": False},
            )

        self.server.state.configure_project(str(project_root))
        batch = self.server.state.register_write_batch(
            [item["path"] for item in changes.values() if item["kind"] == "write"],
            created_at=max(now_ms(), newest_mtime),
            files_sha256="",
            compile_when_edit_mode=compile_when_edit_mode,
            changes=list(changes.values()),
        )
        execution = self._current_execution_state()
        can_compile = bool(
            execution.get("authoritative")
            and not execution.get("isStale")
            and execution.get("playModeState") == "edit"
        )
        if compile_when_edit_mode and can_compile:
            self._schedule_write_batch_resume()
            status = "queued"
            next_action = "Poll unity_mcp_status until correlationVerified=true or the batch fails."
        elif compile_when_edit_mode:
            self.server.state.mark_write_batch(batch["writeBatchId"], "deferred")
            status = "deferred"
            wait_reason = self._write_batch_waiting_diagnostics({**batch, "status": status}, execution)["waitingReason"]
            next_action = (
                "Leave PlayMode when ready; UPilot will resume this authorized batch after Unity publishes authoritative EditMode."
                if wait_reason == "playmode"
                else "Wait for the same authorized batch to recover; do not change PlayMode or submit another compile."
            )
        else:
            status = "registered"
            next_action = "Call unity_sync_after_disk_write with this writeBatchId after authoritative EditMode is available."
        response_batch = {**batch, "status": status, "terminal": False}
        return ok(
            request_id,
            {
                **response_batch,
                **self._write_batch_waiting_diagnostics(response_batch, execution),
                "nextAction": next_action,
                "executionState": execution,
            },
            context=execution,
        )

    def _schedule_write_batch_resume(self) -> None:
        task = getattr(self, "_write_batch_resume_task", None)
        if task is not None and not task.done():
            return
        self._write_batch_resume_task = asyncio.create_task(self._resume_pending_write_batches())

    async def _on_editor_execution_state(self, execution: dict) -> None:
        if not execution.get("authoritative") or execution.get("playModeState") != "edit":
            return
        if self.server.state.pending_write_batches():
            self._schedule_write_batch_resume()

    async def _resume_pending_write_batches(self) -> None:
        await asyncio.sleep(0.5)
        execution = self.server.state.execution_state(stale_after_ms=CONFIG.context_stale_ms)
        if not execution.get("authoritative") or execution.get("playModeState") != "edit":
            return
        for batch in self.server.state.pending_write_batches():
            if not batch["compileWhenEditMode"]:
                continue
            if batch["status"] == "recovery_required":
                if not self.server.state.defer_write_batch_after_predispatch_failure(
                    str(batch["writeBatchId"]), _PREDISPATCH_STALE_ERROR
                ):
                    continue
                batch = self.server.state.get_write_batch(str(batch["writeBatchId"])) or batch
            # A write registered after Unity had already begun an automatic
            # compile cannot borrow that compile's result.  Leave this batch
            # pending; the next fresh EditMode execution snapshot schedules
            # the one already-authorized, request-scoped compile.  This branch
            # neither marks recovery-required nor starts a second compile.
            if (
                str(execution.get("compileOrigin") or "") == "unity_auto"
                and (
                    bool(execution.get("isCompiling"))
                    or str(execution.get("compilePhase") or "") in {
                        "queued", "compiling", "compiler_finished", "domain_reload", "verifying"
                    }
                )
            ):
                continue
            batch_id = str(batch["writeBatchId"])
            stored = self.server.state.get_write_batch(batch_id)
            if stored and stored.get("terminal") and stored.get("correlationVerified"):
                continue
            self.server.state.mark_write_batch(batch_id, "syncing")
            self.server.state.mark_write_batch(batch_id, "compiling")
            result = await self.safe_compile_and_wait(
                timeout_s=600,
                poll_interval_s=1.0,
                prefer_events=True,
                post_compile_delay_s=1.0,
                write_batch_id=batch_id,
                write_batch_created_at=int(batch["writeBatchCreatedAt"]),
            )
            compile_operation_id = str(self.server.state.compile.compile_operation_id or "")
            result_data = result.data or {}
            phase = str(result_data.get("phase") or result_data.get("status") or "").lower()
            correlation_verified = bool(result_data.get("correlationVerified"))
            stored = self.server.state.get_write_batch(batch_id)
            persisted_terminal = bool(
                stored
                and stored.get("terminal")
                and stored.get("correlationVerified")
                and stored.get("outcome") in {"passed", "failed"}
            )
            if not (result.ok and correlation_verified and phase in {"completed", "failed"} and persisted_terminal):
                error_code = str(result.error.code if result.error else "")
                error_detail = result.error.detail if result.error else {}
                if (
                    error_detail.get("dispatchAttempted") is False
                    and error_code in {
                        "COMMAND_TIMEOUT",
                        "EDITOR_BUSY",
                        "EDITOR_CONTEXT_NOT_READY",
                        "EDITOR_IN_PLAY_MODE",
                        "UNITY_NOT_CONNECTED",
                    }
                ):
                    self.server.state.mark_write_batch(batch_id, "deferred")
                    continue
                message = (
                    result.error.message
                    if result.error
                    else "Compilation returned without a persisted correlated terminal snapshot."
                )
                self.server.state.mark_write_batch(
                    batch_id,
                    "recovery_required",
                    compile_operation_id=compile_operation_id,
                    error=message,
                )

    async def asset_find(self, query: str, asset_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"query": query}
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.find", payload)

    async def texture_importer_get(self, asset_path: str) -> ToolResponse:
        return await self.dispatcher.call(new_id("req"), "texture.importerGet", {"assetPath": asset_path})

    async def prefab_patch(
        self, asset_path: str, component_type: str, properties: list[dict],
        hierarchy_path: str = ".", dry_run: bool = True, confirm_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        if not dry_run:
            rejected = self._reject_write_if_unapproved(request_id, "unity_prefab_patch")
            if rejected is not None:
                return rejected
            if not confirm_token:
                return fail(request_id, "PREFAB_CONFIRM_TOKEN_REQUIRED", "Preview and obtain explicit approval before applying.")
            ready = await self.ensure_ready(timeout_s=30)
            if not ready.ok or not (ready.data or {}).get("ready"):
                return fail(request_id, "PREFAB_EDITOR_NOT_READY", "Wait for authoritative Editor readiness before applying.")
        response = await self.dispatcher.call(request_id, "prefab.patch", {
            "assetPath": asset_path, "componentType": component_type, "hierarchyPath": hierarchy_path,
            "properties": properties, "dryRun": dry_run, "confirmToken": confirm_token,
        }, timeout_ms=60000)
        if response.ok and (response.data or {}).get("error"):
            return fail(request_id, "PREFAB_PATCH_FAILED", response.data["error"], response.data)
        if response.ok and not dry_run and (response.data or {}).get("persistenceVerified") is not True:
            return fail(request_id, "PREFAB_PERSISTENCE_UNVERIFIED", "Prefab persistence was not verified; do not retry automatically.", response.data or {})
        return response

    async def scene_summary(self, max_nodes: int = 2000, max_milliseconds: int = 100, max_examples: int = 12) -> ToolResponse:
        if not 1 <= max_nodes <= 10000 or not 1 <= max_milliseconds <= 1000 or not 0 <= max_examples <= 50:
            return fail(new_id("req"), "SCENE_SUMMARY_BUDGET_INVALID", "Use maxNodes=1..10000, maxMilliseconds=1..1000 and maxExamples=0..50.")
        return await self.dispatcher.call(new_id("req"), "resource.sceneSummary", {
            "maxNodes": max_nodes, "maxMilliseconds": max_milliseconds, "maxExamples": max_examples,
        })

    async def asset_dependencies(
        self,
        asset_path: str = "",
        recursive: bool = True,
        evidence_mode: str = "file",
        runtime_boundary: str = "none",
        direction: str = "forward",
        reference_query: dict | None = None,
        scope: list[str] | None = None,
        max_nodes: int = 500,
        time_budget_ms: int = 5000,
        continuation_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(recursive, bool):
            return fail(request_id, "DEPENDENCY_RECURSIVE_INVALID", "recursive must be a boolean.",
                        {"field": "recursive", "sideEffectsMayHaveOccurred": False})
        if not isinstance(continuation_token, str):
            return fail(request_id, "REFERENCE_QUERY_INVALID", "continuationToken must be a string.",
                        {"field": "continuationToken", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if not isinstance(evidence_mode, str):
            return fail(request_id, "DEPENDENCY_EVIDENCE_MODE_INVALID", "evidenceMode must be a string.",
                        {"field": "evidenceMode", "value": evidence_mode, "sideEffectsMayHaveOccurred": False})
        if not isinstance(runtime_boundary, str):
            return fail(request_id, "DEPENDENCY_RUNTIME_BOUNDARY_INVALID", "runtimeBoundary must be a string.",
                        {"field": "runtimeBoundary", "value": runtime_boundary, "sideEffectsMayHaveOccurred": False})
        if not isinstance(direction, str):
            return fail(request_id, "DEPENDENCY_DIRECTION_INVALID", "direction must be a string.",
                        {"field": "direction", "value": direction, "sideEffectsMayHaveOccurred": False})
        evidence_key = str(evidence_mode or "").strip().lower()
        boundary_key = str(runtime_boundary or "").strip()
        direction_key = str(direction or "").strip().lower()
        if scope is not None and not isinstance(scope, list):
            return fail(request_id, "REFERENCE_SCOPE_INVALID", "scope must be an array of project-relative Assets/ folders.",
                        {"field": "scope", "sideEffectsMayHaveOccurred": False})
        normalized_scope = list(scope or [])
        if evidence_key not in {"file", "object"}:
            return fail(request_id, "DEPENDENCY_EVIDENCE_MODE_INVALID", "evidenceMode must be file or object.",
                        {"field": "evidenceMode", "value": evidence_mode, "sideEffectsMayHaveOccurred": False})
        if boundary_key not in {"none", "ExcludeAssetsEditor"}:
            return fail(request_id, "DEPENDENCY_RUNTIME_BOUNDARY_INVALID", "runtimeBoundary must be none or ExcludeAssetsEditor.",
                        {"field": "runtimeBoundary", "value": runtime_boundary, "sideEffectsMayHaveOccurred": False})
        if direction_key not in {"forward", "reverse"}:
            return fail(request_id, "DEPENDENCY_DIRECTION_INVALID", "direction must be forward or reverse.",
                        {"field": "direction", "value": direction, "sideEffectsMayHaveOccurred": False})
        if not isinstance(max_nodes, int) or isinstance(max_nodes, bool) or not 1 <= max_nodes <= 5000:
            return fail(request_id, "DEPENDENCY_NODE_BUDGET_INVALID", "maxNodes must be an integer from 1 through 5000.",
                        {"field": "maxNodes", "sideEffectsMayHaveOccurred": False})
        if not isinstance(time_budget_ms, int) or isinstance(time_budget_ms, bool) or not 1 <= time_budget_ms <= 30000:
            return fail(request_id, "DEPENDENCY_TIME_BUDGET_INVALID", "timeBudgetMs must be an integer from 1 through 30000.",
                        {"field": "timeBudgetMs", "sideEffectsMayHaveOccurred": False})
        if any(not self._is_project_asset_folder(path) for path in normalized_scope):
            return fail(request_id, "REFERENCE_SCOPE_INVALID", "scope must contain project-relative Assets/ folders only.",
                        {"field": "scope", "sideEffectsMayHaveOccurred": False})
        # Scope and literal property paths identify a query, not a traversal order.
        # Canonicalize them before Unity derives the cursor signature so a caller can
        # safely resume a page with the same semantic query.
        normalized_scope = sorted({path.rstrip("/") if path != "Assets/" else path for path in normalized_scope})

        normalized_query, query_error = self._normalize_dependency_reference_query(reference_query)
        if query_error is not None:
            return fail(request_id, "REFERENCE_QUERY_INVALID", query_error,
                        {"field": "referenceQuery", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if direction_key == "forward":
            if not isinstance(asset_path, str) or not asset_path.strip():
                return fail(request_id, "ASSET_PATH_REQUIRED", "assetPath is required for forward dependency queries.",
                            {"field": "assetPath", "sideEffectsMayHaveOccurred": False})
            if normalized_query is not None or normalized_scope:
                return fail(request_id, "REFERENCE_QUERY_INVALID", "referenceQuery and scope are only valid for reverse queries.",
                            {"field": "referenceQuery", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
            if continuation_token:
                return fail(request_id, "REFERENCE_QUERY_INVALID", "continuationToken is only valid for reverse queries.",
                            {"field": "continuationToken", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        else:
            if not isinstance(asset_path, str) or asset_path:
                return fail(request_id, "REFERENCE_QUERY_INVALID", "assetPath must be empty for reverse queries.",
                            {"field": "assetPath", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
            if evidence_key != "object":
                return fail(request_id, "DEPENDENCY_EVIDENCE_MODE_INVALID", "Reverse queries require evidenceMode=object.",
                            {"field": "evidenceMode", "sideEffectsMayHaveOccurred": False})
            if normalized_query is None or not normalized_scope:
                return fail(request_id, "REFERENCE_QUERY_INVALID", "Reverse queries require one referenceQuery and a non-empty scope.",
                            {"field": "referenceQuery", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if boundary_key != "none" and evidence_key != "object":
            return fail(request_id, "DEPENDENCY_RUNTIME_BOUNDARY_INVALID", "runtimeBoundary is only available for object evidence.",
                        {"field": "runtimeBoundary", "sideEffectsMayHaveOccurred": False})

        payload = {"assetPath": asset_path, "recursive": recursive}
        if evidence_key != "file" or boundary_key != "none" or direction_key != "forward" or normalized_query is not None or normalized_scope or max_nodes != 500 or time_budget_ms != 5000 or continuation_token:
            payload.update({
                "evidenceMode": evidence_key,
                "runtimeBoundary": boundary_key,
                "direction": direction_key,
                "referenceQuery": normalized_query,
                "scope": normalized_scope,
                "maxNodes": max_nodes,
                "timeBudgetMs": time_budget_ms,
                "continuationToken": continuation_token,
            })
        return _with_read_only_observation_evidence(
            await self.dispatcher.call(request_id, "asset.dependencies", payload)
        )

    @staticmethod
    def _normalize_dependency_reference_query(reference_query: object) -> tuple[dict | None, str | None]:
        if reference_query is None:
            return None, None
        if not isinstance(reference_query, dict):
            return None, "referenceQuery must be an object."
        kind = reference_query.get("kind")
        if not isinstance(kind, str) or kind not in {"guid", "object", "stringLiteral"}:
            return None, "referenceQuery.kind must be guid, object, or stringLiteral."
        allowed = {
            "guid": {"kind", "value"},
            "object": {"kind", "guid", "localFileId"},
            "stringLiteral": {"kind", "value", "propertyPaths"},
        }[kind]
        if set(reference_query) != allowed:
            return None, "referenceQuery has fields incompatible with its kind."
        if kind == "guid":
            value = reference_query.get("value")
            if not isinstance(value, str) or not value.strip():
                return None, "referenceQuery.value must be a non-empty GUID string."
            return {"kind": kind, "value": value}, None
        if kind == "object":
            guid = reference_query.get("guid")
            local_file_id = reference_query.get("localFileId")
            if not isinstance(guid, str) or not guid.strip() or not isinstance(local_file_id, str) or not local_file_id.strip():
                return None, "referenceQuery.guid and localFileId must be non-empty strings."
            return {"kind": kind, "guid": guid, "localFileId": local_file_id}, None
        value = reference_query.get("value")
        property_paths = reference_query.get("propertyPaths")
        if not isinstance(value, str) or not value or not isinstance(property_paths, list) or not property_paths or any(not isinstance(path, str) or not path for path in property_paths):
            return None, "stringLiteral queries require value and non-empty string propertyPaths."
        return {"kind": kind, "value": value, "propertyPaths": sorted(set(property_paths))}, None

    @staticmethod
    def _is_project_asset_folder(path: object) -> bool:
        """Accept only a canonical Assets folder scope, never an asset/path traversal."""
        if not isinstance(path, str) or not path.startswith("Assets/"):
            return False
        if path == "Assets/":
            return True
        if path != path.strip() or "\\" in path:
            return False
        parts = path.rstrip("/").split("/")
        return all(part and part not in {".", ".."} for part in parts)

    async def texture_importer_patch(
        self,
        asset_path: str,
        changes: dict,
        dry_run: bool = True,
        confirm_token: str = "",
        reimport: bool = True,
    ) -> ToolResponse:
        request_id = new_id("req")
        if not changes:
            return fail(request_id, "TEXTURE_PATCH_INVALID", "changes is required.", {"assetPath": asset_path})
        root = self._active_project_root()
        if root is None:
            return fail(request_id, "PROJECT_PATH_UNAVAILABLE", "Unity project path is unavailable.", {})
        if not asset_path.startswith("Assets/"):
            return fail(request_id, "TEXTURE_PATH_INVALID", "assetPath must be project-relative under Assets/.", {"assetPath": asset_path})
        source = (root / asset_path).resolve()
        meta = Path(str(source) + ".meta")
        try:
            source.relative_to(root)
            before_hash = hashlib.sha256(source.read_bytes()).hexdigest()
            meta_hash = hashlib.sha256(meta.read_bytes()).hexdigest() if meta.exists() else ""
        except (OSError, ValueError) as ex:
            return fail(request_id, "TEXTURE_PATH_INVALID", str(ex), {"assetPath": asset_path})
        normalized = {str(key): value for key, value in sorted(changes.items())}
        token_payload = json.dumps(
            {"assetPath": asset_path, "sourceSha256": before_hash, "metaSha256": meta_hash, "changes": normalized, "reimport": bool(reimport)},
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
        expected_token = hashlib.sha256(token_payload).hexdigest()
        preview = {
            "assetPath": asset_path,
            "dryRun": dry_run,
            "applied": False,
            "writeAttempted": False,
            "changes": normalized,
            "sourceSha256": before_hash,
            "metaSha256": meta_hash,
            "confirmToken": expected_token,
            "reimport": bool(reimport),
        }
        if dry_run:
            current = await self.texture_importer_get(asset_path)
            if current.ok:
                preview["before"] = current.data or {}
            return ok(request_id, preview)
        rejected = self._reject_write_if_unapproved(request_id, "unity_texture_importer_patch")
        if rejected is not None:
            return rejected
        if confirm_token != expected_token:
            return fail(request_id, "TEXTURE_CONFIRM_TOKEN_INVALID", "confirmToken does not match the current asset/meta and changes.", preview)
        payload: dict = {"assetPath": asset_path, "reimport": bool(reimport)}
        field_map = {
            "mipmapEnabled": "MipmapEnabled",
            "alphaSource": "AlphaSource",
            "alphaIsTransparency": "AlphaIsTransparency",
            "sRGBTexture": "SRGBTexture",
            "wrapMode": "WrapMode",
            "filterMode": "FilterMode",
            "anisoLevel": "AnisoLevel",
            "isReadable": "IsReadable",
            "textureCompression": "TextureCompression",
            "maxTextureSize": "MaxTextureSize",
        }
        unknown = sorted(set(normalized) - set(field_map))
        if unknown:
            return fail(request_id, "TEXTURE_PATCH_FIELD_UNKNOWN", "Unsupported texture importer fields.", {"fields": unknown})
        for key, value in normalized.items():
            suffix = field_map[key]
            payload["apply" + suffix] = True
            payload[key] = value
        applied = await self.dispatcher.call(request_id, "texture.importerPatch", payload)
        if applied.ok and applied.data is not None:
            applied.data.update({"dryRun": False, "confirmTokenAccepted": True, "sourceSha256": before_hash, "metaSha256Before": meta_hash})
        return applied

    async def asset_reimport(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_reimport")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(request_id, "asset.reimport", {"assetPath": asset_path})

    async def asset_create_folder(
        self, parent_folder: str, new_folder_name: str
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_create_folder")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "asset.createFolder",
            {
                "parentFolder": parent_folder,
                "newFolderName": new_folder_name,
            },
        )

    async def asset_copy(self, source_path: str, destination_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_copy")
        if rejected is not None:
            return rejected
        response = await self.dispatcher.call(
            request_id,
            "asset.copy",
            {
                "sourcePath": source_path,
                "destinationPath": destination_path,
            },
        )
        return _require_mutation_success(response, "unity_asset_copy")

    async def asset_move(self, source_path: str, destination_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_move")
        if rejected is not None:
            return rejected
        response = await self.dispatcher.call(
            request_id,
            "asset.move",
            {
                "sourcePath": source_path,
                "destinationPath": destination_path,
            },
        )
        return _require_mutation_success(response, "unity_asset_move")

    async def asset_delete(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_delete")
        if rejected is not None:
            return rejected
        response = await self.dispatcher.call(
            request_id, "asset.delete", {"assetPath": asset_path}
        )
        return _require_mutation_success(response, "unity_asset_delete")

    async def asset_refresh(self) -> ToolResponse:
        request_id = new_id("req")
        response = await self.dispatcher.call(request_id, "asset.refresh", {})
        if response.ok and (response.data or {}).get("ok") is not True:
            return fail(request_id, "RESULT_CONTRACT_VIOLATION", "asset.refresh did not report data.ok=true.",
                        {"bridgeData": response.data}, context=response.context, timing=response.timing)
        return response

    async def sync_after_disk_write(
        self,
        delay_s: float = 2.0,
        trigger_compile: bool = False,
        write_batch_id: str = "",
        write_batch_created_at: int = 0,
        compile_operation_id: str = "",
    ) -> ToolResponse:
        """Wait for OS/fs flush, then AssetDatabase.Refresh; optionally unity_compile.

        Intended to be called once per batch after all in-editor script edits/saves are
        finished (not after each file). Same for external toolchains writing many files.
        Reduces redundant compiles and matches disk flush timing. Unity imports without
        relying on window focus.
        """
        logger = logging.getLogger("upilot.facade")
        request_id = new_id("req")

        if write_batch_id:
            batch = self.server.state.get_write_batch(write_batch_id)
            if batch is None:
                return fail(request_id, "WRITE_BATCH_NOT_FOUND", f"Unknown writeBatchId: {write_batch_id}")
            if write_batch_created_at and int(batch["writeBatchCreatedAt"]) != int(write_batch_created_at):
                return fail(
                    request_id,
                    "WRITE_BATCH_TIMESTAMP_MISMATCH",
                    "writeBatchCreatedAt does not match the registered batch.",
                    {"writeBatchId": write_batch_id, "expected": batch["writeBatchCreatedAt"], "actual": write_batch_created_at},
                )
            execution = self._current_execution_state()
            if execution.get("playModeState") != "edit" or not execution.get("authoritative"):
                self.server.state.mark_write_batch(write_batch_id, "deferred")
                return fail(
                    request_id,
                    "EDITOR_IN_PLAY_MODE" if execution.get("playModeState") in {"play", "pause"} else "EDITOR_CONTEXT_NOT_READY",
                    "The registered code write batch can only be synchronized in authoritative EditMode.",
                    {**execution, "writeBatchId": write_batch_id},
                )
            self.server.state.mark_write_batch(write_batch_id, "syncing")

        # External disk writes can make Unity start importing/compiling before
        # the client reaches this workflow.  In that state a refresh command
        # cannot safely cross the Domain Reload transport gap and starting a
        # second compile is redundant.  Report an authoritative attachment so
        # the caller can continue with unity_safe_compile_and_wait.
        server = getattr(self, "server", None)
        state = getattr(server, "state", None)
        compile_state = getattr(state, "compile", None)
        editor_state = getattr(state, "editor", None)
        compile_phase = str(getattr(compile_state, "phase", "") or "").lower()
        compile_active = bool(
            trigger_compile
            and (
                compile_phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"}
                or bool(getattr(editor_state, "is_compiling", False))
            )
        )
        if compile_active:
            return ok(
                request_id,
                {
                    "delayS": delay_s,
                    "triggerCompile": True,
                    "status": "compiling",
                    "refreshed": False,
                    "refreshSkipped": True,
                    "refreshSkipReason": "compile_already_active",
                    "compileStarted": False,
                    "compiled": False,
                    "compileCompleted": False,
                    "compileAlreadyRunning": True,
                    "attachedToExistingCompile": True,
                    "nextAction": "unity_safe_compile_and_wait",
                    "compileState": {
                        "status": getattr(compile_state, "status", "compiling"),
                        "phase": getattr(compile_state, "phase", compile_phase),
                        "errorCount": getattr(compile_state, "error_count", 0),
                        "warningCount": getattr(compile_state, "warning_count", 0),
                        "commandQueuedAt": getattr(compile_state, "command_queued_at", 0),
                        "unityAcceptedAt": getattr(compile_state, "unity_accepted_at", 0),
                        "startedAt": getattr(compile_state, "started_at", 0),
                        "finishedAt": getattr(compile_state, "finished_at", 0),
                        "lastProgressAt": getattr(compile_state, "last_progress_at", 0),
                        "compileRequestId": getattr(compile_state, "compile_request_id", ""),
                    },
                    "executionState": self._current_execution_state(),
                },
            )
        await asyncio.sleep(max(0.0, delay_s))
        refresh_r = await self.asset_refresh()
        payload: dict = {
            "delayS": delay_s,
            "triggerCompile": trigger_compile,
            "status": "refresh_failed" if not refresh_r.ok else "refreshed",
            "refreshed": refresh_r.ok,
            "writeBatchId": write_batch_id,
            "writeBatchCreatedAt": write_batch_created_at,
            "compileOperationId": compile_operation_id,
            "correlationVerified": False,
            "executionState": self._current_execution_state(),
        }
        if refresh_r.data is not None:
            refresh_data = dict(refresh_r.data)
            reported_ok = refresh_data.get("ok")
            reported_status = refresh_data.get("status")
            refresh_data["ok"] = bool(refresh_r.ok)
            refresh_data["status"] = "ok" if refresh_r.ok else (str(reported_status or "failed"))
            if reported_ok is not None and bool(reported_ok) != bool(refresh_r.ok):
                refresh_data["normalizedFrom"] = {
                    "ok": reported_ok,
                    "status": reported_status,
                }
            payload["refresh"] = refresh_data
        if not refresh_r.ok:
            msg = refresh_r.error.message if refresh_r.error else "AssetDatabase.Refresh failed"
            payload["failureSignature"] = refresh_r.error.code if refresh_r.error else "ASSET_REFRESH_FAILED"
            payload["refreshError"] = msg
            return fail(
                request_id,
                payload["failureSignature"],
                msg,
                payload,
            )
        if not trigger_compile:
            payload["nextAction"] = "Call unity_safe_compile_and_wait with this writeBatchId to obtain a correlated terminal result." if write_batch_id else ""
            return ok(request_id, payload)
        compile_r = await (
            self.compile(
                write_batch_id=write_batch_id,
                write_batch_created_at=write_batch_created_at,
            )
            if write_batch_id
            else self.compile()
        )
        payload["compileStarted"] = compile_r.ok
        payload["compiled"] = False
        payload["compileCompleted"] = False
        if compile_r.ok and compile_r.data is not None:
            payload["compile"] = compile_r.data
            wait_r = await self.compile_wait(
                timeout_s=60,
                poll_interval_s=0.25,
                prefer_events=True,
            )
            wait_data = dict(wait_r.data or {})
            payload["compileWait"] = wait_data
            compile_state = self.server.state.compile
            payload["compileState"] = {
                "status": compile_state.status,
                "phase": compile_state.phase,
                "errorCount": compile_state.error_count,
                "warningCount": compile_state.warning_count,
                "commandQueuedAt": compile_state.command_queued_at,
                "unityAcceptedAt": compile_state.unity_accepted_at,
                "startedAt": compile_state.started_at,
                "finishedAt": compile_state.finished_at,
                "lastProgressAt": compile_state.last_progress_at,
                "compileRequestId": compile_state.compile_request_id,
            }
            if not wait_r.ok:
                msg = wait_r.error.message if wait_r.error else "compile wait failed"
                code = wait_r.error.code if wait_r.error else "COMPILE_WAIT_FAILED"
                payload.update(
                    {
                        "status": "compile_failed",
                        "compileError": msg,
                        "failureSignature": code,
                    }
                )
                return fail(request_id, code, msg, payload)

            if str(wait_data.get("status") or "").lower() in (
                "blocked",
                "unknown",
                "recovering_after_reload",
            ):
                blocked_reason = str(
                    wait_data.get("blockedReason")
                    or (wait_data.get("executionState") or {}).get("blockedReason")
                    or "EditorContextNotReady"
                )
                payload.update(
                    {
                        "status": "blocked",
                        "blocked": True,
                        "blockedReason": blocked_reason,
                        "playModeBlocked": blocked_reason == "PlayMode",
                        "nextAction": wait_data.get("nextAction")
                        or (wait_data.get("executionState") or {}).get("nextAction")
                        or "Refresh the Editor context and retry.",
                        "compileStarted": False,
                        "compiled": False,
                        "compileCompleted": False,
                    }
                )
                return fail(
                    request_id,
                    "EDITOR_IN_PLAY_MODE"
                    if blocked_reason == "PlayMode"
                    else "EDITOR_CONTEXT_NOT_READY",
                    "Compilation is blocked by the current Unity Editor state.",
                    payload,
                )

            uses_v2_state = bool(getattr(self.server.state, "producer_epoch", ""))
            terminal = bool(compile_state.terminal and compile_state.errors_verified) if uses_v2_state else (
                str(wait_data.get("status") or "").lower() == "ready"
                and not bool(wait_data.get("isCompiling", False))
                and not bool(self.server.state.editor.is_compiling)
            )
            if terminal:
                if not uses_v2_state:
                    compile_state.status = "finished"
                    compile_state.phase = "completed"
                    if not compile_state.finished_at:
                        compile_state.finished_at = now_ms()
                    compile_state.last_progress_at = compile_state.finished_at
                payload.update(
                    {
                        "status": "compiled",
                        "compiled": True,
                        "compileCompleted": True,
                    }
                )
            else:
                payload.update(
                    {
                        "status": "compiling",
                        "nextAction": "unity_compile_wait",
                        "note": "Compilation was accepted but Unity has not confirmed an idle terminal state.",
                    }
                )
            payload["compileState"] = {
                "status": compile_state.status,
                "phase": compile_state.phase,
                "errorCount": compile_state.error_count,
                "warningCount": compile_state.warning_count,
                "commandQueuedAt": compile_state.command_queued_at,
                "unityAcceptedAt": compile_state.unity_accepted_at,
                "startedAt": compile_state.started_at,
                "finishedAt": compile_state.finished_at,
                "lastProgressAt": compile_state.last_progress_at,
                "compileRequestId": compile_state.compile_request_id,
            }
            payload["executionState"] = self._current_execution_state()
        elif not compile_r.ok:
            msg = compile_r.error.message if compile_r.error else "compile failed"
            code = compile_r.error.code if compile_r.error else "COMPILE_FAILED"
            if code in ("EDITOR_IN_PLAY_MODE", "EDITOR_CONTEXT_NOT_READY"):
                detail = compile_r.error.detail if compile_r.error else {}
                blocked_reason = str(
                    detail.get("blockedReason")
                    or ("PlayMode" if code == "EDITOR_IN_PLAY_MODE" else "EditorContextNotReady")
                )
                payload.update(
                    {
                        "status": "blocked",
                        "blocked": True,
                        "blockedReason": blocked_reason,
                        "playModeBlocked": blocked_reason == "PlayMode",
                        "nextAction": detail.get("nextAction")
                        or "Refresh the Editor context and retry.",
                        "compileStarted": False,
                        "compiled": False,
                        "compileCompleted": False,
                    }
                )
                return fail(request_id, code, msg, payload)
            if code == "EDITOR_BUSY":
                compile_state = self.server.state.compile
                payload.update(
                    {
                        "status": "compiling",
                        "compileStarted": False,
                        "compiled": False,
                        "compileCompleted": False,
                        "compileAlreadyRunning": True,
                        "attachedToExistingCompile": True,
                        "nextAction": "unity_compile_wait",
                        "compileAttachReason": msg,
                        "compileState": {
                            "status": compile_state.status,
                            "phase": compile_state.phase,
                            "errorCount": compile_state.error_count,
                            "warningCount": compile_state.warning_count,
                            "commandQueuedAt": compile_state.command_queued_at,
                            "unityAcceptedAt": compile_state.unity_accepted_at,
                            "startedAt": compile_state.started_at,
                            "finishedAt": compile_state.finished_at,
                            "lastProgressAt": compile_state.last_progress_at,
                            "compileRequestId": compile_state.compile_request_id,
                        },
                        "note": "AssetDatabase.Refresh completed; Unity was already compiling.",
                    }
                )
                return ok(request_id, payload)
            logger.warning("sync_after_disk_write: compile failed: %s", msg)
            payload.update(
                {
                    "status": "compile_failed",
                    "compileStarted": False,
                    "compiled": False,
                    "compileCompleted": False,
                    "compileError": msg,
                    "failureSignature": code,
                }
            )
            return fail(
                request_id,
                code,
                msg,
                payload,
            )
        return ok(request_id, payload)

    async def asset_get_info(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "asset.getInfo", {"assetPath": asset_path}
        )

    async def asset_subresources_list(
        self, asset_path: str, type_filter: str = "", include_preview: bool = False
    ) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "asset.subresourcesList",
            {"assetPath": asset_path, "typeFilter": type_filter, "includePreview": include_preview},
        )

    async def animator_controller_inspect(self, asset_path: str) -> ToolResponse:
        return await self.dispatcher.call(new_id("req"), "animator.controllerInspect", {"assetPath": asset_path})

    async def avatar_mask_inspect(self, asset_path: str) -> ToolResponse:
        return await self.dispatcher.call(new_id("req"), "animator.avatarMaskInspect", {"assetPath": asset_path})

    async def model_importer_inspect(self, asset_path: str) -> ToolResponse:
        return await self.dispatcher.call(new_id("req"), "model.importerInspect", {"assetPath": asset_path})

    async def asset_find_built_in(
        self, query: str = "", asset_type: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if query:
            payload["query"] = query
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.findBuiltIn", payload)

    async def asset_get_data(
        self,
        asset_path: str = "",
        game_object_id: int = 0,
        component_type: str = "",
        component_index: int = 0,
        max_depth: int = 10,
        max_nodes: int = 500,
        continuation_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"maxDepth": max_depth, "maxNodes": max_nodes}
        if asset_path:
            payload["assetPath"] = asset_path
        if game_object_id:
            payload["gameObjectId"] = game_object_id
        if component_type:
            payload["componentType"] = component_type
        if component_index:
            payload["componentIndex"] = component_index
        if continuation_token:
            payload["continuationToken"] = continuation_token
        return await self.dispatcher.call(request_id, "asset.getData", payload)

    async def asset_modify_data(
        self,
        properties: list[dict],
        asset_path: str = "",
        game_object_id: int = 0,
        component_type: str = "",
        component_index: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_asset_modify_data")
        if rejected is not None:
            return rejected
        payload: dict = {"properties": properties}
        if asset_path:
            payload["assetPath"] = asset_path
        if game_object_id:
            payload["gameObjectId"] = game_object_id
        if component_type:
            payload["componentType"] = component_type
        if component_index:
            payload["componentIndex"] = component_index
        return await self.dispatcher.call(request_id, "asset.modifyData", payload)

    async def prefab_query_components(
        self,
        prefab_path: str,
        component_type: str,
        include_serialized_fields: bool = True,
        max_depth: int = 6,
        max_results: int = 50,
        follow_object_references: bool = False,
        include_nested_prefab_contents: bool = False,
        reference_depth: int = 1,
    ) -> ToolResponse:
        request_id = new_id("req")
        if (not isinstance(prefab_path, str) or prefab_path != prefab_path.strip()
                or not prefab_path or not prefab_path.endswith(".prefab")):
            return fail(request_id, "INVALID_PREFAB_PATH", "prefabPath must be a non-empty .prefab asset path.",
                        {"field": "prefabPath", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if not isinstance(component_type, str) or not component_type.strip():
            return fail(request_id, "INVALID_COMPONENT_TYPE", "componentType is required.",
                        {"field": "componentType", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if not isinstance(follow_object_references, bool) or not isinstance(include_nested_prefab_contents, bool):
            return fail(request_id, "REFERENCE_QUERY_INVALID", "followObjectReferences and includeNestedPrefabContents must be booleans.",
                        {"field": "followObjectReferences", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if not isinstance(reference_depth, int) or isinstance(reference_depth, bool) or not 1 <= reference_depth <= 4:
            return fail(request_id, "REFERENCE_DEPTH_INVALID", "referenceDepth must be an integer from 1 through 4.",
                        {"field": "referenceDepth", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if include_nested_prefab_contents and not follow_object_references:
            return fail(request_id, "REFERENCE_QUERY_INVALID", "includeNestedPrefabContents requires followObjectReferences=true.",
                        {"field": "includeNestedPrefabContents", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        if reference_depth != 1 and not follow_object_references:
            return fail(request_id, "REFERENCE_QUERY_INVALID", "referenceDepth requires followObjectReferences=true.",
                        {"field": "referenceDepth", "loadAttempted": False, "sideEffectsMayHaveOccurred": False})
        payload: dict = {
            "prefabPath": prefab_path,
            "componentType": component_type,
            "includeSerializedFields": include_serialized_fields,
            "maxDepth": max_depth,
            "maxResults": max_results,
        }
        if follow_object_references or include_nested_prefab_contents or reference_depth != 1:
            payload.update({
                "followObjectReferences": follow_object_references,
                "includeNestedPrefabContents": include_nested_prefab_contents,
                "referenceDepth": reference_depth,
            })
        return _with_read_only_observation_evidence(
            await self.dispatcher.call(
                request_id,
                "prefab.queryComponents",
                payload,
            )
        )

    async def prefab_physics_audit(
        self,
        prefab_paths: list[str],
        max_results_per_prefab: int = 1000,
        sort_by: str = "colliderCount",
        descending: bool = True,
    ) -> ToolResponse:
        request_id = new_id("req")
        if not prefab_paths:
            return fail(
                request_id,
                "INVALID_PREFAB_PATHS",
                "prefabPaths must contain at least one prefab path.",
                {},
            )
        return await self.dispatcher.call(
            request_id,
            "prefab.physicsAudit",
            {
                "prefabPaths": prefab_paths,
                "maxResultsPerPrefab": max_results_per_prefab,
                "sortBy": sort_by,
                "descending": descending,
            },
        )

    async def prefab_create(
        self, source_game_object_id: int, prefab_path: str
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_prefab_create")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "prefab.create",
            {
                "sourceGameObjectId": source_game_object_id,
                "prefabPath": prefab_path,
            },
        )

    async def prefab_instantiate(
        self, prefab_path: str, parent_id: int = 0
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_prefab_instantiate")
        if rejected is not None:
            return rejected
        payload: dict = {"prefabPath": prefab_path}
        if parent_id:
            payload["parentId"] = parent_id
        return await self.dispatcher.call(request_id, "prefab.instantiate", payload)

    async def prefab_open(self, prefab_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "prefab.open", {"prefabPath": prefab_path}
        )

    async def prefab_close(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "prefab.close", {})

    async def prefab_save(self) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_prefab_save")
        if rejected is not None:
            return rejected
        response = await self.dispatcher.call(request_id, "prefab.save", {})
        return _require_mutation_success(response, "unity_prefab_save")

    async def material_create(
        self, material_path: str, shader_name: str = "Standard"
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_material_create")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "material.create",
            {
                "materialPath": material_path,
                "shaderName": shader_name,
            },
        )

    async def material_modify(
        self, material_path: str, properties: dict
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_material_modify")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "material.modify",
            {
                "materialPath": material_path,
                "properties": properties,
            },
        )

    async def material_assign(
        self, target_game_object_id: int, material_path: str, material_index: int = 0
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_material_assign")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "material.assign",
            {
                "targetGameObjectId": target_game_object_id,
                "materialPath": material_path,
                "materialIndex": material_index,
            },
        )

    async def material_get(self, material_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "material.get", {"materialPath": material_path}
        )

    async def shader_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "shader.list", {})

    async def shader_inspect(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "shader.inspect", {"assetPath": asset_path})

    async def shader_check_errors(self, asset_path: str, include_warnings: bool = True) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "shader.checkErrors", {"assetPath": asset_path, "includeWarnings": include_warnings}
        )

    async def menu_execute(self, menu_path: str, expected_modal: dict | None = None) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_menu_execute")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id, "menu.execute", {"menuPath": menu_path, **({"expectedModal": expected_modal} if expected_modal is not None else {})}
        )

    async def menu_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "menu.list", {})

    async def package_add(self, package_name: str, version: str = "") -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_package_add")
        if rejected is not None:
            return rejected
        payload: dict = {"packageName": package_name}
        if version:
            payload["version"] = version
        return await self.dispatcher.call(
            request_id, "package.add", payload, timeout_ms=120000
        )

    async def package_remove(self, package_name: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_package_remove")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "package.remove",
            {"packageName": package_name},
            timeout_ms=60000,
        )

    async def package_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "package.list", {})

    async def package_search(self, query: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "package.search", {"query": query}
        )

    async def script_read(self, script_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "script.read", {"scriptPath": script_path}
        )

    async def script_create(self, script_path: str, content: str = "") -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_script_create")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "script.create",
            {
                "scriptPath": script_path,
                "content": content,
            },
        )

    async def script_update(self, script_path: str, content: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_script_update")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "script.update",
            {
                "scriptPath": script_path,
                "content": content,
            },
        )

    async def script_delete(self, script_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_script_delete")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id, "script.delete", {"scriptPath": script_path}
        )

    async def resource_scene_hierarchy(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.sceneHierarchy", {})

    async def resource_console_logs(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.consoleLogs", {})

    async def resource_editor_state(self) -> ToolResponse:
        request_id = new_id("req")
        result = await self.dispatcher.call(request_id, "resource.editorState", {})
        if result.ok and result.data is not None:
            self._update_editor_cache_from_resource_state(result.data)
        return result

    async def resource_packages(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.packages", {})

    async def resource_build_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.buildStatus", {})

    async def resource_upilot_logs_tab(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.upilotLogsTab", {})

    async def resource_window_diagnostics(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.windowDiagnostics", {})

    async def resource_console_summary(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.consoleSummary", {})

    async def capabilities_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "capabilities.list", {})

    # Scene graph, component, and batch resource operations.
    async def gameobject_create(
        self, name: str = "New GameObject", parent_id: int = 0, primitive_type: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_gameobject_create")
        if rejected is not None:
            return rejected
        payload: dict = {"name": name}
        if parent_id:
            payload["parentId"] = parent_id
        if primitive_type:
            payload["primitiveType"] = primitive_type
        return await self.dispatcher.call(request_id, "gameobject.create", payload)

    async def gameobject_find(
        self, name: str = "", tag: str = "", instance_id: int | str = 0,
        component_type: str = "", include_inactive: bool = True,
        include_hidden: bool = False, limit: int = 100,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if name:
            payload["name"] = name
        if tag:
            payload["tag"] = tag
        if instance_id:
            payload["instanceId"] = instance_id
        if component_type:
            payload["componentType"] = component_type
        payload["includeInactive"] = include_inactive
        payload["includeHidden"] = include_hidden
        payload["limit"] = max(1, min(int(limit), 1000))
        return await self.dispatcher.call(request_id, "gameobject.find", payload)

    async def gameobject_modify(
        self,
        instance_id: int,
        name: str | None = None,
        tag: str | None = None,
        layer: int | None = None,
        active_self: bool | None = None,
        is_static: bool | None = None,
        parent_id: int | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_gameobject_modify")
        if rejected is not None:
            return rejected
        payload: dict = {"instanceId": instance_id}
        if name is not None:
            payload["name"] = name
        if tag is not None:
            payload["tag"] = tag
        if layer is not None:
            payload["layer"] = layer
        if active_self is not None:
            payload["activeSelf"] = active_self
        if is_static is not None:
            payload["isStatic"] = is_static
        if parent_id is not None:
            payload["parentId"] = parent_id
        return await self.dispatcher.call(request_id, "gameobject.modify", payload)

    async def gameobject_delete(self, instance_id: int) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_gameobject_delete")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id, "gameobject.delete", {"instanceId": instance_id}
        )

    async def gameobject_move(
        self,
        instance_id: int,
        position: dict | None = None,
        rotation: dict | None = None,
        scale: dict | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_gameobject_move")
        if rejected is not None:
            return rejected
        payload: dict = {"instanceId": instance_id}
        if position is not None:
            payload["position"] = position
        if rotation is not None:
            payload["rotation"] = rotation
        if scale is not None:
            payload["scale"] = scale
        return await self.dispatcher.call(request_id, "gameobject.move", payload)

    async def gameobject_duplicate(self, instance_id: int) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_gameobject_duplicate")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id, "gameobject.duplicate", {"instanceId": instance_id}
        )

    async def scene_create(self, scene_name: str = "") -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_scene_create")
        if rejected is not None:
            return rejected
        payload: dict = {}
        if scene_name:
            payload["sceneName"] = scene_name
        return await self.dispatcher.call(request_id, "scene.create", payload)

    async def scene_open(self, scene_path: str, mode: str = "single") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "scene.open",
            {"scenePath": scene_path, "mode": mode},
            timeout_ms=30000,
        )

    async def scene_save(self, scene_path: str = "") -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_scene_save")
        if rejected is not None:
            return rejected
        payload: dict = {}
        if scene_path:
            payload["scenePath"] = scene_path
        return await self.dispatcher.call(request_id, "scene.save", payload)

    async def scene_load(self, scene_path: str, mode: str = "additive") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "scene.load",
            {"scenePath": scene_path, "mode": mode},
            timeout_ms=30000,
        )

    async def scene_set_active(self, scene_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "scene.setActive", {"scenePath": scene_path}
        )

    async def scene_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.list", {})

    async def scene_unload(
        self, scene_path: str, remove_scene: bool = False
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_scene_unload")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "scene.unload",
            {
                "scenePath": scene_path,
                "removeScene": 1 if remove_scene else 0,
            },
        )

    async def scene_ensure_test(
        self,
        scene_name: str = "upilot-test",
        scene_path: str = "",
    ) -> ToolResponse:
        """Open a dedicated empty test scene, or create and save it if missing.

        Bridge command ``scene.ensureTest``: if ``Assets/<name>.unity`` exists, open it;
        otherwise creates ``NewSceneSetup.EmptyScene``, saves to that path, refreshes assets.
        Use for automation / acceptance without touching project business scenes.
        """
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_scene_ensure_test")
        if rejected is not None:
            return rejected
        payload: dict[str, str] = {}
        if scene_path:
            payload["scenePath"] = scene_path
        else:
            payload["sceneName"] = scene_name
        return await self.dispatcher.call(
            request_id, "scene.ensureTest", payload, timeout_ms=60000
        )

    async def component_add(
        self, game_object_id: int, component_type: str
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_component_add")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "component.add",
            {
                "gameObjectId": game_object_id,
                "componentType": component_type,
            },
        )

    async def component_remove(
        self, game_object_id: int, component_type: str, component_index: int = 0
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_component_remove")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "component.remove",
            {
                "gameObjectId": game_object_id,
                "componentType": component_type,
                "componentIndex": component_index,
            },
        )

    async def component_get(
        self, game_object_id: int, component_type: str, component_index: int = 0
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "component.get",
            {
                "gameObjectId": game_object_id,
                "componentType": component_type,
                "componentIndex": component_index,
            },
        )

    async def component_modify(
        self,
        game_object_id: int,
        component_type: str,
        properties: dict,
        component_index: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_component_modify")
        if rejected is not None:
            return rejected
        try:
            property_writes = _normalize_component_property_writes(properties)
        except ValueError as ex:
            return fail(
                request_id,
                "COMPONENT_MODIFY_INVALID_PROPERTIES",
                str(ex),
                {"componentType": component_type},
            )
        return await self.dispatcher.call(
            request_id,
            "component.modify",
            {
                "gameObjectId": game_object_id,
                "componentType": component_type,
                "properties": property_writes,
                "componentIndex": component_index,
            },
        )

    async def component_list(self, game_object_id: int) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "component.list", {"gameObjectId": game_object_id}
        )

    async def batch_execute(
        self, operations: list, mode: str = "sequential", stop_on_error: bool = True
    ) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_batch_execute")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id,
            "batch.execute",
            {
                "operations": operations,
                "mode": mode,
                "stopOnError": stop_on_error,
            },
            timeout_ms=60000,
        )

    async def batch_cancel(self, batch_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "batch.cancel", {"batchId": batch_id}
        )

    async def batch_results(self, batch_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "batch.results", {"batchId": batch_id}
        )
