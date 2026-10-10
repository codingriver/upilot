import asyncio
import hashlib
import json

import pytest

from upilot_mcp.responses import ok, fail
from test_operation_runner_and_agent_rules import _OperationService


def spec(**extra):
    return {"startCall": {"kind": "tool", "toolName": "start"},
            "statusCall": {"kind": "tool", "toolName": "status"}, **extra}


def test_start_call_receives_its_own_operation_identity(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.seed_legacy_operation(spec(startCall={
            "kind": "tool", "toolName": "start",
            "toolArgs": {"operationId": "${operation.operationId}"},
        }))
        assert service.calls[0][1]["operationId"] == started.data["operationId"]
    asyncio.run(run())


def test_cleanup_returns_correlated_exit_but_does_not_copy_another_jobs_transition(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        transition = {"operationId": "another-job", "at": 9999999999999, "transitionId": "exit"}
        fresh = [False]
        async def status(**_):
            return ok("editor", {
                "playModeTransition": transition,
                "executionState": dict(ready=fresh[0], authoritative=True, isStale=False,
                                       playModeState="edit", observedAt=9999999999999),
            })
        service.mcp_status = status
        start = await service.seed_legacy_operation(spec(cleanup={"requireEditMode": True}))
        result = await service.operation_status(start.data["operationId"])
        assert not result.data["playModeTransition"]
        transition["operationId"] = start.data["operationId"]
        fresh[0] = True
        result = await service.operation_status(start.data["operationId"])
        assert result.data["terminal"]
        assert result.data["playModeTransition"]["transitionId"] == "exit"
    asyncio.run(run())


def test_success_waits_for_fresh_editmode_and_serializes_pollers(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        async def editor_status(**_):
            return ok("editor", {"executionState": dict(ready=True, authoritative=True, isStale=False,
                      playModeState="edit", observedAt=observed[0])})
        service.mcp_status = editor_status
        observed = [0]
        start = await service.seed_legacy_operation(spec(cleanup={"requireEditMode": True}))
        operation_id = start.data["operationId"]
        first = await service.operation_status(operation_id)
        assert first.data["status"] == "CleaningUp" and not first.data["terminal"]
        assert first.data["businessTerminal"]
        observed[0] = first.data["businessEndedAt"]
        same_timestamp = await service.operation_status(operation_id)
        assert not same_timestamp.data["terminal"]
        observed[0] += 1
        results = await asyncio.gather(service.operation_status(operation_id), service.operation_status(operation_id))
        assert all(r.data["terminal"] and r.data["status"] == "Succeeded" for r in results)
        assert len([call for call in service.calls if call[0] == "status"]) == 1
    asyncio.run(run())


def test_editor_verification_distinguishes_not_requested_pending_and_failed(tmp_path):
    async def run():
        normal = _OperationService(tmp_path / "normal", [{"status": "Succeeded"}])
        ordinary = await normal.seed_legacy_operation(spec())
        ordinary_status = await normal.operation_status(ordinary.data["operationId"])
        assert ordinary_status.data["terminal"] and ordinary_status.data["editorVerification"] == "not_requested"

        required = _OperationService(tmp_path / "required", [{"status": "Succeeded"}])
        async def play_status(**_):
            return ok("editor", {"executionState": {
                "ready": False, "authoritative": True, "isStale": False,
                "playModeState": "play", "observedAt": 1,
            }})
        required.mcp_status = play_status
        started = await required.seed_legacy_operation(spec(cleanup={"requireEditMode": True, "timeoutSec": 1}))
        pending = await required.operation_status(started.data["operationId"])
        assert not pending.data["terminal"] and pending.data["editorVerification"] == "pending"
        required._operations[started.data["operationId"]]["cleanupDeadlineAt"] = 1
        failed = await required.operation_status(started.data["operationId"])
        assert failed.data["terminal"] and failed.data["editorVerification"] == "failed"
        assert failed.data["status"] == "timed_out"
        assert failed.data["cleanupFailureSignature"] == "OperationCleanupTimeout"
        assert failed.data["businessResult"]["status"] == "Succeeded"
    asyncio.run(run())


def test_historical_operation_without_editor_verification_is_unknown(tmp_path):
    service = _OperationService(tmp_path, [])
    public = service._public_operation_state({"operationId": "historic", "startedAt": 1})
    assert public["editorVerification"] == "unknown"


def test_capture_failure_is_not_stopped_or_success(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        async def stop(**_):
            return fail("stop", "STOP_FAILED", "still active")
        service.console_capture_stop = stop
        start = await service.seed_legacy_operation(spec())
        operation_id = start.data["operationId"]
        state = service._operations[operation_id]
        state["consoleCapture"] = {"sessionId": "capture", "ownerToken": "owner"}
        result = await service.operation_status(operation_id)
        assert result.data["terminal"] is False
        assert state["consoleCapture"]["stopped"] is False
        state["cleanupDeadlineAt"] = 1
        result = await service.operation_status(operation_id)
        assert result.data["terminal"] and not result.data["cleanupPending"]
        assert result.data["status"] == "timed_out"
        assert state["consoleCapture"]["stopped"] is False
        assert result.data["cleanupFailureSignature"] == "OperationCleanupTimeout"
        assert result.data["businessResult"]["status"] == "Succeeded"
    asyncio.run(run())


def test_invalid_cleanup_rejected_without_start(tmp_path):
    service = _OperationService(tmp_path, [])
    for cleanup in ([], {"timeoutSec": 0}, {"timeoutSec": float("inf")}, {"requireEditMode": "yes"}):
        result = asyncio.run(service.seed_legacy_operation(spec(cleanup=cleanup)))
        assert not result.ok
    assert not service.calls


def test_business_terminal_is_latched_while_project_cleanup_is_pending(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [
            {"status": "Succeeded", "cleanupPending": True},
            {"status": "Succeeded", "cleanupPending": False},
        ])
        start = await service.seed_legacy_operation(spec())
        first = await service.operation_status(start.data["operationId"])
        assert first.data["businessTerminal"] and not first.data["terminal"]
        assert first.data["status"] == "CleaningUp"
        assert "businessCleanup" in first.data["unresolvedResources"]
        last = await service.operation_status(start.data["operationId"])
        assert last.data["status"] == "Succeeded" and last.data["terminal"]
        assert last.data["businessEndedAt"] == first.data["businessEndedAt"]
    asyncio.run(run())


def test_capture_stop_requires_actual_matching_artifacts(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        content = b'{"message":"test"}\n'
        (tmp_path / "console.jsonl").write_bytes(b"x" * len(content))
        session = dict(sessionId="capture", active=False, finishedAtUtcMs=1,
                       jsonlPath="console.jsonl", summaryPath="summary.json",
                       fileBytes=len(content), sha256=hashlib.sha256(content).hexdigest(), segmentCount=1)
        (tmp_path / "summary.json").write_text(json.dumps(session))
        stops = []
        async def stop(**_):
            stops.append(1)
            return ok("stop", {"session": session})
        service.console_capture_stop = stop
        start = await service.seed_legacy_operation(spec())
        state = service._operations[start.data["operationId"]]
        state["consoleCapture"] = {"sessionId": "capture", "ownerToken": "owner"}
        first = await service.operation_status(start.data["operationId"])
        assert not first.data["terminal"]
        assert state["consoleCapture"]["stopped"] is True
        assert state["consoleCapture"]["artifactsVerified"] is False
        (tmp_path / "console.jsonl").write_bytes(content)
        last = await service.operation_status(start.data["operationId"])
        assert last.data["terminal"]
        assert state["consoleCapture"]["artifactsVerified"] is True
        assert len(stops) == 1
    asyncio.run(run())


def test_zero_byte_capture_is_verified_only_when_real_file_exists(tmp_path):
    from upilot_mcp.domain.task_service import _verify_capture_artifacts
    content = b""
    session = dict(sessionId="empty-capture", active=False, finishedAtUtcMs=1,
                   jsonlPath="console.jsonl", summaryPath="summary.json",
                   fileBytes=0, recordCount=0, segmentCount=1,
                   sha256=hashlib.sha256(content).hexdigest())
    (tmp_path / "summary.json").write_text(json.dumps(session))
    with pytest.raises(ValueError, match="Missing capture artifact"):
        _verify_capture_artifacts(tmp_path, session)
    (tmp_path / "console.jsonl").write_bytes(content)
    _verify_capture_artifacts(tmp_path, session)


def test_cancel_terminal_latches_business_result_before_project_cleanup(tmp_path):
    async def run():
        service = _OperationService(
            tmp_path, [{"status": "Canceled", "cleanupPending": False}],
            [ok("cancel", {"status": "Canceled", "cleanupPending": True})],
        )
        start = await service.seed_legacy_operation(spec(cancelCall={"kind": "tool", "toolName": "cancel"}))
        operation_id = start.data["operationId"]
        canceled = await service.operation_cancel(operation_id)
        assert canceled.data["businessTerminal"] and not canceled.data["terminal"]
        assert canceled.data["status"] == "CleaningUp"
        assert canceled.data["businessResult"]["status"] == "Canceled"
        duplicate = await service.operation_cancel(operation_id)
        assert duplicate.ok and not duplicate.data["changed"]
        assert duplicate.data["status"] in {"ending", "not_found"}
        final = await service.operation_status(operation_id)
        assert final.data["terminal"] and final.data["status"] == "Canceled"
        assert final.data["businessEndedAt"] == canceled.data["businessEndedAt"]
        assert [name for name, _ in service.calls].count("cancel") == 1
    asyncio.run(run())


def test_rotated_capture_requires_all_segments_and_matching_summary(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        (tmp_path / "console.jsonl").write_bytes(b"first\n")
        (tmp_path / "console.001.jsonl").write_bytes(b"second\n")
        content = b"first\nsecond\n"
        session = dict(sessionId="rotated", active=False, finishedAtUtcMs=1, segmentCount=2,
                       jsonlPath="console.001.jsonl", summaryPath="summary.json",
                       fileBytes=len(content), sha256=hashlib.sha256(content).hexdigest())
        # A stale summary cannot prove that the capture stopped.
        (tmp_path / "summary.json").write_text(json.dumps({**session, "active": True}))
        async def stop(**_):
            return ok("stop", {"session": session})
        service.console_capture_stop = stop
        start = await service.seed_legacy_operation(spec())
        state = service._operations[start.data["operationId"]]
        state["consoleCapture"] = {"sessionId": "rotated", "ownerToken": "owner"}
        result = await service.operation_status(start.data["operationId"])
        assert not result.data["terminal"]
        (tmp_path / "summary.json").write_text(json.dumps(session))
        (tmp_path / "console.jsonl").write_bytes(b"tampered\n")
        result = await service.operation_status(start.data["operationId"])
        assert not result.data["terminal"]
        (tmp_path / "console.jsonl").write_bytes(b"first\n")
        result = await service.operation_status(start.data["operationId"])
        assert result.data["terminal"] and result.data["status"] == "Succeeded"
        assert state["consoleCapture"]["artifactsVerified"]
    asyncio.run(run())


def test_capture_is_not_marked_stopped_when_artifact_verification_is_canceled(tmp_path, monkeypatch):
    async def run():
        service = _OperationService(tmp_path, [])
        entered = asyncio.Event()
        async def pending_verification(*_):
            entered.set()
            await asyncio.Event().wait()
        monkeypatch.setattr(asyncio, "to_thread", pending_verification)
        async def stop(**_):
            return ok("stop", {"session": dict(sessionId="capture", active=False, finishedAtUtcMs=1,
                       sha256="digest", summaryPath="summary.json", fileBytes=1)})
        service.console_capture_stop = stop
        started = await service.seed_legacy_operation(spec())
        state = service._operations[started.data["operationId"]]
        state["consoleCapture"] = {"sessionId": "capture", "ownerToken": "owner"}
        stopping = asyncio.create_task(service._stop_owned_operation_capture(state))
        await entered.wait()
        assert state["consoleCapture"]["stopped"] is True
        stopping.cancel()
        try:
            await stopping
        except asyncio.CancelledError:
            pass
        assert state["consoleCapture"]["stopped"] is True
        assert state["consoleCapture"]["artifactsVerified"] is False
    asyncio.run(run())


def test_unexpected_exit_is_generic_and_correlated_exit_is_allowed(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Running"}, {"status": "Running"}])
        transition = {"at": 9999999999999, "fromState": "play", "toState": "exitingPlay", "operationId": ""}
        editor = {"authoritative": True, "isStale": False}
        async def status(**_):
            return ok("status", {"playModeTransition": transition, "executionState": editor})
        service.mcp_status = status
        started = await service.seed_legacy_operation(spec(failOnUnexpectedPlayModeExit=True))
        operation_id = started.data["operationId"]
        transition["operationId"] = operation_id
        normal = await service.operation_status(operation_id)
        assert not normal.data["terminal"]
        transition["operationId"] = ""
        stopped = await service.operation_status(operation_id)
        assert stopped.data["failureSignature"] == "OperationUnexpectedPlayModeExit"
        assert stopped.data["terminal"]
    asyncio.run(run())


def test_stale_or_non_authoritative_transition_cannot_fail_running_operation(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Running"}] * 3)
        editor = {"authoritative": True, "isStale": True}
        async def status(**_):
            return ok("status", {"executionState": editor, "playModeTransition": {
                "at": 9999999999999, "fromState": "play", "toState": "exitingPlay",
                "operationId": "", "origin": "unknown",
            }})
        service.mcp_status = status
        started = await service.seed_legacy_operation(spec(failOnUnexpectedPlayModeExit=True))
        for changes in ({}, {"authoritative": False, "isStale": False}):
            editor.update(changes)
            result = await service.operation_status(started.data["operationId"])
            assert not result.data["terminal"]
            assert not result.data["playModeTransition"]
        editor.update(authoritative=True, isStale=False)
        result = await service.operation_status(started.data["operationId"])
        assert result.data["failureSignature"] == "OperationUnexpectedPlayModeExit"
    asyncio.run(run())


def test_project_switch_clears_previous_transition(tmp_path):
    from upilot_mcp.state_store import StateStore
    store = StateStore()
    assert store.playmode_transition == {}
    store.configure_project(str(tmp_path / "first"))
    store.playmode_transition = {"transitionId": "previous-project"}
    store.configure_project(str(tmp_path / "second"))
    assert store.playmode_transition == {}


def test_failed_business_with_verified_cleanup_keeps_first_error_and_terminal(tmp_path):
    """Isolated historical fixture only; never enables generic production Start."""
    import copy
    async def run():
        service = _OperationService(tmp_path, [{"status": "Failed", "phase": "BusinessFailed",
                                               "error": "original business error", "failureSignature": "OwnedBusinessError",
                                               "cleanupPending": False}])
        content = b"owned log\n"
        (tmp_path / "console.jsonl").write_bytes(content)
        session = dict(sessionId="owned-failed-capture", active=False, finishedAtUtcMs=1, segmentCount=1,
                       jsonlPath="console.jsonl", summaryPath="summary.json", fileBytes=len(content),
                       sha256=hashlib.sha256(content).hexdigest())
        (tmp_path / "summary.json").write_text(json.dumps(session))
        stops = []
        async def stop(**kwargs):
            assert kwargs == {"session_id": session["sessionId"], "owner_token": "owned-token"}
            stops.append(1)
            return ok("stop", {"session": session})
        async def editor(**_):
            return ok("editor", {"executionState": dict(ready=True, authoritative=True, isStale=False,
                        playModeState="edit", observedAt=9999999999999)})
        service.console_capture_stop = stop
        service.mcp_status = editor
        started = await service.seed_legacy_operation(spec(cleanup={"requireEditMode": True}))
        state = service._operations[started.data["operationId"]]
        state["consoleCapture"] = {"sessionId": session["sessionId"], "ownerToken": "owned-token"}
        result = await service.operation_status(state["operationId"])
        assert result.ok and result.data["terminal"] and result.data["status"] == "Failed"
        assert result.data["error"] == "original business error"
        assert result.data["failureSignature"] == "OwnedBusinessError"
        assert result.data["cleanupPending"] is False and result.data["editorVerification"] == "verified"
        assert state["consoleCapture"]["stopped"] and state["consoleCapture"]["artifactsVerified"]
        frozen = {key: copy.deepcopy(state.get(key)) for key in
                  ("status", "phase", "error", "failureSignature", "businessResult", "endedAt", "cleanupEndedAt")}
        for _ in range(2):
            late = await service.operation_status(state["operationId"])
            assert late.data["terminal"] and late.data["status"] == "Failed"
        assert {key: state.get(key) for key in frozen} == frozen
        assert len(stops) == 1 and [name for name, _ in service.calls] == ["start", "status"]
        persisted = service.server.state.load_operations()[0]
        # Historical storage freezes endedAt; public terminal is projected from it.
        assert persisted["endedAt"] == result.data["endedAt"] and persisted["status"] == "Failed"
        assert persisted["cleanupPending"] is False and persisted["editorVerification"] == "verified"
        assert persisted["consoleCapture"]["artifactsVerified"] is True
        assert persisted["error"] == "original business error"
    asyncio.run(run())


@pytest.mark.parametrize("gap", [
    "capture_owner_unknown", "capture_wrong_session", "capture_still_active",
    "capture_file_missing", "capture_summary_stale", "editor_stale",
    "editor_not_authoritative", "editor_old_sample", "editor_still_playing",
    "business_cleanup_missing", "business_cleanup_wrong_identity",
    "business_cleanup_read_failed",
])
def test_failed_business_with_unverified_cleanup_ends_once_without_claiming_success(tmp_path, monkeypatch, gap):
    """D16 negative matrix on isolated history, with real owned artifact files."""
    import copy
    from upilot_mcp.domain import task_service
    from test_persistent_operations import stop_observers

    async def run():
        business_gap = gap.startswith("business_cleanup_")
        business = {"status": "Failed", "phase": "BusinessFailed",
                    "error": "original business error", "failureSignature": "OwnedBusinessError",
                    "cleanupPending": business_gap}
        service = _OperationService(tmp_path, [business])
        started = await service.seed_legacy_operation(spec(cleanup={"requireEditMode": True, "timeoutSec": 1}))
        assert started.ok
        await stop_observers(service)
        state = service._operations[started.data["operationId"]]
        clock = [state["startedAt"] + 1]
        monkeypatch.setattr(task_service, "now_ms", lambda: clock[0])

        content = b"owned log\n"
        session = dict(sessionId="owned-negative-capture", active=False, finishedAtUtcMs=1, segmentCount=1,
                       jsonlPath="console.jsonl", summaryPath="summary.json", fileBytes=len(content),
                       sha256=hashlib.sha256(content).hexdigest())
        if gap != "capture_file_missing":
            (tmp_path / "console.jsonl").write_bytes(content)
        summary = {**session, "active": True} if gap == "capture_summary_stale" else session
        (tmp_path / "summary.json").write_text(json.dumps(summary), encoding="utf-8")
        response_session = dict(session)
        if gap == "capture_wrong_session":
            response_session["sessionId"] = "another-capture"
        elif gap == "capture_still_active":
            response_session["active"] = True
        state["consoleCapture"] = {"sessionId": session["sessionId"]}
        if gap != "capture_owner_unknown":
            state["consoleCapture"]["ownerToken"] = "owned-token"

        stops, observations, editor_reads = [], [], []
        async def stop(**kwargs):
            assert kwargs == {"session_id": session["sessionId"], "owner_token": "owned-token"}
            stops.append(1)
            return ok("stop", {"session": response_session})
        async def capture_status(**kwargs):
            assert kwargs == {"session_id": session["sessionId"]}
            observations.append(1)
            return ok("capture-status", {"session": response_session})
        async def editor(**_):
            editor_reads.append(1)
            sample = dict(ready=True, authoritative=True, isStale=False, playModeState="edit",
                          observedAt=clock[0] + 1)
            if gap == "editor_stale":
                sample["isStale"] = True
            elif gap == "editor_not_authoritative":
                sample["authoritative"] = False
            elif gap == "editor_old_sample":
                sample["observedAt"] = state["businessEndedAt"]
            elif gap == "editor_still_playing":
                sample.update(ready=False, playModeState="play")
            return ok("editor", {"executionState": sample})
        service.console_capture_stop = stop
        service.console_capture_status = capture_status
        service.mcp_status = editor

        if business_gap:
            followup = {"status": "Failed", "error": "later observation", "cleanupPending": False}
            if gap == "business_cleanup_missing":
                followup.pop("cleanupPending")
            elif gap == "business_cleanup_wrong_identity":
                # Reject a mismatched identity actually established by Start.
                # This tool fixture returns captureId, not a business operationId.
                assert state["startData"]["captureId"] == "capture-123"
                followup["captureId"] = "another-business-capture"
            service._statuses.append(followup)
            if gap == "business_cleanup_read_failed":
                dispatch = service._dispatch_tool
                async def failing_followup(name, args):
                    result = await dispatch(name, args)
                    if name == "status" and sum(call[0] == "status" for call in service.calls) > 1:
                        return fail("status", "READ_FAILED", "cleanup observation unavailable")
                    return result
                service._dispatch_tool = failing_followup

        try:
            first = await service.operation_status(state["operationId"], detail_level="full")
            assert first.ok and first.data["businessTerminal"]
            assert first.data["businessResult"] == {key: business[key] for key in
                                                    ("status", "phase", "error", "failureSignature")}
            deadline = state["cleanupDeadlineAt"]
            assert deadline == state["businessEndedAt"] + 1000
            if gap == "capture_owner_unknown":
                assert first.data["terminal"] and first.data["status"] == "aborted"
                assert first.data["phase"] == "capture_owner_token_unknown"
                assert stops == observations == editor_reads == []
            else:
                assert not first.data["terminal"] and first.data["cleanupPending"]
                unresolved = ("businessCleanup" if business_gap else "authoritativeEditMode"
                              if gap.startswith("editor_") else "consoleCapture:" + session["sessionId"])
                assert unresolved in first.data["unresolvedResources"]
                # Expire the original budget; do not renew it or wait in real time.
                clock[0] = deadline + 1
                final = await service.operation_status(state["operationId"], detail_level="full")
                assert final.ok and final.data["terminal"] and final.data["status"] == "timed_out"
                assert not final.data["cleanupPending"]
                assert final.data["cleanupFailureSignature"] == "OperationCleanupTimeout"
                assert final.data["cleanupError"] and final.data["cleanupError"] != business["error"]
                assert len(stops) == 1
            assert state["cleanupSucceeded"] is False
            assert state["error"] == business["error"] and state["failureSignature"] == business["failureSignature"]
            assert state["cleanupDeadlineAt"] == deadline
            keys = ("status", "phase", "error", "failureSignature", "businessResult", "endedAt",
                    "cleanupDeadlineAt", "cleanupError", "cleanupFailureSignature", "consoleCapture")
            frozen = {key: copy.deepcopy(state.get(key)) for key in keys}
            counts = (len(stops), len(observations), len(editor_reads), len(service.calls))
            for _ in range(2):
                late = await service.operation_status(state["operationId"], detail_level="full")
                assert late.ok and late.data["terminal"] and late.data["status"] == frozen["status"]
                assert "owned-token" not in json.dumps(late.data)
            assert {key: state.get(key) for key in keys} == frozen
            assert counts == (len(stops), len(observations), len(editor_reads), len(service.calls))
            assert [name for name, _ in service.calls].count("start") == 1
            assert [name for name, _ in service.calls].count("status") == (2 if business_gap else 1)
            persisted = service.server.state.load_operations()[0]
            for key in ("status", "error", "failureSignature", "businessResult", "endedAt", "cleanupDeadlineAt"):
                assert persisted[key] == state[key]
            assert persisted["cleanupSucceeded"] is False
        finally:
            await stop_observers(service)
    asyncio.run(run())
