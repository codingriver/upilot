"""V09 failure evidence client contracts; fake MCP, no live Snapshot or maintenance."""
import asyncio
import copy
import hashlib
import importlib
import inspect
import json
import os
from contextlib import asynccontextmanager, contextmanager
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.wire_ids import stringify_wire_ids

import pytest
from PIL import Image


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    module = importlib.import_module("p1_snapshot_failure_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p0_reload_evidence"), "PROJECT", tmp_path)
    return module


class FakeSnapshot:
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls, self.values, self.starts, self.reads = [], {}, 0, 0
        self.closed = False

    @asynccontextmanager
    async def connect(self, port):
        assert port == 8011
        try:
            yield self
        finally:
            self.closed = True

    def camera_inventory(self):
        return {"cameras": [], "count": 1 if self.failure == "camera_inventory" else 0}

    def check_output_before_start(self, args):
        assert not (self.client.PROJECT / args["outputDirectory"]).exists()

    def persist(self, data):
        c = self.client
        manifest = copy.deepcopy(data)
        manifest.pop("persistenceVerified", None)
        manifest.update(persistenceStatus="pending", manifestBytes=0, manifestSha256="")
        field = (self.failure or "").removeprefix("manifest_")
        if (self.failure or "").startswith("manifest_"):
            manifest[field] = "other"
        directory = c.PROJECT / data["outputDirectory"]
        directory.mkdir(parents=True)
        path = directory / "manifest.json"
        raw = json.dumps(manifest).encode()
        path.write_bytes(raw)
        data.update(manifestBytes=len(raw), manifestSha256=hashlib.sha256(raw).hexdigest())
        state = copy.deepcopy(data)
        state.pop("persistenceVerified", None)
        if (self.failure or "").startswith("state_"):
            state[self.failure.removeprefix("state_")] = "other"
        if self.failure == "non_id_numeric_string":
            state["startedAtUtcMs"] = str(state["startedAtUtcMs"])
        elif self.failure == "wire_id_mismatch":
            state["failures"][0]["captureDiagnostics"]["instanceId"] = 1
        state_path = c.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (data["snapshotId"] + ".json")
        state_path.parent.mkdir(parents=True, exist_ok=True)
        state_path.write_text(json.dumps(state), encoding="utf-8")
        if self.failure == "missing_manifest":
            path.unlink()
        elif self.failure == "missing_state":
            state_path.unlink()
        elif self.failure == "extra_file":
            (directory / "extra.png").write_bytes(b"not-image")
        elif self.failure == "outside_manifest":
            data["manifestPath"] = "../outside/manifest.json"
        elif self.failure in ("hash", "bytes"):
            data["manifestSha256" if self.failure == "hash" else "manifestBytes"] = "wrong"
        elif self.failure == "invalid_json":
            path.write_text("{", encoding="utf-8")
            data.update(manifestBytes=1, manifestSha256=hashlib.sha256(b"{").hexdigest())

    def finish(self, data):
        index = 0 if data["request"]["targets"][0]["kind"] == "camera" else 1
        case = (self.client.TIMEOUT_CASE if data["request"]["targets"][0]["targetId"] == self.client.TIMEOUT_CASE[0]
                else self.client.CASES[index])
        data.update(status="failed", phase="failed", terminal=True, success=False, cancelRequested=False,
                    endedAtUtcMs=2000, snapshotSequence=2, persistenceStatus="verified",
                    persistenceVerified=True, persistenceError="", persistenceSchemaVersion=2,
                    targets=[{"targetId": case[0], "status": "failed", "success": False, "artifactIds": []}],
                    failures=[{"targetId": case[0], "code": case[2], "stage": case[3],
                               "captureDiagnostics": {"instanceId": 0}}], artifacts=[])
        if case == self.client.TIMEOUT_CASE:
            data["endedAtUtcMs"] = 11000
            data["failures"][0]["message"] = self.client.TIMEOUT_MESSAGE
            if self.failure == "timeout_message":
                data["failures"][0]["message"] = "A different capture failure"
            elif self.failure == "too_fast":
                data["endedAtUtcMs"] = 1500
        if (self.failure or "").startswith("terminal_"):
            field = self.failure.removeprefix("terminal_")
            data[field] = {"terminal": False, "success": True, "cancelRequested": True,
                           "snapshotSequence": 0, "persistenceVerified": False,
                           "persistenceSchemaVersion": 1, "endedAtUtcMs": 1,
                           "targets": [], "failures": [], "artifacts": [{"acceptedAsEvidence": True}]
                           }.get(field, "other")
        elif self.failure in ("code", "stage"):
            data["failures"][0][self.failure] = "other"
        elif self.failure == "accepted_target":
            data["targets"][0]["artifactIds"] = ["accepted"]
        elif self.failure == "target_success":
            data["targets"][0]["success"] = True
        self.persist(data)

    async def call_tool(self, proxy, request, *, meta):
        assert proxy == "unity_tool_call" and not self.closed
        name, args = request["toolName"], request["args"]
        from upilot_mcp.mcp_tools import snapshot_tools, status_tools, task_tools, test_tools
        model = next((getattr(module, name) for module in (snapshot_tools, status_tools, task_tools, test_tools)
                      if hasattr(module, name)), None)
        assert model is not None, name
        inspect.signature(model).bind(**args)
        self.calls.append((name, copy.deepcopy(args), copy.deepcopy(meta)))
        c = self.client
        if name == "unity_mcp_status":
            data = {"connected": True, "serverReady": True, "paths": {"unityProjectAbsolute": str(c.PROJECT)},
                    "session": {"identityVerified": True, "processId": 1, "processCreatedAt": 1000, "sessionId": "bridge"},
                    "runtimeIdentity": {"mcpServer": {"processId": 2, "loadedSource": {
                        "status": "equivalent", "scope": "selected-entry-code-only"}}},
                    "executionState": {"ready": True, "authoritative": True, "isStale": False,
                                       "playModeState": "edit", "producerEpoch": "epoch", "domainGeneration": "domain"}}
            if self.failure == "project":
                data["paths"]["unityProjectAbsolute"] = str(c.PROJECT.parent)
            elif self.failure in ("stale", "playing", "not_authoritative"):
                data["executionState"][{"stale": "isStale", "playing": "playModeState",
                                          "not_authoritative": "authoritative"}[self.failure]] = {
                                          "stale": True, "playing": "play", "not_authoritative": False}[self.failure]
            elif self.failure == "loaded":
                data["runtimeIdentity"]["mcpServer"]["loadedSource"]["status"] = "unknown"
            elif self.failure == "domain_change" and self.starts:
                data["executionState"]["domainGeneration"] = "other"
        elif name == "unity_queue_cleanup":
            data = {"complete": self.failure != "queue_incomplete", "isStale": self.failure == "queue_stale",
                    "items": ["foreign"] if self.failure == "busy" else []}
        elif name == "unity_console_capture_list":
            data = {"activeCount": 1 if self.failure == "capture" else 0}
        elif name == "unity_test_status":
            data = {"isRunning": self.failure == "runner", "cleanupPending": self.failure == "runner_cleanup"}
        elif name == "unity_ensure_ready":
            assert args["editorModeControl"] == "manual"
            data = {"ready": self.failure != "ready", "blocked": False,
                    "contextAuthoritative": True, "contextStale": False}
        elif name == "unity_camera_list":
            data = self.camera_inventory()
        elif name == "unity_snapshot_capture":
            self.starts += 1
            assert args["waitMs"] == 0
            self.check_output_before_start(args)
            assert args["channels"] == ["color"] and len(args["targets"]) == 1
            target = snapshot_tools._SnapshotTargetSchema.model_validate(args["targets"][0]).model_dump()
            data = {"snapshotId": "snapshot-" + str(self.starts) * 32, "requestKey": args["requestKey"],
                    "requestHash": "a" * 64, "serviceLifecycleId": "lifetime", "startedAtUtcMs": 1000,
                    "outputDirectory": args["outputDirectory"], "manifestPath": args["outputDirectory"] + "/manifest.json",
                    "completionPolicy": args["completionPolicy"], "request": {**copy.deepcopy(args), "targets": [target]},
                    "terminal": False, "status": "queued"}
            if self.failure == "ack_lost":
                raise TimeoutError("secret-transport-text")
            if self.failure == "no_id":
                data["snapshotId"] = ""
            elif self.failure == "admission_request":
                data["request"]["targets"][0]["width"] = 33
            elif self.failure == "admission_hash":
                data["requestHash"] = ""
            self.values[data["snapshotId"]] = data
        elif name in ("unity_snapshot_status", "unity_snapshot_collect_artifacts"):
            assert args["snapshotId"] in self.values
            data = self.values[args["snapshotId"]]
            self.reads += 1
            if self.failure == "observation_error":
                raise TimeoutError("secret-transport-text")
            if data.get("status") == "queued":
                self.finish(data)
                if (self.failure or "").startswith("identity_"):
                    data[self.failure.removeprefix("identity_")] = "other"
            elif name == "unity_snapshot_collect_artifacts":
                if self.failure == "collect_sequence":
                    data["snapshotSequence"] += 1
                elif self.failure == "collect_end":
                    data["endedAtUtcMs"] += 1
                elif self.failure == "read_mutates_file":
                    path = c.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (data["snapshotId"] + ".json")
                    # Semantically identical JSON, different actual bytes/hash.
                    path.write_text(json.dumps(json.loads(path.read_text()), indent=2), encoding="utf-8")
        else:
            raise AssertionError(name)
        return SimpleNamespace(structuredContent=stringify_wire_ids({"ok": True, "data": copy.deepcopy(data)}), content=[], isError=False)


def execute(client, monkeypatch, failure=None):
    fake = FakeSnapshot(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report = {"calls": []}
    error = None
    try:
        asyncio.run(client.exercise(8011, report))
    except BaseException as exc:
        error = exc
    return fake, report, error


def test_two_failures_have_one_owned_start_each_and_actual_files(client, monkeypatch):
    fake, report, error = execute(client, monkeypatch)
    assert error is None
    assert fake.closed and fake.starts == 2 and report["result"] == "observed"
    assert len(set(report["ownedSnapshotIds"])) == 2
    assert all(case["files"]["verified"] for case in report["cases"])
    assert "firstError" not in report and "cleanupError" not in report
    assert {name for name, _, _ in fake.calls} == {
        "unity_mcp_status", "unity_queue_cleanup", "unity_console_capture_list", "unity_test_status",
        "unity_ensure_ready", "unity_camera_list", "unity_snapshot_capture", "unity_snapshot_status",
        "unity_snapshot_collect_artifacts"}
    for name, args, meta in fake.calls:
        assert meta
        if name in ("unity_snapshot_status", "unity_snapshot_collect_artifacts"):
            assert args["snapshotId"] in report["ownedSnapshotIds"]
    gameview = [args for name, args, _ in fake.calls if name == "unity_snapshot_capture"][1]
    assert "instanceId" not in gameview["targets"][0]


@pytest.mark.parametrize("failure", ["project", "stale", "playing", "not_authoritative", "loaded", "busy",
    "queue_incomplete", "queue_stale", "capture", "runner", "runner_cleanup", "ready", "camera_inventory"])
def test_unsafe_preflight_never_starts(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError)
    assert fake.starts == 0 and report["result"] == "unknown"


@pytest.mark.parametrize("failure", ["ack_lost", "no_id", "admission_request", "admission_hash",
    "identity_snapshotId", "identity_requestKey", "identity_requestHash", "identity_serviceLifecycleId",
    "identity_startedAtUtcMs", "identity_request", "terminal_status", "terminal_phase", "terminal_success",
    "terminal_cancelRequested", "terminal_endedAtUtcMs", "terminal_targets", "terminal_failures", "terminal_artifacts",
    "code", "stage", "accepted_target", "target_success", "terminal_persistenceStatus", "terminal_persistenceVerified",
    "terminal_persistenceError", "terminal_persistenceSchemaVersion", "terminal_snapshotSequence", "missing_manifest",
    "missing_state", "extra_file", "outside_manifest", "hash", "bytes", "invalid_json", "manifest_snapshotId",
    "manifest_persistenceStatus", "manifest_manifestSha256", "state_snapshotId", "state_manifestSha256",
    "collect_sequence", "collect_end", "read_mutates_file", "observation_error", "domain_change",
    "non_id_numeric_string", "wire_id_mismatch"])
def test_unknown_or_unverified_failure_stops_without_replay(client, monkeypatch, failure):
    fake, report, error = execute(client, monkeypatch, failure)
    assert error is not None
    assert fake.starts == 1 and fake.closed and report["result"] == "unknown"
    assert not any(name in {"unity_snapshot_cancel", "unity_service_restart", "unity_playmode_start"}
                   for name, _, _ in fake.calls)
    if failure in {"ack_lost", "no_id", "admission_request", "admission_hash"}:
        assert not any(name == "unity_snapshot_status" for name, _, _ in fake.calls)
    if failure == "observation_error":
        assert report["firstError"]["type"] == "TimeoutError"
        assert report["cleanupError"]["type"] == "TimeoutError"
        assert "secret-transport-text" not in json.dumps(report)


def test_files_refuse_escape_and_links(client, tmp_path):
    with pytest.raises(client.CallPreflightError):
        client.read_json(tmp_path.parent / "outside.json")
    target = tmp_path / "owned.json"
    target.write_text("{}", encoding="utf-8")
    link = tmp_path / "link.json"
    try:
        link.symlink_to(target)
    except OSError:
        pytest.skip("OS does not authorize creating this test-owned symlink")
    with pytest.raises(client.CallPreflightError):
        client.read_json(link)


@pytest.mark.parametrize("value", [b"", b"x" * (1048576 + 1)], ids=["empty", "oversized"])
def test_files_are_bounded(client, tmp_path, value):
    path = tmp_path / "receipt.json"
    path.write_bytes(value)
    with pytest.raises(client.CallPreflightError):
        client.read_json(path)


def test_fixed_observation_budget_and_no_hidden_mutations(client):
    source = inspect.getsource(client.exercise)
    assert source.count("asyncio.get_running_loop().time() + 90") == 1
    assert source.count("asyncio.timeout_at(deadline)") == 1
    assert source.count('query("unity_snapshot_capture", request)') == 1
    for forbidden in ("unity_snapshot_cancel", "unity_playmode_start", "unity_playmode_stop",
                      "unity_service_restart", "forceStop", "unity_compile", "unity_operation_start"):
        assert forbidden not in source


@pytest.mark.parametrize("arguments", [[], ["--execute", "--port", "0"], ["--execute", "--port", "65536"]])
def test_cli_requires_explicit_opt_in_and_valid_port(client, arguments):
    with pytest.raises(SystemExit) as error:
        asyncio.run(client.main(arguments))
    assert error.value.code == 2


def test_cli_keeps_first_and_cleanup_errors_separate_and_redacts(client, monkeypatch, capsys):
    async def failed(port, report):
        report.update(firstError={"message": "first"}, cleanupError={"message": "cleanup"}, ownerToken="secret")
        raise RuntimeError("secret-transport-text")
    monkeypatch.setattr(client, "exercise", failed)
    assert asyncio.run(client.main(["--execute"])) == 1
    output = json.loads(capsys.readouterr().out)
    report = json.loads(Path(output["path"]).read_text())
    assert report["firstError"] == {"message": "first"} and report["cleanupError"] == {"message": "cleanup"}
    assert "secret" not in json.dumps(report)
    assert output["result"] == "unknown"


class FakePausedSnapshot(FakeSnapshot):
    """Uses actual tool signatures and existing public wire projection; no live mode writes."""
    def __init__(self, client, failure=None):
        super().__init__(client, failure)
        self.mode, self.start_count, self.stop_count = "edit", 0, 0
        self.play_request = "owned-play-request"

    async def call_tool(self, proxy, request, *, meta):
        name, args = request["toolName"], request["args"]
        from upilot_mcp.mcp_tools import resource_tools, reflection_tools, status_tools
        special = {"unity_scene_list", "unity_gameobject_find", "unity_tools_find", "unity_reflection_call",
                   "unity_playmode_start", "unity_playmode_pause", "unity_playmode_stop"}
        if name in special:
            model = next(getattr(m, name) for m in (resource_tools, reflection_tools, status_tools) if hasattr(m, name))
            inspect.signature(model).bind(**args)
            self.calls.append((name, copy.deepcopy(args), copy.deepcopy(meta)))
            if name == "unity_scene_list":
                data = {"scenes": [{"scenePath": "Assets/owned.unity", "isDirty": self.failure == "dirty_scene",
                                    "isLoaded": True, "isActive": True}]}
                if self.failure == "scene_changed" and self.stop_count:
                    data["scenes"][0]["scenePath"] = "Assets/other.unity"
            elif name == "unity_gameobject_find":
                assert args["includeHidden"] and args["includeInactive"] and args["name"] == self.client.RUNNER_NAME
                data = {"gameObjects": [{"instanceId": "7"}] if (self.mode == "pause" and self.starts or
                          self.failure == "existing_host" or self.failure == "host_leak" and self.stop_count) else [],
                        "truncated": self.failure == "hosts_truncated"}
            elif name == "unity_tools_find":
                data = {"count": 1 if self.failure == "semantic_getter" else 0}
            elif name == "unity_reflection_call":
                assert args == {"expression": "UnityEngine.Application.runInBackground"}
                data = {"status": "Succeeded", "resultValue": {"typeName": "System.Boolean", "serializationStatus": "inline",
                        "valueJson": "true" if self.failure == "background_leak" and self.starts and not self.stop_count else "false"}}
                if self.failure == "getter_invalid":
                    data["resultValue"]["valueJson"] = "unknown"
            elif name == "unity_playmode_start":
                self.start_count += 1
                assert self.mode == "edit" and self.start_count == 1
                self.mode = "play"
                if self.failure == "start_lost":
                    raise TimeoutError("secret-start-text")
                data = {"confirmed": self.failure != "start_unconfirmed", "playModeState": "play", "commandId": "owned-play-command"}
            elif name == "unity_playmode_pause":
                assert self.mode == "play"
                self.mode = "pause"
                if self.failure == "pause_lost":
                    raise TimeoutError("secret-pause-text")
                data = {"confirmed": self.failure != "pause_unconfirmed", "state": "pause", "playModeState": "",
                        "editorState": {"isPlaying": True, "isPaused": True, "playModeState": "pause"}}
            else:
                self.stop_count += 1
                assert self.mode in ("play", "pause") and self.stop_count == 1
                self.mode = "edit"
                if self.failure == "stop_lost":
                    raise TimeoutError("secret-stop-text")
                data = {"confirmed": self.failure != "stop_unconfirmed", "playModeState": "edit"}
            envelope = {"ok": True, "data": data, "requestId": self.play_request if name == "unity_playmode_start" else "query"}
            return SimpleNamespace(structuredContent=stringify_wire_ids(envelope), content=[], isError=False)
        response = await super().call_tool(proxy, request, meta=meta)
        if name == "unity_mcp_status":
            data = response.structuredContent["data"]
            state = data["executionState"]
            state.update(playModeState=self.mode, isCompiling=False,
                         domainGeneration="play-domain" if self.mode != "edit" else "edit-domain")
            data["automationAuthorization"] = {"catalogCurrent": self.failure != "grant_stale",
                    "enabledScopes": [] if self.failure == "grant_missing" else ["editorModeTransition"]}
            if self.start_count:
                data["playModeTransition"] = {"requestId": self.play_request, "origin": "upilot",
                    "toolName": "unity_playmode_start", "toState": "enteringPlay", "transitionId": "transition", "commandId": "owned-play-command"}
            if self.failure == "foreign_transition" and self.starts:
                data["playModeTransition"]["requestId"] = "foreign"
            if self.failure == "foreign_command" and self.starts:
                data["playModeTransition"]["commandId"] = "foreign"
            if self.failure == "process_change" and self.starts:
                data["session"]["processId"] = 3
            if self.failure == "domain_change" and self.starts:
                state["domainGeneration"] = "other"
        return response


def execute_timeout(client, monkeypatch, failure=None):
    fake = FakePausedSnapshot(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report, error = {"calls": []}, None
    try:
        asyncio.run(client.exercise_paused_timeout(8011, report))
    except BaseException as exc:
        error = exc
    return fake, report, error


def test_paused_timeout_one_start_real_contract_and_owned_cleanup(client, monkeypatch):
    fake, report, error = execute_timeout(client, monkeypatch)
    assert error is None and report["result"] == "observed" and report["cleanupVerified"] is True
    assert fake.starts == fake.start_count == fake.stop_count == 1 and fake.closed
    assert report["cases"][0]["files"]["verified"] is True
    assert report["hostsAfterTimeoutPaused"]["gameObjects"] and report["hostsAfterEdit"]["gameObjects"] == []
    assert not any(name in {"unity_snapshot_cancel", "unity_playmode_resume", "unity_service_restart"} for name, _, _ in fake.calls)


@pytest.mark.parametrize("failure", ["grant_missing", "grant_stale", "dirty_scene", "existing_host",
                                       "hosts_truncated", "semantic_getter", "getter_invalid", "busy", "capture", "ready"])
def test_timeout_unsafe_preflight_never_changes_mode(client, monkeypatch, failure):
    fake, report, error = execute_timeout(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError)
    assert fake.start_count == fake.starts == fake.stop_count == 0 and report["result"] == "unknown"


@pytest.mark.parametrize("failure", ["start_lost", "start_unconfirmed", "pause_lost", "pause_unconfirmed", "ack_lost",
    "timeout_message", "too_fast", "code", "stage", "hash", "missing_manifest", "accepted_target", "background_leak",
    "host_leak", "scene_changed", "stop_lost", "stop_unconfirmed", "foreign_transition", "foreign_command", "process_change", "domain_change"])
def test_timeout_failure_preserves_first_error_never_replays_or_stops_foreign_mode(client, monkeypatch, failure):
    fake, report, error = execute_timeout(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and fake.closed
    assert fake.start_count == 1 and fake.starts <= 1 and fake.stop_count <= 1
    if failure in {"start_lost", "foreign_transition", "foreign_command", "process_change"}:
        assert fake.stop_count == 0 and "cleanupError" in report
    elif failure != "start_lost":
        assert fake.stop_count == 1
    assert not any(name in {"unity_snapshot_cancel", "unity_playmode_resume", "unity_service_restart"} for name, _, _ in fake.calls)
    assert "secret-" not in json.dumps(report)


def test_timeout_original_budget_and_single_action_routes(client):
    source = inspect.getsource(client.exercise_paused_timeout)
    assert source.count("asyncio.get_running_loop().time() + 120") == 1
    assert source.count("asyncio.timeout_at(deadline)") == 1
    assert source.count("asyncio.timeout_at(deadline - 30)") == 1
    for action in ("unity_playmode_start", "unity_playmode_pause", "unity_playmode_stop", "unity_snapshot_capture"):
        assert source.count('query("' + action + '"') == 1
    # Resume is exclusive to the new locked-state pixel case. Timeout runtime
    # tests above still prohibit it, and all other mutation routes remain absent.
    assert source.count('query("unity_playmode_resume"') == 1
    assert source.index("state_resources.enter_context(hold_owned_state(baseline))") < source.index('query("unity_playmode_resume"')
    import ast
    tree = ast.parse(source)
    guards = [node for node in ast.walk(tree) if isinstance(node, ast.If) and
              isinstance(node.test, ast.Name) and node.test.id == "successful_pixels" and
              any(isinstance(child, ast.Constant) and child.value == "unity_playmode_resume" for child in ast.walk(node))]
    assert len(guards) == 1
    for forbidden in ("unity_snapshot_cancel", "unity_service_restart", "forceStop", "unity_compile", "unity_operation_start"):
        assert forbidden not in source


@pytest.mark.parametrize("marker", ["play", "enteringPlay"])
@pytest.mark.parametrize("mode", ["play", "pause"])
def test_owned_play_marker_requires_exact_command_and_fresh_play_fact(client, marker, mode):
    data = {"playModeTransition": {"requestId": "request", "commandId": "command", "origin": "upilot",
            "toolName": "unity_playmode_start", "toState": marker, "transitionId": "transition"},
            "executionState": {"authoritative": True, "isStale": False, "playModeState": mode}}
    client.check_owned_play(data, "request", "command")
    for field, value in (("commandId", "foreign"), ("requestId", "foreign"), ("toState", "edit")):
        changed = copy.deepcopy(data)
        changed["playModeTransition"][field] = value
        with pytest.raises(client.CallPreflightError):
            client.check_owned_play(changed, "request", "command")
    for field, value in (("authoritative", False), ("isStale", True), ("playModeState", "enteringPlay"), ("playModeState", "paused")):
        changed = copy.deepcopy(data)
        changed["executionState"][field] = value
        with pytest.raises(client.CallPreflightError):
            client.check_owned_play(changed, "request", "command")


class FakeManifestFailure(FakeSnapshot):
    def check_output_before_start(self, args):
        directory = self.client.PROJECT / args["outputDirectory"]
        assert {p.name for p in directory.iterdir()} == {"manifest.json"}
        assert (directory / "manifest.json").is_dir() and not list((directory / "manifest.json").iterdir())

    def persist(self, data):
        data.update(persistenceStatus="unverified", persistenceError="MANIFEST_REPLACE_FAILED",
                    persistenceVerified=False, manifestBytes=0, manifestSha256="")
        record = copy.deepcopy(data)
        record.pop("persistenceVerified", None)
        record.update(persistenceStatus="pending", persistenceError="")
        directory = self.client.PROJECT / data["outputDirectory"]
        path = directory / ("manifest.json." + "b" * 32 + ".tmp")
        if self.failure == "staging_identity":
            record["snapshotId"] = "snapshot-" + "9" * 32
        elif self.failure == "staging_selfhash":
            record["manifestSha256"] = "f" * 64
        elif self.failure == "staging_no_terminal":
            record["terminal"] = False
        elif self.failure == "staging_terminal_mismatch":
            record["endedAtUtcMs"] += 1
        path.write_text(json.dumps(record), encoding="utf-8")
        if self.failure == "staging_duplicate_terminal":
            (directory / ("manifest.json." + "c" * 32 + ".tmp")).write_bytes(path.read_bytes())
        elif self.failure == "staging_invalid_json":
            path.write_text("{", encoding="utf-8")
        elif self.failure == "unexpected_state":
            state = self.client.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (data["snapshotId"] + ".json")
            state.parent.mkdir(parents=True, exist_ok=True)
            state.write_text("{}", encoding="utf-8")
        elif self.failure == "unexpected_image":
            (directory / "extra.png").write_bytes(b"not-image")
        elif self.failure == "obstruction_changed":
            (directory / "manifest.json" / "foreign").write_bytes(b"changed")
        elif self.failure == "staging_missing":
            path.unlink()
        elif self.failure == "staging_over_budget":
            path.write_bytes(b"x" * (1048576 + 1))
        elif self.failure == "staging_count_over_budget":
            for i in range(33):
                (directory / (f"manifest.json.{i:032x}.tmp")).write_bytes(path.read_bytes())
        for key, value in {"false_verified": ("persistenceVerified", True),
                           "false_status": ("persistenceStatus", "verified"),
                           "false_error": ("persistenceError", "STATE_REPLACE_FAILED"),
                           "false_bytes": ("manifestBytes", 1), "false_bool_bytes": ("manifestBytes", False),
                           "false_hash": ("manifestSha256", "f" * 64)}.items():
            if self.failure == key:
                data[value[0]] = value[1]

    async def call_tool(self, proxy, request, *, meta):
        if request["toolName"] == "unity_snapshot_collect_artifacts" and self.failure == "staging_read_mutation":
            data = self.values[request["args"]["snapshotId"]]
            path = self.client.PROJECT / data["outputDirectory"] / ("manifest.json." + "b" * 32 + ".tmp")
            path.write_text(json.dumps(json.loads(path.read_text()), indent=2), encoding="utf-8")
        return await super().call_tool(proxy, request, meta=meta)


def execute_manifest(client, monkeypatch, failure=None):
    fake = FakeManifestFailure(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report, error = {"calls": []}, None
    try:
        asyncio.run(client.exercise(8011, report, manifest_failure=True))
    except BaseException as exc:
        error = exc
    return fake, report, error


def test_manifest_failure_has_one_owned_start_no_state_and_real_uncommitted_files(client, monkeypatch):
    fake, report, error = execute_manifest(client, monkeypatch)
    assert error is None and report["result"] == "observed" and fake.closed and fake.starts == 1
    assert len(report["cases"]) == 1 and len(report["ownedSnapshotIds"]) == 1
    observed = report["cases"][0]
    assert observed["terminal"]["persistenceStatus"] == "unverified"
    assert observed["terminal"]["persistenceError"] == "MANIFEST_REPLACE_FAILED"
    assert observed["terminal"]["failures"][0]["code"] == "CAMERA_NOT_FOUND"
    assert observed["files"]["verified"] is False and observed["files"]["failureEvidenceVerified"] is True
    assert Path(observed["files"]["obstruction"]).is_dir() and not Path(observed["files"]["statePath"]).exists()
    for file in observed["files"]["staging"]:
        raw = Path(file["path"]).read_bytes()
        assert len(raw) == file["bytes"] and hashlib.sha256(raw).hexdigest() == file["sha256"]
    assert not any(name in {"unity_snapshot_cancel", "unity_service_restart", "unity_playmode_start", "unity_compile"}
                   for name, _, _ in fake.calls)


@pytest.mark.parametrize("failure", ["staging_identity", "staging_selfhash", "staging_no_terminal", "staging_terminal_mismatch",
    "staging_duplicate_terminal", "staging_invalid_json", "unexpected_state", "unexpected_image", "obstruction_changed",
    "staging_missing", "staging_over_budget", "staging_count_over_budget", "false_verified", "false_status", "false_error",
    "false_bytes", "false_bool_bytes", "false_hash", "staging_read_mutation", "collect_sequence", "collect_end", "code", "stage", "ack_lost"])
def test_manifest_failure_refuses_false_evidence_or_changes_without_replay(client, monkeypatch, failure):
    fake, report, error = execute_manifest(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and fake.closed and fake.starts == 1
    assert report["firstError"]
    assert not any(name in {"unity_snapshot_cancel", "unity_service_restart", "unity_playmode_start", "unity_compile"}
                   for name, _, _ in fake.calls)
    if failure == "ack_lost":
        assert not any(name == "unity_snapshot_status" for name, _, _ in fake.calls)


@pytest.mark.parametrize("failure", ["project", "loaded", "stale", "busy", "capture", "runner", "ready"])
def test_manifest_failure_unsafe_preflight_creates_no_obstruction_or_snapshot(client, monkeypatch, failure):
    fake, report, error = execute_manifest(client, monkeypatch, failure)
    assert error is not None and fake.starts == 0 and fake.closed
    assert not list(client.PROJECT.rglob("manifest.json"))


def test_manifest_failure_never_overwrites_existing_owned_output(client, monkeypatch):
    nonce = "1" * 32
    monkeypatch.setattr(client.uuid, "uuid4", lambda: SimpleNamespace(hex=nonce))
    directory = client.PROJECT / "Log" / "P0P1" / ("v09-manifest-failure-" + nonce) / "missing-camera"
    directory.mkdir(parents=True)
    sentinel = directory / "keep"
    sentinel.write_bytes(b"preserve")
    fake, report, error = execute_manifest(client, monkeypatch)
    assert error is not None and fake.starts == 0 and sentinel.read_bytes() == b"preserve"
    assert {p.name for p in directory.iterdir()} == {"keep"}


def test_manifest_failure_cli_is_exclusive_and_dispatches_only_selected_mode(client, monkeypatch, capsys):
    with pytest.raises(SystemExit) as error:
        asyncio.run(client.main(["--execute", "--paused-timeout", "--manifest-write-failure"]))
    assert error.value.code == 2
    seen = []
    async def exercise(port, report, *, manifest_failure=False):
        seen.append((port, manifest_failure))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise", exercise)
    assert asyncio.run(client.main(["--execute", "--manifest-write-failure"])) == 0
    assert seen == [(8011, True)]
    assert json.loads(capsys.readouterr().out)["result"] == "observed"


PIXEL_CAMERA_ID = "568105584918844406"


class FakeManifestPixels(FakeManifestFailure):
    def camera_inventory(self):
        camera = {"instanceId": PIXEL_CAMERA_ID, "active": True, "enabled": True}
        if self.failure in ("inactive_camera", "disabled_camera"):
            camera["active" if self.failure == "inactive_camera" else "enabled"] = False
        cameras = [] if self.failure == "missing_camera" else [camera]
        if self.failure == "ambiguous_camera":
            cameras.append(copy.deepcopy(camera))
        return {"cameras": cameras, "count": len(cameras) + (self.failure == "camera_inventory")}

    def finish(self, data):
        directory = self.client.PROJECT / data["outputDirectory"]
        image_path = directory / "camera-pixels.color.png"
        Image.new("RGBA", (31, 32) if self.failure == "png_dimensions" else (32, 32), (12, 80, 170, 255)).save(image_path)
        if self.failure == "png_invalid":
            image_path.write_bytes(b"not PNG")
        elif self.failure == "png_oversize":
            image_path.write_bytes(b"x" * (1048576 + 1))
        raw = image_path.read_bytes()
        artifact = {"artifactId": "artifact-" + "d" * 32, "targetId": "camera-pixels", "role": "color",
                    "path": image_path.relative_to(self.client.PROJECT).as_posix(), "mimeType": "image/png",
                    "encoding": "rgba8", "units": "", "width": 32, "height": 32, "bytes": len(raw),
                    "sha256": hashlib.sha256(raw).hexdigest(), "acceptedAsEvidence": False, "statistics": None}
        target = {"targetId": "camera-pixels", "kind": "camera", "status": "completed", "success": True,
                  "camera": {"instanceId": PIXEL_CAMERA_ID}, "artifactIds": [artifact["artifactId"]],
                  "provenance": {"captureApi": "Camera.Render(RenderTextureDescriptor)", "matchedInstanceId": PIXEL_CAMERA_ID,
                                 "unityProcessId": 1, "pixelSourceVerified": True, "degraded": False, "occlusionSensitive": False}}
        data.update(status="completed", phase="completed", terminal=True, success=True, cancelRequested=False,
                    endedAtUtcMs=2000, snapshotSequence=3, persistenceSchemaVersion=2, targets=[target], failures=[], artifacts=[artifact])
        self.persist(data)
        staging_path = directory / ("manifest.json." + "b" * 32 + ".tmp")
        record = json.loads(staging_path.read_bytes())
        # Staging may have the pre-RejectArtifacts bool; never assert that it is a receipt.
        record["artifacts"][0]["acceptedAsEvidence"] = True
        if self.failure == "staging_pixel_hash":
            record["artifacts"][0]["sha256"] = "e" * 64
        elif self.failure == "staging_pixel_bool":
            record["artifacts"][0]["acceptedAsEvidence"] = 1
        elif self.failure == "staging_pixel_missing":
            record["artifacts"] = []
        staging_path.write_bytes(json.dumps(record).encode())
        if self.failure == "accepted_pixels":
            artifact["acceptedAsEvidence"] = True
        elif self.failure == "pixel_hash":
            artifact["sha256"] = "f" * 64
        elif self.failure in ("pixel_bytes", "pixel_bool_bytes"):
            artifact["bytes"] = False if self.failure == "pixel_bool_bytes" else len(raw) + 1
        elif self.failure == "pixel_path":
            artifact["path"] = "../foreign.png"
        elif self.failure == "pixel_missing":
            image_path.unlink()
        elif self.failure == "pixel_read_mutation":
            self.original_png = image_path
        elif self.failure == "pixel_camera":
            target["camera"]["instanceId"] = "123"
        elif self.failure == "pixel_pid":
            target["provenance"]["unityProcessId"] = 2
        elif self.failure == "pixel_source":
            target["provenance"]["pixelSourceVerified"] = False
        elif self.failure == "pixel_artifact_id":
            target["artifactIds"] = ["foreign"]
        elif self.failure == "pixel_business_failure":
            data["success"] = False

    async def call_tool(self, proxy, request, *, meta):
        if request["toolName"] == "unity_snapshot_collect_artifacts" and self.failure == "pixel_read_mutation":
            self.original_png.write_bytes(b"changed")
        return await super().call_tool(proxy, request, meta=meta)


def execute_pixels(client, monkeypatch, failure=None, camera_id=PIXEL_CAMERA_ID):
    fake = FakeManifestPixels(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report, error = {"calls": []}, None
    try:
        asyncio.run(client.exercise(8011, report, pixels_camera_id=camera_id))
    except BaseException as exc:
        error = exc
    return fake, report, error


def test_pixels_capture_success_is_separate_from_failed_persistence(client, monkeypatch):
    fake, report, error = execute_pixels(client, monkeypatch)
    assert error is None and report["result"] == "observed" and fake.closed and fake.starts == 1
    observed = report["cases"][0]
    receipt = observed["terminal"]
    assert receipt["success"] is True and receipt["status"] == "completed" and receipt["failures"] == []
    assert receipt["persistenceStatus"] == "unverified" and receipt["artifacts"][0]["acceptedAsEvidence"] is False
    assert observed["files"]["verified"] is False and observed["files"]["failureEvidenceVerified"] is True
    png = observed["files"]["unacceptedPng"]
    raw = Path(png["path"]).read_bytes()
    assert len(raw) == png["bytes"] and hashlib.sha256(raw).hexdigest() == png["sha256"] and png["decoded"]
    staging = json.loads(Path(observed["files"]["terminalStaging"]["path"]).read_bytes())
    assert staging["artifacts"][0]["acceptedAsEvidence"] is True  # pending staging, not authority
    assert not Path(observed["files"]["statePath"]).exists()
    calls = [name for name, _, _ in fake.calls]
    assert calls.count("unity_snapshot_capture") == 1
    assert set(calls) <= {"unity_mcp_status", "unity_queue_cleanup", "unity_console_capture_list", "unity_test_status",
                         "unity_ensure_ready", "unity_camera_list", "unity_snapshot_capture", "unity_snapshot_status",
                         "unity_snapshot_collect_artifacts"}
    assert observed["request"]["targets"][0]["instanceId"] == PIXEL_CAMERA_ID
    assert "business visual correctness or orientation" in report["notCovered"]


@pytest.mark.parametrize("failure", ["project", "loaded", "stale", "busy", "capture", "runner", "ready",
    "missing_camera", "ambiguous_camera", "inactive_camera", "disabled_camera", "camera_inventory"])
def test_pixels_unsafe_preflight_creates_no_files_or_snapshot(client, monkeypatch, failure):
    fake, report, error = execute_pixels(client, monkeypatch, failure)
    assert error is not None and fake.starts == 0 and fake.closed
    assert not list(client.PROJECT.rglob("manifest.json"))


@pytest.mark.parametrize("camera_id", ["0", "-1", " 1", "1 ", "01", "not-an-id"])
def test_pixels_exact_id_required_before_connect(client, monkeypatch, camera_id):
    fake, report, error = execute_pixels(client, monkeypatch, camera_id=camera_id)
    assert error is not None and fake.calls == [] and fake.starts == 0


@pytest.mark.parametrize("failure", ["accepted_pixels", "pixel_hash", "pixel_bytes", "pixel_bool_bytes", "pixel_path",
    "pixel_missing", "pixel_read_mutation", "pixel_camera", "pixel_pid", "pixel_source", "pixel_artifact_id",
    "pixel_business_failure", "png_invalid", "png_dimensions", "png_oversize", "staging_pixel_hash", "staging_pixel_bool",
    "staging_pixel_missing", "false_verified", "false_status", "false_error", "false_bytes", "false_hash",
    "unexpected_state", "unexpected_image", "staging_identity", "obstruction_changed", "ack_lost", "collect_sequence"])
def test_pixels_false_evidence_never_replays_or_accepts(client, monkeypatch, failure):
    fake, report, error = execute_pixels(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and fake.closed and fake.starts == 1
    assert report["firstError"]
    assert not any(name in {"unity_snapshot_cancel", "unity_service_restart", "unity_playmode_start", "unity_compile"}
                   for name, _, _ in fake.calls)
    if failure == "ack_lost":
        assert not any(name == "unity_snapshot_status" for name, _, _ in fake.calls)


def test_pixels_cli_is_exclusive_and_selects_only_exact_camera(client, monkeypatch, capsys):
    for mode in ("--paused-timeout", "--manifest-write-failure"):
        with pytest.raises(SystemExit) as error:
            asyncio.run(client.main(["--execute", mode, "--manifest-pixels-camera-id", PIXEL_CAMERA_ID]))
        assert error.value.code == 2
    seen = []
    async def exercise(port, report, *, pixels_camera_id=""):
        seen.append((port, pixels_camera_id))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise", exercise)
    assert asyncio.run(client.main(["--execute", "--manifest-pixels-camera-id", PIXEL_CAMERA_ID])) == 0
    assert seen == [(8011, PIXEL_CAMERA_ID)]
    assert json.loads(capsys.readouterr().out)["result"] == "observed"


@pytest.mark.parametrize("camera_id", ["", "0", "-1", "unknown"])
def test_pixels_cli_invalid_id_never_falls_back_to_other_case(client, monkeypatch, camera_id):
    async def forbidden(*args, **kwargs):
        raise AssertionError("invalid explicit selector must not dispatch another scenario")
    monkeypatch.setattr(client, "exercise", forbidden)
    with pytest.raises(SystemExit) as error:
        asyncio.run(client.main(["--execute", "--manifest-pixels-camera-id", camera_id]))
    assert error.value.code == 2


class FakeStateFailure(FakePausedSnapshot):
    """Synthetic state boundary; does not claim a real Unity replacement failure."""
    def persist(self, data):
        directory = self.client.PROJECT / data["outputDirectory"]
        manifest = copy.deepcopy(data)
        manifest.pop("persistenceVerified", None)
        manifest.update(persistenceStatus="pending", persistenceError="", manifestBytes=0, manifestSha256="")
        (directory / "manifest.json").write_bytes(json.dumps(manifest).encode())
        data.update(persistenceStatus="unverified", persistenceVerified=False, persistenceError="STATE_REPLACE_FAILED",
                    manifestBytes=0, manifestSha256="")
        if self.failure == "state_false_verified":
            data.update(persistenceStatus="verified", persistenceVerified=True, persistenceError="")
        elif self.failure == "state_wrong_error":
            data["persistenceError"] = "MANIFEST_REPLACE_FAILED"
        elif self.failure == "state_false_hash":
            data["manifestSha256"] = "a" * 64
        elif self.failure == "state_mutated":
            self.state_path.write_bytes(self.state_path.read_bytes() + b" ")
        elif self.failure == "state_deleted":
            self.state_path.unlink()
        elif self.failure == "state_staging":
            self.state_path.with_name(self.state_path.name + ".owned.tmp").write_bytes(b"orphan")
        elif self.failure == "state_manifest_identity":
            manifest["snapshotId"] = "foreign"
            (directory / "manifest.json").write_bytes(json.dumps(manifest).encode())
        elif self.failure == "state_extra_png":
            (directory / "unaccepted.png").write_bytes(b"extra")

    async def call_tool(self, proxy, request, *, meta):
        if request["toolName"] == "unity_playmode_stop":
            assert self.lease_released, "The exact file lease must be released before stopping owned PlayMode."
        original_failure = self.failure
        if request["toolName"] == "unity_playmode_stop" and self.failure == "state_release_failure_stop_lost":
            self.failure = "stop_lost"
        try:
            response = await super().call_tool(proxy, request, meta=meta)
        finally:
            self.failure = original_failure
        if request["toolName"] == "unity_playmode_stop" and self.failure in (
                "recover", "recover_without_marker", "recover_business_changed"):
            for data in self.values.values():
                manifest = self.client.PROJECT / data["manifestPath"]
                raw = manifest.read_bytes()
                data.update(persistenceStatus="verified", persistenceVerified=True, persistenceError="",
                            manifestBytes=len(raw), manifestSha256=hashlib.sha256(raw).hexdigest(),
                            persistenceRecovered=self.failure != "recover_without_marker")
                if self.failure == "recover_business_changed":
                    data["endedAtUtcMs"] += 1
                state = copy.deepcopy(data)
                state.pop("persistenceVerified", None)
                self.state_path.write_bytes(json.dumps(state).encode())
        if request["toolName"] == "unity_snapshot_capture":
            data = self.values[response.structuredContent["data"]["snapshotId"]]
            data.update(snapshotSequence=1, persistenceStatus="verified", persistenceError="")
            directory = self.client.PROJECT / data["outputDirectory"]
            directory.mkdir(parents=True)
            self.state_path = self.client.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (data["snapshotId"] + ".json")
            self.state_path.parent.mkdir(parents=True)
            self.state_path.write_bytes(json.dumps(data).encode())
            response.structuredContent["data"] = stringify_wire_ids(copy.deepcopy(data))
        return response


class FakeStatePixels(FakeStateFailure):
    """Synthetic resumed Display 0 boundary; not a real Unity pixel-source proof."""
    def __init__(self, client, failure=None):
        super().__init__(client, failure)
        self.resume_count = 0
        self.lease_acquired = False

    async def call_tool(self, proxy, request, *, meta):
        if request["toolName"] == "unity_playmode_resume":
            from upilot_mcp.mcp_tools.status_tools import unity_playmode_resume
            inspect.signature(unity_playmode_resume).bind(**request["args"])
            assert self.mode == "pause" and self.lease_acquired and not self.lease_released
            self.resume_count += 1
            assert self.resume_count == 1
            self.calls.append((request["toolName"], copy.deepcopy(request["args"]), copy.deepcopy(meta)))
            self.mode = "play"
            if self.failure == "resume_lost":
                raise TimeoutError("secret-resume-text")
            return SimpleNamespace(structuredContent={"ok": True, "data": {
                "confirmed": self.failure != "resume_unconfirmed", "state": "play"}, "requestId": "resume"},
                content=[], isError=False)
        return await super().call_tool(proxy, request, meta=meta)

    def finish(self, data):
        assert self.mode == "play" and self.resume_count == 1 and self.lease_acquired and not self.lease_released
        directory = self.client.PROJECT / data["outputDirectory"]
        image_path = directory / "gameview-state-pixels.color.png"
        Image.new("RGBA", (32, 32), (21, 71, 111, 255)).save(image_path)
        raw = image_path.read_bytes()
        artifact = {"artifactId": "artifact-" + "d" * 32, "targetId": "gameview-state-pixels", "role": "color",
                    "path": str(image_path.relative_to(self.client.PROJECT)).replace("\\", "/"), "mimeType": "image/png",
                    "encoding": "rgba8", "units": "", "width": 32, "height": 32, "bytes": len(raw),
                    "sha256": hashlib.sha256(raw).hexdigest(), "acceptedAsEvidence": False}
        target = {"targetId": "gameview-state-pixels", "kind": "gameView", "status": "completed", "success": True,
                  "artifactIds": [artifact["artifactId"]],
                  "gameView": {"targetDisplay": 0, "playMode": True, "screenWidth": 640, "screenHeight": 360,
                               "requestedWidth": 32, "requestedHeight": 32, "outputWidth": 32, "outputHeight": 32},
                  "provenance": {"matchedInstanceId": "display:0", "unityProcessId": 1, "pixelSourceVerified": True,
                                 "degraded": False, "occlusionSensitive": False,
                                 "captureApi": "WaitForEndOfFrame+ScreenCapture.CaptureScreenshotIntoRenderTexture"}}
        data.update(status="completed", phase="completed", terminal=True, success=True, cancelRequested=False,
                    endedAtUtcMs=2000, snapshotSequence=4, persistenceSchemaVersion=2, targets=[target], failures=[], artifacts=[artifact])
        self.persist(data)
        if self.failure == "accepted_pixels":
            artifact["acceptedAsEvidence"] = True
        elif self.failure == "pixel_hash":
            artifact["sha256"] = "f" * 64
        elif self.failure == "pixel_bool_bytes":
            artifact["bytes"] = True
        elif self.failure == "pixel_path":
            artifact["path"] = "../foreign.png"
        elif self.failure == "pixel_missing":
            image_path.unlink()
        elif self.failure == "pixel_source":
            target["provenance"]["pixelSourceVerified"] = False
        elif self.failure == "pixel_pid":
            target["provenance"]["unityProcessId"] = 2
        elif self.failure == "pixel_display":
            target["gameView"]["targetDisplay"] = 1
        elif self.failure == "pixel_artifact_id":
            target["artifactIds"] = ["foreign"]
        elif self.failure == "pixel_business_failure":
            data["success"] = False


def execute_state_failure(client, monkeypatch, failure=None, *, successful_pixels=False):
    fake = FakeStatePixels(client, failure) if successful_pixels else FakeStateFailure(client, failure)
    fake.lease_released = False
    @contextmanager
    def hold(baseline):
        try:
            if failure == "state_lease_failure":
                raise client.CallPreflightError("owned lease unavailable")
            file, state = client.read_json(fake.state_path)
            fake.lease_acquired = True
            yield {"file": file, "state": stringify_wire_ids(state), "replacementDenied": True}
        finally:
            fake.lease_released = True
            if failure in ("state_release_failure", "state_release_failure_stop_lost"):
                raise client.CallPreflightError("owned lease release failed")
    monkeypatch.setattr(client, "hold_owned_state", hold)
    monkeypatch.setattr(client, "connect", fake.connect)
    report, error = {"calls": []}, None
    try:
        asyncio.run(client.exercise_paused_timeout(8011, report, state_failure=True, successful_pixels=successful_pixels))
    except BaseException as exc:
        error = exc
    return fake, report, error


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
def test_state_failure_exact_old_state_terminal_manifest_and_release_before_stop(client, monkeypatch):
    fake, report, error = execute_state_failure(client, monkeypatch)
    assert error is None and report["result"] == "observed" and report["cleanupVerified"]
    assert fake.starts == fake.start_count == fake.stop_count == 1 and fake.closed and fake.lease_released
    observed = report["cases"][0]
    assert observed["terminal"]["persistenceError"] == "STATE_REPLACE_FAILED"
    assert observed["files"]["failureEvidenceVerified"] and observed["files"]["verified"] is False
    assert report["stateHandleReleased"] is True
    assert len(observed["failureBoundaryCopies"]) == 2
    assert all(Path(value["path"]).is_file() for value in observed["failureBoundaryCopies"])


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
@pytest.mark.parametrize("failure", ["state_false_verified", "state_wrong_error", "state_false_hash", "state_mutated",
    "state_deleted", "state_staging", "state_manifest_identity", "state_extra_png", "state_lease_failure",
    "state_release_failure", "state_release_failure_stop_lost", "ack_lost", "observation_error", "timeout_message", "too_fast", "stop_lost"])
def test_state_failure_refuses_false_evidence_releases_and_never_replays(client, monkeypatch, failure):
    fake, report, error = execute_state_failure(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and fake.closed
    assert fake.starts == fake.start_count == 1 and fake.stop_count <= 1
    assert not any(name in {"unity_snapshot_cancel", "unity_playmode_resume", "unity_service_restart", "unity_compile"}
                   for name, _, _ in fake.calls)
    if failure not in {"ack_lost"}:
        assert fake.lease_released
    if failure in ("state_release_failure", "state_release_failure_stop_lost"):
        assert report["cleanupError"]["message"] == "Owned state handle release failed."
        assert report.get("stateHandleReleased") is not True


@pytest.mark.skipif(os.name != "nt", reason="Requires actual Windows sharing semantics")
def test_state_failure_native_handle_denies_replace_and_releases_exact_owned_file(client):
    baseline = {key: "owned" for key in client.FROZEN}
    baseline["snapshotId"] = "snapshot-" + "a" * 32
    state = {**baseline, "terminal": False, "persistenceStatus": "verified", "persistenceError": "", "snapshotSequence": 1}
    path = client.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (baseline["snapshotId"] + ".json")
    path.parent.mkdir(parents=True)
    raw = json.dumps(state).encode()
    path.write_bytes(raw)
    replacement = path.with_name(path.name + ".owned.tmp")
    replacement.write_bytes(b"replacement")
    with client.hold_owned_state(baseline) as lease:
        with pytest.raises(PermissionError):
            replacement.replace(path)
        assert path.read_bytes() == raw and lease["file"]["sha256"] == hashlib.sha256(raw).hexdigest()
    replacement.replace(path)  # A release proof, not an extra public Snapshot or repair of its receipt.
    assert path.read_bytes() == b"replacement"


def test_state_failure_cli_is_exclusive_and_uses_only_owned_timeout_entry(client, monkeypatch, capsys):
    for mode in ("--paused-timeout", "--manifest-write-failure"):
        with pytest.raises(SystemExit) as error:
            asyncio.run(client.main(["--execute", "--state-write-failure", mode]))
        assert error.value.code == 2
    seen = []
    async def exercise(port, report, *, state_failure=False):
        seen.append((port, state_failure))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise_paused_timeout", exercise)
    assert asyncio.run(client.main(["--execute", "--state-write-failure"])) == 0
    assert seen == [(8011, True)] and json.loads(capsys.readouterr().out)["result"] == "observed"


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
@pytest.mark.parametrize("failure", ["recover", "recover_without_marker", "recover_business_changed"])
def test_state_failure_recovery_is_separate_and_preserves_original_business_outcome(client, monkeypatch, failure):
    fake, report, error = execute_state_failure(client, monkeypatch, failure)
    observed = report["cases"][0]
    assert observed["terminal"]["persistenceStatus"] == "unverified"
    assert observed["files"]["failureEvidenceVerified"] and observed["files"]["verified"] is False
    assert len(observed["failureBoundaryCopies"]) == 2
    assert fake.starts == fake.start_count == fake.stop_count == 1 and fake.closed and fake.lease_released
    if failure == "recover":
        assert error is None and report["result"] == "observed" and report["cleanupVerified"]
        assert report["persistenceRecoveryObserved"] is True
        assert report["postCleanupSnapshot"]["persistenceRecovered"] is True
        assert report["postCleanupFiles"]["verified"] is True
    else:
        assert error is not None and report["result"] == "unknown" and report["cleanupError"]
        assert report.get("cleanupVerified") is not True


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
def test_state_pixels_one_resume_unaccepted_png_and_exact_old_state(client, monkeypatch):
    fake, report, error = execute_state_failure(client, monkeypatch, successful_pixels=True)
    assert error is None and report["result"] == "observed" and report["cleanupVerified"]
    assert fake.closed and fake.starts == fake.start_count == fake.resume_count == fake.stop_count == 1
    assert fake.lease_released and report["stateHandleReleased"]
    observed = report["cases"][0]
    assert observed["terminal"]["success"] is True and observed["terminal"]["persistenceVerified"] is False
    assert observed["terminal"]["artifacts"][0]["acceptedAsEvidence"] is False
    assert observed["files"]["pixel"]["decoded"] and observed["files"]["failureEvidenceVerified"]
    assert observed["files"]["oldState"] == observed["lockedState"]["file"]
    assert len(observed["failureBoundaryCopies"]) == 2
    assert report["persistenceRecoveryObserved"] is False
    names = [name for name, _, _ in fake.calls]
    assert names.index("unity_snapshot_capture") < names.index("unity_playmode_resume") < names.index("unity_playmode_stop")
    assert not any("cancel" in name or "restart" in name or "compile" in name for name in names)
    assert "successful pixels" not in report["notCovered"]


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
@pytest.mark.parametrize("failure", ["resume_lost", "resume_unconfirmed", "state_lease_failure", "state_release_failure",
    "accepted_pixels", "pixel_hash", "pixel_bool_bytes", "pixel_path", "pixel_missing", "pixel_source", "pixel_pid",
    "pixel_display", "pixel_artifact_id", "pixel_business_failure", "state_false_verified", "state_wrong_error",
    "state_false_hash", "state_mutated", "state_deleted", "state_staging", "state_manifest_identity", "state_extra_png",
    "collect_sequence", "collect_end", "foreign_command", "domain_change", "host_leak", "scene_changed",
    "state_release_failure_stop_lost"])
def test_state_pixels_refuses_false_evidence_or_unknown_action_without_replay(client, monkeypatch, failure):
    fake, report, error = execute_state_failure(client, monkeypatch, failure, successful_pixels=True)
    assert error is not None and report["result"] == "unknown" and fake.closed and fake.lease_released
    assert fake.starts == fake.start_count == 1 and fake.resume_count <= 1 and fake.stop_count <= 1
    assert report.get("firstError") or report.get("cleanupError")
    assert not any("cancel" in name or "restart" in name or "compile" in name for name, _, _ in fake.calls)
    if failure == "state_lease_failure":
        assert fake.resume_count == 0
    if failure in ("state_release_failure", "state_release_failure_stop_lost"):
        assert report["cleanupError"]["message"] == "Owned state handle release failed."
        assert report.get("stateHandleReleased") is not True


@pytest.mark.skipif(os.name != "nt", reason="Opt-in state replacement denial is Windows-only")
@pytest.mark.parametrize("failure", ["recover", "recover_without_marker", "recover_business_changed"])
def test_state_pixels_recovery_is_separate_from_original_unaccepted_pixels(client, monkeypatch, failure):
    fake, report, error = execute_state_failure(client, monkeypatch, failure, successful_pixels=True)
    assert fake.starts == fake.start_count == fake.resume_count == fake.stop_count == 1 and fake.lease_released
    assert report["cases"][0]["terminal"]["persistenceStatus"] == "unverified"
    assert report["cases"][0]["terminal"]["artifacts"][0]["acceptedAsEvidence"] is False
    if failure == "recover":
        assert error is None and report["cleanupVerified"] and report["persistenceRecoveryObserved"]
        assert report["postCleanupFiles"]["verified"]
        assert report["postCleanupFiles"]["pixel"]["decoded"]
        assert report["postCleanupFiles"]["pixel"] == report["cases"][0]["files"]["pixel"]
        assert report["postCleanupSnapshot"]["artifacts"][0]["acceptedAsEvidence"] is False
    else:
        assert error is not None and report["result"] == "unknown" and report["cleanupError"]


def test_state_pixels_requires_state_boundary_before_connect(client, monkeypatch):
    async def forbidden(port):
        raise AssertionError("Must reject inconsistent mode before connect")
    monkeypatch.setattr(client, "connect", forbidden)
    with pytest.raises(client.CallPreflightError):
        asyncio.run(client.exercise_paused_timeout(8011, {"calls": []}, successful_pixels=True))


def test_state_pixels_cli_exclusive_and_exact_entry(client, monkeypatch, capsys):
    for mode in ("--state-write-failure", "--paused-timeout", "--manifest-write-failure"):
        with pytest.raises(SystemExit) as error:
            asyncio.run(client.main(["--execute", "--state-pixels-failure", mode]))
        assert error.value.code == 2
    seen = []
    async def exercise(port, report, *, state_failure=False, successful_pixels=False):
        seen.append((port, state_failure, successful_pixels))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise_paused_timeout", exercise)
    assert asyncio.run(client.main(["--execute", "--state-pixels-failure"])) == 0
    assert seen == [(8011, True, True)] and json.loads(capsys.readouterr().out)["result"] == "observed"


class FakeStateReload(FakeStatePixels):
    """Same owned record, independent normal Reload; never a live recovery claim."""
    def __init__(self, original, failure=None):
        self.__dict__.update(original.__dict__)
        self.failure, self.calls, self.closed = failure, [], False
        self.reload_count = 0

    async def call_tool(self, proxy, request, *, meta):
        name, args = request["toolName"], request["args"]
        if name == "unity_reflection_find" or (name == "unity_reflection_call" and args.get("methodName") == "RequestScriptReload"):
            from upilot_mcp.mcp_tools import reflection_tools
            inspect.signature(getattr(reflection_tools, name)).bind(**args)
            self.calls.append((name, copy.deepcopy(args), copy.deepcopy(meta)))
            assert args["typeName"] == "UnityEditor.EditorUtility" and args["methodName"] == "RequestScriptReload"
            if name == "unity_reflection_find":
                data = {"methods": [{"typeName": args["typeName"], "methodName": args["methodName"],
                                    "isStatic": True, "returnType": "System.Void", "parameters": []}]}
            else:
                self.reload_count += 1
                assert self.reload_count == 1 and args["kind"] == "method" and args["parameters"] == []
                for data in self.values.values():
                    raw = (self.client.PROJECT / data["manifestPath"]).read_bytes()
                    data.update(persistenceStatus="verified", persistenceVerified=True, persistenceError="",
                                persistenceRecovered=self.failure != "recovery_marker",
                                manifestBytes=len(raw), manifestSha256=hashlib.sha256(raw).hexdigest())
                    if self.failure == "recovery_business":
                        data["endedAtUtcMs"] += 1
                    state = copy.deepcopy(data)
                    state.pop("persistenceVerified", None)
                    self.state_path.write_bytes(json.dumps(state).encode())
                if self.failure == "reload_receipt_lost":
                    raise TimeoutError("private-dispatch-detail")
                data = {"status": "Succeeded"}
            return SimpleNamespace(structuredContent=stringify_wire_ids({"ok": True, "data": data}), content=[], isError=False)
        response = await super().call_tool(proxy, request, meta=meta)
        if name == "unity_mcp_status" and self.reload_count:
            response.structuredContent["data"]["executionState"]["domainGeneration"] = "recovered-domain"
            if self.failure == "recovery_process":
                response.structuredContent["data"]["session"]["processId"] = 4
        return response


def prepare_recovery(client, monkeypatch, failure=None):
    original, source, error = execute_state_failure(client, monkeypatch, successful_pixels=True)
    assert error is None and source["result"] == "observed"
    fake = FakeStateReload(original, failure)
    if failure == "unreleased":
        source["stateHandleReleased"] = False
    elif failure == "foreign_process":
        source["identityAfter"]["serverPid"] = 1234
    elif failure == "tampered_manifest":
        path = client.PROJECT / source["cases"][0]["terminal"]["manifestPath"]
        path.write_bytes(path.read_bytes() + b" ")
    path = client.PROJECT / "Log" / "P0P1" / ("v09-failure-observation-" + "e" * 32 + ".json")
    path.write_bytes(json.dumps(source).encode())
    original_bytes = path.read_bytes()
    monkeypatch.setattr(client, "connect", fake.connect)
    return fake, path, original_bytes


def execute_recovery(client, path):
    report, error = {"calls": []}, None
    try:
        asyncio.run(client.exercise_state_recovery(8011, report, path))
    except BaseException as exc:
        error = exc
    return report, error


@pytest.mark.skipif(os.name != "nt", reason="Original owned state-failure scenario is Windows-only")
@pytest.mark.parametrize("failure", [None, "reload_receipt_lost"])
def test_state_reload_recovers_original_receipt_without_recapture_or_replay(client, monkeypatch, failure):
    fake, path, original_bytes = prepare_recovery(client, monkeypatch, failure)
    report, error = execute_recovery(client, path)
    assert error is None and report["result"] == "observed" and fake.closed
    assert report["persistenceRecoveryObserved"] and fake.reload_count == 1
    assert report["identityBefore"]["domainGeneration"] != report["identityAfter"]["domainGeneration"]
    for key in ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch"):
        assert report["identityBefore"][key] == report["identityAfter"][key]
    assert report["recoveredFiles"]["manifest"] == report["originalFiles"]["manifest"]
    assert report["recoveredFiles"]["pixel"] == report["originalFiles"]["pixel"]
    assert report["recoveredFiles"]["state"]["sha256"] != report["originalFiles"]["oldState"]["sha256"]
    assert report["recoveredSnapshot"]["artifacts"][0]["acceptedAsEvidence"] is False
    assert path.read_bytes() == original_bytes
    names = [name for name, _, _ in fake.calls]
    assert names.count("unity_reflection_call") == 1 and "unity_snapshot_capture" not in names
    assert not any("playmode" in name or "compile" in name or "restart" in name or "cancel" in name for name in names)
    if failure:
        assert report["reloadReceiptError"]["type"] == "TimeoutError"
        assert "private-dispatch-detail" not in json.dumps(report)


@pytest.mark.skipif(os.name != "nt", reason="Original owned state-failure scenario is Windows-only")
@pytest.mark.parametrize("failure", ["unreleased", "foreign_process", "busy", "dirty_scene", "tampered_manifest"])
def test_state_reload_unsafe_source_or_environment_refuses_before_dispatch(client, monkeypatch, failure):
    fake, path, original_bytes = prepare_recovery(client, monkeypatch, failure)
    report, error = execute_recovery(client, path)
    assert isinstance(error, client.CallPreflightError)
    assert fake.reload_count == 0 and not report.get("reloadDispatchAttempted")
    assert path.read_bytes() == original_bytes
    assert not any(name == "unity_reflection_call" for name, _, _ in fake.calls)


@pytest.mark.skipif(os.name != "nt", reason="Original owned state-failure scenario is Windows-only")
@pytest.mark.parametrize("failure", ["recovery_marker", "recovery_business", "recovery_process"])
def test_state_reload_unverified_restoration_never_replays_or_claims_success(client, monkeypatch, failure):
    fake, path, original_bytes = prepare_recovery(client, monkeypatch, failure)
    report, error = execute_recovery(client, path)
    assert isinstance(error, client.CallPreflightError) and fake.closed
    assert fake.reload_count == 1 and report["result"] == "unknown"
    assert report["persistenceRecoveryObserved"] is False and path.read_bytes() == original_bytes
    assert [name for name, _, _ in fake.calls].count("unity_reflection_call") == 1


def test_state_reload_cli_requires_opt_in_and_exclusive_mode(client, monkeypatch, capsys):
    with pytest.raises(SystemExit):
        asyncio.run(client.main(["--recover-state-report", "original.json"]))
    with pytest.raises(SystemExit):
        asyncio.run(client.main(["--execute", "--recover-state-report", "original.json", "--state-pixels-failure"]))
    capsys.readouterr()
    seen = []
    async def exercise(port, report, original_path):
        seen.append((port, original_path))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise_state_recovery", exercise)
    assert asyncio.run(client.main(["--execute", "--recover-state-report", "original.json"])) == 0
    assert seen == [(8011, Path("original.json"))]
    assert json.loads(capsys.readouterr().out)["result"] == "observed"


class FakeInflightReload(FakePausedSnapshot):
    """Owned files and actual MCP signatures; no claim of real Unity Reload."""
    def __init__(self, client, failure=None):
        super().__init__(client, failure)
        self.arm_count = self.reload_count = self.disarm_count = 0
        self.armed = None

    def persist(self, data):
        directory = self.client.PROJECT / data["outputDirectory"]
        directory.mkdir(parents=True, exist_ok=True)
        manifest = copy.deepcopy(data)
        manifest.pop("persistenceVerified", None)
        manifest.update(persistenceStatus="pending", manifestBytes=0, manifestSha256="", persistenceError="")
        raw = json.dumps(manifest).encode()
        (directory / "manifest.json").write_bytes(raw)
        data.update(manifestBytes=len(raw), manifestSha256=hashlib.sha256(raw).hexdigest())
        state = copy.deepcopy(data)
        state.pop("persistenceVerified", None)
        path = self.client.PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (data["snapshotId"] + ".json")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(json.dumps(state).encode())

    async def call_tool(self, proxy, request, *, meta):
        name, args = request["toolName"], request["args"]
        if name == "unity_reflection_find" or (name == "unity_reflection_call" and args.get("methodName")):
            from upilot_mcp.mcp_tools import reflection_tools
            assert proxy == "unity_tool_call" and not self.closed
            inspect.signature(getattr(reflection_tools, name)).bind(**args)
            self.calls.append((name, copy.deepcopy(args), copy.deepcopy(meta)))
            method = args["methodName"]
            assert args["typeName"] == ("UnityEditor.EditorUtility" if method == "RequestScriptReload" else self.client.RELOAD_WITNESS)
            if name == "unity_reflection_find":
                names = {"RequestScriptReload": [], "Arm": ["snapshotId", "nonce"], "Disarm": ["nonce"]}[method]
                data = {"methods": [{"typeName": args["typeName"], "methodName": method, "isStatic": True,
                    "returnType": {"RequestScriptReload": "System.Void", "Arm": "System.String", "Disarm": "System.Boolean"}[method],
                    "parameters": [{"name": n, "type": "System.String"} for n in names]}]}
                if self.failure == "signature" and method == "Arm":
                    data["methods"][0]["parameters"][0]["type"] = "System.Int32"
            elif method == "Arm":
                self.arm_count += 1
                assert self.arm_count == 1 and args["kind"] == "method" and args["isStatic"]
                assert args["parameterTypeNames"] == ["System.String", "System.String"]
                snapshot_id, nonce = args["parameters"]
                job = self.values[snapshot_id]
                assert job["requestKey"] == "v09-inflight-" + nonce
                directory = self.client.PROJECT / "Log" / "P0P1" / ("v09-inflight-" + nonce) / "boundary"
                directory.mkdir()
                self.armed = {"nonce": nonce, "directory": str(directory), "stage": "armed", "error": "",
                              "armedAtUtcMs": 2000, "observedAtUtcMs": 0, "isPlaying": True, "isPaused": True,
                              "hostCount": 1, "job": copy.deepcopy(job)}
                (directory / "armed.json").write_bytes(json.dumps(self.armed).encode())
                if self.failure == "arm_receipt_lost":
                    raise TimeoutError("private-arm-detail")
                value = json.dumps(self.armed)
                if self.failure == "arm_receipt_mismatch":
                    value = value.replace('"armedAtUtcMs": 2000', '"armedAtUtcMs": 2001')
                data = {"status": "Succeeded", "resultValue": {"typeName": "System.String", "serializationStatus": "inline",
                                                                "valueJson": json.dumps(value)}}
            elif method == "Disarm":
                self.disarm_count += 1
                assert self.disarm_count == 1 and args["parameters"] == [self.armed["nonce"]]
                assert args["parameterTypeNames"] == ["System.String"]
                if self.failure in {"disarm_failure", "boundary_and_disarm"}:
                    raise TimeoutError("private-disarm-detail")
                data = {"status": "Succeeded", "resultValue": {"typeName": "System.Boolean", "serializationStatus": "inline",
                                                                "valueJson": "false" if self.reload_count else "true"}}
            else:
                assert method == "RequestScriptReload" and args["parameters"] == []
                self.reload_count += 1
                assert self.reload_count == 1 and args["kind"] == "method" and self.armed is not None
                boundary = copy.deepcopy(self.armed)
                boundary.update(stage="beforeAssemblyReload", observedAtUtcMs=2100)
                if self.failure == "witness_terminal":
                    boundary["job"]["terminal"] = True
                elif self.failure == "witness_phase":
                    boundary["job"]["phase"] = "writing"
                elif self.failure == "witness_host":
                    boundary["hostCount"] = 0
                elif self.failure in {"witness_error", "boundary_and_disarm"}:
                    boundary["error"] = "Witness could not verify the host."
                elif self.failure == "witness_id":
                    boundary["job"]["snapshotId"] = "snapshot-" + "f" * 32
                elif self.failure == "witness_nonce":
                    boundary["nonce"] = "f" * 32
                elif self.failure == "witness_time":
                    boundary["observedAtUtcMs"] = 12001
                elif self.failure == "witness_arm_time":
                    boundary["armedAtUtcMs"] += 1
                if self.failure != "missing_boundary":
                    (Path(self.armed["directory"]) / "before-reload.json").write_bytes(json.dumps(boundary).encode())
                for job in self.values.values():
                    job.update(status="failed", phase="failed", terminal=True, success=False,
                               endedAtUtcMs=3000, snapshotSequence=2,
                               failures=[{"targetId": "", "code": self.client.INFLIGHT_CASE[2], "stage": "rendering"}])
                    if self.failure == "terminal_code":
                        job["failures"][0]["code"] = self.client.TIMEOUT_CASE[2]
                    elif self.failure == "terminal_early":
                        job["endedAtUtcMs"] = 2099
                    elif self.failure == "terminal_artifact":
                        job["artifacts"] = [{"acceptedAsEvidence": True}]
                    self.persist(job)
                if self.failure == "reload_receipt_lost":
                    raise TimeoutError("private-reload-detail")
                data = {"status": "Succeeded"}
            return SimpleNamespace(structuredContent=stringify_wire_ids({"ok": True, "data": data}), content=[], isError=False)
        response = await super().call_tool(proxy, request, meta=meta)
        data = response.structuredContent["data"]
        if name == "unity_snapshot_capture":
            job = self.values[data["snapshotId"]]
            job.update(status="running", phase="rendering", terminal=False, success=False, cancelRequested=False,
                       endedAtUtcMs=0, targets=[], artifacts=[], failures=[], snapshotSequence=1,
                       persistenceStatus="verified", persistenceVerified=True, persistenceError="", persistenceSchemaVersion=2)
            self.persist(job)
            response.structuredContent["data"] = stringify_wire_ids(copy.deepcopy(job))
        elif name == "unity_tools_find" and args["query"] == "RequestScriptReload" and self.failure == "semantic_reload":
            data["count"] = 1
        elif name == "unity_gameobject_find":
            if self.reload_count:
                data["gameObjects"] = []
            elif self.failure == "host_missing" and self.starts:
                data["gameObjects"] = []
        elif name == "unity_mcp_status" and self.reload_count:
            if self.mode != "edit" and self.failure != "domain_unchanged":
                data["executionState"]["domainGeneration"] = "reload-domain"
                if self.failure != "bridge_unchanged":
                    data["session"]["sessionId"] = "reload-bridge"
            if self.failure == "reload_process":
                data["session"]["processId"] = 99
            elif self.failure == "reload_lifetime":
                data["executionState"]["producerEpoch"] = "other-lifetime"
        return response


def execute_inflight(client, monkeypatch, failure=None):
    fake = FakeInflightReload(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    report, error = {"calls": []}, None
    async def run():
        # Only the unchanged-domain test has an observation-window timeout. It never renews or replays the action.
        if failure == "domain_unchanged":
            await asyncio.wait_for(client.exercise_paused_timeout(8011, report, in_flight_reload=True), timeout=0.05)
        else:
            await client.exercise_paused_timeout(8011, report, in_flight_reload=True)
    try:
        asyncio.run(run())
    except BaseException as exc:
        error = exc
    return fake, report, error


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
@pytest.mark.parametrize("failure", [None, "reload_receipt_lost"])
def test_inflight_reload_requires_real_boundary_and_single_dispatch(client, monkeypatch, failure):
    fake, report, error = execute_inflight(client, monkeypatch, failure)
    assert error is None and report["result"] == "observed" and fake.closed
    assert (fake.start_count, fake.starts, fake.arm_count, fake.reload_count, fake.disarm_count, fake.stop_count) == (1, 1, 1, 1, 1, 1)
    case = report["cases"][0]
    assert case["reloadBoundary"]["data"]["job"]["terminal"] is False
    assert case["reloadBoundary"]["data"]["job"]["status"] == "running"
    assert case["reloadBoundary"]["data"]["hostCount"] == 1
    assert case["terminal"]["failures"][0]["code"] == client.INFLIGHT_CASE[2]
    assert case["terminal"]["artifacts"] == [] and report["cleanupVerified"]
    assert report["identityAfterReload"]["domainGeneration"] != case["identityPaused"]["domainGeneration"]
    assert report["identityAfterReload"]["producerEpoch"] == case["identityPaused"]["producerEpoch"]
    assert report["postCleanupFiles"] == case["files"]
    assert report["witnessCleanup"] == {"verified": True, "changed": False}
    for file in (case["files"]["manifest"], case["files"]["state"], case["armedWitness"]["file"], case["reloadBoundary"]["file"]):
        raw = Path(file["path"]).read_bytes()
        assert len(raw) == file["bytes"] and hashlib.sha256(raw).hexdigest() == file["sha256"]
    names = [name for name, _, _ in fake.calls]
    assert not any("compile" in name or "restart" in name or "cancel" in name or "resume" in name for name in names)
    if failure:
        assert report["reloadReceiptError"]["type"] == "TimeoutError"
        assert "private-reload-detail" not in json.dumps(report)


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
@pytest.mark.parametrize("failure", ["signature", "semantic_reload", "busy", "capture", "dirty_scene", "grant_missing", "loaded"])
def test_inflight_reload_unsafe_preflight_never_enters_mode(client, monkeypatch, failure):
    fake, report, error = execute_inflight(client, monkeypatch, failure)
    assert isinstance(error, client.CallPreflightError) and report["result"] == "unknown"
    assert (fake.start_count, fake.starts, fake.arm_count, fake.reload_count, fake.stop_count) == (0, 0, 0, 0, 0)


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
@pytest.mark.parametrize("failure", ["missing_boundary", "witness_terminal", "witness_phase", "witness_host", "witness_error",
    "witness_id", "witness_nonce", "witness_time", "witness_arm_time", "terminal_code", "terminal_early", "terminal_artifact", "bridge_unchanged", "read_mutates_file"])
def test_inflight_reload_missing_or_invalid_boundary_never_counts_as_coverage(client, monkeypatch, failure):
    fake, report, error = execute_inflight(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and report["firstError"]
    assert fake.reload_count == 1 and fake.arm_count == 1 and fake.stop_count == 1 and fake.disarm_count == 1
    assert report["cleanupVerified"] and "private-" not in json.dumps(report)


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
@pytest.mark.parametrize("failure", ["host_missing", "arm_receipt_lost", "arm_receipt_mismatch", "disarm_failure", "domain_unchanged"])
def test_inflight_reload_failure_cleans_only_owned_witness_and_mode(client, monkeypatch, failure):
    fake, report, error = execute_inflight(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown"
    assert fake.start_count == fake.starts == fake.stop_count == 1
    assert fake.reload_count == (1 if failure in {"disarm_failure", "domain_unchanged"} else 0)
    assert fake.disarm_count == (0 if failure == "host_missing" else 1)
    assert report["firstError"] if failure != "disarm_failure" else report["cleanupError"]
    assert "private-" not in json.dumps(report)


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
@pytest.mark.parametrize("failure", ["reload_process", "reload_lifetime", "foreign_transition", "foreign_command"])
def test_inflight_reload_changed_ownership_or_identity_never_stops_foreign_state(client, monkeypatch, failure):
    fake, report, error = execute_inflight(client, monkeypatch, failure)
    assert error is not None and report["result"] == "unknown" and report.get("cleanupError")
    assert fake.stop_count == fake.disarm_count == 0
    assert fake.reload_count == (1 if failure.startswith("reload_") else 0)


def test_inflight_reload_cli_requires_optin_exclusive_mode_and_routes(client, monkeypatch, capsys):
    with pytest.raises(SystemExit):
        asyncio.run(client.main(["--in-flight-reload"]))
    with pytest.raises(SystemExit):
        asyncio.run(client.main(["--execute", "--in-flight-reload", "--paused-timeout"]))
    capsys.readouterr()
    seen = []
    async def exercise(port, report, **options):
        seen.append((port, options))
        report["result"] = "observed"
    monkeypatch.setattr(client, "exercise_paused_timeout", exercise)
    assert asyncio.run(client.main(["--execute", "--in-flight-reload"])) == 0
    assert seen == [(8011, {"in_flight_reload": True})]
    assert json.loads(capsys.readouterr().out)["result"] == "observed"


def test_inflight_reload_internal_modes_are_exclusive(client):
    with pytest.raises(client.CallPreflightError, match="one exact"):
        asyncio.run(client.exercise_paused_timeout(8011, {"calls": []}, in_flight_reload=True, state_failure=True))


def test_inflight_reload_nonwindows_refuses_before_connect(client, monkeypatch):
    monkeypatch.setattr(client, "os", SimpleNamespace(name="posix"))
    with pytest.raises(client.CallPreflightError, match="Windows"):
        asyncio.run(client.exercise_paused_timeout(8011, {"calls": []}, in_flight_reload=True))


@pytest.mark.skipif(os.name != "nt", reason="Exact compiled witness is canonical Windows-only")
def test_inflight_reload_first_boundary_error_is_not_replaced_by_cleanup_error(client, monkeypatch):
    fake, report, error = execute_inflight(client, monkeypatch, "boundary_and_disarm")
    assert error is not None and report["result"] == "unknown"
    assert "Reload boundary" in report["firstError"]["message"]
    assert report["cleanupError"]["type"] == "TimeoutError"
    assert fake.reload_count == fake.disarm_count == fake.stop_count == 1
    assert "private-disarm-detail" not in json.dumps(report)
