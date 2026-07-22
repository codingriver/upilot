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

@mcp.tool(description="在 Unity 资源数据库中搜索资源，支持按名称和类型过滤。")
async def unity_asset_find(query: str, assetType: str = ""):
    _log_tool_call("unity_asset_find", {"query": query, "assetType": assetType})
    r = await _get_facade().asset_find(query=query, asset_type=assetType)
    return _log_tool_result("unity_asset_find", _payload(r))

@mcp.tool(description="在 Unity Assets 目录下创建新文件夹。")
async def unity_asset_create_folder(parentFolder: str, newFolderName: str):
    _log_tool_call(
        "unity_asset_create_folder",
        {"parentFolder": parentFolder, "newFolderName": newFolderName},
    )
    r = await _get_facade().asset_create_folder(
        parent_folder=parentFolder, new_folder_name=newFolderName
    )
    return _log_tool_result("unity_asset_create_folder", _payload(r))

@mcp.tool(
    description="复制 Unity 资源到指定路径。会在项目磁盘创建新资源和 .meta；确认 destinationPath 不覆盖重要资源。"
)
async def unity_asset_copy(sourcePath: str, destinationPath: str):
    _log_tool_call(
        "unity_asset_copy",
        {"sourcePath": sourcePath, "destinationPath": destinationPath},
    )
    r = await _get_facade().asset_copy(
        source_path=sourcePath, destination_path=destinationPath
    )
    return _log_tool_result("unity_asset_copy", _payload(r))

@mcp.tool(
    description="移动 Unity 资源到指定路径。会改动项目磁盘资源和 .meta/GUID 引用关系；sourcePath/destinationPath 应在 Assets 或 Packages 可写范围内，调用前确认路径。"
)
async def unity_asset_move(sourcePath: str, destinationPath: str):
    _log_tool_call(
        "unity_asset_move",
        {"sourcePath": sourcePath, "destinationPath": destinationPath},
    )
    r = await _get_facade().asset_move(
        source_path=sourcePath, destination_path=destinationPath
    )
    return _log_tool_result("unity_asset_move", _payload(r))

@mcp.tool(
    description="删除指定路径的 Unity 资源。破坏性磁盘操作：调用前先用 unity_asset_get_info/find 确认路径，不要删除未确认的目录或共享资源。"
)
async def unity_asset_delete(assetPath: str):
    _log_tool_call("unity_asset_delete", {"assetPath": assetPath})
    r = await _get_facade().asset_delete(asset_path=assetPath)
    return _log_tool_result("unity_asset_delete", _payload(r))

@mcp.tool(description="触发 Unity 资源数据库刷新（AssetDatabase.Refresh）。")
async def unity_asset_refresh():
    _log_tool_call("unity_asset_refresh", {})
    r = await _get_facade().asset_refresh()
    return _log_tool_result("unity_asset_refresh", _payload(r))

@mcp.tool(
    description=(
        "在 Cursor/IDE 中改完或新建完本轮所有脚本并全部保存后，再调用一次（不要每文件一调）："
        "先等待 delayS 秒（默认 2，缓解落盘延迟），再 AssetDatabase.Refresh；"
        "triggerCompile=true 时再触发 unity_compile（含 Refresh+脚本编译）。"
        "随后可 unity_compile_wait 确认编译结束。避免 Unity 无焦点时迟迟不导入。"
    ),
)
async def unity_sync_after_disk_write(
    delayS: float = 2.0, triggerCompile: bool = False
):
    _log_tool_call(
        "unity_sync_after_disk_write",
        {"delayS": delayS, "triggerCompile": triggerCompile},
    )
    if triggerCompile:
        rejected = await _reject_compile_in_playmode("unity_sync_after_disk_write")
        if rejected is not None:
            return rejected
    r = await _get_facade().sync_after_disk_write(
        delay_s=delayS, trigger_compile=triggerCompile
    )
    return _log_tool_result("unity_sync_after_disk_write", _payload(r))

@mcp.tool(description="获取 Unity 资源的元数据信息（GUID、类型、大小等）。")
async def unity_asset_get_info(assetPath: str):
    _log_tool_call("unity_asset_get_info", {"assetPath": assetPath})
    r = await _get_facade().asset_get_info(asset_path=assetPath)
    return _log_tool_result("unity_asset_get_info", _payload(r))

@mcp.tool(description="搜索 Unity 内置资源（如默认材质、Shader、字体等）。")
async def unity_asset_find_built_in(query: str = "", assetType: str = ""):
    _log_tool_call(
        "unity_asset_find_built_in", {"query": query, "assetType": assetType}
    )
    r = await _get_facade().asset_find_built_in(query=query, asset_type=assetType)
    return _log_tool_result("unity_asset_find_built_in", _payload(r))

@mcp.tool(description="获取 Unity 资源的序列化属性数据（SerializedObject 深度读取）。")
async def unity_asset_get_data(
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
    maxDepth: int = 10,
):
    _log_tool_call(
        "unity_asset_get_data",
        {
            "assetPath": assetPath,
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
            "maxDepth": maxDepth,
        },
    )
    r = await _get_facade().asset_get_data(
        asset_path=assetPath,
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
        max_depth=maxDepth,
    )
    return _log_tool_result("unity_asset_get_data", _payload(r))

@mcp.tool(
    description="修改 Unity 资源或组件的 SerializedObject 属性。低层写入工具，适合没有专用工具的属性调整；调用前先用 unity_asset_get_data 确认 propertyPath、类型和值，避免写错序列化路径。"
)
async def unity_asset_modify_data(
    properties: list[dict],
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
):
    _log_tool_call(
        "unity_asset_modify_data",
        {
            "properties": properties,
            "assetPath": assetPath,
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().asset_modify_data(
        properties=properties,
        asset_path=assetPath,
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_asset_modify_data", _payload(r))

@mcp.tool(description="将场景中的 GameObject 创建为 Prefab 资源。")
async def unity_prefab_create(sourceGameObjectId: int, prefabPath: str):
    _log_tool_call(
        "unity_prefab_create",
        {"sourceGameObjectId": sourceGameObjectId, "prefabPath": prefabPath},
    )
    r = await _get_facade().prefab_create(
        source_game_object_id=sourceGameObjectId, prefab_path=prefabPath
    )
    return _log_tool_result("unity_prefab_create", _payload(r))

@mcp.tool(description="在场景中实例化指定路径的 Prefab。")
async def unity_prefab_instantiate(prefabPath: str, parentId: int = 0):
    _log_tool_call(
        "unity_prefab_instantiate", {"prefabPath": prefabPath, "parentId": parentId}
    )
    r = await _get_facade().prefab_instantiate(
        prefab_path=prefabPath, parent_id=parentId
    )
    return _log_tool_result("unity_prefab_instantiate", _payload(r))

@mcp.tool(
    description="进入 Prefab 编辑模式。会切换编辑上下文到指定 Prefab；修改后需 unity_prefab_save 保存或 unity_prefab_close 退出。"
)
async def unity_prefab_open(prefabPath: str):
    _log_tool_call("unity_prefab_open", {"prefabPath": prefabPath})
    r = await _get_facade().prefab_open(prefab_path=prefabPath)
    return _log_tool_result("unity_prefab_open", _payload(r))

@mcp.tool(
    description="退出 Prefab 编辑模式。离开当前 Prefab 编辑上下文；若有修改，先确认是否已保存。"
)
async def unity_prefab_close():
    _log_tool_call("unity_prefab_close", {})
    r = await _get_facade().prefab_close()
    return _log_tool_result("unity_prefab_close", _payload(r))

@mcp.tool(
    description="保存当前 Prefab 编辑模式下的修改。会将 Prefab 编辑内容写入磁盘；调用前确认当前 Prefab 是目标资源。"
)
async def unity_prefab_save():
    _log_tool_call("unity_prefab_save", {})
    r = await _get_facade().prefab_save()
    return _log_tool_result("unity_prefab_save", _payload(r))

@mcp.tool(description="创建新的 Unity 材质资源并指定 Shader。")
async def unity_material_create(materialPath: str, shaderName: str = "Standard"):
    _log_tool_call(
        "unity_material_create",
        {"materialPath": materialPath, "shaderName": shaderName},
    )
    r = await _get_facade().material_create(
        material_path=materialPath, shader_name=shaderName
    )
    return _log_tool_result("unity_material_create", _payload(r))

@mcp.tool(description="修改 Unity 材质的属性（颜色、纹理、数值等）。")
async def unity_material_modify(materialPath: str, properties: dict):
    _log_tool_call(
        "unity_material_modify",
        {"materialPath": materialPath, "properties": properties},
    )
    r = await _get_facade().material_modify(
        material_path=materialPath, properties=properties
    )
    return _log_tool_result("unity_material_modify", _payload(r))

@mcp.tool(description="将材质分配给场景中 GameObject 的渲染器。")
async def unity_material_assign(
    targetGameObjectId: int, materialPath: str, materialIndex: int = 0
):
    _log_tool_call(
        "unity_material_assign",
        {
            "targetGameObjectId": targetGameObjectId,
            "materialPath": materialPath,
            "materialIndex": materialIndex,
        },
    )
    r = await _get_facade().material_assign(
        target_game_object_id=targetGameObjectId,
        material_path=materialPath,
        material_index=materialIndex,
    )
    return _log_tool_result("unity_material_assign", _payload(r))

@mcp.tool(description="获取 Unity 材质的详细属性信息。")
async def unity_material_get(materialPath: str):
    _log_tool_call("unity_material_get", {"materialPath": materialPath})
    r = await _get_facade().material_get(material_path=materialPath)
    return _log_tool_result("unity_material_get", _payload(r))

@mcp.tool(description="列出 Unity 中所有可用的 Shader。")
async def unity_shader_list():
    _log_tool_call("unity_shader_list", {})
    r = await _get_facade().shader_list()
    return _log_tool_result("unity_shader_list", _payload(r))

@mcp.tool(
    description="执行 Unity 编辑器中指定路径的菜单项。菜单项可能触发任意编辑器行为、编译、窗口打开或项目修改；调用前确认 menuPath，必要时先 unity_menu_list。"
)
async def unity_menu_execute(menuPath: str):
    _log_tool_call("unity_menu_execute", {"menuPath": menuPath})
    r = await _get_facade().menu_execute(menu_path=menuPath)
    return _log_tool_result("unity_menu_execute", _payload(r))

@mcp.tool(description="列出 Unity 编辑器中所有可用的菜单项。")
async def unity_menu_list():
    _log_tool_call("unity_menu_list", {})
    r = await _get_facade().menu_list()
    return _log_tool_result("unity_menu_list", _payload(r))

@mcp.tool(
    description="通过 Unity Package Manager 添加包（名称、版本或 Git URL）。会修改 Packages/manifest.json 并触发 Unity 包解析/编译；添加前确认包名、版本和项目兼容性。"
)
async def unity_package_add(packageName: str, version: str = ""):
    _log_tool_call(
        "unity_package_add", {"packageName": packageName, "version": version}
    )
    r = await _get_facade().package_add(package_name=packageName, version=version)
    return _log_tool_result("unity_package_add", _payload(r))

@mcp.tool(
    description="通过 Unity Package Manager 移除已安装的包。破坏性项目配置操作：会修改 Packages/manifest.json 并可能导致编译错误；先用 unity_package_list 确认依赖。"
)
async def unity_package_remove(packageName: str):
    _log_tool_call("unity_package_remove", {"packageName": packageName})
    r = await _get_facade().package_remove(package_name=packageName)
    return _log_tool_result("unity_package_remove", _payload(r))

@mcp.tool(description="列出 Unity 项目中所有已安装的包。")
async def unity_package_list():
    _log_tool_call("unity_package_list", {})
    r = await _get_facade().package_list()
    return _log_tool_result("unity_package_list", _payload(r))

@mcp.tool(description="在 Unity Package Manager 注册表中搜索包。")
async def unity_package_search(query: str):
    _log_tool_call("unity_package_search", {"query": query})
    r = await _get_facade().package_search(query=query)
    return _log_tool_result("unity_package_search", _payload(r))

@mcp.tool(description="读取 Unity 项目中指定路径的 C# 脚本内容。")
async def unity_script_read(scriptPath: str):
    _log_tool_call("unity_script_read", {"scriptPath": scriptPath})
    r = await _get_facade().script_read(script_path=scriptPath)
    return _log_tool_result("unity_script_read", _payload(r))

@mcp.tool(
    description="在 Unity 项目中创建新的 C# 脚本文件。写磁盘操作：scriptPath 应位于 Assets 下并以 .cs 结尾；创建/更新完本轮文件后调用 unity_sync_after_disk_write，再编译等待。"
)
async def unity_script_create(scriptPath: str, content: str = ""):
    _log_tool_call(
        "unity_script_create", {"scriptPath": scriptPath, "content": content}
    )
    r = await _get_facade().script_create(script_path=scriptPath, content=content)
    return _log_tool_result("unity_script_create", _payload(r))

@mcp.tool(
    description="更新 Unity 项目中已有 C# 脚本文件。会覆盖文件内容；调用前读取或确认目标文件，完成本轮所有脚本写入后调用 unity_sync_after_disk_write，再用 compile_wait/safe_compile 验证。"
)
async def unity_script_update(scriptPath: str, content: str):
    _log_tool_call(
        "unity_script_update", {"scriptPath": scriptPath, "content": content}
    )
    r = await _get_facade().script_update(script_path=scriptPath, content=content)
    return _log_tool_result("unity_script_update", _payload(r))

@mcp.tool(
    description="删除 Unity 项目中指定路径的 C# 脚本文件。破坏性磁盘操作：调用前确认路径和影响；删除后调用 unity_sync_after_disk_write 并检查编译错误。"
)
async def unity_script_delete(scriptPath: str):
    _log_tool_call("unity_script_delete", {"scriptPath": scriptPath})
    r = await _get_facade().script_delete(script_path=scriptPath)
    return _log_tool_result("unity_script_delete", _payload(r))

@mcp.resource(
    "unity://scenes/hierarchy", description="Unity 当前场景的 GameObject 层级树。"
)
async def resource_scene_hierarchy():
    r = await _get_facade().resource_scene_hierarchy()
    return _log_tool_result("resource_scene_hierarchy", _payload(r))

@mcp.resource("unity://console/logs", description="Unity 控制台最近日志。")
async def resource_console_logs():
    r = await _get_facade().resource_console_logs()
    return _log_tool_result("resource_console_logs", _payload(r))

@mcp.resource("unity://editor/state", description="Unity 编辑器当前状态快照。")
async def resource_editor_state():
    r = await _get_facade().resource_editor_state()
    return _log_tool_result("resource_editor_state", _payload(r))

@mcp.resource("unity://packages/list", description="Unity 项目已安装的包列表。")
async def resource_packages():
    r = await _get_facade().resource_packages()
    return _log_tool_result("resource_packages", _payload(r))

@mcp.resource("unity://build/status", description="Unity 当前构建状态。")
async def resource_build_status():
    r = await _get_facade().resource_build_status()
    return _log_tool_result("resource_build_status", _payload(r))

@mcp.resource(
    "unity://diagnostics/upilot-logs-tab",
    description="upilot 诊断日志标签页布局快照（横向滚动风险、滚动位置等，需打开窗口并切到该标签）。",
)
async def resource_upilot_logs_tab():
    r = await _get_facade().resource_upilot_logs_tab()
    return _log_tool_result("resource_upilot_logs_tab", _payload(r))

@mcp.resource(
    "unity://diagnostics/window",
    description="upilot 全窗口级布局诊断快照（健康分、各区域宽度溢出检测、编译状态、代码版本、Domain Reload 纪元）。",
)
async def resource_window_diagnostics():
    r = await _get_facade().resource_window_diagnostics()
    return _log_tool_result("resource_window_diagnostics", _payload(r))

@mcp.resource(
    "unity://console/summary",
    description="Unity 控制台日志按类型统计（logCount/warningCount/errorCount/assertCount）。",
)
async def resource_console_summary():
    r = await _get_facade().resource_console_summary()
    return _log_tool_result("resource_console_summary", _payload(r))

# Scene graph, component, batch, and selection tools.
@mcp.tool(description="在 Unity 场景中创建新的 GameObject。")
async def unity_gameobject_create(
    name: str = "New GameObject",
    parentId: int = 0,
    primitiveType: str = "",
):
    _log_tool_call(
        "unity_gameobject_create",
        {"name": name, "parentId": parentId, "primitiveType": primitiveType},
    )
    r = await _get_facade().gameobject_create(
        name=name, parent_id=parentId, primitive_type=primitiveType
    )
    return _log_tool_result("unity_gameobject_create", _payload(r))

@mcp.tool(
    description="在 Unity 场景中查找 GameObject，支持按名称、标签或 InstanceID 查找。"
)
async def unity_gameobject_find(
    name: str = "", tag: str = "", instanceId: int = 0
):
    _log_tool_call(
        "unity_gameobject_find", {"name": name, "tag": tag, "instanceId": instanceId}
    )
    r = await _get_facade().gameobject_find(name=name, tag=tag, instance_id=instanceId)
    return _log_tool_result("unity_gameobject_find", _payload(r))

@mcp.tool(
    description="修改 Unity 场景中 GameObject 的属性（名称、标签、层级、激活状态等）。"
)
async def unity_gameobject_modify(
    instanceId: int,
    name: str | None = None,
    tag: str | None = None,
    layer: int | None = None,
    activeSelf: bool | None = None,
    isStatic: bool | None = None,
    parentId: int | None = None,
):
    _log_tool_call(
        "unity_gameobject_modify",
        {
            "instanceId": instanceId,
            "name": name,
            "tag": tag,
            "layer": layer,
            "activeSelf": activeSelf,
            "isStatic": isStatic,
            "parentId": parentId,
        },
    )
    r = await _get_facade().gameobject_modify(
        instance_id=instanceId,
        name=name,
        tag=tag,
        layer=layer,
        active_self=activeSelf,
        is_static=isStatic,
        parent_id=parentId,
    )
    return _log_tool_result("unity_gameobject_modify", _payload(r))

@mcp.tool(
    description="销毁 Unity 场景中的 GameObject。破坏性操作：调用前先用 find/list/get 确认 instanceId 属于目标对象；不会删除磁盘资源，但会修改当前场景，之后需要 scene_save 才会持久化。"
)
async def unity_gameobject_delete(instanceId: int):
    _log_tool_call("unity_gameobject_delete", {"instanceId": instanceId})
    r = await _get_facade().gameobject_delete(instance_id=instanceId)
    return _log_tool_result("unity_gameobject_delete", _payload(r))

@mcp.tool(description="修改 Unity 场景中 GameObject 的变换（位置、旋转、缩放）。")
async def unity_gameobject_move(
    instanceId: int,
    position: dict | None = None,
    rotation: dict | None = None,
    scale: dict | None = None,
):
    _log_tool_call(
        "unity_gameobject_move",
        {
            "instanceId": instanceId,
            "position": position,
            "rotation": rotation,
            "scale": scale,
        },
    )
    r = await _get_facade().gameobject_move(
        instance_id=instanceId,
        position=position,
        rotation=rotation,
        scale=scale,
    )
    return _log_tool_result("unity_gameobject_move", _payload(r))

@mcp.tool(description="复制 Unity 场景中的 GameObject（包含所有子对象和组件）。")
async def unity_gameobject_duplicate(instanceId: int):
    _log_tool_call("unity_gameobject_duplicate", {"instanceId": instanceId})
    r = await _get_facade().gameobject_duplicate(instance_id=instanceId)
    return _log_tool_result("unity_gameobject_duplicate", _payload(r))

@mcp.tool(
    description="在 Unity 中新建空场景。会改变当前编辑器场景上下文；若当前场景有未保存更改，先确认保存策略。"
)
async def unity_scene_create(sceneName: str = ""):
    _log_tool_call("unity_scene_create", {"sceneName": sceneName})
    r = await _get_facade().scene_create(scene_name=sceneName)
    return _log_tool_result("unity_scene_create", _payload(r))

@mcp.tool(
    description="在 Unity 中打开指定路径的场景。mode=single 会替换当前场景，mode=additive 叠加打开；先确认未保存更改和目标路径。"
)
async def unity_scene_open(scenePath: str, mode: str = "single"):
    _log_tool_call("unity_scene_open", {"scenePath": scenePath, "mode": mode})
    r = await _get_facade().scene_open(scene_path=scenePath, mode=mode)
    return _log_tool_result("unity_scene_open", _payload(r))

@mcp.tool(
    description="保存当前 Unity 场景或指定路径的场景。会将当前场景修改写入磁盘；调用前确认目标场景和用户意图。scenePath 为空时保存当前激活场景。"
)
async def unity_scene_save(scenePath: str = ""):
    _log_tool_call("unity_scene_save", {"scenePath": scenePath})
    r = await _get_facade().scene_save(scene_path=scenePath)
    return _log_tool_result("unity_scene_save", _payload(r))

@mcp.tool(
    description="加载 Unity 场景。mode=additive 叠加加载；mode=single 会替换当前打开场景，可能丢失未保存更改，调用前应确认或先保存。"
)
async def unity_scene_load(scenePath: str, mode: str = "additive"):
    _log_tool_call("unity_scene_load", {"scenePath": scenePath, "mode": mode})
    r = await _get_facade().scene_load(scene_path=scenePath, mode=mode)
    return _log_tool_result("unity_scene_load", _payload(r))

@mcp.tool(description="设置指定场景为 Unity 当前激活场景。")
async def unity_scene_set_active(scenePath: str):
    _log_tool_call("unity_scene_set_active", {"scenePath": scenePath})
    r = await _get_facade().scene_set_active(scene_path=scenePath)
    return _log_tool_result("unity_scene_set_active", _payload(r))

@mcp.tool(description="获取 Unity 当前所有已打开场景列表。")
async def unity_scene_list():
    _log_tool_call("unity_scene_list", {})
    r = await _get_facade().scene_list()
    return _log_tool_result("unity_scene_list", _payload(r))

@mcp.tool(
    description="卸载 Unity 场景。可能移除层级视图中的场景；不会删除 .unity 资源。若场景有未保存修改，调用前应确认保存策略。"
)
async def unity_scene_unload(scenePath: str, removeScene: bool = False):
    _log_tool_call(
        "unity_scene_unload", {"scenePath": scenePath, "removeScene": removeScene}
    )
    r = await _get_facade().scene_unload(scene_path=scenePath, remove_scene=removeScene)
    return _log_tool_result("unity_scene_unload", _payload(r))

@mcp.tool(
    description=(
        "确保并打开用于自动化/验收的空场景：若磁盘上已有资源则单场景打开；否则新建 EmptyScene 并保存。"
        "默认 Assets/upilot-test.unity。返回 ensureAction: opened|created 与 scene 信息。"
    ),
)
async def unity_scene_ensure_test(
    sceneName: str = "upilot-test",
    scenePath: str = "",
):
    _log_tool_call(
        "unity_scene_ensure_test", {"sceneName": sceneName, "scenePath": scenePath}
    )
    r = await _get_facade().scene_ensure_test(
        scene_name=sceneName, scene_path=scenePath
    )
    return _log_tool_result("unity_scene_ensure_test", _payload(r))

@mcp.tool(
    description="在指定 GameObject 上添加组件。会修改场景或 Prefab 实例；先确认 gameObjectId 和 componentType，添加后需要保存场景/Prefab 才持久化。"
)
async def unity_component_add(gameObjectId: int, componentType: str):
    _log_tool_call(
        "unity_component_add",
        {"gameObjectId": gameObjectId, "componentType": componentType},
    )
    r = await _get_facade().component_add(
        game_object_id=gameObjectId, component_type=componentType
    )
    return _log_tool_result("unity_component_add", _payload(r))

@mcp.tool(
    description="从指定 GameObject 上移除组件。破坏性场景修改：先用 unity_component_list/get 确认 componentType 与 componentIndex，尤其同类型多组件时。之后需要 scene/prefab 保存才会持久化。"
)
async def unity_component_remove(
    gameObjectId: int, componentType: str, componentIndex: int = 0
):
    _log_tool_call(
        "unity_component_remove",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_remove(
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_remove", _payload(r))

@mcp.tool(description="获取指定 GameObject 上组件的序列化属性。")
async def unity_component_get(
    gameObjectId: int, componentType: str, componentIndex: int = 0
):
    _log_tool_call(
        "unity_component_get",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_get(
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_get", _payload(r))

@mcp.tool(
    description="修改指定 GameObject 上组件的序列化属性。会改变场景或 Prefab 实例状态；先用 unity_component_get 查看属性路径和值，谨慎处理同类型多组件 componentIndex。"
)
async def unity_component_modify(
    gameObjectId: int,
    componentType: str,
    properties: dict,
    componentIndex: int = 0,
):
    _log_tool_call(
        "unity_component_modify",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "properties": properties,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_modify(
        game_object_id=gameObjectId,
        component_type=componentType,
        properties=properties,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_modify", _payload(r))

@mcp.tool(description="列出指定 GameObject 上的所有组件。")
async def unity_component_list(gameObjectId: int):
    _log_tool_call("unity_component_list", {"gameObjectId": gameObjectId})
    r = await _get_facade().component_list(game_object_id=gameObjectId)
    return _log_tool_result("unity_component_list", _payload(r))

@mcp.tool(
    description="批量执行多个 Unity 操作指令（sequential 或 parallel）。用于重复/组合操作；批量中包含删除、写磁盘、包变更、场景保存等破坏性步骤时，必须先确认目标。stopOnError=true 时首个失败会停止后续步骤。"
)
async def unity_batch_execute(
    operations: list,
    mode: str = "sequential",
    stopOnError: bool = True,
):
    _log_tool_call(
        "unity_batch_execute",
        {"operations": operations, "mode": mode, "stopOnError": stopOnError},
    )
    r = await _get_facade().batch_execute(
        operations=operations, mode=mode, stop_on_error=stopOnError
    )
    return _log_tool_result("unity_batch_execute", _payload(r))

@mcp.tool(description="取消正在执行的批量操作。")
async def unity_batch_cancel(batchId: str):
    _log_tool_call("unity_batch_cancel", {"batchId": batchId})
    r = await _get_facade().batch_cancel(batch_id=batchId)
    return _log_tool_result("unity_batch_cancel", _payload(r))

@mcp.tool(description="查询批量操作的执行结果。")
async def unity_batch_results(batchId: str):
    _log_tool_call("unity_batch_results", {"batchId": batchId})
    r = await _get_facade().batch_results(batch_id=batchId)
    return _log_tool_result("unity_batch_results", _payload(r))

@mcp.tool(description="获取 Unity 编辑器当前选中的 GameObject 和资源列表。")
async def unity_selection_get():
    _log_tool_call("unity_selection_get", {})
    r = await _get_facade().selection_get()
    return _log_tool_result("unity_selection_get", _payload(r))

@mcp.tool(
    description="设置 Unity 编辑器的选中项（支持 InstanceID 列表或资源路径列表）。"
)
async def unity_selection_set(
    gameObjectIds: list[int] | None = None,
    assetPaths: list[str] | None = None,
):
    _log_tool_call(
        "unity_selection_set",
        {"gameObjectIds": gameObjectIds, "assetPaths": assetPaths},
    )
    r = await _get_facade().selection_set(
        game_object_ids=gameObjectIds, asset_paths=assetPaths
    )
    return _log_tool_result("unity_selection_set", _payload(r))

@mcp.tool(description="清空 Unity 编辑器的当前选中项。")
async def unity_selection_clear():
    _log_tool_call("unity_selection_clear", {})
    r = await _get_facade().selection_clear()
    return _log_tool_result("unity_selection_clear", _payload(r))


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
    "unity_batch_execute",
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
