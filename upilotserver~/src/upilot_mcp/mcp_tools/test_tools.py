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

@mcp.tool(description="运行 Unity 测试（支持 EditMode 和 PlayMode）。")
async def unity_test_run(testMode: str = "EditMode", testFilter: str = ""):
    _log_tool_call("unity_test_run", {"testMode": testMode, "testFilter": testFilter})
    r = await _get_facade().test_run(test_mode=testMode, test_filter=testFilter)
    return _log_tool_result("unity_test_run", _payload(r))

@mcp.tool(description="获取最近一次 Unity 测试运行的结果。")
async def unity_test_results():
    _log_tool_call("unity_test_results", {})
    r = await _get_facade().test_results()
    return _log_tool_result("unity_test_results", _payload(r))

@mcp.tool(description="列出 Unity 项目中所有可用的测试用例。")
async def unity_test_list(testMode: str = "EditMode"):
    _log_tool_call("unity_test_list", {"testMode": testMode})
    r = await _get_facade().test_list(test_mode=testMode)
    return _log_tool_result("unity_test_list", _payload(r))

@mcp.tool(
    description="一次性获取全部诊断信息：窗口布局诊断 + 控制台摘要 + 编辑器状态。免去多次调用。"
)
async def unity_batch_diagnostics():
    _log_tool_call("unity_batch_diagnostics", {})
    r = await _get_facade().batch_diagnostics()
    return _log_tool_result("unity_batch_diagnostics", _payload(r))

@mcp.tool(
    description="全自动窗口验收：等编译完成 → 截图（可选） + 窗口布局诊断 + 控制台摘要，一次调用完成所有验收步骤。screenshotDegrade 同 unity_screenshot_editor_window。"
)
async def unity_verify_window(
    windowTitle: str = "upilot",
    includeScreenshot: bool = True,
    screenshotDegrade: str = "auto",
):
    _log_tool_call(
        "unity_verify_window",
        {
            "windowTitle": windowTitle,
            "includeScreenshot": includeScreenshot,
            "screenshotDegrade": screenshotDegrade,
        },
    )
    r = await _get_facade().verify_window(
        window_title=windowTitle,
        include_screenshot=includeScreenshot,
        screenshot_degrade=screenshotDegrade,
    )
    return _log_tool_result("unity_verify_window", _payload(r))

@mcp.tool(
    description=(
        "M26：从磁盘路径加载 YAML 规格并执行编辑器 E2E（setup/steps/teardown），"
        "断言 console/截图等；失败时在 artifactDir 写入 report.json 与附件。"
        "M27：exportZip 打包 e2e-bundle.zip；webhookOnFailure 在失败时 POST UPILOT_E2E_WEBHOOK_URL。"
        "只用于已有 YAML 规格的端到端验收；不要把它当作通用 UI 操作工具。"
    ),
)
async def unity_editor_e2e_run(
    specPath: str,
    artifactDir: str = "",
    stopOnFirstFailure: bool = True,
    exportZip: bool = False,
    webhookOnFailure: bool = False,
):
    _log_tool_call(
        "unity_editor_e2e_run",
        {
            "specPath": specPath,
            "artifactDir": artifactDir,
            "stopOnFirstFailure": stopOnFirstFailure,
            "exportZip": exportZip,
            "webhookOnFailure": webhookOnFailure,
        },
    )
    r = await _get_facade().editor_e2e_run(
        spec_path=specPath,
        artifact_dir=artifactDir or None,
        stop_on_first_failure=stopOnFirstFailure,
        export_zip=exportZip,
        webhook_on_failure=webhookOnFailure,
    )
    return _log_tool_result("unity_editor_e2e_run", _payload(r))

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
