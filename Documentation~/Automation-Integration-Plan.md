# UPilot Automation 公共支撑能力集成方案

状态：统一API及计划自有Capture/Snapshot正在收口；本次新增能力已开发，统一定向验收待完成。以下“计划自有证据”优先于历史章节中Operation独占Capture的说明；既有借用路径继续支持。

最后更新：2026-09-23
适用版本：UPilot 0.3.32，Unity 2022.3+

> 第 1～9 节的旧类型名、旧签名和旧验收只描述当时版本，不是当前 API 或当前验收证据。当前统一方案见第 10 节，不迁移业务 Case 本体，也不新增平行 MCP start/status 工具。

## 计划自有证据

- UPilot持有唯一注册目录、计划预检/推进/状态、检查点、Capture/Snapshot资源、
  Policy及报告终态。Skill维护固定组合和业务日志规则；宿主仅实现业务Step。
- 新内置`upilot.console_capture_start`必须第一Normal且唯一，空参数；启动后资源转交run，
  Step Cleanup不停止。Operation明确`consoleCapture.enabled=false`，Server与Unity均拒绝双重所有权。
- `AutomationRunCapture`私有原子文件保存token/身份/启动停止意图；只观察已建立身份，
  不重放Start/Stop。全部Finally之后10秒内确认停止及manifest/summary/原始分段的大小和哈希，
  再由ConsoleCollector固定区间分页（30秒）及Policy/Report收尾。
- `upilot.capture_snapshot`及`AutomationSnapshotEvidence`拥有意图、身份、轮询、
  默认3秒等待/2秒取消确认、恢复、可信像素与原文件验真。默认GameView1280x720；
  项目可在失败回调通过基类BeginSnapshotJson请求证据，不再实现观察器。
- 文件必须位于该run分配的目录，原始bytes/hash不可由新哈希替代；登记期间持有只读句柄。
  无法观察或证明释放时保留首错与RecoveryRequired，但仍执行可安全进行的Finally。
- 项目使用特性和IAutomationStep或AutomationStepBase，字符串参数、JSON返回、
  arguments最后，不依赖Context/Result/Error/Report DTO。Save/Artifact接口保持回调授权约束。
- 本次宿主收敛清单与部署/验收记录独立位于xclient2环境的UPilotStepExecutorMigration.md。
  历史报告与旧验收不批量改写，不作为新增生命周期已验收的证明。

### 本轮定向证据

2026-09-23规范工程140/140通过，覆盖注册、字符串JSON契约、执行器、
Console证据及新增Capture/Snapshot所有权，runGuid=`e9c0e23e-12a5-4ad2-8edc-79c746ac38a7`；
cleanupVerified/testIdentityVerified/sourceUnchanged=true。
报告`Tests~/UPilotTest/Log/UPilotAcceptance/1790138969069_req-0ece9c92-374d-41d8-a670-dc86d07d8d77/summary.json`，
1165244字节，SHA256=`549e20de2a515a38d4f5525664b76bb5fe8685b0abb0675015494431adbd01b3`。
Server预检Python32项通过；真实宿主默认组合12/12、17产物哈希一致，
包含可信截图、Capture停止、Finally日志、Policy及业务配置恢复。
规则41/Skill46在规范工程及授权宿主完成五目标同步、双安装校验和重复同步零写入。
这些是定向通过，不代替尚未执行的扩展故障矩阵或实际无包编译。

宿主后续以原生检查执行：入场单项10/10、资源/GM分别两轮19/19；
业务前置失败保留`KSB_GM_PRECONDITION_FAILED`并完成可信失败截图、
Finally恢复、计划Capture停止及EditMode，公开产物分别15/22/15且原始大小/哈希一致。
连续运行暴露的项目日志恢复定位符残留已在项目业务辅助中修正，
不把该配置协议下沉包内。详见环境迁移文档“退出恢复修正与连续运行验收”。
这些是外部项目接入证据，不新增或替代规范工程的包验收结论。

## 1. 目标与边界

本迭代只把可由任意 Unity Editor 自动化项目复用、且不理解业务语义的能力集成到现有 `io.github.codingriver.upilot` 包。KingShotBattle 的启动、PlayMode、登录界面等待、GM 入场、Case `Begin/Tick/Cleanup`、战斗证据时机、恢复和退出仍由项目桥负责。

UPilot 继续以既有 Operation 作为长任务权威，不增加第二套调度器、Provider 执行框架、Case Runner、工作流 DSL、自动重试/回滚、运行时监控框架或 `automation_start/status` MCP 工具。新增代码全部位于 Editor 程序集，命名空间为 `CodingRiver.UPilot.Automation`，不增加 UPM 依赖。

## 2. 必要能力清单

| 模块 | 公共职责 | 明确不负责 |
| --- | --- | --- |
| `AutomationCatalogV1` / `AutomationSelectionV1` | 校验纯数据 Case/Suite 目录；按 Suite 或显式 Case 列表解析选择；检查重复、未知、顺序、环和终止项 | 业务类型发现、实例化、工厂、关卡/英雄/兼容性、自动注册或排序 |
| `UPilotConsoleCaptureApiV1` | 为项目内编译桥公开 Capture 的能力探测、启动、状态、边界、异步分页读取和所有权保护停止 | 强制停止、凭据托管、接管他人会话、同步阻塞扫描 |
| `AutomationLogPolicyV1` | 对结构化 Console 记录按阶段/Case 区间和项目提供的规则分类，执行 allow/deny/budget 与证据完整性门禁 | 解析业务文本报告、定义业务白名单、推断未知上下文 |
| `AutomationReportWriterV1` | 生成可恢复校验的 `events.jsonl` 与不可改写的 `summary.json`，返回字节数和 SHA256 | 业务执行恢复、任务状态机、复制原始 Console 文件 |

## 3. 公共契约

### 3.1 Catalog 与 Selection

- ID 使用 `StringComparer.OrdinalIgnoreCase`。
- `AutomationCaseDescriptorV1` 只包含 ID、展示名、中性 `beforeCaseIds/afterCaseIds` 和 `mustBeLast`。
- `AutomationSuiteDescriptorV1` 只包含 ID 与有序 Case ID。
- 显式 `caseIds` 非 `null` 时优先于 Suite；空数组是错误，不回退到 Suite 或全量。
- 缺少选择、重复 ID、未知 ID、Suite 空列表、约束环、实际顺序违规和非末位终止 Case 都返回结构化诊断。
- before/after 只在本次选中集合中生效；分析不插入、不删除、不重排 Case。

### 3.2 Console Capture

- `Start` 与 `Stop` 只能在 Editor 主线程调用；`ReadAsync` 仅在主线程准备一致快照，磁盘扫描在线程池执行。
- Operation 创建的采集由 Operation 持有和停止；项目只读取 session ID。
- 本地菜单创建的采集必须自己生成并跨 Domain Reload 保存 owner token，停止时提交同一凭据。
- API 不公开 `forceStop`；凭据缺失或不匹配时拒绝停止，也不静默接管现存会话。
- 返回对象只包含现有 manifest 的 token 哈希，不回显原始凭据。

### 3.3 Log Policy

- 阶段与 Case 采用 `[fromSequenceInclusive, toSequenceExclusive)` 区间。
- 调用方必须在业务 teardown 后固定最终边界，并等待该边界前记录落盘后再完成分页分类。
- 丢失记录、分页不完整、读写失败、会话身份不匹配或等待超时均产生 `evidenceComplete=false`，不得通过。
- deny 规则优先；随后按声明顺序选择第一个 allow 规则。超过该规则 `maximumCount` 的记录阻断。
- 默认无 allow 规则；`Error/Exception/Assert` 阻断，普通 Log/Warning 仅在 deny 命中时阻断。
- 有 phase/case scope 的规则不能匹配未知上下文。
- fingerprint 只归一化空白、GUID 和堆栈源码行号，不归一化普通数字或业务 ID；分类始终基于原始记录。

### 3.4 Report

- `Create` 创建新 run；`OpenExisting` 只恢复报告写入，不恢复或重放业务执行。
- 打开现存 run 时校验 run identity、事件版本、连续 sequence 和每一条 JSONL；损坏显式失败，不截断。
- `Complete` 原子创建 `summary.json`。已有 summary 时只允许内容完全相同的幂等调用，不能改写 outcome、终止时间或正文。
- 输出目录必须位于当前 Unity 工程内。原始 Console Capture 以外部 artifact 引用记录，不复制文件。
- `GetArtifacts` 返回最终路径、bytes 和 SHA256；报告是证据，不是第二个任务状态机。

## 4. 包结构

```text
Editor/Automation/
  UPilot.Automation.asmref
  AutomationCatalogV1.cs
  UPilotConsoleCaptureApiV1.cs
  AutomationLogPolicyV1.cs
  AutomationReportWriterV1.cs
Tests/Editor/Automation/
  AutomationCatalogV1Tests.cs
  AutomationLogPolicyV1Tests.cs
  AutomationReportWriterV1Tests.cs
  UPilotConsoleCaptureApiV1Tests.cs
```

`UPilot.Automation.asmref` 指向既有 `UPilot.Editor`。测试继续进入既有 `UPilot.Editor.Tests`，不把 NUnit 依赖带入产品程序集。

## 5. 接入时序

```text
Operation/项目桥启动
  -> 获得或创建 Console Capture
  -> Catalog Validate + Selection Analyze
  -> 项目桥执行自己的业务生命周期与 Case loop
  -> 按 sequence 记录 phase/case 区间
  -> 失败证据与 Snapshot 收集
  -> 项目 teardown / restoration / cleanup
  -> 固定最终 Console 边界并完成分页读取
  -> Log Policy 分类
  -> Report Complete
  -> 项目桥发布业务终态
  -> Operation 停止其持有的 Capture
```

项目桥不得在证据、恢复、清理日志分类完成前发布 terminal，因为现有 Operation 观察到业务 terminal 后会停止它持有的 Capture。

## 6. 验收

- 默认工程：`D:\upilot\Tests~\UPilotTest`。
- Unity 2022 兼容工程：`D:\upilot\Tests~\UPilotTest2022`，只在需要兼容矩阵时运行。
- 仅运行新增 fixture 及直接回归；不默认运行全量 EditMode。
- Catalog：缺失/空/重复/未知选择、顺序、环、must-be-last。
- Log：deny 优先、allow 顺序、跨 Case budget、未知 scope、fingerprint、证据不完整。
- Capture：主线程门禁、分页、缓冲落盘、边界、错误凭据和不公开 force-stop。
- Report：run identity、损坏尾记录、冻结终态、幂等完成、工程路径门禁、artifact hash。
- 静态检查：产品程序集无业务命名空间、无 NUnit、无 Flow/Tracer 依赖、无 Case 执行循环。

## 7. 后续建议

- P1：完成本文件四个模块及所有权/证据完整性门禁。
- P2：只有出现实际 CI 消费者后再增加 JUnit 导出。
- 暂不开发：通用 Case Runner、自动发现、分组/重试/回滚、业务日志规则库、截图替代实现、第二套 Operation 或 runtime 监控框架。

项目侧延期修改见 `D:\MA\_AI_Docs\xclient\30_ENVIRONMENTS\xclient2\Automation\UPilotIntegrationMigration.md`。

## 8. 完成证据

- 产品代码位于 `Editor/Automation`，通过 asmref 进入既有 `UPilot.Editor`；未增加 package dependency。
- 定向测试位于 `Tests/Editor/Automation`，包含 Catalog/Selection、Capture facade、Log Policy、Report Writer、13KB Console 记录和两个自持执行循环的中性宿主。
- Unity 6 canonical：write batch `wb-fba37fed-b83a-4bb5-b30d-e8968de574ff` 关联编译为 `terminal/errorsVerified/correlationVerified=true`；runGuid `1ad22085-bebd-45c9-9d0e-257cc392f6b8`，21/21 Passed，cleanup 成功，结果权威。artifact：`D:\upilot\Tests~\UPilotTest\Log\UPilotAcceptance\1789986321893_req-d8e1ee52-a684-4081-8533-a7553e62de3a\summary.json`，492976 bytes，SHA256 `13d94ef227fe369a93e048cd9391b226eb7255956bd40e834ef3f26d2e6f0ea6`。
- Unity 2022.3：write batch `wb-2ca626bd-61f6-43bc-8f0f-e26372f8468e` 关联编译为 `terminal/errorsVerified/correlationVerified=true`；runGuid `37dfe123-7e30-422c-8c70-03f05d315ec2`，21/21 Passed，cleanup 成功，结果权威。artifact：`D:\upilot\Tests~\UPilotTest2022\Log\UPilotAcceptance\1789986404557_req-f78d6389-1b0f-4494-b817-edff5839ce37\summary.json`，350294 bytes，SHA256 `c96d6e51ab95df0f9cef6a9e12f761eeef6ea8024b1e79b48e82cd47391cb14f`。
- 两套验收均确认 `sourceUnchanged=true`；编译 warning 仅为既有 Flow Schema 的三条 UAC1009，不来自本模块。

## 9. Step 执行器 V1

### 9.1 分层与范围

Operation 保持外部任务身份、持久观察、取消请求和 Capture 所有权。Unity 主线程执行器只负责一份顺序 Step 列表；Skill/项目代码负责选择和组合业务步骤。项目不再重复实现推进循环。

必须实现 `IAutomationStepV1`，推荐继承 `AutomationStepBaseV1`；`[AutomationStep("稳定ID")]` 只负责注册，不替代类型契约。适配代码仍须 `#if UPILOT`，这不是“只引用特性就没有编译依赖”。既有 Case、正式运行时程序集不需要引用 UPilot。

不新增 asmdef、UPM 依赖、DI 容器、Provider 调度层、工作流 DSL、并发 DAG、自动重试或业务回滚。不把登录、GM、战场、项目日志白名单、业务快照判定放入包。

### 9.2 已实现脚本与职责

```text
Editor/Automation/
|-- AutomationStepAttribute.cs         ID/描述/参数示例/默认超时和轮询间隔
|-- IAutomationStepV1.cs               七个生命周期方法的强契约
|-- AutomationStepBaseV1.cs            默认状态、错误与 Succeed/Fail/Skip 辅助
|-- AutomationStepModelsV1.cs          计划/注册目录/结果/错误/运行记录 DTO
|-- AutomationStepContextV1.cs         身份、计划防御性副本、步骤/共享 JSON 检查点
|-- AutomationStepRegistry.cs          TypeCache 发现、类型/ID 校验、实例工厂
|-- AutomationStepPlanValidator.cs     严格字符串输入、全列表校验、Finally/预算/Policy
|-- AutomationStepExecutor.cs          唯一 Tick、Execute/Poll/Cleanup、失败和恢复
|-- AutomationStepRunStore.cs          原子持久化、计划哈希与 Editor 进程身份
|-- UPilotAutomationStepService.cs     公共 C# 门面与内部 Bridge 路由
|-- BuiltIn/
|   |-- EditorStepOperations.cs        复用场景安全服务及模式切换意图
|   |-- OpenSceneStep.cs               EditMode 打开并确认场景
|   |-- EnterPlayModeStep.cs           可选启动场景、等待实际 PlayMode/域恢复
|   |-- EnterEditModeStep.cs           退出完成后可选打开返回场景
|   `-- WaitSecondsStep.cs             非阻塞 Editor 时间等待，持久化截止时间
`-- Evidence/
    |-- AutomationEvidenceSessionV1.cs Capture 身份/所有权、边界和区间
    `-- AutomationConsoleCollectorV1.cs 固定范围落盘等待、异步分页与完整性
Tests/Editor/Automation/
|-- AutomationStepRegistryTests.cs     注册、非法契约、参数与全量预检
|-- AutomationStepExecutorTests.cs     顺序、状态、错误、超时、取消、Finally、恢复
|-- AutomationBuiltInStepTests.cs      等待、只读场景校验、脏场景/启动场景冲突
`-- AutomationEvidenceTests.cs         所有权、固定边界和跨页读取
upilotserver~/tests/
`-- test_operation_step_plan.py        Operation 接入、原样字符串、预检零启动
```

既有 `UPilotBridge` 注册服务；`UPilotCommandRouter` 定义内部路由权限及幂等性；`task_service.py` 接受 `jobSpec.stepPlan`，在预检成功后转换为既有 Operation 调用组；`task_tools.py` 更新现有工具描述。`AutomationReportWriterV1` 增加公共产物元数据入口，Catalog/Selection、Policy 和 Capture 继续复用。

### 9.3 契约与时序

| 方法 | 约束 |
| --- | --- |
| `Validate(context, string arguments)` | 无副作用；只检查语法/静态约束，不要求后续业务条件已经满足 |
| `Execute(context, string arguments)` | 启动一次并快速返回；禁止同步等待主线程完成异步工作 |
| `Poll(context)` | 快速返回 Running 或终态；由 Editor update 调用，与外部状态查询无关 |
| `GetError(context, string errorCode)` | 返回可读错误与诊断；异常不覆盖原始稳定错误码 |
| `Cancel(context)` | 协作停止；不是成功清理的证明 |
| `Cleanup(context)` | 可轮询，只有确认资源释放才成功；每个已执行步骤都调用 |
| `Restore(context)` | 显式恢复检查点；默认 Unsupported，执行器绝不重新 Execute |

每项参数只有一个 `string arguments`，省略为 `""`，数字、对象和 `null` 拒绝。Execute 接收原始字符串，不做 Operation 占位符替换。超时等框架字段仍为数字。注册类必须非抽象、非泛型、有公共无参构造；构造器及 Validate 必须无副作用，框架不能沙箱化任意用户 C#。

ID 按 Ordinal 区分大小写；特性不继承。一次返回注册冲突、缺失、契约、参数和结构诊断；非法注册会阻止预检通过。默认执行 30 秒、轮询 0.1 秒、清理 10 秒；轮询 0 表示每个 Editor update。Finally 必须是连续后缀，按声明顺序执行。

```text
全列表预检 -> 冻结计划/保存身份
  -> 保存 Execute 意图 -> Execute 一次 -> Poll
  -> 成功/告警/获准 Skip -> Cleanup -> 下一项
  -> 失败/取消/超时 -> 冻结首错 -> Cancel -> Cleanup
     -> 跳过余下 Normal -> 全部 Finally（后续错误作为 secondaryErrors）
  -> 最终 Console 边界 -> 等待落盘/分页 -> Policy -> 冻结报告
  -> 发布终态 -> Operation 停止其 Capture 并收集原始产物
```

`Skipped` 默认拒绝；允许后继续，但报告仍是 Skipped，整轮至少 SucceededWithWarnings，不作为 Passed Case。`TimedOut` 只能由执行器产生。清理失败不覆盖首错，Finally 继续；未释放资源最终保持 `RecoveryRequired`、`terminal=false` 并阻止新计划。

### 9.4 Operation 输入与发现

使用既有 `unity_operation_validate/start/status/wait/cancel/collect_artifacts`。`stepPlan` 与 `startCall/statusCall/cancelCall` 互斥，不新增公共工具。目录可通过编译门面 `UPilotAutomationStepService.CatalogJson()` 获取；内部 `automation.steps.catalog/validate/start/state/cancel/artifacts` 不是新的公共 MCP 工具。

```json
{
  "displayName": "Generic Editor step run",
  "consoleCapture": {"enabled": true},
  "cleanup": {"requireEditMode": true, "timeoutSec": 30},
  "stepPlan": {
    "version": 1,
    "steps": [
      {"instanceId": "play", "stepId": "upilot.enter_play_mode", "arguments": "Assets/Scenes/Launch.unity"},
      {"instanceId": "wait", "stepId": "upilot.wait_seconds", "arguments": "0.5"},
      {"instanceId": "edit", "stepId": "upilot.enter_edit_mode", "arguments": "", "phase": "Finally"}
    ]
  }
}
```

场景必须实际存在。显式启用 Capture 后默认阻断 Error/Exception/Assert；业务可以提供 `stepPlan.logPolicy`。未启用 Capture 时不声称 Console 已验收，报告不生成日志统计；非空 Policy 且没有 Capture 会在启动前被拒绝。执行器只借用 sessionId，不持有或记录 Operation 的 ownerToken。

Operation 总预算至少覆盖各步骤执行/清理上限之和加 60 秒证据与传输余量；显式过小预算拒绝。`SucceededWithWarnings` 对 Operation 是成功终态，原始步骤结果仍在 `domain` 和报告中保留。

### 9.5 内置步骤与恢复限制

- `upilot.open_scene`：非空 `Assets/.../*.unity`，仅 EditMode。
- `upilot.enter_play_mode`：空参使用当前场景；指定场景必须从 EditMode 开始，启动场景冲突拒绝，已经 PlayMode 时不隐式重启。
- `upilot.enter_edit_mode`：空参只退出；非空场景在实际退出后打开。
- `upilot.wait_seconds`：InvariantCulture 非负有限秒数字符串，使用可跨域恢复的截止时间，不阻塞 Editor。

场景脏状态默认 block，本版不隐式保存或丢弃。模式 setter 返回不算成功；等待实际状态与新域恢复。主线程同步阻塞无法被超时强制打断，需走既有 Hang 诊断。

`Library/UPilot/step-run.json` 保存计划哈希、游标、步骤类型身份、Execute 意图和检查点。同进程 Domain Reload 调用 Restore；Server 重连只观察。Editor 进程重启、注册类型变化、存储损坏、恢复不支持时拒绝重放并要求人工处置。不会删除记录来绕过资源恢复门禁，也不自动清理未知 Capture。

报告在 `Log/UPilotSteps/<runId>/events.jsonl` 和 `summary.json`；后者冻结，产物提供 bytes/SHA256。原始 Capture 引用附带固定 sequence 范围，最终原始文件哈希由拥有它的 Operation 收尾。

### 9.6 验证与延期接入

新增定向 fixture 覆盖两种实现方式、非法计划零执行、原样字符串、状态快照、成功/告警/跳过/失败、错误接口异常、取消、超时、Finally/清理异常、同进程恢复及重启拒绝。真实开场景/PlayMode/退出与 Domain Reload 单独通过 canonical 工程 Operation 验收，不把 EditMode 模拟恢复等同于真实切换。

本轮不修改 xclient2 实现或包清单。独立开发清单：`D:\MA\_AI_Docs\xclient\30_ENVIRONMENTS\xclient2\Automation\UPilotStepExecutorMigration.md`。Unity 2022.3 新 Step 矩阵不在本轮默认验收范围，不能沿用第一阶段结果声称已验证。

### 9.7 2026-09-22 交付审计

状态：包代码、文档、Skill 已实现，规范工程定向验收及公开 `stepPlan` 端到端验收通过。用户授权处置旧批次后完成最后验收；历史阻塞与原始未知结果保留如下，不将人工处置记为旧编译成功。

| 要求 | 当前证据 |
| --- | --- |
| 接口强制、基类可选、特性不继承、类型/重复 ID 检查 | Registry/Executor fixture，直接接口与基类两条路径 |
| 字符串原样传递、全列表预检零执行 | Registry fixture 与 `test_operation_step_plan.py`，含 null/数字/对象拒绝和占位符文本保持 |
| 超时、首错、GetError 异常、取消、Cleanup、Finally | Executor fixture，清理失败保留 RecoveryRequired |
| 检查点、同进程 Restore、不重放、Editor 重启拒绝 | Executor fixture；真实切换证据见下 |
| 内置等待、场景安全、模式切换 | BuiltIn fixture + 四步真实 Operation；等待使用 Editor 时间 |
| Capture 所有权、固定边界/落盘/跨页完整性 | Evidence fixture 的 1100 条记录；真实运行最终区间 `[0,13)` |
| Policy 不可无 Capture 绕过、报告冻结及产物格式 | Executor/Python/ReportWriter fixture；实际 collect_artifacts 校验成功 |
| 沿用 Operation，不增公共工具/依赖/asmdef | `task_service.py` / `task_tools.py` 与现有 Editor 程序集；产品 Step/Evidence 无业务类型引用 |
| 项目延期清单 | 独立 `UPilotStepExecutorMigration.md` 及索引已保存；本轮未修改项目实现 |
| Skill 完整维护 | Agent Rules 38 / Skill Pack 40；源生成/检查、双安装校验、五目标同步和重复无写入检查通过 |
| 公开 jobSpec.stepPlan 的运行中 Server 实测 | 完整计划预检、非字符串拒绝、单次启动、真实模式切换/域恢复、Finally、Capture 与产物收尾均已通过；见 9.8 |

**关联编译**

- canonical `D:\upilot\Tests~\UPilotTest`，Unity `6000.6.0a2`。
- writeBatchId `wb-44f1fa52-aa4e-465b-b783-b8fe1de9e1e1`，createdAt `1790063263772`。
- compileOperationId `cf66ee97f5b9467ea5cd2903053ded99`，verifiedAt `1790063280637`。
- `terminal/errorsVerified/correlationVerified=true`，0 Error；3 条既有 Flow UAC1009 Warning。
- 结构化证据：`Tests~/UPilotTest/Log/P0P1/step-compile12.json`。

**定向测试**

- Unity 五个直接相关 fixture，43/43 Passed，0 Failed/Skipped。
- taskId `task-15070164-8add-48fa-b4a1-483d56c38f58`，runGuid `7bd3a060-5c49-4ff5-874d-64b4f93efa8f`。
- acceptancePassed、cleanupVerified、testIdentityVerified、sourceUnchanged 均为 true。
- `Tests~/UPilotTest/Log/UPilotAcceptance/1790063371214_req-c4c8c4c7-70e8-430f-8cf7-b6b5db48bae7/summary.json`，570269 bytes，SHA256 `faf9d175529be7e5da0866510ff58b05ccbe3d321011c39d2091ddf21c1af38c`。
- Python：`test_operation_step_plan.py`、`test_operation_cleanup_barriers.py`、`test_persistent_operations.py`、`test_operation_runner_and_agent_rules.py`，73 Passed；2 条既有 websockets 弃用 Warning。
- Console 边界：按上述 runGuid 读取的完整测试区间为 0 Error；验收后的全局查询另看到一条 `UPilotMcpServerManager Status refresh failed: Thread was being aborted`，不宣称全局 Console 为零，也不归因为 Step 失败。范围查询证据 `Log/P0P1/step-final-test-console.json`，诊断降噪建议另记 P2。
- 未默认运行全量 EditMode、Unity 2022.3 新矩阵或 xclient2 业务用例。

**真实运行**

- 为验证新 Unity Bridge 路由，使用现有 Operation 的调用组提交已只读验证的四步计划；不是旧 start 重放，也不是生产侧 stepPlan 的替代接口。
- Operation `op-bc587727-d676-429c-bd75-d5128114eb1c`，Step run `1716040860a943769cc5a30847b8d671`。
- open_scene / enter_play_mode / wait_seconds / Finally enter_edit_mode 全部 Succeeded；`startAttemptCount=1`，真实域重载后继续原 run。
- Operation `terminal/businessTerminal/cleanupTerminal/editorTerminal=true`、`editorVerification=verified`，最终 EditMode、无待释放资源。
- Capture `console_20260922_155119_146_f69023a3` 由 Operation 停止，droppedCount=0；最终活动 Capture 为 0。
- `unity_operation_collect_artifacts` 返回命名 `events/summary` 文件，`artifactErrors=[]`，bytes/sha256 与报告声明匹配。
- 最终 Step summary：`Log/UPilotSteps/1716040860a943769cc5a30847b8d671/summary.json`，1727 bytes，SHA256 `e94a9bcd9a695e613e08d19d33cdcd0fd231ea43af5ab038340238ea301f141f`。
- 证据：`Log/P0P1/step-live3-wait.json`、`step-live3-artifacts.json`、`step-final-captures.json`。

**Skill 同步**

- 源目录 `D:\upilot\skills\upilot-unity-mcp`；只同步 canonical 工程，未同步外部 xclient2 或 2022 工程。
- templateSha256 `b67cd829fb1e629239ede991c312bbd51887721758c22ff5c9c8471c59158b9c`；双 Skill contentSha256 `106873bf572bdbf9d1ca312f9f72269bbdcb0f283e61f60983d485b5eebe4026`。
- 三份规则托管块外字节哈希不变。重复完整 apply 后 46 个受管文件的集合、SHA256、纳秒 mtime 均不变，备份目录集合未增加；最终五目标 current。
- 证据：`Log/P0P1/step-skill-preview.json`、`step-skill-sync.json`、`step-skill-repeat.json`、`step-skill-handoff.json`。

**历史阻塞与失败记录（后续处置见 9.8）**

- 旧 Server PID `69860` 未加载新分支的证据保留于 `Log/P0P1/step-public-deployment-final.json`。随后服务由外部刷新为 PID `108348`，Unity PID 仍为 `57636`；本任务没有主动重启 Server 或 Unity。新进程已通过公开 `unity_operation_validate(stepPlan)`：完整四步计划 valid=true，数字 arguments 返回 `STEP_FIELD_TYPE_INVALID`。证据 `step-public-full-validate.json` / `step-public-invalid-string.json`。这证明对应公开验证分支已加载，但不等于完整 Server 源码身份认证。
- 新 Server 查询原 Operation `op-bc587727-d676-429c-bd75-d5128114eb1c` 返回 `recovered=true/Succeeded/terminal=true/startAttemptCount=1`，证明恢复观察没有重放 start；证据 `step-public-recovered-observer.json`。
- 当前阻塞改为本任务之前的执行引擎批次 `wb-c8108d7c-3222-49a4-9d90-868a8c72cf55`。其原 compile operation `432cf89ba3534c2fbc2c9b0bea41996a` 缺失可关联终态，持久状态为 `recovery_required/terminal=false/outcome=unknown`；`unity_ensure_ready` 明确返回 `WriteBatchRecoveryRequired`。按原请求 `req-e309aaba-1467-4ad2-919c-221354262589` 查询 `unity_compile_errors` 返回 `COMPILE_IDENTITY_MISMATCH`，不能用较新 Step 批次的成功替代历史证据。证据 `step-public-pending-batch.json`、`step-public-ready.json`、`step-public-original-compile.json`。
- 最新 Step 批次 `wb-44f1fa52-aa4e-465b-b783-b8fe1de9e1e1` 在新 Server 下仍为 verified/passed，见 `step-public-latest-batch.json`。未登记替代批次、触发重复编译、修改持久数据库或绕过 readiness。需原批次负责人通过受支持恢复流程处置该批次，再运行公开 stepPlan 的 start/wait/artifacts 验收；整体验收尚未完成。
- 本次只读预检前后 `Library/UPilot/step-run.json` SHA256 均为 `6fac78897729c697d71f92fe673916e819b4995ef07da8b82e6e887910601c36`，活动 Capture 均为零；未创建新运行。后查证据 `step-public-captures-after-validation.json`。
- 初次真实运行因泛型 object 响应被 JsonUtility 擦除字段而留下 `op-e340ea72-d14e-45a2-a997-a8fc2f47ecb0` 的历史 RecoveryRequired。底层运行已成功退出且未创建 Capture；错误已改为具体 DTO 响应，但没有擅自清除旧 Operation 或重放 start。
- 场景夹具的未命名场景/空 setup 恢复问题已修复；明确属于该失败夹具的临时场景经 `unity_asset_get_info/delete` 清理，后续验收 sourceUnchanged=true。一次 Unity 工具链启动失败保留原证据，后续新代码批次正常编译；未改动无关程序集。
- 维护清单与显式恢复处置建议已在 `D:\upilot\TODO_UPilot.mcd` 更新既有 P2 条目，并与 `F:\xclient2\TODO_UPilot.mcd` 双向关联。

### 9.8 2026-09-22 授权清理与最终验收

用户明确授权“强制清除任务和状态，然后继续当前任务”。先检查规范工程身份、Test Runner inactive、活动 Capture=0，确认没有批次专用强制处置工具后，仅处置已核对的旧批次，不清空数据库、不重启 Server/Unity、不重放旧任务、不修改编译结果。

- SQLite 一致性备份：`D:\upilot\Tests~\UPilotTest\Library\UPilot\MaintenanceBackups\step-authorized-clear-1790065425208957900.sqlite3`，52523008 bytes，SHA256 `0a34e8513ffa3cd669328cc8c7ca74319f6248e813db279caf40ab20fc636ebe`；integrity_check=ok。备份含运行状态，仅在本机保留。
- 在事务中精确核对项目、batchId、原 requestId、recovery_required 和缺失 terminal snapshot，只更新 1 行为 canceled，写入人工授权与备份位置。旧编译仍为 outcome=unknown、errorsVerified=false、correlationVerified=false；没有伪造成功证据。服务下一权威状态自动解除门禁，ready=true/authoritative=true/isStale=false，无需重启或编译。证据 `step-authorized-cleared-batch.json`、`step-authorized-ready.json`。
- 通过公开 `jobSpec.stepPlan`，依次执行已有 `unity_operation_validate/start/wait/collect_artifacts`；没有传手写调用组。Operation `op-cd901715-13d0-4159-940b-af61199f7dd7`，Step run `b8b20e2ede81454fb4e00e9d8a4a7b79`，startAttemptCount=1。
- open_scene、enter_play_mode、wait_seconds、Finally enter_edit_mode 四项均 Succeeded。真实 Domain Reload 后保持原身份；最终 businessTerminal/cleanupTerminal/editorTerminal/terminal=true、editorVerification=verified、cleanupPending=false、unresolvedResources=[]，回到权威 EditMode。
- Capture `console_20260922_162442_329_12414786` 由 Operation 停止，droppedCount=0；报告固定区间 `[0,13)`、evidenceComplete=true、blockedCount=0、lostRecordCount=0。最终活动 Capture=0，artifactErrors=[]。
- Step summary：`Log/UPilotSteps/b8b20e2ede81454fb4e00e9d8a4a7b79/summary.json`，1705 bytes，SHA256 `7f13cad09cae8cb3ec149dbdfe2521dc24258f37af27fe63a84e15f2b02abd4c`。events 1758 bytes，SHA256 `3fa73a6ef37e418b58ea39207f6df7648131caedf408d4ee1258ebab1bc076b1`。
- 证据均位于 `Tests~/UPilotTest/Log/P0P1/`：`step-public-final-validate.json`、`step-public-final-start.json`、`step-public-final-wait.json`、`step-public-final-artifacts.json`、`step-public-final-captures.json`。
- 最终源码身份与 43/43 验收时完全一致：839 files，sourceSha256 `b0b9ba9cfffec7f57d643cf1dba9e4d07d88f6d856d70e872ac3cead470d326f`；不因纯文档和维护状态更新重复编译。五个 Skill 集成目标复查仍 current，见 `step-public-final-integrations.json`。

本方案实现与定向验收已完成；xclient2 接入仍按独立迁移文档延期，未修改其实现或包清单。通用“原编译证据归档/显式恢复处置”P1 与维护清单 P2 仍属于后续改进，本次单次人工维护不等于这些产品能力已实现。全量测试、Unity 2022.3 Step 新矩阵和 xclient2 业务验收不在本次通过声明内。

## 10. Automation 统一 API 与字符串 Step 契约

本节替代历史版本命名和业务 Context 契约；保留历史记录，不建设旧名称兼容层。

### 10.1 归属与命名

UPilot 持有自动发现的 Step 类型/工厂目录、完整预检、唯一执行器、运行状态、检查点、通用 Evidence 与报告收尾。Skill 组合已批准的固定模板和所选 Case；xclient2 实现业务 Step、Case 适配、断言、配置恢复和异常现场证据。新流程不必经过项目 Bridge；旧 Bridge 仅作兼容输入/输出转换。

Automation 范围内类、接口、枚举、结构、方法、脚本与测试移除 V1/V2 后缀；例如 `AutomationCatalog`、`AutomationSelection`、`UPilotConsoleCaptureApi`、`AutomationLogPolicy`、`AutomationReportWriter`。JSON `version/apiVersion`、持久化字段、枚举数值和历史证据不改；脚本重命名保留 `.meta` GUID。无关执行引擎、第三方 API 不在改名范围。

普通 Step 只引用 `AutomationStepAttribute` 加 `IAutomationStep` 或 `AutomationStepBase`。`#if UPILOT` 仍必要；特性也是编译依赖，不能声称零包引用。正式运行时零依赖，不新增 asmdef、依赖包、Provider 或第二套 MCP 工具。

### 10.2 精确执行契约

```csharp
public interface IAutomationStep
{
    string Validate(string runId, string instanceId, string contextJson, string arguments);
    void Execute(string runId, string instanceId, string contextJson, string arguments);
    string Poll(string runId, string instanceId, string contextJson, string arguments);
    string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments);
    void Cancel(string runId, string instanceId, string contextJson, string arguments);
    string Cleanup(string runId, string instanceId, string contextJson, string arguments);
    string Restore(string runId, string instanceId, string contextJson, string arguments);
}
```

- 每次执行一个新 runId；计划项 instanceId 唯一，同 stepId 可重复。arguments 最后且原样传递，省略为 `""`，显式 null/非字符串拒绝。
- Validate 的 runId 为 `""`，contextJson 含完整只读 plan；各项看到同一计划快照，不要求后续业务对象已经存在。启动再次校验，校验与执行实例分离。
- 基类仅 Execute 抽象；默认 Validate 通过、Poll Running、Cancel 无操作、Cleanup 成功、Restore Unsupported。资源拥有者覆盖清理/恢复。辅助方法为 `Succeed(warnings=false)`、`Fail(code,message,diagnostic="")`、`Skip()`、`IsRunning`、`ValidationOk()`、`ValidationError(code,message)`、`ResultJson(status,errorCode="")`。
- 特性不继承，具体注册类必须非抽象、非泛型、公共无参构造并实现接口。目录每个程序集代际建立一次，Catalog/Validate/Start 共用，不复用可变实例。

| 回调 | JSON 返回 |
| --- | --- |
| Validate | `{"ok":true}`；失败 `{"ok":false,"diagnostics":[{"code":"KSB_ARGUMENT_INVALID","message":"参数说明"}]}` |
| Poll | `{"status":"Running"}`；成功 Succeeded/SucceededWithWarnings；Skipped 需显式许可；Failed/Canceled 必须附非空 errorCode |
| GetError | `{"message":"可读说明","diagnostic":"可选诊断"}`，不得改写冻结错误码 |
| Cleanup | 与 Poll 格式相同，仅 Running/Succeeded/SucceededWithWarnings/Failed |
| Restore | Restored/Unsupported，或 Failed 加 errorCode；Restored 继续原执行或清理阶段，不等于步骤成功 |

错误 severity 缺省 error，ok 与诊断必须一致；空返回、非法 JSON、重复键、尾随第二个值、字段类型错误、缺少字段、未知状态均失败。TimedOut 仅由执行器产生，获准 Skipped 不计通过。

运行期 contextJson 仅含 `checkpoint` 和 `shared` 两个对象，每次回调取最新快照。不存在公开业务 Context 类。受控保存：

```csharp
void SaveCheckpoint(string runId, string instanceId, string checkpointJson);
void SaveSharedValue(string runId, string instanceId, string key, string valueJson);
```

服务公开以上 static 方法，基类提供同签名转发。checkpoint 必须为 JSON 对象；共享值可以是任意合法 JSON，按 `ksb.logging` 等命名空间键替换，保留其它键。只有当前 run/item 的可写生命周期主线程回调可以保存，Validate/GetError/逃逸异步回调禁止写。持久化成功才返回，原配置必须先保存再修改；本地快照和返回 JSON 不自动写回。

### 10.3 门面与执行顺序

公开 C# 门面均返回 JSON 字符串：
`CatalogJson()`、`ValidateJson(planJson)`、`StartJson(operationId,captureSessionId,planJson)`、
`StateJson(runId)`、`CancelJson(runId)`、`ArtifactsJson(runId)`。内部 Bridge 发送结构化结果，避免双重编码。

```text
Skill -> UPilot CatalogJson -> 组合 stepPlan
      -> unity_operation_validate -> UPilot Registry + 每项 Validate
      -> unity_operation_start    -> 再预检 -> Capture -> 冻结计划/保存启动意图
                                   -> Execute -> Poll -> Cleanup -> 下一项
                                   -> 首错冻结 -> GetError -> Cancel/Cleanup
                                   -> Finally 连续后缀
                                   -> 最终 Console 区间/分页/Policy/报告
      <- Operation 终态/产物       <- 停止 Operation 自有 Capture
```

省略元数据使用执行30秒/轮询0.1秒/Cleanup10秒，轮询0为每次 Editor update。状态查询只读，不推进步骤。缺失、重复、类型、参数和计划错误集中返回，预检不执行、不创建 Capture。

同工程一次一个计划，原子存入 `Library/UPilot/step-run.json`。Execute 前保存意图且只调用一次；同进程域恢复调用 Restore，不重放；Server 重连仅恢复观察，Editor 重启不自动续跑。清理失败不覆盖首错，尽力完成 Finally 后保持 RecoveryRequired；若持久化本身失败，立即阻止继续副作用/推进，不能承诺此时还能安全运行 Finally。主线程阻塞不能被超时强行中断。

内置 `upilot.open_scene/enter_play_mode/enter_edit_mode/wait_seconds` 的场景、安全和模式语义沿用 9.5：Assets 场景路径、未保存场景默认阻止、不覆盖冲突启动场景、真实模式完成及域恢复后才成功。

### 10.4 当前脚本职责

```text
Editor/Automation/
|-- AutomationStepAttribute.cs / IAutomationStep.cs / AutomationStepBase.cs
|   注册元数据 / 七个字符串生命周期 / 默认行为与 JSON 辅助
|-- AutomationStepModels.cs / AutomationStepJsonCodec.cs
|   框架模型与持久化数据 / 严格 JSON 检验与生成；项目 Step 无需引用这些模型
|-- AutomationStepContextJson.cs        每回调构造 checkpoint/shared/可选 plan 快照
|-- AutomationStepRegistry.cs           自动发现、契约/重复 ID 检查与实例工厂
|-- AutomationStepPlanValidator.cs      全量静态预检、委托 Validate、Finally 与预算检查
|-- AutomationStepExecutor.cs           唯一推进、超时、取消、清理、首错与恢复
|-- AutomationStepRunStore.cs           原子保存、计划哈希及 Editor 进程身份校验
|-- UPilotAutomationStepService.cs      JSON 门面、主线程保存权限、内部 Bridge
|-- BuiltIn/EditorStepOperations.cs     共享场景安全与模式意图
|-- BuiltIn/OpenSceneStep.cs            EditMode 打开并确认场景
|-- BuiltIn/EnterPlayModeStep.cs         可选场景、进入模式、持久化与恢复
|-- BuiltIn/EnterEditModeStep.cs         退出后按需打开返回场景
|-- BuiltIn/WaitSecondsStep.cs          非阻塞时间等待和截止检查点
|-- Evidence/AutomationEvidenceSession.cs  Capture 借用/所有权与固定区间
|-- Evidence/AutomationConsoleCollector.cs 落盘等待、异步分页与完整性
`-- AutomationCatalog.cs / AutomationLogPolicy.cs / AutomationReportWriter.cs /
    UPilotConsoleCaptureApi.cs          既有支撑能力统一命名
```

### 10.5 项目配套与本轮验收

xclient2 仅四个文件做必要类型/方法/名称字符串改名：SmokeCaseRegistry、AutomationEvidence、McpSmokeReport、AutomationBridge。保留序列化字段 `automationCatalogV1`，不是旧类型别名。没有改变 Runner 行为，没有业务 Step 被自动注册；五个业务适配脚本与旧 Runner 替换作为独立后续迭代，详见 `D:\MA\_AI_Docs\xclient\30_ENVIRONMENTS\xclient2\Automation\UPilotStepExecutorMigration.md`。

- canonical 最新批次 `wb-9c458cfd-f12d-44de-bea9-52cf9f9982a8`，编译 `32a580a7e3d7405d8d350380e518dba9`，verifiedAt `1790071727956`，关联通过、0 Error。
- xclient2 最新批次 `wb-9438f09b-8972-4ec0-87e5-f5c8d5aa9424`，编译 `4cd5f758b8794cd3affe2811b57acc76`，verifiedAt `1790071743396`，关联通过、0 Error，21条既有 Warning。
- canonical 十个直接相关 Automation fixture：107/107 Passed，runGuid `f766cc96-6cec-4340-88ae-d428342fc722`，task `task-cf8d88dd-aef5-4215-be4d-762c71b9341a`。cleanupVerified/testIdentityVerified/sourceUnchanged/acceptancePassed 均为 true。
- 验收产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1790072097601_req-0fdb1cf7-7191-45f6-a45a-109c954f7ed0/summary.json`，1045997 bytes，SHA256 `5c66c1c00196b72011efe82d7bcd131144d147864841b6e4b600f81ad1e7185a`。
- 首轮99/101的失败证据保留：尾随 JSON 根值及 null 写回问题已修复并增加回归；未掩盖失败或弱化断言。
- Python 定向回归73项通过，覆盖 `test_operation_step_plan.py`、`test_operation_cleanup_barriers.py`、`test_persistent_operations.py`、`test_operation_runner_and_agent_rules.py`；保留两条既有 websockets 弃用警告。
- 静态检查：包 C# 与当前 README/CHANGELOG/Skill 未发现旧 Automation 版本后缀标识符；四个项目配套文件及间接调用 `KingShotBattleTest.cs` 在 `UNITY_EDITOR=true/UPILOT=false` 分支无 UPilot 类型泄漏。8组改名脚本的 `.meta` GUID 不变；按验收产物的 CRLF-to-LF 规则复核584个包 C#/程序集/meta输入，全部与验收时一致。没有为格式统一改写未修改的脚本。

### 10.6 真实 Operation 与域恢复

通过公开 `unity_operation_validate/start/status/collect_artifacts` 执行 `jobSpec.stepPlan`，没有手写调用组或重新 Start：打开 `Assets/UPilotAcceptance/upilot-acceptance.unity`、进入 PlayMode、等待0.5秒、Finally 返回 EditMode。

- Operation `op-4da7f124-ea4d-4a4f-8ad2-a32feebb1924`，Step run `2a735ead6a2e47f29819477b0d8ffcab`；四项 Succeeded，startAttemptCount=1。
- Unity PID `57636` 不变，Bridge session 从 `7e1013bfc5804756bfd700a864b70c20` 变为 `376b881b62fd45ea97a33e2089b44879`；真实 Domain Reload 后仍观察原 Operation/run 身份，不重放 Execute。
- 最终 businessTerminal/cleanupTerminal/editorTerminal/terminal=true、editorVerification=verified、cleanupPending=false、unresolvedResources=[]，回到 EditMode。
- Operation 自有 Capture `console_20260922_181735_071_9a9219b1` 收尾；报告固定区间 `[0,13)`，最终活动 Capture=0，artifactErrors=[]。
- `Log/UPilotSteps/2a735ead6a2e47f29819477b0d8ffcab/events.jsonl`：1758 bytes，SHA256 `6b2c46c09532b8da37a04d1789695536d5ff9db562e4c213c4fa1274dd81a38f`。
- 同目录 `summary.json`：1722 bytes，SHA256 `cc2d0247d7b1f79d66cd0089c013e9d632d0df766a5508584c0adc01a6234b26`。
- 证据目录 `Tests~/UPilotTest/Log/P0P1/`：`automation-api-catalog.json`、`automation-api-e2e-validate.json`、`automation-api-e2e-start.json`、`automation-api-e2e-terminal.json`、`automation-api-e2e-artifacts.json`、`automation-api-captures-after-e2e.json`。

### 10.7 项目只读兼容与 Skill 同步

- xclient2 `AnalyzeWorkflow` 对 `resource.preload`、关卡10004、英雄10001/10002/10003返回 Ready，selectionEngine 为 `UPilot.AutomationSelection`；请求 `req-bc8ba3f1-6f93-41ac-8466-1a89b2df3fff`。非法 Case 返回 `BattleWorkflow.InvalidSmokeSelection` / `SmokeSelection.CaseNotFound`，请求 `req-58891ef8-d614-4ba8-a6cb-e73c0e6b2818`，没有启动 PlayMode。
- `KingShotBattleSmokeCaseRegistry.GetAutomationCatalogJson()` 返回 version=1 的 Case/Suite JSON；请求 `req-78a56b75-bbcb-4598-aac7-97f9e753bbc1`。此前外包 JObject.Parse 的 Bridge Catalog 投影遇到继承静态重载歧义，保留失败，不将该投影冒充通过；已记录根 TODO 的 `UPilot-20260922-NestedCallBinding`。
- 权威模板 AgentRules=40、SkillPack=43；source 生成/check及校验通过，canonical 与 xclient2 的五个目标均 current，两工程共四份 installed Skill 校验通过。本次显式同步没有扩展到其他工程。
- templateSha256 `06e5f81fa4bbe6c506f8035dfb7af6088587fbcea9ea3151aceb1fc7b3bbd177`；canonical Skill contentSha256 `c8170a14f58311a545496246d111bc9d9e28a0ba3adbe8c32600eed0ec8330e6`；xclient2 `4e117ef6d579cdf873a04f9a4ba731ee7de0c1bed8f517cd2b87c4d436f8e0b6`。
- 两工程实际重复完整 apply，各46个受管文件的集合、SHA256、纳秒mtime均不变，备份目录集合不变；显式 apply 前后规则块外字节不变。canonical 初查时已 current，不追溯声称此前未观测的自动同步也通过了字节基线校验。
- canonical 证据 `automation-api-integrations-check/sync/repeat/final.json`；xclient2 首次同步 `req-5c0d471d-5324-42dd-aef0-b9b22d5580ea`、重复apply `req-7b377ed2-3ad7-48a5-81aa-2c3d2de3237e`、最终检查 `req-712322b5-0e28-4b89-8f7e-cb8745fa42c9`。

### 10.8 交付边界

本轮统一 API、字符串 Step 执行器及上述定向验收已完成。xclient2 业务 Step、旧 Runner 替换、战场实跑及实际无包编译仍为后续迭代；宏关闭静态检查不是无包编译证据，不声称全量回归通过。

测试 Console 增量覆盖标记为 partial/console_delta_truncated，xclient2 编译后的 Console 查询标记为 partial/domain_reload_boundary；不据此声称完整区间零 Console Error。编译0 Error来自各自关联的结构化编译结果；真实 Operation 使用独立固定区间 Capture 证据。

真实公开 stepPlan 分支已通过，但完整运行中 Server 源码身份认证仍 unverified；本轮仅更新的工具描述/校验提示可能尚未被现有 Server 重新加载，不为此自动重启。未沿用历史强制清理授权，未修改包清单脏状态，未提交本地 PlayerSettings。反射消歧 P1 与维护清单 P2 已写入根/项目双向 TODO，属于后续改进，不纳入本次业务无关 Step 执行器。

## 11. 初始化观察竞态修复

2026-09-22 xclient2 业务迁移期间发现：新 Editor 能查询原 Step run 并报告 `STEP_EDITOR_RESTARTED`，而 Server Operation 已因早期 `STEP_RUN_IDENTITY_MISMATCH` 停止观察。源码将服务尚未完成 delayCall 初始化与真实身份不匹配混为同一错误，这是独立于业务 Case 的通用缺口。

- 服务未初始化时返回 `STEP_SERVICE_INITIALIZING`；初始化完成后，无运行或 runId/operationId 不匹配继续返回 `STEP_RUN_IDENTITY_MISMATCH`。
- State 不调用 Initialize、Restore、Tick 或 Execute，不保存数据；返回执行器快照，不把状态查询变为恢复入口。
- Server 只对精确 `automation.steps.state` 路由的初始化错误保持 Recovering，继续观察相同运行。真正身份错配、业务 RecoveryRequired、Editor 重启和其它路由错误不降级；旧锁定历史作业不自动解除。
- 没有新增公共工具、项目类型引用、Provider、包依赖或第二执行器。

验证：Python Step计划19/19及既有Operation status/cancel/recovery/placeholders筛选7/7；canonical两项新增Unity测试2/2，runGuid=`c06bceb9-2cbc-4bcb-848e-7e094787172a`，清理、身份和sourceUnchanged均通过。包编译批次 `wb-3ca931b1-9b7a-45ff-b5e7-0745c0ad6038` 为0 Error/3 Warning；xclient2批次 `wb-17589f5d-d31b-45b2-838b-8d24f4be738e` 为0 Error/21 Warning，两者均关联验证成功。

验收报告：`Tests~/UPilotTest/Log/UPilotAcceptance/1790089489188_req-5571bba5-612a-4910-b569-d7d2a21ed271/summary.json`，465687 bytes，SHA256=`9199d2729987aab12ac9e8172135606d3e76179622abfd97d23b3336947ae062`。没有运行全量测试；live Server尚未证明加载本次Python变更，部署unverified。本次没有重启或自动处置原Operation/Capture；xclient2完整迁移仍未完成，以独立迁移文档15.7为准。

## 12. 附件登记与有界 Console 诊断

为补齐业务Step迁移后的证据链，复用已有Report/Policy/Operation，未新增工具、Provider、依赖或业务类型。

- `RegisterArtifact(runId,instanceId,kind,path)`为服务公开入口及基类辅助；只允许当前可写主线程回调。登记真实项目内不可变文件、保存所属item/大小/SHA256；拒绝缺失、目录、链接/联接、错误身份、冲突内容和框架自己的报告文件。重复同身份同内容不重复添加。Finally可在前序清理失败后记录恢复附件，仍保留未释放资源门禁。
- 执行器冻结前重验登记文件，错误不覆盖原首错。公开`attachments`各项显式`kind=file`并保留`instanceId/artifactKind`。Server仅展开显式附件数组，沿用原包含性/稳定读取/声明哈希检查；旧调用组同样可用，不扫描目录、不把任意数组当文件。
- `domain.logSummary`及冻结summary保留Capture/range、Policy/evidence判定和计数，最多10个阻断样本（消息2048、标签256字符），明确省略条数与文本截断；诊断最多10条。完整分类、原文/堆栈和指纹保存为`console-policy.json`，以`consolePolicy`附件统一收集。
- 分类在全部Finally后计算；写入失败不能被当作证据完成。状态读取不推进/重写报告；没有Capture不生成已验证日志结论。普通项目Step无需引用新增报告内部模型。

定向验收：附件首批21/21及Finally修正1/1；最新Report/Policy/Evidence与执行器相关19/19，runGuid=`f3c4381a-9c4d-4c04-951c-e7e91a29e8c3`，acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true。报告`Tests~/UPilotTest/Log/UPilotAcceptance/1790091589499_req-ac367ad3-5401-4bf3-8df3-c7abbdd3b749/summary.json`，515033 bytes，SHA256=`e5c18fe7c512005f0f916f3fa5156518a7ecdb660d04443ee4fccdfb73bbbfe6`。Python step/artifact筛选37 passed、19 deselected。

包编译`wb-c1da78ca-43bc-4cac-9217-9f7f70a27b1c`/`e501d9ee31ad4f4ab639083f6e01b79e`（verified=1790091537129）0 Error/3 Warning；xclient2编译`wb-70c17d26-f929-406d-957f-85f046e8c02a`/`838e38e6ef6942bbbb24a986a1eab50f`（verified=1790091540763）0 Error/21 Warning，均关联验证成功。xclient2业务附件与Snapshot文件接入已编译，但没有新业务实跑证据。

Skill资源按权威源更新为AgentRules40/SkillPack44，source和四份installed校验通过；canonical/xclient2各五目标current。各46个受管文件在实际重复完整apply后内容/纳秒mtime/集合不变，备份目录及规则块外字节不变；templateSha256=`f20157e7254f6536c9f000044b2e6c073d389acc0eb13268d5e5e3bef51de3c4`，Skill内容hash分别为canonical`19234ccbac55995bfcd473f5d1eac9773542690f7ff299954fc7b31e9b5ba003`和xclient2`8bcc523fc656823b398513d37ff40652ef33b447535483ad9fa7e5b8e4f3547d`。

剩余限制：运行Server源码加载及公开附件收集仍需验证；规范工程当前没有可调用的维护重启工具，不用shell/反射绕过。真实symlink/junction夹具、最新xclient2双轮/异常/Skill/无包编译尚未执行，原中断Operation/Capture未自动处置。业务报告、配置语义和本地兼容Capture持久化继续项目所有，不以通用定向通过代替完整迁移。

2026-09-23环境补充：上述19/19验收及Skill同步完成后，canonical Unity断开；`unity_mcp_status(forceFresh=true)`的实时进程查询报告main_editor_not_found，旧PID56124不再存在，请求`req-6e283dd6-4e3f-4e9d-956a-06bc91c78068`。原因未定，没有自动启动/重启Editor或清除状态。该环境变化不改写已完成验收证据，但当前不能开展新的canonical验收。本轮仅xclient2兼容入口新增项目自有凭据日志，0 Error关联编译及36项隔离文件检查不属于包验收，完整进度以独立迁移文档15.9为准。原生Capture API缺少持久停止证明的代码审查发现已补入根TODO既有Stop条目，未宣称故障注入复现或改动包实现。

## 13. 冻结报告导出与恢复

2026-09-23新增实现；下述01:05记录保留历史待验收状态，最新规范工程50/50结果见13.1，不沿用第12节19/19结果。

- `AutomationReportExport` 只从冻结数据生成 `report.txt` 和 CRLF `timing.csv`，不调用业务代码。UTF-8输出、固定小数格式、CSV引号和公式前缀转义；无时间依据时耗时为空。执行时间与Cleanup时间分别记录，最终Console失败不会被成功Finally掩盖。
- `AutomationReportWriter.Complete` 先写两项导出，再提交summary；`exportVersion=1`为数据契约，非API名称后缀。未完成文件不出现在终态产物索引，已存在导出必须与冻结数据一致，篡改不自动修复。历史0版重复Complete按旧模型语义比较，不修改字节或补写新文件。
- 执行器在副作用前保存`reportCommitJson`和最终时间。同进程Finalizing恢复只提交原快照，不重算Console或重放Step；提交前按冻结引用复验业务附件及console-policy。新缺失/内容变化进入RecoveryRequired，保留原首错与冻结报告；冻结前已明确诊断的坏附件仍作为失败证据记录，不伪装成有效文件。
- 项目指标仍由项目Step保存在共享检查点并登记业务附件，不向包引入轮次、GM、关卡或Case语义。报告提交与Operation停止自有Capture是两个独立证据。
- 新增定向测试覆盖终态/Finally区分、执行/清理耗时、格式/转义、部分导出恢复、篡改拒绝、历史幂等、冻结时间恢复及冻结后业务附件/Policy删除修改和首错。测试代码已写入，尚未在canonical运行。
- xclient2批次`wb-9d16d61b-001e-45d9-8f48-cd77ca4012c1`，compile=`6e8d13e3c7d4434d8034de02283e31cb`，created=1790095415519、verified=1790095427020，0 Error/0 Warning、terminal/errorsVerified/correlationVerified=true。这只证明宿主关联编译，不替代包测试。
- canonical最新批次`wb-2dceec0a-ced5-48da-8720-45affd2f101a`已登记为deferred/disconnected，早先三批同样未取得新终态；重连后观察原身份，不重复注册或手动触发编译。当前不自动启动Editor或清除历史任务。

2026-09-23 01:05补充（上述批次为历史，以下为最新）：

- Writer在GetArtifacts/重复Complete时验证summary文件。静态复核发现旧实现从文本重编码会丢弃历史BOM，已改为OpenExisting单次读取原始字节，再由同一字节解码和建立验证基线；新提交保存实际生成的无BOM字节。验证仍逐字节，打开后仅增删BOM也拒绝，不忽略编码差异。
- 定向测试补充BOM/无BOM历史Complete幂等及bytes/mtime不变、历史/新版报告的BOM增删拒绝。冻结附件恢复24组合夹具已校正：合法Policy经真实Finalizing冻结后再修改/删除，未变对照检查产物集合与哈希。以上新增NUnit均未运行，不标为Passed。
- 最新宿主关联编译`wb-f14ebc7e-07d2-4453-8657-f9671ccb5f31`，created=1790096600193，compile=`a2ff1eaa231e49dfb5df5708acd15d4e`，verified=1790096614503，0 Error/21 Warning、terminal/errorsVerified/correlationVerified=true；警告明细均在本次未修改的项目脚本。Console为空但partial/domain_reload_boundary且scannedCount=0，不证明完整区间无错。
- canonical最新登记`wb-d4f40fb2-87c2-47b0-9938-a90a19a38c92`，created=1790096599956，deferred/disconnected。其前夹具/断言/summary批次`wb-45416c9a-b0d6-4099-9ca4-4870c1b90f25`、`wb-ef2ba434-c022-4715-a901-6327e70a3819`、`wb-a82d9aed-c542-4527-abd9-97c59a238e06`也未取得canonical终态。实时查询main_editor_not_found，不用宿主编译替代规范工程验收。
- Skill报告契约上一批已完成AgentRules40/SkillPack45同步：source和四份installed校验通过、两工程各五目标current、各46个受管文件实际重复apply后集合/哈希/纳秒mtime及备份目录集合不变。规则块外保证仅覆盖已观测基线，不追溯xclient2此前的自动同步。templateSha256=`34c597e49e37ae2d89a1327ec097bd06cab05341c8be35887e2cf301e50d8469`，canonical content=`434904151f9878b7f2b2ed9cf2f44d9d08f46b979add749585b8ab629cdabf80`，宿主content=`c20f89656932cae7879e86edc4a64ca6b4bebef117e5dd89bdc2137ed9f218e2`。本次只回读确认安装记录，未改模板或重复apply。

完整迁移仍未完成：最新包测试、公开产物收集/Server加载、真实业务两轮/兼容/异常恢复及无包编译待验收；没有自动处置原Operation/run/Capture。项目详情及精确身份见独立迁移文档15.10。

### 13.1 规范工程恢复与报告定向验收

- 规范工程重新连接后，观察原批次`wb-d4f40fb2-87c2-47b0-9938-a90a19a38c92`取得`passed/terminal/errorsVerified/correlationVerified=true`，compile=`a6a4a8d31b8b4bf7ac3da3f0686a3029`，verified=`1790119972293`，0 Error/0 Warning；没有重新登记未变化文件。
- 首次定向执行50项，49通过、1失败，runGuid=`e467845a-fe64-4995-b820-58fa3f051107`。无Capture的`logSummary=null`经JsonUtility往返变成默认对象，文本导出误显示空session及false统计，而不是未采集。原失败报告保留：`Tests~/UPilotTest/Log/UPilotAcceptance/1790120310487_req-a8ce0b10-0d90-442d-b716-5d409dfface8/summary.json`，647359 bytes，SHA256=`dc7fdcbc2b84aae1e714e5c18445e50872d9ae9e6f33fcfbfe46444f4a24b6ab`。
- 最小修复只让`AutomationReportExport.Text`按实际Capture session身份判定已采集，不增加JSON字段或业务依赖。Writer测试补充重开/重复Complete/产物哈希不变；Executor两种实现及冻结恢复补充无Capture文本断言；有Capture的失败Policy仍显式输出false，不掩盖失败。
- 修复关联编译：canonical批次`wb-65301382-2241-4bc0-9673-66250ef3af67`，created=`1790120529293`，compile=`6dd7eb3b9f194fa9902fcb107e2394c3`，verified=`1790120546424`；xclient2批次`wb-3eaa41ad-bbba-4433-a00a-5b35d23ec99e`，created=`1790120529572`，compile=`7b82fcc2e31c40c5a98d315db04c637e`，verified=`1790120548500`。两者均0 Error/0 Warning且terminal/errorsVerified/correlationVerified=true，结构化错误已读；不把此增量批次的0 Warning用于改写旧批次警告。
- 最终task=`task-a62e7e15-2d4d-4c46-9c17-dae19a37cbb8`，runGuid=`2d6a18d3-e14d-46d3-b355-3fbddca71827`，50/50通过、0跳过、cleanupSucceeded/resultAuthoritative=true，acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true。选择为Writer fixture的22项，加冻结证据24组合、冻结时间恢复1项、接口/基类顺序2项和Finally附件登记1项；不是全量回归或真实业务恢复验收。
- 最终报告：`Tests~/UPilotTest/Log/UPilotAcceptance/1790120627353_req-d32d9119-cb1c-4c77-9726-191fadc5bc4f/summary.json`，652308 bytes，SHA256=`4be8491e065378184e13f23c5d1dfe829f34685ae2bf7d649dc2105b633d48ec`。源码身份`2050887556be39696b0c9280a2c278f4db44beb5f7806c03078ea264f15a017a`，851文件；测试复用了当前关联编译，没有无代码变化的第二次编译。
- 按最终runGuid查询Console，Error为空、coverage=complete（请求`req-4728369b-727f-46ed-b886-8edfc257f119`）；活动Capture=0（`req-b6db348b-435e-4fd9-961f-d5f62c691367`）。编译Console区间仍为partial/domain_reload_boundary，不能把测试完整区间等同于完整编译日志。

报告导出、BOM兼容及冻结恢复子项已获新的规范工程定向证据；公开Operation附件收集与xclient2真实流程仍未完成。宿主按独立AI Service Maintenance授权只刷新Server，未重启Unity或处置旧任务；刷新后发现历史手动批次重新阻断readiness，详见项目迁移文档15.12及根TODO既有WriteBatch恢复处置条目。本次没有修改Skill模板或重复同步已验收的SkillPack45。

### 13.2 宿主真实链路补验

2026-09-23，宿主对四个精确历史对象授权备份处置后，完成以下验证。本节更新13.1的公开收集/业务待验收状态，不把宿主实跑替代规范工程测试，也不修改旧中断运行结果。

- 生产KSB Skill固定模板直接提交两轮20项stepPlan，Operation=`op-846c3d8a-e664-44d1-9490-defdfb00b669`、run=`baf6b76ed5ad4f3dab98223fe4c82fcc`。20/20 Succeeded、4个业务Case结果Passed，startAttemptCount=1，业务/清理/EditMode均权威确认，unresolvedResources为空。
- 公开unity_operation_collect_artifacts收集37文件，artifactErrors为空，全部独立核对大小/SHA256。业务附件、4张按item隔离的截图及其manifest、完整Policy、report.txt/timing.csv均包含；summary为23455 bytes/SHA256=`7c53bdfcbe319cc4cf75d6933f36357804703ee31641872ca65058a2d50d34b7`。最终Console范围[0,2932)完整且Policy通过，Capture由Operation停止。
- Operation-owned旧兼容调用组单轮成功，run=`03f4234cbb944cefb4e688fbef1ac954`，公开收集14文件并复核一致。本地兼容取消run=`b55c1f1c20164f2a9ea54e79f80bacc8`为Canceled/STEP_CANCELED、15文件核验；正常run=`53532aa838c14d419eb68818407d3c7c`为Succeeded、14文件核验。两次本地Capture均由项目观察器收尾，凭据清除且终态持久化；没有第二执行器或客户端逐步推进。
- 本轮没有C#变更，复用13.1关联编译；未改模板、UPM依赖、包清单或PlayerSettings，未重启Unity。最后宿主EditMode/Ready、activeCapture=0。公开附件路径已获真实行为证据，但这不等于整个Server源码身份已完成认证。
- 定向验收详情、请求身份、报告/截图/Capture哈希和剩余项目矩阵统一见`D:\MA\_AI_Docs\xclient\30_ENVIRONMENTS\xclient2\Automation\UPilotStepExecutorMigration.md`的15.4/15.15。包级真实symlink/junction拒绝夹具、项目异常恢复及实际无包编译仍未执行，不宣称全量或完整迁移通过。
