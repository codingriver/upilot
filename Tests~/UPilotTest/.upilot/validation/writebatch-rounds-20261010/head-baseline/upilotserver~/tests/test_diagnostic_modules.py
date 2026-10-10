from __future__ import annotations

import asyncio
from copy import deepcopy
from dataclasses import asdict
from unittest.mock import Mock

import pytest

from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp import tool_registry
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.domain.execution_service import ExecutionDomainService
from upilot_mcp.mcp_tools import execution_tools
from upilot_mcp.responses import fail, ok
from upilot_mcp.tool_facade import McpToolFacade
from upilot_mcp.tool_registry import REGISTRY, dispatch_public_tool


class Dispatcher:
    def __init__(self, response=None):
        self.calls = []
        self.response = response

    async def call(self, request_id, command, payload, timeout_ms=30000):
        self.calls.append((command, deepcopy(payload), timeout_ms))
        return self.response if self.response is not None else ok(request_id, {"status": "ready"})


def service(response=None):
    instance = ExecutionDomainService()
    instance.dispatcher = Dispatcher(response)
    return instance


def mutation(**changes):
    return {"action": "compile", "session_id": "session.domain.id", "module_id": "probe",
            "source": "public static class Entry { }", "entry_type": "Entry",
            "expected_project_path": r"D:\upilot\Tests~\UPilotTest",
            "expected_domain_generation": "execution-domain", "expected_play_session_id": "play-session",
            "allow_full_csharp": True, **changes}


@pytest.mark.parametrize("action", ["compile", "run", "close"])
@pytest.mark.parametrize("opt_in", [False, "true", 1])
def test_mutations_require_explicit_opt_in_before_dispatch(action, opt_in):
    instance = service()
    result = asyncio.run(instance.diagnostic_module(**mutation(action=action, allow_full_csharp=opt_in)))
    assert result.error.code == "MODULE_OPT_IN_REQUIRED"
    assert instance.dispatcher.calls == []


@pytest.mark.parametrize("field", ["session_id", "module_id", "expected_project_path",
                                    "expected_domain_generation", "expected_play_session_id"])
def test_identity_is_not_inferred_from_server_or_current_session(field):
    instance = service()
    result = asyncio.run(instance.diagnostic_module(**mutation(**{field: ""})))
    assert result.error.code == "MODULE_IDENTITY_REQUIRED"
    assert instance.dispatcher.calls == []


@pytest.mark.parametrize("changes,code", [
    ({"action": "execute"}, "INVALID_MODULE_ACTION"),
    ({"action": []}, "INVALID_MODULE_ACTION"),
    ({"source": None}, "INVALID_MODULE_INPUT"),
    ({"source": ""}, "INVALID_MODULE_SOURCE"),
    ({"entry_type": " "}, "INVALID_MODULE_SOURCE"),
    ({"source": "x" * (1024 * 1024 + 1)}, "MODULE_INPUT_LIMIT"),
    ({"source": "\ud800"}, "INVALID_MODULE_INPUT"),
    ({"reference_type_names": "System.Object"}, "MODULE_REFERENCE_LIMIT"),
    ({"reference_type_names": ["System.Object"] * 33}, "MODULE_REFERENCE_LIMIT"),
    ({"reference_type_names": [" "]}, "MODULE_REFERENCE_LIMIT"),
    ({"reference_type_names": [None]}, "MODULE_REFERENCE_LIMIT"),
])
def test_invalid_inputs_do_not_reach_unity(changes, code):
    instance = service()
    result = asyncio.run(instance.diagnostic_module(**mutation(**changes)))
    assert result.error.code == code
    assert instance.dispatcher.calls == []


@pytest.mark.parametrize("args", ["{", "NaN", "Infinity", "1e400", '{"x": -Infinity}', "[" * 1100 + "]" * 1100])
def test_run_requires_finite_valid_json_before_dispatch(args):
    instance = service()
    result = asyncio.run(instance.diagnostic_module(**mutation(action="run", args_json=args)))
    assert result.error.code == "INVALID_MODULE_ARGUMENTS"
    assert instance.dispatcher.calls == []


def test_root_status_only_observes_and_does_not_open_a_session_or_compile():
    instance = service()
    result = asyncio.run(instance.diagnostic_module())
    assert result.ok
    assert len(instance.dispatcher.calls) == 1
    command, payload, timeout = instance.dispatcher.calls[0]
    assert command == "execution.diagnosticModule"
    assert payload["action"] == "status" and payload["sessionId"] == ""
    assert payload["allowFullCSharp"] is False
    assert timeout == 35000


def test_registry_and_actual_facade_keep_independent_write_gated_entry():
    descriptor = REGISTRY.resolve("diagnostic_module")
    assert descriptor.category == "execution"
    assert descriptor.destructive and descriptor.requires_write_access
    assert descriptor.idempotent is False and descriptor.play_mode_policy == "allowed"
    assert McpToolFacade.diagnostic_module is ExecutionDomainService.diagnostic_module
    tools = {tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())}
    schema = tools["diagnostic_module"].inputSchema["properties"]
    for field in ("action", "sessionId", "moduleId", "source", "entryType", "argsJson",
                  "expectedProjectPath", "expectedDomainGeneration", "expectedPlaySessionId",
                  "referenceTypeNames", "allowFullCSharp"):
        assert schema[field]["description"]
    assert "AssemblyBuilder" in tools["diagnostic_module"].description
    assert "C# 子集 V2" in tools["csharp_eval"].description
    assert "源码编译" in tools["reflection_emit_type"].description


class Transport:
    def __init__(self, *, fail_call=False):
        self.commands = []
        self.future = None
        self.fail_call = fail_call

    def is_ready(self):
        return True

    def register_pending(self, command_id):
        self.future = asyncio.get_running_loop().create_future()
        return self.future

    def unregister_pending(self, command_id):
        pass

    async def send_command(self, *, command_id, name, payload):
        self.commands.append((name, deepcopy(payload)))
        response = {"type": "result", "payload": {"status": "ready", "revision": 2,
                    "dllSha256": "candidate-hash", "cleanupErrors": ["old cleanup"],
                    "compilerStillRunning": False, "sideEffectsMayHaveOccurred": True}}
        if self.fail_call:
            response = {"type": "error", "payload": {"code": "MODULE_BUSY", "message": "observe original",
                        "detail": {"sideEffectsMayHaveOccurred": True, "nextAction": "Observe original ID"}}}
        self.future.set_result(response)


@pytest.mark.parametrize("route", ["native", "proxy"])
@pytest.mark.parametrize("failed", [False, True])
def test_actual_tool_model_facade_dispatcher_transmit_once_without_fallback(monkeypatch, route, failed):
    transport = Transport(fail_call=failed)
    instance = service()
    instance.dispatcher = CommandDispatcher(transport, Mock())
    monkeypatch.setattr(execution_tools, "_get_facade", lambda: instance)
    monkeypatch.setattr(execution_tools, "_reject_write_if_unapproved", lambda _name: None)
    # The proxy has its own write gate; authorize only this isolated test.
    monkeypatch.setattr(tool_registry, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(tool_registry.CONFIG, "write_access_approved", True)
    arguments = {"action": "run", "sessionId": "session.domain.id", "moduleId": "probe",
                 "argsJson": '{"counter": 1}', "expectedProjectPath": r"D:\upilot\Tests~\UPilotTest",
                 "expectedDomainGeneration": "execution-domain", "expectedPlaySessionId": "play-session",
                 "allowFullCSharp": True}
    original = deepcopy(arguments)
    if route == "native":
        result = asyncio.run(runtime.mcp._tool_manager.call_tool("diagnostic_module", arguments))
        public = result.structuredContent
    else:
        result = asyncio.run(dispatch_public_tool(instance, "diagnostic_module", arguments))
        public = asdict(result)
    assert len(transport.commands) == 1 and arguments == original, public
    command, payload = transport.commands[0]
    assert command == "execution.diagnosticModule" and payload["action"] == "run"
    for field in ("sessionId", "moduleId", "argsJson", "expectedProjectPath",
                  "expectedDomainGeneration", "expectedPlaySessionId", "allowFullCSharp"):
        assert payload[field] == arguments[field]
    if failed:
        assert public["error"]["code"] == "MODULE_BUSY"
        assert public["error"]["detail"]["sideEffectsMayHaveOccurred"] is True
    else:
        assert public["data"]["revision"] == 2
        assert public["data"]["cleanupErrors"] == ["old cleanup"]
        assert public["data"]["dllSha256"] == "candidate-hash"


def test_unapproved_tool_never_calls_facade(monkeypatch):
    calls = []
    monkeypatch.setattr(execution_tools, "_get_facade", lambda: calls.append("unexpected"))
    denied = runtime._payload(fail("req", "WRITE_NOT_APPROVED", "write denied"))
    monkeypatch.setattr(execution_tools, "_reject_write_if_unapproved", lambda name: denied)
    result = asyncio.run(execution_tools.diagnostic_module(action="compile", allowFullCSharp=True))
    assert result is denied and calls == []



def test_unapproved_proxy_never_dispatches_or_refreshes_project_config(monkeypatch):
    transport = Transport()
    instance = service()
    instance.dispatcher = CommandDispatcher(transport, Mock())
    monkeypatch.setattr(tool_registry, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(tool_registry.CONFIG, "write_access_approved", False)
    arguments = {"action": "run", "sessionId": "session.domain.id", "moduleId": "probe",
                 "argsJson": "{}", "expectedProjectPath": r"D:\upilot\Tests~\UPilotTest",
                 "expectedDomainGeneration": "execution-domain", "expectedPlaySessionId": "play-session",
                 "allowFullCSharp": True}
    original = deepcopy(arguments)
    result = asyncio.run(dispatch_public_tool(instance, "diagnostic_module", arguments))
    assert result.error.code == "WRITE_ACCESS_NOT_APPROVED"
    assert result.error.detail["tool"] == "diagnostic_module"
    assert transport.commands == [] and arguments == original

def test_unity_preflight_error_keeps_details_without_second_dispatch():
    response = fail("req", "MODULE_IDENTITY_MISMATCH", "exact identity required",
                    {"sideEffectsMayHaveOccurred": False, "nextAction": "Read status"})
    instance = service(response)
    result = asyncio.run(instance.diagnostic_module(**mutation()))
    assert result is response and len(instance.dispatcher.calls) == 1
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
