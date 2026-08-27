# Tool Routing

Use the narrowest tool that matches the request.

| Intent | Prefer |
| --- | --- |
| Connection/project identity | `unity_mcp_status` |
| Tool discovery | `unity_capabilities_get`, `unity_tools_find`; if the exact registered tool is not injected, `unity_tool_call` |
| Editor readiness | `unity_ensure_ready`; decide from `ready`, `blocked`, `blockedReason`, `authoritative`, `isStale`, and `nextAction` |
| Compile/errors | `unity_safe_compile_and_wait`, `unity_compile_errors` |
| Console | `unity_console_tail_logs`, `unity_console_search_logs` |
| Persistent Console | `unity_console_capture_*`; use `afterSequence` / `nextSequence` for live tails, and `fromSequence` / `toSequence` plus `continuationToken` for filtered large captures |
| Optional MonoHook tracing | `unity_monohook_tracing_status`, `unity_monohook_tracing_configure`, `unity_monohook_tracing_events`; read `monohook-tracing.md` before configuration |
| Configuration CSV | `unity_config_csv_get`, `unity_config_csv_patch` |
| Texture importer | `unity_texture_importer_get`, `unity_texture_importer_patch`, `unity_asset_reimport` |
| Hang diagnostics | `unity_hang_status`, `unity_hang_capture` |
| NavMesh diagnostics | `unity_navmesh_status`, `unity_navmesh_sample`, `unity_navmesh_triangulation_summary` |
| Runtime profiler | `unity_profiler_capture_start/status/stop` |
| Scenes/objects/components | `unity_scene_*`, `unity_gameobject_*`, `unity_component_*` |
| Assets/prefabs/materials | `unity_asset_*`, `unity_prefab_*`, `unity_material_*`; use `unity_asset_dependencies` for reference audits |
| Shader diagnostics | `unity_shader_inspect`, `unity_shader_check_errors`; use `unity_shader_list` only for discovery |
| Packages | `unity_package_*` |
| Tests/builds | `unity_upilot_acceptance_run` for canonical package acceptance; otherwise `unity_test_*` (retain `runGuid` across PlayMode reload), `unity_build_*` |
| Screenshots | `unity_screenshot_save` with explicit `fallbackSources`, `unity_screenshot_*`; use `unity_screenshot_pixel_stats/compare` for structured PNG acceptance |
| Long tasks | `unity_operation_validate` before hand-authored specs, then `unity_task_*`, `unity_operation_*`; default `detailLevel=summary` with bounded `maxTailChars` |
| Existing compiled API | `unity_reflection_call` |
| One bounded fallback expression | `reflection_eval` |

Only after `unity_reflection_call` actually fails may `reflection_eval` be used. Do not turn it into a multi-step C# script.

Use mouse, keyboard, and drag tools only after verifying window, focus, layout, and target.
