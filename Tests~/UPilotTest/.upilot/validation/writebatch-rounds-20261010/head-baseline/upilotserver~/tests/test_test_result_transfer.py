"""Lossless full-result transfer contracts; no extra dispatch or cursor change."""
import asyncio
import base64
import copy
import gzip
import json
from types import SimpleNamespace

import pytest

from upilot_mcp.domain import test_service
from upilot_mcp.responses import fail, ok


def pack(value):
    raw = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return {"resultEncoding": "gzip+base64", "uncompressedBytes": len(raw),
            "compressedResult": base64.b64encode(gzip.compress(raw)).decode("ascii")}


def result():
    return {"runGuid": "run-a", "status": "failed", "terminal": True,
            "results": [{"leafKey": "one", "testStatus": "Failed", "message": "中文😀"}],
            "events": [{"sequence": 2, "kind": "leaf_corrected"}],
            "selectedLeafIdentities": ["one"], "cleanupSucceeded": True}


class Wire(test_service.TestDomainService):
    def __init__(self, response):
        self.response = response
        self.calls = []
        self.dispatcher = self

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, payload))
        return self.response


def test_full_result_negotiates_once_and_preserves_all_fields_and_context():
    expected = result()
    service = Wire(ok("bridge", pack(expected), context={"sessionId": "session-a"}, timing={"bridgeMs": 3}))
    actual = asyncio.run(service.test_results("run-a"))
    assert actual.ok and actual.data == expected
    assert actual.context == {"sessionId": "session-a"}
    assert actual.timing == {"bridgeMs": 3}
    assert service.calls == [("test.results", {"acceptCompressed": True, "runGuid": "run-a"})]


def test_full_result_larger_than_frame_round_trips_without_truncation():
    expected = result()
    expected["results"] = [{"leafKey": str(i), "message": "文😀" * 150} for i in range(5001)]
    transfer = pack(expected)
    assert transfer["uncompressedBytes"] > 4 * 1024 * 1024
    assert len(transfer["compressedResult"]) < 4 * 1024 * 1024
    actual = asyncio.run(Wire(ok("bridge", transfer)).test_results("run-a"))
    assert actual.ok and actual.data == expected
    assert len(actual.data["results"]) == 5001


def test_latest_result_keeps_resolved_run_and_legacy_bridge_remains_compatible():
    expected = result()
    for value in (pack(expected), copy.deepcopy(expected)):
        service = Wire(ok("bridge", value))
        actual = asyncio.run(service.test_results())
        assert actual.ok and actual.data == expected
        assert service.calls == [("test.results", {"acceptCompressed": True})]


@pytest.mark.parametrize("fault", [
    "encoding", "bool_size", "negative_size", "huge_size", "too_short", "too_long",
    "invalid_base64", "non_ascii", "missing_payload", "large_encoded", "truncated_gzip",
    "crc", "trailing_bytes", "second_member", "json_list", "wrong_run", "missing_results",
    "invalid_utf8", "invalid_json",
])
def test_malformed_transfer_is_explicit_failure_without_retry(fault):
    transfer = pack(result())
    compressed = base64.b64decode(transfer["compressedResult"])
    if fault == "encoding": transfer["resultEncoding"] = "unknown"
    elif fault == "bool_size": transfer["uncompressedBytes"] = True
    elif fault == "negative_size": transfer["uncompressedBytes"] = -1
    elif fault == "huge_size": transfer["uncompressedBytes"] = test_service._TEST_RESULT_MAX_DECODED_BYTES + 1
    elif fault == "too_short": transfer["uncompressedBytes"] = 1
    elif fault == "too_long": transfer["uncompressedBytes"] += 1
    elif fault == "invalid_base64": transfer["compressedResult"] = "@@@@"
    elif fault == "non_ascii": transfer["compressedResult"] = "文字"
    elif fault == "missing_payload": transfer.pop("compressedResult")
    elif fault == "large_encoded": transfer["compressedResult"] = "A" * (4 * 1024 * 1024 + 1)
    elif fault == "truncated_gzip": transfer["compressedResult"] = base64.b64encode(compressed[:-1]).decode()
    elif fault == "crc": transfer["compressedResult"] = base64.b64encode(compressed[:-8] + bytes([compressed[-8] ^ 1]) + compressed[-7:]).decode()
    elif fault == "trailing_bytes": transfer["compressedResult"] = base64.b64encode(compressed + b"tail").decode()
    elif fault == "second_member": transfer["compressedResult"] = base64.b64encode(compressed + compressed).decode()
    elif fault == "json_list": transfer = pack([])
    elif fault == "wrong_run": transfer = pack({**result(), "runGuid": "run-b"})
    elif fault == "missing_results": transfer = pack({"runGuid": "run-a", "events": []})
    elif fault in ("invalid_utf8", "invalid_json"):
        raw = b"\xff" if fault == "invalid_utf8" else b"{"
        transfer.update(uncompressedBytes=len(raw), compressedResult=base64.b64encode(gzip.compress(raw)).decode())
    service = Wire(ok("bridge", transfer, context={"sessionId": "original"}))
    response = asyncio.run(service.test_results("run-a"))
    assert not response.ok and response.data is None
    assert response.error.code == "TEST_RESULT_TRANSFER_INVALID"
    assert response.error.detail["replayAttempted"] is False
    assert response.context == {"sessionId": "original"}
    assert len(service.calls) == 1


def test_bridge_error_is_preserved_without_decoding_or_replaying():
    original = fail("bridge", "BRIDGE_RESPONSE_TOO_LARGE", "too large", {"actualBytes": 5000000})
    service = Wire(original)
    response = asyncio.run(service.test_results("run-a"))
    assert response is original and len(service.calls) == 1


def test_console_evidence_is_attached_after_decoding():
    service = Wire(ok("bridge", pack(result())))
    evidence = {"coverage": "partial", "gapReason": "original_gap"}
    service.server = SimpleNamespace(state=SimpleNamespace(get_console_evidence=lambda *args: evidence))
    response = asyncio.run(service.test_results("run-a"))
    assert response.data["results"] == result()["results"]
    assert response.data["consoleEvidence"] == evidence


def test_public_tool_returns_complete_decoded_result(monkeypatch):
    from upilot_mcp.mcp_tools import test_tools
    service = Wire(ok("bridge", pack(result())))
    monkeypatch.setattr(test_tools, "_get_facade", lambda: service)
    response = asyncio.run(test_tools.unity_test_results(runGuid="run-a"))
    if hasattr(response, "content"):
        response = json.loads(next(item.text for item in response.content if getattr(item, "text", None)))
    elif isinstance(response, str):
        response = json.loads(response)
    assert response["ok"] is True
    assert response["data"] == result()
    assert len(service.calls) == 1
