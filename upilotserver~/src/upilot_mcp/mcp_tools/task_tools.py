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
    description="预检测试环境就绪：检查 Unity 连接 + 编译完成 + 编辑模式。返回 ready=true/false 及各项状态。"
)
async def unity_ensure_ready(timeoutS: float = 120):
    _log_tool_call("unity_ensure_ready", {"timeoutS": timeoutS})
    r = await _get_facade().ensure_ready(timeout_s=timeoutS)
    return _log_tool_result("unity_ensure_ready", _payload(r))

@mcp.tool(
    description="带超时看门狗执行另一个 MCP 工具。超时后可尝试重连 Unity 并重试；适合长耗时或可能卡住的幂等操作。不要包裹非幂等/破坏性操作重试，除非用户已确认可重复执行。"
)
async def unity_task_execute(
    taskName: str,
    toolName: str,
    toolArgs: dict | None = None,
    timeoutS: float = 600,
    maxTotalS: float = 1200,
    retryCount: int = 1,
    restartUnityOnTimeout: bool = True,
):
    _log_tool_call(
        "unity_task_execute",
        {
            "taskName": taskName,
            "toolName": toolName,
            "toolArgs": toolArgs,
            "timeoutS": timeoutS,
            "maxTotalS": maxTotalS,
            "retryCount": retryCount,
            "restartUnityOnTimeout": restartUnityOnTimeout,
        },
    )
    r = await _get_facade().task_execute(
        task_name=taskName,
        tool_name=toolName,
        tool_args=toolArgs,
        timeout_s=timeoutS,
        max_total_s=maxTotalS,
        retry_count=retryCount,
        restart_unity_on_timeout=restartUnityOnTimeout,
    )
    return _log_tool_result("unity_task_execute", _payload(r))

@mcp.tool(description="异步启动一个公开 UPilot MCP 工具调用，立即返回 taskId。")
async def unity_task_start(
    taskName: str,
    toolName: str,
    toolArgs: dict | None = None,
    timeoutS: float = 600,
    retryCount: int = 0,
):
    _log_tool_call("unity_task_start", {"taskName": taskName, "toolName": toolName, "timeoutS": timeoutS, "retryCount": retryCount})
    r = await _get_facade().task_start(
        task_name=taskName,
        tool_name=toolName,
        tool_args=toolArgs,
        timeout_s=timeoutS,
        retry_count=retryCount,
    )
    return _log_tool_result("unity_task_start", _payload(r))

@mcp.tool(description="读取异步 UPilot 任务的状态、阶段、耗时、结果或错误。")
async def unity_task_status(taskId: str):
    _log_tool_call("unity_task_status", {"taskId": taskId})
    r = await _get_facade().task_status(task_id=taskId)
    return _log_tool_result("unity_task_status", _payload(r))

@mcp.tool(description="取消一个正在运行的异步 UPilot 任务。")
async def unity_task_cancel(taskId: str):
    _log_tool_call("unity_task_cancel", {"taskId": taskId})
    r = await _get_facade().task_cancel(task_id=taskId)
    return _log_tool_result("unity_task_cancel", _payload(r))

@mcp.tool(description="列出 Unity Bridge 最近操作，包含阶段、步骤、耗时、错误和卡住状态。")
async def unity_operation_list(status: str = "", limit: int = 50):
    _log_tool_call("unity_operation_list", {"status": status, "limit": limit})
    r = await _get_facade().operation_list(status=status, limit=limit)
    return _log_tool_result("unity_operation_list", _payload(r))

@mcp.tool(description="按 commandId 读取一个 Unity Bridge 操作的完整步骤和计时。")
async def unity_operation_get(commandId: str):
    _log_tool_call("unity_operation_get", {"commandId": commandId})
    r = await _get_facade().operation_get(command_id=commandId)
    return _log_tool_result("unity_operation_get", _payload(r))

@mcp.tool(
    description=(
        "在 Unity 主线程延迟指定毫秒（不阻塞 Python）。用于等待编辑器布局；"
        "delayMs 最大 120000。"
    ),
)
async def unity_editor_delay(delayMs: int = 100):
    _log_tool_call("unity_editor_delay", {"delayMs": delayMs})
    r = await _get_facade().editor_delay(delay_ms=delayMs)
    return _log_tool_result("unity_editor_delay", _payload(r))

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
