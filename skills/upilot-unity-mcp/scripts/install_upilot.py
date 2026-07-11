#!/usr/bin/env python3
"""Install upilot into a Unity project for agent-driven setup."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


REPO_URL = "https://github.com/codingriver/upilot.git"
UPM_PACKAGE = "io.github.codingriver.upilot"
DEFAULT_UPM_REF = "v0.1.1"
SKILL_NAME = "upilot-unity-mcp"

UIFLOW_DEPS = {
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


def save_manifest(path: Path, data: dict, dry_run: bool) -> None:
    text = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    if dry_run:
        print(f"Would write {path}")
        print(text)
        return
    path.write_text(text, encoding="utf-8")


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
    data = load_manifest(manifest_path)
    deps = data["dependencies"]

    if args.use_local_upm:
        value = "file:" + upilot_dir.as_posix()
    else:
        value = f"{args.repo_url}#{args.upm_ref}"
    deps[UPM_PACKAGE] = value

    if args.enable_uiflow:
        uiflow_deps = dict(UIFLOW_DEPS)
        uiflow_deps.update(dict(args.upm_dep or []))
        deps.update(uiflow_deps)
        testables = data.setdefault("testables", [])
        if isinstance(testables, list) and "com.unity.inputsystem" not in testables:
            testables.append("com.unity.inputsystem")

    save_manifest(manifest_path, data, args.dry_run)
    print(f"Unity manifest configured: {manifest_path}")


def install_skill(args: argparse.Namespace, upilot_dir: Path) -> None:
    if args.install_skill == "none":
        return
    source = upilot_dir / "skills" / SKILL_NAME
    if not source.is_dir():
        raise SystemExit(f"source skill not found: {source}")

    targets: list[Path] = []
    unity_project = Path(args.unity_project).expanduser().resolve() if args.unity_project else Path.cwd()
    if args.install_skill in {"repo", "both"}:
        targets.append(unity_project / ".agents" / "skills" / SKILL_NAME)
    if args.install_skill in {"user", "both"}:
        targets.append(Path.home() / ".agents" / "skills" / SKILL_NAME)

    for target in targets:
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


def write_codex_mcp(args: argparse.Namespace, upilot_dir: Path, venv_python: Path | None) -> None:
    if args.write_codex_mcp == "none":
        return
    if venv_python is None:
        raise SystemExit("--write-codex-mcp requires Python environment setup")

    unity_project = Path(args.unity_project).expanduser().resolve()
    if args.write_codex_mcp == "project":
        config_path = unity_project / ".codex" / "config.toml"
    else:
        config_path = Path.home() / ".codex" / "config.toml"

    server_script = upilot_dir / "upilotserver~" / "run_upilot_mcp.py"
    existing = config_path.read_text(encoding="utf-8") if config_path.exists() else ""
    existing = remove_toml_table(existing, {"mcp_servers.upilot", "mcp_servers.upilot.env"})
    block = (
        "\n[mcp_servers.upilot]\n"
        f"command = {toml_string(str(venv_python))}\n"
        f"args = [{toml_string(str(server_script))}, \"--transport\", \"stdio\", \"--port\", {toml_string(args.port)}]\n"
        "startup_timeout_sec = 10\n"
        "tool_timeout_sec = 60\n"
        "\n[mcp_servers.upilot.env]\n"
        "PYTHONUTF8 = \"1\"\n"
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
    parser = argparse.ArgumentParser(description="Install upilot for a Unity project.")
    parser.add_argument("--unity-project", help="Unity project root containing Packages/manifest.json")
    parser.add_argument("--repo-url", default=REPO_URL)
    parser.add_argument("--upm-ref", default=DEFAULT_UPM_REF)
    parser.add_argument("--upilot-dir", default=str(repo_root_from_script()))
    parser.add_argument("--clone-to", help="Clone upilot here if it is not present")
    parser.add_argument("--use-local-upm", action="store_true", help="Use file:<upilot-dir> instead of Git URL in Unity manifest")
    parser.add_argument("--enable-uiflow", action="store_true", help="Add optional UIFlow Unity package dependencies")
    parser.add_argument("--upm-dep", action="append", type=parse_dep, help="Override/add a Unity package dependency as name=version")
    parser.add_argument("--no-python", action="store_true", help="Skip Python venv creation and server install")
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--venv", help="Python venv path; default is upilotserver~/.venv")
    parser.add_argument("--install-skill", choices=["none", "repo", "user", "both"], default="repo")
    parser.add_argument("--write-codex-mcp", choices=["none", "project", "user"], default="none")
    parser.add_argument("--port", default="8765", help="Unity bridge WebSocket port for stdio MCP config")
    parser.add_argument("--force", action="store_true", help="Replace existing installed skill")
    parser.add_argument("--dry-run", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    upilot_dir = ensure_upilot_repo(args)
    if not upilot_dir.exists() and not args.dry_run:
        raise SystemExit(f"upilot directory does not exist: {upilot_dir}")

    venv_python: Path | None = None
    if not args.no_python:
        venv = Path(args.venv).expanduser().resolve() if args.venv else upilot_dir / "upilotserver~" / ".venv"
        venv_python = setup_python_env(upilot_dir, venv, args.python, args.dry_run)

    if args.unity_project:
        update_unity_manifest(args, upilot_dir)
    elif args.write_codex_mcp != "none":
        raise SystemExit("--write-codex-mcp requires --unity-project")

    install_skill(args, upilot_dir)
    write_codex_mcp(args, upilot_dir, venv_python)

    print("upilot install complete")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
