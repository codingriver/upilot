"""Targeted reliability regressions; deliberately not part of this change's run evidence."""

import json

from upilot_mcp.domain.task_service import TaskDomainService


def test_operation_summary_whitelists_deep_artifacts_and_bounds_utf8():
    service = TaskDomainService()
    state = {
        "operationId": "op-original", "startedAt": 1, "timeoutSec": 60, "status": "Failed",
        "error": "业务首错" * 1000, "cleanupError": "cleanup" * 1000,
        "businessResult": {"status": "Failed", "failureSignature": "BUSINESS_FAILURE"},
        "artifacts": {f"attachment-{index}": {"path": f"file-{index}", "sha256": "a" * 64,
                                         "tail": "不要返回" * 10000} for index in range(30)},
        "lastStatusData": {"domain": {"secret": "勿内联" * 10000}},
    }
    summary = service._public_operation_state(state, "summary", include_raw_state=True)
    assert summary["operationId"] == state["operationId"]
    assert summary["rawStateOmitted"] and "rawState" not in summary
    assert summary["artifactsTotal"] == 30 and len(summary["artifacts"]) <= 8
    assert summary["artifactsReturned"] == len(summary["artifacts"])
    assert summary["artifacts"]
    assert all("tail" not in artifact for artifact in summary["artifacts"].values())
    assert len(json.dumps(summary, ensure_ascii=False, separators=(",", ":")).encode()) <= 16384
    assert summary["responseBytes"] == TaskDomainService._summary_size(summary)


def test_acceptance_summary_retains_error_and_unknown_legacy_history():
    state = {"taskId": "task-original", "durable": True, "status": "failed", "terminal": True,
             "runGuid": "run-original", "error": {"code": "FIRST", "message": "故障" * 3000},
             "cleanupError": "cleanup failure", "recoveryDiagnostics": [
                 {"code": "RECOVERY", "message": "observe"}] * 20,
             "acceptanceReport": {"failureCode": "FIRST", "sourceUnchanged": False,
                                  "artifact": {"path": "summary.json", "bytes": 10, "sha256": "a" * 64}}}
    summary = TaskDomainService._public_task_state(state)
    assert summary["error"]["code"] == "FIRST"
    assert summary["recoveryDiagnosticsHistoryComplete"] is False
    assert summary["recoveryDiagnosticsDroppedCount"] is None
    assert "acceptanceReport" not in summary
    assert TaskDomainService._summary_size(summary) <= 16384
    assert TaskDomainService._public_task_state(state, "full")["acceptanceReport"] == state["acceptanceReport"]


def test_summary_keeps_required_acceptance_fields_and_handles_long_keys():
    state = {"taskId": "task", "durable": True, "status": "failed", "terminal": True,
             "acceptanceReport": {"acceptancePassed": False, "runGuid": "run", "cleanupVerified": False,
                                  "testIdentityVerified": True, "sourceUnchanged": False,
                                  "failureCode": "FIRST", "failureMessage": "first cause",
                                  "artifact": {"path": "report.json", "sha256": "a" * 64},
                                  "deadlineAt": 123}}
    summary = TaskDomainService._public_task_state(state)
    assert summary["result"]["result"]["artifact"]["path"] == "report.json"
    assert summary["result"]["result"]["failureCode"] == "FIRST"
    assert summary["responseBytes"] == TaskDomainService._summary_size(summary)
    summary = TaskDomainService._finalize_summary({"operationId": "op", "artifacts": {
        "x" * 50000: {"path": "report.json", "bytes": 4}}})
    assert summary["responseBytes"] == TaskDomainService._summary_size(summary)
    assert summary["truncatedFields"]


def test_final_collect_projection_cannot_keep_unbounded_attachment_values():
    payload = {"operationId": "op", "artifacts": {f"item-{index}": {
        "kind": "metadata", "value": "汉" * 100000} for index in range(20)},
        "artifactErrors": [{"error": "missing"}] * 100,
        "operation": {"rawStateOmitted": True}}
    summary = TaskDomainService._finalize_summary(payload)
    assert TaskDomainService._summary_size(summary) <= 16384
    assert summary["operationId"] == "op"
    assert summary["truncatedFields"]
