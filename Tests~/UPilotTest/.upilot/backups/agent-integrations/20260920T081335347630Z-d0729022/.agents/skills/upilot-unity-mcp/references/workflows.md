# Workflows

## Health

1. Call `unity_mcp_status`.
2. Verify `connected`, `serverReady`, and the project path.
3. If a tool is not visible, call `unity_capabilities_get` or `unity_tools_find`.
4. Call `unity_ensure_ready` before mutations.
5. Require unified execution state `ready=true`, `authoritative=true`, and `isStale=false`; when blocked, follow `blockedReason` and `nextAction`.

Do not infer readiness from raw `isPlaying` or `isCompiling` values. Treat `queued`, `compiling`, `compiler_finished`, `domain_reload`, and `verifying` as non-ready compile phases.

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

1. In the canonical `./Tests~/UPilotTest` project call `unity_upilot_acceptance_run`.
2. Let it verify project identity, stop active Console captures, run one safe compile, discover and run EditMode tests, and recheck compile/Console errors.
3. Preserve the returned `Log/UPilotAcceptance/<timestamp>/summary.json` metadata and SHA256.
4. Do not start a persistent capture around this workflow because ConsoleCaptureService self-tests require no live capture.

## Multiple Projects

Always verify `paths.unityProjectAbsolute`. Stop if the connected Editor is not the intended project.
