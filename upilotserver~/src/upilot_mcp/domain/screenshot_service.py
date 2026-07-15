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

class ScreenshotDomainService:
    async def screenshot_game_view(
        self,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "screenshot.gameView",
            {
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
        )

    async def screenshot_scene_view(
        self,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "screenshot.sceneView",
            {
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
        )

    async def screenshot_camera(
        self,
        camera_name: str,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "screenshot.camera",
            {
                "cameraName": camera_name,
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
        )

    async def screenshot_save(
        self,
        path: str = "",
        source: str = "gameView",
        overwrite: bool = False,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
        camera_name: str = "",
        window_title: str = "Game",
        allow_outside_project: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        image_format = (format or "png").strip().lower()
        if image_format != "png":
            return fail(
                request_id,
                "INVALID_SCREENSHOT_FORMAT",
                "保存型截图工具当前仅支持 format=png。",
                {"format": format, "path": path},
            )

        source_key = (source or "gameView").strip().lower()
        normalized_source = self._normalize_screenshot_source(source_key)
        if not normalized_source:
            return fail(
                request_id,
                "INVALID_SCREENSHOT_SOURCE",
                "source 仅支持 gameView、sceneView、camera、editorWindow。",
                {"source": source, "path": path},
            )
        if normalized_source == "camera" and not camera_name:
            return fail(
                request_id,
                "CAMERA_NAME_REQUIRED",
                "source=camera 时必须提供 cameraName。",
                {"path": path},
            )

        target_result = self._resolve_screenshot_save_path(
            path, normalized_source, allow_outside_project
        )
        if isinstance(target_result, ToolResponse):
            return target_result
        target_path = target_result
        if target_path.exists() and not overwrite:
            return fail(
                request_id,
                "FILE_EXISTS",
                "目标截图文件已存在，若需要覆盖请传 overwrite=true。",
                {"path": str(target_path)},
            )

        bridge_save = await self.dispatcher.call(
            new_id("req"),
            "screenshot.save",
            {
                "path": str(target_path),
                "source": normalized_source,
                "overwrite": overwrite,
                "width": width,
                "height": height,
                "format": image_format,
                "quality": quality,
                "cameraName": camera_name,
                "windowTitle": window_title,
                "allowOutsideProject": allow_outside_project,
            },
        )
        if bridge_save.ok:
            data = bridge_save.data or {}
            return ok(
                request_id,
                {
                    "path": data.get("path", str(target_path)),
                    "source": data.get("source", normalized_source),
                    "bytes": data.get("bytes", 0),
                    "width": data.get("width", width),
                    "height": data.get("height", height),
                    "format": data.get("format", image_format),
                    "sha256": data.get("sha256", ""),
                    "overwritten": data.get("overwritten", overwrite),
                    "savedBy": "unity_bridge",
                },
            )
        return bridge_save

    @staticmethod
    def _normalize_screenshot_source(source_key: str) -> str:
        if source_key in ("gameview", "game_view", "game"):
            return "gameView"
        if source_key in ("sceneview", "scene_view", "scene"):
            return "sceneView"
        if source_key == "camera":
            return "camera"
        if source_key in ("editorwindow", "editor_window", "window"):
            return "editorWindow"
        return ""

    @staticmethod
    def _is_command_not_found(resp: ToolResponse) -> bool:
        if resp.ok or not resp.error:
            return False
        return (resp.error.code or "").upper() == "COMMAND_NOT_FOUND"

    def _resolve_screenshot_save_path(
        self, path: str, normalized_source: str, allow_outside_project: bool
    ) -> Path | ToolResponse:
        request_id = new_id("req")
        raw_path = (path or "").strip()
        if not raw_path:
            project_root = self._active_project_root()
            if not project_root:
                return fail(
                    request_id,
                    "PROJECT_PATH_UNAVAILABLE",
                    "path 为空时需要当前 Unity 工程路径来生成默认截图保存路径。",
                    {},
                )

            timestamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S-%f")[:-3]
            safe_source = "".join(
                ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in normalized_source
            )
            target_path = project_root / "Log" / "UPilotScreenshots" / f"{timestamp}_{safe_source}.png"
            return target_path.expanduser().resolve()

        target_path = Path(raw_path)
        if not target_path.is_absolute():
            project_root = self._active_project_root()
            target_path = (project_root / target_path) if project_root else target_path
        target_path = target_path.expanduser().resolve()

        if target_path.suffix.lower() != ".png":
            return fail(
                request_id,
                "INVALID_SCREENSHOT_EXTENSION",
                "当前保存型截图工具仅允许写入 .png 文件。",
                {"path": str(target_path)},
            )

        if allow_outside_project:
            return target_path

        project_root = self._active_project_root()
        if not project_root:
            return fail(
                request_id,
                "PROJECT_PATH_UNAVAILABLE",
                "当前没有可用 Unity 工程路径，无法校验截图保存目录。",
                {"path": str(target_path)},
            )

        try:
            target_path.relative_to(project_root)
        except ValueError:
            return fail(
                request_id,
                "SCREENSHOT_PATH_OUTSIDE_PROJECT",
                "默认只允许将截图保存到当前 Unity 工程目录内。",
                {"path": str(target_path), "projectRoot": str(project_root)},
            )

        return target_path

    @staticmethod
    def _decode_screenshot_image_data(image_data: str) -> bytes:
        value = image_data.strip()
        if "," in value and value.lower().startswith("data:"):
            value = value.split(",", 1)[1]
        return base64.b64decode(value, validate=True)

    @staticmethod
    def _screenshot_degrade_mode(explicit: str | None) -> str:
        v = (
            (explicit or getenv("UPILOT_SCREENSHOT_DEGRADE", "auto"))
            .strip()
            .lower()
        )
        if v not in ("none", "auto", "scene", "minimal"):
            return "auto"
        return v

    @staticmethod
    def _response_has_screenshot_payload(resp: ToolResponse) -> bool:
        if not resp.ok or not resp.data:
            return False
        img = resp.data.get("imageData") or resp.data.get("image_data")
        return bool(img and len(str(img)) > 48)

    async def screenshot_editor_window(
        self,
        window_title: str = "upilot",
        degrade: str | None = None,
    ) -> ToolResponse:
        """Capture an editor window; optional degradation when capture is unavailable.

        * degrade=none — only Bridge `screenshot.editorWindow` (strict).
        * degrade=auto — editor → (unless WINDOW_NOT_FOUND) Scene view fallback → 1×1 placeholder.
        * degrade=scene — editor then Scene view; no placeholder.
        * degrade=minimal — editor then 1×1 placeholder (no Scene).

        WINDOW_NOT_FOUND is never upgraded: unknown titles must still fail for T-M26-04.
        """
        request_id = new_id("req")
        mode = self._screenshot_degrade_mode(degrade)

        primary = await self.dispatcher.call(
            new_id("req"),
            "screenshot.editorWindow",
            {"windowTitle": window_title},
        )

        if mode == "none":
            return primary

        if self._response_has_screenshot_payload(primary):
            return primary

        err_code = primary.error.code if primary.error else ""
        if err_code == "WINDOW_NOT_FOUND":
            return primary

        if mode in ("auto", "scene"):
            sv = await self.screenshot_scene_view(
                width=320, height=180, format="png", quality=75
            )
            if self._response_has_screenshot_payload(sv):
                d = sv.data or {}
                return ok(
                    request_id,
                    {
                        "imageData": d.get("imageData"),
                        "width": d.get("width", 320),
                        "height": d.get("height", 180),
                        "format": d.get("format", "png"),
                        "degraded": True,
                        "degradeLevel": "scene_view_fallback",
                        "requestedWindowTitle": window_title,
                        "note": "Editor window capture missing; substituted Scene view.",
                    },
                )
            if mode == "scene":
                return sv

        if mode in ("auto", "minimal"):
            detail = ""
            if primary.error:
                detail = primary.error.message or primary.error.code
            return ok(
                request_id,
                {
                    "imageData": _MIN_PLACEHOLDER_PNG_B64,
                    "width": 1,
                    "height": 1,
                    "format": "png",
                    "degraded": True,
                    "degradeLevel": "minimal_placeholder",
                    "requestedWindowTitle": window_title,
                    "note": "Placeholder PNG; set UPILOT_SCREENSHOT_DEGRADE=none for strict errors only.",
                    "originalError": detail or "empty_or_missing_imageData",
                },
            )

        return primary
