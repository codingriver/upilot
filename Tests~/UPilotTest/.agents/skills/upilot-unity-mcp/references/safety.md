# Safety And Recovery

## Before Writes

- Verify the connected project.
- Inspect the exact scene, object, component, asset, prefab, package, or file.
- Confirm whether persistence is required.

## Timeouts

1. Call `unity_mcp_status`.
2. Inspect `unity_operation_list` or task status for phase, elapsed time, last progress, and suspected-stuck state.
3. Retry once only if the operation is idempotent and non-destructive.
4. Stop when Unity is disconnected, connected to the wrong project, or still stuck after the bounded retry.

## Compile

- Compile only after code or assembly changes.
- Do not compile in PlayMode unless the workflow requires it.
- Read structured errors before editing.

## Reflection

Use `unity_reflection_call` first. After a real failure, use one bounded `reflection_eval` expression or add a stable compiled helper. Do not repeatedly probe unsupported syntax.
