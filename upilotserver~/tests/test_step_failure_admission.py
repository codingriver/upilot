import asyncio
import copy
import hashlib

import pytest

from test_orchestration_suspension import StepService, step_spec
from test_persistent_operations import stop_observers
from upilot_mcp.config import CONFIG
from upilot_mcp.domain.task_service import _operation_cleanup_pending
from upilot_mcp.responses import fail, ok


@pytest.fixture(autouse=True)
def isolated_write_grant(monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    monkeypatch.setattr("upilot_mcp.domain.task_service.refresh_config_if_changed", lambda: None)


def rejection(code="STEP_EDIT_MODE_REQUIRED"):
    return {"ok": False, "started": False, "code": code, "message": "Start in stable EditMode.",
            "currentMode": "play", "instanceId": "scene"}


def test_admission_rejects_before_operation_capture_or_business(tmp_path):
    service = StepService(tmp_path)
    service.admission_reply = rejection()
    job = step_spec()
    job["consoleCapture"]["enabled"] = True
    response = asyncio.run(service.operation_start(job))
    assert not response.ok and response.error.code == "STEP_EDIT_MODE_REQUIRED"
    assert response.error.detail == rejection()
    assert service.admissions == [job["stepPlan"]]
    assert not service._operations and not service.calls


@pytest.mark.parametrize("response_kind", ["exception", "transport", "malformed"])
def test_admission_unknown_never_starts_or_retries(tmp_path, response_kind):
    async def run():
        service = StepService(tmp_path)
        original = service.dispatcher.call
        observed = []

        async def call(request_id, route, payload):
            if route != "automation.steps.admission":
                return await original(request_id, route, payload)
            observed.append(route)
            if response_kind == "exception":
                raise TimeoutError("Admission response lost.")
            if response_kind == "transport":
                return fail(request_id, "BRIDGE_DISCONNECTED", "Disconnected.")
            return ok(request_id, {"ok": False})

        service.dispatcher.call = call
        response = await service.operation_start(step_spec())
        assert not response.ok and response.error.code == "STEP_ADMISSION_UNKNOWN"
        assert response.error.detail["started"] is False
        assert observed == ["automation.steps.admission"]
        assert not service._operations and not service.calls
    asyncio.run(run())


def test_start_rejection_after_admission_releases_only_owned_capture(tmp_path):
    async def run():
        service = StepService(tmp_path)
        order = []
        stops = []
        observations = []

        async def capture(state, **kwargs):
            order.append("capture")
            return ok("capture", {"sessionId": "owned-capture"})

        async def start(call, operation_id=""):
            order.append("start")
            assert call["route"] == "automation.steps.start"
            assert service.admissions == [step_spec()["stepPlan"]]
            return ok("start", rejection("STEP_MODE_TRANSITION_BUSY"))

        async def release(state):
            observations.append(state["operationId"])
            owned = state["consoleCapture"]
            assert owned["sessionId"] == "owned-capture"
            if not owned.get("stopIntentSent"):
                owned["stopIntentSent"] = True
                stops.append(owned["sessionId"])
            else:
                owned.update(stopped=True, artifactsVerified=True)

        service._start_owned_operation_capture = capture
        service._stop_owned_operation_capture = release
        service._operation_invoke = start
        job = step_spec()
        job.update(consoleCapture={"enabled": True}, cleanup={"requireEditMode": True, "timeoutSec": 30})
        try:
            response = await service.operation_start(job)
            assert not response.ok and response.error.code == "STEP_MODE_TRANSITION_BUSY"
            state = service._operations[response.error.detail["operationId"]]
            assert state["startRejected"] and state["businessTerminal"] and not state["endedAt"]
            assert not state.get("startEstablished") and state["startAttemptCount"] == 1
            deadline = state["cleanupDeadlineAt"]
            terminal = await service.operation_cancel(state["operationId"])
            assert terminal.ok and terminal.data["terminal"] and terminal.data["status"] == "Failed"
            assert terminal.data["editorVerification"] == "not_requested"
            assert state["cleanupDeadlineAt"] == deadline
            assert order == ["capture", "start"] and stops == ["owned-capture"]
            assert len(observations) == 2 and state["cancelAttemptCount"] == 0
            await service.operation_status(state["operationId"])
            assert len(observations) == 2
        finally:
            await stop_observers(service)
    asyncio.run(run())


def test_start_transport_failure_is_never_a_deterministic_rejection(tmp_path):
    async def run():
        service = StepService(tmp_path)
        calls = []

        async def lost(call, operation_id=""):
            calls.append(call["route"])
            return fail("start", "BRIDGE_DISCONNECTED", "Start reply lost.")

        service._operation_invoke = lost
        response = await service.operation_start(step_spec())
        assert not response.ok and response.error.code == "OPERATION_START_UNKNOWN"
        state = service._operations[response.error.detail["operationId"]]
        assert not state.get("startRejected")
        await service.operation_status(state["operationId"])
        assert calls == ["automation.steps.start"] and state["startAttemptCount"] == 1
    asyncio.run(run())


def step_terminal(operation_id, *, pending=False, outcome="Unverified", succeeded=False, report="Written"):
    return {"runId": "fake-run", "operationId": operation_id, "status": "Failed",
            "phase": "ResourceReleasing" if pending else "Completed", "terminal": True,
            "businessTerminal": True, "cleanupPending": pending, "cleanupOutcome": outcome,
            "cleanupSucceeded": succeeded, "reportOutcome": report,
            "unresolvedResources": ["businessCleanup:finally.restore"],
            "error": "ORIGINAL_BUSINESS_FAILURE", "failureSignature": "ORIGINAL_BUSINESS_FAILURE"}


@pytest.mark.parametrize("outcome", ["Verified", "Unverified", "NotRequired"])
@pytest.mark.parametrize("report", ["Written", "Failed"])
def test_step_cleanup_ends_with_unresolved_evidence_without_claiming_success(tmp_path, outcome, report):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]
        observations = []

        async def status(call, operation_id=""):
            observations.append(call["route"])
            return ok("status", step_terminal(operation_id, outcome=outcome, report=report))

        service._operation_invoke = status
        try:
            ended = await service.operation_status(operation_id)
            assert ended.ok and ended.data["terminal"] and ended.data["status"] == "Failed"
            assert not ended.data["cleanupPending"] and not ended.data["cleanupSucceeded"]
            assert ended.data["unresolvedResources"] == ["businessCleanup:finally.restore"]
            assert ended.data["cleanupOutcome"] == outcome and ended.data["reportOutcome"] == report
            state = service._operations[operation_id]
            before = copy.deepcopy(state["businessResult"])
            collection_sequence = state["artifactCollectionSequence"]
            await service.operation_status(operation_id)
            await service.operation_wait(operation_id, timeout_s=1)
            await service.operation_cancel(operation_id)
            assert observations == ["automation.steps.state"]
            assert state["businessResult"] == before and state["cancelAttemptCount"] == 0
            assert state["artifactCollectionSequence"] == collection_sequence
        finally:
            await stop_observers(service)
    asyncio.run(run())


def test_final_cleanup_refreshes_report_once_without_replacing_business_failure(tmp_path):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]
        pending = True
        observations = []
        path = tmp_path / "failure-report.txt"
        path.write_bytes(b"Finally failed; restoration was skipped.")
        declaration = {"kind": "file", "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                       "bytes": path.stat().st_size}

        async def status(call, operation_id=""):
            observations.append(call["route"])
            payload = step_terminal(operation_id, pending=pending,
                                    outcome="Pending" if pending else "Unverified",
                                    report="Pending" if pending else "Written")
            if not pending:
                payload.update(error="LATER_CLEANUP_FAILURE", failureSignature="LATER_CLEANUP_FAILURE",
                               artifacts={"attachments": [declaration]})
            return ok("status", payload)

        service._operation_invoke = status
        try:
            first = await service.operation_status(operation_id)
            assert first.data["businessTerminal"] and not first.data["terminal"]
            state = service._operations[operation_id]
            deadline = state["cleanupDeadlineAt"]
            before = copy.deepcopy(state["businessResult"])
            sequence = state["artifactCollectionSequence"]
            pending = False
            final = await service.operation_wait(operation_id, timeout_s=1)
            assert final.data["terminal"] and not final.data["cleanupSucceeded"]
            assert state["businessResult"] == before and final.data["error"] == "ORIGINAL_BUSINESS_FAILURE"
            assert state["cleanupDeadlineAt"] == deadline
            assert state["lastStatusData"]["artifacts"]["attachments"] == [declaration]
            assert state["artifactHints"] == state["lastStatusData"]["artifacts"]
            assert final.data["artifactCollectionSequence"] == sequence + 1
            assert state["artifacts"]["attachments[0]"]["sha256"] == declaration["sha256"]
            assert state["artifactErrors"] == []
            await service.operation_status(operation_id)
            await service.operation_wait(operation_id, timeout_s=1)
            assert len(observations) == 2 and state["artifactCollectionSequence"] == sequence + 1
        finally:
            await stop_observers(service)
    asyncio.run(run())


def test_terminal_start_consumes_final_report_collection_once(tmp_path):
    async def run():
        service = StepService(tmp_path)

        async def start(call, operation_id=""):
            assert call["route"] == "automation.steps.start"
            return ok("start", step_terminal(operation_id))

        service._operation_invoke = start
        result = await service.operation_start(step_spec())
        assert result.ok and result.data["terminal"]
        state = service._operations[result.data["operationId"]]
        assert state["artifactCollectionSequence"] == 1
        await service.operation_wait(state["operationId"], timeout_s=1)
        assert state["artifactCollectionSequence"] == 1
        assert not state.get("_deferredArtifactCollection")
    asyncio.run(run())


@pytest.mark.parametrize("status", ["Succeeded", "Failed", "TimedOut"])
def test_wait_collects_new_step_final_snapshot_once_but_explicit_collect_can_reread(tmp_path, status):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]

        async def observe(call, operation_id=""):
            assert call["route"] == "automation.steps.state"
            payload = step_terminal(operation_id, outcome="Verified", succeeded=True)
            payload.update(status=status, unresolvedResources=[])
            return ok("status", payload)

        service._operation_invoke = observe
        try:
            first = await service.operation_wait(operation_id, timeout_s=1)
            assert first.ok and first.data["terminal"] and first.data["artifactCollectionSequence"] == 1
            await service.operation_wait(operation_id, timeout_s=1)
            state = service._operations[operation_id]
            assert state["artifactCollectionSequence"] == 1
            explicit = await service.operation_collect_artifacts(operation_id)
            assert explicit.ok and explicit.data["artifactCollectionSequence"] == 2
            await service.operation_wait(operation_id, timeout_s=1)
            assert state["artifactCollectionSequence"] == 2
        finally:
            await stop_observers(service)
    asyncio.run(run())


def test_wait_retains_legacy_collection_behavior_without_new_cleanup_contract(tmp_path):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]

        async def observe(call, operation_id=""):
            payload = step_terminal(operation_id)
            payload.pop("cleanupOutcome")
            payload["unresolvedResources"] = []
            return ok("status", payload)

        service._operation_invoke = observe
        try:
            await service.operation_status(operation_id)
            state = service._operations[operation_id]
            assert state["artifactCollectionSequence"] == 1 and not state.get("_stepFinalArtifactsObserved")
            await service.operation_wait(operation_id, timeout_s=1)
            assert state["artifactCollectionSequence"] == 2
        finally:
            await stop_observers(service)
    asyncio.run(run())


def test_wait_does_not_retry_failed_final_collection_but_explicit_collect_can(tmp_path):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]
        writes = []
        save = service.server.state.save_operation

        async def observe(call, operation_id=""):
            return ok("status", step_terminal(operation_id))

        def fail_collection_once(state):
            if state.get("artifactCollectionSequence"):
                writes.append(state["artifactCollectionSequence"])
                if len(writes) == 1:
                    raise OSError("Final artifact archive unavailable.")
            save(state)

        service._operation_invoke = observe
        service.server.state.save_operation = fail_collection_once
        try:
            first = await service.operation_wait(operation_id, timeout_s=1)
            assert first.ok and first.data["terminal"]
            assert first.data["collectionPersisted"] is False
            assert first.data["artifactPersistenceError"] == "Final artifact archive unavailable."
            assert first.data["artifactCollectionSequence"] == 0
            state = service._operations[operation_id]
            assert state["endedAt"] and state.get("artifactCollectionSequence", 0) == 0
            assert writes == [1] and state["error"] == "ORIGINAL_BUSINESS_FAILURE"
            second = await service.operation_wait(operation_id, timeout_s=1)
            assert second.ok and second.data["terminal"] and writes == [1]
            assert state["error"] == "ORIGINAL_BUSINESS_FAILURE"
            explicit = await service.operation_collect_artifacts(operation_id)
            assert explicit.ok and explicit.data["collectionPersisted"]
            assert writes == [1, 2] and state["artifactCollectionSequence"] == 2
        finally:
            service.server.state.save_operation = save
            await stop_observers(service)
    asyncio.run(run())


@pytest.mark.parametrize("change", ["legacy", "wrong_run", "missing_run", "wrong_operation", "other_route", "other_plan"])
def test_terminal_cleanup_override_requires_official_step_and_exact_identity(change):
    payload = step_terminal("operation")
    state = {"operationId": "operation", "jobSpec": {"stepPlan": {},
             "statusCall": {"kind": "bridge", "route": "automation.steps.state"}},
             "startData": {"runId": "fake-run"}}
    if change == "legacy":
        payload.pop("cleanupOutcome")
    elif change == "wrong_run":
        payload["runId"] = "other-run"
    elif change == "missing_run":
        payload.pop("runId")
    elif change == "wrong_operation":
        payload["operationId"] = "other-operation"
    elif change == "other_route":
        state["jobSpec"]["statusCall"]["route"] = "project.state"
    else:
        state["jobSpec"].pop("stepPlan")
    assert _operation_cleanup_pending(payload, state)


def test_expired_original_cleanup_deadline_does_not_issue_more_observations(tmp_path):
    async def run():
        service = StepService(tmp_path)
        started = await service.operation_start(step_spec())
        operation_id = started.data["operationId"]
        observations = []

        async def status(call, operation_id=""):
            observations.append(call["route"])
            return ok("status", step_terminal(operation_id, pending=True, outcome="Pending", report="Pending"))

        service._operation_invoke = status
        try:
            await service.operation_status(operation_id)
            state = service._operations[operation_id]
            state["cleanupDeadlineAt"] = 1
            ended = await service.operation_status(operation_id)
            assert ended.data["terminal"] and ended.data["status"] == "timed_out"
            assert state["cleanupDeadlineAt"] == 1 and not ended.data["cleanupSucceeded"]
            assert observations == ["automation.steps.state"]
            assert state["businessResult"]["error"] == "ORIGINAL_BUSINESS_FAILURE"
        finally:
            await stop_observers(service)
    asyncio.run(run())
