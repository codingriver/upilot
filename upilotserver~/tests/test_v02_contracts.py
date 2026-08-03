from __future__ import annotations

import asyncio
import ast
import types
from pathlib import Path

from upilot_mcp.config import diagnose_client_configs
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.models import ToolResponse
from upilot_mcp.responses import fail, ok
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


def test_registry_find_supports_multi_token_queries_and_runtime_availability() -> None:
    registry = ToolRegistry()
    registry.register(ToolDescriptor("unity_sync_after_disk_write", "sync_after_disk_write", "resource"))
    registry.register(ToolDescriptor("unity_compile_errors", "compile_errors", "compile"))
    registry.register(
        ToolDescriptor(
            "unity_asset_modify_data",
            "asset_modify_data",
            "asset",
            destructive=True,
            requires_write_access=True,
        )
    )

    sync_matches = registry.find(
        query="sync disk write",
        flow_enabled=False,
        connected=False,
        server_ready=False,
        write_access_approved=False,
    )
    assert sync_matches[0]["name"] == "unity_sync_after_disk_write"
    assert sync_matches[0]["registered"] is True
    assert sync_matches[0]["callableNow"] is False
    assert sync_matches[0]["unavailableReason"] == "UNITY_NOT_CONNECTED"

    write_matches = registry.find(
        query="modify data",
        flow_enabled=False,
        connected=True,
        server_ready=True,
        write_access_approved=False,
    )
    assert write_matches[0]["name"] == "unity_asset_modify_data"
    assert write_matches[0]["requiresWriteAccess"] is True
    assert write_matches[0]["callableNow"] is False
    assert write_matches[0]["unavailableReason"] == "WRITE_ACCESS_NOT_APPROVED"


def test_prefab_query_components_tool_is_read_only_in_source_registry() -> None:
    tools_path = Path(__file__).parents[1] / "src" / "upilot_mcp" / "mcp_tools" / "resource_tools.py"
    text = tools_path.read_text(encoding="utf-8")
    assert "async def unity_prefab_query_components" in text
    assert '"unity_prefab_query_components"' not in text.split("_DESTRUCTIVE_TOOLS", 1)[1].split("}", 1)[0]


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


def test_sync_after_disk_write_reports_compiling_as_partial_success() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    service = ResourceDomainService.__new__(ResourceDomainService)

    class _CompileState:
        status = "compiling"
        error_count = 0
        warning_count = 0
        started_at = 123
        finished_at = 0
        compile_request_id = "compile-1"

    class _State:
        compile = _CompileState()

    class _Server:
        state = _State()

    async def _asset_refresh() -> ToolResponse:
        return ok("req-refresh", {"ok": True, "status": "ok"})

    async def _compile() -> ToolResponse:
        return fail("req-compile", "EDITOR_BUSY", "编译进行中，请稍后重试")

    service.server = _Server()
    service.asset_refresh = _asset_refresh
    service.compile = _compile

    result = asyncio.run(service.sync_after_disk_write(delay_s=0, trigger_compile=True))
    assert result.ok is True
    assert result.data["status"] == "compiling"
    assert result.data["refreshed"] is True
    assert result.data["compileAlreadyRunning"] is True
    assert result.data["nextAction"] == "unity_compile_wait"


def test_screenshot_editor_window_fallbacks_include_degrade_metadata() -> None:
    from upilot_mcp.domain.screenshot_service import ScreenshotDomainService

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict, **_: object) -> ToolResponse:
            assert name == "screenshot.editorWindow"
            return fail(request_id, "EDITOR_WINDOW_CAPTURE_UNAVAILABLE", "capture unavailable")

    async def _scene_view(**_: object) -> ToolResponse:
        return ok(
            "req-scene",
            {
                "imageData": "x" * 80,
                "width": 320,
                "height": 180,
                "format": "png",
            },
        )

    service = ScreenshotDomainService.__new__(ScreenshotDomainService)
    service.dispatcher = _Dispatcher()
    service.screenshot_scene_view = _scene_view

    result = asyncio.run(service.screenshot_editor_window("资源审计中心", degrade="auto"))
    assert result.ok is True
    assert result.data["degraded"] is True
    assert result.data["source"] == "sceneView"
    assert result.data["degradeReason"] == "EDITOR_WINDOW_CAPTURE_UNAVAILABLE"
    assert result.data["requestedWindowTitle"] == "资源审计中心"


def test_verify_window_uses_editor_window_list_as_truth() -> None:
    from upilot_mcp.domain.test_service import TestDomainService

    service = TestDomainService.__new__(TestDomainService)

    async def _compile_wait(self, **_: object) -> ToolResponse:
        return ok("req-compile-wait", {"status": "ready"})

    async def _editor_windows_list(self, type_filter: str = "", title_filter: str = "") -> ToolResponse:
        if title_filter == "资源审计中心":
            return ok(
                "req-windows",
                {
                    "windows": [
                        {
                            "title": "资源审计中心",
                            "typeName": "AssetAuditWindow",
                            "fullTypeName": "AUnityLocal.Editor.AssetAudit.AssetAuditWindow",
                            "instanceId": 42,
                            "posX": 1,
                            "posY": 2,
                            "width": 900,
                            "height": 640,
                            "docked": False,
                            "hasFocus": True,
                            "hasUIToolkit": True,
                        }
                    ],
                },
            )
        return ok("req-windows-empty", {"windows": []})

    async def _resource_window_diagnostics(self) -> ToolResponse:
        return ok("req-legacy", {"windowOpen": False})

    async def _resource_console_summary(self) -> ToolResponse:
        return ok("req-console", {"errorCount": 0})

    async def _screenshot_editor_window(self, window_title: str, degrade: str | None = None) -> ToolResponse:
        return ok("req-screenshot", {"source": "editorWindow", "width": 900, "height": 640})

    service.compile_wait = types.MethodType(_compile_wait, service)
    service.editor_windows_list = types.MethodType(_editor_windows_list, service)
    service.resource_window_diagnostics = types.MethodType(_resource_window_diagnostics, service)
    service.resource_console_summary = types.MethodType(_resource_console_summary, service)
    service.screenshot_editor_window = types.MethodType(_screenshot_editor_window, service)

    result = asyncio.run(service.verify_window("资源审计中心"))
    assert result.ok is True
    assert result.data["windowMatch"]["windowOpen"] is True
    assert result.data["windowMatch"]["matchedFullTypeName"] == "AUnityLocal.Editor.AssetAudit.AssetAuditWindow"
    assert result.data["windowDiagnostics"]["windowOpen"] is False
    assert result.data["legacyWindowDiagnostics"]["windowOpen"] is False
    assert result.data["screenshot"]["source"] == "editorWindow"


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
