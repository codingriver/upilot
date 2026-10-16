import asyncio
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.protocol import now_ms
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore


class Tasks(TaskDomainService):
    def __init__(self, result):
        self._async_tasks, self._async_task_handles = {}, {}
        self.server = SimpleNamespace(state=StateStore(), is_ready=lambda: False)
        self.result = result
        self.dispatches = 0

    async def task_execute(self, **kwargs):
        self.dispatches += 1
        if isinstance(self.result, BaseException):
            raise self.result
        return self.result


@pytest.mark.parametrize("outcome", ["exception", "failure", "success", "cancelled"])
def test_background_task_reports_failure_without_a_status_poll_and_never_replays(caplog, outcome):
    result = {
        "exception": RuntimeError("background original stack"),
        "failure": fail("business", "TASK_FAILED", "business failure", {"stackTrace": "Existing.Business.Stack"}),
        "success": ok("business", {"done": True}),
        "cancelled": fail("business", "OPERATION_CANCELLED", "normal cancellation"),
    }[outcome]

    async def run():
        service = Tasks(result)
        started = await service.task_start("diagnostic", "unity_editor_state", retry_count=0)
        task_id = started.data["taskId"]
        await service._async_task_handles[task_id]
        expected_count = 1 if outcome in {"exception", "failure"} else 0
        diagnostics = [record for record in caplog.records if "Background task" in record.getMessage()]
        assert len(diagnostics) == expected_count
        if diagnostics:
            assert diagnostics[0].diagnostic_scope == task_id and diagnostics[0].diagnostic_terminal
            assert task_id in diagnostics[0].getMessage()
        if outcome == "exception":
            assert "RuntimeError: background original stack" in caplog.text and "task_execute" in caplog.text
        if outcome == "failure":
            assert "Existing.Business.Stack" in caplog.text
        for _ in range(3):
            assert (await service.task_status(task_id)).ok
        assert service.dispatches == 1
        assert len([record for record in caplog.records if "Background task" in record.getMessage()]) == expected_count

    with caplog.at_level("ERROR", logger="upilot.mcp"):
        asyncio.run(run())


def test_background_test_exception_and_final_persistence_failure_keep_original_stacks(caplog):
    class TestTasks(Tasks):
        async def _dispatch_tool(self, name, arguments):
            raise RuntimeError("test observer original failure")

    async def run():
        service = TestTasks(None)
        state = dict(taskId="test-task", toolName="unity_test_run", toolArgs={}, runGuid="original-run",
                     deadlineAt=now_ms() + 1000, terminal=False, startedAt=now_ms())
        saves = []

        def save(value):
            saves.append(dict(value))
            if len(saves) > 1:
                raise OSError("test final persistence failure")

        service.server.state.save_test_job = save
        await service._run_test_task(state)
        assert state["status"] == "aborted" and state["terminal"]
        assert state["error"]["code"] == "TEST_TASK_EXCEPTION"
        assert state["persistenceError"] == "test final persistence failure"
        assert len(saves) == 2

    with caplog.at_level("ERROR", logger="upilot.mcp"):
        asyncio.run(run())
    assert "RuntimeError: test observer original failure" in caplog.text
    assert "OSError: test final persistence failure" in caplog.text
    assert "_dispatch_tool" in caplog.text and "runGuid=original-run" in caplog.text
    diagnostics = [record for record in caplog.records if "Background test task" in record.getMessage()]
    assert len(diagnostics) == 2 and all(record.diagnostic_scope == "test-task" for record in diagnostics)
