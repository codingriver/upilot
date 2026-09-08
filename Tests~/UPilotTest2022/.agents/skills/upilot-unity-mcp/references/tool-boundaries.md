# Tool Boundaries

- Server registration, client injection, and successful invocation are separate capability states.
- Query capabilities before declaring a missing client-side tool unavailable.
- Read/list/find/get before destructive or persistent operations.
- Do not place destructive calls inside automatic retries.
- `unity_task_execute` retries only idempotent, non-destructive operations.
- `unity_reflection_call` is the unified public reflection entry point: `typeName` + `methodName` invokes one existing compiled method; `expression` evaluates one bounded expression. These request shapes are mutually exclusive and selected before execution.
- Reflection expression mode has no declarations, loops, branches, lambdas, async syntax, helper definitions, or dynamic compilation. It is exposed only through `unity_reflection_call(expression=...)`.
- `csharp_eval` runs UPilot's bounded C# subset for expressions or statements. It does not compile source, declare types, load assemblies, create threads, or use Unity/Roslyn evaluation APIs.
- `reflection_emit_type` consumes a structured spec and creates a temporary CLR type in a persistent execution session. It does not accept raw IL, save assemblies, or create persistent MonoBehaviour/ScriptableObject assets.
- `execution_session` owns cross-call variables, object/type/delegate handles, callbacks, and cleanup. A close is terminal; dynamic type memory is reclaimed only by Domain Reload.
- Mouse, keyboard, and drag tools affect the real focused UI and are layout-sensitive.
- `unity_snapshot_capture` and legacy screenshot wrappers generate project-relative artifacts and are non-idempotent. `unity_snapshot_baseline_compare` is observational but writes non-evidence diff/heatmap diagnostics.
- `unity_snapshot_baseline_update` is the only managed-baseline write path and always requires dry-run inspection plus the matching current-state confirm token; never retry or approve it automatically.
- Manual Unity YAML editing is a last resort and must preserve GUID/fileID integrity.
- Persistent jobs initially cover only tests and package acceptance. They store identities, deadlines and report references in the existing project SQLite database; uncertain starts are never replayed.
- `RecoveryRequired` and `cancel_requested` are non-terminal. A stopped Python observer does not prove the Unity business operation stopped.
- Prefab patch v1 is a guarded single-file commit with backup and conditional restoration, not a global transaction. External changes prevent automatic restoration. Enum names only; arrays may only retain their structure.
- Scene-summary traversal is bounded. Root enumeration and individual native calls cannot be preempted; reported elapsed time and partial coverage are authoritative for the work actually observed.
