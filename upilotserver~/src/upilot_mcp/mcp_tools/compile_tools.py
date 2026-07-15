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

@mcp.tool(
    description="触发 Unity 脚本编译请求。会刷新/编译项目代码；PlayMode 下会被拒绝。修改代码后优先用 unity_safe_compile_and_wait 或 compile + compile_wait。"
)
async def unity_compile():
    _log_tool_call("unity_compile", {})
    rejected = await _reject_compile_in_playmode("unity_compile")
    if rejected is not None:
        return rejected
    r = await _get_facade().compile()
    return _log_tool_result("unity_compile", _payload(r))

@mcp.tool(description="获取最近一次编译状态。")
async def unity_compile_status(compileRequestId: str = ""):
    _log_tool_call("unity_compile_status", {"compileRequestId": compileRequestId})
    r = await _get_facade().compile_status(compile_request_id=compileRequestId)
    return _log_tool_result("unity_compile_status", _payload(r))

@mcp.tool(description="获取最近一次结构化编译错误（仅 live，不回退缓存）。")
async def unity_compile_errors(compileRequestId: str = ""):
    _log_tool_call("unity_compile_errors", {"compileRequestId": compileRequestId})
    r = await _get_facade().compile_errors(compile_request_id=compileRequestId)
    return _log_tool_result("unity_compile_errors", _payload(r))

@mcp.tool(
    description="启动自动修复循环。会反复读取编译错误并尝试修复代码，可能写文件并触发编译；适合明确要求自动修复时使用，不要用于普通查询或小改动。"
)
async def unity_auto_fix_start(
    maxIterations: int = 20, stopWhenNoError: bool = True
):
    _log_tool_call(
        "unity_auto_fix_start",
        {"maxIterations": maxIterations, "stopWhenNoError": stopWhenNoError},
    )
    rejected = await _reject_compile_in_playmode("unity_auto_fix_start")
    if rejected is not None:
        return rejected
    r = await _get_facade().auto_fix_start(
        max_iterations=maxIterations, stop_when_no_error=stopWhenNoError
    )
    return _log_tool_result("unity_auto_fix_start", _payload(r))

@mcp.tool(description="停止自动修复循环。")
async def unity_auto_fix_stop(loopId: str):
    _log_tool_call("unity_auto_fix_stop", {"loopId": loopId})
    r = await _get_facade().auto_fix_stop(loop_id=loopId)
    return _log_tool_result("unity_auto_fix_stop", _payload(r))

@mcp.tool(description="读取自动修复循环状态。")
async def unity_auto_fix_status():
    _log_tool_call("unity_auto_fix_status", {})
    r = await _get_facade().auto_fix_status()
    return _log_tool_result("unity_auto_fix_status", _payload(r))

@mcp.tool(
    description=(
        "等待 Unity 脚本编译结束。优先使用 Bridge 推送的 compile.started/finished 与 compile.pipeline.* 信号（wait_for_compile_idle），"
        "再以指数退避轮询 resource.editorState。preferEvents=false 可仅用轮询。返回 waitMode：immediate|event|poll|timeout。"
    ),
)
async def unity_compile_wait(
    timeoutS: float = 300,
    pollIntervalS: float = 1.0,
    preferEvents: bool = True,
):
    _log_tool_call(
        "unity_compile_wait",
        {
            "timeoutS": timeoutS,
            "pollIntervalS": pollIntervalS,
            "preferEvents": preferEvents,
        },
    )
    r = await _get_facade().compile_wait(
        timeout_s=timeoutS,
        poll_interval_s=pollIntervalS,
        prefer_events=preferEvents,
    )
    return _log_tool_result("unity_compile_wait", _payload(r))

@mcp.tool(
    description=(
        "在 Unity 编辑器侧单命令阻塞等待：直到 EditorApplication.isCompiling 为 false（任意来源的编译）。"
        "timeoutMs 默认 120000。适合需要与编辑器内部状态严格对齐的场景。"
    ),
)
async def unity_compile_wait_editor(timeoutMs: int = 300000):
    _log_tool_call("unity_compile_wait_editor", {"timeoutMs": timeoutMs})
    r = await _get_facade().compile_wait_editor(timeout_ms=timeoutMs)
    return _log_tool_result("unity_compile_wait_editor", _payload(r))

@mcp.tool(
    description=(
        "安全编译等待：触发编译后等待完成，并在 Domain Reload 后双重验证编译错误。"
        "Unity 侧会将编译错误持久化到磁盘，Domain Reload 后从磁盘恢复，避免假编译成功。"
        "修改代码后应优先使用本工具替代 unity_compile + unity_compile_wait 的组合。"
    ),
)
async def unity_safe_compile_and_wait(
    timeoutS: float = 300,
    pollIntervalS: float = 1.0,
    preferEvents: bool = True,
    postCompileDelayS: float = 3.0,
):
    _log_tool_call(
        "unity_safe_compile_and_wait",
        {
            "timeoutS": timeoutS,
            "pollIntervalS": pollIntervalS,
            "preferEvents": preferEvents,
            "postCompileDelayS": postCompileDelayS,
        },
    )
    rejected = await _reject_compile_in_playmode("unity_safe_compile_and_wait")
    if rejected is not None:
        return rejected
    r = await _get_facade().safe_compile_and_wait(
        timeout_s=timeoutS,
        poll_interval_s=pollIntervalS,
        prefer_events=preferEvents,
        post_compile_delay_s=postCompileDelayS,
    )
    return _log_tool_result("unity_safe_compile_and_wait", _payload(r))

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
