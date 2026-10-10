from __future__ import annotations

import asyncio
import time
from typing import Any

from .env import env_float
from .models import ToolResponse
from .protocol import new_id
from .responses import fail, ok
from .state_store import StateStore
from .wire_ids import normalize_wire_ids_for_unity


class CommandDispatcher:
    DEFAULT_TIMEOUT_MS = 30000
    COMMAND_TIMEOUT_MS: dict[str, int] = {
        "compile.request": 180000,
        "compile.wait": 660000,
        "compile.errors.get": 45000,
        "playmode.set": 180000,
        "editor.delay": 150000,
        "test.run": 300000,
        "test.status": 30000,
        "test.cancel": 30000,
        "test.force_cleanup": 30000,
        "test.force_reset": 30000,
        "build.start": 600000,
        "editor.window.close": 30000,
        "editor.window.setRect": 30000,
        "console.capture.read": 180000,
        "console.capture.list": 60000,
        # "uitoolkit.scrollbar.drag": 60000,
    }
    PREFIX_TIMEOUT_MS: dict[str, int] = {
        "package.": 120000,
        "scene.": 45000,
        "script.": 60000,
    }

    def __init__(self, transport: "WsTransport", state: StateStore, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> None:
        self.transport = transport
        self.state = state
        self.timeout_ms = timeout_ms

    def _resolve_timeout_ms(self, name: str, timeout_ms: int | None) -> int:
        if timeout_ms is not None:
            return timeout_ms
        if name in self.COMMAND_TIMEOUT_MS:
            return self.COMMAND_TIMEOUT_MS[name]
        for prefix, value in self.PREFIX_TIMEOUT_MS.items():
            if name.startswith(prefix):
                return value
        return self.timeout_ms

    def timeout_policy_snapshot(self) -> dict[str, Any]:
        return {
            "scope": "ready-send-response-observation-budget; not Unity execution deadline",
            "default": self.timeout_ms,
            "commands": dict(self.COMMAND_TIMEOUT_MS),
            "prefixes": dict(self.PREFIX_TIMEOUT_MS),
        }

    async def call(self, request_id: str, name: str, payload: dict[str, Any], timeout_ms: int | None = None) -> ToolResponse:
        started = time.monotonic()
        call_started_at = int(time.time() * 1000)
        call_timeout_ms = self._resolve_timeout_ms(name, timeout_ms)
        deadline = started + call_timeout_ms / 1000
        try:
            payload = normalize_wire_ids_for_unity(payload)
        except ValueError as exc:
            return fail(
                request_id,
                "UNSAFE_WIRE_ID",
                str(exc),
                {"command": name, "nextAction": "Pass Unity wire IDs as decimal strings."},
                timing={"totalMs": 0, "queueMs": 0, "bridgeMs": 0, "unityExecutionMs": 0},
            )
        if not self.transport.is_ready():
            wait_s = env_float("UPILOT_CALL_WAIT_READY_S", 300.0)
            waiter = getattr(self.transport, "wait_until_ready", None)
            if callable(waiter):
                remaining_ready = max(0.0, deadline - time.monotonic())
                ready_budget = min(max(0.0, wait_s), remaining_ready)
                call_budget_expired = False
                try:
                    ok_wait = await asyncio.wait_for(waiter(ready_budget), timeout=ready_budget)
                except asyncio.TimeoutError:
                    ok_wait = False
                    # Event-loop timer granularity can fire before another monotonic read.
                    # Classify by the budget that bounded this wait, not a fresh full wait.
                    call_budget_expired = remaining_ready <= max(0.0, wait_s)
                if call_budget_expired or time.monotonic() >= deadline:
                    return self._timeout_response(request_id, name, None, started, "ready")
                if not ok_wait:
                    return fail(
                        request_id,
                        "UNITY_NOT_CONNECTED",
                        "Unity 未连接（等待重连超时，可增大 UPILOT_CALL_WAIT_READY_S）",
                        {"command": name, "waitReadyS": wait_s},
                        timing={"totalMs": round((time.monotonic() - started) * 1000), "queueMs": 0, "bridgeMs": 0, "unityExecutionMs": 0},
                    )
            else:
                return fail(request_id, "UNITY_NOT_CONNECTED", "Unity 未连接", {"command": name}, timing={"totalMs": round((time.monotonic() - started) * 1000), "queueMs": 0, "bridgeMs": 0, "unityExecutionMs": 0})

        if time.monotonic() >= deadline:
            return self._timeout_response(request_id, name, None, started, "ready")

        command_id = new_id("cmd")
        if name in ("playmode.set", "test.run"):
            from .operation_context import OPERATION_ID
            payload = {**payload, "requestId": request_id, "operationId": OPERATION_ID.get()}
            if name == "playmode.set":
                action = str(payload.get("action") or "")
                payload["toolName"] = {
                    "play": "unity_playmode_start", "stop": "unity_playmode_stop",
                    "pause": "unity_playmode_pause", "resume": "unity_playmode_resume",
                }.get(action, "unity_playmode_set")
        record = self.state.create_command(command_id=command_id, request_id=request_id, name=name, payload=payload)
        record.call_started_at = call_started_at
        record.call_timeout_ms = call_timeout_ms
        record.call_deadline_at = call_started_at + call_timeout_ms
        record.call_deadline_monotonic = deadline

        future = self.transport.register_pending(command_id)
        try:
            await asyncio.wait_for(
                self.transport.send_command(command_id=command_id, name=name, payload=payload),
                timeout=max(0.0, deadline - time.monotonic()),
            )
        except asyncio.TimeoutError:
            self.transport.unregister_pending(command_id)
            return self._timeout_response(request_id, name, command_id, started, "send")
        except (Exception, asyncio.CancelledError) as exc:
            # send() may fail/cancel after bytes reached Unity. Release the
            # observer, but neither claim non-execution nor replay the command.
            self.transport.unregister_pending(command_id)
            record = self.state.commands.get(command_id)
            # Reload recovery may already have frozen the original failure
            # while the old socket was still awaiting send completion.
            if record is not None and record.status not in {"success", "failed"}:
                cancelled = isinstance(exc, asyncio.CancelledError)
                self.state.mark_failed(command_id, {
                    "code": "CANCELLED" if cancelled else "COMMAND_SEND_FAILED",
                    "message": "Command send was cancelled." if cancelled else "Command send failed.",
                    "detail": {
                        "commandId": command_id, "commandName": name,
                        "outcome": "unknown", "replayAttempted": False,
                        "exceptionType": type(exc).__name__,
                        "nextAction": "Observe the original operation or task identity; do not retry its start.",
                    },
                })
            raise

        wait_timeout = max(0.0, deadline - time.monotonic())
        wait_timeout_ms = max(0, int(wait_timeout * 1000))
        record = self.state.commands.get(command_id)
        # Actual response stage receives only the remaining total observation budget.
        # Inner awaitables and outer operations retain their independent deadlines.
        if (record is not None and not future.done()
                and record.status not in {"success", "failed"}):
            record.response_wait_started_at = int(time.time() * 1000)
            record.response_wait_timeout_ms = wait_timeout_ms
            record.response_wait_deadline_at = min(
                record.call_deadline_at, record.response_wait_started_at + wait_timeout_ms)
        try:
            result = await asyncio.wait_for(future, timeout=wait_timeout)
        except asyncio.TimeoutError:
            # A timed-out future must not remain in the transport registry.  If
            # it is later moved to the reconnect queue, the server can replay a
            # command whose caller has already received a terminal timeout.
            self.transport.unregister_pending(command_id)
            return self._timeout_response(request_id, name, command_id, started, "response")
        except asyncio.CancelledError:
            self.transport.unregister_pending(command_id)
            self.state.mark_failed(command_id, {"code": "CANCELLED", "message": "命令已取消",
                "detail": {"outcome": "unknown", "replayAttempted": False}})
            raise

        # A prior recovery/timeout/cancellation terminal wins over any late result.
        record = self.state.commands.get(command_id)
        if record is not None and record.status in {"success", "failed"}:
            result = {**result, "type": "error" if record.status == "failed" else "result",
                      "payload": record.error if record.status == "failed" else record.result}

        if result.get("type") == "error":
            err = result.get("payload") or {}
            response_context = result.get("context") if isinstance(result.get("context"), dict) else None
            if response_context:
                self.state.update_editor_context(response_context)
            self.state.mark_failed(command_id, err)
            elapsed_ms = round((time.monotonic() - started) * 1000)
            bridge_timing = result.get("timing") if isinstance(result, dict) else None
            timing = self._compose_timing(elapsed_ms, bridge_timing)
            bridge_detail = err.get("detail", {}) if isinstance(err.get("detail"), dict) else {}
            response_detail = {"command": name, "commandId": command_id, "detail": bridge_detail}
            response_detail.update(bridge_detail)
            return fail(
                request_id,
                str(err.get("code", "INTERNAL_ERROR")),
                str(err.get("message", "命令执行失败")),
                response_detail,
                context=response_context,
                timing=timing,
            )

        payload_data = result.get("payload") or {}
        response_context = result.get("context") if isinstance(result.get("context"), dict) else None
        if response_context:
            self.state.update_editor_context(response_context)
        self.state.mark_success(command_id, payload_data)
        elapsed_ms = round((time.monotonic() - started) * 1000)
        bridge_timing = result.get("timing") if isinstance(result, dict) else None
        timing = self._compose_timing(elapsed_ms, bridge_timing)
        return ok(request_id, payload_data, context=response_context, timing=timing)

    def _timeout_response(self, request_id: str, name: str, command_id: str | None,
                          started: float, phase: str) -> ToolResponse:
        detail = {"command": name, "commandId": command_id, "waitPhase": phase,
                  "outcome": "not_sent" if command_id is None else "unknown",
                  "replayAttempted": False,
                  "nextAction": "Observe the original operation identity; timeout does not stop Unity execution."}
        error = {"code": "COMMAND_TIMEOUT", "message": "命令总等待预算已耗尽", "detail": detail}
        if command_id is not None:
            self.state.mark_failed(command_id, error)
            record = self.state.commands.get(command_id)
            if record is not None and record.status == "success":
                return ok(request_id, record.result, timing=self._compose_timing(
                    round((time.monotonic() - started) * 1000), None))
            if record is not None and record.error:
                error = record.error
                prior_detail = error.get("detail") if isinstance(error.get("detail"), dict) else {}
                detail = {"command": name, "commandId": command_id, **prior_detail}
        return fail(request_id, str(error["code"]), str(error.get("message", "命令等待失败")),
                    detail, timing=self._compose_timing(round((time.monotonic() - started) * 1000), None))

    @staticmethod
    def _compose_timing(total_ms: int, bridge_timing: dict[str, Any] | None) -> dict[str, int]:
        bridge = bridge_timing or {}
        bridge_ms = int(bridge.get("bridgeMs", 0))
        return {
            "totalMs": total_ms,
            "queueMs": int(bridge.get("queueMs", 0)),
            "bridgeMs": bridge_ms,
            "unityExecutionMs": int(bridge.get("unityExecutionMs", 0)),
            "serializationMs": int(bridge.get("serializationMs", 0)),
            "roundTripMs": max(0, total_ms - bridge_ms),
        }


class WsTransport:
    def is_ready(self) -> bool:
        raise NotImplementedError

    def register_pending(self, command_id: str) -> asyncio.Future:
        raise NotImplementedError

    def unregister_pending(self, command_id: str) -> None:
        raise NotImplementedError

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        raise NotImplementedError
