# TODO: Fix UnityPilot `unity_test_run` MCP Result Collection

## Background

During UnityUIFlow package decoupling validation, `unity_test_run` was invoked with:

```text
testMode: EditMode
testFilter: UnityUIFlow.ParsingAndPlanningTests
```

The tool returned `status=running`, `total=0`, and repeated `unity_test_results` polling stayed at `running/total=0` for more than two minutes. A later Unity compile/domain reload cleared the state.

This is not caused by the UnityUIFlow optional compile macro itself. It exposes an existing weakness in the UnityPilot test runner MCP implementation.

## Current Findings

- `unity_test_list` currently reports only InputSystem package tests in the test project.
- `UnityUIFlow.*` tests are not listed because they live under package `Samples~/Tests`, and the consuming test project does not compile those samples by default.
- `Editor/Pilot/UnityPilotTestService.cs` creates a placeholder `TestCallbackProxy`, but does not actually register an implementation of `UnityEditor.TestTools.TestRunner.Api.ICallbacks`.
- `test.run` sets `_lastResults.status = "running"` immediately after `TestRunnerApi.Execute(...)`.
- Completion is currently approximated with `EditorApplication.delayCall`, which does not read real `RunFinished` data and is not reliable.
- When a filter matches no tests, the service does not detect the empty match before executing, so callers can see a confusing `running/total=0`.

## Required Fixes

1. Implement real Test Runner callbacks.
   - Add a concrete class implementing `UnityEditor.TestTools.TestRunner.Api.ICallbacks`.
   - Register it with `TestRunnerApi.RegisterCallbacks(...)` before `Execute(...)`.
   - Populate `_lastResults` from `RunStarted`, `TestFinished`, and `RunFinished`.
   - Ensure callbacks are unregistered/disposed after completion.

2. Handle empty filters explicitly.
   - Before calling `Execute`, resolve/list matching tests for the requested `testMode` and `testFilter`.
   - If there are zero matches, return a completed result with `total=0` and a clear message such as `No tests matched filter`.
   - Do not leave `_isRunning` true for empty matches.

3. Add timeout and recovery.
   - Track run start time and expose a timeout error if no callback arrives within the requested/default timeout.
   - Reset `_isRunning` and mark status `failed` or `timeout`.
   - Prefer a cancellable execution path if Unity Test Framework exposes one for the installed version.

4. Improve result shape.
   - Report `status` as one of `none`, `running`, `completed`, `failed`, `timeout`.
   - Include counters: `total`, `passed`, `failed`, `skipped`.
   - Include per-test `testName`, `testStatus`, `duration`, `message`, and `stackTrace`.

5. Make sample tests discoverable only when intentional.
   - Decide whether the package test project should import/compile `Samples~/Tests`.
   - If yes, document the import step or move internal package validation tests into a normal test assembly path in the test project.
   - If no, avoid using `UnityUIFlow.*` sample test filters for MCP test-run smoke validation.

## Acceptance Criteria

- Running `unity_test_run` with a known valid EditMode test completes and `unity_test_results` eventually returns `completed`.
- Running `unity_test_run` with a filter that matches no tests returns `completed`, `total=0`, and does not remain `running`.
- Running `unity_test_results` after completion includes real Unity Test Runner counts and per-test results.
- Starting a second test while one is active returns `TEST_ALREADY_RUNNING`.
- A stalled Unity Test Runner execution eventually returns `timeout` and resets `_isRunning`.

## Related Files

- `Editor/Pilot/UnityPilotTestService.cs`
- `unitypilot~/src/unitypilot_mcp/mcp_stdio_server.py`
- `unitypilot~/src/unitypilot_mcp/tool_facade.py`
- `Tests~/UnityUIFlowTest/Packages/manifest.json`
- `Samples~/Tests/UnityUIFlow.Tests.asmdef`
