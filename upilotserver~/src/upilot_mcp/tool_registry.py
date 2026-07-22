from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Awaitable, Callable
import re

from .models import ToolResponse
from .protocol import new_id
from .responses import fail
from .config import CONFIG


ToolHandler = Callable[..., Awaitable[ToolResponse]]
REGISTRY_VERSION = 2


@dataclass(frozen=True, slots=True)
class ToolDescriptor:
    name: str
    facade_method: str
    category: str
    idempotent: bool = True
    destructive: bool = False
    play_mode_policy: str = "allowed"
    feature: str = "core"
    timeout_ms: int = 30000
    capability_requirements: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


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
    ) -> list[dict[str, Any]]:
        query_key = query.strip().lower()
        category_key = category.strip().lower()
        results: list[dict[str, Any]] = []
        for item in self.list():
            available = item.feature == "core" or flow_enabled
            if availability == "available" and not available:
                continue
            if availability == "unavailable" and available:
                continue
            if category_key and item.category.lower() != category_key:
                continue
            if query_key and query_key not in item.name.lower() and query_key not in item.category.lower():
                continue
            data = item.to_dict()
            data["available"] = available
            if not available:
                data["unavailableReason"] = "UPilot Flow is disabled"
            results.append(data)
            if len(results) >= max(1, min(limit, 200)):
                break
        return results


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


def register_public_tool(
    name: str,
    *,
    facade_method: str | None = None,
    category: str | None = None,
    idempotent: bool = True,
    destructive: bool = False,
    play_mode_policy: str = "allowed",
    feature: str = "core",
    timeout_ms: int = 30000,
    capability_requirements: tuple[str, ...] = (),
) -> None:
    REGISTRY.register(
        ToolDescriptor(
            name=name,
            facade_method=facade_method or infer_facade_method(name),
            category=category or infer_category(name),
            idempotent=idempotent,
            destructive=destructive,
            play_mode_policy=play_mode_policy,
            feature=feature,
            timeout_ms=timeout_ms,
            capability_requirements=capability_requirements,
        )
    )


async def dispatch_public_tool(facade: Any, public_name: str, args: dict[str, Any]) -> ToolResponse:
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
    normalized_args = {_camel_to_snake(key): value for key, value in args.items()}
    return await method(**normalized_args)


def _camel_to_snake(value: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", value).lower()
