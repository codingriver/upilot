# 更新中心与有限异步停止：实现及验证

日期：2026-10-14。三项改动已实现，并在规范项目 `D:\upilot\Tests~\UPilotTest` 完成关联编译和 **58 项定向 EditMode 测试**。未运行完整 EditMode，也未实际停止当前服务或执行真实 UPM 来源切换。工作区改动未提交。

## 实现范围

| 要求 | 实现与验证 |
| --- | --- |
| 更新中心状态读取 | `UPilotUpdateWindow` 缓存更新、下载、Reload Guard 快照；复用高级设置／快速调试的 300ms 周期，范围 100～5000ms。周期值由变更事件缓存，不在每帧读取偏好。实际窗口连续调用 200 次 Update，未到期时读取计数不增加；状态通知和显式操作允许即时刷新。 |
| 窗口和 PlayMode 生命周期 | 窗口关闭卸载 Update、配置与状态事件、PlayMode 事件；PlayMode 延后操作由事件推进。关闭界面不停止后台更新；独立外部通知仍转发。 |
| 更新完成后延后检查 | 新增 `UpdateCenterPostOperationDelaySeconds`，默认 10s、范围 1～60s。按 operationId 去重，只保留一份冻结截止；重复终态不续期，手动检查消费原计划。关闭／Reload／新更新／服务 Reset 使旧计划及响应失效。 |
| manifest 请求 | 窗口关闭向本窗口的请求发送 CancellationToken；取消不触发 legacy URL 回退。结果、异常和 finally 均验证窗口代次和生命周期。相同终态通知不取消当前有效检查，Reload 恢复不重新安排历史完成操作。 |
| 有限异步停止 | 新增 `ServiceStopObservationTimeoutSeconds` 默认 30s、1～600s，`ServiceStopCheckIntervalMs` 默认 100ms、50～1000ms。请求开始冻结，父期限更短时优先；提前确认即结束。原同步停止等待和 Sleep 循环已移除。 |
| 停止目标与派发 | 有期限的工作线程准备身份、端口和保留句柄，主线程只派发原目标一次；派发前再次检查截止。重复活动请求共享原 Task，不重新准备目标或续期。原项目／命令／PID 创建时间／启动和重启代次校验保留。 |
| 退出及结束 | 活动期间挂载观察，生命周期／取消／截止先于检查间隔；先确认原进程退出，再检查端口。所有终态解绑并释放句柄；生产无目标路径验证生命周期退出到 None，重复推进不改写终态。 |
| 超时与真实退出分离 | `ServerStopResult.ObservationSucceeded` 与物理退出、端口结果分开；超期后即使两项物理证据变真，也不得继续更新交接。超时不自动再次 Kill 或重启。 |
| Reload 与自动恢复 | 保存原停止 ID、项目、服务生命周期、Editor PID、开始／截止、本次参数、原代次及目标创建时间。Reload 后只恢复原只读观察，域内代次采用新域基线，不重发停止。终态停止意图也阻止 Server／Bridge 自动启动；显式新启动或重启才清除。硬 Reset 改变生命周期后不恢复旧观察。 |
| 调用链 | MainWindow、托管服务更新和主动包更新迁移到 await；同一更新的终态停止不由窗口周期重试，迟到窗口结果须匹配原更新及生命周期。公开包准备入口迁移为 `PrepareForPackageUpdateAsync`，原同步入口已移除。 |
| 外部包注册 | `OnRegisteringPackages` 发起一次停止观察，后续恢复用原停止 ID 作为门禁。同步通知无法暂停外部 UPM，未确认停止则不交接恢复；请求观察与包恢复仍有各自期限。 |
| 日志及高级设置 | 普通开始日志显示默认最多等待30秒、本次配置及有效预算；结束日志只输出一次实际耗时和两项物理结果。无目标显示“已停止，无需再次停止”，超时／失败不写成功。高级设置显示下次参数与当前／最近观察的冻结参数、有效预算、已用／剩余时间、状态和原因。 |

配置均按项目保存，登记到现有恢复默认入口。服务维护的总期限和授权没有改为普通停止的 30 秒，也没有新增后台更新巡检。

## 编译和加载身份

- 项目：`D:\upilot\Tests~\UPilotTest`；endpoint：`http://127.0.0.1:8011/mcp`。
- Git 基线：`ce83e499a183cee8bf2daaad0a2f08dcbc310a59`，另有当前未提交改动，以证据目录的源码哈希为准。
- 最终 WriteBatch：`wb-34f7b141-8d5a-4b7d-9b5d-d84c4da4d783`。
- compile request：`req-2b0db1c0-d3e0-4108-b560-cc0585cffec4`；compile operation：`437e82fb4fa541d8833a36782859802f`。
- 创建时间 `1791984698756`；验证时间 `1791984713095`，验证时间晚于本批创建。
- `status=verified`、`outcome=passed`、`correlationVerified=true`、`errorsVerified=true`；结构化编译错误 **0**。
- 另有 4 条警告，来自未修改的 Flow 序列化字段和测试中的过时 AssemblyBuilder；未移除或放宽检查。
- Unity PID `127400`、Server PID `137124`、Bridge session `8f0dda72eba04660888f3eabcce61c99`、managed domain `20`；Reload ID `eba1482e86254f218238d067f89e52c8`。
- 最终测试响应来自同一 domain 20，携带同一最终 compile operation；新停止结果字段、实际窗口及生产停止入口的回归已执行，作为最新 Bridge 代码加载证据。
- Server 未修改、未重启；其诊断所列入口为 `equivalent`，只证明所列入口代码，不能扩大为所有模块和依赖验证。
- Unity 生成的 `UPilot.Editor.dll` SHA256：`176fd8cb790c80ba26488d9dd33da572aa848bd6c320f7b9f02cd3223fd7996a`；`UPilot.Editor.Tests.dll`：`2706a2745e52f27bfc5b1aa60c54ec2d663c10055a1b7c069b3e0e088068ef71`。

## 定向测试

最终 runGuid：`b86b6570-d9d8-4b56-a7d3-8ff9e2dcc8a4`。58 passed、0 failed、0 skipped；`terminal=true`、`resultAuthoritative=true`、`cleanupSucceeded=true`、`cleanupResourcesReleased=true`。每轮均先用官方发现结果绑定选择域及快照，再通过 `unity_test_run` 开始一次并观察原 runGuid。

| 覆盖 | 数量 |
| --- | ---: |
| 更新中心周期、冻结延后、去重、取消及实际窗口解绑 | 10 |
| 停止观察、期限优先、任务共享、真实普通日志断言、无目标生产终态、Reload 终态意图、项目／生命周期隔离 | 19 |
| 项目周期参数、默认恢复及直接回归 | 8 |
| 包恢复固定预算、Reset、迟到结果及交接回归 | 8 |
| 原停止证据、PID 复用／端口归属及生命周期取消的指定叶子 | 12 |
| 托管服务包准备 API 的直接 Core 回归 | 1 |

中间结果保留：首轮 33/33 通过（`b29967a7-299c-4720-97c4-7d6200622203`）；第二轮 52/53（`42fcf63b-294c-437f-95fa-b43b79cc497f`），取消测试误要求精确基类，实际 TaskCanceledException 属于 OperationCanceledException，改为相应类型契约后最终通过。17 个注册批次中有 4 个中间编译失败，分别是调用链迁移遗漏及新增测试的 CS0819，已修正；最终结果不将这些失败写成通过。

## 证据与覆盖限制

证据目录：`D:\upilot\Log\UPilotValidation\UpdateCenterAndStop_20261014`。包含所有批次终态、最终编译、迁移错误、原失败测试、最终摘要及完整结果、选择快照、部署身份、17 个 C# 文件源码哈希和 SHA256 清单。

时钟边界、次数和去重主要使用隔离时钟验证；实际 Editor Update 证明等待期间其他回调仍可推进，生产无目标路径执行了真实端口可用检查和普通终态日志。过期父期限测试走实际异步入口并断言开始／超时日志各一次。未对当前 Server 发起 Kill，未制造真实 30 秒进程退出等待、PID 复用或物理端口争抢，未现场执行更新中的 Reload／外部 UPM 来源切换。这些限制不作为已经测过的行为报告。

本轮 Console 按 runGuid 查询返回 0 条停止日志，阶段边界报告 Console 清理／Reload 检测缺口；日志行为证据来自通过的实际生产入口 LogAssert 和终态记录断言，不能把空查询当成完整日志留存。未启动额外 Capture，没有 CPU／GC 性能测量；未改 Python，因此未重复运行 Server Python 回归。Flow 依赖安装等其他剩余项不在本轮范围。
