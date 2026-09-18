import importlib.util
from pathlib import Path

import pytest


spec = importlib.util.spec_from_file_location(
    "deferred_evidence", Path(__file__).resolve().parents[1] / "scripts" / "p1_compile_deferred_evidence.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


def scenario():
    baseline = dict(playModeState="play", authoritative=True, isStale=False,
                    compilePhase="completed", compileRequestId="old")
    registered = dict(writeBatchId="batch", operationId="op", filesSha256="digest",
                      writeBatchCreatedAt=100, status="deferred", terminal=False, executionState=baseline)
    held = dict(registered, correlationVerified=False, errorsVerified=False,
                outcome="unknown", terminalSnapshot=None)
    play = dict(baseline, pendingWriteBatchId="batch", compileDeferredReason="PlayMode",
                correlationVerified=False, isCompiling=True, terminal=False, errorsVerified=False)
    terminal = dict(registered, status="verified", terminal=True, errorsVerified=True,
                    correlationVerified=True, outcome="passed", lastCompileVerifiedAt=7000,
                    compileRequestId="new")
    final = dict(baseline, ready=True, playModeState="edit", writeBatchId="batch",
                 pendingWriteBatchId="", correlationVerified=True)
    steps = {name: {"data": data} for name, data in {
        "prewrite-play-baseline": {"executionState": baseline}, "register": registered,
        "held-batch": held, "held-play": {"executionState": play},
        "play-stop": {"confirmed": True, "playModeState": "edit"},
        "terminal": terminal, "final-status": {"executionState": final},
        "final-errors": {"total": 0}, "capture-stop": {"session": {"active": False}},
    }.items()}
    steps["held-play"]["timestamp"] = 6000
    return steps, [">>> playmode.set start", ">>> playmode.set stop", ">>> compile.request request"]


def test_deferred_compile_accepts_only_independent_terminal_after_editmode():
    steps, lines = scenario()
    assert all(evidence.checks(steps, lines, True).values())


@pytest.mark.parametrize("fault", [
    "lost_pending", "inferred_phase", "reused_terminal", "wrong_digest",
    "too_short", "early_compile", "duplicate_compile", "source_changed", "capture_leak",
])
def test_deferred_compile_rejects_incomplete_or_contradictory_evidence(fault):
    steps, lines = scenario()
    if fault == "lost_pending":
        steps["held-play"]["data"]["executionState"]["pendingWriteBatchId"] = ""
    elif fault == "inferred_phase":
        steps["held-play"]["data"]["executionState"]["compilePhase"] = "compiling"
    elif fault == "reused_terminal":
        steps["terminal"]["data"]["compileRequestId"] = "old"
    elif fault == "wrong_digest":
        steps["terminal"]["data"]["filesSha256"] = "other"
    elif fault == "too_short":
        steps["held-play"]["timestamp"] = 200
    elif fault == "early_compile":
        lines[1], lines[2] = lines[2], lines[1]
    elif fault == "duplicate_compile":
        lines.append(">>> compile.request duplicate")
    elif fault == "capture_leak":
        steps["capture-stop"]["data"]["session"]["active"] = True
    assert not all(evidence.checks(steps, lines, fault != "source_changed").values())
