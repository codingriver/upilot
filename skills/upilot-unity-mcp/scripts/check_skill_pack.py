#!/usr/bin/env python3
"""Validate UPilot skill, registry, config, docs, and repository entry consistency."""

from __future__ import annotations

import re
import sys
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
    package = (REPO_ROOT / "package.json").read_text(encoding="utf-8")
    pyproject = (REPO_ROOT / "upilotserver~" / "pyproject.toml").read_text(encoding="utf-8")
    server = (REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "mcp_stdio_server.py").read_text(encoding="utf-8")
    tool_modules = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "mcp_tools").glob("*_tools.py"))
    )
    config = (REPO_ROOT / "upilotserver~" / "src" / "upilot_mcp" / "config.py").read_text(encoding="utf-8")
    agent_setup = (REPO_ROOT / "Editor" / "Pilot" / "UPilotAgentSetup.cs").read_text(encoding="utf-8")
    repo_entry = (REPO_ROOT / ".agents" / "skills" / "upilot-unity-mcp" / "SKILL.md").read_text(encoding="utf-8")

    required = {
        "package id": (package, '"name": "io.github.codingriver.upilot"'),
        "package version": (package, '"version": "0.2.0"'),
        "python version": (pyproject, 'version = "0.2.0"'),
        "HTTP default": (config, "http_port: int = 8011"),
        "stable list": (server, "_list_tools_stable"),
        "capability rule": (agent_setup, "unity_capabilities_get"),
        "no-repeat compile rule": (agent_setup, "Do not compile again when no code changed"),
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
