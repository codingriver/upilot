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
    def __init__(self, project_path: Path, statuses: list[dict], cancel_results: list | None = None) -> None:
        self.server = _Server(project_path)
        self._operations: dict[str, dict] = {}
        self._operation_failure_history: dict[str, int] = {}
        self._statuses = list(statuses)
        self._cancel_results = list(cancel_results or [])
        self.calls: list[tuple[str, dict]] = []

    async def _dispatch_tool(self, tool_name: str, tool_args: dict):
        self.calls.append((tool_name, tool_args))
        if tool_name == "start":
            return ok("req-start", {"status": "Running", "phase": "Started", "captureId": "capture-123"})
        if tool_name == "status":
            payload = self._statuses.pop(0) if self._statuses else {"status": "Running", "phase": "Waiting"}
            return ok("req-status", payload)
        if tool_name == "cancel":
            if self._cancel_results:
                return self._cancel_results.pop(0)
            return ok("req-cancel", {"status": "Canceled", "phase": "Canceled"})
        return ok("req-tool", {})


class _ReflectionOperationService(_OperationService):
    def __init__(self, project_path: Path, reflection_results: list[dict]) -> None:
        super().__init__(project_path, [])
        self._reflection_results = list(reflection_results)

    async def reflection_call(self, **_kwargs):
        payload = self._reflection_results.pop(0)
        return ok("req-reflection", payload)


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
    assert "rulesVersion: 19" in text
    assert "Parent Agent rules path" in text
    assert "circular references are skipped" in text
    assert "Streamable HTTP: `http://127.0.0.1:8011/mcp`" in text
    assert "unity_safe_compile_and_wait" in text
    assert "`nextSequence` as the next call's `afterSequence`" in text
    assert "unity_config_csv_patch" in text
    assert "unity_hang_status" in text
    assert "fallbackSources" in text
    assert "Optional UPilot Tracer" in text
    assert "`追踪器`, or `the tracer` as UPilot Tracer (`UPilot 追踪器`)" in text
    assert "saves without applying by default" in text
    assert "Do not use Native, InternalCall, injected" in text
    assert "{{" not in text
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
        _replace_line(text, "rulesVersion: ", "rulesVersion: 8"),
        encoding="utf-8",
    )

    checked = asyncio.run(service.agent_rules_check())

    assert checked.ok and checked.data
    assert checked.data["needsUpdate"] is True
    assert checked.data["recommendedRulesVersion"] == "19"
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


def test_operation_parses_nested_reflection_business_status_and_artifacts(tmp_path: Path) -> None:
    report = tmp_path / "reflection-report.json"
    report.write_text("{}", encoding="utf-8")
    service = _ReflectionOperationService(tmp_path, [
        {"result": '{"status":"Running","phase":"Opening","operationId":"business-1"}'},
        {"result": '{"status":"Succeeded","phase":"Complete","artifacts":{"summaryPath":"%s"}}' % str(report).replace("\\", "\\\\")},
    ])
    job_spec = {
        "startCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Start"},
        "statusCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Status"},
        "artifactRules": {"readReportTailLines": 1},
        "timeoutSec": 5,
    }

    started = asyncio.run(service.operation_start(job_spec))
    terminal = asyncio.run(service.operation_status(started.data["operationId"]))

    assert terminal.ok
    assert terminal.data["status"] == "Succeeded"
    assert terminal.data["phase"] == "Complete"
    assert terminal.data["terminal"] is True
    assert terminal.data["artifacts"]["summaryPath"]["exists"] is True


def test_operation_supports_explicit_result_and_field_paths(tmp_path: Path) -> None:
    service = _ReflectionOperationService(tmp_path, [
        {"envelope": {"business": '{"state":{"name":"Running","step":"Warmup"}}'}},
        {"envelope": {"business": '{"state":{"name":"Done","step":"Complete"}}'}},
    ])
    job_spec = {
        "startCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Start"},
        "statusCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Status"},
        "resultPath": "envelope.business",
        "statusPath": "state.name",
        "phasePath": "state.step",
        "terminalStatusMapping": {"success": ["Done"]},
    }

    started = asyncio.run(service.operation_start(job_spec))
    terminal = asyncio.run(service.operation_status(started.data["operationId"]))

    assert terminal.ok
    assert terminal.data["status"] == "Succeeded"
    assert terminal.data["phase"] == "Complete"


def test_operation_invalid_reflection_json_is_a_diagnostic_terminal_failure(tmp_path: Path) -> None:
    service = _ReflectionOperationService(tmp_path, [
        {"result": '{"status":"Running"}'},
        {"result": "{not-json"},
    ])
    job_spec = {
        "startCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Start"},
        "statusCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Status"},
    }

    started = asyncio.run(service.operation_start(job_spec))
    failed = asyncio.run(service.operation_status(started.data["operationId"]))

    assert failed.ok is False
    assert failed.error.code == "OPERATION_RESULT_INVALID"
    assert failed.error.detail["status"] == "Failed"
    assert failed.error.detail["phase"] == "StatusResultInvalid"
    assert failed.error.detail["failureSignature"] == "OperationResultInvalid"
    assert failed.error.detail["terminal"] is True


def test_operation_cancel_accepts_nested_terminal_result(tmp_path: Path) -> None:
    service = _ReflectionOperationService(tmp_path, [
        {"result": '{"status":"Running","phase":"Work"}'},
        {"result": '{"status":"Canceled","phase":"Canceled","cleanupPending":false}'},
    ])
    job_spec = {
        "startCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Start"},
        "statusCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Status"},
        "cancelCall": {"kind": "reflection", "typeName": "Fixture", "methodName": "Cancel"},
    }

    started = asyncio.run(service.operation_start(job_spec))
    canceled = asyncio.run(service.operation_cancel(started.data["operationId"]))

    assert canceled.ok
    assert canceled.data["status"] == "Canceled"
    assert canceled.data["terminal"] is True
    assert canceled.data["cleanupPending"] is False


def test_operation_validate_normalizes_without_starting_business(tmp_path: Path) -> None:
    service = _OperationService(tmp_path, [])
    job_spec = {
        "displayName": "project workflow",
        "startCall": {"kind": "native", "route": "workflow.start", "payload": {}},
        "statusCall": {
            "kind": "native",
            "route": "workflow.status",
            "payload": {"operationId": "${start.operationId}"},
        },
        "cancelCall": {
            "kind": "native",
            "route": "workflow.cancel",
            "payload": {"operationId": "${start.operationId}"},
        },
        "artifactRules": {"fromStatusFields": ["summaryPath"]},
    }

    result = asyncio.run(service.operation_validate(job_spec))

    assert result.ok and result.data["valid"] is True
    assert result.data["normalizedJobSpec"]["timeoutSec"] == 300
    assert result.data["normalizedJobSpec"]["pollIntervalSec"] == 3
    assert service.calls == []


def test_operation_validate_reports_precise_field_errors(tmp_path: Path) -> None:
    service = _OperationService(tmp_path, [])
    result = asyncio.run(service.operation_validate({
        "startCall": {"kind": "reflection", "typeName": "", "methodName": ""},
        "statusCall": {"kind": "tool", "toolName": "missing_tool", "toolArgs": {"id": "prefix-${start.id}"}},
        "timeoutSec": 0,
        "artifactRules": {"fromStatusFields": "summaryPath"},
    }, inspect_reflection=False))

    assert result.ok is False
    paths = {item["path"] for item in result.error.detail["errors"]}
    assert "startCall.typeName" in paths
    assert "startCall.methodName" in paths
    assert "statusCall.toolName" in paths
    assert "statusCall.toolArgs.id" in paths
    assert "artifactRules.fromStatusFields" in paths


def test_operation_resolves_start_result_placeholders_for_status_and_cancel(tmp_path: Path) -> None:
    service = _OperationService(tmp_path, [{"status": "Running", "phase": "Sampling"}])
    job_spec = {
        "displayName": "profiler capture",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {
            "kind": "tool",
            "toolName": "status",
            "toolArgs": {"captureId": "${start.captureId}"},
        },
        "cancelCall": {
            "kind": "tool",
            "toolName": "cancel",
            "toolArgs": {"captureId": "${start.captureId}"},
        },
        "timeoutSec": 5,
    }

    started = asyncio.run(service.operation_start(job_spec))
    asyncio.run(service.operation_status(started.data["operationId"]))
    canceled = asyncio.run(service.operation_cancel(started.data["operationId"]))

    assert canceled.ok
    assert ("status", {"captureId": "capture-123"}) in service.calls
    assert ("cancel", {"captureId": "capture-123"}) in service.calls


def test_operation_cancel_waits_for_status_and_cleanup_before_terminal(tmp_path: Path) -> None:
    service = _OperationService(
        tmp_path,
        [
            {"status": "Canceled", "phase": "Cleanup", "cleanupPending": True, "activeLeaseCount": 1},
            {"status": "Aborted", "phase": "Aborted", "cleanupPending": False, "activeLeaseCount": 0},
        ],
    )
    job_spec = {
        "displayName": "cancel lifecycle",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        "cancelCall": {"kind": "tool", "toolName": "cancel", "toolArgs": {}},
        "timeoutSec": 5,
    }

    started = asyncio.run(service.operation_start(job_spec))
    operation_id = started.data["operationId"]
    canceled = asyncio.run(service.operation_cancel(operation_id))

    assert canceled.ok
    assert canceled.data["status"] == "CancelRequested"
    assert canceled.data["phase"] == "Stopping"
    assert canceled.data["cancelAccepted"] is True
    assert canceled.data["cleanupPending"] is True
    assert canceled.data["terminal"] is False
    assert canceled.data["endedAt"] == 0

    cleaning = asyncio.run(service.operation_status(operation_id))
    assert cleaning.ok
    assert cleaning.data["status"] == "Stopping"
    assert cleaning.data["phase"] == "Cleanup"
    assert cleaning.data["cleanupPending"] is True
    assert cleaning.data["terminal"] is False

    terminal = asyncio.run(service.operation_status(operation_id))
    assert terminal.ok
    assert terminal.data["status"] == "Canceled"
    assert terminal.data["cleanupPending"] is False
    assert terminal.data["terminal"] is True


def test_operation_cancel_is_idempotent_after_acceptance(tmp_path: Path) -> None:
    service = _OperationService(tmp_path, [{"status": "Running", "phase": "Stopping"}])
    job_spec = {
        "displayName": "idempotent cancel",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        "cancelCall": {"kind": "tool", "toolName": "cancel", "toolArgs": {}},
        "timeoutSec": 5,
    }

    started = asyncio.run(service.operation_start(job_spec))
    operation_id = started.data["operationId"]
    first = asyncio.run(service.operation_cancel(operation_id))
    second = asyncio.run(service.operation_cancel(operation_id))

    assert first.ok and second.ok
    assert first.data["cancelAttemptCount"] == 1
    assert second.data["cancelAttemptCount"] == 1
    assert [name for name, _ in service.calls].count("cancel") == 1


def test_operation_wait_window_does_not_terminate_running_job(tmp_path: Path) -> None:
    service = _OperationService(
        tmp_path,
        ([{"status": "Running", "phase": "LongWork"}] * 20)
        + [{"status": "Succeeded", "phase": "Complete"}],
    )
    job_spec = {
        "displayName": "long smoke",
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        "timeoutSec": 5,
        "pollIntervalSec": 0.01,
    }
    started = asyncio.run(service.operation_start(job_spec))
    first = asyncio.run(service.operation_wait(started.data["operationId"], timeout_s=0.001, poll_interval_s=0.01))

    assert first.ok
    assert first.data["terminal"] is False
    assert first.data["waitWindowElapsed"] is True
    assert first.data["status"] == "Running"
    assert first.data["endedAt"] == 0

    second = asyncio.run(service.operation_wait(started.data["operationId"], timeout_s=1, poll_interval_s=0.01))
    assert second.ok
    assert second.data["status"] == "Succeeded"
    assert second.data["terminal"] is True


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
        "unity_operation_validate",
        "unity_operation_status",
        "unity_operation_wait",
        "unity_operation_cancel",
        "unity_operation_collect_artifacts",
        "unity_agent_rules_check",
        "unity_agent_rules_install",
    }.issubset(names)

    assert REGISTRY.resolve("unity_operation_start").idempotent is False
    assert REGISTRY.resolve("unity_agent_rules_install").destructive is True
