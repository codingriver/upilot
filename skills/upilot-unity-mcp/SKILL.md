---
name: upilot-unity-mcp
description: Inspect, diagnose, automate, and modify Unity Editor projects through the UPilot MCP server. Use for Unity connection checks, compile and Console diagnostics, scenes, GameObjects, components, assets, prefabs, packages, tests, builds, screenshots, Editor windows, existing compiled reflection entry points, and long-running Unity task monitoring.
---

# UPilot Unity MCP

Use UPilot with projects that install `io.github.codingriver.upilot`.

## Start

1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches the intended project.
4. Call `unity_capabilities_get` when tool availability is uncertain.
5. Call `unity_ensure_ready` before Editor mutations.

Use `http://127.0.0.1:8011/mcp` for MCP clients. Treat every WebSocket port as internal Bridge transport.

## Capability Rules

- Distinguish server registration, client tool-list injection, and a successful real call.
- If a native tool is absent from the client list, query `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable.
- Refresh the MCP client after tool registration or optional-feature changes.
- Prefer the narrowest semantic tool.
- Call existing compiled methods with `unity_reflection_call`. Fall back to one bounded `reflection_eval` expression only after an actual reflection-call failure.

## Writes And Validation

- Inspect the exact target before persistent or destructive work.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- Compile only after C# or assembly-related changes. Do not repeat compilation when no code changed.
- Starting a test, build, or async task is not success; poll to a terminal state.
- For long tasks, report phase changes, errors, or suspected-stuck state rather than every poll.
- Retry automatically only when the operation is idempotent and non-destructive.

## Persistent Console Capture

Use persistent capture when logs must survive long waits, Console clears, or Agent polling gaps:

1. Call `unity_console_capture_start` before the operation. Keep its `sessionId` and output directory.
2. Run the task normally. Unity writes JSONL independently of MCP polling.
3. Call `unity_console_capture_status` for counters and write failures. Use `unity_console_capture_read` with the previous `nextSequence` as the next `afterSequence` for incremental analysis.
4. Always call `unity_console_capture_stop` when the task ends, including failure paths. Report the JSONL path, summary path, counts, dropped logs, and SHA256.
5. Use `unity_console_capture_list` to find recent default-directory sessions.
6. Cleanup is two-phase: call `unity_console_capture_cleanup(dryRun=true)` first, inspect the returned directories, then pass its `confirmToken` with the same conditions and `dryRun=false` only when deletion is authorized.

Default captures belong under `Log/UPilotConsole/<timestamp>_<title>/`. Keep raw Console capture separate from domain-specific reports such as battle smoke-test reports. Prefer a project-relative custom path; do not set `allowOutsideProject=true` unless the user explicitly needs an external directory.

## Routing

- Installation: read `references/installation.md`.
- Common flows: read `references/workflows.md`.
- Tool choice: read `references/tool-routing.md` and `references/tool-boundaries.md`.
- Client transport/config: read `references/client-configs.md`.
- Recovery and destructive work: read `references/safety.md`.
- Only when the user explicitly requests UPilot Flow or YAML EditorWindow automation: read `references/flow.md`.
