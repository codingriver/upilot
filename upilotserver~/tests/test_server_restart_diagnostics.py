from __future__ import annotations

import json
import os
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.status_service import StatusDomainService


def _service() -> StatusDomainService:
    return StatusDomainService.__new__(StatusDomainService)


def _write_record(project, **overrides):
    record = {
        "schemaVersion": 1,
        "operationId": "restart-123",
        "projectPath": str(project),
        "status": "succeeded",
        "phase": "completed",
        "oldProcessId": 10,
        "newProcessId": os.getpid(),
        "oldBridgeSessionId": "old-session",
        "newBridgeSessionId": "new-session",
        "healthVerified": True,
        "projectIdentityVerified": True,
        "bridgeVerified": True,
        "healthProjectPath": str(project),
        "requestedAtUtcMs": 1,
        "newProcessStartedAtUtcMs": 2,
        "healthVerifiedAtUtcMs": 3,
        "bridgeVerifiedAtUtcMs": 4,
        "endedAtUtcMs": 5,
    }
    record.update(overrides)
    path = project / "Library" / "UPilot" / "server-restart.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(record), encoding="utf-8")
    return path


def test_restart_summary_requires_current_server_and_new_active_bridge_session(tmp_path):
    service = _service()
    _write_record(tmp_path)

    current = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert current["recordStatus"] == "current"
    assert current["current"] is True
    assert current["verifiedSucceeded"] is True
    assert current["operationId"] == "restart-123"
    assert current["identity"]["processMatches"] is True

    wrong_session = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="old-session")
    )
    assert wrong_session["status"] == "succeeded"
    assert wrong_session["verifiedSucceeded"] is True
    assert wrong_session["identity"]["bridgeWasReplaced"] is True
    assert wrong_session["identity"]["bridgeSessionMatches"] is False


def test_restart_summary_keeps_failed_exit_evidence_but_marks_other_server_historical(tmp_path):
    service = _service()
    _write_record(
        tmp_path,
        status="failed",
        phase="failed",
        newProcessId=os.getpid() + 100000,
        exitObserved=True,
        exitCode=17,
        errorCode="server_process_exited",
        error="failed",
        stderrTail="x" * 5000,
    )

    summary = service._read_server_restart_summary(tmp_path, None)

    assert summary["recordStatus"] == "historical"
    assert summary["current"] is False
    assert summary["status"] == "failed"
    assert summary["processExit"] == {"observed": True, "exitCode": 17}
    assert summary["errorCode"] == "server_process_exited"
    assert len(summary["stderrTail"]) == 4096


def test_restart_summary_rejects_project_mismatch_and_corrupt_record(tmp_path):
    service = _service()
    path = _write_record(tmp_path, projectPath=str(tmp_path / "other"))

    mismatch = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert mismatch["recordStatus"] == "project_mismatch"
    assert mismatch["verifiedSucceeded"] is False

    path.write_text("{broken", encoding="utf-8")
    corrupt = service._read_server_restart_summary(tmp_path, None)
    assert corrupt["recordStatus"] == "corrupt"
    assert corrupt["current"] is False


def test_restart_schema_two_diagnostics_are_bounded_and_history_is_not_exposed(tmp_path):
    service = _service()
    _write_record(
        tmp_path,
        schemaVersion=2,
        gateDiagnostics=[{"key": "health", "state": "failed", "evidence": "x" * 1000}] * 20,
        statusProbeCount=3,
        statusProbeOutcome="request_timeout",
        statusProbeError="x" * 1000,
        lastBridgeCloseCode="1009",
        lastBridgeOversizeActualBytes=1300775,
        lastBridgeOversizeLimitBytes=1048576,
        recoveredAtUtcMs=25,
        recoveryReadOnlyVerified=True,
    )
    (tmp_path / "Library" / "UPilot" / "server-restart-history.json").write_text("secret")
    summary = service._read_server_restart_summary(tmp_path, None)
    assert len(summary["gateDiagnostics"]) == 10
    assert len(summary["gateDiagnostics"][0]["evidence"]) == 512
    assert summary["statusProbe"]["outcome"] == "request_timeout"
    assert len(summary["statusProbe"]["error"]) == 512
    assert summary["bridgeDiagnostics"]["oversizeActualBytes"] == 1300775
    assert summary["recovery"]["readOnlyVerified"] is True
    assert "history" not in json.dumps(summary).lower()


def test_legacy_restart_summary_has_safe_empty_optional_diagnostics(tmp_path):
    service = _service()
    _write_record(tmp_path)
    summary = service._read_server_restart_summary(tmp_path, None)
    assert summary["gateDiagnostics"] == []
    assert summary["statusProbe"]["count"] == 0
    assert summary["bridgeDiagnostics"]["closeCode"] == ""


def test_schema_two_success_requires_deployment_and_real_readonly_evidence(tmp_path):
    service = _service()
    _write_record(tmp_path, schemaVersion=2)
    summary = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert summary["verifiedSucceeded"] is False
    _write_record(tmp_path, schemaVersion=2, deploymentVerified=True, readOnlyVerified=True)
    summary = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert summary["verifiedSucceeded"] is True
    assert summary["verification"]["readOnlyVerified"] is True


@pytest.mark.parametrize("failure", ["timeout", "connection", "os", None])
def test_health_roundtrip_failure_logs_bounded_identity_and_timing_without_secrets(
        monkeypatch, tmp_path, caplog, failure):
    import asyncio
    import time
    import httpx
    from upilot_mcp import mcp_stdio_server as http_server

    session = SimpleNamespace(session_id="session-one", project_path=str(tmp_path),
                              unity_version="2022.3", last_heartbeat_at=0)
    secret_message = "ownerToken=secret-owner nonce=secret-nonce http://secret-host/private?token=secret-token"
    exception = {"timeout": TimeoutError, "connection": ConnectionError, "os": OSError}.get(failure)

    class Bridge:
        port = 8765
        session_manager = SimpleNamespace(active=session)
        def __init__(self):
            self.pending, self.commands = {}, []
        def is_ready(self):
            return True
        def register_pending(self, command_id):
            future = asyncio.get_running_loop().create_future()
            self.pending[command_id] = future
            return future
        async def send_command(self, command_id, name, payload):
            self.commands.append((name, payload))
            if failure in {"connection", "os"}:
                raise exception(secret_message)
            if failure == "timeout":
                self.pending[command_id].set_exception(exception(secret_message))
            else:
                self.pending[command_id].set_result({"type": "result", "name": name, "sessionId": "session-one"})
        def unregister_pending(self, command_id):
            self.pending.pop(command_id, None)

    bridge = Bridge()
    monkeypatch.setattr(http_server, "_orchestrator", bridge)
    monkeypatch.setattr(http_server, "refresh_config_if_changed", lambda: {})
    monkeypatch.setattr(http_server, "configured_project_root", lambda: tmp_path)
    # Replace this module's clock only; asyncio keeps its real monotonic clock.
    ticks = iter((100.0, 100.125))
    monkeypatch.setattr(http_server, "time", SimpleNamespace(time=time.time, monotonic=lambda: next(ticks)))
    original_wait_for = asyncio.wait_for
    wait_budgets = []
    async def wait_for(awaitable, timeout):
        wait_budgets.append(timeout)
        return await original_wait_for(awaitable, timeout)
    monkeypatch.setattr(http_server.asyncio, "wait_for", wait_for)
    monkeypatch.setattr(http_server, "Server", lambda config: SimpleNamespace(app=config.app))

    async def serve_without_sockets(server, _host, _port):
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=server.app), base_url="http://fixture") as client:
            basic = await client.get("/health")
            assert basic.status_code == 200 and "bridge_probe_ok" not in basic.json()
            nonce = "a" * 32
            wrong = await client.get("/health", params={"probe": "bridge", "session": "wrong", "nonce": nonce})
            assert wrong.status_code == 200 and wrong.json()["bridge_probe_ok"] is False
            assert bridge.commands == []
            assert not [record for record in caplog.records if "phase=bridge_roundtrip" in record.message]
            response = await client.get("/health", params={"probe": "bridge", "session": "session-one", "nonce": nonce})
            data = response.json()
            assert response.status_code == 200 and data["bridge_probe_nonce"] == nonce
            assert data["bridge_probe_ok"] is (failure is None)
            assert bridge.commands == [("editor.state", {})] and bridge.pending == {}
            records = [record for record in caplog.records if record.name == "upilot.mcp" and "phase=bridge_roundtrip" in record.message]
            assert len(records) == int(failure is not None)
            if failure is not None:
                assert data["bridge_probe_error"] == (exception.__name__ + ": " + secret_message)[:256]
                message = records[0].message
                assert f"serverPid={data['server_pid']}" in message
                assert f"project={tmp_path.resolve()}" in message
                assert "elapsedMs=125.0" in message and f"exceptionType={exception.__name__}" in message
                assert nonce not in message and "secret-" not in message and "http://" not in message
                assert "ownerToken" not in message and "nonce=" not in message and "token=" not in message
            assert wait_budgets == ([] if failure in {"connection", "os"} else [5.0])
    monkeypatch.setattr(http_server, "_serve_http_sockets", serve_without_sockets)
    async def run():
        await http_server._run_http_server("127.0.0.1", 0, asyncio.create_task(asyncio.sleep(0)))
    asyncio.run(run())


def test_health_bridge_probe_requires_real_roundtrip_and_matching_session(monkeypatch, tmp_path):
    import asyncio
    import httpx
    from upilot_mcp import mcp_stdio_server as http_server

    session = SimpleNamespace(session_id="session-one", project_path=str(tmp_path),
                              unity_version="2022.3", last_heartbeat_at=0)

    class Bridge:
        port = 8765
        session_manager = SimpleNamespace(active=session)
        _oversize_observations = [{"observedAt": 5, "actualBytes": 1300775,
                                   "limitBytes": 1048576, "sourceEvent": "tool result"}]
        _oversize_count = 1

        def __init__(self):
            self.pending = {}
            self.commands = []
            self.response_session = "session-one"

        def is_ready(self):
            return True

        def register_pending(self, command_id):
            future = asyncio.get_running_loop().create_future()
            self.pending[command_id] = future
            return future

        async def send_command(self, command_id, name, payload):
            self.commands.append(name)
            self.pending[command_id].set_result({"type": "result", "name": name,
                                                  "sessionId": self.response_session})

        def unregister_pending(self, command_id):
            self.pending.pop(command_id, None)

    bridge = Bridge()
    monkeypatch.setattr(http_server, "_orchestrator", bridge)
    monkeypatch.setattr(http_server, "refresh_config_if_changed", lambda: {})
    monkeypatch.setattr(http_server, "configured_project_root", lambda: tmp_path)

    class CapturedServer:
        def __init__(self, config):
            self.app = config.app

        async def serve(self, *, sockets):
            assert sockets  # prebound listeners are now passed to Uvicorn
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=self.app),
                                         base_url="http://127.0.0.1") as client:
                basic = (await client.get("/health")).json()
                assert "bridge_probe_ok" not in basic
                assert basic["editor_observation"]["status"] == "unknown"
                assert bridge.commands == []
                assert basic["bridge_diagnostics"]["oversize_actual_bytes"] == 1300775
                nonce = "a" * 32
                wrong = (await client.get("/health", params={"probe": "bridge",
                    "session": "wrong", "nonce": nonce})).json()
                assert wrong["bridge_probe_ok"] is False
                assert bridge.commands == []
                correct = (await client.get("/health", params={"probe": "bridge",
                    "session": "session-one", "nonce": nonce})).json()
                assert correct["bridge_probe_ok"] is True
                assert correct["bridge_probe_nonce"] == nonce
                bridge.response_session = "different-session"
                stale = (await client.get("/health", params={"probe": "bridge",
                    "session": "session-one", "nonce": nonce})).json()
                assert stale["bridge_probe_ok"] is False
                assert bridge.commands == ["editor.state", "editor.state"]
                assert bridge.pending == {}

    monkeypatch.setattr(http_server, "Server", CapturedServer)
    async def run():
        await http_server._run_http_server("127.0.0.1", 0, asyncio.create_task(asyncio.sleep(0)))

    asyncio.run(run())
