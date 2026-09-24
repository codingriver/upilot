#!/usr/bin/env python3
"""Build a standalone UPilot MCP server exe and release manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
import re
import tomllib

SCRIPT_DIR = Path(__file__).resolve().parent
SERVER_ROOT = SCRIPT_DIR.parent
REPO_ROOT = SERVER_ROOT.parent
DIST = SERVER_ROOT / "dist"


def read_pyproject_version() -> str:
    data = tomllib.loads((SERVER_ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    version = data.get("project", {}).get("version")
    if not isinstance(version, str) or not version:
        raise ValueError("Missing Python project version")
    return version


def read_upm_version() -> str:
    data = json.loads((REPO_ROOT / "package.json").read_text(encoding="utf-8"))
    version = data.get("version")
    if not isinstance(version, str) or not version:
        raise ValueError("Missing UPM package version")
    return version


def run(cmd: list[str], cwd: Path | None = None) -> None:
    print("$ " + " ".join(cmd))
    subprocess.run(cmd, cwd=str(cwd or SERVER_ROOT), check=True)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def ensure_pyinstaller() -> None:
    try:
        subprocess.run(
            [sys.executable, "-m", "PyInstaller", "--version"],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except Exception:
        run([sys.executable, "-m", "pip", "install", "pyinstaller"])


def collect_skill_resources(skill_root: Path) -> list[Path]:
    required = (
        "template-manifest.json",
        "AGENTS.md.template",
        "SKILL.md.template",
        "SKILL.md",
        "agents/openai.yaml.template",
        "agents/openai.yaml",
        "references/automation-steps.md",
        "references/installation.md",
    )
    missing = [relative for relative in required if not (skill_root / relative).is_file()]
    if missing:
        raise FileNotFoundError("Missing UPilot Skill resources: " + ", ".join(missing))
    return sorted(
        (
            path for path in skill_root.rglob("*")
            if path.is_file()
            and path.name.lower() != ".upilot-install.json"
            and not any(
                part.lower() == "__pycache__"
                or part.lower().endswith((".meta", ".pyc", ".pyo"))
                for part in path.relative_to(skill_root).parts
            )
        ),
        key=lambda path: path.relative_to(skill_root).as_posix().lower(),
    )


def build_exe(version: str, channel: str, commit: str) -> Path:
    skill_root = REPO_ROOT / "skills" / "upilot-unity-mcp"
    bundled_resources = collect_skill_resources(skill_root)
    ensure_pyinstaller()
    DIST.mkdir(parents=True, exist_ok=True)
    name = f"upilot-mcp-server-{version}-win-x64"
    env = os.environ.copy()
    env["UPILOT_SERVER_VERSION"] = version
    env["UPILOT_BUILD_CHANNEL"] = channel
    env["UPILOT_BUILD_COMMIT"] = commit

    build_dir = SERVER_ROOT / "build" / "pyinstaller"
    spec_dir = SERVER_ROOT / "build" / "spec"
    for path in (build_dir, spec_dir):
        shutil.rmtree(path, ignore_errors=True)
        path.mkdir(parents=True, exist_ok=True)

    build_info = SERVER_ROOT / "src" / "upilot_mcp" / "upilot_build_info.json"
    build_info.write_text(
        json.dumps(
            {
                "server_version": version,
                "build_channel": channel,
                "build_commit": commit,
            },
            indent=2,
        ),
        encoding="utf-8",
    )

    cmd = [
        sys.executable,
        "-m",
        "PyInstaller",
        "--onefile",
        "--name",
        name,
        "--distpath",
        str(DIST),
        "--workpath",
        str(build_dir),
        "--specpath",
        str(spec_dir),
        "--paths",
        str(SERVER_ROOT / "src"),
        "--add-data",
        f"{build_info}{os.pathsep}upilot_mcp",
        "--copy-metadata",
        "mcp",
        "--collect-data",
        "mcp",
        "--collect-submodules",
        "mcp.server",
        "--collect-submodules",
        "mcp.shared",
        "--hidden-import",
        "mcp.types",
        "--collect-all",
        "websockets",
    ]
    for resource in bundled_resources:
        relative_parent = resource.parent.relative_to(skill_root).as_posix()
        destination = "skills/upilot-unity-mcp"
        if relative_parent != ".":
            destination += "/" + relative_parent
        cmd.extend(["--add-data", f"{resource}{os.pathsep}{destination}"])
    cmd.append(str(SERVER_ROOT / "run_upilot_mcp.py"))
    try:
        print("$ " + " ".join(cmd))
        subprocess.run(cmd, cwd=str(SERVER_ROOT), env=env, check=True)
        exe = DIST / f"{name}.exe"
        if not exe.is_file():
            raise FileNotFoundError(exe)
        return exe
    finally:
        build_info.unlink(missing_ok=True)


def write_manifest(
    exe: Path,
    *,
    version: str,
    upm_version: str,
    channel: str,
    commit: str,
    protocol_version: str,
    base_url: str,
) -> Path:
    digest = sha256(exe)
    sha_path = exe.with_suffix(exe.suffix + ".sha256")
    sha_path.write_text(f"{digest}  {exe.name}\n", encoding="utf-8")

    download_url = base_url.rstrip("/") + "/" + exe.name if base_url else exe.name
    manifest = {
        "upmVersion": upm_version,
        "serverVersion": version,
        "protocolVersion": protocol_version,
        "channel": channel,
        "commitSha": commit,
        "minCompatibleUpm": upm_version,
        "minCompatibleServer": version,
        "downloads": [
            {
                "platform": "windows",
                "architecture": "x64",
                "fileName": exe.name,
                "url": download_url,
                "sizeBytes": exe.stat().st_size,
                "sha256": digest,
            }
        ],
    }
    manifest_path = DIST / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest_path


def verify_release_manifest(manifest_path: Path) -> None:
    """Fail packaging before publishing if a managed-server checksum is absent or wrong."""
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    downloads = manifest.get("downloads")
    if not isinstance(downloads, list) or not downloads:
        raise ValueError("Release manifest must contain at least one download")

    for item in downloads:
        if not isinstance(item, dict):
            raise ValueError("Release manifest download entry must be an object")
        file_name = str(item.get("fileName") or "")
        digest = str(item.get("sha256") or "")
        if Path(file_name).name != file_name or not file_name:
            raise ValueError(f"Release manifest download has invalid fileName: {file_name!r}")
        if not re.fullmatch(r"[0-9a-f]{64}", digest, flags=re.IGNORECASE):
            raise ValueError(f"Release manifest download has invalid SHA256: {file_name!r}")
        artifact = manifest_path.parent / file_name
        if not artifact.is_file():
            raise FileNotFoundError(artifact)
        actual = sha256(artifact)
        if actual.lower() != digest.lower():
            raise ValueError(
                f"Release manifest SHA256 mismatch for {file_name}: expected {digest}, actual {actual}"
            )


def verify_local_release(version: str, upm_version: str, channel: str, commit: str,
                         protocol_version: str = "1") -> dict:
    """Verify the exact local release assets without repairing or rebuilding them."""
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-.+][0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid target release version")
    if read_pyproject_version() != version or read_upm_version() != upm_version or upm_version != version:
        raise ValueError("UPM, Python and requested release versions differ")
    name = f"upilot-mcp-server-{version}-win-x64.exe"
    dist = DIST.resolve()
    if dist.parent != SERVER_ROOT.resolve():
        raise ValueError("Release dist path resolves outside the server root")
    exe = DIST / name
    checksum = DIST / f"{name}.sha256"
    manifest_path = DIST / "manifest.json"
    for asset in (exe, checksum, manifest_path):
        if asset.resolve().parent != dist or not asset.is_file():
            raise ValueError(f"Missing or unsafe release asset: {asset.name}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected = {"serverVersion": version, "upmVersion": upm_version, "channel": channel,
                "commitSha": commit, "protocolVersion": protocol_version}
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ValueError(f"Release manifest {key} mismatch: {manifest.get(key)!r} != {value!r}")
    entries = manifest.get("downloads")
    if not isinstance(entries, list) or len(entries) != 1 or not isinstance(entries[0], dict):
        raise ValueError("Release manifest must contain exactly one target download")
    entry = entries[0]
    if (entry.get("fileName") != name or entry.get("platform") != "windows"
            or entry.get("architecture") != "x64"):
        raise ValueError("Release manifest target filename/platform/architecture mismatch")
    if entry.get("sizeBytes") != exe.stat().st_size:
        raise ValueError("Release EXE size differs from manifest")
    digest = sha256(exe)
    if not re.fullmatch(r"[0-9a-f]{64}", str(entry.get("sha256") or "")) or entry["sha256"] != digest:
        raise ValueError("Release EXE SHA256 differs from manifest")
    checksum_text = checksum.read_text(encoding="utf-8")
    if checksum_text != f"{digest}  {name}\n":
        raise ValueError("Release checksum file hash, filename or format mismatch")
    clean_env = {key: value for key, value in os.environ.items()
                 if key not in {"UPILOT_SERVER_VERSION", "UPILOT_BUILD_COMMIT", "UPILOT_BUILD_CHANNEL"}}
    try:
        result = subprocess.run([str(exe), "--version"], capture_output=True, text=True,
                                timeout=30, env=clean_env, check=True)
    except subprocess.TimeoutExpired as exc:
        raise ValueError("Release EXE --version timed out after 30 seconds") from exc
    except (OSError, subprocess.CalledProcessError) as exc:
        raise ValueError(f"Release EXE --version failed: {exc}") from exc
    output = result.stdout.strip()
    match = re.fullmatch(r"upilot-mcp (\S+) channel=(\S+) commit=(\S+) protocol=(\S+)", output)
    if match is None or match.groups() != (version, channel, commit or "unknown", protocol_version):
        raise ValueError(f"Release EXE --version identity mismatch: {output!r}")
    return {"passed": True, "version": version, "commit": commit, "sha256": digest,
            "sizeBytes": exe.stat().st_size, "exe": name, "versionOutput": output}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=read_pyproject_version())
    parser.add_argument("--upm-version", default=read_upm_version())
    parser.add_argument("--channel", default=os.getenv("UPILOT_BUILD_CHANNEL", "release"))
    parser.add_argument("--commit", default=os.getenv("GITHUB_SHA", ""))
    parser.add_argument("--protocol-version", default="1")
    parser.add_argument("--base-url", default="")
    parser.add_argument("--verify-only", action="store_true", help="Only inspect existing local release assets")
    args = parser.parse_args()
    report_path = REPO_ROOT / "artifacts" / "reliability-quality" / "local-release-verification.json"
    try:
        if not args.verify_only:
            exe = build_exe(args.version, args.channel, args.commit)
            manifest = write_manifest(
                exe, version=args.version, upm_version=args.upm_version, channel=args.channel,
                commit=args.commit, protocol_version=args.protocol_version, base_url=args.base_url,
            )
        result = verify_local_release(args.version, args.upm_version, args.channel,
                                      args.commit, args.protocol_version)
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        result = {"passed": False, "version": args.version, "commit": args.commit, "error": str(exc)}
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if result["passed"]:
        (report_path.parent / "exe-version.log").write_text(result["versionOutput"] + "\n", encoding="utf-8")
    print(json.dumps(result))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
