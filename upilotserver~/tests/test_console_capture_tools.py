from __future__ import annotations

import asyncio

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import ok
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
            count=99999,
            log_type="Error",
            include_stack_trace=False,
            contains=["BattleProcess", "LastError"],
            contains_all=True,
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
            "count": 5000,
            "includeStackTrace": False,
            "containsAll": True,
            "newestFirst": False,
            "logType": "Error",
            "contains": ["BattleProcess", "LastError"],
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
