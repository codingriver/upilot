from datetime import datetime, timedelta
import json
import logging
import os
from pathlib import Path

import pytest
from upilot_mcp import config
from upilot_mcp.daily_logs import DailyLogHandler, DailyLogWriter


@pytest.mark.parametrize("value,expected", [(1, 1), (365, 365), (3, 3), (0, 3), (-1, 3),
                                           (366, 3), (None, 3), ("8", 3), (True, 3), (2.5, 3)])
def test_retention_config_defaults_and_boundaries(value, expected):
    assert config._config_from_raw({"logging": {"retentionDays": value}}).log_retention_days == expected
    assert config._config_from_raw({}).log_retention_days == 3
    assert config._config_from_raw({"logging": None}).log_retention_days == 3


def test_all_writers_share_hot_reloaded_project_days(monkeypatch, tmp_path):
    path = tmp_path / ".upilot" / "config.json"
    path.parent.mkdir()
    monkeypatch.setenv("UPILOT_CONFIG", str(path))
    # Isolate mutable global config/cache from other tests.
    import copy
    for name in ("CONFIG", "_CONFIG_LAST_DISK_HASH", "_CONFIG_LAST_LOADED_AT", "_CONFIG_LOAD_ERROR",
                 "_CONFIG_RAW_CACHE", "_CONFIG_RAW_CACHE_KEY"):
        monkeypatch.setattr(config, name, copy.deepcopy(getattr(config, name)))
    import upilot_mcp.daily_logs as daily
    monkeypatch.setattr(daily, "CONFIG", config.CONFIG)
    today = datetime.now().astimezone()
    writers = [DailyLogWriter(tmp_path / name / (name + ".log"), clock=lambda: today)
               for name in ("mcp-server", "csharp_eval", "unity_reflection_call", "csharp_object_dump")]
    path.write_text('{"logging":{"retentionDays":3}}')
    for writer in writers:
        writer.append("first\n")
        writer.path_for(today.date() - timedelta(days=1)).write_text("old")
    path.write_text('{"logging":{"retentionDays":1}}')
    assert not config.refresh_config_if_changed()["restartRequired"]
    for writer in writers:
        writer.append("second\n")
        assert not writer.path_for(today.date() - timedelta(days=1)).exists()
        assert writer.path_for(today.date()).read_text() == "first\nsecond\n"


def test_cleanup_only_owned_expired_regular_files_and_rollover(tmp_path):
    now = [datetime(2026, 10, 15, 23, 59).astimezone()]
    days = [3]
    writer = DailyLogWriter(tmp_path / "mcp-server.log", clock=lambda: now[0], retention=lambda: days[0])
    for day in (11, 12, 13, 14, 15, 16):
        writer.path_for(datetime(2026, 10, day).date()).write_text("prior\n")
    protected = ["mcp-server.log", "mcp-server.log.1", "mcp-server-2026-02-30.log",
                 "mcp-server-2026-10-01.log.1", "other-2026-10-01.log", "report.json"]
    for name in protected:
        (tmp_path / name).write_text("keep")
    directory = tmp_path / "mcp-server-2026-01-01.log"
    directory.mkdir()
    assert writer.append("中文🙂\n")
    assert not writer.path_for(datetime(2026, 10, 12).date()).exists()
    for day in (13, 14, 15, 16):
        assert writer.path_for(datetime(2026, 10, day).date()).exists()
    late = tmp_path / "mcp-server-2026-10-01.log"
    late.write_text("late")
    writer.append("same day\n")
    assert late.exists()  # No timer or cleanup on every write.
    now[0] += timedelta(minutes=2)
    writer.append("midnight\n")
    assert not late.exists()
    assert writer.path_for(now[0].date()).read_text().endswith("midnight\n")
    assert all((tmp_path / name).read_text() == "keep" for name in protected)
    assert directory.is_dir()


def test_new_writer_appends_after_restart_and_preserves_large_partial_tail(tmp_path):
    now = datetime.now().astimezone()
    path = tmp_path / "mcp-server.log"
    writer = DailyLogWriter(path, clock=lambda: now, retention=lambda: 3)
    writer.path_for(now.date()).write_text("partial", encoding="utf-8")
    payload = "中" * 4_000_000 + "\n🙂\n"
    assert writer.append(payload)
    assert DailyLogWriter(path, clock=lambda: now, retention=lambda: 3).append("after reload\n")
    assert writer.path_for(now.date()).read_text(encoding="utf-8") == "partial\n" + payload + "after reload\n"
    assert not path.exists()


def test_failure_outputs_error_and_trace_without_replay(monkeypatch, tmp_path, capsys):
    writer = DailyLogWriter(tmp_path / "mcp-server.log", retention=lambda: 3)
    expired = tmp_path / "mcp-server-2020-01-01.log"
    expired.write_text("old")
    unlink = Path.unlink
    def fail_unlink(path, *args, **kwargs):
        if path == expired:
            raise PermissionError("cleanup denied")
        return unlink(path, *args, **kwargs)
    monkeypatch.setattr(Path, "unlink", fail_unlink)
    assert writer.append("business continues\n")
    failure = capsys.readouterr().err
    assert "ERROR log cleanup" in failure and "PermissionError" in failure and str(expired) in failure
    target = writer.path_for(datetime.now().astimezone().date())
    target.unlink()
    target.mkdir()
    assert not writer.append("lost\n")
    assert "ERROR log write" in capsys.readouterr().err
    target.rmdir()
    assert writer.append("later\n")
    assert target.read_text() == "later\n"


def test_symlink_files_and_parent_are_never_followed(tmp_path):
    target = tmp_path / "protected.txt"
    target.write_text("keep")
    link = tmp_path / "mcp-server-2020-01-01.log"
    try:
        os.symlink(target, link)
    except OSError:
        pytest.skip("Symlink creation unavailable in this environment")
    writer = DailyLogWriter(tmp_path / "mcp-server.log", retention=lambda: 3)
    assert writer.append("current\n")
    assert link.is_symlink() and target.read_text() == "keep"
    directory = tmp_path / "redirect"
    os.symlink(tmp_path, directory, target_is_directory=True)
    assert not DailyLogWriter(directory / "other.log").append("forbidden\n")
    assert not (tmp_path / f"other-{datetime.now():%Y-%m-%d}.log").exists()


def test_server_handler_routes_daily_and_never_creates_legacy(tmp_path):
    path = tmp_path / "mcp-server.log"
    handler = DailyLogHandler(path)
    handler.setFormatter(logging.Formatter("%(levelname)s %(message)s"))
    record = logging.LogRecord("upilot.test", logging.ERROR, __file__, 1, "full %s", ("中" * 70000,), None)
    handler.handle(record)
    assert not path.exists()
    assert handler.writer.path_for(datetime.now().astimezone().date()).read_text(encoding="utf-8") == "ERROR full " + "中" * 70000 + "\n"


def test_server_entry_logging_in_real_process_preserves_identity_path(tmp_path):
    import subprocess
    import sys
    base = tmp_path / "Logs" / "UPilot" / "mcp-server.log"
    base.parent.mkdir(parents=True)
    old_base = tmp_path / "log" / "mcp-server.log"
    old_base.parent.mkdir()
    old_base.write_text("legacy directory", encoding="utf-8")
    base.write_text("legacy", encoding="utf-8")
    cfg = tmp_path / ".upilot" / "config.json"
    cfg.parent.mkdir()
    cfg.write_text('{"logging":{"retentionDays":1}}')
    expired = base.with_name("mcp-server-2020-01-01.log")
    expired.write_text("expired")
    env = dict(os.environ, UPILOT_CONFIG=str(cfg), UPILOT_LOG_FILE=str(base), UPILOT_LOG_LEVEL="INFO")
    result = subprocess.run([sys.executable, "-c",
        "from upilot_mcp.mcp_main import _setup_logging; import logging, os; "
        "_setup_logging(); logging.getLogger('upilot.test').error('daily-process-probe'); "
        "print(os.environ['UPILOT_LOG_FILE'])"], env=env, capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stderr
    assert result.stdout.strip() == str(base)
    assert "daily-process-probe" in result.stderr
    assert "daily-process-probe" in base.with_name(f"mcp-server-{datetime.now():%Y-%m-%d}.log").read_text()
    assert base.read_text() == "legacy" and not expired.exists()
    assert old_base.read_text(encoding="utf-8") == "legacy directory"
    assert list(old_base.parent.iterdir()) == [old_base]
