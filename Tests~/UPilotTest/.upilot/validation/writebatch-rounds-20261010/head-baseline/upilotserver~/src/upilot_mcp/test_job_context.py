"""Internal context shared by the existing test and task services."""
import asyncio
from contextvars import ContextVar

from .protocol import now_ms


class TestJobCancelledBeforeStart(Exception):
    pass


TEST_JOB_CONTEXT = ContextVar("upilot_test_job", default=None)


def checkpoint(phase: str = "", **fields) -> None:
    context = TEST_JOB_CONTEXT.get()
    if context is None:
        return
    state, persist = context
    if state.get("terminal") or state.get("endedAt"):
        raise asyncio.CancelledError("The test task has already ended; late work is discarded.")
    if phase != "finalized" and state.get("cancelRequested") and not state.get("startIntentSent") and not state.get("runGuid"):
        raise TestJobCancelledBeforeStart("Cancelled before test start.")
    state.update(fields)
    if phase:
        state["phase"] = phase
    state["updatedAt"] = now_ms()
    persist(state)
