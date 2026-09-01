# UPilot Unity MCP Agent Rules Template

rulesVersion: 22
upilotPackageVersion: 0.3.27

This template is the generic UPilot rule source for Unity projects that install
`io.github.codingriver.upilot`. Project-specific business rules outside the
controlled UPilot block take precedence over these generic rules.

## Parent Agent Rules

- Parent Agent rules path: nearest ancestor `AGENTS.md` relative to the project root `AGENTS.md`, for example `../../AGENTS.md` when installed under `Tests~/UPilotTest`.
- Before applying this UPilot block, automatically load the parent rules when a parent path exists.
- Resolve every loaded `AGENTS.md` to a canonical absolute path and keep a visited set, including this file, so repeated or circular references are skipped.
- Apply inherited parent rules first, then this project's local rules, then this UPilot block.

## UPilot Package Acceptance

- When validating UPilot repository/package changes, use `./Tests~/UPilotTest` under the UPilot repository as the default and canonical Unity project for compile and EditMode acceptance.
- Do not treat external client projects such as `D:\MA\xclient` or `F:\xclient2` as default UPilot validation targets.
- Use external client projects only when explicitly requested for project-side/business smoke validation or investigation.
- If this rule block is installed inside another Unity project, use that project for its own business workflows, but return to `./Tests~/UPilotTest` before claiming UPilot package compile/EditMode acceptance.

## Connection

- Streamable HTTP: `http://127.0.0.1:8011/mcp`
- Health check: `http://127.0.0.1:8011/health`
- Third-party AI tools must connect through Streamable HTTP at `http://127.0.0.1:<httpPort>/mcp` only.
- Never configure a third-party AI client with a WebSocket URL, the internal Unity Bridge port, a stdio command, or a local MCP Server process command.
- WebSocket transport is internal to MCP Server <-> Unity Bridge. The default internal port is 8765 and must not appear as the AI client's endpoint.
- When multiple Unity projects run concurrently, allocate a unique HTTP/WebSocket port pair per project internally, give each client registration a distinct name, and expose only that project's HTTP `/mcp` endpoint to the AI tool.
1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches the intended project path (allow equivalent slash normalization).
4. Stop and report the mismatch if another Unity project is connected.

## Capabilities

- Distinguish server registration, client tool-list injection, and a successful real call; they are different states.
- If a native tool is not visible in the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable. Treat `registered`, `available`, and `callableNow` as separate states and follow `unavailableReason` / `nextAction`.
- After enabling an optional feature or changing tool registration, restart or refresh the MCP client tool list.
- Use the narrowest dedicated semantic tool. `unity_reflection_call` is a generic, write-authorized, non-idempotent execution entry point because the target method may mutate project or runtime state; inspect the exact target and arguments and never retry it automatically. Use `unity_type_exists`, `unity_reflection_find`, or a dedicated semantic tool for safe read-only discovery.
- Only after `unity_reflection_call` actually fails may you fall back to one bounded `reflection_eval` expression.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Compile

- Call `unity_ensure_ready` before Editor mutations and inspect the exact target before destructive changes.
- Decide Editor readiness from `ready`, `blocked`, `blockedReason`, `authoritative`, `isStale`, and `nextAction`; follow `nextAction` while blocked or recovering.
- Do not infer readiness from raw `isPlaying` or `isCompiling` values alone. Compilation phases `queued`, `compiling`, `domain_reload`, and `verifying` are non-ready even when `isCompiling=false`.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- After C# or assembly-related changes, prefer one `unity_safe_compile_and_wait` call. It attaches to an existing compile and verifies persistent errors after Domain Reload.
- If `unity_sync_after_disk_write(triggerCompile=true)` returns `ok=true/status=compiling`, do not retry sync; call `unity_safe_compile_and_wait` and follow its `stage`, `nextAction`, and structured error result.
- Use `unity_compile_wait` plus `unity_compile_errors` only when observing a compile that must not be triggered or attached through the safe workflow; `unity_compile_errors_get` is a compatibility alias.
- Compile only after C# or assembly-related changes. Do not compile again when no code changed.
- After compilation, read structured compile errors and relevant Console errors before editing again.

## Optional UPilot Tracer

- In a UPilot context, interpret `Tracer`, `追踪器`, or `the tracer` as UPilot Tracer (`UPilot 追踪器`). Use MonoHook only for the internal implementation technology or preserved compatibility identifiers.
- UPilot Tracer is optional and manually controlled. All trace points, stack capture, and Console output default to disabled.
- Physical Hooks are technical replacements, not business interception by default. Each point uses `PassThrough` unless explicitly changed; built-in Providers must preserve original arguments, return semantics, exceptions, and call count. `Intercept` is allowed only for a Provider that declares support, and tracing-path failures must never block the original call.
- `自动注入追踪点位` is the master opt-in setting and defaults to disabled. Domain Reload and PlayMode timing switches are subordinate and cannot inject while it is off; manual `应用` is unaffected. Enable automatic injection only when explicitly requested.
- Do not enable, apply, auto-restore, or consume tracing events unless explicitly requested. Query status first and preserve the existing configuration.
- `unity_monohook_tracing_configure` saves without applying by default; use `apply=true` only when hook installation or application is explicitly required.
- Use target filters to narrow object source/type, GameObject name, hierarchy/parent/ancestor, scene/resource path, Layer/Tag, Active/enabled, Prefab, selection, point/method/phase, EditMode/PlayMode, and value changes. Conditions in one rule are AND; include rules are OR; exclude rules take priority.
- Target filtering uses a global default profile plus optional per-point overrides; an empty point override inherits the global profile. Stack capture uses `Disabled`, `SelectedPoints`, or `AllEnabledPoints` and defaults to disabled. Name/hierarchy filters suppress events before stack capture, buffering, and Console output, while type-only lifecycle filters may reduce physical installation candidates.
- Respect `Unsupported` diagnostics. Do not use Native, InternalCall, injected, reflection-eval, or other lower-level hook fallbacks.
- Keep high-frequency tracing, stack capture, and Console output bounded, then restore the original configuration after temporary diagnostics.

## Long Operations

- Before starting a hand-authored generic operation, call `unity_operation_validate(jobSpec)` and fix its structured field errors; validation must not start PlayMode or business work.
- For tests, builds, smoke runs, and other long workflows, prefer `unity_operation_start`, `unity_operation_status`, `unity_operation_wait`, `unity_operation_cancel`, and `unity_operation_collect_artifacts`.
- Starting an operation is not success; poll until a terminal status.
- Treat `waitWindowElapsed=true/terminal=false` as a running operation, not a timeout failure. Only the operation's `jobTimeoutAt` is its terminal timeout.
- Report only meaningful changes: status, phase, error, `failureSignature`, suspected-stuck, or important artifacts.
- Use `detailLevel=summary` for routine status/wait polling. Request `standard` or `full` only for bounded diagnosis, and set `maxTailChars` instead of returning unbounded domain/log/report text.
- Use project-provided bridge entry points when they exist. Do not rebuild business workflows with shell commands, temporary scripts, menu calls, or UI automation.
- Keep business orchestration in project code. UPilot should start, poll, diagnose, capture logs, and collect artifacts.

## Operation Status Contract

- Project bridge status JSON should use generic fields where possible: `ok`, `operationId`, `status`, `phase`, `error`, `detail`, `elapsedSec`, `phaseElapsedSec`, `progress`, `failureSignature`, `artifacts`, `metrics`, and `domain`.
- UPilot parses only generic fields. Business fields belong in `domain` and are passed through unchanged.

## Persistent Console Capture

- For long-running or audit-sensitive operations, call `unity_console_capture_start` before the operation, keep its `sessionId`, and always call `unity_console_capture_stop` on success or failure.
- Never repeatedly scan a complete large capture. Pass each `unity_console_capture_read` result's `nextSequence` as the next call's `afterSequence`.
- Before concluding cleanup, call `unity_console_capture_list`, inspect recovered or historical sessions still marked active, and stop the relevant session explicitly.
- Keep raw Console capture separate from domain-specific reports. Prefer project-relative output paths and do not allow paths outside the project unless the user explicitly requests one.
- Console capture cleanup must use dry-run, target inspection, and confirm-token execution.
- For canonical UPilot package acceptance, prefer `unity_upilot_acceptance_run`. It stops active captures before ConsoleCaptureService self-tests and does not start a persistent capture around that test run.

## Configuration CSV Safety

- Read targeted records with `unity_config_csv_get`; do not infer the file encoding, newline style, delimiter, header row, column count, or key uniqueness.
- Every `unity_config_csv_patch` write must follow `dryRun=true` -> inspect preview and hashes -> explicit write approval -> `dryRun=false` with the returned `confirmToken`.
- Supply `expectedValues` for fields whose current values are known. After apply, verify the target values, encoding, newline style, column count, and unchanged non-target bytes reported by the tool.

## Hang Diagnostics

- When Unity stops pumping commands or appears stuck, call `unity_hang_status` before retrying or restarting it.
- On Windows, collect `unity_hang_capture` before restart when diagnostic evidence is needed. Confirm the output path and report dump metadata; the capture must not terminate Unity.

## Artifacts And Screenshots

- Prefer project-relative artifact paths returned by the project bridge.
- Prefer `unity_screenshot_save` for screenshots.
- When fallback is allowed, pass an explicit ordered `fallbackSources` list. Report screenshot `path`, `bytes`, `width`, `height`, `sha256`, the actual `source`, `degraded`, `degradeReason`, and `originalError`.
- For EditorWindow capture, resolve the Unity window with `unity_editor_windows_list` and reuse its exact `typeName` or title. Validate returned match metadata against that Unity `instanceId`/type identity; never select an operating-system window by a matching title.
- Treat EditorWindow/SceneView screenshots as trustworthy pixel evidence only when `pixelSourceVerified=true` and `occlusionSensitive=false`. Always report `captureApi`, Unity PID, `windowHandle`, `foreground`, `degraded`, and `degradeReason`; reject or explicitly downgrade screen-pixel/camera fallbacks.
- UPilot records artifact metadata and hashes; business code decides whether the artifact proves success.

## Assets And Prefabs

- Use `unity_prefab_query_components` for read-only Prefab child hierarchy/component checks before entering Prefab Mode or editing YAML. It returns GameObject paths, component types, and optional serialized fields without changing the current scene or requiring write access.
- Use write tools such as `unity_asset_modify_data`, `unity_component_modify`, or `unity_prefab_save` only after exact target inspection and write access approval.

## Acceptance

- Preserve the `runGuid` returned by `unity_test_run`; after PlayMode Domain Reload or MCP reconnect, query `unity_test_results(runGuid=...)` instead of treating a new in-memory service as authoritative.
- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.
- For UPilot repository/package acceptance in `./Tests~/UPilotTest`, call `unity_upilot_acceptance_run` and preserve its `summary.json` path, bytes, and SHA256. Treat `status=no_tests` as a distinct terminal result, not a failed synthetic test and not a passing run when tests are required.
- For Shader failures, use `unity_shader_inspect` and `unity_shader_check_errors` before broad Console searches or manual reimports.
- For EditorWindow acceptance, prefer `unity_verify_window` and use `windowMatch` as the target-window truth. Treat legacy `windowDiagnostics` as UPilot window/layout diagnostics, not proof that a third-party window is absent.
- For SceneView pixel acceptance, require an exact `UnityEditor.SceneView` match plus `includesSceneGui=true`, `includesHandles=true`, `pixelSourceVerified=true`, and `occlusionSensitive=false`; report repaint sequence/timestamps.
- Retry automatically only when the registry marks the operation idempotent and non-destructive.
- If the same `failureSignature` repeats, stop blind reruns and fix project logic, test configuration, or acceptance criteria first.
- On timeout, inspect status, operation timing, Console capture, artifact summary, and last progress before choosing one bounded retry or a documented fallback.

## MCP Improvement Feedback

- If testing or development exposes missing MCP capability, inconsistent state, unstable polling, insufficient artifacts, poor failure attribution, repeated manual steps, or integration friction that could be simplified by improving UPilot features, MCP tools, agent rules, or project integration, record a structured improvement item in the UPilot repository-root `TODO_UPilot.mcd`.
- Each item should include the observed problem, affected workflow/tool, proposed UPilot or integration improvement, reproduction or evidence when available, and current status.
- Do not bury UPilot improvement ideas only in external client project TODO files; the UPilot repository-root `TODO_UPilot.mcd` is the source of truth for UPilot product/backlog follow-up.
- Do not block the main task just to write feedback unless the missing MCP capability prevents safe completion; summarize any recorded UPilot improvement in the final handoff.
