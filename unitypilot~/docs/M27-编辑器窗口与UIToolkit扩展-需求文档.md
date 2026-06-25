# M27-编辑器窗口与UIToolkit扩展-需求文档

## 1. 模块职责
- 负责：
  - 按窗口标题匹配并关闭 `EditorWindow`（`editor.window.close`），并采用类型黑名单与空标题拒绝策略。
  - 枚举编辑器窗口时返回是否允许关闭及拒绝原因（`editor.windows.list` 扩展字段）。
  - 浮动未 dock 窗口设置 `position` 矩形（`editor.window.setRect`）。
  - UIToolkit：`ScrollView` 滚动条拇指路径的指针模拟（`uitoolkit.scrollbar.drag`）。
  - UIToolkit：`wheel` 类型合成事件（`uitoolkit.event` 扩展）。
  - MCP E2E：`uitoolkit.pointerSequence` 展开为多次 `uitoolkit.event`；失败产物可选 ZIP；可选 HTTP Webhook；可选云端图像理解。
- 不负责：
  - 不负责操作系统级窗口管理器或 Unity 外壳（`ContainerWindow`）的拖动与 dock 条操作；该类需求在 `## 11` 标注为不推荐 Bridge 实现。
  - 不负责替代 UTF 或 PlayMode 测试。
- 输入/输出：
  - 输入：WS 命令 `editor.window.close`、`editor.window.setRect`、`editor.windows.list`（扩展）、`uitoolkit.scrollbar.drag`、`uitoolkit.event`（含 `wheel`）。
  - 输出：标准 `result` payload；错误码见 `## 6`。

## 2. 数据模型
| 字段名 | 类型 | 必填/可选 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- | --- |
| windowTitle | string | 必填 | 关闭或定位窗口时匹配的标题文本 | 非空字符串（`close`/`setRect`） | "" |
| matchMode | string | 可选 | 标题匹配方式 | `exact`、`contains` | `exact` |
| x | float | 必填（setRect） | 窗口 `position.x` | 任意有限浮点 | 0 |
| y | float | 必填（setRect） | 窗口 `position.y` | 任意有限浮点 | 0 |
| width | float | 必填（setRect） | 窗口 `position.width` | `(0, 4096]` | 100 |
| height | float | 必填（setRect） | 窗口 `position.height` | `(0, 4096]` | 100 |
| closable | bool | 输出 | 列表项是否允许被 `close` 关闭 | `true`/`false` | false |
| closeDeniedReason | string | 输出 | 不可关闭原因码 | `EMPTY_TITLE`、`BLACKLIST`、空字符串 | "" |
| scrollViewElementName | string | 必填（scrollbar.drag） | 目标 `ScrollView` 的 `name` | 非空 | "" |
| scrollbarAxis | string | 可选 | 滚动条轴 | `vertical`、`horizontal` | `vertical` |
| normalizedThumbPosition | float | 必填（scrollbar.drag） | 沿轨道位置：垂直时 0 为顶端、1 为底端 | `[0, 1]` | 0 |
| dragSteps | int | 可选 | 指针插值采样点数 | `[2, 20]` | 5 |
| wheelDeltaX | float | 可选 | `wheel` 事件水平增量 | 任意有限浮点 | 0 |
| wheelDeltaY | float | 可选 | `wheel` 事件垂直增量 | 任意有限浮点 | 0 |
| exportZip | bool | 可选（E2E） | 是否在 `report.json` 生成后打包 ZIP | `true`/`false` | false |
| webhookOnFailure | bool | 可选（E2E） | 失败时是否 POST 到环境变量 URL | `true`/`false` | false |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | 实现标识 | Undo语义 |
| --- | --- | --- | --- | --- |
| 关闭窗口 | `editor.window.close` | 未找到窗口；`closable=false` | `EditorWindow.Close()` | 不进 Undo |
| 设置窗口矩形 | `editor.window.setRect` | 未找到窗口；窗口 `docked=true` | `EditorWindow.position` 赋值 | 不进 Undo |
| 枚举窗口（扩展） | `editor.windows.list` | 无 | 遍历 `EditorWindow` | 不进 Undo |
| 滚动条拖拽模拟 | `uitoolkit.scrollbar.drag` | 未找到 `ScrollView`/`Scroller` | `MouseDown/Move/Up` 序列 | 不进 Undo |
| 滚轮事件 | `uitoolkit.event` + `wheel` | 非法 `eventType` | `WheelEvent` | 不进 Undo |

## 4. 交互规格
- `editor.window.close`：主线程查找首个匹配标题的 `EditorWindow`；若 `matchMode=contains`，取首个匹配项；调用 `Close()` 前执行 `closable` 同构校验。
- `editor.window.setRect`：仅当 `docked=false` 时允许设置；否则返回 `WINDOW_DOCKED`。
- `uitoolkit.scrollbar.drag`：在目标 `Scroller` 的 `worldBound` 内沿轨道插值 `dragSteps` 个点，依次派发 `mousedown`、`mousemove`（多次）、`mouseup`，坐标为相对 `Scroller` 的本地坐标。
- `uitoolkit.event`：`eventType=wheel` 时读取 `wheelDeltaX`/`wheelDeltaY`，经 IMGUI `EventType.ScrollWheel` 传入 `WheelEvent.GetPooled(imguiEvt)`（`WheelEvent.delta` 在 UIToolkit 内只读，不能直接赋值）。
- `uitoolkit.event`：`eventType=capturePointer` / `releasePointer` 时调用目标 `VisualElement` 的 `CapturePointer`/`ReleasePointer`（`mouseButton` 作 `pointerId`，默认 0）。
- E2E：`action: uitoolkit.pointerSequence` 在 Runner 内展开为连续若干 `uitoolkit.event` 子调用，共享同一步骤超时与失败附件逻辑。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| `windowTitle` 为空 | `close`/`setRect` 收到时 | Error | `windowTitle is required` |
| 未找到匹配窗口 | 查找结束 | Error | `WINDOW_NOT_FOUND` |
| 窗口不可关闭 | `close` 调用前 | Error | `WINDOW_CLOSE_DENIED` |
| `setRect` 目标已 dock | 调用前 | Error | `WINDOW_DOCKED` |
| `uitoolkit.event` 非法类型 | 收到时 | Error | `非法 eventType`（沿用现有） |
| `scrollbar.drag` 未找到 ScrollView | 执行时 | Error | `SCROLLVIEW_NOT_FOUND` |

## 7. 跨模块联动
| 模块 | 方向 | 说明 | 代码依赖点 |
| --- | --- | --- | --- |
| M02 UnityBridge | 双向 | 注册命令与主线程派发 | `UnityPilotWindowService`、`UnityPilotUIToolkitService` |
| M26 E2E | 被动 | 新增 `action` 映射 | `editor_e2e/actions.py`、`runner.py` |
| Python MCP | 主动 | 暴露工具与 E2E ZIP/Webhook | `tool_facade.py`、`mcp_stdio_server.py` |

## 8. 技术实现要点
- 关闭黑名单（`FullName` 精确匹配，不可配置）：`UnityEditor.GameView`、`UnityEditor.SceneView`、`UnityEditor.ProjectBrowser`、`UnityEditor.InspectorWindow`、`UnityEditor.ConsoleWindow`、`UnityEditor.AnimationWindow`。
- `closable=false` 当且仅当：`title` 为空或空白，或 `FullName` 命中黑名单。
- `uitoolkit.scrollbar.drag` 与 `uitoolkit.scroll`（`scrollOffset`）语义不等价：前者走指针路径，后者直接改偏移；验收以业务是否监听指针为准。
- E2E ZIP：使用 `zipfile` 将 `artifactDir` 内 `report.json`、`manifest.json` 及已有附件打包为 `e2e-bundle.zip`。
- Webhook：`UNITYPILOT_E2E_WEBHOOK_URL` 存在且 `webhookOnFailure=true` 时，失败以 `multipart/form-data` POST `report.json`。
- 云端图像理解：`unity_vision_analyze` 读取 `OPENAI_API_KEY`（或 `UNITYPILOT_OPENAI_API_KEY`）；默认模型为工具参数 `model`，若为空则读环境变量 `UNITYPILOT_VISION_MODEL`，再回退 `gpt-4o-mini`；请求 OpenAI 兼容 `chat/completions`；未配置密钥则工具返回明确错误，不静默失败。

## 9. 验收标准
1. `editor.windows.list` 返回项含 `closable`、`closeDeniedReason`，与黑名单及空标题规则一致。
2. 对自定义 `EditorWindow`（非黑名单、标题非空）调用 `editor.window.close` 后窗口关闭。
3. 对 `GameView` 调用 `editor.window.close` 返回 `WINDOW_CLOSE_DENIED`。
4. `setRect` 于浮动窗口成功；于 dock 窗口返回 `WINDOW_DOCKED`。
5. `uitoolkit.scrollbar.drag` 在含 `ScrollView` 的测试窗口执行返回 `ok`。
6. `uitoolkit.event` + `wheel` 可派发，`target` 接收 `WheelEvent`。
7. E2E 含 `uitoolkit.pointerSequence` 的规格可执行；`exportZip=true` 时生成 ZIP 路径于结果 JSON。

## 10. 边界规范
- 多显示器：`position` 使用 Unity `EditorWindow.position` 坐标系，与现有一致。
- `contains` 匹配多个窗口时取第一个匹配项，并在 `close` 结果中附带 `matchModeWarning=multiple_candidates_possible`。
- 指针序列步数过多时总耗时受 E2E `timeoutS` 约束。

## 11. 周边可选功能
- **BL-10（P3）**：Dock/Workspace 布局保存与恢复：不实现 Bridge；推荐 OS 级自动化或人工操作。
- **BL-13（P3）**：基线 PNG 云端版本化：仅保留环境与接口约定，具体存储由 CI 侧实现。
- **ListView 行重排（BL-06）**：依赖指针序列与业务控件 `name`，不单独增加命令。
