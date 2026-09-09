"""Offline workflow contracts: never dispatch, commit, tag, or publish."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
PINS = {
    "actions/checkout": "3d3c42e5aac5ba805825da76410c181273ba90b1",
    "actions/setup-python": "5fda3b95a4ea91299a34e894583c3862153e4b97",
    "actions/upload-artifact": "043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
    "softprops/action-gh-release": "efb35369e0ad2afab669f228072c1b0d510eae64",
}


def test_all_workflow_actions_use_verified_full_sha():
    for workflow in (ROOT / ".github/workflows").glob("*.yml"):
        for action, revision in re.findall(r"uses:\s+([\w/-]+)@([\w.-]+)", workflow.read_text(encoding="utf-8")):
            assert action in PINS, (workflow, action)
            assert revision == PINS[action] and re.fullmatch(r"[a-f0-9]{40}", revision), (workflow, action)


def test_release_allows_optional_evidence_and_non_tag_build_never_uploads_release():
    prepare = (ROOT / ".github/workflows/prepare-release.yml").read_text(encoding="utf-8")
    build = (ROOT / ".github/workflows/build-server-exe.yml").read_text(encoding="utf-8")
    assert "acceptanceRunId:" in prepare and "required: false" in prepare
    assert 'if [[ -n "$UPILOT_ACCEPTANCE_RUN_ID" ]]' in prepare
    assert "--require-unity-summary --release-tag" not in build
    release_step = build[build.rfind("- name:", 0, build.index("uses: softprops/action-gh-release")):]
    assert "if: startsWith(github.ref, 'refs/tags/')" in release_step
    assert 'cache: "pip"' in build
