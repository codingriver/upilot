from __future__ import annotations

import asyncio
from dataclasses import asdict
import json
import time
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.dispatcher import CommandDispatcher

from upilot_mcp.models import WsMessage
from upilot_mcp.server import IDENTITY_CONTRACT_VERSION, WsOrchestratorServer


class _Socket:
    remote_address = ("127.0.0.1", 8765)

    def __init__(self) -> None:
        self.sent: list[str] = []

    async def send(self, raw: str) -> None:
        self.sent.append(raw)


class _ClosedSocket(_Socket):
    def __aiter__(self):
        return self

    async def __anext__(self):
        raise StopAsyncIteration


def _payload(project: Path, pid: int = 101, created_at: int = 202) -> dict:
    return {
        "identityContractVersion": IDENTITY_CONTRACT_VERSION,
        "unityVersion": "6000.0",
        "projectPath": str(project),
        "platform": "windows",
        "processId": pid,
        "processCreatedAt": created_at,
        "processRole": "mainEditor",
    }


def _hello(project: Path, session_id: str = "candidate", pid: int = 101, created_at: int = 202) -> WsMessage:
    return WsMessage(
        id="hello-1",
        type="hello",
        name="session.hello",
        payload=_payload(project, pid, created_at),
        timestamp=1,
        session_id=session_id,
    )


def _accepted_probe(message: WsMessage) -> dict:
    return {
        **message.payload,
        "accepted": True,
        "identityVerified": True,
        "verificationLevel": "test-verified",
    }


def test_rejected_candidate_does_not_replace_active_session(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        active_socket = _Socket()
        candidate_socket = _Socket()
        active_payload = _payload(tmp_path, 11, 22)
        active_payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("active", active_payload)
        server._ws = active_socket
        server._probe_candidate_identity = lambda _payload: {
            "accepted": False,
            "code": "AUXILIARY_EDITOR_ROLE",
            "reason": "Auxiliary Unity processes cannot own the session.",
        }
        server._close_websocket = lambda *_args, **_kwargs: asyncio.sleep(0)

        rejected_hello = _hello(tmp_path)
        rejected_hello.payload["heartbeatIntervalMs"] = 600000
        await server._handle_message(rejected_hello, [None], candidate_socket)

        assert server.heartbeat_interval_ms == 3000
        assert server.session_manager.heartbeat_timeout_ms == 9000
        assert server._ws is active_socket
        assert server.session_manager.active.session_id == "active"
        assert server.session_identity_status()["latestRejection"]["code"] == "AUXILIARY_EDITOR_ROLE"
        assert candidate_socket.sent

    asyncio.run(scenario())


def test_same_process_verified_reconnect_atomically_promotes_candidate(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        old_socket = _Socket()
        candidate_socket = _Socket()
        payload = _payload(tmp_path)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("old", payload)
        server._ws = old_socket
        message = _hello(tmp_path, session_id="new")
        server._probe_candidate_identity = lambda _payload: _accepted_probe(message)
        server._close_websocket = lambda *_args, **_kwargs: asyncio.sleep(0)

        admitted, _ = await server._admit_candidate(candidate_socket, message)
        await asyncio.sleep(0)

        assert admitted is True
        assert server._ws is candidate_socket
        assert server.session_manager.active.session_id == "new"

    asyncio.run(scenario())


def test_different_live_process_cannot_take_over(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        old_socket = _Socket()
        candidate_socket = _Socket()
        payload = _payload(tmp_path, 11, 22)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("old", payload)
        server._ws = old_socket
        message = _hello(tmp_path, session_id="new", pid=33, created_at=44)
        server._probe_candidate_identity = lambda _payload: _accepted_probe(message)
        server._active_process_liveness = lambda: "alive"

        admitted, detail = await server._admit_candidate(candidate_socket, message)

        assert admitted is False
        assert detail["code"] == "ACTIVE_EDITOR_CONFLICT"
        assert server._ws is old_socket
        assert server.session_manager.active.session_id == "old"

    asyncio.run(scenario())


def test_stale_socket_message_cannot_touch_active_state(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        active_socket = _Socket()
        stale_socket = _Socket()
        payload = _payload(tmp_path)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        session = server.session_manager.on_hello("active", payload)
        server._ws = active_socket
        original_heartbeat = session.last_heartbeat_at
        message = WsMessage(
            id="hb-stale",
            type="heartbeat",
            name="session.heartbeat",
            payload={"connected": False, "heartbeatIntervalMs": 600000},
            timestamp=2,
            session_id="stale",
        )

        await server._handle_message(message, ["stale"], stale_socket)

        assert session.last_heartbeat_at == original_heartbeat
        assert server.state.editor.connected is False
        assert server.heartbeat_interval_ms == 3000
        assert server.session_manager.heartbeat_timeout_ms == 9000

    asyncio.run(scenario())


def test_missing_contract_and_wrong_project_are_rejected_without_os_probe(tmp_path: Path) -> None:
    server = WsOrchestratorServer(expected_project_path=str(tmp_path))
    missing = _payload(tmp_path)
    missing.pop("identityContractVersion")
    assert server._probe_candidate_identity(missing)["code"] == "IDENTITY_CONTRACT_MISMATCH"

    other = _payload(tmp_path / "other")
    assert server._probe_candidate_identity(other)["code"] == "PROJECT_IDENTITY_MISMATCH"


def test_candidate_query_timeout_does_not_replace_active_session(monkeypatch, tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        active_socket = _Socket()
        payload = _payload(tmp_path, 11, 22)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("active", payload)
        server._ws = active_socket
        server._probe_candidate_identity = lambda _payload: time.sleep(0.03) or _accepted_probe(_hello(tmp_path))
        monkeypatch.setattr("upilot_mcp.server._CANDIDATE_HANDSHAKE_TIMEOUT_S", 0.001)

        admitted, detail = await server._admit_candidate(_Socket(), _hello(tmp_path, pid=33, created_at=44))

        assert admitted is False
        assert detail["code"] == "PROCESS_QUERY_TIMEOUT"
        assert server._ws is active_socket
        assert server.session_manager.active.session_id == "active"

    asyncio.run(scenario())


def test_unknown_old_process_liveness_blocks_takeover(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        old_socket = _Socket()
        payload = _payload(tmp_path, 11, 22)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("old", payload)
        server._ws = old_socket
        candidate = _hello(tmp_path, session_id="new", pid=33, created_at=44)
        server._probe_candidate_identity = lambda _payload: _accepted_probe(candidate)
        server._active_process_liveness = lambda: "unknown"

        admitted, detail = await server._admit_candidate(_Socket(), candidate)

        assert admitted is False
        assert detail["code"] == "ACTIVE_EDITOR_CONFLICT"
        assert detail["activeProcessLiveness"] == "unknown"
        assert server._ws is old_socket

    asyncio.run(scenario())


def test_confirmed_dead_or_pid_reused_editor_can_take_over(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        old_socket = _Socket()
        payload = _payload(tmp_path, 11, 22)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("old", payload)
        server._ws = old_socket
        candidate_socket = _Socket()
        candidate = _hello(tmp_path, session_id="new", pid=11, created_at=44)
        server._probe_candidate_identity = lambda _payload: _accepted_probe(candidate)
        server._active_process_liveness = lambda: "dead"
        server._close_websocket = lambda *_args, **_kwargs: asyncio.sleep(0)

        admitted, _ = await server._admit_candidate(candidate_socket, candidate)
        await asyncio.sleep(0)

        assert admitted is True
        assert server._ws is candidate_socket
        assert server.session_manager.active.session_id == "new"
        assert server.session_manager.active.process_created_at == 44

    asyncio.run(scenario())


def test_concurrent_candidates_have_only_one_owner(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        first = _hello(tmp_path, session_id="first", pid=11, created_at=22)
        second = _hello(tmp_path, session_id="second", pid=33, created_at=44)
        server._probe_candidate_identity = lambda payload: {
            **payload,
            "accepted": True,
            "identityVerified": True,
            "verificationLevel": "test-verified",
        }
        server._active_process_liveness = lambda: "alive"

        results = await asyncio.gather(
            server._admit_candidate(_Socket(), first),
            server._admit_candidate(_Socket(), second),
        )

        admitted = [result for result in results if result[0]]
        rejected = [result for result in results if not result[0]]
        assert len(admitted) == 1 and len(rejected) == 1
        assert rejected[0][1]["code"] == "ACTIVE_EDITOR_CONFLICT"
        assert server.session_manager.active.session_id in {"first", "second"}

    asyncio.run(scenario())


def test_stale_result_cannot_complete_active_future(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        active_socket = _Socket()
        stale_socket = _Socket()
        payload = _payload(tmp_path)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("active", payload)
        server._ws = active_socket
        future = asyncio.get_running_loop().create_future()
        server._pending["cmd-1"] = future
        message = WsMessage(
            id="cmd-1",
            type="result",
            name="compile.request",
            payload={"status": "completed"},
            timestamp=2,
            session_id="stale",
        )

        await server._handle_message(message, ["stale"], stale_socket)

        assert future.done() is False
        assert server._pending["cmd-1"] is future

    asyncio.run(scenario())


def test_stale_socket_cleanup_does_not_disconnect_new_owner(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        active_socket = _Socket()
        stale_socket = _ClosedSocket()
        payload = _payload(tmp_path)
        payload.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("active", payload)
        server._ws = active_socket
        server.state.editor.connected = True

        await server._handle(stale_socket)

        assert server._ws is active_socket
        assert server.session_manager.active.session_id == "active"
        assert server.state.editor.connected is True

    asyncio.run(scenario())


def _wire_commands(socket):
    return [message for raw in socket.sent if (message := json.loads(raw))["type"] == "command"]


async def _response_stage_command(server, socket):
    # A bounded send wait adds real event-loop turns. Observe the recorded
    # response stage instead of assuming one sleep(0) completes send().
    async def observe():
        while True:
            sent = _wire_commands(socket)
            if sent:
                assert len(sent) == 1
                record = server.state.commands[sent[0]["id"]]
                if record.response_wait_timeout_ms is not None:
                    return sent[0]
            await asyncio.sleep(0)

    return await asyncio.wait_for(observe(), timeout=1)


async def _reload_for_dispatch_test(server, project, socket, next_session):
    old_session = server.session_manager.active.session_id
    await server._handle_message(WsMessage(
        id="reload-" + old_session, type="event", name="domain_reload.starting",
        payload={}, timestamp=2, session_id=old_session,
    ), [old_session], socket)
    assert server._domain_reloading
    server._suspend_or_fail_pending_on_disconnect(old_session)
    candidate = _Socket()
    hello = _hello(project, session_id=next_session)
    server._probe_candidate_identity = lambda _payload: _accepted_probe(hello)
    server._close_websocket = lambda *_args, **_kwargs: asyncio.sleep(0)
    auth = [None]
    await server._handle_message(hello, auth, candidate)
    assert auth == [next_session] and server._ws is candidate
    assert not server._suspended and not server._domain_reloading
    return candidate


@pytest.mark.parametrize("name,payload", [
    ("test.run", {"testMode": "EditMode", "runGuid": "original-run"}),
    ("compile.request", {"writeBatchId": "original-batch"}),
    ("console.capture.start", {"sessionId": "original-capture"}),
    ("console.capture.stop", {"sessionId": "original-capture"}),
])
def test_dispatch_reload_rejects_mutation_and_late_result_cannot_complete_successor(tmp_path, name, payload):
    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        socket = _Socket()
        active = _payload(tmp_path)
        active.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("before", active)
        server._ws = socket
        dispatcher = CommandDispatcher(server, server.state)
        call = asyncio.create_task(dispatcher.call("original-request", name, payload, timeout_ms=2000))
        sent = await _response_stage_command(server, socket)
        command_id = sent["id"]
        original_record = server.state.commands[command_id]
        identity = (original_record.request_id, original_record.created_at, original_record.sent_at)
        wait_identity = (original_record.response_wait_started_at,
                         original_record.response_wait_deadline_at, original_record.response_wait_timeout_ms)
        assert original_record.call_timeout_ms == 2000
        assert original_record.call_deadline_at == original_record.call_started_at + 2000
        assert wait_identity[0] > 0 and 0 < wait_identity[2] <= 2000
        assert wait_identity[1] == min(original_record.call_deadline_at,
                                       wait_identity[0] + wait_identity[2])
        assert original_record.send_attempts == 1
        call_budget = (original_record.call_deadline_at, original_record.call_deadline_monotonic)
        lifecycle = server.state.lifecycle_id
        candidate = await _reload_for_dispatch_test(server, tmp_path, socket, "after")
        result = await call
        assert not result.ok and result.error.code == "COMMAND_RECOVERY_REQUIRED"
        assert result.error.detail["commandId"] == command_id
        assert result.error.detail["replayAttempted"] is False
        assert _wire_commands(candidate) == []
        assert server.state.lifecycle_id == lifecycle
        assert (original_record.request_id, original_record.created_at, original_record.sent_at) == identity
        assert (original_record.response_wait_started_at, original_record.response_wait_deadline_at,
                original_record.response_wait_timeout_ms) == wait_identity
        assert original_record.send_attempts == 1
        assert (original_record.call_deadline_at, original_record.call_deadline_monotonic) == call_budget
        assert not server._pending and not server._suspended
        closed_record = asdict(original_record)

        successor = asyncio.create_task(dispatcher.call("successor-request", "test.status", {}, timeout_ms=2000))
        successor_sent = await _response_stage_command(server, candidate)
        successor_id = successor_sent["id"]
        assert successor_id != command_id
        await server._handle_message(WsMessage(
            id=command_id, type="result", name=name, payload={"late": True},
            timestamp=3, session_id="after",
        ), ["after"], candidate)
        assert not successor.done() and not server._pending[successor_id].done()
        assert asdict(original_record) == closed_record
        await server._handle_message(WsMessage(
            id=successor_id, type="result", name="test.status", payload={"runGuid": "successor-run"},
            timestamp=4, session_id="after",
        ), ["after"], candidate)
        successor_result = await successor
        assert successor_result.ok and successor_result.data["runGuid"] == "successor-run"
        assert set(server.state.commands) == {command_id, successor_id}
        assert len(_wire_commands(socket)) == 1 and len(_wire_commands(candidate)) == 1
        assert not server._pending and not server._suspended

    asyncio.run(scenario())


@pytest.mark.parametrize("name,payload", [
    ("resource.editorState", {}), ("test.status", {}), ("compile.errors.get", {}),
    ("test.results", {"runGuid": "original-run"}),
])
@pytest.mark.parametrize("ending", ["timeout", "cancel"])
def test_dispatch_read_only_reload_keeps_original_wait_and_releases_pending(tmp_path, monkeypatch, name, payload, ending):
    async def scenario():
        waits = []
        real_wait_for = asyncio.wait_for

        async def recorded_wait_for(future, timeout):
            waits.append((future, timeout))
            return await real_wait_for(future, timeout)

        # Only the dispatcher's asyncio reference is replaced; actual asyncio
        # Future/timer/cancellation semantics and the server remain unchanged.
        monkeypatch.setattr("upilot_mcp.dispatcher.asyncio", SimpleNamespace(
            wait_for=recorded_wait_for, TimeoutError=asyncio.TimeoutError,
            CancelledError=asyncio.CancelledError,
        ))
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        socket = _Socket()
        active = _payload(tmp_path)
        active.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("before", active)
        server._ws = socket
        dispatcher = CommandDispatcher(server, server.state)
        call = asyncio.create_task(dispatcher.call("original-request", name, payload, timeout_ms=1000))
        sent = await _response_stage_command(server, socket)
        assert len(waits) == 2  # Original send stage, then original response Future.
        command_id = sent["id"]
        future = server._pending[command_id]
        original_wait = list(waits)
        record = server.state.commands[command_id]
        identity = (record.request_id, record.created_at, record.sent_at)
        wait_identity = (record.response_wait_started_at, record.response_wait_deadline_at,
                         record.response_wait_timeout_ms)
        assert record.call_timeout_ms == 1000
        assert record.call_deadline_at == record.call_started_at + 1000
        assert wait_identity[0] > 0 and 0 < wait_identity[2] <= 1000
        assert wait_identity[1] == min(record.call_deadline_at,
                                       wait_identity[0] + wait_identity[2])
        assert 0 < waits[1][1] <= waits[0][1] <= 1.0
        assert waits[1][0] is future and record.send_attempts == 1
        call_budget = (record.call_deadline_at, record.call_deadline_monotonic)
        first = await _reload_for_dispatch_test(server, tmp_path, socket, "after-one")
        second = await _reload_for_dispatch_test(server, tmp_path, first, "after-two")
        for reconnected in (first, second):
            resent = _wire_commands(reconnected)
            assert len(resent) == 1
            assert (resent[0]["id"], resent[0]["name"], resent[0]["payload"]) == (command_id, name, payload)
        assert waits == original_wait
        assert waits[1][0] is future
        assert server._pending[command_id] is future
        assert (record.request_id, record.created_at, record.sent_at) == identity
        assert (record.response_wait_started_at, record.response_wait_deadline_at,
                record.response_wait_timeout_ms) == wait_identity
        assert record.send_attempts == 3
        assert (record.call_deadline_at, record.call_deadline_monotonic) == call_budget
        if ending == "cancel":
            call.cancel()
            with pytest.raises(asyncio.CancelledError):
                await call
            assert record.error["code"] == "CANCELLED"
        else:
            result = await real_wait_for(call, timeout=2)
            assert not result.ok and result.error.code == "COMMAND_TIMEOUT"
            assert result.error.detail["commandId"] == command_id
        assert future.done() and not server._pending and not server._suspended
        terminal_record = asdict(record)
        await server._handle_message(WsMessage(
            id=command_id, type="result", name=name, payload={"late": True},
            timestamp=5, session_id="after-two",
        ), ["after-two"], second)
        third = await _reload_for_dispatch_test(server, tmp_path, second, "after-terminal")
        assert _wire_commands(third) == []
        assert asdict(record) == terminal_record
        assert set(server.state.commands) == {command_id}
        assert waits == original_wait and not server._pending and not server._suspended

    asyncio.run(scenario())


@pytest.mark.parametrize("failure_kind", ["connection", "cancel"])
def test_send_failure_converges_original_command_record(tmp_path, failure_kind):
    """A failed send must release its command, without claiming non-execution."""
    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        socket = _Socket()
        active = _payload(tmp_path)
        active.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("before-send", active)
        server._ws = socket
        dispatcher = CommandDispatcher(server, server.state)
        original_send = server.send_command
        failure = ConnectionError("injected send failure") if failure_kind == "connection" else asyncio.CancelledError()

        async def failed_send(**_kwargs):
            raise failure

        server.send_command = failed_send
        with pytest.raises(type(failure)) as raised:
            await dispatcher.call("send-failure-request", "console.capture.start", {}, timeout_ms=1000)
        assert raised.value is failure
        record, = server.state.commands.values()
        assert not server._pending and not server._suspended
        assert record.status == "failed" and record.completed_at >= record.created_at
        assert record.error["code"] == ("CANCELLED" if failure_kind == "cancel" else "COMMAND_SEND_FAILED")
        assert record.error["detail"]["outcome"] == "unknown"
        assert record.error["detail"]["replayAttempted"] is False
        assert record.error["detail"]["commandId"] == record.command_id
        frozen = asdict(record)
        server.send_command = original_send
        reconnected = await _reload_for_dispatch_test(server, tmp_path, socket, "after-send-failure")
        assert _wire_commands(reconnected) == []
        assert asdict(record) == frozen
        assert not server._pending and not server._suspended

    asyncio.run(scenario())


def test_loopback_authenticated_idle_socket_has_no_server_heartbeats(tmp_path):
    """The real socket keeps ping/pong, inbound state, commands and disconnect cleanup."""
    import websockets
    from upilot_mcp.protocol import to_wire

    async def scenario():
        server = WsOrchestratorServer(port=0, heartbeat_interval_ms=50, expected_project_path=str(tmp_path))
        server._probe_candidate_identity = lambda payload: {
            **payload, "accepted": True, "identityVerified": True, "verificationLevel": "test-verified",
        }
        candidate_cancelled = asyncio.Event()
        heartbeat_received = asyncio.Event()
        candidate_timeout = server._candidate_timeout

        async def observe_candidate_timeout(*args):
            try:
                await candidate_timeout(*args)
            except asyncio.CancelledError:
                candidate_cancelled.set()
                raise

        server._candidate_timeout = observe_candidate_timeout
        server.on_editor_execution_state = lambda _state: heartbeat_received.set()
        serving = asyncio.create_task(server.start())
        try:
            await server.wait_until_listening(timeout_s=5)
            port = server._server.sockets[0].getsockname()[1]
            async with websockets.connect(f"ws://127.0.0.1:{port}") as peer:
                session = "idle-peer"
                await peer.send(json.dumps(to_wire(_hello(tmp_path, session_id=session))))
                ack = json.loads(await asyncio.wait_for(peer.recv(), timeout=3))
                assert ack["payload"]["accepted"] is True
                assert ack["payload"]["heartbeatIntervalMs"] == 50
                await asyncio.wait_for(candidate_cancelled.wait(), timeout=3)

                # Four former send intervals must produce no application messages.
                with pytest.raises(asyncio.TimeoutError):
                    await asyncio.wait_for(peer.recv(), timeout=0.2)
                pong = await server._ws.ping()
                await asyncio.wait_for(pong, timeout=3)

                now = int(time.time() * 1000)
                await peer.send(json.dumps(to_wire(WsMessage(
                    id="inbound-state", type="heartbeat", name="session.heartbeat",
                    payload={
                        "stateContractVersion": 2, "producerEpoch": "idle-peer-epoch",
                        "domainGeneration": 0, "sequence": 1, "transition": "heartbeat",
                        "sessionId": session, "connected": True, "authoritative": True,
                        "playModeState": "edit", "observedAt": now, "updatedAt": now,
                    },
                    timestamp=now, session_id=session,
                ))))
                await asyncio.wait_for(heartbeat_received.wait(), timeout=3)
                assert server.is_ready()
                assert server.state.editor.authoritative and server.state.sequence == 1

                dispatcher = CommandDispatcher(server, server.state)
                request = asyncio.create_task(dispatcher.call(
                    "idle-roundtrip", "resource.editorState", {}, timeout_ms=3000,
                ))
                command = json.loads(await asyncio.wait_for(peer.recv(), timeout=3))
                assert command["type"] == "command" and command["name"] == "resource.editorState"
                await peer.send(json.dumps(to_wire(WsMessage(
                    id=command["id"], type="result", name=command["name"], payload={"fixture": "ok"},
                    timestamp=now, session_id=session,
                ))))
                result = await asyncio.wait_for(request, timeout=3)
                assert result.ok and result.data == {"fixture": "ok"}

            async def disconnected():
                while server._ws is not None:
                    await asyncio.sleep(0.01)

            await asyncio.wait_for(disconnected(), timeout=3)
            assert not server.session_manager.active.connected
            assert not server._pending and not server._suspended
        finally:
            server.stop()
            await asyncio.wait_for(serving, timeout=3)

    asyncio.run(scenario())


@pytest.mark.parametrize("boundary", ["response_lost", "reload_response_lost", "completed", "send_failure", "send_cancel"])
def test_loopback_socket_loss_never_replays_mutation(tmp_path, boundary):
    """Real TCP/WebSocket + dispatcher; the Bridge and OS identity probe are fixtures."""
    import websockets
    from upilot_mcp.protocol import to_wire

    async def scenario():
        server = WsOrchestratorServer(port=0, expected_project_path=str(tmp_path))
        server._probe_candidate_identity = lambda payload: {
            **payload, "accepted": True, "identityVerified": True, "verificationLevel": "test-verified",
        }
        serving = asyncio.create_task(server.start())
        peers = []
        calls = []
        original_send = server.send_command
        send_boundary = asyncio.Event()
        observed_mutations = []

        async def interrupted_send(**kwargs):
            await original_send(**kwargs)
            await send_boundary.wait()
            raise ConnectionError("injected failure after frame delivery")

        async def send(peer, session, kind, name, payload, command_id):
            await peer.send(json.dumps(to_wire(WsMessage(
                id=command_id, type=kind, name=name, payload=payload,
                timestamp=1, session_id=session,
            ))))

        async def receive(peer):
            message = json.loads(await asyncio.wait_for(peer.recv(), timeout=3))
            if message["type"] == "command" and message["name"] == "console.capture.start":
                observed_mutations.append(message["id"])
            return message

        async def connect(session):
            port = server._server.sockets[0].getsockname()[1]
            peer = await websockets.connect(f"ws://127.0.0.1:{port}")
            peers.append(peer)
            await peer.send(json.dumps(to_wire(_hello(tmp_path, session_id=session))))
            ack = await receive(peer)
            assert ack["payload"]["accepted"] is True
            return peer

        async def await_disconnected():
            async def observe():
                while server._ws is not None:
                    await asyncio.sleep(0.01)
            await asyncio.wait_for(observe(), timeout=3)

        try:
            await server.wait_until_listening(timeout_s=5)
            peer = await connect("wire-before")
            dispatcher = CommandDispatcher(server, server.state)
            if boundary in {"send_failure", "send_cancel"}:
                server.send_command = interrupted_send
            call = asyncio.create_task(dispatcher.call(
                "wire-original", "console.capture.start", {"requestKey": "owned-fixture"}, timeout_ms=5000,
            ))
            calls.append(call)
            command = await receive(peer)
            assert command["type"] == "command" and command["name"] == "console.capture.start"
            command_id = command["id"]
            record = server.state.commands[command_id]
            identity = (record.command_id, record.request_id, record.created_at, record.sent_at)
            lifecycle = server.state.lifecycle_id
            # The simulated Bridge received the mutation before response interruption.
            if boundary == "completed":
                await send(peer, "wire-before", "result", command["name"], {"effectCount": 1}, command_id)
                result = await asyncio.wait_for(call, timeout=3)
                assert result.ok and result.data["effectCount"] == 1
            elif boundary in {"send_failure", "send_cancel"}:
                if boundary == "send_cancel":
                    call.cancel()
                else:
                    send_boundary.set()
                with pytest.raises(asyncio.CancelledError if boundary == "send_cancel" else ConnectionError):
                    await asyncio.wait_for(call, timeout=3)
                assert record.error["detail"]["outcome"] == "unknown"
                assert record.error["detail"]["replayAttempted"] is False
            elif boundary == "reload_response_lost":
                await send(peer, "wire-before", "event", "domain_reload.starting", {}, "reload-boundary")
            server.send_command = original_send
            await peer.close()
            await await_disconnected()
            reconnected = await connect("wire-after")
            if boundary in {"response_lost", "reload_response_lost"}:
                result = await asyncio.wait_for(call, timeout=3)
                assert not result.ok
                assert result.error.code == ("COMMAND_RECOVERY_REQUIRED" if boundary == "reload_response_lost" else "CONNECTION_LOST")
            assert not server._pending and not server._suspended
            assert server.state.lifecycle_id == lifecycle
            assert (record.command_id, record.request_id, record.created_at, record.sent_at) == identity
            assert record.status == ("success" if boundary == "completed" else "failed")
            frozen = asdict(record)
            successor = asyncio.create_task(dispatcher.call("wire-successor", "test.status", {}, timeout_ms=3000))
            calls.append(successor)
            next_command = await receive(reconnected)
            assert next_command["name"] == "test.status" and next_command["id"] != command_id
            # Late old result is delivered through the authenticated *current* socket.
            await send(reconnected, "wire-after", "result", command["name"], {"late": True}, command_id)
            await send(reconnected, "wire-after", "result", "test.status", {"runGuid": "successor"}, next_command["id"])
            result = await asyncio.wait_for(successor, timeout=3)
            assert result.ok and result.data == {"runGuid": "successor"}
            assert asdict(record) == frozen and observed_mutations == [command_id]
            assert len(server.state.commands) == 2 and not server._pending and not server._suspended
        finally:
            for call in calls:
                if not call.done():
                    call.cancel()
            await asyncio.gather(*calls, return_exceptions=True)
            for peer in peers:
                await peer.close()
            server.stop()
            await asyncio.wait_for(serving, timeout=5)

    asyncio.run(scenario())


@pytest.mark.parametrize("cancelled", [False, True])
def test_send_interruption_keeps_reload_recovery_first_error(tmp_path, cancelled):
    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        socket = _Socket()
        active = _payload(tmp_path)
        active.update(identityVerified=True, verificationLevel="test-verified")
        server.session_manager.on_hello("sending", active)
        server._ws = socket
        frozen = {}
        failure = asyncio.CancelledError() if cancelled else ConnectionError("old socket failed")

        async def interrupted_send(**kwargs):
            await _reload_for_dispatch_test(server, tmp_path, socket, "reloaded-during-send")
            frozen.update(asdict(server.state.commands[kwargs["command_id"]]))
            raise failure

        server.send_command = interrupted_send
        dispatcher = CommandDispatcher(server, server.state)
        with pytest.raises(type(failure)):
            await dispatcher.call("reload-send-race", "console.capture.start", {}, timeout_ms=1000)
        record, = server.state.commands.values()
        assert frozen["error"]["code"] == "COMMAND_RECOVERY_REQUIRED"
        assert asdict(record) == frozen
        assert not server._pending and not server._suspended
        assert _wire_commands(server._ws) == []

    asyncio.run(scenario())



def test_heartbeat_defaults_and_explicit_constructor_interval() -> None:
    from upilot_mcp.session_manager import SessionManager

    server = WsOrchestratorServer()
    assert server.heartbeat_interval_ms == 3000
    assert server.session_manager.heartbeat_timeout_ms == 9000
    assert SessionManager().heartbeat_timeout_ms == 9000
    explicit = WsOrchestratorServer(heartbeat_interval_ms=50)
    assert explicit.heartbeat_interval_ms == 50
    assert explicit.session_manager.heartbeat_timeout_ms == 150


@pytest.mark.parametrize("interval", [None, False, True, "3000", 3000.0, 0, -1000, 999, 600001, 1000, 3000, 600000])
def test_verified_hello_negotiates_only_valid_heartbeat_interval(tmp_path, monkeypatch, interval):
    async def scenario():
        server = WsOrchestratorServer(heartbeat_interval_ms=50, expected_project_path=str(tmp_path))
        socket = _Socket()
        message = _hello(tmp_path, "interval-session")
        if interval is not None:
            message.payload["heartbeatIntervalMs"] = interval
        server._probe_candidate_identity = lambda _payload: _accepted_probe(message)
        auth = [None]

        await server._handle_message(message, auth, socket)

        expected = interval if type(interval) is int and 1000 <= interval <= 600000 else 50
        assert auth == ["interval-session"]
        assert server.heartbeat_interval_ms == expected
        assert server.session_manager.heartbeat_timeout_ms == expected * 3
        assert json.loads(socket.sent[-1])["payload"]["heartbeatIntervalMs"] == expected
        received_at = server.session_manager.active.last_heartbeat_at
        monkeypatch.setattr("upilot_mcp.session_manager.now_ms", lambda: received_at + expected * 3)
        assert server.session_manager.is_connected()
        monkeypatch.setattr("upilot_mcp.session_manager.now_ms", lambda: received_at + expected * 3 + 1)
        assert not server.session_manager.is_connected()

    asyncio.run(scenario())


@pytest.mark.parametrize("message_type", ["heartbeat", "event"])
def test_heartbeat_interval_changes_only_from_accepted_current_authoritative_snapshots(tmp_path, monkeypatch, message_type):
    from upilot_mcp.domain.status_service import StatusDomainService

    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        socket = _Socket()
        hello = _hello(tmp_path, "interval-session")
        server._probe_candidate_identity = lambda _payload: _accepted_probe(hello)
        auth = [None]
        await server._handle_message(hello, auth, socket)

        async def send_snapshot(sequence, interval, **changes):
            payload = {
                "stateContractVersion": 2, "projectId": "interval-project",
                "producerEpoch": "interval-epoch", "domainGeneration": 1,
                "sequence": sequence, "snapshotId": f"interval-epoch:1:{sequence}",
                "transition": "heartbeat", "sessionId": "interval-session",
                "connected": True, "authoritative": True, "isStale": False,
                "playModeState": "edit", "heartbeatIntervalMs": interval,
            }
            payload.update(changes)
            await server._handle_message(WsMessage(
                id=f"interval-{sequence}", type=message_type,
                name="session.heartbeat" if message_type == "heartbeat" else "editor.execution_state",
                payload=payload, timestamp=sequence, session_id="interval-session",
            ), auth, socket)

        def assert_interval(value):
            assert server.heartbeat_interval_ms == value
            assert server.session_manager.heartbeat_timeout_ms == value * 3

        await send_snapshot(1, 600000)
        assert_interval(600000)
        await send_snapshot(1, 1000)  # Duplicate ordered observation.
        assert_interval(600000)
        await send_snapshot(2, 1000, sessionId="other-session")
        assert_interval(600000)
        await send_snapshot(3, 1000, authoritative=False)
        assert_interval(600000)
        await send_snapshot(4, 1000, isStale=True)
        assert_interval(600000)
        await send_snapshot(5, 1000, authoritative=None)
        assert_interval(600000)
        await send_snapshot(6, True)
        assert_interval(600000)
        await send_snapshot(7, None)
        assert_interval(600000)
        await send_snapshot(8, 1000)
        assert_interval(1000)

        status = StatusDomainService.__new__(StatusDomainService)
        status.server = server
        status.dispatcher = CommandDispatcher(server, server.state)
        result = await status.mcp_status(include_capabilities=False)
        assert result.ok
        assert result.data["timeouts"]["heartbeatIntervalMs"] == 1000
        assert result.data["timeouts"]["heartbeatTimeoutMs"] == 3000

        await server._handle_message(WsMessage(
            id="ordinary-result", type="result", name="resource.editorState", payload={},
            context={"heartbeatIntervalMs": 600000, "sessionId": "interval-session", "authoritative": True},
            timestamp=9, session_id="interval-session",
        ), auth, socket)
        assert_interval(1000)

        monkeypatch.setattr(server.state, "_persist_execution_snapshot", lambda _payload: False)
        await send_snapshot(9, 600000)
        assert_interval(1000)

    asyncio.run(scenario())
