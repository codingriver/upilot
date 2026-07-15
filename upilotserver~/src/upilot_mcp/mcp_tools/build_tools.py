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

@mcp.tool(description="启动 Unity Player 构建（支持指定平台、输出路径和场景列表）。")
async def unity_build_start(
    buildTarget: str = "StandaloneWindows64",
    outputPath: str = "Builds/",
    scenes: list[str] | None = None,
):
    _log_tool_call(
        "unity_build_start",
        {"buildTarget": buildTarget, "outputPath": outputPath, "scenes": scenes},
    )
    r = await _get_facade().build_start(
        build_target=buildTarget, output_path=outputPath, scenes=scenes
    )
    return _log_tool_result("unity_build_start", _payload(r))

@mcp.tool(description="获取当前 Unity 构建任务的状态和进度。")
async def unity_build_status():
    _log_tool_call("unity_build_status", {})
    r = await _get_facade().build_status()
    return _log_tool_result("unity_build_status", _payload(r))

@mcp.tool(
    description="取消正在进行的 Unity 构建任务。会中断当前 Player 构建；仅在用户要求停止构建或构建卡住时使用。"
)
async def unity_build_cancel():
    _log_tool_call("unity_build_cancel", {})
    r = await _get_facade().build_cancel()
    return _log_tool_result("unity_build_cancel", _payload(r))

@mcp.tool(description="获取当前 Unity 安装中支持的构建目标平台列表。")
async def unity_build_targets():
    _log_tool_call("unity_build_targets", {})
    r = await _get_facade().build_targets()
    return _log_tool_result("unity_build_targets", _payload(r))

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
