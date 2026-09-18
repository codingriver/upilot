"""Content identity for checked source, including uncommitted implementation changes."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess


SOURCE_DIRS = ("Editor", "Runtime", "Tests", "upilotserver~/src", "upilotserver~/tests",
               "upilotserver~/scripts", "upilotserver~/deploy", "skills", ".github/workflows")
SOURCE_FILES = ("package.json", "upilotserver~/pyproject.toml", "upilotserver~/uv.lock")
IMPORT_INPUT_SUFFIXES = {".asmdef", ".asmref", ".cs", ".meta", ".rsp"}


def source_identity(root: Path, revision: str | None = None) -> dict:
    identity, _ = source_identity_with_files(root, revision)
    return identity


def source_identity_with_files(root: Path, revision: str | None = None) -> tuple[dict, dict[str, str]]:
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
    }, dict(entries)


def acceptance_import_inputs(source_root: Path, project: Path) -> dict:
    """Hash importer-sensitive project inputs without treating equal hashes as import proof."""
    source_root = source_root.resolve()
    project = project.resolve()
    _, package_files = source_identity_with_files(source_root)
    inputs = {"package:" + path: digest for path, digest in package_files.items()}
    for folder in (project / "Assets", project / "Packages"):
        if not folder.is_dir():
            continue
        for path in folder.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in IMPORT_INPUT_SUFFIXES:
                continue
            relative = path.relative_to(project).as_posix()
            content = path.read_bytes()
            if path.suffix.lower() in {".asmdef", ".asmref", ".cs", ".meta", ".rsp"}:
                content = content.replace(b"\r\n", b"\n")
            inputs["project:" + relative] = hashlib.sha256(content).hexdigest()
    return {
        "schemaVersion": 1,
        "projectPath": str(project),
        "inputCount": len(inputs),
        "inputs": inputs,
    }


def diff_acceptance_import_inputs(before: dict, after: dict, limit: int = 200) -> dict:
    before_inputs = before.get("inputs") if isinstance(before.get("inputs"), dict) else {}
    after_inputs = after.get("inputs") if isinstance(after.get("inputs"), dict) else {}
    changes = []
    for path in sorted(set(before_inputs) | set(after_inputs)):
        before_hash, after_hash = before_inputs.get(path, ""), after_inputs.get(path, "")
        if before_hash == after_hash:
            continue
        changes.append({"path": path, "beforeSha256": before_hash, "afterSha256": after_hash})
    return {"changed": changes[:limit], "changeCount": len(changes), "changesTruncated": len(changes) > limit}
