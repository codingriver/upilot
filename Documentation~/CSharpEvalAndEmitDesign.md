# `csharp_eval`、Reflection.Emit 与执行 Session

## 2026-09-22 边界修正、固定容量与请求级通知

本轮保持 Eval/Emit-first 能力范围，不新增源码语法、DLL、类型卸载、热替换入口或设置界面。

- Eval `emit` 预检使用完整 V2 AST 和缓存的运行时探测，允许 lambda/closure/await；动态类型方法体继续使用同步限制。`interpret` 只解析，预检成功不等于运行时绑定成功。
- 两类 program cache 使用同一小型内部 LRU 实现，emit 为 256 个源码键，compiled 为 128 个编译键；compiled 预检与执行共用缓存。同步/异步 emit 同键只占一槽，查询不会改变 LRU，失败编译不淘汰旧项，在途 delegate 不失效。
- Domain 共享 256 次用户类型生成配额，开始不可逆生成阶段后失败不退还；缓存命中不占新配额。探针最多生成一个独立类型。Session 在解码参数和生成前预留 type/instance 容量，成功存储消费预留，失败释放未消费部分。
- 关闭 Session、重建 Engine、重连 Server、无 Reload 的模式切换不能恢复 Domain 配额。不自动退出 PlayMode、Reload、强制 GC 或重放用户调用。
- Session/类型超限返回错误；LRU 淘汰与 auto 成功后的预热失败返回 warning，保留成功结果。诊断随当前请求返回，不以全局计数差值推算。

### 返回契约

成功的 `csharp_eval`、`csharp_validate`、`reflection_emit_type` 增加 `resourceDiagnostics` 和 `resourceDiagnosticsDroppedCount`。失败时同字段位于 `error.detail`，原始错误码及副作用证据保留；不改变现有 `diagnostics` 类型。每项包含 `code/severity/resource/action/count/limit/used/nextAction`，按 code/resource 聚合，最多 16 项。

```json
{
  "status": "Succeeded",
  "resourceDiagnostics": [{
    "code": "EVAL_CACHE_EVICTED", "severity": "warning",
    "resource": "compiledCache", "action": "evict",
    "count": 1, "limit": 128, "used": 128,
    "nextAction": "No retry is needed for a successful request."
  }],
  "resourceDiagnosticsDroppedCount": 0
}
```

稳定代码：`EVAL_CACHE_EVICTED`、`EVAL_CACHE_WARMUP_FAILED` 是成功警告；`EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED`、`SESSION_LIMIT_EXCEEDED` 是拒绝错误；`EMIT_GENERATION_SLOT_CONSUMED` 解释失败后的资源消耗，不替换首因。已有淘汰通知在后续业务失败时仍保留。

`unity_capabilities_get.execution.resources` 每次返回实时快照：独立 Domain `generation`、两个缓存的 `capacity/count/hits/misses/evictions`，以及 `domainTypes.limit/used/rejected/failedAfterReservation/probeTypeCount`。这些是条目/尝试次数限制，不是进程内存硬上限。

### 改动范围与验证

- `ExecutionCore.cs`：请求诊断、program LRU、Session 容量预留；两个 Backend 接入共享 LRU。
- `ReflectionEmitEngine.cs`：Domain 预算、一次探测及 compiled body 诊断传递。
- `CSharpSubsetEngine.cs`：求值上下文显式携带请求诊断；`UPilotExecutionService.cs` 和协议 DTO 负责成功/失败响应及实时统计。
- Python execution service 保留嵌套资源诊断，MCP 描述及既有 Skill 同步真实能力边界。
- `UPilotExecutionResourceTests.cs` 使用独立小容量缓存/预算，覆盖预检、LRU、并发隔离、错误首因、探针、预留、构造失败；不耗尽生产配额。Unity 6/2022.3 仅运行定向验收。

注意：共享 Binder 支持 named/ref/out 不等于 Eval 源码支持这些参数语法；当前应使用结构化 `unity_reflection_call`。

### 本轮交付证据

2026-09-22 最终定向验收：两个项目各运行 `UPilotExecutionResourceTests` 的 14 项及 5 项现有直接回归，共 19/19；均 `cleanupVerified=true`、`testIdentityVerified=true`、`sourceUnchanged=true`。不是完整 EditMode 回归。

| 项目 | 编译请求 / 写批次 | 测试 runGuid |
| --- | --- | --- |
| Unity 6000.6.0a2 / `Tests~/UPilotTest` | `req-bb300696-dc75-433c-9997-6b2e1fd6a1ff` / `wb-2df3c5d1-6ee5-4114-84fb-5fff345e1736` | `207e8c0a-3ee2-4d5a-841e-487e89f0868f` |
| Unity 2022.3.62f2 / `Tests~/UPilotTest2022` | `req-349218e6-254e-47ff-b1df-e106a50afe69` / `wb-7e3c5b5e-7a7d-470a-8dc8-1e9b4e45bfcf` | `700a6d73-1b5b-48d5-87e8-6ea73ff664cb` |

两批均为 `verified/passed`、`correlationVerified=true`、`errorsVerified=true`，编译 0 error / 0 warning。Unity 6 的 `lastCompileVerifiedAt=1790056399874 >= writeBatchCreatedAt=1790056380770`；2022.3 为 `1790056395569 >= 1790056380825`。关联结果保存在各项目 `Log/P0P1/eval-resources-early-check-result.json`，不能用后续无批次身份的自动编译快照替代。

- Unity 6 报告：`Tests~/UPilotTest/Log/UPilotAcceptance/1790056453807_req-3367a774-2f8f-4c5b-8c3d-29c2faab7397/summary.json`，485717 bytes，SHA256 `9e0e22c1d7449b7f2dfcd27454639e46dc4f0dd856412e82a717e4a2861dfaad`。
- Unity 2022.3 报告：`Tests~/UPilotTest2022/Log/UPilotAcceptance/1790056453725_req-5f1b37a0-b67b-495f-a52e-674e50a7ac7a/summary.json`，335553 bytes，SHA256 `cf531f60c3fafa093cbb26142214c0505e5ae158ea870d56409a575e16e496c0`。
- Python：`test_execution_tools.py` + `test_p2_public_tool_contracts.py`，55 passed；另有 2 条既有 websockets 弃用警告。
- Agent Rules 37 / Skill Pack 39：source render/check、source 校验、规范项目两个 installed 校验通过；五目标重复同步与最终 check 均为 `current`。重复同步的 44 个受管文件内容/时间戳不变，规则块外字节保留，没有新增备份。不修改发布版本。
- Server 刷新后，8011/8017 的在线 `csharp_eval`、`csharp_validate`、`reflection_emit_type` 描述均包含新资源契约；实际预检和拒绝调用通过。Server PID 为 69860/54220，Unity PID 57636/48124 未变。此为实际行为及描述验证，不是完整已加载 Python 源码哈希证明；现有客户端的 `deploymentFreshness=unverified` 标记仍如实保留。

真实 Session 拒绝样例（省略不相关字段）：空 Session 的 `maxHandles=1` 无法满足创建 type + instance 的两个槽位，拒绝发生在解码/生成之前。

```json
{
  "ok": false,
  "error": {
    "code": "SESSION_LIMIT_EXCEEDED",
    "message": "Cannot store/reserve 2 handle(s) and 1 type(s): session capacity was exceeded for sessionHandles",
    "detail": {
      "sideEffectsMayHaveOccurred": false,
      "resourceDiagnostics": [{
        "code": "SESSION_LIMIT_EXCEEDED", "severity": "error",
        "resource": "sessionHandles", "action": "reject",
        "count": 1, "limit": 1, "used": 0,
        "nextAction": "Inspect session capacity and partial state. Use a session with enough free handles/types, or explicitly open a new session with sufficient limits. Closing a session invalidates its handles; do not replay automatically."
      }],
      "resourceDiagnosticsDroppedCount": 0
    }
  }
}
```

该请求证据位于 `Tests~/UPilotTest/Log/P0P1/eval-resources-delivery-refusal.json`（11759 bytes，SHA256 `9d998a203940f19df2b4f27ecdfe9d14049024a401fdf8879a54c9f8378f6b1f`）；自建 Session 已关闭，无遗留 handle 或 cleanup error。前述成功 warning JSON 是契约示例，由独立小容量缓存测试和 Python MCP `isError=false` 契约验证，没有为采样填满生产缓存。

真实 `execution.resources` 快照：`generation=bba2213e0aa846518e0b1248cff3606a`；emit `count=4/capacity=256/hits=1/misses=4/evictions=0`，compiled `count=3/capacity=128/hits=0/misses=5/evictions=0`；Domain `used=3/limit=256/rejected=0/failedAfterReservation=1/probeTypeCount=1`。来源为规范项目 `Log/P0P1/eval-resources-delivery-capabilities.json`，统计是该采样时刻而非交付后的恒定值。

未覆盖：完整 EditMode、长时间性能/内存基准、真实不支持 Emit 的运行时和生产配额耗尽；不支持能力用内部注入值验证。Console 查询还包含非本轮选择用例的 Flow/window-probe 错误记录，未清空或宣称整个 Console 无错误。测试通过结论限于上述权威 runGuid 和关联编译。本节为测试完成后的文档追加，不属于报告的源码快照；未再修改代码。

## 2026-09-22 Eval/Emit-first 方案

本阶段不实现 DLL 动态加载。DLL 编译、依赖解析、版本入口切换、可信来源和卸载策略作为独立后期项目规划；当前实现不使用 Roslyn、CodeDom、外部 `csc/mcs`、`Assembly.Load` 或 Unity 脚本编译，也不承诺 IL2CPP Player。

目标是让 AI 在不退出 PlayMode、不触发 Domain Reload 的前提下完成常见的小范围诊断代码执行、运行时状态修改、现有 API 调用，以及显式动态扩展点的临时类型实现。它不提供对已编译游戏方法的任意替换；“替换”仅限调用方主动持有并更新的 delegate、接口实例、callback 或其他显式入口。

### 三个 Eval 后端

| 后端 | 实际机制 | 冷启动 | 重复调用 | 能力边界 |
| --- | --- | --- | --- | --- |
| `interpret` | 解析为 V2 AST 后逐节点解释 | 最低 | 最慢 | 完整 V2：异常、闭包、lambda、async/await、泛型、rank 1-4 数组 |
| `emit` | `DynamicMethod` 缓存入口调用已解析的 `CSharpProgram` | 中 | 比重复解析更省，但仍解释 AST | 与解释器 AST 相同；名称为兼容保留，不代表逐节点 IL lowering |
| `compiled` | AST 静态绑定并 lowering 为 `System.Linq.Expressions` delegate | 最高 | 支持范围内最快 | 同步有限子集；不支持则在执行前返回 `CSHARP_COMPILED_UNSUPPORTED_NODE`，绝不回退 |

`auto` 行为保持兼容：首次解释，后续可命中旧 emit-cache；不会自动选择 `compiled`。需要直接编译时必须显式传 `executionBackend=compiled`。不能在没有基准数据时给出固定倍数承诺；通常热循环中的 AST 分派差异最大，而含大量 Unity/用户方法调用的代码仍受主线程调度和反射调用成本主导。

`compiled` 当前支持：

- 字面量、typed/`var` locals、typed input variables、数值提升和转换。
- 算术、比较、布尔短路、条件表达式、`??`、`??=`、`typeof`、`nameof`、`default(T)`。
- local/input/member/index assignment 与前后缀增减，目标只求值一次。
- `if`、`while`、`for`、`break`、`continue`、`return` 和预算/取消检查。
- 编译期绑定的字段、属性、实例/静态方法、显式泛型方法、构造器、cast/as/is。
- 一维显式或隐式数组及 CLR 数组/默认 `Item` indexer。

直接编译边界：`try/catch/finally`、`throw`、`foreach`、lambda/closure、delegate 动态调用、`await`、泛型推断、可选/命名/`params`/`ref/out` 参数、多维数组，以及完整 C# 声明仍使用 `interpret`/`emit` 或明确拒绝。成员调用已在编译期选定目标，但运行时仍经 `CSharpEvaluationContext.Invoke` 调度，以保持 Unity 主线程、安全策略、预算和副作用证据。

### 只读预检

`csharp_validate` 是只读、幂等、无需写授权的预检工具：

- `backend=interpret`：词法和语法解析。
- `backend=emit`：解析完整 V2 Eval AST，并检查缓存的运行时能力；不套用动态类型方法体的同步限制。
- `backend=compiled`：解析、静态绑定、lowering 和 delegate 编译；`variableTypes` 为外部变量提供 CLR 类型，不读取实例值。

预检不会执行 getter、setter、构造器、目标方法或用户转换。实际 `csharp_eval`、`reflection_emit_type` 和 `execution_session` 仍是可写、非幂等执行工具。

### Reflection.Emit 方法体

`reflection_emit_type` 仍负责创建真实 CLR Type；这与 `csharp_eval(executionBackend=emit)` 的 AST 入口缓存不是一回事。类型或成员 spec 可设置 `bodyBackend=interpret|compiled`：

- 缺省 `interpret` 保持现有同步 V2 body 兼容性。
- `compiled` 在类型发布前完成静态绑定和 delegate 编译，不支持的节点使整个 Emit 请求失败，不保留半成品入口，也不回退解释器。
- 生成的 CLR wrapper 当前仍使用 `object[]` dispatcher 来统一 session、callback、异常和生命周期语义；body 内部可以直接编译，但 wrapper 边界仍有装箱与分派成本。
- 不公开 raw IL。Reflection.Emit 是 CLR 的类型/IL 生成基础设施，但它不是 C# 编译器：C# 词法、语法、类型推断、重载解析、控制流和 lowering 都必须由 UPilot 或 Roslyn 实现，CLR 不会自动把 C# 源码编译成等价方法。

### 目录与职责

```text
Editor/
  Execution/Core/
    CSharpSubsetEngine.cs       # V2 lexer/parser/AST、解释器和执行语义
    CSharpEmitBackend.cs        # 兼容 emit-cache：DynamicMethod -> CSharpProgram
    CSharpCompiledBackend.cs    # 同步有限子集静态绑定、Expression Tree lowering/cache
    ReflectionEmitEngine.cs     # 结构化 CLR type、bodyBackend、callback/session 分派
    ExecutionCore.cs            # 预算、类型解析、反射 binder 与共享执行契约
  Core/UPilotExecutionService.cs # Bridge DTO、validate/eval/session/emit 编排
  QuickDebug/UPilotQuickDebugWindow.cs # Editor 内手工调试入口
upilotserver~/
  src/upilot_mcp/domain/execution_service.py # 参数规范化与 Bridge 调度
  src/upilot_mcp/mcp_tools/execution_tools.py # MCP schema、只读/写入元数据
Tests/Editor/UPilotExecutionCoreTests.cs     # lexer/parser/backend/Emit 定向测试
skills/upilot-unity-mcp/references/execution-tools.md # AI 使用契约
```

后期 DLL 项目应另建 `Editor/Execution/DynamicAssemblies/` 边界，至少包含编译服务、引用白名单、版本入口注册表、程序集身份/哈希、诊断和 PlayMode 通道；不要把它塞入当前 Eval lexer 或 Reflection.Emit engine。第一版可只追加加载、用逻辑入口指向最新 revision，不承诺卸载；Mono/CLR 中已加载程序集和类型引用通常只能靠 Domain Reload/进程结束释放。

### 推荐优先级与结论

1. P0（本阶段完成）：严格词法/转义/数值解析、解析深度与 token 限制、条件访问和赋值求值一次、`??`/`??=`/type intrinsics、只读验证。
2. P0（本阶段完成）：显式 `compiled` 后端、无静默 fallback、常用同步成员/调用/构造/数组 lowering、Emit `bodyBackend=compiled`。
3. P1：按真实失败样本补 `compiled` 的 `try/catch/finally`、`foreach`、`params`/optional 和泛型推断；先基准再优化 typed wrapper。
4. P2：插值/逐字字符串、对象/集合 initializer 和有限 class-source 前端；只有 AI 请求频率证明价值时才实现。
5. 后期独立项目：DLL/source compilation channel、revision registry 和明确的项目集成入口。

结论：对 UPilot 的日常 AI 调试，完整 V2 解释器负责覆盖率，`compiled` 负责高频同步小程序，Reflection.Emit 负责“必须有真实 Type”的接口/回调适配，已经形成足够实用的三层能力。继续追求完整 C# 编译器的收益低于维护成本；超出边界的长期逻辑应回到项目正式代码，真正的热更/程序集加载则进入独立 DLL 方案。

## C# 子集 V2（2026-09-04）

当前 language profile 为 `upilot-csharp-subset-v2`。V2 在不引入 Roslyn、CodeDom、mcs 或 Unity Compilation/Eval API 的前提下增加：

- ordered typed/catch-all `try/catch/finally`、`throw expression` 和保留原异常的 `throw;`；执行契约、策略、预算和取消错误不可被用户 catch，取消/预算路径的 finally 使用 100ms、256 statements/iterations、32 calls/allocations 的独立清理预算。
- `ExecutionScope + VariableCell` 词法作用域；closure 按引用捕获，`for` 共享循环 cell，`foreach` 每次迭代创建独立 cell；支持 expression/block、typed、多参数及 async lambda。async lambda 仅能适配 `Task`/`Task<T>` delegate，不支持 async void。
- 实用级确定性泛型推断：直接参数、nullable、rank 1–4/jagged array、`params`、base/interface chain 和 typed lambda contextual conversion；不执行 lambda 推断返回类型，同分仍返回 `REFLECTION_BIND_AMBIGUOUS`。
- 显式/隐式、交错和 rank 1–4 多维数组，递归矩形 initializer、多索引读写、数值提升及 `maxArrayElements` 预算。
- session 持有 closure 根 scope、取消 token 和异步 operation lease；close 使逃逸 delegate 失效并协作取消，已开始项目方法仍等待真实结束。

`csharp_eval` 的 interpreter 与 emit-cache 接受全部 V2 AST。`reflection_emit_type` 使用同步 profile，只接受同步异常、泛型和数组节点，拒绝 lambda/closure/await/async。`unity_reflection_call(expression=...)` 保持 expression-only，但复用 V2 泛型和数组表达式能力。

状态：V2 已实现并通过 Unity 2022.3/Unity 6 定向验收。实现不依赖 Unity 官方 Eval/Pipeline、`UnityEditor.Compilation.AssemblyBuilder`、Roslyn、CodeDom 或 mcs。

## 目标

- 用清晰且互不重叠的工具边界覆盖已加载方法调用、临时代码执行和动态类型生成。
- Unity 2022.3 与 Unity 6 使用同一套 UPilot 自有执行内核，不按 Unity 版本选择编译器。
- 不把任意源码交给外部编译器，不产生磁盘程序集，不公开 raw IL。
- 所有执行都有写入授权、预算、结构化诊断、对象生命周期和 Domain Reload 失效语义。

## 最终工具边界

| 工具 | 职责 | 常用程度 | 兼容性 |
| --- | --- | --- | --- |
| `unity_reflection_call` | 调用一个已加载方法，或执行一条受限表达式 | 高 | 最高；沿用当前 Bridge 和已加载程序集 |
| `csharp_eval` | 执行 UPilot C# 子集的表达式或语句块 | 高 | 高；默认解释执行，仅依赖 BCL |
| `reflection_emit_type` | 从结构化 spec 创建临时动态类型并返回句柄 | 中低 | Editor/JIT 环境高；Emit 不可用时明确拒绝 |
| `execution_session` | 管理跨调用变量、对象/类型/delegate handle 与清理 | 中 | 高；纯托管 Domain 内注册表 |

`reflection_eval` 不属于最终工具集，不提供别名、tombstone 或代理兼容。内部 Bridge 命令 `reflection.eval` 只保留为私有兼容协议；公开 `unity_reflection_call(expression=...)` 已使用共享 parser/interpreter。

`csharp_eval`、`reflection_emit_type` 和 `execution_session` 不遵循现有 `unity_` 自动扫描前缀，均通过 MCP Tool 和 Registry descriptor 显式注册；没有重新引入非 `unity_` 函数自动导出分支。

## 共享运行时

三个工具共享以下基础设施，避免各自重复实现类型绑定和对象转换：

1. `ExecutionSessionService`
   - 创建短期或命名 session。
   - 保存变量、对象句柄、类型句柄、delegate 句柄和动态程序集上下文。
   - 支持 TTL、容量上限、显式释放和统一清理。
   - Domain Reload 后返回 `SESSION_EXPIRED_DOMAIN_RELOAD`，绝不把旧句柄错误绑定到新对象。

2. `TypedValueCodec`
   - 支持 null、primitive、enum、数组、字典、结构化 DTO、Unity 对象引用描述和 handle。
   - 返回 `value`、`valueType`、`valueJson`、`handle`、`serializationStatus`。
   - 大对象默认返回 handle 与摘要，不无界序列化。

3. `MethodBinder`
   - 支持精确签名、重载评分、泛型参数、named/optional/params、`ref/out`。
   - 歧义时返回候选签名和绑定原因，不猜测调用。
   - `unity_reflection_call`、`csharp_eval` 和 Emit method body 使用同一 binder。

4. `AwaitableAdapter`
   - 统一识别 `Task`、`Task<T>`、`ValueTask`、`ValueTask<T>` 和受支持的自定义 awaitable。
   - 返回完成、异常、取消和超时的结构化状态。
   - 不自动重放已经开始的调用。

5. `ExecutionBudget`
   - 限制 wall-clock、statement 数、loop iteration、递归深度、分配对象数和结果大小。
   - 解释器在每条语句和循环回边检查预算。
   - emitted delegate 与 Emit method body 执行同一份已验证 AST，并在入口、语句、循环回边和调用点复用预算检查。

## `csharp_eval`

### 建议请求

```json
{
  "code": "var sum = 0; for (var i = 0; i < 10; i++) sum += i; return sum;",
  "mode": "statements",
  "sessionId": "required-session",
  "variables": { "seed": 1 },
  "imports": ["System"],
  "executionBackend": "auto",
  "limits": {
    "timeoutMs": 3000,
    "maxStatements": 10000,
    "maxLoopIterations": 10000,
    "maxResultBytes": 1048576
  }
}
```

`mode` 支持 `expression`、`statements` 和 `auto`。`executionBackend` 支持 `interpret`、`emit`、`compiled` 和 `auto`；三者的当前语义与边界以本文顶部 2026-09-22 方案为准。

### V1 语言范围

- literals、成员/索引访问、调用、常见一元/二元/三元运算符和 cast/as/is。
- `var` 与显式类型局部变量、赋值、block、`if/else`、`for`、`foreach`、`while`、`break/continue`、`return`。
- typed/implicit、交错及 rank 1–4 数组、closed generic/nullable 类型、显式泛型调用与 practical-v2 推断、受支持对象构造、词法 closure、block/typed/async lambda 和 Task/ValueTask await。
- 支持 bounded `try/catch/finally`、throw/rethrow；异常保留原类型、message、有限 stack、source span 和 cleanup diagnostics。
- `await` 通过共享 `AwaitableAdapter` 实现，不能通过阻塞 Unity 主线程模拟。

当前子集不支持 namespace/type 声明、preprocessor、unsafe、pointer、P/Invoke、dynamic、任意 assembly load、文件/进程/网络 API、线程创建和反射绕过。需要动态类时使用 `reflection_emit_type`，不在 `csharp_eval` 中解析 `class` 源码。

### 返回契约

```json
{
  "status": "Succeeded",
  "languageProfile": "upilot-csharp-subset-v1",
  "backendUsed": "interpreter",
  "result": 45,
  "resultType": "System.Int32",
  "resultHandle": "",
  "sessionId": "...",
  "budget": {
    "statements": 34,
    "loopIterations": 10,
    "elapsedMs": 2
  },
  "diagnostics": []
}
```

解析、绑定和运行错误分别使用 `CSHARP_PARSE_ERROR`、`CSHARP_BIND_ERROR`、`CSHARP_RUNTIME_ERROR`；泛型推断失败使用 `CSHARP_BIND_GENERIC_INFERENCE_FAILED`，预算结束使用 `EXECUTION_BUDGET_EXCEEDED`，协作取消使用 `EXECUTION_CANCELLED`。诊断在 `error.detail` 中以真实对象返回 `stage/sourceSpan/diagnostics/candidates/sideEffectsMayHaveOccurred/nextAction`。`sourceSpanJson/diagnosticsJson/candidatesJson` 仅为迁移兼容字段，新客户端不应二次解析。取消/超时不回滚，也不允许跨后端重放。

事件 `+=` 必须传 persistent session；订阅 lease 保存 source/event/delegate 精确身份。显式 `-=` 会注销 lease，session close、TTL 与相关 PlayMode 失效按同一路径自动解绑。

## `reflection_emit_type`

### 建议请求

```json
{
  "sessionId": "required-session",
  "spec": {
    "typeName": "UPilot.Dynamic.HealthListener",
    "baseType": "System.Object",
    "interfaces": ["Example.IHealthListener"],
    "sealed": true,
    "fields": [
      { "name": "_callback", "type": "System.Action`1[System.Int32]", "visibility": "private" }
    ],
    "constructors": [
      { "parameters": ["System.Action`1[System.Int32]"], "body": "_callback = arg0;" }
    ],
    "methods": [
      {
        "name": "OnHealthChanged",
        "implements": "Example.IHealthListener.OnHealthChanged",
        "returnType": "System.Void",
        "parameters": ["System.Int32"],
        "body": "_callback(arg0);"
      }
    ]
  },
  "cachePolicy": "specHash",
  "createInstance": false
}
```

### 支持能力

- base type、interface、field、property、constructor、method、override 和 interface implementation。
- delegate/callback adapter，使动态实例可以被已有事件、接口或反射调用；callback policy 限制总调用、重入和诊断容量，并提供 isolate/propagate 异常语义。
- property 支持自动、custom getter/setter 和混合 accessor；自定义入口在 session close 后返回 `EMIT_CALLBACK_EXPIRED`。
- 返回 `typeHandle`，可选返回 `instanceHandle`；后续由 `unity_reflection_call` 的 handle 参数调用。
- spec canonicalization 与 SHA-256 缓存；同一 Domain 内相同 spec 只复用 CLR Type。callback registration、guard、诊断 ring 和 cleanup lease 按 session 与 emitted instance 独立创建，不复用首个 session 的状态。
- 返回生成的程序集名、完整类型名、实现成员、spec hash 和生命周期信息。

初期不支持 raw IL、自定义 attribute 任意构造、泛型类型定义、finalizer、unsafe、P/Invoke、程序集保存、独立卸载、动态 `MonoBehaviour`/`ScriptableObject` 资源持久化。动态程序集使用 `Run`；清理由 session 和 Domain Reload 完成。

### V1 Emit 后端边界

- `reflection_emit_type` 创建真实 CLR `Type`，字段、属性、构造器和方法入口由 `System.Reflection.Emit` 生成；受限方法体绑定到共享的已验证 AST 运行时。
- `csharp_eval(executionBackend=emit)` 生成并缓存 `DynamicMethod`，delegate 直接闭包绑定解析后的 `CSharpProgram`，不经过源码编译、程序集加载或整数 ID 查表。
- 为保持 interpreter/emit 行为一致，V1 不把每个 AST 节点单独降级为专用 IL 指令；更激进的逐节点 IL lowering 属于后续性能优化，不改变当前公开语言或安全契约。

### Emit 能力探测

- 启动时用 BCL API执行一次无副作用最小探针，记录 `supported`、runtime、API variant 和失败原因。
- 兼容适配器封装不同运行时的 `AssemblyBuilder` 创建方式，不引用 Unity 版本宏或 Unity 编译 API。
- `reflection_emit_type` 在不支持时返回 `REFLECTION_EMIT_UNAVAILABLE`，不得静默换成源码编译。
- `csharp_eval(executionBackend=auto)` 在 Emit 不可用时继续使用解释器；显式 `emit` 则返回能力错误。

## 安全与执行语义

- 三个执行工具都要求项目写入授权，注册为 non-idempotent，不允许自动重试。
- 默认 deny filesystem、process、network、environment mutation、threading、native interop 和 arbitrary assembly loading。
- 类型/成员允许策略在 binder 层统一执行，解释器和 Emit 后端不能各自绕过。
- 不使用 `Thread.Abort`；解释器采用协作预算，Emit 代码注入检查点。
- 所有 callback/delegate 必须有调用次数、重入深度和异常隔离限制。
- session close 应先解除事件订阅和 callback，再释放 handle；无法卸载的动态类型必须明确报告将在 Domain Reload 时释放。

## 兼容性策略

- 核心项目只引用目标 Unity 支持的最低 BCL API；运行时差异通过 capability probe 和小型 adapter 隔离。
- parser、AST、binder 与 interpreter 不引用 `UnityEngine` 或 `UnityEditor`。Unity 对象只通过外层 adapter 和 handle 进入执行内核。
- 不使用 Unity 6 官方 Eval/Pipeline，也不使用 Unity 2022.3 `AssemblyBuilder`，因此两代 Unity 共用同一行为契约。
- Reflection.Emit 只承诺 Unity Editor/JIT 环境；不承诺 IL2CPP/AOT Player。UPilot MCP 的执行入口不得把 Editor 探测结果外推为 Player 支持。

`unity_capabilities_get.execution` 以 additive 字段报告 `structuredErrorDetails`、`legacyJsonErrorDetails`、`callbackPolicySupported`、`callbackSessionIsolation`、`callbackExceptionModes` 和 `eventSubscriptionCleanup`，并继续报告 interpreter、Emit runtime/API variant 与预算。客户端应优先按 capability 判断，不从 Unity 版本号推测执行能力。

## 实施优先级

1. P0：完成 `reflection_eval` 外部硬移除，锁定 `unity_reflection_call` 唯一反射入口。
2. P0：实现 typed value/handle registry、统一 binder、awaitable adapter 和 execution budget。
3. P1：实现 `csharp_eval` parser/AST statement interpreter；这是使用频率最高、兼容性最好的新增能力。
4. P1：扩展 `unity_reflection_call` 使用 typed arguments、handle、泛型和 `ref/out`。
5. P1：实现 `reflection_emit_type` 高层 spec；重点服务接口实例、事件 listener 和 callback adapter。
6. P2：直接编译同步子集已经改用 Expression Tree lowering；后续仅在性能数据和真实失败样本证明价值时扩边界，不开发手写 raw IL 前端。

不建议先实现 Emit 再补 binder/session。Emit 生成的类型如果没有统一 handle、参数绑定和生命周期管理，无法稳定地被后续工具调用或被项目回调。

## 验收矩阵

- Unity 2022.3 与 Unity 6 使用完全相同的请求得到相同结果或同类结构化诊断。
- 静态扫描确认没有 Unity Eval/Compilation API、Roslyn、CodeDom 或 mcs 引用。
- 覆盖 overload/generic/named/optional/params/`ref/out`、Task/ValueTask 和异常传播。
- 覆盖 locals、branch、loop budget、return、lambda/delegate、await 和 session 变量复用。
- 覆盖 interface implementation、override、event callback、spec-hash cache 和实例句柄调用。
- 覆盖 Domain Reload 后 session/type/object/delegate handle 的确定性失效。
- 覆盖 Emit 不可用、显式 emit、auto fallback，以及不发生源码编译降级。
- 验证未知 `reflection_eval` 调用返回 `UNKNOWN_TOOL`，工具列表与 Registry 均不存在该名称。

## 2026-09-04 验收证据

- 真实 MCP：Registry v5、187 tools（原基线 184 + 3），`csharp_eval`、`reflection_emit_type`、`execution_session` 存在，`reflection_eval` 不存在。
- Unity 6000.6.0a2：安全编译 0 error/0 warning。`UPilotExecutionCoreTests` 共 24 项均通过；扩展首轮 runGuid `97b9102e-2592-41c4-9566-793b97436f06` 暴露 3 项问题，修复后分别以 `6cbf1b8d-2893-46cc-af76-2c84b3216e62`、`19434fd8-1cbe-4564-87c4-558b1f170e0c`、`7dbc1b5a-e2f3-44fe-b2d2-554fce76a290` 精确复跑通过；最终强化的事件/callback/cache 清理断言又以 `b6d5e56b-c176-46d4-a97d-315b3abead24`、`013cb16e-6e3f-47bf-afaa-e1042bcd7da9`、`5904fbe8-54bb-4b04-9d98-02b8cbc029cb` 通过，全部 cleanupSucceeded=true。
- Unity 2022.3.62f2：同一 24 项 fixture 为 24/24；`Tests~/ExecutionCompat2022/P1ExecutionResults24.xml` 16245 bytes、SHA-256 `44BE742357BE99474B9FACA0AB2DDD3CE104A908537B524D23615AA35ECFCE64`。最终新增的 3 个清理断言精确复跑 3/3；`P1ExecutionResults24Cleanup.xml` 4655 bytes、SHA-256 `230506B60D8DBEC41559D239E40278DD1748543068114F0F82E7E8635545D7A7`。
- 24 项覆盖显式/推断泛型、array/nullable 推断、约束失败与歧义、typed/jagged/非法数组、parse/bind/policy/runtime/budget/emit span、循环/调用前/await 取消、timeout 与禁止重放、实例/静态/lambda/TTL/PlayMode 事件清理、解绑失败继续清理、callback limit/isolate/propagate/reentrancy/concurrency/capacity/cache-hit session 隔离、自动/custom/mixed property accessor 及普通对象不被基础设施 Dispose。
- 验收中修复两项产品问题：cache-hit 类型现在为当前 session 重新解析 callback，并按 emitted instance 绑定独立 registration/guard/lease；`exceptionMode=propagate` 使用 `ExceptionDispatchInfo` 抛出解包后的原始异常，而不是重新抛出 `TargetInvocationException`。
- Python execution/reflection/Registry/Agent Rules 定向契约 31 passed；Skill 主包校验通过；`git diff --check` 通过；Execution Core 禁用引擎引用且静态扫描无 Unity/Roslyn/CodeDom/mcs/Compilation 引用。
- 真实组合调用：泛型推断返回 `Int32:String`，jagged array 返回 4，多行 bind span 覆盖完整 44 字符（1:1→3:2）；callback 返回 3 后因 limit 返回 0，session status 为 invocations=2/rejected=1 且包含 `CALLBACK_LIMIT_EXCEEDED`；custom property set 4/get 5，session close 无 cleanup error。
- AI 集成说明随后补齐：MCP schemas 为四个执行工具公开关键参数描述，移除“未来的 csharp_eval”过时措辞；Skill Install 23 增加结构化错误恢复、cache-hit session/instance 隔离和 callback 异常模式说明，Agent Rules 保持 25，Registry 保持 v5/187。
- AI 集成定向验收：Python execution/reflection/install/Agent Rules 契约 49 passed，最终 execution/reflection schema 复跑 16 passed；主 Skill 与规范项目 `.agents`/`.claude` 三份校验通过且安装哈希一致。Unity 6000.6.0a2 安全编译 0 error/0 warning，新增 capability fixture 1/1（runGuid `c1f3d35f-2de8-4043-9e2d-4daec589e004`）；Unity 2022.3.62f2 同一 fixture 1/1，XML `Tests~/ExecutionCompat2022/CapabilityContractResults.xml` 为 3443 bytes、SHA-256 `A682FE793EEE5C826F9C22EE68ACF32A1DFF9C1D178DF9B1B4439FC85A52A8CF`。真实 MCP 重启后 tools/list 仍为 187，四工具描述及参数 schema 已生效，capability 返回全部新增字段。
