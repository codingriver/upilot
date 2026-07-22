from __future__ import annotations

import os
import json
from importlib import metadata
from pathlib import Path

PROTOCOL_VERSION = "1"


def _build_info() -> dict[str, str]:
    current = Path(__file__).resolve()
    for parent in [current.parent, *current.parents]:
        candidate = parent / "upilot_build_info.json"
        if candidate.is_file():
            try:
                data = json.loads(candidate.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    return {str(k): str(v) for k, v in data.items()}
            except Exception:
                return {}
    return {}


def _read_pyproject_version() -> str:
    current = Path(__file__).resolve()
    for parent in current.parents:
        candidate = parent / "pyproject.toml"
        if not candidate.is_file():
            continue
        for raw in candidate.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if line.startswith("version") and "=" in line:
                return line.split("=", 1)[1].strip().strip('"').strip("'")
    return "0.0.0"


def server_version() -> str:
    value = os.getenv("UPILOT_SERVER_VERSION", "").strip()
    if value:
        return value
    value = _build_info().get("server_version", "").strip()
    if value:
        return value
    try:
        return metadata.version("upilot-mcp")
    except metadata.PackageNotFoundError:
        return _read_pyproject_version()


def build_commit() -> str:
    return os.getenv("UPILOT_BUILD_COMMIT", "").strip() or _build_info().get("build_commit", "").strip()


def build_channel() -> str:
    return os.getenv("UPILOT_BUILD_CHANNEL", "").strip() or _build_info().get("build_channel", "").strip() or "source"


def version_payload() -> dict[str, str]:
    return {
        "server_version": server_version(),
        "build_commit": build_commit(),
        "build_channel": build_channel(),
        "protocol_version": PROTOCOL_VERSION,
    }
