# UIFlow 使用指南

UIFlow 是 upilot 随包提供的可选 UI 自动化附属工具。它用 YAML 描述测试步骤，目标是自动化 Unity Editor 中的 `EditorWindow` UI。

根目录 `README.md` 只介绍 upilot 的安装和基础使用；复杂的 UIFlow 用法放在本文档和相关专题文档中。

## 适用范围

UIFlow 面向：

- 基于 UIToolkit 的 Unity EditorWindow。
- 通过 `imgui_*` 动作覆盖的部分 IMGUI EditorWindow 工作流。
- 本地 headed 可视化调试。
- MCP 驱动的自动化验证流程。

UIFlow 不面向：

- Game View Runtime UI。
- Play Mode 游戏逻辑测试。
- 像素级视觉 diff。
- 通用 Web/CSS/浏览器自动化。

## 启用条件

- Unity `6000.0` 或更高。
- 脚本宏：`UPILOT_ENABLE_UIFLOW`。
- Unity 包：
  - `com.unity.inputsystem`
  - `com.unity.ui`
  - `com.unity.ui.test-framework`
  - `com.unity.test-framework`

Unity 2022 中不要启用 UIFlow。upilot 核心 MCP 工具不依赖 UIFlow，仍可正常使用。

## 启用步骤

1. 安装 upilot。
2. 在 **Project Settings > Player > Scripting Define Symbols** 中添加 `UPILOT_ENABLE_UIFLOW`。
3. 安装上面列出的 UIFlow 依赖包。
4. 等待 Unity 重新编译。
5. 打开菜单：

```text
upilot/UIFlow/Test Runner
```

## 最小 YAML 示例

```yaml
fixture:
  host_window: ExampleBasicLoginWindow
steps:
  - wait_for_element:
      selector: "#username-input"
      timeout: "5s"
  - type_text_fast:
      selector: "#username-input"
      text: "admin"
  - type_text_fast:
      selector: "#password-input"
      text: "password"
  - click:
      selector: "#login-button"
  - assert_text:
      selector: "#status-label"
      text: "Login successful"
```

推荐使用 `.yaml` 扩展名；当前约定不使用 `.yml`。

## 选择器建议

推荐优先级：

1. 稳定 `name`：`#login-button`。
2. 语义数据：`[data-role=primary]`。
3. 控件类型：`Button`、`Label`、`TextField`。
4. class 选择器只用于稳定且有测试语义的样式类。

页面开发时，应给关键交互元素设置唯一 `name`。

## 功能支持清单

| 范围 | 示例 |
| --- | --- |
| 指针动作 | `click`、`double_click`、`hover`、`drag`、`scroll`、`open_context_menu`、`open_popup_menu` |
| 键盘/输入 | `focus`、`press_key`、`type_text`、`type_text_fast` |
| 字段 | `set_value`、`set_slider`、`select_option`、`toggle_mask_option`、`set_bound_value`、`assert_bound_value` |
| 集合控件 | `select_list_item`、`drag_reorder`、`select_tree_item`、`sort_column`、`resize_column` |
| 布局/导航 | `select_tab`、`close_tab`、`toggle_foldout`、`navigate_breadcrumb`、`set_split_view_size`、`page_scroller` |
| 等待/断言 | `wait_for_element`、`assert_visible`、`assert_not_visible`、`assert_text`、`assert_text_contains`、`assert_value`、`assert_enabled`、`assert_disabled`、`assert_property` |
| 报告 | 截图、Markdown 报告、JSON 报告、失败附件 |
| IMGUI | `imgui_click`、`imgui_type`、`imgui_focus`、`imgui_scroll`、`imgui_select_option`、`imgui_press_key`、`imgui_assert_*`、`imgui_wait` |

## 执行方式

本地 headed 执行：

```text
upilot/UIFlow/Test Runner
```

适合可视化调试、高亮、单步执行和检查选择器命中。

MCP 驱动执行：

- 使用 upilot MCP endpoint：`http://127.0.0.1:8011/mcp`。
- 需要自动化验证 YAML 时，通过 MCP 执行。
- YAML MCP 验证必须使用 headed 模式。

批量执行时需要分片。单次 UIFlow batch 不要超过 15 个 YAML 文件。

## 报告输出

默认 MCP 报告根目录：

```text
Reports/upilot/UIFlowMcp
```

输出包括 Markdown 汇总、JSON 结果、单用例报告和失败截图。

## 页面接入规范

为了让 UI 自动化稳定：

- 给关键元素设置唯一 lower-kebab-case 名称，例如 `username-input`、`login-button`、`status-label`。
- 断言文本应落在稳定命名元素上，不要只输出到 Console。
- 示例/测试窗口中的按钮逻辑优先注册 `MouseUpEvent`。
- 窗口打开或 `PrepareForAutomatedTest()` 执行时要重置页面状态。
- 动态 UI 先 `wait_for_element`，再执行断言。

`Documentation~/templates/uiflow-minimal-page/` 提供了最小页面接入示例。

## 已知边界

UIFlow V1 不宣称完整支持：

- `ObjectField` Object Picker 中的拖拽。
- `CurveField` / `GradientField` 独立编辑弹窗。
- `ToolbarPopupSearchField` 弹出结果列表。
- `ToolbarBreadcrumbs` 的完整语义导航封装。
- `PropertyField` / `InspectorElement` 内所有生成子控件的直接语义自动化。
- 系统剪贴板、IME、多窗口协同、像素级视觉 diff。

## 相关文档

- upilot 根 README：`README.md`
- MCP 工具状态矩阵：`Documentation~/ToolStatus.md`
