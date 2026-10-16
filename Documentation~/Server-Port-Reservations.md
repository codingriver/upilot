# Local port reference registration

## Authoritative configuration and availability

The actual project configuration remains `<project>/.upilot/config.json`.
Configuration validation, atomic writes, bind probes and observed service listeners
remain authoritative. Reference registration does not reserve a socket or prove
ownership. Recommendations avoid every registered port, including stale entries
and cross-protocol uses, and still check actual bind availability. Manually chosen
ports remain governed by actual project configuration and bind probes.

Saving unrelated project settings preserves the current on-disk port pair. Explicit
endpoint changes validate both ports; invalid ports, actual unavailability and
configuration-write failures retain their existing error behavior. Atomic config
replacement never deletes the destination first; failed candidate files remain
at the `.tmp-*` paths reported in the error log.

## User settings reference list

The only persistent registration copy is `projectPorts` in the current operating
system user's `~/.upilot/settings.json`, shared by all Unity projects:

```json
{
  "maxSegmentSizeBytes": 524288,
  "maxConcurrentSegmentRequests": 8,
  "projectPorts": {
    "D:/upilot/Tests~/UPilotTest": [8765, 8011],
    "D:/OtherProject": [8766, 8012]
  }
}
```

Each key is a normalized absolute project path with `/` separators and no trailing
separator (filesystem roots retain their root separator). Windows path matching is
case-insensitive, while the stored path spelling remains readable. Each value is
exactly `[WebSocket port, HTTP port]`, with distinct integer ports from 1 to 65535.
A missing `projectPorts` field means an empty reference list.

Successful configuration saving and existing startup checks register the current
project. Each update acquires the settings-file-specific cross-process mutex with
a maximum five-second wait, rereads the latest JSON, updates only that project's
entry and atomically replaces the file. Other projects, download preferences and
unknown fields are preserved. An unchanged entry does not rewrite the file.
Download preference saves and restore-default actions likewise preserve the
registration section. UI redraws, port checks, health queries and heartbeats do
not register projects. Entries do not expire; unavailable or unmounted project
folders are not automatically removed.

Download and registration fields are validated independently when read. Invalid
registration data does not invalidate otherwise valid download preferences, but
recommendations fail visibly when the registration section cannot be trusted.
Malformed JSON, read failures, lock timeouts and write failures retain the original
file; default values from a failed read must not be written over it. Caught
registration failures emit red Unity Console errors with the operation, settings
path and original exception stack, independently of file/verbose logging options.
Repeated identical infrastructure failures use the existing bounded ten-second
aggregation policy, with pending counts flushed on recovery. Registration sync
failure does not undo a successful actual-project configuration save or override
its separate validation result. It does not open a modal dialog.

## Print, clear and legacy data

Advanced Settings and the setup wizard retain the existing two buttons:

- **打印端口登记** prints the settings path and one readable project record per
  line, labelled `WS` and `HTTP`; an empty section prints `暂无端口登记`.
- **清除端口登记** atomically replaces only `projectPorts` with an empty object.
  It preserves download preferences, unknown fields, project configuration and
  running services. A later normal save/startup can register the project again.

The shared user settings file is outside current-project preference reset.
There is no additional registration file or continuing EditorPrefs copy. The old
`upilot_allocate_ports` EditorPrefs value is never read or written. Its hashed
project identities cannot reconstruct project paths, so there is no hash migration,
fallback or dual write. After the first successful new registration save, the old
EditorPrefs key is deleted idempotently. Shared Server installation/cache locks
remain unrelated and unchanged.

## Targeted validation

`UPilotUserSettingsTests` and `UPilotPortRegistryTests` use temporary settings files;
only legacy-key deletion tests use isolated random EditorPrefs keys. Coverage
includes path/port validation, multiple projects, field preservation, interleaved
independent updates, mutex contention and storage failures, legacy-data exclusion,
print/clear behavior, actual-config authority and recommendation exclusions.
`UPilotSetupRecoveryTests` uses temporary registration JSON for recommendation
failures and recovery while preserving actual project configuration and endpoints.
The current implementation requires its own correlated Unity compilation and
targeted test evidence; historical results below do not validate it.

## Historical EditorPrefs acceptance (2026-10-10)

The canonical `Tests~/UPilotTest` project (Unity `6000.6.0a2`) passed a correlated
MCP compilation for WriteBatch `wb-90d4f3ad-e274-482a-9a9d-806b77ce17fa`:
`terminal`, `errorsVerified` and `correlationVerified` are true, with zero errors.
The one CS0618 warning is in the unchanged `UPilotDiagnosticModuleTamperTests.cs`.
The first automatic compilation had failed on an unsupported `NonParallelizable`
attribute in the new fixture; removing only that attribute fixed compatibility,
without changing assertions or dependencies. Earlier connection-preflight failures
never submitted a WriteBatch or compilation; their observations remain available.

Run `6dda8a93-37fa-4cdb-8f04-4907818ff21d` passed 58/58 targeted tests
(`UPilotPortRegistryTests`: 48; `UPilotSetupRecoveryTests`: 10), with zero failures
or skips, an authoritative terminal result, successful cleanup and no unresolved
resources. All seven code-input hashes and both deleted paths match the registered
batch. The new Bridge project-path hash helper was exercised by the compiled
fixture; this is not a whole-Bridge or Server loaded-source attestation.

Evidence: `Tests~/UPilotTest/Log/P0P1/ports-editorprefs-20261010/` contains the
original calls and `final-acceptance-verification.json`. Console error searches
returned zero, but compile logs have a `console_cleared` gap and execution/cleanup
phase logs are partial; complete Console coverage is not claimed. No full EditMode
suite, Unity 2022.3 matrix, original-user long-duration observation or manual
production-key button click was run. Full Server loaded-source identity remains
unverified. No real registration key was cleared by the tests, no service was
restarted, and unrelated V06 acceptance matrices remain open.

Previous file-registry evidence below is historical, not EditorPrefs acceptance.

## Historical file-registry evidence (not EditorPrefs acceptance)

### Recorded evidence (2026-09-10)

- Canonical project: `D:\upilot\Tests~\UPilotTest`, Unity `6000.6.0a2`.
- Port change batch: `wb-ed39f0dc-3dc5-43ff-91ef-5bc308a7a658`,
  created `1789034306791`, verified `1789034317472`, zero compiler errors/warnings.
- Latest targeted run: `a3600169-469b-422c-a95a-b467df672940`, 17 passed,
  zero failed/skipped, authoritative results and successful cleanup.
- Acceptance report:
  `Tests~/UPilotTest/Log/UPilotAcceptance/1789034359747_req-050ab36e-0f49-46b8-9666-95145ab5914c/summary.json`,
  90142 bytes, SHA-256
  `745f7e94f61e446dc70198377a1b5c6311c2de8bc4f3a4a7f065c19707368562`.
- Final package acceptance is **pending source stability**, not passing:
  concurrent occupancy/update work changed package source during the run
  (`sourceUnchanged=false`). These edits were preserved. No full EditMode suite
  or two-live-Unity-project end-to-end run was performed.
- The real current-user index contains the canonical project at HTTP `8011`,
  WS `8765`, with no pending ports. The project configuration was not changed.
- Window inspection matched exactly `CodingRiver.UPilot.UPilotPortRegistryWindow`,
  instance `568105589213748610`. Snapshot:
  `Tests~/UPilotTest/Log/UPilotSnapshots/20260910/snapshot-ad91487d82664e0db40fe8bd5e30b257/editor-window.ee018501.color.png`.
  It is 480x552, 25248 bytes, SHA-256
  `4a57c3436a07ab8ade34977d783291b95543c3adaeebc4f26f7d73fc8cb9d89a`.
  `acceptedAsEvidence=true`, `pixelSourceVerified=true`,
  `occlusionSensitive=false`, API `Win32.PrintWindow(PW_RENDERFULLCONTENT)`,
  Unity PID `68332`, HWND `32447650`, foreground false, minimized false,
  degraded false, original error empty. The inspected UI source was unchanged
  by subsequent core-only fixes.
