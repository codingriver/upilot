# Client Configs

- MCP endpoint: `http://127.0.0.1:8011/mcp`
- Health endpoint: `http://127.0.0.1:8011/health`
- WebSocket ports are internal Bridge transport and must not be client URLs.

Codex project config:

```toml
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
startup_timeout_sec = 30
tool_timeout_sec = 300
```

Keep one registration per endpoint. Run `unity_client_config_diagnose` to detect duplicate endpoints, internal ports, wrong HTTP ports, and low timeouts.

After server tool registration changes, restart or refresh the MCP client so its injected tool list is current.
