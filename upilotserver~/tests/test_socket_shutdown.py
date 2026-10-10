from __future__ import annotations

import asyncio
import os
import socket
import struct
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

import websockets
from websockets.exceptions import ConnectionClosed

from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp import server as ws_runtime, socket_shutdown


class _FakeSocket:
    def __init__(self) -> None:
        self.closed = False
        self.sockopts: list[tuple[int, int, bytes]] = []
        self.events = []
        self.close_count = 0

    def setsockopt(self, level: int, optname: int, value: bytes) -> None:
        assert not self.closed
        self.events.append("set")
        self.sockopts.append((level, optname, value))

    def getsockopt(self, level, optname, size):
        assert not self.closed
        self.events.append("verify")
        return self.sockopts[-1][2]

    def getsockname(self):
        return ("127.0.0.1", 8765)

    def fileno(self) -> int:
        return -1 if self.closed else 1

    def close(self) -> None:
        self.close_count += 1
        self.closed = True


class _FakeTransport:
    def __init__(self) -> None:
        self.aborted = False
        self.socket = _FakeSocket()

    def abort(self) -> None:
        self.socket.events.append("abort")
        self.aborted = True
        # Model transports that immediately close/invalidate on abort.
        self.socket.close()

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
        self.transport.socket.events.append("fail")
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
    assert websocket.transport.socket.close_count == 1
    assert websocket.transport.socket.events == ["set", "verify", "fail", "abort"]


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


@pytest.mark.parametrize("platform,fmt", [("nt", "HH"), ("posix", "ii")])
def test_abortive_linger_encoding_and_readback(monkeypatch, platform, fmt):
    monkeypatch.setattr(socket_shutdown, "os", SimpleNamespace(name=platform))
    sock = _FakeSocket()
    assert socket_shutdown.enable_abortive_close(sock) == (1, 0)
    assert sock.sockopts == [(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack(fmt, 1, 0))]
    assert sock.events == ["set", "verify"]
    assert not sock.closed


@pytest.mark.parametrize("stage", ["set", "get"])
def test_abortive_linger_preserves_original_socket_error(stage):
    sock = _FakeSocket()
    error = OSError(10022, "original socket failure")
    def fail(*args):
        raise error
    if stage == "set":
        sock.setsockopt = fail
    else:
        sock.getsockopt = fail
    with pytest.raises(OSError) as caught:
        socket_shutdown.enable_abortive_close(sock)
    assert caught.value is error
    assert not sock.closed  # closure belongs to caller / transport


def test_abortive_linger_rejects_readback_mismatch():
    sock = _FakeSocket()
    sock.getsockopt = lambda *args: struct.pack("HH" if os.name == "nt" else "ii", 0, 0)
    with pytest.raises(RuntimeError, match=r"got \(0, 0\)"):
        socket_shutdown.enable_abortive_close(sock)


def test_force_abort_still_runs_when_linger_verification_fails(caplog):
    websocket = _FakeWebSocket()
    websocket.transport.socket.getsockopt = lambda *args: b""
    WsOrchestratorServer()._force_disconnect_websocket(websocket, reason="readback failed")
    assert websocket.transport.aborted
    assert websocket.transport.socket.close_count == 1
    assert "abortive close unverified" in caplog.text
    assert "verified=True" not in caplog.text


@pytest.mark.parametrize("failure", ["set", "mismatch", "start"])
def test_ws_listener_failure_closes_all_sockets_without_publishing_ready(monkeypatch, failure):
    async def exercise():
        listeners = [_FakeSocket(), _FakeSocket()]
        error = OSError(10022, "listener startup failed")
        if failure == "set":
            def fail(*args):
                raise error
            listeners[1].setsockopt = fail
        elif failure == "mismatch":
            listeners[1].getsockopt = lambda *args: struct.pack("HH" if os.name == "nt" else "ii", 0, 0)
        class FakeServer:
            sockets = listeners
            started = False
            async def start_serving(self):
                assert all(sock.events[:2] == ["set", "verify"] for sock in listeners)
                self.started = True
                raise error
            def close(self):
                for sock in listeners:
                    sock.close()
            async def wait_closed(self):
                pass
        fake = FakeServer()
        serve = AsyncMock(return_value=fake)
        monkeypatch.setattr(ws_runtime.websockets, "serve", serve)
        server = WsOrchestratorServer()
        with pytest.raises(RuntimeError if failure == "mismatch" else OSError) as caught:
            await server.start()
        if failure != "mismatch":
            assert caught.value is error
        assert serve.call_args.kwargs["start_serving"] is False
        assert fake.started is (failure == "start")
        assert all(sock.closed for sock in listeners)
        assert not server._listening_event.is_set()
        assert server._server is None
    asyncio.run(exercise())


def test_real_ws_accepted_socket_inherits_verified_linger():
    async def exercise():
        server = WsOrchestratorServer(port=0)
        accepted = []
        fmt = "HH" if os.name == "nt" else "ii"
        async def echo(websocket):
            sock = websocket.transport.get_extra_info("socket")
            accepted.append(struct.unpack(fmt, sock.getsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.calcsize(fmt))))
            await websocket.send(await websocket.recv())
        server._handle = echo
        serving = asyncio.create_task(server.start())
        try:
            await server.wait_until_listening(timeout_s=5)
            port = server._server.sockets[0].getsockname()[1]
            async with websockets.connect(f"ws://127.0.0.1:{port}") as websocket:
                await websocket.send("normal WS roundtrip")
                assert await websocket.recv() == "normal WS roundtrip"
            assert accepted == [(1, 0)]
            if os.name == "nt":
                assert isinstance(asyncio.get_running_loop(), asyncio.ProactorEventLoop)
        finally:
            server.stop()
            await serving
    asyncio.run(exercise())


def test_new_ws_instance_cannot_steal_live_listener():
    async def exercise():
        incumbent = WsOrchestratorServer(port=0)
        async def echo(websocket):
            await websocket.send(await websocket.recv())
        incumbent._handle = echo
        task = asyncio.create_task(incumbent.start())
        try:
            await incumbent.wait_until_listening(timeout_s=5)
            port = incumbent._server.sockets[0].getsockname()[1]
            competitor = WsOrchestratorServer(port=port)
            with pytest.raises(OSError):
                await competitor.start()
            assert not competitor._listening_event.is_set()
            async with websockets.connect(f"ws://127.0.0.1:{port}") as websocket:
                await websocket.send("incumbent still owns listener")
                assert await websocket.recv() == "incumbent still owns listener"
        finally:
            incumbent.stop()
            await task
    asyncio.run(exercise())
