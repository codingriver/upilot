from __future__ import annotations

import asyncio
from types import SimpleNamespace

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.responses import fail, ok


class _SceneViewDispatcher:
    def __init__(self, responses: list[dict]) -> None:
        self.responses = iter(responses)
        self.calls: list[tuple[str, str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
        self.calls.append((request_id, name, payload))
        return ok(request_id, next(self.responses))


def _changed(request_id: str, *, maximized: bool, domain: str = "domain-7", **extra: object) -> dict:
    return {
        "ok": True,
        "commandId": request_id,
        "instanceId": "9",
        "domainGeneration": domain,
        "maximized": maximized,
        "changed": True,
        "writeCount": 1,
        "authoritative": True,
        "isStale": False,
        **extra,
    }


def test_restore_token_success_replays_without_a_second_setter() -> None:
    dispatcher = _SceneViewDispatcher([
        _changed("unused", maximized=True),
        _changed("unused", maximized=False),
    ])
    service = StatusDomainService()
    service.dispatcher = dispatcher

    changed = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7"))
    token = changed.data["restoreToken"]
    restored = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=token))
    replay = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=token))

    assert restored.ok and restored.data["changed"] is True
    assert replay.ok
    assert replay.data["commandId"] == restored.data["commandId"]
    assert replay.data["idempotentReplay"] is True
    assert replay.data["commandSubmitted"] is False
    assert replay.data["changed"] is False and replay.data["writeCount"] == 0
    assert len(dispatcher.calls) == 2


def test_pause_forwards_the_public_timeout_to_the_single_bridge_dispatch() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, dict, int | None]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((name, payload, timeout_ms))
            return ok(request_id, {"state": "pause", "changed": True, "writeCount": 1})

    service = StatusDomainService()
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.playmode_pause(wait=False, timeout_ms=321))

    assert result.ok
    assert result.data["commandSubmitted"] is True
    assert dispatcher.calls == [("playmode.set", {"action": "pause"}, 321)]


def test_sceneview_wait_requires_fresh_authoritative_non_stale_evidence() -> None:
    dispatcher = _SceneViewDispatcher([{
        "ok": True,
        "instanceId": "9",
        "domainGeneration": "domain-7",
        "maximized": True,
        "changed": True,
        "writeCount": 1,
    }])
    service = StatusDomainService()
    service.dispatcher = dispatcher

    result = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7", wait=True))

    assert result.ok
    assert result.data["commandId"] == dispatcher.calls[0][0]
    assert result.data["confirmed"] is False and result.data["terminal"] is False
    assert result.data["confirmationUnavailableReason"] == "FRESH_AUTHORITATIVE_SCENEVIEW_STATE_REQUIRED"
    assert len(dispatcher.calls) == 1


def test_restore_domain_mismatch_restart_and_invalid_input_dispatch_nothing() -> None:
    dispatcher = _SceneViewDispatcher([_changed("unused", maximized=True)])
    service = StatusDomainService()
    service.dispatcher = dispatcher
    changed = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7"))
    token = changed.data["restoreToken"]

    wrong_domain = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="domain-8", restore_token=token))
    invalid = asyncio.run(service.sceneview_set_maximized(-1, False))
    restarted = StatusDomainService()
    restarted.dispatcher = _SceneViewDispatcher([])
    after_restart = asyncio.run(restarted.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=token))

    assert not wrong_domain.ok and wrong_domain.error.code == "SCENEVIEW_RESTORE_TOKEN_MISMATCH"
    assert not invalid.ok and invalid.error.code == "INVALID_PAYLOAD"
    assert not after_restart.ok and after_restart.error.code == "SCENEVIEW_RESTORE_TOKEN_INVALID"
    assert len(dispatcher.calls) == 1
    assert restarted.dispatcher.calls == []


def test_restore_manual_change_or_persistence_failure_is_not_replayed() -> None:
    manual_dispatcher = _SceneViewDispatcher([
        _changed("unused", maximized=True),
        {
            "ok": False,
            "commandId": "manual-change-command",
            "instanceId": "9",
            "domainGeneration": "domain-7",
            "maximized": False,
            "deniedReason": "SCENEVIEW_STATE_CHANGED",
        },
    ])
    service = StatusDomainService()
    service.dispatcher = manual_dispatcher
    token = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7")).data["restoreToken"]
    denied = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=token))
    retry = asyncio.run(service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=token))

    assert denied.ok and denied.data["changed"] is False and denied.data["writeCount"] == 0
    assert not retry.ok and retry.error.code == "SCENEVIEW_RESTORE_RECOVERY_REQUIRED"
    assert len(manual_dispatcher.calls) == 2

    persistence_dispatcher = _SceneViewDispatcher([
        _changed("unused", maximized=True),
        {
            "ok": False,
            "commandId": "persistence-command",
            "instanceId": "9",
            "domainGeneration": "domain-7",
            "maximized": False,
            "changed": True,
            "writeCount": 1,
            "persistenceError": "state store write failed",
        },
    ])
    persistence_service = StatusDomainService()
    persistence_service.dispatcher = persistence_dispatcher
    persistence_token = asyncio.run(persistence_service.sceneview_set_maximized(9, True, domain_generation="domain-7")).data["restoreToken"]
    failed = asyncio.run(persistence_service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=persistence_token))
    blocked = asyncio.run(persistence_service.sceneview_set_maximized(9, False, domain_generation="domain-7", restore_token=persistence_token))

    assert failed.ok and failed.data["persistenceError"] == "state store write failed"
    assert not blocked.ok and blocked.error.code == "SCENEVIEW_RESTORE_RECOVERY_REQUIRED"
    assert len(persistence_dispatcher.calls) == 2


def test_restore_cancellation_before_setter_preserves_zero_side_effects_and_does_not_replay() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((request_id, name, payload))
            if len(self.calls) == 1:
                return ok(request_id, _changed(request_id, maximized=True))
            return fail(request_id, "EXECUTION_CANCELLED", "cancelled before setter", {
                "commandId": request_id,
                "commandSubmitted": False,
                "stateObserved": False,
                "changed": False,
                "writeCount": 0,
                "sideEffectsMayHaveOccurred": False,
            })

    service = StatusDomainService()
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher
    token = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7")).data["restoreToken"]

    cancelled = asyncio.run(service.sceneview_set_maximized(
        9, False, domain_generation="domain-7", restore_token=token,
    ))
    blocked = asyncio.run(service.sceneview_set_maximized(
        9, False, domain_generation="domain-7", restore_token=token,
    ))

    assert cancelled.ok is False and cancelled.error.code == "EXECUTION_CANCELLED"
    assert cancelled.error.detail["commandSubmitted"] is False
    assert cancelled.error.detail["sideEffectsMayHaveOccurred"] is False
    assert cancelled.error.detail["terminal"] is True
    assert blocked.ok is False and blocked.error.code == "SCENEVIEW_RESTORE_RECOVERY_REQUIRED"
    assert blocked.error.detail["sideEffectsMayHaveOccurred"] is False
    assert len(dispatcher.calls) == 2


def test_sceneview_timeout_is_queryable_by_original_identity_without_replaying_setter() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.calls: list[tuple[str, str, dict]] = []
            self.state = SimpleNamespace(commands={
                "cmd-lost": SimpleNamespace(name="sceneview.setMaximized"),
            })

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((request_id, name, payload))
            if name == "sceneview.setMaximized":
                return fail(request_id, "COMMAND_TIMEOUT", "response lost", {"commandId": "cmd-lost"})
            assert name == "sceneview.commandStatus"
            assert payload == {"commandId": "cmd-lost"}
            return ok(request_id, {
                "commandId": "cmd-lost",
                "observationStatus": "observed",
                "terminal": False,
                "commandSubmitted": True,
                "stateObserved": True,
                "changed": True,
                "writeCount": 1,
                "sideEffectsMayHaveOccurred": True,
            })

    service = StatusDomainService()
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    timed_out = asyncio.run(service.sceneview_set_maximized(9, True, domain_generation="domain-7"))
    observed = asyncio.run(service.sceneview_command_status("cmd-lost"))

    assert not timed_out.ok and timed_out.error.code == "COMMAND_TIMEOUT"
    assert timed_out.error.detail["observationStatus"] == "submitted"
    assert timed_out.error.detail["terminal"] is False
    assert timed_out.error.detail["commandSubmitted"] is True
    assert "unity_sceneview_command_status" in timed_out.error.detail["nextAction"]
    assert observed.ok and observed.data["observationStatus"] == "observed"
    assert observed.data["commandId"] == "cmd-lost"
    assert [name for _, name, _ in dispatcher.calls] == [
        "sceneview.setMaximized", "sceneview.commandStatus",
    ]


def test_sceneview_command_status_after_server_restart_is_recovery_required_without_dispatch() -> None:
    class _Dispatcher:
        def __init__(self) -> None:
            self.state = SimpleNamespace(commands={})
            self.calls: list[tuple[str, str, dict]] = []

        async def call(self, request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
            self.calls.append((request_id, name, payload))
            raise AssertionError("unknown original commands must not reach the Bridge")

    service = StatusDomainService()
    dispatcher = _Dispatcher()
    service.dispatcher = dispatcher

    result = asyncio.run(service.sceneview_command_status("cmd-prior-server"))

    assert not result.ok and result.error.code == "SCENEVIEW_COMMAND_RECOVERY_REQUIRED"
    assert result.error.detail["observationStatus"] == "unknown"
    assert result.error.detail["recoveryRequired"] is True
    assert result.error.detail["terminal"] is False
    assert result.error.detail["sideEffectsMayHaveOccurred"] is True
    assert dispatcher.calls == []
