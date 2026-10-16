# 遗留 Server 的关闭与崩溃恢复方案

创建：2026-10-15；更新：2026-10-16。状态：**代码及定向验收完成，现场覆盖边界见第5节**。

本文件保存用户确认的行为、实施范围和交付证据。本轮已完成关联编译、原 510 项及补充 8 项定向 EditMode、健康遗留服务只读复用、受控异常遗留服务清理后单次替换与失败收口，以及真实维护的旧服务退出、双端口释放和新服务握手/只读验证。补充 8 项包含原 3 项复验，不相加为 518 个不同用例；真实崩溃等现场边界见第 5 节。此前 229 项验收不计入本轮结果。历史记录见 `LogInventoryAndRetention-20261015.md` 第 7.7 节；对应待办为仓库根 `TODO_UPilot.mcd` 的 `UPilot-20261015-ResidualServerPortStop`。

## 1. 已确定的行为

- 停止及遗留服务恢复不核对创建时间和父子关系，也不要求原 PID、旧 Unity 会话或历史启动记录存在。兼容 Unity 崩溃后重启、SessionState 丢失但旧 Server 仍存活的情况。
- 用户已选择：**精确 Server 入口和目标端口匹配即可关闭**。health 不响应、项目信息缺失、日志目录变化均不阻挡；明确属于其他项目的进程除外。
- Unity 崩溃后重新打开项目，用户已选择：**可用则复用，异常则清理后重新启动一次**。继续遵守现有自动启动开关。
- 本次处理当前配置的 Server 入口和 HTTP/WS 端口对，不全局清理其他版本、入口或端口的服务。
- 以上关闭规则替代此前建议中的创建时间、父子关系及强制取得项目归属证据等前置条件；已有实现的历史记录保留，不改写为本方案已交付。

## 2. 发现与关闭

### 统一入口与候选发现

- 在现有 Manager 内统一停止逻辑，供普通停止、重启、修复和启动恢复调用。原 UI 的 `PrepareObservedStop` 与重启的 `PrepareCurrentProjectStop` / `PrepareTrackedServiceStop` 必须同时收口到相同发现、终止和退出观察规则，不能只覆盖一个入口后宣称全部生效。
- 候选来源包括已知 PID、HTTP/WS 端口占用者，以及使用当前精确入口和端口参数的进程。PID 仅作为查找线索，缺失或过期不阻止端口发现。
- Python 按实际脚本参数的绝对路径匹配；EXE 按实际可执行文件路径匹配。禁止仅凭进程名称、目录名或命令行子串匹配。共享解释器路径本身不是 Server 入口证据。
- 沿用受管 Server 已有启动格式：`--http-port`、`--port` 必须唯一且匹配当前配置，支持空格和等号写法，拒绝重复、歧义或错配参数。该条件针对进程启动参数，不要求进程自己同时监听两个端口。
- HTTP、WS 分别由不同匹配进程占用时，逐个关闭，不能仅因端口拥有者超过一个就整体拒绝。
- 日志路径不再作为关闭的必要条件，旧 `log/mcp-server.log` 与当前 `Logs/UPilot/mcp-server.log` 的差异不能单独阻止关闭。health 可用且与实际监听进程对应时，用于识别明确的其他项目；health 缺失不影响匹配进程的关闭。日志路径不同本身不等于明确属于其他项目。

### 终止与残留清理

- 对每个匹配进程持有 OS 句柄，使用该句柄完成终止和退出观察，不在最终终止时重新按 PID 选取进程，不读取创建时间或父子关系作为关闭门禁。
- 启动器与服务进程分别依据相同入口、端口规则识别和关闭，不递归结束整个进程树。
- 首次关闭后继续查询残留端口，重新核验并关闭新发现的匹配进程；原启动器不退出时，也不能挡住残留端口检查。
- 沿用一次固定停止期限：默认 30 秒，使用现有配置并受父操作剩余期限约束。OS 查询、终止及残留检查均计入原期限，轮询不续期。
- 目标进程退出且 HTTP/WS 两个端口均可绑定后才成功。其他程序占用、查询失败、权限不足、仍有残留进程及无监听但端口不可绑定，分别报告具体原因；不能假报成功或无限等待。

## 3. 崩溃恢复、接口与诊断

- 新 Unity 会话独立发现存活服务，不恢复或重放上一个 Unity 进程的停止、重启及业务任务。旧会话失败或终态记录不能挡住新会话按本规则发现和处理遗留服务。
- 复用验证最多 8 秒，包含服务响应、当前项目 Bridge 连接和实际只读调用，不依赖旧启动器仍存在。验证失败后，在现有重启总期限内执行清理，再启动一次；失败不循环重启。
- 正常新服务的启动验收保留现有规则；本次调整关闭和遗留服务附着路径，不将旧启动身份核验作为这两条路径的必经前置步骤。
- 不新增 MCP 工具、配置项或独立 PID 文件。人工本地停止不要求 Bridge 在线；AI 维护保留独立授权及请求身份校验，但不再让缓存归属、原 PID 或原启动关系挡住残留清理。
- 当前 RPC 请求身份校验与实际关闭目标分开：保留维护请求针对当前端点的身份和重复请求约束，不能据此要求每个残留目标都等于原 Server PID。
- 停止记录保存本次入口、端口、目标及派发结果，供同一 Unity 会话的 Domain Reload 继续观察原操作，避免重复原终止派发。操作结束后旧回调不得继续关闭进程，新操作使用新的身份与期限。
- 诊断保留目标 PID、入口、端口、发现依据和关闭结果，不输出完整敏感命令行或凭据。
- 真实失败输出可见红色 Error/Exception 日志并落盘，保留原异常、堆栈及操作身份；可恢复降级使用 Warning。Error/Exception 的 Console 输出不依赖详细日志或文件日志开关；Error/Exception 与 Warning 均不受 `LogToUnityConsole` 关闭影响，保留既有最低级别策略。
- 频繁重复根因首条立即输出，之后按既有 10 秒窗口聚合，保留重复次数及首次、最近发生时间。不同操作、新根因、严重性升级和终态失败独立输出；完成或恢复时结清待输出的重复统计。正常轮询、健康检查和心跳不刷屏、不逐次落盘；文件写入失败不能再次写入同一失败的日志写入器。
- 不可恢复的权限不足等 OS 拒绝必须明确结束并给出原因；已持有句柄的 Win32=5 pending 竞态按第 5 节在原期限内观察，不延长期限。“最终可控”的验收要求是有明确发现、关闭入口及可核实结果，不能以无限重试或隐藏失败代替控制能力。

## 4. 验证与后续交付

- 定向覆盖 PID 丢失或失效、创建时间不同、父进程消失、旧 Unity 会话变化、SessionState 丢失和历史身份记录缺失，证明这些条件不再阻挡关闭及遗留恢复。
- 覆盖旧日志路径、无 health、项目证据缺失但入口和端口匹配、明确属于其他项目、不同入口、端口参数错配/重复/歧义等匹配边界。
- 覆盖 HTTP/WS 分属多个残留 PID、EXE 启动器与服务分离、首次关闭后出现新残留、启动器不退出，以及 OS 权限或查询失败。
- 覆盖固定期限不延长、同一操作 Domain Reload 不重放原派发、终态回调失效、新 Unity 会话可重新清理。
- 覆盖自动启动关闭、健康服务复用、异常服务清理后只启动一次，以及恢复失败明确终结。
- 使用受控子进程和独立测试端口模拟崩溃遗留，不通过实际终止 Unity 制造故障。
- 修改后在规范项目 `Tests~/UPilotTest` 完成关联 Unity 编译及最窄定向测试，不默认运行全量 EditMode，不使用外部 C# 编译器替代 Unity 编译证据。
- 运行时停止/重启验证观察原操作身份；响应中断不重发请求，记录真实进程退出、双端口释放及恢复结果。
- 实施时同步更新对应待办和相关接口说明，保留历史失败与验收边界；当前开发和证据范围见下一节，编译通过不代表已完成上述验收。

## 5. 交付证据与现场覆盖边界

### 已保存的实现范围

- 新增 `UPilotMcpServerManager.StopTargets.cs`，按当前精确 Python 脚本/EXE 镜像和唯一 HTTP/WS 参数发现候选，保留 OS 句柄用于终止及退出观察；已知 PID 仅作线索。health 缺失和日志目录变化不挡住停止，明确其他项目且与实际 HTTP listener 对应时排除。
- `Stop.cs` / Manager / ServiceMaintenance 收口普通停止、重启与维护的发现和残留扫描，保留原固定 deadline、操作身份和维护授权；HTTP/WS 分属不同匹配 PID 时逐个处理。停止后重新发现匹配残留；新监听者在最终端口快照阶段出现时继续核验，避免直接误判为外来占用。
- 新增 `UPilotMcpServerManager.Recovery.cs`，新 Unity 独立发现遗留服务，健康服务最多 8 秒验证当前 Bridge 握手与实际只读往返后复用；异常服务在原总期限内清理后只启动一次，遵守 AutoStart。正常新服务仍保留严格启动身份验收。
- 同 Unity Domain Reload 转移并观察原 OS 句柄，不重复原终止派发或启动。原重启的域内 stop→launch waiter 丢失时，观察原停止终态后明确结束为 `restart_recovery_required`，避免重启永久 running；旧操作回调不能改变新操作。
- 对持有句柄的 `TerminateProcess` 返回 Win32 `5`，保存原异常、原生码、PID、入口和 stopId，首条 Warning 后转为原 deadline 内仅观察，不重复终止、不直接记成功。只有原句柄确认退出且双端口可绑定才成功；到期仍存活则红色失败并保留原异常，后续查询异常也不能覆盖该根因。其他原生错误仍即时失败；该处理不改变打开进程句柄时的权限拒绝规则。
- 已新增或调整并执行定向测试，涉及残留发现、精确入口/参数边界、最终端口快照竞态、停止 pending 及固定期限、初始恢复和 Reload 后原重启收口；健康服务异步恢复实际经过 Bridge 与只读往返。
- 最后补齐非 Win32=5 的终止拒绝即时失败，以及非 `revalidateOnly` 的受控异步恢复链、停止后 AutoStart 撤销和替换阶段缺少 launch 记录的 Reload 失败收口。两个内部隔离入口供自有进程/端口测试使用，不改变公共工具、配置或协议 schema。

### 规范项目关联编译证据

项目均为 `D:/upilot/Tests~/UPilotTest`，Unity PID `27612`、Unity `6000.6.0a2`；保存的是原 `unity_write_batch_status` 结果，不以其他批次冒充登记批次。原 JSON 位于 `Tests~/UPilotTest/Log/P0P1/ResidualServerDevelopment/`。

| 批次 | 原编译身份与时间 | 已观察结果及边界 |
| --- | --- | --- |
| 首批 `wb-981d0972-1706-4045-a620-800e9e94bbd0` | request `req-202cef13-ebc7-4f3f-b5ca-66255c75fd15`；compile `8743390ca24a4a8da1a9a92d71e6f772`；created `1792070827094`，verified `1792070837548` | `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 0 Warning。证据 `batch-status-first.json`。仅证明该批次关联编译。 |
| 审查补充批 `wb-ab973da5-2ab5-49c0-a751-25b5be544595` | request `req-a1c42e56-6cf1-4b93-af8d-54afb7b34af9`；compile `f8dfc40d98384aafa51a35f4c480c5a9`；created `1792071565080`，verified `1792071584601` | 原结果 `failed`，2 Error / 3 Warning；当前项目 NUnit 不支持新 fixture 的 `NonParallelizable` attribute，已移除该夹具 attribute 后进入下一批，不放宽业务断言。原失败保留于 `batch-review-status.json` / `review-compile-errors.json`。 |
| port-race 批 `wb-448dc655-f72d-4a24-91bb-096d7a345ca7` | request `req-2ce2d903-54a4-4d9a-9e43-cc5aa827c358`；compile `5ce02ddd268746979851b8cce65af6c4`；created `1792071643597`，verified `1792071662885` | `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 1 Warning。证据 `port-race-status-ready.json`。登记文件为 StopTargets、RestartStopReloadTests、StopPortSnapshotTests；不据此宣称其他历史批次成功或当前所有后续改动已验收。 |
| pending-exit 批 `wb-6947203a-c84c-4a78-9828-038da707b420` | request `req-3fc0555c-4b69-4a91-8395-ca15153db1ab`；compile `4f59e46d2eb64d0094751930126e0a91`；created `1792072285520`，verified `1792072306725` | 原 `unity_write_batch_status` 为 `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 4 Warning。证据 `pending-exit-batch-verified.json`。 |
| 保留停止根因批 `wb-d780df0a-bfcc-4a08-8c2d-a3528cc660aa` | request `req-874bb821-74fd-4b8d-a148-a775068ecc75`；compile `c3ff23a15fef4c0ea2e97a8284ba2c2e`；created `1792072531144`，verified `1792072568943` | 原批次 `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 4 Warning。证据 `preserve-stop-errors-status.json`。 |
| 正常取消批 `wb-c97c9792-9d93-429a-b16b-7e1818296103` | request `req-88538e53-abf0-4ed1-9f5c-e00aa95d7758`；compile `06085c407ad147379c4a68509e41fb94`；created `1792072637976`，verified `1792072650866` | 原批次 `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 0 Warning。证据 `normal-cancel-status.json`。 |
| Python 镜像匹配批 `wb-c9163985-660c-4de2-b67f-a7afe3cc28e8` | request `req-17cd76ce-5055-425e-bce8-46cf36a52a5f`；compile `9cad310e906443608c98249ef8b67870`；created `1792073001315`，verified `1792073015495` | 原批次 `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 0 Warning。证据 `batch-python-image-status.json`。 |
| pending 定向测试批 `wb-74991fb3-32f8-4ce5-b990-72405ce5e3fc` | request `req-c99b0de1-381b-490c-95da-700fbb6c0a07`；compile `6e98fdb176b645a7840eecf47689bbfc`；created `1792073248530`，verified `1792073252362` | 原结果 `failed`，1 Error / 1 Warning，`CS1503`：当前 NUnit 不支持链式 `Contain` 断言。证据 `batch-pending-direct-tests-status.json`。 |
| NUnit 兼容修正批 `wb-aedcdcc7-a2af-4076-a225-1e1d444fcb08` | request `req-bd984aa0-d917-4ae9-89b3-90994eabd31b`；compile `9767b05e75e64398994220d8eeff0272`；created `1792073321786`，verified `1792073336285` | 两条 `CollectionAssert` 保持原断言含义；`verified/passed`，0 Error / 1 Warning。证据 `batch-pending-nunit-fix-status.json`。 |
| 只读恢复测试批 `wb-184409b6-787b-47a6-9a55-a1ba14bee0e8` | request `req-0f765e51-22f6-42e4-a191-6859eae432d8`；compile `35a410da7e014409adc9ec34f41f6267`；created `1792073600241`，verified `1792073616103` | `verified/passed`，0 Error / 1 Warning。新增健康复用、复用期限已过和 AutoStart 关闭 3 例；证据 `batch-readonly-recovery-tests-status.json`。 |
| 探测摘要修复批 `wb-55a458cd-e9ce-49c1-944b-88f199449f1e` | request `req-2ac1c896-0432-4d7f-832e-e8214f62eb89`；compile `89f844851250424696a26b3747ffc819`；created `1792074054883`，verified `1792074070390` | 恢复 `DescribeProbeException` 的既有 1024 字符 `TruncateDiagnostic`，未修改测试断言；`verified/passed`，0 Error / 0 Warning。证据 `batch-bounded-probe-fix-status.json`。 |
| 异步链及非5拒绝补充批 `wb-ea6350e2-7e39-415f-bfa3-049b155993b9` | request `req-27ecb17c-5f97-44dd-a96a-d15962a5ba1d`；compile `9deb26febc664bc087db471b848f2343`；created `1792079657996`，verified `1792079694905` | `verified/passed`，`terminal/correlationVerified/errorsVerified=true`，0 Error / 4 Warning；官方 `unity_compile_errors` 为 0。证据 `chain-test-batch-status-2.json` / `chain-test-compile-errors.json`。 |

首批与 port-race 结果文件 SHA256 分别为 `bc3772c5d4d254783065af20b9c08dac4eb8968eb0a3db94db81de6aa0be9063`、`293cd9ac3c05186f3b0c0b945930bd7cc006847550fe13f9964afd985a85065a`。后续通过批同样具备 `terminal/correlationVerified/errorsVerified=true`；原失败批保留，不以修正后的通过结果回写。补充批首登记误含 `.cs.meta`，工具以 `UNSUPPORTED_WRITE_BATCH_FILE` 拒绝且没有 `data` 或批次；改为仅登记 4 个 C# 后取得上述新批次。拒绝原记录 `chain-test-batch-register.json` 保留，不记为编译失败或已登记批次。

pending-exit、保留停止根因、正常取消及 Python 镜像匹配四批逐项核对的是请求中原 `writeBatchId` 对应的 `data` / `terminalSnapshot`，均为 `compilePhase=completed` 且 verified 时间晚于本批 created；没有把响应中可能指向其他新批次的当前 `context` 当作原批次通过证据。上述编译结果不等于 Unity 定向测试或完整停止/恢复验收。

### 定向 Unity 验收

- 原覆盖报告 Task `task-645a1750-b5d4-4927-a624-8895035fca5d`、run `22f5009d-72e2-499f-bb0a-9c03a6113826`：510/510，通过且无失败/跳过，`resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged/importInputsUnchanged=true`。覆盖 9 个关联 fixture 和 3 个明确选定的异步只读恢复用例，未运行全量 EditMode。完整官方结果为 `acceptance-probe-fixed-terminal-full.json`；报告为项目内 `Logs/UPilot/Reports/Acceptance/1792074133891_req-9e5d4afb-e980-4650-bc87-d5a89c3978bc/summary.json`，本地核对 2529683 bytes、SHA256 `24c922049ffe891c5b40c0051407f40711dd1296024484d6ce24165ed23c037a`。
- 首轮 Task `task-3a0a3ce7-2fe5-4cbd-b08e-fb0e69f5691a`、run `883d155d-bdbe-4db7-a087-36731753a2c7` 为 509/510、1 失败、0 跳过，失败为 `UPilotStartupDiagnosticsTests.ProbeErrorsKeepBoundedRedactedInnerExceptionEvidence`。原因是误移除既有摘要截断；恢复上述一行 `TruncateDiagnostic` 后重新验收，没有删除或放宽断言。原失败保存在 `acceptance-server-recovery-terminal-summary.json` 及项目报告 `Logs/UPilot/Reports/Acceptance/1792073760733_req-e5d9f99f-3983-4ccd-b22f-770aa4799411/summary.json`。
- 最新补充 Task `task-3f3ecae1-a07c-42ea-b409-8e3165524b10`、run `966b070a-2fa7-4115-8d6a-88f686791a35`：8/8、无失败/跳过，上述 6 个门槛均为 true，runner inactive、cleanup completed。包括新链路 4 叶、非5拒绝 1 叶及原只读恢复 3 叶复验；没有重跑 510 项或 72 项 Python。官方结果 `chain-test-acceptance-terminal-full.json` / `chain-test-results-summary.json`；报告 `Logs/UPilot/Reports/Acceptance/1792079763183_req-33967084-ad53-4c4b-b689-5e8d52aa230e/summary.json` 已本地核对 973693 bytes、SHA256 `e17e2077c3de2bb3e6a9cd4dd6116106a3579a84d2bf687bcf8a7f43418687ba`。
- `NormalFallbackReclaimsOwnedResidualAndStartsAtMostOnce(false)` 实际运行非 `revalidateOnly` 链：固定 8 秒复用验证耗尽，真实自有 launcher/worker 退出、独立双端口释放，替换意图先持久化、实际 `Process.Start` 仅一次，替换服务未通过身份验证后在原总期限内以 `launch_identity_timeout` 明确失败；不伪造 Ready/终态。true 变体验证停止确认后关闭 AutoStart，零替换。`ReloadWithMissingLaunchRecordFailsWithoutReplayingReplacement` 两叶验证替换两个阶段缺失 launch 记录时明确失败且不重放。`NonPendingWin32RejectionEndsStopOnceWithoutTerminatingOrRetryingTheOwnedTarget` 验证 native1314 立即失败、原异常保留、一次调用且目标仍活。

### 旧 compile lifecycle 根因与已观察的界面恢复

- 根因位于 Python `server.py`：Server 的 `_editor_lifecycle` 仍等待旧 compile identity，原收口只接受相同 ID 的 verified terminal；后续编译产生更晚的权威已验证终态时，旧等待未被收口，`editor_observation()` 继续返回 `waiting_lifecycle`，使 UI 保持编译/获取状态中。修复按有序、fresh、authoritative 快照和真实开始/验证时间处理被替代的等待，保留旧编译身份与结果；后续真实编译仍等待其自身 verified terminal，不能因原始 `isCompiling=false` 就变为 Ready。
- `tests/test_editor_lifecycle_health.py` 的 Python 定向结果为 72/72，0 failure/error/skipped，JUnit 为同证据目录 `lifecycle-pytest.xml`；其离线覆盖包括新 compile 替代旧 ID、开始事件丢失后的更晚 verified terminal、过期/非权威/非终态拒绝与会话隔离，不代表 Unity 定向验收。
- 后续新编译 `wb-9866848e-72c9-49fd-b5e3-fbc88d73184a` 已在 `window-compile-completed.json` 的权威 context 中完成：compile `18d27246e83d4e699b42e6fb23f0575d`，created `1792072111912`、verified `1792072131734`，`ready=true/authoritative=true/isStale=false/terminal=true/errorsVerified=true/correlationVerified=true`，0 Error / 0 Warning。此处用于关联截图时的实际状态，不替代该批原 `unity_write_batch_status`。
- 同一官方 `unity_screenshot_editor_window` 结果于 `1792072193175` 捕获规范工程 Unity PID `27612` 的 UPilot 主窗口，截图实际显示绿色“已就绪”。snapshot `snapshot-9e602745910749a1bc8e24e6ecf69a26`，442×463，`pixelSourceVerified/contentRectVerified/cropComplete/acceptedAsEvidence=true`；图片为项目内 `Logs/UPilot/Diagnostics/Snapshots/20261015/snapshot-9e602745910749a1bc8e24e6ecf69a26/editor-window.ee018501.color.png`，36244 bytes，本地 SHA256 核对为 `3e994a1348ec764a2329cdd6b8cdfaaedf2ad4b8bb2dcebc89416638eee9250a`。这证明该时点界面已自然恢复，不扩大为全部模块加载或后续全部场景通过。

### 终止 API 竞态的受控证据

自有 launcher 的最后一个 `KILL_ON_JOB_CLOSE` 句柄关闭后，worker 进入异步终止。受控 Windows 原生 API 实验 48 轮中，31 轮真实出现 `TerminateProcess=false/Win32=5` 且调用前后对原 held handle 的零时等待均为 `WAIT_TIMEOUT(258)`；最终 48/48 的 launcher/worker 都退出且两个测试端口可绑定，另 17 轮 native 调用成功。脚本及结构化结果为同证据目录 `terminate_process_job_race.py` / `terminate_process_job_race.result.json`，仅操作测试自有进程，保存证据时未重跑。该结果确认“拒绝调用但进程尚未 signaled”的窗口存在；原维护现场未记录 native code，仍不能断言原现场返回了 5。该实验不是 Unity 编译、EditMode 或修复后真实维护验收。

### 历史维护失败与后续实际恢复

- 为部署另一个旧 compile lifecycle 收口修复，发起一次维护 `1e935d40-6e03-4cdb-a7e7-1bc64ba5f635`，关联 restart `restart-c6247316bf4c4e1597b8d85dce38f464`、stop `e8c9c9921cd4436997d0c1a75b41c93a`，目标 Server PID `28896`。原请求保存在 `lifecycle-maintenance-request.json`；accepted 不代表完成。
- 原停止在持有句柄的终止 API 返回失败后结束为 `failed`，报错“通过持有的 OS 句柄终止 Server 失败；PID=28896”，耗时约 0.04 秒。随后观察 Server `28896` 实际已退出，但该事实不能改写原停止/维护失败。维护持久记录为 `failed/port_release_timeout`、`startAttempted=false`、`newServerProcessId=0`；此 `port_release_timeout` 记录也不能解释为已等待满 30 秒。
- 该维护没有启动替换服务，随后 MCP 离线；`status-after-lifecycle-maintenance.json` 记录 `sendState=not_sent/stage=connection_preflight`。已请用户只点击本地 Start 恢复连接，未重发原维护。后续独立启动结果必须保留新操作身份，不能回写本次失败为成功。
- 后续用户本地 Start 产生独立 restart `restart-c03b905f7b8247eface2ae9d12079d79`，记录为 `succeeded/verifiedSucceeded=true`，新 Server PID `35804`、launcher `32276`；health/项目身份/deployment 于 `1792072040091` 至 `1792072040096` 验证，Bridge 于 `1792072040155`、实际只读往返于 `1792072040168` 验证。`user-start-status.json` 已观察精确规范项目连接、权威 Ready，以及选定入口 loadedSource=equivalent；该范围不能外推至全部模块。后续编译完成与绿色“已就绪”已有上述官方截图证据；此独立恢复不改变原维护失败。
- 修复后新的维护 `3930ad81-5b54-4207-ad75-c899c3758952`、restart `restart-cd53f59d24704c178da12b4d01174106` 实际 `succeeded/verifiedSucceeded=true`；旧 Server `35804` 和 launcher `32276` 退出，`old_exit` 与 `ports` 均通过（双端口释放于 `1792074405862`），新 Server `36668`、launcher `21220`。health/项目身份/deployment/Bridge/实际只读往返全部验证，Unity PID `27612` 保持运行。证据为 `residual-maintenance-observation-1.json`、`final-server-restart-record.json` 与 `final-service-maintenance-record.json`，没有重放旧失败请求。
- 本次维护真实命中 Win32=5 pending 路径：官方 Console `final-maintenance-target-warnings.json` 保留 Server `35804`、stop `e6bd32d268be474ab74aa9287e00f92e` 的 Warning 和原 `Win32Exception` 堆栈；随后同次维护通过实际退出与端口门槛。这证明修复后该竞态已在真实维护中恢复；旧维护 `1e935…` 的 native code 仍未知，不能混同。
- 维护后的 `window-after-final-maintenance.json` 捕获 snapshot `snapshot-5a07e221d73c44eb975565d5d03086b7`（`1792074493427`），主窗口实际显示绿色“已就绪”；同次 context 为 fresh/authoritative EditMode/Ready。PNG 为项目内 `Logs/UPilot/Diagnostics/Snapshots/20261015/snapshot-5a07e221d73c44eb975565d5d03086b7/editor-window.ee018501.color.png`，442×463、36244 bytes，本地 SHA256 `3e994a1348ec764a2329cdd6b8cdfaaedf2ad4b8bb2dcebc89416638eee9250a` 与官方元数据一致，`pixelSourceVerified/contentRectVerified/cropComplete/acceptedAsEvidence=true`。`final-queue-observation.json` 为 `complete=true/isStale=false/items=[]`，二者均为该维护后的时点证据。
- 补充 8 项后仍为 Server `36668` / Unity `27612`，未再次维护实际服务。先前并行 `chain-test-final-status.json` 的 `EditorContextStale` 保留为当时快照；随后一次只读 `unity_ensure_ready` 的 `chain-test-postrun-ensure-ready.json` 明确 `ready=true/blocked=false/authoritative=true/isStale=false`，compile `9deb26febc664bc087db471b848f2343`、verified `1792079694905`、session `f18e72b28e0a404e88c27090445608bc`。`chain-test-final-queue.json` 为 `complete=true/isStale=false/items=[]`。

### 交付与未覆盖边界

- **代码及定向验收完成，现场覆盖边界见第5节**。实现及原生进程验证范围为 Windows；本轮未实际结束 Unity 来制造崩溃。精确匹配/权限/期限/Reload guards、健康遗留服务只读异步复用、受控异常遗留服务非 revalidate 清理→单次替换→有界失败，以及普通维护真实退出→释放端口→健康新服务只读验证分别有证据。
- 新受控链证明异常清理和替换失败终结，不等于该恢复入口替换为健康服务后成功的现场验收；真实 Unity 崩溃重开和 Recovery `cleaning` 阶段跨真实 Domain Reload 未现场验收，不能宣称所有崩溃和异常恢复场景均通过。模块加载等价性仅为 `selected-entry-code-only`，不代表全部模块、依赖、全局或闭包状态相同；没有把 72 项 Python、截图或普通维护外推为这些场景通过。
- 另见根待办 `UPilot-20261015-StaleCompileLifecycleStatus`：旧 compile lifecycle 修复的 72/72 Python、原 510/510 关联 Unity 定向、最新补充 8/8 及 Ready 观察各有证据；历史维护失败保持原结果。
