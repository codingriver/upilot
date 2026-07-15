#!/usr/bin/env python3
"""UPilot MCP server launcher used by the Unity Editor."""

import os
import sys


def _parse_cli_env() -> None:
    """Map Unity Editor CLI args (--log-file / --log-level) to env vars."""
    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--log-file" and i + 1 < len(args):
            os.environ.setdefault("UPILOT_LOG_FILE", args[i + 1])
            i += 2
        elif args[i] == "--log-level" and i + 1 < len(args):
            os.environ.setdefault("UPILOT_LOG_LEVEL", args[i + 1])
            i += 2
        else:
            i += 1


_parse_cli_env()

_repo_root = os.path.dirname(os.path.abspath(__file__))
_src = os.path.join(_repo_root, "src")
if _src not in sys.path:
    sys.path.insert(0, _src)

from upilot_mcp.mcp_main import _cli


if __name__ == "__main__":
    _cli()
