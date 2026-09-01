from __future__ import annotations

import asyncio
import json

from upilot_mcp import config as config_module
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.mcp_tools import reflection_tools
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY, dispatch_public_tool


def _configure_write_access(tmp_path, monkeypatch, approved: bool) -> dict[str, object]:
    original = {
        name: getattr(config_module.CONFIG, name)
        for name in config_module.CONFIG.__slots__
    }
    config_path = tmp_path / "config.json"
    config_path.write_text(
        json.dumps({"schemaVersion": 2, "safety": {"writeAccessApproved": approved}}),
        encoding="utf-8",
    )
    monkeypatch.setenv("UPILOT_CONFIG", str(config_path))
    config_module.refresh_config_if_changed(force=True)
    assert config_module.CONFIG.write_access_approved is approved
    return original


def _restore_config(original: dict[str, object]) -> None:
    for name, value in original.items():
        setattr(config_module.CONFIG, name, value)
    config_module._CONFIG_LAST_DISK_HASH = ""


def test_reflection_call_registry_is_mutating_non_idempotent_and_write_gated() -> None:
    descriptor = REGISTRY.resolve("unity_reflection_call")

    assert descriptor is not None
    assert descriptor.category == "reflection"
    assert descriptor.destructive is True
    assert descriptor.idempotent is False
    assert descriptor.requires_unity_connection is True
    assert descriptor.requires_write_access is True
    assert descriptor.play_mode_policy == "allowed"
    assert descriptor.feature == "core"


def test_reflection_read_and_operation_observation_tools_remain_safe_mode_callable() -> None:
    for name in (
        "unity_type_exists",
        "unity_reflection_find",
        "unity_reflection_operation_status",
        "unity_reflection_operation_wait",
        "unity_reflection_operation_cancel",
    ):
        descriptor = REGISTRY.resolve(name)
        assert descriptor is not None, name
        assert descriptor.destructive is False, name
        assert descriptor.requires_write_access is False, name
        assert descriptor.idempotent is True, name


def test_tools_find_reports_reflection_call_blocked_without_write_access() -> None:
    matches = REGISTRY.find(
        query="unity_reflection_call",
        availability="all",
        connected=True,
        server_ready=True,
        write_access_approved=False,
    )
    exact = next(item for item in matches if item["name"] == "unity_reflection_call")

    assert exact["available"] is True
    assert exact["callableNow"] is False
    assert exact["unavailableReason"] == "WRITE_ACCESS_NOT_APPROVED"
    assert exact["requiresWriteAccess"] is True
    assert exact["destructive"] is True
    assert exact["idempotent"] is False
    assert exact["nextAction"]


def test_tools_find_hot_reloads_write_access_without_an_intermediate_tool_call(tmp_path, monkeypatch) -> None:
    original = _configure_write_access(tmp_path, monkeypatch, approved=False)
    config_path = tmp_path / "config.json"
    service = StatusDomainService.__new__(StatusDomainService)
    service.server = type(
        "Server",
        (),
        {
            "session_manager": type(
                "Sessions", (), {"is_connected": lambda self: True}
            )(),
            "is_ready": lambda self: True,
        },
    )()

    try:
        blocked = asyncio.run(service.tools_find(query="unity_reflection_call"))
        blocked_exact = blocked.data["exactMatches"][0]
        assert blocked_exact["callableNow"] is False
        assert blocked_exact["unavailableReason"] == "WRITE_ACCESS_NOT_APPROVED"

        config_path.write_text(
            json.dumps(
                {"schemaVersion": 2, "safety": {"writeAccessApproved": True}}
            ),
            encoding="utf-8",
        )
        allowed = asyncio.run(service.tools_find(query="unity_reflection_call"))
        allowed_exact = allowed.data["exactMatches"][0]
        assert allowed_exact["callableNow"] is True
        assert "unavailableReason" not in allowed_exact
    finally:
        _restore_config(original)


def test_typed_reflection_call_rejects_before_facade_in_safe_mode(tmp_path, monkeypatch) -> None:
    original = _configure_write_access(tmp_path, monkeypatch, approved=False)

    class _Facade:
        async def reflection_call(self, **_kwargs):
            raise AssertionError("blocked typed call must not reach the facade")

    monkeypatch.setattr(reflection_tools, "_get_facade", lambda: _Facade())
    try:
        result = asyncio.run(
            reflection_tools.unity_reflection_call(
                typeName="Fixture",
                methodName="Mutate",
            )
        )
    finally:
        _restore_config(original)

    assert result.isError is True
    assert result.structuredContent["error"]["code"] == "WRITE_ACCESS_NOT_APPROVED"
    assert result.structuredContent["error"]["detail"] == {
        "tool": "unity_reflection_call",
        "configKey": "safety.writeAccessApproved",
    }


def test_typed_reflection_call_executes_once_after_write_approval(tmp_path, monkeypatch) -> None:
    original = _configure_write_access(tmp_path, monkeypatch, approved=True)
    calls: list[dict] = []

    class _Facade:
        async def reflection_call(self, **kwargs):
            calls.append(kwargs)
            return ok("reflection", {"result": "done"})

    monkeypatch.setattr(reflection_tools, "_get_facade", lambda: _Facade())
    try:
        result = asyncio.run(
            reflection_tools.unity_reflection_call(
                typeName="Fixture",
                methodName="Mutate",
                parameters=[1],
            )
        )
    finally:
        _restore_config(original)

    assert result.isError is False
    assert result.structuredContent["data"]["result"] == "done"
    assert len(calls) == 1
    assert calls[0]["type_name"] == "Fixture"
    assert calls[0]["method_name"] == "Mutate"


def test_proxy_reflection_call_rejects_before_facade_in_safe_mode(tmp_path, monkeypatch) -> None:
    original = _configure_write_access(tmp_path, monkeypatch, approved=False)
    calls = 0

    class _Facade:
        async def reflection_call(self, **_kwargs):
            nonlocal calls
            calls += 1
            return ok("reflection", {"result": "unexpected"})

    try:
        result = asyncio.run(
            dispatch_public_tool(
                _Facade(),
                "unity_reflection_call",
                {"typeName": "Fixture", "methodName": "Mutate"},
            )
        )
    finally:
        _restore_config(original)

    assert result.ok is False
    assert result.error is not None
    assert result.error.code == "WRITE_ACCESS_NOT_APPROVED"
    assert calls == 0
