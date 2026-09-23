# Automation Steps

Use this reference when implementing a registered step or composing a step plan.
Step execution is Editor-only and belongs to the existing Operation workflow.
It does not require UPilot Flow and does not create public automation_start/status tools.

## Ownership and Discovery

- The project owns business step implementations, case assertions, business recovery
  and log rules. Skills compose approved fixed templates and selected Cases. Do not
  infer a login/GM/business sequence from generic step names.
- UPilot owns neutral sequencing, deadlines, polling, checkpoints, Finally processing,
  fixed-range Console evidence and report finalization.
- A registered class explicitly uses `[AutomationStep("stable.id")]` and implements
  `IAutomationStep`; inheriting `AutomationStepBase` is recommended, not mandatory.
  Attributes are not inherited. Classes must be concrete, non-generic and have a public
  parameterless constructor. Constructors and Validate must have no side effects.
- Project adapters still reference the package and must use `#if UPILOT` when optional.
  Business/runtime classes do not need to reference UPilot.
- Discover through the compiled `UPilotAutomationStepService.CatalogJson()` facade.
  UPilot keeps one type/factory registry snapshot per assembly generation, shared by
  Catalog, Validate and Start; it does not pool mutable Step instances. The project
  Bridge is not a required entry for new Step plans. Use capability discovery before
  a reflection fallback. Do not invent IDs from class names or text descriptions.
- Internal Bridge routes are `automation.steps.catalog/validate/start/state/cancel/artifacts`;
  these are implementation routes, not a second public MCP surface.

## Authoring A Step

### Decide Ownership Before Coding

1. Inspect the current registry and built-ins before adding an ID. A new combination
   of existing actions normally needs a Skill plan change, not another Step.
2. Put business actions, readiness barriers, assertions, configuration recovery and
   business observations in the project's existing Editor Step directory. Reuse its
   business base/helpers when appropriate; runtime types never reference those adapters.
3. A package Step must work without importing a host namespace, configuration table,
   fixed scene, hero/account identity or project log rule. Reuse existing mode, Capture,
   Snapshot and report services; do not create a second implementation of their lifecycle.
4. Record the ID/type/file, description, argument grammar and example, static validation,
   runtime prerequisites, completion conditions, stable error codes, mutations/owners,
   cancellation/cleanup/recovery, timeout/poll budget and intended Normal/Finally position.
   Reserve `upilot.*` for package built-ins; use a project namespace for business IDs.
   This is authoring guidance, not extra mandatory DTO fields or an ID regex.

### Implementation Checklist

- Read `Editor/Automation/AutomationStepAttribute.cs`, `IAutomationStep.cs` and
  `AutomationStepBase.cs` in the actual installed package source. Use unversioned names.
  Specify the attribute on each concrete class; inherited attributes do not register.
  Do not write to a registry or call service Start from a Step.
- Use one strict parser for Validate and Execute; reject unknown/invalid values according
  to the documented grammar. Omitted outer arguments become `""`; explicit null and
  non-string values fail before the Step. Use an existing JSON serializer for structured
  string arguments and checkpoints, never interpolate user text into JSON.
- Validate syntax and static plan constraints without opening scenes, acquiring resources,
  writing files/configuration or requiring a future runtime object. Do not rely on fields
  assigned by Validate: execution receives a different instance.
- Execute performs one bounded start; Poll checks actual progress. Do not block/sleep,
  use async void, subscribe an independent update scheduler, or reissue an accepted action.
  An asynchronous callback may record completion locally, but saves must occur in a
  writable main-thread lifecycle callback. Re-read current contextJson each callback.

| Method | Base default | Override when |
| --- | --- | --- |
| Validate | Valid | Arguments/static combinations need checks; runId is empty |
| Execute | Abstract | Always; initiate action or perform a synchronous assertion |
| Poll | Cached Running/result | Waiting for asynchronous business completion |
| GetError | Cached message/diagnostic | Human detail needs custom readonly lookup |
| Cancel | No operation | Owned work must be asked to stop; preserve base evidence behavior |
| Cleanup | Succeeded | Any resource, subscription or temporary state needs release/verification |
| Restore | Unsupported | Checkpoints and actual identity can prove safe same-process resumption |

`Fail` plus inherited GetError usually suffices; projects do not need an error DTO.
Return Running from Cleanup until release is confirmed. It runs after success too:
state intentionally passed to later items needs explicit run-scoped ownership and a
Finally restoration Step, not accidental survival of local Cleanup. If overriding a
project business base's Cancel/Cleanup, inspect and preserve its evidence/cleanup duties.
Restore must rebuild local observation state and return Restored, not call Execute or
declare success from a stored runId. Resource-free read-only steps can deliberately
remain Unsupported if interrupted; document that limitation.

### Minimal Read-only Example

This is an authoring example, not a new built-in or a file to install automatically.
It checks the active scene at execution time; it does not open it. It owns no resources,
uses default cleanup, and intentionally does not support reload recovery. Replace the
example ID and assertion with an approved project requirement before adoption.

```csharp
#if UNITY_EDITOR && UPILOT
using System;
using CodingRiver.UPilot.Automation;
using UnityEngine.SceneManagement;

namespace Project.Editor.Automation
{
    [AutomationStep("example.assert_active_scene",
        Description = "Check the active scene path without changing it",
        ArgumentsExample = "Assets/Scenes/Launch.unity",
        TimeoutSeconds = 10, PollIntervalSeconds = 0.1)]
    public sealed class AssertActiveSceneStep : AutomationStepBase
    {
        private static bool TryParse(string arguments, out string path)
        {
            path = arguments;
            return !string.IsNullOrEmpty(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && path.EndsWith(".unity", StringComparison.Ordinal)
                && path.IndexOf('\\') < 0
                && Array.IndexOf(path.Split('/'), "..") < 0;
        }

        public override string Validate(string runId, string instanceId,
            string contextJson, string arguments)
        {
            return TryParse(arguments, out _)
                ? ValidationOk()
                : ValidationError("EXAMPLE_ARGUMENT_INVALID", "Expected an Assets scene path.");
        }

        public override void Execute(string runId, string instanceId,
            string contextJson, string arguments)
        {
            if (!TryParse(arguments, out var path))
            {
                Fail("EXAMPLE_ARGUMENT_INVALID", "Expected an Assets scene path.");
                return;
            }
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.path != path)
            {
                Fail("EXAMPLE_SCENE_MISMATCH", "Expected active scene: " + path);
                return;
            }
            Succeed();
        }
    }
}
#endif
```

For a waiting Step, leave the result Running after Execute, then in Poll inspect the
owned request/current business state, call Succeed or Fail only on an observed outcome,
and return `base.Poll(runId, instanceId, contextJson, arguments)`. Never read future
business readiness in Validate. A business observation window can use its own timestamp;
the executor still owns polling, the overall timeout and cancellation.

### Optional Package And Discovery

Guard the entire optional project file with `UNITY_EDITOR && UPILOT`, including usings,
attribute, base/interface and helpers. Do not add a runtime reverse dependency, new
asmdef/Provider or reflection compatibility layer just to make the dependency optional.
UPilot's define manager adds `UPILOT` for the active build target and tracks targets it
wrote; standard uninstall removes owned recorded defines. A preexisting/manual define
may not be owned, and direct directory deletion can bypass cleanup. No package plus
no define excludes the adapter; no package plus a leftover define can fail compilation.
Inspect and manually remove stale local symbols when necessary, preserving other defines.
Do not commit local package paths or generated PlayerSettings changes for teammates.

After the correlated Unity compile, query UPilot Catalog, check the exact ID, type,
metadata and diagnostics, then validate the **whole** proposed plan. A file on disk,
an attribute text search or a catalog entry with diagnostics is not usable registration.
If absent, inspect compile evidence, current target defines, Editor assembly inclusion,
attribute placement, public no-argument constructor, interface and duplicate IDs.
Do not fix absence by adding another registry, recompiling unchanged code blindly,
manually constructing a Step, or bypassing preflight. Missing/invalid registrations
must be repaired before starting any plan.

### Skill Integration And Acceptance

Update the owning business Skill's selection, argument example, prerequisites, ordering,
effects/compatibility and restoration placement. Discovery only makes a Step selectable;
it does not automatically include it in default acceptance or prove other Steps compatible.
Repeated uses have distinct instanceIds. ID removal/rename requires updating current
templates and callers together; leave historical report identities unchanged.

Check the smallest sufficient set for the changed behavior: invalid/missing arguments
and registration must execute nothing; valid inputs must retain their exact string;
check success, meaningful failure codes, cancellation/timeout and verified cleanup.
Resourceful steps additionally need save-failure-before-effect, wrong/stale identity,
repeat cleanup and supported/unsupported reload paths. Configuration restoration needs
actual post-exit values, including nonzero originals and consecutive runs when relevant,
not just a saved `restored=true`. Distinguish synthetic restore tests from real Domain Reload.
Require relevant artifact integrity, final mode and no unresolved owned Capture/Snapshot.

Use canonical package tests for generic changes and the authorized project fixed template
for business changes. No empty-filter full suite or temporary parallel project runner.
Actually test an isolated no-package project when claiming package-absent compilation;
macro scans alone do not prove it. Never uninstall a team's active tooling as a casual test.
Report changed IDs/files, updated templates, compile/run identities, observed outcomes
and unexecuted coverage. Instructions/examples-only edits use instruction validation and
synchronization, without compiling the Markdown example or launching business work.

## String Contract and Checkpoints

Ordinary project Steps reference only the attribute and interface/base class. All
Automation API type/method/script names are unversioned; JSON version/apiVersion,
persisted fields and enum values remain data contracts. Do not rename unrelated
execution engines, third-party APIs or historical evidence.

```csharp
public interface IAutomationStep
{
    string Validate(string runId, string instanceId, string contextJson, string arguments);
    void Execute(string runId, string instanceId, string contextJson, string arguments);
    string Poll(string runId, string instanceId, string contextJson, string arguments);
    string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments);
    void Cancel(string runId, string instanceId, string contextJson, string arguments);
    string Cleanup(string runId, string instanceId, string contextJson, string arguments);
    string Restore(string runId, string instanceId, string contextJson, string arguments);
}
```

- Every callback receives arguments last, including Poll/Cleanup/Restore.
  Validate receives runId="", the item's instanceId, and contextJson containing
  checkpoint/shared empty objects plus the complete readonly plan. Start revalidates;
  execution uses fresh instances, not the ones used for validation.
- Running contextJson contains only `checkpoint` (current item) and `shared` (run)
  objects. Each callback gets a new snapshot. Local JSON mutation and returned fields
  never save state implicitly.
- Base helpers are `Succeed(bool warnings=false)`, `Fail(code,message,diagnostic="")`,
  `Skip()`, `IsRunning`, `ValidationOk()`, `ValidationError(code,message)` and
  `ResultJson(status,errorCode="")`. Use these instead of interpolating dynamic JSON.
- Validate returns `{"ok":true}` or `{"ok":false,"diagnostics":[{"code":"ARG_INVALID","message":"Reason"}]}`.
  Diagnostic severity defaults to error; ok and error diagnostics must agree.
- Poll returns `{"status":"Running|Succeeded|SucceededWithWarnings|Skipped|Failed|Canceled"}`.
  Failed/Canceled require a nonempty errorCode; TimedOut is executor-only.
  GetError returns message and optional diagnostic, never a replacement machine code.
- Cleanup allows only Running/Succeeded/SucceededWithWarnings/Failed.
  Restore returns Restored/Unsupported or Failed with errorCode; Restored resumes the
  original executing or cleaning stage, not immediate success. Base Restore is Unsupported.
- Empty/malformed JSON, extra root values, duplicate properties, wrong field types and
  unknown states fail closed, never through a deserializer default.
- `SaveCheckpoint(runId,instanceId,checkpointJson)` replaces the item's JSON object.
  `SaveSharedValue(runId,instanceId,key,valueJson)` replaces only one shared key;
  use a project namespace such as `ksb.logging`. Values may be any legal JSON.
  Both methods are base helpers and public service methods for direct interface implementations.
- Saving requires the active run/item and a writable lifecycle callback on the Editor
  main thread. Validate/GetError and escaping asynchronous callbacks cannot save.
  Save returns only after durable persistence. Do not catch a save failure and continue
  the protected side effect; the executor also latches failure and rejects progress.
- Persist original configuration before modifying it. Finally restores that saved value,
  tolerates an unstarted configuration step, and fails instead of inventing missing originals.

The service exposes JSON-string `CatalogJson()`, `ValidateJson(planJson)`,
`StartJson(operationId,captureSessionId,planJson)`, `StateJson(runId)`,
`CancelJson(runId)` and `ArtifactsJson(runId)`. Internal Bridge responses remain
structured objects, not JSON strings inside another JSON envelope.

## Plan and Arguments

Submit `jobSpec.stepPlan` to `unity_operation_validate`, then start once with
`unity_operation_start`. Do not also supply handwritten startCall/statusCall/cancelCall.
Keep operationId and observe it with status/wait until authoritative completion.

```json
{
  "displayName": "Editor step validation",
  "consoleCapture": {"enabled": true},
  "cleanup": {"requireEditMode": true, "timeoutSec": 30},
  "stepPlan": {
    "version": 1,
    "steps": [
      {"instanceId": "play", "stepId": "upilot.enter_play_mode", "arguments": "Assets/Scenes/Launch.unity"},
      {"instanceId": "wait", "stepId": "upilot.wait_seconds", "arguments": "0.5"},
      {"instanceId": "edit", "stepId": "upilot.enter_edit_mode", "arguments": "", "phase": "Finally"}
    ]
  }
}
```

Replace the scene with a verified, saved project scene. Business steps/cases go between
startup and Finally according to a project-approved template.

Each item needs a unique instanceId and a discovered, case-sensitive stepId.
There is exactly one business argument string; omission means empty string.
Objects, numbers, booleans and null are rejected, not coerced. A step may parse JSON
inside that string, but the executor passes the original text, including whitespace
and placeholder-looking text, unchanged.

Framework metadata remains numeric: timeoutSeconds (default 30), pollIntervalSeconds
(default 0.1; 0 means each Editor update), cleanupTimeoutSeconds (default 10).
Attribute defaults may differ. Finally items form a contiguous suffix and execute
in listed order. allowSkipped defaults false; an allowed skip remains Skipped,
not Passed, and the run has at least SucceededWithWarnings.

Validation aggregates registration, missing ID, contract, parameter and plan errors.
It does not create Capture, enter PlayMode or call Execute. Validate checks syntax and
static constraints, not whether a future login panel or runtime object already exists.
Operation timeout must cover the summed step/cleanup budgets plus the evidence margin;
do not shorten it to a single tool wait window.

## Lifecycle and Recovery

- Execute starts once and returns quickly; Poll returns Running or a terminal result.
  Never block Unity's main thread waiting for async completion.
- Failures use stable error codes. GetError provides human text and diagnostics;
  a GetError exception must not replace the original failure code.
- Cleanup is polled after every executed step, including successful steps.
  Failure/cancel/timeout freezes the first error, requests Cancel, cleans up, skips
  remaining Normal items and runs all Finally items. Cleanup is not automatic rollback.
- Put project restoration and explicit enter_edit_mode in Finally when the workflow
  needs them. UPilot does not guess recovery actions from a failed business step.
- A cleanup failure preserves first-error evidence, continues Finally, and leaves
  RecoveryRequired for unresolved resources. Do not report it as finished or delete
  the store to permit another run.
- State queries do not call step code. One plan at a time owns the Unity executor.
- Same-process Domain Reload uses durable intent/checkpoints and explicit Restore,
  never another Execute. Base Restore is Unsupported. Reconstruct state only when
  identity and actual side effects are provable.
- Server reconnect resumes observation, not start. Editor restart does not auto-resume.
  Store: `Library/UPilot/step-run.json`; reports: `Log/UPilotSteps/<runId>/`.
- Synchronous main-thread hangs cannot be preempted by a step deadline; use existing
  Hang diagnostics, not repeated starts.

## Built-in Steps

| ID | String argument | Behavior |
| --- | --- | --- |
| upilot.open_scene | Existing Assets/.../*.unity | EditMode only; open and confirm scene |
| upilot.enter_play_mode | Empty or scene path | Optional scene first, then await actual PlayMode and domain recovery |
| upilot.enter_edit_mode | Empty or scene path | Await actual exit, optionally open return scene afterward |
| upilot.wait_seconds | Nonnegative finite invariant-culture seconds | Nonblocking deadline-based wait |
| upilot.console_capture_start | Empty | First Normal item, once per run; transfer Capture to executor |
| upilot.capture_snapshot | Empty or Snapshot options JSON string | Trusted Snapshot, bounded observation/cancel and original file verification |

Specifying a PlayMode scene requires starting in EditMode. Do not implicitly restart
an existing PlayMode session or overwrite a conflicting playModeStartScene.
Dirty scenes block by default; resolve them through the existing explicit authorization
workflow before starting. A successful setter call is not a successful mode transition.

## Evidence

For new self-contained plans, put `upilot.console_capture_start` first and once,
with empty arguments, and explicitly set Operation `consoleCapture.enabled=false`.
Do not wrap that plan in another Capture. Its Step Cleanup does not stop the run's
Capture. UPilot persists private credentials and start/stop intents separately
from public state; same-process recovery observes exact identity without replay.
After all Finally items, it stops and verifies manifest/summary/ordered raw segments,
then reads the final fixed Console interval, evaluates Policy and freezes the report.
Stop confirmation defaults to 10 seconds, collection to 30 seconds.
Unknown ownership, changed files or unconfirmed release cannot be reported as success.

The borrowed-Capture path remains supported for other plans without that Step:
explicitly enable Operation Console Capture.
The executor borrows the exact sessionId, never its owner token and never its stop right.
After all Finally steps it freezes the final sequence boundary, waits for disk,
reads bounded pages over that fixed range, applies project logPolicy and freezes reports.
Operation stops its Capture only after this terminal boundary.

### Run-Owned Snapshot

`upilot.capture_snapshot` defaults to trusted GameView 1280x720, a three-second
wait and two-second cancellation grace. Options are a string containing
`{"capture":{...},"timeoutSeconds":3,"cancelGraceSeconds":2,"required":true}`.
Omitted fields use defaults; user requestKey/outputDirectory and unverified,
stale, fallback or occlusion-sensitive pixel policies are rejected.
Each run/item/evidence key has a durable request intent and original Snapshot ID;
reload does not start another job. Artifacts must remain in the owned directory
and match the original size/hash. Resource completion is executor-owned even
when a business Step never polls it. Failed diagnostics do not replace first error.

For failure-site evidence, ordinary Steps use base helpers
`BeginSnapshotJson(runId,instanceId,evidenceKey,arguments)`,
`PollSnapshotJson(runId,instanceId,evidenceKey)` and
`CancelSnapshotJson(runId,instanceId,evidenceKey)` inside writable callbacks.
They return Step-result JSON. `SnapshotErrorJson` is readonly and may be used
from GetError. `required=false` permits a confirmed failed capture to produce a
warning, but unknown ownership/release still requires recovery. No Snapshot DTO
or project observer is required. The package owns evidence validity; business
assertions decide what the image proves.

The default policy blocks Error/Exception/Assert. A nonempty stepPlan.logPolicy requires
Capture; it cannot be silently bypassed. Without Capture, logs are not accepted as
validated evidence and the report has no log summary. Missing pages, identity mismatch,
dropped records, read/write errors and evidence deadlines fail completeness.

Preserve event/summary bytes and SHA256 and the raw Capture reference. No business
success may be inferred only from the existence of a report. Respect active unknown
Capture ownership and all existing package-acceptance cleanup rules.

### Project Attachments

Use `RegisterArtifact(runId,instanceId,kind,path)` on the base class or public service
after a project file is complete. Registration is synchronous and follows checkpoint
write authorization: the current run/item, Editor main thread, and a writable lifecycle
callback. Validate/GetError, asynchronous callbacks and terminal runs cannot register.
Finally may register restoration evidence after an earlier cleanup failure while the
executor is still running; this does not resolve RecoveryRequired.

Files must exist inside the project, without symlink/junction traversal. Registration
captures size and SHA256 and saves ownership durably; identical same-owner registration
is idempotent. Changed content or conflicting ownership fails. Use immutable per-item
paths, register actual Snapshot files and manifests rather than only their wrapper,
and do not register the executor's own events, summary, console-policy, report.txt
or timing.csv files.
Files are rechecked before freeze; tampering/missing files fails evidence without
replacing an earlier business error. A registration/save exception must not be swallowed
to continue protected effects. No report DTO is needed by ordinary Steps.

`ArtifactsJson` and Operation status expose explicitly typed `attachments` alongside
events/summary. Use `unity_operation_collect_artifacts` to verify declared file bytes and
hashes; an older running Server may not yet collect attachment arrays. Local indexes
and successfully compiled Bridge code do not prove public collection is deployed.

### Policy Diagnostics

The frozen report's `logSummary` and Step state's `domain.logSummary` contain the exact
Capture/range, validity/completeness, counts and up to 10 blocking samples. Each sample
includes sequence, instance/phase, rule, disposition, reason, fingerprint, a message
limited to 2048 characters, and whether a stack exists. Labels are limited to 256
characters. Inspect `omittedBlockingCount` and sample `textTruncated`; diagnostics
also cap at 10 with `omittedDiagnosticCount` and `diagnosticsTextTruncated`.

`console-policy.json` is the immutable hashed `consolePolicy` attachment with all
classifications, original messages/stacks, fingerprints and diagnostics. It is written
after Finally over the fixed Capture range; status queries never reread it or advance
Steps. Fetch this artifact when bounded samples are insufficient. Policy failure
preserves the first business error and remains visible as a secondary error.
Absent Capture has no validated log evidence, and truncated samples never imply that
the underlying Capture lost records or that unshown records were allowed.

### Final Text and Timing

New completed reports have data field `exportVersion=1`. UPilot derives `report.txt`
and `timing.csv` from the same frozen summary, writes them before committing
`summary.json`, then exposes them as hashed `report`/`timing` attachments. Timing
separates execution and cleanup; missing/unstarted timestamps stay blank, not zero.
Text reports retain the final outcome and Console decision even when Finally succeeds.
Business metrics stay in project attachments; the package does not interpret them.

Finalizing persists the exact report intent before file writes. Same-process recovery
commits that snapshot, without replaying Steps or choosing a new terminal time. It
revalidates frozen local attachment hashes; new missing/changed evidence requires
recovery and cannot replace the first error or rewrite the frozen report. Existing
export bytes must match the frozen summary and are never silently repaired.
Historical `exportVersion=0` reports remain readable without backfilled exports.
Report commit proves neither Capture shutdown nor Operation cleanup; verify those
separately through the original Operation.

## Deployment and Verification

Check actual project identity and deployment freshness before use. A running Server
that still requires startCall/statusCall has not loaded the stepPlan integration.
Do not silently rebuild business workflows with hand-authored calls as a production
fallback. For package development, an explicitly bounded internal-route acceptance
can verify the Unity executor but is not proof that public stepPlan deployment is live.
Refresh only with authorized maintenance and verified in-flight work; never replay
an uncertain original start.

Run targeted contract/executor/built-in/evidence tests in Tests~/UPilotTest. Verify real
PlayMode/Domain Reload separately from synthetic Restore tests, preserve run identity,
confirm final EditMode and list remaining active captures. Do not use external client
projects or a full suite as the default package acceptance.
