from __future__ import annotations

import asyncio
import json
from pathlib import Path

from upilot_mcp.domain.reflection_service import ReflectionDomainService
from upilot_mcp.responses import ok


class _Dispatcher:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict, int | None]] = []

    async def call(
        self,
        request_id: str,
        route: str,
        payload: dict,
        timeout_ms: int | None = None,
    ):
        self.calls.append((route, payload, timeout_ms))
        return ok(request_id, {"result": route})


def _service() -> tuple[ReflectionDomainService, _Dispatcher]:
    service = ReflectionDomainService.__new__(ReflectionDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher
    return service, dispatcher


def test_reflection_call_auto_routes_structured_method_once() -> None:
    service, dispatcher = _service()

    result = asyncio.run(
        service.reflection_call(
            type_name="Fixture.Service",
            method_name="Run",
            parameters=[1, {"enabled": True}],
            async_after_sec=0,
        )
    )

    assert result.ok and result.data
    assert result.data["executionKind"] == "method"
    assert result.data["engineUsed"] == "structured-reflection"
    assert dispatcher.calls == [
        (
            "reflection.call",
            {
                "typeName": "Fixture.Service",
                "methodName": "Run",
                "parameters": ["1", '{"enabled":true}'],
                "isStatic": True,
            },
            600000,
        )
    ]


def test_reflection_call_auto_routes_expression_once() -> None:
    service, dispatcher = _service()

    result = asyncio.run(
        service.reflection_call(
            expression="Fixture.Service.Value + offset",
            variables={"offset": 2},
            options={"resultMode": "json"},
        )
    )

    assert result.ok and result.data
    assert result.data["executionKind"] == "expression"
    assert result.data["engineUsed"] == "reflection-expression"
    assert len(dispatcher.calls) == 1
    command, payload, timeout = dispatcher.calls[0]
    assert command == "csharp.eval"
    assert timeout is None
    assert payload["code"] == "Fixture.Service.Value + offset"
    assert payload["mode"] == "expression"
    assert payload["executionBackend"] == "interpret"
    assert payload["languageProfileMode"] == "reflection-expression"
    assert json.loads(payload["variablesJson"])["items"][0]["name"] == "offset"


def test_reflection_call_rejects_ambiguous_shape_without_execution() -> None:
    service, dispatcher = _service()

    result = asyncio.run(
        service.reflection_call(
            type_name="Fixture.Service",
            method_name="Run",
            expression="Fixture.Service.Run()",
        )
    )

    assert result.ok is False
    assert result.error and result.error.code == "AMBIGUOUS_REFLECTION_REQUEST"
    assert dispatcher.calls == []


def test_reflection_call_rejects_incomplete_method_shape_without_execution() -> None:
    service, dispatcher = _service()

    result = asyncio.run(service.reflection_call(type_name="Fixture.Service"))

    assert result.ok is False
    assert result.error and result.error.code == "INVALID_REFLECTION_REQUEST"
    assert dispatcher.calls == []


def test_reflection_eval_external_surface_is_removed() -> None:
    from upilot_mcp.mcp_tools import reflection_tools
    from upilot_mcp.tool_registry import REGISTRY

    assert not hasattr(ReflectionDomainService, "reflection_eval")
    assert not hasattr(reflection_tools, "reflection_eval")
    assert REGISTRY.resolve("reflection_eval") is None
    assert all(
        item["name"] != "reflection_eval"
        for item in REGISTRY.find(query="reflection_eval", connected=True, server_ready=True)
    )


def test_reflection_eval_proxy_call_is_unknown_tool() -> None:
    from upilot_mcp.mcp_tools import reflection_tools  # noqa: F401
    from upilot_mcp.tool_registry import dispatch_public_tool

    result = asyncio.run(dispatch_public_tool(object(), "reflection_eval", {"code": "1 + 2"}))

    assert result.ok is False
    assert result.error and result.error.code == "UNKNOWN_TOOL"


def test_reflection_eval_e2e_action_and_assertion_are_removed() -> None:
    from upilot_mcp.editor_e2e.actions import run_action
    from upilot_mcp.editor_e2e.assertions import run_assert

    action = asyncio.run(run_action(object(), "reflection.eval", {"code": "1 + 2"}))
    assertion = asyncio.run(
        run_assert(
            object(),
            Path("."),
            {"type": "reflection.eval", "code": "1 + 2"},
            {},
            None,
            None,
            None,
        )
    )

    assert action.ok is False
    assert action.error and action.error.code == "E2E_UNKNOWN_ACTION"
    assert assertion[0] is False
    assert assertion[1] == "unknown assert type: reflection.eval"


def test_typed_reflection_call_forwards_expression_shape(monkeypatch) -> None:
    from upilot_mcp.mcp_tools import reflection_tools

    calls: list[dict] = []

    class _Facade:
        async def reflection_call(self, **kwargs):
            calls.append(kwargs)
            return ok("req-expression", {"result": "3"})

    monkeypatch.setattr(reflection_tools, "_get_facade", lambda: _Facade())
    monkeypatch.setattr(reflection_tools, "_reject_write_if_unapproved", lambda _name: None)

    result = asyncio.run(
        reflection_tools.unity_reflection_call(
            expression="1 + value",
            variables={"value": 2},
        )
    )

    assert result.isError is False
    assert calls == [
        {
            "type_name": "",
            "method_name": "",
            "parameters": None,
            "is_static": True,
            "target_instance_path": "",
            "target_static_type_name": "",
            "target_static_member_path": "",
            "async_after_sec": 25.0,
            "operation_timeout_sec": 600.0,
            "force_async": False,
            "expression": "1 + value",
            "variables": {"value": 2},
            "options": None,
            "kind": "auto",
        }
    ]


def test_mcp_tool_schema_does_not_register_reflection_eval() -> None:
    from upilot_mcp import mcp_stdio_server as runtime

    registered = asyncio.run(runtime._original_mcp_list_tools())
    visible = asyncio.run(runtime._list_tools_stable())

    assert "unity_reflection_call" in {tool.name for tool in registered}
    assert "reflection_eval" not in {tool.name for tool in registered}
    assert "reflection_eval" not in {tool.name for tool in visible}
