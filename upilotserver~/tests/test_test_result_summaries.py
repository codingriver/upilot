"""V12 final public data budget; these tests do not start Unity or a Runner."""
import asyncio
import base64
from copy import deepcopy
import gzip
import json

import pytest

from upilot_mcp.domain.test_service import _TEST_SUMMARY_MAX_BYTES, _test_summary
from upilot_mcp.mcp_tools import test_tools
from upilot_mcp.responses import fail, ok
from test_p2_wp08_contracts import _TestWire


def many_leaves():
    return {
        "runGuid": "run-original", "status": "cleanup", "phase": "cleaning_up",
        "terminal": False, "resultAuthoritative": False, "cleanupPending": True,
        "cleanupSucceeded": False, "cleanupResourcesReleased": False,
        "selectionDomain": "domain-original", "selectionSnapshotId": "selection-original",
        "total": 5001, "failed": 1, "firstFailure": "中文😀" * 4000,
        "results": [{"fullName": f"Demo.Cases({n})", "message": "leaf"} for n in range(5001)],
        "selectors": [{"selector": "名字" * 5000}] * 50,
        "selectedLeafIdentities": [str(n) for n in range(5001)],
        "events": [{"sequence": n + 1} for n in range(5001)],
        "unresolvedResources": ["callback"], "cleanupErrors": ["still pending"],
        "consolePhases": [{"logs": ["中文" * 5000]}],
    }


@pytest.mark.parametrize("endpoint", ["results", "status"])
def test_summary_bounds_final_data_and_preserves_identity_unknown_cleanup(endpoint, tmp_path):
    data = many_leaves()
    original = deepcopy(data)
    wire = _TestWire(tmp_path, [ok("read", data)])
    async def evidence(response, run):
        assert run == "run-original"
        response.data["consoleEvidence"] = {"coverage": "partial", "gapReason": "空白😀" * 4000,
                                            "logs": ["post projection" * 8000]}
    wire._attach_test_console_evidence = evidence
    if endpoint == "results":
        result = asyncio.run(wire.test_results("run-original", detail_level="summary"))
    else:
        data["consoleEvidence"] = {"coverage": "partial", "gapReason": "空白😀" * 4000, "logs": ["long" * 8000]}
        original = deepcopy(data)
        result = asyncio.run(wire.test_status(detail_level="summary"))
    summary = result.data
    assert result.ok and summary["detailLevel"] == "summary"
    assert summary["summaryBytes"] == len(json.dumps(summary, ensure_ascii=False).encode("utf-8")) <= 8192
    assert summary["arrayCounts"]["results"] == 5001
    assert summary["arrayCounts"]["unresolvedResources"] == 1
    assert summary["cleanupPending"] is True and summary["cleanupSucceeded"] is False
    assert summary["terminal"] is False and summary["resultAuthoritative"] is False
    assert summary["selectionSnapshotId"] == "selection-original"
    assert "results" not in summary and "unresolvedResources" not in summary
    assert "logs" not in summary["consoleEvidence"] and summary["consoleEvidence"]["coverage"] == "partial"
    assert data == original


def test_default_and_explicit_full_remain_lossless_after_summary(tmp_path):
    data = many_leaves()
    response = ok("read", data)
    wire = _TestWire(tmp_path, [response, response, response])
    summary = asyncio.run(wire.test_results("run-original", detail_level="summary"))
    default = asyncio.run(wire.test_results("run-original"))
    full = asyncio.run(wire.test_results("run-original", detail_level="full"))
    assert summary.data is not data
    assert default.data == full.data == data
    assert len(full.data["results"]) == 5001
    assert all(payload == {"acceptCompressed": True, "runGuid": "run-original"}
               for name, payload in wire.calls)


def test_compressed_summary_preserves_wire_response_and_full_read(tmp_path):
    data = many_leaves()
    raw = json.dumps(data, ensure_ascii=False).encode("utf-8")
    envelope = {
        "resultEncoding": "gzip+base64", "uncompressedBytes": len(raw),
        "compressedResult": base64.b64encode(gzip.compress(raw)).decode("ascii"),
    }
    original = ok("read", deepcopy(envelope))
    wire = _TestWire(tmp_path, [original, ok("full", deepcopy(envelope))])
    summary = asyncio.run(wire.test_results("run-original", detail_level="summary"))
    assert summary.ok and summary.data["arrayCounts"]["results"] == 5001
    assert original.data == envelope
    assert summary.data is not original.data
    assert summary.data["summaryBytes"] == len(json.dumps(summary.data, ensure_ascii=False).encode("utf-8")) <= 8192
    full = asyncio.run(wire.test_results("run-original", detail_level="full"))
    assert full.ok and full.data == data
    assert original.data == envelope
    assert wire.calls == [("test.results", {"acceptCompressed": True, "runGuid": "run-original"})] * 2


@pytest.mark.parametrize("detail", ["", "compact", None, [], 3])
def test_bad_detail_level_does_not_dispatch(tmp_path, detail):
    wire = _TestWire(tmp_path)
    for call in (wire.test_results(detail_level=detail), wire.test_status(detail_level=detail)):
        response = asyncio.run(call)
        assert not response.ok and response.error.code == "TEST_DETAIL_LEVEL_INVALID"
        assert response.error.detail["sideEffectsMayHaveOccurred"] is False
    assert wire.calls == []


def test_summary_cursor_conflict_is_rejected_before_dispatch(tmp_path):
    wire = _TestWire(tmp_path)
    result = asyncio.run(wire.test_results("run-original", cursor="begin", detail_level="summary"))
    assert not result.ok and result.error.code == "TEST_DETAIL_CURSOR_CONFLICT"
    assert wire.calls == []


def test_missing_cleanup_facts_remain_missing_and_oversized_identity_fails_explicitly():
    summary = _test_summary(ok("read", {"runGuid": "run", "status": "completed"})).data
    assert not {"terminal", "cleanupSucceeded", "unresolvedResources"}.intersection(summary)
    assert summary["arrayCounts"] == {}
    result = _test_summary(ok("read", {"runGuid": "身份" * 10000}))
    assert not result.ok and result.error.code == "TEST_SUMMARY_IDENTITY_TOO_LARGE"



@pytest.mark.parametrize("identity_key", ["sessionId", "captureSessionId"])
def test_console_capture_identity_is_never_truncated(identity_key):
    identity = "capture-" + "x" * 700
    response = ok("read", {"runGuid": "run", "consoleEvidence": {identity_key: identity}})
    result = _test_summary(response)
    assert result.ok and result.data["consoleEvidence"][identity_key] == identity
    assert identity_key not in result.data["truncatedFields"]
    oversized = _test_summary(ok("read", {
        "runGuid": "run", "consoleEvidence": {identity_key: "身份" * 10000},
    }))
    assert not oversized.ok and oversized.error.code == "TEST_SUMMARY_IDENTITY_TOO_LARGE"
    assert response.data["consoleEvidence"][identity_key] == identity


def test_summary_preserves_response_metadata_and_bridge_errors():
    original = ok("read", {"runGuid": "run"}, context={"domain": "source"}, timing={"totalMs": 7})
    result = _test_summary(original)
    assert result.context == original.context and result.timing == original.timing
    assert result.timestamp == original.timestamp and result.request_id == original.request_id
    error = fail("read", "ORIGINAL", "first cause", {"cleanup": "unknown"})
    assert _test_summary(error) is error


@pytest.mark.parametrize("tool_name", ["unity_test_results", "unity_test_status"])
def test_public_tool_schema_and_real_entry_forward_summary(tool_name, tmp_path, monkeypatch):
    from upilot_mcp.mcp_stdio_server import mcp
    exposed = {tool.name: tool for tool in asyncio.run(mcp.list_tools())}
    assert exposed[tool_name].inputSchema["properties"]["detailLevel"]["default"] == "full"
    wire = _TestWire(tmp_path, [ok("read", many_leaves())])
    monkeypatch.setattr(test_tools, "_get_facade", lambda: wire)
    arguments = {"detailLevel": "summary"}
    if tool_name == "unity_test_results":
        arguments["runGuid"] = "run-original"
    returned = asyncio.run(getattr(test_tools, tool_name)(**arguments))
    payload = returned.structuredContent
    assert json.loads(returned.content[0].text) == payload
    assert payload["data"]["detailLevel"] == "summary"
    assert payload["data"]["summaryBytes"] == len(json.dumps(payload["data"], ensure_ascii=False).encode()) <= _TEST_SUMMARY_MAX_BYTES
    assert len(wire.calls) == 1
