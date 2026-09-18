from __future__ import annotations

import asyncio
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.responses import ok
from upilot_mcp.state_store import StateStore


def _compile_service(store: StateStore, dispatch) -> CompileDomainService:
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)
    service.dispatcher = SimpleNamespace(call=dispatch)
    return service


def _batch_service(store: StateStore) -> ResourceDomainService:
    service = ResourceDomainService()
    service.server = SimpleNamespace(state=store)
    return service


def _authoritative_edit(store: StateStore) -> None:
    store.editor.connected = True
    store.editor.authoritative = True
    store.editor.play_mode_state = "edit"
    store.editor.updated_at = 10**15


def test_wp05_t01_stale_raw_playmode_never_requests_exit(tmp_path, monkeypatch) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1_000, files_sha256="a", compile_when_edit_mode=True)
    store.editor.connected = True
    store.editor.authoritative = False
    store.editor.play_mode_state = "play"
    store.compile.last_progress_at = 39_999
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 40_000)

    result = asyncio.run(_batch_service(store).write_batch_status(batch["writeBatchId"]))

    assert result.ok
    assert result.data["waitingReason"] == "stale"
    assert result.data["nextAction"].startswith("Wait for the same authorized batch")


@pytest.mark.parametrize("name,batch_status,overrides,expected", [
    ("recovery", "recovery_required", {"unityConnected": False, "compilePhase": "verifying", "authoritative": False, "playModeState": "play", "isStale": True, "isCompiling": True}, "recovery_required"),
    ("disconnected", "pending", {"unityConnected": False, "compilePhase": "verifying", "authoritative": False, "playModeState": "play", "isStale": True, "isCompiling": True}, "disconnected"),
    ("reload", "pending", {"compilePhase": "verifying", "authoritative": False, "playModeState": "play", "isStale": True, "isCompiling": True}, "reload"),
    ("playmode", "pending", {"playModeState": "play", "isStale": True, "compilePhase": "compiling", "isCompiling": True}, "playmode"),
    ("stale", "pending", {"authoritative": False, "playModeState": "play", "compilePhase": "compiling", "isCompiling": True}, "stale"),
    ("compiling", "pending", {"compilePhase": "compiler_finished", "isCompiling": True}, "compile_in_progress"),
    ("idle", "pending", {}, "none"),
])
def test_wp05_waiting_reason_priority_is_identical_for_compile_and_write_batch(
    tmp_path, name, batch_status, overrides, expected,
) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path / name))
    batch = store.register_write_batch(["A.cs"], created_at=1_000, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], batch_status)
    store.compile.write_batch_id = batch["writeBatchId"]
    store.compile.write_batch_created_at = batch["writeBatchCreatedAt"]
    execution = {
        "terminal": False,
        "unityConnected": True,
        "authoritative": True,
        "isStale": False,
        "playModeState": "edit",
        "compilePhase": "completed",
        "isCompiling": False,
        "lastProgressAt": 1_000,
        **overrides,
    }
    compile_service = CompileDomainService()
    compile_service.server = SimpleNamespace(state=store)
    resource_service = _batch_service(store)

    assert compile_service._compile_waiting_diagnostics(execution)["waitingReason"] == expected
    assert resource_service._write_batch_waiting_diagnostics(
        store.get_write_batch(batch["writeBatchId"]), execution,
    )["waitingReason"] == expected


def test_wp05_t02_compile_wait_attention_clears_after_progress(tmp_path, monkeypatch) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    _authoritative_edit(store)
    batch = store.register_write_batch(["A.cs"], created_at=1_000, files_sha256="a", compile_when_edit_mode=True)
    store.compile.write_batch_id = batch["writeBatchId"]
    store.compile.write_batch_created_at = batch["writeBatchCreatedAt"]
    store.compile.phase = "compiling"
    store.compile.status = "compiling"
    store.editor.is_compiling = True
    monkeypatch.setattr("upilot_mcp.domain.compile_service.now_ms", lambda: 32_000)
    store.compile.last_progress_at = 1_000

    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)
    stalled = asyncio.run(service.compile_status())
    assert stalled.ok
    assert stalled.data["waitingReason"] == "compile_in_progress"
    assert stalled.data["attentionRequired"] is True

    store.compile.last_progress_at = 31_999
    recovered = asyncio.run(service.compile_status())
    assert recovered.ok and recovered.data["attentionRequired"] is False
    assert store.get_write_batch(batch["writeBatchId"])["status"] == "pending"


@pytest.mark.parametrize("field,value", [
    ("warningCount", True),
    ("warningsTruncated", "false"),
    ("warnings", ["not-an-object"]),
])
def test_wp05_t03_warning_payload_validation_does_not_update_state(tmp_path, field, value) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.compile.warning_count = 7

    async def dispatch(request_id, _command, _payload, **_kwargs):
        return ok(request_id, {"requestId": "compile-a", "total": 0, "errors": [], field: value})

    result = asyncio.run(_compile_service(store, dispatch).compile_errors("compile-a", include_warnings=True))

    assert not result.ok and result.error.code == "INVALID_COMPILE_DIAGNOSTICS"
    assert store.compile.warning_count == 7


def test_wp05_t04_automatic_snapshot_with_matching_batch_never_becomes_terminal_evidence(tmp_path) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="auto-a")
    snapshot = {
        "stateContractVersion": 2,
        "projectId": "project-a",
        "producerEpoch": "epoch-a",
        "domainGeneration": 0,
        "sequence": 1,
        "snapshotId": "epoch-a:0:1",
        "observedAt": 200,
        "connected": True,
        "authoritative": True,
        "playModeState": "edit",
        "isCompiling": False,
        "compileStatus": "completed",
        "compilePhase": "completed",
        "compileRequestId": "auto-request",
        "compileOperationId": "auto-a",
        "writeBatchId": batch["writeBatchId"],
        "writeBatchCreatedAt": batch["writeBatchCreatedAt"],
        "compileOrigin": "unity_auto",
        "terminal": True,
        "errorsVerified": True,
        "verificationPending": False,
        "lastCompileVerifiedAt": 200,
        "lastTerminalCompileAt": 200,
    }

    assert store.update_editor_execution_state(snapshot)
    stored = store.get_write_batch(batch["writeBatchId"])
    assert not store.correlation_verified
    assert stored["terminalSnapshot"] is None
    assert stored["status"] == "compiling"


@pytest.mark.parametrize("origin", ["unity_auto", ""])
def test_wp05_t04_warning_details_require_explicit_request_scoped_origin(tmp_path, origin) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="operation-a")
    store.producer_epoch = "epoch-a"
    store.editor.authoritative = True
    store.compile.compile_request_id = "request-a"
    store.compile.compile_operation_id = "operation-a"
    store.compile.write_batch_id = batch["writeBatchId"]
    store.compile.write_batch_created_at = batch["writeBatchCreatedAt"]
    store.compile.compile_origin = origin

    async def dispatch(request_id, _command, _payload, **_kwargs):
        return ok(request_id, {
            "compileRequestId": "request-a", "compileOperationId": "operation-a",
            "writeBatchId": batch["writeBatchId"], "warningCount": 1,
            "warningDetailsAvailable": True, "warnings": [{"message": "late"}],
            "total": 0, "errors": [],
        })

    result = asyncio.run(_compile_service(store, dispatch).compile_errors("request-a", include_warnings=True))

    assert result.ok and result.data["warningDetailsPersisted"] is False
    stored = store.get_write_batch(batch["writeBatchId"])
    assert stored["warningDetailsAvailable"] is False
    assert "warnings" not in stored


def test_wp05_t05_active_automatic_compile_defers_authorized_batch_without_replay(tmp_path, monkeypatch) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    _authoritative_edit(store)
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.compile.compile_origin = "unity_auto"
    store.compile.phase = "compiling"
    store.compile.status = "compiling"
    store.editor.is_compiling = True
    service = _batch_service(store)
    calls: list[dict] = []

    async def no_sleep(_delay: float) -> None:
        return None

    async def safe_compile(**kwargs):
        calls.append(kwargs)
        raise AssertionError("an unattributed auto compile must not be reused or replayed")

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    service.safe_compile_and_wait = safe_compile
    asyncio.run(service._resume_pending_write_batches())

    assert calls == []
    assert store.get_write_batch(batch["writeBatchId"])["status"] == "pending"


def test_wp05_auto_compile_after_delete_or_asmdef_change_remains_unattributed(tmp_path) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    # The automatic compile has already started; this later registration includes
    # both a deletion and an assembly-definition input, so timing cannot prove
    # that the automatic compiler covered the complete change manifest.
    store.compile.compile_origin = "unity_auto"
    store.compile.compile_operation_id = "auto-operation"
    batch = store.register_write_batch(
        [],
        created_at=100,
        files_sha256="ignored-for-changes-v1",
        compile_when_edit_mode=True,
        changes=[
            {"path": "Assets/Removed.cs", "kind": "delete", "contentSha256": ""},
            {"path": "Assets/Feature.asmdef", "kind": "write", "contentSha256": "a" * 64},
        ],
    )
    store.compile.write_batch_id = batch["writeBatchId"]
    store.compile.write_batch_created_at = batch["writeBatchCreatedAt"]
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)

    diagnostics = service._automatic_compile_reuse_diagnostics()

    assert diagnostics["reuseDecision"] == "unattributed_auto_compile"
    assert diagnostics["inputCoverageVerified"] is False
    assert diagnostics["replayStartAttempted"] is False
    assert store.get_write_batch(batch["writeBatchId"])["changes"] == [
        {"path": "Assets/Feature.asmdef", "kind": "write", "contentSha256": "a" * 64},
        {"path": "Assets/Removed.cs", "kind": "delete", "contentSha256": ""},
    ]


@pytest.mark.parametrize("foreign", [False, True])
def test_wp05_t08_unknown_or_foreign_batch_never_dispatches_compile(tmp_path, foreign) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path / "connected-project"))
    batch_id = "unknown-batch"
    if foreign:
        foreign_store = StateStore()
        foreign_store.configure_project(str(tmp_path / "other-project"))
        batch_id = foreign_store.register_write_batch(
            ["Other.cs"], created_at=100, files_sha256="other", compile_when_edit_mode=True,
        )["writeBatchId"]

    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)
    dispatched: list[object] = []

    async def unexpected_compile(*args, **kwargs):
        dispatched.append((args, kwargs))
        raise AssertionError("an unknown or foreign batch must not dispatch a compile")

    service.compile = unexpected_compile
    result = asyncio.run(service.safe_compile_and_wait(
        write_batch_id=batch_id,
        write_batch_created_at=100,
    ))

    assert not result.ok and result.error.code == "WRITE_BATCH_NOT_FOUND"
    assert result.error.detail["dispatchAttempted"] is False
    assert dispatched == []


@pytest.mark.parametrize("include_warnings", [{}, [], "true", 1, None])
def test_wp05_t08_invalid_include_warnings_never_dispatches(tmp_path, include_warnings) -> None:
    store = StateStore()
    store.configure_project(str(tmp_path))
    calls: list[object] = []

    async def dispatch(*args, **kwargs):
        calls.append((args, kwargs))
        raise AssertionError("invalid input must not reach Unity")

    result = asyncio.run(_compile_service(store, dispatch).compile_errors(
        "unknown-or-foreign-batch", include_warnings=include_warnings,
    ))

    assert not result.ok and result.error.code == "INVALID_PAYLOAD"
    assert result.error.detail["dispatchAttempted"] is False
    assert calls == []


def test_wp05_unrequested_unity_compile_diagnostics_do_not_default_to_warning_details() -> None:
    source = (
        Path(__file__).resolve().parents[2]
        / "Editor"
        / "Core"
        / "UPilotCompileService.cs"
    ).read_text(encoding="utf-8")

    assert "BuildCompileErrorsPayload(string requestId, bool includeWarnings = false)" in source
    assert "BuildLastCompileErrorsPayload(bool includeWarnings = false)" in source
