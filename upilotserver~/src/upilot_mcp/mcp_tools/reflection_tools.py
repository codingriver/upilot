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

@mcp.tool(description="通过反射搜索 Unity 程序集中的指定类型，可选按方法名过滤。")
async def unity_reflection_find(
    typeName: Annotated[
        str,
        Field(description="要查找的 C# 类型名，可使用完整命名空间或不带命名空间的类名。"),
    ],
    methodName: Annotated[
        str,
        Field(description="可选的方法名过滤条件；留空时返回该类型的所有方法。"),
    ] = "",
):
    _log_tool_call(
        "unity_reflection_find", {"typeName": typeName, "methodName": methodName}
    )
    r = await _get_facade().reflection_find(type_name=typeName, method_name=methodName)
    return _log_tool_result("unity_reflection_find", _payload(r))


@mcp.tool(
    description="只读检查已加载 C#/Unity 类型是否存在；返回完整类型名、程序集、可见性和短名冲突候选，不枚举方法成员。"
)
async def unity_type_exists(typeName: str):
    _log_tool_call("unity_type_exists", {"typeName": typeName})
    r = await _get_facade().type_exists(type_name=typeName)
    return _log_tool_result("unity_type_exists", _payload(r))

@mcp.tool(
    description=(
        "统一反射执行入口：传 typeName + methodName 时精确调用一个已编译方法；只传 expression 时执行"
        "一条受限 C#-like 反射表达式。kind=auto 会在执行前按参数形态选择唯一引擎，不会失败后重试另一"
        "引擎。表达式模式支持成员/索引/链式调用、常见运算符、三元、cast/as/is、赋值、隐式/交错及 rank 1–4 数组、"
        "Vector2/3/4/Quaternion 构造和 JSON variables；仍不支持局部变量、控制流、lambda/LINQ、"
        "async/await、类型定义或动态编译。目标可能修改场景、资源或运行时状态，因此需要项目写入授权且"
        "不得自动重试；复杂多步逻辑使用 csharp_eval，需要稳定复用时使用已编译项目 helper。"
    )
)
async def unity_reflection_call(
    typeName: Annotated[str, Field(description="method 模式的已加载类型名；expression 模式必须留空。")]= "",
    methodName: Annotated[str, Field(description="method 模式的方法名；expression 模式必须留空。")]= "",
    parameters: Annotated[list | None, Field(description="旧版字符串/JSON 参数；与 typed arguments 互斥。")]= None,
    isStatic: Annotated[bool, Field(description="旧版目标选择：true 调用静态方法，false 调用 targetInstancePath 解析的实例。")]= True,
    targetInstancePath: Annotated[str, Field(description="旧版实例目标路径；不能与 targetHandle 混用。")]= "",
    targetStaticTypeName: Annotated[str, Field(description="旧版静态成员路径起始类型；不能与 targetHandle 混用。")]= "",
    targetStaticMemberPath: Annotated[str, Field(description="旧版静态成员路径；不能与 targetHandle 混用。")]= "",
    asyncAfterSec: Annotated[float, Field(description="旧版长调用超过该同步窗口后转为 operation handle 的秒数。")]= 25.0,
    operationTimeoutSec: Annotated[float, Field(description="长反射 operation 的总超时秒数。")]= 600.0,
    forceAsync: Annotated[bool, Field(description="是否立即以长反射 operation 方式返回；不会重复执行目标方法。")]= False,
    expression: Annotated[str, Field(description="expression 模式的一条受限表达式；不能同时传 typeName/methodName。")]= "",
    variables: Annotated[dict | None, Field(description="expression 模式的 JSON 或 TypedValue 变量映射。")]= None,
    options: Annotated[dict | None, Field(description="旧版 expression 选项；新调用通常留空。")]= None,
    kind: Annotated[str, Field(description="auto、method 或 expression；auto 在执行前按互斥请求形态选择唯一引擎。")]= "auto",
    arguments: Annotated[list | None, Field(description="typed/named 参数列表；每项可含 name、direction=in|ref|out 和 value；与 parameters 互斥。")]= None,
    parameterTypeNames: Annotated[list[str] | None, Field(description="用于精确选择重载的参数类型名列表。")]= None,
    genericTypeArguments: Annotated[list[str] | None, Field(description="显式闭合泛型方法的类型参数名列表。")]= None,
    targetHandle: Annotated[str, Field(description="persistent session 中的实例 handle；强制 instance 调用且不能与旧路径目标混用。")]= "",
    sessionId: Annotated[str, Field(description="targetHandle 或 handle 结果使用的 persistent session ID。")]= "",
    awaitMode: Annotated[str, Field(description="awaitable 处理：auto、always 或 never。")]= "auto",
    awaitTimeoutMs: Annotated[int, Field(description="等待 Task/ValueTask 的有界超时毫秒数。")]= 3000,
    resultMode: Annotated[str, Field(description="结果编码：auto、inline、handle 或 legacyString；handle 结果需要 session。")]= "auto",
):
    _log_tool_call(
        "unity_reflection_call",
        {
            "typeName": typeName,
            "methodName": methodName,
            "parameters": parameters,
            "isStatic": isStatic,
            "targetInstancePath": targetInstancePath,
            "targetStaticTypeName": targetStaticTypeName,
            "targetStaticMemberPath": targetStaticMemberPath,
            "expression": expression,
            "variables": variables,
            "options": options,
            "kind": kind,
            "arguments": arguments,
            "parameterTypeNames": parameterTypeNames,
            "genericTypeArguments": genericTypeArguments,
            "targetHandle": targetHandle,
            "sessionId": sessionId,
            "awaitMode": awaitMode,
            "awaitTimeoutMs": awaitTimeoutMs,
            "resultMode": resultMode,
        },
    )
    rejected = _reject_write_if_unapproved("unity_reflection_call")
    if rejected is not None:
        return rejected
    call_kwargs = dict(
        type_name=typeName,
        method_name=methodName,
        parameters=parameters,
        is_static=isStatic,
        target_instance_path=targetInstancePath,
        target_static_type_name=targetStaticTypeName,
        target_static_member_path=targetStaticMemberPath,
        async_after_sec=asyncAfterSec,
        operation_timeout_sec=operationTimeoutSec,
        force_async=forceAsync,
        expression=expression,
        variables=variables,
        options=options,
        kind=kind,
    )
    if arguments is not None:
        call_kwargs["arguments"] = arguments
    if parameterTypeNames:
        call_kwargs["parameter_type_names"] = parameterTypeNames
    if genericTypeArguments:
        call_kwargs["generic_type_arguments"] = genericTypeArguments
    if targetHandle:
        call_kwargs["target_handle"] = targetHandle
    if sessionId:
        call_kwargs["session_id"] = sessionId
    if awaitMode != "auto":
        call_kwargs["await_mode"] = awaitMode
    if awaitTimeoutMs != 3000:
        call_kwargs["await_timeout_ms"] = awaitTimeoutMs
    if resultMode != "auto":
        call_kwargs["result_mode"] = resultMode
    r = await _get_facade().reflection_call(**call_kwargs)
    return _log_tool_result("unity_reflection_call", _payload(r))

@mcp.tool(description="查询自动脱离同步窗口的长反射调用；只轮询 Server 本地任务，不会重复执行 Unity 方法。")
async def unity_reflection_operation_status(operationId: str):
    r = await _get_facade().reflection_operation_status(operation_id=operationId)
    return _log_tool_result("unity_reflection_operation_status", _payload(r))

@mcp.tool(description="等待长反射调用的终态；等待窗口结束不等于原调用超时。")
async def unity_reflection_operation_wait(operationId: str, timeoutSec: float = 30.0, pollIntervalSec: float = 0.5):
    r = await _get_facade().reflection_operation_wait(
        operation_id=operationId,
        timeout_sec=timeoutSec,
        poll_interval_sec=pollIntervalSec,
    )
    return _log_tool_result("unity_reflection_operation_wait", _payload(r))

@mcp.tool(description="长反射调用取消能力查询。任意 Unity 主线程方法开始后不可安全中断，本工具不会伪造取消成功。")
async def unity_reflection_operation_cancel(operationId: str):
    r = await _get_facade().reflection_operation_cancel(operation_id=operationId)
    return _log_tool_result("unity_reflection_operation_cancel", _payload(r))

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
    "unity_batch_execute", "unity_reflection_call",
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
        destructive=_name in _DESTRUCTIVE_TOOLS,
        idempotent=_name not in _DESTRUCTIVE_TOOLS,
        play_mode_policy="blocked" if _name in _PLAYMODE_BLOCKED else "allowed",
        feature="flow" if _name.startswith("unity_upilot_flow_") else "core",
    )
