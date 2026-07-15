from __future__ import annotations

import asyncio
import time
from typing import Any

from .env import env_float
from .models import ToolResponse
from .protocol import new_id
from .responses import fail, ok
from .state_store import StateStore


class CommandDispatcher:
    DEFAULT_TIMEOUT_MS = 30000
    COMMAND_TIMEOUT_MS: dict[str, int] = {
        "compile.request": 180000,
        "compile.wait": 660000,
        "compile.errors.get": 45000,
        "playmode.set": 180000,
        "editor.delay": 150000,
        "test.run": 300000,
        "build.start": 600000,
        "editor.window.close": 30000,
        "editor.window.setRect": 30000,
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
            "default": self.timeout_ms,
            "commands": dict(self.COMMAND_TIMEOUT_MS),
            "prefixes": dict(self.PREFIX_TIMEOUT_MS),
        }

    async def call(self, request_id: str, name: str, payload: dict[str, Any], timeout_ms: int | None = None) -> ToolResponse:
        started = time.monotonic()
        if not self.transport.is_ready():
            wait_s = env_float("UPILOT_CALL_WAIT_READY_S", 300.0)
            waiter = getattr(self.transport, "wait_until_ready", None)
            if callable(waiter):
                ok_wait = await waiter(wait_s if wait_s > 0 else 0.0)
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

        command_id = new_id("cmd")
        self.state.create_command(command_id=command_id, request_id=request_id, name=name, payload=payload)

        future = self.transport.register_pending(command_id)
        await self.transport.send_command(command_id=command_id, name=name, payload=payload)

        wait_timeout = self._resolve_timeout_ms(name, timeout_ms) / 1000
        try:
            result = await asyncio.wait_for(future, timeout=wait_timeout)
        except asyncio.TimeoutError:
            self.state.mark_failed(command_id, {"code": "COMMAND_TIMEOUT", "message": "命令超时"})
            elapsed_ms = round((time.monotonic() - started) * 1000)
            return fail(request_id, "COMMAND_TIMEOUT", "命令超时", {"command": name, "commandId": command_id}, timing={"totalMs": elapsed_ms, "queueMs": 0, "bridgeMs": elapsed_ms, "unityExecutionMs": 0})

        if result.get("type") == "error":
            err = result.get("payload") or {}
            self.state.mark_failed(command_id, err)
            elapsed_ms = round((time.monotonic() - started) * 1000)
            bridge_timing = result.get("timing") if isinstance(result, dict) else None
            timing = self._compose_timing(elapsed_ms, bridge_timing)
            return fail(
                request_id,
                str(err.get("code", "INTERNAL_ERROR")),
                str(err.get("message", "命令执行失败")),
                {"command": name, "commandId": command_id, "detail": err.get("detail", {})},
                timing=timing,
            )

        payload_data = result.get("payload") or {}
        self.state.mark_success(command_id, payload_data)
        elapsed_ms = round((time.monotonic() - started) * 1000)
        bridge_timing = result.get("timing") if isinstance(result, dict) else None
        timing = self._compose_timing(elapsed_ms, bridge_timing)
        return ok(request_id, payload_data, timing=timing)

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

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        raise NotImplementedError
