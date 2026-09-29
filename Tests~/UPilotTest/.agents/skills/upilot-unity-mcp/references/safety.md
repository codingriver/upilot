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

## Controlled Deployment Refresh

- Missing runtime/source identity is insufficient evidence to restart anything. First
  retain the endpoint, exact project, Server/Unity process identities and the evidence
  supporting a suspected mismatch; use the Deployment Freshness workflow.
- Before a refresh, inspect current tasks, tests, `unity_operation_list` and
  `unity_console_capture_list(activeOnly=true)`, including known pending compile/write
  batches. If work is in flight, ownership is unknown or the state cannot be observed,
  stop and report the specific blocker, except for the independently approved
  `unity_service_restart` route described below. Do not cancel tasks or stop another Capture to
  make the deployment check pass.
- Require existing authorization for the exact affected component and a maintenance
  window that does not interrupt other work. A Server-only change does not authorize
  restarting Unity. Changed Bridge code uses the correlated Unity compile/reload workflow;
  do not invent an unconditional "restart both" step or recompile unchanged C#.
- After refresh, revalidate project identity, connection, changed component identity and
  a real read-only call. Preserve old task/run/operation IDs for observation. Neither
  reconnect nor a new PID permits replaying a previously sent start.

## AI Service Maintenance

- The independent AI service-maintenance grant applies only to the exact approved project, regardless of package version/source. Automation select-all, hangRestart and write permission do not grant it. Never self-enable it or edit its timeout through files, reflection or UI automation.
- Read `unity_mcp_status.aiServiceMaintenance`. Explicit restart requires effective approval, valid configuration and exact project/process/Bridge identities. Hard stop bypasses ordinary queue/EditorReady gates and may run during PlayMode or compilation without changing Unity mode.
- Call `unity_service_restart` with the original exact expected identities and a fresh maintenanceId. Both `target=bridge` and `target=server` now mean complete UPilot reset plus Server/Bridge restart. Neither restarts Unity, compiles, saves scenes, installs updates or deletes history.
- Human-confirmed **硬停止全部任务并重启 UPilot…** uses the local Unity manager, including when MCP is disconnected; no AI grant is required for that human action. It affects every chat's current-project tasks and skips user Finally/Cleanup. Queue activity is invalidated first; reports are best effort and cannot block reset.
- Automatic escalation requires the effective service-maintenance grant AND the separate default-off **软停止失败后自动硬停止全部任务并重启 UPilot** setting. Eligible failures are valid-target cooperative-stop failure, exception, cleanup timeout or unsupported stop, not business failure with completed cleanup, missing tasks or validation/permission errors. Multiple failures merge into one reset. A failed hard stop never recursively restarts.
- The accepted maintenance deadline defaults to 120 seconds, configurable from 30 to 600 seconds in the UI. It covers every phase and cannot be renewed by polling, settings changes or duplicate requests. Transient health probes stay inside that budget; health failure alone is not a port conflict. Do not replay side effects.
- Accepted is not completion. Observe the original maintenance identity after reconnect; report queue reset and service restart separately. Timeout/failure leaves the old queue invalidated, not recoverable, and does not auto-retry or kill Unity. A new PID is not proof that edited source was loaded.
- All service startups establish empty active state; retained logs/reports are not recovery input. Ordinary reconnect or normal compile/PlayMode Domain Reload is not a service restart and does not extend existing deadlines.
- Restarting Server cannot interrupt arbitrary synchronous Unity main-thread code. Physical engine/external busy state must remain visible even when UPilot queues are empty. A fully hung Unity editor may not process its button; this workflow does not add automatic Unity termination.
- A Server predating this contract needs a separately authorized deployment refresh. Do not bypass unavailable tools or failed authorization with shell/reflection.

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
