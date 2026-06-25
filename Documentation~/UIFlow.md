# UIFlow Guide

UIFlow is the optional UI automation add-on shipped with upilot. It runs YAML-defined tests against Unity Editor `EditorWindow` UI.

Use this guide for UIFlow-specific setup, YAML authoring, action categories, execution modes, reports, and limits. The root `README.md` stays focused on upilot installation and basic usage.

## Scope

UIFlow targets:

- Unity Editor windows built with UIToolkit.
- Selected IMGUI EditorWindow workflows through `imgui_*` actions.
- Local headed debugging and MCP-driven agent validation.

UIFlow does not target:

- Game View runtime UI.
- Play Mode gameplay testing.
- Pixel-level visual diffing.
- General web/CSS/browser automation.

## Requirements

- Unity `6000.0` or newer.
- Scripting define symbol: `UNITYPILOT_ENABLE_UIFLOW`.
- Unity packages:
  - `com.unity.inputsystem`
  - `com.unity.ui`
  - `com.unity.ui.test-framework`
  - `com.unity.test-framework`

On Unity 2022, keep UIFlow disabled. upilot core MCP tools still work without UIFlow.

## Enable UIFlow

1. Install upilot.
2. Add `UNITYPILOT_ENABLE_UIFLOW` in **Project Settings > Player > Scripting Define Symbols**.
3. Install the UIFlow dependency packages listed above.
4. Let Unity recompile.
5. Open:

```text
upilot/UIFlow/Test Runner
```

## Minimal YAML

```yaml
fixture:
  host_window: ExampleBasicLoginWindow
steps:
  - wait_for_element:
      selector: "#username-input"
      timeout: "5s"
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

Prefer `.yaml` file extensions. `.yml` is not the supported convention.

## Selectors

Recommended selector order:

1. Stable `name` selectors: `#login-button`.
2. Semantic data selectors: `[data-role=primary]`.
3. Type selectors for broad checks: `Button`, `Label`, `TextField`.
4. Class selectors only when they are stable and intentionally part of test semantics.

UIToolkit page authors should give every important interactive element a unique `name`.

## Supported Capability List

| Area | Examples |
| --- | --- |
| Pointer | `click`, `double_click`, `hover`, `drag`, `scroll`, `open_context_menu`, `open_popup_menu` |
| Keyboard/Input | `focus`, `press_key`, `type_text`, `type_text_fast` |
| Fields | `set_value`, `set_slider`, `select_option`, `toggle_mask_option`, `set_bound_value`, `assert_bound_value` |
| Collections | `select_list_item`, `drag_reorder`, `select_tree_item`, `sort_column`, `resize_column` |
| Layout/Navigation | `select_tab`, `close_tab`, `toggle_foldout`, `navigate_breadcrumb`, `set_split_view_size`, `page_scroller` |
| Waits/Assertions | `wait_for_element`, `assert_visible`, `assert_not_visible`, `assert_text`, `assert_text_contains`, `assert_value`, `assert_enabled`, `assert_disabled`, `assert_property` |
| Reporting | screenshots, Markdown reports, JSON reports, failure attachments |
| IMGUI | `imgui_click`, `imgui_type`, `imgui_focus`, `imgui_scroll`, `imgui_select_option`, `imgui_press_key`, `imgui_assert_*`, `imgui_wait` |

For the full action table, see `Documentation~/00-API速查与最佳实践.md`.

## Execution Modes

Headed local run:

```text
upilot/UIFlow/Test Runner
```

Use this for visual debugging, highlighting, step mode, and inspecting selector matches.

MCP-driven runs:

- Use the upilot MCP server endpoint: `http://127.0.0.1:8011/mcp`.
- Run YAML only through MCP when agent validation is required.
- Keep headed mode enabled for YAML MCP verification.

Batch runs should be chunked. Do not send more than 15 YAML files in a single UIFlow batch call.

## Reports

Default MCP report root:

```text
Reports/upilot/UIFlowMcp
```

Outputs include Markdown summaries, JSON results, per-case reports, and failure screenshots.

## Page Authoring Rules

For reliable automation:

- Give key elements unique lower-kebab-case names, such as `username-input`, `login-button`, `status-label`.
- Put assertion text in stable named elements instead of only logging to Console.
- Prefer `MouseUpEvent` handlers for button logic in sample/test windows.
- Reset page state when the host window opens or `PrepareForAutomatedTest()` runs.
- Use `wait_for_element` before assertions on dynamic UI.

See `Documentation~/01-UnityUIFlow-UXML-USS自动化开发规范.md` and `Documentation~/02-UnityUIFlow-新页面接入最小模板.md` for page integration details.

## Known Boundaries

UIFlow V1 intentionally does not claim full coverage for:

- Object Picker drag-and-drop in `ObjectField`.
- `CurveField` and `GradientField` editor popups.
- `ToolbarPopupSearchField` result popups.
- Full semantic navigation for `ToolbarBreadcrumbs`.
- Direct automation of every generated child inside `PropertyField` / `InspectorElement`.
- System clipboard, IME, multi-window choreography, and pixel-level visual diffing.

Detailed coverage notes:

- `Documentation~/00-UIToolkit控件自动化覆盖与限制说明.md`
- `Documentation~/00-IMGUI控件自动化覆盖与限制说明.md`

## Related Docs

- Root upilot README: `README.md`
- Chinese UIFlow guide: `Documentation~/UIFlow.zh-CN.md`
- API quick reference: `Documentation~/00-API速查与最佳实践.md`
- Agent/MCP rules: `Documentation~/03-UnityUIFlow-Agent-MCP测试强制规范.md`
