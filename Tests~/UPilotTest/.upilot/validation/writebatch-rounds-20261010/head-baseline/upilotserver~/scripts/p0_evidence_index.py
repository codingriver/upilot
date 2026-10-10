"""Index immutable P0/P1 evidence; never promotes an expected failure to acceptance."""
import hashlib
import json
from pathlib import Path
from uuid import UUID


RESULT_RELOAD_PREFIX = "CodingRiver.UPilot.Tests.UPilotRunnerReloadAcceptance."


def result_reload_checks(report, witness):
    test = ((report.get("steps") or {}).get("testStatus") or {}).get("data") or {}
    saved = witness.get("persistedResult") or {}
    before = witness.get("beforeReload") or {}
    names = report.get("testNames") or []
    expected_failure = names == [RESULT_RELOAD_PREFIX + "FailImmediatelyBeforeReload"]
    expected_pass = names == [RESULT_RELOAD_PREFIX + "PassImmediatelyBeforeReload"]
    outcome = "failed" if expected_failure else "completed" if expected_pass else None
    leaves = test.get("results") or []
    identity = report.get("runGuid")
    observed = witness.get("resultObservedAt", 0)
    requested = witness.get("reloadRequestedAt", 0)
    reloaded = witness.get("beforeReloadAt", 0)
    return {
        "exactIdentity": bool(identity) and all(value.get("runGuid") == identity
            for value in (test, saved, before, witness)),
        "declaredScenario": (expected_failure or expected_pass) and witness.get("testName") == names[0],
        "authoritativeOutcome": outcome is not None and all(
            value.get("resultAuthoritative") is True and value.get("outcomeStatus") == outcome
            for value in (saved, before, test)),
        "resultDetailsPreserved": len(leaves) == 1 and leaves == saved.get("results") == before.get("results"),
        "exactTestConclusion": (expected_failure or expected_pass) and len(leaves) == 1
            and leaves[0].get("testName") == names[0]
            and leaves[0].get("testStatus") == ("Failed" if expected_failure else "Passed")
            and all(value.get("total") == 1 and value.get("failed") == int(expected_failure)
                    and value.get("passed") == int(expected_pass) for value in (saved, before, test)),
        "expectedFailurePreserved": not expected_failure or (
            len(leaves) == 1 and leaves[0].get("message") == "P0_EXPECTED_IMMEDIATE_RESULT_RELOAD_FAILURE"
            and bool(leaves[0].get("stackTrace")) and leaves[0].get("testStatus") == "Failed"),
        "pendingAtReload": all(value.get("cleanupPending") is True
            and value.get("cleanupSucceeded") is False for value in (saved, before)),
        "newDomain": bool(witness.get("domain")) and bool(test.get("callbackDomain"))
            and test["callbackDomain"] != witness["domain"],
        "orderedBoundary": 0 < observed <= requested <= reloaded <= test.get("endedAt", 0),
        "callbacksPreserved": witness.get("resultCallbacks") == 1
            and witness.get("duplicateCallbacks") == 2 and witness.get("foreignCallbacks") == 2
            and witness.get("callbacksPreservedResult") is True and witness.get("observerReleased") is True
            and not witness.get("error"),
        "cleanupProven": report.get("cleanupVerified") is True and test.get("cleanupSucceeded") is True
            and test.get("cleanupPending") is False and test.get("isRunning") is False
            and test.get("runnerState") == "inactive" and test.get("unresolvedResources") == []
            and test.get("cleanupErrors") == [] and test.get("status") == outcome,
        "sourceAndSelectionVerified": report.get("sourceUnchanged") is True
            and report.get("testIdentityVerified") is True and bool(report.get("sourceIdentity"))
            and report["sourceIdentity"] == report.get("sourceIdentityAfter"),
        "originalVerdictRetained": report.get("acceptancePassed") is expected_pass,
    }


def metadata(path, root):
    content = path.read_bytes()
    return {"path": path.relative_to(root).as_posix(), "bytes": len(content),
            "sha256": hashlib.sha256(content).hexdigest()}


def main():
    root = Path(__file__).resolve().parents[2]
    records = []
    embedded_captures = []
    for name in ("UPilotTest", "UPilotTest2022"):
        project = root / "Tests~" / name
        directory = project / "Log" / "P0P1"
        for path in sorted(directory.glob("*.json")):
            envelope = json.loads(path.read_text(encoding="utf-8-sig"))
            data = envelope.get("data") or (envelope.get("error") or {}).get("detail") or {}
            if not isinstance(data, dict):
                continue
            record = {"label": name + "/" + path.stem, **metadata(path, root),
                      "requestId": envelope.get("requestId"), "ok": envelope.get("ok"),
                      "errorCode": (envelope.get("error") or {}).get("code")}
            nested = (data.get("result") or {}).get("result") if isinstance(data.get("result"), dict) else None
            report = nested if isinstance(nested, dict) and "acceptancePassed" in nested else data
            if report.get("unbatchedTerminal"):
                from p1_compile_empty_identity import identity_checks
                checks = identity_checks(report)
                record["emptyCompileIdentity"] = {
                    "verified": all(checks.values()) and not any(report.get(key)
                        for key in ("error", "restoreError", "captureError")),
                    "checks": checks,
                    "compileOperationIds": [(report.get(key) or {}).get("compileOperationId")
                                           for key in ("unbatchedTerminal", "restoreTerminal")],
                }
                capture = (((report.get("steps") or {}).get("captureStop") or {}).get("response") or {}).get("data") or {}
                if (capture.get("session") or {}).get("active") is False:
                    embedded_captures.append((project, capture["session"]))
            if report.get("registeredA"):
                record["compileOverlap"] = {
                    "checks": report.get("checks", {}),
                    "compileRequestCount": report.get("compileRequestCount"),
                    "captureStopped": report.get("captureStopped", False),
                    "scriptSha256": report.get("scriptSha256"),
                    "error": report.get("error"),
                    "batches": [
                        {field: (report.get(key) or {}).get(field) for field in (
                            "writeBatchId", "operationId", "compileRequestId", "compileOperationId",
                            "filesSha256", "lastCompileVerifiedAt", "outcome", "correlationVerified")}
                        for key in ("finalA", "finalB")
                    ],
                }
                for call in report.get("calls", []):
                    if call.get("tool") == "unity_console_capture_stop":
                        capture = ((call.get("response") or {}).get("data") or {}).get("session") or {}
                        if capture.get("active") is False:
                            embedded_captures.append((project, capture))
            for field in ("runGuid", "taskId", "operationId", "writeBatchId", "outcome", "terminal",
                          "acceptancePassed", "cleanupVerified", "sourceUnchanged", "sourceIdentity",
                          "sourceIdentityAfter", "testIdentityVerified", "status", "phase", "artifact"):
                if field in report:
                    record[field] = report[field]
            test = ((report.get("steps") or {}).get("testStatus") or {}).get("data") or data
            for field in ("runGuid", "total", "passed", "failed", "cleanupSucceeded", "cleanupPending",
                          "resultAuthoritative", "runnerState", "cancelAttemptCount", "callbackDomain", "unresolvedResources"):
                if field in test:
                    record[field] = test[field]
            if test.get("stopRequestedAt") and test.get("endedAt"):
                record["cancelCleanupMs"] = test["endedAt"] - test["stopRequestedAt"]
            artifact = report.get("artifact") or {}
            if artifact.get("path"):
                target = Path(artifact["path"]).resolve()
                if not target.is_relative_to(project.resolve()):
                    raise ValueError("Evidence escaped its project.")
                actual = metadata(target, root)
                record["artifactVerified"] = actual["bytes"] == artifact["bytes"] and actual["sha256"] == artifact["sha256"]
                if not record["artifactVerified"]:
                    raise ValueError(f"Artifact hash mismatch: {target}")
            names = report.get("testNames") or []
            if len(names) == 1 and names[0] in (
                    RESULT_RELOAD_PREFIX + "PassImmediatelyBeforeReload",
                    RESULT_RELOAD_PREFIX + "FailImmediatelyBeforeReload"):
                try:
                    run_guid = str(UUID(report.get("runGuid") or ""))
                except ValueError:
                    run_guid = None
                witness_path = directory / "ResultReload" / run_guid / "before-reload.json" if run_guid else None
                witness = json.loads(witness_path.read_text(encoding="utf-8-sig")) if witness_path and witness_path.exists() else {}
                checks = result_reload_checks(report, witness)
                record["resultReloadScenario"] = {
                    "verified": bool(record.get("artifactVerified")) and all(checks.values()),
                    "checks": checks,
                    "resultToReloadMs": witness.get("beforeReloadAt", 0) - witness.get("resultObservedAt", 0),
                    "witness": metadata(witness_path, root) if witness else None,
                }
            records.append(record)
    artifacts = []
    for project, session in embedded_captures:
        for field in ("jsonlPath", "manifestPath", "summaryPath"):
            if session.get(field):
                target = Path(session[field]).resolve()
                if not target.is_relative_to(project.resolve()):
                    raise ValueError("Embedded Console evidence escaped its project.")
                artifacts.append(metadata(target, root))
    for path in sorted((root / "Tests~" / "UPilotTest" / "Log" / "P0P1").glob("*.xml")):
        artifacts.append(metadata(path, root))
    for path in sorted((root / "Tests~" / "UPilotTest" / "Log" / "UPilotDiagnostics").glob("P0-*.dmp")):
        artifacts.append(metadata(path, root))
    for name in ("UPilotTest", "UPilotTest2022"):
        project = root / "Tests~" / name
        for folder in ("PersistenceStress", "PersistenceCrash", "SnapshotBridge", "LongReload", "CancelReload", "ResultReload"):
            directory = project / "Log" / "P0P1" / folder
            for path in sorted(directory.rglob("*")):
                if path.is_file():
                    artifacts.append(metadata(path, root))
        for path in sorted((project / "Log" / "P0P1").glob("*capture-stop*.json")):
            envelope = json.loads(path.read_text(encoding="utf-8-sig"))
            session = (envelope.get("data") or {}).get("session") or {}
            if session.get("active") is not False:
                continue
            for field in ("jsonlPath", "manifestPath", "summaryPath"):
                if not session.get(field):
                    continue
                target = Path(session[field]).resolve()
                if not target.is_relative_to(project.resolve()):
                    raise ValueError("Console capture evidence escaped its project.")
                artifacts.append(metadata(target, root))
    output = root / "Documentation~" / "P0P1-Evidence-20260912.json"
    output.write_text(json.dumps({"schemaVersion": 1, "overallAcceptancePassed": False,
        "note": "Mixed checkpoints. Unknown and expected failure evidence is not blanket P0/P1 acceptance.",
        "records": records, "artifacts": artifacts}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"records": len(records), "artifacts": len(artifacts), **metadata(output, root)}))


if __name__ == "__main__":
    main()
