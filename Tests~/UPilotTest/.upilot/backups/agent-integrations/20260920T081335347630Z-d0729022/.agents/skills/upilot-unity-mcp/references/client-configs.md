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

## Restricted Client Handshake

The stable entry points are `unity_tools_find` and `unity_tool_call`. A client may
expose only these two functions to its agent. Discover the exact tool, then proxy
its documented arguments; the existing Registry and safety gates remain authoritative.

After installing the Python server package, run the read-only handshake against
the existing server (this command does not start or restart Unity or the server):

```powershell
python -m upilot_mcp.client_probe --url http://127.0.0.1:8011/mcp --project D:\upilot\Tests~\UPilotTest
```

The report separates HTTP initialization, `tools/list` visibility, discovery,
real proxy execution, and exact project identity. `clientToolListInjected` stays
`unknown`: the probe cannot observe another AI client's injected tools. If
registration changed, refresh that client's MCP connection/tool list, perform a
real read-only proxy call there, and verify the returned project before writes.

## Project Skill discovery

- Codex: `.agents/skills/upilot-unity-mcp`
- Claude Code: `.claude/skills/upilot-unity-mcp`
- Cursor: `.agents/skills/upilot-unity-mcp`
- OpenCode: `.agents/skills/upilot-unity-mcp`

Cursor officially discovers both `.agents/skills` and `.cursor/skills`, and also reads compatible Claude/Codex directories. UPilot uses `.agents/skills` as the shared Codex/Cursor/OpenCode install to reduce managed copies. If multiple compatible roots contain a Skill with the same name, keep their contents identical and refresh the affected client after updates.

OpenCode project MCP config belongs in `opencode.json` (or an existing `opencode.jsonc`) under `mcp.upilot` with `type: "remote"`, the project HTTP `/mcp` URL, `enabled: true`, and a tool-discovery timeout of at least 30000 ms. OpenCode natively reads project `AGENTS.md` and discovers `.agents/skills`, `.claude/skills`, and `.opencode/skills`. UPilot shares the `.agents/skills` copy with Codex and Cursor. When `.claude/skills` also contains `upilot-unity-mcp`, keep the physical copies byte-equivalent or disable OpenCode's Claude Skill compatibility with `OPENCODE_DISABLE_CLAUDE_CODE_SKILLS=1`; different hashes are a conflict, not a second Skill.

The shared Skill contains on-demand tool routing, safety, compile/test, long-operation, recovery, and evidence workflows. Keep stable project-wide constraints in Agent rules instead of duplicating the Skill body there.
