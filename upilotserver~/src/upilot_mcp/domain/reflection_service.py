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

class ReflectionDomainService:
    async def reflection_find(
        self, type_name: str, method_name: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"typeName": type_name}
        if method_name:
            payload["methodName"] = method_name
        return await self.dispatcher.call(request_id, "reflection.find", payload)

    async def type_exists(self, type_name: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "reflection.typeExists", {"typeName": type_name}
        )

    async def reflection_call(
        self,
        type_name: str,
        method_name: str,
        parameters: list | None = None,
        is_static: bool = True,
        target_instance_path: str = "",
        target_static_type_name: str = "",
        target_static_member_path: str = "",
        async_after_sec: float = 25.0,
        operation_timeout_sec: float = 600.0,
        force_async: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "typeName": type_name,
            "methodName": method_name,
            "parameters": _normalize_reflection_parameters(parameters),
            "isStatic": is_static,
        }
        if target_instance_path:
            payload["targetInstancePath"] = target_instance_path
        if target_static_type_name:
            payload["targetStaticTypeName"] = target_static_type_name
        if target_static_member_path:
            payload["targetStaticMemberPath"] = target_static_member_path
        task = asyncio.create_task(self.dispatcher.call(
            request_id,
            "reflection.call",
            payload,
            timeout_ms=max(30000, int(float(operation_timeout_sec) * 1000)),
        ))
        if not force_async:
            if async_after_sec <= 0:
                return await task
            try:
                return await asyncio.wait_for(asyncio.shield(task), timeout=max(0.001, float(async_after_sec)))
            except asyncio.TimeoutError:
                pass

        operation_id = new_id("reflection-op")
        jobs = getattr(self, "_reflection_background_jobs", None)
        if jobs is None:
            jobs = {}
            setattr(self, "_reflection_background_jobs", jobs)
        jobs[operation_id] = {
            "operationId": operation_id,
            "requestId": request_id,
            "typeName": type_name,
            "methodName": method_name,
            "startedAt": now_ms(),
            "task": task,
        }
        return ok(request_id, {
            "status": "Running",
            "operationId": operation_id,
            "typeName": type_name,
            "methodName": method_name,
            "executedOnce": True,
            "cancelSupported": False,
            "nextAction": "Call unity_reflection_operation_status or unity_reflection_operation_wait with operationId.",
        })

    async def reflection_operation_status(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        jobs = getattr(self, "_reflection_background_jobs", {})
        job = jobs.get(operation_id)
        if job is None:
            return fail(request_id, "REFLECTION_OPERATION_NOT_FOUND", f"Reflection operation not found: {operation_id}")
        task = job["task"]
        elapsed_ms = max(0, now_ms() - int(job["startedAt"]))
        base = {
            "operationId": operation_id,
            "typeName": job["typeName"],
            "methodName": job["methodName"],
            "startedAt": job["startedAt"],
            "elapsedSec": round(elapsed_ms / 1000.0, 3),
            "executedOnce": True,
            "cancelSupported": False,
        }
        if not task.done():
            return ok(request_id, {**base, "status": "Running", "terminal": False})
        try:
            result = task.result()
        except asyncio.CancelledError:
            return ok(request_id, {**base, "status": "Canceled", "terminal": True})
        except Exception as ex:
            return fail(request_id, "REFLECTION_OPERATION_FAILED", str(ex), {**base, "status": "Failed", "terminal": True})
        if result.ok:
            return ok(request_id, {**base, "status": "Succeeded", "terminal": True, "result": result.data or {}, "timing": result.timing or {}})
        return fail(
            request_id,
            result.error.code if result.error else "REFLECTION_OPERATION_FAILED",
            result.error.message if result.error else "Reflection operation failed.",
            {**base, "status": "Failed", "terminal": True, "resultError": result.error.detail if result.error else {}},
        )

    async def reflection_operation_wait(
        self,
        operation_id: str,
        timeout_sec: float = 30.0,
        poll_interval_sec: float = 0.5,
    ) -> ToolResponse:
        deadline = time.monotonic() + max(0.1, float(timeout_sec))
        while True:
            result = await self.reflection_operation_status(operation_id)
            detail = result.data if result.ok else (result.error.detail if result.error else {})
            if isinstance(detail, dict) and detail.get("terminal"):
                return result
            if time.monotonic() >= deadline:
                if result.ok and isinstance(result.data, dict):
                    result.data["waitWindowElapsed"] = True
                    result.data["nextAction"] = "Call unity_reflection_operation_wait again; the original call is still running."
                return result
            await asyncio.sleep(max(0.05, min(float(poll_interval_sec), deadline - time.monotonic())))

    async def reflection_operation_cancel(self, operation_id: str) -> ToolResponse:
        request_id = new_id("req")
        jobs = getattr(self, "_reflection_background_jobs", {})
        job = jobs.get(operation_id)
        if job is None:
            return fail(request_id, "REFLECTION_OPERATION_NOT_FOUND", f"Reflection operation not found: {operation_id}")
        if job["task"].done():
            return await self.reflection_operation_status(operation_id)
        return fail(
            request_id,
            "CANCEL_UNSUPPORTED",
            "An arbitrary Unity main-thread reflection call cannot be safely interrupted after invocation.",
            {
                "operationId": operation_id,
                "status": "Running",
                "terminal": False,
                "cancelSupported": False,
                "executionMayContinue": True,
                "nextAction": "Wait for the result. For cancellable work, expose project Start/Status/Cancel methods and use unity_operation_start.",
            },
        )

    async def reflection_eval(
        self,
        code: str,
        variables: dict | None = None,
        options: dict | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"code": code}
        if variables is not None:
            payload["variablesJson"] = _json_dumps_or_empty(variables)
        if options is not None:
            payload["optionsJson"] = _json_dumps_or_empty(options)
        return await self.dispatcher.call(request_id, "reflection.eval", payload)
