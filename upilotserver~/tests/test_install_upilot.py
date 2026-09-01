from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import sys

import pytest


REPO_ROOT = Path(__file__).resolve().parents[2]
INSTALLER_PATH = REPO_ROOT / "skills" / "upilot-unity-mcp" / "scripts" / "install_upilot.py"
SPEC = importlib.util.spec_from_file_location("install_upilot", INSTALLER_PATH)
assert SPEC and SPEC.loader
install_upilot = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(install_upilot)

RELEASE_PATH = REPO_ROOT / "upilotserver~" / "deploy" / "build_release.py"
RELEASE_SPEC = importlib.util.spec_from_file_location("build_release", RELEASE_PATH)
assert RELEASE_SPEC and RELEASE_SPEC.loader
build_release = importlib.util.module_from_spec(RELEASE_SPEC)
RELEASE_SPEC.loader.exec_module(build_release)


def _unity_project(tmp_path: Path) -> Path:
    project = tmp_path / "UnityProject"
    packages = project / "Packages"
    packages.mkdir(parents=True)
    packages.joinpath("manifest.json").write_text(
        json.dumps({"dependencies": {}}, indent=2) + "\n",
        encoding="utf-8",
    )
    return project


def _run(project: Path, *extra: str) -> int:
    return install_upilot.main([
        "--unity-project",
        str(project),
        "--upilot-dir",
        str(REPO_ROOT),
        "--install-skill",
        "none",
        *extra,
    ])


def _manifest_dependency(project: Path) -> str:
    manifest = json.loads((project / "Packages" / "manifest.json").read_text(encoding="utf-8"))
    return manifest["dependencies"][install_upilot.UPM_PACKAGE]


def test_remote_install_requires_explicit_upm_ref(tmp_path: Path) -> None:
    project = _unity_project(tmp_path)

    with pytest.raises(SystemExit, match="requires --upm-ref"):
        _run(project)


def test_remote_install_writes_explicit_git_ref(tmp_path: Path) -> None:
    project = _unity_project(tmp_path)

    assert _run(project, "--upm-ref", "v9.8.7") == 0

    assert _manifest_dependency(project) == f"{install_upilot.REPO_URL}#v9.8.7"


def test_local_upm_install_does_not_require_ref(tmp_path: Path) -> None:
    project = _unity_project(tmp_path)

    assert _run(project, "--use-local-upm") == 0

    assert _manifest_dependency(project) == "file:" + REPO_ROOT.as_posix()


def test_local_upm_preserves_equivalent_relative_reference_and_manifest_bytes(tmp_path: Path) -> None:
    project = tmp_path / "repo" / "Tests~" / "UPilotTest"
    manifest_path = project / "Packages" / "manifest.json"
    manifest_path.parent.mkdir(parents=True)
    original = (
        b"\xef\xbb\xbf{\r\n"
        b'  "dependencies": {\r\n'
        b'    "io.github.codingriver.upilot": "file:../../.."\r\n'
        b"  }\r\n"
        b"}\r\n"
    )
    manifest_path.write_bytes(original)
    upilot_dir = tmp_path / "repo"

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--use-local-upm",
        "--install-skill", "none",
    ]) == 0

    assert manifest_path.read_bytes() == original


def test_skill_only_installs_editor_compatible_metadata_without_touching_manifest(tmp_path: Path) -> None:
    upilot_dir = tmp_path / "upilot"
    source = upilot_dir / "skills" / install_upilot.SKILL_NAME
    source.mkdir(parents=True)
    source.joinpath("SKILL.md").write_text("# fixture\n", encoding="utf-8")
    source.joinpath("helper.py").write_text("print('fixture')\n", encoding="utf-8")
    setup = upilot_dir / "Editor" / "Core" / "UPilotAgentSetup.cs"
    setup.parent.mkdir(parents=True)
    setup.write_text("private const int SkillInstallTemplateVersion = 27;\n", encoding="utf-8")
    project = _unity_project(tmp_path)
    manifest_path = project / "Packages" / "manifest.json"
    original_manifest = manifest_path.read_bytes()

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
    ]) == 0

    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    metadata = json.loads(target.joinpath(".upilot-install.json").read_text(encoding="utf-8"))
    assert metadata["templateVersion"] == 27
    assert metadata["contentSha256"] == install_upilot._skill_content_hash(target)
    assert manifest_path.read_bytes() == original_manifest


def test_local_upm_and_remote_ref_are_mutually_exclusive(tmp_path: Path) -> None:
    project = _unity_project(tmp_path)

    with pytest.raises(SystemExit, match="mutually exclusive"):
        _run(project, "--use-local-upm", "--upm-ref", "main")


def test_codex_registration_is_http_only(tmp_path: Path) -> None:
    project = _unity_project(tmp_path)

    assert _run(
        project,
        "--use-local-upm",
        "--write-codex-mcp",
        "project",
        "--http-port",
        "8021",
        "--mcp-name",
        "upilot-test",
    ) == 0

    text = (project / ".codex" / "config.toml").read_text(encoding="utf-8")
    assert "[mcp_servers.upilot-test]" in text
    assert 'url = "http://127.0.0.1:8021/mcp"' in text
    for forbidden in ("command =", "args =", "stdio", "8765"):
        assert forbidden not in text


def test_legacy_bridge_port_option_is_rejected() -> None:
    with pytest.raises(SystemExit, match="WebSocket ports are internal"):
        install_upilot.main(["--port", "8765"])


def test_python_setup_is_opt_in() -> None:
    args = install_upilot.build_parser().parse_args([])

    assert args.setup_python is False


def test_release_client_configs_are_http_only() -> None:
    configs = build_release._make_configs(http_port=8031)
    serialized = json.dumps(configs)

    assert "http://127.0.0.1:8031/mcp" in serialized
    for forbidden in ('"command"', '"args"', '"stdio"', "8765"):
        assert forbidden not in serialized


def test_runtime_transport_defaults_and_falls_back_to_http(monkeypatch) -> None:
    from upilot_mcp import mcp_stdio_server

    monkeypatch.delenv("UPILOT_TRANSPORT", raising=False)
    monkeypatch.setattr(sys, "argv", ["upilot-mcp"])
    assert mcp_stdio_server._resolve_transport() == "http"

    monkeypatch.setenv("UPILOT_TRANSPORT", "invalid")
    assert mcp_stdio_server._resolve_transport() == "http"


@pytest.mark.parametrize("value", ["0", "65536", "not-a-port"])
def test_http_port_is_validated(value: str) -> None:
    with pytest.raises(SystemExit):
        install_upilot.build_parser().parse_args(["--http-port", value])
