# Changelog

## Unreleased

- Add Snapshot schema v1 with multi-Camera same-frame color capture, Built-in/URP raw and linear Float EXR depth, optional depth previews, strict SceneView/EditorWindow pixel evidence, and PlayMode Display 0 final-composite GameView capture.
- Add a unified Snapshot evidence manifest, persisted/cancellable jobs, project-relative hashed artifacts, and managed PNG baselines with pixel-difference ratio, SSIM, diff/heatmap diagnostics, and mandatory dry-run/confirm-token updates.
- Make every legacy `unity_screenshot_*` tool a Snapshot compatibility wrapper, remove the parallel Unity Bridge screenshot routes, and advance the public tool Registry to v6.
- Define minimized-editor behavior explicitly: Camera and GameView capture remain offscreen-capable, while SceneView and EditorWindow capture fail fast instead of returning unverified pixels.
- Make Console diagnostics reliable with persisted Stop-terminal recovery, shared summary/search classification, bounded summary samples, and precise UPilot-owned filtering that preserves business logs invoked through Reflection.
- Simplify the Unity menu to `打开 UPilot`, `高级设置`, `Flow`, and `追踪器`; keep `Test Runner` in English and move imported example/sample launchers into the Flow Test Runner.
- Add explicit `csharp_eval`, `reflection_emit_type`, and `execution_session` tools backed by a Unity-version-independent BCL execution core.
- Upgrade `unity_reflection_call` with typed/named/ref/out arguments, deterministic overload selection, generics, handles, awaitables, and typed results while retaining legacy parameters.
- Add bounded C# subset interpretation, emitted delegate caching, structured Reflection.Emit type generation, session/handle lifecycle diagnostics, Registry v5, and execution-tool Skill guidance.
- Keep `reflection_eval` absent from the public MCP surface and avoid Unity Eval/Compilation APIs, Roslyn, CodeDom, mcs, raw IL input, and assembly persistence.
- Complete execution P1 with structured source spans, cooperative cancellation, generic inference/calls, typed and jagged arrays, session-owned event subscriptions, guarded callback diagnostics, and custom emitted property accessors.
- Isolate cached Reflection.Emit callback registrations by session and emitted instance, and preserve the original callback exception type and stack when `exceptionMode=propagate`.
- Expand targeted execution acceptance across generic constraints/ambiguity, invalid arrays, every structured error stage, cancellation without replay, static/lambda/TTL/PlayMode event cleanup, callback concurrency/reentrancy/diagnostic capacity, mixed property accessors, and infrastructure leak cleanup.
- Keep Agent Rules at 25 and bump Skill Install content to 23 with structured error recovery, event-session cleanup, callback-policy, and cache-hit session/instance isolation guidance; Registry remains v5 with 187 tools.
- Improve execution-tool MCP descriptions and parameter schemas, remove the stale “future csharp_eval” wording, and expose additive structured-error, callback-policy/isolation, exception-mode, and event-cleanup capabilities.
- Upgrade the private evaluator to `upilot-csharp-subset-v2`: bounded try/catch/finally/throw, lexical reference closures, typed/block/async lambdas without async void, practical deterministic generic inference, implicit and rank 1–4 arrays, async/session counters, and bounded finally cleanup diagnostics.
- Allow synchronous V2 exception/generic/array nodes in Reflection.Emit bodies while keeping lambda/closure/await/async rejected; bump Agent Rules to 26 and Skill Install content to 24 without changing Registry v5 or the 187-tool surface.
- Keep persistent async closures independent of disposed per-call cancellation sources, and avoid unsupported reflection over open `MethodBuilder` instances when reporting plain emitted methods on Unity Mono.

## 0.3.14

- Keep the main-window runtime mode accurate across update, status refresh, and restart transitions.
- Resolve managed runtime mode on the Unity main thread instead of caching a source/Python fallback from background status polling.

## 0.3.11

- Add a non-blocking main-window release reminder with per-version skip support.
- Surface update progress in the main window and open the update center directly into the active update state.

## 0.3.10

- Treat main/source installs as Python-only development builds.
- Publish managed MCP server binaries only from tagged release builds.

## 0.3.7

- Add release CI validation so tag, UPM package, and MCP server versions must match before publishing release assets.
- Keep update manifests aligned across UPilot package and managed MCP service releases.

## 0.3.5

- Stop the MCP service before UPilot package updates initiated from either the update center or Unity Package Manager, then restore it after package registration and assembly reload.
- Preserve update restart intent across domain reloads while avoiding automatic service startup during first installation.
- Prefer the current source package version over stale installed Python distribution metadata when reporting the MCP server version.

## 0.3.3

- Added a unified update center that clearly separates release and Main builds, package updates, and managed MCP service updates.
- Corrected local-development and Python runtime update guidance so bundled services are not compared with remote managed builds.
- Refined the main UPilot window with narrower responsive layout, clearer status hierarchy, and aligned Agent configuration controls.

## 0.3.2

- Refined the UPilot main window into a denser status dashboard with compact runtime details and table-style Agent configuration controls.
- Improved Agent MCP and Skill/rule consistency checks, update guidance, and preferences handling.
- Expanded long-running task operations, compile tooling, and related automated coverage.
- Constrained the Python MCP SDK to the compatible 1.x series so standalone server builds remain runnable.

## 0.2.0

- Added persistent Unity Console capture sessions with JSONL output, incremental reads, rotation, summaries, SHA256 verification, session listing, and confirm-token cleanup.
- Renamed the core product, C# namespaces, assemblies, menus, and documentation to UPilot.
- Added a stable tool registry, schema-v2 MCP responses, structured errors, cache freshness, operation timing, and async task tools.
- Standardized Streamable HTTP on port 8011 and kept WebSocket ports internal to the Unity Bridge.
- Added project configuration at `.upilot/config.json` and client configuration diagnostics.
- Made UPilot Flow optional and disabled by default, with Unity 6 and define constraints.
- Added UPilot Flow schema version 2, validation, dry-run migration, action descriptors, and migrated samples.
- Updated Agent rules and the UPilot skill to use capability discovery and phase-based acceptance.
