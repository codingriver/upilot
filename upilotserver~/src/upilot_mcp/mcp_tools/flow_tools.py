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

@mcp.tool(description="校验单个 UPilot Flow YAML 的 schemaVersion、结构和动作名称。Flow 未启用时该工具不会注册。")
async def unity_upilot_flow_validate(yamlPath: str):
    _log_tool_call("unity_upilot_flow_validate", {"yamlPath": yamlPath})
    r = await _get_facade().upilot_flow_validate(yaml_path=yamlPath)
    return _log_tool_result("unity_upilot_flow_validate", _payload(r))

@mcp.tool(description="将旧 UIFlow/UPilot Flow YAML 迁移到 schemaVersion 2；默认 dryRun=true，仅返回字段变化和目标文件。")
async def unity_upilot_flow_migrate(
    yamlPaths: list[str] | None = None,
    yamlDirectory: str = "",
    targetDirectory: str = "",
    dryRun: bool = True,
):
    _log_tool_call("unity_upilot_flow_migrate", {
        "yamlPaths": yamlPaths,
        "yamlDirectory": yamlDirectory,
        "targetDirectory": targetDirectory,
        "dryRun": dryRun,
    })
    r = await _get_facade().upilot_flow_migrate(
        yaml_paths=yamlPaths,
        yaml_directory=yamlDirectory,
        target_directory=targetDirectory,
        dry_run=dryRun,
    )
    return _log_tool_result("unity_upilot_flow_migrate", _payload(r))

@mcp.tool(
    description=(
        "通过 Unity 内置 C# TestRunner.RunFileAsync 执行 UPilot Flow YAML 文件。"
        "要求项目内可解析 CodingRiver.UPilot.Flow.TestRunner、启用 UPilot Flow 且 YAML 含有效 host_window。"
        "只用于 YAML 驱动的 EditorWindow 自动化，不用于 Game View 或运行时 UI。"
    ),
)
async def unity_upilot_flow_run_file(
    yamlPath: str,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = True,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
):
    _log_tool_call(
        "unity_upilot_flow_run_file",
        {
            "yamlPath": yamlPath,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    r = await _run_unity_upilot_flow_file(
        yaml_path=yamlPath,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
        debug_on_failure=debugOnFailure,
    )
    return _log_tool_result("unity_upilot_flow_run_file", _payload(r))

@mcp.tool(
    description=(
        "通过 Unity 内置 C# TestRunner.RunSuiteAsync 执行 UPilot Flow YAML 目录。"
        "directoryPath 应指向包含 .yaml 的目录。只用于 UPilot Flow EditorWindow YAML 套件，不用于普通 Unity Test Runner。"
    ),
)
async def unity_upilot_flow_run_suite(
    directoryPath: str,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
):
    _log_tool_call(
        "unity_upilot_flow_run_suite",
        {
            "directoryPath": directoryPath,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
        },
    )
    r = await _run_unity_upilot_flow_suite(
        directory_path=directoryPath,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
    )
    return _log_tool_result("unity_upilot_flow_run_suite", _payload(r))

@mcp.tool(
    description=(
        "批量执行指定的 UPilot Flow YAML 文件列表，支持分批次运行以避免超时。"
        "yamlPaths 为文件路径列表；batchSize 控制每批数量（默认 10），"
        "batchOffset 为起始偏移；totalAll 为所有批次的总文件数（用于显示整体进度）。"
        "返回结果中包含 hasMore/nextOffset，客户端可根据 hasMore 继续发起下一批。"
        "只用于 UPilot Flow YAML；大量用例优先分批，失败后查询结果再决定是否继续。"
    ),
)
async def unity_upilot_flow_run_batch(
    yamlPaths: list[str],
    batchSize: int = 10,
    batchOffset: int = 0,
    totalAll: int = 0,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
):
    _log_tool_call(
        "unity_upilot_flow_run_batch",
        {
            "yamlPaths": yamlPaths,
            "batchSize": batchSize,
            "batchOffset": batchOffset,
            "totalAll": totalAll,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    r = await _run_unity_upilot_flow_batch(
        yaml_paths=yamlPaths,
        batch_size=batchSize,
        batch_offset=batchOffset,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
        debug_on_failure=debugOnFailure,
        total_all=totalAll,
    )
    return _log_tool_result("unity_upilot_flow_run_batch", _payload(r))

@mcp.tool(
    description=(
        "强制重置 UPilot Flow 执行状态。无需 executionId，直接释放 EDITOR_BUSY 锁，"
        "Dispose 当前 ExecutionContext，关闭测试窗口，并将所有进行中的执行标记为 aborted。"
    ),
)
async def unity_upilot_flow_force_reset():
    _log_tool_call("unity_upilot_flow_force_reset", {})
    r = await _get_facade().upilot_flow_force_reset()
    return _log_tool_result("unity_upilot_flow_force_reset", _payload(r))

@mcp.tool(
    description=(
        "异步启动 UPilot Flow 批量测试，立即返回 executionId，不等待执行完成。"
        "客户端拿到 executionId 后需自行调用 unity_upilot_flow_results 轮询进度。"
        "参数与 unity_upilot_flow_run_batch 相同。适合长套件；不要启动后不轮询结果。"
    ),
)
async def unity_upilot_flow_run_async(
    yamlPaths: list[str],
    batchSize: int = 10,
    batchOffset: int = 0,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
):
    _log_tool_call(
        "unity_upilot_flow_run_async",
        {
            "yamlPaths": yamlPaths,
            "batchSize": batchSize,
            "batchOffset": batchOffset,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    resolved = []
    for p in yamlPaths:
        rp = str(Path(p).expanduser().resolve())
        if not Path(rp).is_file():
            return _payload(
                fail(
                    new_id("upilot_flow"),
                    "UIFLOW_YAML_NOT_FOUND",
                    f"YAML file not found: {rp}",
                    {"yamlPath": rp},
                )
            )
        resolved.append(rp)

    report_root = reportOutputPath.strip() or "Reports/UPilot/Flow"
    run_resp = await _get_facade().upilot_flow_run(
        yaml_paths=resolved,
        headed=headed,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        screenshot_on_failure=screenshotOnFailure,
        default_timeout_ms=defaultTimeoutMs,
        enable_verbose_log=enableVerboseLog,
        report_path=report_root,
        debug_on_failure=debugOnFailure,
        batch_size=batchSize,
        batch_offset=batchOffset,
    )
    if not run_resp.ok:
        return _log_tool_result("unity_upilot_flow_run_async", _payload(run_resp))

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return _log_tool_result(
            "unity_upilot_flow_run_async",
            _payload(
                fail(
                    new_id("upilot_flow"),
                    "UIFLOW_EXECUTION_ID_MISSING",
                    "upilot_flow.run did not return executionId",
                    {"response": run_data},
                )
            ),
        )

    return _log_tool_result(
        "unity_upilot_flow_run_async",
        json.dumps(
            {
                "ok": True,
                "data": {
                    "executionId": execution_id,
                    "status": run_data.get("status", "queued"),
                    "total": int(run_data.get("total") or 0),
                    "hasMore": bool(run_data.get("hasMore")),
                    "nextOffset": int(run_data.get("nextOffset") or 0),
                    "totalAll": int(run_data.get("totalAll") or 0),
                    "reportOutputPath": report_root,
                    "screenshotPath": screenshotPath.strip() or str((Path(report_root) / "Screenshots").as_posix()),
                },
                "error": None,
                "requestId": run_resp.request_id,
                "timestamp": run_resp.timestamp,
            }
        ),
    )

@mcp.tool(
    description=(
        "查询指定 executionId 的 UPilot Flow 执行状态和结果。"
        "返回包含 status、cases 列表、passed/failed/errors/skipped 计数等字段。"
    ),
)
async def unity_upilot_flow_results(
    executionId: str,
):
    _log_tool_call("unity_upilot_flow_results", {"executionId": executionId})
    r = await _get_facade().upilot_flow_results(execution_id=executionId)
    return _log_tool_result("unity_upilot_flow_results", _payload(r))

async def _run_unity_upilot_flow_file(
    yaml_path: str,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
    debug_on_failure: bool,
) -> ToolResponse:
    resolved_yaml = str(Path(yaml_path).expanduser().resolve())
    if not Path(resolved_yaml).is_file():
        return fail(
            new_id("upilot_flow"),
            "UIFLOW_YAML_NOT_FOUND",
            f"YAML file not found: {resolved_yaml}",
            {"yamlPath": resolved_yaml},
        )

    report_root = report_output_path.strip() or "Reports/UPilot/Flow"
    run_resp = await _get_facade().upilot_flow_run(
        yaml_paths=[resolved_yaml],
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
        debug_on_failure=debug_on_failure,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("upilot_flow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "upilot_flow.run did not return executionId",
            {"response": run_data},
        )

    deadline = time.monotonic() + max(60.0, default_timeout_ms / 1000.0 + 180.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().upilot_flow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[UPilot Flow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[UPilot Flow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(60.0, default_timeout_ms / 1000.0 + 120.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            case = ((last_data.get("cases") or [None])[0]) or {}
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_root) / execution_id / "Screenshots").as_posix()
            )
            return ok(
                new_id("upilot_flow"),
                {
                    "yamlPath": resolved_yaml,
                    "reportOutputPath": str(last_data.get("reportPath") or report_root),
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "caseName": case.get("caseName")
                        or last_data.get("currentCaseName")
                        or Path(resolved_yaml).stem,
                        "errorCode": case.get("errorCode")
                        or last_data.get("errorCode")
                        or "",
                        "errorMessage": case.get("errorMessage")
                        or last_data.get("errorMessage")
                        or "",
                        "reportPath": last_data.get("reportPath") or report_root,
                        "raw": last_data,
                    },
                },
            )

    logger.error("[UPilot Flow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(60.0, default_timeout_ms / 1000.0 + 120.0), last_status)
    await _get_facade().upilot_flow_cancel(execution_id)
    return fail(
        new_id("upilot_flow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for upilot_flow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )

async def _run_unity_upilot_flow_suite(
    directory_path: str,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
) -> ToolResponse:
    resolved_dir = str(Path(directory_path).expanduser().resolve())
    if not Path(resolved_dir).is_dir():
        return fail(
            new_id("upilot_flow"),
            "UIFLOW_SUITE_DIR_NOT_FOUND",
            f"Suite directory not found: {resolved_dir}",
            {"directoryPath": resolved_dir},
        )

    report_root = report_output_path.strip() or "Reports/UPilot/Flow"
    run_resp = await _get_facade().upilot_flow_run(
        yaml_directory=resolved_dir,
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("upilot_flow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "upilot_flow.run did not return executionId",
            {"response": run_data},
        )

    deadline = time.monotonic() + max(120.0, default_timeout_ms / 1000.0 + 360.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().upilot_flow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[UPilot Flow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[UPilot Flow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(120.0, default_timeout_ms / 1000.0 + 300.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            report_path = str(last_data.get("reportPath") or report_root)
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_path) / "Screenshots").as_posix()
            )
            failed = int(last_data.get("failed") or 0)
            errors = int(last_data.get("errors") or 0)
            exit_code = (
                0 if status == "completed" and failed == 0 and errors == 0 else 1
            )
            return ok(
                new_id("upilot_flow"),
                {
                    "directoryPath": resolved_dir,
                    "reportOutputPath": report_path,
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "total": int(last_data.get("total") or 0),
                        "passed": int(last_data.get("passed") or 0),
                        "failed": failed,
                        "errors": errors,
                        "skipped": int(last_data.get("skipped") or 0),
                        "exitCode": exit_code,
                        "raw": last_data,
                    },
                },
            )

    logger.error("[UPilot Flow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(120.0, default_timeout_ms / 1000.0 + 300.0), last_status)
    await _get_facade().upilot_flow_cancel(execution_id)
    return fail(
        new_id("upilot_flow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for upilot_flow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )

async def _run_unity_upilot_flow_batch(
    yaml_paths: list[str],
    batch_size: int,
    batch_offset: int,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
    debug_on_failure: bool,
    total_all: int = 0,
) -> ToolResponse:
    resolved = []
    for p in yaml_paths:
        rp = str(Path(p).expanduser().resolve())
        if not Path(rp).is_file():
            return fail(
                new_id("upilot_flow"),
                "UIFLOW_YAML_NOT_FOUND",
                f"YAML file not found: {rp}",
                {"yamlPath": rp},
            )
        resolved.append(rp)

    # totalAll is the overall number of yaml files across all batches.
    # If not provided by caller, infer: resolved list is all files (no external slicing).
    effective_total_all = total_all if total_all > 0 else len(resolved)

    report_root = report_output_path.strip() or "Reports/UPilot/Flow"
    run_resp = await _get_facade().upilot_flow_run(
        yaml_paths=resolved,
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
        debug_on_failure=debug_on_failure,
        batch_size=batch_size,
        batch_offset=batch_offset,
        total_all=effective_total_all,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("upilot_flow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "upilot_flow.run did not return executionId",
            {"response": run_data},
        )

    # Deadline: allow enough time for the current batch (batch_size files)
    deadline = time.monotonic() + max(120.0, default_timeout_ms / 1000.0 * batch_size + 180.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().upilot_flow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[UPilot Flow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[UPilot Flow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(120.0, default_timeout_ms / 1000.0 * batch_size + 120.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            report_path = str(last_data.get("reportPath") or report_root)
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_path) / "Screenshots").as_posix()
            )
            return ok(
                new_id("upilot_flow"),
                {
                    "yamlPaths": resolved,
                    "batchSize": batch_size,
                    "batchOffset": batch_offset,
                    "reportOutputPath": report_path,
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "total": int(last_data.get("total") or 0),
                        "passed": int(last_data.get("passed") or 0),
                        "failed": int(last_data.get("failed") or 0),
                        "errors": int(last_data.get("errors") or 0),
                        "skipped": int(last_data.get("skipped") or 0),
                        "hasMore": bool(last_data.get("hasMore")),
                        "nextOffset": int(last_data.get("nextOffset") or 0),
                        "totalAll": int(last_data.get("totalAll") or 0),
                        "raw": last_data,
                    },
                },
            )

    logger.error("[UPilot Flow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(120.0, default_timeout_ms / 1000.0 * batch_size + 120.0), last_status)
    await _get_facade().upilot_flow_cancel(execution_id)
    return fail(
        new_id("upilot_flow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for upilot_flow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )

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
