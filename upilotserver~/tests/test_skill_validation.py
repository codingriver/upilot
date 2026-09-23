import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import pytest


SOURCE = Path(__file__).resolve().parents[2] / "skills/upilot-unity-mcp"


def module(name):
    spec = importlib.util.spec_from_file_location(name, SOURCE / "scripts" / (name + ".py"))
    target = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(target)
    return target


def mark(target, version=29):
    metadata = {"templateVersion": version, "contentSha256": module("install_upilot")._skill_content_hash(target)}
    (target / ".upilot-install.json").write_text(json.dumps(metadata), encoding="utf-8")
    return metadata


@pytest.fixture(params=[".agents", ".claude"])
def installed(tmp_path, request):
    target = tmp_path / request.param / "skills/upilot-unity-mcp"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    mark(target)
    return target


def contents(root):
    return {path.relative_to(root).as_posix(): path.read_bytes() for path in root.rglob("*") if path.is_file()}


@pytest.mark.parametrize("mode", ["auto", "installed"])
def test_clean_installed_copy_is_verified_without_repository_or_meta(installed, mode):
    before = contents(installed)
    assert module("check_skill_pack").main(["--mode", mode, "--root", str(installed)]) == 0
    assert contents(installed) == before
    assert not any(path.endswith(".meta") for path in before)


def test_installed_checker_runs_from_its_own_directory(installed):
    result = subprocess.run(
        [sys.executable, str(installed / "scripts/check_skill_pack.py")],
        cwd=installed, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
        capture_output=True, text=True, timeout=30,
    )
    assert result.returncode == 0, result.stderr
    assert "mode=installed" in result.stdout


@pytest.mark.parametrize("case", ["missing_marker", "invalid_json", "invalid_version", "invalid_hash", "changed", "missing_file", "extra_file"])
def test_installed_failure_is_read_only_and_never_falls_back_to_source(installed, case):
    metadata = installed / ".upilot-install.json"
    if case == "missing_marker":
        metadata.unlink()
    elif case == "invalid_json":
        metadata.write_text("{", encoding="utf-8")
    elif case == "invalid_version":
        value = json.loads(metadata.read_text())
        value["templateVersion"] = True
        metadata.write_text(json.dumps(value), encoding="utf-8")
    elif case == "invalid_hash":
        value = json.loads(metadata.read_text())
        value["contentSha256"] = "not-a-hash"
        metadata.write_text(json.dumps(value), encoding="utf-8")
    elif case == "changed":
        with (installed / "SKILL.md").open("a", encoding="utf-8") as stream:
            stream.write("\nuser customization\n")
    elif case == "missing_file":
        (installed / "references/safety.md").unlink()
    else:
        (installed / "extra.txt").write_text("changed install", encoding="utf-8")
    before = contents(installed)
    with pytest.raises(SystemExit) as error:
        module("check_skill_pack").main(["--mode", "installed", "--root", str(installed)])
    assert error.value.code == 1
    assert contents(installed) == before


def test_step_authoring_guide_is_required_even_with_matching_install_hash(installed, capsys):
    (installed / "references/automation-steps.md").unlink()
    mark(installed)
    before = contents(installed)
    with pytest.raises(SystemExit):
        module("check_skill_pack").main(["--mode", "installed", "--root", str(installed)])
    assert "missing required file: references/automation-steps.md" in capsys.readouterr().err
    assert contents(installed) == before


def test_cache_meta_and_project_specific_ports_are_supported(installed):
    for name in ("SKILL.md", "agents/openai.yaml"):
        path = installed / name
        path.write_text(path.read_text(encoding="utf-8").replace(":8011/", ":9123/"), encoding="utf-8")
    metadata = mark(installed, version=28)
    for name in ("SKILL.md.meta", "__pycache__/extra.json", "scripts/__PYCACHE__/cached.bin",
                 "scripts/generated.PYC", "scripts/generated.pyo", "ignored.meta/content.txt"):
        path = installed / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("ignored", encoding="utf-8")
    assert module("install_upilot")._skill_content_hash(installed) == metadata["contentSha256"]
    before = contents(installed)
    assert module("check_skill_pack").main(["--root", str(installed)]) == 0
    assert contents(installed) == before


def test_missing_marker_in_auto_mode_reports_unknown_layout(installed, capsys):
    (installed / ".upilot-install.json").unlink()
    with pytest.raises(SystemExit):
        module("check_skill_pack").main(["--root", str(installed)])
    assert "cannot identify Skill layout" in capsys.readouterr().err


def test_forced_source_mode_does_not_accept_an_installation(installed, capsys):
    with pytest.raises(SystemExit):
        module("check_skill_pack").main(["--mode", "source", "--root", str(installed)])
    assert "source mode requires" in capsys.readouterr().err


def test_repository_source_retains_strict_checks():
    assert module("check_skill_pack").main(["--mode", "source", "--root", str(SOURCE)]) == 0


def test_repository_checker_accepts_manifest_version_upgrade(tmp_path, monkeypatch):
    target = tmp_path / "skills" / "upilot-unity-mcp"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.pyo"))
    manifest_path = target / "template-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["agentRulesVersion"] += 1
    manifest["skillPackVersion"] += 1
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    renderer = module("render_skill_pack")
    assert renderer.main(["--root", str(target), "--write"]) == 0
    documentation_path, documentation_content = renderer.render_agent_documentation(target)
    documentation_path.parent.mkdir(parents=True)
    documentation_path.write_text(documentation_content, encoding="utf-8", newline="\n")
    checker = module("check_skill_pack")
    monkeypatch.setattr(checker, "ROOT", target)

    assert checker.check_generated_source_artifacts() == manifest
    checker.check_repository_consistency()


def test_renderer_rejects_missing_template(tmp_path):
    target = tmp_path / "skill"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    (target / "SKILL.md.template").unlink()

    assert module("render_skill_pack").main(["--root", str(target), "--check"]) == 1


def test_renderer_rejects_unknown_or_unresolved_tokens(tmp_path):
    target = tmp_path / "skill"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    template = target / "SKILL.md.template"
    template.write_text(template.read_text(encoding="utf-8") + "\n{{unknownToken}}\n", encoding="utf-8")

    assert module("render_skill_pack").main(["--root", str(target), "--check"]) == 1


def test_renderer_rejects_malformed_residual_placeholder(tmp_path):
    target = tmp_path / "skill"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    template = target / "SKILL.md.template"
    template.write_text(template.read_text(encoding="utf-8") + "\n{{not-a-valid-token}}\n", encoding="utf-8")

    assert module("render_skill_pack").main(["--root", str(target), "--check"]) == 1


def test_renderer_rejects_duplicate_template_paths(tmp_path):
    target = tmp_path / "skill"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    manifest_path = target / "template-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["templates"]["openai"] = manifest["templates"]["skill"]
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    assert module("render_skill_pack").main(["--root", str(target), "--check"]) == 1


def test_renderer_detects_generated_artifact_drift_without_writing(tmp_path):
    target = tmp_path / "skill"
    shutil.copytree(SOURCE, target, ignore=shutil.ignore_patterns("*.meta", "__pycache__", "*.pyc", "*.pyo"))
    skill = target / "SKILL.md"
    skill.write_text(skill.read_text(encoding="utf-8") + "\ndrift\n", encoding="utf-8")
    before = contents(target)

    assert module("render_skill_pack").main(["--root", str(target), "--check"]) == 1
    assert contents(target) == before


def test_renderer_custom_port_updates_both_generated_artifacts():
    renderer = module("render_skill_pack")
    manifest = renderer.load_manifest(SOURCE)
    context = renderer.build_context(manifest, http_port=8021, package="1.2.3")
    outputs = renderer.render_skill_outputs(SOURCE, context, manifest)

    assert "http://127.0.0.1:8021/mcp" in outputs[SOURCE / "SKILL.md"]
    assert "http://127.0.0.1:8021/health" in outputs[SOURCE / "SKILL.md"]
    assert "http://127.0.0.1:8021/mcp" in outputs[SOURCE / "agents/openai.yaml"]
