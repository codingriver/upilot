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

        await server._handle_message(_hello(tmp_path), [None], candidate_socket)

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
            payload={"connected": False},
            timestamp=2,
            session_id="stale",
        )

        await server._handle_message(message, ["stale"], stale_socket)

        assert session.last_heartbeat_at == original_heartbeat
        assert server.state.editor.connected is False

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
        await asyncio.sleep(0)
        sent = _wire_commands(socket)
        assert len(sent) == 1
        command_id = sent[0]["id"]
        original_record = server.state.commands[command_id]
        identity = (original_record.request_id, original_record.created_at, original_record.sent_at)
        lifecycle = server.state.lifecycle_id
        candidate = await _reload_for_dispatch_test(server, tmp_path, socket, "after")
        result = await call
        assert not result.ok and result.error.code == "COMMAND_RECOVERY_REQUIRED"
        assert result.error.detail["commandId"] == command_id
        assert result.error.detail["replayAttempted"] is False
        assert _wire_commands(candidate) == []
        assert server.state.lifecycle_id == lifecycle
        assert (original_record.request_id, original_record.created_at, original_record.sent_at) == identity
        assert not server._pending and not server._suspended
        closed_record = asdict(original_record)

        successor = asyncio.create_task(dispatcher.call("successor-request", "test.status", {}, timeout_ms=2000))
        await asyncio.sleep(0)
        successor_id = _wire_commands(candidate)[0]["id"]
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
        await asyncio.sleep(0)
        sent = _wire_commands(socket)
        assert len(sent) == 1 and len(waits) == 1
        command_id = sent[0]["id"]
        future = server._pending[command_id]
        original_wait = list(waits)
        record = server.state.commands[command_id]
        identity = (record.request_id, record.created_at, record.sent_at)
        first = await _reload_for_dispatch_test(server, tmp_path, socket, "after-one")
        second = await _reload_for_dispatch_test(server, tmp_path, first, "after-two")
        for reconnected in (first, second):
            resent = _wire_commands(reconnected)
            assert len(resent) == 1
            assert (resent[0]["id"], resent[0]["name"], resent[0]["payload"]) == (command_id, name, payload)
        assert waits == original_wait == [(future, 1.0)]
        assert server._pending[command_id] is future
        assert (record.request_id, record.created_at, record.sent_at) == identity
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
