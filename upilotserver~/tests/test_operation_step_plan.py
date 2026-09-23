import asyncio
import json
import hashlib
from types import SimpleNamespace

import pytest

from test_operation_runner_and_agent_rules import _OperationService
from upilot_mcp.responses import fail, ok


def service(tmp_path, valid=True, budget=100):
    result = _OperationService(tmp_path, [])
    result.bridge_calls = []

    async def call(request_id, route, payload, **kwargs):
        result.bridge_calls.append((route, payload))
        if route == "automation.steps.validate":
            return ok(request_id, {"ok": valid, "budgetSeconds": budget, "diagnostics": [] if valid else [
                {"index": 0, "code": "STEP_NOT_FOUND", "message": "Missing step.", "severity": "error"}]})
        if route == "automation.steps.start":
            return ok(request_id, {"status": "Running", "runId": "step-run", "operationId": payload["operationId"]})
        return ok(request_id, {"status": "Running", "runId": "step-run", "operationId": payload["operationId"]})
    result.dispatcher = SimpleNamespace(call=call)
    return result


def spec(arguments="  ${start.literal}  "):
    return {"stepPlan": {"version": 1, "steps": [
        {"instanceId": "wait", "stepId": "upilot.wait_seconds", "arguments": arguments}]}}

@pytest.mark.parametrize("compatibility_bridge", [False, True])
def test_step_attachments_use_existing_file_integrity_checks_without_directory_scan(tmp_path, compatibility_bridge):
    path = tmp_path / "evidence.json"
    path.write_bytes(b'{"value":1}')
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    untouched = tmp_path / "not-declared.txt"
    untouched.write_text("must not be collected")
    declaration = {"kind": "file", "path": "evidence.json", "bytes": path.stat().st_size,
                   "sha256": digest, "instanceId": "case-a", "artifactKind": "project.case"}
    state = {"jobSpec": {} if compatibility_bridge else spec(), "projectPath": str(tmp_path),
             "lastStatusData": {"artifacts": {"attachments": [declaration]}}}
    svc = service(tmp_path)
    artifacts, errors = svc._collect_operation_artifacts(state, 500)
    assert errors == []
    assert set(artifacts) == {"attachments[0]"}
    assert artifacts["attachments[0]"]["sha256"] == digest
    assert artifacts["attachments[0]"]["instanceId"] == "case-a"
    assert artifacts["attachments[0]"]["artifactKind"] == "project.case"
    assert state["lastStatusData"]["artifacts"]["attachments"] == [declaration]
    path.write_bytes(b"modified")
    artifacts, errors = svc._collect_operation_artifacts(state, 500)
    assert artifacts["attachments[0]"]["sha256"] == digest
    assert {error["error"] for error in errors} >= {"ARTIFACT_DECLARED_SHA256_MISMATCH"}


@pytest.mark.parametrize("attachment,error", [
    ({"kind": "file", "path": "../outside.txt"}, "ARTIFACT_PATH_OUTSIDE_PROJECT"),
    ({"kind": "file", "path": "missing.txt"}, "ARTIFACT_MISSING"),
    ({"kind": "file"}, "ARTIFACT_FILE_PATH_MISSING"),
    ({"kind": "metadata", "path": "evidence.json"}, "ARTIFACT_ATTACHMENT_INVALID"),
    ("evidence.json", "ARTIFACT_ATTACHMENT_INVALID"),
])
def test_step_attachment_invalid_declarations_are_reported(tmp_path, attachment, error):
    state = {"jobSpec": spec(), "projectPath": str(tmp_path),
             "lastStatusData": {"artifacts": {"attachments": [attachment]}}}
    artifacts, errors = service(tmp_path)._collect_operation_artifacts(state, 500)
    assert errors == [{"artifact": "attachments[0]", "error": error,
                       **({"path": artifacts["attachments[0]"]["path"]} if error in
                          {"ARTIFACT_PATH_OUTSIDE_PROJECT", "ARTIFACT_MISSING"} else {})}]


def test_legacy_attachment_array_remains_metadata_and_step_schema_is_checked(tmp_path):
    svc = service(tmp_path)
    state = {"jobSpec": {}, "projectPath": str(tmp_path),
             "lastStatusData": {"artifacts": {"attachments": ["legacy-value"]}}}
    artifacts, errors = svc._collect_operation_artifacts(state, 500)
    assert not errors
    assert artifacts["attachments"] == {"kind": "metadata", "value": ["legacy-value"]}
    state["jobSpec"] = spec()
    state["lastStatusData"]["artifacts"]["attachments"] = {}
    _, errors = svc._collect_operation_artifacts(state, 500)
    assert errors[0]["error"] == "ARTIFACT_ATTACHMENTS_INVALID"


def test_validation_is_read_only_and_preserves_arguments(tmp_path):
    svc = service(tmp_path)
    response = asyncio.run(svc.operation_validate(spec()))
    assert response.ok
    assert [c[0] for c in svc.bridge_calls] == ["automation.steps.validate"]
    assert json.loads(svc.bridge_calls[0][1]["planJson"]) == spec()["stepPlan"]
    assert "startCall" not in response.data["normalizedJobSpec"]


def test_invalid_plan_never_creates_capture_or_starts(tmp_path):
    svc = service(tmp_path, valid=False)
    job = {**spec(), "consoleCapture": {"enabled": True}}
    response = asyncio.run(svc.operation_start(job))
    assert not response.ok
    assert svc._operations == {}
    assert [c[0] for c in svc.bridge_calls] == ["automation.steps.validate"]


@pytest.mark.parametrize("field", ["startCall", "statusCall", "cancelCall"])
def test_plan_and_calls_are_mutually_exclusive(tmp_path, field):
    svc = service(tmp_path)
    response = asyncio.run(svc.operation_validate({**spec(), field: {}}))
    assert not response.ok
    assert any(e["code"] == "STEP_PLAN_CALLS_CONFLICT" for e in response.error.detail["errors"])


def test_budget_does_not_truncate_long_plan(tmp_path):
    svc = service(tmp_path, budget=900)
    response = asyncio.run(svc.operation_validate(spec()))
    assert response.data["normalizedJobSpec"]["timeoutSec"] == 900
    rejected = asyncio.run(svc.operation_validate({**spec(), "timeoutSec": 300}))
    assert not rejected.ok


def test_start_persists_existing_operation_identity_without_expanding_arguments(tmp_path):
    async def run():
        svc = service(tmp_path)
        response = await svc.operation_start(spec("${operation.operationId}"))
        assert response.ok
        state = svc._operations[response.data["operationId"]]
        assert state["recoveryIdentityEstablished"]
        start = next(payload for route, payload in svc.bridge_calls if route.endswith(".start"))
        assert start["operationId"] == state["operationId"]
        assert json.loads(start["planJson"])["steps"][0]["arguments"] == "${operation.operationId}"
        assert state["resolvedStatusCall"]["payload"]["runId"] == "step-run"
        assert state["startAttemptCount"] == 1
    asyncio.run(run())


@pytest.mark.parametrize("value", [None, True, "bad", 0, float("inf")])
def test_timeout_type_is_structured_error(tmp_path, value):
    response = asyncio.run(service(tmp_path).operation_validate({**spec(), "timeoutSec": value}))
    assert not response.ok


def test_nonfinite_plan_never_dispatches(tmp_path):
    svc = service(tmp_path)
    job = spec()
    job["stepPlan"]["steps"][0]["timeoutSeconds"] = float("nan")
    response = asyncio.run(svc.operation_validate(job))
    assert not response.ok
    assert svc.bridge_calls == []


def test_normalized_plan_is_a_detached_snapshot(tmp_path):
    job = spec()
    response = asyncio.run(service(tmp_path).operation_validate(job))
    job["stepPlan"]["steps"][0]["arguments"] = "mutated"
    assert response.data["normalizedJobSpec"]["stepPlan"]["steps"][0]["arguments"] != "mutated"


def test_log_policy_cannot_be_silently_bypassed_without_capture(tmp_path):
    svc = service(tmp_path)
    job = spec()
    job["stepPlan"]["logPolicy"] = [{"id": "project-rule"}]
    response = asyncio.run(svc.operation_start(job))
    assert not response.ok
    assert svc._operations == {}
    assert any(e["code"] == "STEP_CAPTURE_REQUIRED" for e in response.error.detail["errors"])


@pytest.mark.parametrize("enabled", [None, True, "false", 0])
def test_plan_capture_requires_explicitly_disabled_operation_capture(tmp_path, enabled):
    svc = service(tmp_path)
    job = spec()
    job["stepPlan"]["steps"].insert(0, {"instanceId": "capture", "stepId": "upilot.console_capture_start", "arguments": ""})
    if enabled is not None:
        job["consoleCapture"] = {"enabled": enabled}
    response = asyncio.run(svc.operation_start(job))
    assert not response.ok
    assert svc._operations == {}
    assert any(e["code"] == "STEP_CAPTURE_OWNERSHIP_CONFLICT" for e in response.error.detail["errors"])


def test_plan_capture_supplies_policy_evidence_without_operation_ownership(tmp_path):
    svc = service(tmp_path)
    job = spec()
    job["stepPlan"]["steps"].insert(0, {"instanceId": "capture", "stepId": "upilot.console_capture_start", "arguments": ""})
    job["stepPlan"]["logPolicy"] = [{"id": "business-rule"}]
    job["consoleCapture"] = {"enabled": False}
    response = asyncio.run(svc.operation_validate(job))
    assert response.ok
    assert response.data["normalizedJobSpec"]["consoleCapture"]["enabled"] is False
    assert [route for route, _ in svc.bridge_calls] == ["automation.steps.validate"]


def test_initializing_step_service_keeps_observing_same_run_without_replay(tmp_path):
    async def run():
        svc = service(tmp_path)
        started = await svc.operation_start(spec())
        operation_id = started.data["operationId"]
        state = svc._operations[operation_id]
        previous = svc.dispatcher.call
        observations = 0

        async def call(request_id, route, payload, **kwargs):
            nonlocal observations
            if route == "automation.steps.state":
                svc.bridge_calls.append((route, payload))
                observations += 1
                assert payload["runId"] == "step-run"
                assert payload["operationId"] == operation_id
                if observations == 1:
                    return fail(request_id, "STEP_SERVICE_INITIALIZING", "Deferred initialization pending.")
                return ok(request_id, {
                    "status": "Running", "runId": "step-run", "operationId": operation_id,
                    "phase": "Executing", "terminal": False, "cleanupPending": True,
                })
            return await previous(request_id, route, payload, **kwargs)

        svc.dispatcher.call = call
        waiting = await svc.operation_status(operation_id)
        assert waiting.ok and waiting.data["phase"] == "Recovering"
        assert waiting.data["status"] != "RecoveryRequired"
        assert not waiting.data["terminal"]
        observed = await svc.operation_status(operation_id)
        assert observed.ok and observed.data["phase"] == "Executing"
        assert state["lastObservationError"] == ""
        assert state["startAttemptCount"] == 1
        assert state["cancelAttemptCount"] == 0
        assert len([route for route, _ in svc.bridge_calls if route.endswith(".start")]) == 1
        assert observations == 2
    asyncio.run(run())


def test_real_step_identity_mismatch_is_not_treated_as_initialization(tmp_path):
    async def run():
        svc = service(tmp_path)
        started = await svc.operation_start(spec())
        operation_id = started.data["operationId"]
        previous = svc.dispatcher.call

        async def call(request_id, route, payload, **kwargs):
            if route == "automation.steps.state":
                svc.bridge_calls.append((route, payload))
                return fail(request_id, "STEP_REQUEST_FAILED", "STEP_RUN_IDENTITY_MISMATCH")
            return await previous(request_id, route, payload, **kwargs)

        svc.dispatcher.call = call
        failed = await svc.operation_status(operation_id)
        assert not failed.ok
        assert failed.error.detail["status"] == "RecoveryRequired"
        count = len(svc.bridge_calls)
        retained = await svc.operation_status(operation_id)
        assert retained.data["status"] == "RecoveryRequired"
        assert len(svc.bridge_calls) == count
        assert svc._operations[operation_id]["startAttemptCount"] == 1
    asyncio.run(run())


def test_editor_restart_recovery_is_not_cleared_after_initialization(tmp_path):
    async def run():
        svc = service(tmp_path)
        started = await svc.operation_start(spec())
        operation_id = started.data["operationId"]

        async def call(request_id, route, payload, **kwargs):
            assert route == "automation.steps.state"
            return ok(request_id, {
                "status": "RecoveryRequired", "runId": "step-run", "operationId": operation_id,
                "phase": "Executing", "error": "STEP_EDITOR_RESTARTED",
                "failureSignature": "STEP_EDITOR_RESTARTED", "terminal": False, "cleanupPending": True,
            })

        svc.dispatcher.call = call
        observed = await svc.operation_status(operation_id)
        assert observed.ok and observed.data["status"] == "RecoveryRequired"
        assert observed.data["error"] == "STEP_EDITOR_RESTARTED"
        assert observed.data["cleanupPending"] and not observed.data["terminal"]
        assert svc._operations[operation_id]["startAttemptCount"] == 1
        assert svc._operations[operation_id]["cancelAttemptCount"] == 0
    asyncio.run(run())


def test_initialization_error_does_not_relax_unrelated_status_routes(tmp_path):
    async def run():
        svc = service(tmp_path)
        started = await svc.operation_start(spec())
        operation_id = started.data["operationId"]
        svc._operations[operation_id]["resolvedStatusCall"]["route"] = "another.state"

        async def call(request_id, route, payload, **kwargs):
            assert route == "another.state"
            return fail(request_id, "STEP_SERVICE_INITIALIZING", "Unrelated service failure.")

        svc.dispatcher.call = call
        failed = await svc.operation_status(operation_id)
        assert not failed.ok
        assert failed.error.detail["status"] == "RecoveryRequired"
    asyncio.run(run())
