import asyncio
import hashlib
import json
import logging
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.queue_service import QueueDomainService, authorization
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.protocol import now_ms
from upilot_mcp.queue_audit import outcome
from upilot_mcp.queue_audit import notice, safe_text
from upilot_mcp.responses import ok, fail
from upilot_mcp.state_store import StateStore
from test_editor_execution_state_v2 import _snapshot


class Service(QueueDomainService):
    def __init__(self, root):
        store = StateStore()
        store.configure_project(str(root))
        snapshot = _snapshot(1, observed_at=now_ms())
        snapshot["lastMainThreadPumpAt"] = now_ms() + 100000
        store.update_editor_execution_state(snapshot)
        self.server = SimpleNamespace(
            state=store, session_manager=SimpleNamespace(active=SimpleNamespace(
                project_path=str(root), identity_verified=True, process_id=123,
                process_created_at=1, process_role="main", session_id="fixture-session")),
            is_ready=lambda: True, _pending={}, _suspended={})
        self._async_tasks, self._operations = {}, {}
        self.calls = []
        self.dispatcher = SimpleNamespace(call=self.dispatch)

    async def dispatch(self, *args, **kwargs):
        if len(args) > 1 and args[1] == "test.status":
            return ok("test", {"status": "idle"})
        return ok("log", {})

    async def console_capture_list(self, **kwargs):
        return ok("captures", {"sessions": [], "activeCount": 0})

    async def task_cancel(self, target):
        self.calls.append(target)
        return ok("cancel", {"cancelAccepted": True, "terminal": False, "cleanupPending": True})

    def task(self, identity="t"):
        value = dict(taskId=identity, projectPath=self.server.state.project_path, durable=True,
                     status="running", terminal=False, runGuid="run-" + identity)
        self.server.state.save_test_job(value)
        return value

    def batch(self):
        store = self.server.state
        batch = store.register_write_batch(["missing.cs"], created_at=now_ms() - 5000,
                                          files_sha256="hash", compile_when_edit_mode=True)
        store.mark_write_batch(batch["writeBatchId"], "recovery_required", error="Original unknown")
        return batch["writeBatchId"]


async def apply(service, kind, target, action="cancel", **overrides):
    args = dict(target_type=kind, target_id=target, action=action, reason="fixture only")
    preview = await service.queue_cleanup(**args)
    assert preview.ok, preview
    args.update(dry_run=False, confirm_token=preview.data["confirmToken"],
                expected_project_path=service.server.state.project_path)
    args.update(overrides)
    return await service.queue_cleanup(**args)


@pytest.mark.parametrize("value,expected", [(None, True), (True, True), (False, False), ("true", False)])
def test_default_and_explicit_authorization(tmp_path, value, expected):
    if value is not None:
        (tmp_path / ".upilot").mkdir()
        (tmp_path / ".upilot/config.json").write_text(json.dumps({"aiQueueCleanupAllowed": value}))
    assert authorization(tmp_path)[0] is expected


def test_inventory_read_only_and_disabled_still_visible(tmp_path):
    async def run():
        service = Service(tmp_path)
        service.task()
        (tmp_path / ".upilot").mkdir()
        (tmp_path / ".upilot/config.json").write_text('{"aiQueueCleanupAllowed":false}')
        result = await service.queue_cleanup()
        assert result.ok and not result.data["complete"]
        assert "BRIDGE_QUEUE_NOT_FULLY_ENUMERATED" in result.data["issues"]
        assert result.data["items"][0]["id"] == "t"
        assert service.calls == []
        denied = await apply(service, "Task", "t")
        assert denied.error.code == "QUEUE_CLEANUP_NOT_APPROVED"
        assert service.calls == []
    asyncio.run(run())


def test_exact_task_cleanup_is_not_terminal_and_token_is_one_shot(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        service.task("t")
        service.task("other")
        args = dict(target_type="Task", target_id="t", action="cancel", reason="test")
        preview = await service.queue_cleanup(**args)
        token = preview.data["confirmToken"]
        args.update(dry_run=False, confirm_token=token, expected_project_path=str(tmp_path))
        response = await service.queue_cleanup(**args)
        assert response.ok and not response.data["terminal"]
        assert service.calls == ["t"]
        assert (await service.queue_cleanup(**args)).error.code == "QUEUE_PREVIEW_CHANGED"
        assert token not in caplog.text
        assert "尚未停止" in caplog.text
        assert "已确认停止" not in caplog.text
    caplog.set_level(logging.INFO, logger="upilot.mcp")
    asyncio.run(run())


def test_changed_target_or_wrong_project_cannot_apply(tmp_path):
    async def run():
        service = Service(tmp_path)
        value = service.task()
        args = dict(target_type="Task", target_id="t", action="cancel", reason="test")
        preview = await service.queue_cleanup(**args)
        value["runGuid"] = "different"
        service.server.state.save_test_job(value)
        response = await service.queue_cleanup(**args, dry_run=False, confirm_token=preview.data["confirmToken"],
                                               expected_project_path=str(tmp_path))
        assert response.error.code == "QUEUE_PREVIEW_CHANGED"
        assert not service.calls
        assert (await apply(service, "Task", "t", expected_project_path=str(tmp_path / "other"))).error.code == "QUEUE_PROJECT_MISMATCH"
    asyncio.run(run())


def test_release_retains_unknown_backup_and_survives_restart(tmp_path):
    async def run():
        service = Service(tmp_path)
        first = service.batch()
        second = service.batch()
        original = service.server.state.get_write_batch(first)
        response = await apply(service, "WriteBatch", first, "release")
        assert response.ok, response
        assert response.data["outcome"] == "unknown"
        disposition = response.data["disposition"]
        content = Path(disposition["backupPath"]).read_bytes()
        assert hashlib.sha256(content).hexdigest() == disposition["backupSha256"]
        assert json.loads(content)["write_batch_id"] == first
        assert json.loads(content)["error"] == "Original unknown"
        restored = StateStore()
        restored.configure_project(str(tmp_path))
        actual = restored.get_write_batch(first)
        assert actual["status"] == original["status"] == "recovery_required"
        assert actual["outcome"] == "unknown" and not actual["terminal"]
        assert [v["writeBatchId"] for v in restored.pending_write_batches()] == [second]
        assert restored.pending_write_batch_id == second
        before = restored.get_write_batch(first)
        late = _snapshot(2, observed_at=now_ms(), write_batch_id=first,
                         write_batch_created_at=before["writeBatchCreatedAt"])
        late["pendingWriteBatchId"] = first
        restored.update_editor_execution_state(late)
        assert restored.get_write_batch(first) == before
        assert restored.pending_write_batch_id != first
    asyncio.run(run())


def test_backup_failure_and_possible_execution_leave_blocker(tmp_path, monkeypatch):
    async def run():
        service = Service(tmp_path)
        identity = service.batch()
        service.server._suspended["old"] = object()
        blocked = await service.queue_cleanup(target_type="WriteBatch", target_id=identity,
                                              action="release", reason="fixture only")
        assert blocked.error.code == "QUEUE_EXECUTION_NOT_EXCLUDED"
        service.server._suspended.clear()
        original_open = Path.open
        def broken_open(path, *args, **kwargs):
            if path.parent.name == "queue-backups":
                raise OSError("fixture backup failure")
            return original_open(path, *args, **kwargs)
        monkeypatch.setattr(Path, "open", broken_open)
        response = await apply(service, "WriteBatch", identity, "release")
        assert not response.ok
        assert not service.server.state.get_write_batch(identity)["disposition"]
        assert service.server.state.pending_write_batches()[0]["writeBatchId"] == identity
    asyncio.run(run())


def test_disconnected_and_incomplete_inventory_never_empty_success(tmp_path):
    async def run():
        service = Service(tmp_path)
        service.server.is_ready = lambda: False
        result = await service.queue_cleanup()
        assert not result.data["complete"] and "UNITY_NOT_CONNECTED" in result.data["issues"]
        service.task()
        assert not (await apply(service, "Task", "t")).ok
        assert not service.calls
    asyncio.run(run())


def test_direct_cancel_errors_are_audited_without_tokens(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        async def timeout(*args, **kwargs):
            return fail("request", "COMMAND_TIMEOUT", "fixture")
        service.dispatcher.call = timeout
        response = await TestDomainService.test_cancel(service, "run-exact")
        assert response.error.code == "COMMAND_TIMEOUT"
        assert any(r.levelno == logging.ERROR and "结果未确认" in r.message for r in caplog.records)
        assert "run-exact" in caplog.text
    caplog.set_level(logging.INFO)
    asyncio.run(run())


@pytest.mark.parametrize("response,phase", [
    (ok("r", {"cancelAccepted": True}), "accepted"),
    (ok("r", {"terminal": True, "cleanupPending": True}), "accepted"),
    (ok("r", {"terminal": True, "cleanupPending": False}), "completed"),
    (fail("r", "CANCEL_UNSUPPORTED", "no"), "failed"),
    (fail("r", "COMMAND_TIMEOUT", "timeout"), "unconfirmed"),
])
def test_log_result_classification(response, phase):
    assert outcome(response)[0] == phase


def test_notice_deduplicates_and_forwarding_failure_preserves_response(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        service.task()
        async def broken(*args, **kwargs):
            raise OSError("logging transport failure")
        service.dispatcher.call = broken
        response = await apply(service, "Task", "t")
        assert response.ok and service.calls == ["t"]
        args = (service, "fixed", "Task", "t", "cancel", "failed", "fixture", "test", "EXPECTED")
        await notice(*args)
        await notice(*args)
        assert sum("requestId=fixed" in r.message for r in caplog.records) == 1
    caplog.set_level(logging.INFO)
    asyncio.run(run())


@pytest.mark.parametrize("reason", [
    "ownerToken=do-not-log-this", '{"ownerToken":"do-not-log-this"}',
    "confirmToken: do-not-log-this", "password=do-not-log-this",
])
def test_log_redaction(reason):
    assert "do-not-log-this" not in safe_text(reason)


def test_generic_task_and_step_refuse_without_removing_records(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        service._async_tasks["generic"] = {"taskId": "generic", "status": "running"}
        response = await apply(service, "Task", "generic")
        assert response.error.code == "QUEUE_CLEANUP_UNSUPPORTED"
        assert "generic" in service._async_tasks
        response = await service.queue_cleanup(target_type="Step", target_id="step",
                                               action="cancel", reason="test")
        assert response.error.code == "QUEUE_CLEANUP_UNSUPPORTED"
        assert any(r.levelno == logging.ERROR for r in caplog.records)
    asyncio.run(run())


def test_operation_uses_existing_cancel_not_new_start_and_grant_is_independent(tmp_path, monkeypatch):
    from test_operation_runner_and_agent_rules import _OperationService
    from test_operation_cleanup_barriers import spec
    from test_persistent_operations import stop_observers
    from upilot_mcp.config import CONFIG
    class OperationService(QueueDomainService, _OperationService):
        pass
    async def run():
        service = OperationService(tmp_path, [])
        service.server.is_ready = lambda: True
        started = await service.operation_start(spec(cancelCall={"kind": "tool", "toolName": "cancel"}))
        identity = started.data["operationId"]
        # Authorization for cleanup does not depend on general write access.
        monkeypatch.setattr(CONFIG, "write_access_approved", False)
        response = await apply(service, "Operation", identity)
        assert response.ok, response
        assert [name for name, _ in service.calls].count("start") == 1
        assert [name for name, _ in service.calls].count("cancel") == 1
        assert response.data["cleanupPending"]
        await stop_observers(service)
    asyncio.run(run())


def test_capture_adapter_stops_only_exact_session_without_logging_owner(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        directory = tmp_path / "Log/UPilotConsole/fixture"
        directory.mkdir(parents=True)
        (directory / "session.json").write_text(json.dumps({
            "sessionId": "capture-exact", "active": True, "ownerTokenSha256": "PRIVATE_HASH",
        }))
        async def stop(**kwargs):
            assert kwargs == {"session_id": "capture-exact", "force_stop": True}
            return ok("stop", {"terminal": True, "active": False, "summaryPath": "fixture/summary.json"})
        service.console_capture_stop = stop
        response = await apply(service, "Capture", "capture-exact", "stop")
        assert response.ok and response.data["terminal"]
        assert "PRIVATE_HASH" not in caplog.text
        assert (directory / "session.json").exists()
    caplog.set_level(logging.INFO)
    asyncio.run(run())


def test_missing_store_data_does_not_become_empty(tmp_path, monkeypatch):
    async def run():
        service = Service(tmp_path)
        def broken():
            raise ValueError("corrupt fixture")
        monkeypatch.setattr(service.server.state, "load_operations", broken)
        result = await service.queue_cleanup()
        assert not result.data["complete"]
        assert "PERSISTED_QUEUE_DATA_INCOMPLETE" in result.data["issues"]
    asyncio.run(run())


def test_disposition_compare_and_set_rejects_changed_record(tmp_path):
    service = Service(tmp_path)
    identity = service.batch()
    original = service.server.state.get_write_batch(identity)
    service.server.state.mark_write_batch(identity, "recovery_required", error="changed")
    with pytest.raises(ValueError, match="QUEUE_TARGET_CHANGED"):
        service.server.state.dispose_write_batch(identity, original, reason="fixture", request_id="test")
    assert not service.server.state.get_write_batch(identity)["disposition"]


def test_connection_lost_during_inventory_is_incomplete(tmp_path):
    async def run():
        service = Service(tmp_path)
        async def lost(**kwargs):
            service.server.is_ready = lambda: False
            return ok("list", {"sessions": [], "activeCount": 0})
        service.console_capture_list = lost
        response = await service.queue_inventory()
        assert response.ok and not response.data["connected"] and not response.data["complete"]
    asyncio.run(run())


def test_mutation_during_start_notice_is_rejected(tmp_path):
    async def run():
        service = Service(tmp_path)
        value = service.task()
        async def change(*args, **kwargs):
            if args[1] == "queue.cleanup.log" and args[2]["phase"] == "started":
                value["runGuid"] = "changed-during-await"
                service.server.state.save_test_job(value)
            return ok("log", {})
        service.dispatcher.call = change
        result = await apply(service, "Task", "t")
        assert result.error.code == "QUEUE_TARGET_CHANGED"
        assert not service.calls
    asyncio.run(run())


def test_completed_test_is_noop_not_second_cancel(tmp_path, caplog):
    async def run():
        service = Service(tmp_path)
        async def result(**kwargs):
            return ok("result", dict(runGuid=kwargs["run_guid"], status="aborted",
                resultAuthoritative=True, cleanupSucceeded=True, cleanupPending=False))
        service.test_results = result
        response = await apply(service, "Test", "run-done")
        assert response.ok and response.data["status"] == "noop"
        assert "无需操作" in caplog.text
    caplog.set_level(logging.INFO)
    asyncio.run(run())


def test_registry_proxy_cleanup_does_not_require_general_write_access(tmp_path, monkeypatch):
    from upilot_mcp import tool_registry
    from upilot_mcp.mcp_tools import task_tools  # noqa: F401
    monkeypatch.setattr(tool_registry, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(tool_registry.CONFIG, "write_access_approved", False)
    async def run():
        service = Service(tmp_path)
        service.task()
        args = {"targetType": "Task", "targetId": "t", "action": "cancel", "reason": "fixture"}
        preview = await tool_registry.dispatch_public_tool(service, "unity_queue_cleanup", args)
        assert preview.ok, preview
        args.update(dryRun=False, confirmToken=preview.data["confirmToken"], expectedProjectPath=str(tmp_path))
        result = await tool_registry.dispatch_public_tool(service, "unity_queue_cleanup", args)
        assert result.ok and service.calls == ["t"]
    asyncio.run(run())


def test_queued_test_task_can_cancel_before_start_but_unknown_start_cannot(tmp_path):
    async def run():
        service = Service(tmp_path)
        value = service.task()
        value.update(runGuid="", startIntentSent=False, startSendState="not_sent")
        service.server.state.save_test_job(value)
        assert (await apply(service, "Task", "t")).ok
        value.update(startIntentSent=True, startSendState="sent_unknown")
        service.server.state.save_test_job(value)
        assert (await apply(service, "Task", "t")).error.code == "QUEUE_CLEANUP_UNSUPPORTED"
        assert service.calls == ["t"]
    asyncio.run(run())


def test_step_records_keep_original_instance_and_operation_ids_and_mark_unverified(tmp_path):
    async def run():
        service = Service(tmp_path)
        path = tmp_path / "Library/UPilot/step-run.json"
        path.write_text(json.dumps(dict(runId="run-1", operationId="op-1", status="Running",
            terminal=False, steps=[
                dict(instanceId="original-step-id", typeIdentity="FixtureStep", stage="Polling", startedAtUtcMs=123),
                dict(instanceId="finished", stage="Completed", finishedAtUtcMs=456),
            ])))
        response = await service.queue_inventory()
        assert not response.data["complete"]
        assert "STEP_LIVE_STATE_UNVERIFIED" in response.data["issues"]
        entries = [v for v in response.data["items"] if v["type"] == "Step"]
        assert len(entries) == 1
        assert entries[0]["id"] == "original-step-id"
        assert entries[0]["operationId"] == "op-1" and entries[0]["runId"] == "run-1"
        assert entries[0]["source"] == "persisted"
    asyncio.run(run())
