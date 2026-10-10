from __future__ import annotations

import asyncio
from dataclasses import asdict
import json
from types import SimpleNamespace

import pytest

from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp.state_store import CommandRecord, StateStore


def test_missing_and_legacy_command_facts_are_unknown_not_zero():
    state = StateStore()
    missing = state.command_observation("original")
    assert missing["status"] == "unknown"
    assert missing["reason"] == "command_record_not_in_current_service_lifetime"
    assert "sendAttempts" not in missing
    legacy = CommandRecord("original", "request", "reflection.call", {})
    state.commands["original"] = legacy
    observed = state.command_observation("original")
    assert observed["status"] == "observed"
    assert observed["sendAttempts"] is None
    assert observed["responseWaitDeadlineAt"] is None
    assert observed["responseWaitTimeoutMs"] is None
    assert observed["callStartedAt"] is None
    assert observed["callDeadlineAt"] is None
    assert observed["callTimeoutMs"] is None


def test_projection_is_read_only_bounded_and_does_not_restore_history():
    state = StateStore()
    record = state.create_command("original", "request", "reflection.call", {"ownerToken": "secret"})
    record.result = {"large": "z" * 100000, "ownerToken": "secret"}
    frozen = asdict(record)
    first = state.command_observation("original")
    second = state.command_observation("original")
    assert first == second and asdict(record) == frozen
    raw = json.dumps(first)
    assert len(raw) < 1500 and "secret" not in raw and "large" not in raw
    assert first["sendAttempts"] == 0 and first["responseWaitDeadlineAt"] is None
    successor = StateStore().command_observation("original")
    assert successor["status"] == "unknown"
    assert successor["serviceLifecycleId"] != first["serviceLifecycleId"]
    assert asdict(record) == frozen


@pytest.mark.parametrize("success", [True, False])
def test_operation_get_preserves_unity_result_or_first_error_and_adds_server_facts(success):
    async def scenario():
        state = StateStore()
        record = state.create_command("original", "request", "reflection.call", {})
        record.send_attempts = 1
        state.mark_failed("original", {"code": "COMMAND_RECOVERY_REQUIRED"})
        frozen = asdict(record)
        calls = []
        original_data = {"commandId": "original", "status": "failed", "terminal": True}
        original_detail = {"commandId": "original", "tracker": "not_in_current_domain"}
        async def dispatch(request_id, name, payload):
            calls.append((name, payload))
            return (ok(request_id, original_data) if success else
                    fail(request_id, "OPERATION_NOT_FOUND", "original tracker is missing", original_detail))
        service = TaskDomainService()
        service.server = SimpleNamespace(state=state)
        service.dispatcher = SimpleNamespace(call=dispatch)
        result = await service.operation_get("original")
        assert result.ok is success
        if success:
            assert result.error is None and original_data == {
                "commandId": "original", "status": "failed", "terminal": True}
            assert {k: v for k, v in result.data.items() if k != "serverCommandObservation"} == original_data
            observation = result.data["serverCommandObservation"]
        else:
            assert result.data is None and result.error.code == "OPERATION_NOT_FOUND"
            assert result.error.message == "original tracker is missing"
            assert original_detail == {"commandId": "original", "tracker": "not_in_current_domain"}
            observation = result.error.detail["serverCommandObservation"]
        assert observation["commandId"] == "original" and observation["sendAttempts"] == 1
        assert observation["recordStatus"] == "failed"
        assert calls == [("operation.get", {"commandId": "original"})]
        assert asdict(record) == frozen
    asyncio.run(scenario())


class _Socket:
    def __init__(self, failure=None):
        self.sent = []
        self.failure = failure
    async def send(self, raw):
        self.sent.append(raw)
        if self.failure is not None:
            raise self.failure


def _connected_server(project, socket):
    server = WsOrchestratorServer(expected_project_path=str(project))
    server.session_manager.on_hello("active", {
        "projectPath": str(project), "platform": "windows", "processId": 101,
        "processCreatedAt": 202, "identityVerified": True,
        "verificationLevel": "test-verified", "processRole": "mainEditor",
    })
    server._ws = socket
    return server


@pytest.mark.parametrize("failure", [ConnectionError("socket failed"), asyncio.CancelledError()])
def test_actual_send_entry_is_counted_even_if_delivery_is_unknown(tmp_path, failure):
    async def scenario():
        socket = _Socket(failure)
        server = _connected_server(tmp_path, socket)
        dispatcher = CommandDispatcher(server, server.state)
        with pytest.raises(type(failure)):
            await dispatcher.call("original-request", "reflection.call", {}, timeout_ms=1000)
        record, = server.state.commands.values()
        assert record.send_attempts == 1 and len(socket.sent) == 1
        assert record.status == "failed" and record.error["detail"]["outcome"] == "unknown"
        # Sending failed before the existing response wait was entered.
        assert record.response_wait_started_at == 0 and record.response_wait_deadline_at == 0
        assert record.response_wait_timeout_ms is None
        assert not server._pending and not server._suspended
    asyncio.run(scenario())


@pytest.mark.parametrize("terminal", ["success", "failed"])
def test_terminal_command_is_never_sent_again_and_record_is_frozen(tmp_path, terminal):
    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", "reflection.call", {})
        await server.send_command("original", "reflection.call", {})
        assert record.send_attempts == 1 and len(socket.sent) == 1
        if terminal == "success":
            server.state.mark_success("original", {"effect": "once"})
        else:
            server.state.mark_failed("original", {"code": "ORIGINAL_FAILURE"})
        frozen = asdict(record)
        with pytest.raises(ConnectionError, match="already terminal"):
            await server.send_command("original", "reflection.call", {})
        assert asdict(record) == frozen and len(socket.sent) == 1
        assert not server._pending and not server._suspended
    asyncio.run(scenario())


def test_disconnected_send_is_not_counted_as_socket_entry(tmp_path):
    async def scenario():
        server = WsOrchestratorServer(expected_project_path=str(tmp_path))
        record = server.state.create_command("original", "request", "reflection.call", {})
        await server.send_command("original", "reflection.call", {})
        assert record.send_attempts == 0
        assert record.response_wait_started_at == 0
    asyncio.run(scenario())


@pytest.mark.parametrize("first", ["success", "failed"])
def test_first_terminal_is_frozen_against_late_results_and_errors(first):
    state = StateStore()
    record = state.create_command("original", "request", "reflection.call", {})
    if first == "success":
        state.mark_success("original", {"effect": "once"})
    else:
        state.mark_failed("original", {"code": "ORIGINAL_FAILURE"})
    frozen = asdict(record)
    state.mark_success("original", {"effect": "late"})
    state.mark_failed("original", {"code": "LATE_FAILURE"})
    assert asdict(record) == frozen


class _BudgetTransport:
    """An owned dispatcher observer, never a real Unity connection."""
    def __init__(self, state, clock, ready_cost=0, send_cost=0, response_delay=0):
        self.state, self.clock = state, clock
        self.ready_cost, self.send_cost, self.response_delay = ready_cost, send_cost, response_delay
        self.ready = False
        self.pending = {}
        self.sent = []
        self.handle = None
        self.ready_limit = None

    def is_ready(self):
        return self.ready

    async def wait_until_ready(self, timeout):
        self.ready_limit = timeout
        self.clock.now += self.ready_cost
        self.ready = True
        return True

    def register_pending(self, command_id):
        future = asyncio.get_running_loop().create_future()
        self.pending[command_id] = future
        return future

    def unregister_pending(self, command_id):
        future = self.pending.pop(command_id, None)
        if future is not None and not future.done():
            future.cancel()
        if self.handle is not None:
            self.handle.cancel()

    async def send_command(self, command_id, name, payload):
        self.sent.append(command_id)
        self.state.commands[command_id].send_attempts += 1
        self.clock.now += self.send_cost
        self.handle = asyncio.get_running_loop().call_later(
            self.response_delay, self.pending[command_id].set_result,
            {"type": "result", "payload": {"effect": "once"}})


def _budget_clock(monkeypatch):
    import upilot_mcp.dispatcher as module
    clock = SimpleNamespace(now=0.0)
    # Do not patch global time.monotonic: asyncio's real event loop remains independent.
    monkeypatch.setattr(module, "time", SimpleNamespace(
        monotonic=lambda: clock.now, time=lambda: 1000 + clock.now))
    return clock


def test_ready_send_and_response_share_one_fixed_observation_budget(monkeypatch):
    async def scenario():
        clock = _budget_clock(monkeypatch)
        state = StateStore()
        transport = _BudgetTransport(state, clock, ready_cost=.04, send_cost=.04, response_delay=.05)
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=100)
        record, = state.commands.values()
        assert not result.ok and result.error.code == "COMMAND_TIMEOUT"
        assert result.error.detail["waitPhase"] == "response"
        assert result.error.detail["outcome"] == "unknown"
        assert record.call_timeout_ms == 100 and record.call_deadline_at - record.call_started_at == 100
        assert record.call_deadline_monotonic == pytest.approx(.1)
        assert 0 < record.response_wait_timeout_ms <= 20
        assert record.response_wait_deadline_at <= record.call_deadline_at
        assert record.send_attempts == 1 and len(transport.sent) == 1
        assert not transport.pending
        observed = state.command_observation(record.command_id)
        assert observed["callDeadlineAt"] == record.call_deadline_at
    asyncio.run(scenario())


def test_ready_timeout_does_not_create_or_send_a_command(monkeypatch):
    async def scenario():
        state = StateStore()
        transport = _BudgetTransport(state, SimpleNamespace(now=0))
        async def never_ready(timeout):
            transport.ready_limit = timeout
            await asyncio.Event().wait()
        transport.wait_until_ready = never_ready
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=20)
        assert not result.ok and result.error.code == "COMMAND_TIMEOUT"
        assert result.error.detail["waitPhase"] == "ready"
        assert result.error.detail["outcome"] == "not_sent"
        assert transport.ready_limit <= .02
        assert not state.commands and not transport.sent and not transport.pending
    asyncio.run(scenario())


@pytest.mark.parametrize("prior_failure", [False, True])
def test_send_timeout_releases_observer_without_replay_or_overwriting_first_failure(prior_failure):
    async def scenario():
        state = StateStore()
        transport = _BudgetTransport(state, SimpleNamespace(now=0))
        transport.ready = True
        async def uncertain_send(command_id, name, payload):
            transport.sent.append(command_id)
            state.commands[command_id].send_attempts += 1
            if prior_failure:
                state.mark_failed(command_id, {"code": "COMMAND_RECOVERY_REQUIRED", "message": "original"})
            await asyncio.Event().wait()
        transport.send_command = uncertain_send
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=20)
        record, = state.commands.values()
        expected = "COMMAND_RECOVERY_REQUIRED" if prior_failure else "COMMAND_TIMEOUT"
        assert not result.ok and result.error.code == expected
        assert record.error["code"] == expected
        if not prior_failure:
            assert result.error.detail["waitPhase"] == "send"
            assert result.error.detail["outcome"] == "unknown"
        assert record.send_attempts == 1 and len(transport.sent) == 1
        assert record.response_wait_timeout_ms is None and not transport.pending
    asyncio.run(scenario())


def test_late_success_cannot_replace_frozen_recovery_failure():
    async def scenario():
        state = StateStore()
        transport = _BudgetTransport(state, SimpleNamespace(now=0))
        transport.ready = True
        frozen = None
        async def late_send(command_id, name, payload):
            nonlocal frozen
            transport.sent.append(command_id)
            state.commands[command_id].send_attempts += 1
            state.mark_failed(command_id, {"code": "COMMAND_RECOVERY_REQUIRED", "message": "original"})
            frozen = asdict(state.commands[command_id])
            transport.pending[command_id].set_result({"type": "result", "payload": {"late": True}})
        transport.send_command = late_send
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=100)
        record, = state.commands.values()
        assert not result.ok and result.error.code == "COMMAND_RECOVERY_REQUIRED"
        assert asdict(record) == frozen and len(transport.sent) == 1
    asyncio.run(scenario())


def test_success_keeps_separate_total_and_remaining_response_wait_facts(monkeypatch):
    async def scenario():
        clock = _budget_clock(monkeypatch)
        state = StateStore()
        transport = _BudgetTransport(state, clock, ready_cost=.02, send_cost=.02, response_delay=.03)
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=100)
        record, = state.commands.values()
        assert result.ok and record.status == "success"
        assert record.call_timeout_ms == 100
        assert 0 < record.response_wait_timeout_ms <= 60
        assert record.response_wait_deadline_at <= record.call_deadline_at
        assert record.send_attempts == 1
    asyncio.run(scenario())


@pytest.mark.parametrize("detail", [None, "legacy detail", ["legacy detail"]])
def test_send_timeout_preserves_first_error_with_non_object_legacy_detail(detail):
    async def scenario():
        state = StateStore()
        transport = _BudgetTransport(state, SimpleNamespace(now=0))
        transport.ready = True
        async def uncertain_send(command_id, name, payload):
            transport.sent.append(command_id)
            state.commands[command_id].send_attempts += 1
            state.mark_failed(command_id, {"code": "ORIGINAL_FAILURE", "message": "original", "detail": detail})
            await asyncio.Event().wait()
        transport.send_command = uncertain_send
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=20)
        record, = state.commands.values()
        assert not result.ok and result.error.code == "ORIGINAL_FAILURE"
        assert record.error["detail"] == detail
        assert result.error.detail["commandId"] == record.command_id
        assert len(transport.sent) == 1 and not transport.pending
    asyncio.run(scenario())


def test_shorter_ready_cap_does_not_claim_total_budget_expired(monkeypatch):
    monkeypatch.setenv("UPILOT_CALL_WAIT_READY_S", "0.01")
    async def scenario():
        state = StateStore()
        transport = _BudgetTransport(state, SimpleNamespace(now=0))
        async def never_ready(timeout):
            transport.ready_limit = timeout
            await asyncio.Event().wait()
        transport.wait_until_ready = never_ready
        result = await CommandDispatcher(transport, state).call("request", "reflection.call", {}, timeout_ms=1000)
        assert not result.ok and result.error.code == "UNITY_NOT_CONNECTED"
        assert transport.ready_limit == .01
        assert not state.commands and not transport.sent and not transport.pending
    asyncio.run(scenario())


@pytest.mark.parametrize("elapsed", [0, 1], ids=["at-deadline", "past-deadline"])
def test_socket_entry_rejects_elapsed_original_monotonic_budget(tmp_path, monkeypatch, elapsed):
    import upilot_mcp.server as module

    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", "reflection.call", {})
        record.call_deadline_monotonic = 100
        frozen = asdict(record)
        monkeypatch.setattr(module, "time", SimpleNamespace(monotonic=lambda: 100 + elapsed))
        with pytest.raises(asyncio.TimeoutError, match="before socket send"):
            await server.send_command("original", record.name, record.payload)
        assert not socket.sent and record.send_attempts == 0
        assert asdict(record) == frozen
        assert not server._pending and not server._suspended

    asyncio.run(scenario())


def test_dispatcher_socket_budget_rejection_uses_original_timeout_result(tmp_path, monkeypatch):
    import upilot_mcp.server as module

    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        monkeypatch.setattr(module, "time", SimpleNamespace(
            monotonic=lambda: next(iter(server.state.commands.values())).call_deadline_monotonic + 1))
        response = await CommandDispatcher(server, server.state).call(
            "request", "reflection.call", {}, timeout_ms=1000)
        record, = server.state.commands.values()
        assert not response.ok and response.error.code == "COMMAND_TIMEOUT"
        assert response.error.detail["waitPhase"] == "send"
        assert record.status == "failed" and record.send_attempts == 0 and not socket.sent
        assert record.call_deadline_at - record.call_started_at == record.call_timeout_ms == 1000
        assert record.call_deadline_monotonic > 0
        facts = server.state.command_observation(record.command_id)
        assert "callDeadlineMonotonic" not in facts and "call_deadline_monotonic" not in facts
        assert not server._pending and not server._suspended

    asyncio.run(scenario())


@pytest.mark.parametrize("name,payload", [
    ("resource.editorState", {}), ("test.status", {}), ("compile.errors.get", {}),
    ("test.results", {"runGuid": "original-run"}),
])
@pytest.mark.parametrize("elapsed", [0, 1], ids=["at-deadline", "past-deadline"])
def test_reload_query_cannot_send_after_original_budget_before_timeout_callback(
        tmp_path, monkeypatch, name, payload, elapsed):
    import upilot_mcp.server as module

    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", name, payload)
        record.call_deadline_monotonic = 100
        record.call_started_at, record.call_deadline_at, record.call_timeout_ms = 1000, 2000, 1000
        monkeypatch.setattr(module, "time", SimpleNamespace(monotonic=lambda: 99))
        await server.send_command(record.command_id, name, payload)
        future = server.register_pending(record.command_id)  # Deliberately still pending at deadline.
        original_budget = (record.call_started_at, record.call_deadline_at, record.call_timeout_ms,
                           record.call_deadline_monotonic)
        monkeypatch.setattr(module, "time", SimpleNamespace(monotonic=lambda: 100 + elapsed))
        await server._resend_pending_commands()
        result = future.result()
        assert result["id"] == "original" and result["type"] == "error"
        assert result["payload"]["code"] == "COMMAND_TIMEOUT"
        assert result["payload"]["detail"]["waitPhase"] == "reload_recovery"
        assert result["payload"]["detail"]["outcome"] == "unknown"
        assert result["payload"]["detail"]["replayAttempted"] is False
        assert record.send_attempts == len(socket.sent) == 1
        assert original_budget == (record.call_started_at, record.call_deadline_at, record.call_timeout_ms,
                                   record.call_deadline_monotonic)
        frozen = asdict(record)
        server.state.mark_success("original", {"late": True})
        server.state.mark_failed("original", {"code": "LATE_FAILURE"})
        await server._resend_pending_commands()
        assert asdict(record) == frozen and len(socket.sent) == 1
        assert not server._pending and not server._suspended

    asyncio.run(scenario())


@pytest.mark.parametrize("wall_deadline", [1, 10**15], ids=["wall-clock-past", "wall-clock-future"])
def test_reload_live_query_keeps_original_identity_and_ignores_wall_clock_skew(tmp_path, wall_deadline):
    import time

    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", "test.results", {"runGuid": "original-run"})
        record.call_deadline_monotonic = time.monotonic() + 1
        record.call_deadline_at = wall_deadline
        await server.send_command(record.command_id, record.name, record.payload)
        future = server.register_pending(record.command_id)
        original_budget = (record.call_deadline_monotonic, record.call_deadline_at)
        await server._resend_pending_commands()
        assert record.send_attempts == len(socket.sent) == 2
        messages = [json.loads(raw) for raw in socket.sent]
        assert all(message["id"] == "original" and message["name"] == "test.results"
                   and message["payload"] == {"runGuid": "original-run"} for message in messages)
        assert (record.call_deadline_monotonic, record.call_deadline_at) == original_budget
        assert not future.done() and server._pending["original"] is future
        server.unregister_pending("original")
        assert future.cancelled() and not server._pending and not server._suspended

    asyncio.run(scenario())


@pytest.mark.parametrize("terminal", [None, "success", "failed"], ids=["timeout", "success-wins", "failure-wins"])
def test_reload_blocked_socket_uses_remaining_budget_and_preserves_first_terminal(tmp_path, terminal):
    import time

    async def scenario():
        class BlockingSocket(_Socket):
            blocked = False
            cancelled = False
            frozen = None

            async def send(self, raw):
                await super().send(raw)
                if self.blocked:
                    try:
                        await asyncio.Event().wait()
                    finally:
                        self.cancelled = True
                        if terminal == "success":
                            server.state.mark_success("original", {"effect": "original-result"})
                        elif terminal == "failed":
                            server.state.mark_failed("original", {"code": "ORIGINAL_FAILURE"})
                        if terminal:
                            self.frozen = asdict(record)

        socket = BlockingSocket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", "test.status", {})
        await server.send_command("original", record.name, record.payload)
        future = server.register_pending("original")
        record.call_deadline_monotonic = time.monotonic() + .03
        deadline = record.call_deadline_monotonic
        socket.blocked = True
        await asyncio.wait_for(server._resend_pending_commands(), timeout=1)
        response = future.result()
        assert socket.cancelled and record.send_attempts == len(socket.sent) == 2
        assert record.call_deadline_monotonic == deadline
        assert not server._pending and not server._suspended
        if terminal:
            assert asdict(record) == socket.frozen
            assert response["type"] == ("result" if terminal == "success" else "error")
            assert response["payload"] == (record.result if terminal == "success" else record.error)
        else:
            assert record.status == "failed" and response["payload"]["code"] == "COMMAND_TIMEOUT"
            assert response["payload"]["detail"]["replayAttempted"] is True
            assert response["payload"]["detail"]["outcome"] == "unknown"
        frozen = asdict(record)
        server.state.mark_failed("original", {"code": "LATE_FAILURE"})
        server.state.mark_success("original", {"effect": "late"})
        assert asdict(record) == frozen

    asyncio.run(scenario())


@pytest.mark.parametrize("name", ["test.status", "reflection.call"])
@pytest.mark.parametrize("terminal", ["success", "failed"])
def test_reload_pending_observer_projects_first_terminal_without_send(tmp_path, name, terminal):
    async def scenario():
        socket = _Socket()
        server = _connected_server(tmp_path, socket)
        record = server.state.create_command("original", "request", name, {})
        if terminal == "success":
            server.state.mark_success("original", {"effect": "once"})
        else:
            server.state.mark_failed("original", {"code": "ORIGINAL_FAILURE"})
        record.call_deadline_monotonic = 1  # First terminal must win even over an elapsed budget.
        frozen = asdict(record)
        future = server.register_pending("original")
        await server._resend_pending_commands()
        assert future.result() == {"id": "original", "name": name,
                                   "type": "result" if terminal == "success" else "error",
                                   "payload": record.result if terminal == "success" else record.error}
        assert asdict(record) == frozen and record.send_attempts == 0 and not socket.sent
        assert not server._pending and not server._suspended

    asyncio.run(scenario())
