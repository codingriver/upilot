from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


def _canonical_path(value: str | os.PathLike[str]) -> str:
    return os.path.normcase(os.path.realpath(os.fspath(value)))


def _load_project_endpoint(project_root: Path, explicit_http_port: int | None) -> tuple[str, int]:
    host = "127.0.0.1"
    port = explicit_http_port or 0
    config_path = project_root / ".upilot" / "config.json"
    if config_path.is_file():
        try:
            config = json.loads(config_path.read_text(encoding="utf-8-sig"))
            mcp = config.get("mcp") if isinstance(config, dict) else {}
            if isinstance(mcp, dict):
                host = str(mcp.get("httpHost") or host)
                if not port:
                    port = int(mcp.get("httpPort") or 0)
        except (OSError, ValueError, TypeError):
            pass
    return host, port or 8011


def _read_json(url: str, timeout_s: float) -> dict[str, Any]:
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout_s) as response:
        return json.loads(response.read().decode("utf-8"))


def _parse_sse_json(raw: bytes) -> dict[str, Any]:
    text = raw.decode("utf-8").replace("\r", "")
    for block in text.split("\n\n"):
        payload = "\n".join(
            line[6:] for line in block.splitlines() if line.startswith("data: ")
        )
        if payload:
            return json.loads(payload)
    raise ValueError("MCP response did not contain an SSE data event.")


def _call_tool(
    mcp_url: str,
    request_id: int,
    name: str,
    arguments: dict[str, Any],
    timeout_s: float,
) -> dict[str, Any]:
    body = json.dumps(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "tools/call",
            "params": {"name": name, "arguments": arguments},
        },
        separators=(",", ":"),
    ).encode("utf-8")
    request = urllib.request.Request(
        mcp_url,
        data=body,
        method="POST",
        headers={
            "Accept": "application/json, text/event-stream",
            "Content-Type": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=timeout_s) as response:
        envelope = _parse_sse_json(response.read())
    return envelope.get("result", {}).get("structuredContent", {})


def run_compile_driver(
    project_path: str,
    *,
    http_port: int | None = None,
    timeout_s: float = 600.0,
) -> dict[str, Any]:
    project_root = Path(project_path).expanduser().resolve()
    result: dict[str, Any] = {
        "ok": False,
        "projectPath": str(project_root),
        "attachedToExistingEditor": False,
        "startedSecondUnityInstance": False,
        "mcpUnavailable": False,
        "verificationBoundary": "Unity compile was not performed.",
    }
    if not (project_root / "Assets").is_dir() or not (project_root / "ProjectSettings" / "ProjectVersion.txt").is_file():
        result.update(
            code="INVALID_UNITY_PROJECT",
            error="projectPath is not a Unity project.",
            nextAction="Pass the Unity project root containing Assets and ProjectSettings/ProjectVersion.txt.",
        )
        return result

    host, port = _load_project_endpoint(project_root, http_port)
    health_url = f"http://{host}:{port}/health"
    mcp_url = f"http://{host}:{port}/mcp"
    result.update(httpPort=port, healthUrl=health_url, mcpUrl=mcp_url)
    try:
        health = _read_json(health_url, min(timeout_s, 5.0))
    except (OSError, ValueError, urllib.error.URLError) as ex:
        result.update(
            code="MCP_UNAVAILABLE",
            error=str(ex),
            mcpUnavailable=True,
            nextAction=(
                "Start or recover the Managed MCP Server for this already-open project, then rerun the compile driver. "
                "Do not launch a second Unity instance."
            ),
        )
        return result

    result.update(
        serverPid=int(health.get("server_pid") or 0),
        unityPid=0,
        unityConnected=bool(health.get("unity_connected")),
        unityVersion=str(health.get("unity_version") or ""),
    )
    health_project = str(health.get("project_path") or "")
    if not health_project or _canonical_path(health_project) != _canonical_path(project_root):
        result.update(
            code="PROJECT_MISMATCH",
            error=f"Endpoint belongs to '{health_project}', not '{project_root}'.",
            nextAction="Use the target project's configured HTTP port; never compile through another project's MCP endpoint.",
        )
        return result
    if not result["unityConnected"]:
        result.update(
            code="UNITY_NOT_CONNECTED",
            error="The project MCP Server is reachable but its Unity Bridge is not connected.",
            nextAction="Recover the existing project's Unity Bridge connection, then rerun the compile driver.",
        )
        return result

    try:
        status = _call_tool(mcp_url, 1, "unity_mcp_status", {"forceFresh": True}, min(timeout_s, 30.0))
        if not status.get("ok"):
            raise RuntimeError(json.dumps(status.get("error") or {}, ensure_ascii=False))
        status_data = status.get("data") or {}
        status_project = ((status_data.get("paths") or {}).get("unityProjectAbsolute") or "")
        if _canonical_path(status_project) != _canonical_path(project_root):
            raise RuntimeError(f"Fresh MCP status project mismatch: {status_project}")
        session = status_data.get("session") or {}
        result["unityPid"] = int(session.get("processId") or 0)
        result["attachedToExistingEditor"] = True

        sync = _call_tool(
            mcp_url,
            2,
            "unity_sync_after_disk_write",
            {"delayS": 0.0, "triggerCompile": True},
            min(timeout_s, 240.0),
        )
        if not sync.get("ok"):
            result.update(
                code="SYNC_FAILED",
                error=sync.get("error"),
                nextAction=(sync.get("context") or {}).get("nextAction") or "Resolve the sync error and retry.",
            )
            return result

        compiled = _call_tool(
            mcp_url,
            3,
            "unity_safe_compile_and_wait",
            {"timeoutS": timeout_s, "pollIntervalS": 1.0},
            timeout_s + 60.0,
        )
        compile_data = compiled.get("data") or {}
        result.update(
            compileRequestId=compile_data.get("compileRequestId") or "",
            compileStatus=compile_data.get("status") or "",
            compilePhase=compile_data.get("phase") or "",
            errorCount=int(compile_data.get("errorCount") or 0),
            warningCount=int(compile_data.get("warningCount") or 0),
            reconnectedAfterReload=bool(compile_data.get("reconnectedAfterReload")),
            sessionChangedDuringCompile=bool(compile_data.get("sessionChangedDuringCompile")),
        )
        if not compiled.get("ok") or result["compileStatus"] != "success" or result["errorCount"] != 0:
            result.update(
                code="COMPILE_FAILED",
                error=compiled.get("error") or compile_data.get("errors") or "Unity compile failed.",
                nextAction=(compiled.get("context") or {}).get("nextAction") or "Read unity_compile_errors and fix the reported errors.",
                verificationBoundary="Unity compile ran but did not pass.",
            )
            return result

        result.update(
            ok=True,
            code="OK",
            nextAction="",
            verificationBoundary="Existing Unity Editor attached; real Unity compile completed and errors were verified.",
        )
        return result
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as ex:
        result.update(
            code="COMPILE_DRIVER_FAILED",
            error=str(ex),
            nextAction="Inspect the project MCP health/status and retry without launching another Unity instance.",
        )
        return result
