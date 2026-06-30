# upilot MCP 工具状态矩阵

本文档用于跟踪 upilot MCP 工具的开发、验收和可用状态。状态矩阵是维护用清单，不替代 `tools/list` 返回的实时 schema。

最近同步：2026-06-30，`tools/list` 返回 116 个工具。

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
- `unity_roslyn_execute` / `unity_roslyn_status` / `unity_roslyn_abort` 已在 MCP schema 中暴露。它们用于临时诊断和动态代码执行，稳定业务自动化仍应优先使用 `unity_reflection_call` 或 `reflection_eval`。

## MCP 工具状态矩阵

### 基础状态与连接

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_open_editor` | 是 | 是 | 是 | 2026-06-30 自动验收通过：空 command 检查现有 Unity 连接成功。 |
| `unity_mcp_status` | 是 | 是 | 是 | 2026-06-30 自动验收通过：返回 MCP/Unity 会话、路径、编译与超时状态。 |
| `unity_ensure_ready` | 是 | 是 | 是 | 2026-06-30 自动验收通过：确认连接、编译空闲且处于编辑模式。 |
| `unity_editor_state` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功获取 Editor 状态快照。 |
| `unity_editor_focus` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Windows 下成功将 Unity Editor 置前。 |
| `unity_editor_focus_state` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Windows 下成功查询 Unity Editor 焦点状态。 |
| `unity_playmode_start` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功进入 PlayMode；会改变 Editor PlayMode 状态。 |
| `unity_playmode_stop` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功退出 PlayMode 并恢复编辑模式。 |
| `unity_editor_delay` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Unity 主线程 50ms 延迟调用成功。 |

### 编译、错误与同步

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_compile` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功触发 Unity 编译请求。 |
| `unity_compile_status` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取最近一次编译状态。 |
| `unity_compile_errors` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取结构化编译错误，当前错误数为 0。 |
| `unity_compile_wait` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在编译空闲状态下等待成功返回。 |
| `unity_compile_wait_editor` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Unity 编辑器侧等待编译空闲成功。 |
| `unity_safe_compile_and_wait` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功触发编译、等待完成并完成 Domain Reload 后校验。 |
| `unity_sync_after_disk_write` | 是 | 是 | 是 | 2026-06-30 自动验收通过：编译空闲状态下成功刷新 AssetDatabase。 |

### 调用与运行时代码

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_reflection_find` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功搜索 `UnityEngine.Application` 并返回方法列表。 |
| `unity_reflection_call` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功调用 `UnityEngine.Application.get_unityVersion`。 |
| `reflection_eval` | 是 | 是 | 是 | 执行一条受限 C# 表达式语句；支持链式访问、调用、运算符、赋值和 JSON 变量。 |
| `unity_roslyn_execute` | 是 | 待补充 | 条件可用 | 依赖 Unity 当前运行时可用的 C# evaluator；2026-06-30 自动验收发现 Unity 6000.6 CodeDom 路径将 warnings 计入 Errors，片段未实际执行，待修正后复验。 |
| `unity_roslyn_status` | 是 | 待补充 | 条件可用 | 依赖 `unity_roslyn_execute` 成功产生 executionId；2026-06-30 可查询 executionId，但 execute 未完成有效执行，暂不标通过。 |
| `unity_roslyn_abort` | 是 | 待补充 | 条件可用 | 依赖存在运行中的 Roslyn executionId；当前 execute 路径未能形成可中止的有效长任务，待补充专项验收。 |

### 控制台、日志与诊断

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_console_mark_logs` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功标记 Unity Console 当前末尾游标。 |
| `unity_console_tail_logs` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功从 Console 游标读取新增日志。 |
| `unity_console_search_logs` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功搜索 Unity Console 全量日志。 |
| `unity_console_clear` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功清空 Unity Console。 |
| `unity_batch_diagnostics` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功一次性获取窗口布局、Console 摘要和编辑器状态。 |
| `unity_verify_window` | 是 | 是 | 是 | 2026-06-30 自动验收通过：以 Scene 窗口执行窗口验收成功。 |
| `unity_task_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：通过看门狗包装成功执行 `unity_ensure_ready`。 |

### 编辑器窗口、菜单与输入

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_editor_windows_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出打开的 EditorWindow。 |
| `unity_editor_window_close` | 是 | 待补充 | 暂不可用 | 2026-06-30 自动验收阻塞：MCP 已暴露，但 Unity Bridge 返回 `COMMAND_NOT_FOUND: editor.window.close`，需注册路由后复验。 |
| `unity_editor_window_set_rect` | 是 | 待补充 | 暂不可用 | 2026-06-30 自动验收阻塞：MCP 已暴露，但 Unity Bridge 返回 `COMMAND_NOT_FOUND: editor.window.setRect`，需注册路由后复验。 |
| `unity_editor_execute_command` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 `Window/General/Console` 编辑器命令。 |
| `unity_menu_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 `GameObject/Camera` 菜单项。 |
| `unity_menu_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出可用菜单项。 |
| `unity_editor_undo` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 1 步 Undo。 |
| `unity_editor_redo` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 1 步 Redo。 |
| `unity_mouse_event` | 是 | 是 | 是 | 2026-06-30 自动验收通过：向 Scene 窗口注入 `move` 鼠标事件成功，返回 `move:scene:uitoolkit`。 |
| `unity_keyboard_event` | 是 | 是 | 是 | 2026-06-30 自动验收通过：向 Console 窗口注入 `F5` keypress 成功。 |
| `unity_drag_drop` | 是 | 待补充 | 暂不可用 | 2026-06-30 自动验收阻塞：资产拖拽模拟返回空引用异常，需修复拖放目标窗口/事件路径后复验。 |
| `unity_sceneview_navigate` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功设置 SceneView pivot、size、rotation 与透视模式。 |

### 截图与视觉

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_screenshot_game_view` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功截取 320x180 Game 视图 PNG。 |
| `unity_screenshot_scene_view` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功截取 320x180 Scene 视图 PNG。 |
| `unity_screenshot_camera` | 是 | 是 | 是 | 2026-06-30 自动验收通过：使用临时 Camera 成功截取 320x180 PNG。 |
| `unity_screenshot_editor_window` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功按 `Scene` 窗口标题截取 EditorWindow PNG。 |
| `unity_vision_analyze` | 是 | 待验收 | 条件可用 | 需配置 OPENAI_API_KEY 或 UNITYPILOT_OPENAI_API_KEY。 |

### 场景、选择与游戏对象

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_scene_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功新建 `scene-op-acceptance` 场景，实际落盘为 `Assets/scene-op-acceptance.unity`。 |
| `unity_scene_open` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功以 single 模式打开临时场景并恢复验收场景。 |
| `unity_scene_save` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功保存当前已加载临时场景。 |
| `unity_scene_load` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功以 additive 模式加载临时场景。 |
| `unity_scene_set_active` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功在两个临时场景间切换活动场景。 |
| `unity_scene_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出已打开场景。 |
| `unity_scene_unload` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功卸载 additive 加载的临时场景。 |
| `unity_scene_ensure_test` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功确保并打开 `Assets/UPilotAcceptance/upilot-acceptance.unity` 临时验收场景。 |
| `unity_selection_get` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功获取当前选择。 |
| `unity_selection_set` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功按资源路径选中临时验收场景资产并通过 `selection_get` 复查。 |
| `unity_selection_clear` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功清空资源选择并通过 `selection_get` 复查 selectionCount=0。 |
| `unity_gameobject_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在临时验收场景中成功创建 Cube GameObject。 |
| `unity_gameobject_find` | 是 | 是 | 是 | 2026-06-30 自动验收通过：按名称成功查找临时 GameObject。 |
| `unity_gameobject_modify` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：Unity 6 返回完整 EntityId 但 schema/DTO 使用 int 导致截断，按 ID 找不到对象；需支持完整 EntityId 或在 Unity 2022 InstanceID 环境复验。 |
| `unity_gameobject_move` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：同 Unity 6 EntityId 截断问题，按 ID 找不到对象；需支持完整 EntityId 或在 Unity 2022 InstanceID 环境复验。 |
| `unity_gameobject_duplicate` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：同 Unity 6 EntityId 截断问题，按 ID 找不到对象；需支持完整 EntityId 或在 Unity 2022 InstanceID 环境复验。 |
| `unity_gameobject_delete` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：同 Unity 6 EntityId 截断问题，按 ID 找不到对象；破坏性操作仍需调用前确认目标。 |

### 组件、预制体、材质与着色器

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_component_add` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖 GameObject ID，当前 Unity 6 EntityId 被 int DTO 截断后找不到对象；需支持完整 EntityId 后复验。 |
| `unity_component_remove` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖 GameObject ID，当前 Unity 6 EntityId 被 int DTO 截断后找不到对象；破坏性操作仍需确认目标。 |
| `unity_component_get` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖 GameObject ID，当前 Unity 6 EntityId 被 int DTO 截断后找不到对象；需支持完整 EntityId 后复验。 |
| `unity_component_modify` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖 GameObject ID，当前 Unity 6 EntityId 被 int DTO 截断后找不到对象；需支持完整 EntityId 后复验。 |
| `unity_component_list` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖 GameObject ID，当前 Unity 6 EntityId 被 int DTO 截断后找不到对象；需支持完整 EntityId 后复验。 |
| `unity_prefab_create` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：创建 Prefab 依赖源 GameObject ID，当前 Unity 6 EntityId/int DTO 不匹配；需修复 ID schema 后复验。 |
| `unity_prefab_instantiate` | 是 | 待补充 | 条件可用 | 依赖可用 Prefab 资源；2026-06-30 因 `unity_prefab_create` 受 Unity 6 EntityId 问题阻塞，未能生成临时 Prefab 做闭环验收。 |
| `unity_prefab_open` | 是 | 待补充 | 条件可用 | 依赖可用 Prefab 资源；2026-06-30 因临时 Prefab 创建受 Unity 6 EntityId 问题阻塞，待补充复验。 |
| `unity_prefab_close` | 是 | 待补充 | 条件可用 | 依赖已打开 Prefab 编辑模式；2026-06-30 因临时 Prefab 创建受 Unity 6 EntityId 问题阻塞，待补充复验。 |
| `unity_prefab_save` | 是 | 待补充 | 条件可用 | 依赖已打开 Prefab 编辑模式；2026-06-30 因临时 Prefab 创建受 Unity 6 EntityId 问题阻塞，待补充复验。 |
| `unity_material_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在临时资源目录成功创建材质。 |
| `unity_material_modify` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功修改临时材质颜色属性。 |
| `unity_material_assign` | 是 | 待补充 | 条件可用 | 2026-06-30 自动验收阻塞：依赖目标 GameObject ID，当前 Unity 6 EntityId/int DTO 不匹配；需修复 ID schema 后复验。 |
| `unity_material_get` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取临时材质属性信息。 |
| `unity_shader_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出可用 Shader。 |

### 资源与脚本文件

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_asset_find` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功搜索临时材质资源。 |
| `unity_asset_create_folder` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功创建 `Assets/UPilotAcceptance/TempAssets` 临时目录。 |
| `unity_asset_copy` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功复制临时材质资源。 |
| `unity_asset_move` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功移动临时材质资源。 |
| `unity_asset_delete` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功删除临时材质资源；破坏性操作仍需调用前确认目标。 |
| `unity_asset_refresh` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功刷新 AssetDatabase。 |
| `unity_asset_get_info` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取临时材质资源元数据。 |
| `unity_asset_find_built_in` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功搜索 Unity 内置 Material 资源。 |
| `unity_asset_get_data` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取临时材质序列化属性。 |
| `unity_asset_modify_data` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功修改临时材质 `m_Name` 序列化属性。 |
| `unity_script_read` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取临时 C# 脚本。 |
| `unity_script_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功创建最小合法临时 C# 脚本。 |
| `unity_script_update` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功更新临时 C# 脚本内容。 |
| `unity_script_delete` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功删除临时 C# 脚本并等待编译恢复空闲。 |

### 包、测试与构建

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_package_add` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功临时添加 `com.unity.nuget.newtonsoft-json`；依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_package_remove` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功移除临时添加的 `com.unity.nuget.newtonsoft-json` 并恢复 manifest；依赖 Unity Package Manager 和网络/registry 可用性。 |
| `unity_package_list` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功列出已安装包；依赖 Unity Package Manager 和 registry/本地缓存可用性。 |
| `unity_package_search` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功查询 Unity Package Manager registry；依赖网络/registry 可用性。 |
| `unity_test_run` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功启动 EditMode 测试运行；依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_test_results` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功读取最近一次测试结果；依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_test_list` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功列出测试用例；依赖 Unity Test Framework 和项目内测试用例。 |
| `unity_build_start` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：使用 `StandaloneWindows64` 和临时场景构建成功，错误/警告为 0；依赖目标平台模块、工程构建配置和本机环境。 |
| `unity_build_status` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功读取最近一次构建状态 `succeeded`；依赖目标平台模块、工程构建配置和本机环境。 |
| `unity_build_cancel` | 是 | 待补充 | 条件可用 | 依赖运行中的构建任务；2026-06-30 无活动构建时返回 `NOT_FOUND`，真实取消路径待补充专项验收。 |
| `unity_build_targets` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功读取支持的构建目标；依赖 Unity 安装的目标平台模块和本机环境。 |

### 批处理、自动修复与端到端验收

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_batch_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：顺序批量执行 `editor.delay` 与 `console.clear` 成功，completed=2、failed=0。 |
| `unity_batch_cancel` | 是 | 待补充 | 条件可用 | 依赖可获取运行中的 batchId；当前 `unity_batch_execute` 同步返回已完成结果，短批次完成后取消返回 NOT_FOUND，待补充长任务取消验收。 |
| `unity_batch_results` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功按 batchId 查询已完成批处理结果。 |
| `unity_auto_fix_start` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：在无编译错误环境下启动空跑循环成功；自动修复能力建议人工监督使用。 |
| `unity_auto_fix_stop` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：对运行中自动修复 loopId 调用停止接口成功；自动修复能力建议人工监督使用。 |
| `unity_auto_fix_status` | 是 | 是 | 条件可用 | 2026-06-30 自动验收通过：成功查询 idle/running/success 状态；自动修复能力建议人工监督使用。 |
| `unity_editor_e2e_run` | 是 | 是 | 是 | 2026-06-30 自动验收通过：执行 `smoke_editor_state.yaml` 成功，stepCount=2，passed=true。 |

### 界面流程自动化

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_uiflow_run_file` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_suite` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_batch` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_force_reset` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_run_async` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_uiflow_results` | 是 | 专项验收中 | 条件可用 | 需 Unity 6+ 且启用 UNITYPILOT_ENABLE_UIFLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
