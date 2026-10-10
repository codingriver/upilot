from __future__ import annotations

import asyncio
from copy import deepcopy

import pytest
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.console_evidence import begin_console_evidence, finish_console_evidence
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore


class _ExecutionState:
    def __init__(self) -> None:
        self.domain = 1

    def execution_state(self):
        return {
            "sessionId": "session-a",
            "producerEpoch": "epoch-a",
            "domainGeneration": self.domain,
            "snapshotId": f"epoch-a:{self.domain}:1",
            "observedAt": 10,
        }


def test_capture_boundaries_are_reused_without_stop_authority() -> None:
    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, _request_id, name, payload, timeout_ms=None):
            self.calls.append((name, payload, timeout_ms))
            if name == "console.capture.list":
                return ok("list", {"sessions": [{"sessionId": "capture-a", "active": True, "nextSequence": 7}]})
            if name == "console.capture.status":
                return ok("status", {"sessionId": "capture-a", "active": True, "nextSequence": 11, "droppedCount": 0})
            raise AssertionError(name)

    async def scenario() -> None:
        dispatcher = Dispatcher()
        state = _ExecutionState()
        evidence = await begin_console_evidence(dispatcher, state)
        completed = await finish_console_evidence(dispatcher, state, evidence)
        assert completed["source"] == "persistentCapture"
        assert completed["startSequence"] == 7 and completed["endSequence"] == 11
        assert completed["coverage"] == "complete"
        assert all(name != "console.capture.stop" for name, _, _ in dispatcher.calls)

    asyncio.run(scenario())


def test_console_delta_reports_clear_gap_instead_of_claiming_complete() -> None:
    class Dispatcher:
        async def call(self, _request_id, name, _payload, timeout_ms=None):
            if name == "console.capture.list":
                return ok("list", {"sessions": []})
            if name == "console.logs.mark":
                return ok("mark", {"cursor": 20, "totalCount": 20})
            if name == "console.logs.tail":
                return ok("tail", {"nextCursor": 3, "totalCount": 3, "logs": [], "truncated": False})
            raise AssertionError(name)

    async def scenario() -> None:
        state = _ExecutionState()
        evidence = await begin_console_evidence(Dispatcher(), state)
        completed = await finish_console_evidence(Dispatcher(), state, evidence)
        assert completed["source"] == "consoleDelta"
        assert completed["coverage"] == "partial"
        assert completed["gapReason"] == "console_cleared"

    asyncio.run(scenario())


def test_search_by_run_identity_uses_persisted_bounded_delta(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "run-a", {
        "source": "consoleDelta",
        "startSequence": 4,
        "endSequence": 6,
        "coverage": "complete",
        "gapReason": "",
        "logs": [
            {"type": "Error", "message": "boom", "stackTrace": "at Demo"},
            {"type": "Log", "message": "fine", "stackTrace": ""},
        ],
    })
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)

    result = asyncio.run(service.console_search_logs(run_guid="run-a", log_type="Error"))
    assert result.ok and [item["message"] for item in result.data["logs"]] == ["boom"]
    assert result.data["association"] == "observed_during_run_not_causal"

    conflict = asyncio.run(service.console_search_logs(run_guid="run-a", compile_operation_id="compile-a"))
    assert not conflict.ok and conflict.error.code == "CONSOLE_EVIDENCE_FILTER_CONFLICT"


def test_persistent_capture_search_uses_the_saved_fixed_range(tmp_path: Path) -> None:
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "run-capture", {
        "source": "persistentCapture",
        "captureSessionId": "capture-a",
        "startSequence": 7,
        "endSequence": 11,
        "coverage": "complete",
        "gapReason": "",
        "logs": [],
    })
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    calls = []

    async def read(**kwargs):
        calls.append(kwargs)
        return ok("read", {"records": [{"sequence": 8, "message": "inside"}]})

    service.console_capture_read = read
    result = asyncio.run(service.console_search_logs(run_guid="run-capture"))

    assert result.ok
    assert calls == [{
        "session_id": "capture-a",
        "from_sequence": 7,
        "to_sequence": 11,
        "count": 200,
        "log_type": "",
        "include_stack_trace": False,
        "contains": None,
        "contains_all": False,
        "regex": "",
        "newest_first": True,
    }]
    assert result.data["association"] == "observed_during_run_not_causal"


def test_console_evidence_survives_state_store_restart(tmp_path: Path) -> None:
    first = StateStore()
    first.configure_project(str(tmp_path))
    first.save_console_evidence("runGuid", "run-restart", {
        "source": "consoleDelta",
        "coverage": "partial",
        "gapReason": "domain_reload_boundary",
        "startSequence": 1,
        "endSequence": 2,
        "logs": [{"message": "persisted"}],
    })

    restarted = StateStore()
    restarted.configure_project(str(tmp_path))

    assert restarted.get_console_evidence("runGuid", "run-restart")["logs"] == [
        {"message": "persisted"}
    ]


def test_domain_reload_and_capture_loss_are_reported_as_gaps() -> None:
    class Dispatcher:
        async def call(self, _request_id, name, _payload, timeout_ms=None):
            if name == "console.capture.list":
                return ok("list", {"sessions": [{"sessionId": "capture-a", "active": True, "nextSequence": 3}]})
            if name == "console.capture.status":
                return ok("status", {"sessionId": "capture-a", "active": True, "nextSequence": 6, "droppedCount": 2})
            raise AssertionError(name)

    async def scenario() -> None:
        state = _ExecutionState()
        evidence = await begin_console_evidence(Dispatcher(), state)
        state.domain = 2
        completed = await finish_console_evidence(Dispatcher(), state, evidence)
        assert completed["coverage"] == "partial"
        assert completed["gapReason"] == "capture_dropped_records"
        assert completed["droppedCount"] == 2

    asyncio.run(scenario())


def test_direct_compile_boundary_is_persisted_and_closed_by_wait(tmp_path: Path) -> None:
    class Dispatcher:
        async def call(self, request_id, name, _payload, timeout_ms=None):
            if name == "resource.editorState":
                return ok(request_id, {"isCompiling": False})
            if name == "console.capture.list":
                return ok(request_id, {"sessions": []})
            if name == "console.logs.mark":
                return ok(request_id, {"cursor": 4, "totalCount": 4})
            if name == "compile.request":
                return ok(request_id, {"compileOperationId": "compile-direct", "compileRequestId": request_id})
            if name == "console.logs.tail":
                return ok(request_id, {
                    "nextCursor": 6,
                    "totalCount": 6,
                    "truncated": False,
                    "logs": [{"type": "Error", "message": "during compile"}],
                })
            raise AssertionError(name)

    async def scenario() -> None:
        state = StateStore()
        state.configure_project(str(tmp_path))
        state.editor.connected = True
        state.editor.authoritative = True
        state.editor.play_mode_state = "edit"
        state.editor.updated_at = 10**15
        service = CompileDomainService()
        service.dispatcher = Dispatcher()
        service.server = SimpleNamespace(
            state=state,
            session_manager=SimpleNamespace(active=None),
        )

        started = await service.compile()
        assert started.ok
        pending = state.get_console_evidence("compileOperationId", "compile-direct")
        assert pending["coverage"] == "pending"
        assert started.data["consoleEvidence"]["startSequence"] == 4

        async def observed_wait(*_args, **_kwargs):
            return ok("wait", {"status": "ready", "terminal": True})

        service._compile_wait = observed_wait
        completed = await service.compile_wait()
        evidence = state.get_console_evidence("compileOperationId", "compile-direct")
        assert completed.ok and evidence["coverage"] == "complete"
        assert evidence["endSequence"] == 6
        assert evidence["logs"][0]["message"] == "during compile"

    asyncio.run(scenario())


def test_incremental_terminal_test_result_closes_console_evidence(tmp_path: Path) -> None:
    class Dispatcher:
        async def call(self, request_id, name, _payload, timeout_ms=None):
            if name == "test.results":
                return ok(request_id, {
                    "runGuid": "run-incremental",
                    "status": "completed",
                    "phase": "completed",
                    "resultStreamVersion": 1,
                    "lastDeliveredEventSequence": 0,
                    "events": [],
                })
            if name == "console.logs.tail":
                return ok(request_id, {
                    "nextCursor": 6,
                    "totalCount": 6,
                    "truncated": False,
                    "logs": [{"type": "Log", "message": "during test"}],
                })
            raise AssertionError(name)

    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "run-incremental", {
        "source": "consoleDelta",
        "startSequence": 4,
        "endSequence": None,
        "coverage": "pending",
        "gapReason": "",
        "startedAt": 1,
        "endedAt": 0,
        "startIdentity": {"producerEpoch": "", "domainGeneration": 0},
        "endIdentity": {},
        "logs": [],
    })
    service = TestDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=state)

    result = asyncio.run(service.test_results("run-incremental", cursor="begin"))

    assert result.ok
    assert result.data["consoleEvidence"]["coverage"] == "complete"
    assert result.data["consoleEvidence"]["endSequence"] == 6
    persisted = state.get_console_evidence("runGuid", "run-incremental")
    assert persisted["coverage"] == "complete"
    assert persisted["endedAt"] > 0


@pytest.mark.parametrize("source", ["consoleDelta", "persistentCapture"])
@pytest.mark.parametrize("include_stack", [False, True])
@pytest.mark.parametrize("max_length", [0, 8])
def test_identity_search_projects_only_bounded_logs_and_keeps_saved_evidence(
    tmp_path, source, include_stack, max_length,
):
    evidence = {
        "source": source, "captureSessionId": "capture-a", "startSequence": 7, "endSequence": 510,
        "coverage": "partial", "gapReason": "domain_reload_boundary",
        "logs": [{"type": "Warning", "message": "unmatched warning", "stackTrace": "private warning stack"}] * 500
                + [{"type": "Error", "message": "selected message " + str(i), "stackTrace": "at Selected"}
                   for i in range(3)],
    }
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("compileOperationId", "compile-original", evidence)
    saved = deepcopy(state.get_console_evidence("compileOperationId", "compile-original"))
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    # Native capture.read owns filtering and its cursor; only response projection belongs here.
    capture_logs = deepcopy(evidence["logs"][-2:][::-1])
    async def read(**kwargs):
        assert kwargs["from_sequence"] == 7 and kwargs["to_sequence"] == 510
        assert kwargs["count"] == 2 and kwargs["log_type"] == "Error"
        assert kwargs["include_stack_trace"] is include_stack
        return ok("read", {"logs": capture_logs, "nextSequence": 510, "scanComplete": True})
    service.console_capture_read = read
    response = asyncio.run(service.console_search_logs(
        compile_operation_id="compile-original", log_type="Error", count=1, max_count=2,
        include_stack_trace=include_stack, max_message_length=max_length,
    ))
    assert response.ok
    assert len(response.data["logs"]) == 2
    for record in response.data["logs"]:
        assert record["type"] == "Error"
        assert record["stackTrace"] == ("at Selected" if include_stack else "")
        assert record["message"] == ("selected" if max_length else record["message"])
    assert response.data["logs"][0]["message"] == ("selected" if max_length else "selected message 2")
    assert response.data["consoleEvidence"] == {k: v for k, v in saved.items() if k != "logs"}
    assert response.data["association"] == "observed_during_run_not_causal"
    if source == "persistentCapture":
        assert response.data["nextSequence"] == 510
        assert capture_logs[0]["stackTrace"] == "at Selected"
        assert capture_logs[0]["message"] == "selected message 2"
    else:
        assert response.data["total"] == 3
    assert state.get_console_evidence("compileOperationId", "compile-original") == saved


@pytest.mark.parametrize("source", ["consoleDelta", "persistentCapture"])
def test_identity_search_zero_matches_does_not_leak_nested_logs(tmp_path, source):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "run-original", {
        "source": source, "captureSessionId": "capture-a", "startSequence": 2, "endSequence": 503,
        "coverage": "partial", "gapReason": "domain_reload_boundary",
        "logs": [{"type": "Warning", "message": "do not leak"}] * 500,
    })
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    async def read(**kwargs):
        return ok("read", {"logs": [], "nextSequence": 503, "scanComplete": True})
    service.console_capture_read = read
    response = asyncio.run(service.console_search_logs(run_guid="run-original", log_type="Error", count=15))
    assert response.data["logs"] == []
    assert "logs" not in response.data["consoleEvidence"]
    assert response.data["consoleEvidence"]["coverage"] == "partial"
    assert "do not leak" not in str(response.data)


@pytest.mark.parametrize("failed", [False, True])
def test_compile_wait_projects_evidence_on_success_and_error_without_modifying_storage(tmp_path, failed):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.compile.compile_operation_id = "compile-original"
    evidence = {"source": "consoleDelta", "coverage": "partial", "gapReason": "domain_reload_boundary",
                "startSequence": 4, "endSequence": 504, "logs": [{"message": "saved log"}] * 500}
    state.save_console_evidence("compileOperationId", "compile-original", evidence)
    service = CompileDomainService()
    service.server = SimpleNamespace(state=state)
    async def wait(*args):
        return fail("wait", "EXPECTED", "original error") if failed else ok("wait", {"terminal": True})
    service._compile_wait = wait
    response = asyncio.run(service.compile_wait())
    payload = response.error.detail if failed else response.data
    assert payload["consoleEvidence"] == {k: v for k, v in evidence.items() if k != "logs"}
    assert state.get_console_evidence("compileOperationId", "compile-original") == evidence


@pytest.mark.parametrize("route", ["compile", "safe_compile_and_wait"])
@pytest.mark.parametrize("failed", [False, True])
def test_compile_entry_points_project_success_and_error_evidence(tmp_path, monkeypatch, route, failed):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.connected = True
    state.editor.authoritative = True
    state.editor.play_mode_state = "edit"
    state.editor.updated_at = 10**15
    state.compile.compile_operation_id = "compile-original"
    evidence = {"source": "consoleDelta", "coverage": "partial", "gapReason": "domain_reload_boundary",
                "startSequence": 4, "endSequence": 504, "logs": [{"message": "saved log"}] * 500}

    async def begin(*args):
        return deepcopy(evidence)

    async def finish(*args):
        return deepcopy(evidence)

    monkeypatch.setattr("upilot_mcp.domain.compile_service.begin_console_evidence", begin)
    monkeypatch.setattr("upilot_mcp.domain.compile_service.finish_console_evidence", finish)

    def result():
        return (fail("compile", "EXPECTED", "original error", {"compileOperationId": "compile-original"})
                if failed else ok("compile", {"compileOperationId": "compile-original"}))

    class Dispatcher:
        async def call(self, request_id, name, payload, timeout_ms=None):
            if name == "resource.editorState":
                return ok(request_id, {"isCompiling": False})
            assert name == "compile.request"
            return result()

    service = CompileDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=state, session_manager=SimpleNamespace(active=None))

    async def observed(*args):
        return result()

    service._safe_compile_and_wait = observed
    response = asyncio.run(getattr(service, route)())
    assert response.ok is not failed
    if failed:
        assert response.error.code == "EXPECTED"
    payload = response.error.detail if failed else response.data
    assert payload["consoleEvidence"] == {k: v for k, v in evidence.items() if k != "logs"}
    assert state.get_console_evidence("compileOperationId", "compile-original") == evidence

@pytest.mark.parametrize("phase", ["testExecution", "cleanup", "sceneRestore"])
def test_phase_search_uses_only_frozen_ranges(tmp_path, phase):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "run-phase", {
        "source": "persistentCapture", "captureSessionId": "whole", "startSequence": 0, "endSequence": 999,
        "phases": [{"phase": phase, "source": "persistentCapture", "captureSessionId": "owned",
                    "startSequence": 7, "endSequence": 11, "endedAt": 20,
                    "coverage": "partial" if phase == "sceneRestore" else "complete",
                    "gapReason": "public_scene_events_only_not_full_restore_boundary" if phase == "sceneRestore" else ""}]})
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    calls = []
    async def read(**kwargs):
        calls.append(kwargs)
        return ok("read", {"logs": [{"sequence": 8, "message": "inside"}],
                           "diskSnapshotNextSequence": 12, "scanComplete": True})
    service.console_capture_read = read
    result = asyncio.run(service.console_search_logs(run_guid="run-phase", phase=phase))
    assert result.ok and result.data["logs"][0]["message"] == "inside"
    assert [(c["session_id"], c["from_sequence"], c["to_sequence"]) for c in calls] == [("owned", 7, 11)]
    assert result.data["consoleEvidence"]["coverage"] == ("partial" if phase == "sceneRestore" else "complete")


def test_phase_missing_or_switched_never_falls_back_to_whole_run(tmp_path):
    state = StateStore()
    state.configure_project(str(tmp_path))
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    for phases in ([], [{"phase": "cleanup", "source": "persistentCapture", "endedAt": 5,
                         "gapReason": "capture_switch_reload_or_cursor_reset"}]):
        state.save_console_evidence("runGuid", "old", {"logs": [{"message": "unrelated"}], "phases": phases})
        result = asyncio.run(service.console_search_logs(run_guid="old", phase="cleanup"))
        assert result.ok and result.data["logs"] == []
        assert result.data["consoleEvidence"]["coverage"] == "partial"
    assert not asyncio.run(service.console_search_logs(phase="cleanup")).ok


def test_phase_frozen_console_records_are_filtered_without_nested_leak(tmp_path):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "delta", {"phases": [{
        "phase": "cleanup", "source": "consoleDelta", "endedAt": 10, "coverage": "partial",
        "gapReason": "console_cursor_clear_or_reload_not_detectable",
        "logs": [{"message": "cleanup", "logType": "Warning"}, {"message": "hidden", "logType": "Log"}]}]})
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    result = asyncio.run(service.console_search_logs(run_guid="delta", phase="cleanup", log_type="Warning"))
    assert [x["message"] for x in result.data["logs"]] == ["cleanup"]
    assert "logs" not in result.data["consoleEvidence"]["phases"][0]

@pytest.mark.parametrize("newest", [True, False])
def test_phase_fallback_order_and_unknown_coverage_stay_explicit(tmp_path, newest):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "order", {"phases": [{
        "phase": "cleanup", "source": "consoleDelta", "endedAt": 10,
        "coverage": "partial", "logs": [{"message": "first"}, {"message": "last"}]}]})
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    result = asyncio.run(service.console_search_logs(run_guid="order", phase="cleanup", newest_first=newest))
    assert [x["message"] for x in result.data["logs"]] == (["last", "first"] if newest else ["first", "last"])
    assert result.data["consoleEvidence"]["coverage"] == "partial"
    assert result.data["consoleEvidence"]["gapReason"] == "phase_coverage_unverified"


@pytest.mark.parametrize("read_data,reason", [
    ({"ok": False, "error": "capture_missing"}, "phase_capture_read_failed"),
    ({"logs": [], "diskSnapshotNextSequence": 7, "scanComplete": True}, "phase_records_not_flushed"),
    ({"logs": [], "diskSnapshotNextSequence": 12, "scanComplete": False}, "phase_scan_incomplete_or_unverified"),
    ({"logs": []}, "phase_disk_coverage_unverified"),
])
def test_phase_capture_unflushed_or_unavailable_is_not_complete(tmp_path, read_data, reason):
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "buffered", {"phases": [{
        "phase": "cleanup", "source": "persistentCapture", "captureSessionId": "owned",
        "startSequence": 7, "endSequence": 11, "endedAt": 20, "coverage": "complete"}]})
    service = StatusDomainService()
    service.server = SimpleNamespace(state=state)
    async def read(**kwargs):
        assert kwargs["from_sequence"] == 7 and kwargs["to_sequence"] == 11
        return ok("read", read_data)
    service.console_capture_read = read
    result = asyncio.run(service.console_search_logs(run_guid="buffered", phase="cleanup"))
    assert result.ok and result.data["logs"] == []
    assert result.data["consoleEvidence"]["coverage"] == "partial"
    assert reason in result.data["consoleEvidence"]["gapReason"]


@pytest.mark.parametrize("phase", ["testExecution", "cleanup", "sceneRestore"])
@pytest.mark.parametrize("reopen_store", [False, True], ids=["same-store", "reopened-store"])
@pytest.mark.parametrize("cursor_reset", [False, True], ids=["clear-not-detectable", "cursor-reset"])
def test_phase_console_clear_gap_survives_late_queries_and_storage(
    tmp_path, phase, reopen_store, cursor_reset,
):
    # Model Unity's two honest fallback outcomes; do not clear a live Editor Console.
    reason = ("capture_switch_reload_or_cursor_reset" if cursor_reset
              else "console_cursor_clear_or_reload_not_detectable")
    frozen_logs = [{"message": "frozen phase record", "type": "Log"}]
    evidence = {
        "source": "consoleDelta", "coverage": "partial", "gapReason": "console_cleared",
        "logs": [{"message": "whole-run record must not leak"}],
        "phases": [{
            "phase": phase, "source": "consoleDelta", "startedAt": 1, "endedAt": 10,
            "startSequence": 20, "endSequence": 2 if cursor_reset else 22,
            "coverage": "partial", "gapReason": reason, "logs": frozen_logs,
        }],
    }
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.save_console_evidence("runGuid", "cleared-phase", evidence)
    if reopen_store:
        state = StateStore()
        state.configure_project(str(tmp_path))
    persisted = deepcopy(state.get_console_evidence("runGuid", "cleared-phase"))

    class NoLiveReads:
        async def call(self, *args, **kwargs):
            raise AssertionError("Frozen phase queries must not read the current Console")

    async def no_capture_read(**kwargs):
        raise AssertionError("Console clearing must not attach a different Capture")

    service = StatusDomainService()
    service.server = SimpleNamespace(state=state, dispatcher=NoLiveReads())
    service.console_capture_read = no_capture_read
    for _ in range(2):
        result = asyncio.run(service.console_search_logs(run_guid="cleared-phase", phase=phase))
        assert result.ok
        assert [item["message"] for item in result.data["logs"]] == (
            [] if cursor_reset else ["frozen phase record"])
        projected = result.data["consoleEvidence"]
        assert projected["coverage"] == "partial"
        assert reason in projected["gapReason"]
        assert "logs" not in projected["phases"][0]
        assert result.data["association"] == "observed_during_run_not_causal"
        assert state.get_console_evidence("runGuid", "cleared-phase") == persisted
