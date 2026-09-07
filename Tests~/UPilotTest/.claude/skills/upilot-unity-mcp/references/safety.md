# Safety And Recovery

## Before Writes

- Verify the connected project.
- Inspect the exact scene, object, component, asset, prefab, package, or file.
- Confirm whether persistence is required.

## Timeouts

1. Call `unity_mcp_status`.
2. Inspect `unity_operation_list` or task status for phase, elapsed time, last progress, and suspected-stuck state.
3. Treat `waitWindowElapsed=true/terminal=false` as non-terminal and continue polling until completion or `jobTimeoutAt`.
4. If the Editor is not pumping, call `unity_hang_status` and collect `unity_hang_capture` before restart when evidence is needed.
5. Retry once only if the operation is idempotent and non-destructive.
6. Stop when Unity is disconnected, connected to the wrong project, or still stuck after the bounded retry.

## Compile

- Compile only after code or assembly changes.
- Register assembly-related disk writes immediately. Do not invoke sync or compile in PlayMode; an authorized write batch resumes automatically only after Unity reports authoritative EditMode.
- Read structured errors before editing.

## Configuration CSV

- Read with `unity_config_csv_get` before modifying.
- Patch only through `dryRun=true`, preview inspection, explicit write approval, and `confirmToken` apply.
- Verify the tool reports unchanged encoding, newline style, column count, and non-target bytes.

## Reflection

- `unity_reflection_call` may invoke arbitrary state-changing methods, requires project write access, is non-idempotent, and must never be retried automatically.
- Inspect the exact type, method, target instance, and arguments before calling it. Use `unity_type_exists`, `unity_reflection_find`, or a dedicated semantic tool for read-only discovery.
- Choose the `unity_reflection_call` request shape before execution: structured `typeName` + `methodName`, or one bounded `expression`. Never fall back between the two engines after a real call because the first attempt may already have side effects. Add a stable compiled helper for repeated or multi-step logic.
- `csharp_eval`, `reflection_emit_type`, and `execution_session` require write access and are non-idempotent. Budget failure and runtime exceptions do not roll back side effects. Never retry automatically.
- Close every persistent execution session in success and failure paths. Treat handles from an earlier Domain Reload as expired; never substitute a similarly named object.
