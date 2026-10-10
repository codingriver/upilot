from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import json
import logging
import os
import shlex
import subprocess
import sys
import time
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from ..config import CONFIG, diagnose_client_configs
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import REGISTRY, REGISTRY_VERSION, dispatch_public_tool

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="


def _normalize_reflection_parameters(parameters: list | None) -> list:
    if not parameters:
        return []
    normalized = []
    for value in parameters:
        if value is None:
            normalized.append(None)
        elif isinstance(value, (list, dict)):
            normalized.append(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
        else:
            normalized.append(str(value))
    return normalized


def _json_dumps_or_empty(value: object | None) -> str:
    if value is None:
        return ""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))

class BuildDomainService:
    async def build_start(
        self,
        build_target: str = "StandaloneWindows64",
        output_path: str = "Builds/",
        scenes: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"buildTarget": build_target, "outputPath": output_path}
        if scenes:
            payload["scenes"] = scenes
        return await self.dispatcher.call(
            request_id, "build.start", payload, timeout_ms=600000
        )

    async def build_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.status", {})

    async def build_cancel(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.cancel", {})

    async def build_targets(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.targets", {})
