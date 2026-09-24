from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

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


def test_local_verifier_checks_exact_assets_and_exe_identity(tmp_path: Path, monkeypatch) -> None:
    repo = tmp_path / "repo"
    dist = repo / "upilotserver~" / "dist"
    dist.mkdir(parents=True)
    (repo / "package.json").write_text('{"version": "1.2.3"}')
    (repo / "upilotserver~" / "pyproject.toml").write_text('[project]\nversion = "1.2.3"\n')
    exe = dist / "upilot-mcp-server-1.2.3-win-x64.exe"
    exe.write_bytes(b"local exe")
    monkeypatch.setattr(build_server_exe, "REPO_ROOT", repo)
    monkeypatch.setattr(build_server_exe, "SERVER_ROOT", repo / "upilotserver~")
    monkeypatch.setattr(build_server_exe, "DIST", dist)
    manifest = build_server_exe.write_manifest(exe, version="1.2.3", upm_version="1.2.3",
                                               channel="release", commit="abc", protocol_version="1", base_url="")
    calls = []

    def fake_run(command, **kwargs):
        calls.append((command, kwargs))
        return subprocess.CompletedProcess(command, 0, "upilot-mcp 1.2.3 channel=release commit=abc protocol=1\n", "")

    monkeypatch.setattr(build_server_exe.subprocess, "run", fake_run)
    monkeypatch.setenv("UPILOT_SERVER_VERSION", "pretend")
    monkeypatch.setenv("UPILOT_BUILD_COMMIT", "pretend")
    monkeypatch.setenv("UPILOT_BUILD_CHANNEL", "pretend")
    good = build_server_exe.verify_local_release("1.2.3", "1.2.3", "release", "abc")
    assert good["passed"] and good["exe"] == exe.name
    assert calls[0][0] == [str(exe), "--version"]
    assert calls[0][1]["timeout"] == 30
    assert all(key not in calls[0][1]["env"] for key in
               ("UPILOT_SERVER_VERSION", "UPILOT_BUILD_COMMIT", "UPILOT_BUILD_CHANNEL"))
    baseline = manifest.read_text(encoding="utf-8")
    for mutate, expected in (
        (lambda data: data["downloads"].append(data["downloads"][0].copy()), "exactly one"),
        (lambda data: data["downloads"][0].update(fileName="../wrong.exe"), "filename"),
        (lambda data: data["downloads"][0].update(sizeBytes=1), "size"),
        (lambda data: data["downloads"][0].update(sha256="0" * 64), "SHA256"),
        (lambda data: data.update(commitSha="wrong"), "commitSha"),
    ):
        original = json.loads(manifest.read_text(encoding="utf-8"))
        mutate(original)
        manifest.write_text(json.dumps(original), encoding="utf-8")
        with pytest.raises(ValueError, match=expected):
            build_server_exe.verify_local_release("1.2.3", "1.2.3", "release", "abc")
        manifest.write_text(baseline, encoding="utf-8")

    checksum = exe.with_suffix(".exe.sha256")
    checksum.write_text("0" * 64 + "  " + exe.name + "\n")
    with pytest.raises(ValueError, match="checksum"):
        build_server_exe.verify_local_release("1.2.3", "1.2.3", "release", "abc")
    build_server_exe.write_manifest(exe, version="1.2.3", upm_version="1.2.3",
                                    channel="release", commit="abc", protocol_version="1", base_url="")
    monkeypatch.setattr(build_server_exe.subprocess, "run", lambda *args, **kw: subprocess.CompletedProcess(args, 0, "wrong", ""))
    with pytest.raises(ValueError, match="identity mismatch"):
        build_server_exe.verify_local_release("1.2.3", "1.2.3", "release", "abc")


@pytest.mark.parametrize("failure,expected", [
    (subprocess.CalledProcessError(3, ["exe", "--version"]), "failed"),
    (subprocess.TimeoutExpired(["exe", "--version"], 30), "timed out"),
])
def test_local_verifier_rejects_exe_failure(tmp_path: Path, monkeypatch, failure, expected) -> None:
    repo = tmp_path / "repo"
    dist = repo / "upilotserver~" / "dist"
    dist.mkdir(parents=True)
    (repo / "package.json").write_text('{"version":"1.2.3"}', encoding="utf-8")
    (repo / "upilotserver~" / "pyproject.toml").write_text('[project]\nversion = "1.2.3"\n', encoding="utf-8")
    exe = dist / "upilot-mcp-server-1.2.3-win-x64.exe"
    exe.write_bytes(b"exe")
    monkeypatch.setattr(build_server_exe, "REPO_ROOT", repo)
    monkeypatch.setattr(build_server_exe, "SERVER_ROOT", repo / "upilotserver~")
    monkeypatch.setattr(build_server_exe, "DIST", dist)
    build_server_exe.write_manifest(exe, version="1.2.3", upm_version="1.2.3",
                                    channel="release", commit="abc", protocol_version="1", base_url="")

    def fail_version(*args, **kwargs):
        raise failure

    monkeypatch.setattr(build_server_exe.subprocess, "run", fail_version)
    with pytest.raises(ValueError, match=expected):
        build_server_exe.verify_local_release("1.2.3", "1.2.3", "release", "abc")


def test_verify_only_never_rebuilds_or_changes_release_assets(tmp_path: Path, monkeypatch, capsys) -> None:
    repo = tmp_path / "repo"
    dist = repo / "upilotserver~" / "dist"
    dist.mkdir(parents=True)
    (repo / "package.json").write_text('{"version":"1.2.3"}', encoding="utf-8")
    (repo / "upilotserver~" / "pyproject.toml").write_text('[project]\nversion = "1.2.3"\n', encoding="utf-8")
    exe = dist / "upilot-mcp-server-1.2.3-win-x64.exe"
    exe.write_bytes(b"exe")
    monkeypatch.setattr(build_server_exe, "REPO_ROOT", repo)
    monkeypatch.setattr(build_server_exe, "SERVER_ROOT", repo / "upilotserver~")
    monkeypatch.setattr(build_server_exe, "DIST", dist)
    build_server_exe.write_manifest(exe, version="1.2.3", upm_version="1.2.3",
                                    channel="release", commit="abc", protocol_version="1", base_url="")
    baseline = {path.name: path.read_bytes() for path in dist.iterdir()}
    monkeypatch.setattr(build_server_exe, "build_exe", lambda *args: pytest.fail("verify-only rebuilt EXE"))
    monkeypatch.setattr(build_server_exe, "write_manifest", lambda *args, **kwargs: pytest.fail("verify-only rewrote manifest"))
    monkeypatch.setattr(build_server_exe, "ensure_pyinstaller", lambda: pytest.fail("verify-only installed dependencies"))
    monkeypatch.setattr(build_server_exe.subprocess, "run", lambda cmd, **kwargs:
                        subprocess.CompletedProcess(cmd, 0, "upilot-mcp 1.2.3 channel=release commit=abc protocol=1\n", ""))
    monkeypatch.setattr(sys, "argv", ["build_server_exe.py", "--verify-only", "--version", "1.2.3",
                                  "--upm-version", "1.2.3", "--commit", "abc"])
    assert build_server_exe.main() == 0
    assert {path.name: path.read_bytes() for path in dist.iterdir()} == baseline
    assert json.loads(capsys.readouterr().out)["passed"] is True
    report = json.loads((repo / "artifacts/reliability-quality/local-release-verification.json").read_text(encoding="utf-8"))
    assert report["versionOutput"] == "upilot-mcp 1.2.3 channel=release commit=abc protocol=1"

    exe.write_bytes(b"tampered")
    assert build_server_exe.main() == 1
    assert exe.read_bytes() == b"tampered"
    assert json.loads((repo / "artifacts/reliability-quality/local-release-verification.json").read_text(encoding="utf-8"))["passed"] is False


def test_server_exe_build_bundles_complete_skill_before_entry_script(
    tmp_path: Path, monkeypatch
) -> None:
    repo = tmp_path / "repo"
    server = repo / "upilotserver~"
    skill = repo / "skills" / "upilot-unity-mcp"
    for relative in (
        "template-manifest.json",
        "AGENTS.md.template",
        "SKILL.md.template",
        "SKILL.md",
        "agents/openai.yaml.template",
        "agents/openai.yaml",
        "references/automation-steps.md",
        "references/installation.md",
        "references/future-guide.md",
        "scripts/install_upilot.py",
    ):
        path = skill / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(relative, encoding="utf-8")
    for relative in (
        "SKILL.md.meta", ".upilot-install.json",
        "scripts/__pycache__/cached.pyc", "scripts/cached.pyo",
    ):
        path = skill / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("not distributed", encoding="utf-8")
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
        "SKILL.md",
        "agents/openai.yaml.template",
        "agents/openai.yaml",
        "references/automation-steps.md",
        "references/installation.md",
        "references/future-guide.md",
        "scripts/install_upilot.py",
    ):
        parent = Path(relative).parent.as_posix()
        destination = "skills/upilot-unity-mcp" + ("/" + parent if parent != "." else "")
        assert f"{skill / relative}{os.pathsep}{destination}" in add_data
    assert not any(
        marker in value for value in add_data
        for marker in (".meta", ".upilot-install.json", "__pycache__", ".pyo")
    )


@pytest.mark.parametrize("missing", ["AGENTS.md.template", "SKILL.md", "references/automation-steps.md"])
def test_missing_skill_resource_fails_before_build_side_effects(tmp_path: Path, monkeypatch, missing) -> None:
    skill = tmp_path / "skills" / "upilot-unity-mcp"
    for source in build_server_exe.collect_skill_resources(REPO_ROOT / "skills" / "upilot-unity-mcp"):
        relative = source.relative_to(REPO_ROOT / "skills" / "upilot-unity-mcp")
        if relative.as_posix() == missing:
            continue
        target = skill / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
    monkeypatch.setattr(build_server_exe, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(build_server_exe, "ensure_pyinstaller",
                        lambda: pytest.fail("Missing resources must not start build setup"))
    with pytest.raises(FileNotFoundError, match=missing):
        build_server_exe.build_exe("1.2.3", "test", "abc")
