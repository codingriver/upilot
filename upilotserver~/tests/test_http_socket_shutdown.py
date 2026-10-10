from __future__ import annotations

import asyncio
import os
import socket
import struct
import sys
from unittest.mock import AsyncMock

import pytest
from uvicorn import Config, Server

from upilot_mcp import mcp_stdio_server as runtime


def _linger(sock):
    fmt = "HH" if os.name == "nt" else "ii"
    return struct.unpack(fmt, sock.getsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.calcsize(fmt)))


@pytest.mark.parametrize("host", ["127.0.0.1", "::1", "localhost"])
def test_http_prebinding_preserves_address_family_and_platform_defaults(host):
    if host == "::1" and not socket.has_ipv6:
        pytest.skip("IPv6 unavailable")
    async def exercise():
        sockets = await runtime._bind_http_sockets(host, 0)
        try:
            assert sockets
            for sock in sockets:
                assert _linger(sock) == (1, 0)
                assert not sock.getblocking()
                assert sock.getsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR) == (os.name == "posix" and sys.platform != "cygwin")
                if sock.family == socket.AF_INET6:
                    assert sock.getsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY) == 1
            if host == "localhost":
                expected = {info[0] for info in await asyncio.get_running_loop().getaddrinfo(
                    host, 0, type=socket.SOCK_STREAM, flags=socket.AI_PASSIVE)}
                assert {sock.family for sock in sockets} == expected
        finally:
            for sock in sockets:
                sock.close()
    asyncio.run(exercise())


@pytest.mark.parametrize("stage", ["create", "set", "get", "mismatch", "bind", "nonblocking"])
def test_partial_http_socket_failure_releases_all_and_preserves_error(monkeypatch, stage):
    async def exercise():
        loop = asyncio.get_running_loop()
        monkeypatch.setattr(loop, "getaddrinfo", AsyncMock(return_value=[
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("127.0.0.1", 10)),
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("127.0.0.1", 11)),
        ]))
        error = OSError(10022, f"original {stage} error")
        created = []
        class FakeSocket:
            closed = False
            def setsockopt(self, level, option, value):
                if len(created) == 2 and stage == "set":
                    raise error
                if option == socket.SO_LINGER:
                    self.linger = value
            def getsockopt(self, *args):
                if len(created) == 2 and stage == "get":
                    raise error
                if len(created) == 2 and stage == "mismatch":
                    return bytes(len(self.linger))
                return self.linger
            def bind(self, address):
                if len(created) == 2 and stage == "bind":
                    raise error
                self.address = address
            def setblocking(self, value):
                if len(created) == 2 and stage == "nonblocking":
                    raise error
            def getsockname(self):
                return self.address
            def close(self):
                self.closed = True
        def make(*args):
            if len(created) == 1 and stage == "create":
                raise error
            sock = FakeSocket()
            created.append(sock)
            return sock
        # Limit the patch to the binder; asyncio.Runner still needs real sockets.
        with monkeypatch.context() as patch:
            patch.setattr(runtime.socket, "socket", make)
            with pytest.raises(RuntimeError if stage == "mismatch" else OSError) as caught:
                await runtime._bind_http_sockets("localhost", 10)
        if stage != "mismatch":
            assert caught.value is error
        assert created and all(sock.closed for sock in created)
    asyncio.run(exercise())


def test_http_serve_failure_closes_untransferred_sockets(monkeypatch):
    async def exercise():
        sockets = await runtime._bind_http_sockets("127.0.0.1", 0)
        monkeypatch.setattr(runtime, "_bind_http_sockets", AsyncMock(return_value=sockets))
        error = OSError(10022, "uvicorn startup failed")
        class FailedServer:
            started = False
            async def serve(self, *, sockets):
                assert all(_linger(sock) == (1, 0) for sock in sockets)
                raise error
        server = FailedServer()
        with pytest.raises(OSError) as caught:
            await runtime._serve_http_sockets(server, "127.0.0.1", 0)
        assert caught.value is error
        assert not server.started
        assert all(sock.fileno() == -1 for sock in sockets)
    asyncio.run(exercise())


def test_new_http_instance_cannot_steal_live_listener():
    async def exercise():
        sockets = await runtime._bind_http_sockets("127.0.0.1", 0)
        sock = sockets[0]
        sock.listen()
        try:
            with pytest.raises(OSError) as caught:
                await runtime._bind_http_sockets("127.0.0.1", sock.getsockname()[1])
            assert caught.value.errno is not None
            assert sock.fileno() != -1
            assert _linger(sock) == (1, 0)
        finally:
            sock.close()
    asyncio.run(exercise())


def test_real_uvicorn_inheritance_keepalive_and_normal_shutdown(monkeypatch):
    async def exercise():
        async def app(scope, receive, send):
            assert scope["type"] == "http"
            await send({"type": "http.response.start", "status": 200,
                        "headers": [(b"content-length", b"2")]})
            await send({"type": "http.response.body", "body": b"ok"})
        config = Config(app, lifespan="off", log_level="warning", timeout_keep_alive=30)
        config.load()  # Keep the installed default HTTP backend, not a test backend.
        protocol = config.http_protocol_class
        original = protocol.connection_made
        observed = []
        def connection_made(self, transport):
            observed.append(_linger(transport.get_extra_info("socket")))
            original(self, transport)
        monkeypatch.setattr(protocol, "connection_made", connection_made)
        sockets = await runtime._bind_http_sockets("127.0.0.1", 0)
        port = sockets[0].getsockname()[1]
        monkeypatch.setattr(runtime, "_bind_http_sockets", AsyncMock(return_value=sockets))
        server = Server(config)
        task = asyncio.create_task(runtime._serve_http_sockets(server, "127.0.0.1", port))
        writer = None
        try:
            async with asyncio.timeout(5):
                while not server.started:
                    if task.done():
                        await task
                        pytest.fail("Uvicorn exited without starting")
                    await asyncio.sleep(0.01)
                reader, writer = await asyncio.open_connection("127.0.0.1", port)
                for _ in range(2):
                    writer.write(b"GET / HTTP/1.1\r\nHost: localhost\r\n\r\n")
                    await writer.drain()
                    assert b"200 OK" in await reader.readuntil(b"\r\n\r\n")
                    assert await reader.readexactly(2) == b"ok"
                assert observed == [(1, 0)]  # request completion didn't close TCP
                if os.name == "nt":
                    assert isinstance(asyncio.get_running_loop(), asyncio.ProactorEventLoop)
                server.should_exit = True
                await task
                try:
                    assert await reader.read() == b""
                except ConnectionResetError:
                    pass  # Expected cost of persistent zero-timeout linger.
            assert all(sock.fileno() == -1 for sock in sockets)
        finally:
            server.should_exit = True
            if not task.done():
                await asyncio.wait_for(task, 5)
            if writer is not None:
                writer.close()
                try:
                    await writer.wait_closed()
                except ConnectionResetError:
                    pass
    asyncio.run(exercise())
