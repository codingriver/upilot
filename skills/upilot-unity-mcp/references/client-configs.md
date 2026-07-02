# Client Configs

Use this when installing or diagnosing upilot MCP client setup.

## Endpoints

- MCP Streamable HTTP endpoint: `http://127.0.0.1:8011/mcp`
- Health endpoint: `http://127.0.0.1:8011/health`
- Internal Unity bridge WebSocket: `127.0.0.1:8765`

Do not configure MCP clients to use WebSocket port `8765`; it is only for the Python server and Unity Editor bridge.

## Codex CLI

Use TOML:

```toml
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
startup_timeout_sec = 10
tool_timeout_sec = 60
```

## Claude Desktop, Claude Code, Cursor

Use the client-specific JSON shape from the root `README.md`.

For HTTP-capable clients, configure the URL `http://127.0.0.1:8011/mcp` after the upilot server is already running.

For local-command clients, launch:

```text
python <PATH_TO_UPILOT_REPO>/upilotserver~/run_upilot_mcp.py --transport stdio --port 8765
```

Set `PYTHONUTF8=1` when paths or logs may contain non-ASCII text.

## Source Install

Install the Unity package from:

```text
https://github.com/codingriver/upilot.git
```

Install Python dependencies from:

```text
upilotserver~/requirements.txt
```

For automated setup, prefer `scripts/install_upilot.py`. It can clone upilot, update a Unity project's `Packages/manifest.json`, create a Python virtual environment for the MCP server, install this skill into `.agents/skills`, and optionally write Codex MCP configuration.
