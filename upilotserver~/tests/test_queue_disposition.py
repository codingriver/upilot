"""Administrative release is evidence-based, durable and never business success."""
import asyncio
import copy
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.queue_service import QueueDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.protocol import now_ms
from upilot_mcp.responses import ok
from test_editor_execution_state_v2 import _snapshot
from test_recovery_observation import QueueRecoveryService, start_step
from test_persistent_operations import stop_observers


class Service(QueueRecoveryService, QueueDomainService, TestDomainService):
    def __init__(self, root):
        super().__init__(root)
        snapshot = _snapshot(1, observed_at=now_ms())
        snapshot.update(mainThreadQueueDepth=0, lastMainThreadPumpAt=now_ms())
        self.server.state.update_editor_execution_state(snapshot)
        self._async_tasks, self._async_task_handles = {}, {}
        self.releases = []
        self.proof_hash = "a" * 64
        self.remote_disposition = None
        self.visible = True
        self.test_sample = dict(runGuid="test-original", status="failed", cleanupSucceeded=True,
            cleanupPending=False, cleanupStatus="completed", cleanupErrors=[], unresolvedResources=[],
            runnerState="inactive", resultAuthoritative=True, total=1, passed=0, failed=1)

    async def bridge_call(self, request_id, route, payload, **kwargs):
        if route == "automation.steps.release":
            self.releases.append(dict(payload))
            self.remote_disposition = dict(requestId=payload["dispositionRequestId"], runId=payload["runId"],
                operationId=payload["operationId"], backupPath="original-unity-backup", backupBytes=500,
                backupSha256="b" * 64)
            if self.lose_response:
                raise TimeoutError("lost response")
        if route in {"automation.steps.release_preview", "automation.steps.state", "automation.steps.release"}:
            released = bool(self.remote_disposition and self.visible)
            return ok(request_id, dict(operationId=payload["operationId"], runId=payload["runId"],
                status="Released" if released else "RecoveryRequired", terminal=released,
                cleanupPending=not released, disposition=self.remote_disposition if released else None,
                releaseProof=dict(eligible=True, operationId=payload["operationId"], runId=payload["runId"],
                                  stateHash=self.proof_hash, adapter="builtin-wait-v1")))
        return ok(request_id, {})

    async def test_results(self, run_guid=""):
        return ok("sample", dict(self.test_sample))


async def preview(service, kind, target):
    args = dict(target_type=kind, target_id=target, action="release", reason="original report unavailable")
    result = await service.queue_cleanup(**args)
    assert result.ok, result
    return dict(args, dry_run=False, confirm_token=result.data["confirmToken"],
                expected_project_path=service.server.state.project_path)


async def prepare_operation(service):
    state = await start_step(service)
    await stop_observers(service)
    state.update(status="RecoveryRequired", phase="StatusCallFailed", recoveryObservationOnly=True)
    return state


def prepare_task(service):
    state = dict(taskId="task-original", projectPath=service.server.state.project_path, durable=True,
                 runGuid="test-original", status="RecoveryRequired", phase="result_lost", terminal=False,
                 endedAt=0, startedAt=now_ms(), startSendState="response_received", recoveryObservationOnly=True)
    service._async_tasks[state["taskId"]] = state
    service.server.state.save_test_job(state)
    return state


def test_release_operation_is_backed_up_not_success_and_survives_restart(tmp_path):
    async def run():
        service = Service(tmp_path)
        state = await prepare_operation(service)
        args = await preview(service, "Operation", state["operationId"])
        assert not service.releases
        original = copy.deepcopy(state)
        result = await service.queue_cleanup(**args)
        assert result.ok and result.data["status"] == "Released", result
        assert result.data["terminal"] and not result.data["businessTerminal"]
        assert result.data["outcome"] == "unknown" and not result.data["originalTerminal"]
        d = result.data["disposition"]
        blob = Path(d["backupPath"]).read_bytes()
        assert hashlib.sha256(blob).hexdigest() == d["backupSha256"]
        assert json.loads(blob)["state"]["status"] == "RecoveryRequired"
        assert len(service.releases) == 1 and [name for name, _ in service.calls] == ["start"]
        restored = Service(tmp_path)
        final = await restored.operation_status(state["operationId"])
        assert final.data["status"] == "Released" and final.data["terminal"]
        assert not restored.releases
        waited = await restored.operation_wait(state["operationId"], timeout_s=0.1)
        assert waited.data["terminal"]
        with pytest.raises(ValueError, match="IMMUTABLE"):
            service.server.state.save_operation(original)
        assert not (await service.queue_cleanup(**args)).ok
        await stop_observers(service); await stop_observers(restored)
    asyncio.run(run())


def test_uncertain_release_observes_same_request_across_server_restart_never_resends(tmp_path):
    async def run():
        service = Service(tmp_path)
        state = await prepare_operation(service)
        args = await preview(service, "Operation", state["operationId"])
        service.lose_response = True; service.visible = False
        result = await service.queue_cleanup(**args)
        assert result.ok and not result.data["terminal"]
        assert len(service.releases) == 1
        await stop_observers(service)
        restored = Service(tmp_path); restored.remote_disposition = service.remote_disposition
        restored._recover_operations()
        restored._operations[state["operationId"]]["nextRecoveryObservationAt"] = 0
        result = await restored.operation_status(state["operationId"])
        assert result.data["status"] == "Released" and not restored.releases
        assert result.data["releaseRequestId"] == service.releases[0]["dispositionRequestId"]
        await stop_observers(restored)
    asyncio.run(run())


@pytest.mark.parametrize("fault", ["changed", "grant", "pending", "backup", "persistence", "identity", "capture"])
def test_release_rejects_unsafe_operation_without_sending(tmp_path, fault):
    async def run():
        service = Service(tmp_path); state = await prepare_operation(service)
        args = await preview(service, "Operation", state["operationId"])
        if fault == "changed": service.proof_hash = "c" * 64
        if fault == "grant":
            folder = tmp_path / ".upilot"; folder.mkdir(exist_ok=True)
            (folder / "config.json").write_text('{"aiQueueCleanupAllowed":false}')
        if fault == "pending": service.server._pending = {"original": object()}
        if fault == "identity": state["startData"]["runId"] = "other"
        if fault == "capture": state["consoleCapture"] = {"sessionId": "original-capture"}
        if fault == "backup": service.server.state.backup_job_disposition = lambda *a, **kw: (_ for _ in ()).throw(OSError("backup"))
        if fault == "persistence": service.server.state.save_operation = lambda *a, **kw: (_ for _ in ()).throw(OSError("persist"))
        result = await service.queue_cleanup(**args)
        assert not result.ok, result
        assert not service.releases and not state.get("endedAt")
        await stop_observers(service)
    asyncio.run(run())


def test_task_release_retains_partial_failed_test_result_and_removes_only_its_admission_block(tmp_path):
    async def run():
        service = Service(tmp_path); state = prepare_task(service); original = dict(state)
        args = await preview(service, "Task", state["taskId"])
        result = await service.queue_cleanup(**args)
        assert result.ok and result.data["status"] == "Released", result
        assert result.data["terminal"] and not result.data["businessTerminal"]
        assert result.data["outcome"] == "unknown"
        assert result.data["originalTestResult"]["failed"] == 1
        assert service.server.state.load_test_jobs()[0]["status"] == "Released"
        with pytest.raises(ValueError, match="IMMUTABLE"):
            service.server.state.save_test_job(original)
        restored = Service(tmp_path)
        status = await restored.task_status(state["taskId"])
        assert status.data["terminal"] and status.data["status"] == "Released"
        assert not restored._async_task_handles
    asyncio.run(run())


@pytest.mark.parametrize("field,value", [("runGuid", "other"), ("runnerState", "unknown"),
    ("cleanupPending", True), ("resultAuthoritative", False), ("unresolvedResources", ["callback"])])
def test_task_release_requires_original_authoritative_execution_and_resource_proof(tmp_path, field, value):
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        service.test_sample[field] = value
        result = await service.queue_cleanup(target_type="Task", target_id=state["taskId"], action="release", reason="test")
        assert not result.ok and not state.get("disposition") and not state["terminal"]
    asyncio.run(run())


@pytest.mark.parametrize("fault", ["finalizing", "became_terminal", "uncertain_cancel"])
def test_task_release_rechecks_state_after_awaiting_test_proof(tmp_path, fault):
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        async def query(run_guid=""):
            await asyncio.sleep(0)
            if fault == "finalizing": state["_recoveryFinalizing"] = True
            if fault == "became_terminal": state.update(status="failed", terminal=True)
            if fault == "uncertain_cancel": state["cancelSendState"] = "sent_unknown"
            return ok("query", dict(service.test_sample))
        service.test_results = query
        result = await service.queue_cleanup(target_type="Task", target_id=state["taskId"], action="release", reason="test")
        assert not result.ok and not state.get("disposition")
    asyncio.run(run())


def test_released_task_allows_new_test_but_does_not_bypass_another_blocker(tmp_path):
    from upilot_mcp.test_job_context import checkpoint
    from upilot_mcp.mcp_tools import test_tools  # registers the real admission contract
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        args = await preview(service, "Task", state["taskId"])
        assert (await service.queue_cleanup(**args)).data["status"] == "Released"
        starts = []
        async def start(name, args):
            starts.append(name)
            checkpoint("observing", runGuid="next-test")
            service.test_sample["runGuid"] = "next-test"
            return ok("start", {"runGuid": "next-test", "status": "running"})
        service._dispatch_tool = start
        next_task = await service.task_start("next", "unity_test_run", retry_count=0)
        assert next_task.ok, next_task
        await service._async_task_handles[next_task.data["taskId"]]
        assert starts == ["unity_test_run"] and state["status"] == "Released"
        blocker = dict(state, taskId="other", status="RecoveryRequired", terminal=False, disposition=None)
        service._async_tasks["other"] = blocker
        refused = await service.task_start("another", "unity_test_run", retry_count=0)
        assert not refused.ok and refused.error.code == "TEST_TASK_ALREADY_ACTIVE"
        assert starts == ["unity_test_run"]
    asyncio.run(run())


def test_task_release_persistence_failure_keeps_original_blocker(tmp_path):
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        args = await preview(service, "Task", state["taskId"])
        save = service.server.state.save_test_job
        def broken(candidate):
            if candidate.get("status") == "Released": raise OSError("disk full")
            save(candidate)
        service.server.state.save_test_job = broken
        result = await service.queue_cleanup(**args)
        assert not result.ok and state["status"] == "RecoveryRequired" and not state["terminal"]
        assert service.server.state.load_test_jobs()[0]["status"] == "RecoveryRequired"
    asyncio.run(run())


def test_late_observer_result_does_not_resurrect_released_task(tmp_path):
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        entered, resume = asyncio.Event(), asyncio.Event()
        async def query(run_guid=""):
            entered.set(); await resume.wait()
            return ok("query", dict(service.test_sample))
        service.test_results = query
        observer = asyncio.create_task(service._observe_test_recovery(state))
        await entered.wait()
        service.test_results = lambda run_guid="": asyncio.sleep(0, result=ok("query", dict(service.test_sample)))
        args = await preview(service, "Task", state["taskId"])
        result = await service.queue_cleanup(**args)
        assert result.ok and result.data["status"] == "Released"
        resume.set(); await observer
        assert state["status"] == "Released" and service.server.state.load_test_jobs()[0]["status"] == "Released"
    asyncio.run(run())


@pytest.mark.parametrize("detail", ["summary", "full"])
def test_released_acceptance_never_exposes_old_pass_as_current_result(tmp_path, detail):
    async def run():
        service = Service(tmp_path); state = prepare_task(service)
        # A partial report may have marked tests passed before outer finalization failed.
        old = {"acceptancePassed": True, "runGuid": state["runGuid"]}
        state.update(acceptanceReport=old, result={"result": old})
        service.server.state.save_test_job(state)
        args = await preview(service, "Task", state["taskId"])
        assert (await service.queue_cleanup(**args)).ok
        assert "acceptanceReport" not in state
        assert state["originalAcceptanceReport"] == old
        assert state["originalTaskResult"] == {"result": old}
        restored_service = Service(tmp_path)
        restored = (await restored_service.task_status(state["taskId"], detail_level="full")).data
        assert "acceptanceReport" not in restored
        assert restored["originalAcceptanceReport"] == old
        result = await service.task_status(state["taskId"], detail_level=detail)
        assert result.data["result"]["result"]["acceptancePassed"] is False
        assert result.data["result"]["result"]["status"] == "Released"
        assert state["originalAcceptanceReport"]["acceptancePassed"] is True
        assert state["originalTaskResult"]["result"] == old
        assert json.loads(Path(state["disposition"]["backupPath"]).read_text())["state"]["acceptanceReport"] == old
    asyncio.run(run())
