from __future__ import annotations

import asyncio
import os
import ast
import base64
import hashlib
import json
import subprocess
import sys
import types
from pathlib import Path

import pytest

from upilot_mcp.config import diagnose_client_configs
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.models import ToolResponse
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore
from upilot_mcp.tool_registry import REGISTRY, ToolDescriptor, ToolRegistry, dispatch_public_tool, register_public_tool
from upilot_mcp.config import CONFIG


class _Facade:
    async def echo(self, value: str = "") -> ToolResponse:
        from upilot_mcp.responses import ok

        return ok("req-test", {"value": value})


@pytest.fixture
def register_test_tool():
    """Register a synthetic tool without leaking it into later inventory checks."""
    missing = object()
    originals = {}

    def register(name: str, **kwargs) -> None:
        if name not in originals:
            originals[name] = REGISTRY._items.get(name, missing)
        register_public_tool(name, **kwargs)

    yield register

    for name, original in originals.items():
        if original is missing:
            REGISTRY._items.pop(name, None)
        else:
            REGISTRY._items[name] = original


class _Transport:
    def __init__(self, result: dict) -> None:
        self.result = result
        self.future: asyncio.Future | None = None

    def is_ready(self) -> bool:
        return True

    def register_pending(self, command_id: str) -> asyncio.Future:
        self.future = asyncio.get_running_loop().create_future()
        return self.future

    async def send_command(self, command_id: str, name: str, payload: dict) -> None:
        assert self.future is not None
        self.future.set_result(self.result)

    def unregister_pending(self, command_id: str) -> None:
        self.unregistered = getattr(self, "unregistered", [])
        self.unregistered.append(command_id)


def test_registry_is_stable_and_reports_flow_availability() -> None:
    registry = ToolRegistry()
    registry.register(ToolDescriptor("unity_z", "z", "test"))
    registry.register(ToolDescriptor("unity_a", "a", "test", feature="flow"))

    assert [item.name for item in registry.list()] == ["unity_a", "unity_z"]
    unavailable = registry.find(availability="unavailable", flow_enabled=False)
    assert [item["name"] for item in unavailable] == ["unity_a"]
    assert unavailable[0]["unavailableReason"]


def test_registry_find_supports_multi_token_queries_and_runtime_availability() -> None:
    registry = ToolRegistry()
    registry.register(ToolDescriptor("unity_sync_after_disk_write", "sync_after_disk_write", "resource"))
    registry.register(ToolDescriptor("unity_compile_errors", "compile_errors", "compile"))
    registry.register(
        ToolDescriptor(
            "unity_asset_modify_data",
            "asset_modify_data",
            "asset",
            destructive=True,
            requires_write_access=True,
        )
    )

    sync_matches = registry.find(
        query="sync disk write",
        flow_enabled=False,
        connected=False,
        server_ready=False,
        write_access_approved=False,
    )
    assert sync_matches[0]["name"] == "unity_sync_after_disk_write"
    assert sync_matches[0]["registered"] is True
    assert sync_matches[0]["callableNow"] is False
    assert sync_matches[0]["unavailableReason"] == "UNITY_NOT_CONNECTED"

    write_matches = registry.find(
        query="modify data",
        flow_enabled=False,
        connected=True,
        server_ready=True,
        write_access_approved=False,
    )
    assert write_matches[0]["name"] == "unity_asset_modify_data"
    assert write_matches[0]["requiresWriteAccess"] is True
    assert write_matches[0]["callableNow"] is False
    assert write_matches[0]["unavailableReason"] == "WRITE_ACCESS_NOT_APPROVED"

    exact = registry.find(
        query="unity_compile_errors",
        availability="callableNow",
        flow_enabled=False,
        connected=True,
        server_ready=True,
        write_access_approved=True,
    )
    assert exact[0]["name"] == "unity_compile_errors"
    assert exact[0]["exactMatch"] is True


def test_compile_playmode_rejection_exposes_blocked_reason(monkeypatch) -> None:
    from upilot_mcp import mcp_stdio_server as runtime

    async def _playing() -> bool:
        return True

    monkeypatch.setattr(runtime, "_unity_is_playmode", _playing)
    result = asyncio.run(runtime._reject_compile_in_playmode("unity_sync_after_disk_write"))

    assert result.isError is True
    detail = result.structuredContent["error"]["detail"]
    assert detail["blockedReason"] == "PlayMode"
    assert detail["playModeBlocked"] is True
    assert detail["nextAction"]


def test_sync_precheck_allows_active_domain_reload_recovery(monkeypatch) -> None:
    from upilot_mcp import mcp_stdio_server as runtime

    async def _must_not_query_live_playmode() -> bool:
        raise AssertionError("active compile recovery must not wait for a live PlayMode query")

    class _State:
        @staticmethod
        def execution_state(stale_after_ms: int) -> dict:
            return {
                "authoritative": False,
                "isStale": True,
                "playModeState": "unknown",
                "compilePhase": "domain_reload",
            }

    class _Server:
        state = _State()

    class _Facade:
        server = _Server()

    monkeypatch.setattr(runtime, "_unity_is_playmode", _must_not_query_live_playmode)
    monkeypatch.setattr(runtime, "_facade", _Facade())

    result = asyncio.run(runtime._reject_compile_in_playmode("unity_sync_after_disk_write"))

    assert result is None


def test_safe_compile_precheck_allows_stale_completed_reload_recovery(monkeypatch) -> None:
    from upilot_mcp import mcp_stdio_server as runtime

    async def _must_not_query_live_playmode() -> bool:
        raise AssertionError("stale completed recovery must preserve the invocation snapshot")

    class _State:
        @staticmethod
        def execution_state(stale_after_ms: int) -> dict:
            return {
                "authoritative": False,
                "isStale": True,
                "playModeState": "edit",
                "compilePhase": "completed",
                "compileRequestId": "req-completed-reload",
            }

    class _Server:
        state = _State()

    class _Facade:
        server = _Server()

    monkeypatch.setattr(runtime, "_unity_is_playmode", _must_not_query_live_playmode)
    monkeypatch.setattr(runtime, "_facade", _Facade())

    result = asyncio.run(runtime._reject_compile_in_playmode("unity_safe_compile_and_wait"))

    assert result is None


def test_dispatcher_preserves_authoritative_bridge_context() -> None:
    context = {
        "connected": True,
        "authoritative": True,
        "source": "bridge-response",
        "sessionId": "session-1",
        "updatedAt": 123,
        "playModeState": "play",
        "isCompiling": False,
        "activeScene": "World",
        "lastMainThreadPumpAt": 123,
        "mainThreadQueueDepth": 2,
        "processId": 42,
    }
    state = StateStore()
    dispatcher = CommandDispatcher(_Transport({"type": "result", "payload": {"ok": True}, "context": context}), state)
    result = asyncio.run(dispatcher.call("req-context", "test.command", {}))

    assert result.ok
    assert result.context == context
    assert state.editor.play_mode_state == "play"
    assert state.editor.authoritative is True
    assert state.editor.main_thread_queue_depth == 2
    assert state.editor.process_id == 42


def test_test_run_dispatch_preserves_request_and_operation_identity() -> None:
    from upilot_mcp.operation_context import OPERATION_ID
    async def run():
        state = StateStore()
        dispatcher = CommandDispatcher(_Transport({"type": "result", "payload": {"ok": True}}), state)
        token = OPERATION_ID.set("op-runner")
        try:
            result = await dispatcher.call("req-runner", "test.run", {"testMode": "PlayMode"})
        finally:
            OPERATION_ID.reset(token)
        assert result.ok
        command = next(iter(state.commands.values()))
        assert command.payload["requestId"] == "req-runner"
        assert command.payload["operationId"] == "op-runner"
    asyncio.run(run())


def test_dispatcher_removes_timed_out_command_from_transport() -> None:
    class _NeverCompletes(_Transport):
        async def send_command(self, command_id: str, name: str, payload: dict) -> None:
            return

    transport = _NeverCompletes({"type": "result", "payload": {}})
    result = asyncio.run(
        CommandDispatcher(transport, StateStore()).call(
            "req-timeout", "console.capture.read", {}, timeout_ms=1
        )
    )

    assert not result.ok
    assert result.error and result.error.code == "COMMAND_TIMEOUT"
    assert len(transport.unregistered) == 1


def test_server_fails_pending_commands_on_ordinary_disconnect() -> None:
    from upilot_mcp.server import WsOrchestratorServer

    server = WsOrchestratorServer()
    future = asyncio.new_event_loop().create_future()
    server._pending["cmd-ordinary-disconnect"] = future
    server._domain_reloading = False

    server._suspend_or_fail_pending_on_disconnect("session-ordinary")

    assert not server._pending
    assert not server._suspended
    assert future.done()
    result = future.result()
    assert result["type"] == "error"
    assert result["payload"]["code"] == "CONNECTION_LOST"


def test_ensure_ready_uses_authoritative_bridge_context_for_legacy_editor_state_payload() -> None:
    state = StateStore()
    context = {
        "connected": True,
        "authoritative": True,
        "source": "bridge-response",
        "sessionId": "session-ready",
        "updatedAt": int(__import__("time").time() * 1000),
        "playModeState": "edit",
        "isCompiling": False,
        "activeScene": "Launch",
    }
    state.update_editor_state(context)

    class _SessionManager:
        @staticmethod
        def is_connected() -> bool:
            return True

    class _Server:
        session_manager = _SessionManager()

        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isPlaying": False, "isPaused": False, "isCompiling": False}, context=context)

    service = TaskDomainService()
    service.server = _Server()
    service.dispatcher = _Dispatcher()

    async def _compile_wait(**_: object) -> ToolResponse:
        return ok("req-compile", {"status": "ready"})

    service.compile_wait = _compile_wait
    result = asyncio.run(service.ensure_ready(timeout_s=1))

    assert result.ok
    assert result.data["ready"] is True
    assert result.data["playModeState"] == "edit"
    assert result.data["contextAuthoritative"] is True
    assert result.data["contextStale"] is False


def test_playmode_start_waits_for_authoritative_play_context() -> None:
    state = StateStore()
    timestamp = int(__import__("time").time() * 1000)
    edit_context = {
        "connected": True,
        "authoritative": True,
        "source": "bridge-response",
        "sessionId": "session-playmode",
        "updatedAt": timestamp,
        "playModeState": "edit",
        "isPlaying": False,
        "isCompiling": False,
        "activeScene": "Acceptance",
    }
    play_context = {
        **edit_context,
        "updatedAt": timestamp + 1,
        "playModeState": "play",
        "isPlaying": True,
    }

    class _SessionManager:
        active = types.SimpleNamespace(session_id="session-playmode", process_id=42)

        @staticmethod
        def is_connected() -> bool:
            return True

    class _Server:
        session_manager = _SessionManager()

        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        calls = 0

        async def call(
            self,
            request_id: str,
            name: str,
            payload: dict,
            timeout_ms: int | None = None,
        ) -> ToolResponse:
            if name == "playmode.set":
                return ok(request_id, {"state": "play"}, context=edit_context)
            assert name == "resource.editorState"
            self.calls += 1
            return ok(
                request_id,
                {
                    "isPlaying": True,
                    "isPaused": False,
                    "isCompiling": False,
                    "activeSceneName": "Acceptance",
                },
                context=play_context,
            )

    service = StatusDomainService()
    service.server = _Server()
    service.dispatcher = _Dispatcher()

    result = asyncio.run(service.playmode_start())

    assert result.ok
    assert result.data["confirmed"] is True
    assert result.data["playModeState"] == "play"
    assert result.data["editorState"]["playModeState"] == "play"
    assert result.context == play_context


def test_playmode_pause_wait_false_submits_once_and_invalid_budget_submits_never() -> None:
    class _SessionManager:
        @staticmethod
        def is_connected() -> bool:
            return True

    class _Server:
        session_manager = _SessionManager()
        state = StateStore()

    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None) -> ToolResponse:
            self.calls.append((name, payload))
            return ok(request_id, {"state": "pause", "changed": False, "writeCount": 0, "observedState": "pause"})

    service = StatusDomainService()
    service.server = _Server()
    service.dispatcher = _Dispatcher()
    _authoritative_editor_state(service.server.state, play_mode_state="play")

    invalid = asyncio.run(service.playmode_pause(timeout_ms=0))
    submitted = asyncio.run(service.playmode_pause(wait=False))

    assert not invalid.ok and invalid.error.code == "INVALID_PAYLOAD"
    assert submitted.ok and submitted.data["commandSubmitted"] is True
    assert submitted.data["confirmed"] is False and submitted.data["terminal"] is False
    assert submitted.data["stateObserved"] is True
    assert submitted.data["changed"] is False and submitted.data["writeCount"] == 0
    assert submitted.data["commandObservedState"] == "pause"
    assert submitted.data["commandId"] == submitted.request_id
    assert service.dispatcher.calls == [("playmode.set", {"action": "pause"})]


def test_playmode_pause_rejects_non_boolean_wait_and_boolean_timeout_without_dispatch() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            raise AssertionError("invalid pause request must not dispatch")

    service = StatusDomainService.__new__(StatusDomainService)
    service.dispatcher = _Dispatcher()

    invalid_wait = asyncio.run(service.playmode_pause(wait="false"))
    invalid_timeout = asyncio.run(service.playmode_resume(timeout_ms=True))

    assert not invalid_wait.ok
    assert invalid_wait.error.code == "INVALID_PAYLOAD"
    assert invalid_wait.error.detail["field"] == "wait"
    assert invalid_wait.error.detail["commandSubmitted"] is False
    assert invalid_wait.error.detail["sideEffectsMayHaveOccurred"] is False
    assert not invalid_timeout.ok
    assert invalid_timeout.error.code == "INVALID_PAYLOAD"
    assert invalid_timeout.error.detail["field"] == "timeoutMs"
    assert service.dispatcher.calls == []


def test_playmode_pause_preflight_rejects_edit_stale_and_transition_without_dispatch() -> None:
    class _State:
        def __init__(self, execution: dict) -> None:
            self.execution = execution

        def execution_state(self) -> dict:
            return self.execution

        def update_editor_state(self, payload: dict) -> bool:
            self.execution.update({
                "authoritative": bool(payload["authoritative"]),
                "isStale": False,
                "isCompiling": bool(payload["isCompiling"]),
                "playModeState": str(payload["playModeState"]),
            })
            return True

    class _SessionManager:
        active = types.SimpleNamespace(session_id="session-pause", process_id=42)

        @staticmethod
        def is_connected() -> bool:
            return True

    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            raise AssertionError("blocked pause must not dispatch")

    base = {"authoritative": True, "isStale": False, "isCompiling": False, "transition": "snapshot"}
    for execution, code in (
        ({**base, "playModeState": "edit"}, "PLAYMODE_REQUIRED"),
        ({**base, "playModeState": "play", "isStale": True}, "EDITOR_STATE_NOT_READY"),
        ({**base, "playModeState": "play", "transition": "enteringPlayMode"}, "PLAYMODE_TRANSITION_IN_PROGRESS"),
    ):
        service = StatusDomainService()
        service.server = type("Server", (), {"state": _State(execution), "session_manager": _SessionManager()})()
        service.dispatcher = _Dispatcher()
        result = asyncio.run(service.playmode_pause())
        assert not result.ok and result.error.code == code
        assert result.error.detail["commandSubmitted"] is False
        assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert _Dispatcher.calls == []


def test_playmode_pause_idempotence_and_timeout_keep_write_count_and_budget_bounded() -> None:
    class _State:
        def __init__(self, execution: dict) -> None:
            self.execution = execution

        def execution_state(self) -> dict:
            return self.execution

        def update_editor_state(self, payload: dict) -> bool:
            self.execution.update({
                "authoritative": bool(payload["authoritative"]),
                "isStale": False,
                "isCompiling": bool(payload["isCompiling"]),
                "playModeState": str(payload["playModeState"]),
            })
            return True

    class _SessionManager:
        active = types.SimpleNamespace(session_id="session-pause", process_id=42)

        @staticmethod
        def is_connected() -> bool:
            return True

    class _Dispatcher:
        def __init__(self, observe_pause: bool) -> None:
            self.observe_pause = observe_pause
            self.calls: list[tuple[str, dict, int | None]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload, timeout_ms))
            if name == "playmode.set":
                return ok(request_id, {})
            assert name == "resource.editorState"
            return ok(request_id, {
                "authoritative": True, "isPlaying": True, "isPaused": self.observe_pause,
                "isCompiling": False, "activeSceneName": "Acceptance",
            })

    paused = {"authoritative": True, "isStale": False, "isCompiling": False, "transition": "snapshot", "playModeState": "pause"}
    idempotent = StatusDomainService()
    idempotent.server = type("Server", (), {"state": _State(paused), "session_manager": _SessionManager()})()
    idempotent.dispatcher = _Dispatcher(observe_pause=True)
    already_paused = asyncio.run(idempotent.playmode_pause())
    assert already_paused.ok and already_paused.data["changed"] is False
    assert already_paused.data["writeCount"] == 0
    assert already_paused.data["commandSubmitted"] is False
    assert idempotent.dispatcher.calls == []

    playing = {**paused, "playModeState": "play"}
    timed_out = StatusDomainService()
    timed_out.server = type("Server", (), {"state": _State(playing), "session_manager": _SessionManager()})()
    timed_out.dispatcher = _Dispatcher(observe_pause=False)
    result = asyncio.run(timed_out.playmode_pause(timeout_ms=1))
    assert not result.ok and result.error.code == "PLAYMODE_PAUSE_TIMEOUT"
    assert result.error.detail["commandSubmitted"] is True
    assert result.error.detail["stateObserved"] is False
    assert result.error.detail["terminal"] is False
    assert timed_out.dispatcher.calls[0][:2] == ("playmode.set", {"action": "pause"})
    assert all((timeout_ms or 0) <= 1 for name, _payload, timeout_ms in timed_out.dispatcher.calls if name == "resource.editorState")


def test_playmode_pause_never_confirms_from_stale_or_unattributed_observation() -> None:
    class _State:
        @staticmethod
        def execution_state() -> dict:
            return {
                "authoritative": True, "isStale": False, "isCompiling": False,
                "transition": "snapshot", "playModeState": "play",
            }

        @staticmethod
        def update_editor_state(_payload: dict) -> bool:
            return True

    class _SessionManager:
        active = types.SimpleNamespace(session_id="session-pause", process_id=42)

        @staticmethod
        def is_connected() -> bool:
            return True

    class _Dispatcher:
        def __init__(self, observation: dict) -> None:
            self.observation = observation

        async def call(self, request_id: str, name: str, _payload: dict, timeout_ms: int | None = None):
            if name == "playmode.set":
                return ok(request_id, {})
            return ok(request_id, self.observation)

    base = {
        "isPlaying": True, "isPaused": True, "isCompiling": False,
        "activeSceneName": "Acceptance",
    }
    for observation in (
        {**base, "authoritative": True, "isStale": True},
        {**base, "authoritative": False, "isStale": False},
        {**base, "isStale": False},
    ):
        service = StatusDomainService()
        service.server = type("Server", (), {"state": _State(), "session_manager": _SessionManager()})()
        service.dispatcher = _Dispatcher(observation)
        result = asyncio.run(service.playmode_pause(timeout_ms=1))
        assert not result.ok and result.error.code == "PLAYMODE_PAUSE_TIMEOUT"
        assert result.error.detail["confirmed"] is False


def test_sceneview_maximized_requires_exact_instance_id_and_boolean_before_dispatch() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {"ok": True, "instanceId": payload["instanceId"], "maximized": payload["maximized"]})

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()

    empty = asyncio.run(service.sceneview_set_maximized("", True))
    string_id = asyncio.run(service.sceneview_set_maximized("42", True))
    boolean_id = asyncio.run(service.sceneview_set_maximized(True, True))
    invalid = asyncio.run(service.sceneview_set_maximized(42, 1))
    applied = asyncio.run(service.sceneview_set_maximized(42, False))
    numeric = asyncio.run(service.sceneview_set_maximized(43, True))
    negative = asyncio.run(service.sceneview_set_maximized(-1, True))

    assert not empty.ok and empty.error.code == "INVALID_PAYLOAD"
    assert not string_id.ok and string_id.error.code == "INVALID_PAYLOAD"
    assert not boolean_id.ok and boolean_id.error.code == "INVALID_PAYLOAD"
    assert not invalid.ok and invalid.error.code == "INVALID_PAYLOAD"
    assert not negative.ok and negative.error.code == "INVALID_PAYLOAD"
    assert applied.ok
    assert numeric.ok
    assert service.dispatcher.calls == [
        ("sceneview.setMaximized", {"instanceId": "42", "domainGeneration": "", "maximized": False}),
        ("sceneview.setMaximized", {"instanceId": "43", "domainGeneration": "", "maximized": True}),
    ]
    assert applied.data["confirmed"] is False
    assert applied.data["terminal"] is False
    assert applied.data["confirmationUnavailableReason"] == "FRESH_AUTHORITATIVE_SCENEVIEW_STATE_REQUIRED"


def test_sceneview_restore_precondition_is_forwarded_without_title_fallback() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {"ok": True})

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.sceneview_set_maximized(9, False, expected_current_maximized=True))

    assert result.ok
    assert service.dispatcher.calls == [(
        "sceneview.setMaximized",
        {"instanceId": "9", "domainGeneration": "", "maximized": False, "hasExpectedCurrentMaximized": True, "expectedCurrentMaximized": True},
    )]


def test_sceneview_domain_generation_is_forwarded_without_mutating_defaults() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {"ok": True})

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()

    result = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="44"))

    assert result.ok
    assert service.dispatcher.calls == [(
        "sceneview.setMaximized", {"instanceId": "9", "domainGeneration": "44", "maximized": False},
    )]


def test_sceneview_restore_token_is_bound_to_exact_instance_domain_and_prior_state() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {
                "ok": True, "commandId": request_id, "instanceId": payload["instanceId"],
                "domainGeneration": payload["domainGeneration"], "maximized": payload["maximized"],
                "changed": True, "writeCount": 1,
            })

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()
    changed = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="44"))
    token = changed.data["restoreToken"]

    wrong_domain = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="45", restore_token=token))
    restored = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="44", restore_token=token))
    replay = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="44", restore_token=token))

    assert not wrong_domain.ok and wrong_domain.error.code == "SCENEVIEW_RESTORE_TOKEN_MISMATCH"
    assert restored.ok
    assert service.dispatcher.calls[-1] == (
        "sceneview.setMaximized",
        {"instanceId": "9", "domainGeneration": "44", "maximized": False,
         "hasExpectedCurrentMaximized": True, "expectedCurrentMaximized": True},
    )
    assert replay.ok
    assert replay.data["commandId"] == restored.data["commandId"]
    assert replay.data["idempotentReplay"] is True
    assert replay.data["commandSubmitted"] is False
    assert replay.data["changed"] is False and replay.data["writeCount"] == 0
    assert len(service.dispatcher.calls) == 2

    restarted = StatusDomainService()
    restarted.dispatcher = _Dispatcher()
    after_restart = asyncio.run(restarted.sceneview_set_maximized(9, False, domain_generation="44", restore_token=token))
    assert not after_restart.ok and after_restart.error.code == "SCENEVIEW_RESTORE_TOKEN_INVALID"
    assert restarted.dispatcher.calls == []


def test_sceneview_wait_false_does_not_submit_a_second_action() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {"ok": True, "instanceId": payload["instanceId"], "maximized": payload["maximized"]})

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.sceneview_set_maximized(9, True, wait=False))

    assert result.ok
    assert result.data["commandSubmitted"] is True
    assert result.data["terminal"] is False
    assert result.data["confirmed"] is False
    assert len(service.dispatcher.calls) == 1


def test_sceneview_persistence_failure_reports_possible_side_effect_without_replay() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload))
            return ok(request_id, {
                "ok": False,
                "commandId": "sceneview-original-command",
                "changed": True,
                "writeCount": 1,
                "persistenceError": "state store write failed",
            })

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.sceneview_set_maximized(9, True))

    assert result.ok
    assert result.data["commandId"] == "sceneview-original-command"
    assert result.data["commandSubmitted"] is True
    assert result.data["sideEffectsMayHaveOccurred"] is True
    assert result.data["confirmed"] is False and result.data["terminal"] is False
    assert result.data["persistenceError"] == "state store write failed"
    assert len(service.dispatcher.calls) == 1


def test_editor_window_history_validates_bounds_before_dispatch_and_preserves_cursor() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {
                "events": [], "earliestSequence": 12, "truncated": True,
                "gap": {"reason": "domain_reload", "afterSequence": 9},
            })

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()

    assert not asyncio.run(service.editor_window_history(after_sequence=-1)).ok
    assert not asyncio.run(service.editor_window_history(count=513)).ok
    assert not asyncio.run(service.editor_window_history(after_sequence=True)).ok
    assert not asyncio.run(service.editor_window_history(instance_id=42)).ok
    accepted = asyncio.run(service.editor_window_history("42", after_sequence=9, count=2))

    assert accepted.ok
    assert accepted.data == {
        "events": [], "earliestSequence": 12, "truncated": True,
        "gap": {"reason": "domain_reload", "afterSequence": 9},
    }
    assert service.dispatcher.calls == [(
        "editor.window.history", {"instanceId": "42", "afterSequence": 9, "count": 2},
    )]


def test_safe_window_open_and_focus_validate_before_dispatch() -> None:
    class _Dispatcher:
        calls: list[tuple[str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {"ok": True})

    service = StatusDomainService()
    service.dispatcher = _Dispatcher()

    assert not asyncio.run(service.editor_window_open("")).ok
    assert not asyncio.run(service.editor_window_focus("", "48")).ok
    assert not asyncio.run(service.editor_window_focus("42", 48)).ok
    assert asyncio.run(service.editor_window_open("CodingRiver.UPilot.UPilotSafeWindowProbe")).ok
    assert asyncio.run(service.editor_window_focus("42", "48")).ok
    assert service.dispatcher.calls == [
        ("editor.window.open", {"typeName": "CodingRiver.UPilot.UPilotSafeWindowProbe"}),
        ("editor.window.focus", {"instanceId": "42", "domainGeneration": "48"}),
    ]


def test_sync_existing_compile_is_a_success_intermediate_state_without_failure_signature() -> None:
    source = (Path(__file__).parents[1] / "src" / "upilot_mcp" / "domain" / "resource_service.py").read_text(
        encoding="utf-8"
    )
    busy_branch = source.split('if code == "EDITOR_BUSY":', 1)[1].split("return ok(request_id, payload)", 1)[0]

    assert '"attachedToExistingCompile": True' in busy_branch
    assert '"failureSignature"' not in busy_branch
    assert '"compileError"' not in busy_branch


def test_safe_compile_verifies_persistent_errors_after_transient_wait_error() -> None:
    from types import SimpleNamespace

    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    _authoritative_editor_state(state)
    state.compile.status = "finished"
    state.compile.phase = "completed"
    state.compile.compile_request_id = "req-transient"
    state.compile.command_queued_at = 1
    state.compile.unity_accepted_at = 2
    state.compile.started_at = 3
    state.compile.finished_at = 4
    state.compile.last_progress_at = 4

    service = CompileDomainService()
    service.server = SimpleNamespace(
        state=state,
        is_ready=lambda: True,
    )

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isCompiling": False})

    service.dispatcher = _Dispatcher()

    async def _compile() -> ToolResponse:
        return ok("req-compile", {"compileRequestId": "req-transient"})

    async def _compile_wait(**_kwargs) -> ToolResponse:
        return fail(
            "req-wait",
            "COMPILE_ERROR",
            "transient pre-reload error state",
            {"elapsedS": 1.25},
        )

    async def _compile_errors(_compile_request_id: str = "") -> ToolResponse:
        state.compile.terminal = True
        state.compile.errors_verified = True
        return ok("req-errors", {"total": 0, "errors": [], "source": "live"})

    service.compile = _compile
    service.compile_wait = _compile_wait
    service.compile_errors = _compile_errors

    result = asyncio.run(
        service.safe_compile_and_wait(timeout_s=30, post_compile_delay_s=0)
    )

    assert result.ok
    assert result.data["waitReportedCompileError"] is True
    assert result.data["errorsVerified"] is True
    assert result.data["errorTotal"] == 0


def test_safe_compile_resumes_wait_after_domain_reload_disconnect() -> None:
    from types import SimpleNamespace

    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    _authoritative_editor_state(state)
    state.compile.phase = "domain_reload"
    state.compile.compile_request_id = "req-reload"
    service = CompileDomainService()
    session = SimpleNamespace(session_id="session-before-reload")
    service.server = SimpleNamespace(
        state=state,
        is_ready=lambda: True,
        session_manager=SimpleNamespace(active=session),
    )

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isCompiling": False})

    service.dispatcher = _Dispatcher()
    waits = 0

    compile_calls = 0

    async def _compile() -> ToolResponse:
        nonlocal compile_calls
        compile_calls += 1
        return ok("req-compile", {"compileRequestId": "req-reload"})

    async def _compile_wait(**_kwargs) -> ToolResponse:
        nonlocal waits
        waits += 1
        if waits == 1:
            return fail("req-wait", "UNITY_NOT_CONNECTED", "domain reload")
        state.compile.phase = "completed"
        session.session_id = "session-after-reload"
        return ok("req-wait-2", {"status": "completed", "elapsedS": 0.1})

    async def _compile_errors(_compile_request_id: str = "") -> ToolResponse:
        state.compile.terminal = True
        state.compile.errors_verified = True
        return ok("req-errors", {"total": 0, "errors": [], "source": "persistent"})

    service.compile = _compile
    service.compile_wait = _compile_wait
    service.compile_errors = _compile_errors

    result = asyncio.run(service.safe_compile_and_wait(timeout_s=5, post_compile_delay_s=0))

    assert result.ok
    assert compile_calls == 0
    assert waits == 2
    assert result.data["attachedToExistingCompile"] is True
    assert result.data["compileRequestId"] == "req-reload"
    assert result.data["waitInterruptedByReload"] is True
    assert result.data["reconnectedAfterReload"] is True
    assert result.data["sessionChangedDuringCompile"] is True
    assert result.data["initialSessionId"] == "session-before-reload"
    assert result.data["finalSessionId"] == "session-after-reload"
    assert result.data["errorsVerified"] is True


def test_safe_compile_preserves_explicit_invocation_compile_identity() -> None:
    from types import SimpleNamespace

    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    state.compile.status = "finished"
    state.compile.phase = "completed"
    state.compile.compile_request_id = "req-snapshot"
    state.compile.initial_session_id = "session-before"
    state.compile.finished_at = 100
    state.editor.connected = True
    state.editor.authoritative = False
    state.editor.play_mode_state = "edit"
    service = CompileDomainService()
    service.server = SimpleNamespace(
        state=state,
        is_ready=lambda: True,
        session_manager=SimpleNamespace(active=SimpleNamespace(session_id="session-after")),
    )

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            raise AssertionError("explicit attachment must not re-query before preserving identity")

    service.dispatcher = _Dispatcher()

    async def _compile() -> ToolResponse:
        raise AssertionError("explicit attachment must not start a second compile")

    async def _compile_wait(**_kwargs) -> ToolResponse:
        return ok("req-wait", {"status": "completed", "elapsedS": 0.1})

    async def _compile_errors(compile_request_id: str = "") -> ToolResponse:
        assert compile_request_id == "req-snapshot"
        state.compile.terminal = True
        state.compile.errors_verified = True
        return ok("req-errors", {"total": 0, "errors": [], "source": "persistent"})

    service.compile = _compile
    service.compile_wait = _compile_wait
    service.compile_errors = _compile_errors

    result = asyncio.run(
        service.safe_compile_and_wait(
            timeout_s=5,
            post_compile_delay_s=0,
            attach_compile_request_id="req-snapshot",
        )
    )

    assert result.ok
    assert result.data["attachedToExistingCompile"] is True
    assert result.data["compileRequestId"] == "req-snapshot"
    assert result.data["initialSessionId"] == "session-before"
    assert result.data["finalSessionId"] == "session-after"
    assert result.data["sessionChangedDuringCompile"] is True
    assert result.data["reconnectedAfterReload"] is True


def test_project_config_hot_reloads_write_access_and_reports_restart_fields(tmp_path: Path, monkeypatch) -> None:
    from upilot_mcp import config as config_module

    config_path = tmp_path / "config.json"
    config_path.write_text(
        '{"mcp":{"httpPort":8011,"wsPort":8765},"safety":{"writeAccessApproved":false}}',
        encoding="utf-8",
    )
    monkeypatch.setenv("UPILOT_CONFIG", str(config_path))
    original = {
        name: getattr(config_module.CONFIG, name)
        for name in config_module.CONFIG.__slots__
    }
    try:
        first = config_module.refresh_config_if_changed(force=True)
        assert config_module.CONFIG.write_access_approved is False
        assert config_module.CONFIG.unsaved_scene_policy == "block"
        assert first["restartRequired"] is False

        config_path.write_text(
            '{"mcp":{"httpPort":8011,"wsPort":8765},"safety":{"writeAccessApproved":true,"unsavedScenePolicy":"autoSave"}}',
            encoding="utf-8",
        )
        second = config_module.refresh_config_if_changed()
        assert config_module.CONFIG.write_access_approved is True
        assert config_module.CONFIG.unsaved_scene_policy == "autoSave"
        assert second["writeAccessApproved"] is True
        assert second["unsavedScenePolicy"] == "autoSave"
        assert second["diskConfigChanged"] is False
        assert second["diskConfigHash"] != first["diskConfigHash"]

        config_path.write_text(
            '{"mcp":{"httpPort":8999,"wsPort":8765},"safety":{"writeAccessApproved":true}}',
            encoding="utf-8",
        )
        third = config_module.refresh_config_if_changed()
        assert third["restartRequired"] is True
        assert third["diskConfigChanged"] is True
        assert third["restartFields"] == ["http_port"]
        assert config_module.CONFIG.http_port == 8011
    finally:
        for name, value in original.items():
            setattr(config_module.CONFIG, name, value)
        config_module.refresh_config_if_changed(force=True)


def test_long_reflection_call_returns_single_pollable_operation_without_reexecution() -> None:
    from upilot_mcp.domain.reflection_service import ReflectionDomainService

    calls = 0

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None) -> ToolResponse:
            nonlocal calls
            calls += 1
            assert name == "reflection.call"
            assert timeout_ms == 30000
            await asyncio.sleep(0.03)
            return ok(request_id, {"result": "done"})

    async def scenario():
        service = ReflectionDomainService()
        service.dispatcher = _Dispatcher()
        started = await service.reflection_call(
            "Fixture",
            "LongCall",
            async_after_sec=0.001,
            operation_timeout_sec=5,
        )
        assert started.ok
        operation_id = started.data["operationId"]
        assert started.data["status"] == "Running"
        assert started.data["executedOnce"] is True

        waited = await service.reflection_operation_wait(operation_id, timeout_sec=1, poll_interval_sec=0.005)
        assert waited.ok
        assert waited.data["status"] == "Succeeded"
        assert waited.data["result"]["result"] == "done"

    asyncio.run(scenario())
    assert calls == 1


def test_failed_reflection_operation_preserves_bridge_exception_evidence_at_top_level() -> None:
    from upilot_mcp.domain.reflection_service import ReflectionDomainService

    calls = 0

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None) -> ToolResponse:
            nonlocal calls
            calls += 1
            return fail(request_id, "REFLECTION_CALL_FAILED", "P0_TARGET_THROW", {
                "stage": "runtime",
                "sideEffectsMayHaveOccurred": True,
                "exceptionType": "System.InvalidOperationException",
                "exceptionMessage": "P0_TARGET_THROW",
                "stackTrace": "Fixture.MutateThenThrow()",
                "commandId": "cmd-p0-target",
            })

    async def scenario():
        service = ReflectionDomainService()
        service.dispatcher = _Dispatcher()
        started = await service.reflection_call("Fixture", "MutateThenThrow", force_async=True)
        result = await service.reflection_operation_wait(
            started.data["operationId"], timeout_sec=1, poll_interval_sec=0.005,
        )
        assert not result.ok
        assert result.error.detail["sideEffectsMayHaveOccurred"] is True
        assert result.error.detail["exceptionType"] == "System.InvalidOperationException"
        assert result.error.detail["exceptionMessage"] == "P0_TARGET_THROW"
        assert "MutateThenThrow" in result.error.detail["stackTrace"]
        assert result.error.detail["commandId"] == "cmd-p0-target"
        assert result.error.detail["resultError"]["sideEffectsMayHaveOccurred"] is True

    asyncio.run(scenario())
    assert calls == 1


def test_running_reflection_operation_rejects_unsafe_cancel_explicitly() -> None:
    from upilot_mcp.domain.reflection_service import ReflectionDomainService

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None) -> ToolResponse:
            await asyncio.sleep(0.1)
            return ok(request_id, {"result": "done"})

    async def scenario():
        service = ReflectionDomainService()
        service.dispatcher = _Dispatcher()
        started = await service.reflection_call("Fixture", "LongCall", force_async=True)
        canceled = await service.reflection_operation_cancel(started.data["operationId"])
        assert canceled.ok is False
        assert canceled.error.code == "CANCEL_UNSUPPORTED"
        assert canceled.error.detail["executionMayContinue"] is True
        await service.reflection_operation_wait(started.data["operationId"], timeout_sec=1, poll_interval_sec=0.01)

    asyncio.run(scenario())


def test_prefab_query_components_tool_is_read_only_in_source_registry() -> None:
    tools_path = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_tools" / "resource_tools.py"
    text = tools_path.read_text(encoding="utf-8")
    assert "async def unity_prefab_query_components" in text
    assert '"unity_prefab_query_components"' not in text.split("_DESTRUCTIVE_TOOLS", 1)[1].split("}", 1)[0]


def test_public_tool_route_and_unknown_tool_are_real_failures(register_test_tool) -> None:
    register_test_tool("unity_contract_echo", facade_method="echo", category="test")
    facade = _Facade()
    result = asyncio.run(dispatch_public_tool(facade, "unity_contract_echo", {"value": "ok"}))
    missing = asyncio.run(dispatch_public_tool(facade, "unity_contract_missing", {}))

    assert result.ok and result.data == {"value": "ok"}
    assert not missing.ok
    assert missing.error and missing.error.code == "UNKNOWN_TOOL"


def test_generic_tool_call_routes_registered_tools_and_blocks_recursion(register_test_tool) -> None:
    register_test_tool("unity_contract_proxy_echo", facade_method="echo", category="test")
    service = StatusDomainService.__new__(StatusDomainService)
    service.echo = _Facade().echo

    result = asyncio.run(service.tool_call("unity_contract_proxy_echo", {"value": "proxied"}))
    recursive = asyncio.run(service.tool_call("unity_tool_call", {}))

    assert result.ok and result.data == {"value": "proxied"}
    assert not recursive.ok
    assert recursive.error and recursive.error.code == "RECURSIVE_TOOL_CALL"


def test_generic_tool_call_maps_public_camel_case_by_target_signature(register_test_tool) -> None:
    class _ProxyFacade:
        async def capture(
            self,
            exclude_upilot: bool = True,
            include_stack_trace: bool = False,
            test_filter: str = "",
            timeout_sec: int = 0,
            asset_path: str = "",
        ) -> ToolResponse:
            return ok(
                "proxy",
                {
                    "excludeUPilot": exclude_upilot,
                    "includeStackTrace": include_stack_trace,
                    "testFilter": test_filter,
                    "timeoutSec": timeout_sec,
                    "assetPath": asset_path,
                },
            )

    register_test_tool(
        "unity_contract_proxy_arguments", facade_method="capture", category="test"
    )
    service = StatusDomainService.__new__(StatusDomainService)
    service.capture = _ProxyFacade().capture

    result = asyncio.run(
        service.tool_call(
            "unity_contract_proxy_arguments",
            {
                "excludeUPilot": False,
                "includeStackTrace": True,
                "testFilter": "Example.Tests",
                "timeoutSec": 45,
                "assetPath": "Assets/Example.prefab",
            },
        )
    )

    assert result.ok
    assert result.data == {
        "excludeUPilot": False,
        "includeStackTrace": True,
        "testFilter": "Example.Tests",
        "timeoutSec": 45,
        "assetPath": "Assets/Example.prefab",
    }


def test_generic_tool_call_rejects_unknown_arguments_with_schema(register_test_tool) -> None:
    register_test_tool(
        "unity_contract_proxy_schema", facade_method="echo", category="test"
    )
    service = StatusDomainService.__new__(StatusDomainService)
    service.echo = _Facade().echo

    result = asyncio.run(
        service.tool_call("unity_contract_proxy_schema", {"unexpectedField": 1})
    )

    assert not result.ok
    assert result.error and result.error.code == "INVALID_TOOL_ARGUMENTS"
    assert result.error.detail["unknownArguments"] == ["unexpectedField"]
    assert result.error.detail["expectedArguments"][0]["name"] == "value"


def test_tools_find_exposes_proxy_argument_schema(register_test_tool) -> None:
    register_test_tool(
        "unity_contract_proxy_find", facade_method="echo", category="test"
    )
    service = StatusDomainService.__new__(StatusDomainService)
    service.echo = _Facade().echo
    service.server = type(
        "Server",
        (),
        {
            "session_manager": type("Sessions", (), {"is_connected": lambda self: True})(),
            "is_ready": lambda self: True,
        },
    )()

    result = asyncio.run(service.tools_find(query="unity_contract_proxy_find"))

    assert result.ok
    assert result.data["exactMatch"] is True
    schema = result.data["exactMatches"][0]["proxyArguments"]
    assert schema == [
        {"name": "value", "camelCase": "value", "required": False, "default": ""}
    ]


def test_console_search_query_alias_is_forwarded_strictly() -> None:
    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "console.logs.search"
            assert payload["query"] == "DeliveryPoint"
            assert payload["count"] == 50
            return ok(request_id, {"logs": [], "effectiveQuery": payload["query"]})

    service = StatusDomainService.__new__(StatusDomainService)
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.console_search_logs(query="DeliveryPoint", count=50))

    assert result.ok
    assert result.data["effectiveQuery"] == "DeliveryPoint"


def test_public_tool_route_rejects_destructive_tools_in_safe_mode(register_test_tool) -> None:
    register_test_tool(
        "unity_contract_write",
        facade_method="echo",
        category="test",
        destructive=True,
    )
    facade = _Facade()
    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", False)
    try:
        result = asyncio.run(dispatch_public_tool(facade, "unity_contract_write", {"value": "nope"}))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert not result.ok
    assert result.error and result.error.code == "WRITE_ACCESS_NOT_APPROVED"


def test_structured_error_sets_is_error() -> None:
    from upilot_mcp.mcp_stdio_server import _payload

    result = _payload(fail("req-1", "EXPECTED", "failed"))
    assert result.isError is True
    assert result.structuredContent["schemaVersion"] == 2
    assert result.structuredContent["ok"] is False
    assert result.structuredContent["error"]["code"] == "EXPECTED"


def test_dispatcher_preserves_bridge_timing_and_round_trip() -> None:
    transport = _Transport({
        "type": "result",
        "payload": {"ok": True},
        "timing": {
            "queueMs": 3,
            "bridgeMs": 8,
            "unityExecutionMs": 4,
            "serializationMs": 1,
        },
    })
    dispatcher = CommandDispatcher(transport, StateStore())
    result = asyncio.run(dispatcher.call("req-1", "test.command", {}))

    assert result.ok
    assert result.timing["queueMs"] == 3
    assert result.timing["bridgeMs"] == 8
    assert result.timing["unityExecutionMs"] == 4
    assert result.timing["serializationMs"] == 1
    assert result.timing["roundTripMs"] >= 0


def test_sync_after_disk_write_attaches_to_active_compile_without_duplicate_refresh() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    class _CompileState:
        status = "compiling"
        phase = "compiling"
        error_count = 0
        warning_count = 0
        command_queued_at = 100
        unity_accepted_at = 110
        started_at = 123
        finished_at = 0
        last_progress_at = 123
        compile_request_id = "compile-1"

    class _State:
        compile = _CompileState()

    class _Server:
        state = _State()

    async def _asset_refresh() -> ToolResponse:
        return ok("req-refresh", {"ok": True, "status": "ok"})

    async def _compile() -> ToolResponse:
        return fail("req-compile", "EDITOR_BUSY", "编译进行中，请稍后重试")

    service.server = _Server()
    service.asset_refresh = _asset_refresh
    service.compile = _compile

    result = asyncio.run(service.sync_after_disk_write(delay_s=0, trigger_compile=True))
    assert result.ok is True
    assert result.data["status"] == "compiling"
    assert result.data["refreshed"] is False
    assert result.data["refreshSkipped"] is True
    assert result.data["refreshSkipReason"] == "compile_already_active"
    assert result.data["compileAlreadyRunning"] is True
    assert result.data["attachedToExistingCompile"] is True
    assert result.data["nextAction"] == "unity_safe_compile_and_wait"


def test_sync_after_disk_write_confirms_editor_idle_before_reporting_complete() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    class _CompileState:
        status = "finished"
        phase = "completed"
        error_count = 0
        warning_count = 0
        command_queued_at = 100
        unity_accepted_at = 200
        started_at = 300
        finished_at = 400
        last_progress_at = 400
        compile_request_id = "compile-2"

    class _EditorState:
        is_compiling = False

    class _State:
        compile = _CompileState()
        editor = _EditorState()

    class _Server:
        state = _State()

    async def _asset_refresh() -> ToolResponse:
        return ok("req-refresh", {"ok": True, "status": "ok"})

    async def _compile() -> ToolResponse:
        return ok("req-compile", {"accepted": True, "compileRequestId": "compile-2"})

    async def _compile_wait(**_: object) -> ToolResponse:
        return ok("req-wait", {"status": "ready", "isCompiling": False})

    service.server = _Server()
    service.asset_refresh = _asset_refresh
    service.compile = _compile
    service.compile_wait = _compile_wait

    result = asyncio.run(service.sync_after_disk_write(delay_s=0, trigger_compile=True))
    assert result.ok is True
    assert result.data["status"] == "compiled"
    assert result.data["compiled"] is True
    assert result.data["compileCompleted"] is True
    assert result.data["compileState"]["phase"] == "completed"
    assert result.data["compileState"]["finishedAt"] == 400


def test_sync_after_disk_write_does_not_report_false_compile_completion() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    class _CompileState:
        status = "accepted"
        phase = "accepted"
        error_count = 0
        warning_count = 0
        command_queued_at = 100
        unity_accepted_at = 200
        started_at = 300
        finished_at = 0
        last_progress_at = 300
        compile_request_id = "compile-3"

    class _EditorState:
        is_compiling = True

    class _State:
        compile = _CompileState()
        editor = _EditorState()

    class _Server:
        state = _State()

    async def _asset_refresh() -> ToolResponse:
        return ok("req-refresh", {"ok": True, "status": "ok"})

    async def _compile() -> ToolResponse:
        return ok("req-compile", {"accepted": True, "compileRequestId": "compile-3"})

    async def _compile_wait(**_: object) -> ToolResponse:
        return ok("req-wait", {"status": "timeout", "isCompiling": True})

    service.server = _Server()
    service.asset_refresh = _asset_refresh
    service.compile = _compile
    service.compile_wait = _compile_wait

    result = asyncio.run(service.sync_after_disk_write(delay_s=0, trigger_compile=True))
    assert result.ok is True
    assert result.data["status"] == "compiling"
    assert result.data["compiled"] is False
    assert result.data["compileCompleted"] is False
    assert result.data["nextAction"] == "unity_safe_compile_and_wait"
    assert result.data["attachedToExistingCompile"] is True
    assert result.data["compileState"]["phase"] == "accepted"


def test_compile_status_acceptance_does_not_become_false_finished() -> None:
    from upilot_mcp.state_store import StateStore

    state = StateStore()
    state.compile.status = "queued"
    state.update_compile_status({"requestId": "compile-4", "status": "accepted"})

    assert state.compile.status == "accepted"
    assert state.compile.phase == "accepted"
    assert state.editor.is_compiling is True


def test_compile_terminal_status_synthesizes_missing_finished_timestamp() -> None:
    from upilot_mcp.state_store import StateStore

    state = StateStore()
    state.update_compile_status(
        {
            "requestId": "compile-terminal",
            "status": "finished",
            "startedAt": 100,
            "finishedAt": 0,
        }
    )

    assert state.compile.status == "finished"
    assert state.compile.phase == "completed"
    assert state.compile.finished_at >= state.compile.started_at


def test_new_compile_request_does_not_reuse_previous_finished_timestamp() -> None:
    from upilot_mcp.state_store import StateStore

    state = StateStore()
    state.update_compile_status(
        {
            "requestId": "compile-first",
            "status": "finished",
            "startedAt": 100,
            "finishedAt": 200,
        }
    )
    state.update_compile_status(
        {
            "requestId": "compile-second",
            "status": "started",
            "startedAt": 300,
        }
    )

    assert state.compile.compile_request_id == "compile-second"
    assert state.compile.started_at == 300
    assert state.compile.finished_at == 0

    state.update_compile_status(
        {
            "requestId": "compile-second",
            "status": "finished",
            "startedAt": 300,
            "finishedAt": 0,
        }
    )

    assert state.compile.finished_at >= state.compile.started_at


def test_compile_result_does_not_overwrite_completed_phase_with_accepted() -> None:
    from upilot_mcp.domain.compile_service import CompileDomainService

    service = CompileDomainService.__new__(CompileDomainService)

    state = StateStore()
    _authoritative_editor_state(state)

    class _Server:
        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        async def call(
            self,
            request_id: str,
            name: str,
            payload: dict,
            **__: object,
        ) -> ToolResponse:
            if name == "resource.editorState":
                return ok(request_id, {"isCompiling": False})
            service.server.state.compile.status = "finished"
            service.server.state.compile.phase = "completed"
            service.server.state.compile.finished_at = 456
            return ok("req-compile", {"accepted": True, "compileRequestId": "compile-5"})

    service.server = _Server()
    service.dispatcher = _Dispatcher()

    result = asyncio.run(service.compile())
    assert result.ok is True
    assert result.data["triggerMode"] == "incremental"
    assert result.data["requestIssued"] is True
    assert result.data["cleanBuildCache"] is False
    assert result.data["attachedToExistingCompile"] is False
    assert service.server.state.compile.status == "finished"
    assert service.server.state.compile.phase == "completed"


def test_screenshot_editor_window_is_strict_snapshot_wrapper() -> None:
    from upilot_mcp.domain.screenshot_service import ScreenshotDomainService

    async def _windows(**_: object) -> ToolResponse:
        return ok("req-windows", {"windows": [{
            "instanceId": 42,
            "title": "资源审计中心",
            "fullTypeName": "Example.AssetAuditWindow",
            "width": 900,
            "height": 640,
            "hasFocus": True,
        }]})

    async def _capture(source: str, target: dict) -> ToolResponse:
        assert source == "editorWindow"
        assert target["instanceId"] == "42"
        assert target["requireContentRect"] is True
        return ok("req-snapshot", {"source": source, "snapshot": {"snapshotId": "snapshot-1"}})

    service = ScreenshotDomainService.__new__(ScreenshotDomainService)
    service.editor_windows_list = _windows
    service._capture_snapshot_screenshot = _capture

    result = asyncio.run(service.screenshot_editor_window("资源审计中心", degrade="auto"))
    assert result.ok is True
    assert result.data["source"] == "editorWindow"
    assert result.data["snapshot"]["snapshotId"] == "snapshot-1"


def test_screenshot_save_preserves_scene_view_repaint_evidence(tmp_path: Path) -> None:
    from upilot_mcp.domain.screenshot_service import ScreenshotDomainService

    evidence = {
        "repaintObservedAtUtcMs": 1234,
        "repaintSequence": 7,
        "includesSceneGui": True,
        "includesHandles": True,
        "matchedFullTypeName": "UnityEditor.SceneView",
        "matchedInstanceId": 99,
    }

    async def _scene_view(*_: object, **__: object) -> ToolResponse:
        return ok("req-scene", {
            "source": "sceneView",
            "snapshot": {"snapshotId": "snapshot-scene"},
            "imageData": base64.b64encode(b"snapshot-png-bytes").decode("ascii"),
            **evidence,
        })

    service = ScreenshotDomainService.__new__(ScreenshotDomainService)
    service._resolve_screenshot_save_path = lambda *_: tmp_path / "scene-view.png"
    service.screenshot_scene_view = _scene_view

    async def save_inside_task():
        # This test covers artifact forwarding inside the task. The public short-wait
        # entry point is covered separately by test_snapshot_short_tasks.
        from upilot_mcp.operation_context import TASK_TOOL
        token = TASK_TOOL.set(True)
        try:
            return await service.screenshot_save(source="sceneView")
        finally:
            TASK_TOOL.reset(token)
    result = asyncio.run(save_inside_task())
    assert result.ok is True
    for key, value in evidence.items():
        assert result.data[key] == value
    assert result.data["savedBy"] == "snapshot_wrapper"
    assert (tmp_path / "scene-view.png").read_bytes() == b"snapshot-png-bytes"


def test_png_pixel_stats_and_compare_are_structured(tmp_path: Path) -> None:
    from PIL import Image
    from upilot_mcp.domain.screenshot_service import ScreenshotDomainService

    baseline = tmp_path / "before.png"
    candidate = tmp_path / "after.png"
    Image.new("RGBA", (2, 2), (0, 0, 0, 255)).save(baseline)
    image = Image.new("RGBA", (2, 2), (0, 0, 0, 255))
    image.putpixel((1, 1), (255, 255, 255, 255))
    image.save(candidate)

    class _Session:
        project_path = str(tmp_path)

    class _SessionManager:
        active = _Session()

    class _Server:
        session_manager = _SessionManager()

    service = ScreenshotDomainService.__new__(ScreenshotDomainService)
    service.server = _Server()

    stats = asyncio.run(service.screenshot_pixel_stats("before.png", near_black_threshold=16))
    comparison = asyncio.run(service.screenshot_compare("before.png", "after.png"))

    assert stats.ok and stats.data["nearBlackRatio"] == 1.0
    assert comparison.ok
    assert comparison.data["differentPixelRatio"] == 0.25
    assert comparison.data["candidateNearBlackRatio"] == 0.75


def test_verify_window_uses_editor_window_list_as_truth() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    service = TestDomainService.__new__(TestDomainService)

    async def _compile_wait(self, **_: object) -> ToolResponse:
        return ok("req-compile-wait", {"status": "ready"})

    async def _editor_windows_list(self, type_filter: str = "", title_filter: str = "") -> ToolResponse:
        if title_filter == "资源审计中心":
            return ok(
                "req-windows",
                {
                    "windows": [
                        {
                            "title": "资源审计中心",
                            "typeName": "AssetAuditWindow",
                            "fullTypeName": "AUnityLocal.Editor.AssetAudit.AssetAuditWindow",
                            "instanceId": 42,
                            "posX": 1,
                            "posY": 2,
                            "width": 900,
                            "height": 640,
                            "docked": False,
                            "hasFocus": True,
                            "hasUIToolkit": True,
                        }
                    ],
                },
            )
        return ok("req-windows-empty", {"windows": []})

    async def _resource_window_diagnostics(self) -> ToolResponse:
        return ok("req-legacy", {"windowOpen": False})

    async def _resource_console_summary(self) -> ToolResponse:
        return ok("req-console", {"errorCount": 0})

    async def _screenshot_editor_window(self, window_title: str, degrade: str | None = None, **_: object) -> ToolResponse:
        return ok("req-screenshot", {"source": "editorWindow", "width": 900, "height": 640})

    service.compile_wait = types.MethodType(_compile_wait, service)
    service.editor_windows_list = types.MethodType(_editor_windows_list, service)
    service.resource_window_diagnostics = types.MethodType(_resource_window_diagnostics, service)
    service.resource_console_summary = types.MethodType(_resource_console_summary, service)
    service.screenshot_editor_window = types.MethodType(_screenshot_editor_window, service)

    result = asyncio.run(service.verify_window("资源审计中心"))
    assert result.ok is True
    assert result.data["windowMatch"]["windowOpen"] is True
    assert result.data["windowMatch"]["matchedFullTypeName"] == "AUnityLocal.Editor.AssetAudit.AssetAuditWindow"
    assert result.data["windowDiagnostics"]["windowOpen"] is False
    assert result.data["legacyWindowDiagnostics"]["windowOpen"] is False
    assert result.data["screenshot"]["source"] == "editorWindow"


def test_verify_window_reuses_exact_identity_and_default_title_does_not_constrain_it() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    service = TestDomainService.__new__(TestDomainService)
    list_calls: list[dict] = []
    screenshot_calls: list[dict] = []

    async def _compile_wait(self, **_: object) -> ToolResponse:
        return ok("req-compile-wait", {"status": "ready"})

    async def _editor_windows_list(self, type_filter: str = "", title_filter: str = "") -> ToolResponse:
        list_calls.append({"typeFilter": type_filter, "titleFilter": title_filter})
        return ok("req-windows", {"windows": [{
            "title": "Owned Probe",
            "typeName": "UPilotSafeWindowProbe",
            "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
            "instanceId": "42",
            "domainGeneration": "7",
            "width": 900,
            "height": 640,
        }]})

    async def _resource_window_diagnostics(self) -> ToolResponse:
        return ok("req-diagnostics", {})

    async def _resource_console_summary(self) -> ToolResponse:
        return ok("req-console", {})

    async def _screenshot_editor_window(self, **kwargs: object) -> ToolResponse:
        screenshot_calls.append(kwargs)
        return ok("req-screenshot", {"source": "editorWindow"})

    service.compile_wait = types.MethodType(_compile_wait, service)
    service.editor_windows_list = types.MethodType(_editor_windows_list, service)
    service.resource_window_diagnostics = types.MethodType(_resource_window_diagnostics, service)
    service.resource_console_summary = types.MethodType(_resource_console_summary, service)
    service.screenshot_editor_window = types.MethodType(_screenshot_editor_window, service)

    result = asyncio.run(service.verify_window(instance_id="42"))

    assert result.ok is True
    assert result.data["windowMatch"]["windowOpen"] is True
    assert result.data["windowMatch"]["identityScope"] == "currentDomain"
    assert list_calls == [{"typeFilter": "", "titleFilter": ""}]
    assert screenshot_calls == [{
        "window_title": "Owned Probe",
        "degrade": "",
        "instance_id": "42",
        "domain_generation": "7",
        "full_type_name": "CodingRiver.UPilot.UPilotSafeWindowProbe",
    }]


def test_verify_window_rejects_explicit_identity_conflict_without_screenshot_dispatch() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    service = TestDomainService.__new__(TestDomainService)
    screenshot_calls = 0

    async def _compile_wait(self, **_: object) -> ToolResponse:
        return ok("req-compile-wait", {"status": "ready"})

    async def _editor_windows_list(self, **_: object) -> ToolResponse:
        return ok("req-windows", {"windows": [{
            "title": "Owned Probe",
            "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
            "instanceId": "42",
            "domainGeneration": "7",
        }]})

    async def _resource_window_diagnostics(self) -> ToolResponse:
        return ok("req-diagnostics", {})

    async def _resource_console_summary(self) -> ToolResponse:
        return ok("req-console", {})

    async def _screenshot_editor_window(self, **_: object) -> ToolResponse:
        nonlocal screenshot_calls
        screenshot_calls += 1
        return ok("req-screenshot", {})

    service.compile_wait = types.MethodType(_compile_wait, service)
    service.editor_windows_list = types.MethodType(_editor_windows_list, service)
    service.resource_window_diagnostics = types.MethodType(_resource_window_diagnostics, service)
    service.resource_console_summary = types.MethodType(_resource_console_summary, service)
    service.screenshot_editor_window = types.MethodType(_screenshot_editor_window, service)

    result = asyncio.run(service.verify_window(window_title="Different", instance_id="42"))

    assert result.ok is True
    assert result.data["windowMatch"]["windowOpen"] is False
    assert result.data["windowMatch"]["code"] == "EDITORWINDOW_TITLE_MISMATCH"
    assert result.data["screenshot"]["sideEffectsMayHaveOccurred"] is False
    assert screenshot_calls == 0

    type_constrained = asyncio.run(service.verify_window(
        window_title="Different",
        full_type_name="CodingRiver.UPilot.UPilotSafeWindowProbe",
    ))
    assert type_constrained.data["windowMatch"]["windowOpen"] is False
    assert type_constrained.data["windowMatch"]["code"] == "WINDOW_NOT_FOUND"
    assert screenshot_calls == 0


def test_editor_state_tracks_freshness_timestamp() -> None:
    state = StateStore()
    assert state.editor.updated_at == 0
    state.update_editor_state({"connected": True, "activeScene": "Launch"})
    assert state.editor.connected is True
    assert state.editor.active_scene == "Launch"
    assert state.editor.updated_at > 0


def test_editor_window_set_rect_requires_one_target_and_forwards_exact_instance_id() -> None:
    from upilot_mcp.domain.status_service import StatusDomainService

    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, _request_id: str, command: str, payload: dict) -> ToolResponse:
            self.calls.append((command, payload))
            return ok("req-window", {"ok": True})

    service = StatusDomainService.__new__(StatusDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    rejected = asyncio.run(service.editor_window_set_rect(x=1, y=2, width=3, height=4))
    assert rejected.ok is False
    assert rejected.error is not None
    assert rejected.error.code == "INVALID_PAYLOAD"
    assert dispatcher.calls == []

    accepted = asyncio.run(
        service.editor_window_set_rect(instance_id="42", x=1, y=2, width=3, height=4)
    )
    assert accepted.ok is True
    assert dispatcher.calls == [
        (
            "editor.window.setRect",
            {"windowTitle": "", "matchMode": "exact", "instanceId": "42", "domainGeneration": "", "fullTypeName": "", "x": 1, "y": 2, "width": 3, "height": 4},
        )
    ]


def test_editor_window_set_rect_forwards_optional_domain_generation() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, _request_id: str, command: str, payload: dict) -> ToolResponse:
            self.calls.append((command, payload))
            if command == "editor.windows.list":
                return ok("req-windows", {"windows": [{
                    "instanceId": "42", "domainGeneration": "43", "title": "Owned Probe",
                    "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
                }]})
            return ok("req-window", {"ok": True})

    service = StatusDomainService.__new__(StatusDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.editor_window_set_rect(
        instance_id="42", domain_generation="43", x=1, y=2, width=3, height=4,
    ))

    assert result.ok
    assert dispatcher.calls[1][1]["domainGeneration"] == "43"


@pytest.mark.parametrize(
    ("selectors", "expected_code"),
    [
        ({"domain_generation": "stale-domain"}, "WINDOW_DOMAIN_MISMATCH"),
        ({"window_title": "Different title"}, "EDITORWINDOW_TITLE_MISMATCH"),
    ],
)
def test_editor_window_set_rect_rejects_explicit_id_selector_conflicts_before_mutation(
    selectors: dict[str, str], expected_code: str,
) -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, _request_id: str, command: str, payload: dict) -> ToolResponse:
            self.calls.append((command, payload))
            if command == "editor.windows.list":
                return ok("req-windows", {"windows": [{
                    "instanceId": "42", "domainGeneration": "current-domain", "title": "Owned Probe",
                    "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
                }]})
            raise AssertionError("setRect must not be dispatched for a selector conflict")

    service = StatusDomainService.__new__(StatusDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.editor_window_set_rect(
        instance_id="42", x=1, y=2, width=3, height=4, **selectors,
    ))

    assert result.ok is False
    assert result.error is not None and result.error.code == expected_code
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert [command for command, _ in dispatcher.calls] == ["editor.windows.list"]


def test_editor_window_set_rect_rejects_explicit_type_conflict_before_mutation() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, _request_id: str, command: str, payload: dict) -> ToolResponse:
            self.calls.append((command, payload))
            if command == "editor.windows.list":
                return ok("req-windows", {"windows": [{
                    "instanceId": "42", "domainGeneration": "7", "title": "Owned Probe",
                    "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
                }]})
            raise AssertionError("setRect must not be dispatched for a selector conflict")

    service = StatusDomainService.__new__(StatusDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.editor_window_set_rect(
        instance_id="42", domain_generation="7", full_type_name="Other.Window",
        x=1, y=2, width=3, height=4,
    ))

    assert result.ok is False
    assert result.error is not None and result.error.code == "EDITORWINDOW_TYPE_MISMATCH"
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert [command for command, _ in dispatcher.calls] == ["editor.windows.list"]


@pytest.mark.parametrize(
    ("selectors", "expected_code"),
    [
        ({"domain_generation": "stale-domain"}, "WINDOW_DOMAIN_MISMATCH"),
        ({"window_title": "Different title"}, "EDITORWINDOW_TITLE_MISMATCH"),
    ],
)
def test_editor_window_close_rejects_explicit_id_selector_conflicts_before_mutation(
    selectors: dict[str, str], expected_code: str,
) -> None:
    """P2-WP-04-T02: exact-ID close cannot discard a conflicting target."""

    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict]] = []

        async def call(self, _request_id: str, command: str, payload: dict) -> ToolResponse:
            self.calls.append((command, payload))
            if command == "editor.windows.list":
                return ok("req-windows", {"windows": [{
                    "instanceId": "42", "domainGeneration": "current-domain", "title": "Owned Probe",
                }]})
            raise AssertionError("close must not be dispatched for a selector conflict")

    service = StatusDomainService.__new__(StatusDomainService)
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.editor_window_close(
        instance_id="42", close_mode="forceClose", **selectors,
    ))

    assert result.ok is False
    assert result.error is not None and result.error.code == expected_code
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert [command for command, _ in dispatcher.calls] == ["editor.windows.list"]


def test_client_config_diagnostics_detects_duplicate_endpoint_and_timeout(tmp_path) -> None:
    config_dir = tmp_path / ".codex"
    config_dir.mkdir()
    config_dir.joinpath("config.toml").write_text(
        """
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
tool_timeout_sec = 60

[mcp_servers.duplicate]
url = "http://127.0.0.1:8011/mcp"
""".strip(),
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}
    assert "DUPLICATE_MCP_ENDPOINT" in codes
    assert "CLIENT_TIMEOUT_TOO_LOW" in codes


def test_client_config_diagnostics_allows_same_endpoint_for_different_clients(tmp_path) -> None:
    codex_dir = tmp_path / ".codex"
    codex_dir.mkdir()
    codex_dir.joinpath("config.toml").write_text(
        '[mcp_servers.upilot]\nurl = "http://127.0.0.1:8011/mcp"',
        encoding="utf-8",
    )
    tmp_path.joinpath(".mcp.json").write_text(
        '{"mcpServers":{"upilot":{"url":"http://127.0.0.1:8011/mcp"}}}',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}
    assert "DUPLICATE_MCP_ENDPOINT" not in codes


def test_client_config_diagnostics_rejects_upilot_process_transport(tmp_path) -> None:
    codex_dir = tmp_path / ".codex"
    codex_dir.mkdir()
    codex_dir.joinpath("config.toml").write_text(
        """
[mcp_servers.upilot]
command = "python"
args = ["run_upilot_mcp.py", "--transport", "stdio", "--port", "8765"]
""".strip(),
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    issues = {item["code"]: item for item in result["issues"]}

    assert "NON_HTTP_UPILOT_TRANSPORT" in issues
    assert "internal Unity Bridge port" in issues["NON_HTTP_UPILOT_TRANSPORT"]["message"]
    assert result["ok"] is False


def test_client_config_diagnostics_ignores_unrelated_mcp_servers(tmp_path) -> None:
    tmp_path.joinpath(".mcp.json").write_text(
        '{"mcpServers":{"database":{"url":"http://127.0.0.1:9999/not-mcp","timeout":5}}}',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)

    assert result["ok"] is True
    assert result["issues"] == []
    assert result["registrations"][0]["isUpilot"] is False


def test_client_config_diagnostics_detects_internal_websocket_url(tmp_path) -> None:
    tmp_path.joinpath(".mcp.json").write_text(
        '{"mcpServers":{"upilot":{"url":"ws://127.0.0.1:8765"}}}',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}

    assert "INTERNAL_BRIDGE_PORT_USED" in codes
    assert "MCP_HTTP_ENDPOINT_MISMATCH" in codes


def test_client_config_diagnostics_accepts_http_only_upilot_config(tmp_path) -> None:
    codex_dir = tmp_path / ".codex"
    codex_dir.mkdir()
    codex_dir.joinpath("config.toml").write_text(
        '[mcp_servers.upilot]\nurl = "http://127.0.0.1:8011/mcp"\ntool_timeout_sec = 300',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)

    assert result["ok"] is True
    assert result["issues"] == []


def test_client_config_diagnostics_accepts_distinct_named_project_endpoints(tmp_path) -> None:
    codex_dir = tmp_path / ".codex"
    codex_dir.mkdir()
    codex_dir.joinpath("config.toml").write_text(
        """
[mcp_servers.upilot-game-a]
url = "http://127.0.0.1:8011/mcp"

[mcp_servers.upilot-game-b]
url = "http://127.0.0.1:8012/mcp"
""".strip(),
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}

    assert "DUPLICATE_MCP_ENDPOINT" not in codes
    assert "MCP_HTTP_ENDPOINT_MISMATCH" not in codes


def test_client_config_diagnostics_reads_vscode_servers_shape(tmp_path) -> None:
    vscode_dir = tmp_path / ".vscode"
    vscode_dir.mkdir()
    vscode_dir.joinpath("mcp.json").write_text(
        '{"servers":{"upilot":{"type":"http","url":"http://127.0.0.1:8011/mcp"}}}',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)

    assert result["ok"] is True
    assert result["registrations"][0]["client"] == "vscode"
    assert result["registrations"][0]["transport"] == "http"


def test_mcp_tool_functions_do_not_declare_legacy_string_outputs() -> None:
    tools_dir = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_tools"
    offenders: list[str] = []
    for path in tools_dir.glob("*_tools.py"):
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in tree.body:
            if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            if not node.name.startswith("unity_"):
                continue
            if isinstance(node.returns, ast.Name) and node.returns.id == "str":
                offenders.append(f"{path.name}:{node.name}")

    assert offenders == []


def test_mcp_server_is_only_a_runtime_composition_root() -> None:
    server_path = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_stdio_server.py"
    tree = ast.parse(server_path.read_text(encoding="utf-8"), filename=str(server_path))
    public_tools = [
        node.name
        for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
        and node.name.startswith("unity_")
    ]

    assert public_tools == []


def test_test_and_flow_cancellation_tools_are_registered() -> None:
    from upilot_mcp.mcp_tools import flow_tools, test_tools  # noqa: F401
    from upilot_mcp.tool_registry import REGISTRY

    names = {item.name for item in REGISTRY.list()}
    assert {
        "unity_test_status",
        "unity_test_cancel",
        "unity_test_force_cleanup",
        "unity_test_force_reset",
        "unity_upilot_flow_status",
        "unity_upilot_flow_executions",
        "unity_upilot_flow_list",
        "unity_upilot_flow_pause",
        "unity_upilot_flow_resume",
        "unity_upilot_flow_stop",
        "unity_upilot_flow_cancel",
        "unity_upilot_flow_force_cleanup",
    }.issubset(names)


def test_monohook_tracing_tools_are_registered_with_safe_defaults() -> None:
    import inspect

    from upilot_mcp.mcp_tools import monohook_tools  # noqa: F401
    from upilot_mcp.tool_registry import REGISTRY

    status = REGISTRY.resolve("unity_monohook_tracing_status")
    configure = REGISTRY.resolve("unity_monohook_tracing_configure")
    events = REGISTRY.resolve("unity_monohook_tracing_events")

    assert status is not None and status.destructive is False
    assert configure is not None and configure.destructive is True
    assert configure.requires_write_access is True
    assert events is not None and events.destructive is False
    signature = inspect.signature(monohook_tools.unity_monohook_tracing_configure)
    assert signature.parameters["updateAutoInjectEnabled"].default is False
    assert signature.parameters["autoInjectEnabled"].default is False
    assert signature.parameters["updateCaptureStackTrace"].default is False
    assert signature.parameters["setStackTraceCaptureMode"].default is False
    assert signature.parameters["stackTraceCaptureMode"].default == "Disabled"
    assert signature.parameters["updatePointStackTraceSelection"].default is False
    assert signature.parameters["updatePointFilterOverridesEnabled"].default is False
    assert signature.parameters["pointFilterOverridesEnabled"].default is False
    assert signature.parameters["updatePointFilterProfile"].default is False
    assert signature.parameters["updatePointEnabled"].default is False
    assert signature.parameters["pointFilterProfileId"].default == ""


def test_monohook_tracing_domain_service_forwards_apply_default_false() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"ok": True})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    asyncio.run(service.monohook_tracing_configure(point_ids=["lifecycle.update"]))

    command, payload, timeout_ms = service.dispatcher.calls[0]
    assert command == "monohook.tracing.configure"
    assert payload["pointIds"] == ["lifecycle.update"]
    assert payload["apply"] is False
    assert timeout_ms == 30000


def test_monohook_tracing_domain_service_forwards_noise_controls() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"ok": True})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    asyncio.run(service.monohook_tracing_configure(
        update_per_object_rate_limit=True,
        enable_per_object_rate_limit=True,
        max_events_per_object_per_second=4,
        update_duplicate_suppression=True,
        suppress_duplicate_events=True,
        duplicate_event_window_milliseconds=250,
    ))

    _, payload, _ = service.dispatcher.calls[0]
    assert payload["updatePerObjectRateLimit"] is True
    assert payload["maxEventsPerObjectPerSecond"] == 4
    assert payload["updateDuplicateSuppression"] is True
    assert payload["duplicateEventWindowMilliseconds"] == 250


def test_monohook_tracing_domain_service_forwards_stack_mode_and_point_overrides() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"ok": True})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    asyncio.run(service.monohook_tracing_configure(
        point_ids=["lifecycle.update"],
        set_stack_trace_capture_mode=True,
        stack_trace_capture_mode="SelectedPoints",
        update_point_stack_trace_selection=True,
        capture_stack_trace=True,
        update_point_filter_overrides_enabled=True,
        point_filter_overrides_enabled=True,
        update_point_filter_profile=True,
        point_filter_profile_id="scene-objects",
    ))

    _, payload, _ = service.dispatcher.calls[0]
    assert payload["setStackTraceCaptureMode"] is True
    assert payload["stackTraceCaptureMode"] == "SelectedPoints"
    assert payload["updatePointStackTraceSelection"] is True
    assert payload["captureStackTrace"] is True
    assert payload["updatePointFilterOverridesEnabled"] is True
    assert payload["pointFilterOverridesEnabled"] is True
    assert payload["updatePointFilterProfile"] is True
    assert payload["pointFilterProfileId"] == "scene-objects"


def test_monohook_tracing_domain_service_forwards_auto_injection_switch() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"ok": True})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    asyncio.run(service.monohook_tracing_configure(
        update_auto_inject_enabled=True,
        auto_inject_enabled=True,
    ))

    _, payload, _ = service.dispatcher.calls[0]
    assert payload["updateAutoInjectEnabled"] is True
    assert payload["autoInjectEnabled"] is True
    assert payload["apply"] is False


def test_test_domain_service_forwards_staged_stop_commands() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self) -> None:
            self.calls = []

        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"status": "stopping"})

    service = TestDomainService()
    service.dispatcher = Dispatcher()

    asyncio.run(service.test_results("run-123"))
    asyncio.run(service.test_cancel("run-123"))
    asyncio.run(service.test_force_cleanup("run-123"))
    asyncio.run(service.test_force_reset())
    asyncio.run(service.upilot_flow_list())
    asyncio.run(service.upilot_flow_stop("flow-123"))
    asyncio.run(service.upilot_flow_resume("flow-123"))
    asyncio.run(service.upilot_flow_force_cleanup("flow-123"))

    assert service.dispatcher.calls == [
        ("test.results", {"runGuid": "run-123"}, None),
        ("test.cancel", {"runGuid": "run-123"}, 30000),
        ("test.force_cleanup", {"runGuid": "run-123"}, 30000),
        ("test.force_reset", {}, 30000),
        ("upilot_flow.list", {}, 30000),
        ("upilot_flow.stop", {"executionId": "flow-123"}, 30000),
        ("upilot_flow.resume", {"executionId": "flow-123"}, 30000),
        ("upilot_flow.force_cleanup", {"executionId": "flow-123"}, 30000),
    ]


def test_test_results_incremental_cursor_binds_project_run_and_stream() -> None:
    from types import SimpleNamespace
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        async def call(self, request_id, command, payload, timeout_ms=None):
            assert command == "test.results"
            assert payload == {
                "runGuid": "run-123", "incremental": True, "afterEventSequence": 0,
                "expectedResultStreamVersion": 0, "eventCount": 2,
            }
            return ok(request_id, {"runGuid": "run-123", "resultStreamVersion": 1,
                                   "lastDeliveredEventSequence": 2, "events": [{"sequence": 1}, {"sequence": 2}]})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=SimpleNamespace(_project_path="C:/Project"))

    result = asyncio.run(service.test_results("run-123", cursor="begin", count=2))

    assert result.ok
    assert result.data["events"] == [{"sequence": 1}, {"sequence": 2}]
    cursor = result.data["nextCursor"]
    decoded = service._decode_test_result_cursor(cursor)
    assert decoded == {"v": 1, "projectPath": os.path.normcase(os.path.abspath("C:/Project")),
                       "runGuid": "run-123", "resultStreamVersion": 1, "sequence": 2}
    rejected = asyncio.run(service.test_results("other-run", cursor=cursor, count=2))
    assert rejected.ok is False
    assert rejected.error.code == "TEST_RESULT_CURSOR_MISMATCH"


def test_test_results_without_cursor_preserves_legacy_bridge_payload() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        def __init__(self): self.calls = []
        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload, timeout_ms))
            return ok(request_id, {"runGuid": "run-123", "events": ["full-history"]})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    result = asyncio.run(service.test_results("run-123"))
    assert result.ok
    assert service.dispatcher.calls == [("test.results", {"runGuid": "run-123"}, None)]


def test_test_results_incremental_not_found_does_not_issue_cursor() -> None:
    from types import SimpleNamespace
    from upilot_mcp.domain.test_service import TestDomainService

    class Dispatcher:
        async def call(self, request_id, command, payload, timeout_ms=None):
            return ok(request_id, {"runGuid": "missing", "status": "none", "phase": "not_found"})

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=SimpleNamespace(_project_path="C:/Project"))
    result = asyncio.run(service.test_results("missing", cursor="begin"))
    assert result.ok
    assert result.data["phase"] == "not_found"
    assert "nextCursor" not in result.data


def test_console_capture_public_owner_token_is_returned_and_forwarded() -> None:
    from upilot_mcp.domain.status_service import StatusDomainService

    class Dispatcher:
        def __init__(self): self.calls = []
        async def call(self, request_id, command, payload, timeout_ms=None):
            self.calls.append((command, payload))
            return ok(request_id, {"session": {"sessionId": "capture-1", "ownerTokenSha256": "hash"}})

    service = StatusDomainService()
    service.dispatcher = Dispatcher()
    started = asyncio.run(service.console_capture_start(owner_id="manual-owner"))
    assert started.ok
    token = started.data["ownerToken"]
    assert token and started.data["ownerId"] == "manual-owner"
    assert service.dispatcher.calls[0][1]["ownerToken"] == token
    stopped = asyncio.run(service.console_capture_stop("capture-1", owner_token=token))
    assert stopped.ok
    assert service.dispatcher.calls[1] == ("console.capture.stop", {"sessionId": "capture-1", "ownerToken": token})


def test_console_capture_recovery_does_not_bypass_owner_token(tmp_path) -> None:
    from types import SimpleNamespace
    from upilot_mcp.domain.status_service import StatusDomainService

    capture = tmp_path / "Log" / "UPilotConsole" / "capture-1"
    capture.mkdir(parents=True)
    (capture / "summary.json").write_text("{}", encoding="utf-8")
    (capture / "session.json").write_text(json.dumps({
        "sessionId": "capture-1", "active": False, "finishedAtUtcMs": 1, "sha256": "digest",
        "ownerTokenSha256": hashlib.sha256(b"owner-token").hexdigest(),
    }), encoding="utf-8")
    service = StatusDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(tmp_path))))
    failed = fail("req", "CAPTURE_OWNERSHIP_REQUIRED", "owner token required")
    assert service._recover_completed_console_capture("capture-1", "wrong-token", failed) is None


def _authoritative_editor_state(
    state: StateStore,
    *,
    session_id: str = "session-current",
    play_mode_state: str = "edit",
    is_compiling: bool = False,
) -> int:
    timestamp = int(__import__("time").time() * 1000)
    state.reset_editor_session(session_id, process_id=42)
    accepted = state.update_editor_state(
        {
            "connected": True,
            "authoritative": True,
            "source": "bridge-heartbeat",
            "sessionId": session_id,
            "updatedAt": timestamp,
            "playModeState": play_mode_state,
            "isCompiling": is_compiling,
            "lastMainThreadPumpAt": timestamp,
        }
    )
    assert accepted is True
    return timestamp


def test_execution_state_defaults_to_unknown_stale_and_not_ready() -> None:
    execution = StateStore().execution_state()

    assert execution["status"] == "disconnected"
    assert execution["ready"] is False
    assert execution["blocked"] is True
    assert execution["authoritative"] is False
    assert execution["isStale"] is True
    assert execution["playModeState"] == "unknown"


def test_new_editor_session_is_recovering_until_authoritative_context_arrives() -> None:
    state = StateStore()
    state.reset_editor_session("session-new", process_id=42)

    execution = state.execution_state()

    assert execution["status"] == "recovering_after_reload"
    assert execution["ready"] is False
    assert execution["blockedReason"] == "EditorContextStale"
    assert execution["sessionId"] == "session-new"


def test_editor_context_rejects_old_session_and_out_of_order_updates() -> None:
    state = StateStore()
    current_timestamp = _authoritative_editor_state(state, session_id="session-new")

    old_session_accepted = state.update_editor_state(
        {
            "connected": True,
            "authoritative": True,
            "sessionId": "session-old",
            "updatedAt": current_timestamp + 100,
            "playModeState": "play",
        }
    )
    older_update_accepted = state.update_editor_state(
        {
            "connected": True,
            "authoritative": True,
            "sessionId": "session-new",
            "updatedAt": current_timestamp - 1,
            "playModeState": "play",
        }
    )

    assert old_session_accepted is False
    assert older_update_accepted is False
    assert state.editor.session_id == "session-new"
    assert state.editor.play_mode_state == "edit"
    assert state.editor.updated_at == current_timestamp


def test_execution_state_blocks_playmode_with_structured_reason() -> None:
    state = StateStore()
    _authoritative_editor_state(state, play_mode_state="play")

    execution = state.execution_state()

    assert execution["status"] == "blocked"
    assert execution["ready"] is False
    assert execution["blockedReason"] == "PlayMode"
    assert execution["isPlaying"] is True
    assert execution["nextAction"]


def test_queued_compile_is_not_ready_when_editor_flag_is_false() -> None:
    state = StateStore()
    _authoritative_editor_state(state, is_compiling=False)
    state.compile.status = "queued"
    state.compile.phase = "queued"

    execution = state.execution_state()

    assert execution["status"] == "queued"
    assert execution["ready"] is False
    assert execution["isCompiling"] is True
    assert execution["blockedReason"] == "CompilationInProgress"


def test_authoritative_editor_context_restores_compile_lifecycle_snapshot() -> None:
    state = StateStore()
    timestamp = int(__import__("time").time() * 1000)
    state.reset_editor_session("session-restore", process_id=42)

    accepted = state.update_editor_state(
        {
            "connected": True,
            "authoritative": True,
            "source": "bridge-heartbeat",
            "sessionId": "session-restore",
            "updatedAt": timestamp,
            "playModeState": "edit",
            "isCompiling": False,
            "compileStatus": "verifying",
            "compilePhase": "verifying",
            "compileRequestId": "compile-restored",
            "compileStartedAt": 100,
            "compileFinishedAt": 200,
            "lastProgressAt": 250,
        }
    )
    execution = state.execution_state()

    assert accepted is True
    assert execution["status"] == "verifying"
    assert execution["ready"] is False
    assert execution["isCompiling"] is True
    assert execution["compileRequestId"] == "compile-restored"
    assert execution["compileStartedAt"] == 100
    assert execution["compileFinishedAt"] == 200
    assert execution["lastProgressAt"] == 250


def test_domain_reload_compile_progresses_through_verification_to_terminal_state() -> None:
    state = StateStore()
    timestamp = _authoritative_editor_state(state)
    state.compile.status = "compiling"
    state.compile.phase = "domain_reload"
    state.compile.compile_request_id = "compile-reload"

    accepted = state.update_editor_state(
        {
            "connected": True,
            "authoritative": True,
            "source": "bridge-heartbeat",
            "sessionId": "session-current",
            "updatedAt": timestamp + 1,
            "playModeState": "edit",
            "isCompiling": False,
        }
    )
    assert accepted is True
    assert state.compile.phase == "verifying"

    state.update_compile_errors({"total": 0, "errors": []})
    assert state.compile.phase == "completed"

    state.compile.status = "verifying"
    state.compile.phase = "verifying"
    state.update_compile_errors(
        {"total": 1, "errors": [{"message": "compile failed"}]}
    )
    assert state.compile.phase == "failed"


def test_compile_wait_returns_structured_playmode_block() -> None:
    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    _authoritative_editor_state(state, play_mode_state="play")

    class _Server:
        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isCompiling": False})

    service = CompileDomainService.__new__(CompileDomainService)
    service.server = _Server()
    service.dispatcher = _Dispatcher()
    service._wake_unity_editor = lambda: False

    result = asyncio.run(
        service.compile_wait(timeout_s=0, poll_interval_s=0, prefer_events=False)
    )

    assert result.ok is True
    assert result.data["status"] == "blocked"
    assert result.data["blockedReason"] == "PlayMode"
    assert result.data["completed"] is False


def test_compile_wait_does_not_report_queued_phase_as_ready() -> None:
    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    _authoritative_editor_state(state)
    state.compile.status = "queued"
    state.compile.phase = "queued"

    class _Server:
        def __init__(self) -> None:
            self.state = state

        @staticmethod
        def reconcile_editor_compile_busy(_: bool) -> None:
            return None

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isCompiling": False})

    service = CompileDomainService.__new__(CompileDomainService)
    service.server = _Server()
    service.dispatcher = _Dispatcher()
    service._wake_unity_editor = lambda: False

    result = asyncio.run(
        service.compile_wait(timeout_s=0, poll_interval_s=0, prefer_events=False)
    )

    assert result.ok is True
    assert result.data["status"] == "timeout"
    assert result.data["phase"] == "queued"
    assert result.data["completed"] is False


def test_compile_wait_keeps_command_timeout_inside_total_wait_budget() -> None:
    from upilot_mcp.domain.compile_service import CompileDomainService

    state = StateStore()
    _authoritative_editor_state(state)
    state.compile.status = "queued"
    state.compile.phase = "queued"

    class _Server:
        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return fail(request_id, "COMMAND_TIMEOUT", "Editor did not pump the observation command.")

    service = CompileDomainService.__new__(CompileDomainService)
    service.server = _Server()
    service.dispatcher = _Dispatcher()
    service._wake_unity_editor = lambda: False

    result = asyncio.run(
        service.compile_wait(timeout_s=0, poll_interval_s=0, prefer_events=False)
    )

    assert result.ok is True
    assert result.data["status"] == "timeout"
    assert result.data["completed"] is False
    assert result.data["lastObservationError"] == "COMMAND_TIMEOUT"
    assert result.data["waitMode"] == "observation_timeout"


def test_ensure_ready_rejects_unknown_editor_context() -> None:
    state = StateStore()

    class _SessionManager:
        @staticmethod
        def is_connected() -> bool:
            return True

    class _Server:
        session_manager = _SessionManager()

        def __init__(self) -> None:
            self.state = state

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict) -> ToolResponse:
            assert name == "resource.editorState"
            return ok(request_id, {"isPlaying": False, "isCompiling": False})

    service = TaskDomainService()
    service.server = _Server()
    service.dispatcher = _Dispatcher()

    async def _compile_wait(**_: object) -> ToolResponse:
        return ok("req-compile", {"status": "ready"})

    service.compile_wait = _compile_wait
    result = asyncio.run(service.ensure_ready(timeout_s=1))

    assert result.ok is True
    assert result.data["ready"] is False
    assert result.data["contextAuthoritative"] is False
    assert result.data["contextStale"] is True
    assert result.data["blockedReason"] in (
        "UnityDisconnected",
        "EditorContextUnknown",
    )


def test_sync_after_disk_write_preserves_compile_block_reason() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    async def _asset_refresh() -> ToolResponse:
        return ok("req-refresh", {"ok": True})

    async def _compile() -> ToolResponse:
        return fail(
            "req-compile",
            "EDITOR_IN_PLAY_MODE",
            "blocked",
            {"blockedReason": "PlayMode", "nextAction": "Exit PlayMode."},
        )

    service.asset_refresh = _asset_refresh
    service.compile = _compile
    result = asyncio.run(
        service.sync_after_disk_write(delay_s=0, trigger_compile=True)
    )

    assert result.ok is False
    assert result.error.code == "EDITOR_IN_PLAY_MODE"
    assert result.error.detail["status"] == "blocked"
    assert result.error.detail["blockedReason"] == "PlayMode"
    assert result.error.detail["compileStarted"] is False
    assert result.error.detail["compileCompleted"] is False


def test_mcp_status_exposes_same_unified_execution_state() -> None:
    from types import SimpleNamespace

    state = StateStore()
    _authoritative_editor_state(state)

    session = SimpleNamespace(
        session_id="session-current",
        project_path="",
        unity_version="2022.3",
        platform="WindowsEditor",
        process_id=42,
        last_heartbeat_at=state.editor.updated_at,
    )

    class _SessionManager:
        active = session

        @staticmethod
        def is_connected() -> bool:
            return True

    class _Dispatcher:
        @staticmethod
        def timeout_policy_snapshot() -> dict:
            return {}

    service = StatusDomainService.__new__(StatusDomainService)
    service.server = SimpleNamespace(
        state=state,
        session_manager=_SessionManager(),
        is_ready=lambda: True,
        mcp_label="test",
        host="127.0.0.1",
        port=8765,
    )
    service.dispatcher = _Dispatcher()

    result = asyncio.run(service.mcp_status(include_capabilities=False))
    execution = state.execution_state()

    assert result.ok is True
    assert result.data["executionState"]["ready"] == execution["ready"]
    assert result.data["executionState"]["blockedReason"] == execution["blockedReason"]
    assert result.data["compile"]["phase"] == execution["compilePhase"]
    assert result.data["runtimeIdentity"]["unityEditor"] == {
        "processId": 42,
        "bridgeSessionId": "session-current",
    }
    assert result.data["runtimeIdentity"]["mcpServer"]["processId"] > 0
    assert result.data["runtimeIdentity"]["managedDomain"]["producerEpoch"] == execution["producerEpoch"]
    assert result.data["runtimeIdentity"]["managedDomain"]["domainGeneration"] == execution["domainGeneration"]


def test_status_process_existence_handles_missing_current_and_exited_process() -> None:
    assert StatusDomainService._process_exists(-1) is False
    assert StatusDomainService._process_exists(os.getpid()) is True

    process = subprocess.Popen([sys.executable, "-c", "pass"])
    process.wait(timeout=10)
    assert StatusDomainService._process_exists(process.pid) is False
