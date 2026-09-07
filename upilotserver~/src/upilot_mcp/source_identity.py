"""Content identity for checked source, including uncommitted implementation changes."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess


def source_identity(root: Path) -> dict:
    root = root.resolve()
    files = set()
    for directory in ("Editor", "Runtime", "Tests", "upilotserver~/src", "upilotserver~/tests",
                      "upilotserver~/scripts", "upilotserver~/deploy", "skills", ".github/workflows"):
        location = root / directory
        if location.exists():
            files.update(path for path in location.rglob("*") if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc")
    for name in ("package.json", "upilotserver~/pyproject.toml", "upilotserver~/uv.lock"):
        if (root / name).is_file():
            files.add(root / name)
    entries = []
    for path in sorted(files):
        content = path.read_bytes()
        if path.suffix.lower() in {".cs", ".py", ".json", ".asmdef", ".asmref", ".meta", ".md", ".yml", ".yaml", ".toml", ".lock", ".template", ".shader", ".hlsl"}:
            content = content.replace(b"\r\n", b"\n")
        entries.append([path.relative_to(root).as_posix(), hashlib.sha256(content).hexdigest()])
    try:
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
    except (OSError, subprocess.CalledProcessError):
        commit = ""
    return {
        "sourceCommit": commit,
        "sourceSha256": hashlib.sha256(json.dumps(entries, separators=(",", ":")).encode()).hexdigest(),
        "fileCount": len(entries),
        "scope": "package-code-tests-skills-workflows-v1",
        "textNormalization": "CRLF-to-LF",
    }
