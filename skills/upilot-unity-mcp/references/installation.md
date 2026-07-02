# Installation

Use this when a user asks an agent to install upilot into a Unity project.

## Recommended Agent Flow

1. Identify the Unity project root. It must contain `Packages/manifest.json`.
2. Clone upilot if the repository is not already present locally.
3. Add `io.github.codingriver.upilot` to the Unity project's `Packages/manifest.json`.
4. Create a Python virtual environment for `upilotserver~`.
5. Install the Python MCP server in editable mode.
6. Install the skill into the Unity project at `.agents/skills/upilot-unity-mcp`.
7. Ask the user to open Unity, then use `unity_mcp_status` after the MCP server is configured and running.

## Scripted Install

Run from a cloned upilot repository:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py --unity-project <UNITY_PROJECT_ROOT>
```

To clone upilot first:

```bash
python install_upilot.py --clone-to <TOOLS_DIR>/upilot --unity-project <UNITY_PROJECT_ROOT>
```

To install UIFlow dependencies too:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py --unity-project <UNITY_PROJECT_ROOT> --enable-uiflow
```

To configure project-scoped Codex MCP after creating the Python environment:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py --unity-project <UNITY_PROJECT_ROOT> --write-codex-mcp project
```

## What The Script Changes

- Unity project `Packages/manifest.json`
- Unity project `.agents/skills/upilot-unity-mcp`
- upilot Python venv under `upilotserver~/.venv` unless `--venv` is passed
- optional project or user Codex config when `--write-codex-mcp` is used

## Defaults

- UPM package: `io.github.codingriver.upilot`
- UPM source: `https://github.com/codingriver/upilot.git#v0.1.0`
- Python install: editable install from `upilotserver~`
- UIFlow package defaults come from this repository's validated test project and can be overridden with `--upm-dep name=version`.

## When To Stop

Stop and ask the user before overwriting an existing skill install unless `--force` is explicitly requested.

Stop if the Unity project root cannot be verified by `Packages/manifest.json`.
