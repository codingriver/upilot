from __future__ import annotations

import asyncio
import time
from pathlib import Path

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
