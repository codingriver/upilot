# upilot MCP 工具状态矩阵

本文档用于跟踪 upilot MCP 工具的开发、验收和可用状态。状态矩阵是维护用清单，不替代 `tools/list` 返回的实时 schema。

## 状态口径

| 字段 | 建议取值 | 说明 |
| --- | --- | --- |
| 开发完成 | 是 / 升级中 / 否 | 是否已有对应 MCP 注册、Python facade 转发和 Unity Bridge 路由或实现。 |
| 验收通过 | 是 / 待补充 / 专项验收中 / 设备验收中 / 否 | 是否已有明确验收记录。没有逐项验收记录时使用“待补充”，不要默认写“是”。 |
| 可用状态 | 是 / 条件可用 / 暂不可用 | 用户当前是否可以直接调用。依赖环境、宏、设备、API Key 或平台模块时使用“条件可用”。 |

## 维护建议

- 新增 MCP 工具时，同步更新本表。
- 工具重命名时，旧名若不保留兼容别名，应在 README 中明确说明。
- 验收通过需要能追溯到测试、手工验收记录或 release checklist。
- 破坏性工具即使“可用状态”为“是”，也需要在调用前确认目标和影响范围。
- `unity_roslyn_execute` / `unity_roslyn_status` / `unity_roslyn_abort` 当前只是预留接口名，Roslyn 执行服务升级完成并验收前保持“暂不可用”。

## MCP 工具状态矩阵

### 基础状态与连接

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_open_editor` | 是 | 待补充 | 是 | 检查 Unity 连接，可选择启动 Unity。 |
| `unity_mcp_status` | 是 | 待补充 | 是 | 诊断 MCP 连接、会话、路径和超时配置。 |
| `unity_ensure_ready` | 是 | 待补充 | 是 | 检查连接、编译空闲和编辑模式状态。 |
| `unity_editor_state` | 是 | 待补充 | 是 | 获取 Editor 状态快照。 |
| `unity_editor_focus` | 是 | 待补充 | 是 | Windows 下将 Unity Editor 置前。 |
| `unity_editor_focus_state` | 是 | 待补充 | 是 | Windows 下查询 Unity Editor 焦点状态。 |
| `unity_playmode_start` | 是 | 待补充 | 是 | 会改变 Editor PlayMode 状态。 |
| `unity_playmode_stop` | 是 | 待补充 | 是 | 会改变 Editor PlayMode 状态。 |
| `unity_editor_delay` | 是 | 待补充 | 是 | 在 Unity 主线程延迟指定毫秒。 |

### 编译、错误与同步

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_compile` | 是 | 待补充 | 是 | 触发 Unity 编译。 |
| `unity_compile_status` | 是 | 待补充 | 是 | 获取最近一次编译状态。 |
| `unity_compile_errors` | 是 | 待补充 | 是 | 获取结构化编译错误。 |
| `unity_compile_wait` | 是 | 待补充 | 是 | 等待脚本编译结束。 |
| `unity_compile_wait_editor` | 是 | 待补充 | 是 | Unity 编辑器侧阻塞等待编译空闲。 |
| `unity_safe_compile_and_wait` | 是 | 待补充 | 是 | 触发编译、等待完成，并做 Domain Reload 后校验。 |
| `unity_sync_after_disk_write` | 是 | 待补充 | 是 | 磁盘写入后刷新 AssetDatabase，可选触发编译。 |

### 调用与运行时代码

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_reflection_find` | 是 | 待补充 | 是 | 通过反射搜索已加载程序集中的类型和方法。 |
| `unity_reflection_call` | 是 | 待补充 | 是 | 稳定调用已有业务方法的推荐入口。 |
| `reflection_eval` | 是 | 是 | 是 | 执行一条受限 C# 表达式语句；支持链式访问、调用、运算符、赋值和 JSON 变量。 |
| `unity_roslyn_execute` | 升级中 | 否 | 暂不可用 | 接口名已预留；Roslyn 执行服务正在升级改造。 |
| `unity_roslyn_status` | 升级中 | 否 | 暂不可用 | 接口名已预留；Roslyn 执行服务正在升级改造。 |
| `unity_roslyn_abort` | 升级中 | 否 | 暂不可用 | 接口名已预留；Roslyn 执行服务正在升级改造。 |

### 控制台、日志与诊断

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_console_get_logs` | 是 | 待补充 | 是 | 获取 Unity Console 日志。 |
| `unity_console_clear` | 是 | 待补充 | 是 | 清空 Unity Console。 |
| `unity_batch_diagnostics` | 是 | 待补充 | 是 | 一次性获取窗口布局、Console 摘要和编辑器状态。 |
| `unity_verify_window` | 是 | 待补充 | 是 | 窗口验收：编译等待、截图和诊断摘要。 |
| `unity_task_execute` | 是 | 待补充 | 是 | 带超时看门狗执行 MCP 工具。 |

### 编辑器窗口、菜单与输入

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_editor_windows_list` | 是 | 待补充 | 是 | 列出打开的 EditorWindow。 |
| `unity_editor_window_close` | 是 | 待补充 | 是 | 关闭可关闭的 EditorWindow。 |
| `unity_editor_window_set_rect` | 是 | 待补充 | 是 | 设置浮动 EditorWindow 位置和大小。 |
| `unity_editor_execute_command` | 是 | 待补充 | 是 | 通过菜单路径执行编辑器命令。 |
| `unity_menu_execute` | 是 | 待补充 | 是 | 执行 Unity 菜单项。 |
| `unity_menu_list` | 是 | 待补充 | 是 | 列出可用菜单项。 |
| `unity_editor_undo` | 是 | 待补充 | 是 | 执行 Undo。 |
| `unity_editor_redo` | 是 | 待补充 | 是 | 执行 Redo。 |
| `unity_mouse_event` | 是 | 待补充 | 是 | 执行 Unity 编辑器鼠标动作。 |
| `unity_keyboard_event` | 是 | 待补充 | 是 | 执行 Unity 编辑器键盘动作。 |
| `unity_drag_drop` | 是 | 待补充 | 是 | 执行 Unity 编辑器拖放。 |
| `unity_sceneview_navigate` | 是 | 待补充 | 是 | 导航 SceneView。 |

### 截图与视觉

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_screenshot_game_view` | 是 | 待补充 | 是 | 截取 Game 视图。 |
| `unity_screenshot_scene_view` | 是 | 待补充 | 是 | 截取 Scene 视图。 |
| `unity_screenshot_camera` | 是 | 待补充 | 是 | 截取指定 Camera。 |
| `unity_screenshot_editor_window` | 是 | 待补充 | 是 | 按窗口标题截取 EditorWindow。 |
| `unity_vision_analyze` | 是 | 待验收 | 条件可用 | 需配置 OPENAI_API_KEY 或 UNITYPILOT_OPENAI_API_KEY。 |

### 场景、选择与游戏对象

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_scene_create` | 是 | 待补充 | 是 | 新建空场景。 |
| `unity_scene_open` | 是 | 待补充 | 是 | 打开指定场景。 |
| `unity_scene_save` | 是 | 待补充 | 是 | 保存当前或指定场景。 |
| `unity_scene_load` | 是 | 待补充 | 是 | 加载场景。 |
| `unity_scene_set_active` | 是 | 待补充 | 是 | 设置活动场景。 |
| `unity_scene_list` | 是 | 待补充 | 是 | 列出已打开场景。 |
| `unity_scene_unload` | 是 | 待补充 | 是 | 卸载场景。 |
| `unity_scene_ensure_test` | 是 | 待补充 | 是 | 确保并打开自动化测试场景。 |
| `unity_selection_get` | 是 | 待补充 | 是 | 获取当前选择。 |
| `unity_selection_set` | 是 | 待补充 | 是 | 设置当前选择。 |
| `unity_selection_clear` | 是 | 待补充 | 是 | 清空当前选择。 |
| `unity_gameobject_create` | 是 | 待补充 | 是 | 创建 GameObject。 |
| `unity_gameobject_find` | 是 | 待补充 | 是 | 查找 GameObject。 |
| `unity_gameobject_modify` | 是 | 待补充 | 是 | 修改 GameObject 基础属性。 |
| `unity_gameobject_move` | 是 | 待补充 | 是 | 修改 Transform。 |
| `unity_gameobject_duplicate` | 是 | 待补充 | 是 | 复制 GameObject。 |
| `unity_gameobject_delete` | 是 | 待补充 | 是 | 破坏性操作，调用前应确认目标。 |

### 组件、预制体、材质与着色器

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_component_add` | 是 | 待补充 | 是 | 添加组件。 |
| `unity_component_remove` | 是 | 待补充 | 是 | 破坏性操作，调用前应确认目标。 |
| `unity_component_get` | 是 | 待补充 | 是 | 获取组件序列化属性。 |
| `unity_component_modify` | 是 | 待补充 | 是 | 修改组件属性。 |
| `unity_component_list` | 是 | 待补充 | 是 | 列出 GameObject 组件。 |
| `unity_prefab_create` | 是 | 待补充 | 是 | 创建 Prefab 资源。 |
| `unity_prefab_instantiate` | 是 | 待补充 | 是 | 实例化 Prefab。 |
| `unity_prefab_open` | 是 | 待补充 | 是 | 进入 Prefab 编辑模式。 |
| `unity_prefab_close` | 是 | 待补充 | 是 | 退出 Prefab 编辑模式。 |
| `unity_prefab_save` | 是 | 待补充 | 是 | 保存 Prefab 编辑模式修改。 |
| `unity_material_create` | 是 | 待补充 | 是 | 创建材质。 |
| `unity_material_modify` | 是 | 待补充 | 是 | 修改材质属性。 |
| `unity_material_assign` | 是 | 待补充 | 是 | 分配材质。 |
| `unity_material_get` | 是 | 待补充 | 是 | 获取材质属性信息。 |
| `unity_shader_list` | 是 | 待补充 | 是 | 列出可用 Shader。 |

### 资源与脚本文件

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_asset_find` | 是 | 待补充 | 是 | 搜索 AssetDatabase 资源。 |
| `unity_asset_create_folder` | 是 | 待补充 | 是 | 创建 Assets 文件夹。 |
| `unity_asset_copy` | 是 | 待补充 | 是 | 复制资源。 |
| `unity_asset_move` | 是 | 待补充 | 是 | 移动资源。 |
| `unity_asset_delete` | 是 | 待补充 | 是 | 破坏性操作，调用前应确认目标。 |
| `unity_asset_refresh` | 是 | 待补充 | 是 | 刷新 AssetDatabase。 |
| `unity_asset_get_info` | 是 | 待补充 | 是 | 获取资源元数据。 |
| `unity_asset_find_built_in` | 是 | 待补充 | 是 | 搜索 Unity 内置资源。 |
| `unity_asset_get_data` | 是 | 待补充 | 是 | 读取资源序列化属性。 |
| `unity_asset_modify_data` | 是 | 待补充 | 是 | 修改资源序列化属性。 |
| `unity_script_read` | 是 | 待补充 | 是 | 读取 C# 脚本。 |
| `unity_script_create` | 是 | 待补充 | 是 | 创建 C# 脚本。 |
| `unity_script_update` | 是 | 待补充 | 是 | 更新 C# 脚本。 |
| `unity_script_delete` | 是 | 待补充 | 是 | 删除 C# 脚本。 |

### 包、测试与构建

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_package_add` | 是 | 待补充 | 条件可用 | 依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_package_remove` | 是 | 待补充 | 条件可用 | 依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_package_list` | 是 | 待补充 | 条件可用 | 依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_package_search` | 是 | 待补充 | 条件可用 | 依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_test_run` | 是 | 待补充 | 条件可用 | 依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_test_results` | 是 | 待补充 | 条件可用 | 依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_test_list` | 是 | 待补充 | 条件可用 | 依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_build_start` | 是 | 待补充 | 条件可用 | 依赖目标平台模块、工程构建配置和本机环境。 |
| `unity_build_status` | 是 | 待补充 | 条件可用 | 依赖目标平台模块、工程构建配置和本机环境。 |
| `unity_build_cancel` | 是 | 待补充 | 条件可用 | 依赖目标平台模块、工程构建配置和本机环境。 |
| `unity_build_targets` | 是 | 待补充 | 条件可用 | 依赖目标平台模块、工程构建配置和本机环境。 |

### 批处理、自动修复与端到端验收

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_batch_execute` | 是 | 待补充 | 是 | 批量执行 Unity 操作。 |
| `unity_batch_cancel` | 是 | 待补充 | 是 | 取消批量操作。 |
| `unity_batch_results` | 是 | 待补充 | 是 | 查询批量操作结果。 |
| `unity_auto_fix_start` | 是 | 待补充 | 条件可用 | 自动修复能力建议人工监督使用。 |
| `unity_auto_fix_stop` | 是 | 待补充 | 条件可用 | 自动修复能力建议人工监督使用。 |
| `unity_auto_fix_status` | 是 | 待补充 | 条件可用 | 自动修复能力建议人工监督使用。 |
| `unity_editor_e2e_run` | 是 | 待补充 | 是 | 从 YAML 规格执行编辑器 E2E。 |

### 界面流程自动化

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_uiflow_run_file` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_suite` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_batch` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_force_reset` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_async` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_results` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
