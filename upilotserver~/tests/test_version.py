from __future__ import annotations

from importlib import metadata

from upilot_mcp import version


def _clear_version_environment(monkeypatch) -> None:
    monkeypatch.delenv("UPILOT_SERVER_VERSION", raising=False)
    monkeypatch.setattr(version, "_build_info", lambda: {})


def test_server_version_prefers_environment_override(monkeypatch) -> None:
    monkeypatch.setenv("UPILOT_SERVER_VERSION", "0.3.3-override")
    monkeypatch.setattr(version, "_build_info", lambda: {"server_version": "0.3.3-build"})
    monkeypatch.setattr(version, "_read_pyproject_version", lambda: "0.3.3-source")

    assert version.server_version() == "0.3.3-override"


def test_server_version_prefers_build_info_over_source(monkeypatch) -> None:
    _clear_version_environment(monkeypatch)
    monkeypatch.setattr(version, "_build_info", lambda: {"server_version": "0.3.3-build"})
    monkeypatch.setattr(version, "_read_pyproject_version", lambda: "0.3.3-source")

    assert version.server_version() == "0.3.3-build"


def test_server_version_prefers_current_source_over_installed_distribution(monkeypatch) -> None:
    _clear_version_environment(monkeypatch)
    monkeypatch.setattr(version, "_read_pyproject_version", lambda: "0.3.3")
    monkeypatch.setattr(version.metadata, "version", lambda _: "0.3.2")

    assert version.server_version() == "0.3.3"


def test_server_version_falls_back_to_installed_distribution(monkeypatch) -> None:
    _clear_version_environment(monkeypatch)
    monkeypatch.setattr(version, "_read_pyproject_version", lambda: "")
    monkeypatch.setattr(version.metadata, "version", lambda _: "0.3.2")

    assert version.server_version() == "0.3.2"


def test_server_version_uses_zero_when_no_source_or_distribution_exists(monkeypatch) -> None:
    _clear_version_environment(monkeypatch)
    monkeypatch.setattr(version, "_read_pyproject_version", lambda: "")

    def missing_distribution(_: str) -> str:
        raise metadata.PackageNotFoundError

    monkeypatch.setattr(version.metadata, "version", missing_distribution)

    assert version.server_version() == "0.0.0"
