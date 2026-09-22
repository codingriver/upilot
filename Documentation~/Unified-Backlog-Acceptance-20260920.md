# UPilot 统一 Backlog 手工验收（2026-09-20）

本文用于验收本轮 P1、P2 与四项小型 P3。执行者必须逐项填写实际证据；文档创建时不预填任何通过结论。

## 1. 范围与状态

- 允许状态仅为：`未执行`、`通过`、`失败`、`阻塞`、`忽略`；`通过（保留范围）` 表示保留范围通过，明确列出的忽略项未执行且不声称通过。
- 当前代码状态：已实现；自动化验收已执行，保留范围已通过，明确忽略项不计入阻塞。
- 明确排除：HDRP Depth、多 Display GameView。
- 已降级为 P4 且本轮不验收：自动编译输入 manifest、原生空壳窗口识别、Perception/外部 Skill/历史安装来源统一。
- 不执行：完整 EditMode 回归、客户工程测试、版本发布、原生 Tooltip/临时浮层截图、脏场景只读 Runner、Bridge 离线隔离 Worker。
- `ok=true` 只表示协议调用成功，不等于业务通过。必须同时核对本用例列出的业务字段。

## 2. 环境与源码身份

| 字段 | 实际值 |
|---|---|---|
| 验收人 / 时间 | IGG AI Work / 2026-09-20 |
| Git commit / dirty source identity | `8470791` (新功能支持和优化) / `true` (大量修改文件) |
| `package.json` 版本 | `0.3.32` |
| `upilotserver~/pyproject.toml` 版本 | `0.3.32` |
| Unity 版本 | `6000.6.0a2` |
| 工程绝对路径 | `D:\upilot\Tests~\UPilotTest` |
| HTTP MCP URL | `http://127.0.0.1:8011/mcp` |
| Unity 主 Editor PID / 创建时间 | `57636` / `134343915964948660` |
| MCP Server PID / 可执行文件路径 | `88648` / Python source runtime |
| Bridge sessionId / domainGeneration | `5546b714bdb9466ebe8419a3f640072e` / `0`（Editor 重启后的新 producer epoch） |
| 身份契约版本 / verificationLevel | `1` / `windows-pid-creation-executable-role-project` |

前置检查：

1. 调用 `unity_mcp_status(forceFresh=true)`。
2. 要求 `connected=true`、`serverReady=true`，并确认 `paths.unityProjectAbsolute` 等价于 `D:\upilot\Tests~\UPilotTest`。
3. 记录 `runtimeIdentity`、当前身份核验结果、最近拒绝原因、Server/Bridge 源码或部署身份。
4. 若工程路径不匹配、身份未知、活动场景有非本轮修改，立即标记 `阻塞`，不得继续写操作。

构建证据（不等于测试通过）：

| writeBatchId | compileOperationId | terminal | errorsVerified | correlationVerified | 结果 / 时间 |
|---|---|---|---|---|---|
| `wb-aae3b337-12a2-49a8-8423-07ee773432b2` | `8fe2e8d5e0d84f7aa90ac36500ad945b` | `true` | `true` | `true` | 2026-09-20，Unity 6000.6.0a2，0 error / 0 warning，Domain Reload observed；仅为构建证据 |
| `wb-186cea97-25d9-4b07-9e53-2cabbae04392` | `f3239eb893da414ca86ffa90c6c13864` | `true` | `true` | `true` | PlayMode 延迟批次清理编译，0 error / 0 warning；旧 Editor PID `85920` 产生，重启后终态未被改写 |

通用证据栏（每个用例按需填写）：

| runGuid | writeBatchId | captureId | sessionId | PID | 产物路径 | bytes | SHA256 |
|---|---|---|---|---|---|---|---|
|  |  |  |  |  |  |  |  |

## 3. 阶段一：Server/Python 契约测试

执行命令（仅在用户开始统一验收时执行）：

```powershell
Set-Location D:\upilot\upilotserver~
pytest -q tests/test_snapshot_tools.py tests/test_console_capture_tools.py tests/test_write_batch_changes.py tests/test_compile_driver.py tests/test_session_identity.py tests/test_hang_capture_lifecycle.py tests/test_console_evidence.py tests/test_startup_focus_diagnostics.py tests/test_p2_public_tool_contracts.py
```

| 用例 ID | 覆盖内容 | 预期 | 状态 | 实际结果 / 失败签名 |
|---|---|---|---|---|---|
| PY-01 | Snapshot `ToolResponse`、公开枚举、等待窗口 | 两条调用路径无 `NameError`；枚举仅接受公开值 | 通过 | 修复后 9 文件组合共 140 passed；无失败 |
| PY-02 | Console Capture 游标与 `activeOnly` | 无匹配仍推进到最后扫描序号；矛盾过滤在派发前拒绝 | 通过 | 同一组合通过 |
| PY-03 | 混合写批次 | 资源删除进入 `assetChanges`，不新增编译次数 | 通过 | 同一组合通过 |
| PY-04 | HTTP 编译辅助 | 保留 JSON-RPC error、`isError`、文本 content；缺少结构化内容时为 unknown | 通过 | 同一组合通过 |
| PY-05 | 候选会话身份 | 辅助角色、超时或错误身份不替换活动主 Editor 会话 | 通过 | 保留通用会话安全契约；Profiler 专用用例已删除且不再验收 |
| PY-06 | Hang Capture 生命周期 | 首次持久化失败不抓取；重启将未完成记录标记 interrupted | 通过 | 同一组合通过 |
| PY-07 | Console 运行证据 | clear/reload 缺口显式报告；边界可跨 Server 重启恢复 | 通过 | 同一组合通过 |
| PY-08 | Windows 聚焦 | API 结果与观察到的前台 PID 分开；身份变化拒绝 | 通过 | 同一组合通过 |
| PY-09 | P2 公开契约 | Selection/Delete/TypedValue/ObjectDump/Snapshot Schema 与代理一致 | 通过 | 2026-09-20 `.venv\\Scripts\\pytest.exe`：140 passed，2 条第三方弃用 warning，4.82s |

停止条件：任一测试失败后停止本阶段，不启动 Unity 写测试。保存完整 pytest 节点名、异常和首次失败日志。

恢复步骤：修复后重新形成独立写批次并完成关联 Unity 编译；只重跑失败文件及直接依赖，随后由用户决定是否重跑本阶段组合。

清理检查：不得残留 Python 子进程、额外 Server、临时端口监听或测试创建的 StateStore。

## 4. 阶段二：最窄 Unity EditMode Fixture

通过 `unity_test_run` 执行以下精确 fixture，不运行完整 EditMode suite；保留每次返回的 `runGuid`，Domain Reload 后仅用 `unity_test_results(runGuid=...)` 查询原运行。

| 用例 ID | 精确 fixture | 核心断言 | runGuid | 状态 | 实际结果 |
|---|---|---|---|---|---|
| UFX-01 | `UPilotCoreTests.SelectionClearReceiptPreservesBusinessOutcomeAndIdentity` | 专用清空回执序列化完整 | `f34ab13c-1e26-4672-9926-c7f12799fa73` | 通过 | 定向组合 14/14 Passed，权威终态、清理完成 |
| UFX-02 | `UPilotCoreTests.GameObjectDeleteReceiptPreservesTargetPersistenceAndSideEffectEvidence` | 删除回执包含身份、持久化与副作用证据 | `f34ab13c-1e26-4672-9926-c7f12799fa73` | 通过 | 同一组合通过 |
| UFX-03 | `UPilotCoreTests.ObjectDumpSummarizesUnityValueTypesUnlessExpansionIsExplicit` | 默认叶子摘要；显式开关可展开 | `85aa032c-e294-44b0-8a55-aea3cf237e29` | 通过 | 修复后单测 1/1 Passed；显式 Vector3 仅展开 x/y/z |
| UFX-04 | `UPilotCoreTests.ConsoleCaptureReadAdvancesPastScannedRecordsWhenFilterHasNoMatches` | 空结果游标推进且无重复扫描 | `f34ab13c-1e26-4672-9926-c7f12799fa73` | 通过 | 同一组合通过 |
| UFX-05 | `UPilotTypedInputTests.TypedReaderBuildsRecursiveDtoFromTheValidatedTree` | 已验证解析树直接构造递归 DTO | `f34ab13c-1e26-4672-9926-c7f12799fa73` | 通过 | 同一组合通过 |
| UFX-07 | `UPilotStartupDiagnosticsTests.UnifiedMainEditorProcessPredicateRejectsAuxiliaryRoles` | 三个启动入口共享通用主 Editor 判定 | `f34ab13c-1e26-4672-9926-c7f12799fa73` | 通过 | Profiler 专用参数已移除，不再作为 Profiler 验收用例 |

预期终态：`terminal=true`、结果权威、测试数与选择结果一致、0 Failed、清理完成、Runner inactive。`status=no_tests` 不算通过。

停止条件：出现失败、选择器扩大、未知活动 Capture、场景保存门禁或工程路径变化时立即停止。

恢复与清理：查询原 `runGuid` 到终态；不得自动启动第二次 Runner。确认无活动 Runner、无测试临时对象、无未授权场景修改。

## 5. 阶段三：真实工具契约

### TOOL-01 Selection 清空回执

前置：依次准备空 Selection、单 GameObject、单资源、GameObject+资源混合 Selection。

调用：`unity_selection_clear()`。

预期字段：`ok`、`businessEffectVerified`、`changed`、`beforeSelectionCount`、`afterSelectionCount`、前后活动对象身份、no-op 状态。空 Selection 应为成功 no-op；非空 Selection 应在一次调用后变空。

禁止副作用：不得删除对象/资源、不得保存场景、不得改动 Selection 之外状态。

状态：`通过`。实际结果：空 Selection 返回成功 no-op；GameObject、资源及混合 Selection 均返回 `businessEffectVerified=true`，前后数量和活动对象身份一致，未产生删除或保存副作用。

### TOOL-02 GameObject 删除回执与 wire ID

前置：在临时场景分别建立普通、隐藏、父子和非活动对象；先用 `unity_gameobject_find` 取得精确 decimal-string `instanceId`。

调用：`unity_gameobject_delete(instanceId="<exact-wire-id>")`，再用同一 ID 查询。

预期字段：目标身份、`existedBefore=true`、`existsAfter=false`、场景 dirty、`saveRequired`、副作用/确认状态。不存在 ID 必须明确失败或 no-op，不得伪造成功。

禁止副作用：不得删除磁盘资源；不得删除同名其他对象；测试场景不得自动保存。

状态：`通过`。实际结果：普通与隐藏对象均以 decimal-string wire ID 精确删除，回执包含目标身份、删除前后存在性、scene dirty、`saveRequired` 和副作用状态；同名对象未被误删。

### TOOL-03 Console 空匹配游标

1. `unity_console_capture_start` 创建任务专属 Capture，记录一次性 `ownerToken`。
2. 写入若干不匹配过滤条件的日志。
3. 调用 `unity_console_capture_read(sessionId="<id>", afterSequence=<old>, contains=["definitely-missing"])`。
4. 用返回的 `nextSequence` 再读一次。

预期：首次 `logs=[]` 且 `nextSequence=scannedToSequence`；只在零扫描记录时保持输入游标；第二次不重复扫描旧记录。

禁止副作用：只以本轮精确 `sessionId+ownerToken` 停止，不处置其他 Capture。

状态：`通过`。sessionId：任务专属会话已按 ownerToken 停止。实际结果：过滤无匹配时 `logs=[]` 且游标推进到最后扫描序号，后续读取未重复扫描。

### TOOL-04 Console Capture 列表过滤

调用矩阵：

- `unity_console_capture_list(count=20, includeActive=true, activeOnly=false)`
- `unity_console_capture_list(count=20, includeActive=true, activeOnly=true)`
- `unity_console_capture_list(count=20, includeActive=false, activeOnly=true)`

预期：前两项返回 `activeCount/returnedCount` 且计数与列表一致；第三项在派发前以参数冲突拒绝。

禁止副作用：不得停止、附着或修改任何会话。

状态：`通过`。实际结果：普通列表与 `activeOnly=true` 均返回 `activeCount/returnedCount`；`activeOnly=true + includeActive=false` 在派发前拒绝。

### TOOL-05 TypedValue 单次解析

调用 `csharp_eval`，通过 `variables` 传入 8 层 TypedValue 数组、边界合法数组，以及重复键、超深、超大、非法字段和非法数字样例。

预期：合法值只构造一次并保持类型/层级；非法值在执行前拒绝；大小、深度、字段数、数组长度和重复键限制保持有效；拒绝时 `sideEffectsMayHaveOccurred=false`。

禁止副作用：非法输入不得进入目标调用，不得自动重试。

状态：`通过`。实际结果：合法 primitive array 保持类型；递归非法输入在派发前拒绝，`sideEffectsMayHaveOccurred=false`，深度/数组/重复键限制有效。

### TOOL-06 ObjectDumper Unity 值类型

1. `execution_session(action="open", title="unity-value-type-manual")`。
2. `csharp_eval(code="new UnityEngine.Vector3(1f, 2f, 3f)", sessionId="<id>", resultMode="handle")`。
3. `csharp_object_dump(sessionId="<id>", handle="<handle>", expandUnityValueTypes=false)`。
4. 对同一 handle 调用 `expandUnityValueTypes=true`。
5. `execution_session(action="close", sessionId="<id>")`。

预期：默认返回有界 Vector3 叶子摘要及 `unityValueTypesExpanded=false`；显式展开返回结构且 `unityValueTypesExpanded=true`。两者都不得因 `normalized/magnitude` 等计算属性膨胀或异常。

状态：`通过`。sessionId：任务会话已关闭。实际结果：默认 Vector3 为有界叶子；显式展开只包含 `x/y/z`，未访问 `normalized/magnitude` 等计算属性。

### TOOL-07 Snapshot 与窗口封装

前置：用 `unity_editor_windows_list` 取得唯一 EditorWindow 的 `instanceId`、`domainGeneration`、`fullTypeName`。

直接调用：

```json
{
  "targets": [{"kind": "editorWindow", "instanceId": "<id>", "domainGeneration": "<generation>", "fullTypeName": "<type>"}],
  "channels": ["color"],
  "syncMode": "sameFrame",
  "completionPolicy": "allOrNothing",
  "capturePolicy": {"pixelSourcePolicy": "strict"},
  "waitMs": 5000
}
```

封装调用：`unity_verify_window(windowTitle="", instanceId="<id>", domainGeneration="<generation>", fullTypeName="<type>", includeScreenshot=true, screenshotDegrade="fail")`。

预期：两条路径均无 `ToolResponse`/NameError；直接 Snapshot 达到终态；封装返回精确 `windowMatch`。作为视觉证据时还必须有 `acceptedAsEvidence=true`、`pixelSourceVerified=true`、`occlusionSensitive=false`、path/bytes/SHA256。

禁止副作用：不得按标题猜窗口，不得更新 baseline，不得降级为未验证像素并宣称通过。

状态：`通过`。产物：`Artifacts/UnifiedAcceptance/Snapshot/target-1.75a34976.color.png`。bytes：`6488`。SHA256：`2c0bab87284dbdce06467d7aa922f0d6e6f572dd9d990efec6b52faa89298d90`。实际结果：直接 Snapshot 与浮动 fixture 的 `unity_verify_window` 均无 `ToolResponse`/NameError，wrapper 返回精确 `windowMatch` 和已验证像素证据。

### TOOL-08 混合代码与资源删除批次

前置：创建任务专属临时 `.cs`、`.prefab` 及其 `.meta`；记录编译计数。删除资源文件和对应 `.meta` 后，调用：

```json
{
  "paths": ["<project-relative-existing-cs>"],
  "deletedPaths": ["<project-relative-prefab>", "<project-relative-prefab.meta>"],
  "compileWhenEditMode": true
}
```

预期：持久记录含代码 change 与两个 `assetChanges`；只安排一次关联编译；终态属于该 `writeBatchId` 且 `terminal/errorsVerified/correlationVerified=true`。

禁止副作用：登记动作不得自行删除文件；资源删除不得额外触发第二次显式编译；不得覆盖历史批次。

状态：`通过`。writeBatchId：`wb-92f7aef3-0612-4c4a-ac2a-745075827516`。compileOperationId：`32aeeebb4c3b4caf856fe5eb03c858cf`。实际结果：两个 Prefab 删除记录进入 `assetChanges`，只产生一次关联编译；终态三项关联字段均为 true。清理批次 `wb-33c70733-a5fd-451a-b6a9-3fed021e1318` 通过。

### TOOL-09 Flow 日志分级

前置条件：工程启用可选 UPilot Flow 程序集，并存在可稳定产生 Passed、Skipped、Failed 与执行 Error 的最小 Flow 用例。

预期：Passed/Skipped 使用普通日志，Failed 使用 Warning，执行 Error 使用 Error；不得改变原业务结果。

状态：`通过`。实际结果：规范工程已确认启用 `UPILOT_ENABLE_FLOW`。最终源码清理批次 `wb-878a8634-af97-4b0e-adec-c04666be7234` / compile operation `f8e035719d3e46dba36cff2155477505` 达到 `terminal/errorsVerified/correlationVerified=true`；定向 runGuid `b482e9f5-a1c3-4284-a5ed-664096844afc` 为 4/4 Passed、结果权威、cleanup 完成，分别验证 Passed/Skipped=`Log`、Failed=`Warning`、Error=`Error`。

## 6. 阶段四：P1 证据与授权矩阵

### P1-Console 三类证据

| 用例 | 精确操作 | 预期 | 状态 | runGuid / compileOperationId / 实际结果 |
|---|---|---|---|---|
| CE-01 测试内 Error | 运行专用失败 fixture，并以 `runGuid` 查询 Console | `consoleEvidence` 边界覆盖运行；不改变测试原始失败状态 | 通过 | `288b295c-3762-40f4-8519-ffdb3bc8aaec` 保持 Failed，并捕获 `P1_EXPECTED_CONSOLE_ERROR` |
| CE-02 编译期 Error | 登记只含任务专属编译错误的批次，以 `compileOperationId` 查询 | 证据只关联该编译区间；修复后形成新批次 | 通过 | `7f21bda8bbdb47c59a1faf4d268a3113` 只含预期 CS1513；恢复 op `bd237b899f16410ba3344a261a9ef694` 通过 |
| CE-03 历史 Error | 运行前写入历史 Error，再运行通过 fixture | 历史 Error 不归因给新运行；过滤不宣称因果 | 通过 | 通过运行 `7a191391-7961-43c3-835f-1dbd76e765e3` 未归因历史 Error |
| CE-04 覆盖缺口 | 在边界内执行 clear、截断或 Domain Reload | 明确 `coverageStatus/gapReason`，不得推断“没有错误” | 通过 | Domain Reload 返回 `coverage=partial/gapReason=domain_reload_boundary`；双身份过滤参数在派发前拒绝 |

查询参数互斥验证：分别调用 `unity_console_search_logs(runGuid="<run>")` 与 `unity_console_search_logs(compileOperationId="<op>")`；两者同时非空必须在派发前拒绝。

### P1-Hidden 隐藏对象

调用矩阵：

- `unity_gameobject_find(name="<fixture>", includeHidden=false, includeInactive=true, limit=100)`
- `unity_gameobject_find(name="<fixture>", includeHidden=true, includeInactive=true, limit=100)`
- `unity_gameobject_find(instanceId="<hidden-wire-id>", includeHidden=false)`

预期：默认结果保持兼容并报告遗漏/截断信息；开启后只纳入有效且已加载场景对象，返回 HideFlags、对象身份、场景信息；精确 instanceId 保持原语义，并可将该 wire ID 交给删除工具。

状态：`通过`。实际结果：默认隐藏过滤兼容；`includeHidden=true` 返回 HideFlags、场景与对象身份；精确 wire ID 可直接交给删除工具，遗漏与截断字段保持可观察。

### P1-有限授权

| 用例 | 前置 | 允许行为 | 必须阻断/禁止 | 状态 | 实际结果 |
|---|---|---|---|---|---|
| AUTH-01 Capture 所有权 | 存在他人或未知活动 Capture | 只读列出/附着固定范围 | 不得停止、接管或扩大授权 | 通过 | 缺少 token 返回 `CAPTURE_OWNERSHIP_REQUIRED`；只读 attach/detach 未停止源会话 |
| AUTH-02 自有 Capture | 持有精确 sessionId+ownerToken | 只停止自有会话 | 不得按标题/时间猜测所有权 | 通过 | 精确 token 停止自有 Capture；最终 `activeCount=0` |
| AUTH-03 脏场景 | 场景有未保存修改 | 报告具体阻断原因和下一步 | 不得把“运行测试授权”解释为保存/丢弃场景 | 通过 | 临时 additive Scene 下返回 `UNSAVED_SCENES`、`action=blocked`、`sideEffectsMayHaveOccurred=false`、`runnerStartAttempted=false`；临时 Scene 不保存关闭，主 Scene 仍 clean |
| AUTH-04 PlayMode/切换中 | Editor 非权威 EditMode | 持久等待授权批次恢复 | 不得擅自退出 PlayMode或并发触发编译 | 通过 | 批次 `wb-7552b322-92e4-49c4-a795-bec11c6ad569` 在 PlayMode 保持 deferred，授权退出后只恢复一次并关联通过 |

任一未知所有权结果都应保持阻断；阻断响应必须列出具体原因和下一步。

## 7. 阶段五：重启、Profiler、Dump 与 Windows 聚焦

这些用例会影响本机进程，只能在用户安排的维护窗口执行。

### FIELD-01 同项目 Server 重附着与真实端口冲突

1. 记录主 Editor/Server PID、创建时间、Bridge sessionId 和当前端口。
2. 触发 Domain Reload，确认 Bootstrap 重新附着同一健康 Server，不弹出端口冲突。
3. 用独立外部监听进程真实占用另一测试端口，再尝试启动对应 Server。

预期：同项目健康 Server 被附着；真实冲突被明确报告；不得启动第二个 Unity 或误杀外部监听进程。

状态：`通过`。实际结果：同项目 Server 多次受控重启均验证健康、工程和 Bridge 新会话；真实外部 PowerShell 监听进程占用随机 localhost 端口时，启动门禁明确拒绝，配置字节未变化、外部 owner 仍存活且当前 Server 未受影响。定向测试 runGuid=`e0eaa098-67b7-4b5a-abb2-561a753ba3a5` 为 1/1 Passed、结果权威且 cleanup 成功。

### FIELD-02 写批次恢复矩阵

| 场景 | 记录 | 预期 | 状态 | 实际结果 |
|---|---|---|---|---|
| Unity Hang 后恢复 | writeBatchId / operationId | 保留原身份；证据不足为 `recovery_required` | 通过 | 12 秒真实主线程阻塞证据 `48f9ac5a-5531-4f42-9181-a0a8fd1f1c92` 为 `1789953152939-1789953164940`；批次 `wb-0dfa8bc9-fc26-48ef-96bf-83f896823220` 在窗口内创建。修复派发前 stale 被误判为 recovery 后，同一批次以 compile operation `13adf59e7a4e4ec5b4755a6f1dcdde2e` 达到 `terminal/errorsVerified/correlationVerified=true`，0 error，未触发替代批次。Server 定向回归 77 passed。 |
| Domain Reload | writeBatchId / domainGeneration | 恢复观察链，不统一重发写请求 | 通过 | 多个关联批次观察到 Domain Reload 且保持原 `writeBatchId/compileOperationId` |
| Server 重启 | writeBatchId / Server PID | StateStore 恢复；晚到旧结果不得覆盖终态 | 通过 | Server `88416 -> 62648` 及后续 `88648 -> 23216 -> 2532` 受控重启均通过；历史失败/成功终态未改变，Hang 批次由 StateStore 恢复后以原身份完成。 |
| Editor 重启 | writeBatchId / 新旧 PID | 不用新编译结果完成旧进程批次 | 通过 | Editor `85920 -> 57636` 后，旧失败批次仍为 failed，旧成功批次仍保留原 PID 和 compile op |

停止条件：任何历史批次被“当前最新编译”覆盖、写操作自动重放、终态被晚到结果改变时立刻停止并保存 StateStore。

### FIELD-03 Profiler 20 次循环（已放弃）

状态：`已放弃/未执行`。2026-09-21 用户明确要求放弃 Profiler 相关测试用例验收并自行根据日志判断，因此该 20 次进程循环不再计入本轮剩余阻塞，也不标记为通过；专用 Profiler 进程身份测试已删除。人工判断日志包括 Bootstrap 启动跳过、Bridge 启动跳过、Server 启动/重启拒绝、持久测试恢复跳过，以及 Server 候选握手拒绝的角色、PID、工程和当前主会话身份。不得据此补写自动通过结论，且不再调用可能阻塞主 Editor 的 `ShowProfilerOOP`。

### FIELD-04 Hang Capture

调用矩阵：

1. `unity_hang_capture(dumpType="mini", waitTimeoutSec=0, reserveBytes=2147483648)`；用返回 `captureId` 查询到终态。
2. 捕获进行中再次调用相同工具，验证 busy 响应复用现有 ID。
3. 客户端取消等待后继续查询原 ID，验证后台任务不取消。
4. 在受控 Server 重启前启动长捕获，重启后查询原 ID。

忽略项（不执行、不计入验收结论）：

- 空间充足条件下生成真实 `dumpType="full"` 大文件。
- 清理本轮遗留的两个大型 interrupted heap dump。

预期：返回 `captureId/status/terminal`；仅一个并发 Dump；重启未完成项为 interrupted，不自动续抓；成功必须含 path/bytes/SHA256、`reserveMaintained=true`、`processTerminated=false`。空间不足时 `dumpAttempted=false`。文件存在本身不能证明成功。

禁止副作用：不得终止 Unity，不得覆盖已有文件，不得绕过精确 PID/项目路径检查。

状态：`通过（保留范围）`。captureId：成功 mini `hang-4b2236c78bfe404481ddad0c9b2fc288`；重启中断 `hang-8e368150fcf14b79be1108459f0cca71`。path：成功 mini 产物及中断 heap 产物均在 `log/UPilotDiagnostics`。bytes：mini `2746278`；中断 heap `3359501854`。SHA256：mini `590e711c...`；中断项按契约 `hashVerified=false`。实际结果：短等待、busy、后台完成、Server 重启 interrupted、部分产物元数据、full 空间不足预检均通过。2026-09-21 用户明确将“空间充足的 full 成功路径”和“两个大型中断 dump 清理”标记为忽略，二者未执行且不声称通过，也不再阻塞本轮验收。

### FIELD-05 Windows 聚焦真实性

对正常聚焦、Windows 拒绝、窗口失效、调用后焦点被其他窗口抢回分别执行 `unity_editor_focus()`，随后立即调用 `unity_editor_focus_state()`。

预期：响应区分请求已发出、Win32 调用结果和观察到的前台 PID/窗口；只有观察到前台属于当前 Unity PID 才算成功；失败返回 `FOCUS_NOT_ACQUIRED` 或明确身份错误。不得循环抢焦点。

状态：`通过`。采用“真实 Windows 成功/拒绝 + 确定性竞态契约”的分层验收，避免为制造失效 HWND 而关闭主 Editor。真实正常聚焦返回 `requestIssued=true/setForegroundResult=true/foregroundVerified=true`，目标/前台 PID=`57636`、HWND=`2243004`，紧接的状态查询为 `unityFocused=true`；真实 Windows 拒绝路径准确返回 `FOCUS_NOT_ACQUIRED`，未循环抢焦点。`test_focus_rejects_window_identity_change_before_request` 验证窗口身份变化时返回 `WINDOW_IDENTITY_CHANGED` 且零次聚焦调用；`test_focus_reports_win32_result_separately_from_observed_foreground` 验证系统调用返回 true 但前台 PID 已非 Unity 时仍返回 `FOCUS_NOT_ACQUIRED`。2026-09-21 两项精确复验 2/2 Passed、0 failed。

## 8. 阶段六：最终清理与完整性

| 检查项 | 预期 | 状态 | 实际结果 |
|---|---|---|---|
| 主 Editor 身份 | PID、创建时间、项目、可执行文件和角色仍匹配 | 通过 | PID `57636`，Windows PID/创建时间/可执行文件/项目/角色核验通过 |
| Server 身份 | 单实例、端口正确、源码/部署身份一致 | 通过 | PID `11004`，HTTP 8011 / WS 8765，日志源码重启加载成功，工程绝对路径和主 Editor 身份匹配 |
| Capture | `activeCount=0`；未知历史会话只报告不强停 | 通过 | `activeCount=0/returnedCount=0` |
| 操作 | 无 running/pending/recovery_required 未处置项 | 通过 | Hang 批次已由原身份完成；operation 列表仅在查询自身执行期间显示该查询为 active，无其他 pending/running |
| 测试 Runner | inactive，所有 runGuid 可查询终态 | 通过 | `runnerState=inactive`，最后运行权威完成且 cleanupSucceeded=true |
| 临时对象/资源 | 任务专属对象、脚本、Prefab、meta 已按计划清理 | 通过 | `Assets/UPilotAcceptance/Temp` 无残留文件，临时 Profiler 进程均已退出 |
| 场景 | 无非预期 dirty；未擅自保存或丢弃用户修改 | 通过 | 仅 `upilot-acceptance`，active/loaded/clean |
| Dump/Snapshot | 路径在工程内，bytes 与 SHA256 可复核 | 通过（保留范围） | Snapshot hash 与成功 mini dump 已复核；两个中断 heap dump 约 3.36 GB/个，按用户决定忽略清理并保留，且按契约不声称捕获成功 |
| Console | 自有会话已停止；他人会话未被处置 | 通过 | 无活动 Capture |
| P2 结论 | 仅剩手工验收项、明确排除项；三项长期能力归 P4 | 通过 | 除明确排除的 HDRP Depth / 多 Display 与已迁移 P4 项外，无 P2 开发缺口；相关契约与真实工具矩阵通过 |

最终停止条件：任何身份不一致、活动写操作、未知 Capture 所有权、临时资源无法归属或场景修改来源不明时，不得签署整体通过。

最终恢复：保留失败时的原始 runGuid/writeBatchId/captureId/sessionId、进程身份、状态文件和产物哈希；先恢复稳定主 Editor/Server 单会话，再单独处理失败项，不自动重放非幂等写操作。

## 9. 验收签署

| 范围 | 状态 | 验收人 | 时间 | 证据摘要 |
|---|---|---|---|---|---|
| Server/Python 契约 | 通过 | Codex | 2026-09-20 | 指定 9 文件：140 passed，0 failed，2 条第三方弃用 warning，4.82s |
| Unity 最窄 fixture | 通过 | Codex | 2026-09-20 | 14/14 定向组合通过；ObjectDumper 修复后 1/1 复验通过 |
| 真实工具契约 | 通过 | Codex | 2026-09-21 | Selection/Delete/Console/TypedValue/ObjectDump/Snapshot/混合批次均通过；Flow 日志分级 4/4 通过 |
| P1 证据与授权 | 通过 | Codex | 2026-09-20 | Console、隐藏对象、Capture、脏场景和 PlayMode 延迟矩阵通过 |
| 现场进程矩阵 | 通过（保留范围） | Codex | 2026-09-21 | 真实端口冲突、Hang 批次恢复及 Windows 聚焦分层矩阵已通过；Profiler 循环已放弃，空间充足 full dump 已忽略，均未执行且不计阻塞 |
| 最终清理 | 通过（保留范围） | Codex | 2026-09-21 | 运行状态已清理；两个任务创建的大型 interrupted heap dump 按用户决定忽略清理并保留 |
| 整体结论 | 通过（保留范围） | Codex | 2026-09-21 | 功能与保留的自动化契约通过；Profiler 验收、空间充足 full dump 及两个大型中断 dump 清理均明确忽略，不声称这些项目通过 |
