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

    assert len(dispatcher.calls) == 1
    command, payload = dispatcher.calls[0]
    assert command == "console.capture.start"
    assert {key: value for key, value in payload.items() if key not in {"ownerId", "ownerToken"}} == {
        "title": "战斗验收", "path": "Log/custom", "includeStackTrace": False,
        "excludeUPilot": False, "clearUnityConsole": True, "flushIntervalMs": 100,
        "maxFileBytes": 1024 * 1024, "allowOutsideProject": False,
    }
    assert payload["ownerId"].startswith("req-")
    assert len(payload["ownerToken"]) >= 43


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
        "unity_console_capture_attach",
        "unity_console_capture_detach",
        "unity_console_capture_list",
        "unity_console_capture_cleanup",
    }
    assert expected.issubset({item.name for item in REGISTRY.list()})

    start = REGISTRY.resolve("unity_console_capture_start")
    stop = REGISTRY.resolve("unity_console_capture_stop")
    cleanup = REGISTRY.resolve("unity_console_capture_cleanup")
    assert start is not None and start.idempotent is False and start.destructive is False
    assert stop is not None and stop.idempotent is False and stop.destructive is True
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
            raise AssertionError("a persisted terminal must not be stopped again")

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
    assert result.data["contextDiagnostic"]["code"] == "CAPTURE_STOP_NOT_DISPATCHED"


def test_console_capture_stop_marks_normal_completed_response_terminal() -> None:
    class _CompletedDispatcher:
        async def call(self, request_id: str, command: str, payload: dict):
            return ok(
                request_id,
                {"ok": True, "session": {"sessionId": "done", "active": False}},
            )

    service = StatusDomainService()
    service.dispatcher = _CompletedDispatcher()

    result = asyncio.run(service.console_capture_stop(session_id="done", owner_token="fixture-owner-token"))

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

    calls: list[tuple[str, dict]] = []

    class _TimedOutDispatcher:
        async def call(self, request_id: str, command: str, payload: dict):
            calls.append((command, payload))
            return fail(request_id, "COMMAND_TIMEOUT", "命令超时")

    service = StatusDomainService()
    service.dispatcher = _TimedOutDispatcher()
    service.server = SimpleNamespace(
        session_manager=SimpleNamespace(
            active=SimpleNamespace(project_path=str(tmp_path))
        )
    )

    result = asyncio.run(service.console_capture_stop(session_id=session_id, owner_token="fixture-owner-token"))

    assert not result.ok
    assert result.error and result.error.code == "COMMAND_TIMEOUT"
    assert calls == [("console.capture.stop", {"sessionId": session_id, "ownerToken": "fixture-owner-token"})]


def test_console_capture_stop_rejects_missing_ownership_or_force_session_without_dispatch() -> None:
    service, dispatcher = _service()

    missing_token = asyncio.run(service.console_capture_stop(session_id="capture-a"))
    missing_session = asyncio.run(service.console_capture_stop(owner_token="fixture-owner-token"))
    missing_force_session = asyncio.run(service.console_capture_stop(force_stop=True))

    assert not missing_token.ok and missing_token.error.code == "CAPTURE_OWNERSHIP_REQUIRED"
    assert not missing_session.ok and missing_session.error.code == "CAPTURE_OWNERSHIP_REQUIRED"
    assert not missing_force_session.ok and missing_force_session.error.code == "CAPTURE_FORCE_STOP_SESSION_REQUIRED"
    assert all(detail.get("stopAttempted") is False for detail in (
        missing_token.error.detail, missing_session.error.detail, missing_force_session.error.detail,
    ))
    assert dispatcher.calls == []


def test_console_capture_direct_invalid_id_boolean_and_integer_inputs_never_dispatch() -> None:
    service, dispatcher = _service()

    rejected = [
        asyncio.run(service.console_capture_status(True)),
        asyncio.run(service.console_capture_read(session_id=1)),
        asyncio.run(service.console_capture_read(after_sequence=True)),
        asyncio.run(service.console_capture_list(count=True)),
        asyncio.run(service.console_capture_list(include_active=1)),
        asyncio.run(service.console_capture_stop(session_id=1, force_stop=True)),
        asyncio.run(service.console_capture_stop(session_id="capture-a", force_stop="true")),
    ]

    assert all(not result.ok and result.error.code == "INVALID_PAYLOAD" for result in rejected)
    assert dispatcher.calls == []


def test_console_capture_force_stop_observes_persisted_terminal_without_second_stop(tmp_path) -> None:
    session_id = "already-ended"
    directory = tmp_path / "Log" / "UPilotConsole" / "capture-1"
    directory.mkdir(parents=True)
    (directory / "summary.json").write_text('{"ok":true}', encoding="utf-8")
    (directory / "session.json").write_text(json.dumps({
        "sessionId": session_id, "active": False, "finishedAtUtcMs": 123,
        "sha256": "fixture-hash", "ownerTokenSha256": "not-a-token",
    }), encoding="utf-8")

    service = StatusDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(tmp_path))))

    class _MustNotStop:
        async def call(self, *_args, **_kwargs):
            raise AssertionError("an already terminal force-stop is observation only")

    service.dispatcher = _MustNotStop()
    result = asyncio.run(service.console_capture_stop(session_id=session_id, force_stop=True))

    assert result.ok and result.data["terminal"] is True
    assert result.data["completionSource"] == "persistedManifest"
