# UPM 与 Server 五项优化验证

日期：2026-10-14。实现及定向验证已完成；规范工程为 `D:\upilot\Tests~\UPilotTest`，Unity `6000.6.0a2`，MCP 端点为 `http://127.0.0.1:8011/mcp`。未提交或发布本批改动，保留工作树中其他任务的修改。

## 已实现范围

| 项目 | 实际行为 | 直接验证 |
| --- | --- | --- |
| resource.packages 主线程阻塞 | 删除同步 Sleep，复用有限异步 UPM 观察；仅资源读取请求启动一次 Client.List，成功结构保持，失败明确返回错误。 | 观察调度回归及真实只读资源调用 |
| UPilot 更新回调残留 | 两入口共用观察，先幂等释放再通知或交接；Reset、Repair、来源切换、外部接管、Reload、退出均释放。包注册成功先结束观察再进入恢复，避免 Reload 误删交接状态。 | source Reset、registered completion 和迟到结果回归 |
| 普通包请求期限与 Reset | add/remove/list/search 与资源及自身更新共用冻结期限／间隔；Bridge Reset 主动结束活动及排队观察。到期先于节流判断，结果未知且不自动重发、不声称物理取消 UPM。 | 冻结参数、检查间隔、超时、取消、排队 Reset、异常和幂等结束回归 |
| Server 配置重复读取 | refresh 锁内单次 read_bytes，同一字节哈希、解码、解析，未变内容复用字典；每次重新计算环境端点覆盖，权限入口仍按需 refresh。 | 文件读取／解析计数、权限撤销、强制刷新、BOM、坏配置、缺失及读取失败回归 |
| 心跳重复终态关联查询 | 单条已持久化且验证通过的终态关联缓存；完全匹配稳定心跳复用，活动／新批次、身份变化、显式查询及持久化失败失效。权威快照和新鲜度继续保存。 | SQL trace、共享轮次、回滚及证据验证回归 |

高级设置入口：**UPilot → 高级设置 → 常规 → 周期与超时 → 包请求与恢复**。新增本地请求观察期限默认 **600 秒，30～3600 秒**；完成检查间隔默认 **100ms，50～1000ms**。按项目保存、支持恢复默认，本次观察开始时冻结，修改只影响下一次请求。原包更新恢复等待独立保留，默认600秒。界面读取本地观察记录并显示身份、类型、本次参数、状态、已用／剩余时间和原因，不启动包清单请求。

Server 响应预算保持资源30秒、add/list/search120秒、remove60秒。Server 等待超时不等于 Unity 侧观察结束。本地期限仍依赖主线程推进，不能抢占已阻塞的原生操作。

## Unity 关联编译

每批 C# 保存后登记原 WriteBatch，所有六批均 `verified`、`correlationVerified=true`、`errorsVerified=true`，验证时间不早于登记时间，编译错误数为0。以下表仅证明相应轮次处理批次，不宣称每个历史文件版本单独编译。

| 批次 | 用途 | lastCompileVerifiedAt（UTC ms） |
| --- | --- | --- |
| wb-1a7597a7-dfd0-4057-8703-6bc1e1fdfea5 | 共享观察、包服务、资源、Bridge、更新及高级设置生产修改 | 1791973593569 |
| wb-b88f4050-0106-44e0-9078-235dadcddad3 | 直接回归及配置覆盖 | 1791973741174 |
| wb-b5d9aedb-e057-4b9a-9acd-e1ca16634763 | 更新结束路径相关回归 | 1791973922557 |
| wb-6951e9fe-19f9-4613-bca9-a01041b16a84 | 包注册成功到 Reload 的观察交接修复及测试 | 1791974044670 |
| wb-059974fd-6390-40bc-a687-22a6b5df2db9 | 修正测试所用实际 SessionState 键 | 1791974173239 |
| wb-41790046-b821-42b0-9b47-47bb45ae6c05 | 调度见证测试由 delayCall 改为独立 Update | 1791974560303 |

最后编译身份：request `req-a15113af-5967-47af-a304-c05136d06ebb`，operation `55e7dcf8c15a4aca9a02a04d5ba66aa4`；0错误，1条既有 obsolete AssemblyBuilder 警告。完整批次状态及结构化错误存于证据目录；没有在无代码变化后再次编译。

## 定向 EditMode

选择 `UPilotUpmObservationTests`、`UPilotPackageRecoveryTimingTests`、`UPilotTimingSettingsTests` 三个 fixture，共27个叶用例。测试经官方 MCP 启动与原 runGuid 查询，未跑完整 EditMode。

| 原 runGuid | 结果 | 说明 |
| --- | --- | --- |
| 6bf09908-62d3-4898-8c5f-ea13b7086d07 | 27总，26通过，1失败 | `AnUncompletedObservationAllowsOtherEditorCallbacksToRun` 在无人交互时20帧内未收到 delayCall；保留原失败消息和堆栈。 |
| f4322e6f-2dc6-4ef3-be88-c81fdf9c92d9 | 1总，1通过 | 仅复验失败叶，改用独立 EditorApplication.update 证明 Editor 继续推进；仍检查未完成观察、单次请求与回调释放。 |

两次均为权威终态、cleanupSucceeded，资源已释放。可以说27个不同用例都有通过证据（26加1复验），不能说一次27/27通过。初次失败是测试调度假设，未据此判断产品同步阻塞。曾传入无效 `matchMode=exact` 被入口拒绝，未启动测试；正式请求采用支持的选择规则。Console 证据覆盖为 partial，原因 `console_cursor_clear_or_reload_not_detectable`，不宣称完整 Console 覆盖。

## Python 回归与 SQL 证据

最终一次定向 pytest 为 **341通过，0失败，0跳过**，两条既有 websockets 弃用警告。选择以下文件，命令从 `upilotserver~` 的 `.venv/Scripts/python.exe` 执行：

```text
tests/test_config_refresh_reads.py
tests/test_terminal_association_cache.py
tests/test_loaded_source_evidence.py
tests/test_v02_contracts.py
tests/test_reflection_write_safety.py
tests/test_editor_execution_state_v2.py
tests/test_write_batch_rounds.py
tests/test_write_batch_deadlines.py
```

`test_stable_terminal_heartbeats_persist_fresh_snapshots_without_association_selects` 对已提交终态发送12次稳定心跳，trace 证明编译关联 SELECT 为0，同时 project_state UPSERT 为12。其他直接回归检查显式查询仍读持久证据、活动新批次不被旧缓存隐藏、身份／验证变化、失败持久化失效、共享轮次 owner 保持；原事务回滚及损坏证据测试通过。此计数来自隔离数据库，不外推为生产 SQL 或 CPU 降幅。

## 部署与真实调用

刷新前已核对精确项目、完整新鲜任务队列为空、无活动 Console Capture、无其他活动 Operation。存在独立有效 AI 服务维护授权，沿用用户600秒维护期限，发起一次 maintenanceId `186a48eb-6a67-4bc1-ae7e-e87f321aed26`，未重放。

维护最终 `succeeded`，queueReset、healthVerified、readOnlyVerified 均为true。Server PID **161852 → 137124**；Unity PID保持 **127400**，未重启 Unity。Bridge session **16b462cba9884e04baf833edc958e9e3 → bafdc0ec37dc4c0bac7dc2eaf0c582b1**。

刷新后以下四个选定入口均 `equivalent`：config.refresh_config_if_changed、config._config_from_raw、StateStore.update_editor_execution_state、StateStore._terminal_compile_key。加载比较范围是 **selected-entry-code-only**，不证明 globals、依赖或全部模块。

重新读取 `unity://packages/list` 成功，request `req-c46365f7-6330-4717-b457-0b85f8db0789`，总耗时219ms，返回50个包，含 UPilot0.3.46。成功结构保持，未安装／卸载包。最终完整队列 `complete=true`、`isStale=false`、items为空，Bridge活动／排队／执行／未追踪均0，issues为空。

## 证据与限制

本地证据目录：[Log/UPilotValidation/UpmAndServer_20261014](../Log/UPilotValidation/UpmAndServer_20261014)。包含原始批次、编译错误、初次失败、复验、维护前后状态、真实只读资源摘要、最终队列及 Python JUnit；可能被仓库忽略，不视为已提交的CI证据。

[final-evidence.json](../Log/UPilotValidation/UpmAndServer_20261014/final-evidence.json) 汇总结果、19份产物哈希及交付时16个相关源文件哈希。SHA256：`9288590eaa0309573d2e136f7a654dfdf817be687ffe5385ab060842fd3da7ef`。交付哈希不是历史批次覆盖证明。

未执行真实安装／卸载、物理取消或600秒原生UPM超时验收；有限期限、Reset与迟到结果由受控观察回归验证。未跑完整EditMode、Unity2022、外部项目验证，未测生产SQL、CPU或GC收益。真实只读清单成功不能替代这些未测场景。
