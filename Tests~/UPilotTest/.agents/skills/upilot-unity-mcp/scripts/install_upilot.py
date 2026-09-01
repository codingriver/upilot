#!/usr/bin/env python3
"""Install UPilot into a Unity project for agent-driven setup."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path


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
    setup_path = upilot_dir / "Editor" / "Core" / "UPilotAgentSetup.cs"
    match = re.search(r"SkillInstallTemplateVersion\s*=\s*(\d+)", setup_path.read_text(encoding="utf-8"))
    if not match:
        raise SystemExit(f"SkillInstallTemplateVersion not found: {setup_path}")
    return int(match.group(1))


def _skill_content_hash(target: Path) -> str:
    digest = hashlib.sha256()
    files = sorted(
        (path for path in target.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(target).as_posix().lower(),
    )
    for path in files:
        relative = path.relative_to(target).as_posix()
        if path.name == ".upilot-install.json" or "__pycache__" in path.parts or path.suffix.lower() in {".pyc", ".pyo"}:
            continue
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def _write_skill_metadata(target: Path, upilot_dir: Path) -> None:
    metadata = {
        "templateVersion": _skill_template_version(upilot_dir),
        "contentSha256": _skill_content_hash(target),
    }
    target.joinpath(".upilot-install.json").write_text(
        json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def install_skill(args: argparse.Namespace, upilot_dir: Path) -> None:
    if args.install_skill == "none":
        return
    source = upilot_dir / "skills" / SKILL_NAME
    if not source.is_dir():
        raise SystemExit(f"source skill not found: {source}")

    clients = set(args.skill_client or ("codex", "claude", "cursor", "opencode"))
    targets: list[Path] = []
    unity_project = Path(args.unity_project).expanduser().resolve() if args.unity_project else Path.cwd()
    if args.install_skill in {"repo", "both"}:
        if clients.intersection({"codex", "cursor", "opencode"}):
            targets.append(unity_project / ".agents" / "skills" / SKILL_NAME)
        if "claude" in clients:
            targets.append(unity_project / ".claude" / "skills" / SKILL_NAME)
    if args.install_skill in {"user", "both"}:
        if clients.intersection({"codex", "cursor", "opencode"}):
            targets.append(Path.home() / ".agents" / "skills" / SKILL_NAME)
        if "claude" in clients:
            targets.append(Path.home() / ".claude" / "skills" / SKILL_NAME)

    for target in dict.fromkeys(targets):
        if target.exists():
            if not args.force:
                raise SystemExit(f"skill already exists, pass --force to replace: {target}")
            if args.dry_run:
                print(f"Would remove {target}")
            else:
                shutil.rmtree(target)
        print(f"Installing skill: {source} -> {target}")
        if not args.dry_run:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(source, target, ignore=shutil.ignore_patterns("*.meta"))
            _write_skill_metadata(target, upilot_dir)


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
            "Agent that should discover the installed Skill; repeat for multiple Agents. "
            "Defaults to all. Codex, Cursor, and OpenCode share .agents/skills; Claude uses .claude/skills."
        ),
    )
    parser.add_argument("--skill-only", action="store_true", help="Synchronize Skill content without modifying Packages/manifest.json")
    parser.add_argument("--write-codex-mcp", choices=["none", "project", "user"], default="none")
    parser.add_argument("--http-port", type=parse_port, default=8011, help="Public Streamable HTTP MCP port")
    parser.add_argument("--mcp-name", type=parse_mcp_name, default="upilot", help="Codex MCP registration name")
    parser.add_argument("--port", help=argparse.SUPPRESS)
    parser.add_argument("--force", action="store_true", help="Replace existing installed skill")
    parser.add_argument("--dry-run", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
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

    if args.setup_python and not args.no_python:
        venv = Path(args.venv).expanduser().resolve() if args.venv else upilot_dir / "upilotserver~" / ".venv"
        setup_python_env(upilot_dir, venv, args.python, args.dry_run)

    if args.unity_project and not args.skill_only:
        update_unity_manifest(args, upilot_dir)
    elif args.write_codex_mcp != "none":
        raise SystemExit("--write-codex-mcp requires --unity-project")

    if args.skill_only and args.install_skill == "none":
        raise SystemExit("--skill-only requires --install-skill repo, user, or both")
    install_skill(args, upilot_dir)
    write_codex_mcp(args)

    print("upilot install complete")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
