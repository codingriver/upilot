# Per-project Server ports

The project's `.upilot/config.json` is authoritative for its HTTP and internal
WebSocket ports. The current OS user's `.upilot/ports.json` is a persistent
reservation index, coordinated through `.upilot/ports.lock`.

- Registered projects reserve both ports even while Unity and Server are stopped.
- HTTP and WS share one conflict set. New recommendations exclude all other
  registered projects' ports, pending reservations and actual listener ports.
- Opening an existing project registers its configuration. Previously unseen
  projects can also be imported through **UPilot > 端口登记管理**.
- Copying a project to another absolute path creates a separate local identity.
  If its copied ports conflict, the user must confirm a new pair.
- A project's on-disk ports win over its own index entry and over stale in-memory
  ports when saving unrelated settings. Conflicting projects are never rewritten.
- Explicit endpoint changes save the pair together and check again under the
  cross-process lock. An exhausted search is an error, not a fallback to occupied
  defaults. Automatic repair requires confirmation before changing ports.

## Failure recovery

Saving first records both the old and new port pairs, atomically writes the
project configuration, then finalizes the index. Interrupted operations retain
both pairs until that project's configuration is successfully reconciled.
Atomic replacement never deletes the destination first; failed candidate files
remain at the `.tmp-*` paths reported in the error log.

Invalid registries, unavailable user directories, lock timeouts, write failures,
port conflicts and failed release operations block the affected action, record
`[UPilotPorts]` errors with paths/context, and queue an Editor main-thread dialog.
Batch-mode runs report errors without interactive dialogs.

Reservations do not expire based on time, process exit, unavailable disks or
missing projects. Manual release requires confirmation, an unchanged index
snapshot and non-listening ports; the current project's reservation cannot be
released from its own window. Release removes only the index entry, not the
project configuration. Opening that project again may then report a conflict.

## Multiple projects and upgrades

Projects retain independent Server processes and project-local logs. Server
cache installation locks are independent of the short-lived port registry lock;
downloading or upgrading a shared EXE never holds the port registry lock.

Process ownership requires both configured ports and the exact project-specific
`--log-file` path. A legacy or manually launched process lacking that identity is
not automatically adopted or stopped.

This index coordinates participating UPilot versions under the same OS user.
Unregistered projects, old clients and other users/programs can still occupy
ports, so Server startup checks actual availability and reports conflicts.
Preflight checks do not reserve sockets against unrelated applications.

## Validation

`UPilotPortRegistryTests` covers offline and cross-protocol reservations,
concurrent commits, external-process locking, missing/corrupt configuration,
configuration authority, interrupted writes, native Windows file locks,
explicit release and main-thread error-dialog deduplication. The process
ownership regression is `McpProcessMatchingRequiresBothCurrentProjectPorts`.

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
