# 周期与超时四阶段验证记录

日期：2026-10-14。四阶段实现、定向验收及服务刷新已完成，配置及实现范围见[周期与超时](TimingAndTimeouts.md)。规范项目为 `D:/upilot/Tests~/UPilotTest`，Unity Editor `6000.6.0a2`、PID `20628`。

Python 定向 472 项通过；Unity 四轮定向验收分别为 47/47、32/32、14/14、10/10。Bootstrap／Reload 恢复竞态已修复并完成实际 Reload 验证。服务刷新、8 个选定入口加载核验及有限现场观察已完成；各轮结果只覆盖各自冻结的源码身份。

集中证据：[final-evidence.json](../Log/UPilotValidation/TimingConfig_20261014/final-evidence.json)，SHA256=`2c6605517dad4c8541d7d41be10fe679a89b5b5d14c73ad9f2187f704545d938`。未覆盖范围见末节，初次失败原样保留。

## 结果总表

| 范围 | 已有结果 | 判定边界 |
| --- | --- | --- |
| Python 定向回归 | 472 passed，30.93 秒；两条既有 websockets 弃用提示 | 覆盖调度、事务、诊断与相关直接回归；不代表在线 Server 已加载修改。 |
| 初次 Unity 选择 | 7 passed，0 failed，0 skipped | **整体验收失败**：staging `.cs` 被 Unity 导入，产生新的编译／Console 错误；不能写成验收通过。 |
| staging 清理批次 | 关联编译 0 errors / 0 warnings，`correlationVerified=true`、`errorsVerified=true` | 证明清理后的该批次编译通过，不改写初次失败。 |
| Unity 定向验收一 | 47 passed，0 failed，0 skipped；`acceptancePassed=true` | cleanup、测试身份和运行期间源码未变均已核验。 |
| Unity 定向验收二 | 32 passed，0 failed，0 skipped；`acceptancePassed=true` | cleanup、测试身份和运行期间源码未变均已核验；不覆盖随后发现的真实 Bootstrap 顺序。 |
| Bootstrap 恢复顺序与首帧恢复 | 14/14、10/10；均 `acceptancePassed=true` | 最后批次编译 0 errors / 1 既有 warning；实际 Reload 独立 120 秒预算、第 1 轮验证成功。 |
| Server 刷新与现场观察 | 维护 succeeded；8 个选定入口 equivalent | 已验证零／双窗口 Health 单实例、心跳与旧样本诊断；生产 timer／SQL 计数未测。 |

四轮是独立选择的结果，不按相加后的数字宣称唯一测试覆盖数量，也不是完整 EditMode 回归。

## Python 回归

已执行的最终命令位于 `upilotserver~` 工作目录：

```powershell
.\.venv\Scripts\python.exe -m pytest tests/test_main_thread_diagnostics.py tests/test_write_batch_deadlines.py tests/test_write_batch_rounds.py tests/test_lifecycle_simplification.py tests/test_startup_focus_diagnostics.py tests/test_editor_execution_state_v2.py tests/test_v02_contracts.py tests/test_editor_observation.py tests/test_editor_lifecycle_health.py tests/test_session_identity.py tests/test_loaded_source_evidence.py -q
```

结果为 **472 passed，2 warnings，30.93s**。范围包括：

- 没有活动截止或只剩终态历史时不访问截止检查 SQL；只有最近活动截止保留计时器。
- 合并保留原截止，取消最近批次重新调度，项目切换／停止撤销计时；PlayMode、无 Bridge 和无客户端查询时仍能按原截止结束。
- 共享轮次及旧式证据提交后才移除索引，事务回滚保留截止；归档失败仍能释放已结束的逻辑活动，不能复活旧行。
- 过期、缺失及身份不匹配样本返回 `null`；新鲜样本携带旧 pump 能识别停滞；CPU 采样结束后再读取时间和身份。
- 既有编译关联、会话与生命周期、选定代码入口加载证据的直接回归。

未因本轮文档整理重复运行已通过的测试。Python 结果不替代 Unity 的编译器、官方 EditMode 执行或真实服务部署来源。

## 初次 7/7：保留整体失败

正式摘要：[初次验收 summary.json](../Tests~/UPilotTest/Log/UPilotAcceptance/1791945448878_req-b597b011-ab8c-4e5d-bf56-4caf531316af/summary.json)。开始时间为 **2026-10-14 10:37:28.878 +08:00**。

| 字段 | 值 |
| --- | --- |
| Task | `task-b979dca8-179b-433f-8e4f-77011f99e8bf` |
| Run | `68b414ce-caaa-47be-b907-9b452f5fbb56` |
| 测试 | 7/7，failed=0，skipped=0，terminal/resultAuthoritative/cleanupSucceeded=true |
| 整体验收 | `acceptancePassed=false`，`failureCode=UPILOT_ACCEPTANCE_FAILED` |
| 后续编译错误 | `compileOperationId=80c10b2bcc6549b0a858c3b56d9a574e`，`compileOrigin=unity_auto`，total=121，errorsVerified=true |
| Console 查询 | matchedCount=121 |
| 摘要 SHA256 | `35198ad57e382d8ec8b71c9c6e36c12e9570a9c81bbe6cf5dda9c97c3d5ae711` |

摘要中的错误样本指向 `D:/upilot/Log/UPilotValidation/TimingConfig_20261014/staging/observations/modified/Editor/Core/UPilotModalObserver.cs:15`，错误为 `CS0101`：命名空间内重复定义 `ExpectedModalPayload`。该暂存目录位于包源码导入范围，`.cs` 副本被 Unity 作为源码处理，不能因为目录名是 `Log` 或文件被 Git 忽略便认定不参与导入。

修正了临时补丁的生成扩展名，副本改为 `.cs.staged`；正式应用时才写目标 `.cs`。问题及集成建议记于根 [TODO_UPilot.mcd](../TODO_UPilot.mcd) 的 `UPilot-20261014-StagingSourceImport`，未扩展 Skill 或规则模板。初次摘要保留为失败，不删除错误证据、不降低 Console 门槛。

随后清理批次的正式回执为：

| 字段 | 值 |
| --- | --- |
| WriteBatch | `wb-79db47cc-fcc1-41b7-aa85-d89c57506793` |
| 注册时间 | `1791945634608` |
| 编译 Request | `req-529042eb-f735-4d42-bd80-1f490ed4d8fa` |
| 编译 Operation | `c243ea1b646e42dc891ddb52abde0987` |
| 验证时间 | `1791945650884`，不早于该批次注册时间 |
| 结果 | 0 errors / 0 warnings；`correlationVerified=true`、`errorsVerified=true` |

完整清理回执已导出至集中证据的 `batches[key=timing_staging_cleanup_status]`；上述身份和结果不替代初次失败摘要。

## 后续两轮官方定向验收

| 字段 | 验收一 | 验收二 |
| --- | --- | --- |
| 开始时间（+08:00） | 2026-10-14 10:58:20.666 | 2026-10-14 11:05:53.439 |
| Task | `task-a650b239-dcd3-42c3-8f2b-716a5adc3ddf` | `task-b345fd44-acc8-4423-a6f9-f7b7bd3f1dd2` |
| Run | `2890e698-9055-4ff4-a3ff-48ae40096724` | `f0695614-aab6-455e-b430-1bee79b61627` |
| 测试结果 | 47 passed / 0 failed / 0 skipped | 32 passed / 0 failed / 0 skipped |
| 验收 | `acceptancePassed=true` | `acceptancePassed=true` |
| 清理与身份 | `cleanupVerified=true`、`testIdentityVerified=true` | `cleanupVerified=true`、`testIdentityVerified=true` |
| 运行期间输入 | `sourceUnchanged=true`、`importInputsUnchanged=true` | `sourceUnchanged=true`、`importInputsUnchanged=true` |
| 编译复用 | 复用已关联验证的原编译 | 复用已关联验证的原编译 |
| WriteBatch | `wb-e20fe700-3085-4638-ad59-9d28c5db82e3` | `wb-5668295a-0b8e-44ea-9fbc-f7b712d3eb71` |
| 编译 Operation | `f2a309d25db046bbb78470f4c27a57f3` | `0aae494faacd4cf09558b0cb13158b90` |
| 编译错误／警告 | 0 errors / 1 warning | 0 errors / 4 warnings |
| Console 查询 | matchedCount=0 | matchedCount=0 |

验收一覆盖周期参数、窗口／模态、包恢复等待、Step，以及选定 Capture 和 Maintenance 生命周期。验收二覆盖服务 Health、Reload 观察、启动身份与周期安排、主窗口配置缓存、窗口退出，以及 Step 管理窗口刷新。精确 selectors、匹配叶子和结果保留在原摘要中。

两轮测试 Console 证据均为 `rangeCoverage=complete`、`phaseCoverage=partial`，阶段缺口为 `console_cursor_clear_or_reload_not_detectable`。因此，Console 查询匹配零错误不扩展为“整个编译与测试全过程完整无错”。警告数量按原回执保留，没有把未运行、跳过或未知检查写成通过。

### 最后两轮恢复顺序回归

| 字段 | Bootstrap 恢复顺序 | 首帧恢复入口 |
| --- | --- | --- |
| 开始时间（+08:00） | 2026-10-14 11:18:48.043 | 2026-10-14 11:24:24.165 |
| Task | `task-8dba897f-bf4d-45c4-a44b-35265e98c106` | `task-c533b453-34c6-4362-abf5-af7f32138520` |
| Run | `7ec86dfa-fc24-4c71-b7d6-684ea744f350` | `ea09fb0e-694c-4537-9af4-f90f2357d33a` |
| 测试结果 | 14 passed / 0 failed / 0 skipped | 10 passed / 0 failed / 0 skipped |
| WriteBatch | `wb-cef2a834-1ff8-4961-8ff9-5449ebaceecf` | `wb-11d5d30c-9f01-4f42-a9fe-611134c6b270` |
| 注册／编译验证时间 | `1791947868743` / `1791947898661` | `1791948186776` / `1791948205697` |
| 编译 Request | `req-86edd6c7-be34-4e35-909d-64af4d1a5fff` | `req-42d879da-fb4a-40a7-99b1-9e7ef63a4d13` |
| 编译 Operation | `5413c58839b34429aa9afea1f286af2c` | `29ef2badeab043e096435edc94bea0fb` |
| 编译错误／警告 | 0 errors / 4 既有 warnings | 0 errors / 1 既有 warning |

14 项选择 `UPilotInitialServiceRecoveryTests` 与 `UPilotReloadObservationTests`，最后 10 项选择更新后的 `UPilotInitialServiceRecoveryTests`。两批均 `correlationVerified=true`、`errorsVerified=true`；两轮均 `acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged/importInputsUnchanged=true`，结果权威且清理成功。Console matchedCount=0，仍为 `rangeCoverage=complete`、`phaseCoverage=partial`，同样保留上述阶段覆盖缺口。

### 摘要与源码身份

五次摘要记录的 Git commit 均为 `28f4190df741f686a4d3e4076509740bde8d511d`，但当时工作区源码内容不同，应使用各自的 `sourceSha256` 区分；不能仅凭同一个 commit 把不同批次视作相同源码。

| 证据 | 内容 SHA256 |
| --- | --- |
| [初次失败摘要](../Tests~/UPilotTest/Log/UPilotAcceptance/1791945448878_req-b597b011-ab8c-4e5d-bf56-4caf531316af/summary.json) | `35198ad57e382d8ec8b71c9c6e36c12e9570a9c81bbe6cf5dda9c97c3d5ae711` |
| 初次 `sourceSha256` | `4f0278c9c08d2f5114426873a6f2155bf3ee929694675e3459719c4ae5bdcbec` |
| [47 项验收摘要](../Tests~/UPilotTest/Log/UPilotAcceptance/1791946700666_req-4a42faee-96f0-4fb5-9ca9-ec745018a664/summary.json) | `22fa25da30d90301752bf5c4d6b0fa21a42a1cd406a90df5fb15688827aceab9` |
| 47 项 `sourceSha256` | `63c264596924600031a609af89b6f28d5ea4b17d6573f44661542b7383771060` |
| [32 项验收摘要](../Tests~/UPilotTest/Log/UPilotAcceptance/1791947153439_req-10a06903-61d8-4840-99e5-b24ae2a5cb88/summary.json) | `7cc0caf14cf295852bc419a1a596e8a3775f1f87b921a7585122a1f0cf84e0e7` |
| 32 项 `sourceSha256` | `2ca32d5b628c0f835632905a1548c3e3b2d4cbb272e34c3f33e47bb42d422e50` |
| [14 项验收摘要](../Tests~/UPilotTest/Log/UPilotAcceptance/1791947928043_req-fc3bc95e-e158-4d69-a269-ff165030e7be/summary.json) | `c3ea9fab22062cb9a01d836d0d2e400b8f4bfcef93b448dae05bfa1470727079` |
| 14 项 `sourceSha256` | `ffba96a902262162d932830369db728c4dc5cebd12caa60c17bfa9ee60350729` |
| [10 项验收摘要](../Tests~/UPilotTest/Log/UPilotAcceptance/1791948264165_req-68015755-7643-4fe7-b507-c4a55b6f53ca/summary.json) | `b44221b5bd1df76863c2f2a67e0043ec64df436d4295efd2aa76aca67eaa1ece` |
| 10 项 `sourceSha256` | `895c6bf979fcfc830b5f4a0d20b54ee2216c72998aa26481ca73fdf2ccf4a9dd` |

摘要文件 SHA256 已从对应本地文件计算核对，各次运行内的前后源码身份一致。最后恢复入口改动由最后 WriteBatch 和 10 项直接回归覆盖；没有把早期运行延伸为后续源码的验收证据。

## Bootstrap 恢复竞态：已修复并验证

已观察到真实启动顺序：首次取得 Manager 实例时把 `RestoreLaunchObservation` 排入 `delayCall`，同一个 Editor update 中 Bootstrap 又调用 `StartServer`；由于内存 `_launchState` 尚为空，Attach 路径建立并持久化新状态，覆盖原恢复记录。随后 delayCall 读到新记录，不进入原 Reload 复核，而走普通启动探测。

现场表现为 LaunchIdentity 最终 `readyVerified=true`，但 `reloadRevalidation.observationId` 为空、嵌套对象字段为零，`_launchReloadRevalidation=false`；`deadlineUtcMs` 使用了现场维护配置的 `now + 600 秒`，没有冻结本次应使用的 Reload `120 秒`预算。历史类型的 `null` 内联对象在 `JsonUtility` 恢复后可能成为空对象，需结合有效观察 ID 判断，不能仅判断引用非空。

修复使 Attach 创建／覆盖状态前先完成持久观察恢复，并以有效观察 ID 区分真实记录与空内联对象。现场还发现 `delayCall` 等待超过 80 秒仍未执行，因此补充一次性 Update 恢复入口；两个入口共享一次恢复控制，保留原观察身份、次数和绝对截止，不重放启动。根 TODO 的 `UPilot-20261014-BootstrapRestoreOrdering` 已关闭。

最后批次后的真实观察为 `recovered=True`、`reload=True`，ID=`afbb07d6f5134e38b862a330b22fbd52`，`status=succeeded`、`endReason=verified`；间隔 2 秒、单轮 5 秒、总期限 120 秒、最大 30 次、实际 1 次。开始 `1791948204556`、截止 `1791948324556`、轮次开始 `1791948206466`、结束 `1791948208097`。本次复核仍使用 Server PID 171612、Unity PID 20628，没有重启服务，也未用维护预算代替 Reload 预算。

## 服务刷新与现场观察

维护 `46838ded-3b87-43a9-a7b2-b4a643ef4db8` 为 `succeeded`，`queueReset/readOnlyVerified/healthVerified=true`。Server PID 171612 → 109724，Unity PID 20628 保持；接受 `1791948373090`，结束 `1791948378682`。这是服务刷新结果，不代表此前任务被恢复或重新执行。

刷新后以下 8 个选定入口及其返回层均为 `equivalent`：`WsOrchestratorServer._schedule_write_batch_deadline`、`WsOrchestratorServer._expire_due_write_batches`、`StateStore.expire_write_batches`、`StateStore._persist_round_evidence`、`CompileDomainService._compile_diagnostics`、`ProjectAnalysisDomainService.hang_status`、`StatusDomainService._queued_editor_diagnostics`、`main_thread_observation`。路径和源码哈希保留于集中证据；范围严格为 `selected-entry-code-only`，不覆盖全局值、闭包值、依赖或全部模块。

| 现场项目 | 实际结果与边界 |
| --- | --- |
| 无窗口 Health | UPilot 窗口数 0；`lastRefresh` 从 `1791948475666` 推进至 `1791948561886`，`next=lastRefresh+10000`，观察循环数 1。证明关闭窗口后仍推进；这两个离散样本不代表逐轮耗时测量。 |
| 双窗口 Health | Main 与 Status 同时打开，窗口数 2，观察循环数仍为 1；`lastRefresh=1791948594231`、`next=1791948604231`。 |
| 最后空闲状态 | 队列查询 `complete=true`、`items=[]`、`issues=[]`，各来源齐全；Capture Update／日志、Step、Maintenance 订阅均为 false，Health 仍为 true、循环数 1。仅报告这些可观察状态，不推断生产零 SQL。 |
| 3 秒心跳 | 对普通 `/health` 做 3 次被动读取，心跳时间 `1791948514514`、`1791948517518`、`1791948520522`，两段均 3004 ms；未请求主动 Bridge 探测。 |
| 长心跳与旧样本 | 临时 15 秒心跳／45 秒网络超时；样本年龄约 11.4 秒时，hang／compile／queued 的 not-pumping 及 `suspectedBusyLoop` 均为 `null`，原因 `editor_snapshot_not_fresh`。 |
| 普通窗口关闭 | 实际关闭返回 `terminal=true`、`closeVerified=true`、`state=closed`；不代表原生长模态或 `pending` 端到端场景已经验证。 |
| 设置与界面恢复 | 心跳恢复 3 秒、网络超时 9 秒；Main 窗口恢复 `(1225,322,442,463)`，Status 已关闭。 |

## 未覆盖范围

- 原生长模态／窗口 `pending` 端到端场景未实测；固定期限、关闭与释放分支由定向 fixture 覆盖。
- 公开状态没有生产 timer／SQL 计数，本轮未宣称现场零周期 SQL；相关结论限于隔离测试和选定入口加载证据。
- 未运行完整 EditMode、Unity 2022 或外部客户端验收，未测 CPU／GC 收益；Console 阶段覆盖缺口仍保留。

本轮没有触发 Release、修改规则模板或改变检查严格度。
