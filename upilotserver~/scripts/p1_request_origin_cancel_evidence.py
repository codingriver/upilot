"""Opt-in D19 owned cancellation and late-old-ID evidence, not a cancellation framework."""
import argparse
import asyncio
import json
import time
import uuid
from pathlib import Path

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p0_reload_evidence import require_status, owned_path
from p1_request_origin_evidence import PROJECT, connect, call, check_task

DELAYED_TEST = "CodingRiver.UPilot.Tests.UPilotRunnerLeafAcceptanceFixtures.DelayedLeaf"
FROZEN_FIELDS = ("taskId", "projectPath", "durable", "createdAt", "deadlineAt", "requestOrigin")


def check_safety(status, identity=None):
    """Refuse potential automatic service escalation; never change the configuration."""
    maintenance = status.get("aiServiceMaintenance") or {}
    if (maintenance.get("configError") != "" or
            maintenance.get("autoHardStopOnSoftFailure") is not False):
        raise CallPreflightError("Automatic hard-stop escalation must be explicitly disabled; no settings are changed.")
    if identity is None:
        return require_status(status)
    # A live Runner is non-ready. Verify identity/freshness, not synthetic readiness.
    session, state = status.get("session") or {}, status.get("executionState") or {}
    server = (status.get("runtimeIdentity") or {}).get("mcpServer") or {}
    loaded = server.get("loadedSource") or {}
    actual = (status.get("paths") or {}).get("unityProjectAbsolute")
    observed = {"unityPid": session.get("processId"), "unityCreatedAt": session.get("processCreatedAt"),
                "serverPid": server.get("processId"), "bridgeSession": session.get("sessionId"),
                "producerEpoch": state.get("producerEpoch"), "domainGeneration": state.get("domainGeneration"),
                "loadedScope": loaded.get("scope")}
    if (not actual or Path(actual).resolve() != PROJECT or status.get("connected") is not True or
            status.get("serverReady") is not True or session.get("identityVerified") is not True or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            loaded.get("status") != "equivalent" or observed != identity):
        raise CallPreflightError("The exact fresh project/service/domain identity changed; no cancellation is sent.")
    return observed


def check_frozen(data, baseline, origin):
    check_task(data, baseline["taskId"], origin)
    if any(data.get(field) != baseline.get(field) for field in FROZEN_FIELDS):
        raise CallPreflightError("The original Task identity, creation origin or deadline changed.")
    if baseline.get("runGuid") and data.get("runGuid") != baseline["runGuid"]:
        raise CallPreflightError("The established original runGuid changed.")


def check_terminal(data, baseline, origin, expected):
    check_frozen(data, baseline, origin)
    tests = data.get("tests") or {}
    if (data.get("terminal") is not True or data.get("status") != expected or
            not isinstance(data.get("endedAt"), int) or data["endedAt"] <= 0 or
            data.get("cleanupSucceeded") is not True or not data.get("runGuid") or
            tests.get("cleanupSucceeded") is not True or tests.get("resultAuthoritative") is not True):
        raise CallPreflightError("Owned terminal or authoritative cleanup is unverified.")
    if expected == "cancelled" and data.get("cancelRequested") is not True:
        raise CallPreflightError("The cancelled Task does not retain its accepted cancellation intent.")
    if expected == "completed" and (data.get("cancelRequested") is True or tests.get("status") != "completed" or
            tests.get("total") != 1 or tests.get("passed") != 1 or tests.get("failed") != 0 or tests.get("skipped") != 0):
        raise CallPreflightError("The successor did not complete its exact single leaf naturally.")


async def exercise(port, report):
    a = {"clientId": "owned-client-A", "chatId": "owned-cancel-A"}
    b = {"clientId": "owned-client-B", "chatId": "owned-cancel-B"}
    c = {"clientId": "owned-client-A", "chatId": "owned-successor-C"}
    deadline = time.monotonic() + 95
    owned, terminal = {}, {}
    report.update(result="unknown", ownedTaskIds=[], terminalTasks=terminal,
                  cancellationVerified=False, lateOldIdVerified=False)

    def remaining():
        value = deadline - time.monotonic()
        if value <= 0:
            raise CallPreflightError("Original observation budget expired; starts and cancellation are not replayed.")
        return value

    async with connect(port) as observer:
        async with connect(port) as starter:
            identities = [check_safety(await call(session, "unity_mcp_status", {
                "forceFresh": True, "includeCapabilities": False}, {}, report)) for session in (starter, observer)]
            if identities[0] != identities[1]:
                raise CallPreflightError("The two clients do not observe the same service/domain.")
            identity = identities[0]
            report["identityBefore"] = identity
            inventory = await call(observer, "unity_queue_cleanup", {}, {}, report)
            if (inventory.get("complete") is not True or inventory.get("isStale") is not False or
                    inventory.get("items") or (inventory.get("authorization") or {}).get("allowed") is not True):
                raise CallPreflightError("An empty complete queue and effective soft-stop grant are required before starting.")
            captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, {}, report)
            if captures.get("activeCount") != 0:
                raise CallPreflightError("Do not interfere with an existing Capture.")
            selectors = {"testMode": "EditMode", "testNames": [DELAYED_TEST],
                         "matchMode": "union", "requireAllSelectorsMatch": True}
            selection = await call(starter, "unity_test_list", selectors, {}, report)
            domain, snapshot = selection.get("selectionDomain"), selection.get("selectionSnapshotId")
            if (selection.get("selectionValid") is not True or selection.get("matchedCount") != 1 or
                    selection.get("tests") != [DELAYED_TEST] or not isinstance(domain, str) or not domain.strip() or
                    not isinstance(snapshot, str) or not snapshot.strip()):
                raise CallPreflightError("Discovery must select the existing explicit delayed leaf exactly.")
            ready = await call(starter, "unity_ensure_ready", {
                "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual"}, {}, report)
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Authoritative unblocked EditMode is required.")
            start_args = {"toolName": "unity_test_run", "toolArgs": {**selectors,
                          "expectedSelectionDomain": domain, "expectedSelectionSnapshotId": snapshot},
                          "timeoutS": 70, "retryCount": 0}

            async def start(origin):
                remaining()
                check_safety(await call(starter, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False}, {}, report), identity)
                remaining()
                data = await call(starter, "unity_task_start", {
                    **start_args, "taskName": "D19-owned-" + origin["chatId"]}, origin, report)
                task_id = data.get("taskId")
                if not isinstance(task_id, str) or not task_id or task_id in owned:
                    raise CallPreflightError("No unique owned Task ID received; never resend Start.")
                owned[task_id] = (dict(data), origin)
                report["ownedTaskIds"].append(task_id)  # retain before validating the rest of the receipt
                check_task(data, task_id, origin)
                if (data.get("durable") is not True or Path(data.get("projectPath") or ".").resolve() != PROJECT or
                        any(not isinstance(data.get(key), int) or data[key] <= 0 for key in ("createdAt", "deadlineAt")) or
                        data["deadlineAt"] <= data["createdAt"]):
                    raise CallPreflightError("Owned durable Task identity or original deadline is missing.")
                return task_id

            async def pending(task_id):
                baseline, origin = owned[task_id]
                while remaining():
                    data = await call(observer, "unity_task_status", {"taskId": task_id}, b, report)
                    check_frozen(data, baseline, origin)
                    if data.get("terminal") is True:
                        terminal[task_id] = data
                        raise CallPreflightError("The owned leaf ended before the intended live observation; do not retry.")
                    if data.get("runGuid"):
                        baseline["runGuid"] = data["runGuid"]
                        runner = await call(observer, "unity_test_status", {}, b, report)
                        if runner.get("runGuid") != data["runGuid"]:
                            raise CallPreflightError("The Runner is not the original owned run.")
                        if (runner.get("currentTest") == DELAYED_TEST and runner.get("isRunning") is True and
                                runner.get("terminal") is False and runner.get("cancelRequested") is False):
                            return data, runner
                    await asyncio.sleep(min(0.5, remaining()))

            async def observe(task_id):
                baseline, origin = owned[task_id]
                while remaining():
                    data = await call(observer, "unity_task_status", {"taskId": task_id}, b, report)
                    check_frozen(data, baseline, origin)
                    if data.get("terminal") is True:
                        terminal[task_id] = data
                        return data
                    await asyncio.sleep(min(0.5, remaining()))

            try:
                old = await start(a)
                _, old_runner = await pending(old)
                report["oldLeafPending"] = old_runner
                check_safety(await call(observer, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False}, {}, report), identity)
                remaining()
                cancel_args = {"targetType": "Task", "targetId": old, "action": "cancel",
                               "reason": "D19 cancel only this owned finite delayed test Task"}
                preview = await call(observer, "unity_queue_cleanup", {**cancel_args, "dryRun": True}, b, report)
                target = preview.get("target") or {}
                if (preview.get("dryRun") is not True or preview.get("allowed") is not True or
                        Path(preview.get("projectPath") or ".").resolve() != PROJECT or
                        target.get("id") != old or target.get("type") != "Task" or target.get("action") != "cancel" or
                        not isinstance(preview.get("confirmToken"), str) or not preview["confirmToken"]):
                    raise CallPreflightError("Cancellation preview is not authorized for the exact owned Task.")
                # Preview is bound to exact target/action/project/grant, not self-declared labels.
                # Queue's safety snapshot may omit origin; the owned Task was checked separately.
                check_safety(await call(observer, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False}, {}, report), identity)
                remaining()
                receipt = await call(observer, "unity_queue_cleanup", {**cancel_args, "dryRun": False,
                    "expectedProjectPath": str(PROJECT), "confirmToken": preview["confirmToken"]}, b, report)
                check_frozen(receipt, owned[old][0], a)
                if receipt.get("cancelRequested") is not True:
                    raise CallPreflightError("Cancellation intent was not accepted; no second cancellation is sent.")
                report["cancelReceipt"] = _redact(receipt)
                cancelled = await observe(old)
                check_terminal(cancelled, owned[old][0], a, "cancelled")
                result = await call(observer, "unity_test_results", {"runGuid": cancelled["runGuid"]}, b, report)
                if (result.get("runGuid") != cancelled["runGuid"] or result.get("terminal") is not True or
                        result.get("cancelAccepted") is not True or result.get("cancelRequested") is not True or
                        result.get("cleanupSucceeded") is not True or result.get("cleanupResourcesReleased") is not True or
                        result.get("cleanupPending") is not False or result.get("unresolvedResources") != [] or
                        result.get("resultAuthoritative") is not True or not isinstance(result.get("endedAt"), int) or
                        not isinstance(old_runner.get("startedAt"), int) or old_runner["startedAt"] <= 0 or
                        not old_runner["startedAt"] <= result["endedAt"] < old_runner["startedAt"] + 30000):
                    raise CallPreflightError("Actual cancellation before the delayed leaf's natural end and cleanup are unverified.")
                report["cancelledRunnerResult"] = result
                report["cancellationVerified"] = True
                successor = await start(c)
                before, _ = await pending(successor)
                remaining()
                # This is deliberately a late old-ID request after a real successor is executing.
                # It does not claim an in-flight request delayed internally by the Server.
                check_safety(await call(observer, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False}, {}, report), identity)
                remaining()
                stale = await call(observer, "unity_task_cancel", {"taskId": old}, b, report)
                if stale.get("taskId") != old or stale.get("status") != "not_found" or stale.get("changed") is not False:
                    raise CallPreflightError("The late old-ID request did not return an unchanged no-op.")
                after, _ = await pending(successor)
                if (after.get("cancelRequested") is True or before.get("runGuid") != after.get("runGuid")):
                    raise CallPreflightError("The late old-ID request changed the executing successor.")
                report["lateOldIdReceipt"] = stale
                report["successorBeforeLateRequest"], report["successorAfterLateRequest"] = before, after
                completed = await observe(successor)
                check_terminal(completed, owned[successor][0], c, "completed")
                frozen_old = await call(observer, "unity_task_status", {"taskId": old}, b, report)
                for field in (*FROZEN_FIELDS, "runGuid", "status", "terminal", "endedAt", "cleanupSucceeded", "tests"):
                    if frozen_old.get(field) != cancelled.get(field):
                        raise CallPreflightError("The old terminal changed after the successor and late request.")
                report["lateOldIdVerified"] = True
                report["identityAfter"] = check_safety(await call(observer, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False}, {}, report))
                if report["identityAfter"] != identity:
                    raise CallPreflightError("Service/domain identity changed during the exercise.")
                report["result"] = "observed"
                report["coverage"] = "two clients; authorized exact owned cancellation; late old ID during live successor; original deadlines and cleanup"
                report["notCovered"] = ["authorization denial on a live service", "internally delayed in-flight request races",
                    "direct Capture/WriteBatch/Bridge attribution", "all Server modules loaded", "spoof-proof origin identity"]
            except BaseException as exc:
                report["firstError"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                                        else "Call outcome unknown; never replay Start or cancellation."}
                raise
            finally:
                # Only observe received owned IDs within the original budget; no stop/restart to tidy up.
                for task_id in owned:
                    if task_id not in terminal and deadline > time.monotonic():
                        try:
                            await observe(task_id)
                        except BaseException as exc:
                            report.setdefault("cleanupErrors", []).append({"taskId": task_id,
                                "type": type(exc).__name__, "message": "Original owned terminal remains unverified."})
                report["allOwnedTasksObservedTerminal"] = bool(owned) and len(owned) == len(terminal)


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--execute", action="store_true", help="Explicitly start and cancel only owned finite Tasks")
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "startedAtUtcMs": int(time.time() * 1000),
              "scope": "D19 owned cancellation and late-old-ID evidence only", "calls": [], "result": "unknown"}
    failed = False
    try:
        async with asyncio.timeout(100):
            await exercise(args.port, report)
    except BaseException as exc:
        failed = True
        report["result"] = "unknown"
        report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                                       else "Call outcome unknown; no business request replayed."})
    finally:
        report["endedAtUtcMs"] = int(time.time() * 1000)
        path = owned_path(PROJECT / "Log" / "P0P1" / ("d19-cancel-" + nonce + ".json"))
        output, size, digest = _write_observation(path, _redact(report))
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
