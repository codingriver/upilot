from __future__ import annotations

import copy
import json
from pathlib import Path

import pytest

import upilot_mcp.config as config


@pytest.fixture
def source(tmp_path, monkeypatch):
    path = tmp_path / ".upilot" / "config.json"
    path.parent.mkdir()
    values = {name: getattr(config.CONFIG, name) for name in config.CONFIG.__slots__}
    globals_ = {name: copy.deepcopy(getattr(config, name)) for name in (
        "_CONFIG_LAST_DISK_HASH", "_CONFIG_LAST_LOADED_AT", "_CONFIG_LOAD_ERROR",
        "_CONFIG_RAW_CACHE", "_CONFIG_RAW_CACHE_KEY")}
    monkeypatch.setenv("UPILOT_CONFIG", str(path))
    for name in ("UPILOT_HTTP_HOST", "UPILOT_HTTP_PORT", "UPILOT_HOST", "UPILOT_PORT"):
        monkeypatch.delenv(name, raising=False)
    config._CONFIG_LAST_DISK_HASH = ""
    config._CONFIG_RAW_CACHE_KEY = None
    path.write_text(json.dumps({"safety": {"writeAccessApproved": True}}), encoding="utf-8")
    try:
        yield path
    finally:
        for name, value in values.items():
            setattr(config.CONFIG, name, value)
        for name, value in globals_.items():
            setattr(config, name, value)


def test_refresh_reads_once_and_unchanged_content_reuses_parse(source, monkeypatch):
    reads, parses = [], []
    read = Path.read_bytes
    parse = config.json.loads

    def tracked_read(path):
        if path == source:
            reads.append(path)
        return read(path)

    def tracked_parse(content, *args, **kwargs):
        parses.append(content)
        return parse(content, *args, **kwargs)

    monkeypatch.setattr(Path, "read_bytes", tracked_read)
    monkeypatch.setattr(config.json, "loads", tracked_parse)
    config.refresh_config_if_changed(force=True)
    config.refresh_config_if_changed()
    assert len(reads) == 2 and len(parses) == 1
    config.refresh_config_if_changed(force=True)
    assert len(reads) == 3 and len(parses) == 2


def test_revoked_permission_is_effective_on_next_refresh(source):
    assert config.refresh_config_if_changed(force=True)["writeAccessApproved"]
    source.write_text('{"safety":{"writeAccessApproved":false}}', encoding="utf-8")
    assert not config.refresh_config_if_changed()["writeAccessApproved"]


def test_unchanged_file_still_observes_environment_endpoint_override(source, monkeypatch):
    config.refresh_config_if_changed(force=True)
    current = config.CONFIG.http_port
    monkeypatch.setenv("UPILOT_HTTP_PORT", str(current + 17))
    result = config.refresh_config_if_changed()
    assert result["restartRequired"] and "http_port" in result["restartFields"]
    assert config.CONFIG.http_port == current


@pytest.mark.parametrize("content", [b"{", b"[]", b"null", b"\xff"])
def test_invalid_content_keeps_existing_safe_default_behavior(source, content):
    config.refresh_config_if_changed(force=True)
    source.write_bytes(content)
    result = config.refresh_config_if_changed()
    assert not result["writeAccessApproved"]
    assert not result["flowEnabled"] and result["configLoadError"] == ""


def test_missing_file_revokes_permission(source):
    config.refresh_config_if_changed(force=True)
    source.unlink()
    result = config.refresh_config_if_changed()
    assert result["diskConfigHash"] == "missing" and not result["writeAccessApproved"]


def test_unreadable_file_uses_defaults_without_second_read(source, monkeypatch):
    config.refresh_config_if_changed(force=True)
    read = Path.read_bytes
    reads = []

    def fail_read(path):
        if path == source:
            reads.append(path)
            raise OSError("fixture read failure")
        return read(path)

    monkeypatch.setattr(Path, "read_bytes", fail_read)
    result = config.refresh_config_if_changed()
    assert len(reads) == 1
    assert result["diskConfigHash"] == "unreadable" and not result["writeAccessApproved"]


def test_conversion_error_does_not_mark_new_content_as_loaded(source):
    config.refresh_config_if_changed(force=True)
    loaded = config._CONFIG_LAST_DISK_HASH
    source.write_text('{"mcp":{"httpPort":"bad"},"safety":{"writeAccessApproved":false}}', encoding="utf-8")
    result = config.refresh_config_if_changed()
    assert result["configLoadError"] and config._CONFIG_LAST_DISK_HASH == loaded
    assert result["writeAccessApproved"]  # Preserve the existing conversion-error contract.
    source.write_text('{"safety":{"writeAccessApproved":false}}', encoding="utf-8")
    assert not config.refresh_config_if_changed()["writeAccessApproved"]


def test_utf8_bom_and_endpoint_changes_remain_supported(source):
    source.write_bytes(b'\xef\xbb\xbf' + json.dumps({"mcp": {"httpPort": 19991}, "safety": {"writeAccessApproved": True}}).encode())
    result = config.refresh_config_if_changed(force=True)
    assert result["writeAccessApproved"] and result["restartRequired"]
    assert "http_port" in result["restartFields"]
