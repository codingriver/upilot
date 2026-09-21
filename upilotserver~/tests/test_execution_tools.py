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
from upilot_mcp.domain.reflection_service import ReflectionDomainService
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
    assert REGISTRY_VERSION == 7
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


def test_typed_value_normalization_canonicalizes_case_insensitive_kinds():
    arguments = json.loads(normalize_arguments([{
        "kind": " ARRAY ",
        "typeName": "System.String[]",
        "items": [
            {"kind": " NULL "},
            {"kind": " LITERAL ", "value": "x"},
        ],
    }]))

    value = arguments["items"][0]["value"]
    assert value["kind"] == "array"
    assert value["items"][0]["kind"] == "null"
    assert value["items"][1] == {"kind": "literal", "valueJson": '"x"'}

    service = _reflection_service()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
        arguments=[{
            "kind": " ARRAY ", "typeName": "System.String[]",
            "items": [{"kind": " NULL "}],
        }],
    ))
    assert result.ok
    wire_value = json.loads(service.dispatcher.calls[0][1]["argumentsJson"])["items"][0]["value"]
    assert wire_value["kind"] == "array"
    assert wire_value["items"][0]["kind"] == "null"


def test_csharp_eval_dispatches_once_with_normalized_contract():
    service = _service()
    result = asyncio.run(service.csharp_eval("return value + 1;", mode="statements", variables={"value": 2}))
    assert result.ok
    assert len(service.dispatcher.calls) == 1
    command, payload, _ = service.dispatcher.calls[0]
    assert command == "csharp.eval"
    assert payload["mode"] == "statements"
    assert json.loads(payload["variablesJson"])["items"][0]["name"] == "value"


def test_csharp_object_dump_forwards_text_and_reflection_options_once():
    service = _service()
    result = asyncio.run(service.csharp_object_dump(
        session_id="s.domain.id",
        handle="h.domain.object.id",
        include_type_names=True,
        expand_reflection_types=True,
    ))
    assert result.ok
    assert len(service.dispatcher.calls) == 1
    command, payload, _ = service.dispatcher.calls[0]
    assert command == "csharp.objectDump"
    assert payload["includeTypeNames"] is True
    assert payload["expandReflectionTypes"] is True


def test_csharp_object_dump_wrapper_forwards_text_and_reflection_options_once(monkeypatch):
    calls = []

    class _Facade:
        async def csharp_object_dump(self, **kwargs):
            calls.append(kwargs)
            return ok("req", {"received": kwargs})

    monkeypatch.setattr(execution_tools, "_get_facade", lambda: _Facade())
    monkeypatch.setattr(execution_tools, "_reject_write_if_unapproved", lambda _name: None)
    result = asyncio.run(execution_tools.csharp_object_dump(
        "s.domain.id",
        "h.domain.object.id",
        includeTypeNames=True,
        expandReflectionTypes=True,
    ))
    assert result.isError is False
    assert len(calls) == 1
    assert calls[0]["include_type_names"] is True
    assert calls[0]["expand_reflection_types"] is True


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
        "lastCompletedSpanJson": '{"start":0,"length":4}',
        "diagnosticsJson": '[{"code":"X"}]',
        "candidatesJson": '["A","B"]',
        "executionDiagnosticsJson": '{"getterCallCount":1,"methodCallCount":2}',
    })
    normalized = normalize_execution_error(response)
    assert normalized.error.detail["sourceSpan"]["line"] == 2
    assert normalized.error.detail["lastCompletedSpan"] == {"start": 0, "length": 4}
    assert normalized.error.detail["diagnostics"][0]["code"] == "X"
    assert normalized.error.detail["candidates"] == ["A", "B"]
    assert normalized.error.detail["executionDiagnostics"]["getterCallCount"] == 1


def _reflection_service():
    service = ReflectionDomainService()
    service.dispatcher = _Dispatcher()
    return service


def _typed_array_nesting(levels: int):
    value = {"kind": "literal", "value": 1}
    for _ in range(levels):
        value = {"kind": "array", "typeName": "System.Int32[]", "items": [value]}
    return value


def test_nested_typed_arrays_are_rejected_before_dispatch():
    service = _reflection_service()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
        arguments=[_typed_array_nesting(2)],
    ))
    assert not result.ok
    assert result.error.code == "INVALID_PARAMS"
    assert result.error.detail["path"] == "arguments[0].items[0].kind"
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert service.dispatcher.calls == []


def test_typed_json_uses_utf8_bytes_at_the_one_mib_boundary_including_multibyte_text():
    template = [{"kind": "literal", "value": ""}]
    base_bytes = len(normalize_arguments(template).encode("utf-8"))
    multibyte_count, remainder = divmod(1024 * 1024 - base_bytes, len("你".encode("utf-8")))
    text = "你" * multibyte_count + "a" * remainder
    arguments = [{"kind": "literal", "value": text}]
    assert len(normalize_arguments(arguments).encode("utf-8")) == 1024 * 1024

    service = _reflection_service()
    accepted = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0, arguments=arguments,
    ))
    assert accepted.ok
    assert len(service.dispatcher.calls) == 1

    service = _reflection_service()
    rejected = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
        arguments=[{"kind": "literal", "value": text + "你"}],
    ))
    assert not rejected.ok
    assert rejected.error.detail["path"] == "arguments"
    assert service.dispatcher.calls == []

    variable_template = {"message": ""}
    variable_base_bytes = len(normalize_variables(variable_template).encode("utf-8"))
    variable_count, variable_remainder = divmod(
        1024 * 1024 - variable_base_bytes, len("你".encode("utf-8")))
    variable_text = "你" * variable_count + "a" * variable_remainder
    variables = {"message": variable_text}
    assert len(normalize_variables(variables).encode("utf-8")) == 1024 * 1024

    service = _reflection_service()
    accepted = asyncio.run(service.reflection_call(
        expression="message", variables=variables,
    ))
    assert accepted.ok
    assert len(service.dispatcher.calls) == 1

    service = _reflection_service()
    rejected = asyncio.run(service.reflection_call(
        expression="message", variables={"message": variable_text + "你"},
    ))
    assert not rejected.ok
    assert rejected.error.detail["path"] == "variables"
    assert service.dispatcher.calls == []


def test_invalid_typed_value_kinds_and_array_wire_are_rejected_with_a_path_before_dispatch():
    for argument, expected_path in (
        ({"kind": "unknown"}, "arguments[0].kind"),
        ({"kind": "array", "typeName": "System.Int32[,]", "items": []}, "arguments[0].typeName"),
        ({"kind": "array", "typeName": "System.Int32[]", "items": {}}, "arguments[0].items"),
        ({"kind": "literal", "items": []}, "arguments[0].items"),
    ):
        service = _reflection_service()
        result = asyncio.run(service.reflection_call(
            type_name="Fixture", method_name="Run", async_after_sec=0, arguments=[argument],
        ))
        assert not result.ok
        assert result.error.detail["path"] == expected_path
        assert service.dispatcher.calls == []


def test_primitive_array_rejects_nonliteral_or_null_elements_before_dispatch():
    invalid_children = (
        ({"kind": "handle", "handle": "h.current"}, "arguments[0].items[0].kind"),
        ({"kind": "type", "typeName": "System.Int32"}, "arguments[0].items[0].kind"),
        ({"kind": "unityobject", "instanceId": 1}, "arguments[0].items[0].kind"),
        ({"kind": "array", "typeName": "System.Int32[]", "items": []}, "arguments[0].items[0].kind"),
        ({"kind": "null"}, "arguments[0].items[0]"),
        ({"kind": "literal", "valueJson": "null"}, "arguments[0].items[0]"),
    )
    for child, expected_path in invalid_children:
        service = _reflection_service()
        result = asyncio.run(service.reflection_call(
            type_name="Fixture", method_name="Run", async_after_sec=0,
            arguments=[{"kind": "array", "typeName": "System.Int32[]", "items": [child]}],
        ))
        assert not result.ok
        assert result.error.code == "INVALID_PARAMS"
        assert result.error.detail["path"] == expected_path
        assert result.error.detail["sideEffectsMayHaveOccurred"] is False
        assert service.dispatcher.calls == []


def test_supported_primitive_array_wire_preserves_values_without_server_side_evaluation():
    arrays = [
        {"kind": "array", "typeName": "System.String[]", "items": [
            {"kind": "literal", "typeName": "System.String", "value": "line\n你"},
            {"kind": "null"},
        ]},
        {"kind": "array", "typeName": "System.Int64[]", "items": [
            {"kind": "literal", "typeName": "System.Int64", "valueJson": "9223372036854775807"},
        ]},
        {"kind": "array", "typeName": "System.Decimal[]", "items": [
            {"kind": "literal", "typeName": "System.Decimal", "valueJson": "1234567890.123456789"},
        ]},
        {"kind": "array", "typeName": "System.Boolean[]", "items": [
            {"kind": "literal", "typeName": "System.Boolean", "value": True},
        ]},
        {"kind": "array", "typeName": "System.Char[]", "items": [
            {"kind": "literal", "typeName": "System.Char", "value": "Z"},
        ]},
    ]
    service = _reflection_service()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0, arguments=arrays,
    ))
    assert result.ok
    assert len(service.dispatcher.calls) == 1
    normalized = json.loads(service.dispatcher.calls[0][1]["argumentsJson"])
    assert normalized["items"][0]["value"]["items"][0]["valueJson"] == '"line\\n你"'
    assert normalized["items"][0]["value"]["items"][1]["kind"] == "null"
    assert normalized["items"][1]["value"]["items"][0]["valueJson"] == "9223372036854775807"
    assert normalized["items"][2]["value"]["items"][0]["valueJson"] == "1234567890.123456789"


def test_typed_handle_requires_a_session_and_preserves_expired_handle_failure_without_retry():
    service = _reflection_service()
    no_session = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
        arguments=[{"kind": "handle", "handle": "h.expired"}],
    ))
    assert not no_session.ok
    assert no_session.error.code == "SESSION_REQUIRED"
    assert service.dispatcher.calls == []

    class ExpiredHandleDispatcher(_Dispatcher):
        async def call(self, request_id, command, payload, timeout_ms=30000):
            self.calls.append((command, payload, timeout_ms))
            return fail(request_id, "HANDLE_EXPIRED", "The handle belongs to a prior domain.", {
                "commandId": request_id, "sideEffectsMayHaveOccurred": False,
            })

    service = ReflectionDomainService()
    service.dispatcher = ExpiredHandleDispatcher()
    expired = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", session_id="s.current", async_after_sec=0,
        arguments=[{"kind": "handle", "handle": "h.expired"}],
    ))
    assert not expired.ok and expired.error.code == "HANDLE_EXPIRED"
    assert expired.error.detail["sideEffectsMayHaveOccurred"] is False
    assert len(service.dispatcher.calls) == 1


def test_all_result_modes_dispatch_once_except_handle_without_session():
    for result_mode in ("auto", "inline", "handle", "legacyString"):
        for session_id in ("", "s.result"):
            service = _reflection_service()
            result = asyncio.run(service.reflection_call(
                type_name="Fixture", method_name="Run", session_id=session_id,
                result_mode=result_mode, async_after_sec=0,
            ))
            if result_mode == "handle" and not session_id:
                assert not result.ok and result.error.code == "SESSION_REQUIRED"
                assert service.dispatcher.calls == []
                continue
            assert result.ok
            assert len(service.dispatcher.calls) == 1
            payload = service.dispatcher.calls[0][1]
            assert payload.get("sessionId", "") == session_id
            if result_mode == "auto":
                assert "resultMode" not in payload
            else:
                assert payload["resultMode"] == result_mode.lower()


def test_reflection_expression_preserves_session_and_result_mode_when_forwarding_to_csharp_eval():
    service = _reflection_service()

    result = asyncio.run(service.reflection_call(
        expression="value",
        session_id="s.expression",
        variables={"value": {"kind": "handle", "handle": "h.expression.object"}},
        result_mode="handle",
    ))

    assert result.ok
    assert len(service.dispatcher.calls) == 1
    command, payload, _ = service.dispatcher.calls[0]
    assert command == "csharp.eval"
    assert payload["sessionId"] == "s.expression"
    assert payload["resultMode"] == "handle"
    assert payload["languageProfileMode"] == "reflection-expression"


def test_nonfinite_typed_values_and_encoding_domain_failures_are_not_retried():
    for argument in (
        {"kind": "literal", "value": float("nan")},
        {"kind": "literal", "typeName": "System.Double", "valueJson": "NaN"},
    ):
        service = _reflection_service()
        result = asyncio.run(service.reflection_call(
            type_name="Fixture", method_name="Run", async_after_sec=0, arguments=[argument],
        ))
        assert not result.ok
        assert result.error.detail["sideEffectsMayHaveOccurred"] is False
        assert service.dispatcher.calls == []

    class DomainInvalidatedDispatcher(_Dispatcher):
        async def call(self, request_id, command, payload, timeout_ms=30000):
            self.calls.append((command, payload, timeout_ms))
            return fail(request_id, "SESSION_DOMAIN_INVALIDATED", "Domain changed while encoding result.", {
                "commandId": request_id, "sideEffectsMayHaveOccurred": True,
            })

    service = ReflectionDomainService()
    service.dispatcher = DomainInvalidatedDispatcher()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", session_id="s.domain", async_after_sec=0,
        result_mode="handle",
    ))
    assert not result.ok and result.error.code == "SESSION_DOMAIN_INVALIDATED"
    assert result.error.detail["sideEffectsMayHaveOccurred"] is True
    assert len(service.dispatcher.calls) == 1


def test_csharp_eval_and_emit_typed_preflight_reject_without_unity_dispatch():
    invalid_csharp_requests = [
        {"variables": {"x": float("nan")}},
        {"variables": {"x": _typed_array_nesting(65)}},
        {"variables": {"target": {"kind": "handle", "handle": "h.expired"}}},
        {"limits": {"timeoutMs": "soon"}},
        {"result_mode": "handle"},
    ]
    for request in invalid_csharp_requests:
        service = _service()
        result = asyncio.run(service.csharp_eval("x", **request))
        assert not result.ok
        assert result.error.detail["sideEffectsMayHaveOccurred"] is False
        assert service.dispatcher.calls == []

    for session_id, constructor_arguments in (
        ("", []),
        ("s.domain", [{"kind": "literal", "value": float("inf")}]),
        ("s.domain", [_typed_array_nesting(65)]),
    ):
        service = _service()
        result = asyncio.run(service.reflection_emit_type(
            session_id,
            {"typeName": "Fixture.Dynamic"},
            constructor_arguments=constructor_arguments,
        ))
        assert not result.ok
        assert result.error.detail["sideEffectsMayHaveOccurred"] is False
        assert service.dispatcher.calls == []


def test_reflection_decorates_legacy_binder_diagnostics_without_losing_error_identity():
    class BinderDispatcher(_Dispatcher):
        async def call(self, request_id, command, payload, timeout_ms=30000):
            self.calls.append((command, payload, timeout_ms))
            return fail(request_id, "CSHARP_BIND_ERROR", "Type instance cannot use static members.", {
                "receiverKind": "typeInstance",
                "receiverType": "System.RuntimeType",
                "representedType": "Example.Window",
                "candidatesJson": "[]",
                "executionDiagnosticsJson": '{"resolveMs":1.25,"methodCallCount":0}',
            })

    service = ReflectionDomainService()
    service.dispatcher = BinderDispatcher()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
    ))

    assert not result.ok
    assert result.error.code == "CSHARP_BIND_ERROR"
    assert result.error.detail["receiverKind"] == "typeInstance"
    assert result.error.detail["candidates"] == []
    assert result.error.detail["executionDiagnostics"]["resolveMs"] == 1.25
    assert result.error.detail["executionKind"] == "method"
    assert len(service.dispatcher.calls) == 1
