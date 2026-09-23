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

- The Unity settings section **AI Service Maintenance** contains an independent human
  grant and an integer restart timeout (30-600 seconds, default 120). It applies to all
  package versions and installation sources. Automatic-disposition select-all,
  `hangRestart`, and project write permission neither grant nor revoke it.
- Read `unity_mcp_status.aiServiceMaintenance` (also returned by capabilities). Require
  `effectiveApproved=true`, valid configuration, stable authoritative EditMode and exact
  project/component identity. Never use files, reflection, or UI automation to grant
  yourself approval or increase the timeout. Project relocation requires renewed approval;
  package upgrades and source/path changes alone do not.
- Call `unity_service_restart` with a fresh UUID `maintenanceId`, `target=bridge|server`,
  a short `reason`, and the reported `expectedProjectPath`, `expectedServerProcessId`,
  `expectedBridgeSessionId`, and `expectedMaintenanceId` (empty only with no prior record).
  The last field is a compare-and-set guard, not an instruction to resume the prior restart.
- `bridge` leaves the Server process intact. `server` restarts the current configured
  Server and re-establishes the Bridge; inspect `affectedComponents`. Neither route exits
  PlayMode, restarts Unity, compiles C#, builds/downloads/installs a Server, or changes its
  runtime mode. Changed Bridge C# still needs the correlated compilation workflow.
- This grant explicitly permits interruption of current-project in-flight work; no idle
  wait is required. Preserve known task/run/operation identities. The affected command list
  is bounded and `affectedWorkComplete=false`: it is not proof that all business work was
  enumerated, stopped or recovered. Never stop another Capture or replay business.
- `accepted` is not success. Keep the request identity and query `aiServiceMaintenance.latest`
  after reconnect. The journal is `Library/UPilot/service-maintenance.json`; Unity settings
  can display failures when the Server cannot answer. A mismatched/missing record requires
  recovery investigation, not an automatic resend. Same-ID duplicate observation does not
  execute a second restart; it is not permission to retry a non-idempotent tool.
- The one deadline begins at durable acceptance and includes all restart phases. Changing
  settings, phase, session or polling does not extend it. `SERVICE_RESTART_TIMEOUT` with
  `status=timed_out` preserves prior effects and does not kill the replacement process.
  Late recovery is current health, not a rewrite of the original timeout as success.
- `deadline_exceeded_unconfirmed` means the Server sees an overdue journal without a
  Unity-confirmed terminal result, for example when the Editor is not pumping. HTTP/tool
  timeout is also distinct from maintenance timeout. Inspect identity and hang diagnostics;
  never automatically restart Unity or replay the request.
- Success requires the expected component identities, project, handshake and a real
  read-only round trip. Refresh the AI client's tool list separately after MCP changes.
  A new Server PID does not prove arbitrary edited source was loaded; an EXE restart runs
  the deployed EXE, not changes in a Python checkout.
- A Server predating this tool needs an initial human-controlled refresh. Do not bypass
  a missing maintenance endpoint or failed authorization with reflection or shell commands.

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
