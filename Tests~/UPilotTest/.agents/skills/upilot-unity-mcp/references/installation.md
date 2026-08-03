# Installation

1. Verify the Unity project root contains `Packages/manifest.json`.
2. Add `io.github.codingriver.upilot` at tag `v0.2.0`.
3. Install the Python package from `upilotserver~`.
4. Install the repository skill into `.agents/skills/upilot-unity-mcp`.
5. Configure one MCP service named `upilot` at `http://127.0.0.1:8011/mcp`.
6. Open Unity and verify project identity with `unity_mcp_status`.

Automated install:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py --unity-project <UNITY_PROJECT_ROOT>
```

The core install keeps optional features disabled. When the user explicitly requests UPilot Flow, read `flow.md` before changing packages or scripting defines.

Do not overwrite an existing skill or MCP registration without checking its current content.

Unity Editor Agent Setup writes `.upilot-install.json` into skill directories that it manages. Later package versions may refresh a managed skill automatically only when the recorded content hash still matches. Legacy, unmanaged, or locally customized skill directories are preserved unless the user explicitly requests overwrite.
