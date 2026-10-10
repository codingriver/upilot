# N01 临时完整 C# 诊断模块

核对日期：2026-10-09（北京时间）。**当前 Windows/Unity6000.6.0a2 已完成真实 AssemblyBuilder v1/v2/v3 Build/run、同名绑定、旧订阅解绑、相同源码复用、异步忙拒绝及 module/session close。三份源码和 DLL 的实际哈希已独立核对一致。原15叶13/15及两次夹具失败保留，修正后两叶单独2/2通过；不重写原失败。剩余受控 source/reference 篡改现场、全Server加载鉴证及其他组合未覆盖；不承诺物理卸载、任意强停或Analyzer支持。见 P0P1方案第13节及精简核验 `Log/P0P1/n01-current-matrix-verification-20261009/verification.json`。**

## 1. 最小用途和不做的事情

需要保留已运行的 PlayMode 对象，同时临时运行一份完整普通 C# 诊断源码时，使用独立 `diagnostic_module` 工具。它不改变 `csharp_eval` / Emit 的语法和权限，不重新开放通用 Operation/Step，不建立项目系统、自动依赖图或热替换框架。

- 编译使用 **Unity 进程内的 `UnityEditor.Compilation.AssemblyBuilder`**；不用外部 C# 编译器，不安装新依赖。
- 一次一个源码文件，固定一个静态入口；代码、参数和结果分别最多 1 MiB。
- 引用只接受最多 32 个精确、已经加载的类型名，不接受外部 DLL 路径。
- 复用已经加载的 Mono.Cecil 元数据读取能力；不可用就拒绝，不额外装载依赖。配置项目/包 Analyzer/Generator 时明确拒绝；Unity 安装目录内且无链接重定向的现有工具链可保留，记录路径与 SHA256。不修改工程配置，不保证隔离 Unity 内置 Analyzer。
- 这是**可信代码执行，不是沙箱**。提交者必须自行保证代码不会操作他人的对象、Capture、文件或任务。
- 普通执行有副作用，不重放、不自动重试、不自动换后端，不承诺任意代码回滚或强制停止。
- 新 DLL 驻留到正常 Domain Reload。close 关闭入口及登记的资源，**不等于物理卸载 DLL**；也不迁移旧 static / private DTO / delegate。
- 不为回收配额自动 Reload、退出 PlayMode 或重启服务。

## 2. 入口和上下文

```csharp
public static string Run(DiagnosticModuleContext context, string argsJson)
// 或
public static Task<string> Run(DiagnosticModuleContext context, string argsJson)
```

入口必须在本次 `Assembly.Load` 返回的 Assembly 中精确、唯一绑定，不通过全局短名类型缓存找新旧同名类。允许普通完整 C# 源码，但不支持 unsafe，不承诺所有编译器扩展。

`DiagnosticModuleContext` 提供：

- `ProjectPath` / `DomainGeneration` / `PlaySessionId`：调用所属现场身份。
- `Cancellation`：协作取消令牌，任意代码可以不响应，不能因此声称已停止。
- `RegisterCleanup(Action)` / `Track(IDisposable)`：在 Unity 主线程登记自有资源收尾。
- `SubscribeEditorUpdate(Action)`：登记可解绑的 update 订阅。
- `IsClosed` / `CleanupErrors`：资源状态与有界错误。

每个 context 最多登记 64 个资源。替换 revision、close、owning session close、模式退出会关闭旧 context；只保证**实际登记**的订阅/资源可由此入口处理，不能自动找到任意用户静态订阅。服务硬停止只 fencing/解绑并协作取消，不调用任意用户 cleanup 来阻塞硬停止。

## 3. 精确身份与调用顺序

通过规范工程 `http://127.0.0.1:8011/mcp`（其他已授权项目必须另行明确选择其精确端点）：

1. 只读 `unity_mcp_status(forceFresh=true)`：确认路径、Unity PID/creation、Bridge/Server 生命周期、权威新鲜状态与相关入口加载。健康、版本字符串和磁盘文件较新不能替代加载证据。
2. `diagnostic_module(action="status")`：只观察可用根身份，不开 session、不编译、不运行。
3. `execution_session(action="open")`：保存自己创建的精确 `sessionId`。
4. `diagnostic_module(action="compile", ...)`：明确 `allowFullCSharp=true`，提供 source、entryType、moduleId 及根状态返回的 `expectedProjectPath`、`expectedDomainGeneration`、`expectedPlaySessionId`。
5. 若 compiler 仍运行，以**原 moduleId/session/身份**查询 status；不重发 compile。`compilationStatus=completed` 后，才显式 run；失败候选不覆盖旧有效 revision，也不自动调用旧 revision。
6. `diagnostic_module(action="run", argsJson="...")`：调用一次；保存 invocationId。异步返回后只观察原 invocationId，status 不再次执行入口。再次明确 run 是一个新的副作用请求，不是幂等重试。
7. 在同一生命周期内关闭原模块和 session；分别检查 cleanupErrors、activeAsyncOperations 与 asyncOperationsStillRunning，最后核验自己创建的对象/文件。

**两种 domain 不混用**：此 API 的 `expectedDomainGeneration` 是 execution-session 域身份字符串；心跳的整数 `domainGeneration` 用于外部身份核对，不代替该字符串。`expectedPlaySessionId` 也不是 Bridge sessionId。

修改动作都沿用已有项目 write gate。status 是只读观察；不能用根 status 的 `available` 推断动态编译能力已通过验收。

## 4. 回执、失败与预算

- `compilationStatus` 与运行 `status` 分开；候选失败时原有效 revision 仍可能存在。必须核对 compilationRevision/revision、compilerMessages、compilationError，不能仅见 `status=completed` 就运行旧版本。
- `sourceSha256`、`dllSha256`、引用路径/hash/MVID、assemblyName 说明本次实际来源。编译前核对已加载程序集与磁盘 MVID，加载候选前再核对源码和所有实际引用 hash。
- `observedUnityToolchainAnalyzers` 是当前程序集编译元数据中观察到的 Unity 安装工具链路径与 SHA256，不表示每一个都实际参与或执行过本次独立编译；不伪造 MVID 或装载证明。项目标签、外部路径、近似前缀、缺文件与链接重定向继续拒绝。编译前后工具链集合/hash 改变时不装载候选。
- 相同源码、入口、引用及观察到的工具链身份复用已绑定 revision，不重复编译、加载或 Run。已关闭 context 的复用会开新 context 并清除旧调用投影，不迁移旧 static 状态。
- 编译观察固定 60 秒，运行观察固定 30 秒；查询不续期。期限到达不等于物理编译器/用户代码已停止。迟到编译不装载候选 DLL，未知构建状态保留原 builder/lease，不能据异常再次编译。
- 在途编译/Task 不允许替换，返回 `MODULE_BUSY` 或编译忙诊断，观察原 ID；不强停、不换路线。
- 每服务最多 16 个逻辑模块；域内累计 32 次 DLL 加载尝试/64 MiB，失败绑定也可能已消耗。服务重启不回收域内 DLL 配额，物理卸载不支持。
- 用户首错与 cleanupErrors 分开保存。响应丢失意味着副作用未知，不能重新 create/run/compile 来取得回执。

源码/产物保存于项目 `.upilot/diagnostic-modules/<唯一程序集名>/`，不在 Assets 下触发普通导入。它们是独立模块编译产物；**不替代产品 C# 修改后的 WriteBatch 关联编译或 NUnit 验收**。

## 5. 开发验收范围（未执行不写通过）

直接回归代码：

- `Tests/Editor/UPilotDiagnosticModuleTests.cs`：入口绑定、同名类绑定当前 Assembly、签名拒绝、context 收尾、投影重置及预算边界等。
- `upilotserver~/tests/test_diagnostic_modules.py`：实际 MCP 模型/dispatcher 归一化、显式 opt-in、身份/有限 JSON、错误原样透传、不 fallback/重放。
- `upilotserver~/tests/test_diagnostic_module_evidence_client.py`：fake 传输但校验真实工具签名；前置拒绝、高位 wire ID、一次忙拒绝、不重发、域变化拒绝旧清理、首错和收尾分列。这不是物理编译或 update 事件解绑的现场证明。

真实现场脚本 `upilotserver~/scripts/p1_diagnostic_module_evidence.py --execute`：

- 默认端口 8011，要求规范工程**已经处于未暂停 PlayMode**，身份精确、新鲜且两 N01 Server 入口加载等价，完整队列为空、没有他人 Capture。
- 只创建自有 session/GameObject，完整保留 `ulong` wire ID。
- 同名 Entry/DTO v1、v2 在各自 Assembly 运行；以 x/y 非对称 update 检查旧订阅解除；相同源码复用不增加 revision/load/调用。
- 异步 v3 期间仅一次替换请求，核对 MODULE_BUSY，观察原 invocationId；核对原对象与 PID/creation/Server/Bridge/producer/domain 不变。
- 总客户端预算 200 秒（含保留 5 秒清理）；仅在新鲜同生命周期内关闭自有 session并核对原对象名称/ID后删除。未知创建 ID 不按名称猜测删除；收尾未知则最终 unknown。
- 原始回执（去秘密）与首错/cleanupError 写入独立 observation 文件，使用实际返回的路径/字节/hash。不切模式、不重启、不 Reload、不抢 Capture、不重复副作用请求。

该脚本不覆盖全部 Server 加载、真正 DLL 卸载、任意代码强停、引用篡改、Analyzer 支持及其他 Unity/平台组合。仅对真实通过的组合声明支持。N01 父项在矩阵与约定能力未验证前保持开放。


## 历史：2026-10-09 新增修复的关联编译与15叶回执（已由后续补记更新）

关联WriteBatch wb-00238ac4-efc3-405d-be16-a0bc5cd54dd5 / compile 2f18ee28712449bc99dc1baf90ae40f5已verified/passed，0错误/0警告。原新增定向runGuid e7a874da-2332-40c5-8808-fec6efb46683共15叶、13通过/2失败/0跳过，清理/身份/源码未变核验均通过；工具链分类10叶全部通过。两失败均为删除测试的additive场景创建被未保存untitled场景拒绝，尚未执行待测产品helper，拟仅修自有场景夹具前提后集中复验，不放宽断言或保存用户场景。本次仅观察原任务，不重发。原summary实际653886字节/hash已复算，详见P0P1方案11.19和Log/P0P1/n01-toolchain-fix-20261009T231234/acceptance-result-observation.json。实际Build/run/revision/解绑/close仍未通过，不能以10叶通过关闭N01。

## 当前真实矩阵与产物核验


- 两个删除夹具只改为自有 `NewPreviewScene/ClosePreviewScene`，保留原断言，不保存或丢弃用户场景。WriteBatch `wb-fd8edead-fadd-49df-9c69-6ba288b6d976` / compile `98a3db56ca24484588edee14e844ca9a` 关联 verified/passed，0 error/0 warning。Console 关联覆盖 partial/domain_reload_boundary，不写成完整无错。
- 原 15 叶运行仍为 13 通过/2 夹具失败；另两次失败尝试保留。冻结后仅复验受影响两叶，runGuid `331319c0-a0b9-4779-bbbb-02cc471ba344` 为 **2/2 passed**，cleanup/identity/sourceUnchanged 均通过。Preview scene 证明 valid Scene/PlayMode guard，不是已保存资产持久化验收；已通过的工具链10叶没有重跑。
- N01 自有 PlayMode 现场 **实际 AssemblyBuilder 构建并运行 v1/v2/v3 通过**：同名入口按各 Assembly 绑定；旧 update 解绑；相同源码复用；异步执行期间替换被 MODULE_BUSY 拒绝，继续观察原 invocation；原对象保持；module/session close 无 cleanupError，异步活动归零，自有对象精确删除。
- 58 次调用只执行一轮现场脚本；现场期间 Unity PID/creation、Server、Bridge、producer/domain 不变。仅证明本次 Windows / Unity6000.6.0a2 组合。所有 Server 模块加载、source/reference 篡改现场及其他组合未覆盖；物理 DLL 卸载、任意代码强停、Analyzer/Generator 支持不是本次承诺。
- 原报告实际 5,157,317 bytes / SHA256 `86ce72750e031d53b5d780953547b2bc96039948e75a60172978654b22800664`；两叶 summary 实际 602,002 bytes / SHA256 `b849de24d9e984461683390b3fa1ca9670939c4935b4148055b904461e084021`。再次独立读取三份源码及 DLL，六个实际 SHA256 全部与回执一致。精简核验见 `Log/P0P1/n01-current-matrix-verification-20261009/verification.json`，不复制5MB报告。
- 已恢复进入前 EditMode。最终只读核对精确规范工程，fresh/authoritative/ready；queue complete=true、items=[]；Capture activeCount=0。没有维护重启、重复编译、重复 Arm 或新增验收。

## 受控 source/reference 篡改验收（开发准备，尚未宣称通过）

`UPilotDiagnosticModuleTamperProbe.Run(nonce)` 仅支持 exact规范工程 stable unpaused PlayMode；只修改自有source/reference及自有session，不修改Unity/package引用。三场景分别要求 `MODULE_SOURCE_CHANGED`、`MODULE_REFERENCE_CHANGED`、`MODULE_REFERENCE_IDENTITY`，核对未安装revision/未增加resident DLL/未调用入口及恢复原引用。

现场客户端 `upilotserver~/scripts/p1_diagnostic_module_tamper_evidence.py --execute` 生成新nonce，一次Run后仅查询原reflection operation；独立重算8件文件及report.json。必须先完成本批开发、关联编译和冻结，再集中执行契约测试、5叶Unity guard和一次受控真实矩阵。客户端不切PlayMode，不能抢占其他Capture/Runner。

反射await有现行30秒上限；请求显式30000ms，客户端180秒及夹具120秒观察不能放大它。若await失败、DLL写锁或身份变化，保存原首错和原operation，不重发Run，也不声明编译器停止。reference Assembly会驻留到正常Reload，不承诺物理卸载。范围仍是当前Windows/Unity组合、局部真实module service及selected-entry来源，不证明全Server加载。

### 首次篡改现场与修正边界（2026-10-10）

首次probe因异步错误文本缺Code而失败，source拒绝已发生但不关闭矩阵；自有reference未变化，truncate式恢复仍报IOException。最小修正保留契约Code，自有overlay在加载前创建、修改/恢复只等长原位写入；OS拒绝仍是失败而非自动fallback。MVID仅唯一16字节替换，产品继续独立读取metadata验证。TaskGroup不再掩盖首个错误叶子。新关联编译、冻结及定向复验完成前不声明修复通过。


### 2026-10-10 MVID磁盘替换夹具的局部修正（开发已保存，尚未新现场复验）

新nonce `cffee5de98044f3988e622ae7430d11e` 只执行一次，原operation `reflection-op-646966ae-832f-492f-bb3b-5f34b3ad7450` 已终结。source/reference-content两个场景分别实际拒绝为MODULE_SOURCE_CHANGED/MODULE_REFERENCE_CHANGED，revision/resident计数未增加；MVID未在编译前被拒绝，因此整轮失败，原报告保留。原sessionClose记录仍有一个异步编译，不能由后来isCompiling=false改写为已通过收尾。

原位写入映射DLL的MVID可能同时改变加载态元数据，尚未证实是产品身份门禁缺陷。最小范围只改自有夹具：MVID场景原子替换自有磁盘DLL，并在请求前明确核对加载态MVID仍为原值；前提失败就不发起候选编译。内容hash篡改仍用已通过的等长overlay写入，不重设计产品引用缓存。替换失败不fallback、不重试，恢复也仅操作自有文件。追加一叶文件替换/既有candidate不覆盖回归，客户端必须先保留probeReport及业务首错再做最终身份预检，并直接测试该顺序与加载态MVID前提。

此前修正冻结的Python55/55、Unity新增3/3通过保留；原5guard及已通过Build/revision矩阵不重跑。本批代码/直接测试/说明保存并关联编译后再新冻结，之后才集中运行受影响Python、新增Unity一叶和一个全新nonce现场。两个失败nonce永不重Arm，N01父项及其完整矩阵仍开放。


### 2026-10-10 当前组合篡改矩阵通过（此前失败保持原状）

七文件冻结后Python57/57、新增Unity一叶1/1及单次新nonce现场通过；关联WriteBatch `wb-815e2a91-8822-42ea-a188-3c6e15a01f01`编译verified/passed，0 error/1既有warning。nonce `f73813e1bc7547e0809ebfab8e4cea5f`/operation `reflection-op-efcd1c86-fe14-4d75-a412-e21312b7b2f7`业务succeeded=true，三场景分别拒绝为MODULE_SOURCE_CHANGED、MODULE_REFERENCE_CHANGED、MODULE_REFERENCE_IDENTITY。前两场景revision/resident计数不增、未调用入口；第三场景已核对磁盘MVID改变但加载态仍为原值，在候选编译前拒绝。自有引用恢复、session收尾成功，cleanupErrors为空/异步编译已结束。

独立重算8件实际产物、report.json、冻结源码、JUnit与Unitysummary；证据`Log/P0P1/n01-tamper-development-20261010/acceptance-verification.json`（SHA256 `c1a2dc5d5fd7d2af4494d3916549ba8d2c127aa63acf292f84b2959c17f79fca`），详见P0P1方案第17节。两次历史失败保留、不重派发旧nonce，恢复原EditMode、queue complete/空、Capture0、Runner inactive。仅当前Windows/Unity6000.6.0a2组合；全Server加载及其他平台/Unity仍未覆盖，不承诺任意代码强停或物理DLL卸载。N01父项未关闭。
