import asyncio
from copy import deepcopy
from types import SimpleNamespace

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain import test_service
from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.mcp_tools import compile_tools, flow_tools
from upilot_mcp.responses import fail, ok
from upilot_mcp.tool_registry import REGISTRY, dispatch_public_tool


TOOLS = ("unity_upilot_flow_run_file", "unity_upilot_flow_run_suite", "unity_upilot_flow_run_async")


class Service(TestDomainService, TaskDomainService):
    def __init__(self):
        self.dispatcher = self
        self.calls = []
        self.start = ok("start", {"executionId": "flow-1", "status": "queued", "total": 1})
        self.result = ok("result", {
            "executionId": "flow-1", "status": "completed", "total": 1,
            "passed": 1, "failed": 0, "cases": [{"caseName": "probe"}],
        })

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, deepcopy(payload)))
        if name == "upilot_flow.run":
            return self.start
        if name == "upilot_flow.results":
            return self.result
        assert name == "upilot_flow.cancel"
        return ok("cancel", {"cancelRequested": True})


@pytest.fixture
def flow(tmp_path, monkeypatch):
    monkeypatch.setattr(CONFIG, "flow_enabled", True)
    monkeypatch.setattr("upilot_mcp.tool_registry.refresh_config_if_changed", lambda: None)

    async def no_sleep(_seconds):
        pass

    monkeypatch.setattr(test_service.asyncio, "sleep", no_sleep)
    for module in (flow_tools, compile_tools):
        monkeypatch.setattr(module, "_payload", lambda response: response)
        monkeypatch.setattr(module, "_log_tool_result", lambda name, response: response)
        monkeypatch.setattr(module, "_log_tool_call", lambda *args: None)
    path = tmp_path / "probe.yaml"
    path.write_text("schemaVersion: 2\n", encoding="utf-8")
    return path


def arguments(name, path):
    if name.endswith("_file"):
        return {"yamlPath": str(path)}
    if name.endswith("_suite"):
        return {"directoryPath": str(path.parent)}
    return {"yamlPaths": [str(path)], "batchSize": 1, "batchOffset": 0}


@pytest.mark.parametrize("name", TOOLS)
def test_native_and_proxy_share_arguments_defaults_and_results(flow, monkeypatch, name):
    native, proxy = Service(), Service()
    monkeypatch.setattr(flow_tools, "_get_facade", lambda: native)
    args = {**arguments(name, flow), "headed": False, "reportOutputPath": "Reports/Probe",
            "screenshotPath": "Reports/Probe/Images", "preStepDelayMs": 10,
            "continueOnStepFailure": True, "enableVerboseLog": False}
    left = asyncio.run(getattr(flow_tools, name)(**args))
    right = asyncio.run(dispatch_public_tool(proxy, name, args))
    assert left.ok and right.ok and left.data == right.data
    assert native.calls == proxy.calls
    assert sum(command == "upilot_flow.run" for command, _ in native.calls) == 1
    if name.endswith("_async"):
        assert [command for command, _ in native.calls] == ["upilot_flow.run"]
        assert left.data["status"] == "queued"
    else:
        assert left.data["result"]["status"] == "completed"
    assert REGISTRY.resolve(name).idempotent is False


@pytest.mark.parametrize("name", TOOLS)
@pytest.mark.parametrize("failure", ["disabled", "path", "start", "identity"])
def test_flow_failure_does_not_start_twice(flow, monkeypatch, name, failure):
    services = [Service(), Service()]
    args = arguments(name, flow)
    expected = {"disabled": "FEATURE_DISABLED", "start": "START_FAILED", "identity": "UIFLOW_EXECUTION_ID_MISSING"}
    if failure == "disabled":
        monkeypatch.setattr(CONFIG, "flow_enabled", False)
    if failure == "path":
        missing = flow.parent / "missing" / "absent.yaml"
        args = arguments(name, missing)
        expected["path"] = "UIFLOW_SUITE_DIR_NOT_FOUND" if name.endswith("_suite") else "UIFLOW_YAML_NOT_FOUND"
    for service in services:
        if failure == "start":
            service.start = fail("start", "START_FAILED", "start rejected")
        elif failure == "identity":
            service.start = ok("start", {})
    monkeypatch.setattr(flow_tools, "_get_facade", lambda: services[0])
    results = [
        asyncio.run(getattr(flow_tools, name)(**args)),
        asyncio.run(dispatch_public_tool(services[1], name, args)),
    ]
    for service, result in zip(services, results):
        assert not result.ok and result.error.code == expected[failure]
        assert sum(command == "upilot_flow.run" for command, _ in service.calls) == (1 if failure in {"start", "identity"} else 0)


@pytest.mark.parametrize("name", TOOLS[:2])
@pytest.mark.parametrize("terminal", ["failed", "aborted"])
def test_sync_flow_preserves_business_terminal_failure(flow, name, terminal):
    service = Service()
    service.result = ok("result", {"executionId": "flow-1", "status": terminal, "failed": 1})
    result = asyncio.run(service._dispatch_tool(name, arguments(name, flow)))
    assert result.ok and result.data["result"]["status"] == terminal
    if name.endswith("_suite"):
        assert result.data["result"]["exitCode"] == 1
    assert sum(command == "upilot_flow.run" for command, _ in service.calls) == 1


@pytest.mark.parametrize("name", TOOLS[:2])
def test_sync_flow_timeout_cancels_once_and_never_replays_start(flow, monkeypatch, name):
    service = Service()
    service.result = ok("result", {"executionId": "flow-1", "status": "running"})
    clock = iter([0.0, 0.0, 1000.0])
    monkeypatch.setattr(test_service, "time", SimpleNamespace(monotonic=lambda: next(clock)))
    result = asyncio.run(dispatch_public_tool(service, name, arguments(name, flow)))
    assert not result.ok and result.error.code == "UIFLOW_WAIT_TIMEOUT"
    assert result.error.detail == {"executionId": "flow-1", "lastStatus": "running"}
    assert [name for name, _ in service.calls] == ["upilot_flow.run", "upilot_flow.results", "upilot_flow.cancel"]


@pytest.mark.parametrize("name", TOOLS)
def test_task_rejects_flow_retries_before_start(flow, name):
    service = Service()
    result = asyncio.run(service.task_execute("unsafe retry", name, arguments(name, flow), retry_count=1))
    assert not result.ok and result.error.code == "TASK_RETRY_UNSAFE"
    assert service.calls == []


def test_compile_compatibility_alias_uses_existing_handler(flow, monkeypatch):
    class CompileService:
        async def compile_errors(self, compile_request_id=""):
            return ok("compile", {"compileRequestId": compile_request_id, "total": 0})

    service = CompileService()
    monkeypatch.setattr(compile_tools, "_get_facade", lambda: service)
    native = asyncio.run(compile_tools.unity_compile_errors_get(compileRequestId="compile-1"))
    proxy = asyncio.run(dispatch_public_tool(service, "unity_compile_errors_get", {"compileRequestId": "compile-1"}))
    assert REGISTRY.resolve("unity_compile_errors_get").facade_method == "compile_errors"
    assert native.ok and proxy.ok and native.data == proxy.data
