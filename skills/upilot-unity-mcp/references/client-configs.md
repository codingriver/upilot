# Client Configs

- MCP endpoint: `http://127.0.0.1:8011/mcp`
- Health endpoint: `http://127.0.0.1:8011/health`
- Third-party AI tools must use Streamable HTTP only.
- WebSocket ports are internal MCP Server <-> Unity Bridge transport and must not be client URLs.
- Do not configure stdio, `command`/`args`, or a local MCP Server process in a third-party AI client.

Codex project config:

```toml
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
startup_timeout_sec = 30
tool_timeout_sec = 300
```

Keep one registration per endpoint. Run `unity_client_config_diagnose` to detect duplicate endpoints, internal ports, wrong HTTP ports, and low timeouts.

For multiple concurrent Unity projects, assign each project a unique internal HTTP/WebSocket port pair and a distinct client registration name, for example `upilot-game-a` at port 8011 and `upilot-game-b` at port 8012. The AI client receives only the corresponding HTTP `/mcp` URLs; it never receives either WebSocket port. Verify `paths.unityProjectAbsolute` after every connection.

After server tool registration changes, restart or refresh the MCP client so its injected tool list is current.
