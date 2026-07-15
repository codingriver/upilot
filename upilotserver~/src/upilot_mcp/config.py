from __future__ import annotations

import json
import os
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True, slots=True)
class UPilotConfig:
    schema_version: int = 2
    http_host: str = "127.0.0.1"
    http_port: int = 8011
    ws_host: str = "127.0.0.1"
    ws_port: int = 8765
    context_stale_ms: int = 2000
    flow_enabled: bool = False


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
        context_stale_ms=max(250, int(cache.get("contextStaleMs") or 2000)),
        flow_enabled=bool(flow.get("enabled", False)),
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
                        "url": str(value.get("url") or ""),
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
            servers = parsed.get("mcpServers", {}) if isinstance(parsed, dict) else {}
            if isinstance(servers, dict):
                for name, value in servers.items():
                    if not isinstance(value, dict):
                        continue
                    registrations.append({
                        "client": client,
                        "file": str(path),
                        "name": str(name),
                        "url": str(value.get("url") or value.get("serverUrl") or ""),
                        "timeoutS": value.get("timeout") or value.get("tool_timeout_sec"),
                    })
        except (OSError, ValueError) as exc:
            issues.append({"code": "CLIENT_CONFIG_PARSE_ERROR", "message": f"{path}: {exc}"})

    by_client_and_url: dict[tuple[str, str], list[str]] = {}
    for item in registrations:
        url = str(item.get("url") or "").strip().lower().rstrip("/")
        if not url:
            continue
        client = str(item.get("client") or "unknown")
        by_client_and_url.setdefault((client, url), []).append(str(item["name"]))
        if ":8765" in url or f":{CONFIG.ws_port}" in url:
            issues.append({
                "code": "INTERNAL_BRIDGE_PORT_USED",
                "message": f"{item['client']}:{item['name']} points to an internal WebSocket port: {item['url']}",
            })
        if url.startswith("http") and f":{CONFIG.http_port}/mcp" not in url:
            issues.append({
                "code": "MCP_HTTP_ENDPOINT_MISMATCH",
                "message": f"{item['client']}:{item['name']} should use http://127.0.0.1:{CONFIG.http_port}/mcp",
            })
        timeout = item.get("timeoutS")
        if isinstance(timeout, (int, float)) and timeout < 120:
            issues.append({
                "code": "CLIENT_TIMEOUT_TOO_LOW",
                "message": f"{item['client']}:{item['name']} timeout is {timeout}s; use at least 120s for Unity operations",
            })

    for (client, url), names in by_client_and_url.items():
        if len(names) > 1:
            issues.append({
                "code": "DUPLICATE_MCP_ENDPOINT",
                "message": f"{client} registers endpoint {url} multiple times: {', '.join(names)}",
            })

    return {
        "projectRoot": str(root),
        "expectedEndpoint": f"http://127.0.0.1:{CONFIG.http_port}/mcp",
        "registrations": registrations,
        "issues": issues,
        "ok": not issues,
    }
