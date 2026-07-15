from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from pathlib import Path
from typing import Annotated, Any

from pydantic import Field
from ..models import ToolResponse
from ..protocol import new_id
from ..responses import fail, ok
from ..tool_registry import REGISTRY, register_public_tool
from .. import mcp_stdio_server as runtime

mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result
_reject_compile_in_playmode = runtime._reject_compile_in_playmode
CONFIG = runtime.CONFIG
logger = logging.getLogger("upilot.mcp")

@mcp.tool(description="截取 Unity Game 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_game_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
):
    _log_tool_call(
        "unity_screenshot_game_view",
        {"width": width, "height": height, "format": format, "quality": quality},
    )
    r = await _get_facade().screenshot_game_view(
        width=width, height=height, format=format, quality=quality
    )
    return _log_tool_result("unity_screenshot_game_view", _payload(r))

@mcp.tool(description="截取 Unity Scene 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_scene_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
):
    _log_tool_call(
        "unity_screenshot_scene_view",
        {"width": width, "height": height, "format": format, "quality": quality},
    )
    r = await _get_facade().screenshot_scene_view(
        width=width, height=height, format=format, quality=quality
    )
    return _log_tool_result("unity_screenshot_scene_view", _payload(r))

@mcp.tool(description="截取指定 Camera 的画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_camera(
    cameraName: str,
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
):
    _log_tool_call(
        "unity_screenshot_camera",
        {
            "cameraName": cameraName,
            "width": width,
            "height": height,
            "format": format,
            "quality": quality,
        },
    )
    r = await _get_facade().screenshot_camera(
        camera_name=cameraName,
        width=width,
        height=height,
        format=format,
        quality=quality,
    )
    return _log_tool_result("unity_screenshot_camera", _payload(r))

@mcp.tool(
    description="截取 Unity 画面并保存为 .png，成功后返回完整路径、大小、分辨率和 sha256。path 可为空；为空时保存到当前 Unity 工程 Log/UPilotScreenshots。source: gameView|sceneView|camera|editorWindow。默认只允许写入当前 Unity 工程目录内。"
)
async def unity_screenshot_save(
    path: str = "",
    source: str = "gameView",
    overwrite: bool = False,
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
    cameraName: str = "",
    windowTitle: str = "Game",
    allowOutsideProject: bool = False,
):
    _log_tool_call(
        "unity_screenshot_save",
        {
            "path": path,
            "source": source,
            "overwrite": overwrite,
            "width": width,
            "height": height,
            "format": format,
            "quality": quality,
            "cameraName": cameraName,
            "windowTitle": windowTitle,
            "allowOutsideProject": allowOutsideProject,
        },
    )
    r = await _get_facade().screenshot_save(
        path=path,
        source=source,
        overwrite=overwrite,
        width=width,
        height=height,
        format=format,
        quality=quality,
        camera_name=cameraName,
        window_title=windowTitle,
        allow_outside_project=allowOutsideProject,
    )
    return _log_tool_result("unity_screenshot_save", _payload(r))

@mcp.tool(
    description="截取 Unity 编辑器窗口（EditorWindow）画面，返回 Base64 编码的 PNG。通过窗口标题匹配。screenshotDegrade: none|auto|scene|minimal — auto 在无法截取窗口时降级为 Scene 视图或占位图。"
)
async def unity_screenshot_editor_window(
    windowTitle: str = "upilot",
    screenshotDegrade: str = "auto",
):
    _log_tool_call(
        "unity_screenshot_editor_window",
        {"windowTitle": windowTitle, "screenshotDegrade": screenshotDegrade},
    )
    r = await _get_facade().screenshot_editor_window(
        window_title=windowTitle, degrade=screenshotDegrade
    )
    return _log_tool_result("unity_screenshot_editor_window", _payload(r))

_DESTRUCTIVE_TOOLS = {
    "unity_asset_delete", "unity_asset_move", "unity_asset_modify_data",
    "unity_script_create", "unity_script_update", "unity_script_delete",
    "unity_package_add", "unity_package_remove", "unity_scene_save",
    "unity_scene_unload", "unity_gameobject_delete", "unity_component_remove",
}
_HIDDEN_PUBLIC_TOOLS = {"unity_upilot_flow_run_batch"}
_PLAYMODE_BLOCKED = {"unity_compile", "unity_auto_fix_start", "unity_safe_compile_and_wait"}
for _name, _value in list(globals().items()):
    if not callable(_value) or not (_name.startswith("unity_") or _name == "reflection_eval"):
        continue
    if _name in _HIDDEN_PUBLIC_TOOLS:
        continue
    register_public_tool(
        _name,
        destructive=_name in _DESTRUCTIVE_TOOLS,
        idempotent=_name not in _DESTRUCTIVE_TOOLS,
        play_mode_policy="blocked" if _name in _PLAYMODE_BLOCKED else "allowed",
        feature="flow" if _name.startswith("unity_upilot_flow_") else "core",
    )
