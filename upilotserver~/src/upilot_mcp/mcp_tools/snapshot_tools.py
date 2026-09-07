from __future__ import annotations

from typing import Any

from .. import mcp_stdio_server as runtime
from ..tool_registry import register_public_tool


mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result


@mcp.tool(description="列出可用于 Snapshot 的活动与非活动 Camera，并返回精确字符串 ID、场景和层级路径。")
async def unity_camera_list():
    _log_tool_call("unity_camera_list", {})
    result = await _get_facade().camera_list()
    return _log_tool_result("unity_camera_list", _payload(result))


@mcp.tool(description="启动统一 Unity Snapshot 作业。支持多目标同帧采集、严格像素来源策略和项目内证据落盘；waitMs 只控制本次等待窗口，不是作业超时。")
async def unity_snapshot_capture(
    targets: list[dict[str, Any]],
    channels: list[str] | None = None,
    syncMode: str = "sameFrame",
    completionPolicy: str = "allOrNothing",
    capturePolicy: dict[str, Any] | None = None,
    outputDirectory: str = "",
    waitMs: int = 5000,
    requestKey: str = "",
):
    args = {
        "targets": targets,
        "channels": channels,
        "syncMode": syncMode,
        "completionPolicy": completionPolicy,
        "capturePolicy": capturePolicy,
        "outputDirectory": outputDirectory,
        "waitMs": waitMs,
        "requestKey": requestKey,
    }
    _log_tool_call("unity_snapshot_capture", args)
    result = await _get_facade().snapshot_capture(
        targets,
        channels=channels,
        sync_mode=syncMode,
        completion_policy=completionPolicy,
        capture_policy=capturePolicy,
        output_directory=outputDirectory,
        wait_ms=waitMs,
        request_key=requestKey,
    )
    return _log_tool_result("unity_snapshot_capture", _payload(result))


@mcp.tool(description="读取统一 Snapshot 作业状态。detailLevel: summary|standard|full。")
async def unity_snapshot_status(snapshotId: str, detailLevel: str = "summary"):
    args = {"snapshotId": snapshotId, "detailLevel": detailLevel}
    _log_tool_call("unity_snapshot_status", args)
    result = await _get_facade().snapshot_status(snapshotId, detailLevel)
    return _log_tool_result("unity_snapshot_status", _payload(result))


@mcp.tool(description="请求取消尚未终结的 Snapshot 作业。取消不会回滚已经生成的诊断产物。")
async def unity_snapshot_cancel(snapshotId: str):
    _log_tool_call("unity_snapshot_cancel", {"snapshotId": snapshotId})
    result = await _get_facade().snapshot_cancel(snapshotId)
    return _log_tool_result("unity_snapshot_cancel", _payload(result))


@mcp.tool(description="收集 Snapshot 的 Manifest 与已生成产物元数据，不返回无界图像内容。")
async def unity_snapshot_collect_artifacts(snapshotId: str):
    _log_tool_call("unity_snapshot_collect_artifacts", {"snapshotId": snapshotId})
    result = await _get_facade().snapshot_collect_artifacts(snapshotId)
    return _log_tool_result("unity_snapshot_collect_artifacts", _payload(result))


@mcp.tool(description="列出 .upilot/snapshots/baselines 下的 Snapshot PNG 基线及其哈希和尺寸。")
async def unity_snapshot_baseline_list(prefix: str = ""):
    args = {"prefix": prefix}
    _log_tool_call("unity_snapshot_baseline_list", args)
    result = await _get_facade().snapshot_baseline_list(prefix)
    return _log_tool_result("unity_snapshot_baseline_list", _payload(result))


@mcp.tool(description="将一个 acceptedAsEvidence=true 的 Snapshot PNG 与受管基线比较，返回像素差比例、8x8 SSIM、diff 和 heatmap 产物。")
async def unity_snapshot_baseline_compare(
    baselineKey: str,
    snapshotId: str,
    targetId: str,
    role: str = "color",
    channelTolerance: int = 0,
    maxDifferentPixelRatio: float = 0.0,
    minSsim: float = 1.0,
    outputDirectory: str = "",
):
    args = {
        "baselineKey": baselineKey,
        "snapshotId": snapshotId,
        "targetId": targetId,
        "role": role,
        "channelTolerance": channelTolerance,
        "maxDifferentPixelRatio": maxDifferentPixelRatio,
        "minSsim": minSsim,
        "outputDirectory": outputDirectory,
    }
    _log_tool_call("unity_snapshot_baseline_compare", args)
    result = await _get_facade().snapshot_baseline_compare(
        baselineKey,
        snapshotId,
        targetId,
        role=role,
        channel_tolerance=channelTolerance,
        max_different_pixel_ratio=maxDifferentPixelRatio,
        min_ssim=minSsim,
        output_directory=outputDirectory,
    )
    return _log_tool_result("unity_snapshot_baseline_compare", _payload(result))


@mcp.tool(description="预览或确认更新 Snapshot 基线。默认 dryRun=true；实际写入必须提交同一当前状态返回的 confirmToken，绝不自动批准。")
async def unity_snapshot_baseline_update(
    baselineKey: str,
    snapshotId: str,
    targetId: str,
    role: str = "color",
    dryRun: bool = True,
    confirmToken: str = "",
):
    args = {
        "baselineKey": baselineKey,
        "snapshotId": snapshotId,
        "targetId": targetId,
        "role": role,
        "dryRun": dryRun,
        "confirmToken": confirmToken,
    }
    _log_tool_call("unity_snapshot_baseline_update", args)
    result = await _get_facade().snapshot_baseline_update(
        baselineKey,
        snapshotId,
        targetId,
        role=role,
        dry_run=dryRun,
        confirm_token=confirmToken,
    )
    return _log_tool_result("unity_snapshot_baseline_update", _payload(result))


for _name, _value in list(globals().items()):
    if callable(_value) and _name.startswith("unity_"):
        register_public_tool(
            _name,
            category="snapshot",
            destructive=_name == "unity_snapshot_baseline_update",
            idempotent=_name not in {
                "unity_snapshot_capture",
                "unity_snapshot_cancel",
                "unity_snapshot_baseline_update",
            },
            play_mode_policy="allowed",
            feature="core",
        )
