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


def _template_upilot(tmp_path: Path, version: int) -> tuple[Path, Path]:
    upilot_dir = tmp_path / "upilot"
    upilot_dir.mkdir(parents=True)
    upilot_dir.joinpath("package.json").write_text(
        json.dumps({"name": install_upilot.UPM_PACKAGE, "version": "1.2.3"}),
        encoding="utf-8",
    )
    source = upilot_dir / "skills" / install_upilot.SKILL_NAME
    source.joinpath("agents").mkdir(parents=True)
    source.joinpath("AGENTS.md.template").write_text(
        "rulesVersion: {{rulesVersion}}\nendpoint: {{mcpUrl}}\n",
        encoding="utf-8",
    )
    source.joinpath("SKILL.md.template").write_text(
        "---\nname: upilot-unity-mcp\ndescription: fixture\n---\nendpoint={{mcpUrl}}\nhealth={{healthUrl}}\n",
        encoding="utf-8",
    )
    source.joinpath("agents/openai.yaml.template").write_text(
        'dependencies:\n  tools:\n    - url: "{{mcpUrl}}"\n',
        encoding="utf-8",
    )
    source.joinpath("template-manifest.json").write_text(
        json.dumps({
            "schemaVersion": 1,
            "agentRulesVersion": 31,
            "skillPackVersion": version,
            "defaultHttpPort": 8011,
            "templates": {
                "agentRules": "AGENTS.md.template",
                "skill": "SKILL.md.template",
                "openai": "agents/openai.yaml.template",
            },
        }),
        encoding="utf-8",
    )
    source.joinpath("helper.py").write_text("print('fixture')\n", encoding="utf-8")
    return upilot_dir, source


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
    import shutil
    fixture, _ = _template_upilot(tmp_path, 31)
    shutil.copytree(fixture, upilot_dir, dirs_exist_ok=True)

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--use-local-upm",
        "--install-skill", "none",
    ]) == 0

    assert manifest_path.read_bytes() == original


def test_skill_only_installs_editor_compatible_metadata_without_touching_manifest(tmp_path: Path) -> None:
    upilot_dir, source = _template_upilot(tmp_path, 27)
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
    assert metadata["schemaVersion"] == 2
    assert metadata["templateVersion"] == 27
    assert metadata["templateSha256"] == install_upilot._skill_renderer().template_sha256(source)
    assert metadata["contentSha256"] == install_upilot._skill_content_hash(target)
    assert metadata["renderContext"]["projectPath"] == str(project.resolve())
    claude_target = project / ".claude" / "skills" / install_upilot.SKILL_NAME
    claude_metadata = json.loads(
        claude_target.joinpath(".upilot-install.json").read_text(encoding="utf-8")
    )
    assert claude_metadata["templateVersion"] == 27
    assert claude_metadata["contentSha256"] == install_upilot._skill_content_hash(claude_target)
    assert not (project / ".opencode" / "skills" / install_upilot.SKILL_NAME).exists()
    assert manifest_path.read_bytes() == original_manifest


def test_skill_templates_render_custom_port_in_instructions_and_openai_metadata(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "codex",
        "--http-port", "8021",
    ]) == 0

    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    assert "http://127.0.0.1:8021/mcp" in target.joinpath("SKILL.md").read_text(encoding="utf-8")
    assert "http://127.0.0.1:8021/health" in target.joinpath("SKILL.md").read_text(encoding="utf-8")
    assert "http://127.0.0.1:8021/mcp" in target.joinpath("agents/openai.yaml").read_text(encoding="utf-8")


def test_clean_v1_skill_install_upgrades_metadata_without_requiring_force(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    args = [
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "codex",
    ]
    assert install_upilot.main(args) == 0
    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    content_hash = install_upilot._skill_content_hash(target)
    target.joinpath(".upilot-install.json").write_text(
        json.dumps({"templateVersion": 31, "contentSha256": content_hash}) + "\n",
        encoding="utf-8",
    )
    content_before = {
        path.relative_to(target).as_posix(): path.read_bytes()
        for path in target.rglob("*")
        if path.is_file() and path.name != ".upilot-install.json"
    }

    assert install_upilot.main(args) == 0

    metadata = json.loads(target.joinpath(".upilot-install.json").read_text(encoding="utf-8"))
    assert metadata["schemaVersion"] == 2
    assert metadata["contentSha256"] == content_hash
    assert content_before == {
        path.relative_to(target).as_posix(): path.read_bytes()
        for path in target.rglob("*")
        if path.is_file() and path.name != ".upilot-install.json"
    }
    assert not (project / ".upilot" / "backups" / "agent-integrations").exists()


@pytest.mark.parametrize("schema_version", [1, 2])
def test_customized_skill_install_is_backed_up_and_replaced_without_force(
    tmp_path: Path, schema_version: int
) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    args = [
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "codex",
    ]
    assert install_upilot.main(args) == 0
    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    if schema_version == 1:
        metadata = json.loads(target.joinpath(".upilot-install.json").read_text(encoding="utf-8"))
        target.joinpath(".upilot-install.json").write_text(
            json.dumps({
                "templateVersion": metadata["templateVersion"],
                "contentSha256": metadata["contentSha256"],
            }) + "\n",
            encoding="utf-8",
        )
    with target.joinpath("SKILL.md").open("a", encoding="utf-8") as stream:
        stream.write("local customization\n")
    before = {
        path.relative_to(target).as_posix(): path.read_bytes()
        for path in target.rglob("*") if path.is_file()
    }
    original_hash, file_count, total_bytes = install_upilot._exact_target_hash(target)

    assert install_upilot.main(args) == 0

    assert "local customization" not in target.joinpath("SKILL.md").read_text(encoding="utf-8")
    sessions = list((project / ".upilot" / "backups" / "agent-integrations").iterdir())
    assert len(sessions) == 1
    backup_target = sessions[0] / ".agents" / "skills" / install_upilot.SKILL_NAME
    assert before == {
        path.relative_to(backup_target).as_posix(): path.read_bytes()
        for path in backup_target.rglob("*") if path.is_file()
    }
    backup_manifest = json.loads(sessions[0].joinpath("manifest.json").read_text(encoding="utf-8"))
    assert "unmanaged_or_modified" in backup_manifest["reason"]
    assert backup_manifest["originalContentSha256"] == original_hash
    assert backup_manifest["fileCount"] == file_count
    assert backup_manifest["totalBytes"] == total_bytes
    assert backup_manifest["agentRulesVersion"] == 31
    assert backup_manifest["skillPackVersion"] == 31

    assert install_upilot.main([*args, "--force"]) == 0


def test_clean_skill_updates_when_render_context_changes(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    common = [
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "codex",
    ]
    assert install_upilot.main(common) == 0

    assert install_upilot.main([*common, "--http-port", "8021"]) == 0

    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    assert "http://127.0.0.1:8021/mcp" in target.joinpath("SKILL.md").read_text(encoding="utf-8")
    assert "http://127.0.0.1:8021/mcp" in target.joinpath("agents/openai.yaml").read_text(encoding="utf-8")
    metadata = json.loads(target.joinpath(".upilot-install.json").read_text(encoding="utf-8"))
    assert metadata["renderContext"]["mcpUrl"] == "http://127.0.0.1:8021/mcp"


def test_incomplete_v2_metadata_is_not_treated_as_clean_managed_install(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    args = [
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "codex",
    ]
    assert install_upilot.main(args) == 0
    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    metadata_path = target / ".upilot-install.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    metadata["renderContext"] = {}
    metadata_path.write_text(json.dumps(metadata) + "\n", encoding="utf-8")
    before = metadata_path.read_bytes()

    assert install_upilot.main(args) == 0

    assert metadata_path.read_bytes() != before
    sessions = list((project / ".upilot" / "backups" / "agent-integrations").iterdir())
    assert len(sessions) == 1
    backup_metadata = sessions[0] / ".agents" / "skills" / install_upilot.SKILL_NAME / ".upilot-install.json"
    assert backup_metadata.read_bytes() == before


def test_unmanaged_skill_is_backed_up_and_authoritatively_replaced(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    target.mkdir(parents=True)
    target.joinpath("custom.txt").write_text("unmanaged bytes", encoding="utf-8")

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
    ]) == 0

    assert not target.joinpath("custom.txt").exists()
    sessions = list((project / ".upilot" / "backups" / "agent-integrations").iterdir())
    assert len(sessions) == 1
    assert (
        sessions[0] / ".agents" / "skills" / install_upilot.SKILL_NAME / "custom.txt"
    ).read_text(encoding="utf-8") == "unmanaged bytes"


def test_backup_failure_preserves_failed_target_and_continues_other_target(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 31)
    project = _unity_project(tmp_path)
    agents_target = project / ".agents" / "skills" / install_upilot.SKILL_NAME
    claude_target = project / ".claude" / "skills" / install_upilot.SKILL_NAME
    for target, marker in ((agents_target, "agents-original"), (claude_target, "claude-original")):
        target.mkdir(parents=True)
        target.joinpath("custom.txt").write_text(marker, encoding="utf-8")

    real_backup = install_upilot._backup_skill_target

    def fail_agents_backup(target: Path, *args, **kwargs):
        if target == agents_target:
            raise OSError("simulated backup failure")
        return real_backup(target, *args, **kwargs)

    monkeypatch.setattr(install_upilot, "_backup_skill_target", fail_agents_backup)

    with pytest.raises(SystemExit, match="simulated backup failure"):
        install_upilot.main([
            "--unity-project", str(project),
            "--upilot-dir", str(upilot_dir),
            "--skill-only",
            "--install-skill", "repo",
        ])

    assert agents_target.joinpath("custom.txt").read_text(encoding="utf-8") == "agents-original"
    assert not claude_target.joinpath("custom.txt").exists()
    assert claude_target.joinpath("SKILL.md").is_file()


def test_cursor_skill_reuses_agents_directory_without_cursor_copy(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 28)
    project = _unity_project(tmp_path)

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "cursor",
    ]) == 0

    assert (project / ".agents" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()
    assert not (project / ".cursor" / "skills" / install_upilot.SKILL_NAME).exists()
    assert (project / ".claude" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()


def test_claude_skill_uses_native_project_directory(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 29)
    project = _unity_project(tmp_path)

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "claude",
    ]) == 0

    assert (project / ".claude" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()
    assert (project / ".agents" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()


def test_opencode_skill_reuses_agents_directory_without_opencode_copy(tmp_path: Path) -> None:
    upilot_dir, _ = _template_upilot(tmp_path, 30)
    project = _unity_project(tmp_path)

    assert install_upilot.main([
        "--unity-project", str(project),
        "--upilot-dir", str(upilot_dir),
        "--skill-only",
        "--install-skill", "repo",
        "--skill-client", "opencode",
    ]) == 0

    assert (project / ".agents" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()
    assert not (project / ".opencode" / "skills" / install_upilot.SKILL_NAME).exists()
    assert (project / ".claude" / "skills" / install_upilot.SKILL_NAME / "SKILL.md").is_file()


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
