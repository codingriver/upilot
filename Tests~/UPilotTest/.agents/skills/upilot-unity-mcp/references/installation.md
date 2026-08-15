# Installation

1. Verify the Unity project root contains `Packages/manifest.json`.
2. Add `io.github.codingriver.upilot` using an explicit stable release tag. For automated installation, pass the intended tag with `--upm-ref`, or use `--use-local-upm` when validating a local checkout.
3. If running the MCP Server from Python instead of the packaged executable, install the Python package from `upilotserver~` with `--setup-python`.
4. Install the repository skill into `.agents/skills/upilot-unity-mcp`.
5. Configure one MCP service named `upilot` at `http://127.0.0.1:8011/mcp`.
6. Open Unity and verify project identity with `unity_mcp_status`.

Automated install:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py \
  --unity-project <UNITY_PROJECT_ROOT> \
  --upm-ref <STABLE_RELEASE_TAG>
```

For a local repository checkout, replace `--upm-ref` with `--use-local-upm`. The installer deliberately has no default UPM version and does not infer one from `package.json`: a remote install must name its tag, branch, or commit explicitly, while the MCP Server may be distributed and versioned independently as an executable.

To write a Codex project registration, add `--write-codex-mcp project`. It writes only an HTTP URL. Use `--http-port` and `--mcp-name` when allocating a distinct endpoint for another Unity project. Never pass the Unity Bridge WebSocket port to a third-party AI client.

The core install keeps optional features disabled. When the user explicitly requests UPilot Flow, read `flow.md` before changing packages or scripting defines.

Do not overwrite an existing skill or MCP registration without checking its current content.

Unity Editor Agent Setup writes `.upilot-install.json` into skill directories that it manages. Later package versions may refresh a managed skill automatically only when the recorded content hash still matches. Legacy, unmanaged, or locally customized skill directories are preserved unless the user explicitly requests overwrite.
