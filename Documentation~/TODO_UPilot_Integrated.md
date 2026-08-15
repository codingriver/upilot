# UPilot 统一改进计划

本文件整合以下来源，并作为 UPilot 后续开发与验收的唯一主清单：

- `F:\xclient2\TODO_UPilot.mcd`
- `D:\MA\xclient\TODO_UPilot.mcd`

整合日期：2026-08-12。实施范围以 `D:\upilot` 为主；通用能力留在 UPilot，用户原始清单中明确属于项目桥接的事项在 `F:\xclient2` 精确落地。

## 优先级

| 优先级 | 工作项 | 必要性 | 实施状态 |
|---|---|---|---|
| P0 | Editor 状态、响应上下文和 readiness 契约 | 所有自动化写操作的安全前提；解决 PlayMode、同步结果和陈旧 context 的矛盾 | 已实现；PlayMode start/stop 均等待权威目标状态，两个 Unity 2022 项目联机通过；Unity 6 保留此前证据 |
| P0 | 编译生命周期、附着现有编译和主线程 pump 诊断 | 解决失焦不编译、`EDITOR_BUSY` 和无信息长等待 | 已实现；现有编译附着、Domain Reload、终态时间戳和零错误验收通过 |
| P0 | Operation 等待窗口与 Console 会话闭环 | 防止单次等待预算把仍运行的作业永久置为 Timeout | 已实现；90 秒真实长作业和 Console 自动闭环通过 |
| P1 | Animator、AvatarMask、FBX 和 ModelImporter 审计 | 减少手工 Inspector 对账，覆盖两个项目反复出现的动画资源问题 | 已实现；`Hero_10003` 联机通过，`hero1010` 当前项目资源缺失 |
| P1 | 编码保持的 CSV 查询与字段补丁 | 避免 GBK/CRLF 配置表转码和整行误改 | 已实现；GB18030、CRLF、31 列和目标外字节不变联机通过 |
| P1 | Unity 卡死看门狗和非终止式 Dump | Unity 主线程卡死时仍能保留 CPU、心跳、队列和 dump 证据 | 已实现；15 秒主线程忙循环和非终止式 Dump 通过 |
| P1 | 窗口 truth 和截图降级 | 确保截图来自 Unity 窗口，并支持退出 PlayMode 后取证 | 已实现；SceneView 降级和 EditorWindow 实例截图联机通过 |
| P1 | 工具精确搜索和 `callableNow` 过滤 | 防止近似 CRUD 工具被误认为目标能力 | 已实现；精确、可调用过滤和近似结果分离联机通过 |
| P1 | 客户端未注入工具的安全代理调用 | 能力发现后无需手写 HTTP/SSE；仍遵守工具注册表、写权限和 Flow 开关 | 已实现 `unity_tool_call`；Python 路由/递归/安全契约通过，客户端刷新后可用 |
| P1 | Console 严格关键词搜索契约 | 修复 `query/maxCount` 被静默忽略而返回无关日志 | 已实现兼容参数、`effectiveQuery/effectiveContains/matchedFields/scannedCount`；Python 与 Unity 2022 编译通过，项目 Console 联机待补 |
| P1 | 大体量 Console 索引、范围与稳定分页 | 68 MB 以上战斗日志必须在 MCP 外层超时前定位事务链 | 已实现 sequence 稀疏索引、采集时增量维护、范围/正则/continuation token、扫描元数据；68,052,043 字节最终冷启动首读 1.073 秒通过 |
| P1 | 运行时 NavMesh 诊断 | 定位异步注册、根节点平移和 `SamplePosition` 一帧竞态 | 已实现 status/sample/triangulation；明确区分公共 API 可观测值与推导 Bounds，Unity 2022 编译通过，关卡 10007 联机待补 |
| P1 | 结构化长时段 Profiler | 自动记录帧耗时、GC、渲染计数和运行时组件规模并生成 JSON/CSV | 已实现 start/status/stop、P50/P95/P99 和产物；Unity 2022 编译通过，120 秒战场联机待补 |
| P2 | 纹理导入与截图像素验收 | 支持透明黑底问题的参数修复和结构化视觉断言 | 已实现 TextureImporter 读/两阶段改/重导入与 PNG stats/compare；Python/Unity 2022 编译通过，AttackLine 资产联机待补 |
| P2 | 脚本分析、依赖图和项目栈检测 | 减少跨模块调用链人工搜索 | 已实现并联机通过；无第三方解析依赖，结果明确标记置信度 |

## 2026-08-12 增量实施结果

- Python：`65 passed`。新增通用代理、Console/PlayMode 阻塞兼容契约、NavMesh/Profiler 路由、资源依赖审计、纹理补丁确认令牌和 PNG 像素/差异分析测试。
- Unity 2022.3.62f2：独立验证工程以 `file:D:/upilot` 引用本地候选，最终 BatchMode 编译 `error CS` 0 并成功退出。
- Console 规模验收：历史会话 68,052,043 字节、23,648 条，过滤 `RewardPending|ExistingTrapSkillReward|SkillTrapManualRequest|SkillTrapCast|FixedSkillCast` 实际命中 33 条；最终删除索引后的冷启动首读 1.073 秒，返回全部 33 条，sequence 33/33 唯一且严格递增，扫描范围/总匹配数/耗时/索引元数据齐全。样本总匹配不足 150，因此正确首批为全部 33 条；另以 210 条匹配合成数据验证 150 条上限的多页 continuation 路径无重复、无丢失。
- 当前客户端没有注入 `unity_*` 类型化工具；联机状态检查按技能降级为本机 Streamable HTTP。`D:\MA\xclient` 在线且 EditMode，但依赖仍是 Git tag `v0.3.24`，未将本地候选临时注入业务工程；`F:\xclient2` 当前断连。因此新增运行时能力的项目场景验收保留为待补，不把编译通过表述为战场验收通过。
- `unity_navmesh_status` 返回 Surface 当前 Transform、NavMeshData、内部 DataInstance 可观测标识/valid、Agent 统计、preUpdate/状态版本和全局 triangulation。Unity 公共 API 不暴露已注册 DataInstance 矩阵，工具将世界 Bounds 明确标记为 `surfaceTransform-inferred`，不伪造权威注册矩阵。
- PlayMode 编译阻塞统一返回 `status=blocked/blockedReason=PlayMode/playModeBlocked=true/nextAction`；`unity_sync_after_disk_write(triggerCompile=true)` 在刷新或编译前执行同一门禁。
- `unity_profiler_capture_*` 支持 marker 白名单/正则发现、Top 20、峰值帧、GC 次数、托管堆、组件统计、可选项目 telemetry sampler 和 baseline JSON 对比；终态生成 JSON/CSV 与 P50/P95/P99，不可用 marker 进入 `unavailableCounters`。完整 Timeline 树仍受 Unity 公共 API 限制。
- `unity_texture_importer_patch` 使用 `dryRun -> confirmToken -> apply`，确认令牌绑定资源哈希、`.meta` 哈希、changes 与 reimport 选项；应用仍要求项目写权限。
- `unity_screenshot_pixel_stats` / `unity_screenshot_compare` 只读取当前 Unity 工程内 PNG，返回哈希、区域、直方图、近黑/透明比例和差异比例，不返回原始像素。

## 2026-08-09 实施与验收结果

- Python：`57 passed`，仅保留 websockets 既有弃用警告；全仓 `python -m pytest -q` 不再误收集 `TestDomainService` 业务类。
- Unity 2022.3.62f2：在 `D:\MA\xclient` 临时启用 UPilot package tests 后，EditMode `45 total / 43 passed / 2 skipped / 0 failed`，终态由 `running` 轮询到 `completed`；验收后已移除临时 `testables` 配置并重新编译通过。
- Unity 6000.6.0a2：保留此前 EditMode `43 total / 41 passed / 2 skipped / 0 failed` 证据；当前机器已卸载该 alpha 版本，无法在 2026-08-09 复跑，不把历史结果表述为当前联机结果。
- `D:\MA\xclient`：Unity 2022.3.62f2，权威 readiness、编译和 Domain Reload 通过；最终编译错误 0。同步终态返回 `status=compiled`、`compileCompleted=true`、`isCompiling=false`，并保留 queued、accepted、started、finished 时间戳。
- `F:\xclient2`：已从 v0.3.23 Git 包升级为候选源码 `file:D:/upilot`，`packages-lock.json` 为 `source=local`；Unity 2022.3.62f2 readiness、编译和 Domain Reload 通过，最终编译错误 0。原文指定的 `Assets/GameRes/Roles/Heroes/Models/hero1010` 当前不存在，`unity_asset_find(query="hero1010")` 也返回空，因此该资源验收记录为“验收输入缺失”，不判定工具失败。
- 已有自动编译场景：`unity_sync_after_disk_write` 返回 `ok=true/status=compiling/attachedToExistingCompile=true/nextAction=unity_compile_wait`，不再携带 `failureSignature` 或 `compileError`；后续等待成功进入 `phase=completed`。
- 安全编译首轮竞态：F 项目曾在 `compile.request` 和 Domain Reload 均成功后，由重连瞬时 `hasCompileErrors` 触发假失败。`unity_safe_compile_and_wait` 现只把非编译类等待错误作为硬失败，瞬时 `COMPILE_ERROR` 会在冷却后以持久 `compile.errors.get` 复核；F/D 各两轮及最终轮均成功、错误 0。
- PlayMode/readiness：PlayMode 中 `unity_ensure_ready` 返回 `ready=false/inEditMode=false/playModeState=play`；`unity_playmode_start` 与 `unity_playmode_stop` 只在权威 Context 确认目标状态后返回 `confirmed=true`。退出后约 0.09 秒恢复 `ready=true/inEditMode=true/playModeState=edit`。
- 编译终态：无代码变化的快速编译也会补齐 `finishedAt`，三项目最终结果均为 `status=finished/phase=completed`，且 `commandQueuedAt/unityAcceptedAt/startedAt/finishedAt` 完整有序、`editorNotPumping=false/suspectedStuck=false`、结构化错误 0。
- 90 秒 Operation：首次等待 30 秒返回 `terminal=false/waitWindowElapsed=true/endedAt=0`，第二次等待同一 operationId 成功；Console 会话自动停止，JSONL、summary、manifest 和 SHA256 均有效，`droppedCount=0`。
- Console `Hold on`：根因为 Unity 主线程扫描 351 MB、124,475 条 JSONL 并保存全部匹配项，WebSocket ping 超时后服务又重放同一命令。JSONL 扫描现移到后台线程、结果内存限制为 `count`、支持取消；ReceiveLoop 可在长命令期间继续收心跳，普通断线不再重放超时命令。真实会话 `console_20260806_224418_960_b09f4207` 在 8.151 秒返回 5 条（`matchedCount=474`），并行 8 次 Hang 采样均 `mainThreadUnresponsive=false`、无重连风暴。
- Console 历史终结：Domain Reload 或 MCP 重启丢失 `SessionState` 指针时，`unity_console_capture_stop` 现会把磁盘上仍为 active 的历史 manifest 幂等终结。现场 5,078,363 字节会话成功写入 `active=false`、summary 和 SHA256 `4ac354c31812c151fd7cc7d19d727ac86dc93fcfc04146637a36a0650da10a57`，F/D 最终活动会话均为 0。
- 动画审计：`Hero_10003.controller` 返回 2 层、26 个 State、Motion、Mask、默认状态和 Transition；AvatarMask 返回 36 条 Transform；`Attack_01` ModelImporter 返回 Generic/NoAvatar、Clip 与 428 条曲线摘要，并发现未绑定 `Skill_01` Clip。
- CSV：`level_id=10044` 唯一命中，自动识别 GB18030、CRLF、第三行技术表头和 31 列。临时副本完成 `dryRun -> confirmToken -> apply`，将 `hero_attack_path_height` 从 `350` 改为 `351`；哈希由 `de0df10db7ebc43d49cd3baac3067b87f02cc2ec9b041f723cedb74ad40392e4` 变为 `e36873956219b987048aa66bd8d8a99d47b18639a1d745543dc0c11d270bcc61`，`outsideTargetBytesUnchanged=true`。业务源 CSV 未写入，临时授权已删除，默认模式再次 apply 返回 `WRITE_ACCESS_NOT_APPROVED`。
- Hang：15 秒主线程忙循环在约 10.5 秒时返回心跳年龄 10466ms、CPU 99.88%、`mainThreadUnresponsive=true`、`suspectedBusyLoop=true`。Console `Hold on` 现场 dump 位于 `D:\MA\xclient\Log\UPilotDiagnostics\hang-20260807-console-read.dmp`，2,598,555 字节，SHA256 `2b365830831da3f3067685c9b64ff2c9487ced6a5cbcef099c6723ae5b06905c`，`processTerminated=false`。
- 截图：GameView 不可用时降级到 SceneView，返回真实 `degraded/source/degradeReason`；PlayMode 帧缓存退出后通过 `fallbackSources=[recentGameView]` 返回 `source=recentGameView/degraded=true`，图片 640x360、SHA256 `b83d258db1f59fab46133e727e01ecaf79d39f451f64308b88141dfa11dc4bb8`。EditorWindow 捕获只接受 Unity `EditorWindow` 对象；Windows EditMode 回归创建同名原生窗口后仍解析为空，证明不会按 OS 标题误截图。
- 依赖图：以 `KingShotBattleTest.cs` 文件路径为根、`maxDepth=1` 时收敛为 77 节点/228 边，不再扩散到先前异常的 4756 节点。
- 项目 Smoke 回归：`F:\xclient2` 当前候选串行运行 `trap.skill.cast;trap.skill.trigger-control` 成功，报告 `2026-08-06_15-10-15`，两项 Case 均通过并在 EditMode/BattleProcess=None 后终结；`D:\MA\xclient` 当前候选分别运行 Formal 报告 `2026-08-06_15-05-39` 与 GM 报告 `2026-08-06_15-12-01`，requested/actual levelId 与 entrySource 一致且均 Succeeded。另保留一次并发干扰失败样本 `2026-08-06_15-08-26`，证明失败终态只在退出 PlayMode、BattleProcess=None、Joystick=Idle 后产生。

本轮未触发发布、未创建提交或标签。

## 公共接口

### 状态和长任务

- 所有 Bridge result/error 增加 `context`：`authoritative/source/sessionId/updatedAt/isStale/playModeState/isCompiling/activeScene/lastMainThreadPumpAt/mainThreadQueueDepth/processId`。
- `unity_operation_wait` 的等待窗口结束返回 `terminal=false`、`waitWindowElapsed=true`；仅 `jobSpec.timeoutSec` 到期才产生作业 Timeout。
- 编译状态增加 `phase/commandQueuedAt/unityAcceptedAt/lastProgressAt/lastEditorUpdateAt/editorNotPumping/suspectedStuck/nextAction`。
- `unity_tool_call(toolName,args)`：调用已注册但客户端未注入的工具；拒绝递归并复用注册表安全策略。
- Operation 的 `statusCall` / `cancelCall` 支持 `${start.field}`、`${status.field}`、`${operation.operationId}` 占位符，可把 start 返回的 `captureId` 等字段注入后续轮询/取消调用。
- `unity_profiler_capture_start/status/stop`

### 资源与配置

- `unity_asset_subresources_list`
- `unity_asset_dependencies`
- `unity_animator_controller_inspect`
- `unity_avatar_mask_inspect`
- `unity_model_importer_inspect`
- `unity_config_csv_get`
- `unity_config_csv_patch`
- `unity_texture_importer_get`
- `unity_texture_importer_patch`
- `unity_asset_reimport`

CSV 修改采用 `dryRun=true` 获取 `confirmToken`，确认时使用相同路径、主键、变更和文件哈希调用 `dryRun=false`。应用阶段要求项目写权限。

### 诊断与分析

- `unity_hang_status`
- `unity_hang_capture`
- `unity_script_analyze`
- `unity_script_dependency_graph`
- `unity_project_stack_detect`
- `unity_navmesh_status`
- `unity_navmesh_sample`
- `unity_navmesh_triangulation_summary`

脚本分析首版基于源码符号索引，不能语义确认的引用统一返回 `resolution=heuristic`，不得作为编译器级精确依赖使用。

### 截图

`unity_screenshot_save` 新增：

- `degrade=none|auto|recent`
- `fallbackSources=[recentGameView,camera,sceneView,editorWindow]`
- 返回 `requestedSource/degraded/degradeReason/source`。

新增只读像素接口：

- `unity_screenshot_pixel_stats`
- `unity_screenshot_compare`

默认 `degrade=none`，保持旧调用行为。

## 已合并或关闭的项目侧事项

- `F:\xclient2` Smoke Runner Case 实例生命周期已修复并完成 `trap.skill.trigger-control` 验收，仅保留回归。
- `F:\xclient2` 必需关卡配置快速失败已实现：10044 的 `trap.skill.cast` 校验 1004406，`trap.skill.trigger-control` 校验 1004406/1004407，失败签名为 `SmokeCase.PreconditionFailed.<caseId>` 并返回缺失引用与实际 trap list。
- `F:\xclient2` 非预期 PlayMode 退出已实现阶段化归因：`Smoke.UnexpectedPlayModeExit.<phase>`，报告包含最后步骤、退出时间、阶段耗时、退出请求和 Case 状态；正常 ExitPlayMode 阶段不误报。
- `F:\xclient2` 移动用例已把 fixture 未就绪与战斗干扰分别归因为 `TestFixture.NotReady` / `TestFixture.CombatInterference`。
- StageTool 投放点校验已复用同一保存批次的内存 `battleTraps`，消除 trigger_mode 读磁盘旧值造成的保存死锁。当前奖励模型直接内联于 `x_battle_rogue_resource_event.reward_list`，源码中不存在独立 reward-group 表或 `SaveRewardGroups` 服务，旧验收项按现模型关闭，不新增虚构表链路。
- `D:\MA\xclient` 新手预加载导致入口假阳性已在项目侧修复并验收，仅保留 requested/actual level 与 entrySource 回归。
- Smoke 失败终态必须等待退出 PlayMode、BattleProcess Idle 和测试服务释放；通用部分由新的 PlayMode/context 契约覆盖，业务资源释放仍由项目桥接负责。
- `F:\xclient2` Smoke 必需配置已从 Runner 的 caseId 硬编码迁入 Case 元数据 `levelRequirements`；Catalog 会公开 required level/trap，Runner 统一执行快速失败。
- `F:\xclient2` 移动连续性 Case 已在自身生命周期内启用 `Battle.TestDisableAwayLogic` 战斗干扰隔离，状态包含隔离模式和重应用次数，Cleanup 只恢复自身负责的位。
- `F:\xclient2` 非预期 PlayMode 退出明细新增项目 Runner 单调 `projectConsoleSequence`、序号来源和最后一条 Console 类型/摘要；该序号明确属于 `KingShotBattleTest.OnLogMessageReceived`，不冒充 UPilot 持久采集 session sequence。
- D/F 两个 KingShotBattle 项目都提供 `KingShotBattleMcpBridge.GetProfilerTelemetry()` 静态采样器，并公开活动弹道/世界技能特效计数；UPilot Profiler 峰值帧可关联流程版本、关卡、逻辑帧、战斗时间、单位分类/存活、Buff、弹道和特效规模。

## 保留在 xclient 项目侧的事项

- BattleEntered 的业务 readiness barrier 已在 D 项目桥接验证；F 移动 Smoke 已补主动战斗干扰隔离和结构化失败归因。
- StageTool 当前保存前流程已经统一从同一批内存态收集 born/level/building/trap/delivery-point/bytes blocker 与 warning，且 ExistingTrapSkill 校验同时覆盖触发模式和所属关卡 trap_list；不再新增重复 dry-run 保存链。若以后需要 MCP 无窗口调用，可再把现有 `RunPreSaveChecks` 的收集结果拆成纯数据 DTO。
- Profiler 业务关联适配已完成；调用 `unity_profiler_capture_start` 时使用 `telemetryTypeName=IGG.Game.Module.KingShotBattle.Editor.KingShotBattleMcpBridge`、`telemetryMethodName=GetProfilerTelemetry`。
- 本轮未修改两份外部 TODO 文档或业务配置。

## 验收门禁

> 2026-08-13 起，UPilot 本体 C# 编译和 EditMode 验收默认使用 `./Tests~/UPilotTest`；`D:\MA\xclient` 和 `F:\xclient2` 仅用于用户明确要求的项目侧/业务 smoke 验收或历史证据追溯。

1. Python 全量测试通过。**已满足：65 passed。**
2. UPilot Unity EditMode 测试通过且无编译错误。**后续回归以 `./Tests~/UPilotTest` 为准；历史已满足：Unity 2022 当前 47 total / 45 passed / 2 skipped / 0 failed，独立 BatchMode 编译错误 0；Unity 6 保留此前 41 passed / 2 skipped / 0 failed 证据。**
3. `D:\MA\xclient` 使用本地 `D:\upilot` 完成状态、编译、动画和截图验收。**已满足。** 长 Operation 的显式 90 秒用例保留此前证据，本轮补充当前 Unity 2022 全量 EditMode 回归。
4. `F:\xclient2` 项目侧新改动复测。**两个 Smoke Runner 文件的源码静态检查、括号、UTF-8 BOM、CRLF 和 SVN 差异范围已通过；UPilot 本体在独立 Unity 2022 工程编译通过。F 当前 UPilot Bridge 断连且 manifest 未安装 UPilot，新增项目代码尚缺该项目实时编译/运行回归证据。**
5. P0 在两个 Unity 2022.3.62f2 项目通过前，不进入发布流程。**P0 已满足，但本轮未执行发布。**
