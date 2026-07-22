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

SCRIPT_DIR = Path(__file__).resolve().parent
SERVER_ROOT = SCRIPT_DIR.parent
REPO_ROOT = SERVER_ROOT.parent
DIST = SERVER_ROOT / "dist"


def read_pyproject_version() -> str:
    for raw in (SERVER_ROOT / "pyproject.toml").read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if line.startswith("version") and "=" in line:
            return line.split("=", 1)[1].strip().strip('"').strip("'")
    return "0.0.0"


def read_upm_version() -> str:
    data = json.loads((REPO_ROOT / "package.json").read_text(encoding="utf-8"))
    return str(data.get("version") or "0.0.0")


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


def build_exe(version: str, channel: str, commit: str) -> Path:
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
        "--clean",
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
        "--collect-all",
        "mcp",
        "--collect-all",
        "websockets",
        str(SERVER_ROOT / "run_upilot_mcp.py"),
    ]
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
    manifest_path = DIST / "upilot-release-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=read_pyproject_version())
    parser.add_argument("--upm-version", default=read_upm_version())
    parser.add_argument("--channel", default=os.getenv("UPILOT_BUILD_CHANNEL", "release"))
    parser.add_argument("--commit", default=os.getenv("GITHUB_SHA", ""))
    parser.add_argument("--protocol-version", default="1")
    parser.add_argument("--base-url", default="")
    args = parser.parse_args()

    exe = build_exe(args.version, args.channel, args.commit)
    manifest = write_manifest(
        exe,
        version=args.version,
        upm_version=args.upm_version,
        channel=args.channel,
        commit=args.commit,
        protocol_version=args.protocol_version,
        base_url=args.base_url,
    )
    print(f"exe={exe}")
    print(f"sha256={exe}.sha256")
    print(f"manifest={manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
