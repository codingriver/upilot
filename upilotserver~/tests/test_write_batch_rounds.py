from __future__ import annotations

import asyncio
import json
import sqlite3
from types import SimpleNamespace

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore
from test_editor_execution_state_v2 import _snapshot


@pytest.fixture
def setup(tmp_path, monkeypatch):
    clock = [1000]
    for target in ("upilot_mcp.state_store._now_ms", "upilot_mcp.domain.resource_service.now_ms"):
        monkeypatch.setattr(target, lambda: clock[0])
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.update_editor_execution_state(_snapshot(1, phase="idle", operation_id="", terminal=False, errors_verified=False))
    return state, clock


def register(state, clock, path="A.cs", *, automatic=True, changes=None):
    return state.register_write_batch([path], created_at=clock[0], files_sha256=f"hash-{clock[0]}",
                                      compile_when_edit_mode=automatic, changes=changes)


def pair(state, clock):
    a = register(state, clock)
    clock[0] += 601
    b = register(state, clock)
    assert a["writeBatchId"] != b["writeBatchId"]
    return a, b


def publish(state, round_state, *, phase="completed", request=None, operation="compile-round", domain=0):
    snapshot = _snapshot(state.sequence + 1, phase=phase, domain=domain,
        operation_id=operation, write_batch_id=round_state["writeBatchId"],
        write_batch_created_at=round_state["writeBatchCreatedAt"],
        observed_at=round_state["deadlineAt"] - 600000 + 50,
        terminal=phase in {"completed", "failed"}, errors_verified=phase in {"completed", "failed"})
    snapshot["compileRequestId"] = request or f"req-{round_state['roundId']}"
    snapshot["errorCount"] = 2 if phase == "failed" else 0
    assert state.update_editor_execution_state(snapshot)
    return snapshot


def dispatch(state, round_state):
    assert state.record_write_batch_round_dispatch(round_state["roundId"], f"req-{round_state['roundId']}")


class Service(CompileDomainService, ResourceDomainService):
    def __init__(self, state):
        self.server = SimpleNamespace(state=state, is_ready=lambda: True,
            session_manager=SimpleNamespace(active=SimpleNamespace(session_id="session", project_path=state.project_path)))
        self._write_batch_resume_task = None


async def no_sleep(_delay):
    pass


@pytest.mark.parametrize("phase", ["completed", "failed"])
def test_frozen_members_share_raw_evidence_not_historical_hashes(setup, phase):
    state, clock = setup
    a, b = pair(state, clock)
    assert a["filesSha256"] != b["filesSha256"]
    round_state = state.freeze_write_batch_round()
    members = [a["writeBatchId"], b["writeBatchId"]]
    assert round_state["compileMemberWriteBatchIds"] == members
    clock[0] += 1
    c = register(state, clock)  # Before compilationStarted, but already after the freeze.
    assert c["writeBatchId"] not in members
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    clock[0] += 601
    d = register(state, clock, "new.asmdef")
    terminal = publish(state, round_state, phase=phase, domain=1)  # Normal Domain Reload.
    for batch in (a, b):
        result = state.get_write_batch(batch["writeBatchId"])
        assert result["writeBatchId"] == batch["writeBatchId"]
        assert result["writeBatchCreatedAt"] == batch["writeBatchCreatedAt"]
        assert result["compileOwnerWriteBatchId"] == a["writeBatchId"]
        assert result["compileMemberWriteBatchIds"] == members
        assert result["terminalSnapshot"] == terminal  # Unity's original owner identity is untouched.
        assert result["compileOperationId"] == "compile-round"
        assert result["correlationVerified"] and result["errorsVerified"]
        assert result["outcome"] == ("passed" if phase == "completed" else "failed")
    for batch in (c, d):
        assert not state.get_write_batch(batch["writeBatchId"])["terminal"]
    next_round = state.freeze_write_batch_round()
    assert next_round["compileMemberWriteBatchIds"] == [c["writeBatchId"], d["writeBatchId"]]
    dispatch(state, next_round)
    publish(state, next_round, operation="compile-next", domain=1)
    assert state.get_write_batch(c["writeBatchId"])["compileOperationId"] == "compile-next"
    assert state.get_write_batch(b["writeBatchId"])["terminalSnapshot"] == terminal


def test_registration_coalescing_manual_and_deletions_remain_compatible(setup):
    state, clock = setup
    a = register(state, clock, changes=[{"kind": "write", "path": "A.cs", "sha256": "first"}])
    clock[0] += 1
    merged = register(state, clock, changes=[{"kind": "write", "path": "A.cs", "sha256": "second"}])
    assert merged["writeBatchId"] == a["writeBatchId"] and merged["writeBatchCreatedAt"] == a["writeBatchCreatedAt"]
    manual = register(state, clock, "Manual.cs", automatic=False)
    round_state = state.freeze_write_batch_round()
    assert round_state["compileMemberWriteBatchIds"] == [a["writeBatchId"]]
    deletion = register(state, clock, changes=[{"kind": "delete", "path": "Old.cs"},
                                              {"kind": "delete", "path": "Old.prefab"}])
    dispatch(state, round_state)
    publish(state, round_state)
    assert not state.get_write_batch(manual["writeBatchId"])["terminal"]
    next_round = state.freeze_write_batch_round()
    assert next_round["compileMemberWriteBatchIds"] == [deletion["writeBatchId"]]
    assert state.get_write_batch(deletion["writeBatchId"])["deletedPaths"] == ["Old.cs", "Old.prefab"]


@pytest.mark.parametrize("which", [0, 1])
@pytest.mark.parametrize("termination", ["aborted", "timed_out"])
def test_member_termination_including_owner_does_not_poison_round(setup, which, termination):
    state, clock = setup
    members = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    selected = members[which]["writeBatchId"]
    state.mark_write_batch(selected, termination, error="fixed terminal")
    terminal = publish(state, round_state)
    ended = state.get_write_batch(selected)
    other = state.get_write_batch(members[1 - which]["writeBatchId"])
    assert ended["status"] == termination and ended["error"] == "fixed terminal"
    assert not ended["correlationVerified"] and ended["outcome"] == "unknown"
    assert other["correlationVerified"] and other["terminalSnapshot"] == terminal
    assert state.get_write_batch_round(round_state["roundId"])["correlationVerified"]
    assert not state.update_editor_execution_state(terminal)  # Duplicate callback.
    assert state.get_write_batch(selected) == ended


def test_each_member_retains_original_deadline_and_observer_uses_maximum(setup):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    assert round_state["deadlineAt"] == b["writeBatchCreatedAt"] + 600000
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    clock[0] = a["writeBatchCreatedAt"] + 600000
    assert state.live_write_batch_round_members(round_state["roundId"]) == [b["writeBatchId"]]
    publish(state, round_state)
    assert state.get_write_batch(a["writeBatchId"])["status"] == "timed_out"
    assert state.get_write_batch(b["writeBatchId"])["correlationVerified"]


@pytest.mark.parametrize("fault", ["request", "operation", "owner_time", "origin", "unverified", "unattributed", "nonterminal"])
def test_incomplete_or_foreign_evidence_never_settles_members(setup, fault):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    snapshot = _snapshot(state.sequence + 1, write_batch_id=a["writeBatchId"],
                         write_batch_created_at=a["writeBatchCreatedAt"], observed_at=clock[0] + 10)
    snapshot["compileRequestId"] = f"req-{round_state['roundId']}"
    snapshot["compileOperationId"] = "compile-round"
    key, value = {
        "request": ("compileRequestId", "other"), "operation": ("compileOperationId", "other"),
        "owner_time": ("writeBatchCreatedAt", 1), "origin": ("compileOrigin", "unity_auto"),
        "unverified": ("errorsVerified", False), "unattributed": ("writeBatchId", ""),
        "nonterminal": ("terminal", False),
    }[fault]
    snapshot[key] = value
    assert state.update_editor_execution_state(snapshot)
    for batch in (a, b):
        result = state.get_write_batch(batch["writeBatchId"])
        assert not result["correlationVerified"] and result["outcome"] == "unknown"
        assert result["terminalSnapshot"] is None


@pytest.mark.parametrize("damage", ["membership", "request", "operation", "missing_round", "round_snapshot"])
def test_stored_member_needs_membership_identity_and_independent_round_proof(setup, damage):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state)
    with sqlite3.connect(state._db_path) as db:
        if damage == "membership":
            db.execute("UPDATE write_batch_compile_rounds SET members_json=?", (json.dumps([a["writeBatchId"]]),))
        elif damage == "missing_round":
            db.execute("DELETE FROM write_batch_compile_rounds")
        elif damage == "round_snapshot":
            db.execute("UPDATE write_batch_compile_rounds SET terminal_snapshot_json='[]'")
        else:
            db.execute(f"UPDATE write_batches SET compile_{damage}_id='other' WHERE write_batch_id=?", (b["writeBatchId"],))
    result = state.get_write_batch(b["writeBatchId"])
    assert not result["correlationVerified"] and result["outcome"] == "unknown"
    assert result["status"] == "recovery_required"


def test_warning_details_are_shared_only_with_frozen_members(setup):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    later = register(state, clock)
    dispatch(state, round_state)
    publish(state, round_state)
    warnings = [{"message": "diagnostic"}]
    assert state.persist_write_batch_warnings(write_batch_id=a["writeBatchId"], compile_operation_id="compile-round",
        compile_request_id=f"req-{round_state['roundId']}", details_available=True,
        warnings_truncated=False, warnings=warnings)
    assert state.get_write_batch(b["writeBatchId"])["warnings"] == warnings
    assert not state.get_write_batch(later["writeBatchId"])["warningDetailsAvailable"]


def test_restart_does_not_restore_round_activity_or_accept_late_completion(setup):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    restarted = StateStore()
    restarted.configure_project(state.project_path)
    assert not restarted.pending_write_batches() and not restarted._current_compile_rounds
    before = restarted.get_write_batch(b["writeBatchId"])
    publish(restarted, round_state, domain=1)
    assert restarted.get_write_batch(b["writeBatchId"]) == before
    assert restarted.freeze_write_batch_round() is None


def test_freeze_commit_failure_rolls_back_without_dispatch(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    connect = sqlite3.connect

    class FailedCommit:
        def __init__(self, *args, **kwargs):
            self.db = connect(*args, **kwargs)
            self.freeze = False
        def __enter__(self):
            return self
        def execute(self, sql, *args):
            self.freeze |= sql.startswith("INSERT INTO write_batch_compile_rounds")
            return self.db.execute(sql, *args)
        def executemany(self, *args):
            return self.db.executemany(*args)
        def __exit__(self, kind, exc, tb):
            if self.freeze and kind is None:
                self.db.rollback()
                self.db.close()
                raise sqlite3.OperationalError("commit failed")
            result = self.db.__exit__(kind, exc, tb)
            self.db.close()
            return result

    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", FailedCommit)
        with pytest.raises(sqlite3.Error, match="commit failed"):
            state.freeze_write_batch_round()
    assert not state._current_compile_rounds
    for batch in (a, b):
        assert state.get_write_batch(batch["writeBatchId"])["status"] == "pending"
        assert not state.write_batch_round_id(batch["writeBatchId"])
    with connect(state._db_path) as db:
        assert db.execute("SELECT COUNT(*) FROM write_batch_compile_rounds").fetchone()[0] == 0


def test_terminal_transaction_failure_does_not_partially_settle_round(setup):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    with sqlite3.connect(state._db_path) as db:
        db.execute("CREATE TRIGGER deny_member BEFORE UPDATE OF terminal_snapshot_json ON write_batches "
                   "BEGIN SELECT RAISE(ABORT,'evidence failure'); END")
    with pytest.raises(sqlite3.Error, match="evidence failure"):
        publish(state, round_state)
    assert not state.get_write_batch_round(round_state["roundId"])["correlationVerified"]
    assert set(state._write_batch_deadlines) == {a["writeBatchId"], b["writeBatchId"]}
    for batch in (a, b):
        assert state.get_write_batch(batch["writeBatchId"])["terminalSnapshot"] is None
    with sqlite3.connect(state._db_path) as db:
        db.execute("DROP TRIGGER deny_member")
    publish(state, round_state)  # Same operation, fresh publication, no new request.
    assert state.get_write_batch(b["writeBatchId"])["correlationVerified"]
    assert state.next_write_batch_deadline is None


def test_coordinator_drains_later_work_without_another_heartbeat(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    service = Service(state)
    calls = []
    later = []

    async def compiled(**kwargs):
        round_state = state.get_write_batch_round(kwargs["_write_batch_round_id"])
        calls.append(round_state)
        if len(calls) == 1:
            later.append(register(state, clock))
        dispatch(state, round_state)
        publish(state, round_state, phase="compiling")
        if len(calls) == 1:
            clock[0] += 601
            later.append(register(state, clock, "D.asmdef"))
        publish(state, round_state, operation=f"compile-{len(calls)}")
        return ok("done", {"phase": "completed"})

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", compiled)
    asyncio.run(service._resume_pending_write_batches())
    assert len(calls) == 2
    assert calls[0]["compileMemberWriteBatchIds"] == [a["writeBatchId"], b["writeBatchId"]]
    assert calls[1]["compileMemberWriteBatchIds"] == [batch["writeBatchId"] for batch in later]
    assert not state.pending_write_batches()


def test_known_predispatch_busy_defers_same_members_and_uncertain_dispatch_never_replays(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    service = Service(state)
    calls = []

    async def busy(**kwargs):
        calls.append(kwargs["_write_batch_round_id"])
        return fail("busy", "EDITOR_BUSY", "occupied", {"dispatchAttempted": False})

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", busy)
    asyncio.run(service._resume_pending_write_batches())
    assert state.get_write_batch(b["writeBatchId"])["status"] == "deferred"
    c = register(state, clock)
    assert c["writeBatchId"] not in [a["writeBatchId"], b["writeBatchId"]]

    async def uncertain(**kwargs):
        round_state = state.get_write_batch_round(kwargs["_write_batch_round_id"])
        calls.append(round_state["roundId"])
        dispatch(state, round_state)
        return fail("uncertain", "COMMAND_TIMEOUT", "Dispatch outcome unknown")

    monkeypatch.setattr(service, "safe_compile_and_wait", uncertain)
    asyncio.run(service._resume_pending_write_batches())
    asyncio.run(service._resume_pending_write_batches())
    assert calls[:2] == [calls[0], calls[0]]
    assert calls.count(calls[0]) == 2  # One known unstarted attempt, one uncertain dispatch; never a third.
    assert state.get_write_batch(b["writeBatchId"])["status"] == "timed_out"
    assert state.get_write_batch(c["writeBatchId"])["status"] == "timed_out"


@pytest.mark.parametrize("origin", ["mcp", "unity_auto"])
def test_unrelated_active_compile_is_deferrable_not_corrupt_identity(setup, origin):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    snapshot = _snapshot(2, phase="compiling", terminal=False, errors_verified=False)
    snapshot["compileOrigin"] = origin
    state.update_editor_execution_state(snapshot)
    service = Service(state)
    result = asyncio.run(service._safe_compile_and_wait(write_batch_id=a["writeBatchId"],
        write_batch_created_at=a["writeBatchCreatedAt"], identity={}, _write_batch_round_id=round_state["roundId"]))
    assert not result.ok and result.error.code == "EDITOR_BUSY"
    assert result.error.detail["dispatchAttempted"] is False
    assert not state.get_write_batch(b["writeBatchId"])["terminal"]


def test_real_safe_workflow_uses_round_proof_after_owner_cancellation(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    service = Service(state)
    requests = []

    async def call(request, name, payload, **kwargs):
        if name == "resource.editorState":
            return ok(request, {"isCompiling": False})
        if name == "compile.request":
            requests.append(payload)
            record = state.get_write_batch_round(round_state["roundId"])
            assert record["compileRequestId"] == request
            assert payload["writeBatchId"] == a["writeBatchId"]
            assert record["compileMemberWriteBatchIds"] == [a["writeBatchId"], b["writeBatchId"]]
            publish(state, record, phase="queued", request=request)
            return ok(request, {"compileRequestId": request, "compileOperationId": "compile-round"})
        return fail(request, "NOT_NEEDED", "Console unavailable in fixture")

    async def wait(**kwargs):
        state.mark_write_batch(a["writeBatchId"], "aborted", error="member cancelled")
        record = state.get_write_batch_round(round_state["roundId"])
        publish(state, record, request=record["compileRequestId"])
        return ok("wait", {"terminal": True})

    service.dispatcher = SimpleNamespace(call=call)
    monkeypatch.setattr(service, "compile_wait", wait)
    result = asyncio.run(service.safe_compile_and_wait(write_batch_id=a["writeBatchId"],
        write_batch_created_at=a["writeBatchCreatedAt"], _write_batch_round_id=round_state["roundId"]))
    assert result.ok and result.data["correlationVerified"]
    assert len(requests) == 1
    assert state.get_write_batch(a["writeBatchId"])["status"] == "aborted"
    assert state.get_write_batch(b["writeBatchId"])["correlationVerified"]


@pytest.mark.parametrize("end", ["aborted", "timed_out", "deferred"])
def test_round_archival_failure_releases_members_without_allowing_replay(setup, monkeypatch, end):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    if end == "deferred":
        with sqlite3.connect(state._db_path) as db:
            db.execute("UPDATE write_batch_compile_rounds SET compile_operation_id='' WHERE round_id=?", (round_state["roundId"],))
        state.get_write_batch_round(round_state["roundId"])
    for batch in (a, b):
        state.get_write_batch(batch["writeBatchId"])

    def unavailable(*_args, **_kwargs):
        raise sqlite3.OperationalError("archive unavailable")

    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", unavailable)
        assert state.end_write_batch_round(round_state["roundId"], end, error="original ending")
        for batch in (a, b):
            ended = state.get_write_batch(batch["writeBatchId"])
            assert ended["terminal"] and not ended["correlationVerified"]
            assert ended["status"] == ("aborted" if end == "deferred" else end)
            assert "archive unavailable" in ended["error"]
    assert state.freeze_write_batch_round() is None
    publish(state, round_state)
    for batch in (a, b):
        assert not state.get_write_batch(batch["writeBatchId"])["correlationVerified"]


def test_normal_disconnect_reconnect_preserves_members_request_and_deadline(setup):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    publish(state, round_state, phase="compiling")
    state.editor.connected = False
    clock[0] += 100
    assert not state.execution_state()["unityConnected"]
    assert state.get_write_batch_round(round_state["roundId"])["deadlineAt"] == round_state["deadlineAt"]
    assert state.freeze_write_batch_round() is None
    publish(state, round_state, phase="verifying", domain=1)
    assert state.editor.connected
    terminal = publish(state, round_state, domain=1)
    for batch in (a, b):
        result = state.get_write_batch(batch["writeBatchId"])
        assert result["correlationVerified"] and result["terminalSnapshot"] == terminal


def test_all_members_ended_stop_observation_not_engine_and_ignore_late_result(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    service = Service(state)
    cancelled = []
    rounds = []
    original_sleep = asyncio.sleep

    async def compile_forever(**kwargs):
        round_state = state.get_write_batch_round(kwargs["_write_batch_round_id"])
        rounds.append(round_state)
        dispatch(state, round_state)
        publish(state, round_state, phase="compiling")
        for batch in (a, b):
            state.mark_write_batch(batch["writeBatchId"], "aborted", error="member cancelled")
        try:
            await original_sleep(60)
        except asyncio.CancelledError:
            cancelled.append(True)
            raise

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", compile_forever)
    asyncio.run(service._resume_pending_write_batches())
    assert len(rounds) == 1 and cancelled == [True]
    assert state.compile.phase == "compiling"
    terminal = publish(state, rounds[0])
    for batch in (a, b):
        result = state.get_write_batch(batch["writeBatchId"])
        assert result["status"] == "aborted" and result["error"] == "member cancelled"
        assert not result["correlationVerified"] and result["terminalSnapshot"] is None
    assert state.get_write_batch_round(rounds[0]["roundId"])["terminalSnapshot"] == terminal


def test_uncertain_dispatch_observes_original_request_without_another_compile(setup, monkeypatch):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    service = Service(state)
    requests = []
    lost = []

    async def call(request, name, payload, **kwargs):
        if name == "compile.request":
            requests.append(request)
            lost.append(True)
            return fail(request, "COMMAND_TIMEOUT", "response lost after dispatch")
        if name == "resource.editorState":
            if lost:
                record = state.get_write_batch_round(round_state["roundId"])
                publish(state, record, phase="compiling", request=record["compileRequestId"])
                lost.clear()
            return ok(request, {"isCompiling": bool(requests)})
        return fail(request, "NOT_NEEDED", "Console unavailable in fixture")

    async def wait(**kwargs):
        record = state.get_write_batch_round(round_state["roundId"])
        publish(state, record, request=record["compileRequestId"])
        return ok("wait", {"terminal": True})

    service.dispatcher = SimpleNamespace(call=call)
    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "compile_wait", wait)
    result = asyncio.run(service.safe_compile_and_wait(timeout_s=2, write_batch_id=a["writeBatchId"],
        write_batch_created_at=a["writeBatchCreatedAt"], _write_batch_round_id=round_state["roundId"]))
    assert result.ok and result.data["correlationVerified"]
    assert len(requests) == 1
    for batch in (a, b):
        assert state.get_write_batch(batch["writeBatchId"])["compileRequestId"] == requests[0]
        assert state.get_write_batch(batch["writeBatchId"])["correlationVerified"]


def test_nonowner_with_equal_creation_time_cannot_poison_round_evidence(setup):
    state, clock = setup
    a, b = pair(state, clock)
    with sqlite3.connect(state._db_path) as db:
        db.execute("UPDATE write_batches SET created_at=? WHERE write_batch_id=?",
                   (a["writeBatchCreatedAt"], b["writeBatchId"]))
    round_state = state.freeze_write_batch_round()
    dispatch(state, round_state)
    foreign = _snapshot(state.sequence + 1, phase="completed", operation_id="foreign",
        write_batch_id=b["writeBatchId"], write_batch_created_at=a["writeBatchCreatedAt"],
        observed_at=clock[0], terminal=True, errors_verified=True)
    foreign["compileRequestId"] = f"req-{round_state['roundId']}"
    assert state.update_editor_execution_state(foreign)
    assert not state.get_write_batch_round(round_state["roundId"])["terminalSnapshot"]
    assert not state.get_write_batch(a["writeBatchId"])["compileOperationId"]
    publish(state, round_state)
    assert state.get_write_batch(b["writeBatchId"])["correlationVerified"]


@pytest.mark.parametrize("code", ["EDITOR_BUSY", "EDITOR_IN_PLAY_MODE"])
def test_bridge_predispatch_refusal_preserves_round_for_later_dispatch(setup, monkeypatch, code):
    state, clock = setup
    a, b = pair(state, clock)
    round_state = state.freeze_write_batch_round()
    service = Service(state)
    calls = []
    async def call(request_id, command, payload, **kwargs):
        calls.append(command)
        assert command == "compile.request"
        return fail(request_id, code, "Rejected before Refresh.")
    async def refresh():
        return ok("state", {}), state.execution_state()
    service.dispatcher = SimpleNamespace(call=call)
    monkeypatch.setattr(service, "_refresh_execution_state", refresh)
    service._suppress_compile_console_evidence = True
    result = asyncio.run(service.compile(write_batch_id=a["writeBatchId"],
        write_batch_created_at=a["writeBatchCreatedAt"], _write_batch_round_id=round_state["roundId"]))
    assert not result.ok and result.error.detail["dispatchAttempted"] is False
    assert calls == ["compile.request"]
    assert state.end_write_batch_round(round_state["roundId"], "deferred")
    next_round = state.freeze_write_batch_round()
    assert next_round["roundId"] == round_state["roundId"]
    assert next_round["compileMemberWriteBatchIds"] == [a["writeBatchId"], b["writeBatchId"]]
    assert not next_round["compileRequestId"]


def test_registration_instructs_polling_own_batch_not_global_compile(setup, tmp_path, monkeypatch):
    state, _clock = setup
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    service = Service(state)
    monkeypatch.setattr(service, "_schedule_write_batch_resume", lambda: None)
    source = tmp_path / "Saved.cs"
    source.write_bytes(b"class Saved {}")
    result = asyncio.run(service.write_batch_register([str(source)], True))
    assert result.ok and result.data["status"] == "queued"
    assert "unity_write_batch_status" in result.data["nextAction"]
    assert "this writeBatchId" in result.data["nextAction"]
    assert "unity_mcp_status" not in result.data["nextAction"]
