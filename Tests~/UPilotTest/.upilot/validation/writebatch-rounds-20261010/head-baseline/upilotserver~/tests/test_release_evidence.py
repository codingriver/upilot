import copy
import importlib.util
import json
from pathlib import Path
import subprocess
from datetime import datetime, timezone

import pytest
from upilot_mcp import release_evidence
from upilot_mcp.release_evidence import (REQUIRED_FIXTURES, GENERATED_FILES, tag_acceptance_run,
                                         verify_required_summary, verify_release_delta, verify_workflow_run)


def report():
    return dict(acceptancePassed=True, cleanupVerified=True, testIdentityVerified=True, sourceUnchanged=True,
                requireTests=True, sourceIdentity={"sourceSha256": "x"}, sourceIdentityAfter={"sourceSha256": "x"},
                runGuid="run", endedAt=1, fixtures=list(REQUIRED_FIXTURES),
                steps={"compile": {"ok": True, "data": {"errorsVerified": True, "status": "success", "errorTotal": 0}},
                       "testStatus": {"ok": True, "data": {"runGuid": "run", "status": "completed",
                                      "resultAuthoritative": True, "total": 3, "failed": 0, "skipped": 0,
                                      "cleanupSucceeded": True, "cleanupPending": False, "unresolvedResources": [],
                                      "selectors": [{"kind": "fixture", "selector": name, "matchedCount": 1}
                                                    for name in REQUIRED_FIXTURES]}}})


def test_required_summary_rejects_weak_evidence():
    valid = report()
    verify_required_summary(valid, {"sourceSha256": "x"})
    for key, value in (("fixtures", []), ("sourceIdentityAfter", {}), ("acceptancePassed", False), ("requireTests", False)):
        with pytest.raises(ValueError):
            verify_required_summary({**valid, key: value}, {"sourceSha256": "x"})
    for key, value in (("total", 0), ("failed", 1), ("skipped", 1), ("runGuid", "other")):
        changed = copy.deepcopy(valid)
        changed["steps"]["testStatus"]["data"][key] = value
        with pytest.raises(ValueError):
            verify_required_summary(changed, {"sourceSha256": "x"})
    for key, value in (("selectors", []), ("cleanupSucceeded", False), ("cleanupPending", True),
                       ("unresolvedResources", ["capture"])):
        changed = copy.deepcopy(valid)
        changed["steps"]["testStatus"]["data"][key] = value
        with pytest.raises(ValueError):
            verify_required_summary(changed, {"sourceSha256": "x"})


@pytest.mark.parametrize("invalid_change", ("field", "leading-space", "trailing-newline", "formatting", "mode"))
def test_release_delta_allows_only_version_fields(tmp_path, invalid_change, monkeypatch):
    # This focused fixture has no template tree; the real C→R renderer is tested below.
    monkeypatch.setattr(release_evidence, "GENERATED_FILES", set())
    monkeypatch.setattr(release_evidence, "_expected_generated", lambda *args: {})
    def git(*args):
        return subprocess.check_output(["git", *args], cwd=tmp_path, text=True).strip()
    git("init")
    git("config", "user.email", "tests@example.invalid")
    git("config", "user.name", "Tests")
    package = tmp_path / "package.json"
    package.write_text(json.dumps({"name": "probe", "version": "1.0.0"}))
    git("add", ".")
    git("commit", "-m", "checked")
    checked = git("rev-parse", "HEAD")
    package.write_text(json.dumps({"name": "probe", "version": "1.0.1"}))
    git("add", ".")
    git("commit", "-m", "version")
    verify_release_delta(tmp_path, checked, git("rev-parse", "HEAD"))
    valid_text = json.dumps({"name": "probe", "version": "1.0.1"})
    invalid_text = {
        "field": json.dumps({"name": "changed", "version": "1.0.1"}),
        "leading-space": " " + valid_text,
        "trailing-newline": valid_text + "\n",
        "formatting": json.dumps({"name": "probe", "version": "1.0.1"}, indent=2),
        "mode": valid_text,
    }[invalid_change]
    package.write_text(invalid_text)
    git("add", ".")
    if invalid_change == "mode":
        git("update-index", "--chmod=+x", "package.json")
    git("commit", "--amend", "--no-edit")
    with pytest.raises(ValueError):
        verify_release_delta(tmp_path, checked, git("rev-parse", "HEAD"))


def test_foreign_or_wrong_workflow_cannot_supply_evidence():
    with pytest.raises(ValueError):
        verify_workflow_run({"conclusion": "success", "head_sha": "x"}, "owner/repo", "x")


def test_trusted_workflow_rejects_stale_failed_and_foreign_runs():
    run = dict(repository={"full_name": "owner/repo"}, head_repository={"full_name": "owner/repo"},
               path=".github/workflows/unity-release-evidence.yml", head_branch="main", head_sha="a" * 40,
               event="workflow_dispatch", conclusion="success", status="completed",
               updated_at=datetime.now(timezone.utc).isoformat())
    verify_workflow_run(run, "owner/repo", "a" * 40)
    for field, value in (("updated_at", "2020-01-01T00:00:00Z"), ("conclusion", "failure"),
                         ("head_repository", {"full_name": "attacker/fork"}), ("head_sha", "b" * 40),
                         ("path", ".github/workflows/quality-check.yml"), ("event", "pull_request")):
        with pytest.raises(ValueError):
            verify_workflow_run({**run, field: value}, "owner/repo", "a" * 40)


def test_optional_tag_marker_is_unambiguous(tmp_path):
    def git(*args):
        return subprocess.check_output(["git", *args], cwd=tmp_path, text=True).strip()
    git("init")
    git("config", "user.email", "tests@example.invalid")
    git("config", "user.name", "Tests")
    (tmp_path / "package.json").write_text('{}')
    git("add", ".")
    git("commit", "-m", "source")
    git("tag", "-a", "v1.2.3", "-m", "Release v1.2.3")
    assert tag_acceptance_run(tmp_path, "v1.2.3") == ""
    git("tag", "-a", "v1.2.4", "-m", "UPilot-Acceptance-Run: 123")
    assert tag_acceptance_run(tmp_path, "v1.2.4") == "123"
    git("tag", "-a", "v1.2.5", "-m", "UPilot-Acceptance-Run: 123\nUPilot-Acceptance-Run: 123")
    with pytest.raises(ValueError, match="ambiguous"):
        tag_acceptance_run(tmp_path, "v1.2.5")
    git("tag", "-a", "v1.2.6", "-m", "UPilot-Acceptance-Run: invalid")
    with pytest.raises(ValueError, match="malformed"):
        tag_acceptance_run(tmp_path, "v1.2.6")


@pytest.mark.parametrize("render_version_artifacts", [False, True])
def test_release_delta_rebuilds_version_bound_artifacts_and_rejects_tampering(tmp_path, render_version_artifacts):
    origin = Path(__file__).resolve().parents[2]
    root = tmp_path / "repo"
    skill = root / "skills" / "upilot-unity-mcp"
    manifest = json.loads((origin / "skills/upilot-unity-mcp/template-manifest.json").read_text(encoding="utf-8"))
    inputs = ["package.json", "upilotserver~/pyproject.toml",
              "skills/upilot-unity-mcp/template-manifest.json",
              "skills/upilot-unity-mcp/scripts/render_skill_pack.py"]
    inputs.extend("skills/upilot-unity-mcp/" + path for path in manifest["templates"].values())
    for name in inputs:
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((origin / name).read_bytes())
    package = root / "package.json"
    original = json.loads(package.read_text(encoding="utf-8"))["version"]
    target_version = "9.8.7"
    script = skill / "scripts" / "render_skill_pack.py"
    spec = importlib.util.spec_from_file_location("temp_renderer", script)
    renderer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(renderer)

    def render():
        source_manifest, context = renderer.source_context(skill)
        outputs = renderer.render_skill_outputs(skill, context, source_manifest)
        doc, content = renderer.render_agent_documentation(skill, source_manifest)
        outputs[doc] = content
        for path, text in outputs.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(text.encode("utf-8"))

    def git(*args):
        return subprocess.check_output(["git", *args], cwd=root, text=True).strip()

    render()
    git("init")
    git("config", "user.email", "tests@example.invalid")
    git("config", "user.name", "Tests")
    git("add", ".")
    git("commit", "-m", "checked")
    checked = git("rev-parse", "HEAD")
    package.write_bytes(package.read_bytes().replace(f'"version": "{original}"'.encode(),
                                                     f'"version": "{target_version}"'.encode(), 1))
    pyproject = root / "upilotserver~/pyproject.toml"
    pyproject.write_bytes(pyproject.read_bytes().replace(f'version = "{original}"'.encode(),
                                                         f'version = "{target_version}"'.encode(), 1))
    if render_version_artifacts:
        render()
    git("add", ".")
    git("commit", "-m", "release")
    release = git("rev-parse", "HEAD")
    if not render_version_artifacts:
        with pytest.raises(ValueError, match="stale or modified"):
            verify_release_delta(root, checked, release)
        return
    verify_release_delta(root, checked, release)
    generated = root / next(iter(GENERATED_FILES))
    generated.write_bytes(generated.read_bytes() + b"tampered\n")
    git("add", ".")
    git("commit", "--amend", "--no-edit")
    with pytest.raises(ValueError, match="stale or modified"):
        verify_release_delta(root, checked, git("rev-parse", "HEAD"))
    git("commit", "--allow-empty", "-m", "another child")
    with pytest.raises(ValueError, match="single version-only child"):
        verify_release_delta(root, checked, git("rev-parse", "HEAD"))
