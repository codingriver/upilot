# Step中低复杂度优化实施记录（2026-10-17）

七项功能已实现并完成本阶段限定验收。F17在真实EditorWindow上完成隔离快照的鼠标交互补验，独立轮次48/48通过；此前356项验收按原冻结源码单列，不能合称同一轮360项。本阶段保留Step v1和现执行器；18项延期核心见[根TODO](../TODO_UPilot.mcd#upilot-20261017-stepdeferredcore18项高复杂度优化延期清单)。包版本0.3.53、规则版本60不变，Skill资源版本为72。

## 逐项交付与覆盖

| 项目 | 实现内容 | 已有验证 | 状态及限制 |
|---|---|---|---|
| F03 | Validator/Service返回budgetPreview；逐项执行/Cleanup/poll有效值及来源；JSON出现性元数据只在预检暂存；Server独立stepBudgetPreview透传 | 规范Registry等定向测试通过；两个项目真实MCP预检均验证省略/-1/显式覆盖/poll=0、Cleanup省略与显式10来源不同，normalizedJobSpec中的计划保持原样 | 本阶段完成；原总预算算法、计划Hash和运行截止时间不变 |
| F10 | 六类AutomationEvidenceValues.Components；Service及基类WriteJsonArtifact在原附件事务身份/可写检查后、文件效果前严格校验单JSON值；KSB局部Json.NET共享转换 | 规范投影/附件事务/JSON合同等定向测试通过；trunk序列化/夹具契约17/17权威通过、清理完成 | 本阶段完成；首错持久化与报告终结可靠性仍延期 |
| F14 | Bridge纯数据automationSteps合同；Server一次5秒只读查询，4态及前后session/epoch/domain身份核对；不改准入 | Python新/旧Bridge、断连/超时/畸形、身份变化契约通过；规范8011及trunk8012真实MCP均返回observed，身份一致、合同值正确 | 本阶段完成；能力声明不代替Ready、授权和业务验收；加载入口鉴证不外推为全模块鉴证 |
| F17 | 管理窗口增加任务和选中Step详情；保留内存最近终态；未知/未记录/缺失时间明确显示；精确身份选择与停止确认前固定三身份字符串 | 原44项合同通过；后续独立窗口fixture 48/48权威通过，含真实Layout/Repaint、鼠标展开/选择/刷新/复制、错误与预算显示及确认期间快照身份变化 | 本阶段完成；隔离只读快照，不启动Step或真实Stop；原生确认框按钮未自动驱动。历史Start初始化拒绝仍单列待定位 |
| F18 | 原裁决分支统计具体规则，deny优先/首allow/跨Case预算保持；全量附件，内联和文本前10项+遗漏数；有效action展示规范化，保留原全量文本 | 规范Policy/Writer等定向测试通过，含遮挡、预算、零命中/无效、跨Case、10项截断、冻结与原判定回归 | 本阶段完成；不完整证据和无效规则仍失败 |
| F20 | battle-step-runner唯一模板trunk专项显式60/120/120秒，每项Cleanup10秒；180秒业务时间与墙钟区分 | 三种14项Cold Launch模板在内存展开静态检查通过 | 本阶段完成，仅文档静态验收；未启动战场，未修改正式CSV/Prefab或已为180秒的夹具 |
| F25 | 权威automation-steps资源说明及manifest72；源码生成/校验；规范项目与trunk五目标同步、两份安装Skill校验、实际重复同步无写 | 两项目最终五项current；块外字节、重复同步内容/mtime_ns/备份目录保持；两项目Server/Bridge真实调用已核验 | 本阶段完成；不把v2、强类型接口或延期终结规则写为可用能力 |

## 接口和语义

- 执行/poll省略或-1为RegisteredDefault，显式有效值为PlanOverride；JSON Cleanup省略为FrameworkDefault，显式10也为PlanOverride；内存C#计划Cleanup为PlanValue。complete只说明所有时间可解析，不等于计划ok。无效条目不伪造预算行，其它有效条目可保留。evidenceSeconds=60，总预算仍由原算法计算。
- JSON附件先做局部严格语法检查，再复用AutomationStepJsonCodec检查重复属性和数值；拒绝空值、尾随内容、注释、尾逗号、非法数字及指数溢出，校验后保留原文本。原ParseJson/计划JSON合同没有接入新语法扫描器。既有文本附件、发布、Hash、登记和失败通道保留。
- Vector2/3/4和Quaternion按x/y/z/w，Color按r/g/b/a，Rect按x/y/width/height；非有限分量异常包含类型/分量。KSB设置只作用于本次序列化，拒绝循环和UnityEngine.Object，getter等异常不被吞掉。
- 能力observed要求结构合法且session、producerEpoch、domainGeneration一致；成功但缺字段为unreported；断连或不可查询为unavailable；超时/异常/身份变化为unknown。5秒只约束新增capabilities.list查询，不是整个capabilities_get总时长；没有缓存、重试或新增启动门禁。
- 规则effectiveMatchCount仅统计实际裁决命中，overBudgetCount是blockedCount子集。deny预算不适用，无上限allow保留-1。零命中规则仍保留，sequence=-1；服务观察忽略和默认阻断不生成虚拟规则。完整console-policy保留所有原始分类，状态只读冻结摘要。

## 已完成的验证

Server定向命令（源码仓库根）：

```powershell
D:\upilot\upilotserver~\.venv\Scripts\python.exe -m pytest upilotserver~/tests/test_step_capabilities.py upilotserver~/tests/test_operation_step_plan.py -q
```

结果68 passed，2条既有websockets弃用Warning。最初新增F03测试误调用legacy fixture导致两项失败，改为生产operation_validate后通过，未修改legacy fixture或放宽断言。随后只恢复测试文件原有LF换行。

规范项目固定为D:/upilot/Tests~/UPilotTest（HTTP8011，Unity6000.6.0a2、PID10016）。批次wb-07e69928-a109-48a0-942b-8380b30db774 / compile 6f29d86135c446ca910c1f34a914288f已verified、correlationVerified/errorsVerified=true，结构化编译错误0。AutomationStepRegistryTests原新增代码误写List.Length，已修正为Count；这是开发错误，与服务无监听分别归因。关联Console查询返回CONSOLE_EVIDENCE_NOT_FOUND，保留缺口。

规范正式验收task-5ea1be64-0132-4da9-b31d-2215f05e396e / run 4bd0e680-cc27-4c97-9801-482dde8171a9：356/356通过、0失败/跳过，resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true，资源清理完成。七个fixture及叶子数为：AutomationStepRegistryTests 42、AutomationStepExecutorTests 181、AutomationStepJsonContractTests 49、AutomationEvidenceValuesTests 4、UPilotStepManagementWindowTests 44、AutomationLogPolicyTests 11、AutomationReportWriterTests 25，均在CodingRiver.UPilot.Tests.Automation命名空间。测试Console coverage为partial，不宣称完整Console覆盖。权威报告Logs/UPilot/Reports/Acceptance/1792238634098_req-11add395-3a57-4234-9402-769ba43aaa70/summary.json，1623090字节，SHA256=1da2c1ba726ede633cc84be858d025b277bd54f50abbac9a462bcabffa248e32；本地字节及Hash核对通过。


F17后续独立验收（只复验窗口fixture，未重跑其余六个fixture）：新增4项UnityTest通过真实UPilotStepManagementWindow.ShowUtility及MouseDown/MouseUp验证交互；使用实例级只读快照和默认关闭的Repaint观察器，确认/停止委托只在隔离测试替换。任务首/次错与Step错误分别显示，成功状态的空清理/报告仍为“未记录”，缺失时间为“未知”；原始参数及60/10/0.25运行计划值和“来源未记录”正确，折叠、选择重置、终态保留、读取失败/初始化未知、复制与5000毫秒真实设置文案通过。停止确认前保存Run/Operation/Lifetime字符串，测试确认期间修改原快照三个身份，外部断言仍只发送旧身份且新任务未停止；取消确认不发送Stop。每项恢复窗口、剪贴板和偏好，并核对真实服务快照未变化；不创建Run/Capture/Gizmos或扫描历史。

该轮最终Task=task-25471144-8ef7-409e-8a5b-35052071845f、run=99410d4f-e207-4905-bfe3-18f612e9188e，48/48通过、0失败/跳过，resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true。规范最终批次wb-d2d12af5-e000-42a1-9ca2-aa230fddd11f已verified且correlationVerified/errorsVerified=true、结构化错误0；原始编译身份在f17-gui-fixed-compile-status.json。报告Logs/UPilot/Reports/Acceptance/1792240585336_req-4ef7d529-3f12-490b-84b7-7913a3f62c38/summary.json，684781字节，SHA256=faf8d113de7bc51c0bddc2aa6f43c960a464bd84f2f4a1cdc0e9a87502e3e06d，字节/Hash核对通过。Console总范围complete、分阶段partial，因此整体coverage仍partial。

GUI补验首轮Task=task-69c4b388-0c15-4f31-91ee-536484ae92c3、run=ec9dfaa7-1340-4966-8c2b-69d372c7f279的4项RealGui已通过，原44项仅在新增TearDown失败：对未显示窗口调用EditorWindow.Close触发NullReferenceException。已修正为只有已显示窗口调用Close，其余DestroyImmediate，finally仍恢复剪贴板/偏好。该错误属于本次测试夹具开发错误，不能归因为原44项功能退化；失败回执/报告保留。发现阶段一次matchMode=exact参数被拒，已按合同改为intersection；只预检回执preflightPassed=true且runnerStartAttempted=false，无测试执行，正式Task均只提交一次。

trunk的F17生产文件关联批次wb-32ce6e35-21b8-4f14-b48a-98182c77b2c1 / compile cedd19719a1a48c093c04f2a20bad581已verified、correlationVerified/errorsVerified=true，结构化错误0、关联Console Error命中0；该工程不声明包测试程序集，不将其编译当成规范48项测试。共享[f17-gui-acceptance.json](D:/MA/_AI_Docs/xclient/40_EVIDENCE/trunk/2026-10-17-upilot-step-improvements/f17-gui-acceptance.json)及[f17-trunk-verification.json](D:/MA/_AI_Docs/xclient/40_EVIDENCE/trunk/2026-10-17-upilot-step-improvements/f17-trunk-verification.json)保留本轮摘要及原始证据路径。

trunk KSB EditMode：runGuid=97b6e90f-3973-4de2-bb9a-21a76fe3cae4，17/17通过、0失败/跳过，resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased=true、terminal=true、未释放资源0。Console范围coverage=unavailable，分阶段partial，保留range_coverage_unverified与console_cursor_clear_or_reload_not_detectable缺口。trunk前阶段关联批次wb-dadf45b2-c4e6-462a-b544-382896b7df3e / compile f50b0125d3c749e480f7b654cc4d4689已verified、correlationVerified/errorsVerified=true、结构化错误0。trunk未声明UPilot为testable，不能据此外推包测试程序集验收。

规范Server PID60308与用户重启后的trunk Server PID54092都完成真实MCP验证：automationSteps=observed，contract为discoveryVersion1、supportedPlanVersions[1]、callbackContract=jsonString、captureOwnershipModes[planOwned,operationBorrowed]；查询时身份一致。各自selected-entry loadedSource=equivalent，范围仅选定入口代码，不含全部模块/依赖/全局或闭包值。trunk Unity PID5340未重启。两个项目预算预检均保留原始arguments及stepPlan，无Run/业务Step启动；3行示例预算合计151秒。

源码render --write、render --check、check_skill_pack --mode source通过。两项目同步来源D:/upilot/skills/upilot-unity-mcp，agentRulesVersion60/skillPackVersion72，模板SHA256=b3a044a655c782cdd9defca76cd2aa616c0cf0a1836278e6d1958312a0de057b。规范项目同步当时8011不可用，使用官方CLI integrations-only fallback；恢复后又经实际MCP确认五目标current。trunk使用Unity MCP同步。两份安装Skill均通过，实际二次完整apply无内容/时间戳/备份目录变化。trunk首次显式apply前已是自动分发后的current；块外保持证据仅覆盖该显式apply基线，不追认此前过程。

同步证据分别位于各项目.upilot/validation/step-improvements-20261017/；本次原始MCP观察保存在规范项目Log/P0P1/step-low-medium-20261017/。共享摘要入口为D:/MA/_AI_Docs/xclient/40_EVIDENCE/trunk/2026-10-17-upilot-step-improvements/Report.md。

## 历史阻塞、当前缺口与边界

规范8011/8765曾无监听而界面显示服务活动，用户手动恢复后规范编译及356项验收已完成；未自行开启维护授权或用脚本/UI绕过重启工具。trunk此前旧Server PID52244未加载新增Python，用户手动重启后PID54092的真实MCP核验已完成。

F17真实GUI补验先遇到另一批编译而菜单准入返回EDITOR_BUSY/outcome=not_executed。该批wb-839fbf5c-9f70-4728-86e0-cf1926c5b86d只涉及UPilotUpdateService.cs、UPilotUpdateWindow.cs和UPilotPackageRecoveryTimingTests.cs；未修改本次Step文件，本任务没有覆盖或接管这些改动。确认其编译终态及新Ready后菜单打开成功。补验使用正式完整两项wait计划，validate成功，Start仅调用一次并返回STEP_SERVICE_INITIALIZING、started=false，无Run进入执行。未反射初始化、改准入、重放Start或用其它执行器代替。精确窗口instanceId=568105589213750598已closeVerified=true。该初始化原因待定位，不能从状态码认定既有修复回归或直接归因于F17。

356项测试期间源码保持不变；验收后上述三个无关输入发生变化并由其它批次编译。356项结果对应其冻结源码，不代表当前整个仓库的所有后续变更已验收。两个较早的规范最终队列查询因Editor身份探测TimeoutError返回EDITOR_IDENTITY_UNVERIFIED/complete=false，均保留原回执；Domain变化后最后一次查询complete=true/isStale=false/items=[]。规范和trunk最后活动Capture均为0；不以之前不完整队列推断空闲。

F17已完成真实窗口的隔离交互补验；未自动驱动原生确认对话框、未将历史被拒的Step启动重放或宣称初始化问题已修复。未跑全量EditMode、Unity2022包矩阵或战场业务复验。本次未发布、提交、修改正式CSV/Prefab或执行SVN写操作。18项高复杂度延期继续保留。

本轮测试框架复盘共4条独立建议：Console关联覆盖、服务活动与监听状态、队列身份探测诊断、Step初始化阻塞诊断。均记录为待人工核查，补充既有项时保留原结论，不在本阶段追加实施。

最终收尾复核：规范与trunk的队列均complete=true、isStale=false、items=[]，活动Capture均0；两工程五个集成目标再次current。源码render --check、source校验和两工程四份installed校验再次通过，仍为规则60/Skill72；本次只追加窗口C#及实施记录，未改分发资源，无需再次递增Skill或重复apply。原STEP_SERVICE_INITIALIZING只证明历史准入时未发布可用执行器；原因未定位，本次不改变Initialize/准入/Tick或扩大生命周期修复范围。
