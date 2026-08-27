# MonoHook Tracing

MonoHook Tracing is an optional diagnostic feature for observing selected Unity lifecycle, object, Transform, and component calls. Manual management is the primary workflow; MCP tools are an auxiliary interface.

## Manual Workflow

- Open `UPilot > Advanced > MonoHook`.
- Hook points, per-point stack capture, and Console output all default to disabled.
- Select only the points needed for the current investigation, then apply the configuration explicitly.
- Keep high-frequency points such as `Update` and Transform setters bounded. Enable stack capture only for the smallest useful point set.
- The window's `输出到 Console` switch emits formatted `[UPilot][MonoHook]` logs and has its own rate limit. This switch is not currently exposed through the MCP configure tool.

## MCP Workflow

Use these tools only when the user asks for MCP-assisted inspection or configuration:

- `unity_monohook_tracing_status`: read the saved point configuration, effective/installed state, support diagnostics, and event counters.
- `unity_monohook_tracing_configure`: update selected point states, stack capture, or the master switch. `apply` defaults to `false`, so the default behavior saves configuration without installing or uninstalling hooks.
- `unity_monohook_tracing_events`: read recent tracing events. `consume` defaults to `false`.

For a temporary diagnostic session:

1. Query status and retain the original master switch, point enablement, and per-point stack settings.
2. Save the narrow configuration with `apply=false`.
3. Use `apply=true` only when the user explicitly requested hook installation or application.
4. Read a bounded event count and stop when enough evidence is collected.
5. Restore the retained configuration and apply it if the temporary configuration was applied.

Do not enable, apply, auto-restore, or consume events merely because the tools are available. Respect `Unsupported` diagnostics and the returned `nextAction`; do not bypass them with Native, InternalCall, injected, reflection-eval, or other lower-level hook fallbacks.

MonoHook Console output is different from `unity_console_capture_*`: MonoHook decides whether hook events are emitted to the Unity Console, while persistent Console capture records Console messages that already exist. Enabling capture does not enable MonoHook Console output.
