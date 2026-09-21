from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess

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


def test_server_exe_build_bundles_manifest_and_all_templates_before_entry_script(
    tmp_path: Path, monkeypatch
) -> None:
    repo = tmp_path / "repo"
    server = repo / "upilotserver~"
    skill = repo / "skills" / "upilot-unity-mcp"
    for relative in (
        "template-manifest.json",
        "AGENTS.md.template",
        "SKILL.md.template",
        "agents/openai.yaml.template",
    ):
        path = skill / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(relative, encoding="utf-8")
    (server / "src" / "upilot_mcp").mkdir(parents=True)
    entry = server / "run_upilot_mcp.py"
    entry.write_text("pass\n", encoding="utf-8")
    dist = tmp_path / "dist"
    captured: list[str] = []

    def fake_run(cmd, **kwargs):
        captured.extend(str(value) for value in cmd)
        name = cmd[cmd.index("--name") + 1]
        dist.mkdir(parents=True, exist_ok=True)
        (dist / f"{name}.exe").write_bytes(b"exe")
        return subprocess.CompletedProcess(cmd, 0)

    monkeypatch.setattr(build_server_exe, "REPO_ROOT", repo)
    monkeypatch.setattr(build_server_exe, "SERVER_ROOT", server)
    monkeypatch.setattr(build_server_exe, "DIST", dist)
    monkeypatch.setattr(build_server_exe, "ensure_pyinstaller", lambda: None)
    monkeypatch.setattr(build_server_exe.subprocess, "run", fake_run)

    exe = build_server_exe.build_exe("1.2.3", "test", "abc")

    assert exe.is_file()
    assert captured[-1] == str(entry)
    add_data = [captured[index + 1] for index, value in enumerate(captured) if value == "--add-data"]
    for relative in (
        "template-manifest.json",
        "AGENTS.md.template",
        "SKILL.md.template",
        "agents/openai.yaml.template",
    ):
        assert any(relative in value.replace("\\", "/") for value in add_data)
