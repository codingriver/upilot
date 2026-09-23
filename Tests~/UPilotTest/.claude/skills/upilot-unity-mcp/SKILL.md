---
name: upilot-unity-mcp
description: Inspect, diagnose, automate, and modify Unity Editor projects through the UPilot MCP server. Use for Unity connection checks, compile and Console diagnostics, optional UPilot Tracer diagnostics, scenes, assets, tests, builds, registered Automation Step development and stepPlan composition, execution sessions, reflection calls, bounded C# evaluation, Reflection.Emit types, long-running Unity task monitoring, and UPilot Agent/Skill template maintenance and project synchronization.
---

<!-- Generated from SKILL.md.template. Do not edit SKILL.md directly. -->

# UPilot Unity MCP

Use UPilot with projects that install `io.github.codingriver.upilot`.

Terminology: in a UPilot context, `Tracer`, `追踪器`, and `the tracer` mean UPilot Tracer (`UPilot 追踪器`). MonoHook names the internal implementation technology or preserved compatibility identifiers, not a separate user-facing feature.

## Template-First Maintenance

- For UPilot Agent/Skill instruction, metadata, or distributed-resource changes, read `references/installation.md` and follow its complete maintenance procedure. Use this existing Skill; do not create a parallel test-project UPilot Skill or hand-edit generated instructions, installed copies, or installed templates.
- After each authorized editing batch, complete versioning, source generation/checks, all-five-target synchronization, and installed validation in the same task, without a separate user reminder. The completion condition is matching source and targets, not a saved template or a successful tool envelope.
- Report missing source or synchronization/validation failures as incomplete work; do not repair outputs by hand or claim partial success as complete. Template/Markdown-only changes do not require Unity compilation or a full test suite. This is task-driven synchronization, not a background file watcher.

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

Use Streamable HTTP at `http://127.0.0.1:8011/mcp` as the only third-party AI client transport. The matching health endpoint is `http://127.0.0.1:8011/health`. Never configure an AI client with a WebSocket URL, the internal Bridge port, stdio, or a command that launches the MCP Server. WebSocket transport is internal to MCP Server <-> Unity Bridge.

For concurrent Unity projects, use a distinct MCP registration name and a unique HTTP/WebSocket port pair per project, but expose only each project's HTTP `/mcp` endpoint to the AI client. Always verify project identity after connecting.

For acceptance after Server/Bridge/protocol changes, suspected deployment mismatch, or an authorized project whose endpoint is not injected into the client, read `references/workflows.md` (Deployment Freshness / Multiple Projects). Use existing clients and report missing deployment evidence; do not infer that unknown freshness authorizes a restart.

## Capability Rules

- Distinguish server registration, client tool-list injection, and a successful real call.
- If a native tool is absent from the client list, query `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable. When an exact tool is registered and callable but not injected, use `unity_tool_call` with its documented arguments.
- Refresh the MCP client after tool registration or optional-feature changes.
- Prefer the narrowest semantic tool.
- Use `unity_reflection_call` as the single public reflection execution entry point. Pass `typeName` + `methodName` for one structured compiled-method call, or pass only `expression` for one bounded C#-like reflection expression; `kind=auto` selects the engine from the request shape before execution and never retries through the other engine. The target may mutate project or runtime state, so inspect it and never retry automatically. Use `unity_type_exists`, `unity_reflection_find`, or a dedicated semantic tool for safe read-only discovery. No separate public reflection-expression alias is exposed.
- Use read-only `csharp_validate` to preflight generated code or an explicit backend without executing target code. Use `csharp_eval` for a bounded expression or multi-statement C# subset program. `interpret` is the complete V2 interpreter, `emit` is the compatible AST-entry cache, and `compiled` is the finite synchronous direct backend with no fallback. Use `reflection_emit_type` only when an actual temporary CLR type/interface implementation is required; its bodies may opt into `bodyBackend=compiled`. Open `execution_session` before any result must survive the call, and always close it. Execution tools are write-gated, non-idempotent, and never automatically retried; `csharp_validate` is read-only and idempotent. Read `references/execution-tools.md` for schemas, examples, limits, and recovery.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.
- Check `resourceDiagnostics` even on successful Eval/Emit/validation responses; on failure read `error.detail.resourceDiagnostics`. Eviction or post-success warmup warnings never require replay. Fixed caches hold 256 emit keys and 128 compiled keys; Domain type generation allows 256 attempts, including failures after reservation. Capacity refusals are errors, not silent eviction of CLR types. Session close/reconnect does not restore type quota; do not reload automatically. Inspect `resourceDiagnosticsDroppedCount` and read `execution.resources` through capabilities for live totals.

## Writes And Validation

- Inspect the exact target before persistent or destructive work.
- Treat Unity as the sole producer of Editor/compile facts and the MCP Server as the ordered latest-snapshot store. Expect fresh persisted snapshots immediately before a compile request (`compile_queued`), at compiler start (`compiling`), at compiler callback completion (`compiler_finished`, non-terminal until verification), immediately before Domain Reload (`domain_reload_starting`), immediately after reconnect (`domain_reload_recovered/verifying`), and after persisted error verification (`completed|failed`). Missing lifecycle evidence means unknown/recovering, not success.
- `isCompiling=true` alone does not establish a new compile phase or identity. If the lifecycle snapshot still describes an earlier terminal, wait for matching lifecycle evidence; do not reuse its successful result. A Unity heartbeat's empty pending field cannot clear a Server-owned unresolved write batch.
- Retain the current compile identity and terminal timestamps before C# or assembly-related disk writes; an old `completed` snapshot does not cover later edits.
- Immediately after saving one batch of C#, asmdef, asmref, or rsp writes, call `unity_write_batch_register(paths, compileWhenEditMode=true)` once and retain its server-derived batch identity and creation time.
- For deletions, include the absent paths in `deletedPaths`; represent a move as the existing destination in `paths` and the absent source in `deletedPaths`. Pure deletion uses `paths=[]`. Registration does not delete/move files; existing writes must still exist and all paths remain restricted to the project/local UPM roots. Use the returned merged change manifest/hash, not a last-call-only digest.
- When that batch is registered during PlayMode, pause, or a mode transition, do not call sync or compile tools and do not exit PlayMode without user authorization. The Server durably defers the authorized batch and automatically performs one sync plus one safe compile after a fresh authoritative EditMode snapshot arrives.
- Do not manually start a second sync or compile for an automatically authorized batch. Poll its execution state for a correlated terminal result. Use `unity_sync_after_disk_write` followed by one `unity_safe_compile_and_wait` only for legacy or explicitly manual flows.
- Accept compilation for the current batch only when the terminal state identifies that write batch/compile operation, reports `errorsVerified=true`, and has `lastCompileVerifiedAt >= writeBatchCreatedAt`. Treat `lastCompilerFinishedAt` as a compiler-boundary observation, not verified completion. Until those fields are exposed, use the result of the one safe-compile call started after EditMode rather than cached compile state.
- Treat envelope `ok` as protocol/tool success only; an observed compiler failure may be `ok=true/status=failed`. Decide the business result from phase, terminal verification, identities, timestamps, and structured errors.
- Never claim the latest code was compiled from a historical `completed` state, an unchanged completion timestamp, or the absence of immediate Console errors.
- **Never use external compilers** (`csc.exe`, `mcs`, `dotnet build`, Roslyn outside Unity, or any non-Unity build tool) to validate or simulate Unity C# compilation. Only Unity's own Roslyn-based script compilation pipeline (invoked through `unity_write_batch_register` + `unity_safe_compile_and_wait` or `unity_compile`) produces authoritative compile results. External compilers differ in Unity-specific assemblies, `UNITY_EDITOR`/`UNITY_ANDROID`/platform defines, conditional compilation symbols, asmdef reference resolution, `csc.rsp`/`mcs.rsp` files, and preprocessor behavior — no external tooling can reproduce this environment.
- Compile only after C# or assembly-related changes. Do not repeat compilation when no code changed.
- `unity_compile` already forces one incremental script-compilation request (`AssetDatabase.Refresh` + `RequestScriptCompilation`); it is not a Clean Build and cannot bypass a disconnected Bridge, PlayMode, an active compile, or stale Editor state. Prefer the correlated write-batch workflow after code changes.
- Starting a test, build, or async task is not success; poll to a terminal state.
- For PlayMode tests, keep the returned `runGuid` and query `unity_test_results(runGuid=...)`; UPilot persists the run across Domain Reload and MCP reconnects.
- Before starting a hand-authored generic operation, call `unity_operation_validate(jobSpec)` to check calls, placeholders, mappings, timeouts, and artifact rules without starting business work.
- A long-operation wait window ending is non-terminal when `waitWindowElapsed=true` and `terminal=false`; continue polling until the job completes or reaches `jobTimeoutAt`.
- For long tasks, report phase changes, errors, or suspected-stuck state rather than every poll.
- Use `detailLevel=summary` and a bounded `maxTailChars` for routine `unity_operation_status/wait`; use `standard` or `full` only for targeted diagnosis.
- Retry automatically only when the operation is idempotent and non-destructive.

## Project Workflows

- When a project exposes an authoritative compiled orchestration entry point for a test, build, or workflow, call it and poll its state. Do not reconstruct the workflow with shell commands, temporary scripts, menu calls, or UI automation.
- Keep business step implementations, assertions and restoration in project code. For UPilot Automation, read `references/automation-steps.md`: query the UPilot-owned directory, compose approved Skill templates with selected Cases into `jobSpec.stepPlan`, validate the complete list, then use existing Operation tools. Project Steps implement the string-only `IAutomationStep` contract or inherit `AutomationStepBase`; `arguments` is always last and results are JSON. Do not add parallel start/status tools, make the legacy project Bridge mandatory for new Step plans, or drive individual steps from client polling.

## Step Development

For adding, changing or removing a registered Step, read the **Authoring A Step**
section of `references/automation-steps.md` before editing code. It includes the
responsibility decision, lifecycle override table, guarded C# example, optional-package
behavior, registration troubleshooting and targeted acceptance checklist.
Load the active project's business rules/Skill as well; those define prerequisites,
arguments, ordering and assertions, not the UPilot framework.

Deliver the stable ID, source/type, argument example, completion/error contract,
resource restoration strategy, intended plan position and actual validation evidence.
Update the owning Skill template/selection when an approved Step becomes selectable;
never restore a removed project Runner, Registry or test menu to make it discoverable.
For instruction-only tasks, validate and synchronize instructions without creating
a sample production Step or launching a workflow.

## Persistent Console Capture

For plans with `upilot.console_capture_start`, use plan ownership instead of the
manual sequence below: start that Step first and once, set Operation
`consoleCapture.enabled=false`, and let the executor stop/verify Capture after
Finally. Use `upilot.capture_snapshot` or the base class's string/JSON evidence
helpers for run-owned screenshots; projects should not duplicate observers.
See `references/automation-steps.md`.

Use persistent capture when logs must survive long waits, Console clears, or Agent polling gaps:

1. Call `unity_console_capture_start` before the operation. Keep its exact `sessionId`, returned one-time `ownerToken`, and output directory; never write the token to normal logs or reports.
2. Run the task normally. Unity writes JSONL independently of MCP polling.
3. Call `unity_console_capture_status` for counters and write failures. For simple live tails, use the previous `nextSequence` as the next `afterSequence`. Filtered reads advance `nextSequence` to the last scanned record even when no records match; only a read that scans no record preserves the input cursor. For filtered or large captures, prefer `fromSequence/toSequence`, regex or keyword filters, and continue with the returned `continuationToken`; keep the first page's stable snapshot and report `totalMatchCount`, scan range/count, elapsed time, and index status.
4. Call `unity_console_capture_stop(sessionId, ownerToken)` only for the capture owned by this task when it ends, including failure paths. An unknown or another task's capture is not an automatic cleanup target; `forceStop=true` requires an exact session and explicit authorized human disposition.
5. Use `unity_console_capture_list(activeOnly=true)` when only the active-session summary is needed; the response reports `activeCount/returnedCount`. Do not combine it with `includeActive=false`, infer ownership from a list entry, or stop recovered/unknown sessions; package acceptance blocks on them instead of stopping them.
6. Use `unity_console_capture_attach` and `unity_console_capture_detach` for a fixed, read-only range of another capture. Detach never stops or adopts the source; paginate an export with the same attachment request key and continuation token.
7. Cleanup is two-phase: call `unity_console_capture_cleanup(dryRun=true)` first, inspect the returned directories, then pass its `confirmToken` with the same conditions and `dryRun=false` only when deletion is authorized.

Default captures belong under `Log/UPilotConsole/<timestamp>_<title>/`. Keep raw Console capture separate from domain-specific reports such as battle smoke-test reports. Prefer a project-relative custom path; do not set `allowOutsideProject=true` unless the user explicitly needs an external directory.

Exception: canonical UPilot package acceptance should use `unity_upilot_acceptance_run`. It blocks on an active Capture with another or unknown owner before ConsoleCaptureService self-tests and writes a structured hashed report; do not wrap it in another persistent capture.

## Configuration CSV

- Use `unity_config_csv_get` for targeted records and trust its detected encoding, newline, delimiter, header, column-count, and key-uniqueness metadata.
- Use `unity_config_csv_patch` only as `dryRun=true` -> inspect -> obtain explicit write approval -> apply with the returned `confirmToken`.
- Supply `expectedValues` when known and verify target values plus the reported non-target byte preservation after apply.

## Hang Diagnostics

For AI-requested Bridge/Server restarts, first read `references/safety.md` (AI Service Maintenance).
Use `unity_service_restart` only with the independent UI grant reported in
`aiServiceMaintenance`. It applies to all versions/install sources, but only in EditMode.
Never self-enable the grant or edit its timeout. The default total deadline is 120 seconds;
an interrupted response is not permission to resend or replay business.

- If Unity stops pumping commands, call `unity_hang_status` before retrying or restarting it.
- On Windows, use `unity_hang_capture` before restart when a dump is needed. It accepts `dumpType=mini|heap|full`, verifies the exact main Editor identity, estimates dump size, and preserves at least the effective `reserveBytes` (minimum 2 GiB). An insufficient-space preflight must report `dumpAttempted=false`; after capture, confirm the path, bytes, SHA256, `reserveMaintained=true`, and `processTerminated=false`.

## Runtime Diagnostics

- Use `unity_navmesh_status`, `unity_navmesh_sample`, and `unity_navmesh_triangulation_summary` for read-only navigation diagnosis. Treat `registrationMatrixSource=surfaceTransform-inferred` as inferred evidence, not an authoritative registered matrix.
- Use `unity_profiler_capture_start/status/stop` for repeatable long captures. Prefer a bounded marker whitelist or regex/cap, optionally provide a compiled static telemetry sampler and baseline JSON, poll to `Completed`/`Stopped`, report unavailable markers and the public-API Timeline limitation, and preserve the JSON/CSV artifacts.
- Use `unity_texture_importer_patch` only as `dryRun=true -> inspect -> confirmToken -> dryRun=false`; application requires project write access and reimports the asset.
- Use `unity_screenshot_pixel_stats` or `unity_screenshot_compare` for structured PNG acceptance under the current Unity project; they return statistics and hashes, not raw pixels.

## Acceptance Evidence

- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.
- For Profiler out-of-process (OOP) acceptance, do not automatically invoke `ShowProfilerOOP` without a verified, non-blocking, closable launch fixture with exact process identity, or guess command-line launch arguments. Identity observation is not a process start/stop cycle. Read `references/safety.md` before this workflow; abandoned acceptance stays unexecuted without renewed authorization.
- Prefer `unity_snapshot_capture`; the legacy `unity_screenshot_*` tools are compatibility wrappers over Snapshot schema v1. Snapshot v1 exposes `syncMode=sameFrame` and `completionPolicy=allOrNothing|bestEffort`.
- Use exact IDs from `unity_camera_list` for multi-Camera or depth capture. Depth v1 supports Built-in/URP Raw Depth and Linear Depth Float EXR with optional PNG preview; HDRP is unsupported.
- GameView Snapshot is PlayMode-only, Display 0, Color-only final composition. Camera and GameView are offscreen-capable while Unity is minimized; SceneView and EditorWindow fail fast when minimized.
- Resolve SceneView/EditorWindow targets with `unity_editor_windows_list` and pass the exact Unity `instanceId`; never select an operating-system window by title. SceneView evidence also requires exact `UnityEditor.SceneView`, a completed Repaint, `includesSceneGui=true`, and `includesHandles=true`.
- Trust a visual artifact only when `acceptedAsEvidence=true`, `pixelSourceVerified=true`, and `occlusionSensitive=false`; report its project-relative path, bytes, dimensions, SHA256, capture API, Unity PID/HWND, foreground/minimized state, degradation, and original error.
- Snapshot baselines live under `.upilot/snapshots/baselines`. Update them only through `unity_snapshot_baseline_update` dry-run -> inspect -> explicit approval -> apply with the returned current-state `confirmToken`; never approve automatically. Use `unity_snapshot_baseline_compare` for pixel-difference ratio, SSIM, and diagnostic diff/heatmap artifacts.
- Use `unity_shader_inspect` / `unity_shader_check_errors` for Shader-specific import, support, dependency, and compiler-message diagnostics.

## Queue Inspection And Cleanup

Use `unity_queue_cleanup()` to inspect the current project without advancing its tasks.
The Advanced Settings window uses the same read-only snapshot and manual refresh.
Missing, disconnected or stale data must not be described as an empty queue.

The independent **允许 AI 清理当前项目队列占用** switch defaults to enabled and is
unaffected by automation select-all. Never change this setting yourself. Disabled
permission still permits inspection and preview. When enabled, it is standing
authorization for exact-target cleanup across chat ownership, without a second human
confirmation; it does not grant arbitrary writes or change the original tools' grants.

1. Preview `unity_queue_cleanup(targetType, targetId, action, reason, dryRun=true)`.
2. Inspect the exact identity, action and project. Apply the identical request with
   `dryRun=false`, `confirmToken`, and `expectedProjectPath` from the preview.
3. Keep original task/run/operation/session IDs. Accepted cancellation is not completion;
   observe the existing status tools until cleanup is confirmed, or report unconfirmed.

Task/Test support `cancel` and existing `cleanup`; Operation supports `cancel`, including
associated Steps; Capture supports exact `stop` while retaining artifacts. Step force
recovery and generic Bridge-command revocation are unsupported. WriteBatch `release`
only disposes an inactive historical recovery blocker after verified backup. Its
original result stays unknown; disposition is separate and survives Server restart.
Never use another compile's success as evidence for that old batch.

Failed backup, possible execution, target changes, permission refusal and unsupported
adapters leave records intact. Preview tokens expire after 120 seconds and are one-shot.
Do not replay an uncertain apply or Start. No automatic Unity restart or blanket cleanup.
This exception permits ownerless Capture disposition only through `unity_queue_cleanup`;
it does not relax the direct Capture ownership rules.

Critical cancel/stop notices use `[UPilot][QueueCleanup]`. Failed/unconfirmed results
are Server Error and, when Unity is reachable, real `Debug.LogError`; a disconnected
Editor cannot immediately display a forwarded error. Never log tokens or full arguments.

## Focused Reliability

- Use `unity_test_list`, `unity_test_run` and `unity_upilot_acceptance_run` with exact `testNames`, fully qualified `fixtures`, `assemblies` and/or `categories`. `matchMode=union` preserves the default; `intersection` intersects nonempty field groups while values inside each group remain a union. List and execute use the same assembly-isolated selection. Inspect selector counts; do not combine these arrays with legacy `testFilter`. Empty arrays are invalid; zero matches do not start a full suite.
- Start long package acceptance through `unity_task_start(toolName="unity_upilot_acceptance_run", retryCount=0, toolArgs={...})`. Keep taskId and runGuid. Tests/package acceptance and generic `unity_operation_*` jobs use project-isolated SQLite records; other generic tasks are not durable. Generic operations persist start/cancel intent and observe established identities independently of client polling. After Server restart they resume queries, never replay start; lost start identity requires `RecoveryRequired`. Cancellation or timeout is not proof of business completion or cleanup.
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
- Adding/changing Steps, lifecycle examples, optional-package isolation, registered plans and evidence ownership: read `references/automation-steps.md`.
- Tool choice: read `references/tool-routing.md` and `references/tool-boundaries.md`.
- Client transport/config: read `references/client-configs.md`.
- Recovery and destructive work: read `references/safety.md`.
- UPilot Tracer: read `references/monohook-tracing.md`.
- Only when the user explicitly requests UPilot Flow or YAML EditorWindow automation: read `references/flow.md`.

## Advanced Automation Authorization

The Advanced Settings authorization catalog is persistent human authorization for finite, exact-target current-project actions. Full authorization selects every current catalog item but never covers unknown dialogs, cross-project/process actions, external publishing, or ambiguous business UI. It preserves `block` scene policy; `autoSave` creates `Assets/UPilotAutoSave_<number>.unity` for unnamed scenes and `ignore` deliberately discards changes. For ownerless Capture recovery, require the relevant enabled scopes plus exact `sessionId`, force-stop one session at a time, and verify manifest/summary/hash evidence without deleting it.
