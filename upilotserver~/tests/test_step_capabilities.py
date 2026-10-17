import asyncio
import copy
from types import SimpleNamespace

import pytest
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain import status_service
from upilot_mcp.responses import fail, ok

CONTRACT = {"discoveryVersion": 1, "supportedPlanVersions": [1], "callbackContract": "jsonString",
            "captureOwnershipModes": ["planOwned", "operationBorrowed"]}

def service(payload=None):
    instance = StatusDomainService()
    state = {"sessionId": "bridge-a", "producerEpoch": "epoch-a", "domainGeneration": 4}
    session = SimpleNamespace(session_id="bridge-a", project_path="D:/UPilotTest", process_id=123)
    ready = [True]
    instance.server = SimpleNamespace(is_ready=lambda: ready[0], session_manager=SimpleNamespace(active=session),
        state=SimpleNamespace(execution_state=lambda: dict(state)))
    calls = []
    response = {"payload": {"automationSteps": copy.deepcopy(CONTRACT)} if payload is None else payload, "effect": None}
    async def dispatch(request_id, route, args, **kwargs):
        calls.append((route, args, kwargs))
        assert (route, args, kwargs) == ("capabilities.list", {}, {"timeout_ms": 5000})
        if response["effect"]: response["effect"]()
        reply = response["payload"]
        return reply if hasattr(reply, "ok") else ok(request_id, reply)
    async def status(**kwargs):
        return ok("status", {"capabilities": {"registeredToolCount": 1}, "session": {}, "paths": {}})
    async def execution(): return ok("execution", {"available": True})
    instance.dispatcher = SimpleNamespace(call=dispatch)
    instance.mcp_status = status
    instance.execution_capabilities = execution
    instance._registry_tools_snapshot = lambda **kwargs: [{"name": "unity_operation_start", "callableNow": True}]
    return instance, state, ready, calls, response

def test_read_once_detached_no_cache():
    instance, _, _, calls, response = service()
    first = asyncio.run(instance.capabilities_get())
    item = first.data["automationSteps"]
    assert first.ok and item["observationStatus"] == "observed"
    assert item["observedAtUtcMs"] > 0 and item["contract"] == CONTRACT
    assert item["identity"]["producerEpoch"] == "epoch-a"
    response["payload"]["automationSteps"]["supportedPlanVersions"].append(2)
    assert item["contract"]["supportedPlanVersions"] == [1]
    assert asyncio.run(instance.capabilities_get()).data["automationSteps"]["contract"]["supportedPlanVersions"] == [1, 2]
    assert len(calls) == 2

def test_old_bridge_unreported():
    instance, _, _, calls, _ = service({"commands": []})
    result = asyncio.run(instance.capabilities_get())
    assert result.ok and result.data["automationSteps"]["observationStatus"] == "unreported"
    assert result.data["tools"][0]["callableNow"] is True and len(calls) == 1

def test_disconnected_no_dispatch():
    instance, _, ready, calls, _ = service()
    ready[0] = False
    assert asyncio.run(instance.capabilities_get()).data["automationSteps"]["observationStatus"] == "unavailable"
    assert calls == []

@pytest.mark.parametrize("change", [{"discoveryVersion": 1.0}, {"discoveryVersion": True}, {"discoveryVersion": 2},
    {"supportedPlanVersions": []}, {"supportedPlanVersions": [1, 1]}, {"supportedPlanVersions": [True]},
    {"callbackContract": ""}, {"captureOwnershipModes": [None]}])
def test_malformed_contract(change):
    instance, _, _, calls, _ = service({"automationSteps": {**CONTRACT, **change}})
    item = asyncio.run(instance.capabilities_get()).data["automationSteps"]
    assert item["observationStatus"] == "unknown" and item["contract"] is None
    assert item["diagnostic"] == "STEP_CAPABILITY_CONTRACT_INVALID" and len(calls) == 1

@pytest.mark.parametrize("changed", ["session", "session_id", "path", "process", "epoch", "domain", "disconnect"])
def test_identity_change(changed):
    instance, state, ready, calls, response = service()
    def effect():
        current = instance.server.session_manager.active
        if changed == "session": instance.server.session_manager.active = copy.copy(current)
        elif changed == "session_id": current.session_id = "bridge-b"
        elif changed == "path": current.project_path = "D:/Other"
        elif changed == "process": current.process_id += 1
        elif changed == "epoch": state["producerEpoch"] = "epoch-b"
        elif changed == "domain": state["domainGeneration"] += 1
        else: ready[0] = False
    response["effect"] = effect
    item = asyncio.run(instance.capabilities_get()).data["automationSteps"]
    assert item["observationStatus"] == "unknown" and item["contract"] is None
    assert item["diagnostic"] == "STEP_CAPABILITY_IDENTITY_CHANGED" and len(calls) == 1

@pytest.mark.parametrize("kind", ["timeout", "exception", "transport", "malformed"])
def test_query_failure(kind):
    instance, _, _, calls, response = service()
    if kind == "transport": response["payload"] = fail("probe", "BRIDGE_DISCONNECTED", "Disconnected.")
    elif kind == "malformed": response["payload"] = []
    else:
        def effect(): raise asyncio.TimeoutError() if kind == "timeout" else RuntimeError("observation failure")
        response["effect"] = effect
    result = asyncio.run(instance.capabilities_get())
    assert result.ok and result.data["automationSteps"]["observationStatus"] == "unknown"
    assert result.data["automationSteps"]["contract"] is None and len(calls) == 1

@pytest.mark.parametrize("after", [False, True])
def test_identity_read_exception(after):
    instance, _, _, calls, response = service()
    def broken(): raise RuntimeError("identity unavailable")
    if after: response["effect"] = lambda: setattr(instance.server.state, "execution_state", broken)
    else: instance.server.state.execution_state = broken
    result = asyncio.run(instance.capabilities_get())
    item = result.data["automationSteps"]
    assert result.ok and item["observationStatus"] == "unknown" and item["contract"] is None
    assert item["diagnostic"] == "STEP_CAPABILITY_IDENTITY_UNKNOWN" and len(calls) == int(after)

@pytest.mark.parametrize("change", [{"sessionId": "other"}, {"producerEpoch": ""}, {"domainGeneration": None},
                                       {"domainGeneration": True}, {"domainGeneration": -1}])
def test_incomplete_identity(change):
    instance, state, _, calls, _ = service()
    state.update(change)
    item = asyncio.run(instance.capabilities_get()).data["automationSteps"]
    assert item["observationStatus"] == "unknown" and item["contract"] is None
    assert item["diagnostic"] == "STEP_CAPABILITY_IDENTITY_UNKNOWN" and len(calls) == 1

def test_outer_five_second_budget(monkeypatch):
    instance, _, _, calls, _ = service()
    budgets, canceled = [], []
    original = asyncio.wait_for
    async def pending(request_id, route, args, **kwargs):
        calls.append((route, args, kwargs))
        try: await asyncio.Event().wait()
        finally: canceled.append(True)
    async def shortened(awaitable, timeout):
        budgets.append(timeout)
        return await original(awaitable, timeout=0.001)
    instance.dispatcher.call = pending
    monkeypatch.setattr(status_service.asyncio, "wait_for", shortened)
    item = asyncio.run(instance.capabilities_get()).data["automationSteps"]
    assert item["diagnostic"] == "STEP_CAPABILITY_QUERY_TIMEOUT"
    assert budgets == [5.0] and canceled == [True] and len(calls) == 1
