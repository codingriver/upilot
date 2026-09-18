from __future__ import annotations

import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.mcp_stdio_server import _redact_log_value
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore


class _CaptureDispatcher:
    def __init__(self, directory: Path, *, next_sequence: int = 4) -> None:
        self.directory = directory
        self.next_sequence = next_sequence
        self.calls: list[tuple[str, dict]] = []
        self.available = True
        self.pages: list[list[dict]] = []

    async def call(self, request_id: str, command: str, payload: dict, timeout_ms=None):
        self.calls.append((command, payload))
        if command == "console.capture.start":
            return ok(request_id, {"session": {
                "sessionId": "capture-start", "active": True,
                "nextSequence": 0, "directory": str(self.directory),
            }})
        if command == "console.capture.status":
            if not self.available:
                return fail(request_id, "COMMAND_TIMEOUT", "source is unavailable")
            return ok(request_id, {"session": {
                "sessionId": payload["sessionId"], "active": True,
                "nextSequence": self.next_sequence, "directory": str(self.directory),
            }})
        if command == "console.capture.read":
            index = 0 if not payload.get("continuationToken") else 1
            page = self.pages[index] if index < len(self.pages) else []
            token = "page-2" if index + 1 < len(self.pages) else ""
            return ok(request_id, {"sessionId": payload["sessionId"], "logs": page, "continuationToken": token})
        if command == "console.capture.stop":
            raise AssertionError("A read-only attachment must never stop its source capture")
        raise AssertionError(f"unexpected command: {command}")


def _service(tmp_path: Path, dispatcher: _CaptureDispatcher) -> StatusDomainService:
    store = StateStore()
    store.configure_project(str(tmp_path))
    service = StatusDomainService()
    service.server = SimpleNamespace(state=store)
    service.dispatcher = dispatcher
    return service


def test_capture_attachment_is_idempotent_and_never_stops_source(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        repeated = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok and repeated.ok
        assert attached.data["attachmentId"] == repeated.data["attachmentId"]
        assert attached.data["fromSequence"] == 4
        detached = await service.console_capture_detach(attached.data["attachmentId"])
        dispatcher.available = False
        repeated_detach = await service.console_capture_detach(attached.data["attachmentId"])
        assert detached.ok and repeated_detach.ok
        assert detached.data["rangeClosed"] and detached.data["toSequence"] == 4
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_attachment_identity_shapes_are_rejected_before_source_observation(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        invalid_attach = await service.console_capture_attach(True, "request-a")
        invalid_key = await service.console_capture_attach("capture-a", 2)
        invalid_detach = await service.console_capture_detach(7)
        invalid_export = await service.console_capture_detach("attachment-a", export=1)
        invalid_continuation = await service.console_capture_detach("attachment-a", continuation_token=object())

        for result, field in (
            (invalid_attach, "sessionId"),
            (invalid_key, "requestKey"),
            (invalid_detach, "attachmentId"),
            (invalid_export, "export"),
            (invalid_continuation, "continuationToken"),
        ):
            assert not result.ok and result.error.code == "CAPTURE_ATTACHMENT_INVALID"
            assert result.error.detail["field"] == field
            assert result.error.detail["stopAttempted"] is False
        assert dispatcher.calls == []

    asyncio.run(run())


def test_capture_attachment_rejects_request_conflicts_and_enforces_active_limit(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        conflict = await service.console_capture_attach("capture-b", "request-a")
        assert attached.ok
        assert not conflict.ok and conflict.error.code == "CAPTURE_ATTACHMENT_REQUEST_CONFLICT"
        store = service.server.state
        for index in range(63):
            store.save_capture_attachment({
                "attachmentId": f"seed-{index}", "projectPath": store.project_path,
                "sessionId": "capture-a", "requestKey": f"seed-request-{index}",
                "fromSequence": 0, "detached": False,
            })
        limited = await service.console_capture_attach("capture-a", "request-limit")
        assert not limited.ok and limited.error.code == "CAPTURE_ATTACHMENT_LIMIT"

    asyncio.run(run())


def test_capture_attachment_capacity_is_per_source_not_global(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        store = service.server.state
        for index in range(64):
            store.save_capture_attachment({
                "attachmentId": f"other-{index}", "projectPath": store.project_path,
                "sessionId": "other-capture", "requestKey": f"other-request-{index}",
                "fromSequence": 0, "detached": False,
            })
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok

    asyncio.run(run())


def test_capture_attachment_exports_fixed_boundary_after_source_terminates(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    dispatcher.pages = [[{"sequence": 4, "message": "final"}]]
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        dispatcher.next_sequence = 5
        closed = await service.console_capture_detach(attached.data["attachmentId"])
        assert closed.ok and closed.data["toSequence"] == 5
        dispatcher.available = False
        exported = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert exported.ok and exported.data["exportComplete"] is True
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]
        assert len([call for call in dispatcher.calls if call[0] == "console.capture.status"]) == 2

    asyncio.run(run())


def test_capture_attachment_index_failure_does_not_confirm_attachment(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        store = service.server.state

        def fail_create(_state: dict):
            raise OSError("fixture attachment index unavailable")

        store.create_capture_attachment = fail_create
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert not attached.ok and attached.error.code == "CAPTURE_ATTACHMENT_PERSIST_FAILED"
        assert store.load_capture_attachments() == []
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_detach_index_failure_does_not_confirm_fixed_boundary(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        store = service.server.state
        original_save = store.save_capture_attachment

        def fail_detach(state: dict) -> None:
            if state.get("detached"):
                raise OSError("fixture detach index unavailable")
            original_save(state)

        store.save_capture_attachment = fail_detach
        failed = await service.console_capture_detach(attached.data["attachmentId"])
        assert not failed.ok and failed.error.code == "CAPTURE_ATTACHMENT_DETACH_PERSIST_FAILED"
        persisted = store.load_capture_attachments()[0]
        assert persisted["detached"] is False
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_attachment_restart_keeps_source_unknown_until_exactly_verified(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    first = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await first.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        restarted = StatusDomainService()
        restarted.server = first.server
        restarted.dispatcher = dispatcher
        dispatcher.available = False
        recovered = await restarted.console_capture_attach("capture-a", "request-a")
        assert not recovered.ok and recovered.error.code == "CAPTURE_ATTACHMENT_SOURCE_UNKNOWN"
        stored = first.server.state.load_capture_attachments()[0]
        assert stored["sourceStatus"] == "unknown" and stored["recoveryRequired"] is True

    asyncio.run(run())


def test_capture_attachment_export_uses_fixed_range_and_reports_sequence_gaps(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    dispatcher.pages = [[
        {"sequence": 4, "message": "first"},
        {"sequence": 6, "message": "gap"},
    ]]
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        dispatcher.next_sequence = 8
        exported = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert exported.ok
        assert exported.data["rangeClosed"] and exported.data["toSequence"] == 8
        assert exported.data["exportComplete"] is True
        assert exported.data["exportIncomplete"] is True
        output = tmp_path / exported.data["exportPath"]
        assert output.is_file()
        assert [json.loads(line)["sequence"] for line in output.read_text(encoding="utf-8").splitlines()] == [4, 6]
        read = [payload for command, payload in dispatcher.calls if command == "console.capture.read"]
        assert read == [{
            "sessionId": "capture-a", "afterSequence": -1, "fromSequence": 4,
            "toSequence": 7, "count": 500, "includeStackTrace": True,
            "containsAll": False, "newestFirst": False,
        }]
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_attachment_restart_resumes_same_paged_export_candidate(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    dispatcher.pages = [
        [{"sequence": 4, "message": "first"}, {"sequence": 5, "message": "second"}],
        [{"sequence": 6, "message": "third"}, {"sequence": 7, "message": "fourth"}],
    ]
    first = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await first.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        dispatcher.next_sequence = 8
        first_page = await first.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert first_page.ok and first_page.data["exportComplete"] is False

        restarted = StatusDomainService()
        restarted.server = first.server
        restarted.dispatcher = dispatcher
        completed = await restarted.console_capture_detach(
            attached.data["attachmentId"],
            export=True,
            request_key="export-a",
            continuation_token=first_page.data["continuationToken"],
        )
        assert completed.ok and completed.data["exportComplete"] is True
        assert completed.data["fromSequence"] == 4 and completed.data["toSequence"] == 8
        output = tmp_path / completed.data["exportPath"]
        assert [json.loads(line)["sequence"] for line in output.read_text(encoding="utf-8").splitlines()] == [4, 5, 6, 7]
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_attachment_final_index_failure_recovers_same_candidate_without_duplicate_lines(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    dispatcher.pages = [[{"sequence": 4, "message": "only"}]]
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        dispatcher.next_sequence = 5
        store = service.server.state
        original_save = store.save_capture_attachment
        failed_once = True

        def fail_final_index(state: dict) -> None:
            nonlocal failed_once
            export = state.get("export") if isinstance(state.get("export"), dict) else {}
            if failed_once and export.get("complete"):
                failed_once = False
                raise OSError("fixture final index unavailable")
            original_save(state)

        store.save_capture_attachment = fail_final_index
        failed = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert not failed.ok and failed.error.code == "CAPTURE_ATTACHMENT_EXPORT_INDEX_PERSIST_FAILED"
        assert failed.error.detail["exportComplete"] is False

        recovered = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert recovered.ok and recovered.data["exportComplete"] is True
        output = tmp_path / recovered.data["exportPath"]
        assert [json.loads(line)["sequence"] for line in output.read_text(encoding="utf-8").splitlines()] == [4]

    asyncio.run(run())


def test_capture_start_response_loss_does_not_replay_start(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")

    async def response_lost(request_id: str, command: str, payload: dict, timeout_ms=None):
        dispatcher.calls.append((command, payload))
        if command == "console.capture.start":
            return fail(request_id, "COMMAND_TIMEOUT", "response lost")
        raise AssertionError(f"unexpected command: {command}")

    dispatcher.call = response_lost
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        first = await service.console_capture_start(title="fixture", request_key="start-a")
        repeated = await service.console_capture_start(title="fixture", request_key="start-a")
        assert not first.ok and first.error.code == "COMMAND_TIMEOUT"
        assert not repeated.ok and repeated.error.code == "CAPTURE_OWNERSHIP_RECOVERY_REQUIRED"
        assert len([call for call in dispatcher.calls if call[0] == "console.capture.start"]) == 1

    asyncio.run(run())


def test_capture_start_intent_persist_failure_never_starts_listener(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    def fail_intent(_state: dict) -> None:
        raise OSError("intent storage unavailable")

    service.server.state.save_capture_start_intent = fail_intent

    result = asyncio.run(service.console_capture_start(title="fixture", request_key="start-a"))

    assert not result.ok and result.error.code == "CAPTURE_START_PERSIST_FAILED"
    assert not [call for call in dispatcher.calls if call[0] == "console.capture.start"]


def test_capture_export_page_persist_failure_never_stops_source(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    service = _service(tmp_path, dispatcher)

    async def unavailable_read(request_id: str, command: str, payload: dict, timeout_ms=None):
        if command == "console.capture.read":
            dispatcher.calls.append((command, payload))
            return fail(request_id, "COMMAND_TIMEOUT", "read response lost")
        return await _CaptureDispatcher.call(dispatcher, request_id, command, payload, timeout_ms)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        original_save = service.server.state.save_capture_attachment

        def fail_page_state(state: dict) -> None:
            export = state.get("export") if isinstance(state.get("export"), dict) else {}
            if export.get("lastError"):
                raise OSError("page state unavailable")
            original_save(state)

        service.server.state.save_capture_attachment = fail_page_state
        dispatcher.call = unavailable_read
        failed = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert not failed.ok and failed.error.code == "CAPTURE_ATTACHMENT_EXPORT_PAGE_PERSIST_FAILED"
        assert failed.error.detail["stopAttempted"] is False
        assert not [call for call in dispatcher.calls if call[0] == "console.capture.stop"]

    asyncio.run(run())


def test_capture_attachment_rejects_out_of_range_or_repeated_export_sequences(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source", next_sequence=4)
    dispatcher.pages = [[{"sequence": 4, "message": "first"}, {"sequence": 4, "message": "repeated"}]]
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        attached = await service.console_capture_attach("capture-a", "request-a")
        assert attached.ok
        dispatcher.next_sequence = 5
        invalid = await service.console_capture_detach(
            attached.data["attachmentId"], export=True, request_key="export-a",
        )
        assert not invalid.ok and invalid.error.code == "CAPTURE_ATTACHMENT_EXPORT_SEQUENCE_INVALID"

    asyncio.run(run())


def test_capture_attachment_token_redaction_is_recursive() -> None:
    redacted = _redact_log_value({"ownerToken": "secret", "nested": [{"apiToken": "also-secret"}]})
    assert redacted == {"ownerToken": "[redacted]", "nested": [{"apiToken": "[redacted]"}]}


def test_capture_start_request_key_persists_intent_without_replaying_owner_token(tmp_path: Path) -> None:
    dispatcher = _CaptureDispatcher(tmp_path / "Log" / "UPilotConsole" / "source")
    service = _service(tmp_path, dispatcher)

    async def run() -> None:
        started = await service.console_capture_start(title="fixture", request_key="start-a")
        assert started.ok and started.data["ownerToken"]
        first_payload = dispatcher.calls[-1][1]
        assert first_payload["requestKey"] == "start-a"
        repeated = await service.console_capture_start(title="fixture", request_key="start-a")
        assert not repeated.ok and repeated.error.code == "CAPTURE_OWNERSHIP_RECOVERY_REQUIRED"
        assert len([item for item in dispatcher.calls if item[0] == "console.capture.start"]) == 1
        conflict = await service.console_capture_start(title="different", request_key="start-a")
        assert not conflict.ok and conflict.error.code == "CAPTURE_START_REQUEST_CONFLICT"

    asyncio.run(run())
