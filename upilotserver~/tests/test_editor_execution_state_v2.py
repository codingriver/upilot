from __future__ import annotations

import asyncio
import inspect
import sqlite3
from pathlib import Path

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.models import WsMessage
from upilot_mcp.responses import fail, ok
from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp.state_store import StateStore


@pytest.fixture(autouse=True)
def controlled_batch_clock(monkeypatch):
    # These evidence fixtures deliberately use small timestamps. Deadline tests
    # advance their own clock; real wall time must not expire unrelated batches.
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 3000)
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 3000)


def _snapshot(
    sequence: int,
    *,
    epoch: str = "epoch-a",
    domain: int = 0,
    phase: str = "completed",
    operation_id: str = "compile-a",
    write_batch_id: str = "",
    write_batch_created_at: int = 0,
    observed_at: int = 1000,
    terminal: bool = True,
    errors_verified: bool = True,
    transition: str = "compile_completed",
) -> dict:
    return {
        "stateContractVersion": 2,
        "projectId": "project-a",
        "producerEpoch": epoch,
        "domainGeneration": domain,
        "sequence": sequence,
        "snapshotId": f"{epoch}:{domain}:{sequence}",
        "transition": transition,
        "observedAt": observed_at,
        "connected": True,
        "authoritative": True,
        "source": "editor.execution_state",
        "playModeState": "edit",
        "isCompiling": phase in {"queued", "compiling"},
        "compileStatus": phase,
        "compilePhase": phase,
        "compileOperationId": operation_id,
        "writeBatchId": write_batch_id,
        "writeBatchCreatedAt": write_batch_created_at,
        "compileOrigin": "mcp",
        "terminal": terminal,
        "verificationPending": not terminal,
        "errorsVerified": errors_verified,
        "lastCompilerFinishedAt": observed_at - 10,
        "lastCompileVerifiedAt": observed_at if errors_verified else 0,
        "lastTerminalCompileAt": observed_at if terminal else 0,
    }


def test_v2_snapshot_ordering_rejects_duplicate_old_domain_and_old_session(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.reset_editor_session("session-new", 7)

    first = _snapshot(1, domain=1)
    first["sessionId"] = "session-new"
    assert state.update_editor_execution_state(first)
    assert not state.update_editor_execution_state(first)

    old_domain = _snapshot(99, domain=0)
    old_domain["sessionId"] = "session-new"
    assert not state.update_editor_execution_state(old_domain)

    old_session = _snapshot(2, epoch="epoch-new", domain=0)
    old_session["sessionId"] = "session-old"
    assert not state.update_editor_execution_state(old_session)
    assert state.rejected_out_of_order_count == 3

    new_epoch = _snapshot(1, epoch="epoch-new", domain=0)
    new_epoch["sessionId"] = "session-new"
    assert state.update_editor_execution_state(new_epoch)
    assert state.producer_epoch == "epoch-new"


def test_sqlite_restart_keeps_stale_evidence_but_never_restores_activity(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(
        [str(tmp_path / "A.cs")],
        created_at=900,
        files_sha256="abc",
        compile_when_edit_mode=True,
    )
    state.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="compile-a")
    assert state.update_editor_execution_state(_snapshot(1, observed_at=1000))

    restored = StateStore()
    restored.configure_project(str(tmp_path))
    execution = restored.execution_state()
    assert execution["source"] == "server-persisted"
    assert execution["authoritative"] is False
    assert execution["isStale"] is True
    assert restored.pending_write_batches() == []
    assert restored.pending_write_batch_id == ""
    assert restored.get_write_batch(batch["writeBatchId"]) is not None

    heartbeat = _snapshot(2, observed_at=1100, transition="heartbeat")
    heartbeat["playModeState"] = "play"
    assert restored.update_editor_execution_state(heartbeat)
    assert restored.pending_write_batches() == []
    assert restored.execution_state()["pendingWriteBatchId"] == ""


def test_write_batches_coalesce_before_compile_but_not_during_compile(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    first = state.register_write_batch(
        [str(tmp_path / "A.cs")], created_at=100, files_sha256="a", compile_when_edit_mode=True
    )
    second = state.register_write_batch(
        [str(tmp_path / "B.cs")], created_at=110, files_sha256="b", compile_when_edit_mode=True
    )
    assert second["writeBatchId"] == first["writeBatchId"]
    assert second["coalesced"] is True
    assert len(second["paths"]) == 2

    state.mark_write_batch(first["writeBatchId"], "compiling", compile_operation_id="compile-a")
    successor = state.register_write_batch(
        [str(tmp_path / "C.cs")], created_at=120, files_sha256="c", compile_when_edit_mode=True
    )
    assert successor["writeBatchId"] != first["writeBatchId"]


def test_only_correlated_terminal_snapshot_verifies_pending_batch(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(
        [str(tmp_path / "A.cs")], created_at=1000, files_sha256="a", compile_when_edit_mode=True
    )
    batch_id = batch["writeBatchId"]

    historical = _snapshot(1, write_batch_id="old-batch", write_batch_created_at=500, observed_at=900)
    assert state.update_editor_execution_state(historical)
    assert state.execution_state()["correlationVerified"] is False
    assert state.pending_write_batch_id == batch_id

    queued = _snapshot(
        2,
        phase="queued",
        write_batch_id=batch_id,
        write_batch_created_at=1000,
        observed_at=1010,
        terminal=False,
        errors_verified=False,
        transition="compile_queued",
    )
    assert state.update_editor_execution_state(queued)
    assert state.execution_state()["ready"] is False
    assert state.get_write_batch(batch_id)["compileOperationId"] == "compile-a"

    verified = _snapshot(
        3,
        write_batch_id=batch_id,
        write_batch_created_at=1000,
        observed_at=1100,
    )
    assert state.update_editor_execution_state(verified)
    execution = state.execution_state()
    assert execution["correlationVerified"] is True
    assert execution["lastCompileVerifiedAt"] >= 1000
    assert state.get_write_batch(batch_id)["status"] == "verified"

    restored = StateStore()
    restored.configure_project(str(tmp_path))
    assert restored.execution_state()["correlationVerified"] is True


@pytest.mark.parametrize("pending_field", ["missing", "", None, "unrelated"])
def test_historical_verified_heartbeat_cannot_hide_new_deferred_batch(tmp_path: Path, pending_field) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))

    old_batch = state.register_write_batch(
        [str(tmp_path / "Old.cs")], created_at=500, files_sha256="old", compile_when_edit_mode=True
    )
    old_batch_id = old_batch["writeBatchId"]
    assert state.update_editor_execution_state(
        _snapshot(1, write_batch_id=old_batch_id, write_batch_created_at=500, observed_at=900)
    )
    assert state.execution_state()["correlationVerified"] is True
    assert state.get_write_batch(old_batch_id)["status"] == "verified"

    state.editor.play_mode_state = "play"
    deferred = state.register_write_batch(
        [str(tmp_path / "New.cs")], created_at=1000, files_sha256="new", compile_when_edit_mode=True
    )
    deferred_id = deferred["writeBatchId"]
    state.mark_write_batch(deferred_id, "deferred")

    historical_heartbeat = _snapshot(
        2,
        write_batch_id=old_batch_id,
        write_batch_created_at=500,
        observed_at=1100,
        transition="heartbeat",
    )
    historical_heartbeat["playModeState"] = "play"
    if pending_field != "missing":
        historical_heartbeat["pendingWriteBatchId"] = pending_field
    assert state.update_editor_execution_state(historical_heartbeat)

    execution = state.execution_state()
    assert execution["correlationVerified"] is False
    assert execution["pendingWriteBatchId"] == deferred_id
    assert execution["compileDeferredReason"] == "PlayMode"
    assert state.get_write_batch(deferred_id)["status"] == "deferred"


def test_v2_busy_flag_does_not_invent_phase_or_reuse_terminal_evidence(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(["A.cs"], created_at=500, files_sha256="a", compile_when_edit_mode=True)
    completed = _snapshot(1, write_batch_id=batch["writeBatchId"],
                          write_batch_created_at=500, observed_at=900)
    assert state.update_editor_execution_state(completed)
    terminal = state.get_write_batch(batch["writeBatchId"])
    busy = dict(completed, sequence=2, snapshotId="epoch-a:0:2", isCompiling=True, observedAt=1000)
    assert state.update_editor_execution_state(busy)
    execution = state.execution_state()
    assert execution["isCompiling"] is True
    assert execution["compilePhase"] == "completed"
    assert execution["status"] == "unknown"
    assert execution["blockedReason"] == "CompilationStatePending"
    assert execution["terminal"] is False and execution["verificationPending"] is True
    assert execution["errorsVerified"] is False and execution["correlationVerified"] is False
    assert state.get_write_batch(batch["writeBatchId"]) == terminal
    assert state.update_editor_execution_state(dict(completed, sequence=3, snapshotId="epoch-a:0:3"))
    assert state.execution_state()["correlationVerified"] is True


def test_verified_batch_terminal_timestamp_is_not_rewritten_by_heartbeats(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(
        [str(tmp_path / "A.cs")], created_at=1000, files_sha256="a", compile_when_edit_mode=True
    )
    batch_id = batch["writeBatchId"]

    assert state.update_editor_execution_state(
        _snapshot(1, write_batch_id=batch_id, write_batch_created_at=1000, observed_at=1100)
    )
    terminal_updated_at = state.get_write_batch(batch_id)["updatedAt"]

    assert state.update_editor_execution_state(
        _snapshot(
            2,
            write_batch_id=batch_id,
            write_batch_created_at=1000,
            observed_at=1200,
            transition="heartbeat",
        )
    )
    assert state.get_write_batch(batch_id)["updatedAt"] == terminal_updated_at


def test_compiler_finished_is_non_ready_and_legacy_context_cannot_overwrite_v2(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    compiler_finished = _snapshot(
        1,
        phase="compiler_finished",
        terminal=False,
        errors_verified=False,
        transition="compiler_finished",
    )
    assert state.update_editor_execution_state(compiler_finished)
    execution = state.execution_state()
    assert execution["ready"] is False
    assert execution["verificationPending"] is True

    assert not state.update_editor_context({"snapshotId": "older", "compilePhase": "completed"})
    assert state.compile.phase == "compiler_finished"
    assert state.update_editor_context({"snapshotId": execution["snapshotId"], "compilePhase": "completed"})
    assert state.compile.phase == "compiler_finished"


def test_v2_mismatched_compile_diagnostics_do_not_overwrite_authoritative_current(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    current = _snapshot(1, operation_id="compile-a", write_batch_id="batch-a", write_batch_created_at=1000)
    current["compileRequestId"] = "request-a"
    current["errorCount"] = 3
    current["warningCount"] = 7
    assert state.update_editor_execution_state(current)
    state.compile.errors = [{"message": "current"}]

    assert not state.update_compile_errors({
        "compileRequestId": "request-b", "compileOperationId": "compile-b",
        "writeBatchId": "batch-b", "writeBatchCreatedAt": 2000,
        "total": 0, "errors": [], "warningCount": 99,
    })
    assert state.compile.compile_request_id == "request-a"
    assert state.compile.compile_operation_id == "compile-a"
    assert state.compile.write_batch_id == "batch-a"
    assert state.compile.write_batch_created_at == 1000
    assert state.compile.errors == [{"message": "current"}]
    assert state.compile.error_count == 3
    assert state.compile.warning_count == 7


def test_state_events_keep_transitions_and_exclude_heartbeats(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    assert state.update_editor_execution_state(_snapshot(1, transition="compile_queued", terminal=False))
    assert state.update_editor_execution_state(_snapshot(2, transition="heartbeat"))
    db_path = tmp_path / "Library" / "UPilot" / "ServerState" / "state-v2.sqlite3"
    with sqlite3.connect(db_path) as db:
        transitions = [row[0] for row in db.execute("SELECT transition FROM state_events")]
    assert transitions == ["compile_queued"]


def test_v2_heartbeat_notifies_pending_batch_coordinator_in_same_lifetime(tmp_path: Path) -> None:
    async def scenario() -> None:
        server = WsOrchestratorServer()
        server.state.configure_project(str(tmp_path))
        batch = server.state.register_write_batch(
            [str(tmp_path / "A.cs")], created_at=1000, files_sha256="a", compile_when_edit_mode=True
        )
        server.state.mark_write_batch(batch["writeBatchId"], "deferred")
        notifications: list[dict] = []

        async def on_execution(execution: dict) -> None:
            notifications.append(execution)

        server.on_editor_execution_state = on_execution
        payload = _snapshot(1, observed_at=1100, transition="heartbeat")
        payload["sessionId"] = "session-a"
        message = WsMessage(
            id="heartbeat-a",
            type="heartbeat",
            name="session.heartbeat",
            payload=payload,
            timestamp=1100,
            session_id="session-a",
        )
        await server._handle_message(message)
        await asyncio.sleep(0)

        assert len(notifications) == 1
        assert notifications[0]["playModeState"] == "edit"
        assert notifications[0]["pendingWriteBatchId"] == batch["writeBatchId"]

    asyncio.run(scenario())


class _Session:
    def __init__(self, project_path: Path) -> None:
        self.project_path = str(project_path)


class _SessionManager:
    def __init__(self, project_path: Path) -> None:
        self.active = _Session(project_path)


class _Server:
    def __init__(self, project_path: Path, state: StateStore) -> None:
        self.session_manager = _SessionManager(project_path)
        self.state = state


class _ResourceService(ResourceDomainService):
    def __init__(self, project_path: Path, state: StateStore) -> None:
        self.server = _Server(project_path, state)
        self.resume_calls = 0
        self._write_batch_resume_task = None

    def _schedule_write_batch_resume(self) -> None:
        self.resume_calls += 1


def test_playmode_write_batch_is_durable_and_resumes_once_on_editmode(tmp_path: Path, monkeypatch) -> None:
    source = tmp_path / "Assets" / "A.cs"
    source.parent.mkdir(parents=True)
    source.write_text("class A {}", encoding="utf-8")
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "play"
    service = _ResourceService(tmp_path, state)
    monkeypatch.setattr(CONFIG, "write_access_approved", True)

    result = asyncio.run(service.write_batch_register([str(source)], True))
    assert result.ok and result.data["status"] == "deferred"
    assert service.resume_calls == 0
    assert state.pending_write_batches()[0]["status"] == "deferred"

    edit_execution = {"authoritative": True, "playModeState": "edit"}
    asyncio.run(service._on_editor_execution_state(edit_execution))
    assert service.resume_calls == 1


def test_auto_resumed_compile_without_persisted_snapshot_aborts_without_claiming_success(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch(
        [str(tmp_path / "Assets" / "A.cs")],
        created_at=1000,
        files_sha256="digest",
        compile_when_edit_mode=True,
    )
    service = _ResourceService(tmp_path, state)

    async def no_sleep(_delay: float) -> None:
        return None

    async def failed_compile(**_kwargs):
        return ok(
            "req-failed",
            {"status": "failed", "phase": "failed", "terminal": True, "correlationVerified": True},
        )

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", failed_compile, raising=False)

    asyncio.run(service._resume_pending_write_batches())

    stored = state.get_write_batch(batch["writeBatchId"])
    assert stored is not None
    assert stored["status"] == "aborted"
    assert stored["outcome"] == "unknown"
    assert stored["terminal"] is True

    assert state.pending_write_batch_id == ""
    assert state.pending_write_batches() == []


def test_auto_resumed_compile_predispatch_stale_context_waits_for_fresh_editor_state(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch(
        [str(tmp_path / "Assets" / "A.cs")],
        created_at=1000,
        files_sha256="digest",
        compile_when_edit_mode=True,
    )
    service = _ResourceService(tmp_path, state)

    async def no_sleep(_delay: float) -> None:
        return None

    async def stale_before_dispatch(**_kwargs):
        return fail(
            "req-stale",
            "EDITOR_CONTEXT_NOT_READY",
            "Unity Editor context is stale.",
            {"dispatchAttempted": False},
        )

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", stale_before_dispatch, raising=False)

    asyncio.run(service._resume_pending_write_batches())

    stored = state.get_write_batch(batch["writeBatchId"])
    assert stored is not None
    assert stored["status"] == "deferred"
    assert stored["compileOperationId"] == ""
    assert stored["outcome"] == "unknown"
    assert stored["terminal"] is False


def test_legacy_recovery_is_aborted_without_replaying_compile(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch(
        [str(tmp_path / "Assets" / "A.cs")],
        created_at=1000,
        files_sha256="digest",
        compile_when_edit_mode=True,
    )
    state.mark_write_batch(
        batch["writeBatchId"],
        "recovery_required",
        compile_operation_id="compile-from-earlier-batch",
        error="Unity Editor context is stale, unknown, or recovering after Domain Reload.",
    )
    service = _ResourceService(tmp_path, state)
    calls = []

    async def no_sleep(_delay: float) -> None:
        return None

    async def stale_before_dispatch(**kwargs):
        calls.append(kwargs)
        return fail(
            "req-stale",
            "EDITOR_CONTEXT_NOT_READY",
            "Unity Editor context is stale.",
            {"dispatchAttempted": False},
        )

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", stale_before_dispatch, raising=False)

    asyncio.run(service._resume_pending_write_batches())

    stored = state.get_write_batch(batch["writeBatchId"])
    assert calls == []
    assert stored is not None
    assert stored["status"] == "aborted"
    assert stored["compileOperationId"] == "compile-from-earlier-batch"
    assert stored["terminal"] is True
    assert state.pending_write_batches() == []


def test_auto_resumed_compile_accepts_only_a_persisted_correlated_terminal(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch(
        [str(tmp_path / "Assets" / "A.cs")],
        created_at=1000,
        files_sha256="digest",
        compile_when_edit_mode=True,
    )
    service = _ResourceService(tmp_path, state)

    async def no_sleep(_delay: float) -> None:
        return None

    async def completed_compile(**_kwargs):
        terminal = _snapshot(
            1,
            operation_id="compile-persisted",
            write_batch_id=batch["writeBatchId"],
            write_batch_created_at=1000,
            observed_at=2000,
        )
        assert state.update_editor_execution_state(terminal)
        return ok("req-completed", {"status": "completed", "phase": "completed", "terminal": True, "correlationVerified": True})

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", completed_compile, raising=False)

    asyncio.run(service._resume_pending_write_batches())

    stored = state.get_write_batch(batch["writeBatchId"])
    assert stored is not None
    assert stored["status"] == "verified"
    assert stored["terminal"] is True
    assert stored["correlationVerified"] is True
    assert stored["outcome"] == "passed"


def test_auto_resumed_compile_timeout_terminates_without_replay(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 10**15
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch(
        [str(tmp_path / "Assets" / "A.cs")],
        created_at=1000,
        files_sha256="digest",
        compile_when_edit_mode=True,
    )
    service = _ResourceService(tmp_path, state)
    calls = []

    async def no_sleep(_delay: float) -> None:
        return None

    async def timed_out_compile(**kwargs):
        calls.append(kwargs)
        state.compile.compile_operation_id = "compile-timeout"
        return fail("req-timeout", "COMMAND_TIMEOUT", "compile.request timed out")

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", timed_out_compile, raising=False)

    asyncio.run(service._resume_pending_write_batches())
    asyncio.run(service._resume_pending_write_batches())

    stored = state.get_write_batch(batch["writeBatchId"])
    assert len(calls) == 1
    assert stored is not None
    assert stored["status"] == "timed_out"
    assert stored["compileOperationId"] == "compile-timeout"
    assert stored["outcome"] == "unknown"
    assert stored["terminal"] is True
    assert state.pending_write_batch_id == ""
    assert state.pending_write_batches() == []


def test_verified_successor_supersedes_fully_covered_recovery_without_rewriting_history(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    path_a = str(tmp_path / "Assets" / "A.cs")
    path_b = str(tmp_path / "Assets" / "B.cs")
    recovery = state.register_write_batch(
        [path_a, path_b], created_at=1000, files_sha256="old", compile_when_edit_mode=True,
        changes=[
            {"path": path_a, "kind": "write", "contentSha256": "old-a"},
            {"path": path_b, "kind": "write", "contentSha256": "old-b"},
        ],
    )
    state.mark_write_batch(recovery["writeBatchId"], "recovery_required", compile_operation_id="compile-old")
    successor = state.register_write_batch(
        [path_a, path_b], created_at=2000, files_sha256="new", compile_when_edit_mode=True,
        changes=[
            {"path": path_a, "kind": "write", "contentSha256": "new-a"},
            {"path": path_b, "kind": "write", "contentSha256": "old-b"},
        ],
    )
    state.mark_write_batch(successor["writeBatchId"], "compiling", compile_operation_id="compile-new")
    terminal = _snapshot(
        1, operation_id="compile-new", write_batch_id=successor["writeBatchId"],
        write_batch_created_at=successor["writeBatchCreatedAt"],
        observed_at=successor["writeBatchCreatedAt"] + 1000,
    )
    assert state.update_editor_execution_state(terminal)

    historical = state.get_write_batch(recovery["writeBatchId"])
    assert historical["status"] == "recovery_required"
    assert historical["terminal"] is False
    assert historical["outcome"] == "unknown"
    assert historical["supersededBy"] == successor["writeBatchId"]
    assert state.pending_write_batches() == []

    with sqlite3.connect(state._db_path) as db:
        db.execute(
            "UPDATE write_batches SET superseded_by='' WHERE write_batch_id=?",
            (recovery["writeBatchId"],),
        )
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    assert restored.pending_write_batch_id == ""
    assert restored.get_write_batch(recovery["writeBatchId"])["supersededBy"] == successor["writeBatchId"]


def test_verified_successor_does_not_supersede_partially_covered_recovery(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    path_a = str(tmp_path / "Assets" / "A.cs")
    path_b = str(tmp_path / "Assets" / "B.cs")
    recovery = state.register_write_batch(
        [path_a, path_b], created_at=1000, files_sha256="old", compile_when_edit_mode=True,
        changes=[
            {"path": path_a, "kind": "write", "contentSha256": "old-a"},
            {"path": path_b, "kind": "write", "contentSha256": "old-b"},
        ],
    )
    state.mark_write_batch(recovery["writeBatchId"], "recovery_required", compile_operation_id="compile-old")
    successor = state.register_write_batch(
        [path_a], created_at=2000, files_sha256="new", compile_when_edit_mode=True,
        changes=[{"path": path_a, "kind": "write", "contentSha256": "new-a"}],
    )
    state.mark_write_batch(successor["writeBatchId"], "compiling", compile_operation_id="compile-new")
    terminal = _snapshot(
        1, operation_id="compile-new", write_batch_id=successor["writeBatchId"],
        write_batch_created_at=successor["writeBatchCreatedAt"],
        observed_at=successor["writeBatchCreatedAt"] + 1000,
    )
    assert state.update_editor_execution_state(terminal)

    historical = state.get_write_batch(recovery["writeBatchId"])
    assert historical["supersededBy"] == ""
    assert [item["writeBatchId"] for item in state.pending_write_batches()] == [recovery["writeBatchId"]]


def test_write_batch_tool_is_registered_write_gated_and_has_public_schema() -> None:
    from upilot_mcp.mcp_tools import resource_tools
    from upilot_mcp.tool_registry import REGISTRY

    descriptor = REGISTRY.resolve("unity_write_batch_register")
    assert descriptor is not None
    assert descriptor.destructive is True
    assert descriptor.requires_write_access is True
    assert descriptor.play_mode_policy == "allowed"
    signature = inspect.signature(resource_tools.unity_write_batch_register)
    assert list(signature.parameters) == ["paths", "compileWhenEditMode", "deletedPaths"]
    assert signature.parameters["compileWhenEditMode"].default is True
    assert signature.parameters["deletedPaths"].default is None


@pytest.mark.parametrize("interrupted", [False, True])
def test_compile_coordinator_exception_ends_batch_once(tmp_path, monkeypatch, interrupted):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 3000
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch([str(tmp_path / "A.cs")], created_at=1000,
        files_sha256="fixture", compile_when_edit_mode=True)
    service = _ResourceService(tmp_path, state)
    calls = []

    async def no_sleep(_delay):
        pass

    async def raises(**kwargs):
        calls.append(kwargs)
        if interrupted:
            raise asyncio.CancelledError()
        raise RuntimeError("fixture coordinator failed")

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", raises, raising=False)
    if interrupted:
        with pytest.raises(asyncio.CancelledError):
            asyncio.run(service._resume_pending_write_batches())
    else:
        asyncio.run(service._resume_pending_write_batches())
    asyncio.run(service._resume_pending_write_batches())
    assert len(calls) == 1
    stored = state.get_write_batch(batch["writeBatchId"])
    assert stored["terminal"] and stored["status"] == "aborted"
    assert stored["outcome"] == "unknown"
    assert service._write_batch_active_id == ""
    assert not state.pending_write_batch_id
    assert state.pending_write_batches() == []


def test_verified_compile_clears_transient_error_but_preserves_evidence_checks(tmp_path):
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch([str(tmp_path / "A.cs")], created_at=1000,
        files_sha256="fixture", compile_when_edit_mode=True)
    state.mark_write_batch(batch["writeBatchId"], "deferred", error="Editor not ready")
    state.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="compile-a",
                           error="Editor not ready")
    assert state.update_editor_execution_state(_snapshot(1, write_batch_id=batch["writeBatchId"],
        write_batch_created_at=1000, observed_at=2000))
    stored = state.get_write_batch(batch["writeBatchId"])
    assert stored["terminal"] and stored["correlationVerified"]
    assert stored["outcome"] == "passed" and stored["error"] == ""


def test_old_batch_cannot_be_mutated_by_new_service(tmp_path):
    original = StateStore()
    original.configure_project(str(tmp_path))
    batch = original.register_write_batch([str(tmp_path / "A.cs")], created_at=1000,
        files_sha256="fixture", compile_when_edit_mode=True)
    replacement = StateStore()
    replacement.configure_project(str(tmp_path))
    before = replacement.get_write_batch(batch["writeBatchId"])
    replacement.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="late")
    replacement.mark_write_batch(batch["writeBatchId"], "aborted")
    assert replacement.get_write_batch(batch["writeBatchId"]) == before
    assert replacement.pending_write_batches() == []


def test_compile_coordinator_deadline_does_not_join_resistant_work(tmp_path, monkeypatch):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.updated_at = 3000
    state.editor.play_mode_state = "edit"
    batch = state.register_write_batch([str(tmp_path / "A.cs")], created_at=1000,
        files_sha256="fixture", compile_when_edit_mode=True)
    service = _ResourceService(tmp_path, state)
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 600990)

    async def no_sleep(_delay):
        pass

    monkeypatch.setattr(asyncio, "sleep", no_sleep)

    async def scenario():
        release = asyncio.Event()
        completed = asyncio.Event()

        async def resistant(**kwargs):
            try:
                await release.wait()
            except asyncio.CancelledError:
                await release.wait()
            finally:
                completed.set()
            state.mark_write_batch(batch["writeBatchId"], "compiling")
            return ok("late", {"status": "completed", "terminal": True})

        monkeypatch.setattr(service, "safe_compile_and_wait", resistant, raising=False)
        await asyncio.wait_for(service._resume_pending_write_batches(), timeout=1)
        assert not completed.is_set()
        assert state.get_write_batch(batch["writeBatchId"])["status"] == "timed_out"
        assert service._write_batch_active_id == ""
        assert state.pending_write_batches() == []
        release.set()
        await asyncio.wait_for(completed.wait(), timeout=1)
        assert state.get_write_batch(batch["writeBatchId"])["status"] == "timed_out"

    asyncio.run(scenario())


@pytest.mark.parametrize("status", ["pending", "deferred", "compiling", "recovery_required", "failed", "canceled", "aborted", "timed_out"])
def test_historical_batch_query_ends_wait_without_rewriting_evidence(tmp_path, status):
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(["A.cs"], created_at=900, files_sha256="abc", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    state.mark_write_batch(identity, status, error="Original diagnostic")
    original = state.get_write_batch(identity)
    current = asyncio.run(_ResourceService(tmp_path, state).write_batch_status(identity))
    assert current.data["status"] == status
    assert "historical" not in current.data

    restored = StateStore()
    restored.configure_project(str(tmp_path))
    service = _ResourceService(tmp_path, restored)
    with sqlite3.connect(restored._db_path) as db:
        rows_before = db.execute("SELECT * FROM write_batches").fetchall()
    for _ in range(2):
        result = asyncio.run(service.write_batch_status(identity))
        assert result.ok
        data = result.data
        was_terminal = original["terminal"]
        assert data["status"] == (status if was_terminal else "aborted")
        assert data["storedStatus"] == status
        assert data["terminal"] is True
        assert data["historical"] is True
        assert data["outcome"] == "unknown"
        assert data["errorsVerified"] is False
        assert data["correlationVerified"] is False
        assert data["error"] == "Original diagnostic"
        assert data["pendingAgeMs"] == 0
        assert data["attentionRequired"] is False
        assert data["waitingReason"] == ("none" if was_terminal else "service_restarted")
        assert data["lastProgressAt"] == original["updatedAt"]
        assert not restored.pending_write_batches()
        assert not restored.pending_write_batch_id
        assert restored.get_write_batch(identity) == original
    with sqlite3.connect(restored._db_path) as db:
        assert db.execute("SELECT * FROM write_batches").fetchall() == rows_before
    assert service.resume_calls == 0


def test_historical_verified_batch_preserves_compile_evidence(tmp_path):
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(["A.cs"], created_at=1000, files_sha256="abc", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    assert state.update_editor_execution_state(_snapshot(
        1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
        observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued"))
    assert state.update_editor_execution_state(_snapshot(
        2, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100))
    original = state.get_write_batch(identity)
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    result = asyncio.run(_ResourceService(tmp_path, restored).write_batch_status(identity))
    assert result.ok
    for key, value in original.items():
        assert result.data[key] == value
    assert result.data["historical"] is True
    assert result.data["status"] == "verified"
    assert result.data["terminal"] is True
    assert result.data["outcome"] == "passed"
    assert result.data["errorsVerified"] is True
    assert result.data["correlationVerified"] is True
    assert restored.pending_write_batches() == []


def test_terminal_batch_uses_one_timestamp_for_memory_and_archive(tmp_path, monkeypatch):
    state = StateStore()
    state.configure_project(str(tmp_path))
    batch = state.register_write_batch(["A.cs"], created_at=900, files_sha256="abc", compile_when_edit_mode=True)
    ticks = iter(range(4000, 4100))
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: next(ticks))
    state.mark_write_batch(batch["writeBatchId"], "aborted", error="stopped")
    original = state.get_write_batch(batch["writeBatchId"])
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    assert restored.get_write_batch(batch["writeBatchId"]) == original


def test_automatic_compile_child_is_authorized_but_other_task_is_rejected(tmp_path, monkeypatch):
    from upilot_mcp.domain.compile_service import CompileDomainService

    async def scenario():
        state = StateStore()
        state.configure_project(str(tmp_path))
        batch = state.register_write_batch(["A.cs"], created_at=1000, files_sha256="abc", compile_when_edit_mode=True)
        identity = batch["writeBatchId"]
        assert state.update_editor_execution_state(_snapshot(1, observed_at=1100))
        service = _ResourceService(tmp_path, state)
        entered = asyncio.Event()
        release = asyncio.Event()
        child_results = []

        async def compile_child(**kwargs):
            assert service._write_batch_compile_task is asyncio.current_task()
            entered.set()
            await release.wait()
            # Correlated completion lets the real guard return evidence without
            # this unit test dispatching Unity commands.
            assert state.update_editor_execution_state(_snapshot(
                2, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                observed_at=1200, terminal=False, errors_verified=False, transition="compile_queued"))
            assert state.update_editor_execution_state(_snapshot(
                3, write_batch_id=identity, write_batch_created_at=1000, observed_at=1300))
            result = await CompileDomainService._safe_compile_and_wait(
                service, write_batch_id=identity, write_batch_created_at=1000, identity={})
            child_results.append(result)
            return result

        monkeypatch.setattr(service, "safe_compile_and_wait", compile_child, raising=False)
        coordinator = asyncio.create_task(service._resume_pending_write_batches())
        service._write_batch_resume_task = coordinator
        await asyncio.wait_for(entered.wait(), timeout=1)
        duplicate = await CompileDomainService._safe_compile_and_wait(
            service, write_batch_id=identity, write_batch_created_at=1000, identity={})
        assert not duplicate.ok
        assert duplicate.error.code == "COMPILE_RECOVERY_REQUIRED"
        assert duplicate.error.detail["dispatchAttempted"] is False
        release.set()
        await asyncio.wait_for(coordinator, timeout=1)
        assert len(child_results) == 1 and child_results[0].ok
        assert state.get_write_batch(identity)["status"] == "verified"
        assert service._write_batch_compile_task is None
        assert service._write_batch_active_id == ""

    asyncio.run(scenario())
