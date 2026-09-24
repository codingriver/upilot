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
3. Immediately register the saved assembly-related paths once with `unity_write_batch_register(paths, compileWhenEditMode=true)`.
4. If Unity is in PlayMode, pause, or a mode transition, do not invoke sync/compile and do not exit PlayMode without authorization. Let the Server retain the batch and resume it once after a fresh authoritative EditMode snapshot.
5. If automatic compilation was authorized, poll the batch/execution state and do not start another sync or compile. For a legacy/manual flow, call `unity_sync_after_disk_write` once and `unity_safe_compile_and_wait` once with the batch identity.
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
| Compile `operationId` | Compile wait/status for that operation | A second compile request |
| Generic `operationId` | `unity_operation_status` / `unity_operation_wait` | Another operation start |
| `taskId` | `unity_task_status` / `unity_task_wait` (`detailLevel=full` for full report) | Another acceptance or test start |
| `runGuid` | `unity_test_results(runGuid=...)` | A test run without that GUID |

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

Public C# Capture owners can call `UPilotConsoleCaptureApi.VerifyStoppedAsync(sessionId)`:
`ok` means observation completed; only `stopped && artifactsVerified` establishes
this observation's cleanup evidence. It never stops a Capture, and a missing file is
not repaired. For `BRIDGE_RESPONSE_TOO_LARGE`, keep the original command or operation
identity and observe its state; `outcome=unknown` is not permission to replay a
business start. An oversize event or connection close 1009 marks its source incomplete.

In PowerShell, select known fields explicitly before serializing; `ConvertTo-Json
-Depth` controls nesting, not response size:

```powershell
$task = $response.data
$task | Select-Object taskId, runGuid, status, deadlineAt, error,
    @{Name='testTotal';Expression={$_.tests.total}}, truncatedFields |
    ConvertTo-Json -Depth 5
$first = @($response.data.items) | Select-Object -First 8 id, type, status
```

## Multiple Projects

Always verify `paths.unityProjectAbsolute`. Stop if the connected Editor is not the intended project.

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
