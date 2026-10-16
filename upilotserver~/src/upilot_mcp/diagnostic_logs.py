"""Bounded repeat aggregation shared by stderr and the general Server log."""
from __future__ import annotations

import logging
import threading
import time
from collections import OrderedDict


class RepeatDiagnosticFilter(logging.Filter):
    window = 10.0
    capacity = 256

    def __init__(self, emit=None, clock=time.monotonic):
        super().__init__()
        self._clock = clock
        self._emit = emit or (lambda record: logging.getLogger(record.name).handle(record))
        self._entries = OrderedDict()
        self._lock = threading.RLock()

    def _summary(self, entry, now):
        if not entry["count"]:
            return
        source = entry["record"]
        record = logging.LogRecord(source.name, source.levelno, source.pathname, source.lineno,
            "%s\n[UPilot] repeated count=%d firstAt=%.3f lastAt=%.3f",
            (source.getMessage(), entry["count"], entry["first_wall"], entry["last_wall"]), None)
        record._upilot_diagnostic_summary = True
        entry["count"] = 0
        entry["emitted"] = now
        self._emit(record)

    def flush(self, scope=None):
        with self._lock:
            for key, entry in list(self._entries.items()):
                if scope is None or key[1] == scope:
                    self._summary(entry, self._clock())
                    del self._entries[key]

    def filter(self, record):
        if getattr(record, "_upilot_diagnostic_summary", False):
            return True
        cached = getattr(record, "_upilot_diagnostic_decision", None)
        if cached and cached[0] == id(self):
            return cached[1]  # Both handlers must make exactly the same decision.
        with self._lock:
            now = self._clock()
            for key, entry in list(self._entries.items()):
                if now - entry["emitted"] >= self.window:
                    self._summary(entry, now)
                if now - entry["last"] >= 60:
                    del self._entries[key]
            allowed = True
            if record.levelno >= logging.WARNING:
                error = record.exc_info[1] if record.exc_info else None
                key = (record.name, getattr(record, "diagnostic_scope", ""), record.levelno,
                    getattr(record, "diagnostic_fingerprint", record.getMessage()),
                    type(error).__qualname__ if error else "", str(error) if error else "")
                terminal = getattr(record, "diagnostic_terminal", False)
                entry = self._entries.get(key)
                if entry and not (terminal and not entry["terminal"]) and (entry["terminal"] or now - entry["emitted"] < self.window):
                    entry["last"] = now
                    entry["last_wall"] = record.created
                    if not (terminal and entry["terminal"]):
                        entry["count"] += 1
                    allowed = False
                else:
                    if entry:
                        self._summary(entry, now)
                    elif len(self._entries) >= self.capacity:
                        _, oldest = self._entries.popitem(last=False)
                        self._summary(oldest, now)
                    self._entries[key] = dict(record=record, first=now, last=now,
                        first_wall=record.created, last_wall=record.created, emitted=now, count=0, terminal=terminal)
                if terminal:
                    for other_key, other in list(self._entries.items()):
                        if other_key[1] == key[1]:
                            self._summary(other, now)
                self._entries.move_to_end(key)
            record._upilot_diagnostic_decision = (id(self), allowed)
            return allowed
