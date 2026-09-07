from __future__ import annotations

from typing import Annotated, Any

from pydantic import Field

from .. import mcp_stdio_server as runtime
from ..tool_registry import register_public_tool

mcp = runtime.mcp
_get_facade = runtime._get_facade
_payload = runtime._payload
_log_tool_call = runtime._log_tool_call
_log_tool_result = runtime._log_tool_result
_reject_write_if_unapproved = runtime._reject_write_if_unapproved


async def _run(name: str, arguments: dict[str, Any], call):
    _log_tool_call(name, arguments)
    rejected = _reject_write_if_unapproved(name)
    if rejected is not None:
        return rejected
    return _log_tool_result(name, _payload(await call()))


@mcp.tool(description="管理执行 session。open 创建持久对象、词法 closure、异步操作、类型和事件订阅生命周期；status 返回活动异步任务、订阅、callback 计数及有界诊断；close 会解绑事件、取消协作式异步操作并使 closure/delegate 失效，最后释放变量和句柄。已开始的项目方法不能强制中断。")
async def execution_session(
    action: Annotated[str, Field(description="生命周期动作：open、status 或 close。")],
    sessionId: Annotated[str, Field(description="open 时留空；status/close 时传 open 返回的 opaque session ID。")] = "",
    title: Annotated[str, Field(description="open 的可选诊断标题。")] = "",
    ttlSec: Annotated[int, Field(description="open 的空闲 TTL 秒数；默认 600，硬上限 3600。")] = 600,
    maxHandles: Annotated[int, Field(description="session 可持有的最大对象/type/delegate handle 数；默认 256。")] = 256,
    maxDynamicTypes: Annotated[int, Field(description="session 可登记的最大动态类型数；默认 32。")] = 32,
    maxCallbacks: Annotated[int, Field(description="session 可登记的最大 callback/accessor 数；默认 64。")] = 64,
    maxAsyncOperations: Annotated[int, Field(description="session 最大活动异步操作数；默认 64，硬上限 256。")] = 64,
):
    args = locals().copy()
    return await _run("execution_session", args, lambda: _get_facade().execution_session(
        action=action, session_id=sessionId, title=title, ttl_sec=ttlSec,
        max_handles=maxHandles, max_dynamic_types=maxDynamicTypes, max_callbacks=maxCallbacks,
        max_async_operations=maxAsyncOperations,
    ))


@mcp.tool(description="执行 UPilot 自有 C# 子集 V2。支持 try/catch/finally/throw、引用捕获 closure、block/async lambda、Task/ValueTask await、实用级泛型推断、隐式数组及 rank 1–4 多维数组。逃逸 closure/async delegate 和事件订阅必须使用 persistent session；禁止 async void。取消与预算失败不回滚且不得自动重试。不依赖 Roslyn 或 Unity 编译 API。")
async def csharp_eval(
    code: Annotated[str, Field(description="要执行的一条表达式或受支持的 C# 子集语句块。")],
    mode: Annotated[str, Field(description="解析模式：auto、expression 或 statements。")] = "auto",
    sessionId: Annotated[str, Field(description="可选 persistent session；跨调用变量、handle、事件订阅或非内联结果需要它。")] = "",
    variables: Annotated[dict[str, Any] | None, Field(description="名称到普通 JSON 或显式 TypedValue 的输入变量映射。")] = None,
    imports: Annotated[list[str] | None, Field(description="允许参与类型解析的命名空间列表；不会加载程序集。")] = None,
    executionBackend: Annotated[str, Field(description="执行后端：auto、interpret 或 emit；代码开始执行后不会切换后端重放。")] = "auto",
    limits: Annotated[dict[str, Any] | None, Field(description="可选预算覆盖，例如 timeoutMs、maxStatements、maxLoopIterations、maxCalls、maxAllocations、maxAwaits、maxArrayElements 和 maxResultBytes。")] = None,
    resultMode: Annotated[str, Field(description="结果编码：auto、inline、handle 或 legacyString；handle 结果需要 session。")] = "auto",
):
    args = locals().copy()
    return await _run("csharp_eval", args, lambda: _get_facade().csharp_eval(
        code=code, mode=mode, session_id=sessionId, variables=variables, imports=imports,
        execution_backend=executionBackend, limits=limits, result_mode=resultMode,
    ))


@mcp.tool(description="根据结构化 spec 使用 Reflection.Emit 创建 session 绑定的临时 CLR Type。同步 body 支持 try/catch/finally、泛型和 rank 1–4 数组，但拒绝 lambda/closure/await/async；callback policy 可隔离异常并记录有界诊断。仅支持 Editor/JIT，不保存 DLL、不接受 raw IL、不降级到源码编译。")
async def reflection_emit_type(
    sessionId: Annotated[str, Field(description="必填 persistent session ID；type/instance/callback/accessor 生命周期绑定到该 session。")],
    spec: Annotated[dict[str, Any], Field(description="动态类型结构化 spec：typeName、base/interfaces、fields/properties/constructors/methods 及受限同步 body。")],
    cachePolicy: Annotated[str, Field(description="缓存策略；V2 使用 specHash，同一 Domain 只复用 canonical spec 对应的 CLR Type。")] = "specHash",
    nameConflictPolicy: Annotated[str, Field(description="同名不同 spec 策略：reject 或 hashSuffix。")] = "reject",
    createInstance: Annotated[bool, Field(description="是否立即创建绑定当前 session 的实例并返回 instanceHandle。")] = False,
    constructorArguments: Annotated[list[Any] | None, Field(description="createInstance=true 时使用的普通 JSON 或 TypedValue 构造参数。")] = None,
):
    args = locals().copy()
    return await _run("reflection_emit_type", args, lambda: _get_facade().reflection_emit_type(
        session_id=sessionId, spec=spec, cache_policy=cachePolicy,
        name_conflict_policy=nameConflictPolicy, create_instance=createInstance,
        constructor_arguments=constructorArguments,
    ))


for _name in ("execution_session", "csharp_eval", "reflection_emit_type"):
    register_public_tool(
        _name,
        facade_method=_name,
        category="execution",
        destructive=True,
        idempotent=False,
        requires_write_access=True,
        play_mode_policy="allowed",
        feature="core",
        timeout_ms=35000,
        capability_requirements=("unity-bridge", "execution-core"),
    )
