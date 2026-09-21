from __future__ import annotations

import asyncio
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.console_evidence import begin_console_evidence, finish_console_evidence
from upilot_mcp.domain.compile_service import CompileDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import ok
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
