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

class ResourceDomainService:
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

    async def asset_find(self, query: str, asset_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"query": query}
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.find", payload)

    async def texture_importer_get(self, asset_path: str) -> ToolResponse:
        return await self.dispatcher.call(new_id("req"), "texture.importerGet", {"assetPath": asset_path})

    async def asset_dependencies(self, asset_path: str, recursive: bool = True) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "asset.dependencies",
            {"assetPath": asset_path, "recursive": recursive},
        )

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
        return await self.dispatcher.call(
            request_id, "asset.delete", {"assetPath": asset_path}
        )

    async def asset_refresh(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.refresh", {})

    async def sync_after_disk_write(
        self, delay_s: float = 2.0, trigger_compile: bool = False
    ) -> ToolResponse:
        """Wait for OS/fs flush, then AssetDatabase.Refresh; optionally unity_compile.

        Intended to be called once per batch after all in-editor script edits/saves are
        finished (not after each file). Same for external toolchains writing many files.
        Reduces redundant compiles and matches disk flush timing. Unity imports without
        relying on window focus.
        """
        logger = logging.getLogger("upilot.facade")
        request_id = new_id("req")

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
                compile_phase in {"queued", "compiling", "domain_reload", "verifying"}
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
                },
            )
        await asyncio.sleep(max(0.0, delay_s))
        refresh_r = await self.asset_refresh()
        payload: dict = {
            "delayS": delay_s,
            "triggerCompile": trigger_compile,
            "status": "refresh_failed" if not refresh_r.ok else "refreshed",
            "refreshed": refresh_r.ok,
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
            return ok(request_id, payload)
        compile_r = await self.compile()
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

            terminal = (
                str(wait_data.get("status") or "").lower() == "ready"
                and not bool(wait_data.get("isCompiling", False))
                and not bool(self.server.state.editor.is_compiling)
            )
            if terminal:
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
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "prefabPath": prefab_path,
            "componentType": component_type,
            "includeSerializedFields": include_serialized_fields,
            "maxDepth": max_depth,
            "maxResults": max_results,
        }
        return await self.dispatcher.call(
            request_id,
            "prefab.queryComponents",
            payload,
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

    async def menu_execute(self, menu_path: str) -> ToolResponse:
        request_id = new_id("req")
        rejected = self._reject_write_if_unapproved(request_id, "unity_menu_execute")
        if rejected is not None:
            return rejected
        return await self.dispatcher.call(
            request_id, "menu.execute", {"menuPath": menu_path}
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
        component_type: str = "", include_inactive: bool = True, limit: int = 100,
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
