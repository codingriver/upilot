# Minimal-Change Reliability Delivery

Completed scoped implementation: 2026-09-07. Canonical project: `Tests~/UPilotTest`.
This checklist tracks the approved implementation, not a release certification.
E1-E3 preserve the earlier reliability delivery; E4 records the current
three-integration-fix follow-up and its separately verified source identity.
Pre-existing worktree changes must be preserved. No full EditMode suite, release,
version update, commit, external-project validation, or automatic Unity restart.

## Scope And Evidence

| ID | Deliverable and value | Boundary | Status / evidence |
| --- | --- | --- | --- |
| R1 | Fail-closed package acceptance | Existing test service; correlated run, verified compile, successful cleanup | Complete; E1/E2, failure/missing-evidence Python cases |
| R2 | Observation does not change test logs | Ordinary COMMAND/NETWORK traffic stays off Console during tests; diagnostics remain on disk | Complete; E1 active-run logging fixture |
| R3 | Lightweight source-bound release gate | Focused Python contracts and Skill check, existing workflow ownership | Complete locally; E2; remote execution not performed |
| T1 | Exact test method/fixture arrays | Extend list/run; one discovery tree, union/dedup, per-selector counts, no mixed legacy filter or accidental full run | Complete; E1 six selector/logging cases and E2 |
| P1 | Safe single-prefab component patch | One new tool; ordinary non-nested prefab, exact target, supported existing fields, enum names only | Complete; E1 four prefab cases |
| P2 | Prefab confirmation and persisted verification | Asset/meta hashes, dry-run/token, commit recheck, isolated candidate, backup, conditional recovery | Complete; E1 persisted reload, stale token, invalid field/enum, callback-induced failure restoration |
| A1 | Background package acceptance | Existing task start/status; no retries, runGuid association, server-alive completion and summary | Complete; E1 background completion and E2 |
| A2 | Truthful cancellation | Forward test cancel; observe cleanup before terminal; unsupported generic cancellation is not business success | Complete; E3 live cancellation and E2 generic cancellation contract |
| S1 | Bounded read-only scene summary | One new tool; traversal node/time budgets, counts, examples and coverage; no persistent index | Complete; E1 two scene cases and real scene query |
| D1 | Limited persistent recovery | Existing project SQLite; tests/acceptance only, identity/deadline/artifacts; never replay start | Complete; E2 project isolation and E3 live server recovery |
| D2 | Recovery uncertainty is explicit | Missing/foreign identities yield observation or RecoveryRequired | Complete; E2 simulated interrupted/unknown recovery; E3 established live identity |
| Q1 | Coherent tool/Skill contracts | Generated registry/schema evidence, managed Skill sync, at most two new public tools | Earlier scope: E2; the four recorded proxy gaps are closed by E4 |
| Q2 | Targeted verification and handoff | Focused Python and canonical Unity tests, current batch compile evidence, updated root backlog | Complete; E1/E2/E3; limitations below |

## Delivery Order

1. R1/R2/R3 and T1: trustworthy and cheaper targeted verification.
2. P1/P2 and A1/A2: safe prefab persistence and background acceptance.
3. S1, D1/D2, Q1/Q2: bounded context, recoverable identity, complete evidence.

Per-client authenticated permissions, all-operation migration, cross-platform Unity
CI, prefab variant/model/nested override editing, array resizing, a new test runner,
and semantic scene indexes remain deferred. Memory tasks are not durable jobs.

## Change Boundaries And Value

| Workstream | Implementation boundary | Value and deliberate limit |
| --- | --- | --- |
| Trusted acceptance | Existing Python `domain/test_service.py` and C# `Logger.cs` | Reject false passes and observation-induced failures; reuse verified compilation instead of compiling unchanged code twice |
| Focused tests | `UPilotTestService`, existing test facade/wrappers, selector fixtures | One runGuid for an exact union; no new runner or new public test tool |
| Prefab persistence | One `UPilotPrefabPatchService`, existing serialized-property utility, resource facade/wrapper | Replace fragile multi-call save sequences; v1 only edits supported values on one ordinary prefab component |
| Background recovery | Existing `task_service`, `StateStore.test_jobs`, a small context helper | Survive client disconnect and server restart, preserve cancellation/cleanup truth; no generic-operation migration |
| Scene context | One `UPilotSceneSummaryService`, existing resource route/facade/wrapper | Bounded read-only counts before full hierarchy queries; no persistent semantic index |
| Quality evidence | One quality script, source identity helper, existing release workflows plus PR workflow | Locked dependencies, focused checks, generated schemas and hashes; no version ownership change or release dispatch |

Only `unity_prefab_patch` and `unity_scene_summary` are new public tools.
The live registry has 198 entries, up from the pre-change local 196.
Generated inventory distinguishes registration, current-config exposure and proxy
handler availability; it is not a claim that every tool was live-tested.
The test MonoBehaviour lives in `Tests/Fixtures/UPilot.TestFixtures.asmdef` because
an Editor-only assembly could not provide an attachable serialized prefab probe.
All package `Tests` sources, including that fixture, participate in source identity.

## Verification Evidence

### E1: Canonical Targeted Unity Acceptance

- Unity 6000.6.0a2, Windows, project `Tests~/UPilotTest`, Unity PID 26060.
- Last C# batch: `wb-68a2a317-b703-4b1b-99da-b21d1cd98e26`, created at `1788755740262`.
- Compile operation: `3d8027f54dd34d3da0ba3df69095b0b1`; verified at `1788755764178`; 0 errors, 0 warnings.
- Acceptance reused that verified compilation (`reusedVerifiedCompile=true`); no new C# writes followed it.
- Task: `task-a5d13934-7e31-43ff-81d7-56976badab90`; runGuid: `b765d561-fbb8-4610-876c-5c51dfdafd82`.
- Fixtures: `UPilotTestSelectionTests` (6), `UPilotPrefabPatchTests` (4), `UPilotSceneSummaryTests` (2), all under `CodingRiver.UPilot.Tests`.
- Result: 12 passed, 0 failed, 0 skipped; authoritative run, cleanup and unchanged source verified.
- Summary: `Tests~/UPilotTest/Log/UPilotAcceptance/1788756368465_req-03cfb20d-c8f7-4263-b35f-0be89556b9e3/summary.json`.
- Summary size: 77283 bytes; SHA256 `18019b3445cea7a533f2ee0dcc047fb9e21c1c2ea54530bfea3db4b7da49fe10`.

### E2: Source-Bound Local Gate

- `check_release_quality.py --unity-summary <E1 summary>` passed.
- 73 focused Python tests passed; Skill validation passed; generated registry/MCP-schema inventory passed.
- Nine persistent-job tests include background completion, recovery without start replay, unknown identity, project isolation and cleanup-aware cancellation.
- Checked source: commit `a6ec4d4a1d2fee5c72989209ced452fe9c679a84` plus uncommitted content hash `6dba1de315367c8dd83b10339e3e51aae51f03f7980051c95e39a6fa3f70cd69`; 564 files, scope `package-code-tests-skills-workflows-v1`.
- Report: `artifacts/reliability-quality/quality.json`, 2177 bytes, SHA256 `9b68ed8e58c10a8d1c9b2a72972e42c38ce0f3a75ac4390b51e53237f1117e4f`.
- Inventory: `artifacts/reliability-quality/tools.json`, 314286 bytes, SHA256 `cd859128649f3e38a64bcdc63145176f705788e308388b58544bba1dd0e083ca`.
- Two existing Python websockets deprecation warnings remain; no dependency migration in this scope.
- Managed Skill source/installed copies synchronized at template 28. Both `.agents` and `.claude` copies match all 14 non-meta source files byte-for-byte. `git diff --check` passed.

### E3: Live Server Recovery And Underlying Cancellation

- Existing explicit waiting test: `CodingRiver.UPilot.Tests.UPilotCoreTests.OperationWaitWindowContractLongRun`.
- Task: `task-03e25bd1-589a-43bb-abac-9e9263e33687`; runGuid: `f9ef3406-7502-47e3-8fc1-034ae368694e`.
- Restarted only MCP Server after establishing runGuid. Original task returned `recovered=true` with the same runGuid; Unity PID remained 26060.
- Cancel returned `cancel_requested/terminal=false`; final state became `cancelled` only after authoritative `aborted` and successful cleanup.
- This deliberately cancelled run is not passing package acceptance (`acceptancePassed=false`).
- Summary: `Tests~/UPilotTest/Log/UPilotAcceptance/1788756263011_req-f67cf09f-856e-49b2-8e77-ebfdf63cdf52/summary.json`.
- Summary size: 71673 bytes; SHA256 `4190c469cb02f099fba8c51c032938f5efc8edb74e2ff99254901e5f9ffa27c3`.

### E4: Three Integration Fixes

Completed 2026-09-07. No new public tool, dependency, package version, client
configuration, Flow engine, or compile workflow was introduced by this follow-up.
Package/Python versions remain `0.3.28`; only the Skill template advanced 28 -> 29.

| Fix | Smallest implementation boundary | Value |
| --- | --- | --- |
| Native/proxy parity | Existing compile Registry alias, Flow wrappers and `TestDomainService`; quality inventory rejects missing handlers | The same four entrypoints work through native, generic proxy and Task dispatch; Flow starts reject automatic retries |
| Deleted/moved compile inputs | Existing write-batch wrapper/service and nullable SQLite `changes_json` migration | Pure deletion and moves have complete, hashed, persisted compile evidence; existing callers and legacy records remain readable |
| Source/installed Skill checks | Existing checker/installer hash helper, C# install hash exclusions and template constant | Managed copies validate without repository/meta assumptions while source checks remain strict; customized copies remain protected |

Python scope: 172 focused gate tests passed, including the three fixes and existing
direct reliability contracts. Coverage includes disabled/missing/failed Flow
starts, missing executionId, failed/aborted terminal results, bounded timeout
cancellation, unsafe Task retries, absent/deleted/conflicting/escaping paths,
old-schema migration, old/new record separation, merged full-manifest hashes,
delete/recreate ordering, deferred once-only resume and restart reads. Skill cases
cover both client layouts, missing/invalid markers, changed/missing/extra content,
cache/meta exclusions and custom ports. The two existing websockets deprecation
warnings remain. No full Python or full EditMode suite was run.

Canonical Unity acceptance:

- Unity 6000.6.0a2, Windows, PID 26060 throughout. Only the MCP Server was refreshed.
- C# template/hash batch: `wb-25fa2f79-4609-4ee6-9a22-ac98efc9b9da`; created `1788762480619`; compile `a6ddadb799be4d78bf0b335d5a56f035`, verified `1788762505498`, 0 errors/0 warnings.
- Three exact `UPilotCoreTests` methods: `AgentRulesAutoSetupKeyTracksRulesAndSkillTemplateVersions`, `SkillInstallMetadataDetectsLocalChanges`, `SkillSourceIncludesAgentsTemplateForManagedUpdates`.
- Task `task-0cdeed2e-555e-4f29-af92-6cbefac9698d`; runGuid `4f542756-15c0-49b2-a3bf-702af9d05a56`.
- Result: 3/3 passed, 0 failed/skipped; `acceptancePassed`, `cleanupVerified`, `testIdentityVerified`, `sourceUnchanged` all true.
- Acceptance reused the verified final probe-deletion compile; it did not trigger another compile.
- Summary: `Tests~/UPilotTest/Log/UPilotAcceptance/1788763249387_req-0a136e80-7993-445c-81c9-e28c56499903/summary.json`, 75924 bytes, SHA256 `312827e8a892692d65db262417fe1f1083702e1b20992d94527830176d0e4a06`.

Live write-batch probes used one temporary C# file in the canonical project.
The move preserved its `.meta` GUID; both file names and metadata were cleaned.
SQLite records are `verified`, retain their full change manifests, and match the
Unity terminal identities. Capture evidence records exactly one `compile.request`
per batch, three in total.

| Operation | writeBatchId | Created / verified (Server/Unity ms) | Compile operation |
| --- | --- | --- | --- |
| Create | `wb-746ef4c7-42cc-444b-a2be-69b070b7a3ca` | `1788762855573` / `1788762866070` | `e0c28aedf8ad4738817df8f722cbc161` |
| Move | `wb-b7aa0ebf-2b39-4977-8919-6f4e21004376` | `1788762926685` / `1788762936914` | `d1e0d47a3d9d49e098b35b162b70108f` |
| Delete, `paths=[]` | `wb-e4dec69d-bcf4-454d-90ed-116acb368aa9` | `1788763018957` / `1788763029160` | `9515452de2a74e478b28053097d6339a` |

Live routing/installation evidence:

- Refreshed HTTP `tools/list`: 198 exposed tools; Registry: 198; no new names; `proxyHandlerGaps=[]`. Registration, HTTP calls and client UI injection remain distinct states.
- `unity_compile_errors_get` native/proxy calls returned matching verified compile identities and empty errors.
- Flow was already enabled. The validated two-step `113-wait-zero-milliseconds.yaml` sample ran once per native/proxy file, suite and async entrypoint: 6/6 passed, six unique executionIds and exactly six start commands. Suite input was an isolated one-file directory, subsequently removed.
- Async starts returned in 0.031s and 0.016s, before result polling; all six runs reached `completed` with zero cleanup errors, unresolved resources or active leases.
- Both managed Skill copies updated using `UpdateAllAgentSkills(false)`, then independently passed their installed checker. Both markers: template 29, content SHA256 `8400108cf9ab77abf5640b6b9e6f29e5ee730912cabd580f65017351f2f6b73f`.
- Console capture stopped and the session list has no active captures. Final Editor state is authoritative/fresh/ready, no pending batch, 0 compile errors/warnings and no new Console errors.
- The capture has six Warning-level messages announcing successful Flow cases, not compiler warnings or failed tests. This diagnostic-noise issue and the HTTP helper's loss of schema-error details were recorded separately in root TODO; neither was expanded into this implementation.

Current artifacts:

- Source commit plus dirty content: `a6ec4d4a1d2fee5c72989209ced452fe9c679a84`, SHA256 `36759c29e2b3f4c006200d020aa15df99aa0cc630000ea7be65cd053c0a78336`, 567 files, `package-code-tests-skills-workflows-v1`.
- Source-bound gate: `artifacts/integration-fixes/quality/quality.json`, 2370 bytes, SHA256 `309736ea6690e2e8df88cc487131734c00d1c7dccc37587a95ed4a2b62a8feb1`; it checked the E4 Unity summary.
- Inventory: `artifacts/integration-fixes/quality/tools.json`, 320255 bytes, SHA256 `8540bfee9bee88bb89aba30dad9e83a22b701e606972857e1e522193f398e903`.
- Live evidence: `artifacts/integration-fixes/live-evidence.json`, 18931 bytes, SHA256 `00870e8e9ef2f2604e71e814e2a83008e0822c0d59b602f0a981d1d66372cc53`.
- Skill checks: `artifacts/integration-fixes/skill-checks.json`, 1155 bytes, SHA256 `ae25260347dc251b1753c52161d48d6d74ff2546fe076544eceeaf283354f682`.
- Capture: `Tests~/UPilotTest/Log/UPilotConsole/2026-09-07_14-33-51-302_integration-fixes/console.jsonl`, 680771 bytes, SHA256 `f2af1959e4392d8ee99b8f0311d39cb1d66cc0d2a163cfc341e3e0b8c6a9c128`.
- `artifacts/integration-fixes/manifest.json` indexes hashes of the raw responses, gate outputs, Unity summary and six Flow report directories.

The live batch probes ran in EditMode. PlayMode deferral, malformed starts/timeouts,
legacy migration and restart restoration have focused Python coverage, not a new
live fault-injection campaign. Optional features were not toggled; no external
project, remote Actions run, package version edit, commit or release occurred.

## Remaining Limits

- No full EditMode suite, Unity 2022 compatibility run, Unity process restart, active-run Domain Reload fault injection, remote Actions run or release was performed.
- A six-minute-plus disconnect scenario and cross-platform/graphics CI matrix remain unrun. E3 proves live server recovery/cancellation for one bounded wait test, not every interruption mode.
- Supplying `--unity-summary` rejects unsuccessful/stale/source-mismatched evidence. The default remote gate does not require a Unity summary and explicitly reports that skip; mandatory cross-version Unity release certification remains deferred.
- Prefab callbacks can have external side effects. Backup restoration is hash-conditional, not a transaction over user callbacks. Variant/model/nested prefabs, object references and array resizing remain unsupported.
- Scene traversal respects budgets between nodes; a single native Unity API call is not preemptible.
- The three integration limits originally identified in E2 are resolved by E4. This does not extend acceptance to all 198 tools, every Flow sample, or interruption at every compile lifecycle boundary.

## Baseline

- Live check: Unity 6000.6.0a2, correct project, connected/ready/authoritative.
- Historical compile operation: `7c7fe500b5454a99a3fddc72dde65e02`;
  `lastCompileVerifiedAt=1788614235782`. This does not verify new writes.
- Historical 516-test run is not current acceptance.
- Refresh baseline before each C# batch and register that batch immediately.
