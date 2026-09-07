from __future__ import annotations

import asyncio
import json
import pytest
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY
from upilot_mcp.mcp_tools import resource_tools as _resource_tools  # noqa: F401
from upilot_mcp.mcp_tools import test_tools as _test_tools  # noqa: F401


class _Dispatcher:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict, **_kwargs):
        self.calls.append((name, payload))
        return ok(request_id, {"assetPath": payload.get("assetPath", ""), "messages": []})


def test_shader_diagnostics_forward_and_register_as_read_only() -> None:
    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    inspected = asyncio.run(service.shader_inspect("Assets/Test.shader"))
    checked = asyncio.run(service.shader_check_errors("Assets/Test.shader", include_warnings=False))

    assert inspected.ok and checked.ok
    assert service.dispatcher.calls == [
        ("shader.inspect", {"assetPath": "Assets/Test.shader"}),
        ("shader.checkErrors", {"assetPath": "Assets/Test.shader", "includeWarnings": False}),
    ]
    for name in ("unity_shader_inspect", "unity_shader_check_errors"):
        descriptor = REGISTRY.resolve(name)
        assert descriptor is not None
        assert descriptor.destructive is False
        assert descriptor.idempotent is True


def _operation_state(tmp_path: Path) -> dict:
    return {
        "operationId": "op-compact", "displayName": "compact", "status": "Running", "phase": "Work",
        "error": "", "detail": "", "progress": 0.5, "failureSignature": "", "startedAt": 1,
        "updatedAt": 2, "endedAt": 0, "timeoutSec": 30, "pollIntervalSec": 1,
        "lastStatusData": {"metrics": {"fps": 60}, "domain": {"huge": "x" * 10000}, "raw": "y" * 10000},
        "artifacts": {"report": {"path": str(tmp_path / "report.txt"), "tail": "z" * 5000}},
        "artifactErrors": [], "consoleCapture": {"sessionId": "cap", "stopped": False, "read": {"logs": "q" * 10000}},
        "timing": {}, "jobSpec": {},
    }


def test_operation_summary_is_compact_and_full_is_available(tmp_path: Path) -> None:
    service = TaskDomainService.__new__(TaskDomainService)
    state = _operation_state(tmp_path)
    summary = service._public_operation_state(state, "summary", 256, False)
    full = service._public_operation_state(state, "full", 20000, False)

    assert summary["responseDetailLevel"] == "summary"
    assert "lastStatusData" not in summary and "domain" not in summary
    assert summary["consoleCapture"] == {"sessionId": "cap", "stopped": False}
    assert summary["artifacts"]["report"]["tail"].endswith("chars]")
    assert full["lastStatusData"]["domain"]["huge"] == "x" * 10000
    assert full["responseBytes"] > summary["responseBytes"] * 2


class _AcceptanceService(TestDomainService):
    def __init__(self, expected_project: Path) -> None:
        self.expected_project = expected_project
        self.status_calls = 0

    async def mcp_status(self, **_kwargs):
        return ok("status", {"connected": True, "serverReady": True, "paths": {"unityProjectAbsolute": str(self.expected_project)}})

    async def ensure_ready(self, **_kwargs): return ok("ready", {"ready": True})
    async def console_capture_list(self, **_kwargs): return ok("captures", {"sessions": [{"sessionId": "old", "active": True}]})
    async def console_capture_stop(self, **_kwargs): return ok("stop", {"stopped": True})
    async def safe_compile_and_wait(self, **_kwargs): return ok("compile", {"status": "success", "errorsVerified": True, "errorTotal": 0})
    async def test_list(self, **_kwargs): return ok("list", {"tests": ["UPilot.Test"]})
    async def test_run(self, **_kwargs): return ok("run", {"status": "started", "runGuid": "run-1"})
    async def test_results(self, run_guid=""):
        self.status_calls += 1
        assert run_guid == "run-1"
        return ok("test-status", {
            "status": "completed", "runGuid": run_guid, "resultAuthoritative": True,
            "cleanupPending": False, "cleanupSucceeded": True, "cleanupStatus": "completed",
            "cleanupErrors": [], "unresolvedResources": [],
            "total": 1, "passed": 1, "failed": 0, "noTests": False,
        })
    async def compile_errors(self, *_args, **_kwargs): return ok("errors", {"total": 0, "errors": []})
    async def console_search_logs(self, **_kwargs): return ok("console", {"logs": []})


def test_acceptance_stops_capture_and_checks_correlated_clean_result() -> None:
    expected = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()
    service = _AcceptanceService(expected)
    result = asyncio.run(service.upilot_acceptance_run(timeout_sec=10, write_artifact=False))

    assert result.ok and result.data["acceptancePassed"] is True
    assert "failureCode" not in result.data
    assert "failureMessage" not in result.data
    assert result.data["stoppedConsoleCaptures"][0]["sessionId"] == "old"
    assert result.data["discoveredTestCount"] == 1
    descriptor = REGISTRY.resolve("unity_upilot_acceptance_run")
    assert descriptor is not None and descriptor.idempotent is False


@pytest.mark.parametrize("change", [
    {"cleanupSucceeded": False, "cleanupStatus": "failed", "cleanupErrors": ["callback-unregister"]},
    {"cleanupSucceeded": None},
    {"cleanupPending": None},
    {"cleanupErrors": None},
    {"unresolvedResources": ["callback"]},
    {"resultAuthoritative": False},
    {"runGuid": "another-run"},
    {"total": 0},
    {"failed": None},
])
def test_acceptance_rejects_failed_or_missing_evidence(change) -> None:
    expected = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()
    service = _AcceptanceService(expected)
    original = service.test_results

    async def altered(run_guid=""):
        response = await original(run_guid)
        response.data.update(change)
        return response

    service.test_results = altered
    result = asyncio.run(service.upilot_acceptance_run(timeout_sec=10, write_artifact=False))
    assert not result.ok
    assert result.error.detail["acceptancePassed"] is False
    assert result.error.detail["runGuid"] == "run-1"
    observation = result.error.detail["steps"]["testStatus"]
    observed = observation["data"] or (observation["error"] or {}).get("detail", {})
    assert observed.items() >= change.items()


@pytest.mark.parametrize("compile_data", [
    {"status": "failed", "errorsVerified": True, "errorTotal": 1},
    {"status": "completed", "errorTotal": 0},
    {"status": "compiling", "errorsVerified": True, "errorTotal": 0},
])
def test_acceptance_does_not_treat_compile_transport_success_as_acceptance(compile_data) -> None:
    expected = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()
    service = _AcceptanceService(expected)

    async def compile_result(**_kwargs):
        return ok("compile", compile_data)

    service.safe_compile_and_wait = compile_result
    result = asyncio.run(service.upilot_acceptance_run(timeout_sec=10, write_artifact=False))
    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_COMPILE_FAILED"
    assert service.status_calls == 0
