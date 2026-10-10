"""D19 cancellation client contracts; no network, Unity or service maintenance."""
import asyncio
import copy
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
    module = importlib.import_module("p1_request_origin_cancel_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p0_reload_evidence"), "PROJECT", tmp_path)
    return module


def fresh_status(project):
    from test_reload_evidence_client import status
    data = status(project)
    data["aiServiceMaintenance"] = {"configError": "", "autoHardStopOnSoftFailure": False}
    return data


class FakeCancellation:
    """Finite exact Task receipts, real tool signatures and an origin-unknown preview."""
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls, self.sessions, self.tasks = [], [], {}
        self.starts = self.previews = self.applies = self.late_requests = 0
        self.reads = {}

    @asynccontextmanager
    async def connect(self, port):
        session = self.Session(self, len(self.sessions))
        self.sessions.append(session)
        try:
            yield session
        finally:
            session.closed = True

    def terminal(self, task_id, cancelled=False):
        value = self.tasks[task_id]
        value.update(status="cancelled" if cancelled else "completed", terminal=True, endedAt=2000,
                     cancelRequested=cancelled, cleanupSucceeded=True,
                     tests={"status": "completed", "total": 1, "passed": 1, "failed": 0,
                            "skipped": 0, "cleanupSucceeded": True, "resultAuthoritative": True})
        if self.failure == "cleanup" and cancelled:
            value["cleanupSucceeded"] = False
        if self.failure == "cancel_unknown" and cancelled:
            value["status"] = "aborted"
        return value

    class Session:
        def __init__(self, fake, index):
            self.fake, self.index, self.closed = fake, index, False

        async def call_tool(self, proxy, request, *, meta):
            fake = self.fake
            name, args = request["toolName"], request["args"]
            from upilot_mcp.mcp_tools import status_tools, task_tools, test_tools
            model = next((getattr(module, name) for module in (status_tools, task_tools, test_tools)
                          if hasattr(module, name)), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call"
            fake.calls.append((self.index, name, copy.deepcopy(args), copy.deepcopy(meta)))
            origin = next(iter(meta.values()))
            data = {}
            if name == "unity_mcp_status":
                data = fresh_status(fake.client.PROJECT)
                if fake.failure == "auto_enabled":
                    data["aiServiceMaintenance"]["autoHardStopOnSoftFailure"] = True
                elif fake.failure == "auto_missing":
                    data["aiServiceMaintenance"].pop("autoHardStopOnSoftFailure")
                elif fake.failure == "config_error":
                    data["aiServiceMaintenance"]["configError"] = "unreadable"
                elif fake.failure == "identity" and self.index == 0:
                    data["executionState"]["domainGeneration"] = "other"
                elif fake.failure == "auto_after_preview" and fake.previews:
                    data["aiServiceMaintenance"]["autoHardStopOnSoftFailure"] = True
            elif name == "unity_queue_cleanup":
                if not args:
                    data = {"complete": fake.failure != "inventory", "isStale": fake.failure == "stale",
                            "items": ["foreign"] if fake.failure == "busy" else [],
                            "authorization": {"allowed": fake.failure != "grant"}}
                elif args["dryRun"]:
                    from upilot_mcp.domain.queue_service import item
                    fake.previews += 1
                    # The real safety snapshot omits origin; this must NOT be invented in a fixture.
                    target = item("Task", args["targetId"], {"status": "running"}, "cancel")
                    assert target["requestOrigin"]["status"] == "unknown"
                    data = {"dryRun": True, "projectPath": str(fake.client.PROJECT), "target": target,
                            "allowed": fake.failure != "preview_denied", "confirmToken": "private-confirmation"}
                    if fake.failure == "wrong_target":
                        target["id"] = "foreign"
                    if fake.failure == "budget_after_preview":
                        fake.clock = 196.0
                else:
                    fake.applies += 1
                    assert args["confirmToken"] == "private-confirmation"
                    assert args["expectedProjectPath"] == str(fake.client.PROJECT)
                    if fake.failure == "apply_ack_lost":
                        fake.terminal(args["targetId"], cancelled=True)
                        raise TimeoutError("Unknown dispatch receipt")
                    value = fake.tasks[args["targetId"]]
                    value.update(cancelRequested=True, status="ending")
                    data = copy.deepcopy(value)
                    fake.terminal(args["targetId"], cancelled=True)
            elif name == "unity_console_capture_list":
                data = {"activeCount": 1 if fake.failure == "capture" else 0}
            elif name == "unity_test_list":
                data = {"selectionValid": fake.failure != "selection", "matchedCount": 1,
                        "tests": [fake.client.DELAYED_TEST], "selectionDomain": "before",
                        "selectionSnapshotId": "exact-delayed"}
            elif name == "unity_ensure_ready":
                data = {"ready": fake.failure != "readiness", "blocked": False,
                        "contextAuthoritative": True, "contextStale": False}
            elif name == "unity_task_start":
                fake.starts += 1
                task_id = "owned-" + str(fake.starts)
                value = {"taskId": task_id, "projectPath": str(fake.client.PROJECT), "durable": True,
                         "createdAt": 1000, "deadlineAt": 71000, "terminal": False,
                         "status": "running", "runGuid": "run-" + task_id, "cancelRequested": False,
                         "requestOrigin": {"status": "self_declared", **origin}}
                fake.tasks[task_id] = value
                if fake.failure == "start_ack_lost":
                    raise TimeoutError("Unknown Start receipt")
                data = copy.deepcopy(value)
                if fake.failure == "bad_start_origin":
                    data["requestOrigin"]["chatId"] = "other"
            elif name == "unity_task_status":
                task_id = args["taskId"]
                fake.reads[task_id] = fake.reads.get(task_id, 0) + 1
                if task_id == "owned-2" and fake.reads[task_id] >= 3:
                    fake.terminal(task_id)
                if task_id == "owned-1" and fake.failure in {
                        "ended_before_cancel", "bad_start_origin", "preview_denied", "wrong_target",
                        "auto_after_preview"} and (fake.reads[task_id] >= 2 or fake.failure == "ended_before_cancel"):
                    fake.terminal(task_id)
                data = copy.deepcopy(fake.tasks[task_id])
                if fake.failure == "old_mutated" and task_id == "owned-1" and fake.starts == 2:
                    data["endedAt"] += 1
            elif name == "unity_test_status":
                task_id = "owned-" + str(fake.starts)
                data = {"runGuid": fake.tasks[task_id]["runGuid"], "currentTest": fake.client.DELAYED_TEST,
                        "isRunning": True, "terminal": False, "cancelRequested": False, "startedAt": 1000}
            elif name == "unity_test_results":
                data = {"runGuid": args["runGuid"], "terminal": True, "cancelAccepted": True,
                        "cancelRequested": True, "cleanupSucceeded": True, "cleanupResourcesReleased": True,
                        "cleanupPending": False, "unresolvedResources": [], "resultAuthoritative": True,
                        "endedAt": 2000}
                if fake.failure == "natural_end":
                    data["endedAt"] = 31000
                elif fake.failure == "missing_resources":
                    data["cleanupResourcesReleased"] = False
                elif fake.failure == "cancel_rejected":
                    data["cancelAccepted"] = False
            elif name == "unity_task_cancel":
                fake.late_requests += 1
                assert args["taskId"] == "owned-1"
                data = {"taskId": args["taskId"], "status": "not_found", "changed": False}
                if fake.failure == "late_effect":
                    fake.tasks["owned-2"]["cancelRequested"] = True
                elif fake.failure == "late_changed":
                    data["changed"] = True
            else:
                raise AssertionError(name)
            return SimpleNamespace(isError=False, structuredContent={"ok": True, "data": copy.deepcopy(data)})


def execute(client, monkeypatch, failure=None):
    fake = FakeCancellation(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report = {"calls": []}
    error = None
    try:
        asyncio.run(client.exercise(8011, report))
    except (client.CallPreflightError, TimeoutError) as exc:
        error = exc
    assert len(fake.sessions) == 2 and all(session.closed for session in fake.sessions)
    return fake, report, error


def test_two_clients_cancel_once_then_ignore_old_id_with_real_tool_signatures(client, monkeypatch):
    fake, report, error = execute(client, monkeypatch)
    assert error is None
    assert (fake.starts, fake.previews, fake.applies, fake.late_requests) == (2, 1, 1, 1)
    assert report["result"] == "observed"
    assert report["cancellationVerified"] is True and report["lateOldIdVerified"] is True
    assert report["allOwnedTasksObservedTerminal"] is True
    assert "private-confirmation" not in json.dumps(report)
    preview_args = next(args for _, name, args, _ in fake.calls if name == "unity_queue_cleanup" and args.get("dryRun"))
    apply_args = next(args for _, name, args, _ in fake.calls if name == "unity_queue_cleanup" and args.get("dryRun") is False)
    assert all(apply_args[key] == preview_args[key] for key in ("targetType", "targetId", "action", "reason"))
    assert all(index == 0 for index, name, _, _ in fake.calls if name == "unity_task_cancel")
    assert all(index == 1 for index, name, _, _ in fake.calls if name == "unity_task_start")


@pytest.mark.parametrize("failure", ["auto_enabled", "auto_missing", "config_error", "identity", "inventory",
                                      "stale", "busy", "grant", "capture", "selection", "readiness"])
def test_unsafe_preflight_never_starts_or_cancels(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError)
    assert (fake.starts, fake.previews, fake.applies, fake.late_requests) == (0, 0, 0, 0)
    assert report["result"] == "unknown"


@pytest.mark.parametrize("failure", ["preview_denied", "wrong_target", "auto_after_preview", "ended_before_cancel"])
def test_no_apply_for_invalid_preview_changed_safety_or_ended_leaf(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError)
    assert fake.starts == 1 and fake.applies == fake.late_requests == 0
    assert report["result"] == "unknown"


@pytest.mark.parametrize("failure", ["start_ack_lost", "apply_ack_lost", "bad_start_origin", "cleanup",
                                      "cancel_unknown", "natural_end", "missing_resources", "cancel_rejected",
                                      "late_effect", "late_changed", "old_mutated"])
def test_unknown_or_unverified_effect_is_not_success_and_never_replayed(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert error is not None
    assert fake.starts <= 2 and fake.applies <= 1 and fake.late_requests <= 1
    assert report["result"] == "unknown" and report["firstError"]
    if failure == "start_ack_lost":
        assert fake.starts == 1 and fake.previews == 0 and report["ownedTaskIds"] == []
    if failure == "bad_start_origin":
        assert report["ownedTaskIds"] == ["owned-1"] and report["cleanupErrors"]
    if failure == "apply_ack_lost":
        assert report["firstError"]["type"] == "TimeoutError"
        assert report["allOwnedTasksObservedTerminal"] is True


@pytest.mark.parametrize("field,value", [("deadlineAt", 71001), ("runGuid", "other"),
                                        ("requestOrigin", {"status": "unknown"}), ("projectPath", "other")])
def test_frozen_fields_cannot_change(client, field, value):
    origin = {"clientId": "A", "chatId": "a"}
    baseline = {"taskId": "owned", "projectPath": str(client.PROJECT), "durable": True,
                "createdAt": 1, "deadlineAt": 2, "runGuid": "run", "requestOrigin": {"status": "self_declared", **origin}}
    data = {**baseline, field: value}
    with pytest.raises(client.CallPreflightError):
        client.check_frozen(data, baseline, origin)


@pytest.mark.parametrize("argv", [[], ["--execute", "--port", "0"], ["--execute", "--port", "65536"]])
def test_cli_requires_explicit_execution_and_valid_local_port(client, argv):
    with pytest.raises(SystemExit) as exc:
        asyncio.run(client.main(argv))
    assert exc.value.code == 2


def test_main_keeps_first_error_separate_from_cleanup_and_unknown_on_exit_failure(client, monkeypatch, capsys):
    saved = []
    async def failed(port, report):
        report.update(result="observed", firstError={"type": "OriginalFailure", "message": "original"},
                      cleanupErrors=[{"type": "CleanupFailure"}])
        raise TimeoutError("Transport exit failed")
    def write(path, value):
        saved.append(value)
        return path, 1, "hash"
    monkeypatch.setattr(client, "exercise", failed)
    monkeypatch.setattr(client, "_write_observation", write)
    assert asyncio.run(client.main(["--execute"])) == 1
    assert saved[0]["result"] == "unknown"
    assert saved[0]["firstError"]["type"] == "OriginalFailure"
    assert saved[0]["cleanupErrors"][0]["type"] == "CleanupFailure"
    assert json.loads(capsys.readouterr().out)["result"] == "unknown"


@pytest.mark.parametrize("expire_after_preview", [False, True])
def test_original_budget_is_not_renewed_and_never_triggers_another_start_or_cancel(client, monkeypatch, expire_after_preview):
    fake = FakeCancellation(client, "budget_after_preview" if expire_after_preview else None)
    fake.clock = 100.0
    reads = 0
    def clock():
        nonlocal reads
        reads += 1
        return fake.clock if expire_after_preview or reads == 1 else 196.0
    monkeypatch.setattr(client, "time", SimpleNamespace(monotonic=clock))
    monkeypatch.setattr(client, "connect", fake.connect)
    report = {"calls": []}
    with pytest.raises(client.CallPreflightError, match="budget expired"):
        asyncio.run(client.exercise(8011, report))
    assert fake.starts == int(expire_after_preview)
    assert fake.previews == int(expire_after_preview) and fake.applies == fake.late_requests == 0
    assert report["result"] == "unknown" and report["allOwnedTasksObservedTerminal"] is False


@pytest.mark.parametrize("section,field,value", [
    ("executionState", "isStale", True), ("executionState", "authoritative", False),
    ("executionState", "domainGeneration", "new"), ("session", "processCreatedAt", 999),
])
def test_live_identity_checks_allow_busy_but_never_stale_or_changed(client, section, field, value):
    data = fresh_status(client.PROJECT)
    identity = client.check_safety(data)
    data["executionState"]["ready"] = False
    assert client.check_safety(data, identity) == identity
    data[section][field] = value
    with pytest.raises(client.CallPreflightError):
        client.check_safety(data, identity)
