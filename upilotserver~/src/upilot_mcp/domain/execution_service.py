from __future__ import annotations

import json
import hashlib
from typing import Any

from ..models import ToolResponse
from ..protocol import new_id


def _compact_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _typed_value(value: Any) -> dict[str, Any]:
    if isinstance(value, dict) and value.get("kind") in {
        "literal", "null", "handle", "unityObject", "unityobject", "array", "type"
    }:
        result = dict(value)
        if "value" in result and "valueJson" not in result:
            result["valueJson"] = _compact_json(result.pop("value"))
        if result.get("kind", "").lower() == "array":
            result["items"] = [_typed_value(item) for item in result.get("items", [])]
        return result
    if value is None:
        return {"kind": "null", "valueJson": "null"}
    if isinstance(value, bool):
        type_name = "System.Boolean"
    elif isinstance(value, int):
        type_name = "System.Int32" if -(2**31) <= value <= 2**31 - 1 else "System.Int64"
    elif isinstance(value, float):
        type_name = "System.Double"
    elif isinstance(value, str):
        type_name = "System.String"
    else:
        type_name = ""
    return {"kind": "literal", "typeName": type_name, "valueJson": _compact_json(value)}


def normalize_arguments(arguments: list[Any] | None) -> str:
    items: list[dict[str, Any]] = []
    for argument in arguments or []:
        if isinstance(argument, dict) and (
            any(key in argument for key in ("name", "direction"))
            or ("value" in argument and set(argument).issubset({"name", "direction", "value"}))
        ):
            item = {
                "name": str(argument.get("name", "")),
                "direction": str(argument.get("direction", "in")),
                "value": _typed_value(argument.get("value")),
            }
        else:
            item = {"name": "", "direction": "in", "value": _typed_value(argument)}
        items.append(item)
    return _compact_json({"items": items})


def normalize_variables(variables: dict[str, Any] | None) -> str:
    return _compact_json({
        "items": [
            {"name": str(name), "value": _typed_value(value)}
            for name, value in (variables or {}).items()
        ]
    })


def normalize_execution_error(response: ToolResponse) -> ToolResponse:
    """Expose legacy JSON-encoded detail fields as real objects during migration."""
    if response.ok or response.error is None:
        return response
    detail = response.error.detail
    nested = detail.get("detail") if isinstance(detail.get("detail"), dict) else detail
    for legacy, structured, fallback in (
        ("sourceSpanJson", "sourceSpan", None),
        ("diagnosticsJson", "diagnostics", []),
        ("candidatesJson", "candidates", []),
    ):
        if structured in nested and nested[structured] not in (None, ""):
            continue
        raw = nested.get(legacy)
        if isinstance(raw, str) and raw:
            try:
                nested[structured] = json.loads(raw)
                continue
            except (TypeError, ValueError, json.JSONDecodeError):
                pass
        if fallback is not None:
            nested[structured] = fallback
    if nested is not detail:
        for key in ("sourceSpan", "diagnostics", "candidates"):
            if key in nested:
                detail[key] = nested[key]
    return response


class ExecutionDomainService:
    async def execution_session(
        self,
        action: str,
        session_id: str = "",
        title: str = "",
        ttl_sec: int = 600,
        max_handles: int = 256,
        max_dynamic_types: int = 32,
        max_callbacks: int = 64,
        max_async_operations: int = 64,
    ) -> ToolResponse:
        return normalize_execution_error(await self.dispatcher.call(new_id("req"), "execution.session", {
            "action": action,
            "sessionId": session_id,
            "title": title,
            "ttlSec": ttl_sec,
            "maxHandles": max_handles,
            "maxDynamicTypes": max_dynamic_types,
            "maxCallbacks": max_callbacks,
            "maxAsyncOperations": max_async_operations,
        }))

    async def csharp_eval(
        self,
        code: str,
        mode: str = "auto",
        session_id: str = "",
        variables: dict[str, Any] | None = None,
        imports: list[str] | None = None,
        execution_backend: str = "auto",
        limits: dict[str, Any] | None = None,
        result_mode: str = "auto",
    ) -> ToolResponse:
        return normalize_execution_error(await self.dispatcher.call(new_id("req"), "csharp.eval", {
            "code": code,
            "mode": mode,
            "sessionId": session_id,
            "variablesJson": normalize_variables(variables),
            "imports": imports or [],
            "executionBackend": execution_backend,
            "limitsJson": _compact_json(limits or {}),
            "resultMode": result_mode,
        }, timeout_ms=max(30000, min(35000, int((limits or {}).get("timeoutMs", 3000)) + 5000))))

    async def reflection_emit_type(
        self,
        session_id: str,
        spec: dict[str, Any],
        cache_policy: str = "specHash",
        name_conflict_policy: str = "reject",
        create_instance: bool = False,
        constructor_arguments: list[Any] | None = None,
    ) -> ToolResponse:
        if cache_policy != "specHash":
            from ..responses import fail
            return fail(new_id("req"), "EMIT_INVALID_CACHE_POLICY", "cachePolicy must be specHash.")
        canonical_spec = _compact_json(spec)
        spec_hash = hashlib.sha256(canonical_spec.encode("utf-8")).hexdigest()
        return normalize_execution_error(await self.dispatcher.call(new_id("req"), "reflection.emitType", {
            "sessionId": session_id,
            "specJson": canonical_spec,
            "specHash": spec_hash,
            "cachePolicy": cache_policy,
            "nameConflictPolicy": name_conflict_policy,
            "createInstance": create_instance,
            "constructorArgumentsJson": normalize_arguments(constructor_arguments),
        }))

    async def execution_capabilities(self) -> ToolResponse:
        return normalize_execution_error(await self.dispatcher.call(new_id("req"), "execution.capabilities", {}))
