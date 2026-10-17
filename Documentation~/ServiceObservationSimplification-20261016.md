# 服务观察简化与执行诊断

日期：2026-10-16；验收更新：2026-10-17。状态：关联编译、分批定向 EditMode、Python 与一次现场服务重启已验证。首次失败保留；不代表全套回归、升级或原业务采样验收。

## 实现边界

- Server hello ACK 提供当前 PID、配置项目及版本/来源；Bridge 保存当前 socket/hello/epoch 身份。控制路径从当前 ACK 读取身份，不回退 HTTP Health。
- 启动沿原 WaitForVerifiedStartupAsync 及在途合并，验证当前 bootstrap 回执，不新增 HTTP 验证接口。
- 普通重启只验证授权目标、旧服务退出/端口释放、新身份认证和当前只读回执。普通重启不下载配套版本、不改写用户配置入口；升级另行验版本。
- Health 缺省 20 秒、保存值保留；单项目完成后调度，独立诊断快照，原异常及 HTTP 上下文输出 Console Warning。
- 服务观察 Warning 的内部来源随 Console/Capture/聚合传递；按线程、发射范围与 Unity 同步日志回调深度隔离同文嵌套日志，业务日志策略及证据完整性不放宽。该嵌套边界已在 2026-10-17 首轮定向测试中通过。
- QuickStart 不再把 Health 的生命周期观察写成恢复门禁；观察异常不写自动修复失败标记，旧 UI 历史只能凭已有当前回执清理。
- schema 5 与 MCP 投影配套；旧历史列表和终态保留，完整 v4 在途凭据验证后迁移，缺失凭据不重放。
- 执行预算长整数及边界证据、调度/目标耗时、十组有界阻断摘要和完整策略文件。

## 2026-10-17 定向验收结果

规范项目 `D:/upilot/Tests~/UPilotTest`，Unity `6000.6.0a2`，Unity PID 3680。
证据汇总：`Tests~/UPilotTest/Log/P0P1/service-observation-20261017/acceptance-summary.json`。

- Python 七文件定向测试：**258 passed，2 条已有 websockets 弃用警告，14.98 秒**；本轮 JUnit 为同目录 `python-targeted.xml`。不与历史运行累加。
- 初始关联编译 `wb-0c42f1f8-2082-4f85-9a69-4b909bc51537`：verified/passed，`correlationVerified=true`、`errorsVerified=true`；compile operation `7ecc6e1aa7de4c4e805bb1853cdc0b4b`，结构化编译错误及关联 Console Error 均为 0。
- 首轮 221 项：**216 passed / 5 failed / 0 skipped**；task `task-f8419f5c-53c2-4726-9eee-551dd15d0151`，run `06cb7d1f-2307-4e55-8192-e8da56b7e06e`，清理成功、结果权威、源码未变。原报告保持失败，不回写通过。
- 5 项失败均定位到两处夹具输入：日志规则辅助函数只配置 Error 却要求 Warning 命中 deny；启动观察夹具仍只填 Health，没有独立 ACK Identity，导致三个成功路径无法完成、旧版本路径未进入版本检查。修正 deny 的显式 Warning/Error 范围与 ACK 身份输入，将负向身份案例改为修改 Identity，未放宽产品门禁或断言。
- 新增 3 项证明 Health 缺失、Health 身份冲突和普通版本差异不阻断必要条件已满足的普通启动。显式升级版本拒绝用例保留。
- 夹具修改关联编译 `wb-1edfef56-eea4-4f26-912d-c156e89d2093`：verified/passed，`correlationVerified=true`、`errorsVerified=true`；operation `317ba7d4d26f41ab8a43a4136dd6a153`，verifiedAt `1792206828279` 晚于登记 `1792206627657`，0 错误。重载曾停留 BridgeBootstrapPending，约 200.6 秒后在原期限内恢复；未重发编译、重启或延长期限。
- 仅重跑受影响的 `AutomationLogPolicyTests` 与 `UPilotVerifiedStartupWaitTests`：**41/41 passed，0 failed/skipped**，task `task-8a2ae035-06d9-46ed-acb8-7e668cabe0a6`，run `96f8e235-a3f0-461a-984a-61d12a221d34`。`acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true`。其余 183 项的首轮通过证据保留；没有把多轮结果包装为一次完整运行。
- 复验产物 `Logs/UPilot/Reports/Acceptance/1792206884579_req-4836048a-7553-4b5e-abe6-2381875ecc88/summary.json`，633262 字节，实际 SHA256 `5edd54cae097354b54e8dda8ae28db36bb1e0b0b132330b32e38e66319e55577`；首轮失败报告实际 SHA256 `b21d54fbd1197448616ad56f43df4c89c2cafa85d94d58a524efd3fdac6302cb`，1398693 字节。两者均与 MCP 返回元数据匹配。

### 一次现场普通重启

独立维护授权有效，预检无活动 Capture、运行中测试或业务任务；并行只读查询产生的短暂命令不作为业务在途工作。仅发起一次新维护，不重放历史失败：

- maintenance `3224f6a3-3ff3-45f7-8adb-a196acc40f4b`，restart `restart-60429cd42b284bd6b6062b34539458c9`。
- maintenance 与 schema 5 restart 均为 succeeded，`verifiedSucceeded=true`，耗时 **7892ms**；保留项目保存的 600 秒维护期限。
- Server PID `19084 → 28124`，Unity PID 仍为 3680；新 Bridge session `1c492394d3a444b7bcad7cf308c93165`。OS 身份、旧进程退出、端口释放、新项目/会话身份和只读回执均通过。
- `healthVerified=false`、`deploymentVerified=false`，必要门列表没有 health/deployment；`readOnlyVerified=true`、`queueReset=true`。旧字段中的 `healthProbeCount` 是兼容投影，不能解释为实际执行了 HTTP Health 验证；本次结果以 schema 5 必要门和 bootstrap 回执为准。
- 当前 bootstrap `bootstrap-cadbba98e8094dbcb5d855d04983f65c`，hello、nonce、新服务生命周期及状态生产者均在 `restart-after.json` 留存。随后 `restart-stable.json` 仍为同一 PID/session、ready/authoritative/fresh，没有第二次 stop/start。

### 证据边界与待办

- 两轮测试 Console 分阶段覆盖为 partial（`console_cursor_clear_or_reload_not_detectable`），不能宣称阶段日志完全无丢失；权威测试结果与清理通过结论不变。
- Server loadedSource 为 equivalent，仅覆盖返回的 selected-entry-code，不是全部模块、闭包或依赖的部署证明。
- Health 错误、身份拒绝及日志来源等负向条件由定向测试验证；未对现场服务注入 HTTP 故障，也未实际下载/升级包、发布版本、运行全套 EditMode 或 H 项目的业务 Step。
- 重载后 hello ACK 到 ready 的延迟仍需独立复现/定位。当前证据仅证明原批次最终恢复，不能归因为模态窗口、死锁或 Health；已登记根 TODO。

## 2026-10-16 历史验证与阻塞（保留）

Python 最后一次合并定向验证（仓库现有虚拟环境，七个相关测试文件）：

~~~
.venv/Scripts/python.exe -m pytest tests/test_bridge_readiness.py tests/test_session_identity.py tests/test_server_restart_diagnostics.py tests/test_execution_tools.py tests/test_service_maintenance.py tests/test_editor_lifecycle_health.py tests/test_editor_observation.py -q --junitxml=../Tests~/UPilotTest/Logs/UPilot/ServiceObservationValidation/python-targeted.xml
~~~

结果：**258 passed，2 个已有 websockets 依赖弃用 Warning，15.07 秒**。
这是最后一次合并结果，不与之前的 133/117 次执行相加。
旧注册表测试的精确版本断言由 8 同步到当前源码 9，写门禁和非幂等断言未放宽。
已修改文件的 CRLF 感知 `git diff --check` 通过。

Agent/Skill：权威 manifest 为 rules 59 / skill pack 70；已完成生成、源验证、五目标同步、
两套安装验证和重复 current 检查。保留受控块外字节及重复同步文件哈希/时间证据；
MCP 断连时按 installation.md 使用精确项目/源码的官方 CLI 维护回退。
证据目录：`Tests~/UPilotTest/Logs/UPilot/ServiceObservationValidation/`。

Unity 现状（不得解释为通过）：

- 前两批关联编译曾通过，只覆盖当时输入。
- `wb-c621c210-409b-4b4b-b0e7-1428b0a42018` 和 `wb-587e7ca1-734d-4825-905a-a0d723418df3`
  保留 failed，correlationVerified/errorsVerified=true。实时结构化错误读取随后超时；未据此猜测错误原因。
- 后续 compiler_finished 的零错误快照停在 Domain Reload，缺少 verified terminal，不能作为最新编译通过。
- `wb-7eeeaa12-32db-48c6-9eaa-08410eeefc79` 已按原 600 秒到期，timed_out/outcome=unknown，未续期或复活。
- 后续修正均登记原 WriteBatch，最近 `wb-68c66b83-44cb-4270-be46-189af7ed8c6f`
  在登记时 deferred/disconnected；须观察原 ID 的真实终态，不另起并行编译。

部署/现场阻塞：

- 正确项目 `D:\upilot\Tests~\UPilotTest`，Unity PID 38044，创建身份 134366077783973010；
  Bridge 仍断连，Server PID 103936 的选定已加载函数与磁盘源码不同。
- 维护尝试 `a195720e-12da-44e7-b4cd-81d4fcd35bbf` 返回
  `SERVICE_RESTART_IDENTITY_CHANGED`（current maintenance owner or authorization changed），未重发或绕过授权。
  持久化最新维护仍为旧 `b9a2d1e4-5c30-4df3-b258-c2e3761d9f0d` failed，未改写历史。
- 只读 hang 诊断观察到可见窗口“UPilot 自动修复失败”，可能阻挡恢复；因果尚未证实。
  已请用户关闭提示并恢复连接。CPU 忙不等于已证明 busy loop；未重启 Unity。

恢复后先确认当前部署/项目身份，读取结构化编译错误并观察原批次；已过期批次不重放。
按当前实际源码完成关联编译和下列定向测试，再验证现场重启与升级边界。
未宣称最新源码 Unity 编译、EditMode、配套部署、现场重启或升级验收通过。

## 计划定向覆盖

UPilotServiceHealthObservationTests、UPilotRestartObservationPolicyTests、
UPilotBridgeHandshakeTests/UPilotBridgeReadinessTests、UPilotDiagnosticLoggingTests、
UPilotServiceMaintenanceTests 的 ACK/Health 区分、UPilotLifecycleHealthTests 的观察不重置自动修复用例、
UPilotExecutionCoreTests 的新增预算/耗时测试、AutomationLogPolicyTests、
AutomationReportWriterTests、UPilotConsoleCaptureApiTests 的新增来源测试。
不得用全套回归替代这组定向验收；原业务错误及原采样预算不在已验证范围内。
