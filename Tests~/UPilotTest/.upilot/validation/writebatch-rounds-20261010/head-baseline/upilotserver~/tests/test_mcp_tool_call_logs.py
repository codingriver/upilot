from __future__ import annotations

import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from mcp.types import CallToolResult, TextContent

from upilot_mcp import mcp_stdio_server as runtime


TARGET_CASES = (
    ("csharp_eval", {"code": "1 + 1"}),
    ("unity_reflection_call", {"expression": "1 + 1"}),
    ("csharp_object_dump", {"sessionId": "session-1", "handle": "handle-1"}),
)


def _result(value: str = "ok") -> CallToolResult:
    structured = {"ok": True, "data": {"value": value}}
    return CallToolResult(
        content=[TextContent(type="text", text=json.dumps(structured))],
        structuredContent=structured,
        isError=False,
    )


def _set_project(monkeypatch: pytest.MonkeyPatch, project_root: Path) -> None:
    facade = SimpleNamespace(
        server=SimpleNamespace(
            session_manager=SimpleNamespace(
                active=SimpleNamespace(project_path=str(project_root)),
            ),
            state=SimpleNamespace(project_path=""),
        ),
    )
    monkeypatch.setattr(runtime, "_facade", facade)


def _log_path(project_root: Path, tool_name: str) -> Path:
    return (
        project_root
        / "Logs"
        / "UPilot"
        / "McpCalls"
        / runtime._MCP_TOOL_LOG_FILES[tool_name]
    )


@pytest.mark.parametrize(("tool_name", "arguments"), TARGET_CASES)
def test_target_tool_call_writes_its_own_log(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    tool_name: str,
    arguments: dict,
) -> None:
    _set_project(monkeypatch, tmp_path)
    expected = _result(tool_name)

    async def fake_call(name, normalized, context=None, convert_result=False):
        assert name == tool_name
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    actual = asyncio.run(runtime._call_tool_with_strict_arguments(tool_name, arguments))

    assert actual is expected
    entry = json.loads(_log_path(tmp_path, tool_name).read_text(encoding="utf-8"))
    assert entry["tool"] == tool_name
    assert entry["parameters"] == arguments
    assert entry["result"]["isError"] is False
    assert entry["result"]["structuredContent"]["data"]["value"] == tool_name
    created = {
        path.name
        for path in (tmp_path / "Logs" / "UPilot" / "McpCalls").iterdir()
    }
    assert created == {runtime._MCP_TOOL_LOG_FILES[tool_name]}


def test_non_target_tool_does_not_create_dedicated_log(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _set_project(monkeypatch, tmp_path)
    expected = _result()

    async def fake_call(name, normalized, context=None, convert_result=False):
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    actual = asyncio.run(runtime._call_tool_with_strict_arguments("unity_mcp_status", {}))

    assert actual is expected
    assert not (tmp_path / "Logs" / "UPilot" / "McpCalls").exists()


def test_exception_is_logged_and_re_raised(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _set_project(monkeypatch, tmp_path)
    expected = RuntimeError("boom")

    async def fake_call(name, normalized, context=None, convert_result=False):
        raise expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    with pytest.raises(RuntimeError) as captured:
        asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "fail"}))

    assert captured.value is expected
    entry = json.loads(_log_path(tmp_path, "csharp_eval").read_text(encoding="utf-8"))
    assert entry["parameters"] == {"code": "fail"}
    assert entry["error"] == {"type": "RuntimeError", "message": "boom"}


def test_log_is_rewritten_when_next_record_would_exceed_limit(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _set_project(monkeypatch, tmp_path)
    monkeypatch.setattr(runtime, "_MCP_TOOL_LOG_MAX_BYTES", 1024)
    path = _log_path(tmp_path, "csharp_eval")
    path.parent.mkdir(parents=True)
    path.write_bytes(b"old" * 300)
    expected = _result()

    async def fake_call(name, normalized, context=None, convert_result=False):
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"}))

    contents = path.read_bytes()
    assert len(contents) <= 1024
    assert b"oldoldold" not in contents
    assert json.loads(contents)["parameters"] == {"code": "1"}


def test_single_oversized_record_is_truncated_to_limit(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _set_project(monkeypatch, tmp_path)
    monkeypatch.setattr(runtime, "_MCP_TOOL_LOG_MAX_BYTES", 512)
    expected = _result()

    async def fake_call(name, normalized, context=None, convert_result=False):
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    asyncio.run(runtime._call_tool_with_strict_arguments(
        "csharp_eval",
        {"code": "中" * 5000},
    ))

    contents = _log_path(tmp_path, "csharp_eval").read_bytes()
    assert len(contents) <= 512
    entry = json.loads(contents)
    assert entry["tool"] == "csharp_eval"
    assert entry["truncated"] is True
    assert entry["originalBytes"] > 512
    assert entry["preview"].endswith("[TRUNCATED]")


def test_missing_project_path_skips_log_without_changing_result(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    facade = SimpleNamespace(
        server=SimpleNamespace(
            session_manager=SimpleNamespace(active=None),
            state=SimpleNamespace(project_path=""),
        ),
    )
    monkeypatch.setattr(runtime, "_facade", facade)
    monkeypatch.chdir(tmp_path)
    expected = _result()

    async def fake_call(name, normalized, context=None, convert_result=False):
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    actual = asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"}))

    assert actual is expected
    assert not (tmp_path / "Logs").exists()


def test_log_write_failure_does_not_change_tool_result(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    _set_project(monkeypatch, tmp_path)
    (tmp_path / "Logs").write_text("not a directory", encoding="utf-8")
    expected = _result()

    async def fake_call(name, normalized, context=None, convert_result=False):
        return expected

    monkeypatch.setattr(runtime, "_original_tool_manager_call_tool", fake_call)

    actual = asyncio.run(runtime._call_tool_with_strict_arguments("csharp_eval", {"code": "1"}))

    assert actual is expected
