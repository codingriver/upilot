import asyncio
import json
from types import SimpleNamespace

import pytest

from upilot_mcp.client_probe import DiscoveryProxyClient, _payload, probe, probe_session


class Session:
    def __init__(self, project, visible=True):
        self.project, self.visible, self.calls = project, visible, []

    async def initialize(self):
        pass

    async def list_tools(self, cursor=None):
        return SimpleNamespace(tools=[SimpleNamespace(name=name) for name in
                                     (DiscoveryProxyClient.exposed_tools if self.visible else [])], nextCursor=None)

    async def call_tool(self, name, args):
        self.calls.append((name, args))
        return SimpleNamespace(isError=False, content=[], structuredContent={
            "ok": True, "data": {"connected": True, "serverReady": True,
                                 "paths": {"unityProjectAbsolute": self.project}},
        })


def test_probe_uses_only_two_entrypoints_and_does_not_infer_client_injection(tmp_path):
    session = Session(str(tmp_path))
    result = asyncio.run(probe_session(session, str(tmp_path), {"clientToolListInjected": "unknown"}))
    assert result["passed"] and result["clientToolListInjected"] == "unknown"
    assert [name for name, _ in session.calls] == ["unity_tools_find", "unity_tool_call"]
    with pytest.raises(ValueError):
        asyncio.run(DiscoveryProxyClient(session).call("unity_mcp_status", {}))


def test_wrong_project_never_passes(tmp_path):
    result = asyncio.run(probe_session(Session(str(tmp_path / "other")), str(tmp_path), {}))
    assert result["actualCallSucceeded"] and not result["passed"]


def test_missing_tools_fail_before_any_proxy_call(tmp_path):
    session = Session(str(tmp_path), False)
    with pytest.raises(ValueError):
        asyncio.run(probe_session(session, str(tmp_path), {}))
    assert not session.calls


def test_internal_transport_is_rejected_without_connecting(tmp_path):
    result = asyncio.run(probe("ws://127.0.0.1:8765", str(tmp_path)))
    assert not result["httpConnected"] and not result["passed"]


@pytest.mark.parametrize("structured", [True, False])
def test_proxy_preserves_business_error_and_recovery_fields(structured):
    envelope = {"ok": False, "context": {"padding": "x" * 4000}, "error": {
        "code": "INVALID_TOOL_ARGUMENTS", "detail": {"nextAction": "Refresh this server.",
                                                  "unknownArguments": ["categories"]}}}
    result = SimpleNamespace(isError=True, structuredContent=envelope if structured else None,
                             content=[] if structured else [SimpleNamespace(type="text", text=json.dumps(envelope))])
    assert _payload(result) == envelope


def test_proxy_plain_protocol_error_does_not_become_success():
    with pytest.raises(ValueError, match="protocol error"):
        _payload(SimpleNamespace(isError=True, structuredContent=None,
                                 content=[SimpleNamespace(type="text", text="Transport unavailable")]))
