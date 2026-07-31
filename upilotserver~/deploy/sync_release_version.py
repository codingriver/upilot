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
    updated, count = re.subn(
        r'(?m)^version\s*=\s*["\'][^"\']+["\']\s*$',
        f'version = "{version}"',
        text,
        count=1,
    )
    if count != 1:
        raise ValueError("Could not find a single project version in upilotserver~/pyproject.toml")
    return updated


def sync_release_version(repo_root: Path, raw_version: str, *, check: bool = False) -> list[str]:
    version = normalize_version(raw_version)
    package_path = repo_root / "package.json"
    pyproject_path = repo_root / "upilotserver~" / "pyproject.toml"

    package_data = json.loads(package_path.read_text(encoding="utf-8"))
    old_package_version = str(package_data.get("version") or "")
    package_data["version"] = version
    package_text = json.dumps(package_data, indent=2, ensure_ascii=False) + "\n"

    pyproject_text = pyproject_path.read_text(encoding="utf-8")
    old_pyproject_version_match = re.search(
        r'(?m)^version\s*=\s*["\']([^"\']+)["\']\s*$',
        pyproject_text,
    )
    old_pyproject_version = old_pyproject_version_match.group(1) if old_pyproject_version_match else ""
    pyproject_text = _replace_pyproject_version(pyproject_text, version)

    changes = []
    if old_package_version != version:
        changes.append(f"package.json: {old_package_version} -> {version}")
    if old_pyproject_version != version:
        changes.append(f"upilotserver~/pyproject.toml: {old_pyproject_version} -> {version}")

    if check:
        if changes:
            raise RuntimeError("Release versions are not synchronized:\n" + "\n".join(changes))
        return []

    package_path.write_text(package_text, encoding="utf-8", newline="\n")
    pyproject_path.write_text(pyproject_text, encoding="utf-8", newline="\n")
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
