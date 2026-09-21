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

## Profiler Out-of-Process Acceptance

- A Unity 6 acceptance attempt using `ProfilerWindow.ShowProfilerOOP` blocked the main Editor without establishing a manageable Profiler process. Treat this as an observed workflow risk, not proof that every Unity version has the same fault.
- Before automatic OOP launch, require a verified, non-blocking launch fixture that identifies the exact Profiler process and can close it safely. Without that fixture, do not invoke `ShowProfilerOOP` or guess command-line arguments as a retry.
- Report read-only process/session identity observations separately from actual process start/stop cycles. Observing an unchanged main session does not satisfy a requested cycle count.
- Do not resume cancelled or abandoned acceptance merely because a TODO or this reference describes it. Without renewed authorization, retain its unexecuted/abandoned result rather than reporting a pass.
- If an attempted launch blocks the Editor, follow the existing Hang diagnostics: inspect `unity_hang_status`, preserve the exact main-session identity, and collect `unity_hang_capture` when diagnostic evidence is needed. Do not automatically terminate or restart the Editor.
- These limits concern OOP process-lifecycle acceptance, not ordinary authorized `unity_profiler_capture_start/status/stop` data collection.

## Compile

- Compile only after code or assembly changes.
- Register assembly-related disk writes immediately. Do not invoke sync or compile in PlayMode; an authorized write batch resumes automatically only after Unity reports authoritative EditMode.
- Read structured errors before editing.
- Compile verification must come from Unity's Roslyn pipeline. External compilers (`csc`, `mcs`, `dotnet build`) differ in defines, asmdef references, and assembly injection — do not treat their results as compile evidence.

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
