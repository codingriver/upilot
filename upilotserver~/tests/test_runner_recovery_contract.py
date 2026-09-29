import asyncio

import pytest
from types import SimpleNamespace

from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import ok


def test_recovery_required_does_not_turn_into_timeout_cancel():
    service = TestDomainService()
    calls = []

    async def result(run_guid):
        return ok("req", {"runGuid": run_guid, "status": "running",
                          "phase": "recovery_required", "runnerState": "unknown",
                          "resultAuthoritative": False})

    async def cancel(**kwargs):
        calls.append(kwargs)
        raise AssertionError("Recovery uncertainty must not cancel business work.")

    service.test_results = result
    service.test_cancel = cancel
    response = asyncio.run(service._wait_for_test_result("original-run", 1))
    assert not response.ok
    assert response.error.code == "TEST_RECOVERY_REQUIRED"
    assert response.error.detail["runGuid"] == "original-run"
    assert calls == []


def test_incremental_cursor_rejects_stream_rebinding_after_reload():
    class Dispatcher:
        def __init__(self):
            self.calls = 0

        async def call(self, request_id, name, payload, **_kwargs):
            assert name == "test.results"
            self.calls += 1
            if self.calls == 1:
                assert payload["afterEventSequence"] == 0
                return ok(request_id, {
                    "runGuid": "original-run", "resultStreamVersion": 1,
                    "lastDeliveredEventSequence": 2,
                    "events": [{"sequence": 1}, {"sequence": 2}],
                })
            assert payload["expectedResultStreamVersion"] == 1
            return ok(request_id, {
                "runGuid": "original-run", "resultStreamVersion": 2,
                "lastDeliveredEventSequence": 3, "events": [{"sequence": 3}],
            })

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=SimpleNamespace(_project_path="C:/CanonicalProject"))
    initial = asyncio.run(service.test_results("original-run", cursor="begin", count=10))
    rebound = asyncio.run(service.test_results("original-run", cursor=initial.data["nextCursor"], count=10))

    assert initial.ok
    assert not rebound.ok
    assert rebound.error.code == "TEST_RESULT_CURSOR_STREAM_MISMATCH"
    assert "nextCursor" not in rebound.error.detail


def test_incremental_cursor_rejects_non_contiguous_events_instead_of_skipping_them():
    class Dispatcher:
        async def call(self, request_id, _name, _payload, **_kwargs):
            return ok(request_id, {
                "runGuid": "original-run", "resultStreamVersion": 1,
                "lastDeliveredEventSequence": 2, "events": [{"sequence": 2}],
            })

    service = TestDomainService()
    service.dispatcher = Dispatcher()
    service.server = SimpleNamespace(state=SimpleNamespace(_project_path="C:/CanonicalProject"))
    result = asyncio.run(service.test_results("original-run", cursor="begin", count=10))

    assert not result.ok
    assert result.error.code == "TEST_RESULT_CURSOR_INVALID_RESPONSE"


def cleaned_result():
    return dict(runGuid="original-run", status="failed", outcomeStatus="failed", phase="failed",
                resultAuthoritative=True, cleanupPending=False, cleanupSucceeded=True,
                cleanupStatus="completed", cleanupErrors=[], unresolvedResources=[],
                cleanupErrorHistory=["api-release: resolved original failure"],
                cleanupAttemptDeadlineAt=1)


@pytest.mark.parametrize("patch", [None, {"cleanupErrors": ["persistence: pending"]},
    {"cleanupStatus": "committing"}, {"unresolvedResources": ["test-callback"]},
    {"cleanupPending": True}, {"cleanupSucceeded": None}, {"resultAuthoritative": False},
    {"persistenceError": "pointer unreadable"}, {"runGuid": "other-run"}])
def test_terminal_requires_current_cleanup_evidence_not_empty_history(patch):
    service = TestDomainService()
    data = cleaned_result()
    if patch:
        data.update(patch)
    async def result(run_guid):
        return ok("req", data)
    async def cancel(**kwargs):
        raise AssertionError("A terminal observation must never reissue cancellation")
    service.test_results = result
    service.test_cancel = cancel
    response = asyncio.run(service._wait_for_test_result("original-run", 1))
    assert response.ok is (patch is None)
    if patch:
        assert response.error.code == "TEST_RECOVERY_REQUIRED"
    else:
        assert response.data["status"] == "failed"
        assert response.data["cleanupErrorHistory"]


def test_missing_cleanup_fields_are_not_success():
    service = TestDomainService()
    async def result(run_guid):
        return ok("req", dict(runGuid=run_guid, status="completed", resultAuthoritative=True))
    async def cancel(**kwargs):
        raise AssertionError("Missing evidence must not trigger cancel")
    service.test_results = result
    service.test_cancel = cancel
    response = asyncio.run(service._wait_for_test_result("original-run", 1))
    assert not response.ok and response.error.code == "TEST_RECOVERY_REQUIRED"


def test_partial_commit_at_deadline_does_not_cancel_completed_runner():
    service = TestDomainService()
    async def result(run_guid):
        return ok("req", dict(cleaned_result(), status="cleanup", phase="cleanup",
                              cleanupStatus="committing", cleanupPending=True, cleanupSucceeded=False))
    async def cancel(**kwargs):
        raise AssertionError("Commit waiting is not a running Runner")
    service.test_results = result
    service.test_cancel = cancel
    response = asyncio.run(service._wait_for_test_result("original-run", 1))
    assert not response.ok and response.error.code == "TEST_RECOVERY_REQUIRED"
