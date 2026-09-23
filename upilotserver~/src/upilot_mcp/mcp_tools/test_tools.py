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
_reject_write_if_unapproved = runtime._reject_write_if_unapproved
CONFIG = runtime.CONFIG
logger = logging.getLogger("upilot.mcp")

@mcp.tool(description="运行 Unity 测试（支持 EditMode 和 PlayMode）。")
async def unity_test_run(
    testMode: str = "EditMode", testFilter: str | None = None,
    testNames: list[str] | None = None, fixtures: list[str] | None = None,
    assemblies: list[str] | None = None, categories: list[str] | None = None, matchMode: str = "union",
    requireAllSelectorsMatch: bool = True, expectedSelectionDomain: str = "",
    expectedSelectionSnapshotId: str = "",
):
    _log_tool_call("unity_test_run", {"testMode": testMode, "testFilter": testFilter, "testNames": testNames, "fixtures": fixtures, "assemblies": assemblies, "categories": categories, "matchMode": matchMode, "requireAllSelectorsMatch": requireAllSelectorsMatch, "expectedSelectionDomain": expectedSelectionDomain, "expectedSelectionSnapshotId": expectedSelectionSnapshotId})
    r = await _get_facade().test_run(test_mode=testMode, test_filter=testFilter, test_names=testNames, fixtures=fixtures, assemblies=assemblies, categories=categories, match_mode=matchMode, require_all_selectors_match=requireAllSelectorsMatch, expected_selection_domain=expectedSelectionDomain, expected_selection_snapshot_id=expectedSelectionSnapshotId)
    return _log_tool_result("unity_test_run", _payload(r))

@mcp.tool(description="获取最近一次或指定 runGuid 的 Unity 测试结果；cursor='begin' 开始只读增量叶子事件，后续使用 nextCursor。省略 cursor 保留完整结果兼容行为。")
async def unity_test_results(
    runGuid: str = "",
    cursor: str = "",
    count: Annotated[int, Field(strict=True, ge=1, le=1000)] = 100,
):
    _log_tool_call("unity_test_results", {"runGuid": runGuid, "cursor": cursor, "count": count})
    r = await _get_facade().test_results(run_guid=runGuid, cursor=cursor, count=count)
    return _log_tool_result("unity_test_results", _payload(r))

@mcp.tool(description="获取当前 Unity Test Runner 运行 GUID、当前测试、进度时间与取消/清理状态。")
async def unity_test_status():
    _log_tool_call("unity_test_status", {})
    r = await _get_facade().test_status()
    return _log_tool_result("unity_test_status", _payload(r))

@mcp.tool(description="通过 Unity Test Framework 官方 run GUID 取消当前测试；重复调用是幂等的，终态以 RunFinished 与清理完成为准。")
async def unity_test_cancel(runGuid: str = ""):
    _log_tool_call("unity_test_cancel", {"runGuid": runGuid})
    r = await _get_facade().test_cancel(run_guid=runGuid)
    return _log_tool_result("unity_test_cancel", _payload(r))

@mcp.tool(description="请求停止并清理当前 Unity Test Runner 作业；不会在后台测试仍运行时伪造终态。")
async def unity_test_force_cleanup(runGuid: str = ""):
    _log_tool_call("unity_test_force_cleanup", {"runGuid": runGuid})
    r = await _get_facade().test_force_cleanup(run_guid=runGuid)
    return _log_tool_result("unity_test_force_cleanup", _payload(r))

@mcp.tool(description="兼容旧名称；等价于 unity_test_force_cleanup，不会伪造测试终态。")
async def unity_test_force_reset():
    _log_tool_call("unity_test_force_reset", {})
    r = await _get_facade().test_force_reset()
    return _log_tool_result("unity_test_force_reset", _payload(r))

@mcp.tool(description="列出 Unity 项目中可用测试，并返回程序集边界、发现数量与过滤命中数量。")
async def unity_test_list(
    testMode: str = "EditMode", testFilter: str | None = None,
    testNames: list[str] | None = None, fixtures: list[str] | None = None,
    assemblies: list[str] | None = None, categories: list[str] | None = None, matchMode: str = "union",
    requireAllSelectorsMatch: bool = True,
):
    _log_tool_call("unity_test_list", {"testMode": testMode, "testFilter": testFilter, "testNames": testNames, "fixtures": fixtures, "assemblies": assemblies, "categories": categories, "matchMode": matchMode, "requireAllSelectorsMatch": requireAllSelectorsMatch})
    r = await _get_facade().test_list(test_mode=testMode, test_filter=testFilter, test_names=testNames, fixtures=fixtures, assemblies=assemblies, categories=categories, match_mode=matchMode, require_all_selectors_match=requireAllSelectorsMatch)
    return _log_tool_result("unity_test_list", _payload(r))

@mcp.tool(description="提交持久 UPilot 包验收 Task，立即返回 taskId/queued；轮询 unity_task_status 查询测试、清理和带 hash 的 summary。preflightOnly=true 只同步预检，不创建 Task。")
async def unity_upilot_acceptance_run(
    testMode: str = "EditMode", testFilter: str | None = None, timeoutSec: float = 900,
    stopActiveCaptures: bool = True, requireTests: bool = True, writeArtifact: bool = True,
    testNames: list[str] | None = None, fixtures: list[str] | None = None,
    assemblies: list[str] | None = None, categories: list[str] | None = None, matchMode: str = "union",
    requireAllSelectorsMatch: bool = True, expectedSelectionDomain: str = "",
    expectedSelectionSnapshotId: str = "", preflightOnly: bool = False,
):
    args = {"testMode": testMode, "testFilter": testFilter, "timeoutSec": timeoutSec,
            "stopActiveCaptures": stopActiveCaptures, "requireTests": requireTests, "writeArtifact": writeArtifact,
            "testNames": testNames, "fixtures": fixtures, "assemblies": assemblies, "categories": categories, "matchMode": matchMode, "requireAllSelectorsMatch": requireAllSelectorsMatch, "expectedSelectionDomain": expectedSelectionDomain, "expectedSelectionSnapshotId": expectedSelectionSnapshotId, "preflightOnly": preflightOnly}
    _log_tool_call("unity_upilot_acceptance_run", args)
    if not preflightOnly:
        rejected = _reject_write_if_unapproved("unity_upilot_acceptance_run")
        if rejected is not None:
            return rejected
    r = await _get_facade().upilot_acceptance_run(
        test_mode=testMode, test_filter=testFilter, timeout_sec=timeoutSec,
        stop_active_captures=stopActiveCaptures, require_tests=requireTests, write_artifact=writeArtifact,
        test_names=testNames, fixtures=fixtures, assemblies=assemblies, categories=categories, match_mode=matchMode, require_all_selectors_match=requireAllSelectorsMatch, expected_selection_domain=expectedSelectionDomain, expected_selection_snapshot_id=expectedSelectionSnapshotId, preflight_only=preflightOnly,
    )
    return _log_tool_result("unity_upilot_acceptance_run", _payload(r))

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
    windowTitle: str = "",
    includeScreenshot: bool = True,
    screenshotDegrade: str = "auto",
    instanceId: str = "",
    domainGeneration: str = "",
    fullTypeName: str = "",
):
    _log_tool_call(
        "unity_verify_window",
        {
            "windowTitle": windowTitle,
            "includeScreenshot": includeScreenshot,
            "screenshotDegrade": screenshotDegrade,
            "instanceId": instanceId,
            "domainGeneration": domainGeneration,
            "fullTypeName": fullTypeName,
        },
    )
    r = await _get_facade().verify_window(
        window_title=windowTitle,
        include_screenshot=includeScreenshot,
        screenshot_degrade=screenshotDegrade,
        instance_id=instanceId,
        domain_generation=domainGeneration,
        full_type_name=fullTypeName,
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
_NON_IDEMPOTENT_TOOLS = {"unity_upilot_acceptance_run", "unity_test_run", "unity_test_cancel", "unity_test_force_cleanup", "unity_test_force_reset"}
_HIDDEN_PUBLIC_TOOLS = {"unity_upilot_flow_run_batch"}
_PLAYMODE_BLOCKED = {"unity_compile", "unity_auto_fix_start", "unity_safe_compile_and_wait"}
for _name, _value in list(globals().items()):
    if not callable(_value) or not _name.startswith("unity_"):
        continue
    if _name in _HIDDEN_PUBLIC_TOOLS:
        continue
    register_public_tool(
        _name,
        public_handler=_value,
        destructive=_name in _DESTRUCTIVE_TOOLS,
        write_access_predicate=(
            (lambda arguments: not bool(arguments.get("preflight_only")))
            if _name == "unity_upilot_acceptance_run"
            else None
        ),
        write_access_condition=(
            "preflightOnly=false"
            if _name == "unity_upilot_acceptance_run"
            else ""
        ),
        idempotent=_name not in (_DESTRUCTIVE_TOOLS | _NON_IDEMPOTENT_TOOLS),
        play_mode_policy="blocked" if _name in _PLAYMODE_BLOCKED else "allowed",
        feature="flow" if _name.startswith("unity_upilot_flow_") else "core",
    )
