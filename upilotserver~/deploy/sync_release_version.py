#!/usr/bin/env python3
"""Synchronize release version fields from a tag/input version."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

SEMVER_RE = re.compile(r"^v?([0-9]+\.[0-9]+\.[0-9]+(?:[-.+][0-9A-Za-z.-]+)?)$")


def normalize_version(raw: str) -> str:
    value = (raw or "").strip()
    match = SEMVER_RE.fullmatch(value)
    if not match:
        raise ValueError(f"Invalid release version: {raw!r}")
    return match.group(1)


def _replace_pyproject_version(text: str, version: str) -> str:
    project = re.search(r'(?m)^\[project\]\s*$', text)
    if project is None:
        raise ValueError("Could not find [project] in upilotserver~/pyproject.toml")
    end = re.search(r'(?m)^\[', text[project.end():])
    section_end = project.end() + end.start() if end else len(text)
    section = text[project.end():section_end]
    pattern = r'(?m)^(version\s*=\s*["\'])[^"\'\r\n]+(["\'])'
    if len(list(re.finditer(pattern, section))) != 1:
        raise ValueError("Could not find a single project version in upilotserver~/pyproject.toml")
    section = re.sub(pattern, lambda m: m.group(1) + version + m.group(2), section)
    return text[:project.end()] + section + text[section_end:]


def sync_release_version(repo_root: Path, raw_version: str, *, check: bool = False) -> list[str]:
    version = normalize_version(raw_version)
    package_path = repo_root / "package.json"
    pyproject_path = repo_root / "upilotserver~" / "pyproject.toml"

    package_text = package_path.read_bytes().decode("utf-8")
    package_data = json.loads(package_text)
    if not isinstance(package_data, dict) or not isinstance(package_data.get("version"), str):
        raise ValueError("Missing package.json version")
    old_package_version = str(package_data.get("version") or "")
    matches = list(re.finditer(r'(?m)^(\s*"version"\s*:\s*")[^"\r\n]+(")', package_text))
    if len(matches) != 1:
        raise ValueError("Could not locate a unique package.json version field")
    match = matches[0]
    package_updated = package_text[:match.start()] + match.group(1) + version + match.group(2) + package_text[match.end():]
    updated_package = json.loads(package_updated)
    if {**updated_package, "version": old_package_version} != package_data:
        raise ValueError("package.json version replacement changed another field")

    pyproject_text = pyproject_path.read_bytes().decode("utf-8")
    old_pyproject_version_match = re.search(r'(?m)^version\s*=\s*["\']([^"\']+)["\']', pyproject_text)
    old_pyproject_version = old_pyproject_version_match.group(1) if old_pyproject_version_match else ""
    pyproject_updated = _replace_pyproject_version(pyproject_text, version)

    changes = []
    if old_package_version != version:
        changes.append(f"package.json: {old_package_version} -> {version}")
    if pyproject_updated != pyproject_text:
        changes.append(f"upilotserver~/pyproject.toml: {old_pyproject_version} -> {version}")

    if check:
        if changes:
            raise RuntimeError("Release versions are not synchronized:\n" + "\n".join(changes))
        return []

    if package_updated != package_text:
        package_path.write_bytes(package_updated.encode("utf-8"))
    if pyproject_updated != pyproject_text:
        pyproject_path.write_bytes(pyproject_updated.encode("utf-8"))
    return changes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("version", help="Release version, with or without a leading v")
    parser.add_argument("--repo-root", default=Path(__file__).resolve().parents[2])
    parser.add_argument("--check", action="store_true", help="Fail if files do not already match")
    args = parser.parse_args()

    try:
        changes = sync_release_version(Path(args.repo_root).resolve(), args.version, check=args.check)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    version = normalize_version(args.version)
    if changes:
        print("Synchronized release version " + version)
        for change in changes:
            print("- " + change)
    else:
        print("Release version already synchronized: " + version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
