"""Opt-in Reload client contracts. No network, live Arm or Unity Reload."""
import asyncio
import copy
import importlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    module = importlib.import_module("p0_reload_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    return module


def status(project):
    return {"paths": {"unityProjectAbsolute": str(project)}, "connected": True, "serverReady": True,
            "executionState": {"ready": True, "authoritative": True, "isStale": False,
                               "playModeState": "edit", "producerEpoch": "producer", "domainGeneration": "before"},
            "session": {"processId": 123, "processCreatedAt": 456, "identityVerified": True, "sessionId": "bridge"},
            "runtimeIdentity": {"mcpServer": {"processId": 789, "loadedSource": {
                "status": "equivalent", "scope": "selected-entry-code-only"}}}}


def write_witness(directory, nonce):
    directory.mkdir(parents=True)
    armed = {"nonce": nonce, "directory": str(directory), "originalCommandId": "original", "commandName": "reflection.call",
             "phaseAtArm": "executing", "domainBefore": "before", "serverLifetimeBefore": "same",
             "processIdBefore": 123, "processCreatedBefore": 456, "armedAt": 1000, "deadlineAt": 31000}
    before = {**armed, "beforeReloadAt": 1500, "phaseBeforeReload": "executing", "activeBeforeReload": True}
    after = {**before, "observedAfterAt": 2000, "domainAfter": "after", "serverLifetimeAfter": "same",
             "processIdAfter": 123, "processCreatedAfter": 456, "realDomainReload": True, "ownedEffectUnchanged": True,
             "resourcesReleased": True, "endedAt": 2001}
    for name, value in [("armed.json", armed), ("before-reload.json", before),
                        ("after-reload.json", after), ("effect.json", {"effectCount": 1})]:
        (directory / name).write_text(json.dumps(value), encoding="utf-8")


def command_observation():
    return {"source": "server_command_record",
            "scope": "current-service-lifetime; call-budget-ready-send-response; send-attempts-not-execution-count",
            "commandId": "original", "serviceLifecycleId": "same", "status": "observed",
            "commandName": "reflection.call", "recordStatus": "failed", "sendAttempts": 1,
            "responseWaitStartedAt": 1100, "responseWaitDeadlineAt": 46000, "responseWaitTimeoutMs": 44900,
            "callStartedAt": 1000, "callDeadlineAt": 46000, "callTimeoutMs": 45000}


@pytest.mark.parametrize("field,value", [("connected", False), ("serverReady", False)])
def test_status_rejects_disconnected_or_unready(client, tmp_path, field, value):
    data = status(tmp_path)
    data[field] = value
    with pytest.raises(client.CallPreflightError):
        client.require_status(data)


@pytest.mark.parametrize("section,field,value", [
    ("executionState", "authoritative", False), ("executionState", "isStale", True),
    ("executionState", "playModeState", "play"), ("executionState", "domainGeneration", None),
    ("session", "identityVerified", False), ("session", "processCreatedAt", 0),
    ("session", "sessionId", ""),
])
def test_status_requires_exact_known_lifetime(client, tmp_path, section, field, value):
    data = status(tmp_path)
    data[section][field] = value
    with pytest.raises(client.CallPreflightError):
        client.require_status(data)


def test_paths_reject_escape_and_existing_links_before_creating_files(client, tmp_path):
    with pytest.raises(client.CallPreflightError):
        client.owned_path(tmp_path / ".." / "elsewhere")
    link = tmp_path / "link"
    try:
        link.symlink_to(tmp_path, target_is_directory=True)
    except OSError:
        pytest.skip("Creating symlinks unavailable; escape assertion above still checked")
    with pytest.raises(client.CallPreflightError):
        client.owned_path(link / "new" / "file.json")
    assert not (tmp_path / "new").exists()


@pytest.mark.parametrize("name,key,value", [
    ("after-reload.json", "deadlineAt", 32000), ("before-reload.json", "originalCommandId", "changed"),
    ("after-reload.json", "domainAfter", "before"), ("after-reload.json", "processIdAfter", 124),
    ("after-reload.json", "observedAfterAt", 32000), ("effect.json", "effectCount", 2),
])
def test_witness_rejects_rewrite_identity_deadline_or_second_effect(client, tmp_path, name, key, value):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    assert len(client.read_witness(directory, "nonce")) == 4
    path = directory / name
    data = json.loads(path.read_text())
    data[key] = value
    path.write_text(json.dumps(data))
    with pytest.raises(client.CallPreflightError):
        client.read_witness(directory, "nonce")


def test_missing_witness_is_not_a_reload_success(client, tmp_path):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    (directory / "after-reload.json").unlink()  # own isolated fixture only
    with pytest.raises(client.CallPreflightError):
        client.read_witness(directory, "nonce")


def test_terminal_uses_original_id_and_rejects_unexpected_success(client):
    with pytest.raises(client.CallPreflightError):
        client.terminal_projection({"ok": True, "data": {"operationId": "other"}}, "original")
    with pytest.raises(client.CallPreflightError):
        client.terminal_projection({"ok": True, "data": {"operationId": "original", "terminal": True, "status": "Succeeded"}}, "original")
    assert client.terminal_projection({"ok": True, "data": {"operationId": "original", "terminal": False}}, "original") is None


def test_readiness_is_observe_only_and_arguments_bind_actual_model(client):
    from upilot_mcp.mcp_tools.task_tools import unity_ensure_ready
    calls = []
    class Session:
        async def call_tool(self, tool, args):
            calls.append((tool, args))
            inspect.signature(unity_ensure_ready).bind(**args["args"])
            return SimpleNamespace(isError=False, structuredContent={"ok": True, "data": {
                "ready": True, "blocked": False, "contextAuthoritative": True, "contextStale": False}})
    asyncio.run(client.ensure_ready(Session(), {"calls": []}))
    assert calls[0][1]["args"]["editorModeControl"] == "manual"


def test_lost_arm_receipt_is_sent_once_and_never_replayed(client, monkeypatch, tmp_path):
    from contextlib import asynccontextmanager
    calls = []
    @asynccontextmanager
    async def connect(port):
        yield object()
    async def invoke(session, tool, args, report):
        calls.append(tool)
        data = {"unity_mcp_status": status(tmp_path), "unity_queue_cleanup": {"complete": True, "items": []},
                "unity_console_capture_list": {"activeCount": 0}, "unity_type_exists": {"exists": True},
                "unity_ensure_ready": {"ready": True, "blocked": False, "contextAuthoritative": True, "contextStale": False}}
        if tool == "unity_reflection_call":
            from upilot_mcp.mcp_tools.reflection_tools import unity_reflection_call
            inspect.signature(unity_reflection_call).bind(**args)
            raise ConnectionError("response lost")
        return {"ok": True, "data": data[tool]}
    monkeypatch.setattr(client, "connect", connect)
    monkeypatch.setattr(client, "invoke", invoke)
    report = {"calls": [], "armSent": False}
    with pytest.raises(ConnectionError):
        asyncio.run(client.exercise(8011, "nonce", report))
    assert calls.count("unity_reflection_call") == 1
    assert calls.index("unity_ensure_ready") < calls.index("unity_reflection_call")
    assert report["armSent"] is True
    assert not (tmp_path / "Log").exists()


@pytest.mark.parametrize("status_value", ["Failed", "Canceled", "Succeeded"])
def test_terminal_is_recorded_before_witness_or_outcome_validation(client, status_value):
    report = {}
    terminal = {"operationId": "original", "terminal": True, "status": status_value,
                "resultError": {"code": "AWAITABLE_TIMEOUT"}}
    result = {"ok": False, "error": {"detail": terminal}}
    if status_value == "Succeeded":
        with pytest.raises(client.CallPreflightError):
            client.terminal_projection(result, "original", report)
    else:
        assert client.terminal_projection(result, "original", report) == terminal
    assert report["originalTerminal"] == terminal
    assert report["terminal"] == terminal


def test_mismatched_terminal_is_not_attributed_to_original(client):
    report = {}
    with pytest.raises(client.CallPreflightError):
        client.terminal_projection({"ok": True, "data": {
            "operationId": "other", "terminal": True, "status": "Failed"}}, "original", report)
    assert not report


def test_partial_witness_preserves_request_and_cleanup_diagnostics(client, tmp_path):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    (directory / "before-reload.json").unlink()
    (directory / "after-reload.json").unlink()
    request = {"reloadRequestStartedAt": 1001, "reloadRequestReturnedAt": 1002}
    ended = {"endedAt": 31000, "resourcesReleased": True, "failure": "Reload not observed."}
    for name, value in [("reload-requested.json", request), ("probe-ended.json", ended)]:
        (directory / name).write_text(json.dumps(value))
    files = client.collect_witness(directory)
    assert set(files) == {"armed.json", "effect.json", "reload-requested.json", "probe-ended.json"}
    assert files["reload-requested.json"]["data"] == request
    assert files["probe-ended.json"]["data"] == ended
    with pytest.raises(client.CallPreflightError):
        client.read_witness(directory, "nonce")


@pytest.mark.parametrize("has_reload", [False, True])
def test_exercise_explicit_inner_budget_and_no_repoll_of_frozen_failure(client, monkeypatch, tmp_path, has_reload):
    from contextlib import asynccontextmanager
    from upilot_mcp.mcp_tools.reflection_tools import unity_reflection_call
    calls = []
    nonce = "0" * 32
    directory = tmp_path / "Log" / "Stage4" / "CommandRealReload" / nonce
    terminal = {"operationId": "original", "terminal": True, "status": "Failed",
                "startedAt": 1000, "resultError": {"code": "INTERRUPTED" if has_reload else "AWAITABLE_TIMEOUT"}}
    states = iter([status(tmp_path), status(tmp_path)])
    @asynccontextmanager
    async def connect(port):
        yield object()
    async def invoke(session, tool, args, report):
        calls.append((tool, args))
        if tool == "unity_mcp_status":
            data = next(states)
            if has_reload and directory.exists():
                data["executionState"]["domainGeneration"] = "after"
                # A reconnect snapshot is stale until the read-only readiness observation.
                ready_count = sum(name == "unity_ensure_ready" for name, _ in calls)
                data["executionState"]["isStale"] = ready_count < 2
            return {"ok": True, "data": data}
        if tool == "unity_reflection_call":
            inspect.signature(unity_reflection_call).bind(**args)
            assert args["awaitTimeoutMs"] == 30000
            assert args["operationTimeoutSec"] == 45
            assert args["forceAsync"] is True
            write_witness(directory, nonce)
            if not has_reload:
                (directory / "before-reload.json").unlink()
                (directory / "after-reload.json").unlink()
            return {"ok": True, "data": {"operationId": "original"}}
        if tool == "unity_reflection_operation_status":
            assert args == {"operationId": "original"}
            return {"ok": False, "error": {"detail": terminal}}
        if tool == "unity_operation_get":
            assert args == {"commandId": "original"}
            # Unity may no longer have the tracker entry; keep Server facts on its error.
            return {"ok": False, "error": {"code": "NOT_FOUND", "detail": {
                "serverCommandObservation": command_observation()}}}
        data = {"unity_queue_cleanup": {"complete": True, "items": []},
                "unity_console_capture_list": {"activeCount": 0}, "unity_type_exists": {"exists": True},
                "unity_ensure_ready": {"ready": True, "blocked": False, "contextAuthoritative": True, "contextStale": False}}
        return {"ok": True, "data": data[tool]}
    async def no_unnecessary_sleep(seconds):
        pytest.fail("Frozen terminal without a before-Reload boundary must not be polled or waited again.")
    monkeypatch.setattr(client, "connect", connect)
    monkeypatch.setattr(client, "invoke", invoke)
    monkeypatch.setattr(client.asyncio, "sleep", no_unnecessary_sleep)
    report = {"calls": [], "observationGaps": 0, "armSent": False}
    if has_reload:
        asyncio.run(client.exercise(8011, nonce, report))
        assert report["result"] == "observed"
        assert report["reloadEvidence"]["status"] == "verified"
        assert sum(tool == "unity_ensure_ready" for tool, _ in calls) == 2
        assert len([tool for tool, _ in calls if tool == "unity_reflection_operation_status"]) == 2  # final freeze check only
    else:
        with pytest.raises(client.CallPreflightError, match="witnesses are missing"):
            asyncio.run(client.exercise(8011, nonce, report))
        assert report["reloadEvidence"] == {"status": "unverified", "missingFiles": ["before-reload.json", "after-reload.json"]}
        assert len([tool for tool, _ in calls if tool == "unity_reflection_operation_status"]) == 1
        assert set(report["witnessFiles"]) == {"armed.json", "effect.json"}
    assert report["originalTerminal"] == terminal
    assert len([tool for tool, _ in calls if tool == "unity_reflection_call"]) == 1
    assert len([tool for tool, _ in calls if tool == "unity_operation_get"]) == 1
    assert report["serverCommandEvidence"]["status"] == "observed"
    assert report["serverCommandEvidence"]["queryOk"] is False
    assert report["serverCommandEvidence"]["observation"] == command_observation()


@pytest.mark.parametrize("query_ok", [True, False])
def test_command_facts_collected_on_success_or_error_before_reload_files_exist(client, monkeypatch, tmp_path, query_ok):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    (directory / "before-reload.json").unlink()
    (directory / "after-reload.json").unlink()
    report = {"calls": [], "witnessFiles": client.collect_witness(directory),
              "originalTerminal": {"status": "Failed", "resultError": {"code": "AWAITABLE_TIMEOUT"}}}
    frozen = copy.deepcopy(report["originalTerminal"])
    calls = []
    facts = command_observation()
    facts["ownerToken"] = "must-not-copy"
    async def invoke(session, tool, args, report):
        calls.append((tool, args))
        payload = {"serverCommandObservation": facts}
        return {"ok": True, "data": payload} if query_ok else {"ok": False, "error": {"detail": payload}}
    monkeypatch.setattr(client, "invoke", invoke)
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    assert calls == [("unity_operation_get", {"commandId": "original"})]
    evidence = report["serverCommandEvidence"]
    assert evidence["status"] == "observed" and evidence["queryOk"] is query_ok
    assert evidence["missingFacts"] == []
    assert "ownerToken" not in evidence["observation"]
    assert report["originalTerminal"] == frozen
    with pytest.raises(client.CallPreflightError, match="witnesses are missing"):
        client.read_witness(directory, "nonce")
    assert report["serverCommandEvidence"] == evidence


@pytest.mark.parametrize("field,value", [("nonce", "other"), ("directory", "elsewhere"),
    ("commandName", "other.call"), ("originalCommandId", ""), ("originalCommandId", 123),
    ("serverLifetimeBefore", "")])
def test_command_facts_do_not_query_an_unowned_arm(client, monkeypatch, tmp_path, field, value):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    files = client.collect_witness(directory)
    files["armed.json"]["data"][field] = value
    async def no_query(*args):
        pytest.fail("Unowned witness must not select a command for observation")
    monkeypatch.setattr(client, "invoke", no_query)
    report = {"witnessFiles": files}
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    assert report["serverCommandEvidence"] == {"status": "unknown", "reason": "owned_arm_identity_unverified"}


@pytest.mark.parametrize("fault,reason", [
    ("missing", "server_command_observation_missing"),
    ("commandId", "server_command_identity_mismatch"),
    ("serviceLifecycleId", "server_command_identity_mismatch"),
    ("commandName", "server_command_identity_mismatch"),
    ("scope", "server_command_observation_scope_unverified"),
    ("source", "server_command_observation_scope_unverified"),
    ("unknown", "command_record_not_observed"),
])
def test_command_facts_missing_or_mismatched_remain_unknown(client, monkeypatch, tmp_path, fault, reason):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    facts = command_observation()
    if fault == "unknown":
        facts["status"] = "unknown"
        facts["reason"] = "command_record_not_in_current_service_lifetime"
    elif fault != "missing":
        facts[fault] = "wrong"
    calls = []
    async def invoke(session, tool, args, report):
        calls.append(tool)
        return {"ok": True, "data": {} if fault == "missing" else {"serverCommandObservation": facts}}
    monkeypatch.setattr(client, "invoke", invoke)
    report = {"witnessFiles": client.collect_witness(directory)}
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    assert calls == ["unity_operation_get"]
    assert report["serverCommandEvidence"]["status"] == "unknown"
    assert report["serverCommandEvidence"]["reason"] == reason


@pytest.mark.parametrize("fault", ["unrecorded", "query_error"])
def test_command_facts_do_not_invent_old_metadata_or_mask_original_error(client, monkeypatch, tmp_path, fault):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    facts = command_observation()
    for field in ("sendAttempts", "responseWaitStartedAt", "responseWaitDeadlineAt", "responseWaitTimeoutMs"):
        facts[field] = None
    calls = []
    async def invoke(session, tool, args, report):
        calls.append(tool)
        if fault == "query_error":
            raise ConnectionError("private raw exception")
        return {"ok": True, "data": {"serverCommandObservation": facts}}
    monkeypatch.setattr(client, "invoke", invoke)
    report = {"witnessFiles": client.collect_witness(directory),
              "originalTerminal": {"status": "Failed", "resultError": {"code": "INTERRUPTED"}}}
    original = copy.deepcopy(report["originalTerminal"])
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    assert calls == ["unity_operation_get"]
    assert report["originalTerminal"] == original
    evidence = report["serverCommandEvidence"]
    if fault == "query_error":
        assert evidence["status"] == "unknown"
        assert evidence["reason"] == "command_observation_unavailable"
        assert "private raw" not in json.dumps(evidence)
    else:
        assert evidence["status"] == "observed"
        assert len(evidence["missingFacts"]) == 4
        assert evidence["observation"]["responseWaitDeadlineAt"] is None


@pytest.mark.parametrize("field,value", [("resourcesReleased", False), ("resourcesReleased", None),
    ("endedAt", 0), ("endedAt", 1999), ("endedAt", None)])
def test_witness_cleanup_receipt_cannot_be_inferred_from_reload_or_effect(client, tmp_path, field, value):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    after_path = directory / "after-reload.json"
    after = json.loads(after_path.read_text())
    after[field] = value
    after_path.write_text(json.dumps(after))
    with pytest.raises(client.CallPreflightError, match="cleanup receipt"):
        client.read_witness(directory, "nonce")
    assert json.loads(after_path.read_text()) == after  # Never repair an old receipt.


def test_legacy_scope_remains_readable_but_total_budget_is_unknown(client, monkeypatch, tmp_path):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    facts = command_observation()
    facts["scope"] = "current-service-lifetime; response-wait-only; send-attempts-not-execution-count"
    for field in ("callStartedAt", "callDeadlineAt", "callTimeoutMs"):
        facts.pop(field)
    async def invoke(*args):
        return {"ok": True, "data": {"serverCommandObservation": facts}}
    monkeypatch.setattr(client, "invoke", invoke)
    report = {"witnessFiles": client.collect_witness(directory)}
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    evidence = report["serverCommandEvidence"]
    assert evidence["status"] == "observed" and evidence["totalBudgetStatus"] == "unknown"
    assert evidence["missingFacts"] == ["callStartedAt", "callDeadlineAt", "callTimeoutMs"]


@pytest.mark.parametrize("field,value", [("callDeadlineAt", 46001), ("callTimeoutMs", 0),
    ("responseWaitDeadlineAt", 46001)])
def test_inconsistent_budget_facts_never_become_verified(client, monkeypatch, tmp_path, field, value):
    directory = tmp_path / "witness"
    write_witness(directory, "nonce")
    facts = command_observation()
    facts[field] = value
    async def invoke(*args):
        return {"ok": True, "data": {"serverCommandObservation": facts}}
    monkeypatch.setattr(client, "invoke", invoke)
    report = {"witnessFiles": client.collect_witness(directory)}
    asyncio.run(client.observe_original_command(object(), directory, "nonce", report))
    assert report["serverCommandEvidence"]["totalBudgetStatus"] != "observed"
