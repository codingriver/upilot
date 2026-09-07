import asyncio
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.protocol import now_ms
from upilot_mcp.responses import ok
from upilot_mcp.state_store import StateStore
from upilot_mcp.test_job_context import checkpoint
from upilot_mcp.mcp_tools import test_tools  # noqa: F401
from test_acceptance_compact_shader import _AcceptanceService


class Service(TaskDomainService, TestDomainService):
    def __init__(self, root: Path):
        store = StateStore()
        store.configure_project(str(root))
        self.server = SimpleNamespace(state=store)
        self._async_tasks = {}
        self._async_task_handles = {}
        self.start_count = 0
        self.cancels = []
        self.complete = True
        self.cleanup = True
        self.cancelled = False

    async def _dispatch_tool(self, name, args):
        checkpoint("starting", startIntentSent=True)
        self.start_count += 1
        checkpoint("observing", runGuid="run-persisted")
        return ok("start", {"status": "running", "runGuid": "run-persisted"})

    async def test_results(self, run_guid=""):
        return ok("result", {
            "runGuid": run_guid, "status": ("aborted" if self.cancelled else "completed") if self.complete else "running",
            "resultAuthoritative": True, "cleanupPending": not self.cleanup, "cleanupSucceeded": self.cleanup,
            "cleanupStatus": "completed" if self.cleanup else "cleaning_up",
            "cleanupErrors": [], "unresolvedResources": [] if self.cleanup else ["callback"],
            "total": 1, "failed": 0, "passed": 1,
        })

    async def test_cancel(self, run_guid=""):
        self.cancels.append(run_guid)
        self.cancelled = True
        self.complete = True
        return ok("cancel", {"cancelAccepted": True, "runGuid": run_guid})


def test_background_test_finishes_without_client_poll_and_survives_facade_recreation(tmp_path):
    async def scenario():
        first = Service(tmp_path)
        started = await first.task_start("focused", "unity_test_run", retry_count=0)
        task_id = started.data["taskId"]
        await first._async_task_handles[task_id]
        assert first.start_count == 1
        second = Service(tmp_path)
        status = await second.task_status(task_id)
        assert status.data["status"] == "completed"
        assert status.data["terminal"] and status.data["runGuid"] == "run-persisted"
        assert second.start_count == 0
    asyncio.run(scenario())


def test_recovery_observes_known_guid_and_never_replays_start(tmp_path):
    async def scenario():
        service = Service(tmp_path)
        service.server.state.save_test_job({
            "taskId": "task-recover", "taskName": "recover", "toolName": "unity_test_run",
            "projectPath": str(tmp_path.resolve()), "durable": True, "status": "running",
            "phase": "observing", "terminal": False, "runGuid": "run-persisted", "startedAt": now_ms(),
            "deadlineAt": now_ms() + 60000, "endedAt": 0,
        })
        service._recover_test_jobs()
        await service._async_task_handles["task-recover"]
        assert service._async_tasks["task-recover"]["status"] == "completed"
        assert service.start_count == 0
    asyncio.run(scenario())


def test_unknown_start_stays_recovery_required_and_blocks_a_second_start(tmp_path):
    async def scenario():
        service = Service(tmp_path)
        service.server.state.save_test_job({
            "taskId": "task-unknown", "projectPath": str(tmp_path.resolve()), "durable": True,
            "status": "running", "terminal": False, "runGuid": "", "startedAt": now_ms(), "endedAt": 0,
        })
        status = await service.task_status("task-unknown")
        assert status.data["status"] == "RecoveryRequired"
        assert not status.data["terminal"] and not service._async_task_handles
        assert not (await service.task_start("another", "unity_test_run")).ok
        assert service.start_count == 0
    asyncio.run(scenario())


def test_cancellation_waits_for_underlying_guid_and_cleanup(tmp_path):
    async def scenario():
        service = Service(tmp_path)
        service.complete = False
        service.cleanup = False
        start = await service.task_start("cancel", "unity_test_run")
        task_id = start.data["taskId"]
        await asyncio.sleep(0)
        cancelling = await service.task_cancel(task_id)
        assert cancelling.data["status"] == "cancel_requested"
        assert not cancelling.data["terminal"]
        await asyncio.sleep(1.1)
        assert service.cancels == ["run-persisted"]
        assert not service._async_tasks[task_id]["terminal"]
        service.cleanup = True
        await service._async_task_handles[task_id]
        assert service._async_tasks[task_id]["status"] == "cancelled"
        repeated = await service.task_cancel(task_id)
        assert repeated.data["status"] == "cancelled" and len(service.cancels) == 1
    asyncio.run(scenario())


def test_retry_and_generic_cancellation_are_not_false_business_success(tmp_path):
    async def scenario():
        service = Service(tmp_path)
        assert not (await service.task_start("unsafe", "unity_test_run", retry_count=1)).ok
        service._async_tasks["plain"] = {"taskId": "plain", "status": "running", "terminal": False}
        result = await service.task_cancel("plain")
        assert not result.ok and result.error.code == "TASK_CANCELLATION_UNSUPPORTED"
        assert service._async_tasks["plain"]["status"] == "running"
    asyncio.run(scenario())


def test_persisted_jobs_are_project_isolated(tmp_path):
    first = Service(tmp_path / "first")
    first.server.state.save_test_job({"taskId": "task-a", "projectPath": str((tmp_path / "first").resolve())})
    second = Service(tmp_path / "second")
    assert second.server.state.load_test_jobs() == []


class AcceptanceService(TaskDomainService, _AcceptanceService):
    def __init__(self, output_root):
        expected = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()
        _AcceptanceService.__init__(self, expected)
        store = StateStore()
        store.configure_project(str(output_root))
        self.server = SimpleNamespace(state=store)
        self._async_tasks = {}
        self._async_task_handles = {}
        self.output_root = output_root
        self.start_count = 0
        self.hold_result = asyncio.Event()
        self.hold_result.set()

    async def _dispatch_tool(self, name, args):
        return await self.upilot_acceptance_run(**args)

    async def ensure_ready(self, **kwargs):
        return await _AcceptanceService.ensure_ready(self, **kwargs)

    async def test_run(self, **kwargs):
        self.start_count += 1
        return await super().test_run(**kwargs)

    async def test_results(self, run_guid=""):
        await self.hold_result.wait()
        return await super().test_results(run_guid)

    def _finish_acceptance_report(self, report, passed, code="", message=""):
        # Isolate report files from the canonical project's live evidence.
        report["expectedProject"] = str(self.output_root)
        return TestDomainService._finish_acceptance_report(report, passed, code, message)


def test_acceptance_summary_finishes_without_polling_and_has_verified_hash(tmp_path):
    async def scenario():
        service = AcceptanceService(tmp_path)
        start = await service.task_start("acceptance", "unity_upilot_acceptance_run", {"write_artifact": True})
        task_id = start.data["taskId"]
        await service._async_task_handles[task_id]
        state = service._async_tasks[task_id]
        assert state["status"] == "completed" and service.start_count == 1, state.get("error")
        artifact = state["artifact"]
        content = Path(artifact["path"]).read_bytes()
        assert hashlib.sha256(content).hexdigest() == artifact["sha256"]
        assert len(content) == artifact["bytes"]
        assert json.loads(content)["acceptancePassed"] is True
        summary = await service.task_status(task_id)
        assert "acceptanceReport" not in summary.data
        assert summary.data["result"]["result"]["acceptancePassed"] is True
        assert summary.data["tests"]["passed"] == 1
    asyncio.run(scenario())


def test_interrupted_acceptance_reattaches_without_start_and_writes_summary(tmp_path):
    async def scenario():
        first = AcceptanceService(tmp_path)
        first.hold_result.clear()
        started = await first.task_start("recover-acceptance", "unity_upilot_acceptance_run", {"write_artifact": True})
        task_id = started.data["taskId"]
        for _ in range(30):
            await asyncio.sleep(0)
            if first._async_tasks[task_id].get("runGuid"):
                break
        assert first._async_tasks[task_id]["runGuid"] == "run-1", first._async_tasks[task_id].get("error")
        first._async_task_handles[task_id].cancel()
        await asyncio.gather(first._async_task_handles[task_id], return_exceptions=True)
        second = AcceptanceService(tmp_path)
        second._recover_test_jobs()
        await second._async_task_handles[task_id]
        state = second._async_tasks[task_id]
        assert state["status"] == "completed"
        assert first.start_count == 1 and second.start_count == 0
        assert Path(state["artifact"]["path"]).is_file()
    asyncio.run(scenario())


def test_generic_task_execute_refuses_retries_for_non_idempotent_tool(tmp_path):
    service = Service(tmp_path)
    result = asyncio.run(service.task_execute("unsafe", "unity_prefab_patch", retry_count=1))
    assert not result.ok and result.error.code == "TASK_RETRY_UNSAFE"
    assert service.start_count == 0
