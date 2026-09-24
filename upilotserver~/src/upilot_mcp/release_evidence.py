"""Fail-closed release evidence verification. No commits, tags, or releases are written."""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import tomllib
from datetime import datetime, timezone, timedelta

from .source_identity import source_identity

EVIDENCE_WORKFLOW = ".github/workflows/unity-release-evidence.yml"
EVIDENCE_ARTIFACT = "upilot-unity-release-evidence"
REQUIRED_FIXTURES = (
    "CodingRiver.UPilot.Tests.UPilotAssetMutationContractTests",
    "CodingRiver.UPilot.Tests.UPilotScreenshotRenderTests",
    "CodingRiver.UPilot.Tests.UPilotWindowInputContractTests",
)
VERSION_FILES = {"package.json", "upilotserver~/pyproject.toml"}
GENERATED_FILES = {
    "skills/upilot-unity-mcp/SKILL.md",
    "skills/upilot-unity-mcp/agents/openai.yaml",
    "Documentation~/AgentRules/AGENTS.upilot.md",
}


def tag_acceptance_run(root: Path, release_tag: str) -> str:
    """Return an optional tag marker; reject ambiguous or malformed claims."""
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", release_tag):
        raise ValueError("Expected vMAJOR.MINOR.PATCH tag.")
    annotation = git(root, "for-each-ref", "--format=%(contents)", "refs/tags/" + release_tag)
    claims = [line for line in annotation.splitlines() if "UPilot-Acceptance-Run" in line]
    if not claims:
        return ""
    if len(claims) != 1 or not re.fullmatch(r"UPilot-Acceptance-Run: ([0-9]+)", claims[0]):
        raise ValueError("Tag has an ambiguous or malformed acceptance run reference.")
    return claims[0].split(": ", 1)[1]


def _blob(root: Path, commit: str, name: str) -> bytes:
    return subprocess.check_output(["git", "show", f"{commit}:{name}"], cwd=root)


def _expected_generated(root: Path, checked_commit: str, version: str) -> dict[str, bytes]:
    """Render from checked-source templates in an isolated temporary repository."""
    with tempfile.TemporaryDirectory(prefix="upilot-release-render-") as directory:
        scratch = Path(directory)
        skill = scratch / "skills" / "upilot-unity-mcp"
        manifest_name = "skills/upilot-unity-mcp/template-manifest.json"
        manifest = json.loads(_blob(root, checked_commit, manifest_name))
        sources = {"package.json", manifest_name, "skills/upilot-unity-mcp/scripts/render_skill_pack.py"}
        for relative in manifest["templates"].values():
            if not isinstance(relative, str) or Path(relative).is_absolute() or ".." in Path(relative).parts:
                raise ValueError("Unsafe release template path.")
            sources.add("skills/upilot-unity-mcp/" + relative)
        for name in sources:
            destination = scratch / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(_blob(root, checked_commit, name))
        package = json.loads((scratch / "package.json").read_text(encoding="utf-8"))
        package["version"] = version
        (scratch / "package.json").write_text(json.dumps(package), encoding="utf-8")
        script = skill / "scripts" / "render_skill_pack.py"
        spec = importlib.util.spec_from_file_location("upilot_checked_renderer", script)
        if spec is None or spec.loader is None:
            raise ValueError("Checked-source renderer could not be loaded.")
        renderer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(renderer)
        source_manifest, context = renderer.source_context(skill)
        outputs = renderer.render_skill_outputs(skill, context, source_manifest)
        documentation, text = renderer.render_agent_documentation(skill, source_manifest)
        outputs[documentation] = text
        return {path.relative_to(scratch).as_posix(): text.encode("utf-8") for path, text in outputs.items()}


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(["git", *args], cwd=root, text=True, encoding="utf-8").strip()


def verify_release_delta(root: Path, checked_commit: str, current_commit: str) -> None:
    if not re.fullmatch(r"[0-9a-f]{40}", checked_commit):
        raise ValueError("Acceptance source commit must be a full SHA.")
    if current_commit == checked_commit:
        return
    if git(root, "show", "-s", "--format=%P", current_commit) != checked_commit:
        raise ValueError("Release must be the checked commit or its single version-only child.")
    allowed = VERSION_FILES | GENERATED_FILES
    changed = set(git(root, "diff", "--name-only", checked_commit, current_commit).splitlines())
    if not changed or not changed <= allowed:
        raise ValueError("Release changes include non-version files or unverified generated files.")
    for name in changed | (GENERATED_FILES if changed & GENERATED_FILES else set()):
        before_tree = git(root, "ls-tree", checked_commit, "--", name).split()
        after_tree = git(root, "ls-tree", current_commit, "--", name).split()
        before_mode = before_tree[0] if before_tree else None
        after_mode = after_tree[0] if after_tree else None
        if before_mode != after_mode or after_mode != "100644":
            raise ValueError("Release changed a version or generated file's mode or type.")
        if name in GENERATED_FILES:
            continue
        # Command/identity output may be stripped; file blobs must preserve every byte.
        old = subprocess.check_output(["git", "show", checked_commit + ":" + name], cwd=root).decode("utf-8")
        new = subprocess.check_output(["git", "show", current_commit + ":" + name], cwd=root).decode("utf-8")
        if name.endswith(".json"):
            before, after = json.loads(old), json.loads(new)
            after["version"] = before["version"]
        else:
            before, after = tomllib.loads(old), tomllib.loads(new)
            after["project"]["version"] = before["project"]["version"]
        if before != after:
            raise ValueError("Release changed fields other than the package/server version.")
        # Do not silently permit comments, key ordering or formatting changes in the version-only child.
        if name.endswith(".json"):
            pattern = r'("version"\s*:\s*")[^"]*(")'
        else:
            pattern = r'''(?m)^(version\s*=\s*["'])[^"']*(["'])'''
        strip_version = lambda text: re.sub(pattern, r"\1<VERSION>\2", text)
        if strip_version(old) != strip_version(new):
            raise ValueError("Release changed non-version bytes in a version file.")
    if changed & (VERSION_FILES | GENERATED_FILES):
        package = json.loads(_blob(root, current_commit, "package.json"))
        target = package.get("version")
        if not isinstance(target, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", target):
            raise ValueError("Release version is missing or invalid.")
        old_package = json.loads(_blob(root, checked_commit, "package.json"))
        for commit, version in ((checked_commit, old_package.get("version")), (current_commit, target)):
            expected = _expected_generated(root, checked_commit, version)
            for name in GENERATED_FILES:
                if _blob(root, commit, name) != expected[name]:
                    raise ValueError("Release generated template artifact is stale or modified: " + name)


def verify_required_summary(report: dict, source: dict) -> None:
    for key in ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged", "requireTests"):
        if report.get(key) is not True:
            raise ValueError("Missing successful Unity evidence: " + key)
    if report.get("sourceIdentity") != source or report.get("sourceIdentityAfter") != source:
        raise ValueError("Unity evidence source identity mismatch.")
    if not report.get("runGuid") or not report.get("endedAt"):
        raise ValueError("Missing Unity run identity or completion time.")
    if not set(REQUIRED_FIXTURES) <= set(report.get("fixtures") or []):
        raise ValueError("Required Unity fixture scope is incomplete.")
    steps = report.get("steps") or {}
    compile_result = steps.get("compile") or {}
    compiled = compile_result.get("data") or {}
    if (compile_result.get("ok") is not True or compiled.get("errorsVerified") is not True
            or compiled.get("errorTotal") != 0 or compiled.get("status") not in ("success", "completed")):
        raise ValueError("Unity compile evidence is unverified.")
    tested_result = steps.get("testStatus") or {}
    tested = tested_result.get("data") or {}
    if (tested_result.get("ok") is not True or tested.get("status") != "completed"
            or tested.get("runGuid") != report["runGuid"] or tested.get("resultAuthoritative") is not True
            or tested.get("failed") != 0 or tested.get("skipped") != 0 or int(tested.get("total") or 0) <= 0):
        raise ValueError("Unity tests are incomplete, skipped, or unsuccessful.")
    selectors = tested.get("selectors") or []
    for fixture in REQUIRED_FIXTURES:
        matches = [entry for entry in selectors if entry.get("kind") == "fixture" and entry.get("selector") == fixture]
        if len(matches) != 1 or int(matches[0].get("matchedCount") or 0) <= 0:
            raise ValueError("Required fixture has no unambiguous matched tests: " + fixture)
    if tested.get("cleanupSucceeded") is not True or tested.get("cleanupPending") is not False or tested.get("unresolvedResources"):
        raise ValueError("Test cleanup is unverified.")


def verify_workflow_run(run: dict, repository: str, checked_commit: str) -> None:
    if (run.get("repository", {}).get("full_name") != repository
            or run.get("head_repository", {}).get("full_name") != repository
            or run.get("path") != EVIDENCE_WORKFLOW or run.get("head_branch") != "main"
            or run.get("head_sha") != checked_commit or run.get("event") != "workflow_dispatch"
            or run.get("conclusion") != "success" or run.get("status") != "completed"):
        raise ValueError("Evidence must come from the controlled main-branch workflow in this repository.")
    completed = datetime.fromisoformat(str(run.get("updated_at") or "").replace("Z", "+00:00"))
    age = datetime.now(timezone.utc) - completed
    if age < timedelta(minutes=-5) or age > timedelta(days=7):
        raise ValueError("Unity acceptance run is expired or has a future timestamp (maximum age: 7 days).")


def verify_release_evidence(root: Path, run_id: str = "", release_tag: str = "") -> dict:
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("GITHUB_REPOSITORY is required for trusted evidence retrieval.")
    if release_tag:
        marker = tag_acceptance_run(root, release_tag)
        if run_id and marker and run_id != marker:
            raise ValueError("Tag acceptance run conflicts with the explicitly supplied run ID.")
        run_id = marker or run_id
        if git(root, "rev-parse", release_tag + "^{commit}") != git(root, "rev-parse", "HEAD"):
            raise ValueError("Checked-out commit does not match release tag.")
    if not re.fullmatch(r"[0-9]+", run_id):
        raise ValueError("A successful acceptance workflow run ID is required.")
    run = json.loads(subprocess.check_output(
        ["gh", "api", f"repos/{repository}/actions/runs/{run_id}"], text=True, encoding="utf-8"))
    checked_commit = str(run.get("head_sha") or "")
    verify_workflow_run(run, repository, checked_commit)
    current = source_identity(root)
    verify_release_delta(root, checked_commit, current["sourceCommit"])
    if current != source_identity(root, current["sourceCommit"]):
        raise ValueError("Release workspace differs from its committed source.")
    source = source_identity(root, checked_commit)
    with tempfile.TemporaryDirectory(prefix="upilot-release-evidence-") as directory:
        subprocess.run(["gh", "run", "download", run_id, "--repo", repository, "--name", EVIDENCE_ARTIFACT,
                        "--dir", directory], check=True, timeout=120)
        content = (Path(directory) / "summary.json").read_bytes()
        manifest = json.loads((Path(directory) / "evidence.json").read_text(encoding="utf-8"))
        digest = hashlib.sha256(content).hexdigest()
        if manifest.get("sha256") != digest or manifest.get("bytes") != len(content):
            raise ValueError("Unity summary hash/size mismatch.")
        report = json.loads(content)
        verify_required_summary(report, source)
    return {"checked": True, "passed": True, "runId": run_id, "runGuid": report["runGuid"],
            "checkedSource": source, "releaseSource": current, "sha256": digest, "bytes": len(content),
            "fixtures": list(REQUIRED_FIXTURES), "workflow": EVIDENCE_WORKFLOW}
