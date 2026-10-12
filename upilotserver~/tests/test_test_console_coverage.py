"""D08: range completeness must not hide weaker reported phase evidence."""
import asyncio
from copy import deepcopy
import json
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.test_service import TestDomainService, _test_summary
from upilot_mcp.responses import ok
from upilot_mcp.state_store import StateStore


GAP = "console_cursor_clear_or_reload_not_detectable"


def phase(coverage="partial", reason=GAP, name="cleanup"):
    return {"phase": name, "coverage": coverage, "gapReason": reason,
            "source": "consoleDelta", "startSequence": 7, "endSequence": 9,
            "logs": []}


@pytest.mark.parametrize("endpoint,detail", [
    ("results", "summary"), ("results", "full"),
    ("status", "summary"), ("status", "full"), ("incremental", "full"),
])
def test_phase_gap_reaches_results_and_status_without_rewriting_history(tmp_path, endpoint, detail):
    state = StateStore()
    state.configure_project(str(tmp_path))
    raw = {"source": "consoleDelta", "coverage": "complete", "gapReason": "",
           "startSequence": 4, "endSequence": 10, "logs": [],
           "phases": [phase(name="testExecution"), phase()]}
    state.save_console_evidence("runGuid", "run-d08", raw)
    persisted = deepcopy(state.get_console_evidence("runGuid", "run-d08"))
    data = {"runGuid": "run-d08", "status": "completed", "outcomeStatus": "passed",
            "cleanupSucceeded": True, "total": 1, "passed": 1, "failed": 0,
            "consolePhases": deepcopy(raw["phases"]), "events": [], "results": [],
            "resultStreamVersion": 1, "lastDeliveredEventSequence": 0}
    # status has no persisted range attachment; exercise its supplied evidence.
    if endpoint == "status":
        data["consoleEvidence"] = deepcopy(raw)
    original = deepcopy(data)
    calls = []

    class Dispatcher:
        async def call(self, request_id, name, payload):
            assert name in {"test.results", "test.status"}, "No live Console/Capture reads"
            calls.append(name)
            return ok(request_id, data)

    service = TestDomainService()
    service.server = SimpleNamespace(state=state)
    service.dispatcher = Dispatcher()
    for _ in range(2):
        if endpoint == "status":
            result = asyncio.run(service.test_status(detail_level=detail))
        else:
            result = asyncio.run(service.test_results(
                "run-d08", detail_level=detail, cursor="begin" if endpoint == "incremental" else ""))
        assert result.ok
        projected = result.data["consoleEvidence"]
        assert projected["coverage"] == "partial"
        assert projected["rangeCoverage"] == "complete"
        assert projected["rangeGapReason"] == ""
        assert projected["phaseCoverage"] == "partial"
        assert GAP in projected["phaseGapReason"] and GAP in projected["gapReason"]
        assert projected["coverageScope"] == "range_and_reported_phases"
        assert result.data["outcomeStatus"] == "passed" and result.data["cleanupSucceeded"] is True
        if detail == "full":
            assert projected["phases"] == raw["phases"]
        else:
            assert "phases" not in projected and "logs" not in projected
            assert result.data["summaryBytes"] == len(json.dumps(result.data, ensure_ascii=False).encode()) <= 8192
        assert state.get_console_evidence("runGuid", "run-d08") == persisted
        assert data == original
    assert calls == ["test.status" if endpoint == "status" else "test.results"] * 2


@pytest.mark.parametrize("range_coverage,phase_coverage,expected", [
    ("complete", "complete", "complete"),
    ("complete", "pending", "pending"),
    ("pending", "complete", "pending"),
    ("pending", "partial", "partial"),
    ("partial", "complete", "partial"),
    ("complete", "unavailable", "unavailable"),
    ("unavailable", "complete", "unavailable"),
    ("complete", "unknown", "partial"),
    ("complete", None, "partial"),
])
def test_summary_uses_weakest_reported_coverage(range_coverage, phase_coverage, expected):
    data = {"consoleEvidence": {"coverage": range_coverage, "gapReason": "",
                               "phases": [phase(phase_coverage, "")]}}
    original = deepcopy(data)
    summary = _test_summary(ok("read", data))
    evidence = summary.data["consoleEvidence"]
    assert evidence["coverage"] == expected
    assert evidence["rangeCoverage"] == range_coverage
    if phase_coverage not in {"complete", "pending"}:
        assert evidence["phaseGapReason"] == "phase_coverage_unverified"
    assert data == original


@pytest.mark.parametrize("phases", [None, [], "malformed"])
def test_legacy_or_empty_phases_are_explicitly_range_only(phases):
    raw = {"coverage": "complete", "gapReason": ""}
    if phases is not None:
        raw["phases"] = phases
    evidence = _test_summary(ok("read", {"consoleEvidence": raw})).data["consoleEvidence"]
    assert evidence["coverageScope"] == "range"
    assert evidence["rangeCoverage"] == evidence["coverage"] == "complete"
    assert evidence["phaseCoverage"] == "unavailable"
    assert evidence["phaseGapReason"] == "phase_evidence_unavailable"


def test_summary_keeps_phase_gaps_and_budget_without_inventing_range():
    phases = [phase("partial", "中文😀" * 4000), phase("partial", "second-gap")]
    data = {"consolePhases": phases, "status": "failed", "firstFailure": "real assertion"}
    summary = _test_summary(ok("read", data)).data
    assert summary["consoleEvidence"]["rangeCoverage"] == "unavailable"
    assert summary["consoleEvidence"]["phaseCoverage"] == "partial"
    assert summary["firstFailure"] == "real assertion"
    assert summary["summaryBytes"] == len(json.dumps(summary, ensure_ascii=False).encode()) <= 8192
    assert "logs" not in json.dumps(summary)


def test_multiple_gaps_idempotence_and_optional_scene_restore():
    raw = {"coverage": "partial", "gapReason": "range-gap", "phases": [
        phase("complete", "", "testExecution"), phase("partial", GAP),
        phase("partial", "restore-gap", "sceneRestore"), phase("partial", GAP),
    ]}
    first = _test_summary(ok("read", {"consoleEvidence": raw})).data
    again = _test_summary(ok("read", first)).data
    assert again["consoleEvidence"] == first["consoleEvidence"]
    assert first["consoleEvidence"]["gapReason"] == ";".join(["range-gap", GAP, "restore-gap"])
    complete = _test_summary(ok("read", {"consoleEvidence": {
        "coverage": "complete", "gapReason": "", "phases": [phase("complete", "", "testExecution")],
    }})).data["consoleEvidence"]
    assert complete["coverage"] == "complete"  # do not invent mandatory sceneRestore


def test_shared_range_and_phase_reason_is_only_reported_once():
    raw = {"coverage": "partial", "gapReason": GAP, "phases": [
        phase(), phase("partial", "restore-gap", "sceneRestore"),
    ]}
    evidence = _test_summary(ok("read", {"consoleEvidence": raw})).data["consoleEvidence"]
    assert evidence["gapReason"] == GAP + ";restore-gap"


def test_explicit_empty_phases_do_not_reuse_old_projected_phase_coverage():
    first = _test_summary(ok("read", {"consoleEvidence": {
        "coverage": "complete", "phases": [phase()],
    }})).data
    first["consolePhases"] = []
    evidence = _test_summary(ok("read", first)).data["consoleEvidence"]
    assert evidence["coverageScope"] == "range"
    assert evidence["phaseCoverage"] == "unavailable"


def test_malformed_phase_cannot_turn_aggregate_complete():
    projected = _test_summary(ok("read", {"consoleEvidence": {
        "coverage": "complete", "phases": [None, {"coverage": "complete"}],
    }})).data["consoleEvidence"]
    assert projected["coverage"] == "partial"
    assert projected["phaseGapReason"] == "phase_coverage_unverified"


@pytest.mark.parametrize("detail", ["full", "summary"])
@pytest.mark.parametrize("tool_name", ["unity_test_results", "unity_test_status"])
def test_public_mcp_response_keeps_gap_and_failed_outcome(tmp_path, monkeypatch, detail, tool_name):
    from test_p2_wp08_contracts import _TestWire
    from upilot_mcp.mcp_tools import test_tools
    raw = {"runGuid": "run-d08", "status": "failed", "outcomeStatus": "failed",
           "cleanupSucceeded": False, "firstFailure": "original assertion",
           "consoleEvidence": {"coverage": "complete", "gapReason": "",
                               "phases": [phase()]}}
    original = deepcopy(raw)
    wire = _TestWire(tmp_path, [ok("read", raw)])
    monkeypatch.setattr(test_tools, "_get_facade", lambda: wire)
    args = {"detailLevel": detail}
    if tool_name == "unity_test_results":
        args["runGuid"] = "run-d08"
    returned = asyncio.run(getattr(test_tools, tool_name)(**args))
    payload = returned.structuredContent
    assert json.loads(returned.content[0].text) == payload
    assert payload["data"]["consoleEvidence"]["coverage"] == "partial"
    assert payload["data"]["consoleEvidence"]["gapReason"] == GAP
    assert payload["data"]["outcomeStatus"] == "failed"
    assert payload["data"]["cleanupSucceeded"] is False
    assert payload["data"]["firstFailure"] == "original assertion"
    assert raw == original and len(wire.calls) == 1


def test_reopened_historical_evidence_is_projected_without_current_console(tmp_path):
    state = StateStore()
    state.configure_project(str(tmp_path))
    raw = {"coverage": "complete", "gapReason": "", "phases": [phase()], "logs": []}
    state.save_console_evidence("runGuid", "historic-d08", raw)
    reopened = StateStore()
    reopened.configure_project(str(tmp_path))
    persisted = deepcopy(reopened.get_console_evidence("runGuid", "historic-d08"))

    class Dispatcher:
        async def call(self, request_id, name, payload):
            assert name == "test.results"
            return ok(request_id, {"runGuid": "historic-d08", "status": "failed"})

    service = TestDomainService()
    service.server = SimpleNamespace(state=reopened)
    service.dispatcher = Dispatcher()
    for detail in ("full", "summary", "full"):
        result = asyncio.run(service.test_results("historic-d08", detail_level=detail))
        assert result.data["consoleEvidence"]["coverage"] == "partial"
        assert result.data["consoleEvidence"]["rangeCoverage"] == "complete"
        assert reopened.get_console_evidence("runGuid", "historic-d08") == persisted
