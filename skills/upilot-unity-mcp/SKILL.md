---
name: upilot-unity-mcp
description: Inspect, diagnose, automate, and modify Unity Editor projects through the UPilot MCP server. Use for Unity connection checks, compile and Console diagnostics, optional UPilot Tracer diagnostics, scenes, assets, tests, builds, execution sessions, reflection calls, bounded C# evaluation, Reflection.Emit types, and long-running Unity task monitoring.
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
- Use `unity_reflection_call` as the single public reflection execution entry point. Pass `typeName` + `methodName` for one structured compiled-method call, or pass only `expression` for one bounded C#-like reflection expression; `kind=auto` selects the engine from the request shape before execution and never retries through the other engine. The target may mutate project or runtime state, so inspect it and never retry automatically. Use `unity_type_exists`, `unity_reflection_find`, or a dedicated semantic tool for safe read-only discovery. No separate public reflection-expression alias is exposed.
- Use `csharp_eval` for a bounded expression or multi-statement C# subset program. Use `reflection_emit_type` only when an actual temporary CLR type/interface implementation is required. Open `execution_session` before any result must survive the call, and always close it. These tools are write-gated, non-idempotent, and never automatically retried. Read `references/execution-tools.md` for schemas, examples, limits, and recovery.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Validation

- Inspect the exact target before persistent or destructive work.
- Treat Unity as the sole producer of Editor/compile facts and the MCP Server as the ordered latest-snapshot store. Expect fresh persisted snapshots immediately before a compile request (`compile_queued`), at compiler start (`compiling`), at compiler callback completion (`compiler_finished`, non-terminal until verification), immediately before Domain Reload (`domain_reload_starting`), immediately after reconnect (`domain_reload_recovered/verifying`), and after persisted error verification (`completed|failed`). Missing lifecycle evidence means unknown/recovering, not success.
- Retain the current compile identity and terminal timestamps before C# or assembly-related disk writes; an old `completed` snapshot does not cover later edits.
- Immediately after saving one batch of C#, asmdef, asmref, or rsp writes, call `unity_write_batch_register(paths, compileWhenEditMode=true)` once and retain its server-derived batch identity and creation time.
- For deletions, include the absent paths in `deletedPaths`; represent a move as the existing destination in `paths` and the absent source in `deletedPaths`. Pure deletion uses `paths=[]`. Registration does not delete/move files; existing writes must still exist and all paths remain restricted to the project/local UPM roots. Use the returned merged change manifest/hash, not a last-call-only digest.
- When that batch is registered during PlayMode, pause, or a mode transition, do not call sync or compile tools and do not exit PlayMode without user authorization. The Server durably defers the authorized batch and automatically performs one sync plus one safe compile after a fresh authoritative EditMode snapshot arrives.
- Do not manually start a second sync or compile for an automatically authorized batch. Poll its execution state for a correlated terminal result. Use `unity_sync_after_disk_write` followed by one `unity_safe_compile_and_wait` only for legacy or explicitly manual flows.
- Accept compilation for the current batch only when the terminal state identifies that write batch/compile operation, reports `errorsVerified=true`, and has `lastCompileVerifiedAt >= writeBatchCreatedAt`. Treat `lastCompilerFinishedAt` as a compiler-boundary observation, not verified completion. Until those fields are exposed, use the result of the one safe-compile call started after EditMode rather than cached compile state.
- Treat envelope `ok` as protocol/tool success only; an observed compiler failure may be `ok=true/status=failed`. Decide the business result from phase, terminal verification, identities, timestamps, and structured errors.
- Never claim the latest code was compiled from a historical `completed` state, an unchanged completion timestamp, or the absence of immediate Console errors.
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
- Prefer `unity_snapshot_capture`; the legacy `unity_screenshot_*` tools are compatibility wrappers over Snapshot schema v1.
- Use exact IDs from `unity_camera_list` for multi-Camera or depth capture. Depth v1 supports Built-in/URP Raw Depth and Linear Depth Float EXR with optional PNG preview; HDRP is unsupported.
- GameView Snapshot is PlayMode-only, Display 0, Color-only final composition. Camera and GameView are offscreen-capable while Unity is minimized; SceneView and EditorWindow fail fast when minimized.
- Resolve SceneView/EditorWindow targets with `unity_editor_windows_list` and pass the exact Unity `instanceId`; never select an operating-system window by title. SceneView evidence also requires exact `UnityEditor.SceneView`, a completed Repaint, `includesSceneGui=true`, and `includesHandles=true`.
- Trust a visual artifact only when `acceptedAsEvidence=true`, `pixelSourceVerified=true`, and `occlusionSensitive=false`; report its project-relative path, bytes, dimensions, SHA256, capture API, Unity PID/HWND, foreground/minimized state, degradation, and original error.
- Snapshot baselines live under `.upilot/snapshots/baselines`. Update them only through `unity_snapshot_baseline_update` dry-run -> inspect -> explicit approval -> apply with the returned current-state `confirmToken`; never approve automatically. Use `unity_snapshot_baseline_compare` for pixel-difference ratio, SSIM, and diagnostic diff/heatmap artifacts.
- Use `unity_shader_inspect` / `unity_shader_check_errors` for Shader-specific import, support, dependency, and compiler-message diagnostics.

## Focused Reliability

- Use `unity_test_list` and `unity_test_run` with exact `testNames` and/or fully qualified `fixtures` arrays for a union in one runGuid. Inspect per-selector match counts. Do not combine these arrays with legacy `testFilter`. Empty arrays are invalid; zero matches do not start a full suite.
- Start long package acceptance through `unity_task_start(toolName="unity_upilot_acceptance_run", retryCount=0, toolArgs={...})`. Keep taskId and runGuid. Only tests/package acceptance use the project-isolated SQLite job records; other tasks and generic operations are not durable.
- `unity_task_cancel` requests underlying test cancellation. It is not terminal until authoritative cleanup succeeds. Unsupported generic-task cancellation leaves both work and observation running.
- A recovered test task observes its established runGuid and never replays start. `RecoveryRequired` means the outcome or cleanup is unproven, not success or cancellation. Inspect original evidence before any new run.
- Acceptance requires a matching authoritative run, successful cleanup, verified compile evidence and unchanged checked source. Already verified compilation covering the current C# input timestamps is reused without a second compile.
- `unity_prefab_patch` supports one ordinary non-nested prefab, one unambiguous child/component and supported existing value fields. Use `dryRun=true`, inspect old/new values and hashes, obtain explicit approval, then apply with the returned confirmToken and identical request.
- Prefab patch v1 rejects an open target Prefab Mode, model/variant/nested prefabs, component/array structure changes, object-reference changes and numeric enums. It creates a temporary candidate only on apply, verifies the reload and preserves a backup; recovery is conditional on current asset/meta hashes. It is not a transaction over user callbacks.
- Use `unity_scene_summary` before fetching a full hierarchy. Honor node/time/example budgets and `coverageComplete`/`truncationReason`; partial counts describe visited nodes only. Native per-node Unity API calls are not preemptible.

## Reference Routing

For execution-tool selection and typed values, read `references/execution-tools.md` on demand.

- Installation: read `references/installation.md`.
- Common flows: read `references/workflows.md`.
- Tool choice: read `references/tool-routing.md` and `references/tool-boundaries.md`.
- Client transport/config: read `references/client-configs.md`.
- Recovery and destructive work: read `references/safety.md`.
- UPilot Tracer: read `references/monohook-tracing.md`.
- Only when the user explicitly requests UPilot Flow or YAML EditorWindow automation: read `references/flow.md`.
