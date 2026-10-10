"""Tamper evidence-client contracts; fake transport is not live Unity evidence."""
import asyncio
import copy
from contextlib import asynccontextmanager
import hashlib
import importlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

NONCE = "0123456789abcdef0123456789abcdef"


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    value = importlib.import_module("p1_diagnostic_module_tamper_evidence")
    monkeypatch.setattr(value, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p1_diagnostic_module_evidence"), "PROJECT", tmp_path)
    return value


def sample_report(client):
    folder = client.PROJECT / "Log" / "P0P1" / ("n01-tamper-" + NONCE)
    folder.mkdir(parents=True)
    value = dict(nonce=NONCE, project=str(client.PROJECT), unityPid=123, succeeded=True, ownedOnly=True,
                 compilerStillRunning=False, firstError="", cleanupErrors=[], startedAt=10, endedAt=20,
                 referenceMvid="1" * 32, changedMvid="2" * 32, loadedMvidAtIdentityCheck="1" * 32, scenarios=[], artifacts=[],
                 sessionClose=dict(sessionId="owned", closed=True, cleanupErrors=[], activeAsyncOperations=0,
                                   asyncOperationsStillRunning=0))
    files = {
        "reference-original": (folder / "reference-original.dll", b"owned PE fixture"),
        "reference-restored": (folder / "OwnedReference.dll", b"owned PE fixture"),
        "reference-during-compile-changed": (folder / "reference-content-changed.dll", b"owned PE fixtur" + bytes([ord("e") ^ 1])),
        "reference-mvid-changed": (folder / "reference-changed-mvid.dll", b"changed MVID fixture"),
    }
    for index, (name, code) in enumerate(client.EXPECTED.items()):
        before = dict(projectPath=str(client.PROJECT), domainGeneration="domain", playSessionId="play",
                      sessionId="owned", moduleId="", loadedAssemblyCount=0, loadedAssemblyBytes=0)
        after = dict(before, compilerStillRunning=False, activeInvocation=False,
                     sideEffectsMayHaveOccurred=False, invocationId="", cleanupErrors=[])
        scenario = dict(name=name, rejectionCode=code, rejectedBeforeLoad=True, invocationNotStarted=True,
                        originalReferenceRestored=True, before=before, after=after)
        if index < 2:
            before.update(moduleId=NONCE + ("-source" if index == 0 else "-reference"),
                          compilationStatus="compiling", compilerStillRunning=True)
            after.update(moduleId=before["moduleId"], compilationStatus="failed", compilationError=code, revision=0)
            candidate_name = "UPilotDiagnostic_" + str(index + 1) * 32
            candidate = client.PROJECT / ".upilot" / "diagnostic-modules" / candidate_name / (candidate_name + ".cs")
            scenario["candidateSource"] = str(candidate)
            original = b"// original source\n"
            files[name + "-source-original"] = (folder / (name + "-original.cs"), original)
            files[name + "-source-final"] = (candidate, original + ("\n// owned tamper " + NONCE + "\n").encode()
                                             if index == 0 else original)
        value["scenarios"].append(scenario)
    for role, (path, raw) in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
        value["artifacts"].append(dict(role=role, path=str(path), bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
    persist_report(client, value)
    return value


def persist_report(client, value):
    (client.PROJECT / "Log" / "P0P1" / ("n01-tamper-" + NONCE) / "report.json").write_text(json.dumps(value), encoding="utf-8")


def terminal(client, value):
    raw = json.dumps(value)
    return dict(ok=True, data=dict(operationId="reflection-op-owned", terminal=True, status="Succeeded",
                                  result=dict(typeName=client.PROBE, methodName="Run", wasAwaitable=True,
                                              awaitableStatus="Completed", result=raw,
                                              resultValue=dict(valueJson=json.dumps(raw)))))


def test_verifies_exact_three_scenarios_and_eight_actual_files(client):
    value = sample_report(client)
    verified = client.verify_report(client.returned_report(terminal(client, value)), NONCE, 123)
    assert len(verified["artifacts"]) == 8
    assert verified["scenariosVerified"] == list(client.EXPECTED)
    assert verified["report"]["sha256"] == hashlib.sha256(Path(verified["report"]["path"]).read_bytes()).hexdigest()


@pytest.mark.parametrize("defect", ["first_error", "cleanup", "async_running", "bool_count", "session_missing",
    "reference_restore", "reference_bytes", "source_bytes", "resident_count", "resident_bytes", "invoked",
    "identity", "rejection", "scenario_missing", "artifact_missing", "artifact_duplicate", "hash", "bytes",
    "path", "candidate_path", "disk_report", "timing", "mvid"])
def test_rejects_incomplete_or_inconsistent_evidence(client, defect):
    value = sample_report(client)
    artifact = value["artifacts"][0]
    scenario = value["scenarios"][0]
    if defect == "first_error": value["firstError"] = "original business failure"
    elif defect == "cleanup": value["cleanupErrors"] = ["owned cleanup failure"]
    elif defect == "async_running": value["sessionClose"]["asyncOperationsStillRunning"] = 1
    elif defect == "bool_count": value["sessionClose"]["activeAsyncOperations"] = False
    elif defect == "session_missing": value["sessionClose"].pop("closed")
    elif defect == "reference_restore": scenario["originalReferenceRestored"] = False
    elif defect in {"reference_bytes", "source_bytes"}:
        role = "reference-restored" if defect == "reference_bytes" else "source-during-compile-source-final"
        item = next(a for a in value["artifacts"] if a["role"] == role)
        raw = b"different bytes with correct receipt"
        Path(item["path"]).write_bytes(raw)
        item.update(bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
    elif defect == "resident_count": scenario["after"]["loadedAssemblyCount"] = 1
    elif defect == "resident_bytes": scenario["after"]["loadedAssemblyBytes"] = 12
    elif defect == "invoked": scenario["after"]["invocationId"] = "unexpected"
    elif defect == "identity": scenario["after"]["playSessionId"] = "other"
    elif defect == "rejection": scenario["rejectionCode"] = "OTHER_ERROR"
    elif defect == "scenario_missing": value["scenarios"].pop()
    elif defect == "artifact_missing": value["artifacts"].pop()
    elif defect == "artifact_duplicate": value["artifacts"][-1] = copy.deepcopy(artifact)
    elif defect == "hash": artifact["sha256"] = "0" * 64
    elif defect == "bytes": artifact["bytes"] += 1
    elif defect == "path": artifact["path"] = str(client.PROJECT / "unowned.dll")
    elif defect == "candidate_path": scenario["candidateSource"] = str(client.PROJECT / "unowned.cs")
    elif defect == "timing": value["endedAt"] = 0
    elif defect == "mvid": value["changedMvid"] = value["referenceMvid"]
    if defect != "disk_report": persist_report(client, value)
    else: value["endedAt"] += 1
    with pytest.raises(client.CallPreflightError): client.verify_report(value, NONCE, 123)


@pytest.mark.parametrize("defect", ["failed", "nonterminal", "wrong_method", "not_awaited", "typed_mismatch"])
def test_original_reflection_receipt_must_be_successful_and_consistent(client, defect):
    receipt = terminal(client, sample_report(client))
    if defect == "failed": receipt.update(ok=False, error=dict(detail=dict(terminal=True, status="Failed")))
    elif defect == "nonterminal": receipt["data"]["terminal"] = False
    elif defect == "wrong_method": receipt["data"]["result"]["methodName"] = "Other"
    elif defect == "not_awaited": receipt["data"]["result"]["wasAwaitable"] = False
    else: receipt["data"]["result"]["resultValue"]["valueJson"] = json.dumps("other")
    with pytest.raises(client.CallPreflightError): client.returned_report(receipt)


class FakeTransport:
    def __init__(self, client, defect=None):
        self.client, self.defect, self.calls = client, defect, []
        self.status_reads, self.polls, self.closed = 0, 0, False

    @asynccontextmanager
    async def connect(self, port):
        try: yield self
        finally:
            self.closed = True
            if self.defect == "transport_close": raise TimeoutError("transport close failure")

    async def call_tool(self, proxy, request):
        from upilot_mcp.mcp_tools import execution_tools, reflection_tools, status_tools, task_tools, test_tools
        name, args = request["toolName"], request["args"]
        model = next((getattr(m, name) for m in (execution_tools, reflection_tools, status_tools, task_tools, test_tools)
                      if hasattr(m, name)), None)
        assert model is not None, name
        inspect.signature(model).bind(**args)
        assert proxy == "unity_tool_call"
        self.calls.append((name, copy.deepcopy(args)))
        envelope = dict(ok=True, data={})
        value = envelope["data"]
        if name == "unity_mcp_status":
            from test_diagnostic_module_evidence_client import status
            self.status_reads += 1
            value.update(status(self.client.PROJECT))
            if self.defect == "stale": value["executionState"]["isStale"] = True
            if self.defect in {"identity_before", "identity_after"} and self.status_reads >= (2 if self.defect == "identity_before" else 3):
                value["executionState"]["domainGeneration"] += 1
        elif name == "unity_queue_cleanup":
            value.update(complete=self.defect != "inventory_unknown", items=[{}] if self.defect == "busy" else [])
        elif name == "unity_console_capture_list": value.update(activeCount=1 if self.defect == "capture" else None if self.defect == "capture_unknown" else False if self.defect == "capture_bool" else 0)
        elif name == "unity_test_status": value.update(isRunning=None if self.defect == "runner_unknown" else self.defect == "runner", cleanupPending=self.defect == "cleanup")
        elif name == "unity_ensure_ready": value.update(ready=self.defect != "readiness", blocked=self.defect == "readiness", contextAuthoritative=True, contextStale=False)
        elif name == "unity_reflection_call":
            assert args["awaitTimeoutMs"] == 30000
            assert args["parameters"] == [NONCE] and args["methodName"] == "Run" and args["forceAsync"] is True
            if self.defect == "dispatch_lost": raise TimeoutError("Run receipt lost; do not replay")
            self.probe = sample_report(self.client)
            if self.defect == "probe_business_failure": self.probe.update(succeeded=False, firstError="original MVID failure", compilerStillRunning=True)
            value.update(operationId="reflection-op-owned", executedOnce=self.defect != "handle_unknown")
        elif name == "unity_reflection_operation_status":
            assert args["operationId"] == "reflection-op-owned"
            self.polls += 1
            if self.defect == "poll_lost": raise TimeoutError("original status lost")
            if self.polls == 1: value.update(operationId="reflection-op-owned", terminal=False, status="Running")
            else:
                envelope = terminal(self.client, self.probe)
                if self.defect == "failed": envelope = dict(ok=False, error=dict(code="REFLECTION_FAILED", detail=dict(operationId="reflection-op-owned", terminal=True, status="Failed")))
                elif self.defect == "wrong_handle": envelope["data"]["operationId"] = "reflection-op-other"
        else: raise AssertionError(name)
        return SimpleNamespace(isError=False, structuredContent=envelope)


def exercise(client, monkeypatch, defect=None):
    fake = FakeTransport(client, defect)
    monkeypatch.setattr(client, "connect", fake.connect)
    async def no_sleep(seconds): pass
    monkeypatch.setattr(client.asyncio, "sleep", no_sleep)
    result = asyncio.run(client.run(8011, NONCE))
    assert fake.closed
    return fake, result


@pytest.mark.parametrize("defect", ["stale", "inventory_unknown", "busy", "capture", "capture_unknown", "capture_bool", "runner", "runner_unknown", "cleanup", "readiness", "identity_before"])
def test_preflight_rejection_never_dispatches_run(client, monkeypatch, defect):
    fake, result = exercise(client, monkeypatch, defect)
    assert result["succeeded"] is False and result["firstError"]
    assert not any(name == "unity_reflection_call" for name, _ in fake.calls)


@pytest.mark.parametrize("defect", [None, "dispatch_lost", "handle_unknown", "poll_lost", "failed", "wrong_handle", "identity_after", "transport_close"])
def test_one_run_original_handle_only_and_no_replay(client, monkeypatch, defect):
    fake, result = exercise(client, monkeypatch, defect)
    assert result["succeeded"] is (defect is None)
    assert result["replayAttempted"] is False
    assert sum(name == "unity_reflection_call" for name, _ in fake.calls) == 1
    assert all(args == {"operationId": "reflection-op-owned"} for name, args in fake.calls if name == "unity_reflection_operation_status")
    assert not any(name in {"unity_playmode_start", "unity_playmode_stop", "unity_service_restart", "unity_compile"} for name, _ in fake.calls)
    if defect in {"handle_unknown", "poll_lost", "failed", "wrong_handle", "identity_after"}:
        assert result["operationId"] == "reflection-op-owned"
    if defect == "failed": assert result["reflectionTerminal"]["error"]["detail"]["status"] == "Failed"


@pytest.mark.parametrize("nonce", [None, "../escape", "A" * 32, "a" * 31, NONCE])
def test_invalid_or_used_nonce_never_connects(client, monkeypatch, nonce):
    if nonce == NONCE: sample_report(client)
    def must_not_connect(port): raise AssertionError("nonce guard must run before transport")
    monkeypatch.setattr(client, "connect", must_not_connect)
    with pytest.raises(client.CallPreflightError): asyncio.run(client.run(8011, nonce))


@pytest.mark.parametrize("nested", [False, True])
def test_taskgroup_envelope_preserves_first_business_error(client, monkeypatch, nested):
    @asynccontextmanager
    async def fail_connect(port):
        leaf = client.CallPreflightError("owned original preflight failure")
        error = ExceptionGroup("inner", [leaf]) if nested else leaf
        raise ExceptionGroup("unhandled errors in a TaskGroup", [error])
        yield
    monkeypatch.setattr(client, "connect", fail_connect)
    report = asyncio.run(client.run(8011, NONCE))
    assert report["succeeded"] is False
    assert report["firstError"] == "CallPreflightError: owned original preflight failure"
    assert report["calls"] == [] and report["replayAttempted"] is False


def test_probe_business_failure_preserved_before_final_preflight(client, monkeypatch):
    fake, result = exercise(client, monkeypatch, "probe_business_failure")
    assert result["succeeded"] is False
    assert result["probeReport"]["firstError"] == "original MVID failure"
    assert result["firstError"] == "CallPreflightError: Owned probe failed: original MVID failure"
    assert fake.status_reads == 2
    assert sum(name == "unity_reflection_call" for name, _ in fake.calls) == 1
    assert result["replayAttempted"] is False


def test_mvid_verifier_requires_unchanged_loaded_metadata(client):
    report = sample_report(client)
    report["loadedMvidAtIdentityCheck"] = report["changedMvid"]
    with pytest.raises(client.CallPreflightError, match="Disk-only MVID"):
        client.verify_report(report, NONCE, 123)
