"""V07 isolated finalization: no Runner starts, service restart or shared artifacts."""
import asyncio
import hashlib
import json
from pathlib import Path

import pytest

from upilot_mcp.domain import test_service as module
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.test_job_context import TEST_JOB_CONTEXT


class _FinalizationService(TestDomainService):
    def __init__(self):
        self.calls = []
        self.compile_result = ok("compile", {"total": 0})
        self.console_result = ok("console", {"logs": []})

    async def compile_errors(self):
        self.calls.append("compile.errors")
        return self.compile_result

    async def console_search_logs(self, **_kwargs):
        self.calls.append("console.search")
        return self.console_result

    async def test_run(self, **_kwargs):
        pytest.fail("Finalization must never start/replay a Runner")


def _report(tmp_path, *, write_artifact=False):
    return {
        "requestId": "req-own", "startedAt": 100, "deadlineAt": 1000,
        "expectedProject": str(tmp_path / "Tests~" / "UPilotTest"),
        "runGuid": "run-own", "requireTests": True, "steps": {},
        "writeArtifact": write_artifact, "sourceIdentity": {"identity": "before"},
        "importInputsBefore": {"identity": "before"},
        "failureCode": "OLD_OBSERVATION", "failureMessage": "old transport uncertainty",
        "nextAction": "old action",
    }


def _completed():
    return {
        "runGuid": "run-own", "status": "completed", "resultAuthoritative": True,
        "cleanupPending": False, "cleanupSucceeded": True, "cleanupStatus": "completed",
        "cleanupErrors": [], "unresolvedResources": [], "total": 1, "failed": 0,
        "noTests": False,
    }


@pytest.mark.parametrize("gate", [
    "none", "runGuid", "resultAuthoritative", "cleanupPending", "cleanupSucceeded",
    "cleanupStatus", "cleanupErrors", "unresolvedResources", "persistenceError",
    "status", "failed", "total", "noTests", "compile_error", "compile_unavailable",
    "console_unavailable", "source_changed", "import_changed", "cancelRequested", "timedOut",
])
def test_every_finalization_gate_preserves_original_run_and_rejects_unknown(tmp_path, monkeypatch, gate):
    report, data, service = _report(tmp_path), _completed(), _FinalizationService()
    changes = {
        "runGuid": "run-other", "resultAuthoritative": None, "cleanupPending": True,
        "cleanupSucceeded": None, "cleanupStatus": "failed", "cleanupErrors": ["unregister"],
        "unresolvedResources": ["callback"], "persistenceError": "commit failed",
        "status": "aborted", "failed": 1, "total": 0, "noTests": True,
    }
    if gate in changes:
        data[gate] = changes[gate]
    if gate == "compile_error":
        service.compile_result = ok("compile", {"total": 1})
    elif gate == "compile_unavailable":
        service.compile_result = fail("compile", "UNAVAILABLE", "unknown compile facts")
    elif gate == "console_unavailable":
        service.console_result = fail("console", "UNAVAILABLE", "unknown Console evidence")
    monkeypatch.setattr(module, "source_identity", lambda _root: {
        "identity": "after" if gate == "source_changed" else "before"})
    monkeypatch.setattr(module, "acceptance_import_inputs", lambda *_args: {"identity": "after"})
    monkeypatch.setattr(module, "diff_acceptance_import_inputs", lambda *_args: {
        "changed": [{"path": "Assets/Probe.asset.meta"}] if gate == "import_changed" else [],
        "changeCount": 1 if gate == "import_changed" else 0,
    })
    job = {gate: True} if gate in ("cancelRequested", "timedOut") else {}
    token = TEST_JOB_CONTEXT.set((job, lambda _state: None))
    try:
        result = asyncio.run(service._complete_acceptance_report(report, final_status=ok("result", data)))
    finally:
        TEST_JOB_CONTEXT.reset(token)
    projected = result.data if result.ok else result.error.detail
    assert result.ok is (gate == "none")
    assert projected["acceptancePassed"] is (gate == "none")
    assert projected["runGuid"] == "run-own"
    assert projected["deadlineAt"] == 1000
    assert service.calls == ["compile.errors", "console.search"]
    if gate == "none":
        assert not {"failureCode", "failureMessage", "nextAction"}.intersection(projected)
        assert projected["recoveryDiagnostics"][-1]["code"] == "OLD_OBSERVATION"
    else:
        assert projected["failureCode"] == "UPILOT_ACCEPTANCE_FAILED"
    if gate == "source_changed":
        assert projected["sourceChanges"]["attribution"] == "unknown/concurrent-unattributed"
    if gate == "import_changed":
        assert projected["importInputChanges"]["changed"][0]["attribution"] == "unknown/concurrent-unattributed"


def test_success_archives_bounded_old_errors_and_atomically_receipts_real_file(tmp_path):
    report = _report(tmp_path, write_artifact=True)
    report["recoveryDiagnostics"] = [{"code": str(n)} for n in range(12)]
    result = TestDomainService._finish_acceptance_report(report, True)
    assert result.ok and result.data["acceptancePassed"] is True
    assert len(result.data["recoveryDiagnostics"]) == 8
    assert result.data["recoveryDiagnostics"][-1]["code"] == "OLD_OBSERVATION"
    assert not {"failureCode", "failureMessage", "nextAction"}.intersection(result.data)
    artifact = result.data["artifact"]
    path = Path(artifact["path"])
    raw = path.read_bytes()
    assert artifact["bytes"] == len(raw)
    assert artifact["sha256"] == hashlib.sha256(raw).hexdigest()
    recorded = json.loads(raw)
    assert recorded["acceptancePassed"] is True and recorded["artifactWritten"] is True
    assert recorded["runGuid"] == "run-own"
    assert "artifact" not in recorded  # External receipt, not a self-hash in the file.
    assert not path.with_suffix(".json.tmp").exists()


@pytest.mark.parametrize("failure_stage", ["write", "replace"])
@pytest.mark.parametrize("business_failed", [False, True])
def test_artifact_failure_preserves_first_error_previous_report_and_unknown_files(
        tmp_path, monkeypatch, failure_stage, business_failed):
    report = _report(tmp_path, write_artifact=True)
    folder = Path(report["expectedProject"]) / "Log" / "UPilotAcceptance" / "100_req-own"
    folder.mkdir(parents=True)
    artifact = folder / "summary.json"
    artifact.write_bytes(b"previous complete report")
    unknown = folder / "foreign.json.tmp"
    unknown.write_bytes(b"not owned by finalizer")
    original_write, original_replace = Path.write_bytes, module.os.replace

    def write(path, content):
        if failure_stage == "write" and path == artifact.with_suffix(".json.tmp"):
            raise PermissionError("owned write denied")
        return original_write(path, content)

    def replace(source, destination):
        if failure_stage == "replace" and Path(destination) == artifact:
            raise PermissionError("owned replacement denied")
        return original_replace(source, destination)

    monkeypatch.setattr(Path, "write_bytes", write)
    monkeypatch.setattr(module.os, "replace", replace)
    result = TestDomainService._finish_acceptance_report(
        report, not business_failed, "FIRST_BUSINESS_ERROR" if business_failed else "",
        "original failure" if business_failed else "")
    assert not result.ok
    detail = result.error.detail
    assert result.error.code == ("FIRST_BUSINESS_ERROR" if business_failed else "UPILOT_ACCEPTANCE_ARTIFACT_FAILED")
    if business_failed:
        assert result.error.message == "original failure"
    assert detail["acceptancePassed"] is False and detail["artifactWritten"] is False
    assert "artifactError" in detail and "artifact" not in detail
    assert artifact.read_bytes() == b"previous complete report"
    assert unknown.read_bytes() == b"not owned by finalizer"
    assert not artifact.with_suffix(".json.tmp").exists()
