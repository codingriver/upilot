# UPilot MCP 工具状态矩阵

本文档用于跟踪 UPilot MCP 工具的开发、验收和可用状态。状态矩阵是维护用清单，不替代 `tools/list` 和 `unity_capabilities_get` 返回的实时 schema。

最近同步：2026-08-15，`tools/list` 以当前 MCP 实时返回为准；本表已同步可信窗口截图、一键包验收、`no_tests`、紧凑 Operation 响应和 Shader 专项诊断。

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
- Roslyn 动态编译工具已从 MCP schema 中移除。稳定业务自动化使用需要项目写入授权且禁止自动重试的 `unity_reflection_call`；只读发现使用 `unity_type_exists` / `unity_reflection_find`，表达式级诊断使用 `reflection_eval`。

## MCP 工具状态矩阵

### 基础状态与连接

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_open_editor` | 是 | 是 | 是 | 2026-06-30 自动验收通过：空 command 检查现有 Unity 连接成功。 |
| `unity_mcp_status` | 是 | 是 | 是 | 2026-06-30 自动验收通过：返回 MCP/Unity 会话、路径、编译与超时状态。 |
| `unity_capabilities_get` | 是 | 是 | 是 | 返回注册表、会话、路径和能力状态；区分已注册、可用与当前可调用。 |
| `unity_tools_find` | 是 | 是 | 是 | 支持 exact 标记、`callableNow` 过滤及独立近似结果；写权限关闭时 CSV patch 不进入可调用结果。 |
| `unity_tool_call` | 是 | 契约通过 | 是 | 调用已注册但客户端未注入的工具；拒绝递归，并复用注册表的写权限/Flow/handler 检查。 |
| `unity_ensure_ready` | 是 | 是 | 是 | 2026-06-30 自动验收通过：确认连接、编译空闲且处于编辑模式。 |
| `unity_editor_state` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功获取 Editor 状态快照。 |
| `unity_editor_focus` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Windows 下成功将 Unity Editor 置前。 |
| `unity_editor_focus_state` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Windows 下成功查询 Unity Editor 焦点状态。 |
| `unity_playmode_start` | 是 | 是 | 是 | 2026-08-06 联机复验：仅在权威 Context 确认 `playModeState=play/isPlaying=true` 后返回 `confirmed=true`。 |
| `unity_playmode_stop` | 是 | 是 | 是 | 2026-08-06 联机复验：等待权威 Context 确认 EditMode 后返回；随后 readiness 约 0.09 秒内恢复。 |
| `unity_editor_delay` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Unity 主线程 50ms 延迟调用成功。 |

### 编译、错误与同步

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_compile` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功触发 Unity 编译请求。 |
| `unity_compile_status` | 是 | 是 | 是 | 2026-08-06 三项目联机复验：终态 `phase=completed`，queued/accepted/started/finished 时间完整有序；快速无变更编译也会补齐 `finishedAt`。 |
| `unity_compile_errors` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取结构化编译错误，当前错误数为 0。 |
| `unity_compile_wait` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在编译空闲状态下等待成功返回。 |
| `unity_compile_wait_editor` | 是 | 是 | 是 | 2026-06-30 自动验收通过：Unity 编辑器侧等待编译空闲成功。 |
| `unity_safe_compile_and_wait` | 是 | 是 | 是 | 2026-08-09 F/D 各连续两轮及最终轮通过：Domain Reload 后瞬时 `COMPILE_ERROR` 继续持久错误复核，避免首轮假失败；权威 EditMode、结构化错误 0。 |
| `unity_sync_after_disk_write` | 是 | 是 | 是 | 2026-08-06 双 Unity 2022 联机通过：现有编译返回 `attachedToExistingCompile=true` 且无错误签名；终态确认后才返回 `compileCompleted=true`，Context 与阶段时间戳一致。 |

### 调用与运行时代码

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_reflection_find` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功搜索 `UnityEngine.Application` 并返回方法列表。 |
| `unity_reflection_call` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功调用 `UnityEngine.Application.get_unityVersion`。 |
| `reflection_eval` | 是 | 是 | 是 | 执行一条受限 C# 表达式语句；支持链式访问、调用、运算符、赋值和 JSON 变量。不是脚本执行器，不支持局部变量、控制流、lambda/LINQ、async/await、任意对象构造或动态编译。 |

### 控制台、日志与诊断

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_console_mark_logs` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功标记 Unity Console 当前末尾游标。 |
| `unity_console_tail_logs` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功从 Console 游标读取新增日志。 |
| `unity_console_search_logs` | 是 | 契约通过，项目联机待补 | 是 | 兼容 `query/maxCount`，回显 `effectiveQuery/effectiveContains/matchedFields/scannedCount`，避免参数静默失效。 |
| `unity_console_clear` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功清空 Unity Console。 |
| `unity_console_capture_start` | 是 | 是 | 是 | 持久会话返回 `sessionId`，长 Operation 可自动关联。 |
| `unity_console_capture_status` | 是 | 是 | 是 | 返回计数、文件大小、活动状态和写入错误。 |
| `unity_console_capture_read` | 是 | 是 | 是 | 支持 `fromSequence/toSequence`、regex、稳定 snapshot、continuation token、总匹配数、扫描范围/计数和耗时；采集时增量维护稀疏索引。68,052,043 字节/23,648 条历史会话删除索引后最终冷启动首读 1.073 秒，5 模式实际 33 条全部返回；另以 210 条匹配验证 150 条分页上限，全程 sequence 无重复无丢失。 |
| `unity_console_capture_stop` | 是 | 是 | 是 | 可幂等终结丢失 SessionState 指针的历史 active manifest；现场 5 MB 会话写出 summary/SHA256 后，F/D 活动会话均为 0。 |
| `unity_console_capture_list` | 是 | 是 | 是 | 兼容顶层与 `session.sessionId` 结构并可列出历史会话。 |
| `unity_console_capture_cleanup` | 是 | 待 apply 验收 | 条件可用 | 两阶段 dry-run/confirmToken 清理；apply 需要显式删除授权。 |
| `unity_batch_diagnostics` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功一次性获取窗口布局、Console 摘要和编辑器状态。 |
| `unity_verify_window` | 是 | 是 | 是 | 2026-08-02 契约测试补充：`windowMatch` 以 `unity_editor_windows_list` 为窗口存在性 truth；旧 `windowDiagnostics` 保留为 legacy 诊断。 |
| `unity_task_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：通过看门狗包装成功执行 `unity_ensure_ready`。 |

### 编辑器窗口、菜单与输入

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_editor_windows_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出打开的 EditorWindow。 |
| `unity_editor_window_close` | 是 | 是 | 是 | 2026-06-30 自动验收通过：补注册 Bridge 路由后成功关闭浮动 `upilot` 窗口，并通过菜单恢复窗口。 |
| `unity_editor_window_set_rect` | 是 | 是 | 是 | 2026-06-30 自动验收通过：补注册 Bridge 路由后成功设置浮动 `upilot` 窗口位置和大小。 |
| `unity_editor_execute_command` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 `Window/General/Console` 编辑器命令。 |
| `unity_menu_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 `GameObject/Camera` 菜单项。 |
| `unity_menu_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出可用菜单项。 |
| `unity_editor_undo` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 1 步 Undo。 |
| `unity_editor_redo` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功执行 1 步 Redo。 |
| `unity_mouse_event` | 是 | 是 | 是 | 2026-06-30 自动验收通过：向 Scene 窗口注入 `move` 鼠标事件成功，返回 `move:scene:uitoolkit`。 |
| `unity_keyboard_event` | 是 | 是 | 是 | 2026-06-30 自动验收通过：向 Console 窗口注入 `F5` keypress 成功。 |
| `unity_drag_drop` | 是 | 是 | 是 | 2026-06-30 自动验收通过：对编辑器内部拖拽事件异常降级为 `event_warning` 后，自定义拖放注入路径返回 ok。 |
| `unity_sceneview_navigate` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功设置 SceneView pivot、size、rotation 与透视模式。 |

### 截图与视觉

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_screenshot_game_view` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功截取 320x180 Game 视图 PNG。 |
| `unity_screenshot_scene_view` | 是 | 是 | 是 | 2026-08-15 联机返回 SceneView 精确 HWND、`PrintWindow`、`pixelSourceVerified=true/occlusionSensitive=false`；相机回退明确标记 degraded。 |
| `unity_screenshot_camera` | 是 | 是 | 是 | 2026-06-30 自动验收通过：使用临时 Camera 成功截取 320x180 PNG。 |
| `unity_screenshot_editor_window` | 是 | 是 | 是 | 2026-08-15 Console 窗口在非前台状态仍通过精确 Unity PID/HWND 离屏捕获，`pixelSourceVerified=true/occlusionSensitive=false`。 |
| `unity_screenshot_save` | 是 | 是 | 是 | 可信窗口截图 `new-editor-window-trust.png`：1016x628、189877 bytes、SHA256 `98e18e25...fb9863`，完整返回像素来源元数据。 |
| `unity_screenshot_pixel_stats` | 是 | Python 通过 | 是 | 只读工程内 PNG，返回区域近黑/透明比例与亮度/Alpha 直方图，不返回原始像素。 |
| `unity_screenshot_compare` | 是 | Python 通过 | 是 | 比较同尺寸 PNG 的差异像素比例、平均通道差和近黑比例变化。 |

### 纹理导入

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_texture_importer_get` | 是 | Unity 2022 编译通过，资产待补 | 是 | 返回 Mipmap、Alpha Source/Transparency、sRGB、Wrap、Filter、压缩、尺寸、可读性和平台覆盖。 |
| `unity_texture_importer_patch` | 是 | Python/Unity 2022 编译通过，资产待补 | 条件可用 | `dryRun -> confirmToken -> apply`；令牌绑定资源和 `.meta` 哈希，apply 需要写权限。 |
| `unity_asset_reimport` | 是 | Unity 2022 编译通过，资产待补 | 条件可用 | 强制重导入单个现有 `Assets/...` 资源，需写权限。 |

### 资源依赖审计

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_asset_dependencies` | 是 | Python/Unity 2022 编译通过，资产待补 | 是 | 返回直接/递归依赖的路径、类型、GUID 和 direct 标记；SerializedObject 的 ObjectReference 同时显示资源路径或运行时 instanceId。 |

### 运行时导航与性能

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_navmesh_status` | 是 | Unity 2022 编译通过，场景待补 | 是 | 返回 Surface/DataInstance 可观测信息、Agent 统计、preUpdate/观察版本和 triangulation；注册矩阵不可观测时明确使用推导 Bounds。 |
| `unity_navmesh_sample` | 是 | Unity 2022 编译通过，场景待补 | 是 | 支持批量点、半径、areaMask/agentType，返回命中、距离、最近边缘和推导 Surface 匹配。 |
| `unity_navmesh_triangulation_summary` | 是 | Unity 2022 编译通过，场景待补 | 是 | 返回顶点/三角形/区域数和世界 Bounds。 |
| `unity_profiler_capture_start` | 是 | Unity 2022 编译通过，战场待补 | 是 | 支持 marker 白名单/正则、最大 marker 数、项目 telemetry 静态采样器和 baseline JSON；不可用计数器写入 `unavailableCounters`。 |
| `unity_profiler_capture_status` | 是 | Unity 2022 编译通过，战场待补 | 是 | 返回进度、样本数、时间、选中/不可用 marker 和产物路径。 |
| `unity_profiler_capture_stop` | 是 | Unity 2022 编译通过，战场待补 | 是 | 终态生成 JSON/CSV、P50/P95/P99、Top 20 marker、峰值帧、GC/托管堆/组件统计和 baseline 对比；不宣称完整 Timeline 树。 |

KingShotBattle 的 D/F 项目侧已提供可选业务采样器：类型
`IGG.Game.Module.KingShotBattle.Editor.KingShotBattleMcpBridge`，方法
`GetProfilerTelemetry`。它把流程版本、关卡、逻辑帧、战斗时间、双方单位与存活数、
英雄/怪物/建筑/陷阱、Buff、弹道和世界技能特效规模写入每个 Profiler 样本及峰值帧。

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
| `unity_gameobject_modify` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功重命名并修改临时 Cube 属性。 |
| `unity_gameobject_move` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功修改临时 Cube Transform。 |
| `unity_gameobject_duplicate` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功复制临时 Cube。 |
| `unity_gameobject_delete` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功删除临时对象；破坏性操作仍需调用前确认目标。 |

### 组件、预制体、材质与着色器

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_component_add` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功向临时 Cube 添加 Rigidbody。 |
| `unity_component_remove` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功移除临时 Rigidbody；破坏性操作仍需确认目标。 |
| `unity_component_get` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功读取临时 Rigidbody 属性。 |
| `unity_component_modify` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功修改临时 Rigidbody `mass`。 |
| `unity_component_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功列出临时 Cube 组件。 |
| `unity_prefab_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功从临时 Cube 创建 Prefab。 |
| `unity_prefab_instantiate` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功实例化临时 Prefab 并返回完整 wire id。 |
| `unity_prefab_query_components` | 是 | 是 | 是 | 2026-08-02 契约测试补充：只读递归查询 Prefab 子层级组件；不进入 Prefab 编辑模式、不保存资源、不要求写权限。 |
| `unity_prefab_open` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功打开临时 Prefab 编辑模式。 |
| `unity_prefab_close` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功退出临时 Prefab 编辑模式。 |
| `unity_prefab_save` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功保存临时 Prefab 编辑模式修改。 |
| `unity_material_create` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在临时资源目录成功创建材质。 |
| `unity_material_modify` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功修改临时材质颜色属性。 |
| `unity_material_assign` | 是 | 是 | 是 | 2026-06-30 自动验收通过：修正 Unity 6 EntityId 截断后成功向临时 Cube Renderer 分配临时材质。 |
| `unity_material_get` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取临时材质属性信息。 |
| `unity_shader_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出可用 Shader。 |
| `unity_shader_inspect` | 是 | 是 | 是 | 2026-08-15 联机检查 URP Core `UnlitGizmo.shader`：imported/supported=true、propertyCount=1、错误警告 0。 |
| `unity_shader_check_errors` | 是 | 是 | 是 | 同一 Shader 联机返回结构化 `messageCount/errorCount/warningCount=0`，不修改或重导入资产。 |

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
| `unity_package_add` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功临时添加 `com.unity.nuget.newtonsoft-json`；调用时会通过 Unity Package Manager 解析 registry/本地缓存，并修改 `Packages/manifest.json` 依赖项。 |
| `unity_package_remove` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功移除临时添加的 `com.unity.nuget.newtonsoft-json` 并恢复 manifest；调用时会通过 Unity Package Manager 修改 `Packages/manifest.json` 依赖项。 |
| `unity_package_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出已安装包；调用时会查询 Unity Package Manager 当前项目包清单、registry 解析结果和本地缓存状态。 |
| `unity_package_search` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功查询 Unity Package Manager registry；调用时会按包名或关键字查询 registry 返回的包元数据。 |
| `unity_test_run` | 是 | 是 | 是 | 2026-08-15 不存在的精确过滤器联机返回清理后的 `status=no_tests,total=0,noTests=true/results=[]`，无伪失败。 |
| `unity_test_results` | 是 | 是 | 是 | 2026-08-09 Unity 2022 实际回调复验：`45 total / 43 passed / 2 skipped / 0 failed`；Unity 6 保留此前 `43/41/2/0` 证据，当前安装已卸载。 |
| `unity_test_list` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功列出测试用例；调用时会查询项目内 Unity Test Framework 测试用例。 |
| `unity_upilot_acceptance_run` | 是 | 是 | 是 | 2026-08-15 联机过滤验收通过：safe compile 错误 0、1/1 测试通过、cleanup 完成；summary SHA256 `96514909...4205693`。 |
| `unity_build_start` | 是 | 是 | 是 | 2026-06-30 自动验收通过：使用 `StandaloneWindows64` 和临时场景构建成功，错误/警告为 0；调用时会使用目标平台模块、工程构建配置和本机构建环境。 |
| `unity_build_status` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取最近一次构建状态 `succeeded`；调用时会查询最近一次构建任务状态。 |
| `unity_build_cancel` | 是 | 是 | 是 | 2026-06-30 自动验收通过：无活动构建时返回幂等 `not_running`；调用时会取消当前活动构建任务。 |
| `unity_build_targets` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功读取支持的构建目标；调用时会查询当前 Unity 安装可用的目标平台模块。 |

### 批处理、自动修复与端到端验收

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_batch_execute` | 是 | 是 | 是 | 2026-06-30 自动验收通过：顺序批量执行 `editor.delay` 与 `console.clear` 成功，completed=2、failed=0。 |
| `unity_batch_cancel` | 是 | 是 | 是 | 2026-06-30 自动验收通过：对已完成 batchId 返回幂等 `not_running`；运行中批次仍按 active batchId 取消。 |
| `unity_batch_results` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功按 batchId 查询已完成批处理结果。 |
| `unity_auto_fix_start` | 是 | 是 | 是 | 2026-06-30 自动验收通过：在无编译错误环境下启动空跑循环成功；调用时会启动自动修复循环，建议人工监督使用。 |
| `unity_auto_fix_stop` | 是 | 是 | 是 | 2026-06-30 自动验收通过：对运行中自动修复 loopId 调用停止接口成功；调用时会按 loopId 停止自动修复循环，建议人工监督使用。 |
| `unity_auto_fix_status` | 是 | 是 | 是 | 2026-06-30 自动验收通过：成功查询 idle/running/success 状态；调用时会查询自动修复 loop 状态，建议人工监督使用。 |
| `unity_editor_e2e_run` | 是 | 是 | 是 | 2026-06-30 自动验收通过：执行 `smoke_editor_state.yaml` 成功，stepCount=2，passed=true。 |

### 长作业编排与 Agent 规则

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_operation_list` | 是 | 是 | 是 | 列出 Unity Bridge 最近操作，保留原有诊断用途。 |
| `unity_operation_get` | 是 | 是 | 是 | 按 commandId 读取 Unity Bridge 操作步骤，保留原有诊断用途。 |
| `unity_operation_start` | 是 | 是 | 是 | 90 秒显式 EditMode 长作业真实启动通过并自动关联 Console Capture；status/cancel 参数支持 `${start.field}`、`${status.field}`、`${operation.operationId}` 注入。 |
| `unity_operation_status` | 是 | 是 | 是 | Python 覆盖大字段截断；2026-08-15 联机 summary 825 bytes、full 1520 bytes 且仅 full 返回 lastStatusData。 |
| `unity_operation_wait` | 是 | 是 | 是 | Python 覆盖等待语义与有界输出；轮询与终态响应支持 `detailLevel/maxTailChars/includeRawState`。 |
| `unity_asset_subresources_list` | 是 | 是 | 是 | `Hero_10003` FBX 联机列出非预览 AnimationClip 子资源。 |
| `unity_animator_controller_inspect` | 是 | 是 | 是 | `Hero_10003.controller` 联机返回 2 层、26 State、Motion、Mask、Transition 和未绑定 Clip。 |
| `unity_avatar_mask_inspect` | 是 | 是 | 是 | `avatarmask.mask` 联机展开 36 条 Transform 路径和 active 状态。 |
| `unity_model_importer_inspect` | 是 | 是 | 是 | `Attack_01` 联机返回 Generic/NoAvatar、Clip、Root 设置及 428 条曲线摘要。 |
| `unity_config_csv_get` | 是 | 是 | 是 | xclient 联机识别 GB18030、CRLF、第三行技术表头、31 列和唯一主键。 |
| `unity_config_csv_patch` | 是 | 是 | 条件可用 | `level_id=10044` 临时副本完成 dry-run/token/apply：GB18030、CRLF、31 列保持，目标外字节不变；业务源文件未写，apply 需要显式写授权。 |
| `unity_hang_status` | 是 | 是 | 是 | 15 秒主线程忙循环中检测到心跳超时、CPU busy、`mainThreadUnresponsive` 和 `suspectedBusyLoop`。 |
| `unity_hang_capture` | 是 | 是 | 是 | 忙循环期间成功生成非终止式 MiniDump，返回 bytes/SHA256/`processTerminated=false`，Unity 随后恢复。 |
| `unity_script_analyze` | 是 | 是 | 是 | `KingShotBattleTest.cs` 联机分析通过，解析依据明确标记 heuristic。 |
| `unity_script_dependency_graph` | 是 | 是 | 是 | 文件根、程序集、方向、依据和置信度联机通过；`maxDepth=1` 收敛为 77 节点/228 边。 |
| `unity_project_stack_detect` | 是 | 是 | 是 | xclient 联机识别 Unity 包、asmdef、Editor/Runtime 和测试程序集。 |
| `unity_operation_cancel` | 是 | 待 Unity 联机验收 | 是 | 调用 JobSpec cancelCall；无 cancelCall 时返回 CANCEL_UNSUPPORTED。 |
| `unity_operation_collect_artifacts` | 是 | Python 通过 | 是 | 报告 tail 受 `maxTailChars` 限制并返回 `tailTruncated/tailOriginalChars`；保留 metadata、sha256。 |
| `unity_agent_rules_check` | 是 | 是 | 是 | Python 单测覆盖只读检查；返回 recommendedBlock 和 diffSummary，不写文件。 |
| `unity_agent_rules_install` | 是 | 是 | 是 | Python 单测覆盖 dry-run 与 apply；仅替换 upilot:start/end 受控块，apply=true 需要写权限。 |
| `unity_compile_errors_get` | 是 | 是 | 是 | D/F 两个 Unity 2022 项目保留此前 live strict 错误 0 证据；当前本地候选在独立 Unity 2022 工程编译错误 0，F Bridge 现为断连。兼容别名为 `unity_compile_errors`。 |

### 界面流程自动化

| 工具名 | 开发完成 | 验收通过 | 可用状态 | 备注 |
| --- | --- | --- | --- | --- |
| `unity_upilot_flow_run_file` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_upilot_flow_run_suite` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_upilot_flow_run_batch` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_upilot_flow_force_reset` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_upilot_flow_run_async` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
| `unity_upilot_flow_results` | 是 | 专项验收中 | 是 | 需 Unity 6+ 且启用 UPILOT_ENABLE_FLOW；Unity 2022 返回 UIFLOW_UNAVAILABLE。 |
