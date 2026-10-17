from __future__ import annotations

import asyncio
from dataclasses import replace
import json
import os
from pathlib import Path
import sqlite3
import time
from uuid import uuid4

import pytest

from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.models import WsMessage
from upilot_mcp.server import WsOrchestratorServer


class Socket:
    remote_address = ("127.0.0.1", 8765)

    def __init__(self):
        self.sent = []
        self.on_send = None

    async def send(self, raw):
        self.sent.append(json.loads(raw))
        if self.on_send:
            self.on_send(self.sent[-1])


def message(kind, name, payload, *, identity="message", session="bridge"):
    return WsMessage(id=identity, type=kind, name=name, payload=payload,
                     timestamp=int(time.time() * 1000), session_id=session)


async def hello(server, socket, *, session="bridge", pid=101, created=202):
    body = dict(identityContractVersion=1, bridgeReadyContractVersion=1, readyBudgetMs=300000,
                projectPath=server.expected_project_path, processId=pid, processCreatedAt=created,
                processRole="mainEditor", unityVersion="6000.0", platform="windows")
    server._probe_candidate_identity = lambda payload: {
        **payload, "accepted": True, "identityVerified": True, "verificationLevel": "test-verified"}
    async def close(*args, **kwargs):
        return None
    server._close_websocket = close
    auth = [None]
    await server._handle_message(message("hello", "session.hello", body, identity="hello-1", session=session), auth, socket)
    return auth


def snapshot(*, sequence=1, epoch="epoch", domain=0, session="bridge", pid=101, created=202):
    now = int(time.time() * 1000)
    return dict(stateContractVersion=2, projectId="project", producerEpoch=epoch, domainGeneration=domain,
                sequence=sequence, snapshotId=f"{epoch}:{domain}:{sequence}", sessionId=session,
                processId=pid, processCreatedAt=created, observedAt=now, updatedAt=now,
                lastMainThreadPumpAt=now, connected=True, authoritative=True, isStale=False,
                playModeState="edit", isCompiling=False, isUpdating=False,
                compileStatus="idle", compilePhase="idle", compileRequestId="", compileOperationId="",
                writeBatchId="", writeBatchCreatedAt=0, compileOrigin="", terminal=False,
                verificationPending=False, errorsVerified=False, lastCompileRequestedAt=0,
                lastCompileStartedAt=0, lastCompilerFinishedAt=0, lastCompileVerifiedAt=0,
                lastTerminalCompileAt=0, errorCount=0, warningCount=0, transition="bootstrap")


def diagnostics(state):
    mappings = {"compileRequestId": "requestId", "compilePhase": "phase", "compileStatus": "status",
                "errorCount": "total"}
    fields = ("compileRequestId", "compileOperationId", "writeBatchId", "writeBatchCreatedAt", "compileOrigin",
              "compileStatus", "compilePhase", "terminal", "verificationPending", "errorsVerified",
              "lastCompileRequestedAt", "lastCompileStartedAt", "lastCompilerFinishedAt",
              "lastCompileVerifiedAt", "lastTerminalCompileAt", "errorCount", "warningCount")
    result = {mappings.get(key, key): state[key] for key in fields}
    result.update(errors=[], warnings=[])
    return result


async def bootstrap(server, socket, auth, state=None, *, transform=None):
    connection = server._connection
    identity = {**server._bootstrap_identity(connection), "bridgeReadyContractVersion": 1}
    await server._handle_message(message("event", "session.ready", identity, session=auth[0]), auth, socket)
    state = state or snapshot(session=auth[0])
    payload = {**identity, "bridgeReadyContractVersion": 1, "nonce": connection.nonce,
               "state": state, "compileEvidence": diagnostics(state)}
    if transform:
        transform(payload)
    await server._handle_message(message("result", "session.bootstrap", payload,
                                        identity=connection.bootstrap_id, session=auth[0]), auth, socket)


def test_hello_alone_gates_business_state_and_pending_results(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        assert server.session_manager.is_connected()
        assert not server.is_ready()
        assert socket.sent[-1]["payload"]["bridgeReadyContractVersion"] == 1
        original_lease = server.session_manager.active.last_heartbeat_at
        future = server.register_pending("ordinary")
        for incoming in [message("heartbeat", "session.heartbeat", snapshot()),
                         message("event", "editor.execution_state", snapshot()),
                         message("event", "compile.errors", {"total": 1, "errors": [{}]}),
                         message("result", "resource.editorState", snapshot(), identity="ordinary")]:
            await server._handle_message(incoming, auth, socket)
        assert server.state.accepted_snapshot_count == 0
        assert not future.done()
        assert server.session_manager.active.last_heartbeat_at == original_lease
        assert not server.state.execution_state()["authoritative"]
        with pytest.raises(ConnectionError, match="RECONNECT_PENDING"):
            await server.send_command("ordinary", "resource.editorState", {})
        assert len(socket.sent) == 1
    asyncio.run(scenario())


def test_bootstrap_persists_unknown_compile_without_inventing_success_and_ack_precedes_business(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth)
        assert server.is_ready()
        assert not server.state.compile.errors_verified
        assert not server.state.compile.terminal
        assert server.state.compile.last_compile_verified_at == 0
        with sqlite3.connect(server.state._db_path) as db:
            persisted = json.loads(db.execute("SELECT snapshot_json FROM project_state").fetchone()[0])
        assert persisted["bootstrapCompileDiagnostics"]["errorsVerified"] is False
        await server.send_command("ordinary", "resource.editorState", {})
        assert [item["name"] for item in socket.sent] == ["session.hello", "session.bootstrap", "session.bootstrap.ack", "resource.editorState"]
        receipt = server.bridge_readiness_status()["bootstrapReceipt"]
        assert receipt["snapshotId"] == server.state.snapshot_id
        assert receipt["nonce"] == server._connection.nonce
    asyncio.run(scenario())


@pytest.mark.parametrize("tamper", [
    lambda payload: payload.update(nonce="foreign"),
    lambda payload: payload.update(serverInstanceId="foreign"),
    lambda payload: payload["state"].update(processCreatedAt=999),
    lambda payload: payload["state"].update(sequence=0),
    lambda payload: payload["compileEvidence"].update(requestId="foreign"),
    lambda payload: payload["compileEvidence"].update(total=1),
    lambda payload: payload["compileEvidence"].update(errorsVerified=True),
    lambda payload: payload["state"].update(errorsVerified=True),
])
def test_invalid_bootstrap_never_commits_or_acknowledges(tmp_path, tamper):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth, transform=tamper)
        assert not server.is_ready()
        assert server.state.accepted_snapshot_count == 0
        assert not any(item["name"] == "session.bootstrap.ack" for item in socket.sent)
    asyncio.run(scenario())


def test_duplicate_ready_uses_original_bootstrap_identity_and_deadline(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        connection = server._connection
        deadline = connection.deadline
        ready = message("event", "session.ready", {**server._bootstrap_identity(connection), "bridgeReadyContractVersion": 1})
        await server._handle_message(ready, auth, socket)
        bootstrap_id, nonce = connection.bootstrap_id, connection.nonce
        await server._handle_message(ready, auth, socket)
        assert len(socket.sent) == 2
        assert (connection.bootstrap_id, connection.nonce, connection.deadline) == (bootstrap_id, nonce, deadline)
    asyncio.run(scenario())


@pytest.mark.parametrize("bootstrap_outcome", ["pending", "rejected", "accepted"])
def test_reload_queries_keep_original_grace_until_bootstrap_ack(tmp_path, bootstrap_outcome):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth)
        futures, budgets = {}, {}
        for identity, name in (("query", "test.status"), ("mutation", "test.run")):
            record = server.state.create_command(identity, identity + "-request", name, {})
            record.call_deadline_monotonic = time.monotonic() + 120
            record.call_deadline_at = int(time.time() * 1000) + 120000
            budgets[identity] = (record.call_deadline_monotonic, record.call_deadline_at)
            futures[identity] = server.register_pending(identity)
            await server.send_command(identity, name, {})
        await server._handle_message(message("event", "domain_reload.starting", {}), auth, socket)
        server._suspend_or_fail_pending_on_disconnect(auth[0])
        original_grace = server._grace_deadline_monotonic
        candidate = Socket()
        candidate_auth = await hello(server, candidate, session="after-reload")
        assert server._suspended == futures and not server._pending
        assert server._domain_reloading
        assert server._grace_deadline_monotonic == original_grace
        assert all(not future.done() for future in futures.values())
        assert [item["name"] for item in candidate.sent] == ["session.hello"]

        if bootstrap_outcome == "pending":
            # Exercise the original grace's expiry while hello has succeeded but
            # main-thread bootstrap has not completed; it must still own callers.
            await server._reconnect_grace_at_deadline(asyncio.get_running_loop().time(), 1)
            assert all(future.result()["payload"]["code"] == "DOMAIN_RELOAD_TIMEOUT"
                       for future in futures.values())
            assert not server._suspended and not server._pending
        else:
            if bootstrap_outcome == "rejected":
                await bootstrap(server, candidate, candidate_auth,
                                snapshot(sequence=2, domain=1, session="after-reload"),
                                transform=lambda payload: payload.update(nonce="foreign"))
                server._suspend_or_fail_pending_on_disconnect(candidate_auth[0])
                assert server._suspended == futures and not server._pending
                assert server._grace_deadline_monotonic == original_grace
                assert all(not future.done() for future in futures.values())
                candidate = Socket()
                candidate_auth = await hello(server, candidate, session="retry-reload")
            await bootstrap(server, candidate, candidate_auth,
                            snapshot(sequence=2, domain=1, session=candidate_auth[0]))
            assert server.is_ready() and not server._domain_reloading and not server._suspended
            assert [item["name"] for item in candidate.sent] == [
                "session.hello", "session.bootstrap", "session.bootstrap.ack", "test.status"]
            assert futures["mutation"].result()["payload"]["code"] == "COMMAND_RECOVERY_REQUIRED"
            assert not futures["query"].done()
            await server._handle_message(message("result", "test.status", {"observed": True},
                                                identity="query", session=candidate_auth[0]),
                                         candidate_auth, candidate)
            assert futures["query"].result()["payload"] == {"observed": True}
            assert server.state.commands["query"].send_attempts == 2
        for identity, budget in budgets.items():
            record = server.state.commands[identity]
            assert (record.call_deadline_monotonic, record.call_deadline_at) == budget
        assert server.state.commands["mutation"].send_attempts == 1
        server._cancel_reconnect_grace()
    asyncio.run(scenario())


def test_expired_heartbeat_lease_closes_socket_and_requires_fresh_bootstrap(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth)
        closed = []
        server._force_disconnect_websocket = lambda target, **kwargs: closed.append(target)
        expired_connection = server._connection
        expired_connection.session.last_heartbeat_at -= server.session_manager.heartbeat_timeout_ms + 1
        assert not server.is_ready()
        assert closed == [socket]
        assert server.bridge_readiness_status()["connectionPhase"] == "lease_expired"
        await server._handle_message(message("heartbeat", "session.heartbeat", snapshot(sequence=100)), auth, socket)
        assert not server.is_ready() and closed == [socket]
        assert server.state.sequence == 1
        replacement = Socket()
        replacement_auth = await hello(server, replacement, session="after-expiry")
        assert not server.is_ready()
        await bootstrap(server, replacement, replacement_auth,
                        snapshot(sequence=2, session="after-expiry"))
        assert server.is_ready()
        assert server._connection is not expired_connection
        assert [item["name"] for item in replacement.sent] == [
            "session.hello", "session.bootstrap", "session.bootstrap.ack"]
    asyncio.run(scenario())


def test_failed_snapshot_persistence_keeps_gate_closed(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        server.state._persist_execution_snapshot = lambda payload: False
        await bootstrap(server, socket, auth)
        assert not server.is_ready()
        assert not server.state.execution_state()["authoritative"]
        assert not any(item["name"] == "session.bootstrap.ack" for item in socket.sent)
    asyncio.run(scenario())


def test_stop_during_ack_send_cannot_reopen_gate_or_commit_late_results(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        socket.on_send = lambda item: server.stop() if item["name"] == "session.bootstrap.ack" else None
        await bootstrap(server, socket, auth)
        assert not server.is_ready()
        assert not server.state.execution_state()["authoritative"]
        assert server.bridge_readiness_status()["connectionPhase"] == "stopping"
        future = server.register_pending("ordinary")
        await server._handle_message(message("result", "resource.editorState", {}, identity="ordinary"), auth, socket)
        assert not future.done()
    asyncio.run(scenario())


def test_result_continuation_rejects_replaced_connection_generation(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth)
        dispatcher = CommandDispatcher(server, server.state)
        call = asyncio.create_task(dispatcher.call("req", "resource.editorState", {}))
        while not any(item["name"] == "resource.editorState" for item in socket.sent):
            await asyncio.sleep(0)
        command_id = socket.sent[-1]["id"]
        await server._handle_message(message("result", "resource.editorState", {"activeScene": "old"}, identity=command_id), auth, socket)
        server._connection = replace(server._connection, generation=uuid4().hex)
        result = await call
        assert not result.ok
        assert result.error.code == "CONNECTION_CHANGED"
        assert server.state.commands[command_id].status == "failed"
        assert server.state.editor.active_scene != "old"
    asyncio.run(scenario())


def test_bound_producer_and_domain_order_survive_reconnect(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        await bootstrap(server, socket, auth, snapshot(sequence=10, domain=2))
        assert server.is_ready()
        await server._handle_message(message("event", "editor.execution_state", snapshot(sequence=999, domain=1)), auth, socket)
        await server._handle_message(message("event", "editor.execution_state", snapshot(sequence=999, epoch="foreign", domain=3)), auth, socket)
        assert (server.state.domain_generation, server.state.sequence, server.state.producer_epoch) == (2, 10, "epoch")
        new_socket = Socket()
        auth = await hello(server, new_socket)
        await bootstrap(server, new_socket, auth, snapshot(sequence=11, epoch="foreign", domain=3))
        assert not server.is_ready()
        assert server.state.producer_epoch == "epoch"
    asyncio.run(scenario())


def test_maintenance_control_before_ready_requires_exact_owner_and_independent_grant(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        path = tmp_path / ".upilot" / "config.json"
        path.parent.mkdir()
        path.write_text(json.dumps({"aiServiceMaintenance": {"approved": True, "projectPath": str(tmp_path)}}))
        payload = dict(maintenanceId=str(uuid4()), target="bridge", reason="refresh",
                       expectedProjectPath=str(tmp_path), expectedServerProcessId=os.getpid(),
                       expectedBridgeSessionId="bridge", expectedMaintenanceId="")
        assert server.maintenance_control_allowed(payload)
        assert not server.maintenance_control_allowed({**payload, "expectedBridgeSessionId": "foreign"})
        with pytest.raises(ConnectionError):
            await server.send_command("ordinary", "service.restart", payload)
        future = server.register_pending("control")
        await server.send_maintenance_control("control", payload)
        assert socket.sent[-1]["payload"]["controlKind"] == "service.maintenance"
        await server._handle_message(message("result", "service.restart", {"status": "accepted"}, identity="control"), auth, socket)
        assert (await future)["_maintenanceControl"]
        assert not server.is_ready()
    asyncio.run(scenario())


def test_health_before_bootstrap_is_local_and_explicit_probe_cannot_open_the_gate(tmp_path, monkeypatch):
    import httpx
    from types import SimpleNamespace
    from upilot_mcp import mcp_stdio_server as runtime

    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        await hello(server, socket)
        monkeypatch.setattr(runtime, "_orchestrator", server)
        monkeypatch.setattr(runtime, "refresh_config_if_changed", lambda: {})
        monkeypatch.setattr(runtime, "configured_project_root", lambda: tmp_path)
        monkeypatch.setattr(runtime, "Server", lambda config: SimpleNamespace(app=config.app))
        def business_observation(**kwargs):
            pytest.fail("Pre-ready health must not diagnose business Editor state")
        server.editor_observation = business_observation

        async def serve(captured, host, port):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=captured.app), base_url="http://fixture") as client:
                for parameters in ({}, {"probe": "bridge", "session": "bridge", "nonce": "a" * 32}):
                    result = (await client.get("/health", params=parameters)).json()
                    assert result["status"] == "ok"
                    assert result["bridgeReadyContractVersion"] == 1
                    assert result["bridgeReady"] is False
                    assert result["connectionPhase"] == "waiting_bridge_ready"
                    assert result["bootstrapReceipt"] is None
                    assert result["editor_observation"]["status"] == "unknown"
                    if parameters:
                        assert result["bridge_probe_ok"] is False
                assert [item["name"] for item in socket.sent] == ["session.hello"]
        monkeypatch.setattr(runtime, "_serve_http_sockets", serve)
        await runtime._run_http_server("127.0.0.1", 0, asyncio.create_task(asyncio.sleep(0)))
    asyncio.run(scenario())


def test_ready_deadline_and_reload_grace_are_not_extended_by_repeated_signals(tmp_path):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        connection = server._connection
        deadline = connection.deadline
        original_lease = connection.session.last_heartbeat_at
        connection.session.last_heartbeat_at -= 100000
        assert server.session_manager.is_connected()  # Initialization has a fixed budget, not the ordinary heartbeat lease.
        await server._handle_message(message("heartbeat", "session.heartbeat", snapshot()), auth, socket)
        assert connection.session.last_heartbeat_at == original_lease - 100000
        assert connection.deadline == deadline
        server._extend_grace_for_domain_reload()
        server._schedule_reconnect_grace()
        grace_deadline = server._grace_deadline_monotonic
        assert grace_deadline - asyncio.get_running_loop().time() > 3590
        server._extend_grace_for_domain_reload()
        server._schedule_reconnect_grace()
        assert server._grace_deadline_monotonic == grace_deadline
        server._cancel_reconnect_grace()
        connection.deadline = time.monotonic() - 1
        await server._readiness_timeout(connection)
        assert connection.phase == "bootstrap_timed_out"
        await bootstrap(server, socket, auth)
        assert not server.is_ready()
    asyncio.run(scenario())


@pytest.mark.parametrize("tamper", [None, "producer", "terminal", "persistence", "duplicate_stage"])
@pytest.mark.parametrize("request_id", ["compile-request", ""])
def test_complete_compile_evidence_is_checked_and_preserved(tmp_path, tamper, request_id):
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        state = snapshot()
        now = state["observedAt"]
        state.update(compileStatus="completed", compilePhase="completed", compileRequestId=request_id,
                     compileOperationId="compile-operation", terminal=True, errorsVerified=True,
                     lastCompileVerifiedAt=now, lastTerminalCompileAt=now)
        evidence = dict(version=1, projectPath=str(tmp_path), producerEpoch="epoch", compileOperationId="compile-operation",
                        processId=101, processCreatedAt=202, revision=1, persistenceVerified=True,
                        milestones=[dict(stage="terminal", phase="completed", revision=1, observedAt=now,
                                         terminal=True, errorsVerified=True, errorCount=0)])
        state["compileEvidence"] = evidence
        def alter(payload):
            payload["compileEvidence"]["compileEvidence"] = json.loads(json.dumps(evidence))
            if tamper == "producer":
                evidence["producerEpoch"] = "foreign"
            elif tamper == "terminal":
                evidence["milestones"][0]["errorsVerified"] = False
            elif tamper == "persistence":
                evidence["persistenceVerified"] = False
            elif tamper == "duplicate_stage":
                evidence["milestones"].append(dict(evidence["milestones"][0]))
            payload["compileEvidence"]["compileEvidence"] = json.loads(json.dumps(evidence))
        await bootstrap(server, socket, auth, state, transform=alter)
        assert server.is_ready() is (tamper is None)
        if tamper is None:
            assert server.state.execution_state()["compileEvidence"] == evidence
            assert server.state.compile.errors_verified
        else:
            assert server.state.accepted_snapshot_count == 0
    asyncio.run(scenario())


def test_bootstrap_has_seven_second_budget_inside_original_ready_deadline(tmp_path, monkeypatch):
    from types import SimpleNamespace
    import upilot_mcp.server as module
    async def scenario():
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        auth = await hello(server, socket)
        clock = [time.monotonic()]
        monkeypatch.setattr(module, "time", SimpleNamespace(monotonic=lambda: clock[0]))
        original_deadline = server._connection.deadline
        def expire(payload):
            assert server._connection.bootstrap_deadline == min(original_deadline, clock[0] + 7)
            clock[0] += 8
        await bootstrap(server, socket, auth, transform=expire)
        assert server._connection.deadline == original_deadline
        assert server.state.accepted_snapshot_count == 0
        assert not server.is_ready()
        assert not any(item["name"] == "session.bootstrap.ack" for item in socket.sent)
        await server._readiness_timeout(server._connection)
        assert server._connection.phase == "bootstrap_timed_out"
    asyncio.run(scenario())


def test_known_project_can_register_offline_and_unknown_compile_resumes_same_batch_after_bootstrap(tmp_path, monkeypatch):
    from unittest.mock import Mock
    from upilot_mcp.config import CONFIG
    from upilot_mcp.domain.resource_service import ResourceDomainService
    from upilot_mcp.domain.compile_service import CompileDomainService

    async def scenario():
        (tmp_path / "Assets").mkdir()
        (tmp_path / "ProjectSettings").mkdir()
        source = tmp_path / "Assets" / "Pending.cs"
        source.write_text("class Pending {}", encoding="utf-8")
        server, socket = WsOrchestratorServer(expected_project_path=str(tmp_path)), Socket()
        service = ResourceDomainService()
        service.server = server
        service._schedule_write_batch_resume = Mock()
        server.on_editor_execution_state = service._on_editor_execution_state
        monkeypatch.setattr(CONFIG, "write_access_approved", False)
        denied = await service.write_batch_register([str(source)], True)
        assert denied.error.code == "WRITE_ACCESS_NOT_APPROVED"
        monkeypatch.setattr(CONFIG, "write_access_approved", True)
        result = await service.write_batch_register([str(source)], True)
        assert result.ok and result.data["status"] == "deferred"
        assert result.data["waitingReason"] == "disconnected"
        batch_id, created_at = result.data["writeBatchId"], result.data["writeBatchCreatedAt"]
        service._schedule_write_batch_resume.assert_not_called()
        auth = await hello(server, socket)
        state = snapshot()
        state.update(compileStatus="unknown", compilePhase="unknown")
        await bootstrap(server, socket, auth, state)
        await asyncio.sleep(0)
        assert server.is_ready()
        assert CompileDomainService._compile_precondition_error("req", server.state.execution_state()) is None
        assert server.state.execution_state()["authoritative"] is True
        assert server.state.execution_state()["compilePhase"] == "unknown"
        assert not server.state.execution_state()["errorsVerified"]
        service._schedule_write_batch_resume.assert_called_once()
        batch = server.state.get_write_batch(batch_id)
        assert batch["writeBatchId"] == batch_id and batch["writeBatchCreatedAt"] == created_at
        assert batch_id in server.state._current_write_batches
        assert not batch["errorsVerified"]
        assert not any(item["name"] == "compile.request" for item in socket.sent)
    asyncio.run(scenario())


def test_offline_registration_rejects_unconfirmed_project_identity(tmp_path, monkeypatch):
    from upilot_mcp.config import CONFIG
    from upilot_mcp.domain.resource_service import ResourceDomainService
    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        service = ResourceDomainService()
        service.server = server
        monkeypatch.setattr(CONFIG, "write_access_approved", True)
        source = tmp_path / "Pending.cs"
        source.write_text("class Pending {}", encoding="utf-8")
        result = await service.write_batch_register([str(source)], True)
        assert result.error.code == "UNITY_NOT_CONNECTED"
        assert not server.state._current_write_batches
    asyncio.run(scenario())
