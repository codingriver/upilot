"""Fixed production suspension; no test-side production re-enable switch."""
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


def rejected(response):
    assert not response.ok
    assert response.error.code == GENERIC_ORCHESTRATION_DISABLED
    assert response.error.message == GENERIC_ORCHESTRATION_REASON
    assert "dedicated" in response.error.detail["nextAction"]


@pytest.mark.parametrize("method", ["operation_start", "operation_validate"])
@pytest.mark.parametrize("job", [None, {}, {"stepPlan": {"version": 1, "steps": []}},
    {"stepPlan": {"version": 1, "steps": [{"instanceId": "a", "stepId": "upilot.wait_seconds", "arguments": "0"}]}},
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
def test_capabilities_cannot_reenable_start_but_preserve_dedicated_and_history(flow, connected, approved):
    def item_for(name):
        # Discovery is deliberately bounded to 200 items; query the exact route
        # instead of treating a truncated global inventory as a missing tool.
        return next(x for x in REGISTRY.find(query=name, flow_enabled=flow,
            connected=connected, server_ready=connected, write_access_approved=approved)
            if x["name"] == name)
    for name in ("unity_operation_start", "unity_operation_validate"):
        item = item_for(name)
        assert item["registered"] and not item["available"] and not item["callableNow"]
        assert item["unavailableReason"] == GENERIC_ORCHESTRATION_REASON
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
def test_wait_recovery_returns_immediately_without_cancel_capture_or_rewrite(tmp_path, ended_at):
    async def run():
        service = _OperationService(tmp_path, [])
        created = await service.seed_legacy_operation(spec())
        await stop_observers(service)
        state = service._operations[created.data["operationId"]]
        state.update(status="RecoveryRequired", phase="StartIdentityUnknown", endedAt=ended_at,
            cleanupPending=True, error="original evidence is missing", startIdentityState="unknown")
        assert service._save_operation(state)
        before = copy.deepcopy(state)
        service.calls.clear()
        async def forbidden(*a, **kw):
            raise AssertionError("wait must not cancel or read/stop Capture when blocked")
        service._operation_read_console_capture = forbidden
        service.operation_cancel = forbidden
        result = await asyncio.wait_for(service.operation_wait(state["operationId"], timeout_s=60), 1)
        assert result.ok and result.data["status"] == "RecoveryRequired"
        assert not result.data["terminal"] and result.data["recoveryBlocked"]
        assert not result.data["waitWindowElapsed"]
        assert "Do not replay" in result.data["recommendation"]
        for key in ("operationId", "status", "endedAt", "cleanupPending", "error", "startAttemptCount", "jobTimeoutAt"):
            assert state.get(key) == before.get(key)
        assert not service.calls
        await stop_observers(service)
    asyncio.run(run())
