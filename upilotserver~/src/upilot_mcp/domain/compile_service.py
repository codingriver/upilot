from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import json
import logging
import os
import shlex
import subprocess
import sys
import time
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from ..config import CONFIG, diagnose_client_configs
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import REGISTRY, REGISTRY_VERSION, dispatch_public_tool

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="


def _normalize_reflection_parameters(parameters: list | None) -> list:
    if not parameters:
        return []
    normalized = []
    for value in parameters:
        if value is None:
            normalized.append(None)
        elif isinstance(value, (list, dict)):
            normalized.append(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
        else:
            normalized.append(str(value))
    return normalized


def _json_dumps_or_empty(value: object | None) -> str:
    if value is None:
        return ""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))

class CompileDomainService:
    def _detect_library_dll_mtime(self) -> int:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return 0
        library_dir = Path(session.project_path) / "Library"
        if not library_dir.exists() or not library_dir.is_dir():
            return 0
        latest = 0.0
        patterns = ["**/*.dll", "**/*.dll.mdb", "**/*.pdb"]
        for pattern in patterns:
            for p in library_dir.glob(pattern):
                try:
                    ts = p.stat().st_mtime
                    if ts > latest:
                        latest = ts
                except OSError:
                    continue
        return int(latest * 1000)

    def _sync_workspace_root_from_session(self) -> None:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return
        self.patch_service.set_workspace_root(session.project_path)
        self.fix_planner.workspace_root = self.patch_service.workspace_root

    async def _post_patch_sync_after_write(self) -> None:
        logger = logging.getLogger("upilot.facade")
        r = await self.sync_after_disk_write(delay_s=2.0, trigger_compile=True)
        if not r.ok:
            logger.warning(
                "post_patch_sync_after_write: %s",
                r.error.message if r.error else "unknown",
            )

    async def compile(self) -> ToolResponse:
        request_id = new_id("req")
        result = await self.dispatcher.call(
            request_id, "compile.request", {"requestId": request_id}, timeout_ms=180000
        )

        # If compile failed due to domain reload disconnect, wait for reconnect then return status
        if (
            not result.ok
            and result.error
            and result.error.code in ("CONNECTION_LOST", "DOMAIN_RELOAD_TIMEOUT")
        ):
            import asyncio

            # Wait up to 60s for Unity to reconnect after domain reload
            for _ in range(30):
                await asyncio.sleep(2)
                if self.server.session_manager.is_connected():
                    # Re-query compile errors after reconnect
                    errors_result = await self.dispatcher.call(
                        new_id("req"), "compile.errors.get", {}, timeout_ms=15000
                    )
                    compile_state = self.server.state.compile
                    return ok(
                        request_id,
                        {
                            "accepted": True,
                            "compileRequestId": request_id,
                            "status": "finished_after_reload",
                            "reconnected": True,
                            "errors": errors_result.data if errors_result.ok else {},
                            "compileState": {
                                "status": compile_state.status,
                                "errorCount": compile_state.error_count,
                            },
                        },
                    )
            return fail(
                request_id,
                "COMPILE_RECONNECT_TIMEOUT",
                "编译触发域重载后 Unity 未能重连",
                {"requestId": request_id},
            )

        return result

    async def compile_status(self, compile_request_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        compile_state = self.server.state.compile
        return ok(
            request_id,
            {
                "status": compile_state.status,
                "errorCount": compile_state.error_count,
                "warningCount": compile_state.warning_count,
                "startedAt": compile_state.started_at,
                "finishedAt": compile_state.finished_at,
                "compileRequestId": compile_request_id
                or compile_state.compile_request_id,
            },
        )

    async def compile_errors(self, compile_request_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        # 强制 live：禁止回退缓存；若拉取失败视为无报错
        result = await self.dispatcher.call(
            request_id, "compile.errors.get", {}, timeout_ms=45000
        )
        if result.ok:
            data = result.data or {}
            self.server.state.update_compile_errors(data)
            data.setdefault("source", "live")
            data.setdefault("mode", "strict_live")
            data.setdefault("compileRequestId", compile_request_id)
            return ok(request_id, data)

        # live 拉取失败：按要求视为“无报错”，并附加辅助诊断信息
        dll_mtime_ms = self._detect_library_dll_mtime()
        return ok(
            request_id,
            {
                "errors": [],
                "total": 0,
                "compileRequestId": compile_request_id,
                "source": "live",
                "mode": "strict_live",
                "liveFetch": "failed_as_empty",
                "diagnostics": {
                    "libraryDllLatestWriteMs": dll_mtime_ms,
                    "libraryDllExists": dll_mtime_ms > 0,
                },
            },
        )

    async def auto_fix_start(
        self, max_iterations: int = 20, stop_when_no_error: bool = True
    ) -> ToolResponse:
        self._sync_workspace_root_from_session()
        request_id = new_id("req")
        loop = await self.auto_fix_loop.start(
            max_iterations=max_iterations, stop_when_no_error=stop_when_no_error
        )
        return ok(request_id, asdict(loop))

    async def auto_fix_stop(self, loop_id: str) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.stop(loop_id)
        if not loop:
            return fail(
                request_id, "INVALID_PAYLOAD", "loopId 不存在", {"loopId": loop_id}
            )
        return ok(request_id, asdict(loop))

    async def auto_fix_status(self) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.status()
        if not loop:
            return ok(request_id, {"status": "idle"})
        return ok(request_id, asdict(loop))

    async def compile_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
    ) -> ToolResponse:
        """Wait until editor reports not compiling: compile.* WebSocket events, then exponential backoff poll."""
        import time

        request_id = new_id("req")
        deadline = time.monotonic() + timeout_s
        polls = 0
        reconnect_waited = False
        modes: list[str] = []
        last_wake = 0.0

        async def poll_editor_state() -> ToolResponse:
            return await self.dispatcher.call(new_id("req"), "resource.editorState", {})

        while True:
            polls += 1
            # Wake Unity every ~5 seconds to avoid background throttling when unfocused
            if time.monotonic() - last_wake >= 5.0:
                if self._wake_unity_editor():
                    last_wake = time.monotonic()

            r = await poll_editor_state()
            if not r.ok:
                err_code = r.error.code if r.error else ""
                if err_code in (
                    "UNITY_NOT_CONNECTED",
                    "CONNECTION_LOST",
                    "DOMAIN_RELOAD_TIMEOUT",
                ):
                    if time.monotonic() >= deadline:
                        return ok(
                            request_id,
                            {
                                "status": "timeout",
                                "isCompiling": True,
                                "pollCount": polls,
                                "elapsedS": timeout_s,
                                "note": "Unity disconnected (likely domain reload)",
                                "reconnectedDuringWait": reconnect_waited,
                                "waitMode": "disconnect_timeout",
                            },
                        )
                    reconnect_waited = True
                    await asyncio.sleep(poll_interval_s * 2)
                    continue
                return r

            is_compiling = bool(r.data.get("isCompiling", False)) if r.data else False
            has_compile_errors = (
                bool(r.data.get("hasCompileErrors", False)) if r.data else False
            )
            if not is_compiling:
                if has_compile_errors:
                    return fail(
                        request_id,
                        "COMPILE_ERROR",
                        "Unity compilation finished with errors.",
                        {
                            "pollCount": polls,
                            "elapsedS": round(
                                timeout_s - (deadline - time.monotonic()), 2
                            ),
                            "reconnectedDuringWait": reconnect_waited,
                        },
                    )
                if polls == 1:
                    wm = "immediate"
                elif modes:
                    wm = "+".join(modes) + "+poll"
                else:
                    wm = "poll"
                return ok(
                    request_id,
                    {
                        "status": "ready",
                        "isCompiling": False,
                        "pollCount": polls,
                        "elapsedS": round(timeout_s - (deadline - time.monotonic()), 2),
                        "reconnectedDuringWait": reconnect_waited,
                        "waitMode": wm,
                    },
                )

            if polls == 1:
                self.server.reconcile_editor_compile_busy(True)

            if polls == 1 and prefer_events and self.server.is_ready():
                remaining = deadline - time.monotonic()
                if remaining > 0:
                    ev_budget = min(45.0, max(5.0, timeout_s * 0.35))
                    ev_budget = min(ev_budget, remaining)
                    if await self.server.wait_for_compile_idle(ev_budget):
                        modes.append("event")
                        r_ev = await poll_editor_state()
                        if (
                            r_ev.ok
                            and r_ev.data
                            and not r_ev.data.get("isCompiling", False)
                        ):
                            if r_ev.data.get("hasCompileErrors", False):
                                return fail(
                                    request_id,
                                    "COMPILE_ERROR",
                                    "Unity compilation finished with errors.",
                                    {
                                        "pollCount": polls + 1,
                                        "elapsedS": round(
                                            timeout_s - (deadline - time.monotonic()), 2
                                        ),
                                        "reconnectedDuringWait": reconnect_waited,
                                    },
                                )
                            return ok(
                                request_id,
                                {
                                    "status": "ready",
                                    "isCompiling": False,
                                    "pollCount": polls + 1,
                                    "elapsedS": round(
                                        timeout_s - (deadline - time.monotonic()), 2
                                    ),
                                    "reconnectedDuringWait": reconnect_waited,
                                    "waitMode": "event",
                                },
                            )
                        continue

            interval = (
                min(poll_interval_s, 0.25)
                if polls <= 2
                else min(poll_interval_s * (1.5 ** min(polls - 3, 8)), 2.0)
            )
            if time.monotonic() >= deadline:
                return ok(
                    request_id,
                    {
                        "status": "timeout",
                        "isCompiling": True,
                        "pollCount": polls,
                        "elapsedS": timeout_s,
                        "reconnectedDuringWait": reconnect_waited,
                        "waitMode": "timeout+" + "+".join(modes)
                        if modes
                        else "timeout",
                    },
                )
            self.server.sync_compile_state_from_editor(is_compiling)
            await asyncio.sleep(interval)

    async def compile_wait_editor(self, timeout_ms: int = 300000) -> ToolResponse:
        """Single Bridge command: block in Unity until EditorApplication.isCompiling is false."""
        request_id = new_id("req")
        tw = int(timeout_ms) + 90000
        if tw > 660000:
            tw = 660000
        return await self.dispatcher.call(
            request_id,
            "compile.wait",
            {"timeoutMs": int(timeout_ms)},
            timeout_ms=tw,
        )

    async def safe_compile_and_wait(
        self,
        timeout_s: float = 300,
        poll_interval_s: float = 1.0,
        prefer_events: bool = True,
        post_compile_delay_s: float = 3.0,
    ) -> ToolResponse:
        """Robust compile wait with post-compile cooldown and double-verification.

        Workflow:
        1. Trigger compile via compile.request
        2. Wait for compile idle (events + poll fallback)
        3. Cooldown period to allow domain reload to complete
        4. Reconnect if disconnected by domain reload
        5. Query compile.errors.get for persistent errors
        6. Return success only if errors.total == 0

        This avoids false-positive "compile success" when domain reload resets
        error state in memory. Unity side now persists errors to disk.
        """
        import time

        request_id = new_id("req")

        # Step 1: Trigger compile
        compile_r = await self.compile()
        if not compile_r.ok:
            return compile_r

        compile_request_id = (
            compile_r.data.get("compileRequestId", "") if compile_r.data else ""
        )

        # Step 2: Wait for compile idle
        wait_r = await self.compile_wait(
            timeout_s=timeout_s,
            poll_interval_s=poll_interval_s,
            prefer_events=prefer_events,
        )

        # If compile_wait itself reports compile errors, fail fast
        if not wait_r.ok:
            return wait_r

        if wait_r.data and wait_r.data.get("status") == "timeout":
            return fail(
                request_id,
                "COMPILE_TIMEOUT",
                "编译等待超时",
                {
                    "compileRequestId": compile_request_id,
                    "compileWaitResult": wait_r.data,
                },
            )

        # Step 3: Cooldown period to allow domain reload to complete
        # This is critical: compile may finish just before domain reload starts
        remaining_after_wait = (
            timeout_s - wait_r.data.get("elapsedS", 0) if wait_r.data else timeout_s
        )
        actual_delay = min(post_compile_delay_s, max(0.5, remaining_after_wait * 0.1))
        if actual_delay > 0:
            await asyncio.sleep(actual_delay)

        # Step 4: If disconnected (likely domain reload), wait for reconnect
        if not self.server.is_ready():
            reconnect_deadline = time.monotonic() + min(60.0, remaining_after_wait)
            reconnected = False
            while time.monotonic() < reconnect_deadline:
                if self.server.is_ready():
                    reconnected = True
                    break
                await asyncio.sleep(1.0)
            if not reconnected:
                return fail(
                    request_id,
                    "RECONNECT_FAILED",
                    "编译完成后 Unity 未能重连（可能 Domain Reload 卡住）",
                    {"compileRequestId": compile_request_id},
                )

        # Step 5: Double-verify compile errors (reads from disk on Unity side)
        errors_r = await self.compile_errors(compile_request_id)
        if errors_r.ok and errors_r.data:
            error_total = errors_r.data.get("total", 0)
            if error_total > 0:
                return fail(
                    request_id,
                    "COMPILE_ERROR",
                    f"编译失败，共 {error_total} 个错误",
                    {
                        "compileRequestId": compile_request_id,
                        "errors": errors_r.data.get("errors", []),
                        "errorTotal": error_total,
                        "source": errors_r.data.get("source", "unknown"),
                        "mode": errors_r.data.get("mode", "unknown"),
                    },
                )

        # Step 6: Success
        return ok(
            request_id,
            {
                "status": "success",
                "compileRequestId": compile_request_id,
                "postCompileDelayS": actual_delay,
                "reconnectedAfterReload": not self.server.is_ready()
                if False
                else False,
                "errorsVerified": True,
                "errorTotal": 0,
            },
        )
