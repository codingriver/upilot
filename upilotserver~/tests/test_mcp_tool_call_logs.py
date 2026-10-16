from __future__ import annotations

import asyncio
import io
import json
import logging
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

import pytest
from mcp.types import CallToolResult, TextContent
from upilot_mcp import mcp_stdio_server as runtime

TARGET_CASES = (
    ("csharp_eval", {"code": "1 + 1"}),
    ("unity_reflection_call", {"expression": "1 + 1"}),
    ("csharp_object_dump", {"sessionId": "session-1", "handle": "handle-1"}),
)


def _result(value="ok", success=True):
    structured = {"ok": success, "data": {"value": value}}
    return CallToolResult(content=[TextContent(type="text", text=json.dumps(structured))],
                          structuredContent=structured, isError=False)


@pytest.fixture(autouse=True)
def project(monkeypatch, tmp_path):
    monkeypatch.setattr(runtime, "_MCP_TOOL_LOG_WRITERS", {})
    monkeypatch.setattr(runtime, "_facade", SimpleNamespace(server=SimpleNamespace(
        session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(tmp_path))),
        state=SimpleNamespace(project_path=""))))


def _log_path(project_root, name):
    return project_root / "Logs" / "UPilot" / "McpCalls" / f"{name}-{datetime.now():%Y-%m-%d}.log"


def _entries(root, name="csharp_eval"):
    return [json.loads(line) for line in _log_path(root, name).read_text(encoding="utf-8").splitlines()]


@pytest.mark.parametrize(("tool_name", "arguments"), TARGET_CASES)
def test_target_tool_call_writes_start_and_finish_once(monkeypatch, tmp_path, tool_name, arguments):
    expected = _result(tool_name)
    calls = []
    async def fake_call(name, normalized, **kwargs):
        calls.append(name)
        start = _entries(tmp_path, name)
        assert len(start) == 1 and start[0]["status"] == "running"
        return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    actual = asyncio.run(runtime._call_tool_with_strict_arguments(tool_name, arguments))
    assert actual is expected and calls == [tool_name]
    start, finish = _entries(tmp_path, tool_name)
    assert start["event"] == "call.start" and finish["event"] == "call.end"
    assert start["callId"] == finish["callId"] and start["callId"]
    assert start["parameters"] == finish["parameters"] == arguments
    assert finish["status"] == "succeeded" and finish["durationMs"] >= 0
    assert finish["startedAt"] == start["startedAt"] <= finish["endedAt"]
    assert finish["result"]["structuredContent"]["data"]["value"] == tool_name
    assert {p.name for p in _log_path(tmp_path, tool_name).parent.iterdir()} == {_log_path(tmp_path, tool_name).name}


def test_non_target_tool_does_not_create_dedicated_log(monkeypatch, tmp_path):
    expected = _result()
    async def fake_call(*args, **kwargs): return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    assert asyncio.run(runtime._call_tool_with_strict_arguments("unity_mcp_status", {})) is expected
    assert not (tmp_path / "Logs").exists()


@pytest.mark.parametrize("cancel", [False, True])
def test_exception_or_cancellation_logged_without_replay(monkeypatch, tmp_path, cancel):
    expected = asyncio.CancelledError("cancelled") if cancel else RuntimeError("boom")
    calls = []
    async def fake_call(*args, **kwargs):
        calls.append(1)
        raise expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    with pytest.raises(type(expected)) as captured:
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "fail"}))
    assert captured.value is expected and calls == [1]
    start, finish = _entries(tmp_path)
    assert start["callId"] == finish["callId"]
    assert finish["status"] == ("cancelled" if cancel else "failed")
    assert finish["error"]["type"] == type(expected).__name__
    assert str(expected) in finish["error"]["stackTrace"]


@pytest.mark.parametrize("shape", ["structured", "dict", "content", "tuple", "isError"])
def test_business_failure_is_not_reported_success(monkeypatch, tmp_path, shape):
    payload = {"ok": False, "error": {"message": "failure"}}
    expected = {"structured": _result(success=False), "dict": payload,
                "content": [TextContent(type="text", text=json.dumps(payload))],
                "tuple": ([], payload), "isError": CallToolResult(content=[], isError=True)}[shape]
    async def fake_call(*args, **kwargs): return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    assert asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"})) is expected
    assert _entries(tmp_path)[1]["status"] == "failed"


def test_large_content_appends_in_full_without_redaction_or_overwrite(monkeypatch, tmp_path):
    path = _log_path(tmp_path, "csharp_eval")
    path.parent.mkdir(parents=True)
    path.write_text("prior partial record", encoding="utf-8")
    legacy = path.with_name("csharp_eval.log")
    legacy.write_text("legacy", encoding="utf-8")
    value = "中" * (4 * 1024 * 1024) + "\n🙂"
    expected = _result(value)
    async def fake_call(*args, **kwargs): return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    arguments = {"code": value, "variables": {"token": "original-secret"}}
    assert asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", arguments)) is expected
    lines = path.read_text(encoding="utf-8").splitlines()
    assert lines[0] == "prior partial record"
    start, finish = map(json.loads, lines[1:])
    assert start["parameters"] == finish["parameters"] == arguments
    assert finish["result"]["structuredContent"]["data"]["value"] == value
    assert path.stat().st_size > 10 * 1024 * 1024
    assert legacy.read_text() == "legacy"


@pytest.mark.parametrize("missing", [False, True])
def test_log_failure_visible_without_changing_result_or_replaying(monkeypatch, tmp_path, capsys, missing):
    if missing:
        runtime._facade.server.session_manager.active = None
    else:
        (tmp_path / "Logs").write_text("not a directory")
    calls = []
    expected = _result()
    async def fake_call(*args, **kwargs):
        calls.append(1)
        return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    assert asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"})) is expected
    assert calls == [1]
    stderr = capsys.readouterr().err
    assert "ERROR" in stderr and "Traceback" in stderr and "path=" in stderr


def test_real_context_ids_are_correlated(monkeypatch, tmp_path):
    from upilot_mcp.operation_context import OPERATION_ID
    async def fake_call(*args, **kwargs): return _result()
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    token = OPERATION_ID.set("real-operation")
    try:
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"},
                    context=SimpleNamespace(request_context=SimpleNamespace(request_id="request-1"))))
    finally:
        OPERATION_ID.reset(token)
    for entry in _entries(tmp_path):
        assert entry["operationId"] == "real-operation" and entry["requestId"] == "request-1"


def test_validation_failure_is_logged_before_any_business_call(monkeypatch, tmp_path):
    calls = []
    async def fake_call(*args, **kwargs): calls.append(1)
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    original = {"code": "1", "unknownArgument": "original"}
    with pytest.raises(ValueError):
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", original))
    start, finish = _entries(tmp_path)
    assert not calls and start["parameters"] == finish["parameters"] == original
    assert finish["status"] == "failed" and finish["error"]["type"] == "ValueError"


def test_normalization_does_not_replace_original_logged_parameters(monkeypatch, tmp_path):
    original = {"code": "1", "execution_backend": "interpret"}
    async def fake_call(name, normalized, **kwargs):
        assert normalized == {"code": "1", "executionBackend": "interpret"}
        return _result()
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", original))
    assert all(entry["parameters"] == original for entry in _entries(tmp_path))


def test_tool_failure_is_visible_in_server_diagnostics_with_original_stack(monkeypatch, caplog):
    async def fake_call(*args, **kwargs):
        raise RuntimeError("diagnostic original exception")
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    with caplog.at_level("ERROR", logger="upilot.mcp"), pytest.raises(RuntimeError):
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"}))
    assert "MCP tool failed name=csharp_eval callId=" in caplog.text
    assert "RuntimeError: diagnostic original exception" in caplog.text
    assert "fake_call" in caplog.text


@pytest.mark.parametrize("cancelled", [False, True])
def test_failed_result_logs_error_but_normal_cancel_does_not(monkeypatch, caplog, cancelled):
    async def fake_call(*args, **kwargs):
        return {"ok": False, "error": {"code": "EXECUTION_CANCELLED" if cancelled else "EXECUTION_RUNTIME_ERROR", "message": "operation result"}}
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    with caplog.at_level("ERROR", logger="upilot.mcp"):
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"}))
    assert ("MCP tool returned failure" in caplog.text) is (not cancelled)


@pytest.mark.parametrize("shape", ["structured", "dict", "content", "tuple", "tuple_content"])
def test_returned_cancellation_keeps_call_identity_and_actual_status(monkeypatch, tmp_path, shape):
    payload = {"ok": False, "error": {"code": "EXECUTION_CANCELLED", "message": "cancelled in Unity"}}
    content = [TextContent(type="text", text=json.dumps(payload))]
    expected = {"structured": CallToolResult(content=content, structuredContent=payload, isError=True),
                "dict": payload, "content": content, "tuple": (content, payload),
                "tuple_content": (content, {})}[shape]
    calls = []
    async def fake_call(*args, **kwargs):
        calls.append(1)
        return expected
    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    assert asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"})) is expected
    start, finish = _entries(tmp_path)
    assert calls == [1] and start["callId"] == finish["callId"]
    assert finish["status"] == "cancelled" and finish["durationMs"] >= 0
    assert finish["result"]


@pytest.fixture
def server_diagnostics(tmp_path):
    from upilot_mcp.daily_logs import DailyLogHandler
    from upilot_mcp.diagnostic_logs import RepeatDiagnosticFilter

    root = logging.getLogger("upilot")
    previous = (root.handlers, root.level, root.propagate, runtime.logger.level)
    now = [0.0]
    stream = io.StringIO()
    gate = RepeatDiagnosticFilter(clock=lambda: now[0])
    console = logging.StreamHandler(stream)
    file = DailyLogHandler(tmp_path / "mcp-server.log")
    file.writer.retention = lambda: 3
    for handler in (console, file):
        handler.setFormatter(logging.Formatter("%(levelname)s %(message)s"))
        handler.addFilter(gate)
    root.handlers = [console, file]
    root.setLevel(logging.DEBUG)
    root.propagate = False
    runtime.logger.setLevel(logging.NOTSET)

    def outputs():
        path = tmp_path / f"mcp-server-{datetime.now():%Y-%m-%d}.log"
        return stream.getvalue(), path.read_text(encoding="utf-8") if path.exists() else ""

    try:
        yield now, gate, outputs
    finally:
        gate.flush()
        root.handlers, level, root.propagate, tool_level = previous
        root.setLevel(level)
        runtime.logger.setLevel(tool_level)
        console.close()
        file.close()


def _observation_failure(operation="operation-1", request="request-1", *, terminal=False, code="OBSERVATION_FAILED"):
    return {"ok": False, "requestId": request,
            "error": {"code": code, "message": "stable original cause",
                      "detail": {"operationId": operation, "terminal": terminal,
                                 "exceptionType": "OriginalException", "exceptionMessage": "original detail",
                                 "stackTrace": "Traceback (original):\n  original_business_frame\nOriginalException: 原异常\n"}}}


@pytest.mark.parametrize("shape", ["structured", "dict", "content", "tuple", "tuple_content"])
def test_returned_failure_original_stack_reaches_both_general_sinks(monkeypatch, server_diagnostics, shape):
    payload = _observation_failure()
    content = [TextContent(type="text", text=json.dumps(payload))]
    expected = {"structured": CallToolResult(content=content, structuredContent=payload, isError=True),
                "dict": payload, "content": content, "tuple": (content, payload),
                "tuple_content": (content, {})}[shape]
    calls = []

    async def fake_call(*args, **kwargs):
        calls.append(1)
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    actual = asyncio.run(runtime._call_tool_with_strict_arguments(
        "unity_reflection_operation_status", {"operationId": "operation-1"}))
    assert actual is expected and calls == [1]
    for text in server_diagnostics[2]():
        assert text.count("ERROR MCP tool returned failure") == 1
        assert payload["error"]["detail"]["stackTrace"] in text
        assert '"requestId": "request-1"' in text and '"operationId": "operation-1"' in text


def test_repeated_observations_aggregate_without_request_or_call_id_fragmentation(monkeypatch, server_diagnostics):
    now, _, outputs = server_diagnostics
    calls = []

    async def fake_call(*args, **kwargs):
        calls.append(1)
        return _observation_failure(request=f"request-{len(calls)}")

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for _ in range(3):
        asyncio.run(runtime._call_tool_with_strict_arguments(
            "unity_reflection_operation_status", {"operationId": "operation-1"}))
    assert len(calls) == 3
    for text in outputs():
        assert text.count("MCP tool returned failure") == 1
        assert '"requestId": "request-2"' not in text
    now[0] = 10
    runtime.logger.info("A later real event flushes pending repeats")
    for text in outputs():
        assert "repeated count=2" in text
        assert text.count("MCP tool returned failure") == 2


@pytest.mark.parametrize(("name", "arguments", "identity_field"), [
    ("unity_operation_wait", {"operationId": "observed"}, "operationId"),
    ("unity_test_results", {"runGuid": "observed"}, "runGuid"),
    ("unity_compile_wait", {}, "compileRequestId"),
])
def test_wait_and_result_polling_aggregate_only_the_real_returned_identity(monkeypatch, server_diagnostics, name, arguments, identity_field):
    count = [0]

    async def fake_call(*args, **kwargs):
        count[0] += 1
        return {"ok": False, "requestId": f"request-{count[0]}",
                "error": {"code": "OBSERVATION_FAILED", "message": "same observation failure",
                          "detail": {identity_field: "observed"}}}

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for _ in range(3):
        asyncio.run(runtime._call_tool_with_strict_arguments(name, arguments))
    assert count[0] == 3
    for text in server_diagnostics[2]():
        assert text.count("MCP tool returned failure") == 1
    server_diagnostics[1].flush()
    for text in server_diagnostics[2]():
        assert "repeated count=2" in text


def test_observation_tasks_roots_and_true_terminal_are_distinct(monkeypatch, server_diagnostics):
    response = [None]

    async def fake_call(*args, **kwargs):
        return response[0]

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for operation, request, terminal, code in [
        ("operation-1", "first", False, "OBSERVATION_FAILED"),
        ("operation-2", "other-task", False, "OBSERVATION_FAILED"),
        ("operation-1", "other-root", False, "OTHER_CAUSE"),
        ("operation-1", "terminal-first", True, "OBSERVATION_FAILED"),
        ("operation-1", "terminal-repeat", True, "OBSERVATION_FAILED"),
    ]:
        response[0] = _observation_failure(operation, request, terminal=terminal, code=code)
        asyncio.run(runtime._call_tool_with_strict_arguments(
            "unity_reflection_operation_status", {"operationId": operation}))
    for text in server_diagnostics[2]():
        assert text.count("MCP tool returned failure") == 4
        assert '"requestId": "other-task"' in text and '"requestId": "other-root"' in text
        assert '"requestId": "terminal-first"' in text and '"requestId": "terminal-repeat"' not in text


def test_observation_recovery_flushes_counts_and_allows_next_first_fault(monkeypatch, server_diagnostics):
    _, gate, outputs = server_diagnostics
    response = [None]

    async def fake_call(*args, **kwargs):
        return response[0]

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for payload in [_observation_failure(), _observation_failure(request="repeat"), _result()]:
        response[0] = payload
        assert asyncio.run(runtime._call_tool_with_strict_arguments(
            "unity_reflection_operation_status", {"operationId": "operation-1"})) is payload
    assert not any(key[1] == "mcp:reflection|operationId=operation-1" for key in gate._entries)
    response[0] = _observation_failure(request="recurrence")
    asyncio.run(runtime._call_tool_with_strict_arguments(
        "unity_reflection_operation_status", {"operationId": "operation-1"}))
    for text in outputs():
        assert "repeated count=1" in text
        assert '"requestId": "recurrence"' in text
        assert text.count("MCP tool returned failure") == 3


def test_independent_execution_with_an_existing_id_keeps_per_call_errors(monkeypatch, server_diagnostics):
    async def fake_call(*args, **kwargs):
        return _observation_failure()

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for _ in range(2):
        asyncio.run(runtime._call_tool_with_strict_arguments(
            "independent_execution", {"operationId": "operation-1"}))
    for text in server_diagnostics[2]():
        assert text.count("MCP tool returned failure") == 2


def test_observation_exceptions_aggregate_and_keep_the_original_stack(monkeypatch, server_diagnostics):
    calls = []

    async def fake_call(*args, **kwargs):
        calls.append(1)
        raise RuntimeError("original observation exception")

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    for _ in range(2):
        with pytest.raises(RuntimeError, match="original observation exception"):
            asyncio.run(runtime._call_tool_with_strict_arguments(
                "unity_reflection_operation_status", {"operationId": "operation-1"}))
    assert len(calls) == 2
    for text in server_diagnostics[2]():
        assert text.count("ERROR MCP tool failed") == 1
        assert "fake_call" in text and "RuntimeError: original observation exception" in text


def test_task_observations_share_producer_scope_without_relogging_failed_task(monkeypatch, server_diagnostics):
    payload = {"ok": True, "data": {"taskId": "task-1", "status": "failed", "terminal": True,
                                    "error": {"code": "TASK_EXCEPTION", "message": "background failure"}}}

    async def fake_call(*args, **kwargs):
        return payload

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    identity = runtime._mcp_diagnostic_identity("unity_task_status", {"taskId": "task-1"}, "per-call", result=payload)
    assert identity["diagnostic_scope"] == "task-1"
    runtime.logger.error("Background task failed taskId=task-1",
                         extra={"diagnostic_scope": "task-1", "diagnostic_terminal": True})
    for _ in range(2):
        assert asyncio.run(runtime._call_tool_with_strict_arguments(
            "unity_task_status", {"taskId": "task-1"})) is payload
    for text in server_diagnostics[2]():
        assert text.count("ERROR Background task failed") == 1
        assert "MCP tool returned failure" not in text


@pytest.mark.parametrize("raised", [False, True])
def test_observation_normal_cancellation_does_not_log_error(monkeypatch, server_diagnostics, raised):
    expected = _observation_failure(code="EXECUTION_CANCELLED", terminal=True)

    async def fake_call(*args, **kwargs):
        if raised:
            raise asyncio.CancelledError("normal cancellation")
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
    if raised:
        with pytest.raises(asyncio.CancelledError):
            asyncio.run(runtime._call_tool_with_strict_arguments(
                "unity_reflection_operation_status", {"operationId": "operation-1"}))
    else:
        assert asyncio.run(runtime._call_tool_with_strict_arguments(
            "unity_reflection_operation_status", {"operationId": "operation-1"})) is expected
    assert all("ERROR" not in text for text in server_diagnostics[2]())
