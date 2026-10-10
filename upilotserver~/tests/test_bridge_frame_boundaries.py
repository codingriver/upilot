"""Real loopback transport/production handler; simulated Bridge and OS identity only."""
from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from dataclasses import asdict
import hashlib
import json

import pytest
import websockets
from websockets.exceptions import ConnectionClosed

from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.models import WsMessage
from upilot_mcp.protocol import to_wire
from upilot_mcp.server import IDENTITY_CONTRACT_VERSION, WsOrchestratorServer

LIMIT = 4 * 1024 * 1024


def wire(session, kind, name, payload, command_id="fixture"):
    return json.dumps(to_wire(WsMessage(
        id=command_id, type=kind, name=name, payload=payload,
        timestamp=1, session_id=session,
    )), ensure_ascii=False, separators=(",", ":"))


async def receive(peer):
    return json.loads(await asyncio.wait_for(peer.recv(), 3))


@asynccontextmanager
async def loopback(project, compression="deflate"):
    server = WsOrchestratorServer(port=0, expected_project_path=str(project))
    server._probe_candidate_identity = lambda payload: {
        **payload, "accepted": True, "identityVerified": True,
        "verificationLevel": "test-verified",
    }
    peers, calls = [], []
    sent_commands = []
    actual_send = server.send_command

    async def observe_send(**kwargs):
        sent_commands.append((kwargs["command_id"], kwargs["name"]))
        return await actual_send(**kwargs)

    server.send_command = observe_send
    serving = asyncio.create_task(server.start())

    async def connect(session):
        port = server._server.sockets[0].getsockname()[1]
        peer = await websockets.connect(f"ws://127.0.0.1:{port}", close_timeout=1, compression=compression)
        peers.append(peer)
        assert peer.protocol.extensions == [], "Internal Bridge transport must not negotiate compression."
        await peer.send(wire(session, "hello", "session.hello", {
            "identityContractVersion": IDENTITY_CONTRACT_VERSION,
            "unityVersion": "6000.0", "projectPath": str(project),
            "platform": "windows", "processId": 101, "processCreatedAt": 202,
            "processRole": "mainEditor",
        }))
        assert (await receive(peer))["payload"]["accepted"] is True
        return peer

    dispatcher = CommandDispatcher(server, server.state)

    def dispatch(name):
        call = asyncio.create_task(dispatcher.call(
            "frame-fixture", name, {}, timeout_ms=5000,
        ))
        calls.append(call)
        return call

    try:
        await server.wait_until_listening(timeout_s=5)
        yield server, connect, dispatch
    finally:
        for call in calls:
            if not call.done():
                call.cancel()
        await asyncio.gather(*calls, return_exceptions=True)
        for peer in peers:
            await peer.close()
        server.stop()
        await asyncio.wait_for(serving, 5)
        # Observe the actual send path across reconnection, not only command records.
        mutation_ids = [command_id for command_id, name in sent_commands if name == "console.capture.start"]
        assert len(mutation_ids) <= 1


async def disconnected(server):
    async def observe():
        while server._ws is not None:
            await asyncio.sleep(0.01)
    await asyncio.wait_for(observe(), 3)


async def reject_and_reconnect(server, connect, dispatch, peer, raw, record, frozen=None):
    await peer.send(raw)
    with pytest.raises(ConnectionClosed) as caught:
        await asyncio.wait_for(peer.recv(), 3)
    assert caught.value.rcvd.code == 1009
    await disconnected(server)
    assert server._oversize_count == 1
    observation, = server._oversize_observations
    assert observation["sessionId"] == "before"
    assert observation["limitBytes"] == LIMIT and observation["closeCode"] == 1009
    assert observation["sentCloseCode"] == 1009
    assert observation["commandId"] == "unknown" and observation["observedAt"] > 0
    if frozen is not None:
        assert asdict(record) == frozen
    frozen = asdict(record)
    assert not server._pending and not server._suspended
    peer = await connect("after")
    successor = dispatch("test.status")
    command = await receive(peer)
    assert command["name"] == "test.status" and command["id"] != record.command_id
    # An old response cannot finish the successor or alter the original first error.
    await peer.send(wire("after", "result", "console.capture.start", {"late": True}, record.command_id))
    await peer.send(wire("after", "result", "test.status", {"runGuid": "successor"}, command["id"]))
    result = await asyncio.wait_for(successor, 3)
    assert result.ok and result.data == {"runGuid": "successor"}
    assert asdict(record) == frozen
    assert len(server.state.commands) == 2
    assert not server._pending and not server._suspended


@pytest.mark.parametrize("compression", [None, "deflate"])
@pytest.mark.parametrize("delta", [-1, 0, 1])
@pytest.mark.parametrize("shape", ["ascii", "unicode", "fragmented_unicode"])
def test_production_handler_utf8_message_boundary(tmp_path, delta, shape, compression):
    async def scenario():
        async with loopback(tmp_path, compression) as (server, connect, dispatch):
            peer = await connect("before")
            # This is a simulated mutation. No Unity or real Capture is contacted.
            call = dispatch("console.capture.start")
            command = await receive(peer)
            assert command["name"] == "console.capture.start"
            record = server.state.commands[command["id"]]
            envelope = wire("before", "result", command["name"], {"text": ""}, command["id"])
            available = LIMIT + delta - len(envelope.encode("utf-8"))
            unit = "x" if shape == "ascii" else "中🙂"
            count, remainder = divmod(available, len(unit.encode("utf-8")))
            text = unit * count + "x" * remainder
            raw = wire("before", "result", command["name"], {"text": text}, command["id"])
            assert len(raw.encode("utf-8")) == LIMIT + delta
            if shape != "ascii":
                assert len(raw) < LIMIT
            # Fragment in the middle of the text, not between separate JSON messages.
            sent = [raw[:len(raw)//2], raw[len(raw)//2:]] if shape == "fragmented_unicode" else raw
            if delta > 0:
                await reject_and_reconnect(server, connect, dispatch, peer, sent, record)
                result = await asyncio.wait_for(call, 3)
                assert not result.ok and result.error.code == "CONNECTION_LOST"
            else:
                await peer.send(sent)
                result = await asyncio.wait_for(call, 3)
                assert result.ok and record.status == "success"
                assert hashlib.sha256(result.data["text"].encode("utf-8")).digest() == hashlib.sha256(text.encode("utf-8")).digest()
                assert getattr(server, "_oversize_count", 0) == 0
                assert len(server.state.commands) == 1 and not server._pending
    asyncio.run(scenario())


@pytest.mark.parametrize("terminal", ["error", "result"])
def test_oversize_disconnect_preserves_original_terminal(tmp_path, terminal):
    async def scenario():
        async with loopback(tmp_path) as (server, connect, dispatch):
            peer = await connect("before")
            call = dispatch("console.capture.start")
            command = await receive(peer)
            payload = {"code": "FIXTURE_FIRST_ERROR", "message": "original failure"} if terminal == "error" else {"effectCount": 1}
            await peer.send(wire("before", terminal, command["name"], payload, command["id"]))
            result = await asyncio.wait_for(call, 3)
            assert result.ok is (terminal == "result")
            if terminal == "error":
                assert result.error.code == "FIXTURE_FIRST_ERROR"
            record = server.state.commands[command["id"]]
            await reject_and_reconnect(server, connect, dispatch, peer, "x" * (LIMIT + 1), record, asdict(record))
    asyncio.run(scenario())


def test_oversize_events_remain_bounded_and_inventory_incomplete(tmp_path, monkeypatch):
    from test_reliability_batch_inventory import _complete_inventory_service

    async def scenario():
        async with loopback(tmp_path / "wire") as (server, connect, dispatch):
            peer = await connect("before")
            for i in range(10):
                await peer.send(wire("before", "event", "bridge.payload_oversize", {
                    "sourceEvent": f"fixture.event.{i}", "actualBytes": LIMIT + i + 1,
                    "limitBytes": LIMIT,
                }))
            # Same socket ordering: completion proves all prior events were consumed.
            call = dispatch("test.status")
            command = await receive(peer)
            await peer.send(wire("before", "result", command["name"], {}, command["id"]))
            assert (await asyncio.wait_for(call, 3)).ok
            assert server._oversize_count == 10
            observations = server._oversize_observations
            assert len(observations) == 8
            assert [item["sourceEvent"] for item in observations] == [f"fixture.event.{i}" for i in range(2, 10)]
            assert all(item["commandId"] == "unknown" and item["sessionId"] == "before" for item in observations)
            service, _, calls = _complete_inventory_service(tmp_path / "inventory", monkeypatch)
            assert (await service.queue_inventory()).data["complete"] is True
            service.server._oversize_observations = observations
            result = await service.queue_inventory()
            assert result.ok and result.data["complete"] is False
            assert result.data["issues"] == ["BRIDGE_EVENT_OR_FRAME_TOO_LARGE"]
            assert result.data["sourceDetails"]["bridgeOversize"] == observations
            assert set(calls) == {"queue.snapshot", "test.status", "console.capture.observe"}
            assert not server._pending and not server._suspended
    asyncio.run(scenario())


@pytest.mark.parametrize("code", [1000, 1011, 1009])
def test_close_diagnostics_distinguish_peer_claim_from_other_disconnects(tmp_path, code):
    async def scenario():
        async with loopback(tmp_path) as (server, connect, _):
            peer = await connect("before")
            await peer.close(code=code, reason="fixture close")
            await disconnected(server)
            assert server._last_bridge_close_code == str(code)
            if code == 1009:
                assert server._oversize_count == 1
                observation, = server._oversize_observations
                assert observation["receivedCloseCode"] == 1009
                assert observation["commandId"] == "unknown"
            else:
                assert getattr(server, "_oversize_count", 0) == 0
                assert getattr(server, "_oversize_observations", []) == []
            assert not server.state.commands and not server._pending and not server._suspended
    asyncio.run(scenario())
