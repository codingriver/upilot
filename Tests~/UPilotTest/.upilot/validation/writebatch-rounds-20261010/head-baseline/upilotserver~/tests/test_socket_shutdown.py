from __future__ import annotations

import asyncio

import websockets
from websockets.exceptions import ConnectionClosed

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


def test_bridge_accepts_result_frame_larger_than_websocket_default() -> None:
    async def exercise() -> None:
        server = WsOrchestratorServer(port=0)

        async def receive_frame(websocket) -> None:
            payload = await websocket.recv()
            await websocket.send(str(len(payload)))

        server._handle = receive_frame
        serving = asyncio.create_task(server.start())
        try:
            await server.wait_until_listening(timeout_s=5)
            port = server._server.sockets[0].getsockname()[1]
            async with websockets.connect(f"ws://127.0.0.1:{port}") as websocket:
                payload = "x" * 1_316_191
                await websocket.send(payload)
                assert await websocket.recv() == str(len(payload))
        finally:
            server.stop()
            await serving

    asyncio.run(exercise())


def test_bridge_rejects_frame_larger_than_four_megabytes_without_business_retry() -> None:
    async def exercise() -> None:
        server = WsOrchestratorServer(port=0)
        received = []

        async def receive_frame(websocket) -> None:
            try:
                received.append(await websocket.recv())
            except ConnectionClosed:
                pass

        server._handle = receive_frame
        serving = asyncio.create_task(server.start())
        try:
            await server.wait_until_listening(timeout_s=5)
            port = server._server.sockets[0].getsockname()[1]
            async with websockets.connect(f"ws://127.0.0.1:{port}", max_size=None) as websocket:
                await websocket.send("x" * (4 * 1024 * 1024 + 1))
                try:
                    await websocket.recv()
                except ConnectionClosed:
                    pass
            assert received == []
        finally:
            server.stop()
            await serving

    asyncio.run(exercise())
