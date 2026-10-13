import asyncio
import hashlib
import json
import sqlite3
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.domain.queue_service import QueueDomainService
from upilot_mcp.protocol import now_ms
from upilot_mcp.responses import fail, ok
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


def test_background_test_finishes_without_poll_but_new_service_starts_empty(tmp_path):
    async def scenario():
        first = Service(tmp_path)
        started = await first.task_start("focused", "unity_test_run", retry_count=0)
        task_id = started.data["taskId"]
        await first._async_task_handles[task_id]
        assert first.start_count == 1
        # Concurrent observations in the same lifetime neither restart work nor change deadlines.
        original = dict(first._async_tasks[task_id])
        responses = await asyncio.gather(*[first.task_status(task_id) for _ in range(3)])
        assert all(response.data["status"] == "completed" and response.data["terminal"]
                   and response.data["runGuid"] == "run-persisted" for response in responses)
        assert first._async_tasks[task_id] == original and first.start_count == 1
        history = first.server.state.load_test_jobs()
        second = Service(tmp_path)
        status = await second.task_status(task_id)
        assert not status.ok and status.error.code == "TASK_NOT_FOUND"
        assert not second._async_task_handles and second.start_count == 0
        assert first.server.state.load_test_jobs() == history
    asyncio.run(scenario())


@pytest.mark.parametrize("run_guid", ["run-persisted", ""])
def test_historical_job_never_recreates_observer_or_replays_start(tmp_path, run_guid):
    async def scenario():
        service = Service(tmp_path)
        service.server.state.save_test_job({
            "taskId": "task-history", "taskName": "history", "toolName": "unity_test_run",
            "projectPath": str(tmp_path.resolve()), "durable": True, "status": "running",
            "phase": "observing", "terminal": False, "runGuid": run_guid, "startedAt": now_ms(),
            "deadlineAt": now_ms() + 60000, "endedAt": 0,
        })
        history = service.server.state.load_test_jobs()
        for _ in range(2):
            service._recover_test_jobs()
            status = await service.task_status("task-history")
            assert not status.ok and status.error.code == "TASK_NOT_FOUND"
        assert not service._async_task_handles and not service._async_tasks
        assert service.start_count == 0 and service.cancels == []
        assert service.server.state.load_test_jobs() == history
    asyncio.run(scenario())



@pytest.mark.parametrize("late_outcome", ["completed", "failed", "exception"])
def test_late_background_result_cannot_mutate_ended_task_or_running_successor(tmp_path, late_outcome):
    # Exercise real task_start/_run_test_task/_wait_for_test_result, not just a
    # direct checkpoint or persistence call. No service/process restart occurs.
    from upilot_mcp.operation_context import REQUEST_ORIGIN
    from upilot_mcp.test_job_context import TEST_JOB_CONTEXT

    async def scenario():
        original_entered = asyncio.Event()
        original_cancelled = asyncio.Event()
        original_release = asyncio.Event()
        successor_entered = asyncio.Event()
        successor_release = asyncio.Event()
        observed_guids = []
        work_handles = {}

        class DelayedService(Service):
            async def _dispatch_tool(self, name, args):
                checkpoint("starting", startIntentSent=True)
                self.start_count += 1
                guid = f"run-delayed-{self.start_count}"
                checkpoint("observing", runGuid=guid)
                return ok("start", {"status": "running", "runGuid": guid})

            async def test_results(self, run_guid=""):
                observed_guids.append(run_guid)
                work_handles[run_guid] = asyncio.current_task()
                if run_guid == "run-delayed-1":
                    original_entered.set()
                    try:
                        await original_release.wait()
                    except asyncio.CancelledError:
                        # A transport/underlying callback may outlive its observer.
                        original_cancelled.set()
                        await original_release.wait()
                    with pytest.raises(asyncio.CancelledError):
                        checkpoint("observing", runGuid="late-unrelated-run")
                    if late_outcome == "exception":
                        raise RuntimeError("Original callback failed after its observer ended.")
                else:
                    assert run_guid == "run-delayed-2"
                    successor_entered.set()
                    await successor_release.wait()
                result = await super().test_results(run_guid)
                if run_guid == "run-delayed-1" and late_outcome == "failed":
                    result.data.update(status="failed", failed=1, passed=0)
                return result

        service = DelayedService(tmp_path)
        handles = []
        context_before = TEST_JOB_CONTEXT.get()
        origin_before = REQUEST_ORIGIN.get()

        async def start_with_origin(name, client):
            token = REQUEST_ORIGIN.set({"clientId": client, "chatId": name})
            try:
                response = await service.task_start(name, "unity_test_run", retry_count=0)
            finally:
                REQUEST_ORIGIN.reset(token)
            assert response.ok
            identity = response.data["taskId"]
            handle = service._async_task_handles[identity]
            handles.append(handle)
            return identity, handle

        try:
            original_id, original_handle = await start_with_origin("original", "client-a")
            await asyncio.wait_for(original_entered.wait(), timeout=5)
            original = service._async_tasks[original_id]
            original_deadline = original["deadlineAt"]
            original_handle.cancel()
            with pytest.raises(asyncio.CancelledError):
                await asyncio.wait_for(original_handle, timeout=5)
            await asyncio.wait_for(original_cancelled.wait(), timeout=5)
            assert original["terminal"] and original["status"] == "aborted" and original["endedAt"]
            assert original["deadlineAt"] == original_deadline
            original_frozen = json.loads(json.dumps(original))

            successor_id, successor_handle = await start_with_origin("successor", "client-b")
            await asyncio.wait_for(successor_entered.wait(), timeout=5)
            successor = service._async_tasks[successor_id]
            successor_frozen = json.loads(json.dumps(successor))
            assert successor_id != original_id
            assert not successor["terminal"] and not successor_handle.done()
            assert successor["runGuid"] == "run-delayed-2"
            assert original["requestOrigin"]["clientId"] == "client-a"
            assert successor["requestOrigin"]["clientId"] == "client-b"

            uri = service.server.state._db_path.as_uri() + "?mode=ro"
            query = "SELECT task_id, state_json FROM test_jobs WHERE project_path=? ORDER BY task_id"
            with sqlite3.connect(uri, uri=True) as db:
                frozen_rows = db.execute(query, (str(tmp_path.resolve()),)).fetchall()
            assert len(frozen_rows) == 2

            original_release.set()
            original_work = work_handles["run-delayed-1"]
            if late_outcome == "exception":
                with pytest.raises(RuntimeError, match="Original callback failed"):
                    await asyncio.wait_for(original_work, timeout=5)
            else:
                late_result = await asyncio.wait_for(original_work, timeout=5)
                assert late_result.ok and late_result.data["status"] == late_outcome
            assert original == original_frozen
            assert successor == successor_frozen and not successor_handle.done()
            with sqlite3.connect(uri, uri=True) as db:
                assert db.execute(query, (str(tmp_path.resolve()),)).fetchall() == frozen_rows
            assert service.start_count == 2 and service.cancels == []
            assert observed_guids == ["run-delayed-1", "run-delayed-2"]
            assert TEST_JOB_CONTEXT.get() is context_before
            assert REQUEST_ORIGIN.get() is origin_before

            successor_release.set()
            await asyncio.wait_for(successor_handle, timeout=5)
            assert successor["terminal"] and successor["status"] == "completed"
            assert successor["runGuid"] == successor_frozen["runGuid"]
            assert successor["deadlineAt"] == successor_frozen["deadlineAt"]
            assert successor["requestOrigin"] == successor_frozen["requestOrigin"]
            assert original == original_frozen
            with sqlite3.connect(uri, uri=True) as db:
                rows = dict(db.execute(query, (str(tmp_path.resolve()),)).fetchall())
            assert rows[original_id] == dict(frozen_rows)[original_id]
            assert json.loads(rows[successor_id])["terminal"] is True
            repeated = await service.task_cancel(original_id)
            assert repeated.ok and repeated.data["status"] == "not_found"
            assert repeated.data["changed"] is False and service.cancels == []
            assert original == original_frozen
        finally:
            original_release.set()
            successor_release.set()
            all_handles = set(handles) | set(work_handles.values())
            for handle in all_handles:
                if not handle.done():
                    handle.cancel()
            await asyncio.gather(*all_handles, return_exceptions=True)

    asyncio.run(scenario())


def test_cancellation_waits_for_underlying_guid_and_cleanup(tmp_path):
    async def scenario():
        service = Service(tmp_path)
        service.complete = False
        service.cleanup = False
        start = await service.task_start("cancel", "unity_test_run")
        task_id = start.data["taskId"]
        for _ in range(30):
            await asyncio.sleep(0)
            if service._async_tasks[task_id].get("runGuid"):
                break
        assert service._async_tasks[task_id]["runGuid"] == "run-persisted"
        deadline = service._async_tasks[task_id]["deadlineAt"]
        cancelling = await service.task_cancel(task_id)
        assert cancelling.data["status"] == "ending"
        assert cancelling.data["phase"] == "cancel_requested"
        assert not cancelling.data["terminal"]
        await asyncio.sleep(1.1)
        assert service.cancels == ["run-persisted"]
        assert not service._async_tasks[task_id]["terminal"]
        service.cleanup = True
        await service._async_task_handles[task_id]
        assert service._async_tasks[task_id]["status"] == "cancelled"
        repeated = await service.task_cancel(task_id)
        assert repeated.ok and repeated.data["status"] == "not_found" and repeated.data["changed"] is False
        assert service._async_tasks[task_id]["status"] == "cancelled" and len(service.cancels) == 1
        assert service._async_tasks[task_id]["deadlineAt"] == deadline
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
        # Keep the database isolated, but model the exact authorized project identity.
        store._project_path = str(expected)
        self.server = SimpleNamespace(state=store)
        self._async_tasks = {}
        self._async_task_handles = {}
        self.output_root = output_root
        self.start_count = 0
        self.dispatcher = self
        self.dispatch_calls = []
        self.hold_result = asyncio.Event()
        self.hold_result.set()

    async def call(self, _request_id, name, payload, **_kwargs):
        self.dispatch_calls.append((name, payload))
        if name == "scene.list":
            return ok("scenes", {"scenes": []})
        raise AssertionError(f"Unexpected acceptance dispatcher call: {name}")

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


@pytest.fixture
def acceptance_gate(monkeypatch):
    from upilot_mcp.domain import task_service
    monkeypatch.setattr(task_service, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(task_service.CONFIG, "write_access_approved", True)


class EscalatingAcceptanceService(AcceptanceService, QueueDomainService):
    """Real task finalization/escalation; process maintenance is always mocked."""
    def __init__(self, output_root):
        super().__init__(output_root)
        self.server.session_manager = SimpleNamespace(active=SimpleNamespace(
            project_path=self.server.state.project_path))
        self.server.is_ready = lambda: False
        self.service_restart = AsyncMock(return_value=ok("restart", {}))


@pytest.fixture
def escalation_gate(monkeypatch):
    from upilot_mcp.domain import queue_service
    monkeypatch.setattr(queue_service, "read_summary", lambda *_args: dict(
        effectiveApproved=True, autoHardStopOnSoftFailure=True,
        expectedServerProcessId=123, expectedBridgeSessionId="fixture", expectedMaintenanceId=""))


@pytest.mark.parametrize("case,code,status", [
    ("stale_snapshot", "TEST_SELECTION_STALE", "failed"),
    ("no_tests", "UPILOT_ACCEPTANCE_NO_TESTS", "no_tests"),
    ("not_ready", "UPILOT_ACCEPTANCE_NOT_READY", "failed"),
    ("compile_budget", "UPILOT_ACCEPTANCE_DEADLINE_EXCEEDED", "failed"),
    ("active_capture", "UPILOT_ACCEPTANCE_CAPTURE_OWNERSHIP_REQUIRED", "failed"),
    ("unknown_capture", "UPILOT_ACCEPTANCE_CAPTURE_STATE_UNKNOWN", "failed"),
    ("runner_preflight", "TEST_SELECTION_STALE", "failed"),
])
def test_acceptance_pre_dispatch_failures_have_no_runner_cleanup_or_restart(
        tmp_path, monkeypatch, acceptance_gate, escalation_gate, case, code, status):
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        arguments = {"write_artifact": False, "timeout_sec": 10}
        if case == "stale_snapshot":
            service.test_list = AsyncMock(return_value=ok("list", {
                "tests": ["Demo.Fixture.Test"], "selectionValid": True,
                "selectionDomain": "current-domain", "selectionSnapshotId": "current-snapshot"}))
            arguments.update(fixtures=["Demo.Fixture"], expected_selection_domain="old-domain",
                             expected_selection_snapshot_id="old-snapshot")
        elif case == "no_tests":
            service.test_list = AsyncMock(return_value=ok("list", {"tests": []}))
        elif case == "not_ready":
            service.ensure_ready = AsyncMock(return_value=ok("ready", {"ready": False}))
        elif case == "compile_budget":
            clock = {"now": 1000}
            monkeypatch.setattr("upilot_mcp.domain.task_service.now_ms", lambda: clock["now"])
            monkeypatch.setattr("upilot_mcp.domain.test_service.now_ms", lambda: clock["now"])
            async def compile(**_kwargs):
                clock["now"] += 11000
                return ok("compile", {"status": "success", "errorsVerified": True, "errorTotal": 0})
            service.safe_compile_and_wait = compile
        elif case == "active_capture":
            service.active_capture = True
        elif case == "unknown_capture":
            service.console_capture_list = AsyncMock(return_value=fail("capture", "CAPTURE_LIST_FAILED", "fixture"))
        else:
            # The acceptance call was attempted, but test_run's own preflight
            # rejected it before the test.run send boundary.
            service.test_run = AsyncMock(return_value=fail("run", "TEST_SELECTION_STALE", "fixture"))
        started = await service.task_start("preflight", "unity_upilot_acceptance_run", arguments)
        assert started.ok, started.error
        task_id = started.data["taskId"]
        await service._async_task_handles[task_id]
        await asyncio.sleep(0)
        state = service._async_tasks[task_id]
        assert state["terminal"] and state["status"] == status
        assert state["error"]["code"] == code
        assert state["acceptanceReport"]["acceptancePassed"] is False
        assert state["acceptanceReport"]["runnerStartAttempted"] is (case == "runner_preflight")
        assert state["startIntentSent"] is False and state["startSendState"] == "not_sent"
        assert not state["runGuid"] and service.start_count == 0
        assert state["cleanupSucceeded"] is None and state["cleanupPending"] is False
        assert state["cleanupStatus"] == "not_applicable"
        assert not state.get("_cleanupEscalationObserved")
        service.service_restart.assert_not_awaited()
        history = service.server.state.load_test_jobs()
        await asyncio.gather(*(service.task_status(task_id) for _ in range(3)))
        assert service.server.state.load_test_jobs() == history
        service.service_restart.assert_not_awaited()
    asyncio.run(scenario())


@pytest.mark.parametrize("after_report", [False, True])
def test_cancel_before_dispatch_needs_no_runner_cleanup_with_or_without_report(
        tmp_path, acceptance_gate, escalation_gate, after_report):
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        entered, release = asyncio.Event(), asyncio.Event()
        if after_report:
            async def ready(**_kwargs):
                entered.set()
                await release.wait()
                checkpoint("preflight")
                return ok("ready", {"ready": True})
            service.ensure_ready = ready
        started = await service.task_start("cancel-preflight", "unity_upilot_acceptance_run", {"write_artifact": False})
        task_id = started.data["taskId"]
        if after_report:
            await asyncio.wait_for(entered.wait(), timeout=2)
        cancelled = await service.task_cancel(task_id)
        assert cancelled.ok
        release.set()
        await service._async_task_handles[task_id]
        await asyncio.sleep(0)
        state = service._async_tasks[task_id]
        assert state["terminal"] and state["status"] == "cancelled"
        assert bool(state.get("acceptanceReport")) is after_report
        assert state["cleanupStatus"] == "not_applicable" and state["cleanupSucceeded"] is None
        assert service.start_count == 0 and not state["runGuid"]
        service.service_restart.assert_not_awaited()
    asyncio.run(scenario())


@pytest.mark.parametrize("failure", ["timeout", "exception", "cancelled"])
@pytest.mark.parametrize("dispatched", [False, True])
def test_exception_finalization_distinguishes_no_dispatch_from_unknown_dispatch(
        tmp_path, acceptance_gate, escalation_gate, failure, dispatched):
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        async def interrupted(**_kwargs):
            checkpoint("preflight", acceptanceReport={"runnerStartAttempted": False, "steps": {}})
            if dispatched:
                checkpoint("starting", startIntentSent=True, startSendState="sent_unknown")
            if failure == "timeout":
                raise TimeoutError("fixture")
            if failure == "cancelled":
                raise asyncio.CancelledError()
            raise RuntimeError("fixture")
        service._execute_upilot_acceptance_run = interrupted
        started = await service.task_start("interrupted", "unity_upilot_acceptance_run", {"write_artifact": False})
        task_id = started.data["taskId"]
        await asyncio.gather(service._async_task_handles[task_id], return_exceptions=True)
        await asyncio.sleep(0)
        state = service._async_tasks[task_id]
        assert state["terminal"] and state["status"] == ("timed_out" if failure == "timeout" else "aborted")
        assert not state["runGuid"]
        if not dispatched or failure == "timeout":
            assert state["cleanupSucceeded"] is (False if dispatched else None)
            assert state["cleanupStatus"] == ("failed" if dispatched else "not_applicable")
        else:
            # Server shutdown can cancel its observer after dispatch. Preserve
            # the existing unknown cleanup instead of scheduling a new restart.
            assert "cleanupSucceeded" not in state and "cleanupStatus" not in state
        expected_restart = int(dispatched and failure == "timeout")
        assert service.service_restart.await_count == expected_restart
        await asyncio.gather(*(service.task_status(task_id) for _ in range(3)))
        assert service.service_restart.await_count == expected_restart
    asyncio.run(scenario())


@pytest.mark.parametrize("evidence", [
    {"runGuid": "run-established"}, {"cleanupSucceeded": False}, {"cleanupPending": True},
    {"cleanupErrors": ["capture-stop"]}, {"unresolvedResources": ["capture"]},
    {"stoppedConsoleCaptures": [{"sessionId": "capture-uncertain"}]},
])
def test_no_dispatch_does_not_mask_independent_run_or_cleanup_evidence(
        tmp_path, acceptance_gate, escalation_gate, evidence):
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        async def failure(**_kwargs):
            checkpoint("preflight", acceptanceReport={
                "runnerStartAttempted": False, "steps": {}, **evidence})
            return fail("preflight", "PREFLIGHT_FAILED", "fixture")
        service._execute_upilot_acceptance_run = failure
        started = await service.task_start("independent-cleanup", "unity_upilot_acceptance_run", {"write_artifact": False})
        await service._async_task_handles[started.data["taskId"]]
        await asyncio.sleep(0)
        state = service._async_tasks[started.data["taskId"]]
        assert state["cleanupSucceeded"] is False and state["cleanupStatus"] == "failed"
        assert state["error"]["code"] == "PREFLIGHT_FAILED"
        service.service_restart.assert_awaited_once()
    asyncio.run(scenario())


@pytest.mark.parametrize("intent,send_state", [(None, "not_sent"), ("false", "not_sent"), (False, None), (True, "not_sent")])
def test_preflight_report_alone_never_proves_an_unknown_send_boundary(
        tmp_path, acceptance_gate, escalation_gate, intent, send_state):
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        async def failure(**_kwargs):
            checkpoint("preflight", startIntentSent=intent, startSendState=send_state,
                       acceptanceReport={"runnerStartAttempted": False, "steps": {}})
            return fail("preflight", "PREFLIGHT_FAILED", "fixture")
        service._execute_upilot_acceptance_run = failure
        started = await service.task_start("unknown-boundary", "unity_upilot_acceptance_run", {"write_artifact": False})
        await service._async_task_handles[started.data["taskId"]]
        await asyncio.sleep(0)
        state = service._async_tasks[started.data["taskId"]]
        assert state["cleanupSucceeded"] is False and state["cleanupStatus"] == "failed"
        service.service_restart.assert_awaited_once()
    asyncio.run(scenario())


def test_late_preflight_finally_preserves_a_real_soft_stop_timeout(
        tmp_path, acceptance_gate, escalation_gate):
    from upilot_mcp.test_job_context import TEST_JOB_CONTEXT
    async def scenario():
        service = EscalatingAcceptanceService(tmp_path)
        async def timed_out(**_kwargs):
            checkpoint("preflight", acceptanceReport={"runnerStartAttempted": False, "steps": {}})
            # Model the existing watchdog's terminal record, which a late return
            # must not reinterpret as a successful or unnecessary cleanup.
            state = TEST_JOB_CONTEXT.get()[0]
            state.update(status="aborted", terminal=True, cleanupSucceeded=False, cleanupPending=False,
                         cleanupStatus="timed_out", error={"code": "SOFT_STOP_TIMEOUT", "message": "fixture"})
            return fail("late", "PREFLIGHT_FAILED", "late result")
        service._execute_upilot_acceptance_run = timed_out
        started = await service.task_start("soft-stop-timeout", "unity_upilot_acceptance_run", {"write_artifact": False})
        await service._async_task_handles[started.data["taskId"]]
        await asyncio.sleep(0)
        state = service._async_tasks[started.data["taskId"]]
        assert state["cleanupStatus"] == "timed_out" and state["cleanupSucceeded"] is False
        assert state["error"]["code"] == "SOFT_STOP_TIMEOUT"
        service.service_restart.assert_awaited_once()
    asyncio.run(scenario())


def test_acceptance_summary_finishes_without_polling_and_has_verified_hash(tmp_path, acceptance_gate):
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


def test_public_acceptance_returns_queued_task_before_runner_and_full_remains_available(tmp_path, acceptance_gate):
    async def scenario():
        service = AcceptanceService(tmp_path)
        service.hold_result.clear()
        start = await service.upilot_acceptance_run(timeout_sec=30, write_artifact=False)
        assert start.ok and start.data["durable"] is True
        assert start.data["status"] == "queued" and start.data["terminal"] is False
        assert start.data["projectPath"] == service.server.state.project_path
        assert start.data["deadlineAt"] and not start.data.get("runGuid")
        task_id = start.data["taskId"]
        assert service.server.state.load_test_jobs()[0]["taskId"] == task_id
        service.hold_result.set()
        await service._async_task_handles[task_id]
        summary = await service.task_status(task_id)
        full = await service.task_status(task_id, detail_level="full")
        assert summary.data["status"] == "completed" and service.start_count == 1
        assert "acceptanceReport" not in summary.data
        assert full.data["acceptanceReport"]["runGuid"] == "run-1"

    asyncio.run(scenario())


def test_interrupted_acceptance_is_archived_without_restarting_or_reattaching(tmp_path, acceptance_gate):
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
        state = first._async_tasks[task_id]
        assert state["status"] == "aborted" and state["terminal"]
        assert state["runGuid"] == "run-1"
        history = first.server.state.load_test_jobs()
        second = AcceptanceService(tmp_path)
        second._recover_test_jobs()
        status = await second.task_status(task_id)
        assert not status.ok and status.error.code == "TASK_NOT_FOUND"
        assert not second._async_task_handles and not second._async_tasks
        assert first.start_count == 1 and second.start_count == 0
        assert first.server.state.load_test_jobs() == history
    asyncio.run(scenario())


def test_generic_task_execute_refuses_retries_for_non_idempotent_tool(tmp_path):
    service = Service(tmp_path)
    result = asyncio.run(service.task_execute("unsafe", "unity_prefab_patch", retry_count=1))
    assert not result.ok and result.error.code == "TASK_RETRY_UNSAFE"
    assert service.start_count == 0


def test_acceptance_budget_exhausted_by_compile_never_starts_runner(tmp_path, monkeypatch):
    from upilot_mcp.domain import test_service
    from upilot_mcp import config
    monkeypatch.setattr(config, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(config.CONFIG, "write_access_approved", True)
    clock = {"now": 1000}
    monkeypatch.setattr(test_service, "now_ms", lambda: clock["now"])
    service = AcceptanceService(tmp_path)

    async def compile(**kwargs):
        clock["now"] += 11000
        return ok("compile", {"status": "success", "errorsVerified": True, "errorTotal": 0})

    service.safe_compile_and_wait = compile
    result = asyncio.run(service._execute_upilot_acceptance_run(timeout_sec=10, write_artifact=False))
    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_DEADLINE_EXCEEDED"
    assert service.start_count == 0
    assert "testRun" not in result.error.detail["steps"]


def test_acceptance_preflight_only_does_not_enter_ready_compile_capture_or_runner(tmp_path):
    service = AcceptanceService(tmp_path)

    async def forbidden(*_args, **_kwargs):
        raise AssertionError("preflightOnly must not invoke this workflow step")

    service.ensure_ready = forbidden
    service.test_run = forbidden
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["preflightOnly"] is True
    assert result.error.detail["runnerStartAttempted"] is False
    assert result.error.detail["artifactWritten"] is False
    assert service.dispatch_calls == [("scene.list", {})]


def test_acceptance_preflight_reports_import_input_changes_without_starting_runner(tmp_path, monkeypatch):
    from upilot_mcp.domain import test_service

    snapshots = iter((
        {"schemaVersion": 1, "projectPath": "project", "inputCount": 1, "inputs": {"project:Assets/A.cs.meta": "before"}},
        {"schemaVersion": 1, "projectPath": "project", "inputCount": 1, "inputs": {"project:Assets/A.cs.meta": "after"}},
    ))
    monkeypatch.setattr(test_service, "acceptance_import_inputs", lambda *_args: next(snapshots))
    service = AcceptanceService(tmp_path)
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    detail = result.error.detail
    assert detail["importState"] == "unknown"
    assert detail["blockingReasons"] == ["ImportInputsChangedDuringPreflight"]
    assert detail["importInputChanges"]["changed"] == [{
        "path": "project:Assets/A.cs.meta", "beforeSha256": "before", "afterSha256": "after",
    }]
    assert service.start_count == 0


def test_acceptance_rejects_source_unchanged_when_import_inputs_change(tmp_path, monkeypatch):
    from upilot_mcp.domain import test_service
    from upilot_mcp import config
    monkeypatch.setattr(config, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(config.CONFIG, "write_access_approved", True)

    snapshots = iter((
        {"schemaVersion": 1, "projectPath": "project", "inputCount": 1, "inputs": {"project:Assets/A.cs.meta": "before"}},
        {"schemaVersion": 1, "projectPath": "project", "inputCount": 1, "inputs": {"project:Assets/A.cs.meta": "after"}},
    ))
    monkeypatch.setattr(test_service, "acceptance_import_inputs", lambda *_args: next(snapshots))
    service = AcceptanceService(tmp_path)
    result = asyncio.run(service._execute_upilot_acceptance_run(write_artifact=False))

    assert not result.ok
    assert result.error.code == "UPILOT_ACCEPTANCE_FAILED"
    assert result.error.detail["sourceUnchanged"] is False
    assert result.error.detail["importInputsUnchanged"] is False


def test_preflight_does_not_promote_stable_hashes_to_import_ready_without_verified_compile(tmp_path):
    service = AcceptanceService(tmp_path)

    async def status(**_kwargs):
        return ok("status", {
            "connected": True, "serverReady": True,
            "paths": {"unityProjectAbsolute": str(service.expected_project)},
            "executionState": {"ready": True, "authoritative": True, "isStale": False,
                               "terminal": True, "errorsVerified": True, "compilePhase": "idle",
                               "lastCompileVerifiedAt": 0},
        })

    service.mcp_status = status
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["importState"] == "unknown"
    assert result.error.detail["preflightPassed"] is False
    assert result.error.detail["blockingReasons"] == ["ImportNotVerified"]
    assert service.start_count == 0


def test_preflight_reports_ready_only_from_existing_verified_compile_without_runner(tmp_path):
    service = AcceptanceService(tmp_path)

    async def status(**_kwargs):
        return ok("status", {
            "connected": True, "serverReady": True,
            "paths": {"unityProjectAbsolute": str(service.expected_project)},
            "executionState": {"ready": True, "authoritative": True, "isStale": False,
                               "terminal": True, "errorsVerified": True, "compilePhase": "completed",
                               "lastCompileVerifiedAt": 10**15},
        })

    service.mcp_status = status
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["importState"] == "ready"
    assert result.error.detail["preflightPassed"] is True
    assert result.error.detail["blockingReasons"] == []
    assert result.error.detail["runnerStartAttempted"] is False
    assert service.start_count == 0


@pytest.mark.parametrize("damage", ["unknown-run", "wrong-run", "non-authoritative", "cleanup-unverified"])
def test_active_task_keeps_original_identity_and_deadline_when_result_cannot_be_verified(tmp_path, monkeypatch, damage):
    async def scenario():
        clock = {"now": 1000}
        monkeypatch.setattr("upilot_mcp.domain.task_service.now_ms", lambda: clock["now"])
        monkeypatch.setattr("upilot_mcp.domain.test_service.now_ms", lambda: clock["now"])
        service = Service(tmp_path)
        entered = asyncio.Event()
        release = asyncio.Event()
        observed_guids = []
        original_results = service.test_results

        async def unavailable_result(run_guid=""):
            observed_guids.append(run_guid)
            entered.set()
            await release.wait()
            if damage == "unknown-run":
                return fail("original-query", "TEST_RUN_NOT_FOUND", "Original run is unavailable.")
            result = await original_results(run_guid)
            if damage == "wrong-run":
                result.data["runGuid"] = "unrelated-run"
            elif damage == "non-authoritative":
                result.data["resultAuthoritative"] = False
            else:
                result.data.update(cleanupSucceeded=False, cleanupPending=True,
                                   cleanupStatus="failed", unresolvedResources=["original-callback"])
            return result

        service.test_results = unavailable_result
        started = await service.task_start("observe-original", "unity_test_run", timeout_s=10)
        assert started.ok
        task_id = started.data["taskId"]
        await asyncio.wait_for(entered.wait(), timeout=2)
        state = service._async_tasks[task_id]
        deadline = state["deadlineAt"]
        assert deadline == 11000 and state["runGuid"] == "run-persisted"
        handle = service._async_task_handles[task_id]
        responses = await asyncio.gather(*(service.task_status(task_id) for _ in range(3)))
        assert all(response.ok and response.data["runGuid"] == "run-persisted" for response in responses)
        assert service._async_task_handles[task_id] is handle
        assert service.start_count == 1 and service.cancels == []
        assert state["deadlineAt"] == deadline
        # Unknown identity cannot trigger a replacement start/cancel after expiry.
        clock["now"] = deadline
        release.set()
        await asyncio.wait_for(handle, timeout=2)
        assert state["terminal"] and state["status"] == "aborted"
        assert state["error"]["code"] == "TEST_RECOVERY_REQUIRED"
        assert not state["cleanupSucceeded"]
        assert state["deadlineAt"] == deadline and state["runGuid"] == "run-persisted"
        assert observed_guids == ["run-persisted"] and service.start_count == 1 and service.cancels == []
        history = service.server.state.load_test_jobs()
        await asyncio.gather(*(service.task_status(task_id) for _ in range(3)))
        assert service.server.state.load_test_jobs() == history
        assert observed_guids == ["run-persisted"]
        # Current-lifetime discovery is not the durable history API. Compare the
        # exact persisted row through an independent read-only connection.
        history_uri = service.server.state._db_path.as_uri() + "?mode=ro"
        query = "SELECT state_json FROM test_jobs WHERE project_path=? AND task_id=?"
        identity = (str(tmp_path.resolve()), task_id)
        with sqlite3.connect(history_uri, uri=True) as db:
            persisted = db.execute(query, identity).fetchone()
        assert persisted is not None and json.loads(persisted[0]) == history[0]
        restarted = Service(tmp_path)
        restarted._recover_test_jobs()
        result = await restarted.task_status(task_id)
        assert not result.ok and result.error.code == "TASK_NOT_FOUND"
        assert not restarted._async_tasks and not restarted._async_task_handles
        assert restarted.start_count == 0 and restarted.cancels == []
        assert restarted.server.state.load_test_jobs() == []
        assert service.server.state.load_test_jobs() == history
        with sqlite3.connect(history_uri, uri=True) as db:
            assert db.execute(query, identity).fetchone() == persisted

    asyncio.run(scenario())


def test_unknown_start_response_without_run_guid_is_terminal_and_never_replayed(tmp_path):
    async def scenario():
        service = Service(tmp_path)

        async def uncertain_start(name, args):
            checkpoint("starting", startIntentSent=True, startSendState="sent_unknown")
            service.start_count += 1
            return fail("original-start", "COMMAND_RECOVERY_REQUIRED", "Start outcome is unknown.")

        service._dispatch_tool = uncertain_start
        started = await service.task_start("uncertain-start", "unity_test_run", timeout_s=10)
        task_id = started.data["taskId"]
        await service._async_task_handles[task_id]
        state = service._async_tasks[task_id]
        assert state["terminal"] and state["status"] == "aborted"
        assert state["runGuid"] == "" and state["startIntentSent"]
        assert state["error"]["code"] == "COMMAND_RECOVERY_REQUIRED"
        assert not state["cleanupSucceeded"]
        history = service.server.state.load_test_jobs()
        responses = await asyncio.gather(*(service.task_status(task_id) for _ in range(3)))
        assert all(response.ok and response.data["status"] == "aborted" for response in responses)
        assert service.start_count == 1 and service.cancels == []
        assert service.server.state.load_test_jobs() == history

    asyncio.run(scenario())


@pytest.mark.parametrize("detail_level", ["summary", "standard", "full"])
@pytest.mark.parametrize("status", ["completed", "failed"])
def test_task_status_summary_counts_elapsed_before_budget(tmp_path, detail_level, status):
    from copy import deepcopy

    async def scenario():
        service = Service(tmp_path)
        passed = status == "completed"
        state = {
            "taskId": "task-summary", "projectPath": service.server.state.project_path,
            "durable": True, "status": status, "phase": status, "terminal": True,
            "runGuid": "run-original", "startedAt": 100, "updatedAt": 1234567,
            "endedAt": 1234567, "deadlineAt": 1234600,
            "cleanupSucceeded": True, "cleanupPending": False,
            "acceptanceReport": {
                "acceptancePassed": passed, "runGuid": "run-original",
                "cleanupVerified": True, "testIdentityVerified": True,
                "sourceUnchanged": True, "failureCode": None if passed else "FIRST",
                "testNames": ["Fixture.Test"],
                "artifact": {"path": "summary.json", "bytes": 42, "sha256": "a" * 64},
                "steps": {
                    "compile": {"data": {"compileOperationId": "compile-original",
                        "compileRequestId": "request-original", "writeBatchId": "batch-original",
                        "errorsVerified": True}},
                    "testStatus": {"data": {"status": "completed", "total": 1,
                        "passed": int(passed), "failed": int(not passed), "skipped": 0,
                        "cleanupSucceeded": True, "resultAuthoritative": True,
                        "results": [{"output": "不应内联" * 10000}]}},
                },
            },
        }
        if not passed:
            state["error"] = {"code": "FIRST", "message": "原始业务失败"}
        service._async_tasks[state["taskId"]] = state
        before = deepcopy(state)
        history_before = service.server.state.load_test_jobs()
        response = await service.task_status(state["taskId"], detail_level)
        result = response.data
        assert response.ok and result["terminal"] is True
        assert result["elapsedMs"] == 1234467
        assert result["taskId"] == state["taskId"] and result["runGuid"] == state["runGuid"]
        if detail_level == "full":
            assert result["acceptanceReport"] == state["acceptanceReport"]
            assert "summaryVersion" not in result
        else:
            assert "acceptanceReport" not in result
            assert result["responseBytes"] == TaskDomainService._summary_size(result) <= 16384
            report = result["result"]["result"]
            assert report["acceptancePassed"] is passed
            assert report["artifact"] == state["acceptanceReport"]["artifact"]
            assert report["compile"]["compileOperationId"] == "compile-original"
            assert report["compile"]["errorsVerified"] is True
            assert result["tests"]["failed"] == int(not passed)
            assert result["recoveryDiagnosticsDroppedCount"] is None
            assert result["recoveryDiagnosticsHistoryComplete"] is False
        if not passed:
            assert result["error"]["code"] == "FIRST"
        assert state == before
        assert service.server.state.load_test_jobs() == history_before
        assert service.start_count == 0 and not service._async_task_handles
    asyncio.run(scenario())


def test_task_status_elapsed_cannot_overflow_exact_summary_budget(tmp_path):
    from copy import deepcopy

    async def scenario():
        service = Service(tmp_path)
        state = {
            "taskId": "task-budget", "projectPath": service.server.state.project_path,
            "durable": True, "status": "failed", "terminal": True,
            "runGuid": "run-budget", "startedAt": 100, "updatedAt": 1234567,
            "endedAt": 1234567, "deadlineAt": 1234600,
            "cleanupError": "清" * 600, "lastObservationError": "错" * 600,
            "error": {"code": "FIRST", "message": "因" * 600},
            "nextAction": "a" * 500, "cleanupFailureSignature": "",
            "originalTestResult": {str(i): "b" * 500 for i in range(8)},
            "businessTerminal": {str(i): "c" * 500 for i in range(8)},
        }
        base = TaskDomainService._public_task_state(state)
        padding = 16384 - TaskDomainService._summary_size(base)
        assert 0 < padding <= 2000
        state["cleanupFailureSignature"] = "d" * padding
        projection = TaskDomainService._public_task_state(state)
        assert projection["responseBytes"] == TaskDomainService._summary_size(projection) == 16384
        service._async_tasks[state["taskId"]] = state
        before = deepcopy(state)
        result = (await service.task_status(state["taskId"])).data
        assert result["responseBytes"] == TaskDomainService._summary_size(result) <= 16384
        assert result["elapsedMs"] == 1234467
        assert result["error"]["code"] == "FIRST"
        assert result["taskId"] == "task-budget" and result["runGuid"] == "run-budget"
        assert result["truncatedFields"]
        assert state == before and service.start_count == 0
    asyncio.run(scenario())
