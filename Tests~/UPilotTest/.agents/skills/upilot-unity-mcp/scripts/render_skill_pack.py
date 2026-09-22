#!/usr/bin/env python3
"""Render and verify the tracked UPilot Skill entrypoint artifacts."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
MANIFEST_NAME = "template-manifest.json"
DOCUMENTATION_MARKER = "<!-- Generated from skills/upilot-unity-mcp/AGENTS.md.template. Do not edit directly. -->\n\n"
ALLOWED_TOKENS = {
    "rulesVersion",
    "skillPackVersion",
    "upilotPackageVersion",
    "projectPath",
    "parentAgentRulesPath",
    "mcpUrl",
    "healthUrl",
    "generatedAt",
}
TOKEN_PATTERN = re.compile(r"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}")


class TemplateError(ValueError):
    pass


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise TemplateError(f"duplicate manifest property: {key}")
        result[key] = value
    return result


def load_manifest(root: Path = ROOT) -> dict:
    path = root / MANIFEST_NAME
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
    except (OSError, json.JSONDecodeError) as exc:
        raise TemplateError(f"invalid template manifest {path}: {exc}") from exc
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        raise TemplateError("template manifest schemaVersion must be 1")
    if set(manifest) != {"schemaVersion", "agentRulesVersion", "skillPackVersion", "defaultHttpPort", "templates"}:
        raise TemplateError("template manifest contains missing or unknown fields")
    for key in ("agentRulesVersion", "skillPackVersion"):
        value = manifest.get(key)
        if type(value) is not int or value <= 0:
            raise TemplateError(f"template manifest {key} must be a positive integer")
    port = manifest.get("defaultHttpPort")
    if type(port) is not int or port < 1 or port > 65535:
        raise TemplateError("template manifest defaultHttpPort must be in 1..65535")
    templates = manifest.get("templates")
    if not isinstance(templates, dict) or set(templates) != {"agentRules", "skill", "openai"}:
        raise TemplateError("template manifest templates must define agentRules, skill, and openai")
    resolved: list[Path] = []
    for key, relative in templates.items():
        if not isinstance(relative, str) or not relative or Path(relative).is_absolute() or ".." in Path(relative).parts:
            raise TemplateError(f"template path {key} must be a project-relative path")
        path = (root / relative).resolve()
        if root.resolve() not in path.parents or not path.is_file():
            raise TemplateError(f"template file is missing or outside the Skill root: {relative}")
        resolved.append(path)
    if len(set(resolved)) != len(resolved):
        raise TemplateError("template paths must be unique")
    return manifest


def package_version(root: Path = ROOT) -> str:
    path = root.parents[1] / "package.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8")).get("version")
    except (OSError, json.JSONDecodeError):
        value = None
    return str(value or "unknown")


def build_context(
    manifest: dict,
    *,
    http_port: int | None = None,
    project_path: str = "(runtime-selected Unity project)",
    package: str = "unknown",
    parent_agent_rules_path: str = "(none)",
    generated_at: str | None = None,
) -> dict[str, str]:
    port = manifest["defaultHttpPort"] if http_port is None else http_port
    if type(port) is not int or port < 1 or port > 65535:
        raise TemplateError("http port must be in 1..65535")
    if any("\n" in value or "\r" in value for value in (project_path, package, parent_agent_rules_path)):
        raise TemplateError("template context values must not contain newlines")
    return {
        "rulesVersion": str(manifest["agentRulesVersion"]),
        "skillPackVersion": str(manifest["skillPackVersion"]),
        "upilotPackageVersion": package,
        "projectPath": project_path,
        "parentAgentRulesPath": parent_agent_rules_path,
        "mcpUrl": f"http://127.0.0.1:{port}/mcp",
        "healthUrl": f"http://127.0.0.1:{port}/health",
        "generatedAt": generated_at or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }


def render_text(template: str, context: dict[str, str], *, label: str) -> str:
    unknown = sorted(set(TOKEN_PATTERN.findall(template)) - ALLOWED_TOKENS)
    if unknown:
        raise TemplateError(f"{label} contains unknown template tokens: {', '.join(unknown)}")
    missing = sorted(token for token in set(TOKEN_PATTERN.findall(template)) if token not in context)
    if missing:
        raise TemplateError(f"{label} is missing template values: {', '.join(missing)}")
    rendered = TOKEN_PATTERN.sub(lambda match: context[match.group(1)], template)
    if "{{" in rendered or "}}" in rendered:
        raise TemplateError(f"{label} contains an unresolved or malformed template token")
    return rendered.replace("\r\n", "\n").replace("\r", "\n").rstrip() + "\n"


def render_template(root: Path, manifest: dict, key: str, context: dict[str, str]) -> str:
    relative = manifest["templates"][key]
    text = (root / relative).read_text(encoding="utf-8")
    if key != "agentRules" and "generatedAt" in TOKEN_PATTERN.findall(text):
        raise TemplateError(f"{relative} must not use generatedAt; Skill outputs must be deterministic")
    return render_text(text, context, label=relative)


def render_skill_outputs(root: Path, context: dict[str, str], manifest: dict | None = None) -> dict[Path, str]:
    manifest = manifest or load_manifest(root)
    return {
        root / "SKILL.md": render_template(root, manifest, "skill", context),
        root / "agents" / "openai.yaml": render_template(root, manifest, "openai", context),
    }


def render_agent_documentation(root: Path, manifest: dict | None = None) -> tuple[Path, str]:
    manifest = manifest or load_manifest(root)
    context = build_context(
        manifest,
        project_path="<UNITY_PROJECT_ROOT>",
        package=package_version(root),
        parent_agent_rules_path="(nearest ancestor AGENTS.md, or none)",
        generated_at="(documentation profile)",
    )
    output = root.parents[1] / "Documentation~" / "AgentRules" / "AGENTS.upilot.md"
    return output, DOCUMENTATION_MARKER + render_template(root, manifest, "agentRules", context)


def template_sha256(root: Path, manifest: dict | None = None) -> str:
    manifest = manifest or load_manifest(root)
    digest = hashlib.sha256()
    paths = [root / MANIFEST_NAME] + [root / manifest["templates"][key] for key in sorted(manifest["templates"])]
    for path in paths:
        relative = path.relative_to(root).as_posix()
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def source_context(root: Path = ROOT, *, http_port: int | None = None, project_path: str = "", package: str = "") -> tuple[dict, dict[str, str]]:
    manifest = load_manifest(root)
    context = build_context(
        manifest,
        http_port=http_port,
        project_path=project_path or "(runtime-selected Unity project)",
        package=package or package_version(root),
    )
    return manifest, context


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--check", action="store_true", help="Verify tracked generated files (default)")
    action.add_argument("--write", action="store_true", help="Rewrite tracked generated files")
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--http-port", type=int)
    parser.add_argument("--project-path", default="")
    parser.add_argument("--package-version", default="")
    args = parser.parse_args(argv)
    root = args.root.expanduser().resolve()
    try:
        manifest, context = source_context(
            root,
            http_port=args.http_port,
            project_path=args.project_path,
            package=args.package_version,
        )
        outputs = render_skill_outputs(root, context, manifest)
        if root == ROOT:
            documentation_path, documentation_content = render_agent_documentation(root, manifest)
            outputs[documentation_path] = documentation_content
        drift: list[str] = []
        for path, expected in outputs.items():
            actual = path.read_bytes() if path.is_file() else None
            if actual == expected.encode("utf-8"):
                continue
            if args.write:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(expected, encoding="utf-8", newline="\n")
                print(f"Rendered {path}")
            else:
                drift.append(str(path))
        if drift:
            raise TemplateError("generated Skill artifacts are stale: " + ", ".join(drift))
        print(f"UPilot Skill templates ok: root={root} skillPackVersion={manifest['skillPackVersion']}")
        return 0
    except (OSError, TemplateError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
