"""Native V05 disconnect client contracts. No network, live Runner or maintenance."""
import asyncio
import copy
import hashlib
import importlib
import inspect
import json
from contextlib import asynccontextmanager
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    module = importlib.import_module("p1_acceptance_disconnect_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p0_reload_evidence"), "PROJECT", tmp_path)
    return module


class FakeAcceptance:
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls, self.sessions, self.reads = [], [], 0
        self.starts = 0
        self.value = None
        self.runner = None

    @asynccontextmanager
    async def connect(self, port):
        assert port == 8011
        session = self.Session(self, len(self.sessions))
        self.sessions.append(session)
        try:
            yield session
        finally:
            session.closed = True
            if self.failure == "starter_exit" and session.index == 1:
                raise TimeoutError("secret-transport-text")

    def terminal(self):
        c = self.client
        self.value.update(status="completed", terminal=True, endedAt=2000,
                          cleanupSucceeded=True, cleanupPending=False)
        self.runner = {"runGuid": "run", "status": "completed", "isRunning": False, "terminal": True,
                       "cleanupSucceeded": True, "cleanupPending": False, "cleanupResourcesReleased": True,
                       "unresolvedResources": [], "resultAuthoritative": True, "total": 1, "passed": 1,
                       "failed": 0, "skipped": 0, "results": [{"testName": c.LEAF, "testStatus": "Passed"}]}
        self.value["tests"] = {key: self.runner[key] for key in ("status", "cleanupSucceeded", "resultAuthoritative",
                                                                "total", "passed", "failed", "skipped")}
        summary = {**c.SELECTORS, "taskId": "owned", "runGuid": "run", "deadlineAt": 91000,
                   "expectedProject": str(c.PROJECT), "expectedSelectionDomain": "before",
                   "expectedSelectionSnapshotId": "selection", "stoppedConsoleCaptures": [],
                   "cleanupVerified": True, "testIdentityVerified": True, "sourceUnchanged": True,
                   "acceptancePassed": True, "artifactWritten": True, "runnerStartAttempted": True,
                   "steps": {"testStatus": {"data": copy.deepcopy(self.runner)}}}
        if self.failure and self.failure.startswith("summary_"):
            field = self.failure.removeprefix("summary_")
            summary[field] = "other"
        path = c.PROJECT / "Log" / "P0P1" / "owned-summary.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        raw = json.dumps(summary).encode()
        path.write_bytes(raw)
        artifact = {"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
        if self.failure == "missing_file":
            path.unlink()
        elif self.failure in ("hash", "bytes"):
            artifact["sha256" if self.failure == "hash" else "bytes"] = "wrong"
        elif self.failure == "outside_path":
            artifact["path"] = str(c.PROJECT.parent / "other-summary.json")
        elif self.failure == "resource":
            self.runner["cleanupResourcesReleased"] = False
        elif self.failure == "failed_leaf":
            self.value["tests"]["passed"] = 0
        elif self.failure == "cleanup":
            self.value["cleanupSucceeded"] = False
        elif self.failure == "cancelled":
            self.value["cancelRequested"] = True
        self.value["result"] = {"result": {"runGuid": "run", "deadlineAt": 91000,
            "acceptancePassed": True, "cleanupVerified": True, "testIdentityVerified": True,
            "sourceUnchanged": True, "artifact": artifact}}

    class Session:
        def __init__(self, fake, index):
            self.fake, self.index, self.closed = fake, index, False

        async def call_tool(self, proxy, request, *, meta):
            fake, c = self.fake, self.fake.client
            name, args = request["toolName"], request["args"]
            from upilot_mcp.mcp_tools import status_tools, task_tools, test_tools
            model = next((getattr(module, name) for module in (status_tools, task_tools, test_tools)
                          if hasattr(module, name)), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call" and not self.closed
            fake.calls.append((self.index, name, copy.deepcopy(args), copy.deepcopy(meta)))
            data = {}
            if name == "unity_mcp_status":
                from test_reload_evidence_client import status
                data = status(c.PROJECT)
                data["executionState"]["ready"] = fake.value is None or fake.value["terminal"]
                if fake.failure == "identity" and self.index == 2:
                    data["executionState"]["domainGeneration"] = "new"
                if fake.failure == "loaded":
                    data["runtimeIdentity"]["mcpServer"]["loadedSource"]["status"] = "unverified"
                if fake.failure == "project":
                    data["paths"]["unityProjectAbsolute"] = str(c.PROJECT.parent)
            elif name == "unity_queue_cleanup":
                assert args == {}
                data = {"complete": fake.failure != "inventory", "isStale": fake.failure == "stale",
                        "items": ["foreign"] if fake.failure == "busy" else []}
            elif name == "unity_console_capture_list":
                data = {"activeCount": 1 if fake.failure == "capture" else 0}
            elif name == "unity_test_list":
                assert args == c.SELECTORS
                data = {"selectionValid": True, "matchedCount": 1, "tests": [c.LEAF],
                        "selectionDomain": "before", "selectionSnapshotId": "selection",
                        "selectedTests": [{"fullName": c.LEAF, "assembly": "UPilot.Editor.Tests.dll"}]}
                if fake.failure == "selection":
                    data["tests"].append("foreign-leaf")
                elif fake.failure == "assembly":
                    data["selectedTests"][0]["assembly"] = "foreign.dll"
            elif name == "unity_ensure_ready":
                assert args["editorModeControl"] == "manual"
                data = {"ready": fake.failure != "ready", "blocked": False,
                        "contextAuthoritative": True, "contextStale": False}
            elif name == "unity_upilot_acceptance_run":
                fake.starts += 1
                assert args["stopActiveCaptures"] is False and args["timeoutSec"] == 90
                assert args["expectedSelectionSnapshotId"] == "selection"
                fake.value = {"taskId": "owned", "requestOrigin": {"status": "self_declared", **c.A},
                              "projectPath": str(c.PROJECT), "durable": True, "startedAt": 1000, "deadlineAt": 91000,
                              "runGuid": "", "terminal": False, "status": "queued", "cancelRequested": False}
                # Native admission has startedAt, NOT a createdAt requirement; runGuid is initially empty.
                assert "createdAt" not in fake.value
                if fake.failure == "ack_lost":
                    raise TimeoutError("secret-transport-text")
                if fake.failure == "no_id":
                    fake.value["taskId"] = ""
                if fake.failure == "origin":
                    fake.value["requestOrigin"] = {"status": "unknown"}
                data = fake.value
            elif name == "unity_task_status":
                assert args["taskId"] == "owned"
                fake.reads += 1
                fake.value["runGuid"] = "run"
                if (len(fake.sessions) == 3 or fake.reads >= 3 or fake.failure == "ended_before" or
                        fake.failure == "ended_after" and fake.sessions[1].closed or
                        fake.failure in ("origin", "starter_exit")):
                    fake.terminal()
                if fake.failure == "run_changed" and fake.sessions[1].closed:
                    fake.value["runGuid"] = "new"
                elif fake.failure == "deadline" and fake.sessions[1].closed:
                    fake.value["deadlineAt"] += 1
                if fake.failure == "terminal_changed" and self.index == 2:
                    fake.value["endedAt"] = 2001
                if fake.failure == "observation_error":
                    raise TimeoutError("secret-transport-text")
                data = fake.value
            elif name == "unity_test_status":
                if not fake.value:
                    data = {"isRunning": fake.failure == "runner_busy", "cleanupPending": fake.failure == "runner_cleanup"}
                else:
                    data = {"runGuid": "run", "currentTest": c.LEAF, "isRunning": True, "terminal": False}
                    if fake.failure == "wrong_runner" and fake.sessions[1].closed:
                        data["runGuid"] = "foreign"
            elif name == "unity_test_results":
                assert args == {"runGuid": "run"}
                data = fake.runner
            else:
                raise AssertionError(name)
            return SimpleNamespace(structuredContent={"ok": True, "data": copy.deepcopy(data)}, content=[], isError=False)


def execute(client, monkeypatch, failure=None):
    fake = FakeAcceptance(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report = {"calls": []}
    error = None
    try:
        asyncio.run(client.exercise(8011, report))
    except BaseException as exc:
        error = exc
    return fake, report, error


def test_native_task_survives_actual_client_exit_and_reconnect(client, monkeypatch):
    fake, report, error = execute(client, monkeypatch)
    assert error is None
    assert fake.starts == 1 and len(fake.sessions) == 3 and all(s.closed for s in fake.sessions)
    assert report["result"] == "observed" and report["summary"]["verified"] is True
    assert report["beforeDisconnect"]["runner"]["isRunning"] is True
    assert report["afterDisconnect"]["runner"]["isRunning"] is True
    assert report["originalTaskObservedTerminal"] is True
    assert not any(name in {"unity_task_start", "unity_task_cancel", "unity_service_restart", "unity_compile",
                           "unity_test_cancel", "unity_console_capture_stop"} for _, name, _, _ in fake.calls)
    assert report["lastObservedTask"]["requestOrigin"] == {"status": "self_declared", **client.A}


@pytest.mark.parametrize("failure", ["inventory", "stale", "busy", "capture", "runner_busy", "runner_cleanup",
                                      "selection", "assembly", "ready", "loaded", "project"])
def test_preflight_refuses_foreign_incomplete_or_unverified_inputs(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError)
    assert fake.starts == 0 and report["result"] == "unknown"


@pytest.mark.parametrize("failure", ["ack_lost", "no_id", "origin", "ended_before", "ended_after", "wrong_runner",
    "identity", "run_changed", "deadline", "cleanup", "cancelled", "failed_leaf", "resource", "terminal_changed",
    "missing_file", "hash", "bytes", "outside_path", "summary_taskId", "summary_runGuid", "summary_deadlineAt",
    "summary_expectedProject", "summary_testNames", "summary_expectedSelectionDomain",
    "summary_expectedSelectionSnapshotId", "summary_acceptancePassed", "summary_stoppedConsoleCaptures", "starter_exit"])
def test_unverified_live_or_terminal_evidence_never_replays_start(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert error is not None and fake.starts == 1
    assert report["result"] == "unknown" and report["firstError"]
    assert "secret-transport-text" not in json.dumps(report)
    if failure in ("ack_lost", "no_id"):
        assert report["ownedTaskIds"] == []
    if failure == "starter_exit":
        assert report["originalTaskObservedTerminal"] is True
        assert report["firstError"]["type"] == "TimeoutError"


def test_business_first_error_and_observation_cleanup_error_remain_separate(client, monkeypatch):
    fake, report, error = execute(client, monkeypatch, "observation_error")
    assert isinstance(error, TimeoutError) and fake.starts == 1
    assert report["firstError"]["type"] == "TimeoutError"
    assert report["cleanupError"]["type"] == "TimeoutError"
    assert report["originalTaskObservedTerminal"] is False


@pytest.mark.parametrize("argv", [[], ["--execute", "--port", "0"], ["--execute", "--port", "65536"]])
def test_cli_is_explicit_opt_in_and_local_port_bounded(client, argv):
    with pytest.raises(SystemExit) as exc:
        asyncio.run(client.main(argv))
    assert exc.value.code == 2


def test_main_archive_is_redacted_and_exit_failure_never_claims_success(client, monkeypatch, capsys):
    saved = []
    async def failed(port, report):
        report.update(result="observed", firstError={"type": "FirstFailure", "message": "original"},
                      ownerToken="private-token", echo="private-token", cleanupError={"type": "CleanupFailure"})
        raise TimeoutError("secret-transport-text")
    def write(path, value):
        saved.append(value)
        return path, 1, "hash"
    monkeypatch.setattr(client, "exercise", failed)
    monkeypatch.setattr(client, "_write_observation", write)
    assert asyncio.run(client.main(["--execute"])) == 1
    assert saved[0]["result"] == "unknown" and saved[0]["firstError"]["type"] == "FirstFailure"
    assert saved[0]["cleanupError"]["type"] == "CleanupFailure"
    assert "private-token" not in json.dumps(saved) and "secret-transport-text" not in json.dumps(saved)
    assert json.loads(capsys.readouterr().out)["result"] == "unknown"


def test_one_fixed_observation_budget_not_renewed_on_reconnect(client, monkeypatch):
    fake = FakeAcceptance(client)
    clock, deadlines = [100.0], []
    @asynccontextmanager
    async def timeout_at(deadline):
        deadlines.append(deadline)
        yield
    async def sleep(seconds):
        clock[0] += seconds
    original = fake.Session.call_tool
    async def expire_on_admission(session, *args, **kwargs):
        data = await original(session, *args, **kwargs)
        if fake.starts:
            clock[0] = 281.0
        return data
    monkeypatch.setattr(fake.Session, "call_tool", expire_on_admission)
    monkeypatch.setattr(client, "connect", fake.connect)
    monkeypatch.setattr(client, "asyncio", SimpleNamespace(get_running_loop=lambda: SimpleNamespace(time=lambda: clock[0]),
        timeout_at=timeout_at, sleep=sleep))
    report = {"calls": []}
    with pytest.raises(client.CallPreflightError, match="budget expired"):
        asyncio.run(client.exercise(8011, report))
    assert deadlines == [280.0] and fake.starts == 1 and len(fake.sessions) == 2
    assert report["originalTaskObservedTerminal"] is False
