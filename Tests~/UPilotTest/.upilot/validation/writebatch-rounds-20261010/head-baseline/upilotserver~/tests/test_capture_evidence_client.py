"""Capture client contracts using two fake transports and owned tmp artifacts only."""
import asyncio
from contextlib import asynccontextmanager
import copy
import hashlib
import importlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from test_reload_evidence_client import status


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    module = importlib.import_module("p1_capture_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p0_reload_evidence"), "PROJECT", tmp_path)
    return module


def write_artifacts(directory, records, rotated=False):
    directory.mkdir(parents=True, exist_ok=True)
    blobs = [b"".join((json.dumps(record) + "\n").encode() for record in records)]
    if rotated:
        split = len(records) // 2
        blobs = [b"".join((json.dumps(record) + "\n").encode() for record in group)
                 for group in (records[:split], records[split:])]
    for index, blob in enumerate(blobs):
        (directory / ("console.jsonl" if index == 0 else "console.001.jsonl")).write_bytes(blob)
    manifest = {"sessionId": "owned", "active": False, "fileBytes": sum(map(len, blobs)),
                "segmentCount": len(blobs), "sha256": hashlib.sha256(b"".join(blobs)).hexdigest(),
                "totalCount": len(records), "nextSequence": len(records), "lastError": "", "droppedCount": 0}
    for name in ("session.json", "summary.json"):
        (directory / name).write_text(json.dumps(manifest))
    return manifest


@pytest.mark.parametrize("rotated", [False, True])
def test_artifacts_recompute_actual_bytes_sequence_and_hash(client, tmp_path, rotated):
    records = [{"sequence": n, "message": "own"} for n in range(4)]
    manifest = write_artifacts(tmp_path, records, rotated)
    evidence, actual = client.verify_artifacts(tmp_path, manifest, "owned")
    assert actual == records and evidence["segmentCount"] == (2 if rotated else 1)


@pytest.mark.parametrize("damage", ["missing", "extra", "tamper", "summary", "sequence"])
def test_missing_extra_corrupt_or_reordered_artifacts_cannot_pass(client, tmp_path, damage):
    records = [{"sequence": 0}, {"sequence": 1}]
    if damage == "sequence":
        records.reverse()
    manifest = write_artifacts(tmp_path, records, True)
    if damage == "missing":
        (tmp_path / "console.001.jsonl").unlink()  # owned tmp fixture only
    elif damage == "extra":
        (tmp_path / "console.002.jsonl").write_bytes(b"")
    elif damage == "tamper":
        (tmp_path / "console.001.jsonl").write_bytes(b'{"sequence":1,"changed":true}\n')
    elif damage == "summary":
        (tmp_path / "summary.json").write_text(json.dumps({**manifest, "active": True}))
    with pytest.raises(client.CallPreflightError):
        client.verify_artifacts(tmp_path, manifest, "owned")


def test_zero_logs_require_a_real_zero_byte_file(client, tmp_path):
    manifest = write_artifacts(tmp_path, [])
    evidence, records = client.verify_artifacts(tmp_path, manifest, "owned")
    assert records == [] and evidence["fileBytes"] == 0
    (tmp_path / "console.jsonl").unlink()
    with pytest.raises(client.CallPreflightError):
        client.verify_artifacts(tmp_path, manifest, "owned")


class FakeCapture:
    """Implements public envelopes; signatures bind the real MCP functions."""
    def __init__(self, project, *, failure=None, rotated=False):
        self.project, self.failure, self.rotated = project, failure, rotated
        self.calls, self.clients, self.records = [], [], []
        self.active = False
        self.legitimate_stops = 0
        self.manifest = None
        self.token = "opaque-private-owner-credential"
        self.frozen = None
        self.reloaded = False
        self.reload_requests = 0
        self.reload_gap_observed = False

    @asynccontextmanager
    async def connect(self, port):
        session = self.Session(self, len(self.clients))
        self.clients.append(session)
        yield session

    class Session:
        def __init__(self, fixture, index):
            self.fixture, self.index = fixture, index

        async def call_tool(self, proxy, request):
            fixture = self.fixture
            name, args = request["toolName"], request["args"]
            fixture.calls.append((self.index, name, copy.deepcopy(args)))
            from upilot_mcp.mcp_tools import status_tools, reflection_tools, task_tools, resource_tools
            model = next((getattr(module, name, None) for module in
                          (status_tools, reflection_tools, task_tools, resource_tools)
                          if getattr(module, name, None) is not None), None)
            assert model is not None, name
            inspect.signature(model).bind(**args)
            assert proxy == "unity_tool_call"
            data = {}
            if name == "unity_mcp_status":
                data = status(fixture.project)
                if fixture.failure == "split-identity" and self.index == 1:
                    data["session"]["sessionId"] = "another-bridge"
                if fixture.reloaded:
                    data["executionState"]["domainGeneration"] = "after"
                    data["session"]["sessionId"] = "bridge-after"
                    if fixture.failure == "changed-lifetime":
                        data["runtimeIdentity"]["mcpServer"]["processId"] += 1
                    if fixture.failure == "reload-gap" and not fixture.reload_gap_observed:
                        fixture.reload_gap_observed = True
                        data["connected"] = False
            elif name == "unity_queue_cleanup":
                data = {"complete": True, "items": []}
            elif name == "unity_console_capture_list":
                data = {"activeCount": 0}
            elif name == "unity_ensure_ready":
                data = {"ready": fixture.failure != "not-ready", "blocked": fixture.failure == "not-ready",
                        "contextAuthoritative": True, "contextStale": False}
            elif name == "unity_tools_find":
                data = {"tools": [{"name": "unity_console_write"}] if fixture.failure == "dedicated-writer" else []}
                if fixture.failure == "dedicated-reload" and args["query"] == "reload":
                    data = {"tools": [{"name": "unity_scripts_reload"}]}
            elif name == "unity_reflection_find":
                data = {"methods": [] if fixture.failure == "missing-reload-entry" else [{
                    "typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload",
                    "returnType": "System.Void", "isStatic": True, "parameters": [],
                }]}
            elif name == "unity_console_capture_start":
                fixture.directory = Path(args["path"])
                fixture.directory.mkdir(parents=True)
                fixture.active = True
                if fixture.failure == "lost-start":
                    raise ConnectionError("lost start")
                data = {"ok": True, "ownerToken": fixture.token, "session": {"sessionId": "owned", "active": True}}
                if fixture.failure == "missing-token":
                    data.pop("ownerToken")
            elif name == "unity_console_capture_attach":
                if fixture.failure == "attach":
                    raise RuntimeError("business first error")
                data = {"attachmentId": "own-attachment", "sessionId": "owned", "readOnlyAttachment": True,
                        "rangeClosed": False, "fromSequence": 0, "toSequence": len(fixture.records)}
            elif name == "unity_console_capture_stop":
                if args.get("ownerToken") != fixture.token:
                    return SimpleNamespace(isError=False, structuredContent={"ok": False, "error": {"code": "OWNER_MISMATCH"}})
                fixture.legitimate_stops += 1
                fixture.active = False
                if fixture.failure in {"lost-stop", "cleanup"}:
                    raise ConnectionError("lost stop")
                if fixture.manifest is None:
                    fixture.manifest = write_artifacts(fixture.directory, fixture.records, fixture.rotated)
                data = {"ok": True, "session": fixture.manifest}
            elif name == "unity_console_capture_status":
                data = {"ok": True, "session": {"sessionId": "owned", "active": fixture.active,
                        "nextSequence": len(fixture.records), "lastError": ""}}
                if fixture.reloaded and fixture.failure == "reset-sequence":
                    data["session"]["nextSequence"] = 0
            elif name == "unity_reflection_call":
                if args["methodName"] == "RequestScriptReload":
                    fixture.reload_requests += 1
                    fixture.reloaded = fixture.failure != "no-reload"
                    if fixture.failure == "lost-reload":
                        raise ConnectionError("Reload response lost after dispatch")
                else:
                    fixture.records.append({"sequence": len(fixture.records), "message": args["parameters"][0]})
            elif name == "unity_console_capture_detach":
                if fixture.frozen is None:
                    fixture.frozen = copy.deepcopy(fixture.records)
                export = fixture.directory / "attachments" / "own-attachment.jsonl"
                export.parent.mkdir(exist_ok=True)
                raw = b"".join((json.dumps(record) + "\n").encode() for record in fixture.frozen)
                export.write_bytes(raw)
                data = {"attachmentId": "own-attachment", "sessionId": "owned", "readOnlyAttachment": True,
                        "rangeClosed": True, "fromSequence": 0, "toSequence": len(fixture.frozen), "exportComplete": True,
                        "gapCount": 0, "exportPath": str(export), "exportBytes": len(raw),
                        "exportSha256": hashlib.sha256(raw).hexdigest()}
                if fixture.failure in {"continued-export", "changed-range"}:
                    if not args.get("continuationToken"):
                        data.update(exportComplete=False, continuationToken="owned-export-next")
                    elif fixture.failure == "changed-range":
                        data["toSequence"] += 1
                if fixture.failure == "relative-export":
                    data["exportPath"] = str(export.relative_to(fixture.project))
                if fixture.failure == "export-path":
                    data["exportPath"] = str(fixture.directory / "summary.json")
            elif name == "unity_console_capture_read":
                index = int(args.get("continuationToken", "0"))
                data = {"sessionId": "owned", "logs": fixture.records[index:index+1],
                        "continuationToken": str(index + 1) if index + 1 < len(fixture.records) else ""}
            elif name != "unity_editor_delay":
                raise AssertionError(name)
            return SimpleNamespace(isError=False, structuredContent={"ok": True, "data": data})


@pytest.mark.parametrize("mode", ["ownership", "empty", "rotate"])
def test_two_clients_ownership_frozen_export_paging_and_no_secrets(client, monkeypatch, tmp_path, mode):
    fixture = FakeCapture(tmp_path, rotated=mode == "rotate")
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    asyncio.run(client.exercise(8011, "nonce", mode, report))
    assert report["result"] == "observed" and len(fixture.clients) == 2
    assert fixture.legitimate_stops == 2  # second stop only after verified first stop
    assert fixture.token not in json.dumps(report)
    assert not fixture.active
    stops = [(index, args) for index, name, args in fixture.calls if name == "unity_console_capture_stop"]
    assert [index for index, _ in stops] == [1, 1, 0, 0]
    if mode != "empty":
        assert len(fixture.frozen) == len(fixture.records) - 1


@pytest.mark.parametrize("failure,stops", [("lost-start", 0), ("attach", 1), ("lost-stop", 1), ("export-path", 2)])
def test_uncertain_nonidempotent_actions_never_replayed(client, monkeypatch, tmp_path, failure, stops):
    fixture = FakeCapture(tmp_path, failure=failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    with pytest.raises((ConnectionError, RuntimeError, client.CallPreflightError)):
        asyncio.run(client.exercise(8011, "nonce", "ownership", report))
    assert sum(name == "unity_console_capture_start" for _, name, _ in fixture.calls) == 1
    assert fixture.legitimate_stops == stops
    assert fixture.token not in json.dumps(report)
    assert report["result"] == "unknown"


def test_cleanup_error_does_not_replace_original_business_error(client, monkeypatch, tmp_path):
    fixture = FakeCapture(tmp_path, failure="cleanup")
    original_call = fixture.Session.call_tool
    async def call(session, name, args):
        if args["toolName"] == "unity_console_capture_attach":
            raise RuntimeError("business first error")
        return await original_call(session, name, args)
    monkeypatch.setattr(fixture.Session, "call_tool", call)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    with pytest.raises(RuntimeError, match="business first error"):
        asyncio.run(client.exercise(8011, "nonce", "ownership", report))
    assert fixture.legitimate_stops == 1 and report["cleanupError"]
    assert fixture.token not in json.dumps(report)


@pytest.mark.parametrize("failure", ["not-ready", "dedicated-writer"])
def test_preflight_refusal_never_starts_capture_or_falls_back(client, monkeypatch, tmp_path, failure):
    fixture = FakeCapture(tmp_path, failure=failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.exercise(8011, "nonce", "ownership", {"calls": [], "result": "unknown"}))
    assert not any(name in {"unity_console_capture_start", "unity_reflection_call"}
                   for _, name, _ in fixture.calls)


def test_missing_owner_receipt_retains_identity_without_stop_or_replay(client, monkeypatch, tmp_path):
    fixture = FakeCapture(tmp_path, failure="missing-token")
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    with pytest.raises(client.CallPreflightError, match="ownership receipt"):
        asyncio.run(client.exercise(8011, "nonce", "ownership", report))
    assert report["ownedSessionId"] == "owned" and report["cleanupError"]
    assert fixture.active and fixture.legitimate_stops == 0
    assert sum(name == "unity_console_capture_start" for _, name, _ in fixture.calls) == 1
    assert not any(name == "unity_console_capture_stop" for _, name, _ in fixture.calls)


@pytest.mark.parametrize("changed", [False, True])
def test_export_continuation_preserves_exact_frozen_range(client, monkeypatch, tmp_path, changed):
    fixture = FakeCapture(tmp_path, failure="changed-range" if changed else "continued-export")
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    if changed:
        with pytest.raises(client.CallPreflightError, match="frozen attachment range"):
            asyncio.run(client.exercise(8011, "nonce", "ownership", report))
        assert fixture.legitimate_stops == 1  # finally cleans only its own session
    else:
        asyncio.run(client.exercise(8011, "nonce", "ownership", report))
        assert report["result"] == "observed" and fixture.legitimate_stops == 2
    detaches = [args for _, name, args in fixture.calls if name == "unity_console_capture_detach"]
    assert len(detaches) == 2 and detaches[1]["continuationToken"] == "owned-export-next"
    assert all(args["attachmentId"] == "own-attachment" for args in detaches)
    assert not fixture.active and fixture.token not in json.dumps(report)


def test_relative_export_is_resolved_from_exact_project_not_client_cwd(client, monkeypatch, tmp_path):
    fixture = FakeCapture(tmp_path, failure="relative-export")
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    asyncio.run(client.exercise(8011, "nonce", "ownership", report))
    assert report["result"] == "observed" and fixture.legitimate_stops == 2
    assert Path(report["exportFile"]["path"]) == fixture.directory / "attachments" / "own-attachment.jsonl"


@pytest.mark.parametrize("failure", [None, "lost-reload", "reload-gap"])
def test_owned_reload_mode_observes_one_request_same_capture_and_disk_continuity(client, monkeypatch, tmp_path, failure):
    fixture = FakeCapture(tmp_path, failure=failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    asyncio.run(client.exercise(8011, "nonce", "reload", report))
    assert fixture.reload_requests == 1 and fixture.legitimate_stops == 2
    assert report["result"] == "observed" and not fixture.active
    assert report["identityBefore"]["domainGeneration"] != report["identityAfter"]["domainGeneration"]
    assert report["captureSequenceAfterReload"] >= report["captureSequenceBeforeReload"] == 3
    assert report["reloadEvidence"]["postReloadMarkerSequence"] == 3
    assert report["bridgeReconnected"] is True and fixture.token not in json.dumps(report)
    assert report["reloadRequest"]["acknowledgement"] == ("unknown" if failure == "lost-reload" else "received")
    assert report["reloadObservationGaps"] == (1 if failure == "reload-gap" else 0)
    assert len(fixture.frozen) == len(fixture.records) - 1
    assert "all logs during the Reload subscription gap" in report["notCovered"]


@pytest.mark.parametrize("failure", ["split-identity", "dedicated-reload", "missing-reload-entry"])
def test_reload_preflight_refuses_before_capture_or_reload_mutation(client, monkeypatch, tmp_path, failure):
    fixture = FakeCapture(tmp_path, failure=failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.exercise(8011, "nonce", "reload", {"calls": [], "result": "unknown"}))
    assert not fixture.reload_requests and not fixture.legitimate_stops
    assert not any(name in {"unity_console_capture_start", "unity_reflection_call"} for _, name, _ in fixture.calls)


@pytest.mark.parametrize("failure,stops", [("changed-lifetime", 0), ("reset-sequence", 1)])
def test_reload_identity_and_sequence_failure_remain_unknown_without_replay(client, monkeypatch, tmp_path, failure, stops):
    fixture = FakeCapture(tmp_path, failure=failure)
    monkeypatch.setattr(client, "connect", fixture.connect)
    report = {"calls": [], "result": "unknown"}
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.exercise(8011, "nonce", "reload", report))
    assert fixture.reload_requests == 1 and fixture.legitimate_stops == stops
    assert report["result"] == "unknown" and fixture.token not in json.dumps(report)
    if failure == "changed-lifetime":
        assert report["cleanupError"] and fixture.active
    assert not any(args.get("forceStop") for _, name, args in fixture.calls if name == "unity_console_capture_stop")


def test_reload_cli_requires_explicit_reload_opt_in_before_connecting(client, monkeypatch):
    @asynccontextmanager
    async def forbidden_connect(port):
        raise AssertionError("Missing opt-in must not open a transport")
        yield
    monkeypatch.setattr(client, "connect", forbidden_connect)
    with pytest.raises(SystemExit) as error:
        asyncio.run(client.main(["--execute", "--mode", "reload"]))
    assert error.value.code == 2


def test_reload_observation_does_not_extend_expired_original_budget(client, tmp_path):
    class ForbiddenSession:
        async def call_tool(self, *args):
            raise AssertionError("Expired observation must not issue a call")
    async def observe():
        await client.observe_reload(ForbiddenSession(), client.require_status(status(tmp_path)),
                                    asyncio.get_running_loop().time() + 5, {"calls": [], "reloadObservationGaps": 0})
    with pytest.raises(client.CallPreflightError, match="fixed Reload observation"):
        asyncio.run(observe())


def test_missing_reload_witness_can_stop_only_after_fresh_same_lifetime_recheck(client, monkeypatch, tmp_path):
    fixture = FakeCapture(tmp_path, failure="no-reload")
    monkeypatch.setattr(client, "connect", fixture.connect)
    async def expired_observation(*args):
        raise client.CallPreflightError("changed domain not observed in fixed window")
    monkeypatch.setattr(client, "observe_reload", expired_observation)
    report = {"calls": [], "result": "unknown"}
    with pytest.raises(client.CallPreflightError, match="changed domain not observed"):
        asyncio.run(client.exercise(8011, "nonce", "reload", report))
    assert fixture.reload_requests == 1 and fixture.legitimate_stops == 1
    assert report["cleanupStoppedOwnedCapture"] is True and report["result"] == "unknown"
    assert not fixture.active and fixture.token not in json.dumps(report)
    own_stop = next(index for index, (who, name, args) in enumerate(fixture.calls)
                    if name == "unity_console_capture_stop" and who == 0)
    assert fixture.calls[own_stop - 1][1] == "unity_mcp_status"
