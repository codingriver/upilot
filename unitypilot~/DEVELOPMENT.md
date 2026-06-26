# upilot MCP Server Development

This document is for developers working on the Python MCP server that ships with `upilot`.
For package installation and normal user setup, start with the root `../README.md`.

The English file is the default development document. The Chinese version is `DEVELOPMENT.zh-CN.md`.

## Role

`upilot` is the Unity package and public product name. The Python code in this directory provides the MCP server used by the Unity package.

The directory is still named `unitypilot~/` during the rename so existing Unity package layouts, scripts, installed projects, and legacy integrations keep working. New user-facing commands and documentation should use `upilot` naming.

The Python server:

- exposes MCP tools over stdio or Streamable HTTP;
- accepts Unity Editor bridge connections over WebSocket;
- forwards tool calls for editor status, console logs, compilation, assets, scenes, GameObjects, components, windows, screenshots, packages, menus, scripts, prefabs, materials, builds, tests, and diagnostics;
- forwards UIFlow YAML automation calls when the Unity side enables UIFlow.

## Directory Map

```text
unitypilot~/
  run_upilot_mcp.py          # Preferred source launcher.
  run_unitypilot_mcp.py      # Legacy-compatible launcher.
  pyproject.toml             # Python package metadata and console scripts.
  requirements.txt           # Runtime dependencies for source runs.
  mcp.example.json           # Example local MCP client config.
  src/unitypilot_mcp/        # Server package.
  tests/                     # Pytest tests.
  scripts/                   # Acceptance and helper scripts.
  deploy/                    # Release/deployment helper scripts.
  e2e-specs/                 # Example UI/editor E2E specs.
```

## Requirements

- Python `3.11` or newer.
- Unity package `io.github.codingriver.upilot`.
- Unity Editor opened in normal editor mode.
- A Unity Editor bridge configured to connect to the same WebSocket host and port as the Python server.

## Architecture

The server has two transport layers:

- **MCP transport**: exposed to MCP clients through either stdio or Streamable HTTP.
- **Unity bridge transport**: an internal WebSocket listener used by the Unity Editor bridge.

The common local development topology is:

```text
MCP client
  -> stdio or http://127.0.0.1:8011/mcp
  -> Python MCP server
  -> ws://127.0.0.1:8765
  -> Unity Editor bridge
```

The HTTP MCP endpoint is always the `/mcp` path. For example:

```text
http://127.0.0.1:8011/mcp
```

The WebSocket port `8765` is not the MCP client endpoint. It is the internal connection between the Python server and the Unity Editor bridge.

## Source Setup

From this directory:

```bash
python -m pip install -r requirements.txt
```

For editable development installs:

```bash
python -m pip install -e .[dev]
```

Editable installs expose these console scripts:

- `upilot`
- `upilot-mcp`
- `unitypilot-mcp` legacy alias

## Run From Source

Recommended Streamable HTTP run:

```bash
python run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

The MCP client URL is:

```text
http://127.0.0.1:8011/mcp
```

Stdio run for local-command MCP clients:

```bash
python run_upilot_mcp.py --transport stdio --port 8765
```

Legacy launcher:

```bash
python run_unitypilot_mcp.py --transport http --http-port 8011 --port 8765
```

Console script run after editable install:

```bash
upilot-mcp --transport http --http-port 8011 --port 8765
```

## Options

| CLI option | Environment variable | Default | Purpose |
| --- | --- | --- | --- |
| `--transport` | `UNITYPILOT_TRANSPORT` | `stdio` | MCP transport: `stdio` or `http`. |
| `--host` | `UNITYPILOT_HOST` | `127.0.0.1` | WebSocket host for the Unity Editor bridge. |
| `--port` | `UNITYPILOT_PORT` | `8765` | WebSocket port for the Unity Editor bridge. |
| `--http-host` | `UNITYPILOT_HTTP_HOST` | `127.0.0.1` | Streamable HTTP host. |
| `--http-port` | `UNITYPILOT_HTTP_PORT` | `8000` | Streamable HTTP port. |
| `--label` | none | current folder name | Display label used in Unity diagnostics. |
| `--log-file` | `UNITYPILOT_LOG_FILE` | unset | Optional log file path. |
| `--log-level` | `UNITYPILOT_LOG_LEVEL` | `DEBUG` | Python logging level. |

Use different `--port` values for multiple Unity projects running at the same time, for example `8765`, `8766`, and `8767`.

## MCP Client Configuration

For clients that support Streamable HTTP, start the server yourself and configure the HTTP URL:

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

For local-command clients, use stdio:

```json
{
  "mcpServers": {
    "upilot": {
      "command": "python",
      "args": [
        "<PATH_TO_UPILOT_REPO>/unitypilot~/run_upilot_mcp.py",
        "--transport",
        "stdio",
        "--port",
        "8765"
      ],
      "env": {
        "PYTHONUTF8": "1"
      }
    }
  }
}
```

See `mcp.example.json` for a fuller local-command example.

## Unity Editor Connection

The Python server is only one side of the bridge. The Unity Editor must also be open and connected to the same WebSocket endpoint.

Basic checks:

- The Python process is listening on the configured WebSocket host and port.
- The Unity Editor package is installed and the bridge is enabled.
- The Unity Editor is not in batchmode.
- The MCP tool `unity_mcp_status` reports `connected: true`.

If `unity_mcp_status` is reachable but reports that Unity is disconnected, the MCP server is running but the Unity Editor bridge is not connected.

## Tests

Run Python tests from this directory:

```bash
python -m pytest
```

Some tests start local server processes and bind ports. If a test fails because a port is already in use, stop the old process or rerun with a free port where the test supports it.

## Development Notes

- Keep `run_upilot_mcp.py` as the preferred source launcher.
- Keep `run_unitypilot_mcp.py` and `unitypilot-mcp` as compatibility aliases unless a migration plan explicitly removes them.
- Keep the MCP HTTP path as `/mcp`.
- Treat WebSocket `8765` as an internal Unity bridge port, not a client-facing MCP endpoint.
- Prefer additive tool aliases during rename work so older clients continue to function.
- Do not rename Python modules from `unitypilot_mcp` casually; installed projects and scripts may still import them.

## Troubleshooting

### HTTP root works but MCP calls fail

Use the full MCP endpoint:

```text
http://127.0.0.1:8011/mcp
```

The root URL is only a human-facing landing page or health hint.

### Unity is not connected

Confirm the Unity Editor is open, the package is installed, and the bridge connects to the same WebSocket port passed through `--port`.

### Port conflict

Change the WebSocket port and keep Unity configured to the same value:

```bash
python run_upilot_mcp.py --transport http --http-port 8011 --port 8766
```

For multiple HTTP servers, also change `--http-port`.

### Non-ASCII paths or text look broken

Set Python UTF-8 mode for local-command clients:

```json
{
  "env": {
    "PYTHONUTF8": "1"
  }
}
```

## Related Documents

- Root package README: `../README.md`
- Chinese root README: `../README.zh-CN.md`
- Chinese version of this document: `DEVELOPMENT.zh-CN.md`
