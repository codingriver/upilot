from __future__ import annotations

import asyncio
import sqlite3

import pytest

from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp.state_store import StateStore
from test_editor_execution_state_v2 import _snapshot


@pytest.fixture
def setup(tmp_path, monkeypatch):
    clock = [1000]
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: clock[0])
    monkeypatch.setattr("upilot_mcp.server.now_ms", lambda: clock[0])
    server = WsOrchestratorServer(port=0, expected_project_path=str(tmp_path))
    return server, server.state, clock


def register(state, clock):
    return state.register_write_batch(["Probe.cs"], created_at=clock[0],
        files_sha256="fixture", compile_when_edit_mode=True)


def forbid_sql(*args, **kwargs):
    raise AssertionError("No database inspection is needed before an active deadline.")


def test_expiry_has_no_idle_or_predue_database_work(setup, monkeypatch):
    _, state, clock = setup
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", forbid_sql)
        state.expire_write_batches()
    batch = register(state, clock)
    clock[0] += 400
    merged = register(state, clock)
    assert merged["writeBatchId"] == batch["writeBatchId"]
    assert state.next_write_batch_deadline == 601000
    clock[0] = 600999
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", forbid_sql)
        state.expire_write_batches()
    state.mark_write_batch(batch["writeBatchId"], "aborted")
    assert batch["writeBatchId"] in state._current_write_batches  # Lifetime identity remains.
    clock[0] = 900000
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", forbid_sql)
        state.expire_write_batches()
    assert state.next_write_batch_deadline is None


def test_nearest_timer_changes_only_with_active_deadline_and_stops(setup):
    server, state, clock = setup
    async def run():
        server._write_batch_deadline_loop = asyncio.get_running_loop()
        state.write_batch_deadline_changed = server._schedule_write_batch_deadline
        server._schedule_write_batch_deadline()
        assert server._write_batch_deadline_timer is None
        first = register(state, clock)
        first_timer = server._write_batch_deadline_timer
        assert first_timer is not None and server._write_batch_deadline_at == 601000
        clock[0] += 601
        second = register(state, clock)
        assert server._write_batch_deadline_timer is first_timer
        state.mark_write_batch(first["writeBatchId"], "canceled")
        second_timer = server._write_batch_deadline_timer
        assert first_timer.cancelled() and second_timer is not first_timer
        assert server._write_batch_deadline_at == second["writeBatchCreatedAt"] + 600000
        server.stop()
        assert second_timer.cancelled() and server._write_batch_deadline_timer is None
        assert state.write_batch_deadline_changed is None
    asyncio.run(run())


def test_project_reset_cancels_deadline_without_restoring_history(setup, tmp_path):
    server, state, clock = setup
    async def run():
        server._write_batch_deadline_loop = asyncio.get_running_loop()
        state.write_batch_deadline_changed = server._schedule_write_batch_deadline
        register(state, clock)
        timer = server._write_batch_deadline_timer
        state.configure_project(str(tmp_path / "Other"))
        assert timer.cancelled() and server._write_batch_deadline_timer is None
        state.configure_project(str(tmp_path))
        assert state.next_write_batch_deadline is None and not state._current_write_batches
        server.stop()
    asyncio.run(run())


def test_listener_expires_original_deadline_without_bridge_or_polling(setup, monkeypatch):
    server, state, clock = setup
    state.editor.play_mode_state = "play"
    batch = register(state, clock)
    # Registration keeps its real original deadline; only the test clock advances.
    clock[0] = 601000
    async def run():
        task = asyncio.create_task(server.start())
        try:
            assert await server.wait_until_listening(3)
            await asyncio.sleep(0.03)
            # Inspect memory directly: no status read or heartbeat triggered expiry.
            assert state._write_batch_runtime[batch["writeBatchId"]]["status"] == "timed_out"
            assert state.editor.play_mode_state == "play"
            assert server.session_manager.active is None
            assert server._write_batch_deadline_timer is None
            with monkeypatch.context() as patch:
                patch.setattr(sqlite3, "connect", forbid_sql)
                await asyncio.sleep(0.03)
        finally:
            server.stop()
            await task
    asyncio.run(run())


@pytest.mark.parametrize("phase", ["completed", "failed"])
def test_round_commit_removes_all_terminal_deadlines_but_keeps_next_round(setup, phase):
    _, state, clock = setup
    first = register(state, clock)
    clock[0] += 601
    second = register(state, clock)
    round_state = state.freeze_write_batch_round()
    state.record_write_batch_round_dispatch(round_state["roundId"], "request")
    later = register(state, clock)
    snapshot = _snapshot(1, phase=phase, write_batch_id=first["writeBatchId"],
        write_batch_created_at=first["writeBatchCreatedAt"], observed_at=clock[0])
    snapshot["compileRequestId"] = "request"
    state._persist_round_evidence(snapshot)
    assert set(state._write_batch_deadlines) == {later["writeBatchId"]}
    assert state.get_write_batch(second["writeBatchId"])["terminal"]


def test_failed_evidence_commit_keeps_deadline_for_expiry(setup, monkeypatch):
    _, state, clock = setup
    batch = register(state, clock)
    state.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="compile-a")
    with sqlite3.connect(state._db_path) as db:
        db.execute("CREATE TABLE reject_terminal(value INTEGER CHECK(value=0))")
        db.execute("CREATE TRIGGER reject_terminal_batch AFTER UPDATE OF terminal_snapshot_json ON write_batches "
                   "BEGIN INSERT INTO reject_terminal VALUES(1); END")
    snapshot = _snapshot(1, write_batch_id=batch["writeBatchId"],
        write_batch_created_at=batch["writeBatchCreatedAt"], observed_at=clock[0])
    with pytest.raises(sqlite3.IntegrityError):
        state._persist_batch_evidence(snapshot)
    assert state.next_write_batch_deadline == 601000
    assert not state.get_write_batch(batch["writeBatchId"])["terminal"]
    clock[0] = 601000
    state.expire_write_batches()
    assert state.next_write_batch_deadline is None
    assert state.get_write_batch(batch["writeBatchId"])["status"] == "timed_out"


def test_deadline_failure_releases_timer_and_cannot_coalesce_stale_row(setup, monkeypatch):
    _, state, clock = setup
    batch = register(state, clock)
    def unavailable(*args, **kwargs):
        raise sqlite3.OperationalError("fixture disk unavailable")
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", unavailable)
        state.mark_write_batch(batch["writeBatchId"], "aborted")
    assert state.next_write_batch_deadline is None
    replacement = register(state, clock)
    assert replacement["writeBatchId"] != batch["writeBatchId"]
    restored = StateStore()
    restored.configure_project(state.project_path)
    assert restored.next_write_batch_deadline is None


def test_disposed_batch_releases_deadline_and_keeps_original_outcome(setup):
    _, state, clock = setup
    batch = register(state, clock)
    state.mark_write_batch(batch["writeBatchId"], "recovery_required")
    original = state.get_write_batch(batch["writeBatchId"])
    state.dispose_write_batch(batch["writeBatchId"], original, reason="fixture", request_id="fixture")
    assert state.next_write_batch_deadline is None
    result = state.get_write_batch(batch["writeBatchId"])
    assert result["outcome"] == original["outcome"] == "unknown"
    assert result["disposition"]["status"] == "released"
