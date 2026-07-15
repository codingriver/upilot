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

class ReflectionDomainService:
    async def reflection_find(
        self, type_name: str, method_name: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"typeName": type_name}
        if method_name:
            payload["methodName"] = method_name
        return await self.dispatcher.call(request_id, "reflection.find", payload)

    async def reflection_call(
        self,
        type_name: str,
        method_name: str,
        parameters: list | None = None,
        is_static: bool = True,
        target_instance_path: str = "",
        target_static_type_name: str = "",
        target_static_member_path: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "typeName": type_name,
            "methodName": method_name,
            "parameters": _normalize_reflection_parameters(parameters),
            "isStatic": is_static,
        }
        if target_instance_path:
            payload["targetInstancePath"] = target_instance_path
        if target_static_type_name:
            payload["targetStaticTypeName"] = target_static_type_name
        if target_static_member_path:
            payload["targetStaticMemberPath"] = target_static_member_path
        return await self.dispatcher.call(request_id, "reflection.call", payload)

    async def reflection_eval(
        self,
        code: str,
        variables: dict | None = None,
        options: dict | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"code": code}
        if variables is not None:
            payload["variablesJson"] = _json_dumps_or_empty(variables)
        if options is not None:
            payload["optionsJson"] = _json_dumps_or_empty(options)
        return await self.dispatcher.call(request_id, "reflection.eval", payload)
