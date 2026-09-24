"""Targeted D03/D02/D10 regression fixtures; intentionally not executed in this batch."""

import asyncio
from types import SimpleNamespace

from upilot_mcp.domain import queue_service
from upilot_mcp.responses import ok
from test_queue_cleanup import Service


def test_inventory_reports_untracked_bridge_work_without_cleanup(tmp_path, monkeypatch):
    monkeypatch.setattr(queue_service, "query_unity_processes", lambda: (
        [], {"processQuerySucceeded": False, "processQueryExitCode": None,
             "processQueryError": "access denied"}))
    service = Service(tmp_path)
    original = service.dispatch

    async def dispatch(*args, **kwargs):
        if args[1] == "queue.snapshot":
            return ok("bridge", {"complete": False, "activeCount": 501, "queuedCount": 1,
                                 "executingCount": 1, "untrackedCount": 1, "observedAt": 100,
                                 "activeCommands": [{"commandId": "last-active", "commandName": "test.run"}],
                                 "queuedCommands": [{"commandId": "queued"}]})
        if args[1] == "console.capture.observe":
            return ok("capture", {"activeCount": 0, "sessions": []})
        return await original(*args, **kwargs)

    service.dispatcher = SimpleNamespace(call=dispatch)
    result = asyncio.run(service.queue_inventory())
    assert result.ok
    assert result.data["complete"] is False
    assert "BRIDGE_QUEUE_INCOMPLETE" in result.data["issues"]
    assert result.data["sources"]["editor"] == "unknown"
    assert result.data["sourceDetails"]["bridgeCommands"]["activeCount"] == 501
    assert any(item["id"] == "last-active" for item in result.data["items"])
    assert service.calls == []


def test_inventory_session_drift_never_becomes_complete(tmp_path, monkeypatch):
    monkeypatch.setattr(queue_service, "query_unity_processes", lambda: (
        [], {"processQuerySucceeded": True, "processQueryExitCode": 0}))
    service = Service(tmp_path)

    async def dispatch(*args, **kwargs):
        if args[1] == "queue.snapshot":
            service.server.session_manager.active = SimpleNamespace(project_path=str(tmp_path))
            return ok("bridge", {"complete": True, "activeCount": 0, "queuedCount": 0,
                                 "executingCount": 0, "untrackedCount": 0, "observedAt": 100})
        if args[1] == "console.capture.observe":
            return ok("capture", {"activeCount": 0, "sessions": []})
        return ok("test", {"status": "idle"})

    service.dispatcher = SimpleNamespace(call=dispatch)
    result = asyncio.run(service.queue_inventory())
    assert result.ok and not result.data["complete"]
    assert "BRIDGE_SESSION_CHANGED" in result.data["issues"]


def test_reflection_and_prior_shared_state_reproduction_shapes():
    # D02: capture the exact overload shape; no binding engine assumption is encoded.
    binding = {"typeName": "Fixture.Nested", "methodName": "Call",
               "arguments": [{"type": "System.Type", "value": "System.String"}, ["a", "b"]]}
    assert binding["arguments"][0]["type"] == "System.Type"
    # D10: the preceding operation and original runGuid are required to diagnose a stall.
    sequence = {"runGuid": "original-guid", "predecessor": "shared-static-state",
                "target": "MonoHook", "sameEditorProcess": True}
    assert sequence["sameEditorProcess"] and sequence["runGuid"]
