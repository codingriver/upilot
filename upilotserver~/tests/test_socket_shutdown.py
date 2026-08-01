from __future__ import annotations

import asyncio

from upilot_mcp.server import WsOrchestratorServer


class _FakeSocket:
    def __init__(self) -> None:
        self.closed = False
        self.sockopts: list[tuple[int, int, bytes]] = []

    def setsockopt(self, level: int, optname: int, value: bytes) -> None:
        self.sockopts.append((level, optname, value))

    def fileno(self) -> int:
        return 1

    def close(self) -> None:
        self.closed = True


class _FakeTransport:
    def __init__(self) -> None:
        self.aborted = False
        self.socket = _FakeSocket()

    def abort(self) -> None:
        self.aborted = True

    def get_extra_info(self, name: str):
        if name == "socket":
            return self.socket
        return None


class _FakeWebSocket:
    remote_address = ("127.0.0.1", 8765)

    def __init__(self) -> None:
        self.transport = _FakeTransport()
        self.failed = False
        self.close_called = False
        self.wait_closed_called = False

    async def close(self, *args, **kwargs) -> None:
        self.close_called = True

    async def wait_closed(self) -> None:
        self.wait_closed_called = True

    def fail_connection(self) -> None:
        self.failed = True


def test_close_websocket_force_aborts_without_graceful_wait() -> None:
    server = WsOrchestratorServer()
    websocket = _FakeWebSocket()

    asyncio.run(server._close_websocket(websocket, reason="test shutdown"))

    assert websocket.close_called is False
    assert websocket.wait_closed_called is False
    assert websocket.failed is True
    assert websocket.transport.aborted is True
    assert websocket.transport.socket.closed is True
