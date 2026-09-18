from __future__ import annotations

import asyncio
import hashlib
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import ok, fail
from upilot_mcp.state_store import StateStore
from test_editor_execution_state_v2 import _snapshot


def test_explicit_identity_clear_and_new_operation_do_not_inherit_evidence(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.update_editor_execution_state(_snapshot(1, write_batch_id="old", write_batch_created_at=5))
    store.update_editor_execution_state(_snapshot(2, operation_id="", terminal=False, errors_verified=False))
    assert store.compile.write_batch_id == ""
    assert store.compile.last_compile_verified_at == 0
    assert store.compile.compile_operation_id == ""
    incoming = _snapshot(3, operation_id="new", terminal=False, errors_verified=False)
    for key in ("writeBatchId", "writeBatchCreatedAt", "lastCompileVerifiedAt"):
        incoming.pop(key)
    store.update_editor_execution_state(incoming)
    assert store.compile.write_batch_id == ""
    assert store.compile.write_batch_created_at == 0
    assert store.compile.last_compile_verified_at == 0


def test_pending_write_batch_distinguishes_missing_field_from_explicit_clear(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    first = _snapshot(1, operation_id="compile-a", terminal=False, errors_verified=False)
    first["pendingWriteBatchId"] = "batch-a"
    store.update_editor_execution_state(first)
    assert store.pending_write_batch_id == "batch-a"

    missing = _snapshot(2, operation_id="compile-a", terminal=False, errors_verified=False)
    missing.pop("pendingWriteBatchId", None)
    store.update_editor_execution_state(missing)
    assert store.pending_write_batch_id == "batch-a"

    cleared = _snapshot(3, operation_id="compile-a", terminal=False, errors_verified=False)
    cleared["pendingWriteBatchId"] = ""
    store.update_editor_execution_state(cleared)
    assert store.pending_write_batch_id == ""


@pytest.mark.parametrize("first_phase", ["completed", "failed"])
def test_overlapping_batch_preserves_own_terminal_after_restart(tmp_path, first_phase):
    store = StateStore()
    store.configure_project(str(tmp_path))
    first = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(first["writeBatchId"], "compiling", compile_operation_id="compile-a")
    second = store.register_write_batch(["B.cs"], created_at=200, files_sha256="b", compile_when_edit_mode=True)
    store.update_editor_execution_state(_snapshot(1, phase=first_phase, write_batch_id=first["writeBatchId"],
                                                 write_batch_created_at=100, observed_at=150))
    assert store.pending_write_batch_id == second["writeBatchId"]
    assert store.get_write_batch(second["writeBatchId"])["terminalSnapshot"] is None
    first_terminal = store.get_write_batch(first["writeBatchId"])
    store.mark_write_batch(second["writeBatchId"], "compiling", compile_operation_id="compile-b")
    store.update_editor_execution_state(_snapshot(2, operation_id="compile-b",
                                                 write_batch_id=second["writeBatchId"],
                                                 write_batch_created_at=200, observed_at=250))
    assert store.get_write_batch(second["writeBatchId"])["outcome"] == "passed"
    assert store.get_write_batch(first["writeBatchId"]) == first_terminal
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    result = restored.get_write_batch(first["writeBatchId"])
    assert result == first_terminal
    assert result["status"] == ("verified" if first_phase == "completed" else "failed")
    assert result["terminalSnapshot"]["compileOperationId"] == "compile-a"
    assert result["correlationVerified"]
    assert result["outcome"] == ("passed" if first_phase == "completed" else "failed")
    assert restored.get_write_batch(second["writeBatchId"])["outcome"] == "passed"
    restored.mark_write_batch(first["writeBatchId"], "compiling", compile_operation_id="unrelated")
    assert restored.get_write_batch(first["writeBatchId"]) == result


def test_write_batch_status_is_read_only_and_project_scoped(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1, files_sha256="a", compile_when_edit_mode=True)
    service = ResourceDomainService()
    service.server = SimpleNamespace(state=store)
    result = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert result.ok and not result.data["terminal"]
    assert result.data["waitingReason"] == "disconnected"
    assert result.data["attentionRequired"] is True
    store.configure_project(str(tmp_path / "other"))
    assert not asyncio.run(service.write_batch_status(batch["writeBatchId"])).ok


def test_write_batch_wait_reason_distinguishes_playmode_from_stale_context(tmp_path, monkeypatch):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1000, files_sha256="a", compile_when_edit_mode=True)
    service = ResourceDomainService()
    service.server = SimpleNamespace(state=store)
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 40000)

    store.editor.connected = True
    store.editor.authoritative = True
    store.editor.updated_at = 10**15
    store.editor.play_mode_state = "play"
    play = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert play.data["waitingReason"] == "playmode"
    assert play.data["pendingAgeMs"] == 39000
    assert play.data["attentionRequired"] is True

    store.editor.play_mode_state = "edit"
    store.editor.authoritative = False
    stale = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert stale.data["waitingReason"] == "stale"


@pytest.mark.parametrize("scenario,expected", [
    ("reload", "reload"), ("disconnected", "disconnected"),
    ("stale", "stale"), ("playmode", "playmode"),
])
def test_write_batch_wait_reason_uses_authoritative_precedence(tmp_path, monkeypatch, scenario, expected):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=10_000, files_sha256="a", compile_when_edit_mode=True)
    service = ResourceDomainService()
    service.server = SimpleNamespace(state=store)
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 20_000)
    store.editor.connected = True
    store.editor.authoritative = True
    store.editor.updated_at = 10**15
    store.editor.play_mode_state = "edit"
    store.compile.phase = "idle"
    store.compile.last_progress_at = 19_999

    if scenario == "reload":
        # Reload must win even if a stale raw PlayMode flag is also present.
        store.compile.phase = "domain_reload"
        store.editor.play_mode_state = "play"
    elif scenario == "disconnected":
        store.editor.connected = False
    elif scenario == "stale":
        store.editor.authoritative = False
    else:
        store.editor.play_mode_state = "play"

    result = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert result.ok and result.data["waitingReason"] == expected
    assert result.data["attentionRequired"] is False


def test_write_batch_attention_clears_when_the_same_batch_makes_progress(tmp_path, monkeypatch):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1_000, files_sha256="a", compile_when_edit_mode=True)
    service = ResourceDomainService()
    service.server = SimpleNamespace(state=store)
    store.editor.connected = True
    store.editor.authoritative = True
    store.editor.updated_at = 10**15
    store.editor.play_mode_state = "edit"
    store.compile.phase = "compiling"
    monkeypatch.setattr("upilot_mcp.domain.resource_service.now_ms", lambda: 32_000)

    store.compile.last_progress_at = 1_000
    stalled = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert stalled.data["waitingReason"] == "compile_in_progress"
    assert stalled.data["attentionRequired"] is True

    store.compile.last_progress_at = 31_999
    recovered = asyncio.run(service.write_batch_status(batch["writeBatchId"]))
    assert recovered.data["waitingReason"] == "compile_in_progress"
    assert recovered.data["attentionRequired"] is False
    assert store.get_write_batch(batch["writeBatchId"])["status"] == "pending"


def test_unattributed_automatic_compile_is_never_reused_as_batch_evidence(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.editor.connected = True
    store.editor.authoritative = True
    store.editor.play_mode_state = "edit"
    store.editor.updated_at = 10**15
    store.compile.compile_origin = "unity_auto"
    store.compile.compile_operation_id = "automatic-compile"
    store.compile.terminal = True
    store.compile.errors_verified = True

    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)

    result = asyncio.run(service.compile_status())

    assert result.ok
    assert result.data["reuseDecision"] == "unattributed_auto_compile"
    assert result.data["inputCoverageVerified"] is False
    assert result.data["replayStartAttempted"] is False
    assert "registered_write_batch" in result.data["missingEvidence"]
    assert result.data["reuseNextAction"].startswith("Register the saved change batch")
    assert store.pending_write_batch_id == ""


def test_automatic_compile_with_late_batch_identity_remains_unattributed(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.compile.compile_origin = "unity_auto"
    store.compile.compile_operation_id = "automatic-compile"
    store.compile.write_batch_id = "batch-registered-after-start"
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)

    result = asyncio.run(service.compile_status())

    assert result.ok
    assert result.data["reuseDecision"] == "unattributed_auto_compile"
    assert result.data["inputCoverageVerified"] is False
    assert result.data["replayStartAttempted"] is False
    assert result.data["observedWriteBatchId"] == "batch-registered-after-start"
    assert "batch_registered_before_automatic_compile" in result.data["missingEvidence"]


def test_manual_compile_status_does_not_claim_automatic_reuse_decision(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.compile.compile_origin = "mcp"
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store)

    result = asyncio.run(service.compile_status())

    assert result.ok
    assert "reuseDecision" not in result.data
    assert "inputCoverageVerified" not in result.data


def test_inflight_batch_request_survives_restart_and_rejects_conflicting_terminal(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    first = _snapshot(1, phase="compiling", terminal=False, errors_verified=False,
                      write_batch_id=batch["writeBatchId"], write_batch_created_at=100)
    first["compileRequestId"] = "original-request"
    assert store.update_editor_execution_state(first)
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    assert restored.get_write_batch(batch["writeBatchId"])["compileRequestId"] == "original-request"
    terminal = _snapshot(2, write_batch_id=batch["writeBatchId"], write_batch_created_at=100)
    terminal["compileRequestId"] = "other-request"
    assert restored.update_editor_execution_state(terminal)
    result = restored.get_write_batch(batch["writeBatchId"])
    assert result["terminalSnapshot"] is None
    assert result["outcome"] == "unknown" and not restored.correlation_verified
    terminal.update(sequence=3, compileRequestId="original-request")
    assert restored.update_editor_execution_state(terminal)
    assert restored.get_write_batch(batch["writeBatchId"])["outcome"] == "passed"


def test_historical_verified_without_snapshot_does_not_prove_pass(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=1, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "verified", compile_operation_id="legacy")
    result = store.get_write_batch(batch["writeBatchId"])
    assert result["status"] == "verified"
    assert result["outcome"] == "unknown"
    assert not result["terminal"] and not result["correlationVerified"]


@pytest.mark.parametrize("inner_ok", [True, False, None])
def test_refresh_requires_explicit_inner_success(inner_ok):
    service = ResourceDomainService()
    calls = []
    async def dispatch(request_id, name, payload):
        calls.append(name)
        return ok(request_id, {"ok": inner_ok, "status": "ok"})
    service.dispatcher = SimpleNamespace(call=dispatch)
    result = asyncio.run(service.asset_refresh())
    assert result.ok == (inner_ok is True)
    assert calls == ["asset.refresh"]


@pytest.mark.parametrize("suffix", [" -batchMode -name AssetImportWorker0", " -batchMode", " -adb2", " -ump", " -worker"])
def test_hang_rejects_worker_even_with_exact_project(tmp_path, suffix):
    row = {"ProcessId": 1, "ExecutablePath": "C:/Unity/Unity.exe",
           "CommandLine": f'Unity.exe -projectPath "{tmp_path}"{suffix}'}
    assert not ProjectAnalysisDomainService._matches_editor_process(row, tmp_path)


def test_hang_resolution_reports_excluded_worker_role(tmp_path):
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=0)))
    service._analysis_project_root = lambda: tmp_path
    main = {"ProcessId": 7, "ProcessCreatedAt": 100, "ExecutablePath": "C:/Unity/Unity.exe",
            "CommandLine": f'Unity.exe -projectPath "{tmp_path}"'}
    worker = {"ProcessId": 8, "ProcessCreatedAt": 101, "ExecutablePath": "C:/Unity/Unity.exe",
              "CommandLine": f'Unity.exe -adb2 -batchMode -name AssetImportWorker0 -projectPath "{tmp_path}"'}
    service._query_unity_processes = lambda: ([main, worker], {
        "processQuerySucceeded": True, "processQueryError": "", "processQueryExitCode": 0,
    })
    service._process_creation_time = lambda _pid: 109

    pid, result = service._resolve_live_unity_pid()

    assert pid == 7
    assert result["candidateProcessIds"] == [7]
    assert result["excludedProcessIds"] == [8]
    worker_result = next(item for item in result["candidates"] if item["processId"] == 8)
    assert worker_result["processRole"] == "assetImportWorker"
    assert worker_result["projectPathMatches"] is True
    assert worker_result["exclusionReasons"] == ["worker_role"]


def test_hang_rejects_path_prefix_and_ambiguous_main_editors(tmp_path):
    row = {"ProcessId": 1, "ExecutablePath": "C:/Unity/Unity.exe",
           "CommandLine": f'Unity.exe -projectPath "{tmp_path}-other"'}
    assert not ProjectAnalysisDomainService._matches_editor_process(row, tmp_path)
    row["CommandLine"] = f'Unity.exe -projectPath "{tmp_path}"'
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=1)))
    service._analysis_project_root = lambda: tmp_path
    service._query_unity_processes = lambda: [row, {**row, "ProcessId": 2}]
    pid, result = service._resolve_live_unity_pid()
    assert pid == 0 and result["reason"] == "ambiguous"


@pytest.mark.parametrize("observed", [0, 200, 109])
def test_hang_requires_same_creation_identity_as_discovery(tmp_path, observed):
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=1)))
    service._analysis_project_root = lambda: tmp_path
    service._query_unity_processes = lambda: [{
        "ProcessId": 1, "ProcessCreatedAt": 100, "ExecutablePath": "C:/Unity/Unity.exe",
        "CommandLine": f'Unity.exe -projectPath "{tmp_path}"',
    }]
    service._process_creation_time = lambda _pid: observed
    pid, result = service._resolve_live_unity_pid()
    assert bool(pid) == (observed == 109)
    assert result["projectIdentityVerified"] == (observed == 109)


@pytest.mark.parametrize("scenario", ["worker", "multiple", "permission", "pid_reused", "missing_executable"])
def test_hang_capture_refusal_never_calls_dump(tmp_path, scenario):
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=7)))
    service._analysis_project_root = lambda: tmp_path
    row = {"ProcessId": 7, "ProcessCreatedAt": 100, "ExecutablePath": "C:/Unity/Unity.exe",
           "CommandLine": f'Unity.exe -projectPath "{tmp_path}"'}
    if scenario == "worker":
        row["CommandLine"] += " -name AssetImportWorker0"
    if scenario == "missing_executable":
        row["ExecutablePath"] = ""
    service._query_unity_processes = lambda: [row, {**row, "ProcessId": 8}] if scenario == "multiple" else [row]
    service._process_creation_time = lambda _pid: 0 if scenario == "permission" else 200 if scenario == "pid_reused" else 100
    dump_calls = []

    def dump(*args):
        dump_calls.append(args)
        raise AssertionError("An unverified PID must not enter dump capture.")

    service._write_windows_minidump = dump
    result = asyncio.run(service.hang_capture())
    assert not result.ok
    assert result.error.code in {"UNITY_PROCESS_UNKNOWN", "HANG_DUMP_UNSUPPORTED"}
    assert dump_calls == []
    assert not list(tmp_path.rglob("*.dmp"))


def test_hang_process_query_failure_is_diagnostic_and_never_attempts_dump(tmp_path):
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=7)))
    service._analysis_project_root = lambda: tmp_path
    service._windows_dump_supported = lambda: True
    service._query_unity_processes = lambda: ([], {
        "processQuerySucceeded": False,
        "processQueryError": "access denied",
        "processQueryExitCode": 5,
    })
    dump_calls = []
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture())

    assert not result.ok and result.error.code == "UNITY_PROCESS_UNKNOWN"
    assert result.error.detail["reason"] == "process_query_failed"
    assert result.error.detail["processQueryError"] == "access denied"
    assert result.error.detail["processQueryExitCode"] == 5
    assert result.error.detail["resolvedProcessId"] == 0
    assert dump_calls == []
    assert not list(tmp_path.rglob("*.dmp"))


@pytest.mark.parametrize("resolved", [True, False])
def test_mcp_status_disconnected_process_identity_never_reuses_unverified_pid(tmp_path, resolved):
    state = StateStore()
    state.configure_project(str(tmp_path))
    session = SimpleNamespace(
        session_id="stale-session",
        project_path=str(tmp_path),
        unity_version="6000.0",
        platform="windows",
        process_id=11,
        last_heartbeat_at=0,
    )
    server = SimpleNamespace(
        session_manager=SimpleNamespace(active=session, is_connected=lambda: False),
        state=state,
        is_ready=lambda: False,
        mcp_label="test",
        host="127.0.0.1",
        port=8765,
    )
    service = StatusDomainService.__new__(StatusDomainService)
    service.server = server
    service.dispatcher = SimpleNamespace(timeout_policy_snapshot=lambda: {})
    service._resolve_live_unity_pid = lambda: ((22, {
        "projectIdentityVerified": True,
        "processRole": "editor",
        "resolvedProcessId": 22,
    }) if resolved else (0, {
        "projectIdentityVerified": False,
        "reason": "process_query_failed",
        "processQuerySucceeded": False,
        "processQueryError": "access denied",
        "resolvedProcessId": 0,
    }))

    result = asyncio.run(service.mcp_status(include_capabilities=False))

    assert result.ok
    assert result.data["session"]["reportedProcessId"] == 11
    assert result.data["session"]["processId"] == (22 if resolved else 0)
    assert result.data["runtimeIdentity"]["unityEditor"]["verified"] is resolved
    assert result.data["processIdentity"]["projectIdentityVerified"] is resolved


def _hang_preflight_service(tmp_path):
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=None),
                                    state=SimpleNamespace(editor=SimpleNamespace(process_id=42)))
    service._analysis_project_root = lambda: tmp_path
    service._windows_dump_supported = lambda: True
    service._resolve_live_unity_pid = lambda: (42, {
        "pidSource": "session",
        "projectIdentityVerified": True,
        "processCreatedAt": 123,
        "processRole": "editor",
    })
    service._process_memory_usage = lambda _pid, _created: {
        "ok": True,
        "workingSetBytes": 512 * 1024 * 1024,
        "peakWorkingSetBytes": 768 * 1024 * 1024,
        "privateBytes": 1024 * 1024 * 1024,
        "pagefileBytes": 1024 * 1024 * 1024,
        "peakPagefileBytes": 1280 * 1024 * 1024,
    }
    return service


def test_hang_capture_rejects_insufficient_space_before_dump(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._volume_space = lambda _path: {
        "volumeTotalBytes": 4 * 1024**3,
        "volumeUsedBytes": 3 * 1024**3,
        "freeBytes": 1024**3,
    }
    dump_calls = []
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture(dump_type="full"))

    assert not result.ok
    assert result.error.code == "HANG_DUMP_INSUFFICIENT_SPACE"
    detail = result.error.detail
    assert detail["expectedBytes"] == detail["expectedBytesMax"]
    assert detail["requiredFreeBytes"] == detail["expectedBytes"] + detail["reserveBytes"]
    assert detail["freeBytes"] < detail["requiredFreeBytes"]
    assert detail["workingSetBytes"] > 0 and detail["privateBytes"] > 0
    assert detail["dumpAttempted"] is False
    assert detail["preflightPassed"] is False
    assert dump_calls == []
    assert not list(tmp_path.rglob("*.dmp"))


def test_hang_capture_success_reports_preflight_and_streamed_hash(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._volume_space = lambda _path: {
        "volumeTotalBytes": 16 * 1024**3,
        "volumeUsedBytes": 4 * 1024**3,
        "freeBytes": 12 * 1024**3,
    }
    dump_calls = []

    def write_dump(pid, path, dump_type, created_at):
        dump_calls.append((pid, path, dump_type, created_at))
        path.write_bytes(b"bounded-dump")
        return True, 0

    service._write_windows_minidump = write_dump
    result = asyncio.run(service.hang_capture(output_path="Log/UPilotDiagnostics/safe.dmp", dump_type="mini"))

    assert result.ok
    assert len(dump_calls) == 1
    assert result.data["preflightPassed"] is True
    assert result.data["dumpAttempted"] is True
    assert result.data["dumpType"] == "mini"
    assert result.data["bytes"] == len(b"bounded-dump")
    assert result.data["sha256"] == hashlib.sha256(b"bounded-dump").hexdigest()
    assert "ok" not in result.data
    assert result.data["reserveMaintained"] is True
    assert result.data["processTerminated"] is False


def test_hang_capture_invalid_type_has_no_preflight_or_dump(tmp_path):
    service = _hang_preflight_service(tmp_path)
    preflight_calls = []
    dump_calls = []
    service._process_memory_usage = lambda *_: preflight_calls.append(True)
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture(dump_type="everything"))

    assert not result.ok
    assert result.error.code == "HANG_DUMP_TYPE_INVALID"
    assert result.error.detail["allowedDumpTypes"] == ["full", "heap", "mini"]
    assert result.error.detail["dumpAttempted"] is False
    assert preflight_calls == [] and dump_calls == []


def test_hang_capture_failure_preserves_partial_file_metadata(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._volume_space = lambda _path: {
        "volumeTotalBytes": 16 * 1024**3,
        "volumeUsedBytes": 4 * 1024**3,
        "freeBytes": 12 * 1024**3,
    }

    def fail_dump(_pid, path, _dump_type, _created_at):
        path.write_bytes(b"partial")
        return False, 112

    service._write_windows_minidump = fail_dump
    result = asyncio.run(service.hang_capture(dump_type="mini"))

    assert not result.ok
    assert result.error.code == "HANG_DUMP_FAILED"
    assert result.error.detail["dumpAttempted"] is True
    assert result.error.detail["partialFileExists"] is True
    assert result.error.detail["partialBytes"] == len(b"partial")
    assert result.error.detail["partialSha256"] == hashlib.sha256(b"partial").hexdigest()
    assert result.error.detail["win32Error"] == 112
    assert list(tmp_path.rglob("*.dmp"))


def test_hang_capture_memory_probe_failure_does_not_attempt_dump(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._process_memory_usage = lambda *_: {
        "ok": False,
        "memoryProbeError": "access denied",
        "memoryProbeWin32Error": 5,
    }
    dump_calls = []
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture())

    assert not result.ok
    assert result.error.code == "HANG_DUMP_PREFLIGHT_FAILED"
    assert result.error.detail["memoryProbeWin32Error"] == 5
    assert result.error.detail["dumpAttempted"] is False
    assert "ok" not in result.error.detail
    assert dump_calls == []


def test_hang_capture_zero_memory_metrics_does_not_attempt_dump(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._process_memory_usage = lambda *_: {
        "ok": True,
        "workingSetBytes": 0,
        "privateBytes": 0,
    }
    dump_calls = []
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture())

    assert not result.ok
    assert result.error.code == "HANG_DUMP_PREFLIGHT_FAILED"
    assert result.error.detail["preflightPassed"] is False
    assert result.error.detail["dumpAttempted"] is False
    assert dump_calls == []


def test_hang_capture_enforces_minimum_reserve_floor(tmp_path):
    service = _hang_preflight_service(tmp_path)
    service._volume_space = lambda _path: {
        "volumeTotalBytes": 16 * 1024**3,
        "volumeUsedBytes": 15 * 1024**3,
        "freeBytes": 1024**3,
    }
    dump_calls = []
    service._write_windows_minidump = lambda *args: dump_calls.append(args)

    result = asyncio.run(service.hang_capture(dump_type="mini", reserve_bytes=0))

    assert not result.ok
    assert result.error.code == "HANG_DUMP_INSUFFICIENT_SPACE"
    assert result.error.detail["requestedReserveBytes"] == 0
    assert result.error.detail["reserveBytes"] == 2 * 1024**3
    assert result.error.detail["minimumReserveBytes"] == 2 * 1024**3
    assert dump_calls == []


@pytest.mark.parametrize("query_failure", [False, True])
def test_safe_compile_failure_preserves_identity_without_second_start(tmp_path, query_failure):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="compile-a")
    store.update_editor_execution_state(_snapshot(1, phase="failed", operation_id="compile-a",
                                                 write_batch_id=batch["writeBatchId"],
                                                 write_batch_created_at=100, observed_at=200))
    store.compile.compile_request_id = "compile-request"
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store, is_ready=lambda: True,
                                    session_manager=SimpleNamespace(active=None))
    async def wait(**kwargs):
        return ok("wait", {"status": "failed"})
    async def errors(_request_id=""):
        return fail("errors", "DISCONNECTED", "lost") if query_failure else ok("errors", {"total": 1, "errors": [{"message": "fixture"}]})
    service.compile_wait = wait
    service.compile_errors = errors
    service._compile_diagnostics = lambda: {}
    result = asyncio.run(service.safe_compile_and_wait(
        attach_compile_request_id="compile-request", compile_operation_id="compile-a",
        write_batch_id=batch["writeBatchId"], write_batch_created_at=100, post_compile_delay_s=0))
    if query_failure:
        assert not result.ok and result.error.code == "COMPILE_ERRORS_NOT_VERIFIED"
    else:
        assert result.ok and result.data["status"] == "failed"
        assert result.data["correlationVerified"] is True
        assert result.data["compileOperationId"] == "compile-a"


def test_safe_compile_reuses_persisted_verified_batch_without_second_start(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "compiling", compile_operation_id="compile-a")
    terminal = _snapshot(
        1,
        operation_id="compile-a",
        write_batch_id=batch["writeBatchId"],
        write_batch_created_at=100,
        observed_at=200,
    )
    terminal["compileRequestId"] = "request-a"
    store.update_editor_execution_state(terminal)
    assert store.get_write_batch(batch["writeBatchId"])["outcome"] == "passed"

    store.update_editor_execution_state(_snapshot(
        2,
        operation_id="compile-b",
        write_batch_id="batch-b",
        write_batch_created_at=300,
        observed_at=400,
    ))
    service = CompileDomainService()
    service.server = SimpleNamespace(
        state=store,
        is_ready=lambda: True,
        session_manager=SimpleNamespace(active=None),
    )
    starts = []

    async def start(*args, **kwargs):
        starts.append((args, kwargs))
        raise AssertionError("a verified batch must not start another compile")

    service.compile = start
    result = asyncio.run(service.safe_compile_and_wait(
        write_batch_id=batch["writeBatchId"],
        write_batch_created_at=100,
        post_compile_delay_s=0,
    ))

    assert result.ok
    assert result.data["status"] == "success"
    assert result.data["reusedVerifiedBatch"] is True
    assert result.data["attachedToExistingCompile"] is False
    assert result.data["compileOperationId"] == "compile-a"
    assert result.data["compileRequestId"] == "request-a"
    assert result.data["writeBatchId"] == batch["writeBatchId"]
    assert starts == []


def test_safe_compile_does_not_replay_unverified_terminal_batch(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["A.cs"], created_at=100, files_sha256="a", compile_when_edit_mode=True)
    store.mark_write_batch(batch["writeBatchId"], "failed", error="observation timeout")
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store, is_ready=lambda: True)
    starts = []

    async def start(*args, **kwargs):
        starts.append((args, kwargs))
        raise AssertionError("an unverified terminal batch must not be replayed")

    service.compile = start
    result = asyncio.run(service.safe_compile_and_wait(
        write_batch_id=batch["writeBatchId"],
        write_batch_created_at=100,
        post_compile_delay_s=0,
    ))

    assert not result.ok
    assert result.error.code == "COMPILE_CORRELATION_NOT_VERIFIED"
    assert result.error.detail["batchStatus"] == "failed"
    assert result.error.detail["outcome"] == "unknown"
    assert result.error.detail["correlationVerified"] is False
    assert starts == []


@pytest.mark.parametrize("outcome", ["exception", "other_batch", "unverified", "success"])
def test_safe_compile_uses_original_terminal_identity(tmp_path, outcome):
    store = StateStore()
    store.configure_project(str(tmp_path))
    store.update_editor_execution_state(_snapshot(
        1, operation_id="compile-a", write_batch_id="batch-a",
        write_batch_created_at=100, observed_at=200))
    store.compile.compile_request_id = "request-a"
    service = CompileDomainService()
    service.server = SimpleNamespace(state=store, is_ready=lambda: True)

    async def wait(**kwargs):
        if outcome == "exception":
            raise RuntimeError("fixture assembly failure")
        if outcome == "other_batch":
            store.update_editor_execution_state(_snapshot(
                2, operation_id="compile-b", write_batch_id="batch-b",
                write_batch_created_at=200, observed_at=300))
        if outcome == "unverified":
            store.compile.errors_verified = False
        return ok("wait", {"status": "completed"})

    async def errors(_request_id):
        return ok("errors", {"total": 0})

    service.compile_wait = wait
    service.compile_errors = errors
    result = asyncio.run(service.safe_compile_and_wait(
        attach_compile_request_id="request-a", compile_operation_id="compile-a",
        write_batch_id="batch-a", write_batch_created_at=100, post_compile_delay_s=0))
    assert result.ok == (outcome == "success")
    detail = result.data if result.ok else result.error.detail
    assert detail["compileOperationId"] == "compile-a"
    assert detail["writeBatchId"] == "batch-a"
    if outcome == "exception":
        assert result.error.code == "COMPILE_WORKFLOW_EXCEPTION"
        assert detail["compileRequestId"] == "request-a"
        assert "do not trigger" in detail["nextAction"]
