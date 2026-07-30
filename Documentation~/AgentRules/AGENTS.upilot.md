# UPilot Unity MCP Agent Rules Template

rulesVersion: 2
upilotPackageVersion: 0.3.1

This template is the generic UPilot rule source for Unity projects that install
`io.github.codingriver.upilot`. Project-specific business rules outside the
controlled UPilot block take precedence over these generic rules.

## Connection

- Use Streamable HTTP MCP at `http://127.0.0.1:8011/mcp`.
- Use `GET http://127.0.0.1:8011/health` only as a health check.
- Never configure MCP clients with the internal Unity Bridge WebSocket port.
- Start every Unity task with `unity_mcp_status`.
- Require `connected: true`, `serverReady: true`, and a project path that matches the intended Unity project.
- Stop and report the mismatch if another Unity project is connected.

## Capability Discovery

- Treat server registration, client tool-list injection, and successful real calls as separate states.
- If a tool is missing from the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable.
- Use the narrowest semantic tool available.
- Use `unity_reflection_call` for stable compiled project entry points.
- Use one bounded `reflection_eval` expression only after a real `unity_reflection_call` failure.
- Do not repeatedly fetch the full tool list.

## Writes And Compile

- Call `unity_ensure_ready` before Editor mutations.
- Inspect the exact target before destructive or persistent changes.
- After a batch of disk writes, call `unity_sync_after_disk_write` once.
- Compile only after C# or assembly-related changes.
- Prefer `unity_compile_wait` plus `unity_compile_errors`; `unity_compile_errors_get` is a compatibility alias.
- Do not compile again when no code changed.
- After compile, read structured compile errors and relevant Console errors before editing again.

## Long Operations

- For tests, builds, smoke runs, and other long workflows, prefer:
  - `unity_operation_start`
  - `unity_operation_status`
  - `unity_operation_wait`
  - `unity_operation_cancel`
  - `unity_operation_collect_artifacts`
- Starting an operation is not success; poll until a terminal status.
- Report only meaningful changes: status, phase, error, `failureSignature`, suspected-stuck, or important artifacts.
- Use project-provided bridge entry points when they exist. Do not rebuild business workflows with shell commands, temporary scripts, menu calls, or UI automation.
- UPilot must only start, poll, diagnose, capture logs, and collect artifacts.

## Operation Status Contract

Project bridge status JSON should use these generic fields where possible:

- `ok`
- `operationId`
- `status`
- `phase`
- `error`
- `detail`
- `elapsedSec`
- `phaseElapsedSec`
- `progress`
- `failureSignature`
- `artifacts`
- `metrics`
- `domain`

UPilot parses only generic fields. Business fields belong in `domain` and are passed through unchanged.

## Console Capture

- For long-running or audit-sensitive operations, enable operation Console capture or call `unity_console_capture_start` before the operation.
- Use incremental reads with `nextSequence`.
- Always call `unity_console_capture_stop` on success, failure, timeout, or cancel.
- Keep raw Console capture separate from domain-specific reports.
- Cleanup is two-phase: dry-run first, then execute with the returned confirm token only when deletion is authorized.

## Artifacts And Screenshots

- Prefer project-relative artifact paths returned by the project bridge.
- Prefer `unity_screenshot_save` for screenshots.
- Report screenshot `path`, `bytes`, `width`, `height`, `sha256`, and `source`.
- UPilot records artifact metadata and hashes; business code decides whether the artifact proves success.

## Retry And Failure Protection

- Retry automatically only when the operation is idempotent and non-destructive.
- If the same `failureSignature` repeats, stop blind reruns and fix project logic, test configuration, or acceptance criteria first.
- On timeout, inspect phase elapsed time, operation timing, Console capture, and artifact summary before choosing one bounded retry.
