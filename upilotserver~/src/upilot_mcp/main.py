from __future__ import annotations

"""Legacy entrypoint kept only for backward compatibility.

Do not use this module to start the MCP server in normal workflows.
It starts only the Unity bridge WebSocket server on a fixed port and does not
support MCP transport selection (`stdio` / `http`) or configurable ports.

Use one of these instead:
- `python run_upilot_mcp.py ...`
- `python -m upilot_mcp.mcp_main ...`
- `upilot-mcp ...`
"""

import asyncio
import warnings

from .server import WsOrchestratorServer


async def main() -> None:
    warnings.warn(
        "upilot_mcp.main is a legacy debug entrypoint. "
        "Use run_upilot_mcp.py or upilot_mcp.mcp_main instead.",
        DeprecationWarning,
        stacklevel=2,
    )
    server = WsOrchestratorServer(host="127.0.0.1", port=8765)
    print("[UPilot MCP][LEGACY] WS server listening at ws://127.0.0.1:8765")
    print("[UPilot MCP][LEGACY] Use run_upilot_mcp.py for stdio/http MCP startup.")
    await server.start()


if __name__ == "__main__":
    asyncio.run(main())
