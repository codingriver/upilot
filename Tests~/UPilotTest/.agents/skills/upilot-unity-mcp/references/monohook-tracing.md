# UPilot Tracer

UPilot Tracer (`UPilot 追踪器`) is an optional diagnostic feature for observing selected Unity lifecycle, object, Transform, and component calls. In a UPilot context, `Tracer`, `追踪器`, and `the tracer` all refer to this feature. MonoHook is the internal implementation technology or a preserved compatibility identifier, not a separate user-facing feature name. Manual management is the primary workflow; MCP tools are an auxiliary interface.

## Manual Workflow

- Open `UPilot > Advanced > 追踪器`.
- Trace points, stack capture, and Console output all default to disabled.
- Select only the points needed for the current investigation, then apply the configuration explicitly.
- Physical Hooks are technical replacements; the default per-point execution mode is `PassThrough`, so built-in Providers must call the original method unchanged. `Intercept` is a separate opt-in mode and is available only when a Provider declares support; tracing-path failures are swallowed and counted rather than propagated to the business call.
- `自动注入追踪点位` is independent from manual `应用` and defaults to disabled. Domain Reload and PlayMode are retained as subordinate timing switches; neither can inject while the master switch is off. Turning the master switch off preserves those timing choices and cancels pending PlayMode injection.
- Keep high-frequency points such as `Update` and Transform setters bounded. Prefer `SelectedPoints` stack capture for focused diagnosis; use `AllEnabledPoints` only with a narrow enabled point set and effective filters.
- The window's `输出到 Console` switch emits formatted `[UPilot][Trace]` logs and has its own rate limit. This switch is not currently exposed through the MCP configure tool.
- Target filters can combine object source/type, GameObject name, hierarchy/parent/ancestor/root/direct-child, scene/resource path, Layer/Tag (equals/wildcard/regex), Active/enabled, required-component enabled state, Prefab state/source path, selection, point/method/phase/event source, EditMode/PlayMode, object InstanceID/GlobalObjectId, and value-change conditions. Conditions inside one rule are AND; include rules are OR; exclude rules have priority.
- Optional global/per-object event limits and duplicate suppression are disabled by default; use them only after filtering and report their dropped counters separately from filter rejections.
- `unity_monohook_tracing_status` reports the per-object and duplicate-suppression settings and separate dropped counters. Configure them only with the explicit `updatePerObjectRateLimit` / `updateDuplicateSuppression` flags.
- Use the built-in `场景业务对象`, `当前选中及子树`, or `排除 Editor 临时对象` profiles for common noise control. Target name and hierarchy filters suppress recording before stack capture and Console output, but do not necessarily reduce the physical Hook cost; type-only lifecycle filters may reduce installation candidates.
- One global profile is the default for every trace point. Per-point overrides are optional and disabled by default; an empty override inherits the global profile. `__none__` or a disabled effective profile means no target filtering.
- Configure stack capture explicitly with `setStackTraceCaptureMode` and `stackTraceCaptureMode` (`Disabled`, `SelectedPoints`, or `AllEnabledPoints`). In `SelectedPoints` mode, update selections with `pointIds`, `updatePointStackTraceSelection`, and `captureStackTrace`.
- Configure point filter overrides explicitly with `updatePointFilterOverridesEnabled` / `pointFilterOverridesEnabled`, then use `pointIds`, `updatePointFilterProfile`, and `pointFilterProfileId`. Do not infer point-level semantics from global fields.

## MCP Workflow

Use these tools only when the user asks for MCP-assisted inspection or configuration:

- `unity_monohook_tracing_status`: read the saved point configuration, effective/installed state, support diagnostics, event counters, and lifecycle installation details (type/method/trampoline counts plus target entries).
- `unity_monohook_tracing_configure`: update selected point states, stack capture mode/selection, global filtering and optional point overrides, execution mode, automatic-injection master/timing switches, or the tracer master switch. Set `updatePointEnabled=true` only when `pointIds` should also change enabled state. `apply` defaults to `false`, so the default behavior saves configuration without installing or uninstalling hooks.
- `unity_monohook_tracing_events`: read recent tracing events. `consume` defaults to `false`.

For a temporary diagnostic session:

1. Query status and retain the original master switch, point enablement, stack capture mode/selection, global filter profile, and point override settings.
2. Save the narrow configuration with `apply=false`.
3. Use `apply=true` only when the user explicitly requested hook installation or application.
4. Read a bounded event count and stop when enough evidence is collected.
5. Restore the retained configuration and apply it if the temporary configuration was applied.

When diagnosing a noisy point, narrow in this order: object source/type → hierarchy/name → point/method/phase → value condition. Use filter statistics and the last rejection reason before widening the scope.

Do not enable, apply, auto-restore, or consume events merely because the tools are available. Respect `Unsupported` diagnostics and the returned `nextAction`; do not bypass them with Native, InternalCall, injected, reflection-eval, or other lower-level hook fallbacks.

UPilot Tracer Console output is different from `unity_console_capture_*`: the tracer decides whether trace events are emitted to the Unity Console, while persistent Console capture records Console messages that already exist. Enabling capture does not enable tracer Console output.
