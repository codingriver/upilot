from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Awaitable, Callable
import inspect
import re

from .models import ToolResponse
from .protocol import new_id
from .responses import fail
from .config import CONFIG, refresh_config_if_changed


ToolHandler = Callable[..., Awaitable[ToolResponse]]
REGISTRY_VERSION = 4


@dataclass(frozen=True, slots=True)
class ToolDescriptor:
    name: str
    facade_method: str
    category: str
    idempotent: bool = True
    destructive: bool = False
    requires_unity_connection: bool = True
    requires_write_access: bool = False
    play_mode_policy: str = "allowed"
    feature: str = "core"
    timeout_ms: int = 30000
    capability_requirements: tuple[str, ...] = ()
    aliases: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["registered"] = True
        data["requiresUnityConnection"] = data.pop("requires_unity_connection")
        data["requiresWriteAccess"] = data.pop("requires_write_access")
        data["capabilityRequirements"] = list(data.pop("capability_requirements"))
        data["aliases"] = list(data["aliases"])
        return data


class ToolRegistry:
    def __init__(self) -> None:
        self._items: dict[str, ToolDescriptor] = {}

    def register(self, descriptor: ToolDescriptor) -> None:
        self._items[descriptor.name] = descriptor

    def resolve(self, public_name: str) -> ToolDescriptor | None:
        return self._items.get(public_name)

    def list(self) -> list[ToolDescriptor]:
        return sorted(self._items.values(), key=lambda item: item.name)

    def find(
        self,
        query: str = "",
        category: str = "",
        availability: str = "all",
        limit: int = 20,
        *,
        flow_enabled: bool = False,
        connected: bool | None = None,
        server_ready: bool | None = None,
        write_access_approved: bool | None = None,
    ) -> list[dict[str, Any]]:
        query_tokens = _tokenize(query)
        query_key = query.strip().lower()
        category_key = category.strip().lower()
        availability_key = availability.strip().lower().replace("_", "")
        if availability_key in {"callablenow", "callable"}:
            availability_key = "callable"
        scored_results: list[tuple[int, dict[str, Any]]] = []
        for item in self.list():
            available = item.feature == "core" or flow_enabled
            callable_now = available
            unavailable_reason = ""
            next_action = ""

            if not available and item.feature == "flow":
                unavailable_reason = "FEATURE_DISABLED"
                next_action = "Enable UPilot Flow in project configuration, then restart the MCP client."

            if available and item.requires_unity_connection:
                connected_now = bool(connected)
                ready_now = bool(server_ready)
                if connected is not None and server_ready is not None and not (connected_now and ready_now):
                    callable_now = False
                    unavailable_reason = "UNITY_NOT_CONNECTED" if not connected_now else "UNITY_NOT_READY"
                    next_action = "Open the Unity project and wait for the UPilot Bridge connection."

            if available and item.requires_write_access and write_access_approved is not None and not write_access_approved:
                callable_now = False
                unavailable_reason = "WRITE_ACCESS_NOT_APPROVED"
                next_action = "Enable project write access in the Unity UPilot setup or .upilot/config.json."

            if availability_key == "available" and not available:
                continue
            if availability_key == "unavailable" and available:
                continue
            if availability_key == "callable" and not callable_now:
                continue
            if availability_key == "blocked" and callable_now:
                continue
            if category_key and item.category.lower() != category_key:
                continue

            score = _match_score(item, query_tokens)
            if query_tokens and score <= 0:
                continue
            data = item.to_dict()
            data["available"] = available
            data["callableNow"] = callable_now
            data["exactMatch"] = bool(
                query_key
                and (
                    item.name.lower() == query_key
                    or item.facade_method.lower() == query_key
                    or any(alias.lower() == query_key for alias in item.aliases)
                )
            )
            if unavailable_reason:
                data["unavailableReason"] = unavailable_reason
            if next_action:
                data["nextAction"] = next_action
            if score:
                data["matchScore"] = score
            scored_results.append((score + (1000 if data["exactMatch"] else 0), data))
            if len(scored_results) >= max(1, min(limit, 200)) and not query_tokens:
                break
        scored_results.sort(key=lambda pair: (-pair[0], pair[1]["name"]))
        return [data for _, data in scored_results[: max(1, min(limit, 200))]]


REGISTRY = ToolRegistry()


def infer_facade_method(public_name: str) -> str:
    if public_name == "reflection_eval":
        return "reflection_eval"
    if public_name.startswith("unity_"):
        return public_name[len("unity_") :]
    return public_name


def infer_category(public_name: str) -> str:
    key = public_name.removeprefix("unity_")
    if key.startswith("upilot_flow_"):
        return "flow"
    return key.split("_", 1)[0]


def infer_requires_unity_connection(public_name: str) -> bool:
    return public_name not in {
        "unity_open_editor",
        "unity_mcp_status",
        "unity_capabilities_get",
        "unity_tools_find",
        "unity_client_config_diagnose",
    }


def _tokenize(value: str) -> list[str]:
    return [
        token
        for token in re.split(r"[^a-z0-9]+", value.strip().lower().replace("_", " "))
        if token
    ]


def _match_score(item: ToolDescriptor, query_tokens: list[str]) -> int:
    if not query_tokens:
        return 0
    haystack_values = [
        item.name,
        item.facade_method,
        item.category,
        *item.capability_requirements,
        *item.aliases,
    ]
    haystack_tokens: set[str] = set()
    haystack_strings: list[str] = []
    for value in haystack_values:
        if not value:
            continue
        lowered = value.lower()
        haystack_strings.append(lowered)
        haystack_tokens.update(_tokenize(lowered))
    score = 0
    for token in query_tokens:
        if token in haystack_tokens:
            score += 3
        elif any(token in value for value in haystack_strings):
            score += 1
    return score


def register_public_tool(
    name: str,
    *,
    facade_method: str | None = None,
    category: str | None = None,
    idempotent: bool = True,
    destructive: bool = False,
    requires_unity_connection: bool | None = None,
    requires_write_access: bool | None = None,
    play_mode_policy: str = "allowed",
    feature: str = "core",
    timeout_ms: int = 30000,
    capability_requirements: tuple[str, ...] = (),
    aliases: tuple[str, ...] = (),
) -> None:
    REGISTRY.register(
        ToolDescriptor(
            name=name,
            facade_method=facade_method or infer_facade_method(name),
            category=category or infer_category(name),
            idempotent=idempotent,
            destructive=destructive,
            requires_unity_connection=(
                infer_requires_unity_connection(name)
                if requires_unity_connection is None
                else requires_unity_connection
            ),
            requires_write_access=destructive if requires_write_access is None else requires_write_access,
            play_mode_policy=play_mode_policy,
            feature=feature,
            timeout_ms=timeout_ms,
            capability_requirements=capability_requirements,
            aliases=aliases,
        )
    )


async def dispatch_public_tool(facade: Any, public_name: str, args: dict[str, Any]) -> ToolResponse:
    refresh_config_if_changed()
    descriptor = REGISTRY.resolve(public_name)
    if descriptor is None:
        return fail(new_id("req"), "UNKNOWN_TOOL", f"Unknown MCP tool: {public_name}", {"tool": public_name})
    if descriptor.destructive and not CONFIG.write_access_approved:
        return fail(
            new_id("req"),
            "WRITE_ACCESS_NOT_APPROVED",
            "UPilot is in safe mode. Enable project write access in the Unity UPilot first setup or .upilot/config.json before using this tool.",
            {"tool": public_name, "configKey": "safety.writeAccessApproved"},
        )
    if descriptor.feature == "flow" and not CONFIG.flow_enabled:
        return fail(
            new_id("req"),
            "FEATURE_DISABLED",
            "UPilot Flow is disabled by project configuration",
            {"tool": public_name, "enableCondition": "features.flow.enabled=true, Unity 6+, required packages, then restart the MCP client"},
        )
    method = getattr(facade, descriptor.facade_method, None)
    if method is None:
        return fail(
            new_id("req"),
            "TOOL_HANDLER_MISSING",
            f"MCP tool has no facade handler: {public_name}",
            {"tool": public_name, "facadeMethod": descriptor.facade_method},
        )
    normalized_args, argument_error = _normalize_proxy_arguments(method, args)
    if argument_error:
        return fail(
            new_id("req"),
            "INVALID_TOOL_ARGUMENTS",
            f"Invalid arguments for MCP tool: {public_name}",
            {
                "tool": public_name,
                "facadeMethod": descriptor.facade_method,
                **argument_error,
            },
        )
    return await method(**normalized_args)


def proxy_argument_schema(method: Any) -> list[dict[str, Any]]:
    """Describe the arguments accepted by unity_tool_call for one bound facade method."""
    try:
        signature = inspect.signature(method)
    except (TypeError, ValueError):
        return []
    result: list[dict[str, Any]] = []
    for parameter in signature.parameters.values():
        if parameter.name == "self" or parameter.kind in {
            inspect.Parameter.VAR_POSITIONAL,
            inspect.Parameter.VAR_KEYWORD,
        }:
            continue
        entry: dict[str, Any] = {
            "name": parameter.name,
            "camelCase": _snake_to_camel(parameter.name),
            "required": parameter.default is inspect.Parameter.empty,
        }
        if parameter.default is not inspect.Parameter.empty:
            entry["default"] = parameter.default
        result.append(entry)
    return result


def _normalize_proxy_arguments(
    method: Any, args: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any] | None]:
    try:
        signature = inspect.signature(method)
    except (TypeError, ValueError):
        return dict(args), None

    parameters = {
        name: parameter
        for name, parameter in signature.parameters.items()
        if name != "self"
    }
    accepts_kwargs = any(
        parameter.kind is inspect.Parameter.VAR_KEYWORD
        for parameter in parameters.values()
    )
    folded_names: dict[str, list[str]] = {}
    for name, parameter in parameters.items():
        if parameter.kind in {
            inspect.Parameter.VAR_POSITIONAL,
            inspect.Parameter.VAR_KEYWORD,
        }:
            continue
        folded_names.setdefault(_fold_argument_name(name), []).append(name)

    normalized: dict[str, Any] = {}
    unknown: list[str] = []
    duplicates: list[str] = []
    for supplied_name, value in args.items():
        target_name = supplied_name if supplied_name in parameters else ""
        if not target_name:
            matches = folded_names.get(_fold_argument_name(supplied_name), [])
            if len(matches) == 1:
                target_name = matches[0]
        if not target_name and accepts_kwargs:
            target_name = supplied_name
        if not target_name:
            unknown.append(supplied_name)
            continue
        if target_name in normalized:
            duplicates.append(supplied_name)
            continue
        normalized[target_name] = value

    if unknown or duplicates:
        return normalized, {
            "unknownArguments": unknown,
            "duplicateArguments": duplicates,
            "expectedArguments": proxy_argument_schema(method),
            "nextAction": "Use the target tool argument names returned by unity_tools_find.",
        }
    try:
        signature.bind(**normalized)
    except TypeError as exc:
        return normalized, {
            "bindingError": str(exc),
            "expectedArguments": proxy_argument_schema(method),
            "nextAction": "Supply all required target tool arguments using the returned schema.",
        }
    return normalized, None


def _fold_argument_name(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", str(value or "").lower())


def _snake_to_camel(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in tail)
