"""Offline workflow contracts: never dispatch, commit, tag, or publish."""
from pathlib import Path
import re
import subprocess

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
    assert "Render version-bound Skill artifacts" in prepare
    assert "render_skill_pack.py --check" in prepare
    assert "--require-unity-summary --release-tag" not in build
    release_step = build[build.rfind("- name:", 0, build.index("uses: softprops/action-gh-release")):]
    assert "if: startsWith(github.ref, 'refs/tags/')" in release_step
    assert 'cache: "pip"' in build


def test_release_local_gate_precedes_asset_publication_and_rejects_overwrite():
    prepare = (ROOT / ".github/workflows/prepare-release.yml").read_text(encoding="utf-8")
    build = (ROOT / ".github/workflows/build-server-exe.yml").read_text(encoding="utf-8")
    assert "ref: ${{ github.sha }}" in prepare
    assert "Require main dispatch source" in prepare
    assert "gh api -i" in prepare and "release_status" in prepare
    assert "--release-tag '${{ github.ref_name }}'" in build
    assert build.index("Verify local release assets and EXE identity") < build.index("Upload artifact")
    assert build.index("Verify local release assets and EXE identity") < build.index("Publish release assets")
    assert build.index("Preserve local release verification") < build.index("Publish release assets")
    assert "--verify-only" in build
    assert "Reject existing release before publishing" in build
    assert "overwrite_files: false" in build
    assert "fail_on_unmatched_files: true" in build
    assert "upilot-mcp-server-*-win-x64.exe" not in build
    assert "fail-fast: false" in (ROOT / ".github/workflows/quality-check.yml").read_text(encoding="utf-8")


def test_generated_release_artifacts_checkout_with_lf_on_windows():
    paths = (
        "skills/upilot-unity-mcp/SKILL.md",
        "skills/upilot-unity-mcp/agents/openai.yaml",
        "Documentation~/AgentRules/AGENTS.upilot.md",
    )
    result = subprocess.run(
        ["git", "check-attr", "eol", "--", *paths],
        cwd=ROOT, capture_output=True, text=True, check=True,
    )
    assert result.stdout.splitlines() == [f"{path}: eol: lf" for path in paths]


def test_generated_artifacts_really_checkout_as_lf_with_autocrlf_on_or_off(tmp_path):
    paths = ("skills/upilot-unity-mcp/SKILL.md", "skills/upilot-unity-mcp/agents/openai.yaml",
             "Documentation~/AgentRules/AGENTS.upilot.md")
    source = tmp_path / "source"
    source.mkdir()
    for name in paths:
        target = source / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b"first\nsecond\n")
    (source / ".gitattributes").write_text("".join(f"/{name} text eol=lf\n" for name in paths))

    def git(*args, cwd=source):
        return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)

    git("init")
    git("config", "user.email", "tests@example.invalid")
    git("config", "user.name", "Tests")
    git("add", ".")
    git("commit", "-m", "lf")
    for autocrlf in ("true", "false"):
        checkout = tmp_path / f"checkout-{autocrlf}"
        git("-c", f"core.autocrlf={autocrlf}", "clone", str(source), str(checkout))
        for name in paths:
            assert (checkout / name).read_bytes() == b"first\nsecond\n"
        changed = checkout / paths[0]
        changed.write_bytes(b"first\r\nsecond\r\n")
        assert changed.read_bytes() != b"first\nsecond\n"
        changed.write_bytes(b"first\nold-version\n")
        assert changed.read_bytes() != b"first\nsecond\n"
        changed.write_bytes(b"first\nchanged-body\n")
        assert changed.read_bytes() != b"first\nsecond\n"
