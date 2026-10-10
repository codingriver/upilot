# Local port reference registration

## Authoritative configuration and availability

The actual project configuration remains `<project>/.upilot/config.json`.
Configuration validation, atomic writes, bind probes and observed service listeners
remain authoritative. Reference registration never reserves a socket, proves
ownership, rejects a port pair or excludes a recommendation.

Saving unrelated settings preserves the current on-disk port pair. Explicit
endpoint changes validate both ports; invalid ports, actual unavailability and
configuration-write failures retain their existing error behavior. Atomic config
replacement never deletes the destination first; failed candidate files remain
at the `.tmp-*` paths reported in the error log.

## EditorPrefs reference list

The global current-user EditorPrefs key is `upilot_allocate_ports`. Its string
value contains one record per line, separated by `\n`:

```text
projectPathHash:wsPort:httpPort
```

The project identity reuses the existing project-preference suffix algorithm:
normalize path separators to `/`, remove trailing separators, lowercase the path,
and encode the first eight bytes of SHA256 as sixteen lowercase hexadecimal
characters. Lowercasing is for hashing only, not filesystem access.

Successful configuration saving and existing startup checks register the current
project. Each update reads EditorPrefs again, replaces that project's record,
removes its duplicate records and preserves other records. An unchanged value is
not written again. UI redraws, port checks, health queries and heartbeats do not
register projects.

There are no file locks, background maintenance, automatic retries or expiry.
Concurrent Unity processes can overwrite each other's read-modify-write updates;
missing, stale or lost records are accepted because this list is reference data
only. Read/write/delete exceptions produce warnings, never modal dialogs or
startup/configuration-save failure. Consecutive identical registration warnings
are suppressed and kept separate from real configuration errors.

## Print and clear

Advanced Settings and the setup wizard provide two buttons:

- **打印端口登记**: prints the key and its value as one multiline Console message,
  one record per line; a missing or empty key prints `暂无端口登记`.
- **清除端口登记**: calls `EditorPrefs.DeleteKey("upilot_allocate_ports")` without
  a confirmation dialog. It does not change project configuration, configured
  ports or running services. A later normal save/startup can register again.

The global key is not part of resetting the current project's preferences.
The former management window, import, per-project release, lock waiting,
pending-state, timestamp and configuration-hash registry features are removed.

## No migration or compatibility

The implementation neither reads nor writes the old registration files or old
key. It has no migration, fallback, dual-write or old-format parsing. Old data is
not automatically deleted and no longer participates in runtime behavior.
Shared Server installation/cache locks are unrelated and remain unchanged.

## Targeted validation

`UPilotPortRegistryTests` covers isolated EditorPrefs keys, registration updates,
reference-only failures, print/clear behavior, config authority, real config
write failures and port allocator regressions. `UPilotSetupRecoveryTests` covers
actual port availability and setup recovery without reference-data UI side
effects. Temporary test keys are deleted in teardown; tests do not clear the
real global registration key. No migration or old-format cases are added.

### EditorPrefs acceptance (2026-10-10)

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
