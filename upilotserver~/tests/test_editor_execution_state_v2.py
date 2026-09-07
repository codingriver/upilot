from __future__ import annotations

import asyncio
import inspect
import sqlite3
from pathlib import Path

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.models import WsMessage
from upilot_mcp.responses import ok
from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp.state_store import StateStore


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


def test_sqlite_restart_restores_stale_snapshot_and_inflight_batch(tmp_path: Path) -> None:
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
    assert restored.pending_write_batches()[0]["status"] == "deferred"

    heartbeat = _snapshot(2, observed_at=1100, transition="heartbeat")
    heartbeat["playModeState"] = "play"
    assert restored.update_editor_execution_state(heartbeat)
    assert restored.execution_state()["compileDeferredReason"] == "PlayMode"


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


def test_historical_verified_heartbeat_cannot_hide_new_deferred_batch(tmp_path: Path) -> None:
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
    assert state.update_editor_execution_state(historical_heartbeat)

    execution = state.execution_state()
    assert execution["correlationVerified"] is False
    assert execution["pendingWriteBatchId"] == deferred_id
    assert execution["compileDeferredReason"] == "PlayMode"
    assert state.get_write_batch(deferred_id)["status"] == "deferred"


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


def test_state_events_keep_transitions_and_exclude_heartbeats(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    assert state.update_editor_execution_state(_snapshot(1, transition="compile_queued", terminal=False))
    assert state.update_editor_execution_state(_snapshot(2, transition="heartbeat"))
    db_path = tmp_path / "Library" / "UPilot" / "ServerState" / "state-v2.sqlite3"
    with sqlite3.connect(db_path) as db:
        transitions = [row[0] for row in db.execute("SELECT transition FROM state_events")]
    assert transitions == ["compile_queued"]


def test_v2_heartbeat_notifies_pending_batch_coordinator_after_server_restart(tmp_path: Path) -> None:
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


def test_auto_resumed_compile_failure_does_not_verify_write_batch(tmp_path: Path, monkeypatch) -> None:
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
    assert stored["status"] == "failed"


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
