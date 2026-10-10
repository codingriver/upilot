# 第四阶段及五项配套改进：实现与验收记录

日期：2026-09-30；续验更新：2026-10-01（Asia/Shanghai）。范围为本次用户批准的 A–E 批次；只做最小现有分支修改，不增加通用调度、依赖或配置，不恢复暂停的 Operation/Step。

## 交付状态

**代码已落地，定向测试通过，部分现场验收通过；不是全部矩阵已验收。**
默认规范工程 `D:\upilot\Tests~\UPilotTest`，Windows / Unity `6000.6.0a2`，Unity PID 37128。
本次未运行全量 EditMode、外部 C# 编译器、外部客户工程或发布。续验在独立维护授权及空闲检查后完成一次 Server/Bridge 刷新；没有重启 Unity。
未新增 Newtonsoft 或其他依赖，未修改权限/测试门槛。

| 批次 | 实现 | 已有验证 | 剩余边界 |
|---|---|---|---|
| A | 显式 cursor 副本清空 results；no_tests terminal=true；仅 GameView 空字符串 ID 在共享转换前移除 | Unity 7项、真实 no_tests、真实失败/取消及晚轮询、Python工具模型→dispatcher；持久化失败夹具通过 | 取消中Reload等扩展组合不声称已覆盖 |
| B | 外部持久化回执说明；维护初始化字段；isUpdating与按需精确PID窗口诊断 | 初始化定向、新Server在线透传；失败/取消/迟到状态夹具；实际PNG/manifest哈希；原Snapshot经真实Reload仍可查询 | 真实受控模态/停泵及后台诊断通过；PID变化/访问失败由隔离Python测试验证，未重启Unity；导入状态分支由契约测试覆盖 |
| C | HostView内容几何；GameView事实诊断；单路线鼠标派发和完整回执 | 四边夹具/混合输入/真实MCP计数1:1；Inspector与Hierarchy停靠截图；非对称GameView三种尺寸 | Console非活动标签安全拒绝已确认；混合DPI/遮挡完整矩阵；不修正GameView像素 |
| D | Console冻结阶段和缺口；保留既有Capture所有权 | 新Server phase实测/晚日志排除；双客户端、空文件、实际轮转/缺段夹具、受控Reload连续性、真实写入失败；首错保留夹具 | 公开场景事件恢复与运行中切Capture已通过；Console清空组合；双客户端轮转组合未单独执行 |
| E | Windows已显示Tooltip精确ID/domain枚举和Snapshot color分支 | C#错误身份/domain/类型拒绝；Python模型链；新版Server枚举/参数实测 | 当前无已显示Tooltip，真实正向像素及前后消失仍需现场；不声明正向支持已验证 |

## 最小代码落点与接口口径

- `Editor/Core/UPilotTestService.cs`：沿用结果深复制；cursor 仅清空副本 results，不改完整结果/磁盘历史。no_tests 不启动 Runner 且 terminal=true。正常终态仍由 Unity 清理/持久化决定，不由 Server 猜测。
- `domain/snapshot_service.py`：仅 GameView `instanceId=""` 删除；显式 `"0"`、真实选择器和 null 不顺便放宽。
- `UPilotServiceMaintenance.cs` / Bridge / Protocol / `state_store.py`：`maintenanceInitialization` 提供 recoveryChecked、checkedAt、storageError、source；journal与初始化分别描述。未知/旧Bridge/过期不伪造就绪，只读查询不运行恢复。
- `domain/status_service.py`：按需精确PID原生窗口诊断，最多8个候选；供compile/write-batch/focus使用，hang保留既有compile嵌套诊断。后台、导入、疑似模态只是事实/线索；不加心跳枚举、定时器、自动聚焦/关窗/编译/重启。
- `UPilotWindowDiagnostics.cs`：GUIView/HostView实际内容边界和宿主scale，分离contentRectVerified与cropComplete；不硬编码26px，不降低requireContentRect。捕获前后核验对象、容器、HWND和几何。
- `UPilotPlayInputService.cs`：panel实际命中及IMGUIContainer祖先决定单路线；UIToolkit用Pointer事件；禁用元素拒绝；补dispatchRoute/hitType/hitName/hitVerified/position/eventConsumption。消费不可观察就标unobserved，businessEffectVerified仍false。坐标命中的enabled取实际元素值。
- `UPilotBridge.HandleMouseEventAsync`：模态观察器返回object，需分别以GenericOkPayload或ModalObservationPayload具体类型发送；避免JsonUtility将ResultMessage<object>.payload写成空对象。仅改鼠标分支，不重构通用序列化或模态生命周期。
- `UPilotTestService` / `UPilotConsoleService` / `console_evidence.py`：回调边界持久化consolePhases，并投影consoleEvidence.phases。testExecution包含框架setup/teardown，不代表每条Assert；cleanup从RunFinished到UPilot终态；sceneRestore只观察自有运行的公开场景事件，始终partial。订阅结束解除，最多32个场景范围。
- `unity_console_search_logs(phase="")`：默认兼容。非空仅接受runGuid与testExecution/cleanup/sceneRestore。优先Capture session/sequence，否则回调时冻结Console游标日志（500条、单条8192字符上限）。缺范围、换Capture/域、Console清空、旧记录、截断、刷盘未覆盖均说明缺口，不查询整个运行冒充阶段。关联始终observed_during_run_not_causal，不改UTF结果。
- `unity_editor_windows_list(includeNativeTooltips=false)`：默认不变，开启后仅额外列出可验证宿主的已显示UnityEditor.TooltipView，含nativeTooltip、精确ID/domain。它不是EditorWindow，不能走普通移动/关闭入口。
- Snapshot `kind="tooltip"`：Windows、精确instanceId+domainGeneration、color、原生尺寸；width/height不缩放。通过GUIView/ContainerWindow与已核验PID/HWND原生截取；消失/变化/无法核验明确失败，不自动悬停/创建/聚焦/关闭，不回退主窗口/桌面。

### Snapshot外部回执与GameView诊断

既有持久化顺序不变：**manifest落盘 → 对最终字节计数和哈希 → 外部state回执 → 发布验证结果**。
公开status/collect/API中的persistenceStatus、manifestBytes、manifestSha256、snapshotSequence来自外部回执。
manifest内部pending/自身哈希为空本身不是失败，不写入“自身最终哈希”，不重写旧文件制造一致外观。
分别核验捕获结束、产物存在/字节哈希、持久化验证，success不能代替三者。

GameView记录真正截图帧的Screen/request/output尺寸、sourceLayout/outputLayout、aspectRatioMatches/aspectWarning、capturedAt和readbackPath。
contentOrientation/compositionComplete保持unverified，internalCrop保持unknown；没有自动翻转、补黑边、纠正比例或调整尺寸。
pixelSourceVerified是来源证明，不是血条完整/方向正确等业务断言。
本地 `Documentation~/Snapshot-Bridge-V1.md` 也补充了这些说明；该文件被仓库既有.gitignore忽略，上述契约在本记录保留，不修改忽略规则。

## 编译与定向结果

每次C#落盘后仅登记一次自动WriteBatch，没有另外重复触发手动编译。
场景恢复夹具批次（后续最新编译见文末“维护存储与停靠宿主续验”；历史身份保留）：

- WriteBatch：`wb-0e501ab8-77e4-4778-8796-4ec9b64e7d33`
- compileOperationId：`20faceb2e04d4c61853e88d5c5bc371a`
- compileRequestId：`req-58d48e4d-530d-45e4-acc5-b7bb268e5d5a`
- writeBatchCreatedAt：1790784832507；lastCompileVerifiedAt：1790784842701
- terminal/errorsVerified/correlationVerified均true，0错误、0警告；已读live结构化编译错误。
- Console编译边界搜索无返回Error，但其coverage=partial/domain_reload_boundary；不能宣称跨域Console无任何日志。
- 前序产品编译曾有3条未改动Flow UAC1009警告；最终增量0警告不表示消除这些历史警告。

| Unity定向runGuid | 结果 | 范围 |
|---|---|---|
| cce49f78-a182-4453-8a63-27b6286b3970 | 7/7 | cursor副本/分页/终态投影 |
| 442407db-e40f-416e-97ba-9685b561626f | 3/3 | 维护初始化与只读性 |
| 2ee616c2-a0b0-4d25-bca3-9cb556361530 | 12/12 | Snapshot真实相机产物/持久边界/Tooltip拒绝、Console阶段、窗口几何和输入 |
| 642f1ddd-4be5-4249-ae82-cca0aa21e502 | 3/3 | 鼠标具体回执序列化、混合输入和同名选择 |
| be14c40d-4f4e-45b1-b197-e15b7ddc1eaa | 3/3 | 最终输入enabled/禁用拒绝及直接回归 |
| d0239848-2f43-40a5-a977-5a67d9f8e351 | 13/13 | 实际Capture writer轮转/缺段/篡改/所有权；Snapshot失败与恢复；Runner终态提交失败 |
| b09f3bc9-8cba-4c45-b523-705105d1d3cb | 1/1 | 非对称GameView：原生、2倍请求、横竖比例不一致，保存真实PNG |
| d456d2df-c35f-4f11-8eb3-7eb5f6c2d091 | 6/6 | Snapshot取消、迟到状态防回滚、恢复不重捕；cleanup异常保留业务首错 |

上述成功运行均完成权威终态和清理。批次间存在重复测试，不把计数相加当独立用例数。不是全量EditMode。

Python最终组合：**287 passed，2条既有websockets弃用警告，12.20秒**。
在`upilotserver~`以`.venv/Scripts/python.exe -m pytest -q`执行下列文件：

```
tests/test_snapshot_tools.py tests/test_console_evidence.py
tests/test_startup_focus_diagnostics.py tests/test_editor_execution_state_v2.py
tests/test_service_maintenance.py tests/test_console_capture_tools.py
tests/test_p2_capture_attachments.py tests/test_p2_wp09_acceptance_capture.py
tests/test_compile_driver.py tests/test_deferred_compile_evidence.py
tests/test_empty_compile_evidence.py tests/test_hang_capture_lifecycle.py
tests/test_write_batch_changes.py tests/test_p1_interaction_contracts.py
```

## 实际现场与保留失败

1. **鼠标真实MCP**：使用仓库MixedInputProbe自有utility窗口。专用open仅支持SafeMode白名单，因此仅创建/关闭夹具使用有界persistent eval；输入走unity_mouse_event。先实测计数1:1但data={}；定位后修正具体类型，最终实测UIToolkit/IMGUI route、命中、坐标、enabled=true、dispatched=true完整返回，计数仍1:1。关闭窗口及session，无遗留订阅。初始失败与最终回执分别保留。
2. **Capture双客户端**：A/B为两个独立HTTP ClientSession；A创建并独自保管令牌，B只读attach/read分页/detach，缺失/错误令牌不能停止A；A合法stop并重复stop，文件字节/哈希稳定。自有session `console_20260930_225317_214_9becf280`。记录已脱敏，不保存ownerToken/hash。
3. **零日志Capture**：`console_20260930_225414_542_5dc98dbb`，真实0字节JSONL及SHA256；不是仅内存空列表。最终activeOnly查询activeCount=0、returnedCount=0。
4. **刷盘间隙**：初次双客户端脚本在flushInterval前读取，不能拿到已计序列的日志；保留失败并finally停止自有Capture。验证脚本明确等待已配置刷盘间隔后核对磁盘，产品不添加等待/flush/重试。phase查询补diskSnapshotNextSequence/scanComplete和嵌套业务ok检查，不能宣称未刷盘记录已覆盖。
5. **持久化夹具修复**：前序run `b27d6704-4a2c-4ea2-b5e3-2f7f28c4eff6` 为8/9；缺少serviceLifecycleId导致恢复正确拒绝。只修夹具身份并新增跨服务生命周期拒绝测试，未放宽产品隔离。最终12/12通过，初始失败保留。
6. 真正PNG/窗口像素覆盖来自相机多目标与四边不同色标记夹具；并非仅检查元数据。Tooltip负向测试不算真实提示截图通过。

## 续验现场（2026-09-30）

1. **部署刷新**：maintenanceId=`5aa4ff9d-6a6d-4e4e-a991-b1de7a728395`，完成一次授权维护，Server PID 15052→45572，Unity PID37128不变。新HTTP会话取得phase/includeNativeTooltips参数，维护初始化、queued/focus诊断和phase查询实际通过。核对源启动器、进程及变更行为；不将这些证据说成整个进程的加载字节证明。
2. **Runner公开终态/晚轮询**：预期失败run=`5aceec4e-eeab-4916-a2c0-fab87ac4843d`实际failed=1；取消run=`8106c45e-4e8f-40b8-8125-4625119bcfaa`实际aborted。两者均权威终态且清理完成；status/results/cursor一致，cursor没有完整results，后续全量读取不变。执行标记落在冻结阶段；更晚无关日志不会混入。预期失败用例不记为“测试全部通过”。
3. **Capture真实Reload**：自有session=`console_20260930_233051_543_8a0e6986`，domain34→35，Unity/Server PID不变。原session/token连续有效，前后标记序列递增，按原token停止。文件10740字节，SHA256=`d074a2674f618bce25030b00127aa7f62f230412979cbc02112cc3995d01d862`。没有服务重启、抢占或forceStop。
4. **GameView真实画面**：源900×600，输出900×600、1800×1200、600×900；三组PNG与manifest的字节/哈希匹配外部verified回执。人工打开PNG观察：原生图上下倒置；2倍请求保留900×600内容、其余黑区，并未放大内容；600×900存在黑区和右侧裁切。横竖比不一致正确警告，内容方向/构图保持unverified、内部裁切unknown。**诊断通过不等于倒置或裁切已修复**，本轮未修改这些像素行为。
5. **停靠窗口**：snapshot=`snapshot-304dccf11ae04af0ab511f8b264bf7df`为partial，持久化verified。Inspector318×603（空Inspector内容）、Hierarchy138×603实际PNG通过内容区域/裁切校验，已打开检查；Console因`WINDOW_CONTENT_RECT_UNVERIFIED / GUIView/HostView unavailable`明确拒绝。进一步只读反射检查被eval策略拒绝，未绕过策略或自动激活窗口；当时原因未确认；后续只读现场确认同一HostView的actualView为GameView而非Console，见文末记录。
6. **Snapshot真实Reload后读取**：上述公开Snapshot在domain39→40后以原ID读取，业务状态仍partial，持久化verified，PNG与manifest哈希不变，无重捕；Unity PID37128及Server PID45572不变。PlayMode后回到原干净场景。独立测试service创建的GameView快照不在公开service中可查，公开查询返回SNAPSHOT_NOT_FOUND；其磁盘证据仍有效，但不当作公共API恢复通过。公共恢复验证使用前述真正MCP作业。
7. **Capture真实写失败**：仅锁住自有session的JSONL写入口，发送一个测试标记；实际观察lastError和droppedCount>0。释放自有文件句柄后，用原session/token停止，activeCount=0。错误丢失的日志不声称已补回，也不把合法停止解释为内容完整。证据已脱敏；无他人Capture、无原文件破坏。
8. **保留未成功的临时夹具尝试**：GameView eval分别遇到Color/Vector2类型歧义和执行预算终止；均执行自有Capture/PlayMode清理，保留原失败，不重放超时eval。改为已有Snapshot测试程序集内的编译夹具，受正常WriteBatch和定向UTF验收。UTF发生Reload期间观察脚本遇到暂时空data，随后只观察原run，未重启测试。

## 尚未关闭的现场边界

- **原生Tooltip正向与前后消失**：目前枚举没有已显示的Tooltip；需要用户在Unity已有控件上保持真实原生提示显示。不得自动悬停、创建、聚焦、关闭或用手绘窗口替代。未验证Windows/Unity正向支持组合。
- 混合DPI/遮挡完整矩阵仍未完成；Console非活动标签拒绝原因已确认。真实受控模态/停泵已通过，PID变化与窗口访问失败由隔离测试覆盖，不冒充真实进程替换。普通utility被列为possibleModal仅是候选，不是已证明的模态阻塞。
- Console公开场景事件sceneRestore正向范围已补，内部RestoreSceneManagerSetup无事件时仍有边界缺口；运行中切Capture缺口已实测；Console清空组合未穷尽；GameView测试的真实Reload已产生明确`capture_switch_reload_or_cursor_reset`缺口，未把缺口说成完整阶段覆盖。
- Capture原方案要求的API契约、双客户端实际行为、磁盘产物、轮转/缺段、实际写失败和真实Reload均有各自证据，V14按原范围关闭；未将独立通过拼接成双客户端轮转+Reload组合已通过，也不增设该组合为关闭前置。维护journal损坏/读取异常后续已在规范Unity内使用隔离临时文件验证，未损坏真实维护记录，见文末记录。
- Snapshot迟到/取消/文件替换失败由定向夹具覆盖，真实已终态作业Reload读取有证据；不声称捕获进行中所有故障组合都已实测。
- 根TODO仅更新可证实进度；未达到关闭条件的矩阵仍保留。无新功能依赖、无权限放宽、无通用Operation启动、无全量EditMode。

## 本地证据索引

原始证据在 `Tests~/UPilotTest/Log/Stage4/`（既有忽略目录，不随源码自动分发）。以下是本次最终文件的字节数及SHA256；本记录不是原始产物的替代品。

| 文件 | 字节 | SHA256 |
|---|---:|---|
| a-results.json | 20381 | `eef3c7032e5a0dfb4fbb8692e71d71f3954def0c8e96b338308a53658be05a81` |
| b-results.json | 12461 | `b8a29c0ef9406bfb2c1786c4aaf04490ff74ad3c32b79a8fea465536191bb503` |
| de-results.json | 28934 | `1dd4f26d47ae64152adedff874d1138ec12614fcfed65138e82203f5e8b5ce38` |
| final-input-tests.json | 13416 | `b56f77bbcd7dda274cdfc6be464558a7aead838fdcc86f76746ecebcbe3924e4` |
| final-input-compile.json | 9877 | `a5c5a146560f8702449ca005a6da4d6938b2ea7ec74abe1a3c957add91757449` |
| final-compile-console.json | 3775 | `864acbe7f5212287a889cd3de4ace90da9004c773c924349a6937f702b2adee0` |
| no-tests.json | 8249 | `9aaa0b9cbb25bba7c5254c5589aa746814d58f1ce160d72064752cf6cfe7bbdd` |
| python-targeted.txt | 1077 | `3ae97cd91b966df2dc70bd07ea3509229d5445bd55a721618a03d5d5363f8a92` |
| mouse-mcp.json | 20306 | `71ab1fcb8ab7a2617b6075037229dafdb7c73f012b1a58bd12edf2f601918282` |
| mouse-mcp-fixed.json | 29017 | `1850f3a3649da4af75d1f13fa68ad54290dae5f31d158acfd6eb08c444197371` |
| capture-two-clients.json | 42936 | `ffee81754e3558f02fccfa86fa69ff6077ac18ee09ac66856e250aa013870afc` |
| capture-two-clients-initial-buffered.json | 16013 | `ebd5bfa240e97fbb48a9a8fa44f72da5ce686c1fad7f85f3af629ea5cf1f1da2` |
| capture-zero-log.json | 316 | `44c4f7f4c80f8441fc04d4ac85ef367f80416bca60542f98d489433c8c9bd463` |
| final-active-captures.json | 3055 | `a8968baf444963805a3f6a9a11be6ad084caf1de3ef9ee5eedaf1c140f7feaac` |
| c-results.json | 24556 | `c4f65da1572d42ee581b8c78072b561da076cb79629aa2cb5b90ab75d3512669` |

### 续验证据哈希

| 文件 | 字节 | SHA256 |
|---|---:|---|
| deployment-live-contract.json | 63468 | `91982fcb8e2a1c8cf48325b59620790c4d66af22daef3f905863bac39f4a9dfd` |
| deployed-status.json | 32167 | `f6d87b28d4ef28f91f11bbb5f3698e87c97fdbfbeb1a6ee9fdeceb9e4bb9269c` |
| runner-live-phases.json | 157603 | `0157a539dbe0c99d9468bd61a6231bcb42b83d33f5043dcae13581401a187d2b` |
| persistence-capture-matrix.json | 61980 | `3866a9bd148c776b03e6814262d6ac66ff3f0b2974cc346809b12528a4d5a6d4` |
| asymmetric-compile.json | 10506 | `dc35e80dc885e748af76d932751f8dd9a5b4310d49959f9f72493c507063439d` |
| gameview-asymmetric-results.json | 10804 | `3a4bf45df5b3056841dfe1af10ab5710dd4ea1414bb51d51448580c6e8d56689` |
| gameview-pixels-verified.json | 5267 | `d0e56120f7ec687635589a6468b822e350c0d010cfed10a893d185652e2ea77a` |
| gameview-reload-live.json | 179094 | `0efbc8cf6871f82123bd7180c0bed2cfab9d190425d8ead0c723858c1b070411` |
| gameview-reload-live-v2.json | 179948 | `3be17c6d017c97c5b0b330c7be9a55a465164e8d7e04615fa77187440f0dd61b` |
| gameview-reload-live-v3.json | 178362 | `c1f6a00a20e8e4ce0c8b7fd64e00d44b4a5f220b0f8cd9c604590f531e2784a7` |
| docked-native-capture.json | 59980 | `b5948d1055ee1bc2e58dc537083d968e54e685c7bd8b2bc430ef23daa5522ec3` |
| snapshot-live-reload.json | 164028 | `d52523b7200d5d43943133496cfb8ae6394c55ca4678179f948f03994fa61669` |
| capture-write-failure-live.json | 26611 | `942bfaaee8cb7c8a7e7603e5053607337d83e8fbf3453c47b07abbd91244f8f2` |
| final-persistence-boundaries.json | 19541 | `19b73cca68d0db0b16d7ba7a564af3591571eb76a8b28b01218c0feeac88b51b` |
| handoff-state.json | 70604 | `0853b3e1f14d0b415d0bf7729fa8f1ae484e2ccfa83cafad7b10d6445f063b73` |

## 本轮交接状态

末次新鲜检查：Unity为EditMode且ready，原验收场景未修改（rootCount=3）；Capture activeCount=0，当前队列complete=true、items=[]、Bridge activeCount=0。最后6项定向运行terminal/cleanupSucceeded=true。新版Tooltip枚举仍为0，没有替代截图或制造提示。工作区代码与报告尚未提交/发布。

## 2026-10-01 Console 场景恢复续验

- 仅在既有 `UPilotRunnerLeafAcceptanceFixtures.cs` 增加 Explicit 夹具，使用 UTF 公开 `IPostBuildCleanup`，不 Hook 内部任务。`Arm` 在运行前限定精确规范工程、单一已保存且干净的活动场景；测试临时建立空场景，RunFinished 后通过 `OpenScene` 恢复原场景。
- 最终运行 `2dc0b187-6737-4214-9893-47a8668ecc97`：1/1，terminal/resultAuthoritative/cleanupSucceeded=true。真实 HTTP 查询只在 testExecution 返回执行标记；cleanup 返回恢复前后标记；sceneRestore 仅返回场景打开期间标记，范围 sequence 6..6，保持 partial / public_scene_events_only_not_full_restore_boundary。原场景和根数量恢复，自有 Capture 停止，activeCount=0，实际 JSONL 字节及 SHA256 已重算。
- 初次夹具失败 `720087cf-f648-4700-a73e-1a5f97ae8a97` 是在 UTF 已切临时场景后才保存布局的假设错误，不是产品缺陷；改为运行前 Arm，没有放宽场景安全断言。原失败证据保留。
- 原生恢复验证 `fed30d4b-eb18-4115-847d-6aa74379891c` 测试叶子通过、场景确实恢复，但本机 Unity 6000.6.0a2 的 `RestoreSceneManagerSetup` 未产生这两个公开场景事件，sceneRestore 返回 phase_boundary_missing。此前已加载场景的恢复 `c9e8debd-cf4c-4440-8bf0-37f748b4299f` 也无此事件。保留两次覆盖缺口，不将后续 OpenScene 成功冒充 UTF 内部恢复已完整覆盖，不为补齐边界修改框架。

| 续验证据 | 字节 | SHA256 |
|---|---:|---|
| console-scene-restore-live.json | 81796 | `88fa43e56901e252eab3a925115f8d33d5288b3ec025ed77a2ef5544a31e029b` |
| console-scene-restore-fixture-failure.json | 53399 | `c0f93fd109c2a52f7fce27e944e4491aa8f311b5ddfb22758ab717f802fa44e1` |
| console-scene-restore-noop.json | 79252 | `fbe668b2a4274bb214f48ec5767ba42d133691f7027abf45e9e5ff8eeed81f20` |
| console-scene-restore-native-gap.json | 79256 | `a3dc85e54961f6b4f3d5e52ab13117e4ca07910d35b01c8c76f5b90fdb891ff0` |

### 运行中 Capture 切换与最终状态

- 运行 `97cd2b23-a55f-47f4-a6af-b51cf5805a00`：精确选取显式 DelayedLeaf，1/1，真实运行中停止自有 A session 并启动自有 B session；未操作其他 Capture。
- testExecution 查询返回 partial / capture_switch_reload_or_cursor_reset，logs=[]，没有拼接不同会话或扩大到整轮日志来伪造完整覆盖。A/B 均按各自 session/token 停止，JSONL 已重算大小及 SHA256，activeCount=0。
- 本轮只新增场景恢复验收夹具；Python/产品代码没有再次修改，既有287项Python结果沿用，不重复编译或运行全量。
- 最终只读检查：EditMode ready、关联最新编译passed；原验收场景 isDirty=false、rootCount=3；队列complete=true且items=[]、Bridge activeCount=0；没有活动Capture。Tooltip枚举仍为0，原生正向截图及消失矩阵未验证，未自动制造/悬停提示。

| 续验证据 | 字节 | SHA256 |
|---|---:|---|
| console-capture-switch-live.json | 68449 | `7d10aac40b0b9c6bf25ab002b35cc3bbe5c331c5ebc08577bec560c7bb57e293` |
| scene-phase-handoff.json | 61243 | `c1e237e0eb56a202d1c690fb7aa920330a73c6649d6cc9cbee322f3a4d7be2fe` |


## 2026-10-01 维护存储与停靠宿主续验

- Console只读核对：窗口 wire ID `568105589213756030` 的HostView实际活动页是 `UnityEditor.GameView`，wire ID `568105589213756028`。因此此前精确截图拒绝是非活动标签身份保护，不是已证实的几何缺陷；未激活Console、调整布局或放宽校验。
- 诊断中两次eval失败保留：一次隐式对象字符串转换触发非主线程ToString异常；一次只读枚举触及3000ms预算。未重放预算失败程序、延长预算或增加诊断脚本到产品；随后缩小为现有只读成员的一条reflection-expression取得实际身份。新发现的隐式ToString线程问题单独登记产品backlog，不扩入本次开发。
- `UPilotServiceMaintenanceTests.cs` 新增2个直接回归，补强既有损坏journal测试的公开初始化投影断言。真实 `FileShare.None` 使隔离临时journal读取失败，验证初始化前后字段、错误持续可见、无任务创建、原文件字节未变；700字符错误只投影512字符、不截断存储诊断。所有替换字段在finally恢复，未触碰当前工程维护journal。
- 最新WriteBatch：`wb-0aa529f8-7434-4762-9f94-8ba130fb2289`；compileOperation：`7fcde17828314d3b82a8e9d8b72da201`；compileRequest：`req-2791cde4-d1f3-4e43-b545-b8d396a31a77`。createdAt=`1790785556227`，lastCompileVerifiedAt=`1790785568178`；verified/passed，terminal/errorsVerified/correlationVerified=true，live编译0错误0警告。编译Console仍明确partial/domain_reload_boundary，不宣称全域Console覆盖。
- 仅运行5个精确选择的维护初始化用例：`3345594d-0033-4863-8a5d-6029d2a9444d`，5/5，terminal/resultAuthoritative/cleanupSucceeded=true。没有全量EditMode，没有再次更改产品代码或重复编译。Python代码未变，沿用既有287项通过结果。
- 最后检查：规范工程EditMode ready，domain45；原场景isDirty=false、rootCount=3；队列complete=true、items=[]，Capture activeCount=0。Tooltip仍未显示，正向像素与消失现场仍未验证。没有将测试独立通过拼接为未运行的组合；这些额外组合不新增为本方案的强制开发前置条件。

| 续验证据 | 字节 | SHA256 |
|---|---:|---|
| console-inactive-host.json | 10384 | `084eda1212bf6fd066311ca30fca0e3efc6311ff37ffc17aad8f5533d6f082f4` |
| maintenance-initialization-storage.json | 17989 | `f83f35c14988f7b65eb04bfcfd3148e2367e0b3af6344cf9629a06cf277d2868` |
| maintenance-storage-handoff.json | 61230 | `02f63ac019fdca1d84e12dfc2319cb07f51a7f0a861bef587e6f12a00d20a51e` |


## queued诊断受控现场续验与原范围收尾

- 复用既有UPilotNativeModalFixture，只打开自有 `UPilot P1 Unknown Modal` 一次；夹具按精确PID、owner、标题、Cancel按钮有界收尾。现场诊断返回mainWindowEnabled=false、同PID候选HWND=51515142、ownerHwnd=722938及possible_modal_window；末尾夹具记录unknownCalls=1、unknownResult=Cancel，原生按钮证据modalClickPosted=true。未处理用户窗口。
- 复用既有UPilotWriteBatchHangAcceptanceFixture，仅一次3000ms延迟/20000ms停泵；现场返回main_thread_not_advancing、editor_in_background，isUpdating=null，不以过期数据猜测导入。夹具结束时armed=false，实际区间1790786121026..1790786141026。未增加编译请求、聚焦、服务重启或新定时器。
- 开始/结束compileRequestId、lastCompileRequestedAt、lastCompileVerifiedAt与domainGeneration全部相同，仍为既有WriteBatch wb-0aa529f8-7434-4762-9f94-8ba130fb2289；最终EditMode ready、队列complete且items为空、Capture activeCount=0、原场景干净且rootCount=3。当前没有C#改动，不重复编译。
- Python只新增7个参数化直接回归：原生枚举访问失败、枚举后PID身份变化、主窗口消失、queued正常观察、过期/非权威导入数据不晋升、停泵优先。test_startup_focus_diagnostics.py共24项通过、2条既有弃用警告。与先前287项有重叠，不相加冒充总覆盖。测试不访问真实窗口、不编译、不重启Unity。
- D14的只读诊断要求已交付，移出根TODO；受控模态/停泵是独立诊断现场，不冒充同时有编译排队，也不声称真实PID替换或硬件混合DPI。原始现场摘录归档如下。V14按原计划的各项覆盖和三类证据关闭；不为未要求的组合继续扩展验收。
- 菜单执行链额外观察到data={}（req-9c88ed54-4172-4b35-93bc-cc77e9461afa）；本次依赖独立窗口、夹具计数和文件证明单次执行及收尾，不将空data当成功证据。只登记独立backlog，未扩大已限定的鼠标序列化修复。
- Tooltip只读枚举req-d7b95a21-453d-46a1-bb62-663d1a24e12d仍为0；本轮未自动制造提示，原生正向截图/消失矩阵继续明确未验证。

| 续验证据 | 字节 | SHA256 |
|---|---:|---|
| queued-modal-pump-live.json | 146548 | `881e1481b5e8bf47a1a32e08cf54e07f85ba56e6a4d7a2cf77675a470ff56176` |
| python-queued-diagnostics.txt | 832 | `1e33361969b64f39fb7046ada896acd1ab903b79e35d8f58b205e0faeb759626` |

### D14历史观察归档（已完成只读诊断开发）

- 观察：2026-09-24 canonical 项目 `Tests~/UPilotTest` 的登记批次 `wb-4e433bd7-fa0d-49f4-bcaf-ed9ae9080354` 于 `1790247507370` 请求编译，约 90 秒内一直处于 `compilePhase=queued`，Editor 心跳正常、`isCompiling=true` 但编译开始时间为 0。`unity_editor_focus_state` 指出 Unity 在后台；`unity_editor_focus` 返回 `FOCUS_NOT_ACQUIRED`。最终该批次以关联编译成功终结，不能将延迟误报成编译失败。
- 建议：在 `unity_write_batch_status` 的长时间 queued 诊断中区分 Editor 未聚焦/主线程持续运行/实际导入阻塞，提示可用且安全的等待/聚焦行动，并明确前台获取失败后的非破坏性处理。保留一次写批次/一次编译、不得自动重复发起编译。


- 2026-09-29 外部业务补证（弹道起点 Transform 开发，非包验收）：Unity PID `51160` 心跳正常而主线程停泵；批次 `wb-bcfb6961-0593-4e8e-bc13-3664152e5246` deferred / EditorContextStale，`unity_editor_focus` 返回 `FOCUS_NOT_ACQUIRED`。只读原生窗口枚举确认同 PID 模态“UPilot 端口配置失败”（HWND 9244650），后方还有“UPilot 自动修复失败”（HWND 9376560），主窗口 disabled。未点击对话框、改端口或重启；不能归因于 C# 编译失败。
- 补充建议/验收：在 hang/status/focus 只读诊断中提供精确 PID 对应的 modal/owner HWND、标题、主窗口 enabled 状态和安全 nextAction；canonical 项目用受控模态验证诊断，无需自动关闭未知提示。维持 P2 / 待现场验收，预计每次节省 3–5 分钟人工定位。双向引用：`F:\xclient2\TODO_UPilot.mcd` / `UPilot-20260929-ModalCompileBlock`；通用实施状态以本 D14 为准。


## 窗口遮挡/移动/尺寸定向续验：环境前置条件未满足

- 本次只加强现有 `NativeCaptureMatchesWindowPixelsAcrossMoves`：目标洋红、同名遮挡窗口绿色，20 次移动/变尺寸，精确 HWND 和原生前台断言；没有修改产品捕获逻辑。保存并登记一次 WriteBatch `wb-afce7b6c-ea3e-4ce6-ad18-36a0e59bb255`，关联 compileOperationId=`31300230d5f64c2bbc28a2120281ca50`、compileRequestId=`req-d755355d-c228-4233-aa17-3e378c671e01`。createdAt=1790786467776，verifiedAt=1790786479677；terminal/errorsVerified/correlationVerified=true，结构化编译错误/警告均为0，domain=46。没有重复编译。
- 仅运行4个精确测试，runGuid=`2f1d536c-ed0e-4e28-b297-449141ca10e6`：3通过、1失败，terminal=true、cleanupSucceeded=true、resultAuthoritative=true。禁止桌面fallback、捕获中几何变化拒绝、四边标记3项通过。
- 遮挡测试失败在绿色自有窗口 `foreground=true` 断言，尚未进入PNG保存和中心像素断言。该失败不能报告成遮挡/20次移动尺寸矩阵通过，也不能从此前同色探针推导它通过。没有删除或放宽断言。
- 只读focus诊断确认真实前台为 Visual Studio Code（PID6652），而非Unity PID37128。一次专用 `unity_editor_focus` 返回 `FOCUS_NOT_ACQUIRED`（req-5ef14f32-6da5-4af9-8efa-7f3de498a3a5），Windows未接受前台请求。没有通过模拟键盘、桌面点击、循环重试等方式绕过。需用户将 canonical Unity 置前台且保持，再执行该单项测试；本次不把环境前置失败当产品截图缺陷，也不将其改成跳过冒充通过。
- 已保留失败结果和结构化编译证据；结束时队列complete且无活动项、Capture activeCount=0，原upilot-acceptance场景isDirty=false/rootCount=3。没有遗留自有窗口或运行任务。原生Tooltip正向/消失现场仍需已经显示的真实提示，不自动悬停制造。

| 本轮证据 | 字节 | SHA256 |
|---|---:|---|
| window-occlusion-captures.json | 3155 | `0ba3bf9d13ee3d3a4fc09b2ed600f4238a2bc6eebc0d43012e6eaeaeb1d897c2` |
| window-occlusion-compile.json | 7031 | `973c56e93ac880386163a44930093be82e16072b6618f6f40d792ea3b7273c3c` |
| window-occlusion-focus.json | 4209 | `0be07581cf44f5a06f2995cd401b368fc8d25438d5db45c4c6ccddbe9c62c9bb` |
| window-occlusion-queue.json | 4788 | `17cca0199c0931ae8f397d6956d1f9cc99acfee926c9373ba59177100f47e45a` |
| window-occlusion-results.json | 16664 | `f2f55df896321784b8cae026765b05cd595591dccbc45a84530aa8bb83e48c19` |
| window-occlusion-scenes.json | 3308 | `98fd28c09f1b292c860ec7a8649b9bd4fbff22b2657bc0a410ae6695f52975c1` |


## 2026-10-08 前台恢复后遮挡单项通过

- 用户手动切前台后，专用focus查询确认canonical项目Unity PID23796/主HWND134414为真实前台；Server PID24852、Bridge session=fbc3796c863944ae9c01ee798a06c525、producerEpoch=46c655b676144c4bafbc5181145bf518/domain0。新会话仅有已记录进程身份及实际调用证据，Server完整加载源码鉴证仍为unverified，不以历史编译终态冒充本次重新编译。
- 只运行既有 `NativeCaptureMatchesWindowPixelsAcrossMoves` 一项，runGuid=`9e0e9784-7b05-41d2-8dd9-a74156ba8529`，1/1 Passed，terminal=true、resultAuthoritative=true、cleanupSucceeded=true；没有C#改动、重复编译或全量回归。首个请求因非公开参数dirtyScenePolicy在Schema阶段被拒绝（sideEffectsMayHaveOccurred=false），删除该无效参数后仅一次有效启动，拒绝回执保留。
- 未改动断言。20次移动/变尺寸、目标原生foreground=false、不同HWND的绿色遮挡窗口foreground=true、内容矩形/完整裁切和中心洋红像素断言全部通过。目标由320×220变为358×239；遮挡窗口保持320×220，宿主scale=1。首末帧PNG尺寸与公开metadata一致，已计算哈希；人工查看末帧确认目标洋红、遮挡绿色且按钮可见。
- 上次runGuid=2f1d536c-ed0e-4e28-b297-449141ca10e6的3通过/1失败保持原样，不回写历史。该前台阻塞已解除；未把本次单项扩展为混合DPI或任意平台支持证明。

| 证据 | 字节 | SHA256 |
|---|---:|---|
| foreground-resume-results.json | 10801 | `5f51ef9bd9384ccd754f0c95f56d72191cb129df201a3f0698a7ce0d0799049d` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/occluder-0.json | 1248 | `f14f04f384e8b1f834870328f2af726d23876ac371e7b8712e7b48ff460ab0bc` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/occluder-0.png | 3989 | `ab9400d92e75189deecfbcf6034a68d91d43833b059f5e10156bf971d0bec42e` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/occluder-19.json | 1249 | `7f73ef791afc97de479cad511cf36d5c418fe039f56016fca48123604b846fda` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/occluder-19.png | 3989 | `ab9400d92e75189deecfbcf6034a68d91d43833b059f5e10156bf971d0bec42e` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/target-0.json | 1247 | `412e46d54865b1c65e248e931f9fb627c0caa79423fe41f1eafd989b1f1a92a3` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/target-0.png | 3943 | `1a9c1573909c585765b8dd0cbdccdd303eada59cdc1773759192128b1688851a` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/target-19.json | 1249 | `e651ca95d5c137e6cac3a81e9573b8ca69bc34ae0251ccdb9300682baeada89b` |
| WindowOcclusion/e476abf605ca4919bb773c4a977af728/target-19.png | 4363 | `565ca7a0814cd182cafdb801a55dc1f7d078d0370721272beabda4e174268929` |

- 本次45秒有界只读Tooltip观察窗口内始终未枚举到已显示提示，未启动Snapshot；观察helper已退出，不留后台轮询。证据为Log/Stage4/tooltip-visible-watch-20261008.json。最终queue complete/items=[]、Capture activeCount=0，原场景isDirty=false/rootCount=3。当前剩余原生Tooltip正向/消失现场仍未验证，需要用户手动显示提示；不自动悬停、创建或模拟。

### 用户准备Tooltip后的只读观察（2026-10-08）
- 用户发出准备捕获后，进行45秒有界观察，公开列表仍为0，未启动Snapshot。记录：tooltip-user-ready-20261008.json。不能将列表为空直接归因为用户未显示提示。
- 后续对已核验Unity PID23796进行只读Win32顶层窗口枚举：前台为其主HWND134414，只发现主Unity窗口可见，未发现独立可见提示宿主；此结果仅说明该观察时刻的原生窗口状态，不证明所有UI提示均不存在。记录：tooltip-native-windows-20261008.json。
- 为区分识别失败尝试只读对象计数，但Eval在读取System.Reflection.Assembly时被EXECUTION_POLICY_DENIED拒绝，未取得对象数量。保留tooltip-native-object-count.json，不修改策略、不换后端绕过。未移动鼠标、改变焦点或创建提示；观察helper已结束。

### Tooltip 按用户决定降为 P5（2026-10-08）

- 用户确认当前看不到 Tooltip 文字，随后明确要求保留 TODO、优先级 P5，并继续后续任务。根清单 N05 已标记为用户暂缓、未完成；不再以该现场条件阻塞其余任务，也不自动观察或制造提示。
- 这不是正向验收通过，也不是已证实的枚举缺陷。真实原生像素、宿主一致性及捕获前/中消失矩阵继续待验；既有代码与失败/空列表证据保持原样。
- 恢复条件：已有真实原生提示可见后，按精确 instanceId/domainGeneration 捕获，核对 PNG/manifest 回执与鼠标、焦点未改变。只对实际验证的 Windows/Unity 组合声明支持。

### P5 暂缓后继续 Console 清空/重置阶段回归（2026-10-08）

- 仅扩展 `upilotserver~/tests/test_console_evidence.py`，新增12组隔离回归：testExecution/cleanup/sceneRestore × 可观察游标重置/无法识别清空 × 同一StateStore/重新打开持久化存储。
- 验证阶段查询重复读取仅使用冻结记录；重置时返回空日志并保留缺口，不借用整个运行的日志、不重新读取当前Console、不附着其他Capture；coverage保持partial、关联仍为observed_during_run_not_causal，查询不修改持久化原记录。无法识别清空时不声称冻结记录完整或具有因果证明。
- 本次命令：`.venv/Scripts/python.exe -m pytest tests/test_console_evidence.py -q`，47 passed，2条既有websockets弃用警告；包含前序用例，不与历史287项或其他定向总数相加。
- 证据：`Log/Stage4/python-console-clear-phase-20261008.txt`，SHA256 `81d0d40448aa71123504d0bf88c82e5cb1f58414076a8942495c172395787eec`。
- 未改变产品代码、C#/程序集、配置或部署；不触发编译/重启/全量EditMode，不清空真实Unity Console。真实清空现场继续保留在D08，不把模拟输入或重新打开StateStore冒充真实Unity清空/Domain Reload。本次未重新证明当前进程的加载源码身份。

### 2026-10-08 历史质量契约复核与菜单回执收尾

- 历史两项失败并非已证明的当前产品缺陷：a451dcd 的旧断言要求 RecoveryRequired 和重复取消携带 cancelAttemptCount；后续 eded9bd 直接删除了两个用例。当前生命周期要求未知结果 aborted、重复取消 ending/not_found 且 changed=false。现已在原 test_operation_runner_and_agent_rules.py 补回当前契约测试，保留错误码/解析诊断、cleanupSucceeded=false 的内部事实、单次 Start/Cancel、终态不重放和重启不恢复；未修改状态机、门禁策略或启动任何在线通用 Operation。
- 定向 Python 两模块43 passed；现有 check_release_quality.py 默认策略通过：contracts 414 passed，skill 和工具清单通过。docs.registry/docs.todo-id 仍默认跳过；历史 evidence 和安装证据仍 unknown，不写成通过。质量证据 sourceSha256=a85011e18e1b2eab119fe2257e7aee6ab0cc4492d63d0f17a8fcdd14fd65a9ac，HEAD=eded9bd399e8d8c279efeba2c686dcf309f5994f（含未提交工作区），适用于本次质量检查时点，不覆盖随后 Eval 修改。不触发发布。
- 新测试首轮错误假定公开 Operation 投影存在 cleanupSucceeded，第二轮误把未请求 Editor 验证当成 unknown；按实际字段职责改为内部 cleanupSucceeded=false、公开 editorVerification=not_requested。两份失败记录保留，未放宽单次执行、终态或诊断断言。
- 菜单局部修改：UPilotMenuService 将 object 返回值按 MenuExecuteResultPayload / ModalObservationPayload 具体类型发送，避免 JsonUtility 空 data。菜单 Python 模块12 passed；Unity ModalObserver 9/9，runGuid=a2df01a1-17f3-437f-af3a-e87366ab0223，terminal/resultAuthoritative/cleanupSucceeded=true。关联编译 wb-a31dce83-e5b8-4634-9df9-44bbe3afe11d，验证时间1791427281031。
- 真实 HTTP：普通 Receipt Counter 仅执行一次，计数0→1且返回 menuPath/executed=true；精确预期模态仅调用一次并按声明 Cancel；未知模态返回完整 commandId/宿主/等待状态，actionAttempted/actionPosted=false，由自有夹具精确 Cancel 收尾，原 commandId 查询返回 terminal/commandReturned=true 及 resultJson。未知与预期计数均0→1。无效模态声明和空路径保留 MENU_EXECUTE_FAILED/INVALID_PARAMS，后续计数仍1，拒绝未触发菜单。
- 菜单在线证据证明该具体 Bridge 行为及 MCP 回执，不等于 Server 全模块加载源码鉴证。未重启服务、清空 Console、改变用户场景、操作其他 Capture 或制造 Tooltip。
- 证据目录 Log/Stage4：quality-contract-restored[-v2/-v3]-20261008.txt、quality-contract-gate-20261008/、menu-receipt-live-normal-20261008.json、menu-receipt-live-modals-20261008.json、menu-receipt-live-modal-terminal-20261008.json、menu-receipt-live-after-errors-20261008.json。重叠测试数不相加。

### 2026-10-08 Eval 隐式 Unity 对象字符串转换收尾

- `UPilot-20261001-EvalImplicitObjectStringThread` 已修复：interpret/emit 的 BinaryExpr 与 compiled 字符串拼接共用 ConcatStrings；Unity 对象 ToString 走现有 context.Invoke 主线程/策略边界，普通 CLR 转换保持原有语义，不重试转换、不新增调度器。Core 程序集继续 noEngineReferences=true。
- 关联编译：wb-73098029-9b09-4107-b687-bc144bd8feb6，createdAt=1791428102453，compileOperationId=b8b74740068542b096ab2d21258a7402，verifiedAt=1791428117198；terminal/errorsVerified/correlationVerified=true，0 error、0 warning。最初直接引用 UnityEngine 导致的编译失败已保留，后续修正使用既有反射/类型边界。
- 定向 Unity：runGuid=869d6c37-78d6-4fb0-89f7-e6cc64f5fc7c，25/25 passed，terminal/resultAuthoritative/cleanupSucceeded=true；涵盖三后端、左右拼接、存活/销毁/抛异常 Unity 对象、CLR/null 与既有调度回归。
- 真实 MCP：interpret/emit/compiled 三后端各自正常 Unity 转换一次且 thread=1；ToString 原始异常保留，另读 Calls=1 证明没有自动重放；CLR/null 精确结果 prefix:clr:suffix|calls=1。自有 session/handle 对象在 finally 销毁，前后同类型资源数量均为0，session 已关闭且 cleanupErrors=[]。证据 eval-string-live-verified-v3-20261008.json。
- 前置测试脚本失败分开保留：compiled 不支持 TryStmt 的验证拒绝、旧 result 字符串解析、legacy GetInstanceID 误作 wire ID、持久 session 重名变量。它们不冒充产品回归；失败后精确清理自有对象，最终独立矩阵使用 handle。DestroyImmediate 后 eval 的 ==null 不作为销毁证明，以资源枚举计数为准。
- 本项从活动 TODO 移除，不等于 Eval 严格后端路由 N11 或 Runner/Snapshot 剩余矩阵完成。旧414项质量门禁早于此修改，不作为本次最终工作区质量证明；未声称 Server 全模块加载源码已鉴证。
- 证据 SHA256：`eval-string-test-results-20261008.json` = `9675b0f6a87378e8331929e2565fff03c8890cd60d096bd82bae36cb0ef2f39d`。
- 证据 SHA256：`eval-string-live-verified-v3-20261008.json` = `f285b7631279263767932f5c0685687900fe5074a3fd298d3b5b5e7dbb3772cc`。

### 2026-10-08 Runner 回调隔离与取消 Reload 终态续验

- 回调定向运行 `236c6ca8-1e31-46f5-afb9-b2949d2d405b`：16/16 passed，terminal/resultAuthoritative/cleanupSucceeded=true。8组错误 run/domain 的 RunStarted/TestStarted/TestFinished/RunFinished 在真实 UPilot 自有测试运行中注入，验证不读取错误载荷、不改变公开投影及完整叶子/事件状态；其余覆盖有界终态投影、cursor/gap 和10001条合成事件。后者不等于真实10001叶子 Runner 容量现场。
- 关联编译 `wb-392404cb-59cf-4682-94d5-996dc64a11ac`，compileOperationId=`b1418afc380c4f3bad708a06f139c7dc`，createdAt=1791429599705，verifiedAt=1791429612055，0 error/0 warning，correlationVerified=true。
- 取消中真实 Reload 运行 `cfeec787-3353-42d3-b5a1-c5365b86870d`：domain3→4；夹具在 beforeAssemblyReload 请求两次取消，实际 cancelAttemptCount=1。重连期间原查询返回 UPILOT_RECONNECT_PENDING/commandSubmitted=false，随后仅查询原 run，未重新启动。最终 aborted/terminal=true，cleanupSucceeded=false、resultAuthoritative=false，保留未证明释放资源及原失败解释。这不是取消清理成功。
- 2026-10-08 迟到复查：status/full/cursor 的 run/status/phase/terminal/endedAt/snapshotSequence/cleanup/resultAuthoritative/取消次数及未释放资源保持一致；cursor results=[]，最多一条事件。完整机器校验及六份源文件哈希见 `Log/Stage4/runner-cancel-reload-late-comparison-20261008.json`。
- 专用 test status 仅提供冻结历史 runnerState。核对当前 UTF 源码后，以只读 structured reflection 调用 `TestRunnerApi.IsRunActive()`、`IsRunning(originalRunGuid)`，两者实时返回 false，Bridge domain4/session9022f30f64fb4d2890fd7c8265d34111。这仅证明查询时底层无活动 Runner，不追溯证明原截止内已清理，不改写历史 aborted。
- 顶层 terminal 投影剩余取消/Reload组合已完成本次一致性核验；该独立投影项从活动清单移除。取消清理成功矩阵继续保留在 V02/V12；容量实机矩阵亦未关闭。未声明 Server 全模块加载源码已鉴证。


### 2026-10-08 Snapshot 最后异步目标取消修复与产物核验

- 真实缺陷：SceneView 最后一个目标等待 repaint/readback 时收到取消，旧 ExecuteJob 只在等待前检查取消，等待结束后仍发布 completed/success。仅在现有捕获 await 后增加取消检查：RejectArtifacts → FinishJob(cancelled,false) → return，不改 schema、持久化顺序或截图输出。
- 红灯：run `bc267888-81a4-4360-af91-44dd0170ed12`，1/1 failed，预期 cancelled、实际 completed；框架结果 authoritative/terminal 且 cleanupSucceeded=true。原始错误结果和 PNG 保留，不改写为通过。测试专用编译 `wb-b9bf8729-a781-4b98-9840-ebddbe44bc76` 已关联验证。
- 绿灯：run `b632f755-5d40-469f-a148-465d73d4f95b`，6/6 passed，terminal/resultAuthoritative/cleanupSucceeded=true。覆盖 allOrNothing/bestEffort 最后目标取消、取消后不捕获第二目标，以及既有 repaint/manifest/state 替换边界直接回归。新增用例在真实自有 SceneView repaint 等待期间取消，验证产物 rejected、终态冻结与 manifest 不变。
- 关联编译 `wb-8b2d5485-be7f-47c3-8972-ab4119e828d3`，compileOperationId=`a37a9a6458b24e62b283d573e8c5cf79`，createdAt=1791430504984，verifiedAt=1791430520626，errorsVerified/correlationVerified=true，0 error/0 warning。本次收尾没有新 C# 改动，没有重复编译或运行上述测试。
- 独立文件核验：`Log/Stage4/verify_snapshot_cancellation_artifacts.py` 检查红灯1组和绿灯3组的实际 PNG/manifest 字节数与 SHA-256、PNG 文件头尺寸与回执、取消状态及产物接受标记，全部通过；报告为 `snapshot-cancel-artifact-verification-20261008.json`，包含实现、测试和原始结果文件哈希。3组绿灯均 cancelled/success=false/acceptedAsEvidence=false，双目标请求仅生成第一目标；红灯 completed/acceptedAsEvidence=true 作为原缺陷证据保留。
- 尺寸核对：请求 320×180，实际 PNG 600×424，与回执及原生 SceneView 窗口尺寸一致。CaptureSceneViewAfterRepaint 的精确 EditorWindow 路径保留原生像素，请求尺寸用于可选 camera fallback，而 Snapshot 禁止该 fallback。因此撤销验证脚本不适用的“必须320×180”假设，不更改产品输出；此文件完整性核验不等于视觉构图验收。
- 收尾只读检查：规范工程身份正确、domain6/ready/fresh；queue complete=true、isStale=false、items=[]，活动 Capture=0；活动场景仍为 `Assets/UPilotAcceptance/upilot-acceptance.unity`、isDirty=false。run 范围 Error 查询0条，但阶段覆盖仍为 partial/console_cursor_clear_or_reload_not_detectable，不声称完整 Console 证据。证据为 `snapshot-cancel-final-{queue,captures,scenes,console}-20261008.json`。
- 尚未覆盖：公开 MCP start/cancel/status/collect 的实际捕获中取消链、GameView 对应现场、真正捕获进行中 Reload/取消组合与外部终态后的迟到回调矩阵；不据本次6/6关闭全部 D18/V09，不宣称 Server 全模块加载源码已鉴证。


### 2026-10-08 命令发送中断收敛与真实 loopback 验证

- 缺陷与最小修复：`CommandDispatcher.call` 在 send 抛普通异常时只删除 pending、留下 sent 非终态；CancelledError 绕过 Exception 处理导致 pending 泄漏。现有异常分支同时收敛这两种路径，记录 COMMAND_SEND_FAILED/CANCELLED、outcome=unknown，不重放，不把传输失败当作业务未执行，保留原异常向调用方传播。不记录异常原文，仅保留类型。
- 首错保护：send 尚未返回时 Reload 可能先冻结 COMMAND_RECOVERY_REQUIRED；新增回归先复现后加终态 guard，既有 success/failed 不被 send 收尾覆盖。两次红灯报告分别为 `command-send-red-20261008.xml`、`command-send-first-error-red-20261008.xml`，各2项失败，作为原缺陷证据保留。
- 定向绿灯：`test_session_identity.py` 新增9个参数化场景，最终联同 `test_service_maintenance.py`、`test_socket_shutdown.py` 共72/72通过，2条既有 websockets 弃用警告。最终报告 `Log/Stage4/command-send-direct-regression-20261008.xml`；中间2项和7项报告不替代最终回归。
- 5组实际 loopback TCP/WebSocket 验证生产 WsOrchestratorServer._handle/dispatcher：丢响应、Reload丢响应、已完成、send异常、send取消。对端实际收到一次变更；重连不重放；旧结果不能改写冻结记录或完成后继查询。Bridge 和 OS 身份探针为模拟，不能作为真实 Unity/进程故障覆盖；测试自有 socket/peer 在 finally 中关闭。
- 扩展范围保留失败：加入 `test_recovery_observation.py` 后73通过/26失败；隔离进程只替换为 HEAD 的修改前 dispatcher 后3通过/26失败，失败名称集合一致。该基线排除本次 send 修复为起因，不排除其他产品缺陷；部分用例期待旧 RecoveryRequired、重启恢复活动或 recover 动作，身份/首错等有效断言仍待逐项审计。新增根清单 R01，不弱化断言或批量标为过时。
- 证据：`command-send-verification-20261008.json` 汇总红/绿范围、原失败信息及6份源码SHA-256；`verify_command_send_baseline.py` 不回滚工作树、不修改在线进程。续做时重算6份源码哈希均匹配，XML计数也一致。
- 边界：本批只修改Python，未触发Unity编译、EditMode、服务重启或外部工程操作。在线 MCP 未加载本修复的证据仍未知，不能宣称实机验收或关闭V01；Tooltip继续P5暂缓。


### 2026-10-08 恢复测试契约逐项校正与 WriteBatch COMMIT 故障验证

- 本批仅修改 `upilotserver~/tests/test_recovery_observation.py` 与 `tests/test_editor_execution_state_v2.py`；未改产品生命周期、权限或检查政策。上批dispatcher修复仍未证明在线加载。本批没有C#/程序集改动、Unity编译、EditMode或服务重启。
- 恢复26项原失败按现行规则逐项映射而非整组跳过：观察未知必须一次终态aborted；清理截止一次timed_out；重启不恢复活动；recover不在队列允许动作中。原失败和中间失败XML全部保留，原来的3个通过场景保持原断言。
- 其中6个身份/终态证据拒绝输入全部保留，并改由真实status入口验证明确OPERATION_IDENTITY_MISMATCH、原run身份不变、业务终态不被接受、aborted冻结与持久化。另补并发只读一次、原截止/endedAt不变、首错与cleanupError分离、未知Capture停止不重放、显式后继任务不改写旧失败；不以断言删除或allowlist放宽换取通过。
- 旧恢复动作的grant/身份测试改为“任何证据都不能复活已移除动作”；仍有效的取消授权/令牌/精确项目/处置比较更新断言，另从 `test_queue_cleanup.py` 定向执行8/8通过，未修改该文件。
- 最终恢复直接回归：`test_recovery_observation.py`、`test_persistent_operations.py`、`test_persistent_test_jobs.py` 共64/64通过（其中修订文件29/29），证据 `recovery-contract-direct-regression-20261008.xml`。现行动作护栏报告 `recovery-active-queue-guards-20261008.xml`。各有2条既有websockets弃用警告，无失败/跳过。
- 夹具校正如实保留：初次新断言误把新lifecycle的load_test_jobs当作历史全表查询，实际按lifecycleId过滤；改为核对新生命周期为空且原store历史不变。后继任务测试恢复继承方法时误指向TaskDomainService，改为删除实例覆盖、恢复原MRO。中间14失败/15通过及1失败/28通过是夹具迭代，不宣称产品缺陷已修复。
- V03增加2个真实SQLite提交故障场景：保持生产WAL模式，仅在自有临时库加延迟外键与触发器，实际UPDATE成功后COMMIT抛IntegrityError并ROLLBACK。trace确认顺序，批次完整行与故障前一致、pending原ID保留、ready/correlationVerified=false；移除测试注入后仅接收同身份新sequence快照，无新编译/批次，completed和failed分别得到正确持久终态。
- 最初用独立reader制造锁冲突的夹具不适用于WAL（2失败/2通过，报告 `writebatch-real-commit-failure-20261008.xml`），未修改产品journal模式以迎合夹具。改用实际延迟约束提交失败后，`test_editor_execution_state_v2.py` 69/69通过，报告 `writebatch-commit-direct-regression-20261008.xml`，2条既有弃用警告。
- 逐项映射、原失败名称、现测试名称、规则理由、源码与报告SHA-256：规范工程 `Log/Stage4/recovery-contract-and-commit-verification-20261008.json`。原26项旧契约校正工作R01已完成，从活动TODO移除；不将其原历史失败覆盖为通过。V01实机组合/在线源码身份、V03断电/磁盘IO/进程中断仍保留，隔离数据库测试不冒充真实存储故障现场。Tooltip继续P5。


### 2026-10-08 WriteBatch 隔离进程退出与 inventory 完整性收敛

- 本批产品改动仅在 `upilotserver~/src/upilot_mcp/domain/queue_service.py` 的现有只读来源聚合分支；测试扩展 `test_editor_execution_state_v2.py` 和 `test_reliability_batch_inventory.py`。未改持久化事务/生命周期/维护权限；未执行Unity编译、EditMode、在线MCP查询、服务重启或外部工程操作。
- V03新增4组真实自有Python子进程直接退出：completed/failed × COMMIT执行前/COMMIT完成但内存发布前，固定退出码71/72和边界标记。保持生产WAL；父进程先检查非空WAL、integrity_check及完整数据库行，再创建新StateStore，避免恢复投影掩盖原始字节状态。
- 提交前完整原行不变，历史API为aborted/unknown；提交后保留verified/passed或failed/failed的关联证据。两者project_state仍是queued旧快照，新生命周期均无活动批次且非权威ready；重复历史查询不resume。显式新批次之后收到旧terminal，旧历史不被补写验证、不夺取新pending槽位。仅创建测试数据，没有编译或业务派发。
- 首轮4/4通过；增加非空WAL和后继批次迟到隔离断言后，状态模块73/73通过，报告 `writebatch-process-exit-direct-regression-20261008.xml`。真实隔离进程退出不等于物理断电、磁盘IO损坏或在线MCP服务崩溃；这些独立剩余仍保留。
- D03新增10组隔离矩阵。初次13项中6通过/7失败：Server信任complete=true，忽略truncated/untracked/计数不符/无有效ID/非boolean标记；合并201项后虽有issue，source仍present/complete。修正为现有分支对照可见来源事实与200项上限，缺口统一incomplete/unknown；没有放宽身份或权限。
- 进一步明确returnedCount必须对应有ID的实际输出项，补充断言复现1失败（原值1、实际0），再修正计数。保留 `inventory-completeness-first-20261008.xml`、`inventory-returned-count-red-20261008.xml` 和中间绿灯；最终inventory/queue两文件51/51通过，报告 `inventory-completeness-final-regression-20261008.xml`。没有通过删除/跳过断言消除红灯。
- 新矩阵同时核对Server已无命令记录但模拟Bridge仍报告的活动身份、501项本地输出上限和只读派发白名单；既有session漂移、OS拒绝、PID/创建时间/项目身份拒绝与现行cleanup授权测试纳入51项回归。Bridge及OS探针均是模拟，不声称真实故障现场通过；未声明无任务，更未自动取消/恢复。
- 本批最终不重复计数为124项定向通过（73+51），两次pytest分别有2条既有websockets弃用警告。6份源码SHA-256、6份红/绿XML及覆盖限制汇总到 `Log/Stage4/process-exit-and-inventory-verification-20261008.json`。新queue_service及此前dispatcher的在线加载仍未核验，不关闭D03/V01全项。
- 根TODO仅更新V03/D03真实剩余。下一项D04大结果边界和1009来源矩阵；Tooltip继续P5暂缓，不悬停、不创建或空轮询。


### 2026-10-08 D04 Bridge 大消息真实 loopback 与 1009 诊断

- 产品修改仅在 `upilotserver~/src/upilot_mcp/server.py`：接收异常读取sent/received close frame，补各自代码；内部Bridge端点不协商permessage-deflate，消息限制仍4 MiB。新增 `tests/test_bridge_frame_boundaries.py`，未改Unity C#/生命周期/业务路由，没有新增依赖、重试或框架。
- 首轮12项为6通过/6失败（`bridge-boundaries-first-20261008.xml`）：5项实际本地发送1009但异常code=1006，导致超限计数/来源漏记；另1项压缩分片Unicode恰好4 MiB被当前传输库拒绝，日志为已读取4194304字节后1字节帧超限。失败证据保留，没有删除断言或扩大上限。
- Bridge源码使用普通ClientWebSocket、未启用压缩；为避免修补第三方解析器/新增版本依赖，Server明确拒绝这项内部未使用扩展。最终边界测试同时覆盖不请求压缩及客户端请求deflate但协商为空，均通过实际生产handler/JSON解析；不是继续支持压缩后再声称边界已修复。
- 新增24项：18项ASCII/中文+emoji/分片Unicode × 4 MiB−1/等于/＋1 × 两种协商请求；2项原失败/成功终态保留；1项10次事件仅保留末8条且inventory不完整；3项1000/1011/对端1009区分。未知commandId不猜测，双向关闭码保留，日志不将对端1009报告冒充已测量本地入站字节数。
- 重连后实际出站send路径计数确认原模拟mutation至多派发一次，后继test.status不能被迟到旧结果完成；原命令完整记录保持不变。Bridge业务/OS身份探针为模拟，TCP/WebSocket、接收handler、dispatcher及inventory聚合是真实执行，不等于真实Unity故障现场。
- 修改后首轮21/21通过（`bridge-boundaries-fixed-20261008.xml`）；补关闭码负向矩阵后直接回归110/110；调整日志措辞和测试收尾顺序后最终5文件110/110，报告 `bridge-boundaries-final-regression-20261008.xml`。均无跳过/错误，最终仅2条既有websockets弃用警告；中间与最终110不重复计数。
- 最终源码、测试文件和4份红绿报告SHA-256及依赖版本汇总 `Log/Stage4/bridge-boundaries-verification-20261008.json`。本批没有在线MCP调用、Unity编译/EditMode、服务重启或外部项目操作；线上仍未核验加载新server/dispatcher/queue源码。
- D04仅保留在线加载与真实Unity发送前最终JSON字节边界、命令/事件单次发送和首错保留现场；不把loopback变成C#发送验收。Tooltip继续P5暂缓，其余批次继续按推荐顺序推进。


### 2026-10-08 D04 Unity生产发送路径与磁盘收包验真

- 新增定向夹具`Tests/Editor/UPilotBridgeSendTests.cs`，调用实际`UPilotBridge.SendEventAsync/SendResultAsync/SendErrorAsync/SendJsonAsync`，使用测试自有ClientWebSocket/loopback；不构造生产Bridge单例、不接管在线连接。11项覆盖最终UTF-8事件边界ASCII/中文emoji × 4 MiB−1/等于/＋1、封套导致的结果超限（有/无原首错）、正常结果context/timing、超大错误有界化，以及底层拒绝后有序marker仍可送达。
- 保留失败过程：原fixture编译CS0103来自`LogLevel`嵌套枚举未限定，改为`Logger.LogLevel.Error`后关联编译通过；首轮run=`4024cbd7-1019-409e-8e63-10ee0ead91c5`停在首例且无叶子结果，精确取消一次后aborted、cleanupSucceeded=true。缺少当时连接阶段证据，不宣称已定位为Unity产品发送缺陷。fixture后续仅补传输后台continuation、连接/接收有界等待、自有socket超时Abort/Dispose及阶段记录。
- 最终编译：writeBatch=`wb-d8a4f703-1fda-40a5-9c30-baee3c1be2e6`，compileOperation=`b7ec90408501408da16940e8a15ec5a4`，verifiedAt=1791435366435 >= createdAt=1791435354779；terminal/errorsVerified/correlationVerified为true，0错误。fixture注册内容哈希与当前文件一致。此次观察没有重复编译或启动测试。
- 原run=`b22c11d3-4cf6-4531-85fd-17a80ad998d1`于续查确认completed，11/11、failed=0、resultAuthoritative=true、cleanupSucceeded=true。独立Python verifier按原运行时段匹配11个目录，逐个核对实际消息字节、JSON语义、SHA-256、receipt及有序marker/Dispose。11份消息中4份事件分别为4,194,303/4,194,304字节，两份超限事件回执各232字节；超大错误4,669字节，消息<=512字符；低层拒绝用例没有业务消息，结束marker成功。原首错tracker断言来自Unity fixture通过，不冒充磁盘可推导信息。
- OS监听快照确认Unity PID23796在11个自有端口无残留监听。只读队列complete=true、items=[]；Capture activeCount=returnedCount=0，无停止其他session操作。公开Console查询返回0条Error，但phase范围仍partial，不宣称完整因果日志证据。
- 证据：`Log/Stage4/bridge-send-verification-20261008.json`、`verify_bridge_send_20261008.py`、`bridge-send-results-continue-20261008.json`、`bridge-send-listener-final-20261008.json`，每例实际收包位于`Log/Stage4/BridgeSend/`。此前红灯、取消及两轮编译记录保留。
- 本批完成的是Unity生产sender在隔离连接的验收，不是在线Python Server/Bridge联调；当前Server PID24852仅有进程身份，加载源码仍unverified。D04保留该在线缺口，不重复两侧已通过矩阵；未重启服务、未操作外部工程。Tooltip仍P5暂缓。


### 2026-10-08 Runner真实容量失败与筛选成本诊断（非容量验收）

- 真实容量夹具为5001个显式叶子，目标10002个开始/完成事件跨过10000保留窗口，不是10001个真实叶子。原run=`605b9d7b-9012-487a-914d-06ab626ba960`及任务探针run=`d6bdd8f5-0863-455b-a850-94f49163f621`均FirstProgressTimeout、0事件、aborted、取消派发1次、cleanupSucceeded=true；没有按通过归档。单叶子控制run=`3e86df4b-80b8-46ed-aff8-bc4656f9fac3` completed 1/1、2事件、清理成功。任务观察出现BuildTestTreeTask/UnlockReloadAssembliesTask，标签不等于根因。
- 两个缓存树regex候选试验（flat、common-prefix）分别只遍历80/751叶子且0匹配，约45秒超观测预算，均失败1/1；同场生产精确名单完整遍历6129叶子、匹配5001，约21.6/22.3秒。候选否决，未改生产过滤器；原候选源码归档`runner-selector-rejected-prefix-source-20261008.cs.txt`，SHA256=`3a76649e61a448691fe7fe02e01a377a9150b4805d44b11ba73652f2dacf0c75`，现fixture移除候选，仅保留生产精确集合及成本测量。45秒预算在Pass之间检查，不宣称可抢占单次原生调用。
- 最终诊断run=`a4532095-e4da-4c91-bfa0-49904ce35f3c` completed 1/1、failed0、terminal/resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased均true。`runner-selector-cost-20261008-055535.json`记录6129叶子全部遍历、精确匹配5001，总24127ms；过滤器构造12ms，匹配叶子Pass 19095.2743ms、不匹配Pass 4997.3439ms。只定位此次缓存树测量的主要成本，不证明实际Runner全部启动耗时来源，不是性能修复或容量验收。
- 对应WriteBatch=`wb-681735ee-6d82-4909-b3c3-c63ebb7d7877`，compileOperation=`3d5c9b91754342988150346871a12728`，createdAt=1791438811289、verifiedAt=1791438828180，completed/terminal/errorsVerified/correlationVerified=true、0错误；当前容量fixture SHA256=`35b82b4b9c8e54f7aa2da31a4f0e7424ca04d1b89d0daafc26333ca1d7a56e10`。初次重连中查询错误保留，仅继续观察同一批次，未重复编译。Console查询曾误用maxResults被拒绝，后改count完成只读查询，不将拒绝视为产品缺陷。
- 编译前只读holder任务数0，但两个原生EditModeRunner残留对象分别为`-8788:finished=True:running=True:runnerNull=True:complete=unknown:started=7`和`-5386:finished=False:running=False:runnerNull=True:complete=unknown:started=7`。底层m_Runner均null、无注册作业；RunningTests字段不足以证明正在运行，holder空也不证明所有原生对象释放。来源/归属未知，未反射Dispose/销毁，也未以编译为清理手段。
- 三份结构化Console观察都含`TestRunner: Unexpected assembly reload happened while running tests`，不能据查询时刻归因成三个新事件或本次run造成；不声明Console干净，不据此改写历史cleanup结果。该差异保留V02继续有界定位，不修改/Hook UTF。
- `Log/Stage4/verify_runner_capacity_20261008.py`独立核验22份产物与哈希，输出`runner-capacity-verification-20261008.json`且`capacityAccepted=false`；包括两次真实失败、控制、两个否决试验、最终成功诊断、任务/Console/原生对象观察、关联编译和归档源码。原失败不删除、不回写。最新Server加载源码仍unverified；本批没有重启、外部项目操作、扩大选择或增加15秒首进度期限。下一步必须有新的定位假设才能再做真实容量运行。

### 2026-10-08 N11执行前后端准备、直接回归与五目标同步

- 产品：`UPilotExecutionService` 的同步/异步 auto 在执行前准备 Emit；只有两个明确准备失败码降级到语义等价解释器。准备捕获不包含业务执行，显式emit/compiled不回退，取消/内存异常不被包装为可降级准备失败。实际后端为公开字段 `backendUsed`；有界原始原因写入现有 `resourceDiagnostics` 的 `EVAL_BACKEND_FALLBACK`。移除成功执行后的预热路径；不增加配置、依赖或通用路由框架。
- 编译过程如实保留：首批 `wb-79e21d00-29b1-4b81-af3a-a6975a2caff7` 因夹具误用Define出现2项CS1061；仅改为实际DeclareVariable后，`wb-268a8214-597b-4b61-96b8-2911c7e4d97e` / compileOperation `949d2c65d2bc4cdb98ac9c70553a87d9` 完成，createdAt=1791439952919，verifiedAt=1791439964733，terminal/errorsVerified/correlationVerified均true。验真脚本重新计算初批与修正批源码哈希；之后未写C#，不重复编译。
- Unity新增定向19/19：run `3dee17fc-c5c7-4838-864e-f92e1e5e9e92`；直接回归9/9：run `760ccfb6-3368-4eb5-95d2-7d5ba2e5bab6`。两次均completed、权威结果、cleanup成功。覆盖准备失败、无可用解释程序、同步/异步、业务副作用不重放、缓存/严格compiled、旧async lambda；不是全量EditMode。准备失败使用注入，不是原生运行时Emit不可用实测。
- Python `test_execution_tools.py` 28/28通过、2条既有websockets弃用warning。保留首轮缺pytest导入和次轮27过1败XML；后者核对提交 `8e55ea27e8ab055228974025027014bb3b369626` 确认Registry已从7升级8，仅更新过时版本断言/名称，保留全部写权限/非幂等/只读安全断言，并增加新诊断说明检查。
- 真实HTTP MCP：六组纯局部计算/Task.FromResult调用结果均42，auto冷=emit、热=emit-cache、interpret=interpreter、emit=emit、compiled=compiled、async auto=emit；第七组二维数组compiled明确CSHARP_COMPILED_UNSUPPORTED_NODE，无自动回退。使用独立代码键，不创建session、事件、动态用户类型或业务对象。工具保守sideEffectsMayHaveOccurred标记未改写。`n11-public-*.json`保存请求、封套和MCP isError。
- Agent规则51/Skill60：源render --check/check --mode source通过；精确规范工程五目标同步，两个安装校验通过；首次apply前保留三规则文件原始prefix/suffix SHA256，验证托管块外字节不变。实际第二次apply changed=false；46个受管文件（含两份安装元数据和整合记录）内容/mtime_ns不变，备份目录集合不变；最终五目标current。证据 `verify_n11_sync_20261008.py`、`n11-integrations-*.json` 与baseline；未手改安装输出。
- 在线说明读取一次实际tools/list：三个脚本CRUD说明与当前源码装饰器字符串逐字相同，D09-D已满足其说明核验边界，从活动TODO移除；这是新建HTTP客户端获得当前列表，不宣称其他AI客户端缓存已刷新。csharp_eval说明不匹配当前源码且无EVAL_BACKEND_FALLBACK，保留N11在线部署余项。五目标同步不能刷新Python进程；PID24852全模块加载身份仍unverified，不为消除待办自动重启。
- 结构化compile.errors再次确认关联编译0错误。Console仍含原发送夹具错误及Unexpected assembly reload观察，未确认各次原始事件归属，不声称Console清洁，不清空日志。
- 统一证据核验 `verify_n11_20261008.py` / `n11-verification-20261008.json`：保存编译源码哈希、两次Unity结果、Python XML、7组公开后端结果、安装身份与在线说明对照。N11保留未关闭：在线说明刷新及适用环境原生准备故障现场未覆盖。Tooltip保持P5暂缓；本批无服务重启、无外部工程、无全量测试、无发布。

### 2026-10-08 部署后核对、D04真实链路与回执标志修复收尾

- 延续既有授权维护 `80c73461-c88b-4a36-8208-3df8cbe90bb9`，本批未新增重启。Server PID24852→36408，Unity仍PID23796/createdAt134358983013306077；规范工程身份、连接、历史N11运行19/19与9/9权威终态可读。实际tools/list的三个脚本CRUD及csharp_eval说明均与源码装饰器逐字相同，含EVAL_BACKEND_FALLBACK。原说明不匹配结论保留为维护前历史，不能继续作为当前剩余。`deployment-postrefresh-verification-20261008.json`保存13份关联证据；工具说明匹配不证明Python全模块加载来源。
- D04实际当前链路：纯框架String.PadRight经真实MCP触发结果封套4198233字节超限（上限4194304），前后16/17字符正常调用通过；实际UPilotBridge.Instance发送唯一自有事件 `upilot.acceptance.d04.a811dc6b96034bfbae92cffa7a1363d7`，最终序列化4194305字节，以有界诊断由Server health/inventory关联。会话 `5d0ee5142c42414a8fade760ed2c9f9c` 和Server进程未变化，无重放。没有该在线交换的原始wire抓包/哈希，不与隔离发送矩阵混同。
- 实际发现：超限结果已产生，但错误detail将commandSubmitted/sideEffectsMayHaveOccurred误报false。最小修复仅涉及 `Editor/Core/UPilotBridge.cs` 与 `Tests/Editor/UPilotBridgeSendTests.cs`：SendBoundedErrorAsync增加默认false的resultWasProduced参数，仅SendResult超限分支传true，输出两个true及stage=response_serialization；outcome保持unknown、replayAttempted=false，SendError默认路径不变。原首错保留断言未削弱；补丁/改前源码保存在 `d04-flags-20261008.patch` 及同前缀before文件。
- 关联编译：WriteBatch `wb-96bb22db-ed27-4e8e-b8be-5c5881ada83a` / compileOperation `14ab0d9cbd4e4b308f6ff823e6aabaa2`，createdAt1791442179391，verifiedAt1791442215021，terminal/errorsVerified/correlationVerified均true，0错误。结构化includeWarnings查询确认3条UAC1009，均来自既有Flow/Schema/UPilot.Flow.Models.cs字典字段（386/407/496行），不属于本批两个改动文件；不称零警告，不顺带修改Flow。
- Unity定向run `381efa8e-0af8-4381-8256-f3fb3fe89a9d`：5/5，通过、权威终态且cleanup成功；只选择超限结果两例、超限错误、正常结果及底层拒绝五例，没有重复六项事件边界测试。实际MCP再验command `cmd-aafe9fd0-6e8e-4b31-82e5-7332894368c5`：4198235字节超限，外层/嵌套detail的两个标志与stage均正确，前后18/19字符正常。编译后Bridge session为 `531fda004d3044aeb5e5a6aebd5946a4`、domain14，Server仍36408。
- 独立收尾脚本 `verify_d04_closeout_20261008.py`：按run实际时间匹配五个fixture目录，不按最新文件猜测；重算5份实际消息大小/哈希，核对有界错误及原首错、新标志、正常UTF8与低层未发送；五组传输均有结束标记和Dispose，实际OS监听快照无五个自有端口残留。重算55份关联原始证据哈希、当前两份源码与WriteBatch一致；重新核对维护后保存的工具说明与当前源码。产物为 `d04-closeout-20261008-verification.json`。此核验未再次编译/启动测试/发送超限事件/重启。
- D03增量与安全边界：原自有超限事件诊断在正常Reload后仍带旧session、精确sourceEvent/4194305字节和commandId=unknown。当前队列fresh、items=[]但complete=false，唯一issue为BRIDGE_EVENT_OR_FRAME_TOO_LARGE；Capture活动数0。不是卡住任务，不抹除诊断或为使complete变true重启，也不能声称完整空闲。Console仍有历史错误观察，未清空、不称Console干净。
- 当前余项：D04在线关联子项已完成；N11在线说明刷新已完成；Server全模块加载来源仍unverified，原生Emit准备失败现场仍未覆盖；D03其他真实来源/OS矩阵仍保留。Tooltip保持P5暂缓。范围内 `git diff --check` 通过不等于整个已有脏工作树检查通过，不格式化其他改动。

### 2026-10-08 V03真实Windows文件共享拒绝的隔离故障矩阵

- 最小改动：只向 `upilotserver~/tests/test_editor_execution_state_v2.py` 增加4个Windows定向用例，原文件字节完整保留为前缀；`state_store.py`与前轮验收源码哈希一致，没有产品修改，不触发Unity编译。所有数据库均在pytest本批自有临时目录，无在线Server数据库、场景、Capture或维护操作。
- 实际故障：用Win32 CreateFileW在自有数据库上保留GENERIC_READ/FILE_SHARE_READ句柄。另一个读写OPEN_EXISTING实际返回ERROR_SHARING_VIOLATION(32)，未经mock的sqlite3/生产StateStore在 `_persist_batch_evidence` 返回SQLITE_READONLY(8)。断言保留原write_batches整行与project_state排队快照、pending原ID，terminal/evidenceAvailable/correlationVerified均不提升为成功，ready=false。句柄在finally关闭，不更改ACL、不填满磁盘。
- 组合：completed/failed × fresh_snapshot/new_lifecycle。释放句柄后新sequence的原批次权威快照才允许verified/passed或failed/failed；仅新建隔离StateStore生命周期时，原批次公开查询保持historical/aborted/unknown、无活动恢复、不重新编译，原创建时间1000不延长。此处生命周期是自有pytest进程中的独立StateStore，不是在线服务真实重启。
- 定向：初次4/4；与已有真实SQLite COMMIT失败2例及提交边界子进程退出4例联合10/10。最终加强到精确SQLITE_READONLY、失败调用栈与原始行回执后，`writebatch-os-sharing-evidence-20261008.xml`再次10/10、67未选择，2条既有websockets弃用warning。中间一次证据增强脚本因CRLF文本匹配未命中而未写入，后续执行仍是旧测试；对应first/regression/final文件保留，不用其结果证明最终增强断言。
- 独立验证：`verify_writebatch_os_sharing_20261008.py`读取最终XML和四份receipt，检查实际Win32/SQLite码、调用边界、前后整行相等；对四个数据库只读PRAGMA integrity_check、实际记录/终态/创建时间核对，再以原生读写OPEN_EXISTING证明自有共享句柄已释放，全程不写数据库字节并检查前后哈希不变。初版枚举误将pytest的current别名计为第五份，修正为canonical path去重后严格4份；原报告保留，权威报告为 `writebatch-os-sharing-verification-final-20261008.json`，明确supersedes初版。
- 范围：这是实际Windows文件共享拒绝，不是物理断电/磁盘满或在线MCP崩溃验收。V03仍保留其他磁盘IO和在线服务故障余项；不重做全部Python/Unity套件，不因缺现场而补发编译或重启。
- 静态检查口径：D04两个C#文件范围内git diff --check通过；包含根TODO的检查因其CRLF产生trailing-whitespace报告，原有脏工作树也有同类报告，未扩大到全仓换行清理，不能宣称全仓diff检查通过。


## 2026-10-08 D03 Unity队列生产端与独立执行项遗漏修复

### 本批范围
- 新增 `Tests/Editor/UPilotBridgeQueueTests.cs`（及meta）：只构造不调用构造函数的隔离Bridge、自有队列/跟踪表，调用实际生产ObserveQueue并保存JsonUtility回执。不注入在线队列、不执行队列动作、不改操作跟踪器、不注册后台回调。
- `upilotserver~/src/upilot_mcp/domain/queue_service.py` 仅修汇总：executingCount=1且有独立有效ID时纳入清单；已在active/queued中存在则不重复；执行ID缺失/类型错误/不可能的执行数保持incomplete；执行数0时不把被exclude的查询自身当活动；执行项纳入合并200条上限，且来源诊断保留其ID。
- `upilotserver~/tests/test_reliability_batch_inventory.py` 新增10组直接回归。原产品/测试文件备份保留在Log/Stage4/inventory-executing-*-before-20261008.py；不回退既有未提交改动。

### 编译与测试证据
- WriteBatch `wb-a9831539-777b-48ac-9b65-6ce72ce2b52b`，compileOperation `1819de0724ea424580fff4dfe3eeb189`，compileRequest `req-a241fd48-1223-4930-9a18-7446f1ee786d`；createdAt=1791444278599，verifiedAt=1791444301001。terminal/errorsVerified/correlationVerified为true；本批结构化编译0 error/0 warning，不改写前批3条Flow警告记录。Unity正常Reload至domain15；没有服务重启。
- 精确预览matchedCount=7，带expectedSelectionDomain/expectedSelectionSnapshotId启动：runGuid `ad05180d-0338-4b07-b7ff-18e5485881ae`，7/7，authoritative terminal、cleanupSucceeded=true。
- Python先红7失败/1通过，再绿59/59；加入执行项跨200条上限的两组后最终61/61。最终文件inventory-executing-final-20261008.xml；2条既有websockets弃用警告，未运行全量Python套件。
- 核验脚本verify_inventory_unity_producer_20261008.py核对关联编译、当前C#哈希、实际7份snapshot.json、测试终态和XML；将每份原样JSON分别交给备份旧生产代码与当前生产代码，在隔离StateStore/模拟OS身份下对照。带执行项的真实Unity回执旧代码只显示1条，新代码2条；只读调用列表未增加其他动作。报告inventory-unity-producer-verification-20261008.json及各目录server-before/after.json保留文件哈希。

### 操作失误及证据边界
- 首次测试调用错误地同时传fixture与assembly，默认union扩大为6154项；runGuid `6cf2f501-24a1-48e5-b464-1b73a92446d6` 已首进度超时aborted、0事件/0叶子结果、取消一次、cleanup成功。属于本次选择器使用失误，不是有效定向验收或新的容量根因证据，更不声称全量通过。后续先test_list预览，仅fixture并绑定选择快照后运行。原请求/终态保留，不隐藏此次未经要求扩大测试范围的尝试。
- 初版离线核验脚本退出TemporaryDirectory时遇到自有SQLite连接未及时回收的Windows共享拒绝；不是在线产品故障。修为保留独立证据目录及回收不可达连接后完成核验，未清理未知目录或在线数据库。
- 本次源文件保持原CRLF；针对相关已跟踪Python文件的git diff --check仍报告CRLF尾空白，未宣称通过、未批量格式化或调整检查配置。
- Unity生产方法在隔离自有队列实际执行，不等于在线Bridge队列故障注入；OS身份仍为模拟。真实历史淘汰、OS访问拒绝/PID复用以及Server全模块加载鉴证仍未完成。
- 本批Server改动未刷新在线进程，不宣称当前HTTP端点已加载此修复。最后在线检查项目身份正确、domain15、Capture activeCount=0；队列items=[]但complete=false，唯一issue仍为原BRIDGE_EVENT_OR_FRAME_TOO_LARGE，不作为完整空闲、不清除历史缺口。
- Tooltip继续P5，未悬停、未捕获；没有外部项目操作、通用Operation/Step启动或服务维护。

## 2026-10-08 Runner 完整方法筛选诊断（生产优化前）

- 本批只修改 `Tests/Editor/UPilotRunnerCapacityTests.cs` 的显式诊断；未修改生产筛选、15秒首进度期限或执行5001叶子。
- 两次诊断构造错误均保留：run `29bc6090-9ff9-4f0c-9e09-0dd7c6f51408` 错把Runnable子叶当作Explicit；run `4abdfc96-3d05-4ce1-8975-04e8eb1c2aae` 候选传`.dll`而基线为程序集简单名，零匹配不能用于否定策略。均失败终态、清理成功。
- 有效fixture反证：run `32ee3e81-2e42-4638-a61d-8cfb48e53795` 使用相同规范程序集名，fixture虽命中5001叶子，Explicit参数化方法祖先的IsExplicitMatch=false，而逐叶子基线=true；因此禁止以fixture整体替代。该诊断失败保留，不能改写为测试全绿。
- 最终收窄为完整参数化方法，run `569ff1f7-0820-41e8-ac29-5866c4308aa7` 定向1/1通过、权威终态且cleanupSucceeded=true。关联WriteBatch `wb-f2296b88-bce9-48ca-b16b-34f0dc8d4f3e`，compile request `req-ceac5876-4b5a-4426-9528-cad641432af0`，结构化0 error/0 warning。
- 实际缓存程序集6155叶子、选择5001叶子（均Runnable，参数化方法祖先Explicit）：逐叶子Pass=36507ms，fixture=111ms，完整方法=119ms；叶子集合及祖先Pass一致，Explicit祖先仅基线/方法满足。Runnable子叶的原始IsExplicitMatch并不等价，不声称所有筛选语义通用等价。
- 独立核验 `Log/Stage4/runner-method-verification-20261008.json`；报告 `runner-suite-selector-20261008-075723-437398fa52e7483a8514f9a28e955c82.json`，1785452 bytes，SHA256 `5410f611bd12f37c69c51e24e0c70e267bc6ac281e7b8afa41d17a9591a7ddb9`。源码SHA256 `0e314370b9f805b39c4e217fa90b29c328cb2b0bf9d6378dd4f14bbff7e4901c`。各轮编译、失败及验证脚本均留Stage4，未覆盖其他UTF/部分选择/Explicit子叶或真实容量。
- 后续只考虑有权威发现树、完整子树及Explicit语义证明的最小优化；未知/部分选择保持原路径，不靠名称前缀推断。不以本诊断代替真实Runner容量验收。

## 2026-10-08 Runner 完整方法生产优化与真实容量续验

### 最小实现与关联编译
- 本批只修改 `Editor/Core/UPilotTestService.cs`、`Tests/Editor/UPilotTestSelectionTests.cs`、`Tests/Editor/UPilotRunnerCapacityTests.cs`。增量补丁 `Log/Stage4/runner-opt-source-20261008.patch` 保留，未改UTF、15秒首进度期限、持久化协议或依赖。
- 在权威发现树中确认完整参数化方法：直接子叶全部Runnable，Method/TestCaseCount元数据完整、名称在程序集内唯一、全部子叶已选中，才使用一个内部方法筛选；非序列化发现计划绑定选择快照。公开选择集合和快照不变，未知/部分选择、Explicit子叶、冲突、嵌套和过期计划继续精确叶子路径。
- WriteBatch `wb-2759be80-26a0-416e-9997-a79ca2a556aa`；compileRequest `req-8c91bb3f-eec7-403a-b6fc-4f6ba024d2f5`；compileOperation `b113b261e6674d42ab274618631a0dba`；createdAt=1791447207901、verifiedAt=1791447230455。terminal/errorsVerified/correlationVerified均true，结构化0错误、3条未修改Flow Models字典序列化警告。续验期间未重复编译。

### 已完成验证
- 选择器直接回归47/47，run `b2fea1d2-08d2-4154-9141-f51ff3e10195`；实际UTF生产筛选诊断1/1，run `004ef390-9b81-4cdc-a03d-8046696b96cc`；真实三叶控制3/3，run `d052856e-323a-4ba0-a95b-810c19c4e9a9`。全部权威终态、cleanupSucceeded=true、未释放资源为空；三叶控制为读取原运行，不重放开始。
- 实际UTF缓存树6176叶子，生产筛选匹配5001、完整遍历61ms；Explicit祖先门槛、部分选择精确性及缓存树不变均验证。报告 `runner-production-method-20261008-081714-8fcc80d2101e44388d400dfa1287d3a3.json`，360986 bytes，SHA256 `02c52f5f1166e4dcb941830919790bbe7262ab36782af768152d6d70543e69d4`。此诊断不是5001叶子执行。
- 独立脚本 `verify_runner_optimization_20261008.py` 复核三源码哈希、编译关联、精确预览/结果集合及上述报告；输出 `runner-opt-verification-20261008.json`。脚本首版误用结果字段fullName，已按实际testName修正后通过；不是产品失败。
- 真实MCP只读cursor验证：容量运行cursor用于三叶运行被TEST_RESULT_CURSOR_MISMATCH拒绝；三叶运行6事件后空页保留序号6；再次full查询的results/events/snapshotSequence/终态与原记录完全一致。证据runner-opt-capacity-cursor-mismatch、runner-opt-small-page/empty-page/full-after-pages-20261008.json。

### 容量执行与边界（2026-10-08 已补终态；完整 API 与去重仍未通过）
- 规范工程精确fixture预览5001，未混入assembly union；绑定domain `61bbc5e115f34c35bea94057a4bfdd07` 和本次选择snapshot `438ba5eb39adfc93bdae5303f731b200acc6934affa56c32763b3f3f1a8ad0b7`。
- 单次启动run `983f2386-4053-4802-8e18-55aa08e6896a`，startedAt=1791448024176；首叶开始1791448030925，相差6749ms，小于原15秒期限。原运行现已completed，endedAt=1791449139438，5001/5001通过、failed=0、terminal/resultAuthoritative/cleanupSucceeded均true、persistenceError为空；snapshotSequence=10009。此前“终态待补”为中间观察，现以保存的terminal-status、cursor与磁盘结果更正，不重跑测试。
- 当日只读核验：5001个结果身份唯一且名称集合与精确预览一致；原Library/TestRuns文件与证据副本逐字节相等，6525677字节，SHA256 `f76d0a24e620e1cf3da6f179ef65cd8f0baa43e279d041067fe6f97bd22741ab`。10页共10000事件与磁盘完全一致，实际保留sequence=4..10003；第11页为空并保留cursor=10003，过期/begin返回GAP、跨run返回MISMATCH。
- 新确认的未完成边界：无cursor full回执为BRIDGE_RESPONSE_TOO_LARGE，actualBytes=4951845、limitBytes=4194304，不是完整结果API验收通过。另，早期observe-01的sequence=2与最终sequence=10003是同一L0000/leafKey/Passed的leaf_completed；不是leaf_corrected。现有RecordFinalLeafResult仅在有界events查找previous，旧事件被淘汰后会重新追加；保留窗口后的去重缺口继续留V12。最终5001结果无重复、均Passed，不将事件问题改写成测试执行失败。
- 核验脚本 `Log/Stage4/audit_todo_closeout_20261008.py` 与报告 `todo-closeout-audit-20261008.json` 保存上述断言及证据哈希；这次只读取已保存结果，没有重新执行Unity/Python测试、编译或在线请求。原尚未运行的容量verifier中10002/earliest3假设不作为通过证据。
- 当前会话domain20；Server全模块来源仍unverified，D03新Server修复未刷新，不将本批C#实测当作Server部署验收。Capture预检activeCount=0；queue items=[]但complete=false，唯一原超限事件来源缺口保留，不报告完全空闲、不清除缺口。
- Tooltip保持P5；没有外部工程操作、全量EditMode、服务重启、未知Runner对象销毁或通用Operation/Step启动。其他Unity/UTF版本及取消Reload成功仍未覆盖。


## 2026-10-08 当日完成清单与 TODO 状态核对

> 本节是首次当日核对快照；后续V12修复、验收及授权维护结论见文末“V12去重与完整结果传输续验”。原失败记录保留，不将后续通过回写为此前通过。

本节以2026年10月8日（本地日期）的已有执行记录为范围，汇总本仓库任务，不声称覆盖外部工程工作。完成项归档于此，根TODO仅保留未完成工作；已独立结项的子项不代表父项整体关闭。测试数量有重叠，不汇总为全量回归数量。此次更新只是文档与保存证据核对，不是新测试执行。

### 一、已完成并可独立结项：5项

| 完成项/原标识 | 今日完成内容与依据 | TODO处理 |
|---|---|---|
| 菜单执行空data回执修复（原独立问题，未另造编号） | 按具体payload序列化；Python菜单12项、Unity ModalObserver 9/9；真实普通/预期模态/未知模态及参数拒绝回执验证，动作计数均未重复 | 按独立缺陷完成归档，不再作为待修；不外推全模块加载身份 |
| UPilot-20261001-EvalImplicitObjectStringThread | 三后端Unity对象隐式字符串转换走既有主线程/策略边界；Unity25/25；真实MCP正常/抛异常/CLR/null与单次调用及自有资源清理验证 | 已完成，维持从活动TODO移除；不等同N11整项完成 |
| Test Runner顶层terminal投影一致性独立余项 | 回调隔离/投影定向16/16；取消+Reload的status/full/cursor冻结终态一致，历史aborted及cleanup=false如实保留 | 投影项已完成；取消清理成功仍归V02/V12，不能把失败运行改成成功 |
| R01：26项旧恢复契约逐项校正 | 完成旧/新契约映射，保留身份、首错、单次执行断言；直接回归64/64，现行动作护栏8/8，原失败记录保留 | 已完成，维持从活动TODO移除；不关闭V01实机矩阵 |
| D09-D：脚本CRUD在线说明核验 | 实际tools/list三项脚本CRUD说明与当前源码逐字一致；维护后再次核对 | 该编号子项已完成并移除；D09-E替代工程安装/连接仍开放 |

### 二、今日已完成子工作，但活动父项仍未关闭：9组

| 父项 | 今日已完成内容 | 证据与剩余边界 |
|---|---|---|
| V15：窗口精确截图 | 前台恢复后遮挡/移动/尺寸单项通过，20轮原生像素/矩形及PNG回执核验 | Unity1/1；模态、取消、自毁、Reload、最小化等组合仍待验；不宣称混合DPI通过 |
| D08：Console阶段 | 新增12组清空/重置/持久化重读的冻结范围回归 | Python47/47；真实Unity Console清空组合仍未覆盖 |
| V01：命令恢复/不重放 | send异常/取消收敛、pending释放、冻结首错保护；5组真实loopback收发 | Python直接72/72，另有R01校正后的64/64与护栏8/8（不相加）；真实Unity故障组合及全模块加载鉴证未完成 |
| V03：WriteBatch持久终态 | SQLite COMMIT失败/回滚2分支、提交边界自有进程退出4组、Windows共享拒绝4组及释放后再验证 | 状态模块最终73/73，共享拒绝4/4及直接回归10/10；均为已注明隔离范围，非物理断电/磁盘满/在线MCP崩溃验收 |
| D03：inventory完整性 | 截断/计数/来源缺口修复，补独立executingCommandId遗漏及200条边界；原超限事件缺口跨Reload保留 | Python最终61/61，Unity生产ObserveQueue隔离7/7；最新Server修改未在线刷新，全模块加载/其他来源与OS故障未完成 |
| D04：Bridge超限诊断 | 4MiB边界、Unicode/分片、1009归因、拒绝压缩协商；生产发送路径、真实在线关联及已执行标志修复 | Unity发送11/11、标志直接5/5和保存的在线错误/事件；全模块加载鉴证仍未完成。诊断通过不能替代V12完整结果获取 |
| N11：执行后端路由 | auto仅在明确准备失败时回退；显式后端严格、不重放；后端回执/诊断、Agent规则51/Skill60及五目标同步、维护后在线说明刷新 | Unity19/19及9/9、Python28/28、7组公开后端调用/拒绝；原生Emit不可用公开现场及全模块来源仍未覆盖 |
| V02/V12：Runner回调与容量 | 回调隔离16/16；完整方法内部筛选优化47/47、真实UTF筛选1/1、三叶3/3；原5001运行终态通过及磁盘/分页核验 | 5001/5001执行与清理成功；full超限、事件淘汰后重复完成、取消+Reload清理成功及版本矩阵仍未完成；不销毁归属未知Runner对象 |
| V09/D18：Snapshot取消及产物 | 修复最后异步目标取消后误报成功；allOrNothing/bestEffort与不继续后续目标，PNG/manifest实际大小/哈希 | Unity6/6，3组取消产物核验；公开MCP捕获中取消、GameView、捕获中Reload及迟到回调组合仍开放 |

### 三、其他已执行但不作为新TODO完成数

- 历史质量契约补回与审计：Python两模块43通过；该时点默认质量门禁contracts414通过、Skill/工具清单通过。默认跳过及unknown维持原口径；该证据早于当天后续修改，不证明最终工作区全量通过，更不是发布验收。
- Runner筛选成本定位、被否决的regex/fixture候选和后续完整方法验证保留在前文；它们是V12过程证据，不另计为多个已完成TODO。早先真实容量超时、取消Reload的aborted与错误选择范围也未改写为成功。
- N05 Tooltip：按用户决定维持P5、暂缓、未完成。未取得可见文字/真实原生像素，既不是截图通过，也不能判定为枚举实现缺陷；不再轮询、悬停或创建提示。
- Server全模块加载来源仍unverified，尤其D03最后修改未刷新；工具说明和个别在线行为已核对不等于全进程代码加载证明。无授权的维护/发布/外部工程及其他尚未执行项保持原状态。

### 四、本次状态核对依据

- `Log/Stage4/todo-closeout-audit-20261008.json`：重新读取窗口/菜单/Eval/Runner定向终态，记录13份既有专项核验报告的哈希；对5001运行另外核对实际结果、选择集合、原文件/副本、10页事件、空页、gap/mismatch、重复事件和超限失败。
- `Log/Stage4/audit_todo_closeout_20261008.py`：可重复的只读核验；没有把“JSON报告可读”升级成其所有历史原始产物已重新验真，也没有运行全量测试。
- 根`TODO_UPilot.mcd`：更正V12“仍待终态”的过期描述；对上述开放项明确标记部分完成，保留两项新确认的V12缺口与原剩余矩阵。历史整合入口没有活动项，无需重复写入；不读取或改写TODO_Distill及外部项目清单。


## 2026-10-08 V12去重与完整结果传输续验

### 交付与最小改动

- `Editor/Core/UPilotTestService.cs`：叶子回调/最终对账优先依据完整results表判断既有完成，保留仅事件旧记录的兼容分支；更新元数据不重复发完成事件，真实状态改变只发leaf_corrected，test_started不算完成。没有新增索引、存储或生命周期。
- 同文件仅在私有`test.results`增加`acceptCompressed`协商；非增量完整JSON达到2MiB且不超过64MiB时，使用内置GZip+Base64；只在有压缩收益时返回传输载体。公开API仍是原完整结果，增量游标分支不变，原内存对象/历史文件不变。
- `upilotserver~/src/upilot_mcp/domain/test_service.py`：只为无cursor查询发起协商，一次派发；严格验证编码、声明大小、gzip完整性/尾随字节、UTF-8、JSON结构和runGuid，解码上限64MiB。无协商旧Bridge保持原结构；损坏载体返回TEST_RESULT_TRANSFER_INVALID，保留context/timing，不降级重放。Bridge仍4MiB；不可压缩或超解码预算仍明确失败，不声称无限结果容量。
- 测试：扩展`UPilotTestSelectionTests.cs`；新增`test_test_result_transfer.py`，仅更新`test_v02_contracts.py`两个完整结果转发载荷预期。无第三方依赖、通用传输框架或配置开关。

### 编译和定向测试

1. 去重红灯：run `25ecbeab-1f54-4a66-b1e1-5afb5b30b9bc`，4/4按预期失败；修复后run `07917590-3a49-457b-b908-0f14ee55c2b0`，51/51通过、权威终态、cleanupSucceeded。原初次选择快照不匹配的拒绝未启动Runner，也保留记录。
2. 压缩首批实际编译发现CS0104（UnityEngine与System.IO.Compression的CompressionLevel同名），未冒充通过；仅限定完整类型名后登记新批次。
3. 最新批次`wb-de5afdd2-ac4d-453d-9c9c-d1dacd78809e`，request `req-b7d79e5f-71b9-46e9-a31c-2cef5074004b`，compile operation `0cf28a42f02441aa8dc467bd205abf75`；created=1791451629175，verified=1791451656815，关联completed、terminal/errorsVerified=true，0错误、3条既有警告。未另触发编译。
4. 精确fixture `CodingRiver.UPilot.Tests.UPilotTestSelectionTests`，预览54项，run `a212e09f-77eb-4b5e-95a1-37008247a6ca`，54/54、权威终态、cleanupSucceeded；包括单个巨大Unicode结果及5001个构造结果的无损往返，不是重跑5001个测试。编译后结构化Console仍有旧Unexpected assembly reload观察，未清空或归因于新运行。
5. Python：传输+精确既有转发+Console evidence直接回归73/73；传输+P2契约+Runner恢复最终53/53；cursor/增量/结果兼容筛选10/10（109 deselected）。各组有重叠，不相加；2条既有websockets弃用警告。未执行全量EditMode或全量Python。
6. 扩大回归原先53项有2失败：一个新增public wrapper测试误解CallToolResult已修正；另一个旧取消持久化测试期望RecoveryRequired，但现行有限任务协议为aborted。仅修改该旧测试，强化terminal、原runGuid、未声称cleanup成功、一次写入、未启动observer/派发及重复取消not_found且不变的断言；未改产品生命周期或削弱身份校验。旧失败XML保留，修正后53/53。

### 真实公开链路与维护

- 维护前核验独立`aiServiceMaintenance.effectiveApproved=true`、精确规范工程、Runner已完成、Capture=0；persisted/serverMemory/Bridge已观测活动均0。队列complete=false的唯一历史超限事件缺口原样存档，不能称维护前完整空闲。
- 为加载本批Server解码改动，仅提交一次维护`7110609b-cdec-402f-a97a-e6d9e481cf51`。随后查询同一身份确认succeeded、queueReset=true、healthVerified/readOnlyVerified=true；Server36408→50628，Bridge→8f82a60a200748229ea8fc5d26c4ed32。Unity PID23796、domain23及compile身份不变；未重启Unity、未切换PlayMode、未再次编译或重放业务。此刷新不是为消除旧诊断；新生命周期空活动不反证旧队列完整。
- 通过实际`unity_test_results(runGuid=983f2386-4053-4802-8e18-55aa08e6896a)`无cursor读取成功。独立对比原持久文件全部字段，5001结果、10000保留事件、5001选择身份完全一致；仅额外增加已有Server consoleEvidence。原文件6525677bytes，SHA256 `f76d0a24e620e1cf3da6f179ef65cd8f0baa43e279d041067fe6f97bd22741ab`，隔离副本与原文件字节相同。旧BRIDGE_RESPONSE_TOO_LARGE记录、旧重复事件均保留，没有修饰历史。
- 旧大运行cursor=begin仍明确TEST_RESULT_CURSOR_GAP（已淘汰早期事件）；新54项运行两次count=1分别只返回sequence1、2且results=[]，随后无cursor仍取得全部结果/事件，与先前终态相同。不是将gap误记分页通过。
- 最后Capture activeCount/returnedCount=0，新生命周期队列complete=true、items=[]。V12公开编解码路径行为已验证；未捕获实际wire字节，不将离线压缩估算当线上字节证据；Server全模块加载来源仍unverified，不把新PID或单路由成功当全模块鉴证。D03等其他改动虽随服务刷新，独立在线行为/来源验证仍开放。

### 状态与证据

- **本轮完成两个V12缺口**：事件淘汰后去重；原5001完整结果在不扩大Bridge帧上限下可经公开API无损获取。根TODO删除这两项“待修复”描述，保留交付说明与证据引用。
- **V12父项未完成**：取消中Reload清理成功、其他Unity/UTF版本矩阵；当前仅证明已测试的Unity6000.6.0a2环境。N05 Tooltip仍P5暂缓，其余独立矩阵不替代、不自动结项。
- `Log/Stage4/verify_v12_closeout_20261008.py`与`v12-closeout-verification-20261008.json`：只读复核红/绿/最新运行、批次关联/时间/当前源哈希、维护身份、全部历史字段/原文件哈希、分页和测试XML。验证脚本不是生产代码，不读写原运行。
- 原始证据：`v12-dedup-*`、`v12-transfer-*`及原`runner-opt-capacity-*`；所有路径位于规范工程`Log/Stage4`。未更新外部项目、未读取历史蒸馏建议、未提交或发布。

## 2026-10-08 Runner Reload恢复契约与终态矩阵续验

### 结论与改动范围

- 只扩展 `Tests/Editor/UPilotRunnerRecoveryTests.cs` 的既有恢复测试；未修改产品生命周期、所有权规则、持久化或取消实现，也未创建新通用 Operation/Step。
- 将既有持久释放恢复测试扩为 completed/failed/cancelled 三组，并经 JsonUtility 序列化往返构造恢复状态；验证完整结果、status 摘要和 cursor 增量投影按值保留业务终态、释放证明及原截止时间。其中 cancelled 是构造的序列化兼容值，不是当前公开取消协议输出（aborted）；该轮未覆盖规范取消值，后续22项及真实现场补证见下节。
- 增加四组结果不可信分支：pending/unknown（即使 authoritative 标志为 true）以及 completed/cancelled 但 authoritative=false。资源已释放也不能推导业务成功，原结果保持，最终 aborted/cleanupSucceeded=false。
- 两组测试均检查重复取消、迟到 RunFinished、重复清理及带旧 active 指针的再次读取：终态 JSON 不变、只保存一次、不续期、不创建/销毁替代 API、不重新安排清理。它们使用隔离 service 和受控 Runner 探针，**不是实际 Domain Reload 清理成功现场**。

### 关联编译与定向验收

- 编译前精确核验规范工程 `D:\upilot\Tests~\UPilotTest`；队列 complete=true/items=[]、Capture activeCount=returnedCount=0、Runner status=none；observe 模式 readiness=true，没有切换模式或聚焦。
- 仅登记一次批次 `wb-1c2b8e67-94c8-4f9e-aff4-27283d0f829d`，compile request `req-e15af136-dc78-4907-8f2f-b1e51c02128c`，compile operation `139ded4ea20c43cabdc237d9126b1857`。created=1791453239332，verified=1791453252928；原批次 verified/terminal/errorsVerified/correlationVerified，结构化错误0、警告0。正常编译 Domain23→24，不将它当作运行中 Reload 的验收。
- 初次按方法名预览未命中参数化叶子（selectionValid=false，未启动）；随后只读发现精确带参数 fullName，重新预览19项并绑定 domain/snapshot 启动，未放宽 requireAllSelectorsMatch 或扩大到整个 fixture。
- run `79922746-6e6b-4bcb-992e-4af5f689c6db`：**19/19通过**，其中7组上述恢复状态、12项直接回归；authoritative/terminal/cleanupSucceeded=true，unresolvedResources=[]。没有全量 EditMode、没有重跑5001容量测试。
- 真实公开 status/full/cursor 的运行身份、计数和终态一致，cursor count=1 仅一条事件且 results=[]；磁盘原结果各字段与完整API一致。测试来源文件SHA256与该WriteBatch登记值一致。
- 运行范围结构化Console错误查询 logs=[]；阶段 evidence 仍为 partial/console_cursor_clear_or_reload_not_detectable，不把空查询升级为完整分阶段日志覆盖。收尾队列 complete=true/items=[]，Capture0；未清空Console、未停止他人任务、未重启服务。

### 原取消Reload现场的归类，不重跑或改写

- 只读复核原 run `cfeec787-3353-42d3-b5a1-c5365b86870d` 的 `Log/P0P1/CancelReload/<runGuid>/cancel-at-reload.json` 与当日保存的晚查询：两次取消调用只产生一次 cancelAttempt，Reload前 cleanupResourcesReleased=false、resultAuthoritative=false；恢复后 terminal=true/status=aborted/cleanupSucceeded=false，未释放资源清单仍保留。
- endedAt=1791429775183，早于原 cleanupAttemptDeadlineAt=1791429800283；该现场不是耗尽原预算才结束。结合原回执原因和当前 RecoverLoadedRun 的所有权保护，属于**无持久释放证据时按契约安全终止**，不是可以靠补 terminal=true 或创建替代 API 修好的清理成功分支。
- 未重跑此场景、未改写旧记录、未销毁归属未知的原生 Runner；原业务结果仍不权威，底层资源是否完全释放不作成功宣称。

### TODO与证据边界

- V12父项继续开放，已补序列化恢复矩阵，不重复列为缺少该契约测试。真实“取消结果及原资源释放已持久化后跨Reload成功收尾”与其他Unity/UTF版本仍未覆盖；V02真实资源释放矩阵、Server全模块加载鉴证亦未因此关闭。Tooltip/N05保持P5暂缓。
- 原始回执：规范工程 `Log/Stage4/runner-reload-audit-*`、`runner-reload-matrix-*`；只读复核脚本 `verify_runner_reload_matrix_20261008.py` 输出 `runner-reload-matrix-verification-20261008.json`，verified=true，包含批次/时间/源码哈希、19项身份与结果、公开投影、原文件字段、旧现场边界及各证据哈希。
- 本批未提交、发布或修改外部工程；无需Python回归（未改Python产品或测试）。


## 2026-10-08 V12结果回调边界取消与真实Reload收尾

### 最小改动与证据边界

- 仅修改 `Tests/Editor/UPilotRunnerRecoveryTests.cs`：复用 `UPilotResultReloadProbe`，新增 Explicit 单叶 `CancelAtResultBoundaryBeforeReload`；精确规范/已授权2022工程路径限制不变，实际本轮只选择规范工程。通过公开UTF回调priority+100在UPilot结果回调前调用一次原runGuid的官方取消，priority−100观察结果及原资源释放落盘后请求Reload。不Hook或修改UTF内部任务，不改产品生命周期，不为恢复创建替代API/回调。
- 新增标准取消业务终态 `aborted` 的序列化恢复用例；保留 `cancelled` 作为旧构造值保留用例，并更正上节容易误读的描述。新增2个拒绝用例，非本次runGuid/非专用测试不得注册观察器或改变当前运行。
- 明确本项是**测试体已结束、UPilot最终结果回调之前的取消**，不是测试执行中途取消。单叶断言Passed与运行业务终态aborted可以同时成立；清理成功不将取消改成业务成功。

### 首轮观察与夹具修正，不改写旧证据

- 第一批 `wb-cc13a373-efba-4cdf-b317-fd58c3c1f505` 关联编译verified、0错误/0警告；回归run `253bd2d7-91ed-44ea-b60f-3b8a1763c0fd` 22/22、权威终态、清理成功。
- 第一现场run `31929430-c0ac-4be5-aebe-4638cc4f7597` 已观察到非终态持久释放后Domain25→26、恢复后aborted且cleanupSucceeded。证据审计发现新增cancelBefore/cancelAfter直接引用服务可变对象，后续见证文件中的这些字段被结果回调更新，不能作为取消前/后的冻结快照。原始四份见证和公开回执全部保留，不改写为正确记录。
- 仅将两个字段改为JsonUtility深复制后登记第二批；不是因观察超时或失败而盲重放原运行。原运行已权威终态、队列和Capture已核对，后续使用新的自有runGuid验收见证修正。

### 最终关联编译与定向回归

- 最终批次 `wb-748b2792-e7ed-473f-866e-ab0a357ee141`；compileRequestId=`req-0d8ecf65-04d1-42a5-8444-e1ad91fc747b`，compileOperationId=`d893b7753b5c400da62652a76c95e1f9`；created=1791454621817，verified=1791454635792。verified/terminal/errorsVerified/correlationVerified，结构化错误0、警告0。
- 最终测试文件SHA256=`441dabb2612a1e1e8fed7c4b4c3e07412bc1040baadb26526d1a7219c36516ea`，与本批changes.contentSha256一致。
- 精确22项预览后绑定selectionDomain/snapshot运行；run `0cb821e6-211b-4dac-b6c6-2a7d8b5cccb5`，22/22、authoritative/terminal/cleanupSucceeded=true，unresolvedResources=[]。不与第一批或此前19项相加，不代表全量回归。

### 真实取消/Reload现场

- 规范工程 `D:\upilot\Tests~\UPilotTest`；Unity6000.6.0a2，安装UTF1.7.0。唯一Explicit测试run `f9994c74-27c2-4289-a7de-c7884b762f93`。
- 冻结取消前：running、resultAuthoritative=false、cancelRequested=false、cancelAttemptCount=0、无释放证明/清理期限。官方取消后：stopping、cancelAccepted=true、cancelAttemptCount=1，尚无权威最终结果及释放证明。
- 结果回调后及真正beforeAssemblyReload：cleanup、terminal=false、outcomeStatus=aborted、resultAuthoritative=true、cleanupResourcesReleased=true、cleanupPending=true、cleanupSucceeded=false。cancelBefore/cancelAfter在cancel-observed、result-observed、before-reload三份见证中逐字段一致，不再被后续回调改变。
- 使用原serviceLifecycleId，Unity主进程PID/创建时间及producerEpoch不变，Bridge session改变，Domain27→28；不是编译导致的Reload替代证据，也不是服务重启。
- 恢复后的status/full/cursor均为原runGuid、aborted、terminal=true、resultAuthoritative=true、cleanupSucceeded=true、cleanupResourcesReleased=true，unresolvedResources=[]。原cleanupAttemptDeadlineAt=1791454763470保持，endedAt=1791454747200，未续期，结束时剩余16270ms。
- 终态后两次公开取消均为 `not_found/changed=false`，不再次调用Runner；随后完整读取和磁盘原结果逐字段一致。结果文件前后字节不变，SHA256=`4d151ac957c65fb3ff91ea9f8635935d2b39b68a5db8dee9d247e071c123ec4f`。cursor count=1返回1事件、results=[]；无cursor返回完整1叶结果。
- 原Domain内重复结果回调2次及外来run/domain回调2次未改变已保存结果，两个自有观察器已注销；不将这部分宣称为新Domain内实际迟到回调矩阵。
- 两轮均保留Reload期间 `UPILOT_RECONNECT_PENDING` 原回执；只重查同一运行，无重启或重放start。最终队列complete=true/items=[]、Capture activeCount=returnedCount=0，Editor ready且权威新鲜，前后场景一致。运行范围Console错误查询为空，但覆盖仍partial/domain_reload_boundary，不宣称跨Reload日志完整。

### 归档与剩余

- 独立只读核验 `Log/Stage4/verify_runner_result_boundary_20261008.py` 校验31份输入、编译关联/源码哈希、精确选择/22项结果、实际Reload边界、原取消/期限/释放证明、公开三种投影、终态不变、场景/队列/Capture，输出 `runner-result-boundary-verification-20261008.json`，verified=true。
- 原始证据：`Log/Stage4/runner-result-boundary-*`、`runner-result-boundary-v2-*`；实际边界见证在 `Log/P0P1/ResultReload/<上述各自runGuid>/`。核验脚本仅读取现场和原产物，输出独立报告，不改变运行文件。
- 当前版本该子项从根TODO的V12剩余关闭条件移除。V12父项仍保留其他Unity/UTF版本矩阵及全Server模块加载来源鉴证；V02原生资源矩阵不因持久释放证明而关闭；旧无释放证明的取消Reload仍是安全aborted，不重写为成功。Tooltip/N05保持P5暂缓。
- 本轮无产品C#/Server改动、无Python产品测试需求、无全量EditMode、无容量重跑、无外部工程、无聚焦/模式切换、无未知Runner销毁、无通用Operation启动、无服务重启/提交/发布。每次测试C#保存各登记一次WriteBatch，无无代码变化的重复编译。


## 2026-10-08 D18公开SceneView非等待身份与捕获中取消

### 现场问题与最小修复

- 真实HTTP基线 `waitMs=0` 返回Task `task-6888d031-4387-4a60-bdfc-f0b4ed3ebd55`，到完成后才得到Snapshot `snapshot-604371700e1b4896ae17e0a23f47784e`；调用方无法及时取得取消所需身份。基线实际completed/success=true，没有发送取消，不能归类为取消被拒绝或截图失败。
- 仅修改 `upilotserver~/src/upilot_mcp/domain/snapshot_service.py` 的现有SceneView Task包装条件：增加 `wait_ms > 0`。非等待复用原 `snapshot.start` 回执；正等待路径、Bridge、持久化及像素行为不改。未新增接口、依赖、配置、定时器或C#代码。
- `upilotserver~/tests/test_snapshot_tools.py` 新增8组：native/proxy × waitMs=0/-1 × allOrNothing/bestEffort，经过真实公开工具模型与服务层，dispatcher为隔离桩。断言无Task包装、只发送一次start、原身份用于cancel/status、选择器保留及原回执不变。先8组红灯，修复后与 `test_snapshot_short_tasks.py` 合计45/45通过，两个既有websockets弃用警告；正等待Task/失败业务终态/完成解包回归保留。不是实机矩阵的替代。

### 一次受控部署刷新

- 规范工程独立aiServiceMaintenance有效；维护前queue complete=true/items=[]、Capture零、Runner已终态、无待编译批次，Operation查询排除自身后的活动数为零。基于具体已复现修复，而非仅因缺少全模块加载鉴证重启。
- 唯一维护ID `a4aa4399-77cb-4d7e-9add-fe2f268f5d62`，status=succeeded、queueReset=true、healthVerified/readOnlyVerified=true；接受1791456193254，结束1791456212287，未超过原120秒截止。断连观察错误原样保存，只查询原维护，不重发。
- Server PID50628→57504；Bridge session变为 `b1712eaff27b4a67a8b3d94e5b16e567`；Unity PID23796、producerEpoch及Domain28不变。实际只读窗口查询成功。该结果和新行为证明此次相关分支在线生效，不等于全Server模块加载来源鉴证。

### 真实公开链与产物

- 使用真实Streamable HTTP持久ClientSession、既有精确SceneView身份；不创建窗口、不聚焦/移动鼠标、不切换模式。每个现场只启动一次自有Snapshot，独立requestKey/目录；原任务终态后才进行下一独立场景。
- 排队取消：`snapshot-3fc2521db0ff4585b5b720c66fe99429`，start立即返回snapshotId且无taskId；cancel回执queued/terminal=false/cancelRequested=true；终态cancelled/success=false/persistenceVerified=true，无PNG。manifest 2760字节，SHA256=`dc6bb9916573a43b4ea0f7eb94d4ff4d1e3c72d48d5c1e3c768a31ff4a8ffd84`。
- 捕获中取消：`snapshot-bdf67d71f01c4fdb8fbdaa147de47784`；先公开status观察rendering/terminal=false/cancelRequested=false，再按原ID取消。cancel在1791456333305返回rendering/terminal=false/cancelRequested=true，终态1791456333426为cancelled/success=false；不是终态后才到达的取消。
- 捕获中产生原生1008×261 PNG，153093字节，SHA256=`938ecf376fc2f3525526209e799ec46929ef12c186b0e42bc4b21f94dcfb4657`；来源匹配精确SceneView、includesSceneGui/Handles=true，但产物acceptedAsEvidence=false。单目标capture成功与作业取消失败分开表达；不把来源验证当作内容/裁切视觉验收，也不承诺按请求320×180缩放。
- 捕获中取消manifest 9166字节，SHA256=`3113df59bc2cc9568f7ecada3956ca3d53c1460c48d3c4325d43c8aa267265c4`，sequence=6。两个取消现场的status/collect、重复取消回执、再次status一致；重复取消前后manifest/PNG原字节哈希不变。
- 原成功基线和两个取消现场的实际manifest、PNG及外部 `Library/UPilot/SnapshotJobs/<id>.json` 均重新核对。manifest内部pending/自身哈希为空仍符合原协议，不回写历史。公共持久化验证来自外部state，不能由捕获成功推断。
- 两个现场前后场景清单一致，queue complete=true/items=[]、Capture activeCount=returnedCount=0，Editor权威新鲜EditMode/ready；无测试/捕获资源遗留。

### 证据与剩余

- 独立核验 `Log/Stage4/verify_snapshot_public_cancel_20261008.py` 读取51份证据（含产物与源文件哈希），输出 `snapshot-public-cancel-verification-20261008.json`，verified=true。原始材料位于 `snapshot-public-cancel-before-20261008/`、`snapshot-public-cancel-after-20261008/`、`snapshot-public-render-cancel-20261008/`、`snapshot-cancel-refresh-20261008/`；Python红绿文本/XML同前缀保存。
- 根TODO D18移除当前SceneView公开捕获中取消缺口，父项继续开放：GameView现场、真正捕获中Reload/取消、外部终态后迟到回调组合未覆盖。bestEffort为隔离契约覆盖，不冒充真实现场；全Server模块加载鉴证仍unverified。Tooltip/N05保持P5暂缓。
- 未改C#/程序集，因此未登记WriteBatch、未编译、未运行EditMode；未启动通用Operation、未操作其他Capture、未修改外部工程、未提交/发布。产品及测试既有无关修改保留。
- 最终检查将本批测试新增行的CRLF统一为仓库LF（另保留原字节副本），无测试语义或产品源文件变化；定向45/45再次通过，最终XML为snapshot-public-cancel-final-20261008.xml。核验同时保留刷新时与当前测试哈希并逐字节验证仅换行变化；产品源码刷新前后哈希一致。根TODO和两个Python文件git diff --check通过。


## 2026-10-08 D18外部终态后SceneView迟到回调冻结

### 已确认的问题与最小改动

- 仅在规范工程 `D:\upilot\Tests~\UPilotTest` 验证。基线Unity PID23796、Domain28、Server PID57504，权威新鲜EditMode/ready；完整队列为空、Capture为零。未触发服务重启，未切换外部工程或PlayMode。
- 源码已有 `FinishJob` / `UpdateJob` 的terminal保护，但异步截图返回后先写PNG、追加targets/artifacts，异常路径也先追加failures。`ResetActive` 在等待期间产生终态后，这些内存和磁盘副作用仍可发生；不能仅看顶层status未回滚就宣称终态不可变。
- 最小可观察性改动：私有 `ExecuteJob` 从async void改为async Task，现有update调度点显式丢弃返回Task；公开接口、调度时机和持久化协议不变。测试通过反射保留该原Task并等待其完成，不依赖固定等待几帧来猜测回调已结束。
- 红灯阶段仍保留原缺陷：自有SceneView、独立 `new UPilotSnapshotService`；真正进入rendering并有未完成Task后，仅对该隔离service调用ResetActive。覆盖allOrNothing/bestEffort × 保留窗口/捕获中关闭自有窗口，不操作Bridge注册的全局service或其他任务。
- 已复现：保留窗口的两组在aborted后各写两份真实600×424 PNG、继续捕获第二目标，且错误保留acceptedAsEvidence=true；关闭窗口的两组分别追加1/2个失败和目标。磁盘manifest/state仍为原终态，内存对象却已变化，故这些PNG不属于有效验收产物。原始红灯文件完整保留，不修饰历史。
- 修复只在SceneView/GameView异步完成后的发布前、ExecuteJob等待后的帧/后继处理前，以及两个异常入口检查terminal。迟到工作结束但不得再发布或落盘；cancelRequested语义未改，普通取消仍可保留acceptedAsEvidence=false的诊断PNG。未增加配置、hook、任务框架或receipt结构。

### 关联编译和定向测试

| 阶段 | WriteBatch / 编译操作 | 结果 |
|---|---|---|
| 红灯测试与Task返回值 | `wb-b4fbbba5-d31c-4319-82e9-2911de8997fe` / `cd27a27d6af6492e9401d4174a71e603` | verified、terminal、errorsVerified、correlationVerified；0错误，3条既有Flow字典序列化警告 |
| 终态检查修复 | `wb-b4d35b09-d6d3-4ce8-aba5-ae280b61d81e` / `6b9edddcc0b04a129cdc7e435a41b9f4` | verified且同批次关联，0错误/0警告 |

- 两批均保存前基线、保存后立即登记一次WriteBatch；只观察原批次，不重复触发编译。verified时间分别1791457238959/1791457368371，均晚于对应登记时间1791457216536/1791457353350。正常编译Reload使Domain28→29→30，不等同捕获中Reload验证。
- 红灯run `4cea8ea3-1b87-4fd3-a42c-70368a035ff5`：4/4失败，均命中“迟到完成/失败不得追加目标”断言，而非环境拒绝或编译失败；resultAuthoritative=true、terminal=true、cleanupSucceeded=true、cleanupResourcesReleased=true、unresolvedResources=[]。
- 绿灯run `ffa7a038-8cd7-4add-aa12-5bdb5820095d`：11/11通过，包含新增4组、已有3组取消、3项持久化终态恢复和1项正常SceneView截图；权威终态及清理字段均确认。
- 新增测试核对原Task真正RanToCompletion、第二目标未执行、targets/artifacts/failures未追加、无PNG、整个job JSON字节不变、manifest/state字节不变、隔离service无任务重新插入。仅关闭自有窗口并清理自有state记录；`Log/Stage4/SnapshotLateCompletion/<guid>/`保留前后state副本、job、manifest和执行结束记录。
- 结构化编译错误为零；相关Console查询无返回Error，但范围均保留partial及cursor/Reload缺口，不声称捕获了所有Console日志。未清空用户Console。

### 独立磁盘核验与源码证据

- `Log/Stage4/verify_snapshot_late_20261008.py` 重新读取8组红绿现场，按Runner起止时间关联，不混入其他任务。核对4组策略/关闭组合、原Task状态、外部回执的manifest大小和SHA256、前后state/manifest字节、所有红灯PNG的真实大小/哈希/PNG头尺寸及repaint来源；绿灯目录没有PNG。输出 `snapshot-late-verification-20261008.json`：verified=true，74份输入哈希。
- 最终产品源码SHA256 `c3d7903c2f510e21b2f572fd250f6f5d85a62273a53417f073f4cd07a85b1026`；测试源码 `96245ebda0f9c07da3cd4ee7351ed9214438b36dc349d8020988f84be0650f49`，与登记哈希一致。红绿测试源码完全相同。
- 同时保存 `snapshot-late-red-service-20261008.cs.txt`、`snapshot-late-green-service-20261008.cs.txt`、`snapshot-late-tests-20261008.cs.txt`。红灯源码由仅逆转本次5处终态检查重建，SHA256 `07a119ee69c8335ff3e0ce50e60db85f925df8b1ef472159dfe7e77c9d53346d`与红灯登记一致；不是用当前绿灯源码替代红灯来源。
- 最后权威新鲜EditMode/ready、activeScene仍为upilot-acceptance；queue complete=true/items=[]、Capture activeCount=returnedCount=0。

### 未覆盖与TODO状态

- D18新增“隔离ResetActive + 真实SceneView迟到成功/失败”子项已完成；父项保持开放。GameView虽补相同异步发布边界检查，本轮未运行PlayMode现场；真正捕获中Reload/取消、实际服务终止及其迟到回调仍待受控验证。
- 不将本轮隔离service断言等同公开MCP服务硬停止，不将编译Reload等同截图中断Reload，不外推到其他Unity/UTF版本或完整Server加载鉴证。Tooltip/N05仍为P5暂缓。
- 未运行全量EditMode、未使用外部编译器、未启动通用Operation/Step、未修改外部工程、未提交/发布。仅改Snapshot service、对应fixture及两份记录，保留既有无关修改。


## 2026-10-08 D18 GameView 生命周期定向续验

### 范围与实现

- 仅扩展 `Tests/Editor/UPilotSnapshotTests.cs` 七项 UnityTest 与局部辅助方法。`Editor/Core/UPilotSnapshotService.cs` 保持上一批 SceneView 终态保护后的字节不变，SHA256 为 `c3d7903c2f510e21b2f572fd250f6f5d85a62273a53417f073f4cd07a85b1026`；未更改公开协议、像素方向/尺寸或持久化。
- 使用规范工程 `D:\upilot\Tests~\UPilotTest`，Unity 6000.6.0a2 / 已安装 UTF 1.7.0。每项通过 UTF EnterPlayMode/ExitPlayMode，自建红色 Camera；只操作独立 Snapshot service 和本测试同步创建的精确 GameViewCaptureRunner，不操作全局 Snapshot service 或其他 Capture。
- 实际进入 rendering 且原 ExecuteJob Task/捕获 Task 尚未完成后再取消/ResetActive。通过原始 capture Task 独立证明帧读回已完成，或仅销毁自有 runner 产生 OperationCanceledException；等待原执行 Task 和精确 runner 销毁，不靠固定帧数猜测。
- 失败时先清理自有对象，再经 ExitPlayMode 返回 EditMode，保留原断言错误。不新增框架、配置或依赖。

### 原始失败与最小修正

1. 原登记 `wb-d59a6011-b625-41a7-a185-fa6e10f6088b`，编译 `dffb5056b9ff43db9d5dbaf075d3f241`，权威失败1错误：新增测试调用 GetInstanceID 在 Unity 6000.6 已禁用。只改为现有 UPilotEntityIds.ToWireId。
2. 修正登记 `wb-461a4724-1e3a-4660-bc68-46a745e1fcd9`，编译 `08d9b0a794644905977d7b12beba3545`，关联verified，0错误/0警告。
3. 首轮 run `8699a7c0-107c-4f23-9adb-43b98efa8644` 权威终态：2通过/5失败，cleanupSucceeded/resourcesReleased=true。五项均因夹具请求两个GameView被既有“A Snapshot may contain at most one true GameView target”拒绝，未进入预期捕获点；不是产品生命周期缺陷。
4. 后继目标改为自建 Camera，保留取消/终态不得执行后继的断言；未放宽单GameView限制。登记 `wb-7835f2bc-fedd-4422-aa85-3a95de135696`，编译 `971d413e946b411fa1bba62132f4b91a`，0错误/0警告，terminal/errorsVerified/correlationVerified=true，验证时间1791458657808晚于登记1791458638361。
5. 三次源码、原编译错误、两次运行及暂时重连查询结果均存档。测试中的正常Reload遇到UPILOT_RECONNECT_PENDING/COMMAND_RECOVERY_REQUIRED时只重新查询原run，未重启或重复启动测试。

### 七项定向结果

run `1c5b27d7-a6d9-45bd-bf68-96fb20b53adc`：**7/7 passed**，resultAuthoritative=true、terminal=true、cleanupSucceeded=true、cleanupResourcesReleased=true、unresolvedResources=[]。

| 组合 | 数量 | 已核验结果 |
|---|---:|---|
| allOrNothing/bestEffort，最后一个GameView捕获期间取消 | 2 | cancelled/success=false；保留原始诊断PNG，acceptedAsEvidence=false；重复取消不改job/state/manifest |
| allOrNothing，GameView取消后还有Camera目标 | 1 | 同上且不捕获后继Camera |
| allOrNothing/bestEffort，隔离ResetActive后原帧读回成功 | 2 | 原捕获确实成功，但aborted终态job/state/manifest冻结，无产品PNG、目标或失败追加，不捕获后继 |
| allOrNothing/bestEffort，ResetActive后销毁自有runner | 2 | 原捕获Task以OperationCanceledException结束；外层Task正常结束，终态及文件冻结，无后继/产物 |

- 七项均验证精确自有runner销毁、Application.runInBackground恢复原值。三份取消PNG均为320×180，独立读图中心为红色(255,0,0)；公开回执大小/哈希与实际字节一致。
- 两份成功但已aborted的原始读回PNG由测试保存在作业目录外的 `witness-readback.png`，仅证明底层捕获真实完成；**不属于产品产物，也不代表终态后产品仍写图**。四个aborted作业的snapshot目录均无PNG。
- 纯红画面只验证真实Camera合成和读回，不证明上下方向、完整构图或裁切正确；不替代先前非对称画面验收。

### 独立核验与收尾

- `Log/Stage4/verify_snapshot_game_lifecycle_20261008.py` 核对三次关联编译/源码哈希、首轮原始失败和最终权威结果、按run时间匹配九组实际捕获目录（首轮两组＋最终七组）、策略/动作组合、PNG解码与中心像素、实际字节/哈希、完整job/state/manifest冻结及后继未执行。
- 输出 `snapshot-game-lifecycle-verification-20261008.json`：verified=true、97份输入哈希。源文件分别归档为 `snapshot-game-lifecycle-tests-compile-red-20261008.cs.txt`、`snapshot-game-lifecycle-tests-run-red-20261008.cs.txt`、`snapshot-game-lifecycle-tests-green-20261008.cs.txt`、`snapshot-game-lifecycle-service-20261008.cs.txt`。
- 核验初稿把“清理后Library state永久不存在”当作要求，实际检查发现后续普通PlayMode Reload会从保留Log manifest重建终态历史。核对生产LoadPersistedJobs/ReadPersistedManifests及每字段差异后改为核验：若重建，则除persistenceRecovered=true外与原终态完全一致。这是既有恢复语义，不是活动任务复活；不删除历史或将存在终态文件误报资源泄漏。
- 最后精确项目身份、connected/serverReady、权威新鲜EditMode/ready成立，Domain46，场景upilot-acceptance；queue complete=true/items=[]，Capture activeCount=returnedCount=0。结构化编译错误为零；运行Console范围为partial，含Reload/游标缺口，不声称所有Console日志完整无错；未清空Console。

### TODO与未覆盖

- D18“真实GameView帧＋隔离service取消/ResetActive迟到成功失败”子项完成，父项保持开放。仍缺GameView公开MCP Snapshot取消链、真正捕获中Reload与取消组合、实际服务终止及迟到回调；两次运行周围的普通Enter/ExitPlayMode Reload不能作为捕获中Reload证明。
- 不外推其他Unity/UTF版本、完整Server加载来源或实际服务硬停止。仅这七项定向测试，不跑全量EditMode，不使用外部编译器，不启动通用Operation/Step，不改外部工程，不提交/发布。N05/Tooltip继续P5暂缓。


## 2026-10-08 D18公开GameView取消、模式感知Ready修复与独立收尾

### 已实现：仅修复公开流程前置Ready误拒绝

- 初次公开探针在成功进入自有PlayMode后，`unity_ensure_ready(requiredEditorMode="any", editorModeControl="observe")`错误返回PlayMode阻塞，尚未调用pause或Snapshot。原`compile_wait`以EditMode为要求可以返回blocked，但ensure_ready后续仍硬性要求compileStatus=ready，导致允许PlayMode的模式策略无效。原始failure、回执与仅退出自有PlayMode的恢复记录保留于`Log/Stage4/snapshot-game-public-cancel-20261008/`；不归因为Snapshot失败。
- 最小产品改动仅`upilotserver~/src/upilot_mcp/domain/task_service.py`的27行局部分支：仅当目标模式允许、实际模式匹配，且权威新鲜、compilePhase=completed、terminal/errorsVerified=true、verificationPending=false、0错误、不在编译、无待处理WriteBatch、既有批次相关性已核实，才使模式感知checks通过。原始compileWait和executionState不改，不改变全局编译状态、默认EditMode、等待/唤醒策略，不新增配置/框架/依赖。
- 定向测试扩展既有`test_v02_contracts.py`，实际串联TaskDomainService与CompileDomainService：8组目标模式与实际模式组合、22组缺失/错误/过期/编译中证据拒绝。断言只做两次resource.editorState观察，无模式写入/sync/compile；测试唤醒替换为无操作，不触碰Unity。

### 定向测试通过与源文件对应

- 红灯30项：5失败/25通过，正好对应允许PlayMode却被错误拒绝的5种组合；不删除失败记录。
- 修复后ensure_ready/compile_wait直接定向 **38/38通过**；`test_v02_contracts.py` + `test_editor_execution_state_v2.py` **212/212通过**，仅两条既有websockets弃用警告。以上为前序执行结果，本收尾只独立解析XML及源码归档，未重新执行。
- 保存前后源码与XML：`game-public-readiness-{red,green,regression}-20261008.*`及`game-public-readiness-*-green-20261008.txt`。核验当前task_service.py、test_v02_contracts.py字节与刷新时哈希及通过时归档一致；Snapshot C#源码仍为上一批`c3d7903c2f510e21b2f572fd250f6f5d85a62273a53417f073f4cd07a85b1026`。
- 本批没有C#/程序集改动，没有新WriteBatch或编译；公开现场前后compileOperationId/writeBatchId/lastCompileVerifiedAt未变。此前编译证据仍是`wb-7835f2bc-fedd-4422-aa85-3a95de135696` / `971d413e946b411fa1bba62132f4b91a`，不包装为本批新编译。

### 受控刷新与真实公开链路通过

- 已存在独立aiServiceMaintenance.effectiveApproved=true；精确工程、fresh EditMode ready、完整空队列、0 Capture、无活动Operation/Runner/待编译批次预检后，只发送一次维护。身份`7779d328-d00f-46a9-aa6e-ecd103f34ec0`：succeeded、queueReset/healthVerified/readOnlyVerified=true，accepted1791459902774、ended1791459908712，未超过固定deadline1791460022774。
- Server PID57504→49968，Bridge session更换，Unity PID23796及维护期间Domain47不变。仅刷新Server/Bridge；不重启Unity、不编译、不重放业务。特定新Ready行为随后公开调用通过，但**全Server模块实际加载来源仍unverified**。
- 规范Unity6000.6.0a2/WindowsEditor/Direct3D12/URP，维护后新探针独立目录`Log/Stage4/snapshot-game-public-cancel-fixed-20261008/`。自有PlayMode启动Domain47→48，any/observe在play与pause均通过；公开capture仅调用一次，GameView Display0/color/320×180、allOrNothing、waitMs=0，原始请求省略instanceId，经过实际工具模型/归一化链。
- 原Snapshot ID **snapshot-9418cd2091f940288ef68a2d58069896**，无Task包装：queued/sequence1→rendering/sequence3→取消请求/sequence4→cancelled/sequence6。rendering时cancelRequested由false变true但terminal仍false；恢复自有PlayMode使帧读回收尾，最终terminal=true、success=false、cancelRequested=true、persistenceStatus=verified。
- collect、终态重复cancel、再次status、退出自有PlayMode后的原ID查询，完整job相同；重复cancel前后文件字节哈希相同，不追加目标/产物或回滚终态。

### 独立磁盘证据

| 产物 | 实际大小 | 实际SHA256 | 语义 |
|---|---:|---|---|
| manifest.json | 9238字节 | a4dfea2923c82ff8c6443997091c17274ee408229a9b71a779334460acbccc69 | 对应sequence6取消终态；外部state/API提供最终回执 |
| public-game.9912e647.color.png | 2088字节，320×180 RGBA | 37e40f47422b48c6310eaf93a762b70d9a690d9a0c2550329e17a3dbcd4ec84b | acceptedAsEvidence=false，仅诊断产物，不作成功截图验收 |

- 当前磁盘manifest、PNG及`Library/UPilot/SnapshotJobs/<snapshotId>.json`独立读取；外部state除API计算的persistenceVerified字段外与公开job一致，manifest内部pending/自身hash空符合既有写入顺序，不是持久化失败。
- 像素来源`WaitForEndOfFrame+ScreenCapture.CaptureScreenshotIntoRenderTexture`，pixelSourceVerified=true、occlusionSensitive=false、PID23796/display:0；源900×600，输出320×180，比例不匹配警告存在。contentOrientation/compositionComplete=unverified、internalCrop=unknown，未更改像素或声称方向/裁切已修复。
- 独立PNG解码通过，查看图像为近乎均一棕色背景，不是非对称方向/构图夹具；不将此次查看升级为视觉业务验收。
- 场景前后scene_list相同，原场景文件SHA256前后及本轮复核相同。已保存的最终状态为权威fresh EditMode ready，queue complete=true/items=[]，Capture activeCount=returnedCount=0；这是现场结束时的证据，不声称本轮重新查询了当前在线状态。
- `Log/Stage4/verify_snapshot_game_public_cancel_20261008.py`只读复核原失败、源码/XML、维护身份/期限、公开链路、实际文件/PNG解码、场景/队列/收尾和模式差异；输出`Log/Stage4/snapshot-game-public-cancel-verification-20261008.json`，**91份输入、verified=true**。首次核验仅遇Windows实际`log`目录与回执`Log`字符串大小写差异，改用解析后的路径集合比较，未改产物/期望哈希或放宽目录边界。

### 新发现：D20模式投影不一致，不能隐去

- resume命令confirmed=true、observedState.isPaused=false、changed=true/writeCount=1；其context observedAt=1791459957693。随后forceFresh状态同一响应，editor显示play，但executionState显示pause/isPaused=true/isPlaying=false，并仍authoritative=true/isStale=false。
- 该executionState使用Domain48/sequence17599，observedAt=1791459957384早于resume，snapshotId仍相同。下一sequence17600（observedAt1791459959387）已显示play；后续cleanup-resume只读确认changed=false/writeCount=0。因此已核实投影差异与旧观测时间，不证明Unity再次暂停，更不能称第二次实际解除暂停写入。
- 保存回执已足以建立可行动问题，根TODO新增**D20/P2**；尚未定位生产/接收哪层，未为此改写状态或重复现场。应先做有序快照与实时状态的定向复现，禁止按请求目标合成成功或靠多写一次掩盖。

### 未覆盖与TODO口径

- D18公开GameView **allOrNothing捕获中取消**子项已核验完成；剩余公开bestEffort等未覆盖组合、真正捕获中Reload/取消、实际服务终止与迟到回调，不以普通PlayMode退出或隔离ResetActive替代。
- 全模块来源鉴证、其他Unity/UTF版本、模式投影一致性仍未完成。Tooltip/N05保持P5暂缓；没有新全量EditMode、外部编译、通用Operation启动、外部工程操作或发布。
- 本次收尾只新建本地核验脚本/报告并更新根TODO与本交付记录，未重跑捕获/测试、未编译或重启。D18父项未关闭；新增独立D20后活动父项由46增至**47（15D+11N+18V+3独立）**，增加源于真实新增诊断问题，不是将已完成子项重新打开。


## 2026-10-08 D20模式投影修复与当前版本真实收尾

### 最小改动与关联编译

- Bridge仅补pauseStateChanged订阅/解除与实际Editor状态采样，复用现有PublishExecutionStateAsync；resource.editorState在SendResult前await权威快照持久发布。没有按请求目标伪造模式或新增快照状态机。
- Server保留有序executionState事实：同snapshotId模式冲突不覆盖、不刷新年龄，明确editor-context-conflict/authoritative=false；新有序快照才能恢复权威，旧session/domain资源回执不能降权新事实；legacy兼容保留。pause/resume确认仍要求authoritative/fresh，不追加setter、重试或超时。
- 文件：Editor/Core/UPilotBridge.cs、Editor/Core/UPilotResourceService.cs、Tests/Editor/UPilotWindowInputContractTests.cs、upilotserver~/src/upilot_mcp/state_store.py、domain/status_service.py；对应Python定向回归扩展test_editor_execution_state_v2.py。
- 关联WriteBatch **wb-2d83dd51-a284-413e-8c4f-b286481aaf06**，compileOperationId **ad156695a3b6418f843841c5aa266d87**，request **req-60ffe541-18d1-40f5-802d-63dd78f0e9fb**。createdAt=1791466309244，verifiedAt=1791466331814，terminal/errorsVerified/correlationVerified均true，0错误/3条既有Flow字典序列化警告。结构化Console错误查询logs=[]。

### 定向验收

- Python新增13/13、直接69/69；两份选择重叠，不能合计为82个独立用例。覆盖真实dispatcher资源投影、同序号冲突与年龄、新domain/旧session、较新快照胜过迟到pause、legacy、编译阶段投影及重复resume无写入。
- Unity定向仅7项，runGuid **ea134795-f06c-456d-acdf-911d744cd1ac**，task **task-241632e3-b0b1-429e-be7e-74ebde4de929**；7/7 passed，completed/terminal/resultAuthoritative，cleanupSucceeded/cleanupResourcesReleased=true，unresolvedResources=[]。四个新增参数化case使用隔离Bridge，不写共享状态文件；另三个是持久化/转换来源/Runner退出意图直接回归。
- 验收summary实际598687字节，SHA256 **3bb5017ac9cfa65d2efcc9aa154af35935b61512438eac873c717fbeefa7ca3f**；重新计算与API回执一致，sourceUnchanged/testIdentityVerified/acceptancePassed均true。当前源码五份哈希与该批保存值一致。

### 当前版本真实公开链路与收尾

- 规范工程Unity6000.6.0a2/UTF1.7.0，自有Assets/UPilotAcceptance/upilot-acceptance.unity。PlayMode只启动一次，正常Reload Domain49→50；Unity PID23796、Server PID83556未变化。真实pause一次、resume一次，均confirmed=true/changed=true/writeCount=1。
- 重复resume是确认首次成功后的显式幂等验收，不是unknown结果自动重试；confirmed=true/changed=false/writeCount=0/commandSubmitted=false。forceFresh状态、mode-aware readiness与后继状态均play/isPaused=false，未出现旧pause继续标fresh的原问题。
- 原脚本在暂停时要求requiredEditorMode=play返回ready=true，这是错误预期。当前ensure_ready实现及既有fixture明确play/pause=False；改用any/observe取得pause ready=true，恢复后play/observe ready=true。未改产品语义、未弱化有序状态断言；原live-pause-ready.json及脚本失败保留。
- 自有PlayMode退出一次，最终权威fresh EditMode ready。场景11856字节/SHA256 **6d541804972d148352f5e00ff7f478720e7e561aada52a9d5bce13392c949d10**，前后相同；一场景三根十组件、无missing scripts，isDirty=false。queue complete=true/isStale=false/items=[]，Capture activeCount=returnedCount=0。原定向7/7结果仍terminal、无资源遗留；编译身份/verifiedAt没有变化。
- 独立核验脚本Log/Stage4/d20-mode-projection-20261008/verify_d20_20261008.py，报告d20-live-verification-20261008.json：**36份输入、59项检查、verified=true**。检查包括五份源码、XML、实际summary、同响应模式、snapshot顺序/身份、一次写入、重复无写入、正常Reload及场景/队列/资源收尾。两个状态视图允许期间出现更晚heartbeat，只要求同producer下顺序不倒退、模式相同，不强行要求不同采样时刻ID相等。

### 部署与未覆盖边界

- Server进程83556本地启动20:50:19，相关Python源码本地20:47:31保存；这不是加载鉴证。没有足够新证据支持为unknown再自动刷新；本续验没有服务重启、C#写入、重复编译、全量EditMode、外部工程操作、Capture抢占、Console清空或通用Operation启动。
- 全Server模块加载来源仍**unverified**；只声明当前公开行为和上述定向范围通过。其他Unity/UTF版本、强制或中断时Reload未覆盖，不能由正常PlayMode进入推导其通过。
- D20当前实现/定向/现场子项已完成，根TODO替换旧“待定位”描述，仅保留加载鉴证及范围说明，父项未关闭；活动父项仍**47（15D+11N+18V+3独立）**。Tooltip/N05保持P5暂缓。


## 2026-10-08 D07验收Task摘要字节预算修复与真实成功/失败对照

### 已确认缺陷与最小实现

- 原线上成功Task `task-241632e3-b0b1-429e-be7e-74ebde4de929` 的summary声明 `responseBytes=1503`，独立重算compact UTF-8为1521字节。`task_status`在摘要定稿后才追加`elapsedMs`，既少算字节，也可能突破精确16KiB边界。历史回执原样保留，不重启旧Task或修改旧报告。
- 仅改 `upilotserver~/src/upilot_mcp/domain/task_service.py` 的公开投影：在原state的副本中先计算`elapsedMs`，纳入现有白名单，再执行现有摘要定稿。无cursor协议变化、无新框架、不改原state/磁盘历史；full仍保留完整内容。
- 定向测试仅扩展 `upilotserver~/tests/test_persistent_test_jobs.py`：成功/失败 × summary/standard/full六组，及原摘要精确16384字节时追加时间字段的边界。红灯5失败/2通过保留；直接回归三个文件共73通过、0失败/错误/跳过，2条既有websockets弃用警告。JUnit及源码hash见独立核验。该修复仅涉及Python，不新增Unity编译。

### 真实公开工具与原身份收尾

| 现场 | Task / runGuid | 真实业务终态 | summary/standard/repeat compact字节 | full compact字节 | 实际磁盘报告 |
|---|---|---|---|---|---|
| PassLeaf | `task-d3358a7e-497a-4c0b-9e2b-a46b16101a00` / `2a8903f0-8595-4363-860b-ca4b5b409bd8` | completed；1/1通过 | 1520，均等于responseBytes | 967371 | 575494字节；SHA256 `f4b9af336a821180427519d715775a2afaa2cf430967c144c4469c8ea7a1cd62` |
| FailLeaf | `task-2e4c0ea7-a93c-4396-8917-fa2eecc4ec4e` / `0a473b2e-6bc7-470f-aeea-47267a0e7718` | failed；1/1故意断言失败，非验收成功 | 1808，均等于responseBytes | 1451677 | 575914字节；SHA256 `40b437c63cbcc4e98c34d9323ca4aa44ca172da4e893ab7d62fe4a25e3d80af3` |

- 两叶各保存一次启动回执，按原Task观察；限定完整测试名、EditMode、union、`requireAllSelectorsMatch=true`、实际discovery的selection domain/snapshot。`stopActiveCaptures=false`，未抢占Capture；固定原期限180秒，不重发或续期。
- 两次UTF结果均authoritative/terminal、cleanupSucceeded/cleanupResourcesReleased=true、unresolvedResources=[]。失败保持`UPILOT_ACCEPTANCE_FAILED`；full/磁盘/原run结果保留FailLeaf首错、`P2_WP08_EXPECTED_LEAF_FAILURE`及原stackTrace，不用通用失败摘要冒充详细断言。
- summary、standard、repeat及所有保存的poll均独立重算UTF-8大小并检查≤16384；full/repeat对象相同，文件报告等于full.acceptanceReport去掉正常外部artifact回执后的对象；文件不含自身最终hash，不认定为失败。`recoveryDiagnosticsDroppedCount=null`、`recoveryDiagnosticsHistoryComplete=false`保留未知，不伪造历史完整性。
- 复用既有权威C#编译：`wb-2d83dd51-a284-413e-8c4f-b286481aaf06` / `ad156695a3b6418f843841c5aa266d87` / `req-60ffe541-18d1-40f5-802d-63dd78f0e9fb`，`lastCompileVerifiedAt=1791466331814`且覆盖最新C#输入，0编译错误/3条既有Flow警告。本D07批次没有重复请求Unity编译。
- Unity PID23796、Server PID100224、Bridge session `88f8b551d455413c85d2a01a62547fe0`、domain50前后不变；最终队列complete/fresh/items=[]、Capture activeCount=0。唯一场景`Assets/UPilotAcceptance/upilot-acceptance.unity`保持loaded/active、rootCount3、isDirty=false及原文件hash。前轮授权维护记录仍是原maintenanceId `2a0021e1-d133-4a49-a98a-1c14d50251ce`；本轮收尾未再维护或重启。

### 独立核验与原始失误归类

- 证据目录：规范工程`Log/Stage4/d07-task-summary-20261008/`。独立脚本`verify_d07_online_20261008.py`仅使用标准库读取JSON/文件/JUnit，不导入产品投影实现、不连接MCP、不派发业务。
- `d07-online-verification-20261008.json`：verified，194项检查，53份输入bytes/hash索引；自身32513字节，SHA256 `726309a901e84a6a04952aa57cd63471dae7ed6cfb8ec3840e987939c9ef8cbe`。两个实际产物、当前两份改动源码、场景文件和保存的公开回执分别核验；不声称所有历史附件均重新验真。
- 保留早期脚本错误：非法`matchMode=exact`不属于产品故障；只读preflight按协议返回`UPILOT_ACCEPTANCE_PREFLIGHT_ONLY`，其detail为preflightPassed=true/blockingReasons=[]/runnerStartAttempted=false/artifactWritten=false，原脚本把外层ok=false误当失败。解释文件`online-preflight-interpretation.json`已保存，不修改产品迎合错误断言。
- 早期`d07-source-baseline.json`的`sceneFiles={}`不能证明场景覆盖；本核验使用独立`runner-baseline-scene-hashes.json`与`live-final-scene-hashes.json`，并重算真实场景文件。

### TODO与未覆盖

- D07当前版本真实summary/full/磁盘产物对照子项已完成并从根清单的待验描述移除；D07父项仍开放，仅保留Server全模块实际加载来源鉴证。根清单仍47个开放父项，不把子项通过算整项关闭。
- 实际文件hash、source gate、进程时间及公开行为通过不替代全模块loaded-source证明；该边界仍unverified，不因此再次重启。未覆盖全部Unity/UTF、物理进程中断故障矩阵或全部历史产物。
- 本轮两个叶子的阶段日志为空/partial，不计入D08真实Console清空验收；N05/Tooltip继续P5用户暂缓。


## 2026-10-08 V03自有SQLite容量耗尽矩阵与独立落盘核验

### 实施范围

- 继续核对Server加载来源：`source_identity.py`只计算当前磁盘内容，公开`runtimeIdentity.mcpServer`只提供PID，不能据此证明全模块实际加载。保持unverified；未新增导入监控/运行时鉴证框架，未再次维护或重启。
- V03只扩展`upilotserver~/tests/test_editor_execution_state_v2.py`：新增`test_sqlite_capacity_full_never_confirms_write_batch`四组（completed/failed × fresh_snapshot/new_lifecycle）。产品文件不变，既有测试前缀字节/哈希不变。
- 使用测试自有临时WAL数据库，将真实SQLite连接的`max_page_count`限制为当前19页，每页4096字节；fixture-only的4页padding强制终态证据分配溢出页，真实SQLite返回`SQLITE_FULL`/13，异常栈含`_persist_batch_evidence`。连接包装仅设置测试库容量，不替换SQL、提交或模拟异常；不填满磁盘，不接触在线state-v2.sqlite3，不将该容量上限等同于物理磁盘满。

### 观察与定向结果

- 四组均核对完整原始行、完整queued快照、真实DB integrity_check和原batch/createdAt/compileOperationId。失败时数据库原行与排队快照不变、terminalSnapshot为空，原批次仍pending、correlationVerified/ready为false，不提前发布成功。
- fresh_snapshot两组：解除容量上限后，仅接收原操作的新sequence=3快照，不重新登记、不增加编译、不延长原批次期限；页数从19增至31，完成/失败分别保存verified/passed和failed/failed，生产ResourceDomainService的公开投影与持久终态一致。
- new_lifecycle两组：新StateStore生命周期不恢复活动，历史公开回执aborted/unknown、无可验证终态证据；实际数据库仍保持原compiling行和queued快照，不回写为通过。查询重复两次，自有ResourceService夹具的编译恢复回调次数为0。此处运行生产存储与公开投影代码，但Server连接为隔离夹具，不冒充真实HTTP/MCP故障验收。
- 新增定向：4 passed、90 deselected、0失败/错误/跳过；随后直接回归`test_editor_execution_state_v2.py`和`test_write_batch_changes.py`：123 passed、0失败/错误/跳过。两次均只有2条既有websockets弃用警告，不修改检查政策或放宽断言。
- 本轮仅Python测试/记录改动，没有C#/程序集写入、WriteBatch登记、Unity编译或EditMode重跑；没有Capture启动/停止、Console清空、在线库损坏或通用Operation启动。

### 独立核验与证据

- 证据目录：规范工程`Log/Stage4/v03-sqlite-capacity-20261008/`；保留两次JUnit/日志和8份私有数据库现场（初次定向4组、直接回归4组）。回归回执额外保存生产公开投影；初次回执不补造缺失字段。
- `verify_sqlite_capacity_20261008.py`仅使用标准库，独立只读查询实际SQLite库、核对完整行/快照与回执，并重算文件bytes/SHA256；不导入产品实现、不连接MCP、不派发业务。
- `sqlite-capacity-verification-20261008.json`：verified，190项检查、41份输入索引，37614字节，SHA256 `cec1e2fdfbb6bd7656c91f327ccc43a0a53e76ce386c9aa7e6c222567a395fee`。
- 首次独立脚本的仓库ancestor索引写成5而非4，查错路径后退出，未生成验证报告；仅修正该辅助脚本，原失误保存于`verification-first-attempt.json`。不是产品失败，不为该错误修改测试/产品断言。

### TODO与剩余范围

- V03容量上限/真实SQLITE_FULL四组子矩阵已完成，不再列为待验；V03父项仍开放，保留物理断电、物理磁盘满、未覆盖其他磁盘IO及在线服务进程故障矩阵。此前共享拒绝、COMMIT/退出等通过证据不重做。
- Server全模块加载来源仍unverified，以上离线证据不替代实际加载鉴证、真实Unity/UTF故障或物理设备验收。N05/Tooltip仍为用户暂缓P5。
- 根TODO保持47个开放父项（15D+11N+18V+3独立），未将子矩阵通过写成整个父项完成。


## Camera空选择器修复及公开策略/成功矩阵续验（2026-10-08批次）

本节整理原2026-10-08测试、维护和公开Snapshot作业的保存证据，并补独立核验。核验保存时钟读数见报告`verifiedAt`；文件名沿用原批次日期，不据此宣称本轮再次执行测试、截图、编译或维护。

### 已确认缺陷与最小实现

- 实际工具模型补`instanceId=""`，共享wire-ID转换原先将它变成显式`"0"`；Camera名称请求因此得到`CAMERA_SELECTOR_INVALID`，而非真正解析后的`CAMERA_NOT_FOUND`。红策略回执原样保留。
- 唯一产品改动位于`upilotserver~/src/upilot_mcp/domain/snapshot_service.py`：现有GameView空ID分支的条件扩展为`kind in ("camera", "gameview")`，仅在共享归一化之前移除空ID。保留显式0/非空ID及null的既有输入行为，不改全局wire-ID转换或Camera捕获实现。
- 测试只在`upilotserver~/tests/test_snapshot_tools.py`插入两个函数，覆盖native/proxy工具模型经实际dispatcher的40组选择器/ID组合及2组null拒绝。独立核对删除新增函数后的AST与旧文件相同，原测试字节diff只有插入，无原断言削弱。
- JUnit实数：红灯42项中20失败/22通过；绿灯42/42；两个直接模块87/87，均0错误/跳过，2条既有websockets弃用警告。没有扩大为全套测试。
- 产品源码31109字节，SHA256`15619025827c81ead5ece631fa241e1fe92ff0223336099d4d8e895dde8a1fc9`；测试32596字节，SHA256`98b46894a4c808b70f75eedb560a85d34a4c2418025df41d58d22b42baaed088`。原源码及原测试保存在`snapshot-camera-selector-20261008/*.before`，原始hash已核验。

### 原授权维护与来源边界

- 原maintenanceId=`0e5b839e-1ee7-4aab-8a70-d75c0a701bbe`，target=server，现有独立grant有效，autoHardStopOnSoftFailure=false；终态succeeded/readonly，queueReset/readOnlyVerified/healthVerified=true。保存的所有观察保持原accepted时间及deadline，无续期或重发。
- Server PID100224→100652，Bridge session`88f8b551d455413c85d2a01a62547fe0`→`6b64af2b30db4da9ba263088dc885c54`；Unity PID23796、producerEpoch`46c655b676144c4bafbc5181145bf518`及domain50不变。此次核验没有再次重启；PID变化和行为对照仍不能替代全Server模块实际加载鉴证，保留unverified。
- 三批状态/源码/场景前后数据及当前真实文件hash逐项一致；红策略仅对应旧snapshot_service.py，其余对应当前源码。两策略批次各核对5份源码，六成功批次核对6份源码（含测试）。唯一Camera ID`568105584918842838`，名称/层级`Camera`；场景`Assets/UPilotAcceptance/upilot-acceptance.unity`保持loaded/active/isDirty=false。
- 复用编译基线`wb-2d83dd51-a284-413e-8c4f-b286481aaf06` / `ad156695a3b6418f843841c5aa266d87` / `req-60ffe541-18d1-40f5-802d-63dd78f0e9fb`，verifiedAt=1791466331814，0错误/3条既有Flow警告。三批before/after及维护前的编译身份、时间、计数不变，没有C#写入/WriteBatch或再次编译。
- 保存的每批before/after队列均complete/fresh/items=[]，Capture activeCount=0；未抢Capture、清Console、改场景或模式，未启动通用Operation/Step。

### 原公开策略红绿对照

| 批次/策略 | 原snapshotId | 业务终态 | missing错误 | valid目标 / PNG | manifest字节 |
|---|---|---|---|---|---:|
| 红 bestEffort | `snapshot-5100ed9304f542b1b501c6bb906baf1d` | partial / success=false | CAMERA_SELECTOR_INVALID | completed / 1 | 18150 |
| 红 allOrNothing | `snapshot-b16469f5c862449ba2965cd4cc64bb9b` | failed / success=false | CAMERA_SELECTOR_INVALID | skipped / 0 | 15430 |
| 绿 bestEffort | `snapshot-7c8c7431b50545b3a4c16859cb868666` | partial / success=false | CAMERA_NOT_FOUND | completed / 1 | 18084 |
| 绿 allOrNothing | `snapshot-c3e277458c7e4fb68a9127e2d6e97648` | failed / success=false | CAMERA_NOT_FOUND | skipped / 0 | 15367 |

- 请求均由公开`unity_snapshot_capture`启动，waitMs=0即时返回原Snapshot身份；后续poll、collect、repeat只观察原ID。SDK structuredContent和每个text JSON逐份与保存的公开envelope一致，未绕开工具模型/dispatcher链。
- 绿矩阵是包含一个预期失败目标的策略验收，不是全部截图业务成功；红矩阵原`allExpectedMissingCodes=false`及原错误不改写。

### 六组有效选择器成功矩阵

以下均completed/terminal=true/success=true，精确命中同一Camera；请求省略ID和显式空ID分别保留原请求证据。

| selector / ID | 原snapshotId | manifest字节 | 实际manifest SHA256 |
|---|---|---:|---|
| cameraName-empty | `snapshot-1a1d9b33d9354ce88b5ff6f186866205` | 10735 | `6ee7494a89addf9edb08ab5dd9c49700e489d3de4ffd012270f8daa209377b5a` |
| cameraName-omitted | `snapshot-ecc4ac6d2448453c94d723f3a5119b99` | 10747 | `39f976ce858bb181625567308a849c656a9dcf1206e3ce345e1774e4a4c69827` |
| exactName-empty | `snapshot-75029107353a4d16b771d82cd91526b4` | 10729 | `a08b0b02eb6a3c4282f8f52e9b3d928cce469d99edcf1c27e78239d1dffca41c` |
| exactName-omitted | `snapshot-d3f2b84116c143f9b9b700943f6909fb` | 10741 | `49afe286cb7232f678090f0d01d244a62f3ed9965760b9a42fba1b37d0981f6f` |
| hierarchyPath-empty | `snapshot-2d3128868faf468fbeecbb567253df24` | 10753 | `0a9ee563aa374103aad12f56927d303a8eada79921f9a19f878a179a9cb13def` |
| hierarchyPath-omitted | `snapshot-74712a5de1f14bac9afadc39af4e2b15` | 10765 | `1f1cdc8cbcaded79774600ecec5a87d4d151f5c5d63dcb99e1c07cef9c629acc` |

- 六份PNG均160×90、2939字节，SHA256`4bd1a73598032ca57ea23037f75378f98da534cacb0931c21394a2fbb4793048`，acceptedAsEvidence=true、pixelSourceVerified=true，无fallback，captureApi为`Camera.Render(RenderTextureDescriptor)`。另两份bestEffort PNG也逐份核验；总计8份真实PNG。
- 原终态external state字节仍与各自保存副本一致；公开final/collect/repeat对象一致。原state到公开投影只多persistenceVerified，四个策略作业默认captureDiagnostics.instanceId的整数0投影为字符串"0"；六成功作业无该转换。没有用投影修正历史文件。
- manifest与原state仅有persistenceStatus=pending、manifestBytes=0、manifestSha256为空三项差异；公开外部receipt的最终manifest大小/hash与磁盘字节一致。该机制不要求manifest嵌入自身最终hash，也不重建事务或schema。
- PNG签名、IHDR尺寸、CRC/IDAT读取及实际字节hash均核对；完整产物集合、冻结前后字节hash与当前文件一致，无漏列PNG。没有打开图片或进行文字/方向/完整构图的视觉断言，不把来源验证当业务视觉验收。

### 独立核验、首次失败与TODO状态

- 标准库脚本`Log/Stage4/verify_snapshot_camera_selector_20261008.py`不导入产品、不连接MCP、不派发业务；10个作业均为已有原ID。报告以xb独占创建，拒绝覆盖原报告。
- `snapshot-camera-selector-verification-final-20261008.json`：verified，1628项检查、329份输入bytes/hash索引、10个原作业；234566字节，SHA256`edbb8faef26c65faf02c58c21ca82879faafe29bab874b7bc635c3195f9e1bf2`。脚本SHA256`7914cc6a2234f2759b0b1a6eb8484d4c93c9dd93e464ed3c2845d82f75f6baf9`。
- 首次`snapshot-camera-selector-verification-20261008.json`保留failed：736项已完成检查/237份输入，失败为红allOrNothing路径集合。实查Path.resolve返回log、回执写Log，但samefile=true且双方manifest hash相同；属于核验脚本按字符串误比较Windows同文件路径，不是产品产物缺失。仅将此处改为Windows Path语义比较，并增加预期/回执两侧重复路径别名拒绝，仍严格比较完整路径集合及每个文件大小/hash，没有删断言或改业务数据。原报告130642字节，SHA256`195d69e1d744ba730019a35316f17e0e7877855e7564098d2e8935d7a897afca`；原脚本保存在`.py.before`。
- 根D18只记录Camera修复和当前公开选择器/策略子项完成；父项仍开放：公开GameView bestEffort捕获中取消、真正捕获中Reload/取消、实际服务终止迟到回调及全Server来源仍未覆盖。六成功不是视觉验收，其他Unity/UTF版本不外推。
- 根清单仍为47个活动父项（15D+11N+18V+3独立）；Tooltip/N05保持P5暂缓，其余条目状态和历史失败不改写。


## UTC 2026-10-08 续验归档：D18公开bestEffort取消与D20辅助isPlaying投影

本节只归档已有原请求、回执、终态和独立核验，不重放Snapshot/维护/PlayMode动作，不重新编译。UTC与北京时间分别注明；原章节、旧红灯及历史覆盖边界全部保留。

### D18：两个公开bestEffort子项已完成，父项仍开放

- 原现场UTC 2026-10-08T16:38:35～16:39:00（北京时间2026-10-09 00:38:35～00:39:00），53个公开调用三件套、179份保存JSON。最后目标rendering取消：`snapshot-eef2ec7815744dfaa3370f9817f4248d`；取消后Camera后继未执行：`snapshot-de082044e01c40d4933f39e83337c318`。后续只查询原ID，未重新启动业务。
- 两作业均cancelled/terminal=true/success=false，PNG acceptedAsEvidence=false；job/state/manifest/PNG和重复查询冻结，外部manifest最终字节数/hash与磁盘匹配。两manifest分别9270/9907字节；诊断PNG均320×180、2088字节，SHA256 `37e40f47422b48c6310eaf93a762b70d9a690d9a0c2550329e17a3dbcd4ec84b`。
- `Log/Stage4/snapshot-game-besteffort-cancel-verification-20261008.json`为verified，1563/1563检查、199份输入。标准库核验不导入/执行原探针、不连接MCP；完整输入大小/hash/mtime索引和冻结结果保存在原报告。
- 捕获源900×600而输出320×180，比例不一致；没有方向、文字、完整构图或内部裁切视觉通过结论。取消PNG只是诊断，不作为成功截图证据。
- 根D18中的这两个公开bestEffort待验描述已纠正；真正捕获中Reload/跨Reload取消、实际服务终止迟到回调、GameView视觉验收、其他版本和Server全模块加载来源仍未覆盖。原SceneView/Camera章节在当时未覆盖bestEffort的记载不改写。

### D20：辅助暂停投影的最小修复及真实六动作

- 产品仅改StateStore辅助投影一行：`isPlaying = play_state in ("play", "pause")`，增加两个直接回归测试；StatusDomainService未改。不合成模式成功、不加定时器、重试或额外模式写入。
- 保存的定向20/20及直接状态模块102/102通过（选择重叠，不相加）；本地标准库独立核验248/248。证据目录`Log/Stage4/d20-pause-playing-projection-regression-20261008T182310Z-3d9814f8/`；首轮红灯和夹具时钟修正仍保留。仅Python改动，无新增C#/程序集输入，不触发Unity编译。
- 两次旧记录器失败均发生在维护派发前，原失败和无派发标记保留；不归因为维护失败或产品故障。
- 唯一维护`c102f9b9-c019-4c80-be2d-8cedade4e0f0`，target=server，succeeded；acceptedAtUtcMs=1791485306562，deadlineAtUtcMs=1791485426562，endedAtUtcMs=1791485309755。queueReset=true、healthVerified=true、readOnlyVerified=true，原120秒期限不变；underlyingExecutionStopped=false，不声称底层执行物理停止。
- Unity PID23796未变；Server PID100652→106268；维护前后Domain均51、Bridge session改变。随后自有PlayMode正常Reload为51→52，producerEpoch不变，不是再次服务重启；维护没有切换模式、编译或重启Unity。
- 公开start/pause/重复pause/resume/重复resume/stop六动作全部confirmed=true：实际模式写入4次，重复pause/resume各writeCount=0、changed=false、commandSubmitted=false。暂停时辅助editorState、权威context及status/editor/executionState均isPlaying=true/isPaused=true，快照有序无回退。暂停readiness使用any/observe，不放宽play/observe口径。
- 原`live-v1/live-report.json`为passed，69份观察，完成UTC 2026-10-08T18:53:54.055102（北京时间2026-10-09 02:53:54）。最终EditMode authoritative/fresh/ready，队列complete/fresh/items=[]/issues=[]、Capture activeCount=0、无测试或清理资源；场景及meta大小/hash/mtime和结构不变，编译身份及时间不变。
- 独立`independent-verification-v1/verification.json`为verified，804项检查、164份输入；重新逐份核对原SDK/semantic回执56份模式观察，56不是原69份观察被减少或失败。原输入重新读取大小/hash/mtime不变，标准库核验MCPCalls/modeWrites/serviceRestarts/compileRequests/productFileWrites均0。
- Console跨正常Reload游标仅有限观察，不能声称完整覆盖或因果关系。当前规范工程Unity6000.6.0a2/UTF1.7.0行为通过，Server全模块实际加载源码身份仍unverified；进程变化、磁盘hash和公开行为不替代加载鉴证，不外推其他版本或中断Reload。

### 文档范围与关闭边界

- 仅修改根TODO的D18/D20当前剩余描述并在本交付文档追加本节；开放父项仍47、Tooltip/N05保持P5暂缓，其余活动条目和历史失败不变。
- 更新前原文件逐字节备份，交付文档原字节前缀不变，根TODO的其他区域不变；备份、源证据hash及更新后校验保存在本次`todo-d18-d20-closeout-*`目录。

## D18：既有三尺寸 GameView 视觉证据独立复核归档（UTC 2026-10-08；北京时间 2026-10-09）

- 本轮性质：核验已有历史产物并更新剩余状态，不是重新执行测试或截图。记录时间：2026-10-08T20:02:02.741304+00:00；本地时间：2026-10-09T04:02:02.741304+08:00。未修改 C#/程序集/产品像素路径，未重编译、进入 PlayMode 或重启服务。
- 原测试：2026-09-30 runGuid=`b09f3bc9-8cba-4c45-b523-705105d1d3cb`，单叶 `AsymmetricGameViewReportsNativeScaledAndMismatchedOutputWithoutCorrection`，1/1 Passed；历史 `completed/terminal/resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased` 均确认。Snapshot 无显式 runGuid，仅以相同 serviceLifecycleId、单叶、夹具输出及捕获时间落在原 run 范围关联，不冒充直接绑定。
- 标准库独立核验：`Log/Stage4/d18-gameview-existing-visual-20261008T193604Z/verification-v2.json` 为 **verified，251/251 项检查、24 份输入**；报告 177623 字节，SHA256=`d86774623dc26e349e31cf0808ca1b416ae51b6787cab5c6d9a4d59549850a6c`。脚本 `verify_existing_visual_v2.py` 19699 字节，SHA256=`ffe11c8a09053a80ac3d5c4402250bf6bd802418f6709f0a52c5947204214268`。
- 保留首轮红灯：原 `verification.json` 为172/175，三项黄色采样被原阈值误判；文件和旧脚本原样保留。夹具使用 Unity `Color.yellow`，当前精确规范工程的属性分量观察为 `[1,0.9215686,0.01568628,1]`，最近字节量化 `[255,235,4,255]`，与三张原 PNG 对应点及四邻点一致；新分类器严格匹配此值，未将绿色阈值泛化放宽。新增标准RGB黄及邻近不匹配/透明值拒绝断言，原175项检查均保留通过。
- 当前颜色观察路径：`Log/P0P1/d18-gameview-existing-visual-20261008T193604Z/unity-yellow-components.json`。`backendUsed=interpreter`、4次属性getter、0次业务方法调用、无resourceDiagnostics；通用sideEffectsMayHaveOccurred标志仍保留为true，不据此伪造全局无副作用证明。此前 `Color -> Color32` 子集转换返回 InvalidCastException 的 `unity-yellow-value.json` 原样保留，不重放该请求、不扩大到转换语法改造。

| 原图 | 实际尺寸/字节 | PNG SHA256 | 本轮重新打开原图的视觉观察 |
|---|---|---|---|
| asymmetric-0.9c5123db.color.png | 900×600 / 24273 | 58008a7afee5f6c44e70d47298891f5a59faac267a521d564c6d16c2f7708032 | 蓝/黄在上、红/绿在下，TOP/BOTTOM文字上下倒置。 |
| asymmetric-1.bb347662.color.png | 1800×1200 / 52549 | 89f887ac0d78c896873f922b446c3dec6e91ad283f8028f5ba6504d6122dc9b6 | 原900×600倒置内容位于左下，顶部和右侧黑区；没有按请求尺寸放大内容。 |
| asymmetric-2.afaf7f34.color.png | 600×900 / 24269 | 27ae72e164df49627973e5bf6655b03918155ac435ad3e22fd2c76ed5c2eedf5 | 顶部约300px黑区，下方内容倒置，右侧/文字裁切。 |

- 三组 manifest 实际大小分别8989/8998/9072字节，最终SHA256与外部state回执匹配；PNG尺寸、字节和哈希与产物回执一致。manifest内部pending/自身哈希空/bytes=0维持原设计，不回写历史；外部回执verified不能被理解为完整构图验证。
- 源均900×600；前两组宽高比匹配，第三组明确SOURCE_OUTPUT_ASPECT_MISMATCH。历史 `contentOrientation/compositionComplete=unverified`、`internalCrop=unknown` 保持不变；`pixelSourceVerified`只说明像素来源。人工记录绑定原图哈希，保存为 `manual-visual-observation-v2.json`；不是OCR或通用视觉业务断言。
- 已完成子项：既有三尺寸真实图像的首次视觉证据及产物独立复核归档；不再把该子项写成完全没有现场证据。**诊断通过不是倒置/黑区/裁切问题已修复**，本轮不翻转、补黑边、缩放或更改像素。
- 未覆盖：当前改动后的代码执行；隔离fixture service的公开MCP恢复；真正捕获中Domain Reload、跨Reload取消、实际服务终止迟到回调；其他Unity/UTF/图形API组合；全Server模块实际加载来源鉴证。当前源码/交付文档哈希只在本轮核验时冻结，不是历史源码或在线加载证明。
- 文档收尾：根TODO仅更新D18当前剩余及本轮归档说明；此文件只追加，本轮之前的全部字节不变。D18父项保持开放，活动父项仍为47（P0=1/P1=15/P2=24/P3=2/P4=4/P5=1）；Tooltip/N05维持P5暂缓。


## V17：多轮窗口生命周期与原PNG独立复核续验（北京时间2026-10-09）

- 本轮记录时间：UTC `2026-10-08T22:36:10.396651+00:00`；本地 `2026-10-09T06:36:10.396651+08:00`。本轮未修改C#/程序集、未编译、未启动测试/截图、未重启服务、未聚焦Unity或关闭跳转列表。仅重新核验既有证据、查看四份原PNG及保存只读现场。
- 原生命周期runGuid=`5d80bc7d-1bad-4404-b559-46f4a44a1d8c`：5/5通过，completed/terminal/resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased均确认，unresolvedResources为空；覆盖utility/floating三轮最小化恢复、注入失败后继续捕获及未显示窗口拒绝。原五项终态不重新执行。
- 原独立报告：`Log/Stage4/v17-repair-20261008T215232Z/v17-independent-verification-v1.json`，verified，1263/1263项检查、228份输入、36个作业、24份成功PNG。本轮228份输入重新计算完整字节数/SHA256仍与该报告冻结记录一致。每个作业绑定原leaf开始/结束事件；完整job与外部state相等；manifest仅保留其自身最终bytes/hash及pending三项机制差异，不回写历史。
- 失败矩阵：6个SNAPSHOT_EDITOR_MINIMIZED、4个SNAPSHOT_TARGET_CAPTURE_FAILED、2个WINDOW_CHANGED_DURING_CAPTURE；失败目录无产品PNG。四个自有窗口均在有界等待后释放，测试seams恢复；Close同帧存在不等于永久泄漏，不处理未知原生对象。
- 像素独立核验覆盖24份成功PNG全图：左红、右绿、上蓝、下黄、内部洋红；原Unity黄色精确量化为[255,235,4,255]。utility实际320×220，floating实际内容320×194，不把默认请求1280×720当成精确原生路径的缩放承诺。
- 本轮通过functions.view_image重新打开两类窗口00-normal与02-restored共四份原PNG：四边色标完整，GUI上对应PNG上，内部洋红，正常/恢复图无肉眼可见fixture边缘缺失。utility两图均2013字节/SHA256 `2dadca3b137ff32c34007ca2873642bd1784b36d8b19dbdabcd21c979ca65dee`；floating两图均1789字节/SHA256 `dd034c136d86813b52adbc8f11dc0971368120ac4639ef10f036856e8e74651e`。人工复核只覆盖这四份，不冒充24份人工复核、文字/OCR或混合DPI验收。记录：`Log/Stage4/v17-followup-closeout-20261008T223610Z/manual-visual-observation-v1.json`。
- 直接几何原runGuid=`3dde722f-e28e-4542-9c53-16eca3c192ce`为failed/terminal/resultAuthoritative，3通过/1失败，cleanup成功且资源已释放。原红灯`geometry-results-final-red.json`保留；唯一失败为`NativeCaptureMatchesWindowPixelsAcrossMoves`在Tests/Editor/UPilotWindowInputContractTests.cs:424断言“自有绿色窗口必须为原生前台”，Expected True/actual False。未降低断言、未把该失败直接归因为截图像素或几何映射缺陷。
- 本轮只读现场：Unity PID23796/HWND134414，Bridge session199c067306244c4196a37c0949fdd8ba/domain55；unityFocused=false，前台HWND394868/PID16920/标题“Unity Editor 的跳转列表”。mainWindowEnabled=true、无可能模态候选。该现场不是原失败瞬间的完整因果证据。EditMode ready/fresh，队列complete=true/items=[]，Capture activeCount=0。原始回执保留为v17-followup-focus/ready/queue/captures-02.json；不自动抢焦点、不关闭跳转列表、不盲复验。
- 沿用原编译批次`wb-bf221939-0464-4864-aa59-daea5db08a76`/operation62602b8ea8b2436abf97f690f8e9444a/requestreq-85610066-77d4-416b-8b06-ad3b584054cf，lastCompileVerifiedAt1791496458211晚于writeBatchCreatedAt1791496441970；errorsVerified/correlationVerified/terminal均确认，0错误/3条既有Flow字典序列化警告。两份当前改动C#源码仍匹配独立报告保存hash；无代码变化不重复编译。
- 剩余：前台条件确认后只复验唯一失败单项，核对该运行的PNG、身份和清理证据后再考虑关闭V17。混合DPI、非零workspace偏移、先最大化再最小化及其他Windows/Unity组合未覆盖；全Server模块加载来源仍unverified。不得用当前版本/磁盘hash外推上述边界，也不要求重做已通过生命周期矩阵。
- 文档范围：仅更新根TODO的V17真实剩余及本次头部补充；此文档只追加，原字节前缀不变。更新前原字节备份及本轮验收报告存入`Log/Stage4/v17-followup-closeout-20261008T223610Z/`。V17父项保留，活动父项仍47（P0=1/P1=15/P2=24/P3=2/P4=4/P5=1），Tooltip/N05保持P5用户暂缓。


## V10：当前实际EXE完整Skill资源核验结项（北京时间2026-10-09）

- 记录时间UTC `2026-10-08T22:55:37.887590+00:00`。本轮仅恢复自有记录器回执、读取已完成构建产物并独立核验、更新两份文档；没有重跑构建、执行EXE、启动Server、安装依赖、发布、Unity编译或EditMode测试。
- 历史EXE只读核验：`upilotserver~/dist/upilot-mcp-server-0.3.35-win-x64.exe`，95/95项检查通过，20/20份Skill资源与内嵌commit `120dd31c0d66d45d26ba8c17ed6bf43e33c8bd1a` 的Git blob逐字节相同。历史规则44/Skill50，与当前11份资源不同；历史验证本身不能关闭当前V10。
- 当前实际构建：此前仅在本任务自有mirror中使用原生产`deploy/build_server_exe.py`，PyInstaller成功终态已落盘；产品EXE、manifest和checksum均存在。原wrapper最后将`write_manifest`返回的WindowsPath放入JSON，产生`TypeError: Object of type WindowsPath is not JSON serializable`，exec session28249已结束且当前handle缺失。原脚本、v1 running记录与构建日志原样保留；新v2从既有产物/日志恢复`built`及11/11收尾检查，不伪造wrapper elapsedSec、不重跑构建。
- 当前EXE：`Log/Stage4/v10-exe-resource-verification-20261008T224414Z/isolated-current-source/upilotserver~/dist/upilot-mcp-server-0.3.42-win-x64.exe`，实际33393152字节，SHA256 `aacf9e14a33d46fc08ccd726f6b5ac67e373171af12d2ad68e3fc685df464be3`。独立实算与manifest及checksum逐项一致；内嵌版本0.3.42/channel local-verification，sourceIdentity为`worktree-c2598e958e51b083a73f935b4832d94a9adba3a706712a2c5434d27503d5b28a`。该身份是未提交工作树的输入快照，不是release commit或已发布版本。
- 独立实际档案核验：`current-exe-resource-verification-v1.json`为verified，256/256项检查通过；CArchive908项，Skill实际20/20份精确资源集合、每份原字节/大小/SHA256均与当前冻结源及mirror一致；内嵌规则51/Skill60。独立排除meta、pyc/pyo、__pycache__和安装回执；独立重新计算输入快照身份。123份共享原输入与7份共享dist文件的大小/hash/mtime及清单前后不变，mirror临时build_info已删除。
- 打包契约定向测试：`tests/test_build_server_exe.py -k "bundles_complete_skill or missing_skill_resource"`为4 passed/5 deselected，实际日志哈希已核验，生产构建脚本及测试文件未变化。该测试只是清单/缺失资源契约；本项关闭的关键证据是上述真实当前EXE逐字节检查，不以单元测试替代产物证据。
- 证据目录：`Log/Stage4/v10-exe-resource-verification-20261008T224414Z/`；包括历史核验、原构建命令和日志、v1/v2构建报告、当前独立核验、定向测试，以及文档更新前原字节备份。Windows Log/log为同一目录，不重复计数。原记录器错误不改写为产品失败，也不删除旧记录。
- 关闭范围：V10唯一剩余的“实际EXE是否携带完整Skill资源”在当前Windows x64本地构建已取得直接证据，根活动表移除V10。未覆盖EXE运行/--version/MCP启动、远端发布或资产、全Server模块加载鉴证、Unity compile/EditMode及其他平台打包；其他父项不因本次通过自动关闭。当前活动父项46（P0=1/P1=15/P2=23/P3=2/P4=4/P5=1）；Tooltip/N05仍P5用户暂缓。
- 文档最小范围：根TODO只删除V10行并添加最新结项说明；本文件只追加，原字节前缀不变。外部工程的同ID来源引用不重复计数、不改写其业务状态。


## V01：真实Unity隔离恢复五场景与独立证据核验归档（北京时间2026-10-09）

### 交付范围、原运行与编译关联

- 本次文档归档时间UTC `2026-10-09T01:04:43.204128+00:00`。只读取已有运行和核验结果、重新核对八份当前源码hash、更新两份文档；没有重跑测试、Unity编译、启动peer、服务重启、聚焦、清Console或操作Capture，不修改产品/测试源码。
- 规范工程精确路径：`D:\upilot\Tests~\UPilotTest`。原runGuid=`8ffe9b3b-e3c4-44df-a236-2acab8d7dbda`，originatingRequestId=`req-107d2a81-9cd3-4f2e-9805-2c03bf8abe19`，originatingCommandId=`cmd-2db61da3-93db-487c-802b-8db7333ae7ca`，公共serviceLifecycleId=`3d69ccbc4f024ada9b7e4f5974235b91`。
- 原定向运行total/passed/failed/skipped=5/5/0/0，status/phase/outcomeStatus/cleanupStatus均completed；terminal/resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased均true，cleanupPending/isRunning=false，runnerState=inactive，cleanupErrors/unresolvedResources均空。startedAt=`1791504787118`、endedAt=`1791504811819`、snapshotSequence=17、persistenceError为空；筛选domain=`22f387bf7eb54d1595be4f2337902c41`，snapshot=`93972adfccba506839f71c40d37425d118909fadc1749d1482622ef352b28d14`。
- 五个精确测试均属于`CodingRiver.UPilot.Tests.UPilotRealUnityRecoveryTests`：`CancelledMutationIsFrozenAcrossSocketLoss`、`CompletedMutationIsFrozenAcrossSocketLoss`、`DeclaredReloadDoesNotReplayRealUnityMutation`、`ResponseLostNeverReplaysRealUnityMutation`、`TimedOutMutationIsFrozenAcrossSocketLoss`。不是全量EditMode，不以五项覆盖全部故障组合。
- 复用原编译：writeBatch=`wb-b48f7519-78ef-435e-a5b7-0e786f7e68e1`，operation=`op-1961a1bf-e498-4727-834d-4f68054fdef3`，compileOperation=`cbdb4b3aeb414affabbbcf7c14971de4`，compileRequest=`req-87575f83-4d2c-4983-8b8c-ede9e745f9a0`。writeBatchCreatedAt=1791504084679、lastCompileVerifiedAt=1791504096932，verified/passed且terminal/errorsVerified/correlationVerified=true，0 errors/0 warnings；夹具hash=`88ef247408ac85f039ceb2d422ba1c73b9e86e3018a6a1fe5c5145dfd4fb6922`。该关联在原运行前完成，本次无C#变化不重复编译。
- 历史失败runGuid=`40ab5907-98ef-4eaf-866a-20aa57faf7ab`仍为failed、5 total/0 passed/5 failed、terminal/resultAuthoritative/cleanupSucceeded=true。夹具CloseAsync收尾竞态修正后的新运行另行归档；旧peer成功不改写旧NUnit失败，旧source-after缺失不补造。

### 五个原现场与冻结契约

原现场根目录：`Log/Stage4/RealUnityRecovery/`。

| 场景 | 原目录 | 原commandId | peerPID/端口 | 冻结结果 |
|---|---|---|---|---|
| cancelled | `cancelled-60d9e1c7d2c34cc3a7a7da240f7ae5ad` | `cmd-463e0b43-9752-4609-bbed-b8f5032d064a` | 113392/13951 | failed/CANCELLED |
| completed | `completed-870f85ad4d654345a430230d69a2bb83` | `cmd-a6e90994-0be2-4d72-a123-22dd05b84688` | 108224/13969 | success/effectCount=1 |
| declared_reload | `declared_reload-31c8ef6744244be292a07b64acd382c5` | `cmd-54de455f-2ea9-40fe-b812-c22552857cee` | 99536/13998 | failed/COMMAND_RECOVERY_REQUIRED |
| response_lost | `response_lost-7873ec9bf8b04683af169986baed625d` | `cmd-65b8c583-cbc0-4dfa-a5fa-72cb4d50017c` | 114464/14020 | failed/CONNECTION_LOST |
| timed_out | `timed_out-8ef9783423a448ecb399bcef223fd052` | `cmd-a9165ad9-804f-4586-9080-549c8026091c` | 109368/14046 | failed/COMMAND_TIMEOUT |

- 每场景只有一次`fixture.mutation`发送，Unity自有effectCount=1；原CommandRecord完整`frozenOriginal == finalOriginal`。重连后的原ID/effectCount=999迟到结果在inspect响应之前有有序屏障，仍不能覆盖原终态/首错，pending/suspended归零。
- timed_out原timeout=2000ms，终态时间等于原创建时间+2000ms；其余原timeout=10000ms。轮询、断连和重连均不续期。declared_reload在声明`domain_reload.starting`后断连时先suspended，重连后不重放，并以COMMAND_RECOVERY_REQUIRED结束；这是声明通知，不是真实Domain Reload。
- response_lost没有记录`originalResponse`字段，核验保留该缺失，不重构或补造响应。完整CommandRecord是内存证据，不能据此宣称命令终态已SQLite落库。
- 各peer自然退出code=0、peerKilled=false、deadlineExpired=false，原业务failure与cleanupError均空；两端自有连接/pending/suspended清零。`unity-cleanup.socketStates=[Aborted,Open]`是finally调用Abort/Dispose之前采样，不是收尾后泄漏计数。共享SessionState lifetime=`dcaa1c7d896b4fbf939f20d4fb0fa686`前后不变；该字段不是公共Task的serviceLifecycleId，不能交叉比较。

### 独立核验、加载证明与磁盘证据

- `Log/Stage4/v01-real-recovery-verification-20261009T003552Z/verify.py`的独立输出`verification.json`为verified，**491/491项证据检查、154份输入**；核验完成UTC `2026-10-09T00:55:09.034673+00:00`。491不是测试数，NUnit仍为5/5。所有输入在原核验前后bytes/SHA256/mtimeNs不变；脚本/报告独占创建，未覆盖原数据。
- 原full公开结果有5个results和10个events；两页各5 events且results=[]，空页events/results均空，sequence1..10连续。持久`Library/UPilot/TestRuns/8ffe9b3b-e3c4-44df-a236-2acab8d7dbda.json`与公开full.data仅差正常外部`consoleEvidence`，其余完整相等，旧failed持久run也纳入不变核验。
- 八份源码的场景before/after与原核验当前hash均相等，本次文档更新前再次实算相等；具体path/hash见verification.inputs。真实Unity进程/Windows PID创建时间/executable/role/project身份、ClientWebSocket以及生产HandleMessageJsonAsync/router/Send已覆盖；**生产ReceiveLoop尚未运行，五场景均没有真实managed Domain Reload**。
- 隔离peer实际live CodeType与冻结源码编译CodeType等价，记录六方法：`_handle_message`、`_suspend_or_fail_pending_on_disconnect`、`_resend_pending_commands`、`send_command`、`CommandDispatcher.call`、`StateStore.configure_project`。这只证明六个隔离peer方法，**不是共享在线Server全模块加载来源鉴证**；不以进程时间、磁盘新旧或版本字符串替代加载证明。
- 五份自有SQLite WAL均0字节，原核验确认后用`mode=ro&immutable=1, uri=True`读取，integrity=ok、准确九表均0行，数据库及副文件前后不变。只证明隔离/schema/完整性，不证明命令终态持久化、SQLite事务故障或共享在线数据库验收。
- Console原logs=[]/total=0，整体观察coverage=complete；testExecution/cleanup仍partial，gapReason=`console_cursor_clear_or_reload_not_detectable`，association=`observed_during_run_not_causal`。不以整体零日志抹去阶段缺口，也不推断因果。
- 公开before/after采集时均为UnityPID23796/creation134358983013306077、ServerPID106268、Bridge session=`6d537507bc8b497c8c9b39aa33d94744`、epoch=`46c655b676144c4bafbc5181145bf518`/domain57，项目匹配、ready/fresh、队列来源完整且空、Capture无活动。这些只代表原采集时刻，不是本次实时状态声明。
- OS原快照UTC `2026-10-09T00:36:56.7253858Z`：五个自有端口无监听；四个peerPID不存在，PID114464已被无关WMIC.exe复用，创建时间晚于原peer结束，不是原peer残留。没有杀无关进程，不把仅PID同号当作泄漏。

### TODO更新与真实剩余

- 根TODO仅替换V01一个表行，本交付文档只追加、原字节前缀不变；原字节备份及更新核验放在上述v01核验目录。原Python72/72、相关64/64、现行动作授权8/8及旧失败记录保留，不重复已通过矩阵。
- V01父项仍开放：真实Domain Reload、生产ReceiveLoop、其他独立故障组合及共享Server全模块加载来源鉴证尚未覆盖。不将自有mutation一次等同于所有工具/Operation的universal exactly-once，也不启动暂停的通用Operation/Step。
- 活动父项仍46（P0=1/P1=15/P2=23/P3=2/P4=4/P5=1）；V10不恢复，Tooltip/N05仍P5用户暂缓，V12/V15当日外部增补和所有其他行保持原字节。


## V01：生产 ReceiveLoop 单叶证据独立核验归档（北京时间2026-10-09）

本节仅追加生产 ReceiveLoop 的 response_lost 单叶增量，不改写上文五场景的历史范围、失败记录或未覆盖声明。V01 仍为 P0、部分完成，整项未关闭。

### 本次结果与原身份

- 原定向叶子：`CodingRiver.UPilot.Tests.UPilotRealUnityRecoveryTests.ProductionReceiveLoopDoesNotBlockOrReplayRealUnityMutation`，权威结果 **1/1 passed**，终态与清理成功；没有把证据检查数写成测试数。
- 原 runGuid：`42cf9e9f-bab1-4218-877a-d6af108fed30`。full、持久 run、status、poll 投影及资源释放已核对；持久结果位于规范工程 `Library/UPilot/TestRuns/<runGuid>.json`。
- 原编译：WriteBatch `wb-04afa71c-6126-47e5-876f-352a855b94eb`，compileOperationId `a247b1d158ad42e18bc4c0c5178514f1`；writeBatchCreatedAt `1791510706480`，lastCompileVerifiedAt `1791510721889`。completed/terminal/errorsVerified，0 error / 0 warning，Domain 57→58。该编译 Reload 不等于活动命令跨真实 Reload。
- 历史环境：Windows / Unity 6000.6.0a2，Unity PID 23796；隔离生产 Server peer PID 119452、端口 5194，共享 Server 历史 PID 106268。均为原现场身份，不证明当前活体。
- 原 commandId：`cmd-5a1c3e43-82dc-4690-a864-57f5b445721d`；probe：`cmd-4ee7d19e-6c9f-4ed8-bf9b-767c13bca3ed`；有序 inspect 屏障：`cmd-6ba5e753-43d6-47e6-97cf-0e7fce6e00ac`。
- 原现场目录：规范工程 `Log/Stage4/RealUnityRecovery/production-receive-loop-response_lost-f6deef59d85f452ab2e2d5485d9af901/`。

### 已核验的行为

- 真实 ClientWebSocket、Windows 进程身份、生产 `ReceiveLoopAsync`、router 和 `EnqueueTracked` 路径；Bridge/router/queue/tracker 为自有隔离对象。
- 原 mutation 已排队、尚未 pump 时，probe 已返回 `effectCount=0 / queueDepth=1`，证明该 ReceiveLoop 没有等待主线程业务完成。手动 pump 后，自有主线程 mutation 的 `effectCount=1`。
- 原命令仅发送一次，响应丢失后保留 `failed / CONNECTION_LOST`；完整 `frozenOriginal == finalOriginal`。迟到 `effectCount=999` 结果在有序 inspect 消费屏障之后仍未覆盖原终态，也未重复执行 mutation。
- wire 84 行 / 10 个完整帧与身份、顺序、真实 probe 完整封套一致；自有 loops/handlers/queue/tracker 收尾、peer 自然退出，stdout/stderr 为真实零字节文件。
- 65 份输入的 bytes/SHA256/mtime_ns 已独立重算且不变，包含原现场、当前八份源码、持久 run、核验脚本及保留的 v1 失败证据。源码与冻结 before/after、fixture/peer 快照一致，但不因此宣称共享 Server 全模块加载鉴证完成。

### 独立核验报告与保留失败

- 核验目录：规范工程 `Log/Stage4/v01-production-receive-loop-20261009T014937Z/`。
- `verification-v2.json`：status=passed，**1032/1032 项证据检查、65 份输入**，error=null；160725 bytes，SHA256 `3365d09ec1ece25fcc2299063161130a24830611d438753dfae73d83d00322ce`。核验时间 UTC 2026-10-09 05:48:28.908973 至 05:48:29.094973。
- `verify-v2.py`：37251 bytes，SHA256 `d5ac387b1f7d5e6fe6e45379965244510bc0a05209746992b7c0d166b8a7aba9`。本次归档仅读取和核对该报告与原输入，没有再次执行核验器或产品测试。
- 原 `verify.py` / `verification.json` 保留不变。v1 为核验器预期目录清单漏列真实 `receive-probe-result.json` 而失败（2 项检查，1 通过 / 1 失败 / 0 输入），不是产品单叶失败。v2 补列真实文件并增加 wire/envelope 一致性断言，没有弱化产品标准。
- v1 脚本：35461 bytes，SHA256 `cec57f37a4f7835ec1bdb4ed861c6796a4a10225f6a832a0db4f17724331bcb2`；v1 报告：7709 bytes，SHA256 `734ff0577ed8512fd65adf33aef2c49dade7720c59f2f9f92ba6c759e778c884`。

### 覆盖限制与当前余项

- `productionReceiveLoopCovered=true`，但 `realDomainReload=false / productionMainThreadPumpCovered=false`。使用自有 uninitialized Bridge、router、queue、tracker，**手动 pump**；没有运行生产 `ProcessMainThreadQueue` 或共享 watchdog。
- 隔离 peer 的历史六方法 liveCodeMatchesFrozenSource=true 加当前 hash/AST 核对，不是独立重建历史 CodeType 等价，也不是共享在线 Server 全模块加载来源鉴证。
- 原 timeout=10000 来自原 report，wire 没有 timeout 字段，不补造线上期限字段。只读 SQLite 完整性、九表零行、无副文件仅证明隔离/schema/integrity，不证明 CommandRecord 持久落库。
- Console 顶层无匹配日志，testExecution/cleanup 仍 partial：`console_cursor_clear_or_reload_not_detectable`；保持 `observed_during_run_not_causal`。保存的 queue/Capture 和 OS 数据只属历史观察（OS：UTC 2026-10-09 02:10:45.2191112），不证明当前空闲或活体。
- V01 剩余：活动命令跨真实 Domain Reload、生产 ProcessMainThreadQueue 与共享 watchdog、其他独立实机故障组合及共享 Server 全模块加载来源鉴证；不外推所有工具 exactly-once。

### 本轮归档改动范围

仅替换根 TODO 的 V01 表行并追加本节，其他条目及本文件原字节前缀不改。原文档备份和更新核验保存于规范工程 `Log/Stage4/v01-production-receive-loop-closeout-20261009T055145Z/`。未改产品/C#，未登记 WriteBatch、未编译或重跑测试、未操作 Capture、未连接或重启服务。父项仍 46，V01 保持 P0 开放，V10 不恢复，Tooltip/N05 保持 P5，两个新增候选不并入父项统计。


## 2026-10-09 P0/P1 开发准备续记（未开始集中验收）

按“全部本轮增量开发准备完成后再验收”执行。V01/V04/D19 自有现场脚本及直接回归代码已准备；D16 添加 Failed + 可信 Capture/Editor cleanup 首错/终态冻结叶子；真实 Reload guard 仅 Windows/精确规范工程，非目标明确 Ignore。详细范围与受控场景前提见 `P0P1-Plan-20261009.md` 第9节。本节不把历史工作树全部归为本轮成果。

- 关联唯一 C# WriteBatch：`wb-e893b04a-17cf-4988-9ecf-b363a5643876`，createdAt=1791542126010，compileOperation=`dc8d46753c7e4ea58e07469f5923e24c`，verifiedAt=1791542143651；terminal/errorsVerified/correlationVerified 全真，0 error/0 warning。
- live structured errors=0；关联 Console Error 无匹配但 partial/domain_reload_boundary，不宣称跨 Reload 全量 Console 无错误。
- Python AST 静态检查，不等于 pytest；未运行本轮 NUnit/真实 Reload/Capture 现场。开发编译 domain62→63 不能充当活动命令 Reload 验收。
- Server equivalent 仍仅 selected-entry-code，不证明 globals/closure/dependencies 或全模块。V01 command 权威 deadline/send count 不由 helper 期限/文件次数推导。
- 根 TODO 更新开发检查点，不删除或关闭父项：46（P0=1/P1=15），N01完整模块仍独立排期、V19发布仍需单独授权，Tooltip/N05=P5不变。


## 2026-10-09 P0/P1 集中定向验收续记（父项未全部完成）

P0P1-Plan-20261009.md第1–9节和本文件既有开发准备记录保留当时状态，本节记录之后实际发生的验收，不将“当时未验收”改写为已通过。先完成本轮明确承诺的脚本/直接测试准备，随后集中定向验收；验收发现错误后只最小修正并复验受影响叶子。N01完整模块不在已开发交付范围，不能把这次增量交付称为全部P0/P1完成。

### 10.1 Python结果：首次失败保留，不累计成虚假的全绿总数

| 批次 | 首轮结果 | 最小修正与复验 |
|---|---|---|
| Reload/Capture/来源客户端及D16新增叶子 | 46 passed / 1 failed | D16测试误将持久记录视为公开terminal投影；核对endedAt，保留公开terminal与首错断言。单叶1/1 passed |
| 来源/有限加载/cleanup/恢复直接回归 | 56 passed / 1 failed | D19测试误用新生命周期的活动loader读取历史；坚持load=[]，改为隔离SQLite只读历史核对。单叶1/1 passed |
| Capture路径直接复验 | 6 passed | 客户端按已核验精确PROJECT解析project-relative exportPath，仍限制自有attachment目录；不改Server/Capture产品协议 |

五份JUnit XML保存在规范工程Log/P0P1：python-incremental-20261009.xml、python-d16-recheck-20261009.xml、python-direct-regressions-20261009.xml、python-d19-history-recheck-20261009.xml、python-capture-path-recheck-20261009.xml。各批存在交叉，不把批次数简单相加当去重测试数量。两条既有websockets弃用warning保留，未借机改依赖。

### 10.2 Unity定向及编译

- Reload guard：5/5 passed，runGuid=129e3a8d-59d6-4b22-b1db-5b3df85a0569。只证明精确工程/无副作用guard，不证明真实Reload。
- 阶段fixture三叶＋共享生产pump一叶：首轮3 passed / 1 failed，runGuid=2c5b5c86-de1b-4928-915b-711da5a9bc1b。共享生产自然pump/no replay、phase终态冻结、共享watchdog叶子通过。
- 失败叶ProductionEditorPumpPublishesQueueAndExecutionPhasesOnce：CurrentStep预期“owned production pump fixture”，实际“完成”。源码Complete(summary)随后AddStep("完成",summary)，summary在末尾Step.Detail，属于测试字段假设错误，未证实产品缺陷。
- 只改Tests/Editor/UPilotCommandPhaseTests.cs：CurrentStep/末尾Step.Step均“完成”，末尾Step.Detail核对summary；保留queued→executing→completed、一次effect，并检查下一帧CompletedAt与Steps.Count不变。失败单叶复验1/1 passed，runGuid=19085807-9bb7-416b-8896-173dc9207b96。
- 上述三次运行terminal/resultAuthoritative/cleanupSucceeded/resourcesReleased均真，unresolved=[]、skipped=0；不改首轮失败历史，不写成首轮4/4。
- 第9节开发批次wb-e893b04a-17cf-4988-9ecf-b363a5643876仍保留；本次修正新增一个批次wb-06b3f12f-ad00-472b-83f7-72e2722ad4cd，createdAt=1791543125017，compileOperationId=2b49558d4983445989fb00c01acb018a，requestId=req-6cbbaa8f-4575-4b63-bedf-be6a515160b7，verifiedAt=1791543141267，verified/passed，terminal/errorsVerified/correlationVerified均真，0 error/0 warning。
- 当前CommandPhaseTests.cs SHA256=7b3d4755abfdae9945ad4eb7830dfc852da9b3cb4ba6f984664eb0f2086ca2ef；关联live structured errors=0。关联Console无匹配但partial/domain_reload_boundary，不声称全量Console无错。
- 本批只登记一次自动WriteBatch，未重复manual sync/compile。开发编译domain62→63与修正编译63→64均不是活动命令跨Reload现场；本任务更早编译另有历史记录，不声称全天只有这两次编译。之后无C#/程序集变化，本次归档不再编译。

### 10.3 V04及D19真实现场

V04三次独立场景均observed：
- ownership：v04-capture-cad2241222404bbabe0c86f7695e10e8.json，349067 bytes，SHA256=41442b1830a4dc7ed473e5c22e701145d2865ca9dab52a73f11e87f706f50b24。
- empty：v04-capture-acf5af7ed03641e291ea5e5eed45e1a0.json，173884 bytes，SHA256=329c0fbb9a0eb4280bcee9661c1aec195d12d103819b8921399d621406d60ae2。
- rotate：v04-capture-bece89bdec3b401faa0fd9433d4a0c17.json，3179293 bytes，SHA256=668ededa06d9d45be59e26dcbb9891783ccb1d2e9d51b5361d1ff9935a95ae98。

已验证独立HTTP A/B、B只读attach/read/detach、缺/错token停止拒绝且A仍活动、A精确合法停止、明确成功后的合法重复停止、冻结attachment范围和导出、真实零字节JSONL与真实至少1MiB轮转。再次独立重算13份实际文件bytes/hash，不输出ownerToken。首轮v04-capture-a3d22db3218a492fbf06f50bcaba7dc5.json客户端路径解析失败保留；只对已合法收尾后的新nonce复验，不回写或再次操作旧session。

D19：d19-origin-62ecf77f4ce64e93bf8bac774d81f850.json，321278 bytes，SHA256=c2777808434a815f3edf3359f69d236e18630163355687d8eb664fe93f9a6801。两独立客户端、三自有Task，创建来源冻结，A断连后B观察原Task全部终态；不是安全权限隔离，也没有制造真实blocker、晚到请求竞态或直接Capture/WriteBatch/Bridge来源。

### 10.4 真实Reload本次未通过：原操作终态已知，场景证据不完整

- 单次原Arm，nonce=a4223ffe66ea498eb4489a1de13460b5，reflectionOperationId=reflection-op-3ad34640-d09b-4cd2-8742-17f5a7fdb241，commandId=cmd-cc3ab691-a23d-4a30-ae23-28baef7560d5；不重发原Arm，不续期限。
- 原脚本报告result=unknown、exit=1，v01-reload-a4223ffe66ea498eb4489a1de13460b5.json，1137103 bytes，SHA256=b81e69638cc31256b57d45f2732197d378444835f0346d410cc983610efdb3cb，保持不变。
- 同报告及后续只读原身份已有terminal=true/status=Failed、AWAITABLE_TIMEOUT：“Awaitable did not complete within 3000ms”。外层operationTimeoutSec=45不替代内层awaitTimeoutMs；本次脚本没有显式传后者。
- 实际目录只有armed.json和effect.json（一次文件副作用）；缺before-reload.json/after-reload.json，权威domain仍64、Bridge session仍8036108a4af047a198e46ac5506d3dfb。未证实RequestOwnedReload为何没形成实际Reload，不能仅按超时判产品恢复缺陷，也不因文件effectCount=1宣称Server发送次数为1。
- command tracker的completed表示该命令已发送错误并完成处理，不等于反射业务成功。首错及原Failed保持，不能用队列空把本次场景改为通过。
- 后续最小开发建议：先局部核对并明确awaitTimeoutMs与helper的30秒固定见证关系；在客户端报告分别记录originalTerminal和reloadEvidence，不让缺见证掩盖已知Failed，也不重复60次查询同一冻结失败。复用现有helper，不加恢复框架、定时器或生产故障开关。delayCall/实际Reload未发生的原因仍待证据；确认根因与前提后才安排独立新现场，不重放本次原动作。
- helper.deadlineAt、effectCount不能替代原Server命令权威deadline/send count；完整加载与独立故障矩阵仍开放。

### 10.5 最终只读归档与剩余范围

- exact D:\upilot\Tests~\UPilotTest、Unity6000.6.0a2、Unity PID23796/creation134358983013306077、Server PID44992、producerEpoch46c655b676144c4bafbc5181145bf518。Server equivalent仅selected-entry-code-only，不包含全模块/globals/closure/dependencies。
- 最终只读报告final-readonly-99a720ba2898471399344fc93e689648.json：120650 bytes，SHA256=147a3fcad3c7ad39763aff15588b14a6993d4577dfcb71cf02119d6a6526b111。保留原run完整无cursor结果、原批次/live errors/关联Console、原反射终态和原command tracker；队列complete=true/items=[]、Capture activeCount=0仅属该时点观察。
- 失败叶复验完整结果unity-19085807-9bb7-416b-8896-173dc9207b96.json：12735 bytes，SHA256=fdf6f2985a740c4909abeaeea04575327d377cc968fe707b782bf2c39628b53c。
- 旧development-inputs-20261009.json不覆盖；另存final-inputs-20261009.json，明确十份增量/修正输入，2692 bytes，SHA256=4aef9e48c71c5c08f29045efc556a806e8b9ca98209dcb9e8e8f1a8f1dc2edfa，不把所有dirty tree归为本轮成果。
- closeout-hashes-20261009.json：10691 bytes，SHA256=10668d56e3a988ceb4a5ead1818a574b8727e0fb4807f2f628f1eb3cc7cfb9b6；只读核对10份原报告、13份实际Capture文件、五份JUnit XML及十份输入，不是新增产品测试或全模块鉴证。
- 尚未完成：V01真实活动命令跨Reload及权威期限/发送次数证据；D19真实blocker/旧请求竞态；V04旧Operation完整收尾、真实Reload连续性/缺片/物理失败；D03/V02/V03/V05/V06/V09/生命周期各自未覆盖实机；D15原故障异常链；D04/N11及共用完整加载证明、原生Emit不可用；N01完整模块；V19受控真实发布。现有通过矩阵不重复跑，不把没有授权/缺平台现场伪装成交付。
- 不全量EditMode、不使用外部C#编译器、不启动通用Operation/Step、不维护或发布、不操作其他工程。根父项仍46（P0=1/P1=15），Tooltip/N05=P5不变。


## 2026-10-09 命令事实诊断开发核对及P0/P1完整方案更新（尚未验收）

- 本回合接续上一轮6份Python输入，只做基于before备份的源码审查和AST解析；6/6可解析，未import/pytest/NUnit，不是6项功能验收通过。没有C#改动，不触发Unity编译；没有在线部署刷新、Reload、Capture、维护或发布。
- 新产品代码范围：state_store.py/dispatcher.py/server.py/domain/task_service.py；测试范围：test_session_identity.py及新增test_command_observation.py。原树中其他差异不归为本回合。
- 最小回执复用现有operation_get：serverCommandObservation不会把Unity错误改成功，也不包含payload/结果/所有权秘密。原记录不存在或旧字段未记录仍unknown/None，不合成历史事实。
- responseWaitDeadlineAt是原发送返回后的响应等待；sendAttempts计socket.send入口，包含该入口抛错，不证明网络送达或Unity执行，不等于整个命令/反射operation/inner-awaitable期限。终态记录拒绝再次发送，原首错和已完成记录冻结。
- 上一轮编辑脚本遇到task_service.py的CRLF匹配假设失败，随后保留该文件换行完成编辑；这是编辑脚本错误，不是产品自动化失败。
- 六份开发输入已冻结于Log/P0P1/command-observation-development-20261009T192839/development-inputs.json，SHA256 7bd620ccfb99728b17b89ea642b8052ade3b47b9e50d963d0b380fd450ef8c3b；before备份均重新核对hash。新增测试注明原先不存在。在线Server是否加载此次Python差异仍未验证。
- 前一开发回合的Python49/原WriteBatch wb-6fa71bad-9f0e-42ea-b949-a95ed4e56f31/guard run ae2dc8d8-7a6f-42d8-8169-311453bb5627属于继承记录，不是本回合新跑，尚未以本回合新增磁盘原MCP回执证明；不重跑或把开发编译当真实活动命令跨Reload。
- P0/P1完整简化方案已追加到P0P1-Plan-20261009.md第11节，覆盖16项并区分必要开发与特定现场。N01保持P1且未开发，不再默认延期后称“全部开发完成”；当前全范围冻结门槛未满足，不开始新一轮集中自动化验收。其余历史通过/失败、46父项及P5 Tooltip均不变。

## 2026-10-09 V01采集链开发续记（不算本轮验收）

- `p0_reload_evidence.py`新增自有Arm身份核对后的单次`unity_operation_get`采集，先保留Server命令事实再验证完整Reload见证。来自失败detail的事实可保留，不修改原Failed；缺原记录/旧字段/ID生命周期scope错配/观察异常明确unknown，None事实单列缺口，不重发Arm。
- 既有加载清单新增两个相关入口，仍只证明selected-entry-code-only。测试代码已补，四份Python仅AST解析及静态diff核对，未执行pytest/NUnit或现场。开发输入`Log/P0P1/reload-observation-development-20261009T194909/development-inputs.json` SHA256=`ebb0aca2c970be426eb4ad816debca3ec8586fc9be6ed37fa9d0d06f21bce058`。
- 没有C#改动，所以无新WriteBatch/编译；旧关联编译、49 Python与guard5/5仅保留历史，不重复执行。在线Server loadedSource仍different，未刷新服务。
- Reload探针已静态核对请求/结束见证及反射异步等待链，旧真实失败根因未确认。N01只读类型发现形成候选而非实现/可用性证明，未执行编译或加载。完整P0/P1开发尚未完成，未开始新一轮集中验收，TODO父项不关闭。


## 2026-10-09 V04 Reload前待写日志的最小开发补丁（未集中验收）

- 静态核对确认：既有 Capture 从 `session.json` 恢复活动会话，但只在 Editor update/读取/停止时刷新内存 Pending；没有 Reload 前刷新回调。当前修复仅在 `EnsureSubscriptions` 注册一个去重的 `beforeAssemblyReload` 回调，调用现有 `FlushActiveCapture(true)`。不停止 Capture、不改 ownerToken、sessionId、nextSequence 或恢复协议，不加定时器、重试、框架或依赖。
- 回调的额外异常只记录有界错误类型，不向自身 Console Capture 递归写日志、不阻断其他 Reload 回调。不把一次刷新当作整个 Reload 区间无日志缺口的证明；无法写入 manifest 时也不承诺错误可在新域读取，仍属真实故障现场缺口。
- 在既有 `UPilotConsoleCaptureApiTests` 写入两个直接回归：未经过 Editor update 的待写记录落盘、重复刷新不重复记录、隔离清空 active 后按原 session/owner/sequence 恢复；自有 JSONL 独占锁的写失败保留 lastError/droppedCount，保持活动且不抛出。隔离恢复 seam 不是真实 Domain Reload，文件锁不是物理磁盘故障。测试尚未执行。
- 首次关联 WriteBatch `wb-a503525b-a5e0-4238-b91d-5fc56a493131`，compile operation `2d3298c1136b431ead5b1a927f5e2d40`，createdAt=1791547430207，verifiedAt=1791547452589；terminal/errorsVerified/correlationVerified 均为 true，0 errors、3 warnings（未改动的 Flow Models.cs 第386/407/496行 UAC1009，不外推为本补丁新增）。关联 Console 查询未返回 Error，但 coverage=partial/domain_reload_boundary，不声称完整 Console 无错。
- 本轮输入 before/after、局部 diff、编译字段投影归档：`Log/P0P1/capture-reload-development-20261009T200344`；`development-inputs.json` SHA256=`ee37e2e3efbd55e281079b2ed102fc546a8001b48b022a06ec5be53a349dd13e`。编译字段投影不是完整原 MCP 回执。C#关联编译引起的域变化65→66不是自有 Capture 真实 Reload 验收。
- P0/P1 全范围仍未开发完成；N01仍计入P1且能力边界待确认，V01/D19/V04及其他现场缺口保持开放。未启动 pytest/NUnit、双客户端、Capture、维护或发布；不关闭 TODO 父项、不处理P5 Tooltip。

- 最小字节校正：首次编辑脚本曾将测试文件原有73行裸LF转换为CRLF；已从本回合before原字节重建，只保留新增测试，并核对移除新增字节后与before逐字节一致。产品文件无该问题。最终局部diff为85行新增、0行删除/改动原行；第一次格式状态保留在 `formatting-checkpoint/`，不抹去历史。
- 最终保存字节对应第二批 WriteBatch `wb-bd88be44-1d2b-42dc-a652-da5817612b66`，compile operation `88a694ffe67c4aefacc1f092acdbca56`，createdAt=1791547686144，verifiedAt=1791547700359；terminal/errorsVerified/correlationVerified=true，0 errors/0 warnings。此增量编译不证明首次记录的Flow警告已修复。关联Console Error查询返回0，仍为partial/domain_reload_boundary。第二批编译字段投影单独保存为 `final-compile-evidence.json`，不是完整原MCP回执，不覆盖首次记录。
- 最终输入以 `final-after/`、`final-development.diff`、`final-development-inputs.json` 为准；最终manifest SHA256=`94123719ad2d6be28e3e4d337c3fa05bb75b18992810a71c9bdd199cd360ecd7`。源码SHA256：Capture service=`55b771a64ca9487448c9db624573e83a120e8274773c9d7091d7bd01031b2944`，API tests=`b018d43ae124402ae5719d4c5b2f7f67a62491b96ac4e8c3e2a9b2ce94a39639`。域66→67仍为开发编译，不算自有Capture跨真实Reload验收；未执行自动化测试，不关闭父项。

## 2026-10-09 D19精确阻塞任务的最小开发补丁（未集中验收）

- 已定位一个具体缺口：`TaskDomainService.task_start` 的 `TEST_TASK_ALREADY_ACTIVE` 已按当前工程的活动持久Task拒绝启动，却只返回泛化提示，没有回传导致拒绝的原Task身份。本轮只将既有any判断改为取得第一个实际满足原条件的记录，并在原错误detail中增加 `blocker.type/id/status/requestOrigin/action` 与只读核对/明确授权取消提示；原错误码、拒绝条件、Task创建和持久化均不变。
- blocker只证明该Task满足当前test/acceptance准入互斥条件，不推断任意底层引擎的阻塞因果；action=cancel是既有可请求动作，不证明已授权或取消必然成功。来源取原记录的有界自声明快照，不取查询者的标签，不泄露token，不自动取消、重试或增加聊天租约。
- 直接回归代码覆盖四个活动状态×已知/未知来源、连续拒绝、修改响应不影响原对象、无新Task/派发/持久化副作用；另覆盖已完成、其他工程及非持久Task不被误当阻塞。本轮只AST解析和局部diff核对，**未执行测试**。
- 两个Python文件的before/after/diff归档：`Log/P0P1/blocker-development-20261009T201631`；输入manifest SHA256=`fc85d0c21250a6edba97142919146fa436b88839e17a9aa2ad71b1c0b66a8dd4`。既有加载清单已含task_start，不扩鉴证机制。本轮无C#改动，无新WriteBatch/编译；在线Server没有刷新，不能声称已加载这次改动。
- 剩余开发/现场分开：N01能力边界及完整模块仍未开发；V04真实Reload采集模式仍未准备完整，已有隔离刷新测试不替代真实Reload；D19实际双客户端blocker/旧请求竞态仍待集中验收。P0/P1全部开发门槛仍未满足，不启动新pytest/NUnit/现场/维护/发布，不关闭父项，46项/P0=1/P1=15/P5 Tooltip不变。


## 2026-10-09 V04真实Reload采集模式开发（尚未执行）

- 只扩展既有 `p1_capture_evidence.py`，新增 `--mode reload`，要求已有 `--execute` 加显式 `--allow-reload`；不新增产品工具、配置开关、调度框架、依赖或C# helper。ownership/empty/rotate继续沿用原路径，不重跑其历史已通过现场。
- 执行前要求两个独立客户端观察同一精确规范工程、fresh authoritative EditMode、已知Unity PID/creation/Server PID/producerEpoch/domain及selected-entry加载相符；共享队列或现有Capture不为空时拒绝。专用Reload工具发现后拒绝绕过；仅在确认没有专用工具且实际已加载的零参数 `UnityEditor.EditorUtility.RequestScriptReload` 存在时，允许一次既有reflection入口请求。开发阶段没有执行上述发现或请求。
- 请求前重新核对进程/服务身份，记录原自有Capture已刷新的sequence边界；只发送一次Reload请求，回执丢失仅保存unknown并观察，不重发。观察最多30秒且不超过原110秒执行/收尾预算；Bridge session变化可以是重连，Unity PID/creation、Server PID、producerEpoch变化则不当作同一生命周期的Reload。
- 新域fresh ready后核对原session继续active且sequence不倒退，追加唯一自有marker；复用原B只读attachment的detach/冻结导出、A原token停止及成功后的重复停止、实际磁盘manifest/summary/JSONL/导出hash与分页核验。前后marker的唯一性和sequence先后都检查。只证明本次观察到的域变化及原Capture连续性，不证明Reload请求因果，也不证明整个Reload未订阅区间没有遗漏环境日志。
- 无可靠changed-domain见证时仍保留businessError/unknown；finally最多在原五秒总收尾预算中重新只读核对一次身份，确认原进程与服务仍一致才合法停止一次自有Capture，身份未知或已变则不派发旧收尾、不forceStop；明确成功后的重复停止只用于原合法重复停止矩阵。不续期、不接管其他Capture、不把清理成功改写为Reload成功。
- 在既有fake双客户端/真实MCP函数签名绑定夹具中新增6个测试函数、11个参数叶子：正常Reload、回执丢失、重连观察缺口；双客户端身份不一致、专用工具存在、Reload入口缺失的前置拒绝；服务换代、sequence倒退；缺显式opt-in；原预算已到期；缺域见证后只在fresh同生命周期下收尾。fake只证明将要测试的协议分支，不是实机Reload证据。**这些测试尚未执行**。
- 本轮两份Python文件只AST解析和局部diff核对；无C#/程序集改动，无WriteBatch/新编译；无pytest/NUnit、网络现场脚本、Capture、Reload、维护或发布。before/after/diff归档 `Log/P0P1/capture-reload-client-development-20261009T202503`，代码输入manifest SHA256=`dddc3217ddc1635bc36c1a00ccce5bd972bfe5c81525690dc42eb3bad05774e1`；与继承dirty工作树分开记录。未刷新在线Server，不声称此前D19产品补丁已部署。
- V04状态为“Reload模式与直接测试已开发，真实连续性待集中验收”；不关闭父项。N01完整模块仍开发未完成且保留P1范围，全部P0/P1开发门槛尚未满足，继续禁止启动新整体集中验收；父项46/P0=1/P1=15不变，Tooltip/N05继续P5。


### 2026-10-09 D19精确blocker采集模式开发续记（未执行验收）

- 沿用既有双客户端来源脚本新增opt-in blocker模式；一个已有纯选择测试叶子/两个单次start，精确核对原Task及创建来源。B被接受的竞态、未知回执和错误拒绝保留unknown，不重放、不取消、不中止其他工作；已收到的自有ID沿固定预算只读观察。
- 新增7个函数/22个参数测试叶子，绑定实际MCP函数签名；只AST解析和局部diff检查，未执行测试或现场。无C#改动/WriteBatch/编译、Capture/Reload/维护/发布，在线Server没有刷新。不是真实blocker通过，也不是旧请求竞态或全模块加载证明。
- 本轮before/after/diff及manifest：`Log/P0P1/blocker-client-development-20261009T204204`；manifest SHA256=`6349c49412fb2c2c5e666c4debc40849094b9834e5662af89326b1c2f94720ba`。完整输入和边界见 `P0P1-Plan-20261009.md` 第11.13节。
- D19产品补丁/采集模式/直接测试已开发，现场仍未验；父项不关闭。N01完整模块仍未开发且保留P1，未满足全部16项开发门槛，不开始新整体集中验收，46项/Tooltip P5不变。


### 2026-10-09 N01完整模块初版开发续记（未启动新集中验收）

- N01从“未形成实现”更新为“独立初版、关联项目编译及验收准备已开发，真实支持待验”，仍计入P1，不沿用旧排除范围宣布全部开发完成。历史记录保留。
- 完整能力/限制、精确执行身份、60/30秒固定观察、同名revision绑定、登记资源收尾、非卸载/非强停边界见 `Documentation~/DiagnosticModules.md`；产品入口不改变Eval/Emit、不重启Unity、不开放通用Operation，不新增依赖。
- 保留三批编译：Assembly别名及CallbackFunction类型两个首次失败均未改写；最终 `wb-91ce062a-0015-49ec-836e-25ffc3d531a2` / `c268c00dd1134d119f7dc1be39d0302c` verified/passed、0错误/3既有Flow UAC1009警告，Console为partial/console_cleared。无C#后续修改，本次只读观察原批次，不重复编译。
- 新增N01现场脚本与直接fake测试，修正ulong wire-ID、模块回执身份、异步候选失败分支、session与对象实际清理、首错/cleanupError及独立产物路径/hash；只做静态检查，不执行脚本/import/pytest/NUnit。在线两个新selected-entry清单尚未核验，不把健康或旧清单equivalent当加载证明。
- 归档 `Log/P0P1/n01-development-20261009T205910` 记录限定scope、before/after、局部diff/hash、三批编译投影；完整进展见P0P1方案11.14。N01父项仍开放，其他16项门槛须逐项核对；未切PlayMode、未运行独立模块、Capture、Reload、维护或发布。46/P0=1/P1=15/P5 Tooltip不变。


### 2026-10-09 P0/P1开发冻结核对续记

开发准备清单见P0P1方案11.15：16项均有明确结论，N01六文件AST/局部归档/三批原回执已补齐，相关五份C#注册hash未变；保留两次首次编译失败。V09新增四个真实链接边界测试叶子，尚未执行。开发准备允许进入集中定向验收，不是父项全部完成。当前新增N01两Server入口不在实际加载清单，现场及加载缺口仍开放；未额外编译、维护或发布。


## 2026-10-09 P0/P1集中定向验收续记

- 首轮Python200通过/2失败，原报告保留；失败为N01 proxy测试夹具未建立隔离写授权，非绕门禁产品修复。仅测试内monkeypatch并补未授权零派发，受影响文件40/40复验通过。V09四个真实symlink/junction叶子全部通过，无skip。
- 规范Unity25/25（N01 23＋Capture Reload flush 2），runGuid `01ce8063-490d-4bcb-8239-90a5ee5a8a8a`；清理/身份/源码未变通过，实际summary 661799 bytes/hash已复算，复用末批关联编译。
- 有效独立维护授权且队列完整空/Capture=0后单次维护 `5251ca70-bc21-4867-a0a5-66d8e9d2211e` 已成功，Server132696→110840，Unity23796/domain68/EditMode不变。新增N01及相关命令/Task入口selected-entry equivalent，不宣称全源码/依赖加载证明。归档 `Log/P0P1/n01-proxy-fixture-20261009T220403/`，细节见方案11.16。
- 单元与有限加载通过不关闭真实现场父项；仍46父项/P0=1/P1=15，P5 Tooltip未碰。


## 2026-10-09 P0/P1真实Reload、精确blocker与Capture续验

- V01单次真实活动命令Reload取得before/after见证；domain69→70、同Unity/Server生命周期、一次effect/sendAttempts及原终态冻结已观测。原采集报告failed，恢复后stale拒绝与后续只读observed分列，不覆写、不再次Arm、不把原Failed/outcome=unknown写成业务成功。见证resourcesReleased=false，完整收尾/端到端期限/Unity执行总次数仍缺证据。
- 局部夹具update/解绑修正关联编译verified，0错误/0警告，guard5/5；Python恢复后复用既有只读ready观察修正44/44，未再真实Reload或额外编译。D19真实blocker=自有A Task/running/self_declared/action=cancel，B正确拒绝、A断连后1/1终态完成；未实际取消。
- V04真实domain70→71、Capture序列3→8/marker8，双客户端附着/范围冻结/导出/错误token拒绝/合法重复stop及三实际文件+export的bytes/hash核对通过。6次观察缺口保留，不声明Reload订阅缺口全部日志完整。N01root只读调用通过但loadedAssemblyCount=0，实际PlayMode Build/revision仍待验。
- 最新只读工程出现另一个新WriteBatch的UPilotCoreTests.cs:1137 CS0029（ulong[]→long[]），terminal/errorsVerified/correlationVerified=true、CompileErrors阻塞；最新selected-entry摘要different，旧有限加载证明不覆盖当前变化。队列完整空/Capture0不是编译成功。本续记无产品/测试代码改动，无测试/编译/维护/模式切换。
- 对应报告名、runGuid、编译身份、覆盖限制及当前完整16项最小方案见 `P0P1-Plan-20261009.md` 11.17/12；文件实读hash清单 `Log/P0P1/documentation-closeout-20261009T223758/verification-inputs.json`。保持46父项/P0=1/P1=15/P5 Tooltip，不提前结项。


### 2026-10-09 23:14 P0/P1 N01 开发追加（非验收结论）

当前N01拒绝调查已区分Unity安装自带工具链与项目配置：项目RoslynAnalyzer标签为空，前12个程序集的6项均来自当前安装；全98去重因预算停止，实际Builder内部读取被Eval策略拒绝，未绕过/Build。最小开发已补安装路径/链接核验、观察路径/hash及加载前复核，不删除项目配置；同时修正PlayMode删除后MarkSceneDirty导致“对象已删但回执异常”的局部收尾。直接测试代码先保存，关联WriteBatch wb-00238ac4-efc3-405d-be16-a0bc5cd54dd5；说明/脚本与代码冻结后集中验收。详见P0P1方案11.18和Log/P0P1/n01-toolchain-fix-20261009T231234/。此时不声明新修复编译或测试通过，不关闭N01/V01父项，Tooltip/P5不处理。


### 2026-10-09 N01新增15叶验收终态核对（保留原失败）

原task-760e8baa-ae08-4778-b4e1-441b767919a1已failed；runGuid e7a874da-2332-40c5-8808-fec6efb46683，13/15通过、2夹具失败、0跳过，cleanup/testIdentity/sourceUnchanged核验均true。两失败在additive场景创建处发生，不是产品helper的失败证明；安装工具链分类10叶全部通过。关联WriteBatch wb-00238ac4-efc3-405d-be16-a0bc5cd54dd5编译0/0已通过。原summary实际653886字节及SHA256 efbf402f44886705cbd03b0a98fa06f4fe4252c2cce008c6cc2263616dec1f58已独立复算。本次仅只读查询旧任务及更新说明/归档，不新启动自动验收、不改产品或测试代码。后续先完成最小夹具修正与开发冻结，再集中复验受影响叶子；真实N01矩阵及父项不提前关闭。详见P0P1方案11.19/12.4。

### 2026-10-09 N01当前组合真实矩阵、夹具两叶复验及独立产物核对


- 两个删除夹具只改为自有 `NewPreviewScene/ClosePreviewScene`，保留原断言，不保存或丢弃用户场景。WriteBatch `wb-fd8edead-fadd-49df-9c69-6ba288b6d976` / compile `98a3db56ca24484588edee14e844ca9a` 关联 verified/passed，0 error/0 warning。Console 关联覆盖 partial/domain_reload_boundary，不写成完整无错。
- 原 15 叶运行仍为 13 通过/2 夹具失败；另两次失败尝试保留。冻结后仅复验受影响两叶，runGuid `331319c0-a0b9-4779-bbbb-02cc471ba344` 为 **2/2 passed**，cleanup/identity/sourceUnchanged 均通过。Preview scene 证明 valid Scene/PlayMode guard，不是已保存资产持久化验收；已通过的工具链10叶没有重跑。
- N01 自有 PlayMode 现场 **实际 AssemblyBuilder 构建并运行 v1/v2/v3 通过**：同名入口按各 Assembly 绑定；旧 update 解绑；相同源码复用；异步执行期间替换被 MODULE_BUSY 拒绝，继续观察原 invocation；原对象保持；module/session close 无 cleanupError，异步活动归零，自有对象精确删除。
- 58 次调用只执行一轮现场脚本；现场期间 Unity PID/creation、Server、Bridge、producer/domain 不变。仅证明本次 Windows / Unity6000.6.0a2 组合。所有 Server 模块加载、source/reference 篡改现场及其他组合未覆盖；物理 DLL 卸载、任意代码强停、Analyzer/Generator 支持不是本次承诺。
- 原报告实际 5,157,317 bytes / SHA256 `86ce72750e031d53b5d780953547b2bc96039948e75a60172978654b22800664`；两叶 summary 实际 602,002 bytes / SHA256 `b849de24d9e984461683390b3fa1ca9670939c4935b4148055b904461e084021`。再次独立读取三份源码及 DLL，六个实际 SHA256 全部与回执一致。精简核验见 `Log/P0P1/n01-current-matrix-verification-20261009/verification.json`，不复制5MB报告。
- 已恢复进入前 EditMode。最终只读核对精确规范工程，fresh/authoritative/ready；queue complete=true、items=[]；Capture activeCount=0。没有维护重启、重复编译、重复 Arm 或新增验收。

V01原活动命令真实Reload已证实；after的resourcesReleased=false是恢复见证未赋值，不能单独证明产品泄漏。整命令总预算、派发/执行次数及完整收尾仍开放，详见P0P1方案13.2；不再Arm原失败现场。最终工程fresh/authoritative EditMode，完整队列为空、Capture=0。本次文档收尾不重复编译或验收，不关闭其余父项。


### 2026-10-10 P0/P1本批冻结后集中验收收尾

- 必要开发先冻结，Python首轮106/108，保留ready分类和夹具时序两失败；最小修正重新冻结后只复验受影响两叶和新增cap回归，3/3通过。没有把首轮改写全绿或重跑已通过矩阵。
- 六文件沿用唯一关联WriteBatch `wb-946bbf39-48b9-4be0-a14a-ea9d64fa9958` 编译verified/passed、0/0；无新增C#写入。本批Unity新5叶run `5c3d31bd-db13-4f5b-bd19-e4a1873d7c43` 5/5、权威终态及清理通过，Reload后公开结果/磁盘原记录一致。首个只读discovery误用matchMode=exact被拒绝、未启动Runner，按union契约修正后单次启动，拒绝证据保留。
- 独立授权、精确项目/完整空队列/无Capture确认后，仅一次Server/Bridge维护 `8f24c47e-a6d1-4dc3-a73c-ae6a18163592` succeeded；Unity PID23796不变，Server115220→111196，所选入口加载equivalent，不代表全模块。
- 新nonce `aa44059333c749cb8f20ced367023c27` 一次Arm，真实domain83→84、sendAttempts及当前invocation三入口计数均1、effectCount=1；自有witness resourcesReleased=true/endedAt有效，五文件实际bytes/hash一致。原 `reflection-op-d90e2442-369d-4f1c-b294-193dc72f4040` 仍Failed/outcome=unknown，不重放、不改旧报告。observed仅表示采集和不重放/冻结/见证收尾验证通过，不是被中断操作成功。
- 独立核验 `Log/P0P1/v01-development-20261010-adf13f3d/acceptance-verification.json`：12283 bytes、SHA256 `25a2add091d55421959dd8e7ea12709e4fa37fc2749c9bdb0ffefd0b2ef1c331`；14输入、5 witness、12份产品/测试/脚本与最新冻结一致。run Console错误0、总边界complete，但阶段边界partial；编译Console跨Reload仍partial，不宣称全范围无错。
- 本批开发及定向验收交付，V01已验证三个子项移出待验；16父项未全关闭，活动46（P0=1/P1=15）、Tooltip P5保持。其他故障组合/全模块加载/任意中断资源停止、N01篡改现场、真实取消/人工与升级/物理IO/真实发布仍按原边界开放。最终fresh/authoritative EditMode、完整空队列、Capture=0。完整当前方案和结果见P0P1方案第15节；未重复编译/启动通用Operation/切外部工程/发布。
