from __future__ import annotations

import asyncio
import json
import os
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
from uuid import uuid4

import pytest

from upilot_mcp.service_maintenance import read_summary
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import CONFIG, REGISTRY, dispatch_public_tool
from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp.mcp_tools import status_tools


def config(project, **values):
    path = project / ".upilot" / "config.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"aiServiceMaintenance": {
        "approved": True, "projectPath": str(project), **values,
    }}), encoding="utf-8")
    return path


def record(project, **values):
    data = dict(schemaVersion=1, maintenanceId=str(uuid4()), target="server", reason="refresh",
                expectedMaintenanceId="", projectPath=str(project),
                oldServerProcessId=os.getpid(), oldBridgeSessionId="bridge",
                acceptedAtUtcMs=1000, deadlineAtUtcMs=121000, status="running", phase="verifying")
    data.update(values)
    path = project / "Library" / "UPilot" / "service-maintenance.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data), encoding="utf-8")
    return data


def service(project):
    instance = StatusDomainService()
    instance._status_project_root = lambda: project
    instance.server = SimpleNamespace(session_manager=SimpleNamespace(active=SimpleNamespace(session_id="bridge")))
    instance.editor_state = AsyncMock(return_value=ok("req", {
        "ready": True, "authoritative": True, "isStale": False, "playModeState": "edit",
    }))
    instance.dispatcher = SimpleNamespace(call=AsyncMock(return_value=ok("req", {"status": "accepted"})))
    return instance


def args(project, **values):
    return dict(maintenance_id=str(uuid4()), target="server", reason="refresh",
                expected_project_path=str(project), expected_server_process_id=os.getpid(),
                expected_bridge_session_id="bridge", expected_maintenance_id="", **values)


def test_default_is_120_and_other_grants_do_not_authorize(tmp_path):
    path = tmp_path / ".upilot" / "config.json"
    path.parent.mkdir()
    path.write_text(json.dumps({"safety": {"writeAccessApproved": True, "automationAuthorizationScopes": ["hangRestart"]}}))
    summary = read_summary(tmp_path)
    assert summary["restartTimeoutSeconds"] == 120
    assert summary["effectiveApproved"] is False
    result = asyncio.run(service(tmp_path).service_restart(**args(tmp_path)))
    assert result.error.code == "SERVICE_MAINTENANCE_NOT_APPROVED"


@pytest.mark.parametrize("value", [0, 29, 601, 120.0, "120", None, True])
def test_invalid_timeout_is_not_clamped(tmp_path, value):
    config(tmp_path, restartTimeoutSeconds=value)
    assert read_summary(tmp_path)["unavailableReason"] == "SERVICE_MAINTENANCE_CONFIG_INVALID"


@pytest.mark.parametrize("value", [30, 120, 600])
def test_configured_timeout(tmp_path, value):
    config(tmp_path, restartTimeoutSeconds=value)
    summary = read_summary(tmp_path)
    assert summary["restartTimeoutSeconds"] == value
    assert summary["effectiveApproved"] is True


def test_missing_field_and_project_relocation(tmp_path):
    config(tmp_path)
    assert read_summary(tmp_path)["restartTimeoutSeconds"] == 120
    config(tmp_path, projectPath=str(tmp_path / "other"))
    assert read_summary(tmp_path)["effectiveApproved"] is False


def test_deadline_is_observation_not_synthetic_terminal(tmp_path):
    config(tmp_path)
    original = record(tmp_path)
    summary = read_summary(tmp_path, now_ms=121000)
    assert summary["deadlineExceeded"] is True
    assert summary["terminalConfirmed"] is False
    assert summary["observationStatus"] == "deadline_exceeded_unconfirmed"
    assert summary["latest"] == original
    assert read_summary(tmp_path, now_ms=200000)["latest"]["deadlineAtUtcMs"] == 121000


def test_timeout_record_remains_terminal_after_recovery(tmp_path):
    record(tmp_path, status="timed_out", errorCode="SERVICE_RESTART_TIMEOUT", endedAtUtcMs=121000)
    summary = read_summary(tmp_path, SimpleNamespace(session_id="new"), now_ms=200000)
    assert summary["latest"]["status"] == "timed_out"
    assert summary["terminalConfirmed"] is True
    assert summary["elapsedMs"] == 120000


def test_wrong_project_or_corrupt_record_requires_recovery(tmp_path):
    record(tmp_path, projectPath=str(tmp_path / "wrong"))
    assert read_summary(tmp_path)["unavailableReason"] == "SERVICE_RESTART_RECOVERY_REQUIRED"
    (tmp_path / "Library" / "UPilot" / "service-maintenance.json").write_text("{")
    assert read_summary(tmp_path)["recordStatus"] == "corrupt"


def test_authorized_dispatch_ignores_unrelated_write_gate(tmp_path, monkeypatch):
    config(tmp_path)
    instance = service(tmp_path)
    monkeypatch.setattr(CONFIG, "write_access_approved", False)
    monkeypatch.setattr("upilot_mcp.tool_registry.refresh_config_if_changed", lambda: {})
    parameters = args(tmp_path)
    result = asyncio.run(dispatch_public_tool(instance, "unity_service_restart", {
        "maintenanceId": parameters["maintenance_id"], "target": "server", "reason": "refresh",
        "expectedProjectPath": str(tmp_path), "expectedServerProcessId": os.getpid(),
        "expectedBridgeSessionId": "bridge",
    }))
    assert result.ok
    instance.dispatcher.call.assert_awaited_once()
    assert instance.dispatcher.call.call_args.args[1] == "service.restart"


@pytest.mark.parametrize("state", [
    {"ready": False}, {"authoritative": False}, {"isStale": True}, {"playModeState": "play"},
    {"playModeState": "pause"}, {"playModeState": "enteringEdit"},
])
def test_not_ready_never_dispatches_or_switches_modes(tmp_path, state):
    config(tmp_path)
    instance = service(tmp_path)
    instance.editor_state.return_value.data.update(state)
    result = asyncio.run(instance.service_restart(**args(tmp_path)))
    assert result.error.code == "SERVICE_RESTART_EDITOR_NOT_READY"
    instance.dispatcher.call.assert_not_called()


def test_duplicate_after_lost_response_is_observation_even_after_revocation(tmp_path):
    original = record(tmp_path)
    config(tmp_path, approved=False)
    instance = service(tmp_path)
    values = args(tmp_path)
    values["maintenance_id"] = original["maintenanceId"]
    result = asyncio.run(instance.service_restart(**values))
    assert result.ok and result.data == original
    instance.dispatcher.call.assert_not_called()
    values["reason"] = "different"
    result = asyncio.run(instance.service_restart(**values))
    assert result.error.code == "SERVICE_RESTART_REQUEST_CONFLICT"


def test_busy_and_stale_identity_do_not_start_new_attempt(tmp_path):
    config(tmp_path)
    original = record(tmp_path)
    instance = service(tmp_path)
    assert asyncio.run(instance.service_restart(**args(tmp_path))).error.code == "SERVICE_RESTART_BUSY"
    record(tmp_path, **{**original, "status": "failed"})
    assert asyncio.run(instance.service_restart(**args(tmp_path))).error.code == "SERVICE_RESTART_IDENTITY_CHANGED"
    instance.dispatcher.call.assert_not_called()


def test_registry_and_public_schema_prevent_retry_and_timeout_override():
    descriptor = REGISTRY.resolve("unity_service_restart")
    assert descriptor.destructive and not descriptor.idempotent
    assert not descriptor.requires_write_access
    assert descriptor.required_editor_mode == "edit"
    tools = {tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())}
    properties = tools["unity_service_restart"].inputSchema["properties"]
    assert "timeout" not in properties and "restartTimeoutSeconds" not in properties
    assert properties["target"]["enum"] == ["bridge", "server"]
    assert "maintenanceId" in properties and "expectedMaintenanceId" in properties


def test_duplicate_json_settings_fail_closed(tmp_path):
    path = config(tmp_path)
    path.write_text('{"aiServiceMaintenance":{"approved":false,"approved":true}}')
    assert read_summary(tmp_path)["unavailableReason"] == "SERVICE_MAINTENANCE_CONFIG_INVALID"


def test_domain_reload_never_resends_maintenance():
    from upilot_mcp.server import WsOrchestratorServer

    async def run():
        server = WsOrchestratorServer.__new__(WsOrchestratorServer)
        future = asyncio.get_running_loop().create_future()
        server._pending = {"command": future}
        server.state = SimpleNamespace(mark_failed=Mock(), commands={
            "command": SimpleNamespace(name="service.restart", payload={"maintenanceId": "original"}),
        })
        server.send_command = AsyncMock()
        await server._resend_pending_commands()
        server.send_command.assert_not_called()
        assert not server._pending
        result = await future
        assert result["payload"]["code"] == "SERVICE_RESTART_RECOVERY_REQUIRED"
        assert result["payload"]["detail"]["maintenanceId"] == "original"
        server.state.mark_failed.assert_called_once()

    asyncio.run(run())


@pytest.mark.parametrize("name,payload,replay", [
    ("resource.editorState", {}, True),
    ("test.status", {}, True),
    ("compile.errors.get", {}, True),
    ("test.results", {"runGuid": "run-original"}, True),
    ("test.results", {"runGuid": "  "}, False),
    ("test.run", {"testMode": "EditMode"}, False),
    ("compile.request", {}, False),
    ("console.capture.stop", {"sessionId": "capture-original"}, False),
])
def test_reload_only_replays_original_read_only_query(name, payload, replay):
    from upilot_mcp.server import WsOrchestratorServer

    async def run():
        server = WsOrchestratorServer.__new__(WsOrchestratorServer)
        future = asyncio.get_running_loop().create_future()
        server._pending = {"original-id": future}
        server.state = SimpleNamespace(mark_failed=Mock(), commands={
            "original-id": SimpleNamespace(name=name, payload=payload),
        })
        server.send_command = AsyncMock()
        await server._resend_pending_commands()
        if replay:
            server.send_command.assert_awaited_once_with("original-id", name, payload)
            assert not future.done() and "original-id" in server._pending
            server.state.mark_failed.assert_not_called()
        else:
            server.send_command.assert_not_called()
            result = await future
            detail = result["payload"]["detail"]
            assert result["payload"]["code"] == "COMMAND_RECOVERY_REQUIRED"
            assert detail["commandId"] == "original-id" and detail["commandName"] == name
            assert detail["outcome"] == "unknown" and detail["replayAttempted"] is False
            assert not server._pending
            server.state.mark_failed.assert_called_once()

    asyncio.run(run())


def test_reload_missing_record_converges_and_completed_future_is_not_sent():
    from upilot_mcp.server import WsOrchestratorServer

    async def run():
        server = WsOrchestratorServer.__new__(WsOrchestratorServer)
        missing = asyncio.get_running_loop().create_future()
        done = asyncio.get_running_loop().create_future()
        done.set_result({"original": True})
        server._pending = {"missing-id": missing, "done-id": done}
        server.state = SimpleNamespace(mark_failed=Mock(), commands={
            "done-id": SimpleNamespace(name="test.status", payload={}),
        })
        server.send_command = AsyncMock()
        await server._resend_pending_commands()
        server.send_command.assert_not_called()
        assert (await missing)["payload"]["code"] == "COMMAND_RECOVERY_REQUIRED"
        assert done.result() == {"original": True} and not server._pending
        server.state.mark_failed.assert_not_called()

    asyncio.run(run())
