from __future__ import annotations

import os


def getenv(name: str, default: str = "") -> str:
    return os.environ.get(name, default)


def setdefault(name: str, value: str) -> None:
    os.environ.setdefault(name, value)


def env_float(name: str, default: float) -> float:
    try:
        return float(getenv(name, str(default)))
    except ValueError:
        return default


def env_int(name: str, default: int) -> int:
    try:
        return int(getenv(name, str(default)))
    except ValueError:
        return default
