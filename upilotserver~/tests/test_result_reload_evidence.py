import copy
import importlib.util
from pathlib import Path

import pytest


spec = importlib.util.spec_from_file_location(
    "p0_evidence_index", Path(__file__).resolve().parents[1] / "scripts" / "p0_evidence_index.py")
index = importlib.util.module_from_spec(spec)
spec.loader.exec_module(index)

call_spec = importlib.util.spec_from_file_location(
    "p0_acceptance_call", Path(__file__).resolve().parents[1] / "scripts" / "p0_acceptance_call.py")
call = importlib.util.module_from_spec(call_spec)
call_spec.loader.exec_module(call)


def evidence():
    name = index.RESULT_RELOAD_PREFIX + "FailImmediatelyBeforeReload"
    saved = {
        "runGuid": "original", "resultAuthoritative": True, "outcomeStatus": "failed",
        "total": 1, "passed": 0, "failed": 1,
        "cleanupPending": True, "cleanupSucceeded": False,
        "results": [{"testName": name, "testStatus": "Failed",
                     "message": "P0_EXPECTED_IMMEDIATE_RESULT_RELOAD_FAILURE", "stackTrace": "original stack"}],
    }
    final = dict(copy.deepcopy(saved), cleanupPending=False, cleanupSucceeded=True,
                 callbackDomain="new", endedAt=30, isRunning=False, runnerState="inactive",
                 unresolvedResources=[], cleanupErrors=[], status="failed")
    report = {
        "runGuid": "original", "testNames": [name], "steps": {"testStatus": {"data": final}},
        "cleanupVerified": True, "sourceUnchanged": True, "testIdentityVerified": True,
        "sourceIdentity": {"sourceSha256": "actual"}, "sourceIdentityAfter": {"sourceSha256": "actual"},
        "acceptancePassed": False,
    }
    witness = {
        "runGuid": "original", "testName": name, "persistedResult": saved,
        "beforeReload": copy.deepcopy(saved), "domain": "old", "resultObservedAt": 10,
        "reloadRequestedAt": 11, "beforeReloadAt": 12, "resultCallbacks": 1,
        "duplicateCallbacks": 2, "foreignCallbacks": 2, "callbacksPreservedResult": True,
        "observerReleased": True, "error": "",
    }
    return report, witness


def test_expected_failure_is_verified_without_promoting_test_verdict():
    report, witness = evidence()
    assert all(index.result_reload_checks(report, witness).values())
    assert report["acceptancePassed"] is False


def test_success_requires_the_declared_passing_leaf():
    report, witness = evidence()
    name = index.RESULT_RELOAD_PREFIX + "PassImmediatelyBeforeReload"
    report.update(testNames=[name], acceptancePassed=True)
    witness["testName"] = name
    for value in (report["steps"]["testStatus"]["data"], witness["persistedResult"], witness["beforeReload"]):
        value.update(outcomeStatus="completed", passed=1, failed=0)
        value["results"] = [{"testName": name, "testStatus": "Passed", "message": "", "stackTrace": ""}]
    report["steps"]["testStatus"]["data"]["status"] = "completed"
    assert all(index.result_reload_checks(report, witness).values())


def test_missing_run_evidence_remains_unverified():
    assert not all(index.result_reload_checks({}, {}).values())


def test_reload_observation_is_exclusive_and_retains_not_sent_state(tmp_path: Path):
    output = tmp_path / "reload-observation.json"
    observation = {
        "schemaVersion": 1,
        "observationId": "observation-fixture",
        "transport": {"sendState": "not_sent", "stage": "connection_preflight"},
    }

    first_path, first_bytes, first_hash = call._write_observation(output, observation)
    second_path, second_bytes, second_hash = call._write_observation(output, observation)

    assert first_path == output
    assert second_path != first_path and second_path.name == "reload-observation-observation-fixture.json"
    assert first_bytes == second_bytes > 0 and first_hash == second_hash
    assert call.json.loads(first_path.read_text(encoding="utf-8"))["transport"]["sendState"] == "not_sent"


@pytest.mark.parametrize("raw", ["{not-json", "[]", '"not-an-object"'])
def test_invalid_cli_arguments_are_rejected_inside_the_observation_scope(raw):
    with pytest.raises((ValueError, call.json.JSONDecodeError)):
        call._decode_arguments(raw)


@pytest.mark.parametrize("change", [
    "foreign_run", "lost_detail", "unknown", "already_clean", "same_domain",
    "extra_callback", "observer_leak", "cleanup_failure", "source_changed", "promoted_verdict",
    "wrong_leaf", "wrong_count",
])
def test_missing_or_contradictory_evidence_never_passes(change):
    report, witness = evidence()
    final = report["steps"]["testStatus"]["data"]
    if change == "foreign_run":
        witness["runGuid"] = "other"
    elif change == "lost_detail":
        final["results"][0]["stackTrace"] = ""
    elif change == "unknown":
        final["resultAuthoritative"] = False
    elif change == "already_clean":
        witness["beforeReload"]["cleanupPending"] = False
    elif change == "same_domain":
        final["callbackDomain"] = "old"
    elif change == "extra_callback":
        witness["resultCallbacks"] = 2
    elif change == "observer_leak":
        witness["observerReleased"] = False
    elif change == "cleanup_failure":
        final["cleanupErrors"] = ["callback still registered"]
    elif change == "source_changed":
        report["sourceIdentityAfter"] = {"sourceSha256": "changed"}
    elif change == "promoted_verdict":
        report["acceptancePassed"] = True
    elif change == "wrong_leaf":
        for value in (final, witness["persistedResult"], witness["beforeReload"]):
            value["results"][0]["testName"] = "another.test"
    elif change == "wrong_count":
        final["passed"] = 1
    assert not all(index.result_reload_checks(report, witness).values())
