import asyncio

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.responses import ok, fail
from test_operation_cleanup_barriers import spec
from test_operation_runner_and_agent_rules import _OperationService


def durable_spec(**kwargs):
    return spec(statusCall={"kind": "tool", "toolName": "status", "toolArgs": {"captureId": "${start.captureId}"}},
                pollIntervalSec=0.05, **kwargs)


async def stop_observers(service):
    tasks = list(service.__dict__.get("_operation_observers", {}).values())
    for task in tasks:
        task.cancel()
    await asyncio.gather(*tasks, return_exceptions=True)


def test_background_completion_without_client_poll_and_restart_read(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        result = await service.operation_start(durable_spec())
        await asyncio.sleep(0.15)
        stored = service.server.state.load_operations()[0]
        assert stored["endedAt"] and stored["status"] == "Succeeded"
        restored = _OperationService(tmp_path, [])
        latest = await restored.operation_status(result.data["operationId"])
        assert latest.data["terminal"] and latest.data["recovered"]
        assert restored.calls == []
        await stop_observers(service)
    asyncio.run(run())


def test_restart_resumes_only_original_status_and_never_start(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        first = await service.operation_start(durable_spec())
        await stop_observers(service)
        restored = _OperationService(tmp_path, [{"status": "Succeeded"}])
        result = await restored.operation_status(first.data["operationId"])
        assert result.data["terminal"]
        assert restored.calls == [("status", {"captureId": "capture-123"})]
        await stop_observers(restored)
    asyncio.run(run())


def test_lost_start_result_requires_recovery_without_replay(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        async def lost(*args):
            raise TimeoutError("response lost after dispatch")
        service._operation_invoke = lost
        first = await service.operation_start(durable_spec())
        assert not first.ok
        operation_id = first.error.detail["operationId"]
        assert first.error.detail["startAttemptCount"] == 1
        restored = _OperationService(tmp_path, [])
        result = await restored.operation_status(operation_id)
        assert result.data["status"] == "RecoveryRequired"
        assert result.data["startAttemptCount"] == 1
        assert not result.data["terminal"] and not restored.calls
    asyncio.run(run())


def test_missing_persistence_blocks_start_side_effects(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        def disk_error(_state):
            raise OSError("disk full")
        service.server.state.save_operation = disk_error
        result = await service.operation_start(durable_spec())
        assert not result.ok and not service.calls
    asyncio.run(run())


def test_lost_cancel_response_never_replays_cancel(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        result = await service.operation_start(durable_spec(cancelCall={"kind": "tool", "toolName": "cancel"}))
        original = service._operation_invoke
        attempts = []
        async def invoke(call, operation_id):
            if call["toolName"] == "cancel":
                attempts.append(operation_id)
                raise TimeoutError("lost")
            return await original(call, operation_id)
        service._operation_invoke = invoke
        first_cancel = await service.operation_cancel(result.data["operationId"])
        second_cancel = await service.operation_cancel(result.data["operationId"])
        assert len(attempts) == 1
        assert first_cancel.error.detail["cancelAttemptCount"] == 1
        assert second_cancel.data["cancelAttemptCount"] == 1
        await stop_observers(service)
        restored = _OperationService(tmp_path, [{"status": "Canceled", "cleanupPending": False}])
        recovered_cancel = await restored.operation_cancel(result.data["operationId"])
        assert recovered_cancel.data["cancelAttemptCount"] == 1
        assert restored.calls == []
        terminal = await restored.operation_status(result.data["operationId"])
        assert terminal.data["terminal"]
        await stop_observers(restored)
    asyncio.run(run())


def test_timeout_cancels_once_but_waits_for_business_and_cleanup(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [
            {"status": "Running"}, {"status": "Canceled"},
            {"status": "Canceled", "cleanupPending": False},
        ])
        result = await service.operation_start(durable_spec(cancelCall={"kind": "tool", "toolName": "cancel"}))
        operation_id = result.data["operationId"]
        state = service._operations[operation_id]
        state["startedAt"] = 1
        first = await service.operation_status(operation_id)
        assert not first.data["terminal"]
        assert not first.data["businessTerminal"]
        second = await service.operation_status(operation_id)
        assert not second.data["terminal"] and second.data["businessTerminal"]
        final = await service.operation_status(operation_id)
        assert final.data["terminal"] and final.data["status"] == "Timeout"
        assert [name for name, _ in service.calls].count("cancel") == 1
        await stop_observers(service)
    asyncio.run(run())


def test_invalid_start_response_does_not_invent_business_completion(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        async def invalid(*_):
            return ok("start", {"result": "{invalid"})
        service._operation_invoke = invalid
        result = await service.operation_start(durable_spec(
            startCall={"kind": "reflection", "typeName": "Bridge", "methodName": "Start"}))
        assert not result.ok and not result.error.detail["terminal"]
        assert result.error.detail["status"] == "RecoveryRequired"
        stored = service.server.state.load_operations()[0]
        assert stored["startIntentSent"] and not stored.get("businessTerminal")
    asyncio.run(run())


def test_unresolved_status_identity_is_not_recoverable(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        result = await service.operation_start(spec(statusCall={
            "kind": "tool", "toolName": "status", "toolArgs": {"id": "${start.missing}"},
        }))
        assert result.data["status"] == "RecoveryRequired"
        assert [name for name, _ in service.calls] == ["start"]
        await stop_observers(service)
        restored = _OperationService(tmp_path, [])
        recovered = await restored.operation_status(result.data["operationId"])
        assert recovered.data["status"] == "RecoveryRequired" and not restored.calls
    asyncio.run(run())


def test_capture_response_loss_preserves_intent_and_blocks_business(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        attempts = []
        async def lost(*args, **kwargs):
            attempts.append({"args": args, "kwargs": kwargs})
            raise TimeoutError("capture may already exist")
        service._start_owned_operation_capture = lost
        result = await service.operation_start(durable_spec(consoleCapture={"enabled": True}))
        assert not result.ok and not result.error.detail["terminal"]
        stored = service.server.state.load_operations()[0]
        assert stored["captureIntentSent"] and not stored.get("startIntentSent")
        assert stored["consoleCapture"]["ownerId"] == stored["operationId"]
        assert stored["consoleCapture"]["ownerToken"]
        assert stored["cleanupPending"] and not service.calls and len(attempts) == 1
        restored = _OperationService(tmp_path, [])
        observed = await restored.operation_status(stored["operationId"])
        assert observed.data["status"] == "RecoveryRequired" and not restored.calls
    asyncio.run(run())


def test_arbitrary_start_exception_response_does_not_prove_no_side_effect(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        async def failed(*args):
            return fail("original", "TARGET_INVOCATION_EXCEPTION", "failed after spawning work")
        service._operation_invoke = failed
        result = await service.operation_start(durable_spec())
        assert result.error.code == "OPERATION_START_UNKNOWN"
        assert not result.error.detail["terminal"]
        assert service.server.state.load_operations()[0]["startResult"]["requestId"] == "original"
    asyncio.run(run())


def test_status_cannot_accept_another_job_terminal(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded", "captureId": "another"}])
        start = await service.operation_start(durable_spec())
        result = await service.operation_status(start.data["operationId"])
        assert not result.ok and result.error.code == "OPERATION_IDENTITY_MISMATCH"
        assert not result.error.detail["terminal"]
        await stop_observers(service)
    asyncio.run(run())


def test_disconnected_unity_status_returns_recovering_without_dispatch(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        started = await service.operation_start(durable_spec())
        operation_id = started.data["operationId"]
        service.server.is_ready = lambda: False

        result = await service.operation_status(operation_id)

        assert result.ok
        assert result.data["operationId"] == operation_id
        assert result.data["phase"] == "Recovering"
        assert result.data["terminal"] is False
        assert result.data["startAttemptCount"] == 1
        assert service.server.session_manager.active is not None
        assert service.calls == [("start", {})]
        stored = service.server.state.load_operations()[0]
        assert stored["phase"] == "Recovering" and stored["endedAt"] == 0
        await stop_observers(service)
    asyncio.run(run())


def test_reconnected_success_clears_transient_recovery_diagnostics(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(durable_spec())
        operation_id = started.data["operationId"]
        await stop_observers(service)
        service.server.is_ready = lambda: False

        recovering = await service.operation_status(operation_id)
        assert recovering.data["phase"] == "Recovering"
        assert recovering.data["nextAction"]
        assert service._operations[operation_id]["lastObservationError"]

        service.server.is_ready = lambda: True
        service._statuses.append({"status": "Succeeded", "phase": "Completed"})
        completed = await service.operation_status(operation_id)

        assert completed.data["terminal"] is True
        assert completed.data["status"] == "Succeeded"
        assert completed.data["nextAction"] == ""
        assert service._operations[operation_id]["lastObservationError"] == ""
        stored = service.server.state.load_operations()[0]
        assert stored["lastObservationError"] == ""
        assert stored["nextAction"] == ""

        stored["lastObservationError"] = "Unity Bridge is disconnected; status was not dispatched."
        stored["nextAction"] = "Reconnect the same Unity project."
        service.server.state.save_operation(stored)
        restored = _OperationService(tmp_path, [])
        reloaded = await restored.operation_status(operation_id)
        assert reloaded.data["terminal"] is True
        assert reloaded.data["nextAction"] == ""
        reloaded_state = restored.server.state.load_operations()[0]
        assert reloaded_state["lastObservationError"] == ""
        assert reloaded_state["nextAction"] == ""

    asyncio.run(run())


def test_operation_call_connection_requirement_uses_registry_metadata(monkeypatch):
    assert TaskDomainService._operation_call_requires_unity({
        "kind": "reflection", "typeName": "Bridge", "methodName": "Status",
    }) is True
    descriptor = type("Descriptor", (), {"requires_unity_connection": False})()
    monkeypatch.setattr("upilot_mcp.domain.task_service.REGISTRY.resolve",
                        lambda name: descriptor if name == "server_local_status" else None)
    assert TaskDomainService._operation_call_requires_unity({
        "kind": "tool", "toolName": "server_local_status",
    }) is False
    assert TaskDomainService._operation_call_requires_unity({
        "kind": "tool", "toolName": "unregistered-project-status",
    }) is True
