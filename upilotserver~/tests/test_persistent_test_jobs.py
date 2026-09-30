import asyncio
import hashlib
import json
import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
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
