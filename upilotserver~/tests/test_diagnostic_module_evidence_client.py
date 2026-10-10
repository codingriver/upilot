"""N01 evidence-client contracts. Fake transport only; no Unity compilation or execution."""
import asyncio
import copy
from contextlib import asynccontextmanager
import importlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    value = importlib.import_module("p1_diagnostic_module_evidence")
    monkeypatch.setattr(value, "PROJECT", tmp_path)
    return value


def status(project):
    entries = [
        {"module": "upilot_mcp.domain.execution_service", "entry": "ExecutionDomainService.diagnostic_module", "status": "equivalent"},
        {"module": "upilot_mcp.mcp_tools.execution_tools", "entry": "diagnostic_module", "status": "equivalent"},
    ]
    return {
        "paths": {"unityProjectAbsolute": str(project)}, "connected": True, "serverReady": True,
        "executionState": {"ready": False, "blocked": True, "blockedReason": "PlayMode",
            "compilePhase": "completed", "isCompiling": False, "isUpdating": False,
            "authoritative": True, "isStale": False,
            "playModeState": "play", "isPaused": False, "producerEpoch": "producer", "domainGeneration": 68},
        "session": {"processId": 123, "reportedProcessId": 123, "processCreatedAt": 456,
            "identityVerified": True, "sessionId": "bridge"},
        "runtimeIdentity": {"unityEditor": {"processId": 123, "verified": True, "bridgeSessionId": "bridge"},
            "mcpServer": {"processId": 789, "loadedSource": {"status": "equivalent", "entries": entries}}},
    }


@pytest.mark.parametrize("section,field,value", [
    ("executionState", "playModeState", "edit"), ("executionState", "isPaused", True),
    ("executionState", "isStale", True), ("executionState", "authoritative", False),
    ("executionState", "blocked", False), ("executionState", "blockedReason", "CompileErrors"),
    ("executionState", "compilePhase", "queued"), ("executionState", "compilePhase", "verifying"),
    ("executionState", "compilePhase", "failed"), ("executionState", "isCompiling", True),
    ("executionState", "isUpdating", True),
    ("session", "processId", 0), ("session", "processId", True),
    ("session", "processCreatedAt", 0), ("session", "identityVerified", False),
    ("session", "sessionId", ""),
])
def test_identity_requires_verified_unpaused_exact_playmode(client, section, field, value):
    sample = status(client.PROJECT)
    sample[section][field] = value
    with pytest.raises(client.CallPreflightError):
        client.identity(sample)


@pytest.mark.parametrize("change", ["unity_unverified", "unity_pid", "bridge", "entry_missing", "entry_stale", "other_project"])
def test_reported_pid_or_partial_loading_cannot_replace_exact_identity(client, change):
    sample = status(client.PROJECT)
    if change == "unity_unverified":
        sample["runtimeIdentity"]["unityEditor"]["verified"] = False
    elif change == "unity_pid":
        sample["runtimeIdentity"]["unityEditor"]["processId"] = 999
    elif change == "bridge":
        sample["runtimeIdentity"]["unityEditor"]["bridgeSessionId"] = "other"
    elif change == "entry_missing":
        sample["runtimeIdentity"]["mcpServer"]["loadedSource"]["entries"].pop()
    elif change == "entry_stale":
        sample["runtimeIdentity"]["mcpServer"]["loadedSource"]["entries"][0]["status"] = "changed"
    else:
        sample["paths"]["unityProjectAbsolute"] = str(client.PROJECT / "other")
    with pytest.raises(client.CallPreflightError):
        client.identity(sample)


def test_unrelated_loaded_entry_difference_does_not_replace_n01_entry_evidence(client):
    sample = status(client.PROJECT)
    loaded = sample["runtimeIdentity"]["mcpServer"]["loadedSource"]
    loaded["status"] = "different"
    loaded["entries"].append({"module": "upilot_mcp.domain.task_service",
        "entry": "TaskDomainService.task_start", "status": "different"})
    original = copy.deepcopy(sample)
    assert client.identity(sample) == (123, 456, 789, "producer", 68, "bridge")
    assert sample == original  # Never erase or rewrite the full loaded-source receipt.


@pytest.mark.parametrize("entry_index", [0, 1])
@pytest.mark.parametrize("entry_status", ["different", "unknown", None])
def test_unrelated_difference_never_excuses_unverified_n01_entry(client, entry_index, entry_status):
    sample = status(client.PROJECT)
    loaded = sample["runtimeIdentity"]["mcpServer"]["loadedSource"]
    loaded["status"] = "different"
    loaded["entries"][entry_index]["status"] = entry_status
    with pytest.raises(client.CallPreflightError):
        client.identity(sample)


def test_real_playmode_only_block_requires_completed_nonupdating_authoritative_state(client):
    sample = status(client.PROJECT)
    assert sample["executionState"]["ready"] is False
    assert client.identity(sample) == (123, 456, 789, "producer", 68, "bridge")


def test_source_preserves_wire_id_and_same_named_entry_dto(client):
    first, second = client.source(1, "x"), client.source(2, "y")
    for code in (first, second):
        assert "public ulong instanceId" in code
        assert "ulong.Parse(argsJson" in code
        assert "UPilotGameObjectService.FindByInstanceId(wireId)" in code
        assert "UPilotEntityIds.ToWireId(go)" in code
        assert "GetInstanceID()" not in code and "InstanceIDToObject" not in code
        assert "OwnedDiagnosticEntry" in code and "class Receipt" in code
    assert "p.x += 1" in first and "p.y += 1" in second
    assert "await Task.Delay(10000, context.Cancellation)" in client.source(3, asynchronous=True)


class FakeModule:
    def __init__(self, client, failure=None):
        self.client, self.failure = client, failure
        self.calls = []
        self.status_reads = 0
        self.object_id = 2**63 + 17
        self.module_id = ""
        self.version = 0
        self.revision = 0
        self.invocations = 0
        self.x, self.y = 0, 0
        self.pending = False
        self.compilation_failed = False
        self.last = {}
        self.closed = False

    @asynccontextmanager
    async def connect(self, port):
        try:
            yield self
        finally:
            self.closed = True

    def snapshot(self):
        return {"projectPath": str(self.client.PROJECT), "domainGeneration": "execution-domain",
            "playSessionId": "play-session", "sessionId": "owned-session", "moduleId": self.module_id,
            "revision": self.revision, "assemblyName": "owned-" + str(self.revision),
            "dllSha256": str(self.revision) * 64, "loadedAssemblyCount": self.revision,
            "invocationId": "inv-" + str(self.invocations), "activeInvocation": self.pending,
            "compilerStillRunning": False, "compilationStatus": "failed" if self.compilation_failed else "completed",
            "status": "running" if self.pending else "completed", "cleanupErrors": [],
            "result": json.dumps({"version": self.version, "instanceId": self.object_id, "x": self.x, "y": self.y})}

    async def call_tool(self, proxy, request):
        from upilot_mcp.mcp_tools import execution_tools, resource_tools, status_tools, task_tools
        name, args = request["toolName"], request["args"]
        model = next((getattr(module, name) for module in (execution_tools, resource_tools, status_tools, task_tools)
                      if hasattr(module, name)), None)
        assert model is not None, name
        inspect.signature(model).bind(**args)
        assert proxy == "unity_tool_call"
        self.calls.append((name, copy.deepcopy(args)))
        envelope = {"ok": True, "data": {}}
        value = envelope["data"]
        if name == "unity_mcp_status":
            self.status_reads += 1
            value.update(status(self.client.PROJECT))
            if self.failure == "identity" and self.status_reads > 1:
                value["executionState"]["domainGeneration"] = 69
        elif name == "unity_queue_cleanup":
            value.update(complete=self.failure != "inventory", items=[])
        elif name == "unity_console_capture_list":
            value.update(activeCount=1 if self.failure == "capture" else 0)
        elif name == "unity_ensure_ready":
            value.update(ready=True, blocked=False, contextAuthoritative=True, contextStale=False)
        elif name == "execution_session":
            value.update(sessionId="owned-session", domainGeneration="execution-domain", closed=args["action"] == "close",
                         cleanupErrors=[], activeAsyncOperations=0, asyncOperationsStillRunning=0)
            if args["action"] == "close" and self.failure == "cleanup":
                value.update(cleanupErrors=["owned cleanup failure"], asyncOperationsStillRunning=1)
        elif name == "unity_gameobject_create":
            if self.failure == "create_lost":
                raise TimeoutError("receipt lost; object may exist")
            value.update(instanceId=str(self.object_id), name=args["name"])
        elif name == "unity_gameobject_find":
            value.update(exactInstanceIdLookup=True, truncated=False,
                gameObjects=[{"instanceId": str(self.object_id), "name": "other" if self.failure == "name" else self.module_id}])
        elif name == "unity_gameobject_delete":
            value.update(ok=True, target={"instanceId": str(self.object_id), "name": self.module_id},
                         beforeExists=True, afterExists=self.failure == "delete", businessEffectVerified=self.failure != "delete")
        elif name == "diagnostic_module":
            action = args.get("action", "status")
            if "moduleId" not in args:
                value.update(projectPath=str(self.client.PROJECT), domainGeneration="execution-domain", playSessionId="play-session")
            else:
                self.module_id = args["moduleId"]
                if action == "compile":
                    if self.pending:
                        envelope = {"ok": False, "error": {"code": "OTHER_ERROR" if self.failure == "busy" else "MODULE_BUSY"}}
                    else:
                        code = args["source"]
                        version = 3 if "async Task<string>" in code else (2 if "p.y += 1" in code else 1)
                        if version != self.version:
                            self.version, self.revision = version, self.revision + 1
                        self.compilation_failed = self.failure == "async_compile" and version == 3
                        value.update(self.snapshot())
                elif action == "run":
                    if self.failure == "run_lost":
                        raise TimeoutError("invocation receipt lost")
                    self.invocations += 1
                    if self.version == 1:
                        self.x += 1
                    elif self.version == 2:
                        self.y += 1
                    else:
                        self.pending = True
                    value.update(self.snapshot())
                elif action == "status":
                    self.pending = False
                    value.update(self.snapshot())
                elif action == "close":
                    value.update(self.snapshot(), status="closed")
        return SimpleNamespace(isError=False, structuredContent=envelope)


def exercise(client, monkeypatch, failure=None):
    fake = FakeModule(client, failure)
    monkeypatch.setattr(client, "connect", fake.connect)
    # Advance the fake counter in Run instead of sleeping; this is not a real update/unsubscription proof.
    async def no_sleep(seconds):
        return None
    monkeypatch.setattr(client.asyncio, "sleep", no_sleep)
    report = {}
    asyncio.run(client.exercise(8011, report))
    assert fake.closed
    return fake, report


def test_owned_matrix_uses_real_argument_signatures_and_high_wire_id(client, monkeypatch):
    fake, report = exercise(client, monkeypatch)
    assert report["result"] == "observed" and report["cleanupErrors"] == []
    assert report["ownedObjectId"] == 2**63 + 17
    compiles = [args for name, args in fake.calls if name == "diagnostic_module" and args.get("action") == "compile"]
    assert len(compiles) == 6  # two revisions and reuse, async revision, one precise busy rejection
    assert len([1 for name, args in fake.calls if name == "diagnostic_module" and args.get("action") == "run"]) == 5
    assert len([1 for name, args in fake.calls if name == "unity_gameobject_delete"]) == 1
    assert not any(name in {"unity_playmode_start", "unity_playmode_stop", "unity_service_restart", "unity_compile"}
                   for name, args in fake.calls)


@pytest.mark.parametrize("failure", ["inventory", "capture"])
def test_preflight_refusal_creates_no_session_object_or_module(client, monkeypatch, failure):
    fake, report = exercise(client, monkeypatch, failure)
    assert report["result"] == "unknown" and report["businessError"]
    assert not any(name in {"execution_session", "unity_gameobject_create", "diagnostic_module"} for name, _ in fake.calls)


@pytest.mark.parametrize("failure,tool,action", [
    ("create_lost", "unity_gameobject_create", None), ("run_lost", "diagnostic_module", "run"),
])
def test_uncertain_mutation_receipt_is_not_replayed(client, monkeypatch, failure, tool, action):
    fake, report = exercise(client, monkeypatch, failure)
    assert report["result"] == "unknown" and report["businessError"] == "TimeoutError"
    assert len([1 for name, args in fake.calls if name == tool and (action is None or args.get("action") == action)]) == 1


def test_async_compile_failure_does_not_invoke_retained_revision(client, monkeypatch):
    fake, report = exercise(client, monkeypatch, "async_compile")
    assert report["result"] == "unknown" and "candidate" in report["businessError"]
    assert len([1 for name, args in fake.calls if name == "diagnostic_module" and args.get("action") == "run"]) == 4


def test_busy_first_error_is_preserved_and_not_retried(client, monkeypatch):
    fake, report = exercise(client, monkeypatch, "busy")
    assert report["result"] == "unknown" and "in-flight" in report["businessError"]
    assert len([1 for name, args in fake.calls if name == "diagnostic_module" and args.get("action") == "compile"]) == 6
    assert "cleanupErrors" in report


def test_changed_domain_never_routes_old_cleanup(client, monkeypatch):
    fake, report = exercise(client, monkeypatch, "identity")
    assert report["result"] == "unknown" and "changed" in report["businessError"]
    assert report["cleanupErrors"] and "not dispatched" in report["cleanupErrors"][0]
    assert not any(name == "unity_gameobject_delete" or (name == "execution_session" and args["action"] == "close")
                   for name, args in fake.calls)


@pytest.mark.parametrize("failure", ["cleanup", "delete", "name"])
def test_successful_business_work_cannot_hide_unverified_cleanup(client, monkeypatch, failure):
    fake, report = exercise(client, monkeypatch, failure)
    assert report["result"] == "unknown" and report["cleanupErrors"]
    deletes = [1 for name, _ in fake.calls if name == "unity_gameobject_delete"]
    assert len(deletes) == (0 if failure == "name" else 1)


def test_original_observation_budget_never_dispatches_when_expired(client):
    fake = FakeModule(client)
    with pytest.raises(client.CallPreflightError, match="budget"):
        asyncio.run(client.observe(fake, {"moduleId": "owned"}, {"calls": []}, deadline=0))
    assert fake.calls == []


@pytest.mark.parametrize("field", ["projectPath", "moduleId", "sessionId", "domainGeneration", "playSessionId"])
def test_each_module_receipt_retains_exact_original_identity(client, field):
    arguments = {"moduleId": "owned", "sessionId": "session", "expectedProjectPath": str(client.PROJECT),
        "expectedDomainGeneration": "execution-domain", "expectedPlaySessionId": "play-session"}
    value = {"projectPath": str(client.PROJECT), "moduleId": "owned", "sessionId": "session",
        "domainGeneration": "execution-domain", "playSessionId": "play-session"}
    value[field] = "other"
    with pytest.raises(client.CallPreflightError, match="identity changed"):
        client.check_module(value, arguments)
