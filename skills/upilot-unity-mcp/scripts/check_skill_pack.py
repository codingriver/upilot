#!/usr/bin/env python3
"""Validate UPilot skill, registry, config, docs, and repository entry consistency."""

from __future__ import annotations

import json
import re
import sys
import tomllib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = ROOT.parents[1]

REQUIRED_FILES = [
    "SKILL.md",
    "agents/openai.yaml",
    "references/workflows.md",
    "references/tool-routing.md",
    "references/client-configs.md",
    "references/installation.md",
    "references/safety.md",
    "references/flow.md",
    "scripts/install_upilot.py",
    "scripts/check_skill_pack.py",
]

REQUIRED_SKILL_REFERENCES = [
    "references/workflows.md",
    "references/tool-routing.md",
    "references/client-configs.md",
    "references/installation.md",
    "references/safety.md",
    "references/flow.md",
]


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def require_file(relative: str) -> Path:
    path = ROOT / relative
    if not path.is_file():
        fail(f"missing required file: {relative}")
    return path


def check_skill_frontmatter() -> None:
    text = require_file("SKILL.md").read_text(encoding="utf-8")
    match = re.match(r"^---\n(.*?)\n---\n", text, re.DOTALL)
    if not match:
        fail("SKILL.md is missing YAML frontmatter")
    frontmatter = match.group(1)
    if "name: upilot-unity-mcp" not in frontmatter:
        fail("SKILL.md frontmatter has the wrong skill name")
    if "description:" not in frontmatter:
        fail("SKILL.md frontmatter is missing description")
    for reference in REQUIRED_SKILL_REFERENCES:
        if reference not in text:
            fail(f"SKILL.md does not mention {reference}")


def check_openai_yaml() -> None:
    text = require_file("agents/openai.yaml").read_text(encoding="utf-8")
    required_fragments = [
        'display_name: "UPilot Unity MCP"',
        'short_description: "Control Unity Editor through MCP"',
        'brand_color: "#2563EB"',
        "Use $upilot-unity-mcp",
        'value: "upilot"',
        'url: "http://127.0.0.1:8011/mcp"',
    ]
    for fragment in required_fragments:
        if fragment not in text:
            fail(f"agents/openai.yaml missing fragment: {fragment}")


def check_unity_meta_files() -> None:
    for path in [ROOT, *ROOT.rglob("*")]:
        if ".meta" in path.name or "__pycache__" in path.parts:
            continue
        if path.is_file() or path.is_dir():
            meta = path.with_name(path.name + ".meta")
            if not meta.exists():
                fail(f"missing Unity meta file: {meta.relative_to(ROOT.parent)}")


def check_repository_consistency() -> None:
    package_path = REPO_ROOT / "package.json"
    pyproject_path = REPO_ROOT / "upilotserver~" / "pyproject.toml"
    package = package_path.read_text(encoding="utf-8")
    pyproject = pyproject_path.read_text(encoding="utf-8")
    package_data = json.loads(package)
    with pyproject_path.open("rb") as stream:
        pyproject_data = tomllib.load(stream)
    package_version = str(package_data.get("version", ""))
    python_version = str(pyproject_data.get("project", {}).get("version", ""))
    if not re.fullmatch(r"\d+\.\d+\.\d+", package_version):
        fail(f"package.json has an invalid semantic version: {package_version!r}")
    if python_version != package_version:
        fail(
            "package version mismatch: "
            f"package.json={package_version!r}, pyproject.toml={python_version!r}"
        )
    server = (REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "mcp_stdio_server.py").read_text(encoding="utf-8")
    tool_modules = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "mcp_tools").glob("*_tools.py"))
    )
    config = (REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "config.py").read_text(encoding="utf-8")
    agent_setup = (REPO_ROOT / "Editor" / "Pilot" / "UPilotAgentSetup.cs").read_text(encoding="utf-8")
    task_service = (REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "domain" / "task_service.py").read_text(encoding="utf-8")
    agent_template = (ROOT / "AGENTS.md.template").read_text(encoding="utf-8")
    agent_reference = (REPO_ROOT / "Documentation~" / "AgentRules" / "AGENTS.upilot.md").read_text(encoding="utf-8")
    skill = (ROOT / "SKILL.md").read_text(encoding="utf-8")
    installation = (ROOT / "references" / "installation.md").read_text(encoding="utf-8")
    installer = (ROOT / "scripts" / "install_upilot.py").read_text(encoding="utf-8")
    release_builder = (REPO_ROOT / "upilotserver~" / "deploy" / "build_release.py").read_text(encoding="utf-8")
    mcp_example_path = REPO_ROOT / "upilotserver~" / "mcp.example.json"
    mcp_example_text = mcp_example_path.read_text(encoding="utf-8")
    repo_entry = (REPO_ROOT / ".agents" / "skills" / "upilot-unity-mcp" / "SKILL.md").read_text(encoding="utf-8")

    rules_versions = {
        "C# generator": re.search(r"AgentRulesTemplateVersion\s*=\s*(\d+)", agent_setup),
        "Python installer": re.search(r"_UPILOT_RULES_VERSION\s*=\s*(\d+)", task_service),
        "reference document": re.search(r"^rulesVersion:\s*(\d+)\s*$", agent_reference, re.MULTILINE),
    }
    missing_versions = [label for label, match in rules_versions.items() if match is None]
    if missing_versions:
        fail("missing Agent rules version in " + ", ".join(missing_versions))
    resolved_versions = {label: match.group(1) for label, match in rules_versions.items()}
    if len(set(resolved_versions.values())) != 1:
        fail(f"Agent rules version mismatch: {resolved_versions}")
    skill_version = re.search(r"SkillInstallTemplateVersion\s*=\s*(\d+)", agent_setup)
    if skill_version is None or int(skill_version.group(1)) < 4:
        fail("Skill install template version must be at least 4 for the HTTP-only skill update")

    required = {
        "package id": (package, '"name": "io.github.codingriver.upilot"'),
        "HTTP default": (config, "http_port: int = 8011"),
        "stable list": (server, "_list_tools_stable"),
        "capability rule": (agent_template, "unity_capabilities_get"),
        "parent rules inheritance": (agent_template, "Parent Agent rules path"),
        "parent rules cycle guard": (agent_template, "circular references are skipped"),
        "safe compile rule": (agent_template, "unity_safe_compile_and_wait"),
        "no-repeat compile rule": (agent_template, "Do not compile again when no code changed"),
        "HTTP-only Agent rule": (agent_template, "Third-party AI tools must connect through Streamable HTTP"),
        "internal WebSocket rule": (agent_template, "WebSocket transport is internal to MCP Server <-> Unity Bridge"),
        "HTTP-only Skill rule": (skill, "only third-party AI client transport"),
        "explicit remote UPM ref documentation": (installation, "--upm-ref <STABLE_RELEASE_TAG>"),
        "local UPM documentation": (installation, "--use-local-upm"),
        "HTTP installer config": (installer, "url = {toml_string(f'http://127.0.0.1:{args.http_port}/mcp')}"),
        "explicit remote ref error": (installer, "Remote UPM installation requires --upm-ref"),
        "repository skill entry": (repo_entry, "../../../skills/upilot-unity-mcp/SKILL.md"),
    }
    for label, (text, fragment) in required.items():
        if fragment not in text:
            fail(f"repository consistency check failed for {label}: missing {fragment}")

    for tool in (
        "unity_capabilities_get",
        "unity_tools_find",
        "unity_operation_list",
        "unity_operation_get",
        "unity_task_start",
        "unity_task_status",
        "unity_task_cancel",
    ):
        if f"def {tool}" not in tool_modules:
            fail(f"core tool is not registered: {tool}")

    if "UIFlow" in ROOT.joinpath("SKILL.md").read_text(encoding="utf-8"):
        fail("canonical core SKILL.md must not preload legacy UIFlow guidance")

    forbidden_installer_fragments = {
        "hardcoded default UPM ref": "DEFAULT_UPM_REF",
        "generated stdio client transport": '"--transport", "stdio"',
        "generated internal Bridge port": '"--port", "8765"',
    }
    for label, fragment in forbidden_installer_fragments.items():
        if fragment in installer:
            fail(f"installer still contains {label}: {fragment}")

    for label, fragment in {
        "release stdio type": '"type": "stdio"',
        "release command config": '"command": cmd',
        "release args config": '"args": args',
    }.items():
        if fragment in release_builder:
            fail(f"release builder still emits {label}: {fragment}")

    try:
        mcp_example = json.loads(mcp_example_text)
        example_server = mcp_example["mcpServers"]["upilot"]
    except (KeyError, TypeError, ValueError) as exc:
        fail(f"invalid HTTP MCP example config: {exc}")
    if example_server.get("url") != "http://127.0.0.1:8011/mcp":
        fail("mcp.example.json must use the default HTTP /mcp endpoint")
    if any(key in example_server for key in ("command", "args", "env", "transport")):
        fail("mcp.example.json must not configure a client-side process or stdio transport")

    if re.search(r"\bv\d+\.\d+\.\d+\b", installation):
        fail("installation documentation must not hardcode a semantic release tag")


def main() -> int:
    for relative in REQUIRED_FILES:
        require_file(relative)
    check_skill_frontmatter()
    check_openai_yaml()
    check_unity_meta_files()
    check_repository_consistency()
    print("UPilot skill pack ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
