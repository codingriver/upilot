# Execution Tools

Load this reference only when using reflection calls, C# subset evaluation, dynamic types, or persistent handles.

## Tool and backend hierarchy

`unity_reflection_call`, `csharp_eval`, and `reflection_emit_type` are peer MCP execution
tools: call an existing method or evaluate one bounded reflection expression, execute
a temporary C# subset program, or create a temporary CLR type from a structured spec.
Shared execution infrastructure does not make one public tool a backend of another.

Within `csharp_eval`, `interpret`, `emit`, and `compiled` are execution backends;
`auto` is a selection strategy, not a fourth implementation. Eval's `emit` backend
caches a DynamicMethod entry that still executes the AST; it does not invoke
`reflection_emit_type` or create a user-defined CLR type. Dynamic type method bodies
instead select `bodyBackend=interpret|compiled` and retain their synchronous limits.

`csharp_validate` provides preflight checks and `execution_session` manages state and
lifecycle across calls. They support the execution tools rather than adding Eval
backends. Choose the public tool first, then its supported mode or backend.

## Choose the tool

| Need | Tool |
| --- | --- |
| Call one already-loaded static or instance method | `unity_reflection_call` |
| Evaluate one bounded expression through the reflection entry point | `unity_reflection_call(expression=...)` |
| Check syntax or backend support without executing target code | `csharp_validate` |
| Run locals, exceptions, closures, async lambdas, control flow, or await | `csharp_eval` |
| Create an actual temporary CLR type or interface adapter | `reflection_emit_type` |
| Keep variables, instances, types, or callbacks across calls | `execution_session` |

`csharp_validate` is read-only and idempotent. The execution routes require write access, can produce side effects, are non-idempotent, and must not be retried automatically. A failure does not imply rollback.

## Typed values

Plain JSON primitives are accepted. Use an explicit typed value when overload selection or identity matters:

```json
{"kind":"literal","typeName":"System.Int32","value":3}
{"kind":"null","typeName":"Example.IMyService"}
{"kind":"handle","handle":"h.<domain>.<kind>.<id>"}
{"kind":"type","typeName":"System.String"}
{"kind":"unityObject","instanceId":123}
{"kind":"array","typeName":"System.Int32[]","items":[1,2,3]}
```

Results include `kind`, `typeName`, `valueJson`, `handle`, `summary`, and `serializationStatus`. `requiresSession` means the value cannot outlive a call-scoped execution.

## Session lifecycle

Open before the first stateful call:

```json
{"action":"open","title":"temporary adapter","ttlSec":600}
```

Pass the returned `sessionId` to later tools. Query with `status`, then close in success and failure cleanup:

```json
{"action":"close","sessionId":"s.<domain>.<id>"}
```

Old-domain handles return `SESSION_EXPIRED_DOMAIN_RELOAD`. Close first removes event subscriptions, then callback/Emit registrations, then variables and handles. Emitted type memory reports `domainReload` because a `Run` dynamic assembly cannot unload independently. Cancellation and timeout preserve effects that already happened; inspect `sideEffectsMayHaveOccurred` and session status, then close without retrying the program.

Use `maxAsyncOperations` only when the default 64 concurrent operations is insufficient; the hard maximum is 256. `status` reports `activeAsyncOperations`, completed/cancelled/error counters, `recentAsyncDiagnostics`, the real active `subscriptionCount`, callback counters, and bounded diagnostics. `close` cancels the session token and reports operations still running; it cannot forcibly stop a project method that has already begun. A closed session invalidates closures, async delegates, callbacks, and custom-accessor entry points even though a generated CLR type remains allocated until Domain Reload.

## Error recovery

Execution failures expose structured recovery data under `error.detail`. Read the object fields directly:

```json
{
  "stage": "bind",
  "sourceSpan": {"start": 12, "length": 5, "end": 17, "line": 2, "column": 4, "endLine": 2, "endColumn": 9},
  "diagnostics": [],
  "candidates": ["Example.Api.Call(System.Int32)"],
  "sideEffectsMayHaveOccurred": false,
  "nextAction": "Correct the argument type and call once with the revised code."
}
```

`stage` is `parse`, `bind`, `policy`, `runtime`, `budget`, or `cancelled`. Prefer `sourceSpan`, `diagnostics`, and `candidates`; the parallel `sourceSpanJson`, `diagnosticsJson`, and `candidatesJson` fields are temporary migration compatibility and new clients should not parse them. Follow `nextAction`, inspect session state when side effects may have occurred, and never automatically replay the original execution.

## Reflection calls

Use exactly one request shape. A typed static call:

```json
{"typeName":"Example.MathApi","methodName":"Add","arguments":[{"value":{"kind":"literal","typeName":"System.Int32","value":2}},{"value":{"kind":"literal","typeName":"System.Int32","value":3}}],"parameterTypeNames":["System.Int32","System.Int32"]}
```

For an instance, pass `targetHandle` plus `sessionId`; do not combine it with hierarchy/static-member paths. Named arguments use `name`; by-reference arguments use `direction=ref|out`. Generic methods use `genericTypeArguments`. Awaitables use `awaitMode=auto|always|never` with bounded `awaitTimeoutMs`.

`resultMode` accepts `auto|inline|handle|legacyString`; `handle` requires a valid persistent session. Invalid modes, conflicting shapes and malformed typed arguments are rejected before target dispatch. Metadata errors are checked before instance getters or custom conversions. Once user code may have run, failures retain `sideEffectsMayHaveOccurred=true`, including await and result encoding failures; do not infer rollback from an error code.

The shared expression/evaluation Bridge handler also validates modes, syntax,
budget field names and integer wire types before dispatch. All variable type and
handle metadata is checked before any custom decoding. Decode, execution, result
summary and response failures retain the same request's side-effect evidence.
Budget defaults and clamping remain compatible; unknown, duplicate or non-integer
budget fields are rejected instead of silently selecting a default.

## C# subset

Preflight generated code before execution when the backend boundary is uncertain:

```json
{"code":"value + 2","mode":"expression","backend":"compiled","variableTypes":[{"name":"value","typeName":"System.Int32"}]}
```

`backend=interpret` parses only; `emit` accepts the same full V2 Eval AST (including lambda/closure/await) and checks cached runtime capability; `compiled` parses, statically binds, lowers and compiles a delegate. Validation does not execute getters, constructors, methods, conversions, or business code. Success is not proof of runtime binding or successful execution. The synchronous profile remains specific to dynamic type bodies.

```json
{"code":"var sum = 0; for (var i = 0; i < 10; i++) sum += i; return sum;","mode":"statements","executionBackend":"auto","limits":{"timeoutMs":3000,"maxStatements":10000,"maxLoopIterations":10000}}
```

The V2 interpreter supports literals, member/index access, calls, operators, cast/as/is, `??`, `??=`, `typeof`, `nameof`, `default(T)`, lexical locals, assignment, blocks, branches, loops, flow control, allowed construction, exceptions, closures, block lambdas, async lambdas, and non-main-thread-blocking await. Explicit generic calls and practical deterministic inference are available, including `Fixture.Identity<int>(3)`. Inference follows base/interface chains, nullable, arrays and `params`; lambda return values alone never infer a type argument. Use explicit generic arguments when `CSHARP_BIND_GENERIC_INFERENCE_FAILED` returns candidates.

Exceptions support ordered typed/catch-all clauses, catch variables, `throw expression`, rethrow, and `finally` across return/break/continue:

```json
{"code":"try { return Api.Read(); } catch (System.TimeoutException ex) { return fallback; } finally { audit++; }","mode":"statements","sessionId":"s.<domain>.<id>"}
```

Policy, budget, cancellation, and other execution-contract failures cannot be swallowed by user catch clauses. On cancellation/budget failure, finally receives a separate 100ms/256-statement cleanup budget; cleanup failure appears in `error.detail.cleanupDiagnostics` without replacing the primary failure. Catch filters, `using`, `lock`, iterators, and `yield` remain unsupported. `return`, `break`, and `continue` from finally are rejected.

Closures capture variable cells by reference. `foreach` creates one captured cell per iteration; `for` captures its shared loop variable. Expression, multi-parameter, typed, and block lambdas are supported:

```json
{"code":"var offset = 2; var add = (int x) => { return x + offset; }; offset = 4; return add(3);","mode":"statements"}
```

Async lambdas may convert only to delegates returning `Task` or `Task<T>`; async void/`Action`/ordinary event handler conversion returns `CSHARP_ASYNC_VOID_UNSUPPORTED`. Any closure or async delegate returned or otherwise retained past the current call requires a persistent session, which must be closed.
When a persisted closure is invoked by a later `csharp_eval`, it uses that call's current budget and cancellation context. A delegate invoked independently by project code uses the owning session token, so closing the session remains the cancellation and invalidation boundary.

Closed generic types, nullable types, implicit arrays, jagged arrays, and multidimensional arrays of rank 1–4 are supported:

```json
{"code":"var values = new[] { 1, 2L }; int[,] matrix = { { 1, 2 }, { 3, 4 } }; matrix[1,0] = 7; return matrix[1,0];","mode":"statements","limits":{"maxArrayElements":100000,"maxAwaits":1000}}
```

Initializers must be rectangular; rank above 4, empty/all-null implicit arrays, and element-budget overflow return stable array diagnostics. The policy denies filesystem, process, network, environment mutation, threading, native interop, assembly loading, Editor exit, and compilation operations.

Event subscriptions are session-owned. Keep the same delegate identity for explicit removal:

```json
{"code":"source.Changed += handler;","mode":"statements","sessionId":"s.<domain>.<id>","variables":{"source":{"kind":"handle","handle":"h.<domain>.object.<id>"},"handler":{"kind":"handle","handle":"h.<domain>.delegate.<id>"}}}
```

`source.Changed -= handler` unregisters the matching lease. Session close, TTL expiry, and relevant PlayMode invalidation also unsubscribe it.

Backend selection is explicit and stable:

- `interpret` executes the complete V2 AST.
- `emit` generates/caches a DynamicMethod entry that still calls the parsed `CSharpProgram`; it avoids repeated parse/entry setup but is not direct C# or per-node IL compilation.
- `compiled` statically binds and lowers the supported synchronous AST to an Expression Tree delegate. It supports typed locals/inputs, operators, assignment, `if`/`while`/`for`, members, calls, explicit generic calls, constructors, casts, one-dimensional arrays/indexers, `??`, and type intrinsics. Unsupported nodes fail before execution with `CSHARP_COMPILED_UNSUPPORTED_NODE`; there is no interpreter fallback.
- `auto` preserves compatibility: it interprets the first successful AST and may reuse the old emit-cache on later identical calls. It does not auto-select `compiled`, and no backend switch occurs after execution begins.

Use `compiled` for repeatedly invoked synchronous snippets whose variable types are stable. Use `interpret` for `try/catch/finally`, `foreach`, lambda/closure, await, inferred generics, supported optional/params method calls, multidimensional arrays, and other full-V2 nodes. Named/ref/out source arguments are not supported by the Eval parser: use structured `unity_reflection_call` arguments instead. Member access and calls in a compiled delegate still pass through the execution context so Unity main-thread scheduling, policy, budgets, diagnostics, and side-effect evidence remain intact.

## Capacity and request diagnostics

The fixed LRU limits are 256 emit source keys and 128 compiled keys. Sync/async emit delegates share a slot. Compiled validation uses the same bounded cache as execution. Eviction removes cache references only; it does not invalidate retained delegates, close sessions, or guarantee immediate memory collection.

Dynamic type generation has a shared AppDomain quota of 256 attempts. Once a generation slot is reserved for the irreversible generation phase it is not refunded on failure. Cached types remain reusable at the limit, subject to session capacity. The capability probe creates at most one separate type per Domain. Session close, Engine recreation, Server reconnect, and PlayMode transitions without Domain Reload do not reset the quota. Never force a Reload automatically.

Read the response's `resourceDiagnostics` on success and `error.detail.resourceDiagnostics` on failure. Entries contain `code`, `severity`, `resource`, `action`, `count`, `limit`, `used`, and `nextAction`. They are request-local, aggregated by code/resource, and bounded to 16 entries; `resourceDiagnosticsDroppedCount` reports omitted entries. Existing `diagnostics` fields keep their original types.

| Code | Meaning and action |
| --- | --- |
| `EVAL_CACHE_EVICTED` | Warning: an older program was evicted. Keep a successful result; no replay is needed. |
| `EVAL_CACHE_WARMUP_FAILED` | Warning: auto execution succeeded but cache warmup failed. Keep the result; do not replay execution. |
| `EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED` | Error: new generation was refused. Reuse an existing type or Eval; a future Reload requires user authorization. |
| `SESSION_LIMIT_EXCEEDED` | Error: session handle/type capacity was refused before generation. Inspect the named resource and session state. |
| `EMIT_GENERATION_SLOT_CONSUMED` | Supplemental warning on failure: the slot cannot be reclaimed; the primary error is still authoritative. |

`unity_capabilities_get.execution.resources` returns live `generation`, `emitCache`/`compiledCache` (`capacity/count/hits/misses/evictions`), and `domainTypes` (`limit/used/rejected/failedAfterReservation/probeTypeCount`). These are entry/attempt limits, not process memory limits. Global totals do not replace request-local evidence. A later business failure retains any eviction notices; failure never implies rollback.

## Dynamic types

`reflection_emit_type` requires a session and structured spec:

```json
{"sessionId":"s.<domain>.<id>","spec":{"typeName":"UPilot.Dynamic.Listener","interfaces":["Example.IListener"],"methods":[{"name":"OnValue","implements":"Example.IListener.OnValue","returnType":"System.Void","parameters":[{"name":"value","typeName":"System.Int32"}],"body":"return;"}]},"cachePolicy":"specHash","nameConflictPolicy":"reject","createInstance":true}
```

Specs support base type, interfaces, fields, automatic or custom property accessors, constructors/base constructor signatures, methods, overrides/interface implementations, and callback handles. Custom accessors use `getterBody`/`setterBody`; getter receives `this`, setter receives `this` and `value`.

Callback methods accept a bounded policy:

```json
{"callbackPolicy":{"exceptionMode":"isolate","maxInvocations":10000,"maxReentrancy":8,"diagnosticsCapacity":32}}
```

`execution_session(status)` reports callback invocation/rejection/error counters and bounded recent diagnostics. `isolate` returns the declared type's default value after a callback failure or limit rejection; `propagate` preserves the exception. Bodies default to `bodyBackend=interpret` and use the synchronous V2 profile: try/catch/finally, generics, implicit/typed arrays, and rank 1–4 arrays are allowed. Set type- or member-level `bodyBackend=compiled` to require direct lowering before the CLR type is published; it never falls back and follows the finite compiled boundary above. Await, async, lambda/closure, raw IL, assembly saving, DLL loading, arbitrary method replacement, and source compilation are rejected. Same-domain canonical SHA-256 specs are cached; name conflicts require explicit `hashSuffix`.

Spec-hash cache hits reuse only the generated CLR `Type`. Each session and emitted instance receives its own callback registration, guard counters, diagnostics ring, and cleanup lease. Prefer `createInstance=true` to obtain an instance bound to the current session, and never reuse an `instanceHandle` in another session. With `exceptionMode=isolate`, callback exceptions and limit rejections are recorded and return the declared return type's default value. With `exceptionMode=propagate`, the original callback exception type and stack semantics are preserved rather than exposing a reflection wrapper.

If capabilities report `REFLECTION_EMIT_UNAVAILABLE`, do not fall back to Roslyn, CodeDom, mcs, Unity compilation, or source files. Use an existing compiled project helper instead.
