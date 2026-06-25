# M27 — 编辑器自动化验收：缺口 Backlog（全量）

本文将前文所述能力缺口**全部**落成可排期的条目，并标注 **优先级、实现路径（Bridge / YAML / 外部）、验收标准**。优先级：**P0** 最快收益、**P3** 长期或依赖外部系统。

---

## 1. 优先级说明

| 级别 | 含义 |
|------|------|
| **P0** | 高频验收刚需、实现成本可控、无强外部依赖 |
| **P1** | 明显增强「类人操作」或缩小与手动测试差距 |
| **P2** | 覆盖长尾场景、或依赖 OS/Unity 内部 API，成本与风险较高 |
| **P3** | 强视觉云、生态集成，需单独产品与合规评估 |

---

## 2. Backlog 总表

| ID | 主题 | 优先级 | 路径 | 提议 Bridge / 产物 |
|----|------|--------|------|-------------------|
| **BL-01** | 按标题/白名单关闭 EditorWindow | P0 | Bridge + MCP + E2E `action` | `editor.window.close` |
| **BL-02** | 枚举窗口时返回可关闭/安全提示 | P0 | Bridge | 扩展 `editor.windows.list` payload |
| **BL-03** | Scrollbar 拇指拖拽（相对 scrollOffset 语义） | P1 | Bridge | `uitoolkit.scrollbar.drag` 或扩展 `uitoolkit.scroll` |
| **BL-04** | 文档化「scrollOffset vs 拖拽」等价策略与限制 | P0 | 文档 + YAML 示例 | `docs` + `e2e-specs/examples/` |
| **BL-05** | UIToolkit 连续指针序列（down→move→up） | P1 | Bridge + YAML | `uitoolkit.pointerSequence` 或 E2E 多步 `uitoolkit.event` |
| **BL-06** | ListView / 列表行内重排（若可定位） | P2 | Bridge + 试验 | 基于 BL-05 + 坐标插值 |
| **BL-07** | 嵌套 ScrollView 明确 target 与滚动顺序 | P1 | YAML + 文档 | `defaults` + 多步 `uitoolkit.scroll` |
| **BL-08** | 窗口客户区内「拖动」与 dock 区分的说明与示例 | P1 | 文档 | M26 指南补章 |
| **BL-09** | 浮动窗口标题栏拖动（改 position） | P2 | Bridge（Unity API） | `editor.window.setRect` 或 `editor.window.move` |
| **BL-10** | Dock/Workspace 切换与拆分 | P3 | Bridge 或 **不实现** | 调研 `UnityEditor.WindowLayout` 等；高风险则列为「仅 OS 自动化」 |
| **BL-11** | 强视觉云：上传截图到可配置 HTTP 端点 | P2 | Python MCP + 配置 | `editor_e2e` 钩子 + `UNITYPILOT_VISION_WEBHOOK` |
| **BL-12** | 强视觉云：可选 OpenAI/Anthropic/Gemini 图像理解 | P3 | Python | `unity_vision_analyze`（API Key 由 env/secret） |
| **BL-13** | 基线 PNG 云端存储与版本化 | P3 | Python + CI | 与 BL-11/12 联动 |
| **BL-14** | E2E 失败 bundle（zip）一键导出路径 | P1 | Python | `unity_editor_e2e_run` 返回 `zipPath` |
| **BL-15** | 复杂手势模板库（YAML 片段） | P1 | YAML | `e2e-specs/templates/gestures/*.yaml` |
| **BL-16** | Bridge 层 PointerCapture / 滚轮事件 | P2 | Bridge | `uitoolkit.wheel` / 扩展 `uitoolkit.event` |

---

## 3. 分项说明（验收标准与依赖）

### BL-01 — 关窗 API（P0）

- **缺口**：无法用单一 MCP 安全关闭**指定**编辑器窗口。
- **方案**：Bridge 新增 `editor.window.close`，参数：`windowTitle`（必填）、`allowCloseList`（可选，pref 持久化白名单类型名或标题正则），**禁止**关闭无标题主窗口等（硬编码黑名单）。
- **验收**：仅白名单/非黑名单窗口可关；错误返回明确 `WINDOW_CLOSE_DENIED` / `WINDOW_NOT_FOUND`。
- **E2E**：`action: editor.window.close`。
- **依赖**：`UnityPilotWindowService` 或新小类，主线程 `Resources.FindObjectsOfTypeAll<EditorWindow>` 匹配标题后 `Close()`。

### BL-02 — 窗口列表增强（P0）

- **缺口**：Agent 不知道哪些窗口「建议关」「不可关」。
- **方案**：`editor.windows.list` 增加 `closable`（bool）、`isMainWindow`（bool 若可检测）等。
- **验收**：与 BL-01 黑名单规则一致。

### BL-03 — Scrollbar 拖拽与 scrollOffset 等价（P1）

- **缺口**：仅改 `scrollOffset` 可能不触发依赖「拖拽」路径的业务逻辑。
- **方案 A（优先）**：对目标 `ScrollView` 计算 scrollbar 拇指中心，发送 **MouseDown → MouseMove（多采样）→ MouseUp**（客户区坐标），走现有事件管线。
- **方案 B**：文档声明「验收以 scrollOffset 为准」，拖拽仅用于专项用例。
- **验收**：提供对比测试：同一 ScrollView，`scroll` API 与 `scrollbar.drag` 在测试钩子中可观测行为一致（若业务无区分则标记 N/A）。
- **Bridge**：`uitoolkit.scrollbar.drag`：`targetWindow`, `scrollViewName`, `normalizedThumbPosition` 或 `deltaPixelsAlongTrack`。

### BL-04 — 文档化等价策略（P0）

- **缺口**：团队与 Agent 不理解何时用 API 滚动、何时必须模拟拖拽。
- **方案**：在 `M26-UIToolkit编辑器验收-Agent操作指南.md` 增加「Scroll 语义与限制」；示例 YAML 两段并列。
- **验收**：PR 审查通过即可。

### BL-05 — 连续指针序列（P1）

- **缺口**：复杂手势需多次 `uitoolkit.event`，规格冗长易错。
- **方案**：  
  - **轻量**：E2E 支持 `steps: - uitoolkit.pointerSequence: { events: [...] }`（纯 YAML 展开为多次 `event`）。  
  - **重量**：Bridge 单次 `uitoolkit.pointerSequence` 原子执行。
- **验收**：至少实现轻量；重量可选。

### BL-06 — ListView 行重排（P2）

- **缺口**：行内拖拽重排依赖内部实现。
- **方案**：先用 BL-05 对「行 VisualElement」做 down/move/up；失败则 UTF + 自定义 `IPointer` 测试桩。
- **验收**：在示例工程中有一条可重复通过的用例。

### BL-07 — 嵌套 ScrollView（P1）

- **缺口**：单步 `scroll` 可能滚到外层面板。
- **方案**：文档约定 `elementName` 指向**内层** `ScrollView`；YAML 模板多步滚动。
- **Bridge 可选**：`scroll` 增加 `depth` 或 `path`（名称链）。

### BL-08 — 客户区拖动 vs dock（P1）

- **缺口**：概念混淆导致错误预期。
- **方案**：指南中图示：客户区 = `mouse`/`uitoolkit`；dock/壳 = BL-09/BL-10 或 OS。
- **验收**：文档 + 截图。

### BL-09 — 浮动窗 position（P2）

- **缺口**：自动化调整浮动 `EditorWindow.position`。
- **方案**：`editor.window.setRect`：`title` + `x,y,w,h`（屏幕或 Unity 坐标系需文档统一）。
- **验收**：窗口位置可读回一致（`editor.windows.list` 已有 pos）。
- **风险**：多显示器、DPI。

### BL-10 — Dock / Workspace（P3）

- **缺口**：拖动 dock 条、保存/加载布局。
- **方案**：调研 Unity 公开 API；若不稳定则 **Backlog 标注「推荐 OS 级自动化或人工」**，不强制 Bridge。
- **验收**：能保存/恢复布局或通过则关闭；否则文档列为「非目标」。

### BL-11 — 视觉云上传（P2）

- **缺口**：失败截图只在本机，CI/远程评审不便。
- **方案**：环境变量 `UNITYPILOT_VISION_WEBHOOK_URL` + POST multipart，E2E 失败时可选上传。
- **验收**：可关闭；默认不上传（隐私）。

### BL-12 — 云端多模态分析（P3）

- **缺口**：自动判断「界面是否符合需求描述」。
- **方案**：`unity_vision_analyze(imageBase64, prompt)` 调用可选供应商；**API Key 仅存 env**，不落盘。
- **验收**：离线环境可跳过；合规审查通过。

### BL-13 — 基线云与版本（P3）

- **缺口**：视觉基线团队共享。
- **方案**：与 BL-11 同源存储或 S3 兼容 API；与 Git LFS/标签绑定版本。
- **验收**：可追溯 `baselineId → PNG hash`。

### BL-14 — E2E 失败包（P1）

- **缺口**：附件散在目录，不便粘贴到 issue。
- **方案**：`unity_editor_e2e_run` 可选 `exportZip: true`，打包 `report.json` + dump + console + png。
- **验收**：解压后结构固定。

### BL-15 — 手势 YAML 模板库（P1）

- **缺口**：每个项目重复写 down/move/up。
- **方案**：`e2e-specs/templates/gestures/`：`swipe_scroll.yml`、`drag_row.yml`（含注释）。
- **验收**：至少 3 个模板，与 BL-05 联动。

### BL-16 — 滚轮 / Capture（P2）

- **缺口**：Wheel、Captured 指针未系统暴露。
- **方案**：扩展 `uitoolkit.event` 或 `WheelEvent` 封装。
- **验收**：在可复现的测试窗口上通过。

---

## 4. 推荐实施顺序（迭代）

1. **Sprint A（P0）**：BL-01、BL-02、BL-04  
2. **Sprint B（P1 核心）**：BL-03、BL-05（轻量 YAML）、BL-07、BL-08、BL-14、BL-15  
3. **Sprint C（P2）**：BL-06、BL-09、BL-16、BL-11（可选开关）  
4. **Sprint D（P3）**：BL-10（结论）、BL-12、BL-13  

---

## 5. 与现有模块关系

- **M26**：E2E 编排；本 Backlog 新增 `action` 需在 `editor_e2e/actions.py` 映射。  
- **M02/M05**：Bridge 与输入；关窗、窗口 rect 属编辑器命令扩展。  
- **M11**：截图；视觉云上传在 Python 侧扩展即可。

---

## 6. 版本记录

- v1.0（2026-04-05）：初版 — 全量缺口 backlog（BL-01～BL-16）与优先级、验收要点。
