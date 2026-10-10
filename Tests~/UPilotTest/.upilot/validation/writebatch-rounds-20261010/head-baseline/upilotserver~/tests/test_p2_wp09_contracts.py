import asyncio
import json
from pathlib import Path

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.responses import ok


class _OwnerEchoDispatcher:
    def __init__(self):
        self.calls = []

    async def call(self, request_id, command, payload, **_kwargs):
        self.calls.append((command, payload))
        if command == "console.capture.start":
            return ok(request_id, {
                "session": {
                    "sessionId": "capture-owned", "active": True,
                    "ownerToken": payload["ownerToken"],
                    "ownerTokenSha256": "owner-hash",
                },
                "ownerToken": payload["ownerToken"],
            })
        if command == "console.capture.stop":
            return ok(request_id, {
                "session": {
                    "sessionId": payload["sessionId"], "active": False,
                    "ownerToken": payload.get("ownerToken", "bridge-secret"),
                    "ownerTokenSha256": "owner-hash",
                },
            })
        if command == "console.capture.read":
            return ok(request_id, {
                "entries": [],
                "metadata": {"owner_token": "test-only-secret", "owner-hash": "test-only-hash"},
            })
        if command in {"console.capture.status", "console.capture.list"}:
            return ok(request_id, {
                "sessions": [{"sessionId": "capture-owned", "ownerToken": "bridge-secret", "ownerHash": "owner-hash"}],
                "ownerToken": "bridge-secret",
            })
        raise AssertionError(command)


def _contains(value, expected):
    return expected in json.dumps(value, ensure_ascii=False, default=str)


def test_manual_start_generates_its_own_token_and_ordinary_capture_responses_never_echo_owner_secrets():
    async def run():
        dispatcher = _OwnerEchoDispatcher()
        service = StatusDomainService()
        service.dispatcher = dispatcher

        started = await service.console_capture_start(owner_token="caller-chosen-secret")
        status = await service.console_capture_status("capture-owned")
        listed = await service.console_capture_list()
        read = await service.console_capture_read("capture-owned")
        stopped = await service.console_capture_stop("capture-owned", owner_token=started.data["ownerToken"])

        generated = started.data["ownerToken"]
        start_payload = dispatcher.calls[0][1]
        assert generated != "caller-chosen-secret"
        assert start_payload["ownerToken"] == generated
        assert started.data["ownerId"].startswith("req-")
        # The one permitted manual-start return is the generated token itself;
        # no echoed nested Bridge token/hash survives in any public response.
        assert started.data["session"] == {"sessionId": "capture-owned", "active": True}
        for response in (status, listed, read, stopped):
            assert response.ok
            assert not _contains(response.data, "bridge-secret")
            assert not _contains(response.data, "owner-hash")
            assert not _contains(response.data, "test-only-secret")
            assert not _contains(response.data, "test-only-hash")

    asyncio.run(run())


def test_operation_response_summaries_redact_owner_token_and_hash_before_persistence_or_public_state():
    summary = TaskDomainService._tool_response_summary(ok("capture", {
        "ownerToken": "secret", "ownerTokenSha256": "hash", "nested": {"ownerHash": "hash2", "owner_token": "alias", "owner-hash": "alias-hash"},
    }))

    assert summary["data"] == {
        "ownerToken": "[redacted]",
        "ownerTokenSha256": "[redacted]",
        "nested": {"ownerHash": "[redacted]", "owner_token": "[redacted]", "owner-hash": "[redacted]"},
    }
    assert not _contains(summary, "secret")
    assert not _contains(summary, "hash2")


def test_operation_full_status_output_redacts_normalized_capture_owner_fields():
    service = TaskDomainService()
    state = {
        "lastStatusData": {"owner_token": "test-only-secret", "nested": {"owner-hash": "test-only-hash"}},
        "consoleCapture": {},
        "artifacts": {},
        "artifactErrors": [],
        "timing": {},
        "milestones": [],
        "startedAt": 1,
    }

    public = service._public_operation_state(state, detail_level="full")

    assert public["lastStatusData"] == {
        "owner_token": "[redacted]",
        "nested": {"owner-hash": "[redacted]"},
    }


def test_unity_capture_owner_hash_comparison_is_fixed_time():
    source = (Path(__file__).resolve().parents[2] / "Editor" / "Core" / "UPilotConsoleCaptureService.cs").read_text(encoding="utf-8")
    start = source.index("private static bool OwnerTokenMatches")
    end = source.index("private static bool CaptureStartRequestMatches", start)
    owner_match = source[start:end]

    assert "TextHashesMatch(manifest.ownerTokenSha256, ComputeTextSha256(ownerToken))" in owner_match
    assert "CryptographicOperations.FixedTimeEquals(expected, actual)" in source


def test_force_stop_dispatches_once_only_for_an_explicit_active_project_session(tmp_path):
    capture_root = tmp_path / "Log" / "UPilotConsole" / "capture-fixture"
    capture_root.mkdir(parents=True)
    (capture_root / "session.json").write_text(json.dumps({
        "sessionId": "capture-active-fixture",
        "active": True,
    }), encoding="utf-8")

    class Dispatcher:
        def __init__(self):
            self.calls = []

        async def call(self, request_id, command, payload, **_kwargs):
            self.calls.append((command, payload))
            if command != "console.capture.stop":
                raise AssertionError(command)
            return ok(request_id, {"session": {"sessionId": payload["sessionId"], "active": False}})

    service = StatusDomainService()
    dispatcher = Dispatcher()
    service.dispatcher = dispatcher
    service.server = type("Server", (), {
        "session_manager": type("SessionManager", (), {
            "active": type("Session", (), {"project_path": str(tmp_path)})(),
        })(),
    })()

    stopped = asyncio.run(service.console_capture_stop(
        session_id="capture-active-fixture", force_stop=True,
    ))
    unknown = asyncio.run(service.console_capture_stop(
        session_id="capture-unknown-fixture", force_stop=True,
    ))
    different = asyncio.run(service.console_capture_stop(
        session_id="capture-different-fixture", force_stop=True,
    ))

    assert stopped.ok and stopped.data["terminal"] is True
    assert dispatcher.calls == [
        ("console.capture.stop", {"sessionId": "capture-active-fixture", "forceStop": True}),
    ]
    for rejected in (unknown, different):
        assert not rejected.ok and rejected.error.code == "CAPTURE_FORCE_STOP_SESSION_UNKNOWN"
        assert rejected.error.detail["stopAttempted"] is False
