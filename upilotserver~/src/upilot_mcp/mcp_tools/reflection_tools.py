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
    description="通过反射调用已编译并加载的 Unity/C# 方法。不是脚本执行器；适合稳定业务入口和静态/实例方法调用。调用前可用 unity_reflection_find 确认类型和方法；复杂多步逻辑应放进项目 helper 方法。"
)
async def unity_reflection_call(
    typeName: str,
    methodName: str,
    parameters: list | None = None,
    isStatic: bool = True,
    targetInstancePath: str = "",
    targetStaticTypeName: str = "",
    targetStaticMemberPath: str = "",
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
        },
    )
    r = await _get_facade().reflection_call(
        type_name=typeName,
        method_name=methodName,
        parameters=parameters,
        is_static=isStatic,
        target_instance_path=targetInstancePath,
        target_static_type_name=targetStaticTypeName,
        target_static_member_path=targetStaticMemberPath,
    )
    return _log_tool_result("unity_reflection_call", _payload(r))

@mcp.tool(
    description=(
        "执行一条受限 C#-like 反射表达式，不是 C# 脚本/编译器。适合读属性、调用已有方法、"
        "简单赋值和单表达式诊断，例如 `UnityEngine.Application.unityVersion`、"
        "`Some.Type.Inst.Method(1, \"x\")`、`UnityEditor.EditorPrefs.SetInt(\"k\", 1)`。"
        "只接受一条表达式语句，可带分号；支持成员/索引/链式调用、常见运算符、三元、cast/as/is、"
        "null 条件访问、typed array、Vector2/3/4/Quaternion 构造。"
        "不支持 var/局部变量、if/for/foreach/while/switch、lambda/LINQ、async/await、ref/out/in、"
        "using/namespace/方法或类型定义、任意 new 对象、动态编译。遇到这些失败不要反复尝试，"
        "改用已有专用工具、unity_reflection_call，或请用户添加稳定 helper 方法。"
    ),
)
async def reflection_eval(
    code: str,
    variables: dict | None = None,
    options: dict | None = None,
):
    _log_tool_call(
        "reflection_eval",
        {"code": code, "variables": variables, "options": options},
    )
    rejected = _reject_write_if_unapproved("reflection_eval")
    if rejected is not None:
        return rejected
    r = await _get_facade().reflection_eval(
        code=code,
        variables=variables,
        options=options,
    )
    return _log_tool_result("reflection_eval", _payload(r))

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
    "unity_batch_execute", "reflection_eval",
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
