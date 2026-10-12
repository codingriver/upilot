"""Interrupted work ends once; history never resurrects lifecycle commands.

LegacyOperationFixture only seeds isolated historical records. These tests do not
re-enable public generic Operation/Step admission or dispatch to real Unity.
"""
import asyncio
import copy
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
def test_observation_failure_freezes_original_run_without_background_recovery(tmp_path, outcome):
    async def run():
        service = StepService(tmp_path, [fail("q", "READ_ERROR", "transient observation failure"),
                                         {"status": outcome, "error": "later business result"}])
        state = await start_step(service)
        try:
            first = await service.operation_status(state["operationId"])
            assert not first.ok and first.error.code == "OPERATION_STATUS_FAILED"
            assert state["status"] == "aborted" and state["terminal"] and state["endedAt"]
            assert state["cleanupSucceeded"] is False and not state["cleanupPending"]
            await asyncio.wait_for(service._operation_observers[state["operationId"]], 2)
            frozen = copy.deepcopy(state)
            for _ in range(2):
                result = await service.operation_status(state["operationId"])
                assert result.ok and result.data["terminal"] and result.data["status"] == "aborted"
            assert service.reads == 1 and len(service.samples) == 1
            assert state["error"] == "transient observation failure"
            assert state["endedAt"] == frozen["endedAt"]
            assert state["startData"] == frozen["startData"]
            assert [name for name, _ in service.calls] == ["start"]
            history = service.server.state.load_operations()
            restored = StepService(tmp_path, [{"status": "Succeeded"}])
            final = await restored.operation_status(state["operationId"])
            assert not final.ok and final.error.code == "OPERATION_NOT_FOUND"
            assert restored.reads == 0 and restored.calls == []
            assert service.server.state.load_operations() == history
        finally:
            await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("payload", [
    {"runId": "other"}, {"operationId": "other"}, {"runId": None},
    {"terminal": None}, {"cleanupPending": None}, {"terminal": False},
])
def test_status_rejects_missing_or_wrong_identity_and_terminal_evidence(tmp_path, payload):
    async def run():
        service = StepService(tmp_path, [{"status": "Failed", **payload}, {"status": "Succeeded"}])
        state = await start_step(service)
        await stop_observers(service)
        original_start = copy.deepcopy(state["startData"])
        result = await service.operation_status(state["operationId"])
        assert not result.ok and result.error.code == "OPERATION_IDENTITY_MISMATCH"
        assert state["terminal"] and state["status"] == "aborted" and state["endedAt"]
        assert not state.get("businessTerminal") and state["cleanupSucceeded"] is False
        assert state["startData"] == original_start and service.reads == 1
        assert state["lastObservationError"]
        frozen = {key: copy.deepcopy(state.get(key)) for key in
                  ("error", "lastObservationError", "endedAt", "startData", "status", "cleanupSucceeded")}
        for _ in range(2):
            late = await service.operation_status(state["operationId"])
            assert late.ok and late.data["terminal"] and late.data["status"] == "aborted"
        assert {key: state.get(key) for key in frozen} == frozen
        assert service.reads == 1 and [name for name, _ in service.calls] == ["start"]
        persisted = service.server.state.load_operations()[0]
        assert persisted["terminal"] and persisted["status"] == "aborted"
        assert persisted["startData"] == original_start
    asyncio.run(run())


def test_concurrent_observation_failure_is_terminal_and_not_retried(tmp_path, monkeypatch):
    async def run():
        service = StepService(tmp_path, [fail("q", "READ_FAILED", "no result")])
        state = await start_step(service)
        await stop_observers(service)
        clock = [task_service.now_ms()]
        monkeypatch.setattr(task_service, "now_ms", lambda: clock[0])
        deadline = state["startedAt"] + state["timeoutSec"] * 1000
        ended_at = None
        for delay in [3, 6, 12, 24, 48, 60, 60]:
            results = await asyncio.gather(*(service.operation_status(state["operationId"]) for _ in range(3)))
            assert service.reads == 1
            assert state["terminal"] and state["status"] == "aborted"
            assert all((result.data if result.ok else result.error.detail)["terminal"] for result in results)
            ended_at = ended_at or state["endedAt"]
            assert state["endedAt"] == ended_at
            assert state["startedAt"] + state["timeoutSec"] * 1000 == deadline
            clock[0] += delay * 1000
        assert len(state["recoveryDiagnostics"]) == 1
        assert [name for name, _ in service.calls] == ["start"]
    asyncio.run(run())


def test_custom_callback_and_persistence_barrier_end_without_retry(tmp_path):
    async def run():
        generic = _OperationService(tmp_path / "generic", [{"status": "Succeeded"}])
        result = await generic.seed_legacy_operation(spec())
        await stop_observers(generic)
        state = generic._operations[result.data["operationId"]]
        state.update(status="RecoveryRequired", phase="StatusCallFailed")
        status = await generic.operation_status(state["operationId"])
        assert status.data["terminal"] and status.data["status"] == "aborted"
        assert len(generic.calls) == 1 and status.data["recoveryObservationUnsupportedReason"]
        step = StepService(tmp_path / "step", [{"status": "Succeeded"}])
        state = await start_step(step)
        await stop_observers(step)
        state.update(status="RecoveryRequired", phase="persistence_failed", recoveryObservationOnly=True)
        for _ in range(2):
            result = await step.operation_status(state["operationId"])
            assert result.ok and result.data["terminal"] and result.data["status"] == "aborted"
        assert state["cleanupSucceeded"] is False and state["endedAt"]
        assert step.reads == 0 and [name for name, _ in step.calls] == ["start"]
    asyncio.run(run())


def test_restart_does_not_restore_interrupted_operation_or_renew_deadline(tmp_path):
    async def run():
        first = StepService(tmp_path, [{"status": "Running", "terminal": False}])
        state = await start_step(first)
        await stop_observers(first)
        state.update(status="RecoveryRequired", phase="StatusCallFailed", startedAt=1, jobTimeoutAt=1)
        # Seed an actual historical record, not _save_operation's current finalization path.
        first.server.state.save_operation(state)
        history = first.server.state.load_operations()
        second = StepService(tmp_path, [{"status": "Succeeded"}])
        for _ in range(2):
            second._recover_operations()
            result = await second.operation_status(state["operationId"])
            assert not result.ok and result.error.code == "OPERATION_NOT_FOUND"
        assert not second._operations and not second.__dict__.get("_operation_observers")
        assert second.calls == [] and second.reads == 0
        assert first.server.state.load_operations() == history
        assert history[0]["jobTimeoutAt"] == 1
    asyncio.run(run())


@pytest.mark.parametrize("business", ["Failed", "Succeeded"])
def test_cleanup_timeout_is_terminal_and_keeps_original_business_result(tmp_path, business):
    async def run():
        service = StepService(tmp_path, [
            {"status": business, "cleanupPending": True, "error": "original"},
            {"status": business, "cleanupPending": True},
            {"status": business, "cleanupPending": False},
        ])
        state = await start_step(service)
        await stop_observers(service)
        await service.operation_status(state["operationId"])
        state["cleanupDeadlineAt"] = 1
        result = await service.operation_status(state["operationId"])
        assert result.data["terminal"] and result.data["status"] == "timed_out"
        assert state["endedAt"] and not state["cleanupPending"] and state["cleanupSucceeded"] is False
        assert state["cleanupFailureSignature"] == "OperationCleanupTimeout" and state["cleanupError"]
        assert state["businessResult"]["status"] == business
        assert state["businessResult"]["error"] == "original"
        assert state["error"] == "original" and state["cleanupError"] != state["error"]
        frozen = copy.deepcopy(state)
        for _ in range(2):
            late = await service.operation_status(state["operationId"])
            assert late.data["terminal"] and late.data["status"] == "timed_out"
        for key in ("businessResult", "cleanupFailureSignature", "cleanupError", "endedAt", "cleanupDeadlineAt"):
            assert state[key] == frozen[key]
        assert service.reads == 2 and len(service.samples) == 1
        assert [name for name, _ in service.calls] == ["start"]
    asyncio.run(run())


def test_durable_test_unverified_result_ends_once_without_recovery_task(tmp_path):
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
        state = service._async_tasks[task_id]
        assert initial is service._async_task_handles[task_id] and initial.done()
        assert state["terminal"] and state["status"] == "aborted" and state["endedAt"]
        assert state["cleanupSucceeded"] is False
        assert state["error"]["code"] == "TEST_RECOVERY_REQUIRED"
        frozen = copy.deepcopy(state)
        service.cleanup = True
        for _ in range(2):
            observed = await service.task_status(task_id)
            assert observed.data["terminal"] and observed.data["status"] == "aborted"
        assert state == frozen and service.start_count == 1 and not service.cancels
        # Only a separately requested successor can start; it cannot rewrite the old failure.
        del service._wait_for_test_result  # Restore the inherited production result observer.
        next_start = await service.task_start("next", "unity_test_run")
        assert next_start.ok and next_start.data["taskId"] != task_id
        await service._async_task_handles[next_start.data["taskId"]]
        assert service._async_tasks[next_start.data["taskId"]]["status"] == "completed"
        assert state == frozen and service.start_count == 2 and not service.cancels
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


def test_capture_persist_failure_prevents_stop_and_freezes_unknown_cleanup(tmp_path):
    async def run():
        service = StepService(tmp_path, [{"status": "Failed", "error": "first business failure"}])
        state = await start_step(service)
        await stop_observers(service)
        state["consoleCapture"] = {"sessionId": "owned", "ownerToken": "secret"}
        async def stop(**kwargs):
            pytest.fail("No stop may be sent before intent is durable")
        service.console_capture_stop = stop
        original = service.server.state.save_operation
        def fail_persist(value):
            raise OSError("disk unavailable")
        service.server.state.save_operation = fail_persist
        failed = await service.operation_status(state["operationId"])
        assert not failed.ok and failed.error.code == "OPERATION_PERSIST_FAILED"
        assert state["phase"] == "persistence_failed" and state["endedAt"] and state["terminal"]
        assert state["status"] == "aborted" and state["cleanupSucceeded"] is False
        assert state["error"] == "first business failure" and state["persistenceError"] == "disk unavailable"
        frozen = copy.deepcopy(state)
        service.server.state.save_operation = original
        for _ in range(2):
            result = await service.operation_status(state["operationId"])
            assert result.data["terminal"] and result.data["status"] == "aborted"
        for key in ("endedAt", "error", "persistenceError", "cleanupSucceeded", "consoleCapture"):
            assert state[key] == frozen[key]
        assert service.reads == 1
        assert service.server.state.load_operations()[0]["error"] == "first business failure"
    asyncio.run(run())


@pytest.mark.parametrize("expired", [False, True])
def test_persisted_test_recovery_does_not_survive_service_restart(tmp_path, expired):
    async def run():
        first = Service(tmp_path)
        state = {"taskId": "old", "runGuid": "original", "durable": True, "terminal": False,
                 "projectPath": str(tmp_path.resolve()), "status": "RecoveryRequired", "startedAt": 1,
                 "endedAt": 0, "deadlineAt": 1 if expired else task_service.now_ms() + 60000}
        first.server.state.save_test_job(state)
        history = first.server.state.load_test_jobs()
        second = Service(tmp_path)
        async def results(**kwargs):
            pytest.fail("A new service must not query an old run, regardless of its original deadline")
        second.test_results = results
        for _ in range(2):
            second._recover_test_jobs()
            result = await second.task_status("old")
            assert not result.ok and result.error.code == "TASK_NOT_FOUND"
        assert not second._async_task_handles and not second._async_tasks
        assert second.start_count == 0 and not second.cancels
        assert second.server.state.load_test_jobs() == []
        assert first.server.state.load_test_jobs() == history
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
        self.reads += 1
        if route == "automation.steps.recover":
            self.recoveries.append(dict(payload))
            self.domain.update(cleanupRecoveryRequestId=payload["recoveryRequestId"], cleanupRecoveryState="Running")
            if self.lose_response:
                raise TimeoutError("lost reply")
        return ok(request_id, {"operationId": payload["operationId"], "runId": payload["runId"],
                              "status": "RecoveryRequired", "terminal": False, "cleanupPending": True,
                              "domain": dict(self.domain)})


async def assert_recovery_rejected(service, state, **overrides):
    args = dict(target_type="Operation", target_id=state["operationId"], action="recover",
                reason="original cleanup must not be replayed", **overrides)
    before = copy.deepcopy(state)
    response = await service.queue_cleanup(**args)
    assert not response.ok and response.error.code == "QUEUE_CLEANUP_UNSUPPORTED"
    assert response.error.detail["dispatchAttempted"] is False
    assert state == before and not service.recoveries and service.reads == 0
    assert [name for name, _ in service.calls] == ["start"]
    return response


def test_queue_recovery_preview_and_apply_are_rejected_before_probe(tmp_path):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
        async def no_probe(*args):
            pytest.fail("Removed recovery action must be rejected before probing business state")
        service._queue_target = no_probe
        await assert_recovery_rejected(service, state)
        await assert_recovery_rejected(service, state, dry_run=False, confirm_token="stale-token",
                                       expected_project_path=service.server.state.project_path)
    asyncio.run(run())


@pytest.mark.parametrize("change", ["state", "grant", "unknown", "identity"])
def test_queue_recovery_stays_removed_under_changed_evidence_or_grant(tmp_path, change):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True)
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
        await assert_recovery_rejected(service, state)
        await assert_recovery_rejected(service, state, dry_run=False, confirm_token="stale-token",
                                       expected_project_path=service.server.state.project_path)
    asyncio.run(run())


def test_old_uncertain_queue_recovery_request_is_never_resent(tmp_path):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        state.update(status="RecoveryRequired", recoveryObservationOnly=True,
                     cleanupRecoverySendState="unknown", cleanupRecoveryRequestId="old-request")
        service.lose_response = True
        for _ in range(2):
            await assert_recovery_rejected(service, state, dry_run=False, confirm_token="old-token",
                                           expected_project_path=service.server.state.project_path)
        assert state["cleanupRecoverySendState"] == "unknown"
        assert state["cleanupRecoveryRequestId"] == "old-request"
    asyncio.run(run())


def test_removed_recovery_does_not_dispatch_even_when_persistence_fails(tmp_path, monkeypatch):
    async def run():
        service = QueueRecoveryService(tmp_path)
        state = await start_step(service)
        await stop_observers(service)
        direct = await service.operation_recover(state["operationId"], "not-granted")
        assert not direct.ok and direct.error.code == "LIFECYCLE_RECOVERY_REMOVED"
        assert not service.recoveries
        def broken(_):
            raise OSError("persistence unavailable")
        monkeypatch.setattr(service.server.state, "save_operation", broken)
        await assert_recovery_rejected(service, state)
        await assert_recovery_rejected(service, state, dry_run=False, confirm_token="old-token",
                                       expected_project_path=service.server.state.project_path)
    asyncio.run(run())


@pytest.mark.parametrize("code", ["CONNECTION_LOST", "COMMAND_RECOVERY_REQUIRED", "DOMAIN_RELOAD_TIMEOUT",
                                  "UPILOT_RECONNECT_PENDING"])
def test_original_step_transport_interruption_does_not_escalate_cleanup(tmp_path, code):
    async def run():
        service = StepService(tmp_path, [fail("q", code, "status transport interrupted"),
                                         {"status": "Succeeded", "cleanupSucceeded": True}])
        state = await start_step(service)
        await stop_observers(service)
        escalations = []
        async def escalate(response):
            escalations.append(response)
        service._maybe_auto_hard_stop = escalate
        original = copy.deepcopy({key: state.get(key) for key in
                                  ("operationId", "startData", "startedAt", "timeoutSec", "jobTimeoutAt")})
        first = await service.operation_status(state["operationId"])
        await asyncio.sleep(0)
        assert first.ok, first.error
        assert state["phase"] == "Recovering" and state["status"] == "Running"
        assert not state.get("terminal") and not state.get("endedAt")
        assert state.get("cleanupSucceeded") is not False
        assert not state.get("_cleanupEscalationObserved") and not escalations
        assert state["lastObservationError"] == "status transport interrupted"
        assert {key: state.get(key) for key in original} == original
        second = await service.operation_status(state["operationId"])
        assert second.ok and state["status"] == "Succeeded" and state["endedAt"]
        assert state["lastObservationError"] == ""
        assert service.reads == 2 and [name for name, _ in service.calls] == ["start"]
        assert not escalations
    asyncio.run(run())


@pytest.mark.parametrize("code", ["COMMAND_RECOVERY_REQUIRED", "UPILOT_RECONNECT_PENDING"])
@pytest.mark.parametrize("change", ["runId", "operationId", "missing_run", "unestablished"])
def test_transport_interruption_requires_audited_original_step_identity(tmp_path, change, code):
    async def run():
        service = StepService(tmp_path, [fail("q", code, "not an audited original read")])
        state = await start_step(service)
        await stop_observers(service)
        payload = state["resolvedStatusCall"]["payload"]
        if change == "missing_run":
            payload.pop("runId")
        elif change == "unestablished":
            state["recoveryIdentityEstablished"] = False
        else:
            payload[change] = "other"
        result = await service.operation_status(state["operationId"])
        assert not result.ok and result.error.code == "OPERATION_STATUS_FAILED"
        assert state["terminal"] and state["status"] == "aborted"
        assert service.reads == 1 and [name for name, _ in service.calls] == ["start"]
    asyncio.run(run())


@pytest.mark.parametrize("code", ["CONNECTION_LOST", "UPILOT_RECONNECT_PENDING"])
def test_retryable_step_observation_keeps_original_timeout_and_one_cancel(tmp_path, monkeypatch, code):
    async def run():
        service = StepService(tmp_path, [fail("q", code, "interrupted read"),
                                         {"status": "Canceled", "cleanupSucceeded": True}])
        state = await start_step(service)
        await stop_observers(service)
        deadline = state["startedAt"] + int(state["timeoutSec"] * 1000)
        clock = [deadline - 1]
        monkeypatch.setattr(task_service, "now_ms", lambda: clock[0])
        first = await service.operation_status(state["operationId"])
        assert first.ok and not state.get("timedOut") and not state.get("endedAt")
        assert state["startedAt"] + int(state["timeoutSec"] * 1000) == deadline
        clock[0] = deadline + 1
        second = await service.operation_status(state["operationId"])
        assert second.ok and state["timedOut"] and state["endedAt"]
        assert state["startedAt"] + int(state["timeoutSec"] * 1000) == deadline
        assert [name for name, _ in service.calls] == ["start", "cancel"]
        await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("status, public_status", [
    ("Succeeded", "Succeeded"), ("SucceededWithWarnings", "Succeeded"),
    ("Failed", "Failed"), ("Canceled", "Canceled"), ("TimedOut", "Timeout"),
])
@pytest.mark.parametrize("explicit_outcome", [True, False])
def test_step_business_outcome_survives_status_normalization_and_publication(
        tmp_path, status, public_status, explicit_outcome):
    async def run():
        sample = {"status": status}
        if explicit_outcome:
            sample["outcome"] = status
        service = StepService(tmp_path, [sample])
        state = await start_step(service, terminalStatusMapping={"success": ["SucceededWithWarnings"]})
        await stop_observers(service)
        result = await service.operation_status(state["operationId"])
        assert result.ok and result.data["terminal"]
        assert result.data["status"] == public_status
        assert result.data["outcome"] == status
        assert state["outcome"] == status
        assert service.server.state.load_operations()[0]["outcome"] == status
        # A late status cannot rewrite the original business result.
        service._merge_operation_status(state, {"status": "Failed", "outcome": "Failed"})
        late = await service.operation_status(state["operationId"])
        assert late.data["outcome"] == status and service.reads == 1
    asyncio.run(run())


@pytest.mark.parametrize("explicit_unknown", [None, "", "unknown"])
def test_step_explicit_unknown_outcome_is_not_inferred_from_success(tmp_path, explicit_unknown):
    async def run():
        service = StepService(tmp_path, [{"status": "Succeeded", "outcome": explicit_unknown}])
        state = await start_step(service)
        await stop_observers(service)
        result = await service.operation_status(state["operationId"])
        assert result.ok and result.data["terminal"]
        assert result.data["outcome"] == "unknown"
    asyncio.run(run())


def test_step_cleanup_timeout_does_not_replace_successful_business_outcome(tmp_path):
    async def run():
        service = StepService(tmp_path, [
            {"status": "Succeeded", "outcome": "Succeeded", "cleanupPending": True},
            {"status": "Succeeded", "outcome": "unknown", "cleanupPending": True},
        ])
        state = await start_step(service)
        await stop_observers(service)
        pending = await service.operation_status(state["operationId"])
        assert pending.data["status"] == "CleaningUp" and not pending.data["terminal"]
        assert pending.data["outcome"] == "Succeeded"
        state["cleanupDeadlineAt"] = 1
        final = await service.operation_status(state["operationId"])
        assert final.data["terminal"] and final.data["status"] == "timed_out"
        assert state["cleanupSucceeded"] is False
        assert final.data["businessResult"]["status"] == "Succeeded"
        assert final.data["outcome"] == "Succeeded"
        assert service.server.state.load_operations()[0]["outcome"] == "Succeeded"
    asyncio.run(run())


@pytest.mark.parametrize("identity_key", ["runId", "operationId"])
def test_step_foreign_terminal_cannot_publish_a_business_outcome(tmp_path, identity_key):
    async def run():
        service = StepService(tmp_path, [
            {"status": "Succeeded", "outcome": "Succeeded", identity_key: "foreign-run"},
        ])
        state = await start_step(service)
        await stop_observers(service)
        result = await service.operation_status(state["operationId"])
        assert not result.ok and result.error.code == "OPERATION_IDENTITY_MISMATCH"
        assert result.error.detail["outcome"] == "unknown"
        assert state.get("outcome", "unknown") == "unknown"
    asyncio.run(run())
