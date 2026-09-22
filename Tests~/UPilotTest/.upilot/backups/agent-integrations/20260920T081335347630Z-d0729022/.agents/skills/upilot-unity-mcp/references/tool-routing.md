# Tool Routing

Use the narrowest tool that matches the request.

| Intent | Prefer |
| --- | --- |
| Connection/project identity | `unity_mcp_status` |
| Tool discovery | `unity_capabilities_get`, `unity_tools_find`; if the exact registered tool is not injected, `unity_tool_call` |
| Editor readiness | `unity_ensure_ready`; decide from `ready`, `blocked`, `blockedReason`, `authoritative`, `isStale`, and `nextAction` |
| Compile/errors | `unity_write_batch_register`, `unity_safe_compile_and_wait`, `unity_compile_errors` |
| Console | `unity_console_tail_logs`, `unity_console_search_logs` |
| Persistent Console | `unity_console_capture_*`; use `afterSequence` / `nextSequence` for live tails, and `fromSequence` / `toSequence` plus `continuationToken` for filtered large captures |
| Optional UPilot Tracer | `unity_monohook_tracing_status`, `unity_monohook_tracing_configure`, `unity_monohook_tracing_events`; read `monohook-tracing.md` before configuration |
| Configuration CSV | `unity_config_csv_get`, `unity_config_csv_patch` |
| Texture importer | `unity_texture_importer_get`, `unity_texture_importer_patch`, `unity_asset_reimport` |
| Hang diagnostics | `unity_hang_status`, `unity_hang_capture` |
| NavMesh diagnostics | `unity_navmesh_status`, `unity_navmesh_sample`, `unity_navmesh_triangulation_summary` |
| Runtime profiler | `unity_profiler_capture_start/status/stop` |
| Scenes/objects/components | `unity_scene_*`, `unity_gameobject_*`, `unity_component_*` |
| Bounded scene context | `unity_scene_summary`; inspect coverage before treating counts as totals |
| Persisted prefab fields | `unity_prefab_patch`; dry-run, explicit approval, confirmToken, persisted verification |
| Assets/prefabs/materials | `unity_asset_*`, `unity_prefab_*`, `unity_material_*`; use `unity_asset_dependencies` for reference audits |
| Shader diagnostics | `unity_shader_inspect`, `unity_shader_check_errors`; use `unity_shader_list` only for discovery |
| Packages | `unity_package_*` |
| Tests/builds | `unity_upilot_acceptance_run` for canonical package acceptance; otherwise `unity_test_*` (retain `runGuid` across PlayMode reload), `unity_build_*` |
| Focused test union | `unity_test_list/run(testNames=[...], fixtures=[...])`; no mixing with testFilter |
| Long package acceptance | `unity_task_start` with `toolName="unity_upilot_acceptance_run"` and `retryCount=0`, then `unity_task_status` |
| Visual snapshots | `unity_camera_list`, `unity_snapshot_capture/status/collect_artifacts`; use `unity_snapshot_baseline_compare` for managed pixel/SSIM acceptance and `unity_snapshot_baseline_update` only through explicit two-phase approval |
| Legacy screenshots | `unity_screenshot_*` compatibility wrappers; use only for one Color target or a compatibility save path, never for silent fallback |
| Long tasks | `unity_operation_validate` before hand-authored specs, then `unity_task_*`, `unity_operation_*`; default `detailLevel=summary` with bounded `maxTailChars` |
| Existing compiled API | `unity_reflection_call(typeName=..., methodName=...)` |
| One bounded reflection expression | `unity_reflection_call(expression=...)` |
| Bounded C# expression/statements | `csharp_eval`; open `execution_session` when state or handles must persist |
| Temporary CLR type/interface adapter | `reflection_emit_type` with a required `execution_session` |

`unity_reflection_call` resolves method versus expression mode from mutually exclusive arguments before execution. Never pass both shapes and never retry one engine through the other after a failure. There is no separate public expression alias. Expression mode remains one bounded expression, not a multi-step C# script.

Read `execution-tools.md` before using typed arguments, handles, sessions, `csharp_eval`, or `reflection_emit_type`.

Use mouse, keyboard, and drag tools only after verifying window, focus, layout, and target.
