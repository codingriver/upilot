# UnityUIFlow API 速查与最佳实践

版本：1.4.0
日期：2026-04-13
状态：以当前代码实现为准更新

## 内置动作速查

当前内置动作以当前代码实现为准，核心动作如下。

### 指针交互

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `click` | `selector`，可选 `button`、`modifiers` | 单击目标元素 |
| `double_click` | `selector`，可选 `button`、`modifiers` | 双击目标元素 |
| `hover` | `selector`，可选 `duration`、`modifiers` | 悬停目标元素 |
| `drag` | `selector` 或 `from`，以及 `to`，可选 `duration`、`button`、`modifiers` | 从元素或坐标拖拽到目标 |
| `scroll` | `selector`、`delta` | 在目标元素上发送滚轮事件 |

### 文本与键盘

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `press_key` | `key`，可选 `selector` | 发送按键 |
| `type_text` | `selector`、`value` | 优先走官方文本输入链路 |
| `type_text_fast` | `selector`、`value` | 直接写入文本值，不宣称真实输入 |
| `focus` | `selector` | 聚焦目标元素 |

### 命令与菜单

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `execute_command` | `selector`、`command` | 执行编辑器命令 |
| `validate_command` | `selector`、`command` | 预校验编辑器命令 |
| `open_context_menu` | `selector` | 打开上下文菜单 |
| `select_context_menu_item` | `item` | 选择上下文菜单项 |
| `open_popup_menu` | `selector` | 打开弹出菜单 |
| `select_popup_menu_item` | `item` | 选择弹出菜单项 |
| `assert_menu_item` | `item` | 断言菜单项存在且可用 |
| `assert_menu_item_disabled` | `item` | 断言菜单项存在且禁用 |
| `menu_item` | 可选 `selector`，必填 `item`，可选 `kind`、`mode` | 统一菜单 DSL，支持选择与菜单项启用状态断言 |

### 值与选择

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `set_value` | `selector`、`value` | 直接写入 `BaseField<T>` 等控件值 |
| `select_option` | `selector`，`value` 或 `index` 或 `indices` | 用于下拉、枚举、遮罩、Popup、Tag、Layer 等 |
| `select_list_item` | `selector`，`index` 或 `indices` | 选择 `ListView` / `MultiColumnListView` 行 |
| `drag_reorder` | `selector`、`from_index`、`to_index` | 对 `ListView.itemsSource` 执行逻辑重排 |
| `select_tree_item` | `selector`，`id` 或 `index` | 选择 `TreeView` / `MultiColumnTreeView` 节点 |
| `toggle_foldout` | `selector`，可选 `expand` | 折叠/展开 `Foldout` |
| `set_slider` | `selector`、`value` 或 `min_value/max_value` | 设置 `Slider` / `SliderInt` / `MinMaxSlider` |
| `select_tab` | `selector`，`label` 或 `index` | 切换 `TabView` 页签 |
| `sort_column` | `selector`，`column` 或 `index`，可选 `direction` | 设置 `MultiColumn*View` 排序 |
| `resize_column` | `selector`，`column` 或 `index`，`width` | 设置 `MultiColumn*View` 列宽 |
| `set_bound_value` | `selector`、`binding_path`、`value` | 对绑定字段执行语义赋值 |
| `assert_bound_value` | `selector`、`binding_path`、`expected` | 断言绑定字段值 |
| `navigate_breadcrumb` | `selector`，以及 `label` 或 `index` | 导航 `ToolbarBreadcrumbs` |
| `set_split_view_size` | `selector`、`size`，可选 `pane` | 设置 `TwoPaneSplitView` 固定 pane 尺寸 |
| `page_scroller` | `selector`，可选 `direction`、`pages`、`page_size` | 按分页语义驱动 `Scroller` |

### 等待与断言

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `wait` | `duration` | 固定等待 |
| `wait_for_element` | `selector`，可选 `timeout` | 等待元素出现且可见 |
| `assert_visible` | `selector` | 断言元素可见 |
| `assert_not_visible` | `selector`，可选 `timeout` | 断言元素不可见 |
| `assert_text` | `selector`、`expected` | 断言文本完全相等 |
| `assert_text_contains` | `selector`、`expected` | 断言文本包含片段 |
| `assert_value` | `selector`、`expected` | 断言控件值 |
| `assert_enabled` | `selector` | 断言元素可用 |
| `assert_disabled` | `selector` | 断言元素不可用 |
| `assert_property` | `selector`、`property`、`expected` | 断言属性值 |
| `screenshot` | 可选 `tag` | 生成截图附件 |

## IMGUI 动作速查（新增）

> 适用于 `EditorGUILayout` / `GUILayout` 绘制的 IMGUI 控件。选择器语法与 UIToolkit 完全不同。

### 动作

| 动作 | 关键参数 | 说明 |
| --- | --- | --- |
| `imgui_click` | `selector` | 左键点击控件中心 |
| `imgui_double_click` | `selector` | 快速双击 |
| `imgui_right_click` | `selector` | 右键点击 |
| `imgui_hover` | `selector` | 悬停（MouseMove） |
| `imgui_type` | `selector`、`text` | 逐字符输入 |
| `imgui_focus` | `selector` | 点击获取焦点 |
| `imgui_scroll` | `selector`（可选）、`delta` | 滚轮事件 |
| `imgui_select_option` | `selector`、`option` 或 `index` | Dropdown 键盘导航选择 |
| `imgui_press_key` | `key`，可选 `selector` | 单按键 |
| `imgui_press_key_combination` | `keys`，可选 `selector` | 组合键（如 `Ctrl+A`） |
| `imgui_read_value` | `selector`、`bag_key` | 读取值存入 SharedBag |
| `imgui_assert_text` | `selector`、`text` | 断言文本 |
| `imgui_assert_visible` | `selector` | 断言可见 |
| `imgui_assert_value` | `selector`、`expected` | 尽力断言值 |
| `imgui_wait` | `selector`、`timeout` | 轮询等待控件出现 |

### 选择器语法

| 语法 | 含义 |
| --- | --- |
| `gui(button)` | 匹配第一个 Button |
| `gui(button, text="OK")` | 匹配文本为 OK 的 Button |
| `gui(textfield, index=2)` | 匹配第 3 个 TextField |
| `gui(toggle, text="Enabled")` | 匹配 label 为 Enabled 的 Toggle |
| `gui(group="Settings")` | 匹配 Settings 组 |
| `gui(group="Settings" > button, text="Apply")` | 在 Settings 组内匹配 Apply 按钮 |
| `gui(textfield, control_name="username")` | 匹配 ControlName 为 username 的 TextField |
| `gui(focused)` | 匹配当前焦点控件 |

## 选择器速查

| 语法 | 含义 |
| --- | --- |
| `#login-button` | 按 `name` 查找 |
| `.btn-primary` | 按 class 查找 |
| `Button` | 按类型名查找 |
| `[tooltip=Save]` | 按属性值查找 |
| `#panel .btn` | 后代选择器 |
| `#panel > .btn` | 直接子级选择器 |
| `.item:first-child` | 首子元素伪类 |

## 常见值格式

| 场景 | 格式示例 |
| --- | --- |
| `Vector3Field` | `1,2,3` |
| `RectField` | `0,0,100,50` |
| `BoundsField` | `0,0,0,1,1,1` |
| `ColorField` | `#FF8040CC` 或 `1,0.5,0.25,0.8` |
| `ObjectField` | `Assets/.../file.asset` 或 `guid:xxxxxxxx...` |
| `CurveField` | `0:0:1:1;1:2:0:0` |
| `GradientField` | `0:#FF0000FF;1:#00FF00FF|0:1;1:0.5` |
| `select_list_item.indices` | `1,3,5` |
| `scroll.delta` | `0,120` |
| `modifiers` | `shift,ctrl` |

## CLI 参数速查

| 参数 | 说明 |
| --- | --- |
| `-unityUIFlow.testFilter` | 按 YAML 文件名或用例名过滤 |
| `-unityUIFlow.headed` | 开启/关闭 Headed 模式 |
| `-unityUIFlow.reportPath` | 指定报告输出目录 |
| `-unityUIFlow.screenshotOnFailure` | 失败时自动截图 |
| `-unityUIFlow.requireOfficialHost` | 强制要求官方宿主桥接 |
| `-unityUIFlow.requireOfficialPointerDriver` | 强制要求官方指针驱动 |
| `-unityUIFlow.requireInputSystemKeyboardDriver` | 强制要求 InputSystem 键盘高保真链路 |
| `-unityUIFlow.preStepDelayMs` | 每步执行前注入固定延时 |

## 当前驱动口径

| 能力 | 当前实现 |
| --- | --- |
| 指针动作 | 官方 `PanelSimulator` 优先；非官方入口 fallback |
| 键盘/文本 | 官方 `PanelSimulator` 优先；InputSystem 测试桥接补充 |
| 快速写值 | `set_value` / `type_text_fast` 保留直接写值 |
| 菜单动作 | 官方 `ContextMenuSimulator` / `PopupMenuSimulator` |
| 命令动作 | 官方 `PanelSimulator.ExecuteCommand/ValidateCommand` 优先 |

## 最佳实践

1. 优先给被测元素设置稳定 `name`，然后用 `#name` 选择器。
2. 对真正依赖用户语义的输入用 `type_text` / `press_key`，只在需要稳定直写时使用 `type_text_fast` / `set_value`。
3. 对 `ObjectField`、`CurveField`、`GradientField` 优先使用当前支持的值 DSL，不要把浮窗交互写进 YAML。
4. 对 `PropertyField`、`InspectorElement` 通过已生成的后代控件命名后再自动化，不要假设内部子树永远稳定。
5. 列表重排、列排序、列宽调整优先用 `drag_reorder`、`sort_column`、`resize_column`，不要依赖易碎的内部拖拽结构。
6. 需要验证菜单项时，先 `open_context_menu` / `open_popup_menu`，再执行 `assert_menu_item*` 或 `select_*_menu_item`。
7. CI 或严格验收场景打开 `requireOfficialHost` / `requireOfficialPointerDriver` / `requireInputSystemKeyboardDriver`，避免 fallback 被误判为高保真通过。

## 已知边界

| 范围 | 当前结论 |
| --- | --- |
| `ObjectField` Object Picker / DragAndDrop | 不支持 |
| `CurveField` / `GradientField` 独立编辑器浮窗 | 不支持 |
| `ToolbarPopupSearchField` 弹出结果列表 | 不支持 |
| `ToolbarBreadcrumbs` 专用“按 label / index 导航”动作 | 未封装 |
| `PropertyField` / `InspectorElement` 自身统一语义赋值 | 未封装 |
| `IMGUIContainer` 内部内容 | 不支持 |
| IME / 系统剪贴板 / 多窗口协同 / 像素级视觉 diff | 不支持 |

## 与 Playwright 的类比

| Playwright | UnityUIFlow |
| --- | --- |
| `page.locator('#btn')` | `selector: "#btn"` |
| `locator.click()` | `click` |
| `locator.fill()` | `type_text` / `type_text_fast` / `set_value` |
| `locator.dragTo()` | `drag` |
| `page.waitForSelector()` | `wait_for_element` |
| `expect(locator).toHaveText()` | `assert_text` |
| `expect(locator).toHaveValue()` | `assert_value` |
| `headed` | Headed 窗口 + `RuntimeController` |

## 2026-04-13 更新摘要

- 动作清单已从早期版本的 16 个扩展为当前真实实现的 38 个。
- 已补充 `focus`、`set_value`、`select_option`、`drag_reorder`、`sort_column`、`resize_column`、菜单动作、命令动作。
- 已补充复杂字段 DSL、Toolbar 系列、`PropertyField` / `InspectorElement` 相关最佳实践和边界。
- 全文口径已改为“代码为准”，不再把已实现动作继续写成待开发。
