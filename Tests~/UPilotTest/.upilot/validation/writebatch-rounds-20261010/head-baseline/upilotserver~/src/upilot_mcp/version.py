from __future__ import annotations

import os
import json
import sys
import time
import uuid
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
    return ""


def server_version() -> str:
    if "_RUNTIME_IDENTITY" in globals():
        return _RUNTIME_IDENTITY["server_version"]
    value = os.getenv("UPILOT_SERVER_VERSION", "").strip()
    if value:
        return value
    value = _build_info().get("server_version", "").strip()
    if value:
        return value
    value = _read_pyproject_version().strip()
    if value:
        return value
    try:
        return metadata.version("upilot-mcp")
    except metadata.PackageNotFoundError:
        return "0.0.0"


def build_commit() -> str:
    if "_RUNTIME_IDENTITY" in globals():
        return _RUNTIME_IDENTITY["build_commit"]
    return os.getenv("UPILOT_BUILD_COMMIT", "").strip() or _build_info().get("build_commit", "").strip()


def build_channel() -> str:
    if "_RUNTIME_IDENTITY" in globals():
        return _RUNTIME_IDENTITY["build_channel"]
    return os.getenv("UPILOT_BUILD_CHANNEL", "").strip() or _build_info().get("build_channel", "").strip() or "source"


# Capture once, before serving requests. New files on disk do not describe an
# already running process (in particular after a UPM remove/add operation).
_RUNTIME_IDENTITY = {
    "server_version": server_version(),
    "build_commit": build_commit(),
    "build_channel": build_channel(),
    "protocol_version": PROTOCOL_VERSION,
    "identity_contract_version": 1,
    "server_instance_id": uuid.uuid4().hex,
    "server_started_at_ms": int(time.time() * 1000),
    "server_entry_path": str(Path(sys.executable if getattr(sys, "frozen", False) else sys.argv[0]).resolve()),
    "server_module_root": str(Path(__file__).resolve().parent),
    "server_install_source": os.getenv("UPILOT_INSTALL_SOURCE", "unknown"),
}


def version_payload() -> dict:
    return dict(_RUNTIME_IDENTITY)
