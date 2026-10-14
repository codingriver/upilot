# 命令 Watchdog 验证记录

日期：2026-10-14。活动门禁、项目配置、缓存更新和直接回归已完成。配置入口为 **UPilot → 高级设置 → 常规 → 周期与超时 → 命令 Watchdog 检查间隔（秒）**，默认 2 秒，范围 1～60 秒。

## 实现与边界

- 复用现有 Bridge 主线程 pump；Tracker 没有活动上下文时不执行普通／临界扫描，也不更新时间戳。没有增加 Timer、线程或 Update 订阅。
- 配置按项目保存，加入既有恢复默认入口；Bridge 通过设置变更事件更新缓存，每帧不读取 EditorPrefs。下一次检查以最后实际检查时间加新间隔判断。
- queued／executing／waiting／其他状态的 10／30／120／300 秒警告阈值、ReceivedAt 累计方式、单次警告和已完成条目过滤保持原有行为。检查间隔不改变命令期限。
- 原临界重启开关仍默认关闭；本次不改变 300 秒判定或强制重启 Unity 的动作。测试仅验证判定，没有执行该动作。主线程完全阻塞时 Watchdog 不能独立执行。
- `CheckWatchdog(tracker, now)` 是从原 pump 提取的局部方法，供独立 Tracker 的调度回归使用；真实生产 pump 验收没有调用该方法或注入时间。

## 关联编译与加载身份

验证项目为 `D:\upilot\Tests~\UPilotTest`，Unity `6000.6.0a2`，Editor PID `127140`。写入前保留编译基线 `req-ff5cb51b-8275-40b4-ae8a-48eb6946d174`，验证时间 `1791967990700`。

| 证据 | 本次结果 |
| --- | --- |
| WriteBatch | `wb-7d92b889-563b-4e0c-9a0d-6c1c78202c8c` |
| 编译请求 | `req-e52a528a-a06b-4bcb-be36-345e92fc0c63` |
| 编译操作 | `4b148e91aa444ebfa5f3a42dc9c6b99d` |
| 本批创建／编译验证时间 | `1791970313642`／`1791970360578` |
| 终态 | `verified`，`correlationVerified=true`，`errorsVerified=true` |
| 结构化编译错误 | 0 错误、1 个既有 `UPilotDiagnosticModuleTamperTests.cs:88` 的 CS0618 警告 |
| Reload 后身份 | domainGeneration `3`，Bridge session `65e718c51ae844bbb2a45b843120d6f1` |

最终 MCP 检查确认项目身份一致、connected/serverReady/ready/authoritative 均为 true，isStale=false，编译请求仍为本次身份。七个登记 C# 文件的最终 SHA256 均与本轮冻结记录一致；实际生产 pump 和新增缓存／调度测试提供本次 Bridge 行为证据。哈希仅用于源码稳定性记录，不将历史保存版本独立视为编译覆盖。

编译操作没有可用的持久 Console 边界，相关查询返回 `CONSOLE_EVIDENCE_NOT_FOUND`；随后使用 MCP 的实时 Console 错误查询，匹配数为 0。编译结论依据本批结构化、关联且已验证的终态，不把缺失边界视为通过。

## 定向测试与现场观察

| Run GUID | 范围 | 结果 |
| --- | --- | --- |
| `b9d06b20-34c4-4588-be35-1eafc5671c21` | CommandPhase 15 项，以及设置默认／范围／缓存 3 项 | 18/18 Passed |
| `2541248d-05a5-4a02-9ef1-25c2179e9e99` | SharedProductionPumpDoesNotReplayRealUnityMutation | 1/1 Passed |

两轮均为 authoritative completed，cleanupSucceeded=true，无清理错误。恢复测试首次因并行测试占用而被拒绝，runnerStartAttempted=false；等待该运行结束后才启动，没有取消其他任务。

真实生产 pump 临时采用 1 秒间隔：两次检查时间为 `3744.0679793800614` 和 `3745.068018211812`，相差 **1.0000388317507714 秒**。释放自有命令后，连续 **1.3015225398762595 秒**自然空闲期间检查时间戳保持不变；主线程 pump 时间从 `1791970471315` 前进至 `1791970472620`。自有命令已释放，偏好键的原存在性和原始值已恢复。

共享恢复 fixture 临时采用 1 秒间隔，并持有独立自有命令，观察真实检查时间戳从 `3907.7313784293815` 增长至 `3908.8232853616873`。原 Python 单调断言、45 秒 peer／65 秒 Unity 总期限及派发计数保持；观察命令已释放、配置已恢复，业务效果恰好一次，迟到结果未改变原终态。peer 正常退出，清理记录中未释放命令、排队动作与共享待处理动作均为零。

## 证据位置与覆盖限制

- 汇总原始证据：[final-evidence.json](../Tests~/UPilotTest/Log/UPilotValidation/Watchdog_20261014/final-evidence.json)，SHA256：`2d798abe666c82413446ac70ba71206f741e50c32b185a02db0286fa750a0234`。
- 周期与空闲观察：[watchdog-pump JSON](../Tests~/UPilotTest/Log/UPilotValidation/Watchdog_20261014/watchdog-pump-295f83f9f08b4f1e9f36a6270eba1a11.json)。
- 恢复、源码与清理证据：[shared-pump.json](../Tests~/UPilotTest/Log/Stage4/RealUnityRecovery/shared-production-pump-response_lost-b0b7e326ff03450987c2a56ac265e7ed/shared-pump.json)，同目录包含 report、owned-tasks、unity-cleanup 和 source-before/after。
- 恢复 fixture 的 source-before/after SHA256 均为 `5c6ff42441f7e8c710ca372cd60f7a750a0581219bfd7cce845643425e5f3101`。

本次只运行 19 项直接相关 EditMode 测试，没有运行完整套件、实际强制重启或 CPU／GC 测量。本任务没有修改 Server/Python 代码；Python peer 已随恢复测试验证。工作区并行的 Profiler 修改保留，不列入本项功能验收。
