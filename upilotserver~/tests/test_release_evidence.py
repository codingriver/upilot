import copy
import json
import subprocess
from datetime import datetime, timezone

import pytest
from upilot_mcp.release_evidence import REQUIRED_FIXTURES, verify_required_summary, verify_release_delta, verify_workflow_run


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
def test_release_delta_allows_only_version_fields(tmp_path, invalid_change):
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
