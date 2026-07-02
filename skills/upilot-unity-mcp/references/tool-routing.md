# Tool Routing

Use the narrowest tool that matches the user's intent.

## Status And Readiness

| Intent | Prefer |
| --- | --- |
| Verify MCP and Unity connection | `unity_mcp_status` |
| Confirm editor is usable | `unity_ensure_ready` |
| Inspect editor state | `unity_editor_state` |
| Wait after a short editor operation | `unity_editor_delay` |

## Compile And Logs

| Intent | Prefer |
| --- | --- |
| Trigger compile and wait safely | `unity_safe_compile_and_wait` |
| Read compile state | `unity_compile_status` |
| Read structured compiler errors | `unity_compile_errors` |
| Read recent Console logs | `unity_console_tail_logs` |
| Search all Console logs | `unity_console_search_logs` |
| Refresh assets after disk writes | `unity_sync_after_disk_write` |

## Code Invocation

Prefer this order:

1. `unity_reflection_call` for stable compiled methods.
2. `reflection_eval` for one expression, property access, simple assignment, or diagnostic call.
3. `unity_roslyn_execute` for temporary dynamic C# that cannot fit the first two options.

Avoid `unity_roslyn_execute` for routine game logic calls that already exist in loaded assemblies.

## Editor Objects And Assets

| Intent | Prefer |
| --- | --- |
| Scene create/open/save/load/list | `unity_scene_*` |
| Selection read or set | `unity_selection_*` |
| GameObject create/find/modify/move/duplicate/delete | `unity_gameobject_*` |
| Component add/get/modify/list/remove | `unity_component_*` |
| Asset find/copy/move/delete/refresh | `unity_asset_*` |
| Prefab create/open/save/instantiate | `unity_prefab_*` |
| Material create/get/modify/assign | `unity_material_*` |
| Package add/remove/list/search | `unity_package_*` |

## Tests, Builds, And Batches

| Intent | Prefer |
| --- | --- |
| List tests | `unity_test_list` |
| Run tests | `unity_test_run` |
| Read test results | `unity_test_results` |
| List build targets | `unity_build_targets` |
| Start build | `unity_build_start` |
| Read or cancel build | `unity_build_status`, `unity_build_cancel` |
| Run guarded tool calls | `unity_task_execute` |
| Batch repeated calls | `unity_batch_execute` |

## Windows, Input, And Screenshots

| Intent | Prefer |
| --- | --- |
| List or close EditorWindow | `unity_editor_windows_list`, `unity_editor_window_close` |
| Execute menu items | `unity_menu_execute`, `unity_editor_execute_command` |
| Keyboard or mouse input | `unity_keyboard_event`, `unity_mouse_event` |
| Drag and drop | `unity_drag_drop` |
| Scene view navigation | `unity_sceneview_navigate` |
| Screenshots | `unity_screenshot_*` |

## UIFlow

Use `unity_uiflow_*` only for YAML-driven Unity EditorWindow automation. If UIFlow is unavailable, use core window, screenshot, input, and diagnostic tools.
