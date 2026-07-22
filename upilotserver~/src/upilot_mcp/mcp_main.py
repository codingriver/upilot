from __future__ import annotations

import asyncio
import logging
import os
import sys

from .env import getenv
from .mcp_stdio_server import main
from .version import version_payload


def _setup_logging() -> None:
    """Configure logging for upilot when used as a package entry point."""
    root = logging.getLogger("upilot")
    if root.handlers:
        return

    log_level = getenv("UPILOT_LOG_LEVEL", "DEBUG").upper()
    if log_level not in {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}:
        log_level = "DEBUG"
    numeric_level = getattr(logging, log_level)

    fmt = logging.Formatter(
        "[%(asctime)s] %(name)s %(levelname)s  %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    root.setLevel(numeric_level)

    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setLevel(numeric_level)
    stderr_handler.setFormatter(fmt)
    root.addHandler(stderr_handler)

    log_file = getenv("UPILOT_LOG_FILE", "")
    if log_file:
        file_handler = logging.FileHandler(log_file, encoding="utf-8")
        file_handler.setLevel(numeric_level)
        file_handler.setFormatter(fmt)
        file_handler.stream.reconfigure(write_through=True)
        root.addHandler(file_handler)


def _cli() -> None:
    """Console script entry point (used by pip install / uvx)."""
    if "--version" in sys.argv[1:]:
        payload = version_payload()
        print(
            f"upilot-mcp {payload['server_version']} "
            f"channel={payload['build_channel']} "
            f"commit={payload['build_commit'] or 'unknown'} "
            f"protocol={payload['protocol_version']}"
        )
        return
    _setup_logging()
    asyncio.run(main())


if __name__ == "__main__":
    _cli()
