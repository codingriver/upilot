from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import json
import logging
import math
import os
import shlex
import subprocess
import sys
import time
import traceback
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
from .execution_service import (
    TypedValueValidationError,
    normalize_arguments,
    normalize_execution_error,
    normalize_variables,
    validate_finite_values,
    validate_typed_arguments,
    validate_typed_json_size,
    validate_typed_variables,
)

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
        type_name: str = "",
        method_name: str = "",
        parameters: list | None = None,
        is_static: bool = True,
        target_instance_path: str = "",
        target_static_type_name: str = "",
        target_static_member_path: str = "",
        async_after_sec: float = 25.0,
        operation_timeout_sec: float = 600.0,
        force_async: bool = False,
        expression: str = "",
        variables: dict | None = None,
        options: dict | None = None,
        kind: str = "auto",
        arguments: list | None = None,
        parameter_type_names: list[str] | None = None,
        generic_type_arguments: list[str] | None = None,
        target_handle: str = "",
        session_id: str = "",
        await_mode: str = "auto",
        await_timeout_ms: int = 3000,
        result_mode: str = "auto",
    ) -> ToolResponse:
        request_id = new_id("req")
        def reject(code: str, message: str, path: str = "") -> ToolResponse:
            detail = {
                "stage": "policy", "sideEffectsMayHaveOccurred": False,
                "nextAction": "Correct the request before calling once; no target was dispatched.",
            }
            if path:
                detail["path"] = path
            return fail(request_id, code, message, detail)
        try:
            for name, value in (("typeName", type_name), ("methodName", method_name), ("expression", expression),
                                ("kind", kind), ("sessionId", session_id), ("targetHandle", target_handle),
                                ("targetInstancePath", target_instance_path), ("targetStaticTypeName", target_static_type_name),
                                ("targetStaticMemberPath", target_static_member_path)):
                if not isinstance(value, str):
                    raise ValueError(f"{name} must be a string.")
            for name, value in (("parameterTypeNames", parameter_type_names), ("genericTypeArguments", generic_type_arguments)):
                if value is not None and (not isinstance(value, list)
                        or any(not isinstance(item, str) or not item.strip() for item in value)):
                    raise ValueError(f"{name} must be an array of nonempty type names.")
            if not isinstance(is_static, bool) or not isinstance(force_async, bool):
                raise ValueError("isStatic and forceAsync must be booleans.")
            await_mode = (await_mode or "auto").strip().lower()
            result_mode = (result_mode or "auto").strip().lower()
            if await_mode not in {"auto", "always", "never"}:
                return reject("INVALID_AWAIT_MODE", "awaitMode must be auto, always or never.")
            if result_mode not in {"auto", "inline", "handle", "legacystring"}:
                return reject("INVALID_RESULT_MODE", "resultMode must be auto, inline, handle or legacyString.")
            if result_mode == "handle" and not session_id.strip():
                return reject("SESSION_REQUIRED", "resultMode=handle requires a persistent session.")
            for name, value in (("asyncAfterSec", async_after_sec), ("operationTimeoutSec", operation_timeout_sec)):
                if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                    raise ValueError(f"{name} must be a finite number.")
            if operation_timeout_sec <= 0:
                raise ValueError("operationTimeoutSec must be positive.")
            if isinstance(await_timeout_ms, bool) or not isinstance(await_timeout_ms, int):
                raise ValueError("awaitTimeoutMs must be an integer.")
            if arguments is not None and not isinstance(arguments, list):
                raise ValueError("arguments must be an array.")
            if parameters is not None and not isinstance(parameters, list):
                raise ValueError("parameters must be an array.")
            if variables is not None and not isinstance(variables, dict):
                raise ValueError("variables must be an object.")
            if options is not None and not isinstance(options, dict):
                raise ValueError("options must be an object.")
            for name, value in (options or {}).items():
                if name == "resultMode":
                    if not isinstance(value, str) or value.strip().lower() not in {"string", "json", "type"}:
                        raise ValueError("options.resultMode must be string, json or type.")
                    continue
                if name not in {"timeoutMs", "maxStatements", "maxLoopIterations", "maxCalls",
                                "maxAllocations", "maxRecursion", "maxResultBytes", "maxAwaits", "maxArrayElements"}:
                    raise ValueError(f"options.{name} is not supported by the expression execution profile.")
                if name != "resultMode" and (isinstance(value, bool) or not isinstance(value, int)):
                    raise ValueError(f"options.{name} must be an integer budget.")
            validate_finite_values(parameters, "parameters")
            has_input_handle = validate_typed_arguments(arguments)
            has_input_handle |= validate_typed_variables(variables)
            arguments_json = normalize_arguments(arguments) if arguments is not None else ""
            variables_json = normalize_variables(variables)
            if arguments_json:
                validate_typed_json_size(arguments_json, "arguments")
            validate_typed_json_size(variables_json, "variables")
            if target_handle and (target_instance_path or target_static_type_name or target_static_member_path):
                return reject("REFLECTION_TARGET_CONFLICT", "targetHandle cannot be combined with instance/static member paths.")
            if (target_handle or has_input_handle) and not session_id.strip():
                return reject("SESSION_REQUIRED", "targetHandle and typed handle values require a persistent session.")
        except TypedValueValidationError as ex:
            return reject("INVALID_PARAMS", str(ex), ex.path)
        except (ValueError, TypeError, AttributeError) as ex:
            return reject("INVALID_PARAMS", str(ex))
        if arguments is not None and parameters:
            return reject("REFLECTION_ARGUMENTS_CONFLICT", "arguments and legacy parameters are mutually exclusive.")
        execution_kind, kind_error = self._resolve_reflection_execution_kind(
            kind=kind,
            type_name=type_name,
            method_name=method_name,
            expression=expression,
            target_handle=target_handle,
        )
        if kind_error:
            return fail(
                request_id,
                kind_error[0],
                kind_error[1],
                {
                    "stage": "policy",
                    "sideEffectsMayHaveOccurred": False,
                    "kind": kind or "auto",
                    "hasTypeName": bool(type_name.strip()),
                    "hasMethodName": bool(method_name.strip()),
                    "hasExpression": bool(expression.strip()),
                    "nextAction": (
                        "Pass typeName + methodName for a structured method call, or pass only expression "
                        "for one bounded expression. Do not provide both request shapes."
                    ),
                },
            )

        if execution_kind == "expression":
            if (arguments is not None or parameters or parameter_type_names or generic_type_arguments
                    or target_instance_path or target_static_type_name or target_static_member_path):
                return reject("AMBIGUOUS_REFLECTION_REQUEST", "Method arguments and targets cannot accompany an expression.")
            result = await self._reflection_expression_eval(
                code=expression,
                variables=variables,
                variables_json=variables_json,
                options=options,
                session_id=session_id,
                result_mode=result_mode,
            )
            return self._decorate_reflection_response(
                result,
                execution_kind="expression",
                engine_used="reflection-expression",
            )

        if options or variables:
            return reject("INVALID_PARAMS", "options and variables are expression-only parameters.")
        payload: dict = {
            "typeName": type_name,
            "methodName": method_name,
            "parameters": _normalize_reflection_parameters(parameters),
            "isStatic": is_static,
        }
        if arguments is not None:
            payload["argumentsJson"] = arguments_json
        if parameter_type_names:
            payload["parameterTypeNames"] = parameter_type_names
        if generic_type_arguments:
            payload["genericTypeArguments"] = generic_type_arguments
        if target_handle:
            payload["targetHandle"] = target_handle
        if session_id:
            payload["sessionId"] = session_id
        if await_mode != "auto":
            payload["awaitMode"] = await_mode
        if await_timeout_ms != 3000:
            payload["awaitTimeoutMs"] = await_timeout_ms
        if result_mode != "auto":
            payload["resultMode"] = result_mode
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
                return self._decorate_reflection_response(
                    await task,
                    execution_kind="method",
                    engine_used="structured-reflection",
                )
            try:
                return self._decorate_reflection_response(
                    await asyncio.wait_for(asyncio.shield(task), timeout=max(0.001, float(async_after_sec))),
                    execution_kind="method",
                    engine_used="structured-reflection",
                )
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
            "executionKind": "method",
            "engineUsed": "structured-reflection",
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
            "executionKind": "method",
            "engineUsed": "structured-reflection",
            "startedAt": job["startedAt"],
            "elapsedSec": round(elapsed_ms / 1000.0, 3),
            "executedOnce": True,
            "cancelSupported": False,
        }
        if not task.done():
            return ok(request_id, {**base, "status": "Running", "terminal": False})
        try:
            result = self._decorate_reflection_response(
                task.result(),
                execution_kind="method",
                engine_used="structured-reflection",
            )
        except asyncio.CancelledError:
            return ok(request_id, {**base, "status": "Canceled", "terminal": True})
        except Exception as ex:
            return fail(request_id, "REFLECTION_OPERATION_FAILED", str(ex), {
                **base,
                "status": "Failed",
                "terminal": True,
                "stage": "runtime",
                "sideEffectsMayHaveOccurred": True,
                "exceptionType": type(ex).__name__,
                "exceptionMessage": str(ex),
                "stackTrace": "".join(traceback.format_exception(type(ex), ex, ex.__traceback__))[:4096],
                "nextAction": "Inspect the original request and actual state; do not replay the target.",
            })
        if result.ok:
            return ok(request_id, {**base, "status": "Succeeded", "terminal": True, "result": result.data or {}, "timing": result.timing or {}})
        result_error = dict(result.error.detail or {}) if result.error else {}
        return fail(
            request_id,
            result.error.code if result.error else "REFLECTION_OPERATION_FAILED",
            result.error.message if result.error else "Reflection operation failed.",
            {
                **result_error,
                **base,
                "status": "Failed",
                "terminal": True,
                "resultError": result_error,
            },
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

    async def _reflection_expression_eval(
        self,
        code: str,
        variables: dict | None = None,
        variables_json: str | None = None,
        options: dict | None = None,
        session_id: str = "",
        result_mode: str = "auto",
    ) -> ToolResponse:
        # The public expression shape now uses the shared parser/interpreter with
        # the expression-only profile. reflection.eval remains a private bridge
        # compatibility command and is never exported as an MCP tool.
        return await self.dispatcher.call(new_id("req"), "csharp.eval", {
            "code": code,
            "mode": "expression",
            "sessionId": session_id,
            "variablesJson": variables_json if variables_json is not None else normalize_variables(variables),
            "imports": [],
            "executionBackend": "interpret",
            "limitsJson": _json_dumps_or_empty(options or {}),
            "resultMode": result_mode,
            "languageProfileMode": "reflection-expression",
        })

    @staticmethod
    def _resolve_reflection_execution_kind(
        *,
        kind: str,
        type_name: str,
        method_name: str,
        expression: str,
        target_handle: str = "",
    ) -> tuple[str, tuple[str, str] | None]:
        requested = (kind or "auto").strip().lower()
        has_type = bool(type_name.strip())
        has_method = bool(method_name.strip())
        has_expression = bool(expression.strip())
        has_target = bool(target_handle.strip())

        if requested not in {"auto", "method", "expression"}:
            return "", (
                "INVALID_REFLECTION_KIND",
                "kind must be one of: auto, method, expression.",
            )

        if requested == "auto":
            if has_expression and (has_type or has_method or has_target):
                return "", (
                    "AMBIGUOUS_REFLECTION_REQUEST",
                    "expression cannot be combined with typeName or methodName in auto mode.",
                )
            if has_expression:
                return "expression", None
            if has_method and (has_type or has_target):
                return "method", None
            return "", (
                "INVALID_REFLECTION_REQUEST",
                "Provide methodName with typeName or targetHandle, or provide one expression.",
            )

        if requested == "method":
            if has_expression:
                return "", (
                    "AMBIGUOUS_REFLECTION_REQUEST",
                    "kind=method cannot include expression.",
                )
            if not (has_method and (has_type or has_target)):
                return "", (
                    "INVALID_REFLECTION_REQUEST",
                    "kind=method requires methodName with typeName or targetHandle.",
                )
            return "method", None

        if has_type or has_method or has_target:
            return "", (
                "AMBIGUOUS_REFLECTION_REQUEST",
                "kind=expression cannot include typeName or methodName.",
            )
        if not has_expression:
            return "", (
                "INVALID_REFLECTION_REQUEST",
                "kind=expression requires expression.",
            )
        return "expression", None

    @staticmethod
    def _decorate_reflection_response(
        result: ToolResponse,
        *,
        execution_kind: str,
        engine_used: str,
    ) -> ToolResponse:
        result = normalize_execution_error(result)
        if result.ok:
            data = dict(result.data or {})
            data.setdefault("executionKind", execution_kind)
            data.setdefault("engineUsed", engine_used)
            result.data = data
        elif result.error is not None:
            detail = dict(result.error.detail or {})
            detail.setdefault("executionKind", execution_kind)
            detail.setdefault("engineUsed", engine_used)
            result.error.detail = detail
        return result
