# Tool Routing

Use the narrowest tool that matches the request.

| Intent | Prefer |
| --- | --- |
| Connection/project identity | `unity_mcp_status` |
| Tool discovery | `unity_capabilities_get`, `unity_tools_find` |
| Editor readiness | `unity_ensure_ready` |
| Compile/errors | `unity_safe_compile_and_wait`, `unity_compile_errors` |
| Console | `unity_console_tail_logs`, `unity_console_search_logs` |
| Scenes/objects/components | `unity_scene_*`, `unity_gameobject_*`, `unity_component_*` |
| Assets/prefabs/materials | `unity_asset_*`, `unity_prefab_*`, `unity_material_*` |
| Packages | `unity_package_*` |
| Tests/builds | `unity_test_*`, `unity_build_*` |
| Screenshots | `unity_screenshot_*` |
| Long tasks | `unity_task_*`, `unity_operation_*` |
| Existing compiled API | `unity_reflection_call` |
| One bounded fallback expression | `reflection_eval` |

Only after `unity_reflection_call` actually fails may `reflection_eval` be used. Do not turn it into a multi-step C# script.

Use mouse, keyboard, and drag tools only after verifying window, focus, layout, and target.
