# upilot MCP server

This directory contains the Python MCP server used by `upilot`.

The folder is still named `unitypilot~/` during the rename so existing Unity
package layouts, scripts, and installed projects keep working. New user-facing
commands should use `upilot` naming.

## Role

`upilot` is the Unity package and public product name.

The Python server:

- exposes MCP tools over stdio or Streamable HTTP;
- connects to the Unity Editor bridge over WebSocket;
- forwards tool calls for editor status, console, compilation, assets, scenes,
  windows, screenshots, packages, scripts, tests, and diagnostics;
- optionally forwards UIFlow YAML automation calls when the Unity side enables
  UIFlow.

## Requirements

- Python `3.11` or newer
- Unity package `io.github.codingriver.upilot`
- Unity Editor opened in normal editor mode

## Run From Source

Install dependencies:

```bash
python -m pip install -r requirements.txt
```

Start the recommended HTTP MCP server:

```bash
python run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

The HTTP MCP endpoint is:

```text
http://127.0.0.1:8011/mcp
```

The WebSocket port `8765` is the internal connection between this Python server
and the Unity Editor bridge.

## Console Scripts

For editable development installs:

```bash
python -m pip install -e .
upilot-mcp --transport http --http-port 8011 --port 8765
```

The package also exposes:

- `upilot`
- `upilot-mcp`
- `unitypilot-mcp` legacy alias

## Compatibility

The legacy launcher remains available:

```bash
python run_unitypilot_mcp.py --transport http --http-port 8011 --port 8765
```

Some internal Python modules and MCP tool aliases still use `unitypilot` or
`unityuiflow` names for compatibility. Public package identity, menu text,
panel title, and MCP server identity should use lowercase `upilot`.

## MCP Client Config

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

For the root Unity package README and Chinese documentation, see:

- `../README.md`
- `../README.zh-CN.md`
