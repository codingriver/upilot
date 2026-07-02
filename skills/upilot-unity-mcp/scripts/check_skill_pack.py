#!/usr/bin/env python3
"""Validate the upilot skill package structure."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

REQUIRED_FILES = [
    "SKILL.md",
    "agents/openai.yaml",
    "references/workflows.md",
    "references/tool-routing.md",
    "references/client-configs.md",
    "references/installation.md",
    "references/safety.md",
    "scripts/install_upilot.py",
    "scripts/check_skill_pack.py",
]

REQUIRED_SKILL_REFERENCES = [
    "references/workflows.md",
    "references/tool-routing.md",
    "references/client-configs.md",
    "references/installation.md",
    "references/safety.md",
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
        'display_name: "upilot Unity MCP"',
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


def main() -> int:
    for relative in REQUIRED_FILES:
        require_file(relative)
    check_skill_frontmatter()
    check_openai_yaml()
    check_unity_meta_files()
    print("upilot skill pack ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
