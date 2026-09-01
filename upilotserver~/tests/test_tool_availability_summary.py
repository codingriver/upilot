from __future__ import annotations

from upilot_mcp.mcp_stdio_server import _build_tool_availability_summary
from upilot_mcp.tool_registry import REGISTRY


def test_tool_availability_summary_distinguishes_registered_available_and_callable() -> None:
    ready = _build_tool_availability_summary(
        flow_enabled=True,
        connected=True,
        server_ready=True,
        write_access_approved=True,
    )
    read_only = _build_tool_availability_summary(
        flow_enabled=True,
        connected=True,
        server_ready=True,
        write_access_approved=False,
    )
    disconnected = _build_tool_availability_summary(
        flow_enabled=True,
        connected=False,
        server_ready=False,
        write_access_approved=True,
    )

    assert ready["registered_tool_count"] == len(REGISTRY.list())
    assert ready["available_tool_count"] <= ready["registered_tool_count"]
    assert ready["callable_tool_count"] == ready["available_tool_count"]
    assert read_only["callable_tool_count"] < read_only["available_tool_count"]
    assert disconnected["callable_tool_count"] < disconnected["available_tool_count"]
    assert ready["tool_count"] == ready["available_tool_count"]
    assert ready["registry_version"] > 0

    category_total = sum(
        int(item.rsplit(":", 1)[1])
        for item in ready["tool_category_summary"].split(",")
        if item
    )
    assert category_total == ready["available_tool_count"]
