from __future__ import annotations

import asyncio
import base64
import binascii
import copy
import hashlib
import json
import logging
import math
from functools import wraps
import os
import secrets
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
from ..test_job_context import TEST_JOB_CONTEXT, TestJobCancelledBeforeStart
from ..operation_context import OPERATION_ID, TASK_TOOL

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


_UPILOT_BLOCK_START = "<!-- upilot:start -->"
_UPILOT_BLOCK_END = "<!-- upilot:end -->"
_SKILL_TEMPLATE_ROOT_RELATIVE = Path("skills") / "upilot-unity-mcp"
_TEMPLATE_MANIFEST_RELATIVE = _SKILL_TEMPLATE_ROOT_RELATIVE / "template-manifest.json"
_DEFAULT_OPERATION_SUCCESS = {"succeeded", "success", "complete", "completed", "passed", "ok"}
_DEFAULT_OPERATION_FAILURE = {"failed", "failure", "canceled", "cancelled", "aborted", "timeout", "timedout", "error"}
_TERMINAL_STATUSES = _DEFAULT_OPERATION_SUCCESS | _DEFAULT_OPERATION_FAILURE


def _serialized_operation(method):
    """Serialize status/cancel so only one caller advances an operation's cleanup."""
    @wraps(method)
    async def guarded(self, operation_id, *args, **kwargs):
        self._recover_operations()
        locks = self.__dict__.setdefault("_operation_locks", {})
        collect_after = False
        async with locks.setdefault(operation_id, asyncio.Lock()):
            result = await method(self, operation_id, *args, **kwargs)
            state = self._operations.get(operation_id)
            collect_after = bool(state and state.pop("_deferredArtifactCollection", False))
            if state is not None and not self._save_operation(state):
                return fail(new_id("req"), "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        if collect_after and result.ok:
            # Artifact reads can block on project files; they deliberately run after
            # the status/cancel lock is released so cancellation and newer samples
            # are never held behind filesystem I/O.
            collection = await self.operation_collect_artifacts(
                operation_id,
                detail_level=str(kwargs.get("detail_level") or "summary"),
                max_tail_chars=int(kwargs.get("max_tail_chars") or 2000),
                include_raw_state=bool(kwargs.get("include_raw_state", False)),
            )
            state = self._operations.get(operation_id)
            if state is not None:
                public = self._public_operation_state(
                    state, str(kwargs.get("detail_level") or "summary"),
                    int(kwargs.get("max_tail_chars") or 2000), bool(kwargs.get("include_raw_state", False)),
                )
                # Artifact persistence is independent of the business terminal
                # result.  A deferred collection can fail after the status state
                # was durably saved; surface that failure to this caller without
                # overwriting the original business failureSignature or pretending
                # that the collection was persisted.
                if not collection.ok and collection.error is not None:
                    detail = collection.error.detail if isinstance(collection.error.detail, dict) else {}
                    public.update({
                        "collectionPersisted": False,
                        "artifactPersistenceError": detail.get(
                            "artifactPersistenceError", collection.error.message,
                        ),
                        "artifactCollectionSequence": int(detail.get(
                            "artifactCollectionSequence", state.get("artifactCollectionSequence") or 0,
                        )),
                    })
                return ok(result.request_id, public)
        return result
    return guarded


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


def _extract_operation_payload(result: ToolResponse) -> tuple[dict, str, dict | None]:
    """Return a normalized payload, retaining JSON parse evidence for every call kind."""
    if not result.ok or not isinstance(result.data, dict):
        return {}, "", None

    data = result.data
    for key in ("result", "raw", "data", "payload"):
        value = data.get(key)
        if isinstance(value, dict) and value:
            return value, "", None
        if not isinstance(value, str) or not value.strip() or value.strip() == "(null)":
            continue
        parsed, parse_error, diagnostic = _operation_parse_object(value)
        if parse_error:
            return {}, parse_error, diagnostic
        if parsed:
            return parsed, "", None

    return data, "", None


def _operation_path_get(value: object, path: object) -> tuple[bool, object]:
    """Read a small dot/bracket path without evaluating user input."""
    if not isinstance(path, str) or not path.strip():
        return False, None
    text = path.strip()
    if text == "$":
        return True, value
    if text.startswith("$."):
        text = text[2:]
    text = text.replace("[", ".").replace("]", "")
    current = value
    for segment in (part for part in text.split(".") if part):
        if isinstance(current, dict) and segment in current:
            current = current[segment]
            continue
        if isinstance(current, (list, tuple)) and segment.isdigit():
            index = int(segment)
            if 0 <= index < len(current):
                current = current[index]
                continue
        return False, None
    return True, current


def _operation_parse_object(value: object) -> tuple[dict, str, dict | None]:
    if isinstance(value, dict):
        return value, "", None
    if isinstance(value, str):
        # Parse the original text: JSONDecodeError positions are Unicode-character
        # offsets into this exact input, including any leading whitespace.
        text = value
        if not text.strip() or text.strip() == "(null)":
            return {}, "", None
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError as ex:
            start = max(0, ex.pos - 128)
            snippet = text[start:start + 256]
            return {}, f"Operation result is not valid JSON: {ex.msg} at position {ex.pos}.", {
                "offset": ex.pos, "offsetUnit": "unicodeCharacter", "line": ex.lineno,
                "column": ex.colno, "path": None, "snippet": snippet,
                "truncated": start > 0 or start + len(snippet) < len(text),
            }
        if isinstance(parsed, dict):
            return parsed, "", None
        return {}, f"Operation result JSON must be an object, got {type(parsed).__name__}.", None
    if value is None:
        return {}, "", None
    return {}, f"Operation result must be an object or JSON object string, got {type(value).__name__}.", None


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


def _operation_cleanup_pending(payload: dict | None) -> bool:
    """Return True while the project reports resources that still need cleanup."""
    if not isinstance(payload, dict):
        return False
    if bool(payload.get("cleanupPending")):
        return True
    try:
        if int(payload.get("activeLeaseCount") or 0) > 0:
            return True
    except (TypeError, ValueError):
        return True
    unresolved = payload.get("unresolvedResources")
    return isinstance(unresolved, (list, tuple, set, dict)) and len(unresolved) > 0


def _operation_cleanup_state_is_explicit(payload: dict | None) -> bool:
    """Return True only when the project explicitly reports its cleanup state."""
    if not isinstance(payload, dict):
        return False
    return any(
        key in payload
        for key in ("cleanupPending", "cleanupStatus", "cleanupSucceeded", "activeLeaseCount", "unresolvedResources")
    )


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _read_stable_artifact(path: Path) -> tuple[dict, str]:
    """Hash one opened file and reject a source that changes during that read."""
    h = hashlib.sha256()
    path_before = path.stat()
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
        after = os.fstat(stream.fileno())
    path_after = path.stat()
    identity_before = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
    identity_after = (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
    path_identity_before = (path_before.st_dev, path_before.st_ino, path_before.st_size, path_before.st_mtime_ns)
    path_identity_after = (path_after.st_dev, path_after.st_ino, path_after.st_size, path_after.st_mtime_ns)
    if identity_before != identity_after or identity_before != path_identity_before or identity_after != path_identity_after:
        return {}, "ARTIFACT_CHANGED_DURING_READ"
    return {
        "bytes": before.st_size,
        "sha256": h.hexdigest(),
        "modifiedAt": int(before.st_mtime * 1000),
    }, ""


def _is_sha256_text(value: object) -> bool:
    return isinstance(value, str) and len(value) == 64 and all(char in "0123456789abcdefABCDEF" for char in value)


def _verify_capture_artifacts(root: Path, session: dict) -> None:
    """Match ConsoleCaptureService's ordered, concatenated segment digest."""
    paths = {}
    for field in ("jsonlPath", "summaryPath"):
        path = Path(str(session.get(field) or ""))
        path = (path if path.is_absolute() else root / path).resolve()
        path.relative_to(root)
        if not path.is_file():
            raise ValueError(f"Missing capture artifact: {field}")
        paths[field] = path
    summary = json.loads(paths["summaryPath"].read_text(encoding="utf-8-sig"))
    for field in ("sessionId", "active", "finishedAtUtcMs", "sha256", "fileBytes", "segmentCount"):
        if field not in session or not isinstance(summary, dict) or summary.get(field) != session[field]:
            raise ValueError(f"Capture summary does not match stopped session: {field}")
    directory = paths["jsonlPath"].parent
    def segments():
        return sorted(
            (p for p in directory.iterdir()
             if p.name.lower().startswith("console") and p.name.lower().endswith(".jsonl")),
            key=lambda p: (p.name.lower() != "console.jsonl", p.name.lower()),
        )
    files = segments()
    if len(files) != session["segmentCount"] or paths["jsonlPath"] not in [p.resolve() for p in files]:
        raise ValueError("Console capture segment inventory mismatch.")
    digest, size = hashlib.sha256(), 0
    for path in files:
        path.resolve().relative_to(root)
        with path.open("rb") as stream:
            before = os.fstat(stream.fileno())
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
                size += len(chunk)
            after = os.fstat(stream.fileno())
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError("Console capture segment changed during verification.")
    if files != segments() or digest.hexdigest() != session["sha256"] or size != session["fileBytes"]:
        raise ValueError("Console capture artifact hash/size mismatch.")


def _normalize_agent_rules_block(block: str) -> str:
    lines = block.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    normalized = ["generatedAt: <ignored>" if line.startswith("generatedAt: ") else line for line in lines]
    return "\n".join(normalized).strip()


def _read_text_tail(path: Path, lines: int) -> str:
    if lines <= 0:
        return ""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    return "\n".join(text.splitlines()[-lines:])


class TaskDomainService:
    def _save_operation(self, state: dict) -> bool:
        try:
            self.server.state.save_operation(state)
            return True
        except Exception as exc:
            state.update(status="RecoveryRequired", phase="persistence_failed", endedAt=0,
                         error=str(exc), nextAction="Inspect persisted operation evidence; do not replay start.")
            return False

    def _recover_operations(self) -> None:
        store = getattr(self.server, "state", None)
        project = getattr(store, "_project_path", "")
        if not project or getattr(self, "_operations_loaded_project", "") == project:
            return
        if getattr(self, "_operations_loaded_project", ""):
            for handle in self.__dict__.get("_operation_observers", {}).values():
                handle.cancel()
            self._operations.clear()
        self._operations_loaded_project = project
        for state in store.load_operations():
            if state["operationId"] in self._operations:
                continue
            state["recovered"] = True
            self._operations[state["operationId"]] = state
            if state.get("endedAt"):
                continue
            if not state.get("startEstablished") or not state.get("recoveryIdentityEstablished"):
                state.update(status="RecoveryRequired", phase="start_identity_unknown", endedAt=0,
                             nextAction="Inspect the original start/capture evidence; start was not replayed.")
                self._save_operation(state)
            else:
                self._resume_operation_observer(state)

    def _resume_operation_observer(self, state: dict) -> None:
        handles = self.__dict__.setdefault("_operation_observers", {})
        operation_id = state["operationId"]
        current = handles.get(operation_id)
        if not state.get("endedAt") and (current is None or current.done()):
            handles[operation_id] = asyncio.create_task(self._observe_operation(state), name=operation_id)

    async def _observe_operation(self, state: dict) -> None:
        try:
            while not state.get("endedAt"):
                await asyncio.sleep(max(0.05, float(state.get("pollIntervalSec") or 3)))
                if state.get("projectPath") != self.server.state._project_path:
                    return
                if state.get("status") == "RecoveryRequired":
                    return
                await self.operation_status(state["operationId"])
        except asyncio.CancelledError:
            # Shutdown stops observation, not the underlying operation.
            raise
        except Exception as exc:
            state.update(status="RecoveryRequired", phase="observer_failed", endedAt=0,
                         error=str(exc), nextAction="Inspect the original operation; do not replay start.")
        finally:
            self._save_operation(state)

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

    def _operation_project_matches(self, state: dict) -> bool:
        """Operations may observe only the exact project that established them."""
        stored = str(state.get("projectPath") or "")
        if not stored:
            # Historical operations have no authoritative project binding.  They
            # remain recovery records, never cross-project work.
            return False
        try:
            return os.path.normcase(str(Path(stored).resolve())) == os.path.normcase(str(self._project_root()))
        except OSError:
            return False

    def _operation_project_mismatch(self, request_id: str, state: dict) -> ToolResponse:
        return fail(
            request_id,
            "OPERATION_PROJECT_MISMATCH",
            "Operation belongs to a different Unity project; no operation call or artifact read was attempted.",
            {
                **self._public_operation_state(state),
                "operationProject": str(state.get("projectPath") or ""),
                "connectedProject": str(self._project_root()),
                "sideEffectsMayHaveOccurred": False,
            },
        )

    @staticmethod
    def _upilot_package_version() -> str:
        package_json = Path(__file__).resolve().parents[4] / "package.json"
        try:
            data = json.loads(package_json.read_text(encoding="utf-8"))
            return str(data.get("version") or "unknown")
        except (OSError, json.JSONDecodeError):
            return "unknown"

    @staticmethod
    def _read_template_resource(relative: Path) -> tuple[str, Path]:
        candidates = []
        bundle_root = getattr(sys, "_MEIPASS", "")
        if bundle_root:
            candidates.append(Path(bundle_root) / relative)
        candidates.append(Path(__file__).resolve().parents[4] / relative)
        candidates.append(Path.cwd() / relative)

        for path in candidates:
            try:
                text = path.read_text(encoding="utf-8")
                logger.info("UPilot template resource loaded: path=%s", path)
                return text, path
            except OSError:
                continue
        raise FileNotFoundError(f"UPilot template resource is missing: {relative}")

    @classmethod
    def _read_agent_rules_template(cls, manifest: dict) -> tuple[str, Path]:
        return cls._read_template_resource(
            _SKILL_TEMPLATE_ROOT_RELATIVE / manifest["templates"]["agentRules"]
        )

    @classmethod
    def _read_template_manifest(cls) -> tuple[dict, Path]:
        text, path = cls._read_template_resource(_TEMPLATE_MANIFEST_RELATIVE)
        try:
            manifest = json.loads(text)
            if (
                not isinstance(manifest, dict)
                or manifest.get("schemaVersion") != 1
                or type(manifest.get("agentRulesVersion")) is not int
                or manifest["agentRulesVersion"] <= 0
                or type(manifest.get("skillPackVersion")) is not int
                or manifest["skillPackVersion"] <= 0
                or type(manifest.get("defaultHttpPort")) is not int
                or not 1 <= manifest["defaultHttpPort"] <= 65535
            ):
                raise ValueError("invalid required fields")
            templates = manifest.get("templates")
            if not isinstance(templates, dict) or set(templates) != {"agentRules", "skill", "openai"}:
                raise ValueError("invalid template mappings")
            relative_paths = []
            for key, value in templates.items():
                relative = Path(value) if isinstance(value, str) else Path()
                if not value or relative.is_absolute() or ".." in relative.parts:
                    raise ValueError(f"invalid {key} template path")
                relative_paths.append(relative.as_posix().lower())
                cls._read_template_resource(_SKILL_TEMPLATE_ROOT_RELATIVE / relative)
            if len(set(relative_paths)) != len(relative_paths):
                raise ValueError("template paths must be unique")
        except (ValueError, json.JSONDecodeError) as exc:
            raise ValueError(f"UPilot template manifest is invalid: {path}: {exc}") from exc
        return manifest, path

    def _build_agent_rules_block(self, project_root: Path) -> str:
        project_path = str(project_root)
        version = self._upilot_package_version()
        generated_at = _utc_iso()
        manifest, manifest_path = self._read_template_manifest()
        template, template_path = self._read_agent_rules_template(manifest)
        rules_version = int(manifest["agentRulesVersion"])
        parent_agent_rules_path = self._find_parent_agent_rules_relative_path(project_root)
        http_port = int(CONFIG.http_port)
        mcp_url = f"http://127.0.0.1:{http_port}/mcp"
        health_url = f"http://127.0.0.1:{http_port}/health"
        logger.info(
            "Rendering UPilot Agent rules template: template=%s manifest=%s target=%s rulesVersion=%s "
            "upilotPackageVersion=%s projectPath=%s generatedAt=%s parentAgentRulesPath=%s mcpUrl=%s healthUrl=%s",
            template_path,
            manifest_path,
            project_root / "AGENTS.md",
            rules_version,
            version,
            project_path,
            generated_at,
            parent_agent_rules_path,
            mcp_url,
            health_url,
        )
        rendered = (
            template
            .replace("{{rulesVersion}}", str(rules_version))
            .replace("{{skillPackVersion}}", str(manifest["skillPackVersion"]))
            .replace("{{upilotPackageVersion}}", version)
            .replace("{{projectPath}}", project_path)
            .replace("{{generatedAt}}", generated_at)
            .replace("{{parentAgentRulesPath}}", parent_agent_rules_path)
            .replace("{{mcpUrl}}", mcp_url)
            .replace("{{healthUrl}}", health_url)
            .replace("\r\n", "\n")
            .replace("\r", "\n")
            .strip()
        )
        if "{{" in rendered or "}}" in rendered:
            raise ValueError("UPilot Agent rules template contains an unresolved or malformed token")
        return f"{_UPILOT_BLOCK_START}\n{rendered}\n{_UPILOT_BLOCK_END}"

    @staticmethod
    def _find_parent_agent_rules_relative_path(project_root: Path) -> str:
        try:
            root = project_root.resolve()
            for parent in root.parents:
                candidate = parent / "AGENTS.md"
                if candidate.exists():
                    try:
                        return os.path.relpath(candidate.resolve(), root).replace(os.sep, "/")
                    except ValueError:
                        return str(candidate.resolve()).replace("\\", "/")
        except OSError:
            return "(none)"
        return "(none)"

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
        return await self._agent_rules_adapter(sync=False, apply=False)

    async def agent_rules_install(self, apply: bool = False) -> ToolResponse:
        return await self._agent_rules_adapter(sync=True, apply=apply)

    async def _agent_integrations(self, *, sync: bool, apply: bool = False, scope: str = "all") -> ToolResponse:
        request_id = new_id("req")
        # Unity resolves its installed UPM package. Never fall back to Server/EXE template resources.
        response = await self.dispatcher.call(request_id,
            "agent.integrations.sync" if sync else "agent.integrations.check",
            {"apply": apply, "scope": scope})
        if not response.ok:
            if response.error and response.error.code in {"UNKNOWN_COMMAND", "UNSUPPORTED_COMMAND", "COMMAND_NOT_FOUND"}:
                return fail(request_id, "AGENT_INTEGRATIONS_UNSUPPORTED",
                            "Upgrade the Unity Bridge to support agent.integrations; no file-write fallback is used.")
            return response
        data = response.data
        if not isinstance(data, dict) or data.get("schemaVersion") != 1 or not isinstance(data.get("targets"), list):
            return fail(request_id, "AGENT_INTEGRATIONS_UNSUPPORTED",
                        "The Unity Bridge must support agent.integrations schema v1; no file-write fallback is used.")
        if not data.get("ok"):
            return fail(request_id, "AGENT_INTEGRATIONS_FAILED", data.get("error") or data.get("status"), data)
        return response

    async def agent_integrations_check(self) -> ToolResponse:
        return await self._agent_integrations(sync=False)

    async def agent_integrations_sync(self, apply: bool = False) -> ToolResponse:
        return await self._agent_integrations(sync=True, apply=apply)

    async def _agent_rules_adapter(self, *, sync: bool, apply: bool) -> ToolResponse:
        response = await self._agent_integrations(sync=sync, apply=apply, scope="shared")
        report = response.data if isinstance(response.data, dict) else {}
        targets = report.get("targets", [])
        if not targets:
            return response
        target = targets[0]
        current = self._parse_rules_metadata(target.get("currentBlock", ""))
        recommended = self._parse_rules_metadata(target.get("recommendedBlock", ""))
        marker_error = target.get("error", "") if "marker" in target.get("error", "").lower() else ""
        project = report.get("renderContext", {}).get("projectPath", "")
        data = {
            "action": "CheckAgentRules", "rulesPath": target["path"],
            "hasUpilotBlock": target["hasUpilotBlock"], "needsImport": not target["hasUpilotBlock"],
            "needsUpdate": target["needsUpdate"] and not marker_error,
            "currentRulesVersion": current.get("rulesVersion", ""),
            "recommendedRulesVersion": str(report["agentRulesVersion"]),
            "currentUpilotPackageVersion": current.get("upilotPackageVersion", ""),
            "recommendedUpilotPackageVersion": recommended.get("upilotPackageVersion", ""),
            "projectPathMatches": current.get("projectPath", project).replace("\\", "/") == project.replace("\\", "/"),
            "diffSummary": target["reasons"], "recommendedBlock": target["recommendedBlock"], "markerError": marker_error,
        }
        if sync:
            data.update(dryRun=not apply, applied=apply and response.ok)
            if apply and response.ok:
                data.update(fileSha256=target["afterSha256"], installedRulesVersion=str(report["agentRulesVersion"]),
                            installedUpilotPackageVersion=recommended.get("upilotPackageVersion", ""))
        if not response.ok:
            if marker_error and not apply:
                return ok(new_id("req"), data)
            return fail(new_id("req"), "AGENT_RULES_MARKER_ERROR" if marker_error else "AGENT_INTEGRATIONS_FAILED",
                        marker_error or target.get("error") or report.get("status", "failed"), data)
        return ok(new_id("req"), data)

    async def operation_validate(
        self, job_spec: dict | None, inspect_reflection: bool = True, strict_tool_registry: bool = True,
    ) -> ToolResponse:
        request_id = new_id("req")
        errors: list[dict] = []
        warnings: list[dict] = []
        if not isinstance(job_spec, dict):
            return fail(request_id, "INVALID_JOB_SPEC", "jobSpec must be an object.", {
                "valid": False, "errors": [{"path": "jobSpec", "code": "type", "message": "Expected an object."}]
            })

        job_spec = copy.deepcopy(job_spec)
        normalized = dict(job_spec)
        step_plan = job_spec.get("stepPlan")
        has_step_plan = "stepPlan" in job_spec
        step_budget = 0.0
        if has_step_plan:
            if any(field in job_spec for field in ("startCall", "statusCall", "cancelCall")):
                errors.append({"path": "stepPlan", "code": "STEP_PLAN_CALLS_CONFLICT",
                               "message": "stepPlan and handwritten calls are mutually exclusive."})
            if not isinstance(step_plan, dict):
                errors.append({"path": "stepPlan", "code": "STEP_PLAN_INVALID", "message": "Expected a version 1 plan object."})
            else:
                capture = job_spec.get("consoleCapture")
                items = step_plan.get("steps")
                plan_capture = isinstance(items, list) and any(
                    isinstance(item, dict) and item.get("stepId") == "upilot.console_capture_start" for item in items)
                operation_capture = isinstance(capture, dict) and capture.get("enabled") is True
                if plan_capture and (not isinstance(capture, dict) or capture.get("enabled") is not False):
                    errors.append({"path": "consoleCapture.enabled", "code": "STEP_CAPTURE_OWNERSHIP_CONFLICT",
                                   "message": "Plan-owned Capture requires consoleCapture.enabled=false."})
                if step_plan.get("logPolicy") and not operation_capture and not plan_capture:
                    errors.append({"path": "consoleCapture.enabled", "code": "STEP_CAPTURE_REQUIRED",
                                   "message": "A step logPolicy requires one plan-owned or Operation-owned Capture."})
                # Unity owns discovery and per-step argument validation. Never create Capture here.
                try:
                    plan_json = json.dumps(step_plan, ensure_ascii=False, allow_nan=False)
                except (TypeError, ValueError):
                    return fail(request_id, "INVALID_JOB_SPEC", "stepPlan must contain finite JSON data.", {
                        "valid": False, "errors": errors + [{"path": "stepPlan", "code": "STEP_PLAN_INVALID",
                                                          "message": "Non-finite or non-JSON values are unsupported."}]
                    })
                response = await self.dispatcher.call(request_id, "automation.steps.validate",
                                                      {"planJson": plan_json})
                data = response.data if isinstance(response.data, dict) else {}
                if not response.ok:
                    errors.append({"path": "stepPlan", "code": response.error.code if response.error else "STEP_VALIDATION_FAILED",
                                   "message": response.error.message if response.error else "Step validation failed."})
                elif data.get("ok") is not True:
                    errors.extend({"path": f"stepPlan.steps[{item.get('index', -1)}]",
                                   "code": item.get("code", "STEP_PLAN_INVALID"), "message": item.get("message", "")}
                                  for item in data.get("diagnostics", []) if item.get("severity", "error") == "error")
                    if not errors:
                        errors.append({"path": "stepPlan", "code": "STEP_PLAN_INVALID", "message": "Plan validation failed."})
                else:
                    step_budget = float(data.get("budgetSeconds") or 0)
                    if not math.isfinite(step_budget) or step_budget <= 0:
                        errors.append({"path": "stepPlan", "code": "STEP_BUDGET_INVALID", "message": "Bridge returned no valid plan budget."})
        if not isinstance(job_spec.get("failOnUnexpectedPlayModeExit", False), bool):
            errors.append({"path": "failOnUnexpectedPlayModeExit", "code": "type", "message": "Expected a boolean."})
        normalized["displayName"] = str(job_spec.get("displayName") or "Unity operation")
        normalized["timeoutSec"] = job_spec.get("timeoutSec", max(300, step_budget))
        normalized["pollIntervalSec"] = job_spec.get("pollIntervalSec", 3)
        cleanup = job_spec.get("cleanup", {})
        if not isinstance(cleanup, dict):
            errors.append({"path": "cleanup", "code": "type", "message": "Expected an object."})
        else:
            cleanup = dict(cleanup)
            cleanup.setdefault("requireEditMode", False)
            cleanup.setdefault("timeoutSec", 30)
            if not isinstance(cleanup["requireEditMode"], bool):
                errors.append({"path": "cleanup.requireEditMode", "code": "type", "message": "Expected a boolean."})
            value = cleanup["timeoutSec"]
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                errors.append({"path": "cleanup.timeoutSec", "code": "range", "message": "Expected a finite positive number."})
            normalized["cleanup"] = cleanup
            if cleanup.get("requireEditMode") is True:
                normalized["_editorVerificationExpected"] = (
                    "After business terminal, cleanup verifies authoritative "
                    "EditMode readiness before declaring editorTerminal=true."
                )
        for field in ("timeoutSec", "pollIntervalSec"):
            value = normalized[field]
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                errors.append({"path": field, "code": "range", "message": "Expected a finite positive number."})
            elif field == "timeoutSec" and has_step_plan and value < step_budget:
                errors.append({"path": field, "code": "STEP_BUDGET_TOO_SMALL",
                               "message": f"Operation timeout must cover the plan, cleanup and evidence budget ({step_budget} seconds)."})

        for field, required in (("startCall", True), ("statusCall", True), ("cancelCall", False)):
            if has_step_plan:
                continue
            call = job_spec.get(field)
            if call is None and not required:
                continue
            if not isinstance(call, dict):
                errors.append({"path": field, "code": "type", "message": f"{field} must be an object."})
                continue
            await self._validate_operation_call(
                field, call, errors, warnings, inspect_reflection, strict_tool_registry,
            )

        mapping = job_spec.get("terminalStatusMapping")
        if mapping is not None and not isinstance(mapping, dict):
            errors.append({"path": "terminalStatusMapping", "code": "type", "message": "terminalStatusMapping must be an object."})
        elif isinstance(mapping, dict):
            for key, value in mapping.items():
                if not isinstance(value, (str, list, tuple)):
                    errors.append({"path": f"terminalStatusMapping.{key}", "code": "type", "message": "Status mapping values must be a string or array."})

        rules = job_spec.get("artifactRules")
        if rules is not None and not isinstance(rules, dict):
            errors.append({"path": "artifactRules", "code": "type", "message": "artifactRules must be an object."})
        elif isinstance(rules, dict):
            fields = rules.get("fromStatusFields")
            if fields is not None and (not isinstance(fields, list) or not all(isinstance(item, str) for item in fields)):
                errors.append({"path": "artifactRules.fromStatusFields", "code": "type", "message": "fromStatusFields must be an array of field names."})
            field_kinds = rules.get("fieldKinds")
            if field_kinds is not None and (not isinstance(field_kinds, dict) or not all(
                    isinstance(key, str) and str(value).lower() in {"file", "metadata", "sha256", "bytes"}
                    for key, value in field_kinds.items())):
                errors.append({"path": "artifactRules.fieldKinds", "code": "type", "message": "fieldKinds must map field names to file, metadata, sha256, or bytes."})
            elif field_kinds is None:
                rules = dict(rules)
                rules["_fieldKindsDefault"] = "file for absolute/relative paths, metadata otherwise"
                normalized["artifactRules"] = rules

        for field in ("resultPath", "statusPath", "phasePath", "errorPath", "detailPath", "progressPath", "failureSignaturePath", "artifactsPath"):
            value = job_spec.get(field)
            if value is not None and (not isinstance(value, str) or not value.strip()):
                errors.append({"path": field, "code": "type", "message": f"{field} must be a non-empty path string."})

        valid = not errors
        data = {
            "valid": valid,
            "normalizedJobSpec": normalized,
            "errors": errors,
            "warnings": warnings,
            "nextAction": "Call unity_operation_start with normalizedJobSpec." if valid else "Fix the reported jobSpec fields and validate again.",
        }
        return ok(request_id, data) if valid else fail(request_id, "INVALID_JOB_SPEC", "jobSpec validation failed.", data)

    async def _validate_operation_call(
        self, field: str, call: dict, errors: list[dict], warnings: list[dict], inspect_reflection: bool,
        strict_tool_registry: bool,
    ) -> None:
        kind = str(call.get("kind") or call.get("type") or "").strip().lower()
        if kind not in {"reflection", "mcp", "tool", "mcp_tool", "menu", "native", "route", "bridge"}:
            errors.append({"path": f"{field}.kind", "code": "unsupported", "message": f"Unsupported operation call kind: {kind or '(empty)'}"})
            return
        self._validate_operation_placeholders(field, call, errors)
        if kind == "reflection":
            type_name = str(call.get("typeName") or call.get("type_name") or "").strip()
            method_name = str(call.get("methodName") or call.get("method_name") or "").strip()
            if not type_name:
                errors.append({"path": f"{field}.typeName", "code": "required", "message": "typeName is required."})
            if not method_name:
                errors.append({"path": f"{field}.methodName", "code": "required", "message": "methodName is required."})
            parameters = call.get("parameters", [])
            if not isinstance(parameters, list):
                errors.append({"path": f"{field}.parameters", "code": "type", "message": "parameters must be an array."})
            if inspect_reflection and type_name and method_name:
                result = await self.reflection_find(type_name=type_name, method_name=method_name)
                if not result.ok:
                    errors.append({"path": field, "code": "reflection_not_found", "message": result.error.message if result.error else "Reflection entry point was not found."})
        elif kind in {"mcp", "tool", "mcp_tool"}:
            tool_name = str(call.get("toolName") or call.get("name") or "").strip()
            if not tool_name:
                errors.append({"path": f"{field}.toolName", "code": "required", "message": "toolName is required."})
            elif strict_tool_registry and REGISTRY.resolve(tool_name) is None:
                errors.append({"path": f"{field}.toolName", "code": "tool_not_found", "message": f"Registered tool not found: {tool_name}"})
            tool_args = call.get("toolArgs", call.get("args", call.get("arguments", {})))
            if not isinstance(tool_args, dict):
                errors.append({"path": f"{field}.toolArgs", "code": "type", "message": "toolArgs must be an object."})
        elif kind == "menu" and not str(call.get("commandName") or call.get("menuPath") or "").strip():
            errors.append({"path": f"{field}.commandName", "code": "required", "message": "commandName or menuPath is required."})
        elif kind in {"native", "route", "bridge"}:
            if not str(call.get("route") or call.get("command") or "").strip():
                errors.append({"path": f"{field}.route", "code": "required", "message": "route or command is required."})
            if not isinstance(call.get("payload", {}), dict):
                errors.append({"path": f"{field}.payload", "code": "type", "message": "payload must be an object."})

    @staticmethod
    def _validate_operation_placeholders(path: str, value: object, errors: list[dict]) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                TaskDomainService._validate_operation_placeholders(f"{path}.{key}", child, errors)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                TaskDomainService._validate_operation_placeholders(f"{path}[{index}]", child, errors)
        elif isinstance(value, str) and "${" in value:
            if not (value.startswith("${") and value.endswith("}")):
                errors.append({"path": path, "code": "placeholder_format", "message": "Placeholders must occupy the complete string value."})
                return
            parts = value[2:-1].strip().split(".")
            if len(parts) < 2 or parts[0] not in {"start", "status", "operation"} or any(not part for part in parts):
                errors.append({"path": path, "code": "placeholder_path", "message": f"Unsupported placeholder: {value}"})

    async def operation_start(self, job_spec: dict | None) -> ToolResponse:
        self._recover_operations()
        request_id = new_id("req")
        validation = await self.operation_validate(
            job_spec, inspect_reflection=False, strict_tool_registry=False,
        )
        if not validation.ok:
            detail = validation.error.detail if validation.error else {}
            return fail(request_id, "INVALID_JOB_SPEC", "jobSpec validation failed.", detail)
        job_spec = dict((validation.data or {}).get("normalizedJobSpec") or job_spec or {})
        if "stepPlan" in job_spec:
            # Materialize only after validation. JSON text is opaque to placeholder expansion.
            job_spec["startCall"] = {"kind": "bridge", "route": "automation.steps.start", "payload": {
                "planJson": json.dumps(job_spec["stepPlan"], ensure_ascii=False, allow_nan=False),
                "operationId": "${operation.operationId}",
            }}
            for field, route in (("statusCall", "state"), ("cancelCall", "cancel")):
                job_spec[field] = {"kind": "bridge", "route": "automation.steps." + route, "payload": {
                    "operationId": "${operation.operationId}", "runId": "${start.runId}",
                }}
            job_spec["terminalStatusMapping"] = {
                "success": ["Succeeded", "SucceededWithWarnings"],
                "failure": ["Failed"], "canceled": ["Canceled"], "timeout": ["TimedOut"],
            }
        start_call = job_spec["startCall"]

        operation_id = new_id("op")
        now = now_ms()
        timeout_sec = float(job_spec.get("timeoutSec") or 300)
        state = {
            "projectPath": str(self._project_root()),
            "durable": True,
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
            "startAttemptCount": 0,
            "cancelRequested": False,
            "cancelAccepted": False,
            "cancelRequestedAt": 0,
            "cancelAttemptCount": 0,
            "cleanupPending": False,
            "editorVerification": "not_requested",
            "startedAt": now,
            "updatedAt": now,
            "endedAt": 0,
            "timeoutSec": timeout_sec,
            "pollIntervalSec": float(job_spec.get("pollIntervalSec") or 3),
            "lastStatusData": {},
            "lastStatusAt": 0,
            "changes": [],
            "milestones": [],
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
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))

        capture = job_spec.get("consoleCapture") if isinstance(job_spec.get("consoleCapture"), dict) else {}
        if capture.get("enabled"):
            state["consoleCapture"].update({
                "ownerId": operation_id,
                "ownerToken": secrets.token_urlsafe(32),
                "requestKey": operation_id,
            })
            state["captureIntentSent"] = True
            if not self._save_operation(state):
                return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
            try:
                capture_result = await self._start_owned_operation_capture(
                    state,
                    title=str(capture.get("title") or state["displayName"]),
                    path=str(capture.get("path") or ""),
                    include_stack_trace=bool(capture.get("includeStackTrace", True)),
                    exclude_upilot=bool(capture.get("excludeUPilot", True)),
                    clear_unity_console=bool(capture.get("clearUnityConsole", False)),
                )
            except Exception as exc:
                state.update(status="RecoveryRequired", phase="capture_identity_unknown",
                             error=str(exc), cleanupPending=True,
                             nextAction="Inspect capture sessions for the original start intent; business was not started. Do not replay capture start.")
                self._save_operation(state)
                return fail(request_id, "OPERATION_CAPTURE_START_UNKNOWN", str(exc), self._public_operation_state(state))
            state["consoleCapture"]["start"] = self._tool_response_summary(capture_result)
            if capture_result.ok and isinstance(capture_result.data, dict):
                session_data = capture_result.data.get("session") if isinstance(capture_result.data.get("session"), dict) else {}
                session_data = self._redact_operation_secrets(session_data)
                state["consoleCapture"]["sessionId"] = str(
                    capture_result.data.get("sessionId") or session_data.get("sessionId") or ""
                )
                state["consoleCapture"]["nextSequence"] = int(
                    capture_result.data.get("nextSequence") or session_data.get("nextSequence") or -1
                )
                state["consoleCapture"]["session"] = session_data
            if not capture_result.ok or not state["consoleCapture"].get("sessionId"):
                state.update(status="RecoveryRequired", phase="capture_identity_unknown", cleanupPending=True,
                             error="Required console capture did not establish a session; business was not started.",
                             failureSignature="OperationCaptureStartFailed",
                             nextAction="Inspect original capture intent and active sessions; do not replay capture start.")
                self._save_operation(state)
                return fail(request_id, "OPERATION_CAPTURE_START_FAILED", state["error"], self._public_operation_state(state))

        if "stepPlan" in job_spec:
            start_call["payload"]["captureSessionId"] = state["consoleCapture"].get("sessionId", "")
        state["startIntentSent"] = True
        state["startAttemptCount"] = int(state.get("startAttemptCount") or 0) + 1
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        try:
            start_result = await self._operation_invoke(self._resolve_operation_call(start_call, state), operation_id)
        except Exception as exc:
            state.update(status="RecoveryRequired", phase="start_result_unknown", error=str(exc),
                         nextAction="Inspect original operation evidence; start was not replayed.")
            self._save_operation(state)
            return fail(request_id, "OPERATION_START_UNKNOWN", str(exc), self._public_operation_state(state))
        self._accumulate_timing(state, start_result)
        state["startResult"] = self._tool_response_summary(start_result)
        if not start_result.ok:
            state.update(status="RecoveryRequired", phase="start_result_unknown",
                         error=start_result.error.message if start_result.error else "startCall failed",
                         nextAction="A failed tool response does not prove that business never started. Inspect original start evidence; do not replay.")
            self._save_operation(state)
            return fail(request_id, "OPERATION_START_UNKNOWN", state["error"], self._public_operation_state(state))

        payload, payload_error, payload_diagnostic = self._operation_adapt_payload(start_result, start_call, job_spec)
        if payload_error:
            state["parseDiagnostic"] = payload_diagnostic
            state["status"] = "RecoveryRequired"
            state["phase"] = "StartResultInvalid"
            state["error"] = payload_error
            state["failureSignature"] = "OperationResultInvalid"
            state["nextAction"] = "Inspect the original start response; business may have started. Do not replay start."
            self._finalize_operation_timing(state)
            self._save_operation(state)
            return fail(request_id, "OPERATION_RESULT_INVALID", payload_error, self._public_operation_state(state))
        if payload:
            state["startData"] = payload
            self._merge_operation_status(state, payload)
        state["resolvedStatusCall"] = self._resolve_operation_call(job_spec["statusCall"], state)
        status_spec = json.dumps(job_spec["statusCall"], sort_keys=True)
        state["recoveryIdentityEstablished"] = bool(
            "${start." in status_spec or "${operation.operationId}" in status_spec
        ) and "${" not in json.dumps(state["resolvedStatusCall"], sort_keys=True)
        state["startEstablished"] = True
        if self._operation_call_has_missing_identity(job_spec["statusCall"], state["resolvedStatusCall"]):
            state.update(status="RecoveryRequired", phase="status_identity_unknown", endedAt=0,
                         nextAction="The original start did not resolve status identity. Inspect its response; do not replay start or query an unspecified job.")
            self._save_operation(state)
            return ok(request_id, self._public_operation_state(state))
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        mapping = job_spec.get("terminalStatusMapping")
        if _is_terminal_status(str(state["status"]), mapping):
            state["businessCleanupPending"] = _operation_cleanup_pending(payload)
            if _is_success_status(str(state["status"]), mapping):
                state["status"] = "Succeeded"
            elif str(state["status"]).strip().lower() in {"canceled", "cancelled", "aborted"}:
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
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        self._resume_operation_observer(state)
        return ok(request_id, self._public_operation_state(state))

    @_serialized_operation
    async def operation_status(
        self, operation_id: str, detail_level: str = "summary", max_tail_chars: int = 2000,
        include_raw_state: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
        if not self._operation_project_matches(state):
            return self._operation_project_mismatch(request_id, state)
        if state.get("status") == "RecoveryRequired":
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))
        if not state.get("startEstablished"):
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))
        if state.get("endedAt"):
            mapping = state.get("jobSpec", {}).get("terminalStatusMapping")
            if _is_success_status(str(state.get("status") or ""), mapping):
                state["lastObservationError"] = ""
                state["nextAction"] = ""
            self._finalize_operation_timing(state)
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

        if state.get("businessTerminal"):
            await self._operation_stop_console_capture(state)
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

        status_call = state.get("resolvedStatusCall") or self._resolve_operation_call(state["jobSpec"].get("statusCall"), state)
        if self._operation_call_requires_unity(status_call) and not self._operation_has_unity_session():
            deadline = int(state.get("startedAt") or 0) + int(float(state.get("timeoutSec") or 0) * 1000)
            if not state.get("timedOut") and deadline > 0 and now_ms() >= deadline:
                state.update(timedOut=True, timeoutAt=now_ms(), cleanupPending=True)
            state.update(
                phase="JobTimeoutAwaitingRecovery" if state.get("timedOut") else "Recovering",
                lastObservationError="Unity Bridge is disconnected; status was not dispatched.",
                nextAction="Reconnect the same Unity project, then query this operationId again. Do not replay start.",
            )
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

        if not state.get("timedOut") and now_ms() >= int(state.get("startedAt") or 0) + int(float(state.get("timeoutSec") or 0) * 1000):
            state.update(timedOut=True, phase="JobTimeoutAwaitingBusiness",
                         timeoutAt=now_ms(), cleanupPending=True)
            if not self._save_operation(state):
                return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
            # This caller already holds the job lock. Use the same cancellation
            # adapter without re-entering it; intent is persisted before effects.
            if isinstance(state["jobSpec"].get("cancelCall"), dict):
                await TaskDomainService.operation_cancel.__wrapped__(self, operation_id)
                if state.get("businessTerminal") or state.get("endedAt"):
                    return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

        try:
            result = await self._operation_invoke(status_call, operation_id)
        except Exception as exc:
            state.update(phase="Recovering", lastObservationError=str(exc))
            return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))
        self._accumulate_timing(state, result)
        state["lastStatusAt"] = now_ms()
        if not result.ok:
            step_initializing = (
                result.error is not None
                and result.error.code == "STEP_SERVICE_INITIALIZING"
                and status_call.get("kind") == "bridge"
                and status_call.get("route") == "automation.steps.state"
            )
            if result.error and (step_initializing or result.error.code in {
                "COMMAND_TIMEOUT", "UNITY_NOT_CONNECTED", "DISCONNECTED", "EDITOR_BUSY"
            }):
                state.update(phase="Recovering", lastObservationError=result.error.message)
                return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))
            state["status"] = "RecoveryRequired"
            state["phase"] = "StatusCallFailed"
            state["error"] = result.error.message if result.error else "statusCall failed"
            state["failureSignature"] = result.error.code if result.error else "StatusCallFailed"
            state["nextAction"] = "Status observation failed; inspect original operation identity. Do not replay start."
            self._finalize_operation_timing(state)
            return fail(request_id, "OPERATION_STATUS_FAILED", state["error"], self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

        payload, payload_error, payload_diagnostic = self._operation_adapt_payload(result, status_call, state["jobSpec"])
        if payload_error:
            state["parseDiagnostic"] = payload_diagnostic
            state["status"] = "RecoveryRequired"
            state["phase"] = "StatusResultInvalid"
            state["error"] = payload_error
            state["failureSignature"] = "OperationResultInvalid"
            state["nextAction"] = "Status could not be parsed; business completion and cleanup remain unknown."
            self._finalize_operation_timing(state)
            return fail(request_id, "OPERATION_RESULT_INVALID", payload_error, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))
        state["lastObservationError"] = ""
        state["nextAction"] = ""
        if payload:
            mismatch = self._operation_identity_mismatch(state, payload)
            if mismatch:
                state.update(status="RecoveryRequired", phase="status_identity_mismatch", error=mismatch,
                             nextAction="Inspect the original business identity; no terminal or cleanup was accepted.")
                return fail(request_id, "OPERATION_IDENTITY_MISMATCH", mismatch, self._public_operation_state(state))
            self._merge_operation_status(state, payload)
        mapping = state["jobSpec"].get("terminalStatusMapping")
        if state["jobSpec"].get("failOnUnexpectedPlayModeExit"):
            observed = await self.mcp_status(force_fresh=True, include_capabilities=False)
            transition = (observed.data or {}).get("playModeTransition") or {}
            editor = (observed.data or {}).get("executionState") or {}
            if (observed.ok and editor.get("authoritative") is True and editor.get("isStale") is False
                    and transition and int(transition.get("at") or 0) >= state["startedAt"]):
                state["playModeTransition"] = transition
                if (transition.get("toState") in ("exitingPlay", "edit")
                        and transition.get("fromState") in ("play", "exitingPlay")
                        and transition.get("operationId") != operation_id
                        and not _is_terminal_status(str(state["status"]), mapping)):
                    state.update(status="Failed", phase="UnexpectedPlayModeExit", error="PlayMode exited without a correlated operation request.",
                                 failureSignature="OperationUnexpectedPlayModeExit")
        cleanup_pending = _operation_cleanup_pending(payload)
        state["cleanupPending"] = cleanup_pending
        if _is_terminal_status(str(state["status"]), mapping):
            state["businessCleanupPending"] = cleanup_pending
            if state.get("timedOut") or state.get("cancelRequested"):
                state["businessCleanupPending"] = cleanup_pending or not _operation_cleanup_state_is_explicit(payload)
            if _is_success_status(str(state["status"]), mapping):
                state["status"] = "Succeeded"
            elif str(state["status"]).strip().lower() in {"canceled", "cancelled", "aborted"}:
                state["status"] = "Canceled"
            elif str(state["status"]).strip().lower() in {"timeout", "timedout"}:
                state["status"] = "Timeout"
            else:
                state["status"] = "Failed"
            state["endedAt"] = now_ms()
            self._mark_repeat_failure(state)
            await self._operation_stop_console_capture(state)
            state["_deferredArtifactCollection"] = True
        elif state.get("cancelRequested"):
            state["status"] = "Stopping"
            state["phase"] = "Cleanup" if cleanup_pending else (state.get("phase") or "Stopping")
        if state.get("timedOut") and not state.get("businessTerminal"):
            state.update(phase="JobTimeoutAwaitingBusiness", cleanupPending=True)
        state["updatedAt"] = now_ms()
        self._finalize_operation_timing(state)
        return ok(request_id, self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state))

    async def operation_wait(
        self,
        operation_id: str,
        timeout_s: float | None = None,
        poll_interval_s: float | None = None,
        return_on_suspected_stuck: bool = True,
        timeout_sec: float | None = None,
        poll_interval_sec: float | None = None,
        detail_level: str = "summary",
        max_tail_chars: int = 2000,
        include_raw_state: bool = False,
    ) -> ToolResponse:
        self._recover_operations()
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
            status_result = await self.operation_status(operation_id, detail_level="summary", max_tail_chars=max_tail_chars)
            state = self._operations.get(operation_id, state)
            # A rejected observation (notably a project-boundary or parse
            # failure) is not a running operation.  Do not hide it behind a
            # wait-window timeout or issue another status call.
            if not status_result.ok:
                self._finalize_operation_timing(state)
                return status_result
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
                payload = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
                payload["changes"] = changes
                return ok(request_id, payload) if status_result.ok else status_result

            if suspected_stuck_sec > 0:
                phase_elapsed = float((state.get("lastStatusData") or {}).get("phaseElapsedSec") or 0)
                if phase_elapsed >= suspected_stuck_sec and return_on_suspected_stuck:
                    state["suspectedStuck"] = True
                    self._finalize_operation_timing(state)
                    payload = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
                    payload["changes"] = changes
                    payload["recommendation"] = "Inspect operation status, Console capture, and artifacts before retrying."
                    return ok(request_id, payload)

            if time.monotonic() >= deadline:
                self._finalize_operation_timing(state)
                payload = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
                payload["changes"] = changes
                payload["terminal"] = False
                payload["waitWindowElapsed"] = True
                payload["waitWindowSec"] = timeout
                payload["recommendation"] = "Call unity_operation_wait again or inspect status; the operation is still running."
                return ok(request_id, payload)

            last_poll = float(state.get("_lastPollMono") or 0)
            if last_poll:
                gap = max(0, int((time.monotonic() - last_poll - interval) * 1000))
                state["timing"]["agentPollGapMs"] += gap
            state["_lastPollMono"] = time.monotonic()
            await asyncio.sleep(min(interval, max(0.1, deadline - time.monotonic())))

    @_serialized_operation
    async def operation_cancel(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
        if not self._operation_project_matches(state):
            return self._operation_project_mismatch(request_id, state)
        if state.get("endedAt"):
            return ok(request_id, self._public_operation_state(state))
        if not state.get("startEstablished"):
            return fail(request_id, "OPERATION_RECOVERY_REQUIRED", "Start identity is unknown; cancellation was not dispatched.", self._public_operation_state(state))
        if state.get("businessTerminal"):
            await self._operation_stop_console_capture(state)
            return ok(request_id, self._public_operation_state(state))
        if state.get("cancelAccepted") or state.get("cancelIntentSent"):
            return ok(request_id, self._public_operation_state(state))
        cancel_call = state["jobSpec"].get("cancelCall")
        if not isinstance(cancel_call, dict):
            return fail(request_id, "CANCEL_UNSUPPORTED", "This operation has no cancelCall.", self._public_operation_state(state))
        state["cancelIntentSent"] = True
        state["cancelRequested"] = True
        state["cancelRequestedAt"] = state.get("cancelRequestedAt") or now_ms()
        state["cancelAttemptCount"] = int(state.get("cancelAttemptCount") or 0) + 1
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        try:
            result = await self._operation_invoke(self._resolve_operation_call(cancel_call, state), operation_id)
        except Exception as exc:
            state.update(phase="CancelResultUnknown", error=str(exc))
            return fail(request_id, "OPERATION_CANCEL_UNKNOWN", str(exc), self._public_operation_state(state))
        self._accumulate_timing(state, result)
        state["cancelResult"] = self._tool_response_summary(result)
        state["cancelRequested"] = True
        state["cancelRequestedAt"] = state.get("cancelRequestedAt") or now_ms()
        state["cancelAccepted"] = bool(result.ok)
        cancel_payload, cancel_payload_error, cancel_payload_diagnostic = self._operation_adapt_payload(result, cancel_call, state["jobSpec"])
        cleanup_state_explicit = _operation_cleanup_state_is_explicit(cancel_payload)
        cleanup_pending = _operation_cleanup_pending(cancel_payload) if cleanup_state_explicit else bool(result.ok)
        state["cleanupPending"] = cleanup_pending
        cancel_status = _normalize_status(cancel_payload.get("status")) if cancel_payload and "status" in cancel_payload else ""
        cancel_terminal = (
            bool(cancel_status)
            and _is_terminal_status(cancel_status, state["jobSpec"].get("terminalStatusMapping"))
            and cleanup_state_explicit
        )
        if result.ok and cancel_terminal:
            self._merge_operation_status(state, cancel_payload)
            state["businessCleanupPending"] = cleanup_pending
            if cancel_status.lower() in {"canceled", "cancelled", "aborted"}:
                state["status"] = "Canceled"
            elif _is_success_status(cancel_status, state["jobSpec"].get("terminalStatusMapping")):
                state["status"] = "Succeeded"
            else:
                state["status"] = "Failed"
            state["endedAt"] = now_ms()
            await self._operation_stop_console_capture(state)
        else:
            state["status"] = "CancelRequested" if result.ok else "Running"
            state["phase"] = "Stopping" if result.ok else "CancelFailed"
        if not state.get("businessTerminal"):
            state["error"] = "" if result.ok else (result.error.message if result.error else "cancelCall failed")
        if cancel_payload_error:
            state["cancelResultParseError"] = cancel_payload_error
            state["parseDiagnostic"] = cancel_payload_diagnostic
        if not result.ok:
            state["failureSignature"] = result.error.code if result.error else "CancelCallFailed"
        state["updatedAt"] = now_ms()
        self._finalize_operation_timing(state)
        return ok(request_id, self._public_operation_state(state)) if result.ok else fail(request_id, "OPERATION_CANCEL_FAILED", state["error"], self._public_operation_state(state))

    async def operation_collect_artifacts(
        self, operation_id: str, detail_level: str = "summary", max_tail_chars: int = 2000,
        include_raw_state: bool = False,
    ) -> ToolResponse:
        """Snapshot under the operation lock, then perform filesystem I/O outside it."""
        self._recover_operations()
        locks = self.__dict__.setdefault("_operation_locks", {})
        async with locks.setdefault(operation_id, asyncio.Lock()):
            state = self._operations.get(operation_id)
            if state is None:
                return fail(new_id("req"), "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
            if not self._operation_project_matches(state):
                return self._operation_project_mismatch(new_id("req"), state)
            sequence = max(
                int(state.get("artifactCollectionSequence") or 0),
                int(state.get("_artifactCollectionIssuedSequence") or 0),
            ) + 1
            state["_artifactCollectionIssuedSequence"] = sequence
            snapshot = copy.deepcopy(state)

        started = time.monotonic()
        artifacts, errors = await asyncio.to_thread(
            self._collect_operation_artifacts, snapshot, max_tail_chars,
        )

        async with locks.setdefault(operation_id, asyncio.Lock()):
            state = self._operations.get(operation_id)
            if state is None:
                return fail(new_id("req"), "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})
            request_id = new_id("req")
            # A later collection owns the current source snapshot.  A slow earlier
            # read is evidence only for its own response and must never overwrite it.
            if int(state.get("_artifactCollectionIssuedSequence") or 0) != sequence:
                public = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
                public.update({"collectionPersisted": False, "collectionSuperseded": True,
                               "artifactCollectionSequence": int(state.get("artifactCollectionSequence") or 0)})
                return ok(request_id, public)
            candidate = copy.deepcopy(state)
            candidate["artifacts"] = artifacts
            candidate["artifactErrors"] = errors
            candidate["artifactCollectionSequence"] = sequence
            candidate["artifactPersistenceError"] = ""
            candidate["timing"]["artifactReadMs"] += int((time.monotonic() - started) * 1000)
            self._finalize_operation_timing(candidate)
            try:
                self.server.state.save_operation(candidate)
            except Exception as exc:
                public = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
                public.update({"collectionPersisted": False, "artifactPersistenceError": str(exc),
                               "artifactCollectionSequence": int(state.get("artifactCollectionSequence") or 0)})
                return fail(request_id, "OPERATION_PERSIST_FAILED", str(exc), public)
            state.clear()
            state.update(candidate)
            payload = {"operationId": operation_id, "artifacts": artifacts, "artifactErrors": errors,
                       "artifactCollectionSequence": sequence, "collectionPersisted": True,
                       "responseDetailLevel": self._normalize_operation_detail_level(detail_level)}
            if include_raw_state:
                payload["operation"] = self._public_operation_state(state, detail_level, max_tail_chars, True)
            return ok(request_id, payload)

    async def _operation_collect_artifacts_locked(
        self, operation_id: str, detail_level: str = "summary", max_tail_chars: int = 2000,
        include_raw_state: bool = False,
    ) -> ToolResponse:
        """Compatibility path for callers which already own the operation lock."""
        self._recover_operations()
        request_id = new_id("req")
        state = self._operations.get(operation_id)
        if state is None:
            return fail(request_id, "OPERATION_NOT_FOUND", f"Operation not found: {operation_id}", {"operationId": operation_id})

        started = time.monotonic()
        artifacts, errors = await asyncio.to_thread(self._collect_operation_artifacts, copy.deepcopy(state), max_tail_chars)
        return self._commit_operation_artifact_collection(
            state, request_id, artifacts, errors,
            int(state.get("artifactCollectionSequence") or 0) + 1,
            int((time.monotonic() - started) * 1000), detail_level, max_tail_chars, include_raw_state,
        )

    def _collect_operation_artifacts(self, state: dict, max_tail_chars: int) -> tuple[dict[str, object], list[dict]]:
        """Classify an immutable status snapshot and read only declared files."""
        artifacts: dict[str, object] = {}
        errors: list[dict] = []
        source_artifacts = {}
        status_data = state.get("lastStatusData") or {}
        if isinstance(status_data.get("artifacts"), dict):
            source_artifacts.update(status_data["artifacts"])
        rules = state["jobSpec"].get("artifactRules") if isinstance(state["jobSpec"].get("artifactRules"), dict) else {}
        field_kinds = rules.get("fieldKinds") if isinstance(rules.get("fieldKinds"), dict) else {}
        root = Path(str(state.get("projectPath") or self._project_root())).resolve()
        for field in rules.get("fromStatusFields") or []:
            if field in status_data:
                source_artifacts.setdefault(str(field), status_data.get(field))
        # Step attachments are explicit file declarations, not a directory to scan.
        # A compatibility Bridge can expose the same explicitly typed file list.
        attachment_values = source_artifacts.get("attachments")
        declares_files = isinstance(attachment_values, list) and bool(attachment_values) and all(
            isinstance(value, dict) and value.get("kind") == "file" for value in attachment_values
        )
        if "attachments" in source_artifacts and (isinstance(state["jobSpec"].get("stepPlan"), dict) or declares_files):
            attachments = source_artifacts.pop("attachments")
            if not isinstance(attachments, list):
                errors.append({"artifact": "attachments", "error": "ARTIFACT_ATTACHMENTS_INVALID"})
                artifacts["attachments"] = {"kind": "metadata", "value": attachments, "error": "ARTIFACT_ATTACHMENTS_INVALID"}
            else:
                for index, attachment in enumerate(attachments):
                    name = f"attachments[{index}]"
                    if name in source_artifacts:
                        errors.append({"artifact": name, "error": "ARTIFACT_NAME_CONFLICT"})
                        continue
                    if not isinstance(attachment, dict) or attachment.get("kind") != "file":
                        errors.append({"artifact": name, "error": "ARTIFACT_ATTACHMENT_INVALID"})
                        artifacts[name] = {"kind": "metadata", "value": attachment, "error": "ARTIFACT_ATTACHMENT_INVALID"}
                        continue
                    source_artifacts[name] = attachment
        tail_lines = int(rules.get("readReportTailLines") or 0)

        for name, raw_value in source_artifacts.items():
            name = str(name)
            declared_kind = str(field_kinds.get(str(name)) or "").lower()
            explicit_kind = str(raw_value.get("kind") or "").lower() if isinstance(raw_value, dict) else ""
            explicit_path = isinstance(raw_value, dict) and ("path" in raw_value or "file" in raw_value)
            if explicit_kind and explicit_kind not in {"file", "metadata"}:
                artifacts[name] = {"kind": "metadata", "value": raw_value, "error": "ARTIFACT_KIND_INVALID"}
                errors.append({"artifact": name, "error": "ARTIFACT_KIND_INVALID"})
                continue
            # A path/file object is an explicit file declaration even when legacy
            # callers omit kind.  Never let fieldKinds silently recast it as a
            # scalar, because that would suppress the contract's type conflict.
            explicit_value_kind = explicit_kind or ("file" if explicit_path else "")
            if explicit_value_kind and declared_kind and explicit_value_kind != declared_kind:
                artifacts[name] = {
                    "kind": explicit_value_kind, "value": raw_value,
                    "declaredKind": declared_kind, "error": "ARTIFACT_TYPE_CONFLICT",
                }
                errors.append({"artifact": name, "error": "ARTIFACT_TYPE_CONFLICT", "declaredKind": declared_kind})
                continue
            path_text = str(raw_value.get("path") or raw_value.get("file") or "") if isinstance(raw_value, dict) else (str(raw_value) if declared_kind == "file" else "")
            is_file = (
                explicit_value_kind == "file" or declared_kind == "file"
            )
            if not is_file:
                item = {
                    "kind": declared_kind if declared_kind in {"metadata", "sha256", "bytes"} else "metadata",
                    "value": raw_value.get("value") if explicit_kind == "metadata" else raw_value,
                }
                if not declared_kind and not explicit_kind and _is_sha256_text(raw_value):
                    item["error"] = "ARTIFACT_AMBIGUOUS_BARE_SHA256"
                    errors.append({"artifact": name, "error": "ARTIFACT_AMBIGUOUS_BARE_SHA256"})
                artifacts[name] = item
                continue
            if not path_text:
                artifacts[name] = {"kind": "file", "error": "ARTIFACT_FILE_PATH_MISSING"}
                errors.append({"artifact": name, "error": "ARTIFACT_FILE_PATH_MISSING"})
                continue
            path = Path(path_text)
            if not path.is_absolute():
                path = root / path
            try:
                path = path.resolve()
                path.relative_to(root)
            except ValueError:
                artifacts[name] = {"kind": "file", "path": str(path), "error": "ARTIFACT_PATH_OUTSIDE_PROJECT"}
                errors.append({"artifact": name, "path": str(path), "error": "ARTIFACT_PATH_OUTSIDE_PROJECT"})
                continue
            item = {"kind": "file", "path": str(path), "exists": path.exists()}
            if isinstance(raw_value, dict):
                for identity_field in ("instanceId", "artifactKind"):
                    if isinstance(raw_value.get(identity_field), str):
                        item[identity_field] = raw_value[identity_field]
            if path.exists() and path.is_file():
                try:
                    evidence, read_error = _read_stable_artifact(path)
                    if read_error:
                        item["error"] = read_error
                        errors.append({"artifact": name, "path": str(path), "error": read_error})
                        artifacts[str(name)] = item
                        continue
                    item.update(evidence)
                    if isinstance(raw_value, dict):
                        for declared_field, actual_field, mismatch in (
                            ("bytes", "bytes", "ARTIFACT_DECLARED_BYTES_MISMATCH"),
                            ("sha256", "sha256", "ARTIFACT_DECLARED_SHA256_MISMATCH"),
                        ):
                            if declared_field in raw_value and raw_value[declared_field] != evidence[actual_field]:
                                item[f"declared{declared_field[:1].upper()}{declared_field[1:]}"] = raw_value[declared_field]
                                item[f"actual{actual_field[:1].upper()}{actual_field[1:]}"] = evidence[actual_field]
                                item[declared_field] = raw_value[declared_field]
                                item["error"] = mismatch
                                errors.append({"artifact": name, "path": str(path), "error": mismatch})
                    if tail_lines and path.suffix.lower() in {".txt", ".log", ".md", ".json", ".csv"}:
                        tail = _read_text_tail(path, tail_lines)
                        if max_tail_chars > 0 and len(tail) > max_tail_chars:
                            item["tail"] = tail[-max_tail_chars:]
                            item["tailTruncated"] = True
                            item["tailOriginalChars"] = len(tail)
                        else:
                            item["tail"] = tail
                except OSError as ex:
                    item["error"] = str(ex)
                    errors.append({"artifact": name, "path": str(path), "error": str(ex)})
            elif not path.exists():
                item["error"] = "ARTIFACT_MISSING"
                errors.append({"artifact": name, "path": str(path), "error": "ARTIFACT_MISSING"})
            else:
                item["error"] = "ARTIFACT_NOT_A_FILE"
                errors.append({"artifact": name, "path": str(path), "error": "ARTIFACT_NOT_A_FILE"})
            artifacts[name] = item
        return artifacts, errors

    def _commit_operation_artifact_collection(
        self, state: dict, request_id: str, artifacts: dict[str, object], errors: list[dict], sequence: int,
        elapsed_ms: int, detail_level: str, max_tail_chars: int, include_raw_state: bool,
    ) -> ToolResponse:
        # Do not replace a previously durable artifact snapshot until the complete
        # candidate has been accepted by persistence.
        candidate = copy.deepcopy(state)
        candidate["artifacts"] = artifacts
        candidate["artifactErrors"] = errors
        candidate["artifactCollectionSequence"] = sequence
        candidate["artifactPersistenceError"] = ""
        candidate["timing"]["artifactReadMs"] += elapsed_ms
        self._finalize_operation_timing(candidate)
        try:
            self.server.state.save_operation(candidate)
        except Exception as exc:
            public = self._public_operation_state(state, detail_level, max_tail_chars, include_raw_state)
            public.update({
                "collectionPersisted": False,
                "artifactPersistenceError": str(exc),
                "artifactCollectionSequence": int(state.get("artifactCollectionSequence") or 0),
            })
            return fail(request_id, "OPERATION_PERSIST_FAILED", str(exc), public)
        state.clear()
        state.update(candidate)
        payload = {"operationId": state["operationId"], "artifacts": artifacts, "artifactErrors": errors,
                   "artifactCollectionSequence": state["artifactCollectionSequence"],
                   "collectionPersisted": True,
                   "responseDetailLevel": self._normalize_operation_detail_level(detail_level)}
        if include_raw_state:
            payload["operation"] = self._public_operation_state(state, detail_level, max_tail_chars, True)
        return ok(request_id, payload)

    async def _operation_invoke(self, call: dict | None, operation_id: str = "") -> ToolResponse:
        token = OPERATION_ID.set(operation_id)
        try:
            return await self._operation_invoke_unscoped(call)
        finally:
            OPERATION_ID.reset(token)

    def _operation_has_unity_session(self) -> bool:
        server = getattr(self, "server", None)
        is_ready = getattr(server, "is_ready", None)
        if callable(is_ready):
            return bool(is_ready())
        manager = getattr(server, "session_manager", None)
        return manager is None or getattr(manager, "active", None) is not None

    @staticmethod
    def _operation_call_requires_unity(call: dict | None) -> bool:
        if not isinstance(call, dict):
            return True
        kind = str(call.get("kind") or call.get("type") or "").strip().lower()
        if kind in {"mcp", "tool", "mcp_tool"}:
            descriptor = REGISTRY.resolve(str(call.get("toolName") or call.get("name") or ""))
            return descriptor is None or descriptor.requires_unity_connection
        return True

    async def _operation_invoke_unscoped(self, call: dict | None) -> ToolResponse:
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
                async_after_sec=0,
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
    def _operation_adapt_payload(result: ToolResponse, call: dict | None, job_spec: dict | None) -> tuple[dict, str, dict | None]:
        """Normalize direct and reflection results, then apply optional jobSpec field paths."""
        if not result.ok or not isinstance(result.data, dict):
            return {}, "", None
        call = call if isinstance(call, dict) else {}
        spec = job_spec if isinstance(job_spec, dict) else {}
        data = result.data
        kind = str(call.get("kind") or call.get("type") or "").strip().lower()
        result_path = call.get("resultPath", spec.get("resultPath"))

        if result_path:
            found, selected = _operation_path_get(data, result_path)
            if not found:
                return {}, f"Configured resultPath was not found: {result_path}", None
            payload, parse_error, parse_diagnostic = _operation_parse_object(selected)
        elif kind == "reflection" and "result" in data:
            payload, parse_error, parse_diagnostic = _operation_parse_object(data.get("result"))
        else:
            payload, parse_error, parse_diagnostic = _extract_operation_payload(result)
        if parse_error:
            return {}, parse_error, parse_diagnostic

        field_paths = {
            "status": "statusPath",
            "phase": "phasePath",
            "error": "errorPath",
            "detail": "detailPath",
            "progress": "progressPath",
            "failureSignature": "failureSignaturePath",
            "artifacts": "artifactsPath",
        }
        adapted = dict(payload)
        for target, path_field in field_paths.items():
            path = call.get(path_field, spec.get(path_field))
            if not path:
                continue
            found, selected = _operation_path_get(payload, path)
            if not found:
                found, selected = _operation_path_get(data, path)
            if not found:
                return {}, f"Configured {path_field} was not found: {path}", None
            adapted[target] = selected
        return adapted, "", None

    def _resolve_operation_call(self, call: dict | None, state: dict) -> dict | None:
        """Resolve exact ${start.*}/${status.*}/${operation.*} placeholders in a call."""
        if not isinstance(call, dict):
            return call

        roots = {
            "start": state.get("startData") or {},
            "status": state.get("lastStatusData") or {},
            "operation": self._public_operation_state(state),
        }

        def resolve(value):
            if isinstance(value, list):
                return [resolve(item) for item in value]
            if isinstance(value, dict):
                return {key: resolve(item) for key, item in value.items()}
            if not isinstance(value, str) or not value.startswith("${") or not value.endswith("}"):
                return value
            path = value[2:-1].strip().split(".")
            current = roots.get(path[0])
            if current is None:
                return value
            for segment in path[1:]:
                if isinstance(current, dict) and segment in current:
                    current = current[segment]
                else:
                    return value
            return current

        return resolve(call)

    @staticmethod
    def _operation_call_has_missing_identity(spec: object, resolved: object) -> bool:
        if isinstance(spec, dict):
            return not isinstance(resolved, dict) or any(
                TaskDomainService._operation_call_has_missing_identity(value, resolved.get(key))
                for key, value in spec.items())
        if isinstance(spec, list):
            return not isinstance(resolved, list) or len(spec) != len(resolved) or any(
                TaskDomainService._operation_call_has_missing_identity(a, b) for a, b in zip(spec, resolved))
        if isinstance(spec, str) and spec.startswith("${"):
            return resolved is None or resolved == "" or (isinstance(resolved, str) and "${" in resolved)
        return False

    @staticmethod
    def _operation_identity_mismatch(state: dict, payload: dict) -> str:
        for key in ("operationId", "runGuid", "taskId", "captureId"):
            expected = (state.get("startData") or {}).get(key)
            if expected and key in payload and payload[key] != expected:
                return f"Status {key}={payload[key]!r} does not match original {expected!r}."
        return ""

    @staticmethod
    def _tool_response_summary(result: ToolResponse) -> dict:
        data = TaskDomainService._redact_operation_secrets(
            result.data if isinstance(result.data, dict) else {}
        )
        return {
            "ok": result.ok,
            "requestId": result.request_id,
            "error": {
                "code": result.error.code,
                "message": result.error.message,
                "detail": TaskDomainService._redact_operation_secrets(result.error.detail),
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
        if "cleanupPending" in payload:
            state["cleanupPending"] = bool(payload.get("cleanupPending"))
        metrics = payload.get("metrics") if isinstance(payload.get("metrics"), dict) else {}
        project_elapsed = metrics.get("projectElapsedSec", payload.get("elapsedSec"))
        try:
            if project_elapsed is not None:
                state["timing"]["projectElapsedMs"] = int(float(project_elapsed) * 1000)
        except (TypeError, ValueError):
            pass
        if isinstance(payload.get("artifacts"), dict):
            state["artifactHints"] = payload["artifacts"]
        self._merge_operation_milestones(state, payload)

    @staticmethod
    def _merge_operation_milestones(state: dict, payload: dict) -> None:
        incoming = payload.get("milestones")
        if isinstance(payload.get("milestone"), dict):
            incoming = [payload["milestone"], *(incoming if isinstance(incoming, list) else [])]
        if not isinstance(incoming, list):
            return
        milestones = state.setdefault("milestones", [])
        for item in incoming:
            if not isinstance(item, dict):
                continue
            name = str(item.get("name") or "").strip()
            if not name:
                continue
            reached_at = int(item.get("reachedAt") or item.get("timestamp") or now_ms())
            occurrence_id = str(item.get("occurrenceId") or f"{name}:{reached_at}")
            normalized = {
                "name": name,
                "occurrenceId": occurrence_id,
                "operationId": state.get("operationId", ""),
                "reachedAt": reached_at,
                "source": str(item.get("source") or "project-status"),
                "valid": bool(item.get("valid", True)),
                "invalidatedAt": int(item.get("invalidatedAt") or 0),
                "invalidatedReason": str(item.get("invalidatedReason") or ""),
                "invalidateOnFailure": bool(item.get("invalidateOnFailure", False)),
            }
            if isinstance(item.get("evidence"), dict):
                normalized["evidence"] = item["evidence"]
            existing = next(
                (entry for entry in milestones if entry.get("occurrenceId") == occurrence_id),
                None,
            )
            if existing is None:
                milestones.append(normalized)
            else:
                for field in (
                    "source",
                    "valid",
                    "invalidatedAt",
                    "invalidatedReason",
                    "invalidateOnFailure",
                ):
                    if field not in item:
                        normalized[field] = existing.get(field, normalized[field])
                existing.update(normalized)
        milestones.sort(key=lambda entry: (int(entry.get("reachedAt") or 0), str(entry.get("occurrenceId") or "")))
        if len(milestones) > 500:
            del milestones[:-500]

    @staticmethod
    def _invalidate_milestones_on_failure(state: dict) -> None:
        invalidated_at = int(state.get("endedAt") or now_ms())
        reason = str(state.get("failureSignature") or state.get("error") or "OperationFailed")
        for milestone in state.get("milestones") or []:
            if milestone.get("valid", True) and milestone.get("invalidateOnFailure"):
                milestone["valid"] = False
                milestone["invalidatedAt"] = invalidated_at
                milestone["invalidatedReason"] = reason

    def _mark_repeat_failure(self, state: dict) -> None:
        if str(state.get("status", "")).lower() not in _DEFAULT_OPERATION_FAILURE:
            self._operation_failure_history.clear()
            setattr(self, "_operation_last_failure_signature", "")
            return
        self._invalidate_milestones_on_failure(state)
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
            state.get("cancelRequested", False),
            state.get("cleanupPending", False),
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
        # All terminal routes converge here; business completion is not final completion.
        first_cleanup = not state.get("businessTerminal")
        if not state.get("businessTerminal"):
            if state.get("timedOut"):
                state["observedBusinessResult"] = {key: state.get(key) for key in ("status", "phase", "error", "failureSignature")}
                state.update(status="Timeout", phase="JobTimeout", failureSignature="OperationJobTimeout",
                             error="Job deadline elapsed; underlying business termination has now been observed.")
            state.update(
                businessTerminal=True, businessEndedAt=state.get("endedAt") or now_ms(),
                businessResult={key: state.get(key) for key in ("status", "phase", "error", "failureSignature")},
                cleanupDeadlineAt=now_ms() + int(state["jobSpec"].get("cleanup", {}).get("timeoutSec", 30) * 1000),
            )
        state["endedAt"] = 0
        state["status"], state["phase"] = "CleaningUp", "Cleanup"
        if state.get("businessCleanupPending") and not first_cleanup:
            try:
                call = state.get("resolvedStatusCall") or self._resolve_operation_call(state["jobSpec"]["statusCall"], state)
                result = await asyncio.wait_for(
                    self._operation_invoke(call, state["operationId"]), timeout=5)
                payload, error, diagnostic = self._operation_adapt_payload(result, call, state["jobSpec"])
                if error:
                    state["parseDiagnostic"] = diagnostic
                if result.ok and not error and not self._operation_identity_mismatch(state, payload) and _operation_cleanup_state_is_explicit(payload):
                    state["businessCleanupPending"] = _operation_cleanup_pending(payload)
                    state["cleanupBusinessEvidence"] = payload
            except Exception as exc:
                state["cleanupBusinessError"] = str(exc)
        capture = state.get("consoleCapture") or {}
        if not capture.get("stopped"):
            try:
                await asyncio.wait_for(self._stop_owned_operation_capture(state), timeout=5)
            except Exception as exc:
                capture["stopError"] = str(exc)
        capture_done = not capture.get("sessionId") or capture.get("stopped") is True
        resources_done = capture_done and not state.get("businessCleanupPending", False)
        state["cleanupTerminal"] = resources_done
        if resources_done:
            state.setdefault("cleanupEndedAt", now_ms())
        require_edit = state["jobSpec"].get("cleanup", {}).get("requireEditMode", False)
        editor_done = not require_edit
        state["editorVerification"] = "not_requested" if not require_edit else "pending"
        if require_edit:
            try:
                observed = await asyncio.wait_for(self.mcp_status(force_fresh=True, include_capabilities=False), timeout=5)
                editor = (observed.data or {}).get("executionState", {})
                state["cleanupEditorEvidence"] = editor
                transition = (observed.data or {}).get("playModeTransition") or {}
                if (observed.ok and editor.get("authoritative") is True and editor.get("isStale") is False
                        and transition.get("operationId") == state["operationId"]
                        and int(transition.get("at") or 0) >= state["startedAt"]):
                    state["playModeTransition"] = transition
                editor_done = (
                    observed.ok and editor.get("ready") is True and editor.get("authoritative") is True
                    and editor.get("isStale") is False and editor.get("playModeState") == "edit"
                    and int(editor.get("observedAt") or 0) > state["businessEndedAt"]
                )
            except Exception as exc:
                state["cleanupEditorError"] = str(exc)
                state["editorVerification"] = "unknown"
        state["editorTerminal"] = bool(editor_done)
        if require_edit and editor_done:
            state["editorVerification"] = "verified"
            state.setdefault("editorEndedAt", now_ms())
        state["cleanupPending"] = not (resources_done and editor_done)
        state["unresolvedResources"] = (
            (["consoleCapture:" + str(capture.get("sessionId"))] if not capture_done else [])
            + (["businessCleanup"] if state.get("businessCleanupPending") else [])
            + (["authoritativeEditMode"] if not editor_done else [])
        )
        if not state["cleanupPending"]:
            state.update(state["businessResult"])
            state["endedAt"] = now_ms()
        elif now_ms() >= state["cleanupDeadlineAt"]:
            if require_edit and not editor_done:
                state["editorVerification"] = "failed"
            state.update(status="Failed", phase="CleanupTimeout", endedAt=now_ms(),
                         failureSignature="OperationCleanupTimeout",
                         error="Business ended but cleanup is unverified; inspect capture and Editor state before starting another operation.")
        state["updatedAt"] = now_ms()

    async def _stop_owned_operation_capture(self, state: dict) -> None:
        capture = state.get("consoleCapture") or {}
        session_id = capture.get("sessionId")
        if not session_id or capture.get("stopped"):
            return
        owner_token = str(capture.get("ownerToken") or "")
        if not owner_token:
            state.update(status="RecoveryRequired", phase="capture_owner_token_unknown", cleanupPending=True,
                         nextAction="The operation capture ownership token is unavailable; do not stop or adopt the session automatically.")
            return
        result = await self.console_capture_stop(session_id=session_id, owner_token=owner_token)
        capture["stop"] = self._tool_response_summary(result)
        if result.ok and isinstance(result.data, dict):
            session_data = result.data.get("session") if isinstance(result.data.get("session"), dict) else result.data
            session_data = self._redact_operation_secrets(session_data)
            capture["session"] = session_data
            for key in ("jsonlPath", "summaryPath", "manifestPath", "sha256", "recordCount", "droppedCount", "fileBytes"):
                if key in session_data:
                    capture[key] = session_data[key]
        session_data = capture.get("session") or {}
        capture["stopped"] = False
        capture["artifactsVerified"] = False
        stop_confirmed = bool(
            result.ok and session_data.get("sessionId") == session_id
            and session_data.get("active") is False
            and session_data.get("finishedAtUtcMs")
            and session_data.get("sha256") and session_data.get("summaryPath")
            and session_data.get("fileBytes") is not None
        )
        if stop_confirmed:
            try:
                root = self._project_root().resolve()
                await asyncio.to_thread(_verify_capture_artifacts, root, session_data)
                capture["artifactsVerified"] = True
                capture["stopped"] = True
            except (OSError, ValueError) as exc:
                capture["stopped"] = False
                capture["artifactsVerified"] = False
                capture["stopError"] = str(exc)

    async def console_capture_stop(self, *, session_id: str, owner_token: str) -> ToolResponse:
        """Stop only a capture whose one-time ownership credential is established."""
        return await self.dispatcher.call(
            new_id("req"), "console.capture.stop", {"sessionId": session_id, "ownerToken": owner_token},
        )

    async def _start_owned_operation_capture(
        self, state: dict, *, title: str, path: str, include_stack_trace: bool,
        exclude_upilot: bool, clear_unity_console: bool,
    ) -> ToolResponse:
        capture = state.get("consoleCapture") or {}
        return await self.dispatcher.call(new_id("req"), "console.capture.start", {
            "title": title, "path": path, "includeStackTrace": include_stack_trace,
            "excludeUPilot": exclude_upilot, "clearUnityConsole": clear_unity_console,
            "ownerId": capture.get("ownerId", ""), "ownerToken": capture.get("ownerToken", ""),
            "requestKey": capture.get("requestKey", ""),
        })

    @staticmethod
    def _finalize_operation_timing(state: dict) -> None:
        state["timing"]["totalWallMs"] = max(0, (state.get("endedAt") or now_ms()) - int(state.get("startedAt") or now_ms()))

    @staticmethod
    def _normalize_operation_detail_level(value: str) -> str:
        normalized = str(value or "summary").strip().lower()
        return normalized if normalized in {"summary", "standard", "full"} else "summary"

    @staticmethod
    def _bounded_operation_value(value, max_chars: int, path: str = "", truncated: list[str] | None = None):
        truncated = truncated if truncated is not None else []
        if isinstance(value, str) and max_chars > 0 and len(value) > max_chars:
            truncated.append(path or "value")
            return value[:max_chars] + f"…[truncated {len(value) - max_chars} chars]"
        if isinstance(value, dict):
            return {str(k): TaskDomainService._bounded_operation_value(v, max_chars, f"{path}.{k}".strip("."), truncated) for k, v in value.items()}
        if isinstance(value, list):
            return [TaskDomainService._bounded_operation_value(v, max_chars, f"{path}[{i}]", truncated) for i, v in enumerate(value)]
        return value

    @staticmethod
    def _redact_operation_secrets(value):
        if isinstance(value, dict):
            return {
                key: (
                    "[redacted]"
                    if "".join(char for char in str(key).lower() if char.isalnum())
                    in {"ownertoken", "ownertokensha256", "ownerhash"}
                    or "".join(char for char in str(key).lower() if char.isalnum()).endswith("token")
                    else TaskDomainService._redact_operation_secrets(item)
                )
                for key, item in value.items()
            }
        if isinstance(value, list):
            return [TaskDomainService._redact_operation_secrets(item) for item in value]
        return value

    def _public_operation_state(
        self, state: dict, detail_level: str = "summary", max_tail_chars: int = 2000,
        include_raw_state: bool = False,
    ) -> dict:
        level = self._normalize_operation_detail_level(detail_level)
        max_chars = max(128, min(int(max_tail_chars or 2000), 1000000))
        truncated: list[str] = []
        public = {
            "durable": state.get("durable", False),
            "recovered": state.get("recovered", False),
            "nextAction": state.get("nextAction", ""),
            "operationId": state.get("operationId"),
            "displayName": state.get("displayName"),
            "status": state.get("status"),
            "phase": state.get("phase"),
            "error": state.get("error"),
            "detail": state.get("detail"),
            "progress": state.get("progress"),
            "failureSignature": state.get("failureSignature"),
            "parseDiagnostic": state.get("parseDiagnostic"),
            "repeatFailure": state.get("repeatFailure", False),
            "startAttemptCount": state.get("startAttemptCount", 0),
            "cancelRequested": state.get("cancelRequested", False),
            "cancelAccepted": state.get("cancelAccepted", False),
            "cancelRequestedAt": state.get("cancelRequestedAt", 0),
            "cancelAttemptCount": state.get("cancelAttemptCount", 0),
            "cleanupPending": state.get("cleanupPending", False),
            "businessTerminal": state.get("businessTerminal", False),
            "unresolvedResources": state.get("unresolvedResources", []),
            "cleanupTerminal": state.get("cleanupTerminal", False),
            "editorTerminal": state.get("editorTerminal", False),
            "editorVerification": state.get("editorVerification", "unknown"),
            "businessEndedAt": state.get("businessEndedAt", 0),
            "cleanupEndedAt": state.get("cleanupEndedAt", 0),
            "editorEndedAt": state.get("editorEndedAt", 0),
            "cleanupDeadlineAt": state.get("cleanupDeadlineAt", 0),
            "businessResult": state.get("businessResult", {}),
            "playModeTransition": state.get("playModeTransition", {}),
            "cleanupEditorEvidence": state.get("cleanupEditorEvidence", {}),
            "startedAt": state.get("startedAt"),
            "updatedAt": state.get("updatedAt"),
            "endedAt": state.get("endedAt"),
            "elapsedMs": max(0, (state.get("endedAt") or now_ms()) - state.get("startedAt", now_ms())),
            "terminal": bool(state.get("endedAt")),
            "timeoutSec": state.get("timeoutSec"),
            "jobTimeoutAt": int(state.get("startedAt") or 0) + int(float(state.get("timeoutSec") or 0) * 1000),
            "pollIntervalSec": state.get("pollIntervalSec"),
            "artifacts": state.get("artifacts") or {},
            "artifactErrors": state.get("artifactErrors") or [],
            "artifactCollectionSequence": int(state.get("artifactCollectionSequence") or 0),
            "artifactPersistenceError": state.get("artifactPersistenceError") or "",
            "timing": state.get("timing") or {},
            "milestones": self._bounded_operation_value(state.get("milestones") or [], max_chars, "milestones", truncated),
            "milestoneCount": len(state.get("milestones") or []),
            "responseDetailLevel": level,
            "rawStateAvailable": True,
        }
        status_data = state.get("lastStatusData") or {}
        safe_status_data = self._redact_operation_secrets(status_data)
        if isinstance(safe_status_data.get("metrics"), dict):
            public["metrics"] = safe_status_data["metrics"]
        capture = state.get("consoleCapture") or {}
        public_capture = self._redact_operation_secrets(
            {key: value for key, value in capture.items() if key != "ownerToken"}
        )
        if level == "summary":
            public["consoleCapture"] = {key: public_capture.get(key) for key in (
                "sessionId", "stopped", "jsonlPath", "summaryPath", "manifestPath", "sha256",
                "recordCount", "droppedCount", "fileBytes",
            ) if key in capture}
        elif level == "standard":
            public["lastStatusData"] = self._bounded_operation_value(safe_status_data, max_chars, "lastStatusData", truncated)
            if isinstance(safe_status_data.get("domain"), dict):
                public["domain"] = self._bounded_operation_value(safe_status_data["domain"], max_chars, "domain", truncated)
            public["consoleCapture"] = self._bounded_operation_value(public_capture, max_chars, "consoleCapture", truncated)
        else:
            public["lastStatusData"] = safe_status_data
            if isinstance(safe_status_data.get("domain"), dict): public["domain"] = safe_status_data["domain"]
            public["consoleCapture"] = public_capture
        public["artifacts"] = self._bounded_operation_value(public["artifacts"], max_chars, "artifacts", truncated)
        if include_raw_state:
            safe_raw_state = self._redact_operation_secrets(state)
            public["rawState"] = (
                self._bounded_operation_value(safe_raw_state, max_chars, "rawState", truncated)
                if level != "full" else safe_raw_state
            )
        if state.get("suspectedStuck"):
            public["suspectedStuck"] = True
        if state.get("recommendation"):
            public["recommendation"] = state["recommendation"]
        if truncated:
            public["truncatedFields"] = sorted(set(truncated))
        try:
            public["responseBytes"] = len(json.dumps(public, ensure_ascii=False, default=str).encode("utf-8"))
        except (TypeError, ValueError):
            pass
        return public

    async def ensure_ready(self, timeout_s: float = 300, required_editor_mode: str = "edit", editor_mode_control: str = "automatic") -> ToolResponse:
        """Pre-task check; concrete modes are automatically coordinated when authorized."""
        import time

        request_id = new_id("req")
        checks: dict = {}
        required_editor_mode = str(required_editor_mode or "edit").lower()
        if required_editor_mode not in {"any", "edit", "play", "managed"}:
            return fail(request_id, "EDITOR_MODE_INVALID", "requiredEditorMode must be any, edit, play, or managed.")
        checks["requiredEditorMode"] = required_editor_mode
        checks["editorModeControl"] = editor_mode_control

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
            checks["compileWait"] = compile_r.data
        else:
            checks["compileStatus"] = "error"

        # 3. Check editor state
        state_r = await self.dispatcher.call(new_id("req"), "resource.editorState", {})
        if state_r.ok and state_r.data:
            execution = self.server.state.execution_state()
            checks["executionState"] = execution
            checks["isCompiling"] = bool(execution["isCompiling"])
            checks["playModeState"] = execution["playModeState"]
            checks["inEditMode"] = execution["playModeState"] == "edit"
            checks["contextAuthoritative"] = bool(execution["authoritative"])
            checks["contextStale"] = bool(execution["isStale"])
            checks["blocked"] = bool(execution["blocked"])
            checks["blockedReason"] = execution["blockedReason"]
            checks["nextAction"] = execution["nextAction"]
            target = "" if required_editor_mode in {"any", "managed"} else required_editor_mode
            if target and execution["playModeState"] != target and bool(execution["authoritative"]) and execution["playModeState"] in {"edit", "play", "paused"} and editor_mode_control == "automatic":
                from ..config import CONFIG
                from ..automation_authorization import has_scope
                allowed = not CONFIG.automation_authorization_scopes or has_scope("editorModeTransition", CONFIG.automation_authorization_scopes)
                if allowed:
                    transition = await (self.playmode_stop() if target == "edit" else self.playmode_start())
                    checks["editorModeTransition"] = {"target": target, "ok": transition.ok, "error": transition.error.code if transition.error else ""}
                    if transition.ok:
                        await self.dispatcher.call(new_id("req"), "resource.editorState", {})
                        execution = self.server.state.execution_state()
                        checks["executionState"] = execution
                        checks["playModeState"] = execution["playModeState"]
                        checks["inEditMode"] = execution["playModeState"] == "edit"
                        checks["blocked"] = bool(execution["blocked"])
                        checks["blockedReason"] = execution["blockedReason"]
                else:
                    checks["blocked"] = True
                    checks["blockedReason"] = "AutomationAuthorizationRequired"
                    checks["nextAction"] = "Enable editorModeTransition in Advanced Settings."
        else:
            checks["inEditMode"] = False
            checks["playModeState"] = "unknown"
            checks["contextAuthoritative"] = False
            checks["contextStale"] = True
            checks["blocked"] = True
            checks["blockedReason"] = "EditorContextUnknown"

        checks["ready"] = (
            checks["connected"]
            and checks.get("compileStatus") == "ready"
            and (required_editor_mode in {"any", "managed"} or (required_editor_mode == "edit" and checks.get("inEditMode", False)) or (required_editor_mode == "play" and checks.get("playModeState") == "play"))
            and checks.get("contextAuthoritative", False)
            and not checks.get("contextStale", True)
            and (not checks.get("blocked", True) or (required_editor_mode in {"any", "managed", "play"} and checks.get("blockedReason") == "PlayMode"))
        )
        if not checks["ready"]:
            if checks.get("blockedReason"):
                checks["failReason"] = checks["blockedReason"]
            elif checks.get("playModeState") in ("unknown", ""):
                checks["failReason"] = "Editor mode is unknown"
                checks["nextAction"] = "Call unity_mcp_status(forceFresh=true) and retry after a live Editor response."
            elif not checks.get("inEditMode"):
                checks["failReason"] = "Unity is not in EditMode"
                checks["nextAction"] = "Exit PlayMode and wait for EditMode confirmation."
            elif checks.get("contextStale") or not checks.get("contextAuthoritative"):
                checks["failReason"] = "Editor context is stale or non-authoritative"
                checks["nextAction"] = "Call unity_mcp_status(forceFresh=true) before mutating the Editor."
        return ok(request_id, checks)

    @staticmethod
    def _task_execute_tool_succeeded(tool_name: str, result: ToolResponse) -> bool:
        """True when the tool transport succeeded *and* the tool-specific outcome is success."""
        data = result.data
        if tool_name == "wait_condition" and isinstance(data, dict):
            return bool(data.get("met"))
        if tool_name == "unity_snapshot_capture" and isinstance(data, dict):
            return data.get("terminal") is True and data.get("success") is True
        return True

    @staticmethod
    def _task_execute_logical_error(tool_name: str, result: ToolResponse) -> str:
        data = result.data if isinstance(result.data, dict) else {}
        if tool_name == "wait_condition":
            return str(data.get("lastError") or "wait_condition not met (met=false)")
        if tool_name == "unity_snapshot_capture":
            failures = data.get("failures") or []
            return "; ".join(
                str(item.get("code") or "SNAPSHOT_CAPTURE_FAILED") + ": " + str(item.get("message") or "")
                for item in failures[:4] if isinstance(item, dict)
            ) or "Snapshot did not produce a successful terminal result."
        return "logical failure"

    async def task_execute(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        max_total_s: float = 1200,
        retry_count: int = 1,
        restart_unity_on_timeout: bool = False,
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
        snapshot_evidence = None
        events: list[dict] = []

        descriptor = REGISTRY.resolve(tool_name)
        if retry_count > 0 and descriptor and (not descriptor.idempotent or descriptor.destructive):
            return fail(request_id, "TASK_RETRY_UNSAFE", "Non-idempotent or destructive tools require retryCount=0.")

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
                    if tool_name == "unity_snapshot_capture" and isinstance(result.data, dict):
                        snapshot_evidence = {key: result.data.get(key) for key in
                                             ("snapshotId", "status", "terminal", "success", "manifestPath")}
                        snapshot_evidence["failures"] = (result.data.get("failures") or [])[:16]
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
                **({"snapshot": snapshot_evidence} if snapshot_evidence is not None else {}),
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
        token = TASK_TOOL.set(tool_name)
        try:
            return await dispatch_public_tool(self, tool_name, tool_args)
        finally:
            TASK_TOOL.reset(token)

    async def task_start(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        retry_count: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        self._recover_test_jobs()
        descriptor = REGISTRY.resolve(tool_name)
        is_test_job = descriptor is not None and descriptor.facade_method in {"test_run", "upilot_acceptance_run"}
        if is_test_job:
            if retry_count != 0:
                return fail(request_id, "TEST_TASK_RETRY_UNSAFE", "Tests and acceptance require retryCount=0; an uncertain start must never be replayed.")
            if not 1 <= timeout_s <= 7200:
                return fail(request_id, "TEST_TASK_TIMEOUT_INVALID", "Use timeoutS=1..7200.")
            store = self.server.state
            if store._db_path is None or not store._project_path:
                return fail(request_id, "TEST_TASK_PROJECT_UNKNOWN", "Connect the intended Unity project before starting a persistent test task.")
            if any(
                value.get("durable") and value.get("projectPath") == store._project_path
                and value.get("status") in {"queued", "running", "cancel_requested", "RecoveryRequired"}
                for value in self._async_tasks.values()
            ):
                return fail(request_id, "TEST_TASK_ALREADY_ACTIVE", "Resolve the existing test/acceptance task before starting another.")
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
            "terminal": False,
        }
        if is_test_job:
            state.update({
                "durable": True, "projectPath": store._project_path, "toolArgs": tool_args or {},
                "deadlineAt": now_ms() + int(timeout_s * 1000), "runGuid": "",
                # These fields record the only safe send boundary for a
                # durable TestRunner task.  Recovery must observe an
                # established run and must never replay either request.
                "cancelRequested": False, "startIntentSent": False,
                "startSendState": "not_sent", "cancelSendState": "not_sent",
                "recovered": False,
            })
            try:
                store.save_test_job(state)
            except Exception as exc:
                return fail(request_id, "TEST_TASK_PERSIST_FAILED", str(exc))
            self._async_tasks[task_id] = state
            self._async_task_handles[task_id] = asyncio.create_task(self._run_test_task(state), name=task_id)
            return ok(request_id, state.copy())
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
            state["terminal"] = True
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

    async def task_status(self, task_id: str, detail_level: str = "summary") -> ToolResponse:
        self._recover_test_jobs()
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        if state.get("durable") and state.get("projectPath") != self.server.state._project_path:
            return fail(new_id("req"), "TASK_PROJECT_MISMATCH", "Task belongs to another Unity project.", {"taskId": task_id})
        self._resume_test_observer(state)
        result = self._public_task_state(state, detail_level)
        result["elapsedMs"] = max(0, (result["endedAt"] or now_ms()) - result["startedAt"])
        return ok(new_id("req"), result)

    async def task_cancel(self, task_id: str) -> ToolResponse:
        self._recover_test_jobs()
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        if state.get("terminal"):
            return ok(new_id("req"), self._public_task_state(state))
        if not state.get("durable"):
            return fail(new_id("req"), "TASK_CANCELLATION_UNSUPPORTED", "This task has no business cancellation adapter; its work and observer were left running.", {"taskId": task_id})
        if state.get("projectPath") != self.server.state._project_path:
            return fail(new_id("req"), "TASK_PROJECT_MISMATCH", "Task belongs to another Unity project.", {"taskId": task_id})
        state["cancelRequested"] = True
        state["status"] = "cancel_requested"
        state["phase"] = "cancel_requested"
        state["updatedAt"] = now_ms()
        # This is an intent only; the observer marks sent_unknown immediately
        # before invoking test.cancel.  If persistence fails, do not let this
        # process (or a restarted Server) retry an unproven cancellation.
        try:
            self.server.state.save_test_job(state)
        except Exception as exc:
            state.update(
                status="RecoveryRequired", phase="cancel_intent_persist_failed", terminal=False,
                error={"code": "TEST_TASK_PERSIST_FAILED", "message": str(exc)},
                nextAction="Inspect the original run; cancellation was not dispatched and will not be replayed.",
            )
            return fail(new_id("req"), "TEST_TASK_PERSIST_FAILED", str(exc), self._public_task_state(state))
        self._resume_test_observer(state)
        return ok(new_id("req"), self._public_task_state(state))

    @staticmethod
    def _public_task_state(state: dict, detail_level: str = "summary") -> dict:
        if not state.get("durable") or detail_level == "full":
            return state.copy()
        result = {key: value for key, value in state.items() if key not in {"acceptanceReport", "toolArgs", "result", "error", "cancelResponse"}}
        report = state.get("acceptanceReport") or {}
        if report:
            result["result"] = {"result": {key: report[key] for key in (
                "acceptancePassed", "runGuid", "cleanupVerified", "testIdentityVerified", "sourceIdentity",
                "sourceUnchanged", "failureCode", "failureMessage", "artifact",
            ) if key in report}}
            test_data = report.get("steps", {}).get("testStatus", {}).get("data", {})
        else:
            test_data = (state.get("result") or {}).get("result") or {}
        result["tests"] = {key: test_data[key] for key in ("status", "total", "passed", "failed", "skipped", "cleanupSucceeded", "resultAuthoritative") if key in test_data}
        error = state.get("error")
        if error:
            result["error"] = {"code": error.get("code"), "message": error.get("message", "")[:2000]}
        return result

    def _resume_test_observer(self, state: dict) -> None:
        handle = self._async_task_handles.get(state["taskId"])
        if state.get("durable") and state.get("runGuid") and not state.get("terminal") and (handle is None or handle.done()):
            self._async_task_handles[state["taskId"]] = asyncio.create_task(self._run_test_task(state, recovering=True), name=state["taskId"])

    def _recover_test_jobs(self) -> None:
        store = getattr(getattr(self, "server", None), "state", None)
        if store is None or not getattr(store, "_project_path", ""):
            return
        project_path = store._project_path
        if getattr(self, "_test_jobs_loaded_project", "") == project_path:
            return
        self._test_jobs_loaded_project = project_path
        for state in store.load_test_jobs():
            task_id = state["taskId"]
            if task_id in self._async_tasks:
                continue
            # Older durable records predate explicit send-state evidence.  Do
            # not reinterpret an old intent as proof that nothing was sent.
            # ``sent_unknown`` makes the observer evidence-only until a human
            # resolves the original run/cancel request.
            state.setdefault(
                "startSendState",
                "sent_unknown" if state.get("startIntentSent") else "not_sent",
            )
            state.setdefault(
                "cancelSendState",
                "sent_unknown" if state.get("cancelRequested") else "not_sent",
            )
            state["recovered"] = True
            self._async_tasks[task_id] = state
            if state.get("terminal"):
                continue
            if not state.get("runGuid"):
                state.update(status="RecoveryRequired", phase="start_identity_unknown", terminal=False,
                             error={"code": "TEST_RECOVERY_REQUIRED", "message": "Server restarted without an established runGuid. Start was not replayed."})
                store.save_test_job(state)
                continue
            state["status"] = "running"
            self._async_task_handles[task_id] = asyncio.create_task(self._run_test_task(state, recovering=True), name=task_id)

    async def _run_test_task(self, state: dict, recovering: bool = False) -> None:
        store = self.server.state
        token = TEST_JOB_CONTEXT.set((state, store.save_test_job))
        try:
            state.update(status="running", phase="recovering" if recovering else "executing", updatedAt=now_ms())
            store.save_test_job(state)
            if recovering:
                report = state.get("acceptanceReport")
                if report:
                    report["runGuid"] = state["runGuid"]
                    result = await self._complete_acceptance_report(report)
                else:
                    result = await self._wait_for_test_result(state["runGuid"], state["deadlineAt"])
            else:
                result = await self._dispatch_tool(state["toolName"], state["toolArgs"])
                if state.get("runGuid") and not state.get("acceptanceReport"):
                    result = await self._wait_for_test_result(state["runGuid"], state["deadlineAt"])

            report = state.get("acceptanceReport") or {}
            test_result = (report.get("steps", {}).get("testStatus", {}).get("data")
                           if report else result.data) or {}
            no_tests = bool(test_result.get("noTests"))
            cleaned = self._test_cleanup_verified(test_result)
            identity_verified = test_result.get("runGuid") == state.get("runGuid") and test_result.get("resultAuthoritative") is True
            state["result"] = {"result": result.data or (result.error.detail if result.error else {}), "runGuid": state.get("runGuid")}
            if report.get("artifact"):
                state["artifact"] = report["artifact"]
            if no_tests and not state.get("runGuid") and not report:
                state.update(status="no_tests", terminal=True)
            elif state.get("runGuid") and not (cleaned and identity_verified):
                state.update(status="RecoveryRequired", terminal=False)
            elif state.get("cancelRequested"):
                state.update(status="cancelled", terminal=True)
            elif state.get("timedOut"):
                state.update(status="timed_out", terminal=True)
            elif report.get("failureCode") == "UPILOT_ACCEPTANCE_NO_TESTS":
                state.update(status="no_tests", terminal=True)
            elif report:
                state.update(status="completed" if result.ok and report.get("acceptancePassed") else "failed", terminal=True)
            else:
                state.update(status="completed" if result.ok and test_result.get("status") == "completed" and test_result.get("failed") == 0 else "failed", terminal=True)
            if state.get("startIntentSent") and not state.get("runGuid") and not no_tests:
                state.update(status="RecoveryRequired", terminal=False)
            if result.error:
                state["error"] = {"code": result.error.code, "message": result.error.message, "detail": result.error.detail}
            if state["status"] == "RecoveryRequired":
                state["nextAction"] = "Inspect the original runGuid and persisted TestRuns evidence; do not replay start."
        except TestJobCancelledBeforeStart:
            state.update(status="cancelled", terminal=True)
            if state.get("acceptanceReport"):
                report = state["acceptanceReport"]
                self._finish_acceptance_report(report, False, "UPILOT_ACCEPTANCE_CANCELLED", "Cancelled before test start.")
                state["result"] = {"result": report}
                if report.get("artifact"):
                    state["artifact"] = report["artifact"]
        except asyncio.CancelledError:
            # Server shutdown only stops observation. A later server may reattach by runGuid.
            state.update(status="RecoveryRequired", terminal=False, phase="observer_interrupted")
            raise
        except Exception as exc:
            state.update(status="RecoveryRequired", terminal=False, error={"code": "TEST_TASK_EXCEPTION", "message": str(exc)})
        finally:
            TEST_JOB_CONTEXT.reset(token)
            state["phase"] = state["status"]
            state["updatedAt"] = now_ms()
            state["endedAt"] = now_ms() if state.get("terminal") else 0
            try:
                store.save_test_job(state)
            except Exception as exc:
                state.update(status="RecoveryRequired", terminal=False, endedAt=0,
                             error={"code": "TEST_TASK_PERSIST_FAILED", "message": str(exc)})
