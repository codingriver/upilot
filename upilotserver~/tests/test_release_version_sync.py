from __future__ import annotations

import importlib.util
import json
from pathlib import Path

import pytest


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "deploy" / "sync_release_version.py"
SPEC = importlib.util.spec_from_file_location("sync_release_version", SCRIPT_PATH)
sync_release_version = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(sync_release_version)


def _write_release_files(repo_root: Path, package_version: str, pyproject_version: str) -> None:
    (repo_root / "upilotserver~").mkdir()
    (repo_root / "package.json").write_text(
        json.dumps({"name": "io.github.codingriver.upilot", "version": package_version}, indent=2) + "\n",
        encoding="utf-8",
    )
    (repo_root / "upilotserver~" / "pyproject.toml").write_text(
        "\n".join(
            [
                "[project]",
                'name = "upilot-mcp"',
                f'version = "{pyproject_version}"',
                "",
            ]
        ),
        encoding="utf-8",
    )


def test_sync_release_version_updates_upm_and_python_versions(tmp_path) -> None:
    _write_release_files(tmp_path, "0.3.11", "0.3.11")

    changes = sync_release_version.sync_release_version(tmp_path, "v0.3.12")

    package_data = json.loads((tmp_path / "package.json").read_text(encoding="utf-8"))
    pyproject_text = (tmp_path / "upilotserver~" / "pyproject.toml").read_text(encoding="utf-8")
    assert package_data["version"] == "0.3.12"
    assert 'version = "0.3.12"' in pyproject_text
    assert changes == [
        "package.json: 0.3.11 -> 0.3.12",
        "upilotserver~/pyproject.toml: 0.3.11 -> 0.3.12",
    ]


def test_sync_release_version_check_fails_on_mismatch(tmp_path) -> None:
    _write_release_files(tmp_path, "0.3.11", "0.3.11")

    with pytest.raises(RuntimeError, match="Release versions are not synchronized"):
        sync_release_version.sync_release_version(tmp_path, "0.3.12", check=True)


def test_sync_release_version_check_passes_when_already_synced(tmp_path) -> None:
    _write_release_files(tmp_path, "0.3.12", "0.3.12")

    assert sync_release_version.sync_release_version(tmp_path, "v0.3.12", check=True) == []


def test_normalize_version_rejects_non_semver() -> None:
    with pytest.raises(ValueError, match="Invalid release version"):
        sync_release_version.normalize_version("release-0.3")
