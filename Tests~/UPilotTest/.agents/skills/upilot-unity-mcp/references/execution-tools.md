# Execution Tools

Load this reference only when using reflection calls, C# subset evaluation, dynamic types, or persistent handles.

## Choose the tool

| Need | Tool |
| --- | --- |
| Call one already-loaded static or instance method | `unity_reflection_call` |
| Evaluate one bounded expression through the reflection entry point | `unity_reflection_call(expression=...)` |
| Run locals, exceptions, closures, async lambdas, control flow, or await | `csharp_eval` |
| Create an actual temporary CLR type or interface adapter | `reflection_emit_type` |
| Keep variables, instances, types, or callbacks across calls | `execution_session` |

All four routes require write access, can produce side effects, are non-idempotent, and must not be retried automatically. A failure does not imply rollback.

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
{"action":"open","title":"temporary adapter","ttlSec":600,"maxHandles":256,"maxDynamicTypes":32,"maxCallbacks":64,"maxAsyncOperations":64}
```

`ttlSec` is idle TTL (default 600, hard max 3600). In addition, every session has an **absolute lifetime of 3600 seconds from creation** — it is force-killed 1 hour after creation regardless of touch activity. `maxHandles` (default 256, max 1024) bounds object/type/delegate handles. `maxDynamicTypes` (default 32, max 128) bounds registered dynamic types. `maxCallbacks` (default 64, max 256) bounds callback/accessor registrations. `maxAsyncOperations` (default 64, max 256) bounds concurrent async operations.

Pass the returned `sessionId` to later tools. Query with `status`, then close in success and failure cleanup:

```json
{"action":"close","sessionId":"s.<domain>.<id>"}
```

Close is idempotent —  a second close returns `alreadyClosed: true` instead of throwing. Old-domain handles return `SESSION_EXPIRED_DOMAIN_RELOAD`. Close first removes event subscriptions, then callback/Emit registrations, then variables and handles. Emitted type memory reports `domainReload` because a `Run` dynamic assembly cannot unload independently. Cancellation and timeout preserve effects that already happened; inspect `sideEffectsMayHaveOccurred` and session status, then close without retrying the program.

Use `maxAsyncOperations` only when the default 64 concurrent operations is insufficient; the hard maximum is 256. `status` reports `activeAsyncOperations`, completed/cancelled/error counters, `recentAsyncDiagnostics`, the real active `subscriptionCount`, callback counters, and bounded diagnostics. `close` cancels the session token and reports operations still running; it cannot forcibly stop a project method that has already begun. A closed session invalidates closures, async delegates, callbacks, and custom-accessor entry points even though a generated CLR type remains allocated until Domain Reload.

PlayMode transitions invalidate handles referencing scene/GameObject components. Session-scoped objects survive Domain Reloads (which issue new domain generation IDs into session IDs) but are lost on Editor restart or AppDomain unload.

Session-level error codes returned by tools:
- `SESSION_EXPIRED_DOMAIN_RELOAD` — session was opened in a prior AppDomain
- `EXECUTION_SESSION_CLOSED` — session is already closed
- `SESSION_LIMIT_EXCEEDED` — handle/dynamic-type/callback capacity exceeded
- `HANDLE_NOT_FOUND` — the referenced handle does not exist in this session
- `HANDLE_KIND_MISMATCH` — the handle's kind does not match the expected kind (e.g. using a delegate handle as an object handle)

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

```json
{"code":"var sum = 0; for (var i = 0; i < 10; i++) sum += i; return sum;","mode":"statements","executionBackend":"auto","limits":{"timeoutMs":3000,"maxStatements":10000,"maxLoopIterations":10000},"imports":["System","UnityEngine"]}
```

Type resolution uses the default import set `["System", "UnityEngine", "UnityEditor"]`. Providing explicit `imports` replaces (not appends to) the defaults. Imports do not load assemblies; they only participate in namespace-based type resolution.

The V2 interpreter supports literals, member/index access, calls, operators, cast/as/is, lexical locals, assignment, blocks, branches, loops, flow control, allowed construction, exceptions, closures, block lambdas, async lambdas, and await. Await targets `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>`. A `Task.Yield()`-style await uses a worker-thread poll loop to avoid blocking the Unity main thread, but any await whose continuation requires the main thread has a **hard 30-second timeout** that triggers `CSHARP_MAIN_THREAD_TIMEOUT`. Explicit generic calls and practical deterministic inference are available, including `Fixture.Identity<int>(3)`. Inference follows base/interface chains, nullable, arrays and `params`; lambda return values alone never infer a type argument. Use explicit generic arguments when `CSHARP_BIND_GENERIC_INFERENCE_FAILED` returns candidates.

Exceptions support ordered typed/catch-all clauses, catch variables, `throw expression`, rethrow, and `finally` across return/break/continue:

```json
{"code":"try { return Api.Read(); } catch (System.TimeoutException ex) { return fallback; } finally { audit++; }","mode":"statements","sessionId":"s.<domain>.<id>"}
```

Policy, budget, cancellation, and other execution-contract failures cannot be swallowed by user catch clauses. On cancellation/budget failure, finally receives a separate 100ms/256-statement cleanup budget; cleanup failure appears in `error.detail.cleanupDiagnostics` without replacing the primary failure. Catch filters, `using`, `lock`, iterators, `yield`, and anonymous type construction (`new { ... }`) remain unsupported. `return`, `break`, and `continue` from finally are rejected.

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

`auto` interprets the first successful AST and may reuse a verified emitted delegate cache on later identical calls. The cache key is `(SHA-256(code), mode, imports)` — changing any of these components invalidates the cache. Explicit `emit` uses `DynamicMethod` (JIT-only) and may fail on IL2CPP / AOT platforms. No backend switch occurs after execution begins.

### Budget fields

Every budget field has a default and a hard ceiling enforced by the capability probe. All integer budget fields are clamped and unknown/duplicate fields are rejected:

| Budget field | Default | Hard limit | Effect |
|---|---|---|---|
| `timeoutMs` | 3000 | 30000 | Wall-clock timeout (ms) |
| `maxStatements` | 10000 | 100000 | Statement execution limit |
| `maxLoopIterations` | 10000 | 100000 | Total loop iterations across all loops |
| `maxCalls` | 1000 | 100000 | Total method call limit |
| `maxAllocations` | 1000 | 100000 | Total object allocations |
| `maxRecursion` | 64 | 256 | Call-stack depth limit |
| `maxResultBytes` | 1048576 | 1048576 | JSON result serialization limit (1 MiB) |
| `maxAwaits` | 1000 | 100000 | Total await completions |
| `maxArrayElements` | 100000 | 1000000 | Total array elements accessed/created |

### languageProfileMode

When a `unity_reflection_call(expression=...)` routes through the shared evaluation bridge, the `languageProfileMode` is set to `"reflection-expression"` internally. This restricts the expression to expression-level constructs (no statements, no async/lambda), enforces a `maxStatements` budget of 1, and returns `"expression"` encoding for compatibility. Manual `csharp_eval` calls should not use this mode.

## Object inspector

`csharp_object_dump` recursively prints all fields and properties of an arbitrary C# runtime object. It requires a persistent `execution_session` and a handle obtained from `csharp_eval(resultMode=handle)`:

```json
{"sessionId":"s.<domain>.<id>","handle":"h.<domain>.object.<id>","maxDepth":3,"maxFieldsPerNode":100,"maxTotalNodes":5000,"includeStatic":false,"ignoreTypes":["System.String"],"outputFormat":"json","indentation":"  "}
```

`maxDepth` defaults to 3 (max 64); 0 skips all children. `maxFieldsPerNode` defaults to 100 (max 500). `maxTotalNodes` defaults to 5000 (max 20000). `includeStatic` also exposes public static properties. `ignoreTypes` accepts full type names (e.g. `"UnityEngine.Vector3"`); matched types show "`(ignored: TypeName)`" in text output and skip recursive expansion. `outputFormat` of `"text"` returns indented tree text; `"json"` returns a structured tree with `children` arrays. Regardless of the requested format, the response payload always contains both `.root` (JSON tree) and `.text` (indented text). `indentation` only applies to text output and defaults to two spaces.

### Supported types

Scalar leaf types render their value directly: primitive types, `System.Decimal`, `System.String`, `System.Enum`, `System.Type`, `System.Guid`, `System.DateTime`, `System.DateTimeOffset`, `System.TimeSpan`, `System.Uri`, `System.Version`, and any other type implementing `System.IFormattable`. Arrays and `System.Collections.IEnumerable` are expanded by index. All other types are recursed via instance fields (public and private) and public instance properties.

### Hard limits and defaults

| Parameter          | Default | Hard Limit | Effect when hit                        |
|--------------------|---------|------------|----------------------------------------|
| `maxDepth`         | 3       | 64         | Leaf shows `"[max depth]"` suffix      |
| `maxFieldsPerNode` | 100     | 500        | Truncated with count message           |
| `maxTotalNodes`    | 5000    | 20000      | Remaining nodes replaced with "`(truncated: max total nodes)`" |

### Limitations

- **ignoreTypes is a union with the default skip set, not a replacement**. The user-provided `ignoreTypes` is merged with the built-in set of `System.IntPtr`, `System.UIntPtr`, `System.RuntimeType`, `System.RuntimeMethodHandle`, `System.RuntimeFieldHandle`, and `System.Threading.Thread`. To inspect all types including the defaults, pass a single placeholder and then filter yourself.
- **Response always contains both `.root` (JSON tree) and `.text` (indented text representation)** regardless of `outputFormat`. Use `outputFormat` to indicate your preferred primary view, but both are available. The text format uses `{static} {name} ({declaredType} -> {runtimeType}): {value}` when runtime and declared types differ, or `{name} ({typeName}): {value}` when they match.
- **Compiler-generated fields**: fields marked `IsSpecialName` (e.g. `_BackingField`) and `IsLiteral` (`const`) are always skipped.
- **Indexed properties**: properties with index parameters are always skipped. Unreadable properties (`!CanRead`) are always skipped.
- **Collection element types**: elements from non-generic `IEnumerable` have no `declaredType` — only `runtimeType` is available.
- **Static property name collisions**: if an instance property already has name `X`, a static property with the same name `.X` is skipped to avoid duplication.
- **Getter failures are silent**: a field or property whose getter throws is silently rendered as `null` — indistinguishable from a genuinely null value.
- **Threading and native primitives**: `Span<T>`, `Memory<T>`, native pointers, thread handles, and other non-reflection-friendly types may fail to retrieve a value and render as `null`.
- **Circular references**: detected via reference-equality visited set and rendered as `"(circular ref: TypeName)"`.
- **ignoreTypes matching**: at field/property level, matching is against the declared type; at the root and child walk level, matching is against the runtime type. A field declared `object` holding a `string` is not caught at the field-level check.
- **maxFieldsPerNode has an implicit minimum of 1** — values below 1 are silently clamped.

## Dynamic types

`reflection_emit_type` requires a session and structured spec:

```json
{"sessionId":"s.<domain>.<id>","spec":{"typeName":"UPilot.Dynamic.Listener","interfaces":["Example.IListener"],"methods":[{"name":"OnValue","implements":"Example.IListener.OnValue","returnType":"System.Void","parameters":[{"name":"value","typeName":"System.Int32"}],"body":"return;"}]},"cachePolicy":"specHash","nameConflictPolicy":"reject","createInstance":true,"constructorArguments":[42]}
```

### Full spec schema

| Element | Field | Required | Description |
|---|---|---|---|
| **Type** | `typeName` | Y | Fully qualified CLR type name |
| | `visibility` | N | `"public"` (default) or `"internal"` |
| | `baseType` | N | Base type; defaults to `"System.Object"` |
| | `interfaces` | N | String array of interface type names |
| | `isSealed` | N | Default `true`; `false` allows subclassing |
| **Fields** | `fields[].name` | Y | Field name |
| | `fields[].typeName` | Y | Type name |
| | `fields[].visibility` | N | `"public"` or `"private"` (default) |
| | `fields[].isStatic` | N | `false` by default |
| | `fields[].isReadonly` | N | `false` by default |
| **Properties** | `properties[].name` | Y | Property name |
| | `properties[].typeName` | Y | Property type |
| | `properties[].visibility` | N | `"public"` (default) or `"private"` |
| | `properties[].hasGetter` | N | Default `false` |
| | `properties[].hasSetter` | N | Default `false` |
| | `properties[].backingField` | N | Explicit backing field name; auto-generated as `"<{name}>k__BackingField"` if omitted |
| | `properties[].getterBody` | N | C# subset body for getter (`this` accessible) |
| | `properties[].setterBody` | N | C# subset body for setter (`this` + `value` accessible) |
| **Constructors** | `constructors[].visibility` | N | `"public"` (default) or `"private"` |
| | `constructors[].parameters` | N | Array of `{name, typeName}` |
| | `constructors[].baseConstructorParameterTypeNames` | N | Base type constructor argument types |
| | `constructors[].baseArgumentNames` | N | Names of variables/fields passed to base |
| | `constructors[].body` | N | C# subset body |
| **Methods** | `methods[].name` | Y | Method name |
| | `methods[].returnType` | Y | Return type name |
| | `methods[].parameters` | N | Array of `{name, typeName}` |
| | `methods[].visibility` | N | `"public"` (default) or `"private"` |
| | `methods[].isStatic` | N | `false` by default |
| | `methods[].isVirtual` | N | `false` by default |
| | `methods[].isFinal` | N | `false` by default |
| | `methods[].implements` | N | Interface method fully qualified name |
| | `methods[].overrides` | N | Base type method fully qualified name |
| | `methods[].body` | N | Synchronous V2 C# subset body |
| | `methods[].callbackHandle` | N | Session handle to a delegate/lambda; method becomes callback dispatcher |
| | `methods[].callbackPolicy` | N | Callback policy object (see below) |

### Default constructor

If no `constructors` array is provided, a `public` parameterless constructor is generated automatically —  but only if the base type also has a parameterless constructor. Otherwise `REFLECTION_EMIT_CONSTRUCTOR_ERROR` is thrown.

### Body constraints

Method and constructor bodies run under the synchronous V2 profile: try/catch/finally, generics, implicit/typed arrays, and rank 1–4 arrays are allowed; await, async, lambda/closure, raw IL, assembly saving, and source compilation are rejected with `CSHARP_EMIT_UNSUPPORTED_NODE`. When a method has `callbackHandle`, the body is replaced by the callback dispatch; `body` is ignored.

### Callback policies

Callback methods accept a bounded policy:

```json
{"callbackPolicy":{"exceptionMode":"isolate","maxInvocations":10000,"maxReentrancy":8,"diagnosticsCapacity":32}}
```

| Parameter | Default | Hard Limit | Description |
|---|---|---|---|
| `exceptionMode` | `"isolate"` | — | `"isolate"`: return default value on failure; `"propagate"`: rethrow original exception with preserved stack via `ExceptionDispatchInfo` |
| `maxInvocations` | 10000 | 100000 | Total callback invocation limit |
| `maxReentrancy` | 8 | 64 | Per-thread reentry depth (`[ThreadStatic]`) |
| `diagnosticsCapacity` | 32 | 128 | Ring buffer size for recent diagnostics |

Non-callback methods (those without `callbackHandle`) always use `"propagate"` exception mode regardless of any specified policy.

### Cache and lifecycle

`cachePolicy` only supports `"specHash"` (the V2 default). The SHA-256 hash of the canonical spec JSON is used as the cache key. If no `specHash` is provided, a random GUID is generated —  meaning the spec **never hits the cache** and a new CLR Type is emitted each time. Spec-hash cache hits reuse only the generated CLR `Type` — the cache is shared across all sessions within the same AppDomain. Each session and emitted instance still receives its own callback registration, guard counters, diagnostics ring, and cleanup lease.

`nameConflictPolicy="hashSuffix"` resolves conflicts by appending `__{first8CharsOfHash}` to the type name. Prefer `createInstance=true` to obtain an instance bound to the current session, and never reuse an `instanceHandle` in another session. `constructorArguments` accepts plain JSON values or TypedValues when `createInstance=true`.
