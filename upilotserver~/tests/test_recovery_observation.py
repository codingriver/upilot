"""Recovery observes original identities; it never replays lifecycle commands."""
import asyncio
from types import SimpleNamespace

import pytest

from upilot_mcp.domain import task_service
from upilot_mcp.responses import fail, ok
from test_operation_cleanup_barriers import spec
from test_operation_runner_and_agent_rules import _OperationService
from test_persistent_operations import stop_observers
from test_persistent_test_jobs import Service


class StepService(_OperationService):
    def __init__(self, root, samples):
        super().__init__(root, [])
        self.samples = list(samples)
        self.reads = 0
        self.dispatcher = SimpleNamespace(call=self.bridge_call)

    async def _dispatch_tool(self, name, args):
        result = await super()._dispatch_tool(name, args)
        if name == "start":
            result.data["runId"] = "original-run"
        return result

    async def bridge_call(self, request_id, route, payload, **kwargs):
        assert route == "automation.steps.state", "Recovery must not dispatch lifecycle effects"
        self.reads += 1
        sample = self.samples.pop(0) if len(self.samples) > 1 else self.samples[0]
        if isinstance(sample, Exception):
            raise sample
        if not isinstance(sample, dict):
            return sample
        return ok(request_id, {"runId": payload["runId"], "operationId": payload["operationId"],
                               "terminal": True, "cleanupPending": False, **sample})


async def start_step(service, **kwargs):
    result = await service.seed_legacy_operation(spec(
        statusCall={"kind": "bridge", "route": "automation.steps.state", "payload": {
            "runId": "${start.runId}", "operationId": "${operation.operationId}"}},
        cancelCall={"kind": "tool", "toolName": "cancel"}, pollIntervalSec=0.05, **kwargs))
    assert result.ok, result.error
    return service._operations[result.data["operationId"]]


@pytest.mark.parametrize("outcome", ["Failed", "Succeeded", "Canceled", "TimedOut"])
def test_background_recovery_reaches_original_result_without_chat_poll(tmp_path, outcome):
    async def run():
        service = StepService(tmp_path, [fail("q", "READ_ERROR", "transient observation failure"),
                                         {"status": outcome, "error": "business failure" if outcome == "Failed" else ""}])
        state = await start_step(service)
        first = await service.operation_status(state["operationId"])
        assert not first.ok and state["status"] == "RecoveryRequired"
        await asyncio.wait_for(service._operation_observers[state["operationId"]], 5)
        assert state["endedAt"] and not state["cleanupPending"]
        assert state["status"] == ("Timeout" if outcome == "TimedOut" else outcome)
        assert state["error"] != "transient observation failure"
        assert [name for name, _ in service.calls] == ["start"]
        restored = StepService(tmp_path, [{"status": "Running"}])
        final = await restored.operation_status(state["operationId"])
        assert final.data["terminal"] and restored.reads == 0
        await stop_observers(service)
        await stop_observers(restored)
    asyncio.run(run())


@pytest.mark.parametrize("payload", [
    {"runId": "other"}, {"operationId": "other"}, {"runId": None},
    {"terminal": None}, {"cleanupPending": None}, {"terminal": False},
])
def test_recovery_rejects_missing_or_wrong_identity_and_terminal_evidence(tmp_path, payload):
    async def run():
        service = StepService(tmp_path, [{"status": "Failed", **payload}])
        state = await start_step(service)
        state.update(status="RecoveryRequired", phase="StatusCallFailed")
        result = await service.operation_status(state["operationId"])
        assert not result.data["terminal"] and not state.get("businessTerminal")
        assert state["lastObservationError"] and state["nextRecoveryObservationAt"]
        await stop_observers(service)
    asyncio.run(run())


def test_recovery_backoff_is_capped_and_serialized(tmp_path, monkeypatch):
    async def run():
        service = StepService(tmp_path, [fail("q", "READ_FAILED", "no result")])
        state = await start_step(service)
        clock = [task_service.now_ms()]
        monkeypatch.setattr(task_service, "now_ms", lambda: clock[0])
        state.update(status="RecoveryRequired", phase="StatusCallFailed")
        for index, delay in enumerate([3, 6, 12, 24, 48, 60, 60]):
            await asyncio.gather(*(service.operation_status(state["operationId"]) for _ in range(3)))
            assert service.reads == index + 1
            assert state["nextRecoveryObservationAt"] == clock[0] + delay * 1000
            clock[0] += delay * 1000
        assert len(state["recoveryDiagnostics"]) <= 8
        await stop_observers(service)
    asyncio.run(run())


def test_custom_callback_and_persistence_barrier_are_not_retried(tmp_path):
    async def run():
        generic = _OperationService(tmp_path / "generic", [{"status": "Succeeded"}])
        result = await generic.seed_legacy_operation(spec())
        state = generic._operations[result.data["operationId"]]
        state.update(status="RecoveryRequired", phase="StatusCallFailed")
        status = await generic.operation_status(state["operationId"])
        assert not status.data["terminal"] and len(generic.calls) == 1
        assert status.data["recoveryObservationUnsupportedReason"]
        step = StepService(tmp_path / "step", [{"status": "Succeeded"}])
        state = await start_step(step)
        state.update(status="RecoveryRequired", phase="persistence_failed", recoveryObservationOnly=True)
        await step.operation_status(state["operationId"])
        assert step.reads == 0 and not state.get("endedAt")
        await stop_observers(generic)
        await stop_observers(step)
    asyncio.run(run())


def test_restart_reattaches_readonly_recovery_and_honors_timeout_without_cancel(tmp_path):
    async def run():
        first = StepService(tmp_path, [{"status": "Running", "terminal": False}])
        state = await start_step(first)
        state.update(status="RecoveryRequired", phase="StatusCallFailed", startedAt=1, jobTimeoutAt=1)
        assert first._save_operation(state)
        await stop_observers(first)
        second = StepService(tmp_path, [{"status": "Succeeded"}])
        second._recover_operations()
        await asyncio.wait_for(second._operation_observers[state["operationId"]], 2)
        result = second._operations[state["operationId"]]
        assert result["status"] == "Timeout" and result["endedAt"]
        assert second.calls == [] and second.reads == 1
        await stop_observers(second)
    asyncio.run(run())


@pytest.mark.parametrize("business", ["Failed", "Succeeded"])
def test_cleanup_timeout_remains_nonterminal_then_converges_without_poll(tmp_path, business):
    async def run():
        service = StepService(tmp_path, [
            {"status": business, "cleanupPending": True, "error": "original"},
            {"status": business, "cleanupPending": True},
            {"status": business, "cleanupPending": False},
        ])
        state = await start_step(service)
        await service.operation_status(state["operationId"])
        state["cleanupDeadlineAt"] = 1
        result = await service.operation_status(state["operationId"])
        assert not result.data["terminal"] and result.data["status"] == "RecoveryRequired"
        assert result.data["cleanupFailureSignature"] == "OperationCleanupTimeout"
        await asyncio.wait_for(service._operation_observers[state["operationId"]], 5)
        assert state["status"] == "Failed" and state["endedAt"] and not state["cleanupPending"]
        assert state["businessResult"]["status"] == business
        assert state["cleanupFailureSignature"] == "OperationCleanupTimeout"
        if business == "Failed":
            assert state["error"] == "original"
        await stop_observers(service)
    asyncio.run(run())


def test_durable_test_recovery_continues_after_initial_runner_exits_without_poll(tmp_path):
    async def run():
        service = Service(tmp_path)
        service.cleanup = False
        async def initially_unavailable(*args):
            return fail("q", "TEST_RECOVERY_REQUIRED", "cleanup unknown")
        service._wait_for_test_result = initially_unavailable
        start = await service.task_start("recover", "unity_test_run")
        task_id = start.data["taskId"]
        initial = service._async_task_handles[task_id]
        await initial
        recovery = service._async_task_handles[task_id]
        assert initial is not recovery
        await asyncio.sleep(0)
        assert not service._async_tasks[task_id]["terminal"]
        assert service._async_tasks[task_id]["nextRecoveryObservationAt"]
        service.cleanup = True
        await asyncio.wait_for(recovery, 5)
        state = service._async_tasks[task_id]
        assert state["status"] == "completed" and state["terminal"]
        assert service.start_count == 1 and not service.cancels
        assert not state.get("error")
        next_start = await service.task_start("next", "unity_test_run")
        assert next_start.ok
        await service._async_task_handles[next_start.data["taskId"]]
        for task in service._async_task_handles.values():
            if not task.done():
                task.cancel()
        await asyncio.gather(*service._async_task_handles.values(), return_exceptions=True)
    asyncio.run(run())


def test_durable_test_recovery_rejects_wrong_run_and_stops_at_project_boundary(tmp_path):
    async def run():
        service = Service(tmp_path)
        state = {"taskId": "old", "runGuid": "original", "durable": True, "terminal": False,
                 "projectPath": str(tmp_path.resolve()), "status": "RecoveryRequired", "startedAt": 1}
        async def results(**kwargs):
            assert kwargs == {"run_guid": "original"}
            data = (await Service.test_results(service, "wrong")).data
            # Stop the observer after the rejected sample, without waiting for the backoff.
            service.server.state._project_path = str(tmp_path / "other")
            return ok("q", data)
        service.test_results = results
        await service._observe_test_recovery(state)
        assert not state["terminal"] and state["lastObservationError"]
        assert service.start_count == 0 and not service.cancels
    asyncio.run(run())


def test_capture_lost_stop_response_is_observed_once_not_replayed(tmp_path):
    import hashlib
    import json

    async def run():
        service = StepService(tmp_path, [{"status": "Failed", "error": "original failure"}])
        state = await start_step(service)
        state["consoleCapture"] = {"sessionId": "owned", "ownerToken": "secret"}
        calls = []
        session = {"sessionId": "owned", "active": False, "finishedAtUtcMs": 1,
                   "jsonlPath": "console.jsonl", "summaryPath": "summary.json", "fileBytes": 0,
                   "recordCount": 0, "segmentCount": 1, "sha256": hashlib.sha256(b"").hexdigest()}
        (tmp_path / "console.jsonl").write_bytes(b"")
        (tmp_path / "summary.json").write_text(json.dumps(session))
        async def stop(**kwargs):
            calls.append(("stop", kwargs["session_id"]))
            persisted = service.server.state.load_operations()[0]
            assert persisted["consoleCapture"]["stopIntentSent"]
            raise TimeoutError("response lost after dispatch")
        async def status(**kwargs):
            calls.append(("status", kwargs["session_id"]))
            return ok("status", {"session": session})
        service.console_capture_stop = stop
        service.console_capture_status = status
        first = await service.operation_status(state["operationId"])
        assert not first.data["terminal"]
        state["cleanupDeadlineAt"] = 1
        # No new stop even after the original budget expires.
        final = await service.operation_status(state["operationId"])
        assert final.data["terminal"] and final.data["status"] == "Failed"
        assert final.data["error"] == "original failure"
        assert calls == [("stop", "owned"), ("status", "owned")]
        await stop_observers(service)
    asyncio.run(run())


def test_capture_persist_failure_prevents_stop_and_does_not_clear_barrier(tmp_path):
    async def run():
        service = StepService(tmp_path, [{"status": "Succeeded"}])
        state = await start_step(service)
        state["consoleCapture"] = {"sessionId": "owned", "ownerToken": "secret"}
        async def stop(**kwargs):
            pytest.fail("No stop may be sent before intent is durable")
        service.console_capture_stop = stop
        original = service.server.state.save_operation
        def fail_persist(value):
            raise OSError("disk unavailable")
        service.server.state.save_operation = fail_persist
        failed = await service.operation_status(state["operationId"])
        assert not failed.ok and state["phase"] == "persistence_failed" and not state["endedAt"]
        service.server.state.save_operation = original
        await service.operation_status(state["operationId"])
        assert state["status"] == "RecoveryRequired" and state["phase"] == "persistence_failed"
        assert service.reads == 1
        await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("status,failed,expected", [("failed", 1, "failed"), ("completed", 0, "completed")])
def test_persisted_test_recovery_survives_restart_without_start_or_cancel(tmp_path, status, failed, expected):
    async def run():
        first = Service(tmp_path)
        state = {"taskId": "old", "runGuid": "original", "durable": True, "terminal": False,
                 "projectPath": str(tmp_path.resolve()), "status": "RecoveryRequired", "startedAt": 1,
                 "endedAt": 0, "deadlineAt": task_service.now_ms() + 60000}
        first.server.state.save_test_job(state)
        second = Service(tmp_path)
        async def results(**kwargs):
            data = (await Service.test_results(second, kwargs["run_guid"])).data
            return ok("q", {**data, "status": status, "failed": failed})
        second.test_results = results
        second._recover_test_jobs()
        await asyncio.wait_for(second._async_task_handles["old"], 2)
        assert second._async_tasks["old"]["status"] == expected
        assert second._async_tasks["old"]["terminal"]
        assert second.start_count == 0 and not second.cancels
        assert second.server.state.load_test_jobs()[0]["terminal"]
    asyncio.run(run())


def test_test_recovery_persist_failure_remains_a_barrier(tmp_path):
    async def run():
        service = Service(tmp_path)
        state = {"taskId": "old", "runGuid": "original", "durable": True, "terminal": False,
                 "projectPath": str(tmp_path.resolve()), "status": "RecoveryRequired", "startedAt": 1}
        def failed_save(value):
            raise OSError("disk unavailable")
        service.server.state.save_test_job = failed_save
        await service._observe_test_recovery(state)
        assert not state["terminal"] and state["endedAt"] == 0
        assert state["error"]["code"] == "TEST_TASK_PERSIST_FAILED"
        async def no_query(**kwargs):
            pytest.fail("A successful observation must not clear a persistence barrier")
        service.test_results = no_query
        await service._observe_test_recovery(state)
        assert not state["terminal"]
    asyncio.run(run())


class QueueRecoveryService(StepService):
    from upilot_mcp.domain.queue_service import QueueDomainService as _Queue
    queue_cleanup = _Queue.queue_cleanup
    _queue_target = _Queue._queue_target
    _queue_project = _Queue._queue_project
    _queue_step_recovery_probe = _Queue._queue_step_recovery_probe
    _queue_operation_guard_valid = _Queue._queue_operation_guard_valid

    def __init__(self, root):
        self.domain = {"cleanupTrackingVersion": 1, "stage": "Completed", "reportCommitJson": "frozen",
                       "steps": [{"cleanupState": "Unresolved"}], "cleanupRecoveryState": ""}
        super().__init__(root, [])
        self.server.is_ready = lambda: True
        self.recoveries = []
        self.lose_response = False

    async def bridge_call(self, request_id, route, payload, **kwargs):
        if route not in {"automation.steps.state", "automation.steps.recover"}:
            return ok(request_id, {})  # queue audit only
        if route == "automation.steps.recover":
            self.recoveries.append(dict(payload))
            self.domain.update(cleanupRecoveryRequestId=payload["recoveryRequestId"], cleanupRecoveryState="Running")
            if self.lose_response:
                raise TimeoutError("lost reply")
        return ok(request_id, {"operationId": payload["operationId"], "runId": payload["runId"],
                              "status": "RecoveryRequired", "terminal": False, "cleanupPending": True,
                              "domain": dict(self.domain)})


async def recovery_preview(service, state):
    args = dict(target_type="Operation", target_id=state["operationId"], action="recover", reason="recover original cleanup")
    preview = await service.queue_cleanup(**args)
    assert preview.ok, preview
    return dict(**args, dry_run=False, confirm_token=preview.data["confirmToken"],
                expected_project_path=service.server.state.project_path)


def test_queue_recovery_preview_apply_is_exact_granted_and_one_shot(tmp_path):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
        args = await recovery_preview(service, state)
        assert not service.recoveries
        result = await service.queue_cleanup(**args)
        assert result.ok, result
        assert len(service.recoveries) == 1
        assert service.recoveries[0]["operationId"] == state["operationId"]
        assert service.recoveries[0]["runId"] == "original-run"
        assert state["cleanupRecoveryRequestId"] == service.recoveries[0]["recoveryRequestId"]
        again = await service.queue_cleanup(**args)
        assert not again.ok and len(service.recoveries) == 1
        assert [name for name, _ in service.calls] == ["start"]
        await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("change", ["state", "grant", "unknown", "identity"])
def test_queue_recovery_rechecks_authority_evidence_and_preview(tmp_path, change):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
        args = await recovery_preview(service, state)
        if change == "state":
            service.domain["steps"] = [{"cleanupState": "Verified"}, {"cleanupState": "Unresolved"}]
        elif change == "grant":
            import json
            config = tmp_path / ".upilot" / "config.json"
            config.parent.mkdir(exist_ok=True)
            config.write_text(json.dumps({"aiQueueCleanupAllowed": False}))
        elif change == "unknown":
            service.domain["cleanupTrackingVersion"] = 0
        else:
            state["startData"]["runId"] = "different"
        result = await service.queue_cleanup(**args)
        assert not result.ok
        assert not service.recoveries and not state.get("endedAt")
        await stop_observers(service)
    asyncio.run(run())


def test_uncertain_queue_recovery_is_observed_not_resent(tmp_path):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
        args = await recovery_preview(service, state)
        service.lose_response = True
        result = await service.queue_cleanup(**args)
        assert not result.ok
        assert state["cleanupRecoverySendState"] == "unknown"
        await service.operation_status(state["operationId"])
        result = await service.queue_cleanup(target_type="Operation", target_id=state["operationId"],
                                             action="recover", reason="do not repeat")
        assert not result.ok and len(service.recoveries) == 1
        assert state["cleanupRecoveryRequestId"]
        await stop_observers(service)
    asyncio.run(run())


def test_recovery_requires_queue_grant_and_persisted_dispatch_intent(tmp_path, monkeypatch):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
        direct = await service.operation_recover(state["operationId"], "not-granted")
        assert not direct.ok and not service.recoveries
        args = await recovery_preview(service, state)
        def broken(_):
            raise OSError("persistence unavailable")
        monkeypatch.setattr(service.server.state, "save_operation", broken)
        result = await service.queue_cleanup(**args)
        assert not result.ok and not service.recoveries
        assert state["phase"] == "persistence_failed"
        await stop_observers(service)
    asyncio.run(run())
