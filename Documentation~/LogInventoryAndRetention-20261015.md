# UPilot 日志、报告与保留策略审查

日期：2026-10-15（Asia/Shanghai）。第 1–5 节为现行统一路径和日志保留说明；第 6、7 节保留此前日志保留及错误/降噪工作的原始验证记录；第 8 节记录本次默认输出路径调整和定向验证。

审查范围为当前工作区 `Editor/`、`upilotserver~/src/` 及规范工程的输出目录元数据。所有默认可见产物集中于所属工程 `Logs/UPilot/`，按用途分组；下列 `P` 表示 Unity 项目根目录，本次为 `D:\upilot\Tests~\UPilotTest`。此清单按当前代码生成的产物类别聚合，不把工程中每个手工保存的旧文件视为现行产品输出。

## 1. 已实施的持续日志统一规则

高级设置 → 常规 → 日志只提供一个项目级“日志保留天数”，所有按天日志共用 `.upilot/config.json` 的 `logging.retentionDays`。默认 3，范围 1–365；旧配置缺字段或无效值回落 3。N 表示本地自然日的今天及前 N−1 天，不是滚动 N×24 小时。

| 当前文件 | 内容 | 统一行为 |
| --- | --- | --- |
| `P/Logs/UPilot/step-YYYY-MM-DD.log` | 完整生效计划、任务/步骤/清理起止及耗时、恢复/停止/终态 | 按实际写入日期追加，事件时间戳保持原值 |
| `P/Logs/UPilot/upilot-YYYY-MM-DD.log` | Unity 通用诊断及 Flow 转发日志 | 同日、新 Unity 会话及 Domain Reload 均追加；保留会话头和日志级别/可选开关 |
| `P/Logs/UPilot/mcp-server-YYYY-MM-DD.log` | Python Server 诊断，继续同时输出 stderr | 内部按天路由，启动身份参数为固定 `--log-file …/Logs/UPilot/mcp-server.log`，严格匹配新路径 |
| `P/Logs/UPilot/McpCalls/csharp_eval-YYYY-MM-DD.log` | 调用开始/结束、原始参数/结果、异常或取消 | JSON 每事件一行，callId 关联，记录起止、状态和 durationMs |
| `P/Logs/UPilot/McpCalls/unity_reflection_call-YYYY-MM-DD.log` | 同上 | 同上 |
| `P/Logs/UPilot/McpCalls/csharp_object_dump-YYYY-MM-DD.log` | 同上 | 同上 |

所有上述文件不限大小、不按大小轮转/覆盖、不截断持久化条目。MCP 参数和结果不新增脱敏；工具自身的结果/协议边界以及 Console 和诊断尾读的展示上限保留。已有固定文件及 `.1/.2` 备份停止写入但保留原样，不迁移、不按 mtime 清理。Flow 旧独立文件也不纳入删除白名单。

各写入器首次使用、跨日首次写入、有效保留天数变化后的下一次写入时清理；无后台清理定时器。仅删除本写入器精确日期名称、日期有效且过期的普通文件，不跟随链接、不删除未知/未来/畸形文件和目录。空闲或离线期间不承诺即时物理删除。

Unity 和 Python 使用各自共用的每日写入组件；以后新增按天日志应使用相应组件，读取同一配置，不另设天数。Python 在写入前通过现有安全热加载机制观察配置变化，不需重启。打开日志及 Server 尾读仅在新目录优先当天、其次最新有效日期文件；未生成时明确报告缺失，不回退旧固定文件，跨日重新解析。

依据：[每日文件组件](../Editor/Core/UPilotDailyLogFile.cs)、[项目配置](../Editor/Core/UPilotProjectConfig.cs)、[核心 Logger](../Editor/Core/Logger.cs)、[Step](../Editor/Automation/AutomationStepExecutionLog.cs)、[Python 每日组件](../upilotserver~/src/upilot_mcp/daily_logs.py)、[调用记录](../upilotserver~/src/upilot_mcp/mcp_stdio_server.py)。

## 2. 报告与采集：可讨论 N 天过期，但不能合并每日文件或删单个片段

| 当前路径/文件组 | 当前行为与保留 | 不可直接照搬的原因及可行方向 |
| --- | --- | --- |
| `P/Logs/UPilot/Reports/Steps/<runId>/events.jsonl`、`summary.json`、`report.txt`、`timing.csv`、可选 `console-policy.json`、`snapshots/<instanceId>/…` | 按 run 输出；最终报告冻结并记录 bytes/SHA256；未发现年龄清理。 | 一次任务是一组证据。可在终态后整组过期，不能截断事件、改写冻结报告或删除仍被引用的附件；过期会使旧 artifact 链接失效。 |
| `P/Logs/UPilot/Console/<时间_标题>/console.jsonl`、`console.001.jsonl` 等、`session.json`、`summary.json`、`console.index.json`；根 `session-index.json` | 显式采集；默认每片 50 MiB、约每秒 flush。现有显式清理默认 14 天且保留最近 20 个，排除活动会话，dryRun/confirmToken 两阶段。 | 序号、索引、hash 和报告关联依赖会话整体。可按终态会话清理并维护索引，不能删部分分片。保留最近 20 个与“最多 N 天”冲突，是否取消该保护须决定。自定义目录不默认纳入。 |
| Capture 会话目录下 `attachments/<attachmentId>.jsonl` | 固定序号范围的分页导出，有持久提交边界/恢复和最终 hash；未见独立过期清理。 | 与源会话及引用任务关联；导出中不能删除或重置，按组处理。 |
| `P/Logs/UPilot/Reports/Acceptance/<startedAt>_<requestId>/summary.json` | writeArtifact 时输出编译/测试/清理/源码身份及结论，原子提交，提供大小/hash；未见年龄清理。 | 验收或发布可能引用历史证据。可以另设报告保留期并接受旧链接失效；不能混入普通日志自动删除。 |
| `P/Logs/UPilot/Diagnostics/Profiler/<UTC时间>_<title>.json`、`.csv`、可选比较 `.json` | 采集结束保存；JSON 可作为后续 baseline；未见年龄清理。 | 应按 capture 成组，保护被作为基准的文件。各路径分别取时间，不能只靠相似文件前缀猜组。 |
| `P/Logs/UPilot/Diagnostics/Snapshots/<UTC日期>/<snapshotId>/manifest.json`、color PNG、depth EXR、预览 | 显式采集，manifest 记录身份、字节、hash、证据有效性；未见年龄清理。 | 整个 snapshot 与状态及报告引用相关。可按终态组过期，不能只看 PNG/EXR 修改时间删文件。 |
| `P/Logs/UPilot/Diagnostics/Screenshots/*.png` 或用户自选图片路径；`Logs/UPilot/Diagnostics/SnapshotComparisons/<时间-key>/diff.png/heatmap.png` | 截图/比较输出；未见年龄清理。 | 默认托管目录可另议过期；用户指定输出不得自动纳入。比较图按一次比较成组。 |
| `P/Logs/UPilot/Diagnostics/QuickDebug/ToolChecks/{csharp_eval,unity_reflection_call,csharp_object_dump}.log` | QuickDebug 工具检查：先写未完成占位，结束原子覆盖为本次完整报告。 | 虽后缀为 .log，却是最近一次报告，不宜改成每日拼接。可另存按次历史并清理到期终态报告，保留“查看本次结果”语义。 |
| `P/Logs/UPilot/Diagnostics/QuickDebug/csharp_object_dump.log` | QuickDebug 对象查看结果；每次开始清空，完成覆盖。 | 当前是本次完整对象输出。是否另留历史需决定，不能把旧对象结果混进当前查看文件。 |
| Flow：MCP 默认 `P/Logs/UPilot/Reports/Flow/<executionId>/`；固定输出为 `Default/`、`TestRunner/`、`HeadedAll/`，包括 `Cases/*.json/*.md`、suite JSON、`full_reports.md`、`single_reports.md`、`Screenshots/*.png`、`Artifacts/artifacts.json`；统一历史 `P/Logs/UPilot/Reports/Flow/History/Cases/unified-history.json` | 按执行输出；统一历史仅从新 History 目录聚合重写，不导入旧历史；未见年龄清理。另有 TestRunner/HeadedAll 输出。 | 显式报告目录可能与用户文件共用，不能扫目录清空；execution 组及聚合历史分别设计过期，不能当日流水。 |
| E2E：默认 `P/Logs/UPilot/Reports/E2E/<runId>/report.json`、`manifest.json`、截图、失败 UI/Console JSON、可选 bundle ZIP | 显式执行输出，可自选 artifactDir；未见年龄清理。 | 保留完整 run 及证据引用；只管理明确的默认托管组，自选目录排除。 |
| `P/Logs/UPilot/Diagnostics/Dumps/unity-<pid>-<UTCms>.dmp` 或自选 outputPath | 显式诊断转储，验证进程、空间和 hash；可能很大。 | 不是文本日志。建议独立诊断产物保留期或显式清理；不能仅因父目录为 Log 而默认删。 |
| Tracer：用户导出的 `UPilotTrace.jsonl`、`UPilotTraceDiagnostics.jsonl`，过滤器 JSON | 手动导出才落盘，通常覆盖自选文件；事件导出默认最多 2048 条；无年龄清理。 | 用户保存的数据或配置。不能按通用 3 天清理；也不能为了持续日志自动开启 Tracer。 |

依据：[Step 报告提交](../Editor/Automation/AutomationReportWriter.cs) 286/319/454；[Console Capture](../Editor/Core/UPilotConsoleCaptureService.cs) 28/679/1120/1214；[Capture 导出](../upilotserver~/src/upilot_mcp/domain/status_service.py) 2886；[包验收](../upilotserver~/src/upilot_mcp/domain/test_service.py) 1150；[Profiler](../Editor/Core/UPilotRuntimeDiagnosticsService.cs) 496/783/971；[Snapshot](../Editor/Core/UPilotSnapshotService.cs) 2038/2198；[基线/比较](../upilotserver~/src/upilot_mcp/domain/snapshot_service.py) 294/430；[QuickDebug 检查](../Editor/QuickDebug/UPilotQuickDebugToolCheck.cs) 173/184；[Flow 报告](../Editor/Optional/Flow/Report/UPilot.Flow.Reporting.cs) 34/593；[E2E](../upilotserver~/src/upilot_mcp/editor_e2e/runner.py) 99/214/258；[dump](../upilotserver~/src/upilot_mcp/domain/analysis_service.py) 965；[Tracer 导出](../Editor/Optional/MonoHook.Tracing/UPilotMonoHookTelemetry.cs) 32。

## 3. 不属于普通日志的状态、输入、备份与构建文件

| 文件组 | 不能按普通日志期删除的原因 |
| --- | --- |
| `Logs/UPilot/CompileErrors.json`、`EditorExecutionState.json`、`.session` | 当前编译/Editor 的身份与验证快照及会话标记；旧时间不意味着已无用。 |
| `Library/UPilot/ServerState/state-v2.sqlite3`、`-wal`、`-shm` | 当前与历史 Task/Operation/WriteBatch/编译/Capture/Console evidence 数据库；只有 state_events 已限最近 500 条。不能直接删除 DB 或 WAL。历史压缩需独立的关联保留策略。 |
| `Library/UPilot/TestRuns/<runGuid>.json`、`active-run.txt`、`last-run.txt`、dispositions；`step-run.json`、`.capture`、处置备份；`SnapshotJobs/<id>.json` | 运行/恢复/清理/幂等身份及终态查询。可以另议终态历史淘汰，不能年龄 glob 清 Library。当前 TestRunner 不自动导出 NUnit XML，磁盘旧 XML 不代表现有默认输出。 |
| `Library/UPilot/startup.json`、`server-restart.json`、`service-maintenance.json`、`QueueCleanup/*.json`、`playmode-transitions.json`、`window-history.json` | 当前诊断和执行控制状态；重启原身份的持续观察依赖这些记录。server-restart-history.json 另有最多 10 条的结构化历史，不应整文件按年龄删除，可另议终态条目裁剪。 |
| `.upilot/snapshots/baselines/<key>.png/.json` | 后续比较的长期输入，非一次性报告。 |
| `Library/UPilot/PrefabPatches/.../before.prefab/.meta`、`.upilot/backups/agent-integrations/...`、维护/队列状态备份与 `.corrupt` 原件 | 修改恢复、审计及防重复执行凭据。保留期应服从各自恢复功能，不能默认为普通 3 天日志。 |
| `.upilot/config.json`、Agent 集成清单、诊断模块 DLL/源码等 | 配置、安装或执行输入，不是日志/报告。 |
| Unity `%LOCALAPPDATA%/Unity/Editor/Editor.log` | Unity 自身拥有的引擎日志，UPilot 仅提供打开入口，不属于项目专属日志清理。 |
| Unity Player 构建 outputPath 下的应用/包 | 用户指定的发布/构建产物；UPilot 当前 BuildService 只在内存保存 _lastBuildStatus，不单独写默认 build-report 文件。 |

依据：[Compile 状态](../Editor/Core/UPilotCompileService.cs) 18/90/136；[Editor 状态](../Editor/Core/UPilotBridge.cs) 2306；[DB](../upilotserver~/src/upilot_mcp/state_store.py) 145/1276；[TestRun 状态](../Editor/Core/UPilotTestService.cs) 323/2841；[Step 状态](../Editor/Automation/UPilotAutomationStepService.cs) 57；[重启历史](../Editor/Core/UPilotServerRestartDiagnostics.cs) 151/882；[Agent 备份](../Editor/Core/UPilotAgentSetup.Integrations.cs) 450；[BuildService](../Editor/Core/UPilotBuildService.cs) 96/163。

工程磁盘还有 P0P1、Stage4、StepAdmission、DistillImplementation、各 Validation 等历史人工调试/验收目录，以及临时和旧版本文件。本次没有证明它们由当前受管写入器生成，不应列入自动清理白名单；不能按 Log/Logs 父目录、后缀或最后修改时间一刀切删除。

## 4. 内容、故障与路径边界

- Step 任务计划、生命周期和报告顺序保持现有机制，不重放步骤、Finally 或丢失的日志事件。
- 三类 MCP 工具在实际调用前写 `call.start`，返回/异常/取消后写 `call.end`；同一 callId 关联。成功、明确业务失败、异常和取消分别判断，记录原始参数、结果或原异常堆栈；仅在确实可用时附加 requestId/operationId。
- 日志写入/清理失败必须有可见 Error/Exception，包含操作、路径和原异常。Unity 通过不回写失败文件的 Console 错误通道，Python 直接 stderr；不递归、不抛回业务、不自动重试调用。
- 本次用户已明确将 Step 文件故障改为可见错误，替代此前 file-only 静默策略；不从 Step Console Policy 中豁免，因此可能影响其判定。现有重复故障合并不等于故障已解决。
- Server 的启动和停止只精确匹配新逻辑路径 `Logs/UPilot/mcp-server.log`；文件写入与读取解析每日实际路径，不再接受旧身份路径，不放宽跨项目/外来进程限制。
- 第 2、3 节报告、采集、状态、基线、备份、转储、用户导出、构建及 Unity Editor.log 保持原生命周期，不由日志保留天数删除。

## 5. 初次审查发现及本次处理

原先核心 Logger 的 10 MiB/2 备份和 64 Ki 文件条目限制、MCP 文件的 10 MiB 覆盖/截断、Step 固定 3 天及文件失败仅 Warning/静默等，已由本次统一规则替换。Bridge 持久化前 800 字符及 Server 日志专用诊断截断亦移除；保留协议错误字段、诊断尾读和 Console 的展示边界及既有安全处理。

初次磁盘盘点曾观察到 UPilotDiagnostics 约 12.8 GiB、Acceptance 约 141.1 MiB、Console 约 127.8 MiB、ServerState 约 237.9 MiB；这仅为当时观察值。大部分不是本次受管文本日志，不能据此声称日志改造已控制全部磁盘占用，也不授权删除这些产物。


默认输出路径由 C# `UPilotOutputPaths` 与 Python `output_paths` 集中定义，根目录为所属 Unity 工程，不依赖调用时工作目录。完全放弃旧输出路径和旧文件的兼容与迁移：不扫描、导入、合并、转换、恢复、复制、移动或删除旧产物；新目录历史从空开始。已保存的显式输出设置和用户传入的路径原值继续有效，即使它们恰好等于旧默认路径，也不进行识别和改写。

配置、基线、备份、运行和恢复状态保持现址，包括 `CompileErrors.json`、`EditorExecutionState.json` 和 `.session`。报告、附件、截图、Dump 继续按原生命周期保存，不纳入每日日志保留。仓库忽略规则覆盖默认输出目录，不修改用户工程的 `.gitignore`。以下第 6、7 节的既有验证身份和历史证据引用保留原值，不代表新默认路径。

## 6. 验证记录

本次使用规范工程 `D:/upilot/Tests~/UPilotTest`，Unity `6000.6.0a2`，Editor PID `127400`；没有运行全量回归。

- Python：`tests/test_daily_logs.py`、`test_mcp_tool_call_logs.py`、`test_config_refresh_reads.py`、`test_request_origin.py` 最终 **88/88** 通过，2 条既有 websockets 弃用警告。覆盖共享默认/边界/热加载、换日及同日缩短、严格清理白名单和链接、大于 10 MiB 的完整 Unicode/多行追加、IO 错误 stderr、实际入口子进程，以及调用成功/业务失败/异常/抛出式和返回式取消的关联、耗时和单次执行。
- C#：实施和测试批次均通过官方 WriteBatch 验证。最后本任务测试修正批次 `wb-d80bd748-b200-465d-b766-417267719f59`，compile `429182cbceb64b9db9ca7364cbff0f6d`，created `1792055430331`，verified `1792055442946`；terminal/correlationVerified/errorsVerified=true，0 Error、1 条既有测试 AssemblyBuilder 弃用 Warning。此前整体编译 `wb-c7be937b-a677-40ed-b0f0-951ddd05c990` 为 0 Error、4 条既有 Warning；不把期间失败的旧批次改记成功。
- EditMode：每日文件/配置/定位/完整内容/故障 18 项，Step 文件 10 项，Step 生命周期 15 项，共 **43 个不同用例取得通过结果**。run `cd48da94-e845-4c99-b473-45489975b37b` 为 42/43，其中 Step 文件故障的日志样式断言未消费预期 Error；改为按唯一 runId 消费，并显式核对路径、operationId、原 IOException 和堆栈。最终每日文件 fixture run `7a5d9ce9-5abd-444c-b05e-4559d9a3d25d` 为 **18/18**，两次均 resultAuthoritative/cleanupSucceeded=true，无跳过。测试 Console 的范围覆盖完整，但阶段覆盖受清空/重载检测限制，不能宣称完整阶段证据。
- 较早定向回归包含 Core 日志兼容和 Bridge 发送边界。一次本地连接夹具在 connect 阶段超时，另一次新增测试独占读取活动日志触发真实 IO Error；改用共享读取并隔离 Bridge 测试实例，相关四项复验 run `b9f5bf21-9007-4c49-a8f5-12f512371d03` 为 4/4。未放宽生产错误、协议边界或原断言。
- Server 部署：维护 `3f409e3b-5125-4f62-8a2c-4ed5ca7ef45e` 已 succeeded，Server PID `140048→100732`，Unity PID 不变。刷新后选定入口 loadedSource=equivalent（仅这些入口，不代表全部模块/依赖）。真实三工具各执行一次，各有一条 call.start 和 call.end、相同 callId/startedAt、完整结果及非负耗时；关联 callId 分别为 reflection `call-f874db5a-d4a8-468d-bb62-4625be0d002b`、eval `call-1809dc65-0dad-4d69-be5f-77cd140571cd`、dump `call-910fe9ef-10a9-4a05-be6a-a218b312910d`。临时 execution session 已关闭。

**未通过的现场覆盖：**完整 Step 计划经官方 validate 后 start 一次，Operation `op-316b8ac9-c93e-450f-9b8f-94db9d549c81`，run `c02d4a0b2da541ad83ef757ac6d989f2`，进入 PlayMode 后发生同 PID Domain Reload（42→43），随后持续 `STEP_SERVICE_INITIALIZING`，没有恢复到执行器终态。当天 Step 文件保留任务开始、完整生效计划、步骤开始和 run.reloading 等 6 条原始记录，没有伪造任务结束。该次跨域现场验证不能记为通过；初始化根因尚未确认，本次未改写 Step 生命周期。

已向原 Operation 取消一次并通过语义工具退出本次验证进入的 PlayMode。取消未完成后，项目现有有效的自动硬停止设置触发维护 `d2a01b1a-852e-4f2c-8727-046a4bce5a1f`；succeeded、queueReset=true，Server `100732→42208`，Unity PID 未变。该维护中止了并行任务的待处理批次 `wb-dd4d2849-3128-4a5d-94a3-a5d1ecff9127`（aborted/unknown，不能算通过）。最终实时观测为 EditMode/Ready、队列完整且为空、activeCapture=0；旧 Operation 在新活动生命周期查询为 not_found，不能据此声称原任务成功或 Finally 已执行。相关问题已登记根 TODO。真实故障恢复、硬停止前未完成任务的业务结果和跨域完整执行仍未获通过证据。

## 7. 错误与警告的文件、Console 输出及高频过滤

本节方案已实施并完成分批定向验收，既有结果见第 7.4/7.5 节，本轮补漏及部署边界见第 7.6 节；不等同于全量 EditMode 或全部生产故障现场验收。真实异常和失败不能只写日志文件或状态，必须有可见 Console 错误/警告；频繁重复项适当过滤，心跳等周期流水不写日志文件。文件与 Console 是两个独立输出目的地，文件失败不得阻止 Console 输出。以下 `upilot.log`、`mcp-server.log` 为逻辑名称；第 8 节路径统一后，新默认通用文件均位于 `Logs/UPilot/`，实际按日写入 `upilot-YYYY-MM-DD.log` 与 `mcp-server-YYYY-MM-DD.log`。历史运行 Server 的旧目录不因源码修改自动迁移。

### 7.1 级别与输出目的地

真实异常、操作失败、超时未完成以及清理/持久化失败使用 Error/Exception（Unity 红色）。Warning 用于已知且可恢复的降级、部分可选结果不可用、非致命风险；不能仅因为异常被捕获、已转成结果或任务仍可继续，就把真实错误降为 Warning。正常主动取消和无副作用的预期探测不作为错误；取消过程中的清理异常仍是 Error。

| 场景 | Console 级别 | 文件与上下文 |
| --- | --- | --- |
| 构建异常、构建失败、达到期限仍未完成 | Error/Exception | `upilot.log` 记录一次失败摘要、目标平台、输出路径、耗时、请求身份；捕获异常保留原堆栈。引擎已打印的同一根因应关联去重，不能直接假定所有失败均已有 Console。 |
| Step 执行异常、断言/步骤失败、超时、Cleanup/Finally 失败 | Error/Exception | 独立 `step-YYYY-MM-DD.log` 和状态/报告保留完整过程；`upilot.log` 与 Console 保留可定位错误摘要，带 runId/operationId、步骤及阶段、错误码、异常。 |
| Step 已知且可恢复的非致命降级 | Warning | Console 和通用日志保留一次警告，完整细节仍留 Step 文件/报告；是否影响任务结果沿用该项已定义的语义。 |
| 测试运行、Watchdog、资源收尾或状态保存失败 | Error/Exception | `upilot.log` 与 Console 同步输出；保留 runGuid、清理阶段及原异常，JSON 状态继续保存，不能用状态替代日志。 |
| Flow 执行失败、关键结果/事件上报失败 | Error/Exception | `upilot.log` 与 Console 同步输出，包含 executionId/用例/事件身份；报告仍按原流程生成，不假定外层 catch 必然进入报告。 |
| Bridge 命令真实失败、编译/Editor 状态持久化及关键事件发送失败 | Error/Exception | `upilot.log` 与 Console 同步输出，包含 command/request/compile 身份；同一失败在业务边界和 SendErrorAsync 只输出一次，传输响应不丢失。 |
| 停止/恢复服务失败或超时、更新锁释放失败、资源加载/卸载异常 | Error/Exception | `upilot.log` 与 Console 同步输出；如果只是明确允许的部分结果降级，按实际语义输出 Warning，并说明影响。 |
| 日志写入器自身失败，包括 Step 文件写入/清理失败 | Error/Exception | Console 直接兜底并去重；独立文件失败可尽力写通用日志，`upilot.log` 本身失败时禁止递归调用该写入器。恢复后补一次摘要，不把文件成功当作 Console 输出前提。 |
| Server 工具执行或调用流水写入失败 | Error/Exception | Server 输出 stderr，并写独立 `mcp-server.log`，三工具详细流水仍在 `McpCalls/*.log`；不让 Python 与 Unity 共写 `upilot.log`。Server Console 与 Unity Console 分开说明，断连时不能承诺 Unity 即时可见。 |

Warning/Error/Exception 沿用已实现的 `LogToUnityConsole` 豁免；这些失败诊断不依赖 verbose/debug 开关。保留已确认的 MinLevel 语义，不借本方案扩大普通信息日志输出。只留 `ex.Message` 不足以保留异常信息，首条记录应包含原异常类型、堆栈及操作上下文；没有异常对象的失败记录实际错误码和原因，不虚构堆栈。

### 7.2 高频过滤：首条立即、重复合并、终态可见

已使用 10 秒固定合并窗口接入审计过的诊断责任边界，未对所有公开 Log 调用机械限频，未新增配置页面。过滤同时覆盖 Console 和通用诊断日志；Step 原始事件、冻结报告、测试结果及显式 Capture 证据不因降噪被删改。缓存最多 256 个根因，空闲 60 秒清除；Unity 通过 Editor update 结清到期重复计数，Server 在后续实际日志事件或退出时结清，不新增无变化轮询或后台计时线程。

1. 同一问题首次发生立即按原级别输出，保留完整上下文和异常；不能等窗口结束才输出首次错误。
2. 在组件/操作类型、错误码、异常类型及稳定根因相同的前提下合并重复项。独立用户任务按 runId/operationId 等隔离；同一后台轮询的每次 requestId 不应使去重失效。聚合记录保留首次消息、批次/任务关联身份、重复计数和首次/最近时间；每次原始响应保留独立请求身份。不得简单按任意消息前缀合并不同根因。
3. 窗口内重复只累计次数。问题持续且有新增重复时，每 10 秒至多补一条同级别摘要，写明本窗口合并次数、首次/最近时间和仍未解决；没有新事件时不周期打印“没有变化”。
4. 级别升级、根因变化、新独立任务的首次失败必须立即输出。用户任务最终失败/超时、清理结束以及状态恢复时，结清未输出的重复计数；终态不得被窗口吞掉，同一终态不重复上报。
5. 同一异常沿调用链传播时，在最接近责任边界的位置输出一次；其他层继续传递异常/结果和关联身份。发送失败响应、更新 UI、落状态文件不再重复打印同一根因；第二个独立故障（例如错误上报本身失败）另行记录。
6. 计数及去重缓存必须有界，并随任务结束/服务生命周期清理；日志输出或计数自身故障不能递归打印、重放操作或掩盖原错误。限频只影响展示/通用诊断记录，不修改实际错误计数、状态和验收结论。

### 7.3 心跳及周期流水排除

- 逐次心跳成功/失败、健康检查成功、没有状态变化的轮询与等待提示，不写通用日志文件；也不逐次刷 Console。关闭 verbose 后排除仍应生效，开启 verbose 不恢复这些文件流水。
- 连接断开、恢复、重连最终失败或后台故障影响实际操作时，输出一次状态变化/错误摘要，按 7.2 合并持续重复项。这些是故障边界记录，不是逐次心跳流水。
- 删除 `Heartbeat OK` 及逐次心跳失败的通用日志调用，不删除心跳协议、时间戳、错误计数或编译/Editor 等权威状态持久化。

### 7.4 实施与定向验证

- 先覆盖构建与 Step 的执行/清理失败，再覆盖测试/Flow、Bridge/编译/状态、服务/更新/资源及 Server 日志异常。只修改必要入口和直接回归测试，不引入另一套执行器或全局 Unity Console 回写。
- 使用隔离时钟验证首次立即输出、窗口合并与计数、不同任务/根因不误合并、升级/终态不丢失、恢复结清、缓存有界。验证文件不可写仍有 Console 错误且无递归；验证 verbose 开关两态都没有逐次心跳文件记录。
- 在规范项目运行最小定向测试，覆盖 Console 开关两态、原异常堆栈、独立文件/主日志去向及 Step Cleanup/Finally。框架诊断不得递归产生新失败或重放清理，也不得通过忽略全部 Error、关闭 Console Policy、隐藏已有捕获证据来使验收通过。
- 已实施：`Logger.Diagnostics.cs` 的失败/警告边界及有界聚合；构建终态、Step 执行/Cleanup/Finally、测试运行与清理、Flow、Bridge 命令及关键状态、编译状态持久化、服务停止/恢复、更新锁、资源查询和 Server 工具/文件故障。命令处理器继续传递原异常对象，保留原响应与业务状态；正常取消不误报 Error，已知可恢复降级保持 Warning。文件写入器故障使用不递归的可见通道，恢复结清重复计数。实际根 `AGENTS.md` 已更新，本任务没有修改 Agent 模板。
- 已删除 Bridge `Heartbeat OK`、逐次心跳失败和 Flow 每 10 秒无变化等待提示；协议、心跳时间戳、计数和权威状态持久化保留。Server 使用独立 stderr/每日主日志，不能把它们表述为 Unity 红色 Console。
- Python 定向验证：`test_diagnostic_logs.py`、`test_daily_logs.py`、`test_mcp_tool_call_logs.py` 共 **48/48 通过**，2 条既有 websockets 弃用警告。包含假时钟重复聚合/任务隔离/终态、两个 handler 一致过滤、原始堆栈，以及真实子进程双输出和独立调用文件故障向 Server 主日志/stderr 报告；故障注入退出摘要是预期输出。
- Unity 早期定向 run `72b6f356-f541-4316-adfb-bef6a39aa9cd` 为 **267/272**，权威结果及清理确认，但 `sourceUnchanged=false`，不算包验收通过。5 个失败已修正：3 处新增可见 Error 的精确预期、Step 默认诊断样式，以及停止观察器保持原 `reason=ex.Message` 并独立保存原异常。新增 Execute/Cleanup 生产默认双输出、文件恢复计数、聚合锁与文件锁隔离测试；后续结果见下文，未忽略全部 Error、关闭 Console Policy 或放宽业务断言。
- 首轮证据：`Tests~/UPilotTest/Log/UPilotAcceptance/1792055235620_req-a90ea7b9-b77d-46e1-bbea-8688133a6488/summary.json`，1972851 bytes，SHA256 `e7a1f5fcc33470731e07b952053bebac051d9825c2c588cc5ffaf49da971e753`。较早整体 WriteBatch `wb-c7be937b-a677-40ed-b0f0-951ddd05c990` 关联编译通过（0 Error/4 既有 Warning）；后续批次不能借用其结果。
- 历史阻塞：并行维护中止过批次 `wb-dd4d2849-3128-4a5d-94a3-a5d1ecff9127`；后续自动批次反复被历史 `exitingPlay` 拒绝。`wb-90501728-cea5-4c35-acc3-f4af65ae68ab`、`wb-269386b5-4f1b-4230-93e1-d4cbd3e4a46c`、`wb-0d07fe84-f549-4df4-8f7a-eb07227068ae` 最终均 timed_out，无关联通过证据，不因后续恢复改写结论。17:33 预检曾以 `ImportNotVerified` 拒绝启动 TestRunner；当时发现 276 个定向用例。预检产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1792056788633_req-dd9c6b70-a5ff-47d1-b690-b9ed74af9356/summary.json` 的 668310 bytes、SHA256 `25a3555d7b6fa46d36adea21d2859214115be8718949507cd25b960dc38456dd` 已本地核对。第 7.5 节修复及用户触发原生导入后，原有效批次取得关联终态，再恢复定向验收。
- 恢复后的合并定向 run `f2b7366d-796b-471f-9628-0883081aebc2` 为 **351/357，通过 351、失败 6**；权威结果、清理、测试身份和源码未变化均确认，但 `acceptancePassed=false`。5 个失败由测试以 `File.ReadAllText` 读取活动主日志时短暂阻断后台写入引起，已改共享读取 `FileShare.ReadWrite | FileShare.Delete`；另一个旧执行器替换测试精确预期两个责任位置各一次的注入时钟异常，保留原替换/状态/异常断言。产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1792057559498_req-53edaf2a-cbf4-40ab-bc11-d8a812986af4/summary.json`，1910844 bytes、SHA256 `5c45bb71ea5f64ecb4d06560f22a301c596f885bfbcd72a5ebb9c07e30d2c8be`，已本地核对。
- 最终最小直接回归 **118/118 通过，无失败/跳过**：run `770e4edc-ba60-4c1a-8f5b-5dc3193774ea`，覆盖上述 6 个失败、日志生产双输出/恢复/隔离、Step 日志与服务、Bridge 公开签名、编译保护及模式恢复。`resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true`。产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1792057893017_req-788d82c4-0db5-4427-87f9-cf5866957aaf/summary.json`，988642 bytes、SHA256 `998865ed7053eee8296781b2e0310037d84bfdec07b83b173e3a9d4690d49ce2`，已本地核对。按精确 testName 核对，两轮累计 357 个不同定向用例均取得通过结果；这不是单轮 357/357，也不是最后修改后重跑所有 357 项或全量 EditMode。
- 部署边界：最近 Server PID 42208 的选定入口 `_call_tool_with_strict_arguments` 对磁盘为 `loadedSource=different`；独立子进程测试通过不证明当前 Server 已加载新实现。最终应分别报告源码/测试与现网加载状态。未执行真实 BuildPlayer、全量 EditMode 或真实更新锁/服务停止故障注入。

### 7.5 EditMode 被历史 exitingPlay 拦截的修复

用户于现场确认已是 EditMode 后要求修复。根因是 2026-10-15 17:15:27.453 +08:00 的退出记录没有后续 edit 终态，编译策略保守采用该历史过渡，而稳定恢复只覆盖进入播放。

- 已在原 `RecoverOnEditorUpdate` 补齐退出恢复：同一 session/transitionId 连续两次 !isPlaying/!willPlay/!busy 才生成 edit；忙碌、重新播放、会话或切换身份变化重新计数；保留原请求归属及历史，继续拒绝真实未完成切换。没有按超时直接认定完成。
- 编译模式/busy 拒绝统一经命令响应输出 Warning，移除 `TryBeginCompile` 的重复直接 Console；按 WriteBatch 或独立 request 分组，成功接纳时结清重复数。保留公开 SendErrorAsync 的 5/6/7 参数签名；主线程同时捕获拒绝码和模式，防止多个并发请求覆盖共享状态。原始响应、编译快照、错误验证和批次期限保持原流程。
- 新增退出终态与归属、首次观测仍拒绝、模式/busy 中断、session/transition 更换、非法进程/项目身份和时间回归；新增编译拒绝同批次聚合/独立请求可见回归。原进入恢复断言保留，独立只读复审未发现新增阻塞问题。
- 三个原登记批次均 verified/passed，`terminal/correlationVerified/errorsVerified=true`，每批 0 Error/0 Warning，验证时间均晚于登记：`wb-abffd94b-78b9-4f21-98b9-0e53aff43086`（created 1792057177548，request `req-07fd5574-04cd-40b1-9d12-0bef10feda21`，verified 1792057497595）；闭包竞态修正 `wb-5027861a-6f29-4ae4-887f-1db7b14811ec`（created 1792057289113，request `req-c297b9c0-db49-4945-83e8-9b4ef4771c12`，verified 1792057515118）；测试夹具修正 `wb-386b9465-d358-4d43-8658-6cda39e4fb86`（created 1792057793907，request `req-1a362874-8db0-4c04-825b-a40c97365db0`，operation `0783e1f964914744868834e932c37775`，verified 1792057831978）。后续并行编译不替代这些原批次证据。
- 首次加载曾被旧 C# 自锁；用户切回 Unity 并回复“已开始编译”，Unity 原生自动导入加载修复。随后真实 ledger 于 **2026-10-15 17:44:44.972 +08:00** 补记 transition `400b57c674fc462abd21cac03fc67c7b`、`exitingPlay → edit`，新 session `591710dc1f854260a05e49b9201d06ad`，保留原 `unity_playmode_stop` 的 request `req-7c8d31ee-f0ed-4f6b-a9f0-68aac73f25c1`、command `cmd-c58268f4-7b73-456a-a0d6-3d6660370b08`。现场权威状态已恢复 EditMode/Ready；收尾只读复核为同一 Unity PID 127400、domainGeneration=52，当前队列为空。没有绕过 guard、删除 ledger、伪造回调、重放编译或重启服务；漏掉原终态通知的具体时机仍未证实。
- 最终 118/118 定向权威验收通过，包含 30 个精确生命周期恢复用例及 5 个直接相关 fixture；清理、测试身份、源码未变化均确认，完整身份与本地核对 hash 见第 7.4 节。测试 Console 分阶段覆盖标为 partial，不能据此声称全范围无错误；编译 0 Error/0 Warning 来自上述关联终态。未运行全量 EditMode，也未额外重演一次完整进出播放现场。

### 7.6 续开发补漏与定向复验（2026-10-15）

本轮只补齐已确认的责任日志缺口，未改模板、维护授权、任务状态机、取消语义或超时期限。

- Server 工具包装统一解析 structured/dict/tuple/content 结果，保留错误详情中的原异常类型、消息、多行堆栈和任务身份。只读 status/wait/result 按响应或请求内真实业务身份聚合；独立 start/execute 继续按调用隔离。`unity_compile_wait` 没有 compileRequestId 入参，只能采用响应中的真实身份，不猜测全局当前编译。后台 task 失败、超时、异常和测试状态保存失败在生产者边界立即输出 Error；轮询不重复报告，成功恢复/正常取消结清原 scope。
- Unity 命令路由与主线程排队的跟踪只记录状态，由 Bridge 责任边界报告原异常，避免同一次异常重复红日志；真正取消保留取消通道。活动清理失败保留原堆栈及动作身份。正常取消 Server 重启不再产生 Error；更新失败按现有 OperationId 隔离，新操作、结束和恢复结清累计。
- 服务维护 Journal 读写、恢复、失败和真实超时输出 Error，保留路径、maintenanceId、InnerException 和原堆栈；已知可恢复 probe/fallback 使用 Warning，正常取消不误红。既有故障注入只补精确日志预期，没有忽略全部 Error、放宽业务断言或改变维护流程。
- Python：6 个定向文件首轮 **146 passed**；补充 operation_wait/test_results/compile_wait 的 3 个身份映射用例后，修改文件 `test_mcp_tool_call_logs.py` **41 passed**。两轮累计 **149 个不同定向用例取得通过**，不是单轮 149/149。保留 2 条既有 websockets 弃用警告，文件故障注入的退出摘要是预期诊断。
- 产品及维护测试最终编译批次 `wb-3f004589-3b8d-4a83-8d8b-0eb730877ccd`（created `1792059813779`，request `req-c97c0bfb-b1c8-496d-b939-c438b4a2a36a`，operation `8cbd8e7af3ce449a8b0333aee9f5595a`，verified `1792059838335`）关联通过，0 Error/1 条既有 AssemblyBuilder 弃用 Warning。历史批次 `wb-f11d50d9-bd0a-477f-90bd-c786b796e5f0` 与 `wb-434bccbc-1051-4a01-868d-84b1ab34715b` 曾因新增测试误写 GetActive 而失败；后续改为 GetContext 并关联通过，不改写旧失败记录。
- Unity 首轮 Task `task-3d42510b-af8b-441c-b0bb-c94fea27e220`、run `9ec2e47a-4bc5-4c76-931f-e80f02200528` 为 **90/93**，权威结果、清理、身份和源码未变化均确认，但验收失败。两项夹具漏声明正常生命周期结束 Log；一项通过 NUnit ThrowsAsync 观察取消时被转换为 TaskCanceledException。仅增加精确普通日志预期、改为直接 await/catch 后核对同一原异常对象，保留全部状态和 NoUnexpectedReceived 断言。首轮产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1792060105232_req-a3ce3bcb-4acc-4b25-9950-1033b66d6b2d/summary.json`，1046347 bytes、SHA256 `8bddfc8c5e7b979602e5147036714c430280332b99ccc8aa6bc755e8295e1bc6`，已本地核对。
- 夹具修正批次 `wb-960cfdc3-070f-4930-ab51-dd47b0310cba`（created `1792060286748`，request `req-a1d5d78d-fea4-47f4-94d6-ba9734fe5c3e`，operation `9e43a14b496c4274a1fa5690eeb21476`，verified `1792060301774`）关联通过，correlationVerified/errorsVerified=true、0 Error/1 条同一既有 Warning。随后 Task `task-96ad1c64-61bb-4592-9862-8acb17c511f3`、run `39a50376-be10-441a-ad75-d13849e3c6f7` **19/19 通过**，覆盖完整 CommandPhase fixture 及两项取消重启用例；resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true。产物 `Tests~/UPilotTest/Log/UPilotAcceptance/1792060340162_req-03aaa263-b802-4f3b-a209-1647e2bff4fa/summary.json`，833576 bytes、SHA256 `19549a5c4e7b9de174281abe4ed011ebf000e06a2044697676ef33599f3b5c6e`，已本地核对。本轮两次按精确 testName 累计 93 个不同定向用例均取得通过，不是最后修改后重跑全部 93 项。Console 阶段证据仍有 partial 边界，不据此声称全范围无错误；未运行全量 EditMode。
- 部署仍未完成：刷新前已确认精确项目、有效独立 AI 维护授权、队列/Test/Capture/WriteBatch 空闲。维护请求 `5b118248-45ef-4679-a4fd-327dd64ba42c`（command `cmd-f71b07cf-59b5-4e06-a6e3-fba0a2f8e638`）被 `SERVICE_RESTART_IDENTITY_CHANGED` 拒绝，changed/sideEffectsMayHaveOccurred=false；Unity 原栈证明命中 RequireGate，尚未进入 Journal.Accept 或服务停止，不能把 commandSubmitted=false 解读为没到 Bridge。fresh 状态的 Server PID 42208、Bridge session 和项目均匹配；OS 命令行为旧 `--log-file …/log/mcp-server.log`，当前匹配要求 `Logs/UPilot/mcp-server.log`。据源码和 OS 证据推断 ProcessOwnership 因路径不同而不满足 CurrentUPilot，拒绝时缓存枚举未直接读取。没有重发维护、放宽匹配或通过 shell/reflection 绕过。辅助只读反射核对曾在绑定阶段拒绝（req `req-89b48740-ce87-47da-ac5a-d8c50f4b1e16`，methodCallCount=0），未执行目标；该外部诊断不冒充产品故障注入或成功证据。
- 当前 Server 的工具包装、后台 task 入口仍为 selected-entry loadedSource=different，Python 子进程测试不证明运行进程已加载修改。人工结束经核对的旧进程并从 Unity UPilot 按当前参数启动后，仍需重新核对项目、PID、选定入口来源及真实只读调用；无需重启 Unity。最终定向验收后队列完整为空、activeCapture=0。旧产物不迁移、不删除。

### 7.7 重启后持续“获取状态中”的修复与定向验收（2026-10-15，已完成）

本节记录用户随后执行停止/启动后的新问题，不改写第 7.6 节已经完成的日志验收及当时部署结论。本轮规范目标仍为 `D:/upilot/Tests~/UPilotTest`；初始现场为旧 Server PID `42208`、Unity PID `127400`、Bridge 断开。本轮修复、关联编译和最窄定向验收现已完成，后续已取得新服务的权威 EditMode/Ready；初始断开及失败记录继续保留。

- 已确认的链路：旧进程参数仍使用 `log/mcp-server.log`，新的停止候选筛选使用当前默认 `Logs/UPilot/mcp-server.log`，因此在端口归属校验中拒绝停止。失败收尾又把真实身份异常统一记为 Transient；主窗口先返回未确认健康视图，绕过已结束的停止结果。随后显式启动仍按存活 PID 合并，或因缓存 IsRunning 跳过 StartServer，没有重新完成身份、Bridge 和实际只读往返验证，周期健康观察仍处于暂停状态，造成持续“获取状态中”。这不是已有证据证明 Unity 卡死。
- 已完成的最小修复：可信、已核实的冻结启动记录提供唯一、绝对且位于同一精确项目内的 `--log-file` 路径，停止候选、完整启动身份及最后停止复核沿用同一冻结值；无可信记录时仍只接受当前默认路径，不添加旧/新双路径白名单。生命周期收尾保留原异常分类和堆栈，正常取消不残留健康错误。主窗口对同一项目、服务 lifetime、Unity PID 的失败/超时停止记录持续显示“停止未确认”，不再被瞬时健康未确认状态遮盖；普通健康超时仍不据等待时长推断故障。显式 Start 总调用现有 StartServer，由其按当前实际状态进行有界认领/验证。
- Reload 证据与修复：用户日志在 **2026-10-15 19:07:25、19:07:47 +08:00** 仍显示自动修复归属检查失败；停止记录 `5066392c854b463b8210da28ea88d2c3` 的目标 PID 为 `0`。结合恢复源码，已定位到恢复终态 stop 后提前 return、未恢复冻结 launchState 的缺口；后续 Reload 还可能以尚未载入的空状态覆盖原启动记录。现已独立恢复终态 stop 与冻结启动身份，并防止 Reload 前的空状态覆盖。历史启动记录仅提供精确候选，必须在有限 8 秒内重新取得当前 OS 活体证据、确认原 servicePid 与创建时间后才持久化认领；不直接授权停止未知进程、不重放旧操作。
- 显式认领及终态修复：现存服务的新显式 attach 使用单独的有限验证窗口，保留原启动、维护及停止操作的 deadline 和历史终态。实际认领失败保留真实异常，并将终态绑定当前服务身份和状态代际；只有仍匹配当前身份/代际的失败才能优先于 Unconfirmed 展示，旧失败不能遮盖新服务恢复。相关 9 个直接回归已纳入最终定向集合。
- 验证流程：初始保存批次经自动 WriteBatch 登记后因 Bridge disconnected 延后；历史终态不冒充后续修改的编译证据。连接恢复后继续观察原批次关联结果并读取结构化编译错误，没有为自动登记批次另开手动 sync/compile。本轮停止失败分类、终态展示、正常取消、精确项目/lifetime/PID 隔离、冻结身份恢复及有界认领等回归均已取得下述最终权威结果。
- 后续现场观察：旧 PID `42208` 和启动器 `154808` 已退出；新 Server PID `163460`、启动器 `152080` 使用当前默认 `Logs/UPilot/mcp-server.log`，Unity PID `127400` 未变。原生重启记录 `restart-e455936f6bcb4fd899cf58238ac2e1cc` 为 succeeded，health/项目/Bridge/部署/真实只读验证全部通过；该记录的 old_identity 为 no_old_process，不能据此声称本轮代码实际停止了旧路径进程。Server 选定入口 `loadedSource=equivalent`，仅证明所选入口与磁盘等价，不鉴证全部模块或全局状态。
- 编译记录：最初四批 `wb-9738d555-90fb-4b4c-930d-512dcd8e94b6`、`wb-68d91816-7950-4507-86f6-2d8593a26a14`、`wb-90aec43b-7fe1-48f7-a24e-bbb75e864b59`、`wb-fd68df1c-666e-436b-b694-c186400396e8` 因 disconnected 超过原 600 秒而 timed_out；中间共享轮 `req-660a60ba-1e6e-44c1-be6b-0b23cc5857b9` 的 6 个成员最终 aborted，不改写为通过。后续 `wb-58c708dd-684a-450a-8a12-db896d947241`（created `1792063187540`、verified `1792063209482`）、`wb-54fec46e-5190-49fb-8130-05ee8e96dc3c`（created `1792063216783`、verified `1792063235116`）及 `wb-9d4afc76-15db-45c6-ad38-4fc12d993919`（created `1792063236132`、verified `1792063250911`）均取得原批次 correlationVerified/errorsVerified/terminal=true。最后一批对应 `req-f4406163-d0fa-4afa-9232-c6f607c504e4`，结构化结果 0 Error/0 Warning。一次登记在端点切换时传输失败；只读持久记录确认无最新测试哈希对应批次后，仅登记尚未接收的最终测试，不重放维护或手动编译。
- 首轮定向 Task `task-61d70128-307e-4558-b7bf-038757f0424e`、run `30bcd66a-5b30-4cf6-a09c-84a98addd990` 为 **215/220，失败 5、跳过 0**；resultAuthoritative/cleanupVerified/testIdentityVerified/sourceUnchanged=true，acceptancePassed=false。两项 Reload 终态测试未声明新的 Error，三项停止测试未声明正常生命周期 Log；只补精确身份/原因/级别的日志预期，保留业务和 NoUnexpectedReceived 断言。报告 `Tests~/UPilotTest/Logs/UPilot/Reports/Acceptance/1792063325193_req-af9ba569-959f-4a65-912b-d60cd56110b9/summary.json`，1174935 bytes，SHA256 `b288cd8e4f677c8c31d28b6726148487fff742d545ac06a5b1aa0fb05df7e194` 已本地核对；原生失败 XML 已按同一用例/数量/时间核对并保存在同目录 `native-test-results.xml`，SHA256 `3a5e97a130a219eb97fe70d0aa57548fda8a96908f0bdd34a6d7d0ab9e563141`。Console 边界 coverage=partial，不将未匹配到 Error 误报为全范围无错误。
- 最终关联编译：原批次 `wb-a1840424-0f08-49ae-b7b1-ad3dde1180e3`（created `1792063701440`、verified `1792063720921`，request `req-9c227107-ad09-4712-8cb6-bf3aad0d11ad`，operation `9c67416977a545479cb855cd2bcb559a`）terminal/correlationVerified/errorsVerified=true，**0 Error/4 条既有 Warning**（3 条 Flow UAC1009、1 条 AssemblyBuilder CS0618）。原失败、timed_out、aborted 与夹具漏声明日志的历史均保留，不回写为成功。
- 最终定向验收：Task `task-24628af8-4cdf-4bc0-b663-689f5c3ceb02`、run `117f417c-5554-4030-aa31-fff9ee46b207` 为 **229/229 通过，失败 0、跳过 0**；resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged 全为 true。范围为完整 ServerLaunch、InitialServiceRecovery、ServiceStopObserver、ReloadObservation 四个直接相关 fixture，加一个 DailyLog 默认身份用例，未运行全量 EditMode。报告 `Tests~/UPilotTest/Logs/UPilot/Reports/Acceptance/1792063765902_req-ceb814df-661d-4b71-8f0b-40c214c9346b/summary.json` 已本地核对：1231701 bytes，SHA256 `759c8fa95d10cf3d870d31f4c307906c511d02c98c774ee02ac3113352c28466`。
- 当前部署与界面证据：fresh 状态确认精确项目 `D:/upilot/Tests~/UPilotTest`、connected/serverReady=true、authoritative=true、isStale=false、EditMode/Ready、domainGeneration=71；服务 PID `163460`、启动器 PID `152080`，Unity PID `127400` 未变。队列 dryRun complete=true、issues=[]、items=[]，active Capture=0。语义截图实际确认主窗口为绿色“已就绪”，不再显示“获取状态中”：snapshot `snapshot-5da0b5489c3d43ba97c7a063898c2dfc`，PNG `Tests~/UPilotTest/Logs/UPilot/Diagnostics/Snapshots/20261015/snapshot-5da0b5489c3d43ba97c7a063898c2dfc/editor-window.ee018501.color.png`，SHA256 `3f720f7b12f72b55f3c5729c5fcca19b72b9f165486cc28f9efa187caad07d20`。官方最终摘要再次确认 229/229、cleanup succeeded、resultAuthoritative，只读代码审查及识别既有 CRLF 的 diff --check 无新阻断；未修改源码换行来消除默认检查误报。选定入口 loadedSource=equivalent 仅证明该入口，不扩展为所有依赖模块的加载鉴证。旧进程退出不属于本轮实际停止旧路径服务的验收证据；Console coverage 仍为 partial，不宣称全范围无错误。

## 8. 默认输出路径统一实施（2026-10-15）

已按第 1、2 节现行位置调整 C#/Python 默认路径、写入器、列表/诊断读取和打开入口；新默认根目录固定取所属 Unity 工程。ServerLaunchSpec 和启动/停止候选匹配要求新逻辑路径 `Logs/UPilot/mcp-server.log`，拒绝旧目录、日期文件、外来项目及重复参数。每日定位无旧固定文件回退。Flow 聚合历史、Snapshot manifest 和 Console 默认发现均限定新目录；E2E 默认归属执行项目，spec、相对 baseline 和显式 artifactDir 语义保留。旧文件没有迁移或批量删除，显式旧路径字符串仍按用户原值处理。

### 定向验证

- Python：本次定向集合共 239 个用例，跨轮均取得通过结果，无跳过；不等同于全量回归或单轮 239/239。首轮 96 项通过，第二轮原 142 项有 1 个 Capture 夹具使用旧默认目录而失败；修正位置并增加 Snapshot 比较默认/显式路径参数化后，相关 123 项复验通过。其余 20 项已在第二轮通过。Server 实际子进程日志入口另补新逻辑路径、旧目录不变的断言，单项复验通过。保留 2 条既有 websockets 弃用警告。
- C#：规范工程 `Tests~/UPilotTest`，Unity `6000.6.0a2`。生产路径和关联测试 WriteBatch `wb-bed6faac-9634-4013-8655-7bd0533873b8`、`wb-a37b8b26-5a54-4166-a6cd-8314b29533bf` 均 verified（0 Error/4 既有 Warning）。Snapshot 相对字段夹具修正 `wb-51cf93aa-e635-4ea3-81ff-499fa2892720`（compile `934aad6c8df4402b8931831fbfd1a688`）、Flow 相对截图链接及 Capture 目录修正 `wb-dd285545-4263-44e0-82f9-9dcf514e6341`（compile `601f317f39a644d7a4afd1d971c96284`）、最后清理夹具规范化 `wb-52762277-54ad-44be-a794-64efae3d692f`（compile `b433eb973c0d4a0684527bc8b5c87994`，created `1792059049340`，verified `1792059065541`）均 terminal/correlationVerified/errorsVerified=true；后两批及最后一批均 0 Error/1 既有 AssemblyBuilder Warning。
- EditMode：46 个当前用例跨轮取得通过结果，无跳过。包含默认路径/cwd/旧目录隔离、每日日志、Server 启动及身份、停止候选、Flow 历史与绝对/相对截图链接、QuickDebug、Step 报告附件和 Capture 的索引/恢复；没有运行全量测试。初轮 run `92055da3-d0b5-4ce8-bf9a-eaf94d5c407b` 为 38/39，Snapshot 夹具误用绝对持久化字段，修正后 run `3cb834e7-b8fa-4dcd-a55e-0231f4d28a95` 为 4/4。run `373e39e0-82d2-4902-84e9-26a25c4ffaf8` 为 2/3，Capture 原夹具目录改后需继续规范化分隔符；run `f196eabf-77b4-4d92-8991-019b3151905f` 为 Flow 4 项通过、Capture 2 项清理失败。最后仅修正测试目录比较，Capture run `c7c3375d-e577-4369-8c64-fe5ff6e90e9c` 为 2/2。各次结果均权威且 TestRunner cleanupSucceeded=true；失败的测试自身清理不能据此算成功。Console 范围覆盖 complete，分阶段覆盖 partial，不能声称全阶段无错误。
- Capture 收尾：失败夹具留下本次会话 `console_20261015_180814_727_d569be63`，用户明确授权仅停止该会话并保留文件。旧 Server 原生停止预检因路径查询返回 unknown，改走已检查的同一 Unity 服务精确入口；旧式参数绑定未执行，使用有类型参数实际调用一次后，返回编码为 RESULT_NOT_INLINEABLE，未重放。随后原生精确 status 确认 active=false、finishedAt=`1792059119046`、sha256=`e4f76a3170391216c8daf0e265ac11716825be43d9f88ddfbb19e5ffa4224a26`，活动会话数为 0，文件保留。
- 文档/集成：更新清单、Server 开发说明、Flow/Step 路径说明和仓库忽略规则；不改用户工程忽略规则。Skill 文档随路径更新到 skillPackVersion=69，agentRulesVersion=58 不变；源生成/check/source validation、五目标同步和两份 installed validation 均通过。实际重复同步全部 current，受管文件集合/hash/纳秒 mtime、备份目录集合均未变，规则块外字节保持一致。

### 现场部署限制

第 8 节路径改造最初交付时未重启 Server 或 Unity：当时 PID `42208` 仍使用旧 `--log-file …/log/mcp-server.log`，选定 loadedSource=different，上述路径测试不能证明当时在线 Server 已加载新实现。该历史边界保留。

后续第 7.7 节修复与定向验收已完成，新 Server PID `163460`、启动器 PID `152080` 使用当前默认 `Logs/UPilot/mcp-server.log`；Unity PID `127400` 未重启，fresh 状态已确认精确项目、connected/serverReady 和权威 EditMode/Ready。新启动及无可信冻结记录的进程继续只认当前默认；已验证冻结记录恢复时沿用其中唯一的同项目绝对日志路径，并经精确 OS 身份复核，不采用旧/新双路径白名单。选定入口 loadedSource=equivalent 仅覆盖该入口。旧 PID 已退出，但重启记录 old_identity=no_old_process，没有本轮真实停止旧路径进程的证据；不能把现场新服务成功外推为这一迁移场景已在线覆盖。本次没有迁移或删除旧产物。
