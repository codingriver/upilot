from __future__ import annotations

import asyncio
from pathlib import Path
import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import fail, ok


@pytest.fixture(autouse=True)
def _acceptance_write_grant(monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)


class _AcceptanceCaptureService(TestDomainService):
    def __init__(self, *, list_result):
        self.list_result = list_result
        self.capture_stop_calls = []
        self.compile_calls = 0
        self.runner_start_calls = 0

    async def mcp_status(self, **_kwargs):
        expected = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()
        return ok("status", {
            "connected": True,
            "serverReady": True,
            "paths": {"unityProjectAbsolute": str(expected)},
        })

    async def ensure_ready(self, **_kwargs):
        return ok("ready", {"ready": True})

    async def console_capture_list(self, **_kwargs):
        return self.list_result

    async def console_capture_stop(self, **kwargs):
        self.capture_stop_calls.append(kwargs)
        return ok("stop", {"active": False})

    async def safe_compile_and_wait(self, **_kwargs):
        self.compile_calls += 1
        return ok("compile", {"status": "success", "errorsVerified": True, "errorTotal": 0})

    async def test_run(self, **_kwargs):
        self.runner_start_calls += 1
        return ok("run", {"runGuid": "should-not-start"})


def test_acceptance_active_unknown_capture_never_force_stops_even_when_scopes_enabled(monkeypatch):
    # These scopes authorize the dedicated exact-session tool; listing a capture
    # remains insufficient authority for package acceptance to stop it.
    monkeypatch.setattr(
        CONFIG,
        "automation_authorization_scopes",
        ("captureAcceptanceClearance", "captureForceStop"),
    )
    service = _AcceptanceCaptureService(list_result=ok("captures", {
        "sessions": [{"sessionId": "capture-other", "ownerId": "other-task", "active": True}],
    }))

    result = asyncio.run(service._execute_upilot_acceptance_run(timeout_sec=10, write_artifact=False))

    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_CAPTURE_OWNERSHIP_REQUIRED"
    assert result.error.detail["activeConsoleCaptures"] == [
        {"sessionId": "capture-other", "ownerId": "other-task"},
    ]
    assert result.error.detail["captureDisposition"]["automaticForceStop"] is False
    assert result.error.detail["runnerStartAttempted"] is False
    assert service.capture_stop_calls == []
    assert service.compile_calls == 0
    assert service.runner_start_calls == 0


def test_acceptance_unknown_capture_state_does_not_start_runner_or_stop_any_capture():
    service = _AcceptanceCaptureService(list_result=fail(
        "captures", "CAPTURE_LIST_FAILED", "Capture state is unavailable.",
    ))

    result = asyncio.run(service._execute_upilot_acceptance_run(timeout_sec=10, write_artifact=False))

    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_CAPTURE_STATE_UNKNOWN"
    assert result.error.detail["runnerStartAttempted"] is False
    assert service.capture_stop_calls == []
    assert service.compile_calls == 0
    assert service.runner_start_calls == 0
