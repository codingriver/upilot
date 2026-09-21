from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import Field, StrictBool
from ..models import ToolResponse
from ..protocol import new_id
from ..responses import fail, ok
from ..tool_registry import REGISTRY, register_public_tool
from .. import mcp_stdio_server as runtime
from ..wire_ids import WireIdInput

mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result
_reject_compile_in_playmode = runtime._reject_compile_in_playmode
_reject_write_if_unapproved = runtime._reject_write_if_unapproved
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
        "runtimeIdentity 分开报告 Unity PID、MCP Server PID、Bridge sessionId 与 managed domainGeneration；"
        "同一 Unity PID 下 domainGeneration 变化表示 Domain Reload，不是 Unity 进程重启。"
    ),
)
async def unity_mcp_status(forceFresh: bool = False, includeCapabilities: bool = True):
    _log_tool_call("unity_mcp_status", {"forceFresh": forceFresh, "includeCapabilities": includeCapabilities})
    r = await _get_facade().mcp_status(force_fresh=forceFresh, include_capabilities=includeCapabilities)
    return _log_tool_result("unity_mcp_status", _payload(r))

@mcp.tool(description="返回 UPilot 核心能力、工具注册表版本、可选 Flow 模块状态和当前 Unity 能力摘要。")
async def unity_capabilities_get(forceFresh: bool = False):
    _log_tool_call("unity_capabilities_get", {"forceFresh": forceFresh})
    r = await _get_facade().capabilities_get(force_fresh=forceFresh)
    return _log_tool_result("unity_capabilities_get", _payload(r))

@mcp.tool(description="按名称、类别和可用状态搜索 UPilot MCP 工具，避免读取完整 tools/list。")
async def unity_tools_find(
    query: str = "",
    category: str = "",
    availability: str = "all",
    limit: int = 20,
):
    _log_tool_call("unity_tools_find", {"query": query, "category": category, "availability": availability, "limit": limit})
    r = await _get_facade().tools_find(
        query=query,
        category=category,
        availability=availability,
        limit=limit,
    )
    return _log_tool_result("unity_tools_find", _payload(r))

@mcp.tool(description="调用一个已注册但当前客户端未注入类型化包装的 UPilot 工具。调用仍遵守写权限、Flow 开关和各工具自身安全检查。")
async def unity_tool_call(toolName: str, args: dict | None = None):
    _log_tool_call("unity_tool_call", {"toolName": toolName, "args": args or {}})
    r = await _get_facade().tool_call(tool_name=toolName, args=args or {})
    return _log_tool_result("unity_tool_call", _payload(r))

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

@mcp.tool(description="暂停当前 PlayMode；EditMode 会明确拒绝，重复 pause 不应产生额外状态写入。")
async def unity_playmode_pause(
    wait: StrictBool = True,
    timeoutMs: Annotated[int, Field(strict=True, ge=1, le=30000)] = 5000,
):
    _log_tool_call("unity_playmode_pause", {"wait": wait, "timeoutMs": timeoutMs})
    rejected = _reject_write_if_unapproved("unity_playmode_pause")
    if rejected is not None:
        return rejected
    r = await _get_facade().playmode_pause(wait=wait, timeout_ms=timeoutMs)
    return _log_tool_result("unity_playmode_pause", _payload(r))

@mcp.tool(description="恢复当前已暂停的 PlayMode；不会退出 PlayMode。")
async def unity_playmode_resume(
    wait: StrictBool = True,
    timeoutMs: Annotated[int, Field(strict=True, ge=1, le=30000)] = 5000,
):
    _log_tool_call("unity_playmode_resume", {"wait": wait, "timeoutMs": timeoutMs})
    rejected = _reject_write_if_unapproved("unity_playmode_resume")
    if rejected is not None:
        return rejected
    r = await _get_facade().playmode_resume(wait=wait, timeout_ms=timeoutMs)
    return _log_tool_result("unity_playmode_resume", _payload(r))

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
async def unity_editor_window_close(
    windowTitle: str = "", matchMode: str = "exact", instanceId: str = "", domainGeneration: str = "",
    closeMode: str = "requestUserClose",
):
    _log_tool_call(
        "unity_editor_window_close",
        {"windowTitle": windowTitle, "matchMode": matchMode, "instanceId": instanceId, "domainGeneration": domainGeneration, "closeMode": closeMode},
    )
    r = await _get_facade().editor_window_close(
        window_title=windowTitle, match_mode=matchMode, instance_id=instanceId,
        domain_generation=domainGeneration, close_mode=closeMode,
    )
    return _log_tool_result("unity_editor_window_close", _payload(r))

@mcp.tool(
    description=(
        "M27：设置浮动编辑器窗口的位置与大小（像素）。"
        "已停靠或不可浮动的窗口可能返回 WINDOW_DOCKED 等错误。"
    ),
)
async def unity_editor_window_set_rect(
    windowTitle: str = "",
    x: float | None = None,
    y: float | None = None,
    width: float | None = None,
    height: float | None = None,
    matchMode: str = "exact",
    instanceId: str = "",
    domainGeneration: str = "",
    fullTypeName: str = "",
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
            "instanceId": instanceId,
            "domainGeneration": domainGeneration,
            "fullTypeName": fullTypeName,
        },
    )
    r = await _get_facade().editor_window_set_rect(
        window_title=windowTitle,
        x=x,
        y=y,
        width=width,
        height=height,
        match_mode=matchMode,
        instance_id=instanceId,
        domain_generation=domainGeneration,
        full_type_name=fullTypeName,
    )
    return _log_tool_result("unity_editor_window_set_rect", _payload(r))

@mcp.tool(description="查询已观测的 EditorWindow 生命周期事件；仅返回有界元数据，不打开、聚焦或关闭窗口。")
async def unity_editor_window_history(
    instanceId: str = "",
    afterSequence: Annotated[int, Field(strict=True, ge=0)] = 0,
    count: Annotated[int, Field(strict=True, ge=1, le=512)] = 100,
):
    _log_tool_call("unity_editor_window_history", {"instanceId": instanceId, "afterSequence": afterSequence, "count": count})
    r = await _get_facade().editor_window_history(
        instance_id=instanceId, after_sequence=afterSequence, count=count,
    )
    return _log_tool_result("unity_editor_window_history", _payload(r))

@mcp.tool(description="仅打开已审计的 UPilot Safe Mode 探针窗口；未知或第三方窗口类型会在创建前拒绝。")
async def unity_editor_window_open(typeName: str):
    _log_tool_call("unity_editor_window_open", {"typeName": typeName})
    r = await _get_facade().editor_window_open(type_name=typeName)
    return _log_tool_result("unity_editor_window_open", _payload(r))

@mcp.tool(description="仅聚焦已审计的 UPilot Safe Mode 探针窗口；要求精确 instanceId，可选 domainGeneration 用于跨 Reload 防护。")
async def unity_editor_window_focus(instanceId: str, domainGeneration: str = ""):
    _log_tool_call("unity_editor_window_focus", {"instanceId": instanceId, "domainGeneration": domainGeneration})
    r = await _get_facade().editor_window_focus(instance_id=instanceId, domain_generation=domainGeneration)
    return _log_tool_result("unity_editor_window_focus", _payload(r))

@mcp.tool(description="按精确非负 instanceId 设置现有 SceneView 的最大化状态；不会创建窗口或保存布局。wait=true 仅在 Unity 返回 fresh authoritative、non-stale 状态时确认；restoreToken 仅在同一 Server 生命周期内可幂等重放。")
async def unity_sceneview_set_maximized(
    instanceId: Annotated[int, Field(strict=True, ge=0)], maximized: StrictBool,
    expectedCurrentMaximized: StrictBool | None = None, domainGeneration: str = "",
    wait: StrictBool = True, timeoutMs: Annotated[int, Field(strict=True, ge=1, le=30000)] = 5000,
    restoreToken: str = "",
):
    _log_tool_call("unity_sceneview_set_maximized", {"instanceId": instanceId, "maximized": maximized, "expectedCurrentMaximized": expectedCurrentMaximized, "domainGeneration": domainGeneration, "wait": wait, "timeoutMs": timeoutMs, "restoreToken": restoreToken})
    rejected = _reject_write_if_unapproved("unity_sceneview_set_maximized")
    if rejected is not None:
        return rejected
    r = await _get_facade().sceneview_set_maximized(
        instance_id=instanceId, maximized=maximized, expected_current_maximized=expectedCurrentMaximized,
        domain_generation=domainGeneration, wait=wait, timeout_ms=timeoutMs, restore_token=restoreToken,
    )
    return _log_tool_result("unity_sceneview_set_maximized", _payload(r))

@mcp.tool(description="按原始 commandId 只读查询 SceneView maximize/restore 的已记录观察状态；不会再次设置窗口或创建窗口。未知身份、Domain Reload 或 Server 重启明确返回 RecoveryRequired/unknown。")
async def unity_sceneview_command_status(commandId: str):
    _log_tool_call("unity_sceneview_command_status", {"commandId": commandId})
    r = await _get_facade().sceneview_command_status(command_id=commandId)
    return _log_tool_result("unity_sceneview_command_status", _payload(r))

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
    windowInstanceId: str = "",
    escapeGenericMenu: bool = False,
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
            "escapeGenericMenu": escapeGenericMenu,
        },
    )
    rejected = _reject_write_if_unapproved("unity_mouse_event")
    if rejected is not None:
        return rejected
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
        window_instance_id=windowInstanceId,
        escape_generic_menu=escapeGenericMenu,
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
    gameObjectIds: list[WireIdInput] | None = None,
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
    rejected = _reject_write_if_unapproved("unity_drag_drop")
    if rejected is not None:
        return rejected
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
    action: Annotated[
        Literal["keydown", "keyup", "keypress", "type"],
        Field(description="键盘动作：keydown、keyup、keypress 或 type；严格使用小写。"),
    ],
    targetWindow: Annotated[str, Field(description="目标 EditorWindow 的精确标题或类型名。")],
    keyCode: Annotated[str, Field(description="keydown、keyup、keypress 使用的 Unity KeyCode；type 动作忽略。")]= "",
    character: Annotated[str, Field(description="可选单字符，随 keydown、keyup、keypress 事件发送。")]= "",
    text: Annotated[str, Field(description="type 动作逐字符输入的文本；其他动作忽略。")]= "",
    modifiers: Annotated[list[str] | None, Field(description="可选修饰键列表，例如 Control、Shift、Alt、Command。")]= None,
    windowInstanceId: str = "",
    escapeGenericMenu: bool = False,
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
            "escapeGenericMenu": escapeGenericMenu,
        },
    )
    rejected = _reject_write_if_unapproved("unity_keyboard_event")
    if rejected is not None:
        return rejected
    r = await _get_facade().keyboard_event(
        action=action,
        target_window=targetWindow,
        key_code=keyCode,
        window_instance_id=windowInstanceId,
        character=character,
        text=text,
        modifiers=modifiers,
        escape_generic_menu=escapeGenericMenu,
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
        "默认不返回堆栈并排除 upilot/MCP 自身日志；返回 excludedUPilotCount 说明排除数量。"
    )
)
async def unity_console_tail_logs(
    cursor: int = -1,
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUPilot: bool = True,
    contains: str | list[str] | None = None,
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
    normalized_contains = [contains] if isinstance(contains, str) else contains
    r = await _get_facade().console_tail_logs(
        cursor=cursor,
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_upilot=excludeUPilot,
        contains=normalized_contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
    )
    return _log_tool_result("unity_console_tail_logs", _payload(r))

@mcp.tool(
    description=(
        "搜索 Unity 控制台全量日志，支持关键词/正则和日志类型过滤。"
        "默认不返回堆栈并排除 upilot/MCP 自身日志；返回 excludedUPilotCount 说明排除数量。"
        "可用互斥的 runGuid 或 compileOperationId 查询持久化运行边界；关联仅表示在该运行期间观察到，不表示因果归属。"
    )
)
async def unity_console_search_logs(
    count: int = 200,
    query: str = "",
    maxCount: int = 0,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUPilot: bool = True,
    contains: str | list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = True,
    maxMessageLength: int = 0,
    runGuid: str = "",
    compileOperationId: str = "",
):
    _log_tool_call(
        "unity_console_search_logs",
        {
            "count": maxCount if maxCount > 0 else count,
            "query": query,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "excludeUPilot": excludeUPilot,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "maxMessageLength": maxMessageLength,
            "runGuid": runGuid,
            "compileOperationId": compileOperationId,
        },
    )
    normalized_contains = [contains] if isinstance(contains, str) else contains
    r = await _get_facade().console_search_logs(
        count=maxCount if maxCount > 0 else count,
        query=query,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_upilot=excludeUPilot,
        contains=normalized_contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
        run_guid=runGuid,
        compile_operation_id=compileOperationId,
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
    ownerId: str = "",
    requestKey: str = "",
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
            "ownerId": ownerId,
            "requestKey": requestKey,
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
        owner_id=ownerId,
        request_key=requestKey,
    )
    return _log_tool_result("unity_console_capture_start", _payload(r))

@mcp.tool(description="获取当前或指定 Unity Console 持久化采集会话的状态、计数、路径和写入错误。")
async def unity_console_capture_status(sessionId: str = ""):
    _log_tool_call("unity_console_capture_status", {"sessionId": sessionId})
    r = await _get_facade().console_capture_status(session_id=sessionId)
    return _log_tool_result("unity_console_capture_status", _payload(r))

@mcp.tool(
    description=(
        "分页读取持久化 Console JSONL 日志，支持 sequence 范围、关键词 OR/AND、正则和日志类型过滤。"
        "首批返回稳定快照范围、总匹配数、扫描范围和 continuationToken；后续优先原样传回 token。"
        "旧调用仍可把上次 nextSequence 作为 afterSequence。"
    )
)
async def unity_console_capture_read(
    sessionId: str = "",
    afterSequence: int = -1,
    fromSequence: int = -1,
    toSequence: int = -1,
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = False,
    continuationToken: str = "",
):
    _log_tool_call(
        "unity_console_capture_read",
        {
            "sessionId": sessionId,
            "afterSequence": afterSequence,
            "fromSequence": fromSequence,
            "toSequence": toSequence,
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "continuationToken": continuationToken,
        },
    )
    r = await _get_facade().console_capture_read(
        session_id=sessionId,
        after_sequence=afterSequence,
        from_sequence=fromSequence,
        to_sequence=toSequence,
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        contains=contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        continuation_token=continuationToken,
    )
    return _log_tool_result("unity_console_capture_read", _payload(r))

@mcp.tool(description="停止精确且已归属的 Unity Console 持久化采集。ownerToken 必须匹配；forceStop 仅限已授权的明确人工处置，不能用于自动清理。")
async def unity_console_capture_stop(sessionId: str = "", ownerToken: str = "", forceStop: bool = False):
    _log_tool_call("unity_console_capture_stop", {"sessionId": sessionId, "ownerToken": ownerToken, "forceStop": forceStop})
    r = await _get_facade().console_capture_stop(
        session_id=sessionId, owner_token=ownerToken, force_stop=forceStop,
    )
    return _log_tool_result("unity_console_capture_stop", _payload(r))

@mcp.tool(description="只读附着到精确 Console Capture 会话；不会启动、停止或接管采集。")
async def unity_console_capture_attach(sessionId: str, requestKey: str):
    _log_tool_call("unity_console_capture_attach", {"sessionId": sessionId, "requestKey": requestKey})
    r = await _get_facade().console_capture_attach(session_id=sessionId, request_key=requestKey)
    return _log_tool_result("unity_console_capture_attach", _payload(r))

@mcp.tool(description="关闭只读 Console Capture 附着的固定范围；可分页导出该范围，但绝不停止或接管源采集。")
async def unity_console_capture_detach(
    attachmentId: str,
    export: bool = False,
    requestKey: str = "",
    continuationToken: str = "",
):
    _log_tool_call(
        "unity_console_capture_detach",
        {
            "attachmentId": attachmentId,
            "export": export,
            "requestKey": requestKey,
            "continuationToken": continuationToken,
        },
    )
    r = await _get_facade().console_capture_detach(
        attachment_id=attachmentId,
        export=export,
        request_key=requestKey,
        continuation_token=continuationToken,
    )
    return _log_tool_result("unity_console_capture_detach", _payload(r))

@mcp.tool(description="列出工程默认 Log/UPilotConsole 目录中的近期持久化采集会话；activeOnly=true 仅返回活动会话，响应包含 activeCount/returnedCount。activeOnly=true 与 includeActive=false 互斥。")
async def unity_console_capture_list(
    count: int = 20, includeActive: bool = True, activeOnly: bool = False
):
    _log_tool_call(
        "unity_console_capture_list",
        {"count": count, "includeActive": includeActive, "activeOnly": activeOnly},
    )
    r = await _get_facade().console_capture_list(
        count=count, include_active=includeActive, active_only=activeOnly
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
async def unity_editor_execute_command(commandName: str, expectedModal: dict | None = None):
    _log_tool_call("unity_editor_execute_command", {"commandName": commandName, "expectedModal": expectedModal})
    r = await _get_facade().editor_execute_command(command_name=commandName, expected_modal=expectedModal)
    return _log_tool_result("unity_editor_execute_command", _payload(r))

@mcp.tool(
    description="导航 Unity SceneView 视图（聚焦对象、设置视角、正交/透视切换等）。"
)
async def unity_sceneview_navigate(
    lookAtInstanceId: WireIdInput = 0,
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
    "unity_asset_create_folder", "unity_asset_copy",
    "unity_prefab_create", "unity_prefab_instantiate", "unity_prefab_save",
    "unity_material_create", "unity_material_modify", "unity_material_assign",
    "unity_menu_execute",
    "unity_script_create", "unity_script_update", "unity_script_delete",
    "unity_package_add", "unity_package_remove", "unity_scene_create",
    "unity_scene_save", "unity_scene_unload", "unity_scene_ensure_test",
    "unity_gameobject_create", "unity_gameobject_modify",
    "unity_gameobject_delete", "unity_gameobject_move",
    "unity_gameobject_duplicate", "unity_component_add",
    "unity_component_remove", "unity_component_modify",
    "unity_batch_execute", "unity_mouse_event", "unity_drag_drop",
    "unity_keyboard_event",
    "unity_console_capture_stop",
    "unity_console_capture_cleanup",
    "unity_playmode_pause", "unity_playmode_resume",
    "unity_sceneview_set_maximized",
}
_NON_IDEMPOTENT_TOOLS = {
    "unity_console_capture_start",
    "unity_console_capture_stop",
    "unity_console_capture_cleanup",
}
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
        idempotent=_name not in (_DESTRUCTIVE_TOOLS | _NON_IDEMPOTENT_TOOLS),
        play_mode_policy="blocked" if _name in _PLAYMODE_BLOCKED else "allowed",
        feature="flow" if _name.startswith("unity_upilot_flow_") else "core",
    )
