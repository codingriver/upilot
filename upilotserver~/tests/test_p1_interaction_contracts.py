import asyncio
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.responses import ok


class Service(StatusDomainService, ResourceDomainService, ProjectAnalysisDomainService):
    def __init__(self):
        self.calls = []
        self.dispatcher = SimpleNamespace(call=self.call)

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, payload))
        return ok(request_id, payload)

    def _reject_write_if_unapproved(self, *_):
        return None


def test_close_defaults_to_user_protocol_and_forwards_exact_identity():
    service = Service()
    result = asyncio.run(service.editor_window_close(instance_id="123"))
    assert result.data["instanceId"] == "123"
    assert result.data["closeMode"] == "requestUserClose"
    result = asyncio.run(service.editor_window_close(instance_id="123", close_mode="forceClose"))
    assert result.data["closeMode"] == "forceClose"
    failed = asyncio.run(service.editor_window_close(instance_id="123", close_mode="discard"))
    assert not failed.ok and len(service.calls) == 2


def test_close_forwards_optional_domain_generation():
    service = Service()
    async def windows_list():
        return ok("req-list", {"windows": [{"instanceId": "123", "domainGeneration": "44"}]})
    service.editor_windows_list = windows_list
    result = asyncio.run(service.editor_window_close(instance_id="123", domain_generation="44"))

    assert result.ok
    assert service.calls == [(
        "editor.window.close",
        {
            "windowTitle": "", "matchMode": "exact", "instanceId": "123",
            "domainGeneration": "44", "closeMode": "requestUserClose",
        },
    )]


@pytest.mark.parametrize("method,key", [("menu_execute", "menu_path"), ("editor_execute_command", "command_name")])
def test_both_menu_entries_forward_expected_modal(method, key):
    service = Service()
    expected = {"title": "Fixture", "buttons": ["OK", "Cancel"], "clickButton": "OK"}
    result = asyncio.run(getattr(service, method)(**{key: "Fixture/Test"}, expected_modal=expected))
    assert result.data["expectedModal"] == expected


def test_direct_input_escape_is_explicit():
    service = Service()
    normal = asyncio.run(service.keyboard_event("keypress", "Fixture", key_code="Return"))
    assert normal.data["escapeGenericMenu"] is False
    declared = asyncio.run(service.mouse_event("click", "right", 1, 2, "Fixture", escape_generic_menu=True))
    assert declared.data["escapeGenericMenu"] is True


def test_profiler_defaults_and_explicit_legacy_mode_are_forwarded():
    service = Service()
    default = asyncio.run(service.profiler_capture_start())
    assert default.data["captureMode"] == "lowOverhead"
    assert default.data["maxSamples"] == 4096
    assert default.data["useConfiguredDuration"] is True
    assert default.data["durationSec"] == 30.0
    legacy = asyncio.run(service.profiler_capture_start(capture_mode="legacy", include_default_ai_markers=False))
    assert legacy.data["captureMode"] == "legacy" and legacy.data["includeDefaultAiMarkers"] is False
    assert not asyncio.run(service.profiler_capture_start(capture_mode="wrong")).ok


@pytest.mark.parametrize("route,dispatched", [("uitoolkit", True), ("imgui", True), ("uitoolkit", False)])
def test_mouse_public_tool_dispatcher_roundtrip_preserves_receipt(monkeypatch, route, dispatched):
    from unittest.mock import Mock
    from upilot_mcp.dispatcher import CommandDispatcher
    from upilot_mcp.mcp_tools import status_tools

    receipt = {"ok": dispatched, "state": "click:Fixture:" + route, "input": {
        "windowInstanceId": "123", "dispatchRoute": route, "dispatched": dispatched,
        "businessEffectVerified": False, "hitVerified": True,
        "hitType": "fixture", "hitName": "counter", "position": {"x": 15, "y": 30},
        "eventConsumption": "unobserved"}}

    class Transport:
        def is_ready(self): return True
        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future
        async def send_command(self, *, command_id, name, payload):
            self.name, self.payload = name, payload
            self.future.set_result({"type": "result", "payload": receipt})

    transport = Transport()
    service = Service()
    service.dispatcher = CommandDispatcher(transport, Mock())
    monkeypatch.setattr(status_tools, "_get_facade", lambda: service)
    monkeypatch.setattr(status_tools, "_reject_write_if_unapproved", lambda _: None)
    result = asyncio.run(status_tools.unity_mouse_event(
        action="click", button="left", x=15, y=30, targetWindow="Fixture", windowInstanceId="123"))
    assert transport.name == "mouse.event"
    assert str(transport.payload["windowInstanceId"]) == "123"
    assert result.structuredContent["data"] == receipt


@pytest.mark.parametrize("receipt", [
    {"menuPath": "Fixture/Menu", "executed": True},
    {"menuPath": "Fixture/Menu", "executed": False},
    {"commandId": "menu-pending", "waitingForModalUi": True, "terminal": False},
])
def test_menu_public_dispatcher_roundtrip_preserves_receipt_once(monkeypatch, receipt):
    from unittest.mock import Mock
    from upilot_mcp.dispatcher import CommandDispatcher
    from upilot_mcp.mcp_tools import resource_tools

    class Transport:
        def __init__(self): self.calls = []
        def is_ready(self): return True
        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future
        async def send_command(self, *, command_id, name, payload):
            self.calls.append((name, payload))
            self.future.set_result({"type": "result", "payload": receipt})

    transport = Transport()
    service = Service()
    service.dispatcher = CommandDispatcher(transport, Mock())
    monkeypatch.setattr(resource_tools, "_get_facade", lambda: service)
    result = asyncio.run(resource_tools.unity_menu_execute(menuPath="Fixture/Menu"))
    assert transport.calls == [("menu.execute", {"menuPath": "Fixture/Menu"})]
    assert result.structuredContent["data"] == receipt
