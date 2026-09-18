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
    legacy = asyncio.run(service.profiler_capture_start(capture_mode="legacy", include_default_ai_markers=False))
    assert legacy.data["captureMode"] == "legacy" and legacy.data["includeDefaultAiMarkers"] is False
    assert not asyncio.run(service.profiler_capture_start(capture_mode="wrong")).ok
