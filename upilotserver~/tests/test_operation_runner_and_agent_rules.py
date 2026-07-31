from __future__ import annotations

import asyncio
from pathlib import Path

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY


class _Session:
    def __init__(self, project_path: Path) -> None:
        self.project_path = str(project_path)


class _SessionManager:
    def __init__(self, project_path: Path) -> None:
        self.active = _Session(project_path)


class _Server:
    def __init__(self, project_path: Path) -> None:
        self.session_manager = _SessionManager(project_path)


class _OperationService(TaskDomainService):
    def __init__(self, project_path: Path, statuses: list[dict]) -> None:
        self.server = _Server(project_path)
        self._operations: dict[str, dict] = {}
        self._operation_failure_history: dict[str, int] = {}
        self._statuses = list(statuses)
        self.calls: list[tuple[str, dict]] = []

    async def _dispatch_tool(self, tool_name: str, tool_args: dict):
        self.calls.append((tool_name, tool_args))
        if tool_name == "start":
            return ok("req-start", {"status": "Running", "phase": "Started"})
        if tool_name == "status":
            payload = self._statuses.pop(0) if self._statuses else {"status": "Running", "phase": "Waiting"}
            return ok("req-status", payload)
        if tool_name == "cancel":
            return ok("req-cancel", {"status": "Canceled", "phase": "Canceled"})
        return ok("req-tool", {})


def _replace_line(text: str, prefix: str, replacement: str) -> str:
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line.startswith(prefix):
            lines[index] = replacement
            return "\n".join(lines) + "\n"
    raise AssertionError(f"line prefix not found: {prefix}")


def test_agent_rules_check_and_install_preserve_existing_business_rules(tmp_path: Path) -> None:
    agents = tmp_path / "AGENTS.md"
    agents.write_text("# Project Rules\n\nbusiness rule stays\n", encoding="utf-8")
    service = _OperationService(tmp_path, [])

    dry = asyncio.run(service.agent_rules_install(apply=False))
    assert dry.ok and dry.data
    assert dry.data["dryRun"] is True
    assert "<!-- upilot:start -->" not in agents.read_text(encoding="utf-8")

    applied = asyncio.run(service.agent_rules_install(apply=True))
    text = agents.read_text(encoding="utf-8")
    assert applied.ok and applied.data["applied"] is True
    assert "business rule stays" in text
    assert "rulesVersion: 4" in text
    assert "<!-- upilot:start -->" in text
    assert "<!-- upilot:end -->" in text
    assert applied.data["fileSha256"]


def test_agent_rules_check_ignores_generated_timestamp_only(tmp_path: Path) -> None:
    agents = tmp_path / "AGENTS.md"
    service = _OperationService(tmp_path, [])

    applied = asyncio.run(service.agent_rules_install(apply=True))
    assert applied.ok and applied.data["applied"] is True

    text = agents.read_text(encoding="utf-8")
    agents.write_text(
        _replace_line(text, "generatedAt: ", "generatedAt: 2000-01-01T00:00:00Z"),
        encoding="utf-8",
    )

    checked = asyncio.run(service.agent_rules_check())

    assert checked.ok and checked.data
    assert checked.data["needsUpdate"] is False
    assert checked.data["diffSummary"] == []


def test_agent_rules_check_detects_rules_version_change(tmp_path: Path) -> None:
    agents = tmp_path / "AGENTS.md"
    service = _OperationService(tmp_path, [])

    applied = asyncio.run(service.agent_rules_install(apply=True))
    assert applied.ok and applied.data["applied"] is True

    text = agents.read_text(encoding="utf-8")
    agents.write_text(
        _replace_line(text, "rulesVersion: ", "rulesVersion: 3"),
        encoding="utf-8",
    )

    checked = asyncio.run(service.agent_rules_check())

    assert checked.ok and checked.data
    assert checked.data["needsUpdate"] is True
    assert checked.data["recommendedRulesVersion"] == "4"
    assert "rulesVersion differs" in checked.data["diffSummary"]


def test_operation_wait_collects_artifacts_and_timing(tmp_path: Path) -> None:
    report = tmp_path / "report.txt"
    report.write_text("line1\nline2\nline3\n", encoding="utf-8")
    service = _OperationService(
        tmp_path,
        [
            {"ok": True, "status": "Running", "phase": "WaitingBattle", "progress": 0.5},
            {
                "ok": True,
                "status": "Succeeded",
                "phase": "Complete",
                "elapsedSec": 1.25,
                "artifacts": {"reportPath": str(report)},
            },
        ],
    )
    job_spec = {
        "displayName": "fake smoke",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        "cancelCall": {"kind": "tool", "toolName": "cancel", "toolArgs": {}},
        "timeoutSec": 5,
        "pollIntervalSec": 0.01,
        "artifactRules": {"readReportTailLines": 2},
    }

    started = asyncio.run(service.operation_start(job_spec))
    waited = asyncio.run(service.operation_wait(started.data["operationId"], timeout_s=1, poll_interval_s=0.01))

    assert waited.ok and waited.data["status"] == "Succeeded"
    assert waited.data["artifacts"]["reportPath"]["exists"] is True
    assert waited.data["artifacts"]["reportPath"]["tail"] == "line2\nline3"
    assert waited.data["timing"]["totalWallMs"] >= 0
    assert waited.data["timing"]["projectElapsedMs"] == 1250


def test_operation_repeat_failure_signature_is_reported(tmp_path: Path) -> None:
    job_spec = {
        "displayName": "fake failure",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        "timeoutSec": 5,
        "pollIntervalSec": 0.01,
    }
    failure = {
        "ok": False,
        "status": "Failed",
        "phase": "Validate",
        "error": "same failure",
        "failureSignature": "Fake.Repeat",
    }
    service = _OperationService(tmp_path, [failure, failure])

    first = asyncio.run(service.operation_start(job_spec))
    first_wait = asyncio.run(service.operation_wait(first.data["operationId"], timeout_s=1, poll_interval_s=0.01))
    second = asyncio.run(service.operation_start(job_spec))
    second_wait = asyncio.run(service.operation_wait(second.data["operationId"], timeout_s=1, poll_interval_s=0.01))

    assert first_wait.data["status"] == "Failed"
    assert first_wait.data["repeatFailure"] is False
    assert second_wait.data["status"] == "Failed"
    assert second_wait.data["repeatFailure"] is True
    assert "Stop retrying" in second_wait.data["recommendation"]


def test_operation_and_agent_rule_tools_are_registered() -> None:
    from upilot_mcp.mcp_tools import task_tools  # noqa: F401

    names = {item.name for item in REGISTRY.list()}
    assert {
        "unity_operation_start",
        "unity_operation_status",
        "unity_operation_wait",
        "unity_operation_cancel",
        "unity_operation_collect_artifacts",
        "unity_agent_rules_check",
        "unity_agent_rules_install",
    }.issubset(names)

    assert REGISTRY.resolve("unity_operation_start").idempotent is False
    assert REGISTRY.resolve("unity_agent_rules_install").destructive is True
