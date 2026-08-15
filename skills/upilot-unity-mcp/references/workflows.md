# Workflows

## Health

1. Call `unity_mcp_status`.
2. Verify `connected`, `serverReady`, and the project path.
3. If a tool is not visible, call `unity_capabilities_get` or `unity_tools_find`.
4. Call `unity_ensure_ready` before mutations.

## Compile Fix

1. Read `unity_compile_errors`.
2. Patch the smallest relevant surface.
3. Call `unity_sync_after_disk_write` once for the edit batch.
4. Call `unity_safe_compile_and_wait` once.
5. Re-read compile and Console errors.

Do not trigger another compile when no C# or assembly file changed.

## Scene Or Asset Change

1. Read/find the exact target.
2. Use the matching semantic tool.
3. Save only when persistence is required.
4. Verify by reading the changed object or asset again.

## Tests And Builds

1. Start the operation.
2. Poll the result/status tool to a terminal state.
3. For long operations, report only phase changes, errors, or suspected-stuck state.
4. Read Console errors and artifacts before declaring success.

`status=no_tests` is a distinct cleaned terminal state with `total=0`; do not represent it as a fake failed test. If tests are required, fail the acceptance criterion explicitly.

## UPilot Package Acceptance

1. In the canonical `./Tests~/UPilotTest` project call `unity_upilot_acceptance_run`.
2. Let it verify project identity, stop active Console captures, run one safe compile, discover and run EditMode tests, and recheck compile/Console errors.
3. Preserve the returned `Log/UPilotAcceptance/<timestamp>/summary.json` metadata and SHA256.
4. Do not start a persistent capture around this workflow because ConsoleCaptureService self-tests require no live capture.

## Multiple Projects

Always verify `paths.unityProjectAbsolute`. Stop if the connected Editor is not the intended project.
