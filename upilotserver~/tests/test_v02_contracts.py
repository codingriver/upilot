from __future__ import annotations

import asyncio
import ast
from pathlib import Path

from upilot_mcp.config import diagnose_client_configs
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.models import ToolResponse
from upilot_mcp.responses import fail
from upilot_mcp.state_store import StateStore
from upilot_mcp.tool_registry import ToolDescriptor, ToolRegistry, dispatch_public_tool, register_public_tool
from upilot_mcp.config import CONFIG


class _Facade:
    async def echo(self, value: str = "") -> ToolResponse:
        from upilot_mcp.responses import ok

        return ok("req-test", {"value": value})


class _Transport:
    def __init__(self, result: dict) -> None:
        self.result = result
        self.future: asyncio.Future | None = None

    def is_ready(self) -> bool:
        return True

    def register_pending(self, command_id: str) -> asyncio.Future:
        self.future = asyncio.get_running_loop().create_future()
        return self.future

    async def send_command(self, command_id: str, name: str, payload: dict) -> None:
        assert self.future is not None
        self.future.set_result(self.result)


def test_registry_is_stable_and_reports_flow_availability() -> None:
    registry = ToolRegistry()
    registry.register(ToolDescriptor("unity_z", "z", "test"))
    registry.register(ToolDescriptor("unity_a", "a", "test", feature="flow"))

    assert [item.name for item in registry.list()] == ["unity_a", "unity_z"]
    unavailable = registry.find(availability="unavailable", flow_enabled=False)
    assert [item["name"] for item in unavailable] == ["unity_a"]
    assert unavailable[0]["unavailableReason"]


def test_public_tool_route_and_unknown_tool_are_real_failures() -> None:
    register_public_tool("unity_contract_echo", facade_method="echo", category="test")
    facade = _Facade()
    result = asyncio.run(dispatch_public_tool(facade, "unity_contract_echo", {"value": "ok"}))
    missing = asyncio.run(dispatch_public_tool(facade, "unity_contract_missing", {}))

    assert result.ok and result.data == {"value": "ok"}
    assert not missing.ok
    assert missing.error and missing.error.code == "UNKNOWN_TOOL"


def test_public_tool_route_rejects_destructive_tools_in_safe_mode() -> None:
    register_public_tool(
        "unity_contract_write",
        facade_method="echo",
        category="test",
        destructive=True,
    )
    facade = _Facade()
    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", False)
    try:
        result = asyncio.run(dispatch_public_tool(facade, "unity_contract_write", {"value": "nope"}))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert not result.ok
    assert result.error and result.error.code == "WRITE_ACCESS_NOT_APPROVED"


def test_structured_error_sets_is_error() -> None:
    from upilot_mcp.mcp_stdio_server import _payload

    result = _payload(fail("req-1", "EXPECTED", "failed"))
    assert result.isError is True
    assert result.structuredContent["schemaVersion"] == 2
    assert result.structuredContent["ok"] is False
    assert result.structuredContent["error"]["code"] == "EXPECTED"


def test_dispatcher_preserves_bridge_timing_and_round_trip() -> None:
    transport = _Transport({
        "type": "result",
        "payload": {"ok": True},
        "timing": {
            "queueMs": 3,
            "bridgeMs": 8,
            "unityExecutionMs": 4,
            "serializationMs": 1,
        },
    })
    dispatcher = CommandDispatcher(transport, StateStore())
    result = asyncio.run(dispatcher.call("req-1", "test.command", {}))

    assert result.ok
    assert result.timing["queueMs"] == 3
    assert result.timing["bridgeMs"] == 8
    assert result.timing["unityExecutionMs"] == 4
    assert result.timing["serializationMs"] == 1
    assert result.timing["roundTripMs"] >= 0


def test_editor_state_tracks_freshness_timestamp() -> None:
    state = StateStore()
    assert state.editor.updated_at == 0
    state.update_editor_state({"connected": True, "activeScene": "Launch"})
    assert state.editor.connected is True
    assert state.editor.active_scene == "Launch"
    assert state.editor.updated_at > 0


def test_client_config_diagnostics_detects_duplicate_endpoint_and_timeout(tmp_path) -> None:
    config_dir = tmp_path / ".codex"
    config_dir.mkdir()
    config_dir.joinpath("config.toml").write_text(
        """
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
tool_timeout_sec = 60

[mcp_servers.duplicate]
url = "http://127.0.0.1:8011/mcp"
""".strip(),
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}
    assert "DUPLICATE_MCP_ENDPOINT" in codes
    assert "CLIENT_TIMEOUT_TOO_LOW" in codes


def test_client_config_diagnostics_allows_same_endpoint_for_different_clients(tmp_path) -> None:
    codex_dir = tmp_path / ".codex"
    codex_dir.mkdir()
    codex_dir.joinpath("config.toml").write_text(
        '[mcp_servers.upilot]\nurl = "http://127.0.0.1:8011/mcp"',
        encoding="utf-8",
    )
    tmp_path.joinpath(".mcp.json").write_text(
        '{"mcpServers":{"upilot":{"url":"http://127.0.0.1:8011/mcp"}}}',
        encoding="utf-8",
    )

    result = diagnose_client_configs(tmp_path)
    codes = {item["code"] for item in result["issues"]}
    assert "DUPLICATE_MCP_ENDPOINT" not in codes


def test_mcp_tool_functions_do_not_declare_legacy_string_outputs() -> None:
    tools_dir = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_tools"
    offenders: list[str] = []
    for path in tools_dir.glob("*_tools.py"):
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in tree.body:
            if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            if not node.name.startswith("unity_") and node.name != "reflection_eval":
                continue
            if isinstance(node.returns, ast.Name) and node.returns.id == "str":
                offenders.append(f"{path.name}:{node.name}")

    assert offenders == []


def test_mcp_server_is_only_a_runtime_composition_root() -> None:
    server_path = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_stdio_server.py"
    tree = ast.parse(server_path.read_text(encoding="utf-8"), filename=str(server_path))
    public_tools = [
        node.name
        for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
        and (node.name.startswith("unity_") or node.name == "reflection_eval")
    ]

    assert public_tools == []
