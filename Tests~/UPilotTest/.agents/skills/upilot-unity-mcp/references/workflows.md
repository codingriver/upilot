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

## Multiple Projects

Always verify `paths.unityProjectAbsolute`. Stop if the connected Editor is not the intended project.
