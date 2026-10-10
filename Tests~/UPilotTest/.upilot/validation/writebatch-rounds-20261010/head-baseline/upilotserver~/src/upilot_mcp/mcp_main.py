from __future__ import annotations

import asyncio
import argparse
import json
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
    if len(sys.argv) > 1 and sys.argv[1] == "compile":
        parser = argparse.ArgumentParser(prog="upilot compile")
        parser.add_argument("--project", required=True, help="Unity project root to attach and compile.")
        parser.add_argument("--http-port", type=int, default=None, help="Override the project HTTP MCP port.")
        parser.add_argument("--timeout", type=float, default=600.0, help="Compile timeout in seconds.")
        args = parser.parse_args(sys.argv[2:])
        from .compile_driver import run_compile_driver

        result = run_compile_driver(
            args.project,
            http_port=args.http_port,
            timeout_s=max(1.0, args.timeout),
        )
        print(json.dumps(result, ensure_ascii=False, indent=2))
        raise SystemExit(0 if result.get("ok") else 1)
    _setup_logging()
    asyncio.run(main())


if __name__ == "__main__":
    _cli()
