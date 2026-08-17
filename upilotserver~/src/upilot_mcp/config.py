from __future__ import annotations

import json
import os
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


@dataclass(frozen=True, slots=True)
class UPilotConfig:
    schema_version: int = 2
    http_host: str = "127.0.0.1"
    http_port: int = 8011
    ws_host: str = "127.0.0.1"
    ws_port: int = 8765
    context_stale_ms: int = 5000
    flow_enabled: bool = False
    write_access_approved: bool = False


def _project_config_path() -> Path:
    explicit = os.getenv("UPILOT_CONFIG", "").strip()
    if explicit:
        return Path(explicit).expanduser().resolve()
    return (Path.cwd() / ".upilot" / "config.json").resolve()


def load_config() -> UPilotConfig:
    raw: dict[str, Any] = {}
    path = _project_config_path()
    if path.is_file():
        try:
            parsed = json.loads(path.read_text(encoding="utf-8-sig"))
            if isinstance(parsed, dict):
                raw = parsed
        except (OSError, ValueError):
            raw = {}

    mcp = raw.get("mcp") if isinstance(raw.get("mcp"), dict) else {}
    cache = raw.get("cache") if isinstance(raw.get("cache"), dict) else {}
    features = raw.get("features") if isinstance(raw.get("features"), dict) else {}
    flow = features.get("flow") if isinstance(features.get("flow"), dict) else {}
    safety = raw.get("safety") if isinstance(raw.get("safety"), dict) else {}

    def env_int(name: str, fallback: int) -> int:
        value = os.getenv(name, "").strip()
        if not value:
            return fallback
        try:
            return int(value)
        except ValueError:
            return fallback

    return UPilotConfig(
        schema_version=int(raw.get("schemaVersion") or 2),
        http_host=os.getenv("UPILOT_HTTP_HOST", str(mcp.get("httpHost") or "127.0.0.1")),
        http_port=env_int("UPILOT_HTTP_PORT", int(mcp.get("httpPort") or 8011)),
        ws_host=os.getenv("UPILOT_HOST", str(mcp.get("wsHost") or "127.0.0.1")),
        ws_port=env_int("UPILOT_PORT", int(mcp.get("wsPort") or 8765)),
        context_stale_ms=max(250, int(cache.get("contextStaleMs") or 5000)),
        flow_enabled=bool(flow.get("enabled", False)),
        write_access_approved=bool(safety.get("writeAccessApproved", False)),
    )


CONFIG = load_config()


def diagnose_client_configs(project_root: Path | None = None) -> dict[str, Any]:
    root = (project_root or Path.cwd()).resolve()
    registrations: list[dict[str, Any]] = []
    issues: list[dict[str, str]] = []

    codex_path = root / ".codex" / "config.toml"
    if codex_path.is_file():
        try:
            parsed = tomllib.loads(codex_path.read_text(encoding="utf-8-sig"))
            servers = parsed.get("mcp_servers", {}) if isinstance(parsed, dict) else {}
            if isinstance(servers, dict):
                for name, value in servers.items():
                    if not isinstance(value, dict):
                        continue
                    registrations.append({
                        "client": "codex",
                        "file": str(codex_path),
                        "name": str(name),
                        "transport": str(value.get("transport") or ""),
                        "url": str(value.get("url") or ""),
                        "command": str(value.get("command") or ""),
                        "args": value.get("args") if isinstance(value.get("args"), list) else [],
                        "timeoutS": value.get("tool_timeout_sec"),
                    })
        except (OSError, ValueError) as exc:
            issues.append({"code": "CLIENT_CONFIG_PARSE_ERROR", "message": f"{codex_path}: {exc}"})

    for client, relative in (
        ("generic", ".mcp.json"),
        ("cursor", ".cursor/mcp.json"),
        ("vscode", ".vscode/mcp.json"),
    ):
        path = root / relative
        if not path.is_file():
            continue
        try:
            parsed = json.loads(path.read_text(encoding="utf-8-sig"))
            servers: dict[str, Any] = {}
            if isinstance(parsed, dict):
                for key in ("mcpServers", "servers"):
                    candidate = parsed.get(key)
                    if isinstance(candidate, dict):
                        servers.update(candidate)
            if isinstance(servers, dict):
                for name, value in servers.items():
                    if not isinstance(value, dict):
                        continue
                    registrations.append({
                        "client": client,
                        "file": str(path),
                        "name": str(name),
                        "transport": str(value.get("transport") or value.get("type") or ""),
                        "url": str(value.get("url") or value.get("serverUrl") or ""),
                        "command": str(value.get("command") or ""),
                        "args": value.get("args") if isinstance(value.get("args"), list) else [],
                        "timeoutS": value.get("timeout") or value.get("tool_timeout_sec"),
                    })
        except (OSError, ValueError) as exc:
            issues.append({"code": "CLIENT_CONFIG_PARSE_ERROR", "message": f"{path}: {exc}"})

    expected_url = f"http://127.0.0.1:{CONFIG.http_port}/mcp"
    expected_normalized = expected_url.lower().rstrip("/")
    by_client_and_url: dict[tuple[str, str], list[str]] = {}
    for item in registrations:
        url = str(item.get("url") or "").strip().lower().rstrip("/")
        name = str(item.get("name") or "")
        command = str(item.get("command") or "")
        args = item.get("args") if isinstance(item.get("args"), list) else []
        transport = str(item.get("transport") or "").strip().lower()
        identity = " ".join([name, url, command, *[str(arg) for arg in args]]).lower()
        parsed_url = urlparse(url) if url else None
        try:
            url_port = parsed_url.port if parsed_url else None
        except ValueError:
            url_port = None
        is_upilot = (
            "upilot" in identity
            or url_port in {CONFIG.http_port, CONFIG.ws_port}
            and parsed_url is not None
            and parsed_url.hostname in {"127.0.0.1", "localhost", "::1"}
        )
        item["isUpilot"] = is_upilot
        if not is_upilot:
            continue

        process_config = bool(command or args) or transport in {"stdio", "process", "command"}
        if process_config:
            bridge_port_arg = any(
                str(arg) == str(CONFIG.ws_port) or str(arg).endswith(f"={CONFIG.ws_port}")
                for arg in args
            )
            detail = " It also exposes the internal Unity Bridge port." if bridge_port_arg else ""
            issues.append({
                "code": "NON_HTTP_UPILOT_TRANSPORT",
                "message": (
                    f"{item['client']}:{item['name']} uses a command/process MCP registration.{detail} "
                    "Third-party AI clients must use Streamable HTTP only."
                ),
                "nextAction": f"Replace it with url = \"{expected_url}\" and remove command, args, and stdio transport settings.",
            })

        if not url:
            continue
        client = str(item.get("client") or "unknown")
        by_client_and_url.setdefault((client, url), []).append(str(item["name"]))
        if parsed_url and (parsed_url.scheme in {"ws", "wss"} or url_port == CONFIG.ws_port):
            issues.append({
                "code": "INTERNAL_BRIDGE_PORT_USED",
                "message": f"{item['client']}:{item['name']} points to an internal WebSocket port: {item['url']}",
                "nextAction": f"Use the public MCP endpoint {expected_url} instead.",
            })

        valid_http_shape = bool(
            parsed_url
            and parsed_url.scheme in {"http", "https"}
            and parsed_url.hostname in {"127.0.0.1", "localhost", "::1"}
            and parsed_url.path.rstrip("/") == "/mcp"
        )
        primary_registration = name.strip().lower() == "upilot"
        endpoint_mismatch = not valid_http_shape or (
            primary_registration and url != expected_normalized
        )
        if endpoint_mismatch:
            issues.append({
                "code": "MCP_HTTP_ENDPOINT_MISMATCH",
                "message": f"{item['client']}:{item['name']} should use {expected_url}",
                "nextAction": "Use an HTTP /mcp URL. For concurrent projects, give each project a distinct MCP name and HTTP port.",
            })
        timeout = item.get("timeoutS")
        if isinstance(timeout, (int, float)) and timeout < 120:
            issues.append({
                "code": "CLIENT_TIMEOUT_TOO_LOW",
                "message": f"{item['client']}:{item['name']} timeout is {timeout}s; use at least 120s for Unity operations",
                "nextAction": "Set the client tool timeout to at least 120 seconds; 300 seconds is recommended.",
            })

    for (client, url), names in by_client_and_url.items():
        if len(names) > 1:
            issues.append({
                "code": "DUPLICATE_MCP_ENDPOINT",
                "message": f"{client} registers endpoint {url} multiple times: {', '.join(names)}",
                "nextAction": "Keep one registration per client and endpoint.",
            })

    return {
        "projectRoot": str(root),
        "expectedEndpoint": expected_url,
        "registrations": registrations,
        "issues": issues,
        "ok": not issues,
    }
