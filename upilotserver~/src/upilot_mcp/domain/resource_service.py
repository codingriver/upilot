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

class ResourceDomainService:
    def _active_project_root(self) -> Path | None:
        session = self.server.session_manager.active
        if session and session.project_path:
            return Path(session.project_path).expanduser().resolve()
        return None

    async def asset_find(self, query: str, asset_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"query": query}
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.find", payload)

    async def asset_create_folder(
        self, parent_folder: str, new_folder_name: str
    ) -> ToolResponse:
        request_id = new_id("req")
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
        return await self.dispatcher.call(
            request_id,
            "asset.copy",
            {
                "sourcePath": source_path,
                "destinationPath": destination_path,
            },
        )

    async def asset_move(self, source_path: str, destination_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "asset.move",
            {
                "sourcePath": source_path,
                "destinationPath": destination_path,
            },
        )

    async def asset_delete(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
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
        await asyncio.sleep(max(0.0, delay_s))
        refresh_r = await self.asset_refresh()
        payload: dict = {
            "delayS": delay_s,
            "refreshed": refresh_r.ok,
        }
        if refresh_r.ok and refresh_r.data is not None:
            payload["refresh"] = refresh_r.data
        if not refresh_r.ok:
            return refresh_r
        if not trigger_compile:
            return ok(request_id, payload)
        compile_r = await self.compile()
        payload["compiled"] = compile_r.ok
        if compile_r.ok and compile_r.data is not None:
            payload["compile"] = compile_r.data
        elif not compile_r.ok:
            msg = compile_r.error.message if compile_r.error else "compile failed"
            logger.warning("sync_after_disk_write: compile failed: %s", msg)
            payload["compileError"] = msg
            return fail(
                request_id,
                compile_r.error.code if compile_r.error else "COMPILE_FAILED",
                msg,
                payload,
            )
        return ok(request_id, payload)

    async def asset_get_info(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "asset.getInfo", {"assetPath": asset_path}
        )

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
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"maxDepth": max_depth}
        if asset_path:
            payload["assetPath"] = asset_path
        if game_object_id:
            payload["gameObjectId"] = game_object_id
        if component_type:
            payload["componentType"] = component_type
        if component_index:
            payload["componentIndex"] = component_index
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

    async def prefab_create(
        self, source_game_object_id: int, prefab_path: str
    ) -> ToolResponse:
        request_id = new_id("req")
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
        return await self.dispatcher.call(request_id, "prefab.save", {})

    async def material_create(
        self, material_path: str, shader_name: str = "Standard"
    ) -> ToolResponse:
        request_id = new_id("req")
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

    async def menu_execute(self, menu_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "menu.execute", {"menuPath": menu_path}
        )

    async def menu_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "menu.list", {})

    async def package_add(self, package_name: str, version: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"packageName": package_name}
        if version:
            payload["version"] = version
        return await self.dispatcher.call(
            request_id, "package.add", payload, timeout_ms=120000
        )

    async def package_remove(self, package_name: str) -> ToolResponse:
        request_id = new_id("req")
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
        payload: dict = {"name": name}
        if parent_id:
            payload["parentId"] = parent_id
        if primitive_type:
            payload["primitiveType"] = primitive_type
        return await self.dispatcher.call(request_id, "gameobject.create", payload)

    async def gameobject_find(
        self, name: str = "", tag: str = "", instance_id: int = 0
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if name:
            payload["name"] = name
        if tag:
            payload["tag"] = tag
        if instance_id:
            payload["instanceId"] = instance_id
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
        return await self.dispatcher.call(
            request_id, "gameobject.duplicate", {"instanceId": instance_id}
        )

    async def scene_create(self, scene_name: str = "") -> ToolResponse:
        request_id = new_id("req")
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
        return await self.dispatcher.call(
            request_id,
            "component.modify",
            {
                "gameObjectId": game_object_id,
                "componentType": component_type,
                "properties": properties,
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

