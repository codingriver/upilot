"""Lifecycle contract regressions. Fixtures only: never restart an actual service."""
import asyncio
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain import queue_service
from upilot_mcp.queue_audit import audited
from upilot_mcp.responses import ok, fail
from upilot_mcp.state_store import StateStore
from test_queue_cleanup import Service


@pytest.mark.parametrize("kind", ["test", "operation"])
def test_terminal_once_and_new_service_cannot_revive_history(tmp_path, kind):
    store = StateStore()
    store.configure_project(str(tmp_path))
    key = "taskId" if kind == "test" else "operationId"
    save = store.save_test_job if kind == "test" else store.save_operation
    load = store.load_test_jobs if kind == "test" else store.load_operations
    state = {key: "original", "projectPath": str(tmp_path), "terminal": False, "status": "running"}
    save(state)
    state.update(terminal=True, endedAt=123, status="failed", error="original error")
    save(state)
    original = deepcopy(load()[0])
    state.update(terminal=False, endedAt=0, status="running")
    save(state)
    assert load() == [original]
    replacement = StateStore()
    replacement.configure_project(str(tmp_path))
    new_save = replacement.save_test_job if kind == "test" else replacement.save_operation
    new_load = replacement.load_test_jobs if kind == "test" else replacement.load_operations
    assert new_load() == []
    new_save(state)  # explicit old generation
    state.pop("lifecycleId")
    new_save(state)  # even a callback without generation cannot reuse an archived ID
    assert new_load() == []
    assert load() == [original]


@pytest.mark.parametrize("maintenance,automatic", [(False, False), (True, False), (False, True), (True, True)])
def test_soft_stop_escalation_requires_both_grants_and_merges(tmp_path, monkeypatch, maintenance, automatic):
    async def run():
        service = Service(tmp_path)
        monkeypatch.setattr(queue_service, "read_summary", lambda *args: dict(
            effectiveApproved=maintenance, autoHardStopOnSoftFailure=automatic,
            expectedServerProcessId=123, expectedBridgeSessionId="fixture", expectedMaintenanceId=""))
        service.service_restart = AsyncMock(return_value=fail("restart", "SERVICE_RESTART_TIMEOUT", "fixture"))
        responses = await asyncio.gather(*[service._maybe_auto_hard_stop(
            fail("stop", "SOFT_STOP_TIMEOUT", "fixture")) for _ in range(3)])
        assert service.service_restart.await_count == int(maintenance and automatic)
        if maintenance and automatic:
            assert service._automatic_hard_stop_id
            assert responses[0].data["hardStop"]["accepted"] is False
            await service._maybe_auto_hard_stop(fail("stop", "SOFT_STOP_TIMEOUT", "again"))
            assert service.service_restart.await_count == 1, "Restart failure must not cause a restart loop."
        else:
            assert all(response.data["hardStopAvailable"] for response in responses)
    asyncio.run(run())


@pytest.mark.parametrize("code", ["INVALID_TOOL_ARGUMENTS", "QUEUE_PROJECT_MISMATCH", "QUEUE_CLEANUP_NOT_APPROVED", "TASK_NOT_FOUND"])
def test_validation_failures_never_escalate(tmp_path, monkeypatch, code):
    async def run():
        service = Service(tmp_path)
        service.service_restart = AsyncMock()
        monkeypatch.setattr(queue_service, "read_summary", lambda *args: pytest.fail("Must not inspect restart authority"))
        await service._maybe_auto_hard_stop(fail("invalid", code, "fixture"))
        await service._maybe_auto_hard_stop(ok("missing", dict(status="not_found", changed=False)))
        service.service_restart.assert_not_called()
    asyncio.run(run())


def test_direct_soft_stop_exception_is_returned_and_escalated_once(tmp_path, monkeypatch):
    class Direct(Service):
        @audited("Test", "cancel", "run_guid")
        async def stop(self, run_guid):
            raise RuntimeError("fixture cleanup exception")
    async def run():
        service = Direct(tmp_path)
        service._maybe_auto_hard_stop = AsyncMock(side_effect=lambda response: response)
        result = await service.stop("original")
        assert result.error.code == "SOFT_STOP_FAILED"
        service._maybe_auto_hard_stop.assert_awaited_once()
    asyncio.run(run())


def test_generic_task_exception_is_terminal_and_repeat_cancel_is_noop(tmp_path):
    class Tasks(TaskDomainService):
        def __init__(self):
            self._async_tasks, self._async_task_handles = {}, {}
            self.server = SimpleNamespace(state=StateStore(), is_ready=lambda: False)
        async def task_execute(self, **kwargs):
            raise RuntimeError("fixture dispatch exception")
    async def run():
        service = Tasks()
        result = await service.task_start("fixture", "unity_editor_state", timeout_s=1)
        identity = result.data["taskId"]
        await service._async_task_handles[identity]
        state = (await service.task_status(identity)).data
        assert state["terminal"] and state["status"] == "failed" and state["endedAt"]
        original = deepcopy(service._async_tasks[identity])
        stopped = await service.task_cancel(identity)
        assert stopped.ok and stopped.data["status"] == "not_found" and not stopped.data["changed"]
        assert service._async_tasks[identity] == original
    asyncio.run(run())


def test_late_test_checkpoint_cannot_mutate_terminal_result():
    from upilot_mcp.test_job_context import TEST_JOB_CONTEXT, checkpoint
    state = dict(terminal=True, endedAt=123, status="timed_out", phase="timed_out")
    original = deepcopy(state)
    token = TEST_JOB_CONTEXT.set((state, lambda value: pytest.fail("Late callback persisted")))
    try:
        with pytest.raises(asyncio.CancelledError):
            checkpoint("observing", terminal=False, endedAt=0, status="running")
        assert state == original
    finally:
        TEST_JOB_CONTEXT.reset(token)


def test_capture_activity_does_not_restore_after_new_service(tmp_path):
    store = StateStore()
    store.configure_project(str(tmp_path))
    attachment = dict(projectPath=str(tmp_path), attachmentId="a", sessionId="capture",
                      requestKey="attach", detached=False)
    assert store.create_capture_attachment(attachment)[0] == "created"
    intent = dict(projectPath=str(tmp_path), requestKey="start", status="starting")
    store.save_capture_start_intent(intent)
    replacement = StateStore()
    replacement.configure_project(str(tmp_path))
    assert replacement.load_capture_attachments() == []
    assert replacement.load_capture_start_intent("start") is None
    assert replacement.create_capture_attachment(attachment) == ("stale", None)
    replacement.save_capture_start_intent(intent)
    assert replacement.load_capture_start_intent("start") is None
    assert store.load_capture_attachments()[0]["attachmentId"] == "a"


def test_disconnected_operation_deadline_ends_without_recovery(tmp_path):
    from test_operation_runner_and_agent_rules import _OperationService
    from test_persistent_operations import durable_spec, stop_observers
    from upilot_mcp.protocol import now_ms
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.seed_legacy_operation(durable_spec())
        await stop_observers(service)
        state = service._operations[started.data["operationId"]]
        service.server.is_ready = lambda: False
        state["startedAt"] = now_ms() - 400000
        await service._observe_operation(state)
        assert state["terminal"] and state["status"] == "timed_out"
        assert not state["cleanupPending"] and not state.get("businessTerminal")
        assert service.calls == [("start", {})]
    asyncio.run(run())


def test_implicit_stop_identity_does_not_share_deadline_with_next_run(tmp_path):
    class Direct(Service):
        @audited("Test", "cancel", "run_guid")
        async def stop(self, run_guid=""):
            return ok("fixture", dict(runGuid=self.run, status="not_found", changed=False))
    async def run():
        service = Direct(tmp_path)
        service._maybe_auto_hard_stop = AsyncMock(side_effect=lambda response: response)
        service.run = "first"
        assert (await service.stop()).ok
        service._soft_stop_deadlines[("Test", "first")] = 0
        service.run = "second"
        assert (await service.stop()).ok
        assert ("Test", "") not in service._soft_stop_deadlines
        assert service._soft_stop_deadlines[("Test", "first")] == 0
        assert service._soft_stop_deadlines[("Test", "second")] > 0
    asyncio.run(run())


def test_expired_cleanup_clock_does_not_hide_duplicate_or_permission_response(tmp_path):
    class Direct(Service):
        @audited("Test", "cancel", "run_guid")
        async def stop(self, run_guid):
            await asyncio.sleep(0.01)  # asynchronous Bridge response
            return self.response
    async def run():
        service = Direct(tmp_path)
        service._maybe_auto_hard_stop = AsyncMock(side_effect=lambda response: response)
        service._soft_stop_deadlines = {("Test", "original"): 0}
        for response in [ok("fixture", dict(status="not_found", changed=False)),
                         fail("fixture", "QUEUE_PROJECT_MISMATCH", "fixture")]:
            service.response = response
            assert await service.stop("original") is response
            assert service._soft_stop_deadlines[("Test", "original")] == 0
    asyncio.run(run())


def test_write_batch_deadline_releases_slot_even_when_database_unavailable(tmp_path, monkeypatch):
    import sqlite3
    from upilot_mcp import state_store
    store = StateStore()
    store.configure_project(str(tmp_path))
    batch = store.register_write_batch(["Probe.cs"], created_at=1000,
        files_sha256="fixture", compile_when_edit_mode=True)
    identity = batch["writeBatchId"]
    monkeypatch.setattr(state_store, "_now_ms", lambda: 601000)
    def unavailable(*args, **kwargs):
        raise sqlite3.OperationalError("fixture disk unavailable")
    with monkeypatch.context() as patch:
        patch.setattr(state_store.sqlite3, "connect", unavailable)
        store.expire_write_batches()
        result = store.get_write_batch(identity)
        assert result["terminal"] and result["status"] == "timed_out"
        assert "fixture disk unavailable" in result["persistenceError"]
        assert not store.pending_write_batch_id
        assert store.pending_write_batches() == []
    # Even when disk is available again, the old nonterminal row cannot revive it.
    assert store.get_write_batch(identity)["status"] == "timed_out"
    store.mark_write_batch(identity, "compiling")
    assert store.get_write_batch(identity)["status"] == "timed_out"


def test_public_tool_help_matches_bounded_stop_and_empty_restart_contract():
    from upilot_mcp import mcp_stdio_server as runtime
    tools = {tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())}
    restart = tools["unity_service_restart"].description
    assert "120" in restart and "30–600" in restart
    assert "仅权威 EditMode" not in restart and "旧任务不恢复" in restart
    cleanup = tools["unity_queue_cleanup"].description
    assert "ending/not_found" in cleanup and "60 秒" in cleanup
    assert "不再提供 recover/release/abandon" in cleanup
    assert "只观察式恢复" not in tools["unity_task_start"].description
    assert "RecoveryRequired" not in tools["unity_operation_wait"].description
