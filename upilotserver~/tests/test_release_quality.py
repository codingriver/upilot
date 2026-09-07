import importlib.util
from pathlib import Path

import pytest

from upilot_mcp.source_identity import source_identity


def gate():
    path = Path(__file__).resolve().parents[1] / "scripts" / "check_release_quality.py"
    spec = importlib.util.spec_from_file_location("check_release_quality", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_source_identity_detects_uncommitted_code_but_ignores_generated_caches(tmp_path):
    folder = tmp_path / "Editor"
    folder.mkdir()
    code = folder / "Probe.cs"
    code.write_bytes(b"class Probe {}\r\n")
    first = source_identity(tmp_path)
    code.write_bytes(b"class Probe {}\n")
    assert source_identity(tmp_path) == first
    cache = folder / "__pycache__"
    cache.mkdir()
    (cache / "ignored.pyc").write_bytes(b"cache")
    assert source_identity(tmp_path) == first
    code.write_bytes(b"class Probe { int changed; }\n")
    assert source_identity(tmp_path)["sourceSha256"] != first["sourceSha256"]


def test_unity_gate_rejects_stale_and_missing_evidence():
    verify = gate().verify_unity_summary
    source = {"sourceSha256": "checked-source"}
    report = {
        "acceptancePassed": True, "cleanupVerified": True, "testIdentityVerified": True,
        "sourceUnchanged": True, "sourceIdentity": source, "sourceIdentityAfter": source,
        "runGuid": "run-1", "endedAt": 1,
    }
    verify(report, source)
    with pytest.raises(ValueError):
        verify(report, {"sourceSha256": "new-source"})
    for key in ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged"):
        with pytest.raises(ValueError):
            verify({**report, key: False}, source)


def test_source_identity_includes_non_editor_test_fixtures(tmp_path):
    folder = tmp_path / "Tests" / "Fixtures"
    folder.mkdir(parents=True)
    probe = folder / "Probe.cs"
    probe.write_text("class Probe {}", encoding="utf-8")
    before = source_identity(tmp_path)
    probe.write_text("class Probe { int value; }", encoding="utf-8")
    assert source_identity(tmp_path)["sourceSha256"] != before["sourceSha256"]


def test_tool_inventory_uses_registry_and_real_mcp_schemas():
    inventory = gate().tool_inventory()
    tools = {tool["name"]: tool for tool in inventory["tools"]}
    assert inventory["registeredCount"] == len(tools)
    assert inventory["exposedCount"] == sum(tool["exposedInCurrentConfiguration"] for tool in tools.values())
    assert "confirmToken" in tools["unity_prefab_patch"]["inputSchema"]["properties"]
    assert "maxNodes" in tools["unity_scene_summary"]["inputSchema"]["properties"]
    assert {"testNames", "fixtures"} <= set(tools["unity_test_run"]["inputSchema"]["properties"])
    assert "detailLevel" in tools["unity_task_status"]["inputSchema"]["properties"]
    for name in ("unity_prefab_patch", "unity_scene_summary", "unity_test_run", "unity_task_status"):
        assert tools[name]["proxyHandlerAvailable"]
    assert inventory["proxyHandlerGaps"] == sorted(
        name for name, tool in tools.items() if not tool["proxyHandlerAvailable"]
    )
    assert inventory["proxyHandlerGaps"] == []


def test_tool_inventory_rejects_missing_proxy_handler(monkeypatch):
    from dataclasses import replace
    from upilot_mcp.tool_registry import REGISTRY

    descriptor = REGISTRY.resolve("unity_compile_errors_get")
    monkeypatch.setitem(REGISTRY._items, descriptor.name, replace(descriptor, facade_method="missing_handler"))
    with pytest.raises(ValueError, match="missing proxy handler"):
        gate().tool_inventory()
