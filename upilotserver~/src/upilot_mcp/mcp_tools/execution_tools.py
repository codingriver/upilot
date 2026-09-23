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


async def _run_readonly(name: str, arguments: dict[str, Any], call):
    _log_tool_call(name, arguments)
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


@mcp.tool(description="执行 UPilot 自有 C# 子集 V2。支持 try/catch/finally/throw、引用捕获 closure、block/async lambda、Task/ValueTask await、实用级泛型推断、??/??=/typeof/nameof/default(T)、隐式数组及 rank 1–4 多维数组。逃逸 closure/async delegate 和事件订阅必须使用 persistent session；禁止 async void。源码不支持 named/ref/out 参数；这类调用使用结构化 unity_reflection_call。\n\n后端：interpret 执行完整 V2 AST；emit 是兼容 DynamicMethod 入口缓存且仍执行 AST；compiled 将可静态绑定的同步有限子集 lowering 为 Expression Tree delegate，不支持时执行前失败且不回退；auto 保持首次解释/后续 emit-cache 的兼容行为，不自动选择 compiled。\n\n默认 import 命名空间：System、UnityEngine、UnityEditor。\n\n完整预算字段（默认值/硬上限）：timeoutMs(3000/30000)、maxStatements(10000/100000)、maxLoopIterations(10000/100000)、maxCalls(1000/100000)、maxAllocations(1000/100000)、maxRecursion(64/256)、maxResultBytes(1048576/1048576)、maxAwaits(1000/100000)、maxArrayElements(100000/1000000)。\n\n错误恢复结构：error.detail 含 stage（parse/bind/policy/runtime/budget/cancelled）、sourceSpan、candidates、sideEffectsMayHaveOccurred、nextAction、cleanupDiagnostics。取消与预算失败不回滚且不得自动重试。cancel 后 finally 获得 100ms/256-statement 清理预算。\n\n固定 LRU：emit 256 键、compiled 128 键。成功响应 resourceDiagnostics 中 EVAL_CACHE_EVICTED / EVAL_CACHE_WARMUP_FAILED 是 warning，保留成功结果且无需重试；失败时诊断在 error.detail.resourceDiagnostics，不覆盖原始错误。resourceDiagnosticsDroppedCount 报告截断。unity_capabilities_get.execution.resources 返回实时统计。\n\nauto 后端缓存 key 包含 code、mode、imports，变更任一组件即改变缓存键。不依赖 Roslyn、DLL 加载或 Unity 编译 API。")
async def csharp_eval(
    code: Annotated[str, Field(description="要执行的一条表达式或受支持的 C# 子集语句块。")],
    mode: Annotated[str, Field(description="解析模式：auto、expression 或 statements。")] = "auto",
    sessionId: Annotated[str, Field(description="可选 persistent session；跨调用变量、handle、事件订阅或非内联结果需要它。")] = "",
    variables: Annotated[dict[str, Any] | None, Field(description="名称到普通 JSON 或显式 TypedValue 的输入变量映射。")] = None,
    imports: Annotated[list[str] | None, Field(description="允许参与类型解析的命名空间列表；不会加载程序集。")] = None,
    executionBackend: Annotated[str, Field(description="执行后端：auto、interpret、emit 或 compiled；compiled 仅接受可静态绑定的同步子集且不静默回退；代码开始执行后不会切换后端重放。")] = "auto",
    limits: Annotated[dict[str, Any] | None, Field(description="可选预算覆盖，例如 timeoutMs、maxStatements、maxLoopIterations、maxCalls、maxAllocations、maxAwaits、maxArrayElements 和 maxResultBytes。")] = None,
    resultMode: Annotated[str, Field(description="结果编码：auto、inline、handle 或 legacyString；handle 结果需要 session。")] = "auto",
):
    args = locals().copy()
    return await _run("csharp_eval", args, lambda: _get_facade().csharp_eval(
        code=code, mode=mode, session_id=sessionId, variables=variables, imports=imports,
        execution_backend=executionBackend, limits=limits, result_mode=resultMode,
    ))


@mcp.tool(description="只读验证 UPilot C# 子集源码，不执行代码。interpret 检查词法/语法；emit 接受完整 V2 Eval AST（含 lambda/closure/await）并检查缓存的运行时能力，不套用动态类型方法体的同步限制；compiled 额外完成静态绑定、Expression Tree lowering 与 delegate 编译。不会调用 getter、构造器、用户转换或业务方法，预检成功不保证运行成功。compiled 预检共用 128 键 LRU；resourceDiagnostics 中 EVAL_CACHE_EVICTED 是成功 warning，不是验证失败，无需重试；resourceDiagnosticsDroppedCount 报告截断。")
async def csharp_validate(
    code: Annotated[str, Field(description="要验证的一条表达式或 C# 子集语句块。")],
    mode: Annotated[str, Field(description="解析模式：auto、expression 或 statements。")] = "auto",
    backend: Annotated[str, Field(description="验证目标：interpret、emit 或 compiled。")] = "interpret",
    imports: Annotated[list[str] | None, Field(description="参与类型解析的命名空间列表；不会加载程序集。")] = None,
    variableTypes: Annotated[dict[str, str] | None, Field(description="compiled 验证使用的变量名到 CLR 类型名映射，不传业务对象。")] = None,
):
    args = locals().copy()
    return await _run_readonly("csharp_validate", args, lambda: _get_facade().csharp_validate(
        code=code, mode=mode, backend=backend, imports=imports, variable_types=variableTypes,
    ))


@mcp.tool(description="根据结构化 spec 使用 Reflection.Emit 创建 session 绑定的临时 CLR Type。\n\nSpec 完整字段：typeName、visibility(public/internal)、baseType、interfaces、isSealed、bodyBackend(interpret/compiled)；fields[name/typeName/visibility(isStatic/isReadonly)]；properties[name/typeName/visibility/hasGetter/hasSetter/backingField/getterBody/setterBody/bodyBackend]；constructors[visibility/parameters/baseConstructorParameterTypeNames/baseArgumentNames/body/bodyBackend]；methods[name/returnType/parameters/visibility(isStatic/isVirtual/isFinal)/implements/overrides/body/bodyBackend/callbackHandle/callbackPolicy]。成员 bodyBackend 覆盖类型默认值。\n\ninterpret body 使用支持 try/catch/finally、泛型和 rank 1–4 数组的同步 V2 profile；compiled body 在类型发布前直接 lowering，边界外节点失败且不回退。两者都拒绝 lambda/closure/await/async。\n\nCallback policy：exceptionMode(isolate 返回默认值 / propagate 用 ExceptionDispatchInfo 重抛原始异常堆栈)、maxInvocations(默认10000/上限100000)、maxReentrancy(默认8/上限64/ThreadStatic)、diagnosticsCapacity(默认32/上限128)。非 callback 方法始终 propagate。\n\ncachePolicy 仅支持 specHash，Server 自动计算 spec SHA-256。缓存命中跨 session 复用同一 Engine 的 CLR Type，实例与 callback 状态仍隔离。未提供 constructors 时生成公开无参构造器，要求基类也有无参构造。\n\nDomain 固定 256 次生成配额，DefineType 开始后失败不退还；满额仍可命中已有类型。超限返回 EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED；Session 提前预留 type/instance 槽，超限返回 SESSION_LIMIT_EXCEEDED。失败后的 EMIT_GENERATION_SLOT_CONSUMED 位于 error.detail.resourceDiagnostics，不覆盖原始错误。compiled 缓存淘汰可随成功响应返回 EVAL_CACHE_EVICTED warning，不得因此重试。关闭 session、重连不释放类型配额，不自动 Reload。\n\n仅支持 Editor/JIT，不保存/加载 DLL、不接受 raw IL、不替换已有程序集方法、不降级到源码编译。REFLECTION_EMIT_UNAVAILABLE 时应改用 unity_reflection_call 或项目现有编译类型。")
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


@mcp.tool(description="递归打印任意 C# 运行时对象所有字段/属性。需配合 execution_session + csharp_eval(resultMode=handle) 先获得对象 handle。支持配置嵌套深度、静态字段、忽略类型、JSON/文本双格式输出。纯只读操作。\n\n响应始终包含 .root（JSON 树）和 .text（缩进文本），outputFormat 仅指示首选视图。文本默认隐藏节点类型注记；需要显示 (Type) 或 (DeclaredType -> RuntimeType) 时设置 includeTypeNames=true，.root 中的类型字段始终保留。\n\n常见 Unity 值类型（Vector、Quaternion、Color、Rect、Bounds、Matrix 等）默认作为叶子摘要，避免递归计算属性；仅在明确需要时设置 expandUnityValueTypes=true。Delegate、Assembly、Module 和 MemberInfo 默认仅显示摘要；仅在明确需要 CLR 内部结构时设置 expandReflectionTypes=true。普通对象的字段和属性值不受此摘要规则影响。\n\nignoreTypes 与默认跳过集（IntPtr、UIntPtr、RuntimeType、RuntimeMethodHandle、RuntimeFieldHandle、Thread）是并集关系，非替换。\n\n限制：编译器生成字段(IsSpecialName)、const 字段(IsLiteral)、索引器属性始终跳过；非泛型集合元素无 declaredType；字段/属性 getter 异常静默返回 null；静态属性与实例属性同名时静态属性被跳过；循环引用输出 circular ref 标记。硬限制：maxDepth ≤64、maxFieldsPerNode ≤500（隐式下限1）、maxTotalNodes ≤20000。")
async def csharp_object_dump(
    sessionId: Annotated[str, Field(description="persistent execution session ID。")],
    handle: Annotated[str, Field(description="来自 session 的对象 handle（如 csharp_eval resultMode=handle 返回的 handle）。")],
    maxDepth: Annotated[int, Field(description="最大递归深度，默认 3（0 不展开子对象，上限 64）。")] = 3,
    maxFieldsPerNode: Annotated[int, Field(description="每个对象节点最多展开的字段数，默认 100（上限 500）。")] = 100,
    maxTotalNodes: Annotated[int, Field(description="全局最大节点数限制防膨胀，默认 5000（上限 20000）。")] = 5000,
    includeStatic: Annotated[bool, Field(description="是否包含静态字段/属性；默认 false。")] = False,
    includeTypeNames: Annotated[bool, Field(description="是否在 .text 中显示 (Type) 和 (DeclaredType -> RuntimeType) 类型注记；默认 false。.root 中的 declaredType/runtimeType 始终保留。")] = False,
    expandUnityValueTypes: Annotated[bool, Field(description="是否递归展开常见 Unity 值类型；默认 false，仅返回有界叶子摘要。")] = False,
    expandReflectionTypes: Annotated[bool, Field(description="是否递归展开 Delegate、Assembly、Module、MemberInfo 等反射基础类型；默认 false，仅返回有界摘要，普通成员值不受影响。")] = False,
    ignoreTypes: Annotated[list[str] | None, Field(description="不展开子字段的完整类型名列表，如 ['System.String', 'UnityEngine.Vector3']；匹配时仅显示类型名。")] = None,
    outputFormat: Annotated[str, Field(description="输出格式：json 返回结构化树，text 返回缩进文本。默认 json。")] = "json",
    indentation: Annotated[str, Field(description="text 模式缩进字符串，默认两个空格。")] = "  ",
):
    args = locals().copy()
    return await _run("csharp_object_dump", args, lambda: _get_facade().csharp_object_dump(
        session_id=sessionId, handle=handle, max_depth=maxDepth,
        max_fields_per_node=maxFieldsPerNode, max_total_nodes=maxTotalNodes,
        include_static=includeStatic, include_type_names=includeTypeNames,
        expand_unity_value_types=expandUnityValueTypes,
        expand_reflection_types=expandReflectionTypes,
        ignore_types=ignoreTypes,
        output_format=outputFormat, indentation=indentation,
    ))


for _name in ("execution_session", "csharp_validate", "csharp_eval", "reflection_emit_type", "csharp_object_dump"):
    register_public_tool(
        _name,
        public_handler=globals()[_name],
        facade_method=_name,
        category="execution",
        destructive=False if _name in {"csharp_validate", "csharp_object_dump"} else True,
        idempotent=True if _name in {"csharp_validate", "csharp_object_dump"} else False,
        requires_write_access=False if _name in {"csharp_validate", "csharp_object_dump"} else True,
        play_mode_policy="allowed",
        feature="core",
        timeout_ms=60000 if _name == "csharp_object_dump" else 35000,
        capability_requirements=("unity-bridge", "execution-core"),
    )
