# 通用编排暂停：实现与独立验收报告

日期：2026-09-29（Asia/Shanghai）  
范围：`D:\upilot` 权威源码、默认验收工程 `D:\upilot\Tests~\UPilotTest`。本报告只覆盖本次功能收缩，不替代之前的测试清理修复报告。

## 1. 交付结论

**本轮实现及所要求的定向验收已完成：所有公开生产入口固定拒绝新的通用 Operation／Step Run；专用路径保留；历史恢复保护不变。**

- 新启动和对应验证返回 `GENERIC_ORCHESTRATION_DISABLED`，没有白名单、用户开关或生产测试旁路。
- 已有 Operation／Step 的查询、产物和原有安全取消、清理、恢复、处置能力保留。后台安全观察不因前台返回而停止。
- `operation_wait` 遇到 `RecoveryRequired` 立即返回 `recoveryBlocked=true`、`terminal=false`、`waitWindowElapsed=false`，不冒充完成、不重放 Start／Cancel、不重置期限。
- 高级设置使用“批量清理可安全处理项”；保留既有 MCP 参数、授权、预览、一次性 token、项目身份、备份和资源证据检查。
- 不是将所有历史 `RecoveryRequired` 消除。本轮没有清理或批量修改现场 7 条历史 Operation，也没有忽略 Step Busy。

## 2. 实现位置与边界

| 位置 | 本轮变化 |
|---|---|
| `upilotserver~/src/upilot_mcp/domain/task_service.py` | `operation_start/operation_validate` 在记录、Capture、观察器和业务调用之前固定拒绝；已知 Task 包装在占位、重试／重启之前拒绝；历史 wait 立即报告恢复阻塞。 |
| `upilotserver~/src/upilot_mcp/tool_registry.py` | 统一暂停错误及结构化包装识别；直接派发、`unity_tool_call`、可静态解析的嵌套 Task 包装一致；registryVersion=8，启动／验证能力为 `available=false/callableNow=false`。 |
| `upilotserver~/src/upilot_mcp/mcp_tools/task_tools.py` | 保留工具名称、参数，更新公开说明；历史工具仍登记。 |
| `Editor/Automation/UPilotAutomationStepService.cs` | 公开 `StartJson/ValidateJson`、Bridge `automation.steps.start/validate` 均在项目验证／执行及 Run 创建之前拒绝。内部执行器及历史入口保留，不新增生产绕过开关。 |
| `Editor/Core/UPilotStatusWindow.cs`、`Editor/Core/UPilotQueueCleanupWindow.cs` | 按钮、窗口标题及确认提示改称“批量清理可安全处理项”；`allCleared=false` 仍明确表示未确认全部清理。 |
| `skills/upilot-unity-mcp/`、`Documentation~/QueueCleanup.md` | 从源模板与分发参考维护新边界，改用对应专用工具，禁止以 Task、反射或临时脚本重新包装绕过。 |
| `Tests/Editor/Automation/AutomationStepRegistryTests.cs`、相关 Server 测试 | 覆盖固定门禁、零 Validate／Execute、副作用、历史保护及专用路由直接回归。 |

Server 仅识别已知结构化包装，不分析任意代码内部意图。普通反射仍可用；若调用公开 Step 启动入口，则由 Unity 的真实门禁拒绝。历史测试所需旧 admission 位于 `upilotserver~/tests/legacy_operation_fixture.py` 隔离夹具，不暴露给生产。

未修改 `stateless_http`，未新增全局调度器或恢复框架，未新增强制释放适配器，未发布、未部署外部客户工程、未修改 PackageCache。

## 3. 实际加载身份

本轮验收端点为 `http://127.0.0.1:8011/mcp`，返回精确项目路径与默认工程一致。

- Unity：`6000.6.0a2`；Editor PID=`43112`，验收 Domain generation=`29`。
- 经独立 `aiServiceMaintenance` 授权，执行一次 Server／Bridge 维护：`d06e5815-4aca-4241-985a-1f1cdd37a6f7`，最终 `succeeded/completed`、`readOnlyVerified=true`。
- Server PID：`32968 → 36628`；新 Bridge session：`bb704d5a806149f3ac990b597f0b9eed`。维护前后 Unity PID、Domain 未变化，未重启 Unity。
- 实际 Server 命令行指向 `D:/upilot/upilotserver~/run_upilot_mcp.py`，HTTP 8011。新门禁的真实调用、能力返回以及 Unity 新增公开／Bridge 门禁测试共同提供加载证据，不仅依赖健康检查或版本字符串。
- 两轮验收报告均记录 `sourceUnchanged=true`，来源为 dirty 工作树而非单独一个已提交版本：
  - `sourceCommit=19d5569ca8846adec29837bd88ed58681093312e`
  - `sourceSha256=83825f8f6f7f337048f037584ddb6b7f41c8703d3157cafab505ac739a197dda`
  - `fileCount=894`，`scope=package-code-tests-skills-workflows-v2`，文本归一化 `CRLF-to-LF`。

证据：`after-maintenance.json`、`server-process.json`、`final-runtime.json`、两轮 Task 完整报告。本报告中的 PID／Domain 是验收时观测值，不承诺将来仍相同。

## 4. Python 定向回归

**290 passed，2 warnings，35.97s**。警告为 `websockets` 弃用提示。未默认运行全套测试。

精确文件选择：

```text
tests/test_orchestration_suspension.py
tests/test_operation_runner_and_agent_rules.py
tests/test_operation_step_plan.py
tests/test_operation_cleanup_barriers.py
tests/test_persistent_operations.py
tests/test_recovery_observation.py
tests/test_queue_cleanup.py
tests/test_queue_disposition.py
tests/test_queue_bulk.py
tests/test_queue_abandon.py
tests/test_runner_recovery_contract.py
tests/test_p2_public_tool_contracts.py
tests/test_p2_wp07_contracts.py
```

覆盖启动／验证／代理／Task 包装一致拒绝、失败前零 Start／Capture／记录、能力一致性、恢复阻塞即时返回、原身份与清理保护、专用路由不被门禁误关等。新暂停测试文件包含 43 个用例；旧历史语义测试继续通过隔离夹具验证，未弱化为“反正暂停就算通过”。

证据：`python-final.txt`。

## 5. 唯一 C# 写批次与关联编译

| 字段 | 证据 |
|---|---|
| writeBatchId | `wb-01159c38-1b8e-49e7-a08f-a3dcc85c0aa4` |
| 批次 Operation（专用写批次内部身份） | `op-c4e8c397-319f-4cca-bc15-6aa3bf21c791` |
| writeBatchCreatedAt | `1790649136757` |
| compileOperationId | `7c80f90b3cd446bcbe617e25c2aca6cc` |
| compileRequestId | `req-8b220aae-1fd7-4325-ab7d-371a382f5b87` |
| lastCompileVerifiedAt | `1790649154155`（晚于本批次写入） |
| 结果 | `status=verified`、`outcome=passed`、`terminal=true`、`errorsVerified=true`、`correlationVerified=true` |

四个输入为 Step Service、两个设置窗口以及 Registry Tests。收尾时重新核对这四个当前文件的 SHA256，均与批次逐文件证据完全相同；没有改代码后沿用旧编译证据。

编译 **0 错误、3 警告**，均为未修改的 `Editor/Optional/Flow/Schema/UPilot.Flow.Models.cs` 386／407／496 行字典序列化 UAC1009。Console 查询覆盖标记为 `partial/domain_reload_boundary`，因此不宣称跨 Reload 的整个 Console 窗口没有错误。

证据：`compile-evidence.json`。没有为无新增 C# 变更再次编译。

## 6. 真实 HTTP 门禁与能力

以下每项只请求一次，均返回 `ok=false/error.code=GENERIC_ORCHESTRATION_DISABLED`：

1. 原生 callback Operation start。
2. 原生 stepPlan Operation start。
3. 原生 Operation validate。
4. `unity_tool_call` 代理 start。
5. `unity_tool_call` 代理 validate。
6. `task_start` 包装被暂停入口。
7. `task_execute → tool_call → operation_start` 包装。

在这些调用前后，对 `Library/UPilot/ServerState`、`Log/UPilotConsole`、`Library/UPilot/step-run.json` 范围内 **391 个文件**的集合与 hash 比较，没有新增、删除或变化。该证据与定向单元测试共同证明拒绝没有创建本轮 Operation／Step／Capture 或派发业务 Start，不泛化为对所有工程文件的监控。

能力查询中，start／validate 为 false／false，历史 status／wait／cancel／get／list／collect_artifacts 仍为 true／true。专用工具保留有契约测试覆盖；本轮真实业务验收覆盖专用编译和测试／包验收，并不等于构建、Capture、Snapshot 等全部专用功能都重新做过现场 smoke。

证据：`live-*.json` 及相应请求、`live-capabilities.json`、`gate-files-before.json`、`gate-files-after.json`。

## 7. 两轮连续真实 Unity EditMode 验收

两轮均通过专用 `unity_upilot_acceptance_run`，精确选择 34 个用例，`requireTests=true`、`stopActiveCaptures=false`。两轮之间没有重启、编译或手动清状态。

- `CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests`：24 项（含四个公开拒绝、两个真实 Bridge Handle 拒绝及七个历史 route 保留用例）。
- `AutomationStepExecutorTests`：以下 10 个精确用例，验证历史观察／取消／恢复／处置与 Busy 保护：

```text
ServiceObservationDistinguishesInitializationFromMissingRun
ServiceObservationPreservesExactIdentityAndNeverAdvances
CancelActiveStepThenFinally
RestoreNeverReplaysExecute
UnsupportedRestoreStillRunsFinallyButRequiresRecovery
ExplicitCleanupRecoveryPreservesFailureReportAndReleasesBusy
UncertainCleanupRecoveryCannotBeReplayedEvenAfterReload
DispositionBacksUpFencesOriginalIdentityAndSurvivesNewRunAndReload
RecoveryWithRemainingResourceNeverClearsBusyOrRepeatsFinally
CleanupTimeoutStillRunsFinallyAndBlocksNewRun
```

选择契约：`matchMode=union`、`requireAllSelectorsMatch=true`；selection domain=`7cc9a48eb7fa47cc8d61aa6a467a3d33`，snapshot=`75b9fbca7c3693780a7d175416f9bbf43b3d7e51956218c8ea55b8759f1ca535`。

| 轮次 | taskId | 原 runGuid | 权威结果 |
|---|---|---|---|
| 1 | `task-64608e1b-2b78-420a-be03-df32298a7cad` | `76bc9985-e385-42ff-8a52-d2ea5cdcbdc5` | 34 passed／0 failed／0 skipped；acceptancePassed=true |
| 2 | `task-86c654bc-c422-4d89-a9d5-fcccb01d7f31` | `474acfe7-dccc-48ca-a7aa-698114d4e62e` | 34 passed／0 failed／0 skipped；acceptancePassed=true |

两轮均取得：

```text
Task status=completed / terminal=true / cancelSendState=not_sent
resultAuthoritative=true
cleanupPending=false
cleanupSucceeded=true
cleanupStatus=completed
cleanupResourcesReleased=true
cleanupErrors=[]
unresolvedResources=[]
persistenceError=""
isRunning=false / runnerState=inactive
snapshotSequence=75
```

第二轮实际通过准入，证明第一轮正常清理已解除测试占位，不是仅在 UI 隐藏旧记录。

产物相对默认工程根目录：

- 轮 1：`Log/UPilotAcceptance/1790650482923_req-a6cc1351-1a54-4afd-a03c-ab30a4c9e33c/summary.json`，654024 bytes；SHA256=`0483a0bdf8d5eb3e2c6b3906600a1fcc68068834ee1bcf4acebb49ade208b1ae`。
- 轮 2：`Log/UPilotAcceptance/1790650601915_req-6311b105-e156-416b-91fd-7cfc0dbbe94b/summary.json`，654031 bytes；SHA256=`99e27d4681c47d3bc692f847bb71b228569740adb5b19f0e723726217c0f38e5`。

收尾时已直接计算产物文件 hash，与 Task 回执一致。原始证据为 `round1/2-start.json`、`round1/2-task.json`、`round1/2-status.json`、`round1/2-results.json`、`selection.json`、`test-selection.json`。本轮是 **34×2**，不能与前次清理修复的 **66×2** 混用。

## 8. Agent／Skill 完整同步

- 源 manifest：`agentRulesVersion=48`、`skillPackVersion=57`；未发布，UPM 版本仍为 `0.3.41`。
- 已完成源模板生成、源校验、默认工程五目标同步、两个已安装 Skill 校验及最终重复只读检查；五目标均为 `current`、`changed=false`。
- 已实际重复执行同步确认幂等：46 个文件的集合、SHA、mtime_ns 和备份目录均不变；托管块外原字节未变。
- template SHA256：`2052c24bc760742dc0e57be250445e22dac6dd3d41a48027adcbb607c25e1181`；两份 Skill content hash：`96c9dcf222549ab9a79f48fc9f2c753c202cff9ca1c6ce7ec209b485364aa3fd`。
- `sourceIdentity.loadedTemplateEvidence` 仍显示 `unverified`。这里证明的是本次返回模板 hash 与源／安装文件匹配，以及实际调用行为；不将该字段或健康检查包装成任意已加载模块的完整指纹证明。
- 本轮未同步 Unity2022 或任何外部客户工程。

证据：`rules-final.json`、`repeat-sync-before.json`、`repeat-sync-after.json`。

## 9. 历史保护与独立后续问题

现场前后 inventory 中 7 条历史 Operation 的 ID 与状态完全一致：

| 原 Operation | 当前状态 |
|---|---|
| `op-7bae985e-83f9-45e0-a453-fd8bfbf307c0` | RecoveryRequired |
| `op-951c6aac-f972-43b9-a469-1e9fdc63b531` | RecoveryRequired |
| `op-9529f808-275b-4703-b7a9-f41e11b19a86` | Failed，清理未验证 |
| `op-b5e7222e-34d3-450a-998b-78ce52ffb693` | RecoveryRequired |
| `op-de6fa012-4c63-4976-b5eb-56ce2e9fe12a` | RecoveryRequired |
| `op-e340ea72-d14e-45a2-a997-a8fc2f47ecb0` | RecoveryRequired，Step 原启动身份不全 |
| `op-e7574602-9f01-4564-ac93-ece2e12ab63a` | RecoveryRequired |

库存仍 `complete=false`，明确保留 `CAPTURE_STATE_UNAVAILABLE`、`EDITOR_IDENTITY_UNVERIFIED`。本轮没有为了得到“空队列”删除索引或放宽身份验证。

以下作为独立后续工作记录到根 `TODO_UPilot.mcd`，不是本轮新增恢复补丁：

1. **进程创建时间精度不一致**：同 Editor 的 kernel FILETIME=`134350644899879764`，CIM=`134350644899879760`。握手链路按 `//10` 归一，队列链路 raw exact 比较。后续统一来源与精度契约并加精确身份回归，不能简单容差放行。
2. **Capture 测试索引残留**：`Log/UPilotConsole/session-index.json` 中 `console_20260924_073244_177_bacea4d4` 指向已被测试 TearDown 移除的 `Log/AutomationCaptureApiTests/f66bab5805a4419f80dd00f23d0c28b3/session.json`。后续修测试资源与持久索引的生命周期；不在本轮删除现场索引或把 missing manifest 当作完成证据。
3. **未执行的兼容／交互验收**：Unity2022 的 UTF 1.1.33／1.4.6 各两轮、原生确认对话框完整点击链路仍未完成，本轮不宣称通过。
4. **尚未证实的根因**：原生 API 被谁销毁、清理时触发自动编译的具体 dirty 输入仍未查明，不将推测当作结论。

原始 `git diff --check` 曾报告工作树中的 CRLF／混合行尾等 trailing-whitespace 项；未将其报告为通过，也未为消除噪声批量规范化已有未提交修改。本报告的通过范围是上述真实编译、定向测试、门禁调用及模板校验，不是全仓格式、全套测试或未覆盖业务的全面验收。

## 10. 证据定位

除单独列出的 Unity 验收产物外，本轮原始证据统一位于：

`Tests~/UPilotTest/Log/P0P1/orchestration-paused/`

该目录为本地验收产物，不是已发布资产。实现后的文档收尾仅新增本报告并追加根 TODO，没有再次修改或编译产品代码。保留此前未提交及并发修改；交付不包括提交、推送或发布。
