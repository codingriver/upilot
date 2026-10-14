from __future__ import annotations

import sqlite3

import pytest

from upilot_mcp.state_store import StateStore
from test_editor_execution_state_v2 import _snapshot


@pytest.fixture
def committed(tmp_path, monkeypatch):
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 1000)
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    payload = _snapshot(1, write_batch_id=batch["writeBatchId"], write_batch_created_at=1000, observed_at=1100)
    payload["compileRequestId"] = "request-a"
    assert state.update_editor_execution_state(payload)
    assert state.correlation_verified and state._terminal_association_key is not None
    return state, payload


def heartbeat(state, payload):
    return dict(payload, sequence=state.sequence + 1, snapshotId=f"epoch-a:0:{state.sequence + 1}",
                transition="heartbeat", observedAt=state.observed_at + 100, pendingWriteBatchId="")


def trace(monkeypatch):
    statements = []
    connect = sqlite3.connect

    def traced_connect(*args, **kwargs):
        db = connect(*args, **kwargs)
        db.set_trace_callback(statements.append)
        return db

    monkeypatch.setattr("upilot_mcp.state_store.sqlite3.connect", traced_connect)
    return statements


def association_reads(statements):
    return [sql for sql in statements if sql.lstrip().upper().startswith("SELECT") and "WRITE_BATCH" in sql.upper()]


def test_stable_terminal_heartbeats_persist_fresh_snapshots_without_association_selects(committed, monkeypatch):
    state, payload = committed
    statements = trace(monkeypatch)
    for _ in range(12):
        assert state.update_editor_execution_state(heartbeat(state, payload))
    assert association_reads(statements) == []
    assert len([sql for sql in statements if "INSERT INTO project_state" in sql]) == 12
    assert state.correlation_verified and state.received_at == 1000


def test_explicit_batch_read_still_checks_durable_evidence_and_invalidates_cache(committed, monkeypatch):
    state, payload = committed
    statements = trace(monkeypatch)
    result = state.get_write_batch(payload["writeBatchId"])
    assert result["correlationVerified"] and result["terminalSnapshot"] == payload
    assert association_reads(statements) and state._terminal_association_key is None
    statements.clear()
    assert state.update_editor_execution_state(heartbeat(state, payload))
    assert association_reads(statements)


def test_old_cached_terminal_cannot_hide_a_new_deferred_batch(committed):
    state, payload = committed
    state.editor.play_mode_state = "play"
    new = state.register_write_batch(["B.cs"], created_at=1601, files_sha256="b", compile_when_edit_mode=True)
    state.mark_write_batch(new["writeBatchId"], "deferred")
    assert state._terminal_association_key is None
    assert state.update_editor_execution_state(heartbeat(state, payload))
    assert state.pending_write_batch_id == new["writeBatchId"] and not state.correlation_verified


@pytest.mark.parametrize("field,value", [("compileRequestId", "foreign"), ("compileOperationId", "foreign"),
                                        ("domainGeneration", 1), ("producerEpoch", "epoch-b"),
                                        ("errorsVerified", False), ("lastCompileVerifiedAt", 900)])
def test_changed_identity_or_evidence_never_uses_cached_association(committed, monkeypatch, field, value):
    state, payload = committed
    statements = trace(monkeypatch)
    changed = heartbeat(state, payload)
    changed[field] = value
    state.update_editor_execution_state(changed)
    assert association_reads(statements)
    if field in {"compileRequestId", "compileOperationId", "errorsVerified", "lastCompileVerifiedAt"}:
        assert not state.correlation_verified


def test_snapshot_persistence_failure_clears_cache_and_requires_full_revalidation(committed, monkeypatch):
    state, payload = committed
    with monkeypatch.context() as patch:
        patch.setattr(state, "_persist_execution_snapshot", lambda _: False)
        assert not state.update_editor_execution_state(heartbeat(state, payload))
    assert state._terminal_association_key is None and not state.editor.authoritative
    statements = trace(monkeypatch)
    assert state.update_editor_execution_state(heartbeat(state, payload))
    assert association_reads(statements)


def test_project_change_cannot_reuse_previous_terminal_cache(committed, tmp_path):
    state, _ = committed
    state.configure_project(str(tmp_path / "other"))
    assert state._terminal_association_key is None and not state.correlation_verified


def test_shared_round_members_preserve_owner_and_use_terminal_cache(tmp_path, monkeypatch):
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 1000)
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    round_ = state.freeze_write_batch_round()
    assert state.record_write_batch_round_dispatch(round_["roundId"], "request-round")
    payload = _snapshot(1, write_batch_id=round_["writeBatchId"], write_batch_created_at=1000,
                        operation_id="operation-round", observed_at=1100)
    payload["compileRequestId"] = "request-round"
    assert state.update_editor_execution_state(payload)
    statements = trace(monkeypatch)
    assert state.update_editor_execution_state(heartbeat(state, payload))
    assert association_reads(statements) == []
    assert state.get_write_batch(round_["writeBatchId"])["compileOwnerWriteBatchId"] == round_["writeBatchId"]
