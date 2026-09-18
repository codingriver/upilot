from __future__ import annotations

import asyncio
import json

import pytest
from mcp.server.fastmcp.exceptions import ToolError

from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.mcp_tools import (
    analysis_tools,
    compile_tools,
    execution_tools,
    resource_tools,
    status_tools,
    task_tools,
    test_tools,
)
from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp.config import CONFIG
from upilot_mcp.responses import ok
from upilot_mcp.tool_facade import McpToolFacade
from upilot_mcp.tool_registry import REGISTRY, dispatch_public_tool, proxy_argument_schema


def _schema(tool_name: str) -> dict:
    tools = {tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())}
    return tools[tool_name].inputSchema


def test_proxy_discovery_uses_exact_public_schema_names_and_types() -> None:
    service = McpToolFacade.__new__(McpToolFacade)
    cases = {
        "unity_menu_execute": service.menu_execute,
        "unity_console_capture_start": service.console_capture_start,
        "unity_console_search_logs": service.console_search_logs,
        "unity_component_remove": service.component_remove,
        "unity_compile_errors": service.compile_errors,
        "unity_compile_errors_get": service.compile_errors,
        "unity_config_csv_get": service.config_csv_get,
        "unity_config_csv_patch": service.config_csv_patch,
        "unity_test_run": service.test_run,
        "unity_test_results": service.test_results,
        "unity_upilot_acceptance_run": service.upilot_acceptance_run,
        "unity_console_capture_attach": service.console_capture_attach,
        "unity_console_capture_detach": service.console_capture_detach,
        "unity_asset_dependencies": service.asset_dependencies,
        "unity_prefab_query_components": service.prefab_query_components,
        "unity_playmode_pause": service.playmode_pause,
        "unity_playmode_resume": service.playmode_resume,
        "unity_sceneview_set_maximized": service.sceneview_set_maximized,
        "unity_operation_start": service.operation_start,
        "unity_operation_status": service.operation_status,
        "unity_operation_wait": service.operation_wait,
        "unity_operation_cancel": service.operation_cancel,
        "execution_session": service.execution_session,
        "csharp_eval": service.csharp_eval,
        "reflection_emit_type": service.reflection_emit_type,
    }

    for tool_name, method in cases.items():
        public_properties = _schema(tool_name)["properties"]
        discovered = proxy_argument_schema(method, tool_name)
        assert [entry["name"] for entry in discovered] == list(public_properties)

    capture = proxy_argument_schema(service.console_capture_start, "unity_console_capture_start")
    exclude = next(entry for entry in capture if entry["name"] == "excludeUPilot")
    assert exclude["schema"]["type"] == "boolean"
    assert "exclude_upilot" in exclude["aliases"]
    assert "excludeUpilot" in exclude["aliases"]

    search = proxy_argument_schema(service.console_search_logs, "unity_console_search_logs")
    contains = next(entry for entry in search if entry["name"] == "contains")
    assert {item.get("type") for item in contains["schema"]["anyOf"]} >= {"string", "array", "null"}


@pytest.mark.parametrize("tool_name,method_name", [
    ("unity_test_run", "test_run"),
    ("unity_upilot_acceptance_run", "upilot_acceptance_run"),
])
def test_selection_snapshot_expectations_are_public_and_forwarded_by_native_and_proxy(
    monkeypatch, tool_name, method_name,
) -> None:
    calls: list[dict] = []

    class Facade:
        async def test_run(
            self, test_mode="EditMode", test_filter=None, test_names=None, fixtures=None,
            assemblies=None, categories=None, match_mode="union", require_all_selectors_match=True,
            expected_selection_domain="", expected_selection_snapshot_id="",
        ):
            kwargs = {
                "test_mode": test_mode, "test_filter": test_filter, "test_names": test_names,
                "fixtures": fixtures, "assemblies": assemblies, "categories": categories,
                "match_mode": match_mode, "require_all_selectors_match": require_all_selectors_match,
                "expected_selection_domain": expected_selection_domain,
                "expected_selection_snapshot_id": expected_selection_snapshot_id,
            }
            calls.append(kwargs)
            return ok("run", {"received": kwargs})

        async def upilot_acceptance_run(
            self, test_mode="EditMode", test_filter=None, timeout_sec=900,
            stop_active_captures=True, require_tests=True, write_artifact=True,
            test_names=None, fixtures=None, assemblies=None, categories=None,
            match_mode="union", require_all_selectors_match=True,
            expected_selection_domain="", expected_selection_snapshot_id="", preflight_only=False,
        ):
            kwargs = {
                "test_mode": test_mode, "test_filter": test_filter, "timeout_sec": timeout_sec,
                "stop_active_captures": stop_active_captures, "require_tests": require_tests,
                "write_artifact": write_artifact, "test_names": test_names, "fixtures": fixtures,
                "assemblies": assemblies, "categories": categories, "match_mode": match_mode,
                "require_all_selectors_match": require_all_selectors_match,
                "expected_selection_domain": expected_selection_domain,
                "expected_selection_snapshot_id": expected_selection_snapshot_id,
                "preflight_only": preflight_only,
            }
            calls.append(kwargs)
            return ok("acceptance", {"received": kwargs})

    facade = Facade()
    monkeypatch.setattr(test_tools, "_get_facade", lambda: facade)
    schema = _schema(tool_name)
    assert list(schema["properties"])[-2:] == [
        "expectedSelectionDomain", "expectedSelectionSnapshotId",
    ] if tool_name == "unity_test_run" else [
        "expectedSelectionSnapshotId", "preflightOnly",
    ]
    assert "expectedSelectionDomain" in schema["properties"]
    assert "expectedSelectionSnapshotId" in schema["properties"]
    arguments = {
        "fixtures": ["Demo.Fixture"],
        "expectedSelectionDomain": "domain-p2",
        "expectedSelectionSnapshotId": "snapshot-p2",
    }
    approved = CONFIG.write_access_approved
    if tool_name == "unity_upilot_acceptance_run":
        object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        native = asyncio.run(runtime.mcp._tool_manager.call_tool(tool_name, arguments))
        proxy = asyncio.run(dispatch_public_tool(facade, tool_name, arguments))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)
    assert native.structuredContent["data"]["received"]["expected_selection_domain"] == "domain-p2"
    assert proxy.ok
    assert [call["expected_selection_snapshot_id"] for call in calls] == ["snapshot-p2", "snapshot-p2"]


def test_config_csv_and_operation_camelcase_arguments_match_native_and_proxy_dispatch(monkeypatch) -> None:
    calls: list[tuple[str, dict]] = []

    class Facade:
        async def config_csv_get(
            self, path: str, keys: dict, fields=None, header_row_index: int = 0, encoding: str = "auto",
        ):
            received = {
                "path": path, "keys": keys, "fields": fields,
                "header_row_index": header_row_index, "encoding": encoding,
            }
            calls.append(("csv", received))
            return ok("csv", {"received": received})

        async def operation_start(self, job_spec: dict):
            calls.append(("operation", {"job_spec": job_spec}))
            return ok("operation", {"received": job_spec})

    facade = Facade()
    monkeypatch.setattr(analysis_tools, "_get_facade", lambda: facade)
    monkeypatch.setattr(task_tools, "_get_facade", lambda: facade)

    csv_args = {
        "path": "Assets/P2.csv", "keys": {"id": "42"},
        "headerRowIndex": 3,
    }
    job_args = {"jobSpec": {"kind": "test", "timeoutSec": 30}}
    native_csv = asyncio.run(runtime.mcp._tool_manager.call_tool("unity_config_csv_get", csv_args))
    proxy_csv = asyncio.run(dispatch_public_tool(facade, "unity_config_csv_get", csv_args))
    native_operation = asyncio.run(runtime.mcp._tool_manager.call_tool("unity_operation_start", job_args))
    proxy_operation = asyncio.run(dispatch_public_tool(facade, "unity_operation_start", job_args))

    assert native_csv.structuredContent["data"]["received"]["header_row_index"] == 3
    assert proxy_csv.ok and proxy_csv.data["received"]["header_row_index"] == 3
    assert native_operation.structuredContent["data"]["received"] == job_args["jobSpec"]
    assert proxy_operation.ok and proxy_operation.data["received"] == job_args["jobSpec"]
    assert calls == [
        ("csv", {"path": "Assets/P2.csv", "keys": {"id": "42"}, "fields": None, "header_row_index": 3, "encoding": "auto"}),
        ("csv", {"path": "Assets/P2.csv", "keys": {"id": "42"}, "fields": None, "header_row_index": 3, "encoding": "auto"}),
        ("operation", {"job_spec": job_args["jobSpec"]}),
        ("operation", {"job_spec": job_args["jobSpec"]}),
    ]


def test_execution_public_schema_and_proxy_dispatch_match_native_wrappers(monkeypatch) -> None:
    calls: list[tuple[str, dict]] = []

    class Facade:
        async def execution_session(
            self, action, session_id="", title="", ttl_sec=600, max_handles=256,
            max_dynamic_types=32, max_callbacks=64, max_async_operations=64,
        ):
            received = {
                "action": action, "session_id": session_id, "title": title,
                "ttl_sec": ttl_sec, "max_handles": max_handles,
                "max_dynamic_types": max_dynamic_types, "max_callbacks": max_callbacks,
                "max_async_operations": max_async_operations,
            }
            calls.append(("session", received))
            return ok("session", {"received": received})

        async def csharp_eval(
            self, code, mode="auto", session_id="", variables=None, imports=None,
            execution_backend="auto", limits=None, result_mode="auto",
        ):
            received = {
                "code": code, "mode": mode, "session_id": session_id,
                "variables": variables, "imports": imports,
                "execution_backend": execution_backend, "limits": limits,
                "result_mode": result_mode,
            }
            calls.append(("eval", received))
            return ok("eval", {"received": received})

        async def reflection_emit_type(
            self, session_id, spec, cache_policy="specHash", name_conflict_policy="reject",
            create_instance=False, constructor_arguments=None,
        ):
            received = {
                "session_id": session_id, "spec": spec, "cache_policy": cache_policy,
                "name_conflict_policy": name_conflict_policy,
                "create_instance": create_instance,
                "constructor_arguments": constructor_arguments,
            }
            calls.append(("emit", received))
            return ok("emit", {"received": received})

    facade = Facade()
    monkeypatch.setattr(execution_tools, "_get_facade", lambda: facade)
    monkeypatch.setattr(execution_tools, "_reject_write_if_unapproved", lambda _name: None)
    cases = (
        ("execution_session", {"action": "open", "sessionId": "s-p2", "maxAsyncOperations": 7}),
        ("csharp_eval", {"code": "return 1;", "sessionId": "s-p2", "executionBackend": "interpret"}),
        ("reflection_emit_type", {"sessionId": "s-p2", "spec": {"typeName": "P2.Dynamic"}, "createInstance": True}),
    )
    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        for tool_name, arguments in cases:
            native = asyncio.run(getattr(execution_tools, tool_name)(**arguments))
            proxy = asyncio.run(dispatch_public_tool(facade, tool_name, arguments))
            assert native.isError is False
            assert proxy.ok
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)

    assert calls[0] == calls[1]
    assert calls[2] == calls[3]
    assert calls[4] == calls[5]


def test_acceptance_run_write_gate_is_conditional_on_preflight_and_never_dispatches_full_run(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def upilot_acceptance_run(self, preflight_only: bool = False, **kwargs):
            calls.append({"preflight_only": preflight_only, **kwargs})
            return ok("acceptance", {"preflightOnly": preflight_only})

    facade = Facade()
    monkeypatch.setattr(test_tools, "_get_facade", lambda: facade)
    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", False)
    try:
        native_preflight = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_upilot_acceptance_run", {"preflightOnly": True},
        ))
        proxy_preflight = asyncio.run(dispatch_public_tool(
            facade, "unity_upilot_acceptance_run", {"preflightOnly": True},
        ))
        native_full = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_upilot_acceptance_run", {},
        ))
        proxy_full = asyncio.run(dispatch_public_tool(facade, "unity_upilot_acceptance_run", {}))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)

    assert native_preflight.structuredContent["data"]["preflightOnly"] is True
    assert proxy_preflight.ok and proxy_preflight.data["preflightOnly"] is True
    assert native_full.structuredContent["error"]["code"] == "WRITE_ACCESS_NOT_APPROVED"
    assert not proxy_full.ok and proxy_full.error and proxy_full.error.code == "WRITE_ACCESS_NOT_APPROVED"
    assert calls == [
        {"test_mode": "EditMode", "test_filter": None, "timeout_sec": 900, "stop_active_captures": True, "require_tests": True, "write_artifact": True, "test_names": None, "fixtures": None, "assemblies": None, "categories": None, "match_mode": "union", "require_all_selectors_match": True, "expected_selection_domain": "", "expected_selection_snapshot_id": "", "preflight_only": True},
        {"preflight_only": True},
    ]


def test_direct_menu_alias_is_normalized_and_unknown_field_never_dispatches(monkeypatch) -> None:
    calls: list[str] = []

    class Facade:
        async def menu_execute(self, menu_path: str, expected_modal=None):
            calls.append(menu_path)
            return ok("menu", {"menuPath": menu_path, "expectedModal": expected_modal})

    monkeypatch.setattr(resource_tools, "_get_facade", lambda: Facade())
    result = None
    for index in range(10):
        result = asyncio.run(
            runtime.mcp._tool_manager.call_tool(
                "unity_menu_execute", {"menu_path": f"Assets/P2 Contract/{index}"}
            )
        )
    assert result is not None
    assert result.structuredContent["data"]["menuPath"] == "Assets/P2 Contract/9"
    assert calls == [f"Assets/P2 Contract/{index}" for index in range(10)]

    with pytest.raises(ValueError) as exc:
        asyncio.run(
            runtime.mcp._tool_manager.call_tool(
                "unity_menu_execute",
                {"menuPath": "Assets/Never Runs", "unexpectedField": True},
            )
        )
    detail = json.loads(str(exc.value))
    assert detail["unknownArguments"] == ["unexpectedField"]
    assert "menuPath" in detail["expectedArguments"]
    assert calls == [f"Assets/P2 Contract/{index}" for index in range(10)]


def test_direct_capture_legacy_case_alias_preserves_false_and_unknown_never_dispatches(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def console_capture_start(self, **kwargs):
            calls.append(kwargs)
            return ok("capture", {"excludeUPilot": kwargs["exclude_upilot"]})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    result = asyncio.run(
        runtime.mcp._tool_manager.call_tool(
            "unity_console_capture_start", {"excludeUpilot": False}
        )
    )
    assert result.structuredContent["data"]["excludeUPilot"] is False
    assert calls[0]["exclude_upilot"] is False

    with pytest.raises(ValueError):
        asyncio.run(
            runtime.mcp._tool_manager.call_tool(
                "unity_console_capture_start", {"excludePilot": False}
            )
        )
    assert len(calls) == 1


def test_compile_warning_option_is_exposed_and_forwarded_by_native_and_proxy(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def compile_errors(self, compile_request_id: str = "", include_warnings: bool = False):
            calls.append({
                "compile_request_id": compile_request_id,
                "include_warnings": include_warnings,
            })
            return ok("compile", {"includeWarnings": include_warnings})

    facade = Facade()
    monkeypatch.setattr(compile_tools, "_get_facade", lambda: facade)
    for tool_name in ("unity_compile_errors", "unity_compile_errors_get"):
        schema = _schema(tool_name)
        assert list(schema["properties"]) == ["compileRequestId", "includeWarnings"]
        assert schema["properties"]["includeWarnings"] == {
            "type": "boolean",
            "default": False,
            "title": "Includewarnings",
        }

        native = asyncio.run(runtime.mcp._tool_manager.call_tool(
            tool_name, {"compileRequestId": "compile-p2", "includeWarnings": True},
        ))
        proxy = asyncio.run(dispatch_public_tool(
            facade, tool_name, {"compileRequestId": "compile-p2", "includeWarnings": True},
        ))
        assert native.structuredContent["data"]["includeWarnings"] is True
        assert proxy.ok and proxy.data["includeWarnings"] is True

    assert calls == [
        {"compile_request_id": "compile-p2", "include_warnings": True},
        {"compile_request_id": "compile-p2", "include_warnings": True},
        {"compile_request_id": "compile-p2", "include_warnings": True},
        {"compile_request_id": "compile-p2", "include_warnings": True},
    ]


def test_p2_public_schemas_expose_existing_bounded_input_contracts() -> None:
    """Keep client-visible schemas aligned with the P2 domain validation limits."""
    test_results = _schema("unity_test_results")["properties"]
    assert test_results["count"]["minimum"] == 1
    assert test_results["count"]["maximum"] == 1000

    history = _schema("unity_editor_window_history")["properties"]
    assert history["afterSequence"]["minimum"] == 0
    assert history["count"]["minimum"] == 1
    assert history["count"]["maximum"] == 512

    for name in ("unity_config_csv_get", "unity_config_csv_patch"):
        assert _schema(name)["properties"]["headerRowIndex"]["minimum"] == 0

    prefab = _schema("unity_prefab_query_components")["properties"]
    assert prefab["referenceDepth"]["minimum"] == 1
    assert prefab["referenceDepth"]["maximum"] == 4

    dependencies = _schema("unity_asset_dependencies")["properties"]
    assert dependencies["evidenceMode"]["enum"] == ["file", "object"]
    assert dependencies["runtimeBoundary"]["enum"] == ["none", "ExcludeAssetsEditor"]
    assert dependencies["direction"]["enum"] == ["forward", "reverse"]
    assert dependencies["maxNodes"]["minimum"] == 1
    assert dependencies["maxNodes"]["maximum"] == 5000
    assert dependencies["timeBudgetMs"]["minimum"] == 1
    assert dependencies["timeBudgetMs"]["maximum"] == 30000


def test_verify_window_exact_identity_is_exposed_without_default_title_conflict(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def verify_window(
            self, window_title: str = "", include_screenshot: bool = True,
            screenshot_degrade: str = "auto", instance_id: str = "",
            domain_generation: str = "", full_type_name: str = "",
        ):
            values = {
                "window_title": window_title,
                "include_screenshot": include_screenshot,
                "screenshot_degrade": screenshot_degrade,
                "instance_id": instance_id,
                "domain_generation": domain_generation,
                "full_type_name": full_type_name,
            }
            calls.append(values)
            return ok("verify", {"instanceId": instance_id})

    monkeypatch.setattr(test_tools, "_get_facade", lambda: Facade())
    schema = _schema("unity_verify_window")
    assert list(schema["properties"]) == [
        "windowTitle", "includeScreenshot", "screenshotDegrade",
        "instanceId", "domainGeneration", "fullTypeName",
    ]
    assert schema["properties"]["windowTitle"]["default"] == ""

    native = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_verify_window", {"instanceId": "42", "domainGeneration": "7"},
    ))
    proxy = asyncio.run(dispatch_public_tool(
        Facade(), "unity_verify_window", {"instanceId": "42", "fullTypeName": "Example.Window"},
    ))

    assert native.structuredContent["data"]["instanceId"] == "42"
    assert proxy.ok is True
    assert calls == [
        {
            "window_title": "", "include_screenshot": True,
            "screenshot_degrade": "auto", "instance_id": "42",
            "domain_generation": "7", "full_type_name": "",
        },
        {
            "window_title": "", "include_screenshot": True,
            "screenshot_degrade": "auto", "instance_id": "42",
            "domain_generation": "", "full_type_name": "Example.Window",
        },
    ]


def test_editor_window_set_rect_exposes_and_forwards_full_type_name(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def editor_window_set_rect(
            self, window_title="", x=None, y=None, width=None, height=None,
            match_mode="exact", instance_id="", domain_generation="", full_type_name="",
        ):
            kwargs = {
                "window_title": window_title, "x": x, "y": y, "width": width,
                "height": height, "match_mode": match_mode, "instance_id": instance_id,
                "domain_generation": domain_generation, "full_type_name": full_type_name,
            }
            calls.append(kwargs)
            return ok("set-rect", {"fullTypeName": full_type_name})

    facade = Facade()
    monkeypatch.setattr(status_tools, "_get_facade", lambda: facade)
    schema = _schema("unity_editor_window_set_rect")
    assert list(schema["properties"])[-3:] == [
        "instanceId", "domainGeneration", "fullTypeName",
    ]
    assert schema["properties"]["fullTypeName"]["default"] == ""
    arguments = {
        "instanceId": "42", "domainGeneration": "7",
        "fullTypeName": "CodingRiver.UPilot.UPilotSafeWindowProbe",
        "x": 1, "y": 2, "width": 3, "height": 4,
    }
    native = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_editor_window_set_rect", arguments,
    ))
    proxy = asyncio.run(dispatch_public_tool(
        facade, "unity_editor_window_set_rect", arguments,
    ))
    assert native.structuredContent["data"]["fullTypeName"] == arguments["fullTypeName"]
    assert proxy.ok is True
    assert [call["full_type_name"] for call in calls] == [
        arguments["fullTypeName"], arguments["fullTypeName"],
    ]


def test_capture_detach_and_force_stop_public_contracts(monkeypatch) -> None:
    detach_schema = _schema("unity_console_capture_detach")
    assert list(detach_schema["properties"]) == ["attachmentId", "export", "requestKey", "continuationToken"]
    assert detach_schema["required"] == ["attachmentId"]
    stop_schema = _schema("unity_console_capture_stop")
    assert "forceStop" in stop_schema["properties"]

    calls: list[dict] = []

    class Facade:
        async def console_capture_detach(self, **kwargs):
            calls.append(kwargs)
            return ok("detach", {"attachmentId": kwargs["attachment_id"]})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    result = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_console_capture_detach",
        {"attachmentId": "att-1", "export": True, "requestKey": "export-1", "continuationToken": "next"},
    ))
    assert result.structuredContent["data"]["attachmentId"] == "att-1"
    assert calls == [{
        "attachment_id": "att-1", "export": True,
        "request_key": "export-1", "continuation_token": "next",
    }]


def test_sceneview_maximized_schema_and_proxy_use_exact_instance_id(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def sceneview_set_maximized(self, **kwargs):
            calls.append(kwargs)
            return ok("sceneview", {"instanceId": kwargs["instance_id"], "maximized": kwargs["maximized"]})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    schema = _schema("unity_sceneview_set_maximized")
    descriptor = REGISTRY.resolve("unity_sceneview_set_maximized")
    assert descriptor is not None
    assert descriptor.destructive is True
    assert descriptor.requires_write_access is True
    assert descriptor.idempotent is False
    assert list(schema["properties"]) == ["instanceId", "maximized", "expectedCurrentMaximized", "domainGeneration", "wait", "timeoutMs", "restoreToken"]
    assert schema["required"] == ["instanceId", "maximized"]
    assert schema["properties"]["instanceId"]["type"] == "integer"

    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        result = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_sceneview_set_maximized", {"instanceId": 42, "maximized": False, "expectedCurrentMaximized": True},
        ))
        assert result.structuredContent["data"]["maximized"] is False
        assert calls == [{"instance_id": 42, "maximized": False, "expected_current_maximized": True, "domain_generation": "", "wait": True, "timeout_ms": 5000, "restore_token": ""}]

        numeric = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_sceneview_set_maximized", {"instanceId": 42, "maximized": True},
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)

    assert numeric.structuredContent["data"]["instanceId"] == "42"
    assert calls[-1] == {"instance_id": 42, "maximized": True, "expected_current_maximized": None, "domain_generation": "", "wait": True, "timeout_ms": 5000, "restore_token": ""}

    with pytest.raises(ToolError, match="instanceId"):
        asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_sceneview_set_maximized", {"instanceId": True, "maximized": True},
        ))
    with pytest.raises(ToolError, match="instanceId"):
        asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_sceneview_set_maximized", {"instanceId": "42", "maximized": True},
        ))
    assert len(calls) == 2


def test_sceneview_maximize_write_gate_rejects_coerced_boolean_and_timeout_before_facade(monkeypatch) -> None:
    """P2-WP-13-T02: invalid public input cannot reach the setter facade."""

    calls: list[dict] = []

    class Facade:
        async def sceneview_set_maximized(self, **kwargs):
            calls.append(kwargs)
            return ok("sceneview", {})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        for payload in (
            {"instanceId": 42, "maximized": 1},
            {"instanceId": 42, "maximized": True, "wait": "false"},
            {"instanceId": 42, "maximized": True, "timeoutMs": True},
        ):
            with pytest.raises(ToolError):
                asyncio.run(runtime.mcp._tool_manager.call_tool("unity_sceneview_set_maximized", payload))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)

    assert calls == []


def test_sceneview_command_status_is_read_only_and_forwards_original_identity(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def sceneview_command_status(self, **kwargs):
            calls.append(kwargs)
            return ok("sceneview-status", {"commandId": kwargs["command_id"], "observationStatus": "observed"})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    schema = _schema("unity_sceneview_command_status")
    assert list(schema["properties"]) == ["commandId"]
    descriptor = REGISTRY.resolve("unity_sceneview_command_status")
    assert descriptor is not None and descriptor.idempotent is True and descriptor.destructive is False
    result = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_sceneview_command_status", {"commandId": "cmd-original"},
    ))

    assert result.structuredContent["data"]["commandId"] == "cmd-original"
    assert calls == [{"command_id": "cmd-original"}]


def test_playmode_pause_resume_public_contracts_are_registered_and_forward_once(monkeypatch) -> None:
    calls: list[tuple[str, dict]] = []

    class Facade:
        async def playmode_pause(self, **kwargs):
            calls.append(("pause", kwargs))
            return ok("pause", {"requestedState": {"isPaused": True}})

        async def playmode_resume(self, **kwargs):
            calls.append(("resume", kwargs))
            return ok("resume", {"requestedState": {"isPaused": False}})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    for name in ("unity_playmode_pause", "unity_playmode_resume"):
        descriptor = REGISTRY.resolve(name)
        assert descriptor is not None
        assert descriptor.facade_method == name.removeprefix("unity_")
        assert descriptor.idempotent is False
        assert descriptor.destructive is True
        assert descriptor.requires_write_access is True
        schema = _schema(name)
        assert list(schema["properties"]) == ["wait", "timeoutMs"]
        assert schema.get("required", []) == []

    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        pause = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_playmode_pause", {"wait": False, "timeoutMs": 321},
        ))
        resume = asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_playmode_resume", {"wait": True, "timeoutMs": 654},
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)

    assert pause.structuredContent["data"]["requestedState"] == {"isPaused": True}
    assert resume.structuredContent["data"]["requestedState"] == {"isPaused": False}
    assert calls == [
        ("pause", {"wait": False, "timeout_ms": 321}),
        ("resume", {"wait": True, "timeout_ms": 654}),
    ]


def test_wp13_state_mutations_require_write_access_before_native_or_proxy_dispatch(monkeypatch) -> None:
    calls: list[str] = []

    class Facade:
        async def playmode_pause(self, **kwargs):
            calls.append("pause")
            return ok("pause", {})

        async def playmode_resume(self, **kwargs):
            calls.append("resume")
            return ok("resume", {})

        async def sceneview_set_maximized(self, **kwargs):
            calls.append("sceneview")
            return ok("sceneview", {})

    facade = Facade()
    monkeypatch.setattr(status_tools, "_get_facade", lambda: facade)
    cases = (
        ("unity_playmode_pause", {"wait": True, "timeoutMs": 5000}),
        ("unity_playmode_resume", {"wait": True, "timeoutMs": 5000}),
        ("unity_sceneview_set_maximized", {"instanceId": 42, "maximized": True}),
    )
    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", False)
    try:
        for name, arguments in cases:
            native = asyncio.run(runtime.mcp._tool_manager.call_tool(name, arguments))
            assert native.structuredContent["ok"] is False
            assert native.structuredContent["error"]["code"] == "WRITE_ACCESS_NOT_APPROVED"

            proxy = asyncio.run(dispatch_public_tool(facade, name, arguments))
            assert not proxy.ok
            assert proxy.error and proxy.error.code == "WRITE_ACCESS_NOT_APPROVED"
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)
    assert calls == []


def test_editor_window_history_schema_and_proxy_are_read_only(monkeypatch) -> None:
    calls: list[dict] = []

    class Facade:
        async def editor_window_history(self, **kwargs):
            calls.append(kwargs)
            return ok("history", {"events": []})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    schema = _schema("unity_editor_window_history")
    assert list(schema["properties"]) == ["instanceId", "afterSequence", "count"]
    result = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_editor_window_history", {"instanceId": "42", "afterSequence": 9, "count": 2},
    ))

    assert result.structuredContent["data"]["events"] == []
    assert calls == [{"instance_id": "42", "after_sequence": 9, "count": 2}]


def test_safe_window_tools_expose_only_explicit_type_and_identity(monkeypatch) -> None:
    calls: list[tuple[str, dict]] = []

    class Facade:
        async def editor_window_open(self, **kwargs):
            calls.append(("open", kwargs))
            return ok("open", {"ok": True})

        async def editor_window_focus(self, **kwargs):
            calls.append(("focus", kwargs))
            return ok("focus", {"ok": True})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    open_schema = _schema("unity_editor_window_open")
    focus_schema = _schema("unity_editor_window_focus")
    assert list(open_schema["properties"]) == ["typeName"]
    assert list(focus_schema["properties"]) == ["instanceId", "domainGeneration"]
    asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_editor_window_open", {"typeName": "CodingRiver.UPilot.UPilotSafeWindowProbe"},
    ))
    asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_editor_window_focus", {"instanceId": "42", "domainGeneration": "48"},
    ))
    assert calls == [
        ("open", {"type_name": "CodingRiver.UPilot.UPilotSafeWindowProbe"}),
        ("focus", {"instance_id": "42", "domain_generation": "48"}),
    ]


@pytest.mark.parametrize("contains", ["P2Contract", ["P2Contract", "Second"]])
def test_direct_console_search_accepts_scalar_or_array_contains(monkeypatch, contains) -> None:
    calls: list[dict] = []

    class Facade:
        async def console_search_logs(self, **kwargs):
            calls.append(kwargs)
            return ok("search", {"contains": kwargs["contains"]})

    monkeypatch.setattr(status_tools, "_get_facade", lambda: Facade())
    result = asyncio.run(
        runtime.mcp._tool_manager.call_tool(
            "unity_console_search_logs", {"contains": contains}
        )
    )
    expected = [contains] if isinstance(contains, str) else contains
    assert result.structuredContent["data"]["contains"] == expected
    assert calls[0]["contains"] == expected


def test_proxy_unknown_argument_is_rejected_before_capture_dispatch() -> None:
    calls = 0

    class Facade:
        async def console_capture_start(
            self,
            title: str = "",
            path: str = "",
            include_stack_trace: bool = True,
            exclude_upilot: bool = True,
            clear_unity_console: bool = False,
            flush_interval_ms: int = 1000,
            max_file_bytes: int = 50 * 1024 * 1024,
            allow_outside_project: bool = False,
        ):
            nonlocal calls
            calls += 1
            return ok("capture", {"excludeUPilot": exclude_upilot})

    result = asyncio.run(
        dispatch_public_tool(
            Facade(), "unity_console_capture_start", {"excludePilot": False}
        )
    )
    assert not result.ok
    assert result.error and result.error.code == "INVALID_TOOL_ARGUMENTS"
    assert result.error.detail["unknownArguments"] == ["excludePilot"]
    assert calls == 0


def test_keyboard_schema_exposes_exact_actions_and_action_conditions() -> None:
    action = _schema("unity_keyboard_event")["properties"]["action"]
    assert action["enum"] == ["keydown", "keyup", "keypress", "type"]
    assert "严格使用小写" in action["description"]

    properties = _schema("unity_keyboard_event")["properties"]
    assert "keydown、keyup、keypress" in properties["keyCode"]["description"]
    assert "type 动作" in properties["text"]["description"]


def test_keyboard_domain_rejects_invalid_action_before_dispatch_and_dispatches_each_valid_action_once() -> None:
    service = StatusDomainService.__new__(StatusDomainService)
    calls: list[dict] = []

    class Dispatcher:
        async def call(self, request_id, name, payload):
            calls.append({"name": name, "payload": payload})
            return ok(request_id, payload)

    service.dispatcher = Dispatcher()
    rejected = asyncio.run(
        service.keyboard_event("KeyPress", "P2 Contract", key_code="Return")
    )
    assert not rejected.ok
    assert rejected.error and rejected.error.code == "INVALID_KEYBOARD_ACTION"
    assert rejected.error.detail["candidates"] == ["keydown", "keyup", "keypress", "type"]
    assert rejected.error.detail["sideEffectsMayHaveOccurred"] is False
    assert calls == []

    for action in ("keydown", "keyup", "keypress", "type"):
        result = asyncio.run(
            service.keyboard_event(
                action,
                "P2 Contract",
                key_code="Return" if action != "type" else "",
                text="x" if action == "type" else "",
            )
        )
        assert result.ok
    assert [call["payload"]["action"] for call in calls] == [
        "keydown", "keyup", "keypress", "type"
    ]


def test_native_and_proxy_keyboard_enum_errors_never_dispatch(monkeypatch) -> None:
    native_calls = 0

    class NativeFacade:
        async def keyboard_event(self, **kwargs):
            nonlocal native_calls
            native_calls += 1
            return ok("keyboard", kwargs)

    monkeypatch.setattr(status_tools, "_get_facade", lambda: NativeFacade())
    with pytest.raises(ValueError) as native_error:
        asyncio.run(
            runtime.mcp._tool_manager.call_tool(
                "unity_keyboard_event",
                {"action": "KeyPress", "targetWindow": "P2 Contract"},
            )
        )
    native_detail = json.loads(str(native_error.value))
    assert native_detail["invalidArguments"] == [{"name": "action", "value": "KeyPress"}]
    assert native_detail["candidates"]["action"] == ["keydown", "keyup", "keypress", "type"]
    assert native_detail["sideEffectsMayHaveOccurred"] is False
    assert native_calls == 0

    proxy_calls = 0

    class ProxyFacade:
        async def keyboard_event(
            self,
            action: str,
            target_window: str,
            key_code: str = "",
            character: str = "",
            text: str = "",
            modifiers=None,
            window_instance_id: str = "",
            escape_generic_menu: bool = False,
        ):
            nonlocal proxy_calls
            proxy_calls += 1
            return ok("keyboard", {"action": action})

    approved = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        proxy = asyncio.run(
            dispatch_public_tool(
                ProxyFacade(),
                "unity_keyboard_event",
                {"action": "keyPress", "targetWindow": "P2 Contract"},
            )
        )
    finally:
        object.__setattr__(CONFIG, "write_access_approved", approved)
    assert not proxy.ok
    assert proxy.error and proxy.error.code == "INVALID_TOOL_ARGUMENTS"
    assert proxy.error.detail["candidates"] == ["keydown", "keyup", "keypress", "type"]
    assert proxy.error.detail["sideEffectsMayHaveOccurred"] is False
    assert proxy_calls == 0
