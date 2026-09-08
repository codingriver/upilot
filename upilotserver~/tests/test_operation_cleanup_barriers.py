import asyncio
import hashlib
import json

from upilot_mcp.responses import ok, fail
from test_operation_runner_and_agent_rules import _OperationService


def spec(**extra):
    return {"startCall": {"kind": "tool", "toolName": "start"},
            "statusCall": {"kind": "tool", "toolName": "status"}, **extra}


def test_start_call_receives_its_own_operation_identity(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(spec(startCall={
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
        start = await service.operation_start(spec(cleanup={"requireEditMode": True}))
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
        start = await service.operation_start(spec(cleanup={"requireEditMode": True}))
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


def test_capture_failure_is_not_stopped_or_success(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{"status": "Succeeded"}])
        async def stop(**_):
            return fail("stop", "STOP_FAILED", "still active")
        service.console_capture_stop = stop
        start = await service.operation_start(spec())
        operation_id = start.data["operationId"]
        state = service._operations[operation_id]
        state["consoleCapture"] = {"sessionId": "capture"}
        result = await service.operation_status(operation_id)
        assert result.data["terminal"] is False
        assert state["consoleCapture"]["stopped"] is False
        state["cleanupDeadlineAt"] = 1
        result = await service.operation_status(operation_id)
        assert result.data["terminal"]
        assert result.data["status"] == "Failed"
        assert result.data["failureSignature"] == "OperationCleanupTimeout"
        assert result.data["businessResult"]["status"] == "Succeeded"
    asyncio.run(run())


def test_invalid_cleanup_rejected_without_start(tmp_path):
    service = _OperationService(tmp_path, [])
    for cleanup in ([], {"timeoutSec": 0}, {"timeoutSec": float("inf")}, {"requireEditMode": "yes"}):
        result = asyncio.run(service.operation_start(spec(cleanup=cleanup)))
        assert not result.ok
    assert not service.calls


def test_business_terminal_is_latched_while_project_cleanup_is_pending(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [
            {"status": "Succeeded", "cleanupPending": True},
            {"status": "Succeeded", "cleanupPending": False},
        ])
        start = await service.operation_start(spec())
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
        (tmp_path / "console.jsonl").write_bytes(content)
        session = dict(sessionId="capture", active=False, finishedAtUtcMs=1,
                       jsonlPath="console.jsonl", summaryPath="summary.json",
                       fileBytes=len(content), sha256="wrong", segmentCount=1)
        (tmp_path / "summary.json").write_text(json.dumps(session))
        async def stop(**_):
            return ok("stop", {"session": session})
        service.console_capture_stop = stop
        start = await service.operation_start(spec())
        state = service._operations[start.data["operationId"]]
        state["consoleCapture"] = {"sessionId": "capture"}
        first = await service.operation_status(start.data["operationId"])
        assert not first.data["terminal"]
        assert state["consoleCapture"]["artifactsVerified"] is False
        session["sha256"] = hashlib.sha256(content).hexdigest()
        (tmp_path / "summary.json").write_text(json.dumps(session))
        last = await service.operation_status(start.data["operationId"])
        assert last.data["terminal"]
        assert state["consoleCapture"]["artifactsVerified"] is True
    asyncio.run(run())


def test_cancel_terminal_latches_business_result_before_project_cleanup(tmp_path):
    async def run():
        service = _OperationService(
            tmp_path, [{"status": "Canceled", "cleanupPending": False}],
            [ok("cancel", {"status": "Canceled", "cleanupPending": True})],
        )
        start = await service.operation_start(spec(cancelCall={"kind": "tool", "toolName": "cancel"}))
        operation_id = start.data["operationId"]
        canceled = await service.operation_cancel(operation_id)
        assert canceled.data["businessTerminal"] and not canceled.data["terminal"]
        assert canceled.data["status"] == "CleaningUp"
        assert canceled.data["businessResult"]["status"] == "Canceled"
        final = await service.operation_cancel(operation_id)
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
        start = await service.operation_start(spec())
        state = service._operations[start.data["operationId"]]
        state["consoleCapture"] = {"sessionId": "rotated"}
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
        state = {"consoleCapture": {"sessionId": "capture"}}
        stopping = asyncio.create_task(service._stop_owned_operation_capture(state))
        await entered.wait()
        assert state["consoleCapture"]["stopped"] is False
        stopping.cancel()
        try:
            await stopping
        except asyncio.CancelledError:
            pass
        assert state["consoleCapture"]["stopped"] is False
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
        started = await service.operation_start(spec(failOnUnexpectedPlayModeExit=True))
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
        started = await service.operation_start(spec(failOnUnexpectedPlayModeExit=True))
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
