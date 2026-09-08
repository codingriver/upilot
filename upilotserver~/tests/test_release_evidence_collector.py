import asyncio
import copy
import importlib.util
from pathlib import Path

import pytest

SPEC = importlib.util.spec_from_file_location(
    "collect_release_evidence", Path(__file__).resolve().parents[1] / "scripts/collect_release_evidence.py")
collector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(collector)


def test_completed_observation_never_sends_cancel():
    async def run():
        calls, retained = [], []
        async def call(name, args):
            calls.append((name, args))
            return {"taskId": "original", "terminal": True, "status": "completed"}
        result = await collector.observe_acceptance(
            call, {"taskId": "original"}, retained.append, poll_interval_sec=0)
        assert result["status"] == "completed"
        assert [name for name, _ in calls] == ["unity_task_status"]
    asyncio.run(run())


@pytest.mark.parametrize("cleanup_fails", (False, True))
def test_observation_failure_retains_original_id_and_never_replays_start(cleanup_fails):
    async def run():
        calls, retained = [], []
        async def call(name, args):
            calls.append((name, args))
            if len(calls) == 1:
                raise RuntimeError("connection interrupted")
            if cleanup_fails:
                raise RuntimeError("cleanup unavailable")
            return {"taskId": "original", "terminal": True, "status": "canceled"}
        with pytest.raises(RuntimeError, match="connection interrupted"):
            await collector.observe_acceptance(
                call, {"taskId": "original", "runGuid": "run"}, lambda v: retained.append(copy.deepcopy(v)),
                poll_interval_sec=0)
        assert [name for name, _ in calls].count("unity_task_cancel") == 1
        assert all(args["taskId"] == "original" for _, args in calls)
        assert "unity_task_start" not in [name for name, _ in calls]
        assert retained[-1]["observationError"] == "connection interrupted"
        assert retained[-1]["cleanupVerified"] is False
        if cleanup_fails:
            assert retained[-1]["cleanupError"] == "cleanup unavailable"
        else:
            assert retained[-1]["cleanupObservedTerminal"]
    asyncio.run(run())


def test_observation_deadline_requests_cleanup_without_restart():
    async def run():
        calls = []
        async def call(name, args):
            calls.append(name)
            return {"taskId": "original", "terminal": True, "status": "canceled"}
        with pytest.raises(TimeoutError):
            await collector.observe_acceptance(
                call, {"taskId": "original"}, lambda _: None, timeout_sec=0, poll_interval_sec=0)
        assert calls == ["unity_task_cancel", "unity_task_status"]
    asyncio.run(run())
