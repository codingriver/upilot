---
name: upilot-unity-mcp
description: Unity Editor automation through the upilot MCP server. Use when an agent needs to inspect, control, diagnose, or modify Unity Editor projects via MCP, Codex, Claude, Cursor, or other AI clients, including compile errors, Console logs, scenes, GameObjects, components, assets, prefabs, packages, tests, screenshots, UIFlow YAML, UIToolkit EditorWindow automation, reflection_eval, and Roslyn execution.
---

# upilot Unity MCP

Use upilot when working in a Unity project that has the `io.github.codingriver.upilot` UPM package installed, or when the user wants to install a Unity Editor MCP server from `https://github.com/codingriver/upilot`.

## Standard Start

For any Unity Editor task:

1. Call `unity_mcp_status`.
2. Verify `paths.unityProjectAbsolute` matches the intended Unity project.
3. Call `unity_ensure_ready` before editor mutations.
4. Perform the task with the narrowest matching upilot tool.
5. After code or asset writes, call `unity_sync_after_disk_write` and a compile/wait tool as appropriate.

## Routing

- For installing upilot into a Unity project, read `references/installation.md`.
- For common task flows, read `references/workflows.md`.
- For tool choice, read `references/tool-routing.md`.
- For client setup or transport confusion, read `references/client-configs.md`.
- For destructive operations, timeout recovery, or unavailable features, read `references/safety.md`.

## Core Rules

- Prefer Streamable HTTP endpoint `http://127.0.0.1:8011/mcp` when the client supports remote MCP.
- Treat WebSocket port `8765` as internal to the Python server and Unity Editor bridge; do not use it as the MCP client URL.
- Prefer existing compiled entry points through `unity_reflection_call`; use `reflection_eval` for one-line diagnostics; use `unity_roslyn_execute` only when dynamic code is truly needed.
- Use UIFlow only for YAML-driven Unity EditorWindow automation. Require Unity 6+ and `UPILOT_ENABLE_UIFLOW`; in Unity 2022, continue with core upilot tools.

## References

- Install and client setup: root `README.md`
- MCP server development and transport details: `upilotserver~/DEVELOPMENT.md`
- Tool availability and validation status: `Documentation~/ToolStatus.md`
- UIFlow YAML automation guide: `Documentation~/UIFlow.md`
