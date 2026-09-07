from __future__ import annotations

import asyncio
import json
from types import SimpleNamespace

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.tool_registry import REGISTRY


class _RecordingDispatcher:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict]] = []

    async def call(self, request_id: str, command: str, payload: dict):
        self.calls.append((command, payload))
        return ok(request_id, payload)


def _service() -> tuple[StatusDomainService, _RecordingDispatcher]:
    service = StatusDomainService()
    dispatcher = _RecordingDispatcher()
    service.dispatcher = dispatcher
    return service, dispatcher


def test_console_capture_facade_maps_start_payload() -> None:
    service, dispatcher = _service()

    asyncio.run(
        service.console_capture_start(
            title="战斗验收",
            path="Log/custom",
            include_stack_trace=False,
            exclude_upilot=False,
            clear_unity_console=True,
            flush_interval_ms=50,
            max_file_bytes=10,
        )
    )

    assert dispatcher.calls == [
        (
            "console.capture.start",
            {
                "title": "战斗验收",
                "path": "Log/custom",
                "includeStackTrace": False,
                "excludeUPilot": False,
                "clearUnityConsole": True,
                "flushIntervalMs": 100,
                "maxFileBytes": 1024 * 1024,
                "allowOutsideProject": False,
            },
        )
    ]


def test_console_capture_facade_maps_read_and_cleanup_payloads() -> None:
    service, dispatcher = _service()

    asyncio.run(
        service.console_capture_read(
            session_id="console-1",
            after_sequence=12,
            from_sequence=20,
            to_sequence=200,
            count=99999,
            log_type="Error",
            include_stack_trace=False,
            contains=["BattleProcess", "LastError"],
            contains_all=True,
            regex="Battle(Process|State)",
            continuation_token="cursor-1",
        )
    )
    asyncio.run(
        service.console_capture_cleanup(
            older_than_days=-1,
            keep_latest=-1,
            dry_run=False,
            confirm_token="token-1",
        )
    )

    assert dispatcher.calls[0] == (
        "console.capture.read",
        {
            "sessionId": "console-1",
            "afterSequence": 12,
            "fromSequence": 20,
            "toSequence": 200,
            "count": 5000,
            "includeStackTrace": False,
            "containsAll": True,
            "newestFirst": False,
            "logType": "Error",
            "contains": ["BattleProcess", "LastError"],
            "regex": "Battle(Process|State)",
            "continuationToken": "cursor-1",
        },
    )
    assert dispatcher.calls[1] == (
        "console.capture.cleanup",
        {
            "olderThanDays": 0,
            "keepLatest": 0,
            "dryRun": False,
            "confirmToken": "token-1",
        },
    )


def test_console_capture_public_tools_are_registered_with_safety_metadata() -> None:
    from upilot_mcp.mcp_tools import status_tools  # noqa: F401

    expected = {
        "unity_console_capture_start",
        "unity_console_capture_status",
        "unity_console_capture_read",
        "unity_console_capture_stop",
        "unity_console_capture_list",
        "unity_console_capture_cleanup",
    }
    assert expected.issubset({item.name for item in REGISTRY.list()})

    start = REGISTRY.resolve("unity_console_capture_start")
    cleanup = REGISTRY.resolve("unity_console_capture_cleanup")
    assert start is not None and start.idempotent is False and start.destructive is False
    assert cleanup is not None and cleanup.idempotent is False and cleanup.destructive is True


def test_console_capture_stop_recovers_completed_persisted_terminal(tmp_path) -> None:
    session_id = "console-stop-recovered"
    directory = tmp_path / "Log" / "UPilotConsole" / "capture-1"
    directory.mkdir(parents=True)
    summary_path = directory / "summary.json"
    summary_path.write_text('{"ok":true}', encoding="utf-8")
    (directory / "session.json").write_text(
        json.dumps(
            {
                "sessionId": session_id,
                "active": False,
                "finishedAtUtcMs": 123,
                "summaryPath": str(summary_path),
                "sha256": "abc123",
            }
        ),
        encoding="utf-8",
    )

    class _TimedOutDispatcher:
        async def call(self, request_id: str, command: str, payload: dict):
            assert command == "console.capture.stop"
            assert payload == {"sessionId": session_id}
            return fail(request_id, "COMMAND_TIMEOUT", "命令超时")

    service = StatusDomainService()
    service.dispatcher = _TimedOutDispatcher()
    service.server = SimpleNamespace(
        session_manager=SimpleNamespace(
            active=SimpleNamespace(project_path=str(tmp_path))
        )
    )

    result = asyncio.run(service.console_capture_stop(session_id=session_id))

    assert result.ok
    assert result.data["terminal"] is True
    assert result.data["active"] is False
    assert result.data["completionSource"] == "persistedManifest"
    assert result.data["summaryPath"] == str(summary_path.resolve())
    assert result.data["summaryBytes"] == summary_path.stat().st_size
    assert result.data["sha256"] == "abc123"
    assert result.data["contextDiagnostic"]["code"] == "COMMAND_TIMEOUT"


def test_console_capture_stop_marks_normal_completed_response_terminal() -> None:
    class _CompletedDispatcher:
        async def call(self, request_id: str, command: str, payload: dict):
            return ok(
                request_id,
                {"ok": True, "session": {"sessionId": "done", "active": False}},
            )

    service = StatusDomainService()
    service.dispatcher = _CompletedDispatcher()

    result = asyncio.run(service.console_capture_stop(session_id="done"))

    assert result.ok
    assert result.data["terminal"] is True
    assert result.data["active"] is False
    assert result.data["completionSource"] == "unity"


def test_console_capture_stop_does_not_recover_incomplete_manifest(tmp_path) -> None:
    session_id = "console-stop-incomplete"
    directory = tmp_path / "Log" / "UPilotConsole" / "capture-1"
    directory.mkdir(parents=True)
    (directory / "session.json").write_text(
        json.dumps({"sessionId": session_id, "active": False}),
        encoding="utf-8",
    )

    class _TimedOutDispatcher:
        async def call(self, request_id: str, command: str, payload: dict):
            return fail(request_id, "COMMAND_TIMEOUT", "命令超时")

    service = StatusDomainService()
    service.dispatcher = _TimedOutDispatcher()
    service.server = SimpleNamespace(
        session_manager=SimpleNamespace(
            active=SimpleNamespace(project_path=str(tmp_path))
        )
    )

    result = asyncio.run(service.console_capture_stop(session_id=session_id))

    assert not result.ok
    assert result.error and result.error.code == "COMMAND_TIMEOUT"
