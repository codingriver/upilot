from __future__ import annotations

import asyncio
import inspect
import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.domain.status_service import StatusDomainService
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


@pytest.mark.parametrize("damage", [
    "malformed_json", "array_root", "missing_snapshot", "wrong_batch", "wrong_operation",
    "wrong_request", "stale_verification", "wrong_created_at", "nonterminal",
    "unverified_errors", "wrong_phase", "array_phase", "object_phase",
])
def test_corrupt_historical_batch_never_verifies_or_dispatches(tmp_path, damage):
    import json
    from upilot_mcp.domain.compile_service import CompileDomainService

    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    queued["compileRequestId"] = "request-a"
    assert store.update_editor_execution_state(queued)
    terminal = _snapshot(2, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    terminal["compileRequestId"] = "request-a"
    assert store.update_editor_execution_state(terminal)
    assert store.get_write_batch(identity)["correlationVerified"] is True
    changes = {
        "wrong_batch": ("writeBatchId", "foreign"),
        "wrong_operation": ("compileOperationId", "foreign"),
        "wrong_request": ("compileRequestId", "foreign"),
        "stale_verification": ("lastCompileVerifiedAt", 999),
        "wrong_created_at": ("writeBatchCreatedAt", 999),
        "nonterminal": ("terminal", False),
        "unverified_errors": ("errorsVerified", False),
        "wrong_phase": ("compilePhase", "failed"),
        "array_phase": ("compilePhase", ["completed"]),
        "object_phase": ("compilePhase", {"phase": "completed"}),
    }
    if damage in changes:
        key, value = changes[damage]
        terminal[key] = value
    raw = {"malformed_json": "{", "array_root": "[]", "missing_snapshot": None}.get(damage, json.dumps(terminal))
    with sqlite3.connect(store._db_path) as db:
        db.execute("UPDATE write_batches SET terminal_snapshot_json=? WHERE write_batch_id=?", (raw, identity))
        before = db.execute("SELECT * FROM write_batches").fetchall()
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    result = asyncio.run(_ResourceService(tmp_path, restored).write_batch_status(identity))
    assert result.ok and result.data["historical"] is True
    assert result.data["correlationVerified"] is False
    assert result.data["errorsVerified"] is False
    assert result.data["outcome"] == "unknown"
    assert result.data["status"] == "recovery_required"

    service = CompileDomainService()
    service.server = _Server(tmp_path, restored)
    starts = []
    async def unexpected_start(*args, **kwargs):
        starts.append(kwargs)
        raise AssertionError("Damaged historical evidence cannot authorize a replacement compile.")
    service.compile = unexpected_start
    response = asyncio.run(service.safe_compile_and_wait(write_batch_id=identity, write_batch_created_at=1000))
    assert not response.ok and response.error.code == "COMPILE_CORRELATION_NOT_VERIFIED"
    assert starts == [] and restored.pending_write_batches() == []
    with sqlite3.connect(restored._db_path) as db:
        assert db.execute("SELECT * FROM write_batches").fetchall() == before


@pytest.mark.parametrize("damage", ["missing_operation", "missing_request", "wrong_request"])
def test_attach_without_exact_persisted_request_never_dispatches(tmp_path, damage):
    from upilot_mcp.domain.compile_service import CompileDomainService

    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    operation = "" if damage == "missing_operation" else "compile-a"
    request = "" if damage == "missing_request" else "request-a"
    with sqlite3.connect(store._db_path) as db:
        db.execute("UPDATE write_batches SET status='compiling',compile_operation_id=?,compile_request_id=? WHERE write_batch_id=?",
                   (operation, request, identity))
        before = db.execute("SELECT * FROM write_batches").fetchall()
    service = CompileDomainService()
    service.server = _Server(tmp_path, store)
    starts = []
    async def unexpected_start(*args, **kwargs):
        starts.append(kwargs)
        raise AssertionError("Unknown attachment identity cannot authorize another compile.")
    service.compile = unexpected_start
    result = asyncio.run(service.safe_compile_and_wait(
        write_batch_id=identity, write_batch_created_at=1000,
        attach_compile_request_id="foreign" if damage == "wrong_request" else "request-a"))
    assert not result.ok
    assert result.error.code == ("COMPILE_OPERATION_MISMATCH" if damage == "wrong_request" else "COMPILE_RECOVERY_REQUIRED")
    assert starts == []
    with sqlite3.connect(store._db_path) as db:
        assert db.execute("SELECT * FROM write_batches").fetchall() == before


def test_concurrent_public_safe_wait_reserves_batch_before_first_await(tmp_path, monkeypatch):
    from upilot_mcp.domain.compile_service import CompileDomainService

    async def scenario():
        store = StateStore()
        store.configure_project(str(tmp_path))
        batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
        identity = batch["writeBatchId"]
        service = CompileDomainService()
        service.server = _Server(tmp_path, store)
        service.dispatcher = object()
        entered, release = asyncio.Event(), asyncio.Event()
        calls = []
        async def begin(*args):
            entered.set()
            await release.wait()
            return {}
        async def finish(*args):
            return {}
        async def workflow(*args):
            calls.append(args)
            return ok("observed", {"writeBatchId": identity})
        monkeypatch.setattr("upilot_mcp.domain.compile_service.begin_console_evidence", begin)
        monkeypatch.setattr("upilot_mcp.domain.compile_service.finish_console_evidence", finish)
        monkeypatch.setattr(service, "_safe_compile_and_wait", workflow)
        first = asyncio.create_task(service.safe_compile_and_wait(write_batch_id=identity))
        try:
            await asyncio.wait_for(entered.wait(), 1)
            duplicate = await service.safe_compile_and_wait(write_batch_id=identity)
            assert not duplicate.ok and duplicate.error.code == "COMPILE_RECOVERY_REQUIRED"
            assert duplicate.error.detail["dispatchAttempted"] is False
            assert calls == []
            release.set()
            assert (await asyncio.wait_for(first, 1)).ok
            assert len(calls) == 1
            assert service._write_batch_safe_waits == set()
        finally:
            release.set()
            if not first.done():
                first.cancel()
                await asyncio.gather(first, return_exceptions=True)
    asyncio.run(scenario())


@pytest.mark.parametrize("first_status", ["verified", "failed", "aborted", "timed_out"])
@pytest.mark.parametrize("late_phase", ["completed", "failed"])
def test_late_terminal_cannot_rewrite_closed_batch_or_complete_successor(tmp_path, first_status, late_phase):
    store = StateStore()
    store.configure_project(str(tmp_path))
    first = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = first["writeBatchId"]
    assert store.update_editor_execution_state(_snapshot(
        1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
        observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued"))
    if first_status in {"verified", "failed"}:
        assert store.update_editor_execution_state(_snapshot(
            2, phase="completed" if first_status == "verified" else "failed",
            write_batch_id=identity, write_batch_created_at=1000, observed_at=1100))
    else:
        store.mark_write_batch(identity, first_status, error="Original terminal reason")
    original = store.get_write_batch(identity)
    assert original["status"] == first_status and original["terminal"] is True
    successor = store.register_write_batch(["B.cs"], created_at=1200, files_sha256="b", compile_when_edit_mode=True)
    successor_id = successor["writeBatchId"]
    assert successor_id != identity
    with sqlite3.connect(store._db_path) as db:
        original_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
    # A fresh producer sequence carrying an old operation's late terminal is not a new outcome.
    assert store.update_editor_execution_state(_snapshot(
        3, phase=late_phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1300))
    assert store.get_write_batch(identity) == original
    assert store.pending_write_batch_id == successor_id
    assert store.get_write_batch(successor_id)["correlationVerified"] is False
    assert store.get_write_batch(successor_id)["terminalSnapshot"] is None
    assert store.update_editor_execution_state(_snapshot(
        4, phase="queued", operation_id="compile-b", write_batch_id=successor_id, write_batch_created_at=1200,
        observed_at=1310, terminal=False, errors_verified=False, transition="compile_queued"))
    assert store.update_editor_execution_state(_snapshot(
        5, operation_id="compile-b", write_batch_id=successor_id, write_batch_created_at=1200, observed_at=1400))
    assert store.get_write_batch(successor_id)["outcome"] == "passed"
    assert store.get_write_batch(identity) == original
    with sqlite3.connect(store._db_path) as db:
        assert db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone() == original_row


@pytest.mark.parametrize("phase", ["completed", "failed"])
def test_rejected_terminal_write_rolls_back_status_and_snapshot(tmp_path, phase):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    queued["compileRequestId"] = "request-a"
    assert store.update_editor_execution_state(queued)
    original = store.get_write_batch(identity)
    with sqlite3.connect(store._db_path) as db:
        original_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        # Fail after SQLite applies the UPDATE, exercising real statement rollback
        # rather than replacing the persistence method with a successful mock.
        db.execute("CREATE TRIGGER reject_terminal_evidence AFTER UPDATE OF terminal_snapshot_json "
                   "ON write_batches WHEN NEW.terminal_snapshot_json IS NOT NULL "
                   "BEGIN SELECT RAISE(ABORT, 'injected terminal evidence rejection'); END")
    terminal = _snapshot(2, phase=phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    terminal["compileRequestId"] = "request-a"
    with pytest.raises(sqlite3.IntegrityError, match="injected terminal evidence rejection"):
        store.update_editor_execution_state(terminal)
    assert store.get_write_batch(identity) == original
    assert store.pending_write_batch_id == identity
    assert not store.execution_state()["correlationVerified"]
    assert not store.execution_state()["ready"]
    with sqlite3.connect(store._db_path) as db:
        assert db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone() == original_row
        db.execute("DROP TRIGGER reject_terminal_evidence")
    # A later authoritative snapshot may persist evidence; no compile is started.
    terminal["sequence"] = 3
    terminal["snapshotId"] = "epoch-a:0:3"
    assert store.update_editor_execution_state(terminal)
    result = store.get_write_batch(identity)
    assert result["status"] == ("verified" if phase == "completed" else "failed")
    assert result["correlationVerified"] and result["terminalSnapshot"] == terminal
    assert store.pending_write_batch_id == ""


def test_initialization_and_import_facts_follow_authoritative_snapshot_not_disk(tmp_path, monkeypatch):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.reset_editor_session("session-new", 7)
    snapshot = _snapshot(1)
    snapshot.update(sessionId="session-new", isUpdating=True,
                    maintenanceInitialization={"recoveryChecked": True, "checkedAt": 900, "storageError": "", "source": "unity-maintenance-initialization"})
    assert state.update_editor_execution_state(snapshot)
    observed = state.execution_state()
    assert observed["isUpdating"] is True
    assert observed["maintenanceInitialization"]["checkedAt"] == 900
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 4000)
    assert state.execution_state(stale_after_ms=250)["maintenanceInitialization"] is None
    legacy = _snapshot(2)
    legacy["sessionId"] = "session-new"
    assert state.update_editor_execution_state(legacy)
    assert state.execution_state()["maintenanceInitialization"] is None
    assert state.execution_state()["isUpdating"] is None


@pytest.mark.parametrize("phase", ["completed", "failed"])
def test_deferred_constraint_commit_failure_does_not_publish_verified_write_batch(tmp_path, monkeypatch, phase):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    queued["compileRequestId"] = "request-a"
    assert store.update_editor_execution_state(queued)
    original = store.get_write_batch(identity)
    connect = sqlite3.connect
    with connect(store._db_path) as db:
        assert db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
        original_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        # Keep the real WAL mode. The test-only deferred foreign key permits the
        # UPDATE statement and fails at COMMIT, not at execute()/a mocked save.
        db.execute("CREATE TABLE commit_fault_parent(id INTEGER PRIMARY KEY)")
        db.execute("CREATE TABLE commit_fault_child(id INTEGER REFERENCES commit_fault_parent(id) "
                   "DEFERRABLE INITIALLY DEFERRED)")
        db.execute("CREATE TRIGGER inject_commit_failure AFTER UPDATE OF terminal_snapshot_json "
                   "ON write_batches WHEN NEW.terminal_snapshot_json IS NOT NULL "
                   "BEGIN INSERT INTO commit_fault_child VALUES(1); END")
    traces = []
    def traced_connect(*args, **kwargs):
        db = connect(*args, **kwargs)
        db.execute("PRAGMA foreign_keys=ON")
        db.set_trace_callback(traces.append)
        return db
    terminal = _snapshot(2, phase=phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    terminal["compileRequestId"] = "request-a"
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", traced_connect)
        with pytest.raises(sqlite3.IntegrityError, match="FOREIGN KEY constraint failed"):
            store.update_editor_execution_state(terminal)
    statements = [statement.strip().upper() for statement in traces]
    update_index = next(index for index, statement in enumerate(statements)
                        if statement.startswith("UPDATE WRITE_BATCHES") and "TERMINAL_SNAPSHOT_JSON" in statement)
    commit_index = statements.index("COMMIT", update_index)
    assert statements.index("ROLLBACK", commit_index) > commit_index
    assert store.get_write_batch(identity) == original
    assert store.pending_write_batch_id == identity
    assert not store.execution_state()["correlationVerified"]
    assert not store.execution_state()["ready"]
    with connect(store._db_path) as db:
        assert db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone() == original_row
        assert db.execute("SELECT COUNT(*) FROM commit_fault_child").fetchone()[0] == 0
        db.execute("DROP TRIGGER inject_commit_failure")
    # Observe fresh evidence for the original identity; never register/start another compile.
    terminal["sequence"] = 3
    terminal["snapshotId"] = "epoch-a:0:3"
    assert store.update_editor_execution_state(terminal)
    result = store.get_write_batch(identity)
    assert result["status"] == ("verified" if phase == "completed" else "failed")
    assert result["outcome"] == ("passed" if phase == "completed" else "failed")
    assert result["correlationVerified"] and result["terminalSnapshot"] == terminal
    assert result["writeBatchCreatedAt"] == 1000 and result["compileRequestId"] == "request-a"
    assert store.pending_write_batch_id == ""



def _exit_during_write_batch_commit(project, phase, boundary):
    """Run only in a test-owned child: exit without closing the real WAL connection."""
    import gc
    import json
    import os
    import upilot_mcp.state_store as state_module

    connect = sqlite3.connect

    def collect_connect(*args, **kwargs):
        # Make temporary-connection cleanup deterministic instead of depending
        # on interpreter/runner GC timing to leave committed WAL frames behind.
        gc.collect()
        return connect(*args, **kwargs)

    sqlite3.connect = collect_connect
    state_module._now_ms = lambda: 3000
    project = Path(project)
    store = StateStore()
    store.configure_project(str(project))
    # Keep the database open until os._exit: closing the last WAL connection
    # may checkpoint prior commits before we reach the injected COMMIT boundary.
    wal_guard = sqlite3.connect(store._db_path)
    assert wal_guard.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    queued["compileRequestId"] = "request-a"
    assert store.update_editor_execution_state(queued)
    terminal = _snapshot(2, phase=phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    terminal["compileRequestId"] = "request-a"
    with sqlite3.connect(store._db_path) as db:
        assert db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
        db.row_factory = sqlite3.Row
        original = dict(db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone())
        snapshot = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
    (project / "before-exit.json").write_text(json.dumps({
        "identity": identity, "originalRow": original, "queuedSnapshot": snapshot,
        "terminal": terminal, "lifecycleId": store.lifecycle_id,
    }), encoding="utf-8")

    def exit_at_boundary(db, stage, code):
        print(json.dumps({"stage": stage, "inTransaction": db.in_transaction,
                          "pending": store.pending_write_batch_id,
                          "correlationVerified": store.correlation_verified}), flush=True)
        os._exit(code)

    class ExitConnection(sqlite3.Connection):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self.evidence_transaction = False
            self.set_trace_callback(self.trace)

        def trace(self, statement):
            sql = statement.strip().upper()
            if sql.startswith("UPDATE WRITE_BATCHES SET STATUS=") and "TERMINAL_SNAPSHOT_JSON=" in sql:
                self.evidence_transaction = True
            if sql == "COMMIT" and self.evidence_transaction and boundary == "before_commit":
                exit_at_boundary(self, boundary, 71)

        def __exit__(self, exc_type, exc_value, traceback):
            result = super().__exit__(exc_type, exc_value, traceback)
            if exc_type is None and self.evidence_transaction and boundary == "after_commit":
                exit_at_boundary(self, boundary, 72)
            return result

    def exit_connect(*args, **kwargs):
        return collect_connect(*args, factory=ExitConnection, **kwargs)

    sqlite3.connect = exit_connect
    store.update_editor_execution_state(terminal)
    raise AssertionError("The child did not reach its requested COMMIT exit boundary.")


@pytest.mark.parametrize("phase", ["completed", "failed"])
@pytest.mark.parametrize("boundary", ["before_commit", "after_commit"])
def test_process_exit_at_write_batch_commit_preserves_history_without_replay(tmp_path, phase, boundary):
    import json
    import subprocess
    import sys

    root = Path(__file__).resolve().parents[1]
    child = subprocess.run([
        sys.executable, "-c",
        "import sys; sys.path[:0] = sys.argv[1:3]; "
        "from test_editor_execution_state_v2 import _exit_during_write_batch_commit; "
        "_exit_during_write_batch_commit(*sys.argv[3:])",
        str(root / "src"), str(root / "tests"), str(tmp_path), phase, boundary,
    ], capture_output=True, text=True, timeout=30,
       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert child.returncode == (71 if boundary == "before_commit" else 72), (child.stdout, child.stderr)
    before = json.loads((tmp_path / "before-exit.json").read_text(encoding="utf-8"))
    identity = before["identity"]
    marker = json.loads(child.stdout.strip())
    assert marker == {"stage": boundary, "inTransaction": boundary == "before_commit",
                      "pending": identity, "correlationVerified": False}
    db_path = tmp_path / "Library/UPilot/ServerState/state-v2.sqlite3"
    assert Path(str(db_path) + "-wal").stat().st_size > 0
    # Inspect recovered WAL bytes before StateStore can restore/project history.
    with sqlite3.connect(db_path) as db:
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        assert db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
        db.row_factory = sqlite3.Row
        row = dict(db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone())
        assert db.execute("SELECT COUNT(*) FROM write_batches").fetchone()[0] == 1
        assert db.execute("SELECT snapshot_json FROM project_state").fetchone()[0] == before["queuedSnapshot"]
    expected_row = dict(before["originalRow"])
    if boundary == "after_commit":
        expected_row.update(status="verified" if phase == "completed" else "failed", updated_at=3000,
                            terminal_snapshot_json=json.dumps(before["terminal"], ensure_ascii=False), error="")
    assert row == expected_row

    restored = StateStore()
    restored.configure_project(str(tmp_path))
    assert restored.lifecycle_id != before["lifecycleId"]
    assert restored.pending_write_batches() == [] and restored.pending_write_batch_id == ""
    assert restored._current_write_batches == set()
    assert not restored.execution_state()["ready"]
    assert not restored.execution_state()["authoritative"]
    service = _ResourceService(tmp_path, restored)
    original = restored.get_write_batch(identity)
    for _ in range(2):
        response = asyncio.run(service.write_batch_status(identity))
        assert response.ok and response.data["historical"] and response.data["terminal"]
        assert response.data["writeBatchId"] == identity
        assert response.data["compileRequestId"] == "request-a"
        assert response.data["writeBatchCreatedAt"] == 1000
        if boundary == "before_commit":
            assert response.data["status"] == "aborted"
            assert response.data["outcome"] == "unknown"
            assert not response.data["errorsVerified"] and not response.data["correlationVerified"]
        else:
            assert response.data["status"] == ("verified" if phase == "completed" else "failed")
            assert response.data["outcome"] == ("passed" if phase == "completed" else "failed")
            assert response.data["errorsVerified"] and response.data["correlationVerified"]
            assert response.data["terminalSnapshot"] == before["terminal"]
    assert service.resume_calls == 0
    assert restored.get_write_batch(identity) == original
    assert restored.pending_write_batches() == []
    with sqlite3.connect(db_path) as db:
        db.row_factory = sqlite3.Row
        assert dict(db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()) == row

    # A later explicitly registered batch is independent; old terminal evidence
    # cannot turn historical unverified work into an active/verified batch.
    successor = restored.register_write_batch(["B.cs"], created_at=2000, files_sha256="b", compile_when_edit_mode=True)
    assert successor["writeBatchId"] != identity
    assert restored.update_editor_execution_state(before["terminal"])
    assert restored.pending_write_batch_id == successor["writeBatchId"]
    assert [item["writeBatchId"] for item in restored.pending_write_batches()] == [successor["writeBatchId"]]
    assert not restored.execution_state()["ready"]
    assert not restored.execution_state()["correlationVerified"]
    assert restored.get_write_batch(identity) == original
    assert service.resume_calls == 0
    with sqlite3.connect(db_path) as db:
        db.row_factory = sqlite3.Row
        assert dict(db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()) == row


@pytest.mark.skipif(__import__("sys").platform != "win32", reason="Requires actual Windows file sharing denial")
@pytest.mark.parametrize("phase", ["completed", "failed"])
@pytest.mark.parametrize("observation", ["fresh_snapshot", "new_lifecycle"])
def test_windows_file_sharing_denial_never_confirms_write_batch(tmp_path, phase, observation):
    """Exercise the real OS/SQLite failure, without replacing sqlite.connect or touching live state."""
    import ctypes
    from ctypes import wintypes
    import gc
    import json

    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    assert store.update_editor_execution_state(queued)
    original = store.get_write_batch(identity)
    db_path = Path(store._db_path).resolve()
    assert db_path.is_relative_to(tmp_path.resolve())
    with sqlite3.connect(db_path) as db:
        assert db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
        before_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        before_snapshot = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
    db.close()
    # SQLite connection context managers commit/rollback but do not close. Drop
    # test-owned unreachable connections before opening our exclusive write guard.
    gc.collect()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    invalid = ctypes.c_void_p(-1).value
    # GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING. Readers remain allowed;
    # SQLite's read/write open is denied by Windows, not a monkeypatched error.
    guard = kernel.CreateFileW(str(db_path), 0x80000000, 1, None, 3, 0, None)
    assert guard != invalid, ctypes.WinError(ctypes.get_last_error())
    terminal = _snapshot(2, phase=phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    try:
        probe = kernel.CreateFileW(str(db_path), 0xC0000000, 7, None, 3, 0, None)
        native_error = ctypes.get_last_error()
        if probe != invalid:
            kernel.CloseHandle(probe)
        assert probe == invalid and native_error == 32  # ERROR_SHARING_VIOLATION
        with pytest.raises(sqlite3.Error) as failure:
            store.update_editor_execution_state(terminal)
        sqlite_code = failure.value.sqlite_errorcode
        sqlite_name = failure.value.sqlite_errorname
        assert sqlite_code == sqlite3.SQLITE_READONLY
        failure_frames = []
        frame = failure.value.__traceback__
        while frame is not None:
            failure_frames.append(frame.tb_frame.f_code.co_name)
            frame = frame.tb_next
        assert "_persist_batch_evidence" in failure_frames
        failed_view = store.get_write_batch(identity)
        assert not failed_view["terminal"] and not failed_view["evidenceAvailable"]
        assert not failed_view["correlationVerified"] and failed_view["terminalSnapshot"] is None
        assert store.pending_write_batch_id == identity
        assert not store.execution_state()["ready"] and not store.execution_state()["correlationVerified"]
    finally:
        assert kernel.CloseHandle(guard), ctypes.WinError(ctypes.get_last_error())
        gc.collect()
    with sqlite3.connect(db_path) as db:
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        after_failure_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        assert after_failure_row == before_row
        assert db.execute("SELECT snapshot_json FROM project_state").fetchone()[0] == before_snapshot
        assert db.execute("SELECT COUNT(*) FROM write_batches").fetchone()[0] == 1
    db.close()
    assert store.get_write_batch(identity) == original
    if observation == "fresh_snapshot":
        # Only a new authoritative observation of the original operation; no
        # registration, compile dispatch, or extension of its original deadline.
        terminal.update(sequence=3, snapshotId="epoch-a:0:3")
        assert store.update_editor_execution_state(terminal)
        final = store.get_write_batch(identity)
        assert final["status"] == ("verified" if phase == "completed" else "failed")
        assert final["outcome"] == ("passed" if phase == "completed" else "failed")
        assert final["terminalSnapshot"] == terminal and final["correlationVerified"]
        assert final["writeBatchCreatedAt"] == 1000 and store.pending_write_batch_id == ""
        service = _ResourceService(tmp_path, store)
        response = asyncio.run(service.write_batch_status(identity))
        assert response.ok and response.data["terminal"] and response.data["outcome"] == final["outcome"]
    else:
        restored = StateStore()
        restored.configure_project(str(tmp_path))
        assert restored.lifecycle_id != store.lifecycle_id
        assert restored.pending_write_batches() == [] and restored.pending_write_batch_id == ""
        assert not restored.execution_state()["ready"]
        service = _ResourceService(tmp_path, restored)
        for _ in range(2):
            response = asyncio.run(service.write_batch_status(identity))
            assert response.ok and response.data["historical"] and response.data["terminal"]
            assert response.data["status"] == "aborted" and response.data["outcome"] == "unknown"
            assert not response.data["errorsVerified"] and not response.data["correlationVerified"]
            assert response.data["writeBatchCreatedAt"] == 1000
        final = response.data
        assert restored._current_write_batches == set()
    assert service.resume_calls == 0
    (tmp_path / "sharing-fault-receipt.json").write_text(json.dumps({
        "phase": phase, "observation": observation, "dbPath": str(db_path),
        "win32Error": native_error, "sqliteErrorCode": sqlite_code, "sqliteErrorName": sqlite_name,
        "failureFrames": failure_frames, "beforeRow": before_row, "afterFailureRow": after_failure_row,
        "queuedSnapshot": json.loads(before_snapshot),
        "writeBatchId": identity, "writeBatchCreatedAt": 1000,
        "guardClosed": True, "originalRowsUnchangedAfterFailure": True,
        "pendingAtFailure": identity, "terminalAtFailure": failed_view["terminal"],
        "finalStatus": final["status"], "finalOutcome": final["outcome"],
        "compileRequests": service.resume_calls,
    }, indent=2), encoding="utf-8")


def test_v2_same_identity_mode_conflict_cannot_refresh_liveness(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    paused = _snapshot(1)
    paused.update(sessionId="session-a", playModeState="pause")
    assert state.update_editor_execution_state(paused)
    original_received_at = state.received_at
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 4500)
    accepted = state.update_editor_context({
        "snapshotId": paused["snapshotId"], "sessionId": "session-a",
        "connected": True, "authoritative": True, "updatedAt": 4000,
        "playModeState": "play", "isPlaying": True, "isPaused": False,
    })
    assert not accepted
    assert state.received_at == original_received_at
    execution = state.execution_state()
    assert execution["authoritative"] is False and execution["isStale"] is True
    assert execution["snapshotId"] == paused["snapshotId"]
    assert execution["playModeState"] == "pause"  # Do not invent a new ordered observation.


def test_d20_real_dispatcher_resource_query_cannot_publish_fresh_conflicting_modes(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    paused = _snapshot(1)
    paused.update(sessionId="session-a", playModeState="pause")
    assert state.update_editor_execution_state(paused)
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 4500)
    raw = {
        "authoritative": True, "isStale": False, "updatedAt": 4000,
        "sessionId": "session-a", "isPlaying": True, "isPaused": False,
        "isCompiling": False, "activeSceneName": "TestScene",
    }
    context = {
        "snapshotId": paused["snapshotId"], "sessionId": "session-a",
        "connected": True, "authoritative": True, "updatedAt": 4000,
        "playModeState": "play", "isPlaying": True, "isPaused": False,
    }
    class Transport:
        def __init__(self):
            self.calls = []
        def is_ready(self):
            return True
        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future
        async def send_command(self, *, command_id, name, payload):
            self.calls.append(name)
            self.future.set_result({"type": "result", "payload": raw, "context": context})

    transport = Transport()
    service = StatusDomainService()
    service.dispatcher = CommandDispatcher(transport, state)
    service.server = SimpleNamespace(
        state=state, is_ready=transport.is_ready,
        session_manager=SimpleNamespace(active=SimpleNamespace(session_id="session-a", process_id=7)),
    )
    response = asyncio.run(service.editor_state())
    assert response.ok
    execution = state.execution_state()
    assert transport.calls == ["resource.editorState"]
    assert response.data["playModeState"] == execution["playModeState"]
    assert not execution["authoritative"] and execution["isStale"]
    assert not response.data["authoritative"] and response.data["isStale"]


def test_d20_coherent_context_does_not_refresh_snapshot_age(tmp_path: Path, monkeypatch) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    snapshot = _snapshot(1)
    snapshot.update(sessionId="session-a", playModeState="play")
    assert state.update_editor_execution_state(snapshot)
    before = (state.received_at, state.editor.updated_at)
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 12000)
    assert state.update_editor_context({
        "snapshotId": snapshot["snapshotId"], "sessionId": "session-a",
        "playModeState": "play", "authoritative": True, "updatedAt": 12000,
    })
    assert (state.received_at, state.editor.updated_at) == before
    assert state.execution_state()["isStale"] is True


@pytest.mark.parametrize("snapshot_id,session_id", [
    ("epoch-a:0:0", "session-a"), ("epoch-a:0:1", "old-session"),
    ("epoch-a:0:1", "session-a"),
])
def test_d20_old_context_cannot_downgrade_new_domain(tmp_path: Path, snapshot_id, session_id) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    current = _snapshot(2, domain=1)
    current.update(sessionId="session-a", playModeState="play")
    assert state.update_editor_execution_state(current)
    before = state.execution_state()
    assert not state.update_editor_context({
        "snapshotId": snapshot_id, "sessionId": session_id,
        "playModeState": "pause", "authoritative": False, "isStale": True,
        "updatedAt": 9999,
    })
    assert state.execution_state() == before


def test_d20_only_new_ordered_snapshot_restores_conflict_authority(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    paused = _snapshot(1)
    paused.update(sessionId="session-a", playModeState="pause")
    assert state.update_editor_execution_state(paused)
    assert not state.update_editor_context({
        "snapshotId": paused["snapshotId"], "sessionId": "session-a", "playModeState": "play",
    })
    assert state.update_editor_context({
        "snapshotId": paused["snapshotId"], "sessionId": "session-a",
        "playModeState": "pause", "authoritative": True,
    })
    assert not state.execution_state()["authoritative"]
    assert not state.update_editor_execution_state(paused)
    resumed = _snapshot(2, observed_at=2000, transition="heartbeat")
    resumed.update(sessionId="session-a", playModeState="play")
    assert state.update_editor_execution_state(resumed)
    restored = state.execution_state()
    assert restored["authoritative"] and not restored["isStale"]
    assert restored["playModeState"] == "play" and restored["sequence"] == 2


def _d20_real_dispatcher_service(state, reply):
    class Transport:
        def __init__(self):
            self.calls = []
        def is_ready(self):
            return True
        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future
        async def send_command(self, *, command_id, name, payload):
            self.calls.append((name, payload))
            self.future.set_result(reply(name, payload))
    transport = Transport()
    service = StatusDomainService()
    service.dispatcher = CommandDispatcher(transport, state)
    service.server = SimpleNamespace(
        state=state, is_ready=transport.is_ready,
        session_manager=SimpleNamespace(
            active=SimpleNamespace(session_id="session-a", process_id=7), is_connected=lambda: True,
        ),
    )
    return service, transport


def test_d20_new_snapshot_wins_over_late_paused_resource_reply(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    paused = _snapshot(1)
    paused.update(sessionId="session-a", playModeState="pause")
    assert state.update_editor_execution_state(paused)
    def reply(name, payload):
        resumed = _snapshot(2, observed_at=2000, transition="pause_state_changed")
        resumed.update(sessionId="session-a", playModeState="play")
        assert state.update_editor_execution_state(resumed)
        return {"type": "result", "context": paused, "payload": {
            "isPlaying": True, "isPaused": True, "isCompiling": False,
            "authoritative": True, "isStale": False, "updatedAt": 1000,
        }}
    service, transport = _d20_real_dispatcher_service(state, reply)
    response = asyncio.run(service.editor_state())
    assert response.ok and len(transport.calls) == 1
    execution = state.execution_state()
    assert response.data["playModeState"] == execution["playModeState"] == "play"
    assert response.data["authoritative"] and not response.data["isStale"]
    assert execution["sequence"] == 2


def test_d20_legacy_resource_projection_keeps_accepted_newer_state(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    service, _ = _d20_real_dispatcher_service(state, lambda *_: None)
    raw = {"connected": True, "sessionId": "session-a", "authoritative": True,
           "isPlaying": True, "isPaused": False, "updatedAt": 2500}
    assert service._update_editor_cache_from_resource_state(raw)["playModeState"] == "play"
    assert not state.producer_epoch
    older = dict(raw, isPaused=True, updatedAt=2000)
    assert service._update_editor_cache_from_resource_state(older)["playModeState"] == "play"
    assert state.editor.updated_at == 2500


def test_d20_compile_refresh_observes_ordered_phase_not_old_raw(tmp_path: Path) -> None:
    from upilot_mcp.domain.compile_service import CompileDomainService
    state = StateStore()
    state.configure_project(str(tmp_path))
    initial = _snapshot(1)
    initial.update(sessionId="session-a", playModeState="play")
    assert state.update_editor_execution_state(initial)
    def reply(name, payload):
        queued = _snapshot(2, phase="queued", observed_at=2000, terminal=False, errors_verified=False)
        queued.update(sessionId="session-a", playModeState="edit")
        assert state.update_editor_execution_state(queued)
        return {"type": "result", "context": initial, "payload": {
            "authoritative": True, "isStale": False, "isCompiling": False,
            "isPlaying": True, "isPaused": False, "compilePhase": "completed",
        }}
    status, transport = _d20_real_dispatcher_service(state, reply)
    service = CompileDomainService()
    service.server, service.dispatcher = status.server, status.dispatcher
    result, execution = asyncio.run(service._refresh_execution_state())
    assert result.ok and execution["compilePhase"] == "queued"
    assert execution["isCompiling"] and not execution["terminal"]
    assert execution["playModeState"] == "edit"
    assert transport.calls == [("resource.editorState", {})]


def test_d20_repeated_resume_does_not_dispatch_or_write(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    playing = _snapshot(1)
    playing.update(sessionId="session-a", playModeState="play")
    assert state.update_editor_execution_state(playing)
    def reply(name, payload):
        raise AssertionError("A confirmed no-op must not dispatch a command or a resource query.")
    service, transport = _d20_real_dispatcher_service(state, reply)
    result = asyncio.run(service._set_pause_state(False, wait=True, timeout_ms=1000))
    assert result.ok and result.data["confirmed"]
    assert not result.data["changed"] and result.data["writeCount"] == 0
    assert result.data["commandSubmitted"] is False
    assert transport.calls == []


@pytest.mark.parametrize("paused", [True, False])
def test_d20_pause_resume_dispatches_once_and_confirms_new_snapshot(tmp_path: Path, paused: bool) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    initial = _snapshot(1)
    initial.update(sessionId="session-a", playModeState="play" if paused else "pause")
    assert state.update_editor_execution_state(initial)
    observed = _snapshot(2, observed_at=2000, transition="pause_state_changed")
    observed.update(sessionId="session-a", playModeState="pause" if paused else "play",
                    isPlaying=True, isPaused=paused)
    def reply(name, payload):
        if name == "playmode.set":
            assert set(payload) == {"action", "requestId", "operationId", "toolName"}
            assert payload["action"] == ("pause" if paused else "resume")
            assert payload["toolName"] == ("unity_playmode_pause" if paused else "unity_playmode_resume")
            assert payload["requestId"].startswith("req-") and payload["operationId"] == ""
            assert state.update_editor_execution_state(observed)
            body = {"changed": True, "writeCount": 1, "observedState": {"isPaused": paused}}
        else:
            assert name == "resource.editorState" and payload == {}
            body = {"authoritative": True, "isStale": False, "isPlaying": True, "isPaused": paused}
        return {"type": "result", "context": observed, "payload": body}
    service, transport = _d20_real_dispatcher_service(state, reply)
    result = asyncio.run(service._set_pause_state(paused, wait=True, timeout_ms=1000))
    assert result.ok and result.data["confirmed"] and result.data["terminal"]
    assert result.data["changed"] and result.data["writeCount"] == 1
    assert result.data["commandSubmitted"] is True
    assert result.data["editorState"]["playModeState"] == observed["playModeState"]
    assert result.data["editorState"]["isPlaying"] is result.context["isPlaying"] is True
    assert result.data["editorState"]["isPaused"] is result.context["isPaused"] is paused
    assert result.context["snapshotId"] == state.execution_state()["snapshotId"] == observed["snapshotId"]
    assert [name for name, _ in transport.calls] == ["playmode.set", "resource.editorState"]
    repeated = asyncio.run(service._set_pause_state(paused, wait=True, timeout_ms=1000))
    assert repeated.ok and repeated.data["confirmed"]
    assert not repeated.data["changed"] and repeated.data["writeCount"] == 0
    assert repeated.data["commandSubmitted"] is False
    assert repeated.data["editorState"]["isPlaying"] is True
    assert repeated.data["editorState"]["isPaused"] is paused
    assert len(transport.calls) == 2  # The repeated action cannot resubmit either command.


@pytest.mark.parametrize("phase", ["completed", "failed"])
@pytest.mark.parametrize("observation", ["fresh_snapshot", "new_lifecycle"])
def test_sqlite_capacity_full_never_confirms_write_batch(tmp_path, monkeypatch, phase, observation):
    """Real SQLITE_FULL at a private database page cap, not a filled system disk."""
    import json

    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    queued = _snapshot(1, phase="queued", write_batch_id=identity, write_batch_created_at=1000,
                       observed_at=1010, terminal=False, errors_verified=False, transition="compile_queued")
    assert store.update_editor_execution_state(queued)
    original = store.get_write_batch(identity)
    db_path = Path(store._db_path).resolve()
    assert db_path.is_relative_to(tmp_path.resolve())
    connect = sqlite3.connect
    with connect(db_path) as db:
        assert db.execute("PRAGMA journal_mode").fetchone()[0] == "wal"
        page_count = db.execute("PRAGMA page_count").fetchone()[0]
        page_size = db.execute("PRAGMA page_size").fetchone()[0]
        before_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        before_snapshot = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
    db.close()
    limits = []
    traces = []
    def capacity_limited_connect(*args, **kwargs):
        # max_page_count is connection-local. Configure the actual production
        # connection; do not replace its statements/commit or synthesize errors.
        assert Path(args[0]).resolve() == db_path
        db = connect(*args, **kwargs)
        try:
            actual_limit = db.execute(f"PRAGMA max_page_count={page_count}").fetchone()[0]
            assert actual_limit == page_count
            limits.append(actual_limit)
            db.set_trace_callback(lambda sql: traces.append(sql.split(None, 1)[0].upper()))
            return db
        except BaseException:
            db.close()
            raise
    terminal = _snapshot(2, phase=phase, write_batch_id=identity, write_batch_created_at=1000, observed_at=1100)
    # Explicit fixture-only payload forces overflow-page allocation even when
    # existing rows have free space. It is never sent to an online endpoint.
    terminal["capacityProbePadding"] = "x" * (4 * page_size)
    with monkeypatch.context() as patch:
        patch.setattr(sqlite3, "connect", capacity_limited_connect)
        with pytest.raises(sqlite3.OperationalError) as failure:
            store.update_editor_execution_state(terminal)
        sqlite_code = failure.value.sqlite_errorcode
        sqlite_name = failure.value.sqlite_errorname
        assert sqlite_code == sqlite3.SQLITE_FULL and sqlite_name == "SQLITE_FULL"
        failure_frames = []
        frame = failure.value.__traceback__
        while frame is not None:
            failure_frames.append(frame.tb_frame.f_code.co_name)
            frame = frame.tb_next
        assert "_persist_batch_evidence" in failure_frames
        failed_view = store.get_write_batch(identity)
        assert failed_view == original
        assert not failed_view["terminal"] and not failed_view["evidenceAvailable"]
        assert not failed_view["correlationVerified"] and failed_view["terminalSnapshot"] is None
        assert store.pending_write_batch_id == identity
        assert not store.execution_state()["ready"] and not store.execution_state()["correlationVerified"]
    assert limits and all(limit == page_count for limit in limits)
    assert "UPDATE" in traces
    # SQLITE_FULL may automatically roll back before Python exits the context;
    # the complete row and queued snapshot are the authoritative rollback proof.
    with connect(db_path) as db:
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        after_failure_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        after_failure_snapshot = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
        assert after_failure_row == before_row and after_failure_snapshot == before_snapshot
        assert db.execute("PRAGMA page_count").fetchone()[0] == page_count
        assert db.execute("SELECT COUNT(*) FROM write_batches").fetchone()[0] == 1
    db.close()
    if observation == "fresh_snapshot":
        # A new observation, not a new batch or compile; retain creation/deadline.
        terminal.update(sequence=3, snapshotId="epoch-a:0:3")
        assert store.update_editor_execution_state(terminal)
        final = store.get_write_batch(identity)
        assert final["status"] == ("verified" if phase == "completed" else "failed")
        assert final["outcome"] == ("passed" if phase == "completed" else "failed")
        assert final["terminalSnapshot"] == terminal and final["correlationVerified"]
        assert final["writeBatchCreatedAt"] == 1000 and store.pending_write_batch_id == ""
        service = _ResourceService(tmp_path, store)
        response = asyncio.run(service.write_batch_status(identity))
        assert response.ok and response.data["terminal"] and response.data["outcome"] == final["outcome"]
    else:
        restored = StateStore()
        restored.configure_project(str(tmp_path))
        assert restored.lifecycle_id != store.lifecycle_id
        assert restored.pending_write_batches() == [] and restored.pending_write_batch_id == ""
        assert not restored.execution_state()["ready"]
        service = _ResourceService(tmp_path, restored)
        for _ in range(2):
            response = asyncio.run(service.write_batch_status(identity))
            assert response.ok and response.data["historical"] and response.data["terminal"]
            assert response.data["status"] == "aborted" and response.data["outcome"] == "unknown"
            assert not response.data["errorsVerified"] and not response.data["correlationVerified"]
            assert response.data["writeBatchCreatedAt"] == 1000
        final = response.data
        assert restored._current_write_batches == set()
    assert service.resume_calls == 0
    with connect(db_path) as db:
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        final_row = db.execute("SELECT * FROM write_batches WHERE write_batch_id=?", (identity,)).fetchone()
        final_snapshot = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
        final_page_count = db.execute("PRAGMA page_count").fetchone()[0]
    db.close()
    (tmp_path / "capacity-fault-receipt.json").write_text(json.dumps({
        "phase": phase, "observation": observation, "dbPath": str(db_path),
        "sqliteErrorCode": sqlite_code, "sqliteErrorName": sqlite_name, "failureFrames": failure_frames,
        "pageCountBefore": page_count, "pageSize": page_size, "connectionPageLimits": limits,
        "fixturePaddingBytes": len(terminal["capacityProbePadding"]), "statementKinds": traces,
        "beforeRow": before_row, "afterFailureRow": after_failure_row, "finalRow": final_row,
        "queuedSnapshot": json.loads(before_snapshot), "afterFailureSnapshot": json.loads(after_failure_snapshot),
        "finalSnapshot": json.loads(final_snapshot), "finalPageCount": final_page_count,
        "writeBatchId": identity, "writeBatchCreatedAt": 1000,
        "pendingAtFailure": identity, "terminalAtFailure": failed_view["terminal"],
        "finalStatus": final["status"], "finalOutcome": final["outcome"],
        "finalPublicView": response.data, "compileRequests": service.resume_calls, "liveDatabaseTouched": False,
        "physicalDiskFullCovered": False,
    }, indent=2), encoding="utf-8")


@pytest.mark.parametrize("stale", [False, True], ids=["fresh", "stale"])
@pytest.mark.parametrize("play_state, expected_playing, expected_paused", [
    ("edit", False, False),
    ("play", True, False),
    ("pause", True, True),
    ("unknown", False, False),
])
def test_d20_execution_projection_pause_is_still_playmode(
    tmp_path: Path, monkeypatch, stale: bool, play_state: str, expected_playing: bool, expected_paused: bool,
) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    observed = _snapshot(1)
    observed.update(sessionId="session-a", playModeState=play_state,
                    isPlaying=expected_playing, isPaused=expected_paused)
    original = dict(observed)
    assert state.update_editor_execution_state(observed)
    with sqlite3.connect(state._db_path) as db:
        persisted = db.execute("SELECT snapshot_json FROM project_state").fetchone()[0]
    # Freshness is based on receipt time; advance the controlled clock, not observedAt.
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 3300 if stale else 3000)
    stale_after_ms = 250 if stale else 5000
    for projection in (state.execution_state(stale_after_ms=stale_after_ms),
                       state.response_context(stale_after_ms=stale_after_ms)):
        assert projection["playModeState"] == play_state
        assert projection["isPlaying"] is expected_playing
        assert projection["isPaused"] is expected_paused
        assert projection["ageMs"] == (300 if stale else 0)
        assert projection["isStale"] is stale
        assert projection["authoritative"] is (not stale)
        assert projection["ready"] is (not stale and play_state == "edit")
        expected_blocker = "EditorContextStale" if stale else (
            "PlayMode" if play_state in ("play", "pause") else (
                "EditorModeUnknown" if play_state == "unknown" else ""
            )
        )
        assert projection["blockedReason"] == expected_blocker
        assert projection["snapshotId"] == observed["snapshotId"]
        assert projection["acceptedSnapshotCount"] == 1
    assert observed == original
    with sqlite3.connect(state._db_path) as db:
        assert db.execute("SELECT snapshot_json FROM project_state").fetchone()[0] == persisted
