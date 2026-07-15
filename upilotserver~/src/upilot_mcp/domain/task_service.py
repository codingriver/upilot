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

class TaskDomainService:
    async def operation_list(self, status: str = "", limit: int = 50) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "operation.list",
            {"status": status, "limit": max(1, min(limit, 200))},
        )

    async def operation_get(self, command_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "operation.get", {"commandId": command_id})

    async def ensure_ready(self, timeout_s: float = 300) -> ToolResponse:
        """Pre-test environment check: connection + compile wait + edit mode."""
        import time

        request_id = new_id("req")
        checks: dict = {}

        # 1. Wait for connection
        deadline = time.monotonic() + timeout_s
        connected = False
        for _ in range(int(timeout_s / 0.5)):
            if self.server.session_manager.is_connected():
                connected = True
                break
            await asyncio.sleep(0.5)
            if time.monotonic() >= deadline:
                break
        checks["connected"] = connected
        if not connected:
            checks["ready"] = False
            checks["failReason"] = "Unity not connected within timeout"
            return ok(request_id, checks)

        # 2. Wait for compilation to finish
        remaining = max(1, deadline - time.monotonic())
        compile_r = await self.compile_wait(timeout_s=remaining, poll_interval_s=0.5)
        if compile_r.ok and compile_r.data:
            checks["compileStatus"] = compile_r.data.get("status", "unknown")
        else:
            checks["compileStatus"] = "error"

        # 3. Check editor state
        state_r = await self.dispatcher.call(new_id("req"), "resource.editorState", {})
        if state_r.ok and state_r.data:
            checks["isCompiling"] = state_r.data.get("isCompiling", False)
            checks["playModeState"] = state_r.data.get("playModeState", "unknown")
            in_edit = state_r.data.get("playModeState", "") in ("edit", "Edit", "")
            checks["inEditMode"] = in_edit
        else:
            checks["inEditMode"] = False

        checks["ready"] = (
            checks["connected"]
            and checks.get("compileStatus") == "ready"
            and checks.get("inEditMode", False)
        )
        return ok(request_id, checks)

    @staticmethod
    def _task_execute_tool_succeeded(tool_name: str, result: ToolResponse) -> bool:
        """True when the tool transport succeeded *and* the tool-specific outcome is success."""
        data = result.data
        if tool_name == "wait_condition" and isinstance(data, dict):
            return bool(data.get("met"))
        return True

    @staticmethod
    def _task_execute_logical_error(tool_name: str, result: ToolResponse) -> str:
        data = result.data if isinstance(result.data, dict) else {}
        if tool_name == "wait_condition":
            return str(data.get("lastError") or "wait_condition not met (met=false)")
        return "logical failure"

    async def task_execute(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        max_total_s: float = 1200,
        retry_count: int = 1,
        restart_unity_on_timeout: bool = True,
    ) -> ToolResponse:
        """Execute an MCP tool call with timeout/watchdog.

        Workflow (per user spec):
        1. Run tool with timeout_s (default 10min).
        2. On timeout, if restart_unity_on_timeout: attempt to close/reopen Unity.
        3. Retry up to retry_count times.
        4. If total time exceeds max_total_s (default 20min), skip.
        """
        import time

        request_id = new_id("req")
        start = time.monotonic()
        attempts = 0
        last_error = ""
        events: list[dict] = []

        for attempt in range(retry_count + 1):
            attempts += 1
            elapsed_total = time.monotonic() - start
            if elapsed_total >= max_total_s:
                events.append(
                    {"event": "max_total_exceeded", "elapsed": round(elapsed_total, 1)}
                )
                break

            remaining_total = max_total_s - elapsed_total
            effective_timeout = min(timeout_s, remaining_total)

            try:
                result = await asyncio.wait_for(
                    self._dispatch_tool(tool_name, tool_args or {}),
                    timeout=effective_timeout,
                )
                if result.ok and self._task_execute_tool_succeeded(tool_name, result):
                    return ok(
                        request_id,
                        {
                            "taskName": task_name,
                            "status": "completed",
                            "attempt": attempts,
                            "elapsedS": round(time.monotonic() - start, 1),
                            "events": events,
                            "result": result.data,
                        },
                    )
                if result.ok:
                    last_error = self._task_execute_logical_error(tool_name, result)
                    events.append(
                        {
                            "event": "tool_logical_failure",
                            "attempt": attempts,
                            "tool": tool_name,
                            "error": last_error,
                        }
                    )
                else:
                    last_error = (
                        result.error.message if result.error else "tool returned error"
                    )
                    events.append(
                        {
                            "event": "tool_error",
                            "attempt": attempts,
                            "error": last_error,
                        }
                    )
            except asyncio.TimeoutError:
                last_error = (
                    f"Timeout after {effective_timeout:.0f}s on attempt {attempts}"
                )
                events.append(
                    {
                        "event": "timeout",
                        "attempt": attempts,
                        "timeoutS": round(effective_timeout, 1),
                    }
                )

                if restart_unity_on_timeout and attempt < retry_count:
                    events.append(
                        {"event": "restart_unity_requested", "attempt": attempts}
                    )
                    try:
                        await self._restart_unity_connection(events)
                    except Exception as e:
                        events.append({"event": "restart_failed", "error": str(e)})
            except Exception as e:
                last_error = str(e)
                events.append(
                    {"event": "exception", "attempt": attempts, "error": last_error}
                )

        return fail(
            request_id,
            "TASK_FAILED",
            last_error or "Task did not complete successfully",
            {
                "taskName": task_name,
                "status": "failed",
                "attempts": attempts,
                "elapsedS": round(time.monotonic() - start, 1),
                "events": events,
            },
        )

    async def _restart_unity_connection(self, events: list[dict]) -> None:
        """Wait for Unity to disconnect and reconnect (soft restart via domain reload)."""
        import time

        start = time.monotonic()
        # Wait up to 90s for Unity to reconnect (it may already be reconnecting)
        for i in range(45):
            if self.server.session_manager.is_connected():
                events.append(
                    {
                        "event": "unity_reconnected",
                        "waitS": round(time.monotonic() - start, 1),
                    }
                )
                ready_r = await self.ensure_ready(timeout_s=60)
                if ready_r.ok and ready_r.data and ready_r.data.get("ready"):
                    events.append({"event": "unity_ready_after_restart"})
                    return
            await asyncio.sleep(2)
        events.append(
            {
                "event": "unity_reconnect_timeout",
                "waitS": round(time.monotonic() - start, 1),
            }
        )

    async def _dispatch_tool(self, tool_name: str, tool_args: dict) -> ToolResponse:
        """Route a public MCP tool name through the shared registry."""
        return await dispatch_public_tool(self, tool_name, tool_args)

    async def task_start(
        self,
        task_name: str,
        tool_name: str,
        tool_args: dict | None = None,
        timeout_s: float = 600,
        retry_count: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        task_id = new_id("task")
        state = {
            "taskId": task_id,
            "taskName": task_name,
            "toolName": tool_name,
            "status": "queued",
            "phase": "queued",
            "startedAt": now_ms(),
            "updatedAt": now_ms(),
            "endedAt": 0,
            "result": None,
            "error": None,
        }
        self._async_tasks[task_id] = state

        async def run() -> None:
            state["status"] = "running"
            state["phase"] = "executing"
            state["updatedAt"] = now_ms()
            result = await self.task_execute(
                task_name=task_name,
                tool_name=tool_name,
                tool_args=tool_args,
                timeout_s=timeout_s,
                max_total_s=timeout_s * max(1, retry_count + 1),
                retry_count=retry_count,
                restart_unity_on_timeout=False,
            )
            state["updatedAt"] = now_ms()
            state["endedAt"] = now_ms()
            if result.ok:
                state["status"] = "completed"
                state["phase"] = "completed"
                state["result"] = result.data
            else:
                state["status"] = "failed"
                state["phase"] = "failed"
                state["error"] = {
                    "code": result.error.code if result.error else "TASK_FAILED",
                    "message": result.error.message if result.error else "Task failed",
                    "detail": result.error.detail if result.error else {},
                }

        self._async_task_handles[task_id] = asyncio.create_task(run(), name=task_id)
        return ok(request_id, state.copy())

    async def task_status(self, task_id: str) -> ToolResponse:
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        result = state.copy()
        result["elapsedMs"] = max(0, (result["endedAt"] or now_ms()) - result["startedAt"])
        return ok(new_id("req"), result)

    async def task_cancel(self, task_id: str) -> ToolResponse:
        state = self._async_tasks.get(task_id)
        if state is None:
            return fail(new_id("req"), "TASK_NOT_FOUND", f"Task not found: {task_id}", {"taskId": task_id})
        handle = self._async_task_handles.get(task_id)
        if handle and not handle.done():
            handle.cancel()
        state["status"] = "cancelled"
        state["phase"] = "cancelled"
        state["updatedAt"] = now_ms()
        state["endedAt"] = now_ms()
        return ok(new_id("req"), state.copy())
