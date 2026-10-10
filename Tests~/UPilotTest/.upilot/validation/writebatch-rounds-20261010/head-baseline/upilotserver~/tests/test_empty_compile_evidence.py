import importlib.util
from pathlib import Path

import pytest


spec = importlib.util.spec_from_file_location(
    "empty_identity", Path(__file__).resolve().parents[1] / "scripts" / "p1_compile_empty_identity.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


def scenario():
    report = {
        "steps": {"baseline": {"response": {"timestamp": 100}}},
        "oldBatchUnchanged": True, "serverLogDelta": "",
        "definesRestored": True, "settingsSha256Before": "hash", "settingsSha256After": "hash",
        "sourceIdentity": {"hash": "source"}, "sourceIdentityAfter": {"hash": "source"},
        "captureStopped": True, "lifecycleSnapshots": [],
    }
    for key in ("errorsAfterAdd", "finalErrors"):
        report["steps"][key] = {"response": {"data": {"total": 0}}}
    for key, operation in (("unbatchedTerminal", "a"), ("restoreTerminal", "b")):
        report[key] = dict(compileOperationId=operation, writeBatchId="", writeBatchCreatedAt=0,
                          compileRequestId="", compileOrigin="unity_auto", correlationVerified=False,
                          terminal=True, errorsVerified=True, compilePhase="completed",
                          lastCompileStartedAt=200, lastCompileVerifiedAt=300)
        for transition in ("compile_started", "compiler_finished", "domain_reload_starting",
                           "domain_reload_recovered", "compile_completed"):
            report["lifecycleSnapshots"].append(dict(compileOperationId=operation, transition=transition,
                lastCompilerFinishedAt=0, lastCompileVerifiedAt=0, lastTerminalCompileAt=0,
                lastCompileRequestedAt=0, terminal=False, errorsVerified=False))
    return report


def test_empty_identity_accepts_complete_fresh_evidence():
    assert all(evidence.identity_checks(scenario()).values())


@pytest.mark.parametrize("fault", [
    "old_verified_at", "old_finished_at", "old_terminal_at", "missing_start",
    "missing_reload", "old_batch", "manual_compile", "unrestored_settings", "compile_error",
])
def test_empty_identity_rejects_missing_or_inherited_evidence(fault):
    report = scenario()
    if fault == "old_verified_at":
        report["lifecycleSnapshots"][0]["lastCompileVerifiedAt"] = 50
    elif fault == "old_finished_at":
        report["lifecycleSnapshots"][0]["lastCompilerFinishedAt"] = 50
    elif fault == "old_terminal_at":
        report["lifecycleSnapshots"][0]["lastTerminalCompileAt"] = 50
    elif fault == "missing_start":
        report["lifecycleSnapshots"].pop(0)
    elif fault == "missing_reload":
        report["lifecycleSnapshots"].pop(2)
    elif fault == "old_batch":
        report["unbatchedTerminal"]["writeBatchId"] = "old"
    elif fault == "manual_compile":
        report["serverLogDelta"] = ">>> compile.request new"
    elif fault == "unrestored_settings":
        report["settingsSha256After"] = "other"
    elif fault == "compile_error":
        report["steps"]["finalErrors"]["response"]["data"]["total"] = 1
    assert not all(evidence.identity_checks(report).values())
