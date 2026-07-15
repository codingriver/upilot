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

class TestDomainService:
    async def test_run(
        self, test_mode: str = "EditMode", test_filter: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"testMode": test_mode}
        if test_filter:
            payload["testFilter"] = test_filter
        return await self.dispatcher.call(
            request_id, "test.run", payload, timeout_ms=300000
        )

    async def test_results(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.results", {})

    async def test_list(self, test_mode: str = "EditMode") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "test.list", {"testMode": test_mode}
        )

    async def editor_e2e_run(
        self,
        spec_path: str,
        artifact_dir: str | None = None,
        stop_on_first_failure: bool = True,
        export_zip: bool = False,
        webhook_on_failure: bool = False,
    ) -> ToolResponse:
        """Run M26 YAML E2E spec from disk (orchestrates screenshot/console; UIToolkit steps disabled)."""
        from .editor_e2e.runner import run_editor_e2e_from_path

        return await run_editor_e2e_from_path(
            self,
            spec_path,
            artifact_dir=artifact_dir,
            stop_on_first_failure=stop_on_first_failure,
            export_zip=export_zip,
            webhook_on_failure=webhook_on_failure,
        )

    async def batch_diagnostics(self) -> ToolResponse:
        """Fetch window diagnostics, console summary, and editor state in one call."""
        request_id = new_id("req")
        results = await asyncio.gather(
            self.resource_window_diagnostics(),
            self.resource_console_summary(),
            self.resource_editor_state(),
            return_exceptions=True,
        )
        combined: dict = {}
        labels = ["windowDiagnostics", "consoleSummary", "editorState"]
        for label, r in zip(labels, results):
            if isinstance(r, Exception):
                combined[label] = {"error": str(r)}
            elif not r.ok:
                combined[label] = {"error": r.error.message if r.error else "unknown"}
            else:
                combined[label] = r.data
        return ok(request_id, combined)

    async def verify_window(
        self,
        window_title: str = "upilot",
        include_screenshot: bool = True,
        screenshot_degrade: str | None = None,
    ) -> ToolResponse:
        """All-in-one verification: compile wait → open window → screenshot + diagnostics + console."""
        request_id = new_id("req")

        compile_r = await self.compile_wait(timeout_s=60, poll_interval_s=0.5)
        compile_data = (
            compile_r.data
            if compile_r.ok
            else {"error": compile_r.error.message if compile_r.error else "unknown"}
        )

        diag_results = await asyncio.gather(
            self.resource_window_diagnostics(),
            self.resource_console_summary(),
            return_exceptions=True,
        )

        screenshot_data = None
        if include_screenshot:
            try:
                deg = screenshot_degrade or getenv(
                    "UPILOT_VERIFY_SCREENSHOT_DEGRADE"
                )
                ss_r = await self.screenshot_editor_window(window_title, degrade=deg)
                if ss_r.ok:
                    screenshot_data = ss_r.data
                else:
                    screenshot_data = {
                        "error": ss_r.error.message if ss_r.error else "unknown",
                        "code": ss_r.error.code if ss_r.error else "",
                    }
            except Exception as e:
                screenshot_data = {"error": str(e)}

        combined: dict = {"compileWait": compile_data}
        labels = ["windowDiagnostics", "consoleSummary"]
        for label, r in zip(labels, diag_results):
            if isinstance(r, Exception):
                combined[label] = {"error": str(r)}
            elif not r.ok:
                combined[label] = {"error": r.error.message if r.error else "unknown"}
            else:
                combined[label] = r.data

        if screenshot_data is not None:
            combined["screenshot"] = screenshot_data

        return ok(request_id, combined)

    async def wait_condition(
        self,
        target_window: str,
        condition_type: str = "element_exists",
        element_name: str = "",
        text_contains: str = "",
        value_equals: str = "",
        type_filter: str = "",
        timeout_s: float = 30,
        poll_interval_s: float = 0.5,
    ) -> ToolResponse:
        """Disabled together with UIToolkit MCP (previously polled uitoolkit.query)."""
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "wait_condition depends on UIToolkit; disabled in this build.",
            {},
        )

    # Optional UPilot Flow test operations.
    async def upilot_flow_run(
        self,
        yaml_paths: list[str] | None = None,
        yaml_directory: str = "",
        headed: bool = False,
        stop_on_first_failure: bool = False,
        continue_on_step_failure: bool = False,
        screenshot_on_failure: bool = True,
        default_timeout_ms: int = 10000,
        enable_verbose_log: bool = False,
        report_path: str = "Reports/UPilot/Flow",
        debug_on_failure: bool = False,
        batch_size: int = 10,
        batch_offset: int = 0,
        total_all: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict[str, object] = {
            "headed": headed,
            "stopOnFirstFailure": stop_on_first_failure,
            "continueOnStepFailure": continue_on_step_failure,
            "screenshotOnFailure": screenshot_on_failure,
            "defaultTimeoutMs": default_timeout_ms,
            "enableVerboseLog": enable_verbose_log,
            "debugOnFailure": debug_on_failure,
            "reportPath": report_path,
            "batchSize": batch_size,
            "batchOffset": batch_offset,
            "totalAll": total_all,
        }
        if yaml_paths:
            payload["yamlPaths"] = yaml_paths
        if yaml_directory:
            payload["yamlDirectory"] = yaml_directory
        return await self.dispatcher.call(
            request_id, "upilot_flow.run", payload, timeout_ms=180000
        )

    async def upilot_flow_validate(self, yaml_path: str) -> ToolResponse:
        return await self.dispatcher.call(
            new_id("req"),
            "upilot_flow.validate",
            {"yamlPath": yaml_path},
            timeout_ms=30000,
        )

    async def upilot_flow_migrate(
        self,
        yaml_paths: list[str] | None = None,
        yaml_directory: str = "",
        target_directory: str = "",
        dry_run: bool = True,
    ) -> ToolResponse:
        payload: dict[str, object] = {"dryRun": dry_run}
        if yaml_paths:
            payload["yamlPaths"] = yaml_paths
        if yaml_directory:
            payload["yamlDirectory"] = yaml_directory
        if target_directory:
            payload["targetDirectory"] = target_directory
        return await self.dispatcher.call(
            new_id("req"), "upilot_flow.migrate", payload, timeout_ms=180000
        )

    async def upilot_flow_results(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.results",
            {"executionId": execution_id},
            timeout_ms=30000,
        )

    async def upilot_flow_cancel(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.cancel",
            {"executionId": execution_id},
            timeout_ms=30000,
        )

    async def upilot_flow_force_reset(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "upilot_flow.force_reset",
            {},
            timeout_ms=30000,
        )

