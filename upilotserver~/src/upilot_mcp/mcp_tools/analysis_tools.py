from __future__ import annotations

from ..tool_registry import register_public_tool
from .. import mcp_stdio_server as runtime

mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result


@mcp.tool(description="按主键读取 CSV 配置字段，自动识别并报告 UTF-8/GBK、换行、分隔符、列数和唯一性。")
async def unity_config_csv_get(
    path: str,
    keys: dict,
    fields: list[str] | None = None,
    headerRowIndex: int = 0,
    encoding: str = "auto",
):
    args = {"path": path, "keys": keys, "fields": fields, "headerRowIndex": headerRowIndex, "encoding": encoding}
    _log_tool_call("unity_config_csv_get", args)
    result = await _get_facade().config_csv_get(path=path, keys=keys, fields=fields, header_row_index=headerRowIndex, encoding=encoding)
    return _log_tool_result("unity_config_csv_get", _payload(result))


@mcp.tool(description="按主键安全修改 CSV 字段。默认 dryRun=true；应用时必须使用预览返回的 confirmToken，并保持编码、换行及目标记录之外的字节不变。")
async def unity_config_csv_patch(
    path: str,
    keys: dict,
    changes: dict,
    expectedValues: dict | None = None,
    headerRowIndex: int = 0,
    encoding: str = "auto",
    dryRun: bool = True,
    confirmToken: str = "",
):
    args = {"path": path, "keys": keys, "changes": changes, "expectedValues": expectedValues, "headerRowIndex": headerRowIndex, "encoding": encoding, "dryRun": dryRun, "confirmToken": bool(confirmToken)}
    _log_tool_call("unity_config_csv_patch", args)
    result = await _get_facade().config_csv_patch(path=path, keys=keys, changes=changes, expected_values=expectedValues, header_row_index=headerRowIndex, encoding=encoding, dry_run=dryRun, confirm_token=confirmToken)
    return _log_tool_result("unity_config_csv_patch", _payload(result))


@mcp.tool(description="不依赖 Unity 主线程响应，返回 Unity 进程 CPU、网络心跳、主线程 pump、队列深度和疑似忙循环状态。")
async def unity_hang_status(sampleWindowSec: float = 0.5):
    _log_tool_call("unity_hang_status", {"sampleWindowSec": sampleWindowSec})
    result = await _get_facade().hang_status(sample_window_sec=sampleWindowSec)
    return _log_tool_result("unity_hang_status", _payload(result))


@mcp.tool(description="Windows 下为当前 Unity 进程生成非终止式 minidump。默认写入项目 Log/UPilotDiagnostics，不会结束 Unity。")
async def unity_hang_capture(outputPath: str = "", dumpType: str = "mini"):
    _log_tool_call("unity_hang_capture", {"outputPath": outputPath, "dumpType": dumpType})
    result = await _get_facade().hang_capture(output_path=outputPath, dump_type=dumpType)
    return _log_tool_result("unity_hang_capture", _payload(result))


@mcp.tool(description="只读分析一个 C# 文件或类型，返回类型、using、成员和启发式引用位置。")
async def unity_script_analyze(pathOrType: str, includeMembers: bool = True, includeUsages: bool = True):
    _log_tool_call("unity_script_analyze", {"pathOrType": pathOrType, "includeMembers": includeMembers, "includeUsages": includeUsages})
    result = await _get_facade().script_analyze(path_or_type=pathOrType, include_members=includeMembers, include_usages=includeUsages)
    return _log_tool_result("unity_script_analyze", _payload(result))


@mcp.tool(description="构建只读 C# 类型依赖图。首版基于源码符号索引，所有不能语义确认的边均标记 heuristic。")
async def unity_script_dependency_graph(roots: list[str], direction: str = "outgoing", maxDepth: int = 3, includeEditor: bool = True):
    _log_tool_call("unity_script_dependency_graph", {"roots": roots, "direction": direction, "maxDepth": maxDepth, "includeEditor": includeEditor})
    result = await _get_facade().script_dependency_graph(roots=roots, direction=direction, max_depth=maxDepth, include_editor=includeEditor)
    return _log_tool_result("unity_script_dependency_graph", _payload(result))


@mcp.tool(description="只读检测 Unity 版本、Packages、asmdef、Editor/Runtime 边界和测试程序集。")
async def unity_project_stack_detect():
    _log_tool_call("unity_project_stack_detect", {})
    result = await _get_facade().project_stack_detect()
    return _log_tool_result("unity_project_stack_detect", _payload(result))


@mcp.tool(description="只读返回运行时 NavMesh Surface、DataInstance 可观测状态、Agent 就绪统计和全局三角化摘要。")
async def unity_navmesh_status(includeSurfaces: bool = True, includeAgents: bool = True, includeTriangulation: bool = True):
    args = {"includeSurfaces": includeSurfaces, "includeAgents": includeAgents, "includeTriangulation": includeTriangulation}
    _log_tool_call("unity_navmesh_status", args)
    result = await _get_facade().navmesh_status(include_surfaces=includeSurfaces, include_agents=includeAgents, include_triangulation=includeTriangulation)
    return _log_tool_result("unity_navmesh_status", _payload(result))


@mcp.tool(description="只读批量调用 NavMesh.SamplePosition，返回命中点、距离、最近边缘及基于推导 Bounds 的 Surface 匹配。")
async def unity_navmesh_sample(points: list[dict], maxDistance: float = 10.0, areaMask: int = -1, agentTypeId: int = -1):
    args = {"points": points, "maxDistance": maxDistance, "areaMask": areaMask, "agentTypeId": agentTypeId}
    _log_tool_call("unity_navmesh_sample", args)
    result = await _get_facade().navmesh_sample(points=points, max_distance=maxDistance, area_mask=areaMask, agent_type_id=agentTypeId)
    return _log_tool_result("unity_navmesh_sample", _payload(result))


@mcp.tool(description="只读返回当前 NavMesh 全局 triangulation 的顶点、三角形、区域数与世界 Bounds。")
async def unity_navmesh_triangulation_summary():
    _log_tool_call("unity_navmesh_triangulation_summary", {})
    result = await _get_facade().navmesh_triangulation_summary()
    return _log_tool_result("unity_navmesh_triangulation_summary", _payload(result))


@mcp.tool(description="启动结构化运行时 Profiler 采集，记录 CPU/GPU/GC/渲染计数及常见运行时组件规模，终态输出 JSON/CSV。")
async def unity_profiler_capture_start(
    durationSec: float = 30.0,
    sampleEveryFrames: int = 1,
    title: str = "runtime-profiler",
    outputDirectory: str = "",
    markerNames: list[str] | None = None,
    markerNameRegex: str = "",
    maxMarkers: int = 64,
    telemetryTypeName: str = "",
    telemetryMethodName: str = "",
    baselineJsonPath: str = "",
):
    args = {"durationSec": durationSec, "sampleEveryFrames": sampleEveryFrames, "title": title, "outputDirectory": outputDirectory, "markerNames": markerNames, "markerNameRegex": markerNameRegex, "maxMarkers": maxMarkers, "telemetryTypeName": telemetryTypeName, "telemetryMethodName": telemetryMethodName, "baselineJsonPath": baselineJsonPath}
    _log_tool_call("unity_profiler_capture_start", args)
    result = await _get_facade().profiler_capture_start(duration_sec=durationSec, sample_every_frames=sampleEveryFrames, title=title, output_directory=outputDirectory, marker_names=markerNames, marker_name_regex=markerNameRegex, max_markers=maxMarkers, telemetry_type_name=telemetryTypeName, telemetry_method_name=telemetryMethodName, baseline_json_path=baselineJsonPath)
    return _log_tool_result("unity_profiler_capture_start", _payload(result))


@mcp.tool(description="查询结构化 Profiler 采集进度、样本数、可用计数器和产物路径。")
async def unity_profiler_capture_status(captureId: str = ""):
    _log_tool_call("unity_profiler_capture_status", {"captureId": captureId})
    result = await _get_facade().profiler_capture_status(capture_id=captureId)
    return _log_tool_result("unity_profiler_capture_status", _payload(result))


@mcp.tool(description="提前停止结构化 Profiler 采集并生成 JSON/CSV 与 P50/P95/P99 摘要。")
async def unity_profiler_capture_stop(captureId: str = ""):
    _log_tool_call("unity_profiler_capture_stop", {"captureId": captureId})
    result = await _get_facade().profiler_capture_stop(capture_id=captureId)
    return _log_tool_result("unity_profiler_capture_stop", _payload(result))


_DESTRUCTIVE = {"unity_config_csv_patch", "unity_hang_capture"}
_NON_IDEMPOTENT = {"unity_profiler_capture_start", "unity_profiler_capture_stop"}
for _name, _value in list(globals().items()):
    if callable(_value) and _name.startswith("unity_"):
        register_public_tool(
            _name,
            destructive=_name in _DESTRUCTIVE,
            idempotent=_name not in (_DESTRUCTIVE | _NON_IDEMPOTENT),
            requires_unity_connection=_name not in {"unity_hang_status", "unity_hang_capture"},
        )
