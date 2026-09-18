from __future__ import annotations

import json
import os
from types import SimpleNamespace

from upilot_mcp.domain.status_service import StatusDomainService


def _service() -> StatusDomainService:
    return StatusDomainService.__new__(StatusDomainService)


def _write_record(project, **overrides):
    record = {
        "schemaVersion": 1,
        "operationId": "restart-123",
        "projectPath": str(project),
        "status": "succeeded",
        "phase": "completed",
        "oldProcessId": 10,
        "newProcessId": os.getpid(),
        "oldBridgeSessionId": "old-session",
        "newBridgeSessionId": "new-session",
        "healthVerified": True,
        "projectIdentityVerified": True,
        "bridgeVerified": True,
        "healthProjectPath": str(project),
        "requestedAtUtcMs": 1,
        "newProcessStartedAtUtcMs": 2,
        "healthVerifiedAtUtcMs": 3,
        "bridgeVerifiedAtUtcMs": 4,
        "endedAtUtcMs": 5,
    }
    record.update(overrides)
    path = project / "Library" / "UPilot" / "server-restart.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps(record), encoding="utf-8")
    return path


def test_restart_summary_requires_current_server_and_new_active_bridge_session(tmp_path):
    service = _service()
    _write_record(tmp_path)

    current = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert current["recordStatus"] == "current"
    assert current["current"] is True
    assert current["verifiedSucceeded"] is True
    assert current["operationId"] == "restart-123"
    assert current["identity"]["processMatches"] is True

    wrong_session = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="old-session")
    )
    assert wrong_session["status"] == "succeeded"
    assert wrong_session["verifiedSucceeded"] is True
    assert wrong_session["identity"]["bridgeWasReplaced"] is True
    assert wrong_session["identity"]["bridgeSessionMatches"] is False


def test_restart_summary_keeps_failed_exit_evidence_but_marks_other_server_historical(tmp_path):
    service = _service()
    _write_record(
        tmp_path,
        status="failed",
        phase="failed",
        newProcessId=os.getpid() + 100000,
        exitObserved=True,
        exitCode=17,
        errorCode="server_process_exited",
        error="failed",
        stderrTail="x" * 5000,
    )

    summary = service._read_server_restart_summary(tmp_path, None)

    assert summary["recordStatus"] == "historical"
    assert summary["current"] is False
    assert summary["status"] == "failed"
    assert summary["processExit"] == {"observed": True, "exitCode": 17}
    assert summary["errorCode"] == "server_process_exited"
    assert len(summary["stderrTail"]) == 4096


def test_restart_summary_rejects_project_mismatch_and_corrupt_record(tmp_path):
    service = _service()
    path = _write_record(tmp_path, projectPath=str(tmp_path / "other"))

    mismatch = service._read_server_restart_summary(
        tmp_path, SimpleNamespace(session_id="new-session")
    )
    assert mismatch["recordStatus"] == "project_mismatch"
    assert mismatch["verifiedSucceeded"] is False

    path.write_text("{broken", encoding="utf-8")
    corrupt = service._read_server_restart_summary(tmp_path, None)
    assert corrupt["recordStatus"] == "corrupt"
    assert corrupt["current"] is False
