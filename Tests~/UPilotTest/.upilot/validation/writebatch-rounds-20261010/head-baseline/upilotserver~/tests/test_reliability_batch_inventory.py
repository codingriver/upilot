"""Targeted inventory completeness regressions and preserved reproduction shapes."""

import asyncio
from types import SimpleNamespace

import pytest

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



def _complete_inventory_service(tmp_path, monkeypatch):
    from upilot_mcp.protocol import now_ms

    service = Service(tmp_path)
    created = 134350644899879764
    service.server.session_manager.active.process_created_at = created
    monkeypatch.setattr(queue_service, "query_unity_processes", lambda: ([{
        "ProcessId": 123, "ExecutablePath": "C:/Unity/Unity.exe",
        "CommandLine": f'"C:/Unity/Unity.exe" -projectPath "{tmp_path}"',
        "ProcessCreatedAt": created // 10 * 10,
    }], {"processQuerySucceeded": True, "processQueryExitCode": 0}))
    monkeypatch.setattr(queue_service, "process_creation_time", lambda pid: created)
    snapshot = dict(complete=True, activeCount=0, queuedCount=0, executingCount=0,
                    untrackedCount=0, observedAt=now_ms(), activeCommands=[], queuedCommands=[],
                    serviceLifecycleId="inventory-fixture")
    calls = []

    async def dispatch(request, command, payload, **kwargs):
        calls.append(command)
        if command == "queue.snapshot":
            return ok(request, snapshot)
        if command == "console.capture.observe":
            return ok(request, {"activeCount": 0, "sessions": []})
        if command == "test.status":
            return ok(request, {"status": "idle"})
        raise AssertionError(f"Inventory attempted a non-observation command: {command}")

    service.dispatcher.call = dispatch
    return service, snapshot, calls


# Deliberately contradictory producer metadata must not override visible gaps.


@pytest.mark.parametrize("gap", ["producer_incomplete", "truncated", "untracked",
                                    "active_count", "queued_count", "missing_identity",
                                    "non_boolean_complete", "combined_limit"])
def test_inventory_bridge_gaps_cannot_claim_complete(tmp_path, monkeypatch, gap):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    if gap == "producer_incomplete":
        snapshot["complete"] = False
    elif gap == "truncated":
        snapshot["truncated"] = True
    elif gap == "untracked":
        snapshot["untrackedCount"] = 1
    elif gap == "active_count":
        snapshot["activeCount"] = 1
    elif gap == "queued_count":
        snapshot["queuedCount"] = 1
    elif gap == "missing_identity":
        snapshot.update(activeCount=1, activeCommands=[{}])
    elif gap == "non_boolean_complete":
        snapshot["complete"] = "true"
    else:
        snapshot.update(activeCount=101, queuedCount=100,
                        activeCommands=[{"commandId": f"active-{i}"} for i in range(101)],
                        queuedCommands=[{"commandId": f"queued-{i}"} for i in range(100)])
    result = asyncio.run(service.queue_inventory())
    assert result.ok
    data = result.data
    assert data["editorIdentity"]["status"] == "present", "Unrelated identity failure must not hide the gap."
    assert data["issues"] == ["BRIDGE_QUEUE_INCOMPLETE"]
    assert not data["complete"] and data["maintenanceRisk"] == "unknown"
    assert data["sources"]["bridgeCommands"] == "unknown"
    details = data["sourceDetails"]["bridgeCommands"]
    assert details["complete"] is False and details["queryResult"] == "incomplete"
    assert details["returnedCount"] == len([item for item in data["items"] if item["type"] == "BridgeCommand"])
    assert len(data["items"]) <= 200
    assert service.calls == []
    assert calls == ["queue.snapshot", "test.status", "console.capture.observe"]


def test_inventory_live_bridge_identity_survives_evicted_server_history(tmp_path, monkeypatch):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    snapshot.update(activeCount=1, activeCommands=[{"commandId": "evicted-active", "commandName": "fixture.work"}])
    # The server command ring no longer has this identity; the live Bridge still does.
    assert not service.server.state.commands
    result = asyncio.run(service.queue_inventory())
    assert result.ok and result.data["complete"]
    assert [item["id"] for item in result.data["items"]] == ["evicted-active"]
    assert result.data["items"][0]["type"] == "BridgeCommand"
    assert service.calls == [] and len(calls) == 3


def test_inventory_local_item_cap_reports_incomplete_without_writes(tmp_path, monkeypatch):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    service._async_tasks = {f"task-{i}": dict(taskId=f"task-{i}", status="running", terminal=False,
        durable=True, projectPath=str(tmp_path), runGuid=f"run-{i}") for i in range(501)}
    original = {key: dict(value) for key, value in service._async_tasks.items()}
    result = asyncio.run(service.queue_inventory())
    assert result.ok and not result.data["complete"]
    assert result.data["issues"] == ["QUEUE_ITEMS_TRUNCATED"]
    assert len(result.data["items"]) == 500
    assert not result.data["sourceDetails"]["serverMemory"]["complete"]
    assert result.data["sourceDetails"]["serverMemory"]["totalCount"] == 501
    assert service._async_tasks == original and service.calls == []
    assert len(calls) == 3


@pytest.mark.parametrize("placement", ["absent", "active", "queued"])
def test_inventory_keeps_executing_identity_without_duplicate(tmp_path, monkeypatch, placement):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    snapshot.update(executingCount=1, executingCommandId="executing-owned")
    if placement != "absent":
        snapshot[placement + "Count"] = 1
        snapshot[placement + "Commands"] = [{"commandId": "executing-owned"}]
    result = asyncio.run(service.queue_inventory())
    assert result.ok and result.data["complete"]
    assert [entry["id"] for entry in result.data["items"]] == ["executing-owned"]
    details = result.data["sourceDetails"]["bridgeCommands"]
    assert details["executingCommandId"] == "executing-owned"
    assert details["returnedCount"] == details["totalCount"] == 1
    assert service.calls == [] and calls == ["queue.snapshot", "test.status", "console.capture.observe"]


@pytest.mark.parametrize("count,identity", [(1, None), (1, ""), (1, 123), (2, "executing-owned")])
def test_inventory_executing_identity_gap_never_claims_idle(tmp_path, monkeypatch, count, identity):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    snapshot.update(executingCount=count, executingCommandId=identity)
    result = asyncio.run(service.queue_inventory())
    assert result.ok and not result.data["complete"]
    assert result.data["issues"] == ["BRIDGE_QUEUE_INCOMPLETE"]
    assert result.data["maintenanceRisk"] == "unknown"
    assert not result.data["sourceDetails"]["bridgeCommands"]["complete"]
    assert service.calls == [] and len(calls) == 3


def test_inventory_excluded_executing_observer_does_not_create_phantom_work(tmp_path, monkeypatch):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    # ObserveQueue retains executingCommandId even when excludeId suppresses its count.
    snapshot.update(executingCount=0, executingCommandId="excluded-observer")
    result = asyncio.run(service.queue_inventory())
    assert result.ok and result.data["complete"] and result.data["items"] == []
    assert result.data["sourceDetails"]["bridgeCommands"]["returnedCount"] == 0
    assert service.calls == [] and len(calls) == 3


@pytest.mark.parametrize("queued_count", [199, 200])
def test_inventory_executing_item_obeys_combined_output_cap(tmp_path, monkeypatch, queued_count):
    service, snapshot, calls = _complete_inventory_service(tmp_path, monkeypatch)
    snapshot.update(queuedCount=queued_count,
                    queuedCommands=[{"commandId": f"queued-{i}"} for i in range(queued_count)],
                    executingCount=1, executingCommandId="executing-owned")
    result = asyncio.run(service.queue_inventory())
    assert result.ok
    data = result.data
    details = data["sourceDetails"]["bridgeCommands"]
    assert details["totalCount"] == queued_count + 1 and details["returnedCount"] == 200
    assert len(data["items"]) == 200
    assert data["complete"] is (queued_count == 199)
    assert details["truncated"] is (queued_count == 200)
    if queued_count == 199:
        assert data["items"][-1]["id"] == "executing-owned"
        assert data["items"][-1]["status"] == "executing"
        assert data["issues"] == []
    else:
        assert data["issues"] == ["BRIDGE_QUEUE_INCOMPLETE"]
        assert data["maintenanceRisk"] == "unknown"
        assert details["executingCommandId"] == "executing-owned"
    assert service.calls == [] and len(calls) == 3
