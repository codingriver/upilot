"""Content identity for checked source, including uncommitted implementation changes."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess


SOURCE_DIRS = ("Editor", "Runtime", "Tests", "upilotserver~/src", "upilotserver~/tests",
               "upilotserver~/scripts", "upilotserver~/deploy", "skills", ".github/workflows")
SOURCE_FILES = ("package.json", "upilotserver~/pyproject.toml", "upilotserver~/uv.lock")


def source_identity(root: Path, revision: str | None = None) -> dict:
    root = root.resolve()
    files = set()
    for directory in SOURCE_DIRS:
        location = root / directory
        if location.exists():
            files.update(path for path in location.rglob("*") if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc")
    for name in SOURCE_FILES:
        if (root / name).is_file():
            files.add(root / name)
    entries = []
    if revision:
        revision = subprocess.check_output(["git", "rev-parse", "--verify", revision + "^{commit}"], cwd=root, text=True).strip()
        names = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", "-z", revision], cwd=root).decode().split("\0")
        files = {root / name for name in names if name in SOURCE_FILES or any(name.startswith(d + "/") for d in SOURCE_DIRS)}
        files = {p for p in files if "__pycache__" not in p.parts and p.suffix != ".pyc"}
    # Explicit POSIX lexical order is identical on Windows and Linux.
    for path in sorted(files, key=lambda p: p.relative_to(root).as_posix()):
        content = (subprocess.check_output(["git", "show", revision + ":" + path.relative_to(root).as_posix()], cwd=root)
                   if revision else path.read_bytes())
        if path.suffix.lower() in {".cs", ".py", ".json", ".asmdef", ".asmref", ".meta", ".md", ".yml", ".yaml", ".toml", ".lock", ".template", ".shader", ".hlsl"}:
            content = content.replace(b"\r\n", b"\n")
        entries.append([path.relative_to(root).as_posix(), hashlib.sha256(content).hexdigest()])
    try:
        commit = revision or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
    except (OSError, subprocess.CalledProcessError):
        commit = ""
    return {
        "sourceCommit": commit,
        "sourceSha256": hashlib.sha256(json.dumps(entries, separators=(",", ":")).encode()).hexdigest(),
        "fileCount": len(entries),
        "scope": "package-code-tests-skills-workflows-v2",
        "textNormalization": "CRLF-to-LF",
    }
