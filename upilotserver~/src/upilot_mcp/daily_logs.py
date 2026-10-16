"""Project-wide calendar-day retention for append-only diagnostic logs."""
from __future__ import annotations

import logging
import atexit
import os
import stat
import sys
import threading
from datetime import date, datetime, timedelta
from pathlib import Path

from .config import CONFIG, refresh_config_if_changed
from .diagnostic_logs import RepeatDiagnosticFilter


def _stderr_diagnostic(record):
    try:
        print(logging.Formatter("[UPilot] %(levelname)s %(message)s").format(record), file=sys.stderr)
    except Exception:
        pass


_file_diagnostics = RepeatDiagnosticFilter(emit=_stderr_diagnostic)
atexit.register(_file_diagnostics.flush)


def report_log_failure(operation: str, path: Path | str, error: BaseException) -> None:
    # Never route a failed file sink back through logging's handlers.
    try:
        record = logging.LogRecord("upilot.logfile", logging.ERROR, __file__, 0,
            f"log {operation} path={path}: {error}", (), (type(error), error, error.__traceback__))
        record.diagnostic_scope = "file|" + str(path)
        if _file_diagnostics.filter(record):
            _stderr_diagnostic(record)
    except Exception:
        pass  # A broken stderr must not escape into business execution.


def require_safe_path(path: Path) -> None:
    for current in (path, *path.parents):
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise OSError(f"Log path contains a symbolic link or junction: {current}")


def retention_days() -> int:
    refresh_config_if_changed()
    return CONFIG.log_retention_days


class DailyLogWriter:
    def __init__(self, base_path: Path | str, *, clock=None, retention=None, report=None):
        self.base_path = Path(os.path.abspath(base_path))
        self.clock = clock or (lambda: datetime.now().astimezone())
        self.retention = retention or retention_days
        self._report = report or report_log_failure
        self._cleaned = None
        self._lock = threading.RLock()

    def path_for(self, day: date) -> Path:
        return self.base_path.with_name(f"{self.base_path.stem}-{day.isoformat()}.log")

    def _clean(self, today: date, days: int) -> None:
        cutoff = today - timedelta(days=days - 1)
        prefix = self.base_path.stem + "-"
        try:
            for path in self.base_path.parent.iterdir():
                name = path.name
                if not (name.startswith(prefix) and name.endswith(".log") and len(name) == len(prefix) + 14):
                    continue
                try:
                    file_date = date.fromisoformat(name[len(prefix):-4])
                    if file_date.isoformat() != name[len(prefix):-4]:
                        continue
                except ValueError:
                    continue
                if file_date >= cutoff:
                    continue
                try:
                    info = path.lstat()
                    if not stat.S_ISREG(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                        continue
                    require_safe_path(path)
                    path.unlink()
                except Exception as error:
                    self._report("cleanup", path, error)
        except Exception as error:
            self._report("cleanup", self.base_path.parent, error)

    def append(self, text: str) -> bool:
        path = self.base_path
        try:
            with self._lock:
                today = self.clock().astimezone().date()
                days = self.retention()
                path = self.path_for(today)
                require_safe_path(path)
                path.parent.mkdir(parents=True, exist_ok=True)
                require_safe_path(path)
                if self._cleaned != (today, days):
                    self._cleaned = (today, days)  # One attempt per observed date/config.
                    self._clean(today, days)
                with path.open("ab+") as stream:
                    if stream.tell():
                        stream.seek(-1, os.SEEK_END)
                        if stream.read(1) != b"\n":
                            stream.write(b"\n")
                    stream.seek(0, os.SEEK_END)
                    stream.write(text.encode("utf-8"))
            _file_diagnostics.flush("file|" + str(path))
            return True
        except Exception as error:
            self._report("write", path, error)
            return False


class DailyLogHandler(logging.Handler):
    def __init__(self, base_path: Path | str):
        super().__init__()
        self.writer = DailyLogWriter(base_path)

    def emit(self, record: logging.LogRecord) -> None:
        try:
            self.writer.append(self.format(record) + "\n")
        except Exception as error:
            report_log_failure("format", self.writer.base_path, error)
