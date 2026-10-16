from __future__ import annotations

import asyncio
from types import SimpleNamespace

import pytest

from upilot_mcp.responses import fail, ok


@pytest.mark.parametrize("tool", ["unity_asset_refresh", "unity_sync_after_disk_write"])
def test_global_refresh_precheck_blocks_play_without_dispatch(monkeypatch, tool):
    from upilot_mcp import mcp_stdio_server as runtime
    from upilot_mcp.mcp_tools import resource_tools

    async def playing():
        return True

    monkeypatch.setattr(runtime, "_facade", None)
    monkeypatch.setattr(runtime, "_unity_is_playmode", playing)
    monkeypatch.setattr(resource_tools, "_get_facade", lambda: pytest.fail("Blocked refresh dispatched"))
    call = resource_tools.unity_asset_refresh() if tool == "unity_asset_refresh" else resource_tools.unity_sync_after_disk_write(triggerCompile=False)
    result = asyncio.run(call)
    assert result.structuredContent["error"]["code"] == "EDITOR_IN_PLAY_MODE"


def test_refresh_only_does_not_use_compile_recovery_bypass(monkeypatch):
    from upilot_mcp import mcp_stdio_server as runtime

    execution = {"compilePhase": "domain_reload", "playModeState": "unknown", "authoritative": False, "isStale": True}
    facade = SimpleNamespace(server=SimpleNamespace(state=SimpleNamespace(execution_state=lambda **_: execution)))

    async def not_playing():
        return False

    monkeypatch.setattr(runtime, "_facade", facade)
    monkeypatch.setattr(runtime, "_unity_is_playmode", not_playing)
    monkeypatch.setattr(runtime, "_response_context", lambda: execution)
    result = asyncio.run(runtime._reject_compile_in_playmode("unity_sync_after_disk_write", allow_compile_recovery=False))
    assert result.structuredContent["error"]["code"] == "EDITOR_CONTEXT_NOT_READY"
    assert asyncio.run(runtime._reject_compile_in_playmode("unity_sync_after_disk_write")) is None


def test_sync_preserves_final_bridge_race_refusal():
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    async def refresh():
        return fail("req-race", "EDITOR_IN_PLAY_MODE", "actual=enteringPlay", {"blockedReason": "enteringPlay"})

    service.asset_refresh = refresh
    result = asyncio.run(service.sync_after_disk_write(delay_s=0, trigger_compile=False))
    assert not result.ok
    assert result.error.code == "EDITOR_IN_PLAY_MODE"
    assert result.error.detail["refreshed"] is False
    assert result.error.detail["blockedReason"] == "enteringPlay"
    assert result.error.detail["status"] == "blocked"


@pytest.mark.parametrize("operation", ["create", "update", "delete"])
def test_script_mutations_preserve_deferred_disk_evidence(operation):
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)
    service._reject_write_if_unapproved = lambda *_: None
    deleted = ["Assets/Test.cs", "Assets/Test.cs.meta"] if operation == "delete" else []
    data = {"ok": True, "saved": True, "imported": False, "importDeferred": True, "deletedPaths": deleted,
            "projectFilesSynchronized": False}

    async def dispatch(request_id, command, payload):
        assert command == "script." + operation
        assert payload["scriptPath"] == "Assets/Test.cs"
        return ok(request_id, data)

    service.dispatcher = SimpleNamespace(call=dispatch)
    call = getattr(service, "script_" + operation)
    result = asyncio.run(call("Assets/Test.cs") if operation == "delete" else call("Assets/Test.cs", "test"))
    assert result.ok and result.data == data
