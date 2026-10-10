"""Official Step admission is enabled; legacy callbacks and Task wrapping remain suspended."""
import asyncio
import copy

import pytest

from upilot_mcp import mcp_stdio_server as runtime  # Populate the real public registry.
from upilot_mcp.config import CONFIG
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.tool_registry import (REGISTRY, GENERIC_ORCHESTRATION_DISABLED,
    GENERIC_ORCHESTRATION_REASON, dispatch_public_tool, disabled_orchestration_target)
from test_operation_runner_and_agent_rules import _OperationService
from test_operation_cleanup_barriers import spec
from test_persistent_operations import stop_observers
from upilot_mcp.responses import ok, fail
from types import SimpleNamespace
import json


@pytest.fixture(autouse=True)
def isolated_write_grant(monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    monkeypatch.setattr("upilot_mcp.domain.task_service.refresh_config_if_changed", lambda: None)
    monkeypatch.setattr("upilot_mcp.tool_registry.refresh_config_if_changed", lambda: None)


def rejected(response):
    assert not response.ok
    assert response.error.code == GENERIC_ORCHESTRATION_DISABLED
    assert response.error.message == GENERIC_ORCHESTRATION_REASON
    assert "dedicated" in response.error.detail["nextAction"]


@pytest.mark.parametrize("method", ["operation_start", "operation_validate"])
@pytest.mark.parametrize("job", [None, {},
    {"startCall": {"typeName": "Project.Business", "methodName": "Start"},
     "statusCall": {}, "consoleCapture": {"enabled": True}}])
def test_production_gate_needs_no_store_bridge_capture_or_observer(method, job):
    # Any attempt to recover records or allocate work fails on this deliberately bare service.
    service = TaskDomainService()
    before = copy.deepcopy(job)
    rejected(asyncio.run(getattr(service, method)(job)))
    assert service.__dict__ == {} and job == before


WRAPPED = [
    ("unity_operation_start", {"jobSpec": {}}),
    ("unity_operation_validate", {"jobSpec": {}}),
    ("unity_tool_call", {"toolName": "unity_operation_start", "args": {"jobSpec": {}}}),
    ("unity_task_start", {"taskName": "nested", "toolName": "unity_operation_start", "toolArgs": {"jobSpec": {}}}),
    ("unity_task_execute", {"task_name": "nested", "tool_name": "unity_operation_validate", "tool_args": {"jobSpec": {}}}),
    ("unity_tool_call", {"toolName": "unity_task_execute", "args": {"taskName": "nested",
        "toolName": "unity_tool_call", "toolArgs": {"toolName": "unity_operation_start", "args": {"jobSpec": {}}}}}),
]


@pytest.mark.parametrize("tool,args", WRAPPED)
@pytest.mark.parametrize("approved", [True, False])
def test_proxy_refuses_known_wrappers_before_dispatch_or_permissions(tool, args, approved, monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", approved)
    # No facade implementation: dispatch must not get as far as calling a handler.
    rejected(asyncio.run(dispatch_public_tool(object(), tool, args)))


@pytest.mark.parametrize("tool,args", WRAPPED)
@pytest.mark.parametrize("method", ["task_start", "task_execute"])
def test_task_wrapper_does_not_allocate_retry_or_restart(method, tool, args):
    service = TaskDomainService()
    kwargs = {"task_name": "must not allocate", "tool_name": tool, "tool_args": args, "retry_count": 10}
    if method == "task_execute":
        kwargs["restart_unity_on_timeout"] = True
    rejected(asyncio.run(getattr(service, method)(**kwargs)))
    assert service.__dict__ == {}


@pytest.mark.parametrize("flow", [False, True])
@pytest.mark.parametrize("connected,approved", [(True, True), (True, False), (False, True)])
def test_capabilities_enable_official_steps_but_preserve_readiness_and_write_gates(flow, connected, approved):
    def item_for(name):
        # Discovery is deliberately bounded to 200 items; query the exact route
        # instead of treating a truncated global inventory as a missing tool.
        return next(x for x in REGISTRY.find(query=name, flow_enabled=flow,
            connected=connected, server_ready=connected, write_access_approved=approved)
            if x["name"] == name)
    for name in ("unity_operation_start", "unity_operation_validate"):
        item = item_for(name)
        assert item["registered"] and item["available"]
        assert item["callableNow"] == (connected and (approved or name == "unity_operation_validate"))
        if not connected:
            assert item["unavailableReason"] == "UNITY_NOT_CONNECTED"
        elif name == "unity_operation_start" and not approved:
            assert item["unavailableReason"] == "WRITE_ACCESS_NOT_APPROVED"
    for name in ("unity_operation_status", "unity_operation_wait", "unity_operation_cancel",
        "unity_operation_collect_artifacts", "unity_queue_cleanup", "unity_test_run", "unity_test_status",
        "unity_upilot_acceptance_run", "unity_write_batch_register", "unity_compile",
        "unity_console_capture_start", "unity_snapshot_capture", "unity_build_start"):
        assert item_for(name)["registered"] and item_for(name)["available"]
        assert not disabled_orchestration_target(name)


def test_structured_resolver_never_inspects_code_or_loops():
    request = {"toolName": "unity_tool_call"}
    request["args"] = request
    assert not disabled_orchestration_target("unity_tool_call", request)
    assert not disabled_orchestration_target("unity_reflection_call", {"expression": "StartJson()"})


@pytest.mark.parametrize("ended_at", [0, 123])
def test_unknown_start_terminates_aborted_without_replay_capture_or_deadline_renewal(tmp_path, ended_at):
    async def run():
        service = _OperationService(tmp_path, [])
        created = await service.seed_legacy_operation(spec())
        await stop_observers(service)
        state = service._operations[created.data["operationId"]]
        state.update(status="RecoveryRequired", phase="StartIdentityUnknown", endedAt=ended_at,
            cleanupPending=True, error="original evidence is missing", startIdentityState="unknown")
        # The bounded-lifetime contract archives unknown starts as aborted,
        # never a permanently active RecoveryRequired entry. Preserve the old
        # no-replay/no-Capture-side-effect guarantees while asserting termination.
        original_deadline = state.get("jobTimeoutAt")
        original_attempts = state.get("startAttemptCount")
        assert service._save_operation(state)
        assert state["status"] == "aborted" and state["terminal"]
        assert state["endedAt"] > 0 and not state["cleanupPending"]
        assert not state["cleanupSucceeded"]
        before = copy.deepcopy(state)
        service.calls.clear()
        async def forbidden(*a, **kw):
            raise AssertionError("observation must not replay work or acquire/read/stop Capture")
        service.console_capture_start = forbidden
        service.console_capture_read = forbidden
        service.console_capture_stop = forbidden
        service.operation_cancel = forbidden
        result = await asyncio.wait_for(service.operation_wait(state["operationId"], timeout_s=60), 1)
        assert result.ok and result.data["status"] == "aborted"
        assert result.data["terminal"] and not result.data.get("recoveryBlocked")
        assert not result.data.get("waitWindowElapsed")
        assert state.get("jobTimeoutAt") == original_deadline
        assert state.get("startAttemptCount") == original_attempts
        for key in ("operationId", "status", "endedAt", "cleanupPending", "error", "startAttemptCount", "jobTimeoutAt"):
            assert state.get(key) == before.get(key)
        assert not service.calls
        await stop_observers(service)
    asyncio.run(run())


def step_spec():
    return {"stepPlan": {"version": 1, "steps": [
        {"instanceId": "wait", "stepId": "upilot.wait_seconds", "arguments": "0"}]},
        "consoleCapture": {"enabled": False}, "timeoutSec": 300}


class StepService(_OperationService):
    """Fake Bridge only; exercise the actual production methods, never Unity or a Step."""
    def __init__(self, project):
        super().__init__(project, [])
        self.validations = []
        self.validation_reply = {"ok": True, "budgetSeconds": 60, "diagnostics": []}
        self.dispatcher = SimpleNamespace(call=self.validate_bridge)

    async def validate_bridge(self, request_id, route, payload):
        assert route == "automation.steps.validate"
        self.validations.append(json.loads(payload["planJson"]))
        return ok(request_id, self.validation_reply)

    async def _operation_invoke(self, call, operation_id=""):
        self.calls.append((call["route"], copy.deepcopy(call["payload"])))
        assert call["route"] == "automation.steps.start"
        assert call["payload"]["operationId"] == operation_id
        return ok("fake-start", {"runId": "fake-run", "operationId": operation_id,
            "status": "Running", "phase": "Executing", "terminal": False})


def test_production_step_validation_has_no_run_capture_or_execute(tmp_path):
    service = StepService(tmp_path)
    job = step_spec(); before = copy.deepcopy(job)
    result = asyncio.run(service.operation_validate(job))
    assert result.ok and result.data["valid"] and result.data["normalizedJobSpec"]["cleanup"]["timeoutSec"] == 30
    assert service.validations == [job["stepPlan"]]
    assert not service.calls and not service._operations and job == before


def test_production_step_start_uses_original_executor_route_and_identity_once(tmp_path):
    async def run():
        service = StepService(tmp_path)
        try:
            result = await service.operation_start(step_spec())
            assert result.ok
            assert len(service.validations) == len(service.calls) == 1
            state = service._operations[result.data["operationId"]]
            assert state["startEstablished"] and state["startAttemptCount"] == 1
            assert state["resolvedStatusCall"]["payload"]["runId"] == "fake-run"
            assert state["resolvedStatusCall"]["payload"]["operationId"] == result.data["operationId"]
            assert not state["consoleCapture"]
        finally:
            await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("change,code", [
    ({"stepPlan": None}, "STEP_PLAN_INVALID"),
    ({"timeoutSec": 59}, "STEP_BUDGET_TOO_SMALL"),
    ({"startCall": {}}, "STEP_PLAN_CALLS_CONFLICT"),
    ({"stepPlan": {"steps": [{"stepId": "upilot.console_capture_start"}]},
      "consoleCapture": {"enabled": True}}, "STEP_CAPTURE_OWNERSHIP_CONFLICT"),
])
def test_invalid_official_plans_never_allocate_or_start(tmp_path, change, code):
    service = StepService(tmp_path); job = step_spec(); job.update(change)
    result = asyncio.run(service.operation_start(job))
    assert not result.ok and result.error.code == "INVALID_JOB_SPEC"
    assert any(x["code"] == code for x in result.error.detail["errors"])
    assert not service.calls and not service._operations


def test_bridge_step_validation_errors_propagate_without_start(tmp_path):
    service = StepService(tmp_path)
    service.validation_reply = {"ok": False, "diagnostics": [{
        "index": 0, "code": "STEP_NOT_FOUND", "message": "missing", "severity": "error"}]}
    result = asyncio.run(service.operation_start(step_spec()))
    assert not result.ok and result.error.detail["errors"][0]["code"] == "STEP_NOT_FOUND"
    assert not service.calls and not service._operations


@pytest.mark.parametrize("wrapped", [False, True])
@pytest.mark.parametrize("approved", [False, True])
def test_official_proxy_admission_preserves_write_access(wrapped, approved, monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", approved)
    # Config refresh must not overwrite the isolated fixture grant.
    monkeypatch.setattr("upilot_mcp.tool_registry.refresh_config_if_changed", lambda: None)
    calls = []
    class Facade:
        async def operation_start(self, job_spec: dict):
            calls.append(job_spec)
            return ok("fake", {"accepted": True})
        async def tool_call(self, tool_name: str, args: dict):
            return await dispatch_public_tool(self, tool_name, args)
    args = {"jobSpec": step_spec()}; name = "unity_operation_start"
    if wrapped:
        args = {"toolName": name, "args": args}; name = "unity_tool_call"
    result = asyncio.run(dispatch_public_tool(Facade(), name, args))
    assert result.ok == approved
    assert len(calls) == int(approved)
    if not approved:
        assert result.error.code == "WRITE_ACCESS_NOT_APPROVED"


@pytest.mark.parametrize("wrapper", ["unity_task_start", "unity_task_execute"])
def test_tasks_cannot_wrap_even_valid_step_plans(wrapper):
    assert disabled_orchestration_target(wrapper, {
        "toolName": "unity_operation_start", "toolArgs": {"jobSpec": step_spec()}})


def test_nonfinite_plan_is_rejected_before_bridge(tmp_path):
    service = StepService(tmp_path); job = step_spec(); job["stepPlan"]["bad"] = float("nan")
    result = asyncio.run(service.operation_validate(job))
    assert not result.ok and not service.validations and not service.calls and not service._operations


@pytest.mark.parametrize("capture", [True, [], None, {"enabled": "false"},
    {"enabled": 1}, {"enabled": False, "clearUnityConsole": "false"}])
def test_invalid_capture_configuration_cannot_reach_bridge_or_allocate(tmp_path, capture):
    service = StepService(tmp_path); job = step_spec(); job["consoleCapture"] = capture
    result = asyncio.run(service.operation_start(job))
    assert not result.ok and result.error.code == "INVALID_JOB_SPEC"
    assert any(x["path"].startswith("consoleCapture") for x in result.error.detail["errors"])
    assert not service.validations and not service.calls and not service._operations


@pytest.mark.parametrize("budget", [None, True, "invalid", "60", 0, -1, float("nan"), float("inf")])
def test_unverified_bridge_budget_never_becomes_admission(tmp_path, budget):
    service = StepService(tmp_path); service.validation_reply["budgetSeconds"] = budget
    result = asyncio.run(service.operation_start(step_spec()))
    assert not result.ok and result.error.code == "INVALID_JOB_SPEC"
    assert any(x["code"] == "STEP_BUDGET_INVALID" for x in result.error.detail["errors"])
    assert not service.calls and not service._operations


def test_start_revalidates_instead_of_reusing_old_preflight(tmp_path):
    service = StepService(tmp_path); job = step_spec()
    assert asyncio.run(service.operation_validate(job)).ok
    service.validation_reply = {"ok": False, "diagnostics": [
        {"code": "STEP_NOT_FOUND", "message": "registry changed", "severity": "error"}]}
    result = asyncio.run(service.operation_start(job))
    assert not result.ok and len(service.validations) == 2
    assert not service.calls and not service._operations
