"""D19: namespaced MCP labels are diagnostic, frozen and never authorization.

These tests exercise the registered unified call entry and existing task/queue
projections. They do not start Unity, services, or generic Operation/Step work.
"""
import asyncio
import copy
from types import SimpleNamespace

import pytest
from mcp.server.fastmcp import Context
from mcp.shared.context import RequestContext
from mcp.types import CallToolRequest, CallToolResult

from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp.domain import queue_service
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.operation_context import (
    ORIGIN_META_KEY, REQUEST_ORIGIN, normalize_request_origin, request_origin_from_context,
)
from upilot_mcp.responses import ok
from test_persistent_test_jobs import Service as PersistentService
from test_queue_cleanup import Service as QueueService


def context_for(origin=None, *, include_meta=True):
    params = {"name": "unity_task_start", "arguments": {
        "taskName": "owned fixture", "toolName": "fixture.work"}}
    if include_meta:
        params["_meta"] = {ORIGIN_META_KEY: origin}
    request = CallToolRequest.model_validate({"method": "tools/call", "params": params})
    ctx = Context(request_context=RequestContext(
        request_id="owned-request", meta=request.params.meta,
        session=None, lifespan_context=None,
    ))
    return request, ctx


@pytest.mark.parametrize("origin,expected", [
    (None, {"status": "unknown", "clientId": "", "chatId": ""}),
    ({"clientId": " client ", "chatId": "聊天-A"},
     {"status": "self_declared", "clientId": "client", "chatId": "聊天-A"}),
    ({"clientId": "x" * 129, "chatId": "valid"},
     {"status": "self_declared", "clientId": "", "chatId": "valid"}),
    ({"clientId": False, "chatId": ["chat"]},
     {"status": "unknown", "clientId": "", "chatId": ""}),
    ({"clientId": "bad\nlabel", "chatId": "bad\x00label"},
     {"status": "unknown", "clientId": "", "chatId": ""}),
    ({"clientId": "bad\u200blabel", "chatId": " "},
     {"status": "unknown", "clientId": "", "chatId": ""}),
    ({"clientId": "x" * 128, "chatId": "y" * 128, "status": "verified", "ownerToken": "secret"},
     {"status": "self_declared", "clientId": "x" * 128, "chatId": "y" * 128}),
])
def test_sdk_metadata_and_projection_are_bounded_self_declarations(origin, expected):
    _, ctx = context_for(origin)
    assert request_origin_from_context(ctx) == expected
    assert normalize_request_origin(origin) == expected


def test_missing_context_never_infers_chat_from_transport_or_sdk_client_id():
    unknown = normalize_request_origin(None)
    assert request_origin_from_context(None) == unknown
    assert request_origin_from_context(Context()) == unknown
    _, ctx = context_for(include_meta=False)
    assert request_origin_from_context(ctx) == unknown
    fake = SimpleNamespace(client_id="http-client", session_id="mcp-session",
                           request_context=SimpleNamespace(meta={"chatId": "unscoped"}))
    assert request_origin_from_context(fake) == unknown


@pytest.mark.parametrize("tool_names", [
    ("unity_task_start", "unity_task_start"), ("unity_task_start", "unity_task_status"),
])
def test_registered_mcp_entry_isolates_concurrent_chat_metadata(monkeypatch, tool_names):
    async def scenario():
        outer = REQUEST_ORIGIN.set({"clientId": "outer", "chatId": "outer"})
        try:
            entered = 0
            both = asyncio.Event()
            async def fake_call(name, arguments, context=None, convert_result=False):
                nonlocal entered
                before = normalize_request_origin(REQUEST_ORIGIN.get())
                entered += 1
                if entered == 2:
                    both.set()
                await asyncio.wait_for(both.wait(), 2)
                assert normalize_request_origin(REQUEST_ORIGIN.get()) == before
                assert "requestOrigin" not in arguments and "_meta" not in arguments
                return CallToolResult(content=[], structuredContent={"requestOrigin": before})
            monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
            monkeypatch.setattr(runtime, "_write_mcp_tool_call_log", lambda *a, **k: None)
            async def call(index):
                request, ctx = context_for({"clientId": "client", "chatId": f"chat-{index}"})
                arguments = request.params.arguments if tool_names[index] == "unity_task_start" else {"taskId": "owned"}
                return await runtime.mcp._tool_manager.call_tool(tool_names[index], arguments, context=ctx)
            results = await asyncio.gather(call(0), call(1))
            assert [r.structuredContent["requestOrigin"]["chatId"] for r in results] == ["chat-0", "chat-1"]
            assert REQUEST_ORIGIN.get() == {"clientId": "outer", "chatId": "outer"}
        finally:
            REQUEST_ORIGIN.reset(outer)
    asyncio.run(scenario())


@pytest.mark.parametrize("failure", ["normalize", "exception", "cancel", "result_log"])
def test_unified_entry_restores_context_on_every_exit(monkeypatch, failure):
    async def scenario():
        outer = REQUEST_ORIGIN.set({"clientId": "parent", "chatId": "parent"})
        try:
            async def fake_call(*args, **kwargs):
                assert REQUEST_ORIGIN.get()["chatId"] == "child"
                if failure == "exception":
                    raise RuntimeError("owned call failure")
                if failure == "cancel":
                    raise asyncio.CancelledError()
                return CallToolResult(content=[])
            def write_log(*args, **kwargs):
                if failure == "result_log":
                    raise RuntimeError("owned log failure")
            monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
            monkeypatch.setattr(runtime, "_write_mcp_tool_call_log", write_log)
            if failure == "normalize":
                monkeypatch.setattr(runtime, "normalize_public_mcp_arguments",
                                    lambda *args: ({}, {"code": "OWNED_ARGUMENT_FAILURE"}))
            request, ctx = context_for({"chatId": "child"})
            expected = asyncio.CancelledError if failure == "cancel" else ValueError if failure == "normalize" else RuntimeError
            with pytest.raises(expected):
                await runtime.mcp._tool_manager.call_tool(request.params.name, request.params.arguments, context=ctx)
            assert REQUEST_ORIGIN.get() == {"clientId": "parent", "chatId": "parent"}
        finally:
            REQUEST_ORIGIN.reset(outer)
    asyncio.run(scenario())


def test_persistent_task_origin_is_frozen_in_history_and_all_projections(tmp_path):
    async def scenario():
        service = PersistentService(tmp_path)
        declared = {"clientId": "client-a", "chatId": "chat-a"}
        expected = normalize_request_origin(declared)
        token = REQUEST_ORIGIN.set(declared)
        try:
            started = await service.task_start("owned", "unity_test_run")
        finally:
            REQUEST_ORIGIN.reset(token)
        assert started.ok
        identity = started.data["taskId"]
        declared["chatId"] = "mutated-input"
        started.data["requestOrigin"]["chatId"] = "mutated-receipt"
        await service._async_task_handles[identity]
        state = service._async_tasks[identity]
        frozen = copy.deepcopy(state)
        query = REQUEST_ORIGIN.set({"clientId": "client-b", "chatId": "chat-b"})
        try:
            for level in ("summary", "full"):
                result = await service.task_status(identity, level)
                assert result.ok and result.data["requestOrigin"] == expected
                result.data["requestOrigin"]["chatId"] = "changed-projection"
        finally:
            REQUEST_ORIGIN.reset(query)
        assert state == frozen
        history = service.server.state.load_test_jobs()
        assert next(v for v in history if v["taskId"] == identity)["requestOrigin"] == expected
        # load_test_jobs is current-lifecycle admission, not the history API.
        # Compare the actual stored JSON to prove history is not rewritten.
        import sqlite3
        def history_bytes():
            with sqlite3.connect(service.server.state._db_path) as db:
                return db.execute("SELECT state_json FROM test_jobs WHERE project_path=? AND task_id=?",
                                  (service.server.state._project_path, identity)).fetchone()[0]
        stored = history_bytes()
        fresh = PersistentService(tmp_path)
        assert not (await fresh.task_status(identity)).ok
        assert not fresh._async_tasks and not fresh._async_task_handles
        assert fresh.server.state.load_test_jobs() == []
        assert history_bytes() == stored
    asyncio.run(scenario())


class PlainService(TaskDomainService):
    def __init__(self):
        self._async_tasks, self._async_task_handles = {}, {}
        self.release = asyncio.Event()
        self.entered = []

    async def task_execute(self, **kwargs):
        self.entered.append(normalize_request_origin(REQUEST_ORIGIN.get()))
        await self.release.wait()
        return ok("owned", {"effectCount": 1})


def test_request_completion_does_not_cancel_background_work_or_change_origin(monkeypatch):
    async def scenario():
        service = PlainService()
        async def fake_call(name, arguments, context=None, convert_result=False):
            response = await service.task_start("owned", "fixture.work", timeout_s=5)
            return CallToolResult(content=[], structuredContent=response.data)
        monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)
        monkeypatch.setattr(runtime, "_write_mcp_tool_call_log", lambda *a, **k: None)
        request, ctx = context_for({"chatId": "original-chat"})
        response = await runtime.mcp._tool_manager.call_tool(request.params.name, request.params.arguments, context=ctx)
        identity = response.structuredContent["taskId"]
        try:
            for _ in range(10):
                if service.entered:
                    break
                await asyncio.sleep(0)
            assert service.entered == [normalize_request_origin({"chatId": "original-chat"})]
            assert not service._async_task_handles[identity].done()
            assert service._async_tasks[identity]["terminal"] is False
        finally:
            service.release.set()
            await service._async_task_handles[identity]
        assert service._async_tasks[identity]["status"] == "completed"
        assert service._async_tasks[identity]["requestOrigin"]["chatId"] == "original-chat"
    asyncio.run(scenario())


@pytest.mark.parametrize("kind", ["Task", "Operation", "Capture", "WriteBatch", "BridgeCommand"])
def test_old_and_uncovered_records_are_unknown_not_current_chat(kind):
    state = {"taskId": "old", "operationId": "old", "status": "running", "startedAt": 1}
    before = copy.deepcopy(state)
    token = REQUEST_ORIGIN.set({"chatId": "current-chat"})
    try:
        assert queue_service.item(kind, "old", state)["requestOrigin"] == normalize_request_origin(None)
        assert TaskDomainService._public_task_state(state)["requestOrigin"] == normalize_request_origin(None)
        service = TaskDomainService()
        for level in ("summary", "standard", "full"):
            assert service._public_operation_state(state, level)["requestOrigin"] == normalize_request_origin(None)
    finally:
        REQUEST_ORIGIN.reset(token)
    assert state == before


def test_origin_survives_summary_budget_without_mutating_labels():
    expected = normalize_request_origin({"clientId": "客" * 128, "chatId": "聊" * 128})
    state = {"durable": True, "requestOrigin": expected,
             "error": {"code": "OWNED", "message": "错误" * 20000},
             "recoveryDiagnostics": [{"code": "OWNED", "message": "诊断" * 5000} for _ in range(50)]}
    frozen = copy.deepcopy(state)
    public = TaskDomainService._public_task_state(state)
    assert public["requestOrigin"] == expected
    assert public["responseBytes"] == TaskDomainService._summary_size(public) <= 16384
    assert state == frozen
    # A tighter operation tail limit must not rewrite the frozen source identity.
    public = TaskDomainService._finalize_summary({"requestOrigin": expected, "detail": "x" * 20000}, 32)
    assert public["requestOrigin"] == expected and public["responseBytes"] <= 16384


def test_real_inventory_projection_distinguishes_sources_without_cleanup(tmp_path, monkeypatch):
    monkeypatch.setattr(queue_service, "query_unity_processes", lambda: (
        [], {"processQuerySucceeded": False, "processQueryError": "owned access denial"}))
    service = QueueService(tmp_path)
    for index in range(2):
        state = service.task(f"owned-{index}")
        state["requestOrigin"] = normalize_request_origin({"clientId": "client", "chatId": f"chat-{index}"})
        state["error"] = {"code": "OWNED_BLOCKER"}
        service.server.state.save_test_job(state)
    before = service.server.state.load_test_jobs()
    response = asyncio.run(service.queue_inventory())
    assert response.ok
    tasks = {v["id"]: v for v in response.data["items"] if v["type"] == "Task"}
    assert [tasks[f"owned-{i}"]["requestOrigin"]["chatId"] for i in range(2)] == ["chat-0", "chat-1"]
    assert all(v["blockingReason"] == "OWNED_BLOCKER" and v["action"] == "cancel" for v in tasks.values())
    assert service.calls == [] and service.server.state.load_test_jobs() == before


@pytest.mark.parametrize("status", ["queued", "running", "cancel_requested", "RecoveryRequired"])
@pytest.mark.parametrize("origin", [None, {"clientId": "client-a", "chatId": "chat-a"}])
def test_active_test_rejection_identifies_exact_blocker_without_mutation(tmp_path, status, origin):
    async def scenario():
        service = PersistentService(tmp_path)
        original = {"taskId": "task-owned-a", "durable": True, "projectPath": service.server.state._project_path,
                    "status": status, "requestOrigin": origin, "ownerToken": "private-token"}
        service._async_tasks["task-owned-a"] = original
        before = copy.deepcopy(original)
        token = REQUEST_ORIGIN.set({"clientId": "client-b", "chatId": "chat-b"})
        try:
            for _ in range(2):
                result = await service.task_start("rejected-b", "unity_test_run")
                assert not result.ok and result.error.code == "TEST_TASK_ALREADY_ACTIVE"
                assert result.data is None
                assert result.error.detail["blocker"] == {
                    "type": "Task", "id": "task-owned-a", "status": status,
                    "requestOrigin": normalize_request_origin(origin), "action": "cancel",
                }
                assert "unity_task_status" in result.error.detail["nextAction"]
                assert "unity_task_cancel" in result.error.detail["nextAction"]
                assert "private-token" not in str(result.error.detail)
                result.error.detail["blocker"]["requestOrigin"]["chatId"] = "modified-receipt"
                assert original == before
        finally:
            REQUEST_ORIGIN.reset(token)
        assert service._async_tasks == {"task-owned-a": before}
        assert service.start_count == 0 and service.cancels == [] and service._async_task_handles == {}
        assert service.server.state.load_test_jobs() == []
    asyncio.run(scenario())


def test_finished_or_other_project_tasks_do_not_become_blockers(tmp_path):
    async def scenario():
        service = PersistentService(tmp_path)
        project = service.server.state._project_path
        service._async_tasks.update({
            "finished": {"durable": True, "projectPath": project, "status": "completed", "terminal": True},
            "other-project": {"durable": True, "projectPath": project + "-other", "status": "running"},
            "non-durable": {"durable": False, "projectPath": project, "status": "running"},
        })
        before = copy.deepcopy(service._async_tasks)
        result = await service.task_start("owned-new", "unity_test_run")
        assert result.ok
        await service._async_task_handles[result.data["taskId"]]
        assert service.start_count == 1 and service.cancels == []
        assert {key: service._async_tasks[key] for key in before} == before
    asyncio.run(scenario())


@pytest.mark.parametrize("old_status", ["completed", "aborted", "timed_out"])
def test_late_old_request_cannot_cancel_or_relabel_successor(tmp_path, old_status):
    """An owned same-lifetime race, not proof of engine cancellation or authorization."""
    from upilot_mcp.test_job_context import TEST_JOB_CONTEXT, checkpoint
    async def scenario():
        service = PersistentService(tmp_path)
        project = service.server.state._project_path
        old = {"taskId": "owned-old", "durable": True, "projectPath": project,
               "status": old_status, "terminal": True, "endedAt": 123,
               "runGuid": "owned-old-run", "deadlineAt": 456,
               "requestOrigin": normalize_request_origin({"clientId": "a", "chatId": "old"})}
        successor = {"taskId": "owned-new", "durable": True, "projectPath": project,
                     "status": "running", "terminal": False, "runGuid": "owned-new-run",
                     "deadlineAt": 789,
                     "requestOrigin": normalize_request_origin({"clientId": "b", "chatId": "new"})}
        service._async_tasks[old["taskId"]] = old
        entered, release = asyncio.Event(), asyncio.Event()
        async def delayed_old_request():
            origin_token = REQUEST_ORIGIN.set(old["requestOrigin"])
            checkpoint_token = TEST_JOB_CONTEXT.set((old, lambda value: pytest.fail("Late callback persisted")))
            try:
                entered.set()
                await release.wait()
                result = await service.task_cancel(old["taskId"])
                assert result.ok and result.data == {"taskId": "owned-old", "status": "not_found", "changed": False}
                with pytest.raises(asyncio.CancelledError):
                    checkpoint("late", terminal=False, status="running", runGuid=successor["runGuid"])
            finally:
                TEST_JOB_CONTEXT.reset(checkpoint_token)
                REQUEST_ORIGIN.reset(origin_token)
        delayed = asyncio.create_task(delayed_old_request())
        await entered.wait()
        service._async_tasks[successor["taskId"]] = successor
        before = copy.deepcopy(service._async_tasks)
        release.set()
        await delayed
        assert service._async_tasks == before
        assert service.cancels == [] and service.start_count == 0
        assert service._async_task_handles == {} and service.server.state.load_test_jobs() == []
        assert REQUEST_ORIGIN.get() is None and TEST_JOB_CONTEXT.get() is None
    asyncio.run(scenario())
