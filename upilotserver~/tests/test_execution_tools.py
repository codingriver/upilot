from __future__ import annotations

import asyncio
import hashlib
import json

from upilot_mcp.domain.execution_service import (
    ExecutionDomainService,
    normalize_arguments,
    normalize_execution_error,
    normalize_variables,
)
from upilot_mcp.responses import fail, ok
from upilot_mcp.tool_registry import REGISTRY, REGISTRY_VERSION

# Importing the module is the explicit FastMCP + Registry registration point.
from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp.mcp_tools import execution_tools, reflection_tools


class _Dispatcher:
    def __init__(self):
        self.calls = []

    async def call(self, request_id, command, payload, timeout_ms=30000):
        self.calls.append((command, payload, timeout_ms))
        return ok(request_id, {"command": command})


def _service():
    service = ExecutionDomainService()
    service.dispatcher = _Dispatcher()
    return service


def test_execution_registry_v6_explicit_tools_are_write_gated_non_idempotent():
    assert REGISTRY_VERSION == 6
    for name in ("execution_session", "csharp_eval", "reflection_emit_type"):
        descriptor = REGISTRY.resolve(name)
        assert descriptor is not None
        assert descriptor.category == "execution"
        assert descriptor.destructive is True
        assert descriptor.idempotent is False
        assert descriptor.requires_write_access is True
        assert descriptor.play_mode_policy == "allowed"
    assert REGISTRY.resolve("reflection_eval") is None


def test_execution_tool_schemas_explain_routes_and_complex_parameters():
    tools = {tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())}

    reflection = tools["unity_reflection_call"]
    assert "csharp_eval" in reflection.description
    assert "未来的 csharp_eval" not in reflection.description
    reflection_properties = reflection.inputSchema["properties"]
    for name in (
        "kind",
        "expression",
        "arguments",
        "parameterTypeNames",
        "genericTypeArguments",
        "targetHandle",
        "awaitMode",
        "resultMode",
    ):
        assert reflection_properties[name]["description"]

    csharp = tools["csharp_eval"]
    assert "C# 子集 V2" in csharp.description
    assert "async void" in csharp.description
    for name in ("code", "mode", "sessionId", "variables", "executionBackend", "limits", "resultMode"):
        assert csharp.inputSchema["properties"][name]["description"]

    emit = tools["reflection_emit_type"]
    assert "try/catch/finally" in emit.description
    assert "拒绝 lambda/closure/await/async" in emit.description
    for name in ("sessionId", "spec", "cachePolicy", "nameConflictPolicy", "createInstance"):
        assert emit.inputSchema["properties"][name]["description"]

    session = tools["execution_session"]
    assert "活动异步任务" in session.description
    for name in ("action", "sessionId", "ttlSec", "maxHandles", "maxDynamicTypes", "maxCallbacks", "maxAsyncOperations"):
        assert session.inputSchema["properties"][name]["description"]


def test_typed_value_normalization_accepts_primitives_and_explicit_values():
    arguments = json.loads(normalize_arguments([
        3,
        {"name": "target", "direction": "ref", "value": {"kind": "handle", "handle": "h.domain.object.id"}},
    ]))
    assert arguments["items"][0]["value"]["kind"] == "literal"
    assert arguments["items"][0]["value"]["valueJson"] == "3"
    assert arguments["items"][1]["direction"] == "ref"
    assert arguments["items"][1]["value"]["handle"] == "h.domain.object.id"
    variables = json.loads(normalize_variables({"enabled": True}))
    assert variables["items"][0]["value"]["typeName"] == "System.Boolean"


def test_csharp_eval_dispatches_once_with_normalized_contract():
    service = _service()
    result = asyncio.run(service.csharp_eval("return value + 1;", mode="statements", variables={"value": 2}))
    assert result.ok
    assert len(service.dispatcher.calls) == 1
    command, payload, _ = service.dispatcher.calls[0]
    assert command == "csharp.eval"
    assert payload["mode"] == "statements"
    assert json.loads(payload["variablesJson"])["items"][0]["name"] == "value"


def test_emit_uses_canonical_sha256_and_dispatches_once():
    service = _service()
    spec = {"typeName": "Example.Dynamic", "isSealed": True}
    result = asyncio.run(service.reflection_emit_type("s.domain.id", spec))
    assert result.ok
    command, payload, _ = service.dispatcher.calls[0]
    canonical = json.dumps(spec, ensure_ascii=False, separators=(",", ":"))
    assert command == "reflection.emitType"
    assert payload["specHash"] == hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def test_execution_session_wrapper_calls_facade_once(monkeypatch):
    calls = []

    class _Facade:
        async def execution_session(self, **kwargs):
            calls.append(kwargs)
            return ok("req", {"sessionId": "s.domain.id"})

    monkeypatch.setattr(execution_tools, "_get_facade", lambda: _Facade())
    monkeypatch.setattr(execution_tools, "_reject_write_if_unapproved", lambda _name: None)
    result = asyncio.run(execution_tools.execution_session("open", title="test"))
    assert result.isError is False
    assert len(calls) == 1
    assert calls[0]["action"] == "open"
    assert calls[0]["max_async_operations"] == 64


def test_v2_limits_and_session_async_capacity_dispatch_once():
    service = _service()
    result = asyncio.run(service.execution_session("open", max_async_operations=12))
    assert result.ok
    assert service.dispatcher.calls[0][1]["maxAsyncOperations"] == 12

    service = _service()
    result = asyncio.run(service.csharp_eval(
        "return new int[2,3];",
        mode="statements",
        limits={"maxAwaits": 9, "maxArrayElements": 77},
    ))
    assert result.ok
    limits = json.loads(service.dispatcher.calls[0][1]["limitsJson"])
    assert limits == {"maxAwaits": 9, "maxArrayElements": 77}


def test_execution_error_normalizes_legacy_json_details_to_objects():
    response = fail("req", "CSHARP_PARSE_ERROR", "bad", {
        "sourceSpanJson": '{"start":4,"length":2,"end":6,"line":2,"column":1,"endLine":2,"endColumn":3}',
        "diagnosticsJson": '[{"code":"X"}]',
        "candidatesJson": '["A","B"]',
    })
    normalized = normalize_execution_error(response)
    assert normalized.error.detail["sourceSpan"]["line"] == 2
    assert normalized.error.detail["diagnostics"][0]["code"] == "X"
    assert normalized.error.detail["candidates"] == ["A", "B"]
