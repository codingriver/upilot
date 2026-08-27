from __future__ import annotations

from ..tool_registry import register_public_tool
from .. import mcp_stdio_server as runtime

mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result


@mcp.tool(description="读取 UPilot 追踪器手动配置、点位安装状态、堆栈开关和事件计数。")
async def unity_monohook_tracing_status():
    _log_tool_call("unity_monohook_tracing_status", {})
    result = await _get_facade().monohook_tracing_status()
    return _log_tool_result("unity_monohook_tracing_status", _payload(result))


@mcp.tool(description="修改 UPilot 追踪器点位配置；apply 默认 false，仅保存配置，不安装或卸载 Hook。")
async def unity_monohook_tracing_configure(
    pointIds: list[str] | None = None,
    enabled: bool = False,
    updateCaptureStackTrace: bool = False,
    captureStackTrace: bool = False,
    setMasterEnabled: bool = False,
    masterEnabled: bool = True,
    apply: bool = False,
):
    args = {
        "pointIds": pointIds or [],
        "enabled": enabled,
        "updateCaptureStackTrace": updateCaptureStackTrace,
        "captureStackTrace": captureStackTrace,
        "setMasterEnabled": setMasterEnabled,
        "masterEnabled": masterEnabled,
        "apply": apply,
    }
    _log_tool_call("unity_monohook_tracing_configure", args)
    result = await _get_facade().monohook_tracing_configure(
        point_ids=pointIds,
        enabled=enabled,
        update_capture_stack_trace=updateCaptureStackTrace,
        capture_stack_trace=captureStackTrace,
        set_master_enabled=setMasterEnabled,
        master_enabled=masterEnabled,
        apply=apply,
    )
    return _log_tool_result("unity_monohook_tracing_configure", _payload(result))


@mcp.tool(description="读取 UPilot 追踪器事件；consume 默认 false，不消费窗口中的事件缓存。")
async def unity_monohook_tracing_events(maxCount: int = 100, consume: bool = False):
    args = {"maxCount": maxCount, "consume": consume}
    _log_tool_call("unity_monohook_tracing_events", args)
    result = await _get_facade().monohook_tracing_events(max_count=maxCount, consume=consume)
    return _log_tool_result("unity_monohook_tracing_events", _payload(result))


register_public_tool("unity_monohook_tracing_status", category="monohook", idempotent=True)
register_public_tool(
    "unity_monohook_tracing_configure",
    category="monohook",
    idempotent=True,
    destructive=True,
    requires_write_access=True,
)
register_public_tool("unity_monohook_tracing_events", category="monohook", idempotent=True)
