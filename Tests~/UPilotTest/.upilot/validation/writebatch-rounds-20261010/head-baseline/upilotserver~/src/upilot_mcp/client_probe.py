"""Read-only Streamable HTTP handshake using only discovery and proxy tools."""
from __future__ import annotations

import argparse
import asyncio
import json
import os
from pathlib import Path
from urllib.parse import urlsplit

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client


def _payload(result) -> dict:
    def checked(value: dict) -> dict:
        if result.isError and not (value.get("ok") is False and isinstance(value.get("error"), dict)):
            raise ValueError("MCP protocol error: " + json.dumps(value, ensure_ascii=False)[:1500])
        return value

    if isinstance(result.structuredContent, dict):
        data = result.structuredContent
        # FastMCP may wrap a scalar JSON result.
        if "result" in data and isinstance(data["result"], dict):
            return checked(data["result"])
        return checked(data)
    for item in result.content:
        if getattr(item, "type", "") == "text":
            try:
                value = json.loads(item.text)
            except json.JSONDecodeError:
                if result.isError:
                    raise ValueError("MCP protocol error: " + item.text[:1500]) from None
                continue
            if isinstance(value, dict):
                return checked(value)
    raise ValueError("MCP call did not return an object.")


class DiscoveryProxyClient:
    """Expose exactly the two stable entry points to a calling agent."""
    exposed_tools = ("unity_tools_find", "unity_tool_call")

    def __init__(self, session):
        self.session = session

    async def call(self, name: str, arguments: dict) -> dict:
        if name not in self.exposed_tools:
            raise ValueError("Typed tool is not exposed by this restricted client.")
        return _payload(await self.session.call_tool(name, arguments))


async def probe_session(session, expected_project: str, report: dict) -> dict:
    await session.initialize()
    report["httpConnected"] = True
    visible = set()
    cursor = None
    seen_cursors = set()
    for _ in range(32):
        page = await session.list_tools(cursor=cursor)
        visible.update(tool.name for tool in page.tools)
        cursor = page.nextCursor
        if not cursor:
            break
        if cursor in seen_cursors:
            raise ValueError("Repeated tools/list cursor.")
        seen_cursors.add(cursor)
    else:
        raise ValueError("tools/list exceeded the bounded page budget.")
    report["serverToolListVisible"] = True
    report["stableEntrypointsVisible"] = all(name in visible for name in DiscoveryProxyClient.exposed_tools)
    if not report["stableEntrypointsVisible"]:
        raise ValueError("Missing discovery/proxy entry points; refresh the server and client tool list.")
    client = DiscoveryProxyClient(session)
    discovery = await client.call("unity_tools_find", {"query": "unity_mcp_status", "limit": 5})
    report["discoveryCallSucceeded"] = discovery.get("ok") is True
    if not report["discoveryCallSucceeded"]:
        raise ValueError("Discovery call failed.")
    status = await client.call("unity_tool_call", {
        "toolName": "unity_mcp_status", "args": {"forceFresh": True, "includeCapabilities": False},
    })
    report["actualCallSucceeded"] = status.get("ok") is True
    data = status.get("data") or {}
    actual = (data.get("paths") or {}).get("unityProjectAbsolute") or ""
    report["projectPath"] = actual
    report["unityConnected"] = data.get("connected") is True
    report["serverReady"] = data.get("serverReady") is True
    report["projectIdentityVerified"] = bool(actual) and os.path.normcase(str(Path(actual).resolve())) == os.path.normcase(str(Path(expected_project).resolve()))
    report["passed"] = all(report.get(key) is True for key in (
        "httpConnected", "stableEntrypointsVisible", "discoveryCallSucceeded", "actualCallSucceeded",
        "unityConnected", "serverReady", "projectIdentityVerified",
    ))
    return report


async def probe(url: str, expected_project: str, timeout_sec: float = 30) -> dict:
    report = dict(endpoint=url, httpConnected=False, serverToolListVisible=False,
                  stableEntrypointsVisible=False, clientToolListInjected="unknown",
                  clientExposedTools=list(DiscoveryProxyClient.exposed_tools),
                  actualCallSucceeded=False, projectIdentityVerified=False, passed=False)
    parsed = urlsplit(url)
    if parsed.scheme not in ("http", "https") or parsed.path.rstrip("/") != "/mcp" or parsed.username or parsed.password:
        report["error"] = "Use a Streamable HTTP /mcp endpoint without embedded credentials."
        return report
    try:
        async with asyncio.timeout(timeout_sec):
            async with streamable_http_client(url) as (read, write, _):
                async with ClientSession(read, write) as session:
                    await probe_session(session, expected_project, report)
    except Exception as exc:
        report["passed"] = False
        report["error"] = str(exc)[:2000]
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8011/mcp")
    parser.add_argument("--project", required=True)
    parser.add_argument("--timeout", type=float, default=30)
    args = parser.parse_args()
    report = asyncio.run(probe(args.url, args.project, args.timeout))
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
