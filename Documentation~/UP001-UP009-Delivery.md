# UP-001～UP-009 实施记录

日期：2026-09-07～2026-09-08。工作区实现，未提交、未打 tag、未发布，未修改客户业务代码。
根 `TODO_UPilot.mcd` 是唯一活动状态源；本文件记录接口、验收证据与尚未交付的部分，不另建活动 TODO。

## 实施边界

| ID | 已落地 | 仍需闭环 |
|---|---|---|
| UP-001 | 删除前路径/GUID、删除后 AssetDatabase/文件/meta 验证；Server 成功契约；不重试；失败注入测试 | 已通过最终定向验收，根清单完成/次数 0 |
| UP-002 | `allowFallback` 严格像素门禁、精确映射；2022.3 浮动窗口移动/遮挡 20 次、失败注入 20 次与停靠 SceneView 捕获通过 | 已忽略剩余跨 DPI、停靠遮挡与 Reload 组合验收；非全部通过 |
| UP-003 | 统一清理屏障；3 秒实际延迟退出、Runner Reload 与 capture 清理；停止失败/陈旧/并发/取消契约通过 | 已完成，次数 0；通用 Server 重启持久化仍归 UP-013 |
| UP-004 | 受控验收 workflow、强制摘要、源码 C→R 证据链、必选 fixture 实际匹配、摘要 SHA/bytes/7 天有效期 | 已忽略剩余受控 runner/远端演练/双稳定版本图形 CI；不解除已有发布门禁 |
| UP-005 | 四类实际来源、Runner 自动退出、权威快照透传、真实 requestId/operationId；三端同 transition 与防串用通过 | 已完成，次数 0；未知来源仍明确 unknown |
| UP-006 | 按规则需求位图构建上下文、延迟 GlobalObjectId、缓存类型/表达式、入口预算；重装与所有权防护 | 已忽略剩余性能基准、在途延迟拆卸及原生稳定性专项；未实现/崩溃风险仍保留 |
| UP-007 | 精确实例与注册式定位；2022.3 同名窗口/重复按钮/实际滚动区各 20 次回调计数通过 | 已忽略第三方无接入自动识别（仍未交付）；保留 unsupported |
| UP-008 | 短 Task/watchdog、单目标 sameFrame 和 Editor 主线程续体；最终 10/10，含低层/Snapshot 各 10 次 SceneGUI 像素、超时/关闭、真实 Reload、Camera/GameView 直接回归 | 原定单 SceneView 范围已完成，次数 0；额外多目标 SceneView 混合同帧矩阵已忽略，不宣称通过 |
| UP-009 | 四类 Actions 固定完整 SHA、Node24 版本注释、离线 workflow 契约测试 | 已忽略剩余 runner/EXE/缓存/警告及远端演练；非远端验收通过 |

以上“已落地”不等于整项验收完成。2026-09-07 使用 alpha `6000.6.0a2` 且 Python Server 未重载的限制保留为历史；2026-09-08 已在用户授权的 `Tests~/UPilotTest2022` / Unity `2022.3.62f2` / HTTP 8017 加载候选 Python 并完成下列真实调用。8011 原项目未主动重启或运行测试，物理 Hook 未安装；单个 2022.3 项目不等于双稳定版本图形 CI。

## 调用契约

### Operation 清理

在已有 jobSpec 上添加：

```json
{
  "cleanup": {"requireEditMode": true, "timeoutSec": 30},
  "failOnUnexpectedPlayModeExit": false
}
```

`requireEditMode` 默认 `false`；开启只是授权等待，不授权 UPilot 停止 PlayMode。项目自身必须退出。
业务终态后返回 `businessTerminal=true/status=CleaningUp/terminal=false/endedAt=0`，保存 `businessResult` 与 `businessEndedAt`。
只有资源清理成功且必要的 Editor 证据晚于业务终态，才恢复业务结果并设置最终 `endedAt`。
超时为 `Failed/OperationCleanupTimeout`，`unresolvedResources` 列出未释放资源，原业务结果保持不变。
只停止 Operation 自建 capture；停止响应与 summary 的会话、终态、时间、分段数及哈希/bytes 必须一致。
按 Unity 的 `console.jsonl` 优先、其余名称忽略大小写排序，流式核验全部轮转分段的拼接 SHA256 与总 bytes；不能用最后一个 JSONL 代表整个会话。
产物验证被取消或超时时仍为 `stopped=false`。取消接口若已返回业务终态但项目清理未结束，也立即锁存业务结果并进入清理屏障；同毫秒的 Editor 快照不算晚于业务终态。
通用 Operation 仍不支持 Server 重启持久恢复，归属 UP-013，不因本轮屏障交付而宣称支持。

### PlayMode 来源

`UPilotPlayModeTransitions.RequestProjectExit(operationId)` 是通用显式项目入口。
ledger 写入 `Library/UPilot/playmode-transitions.json`，最多 256 条；跨 Domain Reload 恢复数据而不重放请求。
无法关联已登记意图时 `origin=unknown`，不根据 Console Error、焦点或窗口猜测来源。
Test Runner 在进入及 RunFinished 退出前登记来源，并把实际 runGuid 补入记录；结果更新只接受同 runGuid 的相关 transition。dispatcher 将原 requestId 和 Operation 上下文传给 test.run，Bridge 的权威 execution snapshot 传递同一 ledger 记录。陈旧/非权威快照不能触发非预期退出失败，项目切换清除旧项目来源。

### 窗口输入

鼠标/键盘传 `windowInstanceId`（`unity_editor_windows_list` 返回的 wire ID 字符串）。
指定实例不存在时失败，不改用焦点窗口。返回 `window:<domainEpoch>:<id>` 句柄可供反射使用；
窗口关闭或 Domain Reload 后失效，需重新列窗口，不自动恢复旧句柄。

自有 IMGUI 窗口在自身 `OnGUI` 的 Repaint 中注册：

```csharp
if (Event.current.type == EventType.Repaint)
{
    using (var frame = UPilotWindowInputRegistry.BeginFrame(this))
        frame.Add("停止", "Button", windowLocalRect, windowLocalClipRect, GUI.enabled);
}
```

矩形必须已转换为窗口局部坐标（包括滚动/GUI 变换）；注册必须来自目标 GUIView。
相同文字有多个匹配时需要 `elementIndex`，索引属于匹配集合，不是整棵树。
映射超过 1 秒、窗口几何变化、控件不可见/禁用时拒绝派发。
没有可靠映射时为 `IMGUI_LOCATOR_UNSUPPORTED`，不得把名称失败变为坐标 `(0,0)` 点击。
`input.dispatched=true` 只证明已派发，不证明业务效果，后者需回调计数或业务状态验证。

### 截图

禁止降级时，原生失败立即失败，不先调用 ReadScreenPixel 生成图片。
允许降级才返回遮挡敏感产物；`acceptedAsEvidence` 仍为 false。
`mappingEvidence` 保留目标矩形、ContainerWindow 矩形、PID 与有界 HWND 候选，便于诊断布局瞬态。
Windows LastError 在失败 API 后立即读取，不从 GDI 清理后的错误值推测原因。

SceneView 同步入口最多观察 4.5 秒，未结束返回原 `taskId`、`repaintPending`、`waitWindowElapsed` 和任务 `deadline`。
`deadlineSource=task-timeout` 与 `repaintDeadline=null` 明确区分任务观察期限和尚不可知的 Unity Repaint 时间。
继续查询同一 `unity_task_status`，不要重新启动截图。旧帧不复用，Camera 降级不含 Handles。
单 SceneView 的 sameFrame 基准取新 Repaint 后实际捕获帧，而不是排队时的请求帧；多目标同帧检查未放宽。Task 失败保留原 Snapshot failure code、snapshotId 和 manifestPath，不再只有 `logical failure`。

### Tracer

入口预算 `enableIngressBudget=false`、`maxIngressEventsPerSecond=1000`；面板可显式启用，独立 `IngressDroppedCount` 不混入过滤拒绝/过滤后限流。
类型/匹配表达式缓存有界，不缓存对象 Active/enabled 等动态状态。
Hook 安装/卸载操作串行；不同 owner 不自动顶替。缓存 patcher 在卸载后重新安装时会真正重新应用补丁。
原始字节或已安装补丁字节不符合预期时拒绝覆盖，并保持未知状态禁止重装。
这不是“任意线程在途调用安全卸载”保证；该剩余项和原生崩溃仍待独立验证。

## 发布证据流程

1. `UPilot Unity Release Evidence` 为可选增强校验；如运行，需在本仓库受保护 main 提交 C 上执行。self-hosted Windows runner 需 `upilot-unity` label、`upilot-unity-acceptance` environment、图形桌面、匹配 checkout 的规范项目及已加载候选 Server。`UPILOT_MCP_URL` 仅用于其 HTTP `/mcp` 连接。
2. workflow 运行指定 fixture，保存 `upilot-unity-release-evidence` artifact（summary.json、evidence.json）。缺摘要、零匹配、跳过、未清理、编译不可信、内容哈希错误均拒绝。
3. 发布授权任务必须提供 `version`；`acceptanceRunId` 可选。未提供时发布准备与 tag 构建仍执行合同、工具清单和源码一致性校验，但不执行 Unity evidence 门禁；若提供则必须来自受控 workflow，不接受任意本地文件替代受控 artifact。
4. 验收提交 C 与发布提交 R 相同，或 R 是 C 的单亲版本提交；只允许两个版本字段改变。完整源码哈希仍包含版本文件。若提供 evidence，tag 记录验收 runId，下游重新下载并核对同一证据链。
5. 当前工作区产生的 alpha/v1-source-summary 不可作为该门禁的 release evidence；需候选 Server 产生 v2 同源摘要后重新走受控 workflow。

版本提交检查保留完整 Git blob 字节，不忽略首尾空白、格式变化或文件 mode 变化。采集器观察超时、连接异常或取消时保留原 taskId，最多请求一次任务取消并有界观察清理；连接不可用时明确保存清理未验证，绝不重新发起验收。

本次未配置远端 environment/runner、未触发 workflow、未上传 Release。下游 artifact 获取使用 `gh run download`，没有引入未固定 SHA 的 download action。本地环境缺少 PyInstaller，未执行 EXE 构建；离线 workflow 契约通过不能替代缓存、runner 版本和打包产物的实际演练。

## 验收记录

- 初始资源定向：runGuid `2a3c2f97-032e-4950-8ec6-48bbbbec2367`，3/3。
- 窗口契约首轮：runGuid `3b129618-8957-494b-9244-646adb260fb4`，3/3（20 次精准点击与 20 次禁止降级）。
- 联合首轮：runGuid `ae88a926-8aab-49cc-be0b-472fbf434e3f`，29/29；`Tests~/UPilotTest/Log/UPilotAcceptance/1788776800927_req-dcd60e92-8376-4f19-90e9-70ebbf7aef14/summary.json`，86845 bytes，SHA256 `0d2dd997c02c38f4e202246d340192c11ca468eb13b4b9b7eeb884144663efe5`。
- 边界复验：runGuid `0099544f-ac45-4ccc-bcd8-883c6d6a0321`，29/30；唯一失败为移动窗口 `WINDOW_MAPPING_UNVERIFIED`，清理成功。保留 `Tests~/UPilotTest/Log/UPilotAcceptance/1788777096873_req-e948d72b-8e45-4c48-9357-2380f6ea4b37/summary.json`，88384 bytes，SHA256 `c1277d1b3bbf23b5e33f20f2b7aa1f7d80c867f9033d2cafae9b1c719e8bcee5`，不以首轮通过覆盖此失败。
- Python 联合定向 80/80；轻量质量门禁首轮 191/191、Skill/Registry 校验通过。统计有重叠，不相加。
- HWND 歧义诊断：runGuid `c64e6cb3-2ab3-450b-9707-47f5e7db4cbf` 的原生窗口客户区分别为 `136,100,456,320` 和 `134,100,454,320`，旧 2px 容差令两者均匹配；同时测试在 ShowUtility 前设置位置被窗口恢复覆盖。修复为严格像素矩形相等，并在 ShowUtility 后放置测试窗口，不放宽歧义门禁。
- 最终定向：runGuid `571876c7-4267-4f43-a0fe-8ac7855c1eed`，30/30、无跳过、清理成功且源码未变；`Tests~/UPilotTest/Log/UPilotAcceptance/1788777521399_req-1732cd04-4d82-4962-b90c-0ede17f47ea8/summary.json`，87287 bytes，SHA256 `452258e1e52b60c0bd9ccf673cfb61c2fc8d9f1a0c846dadc7161b4c47af4b0d`。
- 最终 C# 编译批次 `wb-5e29e3ee-dd8f-4f01-a670-1f28b5bdbd00`，createdAt `1788777436180`，operation `0b1ec12ff2924a98a9a33953b5c51232`，`errorsVerified=true/errorCount=0`，verifiedAt `1788777446578`。每批写入均独立登记；新增失败注入测试曾有类型名/命名空间编译错误，已修复并通过后续关联编译，不遗漏失败历史。
- 最终 Python/发布轻量质量门禁：208/208，通过 Skill 与 Registry 校验，`sourceUnchanged=true`；`artifacts/reliability-quality/quality.json`，2441 bytes，SHA256 `7e13dda7b0bbf5935531dd945151c142397301a0069a9dce6bec8a94c51ef55f`。两条既有 websockets 弃用警告未在本轮扩展处理。该门禁未传 Unity 摘要，不是发布强制门禁已通过的声明。
- Unity 最终验收后仅补了 Python 清理/发布契约及文档，未再修改 C# 或触发编译；最终 Python 源码身份为 v2 `d28da2e325370558bf056a9fd9a4c089a118e927d8d9a23440290cd005b14f43`，不能将此前 Unity v1 摘要当作这一最终整包源码的同源发布证据。
- 相关 Console capture 列表检查无活动会话；未启用 Tracer 或运行物理 Hook，未运行全量 EditMode。规范项目 Tracer 设置仅由序列化新增默认关闭的入口预算字段，不改变原有开关。
- 清单收口：根 UP-001 与 xclient2 现场旧计数 5 均归零，历史次数保留在证据中；UP-002～009 保持部分完成，现场精确计数不重置。没有复制新的同类活动条目，也未回退用户另行增加的 UP-025。

## 2026-09-08 Unity 2022 增量验收

### 环境及代码变更

- 用户指定的实际项目为 `D:\upilot\Tests~\UPilotTest2022`，不是 `Tests\~\UPilotTest2022`；HTTP MCP 8017 校验 `connected/serverReady=true`、Unity `2022.3.62f2`、PID `55700`，依赖为 `file:D:/upilot`。
- 通过 UPM 语义工具安装 Test Framework `1.4.6`，项目 manifest 加入 UPilot testables。该项目加入精确路径验收 allowlist；原 `Tests~/UPilotTest` 仍为默认，不接受任意同名目录。manifest 不属于 C#/程序集写批次，使用一次手动 sync + safe compile；后续 C# 批次均按技能独立登记。
- 修复 Camera 测试：Unity 2022 未必接受 `allowDynamicResolution=true`，回归现在核对实际可设置的原值被恢复，没有放宽像素尺寸/内容断言。
- 增加真实 BeginScrollView/回调计数、实际 PlayMode Runner、通用三秒自行退出 fixture，以及 SceneView Handles/SceneGUI 标记像素与超时/销毁测试。
- 修复 product：关闭的 SceneView 不再读取失效 ID；单 SceneView 新 Repaint 不再被请求帧 sameFrame 检查误杀；Snapshot Task 保留原始失败诊断。只变更单目标帧基准，不扩展多目标同步语义。
- 修复来源链：Runner RunFinished 退出意图、runGuid 补关联、权威 execution snapshot 漏字段、dispatcher 原 requestId/operationId、Operation startCall 占位符解析，以及陈旧状态/项目切换防串用。
- 候选 Python 已通过当前项目 Server 的一次性显式重启加载（分批修改后分别重载），未重启 Unity 或 8011 项目；每次均先确认无运行作业和活动 capture，再核对重连身份。未启用 Tracer、未提交/推送/发布，未修改客户业务代码。

### 保留的失败及修正证据

所有路径以下均相对 `Tests~/UPilotTest2022`：

| runGuid / 场景 | 结果与原因 | summary 路径 | bytes / SHA256 |
|---|---|---|---|
| `297763a0-5d0f-4320-bd70-19a149da7153` | 10/11；Camera 测试错误地硬断言动态分辨率 true | `Log/UPilotAcceptance/1788838835075_req-ae6424c1-119e-40eb-8815-6788a403742b/summary.json` | 33798 / `c0da7585bb1f1f8d6c0f8eade8dfe261f1789543c1f3f0dbc10aaedf8cc9c11a` |
| `c261d926-f0be-4ccd-83e2-183aaca7b078` | 9/11；关闭对象访问和测试跨 Reload 闭包失效 | `Log/UPilotAcceptance/1788839149103_req-32b9e908-45af-4b83-b1b5-b0452a9fc508/summary.json` | 36283 / `2cbb0a30b9bfe200187639e113425c0ff2221cdacfbeced5fb69d592d120d3c2` |
| `05ad1ce2-8b5e-4cc4-ac0e-865bfaf56c61` | SceneView 修正后 3/3 | `Log/UPilotAcceptance/1788839336073_req-94a953f3-a152-470b-bebe-dec8c85cf063/summary.json` | 29831 / `0b78f7f85c678021fd5b6bf68a12c3647e8237f3e699b102610ec97844b47318` |
| HTTP `task-eaa929c2-2b05-4cc6-8712-1256a2a7fcdc` | 17ms 返回 task，随后 `FRAME_ADVANCED_DURING_CAPTURE`；虽然原生像素可信，最终 artifact 正确拒绝为证据 | `Log/UPilotSceneViewAcceptance/http-short-task/manifest.json` | 失败目录未覆盖；不能用其中 PNG 宣称成功 |

追加 Snapshot 回归首次编译因测试 DTO 误写 `SnapshotTargetPayload` 失败（batch `wb-62e94bf9-d0da-4829-8d9a-35d09e2e92b2`）；读取错误后改为实际 `SnapshotTargetRequestPayload`。最终 batch `wb-0a5c386d-8521-4212-846d-fdcfd5cc8990`，created `1788840696675`，compileOperation `2092e0c1ac824cb488a7b9891018db90`，verified `1788840702299`，errorsVerified=true/errors=0。

Python 曾有旧 Screenshot 单元测试直接构建裸 mixin、未进入 Task 上下文而失败；修正测试边界后 103/103。新失败诊断测试初次把 fail 响应 detail 当作 data 读取，纠正为 `error.detail` 后门禁通过；均没有通过放宽产品成功判断掩盖失败。

### 定向通过的摘要

| 范围 | runGuid | 结果 | summary 路径 | bytes / SHA256 |
|---|---|---|---|---|
| 窗口/滚动/Camera/SceneView/快照透传/序列化直接回归 | `713270c6-f83c-45e9-9f69-0b6a2364a48a` | 16/16 | `Log/UPilotAcceptance/1788840411686_req-466d1cd7-eb6e-45ae-978b-777db2dd3545/summary.json` | 42059 / `8f5eec326d41a6ac7859c2db78c4f7d4cd55ce152c6149fad2626c74a458697b` |
| 资源删除失败注入及复制/移动/Prefab 直接回归 | `a99d0fb0-cc4e-4d71-82e5-82ef28096982` | 4/4 | `Log/UPilotAcceptance/1788840467923_req-28957a1a-8d69-4895-abb0-9305224dabbb/summary.json` | 35300 / `a5a1a02f77f1a80f167318d5f70db6e3434838503075f7897159d269ff7e0cf2` |
| 最终 SceneView（含 Snapshot 10 次新增回归） | `8419ac23-32c5-4748-a0d3-a152a3aae03d` | 4/4 | `Log/UPilotAcceptance/1788840757096_req-6f47a07b-7034-4f2f-b2a1-ef14e4dc88e5/summary.json` | 35253 / `c43ce9171a291b8a5c553607d2fcfa1f03bbcbff6695a30705ec62ec3686d659` |
| 最终真实 PlayMode Runner/requestId 来源 | `df9e584b-3fa1-49a1-927f-5c34cf7b94e7` | 1/1 | `Log/UPilotAcceptance/1788840851034_req-e1f1e879-f8dc-4d1c-b132-39d930688645/summary.json` | 34251 / `ad68432df46a2d7603fcf52b80fc148bc5f1d81b01df77fbc72ce0d457712b04` |

均无跳过、cleanupVerified/testIdentityVerified/sourceUnchanged=true。各轮范围重叠，不相加为一个“25 项独立测试”或全量回归。前两轮源码 SHA 为 `d51120cfb24e1ea1f11aafe68d534dd0858011196b5e5ddcd8b8c75af0fd605b`；后两轮最终源码为 v2 `f37f8a721364982b7daa57f1e2a225ff3c46e4b6c659ff7b845e23f001c06755`，591 文件，工作区基准 commit `932727cbfd7954b54cae5d8b6b320882e2fa8c46`，不是已提交的发布候选。

最终轻量门禁 `217 passed`、Skill/Registry 校验通过、sourceUnchanged=true；`artifacts/reliability-quality/quality.json`，2442 bytes，SHA256 `d19e5f4e0fff3cd5064a1fb22829c293d4dc01fae4a80ee17fa3474cf149ed54`。额外 `test_v02_contracts.py` 的 request 透传和旧 screenshot 包装器定向回归在 103/103 中通过；统计不与门禁相加。两条已有 websockets 弃用警告仍保留。

### Operation 与退出归因联机记录

| 场景 | operationId / transitionId | 结果 |
|---|---|---|
| 业务成功、项目 3 秒后自行退出 | `op-a6725960-1a90-475d-b516-1bd2f5eccf26` / `3b23a5bc3b7d46a98dc0043502909adf` | 先 CleaningUp/terminal=false，businessEndedAt=1788840105038；退出 at=1788840108186；权威 observedAt=1788840110965 晚于业务结束，最终 Succeeded；origin=project、operationId 相同 |
| 业务已成功，未登记的收尾退出 | `op-43180744-8641-425d-a135-a6c4f53378ee` / `216b0d97b02d48dd9fc99b160ee263c7` | 策略开启仍不误报业务成功；状态 origin=unknown；未把未知事件冒称当前作业的主动请求 |
| 业务仍 Running，未登记退出 | `op-bf81ea85-2039-4751-8720-4ad3c09a7495` / `6e5745246588469688926b33af3dd971` | Failed/OperationUnexpectedPlayModeExit；origin=unknown；capture/Editor 清理完成且 unresolvedResources=[] |
| UPilot 明确停止 | transition `694320c42a004baeb6e2413738f15788` | origin=upilot；request `req-8f22ef91-5d15-4e50-a83f-c3f9b529e973` 与 command `cmd-afd5fd24-59bb-4c11-ba33-8a0a98a7551e` 分别保留 |
| Runner 经 Operation 启动并经历 Reload | `op-c6790bb6-4aa9-4e55-a520-a7b66f28fd4d` / `40a793be86404ef6898f0a9de7e884d0` | runGuid `d9a02f8a-c702-4751-9175-a0317925e13c`，1/1；状态/Operation/测试三端同 transition；origin=testFramework、相同 operationId，真实 request `req-bc3bb452-7c1a-4e78-9b88-91137036783c` |

Runner Operation 首次 20 秒观察窗结束仍为 CleaningUp/terminal=false；检查 hang_status 后确认主线程恢复，无需重启或重放。businessEndedAt=1788840936063，cleanupEndedAt=1788840952543，editorEndedAt/endedAt=1788840952658，最终 Succeeded；这验证了窗口等待结束不是业务超时。

四个 Operation 自建 capture 均已停止，summary 与实际 JSONL 内容核验通过（路径前缀 `Log/UPilotConsole/`）：

| 目录 | JSONL bytes | JSONL SHA256 |
|---|---:|---|
| `2026-09-08_12-01-45-000_UP003 delayed exit` | 15395 | `366ed3a7ab0a50ee95f2f41fe1d71f31ff731638d2a679f5efdb56023191a89f` |
| `2026-09-08_12-02-08-997_UP003 delayed exit` | 16041 | `f289e65e2a8b2a7e4dd330ca163a54afc0607fcb2284a383e05ea89c51cede48` |
| `2026-09-08_12-02-34-626_UP003 delayed exit` | 176586 | `bc473af8ea7d913fc0037e460c52ee4553e2abab8c96ff28b0c87db0032e098e` |
| `2026-09-08_12-15-18-488_UP005 Runner operation` | 12374 | `db083a4977dcf228d0ed16e4b7b2559bc32dfaf1c1522bc7ee1e6ef76f6f5afd` |

停止失败、summary/分段哈希矛盾、取消、同毫秒/陈旧 Editor、并发轮询用 Python 故障注入覆盖；没有声称这些全部是 Unity 原生故障注入。最终 unity_console_capture_list 无活动会话，Editor 为权威 fresh ready/EditMode。

### SceneView HTTP 证据

修正后用精确 SceneView `17004` 在 PlayMode 连续 10 次调用 `unity_snapshot_capture(waitMs=1)`，每次只启动一次，持续查询返回的同一 taskId，全部 completed/success=true：

| 序号 | taskId | 返回 task / 完成耗时 ms |
|---:|---|---:|
| 0 | `task-a5a5440a-dd97-4862-bdf8-a1a5c4b98e44` | 24 / 468 |
| 1 | `task-b4b9f115-d009-4e12-abe8-8eb5d850fef3` | 9 / 238 |
| 2 | `task-0dc1bc1a-b97b-4468-a019-c65fe8a10ae8` | 22 / 245 |
| 3 | `task-a3a758a2-d7ae-4a1e-9ffe-a8a00cf72a52` | 10 / 347 |
| 4 | `task-860a24b4-e476-45e3-b66c-cd8d1b2fd34f` | 7 / 340 |
| 5 | `task-cf2ec28f-aff8-49a9-a4c8-268434d1e6ba` | 10 / 237 |
| 6 | `task-1013bc5f-0fd6-49c0-adef-66b50c1160d8` | 23 / 246 |
| 7 | `task-7a7f7eef-cd4a-4822-95b3-8055f8f61b37` | 11 / 234 |
| 8 | `task-0939d463-50f8-4631-b8c3-e3ac48fe77f9` | 13 / 348 |
| 9 | `task-dc058eb9-86ee-4fb6-a79c-74d3cb7ce404` | 24 / 353 |

产物分别位于 `Log/UPilotSceneViewAcceptance/http-final-0`～`http-final-9`，每个目录保留 manifest.json。PNG 均为 1074×320、145995 bytes，SHA256 `0ca096b4fe83d7e2ba6fcbda79392f2af820a1947770d8607a065f853bc89b9a`；该 HTTP 场景静止，相同像素哈希不替代新的 Repaint 证据。API=`Win32.PrintWindow(PW_RENDERFULLCONTENT)`、PID=55700、HWND=6624496、foreground=false、minimized=false、degraded=false、originalError 为空，acceptedAsEvidence/pixelSourceVerified/includesSceneGui/includesHandles=true，occlusionSensitive=false。Repaint sequence=1～10，首帧 requested/observed=1788840830493/1788840830504，末帧=1788840833178/1788840833184。

带实际黄色 Handles 和品红 SceneGUI 标记的像素验证由上述 SceneView fixture 单独完成（每帧要求标记像素 >3000），不凭 HTTP 静态背景图片声称业务画面正确。当时捕获中 Reload 与多目标 sameFrame 组合尚未专项验收，UP-008 保持部分完成；当前结论见下方最终收口，不覆盖原始记录。

### 本日上一批清单收口（历史）

- UP-001、003、005 已完成且次数 0；根清单和 xclient2 对应通用引用同步，历史现场计数保留。前九项为 3 完成、6 部分完成；整表为 55 完成、11 部分完成、11 待处理，无新增重复活动项。
- 当时推荐优先级为 UP-002 → UP-004/009 → UP-008 → UP-006 → UP-007；用户后续已决定只继续 UP-008、忽略其余五项剩余缺口，因此此排序不再是当前开发队列。
- UP-004、007 按分阶段约定不关闭；UP-006 没有启用 Hook 或运行崩溃组合；UP-009 没有实际 EXE/缓存/runner 证据。2022.3 本地 summary 不冒充受控 workflow artifact，仍禁止据此宣称发布门禁、双稳定版本 CI 或整个九项计划全部完成。
- 最后只读检查：8017 项目权威 fresh ready/EditMode，compile errors=0、Console Error=0、无活动 capture。排除 `.meta` 的 `git diff --check` 通过；整工作区检查仍报告五个先前已有变更的 `.meta` 空字段尾空格，未顺手改动这些用户变更，也未恢复用户删除的旧 ExecutionCompat2022 项目文件。

## 2026-09-08 UP-008 最终收口与剩余缺口忽略

### 变更与失败归因

- 仍只使用用户授权的 Unity 2022.3.62f2 项目、HTTP 8017。新增 `PendingCaptureFailsAndUnsubscribesAcrossRealDomainReload`，在无 Repaint 的自建 SceneView 同时启动底层和 Snapshot 捕获，通过真实 EnterPlayMode/Domain Reload 验证失败终态、订阅解绑、原 snapshotId 恢复且无图片；跨域数据存于 SessionState，不携带失效闭包或 Task。
- 联合运行两次暴露 Snapshot 续体超过 7 秒仍停在 rendering。仅把测试入口改为产品实际 `CreateAndSchedule` 不足以解决；最终在产品 SceneView 两层等待中使用 Editor.update 主线程续体，独立于可能延迟的 Unity SynchronizationContext。后台 watchdog 只完成 Task，Unity API 和写入结果继续在主线程执行，完成/Reload 仅收尾一次并解绑。保留其他目标的严格同帧语义和 Camera/GameView 行为。
- 新 Reload fixture 的窗口标题被 Unity 重置为 Scene，原按“ID 且标题”清理遗漏自建窗口，后续触发两个 HWND 客户区完全相同的严格映射拒绝；改为持久精确 ID 清理并断言对象已释放，不放宽像素门禁。按证据清理了遗留测试窗口；隐藏窗口 Close 的空引用单独保留，检查部分清理后只对剩余确切 owned ID 执行 DestroyImmediate，没有重放整个关闭序列，也没有删除证据文件。
- Camera 直接回归在 2022.3 的动态分辨率断言再次暴露平台差异：现在先记录配置后实际可设置的值，再验证 Snapshot 完成后原值被恢复；像素、产物和同帧断言不变。

以下 summary 路径相对 `Tests~/UPilotTest2022`；失败未覆盖，范围有重叠，不能合计为独立测试数量：

| runGuid | 结果 | summary 路径 | bytes / SHA256 |
|---|---|---|---|
| `e725a021-281e-4e5a-9031-43348e20a68e` | 独立真实 Reload 1/1 | `Log/UPilotAcceptance/1788846066516_req-1c80680a-afaf-4219-b087-d6ca0701f168/summary.json` | 35894 / `f806ac52c1e887fdf2267562da94d12b7c96875dcdbc4c680eea8004666efa4d` |
| `6dfa7288-395b-41fa-935e-2ee5a2b7ff33` | 6/7；Snapshot 续体迟到 | `Log/UPilotAcceptance/1788846146424_req-a957150e-d591-4191-9e1d-cf592e35eeff/summary.json` | 41640 / `6bca5ca9a079c42ff71cbeb2b393c44d08f6df4b11763140826c4ad14d5a79bf` |
| `23643292-73b8-4552-96fb-19c1daef9a4c` | 6/7；只调整测试调度仍失败 | `Log/UPilotAcceptance/1788846585401_req-3f2fa72e-66ef-4bf9-8821-b94b91501fea/summary.json` | 41636 / `efc1819aaa45318b339ec03aa065684949b333c5af8f17edacba8a9448614020` |
| `2e3dc4e2-d0fc-4d02-86a8-7499eb5292ca` | 6/10；3 个窗口映射拒绝、1 个动态分辨率硬断言 | `Log/UPilotAcceptance/1788846769710_req-055310cb-fe23-4c87-aff0-751c101306e4/summary.json` | 51767 / `b833edd6d1c194ef0cd9724d3ad58feb4558e3dcea45ce221f3e0e4bbe7d16d1` |
| `e27d5d98-c2b3-49ee-be2c-2655d18d72d9` | 最终 10/10、0 跳过，清理/测试身份/源码未变均验证 | `Log/UPilotAcceptance/1788846992902_req-53715ace-d11f-43e4-b3e9-30163c1df133/summary.json` | 42637 / `492cfb1d4efed37c20c90dbdd59be1aa72e18f0a26cec56b509b0de9aa7450c5` |

最终 taskId=`task-c9516d68-dce7-4cdf-a548-f4c0c3662bc8`，completed/acceptancePassed=true。范围为整个 SceneViewCaptureTests 的 5 个用例，加 Snapshot 的精确 SceneView、真实 Repaint、双 Camera、GameView 非 PlayMode 拒绝、GameView 最终组合 5 个直接回归，不是全量 EditMode。

### 最终编译与产物

- 四个新增 C# 写批次均只登记一次自动编译：`wb-bc2f8ccd-b658-4774-a533-492b4b197b88`（新增 Reload 测试）、`wb-ed6eaf4a-1254-4843-be63-3e528f0e53e5`（测试入口对齐）、`wb-863c18d2-001a-4648-bbfc-b59d691504f8`（产品主线程续体）、`wb-6ebcc2a0-ea6e-480c-b403-adcd311aa515`（测试清理与 Camera 原值断言），均为相关 terminal completed/errorsVerified=true/errors=0。
- 最终批次 createdAt=`1788846913052`，compileOperation=`18e873d89cc842c793d8fa2a4fa6365c`，verifiedAt=`1788846918075`，晚于写入；最终整包源码 v2 SHA256=`c682a178665b3b8b4302f9a8bad2e10e2bf1fe56a7380590ca866196131e73b5`，591 文件，仍为未提交工作区，不冒充已提交发布候选。
- 最终 Reload 证据：`Log/UPilotSceneViewAcceptance/reload-2efd4102d34e48cc87421d42c817506f/reload-evidence.json`，680 bytes，SHA256=`81be6447f9b5b2cefff5926784a93d951dbcf166fed481afd819e767b8c60850`。domain `e8a88ba2b70c4518a2cd473bbbf0b171` → `3800a36f99a543b7b926aeccd94312c2`；订阅数 0→2→0→0，窗口 `4294935812` 已释放。MCP 重连后查询原 `snapshot-2436c92d25194f2296b35bbe5133f865` 仍为 failed/terminal=true/success=false、artifacts=[]、message=SCENEVIEW_DOMAIN_RELOAD，迟到回调未覆盖。
- 最终 Snapshot 连续 10 次目录 `Log/UPilotSceneViewAcceptance/snapshot-eb557677acac48f391bde85340459306/0`～`9`，任务完成耗时 123～214ms；实际文件 bytes/SHA256 全部复核一致。每帧品红 SceneGUI 标记像素 >3000，精确 SceneView `4294934844`，PrintWindow/PW_RENDERFULLCONTENT、PID 55700、HWND 22687168，600×450，foreground=false、环境 normal/非最小化、degraded=false、originalError 为空，acceptedAsEvidence/pixelSourceVerified/includesSceneGui/includesHandles=true，occlusionSensitive=false。
- 首帧 `0/target-1.75a34976.color.png` 为 101787 bytes，SHA256=`3df9c0a2e29f0e6a7b360bd8423674244e86f55bf56690c60c4bbd950ee70638`；末帧同名 PNG 为 105249 bytes，SHA256=`c23e6349a1ed3a7ab3467ecabffff1535d1aae781749b8914d71586715a6d07f`。首帧 Repaint requested/observed=`1788847032309/1788847032325`，逐帧完整 provenance 留在各 manifest 中。此前 HTTP 10 次短 Task 证据仍保留，其旧源码身份不冒充最终批次摘要。
- 最终同源码轻量质量门禁 217/217，Skill/Registry 校验通过、sourceUnchanged=true：`artifacts/up008-closure-quality/quality.json`，2448 bytes，SHA256=`21de581439f5ac8cd436eef00823e9f985be1e5f60e385ec047b119c354dc0f9`。另行 Python 短任务 3/3，与门禁有重叠；两条既有 websockets 弃用警告保留。未传受控 Unity artifact，不是强制发布门禁通过声明。
- 最终 MCP 权威 fresh ready/EditMode，Console Error=0，Console capture 无活动会话；本轮 owned Reload 窗口无遗留。未启用 Tracer、未运行全量 EditMode、未提交/推送/tag/发布、未复验客户业务。

### TODO 决定与计数

- UP-008 原定单 SceneView 短窗口范围已完成，根与 xclient/xclient2 现场引用次数均清零，历史现场与失败产物保留。额外多目标 SceneView/Camera 混合同帧扩展矩阵按用户要求忽略，不宣称已验收，也不放宽原严格 sameFrame 检查；不增加 independent 或旧帧复用功能。
- UP-002、004、006、007、009 标为“已忽略剩余缺口”，从活动优先级移除；不是完成，保留原未闭环计数和风险。分别保留跨 DPI/窗口组合未验收、受控远端/双稳定版本 CI 未完成、Tracer 在途卸载/性能/原生稳定性未闭环、第三方零接入 IMGUI 自动定位未交付、Actions runner/EXE/缓存演练未运行等边界。任何像素、安全或发布门禁都未因此解除。
- 当前前九项为 4 完成（001/003/005/008）、5 忽略；根共 78 个详细条目：56 完成、5 部分完成、12 待处理、5 忽略。UP-010 起不属于此次忽略范围，用户新增 UP-025/026 保留；历史整合文件只增加当前引用，不重复建立活动项。
