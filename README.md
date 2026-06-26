# upilot

upilot is an open-source Unity Editor automation bridge for AI agents and MCP clients.
It lets external tools inspect, control, and diagnose the Unity Editor through a local MCP server.

UIFlow is included as an optional add-on for YAML-driven EditorWindow UI automation. The main package identity is `upilot`.

## Features

- Unity Editor automation over MCP Streamable HTTP.
- Python MCP server connected to the Unity Editor bridge over WebSocket.
- Tools for editor status, console logs, compilation, assets, scenes, GameObjects, components, windows, screenshots, packages, menus, scripts, prefabs, materials, builds, tests, and diagnostics.
- Optional UIFlow automation for UIToolkit and selected IMGUI EditorWindow workflows.
- Core bridge support on Unity 2022.3+; UIFlow remains opt-in for Unity 6+.

## Compatibility

| Capability | Requirement | Notes |
| --- | --- | --- |
| upilot core bridge | Unity `2022.3` or newer | Compiled by default. |
| upilot MCP server | Python `3.11` or newer | Bundled under `unitypilot~/` during the rename. |
| UIFlow YAML automation | Unity `6000.0` or newer | Requires `UNITYPILOT_ENABLE_UIFLOW`. |
| Current validation project | Unity `6000.6.0a2` | See `Tests~/UnityUIFlowTest`. |

UIFlow also requires these Unity packages in the consuming project:

- `com.unity.inputsystem`
- `com.unity.ui`
- `com.unity.ui.test-framework`
- `com.unity.test-framework`

On Unity 2022, do not enable `UNITYPILOT_ENABLE_UIFLOW`; UIFlow MCP calls return `UIFLOW_UNAVAILABLE`, while the rest of upilot remains available.

## Installation

Add the package via **Window > Package Manager > Add package from git URL**:

```text
https://github.com/codingriver/upilot.git#v0.1.0
```

Or edit `Packages/manifest.json`:

```json
{
  "dependencies": {
    "io.github.codingriver.upilot": "https://github.com/codingriver/upilot.git#v0.1.0"
  }
}
```

## Use upilot

Open the Unity panel:

```text
upilot/upilot
```

The panel can start and stop the local MCP server, inspect bridge status, and show diagnostics.

For local development, the Python MCP server can also be started from this repository:

```bash
python -m pip install -r unitypilot~/requirements.txt
python unitypilot~/run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

Configure MCP clients to use:

```json
{
  "servers": {
    "upilot": {
      "type": "http",
      "url": "http://127.0.0.1:8011/mcp"
    }
  }
}
```

The legacy launcher remains available for compatibility:

```bash
python unitypilot~/run_unitypilot_mcp.py --transport http --http-port 8011 --port 8765
```

## UIFlow Add-On

UIFlow automates Unity Editor `EditorWindow` UI with YAML test cases. It is not a Game View or Play Mode runtime UI test framework.

Supported capabilities:

- UIToolkit selection with `#name`, `.class`, type names, and `[data-role=value]` style selectors.
- Pointer actions: click, double click, hover, drag, scroll, context menu, popup menu.
- Keyboard and input actions: focus, key press, text input, fast text assignment.
- Field and collection actions: set values, select options, sliders, tabs, lists, trees, tables, split views, breadcrumbs.
- Assertions and waits: visible, not visible, text, value, enabled state, property, element wait.
- Screenshots and Markdown/JSON reports.
- Selected IMGUI workflows through `imgui_*` actions.
- Headed Test Runner for local visual debugging and MCP-driven runs for agent validation.

Enable UIFlow with this scripting define symbol:

```text
UNITYPILOT_ENABLE_UIFLOW
```

Then open:

```text
upilot/UIFlow/Test Runner
```

Minimal YAML example:

```yaml
fixture:
  host_window: ExampleBasicLoginWindow
steps:
  - type_text_fast:
      selector: "#username-input"
      text: "admin"
  - type_text_fast:
      selector: "#password-input"
      text: "password"
  - click:
      selector: "#login-button"
  - assert_text:
      selector: "#status-label"
      text: "Login successful"
```

Complex UIFlow usage, action details, selector rules, page authoring conventions, and automation limits are documented separately:

- `Documentation~/UIFlow.md`
- `Documentation~/UIFlow.zh-CN.md`
- `Documentation~/00-API速查与最佳实践.md`
- `Documentation~/00-UIToolkit控件自动化覆盖与限制说明.md`
- `Documentation~/00-IMGUI控件自动化覆盖与限制说明.md`

## Compatibility Aliases

During the rename, some internal C# namespaces, assembly names, Python module names, and legacy MCP tool aliases still contain `UnityPilot`, `unitypilot`, or `UnityUIFlow`.
These remain intentionally for source and script compatibility while the public package, menu, panel, and MCP server identity move to `upilot`.

## Documentation

- Chinese README: `README.zh-CN.md`
- Changelog: `CHANGELOG.md`
- Chinese changelog: `CHANGELOG.zh-CN.md`
- License: `LICENSE.md`
- Third-party notices: `NOTICE.md`
- upilot MCP server development: `unitypilot~/DEVELOPMENT.md`
- upilot MCP server development (Chinese): `unitypilot~/DEVELOPMENT.zh-CN.md`
- UIFlow guide: `Documentation~/UIFlow.md`
- UIFlow Chinese guide: `Documentation~/UIFlow.zh-CN.md`
- Agent/MCP execution rules: `Documentation~/03-UnityUIFlow-Agent-MCP测试强制规范.md`

## License

See `LICENSE.md` for the main project terms and `NOTICE.md` for third-party component notices. Embedded third-party components keep their original licenses.

## Embedded MonoHook and unsafe code

This package includes a copy of the core [MonoHook](https://github.com/Misaka-Mikoto-Tech/MonoHook) sources under `Editor/Plugins/MonoHook/`. MonoHook is MIT licensed and is used inside the Unity Editor to hook managed methods at runtime.

- **Unsafe code**: MonoHook requires C# `unsafe` code. The required assemblies enable this through the `allowUnsafeCode` option in their `.asmdef` files, including `Editor/Plugins/MonoHook/MonoHook.asmdef` and editor assemblies that consume the hook support.
- **Native plugin**: `Editor/Plugins/MonoHook/Plugins/` includes the macOS `libMonoHookUtils_OSX.dylib` and `Utils.cpp`, matching the upstream implementation.
- **Usage boundary**: Editor scripts that need hook support can reference the `MonoHook` namespace, such as `MethodHook`. Keep product logic decoupled from MonoHook, and revalidate hook targets after upgrading Unity minor versions.

When `allowUnsafeCode` is enabled, Unity allows that assembly to compile pointer and unmanaged-memory access code. This is required for MonoHook to work. Disabling the option tightens the assembly boundary, but any source that depends on `unsafe` code will fail to compile and MonoHook-based IMGUI/Editor method interception will be unavailable. Leave it enabled unless those hook features are intentionally removed.

See `NOTICE.md` for the third-party component notice.
