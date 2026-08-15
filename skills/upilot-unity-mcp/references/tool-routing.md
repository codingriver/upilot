# Tool Routing

Use the narrowest tool that matches the request.

| Intent | Prefer |
| --- | --- |
| Connection/project identity | `unity_mcp_status` |
| Tool discovery | `unity_capabilities_get`, `unity_tools_find`; if the exact registered tool is not injected, `unity_tool_call` |
| Editor readiness | `unity_ensure_ready` |
| Compile/errors | `unity_safe_compile_and_wait`, `unity_compile_errors` |
| Console | `unity_console_tail_logs`, `unity_console_search_logs` |
| Persistent Console | `unity_console_capture_*`; use `afterSequence` / `nextSequence` for live tails, and `fromSequence` / `toSequence` plus `continuationToken` for filtered large captures |
| Configuration CSV | `unity_config_csv_get`, `unity_config_csv_patch` |
| Texture importer | `unity_texture_importer_get`, `unity_texture_importer_patch`, `unity_asset_reimport` |
| Hang diagnostics | `unity_hang_status`, `unity_hang_capture` |
| NavMesh diagnostics | `unity_navmesh_status`, `unity_navmesh_sample`, `unity_navmesh_triangulation_summary` |
| Runtime profiler | `unity_profiler_capture_start/status/stop` |
| Scenes/objects/components | `unity_scene_*`, `unity_gameobject_*`, `unity_component_*` |
| Assets/prefabs/materials | `unity_asset_*`, `unity_prefab_*`, `unity_material_*`; use `unity_asset_dependencies` for reference audits |
| Packages | `unity_package_*` |
| Tests/builds | `unity_test_*`, `unity_build_*` |
| Screenshots | `unity_screenshot_save` with explicit `fallbackSources`, `unity_screenshot_*`; use `unity_screenshot_pixel_stats/compare` for structured PNG acceptance |
| Long tasks | `unity_task_*`, `unity_operation_*` |
| Existing compiled API | `unity_reflection_call` |
| One bounded fallback expression | `reflection_eval` |

Only after `unity_reflection_call` actually fails may `reflection_eval` be used. Do not turn it into a multi-step C# script.

Use mouse, keyboard, and drag tools only after verifying window, focus, layout, and target.
