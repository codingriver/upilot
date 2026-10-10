"""Contract tests for the opt-in D19 client; no network or owned Task is started."""
import asyncio
import copy
from contextlib import asynccontextmanager
import hashlib
import inspect
import json
import importlib
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.fixture
def client(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    return importlib.import_module("p1_request_origin_evidence")


def test_client_places_labels_in_request_metadata_not_tool_arguments(client):
    origin = {"clientId": "A", "chatId": "chat-A"}
    calls = []
    class Session:
        async def call_tool(self, name, arguments, *, meta):
            calls.append((name, arguments, meta))
            return SimpleNamespace(isError=False, structuredContent={"ok": True, "data": {"taskId": "owned"}})
    report = {"calls": []}
    result = asyncio.run(client.call(Session(), "unity_task_status", {"taskId": "owned"}, origin, report))
    assert result == {"taskId": "owned"}
    assert calls == [("unity_tool_call", {"toolName": "unity_task_status", "args": {"taskId": "owned"}},
                      {client.ORIGIN_META_KEY: origin})]
    assert len(report["calls"]) == 1


@pytest.mark.parametrize("data", [
    {"taskId": "other", "requestOrigin": {"status": "self_declared", "clientId": "A", "chatId": "chat-A"}},
    {"taskId": "owned"},
    {"taskId": "owned", "requestOrigin": {"status": "unknown", "clientId": "", "chatId": ""}},
])
def test_unknown_changed_identity_or_missing_origin_is_not_evidence(client, data):
    with pytest.raises(client.CallPreflightError):
        client.check_task(data, "owned", {"clientId": "A", "chatId": "chat-A"})


def test_failed_start_response_is_not_retried(client):
    calls = []
    class Session:
        async def call_tool(self, *args, **kwargs):
            calls.append(args)
            return SimpleNamespace(isError=False, structuredContent={"ok": False, "error": {"code": "OWNED_ERROR"}})
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.call(Session(), "unity_task_start", {}, {}, {"calls": []}))
    assert len(calls) == 1


@pytest.fixture
def blocker_client(client, monkeypatch, tmp_path):
    # Import before patching: the helper imports PROJECT from this module once.
    # Otherwise its restored value is the first temporary path, leaking across tests.
    reload_client = importlib.import_module("p0_reload_evidence")
    monkeypatch.setattr(client, "PROJECT", tmp_path)
    monkeypatch.setattr(reload_client, "PROJECT", tmp_path)
    return client


class FakeBlocker:
    """Two transports, real MCP argument signatures, no network or Runner execution."""
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls, self.sessions, self.tasks = [], [], {}
        self.starts = 0
        self.status_reads = 0

    @asynccontextmanager
    async def connect(self, port):
        session = self.Session(self, len(self.sessions))
        self.sessions.append(session)
        try:
            yield session
        finally:
            session.closed = True

    class Session:
        def __init__(self, fixture, index):
            self.fixture, self.index, self.closed = fixture, index, False

        async def call_tool(self, proxy, request, *, meta):
            fixture = self.fixture
            name, args = request["toolName"], request["args"]
            from upilot_mcp.mcp_tools import status_tools, task_tools, test_tools
            model = next((getattr(module, name, None) for module in (status_tools, task_tools, test_tools)
                          if getattr(module, name, None) is not None), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call"
            fixture.calls.append((self.index, name, copy.deepcopy(args), copy.deepcopy(meta)))
            origin = meta[fixture.client.ORIGIN_META_KEY]
            result = {"ok": True, "data": {}}
            data = result["data"]
            if name == "unity_mcp_status":
                from test_reload_evidence_client import status
                data.update(status(fixture.client.PROJECT))
                if fixture.failure == "identity" and self.index == 0:
                    data["executionState"]["domainGeneration"] = "other"
                if fixture.failure == "lifecycle" and fixture.starts:
                    data["runtimeIdentity"]["mcpServer"]["processId"] = 999
            elif name == "unity_queue_cleanup":
                data.update(complete=fixture.failure != "inventory", items=[])
            elif name == "unity_console_capture_list":
                data.update(activeCount=1 if fixture.failure == "capture" else 0)
            elif name == "unity_test_list":
                data.update(selectionValid=True, matchedCount=1, tests=[fixture.client.BLOCKER_TEST],
                            selectionDomain="before", selectionSnapshotId="single-leaf")
                if fixture.failure == "selection":
                    data["matchedCount"] = 2
            elif name == "unity_ensure_ready":
                data.update(ready=True, blocked=False, contextAuthoritative=True, contextStale=False)
            elif name == "unity_task_start":
                inspect.signature(test_tools.unity_test_run).bind(**args["toolArgs"])
                fixture.starts += 1
                if fixture.failure == "A_lost" and fixture.starts == 1 or fixture.failure == "B_lost" and fixture.starts == 2:
                    raise OSError("simulated acknowledgement loss; raw text must not be persisted")
                if fixture.starts == 2 and fixture.failure != "accepted":
                    blocker = {"type": "Task", "id": "owned-A", "status": "running", "action": "cancel",
                               "requestOrigin": copy.deepcopy(fixture.tasks["owned-A"]["requestOrigin"])}
                    if fixture.failure == "wrong_blocker":
                        blocker["id"] = "another-task"
                    result = {"ok": False, "error": {"code": "TEST_TASK_ALREADY_ACTIVE", "detail": {"blocker": blocker}}}
                else:
                    task_id = "owned-A" if fixture.starts == 1 else "owned-B"
                    data.update(taskId=task_id, durable=True, projectPath=str(fixture.client.PROJECT),
                                status="queued", terminal=False,
                                requestOrigin={"status": "self_declared", **origin})
                    fixture.tasks[task_id] = copy.deepcopy(data)
            elif name == "unity_task_status":
                fixture.status_reads += 1
                assert fixture.sessions[1].closed, "A must already be disconnected"
                data.update(copy.deepcopy(fixture.tasks[args["taskId"]]))
                data.update(status="completed", terminal=True, endedAt=1000, runGuid="run-" + args["taskId"],
                            cleanupSucceeded=True, tests={"status": "completed", "total": 1, "passed": 1,
                            "failed": 0, "skipped": 0, "cleanupSucceeded": True, "resultAuthoritative": True})
                if fixture.failure == "origin":
                    data["requestOrigin"]["chatId"] = origin["chatId"]
                if fixture.failure == "terminal":
                    data["tests"]["resultAuthoritative"] = False
                if fixture.failure == "freeze" and fixture.status_reads > 1:
                    data["endedAt"] += 1
            else:
                raise AssertionError(name)
            return SimpleNamespace(isError=False, structuredContent=result)


def test_blocker_client_checks_exact_origin_after_disconnect_without_cancel_or_replay(blocker_client, monkeypatch):
    fixture = FakeBlocker(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = {"calls": []}
    asyncio.run(blocker_client.exercise_blocker(8011, report))
    assert report["result"] == "observed" and report["blockerObserved"] is True
    assert report["ownedTaskIds"] == ["owned-A"] and report["allOwnedTasksObservedTerminal"] is True
    assert fixture.starts == 2 and len(fixture.sessions) == 2
    starts = [entry for entry in fixture.calls if entry[1] == "unity_task_start"]
    assert [entry[0] for entry in starts] == [1, 0]
    assert all(entry[2]["retryCount"] == 0 and entry[2]["timeoutS"] == 20 for entry in starts)
    assert all(entry[2]["toolArgs"]["expectedSelectionSnapshotId"] == "single-leaf" for entry in starts)
    assert not any("cancel" in entry[1] or "restart" in entry[1] for entry in fixture.calls)
    assert report["blocker"]["requestOrigin"]["chatId"] == "owned-blocker-A"
    assert all(entry[3][blocker_client.ORIGIN_META_KEY]["chatId"] == "owned-blocker-B"
               for entry in fixture.calls if entry[1] == "unity_task_status")


@pytest.mark.parametrize("failure", ["identity", "inventory", "capture", "selection"])
def test_blocker_client_rejects_unsafe_preconditions_before_start(blocker_client, monkeypatch, failure):
    fixture = FakeBlocker(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    with pytest.raises(blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_blocker(8011, {"calls": []}))
    assert fixture.starts == 0


@pytest.mark.parametrize("failure", ["accepted", "wrong_blocker", "A_lost", "B_lost"])
def test_blocker_client_preserves_uncertain_start_and_observes_only_received_owned_ids(blocker_client, monkeypatch, failure):
    fixture = FakeBlocker(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = {"calls": []}
    with pytest.raises(Exception):
        asyncio.run(blocker_client.exercise_blocker(8011, report))
    assert report["result"] == "unknown" and report["blockerObserved"] is False
    expected = [] if failure == "A_lost" else ["owned-A", "owned-B"] if failure == "accepted" else ["owned-A"]
    assert report["ownedTaskIds"] == expected
    assert set(report["terminalTasks"]) == set(expected)
    assert fixture.starts == (1 if failure == "A_lost" else 2)
    assert "raw text" not in json.dumps(report)
    assert not any("cancel" in entry[1] or "restart" in entry[1] for entry in fixture.calls)


@pytest.mark.parametrize("failure", ["origin", "terminal", "lifecycle", "freeze"])
def test_blocker_client_does_not_claim_success_on_unverified_terminal_or_lifecycle(blocker_client, monkeypatch, failure):
    fixture = FakeBlocker(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = {"calls": []}
    with pytest.raises(blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_blocker(8011, report))
    assert report["result"] == "unknown" and fixture.starts == 2


@pytest.mark.parametrize("damage", ["id", "origin", "action", "status", "type", "code", "ok"])
def test_blocker_contract_requires_exact_anticipated_rejection(client, damage):
    origin = {"clientId": "A", "chatId": "chat-A"}
    reply = {"ok": False, "error": {"code": "TEST_TASK_ALREADY_ACTIVE", "detail": {"blocker": {
        "type": "Task", "id": "owned", "status": "queued", "action": "cancel",
        "requestOrigin": {"status": "self_declared", **origin}}}}}
    if damage == "code":
        reply["error"]["code"] = "UNRELATED_ERROR"
    elif damage == "ok":
        reply["ok"] = True
    else:
        reply["error"]["detail"]["blocker"]["requestOrigin" if damage == "origin" else damage] = "wrong"
    with pytest.raises(client.CallPreflightError):
        client.check_blocker(reply, "owned", origin)


def test_blocker_cli_requires_execute(client):
    with pytest.raises(SystemExit) as stopped:
        asyncio.run(client.main(["--mode", "blocker"]))
    assert stopped.value.code == 2



def test_blocker_expired_original_budget_does_not_dispatch_a_second_start(blocker_client, monkeypatch):
    fixture = FakeBlocker(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    clock = [0]
    monkeypatch.setattr(blocker_client, "time", SimpleNamespace(monotonic=lambda: clock[0], time=lambda: 0))
    original = blocker_client.call_envelope

    async def advance_after_start(*args):
        result = await original(*args)
        if args[1] == "unity_task_start":
            clock[0] = 40
        return result

    monkeypatch.setattr(blocker_client, "call_envelope", advance_after_start)
    report = {"calls": []}
    with pytest.raises(blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_blocker(8011, report))
    assert fixture.starts == 1 and report["ownedTaskIds"] == ["owned-A"]
    assert report["result"] == "unknown" and report["allOwnedTasksObservedTerminal"] is False
    assert report["businessError"]["message"] == "Original observation budget expired before B; do not start."


class FakeCapture:
    """Real tool argument signatures and private test files; no network or Unity."""
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls, self.sessions = [], []
        self.starts, self.stops, self.polls = 0, 0, 0
        self.capture = None
        self.token = "private-owned-capture-token"

    @asynccontextmanager
    async def connect(self, port):
        session = self.Session(self, len(self.sessions))
        self.sessions.append(session)
        yield session

    class Session:
        def __init__(self, fixture, index):
            self.fixture, self.index = fixture, index

        async def call_tool(self, proxy, request, *, meta):
            fixture = self.fixture
            name, args = request["toolName"], request["args"]
            from upilot_mcp.mcp_tools import status_tools, task_tools
            model = next((getattr(module, name, None) for module in (status_tools, task_tools)
                          if getattr(module, name, None) is not None), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call"
            fixture.calls.append((self.index, name, copy.deepcopy(args), copy.deepcopy(meta)))
            result = {"ok": True, "data": {}}
            data = result["data"]
            if name == "unity_mcp_status":
                from test_reload_evidence_client import status
                data.update(status(fixture.client.PROJECT))
                if fixture.failure == "identity" and self.index == 0:
                    data["executionState"]["domainGeneration"] = "other"
                if fixture.failure == "lifecycle" and fixture.starts:
                    data["runtimeIdentity"]["mcpServer"]["processId"] = 999
            elif name == "unity_queue_cleanup":
                data.update(complete=fixture.failure != "inventory", items=[])
                if fixture.capture and fixture.capture["active"]:
                    fixture.polls += 1
                    item = {"type": "Capture", "id": fixture.capture["sessionId"], "status": "active",
                            "requestOrigin": {"status": "unknown", "clientId": "", "chatId": ""}}
                    if fixture.failure in {"origin", "both"}:
                        item["requestOrigin"] = {"status": "self_declared", **meta[fixture.client.ORIGIN_META_KEY]}
                    data["items"] = [] if fixture.failure == "missing" else [item]
                    if fixture.failure == "poll_incomplete":
                        data["complete"] = False
                    if fixture.failure == "poll_cancelled":
                        raise asyncio.CancelledError(fixture.token)
            elif name == "unity_console_capture_list":
                data["activeCount"] = 1 if fixture.failure == "capture" or (
                    fixture.capture and fixture.capture["active"]) else 0
            elif name == "unity_ensure_ready":
                data.update(ready=fixture.failure != "ready", blocked=False,
                            contextAuthoritative=True, contextStale=False)
            elif name == "unity_console_capture_start":
                fixture.starts += 1
                if fixture.failure == "start_lost":
                    raise RuntimeError(fixture.token)
                directory = Path(args["path"])
                directory.mkdir(parents=True)
                fixture.capture = {"sessionId": "owned-capture", "ownerId": args["ownerId"],
                    "requestKey": args["requestKey"], "directory": str(directory), "active": True,
                    "ownerTokenSha256": hashlib.sha256(fixture.token.encode()).hexdigest(),
                    "finishedAtUtcMs": 0, "segmentCount": 1, "fileBytes": 0,
                    "sha256": hashlib.sha256(b"").hexdigest(), "totalCount": 0, "lastError": ""}
                (directory / "console.jsonl").write_bytes(b"")
                if fixture.failure == "multi":
                    first, second = b'{"sequence":0}\n', b'{"sequence":1}\n'
                    (directory / "console.jsonl").write_bytes(first)
                    (directory / "console_0001.jsonl").write_bytes(second)
                    fixture.capture.update(segmentCount=2, totalCount=2, fileBytes=len(first + second),
                                           sha256=hashlib.sha256(first + second).hexdigest())
                (directory / "session.json").write_text(json.dumps(fixture.capture), encoding="utf-8")
                data.update(ok=True, session=copy.deepcopy(fixture.capture), ownerToken=fixture.token)
                if fixture.failure == "bad_owner":
                    data["session"]["ownerId"] = "not-owned"
                if fixture.failure == "inactive":
                    data["session"]["active"] = False
            elif name == "unity_console_capture_stop":
                fixture.stops += 1
                assert self.index == 1, "Only A may stop its own Capture"
                assert args == {"sessionId": "owned-capture", "ownerToken": fixture.token, "forceStop": False}
                if fixture.failure in {"stop_lost", "both"}:
                    raise RuntimeError(fixture.token)
                fixture.capture.update(active=False, finishedAtUtcMs=1000)
                directory = Path(fixture.capture["directory"])
                for name in ("session.json", "summary.json"):
                    (directory / name).write_text(json.dumps(fixture.capture), encoding="utf-8")
                data.update(ok=True, terminal=True, session=copy.deepcopy(fixture.capture))
                if fixture.failure == "terminal":
                    data["terminal"] = False
                if fixture.failure == "hash":
                    (directory / "console.jsonl").write_bytes(b"changed")
                if fixture.failure == "segment":
                    (directory / "console.jsonl").unlink()
                if fixture.failure == "summary":
                    summary = {**fixture.capture, "requestKey": "other"}
                    (directory / "summary.json").write_text(json.dumps(summary), encoding="utf-8")
            else:
                raise AssertionError(name)
            return SimpleNamespace(isError=False, structuredContent=result)


def capture_report():
    return {"calls": [], "observationId": "c" * 32}


def test_capture_client_observes_unknown_origin_and_stops_once_with_real_file_hashes(blocker_client, monkeypatch):
    fixture = FakeCapture(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    asyncio.run(blocker_client.exercise_capture(8011, report))
    assert report["result"] == "observed" and report["captureOriginObserved"] is True
    assert report["cleanupVerified"] is True and report["captureFiles"]["verified"] is True
    assert fixture.starts == fixture.stops == 1 and len(fixture.sessions) == 2
    assert [item["poller"]["chatId"] for item in report["captureOriginObservations"]] == [
        "owned-capture-B", "owned-capture-A"]
    assert all(item["item"]["requestOrigin"]["status"] == "unknown"
               for item in report["captureOriginObservations"])
    assert report["captureFiles"]["jsonlBytes"] == 0
    assert report["captureFiles"]["combinedSha256"] == hashlib.sha256(b"").hexdigest()
    assert len(report["captureFiles"]["files"]) == 3
    for item in report["captureFiles"]["files"]:
        raw = Path(item["path"]).read_bytes()
        assert item["bytes"] == len(raw) and item["sha256"] == hashlib.sha256(raw).hexdigest()
    assert fixture.token not in json.dumps(report)
    assert not any("cancel" in item[1] or "restart" in item[1] for item in fixture.calls)
    assert all(item[2]["clearUnityConsole"] is False for item in fixture.calls if item[1].endswith("_start"))


@pytest.mark.parametrize("failure", ["identity", "inventory", "capture", "ready"])
def test_capture_client_rejects_unsafe_preconditions_without_start(blocker_client, monkeypatch, failure):
    fixture = FakeCapture(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    with pytest.raises(blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_capture(8011, capture_report()))
    assert fixture.starts == fixture.stops == 0


@pytest.mark.parametrize("failure", ["start_lost", "bad_owner"])
def test_capture_client_does_not_replay_or_stop_unknown_start(blocker_client, monkeypatch, failure):
    fixture = FakeCapture(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    with pytest.raises(Exception):
        asyncio.run(blocker_client.exercise_capture(8011, report))
    assert fixture.starts == 1 and fixture.stops == 0
    assert report["result"] == "unknown" and report["cleanupVerified"] is False
    assert report["cleanupNotAttempted"] and fixture.token not in json.dumps(report)


@pytest.mark.parametrize("failure", ["origin", "missing", "poll_incomplete", "inactive", "poll_cancelled"])
def test_capture_business_failure_still_cleans_only_received_owned_session(blocker_client, monkeypatch, failure):
    fixture = FakeCapture(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    with pytest.raises(asyncio.CancelledError if failure == "poll_cancelled" else blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_capture(8011, report))
    assert fixture.starts == fixture.stops == 1 and report["cleanupVerified"] is True
    assert report["result"] == "unknown" and report["businessError"]
    assert fixture.token not in json.dumps(report)


@pytest.mark.parametrize("failure", ["stop_lost", "terminal", "hash", "segment", "summary", "lifecycle"])
def test_capture_cleanup_requires_terminal_files_and_same_lifetime_without_retries(blocker_client, monkeypatch, failure):
    fixture = FakeCapture(blocker_client, failure)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    with pytest.raises(Exception):
        asyncio.run(blocker_client.exercise_capture(8011, report))
    assert fixture.starts == 1 and fixture.stops == (0 if failure == "lifecycle" else 1)
    assert report["result"] == "unknown" and report["cleanupVerified"] is False
    assert report["cleanupError"] and fixture.token not in json.dumps(report)


def test_capture_preserves_business_error_when_cleanup_also_fails(blocker_client, monkeypatch):
    fixture = FakeCapture(blocker_client, "both")
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    with pytest.raises(blocker_client.CallPreflightError, match="polling client"):
        asyncio.run(blocker_client.exercise_capture(8011, report))
    assert report["businessError"]["type"] == "CallPreflightError"
    assert report["cleanupError"]["type"] == "RuntimeError"
    assert fixture.starts == fixture.stops == 1 and fixture.token not in json.dumps(report)


@pytest.mark.parametrize("nonce", ["", "../escape", "g" * 32, None])
def test_capture_invalid_nonce_cannot_connect_or_create_files(blocker_client, monkeypatch, nonce):
    fixture = FakeCapture(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    with pytest.raises(blocker_client.CallPreflightError):
        asyncio.run(blocker_client.exercise_capture(8011, {"calls": [], "observationId": nonce}))
    assert fixture.sessions == []


def test_capture_does_not_reuse_existing_nonce_directory(blocker_client, monkeypatch):
    directory = blocker_client.PROJECT / "Log" / "UPilotConsole" / ("d19-origin-" + "c" * 32)
    directory.mkdir(parents=True)
    fixture = FakeCapture(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    with pytest.raises(blocker_client.CallPreflightError, match="already exists"):
        asyncio.run(blocker_client.exercise_capture(8011, capture_report()))
    assert fixture.sessions == []


def test_capture_cli_requires_execute(client):
    with pytest.raises(SystemExit) as stopped:
        asyncio.run(client.main(["--mode", "capture"]))
    assert stopped.value.code == 2



def test_capture_file_verifier_matches_ordered_multi_segment_bytes_without_editing_files(blocker_client, monkeypatch):
    fixture = FakeCapture(blocker_client, "multi")
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    report = capture_report()
    asyncio.run(blocker_client.exercise_capture(8011, report))
    files = report["captureFiles"]["files"]
    assert [Path(item["path"]).name for item in files] == [
        "session.json", "summary.json", "console.jsonl", "console_0001.jsonl"]
    before = {item["path"]: Path(item["path"]).read_bytes() for item in files}
    assert blocker_client.verify_capture_files(Path(fixture.capture["directory"]), fixture.capture,
                                              fixture.token) == report["captureFiles"]
    assert {path: Path(path).read_bytes() for path in before} == before
    assert report["captureFiles"]["combinedSha256"] == hashlib.sha256(
        before[files[2]["path"]] + before[files[3]["path"]]).hexdigest()


@pytest.mark.parametrize("damage", ["owner_hash", "receipt", "manifest", "foreign_directory", "segment_count"])
def test_capture_file_verifier_rejects_mismatched_ownership_and_receipts(blocker_client, monkeypatch, damage):
    fixture = FakeCapture(blocker_client)
    monkeypatch.setattr(blocker_client, "connect", fixture.connect)
    asyncio.run(blocker_client.exercise_capture(8011, capture_report()))
    directory = Path(fixture.capture["directory"])
    receipt = copy.deepcopy(fixture.capture)
    token = fixture.token
    if damage == "owner_hash":
        token = "wrong"
    elif damage == "receipt":
        receipt["requestKey"] = "other"
    elif damage == "manifest":
        (directory / "session.json").write_text("[]", encoding="utf-8")
    elif damage == "foreign_directory":
        directory = blocker_client.PROJECT.parent / "not-owned"
    elif damage == "segment_count":
        for name in ("session.json", "summary.json"):
            changed = {**fixture.capture, "segmentCount": 2}
            (directory / name).write_text(json.dumps(changed), encoding="utf-8")
        receipt["segmentCount"] = 2
    with pytest.raises(blocker_client.CallPreflightError):
        blocker_client.verify_capture_files(directory, receipt, token)


class FakeBridge:
    """Actual tool signatures, isolated concurrent delay and exact request correlation."""
    def __init__(self, client, failure=""):
        self.client, self.failure = client, failure
        self.calls, self.sessions = [], []
        self.started, self.finished, self.cancelled = 0, False, False
        self.release = asyncio.Event()
        self.live_reads = 0

    @asynccontextmanager
    async def connect(self, port):
        session = self.Session(self)
        self.sessions.append(session)
        try:
            yield session
        finally:
            self.release.set()  # transport fixture closes, not a product cancel route

    class Session:
        def __init__(self, fixture):
            self.fixture = fixture

        async def call_tool(self, proxy, request, *, meta):
            f = self.fixture
            name, args = request["toolName"], request["args"]
            from upilot_mcp.mcp_tools import status_tools, task_tools
            model = next((getattr(m, name, None) for m in (status_tools, task_tools)
                          if getattr(m, name, None) is not None), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call"
            origin = meta[f.client.ORIGIN_META_KEY]
            f.calls.append((name, copy.deepcopy(args), copy.deepcopy(origin)))
            result = {"ok": True, "requestId": "original-request" if name == "unity_editor_delay" else name,
                      "data": {}}
            data = result["data"]
            if name == "unity_mcp_status":
                from test_reload_evidence_client import status
                data.update(status(f.client.PROJECT))
                if f.failure == "identity" and len(f.calls) == 2:
                    data["executionState"]["domainGeneration"] = "other"
                if f.failure == "lifecycle" and f.finished:
                    data["runtimeIdentity"]["mcpServer"]["processId"] = 999
            elif name == "unity_console_capture_list":
                data["activeCount"] = 1 if f.failure == "capture" else 0
            elif name == "unity_ensure_ready":
                data.update(ready=f.failure != "ready", blocked=False,
                            contextAuthoritative=True, contextStale=False)
            elif name == "unity_editor_delay":
                assert args == {"delayMs": 15000}
                f.started += 1
                try:
                    await f.release.wait()
                    f.finished = True
                    if f.failure == "lost_ack":
                        raise OSError("private raw transport text")
                    data["state"] = "failed_delay" if f.failure == "outcome" else "delayed_15000ms"
                except asyncio.CancelledError:
                    f.cancelled = True
                    raise
            elif name == "unity_queue_cleanup":
                await asyncio.sleep(0)  # permit the concurrent original request to enter
                data.update(complete=f.failure != "inventory", items=[])
                if f.started and not f.finished:
                    f.live_reads += 1
                    items = [{"type": kind, "id": "candidate", "name": "editor.delay", "status": state,
                              "requestOrigin": {"status": "unknown", "clientId": "", "chatId": ""}}
                             for kind, state in (("Command", "sent"), ("BridgeCommand", "active"))]
                    if f.failure in ("poll_cancelled", "poll_timeout", "poll_incomplete"):
                        f.release.set()
                        if f.failure == "poll_cancelled":
                            raise asyncio.CancelledError()
                        if f.failure == "poll_timeout":
                            raise TimeoutError("private raw timeout")
                        data["complete"] = False
                    if f.failure in ("origin", "origin_cleanup"):
                        items[0]["requestOrigin"] = {"status": "self_declared", **origin}
                    elif f.failure == "ambiguous":
                        items.append({**items[0], "id": "someone-else"})
                    elif f.failure == "missing_side":
                        items.pop()
                    elif f.failure == "different_id" and f.live_reads == 2:
                        for item in items:
                            item["id"] = "someone-else"
                    elif f.failure == "early_end":
                        f.finished = True
                        items = []
                    if f.failure != "poll_incomplete":
                        data["items"] = items
                    if f.live_reads >= 2 or f.failure in {"origin", "origin_cleanup", "ambiguous", "missing_side", "early_end",
                            "poll_cancelled", "poll_timeout", "poll_incomplete"}:
                        f.release.set()
                if f.failure in ("residual", "origin_cleanup") and f.finished:
                    data["items"] = [{"type": "Task", "id": "other"}]
            elif name == "unity_operation_get":
                assert f.finished and args == {"commandId": "candidate"}
                data.update(commandId="candidate", commandName="editor.delay", terminal=True,
                            resultReported=True, completedAt="observed", errorCode="", errorMessage="",
                            dispatchCount=1, executionCount=1, mainThreadActionCount=1,
                            serverCommandObservation={"source": "server_command_record", "status": "observed",
                                "commandId": "candidate", "commandName": "editor.delay", "requestId": "original-request",
                                "serviceLifecycleId": "same", "recordStatus": "success", "sendAttempts": 1})
                if f.failure == "request_mismatch":
                    data["serverCommandObservation"]["requestId"] = "someone-else"
                elif f.failure == "send_twice":
                    data["serverCommandObservation"]["sendAttempts"] = 2
                elif f.failure == "nonterminal":
                    data["terminal"] = False
                elif f.failure == "execution_twice":
                    data["executionCount"] = 2
                elif f.failure == "tracker_error":
                    data["errorCode"] = "OTHER_FAILURE"
            else:
                raise AssertionError(name)
            return SimpleNamespace(isError=False, structuredContent=result)


def test_bridge_client_observes_two_anonymous_sides_then_correlates_original_request(client, monkeypatch):
    fixture = FakeBridge(client)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": []}
    asyncio.run(client.exercise_bridge(8011, report))
    assert len(fixture.sessions) == 2 and fixture.started == 1 and not fixture.cancelled
    assert report["result"] == "observed" and report["ownedCommandId"] == "candidate"
    assert report["bridgeOriginObserved"] is report["cleanupVerified"] is True
    obs = report["bridgeOriginObservations"]
    assert len(obs) == 2 and {x["poller"]["chatId"] for x in obs} == {"owned-bridge-A", "owned-bridge-B"}
    assert all({i["type"] for i in x["items"]} == {"Command", "BridgeCommand"} for x in obs)
    assert report["commandObservation"]["serverCommandObservation"]["requestId"] == "original-request"
    assert not any(any(word in name for word in ("cancel", "stop", "restart", "compile", "start"))
                   for name, _, _ in fixture.calls)


@pytest.mark.parametrize("failure", ["identity", "inventory", "capture", "ready"])
def test_bridge_client_rejects_preconditions_without_dispatch(client, monkeypatch, failure):
    fixture = FakeBridge(client, failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.exercise_bridge(8011, {"calls": []}))
    assert fixture.started == 0


@pytest.mark.parametrize("failure", ["origin", "ambiguous", "missing_side", "different_id", "early_end",
    "poll_cancelled", "poll_timeout", "poll_incomplete", "request_mismatch", "send_twice", "nonterminal",
    "execution_twice", "tracker_error"])
def test_bridge_failed_observation_waits_original_request_without_claiming_ownership(client, monkeypatch, failure):
    fixture = FakeBridge(client, failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": []}
    with pytest.raises((client.CallPreflightError, asyncio.CancelledError, TimeoutError)):
        asyncio.run(client.exercise_bridge(8011, report))
    assert fixture.started == 1 and fixture.finished and not fixture.cancelled
    assert report["cleanupVerified"] is True and report["bridgeOriginObserved"] is False
    assert report["ownedCommandId"] == "" and "businessError" in report and "cleanupError" not in report


@pytest.mark.parametrize("failure", ["lost_ack", "outcome", "lifecycle", "residual"])
def test_bridge_uncertain_outcome_or_cleanup_is_not_replayed_or_reported_success(client, monkeypatch, failure):
    fixture = FakeBridge(client, failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": []}
    with pytest.raises((client.CallPreflightError, OSError)):
        asyncio.run(client.exercise_bridge(8011, report))
    assert fixture.started == 1 and fixture.finished and not fixture.cancelled
    assert report["cleanupVerified"] is False and "cleanupError" in report
    assert report.get("result") != "observed"
    if failure == "lost_ack":
        assert "private" not in json.dumps(report)
        assert report["businessError"]["type"] == report["cleanupError"]["type"] == "OSError"


def test_bridge_cli_requires_explicit_execute(client):
    with pytest.raises(SystemExit) as exc:
        asyncio.run(client.main(["--mode", "bridge"]))
    assert exc.value.code == 2


@pytest.mark.parametrize("mode", ["origin", "blocker", "capture", "bridge"])
def test_client_cli_routes_one_selected_mode_after_explicit_execute(client, monkeypatch, mode):
    invoked = []
    async def run(port, report):
        invoked.append((port, report["mode"]))
        report["result"] = "observed"
    for name in ("exercise", "exercise_blocker", "exercise_capture", "exercise_bridge"):
        monkeypatch.setattr(client, name, run)
    monkeypatch.setattr(client, "_write_observation", lambda path, report: (path, 1, "fixture"))
    assert asyncio.run(client.main(["--mode", mode, "--execute"])) == 0
    assert invoked == [(8011, mode)]


def test_bridge_preserves_first_business_error_if_final_inventory_also_fails(client, monkeypatch):
    fixture = FakeBridge(client, "origin_cleanup")
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": []}
    with pytest.raises(client.CallPreflightError, match="contaminated"):
        asyncio.run(client.exercise_bridge(8011, report))
    assert fixture.started == 1 and not fixture.cancelled
    assert "contaminated" in report["businessError"]["message"]
    assert "empty queue" in report["cleanupError"]["message"]
    assert report["bridgeOriginObserved"] is report["cleanupVerified"] is False


def test_bridge_uses_two_fixed_deadlines_without_renewal_during_polling(client, monkeypatch):
    fixture = FakeBridge(client)
    monkeypatch.setattr(client, "connect", fixture.connect)
    original, deadlines = asyncio.timeout_at, []
    def observe(deadline):
        deadlines.append(deadline)
        return original(deadline)
    monkeypatch.setattr(client.asyncio, "timeout_at", observe)
    asyncio.run(client.exercise_bridge(8011, {"calls": []}))
    assert len(deadlines) == 2 and deadlines[1] - deadlines[0] == 15
    assert fixture.started == 1 and fixture.live_reads == 2
