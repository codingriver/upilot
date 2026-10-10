import asyncio
import pytest

from upilot_mcp.domain.reflection_service import ReflectionDomainService
from upilot_mcp.responses import ok


class Dispatcher:
    def __init__(self):
        self.calls = []

    async def call(self, request_id, command, payload, **kwargs):
        self.calls.append((command, payload))
        return ok(request_id, {"value": 1})


@pytest.mark.parametrize("change", [
    {"await_mode": "none"}, {"result_mode": "bad"},
    {"result_mode": "handle"}, {"await_timeout_ms": float("nan")},
    {"operation_timeout_sec": float("inf")}, {"operation_timeout_sec": -1},
    {"async_after_sec": "later"}, {"options": {"timeoutMs": "soon"}},
    {"arguments": [{"kind": "invalid"}]},
    {"arguments": [{"direction": "invalid", "value": 1}]},
    {"arguments": [{"kind": "array", "items": {}}]},
    {"arguments": {}, "force_async": True},
    {"target_handle": "h.test", "target_instance_path": "Object"},
    {"arguments": [{"kind": "handle", "handle": ""}]},
    {"parameter_type_names": "System.Int32"},
    {"generic_type_arguments": [None]},
    {"is_static": "false"},
    {"options": {"resultMode": "bad"}},
    {"options": {"unknownBudget": 10}},
])
def test_invalid_request_never_dispatches(change):
    service = ReflectionDomainService()
    service.dispatcher = Dispatcher()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", **change))
    assert not result.ok
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert service.dispatcher.calls == []
    assert not getattr(service, "_reflection_background_jobs", {})


def test_valid_normalized_request_dispatches_once():
    service = ReflectionDomainService()
    service.dispatcher = Dispatcher()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", await_mode=" NEVER ",
        result_mode=" LegacyString ", async_after_sec=0))
    assert result.ok
    assert len(service.dispatcher.calls) == 1
    assert service.dispatcher.calls[0][1]["awaitMode"] == "never"


def test_literal_business_fields_are_not_execution_options():
    service = ReflectionDomainService()
    service.dispatcher = Dispatcher()
    result = asyncio.run(service.reflection_call(
        type_name="Fixture", method_name="Run", async_after_sec=0,
        arguments=[{"kind": "literal", "value": {"kind": "business", "direction": "north"}}]))
    assert result.ok
    assert len(service.dispatcher.calls) == 1


@pytest.mark.parametrize("change", [
    {"arguments": []}, {"parameter_type_names": ["System.Int32"]}, {"target_instance_path": "Object"},
    {"options": {"resultMode": False}}, {"options": {"timeoutMs": "soon"}},
])
def test_expression_preflight_does_not_silently_ignore_method_fields(change):
    service = ReflectionDomainService()
    service.dispatcher = Dispatcher()
    result = asyncio.run(service.reflection_call(expression="Fixture.Run()", **change))
    assert not result.ok
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert service.dispatcher.calls == []
