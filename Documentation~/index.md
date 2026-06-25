# UIFlow Documentation

## Table of Contents

- [Overview](#overview)
- [Quick Start](#quick-start)
- [Writing YAML Test Cases](#writing-yaml-test-cases)
- [Selectors](#selectors)
- [Actions](#actions)
- [Fixtures](#fixtures)
- [Headed Mode](#headed-mode)
- [CLI / CI Mode](#cli--ci-mode)
- [Custom Actions](#custom-actions)

## Overview

UIFlow is the optional YAML UI automation add-on for upilot. For the current high-level guide, feature support list, requirements, and known boundaries, start with:

- `Documentation~/UIFlow.md`
- `Documentation~/UIFlow.zh-CN.md`

## Quick Start

1. Create a YAML file in your project (e.g., `Samples~/Yaml/my-test.yaml`).
2. Define a `fixture` with a `host_window` pointing to your `EditorWindow` type.
3. Add `steps` using built-in actions.
4. Run via **upilot/UIFlow/Test Runner** or through the MCP server.

## Writing YAML Test Cases

```yaml
fixture:
  host_window: MyEditorWindow
  custom_action_assemblies:
    - MyProject.Editor
steps:
  - wait_for_element:
      selector: "#submit-button"
      timeout_ms: 5000
  - click:
      selector: "#submit-button"
  - assert_text:
      selector: "#result-label"
      text: "Success"
```

## Selectors

UIFlow supports a CSS-like selector syntax:

- `#name` — match by `name` property
- `.class` — match by USS class
- `[data-role=primary]` — match by `userData` dictionary key/value
- `Panel > Container` — parent/child hierarchy
- `:visible` — filter only visible elements

**Best practice**: Always assign unique `name` to interactive elements in UXML.

## Actions

Common built-in actions include:

| Action | Description |
|--------|-------------|
| `click` | Mouse click on element |
| `type_text` / `type_text_fast` | Keyboard input |
| `drag` | Drag-and-drop between elements |
| `assert_text` | Verify element text content |
| `assert_property` | Verify element style or trait |
| `wait_for_element` | Wait until element appears |
| `screenshot` | Capture EditorWindow image |
| `execute_command` | Invoke Unity MenuItem by path |

See `Documentation~/00-API速查与最佳实践.md` for the full action list.

## Fixtures

For C#-driven tests, inherit from `UnityUIFlowFixture<TWindow>`:

```csharp
using UnityUIFlow;

public class MyWindowTests : UnityUIFlowFixture<MyEditorWindow>
{
    [UnityTest]
    public IEnumerator SmokeTest()
    {
        yield return RunYamlAsync("Samples~/Yaml/smoke.yaml");
    }
}
```

## Headed Mode

Open **upilot/UIFlow/Test Runner** to:
- Browse and run single YAML files or full directories.
- Use Step mode to pause after each action.
- Inspect element hierarchy and selector matches in real time.

## CLI / CI Mode

Run from command line (do **not** use `-batchmode`):

```powershell
Unity.exe -projectPath $PWD `
  -quit `
  -executeMethod UnityUIFlow.UnityUIFlowCliEntry.RunAllFromCommandLine `
  -unityUIFlow.headed false `
  -unityUIFlow.reportPath ./Reports
```

## Custom Actions

Implement `IAction` and register via `ActionRegistry.Register`:

```csharp
public class MyCustomAction : IAction
{
    public string ActionName => "my_custom";
    public Task ExecuteAsync(StepContext ctx, Dictionary<string, object> args) { ... }
}
```

Register in a `[InitializeOnLoad]` static constructor or host window setup.
