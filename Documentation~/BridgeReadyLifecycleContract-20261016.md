# Bridge Ready 与服务生命周期隔离

更新日期：2026-10-16。本页记录本次修复的契约、门禁、时间预算及阶段证据。实现已集成到源码，Python 定向回归已通过；规范项目最新关联编译、Unity 失败夹具修复后的复验及当前 Server 加载来源核验仍未完成，不能报告最终验收通过，也不能引用其他修复的历史通过结果代替本次验收。

## Ready 的含义

HTTP 可以响应、端口监听、进程归属正确或 hello 已认证，都只是中间证据。Bridge Ready 还要求当前连接完成主线程初始化和首次权威状态同步。Server 与 Bridge 必须同时支持 `bridgeReadyContractVersion=1`；缺失或不匹配时拒绝，不回退到旧 hello 即 Ready 的行为。

完整顺序如下：

1. Unity 发送 hello，保留本次发送的确切 `helloId`。Server 验证精确项目、Unity PID/创建时间等身份；Unity 在接收和主线程应用 ACK 时都验证原 socket、连接 epoch、session、hello ID、契约及服务身份。接收线程认证不开放普通请求。
2. 主线程应用 hello，完成既有维护恢复初始化，观察到 `recoveryChecked` 后发送 `session.ready`。该消息只是本地初始化完成通知，仍不开放普通请求。
3. Server 对该连接发送一次带 bootstrap ID 和 nonce 的 `session.bootstrap`。Unity 在主线程刷新 Editor 事实；仅在没有编译或资源更新时完成编译错误复核，然后构建并持久化完整权威 Editor 快照及对应编译证据。
4. Server 检查连接、挑战、项目/进程/producer 身份、快照顺序和编译证据一致性，持久化状态后发送 `session.bootstrap.ack`，才打开其 Ready 门禁。
5. Unity 仅接受当前连接、原 hello、服务生命周期/实例、bootstrap ID、nonce、snapshot ID、producer epoch、domain generation、sequence 以及原 Ready 期限全部匹配的 ACK。此时打开内存准入并保存同步收据；公开 `IsStateSynchronized` 还要求 Bridge 已启动、已认证、未 reset、socket 为 Open。

重复 bootstrap 不替换原挑战或重复排队；旧 ACK、过期 ACK 和身份不完整的 ACK 不能打开门禁。断开、重连、停止、Reset、Domain Reload 或主动关闭准入立即撤销收据和普通请求权限。

Manager 的启动、重启和已有服务复用仍验证 OS 进程/创建时间、命令、双端口、精确项目及部署身份。最后复用**当前连接的 bootstrap 收据**证明主线程实际往返与状态同步，不再为了达到 Ready 额外发起依赖普通请求准入的 HTTP/Bridge 自探测。原 `CreateReadOnlyProbeClient` 的 7 秒兼容 helper 不构成新的必经检查。

Manager 只在同一验证 owner/generation 和有效收据仍成立时，一次提交成功标记、生命周期完成与状态缓存。收据在提交前被撤销或连接已更换时，保留原观察器与截止时间，不提前清除待复核状态，不显示验证成功。

受控 replacement 单独使用 `_shutdownLaunchIdentityConfirmed` 记录本轮实时 OS 归属确认。该标记在新 shutdown owner、替换 launch 建立及 shutdown 结束时清除，仅在原 owner/generation、当前 launch 引用及启动 generation 仍匹配、原期限未到且实时归属核验成功后置位；历史持久化 `verified` 不能单独满足条件。hello 可据此在原身份与期限约束内复用既有 reset 收据，避免等待最终 Ready 才允许 hello 的循环依赖。`_launchNeedsRevalidation` 仍保持待复核，直到当前 bootstrap 收据通过最后提交；OS 归属确认不代表 Ready，不重置预算，也不授权再次启动或重启。

**连接同步成功与 Editor 可写、编译通过、测试通过分别判断。** Bootstrap 可以如实同步正在编译、待验证、编译失败或尚无已验证编译记录的状态；它不把这些状态变成成功，也不补造编译身份或 `errorsVerified=true`。

## 请求门禁矩阵

| 当前阶段 | 普通命令与业务写入 | Manager 普通 Health / Stats | 窄范围内部工作 |
| --- | --- | --- | --- |
| 初次恢复未完成、未认证或 hello 尚未应用 | 不准入 | 不派发、不提交普通缓存 | 连接、hello、身份验证及本地恢复 |
| hello 已应用，主线程初始化或 bootstrap 尚未确认 | 不准入 | 不派发、不提交普通缓存 | 原连接的 Ready/bootstrap 控制消息与主线程快照持久化 |
| Manager `Starting` / `Stopping` | 不准入；停止起点立即撤销旧权限 | 不派发；迟到结果不能提交 | 原生命周期 owner 的启动/停止工作；停止前独立、有限的归属安全核验 |
| Manager `Verifying` | 收据尚未成立时不准入 | 普通观察保持关闭 | 仅 owner 内部 Health 探测及 OS 身份核验，随后复用精确匹配收据 |
| 已同步，编译或编译复核进行中 | 普通操作返回 `EDITOR_BUSY` | 生命周期空闲且其余门禁满足时允许 | 只允许下述编译观察清单；本地编译回调继续持久化真实状态 |
| Domain Reload / 断线 / 重连 / Reset | 立即关闭；旧队列和回调不恢复执行权 | 停止普通观察，拒绝迟到结果 | 按原期限重建当前连接及 bootstrap；Reset 中返回对应 reset 状态 |
| 已同步且生命周期空闲 | 依各工具原权限、Editor Ready 及业务条件准入 | 初次恢复已完成、无 shutdown/transition/maintenance/显式停止时允许 | 正常主线程工作及有活动任务的观察 |
| 显式服务维护 | 普通命令保持关闭 | 普通观察保持关闭 | 仅精确匹配当前身份的 `service.restart` 维护控制包；仍须原授权、项目/PID 和维护 owner 检查 |

编译观察清单是 `compile.wait`、`compile.errors.get`、`editor.state`、`resource.editorState`、`test.status`、`test.results`、`queue.snapshot`。清单不能绕过连接同步或 Reset 门禁。编译回调可以在尚未 Ready 时更新本地权威观察，不能由此开放普通网络状态发布。

维护控制不是通用 bypass。只有 `controlKind=service.maintenance`、契约、hello ID、服务生命周期/实例和预期 Bridge session 全部匹配的 `service.restart` 才进入原维护处理器；不能自启用维护权限，也不将普通请求升级为维护请求。

## 旧连接与迟到结果隔离

每个请求绑定其原 socket、连接 epoch、session、Server 生命周期、本地服务生命周期、准入 generation 和取消 token。接收派发、主线程 tracked/control 队列执行、帧发送前及取得发送锁后均复核来源。命令结果保留原来源，不能借重连后的当前连接发送。独立生成的新状态/事件才可捕获当前连接，且仍遵循 Ready 门禁。

Manager 普通状态观察同样在接纳、HTTP 发送、返回和缓存提交处复核生命周期与 Bridge 同步状态。开始停止时先撤销准入，再等已有锁或清理，避免这段间隙继续发送 Health/Stats 或提交旧结果。

显式 `/health?probe=bridge` 在 `mcp_stdio_server.py` 中把发送 `editor.state` 和等待回包纳入同一次固定 5 秒预算，固定本次 orchestrator、session 对象及 bootstrap 收据，并在返回前重新核对当前 Ready、来源 generation 和收据。停止、同 session 换代、orchestrator 替换或移除后，不返回旧 Ready 或旧收据；缺少有效 generation 的回包不能证明成功。Timeout、ConnectionError 和 OSError 输出带操作上下文与原堆栈的 Error；`CancelledError` 继续传播，所有路径都从原 orchestrator 清理本次 pending。这仍是显式诊断入口，不恢复 Manager 的额外自探测。

显式 Repair 的启动前检查使用独立 preflight，避免普通 `GetFreshStatus` 在未 Ready 门禁下返回旧终态失败，使新 restart 尚未发起就抛错。检查固定当前 HTTP/WS 两端口：两端口空闲只表示本轮未发现监听；存在占用时复用内部 Health 与精确 OS 归属核验，Unknown、Foreign 或缺失 PID 均拒绝继续；后续停止仍须通过精确 OS 归属门禁。该入口保留原 deadline、取消、generation 和端点漂移检查，不提交普通状态缓存，也不清除旧失败或重置预算。此修复已保存，新批关联编译与新增 11 个定向情景尚未执行完成。

取消、正常停止和 Domain Reload 中断是正常结果；不能包装为 WMIC 查询故障。真实进程查询、输出读取、持久化或 helper 清理失败仍保留红色 Error/Exception、原异常栈和操作身份。相同根因按既有 10 秒窗口聚合，不屏蔽测试期间的真实错误。

## 时间预算与周期

同一个数值可能用于调度间隔、单次 I/O 上限或父操作期限，含义必须分别说明。本次修复的 HTTP、后台归属与 bootstrap 有限步骤取自身上限与**原父期限剩余时间的较小值**；继续轮询、重新连接、重复查询和设置变更都不为已接受操作续期。表内其他既有周期或短观察保留各自边界，不能据本表声称所有旧 helper 都新增了父期限参数。

| 时间 | 用途 | 边界 |
| --- | --- | --- |
| 2 秒，默认 | 命令 Watchdog 检查间隔 | 可配 1～60 秒；只调度检查，不是命令执行期限；警告阈值仍独立计算。 |
| 2 秒，默认 | Reload 复核轮次间隔 | 可配 1～60 秒；从一轮结束到下一轮，不是单轮超时。 |
| 2 秒 | 普通 Health/可选 Stats 的单次 HTTP 请求上限 | 包括响应头与响应体，不是整个启动或维护期限；owner 显式使用剩余轮次预算时受该轮原截止约束。版本等待只取 Health，不取可选 Stats。 |
| 2 秒 | netstat helper 等待退出上限 | 与 stdout 读取、WMIC 共用后台归属轮次预算，不能各自重开一轮。 |
| 2 秒 | 启动/重启身份观察的常规下一轮间隔 | `RestartProbeIntervalMs`；不是 HTTP、WMIC 或整个恢复期限。端口恢复的后一次等待保留原 3 秒策略。 |
| 8 秒 | 后台进程归属**整轮**上限 | 从后台查询开始固定；netstat、候选 PID 和 WMIC 共用，不是每阶段 8 秒。父期限/Reload 单轮更短时进一步收紧。 |
| 最多 5 秒 | 后台归属查询中的 WMIC helper | 取整轮剩余预算；替代把后台 WMIC 误用为同步诊断 1 秒上限的行为。 |
| 最多 1 秒 | 直接同步命令行诊断中的 WMIC helper | 保留已有短上限，避免主线程同步诊断长期等待；不代表后台轮次只有 1 秒。 |
| 最多 100 ms | helper 已退出后的 stdout 完成观察 | 同时受原轮次截止约束；等待中持续检查取消。 |
| 最多 250 ms | helper 清理后的退出确认 | 同时受原父期限约束；清理失败保留原异常，不能吞为成功。 |
| 8 秒 | 已有服务复用验证总窗口 | discovery、Health、进程身份、握手及同步收据共用固定 `reuseDeadline`；短暂延后只在原窗口内继续，不能每次重新给 8 秒。 |
| 最多 8 秒 | 维护端点/停止候选身份检查 | 各自既有安全检查的上限，受原停止/维护期限约束；不开放普通状态观察。 |
| 8 秒 | 原有进程身份暂未就绪的宽限 | `IdentityGraceMs` 仅用于 pending 分类，不证明 Ready，也不重设父期限。 |
| 最多 7 秒 | bootstrap 挑战、主线程快照响应和 ACK | Server 在接到 `session.ready` 后建立 `min(Ready截止, 当前时间+7秒)`；不是普通 HTTP 的 2 秒上限，也不是额外启动期限。 |
| 3 秒 | 单次 WebSocket 连接尝试 | 与重连等待及整个 Ready 恢复预算分别计算。 |
| 1 秒，默认 | Bridge 重连等待 | 可配 1～60 秒；只影响后续调度，不重置 Ready 恢复期限。 |
| 最多 10 秒 | Server 候选 Unity 进程身份握手检查 | 独立候选身份上限；不能据此延长 Unity 已冻结的 Ready/父操作期限。 |
| 最多 300 ms | 单端口 TCP 可连接性观察 | 只是端口证据，不能代替进程归属、Bridge 同步或 Editor Ready。 |
| 最多 750 ms | 停止前可选 foreign-project Health 证据 | 使用独立有限 HTTP 安全路径，受原停止截止约束；无响应不构成普通 Ready 证据。 |
| 5 秒，默认 | Reload 单轮复核 | 可配 1～60 秒；HTTP、归属和当前同步收据共用一轮剩余预算，不因后台归属上限为 8 秒而扩大。 |
| 120 秒/30 次，默认 | Reload 总观察/最多轮数 | 总期限可配 5～600 秒，最多次数 1～100；任一到达即结束，再次 Reload 保留原记录。 |
| 30 秒 | 整次普通状态刷新的现有 watchdog | 区别于每次 HTTP 2 秒与后台归属整轮 8 秒，不能把迟到结果写回新生命周期。 |
| 300 秒，环境默认上限 | 普通工具等待连接 Ready 的 `UPILOT_CALL_WAIT_READY_S` | 实际取该值与原工具调用剩余期限的较小值；大多数普通命令默认总观察仅 30 秒，不会先等 300 秒再开始计时。 |
| 30 秒，默认 | 普通停止总观察 | 可配 1～600 秒；从准入起包含准备、停止和退出/端口确认，不因子步骤重置。 |
| 120 秒，默认；30～600 秒可配 | 服务启动/重启和没有更窄父操作时的 Ready 恢复总预算 | 在原操作/恢复 episode 开始固定；若已有 launch、maintenance、reuse 或 Reload 期限则取更早值。**600 秒是该父预算的配置上限，不是每次 HTTP、bootstrap、重连或进程查询都能等 600 秒。** |

Ready 恢复 episode 的预算只可缩短。成功完成后结束该 episode；后续独立恢复可创建新 episode。断线后同一恢复中的 socket 重试不能反复初始化期限。Unity 使用本地单调时钟检查本次 hello 剩余预算，Server 也检查连接与 bootstrap 的单调截止；时限到达不能被迟到 ACK 逆转。

hello 前预算已耗尽时抛出 `InitializationBudgetExpiredException`，连接循环以 `Logger.ReportFailure` 输出红色 Error，保留原异常及堆栈，并附 endpoint、sessionId、connectionEpoch、remainingBudgetMs、originalDeadlineTick 和 readyRecoveryDeadlineUtcMs；期限值 0 明确表示不可用，不补造身份。诊断按 endpoint 与原 Ready 恢复 UTC 截止时间识别 episode，在既有 10 秒窗口内跨重连 session 聚合；首次立即输出，保留重复次数和首末时间，不同原截止时间分别记录。查询原截止时间只读，不创建或延长预算。

预算失败不以每次重连作为 terminal 关闭聚合，否则会丢失重复计数。仅当前来源的精确 bootstrap ACK 成功后，在同步回调中刷新 `bridge-connection` 待输出计数，再结束 Ready 恢复 episode；仅 TCP/socket 连接成功不再关闭该诊断窗口。普通可恢复连接诊断仍遵循原 Warning 策略。本次日志修复不改变重试或期限行为；新增 `ExhaustedBridgeInitializationBudgetIsRedAndAggregatesAcrossReconnectSessions` 验证 Console 开关关闭仍输出 Error、同 episode 跨 session 聚合与不同 episode 分离，尚待关联编译及定向复验。

工具调用的 ready 等待、发送与响应观察还共用该工具自身原始期限。Reload 后通过 hello 的候选在 bootstrap ACK 成功前保留原 suspended 查询、原命令预算及原固定 grace；候选 bootstrap 失败或超时不能提前结束原恢复观察，候选 hello 也不能使查询脱离原 grace。ACK 成功后仅恢复既有允许重放的只读查询，副作用命令仍不重放。Ready 后心跳租约到期时撤销准入并关闭该 socket，要求新连接重新 bootstrap；普通 heartbeat 不能重新打开旧门禁。响应等待超时不代表 Unity 已物理停止执行；不自动重发业务命令。UPM 600 秒默认观察、编译/测试工具各自期限及用户业务操作预算仍按其原契约计算，不能混作服务 Ready 的 600 秒上限。其余可配周期和范围见[周期与超时](TimingAndTimeouts.md)。

### 其他相关边界：保留既有时长与设置

下表补齐与本次门禁相关的既有等待、租约、观察和界面周期。除 Ready 起算点、同一断线/Reload episode 不续期及来源门禁按本次契约收紧外，保留已有时长、配置和权限；列入清单不表示本次另行修改了这些功能，也不表示已在现场验证。

| 时间 | 用途 | 保留的含义与边界 |
| --- | --- | --- |
| 心跳 3 秒；超时 9 秒，默认 | Ready 连接的心跳租约 | 心跳间隔可配 1～600 秒，网络超时为间隔的 3 倍；**租约只在 Ready 成立后起算**，初始化/bootstrap 由 Ready 原期限约束，不能把未 Ready 判为健康租约。 |
| 10 秒，默认 | 周期 HTTP Health 调度 | 可配 1～600 秒；仅 Ready 且 Manager 普通观察门禁允许后推进，不改变单次 HTTP 2 秒上限。 |
| 3600 秒，环境默认 | 断线后原挂起工作的宽限 `UPILOT_DISCONNECT_GRACE_S` | 从本次首次断线固定截止；重复断线、候选 hello、重连尝试和轮询不续期。只保留原工作的观察，不重放执行，也不延长各原命令的父期限。 |
| 600 秒，环境默认 | Reload 宽限 `UPILOT_DOMAIN_RELOAD_BONUS_S` | 从同一 episode 首次 Reload 固定一次 bonus 边界，与原宽限取更晚截止；不是每条 Reload 通知累加 600 秒，再次 Reload 不续期。与 Ready 父预算最高 600 秒、WriteBatch 600 秒分别计算。 |
| 120 秒 | Unity `compile.request` 发起后的本地完成等待 | 本地等待与 Watchdog 的 `waiting` 状态共用同一个绝对 deadline；等待结束不替代编译终态、持久化错误复核及写入批次关联验证。 |
| 300 秒默认，限制为 1～600 秒 | Unity `compile.wait` 本地等待 | 采用请求的 `timeoutMs`，等待和 Watchdog 共用一次创建的 deadline，不因查询、状态切换或轮询重新给一份时间。Server 对此命令的响应观察默认 660 秒是另一层预算。 |
| queued 10 秒、executing 30 秒、waiting 120 秒、received/其他 300 秒 | 命令 Watchdog 各阶段固定警告阈值 | 普通阈值按原 `ReceivedAt` 累计，切阶段不清零；`waiting` 已设置显式 deadline 时使用该期限，编译等待不另外套一个新的 120 秒。每次检查的默认 2 秒只影响发现延迟；警告不会自动取消命令或证明执行失败。 |
| 500 ms | 自动 WriteBatch 首轮 settle 窗口 | 原轮次在首次 Refresh 前原子冻结成员；冻结之后的新登记进入下一轮，不向已冻结轮次追加，也不因重复登记重建或延长旧轮次。 |
| 600 秒 | 每个 WriteBatch 原始期限 | 原 batch ID、创建时间、关联 owner/成员及期限贯穿等待、编译、Reload 和恢复；单个成员取消/到期不取消其他成员或引擎，不借重建轮次续期。 |
| 10 秒，默认 | `WaitForServerVersionAsync` 版本响应观察 | 只请求 Health，250 ms 间隔继续原观察；版本一致仍不等于启动身份、Bridge Ready 或编译验收通过。 |
| 外层最多 7 秒、内层 5 秒 | Ready 后显式 `probe=bridge` 只读往返诊断 | 外层沿用有限 HTTP helper，Server 内层发送一次 `editor.state` 与等待回包共用固定 5 秒；返回前复核原 orchestrator、session、generation 和 bootstrap 收据，不发布旧 Ready。这是显式诊断入口，不能替代 bootstrap，Manager 完成 Ready 不再额外调用它。 |
| 最多 1500 ms | 原 PID 恢复核验的 Health 请求 | `GetHealthForRecovery` 收紧到原恢复剩余期限，保持精确旧 PID、项目及 UPilot 身份检查，不触发重启或开启普通状态观察。 |
| 最多 3 轮；两次等待分别 2 秒、3 秒 | 已有服务附着/端口恢复的有限重查 | 只有原父期限尚够才继续，最多两次等待；不是无限自动重试，也不重新派发已开始的启动/停止操作。 |
| 30 秒 | 相同重启诊断的既有节流窗口 | 影响重复诊断频率，不改变恢复期限或把失败变为成功；与下述错误聚合分别处理。 |
| 10 秒 | 相同根因错误的有界聚合窗口 | 首次立即输出红日志，保留次数、首末时间；独立 owner/新根因、升级和终态分别保留，完成/恢复刷新待输出计数。不是业务等待或重试期限。 |
| 主窗口 400 ms；高级设置/快速调试/更新中心 300 ms，默认 | 已打开窗口的界面刷新 | 均可配 100～5000 ms；关闭即解绑，界面刷新不是健康成功证据，也不改变操作期限。Step 管理窗口仍保留独立默认 1000 ms 周期。 |
| 连续 3 秒稳定 EditMode | 一次性启动配置入口 | 由既有 Bridge 主线程循环在连接/认证后、非编译/资源更新的稳定 EditMode 驱动 `CheckAndApplyStartupConfiguration()`；写设置前先持久化 SessionState attempted 标记。每个 Unity 进程只尝试一次，重连、Server/Bridge 重启、Domain Reload、成功或失败均不重放；配置成功与随后编译成功分别验收。 |

## 验证范围与待办

本次定向回归覆盖：初始化/生命周期普通观察门禁、Bridge 同步撤销后的迟到结果、验证提交前收据失效/owner 更替、Ready 预算只缩短、后台查询取消与 helper 清理、hello ID 严格匹配、bootstrap ACK 身份矩阵、重复挑战和旧来源队列/结果隔离。以下仅为阶段证据；尚未完成最新源码的统一验收。

- Python：原相关 suite 与新增生命周期回归共 **399 passed，2 条既有 websockets 弃用警告，29.10 秒**；`git diff --check` 通过。新增 suspended 固定 grace 和 lease 关闭 socket 回归取得实际 **4 failed → 4 passed** 证据，覆盖等待 bootstrap、bootstrap 被拒、成功 ACK 后恢复及 lease 过期后必须重新 bootstrap。完整结果保存在 `C:\Users\coding\AppData\Local\Temp\upilot-python-lifecycle-20261016\regression.log`；这不证明当前运行 Server 已加载新代码。
- Python 显式 Health 后续回归：`test_server_restart_diagnostics.py` 与 `test_bridge_readiness.py` 共 **64 passed、2 条既有 websockets 弃用警告、3.04 秒**，与前述 399 项分开记录，不相加。新增 8 种情景覆盖发送阻塞、停止、同 session 换代、缺失 generation、成功、取消，以及 orchestrator 替换/移除；初轮 5 failed、追加日志断言 3 failed、移除情景 1 failed 均在对应修复后转绿。最终日志：`C:\Users\coding\AppData\Local\Temp\upilot-python-lifecycle-20261016\health-probe-regression-final.log`。当前运行 Server 仍为旧加载来源，此结果不构成现场部署或端到端恢复验证。
- Unity 首轮：定向运行共 **441 项，415 passed、26 failed**，`resultAuthoritative=true`、`cleanupSucceeded=true`；首轮失败保留。26 项失败分别为序列化断言计数 1 项、已删除发送入口及缺失 bootstrap 的真实连接夹具 7 项、旧反射参数夹具 18 项，相关修复已合入源码，尚待规范项目最新关联编译与复验；不能把夹具已修复表述为 441 项已通过。
- 已取得的关联编译：原批次 `wb-74dc1768…` 已 `verified/passed`，`correlationVerified=true`、`errorsVerified=true`。该证据只覆盖该批次当时输入，不能替代之后夹具修复与后续代码的编译验收。
- 后续夹具批次：`wb-e9c2dc64-b54b-41a4-a94a-0892ac37ab35` 观察到 `compiler_finished`、0 个编译错误，随后进入 `domain_reload`，连接未恢复；原 600 秒期限耗尽后已成为 `timed_out/outcome=unknown`，`correlationVerified=false`，**未通过验收**。没有续期或重放，0 错误回调不能代替终态关联核验。
- 后续修复已实际保存并分别登记：`wb-d17a8030-b131-46db-bbe4-c0fec9d8551e` 修复受控 replacement 的 hello/Ready 循环等待，`wb-8b2ad980-9c47-4969-be04-c9d85b7f916e` 修复初始化期限耗尽的红色错误与聚合。两批登记时均为 `deferred/disconnected`，截至 **2026-10-16 14:59 UTC** 的观察仍断连。随后原批 `wb-d17a8030-b131-46db-bbe4-c0fec9d8551e` 已确认原 600 秒期限耗尽，终态为 `timed_out/outcome=unknown`、`correlationVerified=false`、`errorsVerified=false`；未续期或重放。`wb-8b2ad980-9c47-4969-be04-c9d85b7f916e` 随后的原批观察也确认原 600 秒期限耗尽，终态同为 `timed_out/outcome=unknown`、`correlationVerified=false`、`errorsVerified=false`；未续期或重放，两批均未通过验收。最新 C# 编译与定向复验仍未完成；本页更新没有新增规范项目运行时通过证据。
- 新增显式 Repair 批次：`wb-566b0904-47d1-4a1b-b055-c65d8de45782`，`writeBatchCreatedAt=1792163478926`，保存后立即登记 `UPilotQuickStart.cs`、`UPilotMcpServerManager.cs` 与 `UPilotStartupDiagnosticsTests.cs`。登记结果为 `deferred/disconnected`，保留原 600 秒期限；新增 11 个定向情景目前仅落源码、未执行；静态审查完成，Unity 关联编译与定向测试未完成。最新原批观察为 `deferred`、`terminal=false`、`outcome=unknown`、`correlationVerified=false`、`errorsVerified=false`，`ageMs=101734`（2026-10-16 15:13:00 UTC），未续期或重放。该批没有已验证编译或测试通过证据，不能改写此前失败批次。
- 中断历史：更早两批因 `service_restarted` 中断，结果仍为 `unknown`；不据后续重启或其他批次通过改写旧批成功。旧维护 `b9a2…` 为 `failed/authentication_failed`，原失败保留；实际发生进程重启不能代替维护成功。
- 当前部署：最终只读快照为 **2026-10-16 15:13:00 UTC**（`timestamp=1792163580638`）：规范 `D:/upilot/Tests~/UPilotTest` 仍 `connected=false`、`serverReady=false`，Editor 状态 stale/non-authoritative；Server PID 仍为 103936，加载来源仍为 different。Server PID **103936** 的已取得加载来源证据显示 `_handle_message` 与当前磁盘源码 **different**。当前源码已包含上述 Python 修复，但现有进程不能据健康响应或版本号报告已加载；须在后续已授权维护窗口刷新，再核验实际加载函数/源码、精确项目与当前 bootstrap 收据。维护 accepted、实际 restart 或出现新 PID 均不算最终验收通过。

`UPilotBridgeReadinessTests` 隔离用例不创建真实 socket；它们能验证内存门禁和身份判断，不能单独证明公开同步属性、有效 bootstrap 收据、真实 Open-socket 派发或发送锁后的网络检查已经现场通过。`UPilotBridgeSendTests` 的测试自有 loopback/真实 ClientWebSocket 用例覆盖实际 ACK 开启同步、等待发送锁时准入撤销后丢弃旧帧，以及连接发送取消对自有锁/socket 的影响；首轮总运行仍有 26 项失败，修复后的结果待复验，不能据文件落盘或部分通过宣称整体通过。

最终交付需分别记录规范 `Tests~/UPilotTest` 的原写入批次关联编译、窄范围 EditMode、Python 协议测试及 Server/Bridge 加载身份；测试自有 socket 不能替代规范项目当前 Server/Bridge 的实际加载来源或完整端到端恢复证据，未跑覆盖明确保留。

实现入口：`Editor/Core/UPilotBridge.Readiness.cs`、`UPilotBridge.Handshake.cs`、`UPilotBridge.cs`，`UPilotMcpServerManager.Lifecycle.cs`、`.ProcessDiagnostics.cs`、`.Launch.cs`、`.Recovery.cs`、`.StopProtection.cs` 及 Manager 主文件；Server 对应 `upilotserver~/src/upilot_mcp/server.py`、`session_manager.py`、`state_store.py`、`dispatcher.py` 与 `mcp_stdio_server.py`；显式 Repair 入口另见 `Editor/Core/UPilotQuickStart.cs`。
