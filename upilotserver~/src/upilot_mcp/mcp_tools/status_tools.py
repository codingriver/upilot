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
    description="检查 Unity 连接并返回会话信息。command 留空或仅空白：不启动进程，仅等待已有 Unity 连接 Bridge（手动打开项目）。非空则 shell 启动后再等待连接。"
)
async def unity_open_editor(command: str = "", waitForConnectMs: int = 60000):
    _log_tool_call(
        "unity_open_editor", {"command": command, "waitForConnectMs": waitForConnectMs}
    )
    r = await _get_facade().open_editor(
        command=command, wait_for_connect_ms=waitForConnectMs
    )
    return _log_tool_result("unity_open_editor", _payload(r))

@mcp.tool(
    description=(
        "诊断 MCP 连接/会话/超时/编译状态。"
        "返回 paths.unityProjectAbsolute（当前 Unity 工程绝对路径）与 paths.mcpProcessWorkingDirectory（MCP Python 进程当前工作目录，多为 Cursor 工作区根目录）。"
    ),
)
async def unity_mcp_status(forceFresh: bool = False, includeCapabilities: bool = True):
    _log_tool_call("unity_mcp_status", {"forceFresh": forceFresh, "includeCapabilities": includeCapabilities})
    r = await _get_facade().mcp_status(force_fresh=forceFresh, include_capabilities=includeCapabilities)
    return _log_tool_result("unity_mcp_status", _payload(r))

@mcp.tool(description="返回 UPilot 核心能力、工具注册表版本、可选 Flow 模块状态和当前 Unity 能力摘要。")
async def unity_capabilities_get(forceFresh: bool = False):
    _log_tool_call("unity_capabilities_get", {"forceFresh": forceFresh})
    status = await _get_facade().mcp_status(force_fresh=forceFresh, include_capabilities=True)
    if not status.ok:
        return _log_tool_result("unity_capabilities_get", _payload(status))
    data = status.data or {}
    r = ok(status.request_id, {
        "registryVersion": REGISTRY_VERSION,
        "tools": [item.to_dict() for item in REGISTRY.list()],
        "capabilities": data.get("capabilities", {}),
        "session": data.get("session", {}),
        "paths": data.get("paths", {}),
    })
    return _log_tool_result("unity_capabilities_get", _payload(r))

@mcp.tool(description="按名称、类别和可用状态搜索 UPilot MCP 工具，避免读取完整 tools/list。")
async def unity_tools_find(
    query: str = "",
    category: str = "",
    availability: str = "all",
    limit: int = 20,
):
    _log_tool_call("unity_tools_find", {"query": query, "category": category, "availability": availability, "limit": limit})
    items = REGISTRY.find(
        query=query,
        category=category,
        availability=availability,
        limit=limit,
        flow_enabled=CONFIG.flow_enabled,
    )
    r = ok(new_id("req"), {"count": len(items), "tools": items})
    return _log_tool_result("unity_tools_find", _payload(r))

@mcp.tool(description="诊断 Codex/Cursor/通用 MCP 客户端配置中的重复端点、内部端口、HTTP 端口和超时问题。")
async def unity_client_config_diagnose():
    _log_tool_call("unity_client_config_diagnose", {})
    r = await _get_facade().client_config_diagnose()
    return _log_tool_result("unity_client_config_diagnose", _payload(r))

@mcp.tool(
    description="进入 PlayMode。会改变编辑器运行状态，可能触发脚本生命周期和场景运行逻辑；调用前确认用户需要运行态验证。"
)
async def unity_playmode_start():
    _log_tool_call("unity_playmode_start", {})
    r = await _get_facade().playmode_start()
    return _log_tool_result("unity_playmode_start", _payload(r))

@mcp.tool(
    description="退出 PlayMode。会停止运行态并回到编辑模式；PlayMode 中的非持久化运行时变更通常会丢失。"
)
async def unity_playmode_stop():
    _log_tool_call("unity_playmode_stop", {})
    r = await _get_facade().playmode_stop()
    return _log_tool_result("unity_playmode_stop", _payload(r))

@mcp.tool(
    description=(
        "列出 Unity 编辑器中打开的窗口（可按类型/标题过滤）。"
        "每项含 instanceId、标题、位置、docked、closable、closeDeniedReason（M27）等。"
    ),
)
async def unity_editor_windows_list(typeFilter: str = "", titleFilter: str = ""):
    _log_tool_call(
        "unity_editor_windows_list",
        {"typeFilter": typeFilter, "titleFilter": titleFilter},
    )
    r = await _get_facade().editor_windows_list(
        type_filter=typeFilter, title_filter=titleFilter
    )
    return _log_tool_result("unity_editor_windows_list", _payload(r))

@mcp.tool(
    description=(
        "M27：按标题关闭可关闭的编辑器窗口（非停靠、非黑名单）。"
        "matchMode：exact | contains。"
    ),
)
async def unity_editor_window_close(windowTitle: str, matchMode: str = "exact"):
    _log_tool_call(
        "unity_editor_window_close",
        {"windowTitle": windowTitle, "matchMode": matchMode},
    )
    r = await _get_facade().editor_window_close(
        window_title=windowTitle, match_mode=matchMode
    )
    return _log_tool_result("unity_editor_window_close", _payload(r))

@mcp.tool(
    description=(
        "M27：设置浮动编辑器窗口的位置与大小（像素）。"
        "已停靠或不可浮动的窗口可能返回 WINDOW_DOCKED 等错误。"
    ),
)
async def unity_editor_window_set_rect(
    windowTitle: str,
    x: float,
    y: float,
    width: float,
    height: float,
    matchMode: str = "exact",
):
    _log_tool_call(
        "unity_editor_window_set_rect",
        {
            "windowTitle": windowTitle,
            "x": x,
            "y": y,
            "width": width,
            "height": height,
            "matchMode": matchMode,
        },
    )
    r = await _get_facade().editor_window_set_rect(
        window_title=windowTitle,
        x=x,
        y=y,
        width=width,
        height=height,
        match_mode=matchMode,
    )
    return _log_tool_result("unity_editor_window_set_rect", _payload(r))

@mcp.tool(description="获取 Unity 编辑器状态快照。")
async def unity_editor_state():
    _log_tool_call("unity_editor_state", {})
    r = await _get_facade().editor_state()
    return _log_tool_result("unity_editor_state", _payload(r))

@mcp.tool(
    description=(
        "将 Unity Editor 窗口设置为前台焦点窗口（仅 Windows）。"
        "调用后会恢复窗口并执行 SetForegroundWindow，用于解决 Unity 在后台时编译延迟的问题。"
    ),
)
async def unity_editor_focus():
    _log_tool_call("unity_editor_focus", {})
    r = await _get_facade().editor_focus()
    return _log_tool_result("unity_editor_focus", _payload(r))

@mcp.tool(
    description=(
        "查询 Unity Editor 窗口的焦点状态（仅 Windows）。"
        "返回 unityFocused、unityTitle、foregroundTitle 等字段，用于判断 Unity 是否处于前台焦点。"
    ),
)
async def unity_editor_focus_state():
    _log_tool_call("unity_editor_focus_state", {})
    r = await _get_facade().editor_focus_state()
    return _log_tool_result("unity_editor_focus_state", _payload(r))

# Editor input, console, and command tools.
@mcp.tool(
    description="执行 Unity 编辑器鼠标动作。用于真实 UI 交互；优先用 elementName 自动定位元素中心，少用裸坐标。调用前确认目标窗口/控件，避免误点菜单、删除按钮或不稳定布局。"
)
async def unity_mouse_event(
    action: str,
    button: str,
    x: float = 0,
    y: float = 0,
    targetWindow: str = "",
    modifiers: list[str] | None = None,
    scrollDeltaX: float = 0.0,
    scrollDeltaY: float = 0.0,
    elementName: str = "",
    elementIndex: int = -1,
):
    _log_tool_call(
        "unity_mouse_event",
        {
            "action": action,
            "button": button,
            "x": x,
            "y": y,
            "targetWindow": targetWindow,
            "modifiers": modifiers,
            "scrollDeltaX": scrollDeltaX,
            "scrollDeltaY": scrollDeltaY,
            "elementName": elementName,
            "elementIndex": elementIndex,
        },
    )
    r = await _get_facade().mouse_event(
        action=action,
        button=button,
        x=x,
        y=y,
        target_window=targetWindow,
        modifiers=modifiers,
        scroll_delta_x=scrollDeltaX,
        scroll_delta_y=scrollDeltaY,
        element_name=elementName,
        element_index=elementIndex,
    )
    return _log_tool_result("unity_mouse_event", _payload(r))

@mcp.tool(
    description="执行 Unity 编辑器拖放操作。用于 Project/Hierarchy/Inspector 等真实 UI 拖拽；可能改变场景、Prefab 或资源引用。调用前确认源/目标窗口、坐标和 assetPaths/gameObjectIds。"
)
async def unity_drag_drop(
    sourceWindow: str,
    targetWindow: str,
    dragType: str,
    fromX: float,
    fromY: float,
    toX: float,
    toY: float,
    assetPaths: list[str] | None = None,
    gameObjectIds: list[int] | None = None,
    customData: str = "",
    modifiers: list[str] | None = None,
):
    _log_tool_call(
        "unity_drag_drop",
        {
            "sourceWindow": sourceWindow,
            "targetWindow": targetWindow,
            "dragType": dragType,
            "fromX": fromX,
            "fromY": fromY,
            "toX": toX,
            "toY": toY,
            "assetPaths": assetPaths,
            "gameObjectIds": gameObjectIds,
            "customData": customData,
            "modifiers": modifiers,
        },
    )
    r = await _get_facade().drag_drop(
        source_window=sourceWindow,
        target_window=targetWindow,
        drag_type=dragType,
        from_x=fromX,
        from_y=fromY,
        to_x=toX,
        to_y=toY,
        asset_paths=assetPaths,
        game_object_ids=gameObjectIds,
        custom_data=customData,
        modifiers=modifiers,
    )
    return _log_tool_result("unity_drag_drop", _payload(r))

@mcp.tool(
    description="执行 Unity 编辑器键盘动作。用于真实 UI 输入；targetWindow 必须明确，text 会输入到当前焦点控件。优先使用专用设置/脚本/组件工具，避免焦点不确定时盲打。"
)
async def unity_keyboard_event(
    action: str,
    targetWindow: str,
    keyCode: str = "",
    character: str = "",
    text: str = "",
    modifiers: list[str] | None = None,
):
    _log_tool_call(
        "unity_keyboard_event",
        {
            "action": action,
            "targetWindow": targetWindow,
            "keyCode": keyCode,
            "character": character,
            "text": text,
            "modifiers": modifiers,
        },
    )
    r = await _get_facade().keyboard_event(
        action=action,
        target_window=targetWindow,
        key_code=keyCode,
        character=character,
        text=text,
        modifiers=modifiers,
    )
    return _log_tool_result("unity_keyboard_event", _payload(r))

@mcp.tool(description="标记 Unity 控制台当前末尾游标，用于后续 tail 读取新增日志。")
async def unity_console_mark_logs():
    _log_tool_call("unity_console_mark_logs", {})
    r = await _get_facade().console_mark_logs()
    return _log_tool_result("unity_console_mark_logs", _payload(r))

@mcp.tool(
    description=(
        "从 Unity 控制台游标之后读取新增日志，支持服务端过滤。"
        "默认不返回堆栈并排除 upilot/MCP 自身日志。"
    )
)
async def unity_console_tail_logs(
    cursor: int = -1,
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUPilot: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = False,
    maxMessageLength: int = 0,
):
    _log_tool_call(
        "unity_console_tail_logs",
        {
            "cursor": cursor,
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "excludeUPilot": excludeUPilot,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "maxMessageLength": maxMessageLength,
        },
    )
    r = await _get_facade().console_tail_logs(
        cursor=cursor,
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_upilot=excludeUPilot,
        contains=contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
    )
    return _log_tool_result("unity_console_tail_logs", _payload(r))

@mcp.tool(
    description=(
        "搜索 Unity 控制台全量日志，支持关键词/正则和日志类型过滤。"
        "默认不返回堆栈并排除 upilot/MCP 自身日志。"
    )
)
async def unity_console_search_logs(
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUPilot: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = True,
    maxMessageLength: int = 0,
):
    _log_tool_call(
        "unity_console_search_logs",
        {
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "excludeUPilot": excludeUPilot,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "maxMessageLength": maxMessageLength,
        },
    )
    r = await _get_facade().console_search_logs(
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_upilot=excludeUPilot,
        contains=contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
    )
    return _log_tool_result("unity_console_search_logs", _payload(r))

@mcp.tool(
    description=(
        "开始将 Unity Console 新日志持续写入独立 JSONL 会话目录。"
        "默认目录为工程内 Log/UPilotConsole/<时间戳_标题>；同一时间只允许一个活跃会话。"
    )
)
async def unity_console_capture_start(
    title: str = "",
    path: str = "",
    includeStackTrace: bool = True,
    excludeUPilot: bool = True,
    clearUnityConsole: bool = False,
    flushIntervalMs: int = 1000,
    maxFileBytes: int = 50 * 1024 * 1024,
    allowOutsideProject: bool = False,
):
    _log_tool_call(
        "unity_console_capture_start",
        {
            "title": title,
            "path": path,
            "includeStackTrace": includeStackTrace,
            "excludeUPilot": excludeUPilot,
            "clearUnityConsole": clearUnityConsole,
            "flushIntervalMs": flushIntervalMs,
            "maxFileBytes": maxFileBytes,
            "allowOutsideProject": allowOutsideProject,
        },
    )
    r = await _get_facade().console_capture_start(
        title=title,
        path=path,
        include_stack_trace=includeStackTrace,
        exclude_upilot=excludeUPilot,
        clear_unity_console=clearUnityConsole,
        flush_interval_ms=flushIntervalMs,
        max_file_bytes=maxFileBytes,
        allow_outside_project=allowOutsideProject,
    )
    return _log_tool_result("unity_console_capture_start", _payload(r))

@mcp.tool(description="获取当前或指定 Unity Console 持久化采集会话的状态、计数、路径和写入错误。")
async def unity_console_capture_status(sessionId: str = ""):
    _log_tool_call("unity_console_capture_status", {"sessionId": sessionId})
    r = await _get_facade().console_capture_status(session_id=sessionId)
    return _log_tool_result("unity_console_capture_status", _payload(r))

@mcp.tool(
    description=(
        "按 sequence 增量读取持久化 Console JSONL 日志，支持类型和关键词过滤。"
        "后续读取应把上次返回的 nextSequence 作为 afterSequence。"
    )
)
async def unity_console_capture_read(
    sessionId: str = "",
    afterSequence: int = -1,
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    newestFirst: bool = False,
):
    _log_tool_call(
        "unity_console_capture_read",
        {
            "sessionId": sessionId,
            "afterSequence": afterSequence,
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "contains": contains,
            "containsAll": containsAll,
            "newestFirst": newestFirst,
        },
    )
    r = await _get_facade().console_capture_read(
        session_id=sessionId,
        after_sequence=afterSequence,
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        contains=contains,
        contains_all=containsAll,
        newest_first=newestFirst,
    )
    return _log_tool_result("unity_console_capture_read", _payload(r))

@mcp.tool(description="停止当前 Unity Console 持久化采集，刷新缓冲区并生成 summary.json 与 SHA256。")
async def unity_console_capture_stop(sessionId: str = ""):
    _log_tool_call("unity_console_capture_stop", {"sessionId": sessionId})
    r = await _get_facade().console_capture_stop(session_id=sessionId)
    return _log_tool_result("unity_console_capture_stop", _payload(r))

@mcp.tool(description="列出工程默认 Log/UPilotConsole 目录中的近期持久化采集会话。")
async def unity_console_capture_list(count: int = 20, includeActive: bool = True):
    _log_tool_call(
        "unity_console_capture_list", {"count": count, "includeActive": includeActive}
    )
    r = await _get_facade().console_capture_list(
        count=count, include_active=includeActive
    )
    return _log_tool_result("unity_console_capture_list", _payload(r))

@mcp.tool(
    description=(
        "清理过期 Unity Console 采集目录。危险操作：先 dryRun=true 获取目录清单和 confirmToken，"
        "确认后再以相同条件、dryRun=false 和 confirmToken 执行。"
    )
)
async def unity_console_capture_cleanup(
    olderThanDays: int = 14,
    keepLatest: int = 20,
    dryRun: bool = True,
    confirmToken: str = "",
):
    _log_tool_call(
        "unity_console_capture_cleanup",
        {
            "olderThanDays": olderThanDays,
            "keepLatest": keepLatest,
            "dryRun": dryRun,
            "confirmToken": confirmToken,
        },
    )
    r = await _get_facade().console_capture_cleanup(
        older_than_days=olderThanDays,
        keep_latest=keepLatest,
        dry_run=dryRun,
        confirm_token=confirmToken,
    )
    return _log_tool_result("unity_console_capture_cleanup", _payload(r))

@mcp.tool(
    description="清空 Unity 控制台日志。会移除当前 Console 历史；如果需要诊断先用 tail/search 读取或保存关键日志。"
)
async def unity_console_clear():
    _log_tool_call("unity_console_clear", {})
    r = await _get_facade().console_clear()
    return _log_tool_result("unity_console_clear", _payload(r))

@mcp.tool(
    description="执行 Unity 撤销操作（Undo）。会改变编辑器状态并回退最近操作；steps>1 前确认用户意图和当前 Undo 栈上下文。"
)
async def unity_editor_undo(steps: int = 1):
    _log_tool_call("unity_editor_undo", {"steps": steps})
    r = await _get_facade().editor_undo(steps=steps)
    return _log_tool_result("unity_editor_undo", _payload(r))

@mcp.tool(
    description="执行 Unity 重做操作（Redo）。会重新应用最近撤销的操作；steps>1 前确认用户意图和当前 Redo 栈上下文。"
)
async def unity_editor_redo(steps: int = 1):
    _log_tool_call("unity_editor_redo", {"steps": steps})
    r = await _get_facade().editor_redo(steps=steps)
    return _log_tool_result("unity_editor_redo", _payload(r))

@mcp.tool(description="执行 Unity 编辑器命令（通过菜单路径，如 'Edit/Play'）。")
async def unity_editor_execute_command(commandName: str):
    _log_tool_call("unity_editor_execute_command", {"commandName": commandName})
    r = await _get_facade().editor_execute_command(command_name=commandName)
    return _log_tool_result("unity_editor_execute_command", _payload(r))

@mcp.tool(
    description="导航 Unity SceneView 视图（聚焦对象、设置视角、正交/透视切换等）。"
)
async def unity_sceneview_navigate(
    lookAtInstanceId: int = 0,
    pivot: dict | None = None,
    size: float = -1,
    rotation: dict | None = None,
    orthographic: bool | None = None,
    in2DMode: bool | None = None,
):
    _log_tool_call(
        "unity_sceneview_navigate",
        {
            "lookAtInstanceId": lookAtInstanceId,
            "pivot": pivot,
            "size": size,
            "rotation": rotation,
            "orthographic": orthographic,
            "in2DMode": in2DMode,
        },
    )
    r = await _get_facade().sceneview_navigate(
        look_at_instance_id=lookAtInstanceId,
        pivot=pivot,
        size=size,
        rotation=rotation,
        orthographic=orthographic,
        in_2d_mode=in2DMode,
    )
    return _log_tool_result("unity_sceneview_navigate", _payload(r))


_DESTRUCTIVE_TOOLS = {
    "unity_asset_delete", "unity_asset_move", "unity_asset_modify_data",
    "unity_script_create", "unity_script_update", "unity_script_delete",
    "unity_package_add", "unity_package_remove", "unity_scene_save",
    "unity_scene_unload", "unity_gameobject_delete", "unity_component_remove",
    "unity_console_capture_cleanup",
}
_NON_IDEMPOTENT_TOOLS = {
    "unity_console_capture_start",
    "unity_console_capture_stop",
    "unity_console_capture_cleanup",
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
        idempotent=_name not in (_DESTRUCTIVE_TOOLS | _NON_IDEMPOTENT_TOOLS),
        play_mode_policy="blocked" if _name in _PLAYMODE_BLOCKED else "allowed",
        feature="flow" if _name.startswith("unity_upilot_flow_") else "core",
    )
