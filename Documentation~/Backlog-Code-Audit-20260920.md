# TODO 清单代码核对与优先级

核对日期：2026-09-20。

## 范围与判定

- 基线为 `D:\upilot` 当前工作树，HEAD 为 `8470791902ba9b0b5dbff1433cfeaa4f9d543d0d`，包含任务开始前已有的未提交修改。源码存在不代表已发布或已被客户 Editor/Server 加载。
- 检索仓库内 TODO 文件，并按根清单的来源约定读取三个关联项目清单：`TODO_UPilot.mcd`、`Documentation~/TODO_UPilot_Integrated.md`、`D:\MA\xclient\TODO_UPilot.mcd`、`F:\xclient2\TODO_UPilot.mcd`、`F:\xclient3\TODO_UPilot.mcd`。测试脚本、缓存、历史 acceptance JSON 不另算 TODO 文档。
- `TODO_UPilot_Integrated.md` 是历史快照；三个客户清单中的通用问题按根条目去重。另参考 `P2-Development-Plan-20260915.md` 的验收范围，不能把方案当成实现。
- 本轮是静态源码和测试定义核对，未执行 Python/Unity 测试、未连接或改动客户 Unity、未重新校验历史产物哈希、未发布。下文历史通过均只是文档记录，不是本轮实测。
- 优先级沿用根清单 P0/P1/P2/P3；同级按身份安全、结果可信度、调用频率排序。编号 A/B/X 仅为本报告索引，不新建产品 backlog ID。
- “待开发/部分实现”“已有实现但待验收/部署复核”“应更新文档状态”分开。不能用标题数量或旧的 78/83 项统计估算剩余开发量。

规范工程配置为 Unity `6000.6.0a2`，manifest 本地引用 UPilot；包版本 `0.3.32`。这不是任何外部项目安装版本的确认。

## A. 有代码缺口或需求边界待处理

### P0

| 编号 | 唯一事项 / 原条目 | 源码依据 | 未完成工作与排序理由 |
|---|---|---|---|
| A01 | 同工程 UMP Profiler 子进程抢占主 Editor 会话 | [server.py:497](../upilotserver~/src/upilot_mcp/server.py#L497) 的 `session.hello` 接受新 processId，随后 `on_hello/reset_editor_session`；540 行仍为 `latest connection wins`。`UPilotBridge.cs:782` 握手未携带主 Editor 候选角色裁决。Hang 进程发现的 Worker 排除不是握手隔离。 | **先做**主进程角色校验、权威 session 绑定、辅助进程拒绝/隔离和批次恢复测试。否则错误进程会影响状态、编译、测试等共同基础，其他验收证据也可能不可信。 |

### P1

| 编号 | 唯一事项 / 原条目 | 源码依据 | 未完成工作 |
|---|---|---|---|
| A02 | Hang dump 有界返回与中断恢复 | `domain/analysis_service.py:717` 已严格限制 `mini/heap/full` 并做空间预检；850 行仍在单次请求中 `await asyncio.to_thread(_write_windows_minidump, ...)`。`mcp_tools/analysis_tools.py:56` 无采集作业查询参数。 | 不重做已交付的 2 GiB 余量保护。剩余是可持久查询的 capture identity、等待窗口、进度/部分文件和客户端失联恢复；兼容名当前被拒绝，不应写成“已安全映射”。需核对安装版大体积 MiniDump 现场。 |
| A03 | 场景查询覆盖 HideFlags 隐藏临时根 | `Editor/Core/UPilotGameObjectService.cs:286` 枚举后直接过滤 `go.hideFlags == HideFlags.None`，公开查找参数无 `includeHidden`。 | 增加显式隐藏对象查询、覆盖范围/遗漏说明及可直接复用的对象 ID；现在 `includeInactive=true` 不能解决隐藏对象漏报。 |
| A04 | 测试/编译 Console 错误来源归因 | `UPilotConsoleService.cs:81` 搜索请求与 `ConsoleLogEntry` 只有类型、文本、index/timestamp 等；未包含 runGuid、compileOperationId、稳定运行区间。Test/Compile 服务没有配套的 Console sequence 归因链。 | 记录运行前后区间，区分测试内、编译期、既有与 unknown；公开堆栈可用性和归因置信度。持久 Capture 的 sequence 查询已经存在，不必重建日志系统。 |
| A05 | 自动推进验收的有限授权闭环 | `UPilotSceneService.cs:217`、`UPilotTestService.cs:405` 和 `domain/test_service.py:163` 已接入场景策略。**但 `test_service.py:748` 明确禁止验收从 active 列表推导 force-stop 授权，返回 `automaticForceStop=false`。** | 根 TODO 的“已实施逐个自动 force-stop”与当前代码及捕获所有权规则不一致，不能列成纯验收项。先明确并同步契约，保留未知 owner 不自动停止的保护；之后只验收已批准的场景/模式策略和精确会话人工处置。 |

### P2

| 编号 | 唯一事项 / 原条目 | 源码依据 | 未完成工作 |
|---|---|---|---|
| A06 | Selection 清空返回错误业务状态 | `UPilotSelectionService.cs:137` 清空后返回 `new GenericOkPayload()`；`UPilotProtocol.cs:739` 的 `ok` 无默认 true，且含无关 `input` 字段。 | 修正成功/失败真值，并返回 before/after selection、active object、changed/no-op。这是当前源码可直接确认的回执缺陷，不只是客户部署问题。 |
| A07 | 破坏性对象写操作的目标化回执 | `UPilotGameObjectService.cs:419` 删除只返回 `GenericOkPayload { ok = true }`；相对地 `UPilotComponentService.cs:303` 已返回 target、before/after、sceneDirty、requiresSave。 | 缩小为 GameObject 删除等仍用泛化回执的入口；不要重做 component.remove。补精确目标、变化核验、保存需求与异常后的副作用说明。 |
| A08 | Console 无匹配时扫描游标不推进 | `UPilotConsoleCaptureService.cs:859` 的 `nextSequence` 无匹配时回退到请求 `afterSequence`；881 行仍返回 `scanComplete=true`。 | 区分扫描游标和最后匹配游标，保证空匹配、分页、追加日志均可安全续读，并同步 Agent 用法。该子项位于“已完成”的大日志性能父项下，不能漏掉。 |
| A09 | 登记前 Unity 自动编译的输入归属复用，WP-05-C | `domain/compile_service.py:106` 明确没有 complete input manifest，返回 `reuseDecision=unattributed_auto_compile`、`inputCoverageVerified=false`；`resource_service.py:344` 等待自动编译结束后仍需授权批次编译。 | 拒绝冒认已有实现；“证实覆盖后仅编译一次”尚未实现。需要可证明的输入覆盖，证据不足继续保守拒绝，不能仅用 mtime 或当前 hash 认领自动编译。 |
| A10 | UP-025 TypedValue 深度/噪声消除 | `UPilotExecutionService.cs:735` 有 JSON 结构预检，但 739/769/816/1021 行仍调用 `JsonUtility.FromJson<...Envelope>`；`ExecutionCore.cs:43` 的 `TypedValueSpec.items` 仍是递归可序列化 DTO。 | **部分实现，不宜仅标“待验收”**：预算/重复字段/数组形态检查已存在，但尚未从源码消除原先触发深度警告的递归解码路径。需用实际公开入口验证两次 PlayMode 的无噪声和不截断；若仍触发，复用已解析数据替代递归 JsonUtility 解码。此处不宣称本轮已运行复现。 |
| A11 | WindowFailureForensics 的原生空壳窗口增量 | `UPilotWindowHistory.cs:183` 记录 managed 窗口 opened/closed-or-lost；194 行明确原因是下次采样未观察到。未发现 `orphanedNativeHost` 或等价托管 pane 与残留原生宿主关联。 | 生命周期历史已实现；剩余是 DestroyImmediate 后 native ContainerWindow/GUIView 空壳与正常 Close 的区分，不应把“有窗口历史”当成该新增现场已覆盖。 |
| A12 | UP-018 HDRP Depth、多 Display GameView，WP-14 | `UPilotSnapshotService.cs:919` 拒绝非 Display 0；1239 行仅支持 Built-in/URP depth，其他管线显式 unsupported。 | 尚未支持的扩展能力。先按具体 Unity/管线版本做可行性探针，再实施并验收；探针完成不等于支持。 |
| A13 | UP-016 / UP-020 文档、外部 Skill 与证据来源一致性，WP-12 | `scripts/check_release_quality.py:331` 已检查 Registry、声明 ID、归档、证据和安装 manifest；592 行缺少安装来源时保持 unknown。当前根 TODO 的旧活动表、条目正文、顶部证据仍互相矛盾。 | 不重建现有检查器。剩余为稳定 ID/状态迁移、历史记录与当前结论分离、安装/客户端来源核对，以及外部 Perception Skill 工具名迁移或明确不支持。不能要求 UPilot 实现其他工具包的同名接口。 |

### P3

| 编号 | 唯一事项 / 原条目 | 源码依据 | 未完成工作 |
|---|---|---|---|
| A14 | 混合资源删除及脚本伴随 `.meta` 写批次登记 | `resource_service.py:120` 白名单只有 `.cs/.asmdef/.asmref/.rsp`；259 行对 delete 使用同一限制。 | `.prefab/.meta` 仍整批拒绝。将普通资源记录为不触发编译的独立变更，或提供结构化拆批建议。外部 `.cs.meta` 与根 MixedResourceDeletion 合并排期；已经支持脚本 deletedPaths 本身不重开。 |
| A15 | UP-022 Capture 活跃会话摘要 | `UPilotConsoleCaptureService.cs:87` 与 `status_tools.py:678` 只有 count/includeActive。 | `includeActive` 是包含活动项，不是仅活动项；需 activeOnly/activeCount 的有界结果，避免清理检查被历史会话挤占。 |
| A16 | UP-023 HTTP 辅助调用保留 MCP 校验错误 | `upilotserver~/src/upilot_mcp/compile_driver.py:76` 仍只取 `result.structuredContent`，缺失时返回 `{}`。 | 保留 JSON-RPC error、isError/content 与无法确认的执行状态，增加解析契约测试；不自动重发原调用。 |
| A17 | UP-024 Flow 成功汇总降为正常日志 | `Editor/Optional/Flow/Scheduler/UPilot.Flow.Scheduler.cs:1003` 完成摘要仍无条件调用 `LogWarning`。 | 成功使用 Info/结果日志，异常才 Warning/Error；统一 Logger 门面已经完成，并不自动改变该调用级别。 |
| A18 | UP-030 脏场景保留条件下显式只读资源测试 | `UPilotTestService.cs:405/425` 仍执行场景预处理和清洁场景门禁；没有 allowDeclaredReadOnlyEditMode/声明验证入口。 | 现有 autoSave/ignore 会保存或丢弃，不等于保留脏场景运行。需先确认 UTF 可安全执行，再做显式声明和场景不变式检查；不能根据 fixture 名称放行。沿用根清单 P3。 |
| A19 | UP-021 原生 Tooltip/临时浮层截图 | Snapshot 请求没有 includeTransientWindows；现有窗口截图针对目标窗口，不包含独立 Tooltip 的合成证据。 | 精确 PID/所有权下临时窗口枚举和可信合成；当前 Tooltip 文本测试不等于像素验收。 |
| A20 | Bridge 离线的隔离一次性 Worker | `compile_driver.py:134` 的入口要求对应工程已有 connected Bridge；既有 open_editor 是打开/连接 Editor，不是隔离 Worker 生命周期。 | 原本即为延后候选；需独立工程锁、端口、产物和清理设计后才实施，不应为本次分析启动或模拟 Worker。 |

以上按归并后的具体剩余范围列出；同一父条目的已完成部分不计入开发工作量。

## B. 已有实现，剩余主要是验收或部署复核

这些事项仍未完整闭环，但不应再次按“从零开发”排期。

| 优先级 | 编号 / 范围 | 当前源码证据 | 应保留的剩余任务 |
|---|---|---|---|
| P1 | B01 写批次持久查询、Hang/重启恢复、历史终态不污染 | `resource_tools.py:86` 已公开 `unity_write_batch_status`；`resource_service.py:213` 返回持久批次和等待诊断；`state_store.py:402/521` 通过 `terminal_snapshot_json IS NULL` 保护既有终态，414 行按身份/时间核验关联。`test_p1_reliability.py:240` 有重启/冲突终态测试。 | 将“没有批次查询”改为“源码有，客户安装链路与新 Hang/辅助进程场景待复核”。核对 Server/Bridge/Registry 同源，复验 stale、Hang 恢复、真实重启与自动失败不污染旧成功；与 A01 一起优先收口。不要用 registration operationId 冒充通用 Operation ID。 |
| P1 | B02 Editor 聚焦真值 | `status_service.py:790` 读取实际 foreground PID，失败返回 FOCUS_NOT_ACQUIRED；`test_startup_focus_diagnostics.py:123` 已测失败不报告 focused。 | 客户仍出现旧行为，优先查安装代码/活动 Server 来源；补真实双 Editor、拒绝切前台、被抢回焦点矩阵。不把单次瞬时前台验证当成持续获焦保证。 |
| P2 | B03 Vector/Color/Rect JSON 回归 | `UPilotComponentService.cs:660/669` 已复用 `UPilotSerializedPropertyUtility`，后者 140 行起使用 JsonUtility 并拒绝非有限分量。 | 当前源码不再包含该向量手拼路径；确认客户实际加载实现，补公开 add/get/modify 和双版本零值/小数矩阵，不能仅用内存字段正确关闭非法 JSON 回包问题。 |
| P2 | B04 Operation 产物路径/标量 | `task_service.py:1347` 按 fieldKinds/显式 path/file 读取文件，sha256/bytes 作为元数据；同对象内声明 bytes/sha256 才做一致性核验。 | 复核客户嵌套 reportPath/reportBytes/reportSha256 原现场、requireEditMode 和持久恢复。若要求旁置字段自动绑定校验，当前透传标量不等于完成该要求，应先明确契约。 |
| P2 | B05 WP-01 严格选择器/null | `test_tools.py:30` nullable 参数、默认 requireAllSelectorsMatch=true；`test_service.py:130` 严格零交集零启动；`test_test_selectors.py:88/102/366` 有 null、严格拒绝、原生/代理 Schema 测试。 | 顶部已有 9 月 18 日双版本定向证据，底部仍写“未授权开发/待处理”已过时。剩余是完整选择矩阵/客户工具缓存核对和逐子门禁状态更新，不是重写筛选器。 |
| P2 | B06 PrimitiveArrayResult，WP-02 结果编码 | `UPilotExecutionService.cs:864/941` 已内联支持的基础数组并检查 UTF-8 大小；`UPilotReflectionPreflightTests.cs:173` 有数组、溢出、不合法浮点测试。 | 补公开入口及 Reload 后纯值可读/旧 handle 拒绝；与 A10 输入 DTO 的剩余风险分开。 |
| P2 | B07 Type 值绑定、运算符、表达式预算，WP-03 | `CSharpSubsetEngine.cs:1000/1651` 用 StaticTypeTarget 区分类型目标和 Type 实例；有嵌套/闭合泛型、Unity 向量运算；`ReflectionCache.cs:35` 有缓存，Diagnostics 有 getterCallCount。 | 不重做 binder。补冷/热/慢 getter、调用恰一次、缓存失效、AssemblyLoad/Emit/Reload、取消和双版本公开入口证据。文档 9 月 18 日仍有非零 reflection fixture 失败，必须解释具体断言或修复后复测，不能用“预期”二字判通过。 |
| P2 | B08 Snapshot manifest 持久一致性，WP-06 | `UPilotSnapshotService.cs:1931` 原子替换 manifest，计算 hash 后写 state，只有 state 成功才公开 verified；`UPilotSnapshotTests.cs:230/262/315` 已定义两写边界失败/恢复测试。 | 剩余真实 Reload/Server 恢复、不重新捕获、同一 snapshotId 的边界证据。9 月 19 日记录的“两个 writeBatch 编译边界”不能证明 Snapshot manifest/state 双写正确。 |
| P2 | B09 WP-04 窗口身份/内容区、Safe Mode、UP-019 最小化矩阵、历史生命周期 | `screenshot_service.py:585/612` 复用精确实例与域；Snapshot 有 contentRectVerified；`UPilotWindowService.cs:503/565` Safe Mode 仅允许自有 UPilotSafeWindowProbe；历史环形记录已存在。 | 补停靠/浮动、DPI/内容区、遮挡、双 Editor、Reload、自毁/OnEnable 失败及正常→最小化→恢复 fixture。自有探针通过不等于任意第三方窗口安全白名单已交付。新增 native 空壳检测单列 A11。 |
| P2 | B10 WP-08 叶子增量、断连观察、双版本导入预热 | `UPilotTestService.cs:1603` 维护已完成叶子；`p0_acceptance_call.py:65/91/93` 保留发送状态，25 行排他落盘；`test_service.py:724/968` 保留导入输入及 sourceUnchanged。 | 补真实 Reload 中 cursor/取消/重复回调、10001 事件边界、观察 envelope，以及 U6/U22 导入 `.meta` 变化和预检后的状态变化。三项各自验收，不能用一个叶子运行通过代替全部。 |
| P2 | B11 WP-09 Capture 所有权/只读附着 | `status_service.py:2102` 开始请求持久归属，2270 行起校验停止凭据；已有 attachment 持久存储、attach/detach 与相关契约测试。 | 补双客户端、轮转缺段、64/65 附着边界、断连/Reload/Server restart、导出失败恢复。9 月 19 日 start/重复 key/stop 只覆盖其中一条生命周期。未知 owner 不自动收尾。 |
| P2 | B12 UP-026 与 Prefab 引用跟踪，WP-11 | `UPilotAssetService.cs:1217` 起区分依赖模式；1502 行起对象身份/链路；1570 行起反向游标，1631 行拒绝源变化；3015 行起 Prefab 对象引用解释。 | 现已有正向/反向/字符串字段/稳定分页，不再列为“只有文件依赖”。补 TTL、容量8/9、取消/卸载失败、Reload、千级候选及覆盖率性能证据。 |
| P2 | B13 pause/resume、SceneView maximize/restore，WP-13 | `status_tools.py:105/117/233` 已有公开入口；`test_p2_wp13_contracts.py` 定义幂等、取消、freshness、恢复和 Server restart 测试。 | 补双 SceneView、停靠组副作用、10轮真实 PlayMode、setter 前后取消、Reload/finally 恢复。已有单目标最大化会引起邻窗实例重建的历史观察，不可写成“其他窗口零影响”。 |
| P2 | B14 UP-017 JSON 诊断与 Editor 验证说明，WP-07 | `task_service.py:180` 返回 Unicode offset、行列、有界 snippet；path 明确为 null，不伪造 JSON 路径。1845 行起有 not_requested/pending/verified/failed。 | 字段级位置诊断已实现，但准确 jsonPath 不是当前保证。按已批准契约复核真实 Bridge 非法 JSON、状态持久化及 requireEditMode 省略/false/true 的区分；不要再称缺少全部解析诊断。 |

代码引用中仅写文件名的 Server 路径默认是 `upilotserver~/src/upilot_mcp/`，测试路径默认是 `upilotserver~/tests/`；Unity 服务默认是 `Editor/Core/`，执行核心默认是 `Editor/Execution/Core/`。

## C. 应从“待开发”移出的旧描述

| 原描述 | 本轮核对 |
|---|---|
| Scene Summary 预算没有公开 Schema | `mcp_tools/resource_tools.py:233` 已用 strict Field 声明 maxNodes=1..10000、maxMilliseconds=1..1000、maxExamples=0..50。应转为客户端缓存/状态同步核对，不继续排功能开发。 |
| 没有 warning 详细信息 | `state_store.py:467` 已持久化批次 warnings，WP-05 有相应契约；根清单 9 月 19 日已记录双版本 CS0168 实测。不要重做，保留自动编译来源与安装版复核边界。 |
| component.remove 没有目标回执 | 当前已有精确 target、before/after、dirty/requiresSave；真正未完成的是 A07 中仍泛化的入口。 |
| contains 仅支持数组、excludeUpilot 静默丢弃 | `status_tools.py:496` 已接受 string/list/null；根清单记录 Registry v7/公开 wrapper 别名修复。保留客户刷新/部署检查，不重开通用映射实现。 |
| CSV 表头逻辑/物理行完全未实现 | UP-031 已交付并有历史定向记录。外部 CSV 结构变更需求不能与该已完成项混为一谈。 |
| Snapshot V1 通用桥不存在 | `UPilotSnapshotApiV1.cs` 已有版本化入口。剩余客户自身迁移与截图证据，不应再次申请通用截图 API。 |
| “所有 P2 已完成”或“所有 P2 都未开发” | 两者均不成立。WP-01/02/03/04/06/07/08/09/11/13 大量实现已存在；A09/A10/A11/A12 及真实矩阵仍有明确剩余。 |

根清单中 5 个“已忽略”条目保持不推进：追踪器高频过滤性能、IMGUI 定位完整矩阵、EditorWindow 截图旧完整矩阵、Actions Node 运行时剩余验证、发布质量门禁剩余部署。忽略不等于通过，也不解除现有安全检查。

## D. 客户项目自身的未完成事项

以下仅核对现有源码，不修改或执行客户工程；不是 UPilot 包的开发任务。项目原优先级不替代产品排序。

| 优先级 | 编号 / 项目事项 | 当前代码与具体剩余 |
|---|---|---|
| P1 | X01 xclient：退出 PlayMode 后 Console 错误漏入最终报告 | `D:\MA\xclient\Assets\Editor\KingShotBattle\TestRunner\KingShotBattleTest.cs:1083` 在退出前 CollectConsole；1130 行退出后直接 FinishRun。需要退出后的增量分类，并区分 caseOutcome/cleanupOutcome。 |
| P1 | X02 xclient：历史 failureDetail 空串破坏 JSON | 同文件 208 行初始化 `{}` 已有；312 行从 SessionState 读取，2324 行仍拼接 rawFields。保留历史空串/转义输入防御，不能再要求新增初始化，也不能断言任意历史值都安全。 |
| P1 | X03 xclient2：距离出生点 Case 自动移动与准确计数 | `KingShotBattleBornWaveAuditSmokeCase.cs:24` 仍固定40秒，Begin/Tick 只审计，不驱动摇杆。需按配置点路径有界移动、逐点触发/唯一计数、不可达与未覆盖归因。 |
| P1 | X04 xclient2：专属弹道 Case 严格来源和逐弹报告 | `ProjectileGameplaySystem/TestRunner/Smoke/KingShotBattleProjectileMultiTargetTrackingSmokeCase.cs:149` 有 SkillLogic 过滤，301 行主要是布尔汇总；没有独立 SourceConfig 校验/逐弹槽位结算明细。专属 Case 已存在，补严格证据，不新建同功能 Case。 |
| P1 | X05 xclient2：失败截图 Snapshot V1 接入 | `KingShotBattleTest.cs:1683` 仍探测旧同步 TrySaveScreenshot，**已经有 Camera fallback**，不能说完全无回退；尚未接入 V1 异步终态/provenance。需核对旧方法返回迁移失败时是否走回退，补失败/超时下首因保留、清理前截图与来源降级。 |
| P1 | X06 xclient：相机控制链原子快照 | `KingShotBattleAutomationBridge.cs:310` 仅拼 battleState、battle time/id、players 等，未聚合 CameraBounds/HorizontalFollowSnapshot/坐标域。已有生产相机数据不等于统一桥已暴露。 |
| P1 | X07 xclient：持久战场监视及 scope 一致性 | 同 Bridge 85 行静态 MonitorSessions，454 行 CollectBattleMonitorArtifacts 返回内联 events/lastSnapshot。需 scope/过滤/事件归因、JSONL+summary、sequence/dropped/错误与 Reload 恢复，不能拿 UPilot 的通用 Capture 当业务监视已实现。 |
| P1 | X08 xclient：声明步骤与实际计划对账 | 同 Bridge 131 行 AnalyzeWorkflow 校验步骤/选择/日志计划后返回 Ready，未返回完整 effectivePlan/外部步骤/条件跳过。需明确步骤是意图还是驱动执行，报告与实际计划一致。 |
| P1 | X09 xclient：BattleEvent 参数化诊断 | `GetBattleRuntimeSnapshot(optionsJson)` 当前没有消费 battleEventIds 并返回对应事件运行态。项目已有特定事件 Smoke 与运行时 snapshot 类型，不等于统一查询入口和任意 ID 的参数化 Case 已完成。 |
| P1 / P2 | X10 xclient / xclient2：ColdLaunch 前关卡和 Case 配置预检 | 两项目 Bridge `AnalyzeWorkflow` 均有选择/日志校验，但未验证精确请求关卡存在及 Case levelRequirements 与当前 CSV 一致。xclient 原为 P1，xclient2 对应项为 P2；无效输入应在进入 PlayMode 前拒绝。 |
| P2 | X11 xclient / xclient2：终态耗时冻结 | 两项目 `KingShotBattleMcpSmokeReport.cs:241` 都以当前 Editor 时间减开始时间；xclient2 `KingShotBattleTest.cs:284` phaseElapsedSec 同样持续增长。持久 finishedAt 已存在不等于 live 查询冻结；需要统一终态时间源。 |
| P2 | X12 xclient：CSV 结构变更的项目入口 | `KingShotBattleCsvTailColumnCompleter.cs:16` 已按逻辑记录处理尾列；`BattleEventSystemConfigCsvService.cs:12` 为固定表专用迁移。通用表头诊断已完成，剩余只应保留项目需要的新增列 dry-run/统一预览，不重复开发 parser 或专用迁移。 |
| P2，待确认是否仍适用 | X13 xclient2：预览 PlayMode 工作场景夹具抽取 | TODO 指向的 `Assets/Tests/KingShotBattle/ProjectileGameplaySystem/ProjectilePreviewPlayModeTests.cs` 当前不存在，定向搜索也未找到同名文件。不能沿用旧路径断言“只差抽取”；先确认被移除测试的替代覆盖和需求存续，再排实现。 |

`F:\xclient3` 的截图事项记录 Done，源码存在 Camera.Render 回退；本轮不重新打开该已完成项目项。其编译身份与 warnings 补充是安装版复核，归并 B01/编译诊断，不另计产品功能缺口。

## 推荐实施顺序

1. **主 Editor 会话隔离 A01，然后 B01 批次身份/部署复核。** 先确保后续证据归属可信。
2. **核心诊断 P1：A02、A03、A04；并行澄清 A05 契约、复核 B02。** 不以“自动推进”为由扩大未知 Capture 的停止授权。
3. **小范围确定缺陷：A06、A07、A08；随后 A10 输入 DTO 与 B03/B04 回归链路。** 已有 component.remove 等实现直接复用。
4. **按 WP 子门禁补定向验收。** 重点是 B08 Snapshot 真正双写边界、B10 Reload/导入、B11 所有权、B12 大候选和 B13 恢复；历史 failed fixture 先归因，不混进绿色统计。
5. **A09 自动编译复用、A11 原生空壳和 A13 状态/安装一致性。** 前两项需可证明的边界，不能把当前保守拒绝算作功能完成。
6. **HDRP/多 Display 与 P3 延后。** A15/A16/A17 可作为小改动穿插，但不抢占 P0/P1；Tooltip、脏场景只读测试、离线 Worker 先验证技术边界。

任何后续 C#/程序集修改仍必须走规范 Unity 工程的关联编译与最窄定向验收。本报告不授权全量 EditMode、客户项目运行、发布、自动关闭历史事项或更新截图基线。
