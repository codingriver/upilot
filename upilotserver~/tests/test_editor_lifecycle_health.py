from __future__ import annotations

import asyncio
from types import SimpleNamespace
import pytest

from upilot_mcp.server import WsOrchestratorServer
import upilot_mcp.server as server_module
import upilot_mcp.session_manager as session_module


@pytest.fixture
def lifecycle(tmp_path, monkeypatch):
    clock = [100_000]
    monkeypatch.setattr(server_module, "now_ms", lambda: clock[0])
    monkeypatch.setattr(session_module, "now_ms", lambda: clock[0])
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: clock[0])
    monkeypatch.setattr(server_module, "process_exists", lambda pid: pid == 12)
    monkeypatch.setattr(server_module, "process_creation_time", lambda pid: 12345 if pid == 12 else 0)
    project = str(tmp_path / "project-a")
    server = WsOrchestratorServer(expected_project_path=project)
    session = server.session_manager.on_hello("session-a", {
        "projectPath": project, "processId": 12, "processCreatedAt": 12345, "identityVerified": True,
    })
    server.state.reset_editor_session("session-a", 12)
    session.last_heartbeat_at = clock[0]
    return server, clock


def snapshot(server, clock, sequence=1, **overrides):
    session = server.session_manager.active
    return dict(stateContractVersion=2, producerEpoch="epoch-a", domainGeneration=1, sequence=sequence,
                snapshotId=f"epoch-a:1:{sequence}", observedAt=clock[0], sessionId=session.session_id,
                processId=session.process_id, processCreatedAt=session.process_created_at,
                transition="play_mode_changed", authoritative=True, connected=True,
                playModeState="edit", lastMainThreadPumpAt=clock[0], **overrides)


def transition(server, clock, to_state="enteringPlay", **overrides):
    session = server.session_manager.active
    result = dict(transitionId=f"transition-{to_state}-{clock[0]}", toState=to_state,
                  at=clock[0], unitySessionId=session.session_id, processId=session.process_id,
                  processCreatedAt=session.process_created_at, projectPath=session.project_path)
    result.update(overrides)
    return result


def publish(server, payload, kind="event"):
    asyncio.run(server._handle_message(SimpleNamespace(type=kind, name="editor.execution_state", payload=payload,
                                               session_id=payload["sessionId"], id="test")))


@pytest.mark.parametrize("phase", ["enteringPlay", "exitingPlay"])
@pytest.mark.parametrize("seconds", [120, 180, 600])
def test_long_playmode_wait_keeps_truthful_stale_disconnected_facts(lifecycle, phase, seconds):
    server, clock = lifecycle
    start = clock[0]
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock, phase)))
    clock[0] += seconds * 1000
    server.session_manager.active.connected = False
    server.state.editor.authoritative = False
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=False)
    assert result["status"] == "waiting_lifecycle"
    assert result["lifecycle_phase"] == phase
    assert result["lifecycle_started_at_ms"] == start
    assert result["waiting_duration_ms"] == seconds * 1000
    assert result["heartbeat_age_ms"] >= seconds * 1000
    assert not server.state.editor.authoritative
    assert not server.session_manager.active.connected


@pytest.mark.parametrize("event", ["compile_queued", "compile_started", "domain_reload_starting"])
@pytest.mark.parametrize("network", [True, False])
def test_compile_reload_wait_survives_long_heartbeat_gaps(lifecycle, event, network):
    server, clock = lifecycle
    data = snapshot(server, clock, compilePhase="compiling", compileOperationId="compile-a")
    data["transition"] = event
    publish(server, data)
    clock[0] += 600000
    if network:
        server.session_manager.active.last_heartbeat_at = clock[0]
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=network)
    assert result["status"] == "waiting_lifecycle"
    assert result["lifecycle_id"] == "compile-a"
    assert result["waiting_duration_ms"] == 600000


def test_same_process_new_session_and_domain_end_wait_only_on_fresh_terminal(lifecycle):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    clock[0] += 180000
    old = server.session_manager.active
    server.session_manager.on_hello("session-b", dict(projectPath=old.project_path, processId=12,
        processCreatedAt=12345, identityVerified=True))
    server.session_manager.active.last_heartbeat_at = clock[0]
    server.state.reset_editor_session("session-b", 12)
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "waiting_lifecycle"
    end = snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock, "play"))
    end["domainGeneration"] = 2
    end["playModeState"] = "play"
    publish(server, end, "heartbeat")
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=True)
    assert result["status"] == "responsive"
    assert result["lifecycle_phase"] == "verifying_recovery"
    clock[0] += 120000
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=False)
    assert result["status"] == "unknown", "ended enteringPlay must never mask a new stall"


@pytest.mark.parametrize("invalid", ["old-session", "old-epoch", "old-domain", "old-time", "wrong-process", "wrong-project", "duplicate"])
def test_stale_or_foreign_end_cannot_end_current_wait(lifecycle, invalid):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    clock[0] += 180000
    end = snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock, "play"))
    if invalid == "old-session": end["playModeTransition"]["unitySessionId"] = "old-session"
    if invalid == "old-epoch": end["producerEpoch"] = "old-epoch"
    if invalid == "old-domain": end["domainGeneration"] = 0
    if invalid == "old-time": end["playModeTransition"]["at"] = 1
    if invalid == "wrong-process": end["playModeTransition"]["processCreatedAt"] = 54321
    if invalid == "wrong-project": end["playModeTransition"]["projectPath"] += "/other"
    if invalid == "duplicate": end["sequence"] = 1
    publish(server, end)
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "waiting_lifecycle"


@pytest.mark.parametrize("invalid", ["old-record", "unverified-session", "wrong-pid", "wrong-creation", "other-project"])
def test_untrusted_start_cannot_establish_wait(lifecycle, invalid):
    server, clock = lifecycle
    data = snapshot(server, clock, playModeTransition=transition(server, clock))
    if invalid == "old-record": data["playModeTransition"]["unitySessionId"] = "old-session"
    if invalid == "unverified-session": server.session_manager.active.identity_verified = False
    if invalid == "wrong-pid": data["processId"] = 13
    if invalid == "wrong-creation": data["processCreatedAt"] = 54321
    if invalid == "other-project": data["playModeTransition"]["projectPath"] += "/other"
    publish(server, data)
    clock[0] += 120000
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "unknown"


@pytest.mark.parametrize("fault", ["exit", "pid-reuse", "different-project", "unverified-session"])
def test_real_identity_failure_wins_over_wait(lifecycle, monkeypatch, fault):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    if fault == "exit": monkeypatch.setattr(server_module, "process_exists", lambda _: False)
    if fault == "pid-reuse": monkeypatch.setattr(server_module, "process_creation_time", lambda _: 54321)
    if fault == "different-project": server.session_manager.active.project_path += "/other"
    if fault == "unverified-session": server.session_manager.active.identity_verified = False
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "unknown"
    assert server._editor_lifecycle is None



@pytest.mark.parametrize("fault,reason", [
    ("exit", "unity_process_exited"),
    ("pid-reuse", "unity_process_identity_mismatch"),
])
def test_confirmed_process_fault_survives_repeated_health_queries(lifecycle, monkeypatch, fault, reason):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    if fault == "exit":
        monkeypatch.setattr(server_module, "process_exists", lambda _: False)
    else:
        monkeypatch.setattr(server_module, "process_creation_time", lambda _: 54321)
    for _ in range(3):
        clock[0] += 1000
        result = server.editor_observation(observed_at_ms=clock[0], network_connected=False)
        assert result["status"] == "unknown"
        assert result["reason"] == reason
        assert "lifecycle_id" not in result
        assert server._editor_lifecycle is None
    # An admitted snapshot for the failed old identity must not restart its wait.
    publish(server, snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock)))
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["reason"] == reason


def test_new_verified_process_does_not_inherit_confirmed_lifecycle_fault(lifecycle, monkeypatch):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    monkeypatch.setattr(server_module, "process_exists", lambda _: False)
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["reason"] == "unity_process_exited"
    project = server.session_manager.active.project_path
    server.session_manager.on_hello("session-new", dict(projectPath=project, processId=13,
        processCreatedAt=54321, identityVerified=True))
    server.state.reset_editor_session("session-new", 13)
    monkeypatch.setattr(server_module, "process_exists", lambda pid: pid == 13)
    monkeypatch.setattr(server_module, "process_creation_time", lambda pid: 54321 if pid == 13 else 0)
    clock[0] += 1000
    server.session_manager.active.last_heartbeat_at = clock[0]
    data = snapshot(server, clock, playModeTransition=transition(server, clock))
    data.update(producerEpoch="epoch-new", snapshotId="epoch-new:1:1")
    publish(server, data)
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=True)
    assert result["status"] == "waiting_lifecycle"
    assert result["reason"] == "confirmed_unity_lifecycle"


def test_wait_is_isolated_to_server_and_project(lifecycle, tmp_path):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    other = WsOrchestratorServer(expected_project_path=str(tmp_path / "project-b"))
    clock[0] += 600000
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "waiting_lifecycle"
    assert other.editor_observation(observed_at_ms=clock[0], network_connected=False)["status"] == "unknown"


def test_completed_compile_clears_wait_but_verification_is_not_ready(lifecycle):
    server, clock = lifecycle
    start = snapshot(server, clock, compilePhase="queued", compileOperationId="compile-a")
    start["transition"] = "compile_queued"
    publish(server, start)
    clock[0] += 600000
    pending = snapshot(server, clock, sequence=2, compilePhase="verifying", compileOperationId="compile-a")
    pending["transition"] = "domain_reload_recovered"
    publish(server, pending)
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=True)["status"] == "waiting_lifecycle"
    end = snapshot(server, clock, sequence=3, compilePhase="completed", compileOperationId="compile-a", terminal=True, errorsVerified=True)
    end["transition"] = "compile_completed"
    publish(server, end)
    server.session_manager.active.last_heartbeat_at = clock[0]
    assert server.editor_observation(observed_at_ms=clock[0], network_connected=True)["status"] == "responsive"


def test_stale_pump_is_not_misclassified_as_identity_failure(lifecycle):
    server, clock = lifecycle
    session = server.session_manager.active
    data = snapshot(server, clock)
    assert server.state.update_editor_execution_state(data)
    clock[0] += 10000
    session.last_heartbeat_at = clock[0]
    # A new network snapshot still carries the old pump, proving current stalling.
    data.update(sequence=2, snapshotId="epoch-a:1:2", observedAt=clock[0], authoritative=False)
    assert server.state.update_editor_execution_state(data)
    result = server.editor_observation(observed_at_ms=clock[0], network_connected=True)
    assert result["status"] == "waiting_editor"
    assert result["reason"] == "main_thread_pump_stale"


def test_wrong_compile_terminal_cannot_end_wait(lifecycle):
    server, clock = lifecycle
    data = snapshot(server, clock, compilePhase="compiling", compileOperationId="compile-a")
    data["transition"] = "compile_started"
    publish(server, data)
    clock[0] += 180000
    publish(server, snapshot(server, clock, sequence=2, compilePhase="completed", compileOperationId="old-compile",
                             terminal=True, errorsVerified=True))
    assert server.editor_observation(network_connected=True)["status"] == "waiting_lifecycle"


def test_ended_transition_cannot_be_resurrected_by_old_start(lifecycle):
    server, clock = lifecycle
    start = transition(server, clock)
    publish(server, snapshot(server, clock, playModeTransition=start))
    clock[0] += 180000
    publish(server, snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock, "play")))
    clock[0] += 1000
    publish(server, snapshot(server, clock, sequence=3, playModeTransition=start))
    assert server.editor_observation(network_connected=True)["status"] != "waiting_lifecycle"


def test_noncompile_domain_reload_ends_on_fresh_reconnected_domain(lifecycle):
    server, clock = lifecycle
    data = snapshot(server, clock, compilePhase="idle", reloadId="reload-a")
    data["transition"] = "domain_reload_starting"
    publish(server, data)
    clock[0] += 120000
    data = snapshot(server, clock, sequence=2, compilePhase="idle", reloadId="reload-a")
    data.update(domainGeneration=2, transition="connected_snapshot")
    publish(server, data)
    assert server.editor_observation(network_connected=True)["status"] == "responsive"
    assert server.editor_observation(network_connected=True)["lifecycle_phase"] == "verifying_recovery"


def test_recovery_evidence_does_not_survive_changed_process(lifecycle):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    clock[0] += 1000
    publish(server, snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock, "play")))
    server.session_manager.active.process_created_at += 100
    result = server.editor_observation(network_connected=True)
    assert result["status"] == "unknown"
    assert "lifecycle_id" not in result


def test_process_creation_precision_matches_existing_admission(lifecycle, monkeypatch):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    monkeypatch.setattr(server_module, "process_creation_time", lambda _: 12340)
    assert server.editor_observation(network_connected=False)["status"] == "waiting_lifecycle"


@pytest.mark.parametrize("reload_id", ["", "previous-reload"])
def test_noncompile_reload_same_domain_new_session_ends_wait(lifecycle, reload_id):
    server, clock = lifecycle
    data = snapshot(server, clock, compilePhase="idle", reloadId=reload_id)
    data["transition"] = "domain_reload_starting"
    publish(server, data)
    clock[0] += 180000
    old = server.session_manager.active
    server.session_manager.on_hello("session-b", dict(projectPath=old.project_path, processId=12,
        processCreatedAt=12345, identityVerified=True))
    server.state.reset_editor_session("session-b", 12)
    data = snapshot(server, clock, sequence=2, compilePhase="idle", reloadId=reload_id)
    data["transition"] = "connected_snapshot"
    publish(server, data)
    result = server.editor_observation(network_connected=True)
    assert result["status"] == "responsive"
    assert result["lifecycle_phase"] == "verifying_recovery"


def test_noncompile_reload_same_domain_same_session_does_not_end_wait(lifecycle):
    server, clock = lifecycle
    data = snapshot(server, clock, compilePhase="idle")
    data["transition"] = "domain_reload_starting"
    publish(server, data)
    clock[0] += 180000
    data = snapshot(server, clock, sequence=2, compilePhase="idle")
    data["transition"] = "connected_snapshot"
    publish(server, data)
    assert server.editor_observation(network_connected=True)["status"] == "waiting_lifecycle"


def test_canceled_playmode_transition_ends_on_current_editmode_terminal(lifecycle):
    server, clock = lifecycle
    publish(server, snapshot(server, clock, playModeTransition=transition(server, clock)))
    clock[0] += 180000
    publish(server, snapshot(server, clock, sequence=2, playModeTransition=transition(server, clock, "edit")))
    server.session_manager.active.last_heartbeat_at = clock[0]
    result = server.editor_observation(network_connected=True)
    assert result["status"] == "responsive"
    assert result["lifecycle_phase"] == "verifying_recovery"
