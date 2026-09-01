---
name: upilot-unity-mcp
description: Inspect, diagnose, automate, and modify Unity Editor projects through the UPilot MCP server. Use for Unity connection checks, compile and Console diagnostics, optional UPilot Tracer diagnostics, scenes, GameObjects, components, assets, prefabs, packages, tests, builds, screenshots, Editor windows, existing compiled reflection entry points, and long-running Unity task monitoring.
---

# UPilot Unity MCP

Use UPilot with projects that install `io.github.codingriver.upilot`.

Terminology: in a UPilot context, `Tracer`, `追踪器`, and `the tracer` mean UPilot Tracer (`UPilot 追踪器`). MonoHook names the internal implementation technology or preserved compatibility identifiers, not a separate user-facing feature.

## Start

1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches the intended project.
4. Call `unity_capabilities_get` when tool availability is uncertain.
5. Call `unity_ensure_ready` before Editor mutations.

Use the unified execution state: require `ready=true`, `authoritative=true`, and `isStale=false`. When `blocked=true` or readiness is false, follow `blockedReason` and `nextAction`; do not infer readiness from raw `isPlaying` or `isCompiling` values alone.

## Optional UPilot Tracer

- In a UPilot context, `Tracer`, `追踪器`, and `the tracer` mean UPilot Tracer (`UPilot 追踪器`); MonoHook is only the internal implementation technology.
- The Tracer is manually controlled. Trace points, stack capture, and Console output default to disabled; do not enable, apply, or consume events unless explicitly requested.
- A Hook is a technical method replacement, but the default per-point execution mode is `PassThrough`: built-in points must call the original method with unchanged arguments, return semantics, exceptions, and call count. Per-point `Intercept` is opt-in and only valid when the Provider declares interception support; tracing failures are isolated and must not block the original call.
- `自动注入追踪点位` is the master opt-in switch and defaults to disabled. Its Domain Reload and PlayMode timing switches take effect only while the master switch is enabled; manual `应用` remains available when automatic injection is off. Do not enable any automatic injection setting implicitly.
- Use target filters to narrow object source/type, GameObject name, hierarchy/parent/ancestor/root/direct-child, scene/resource path, Layer/Tag, Active/enabled, required-component state, Prefab/source path, selection, point/method/phase/event source, EditMode/PlayMode, object identity, and value changes. Conditions in one rule are AND; include rules are OR; exclude rules take priority.
- Optional global/per-object rate limits and duplicate suppression are disabled by default; when enabled, report their dropped counters separately from filter rejections.
- Target filtering uses one global default profile plus optional explicit per-point overrides; an empty point override inherits the global profile. Stack capture uses `Disabled`, `SelectedPoints`, or `AllEnabledPoints`, and defaults to `Disabled`. Name/hierarchy filters suppress events before stack capture, buffering, and Console output, while type-only lifecycle filters may reduce physical installation candidates.
- Keep high-frequency points, stack capture, and Console output bounded; use filter statistics and rejection reasons before widening the scope.

Use Streamable HTTP such as `http://127.0.0.1:8011/mcp` as the only third-party AI client transport. Never configure an AI client with a WebSocket URL, the internal Bridge port, stdio, or a command that launches the MCP Server. WebSocket transport is internal to MCP Server <-> Unity Bridge.

For concurrent Unity projects, use a distinct MCP registration name and a unique HTTP/WebSocket port pair per project, but expose only each project's HTTP `/mcp` endpoint to the AI client. Always verify project identity after connecting.

## Capability Rules

- Distinguish server registration, client tool-list injection, and a successful real call.
- If a native tool is absent from the client list, query `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable. When an exact tool is registered and callable but not injected, use `unity_tool_call` with its documented arguments.
- Refresh the MCP client after tool registration or optional-feature changes.
- Prefer the narrowest semantic tool.
- Call existing compiled methods with `unity_reflection_call`. It is a generic, write-authorized, non-idempotent execution entry point because the target method may mutate project or runtime state; inspect the target and never retry it automatically. Use `unity_type_exists`, `unity_reflection_find`, or a dedicated semantic tool for safe read-only discovery. Fall back to one bounded `reflection_eval` expression only after an actual reflection-call failure.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Validation

- Inspect the exact target before persistent or destructive work.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- After C# or assembly-related changes, prefer one `unity_safe_compile_and_wait` call. It attaches to an existing compile and verifies errors after Domain Reload.
- Compile only after C# or assembly-related changes. Do not repeat compilation when no code changed.
- Starting a test, build, or async task is not success; poll to a terminal state.
- For PlayMode tests, keep the returned `runGuid` and query `unity_test_results(runGuid=...)`; UPilot persists the run across Domain Reload and MCP reconnects.
- Before starting a hand-authored generic operation, call `unity_operation_validate(jobSpec)` to check calls, placeholders, mappings, timeouts, and artifact rules without starting business work.
- A long-operation wait window ending is non-terminal when `waitWindowElapsed=true` and `terminal=false`; continue polling until the job completes or reaches `jobTimeoutAt`.
- For long tasks, report phase changes, errors, or suspected-stuck state rather than every poll.
- Use `detailLevel=summary` and a bounded `maxTailChars` for routine `unity_operation_status/wait`; use `standard` or `full` only for targeted diagnosis.
- Retry automatically only when the operation is idempotent and non-destructive.

## Project Workflows

- When a project exposes an authoritative compiled orchestration entry point for a test, build, or workflow, call it and poll its state. Do not reconstruct the workflow with shell commands, temporary scripts, menu calls, or UI automation.
- Keep business orchestration in project code. MCP should start, poll, diagnose, capture logs, and collect artifacts.

## Persistent Console Capture

Use persistent capture when logs must survive long waits, Console clears, or Agent polling gaps:

1. Call `unity_console_capture_start` before the operation. Keep its `sessionId` and output directory.
2. Run the task normally. Unity writes JSONL independently of MCP polling.
3. Call `unity_console_capture_status` for counters and write failures. For simple live tails, use the previous `nextSequence` as the next `afterSequence`. For filtered or large captures, prefer `fromSequence/toSequence`, regex or keyword filters, and continue with the returned `continuationToken`; keep the first page's stable snapshot and report `totalMatchCount`, scan range/count, elapsed time, and index status.
4. Always call `unity_console_capture_stop` when the task ends, including failure paths. Report the JSONL path, summary path, counts, dropped logs, and SHA256.
5. Use `unity_console_capture_list` to find recent default-directory sessions. Before concluding cleanup, inspect and stop relevant recovered or historical sessions still marked active.
6. Cleanup is two-phase: call `unity_console_capture_cleanup(dryRun=true)` first, inspect the returned directories, then pass its `confirmToken` with the same conditions and `dryRun=false` only when deletion is authorized.

Default captures belong under `Log/UPilotConsole/<timestamp>_<title>/`. Keep raw Console capture separate from domain-specific reports such as battle smoke-test reports. Prefer a project-relative custom path; do not set `allowOutsideProject=true` unless the user explicitly needs an external directory.

Exception: canonical UPilot package acceptance should use `unity_upilot_acceptance_run`. It detects and stops active captures before running ConsoleCaptureService self-tests and writes a structured hashed report; do not wrap it in another persistent capture.

## Configuration CSV

- Use `unity_config_csv_get` for targeted records and trust its detected encoding, newline, delimiter, header, column-count, and key-uniqueness metadata.
- Use `unity_config_csv_patch` only as `dryRun=true` -> inspect -> obtain explicit write approval -> apply with the returned `confirmToken`.
- Supply `expectedValues` when known and verify target values plus the reported non-target byte preservation after apply.

## Hang Diagnostics

- If Unity stops pumping commands, call `unity_hang_status` before retrying or restarting it.
- On Windows, use `unity_hang_capture` before restart when a dump is needed. Confirm the path and verify `processTerminated=false` in the result.

## Runtime Diagnostics

- Use `unity_navmesh_status`, `unity_navmesh_sample`, and `unity_navmesh_triangulation_summary` for read-only navigation diagnosis. Treat `registrationMatrixSource=surfaceTransform-inferred` as inferred evidence, not an authoritative registered matrix.
- Use `unity_profiler_capture_start/status/stop` for repeatable long captures. Prefer a bounded marker whitelist or regex/cap, optionally provide a compiled static telemetry sampler and baseline JSON, poll to `Completed`/`Stopped`, report unavailable markers and the public-API Timeline limitation, and preserve the JSON/CSV artifacts.
- Use `unity_texture_importer_patch` only as `dryRun=true -> inspect -> confirmToken -> dryRun=false`; application requires project write access and reimports the asset.
- Use `unity_screenshot_pixel_stats` or `unity_screenshot_compare` for structured PNG acceptance under the current Unity project; they return statistics and hashes, not raw pixels.

## Acceptance Evidence

- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.
- Prefer dedicated project-relative artifact or screenshot save tools that return metadata or hashes.
- For screenshot fallback, pass ordered `fallbackSources` and report the actual `source`, `degraded`, and `degradeReason`.
- Resolve EditorWindow targets with `unity_editor_windows_list`, reuse the exact Unity type/title identity, and never select an operating-system window by a matching title.
- Trust EditorWindow/SceneView pixels only when `pixelSourceVerified=true` and `occlusionSensitive=false`; for SceneView evidence also require `matchedFullTypeName=UnityEditor.SceneView`, `includesSceneGui=true`, and `includesHandles=true`, then report repaint sequence/timestamps, `captureApi`, Unity PID, HWND, foreground state, and any degradation.
- If capture falls back to base64 or OS-level automation, report the reason.
- Use `unity_shader_inspect` / `unity_shader_check_errors` for Shader-specific import, support, dependency, and compiler-message diagnostics.

## Routing

- Installation: read `references/installation.md`.
- Common flows: read `references/workflows.md`.
- Tool choice: read `references/tool-routing.md` and `references/tool-boundaries.md`.
- Client transport/config: read `references/client-configs.md`.
- Recovery and destructive work: read `references/safety.md`.
- UPilot Tracer: read `references/monohook-tracing.md`.
- Only when the user explicitly requests UPilot Flow or YAML EditorWindow automation: read `references/flow.md`.
