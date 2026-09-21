#!/usr/bin/env python3
"""Install UPilot into a Unity project for agent-driven setup."""

from __future__ import annotations

import argparse
from contextlib import nullcontext, redirect_stdout
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from pathlib import Path
from types import SimpleNamespace


REPO_URL = "https://github.com/codingriver/upilot.git"
UPM_PACKAGE = "io.github.codingriver.upilot"
SKILL_NAME = "upilot-unity-mcp"

FLOW_DEPS = {
    "com.unity.inputsystem": "1.19.0",
    "com.unity.ui": "2.0.0",
    "com.unity.ui.test-framework": "6.3.0",
    "com.unity.test-framework": "1.7.0",
}


def repo_root_from_script() -> Path:
    return Path(__file__).resolve().parents[3]


def run(cmd: list[str], cwd: Path | None = None, dry_run: bool = False) -> None:
    print("+ " + " ".join(cmd))
    if dry_run:
        return
    subprocess.run(cmd, cwd=str(cwd) if cwd else None, check=True)


def parse_dep(value: str) -> tuple[str, str]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected name=version")
    name, version = value.split("=", 1)
    name = name.strip()
    version = version.strip()
    if not name or not version:
        raise argparse.ArgumentTypeError("expected name=version")
    return name, version


def parse_port(value: str) -> int:
    try:
        port = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("expected an integer port") from exc
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("port must be between 1 and 65535")
    return port


def parse_mcp_name(value: str) -> str:
    name = value.strip()
    if not re.fullmatch(r"[A-Za-z0-9_-]+", name):
        raise argparse.ArgumentTypeError("MCP name may contain only letters, digits, '_' and '-'")
    return name


def load_manifest(path: Path) -> dict:
    if not path.is_file():
        raise SystemExit(f"Unity manifest not found: {path}")
    with path.open("r", encoding="utf-8-sig") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise SystemExit(f"Unity manifest is not a JSON object: {path}")
    data.setdefault("dependencies", {})
    if not isinstance(data["dependencies"], dict):
        raise SystemExit("Unity manifest dependencies must be an object")
    return data


def _manifest_style(path: Path) -> tuple[str, bool, str]:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else ("\r" if "\r" in text else "\n")
    return text, has_bom, newline


def save_manifest(path: Path, data: dict, dry_run: bool, *, has_bom: bool = False, newline: str = "\n") -> None:
    text = (json.dumps(data, indent=2, ensure_ascii=False) + "\n").replace("\n", newline)
    if dry_run:
        print(f"Would write {path}")
        print(text)
        return
    encoded = text.encode("utf-8")
    if has_bom:
        encoded = b"\xef\xbb\xbf" + encoded
    path.write_bytes(encoded)


def _equivalent_local_upm_reference(value: str, unity_project: Path, upilot_dir: Path) -> bool:
    if not str(value).startswith("file:"):
        return False
    raw_path = str(value)[5:].replace("/", os.sep)
    candidate = Path(raw_path)
    if not candidate.is_absolute():
        candidate = unity_project / "Packages" / candidate
    try:
        return candidate.resolve() == upilot_dir.resolve()
    except OSError:
        return False


def ensure_upilot_repo(args: argparse.Namespace) -> Path:
    if args.clone_to:
        repo_dir = Path(args.clone_to).expanduser().resolve()
        if not repo_dir.exists():
            run(["git", "clone", args.repo_url, str(repo_dir)], dry_run=args.dry_run)
        elif not (repo_dir / ".git").exists():
            raise SystemExit(f"--clone-to exists but is not a git repo: {repo_dir}")
        return repo_dir
    return Path(args.upilot_dir).expanduser().resolve()


def python_executable_for_venv(venv: Path) -> Path:
    if os.name == "nt":
        return venv / "Scripts" / "python.exe"
    return venv / "bin" / "python"


def setup_python_env(upilot_dir: Path, venv: Path, python: str, dry_run: bool) -> Path:
    server_dir = upilot_dir / "upilotserver~"
    if not server_dir.is_dir():
        raise SystemExit(f"upilotserver~ not found: {server_dir}")
    if not venv.exists():
        run([python, "-m", "venv", str(venv)], dry_run=dry_run)
    venv_python = python_executable_for_venv(venv)
    run([str(venv_python), "-m", "pip", "install", "--upgrade", "pip"], dry_run=dry_run)
    run([str(venv_python), "-m", "pip", "install", "-e", str(server_dir)], dry_run=dry_run)
    return venv_python


def update_unity_manifest(args: argparse.Namespace, upilot_dir: Path) -> None:
    unity_project = Path(args.unity_project).expanduser().resolve()
    manifest_path = unity_project / "Packages" / "manifest.json"
    _, has_bom, newline = _manifest_style(manifest_path)
    data = load_manifest(manifest_path)
    deps = data["dependencies"]
    original_dependency = str(deps.get(UPM_PACKAGE) or "")

    if args.use_local_upm:
        value = original_dependency if _equivalent_local_upm_reference(original_dependency, unity_project, upilot_dir) else "file:" + upilot_dir.as_posix()
    else:
        upm_ref = str(args.upm_ref or "").strip()
        if not upm_ref:
            raise SystemExit(
                "Remote UPM installation requires --upm-ref <tag|branch|commit>. "
                "Use --use-local-upm for a local checkout."
            )
        value = f"{args.repo_url}#{upm_ref}"
    deps[UPM_PACKAGE] = value

    if args.enable_flow:
        flow_deps = dict(FLOW_DEPS)
        flow_deps.update(dict(args.upm_dep or []))
        deps.update(flow_deps)
        testables = data.setdefault("testables", [])
        if isinstance(testables, list) and "com.unity.inputsystem" not in testables:
            testables.append("com.unity.inputsystem")

    if value == original_dependency and not args.enable_flow:
        print(f"Unity manifest preserved (equivalent UPM reference): {manifest_path}")
        return
    save_manifest(manifest_path, data, args.dry_run, has_bom=has_bom, newline=newline)
    print(f"Unity manifest configured: {manifest_path}")


def _skill_template_version(upilot_dir: Path) -> int:
    manifest = _skill_renderer().load_manifest(upilot_dir / "skills" / SKILL_NAME)
    return int(manifest["skillPackVersion"])


def _skill_renderer():
    path = Path(__file__).with_name("render_skill_pack.py")
    spec = importlib.util.spec_from_file_location("_upilot_skill_renderer", path)
    module = importlib.util.module_from_spec(spec)
    previous = sys.dont_write_bytecode
    try:
        sys.dont_write_bytecode = True
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = previous
    return module


def _skill_content_hash(target: Path) -> str:
    digest = hashlib.sha256()
    files = sorted(
        (path for path in target.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(target).as_posix().lower(),
    )
    for path in files:
        relative = path.relative_to(target).as_posix()
        if path.name.lower() == ".upilot-install.json" or any(
            part.lower() == "__pycache__" or part.lower().endswith((".meta", ".pyc", ".pyo"))
            for part in path.relative_to(target).parts
        ):
            continue
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def _skill_content_hash_from_source(source: Path, rendered: dict[Path, str]) -> str:
    rendered_by_relative = {
        path.relative_to(source).as_posix(): content.encode("utf-8")
        for path, content in rendered.items()
    }
    content_by_relative: dict[str, bytes] = {}
    for path in source.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(source).as_posix()
        if path.name.lower() == ".upilot-install.json" or any(
            part.lower() == "__pycache__" or part.lower().endswith((".meta", ".pyc", ".pyo"))
            for part in path.relative_to(source).parts
        ):
            continue
        content_by_relative[relative] = path.read_bytes()
    content_by_relative.update(rendered_by_relative)
    digest = hashlib.sha256()
    for relative in sorted(content_by_relative, key=str.lower):
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(content_by_relative[relative])
        digest.update(b"\0")
    return digest.hexdigest()


def _exact_target_hash(target: Path) -> tuple[str, int, int]:
    if target.is_file():
        data = target.read_bytes()
        return hashlib.sha256(data).hexdigest(), 1, len(data)
    if not target.is_dir():
        raise FileNotFoundError(target)

    digest = hashlib.sha256()
    files = sorted(
        (path for path in target.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(target).as_posix().lower(),
    )
    total_bytes = 0
    for path in files:
        relative = path.relative_to(target).as_posix()
        data = path.read_bytes()
        total_bytes += len(data)
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(data)
        digest.update(b"\0")
    return digest.hexdigest(), len(files), total_bytes


def _backup_relative_target(project_root: Path, target: Path) -> Path:
    project_root = project_root.resolve()
    target = target.resolve()
    try:
        return target.relative_to(project_root)
    except ValueError:
        anchor = target.anchor.replace(":", "_").strip("/\\") or "root"
        remaining = target.parts[1:] if target.anchor else target.parts
        return Path("external") / anchor / Path(*remaining)


def _backup_skill_target(
    target: Path,
    project_root: Path,
    *,
    trigger: str,
    reason: str,
    agent_rules_version: int,
    skill_pack_version: int,
) -> Path:
    timestamp = datetime.now(timezone.utc)
    backup_root = project_root / ".upilot" / "backups" / "agent-integrations"
    session = backup_root / f"{timestamp.strftime('%Y%m%dT%H%M%S%fZ')}-{uuid.uuid4().hex[:8]}"
    backup_target = session / _backup_relative_target(project_root, target)
    try:
        backup_target.parent.mkdir(parents=True, exist_ok=True)
        if target.is_dir():
            shutil.copytree(target, backup_target)
        elif target.is_file():
            shutil.copy2(target, backup_target)
        else:
            raise FileNotFoundError(target)

        original_hash, file_count, total_bytes = _exact_target_hash(target)
        backup_hash, backup_count, backup_bytes = _exact_target_hash(backup_target)
        if (original_hash, file_count, total_bytes) != (backup_hash, backup_count, backup_bytes):
            raise OSError(f"backup verification failed for {target}")
        manifest = {
            "schemaVersion": 1,
            "trigger": trigger,
            "reason": reason,
            "originalTargetPath": str(target.resolve()),
            "originalContentSha256": original_hash,
            "agentRulesVersion": agent_rules_version,
            "skillPackVersion": skill_pack_version,
            "backedUpAt": timestamp.strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
            "fileCount": file_count,
            "totalBytes": total_bytes,
        }
        session.joinpath("manifest.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
        )
        return session
    except Exception as exc:
        raise OSError(
            f"could not back up UPilot-managed target; original was not changed: {target}"
        ) from exc


def _read_skill_metadata(target: Path) -> dict | None:
    path = target / ".upilot-install.json"
    try:
        metadata = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    if not isinstance(metadata, dict):
        return None
    schema = metadata.get("schemaVersion", 1)
    version = metadata.get("templateVersion")
    content_hash = metadata.get("contentSha256")
    if schema not in {1, 2} or type(version) is not int or version <= 0:
        return None
    if not isinstance(content_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", content_hash):
        return None
    if schema == 2:
        template_hash = metadata.get("templateSha256")
        render_context = metadata.get("renderContext")
        if not isinstance(template_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", template_hash):
            return None
        if not isinstance(render_context, dict) or not all(
            isinstance(render_context.get(key), str) and bool(render_context[key])
            for key in ("projectPath", "mcpUrl", "healthUrl", "upilotPackageVersion")
        ):
            return None
        if not isinstance(metadata.get("renderedAt"), str) or not metadata["renderedAt"]:
            return None
    return metadata


def _write_skill_metadata(
    target: Path,
    upilot_dir: Path,
    *,
    template_hash: str,
    context: dict[str, str],
) -> None:
    metadata = {
        "schemaVersion": 2,
        "templateVersion": _skill_template_version(upilot_dir),
        "templateSha256": template_hash,
        "contentSha256": _skill_content_hash(target),
        "renderContext": {
            "projectPath": context["projectPath"],
            "mcpUrl": context["mcpUrl"],
            "healthUrl": context["healthUrl"],
            "upilotPackageVersion": context["upilotPackageVersion"],
        },
        "renderedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    target.joinpath(".upilot-install.json").write_text(
        json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def _integration_engine():
    path = Path(__file__).with_name("sync_integrations.py")
    spec = importlib.util.spec_from_file_location("_upilot_integrations", path)
    module = importlib.util.module_from_spec(spec)
    previous = sys.dont_write_bytecode
    try:
        sys.dont_write_bytecode = True
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = previous
    return module


def synchronize_integrations(args, upilot_dir: Path, scope="all", root: Path | None = None) -> dict:
    project = Path(args.unity_project).expanduser().absolute() if args.unity_project else Path.cwd()
    helpers = SimpleNamespace(**{name: globals()[name] for name in (
        "_skill_renderer", "_skill_content_hash", "_read_skill_metadata", "_exact_target_hash", "_backup_skill_target")})
    return _integration_engine().synchronize(root or project, upilot_dir, apply=not args.dry_run, scope=scope,
        http_port=args.http_port, context_project=project, trigger="python-install", helpers=helpers)


def _report_integrations(report: dict) -> None:
    for target in report["targets"]:
        print(f"{target['status']}: {target['path']}" +
              (f" -> backup: {target['backupPath']}" if target["backupPath"] else "") +
              (f": {target['error']}" if target["error"] else ""))
    if not report["ok"]:
        raise SystemExit("one or more UPilot integration targets failed to synchronize: " +
                         report["error"] + "\n" + "\n".join(t["error"] for t in report["targets"] if t["error"]))


def install_skill(args: argparse.Namespace, upilot_dir: Path) -> list[dict]:
    reports = []
    if args.install_skill in {"repo", "both"}:
        reports.append(synchronize_integrations(args, upilot_dir, "skills"))
    if args.install_skill in {"user", "both"}:
        reports.append(synchronize_integrations(args, upilot_dir, "skills", Path.home()))
    for report in reports:
        if not getattr(args, "json", False):
            _report_integrations(report)
    return reports


def toml_string(value: str) -> str:
    return json.dumps(value)


def remove_toml_table(text: str, table_names: set[str]) -> str:
    lines = text.splitlines()
    output: list[str] = []
    skipping = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            name = stripped.strip("[]")
            skipping = name in table_names
        if not skipping:
            output.append(line)
    return "\n".join(output).rstrip() + ("\n" if output else "")


def write_codex_mcp(args: argparse.Namespace) -> None:
    if args.write_codex_mcp == "none":
        return

    unity_project = Path(args.unity_project).expanduser().resolve()
    if args.write_codex_mcp == "project":
        config_path = unity_project / ".codex" / "config.toml"
    else:
        config_path = Path.home() / ".codex" / "config.toml"

    existing = config_path.read_text(encoding="utf-8") if config_path.exists() else ""
    table_name = f"mcp_servers.{args.mcp_name}"
    existing = remove_toml_table(existing, {table_name, f"{table_name}.env"})
    block = (
        f"\n[{table_name}]\n"
        f"url = {toml_string(f'http://127.0.0.1:{args.http_port}/mcp')}\n"
        "startup_timeout_sec = 30\n"
        "tool_timeout_sec = 300\n"
    )
    text = existing.rstrip() + "\n" + block
    if args.dry_run:
        print(f"Would write {config_path}")
        print(text)
        return
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_text(text, encoding="utf-8")
    print(f"Codex MCP config written: {config_path}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Install UPilot for a Unity project.")
    parser.add_argument("--unity-project", help="Unity project root containing Packages/manifest.json")
    parser.add_argument("--repo-url", default=REPO_URL)
    parser.add_argument("--upm-ref", help="Required tag, branch, or commit for remote UPM installation")
    parser.add_argument("--upilot-dir", default=str(repo_root_from_script()))
    parser.add_argument("--clone-to", help="Clone upilot here if it is not present")
    parser.add_argument("--use-local-upm", action="store_true", help="Use file:<upilot-dir> instead of Git URL in Unity manifest")
    parser.add_argument("--enable-flow", action="store_true", help="Explicitly add optional UPilot Flow Unity package dependencies")
    parser.add_argument("--upm-dep", action="append", type=parse_dep, help="Override/add a Unity package dependency as name=version")
    parser.add_argument("--setup-python", action="store_true", help="Create a Python venv and install the MCP server package")
    parser.add_argument("--no-python", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--venv", help="Python venv path; default is upilotserver~/.venv")
    parser.add_argument("--install-skill", choices=["none", "repo", "user", "both"], default="repo")
    parser.add_argument(
        "--skill-client",
        action="append",
        choices=["codex", "claude", "cursor", "opencode"],
        help=(
            "Compatibility option. All supported project Skill targets are synchronized; "
            "Codex, Cursor, and OpenCode share .agents/skills, while Claude uses .claude/skills."
        ),
    )
    parser.add_argument("--skill-only", action="store_true", help="Synchronize Skill content without modifying Packages/manifest.json")
    parser.add_argument("--integrations-only", action="store_true", help="Sync all five project Agent/Skill targets only")
    parser.add_argument("--json", action="store_true", help="Emit structured integration results to stdout")
    parser.add_argument("--write-codex-mcp", choices=["none", "project", "user"], default="none")
    parser.add_argument("--http-port", type=parse_port, help="Public HTTP port; defaults to project config then template manifest")
    parser.add_argument("--mcp-name", type=parse_mcp_name, default="upilot", help="Codex MCP registration name")
    parser.add_argument("--port", help=argparse.SUPPRESS)
    parser.add_argument(
        "--force",
        action="store_true",
        help="Compatibility option; UPilot-owned Skill targets are always synchronized authoritatively",
    )
    parser.add_argument("--dry-run", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    output = sys.stdout
    with redirect_stdout(sys.stderr) if args.json else nullcontext():
        reports, code = _main(args)
    if args.json:
        result = reports[0] if len(reports) == 1 else {"ok": code == 0, "results": reports}
        print(json.dumps(result, ensure_ascii=False), file=output)
    return code


def _main(args) -> tuple[list[dict], int]:
    if args.port is not None:
        raise SystemExit("Unity Bridge WebSocket ports are internal; use --http-port for the external MCP endpoint.")
    if args.skill_only and (args.setup_python or args.write_codex_mcp != "none"):
        raise SystemExit("--skill-only cannot modify Python environments or MCP client configurations")
    if args.integrations_only:
        if not args.unity_project or not (Path(args.unity_project) / "Packages/manifest.json").is_file():
            raise SystemExit("--integrations-only requires an explicit Unity project containing Packages/manifest.json")
        if (args.skill_only or args.install_skill != "repo" or args.write_codex_mcp != "none"
                or args.setup_python or args.clone_to or args.upm_ref or args.use_local_upm or args.enable_flow or args.upm_dep):
            raise SystemExit("--integrations-only cannot be combined with Skill-only, user-level, dependency or environment options")
        source = Path(args.upilot_dir).expanduser().absolute()
        report = synchronize_integrations(args, source)
        if not args.json:
            _report_integrations(report)
        return [report], 0 if report["ok"] else 1
    if args.port is not None:
        raise SystemExit(
            "--port is no longer a client configuration option. "
            "Unity Bridge WebSocket ports are internal; use --http-port for the external MCP endpoint."
        )
    if args.setup_python and args.no_python:
        raise SystemExit("--setup-python and the deprecated --no-python option cannot be used together")
    if args.use_local_upm and args.upm_ref:
        raise SystemExit("--use-local-upm and --upm-ref are mutually exclusive")
    upilot_dir = ensure_upilot_repo(args)
    if not upilot_dir.exists() and not args.dry_run:
        raise SystemExit(f"upilot directory does not exist: {upilot_dir}")

    source_manifest = _skill_renderer().load_manifest(upilot_dir / "skills" / SKILL_NAME)
    project = Path(args.unity_project).expanduser().absolute() if args.unity_project else Path.cwd()
    args.http_port = _integration_engine().resolve_port(project, args.http_port, source_manifest)
    if args.setup_python and not args.no_python:
        venv = Path(args.venv).expanduser().resolve() if args.venv else upilot_dir / "upilotserver~" / ".venv"
        setup_python_env(upilot_dir, venv, args.python, args.dry_run)

    if args.unity_project and not args.skill_only:
        update_unity_manifest(args, upilot_dir)
    elif args.write_codex_mcp != "none":
        raise SystemExit("--write-codex-mcp requires --unity-project")

    if args.skill_only and args.install_skill == "none":
        raise SystemExit("--skill-only requires --install-skill repo, user, or both")
    reports = []
    if args.unity_project and not args.skill_only:
        report = synchronize_integrations(args, upilot_dir, "all" if args.install_skill in {"repo", "both"} else "rules")
        reports.append(report)
        if args.install_skill in {"user", "both"}:
            reports.append(synchronize_integrations(args, upilot_dir, "skills", Path.home()))
        if not args.json:
            for report in reports:
                _report_integrations(report)
    else:
        reports = install_skill(args, upilot_dir)
    if any(not r["ok"] for r in reports):
        return reports, 1
    write_codex_mcp(args)

    print("upilot install complete")
    return reports, 0


if __name__ == "__main__":
    raise SystemExit(main())
