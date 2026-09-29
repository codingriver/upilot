# Workflows

## Health

1. Call `unity_mcp_status`.
2. Verify `connected`, `serverReady`, and the project path.
3. If a tool is not visible, call `unity_capabilities_get` or `unity_tools_find`.
4. Call `unity_ensure_ready` before mutations.
5. Require unified execution state `ready=true`, `authoritative=true`, and `isStale=false`; when blocked, follow `blockedReason` and `nextAction`.

Do not infer readiness from raw `isPlaying` or `isCompiling` values. Treat `queued`, `compiling`, `compiler_finished`, `domain_reload`, and `verifying` as non-ready compile phases.

## Deployment Freshness

Use this preflight after Server/Bridge/protocol changes or for suspected version mismatch,
including an authorized Unity 6 / Unity 2022.3 acceptance matrix. It is not a new gate
for every ordinary Editor operation.

1. For each intended endpoint, record its exact project path and expected package/Server
   deployment source. Query `unity_mcp_status`; retain Server PID separately from Unity
   PID, Bridge session/identity-contract version and managed-domain generation.
2. Record the Server process start time and loaded build/source identity only when
   available from evidence tied to that exact process. Current
   `runtimeIdentity.mcpServer` supplies a PID, not loaded-source attestation. Unity's
   `processCreatedAt`, package acceptance `sourceIdentity` and the files currently on
   disk are not substitutes for Server identity. For an EXE, compare its selected build,
   not unrelated Python checkout timestamps.
3. Classify the evidence as **verified**, **suspected-stale**, or **unverified**. A Server
   predating relevant source edits is a risk indicator, not conclusive proof. `/health`
   reachability, equal version strings, matching disk hashes or a later process start
   alone cannot establish that the intended code was loaded. Missing evidence must remain
   unverified; do not mislabel this as a network fault or report deployment acceptance.
4. If refresh is necessary, follow `safety.md`'s Controlled Deployment Refresh procedure.
   Unknown freshness never triggers an automatic restart. Preserve active task/run/operation
   identities; report a busy or unobservable endpoint instead of disrupting its work unless
   the independent AI Service Maintenance grant explicitly covers `unity_service_restart`.
   That route may interrupt in-flight work but never cancels or replays it.
5. After an authorized refresh, recheck the exact project, connection, relevant identities
   and one real read-only call. Report the evidence actually established; reconnect success
   alone is not source attestation. Resume observing existing identities, never replay start.

This workflow does not introduce a runtime fingerprint API or require restarting both
components when only one changed.

## Compile Fix

1. Read `unity_compile_errors`.
2. Retain the current compile identity and terminal timestamps as the pre-write baseline, then patch the smallest relevant surface.
3. Immediately register the whole saved code batch once with `unity_write_batch_register(paths, compileWhenEditMode=true, deletedPaths=...)`. Put only existing code files in `paths` and absent deleted code files in `deletedPaths`; a code-deletion-only batch uses `paths=[]`. Do not register once per file.
4. If Unity is in PlayMode, pause, or a mode transition, do not invoke sync/compile and do not exit PlayMode without authorization. Let the Server retain the batch and resume it once after a fresh authoritative EditMode snapshot.
5. If automatic compilation was authorized, query `unity_write_batch_status(writeBatchId)` using the original batch identity and do not start another sync or compile. If a later automatic compile replaces the latest snapshot or loses correlation, inspect the original persisted batch; this is not evidence of batch failure or a reason to recompile. Only for a legacy/manual flow without automatic batch authorization, call `unity_sync_after_disk_write` once and `unity_safe_compile_and_wait` once with the available batch identity.
6. Observe the Unity-produced sequence `compile_queued -> compiling -> compiler_finished -> domain_reload_starting -> domain_reload_recovered/verifying -> completed|failed` when those boundaries apply. Each boundary must be persisted and sent immediately; `compiler_finished` remains non-terminal until Reload recovery and error verification complete.
7. Require terminal evidence for the same write batch/compile operation, `errorsVerified=true`, and `lastCompileVerifiedAt >= writeBatchCreatedAt`. `lastCompilerFinishedAt` alone is not verified completion. Until those fields exist, accept only the safe-compile result started after EditMode, not a cached historical status.
8. Treat `ok` as protocol success and inspect `status/phase`; compiler errors are an observed `ok=true/status=failed` business terminal. Re-read structured compile and relevant Console errors.

Do not trigger another compile when no C# or assembly file changed. Never infer that Unity compiled the latest edits from a pre-existing `completed` state, an unchanged timestamp, or missing immediate Console errors.

### Forbidden Compile Shortcuts

- **Never use `csc.exe`, `mcs`, `dotnet build`, or any external compiler** to validate Unity C# code. External compilers lack Unity's platform defines, asmdef references, preprocessor symbols, and assembly injection — their errors and success are not evidence of Unity compile state.
- **Never invoke Unity batchmode `-executeMethod` or `-runTests` directly from the shell** to bypass the MCP compile pipeline. Use `unity_test_run` or the project's acceptance workflow through MCP.
- **Never substitute shell-based `diff`/`grep` over log files** for `unity_compile_errors` or `unity_console_search_logs` — these tools carry the MCP Server's compile identity and error-verification metadata.

## Scene Or Asset Change

1. Read/find the exact target.
2. Use the matching semantic tool.
3. Save only when persistence is required.
4. Verify by reading the changed object or asset again.

## Tests And Builds

1. Start the operation.
2. Keep the returned test `runGuid`; for PlayMode/Domain Reload query `unity_test_results(runGuid=...)` after reconnect.
3. Poll the result/status tool to a terminal state.
4. For long operations, report only phase changes, errors, or suspected-stuck state.
5. Read Console errors and artifacts before declaring success.

For a generic project bridge operation, call `unity_operation_validate(jobSpec)` before `unity_operation_start`; validation is read-only and returns a normalized spec or precise field errors.

`status=no_tests` is a distinct cleaned terminal state with `total=0`; do not represent it as a fake failed test. If tests are required, fail the acceptance criterion explicitly.

## UPilot Package Acceptance

1. In the canonical `./Tests~/UPilotTest` project call `unity_upilot_acceptance_run` and retain the immediately returned `taskId`; `preflightOnly=true` remains a synchronous, read-only preflight.
2. Poll that Task with `unity_task_status` until terminal. Submission `ok=true` means queued, not accepted; the background Task verifies project identity, checks captures, compiles, discovers/runs tests, and checks compile/Console errors.
3. Preserve the final Task's `Log/UPilotAcceptance/<timestamp>/summary.json` metadata and SHA256.
4. Do not start a persistent capture around this workflow because ConsoleCaptureService self-tests require no live capture.

### Observe By Original Identity

| Identity retained at dispatch | Observe using | Never substitute |
|---|---|---|
| `writeBatchId` | `unity_write_batch_status(writeBatchId)` for the original persisted terminal state and linked compile evidence | Latest execution snapshot or a new compile |
| Compile `compileRequestId` | `unity_compile_status(compileRequestId)` / `unity_compile_errors(compileRequestId)` | Passing a compile `operationId` to generic operation tools or triggering another compile |
| Generic `operationId` | `unity_operation_status` / `unity_operation_wait` | Another operation start |
| `taskId` | `unity_task_status(taskId)` (`detailLevel=full` for full report) | Another acceptance or test start |
| `runGuid` | `unity_test_results(runGuid=...)` | A test run without that GUID |

Compile `operationId` is correlation metadata, not a generic job ID. If only that ID is known, retain it and query the original batch for linked evidence; do not pass it as `compileRequestId` or invent a request ID. `unity_compile_wait` observes idle state, not acceptance of a particular batch.

The public acceptance call queues a durable Task; its immediate `ok=true` only confirms
receipt. Default Operation and acceptance Task `summary` objects are capped at 16 KiB
UTF-8 (excluding MCP framing); `summaryVersion`, `responseBytes`, collection totals,
and `truncatedFields` describe omissions. Request `detailLevel=full` for the persisted
report or collected evidence. `includeRawState=true` with `summary` omits raw state.

`unity_queue_cleanup()` is a read-only inventory, not authorization to restart or
clean anything. It covers persisted and Server-memory work, a bounded Bridge queue
snapshot, Capture observation and OS Editor identity; inspect each source's result,
age, `complete` and truncation. A Bridge history list, ended known tasks, or zero
Capture sessions cannot prove the whole queue idle. Unknown, stale, disconnected,
truncated and Reload-affected sources remain `incomplete` even when listed items are
empty. For cleanup use the existing independent grant and exact-target preview flow.
For ordinary deployment refresh, retain each source's age, completeness and truncation; an incomplete inventory cannot establish an idle maintenance window. This does not remove the independently approved `unity_service_restart` exception in `safety.md`; that grant may interrupt in-flight work without proving the queue idle. An inventory alone never grants either cleanup or restart.

Public C# Capture owners can call `UPilotConsoleCaptureApi.VerifyStoppedAsync(sessionId)`:
`ok` means observation completed; only `stopped && artifactsVerified` establishes
this observation's cleanup evidence. It never stops a Capture, and a missing file is
not repaired. For `BRIDGE_RESPONSE_TOO_LARGE`, keep the original command or operation
identity and observe its state; `outcome=unknown` is not permission to replay a
business start. An oversize event or connection close 1009 marks its source incomplete.

### Bounded Acceptance Evidence Projection

Select known fields before serializing; `ConvertTo-Json -Depth` limits nesting, not
collection length, string length or UTF-8 bytes. Reuse the existing evidence client
below for transport; the following is a task-local read-only example, not a new API.
Do not print raw state, whole errors or arbitrary nested objects.

Choose the input shape explicitly (do not guess it from missing fields):
- Task **summary**: pass `unity_task_status(..., detailLevel="summary")` response `.data`
  with `Kind=TaskSummary`. Counts are `.tests`; acceptance/compile/artifact are
  `.result.result`. A full Task is different: its `.acceptanceReport` uses `Kind=AcceptanceReport`.
- Full acceptance report / local `summary.json`: pass the report object with
  `Kind=AcceptanceReport`; counts and compile evidence are `.steps.testStatus.data`
  and `.steps.compile.data`. A saved report has no self-referential artifact hash;
  compute its actual file metadata separately and supply `Evidence`.
- The existing HTTP client's immutable observation file wraps the tool envelope in
  `.result`: for a task-summary observation pass `.result.data`, not the outer
  observation. Missing/unknown shapes are a coverage gap, not zero tests or success.

<!-- bounded-acceptance-projection -->
```powershell
function ConvertTo-UPilotEvidenceSummary {
    param($InputObject, [ValidateSet('TaskSummary','AcceptanceReport')]$Kind, $Evidence = $null)
    $omitted = [Collections.Generic.List[string]]::new()
    function Scalar($value, [string]$field) {
        if ($null -eq $value) { return $null }
        if ($value -is [string]) {
            if ($value.Length -gt 512) { $omitted.Add($field); return $value.Substring(0,512) }
            return $value
        }
        if ($value -is [bool] -or $value -is [ValueType]) { return $value }
        $omitted.Add($field); return $null
    }
    $report = $InputObject
    $tests = $InputObject.steps.testStatus.data
    $compile = $InputObject.steps.compile.data
    $errors = @($InputObject.steps.compileErrors.data.errors | Where-Object { $null -ne $_ })
    if ($Kind -eq 'TaskSummary') {
        $report = $InputObject.result.result
        $tests = $InputObject.tests
        $compile = $report.compile
        $errors = @($InputObject.error | Where-Object { $null -ne $_ })
    }
    $artifact = $report.artifact
    if ($null -ne $Evidence) { $artifact = $Evidence }
    $view = [ordered]@{ kind=$Kind }
    foreach ($key in @('taskId','runGuid','status','phase')) {
        $view[$key] = Scalar $InputObject.$key $key
    }
    foreach ($key in @('acceptancePassed','cleanupVerified','testIdentityVerified','sourceUnchanged','failureCode','failureMessage')) {
        $view[$key] = Scalar $report.$key $key
    }
    $view.tests = [ordered]@{}
    foreach ($key in @('total','passed','failed','skipped','cleanupSucceeded','resultAuthoritative')) {
        $view.tests[$key] = Scalar $tests.$key "tests.$key"
    }
    $view.compile = [ordered]@{}
    foreach ($key in @('writeBatchId','compileRequestId','compileOperationId','errorsVerified')) {
        $view.compile[$key] = Scalar $compile.$key "compile.$key"
    }
    $view.artifact = [ordered]@{}
    foreach ($key in @('path','bytes','sha256')) { $view.artifact[$key] = Scalar $artifact.$key "artifact.$key" }
    $view.errorTotal = $errors.Count
    $view.errors = @($errors | Select-Object -First 8 | ForEach-Object {
        [ordered]@{ code=(Scalar $_.code 'errors.code'); message=(Scalar $_.message 'errors.message') }
    })
    if ($errors.Count -gt 8) { $omitted.Add('errors[8:]') }
    # Mark upstream summary omissions separately; local projection cannot restore them.
    $view.upstreamTruncatedFields = @($InputObject.truncatedFields | Select-Object -First 8 | ForEach-Object { Scalar $_ 'upstreamTruncatedFields' })
    if (@($InputObject.truncatedFields).Count -gt 8) { $omitted.Add('upstreamTruncatedFields[8:]') }
    $uniqueOmissions = @($omitted | Select-Object -Unique)
    $view.projectionOmissionCount = $uniqueOmissions.Count
    $view.projectionOmissions = @($uniqueOmissions | Select-Object -First 8)
    $json = $view | ConvertTo-Json -Depth 6 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 16384) {
        # Even JSON escaping can exceed the limit. Omit, never cut invalid JSON or fake success.
        $json = '{"projectionOmitted":true,"reason":"16 KiB UTF-8 budget exceeded; inspect original evidence","acceptancePassed":null}'
    }
    return $json
}
```
<!-- /bounded-acceptance-projection -->

Example for an already-existing acceptance artifact (no test/compile is started):

```powershell
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$evidence = @{ path=$reportPath; bytes=(Get-Item -LiteralPath $reportPath).Length;
    sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash }
ConvertTo-UPilotEvidenceSummary -InputObject $report -Kind AcceptanceReport -Evidence $evidence
```

The example exposes at most eight error entries and 512 characters per scalar string,
then enforces 16 KiB UTF-8 overall. `errorTotal` counts only errors present in this
input; it is not the complete run's error count. Missing test/outcome/cleanup fields
remain null (unknown); task `status` or protocol `ok` does not establish acceptance.
Projection omissions require consulting the original evidence, not replaying a run.
Retain original artifact path/bytes/SHA256 separately if the projection is omitted;
read any additional error details in bounded pages rather than dumping the report.

## Multiple Projects

The configured canonical project remains the default. Use an alternate only when existing project business rules explicitly authorize it and the current task explicitly selects it. Establish its full absolute path and dedicated HTTP endpoint before connecting; verify `paths.unityProjectAbsolute` against that exact target before any target tool call. Stop on a mismatch or unknown identity; never infer authorization from a matching basename or automatically switch projects. Do not broaden the existing exact-path acceptance allowlist.

Prefer that project's native registered tools. When an authorized second endpoint is not
injected, use the existing Streamable HTTP clients rather than hand-writing JSON-RPC/SSE.
Read `client-configs.md` for the read-only `upilot_mcp.client_probe` handshake. A probe's
success is not proof that tools are injected into another AI client or that deployment is fresh.

For repository acceptance calls, reuse `upilotserver~/scripts/p0_acceptance_call.py` from
the confirmed source checkout. It supports only the exact repository `Tests~/UPilotTest`
and `Tests~/UPilotTest2022` roots; project names select these fixed paths, not arbitrary
same-named directories. Determine each HTTP port from that project's configuration.

Example, from the repository root with the existing Python server environment:

```powershell
python upilotserver~/scripts/p0_acceptance_call.py --project UPilotTest2022 --port <HTTP_PORT> --tool unity_mcp_status --args '{}' --output alternate-status.json
```

The script initializes through the MCP SDK, checks the connected absolute project path,
then proxies the exact tool through `unity_tool_call` and its existing permission gates.
It neither registers clients nor starts/restarts the Server. Missing source or Python/MCP
dependencies is a reported prerequisite, not permission to install them automatically.

- Keep `not_sent`, `sent_unknown` and `response_received` distinct. Connection/argument
  failures do not send the requested tool; a timeout after dispatch leaves execution
  uncertain. Never automatically replay a write. Poll established task/run/operation IDs
  through their normal status tools after reconnecting.
- Stdout contains connection identity, selected status/test evidence and the immutable
  evidence path/bytes/SHA256, not the complete status payload. Interpret protocol `ok`
  separately from the business outcome and cleanup fields. Process identity summaries
  explicitly leave deployment freshness unverified.
- Evidence files remain under the selected project's `Log/P0P1`; existing files are not
  overwritten. Credentials are redacted from parameters, results and output. Raw
  exception text and malformed argument text are omitted, not written to a traceback.
- Do not supply secrets through `--args`. The one-shot client rejects credential-bearing
  arguments and known Capture start/stop ownership workflows, including nested
  task/proxy calls. Use a native client with secure owner-token lifecycle handling for
  those workflows. This guard is not a sandbox for arbitrary reflection or user code.
- Use task-local read-only probes for this routing validation; do not start a full test
  suite merely to check a second endpoint.
