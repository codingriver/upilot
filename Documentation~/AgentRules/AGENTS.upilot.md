# UPilot Unity MCP Agent Rules Template

rulesVersion: 4
upilotPackageVersion: 0.3.11

This template is the generic UPilot rule source for Unity projects that install
`io.github.codingriver.upilot`. Project-specific business rules outside the
controlled UPilot block take precedence over these generic rules.

## Connection

- Streamable HTTP: `http://127.0.0.1:8011/mcp`
- Health check: `http://127.0.0.1:8011/health`
- Never configure an MCP client with the internal Unity Bridge WebSocket port.
1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches the intended project path (allow equivalent slash normalization).
4. Stop and report the mismatch if another Unity project is connected.

## Capabilities

- Distinguish server registration, client tool-list injection, and a successful real call; they are different states.
- If a native tool is not visible in the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable. Treat `registered`, `available`, and `callableNow` as separate states and follow `unavailableReason` / `nextAction`.
- After enabling an optional feature or changing tool registration, restart or refresh the MCP client tool list.
- Use the narrowest dedicated semantic tool. Use `unity_reflection_call` for existing compiled entry points.
- Only after `unity_reflection_call` actually fails may you fall back to one bounded `reflection_eval` expression.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Compile

- Call `unity_ensure_ready` before Editor mutations and inspect the exact target before destructive changes.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- If `unity_sync_after_disk_write(triggerCompile=true)` returns `ok=true/status=compiling`, do not retry sync immediately; call `unity_compile_wait` and then inspect compile errors.
- Prefer `unity_compile_wait` plus `unity_compile_errors`; `unity_compile_errors_get` is a compatibility alias.
- Compile only after C# or assembly-related changes. Do not compile again when no code changed.
- After compilation, read structured compile errors and relevant Console errors before editing again.

## Long Operations

- For tests, builds, smoke runs, and other long workflows, prefer `unity_operation_start`, `unity_operation_status`, `unity_operation_wait`, `unity_operation_cancel`, and `unity_operation_collect_artifacts`.
- Starting an operation is not success; poll until a terminal status.
- Report only meaningful changes: status, phase, error, `failureSignature`, suspected-stuck, or important artifacts.
- Use project-provided bridge entry points when they exist. Do not rebuild business workflows with shell commands, temporary scripts, menu calls, or UI automation.
- Keep business orchestration in project code. UPilot should start, poll, diagnose, capture logs, and collect artifacts.

## Operation Status Contract

- Project bridge status JSON should use generic fields where possible: `ok`, `operationId`, `status`, `phase`, `error`, `detail`, `elapsedSec`, `phaseElapsedSec`, `progress`, `failureSignature`, `artifacts`, `metrics`, and `domain`.
- UPilot parses only generic fields. Business fields belong in `domain` and are passed through unchanged.

## Persistent Console Capture

- For long-running or audit-sensitive operations, call `unity_console_capture_start` before the operation, use `unity_console_capture_status` and incremental `unity_console_capture_read`, and always call `unity_console_capture_stop` on success or failure.
- Keep raw Console capture separate from domain-specific reports. Prefer project-relative output paths and do not allow paths outside the project unless the user explicitly requests one.
- Console capture cleanup must use dry-run, target inspection, and confirm-token execution.

## Artifacts And Screenshots

- Prefer project-relative artifact paths returned by the project bridge.
- Prefer `unity_screenshot_save` for screenshots.
- Report screenshot `path`, `bytes`, `width`, `height`, `sha256`, and `source`. If screenshot capture falls back, also report `degraded`, `degradeReason`, and `originalError`.
- UPilot records artifact metadata and hashes; business code decides whether the artifact proves success.

## Assets And Prefabs

- Use `unity_prefab_query_components` for read-only Prefab child hierarchy/component checks before entering Prefab Mode or editing YAML. It returns GameObject paths, component types, and optional serialized fields without changing the current scene or requiring write access.
- Use write tools such as `unity_asset_modify_data`, `unity_component_modify`, or `unity_prefab_save` only after exact target inspection and write access approval.

## Acceptance

- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.
- For EditorWindow acceptance, prefer `unity_verify_window` and use `windowMatch` as the target-window truth. Treat legacy `windowDiagnostics` as UPilot window/layout diagnostics, not proof that a third-party window is absent.
- Retry automatically only when the registry marks the operation idempotent and non-destructive.
- If the same `failureSignature` repeats, stop blind reruns and fix project logic, test configuration, or acceptance criteria first.
- On timeout, inspect status, operation timing, Console capture, artifact summary, and last progress before choosing one bounded retry or a documented fallback.

## MCP Improvement Feedback

- If a task exposes missing MCP capability, inconsistent state, unstable polling, insufficient artifacts, or poor failure attribution, record a structured improvement item in the project-level `TODO_UPilot.mcd` when that file exists.
- Do not block the main task just to write feedback unless the missing MCP capability prevents safe completion; summarize any recorded UPilot improvement in the final handoff.
