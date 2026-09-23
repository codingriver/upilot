# AI Service Maintenance

Implementation and targeted evidence, 2026-09-22.

## Contract

The independent Unity settings section "AI Service Maintenance" owns the human
approval to restart this project's Bridge or Server. Approval defaults to false.
It applies to every UPilot version and installation source, only in stable
EditMode. It does not authorize exiting PlayMode, restarting Unity, compiling,
downloading/updating a Server, stopping Capture sessions, or replaying business.

Configuration lives in `.upilot/config.json`:

```json
{
  "aiServiceMaintenance": {
    "approved": false,
    "approvedAtUtc": "",
    "projectPath": "",
    "restartTimeoutSeconds": 120
  }
}
```

Only the human settings UI should change this grant or timeout. The integer
timeout range is 30-600 seconds; an omitted timeout defaults to 120. Explicit
invalid values fail closed. Project relocation requires reapproval; changing the
package version or source does not. Automation select-all, `hangRestart`, and
ordinary project write access are independent of this grant.

## Entry And Observation

`unity_service_restart` is destructive and non-idempotent. It accepts:

- `maintenanceId`: a new hyphenated UUID retained by the caller.
- `target`: `bridge` or `server`; Server restart also re-establishes Bridge.
- `reason`: 1-512 characters.
- `expectedProjectPath`, `expectedServerProcessId`, `expectedBridgeSessionId`.
- `expectedMaintenanceId`: the most recent maintenance ID, or empty initially.

Read expected identities from `unity_mcp_status.aiServiceMaintenance`.
`unity_capabilities_get` exposes the same summary. The previous-maintenance field
is compare-and-set protection: an old request cannot become a new restart after
the latest journal is replaced. Exact duplicate requests observe their existing
record; changed arguments conflict. Neither behavior authorizes automatic retry.

Acceptance persists `Library/UPilot/service-maintenance.json` before scheduling
execution outside the Bridge request lifetime. The configured timeout is fixed
at acceptance; `deadlineAtUtcMs` spans all phases. Per-probe timeouts consume the
same budget. Authorization, Editor state and remaining time are rechecked before
stop/start, including a delayed replacement-process launch.

The recent record contains phases, affected components, bounded command IDs,
original/replacement identities, fixed timestamps, attempted actions, verification
and errors. `affectedWorkComplete=false` intentionally does not promise a complete
inventory of user business work. In-flight work may be interrupted; it is never
automatically cancelled or replayed by this feature.

The following are illustrative fragments, not real restart evidence:

```json
{"status":"accepted","restartTimeoutSeconds":120,"affectedComponents":["server","bridge"]}
```

```json
{"ok":false,"error":{"code":"SERVICE_MAINTENANCE_NOT_APPROVED"}}
```

```json
{"status":"timed_out","errorCode":"SERVICE_RESTART_TIMEOUT","failurePhase":"verifying","stopAttempted":true}
```

Timeout removes pending maintenance stop/start callbacks, but does not kill a
replacement Server, undo previous effects or rewrite late recovery as success.
`deadline_exceeded_unconfirmed` is the read-only Server projection of an overdue
journal without a Unity-confirmed terminal result, not a synthesized timeout.
An HTTP timeout or disconnect must lead to observation, never resubmission.

## Files And Responsibilities

| File | Responsibility |
| --- | --- |
| `Editor/Core/UPilotProjectConfig.cs` | Independent project-local config defaults. |
| `Editor/Core/UPilotStatusWindow.cs` | Human grant, bounded timeout input and last-operation diagnostics. |
| `Editor/Core/UPilotServiceMaintenance.cs` | Strict parsing, journal, deduplication, deadline, preflight, scheduling, Bridge-only verification and recovery. |
| `Editor/Core/UPilotMcpServerManager.cs` | Reuse process ownership/stop/start/probe logic; optional maintenance deadline; delayed-action cancellation. |
| `Editor/Core/UPilotServerRestartDiagnostics.cs` | Persist Server maintenance deadline and reject late success. |
| `Editor/Core/UPilotQuickStart.cs` | Mutual exclusion with manual/automatic repair; prevent a second automatic repair of the same failure cycle. |
| `Editor/Core/UPilotBridge.cs` | Register the command and exclude competing Bridge restarts. |
| `upilotserver~/src/upilot_mcp/service_maintenance.py` | Read-only config/journal projection, including overdue-unconfirmed observation. |
| `upilotserver~/src/upilot_mcp/domain/status_service.py` | Preflight, dispatch, duplicate observation and status/capabilities integration. |
| `upilotserver~/src/upilot_mcp/mcp_tools/status_tools.py` | Public typed tool and independent-permission registry metadata. |
| `upilotserver~/src/upilot_mcp/server.py` | Never resend `service.restart` after Domain Reload. |
| `Tests/Editor/UPilotServiceMaintenanceTests.cs` | Temporary-journal and controlled-clock tests without real service termination. |
| `upilotserver~/tests/test_service_maintenance.py` | Permission, schema, proxy, state, timeout and transport-replay contracts. |

Legacy restart callers retain their existing timeouts when no AI maintenance
deadline is supplied. This is not a new deployment engine or generic task system.

## Verification

- Canonical project: `D:\upilot\Tests~\UPilotTest`, Unity `6000.6.0a2`.
- Unity process: `57636`; observed Server process: `108348`.
- Final write batch: `wb-9d2dad67-4cff-46bc-9d09-6b3665454bda`.
- Compile request: `req-ae9272a0-fe41-4146-b7c1-46dc7a53f789`.
- Compile operation: `d20b556e8e6944b291a7fa300574a70d`.
- `writeBatchCreatedAt=1790067361807`, `lastCompileVerifiedAt=1790067378763`;
  `terminal=true`, `errorsVerified=true`, `correlationVerified=true`.
- Compile: 0 errors, 3 existing UAC1009 warnings in unchanged
  `Editor/Optional/Flow/Schema/UPilot.Flow.Models.cs`.
- Targeted acceptance: 25 total / 25 passed / 0 failed / 0 skipped;
  new maintenance fixture plus three existing Server restart diagnostic tests.
- Task: `task-70d104ab-47b1-460e-9d43-61d3b0b920b3`.
- runGuid: `a2b8de4f-cd01-4fc2-9a28-f2ca2cb29d29`.
- `acceptancePassed=true`, `cleanupVerified=true`, `sourceUnchanged=true`.
- Source SHA256 at acceptance:
  `90ac9f833b11ee3a0cfbe60ef97f47e2a25b3c7cc554063b7625af35381ad440`.
- Report:
  `Tests~/UPilotTest/Log/UPilotAcceptance/1790067653524_req-e88b1d6c-1e20-4fb9-bbf2-70bff5d6ee15/summary.json`.
- Report bytes: `523923`; SHA256:
  `ebbf9b718054b72110947e945b42aa3f9dcac31f8b62da9a5e461dddff779208`.
- Python: 49 passed across `test_service_maintenance.py`,
  `test_server_restart_diagnostics.py`, `test_http_unity_mcp_status.py`,
  `test_result_reload_evidence.py`; two existing websockets deprecation warnings.
- Agent Rules 39 / Skill Pack 42: source checks and both installed validators
  passed; five targets current. An actual repeated sync left all 46 managed
  files' hashes and full-precision modification times unchanged, added no backup
  directories, and preserved all bytes outside the three managed rule blocks.

## Remaining Live Coverage

The human grant was not enabled or changed during implementation. No real
Bridge/Server restart was executed through the new entry, and no user's business
task or Capture was stopped. A running-work fixture tests dispatch-once semantics;
it is not evidence of actual process interruption.

Exact tool discovery on the running Server returned no `unity_service_restart`
match (`req-ef31cf07-dacf-4ecd-97f3-3d9230d121ec`). Its Python process has not been
refreshed to expose this tool. Initial deployment therefore needs a human refresh,
followed by human approval in settings and a client tool-list refresh.

Pending live checks: Bridge-only and Server process cycles, real OS port/ownership
failure injection, visual UI acceptance, live overdue recovery and permitted
in-flight interruption. No complete EditMode suite, Unity 2022.3 matrix or release
was run for this task.
