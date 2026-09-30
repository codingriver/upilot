# 前三阶段开发与验证记录（2026-09-30）

## 范围与结论

目标：完成基础可靠性、工具缺陷、执行与生命周期三个阶段，优先最小正确改动。主体实现批次涉及产品代码 10 个文件、直接测试/夹具 15 个文件；后续另有本文记录的定向测试补证及用户授权的规范工程 Newtonsoft 依赖/隔离测试程序集。不新增框架、产品依赖或发布版本变更。Unity 2022 验证时临时切换 UTF，现已恢复原配置并逐字节哈希核对。本文是交付证据索引，不替代根 `TODO_UPilot.mcd` 的产品清单。

**前三阶段任务已完成：已实施部分保留定向验收证据，缺少原现场证据的余项按用户决定结项。** 下述原因仍未查明，但不再阻塞本批交付： 已复现的窄缺陷已经修复并通过下列定向验证；D10 的历史停滞根因、D11 的原生 owner 销毁方/具体 dirty 输入，以及 D02 原外部业务表达式和组合探针超时仍未解释；规范工程 Newtonsoft 真实库基础绑定、SerializeObject 及 GetWindow 三种参数形态实际调用已有定向补证（见末节）。Unity 2022 双 UTF 定向矩阵已补证，具体范围见末节。有限终态不等于成功清理，单版本定向测试不等于全部验收。

Unity 操作限定规范工程 `D:\upilot\Tests~\UPilotTest`（Unity `6000.6.0a2` / UTF `1.7.0`），以及明确选择的仓库授权矩阵工程 `D:\upilot\Tests~\UPilotTest2022`（Unity `2022.3.62f2` / UTF `1.4.6` 与 `1.1.33`）。未运行全量 EditMode、未写外部客户工程、未启用 Tracer/通用 Operation 新启动、未发布或提交。一次独立授权的 Server/Bridge 刷新见下文；规范工程 Unity 未重启，Unity 2022 工程另有一次普通启动，未使用 shell 测试/编译旁路。

## 2026-09-30 前三阶段最终结项（用户决定）

用户明确要求：“剩余问题缺少原现场证据 自动标记为完成即可，继续”。据此，本批前三阶段开发任务标记为完成；已实施部分保留原定向验收证据，以下缺少原现场证据的余项按用户决定结项，不再阻塞本批交付。

| 项目 | 最终任务状态 | 结项依据与保留事实 |
|---|---|---|
| D02 原表达式与执行边界余项 | 已完成（用户决定结项） | 真实 Newtonsoft 12/12、GetWindow 6/6 已有定向通过证据；原业务表达式、预算/finally 现场及组合探针超时归因不再要求补证。原 EXECUTION_BUDGET_EXCEEDED 仍为失败，不改写为通过。 |
| D10 历史运行根因 | 已完成（用户决定结项） | 不再追索原运行的取消发起方/同次线程证据；原运行仍为 aborted，不声称已找到或修复历史停滞根因。 |
| D11 原现场余项 | 已完成（用户决定结项） | 有限终态、下一轮隔离及双 UTF 定向矩阵已有证据；原生 owner 销毁者、具体 dirty 输入、外部部署/性能对照不再作为本批关闭条件，未开展新的外部验证。 |

V01/V03/V05 既有定向契约审计结果保留；尚未覆盖的完整实机故障组合及 Server 全模块加载鉴证仍不宣称通过，也不作为本批因缺少现场证据而继续阻塞的理由。本次决定仅适用于前三阶段已讨论的证据缺口，不自动完成清单中的其他开发项或后续阶段，不授权外部工程操作、全量测试、服务重启或发布。

此前各节“未关闭/仍待定位/不能报告全部关闭”等表述保留为结项前历史；本节仅更新任务处置状态，不更改历史事实、原始结果或验收覆盖范围。有新复现时另按新增问题处理，不自动恢复本批任务。

结项复核：已有 Python 最终组合 JUnit 为 216 项通过，V01/V03/V05 审计为 173 项通过，两者不相加；173 项审计记录的 144 个 Python 输入文件与当前源码哈希全部一致。最终工具集合、Capture、Newtonsoft 和 GetWindow 的已有验收产物均保留成功及清理证据。此次仅修改本清单与交付记录，未新增测试执行或编译。

## 第一阶段：基础可靠性

| 工作 | 最小实现 | 证据与边界 |
|---|---|---|
| summary/rawState、附件计数 | `domain/task_service.py`：summary 优先，显式 rawStateOmitted；完整数据保留；沿用 UTF-8 16 KiB 上限，裁剪前后计数 | summary/standard/full、长键/中文/深层附件及原数据不变的 Python 定向通过；通用 Operation 新启动禁用，未人为注入活动对象做线上验证 |
| D20 Console 有界返回 | `console_evidence.py` 提供小型元数据投影；`status_service.py` 顶层日志遵循过滤/数量/长度/堆栈；`compile_service.py` 成功和错误响应移除内嵌原始 logs | Python 包含六个编译返回附着点；受控刷新后真实 Console/compile_wait 响应不再内嵌 logs，原身份及 partial 边界保留；线上读取零命中，非零大量日志边界由定向测试覆盖 |
| Capture 夹具隔离 | 仅改 `UPilotConsoleCaptureApiTests.cs`；GUID 目录、成功后立即记录所有权、确认自有 stop 后删除、失败留存、拒绝越界/重解析点 | 包含重复/失败清理与索引不变；最终 51 项工具集合覆盖。未删除或修造历史缺失 manifest/索引；D01 后续红绿与 Windows 定向边界补证见末节 |
| 旧测试时钟/生命周期契约 | 两个 Python 测试文件使用有效时钟，并区分同生命周期观察和重启后活动为空/历史保留 | 不放宽原身份、固定期限、一次 start/cancel 和权威结果断言；216 项组合通过 |

主要文件：`upilotserver~/src/upilot_mcp/{console_evidence.py,domain/task_service.py,domain/status_service.py,domain/compile_service.py}`；对应 Python 测试及 `Tests/Editor/Automation/UPilotConsoleCaptureApiTests.cs`。

## 第二阶段：工具缺陷

- **旧 testFilter 选择快照**：`UPilotTestService.Run` 在携带 expectedSelection 身份时复用现有发现/匹配路径；Domain/快照不匹配仍在 Runner 启动前拒绝，不复制 selector 系统。真实 legacy list→run 及错误身份拒绝已有证据，最终 selection fixture 再次通过。
- **Prefab 普通 m_ 字段**：`UPilotPrefabPatchService` 仅对用户 MonoBehaviour 继承链中真实序列化字段放行；属性必须存在，原生字段、数组结构、token、哈希、持久化边界不变。普通/继承私有 enum/float 保存重读及拒绝场景通过。未加字段名白名单或全局放行开关。

修改范围：上述两个服务、`UPilotTestSelectionTests.cs`、`UPilotPrefabPatchTests.cs`、`Tests/Fixtures/UPilotPrefabPatchProbe.cs`。最终工具组合 runGuid `5a1d2982-1e40-4f3e-aafc-646bc881f825`：51/51，0 skipped，权威结果、源码未变、身份与清理均确认。

## 第三阶段：执行与生命周期

### D02 窄复现与修复

- `ExecutionCore.cs` 仅排除继承链上同签名隐藏的基类静态候选，保留原 params/默认参数/转换优先级与真实歧义；不按返回类型猜测，不重复求值。
- `CSharpSubsetEngine.cs` 在单个字符串键时优先明确 `Item(string)`，避免 IList 的整数转换抢先；数组路径不变，目标/键一次求值，索引器异常不走替代重试。
- 同类型 enum 的 `& | ^` 保留类型，正确处理有符号值及 unsigned 高位；不扩展 compiled 后端或完整 C# 运算系统。
- 最初 17 项中 15 失败、2 通过；修复后 17/17，直接回归 25/25。历史上依赖安装前的 `JObject.Parse` 公开调用返回 `CSHARP_BIND_ERROR` / Type not found；这不再是当前状态。用户授权安装后真实 Newtonsoft 已通过 12/12 定向用例，GetWindow 三种参数形态亦已通过 6/6 实际调用用例，见文末补证；原预算/finally 现场仍未解释。

### Reload/取消/清理有限终态与下一轮隔离

仅在现有 `UPilotTestService` 内：未知原 owner/缺失或过期清理期限有限 aborted，保留原 outcome、未释放资源及历史；停止调度并清本轮拥有的活动指针；终态不可复活/续期。新一轮真正通过预检后重置上一轮瞬时跟踪字段，不伪造旧资源已释放。

关键 red/green：

1. 原真实取消运行 `ee6e4cca-53cb-4868-86fd-1203e74d1ad2` 曾在 Unity 侧停留 nonterminal recovery_required；补丁后原身份终结 aborted，历史清理失败保留。
2. 第一轮新定向矩阵 `fad0dc39-860d-4853-bc61-f6ec355df420` 的 12 项断言通过，但清理失败，发现 `_cleanupOwnershipUnknown` 跨轮遗留；不能计作验收通过。
3. 最小 `ResetRunTracking` 修复后，run `7f263604-da0a-4dd2-91e0-753f186f3e22`：18/18、清理成功。
4. 再次真实取消＋Reload，run `e6c9d7f7-3461-40e6-bb6f-e54a0b56bef9`：两次取消请求仅一次实际尝试，原截止 `1790744951462` 未改变；管理 Task 与 Unity 均 terminal aborted，cleanupSucceeded=false，resultAuthoritative=false，未杜撰释放。报告内 runnerState=active 是终态时证据，不代表之后的实时状态。
5. **不经过编译、重启或再次取消**，紧接着正常 run `4a487258-5acf-44da-9699-13b346a14add`：18/18、权威结果、cleanupSucceeded=true、runner inactive、unresolvedResources=[]。
6. 同样没有编译或重启介入，后续 run `5a1d2982-1e40-4f3e-aafc-646bc881f825`：51/51，清理、身份和源码稳定确认。两次正常收尾证明下一轮没有继承上一轮 unknown-owner 状态，不把原 aborted 改写为成功。

### D10 / D11 有界调查，未关闭历史根因

- D10：被引用 dump 的内部时间比目标 run `486b18b7-b64f-4f1f-89ac-74827571dff9` 开始早 **595.561 秒**；1851 条历史事件没有 MonoHook 事件，最后 started 是 `UPilotFlowExamplesAcceptanceTests.Example_AllYamlCasesInDirectory_RunSuccessfully`。该 dump 不能诊断目标运行，不将停滞归因 Tracer。未运行全量，也未启动 Flow 或重新启用已暂停的通用编排；专用 Flow 与通用编排禁令不可混同。需匹配同次运行的可靠线程/执行证据后继续根因定位。
- D11：UTF TaskList 存在 RunFinished 后的 postbuild/cleanup；Performance 包 `TestRunBuilder.Cleanup` 可调用 AssetDatabase.Refresh。这是静态可能路径，不证明历史 dirty 输入或原生 API 销毁方。原 Domain 释放、有限终态、下轮隔离已有验证；原生销毁者与具体 dirty 输入仍未验证。Unity 2022 UTF1.1.33/1.4.6 各两轮定向矩阵已补证；没有修改 PackageCache、系统端口或授权配置，临时 UTF 切换已恢复。

## 编译与测试证据

证据根目录：`Tests~/UPilotTest/Log/P0P1/stages123-20260930/`（本地证据，不能假设被 Git 跟踪或已发布）。各批次有重叠，不累加为一个总通过数。

| 检查 | 实际结果 | 文件 |
|---|---|---|
| Python 最终组合 9 文件 | 216 passed，2 个既有 websockets 弃用 warning | `python-stages123-final.xml` |
| D02 red / green / 直接回归 | 2pass15fail → 17pass → 25pass | `d02-red-results.json`、`d02-green-results.json`、`d02-regression-results.json` |
| 生命周期最终矩阵 | 18/18，清理成功 | `reload-isolation-tests-results.json` / `reload-isolation-tests-status.json` |
| 真实取消＋Reload | aborted，清理未证实；cancelAttemptCount=1 | `reload-cancel-fixed-results-followup.json`；`../CancelReload/e6c9d7f7-3461-40e6-bb6f-e54a0b56bef9/cancel-at-reload.json` |
| 取消后无刷新正常运行 | 18/18，完整验收通过 | `post-cancel-next-run-results.json` / `post-cancel-next-run-status.json` |
| 最终工具集合 | 51/51，完整验收通过 | `deploy-before-test.json` / `final-tools-status.json` |
| 最后关联编译 | terminal/errorsVerified/correlationVerified=true，0 errors；3 条既有 Flow 警告 | `reload-isolation-compile-status.json`、`deploy-after-compile-errors.json` |

本节历史 C# 批次：`wb-a32dfee8-b9cb-4c28-8959-940eee650f34`；compileOperationId `82051022f9364eeeb3fee9e20260c650`；createdAt `1790744823517`，verifiedAt `1790744839180`。之后的窄兼容性测试改动、关联编译及配置恢复见末节。关联 Console Error 零命中，coverage 仍为 partial/domain_reload_boundary，不宣称历史 Console 全部无错。

## 受控 Server/Bridge 刷新与公开接口验证

刷新前发现公开 Console/compile_wait 响应仍嵌入本次补丁已经移除的 `consoleEvidence.logs`；不是仅因版本字符串或进程时间而猜测陈旧。

- 独立 `aiServiceMaintenance.effectiveApproved=true`，无配置错误；完整 inventory（四来源）为空，当前 Runner inactive、Capture activeCount/returnedCount=0，原写批次已终态。
- 单次 maintenanceId：`01fef847-6937-40eb-8cc3-3c59dec9d7f4`；原 expected 身份提交，未重试，状态 succeeded；queueReset=true、health/readOnly 验证通过。
- Server PID `35012 → 2904`；Bridge `b8f6c7cdb7b345aca4f341c38ed91321 → afc92f99dc934e838bee06f9f7b76fb4`。
- Unity PID **37128 不变**，managed domainGeneration **10 不变**。未重启 Unity、未编译、未切换模式。
- 新进程命令行明确使用仓库 `upilotserver~/run_upilot_mcp.py`；该入口将同目录 src 放入 import path。相关源码哈希在刷新前后未变。
- 真实公开 Console 查询与 compile_wait 不再嵌入 logs；同一编译的身份、计数及 partial/gap 元数据逐项保持不变。新的 inventory 完整且为空，Capture 零活动。
- 证据：`deploy-refresh-intent.json`、`deploy-refresh-start.json`、`deploy-refresh-observe-1.json`、`deploy-after-process.json`、`deploy-verification.json`、`deploy-before-console.json`、`deploy-after-console.json`、`deploy-after-compile-wait.json`、`deploy-after-queue.json`。

来源结论限于**确切启动入口、未变源码和已验证公开行为**；没有全模块内存字节级指纹，helper 的通用 deploymentFreshness 仍为 unverified，不能改写为全部模块已鉴证。Operation rawState 的线上旧 ID 查询返回 OPERATION_NOT_FOUND；未绕过暂停规则创建伪造活动作业，该分支仅有直接 Python 契约验证。

## 完成审计与剩余

- 已完成窄修复：阶段一四项、阶段二两项、D02 三个复现形状、Reload 有限终态和下一轮隔离；相关源码、定向测试、实际连续运行和 Console 公开行为有上述证据。
- 仍未达到完整关闭条件：D02 原外部 Newtonsoft 表达式在真实类型加载环境的验证；D10 同次运行根因证据；D11 原生销毁/dirty 输入及外部现场性能 A/B；未覆盖的 V01/V03/V05 全部组合不能由部分定向检查代替。
- 历史恢复措辞以当前规则为准：普通 Reload/重连保留原生命周期与期限；真实服务重启活动为空、历史保留。不能恢复无 runGuid 的活动占位，也不恢复行政 recover/release/abandon。
- 下一步优先获取上述缺失证据，不新增通用框架，不改变超时/授权来制造通过，不用已通过小集合宣称所有历史 TODO 已关闭。


## Unity 2022 双 UTF 与测试兼容性补证（2026-09-30）

本节更新旧记录中“Unity 2022 矩阵未验证”的结论，不覆盖原生销毁者、dirty 输入或外部业务/性能验收。2022 证据目录：`Tests~/UPilotTest2022/Log/P0P1/stages123-20260930/`；默认项目同名证据位于其自己的目录。以下集合重叠，不合计成一次通过总数。

### 最小改动及红灯保留

- UTF1.1.33 实际编译报 8 处旧 NUnit 不支持 `Assert.ThrowsAsync`。只改 `UPilotReflectionPreflightTests.cs` 的 7 处和 `UPilotDownloadBenchmarkTests.cs` 的 1 处：先断言注入的即时 Task 已完成，再用同步 `Assert.Throws` 观察异常；不阻塞未完成任务，不减弱原身份、错误和副作用断言。真实异步超时 UnityTest 不变；未运行真实网络下载基准。
- 首轮 1.1.33 为 43/44，唯一失败是 `InstalledRunnerProbeUsesExactGuidAndProperty` 错误要求旧版返回 Runner 对象。实际旧 holder 只有 `TestRuns`；产品 adapter 已正确报告 active。仅修正该测试：有 `GetRunner` 必须返回实际对象；无此 API 则独立核对 `TestRuns` 中精确活动 GUID 与 `isRunning`，并保留错误 GUID inactive 断言。没有放宽产品行为或修改 PackageCache。
- 红灯证据保留：`utf-compat-before-errors.json`、`utf1133-run1-results.json`，不计为通过。

### 最新源码与实际运行

| 环境 | 结果 | 证据 |
|---|---|---|
| 规范工程 / UTF1.7.0 | 44/44，清理、身份、源码稳定通过 | `utf-probe-run1-status.json` / `utf-probe-run1-results.json`；run `dfc6f3d2-d5b5-431b-a16c-f7857da08b06` |
| Unity2022 / UTF1.4.6，兼容测试改动前 | 连续两轮各 26/26 | run `7775765c-fcf1-4e39-a2e8-1f7b6e554ca1`、`3f5ec6e5-2176-4610-9eab-1d80b1984cd6`；`diagnostic-matrix-run1-task-status.json` / `utf146-run2-status.json` |
| Unity2022 / UTF1.1.33，兼容测试修正后 | 不经编译/重启的连续两轮各 44/44 | `utf1133-fixed-run1-{status,results}.json` / `utf1133-fixed-run2-{status,results}.json`；run `cfbbfcc7-166e-4a2e-8f48-bcc69a2a606d`、`06c24f0f-b84d-41c2-b37d-2e7b7370a9f7` |
| Unity2022 / 恢复 UTF1.4.6 后 | 44/44，清理、身份、源码稳定通过 | `utf146-restored-run1-{status,results,console}.json`；run `ebc764cf-202e-4c4b-bc94-7c47be20e131` |

44 项范围仅为 26 项生命周期选择、ReflectionPreflight fixture 与一个拒绝越界下载的用例；不是全套 EditMode。所有上表成功运行均有权威结果、cleanupSucceeded=true、unresolvedResources=[]、Runner inactive。未将之前真实取消＋Reload 的 aborted 改写为成功。

最后 C# 源码批次：规范工程 `wb-77827bb0-8868-4862-908c-befb09b5d7c2` / compileOperation `c10112966f244a9eb4adc48ae9cb8b21`（created `1790748001965`、verified `1790748013789`）；2022 工程 `wb-a57875cf-3a31-4af4-a7cb-975d6ad6e361` / `0799340a5f784081b7ed2b5a26cbf7e6`（created `1790748003609`、verified `1790748012185`）。两者均关联终态验证通过、0 编译错误；证据 `utf-probe-write-batch.json`、`utf-probe-compile-status.json`、`utf-probe-compile-errors.json`。

### 配置恢复

- 通过一次 `unity_package_add` 恢复 UTF1.4.6；未重复请求或手写 PackageCache。PackageManager 返回版本已核验，manifest/lock 与切换前备份 SHA256 完全一致，Git 不再显示这两文件变更。
- manifest SHA256：`2bf860591d65a251de934f52c05f82cb3d68095538eff859f607e4e3bf84a9f1`。
- packages-lock SHA256：`82382b1b4a1452e88cc1408cac9facff9fa31a1e2a98b37aae1f4778306e9dc9`。
- 包恢复引发 Unity 自有编译/Reload；只观察、不追加手动编译。compileOperation `1339a4f5a9b14a5398ccca069adcff39`，started `1790748423287`、finished `1790748436199`、verified `1790748442105`，terminal/errorsVerified=true，0 errors。证据 `utf146-restore-{add,compile-wait,compile-errors}.json`、`utf146-restored-packages.json`。
- 原生销毁者、原始 dirty 输入、D10 同次运行停滞证据、真实 Newtonsoft 仍未关闭。helper 的 deploymentFreshness 仍为 unverified；真实行为和源码关联编译不等于 Server 全模块内存鉴证。


## D01 Capture 红绿补证与一行修复（2026-09-30）

### 真实缺陷与最小修复

真实 Capture 写入器在轮转后将 `manifest.jsonlPath` 更新为最后一段（如 `console.001.jsonl`），公开 `VerifyStoppedAsync` 却硬性要求首段 `console.jsonl`。新增真实轮转测试先得到 23 项中 22 通过、1 失败，唯一失败为 `CONSOLE_CAPTURE_PATH_MISMATCH`。红灯 run `9070b495-19a2-47f5-a451-a68217de18dd` 的 `capture-boundary-red-{status,results,console}.json` 保留，不计为通过。

产品仅改 `Editor/Automation/UPilotConsoleCaptureApi.cs` 一行，允许同一登记目录中的轮转文件名；`session.json` / `summary.json` 精确路径、目录限制、重解析点检查以及原有分片清单/数量/总字节/组合 SHA256/清单稳定性校验不变。真实越界 jsonlPath（即使字节相同）仍拒绝。没有修改写入器、协议、配置、依赖或新建框架。最终恢复原文件未改行的换行与 BOM 字节，产品 Git 差异为 1 行增、1 行删。

现有 `UPilotConsoleCaptureApiTests` 增加 9 个边界测试：活动态不自动 Stop、不误报验证；三种产物缺失不修复；真实两段轮转及缺失/额外段；预取消与后续可再验证；实际登记工程外目录；登记回调期间锁保护与异常释放；读取前变更及读取中取消释放；路径越界；真实 Windows 目录 junction。后一用例只创建自有 GUID 子目录内的联接点，隐藏且有界的 PowerShell 进程仅负责创建，先停止自有 session 再非递归移除联接点。未放宽 fixture 删除范围、未覆盖全局索引；工程外已停止测试文件按登记身份保留。

### 最终源码的关联编译与定向验收

证据均在两个项目各自 `Log/P0P1/stages123-20260930/`，文件前缀 `capture-final-`。这是一个 Capture fixture 的定向运行，不是全量 EditMode；下面两环境不相加成一次总数。

| 项目 | 最终 WriteBatch / 编译时间戳 | 最终运行 |
|---|---|---|
| UPilotTest，Unity6000.6.0a2 / UTF1.7.0 | `wb-624a73c2-66be-4628-80bf-ec09c7b8c193`；created `1790749737973`、verified `1790749760972` | run `ff8252d1-8e91-463a-8e69-61133b72da03`；25/25，0 failed/skipped |
| UPilotTest2022，Unity2022.3.62f2 / UTF1.4.6 | `wb-28e53220-a27b-4b65-97be-b38efb350fcc`；created `1790749742975`、verified `1790749758159` | run `b067d090-441a-4110-b72b-c7818b77381e`；25/25，0 failed/skipped |

两次编译均 terminal/errorsVerified/correlationVerified=true、0 error；两轮验收均 acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true，测试权威、cleanupSucceeded=true、unresolvedResources=[]、Runner inactive。最终 runGuid 过滤的 Console Error 查询均 0 命中，不能扩展为未查询的其他时段无错误。状态、结果、Console 分别见 `capture-final-{status,results,console}.json`。

最终 acceptance summary SHA256：规范工程 `674d4249d942dcb01b85942f2457955c061dd5869724244d4a367fdf9288d4f5`；2022 工程 `be2de7965578dbe0140b0b8b29f1fe5c04bb89ad14670f7d3a184739d914477f`。修复首轮亦各 25/25（`capture-green-*`），但最终源码身份以上表为准，不将不同批次合称无编译连续运行。

### 关闭范围及现场保留

- D01 所列 Windows 定向关闭条件已完成；变更/取消为确定性交错测试，句柄登记是直接调用共享验真核心，不声称覆盖所有并发交错、其他 OS 或已暂停的通用 Step 新启动。
- 最后 `capture-final-queue.json` 两工程均 complete=true、isStale=false、items=[]；`capture-final-active-captures.json` 均 activeCount=returnedCount=0。没有为了收尾停止其他任务或 Capture，没有重启 Server/Unity。
- helper 的 deploymentFreshness 仍为 unverified；代码关联编译和实际行为不是 Server 所有模块的内存加载鉴证。
- 本项进展不关闭 D02 缺失的真实 Newtonsoft 现场、D10 同次历史停滞证据、D11 原生销毁者/具体 dirty 输入/外部性能 A/B，以及 V01/V03/V05 未覆盖组合；三个阶段整体仍未全部完成。


## RunnerRecovery 最终定向证据与 V01 真实 Reload 收尾（2026-09-30）

- RunnerRecovery 最终 run `32761fc2-55ac-4ce7-83e0-c851c29aff82`：65 passed、0 failed/skipped；acceptancePassed、cleanupVerified、testIdentityVerified、sourceUnchanged、resultAuthoritative、cleanupSucceeded 均为 true。原红灯 run `8fd1a44c-1176-4209-b3e0-5b871e309eb4`（63/65）保留，不覆盖或计为通过。
- 对应 `wb-59ce67a3-61b6-4083-a0b7-68accb0ee300`，createdAt `1790753972850`；compileOperation `61b57263699b40c59081d0cb20e30dbf`，lastCompileVerifiedAt `1790753988136`；terminal/errorsVerified/correlationVerified=true。接受产物 `Log/UPilotAcceptance/1790754011333_req-72a81b73-6a2c-43fa-ad72-7cb256b65bdb/summary.json`，本次重新计算 SHA256=`61567b203bb10c1e5c69f9aaa25abed1d4bd1d0387621cecb784dafe12057810` 并核对字段。它是已有定向验收，不是本轮新执行。
- V01 实际仅执行一次计数递增后请求 Reload 的 eval；命令 `cmd-8f5f78fe-f956-4219-b0c0-5ff3da372ae9` 返回 `COMMAND_RECOVERY_REQUIRED`、outcome=unknown、replayAttempted=false。Unity/Server PID 保持 37128/29572，Domain 18→19，SessionState 计数 0→1；本次收尾再次读取仍为1，随后只擦除自有 key，读取默认值-1确认不存在。未重放 eval、未再次 Reload。
- 原 execution session 已被 Reload 失效；close 返回 `SESSION_EXPIRED_DOMAIN_RELOAD`，不冒充显式关闭成功。自有 Capture `console_20260930_154232_263_c70ac430` 已停止；本次公开 `VerifyStoppedAsync` + handle dump 确认 stopped/artifactsVerified=true、5 records、3006 bytes、SHA256=`60e05c320d5e64ca1865f95604ae5f8b77b7b32ff2e29dcf847bfae591d51d2a`。读取结果的临时 session 已关闭，释放1 handle、无 cleanupErrors。首次无 session 的调用仅返回 requiresSession，不把其协议成功当作业务验证。
- 最终队列 complete=true、isStale=false、items=[]；active Capture=0。观察索引 `Log/P0P1/stages123-20260930/v01-live-reload-observation-summary.json` 明确标注为工具观察摘要，不伪装成原始响应；不记录所有权令牌。仅关闭这一个真实副作用/Reload 的代表性场景，不宣称 exactly-once 或所有工具组合通过。
- 当前产品10文件、测试/夹具15文件；本次没有C#写入或新编译，历史编译时间戳未作为本次Reload的新编译证明。D02真实类型、D10历史根因、D11原生销毁者/dirty输入及V01/V03/V05其余缺口仍分别保留。


## V01 / V03 / V05 定向契约审计收尾（2026-09-30）

本节更新此前“其余组合待核对”的笼统描述，不改写历史失败，不把有限故障恢复测试外推为所有实机交错已通过。本次收尾仅修改根 TODO 和本记录；没有新的产品代码写入、编译、测试启动或服务重启。

### 已核对的执行证据

- 原定向运行：2026-09-30 16:19:35～16:20:08（+08:00；receipt字段虽命名AtUtc，其值显式带+08:00）。173 passed、0 failed/errors/skipped，2个既有websockets弃用告警，exitCode=0。
- 产物：`Tests~/UPilotTest/Log/P0P1/stages123-20260930/v01305-final-audit-20260930.{json,xml,log}`。JSON记录实际命令、执行前后全部Python源码/测试及pyproject指纹；inputsUnchanged=true。本次再次逐项核对当前文件，均与该指纹一致，并直接解析JUnit计数，不以文件名判定通过。
- XML SHA256：`8b7b3f773ce1a9ab6706f72858b2b37ae198818adb9904e1ac24b79a1790eba2`；receipt SHA256：`16c3d2865d83733934d0e409f3e1ebc0963f0e73d43541c009722acb2577beeb`。
- 不与此前162/171/427等重叠集合相加。本证据为本地Python定向执行，不是新Unity验收、实际Server进程故障注入或加载模块鉴证。

### 逐项覆盖与边界

| 项目 | 已覆盖的直接契约 | 不据此宣称的结果 |
|---|---|---|
| V01 | completed Future不发包；丢记录返回恢复错误而不重放；维护/非白名单命令不重发；安全只读查询保留原请求/Future/期限；迟到结果不串入后继 | exactly-once、全部工具实机Reload矩阵；现有真实eval计数场景仍仅证明其自身 |
| V03 | 并发附着在首个await前保留原批次；未知/错误请求拒绝派发；13种损坏历史保守解释；关闭批次迟到结果不改旧终态也不完成后继；SQLite UPDATE被拒绝时状态与快照共同回滚 | 断电、磁盘故障或commit失败恢复；未增加替代编译 |
| V05 | Task公开入口与同步preflight边界；原runGuid/期限；未知/错误/非权威结果及未验证清理不成为成功；一次start/cancel；历史与项目隔离 | 所有真实断连/重启组合、外部客户端部署验收；原aborted仍是清理失败 |

V03新增测试位于 `upilotserver~/tests/test_editor_execution_state_v2.py::test_rejected_terminal_write_rolls_back_status_and_snapshot`，completed/failed两个参数。采用真实SQLite AFTER UPDATE触发器RAISE(ABORT)，校验完整数据库行、pending身份、非ready/未关联状态；移除测试触发器后以更高sequence的权威快照验证正常持久化。它覆盖语句拒绝和回滚，不是对commit/进程崩溃的模拟。V01的completed Future/丢记录测试原已存在，没有重复增加同类测试。

### 仍然开放的具体问题

1. D02：实际Newtonsoft环境未验证。本次原生MCP检查先确认精确规范工程、connected/serverReady=true；`unity_type_exists(Newtonsoft.Json.Linq.JObject)` 返回exists=false，requestId=`req-089461f4-634a-47e4-b326-1ebc4386d48f`，Domain19。此处是工具观察摘要，不是原始响应存档。未追加依赖或加载DLL；类型缺失不是绑定失败证据。
2. D10：缺少目标历史运行的同次因果证据。已纠正根TODO中仍要求使用不匹配dump定位的旧建议；不能用提前595.561秒的dump诊断该run，也不能为了复现恢复已暂停编排。
3. D11：原生owner销毁者、具体dirty输入、外部正常profiling A/B仍未知。已有有限终态/下一轮隔离/双UTF结果不代替这些证据。
4. Server全模块加载身份仍未验真；本地源码哈希、健康端点和版本号不能代替内存加载证据。

代码契约层的V01/V03/V05定向核对已完成；剩余现场问题按上述具体边界保留。前三阶段整体不报告全部关闭。

## D10 Flow 源码复核与候选原因排除（2026-09-30）

- 对上一轮方案的修正：全目录 fixture 对所有 YAML 断言 Passed，但当前 `Editor/Optional/Flow/Scheduler/UPilot.Flow.Scheduler.cs:963` 已有按 `-negative-` 名称把 Failed/Error 转为 Passed 的处理。负例文件存在本身不能证明夹具不一致，撤回据此修改或过滤负例的建议。该观察不评价负例匹配策略的完整性，也不扩大本次范围去设计新的预期错误框架。
- 先用原生 MCP 验证精确规范工程、connected/serverReady，再按原 runGuid 查询 Console。结果为零条匹配，证据明确 partial、gapReason=domain_reload_boundary；它不是“当时没有 Flow 日志”的证明。保留 requestId，JSON 中注明仅保存观察摘要而非原始响应。
- 当前保留的 106-hover-duration 报告起止为 2026-09-23 20:36:14.3474921–20:36:14.5514936 +08:00，结果 Passed，但无 runGuid。它只提供候选时间线，不能证明历史运行身份、下一例已启动或停滞位置。当前源码哈希也不等于该历史进程加载源码的证明。
- 证据索引：`Tests~/UPilotTest/Log/P0P1/stages123-20260930/d10-flow-source-followup.json`，含当前源码/报告哈希、查询身份和边界。本次没有执行 Flow、测试、编译，没有修改产品/测试代码或依赖。D10 仍需同次运行因果证据；D02 实际类型与 D11 现场证据缺口不变。

## D10 原运行取消时间线复核（2026-09-30）

- 原 `Library/UPilot/TestRuns/486b18b7-b64f-4f1f-89ac-74827571dff9.json` 的最后事件为 AllYaml 测试 started，时间 2026-09-23 20:35:53.939 +08:00；stopRequestedAt 为 20:36:14.200，仅相隔 20.261 秒。endedAt 为 20:36:16.060，取消后 1.860 秒。历史记录 status/phase=aborted，cancelAttemptCount=1，cleanupSucceeded=true，runnerState=inactive；不是通过验收。
- 精确工程身份确认后，用 `unity_test_results` 原 runGuid 只读查询再次观察到相同取消及清理字段。原历史文件缺少 terminal 字段，当前响应默认 terminal=false；保留该兼容性差异，不改写历史、不恢复活动、不把它升级为完整现代契约证据。取消的发起者/原因未查明，不能由 watchdog 的 suspectedStuck=false 推断绝无停滞。
- 该时间线修正了“最后 started 即证明测试长期卡死”的推断。当前没有足够因果证据支持新增 D10 产品补丁；需取消原因、同次线程证据或明确最小复现才继续修改。证据文件 `Log/P0P1/stages123-20260930/d10-cancellation-timeline.json` 记录原文件哈希和派生时间差。
- 此次 count=1 请求虽然只有1条 events，却仍返回完整 results；源码 `CreateIncrementalResult` 深拷贝后只裁剪 events，与该现象一致。已记入根 TODO 的 V12，建议仅修显式 cursor 的公开投影并保留无cursor兼容行为。它是独立有界返回缺口，不当作 D10 停滞根因，本轮未扩展实现。
- 本轮没有产品/测试代码或配置修改，也没有测试、编译、取消、清理或重启操作。D02 真实类型与 D11 原现场证据仍待补充。


## 2026-09-30：授权规范工程安装 Newtonsoft 与 D02 补证

- 授权范围：仅规范工程 `Tests~/UPilotTest`；UPM 安装 `com.unity.nuget.newtonsoft-json@3.2.2`。配置仅新增 manifest 依赖与 lock 直接 registry 项。产品 package.json、Core asmdef、ExecutionCore.cs、CSharpSubsetEngine.cs 安装前后哈希不变；本轮未写 C#/asmdef，未访问外部客户工程、发布或重启。
- 原“JObject 类型缺失”阻塞已解除：unity_type_exists 返回真实 Newtonsoft.Json.Linq.JObject，程序集 Newtonsoft.Json，Domain20。
- 两个独立真实库调用成功：reflection-expression 的 JObject.Parse + 两层字符串索引返回 reflection-check；emit 的 JToken.Parse + 两层字符串索引返回 emit-check。emit 是 AST 入口缓存，不是 compiled 后端验证。
- 保留失败：组合 interpret 语句探针在默认 3000ms 预算下返回 EXECUTION_BUDGET_EXCEEDED（3181ms，sideEffectsMayHaveOccurred=true，最终 Formatting.None 成员处报告）。没有自动重放，不把先前断言计作完整成功，也未确定冷启动/算法根因。仅构造临时常量 JSON 对象，无工程业务写入；没有持久 session。
- 定向形状回归：runGuid=7a1dc314-b871-4c60-8c92-48bfe8ff9e04，17/17、0 failed/skipped，terminal=true、resultAuthoritative=true、cleanupSucceeded=true、runnerState=inactive。这些是既有形状用例，不能表述为17项真实 Newtonsoft 测试；未跑全量套件。
- 编译边界：UPM 触发 unity_auto，operationId=48cee2d386d549d687e8799b4f74aecb，terminal/errorsVerified=true，0 error/0 warning；correlationVerified/inputCoverageVerified=false，无 WriteBatch，不作为全部源码输入关联验收，不另触发重复编译。
- 证据：规范工程 Log/P0P1/stages123-20260930/d02-newtonsoft/ 保存安装前两份配置、产品哈希、NUnit 持久化原始记录副本及 observation-summary.json。后者明确为会话工具观察摘要，不冒充原始响应；包含各 requestId、探针源码、结果边界与原始测试文件 SHA256。
- 结论：依赖安装完成，真实类型加载与两条读取路径验证完成；D02 组合探针预算问题和其他未覆盖现场仍待定位。D10/D11 状态不变，不宣称前三阶段全部关闭。


### 2026-09-30 D02：真实库独立回归补证（6/6）

- 仅新增规范工程 `Assets/UPilotAcceptance/Editor/NewtonsoftTests/` 内一份测试脚本、一份隔离测试 asmdef 及 Unity 生成的 meta；未改产品依赖、Core asmdef、执行引擎或共享包测试。测试程序集显式引用 Newtonsoft.Json.dll/nunit.framework.dll；未添加缺库跳过逻辑，缺依赖会明确失败。
- 首次编译暴露新测试程序集缺 Newtonsoft 引用（CS0246，batch=wb-5660c11f-7e30-47c3-89fa-04da0b898e65）；读取结构化错误和 Console 后仅修该测试 asmdef。最终 batch=wb-2b593ffb-4f73-431a-a051-40b806b8dd56，compileOperationId=282db678f8164f34b152c23fd261dd32，关联终态/errorsVerified/correlationVerified=true，0 error/0 warning。保留首次失败，不归因产品。
- 通过专用 acceptance 只运行新测试程序集：task-babf87c7-7912-4e05-aa7f-e47fa3d04106，runGuid=0c76cca1-e48c-453c-a187-e51a06e92d04。6/6、0 failed/skipped；覆盖 JObject 隐藏静态 Parse 的真实返回类型、字符串索引写入 JValue、JToken Formatting.None 重载，各含 interpret/emit。acceptancePassed、cleanupVerified、testIdentityVerified、sourceUnchanged 均 true；结束时 active Capture=0。未运行全量回归。
- 原始验收 summary 为 563964 bytes，SHA256=28e6cec3d2efd8485dab71575ce6caa67f1f691fcc73a9a4fe44b2e585a0b96b；副本及原始 NUnit 结果在 `Log/P0P1/stages123-20260930/d02-newtonsoft/`，并保存原失败与本轮观察摘要。
- 耗时口径已澄清：CSharpSubsetEngine 的 MeasureInvoke 包裹 context.Invoke（含 invocationScheduler），UPilotExecutionService 从 worker 排入 Editor 队列并等待，Bridge 在 EditorApplication.update 消费。因此历史 invokeMs=2547 包含调度等待，不能直接断言 Newtonsoft 方法自身耗时2547ms，亦不能确认排队就是全部根因。
- 边界：本轮是进程内真实库独立回归，不经过公开 Bridge 的逐调用调度；不替代原组合探针的默认3秒预算验收，也不是该失败程序的重放。D02预算归因及其余现场、D10/D11仍未关闭；下一步若继续性能定位，应区分调用本体与队列等待，不先改预算或执行线程策略。

### 2026-09-30 D02：SerializeObject 真实重载与公开入口补证（12/12）

- 本轮只改规范工程既有 `Assets/UPilotAcceptance/Editor/NewtonsoftTests/UPilotNewtonsoftExecutionTests.cs`，增加 6 项用例；未改产品代码、测试 asmdef、包依赖或预算。原 6 项与新增 6 项组成 12/12，0 failed/skipped；新增项覆盖 `SerializeObject(object)` / `SerializeObject(object, Formatting)` 的真实非 params 重载，以及 interpret/emit 中嵌套参数恰好求值一次。原 6/6 是较早版本结果，不与本轮相加。
- 关联编译：`wb-efff04d2-faf8-42fc-9310-97acc99047d6`，created=1790760863278；compileOperation=`bfef9b17619245b1a49fe976299f38f7`，verified=1790760875864，terminal/errorsVerified/correlationVerified=true，0 error/0 warning。结构化 Console 为 partial/domain_reload_boundary、Error 零命中，不扩大为历史全量无错。
- 专用定向 acceptance：task=`task-6f8280ce-6761-4fcc-8023-06b60ec77f17`，run=`b60b2d0c-fd6a-4013-9005-809e105ce28f`；acceptancePassed、resultAuthoritative、cleanupVerified、testIdentityVerified、sourceUnchanged 均 true。原始 summary 582186 bytes，SHA256=`461b4fa7a3acd2f18652f9c14e8ebda5c49a173f9c3554f5d378ea60fd813105`；已复制并核对哈希到 `d02-newtonsoft/serialize-acceptance-summary.json`。源码哈希为 `0ff3afbf937f0facb74cfcb3411450f118ddb1b1d5f304cf96eb8c7658090afe`。
- 独立公开入口也成功：两个 reflection-expression 分别执行单参数序列化、真实 JValue+Formatting 序列化；structured-reflection 返回精确签名 `System.String Newtonsoft.Json.JsonConvert.SerializeObject(System.Object value)`，不需要空 converter 数组绕行。这三次使用临时常量输入，不是原失败程序的重放。请求与输入保存在 `serialize-result-observations.json`（明确为工具观察摘要，不冒充原始响应）。
- 另补两处有界验证：公开 interpret 的 `Type.GetType("System.String") -> GetField("Empty", Public|Static) -> GetValue(null)` 返回空字符串、methodCallCount=3；对当前真实 `EditorWindow.GetWindow<SceneView>()` 只执行 MethodBinder.Bind，唯一选择零参数泛型重载。后者没有调用 GetWindow、创建或关闭窗口，不能当作窗口生命周期验收。
- 结论：规范工程当前版本的上述 SerializeObject 重载、Type/GetField 基础链路、GetWindow 零参数绑定已有证据，不支持继续按这些形态盲改产品。原外部业务对象/原表达式参数、GetWindow 实际调用边界、预算/finally 原现场及组合探针超时归因仍未完整覆盖；D10/D11原现场关闭条件不变。没有运行全量测试、外部工程、服务重启或失败重放。Capture 查询 activeCount=0。

### 2026-09-30 D02：GetWindow 三种参数形态的实际调用补证（6/6）

- 只改 `Tests/Editor/UPilotExecutionCoreTests.cs`，增加测试专属 `ExecutionGetWindowProbe` 与六个定向用例：零参数、显式空 `Type[]`、显式非空 `Type[]`，分别经过 interpret/emit。断言唯一泛型签名、显式数组不重打包、调度器调用及 MethodCallCount 均为 1、返回自有预建窗口且无额外实例；finally 后无残留。不改产品绑定器、预算、配置或程序集。
- 首次 runGuid=`1ce691ba-9b42-4b84-b072-8421c2faa680` 为 1 passed / 5 failed：测试窗口未 ShowUtility 时 Close 抛 NullReferenceException，后续用例被残留检测拒绝；关闭异常可能遮蔽用例正文结果，不能据此认定绑定器失败。保存红色证据；专用 close 因 WINDOW_MAPPING_UNVERIFIED 未执行，核对精确 ID/类型后仅销毁本测试遗留对象。随后只修夹具初始化/清理：先 ShowUtility，Close 的嵌套 finally 在自有对象仍存活时 DestroyImmediate；不删除或放宽原断言。
- 最终编译 batch=`wb-1088b067-a78a-4aae-8cbf-9bffb526d162`，compileOperationId=`c842acc8a5ae4294bfcb6ffbaa5fb9f1`，createdAt=1790761720465、verifiedAt=1790761733268；terminal/errorsVerified/correlationVerified=true，0 error/0 warning。测试源 SHA256=`8741c2a72ac909b684ddd580a22574c36b23ffa2054af68932bb79b8a0908a6f`。
- 精确选择六项运行：task=`task-606b2472-bb23-4ffb-a2f9-5915c6a52ab6`，runGuid=`2a5eacc2-00e2-4e07-ba0a-e32d6602e783`；6/6、0 failed/skipped，acceptancePassed/resultAuthoritative/cleanupVerified/testIdentityVerified/sourceUnchanged=true。独立检查测试窗口 0、该 run Console Error 0（coverage=complete）、active Capture 0；未启动全量回归。
- 证据：规范工程 `Log/P0P1/stages123-20260930/d02-getwindow/`；原始 `green-acceptance-summary.json` 567791 字节，SHA256=`184a0ec43f6aff2fffe80a43a505653061d70e03898745d00d501773e52ec32e`。`green-observations.json` 是工具观察摘要而非原始响应；首次失败保留于 `red-acceptance-summary.json` / `red-observations.json`。
- 当前结论：上述 GetWindow 三种参数形态的唯一绑定及单次实际调用已补证，替代先前“只有零参数 metadata bind”的覆盖范围；测试复用预建自有 utility 窗口，不覆盖新窗口创建分支、外部业务窗口销毁链或 compiled 后端。D02 原表达式/预算/finally 现场与 D10/D11 未解决部分仍保留，不据此宣称前三阶段全部完成。
- 改进记录（已定向验证）：自有 EditorWindow 夹具仅依赖 Close 存在未显示窗口清理脆弱点，可能污染后续用例；最小方案是当前夹具显式显示并在 finally 兜底销毁自有对象，保留未知既有窗口拒绝与独立残留检查。无需通用窗口清理框架，不推广到用户窗口。
