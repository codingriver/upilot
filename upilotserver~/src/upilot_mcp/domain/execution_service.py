from __future__ import annotations

import json
import hashlib
import math
from typing import Any

from ..models import ToolResponse
from ..protocol import new_id
from ..responses import fail


_TYPED_JSON_MAX_BYTES = 1024 * 1024
_TYPED_ARRAY_TYPES = {
    "System.Boolean[]", "System.Char[]", "System.String[]", "System.SByte[]",
    "System.Byte[]", "System.Int16[]", "System.UInt16[]", "System.Int32[]",
    "System.UInt32[]", "System.Int64[]", "System.UInt64[]", "System.Single[]",
    "System.Double[]", "System.Decimal[]",
}
_EXECUTION_LIMIT_FIELDS = {
    "timeoutMs", "maxStatements", "maxLoopIterations", "maxCalls", "maxAllocations",
    "maxRecursion", "maxResultBytes", "maxAwaits", "maxArrayElements",
}


class TypedValueValidationError(ValueError):
    def __init__(self, path: str, message: str) -> None:
        super().__init__(f"{path}: {message}")
        self.path = path


def _compact_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def validate_finite_values(value: object, path: str) -> None:
    if isinstance(value, float) and not math.isfinite(value):
        raise TypedValueValidationError(path, "numeric values must be finite.")
    if isinstance(value, dict):
        for key, child in value.items():
            validate_finite_values(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            validate_finite_values(child, f"{path}[{index}]")


def validate_typed_value(value: object, path: str, depth: int = 0) -> bool:
    """Validate only TypedValue nesting; JSON envelope depth is a separate limit."""
    validate_finite_values(value, path)
    if not isinstance(value, dict) or "kind" not in value:
        return False
    if depth > 64:
        raise TypedValueValidationError(path, "typed value nesting exceeds 64.")
    kind = value["kind"]
    if not isinstance(kind, str):
        raise TypedValueValidationError(f"{path}.kind", "typed value kind must be a string.")
    kind = kind.strip().lower()
    if kind not in {"literal", "null", "handle", "type", "unityobject", "array"}:
        raise TypedValueValidationError(f"{path}.kind", "unsupported typed value kind.")
    if "typeName" in value and not isinstance(value["typeName"], str):
        raise TypedValueValidationError(f"{path}.typeName", "typeName must be a string.")
    if "valueJson" in value:
        if not isinstance(value["valueJson"], str):
            raise TypedValueValidationError(f"{path}.valueJson", "valueJson must be a JSON string.")
        try:
            parsed_value = json.loads(
                value["valueJson"],
                parse_constant=lambda token: (_ for _ in ()).throw(ValueError(token)),
            )
        except (TypeError, ValueError, json.JSONDecodeError):
            raise TypedValueValidationError(f"{path}.valueJson", "valueJson must contain finite JSON.") from None
        if (value.get("typeName") in {"System.Single", "System.Double"}
                and isinstance(parsed_value, str)
                and parsed_value.strip().lower() in {"nan", "infinity", "+infinity", "-infinity"}):
            raise TypedValueValidationError(f"{path}.valueJson", "numeric values must be finite.")
    if kind == "handle" and (not isinstance(value.get("handle"), str) or not value["handle"].strip()):
        raise TypedValueValidationError(f"{path}.handle", "a handle string is required.")
    if kind == "array":
        type_name = value.get("typeName")
        if not isinstance(type_name, str) or type_name not in _TYPED_ARRAY_TYPES:
            raise TypedValueValidationError(f"{path}.typeName", "a supported one-dimensional primitive array typeName is required.")
        if not isinstance(value.get("items"), list):
            raise TypedValueValidationError(f"{path}.items", "an array items value is required.")
        for index, child in enumerate(value["items"]):
            child_path = f"{path}.items[{index}]"
            validate_typed_value(child, child_path, depth + 1)
            child_kind = child.get("kind", "literal").strip().lower() if isinstance(child, dict) else ""
            if child_kind not in {"literal", "null"}:
                raise TypedValueValidationError(child_path + ".kind", "primitive array elements must be literal or null values.")
            is_null = child_kind == "null" or (
                child_kind == "literal" and (
                    child.get("value") is None if "value" in child
                    else str(child.get("valueJson", "null")).strip() == "null"
                )
            )
            if is_null and type_name != "System.String[]":
                raise TypedValueValidationError(child_path, "only System.String[] accepts null array elements.")
    elif "items" in value:
        raise TypedValueValidationError(f"{path}.items", "items is valid only for kind=array.")
    return kind == "handle"


def validate_typed_arguments(arguments: list | None) -> bool:
    has_handle = False
    for index, argument in enumerate(arguments or []):
        path = f"arguments[{index}]"
        if isinstance(argument, dict) and (
            any(key in argument for key in ("name", "direction"))
            or ("value" in argument and set(argument).issubset({"name", "direction", "value"}))
        ):
            if "direction" in argument and argument["direction"] not in {"in", "ref", "out"}:
                raise TypedValueValidationError(f"{path}.direction", "expected in, ref or out.")
            has_handle |= validate_typed_value(argument.get("value"), f"{path}.value")
        else:
            has_handle |= validate_typed_value(argument, path)
    return has_handle


def validate_typed_variables(variables: dict | None) -> bool:
    has_handle = False
    for name, value in (variables or {}).items():
        if not isinstance(name, str) or not name.strip():
            raise TypedValueValidationError("variables", "variable names must be nonempty strings.")
        has_handle |= validate_typed_value(value, f"variables.{name}")
    return has_handle


def validate_typed_json_size(serialized: str, field: str) -> None:
    actual_bytes = len(serialized.encode("utf-8"))
    if actual_bytes > _TYPED_JSON_MAX_BYTES:
        raise TypedValueValidationError(field, f"typed JSON exceeds the 1 MiB UTF-8 limit ({actual_bytes} bytes).")


def _validate_execution_limits(limits: dict[str, Any] | None) -> None:
    if limits is not None and not isinstance(limits, dict):
        raise ValueError("limits must be an object.")
    for name, value in (limits or {}).items():
        if name not in _EXECUTION_LIMIT_FIELDS:
            raise ValueError(f"limits.{name} is not supported by the execution profile.")
        if isinstance(value, bool) or not isinstance(value, int):
            raise ValueError(f"limits.{name} must be an integer budget.")


def _typed_value(value: Any) -> dict[str, Any]:
    kind = value.get("kind") if isinstance(value, dict) else None
    normalized_kind = kind.strip().lower() if isinstance(kind, str) else ""
    if normalized_kind in {"literal", "null", "handle", "unityobject", "array", "type"}:
        result = dict(value)
        result["kind"] = normalized_kind
        if "value" in result and "valueJson" not in result:
            result["valueJson"] = _compact_json(result.pop("value"))
        if normalized_kind == "array":
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
        ("lastCompletedSpanJson", "lastCompletedSpan", None),
        ("diagnosticsJson", "diagnostics", []),
        ("candidatesJson", "candidates", []),
        ("executionDiagnosticsJson", "executionDiagnostics", {}),
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
        for key in ("sourceSpan", "lastCompletedSpan", "diagnostics", "candidates", "executionDiagnostics"):
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
        request_id = new_id("req")
        def reject(code: str, message: str, path: str = "") -> ToolResponse:
            detail = {
                "stage": "policy",
                "sideEffectsMayHaveOccurred": False,
                "nextAction": "Correct the request before calling once; no target was dispatched.",
            }
            if path:
                detail["path"] = path
            return fail(request_id, code, message, detail)
        try:
            if not isinstance(code, str) or not isinstance(mode, str) or not isinstance(session_id, str):
                raise ValueError("code, mode, and sessionId must be strings.")
            if not isinstance(execution_backend, str) or not isinstance(result_mode, str):
                raise ValueError("executionBackend and resultMode must be strings.")
            if mode.strip().lower() not in {"auto", "expression", "statements"}:
                raise ValueError("mode must be auto, expression, or statements.")
            if execution_backend.strip().lower() not in {"auto", "interpret", "emit"}:
                raise ValueError("executionBackend must be auto, interpret, or emit.")
            normalized_result_mode = result_mode.strip().lower()
            if normalized_result_mode not in {"auto", "inline", "handle", "legacystring"}:
                raise ValueError("resultMode must be auto, inline, handle, or legacyString.")
            if normalized_result_mode == "handle" and not session_id.strip():
                return reject("SESSION_REQUIRED", "resultMode=handle requires a persistent session.")
            if variables is not None and not isinstance(variables, dict):
                raise ValueError("variables must be an object.")
            if imports is not None and (not isinstance(imports, list)
                                        or any(not isinstance(item, str) or not item.strip() for item in imports)):
                raise ValueError("imports must be an array of nonempty namespace strings.")
            _validate_execution_limits(limits)
            validate_finite_values(variables, "variables")
            has_input_handle = validate_typed_variables(variables)
            if has_input_handle and not session_id.strip():
                return reject("SESSION_REQUIRED", "Typed handle variables require a persistent session.")
            variables_json = normalize_variables(variables)
            validate_typed_json_size(variables_json, "variables")
        except TypedValueValidationError as ex:
            return reject("INVALID_PARAMS", str(ex), ex.path)
        except (TypeError, ValueError, AttributeError) as ex:
            return reject("INVALID_PARAMS", str(ex))
        return normalize_execution_error(await self.dispatcher.call(request_id, "csharp.eval", {
            "code": code,
            "mode": mode.strip().lower(),
            "sessionId": session_id,
            "variablesJson": variables_json,
            "imports": imports or [],
            "executionBackend": execution_backend.strip().lower(),
            "limitsJson": _compact_json(limits or {}),
            "resultMode": normalized_result_mode,
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
        request_id = new_id("req")
        def reject(code: str, message: str, path: str = "") -> ToolResponse:
            detail = {
                "stage": "policy",
                "sideEffectsMayHaveOccurred": False,
                "nextAction": "Correct the request before calling once; no type was emitted.",
            }
            if path:
                detail["path"] = path
            return fail(request_id, code, message, detail)
        try:
            if not isinstance(session_id, str) or not session_id.strip():
                return reject("SESSION_REQUIRED", "reflection_emit_type requires a persistent session.")
            if not isinstance(spec, dict):
                raise ValueError("spec must be an object.")
            if cache_policy != "specHash":
                return reject("EMIT_INVALID_CACHE_POLICY", "cachePolicy must be specHash.")
            if not isinstance(name_conflict_policy, str) or name_conflict_policy not in {"reject", "hashSuffix"}:
                raise ValueError("nameConflictPolicy must be reject or hashSuffix.")
            if not isinstance(create_instance, bool):
                raise ValueError("createInstance must be a boolean.")
            if constructor_arguments is not None and not isinstance(constructor_arguments, list):
                raise ValueError("constructorArguments must be an array.")
            validate_finite_values(constructor_arguments, "arguments")
            validate_typed_arguments(constructor_arguments)
            constructor_arguments_json = normalize_arguments(constructor_arguments)
            validate_typed_json_size(constructor_arguments_json, "arguments")
            canonical_spec = _compact_json(spec)
        except TypedValueValidationError as ex:
            return reject("INVALID_PARAMS", str(ex), ex.path)
        except (TypeError, ValueError, AttributeError) as ex:
            return reject("INVALID_PARAMS", str(ex))
        spec_hash = hashlib.sha256(canonical_spec.encode("utf-8")).hexdigest()
        return normalize_execution_error(await self.dispatcher.call(request_id, "reflection.emitType", {
            "sessionId": session_id,
            "specJson": canonical_spec,
            "specHash": spec_hash,
            "cachePolicy": cache_policy,
            "nameConflictPolicy": name_conflict_policy,
            "createInstance": create_instance,
            "constructorArgumentsJson": constructor_arguments_json,
        }))

    async def execution_capabilities(self) -> ToolResponse:
        return normalize_execution_error(await self.dispatcher.call(new_id("req"), "execution.capabilities", {}))
