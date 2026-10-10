from __future__ import annotations

import logging
import uuid

from upilot_mcp.server import WsOrchestratorServer


def _server_with_snapshot(*, now: int, pump_at: int, heartbeat_at: int | None = None) -> WsOrchestratorServer:
    server = WsOrchestratorServer()
    project_path = f"D:/project-{now}-{uuid.uuid4().hex}"
    session = server.session_manager.on_hello(
        "session-a",
        {"projectPath": project_path, "unityVersion": "2022.3", "processId": 12},
    )
    session.last_heartbeat_at = heartbeat_at if heartbeat_at is not None else now - 1000
    server.state.configure_project(project_path)
    server.state.reset_editor_session("session-a", 12)
    assert server.state.update_editor_execution_state({
        "stateContractVersion": 2,
        "projectId": "project-a",
        "producerEpoch": "epoch-a",
        "domainGeneration": 3,
        "sequence": 1,
        "snapshotId": "epoch-a:3:1",
        "transition": "heartbeat",
        "observedAt": now - 1000,
        "connected": True,
        "authoritative": True,
        "source": "bridge-heartbeat",
        "sessionId": "session-a",
        "playModeState": "edit",
        "lastMainThreadPumpAt": pump_at,
        "mainThreadQueueDepth": 2,
        "lastDequeuedCommandId": "command-a",
    })
    return server


def test_waiting_editor_requires_fresh_network_and_stale_same_identity_pump() -> None:
    now = 100_000
    server = _server_with_snapshot(now=now, pump_at=now - 8000)

    observation = server.editor_observation(observed_at_ms=now, network_connected=True)

    assert observation["status"] == "waiting_editor"
    assert observation["pump_age_ms"] == 8000
    assert observation["heartbeat_age_ms"] == 1000
    assert observation["session_id"] == "session-a"
    assert observation["producer_epoch"] == "epoch-a"
    assert observation["domain_generation"] == 3
    assert observation["main_thread_queue_depth"] == 2
    assert observation["last_dequeued_command_id"] == "command-a"


def test_http_success_without_pump_progress_stays_waiting_then_real_progress_recovers() -> None:
    now = 200_000
    server = _server_with_snapshot(now=now, pump_at=now - 8000)
    first = server.editor_observation(observed_at_ms=now, network_connected=True)
    second = server.editor_observation(observed_at_ms=now + 1000, network_connected=True)
    assert first["status"] == "waiting_editor"
    assert second["status"] == "waiting_editor"
    assert second["observation_count"] == 2

    server.session_manager.active.last_heartbeat_at = now + 1500
    assert server.state.update_editor_execution_state({
        "stateContractVersion": 2,
        "projectId": "project-a",
        "producerEpoch": "epoch-a",
        "domainGeneration": 3,
        "sequence": 2,
        "snapshotId": "epoch-a:3:2",
        "transition": "heartbeat",
        "observedAt": now + 1500,
        "connected": True,
        "authoritative": True,
        "source": "bridge-heartbeat",
        "sessionId": "session-a",
        "playModeState": "edit",
        "lastMainThreadPumpAt": now + 1400,
    })
    recovered = server.editor_observation(observed_at_ms=now + 1600, network_connected=True)

    assert recovered["status"] == "responsive"
    assert recovered["recent_stall"]["outcome"] == "recovered"
    assert recovered["recent_stall"]["observation_count"] == 2
    assert recovered["recent_stall"]["observed_update_gap_ms"] >= 8000


def test_stale_network_or_identity_transition_is_unknown_and_interrupts_without_recovery() -> None:
    now = 300_000
    server = _server_with_snapshot(now=now, pump_at=now - 8000)
    assert server.editor_observation(observed_at_ms=now, network_connected=True)["status"] == "waiting_editor"

    unknown = server.editor_observation(observed_at_ms=now + 7000, network_connected=False)

    assert unknown["status"] == "unknown"
    assert unknown["recent_stall"]["outcome"] == "interrupted"
    assert unknown["recent_stall"]["reason"] == "network_heartbeat_not_fresh"


def test_stall_warning_is_rate_limited_and_recovery_is_logged_once(caplog) -> None:
    now = 400_000
    server = _server_with_snapshot(now=now, pump_at=now - 8000)
    caplog.set_level(logging.INFO, logger="upilot.server")

    server.editor_observation(observed_at_ms=now, network_connected=True)
    server.session_manager.active.last_heartbeat_at = now + 4500
    server.editor_observation(observed_at_ms=now + 5000, network_connected=True)
    server.session_manager.active.last_heartbeat_at = now + 9500
    server.editor_observation(observed_at_ms=now + 10000, network_connected=True)

    warnings = [record for record in caplog.records if record.levelno == logging.WARNING]
    assert len(warnings) == 2
    assert "cause unknown" in warnings[0].getMessage()
    assert "continues" in warnings[1].getMessage()

    server.session_manager.active.last_heartbeat_at = now + 10_500
    assert server.state.update_editor_execution_state({
        "stateContractVersion": 2,
        "projectId": "project-a",
        "producerEpoch": "epoch-a",
        "domainGeneration": 3,
        "sequence": 2,
        "snapshotId": "epoch-a:3:2",
        "transition": "heartbeat",
        "observedAt": now + 10_500,
        "connected": True,
        "authoritative": True,
        "source": "bridge-heartbeat",
        "sessionId": "session-a",
        "playModeState": "edit",
        "lastMainThreadPumpAt": now + 10_400,
    })
    server.editor_observation(observed_at_ms=now + 10_600, network_connected=True)
    recovered = [record for record in caplog.records
                 if record.levelno == logging.INFO and "resumed" in record.getMessage()]
    assert len(recovered) == 1
