from __future__ import annotations

import asyncio
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.editor_diagnostics import main_thread_observation
from upilot_mcp.state_store import StateStore


class Service(ProjectAnalysisDomainService, CompileDomainService, StatusDomainService):
    pass


@pytest.fixture
def setup(monkeypatch):
    clock = [20000]
    for target in ("upilot_mcp.state_store._now_ms", "upilot_mcp.domain.analysis_service.now_ms",
                   "upilot_mcp.domain.compile_service.now_ms", "upilot_mcp.domain.status_service.now_ms"):
        monkeypatch.setattr(target, lambda: clock[0])
    state = StateStore()
    state.producer_epoch = "epoch"
    state.received_at = 20000
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.session_id = "session"
    state.editor.updated_at = 20000
    state.editor.process_id = 42
    state.editor.last_main_thread_pump_at = 20000
    service = Service()
    service.server = SimpleNamespace(state=state, session_manager=SimpleNamespace(active=SimpleNamespace(
        session_id="session", process_id=42, last_heartbeat_at=20000)))
    service._resolve_verified_unity_window = lambda: {"ok": False, "reason": "fixture"}
    service._resolve_live_unity_pid = lambda: (42, {"projectIdentityVerified": True})
    service._sample_process_cpu_percent = lambda *_: 95.0
    return service, state, clock


@pytest.mark.parametrize("changes,expected,reason", [
    ({}, False, "fresh_same_identity_evidence"),
    ({"lastMainThreadPumpAt": 10000}, False, "fresh_same_identity_evidence"),
    ({"lastMainThreadPumpAt": 9999, "authoritative": False, "isStale": True}, True, "main_thread_pump_stale"),
    ({"receivedAt": 15000, "lastMainThreadPumpAt": 9999}, True, "main_thread_pump_stale"),
    ({"receivedAt": 14999, "lastMainThreadPumpAt": 9999}, None, "editor_snapshot_not_fresh"),
    ({"receivedAt": 0}, None, "editor_snapshot_not_fresh"),
    ({"lastMainThreadPumpAt": 0}, None, "main_thread_pump_missing"),
    ({"producerEpoch": ""}, None, "identity_transition_or_missing"),
    ({"sessionId": "old-session"}, None, "identity_transition_or_missing"),
    ({"unityConnected": False}, None, "unity_not_connected"),
])
def test_main_thread_observation_requires_fresh_same_identity_evidence(changes, expected, reason):
    execution = {"sessionId": "session", "producerEpoch": "epoch", "unityConnected": True,
                 "receivedAt": 20000, "lastMainThreadPumpAt": 20000, **changes}
    result = main_thread_observation(execution, "session", 20000)
    assert result["notPumping"] is expected
    assert result["reason"] == reason


@pytest.mark.parametrize("case,expected,reason", [
    ("fresh", False, "fresh_same_identity_evidence"),
    ("stalled", True, "main_thread_pump_stale"),
    ("old_sample", None, "editor_snapshot_not_fresh"),
    ("missing_pump", None, "main_thread_pump_missing"),
    ("wrong_session", None, "identity_transition_or_missing"),
])
def test_hang_compile_and_queued_diagnostics_agree(setup, case, expected, reason):
    service, state, clock = setup
    if case == "stalled":
        state.editor.last_main_thread_pump_at = 9000
        state.editor.authoritative = False  # A live stall must remain detectable.
    elif case == "old_sample":
        state.received_at = 14000
        state.editor.last_main_thread_pump_at = 9000
    elif case == "missing_pump":
        state.editor.last_main_thread_pump_at = 0
    elif case == "wrong_session":
        state.editor.session_id = "old-session"
    hang = asyncio.run(service.hang_status()).data
    compile_diagnostics = hang["lastCompile"]
    queued = compile_diagnostics["queuedDiagnostics"]
    assert hang["mainThreadUnresponsive"] is expected
    assert compile_diagnostics["editorNotPumping"] is expected
    assert queued["editorNotPumping"] is expected
    assert hang["mainThreadObservation"] == compile_diagnostics["mainThreadObservation"] == queued["mainThreadObservation"]
    assert hang["mainThreadObservation"]["reason"] == reason
    assert hang["suspectedBusyLoop"] is expected
    assert ("main_thread_not_advancing" in queued["hints"]) is (expected is True)
    if expected is None:
        assert "unity_hang_capture" not in hang["nextAction"]


def test_hang_reads_time_and_snapshot_after_cpu_sampling(setup):
    service, state, clock = setup
    state.editor.last_main_thread_pump_at = 10000
    def sample(*_):
        clock[0] = 30000
        state.received_at = 30000
        return 95.0
    service._sample_process_cpu_percent = sample
    result = asyncio.run(service.hang_status()).data
    assert result["mainThreadHeartbeatAgeMs"] == 20000
    assert result["mainThreadUnresponsive"] is True
    assert result["mainThreadObservation"]["snapshotAgeMs"] == 0


def test_cpu_sampling_does_not_keep_old_snapshot_fresh(setup):
    service, state, clock = setup
    def sample(*_):
        clock[0] += 6000
        return 95.0
    service._sample_process_cpu_percent = sample
    result = asyncio.run(service.hang_status()).data
    assert result["mainThreadUnresponsive"] is None
    assert result["suspectedBusyLoop"] is None
    assert result["mainThreadObservation"]["reason"] == "editor_snapshot_not_fresh"


def test_cpu_sample_from_previous_process_cannot_diagnose_new_session(setup):
    service, state, _ = setup
    def sample(*_):
        service.server.session_manager.active.session_id = "replacement"
        state.editor.session_id = "replacement"
        state.editor.process_id = 99
        state.editor.last_main_thread_pump_at = 9000
        return 95.0
    service._sample_process_cpu_percent = sample
    result = asyncio.run(service.hang_status()).data
    assert result["mainThreadUnresponsive"] is None
    assert result["suspectedBusyLoop"] is None
    assert result["mainThreadObservation"]["reason"] == "identity_transition_or_missing"


def test_standalone_compile_diagnostics_without_session_identity_remain_unknown(setup):
    service, state, _ = setup
    standalone = CompileDomainService()
    standalone.server = SimpleNamespace(state=state)
    result = standalone._compile_diagnostics()
    assert result["editorNotPumping"] is None
    assert result["mainThreadObservation"]["reason"] == "identity_transition_or_missing"
