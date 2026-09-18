import asyncio
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
