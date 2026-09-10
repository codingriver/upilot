from __future__ import annotations

import importlib.util
import json
from pathlib import Path

import pytest


REPO_ROOT = Path(__file__).resolve().parents[2]
BUILD_PATH = REPO_ROOT / "upilotserver~" / "deploy" / "build_server_exe.py"
SPEC = importlib.util.spec_from_file_location("build_server_exe", BUILD_PATH)
assert SPEC and SPEC.loader
build_server_exe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_server_exe)


def test_release_manifest_requires_a_matching_sha256(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(build_server_exe, "DIST", tmp_path)
    exe = tmp_path / "upilot-mcp-server-9.8.7-win-x64.exe"
    exe.write_bytes(b"verified server")

    manifest_path = build_server_exe.write_manifest(
        exe,
        version="9.8.7",
        upm_version="9.8.7",
        channel="release",
        commit="abc",
        protocol_version="1",
        base_url="https://example.test/release",
    )
    build_server_exe.verify_release_manifest(manifest_path)

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["downloads"][0]["sha256"] = "0" * 64
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="SHA256 mismatch"):
        build_server_exe.verify_release_manifest(manifest_path)
