"""V05 opt-in native acceptance Task disconnect evidence; no cancel, restart or replay."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import time
import uuid

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p0_reload_evidence import PROJECT, owned_path, require_status
from p1_request_origin_evidence import call, check_task, connect

LEAF = "CodingRiver.UPilot.Tests.UPilotRunnerLeafAcceptanceFixtures.DelayedLeaf"
SELECTORS = {"testMode": "EditMode", "testNames": [LEAF], "assemblies": ["UPilot.Editor.Tests"],
             "matchMode": "intersection", "requireAllSelectorsMatch": True}
FROZEN = ("taskId", "projectPath", "durable", "startedAt", "deadlineAt", "requestOrigin")
A = {"clientId": "owned-v05-A", "chatId": "owned-v05-start"}
B = {"clientId": "owned-v05-B", "chatId": "owned-v05-observer"}


def check_identity(data, baseline):
    # A live Runner is non-ready: compare fresh identities, not idle readiness.
    session, state = data.get("session") or {}, data.get("executionState") or {}
    server = (data.get("runtimeIdentity") or {}).get("mcpServer") or {}
    loaded = server.get("loadedSource") or {}
    actual = (data.get("paths") or {}).get("unityProjectAbsolute")
    identity = {"unityPid": session.get("processId"), "unityCreatedAt": session.get("processCreatedAt"),
                "serverPid": server.get("processId"), "bridgeSession": session.get("sessionId"),
                "producerEpoch": state.get("producerEpoch"), "domainGeneration": state.get("domainGeneration"),
                "loadedScope": loaded.get("scope")}
    if (not actual or Path(actual).resolve() != PROJECT or data.get("connected") is not True or
            data.get("serverReady") is not True or session.get("identityVerified") is not True or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            loaded.get("status") != "equivalent" or identity != baseline):
        raise CallPreflightError("Exact fresh project/service/domain identity changed.")
    return identity


def check_frozen(data, baseline):
    check_task(data, baseline["taskId"], A)
    if any(data.get(field) != baseline.get(field) for field in FROZEN):
        raise CallPreflightError("Original Task identity, origin or fixed deadline changed.")
    run = data.get("runGuid")
    if baseline.get("runGuid") and run != baseline["runGuid"]:
        raise CallPreflightError("Established original runGuid changed or disappeared.")
    if run:
        if not isinstance(run, str):
            raise CallPreflightError("Original runGuid is invalid.")
        baseline["runGuid"] = run  # freeze only after the native queued receipt establishes it


def check_terminal(data, baseline):
    check_frozen(data, baseline)
    tests = data.get("tests") or {}
    acceptance = (data.get("result") or {}).get("result") or {}
    if (data.get("terminal") is not True or data.get("status") != "completed" or
            type(data.get("endedAt")) is not int or data["endedAt"] <= 0 or not baseline.get("runGuid") or
            data.get("cancelRequested") is not False or data.get("cleanupSucceeded") is not True or
            data.get("cleanupPending") is not False or tests.get("status") != "completed" or
            tests.get("cleanupSucceeded") is not True or tests.get("resultAuthoritative") is not True or
            any(tests.get(key) != expected for key, expected in (("total", 1), ("passed", 1), ("failed", 0), ("skipped", 0))) or
            any(acceptance.get(key) is not True for key in
                ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged")) or
            acceptance.get("runGuid") != baseline["runGuid"] or acceptance.get("deadlineAt") != baseline["deadlineAt"]):
        raise CallPreflightError("Natural single-leaf acceptance or authoritative cleanup is unverified.")


def check_runner(data, run_guid, live=False):
    if data.get("runGuid") != run_guid:
        raise CallPreflightError("Runner does not match the original owned run.")
    if live:
        if data.get("isRunning") is not True or data.get("currentTest") != LEAF or data.get("terminal") is True:
            raise CallPreflightError("Original delayed leaf is not actually executing across disconnect.")
    elif (data.get("terminal") is not True or data.get("isRunning") is not False or
          data.get("cleanupSucceeded") is not True or data.get("cleanupPending") is not False or
          data.get("cleanupResourcesReleased") is not True or data.get("unresolvedResources") != [] or
          data.get("resultAuthoritative") is not True or data.get("status") != "completed" or
          any(data.get(key) != expected for key, expected in (("total", 1), ("passed", 1), ("failed", 0), ("skipped", 0))) or
          len(data.get("results") or []) != 1 or data["results"][0].get("testName") != LEAF or
          data["results"][0].get("testStatus") != "Passed"):
        raise CallPreflightError("Original Runner single leaf or actual resource cleanup is unverified.")


def verify_summary(data, baseline, selection):
    receipt = data["result"]["result"].get("artifact") or {}
    if not isinstance(receipt.get("path"), str) or not receipt["path"]:
        raise CallPreflightError("Final summary receipt has no owned path.")
    path = owned_path(receipt["path"])
    if not path.is_file() or not 0 < path.stat().st_size <= 16 * 1024 * 1024:
        raise CallPreflightError("Final summary file is missing or outside the bounded read limit.")
    with path.open("rb") as stream:
        raw = stream.read(16 * 1024 * 1024 + 1)
    digest = hashlib.sha256(raw).hexdigest()
    if len(raw) != receipt.get("bytes") or digest != receipt.get("sha256"):
        raise CallPreflightError("Actual summary bytes/hash differ from the external receipt.")
    summary = json.loads(raw)
    if (summary.get("taskId") != baseline["taskId"] or summary.get("runGuid") != baseline["runGuid"] or
            summary.get("deadlineAt") != baseline["deadlineAt"] or
            summary.get("expectedProject") != baseline["projectPath"] or
            summary.get("expectedSelectionDomain") != selection["selectionDomain"] or
            summary.get("expectedSelectionSnapshotId") != selection["selectionSnapshotId"] or
            any(summary.get(key) != value for key, value in SELECTORS.items()) or
            summary.get("stoppedConsoleCaptures") != [] or
            any(summary.get(key) is not True for key in ("cleanupVerified", "testIdentityVerified",
                "sourceUnchanged", "acceptancePassed", "artifactWritten", "runnerStartAttempted"))):
        raise CallPreflightError("Summary internal Task/run/deadline/project/selection identity is unverified.")
    check_runner(((summary.get("steps") or {}).get("testStatus") or {}).get("data") or {}, baseline["runGuid"])
    return {"path": str(path), "bytes": len(raw), "sha256": digest, "verified": True}


async def exercise(port, report):
    deadline = asyncio.get_running_loop().time() + 180
    baseline, last_terminal = {}, None
    report.update(result="unknown", ownedTaskIds=[])
    async with asyncio.timeout_at(deadline):
        async with connect(port) as observer:
            async def query(session, tool, args=None, origin=B):
                if asyncio.get_running_loop().time() >= deadline:
                    raise CallPreflightError("Original observation budget expired; never renew it.")
                return await call(session, tool, args or {}, origin, report)

            async def inventory():
                queue = await query(observer, "unity_queue_cleanup")
                captures = await query(observer, "unity_console_capture_list", {"activeOnly": True})
                if (queue.get("complete") is not True or queue.get("isStale") is not False or
                        queue.get("items") != [] or captures.get("activeCount") != 0):
                    raise CallPreflightError("Fresh complete queue and Capture must both be empty.")

            async def task(session=observer):
                nonlocal last_terminal
                data = await query(session, "unity_task_status", {"taskId": baseline["taskId"]})
                check_frozen(data, baseline)
                if data.get("terminal") is True:
                    last_terminal = data
                return data

            async def observe():
                data = await task()
                while data.get("terminal") is not True:
                    await asyncio.sleep(min(0.5, max(0, deadline - asyncio.get_running_loop().time())))
                    data = await task()
                return data

            try:
                async with connect(port) as starter:
                    status_args = {"forceFresh": True, "includeCapabilities": False}
                    identity = require_status(await query(observer, "unity_mcp_status", status_args))
                    if require_status(await query(starter, "unity_mcp_status", status_args, A)) != identity:
                        raise CallPreflightError("The two HTTP clients do not observe the same verified lifetime.")
                    report["identityBefore"] = identity
                    await inventory()
                    runner = await query(observer, "unity_test_status")
                    if runner.get("isRunning") is not False or runner.get("cleanupPending") is True:
                        raise CallPreflightError("Runner activity or pending cleanup prevents an owned start.")
                    selection = await query(starter, "unity_test_list", SELECTORS, A)
                    selected = selection.get("selectedTests") or []
                    if (selection.get("selectionValid") is not True or selection.get("matchedCount") != 1 or
                            selection.get("tests") != [LEAF] or len(selected) != 1 or
                            selected[0].get("fullName") != LEAF or selected[0].get("assembly") != "UPilot.Editor.Tests.dll" or
                            not selection.get("selectionDomain") or not selection.get("selectionSnapshotId")):
                        raise CallPreflightError("Discovery must select only the exact delayed leaf and assembly.")
                    ready = await query(starter, "unity_ensure_ready", {"timeoutS": 5,
                        "requiredEditorMode": "edit", "editorModeControl": "manual"}, A)
                    if (ready.get("ready") is not True or ready.get("blocked") is not False or
                            ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                        raise CallPreflightError("Fresh unblocked manual EditMode readiness is required.")
                    # One native acceptance start, not a unity_task_start wrapper. Unknown ack is never replayed.
                    receipt = await query(starter, "unity_upilot_acceptance_run", {**SELECTORS,
                        "timeoutSec": 90, "stopActiveCaptures": False, "requireTests": True, "writeArtifact": True,
                        "expectedSelectionDomain": selection["selectionDomain"],
                        "expectedSelectionSnapshotId": selection["selectionSnapshotId"]}, A)
                    if not isinstance(receipt.get("taskId"), str) or not receipt["taskId"]:
                        raise CallPreflightError("No owned Task identity was received; never replay start.")
                    baseline = {key: receipt.get(key) for key in (*FROZEN, "runGuid")}
                    report["ownedTaskIds"] = [baseline["taskId"]]
                    check_frozen(receipt, baseline)
                    if (baseline.get("durable") is not True or baseline.get("projectPath") != str(PROJECT) or
                            type(baseline.get("startedAt")) is not int or type(baseline.get("deadlineAt")) is not int or
                            not 0 < baseline["startedAt"] < baseline["deadlineAt"]):
                        raise CallPreflightError("Native original start/deadline/project identity is invalid.")
                    while True:
                        pending = await task()
                        if pending.get("terminal") is True:
                            raise CallPreflightError("Task ended before disconnect; live coverage is missing.")
                        runner = await query(observer, "unity_test_status")
                        if baseline.get("runGuid") and runner.get("runGuid") == baseline["runGuid"] and runner.get("currentTest") == LEAF:
                            check_runner(runner, baseline["runGuid"], live=True)
                            break
                        await asyncio.sleep(min(0.5, max(0, deadline - asyncio.get_running_loop().time())))
                    check_identity(await query(observer, "unity_mcp_status", status_args), identity)
                    report["beforeDisconnect"] = {"task": pending, "runner": runner}
                report["clientADisconnectedAtUtcMs"] = int(time.time() * 1000)
                pending = await task()
                if pending.get("terminal") is True:
                    raise CallPreflightError("Task ended immediately after disconnect; live continuity is unverified.")
                runner = await query(observer, "unity_test_status")
                check_runner(runner, baseline["runGuid"], live=True)
                report["afterDisconnect"] = {"task": pending, "runner": runner}
                async with connect(port) as reconnected:
                    check_identity(await query(reconnected, "unity_mcp_status", status_args, A), identity)
                    await task(reconnected)
                    completed = await observe()
                    check_terminal(completed, baseline)
                    again = await task(reconnected)
                    check_terminal(again, baseline)
                    if any(again.get(key) != completed.get(key) for key in
                           (*FROZEN, "runGuid", "status", "terminal", "endedAt", "cleanupSucceeded", "tests", "result")):
                        raise CallPreflightError("Independent terminal receipts do not match or changed.")
                    results = await query(observer, "unity_test_results", {"runGuid": baseline["runGuid"]})
                    check_runner(results, baseline["runGuid"])
                    report["summary"] = verify_summary(completed, baseline, selection)
                    report["terminalTask"], report["runnerResults"] = completed, results
                report["identityAfter"] = require_status(await query(observer, "unity_mcp_status", status_args))
                if report["identityAfter"] != identity:
                    raise CallPreflightError("Final service/domain identity changed.")
                await inventory()
                report["coverage"] = "native acceptance Task; A disconnected during actual leaf; B live; A reconnect; natural completion; summary bytes/hash and resources"
                report["notCovered"] = ["service restart", "all Server modules loaded", "internal races", "arbitrary resource cleanup", "spoof-proof client identity"]
            except BaseException as exc:
                report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc)
                    if isinstance(exc, CallPreflightError) else "Outcome unknown; raw exception omitted; never replay start."})
                raise
            finally:
                # Only read the received owned ID within the original budget. Never stop shared work.
                if baseline and last_terminal is None and asyncio.get_running_loop().time() < deadline:
                    try:
                        await observe()
                    except BaseException as exc:
                        report["cleanupError"] = {"type": type(exc).__name__, "message": "Original owned terminal remains unverified."}
                report["originalTaskObservedTerminal"] = last_terminal is not None
                if last_terminal is not None:
                    report["lastObservedTask"] = last_terminal
    report["result"] = "observed"  # only after both transport exits succeed


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--execute", action="store_true", help="Start exactly one owned finite native acceptance Task")
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "startedAtUtcMs": int(time.time() * 1000),
              "scope": "V05 native acceptance HTTP disconnect only", "calls": [], "result": "unknown"}
    failed = False
    try:
        await exercise(args.port, report)
    except BaseException as exc:
        failed = True
        report["result"] = "unknown"
        report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                          else "Outcome unknown; no start, cancel or maintenance replayed."})
    finally:
        report["endedAtUtcMs"] = int(time.time() * 1000)
        path = owned_path(PROJECT / "Log" / "P0P1" / ("v05-disconnect-" + nonce + ".json"))
        output, size, digest = _write_observation(path, _redact(report))
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
