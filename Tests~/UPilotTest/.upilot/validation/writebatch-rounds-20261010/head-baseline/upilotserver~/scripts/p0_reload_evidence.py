"""Opt-in V01 real Reload witness; one Arm request, no recovery or replay."""
import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import stat
import time
import uuid

from upilot_mcp.client_probe import _payload
from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p1_request_origin_evidence import connect, PROJECT

PROBE = "CodingRiver.UPilot.Tests.UPilotCommandReloadProbe"
AWAIT_TIMEOUT_MS = 30000
WITNESS_NAMES = ("armed.json", "before-reload.json", "after-reload.json", "effect.json")


def owned_path(path):
    """Reject links before resolve/mkdir, including existing ancestor junctions."""
    path = Path(os.path.abspath(path))
    try:
        path.relative_to(PROJECT)
    except ValueError:
        raise CallPreflightError("Evidence path must stay in the exact canonical project.")
    for current in (path, *path.parents):
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
            raise CallPreflightError("Evidence paths must not follow symlinks or reparse points.")
    return path


async def invoke(session, tool, arguments, report):
    result = _payload(await session.call_tool("unity_tool_call", {"toolName": tool, "args": arguments}))
    report["calls"].append(_redact({"tool": tool, "arguments": arguments, "result": result,
                                   "receivedAtUtcMs": int(time.time() * 1000)}))
    return result


def require_data(result):
    if result.get("ok") is not True or not isinstance(result.get("data"), dict):
        raise CallPreflightError("An evidence request failed; the original action will not be replayed.")
    return result["data"]


def require_status(data):
    actual = (data.get("paths") or {}).get("unityProjectAbsolute")
    state = data.get("executionState") or {}
    loaded = ((data.get("runtimeIdentity") or {}).get("mcpServer") or {}).get("loadedSource") or {}
    if (not actual or Path(actual).resolve() != PROJECT or data.get("connected") is not True or
            data.get("serverReady") is not True or state.get("ready") is not True or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            state.get("playModeState") != "edit" or loaded.get("status") != "equivalent"):
        raise CallPreflightError("Exact canonical fresh EditMode and selected-entry loading are required.")
    session = data.get("session") or {}
    pid = session.get("processId")
    created = session.get("processCreatedAt")
    server_pid = ((data.get("runtimeIdentity") or {}).get("mcpServer") or {}).get("processId")
    if (session.get("identityVerified") is not True or not isinstance(pid, int) or pid <= 0 or
            not isinstance(created, int) or created <= 0 or not isinstance(server_pid, int) or server_pid <= 0):
        raise CallPreflightError("Verified Unity PID, creation time and Server PID are required.")
    if not session.get("sessionId") or not state.get("producerEpoch") or not state.get("domainGeneration"):
        raise CallPreflightError("Bridge, producer and managed-domain identities must be known.")
    return {"unityPid": pid, "unityCreatedAt": created, "serverPid": server_pid,
            "bridgeSession": session.get("sessionId"), "producerEpoch": state.get("producerEpoch"),
            "domainGeneration": state.get("domainGeneration"), "loadedScope": loaded.get("scope")}


async def ensure_ready(session, report):
    # Observe only: this helper must never exit PlayMode or renew an action deadline.
    data = require_data(await invoke(session, "unity_ensure_ready", {
        "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual",
    }, report))
    if (data.get("ready") is not True or data.get("blocked") is not False or
            data.get("contextAuthoritative") is not True or data.get("contextStale") is not False):
        raise CallPreflightError("Authoritative unblocked Editor readiness is required before the owned mutation.")


def collect_witness(directory):
    """Keep partial facts on failure without pretending missing files are evidence."""
    files = {}
    for name in (*WITNESS_NAMES, "reload-requested.json", "probe-ended.json"):
        path = owned_path(directory / name)
        if not path.exists():
            continue
        if not path.is_file() or path.stat().st_size > 65536:
            raise CallPreflightError("A witness file is not a bounded regular file.")
        raw = path.read_bytes()
        files[name] = {"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
                       "data": json.loads(raw)}
    return files


def read_witness(directory, nonce):
    files = collect_witness(directory)
    if any(name not in files for name in WITNESS_NAMES):
        raise CallPreflightError("Required Reload witnesses are missing; the original terminal remains recorded.")
    armed, before, after, effect = (files[name]["data"] for name in WITNESS_NAMES)
    identity = armed.get("originalCommandId")
    if not identity or any(value.get("nonce") != nonce or value.get("originalCommandId") != identity or
                           Path(value.get("directory", "")) != directory or value.get("commandName") != "reflection.call"
                           for value in (armed, before, after)):
        raise CallPreflightError("Witness files do not carry the same owned nonce, command and directory.")
    frozen = ("nonce", "directory", "originalCommandId", "commandName", "phaseAtArm", "domainBefore",
              "serverLifetimeBefore", "processIdBefore", "processCreatedBefore", "armedAt", "deadlineAt")
    if any(any(value.get(key) != armed.get(key) for key in frozen) for value in (before, after)):
        raise CallPreflightError("Original witness identity or helper deadline was rewritten.")
    if (armed.get("phaseAtArm") != "executing" or before.get("phaseBeforeReload") != "executing" or
            before.get("activeBeforeReload") is not True or after.get("realDomainReload") is not True or
            after.get("ownedEffectUnchanged") is not True or effect != {"effectCount": 1}):
        raise CallPreflightError("Actual executing-command Reload or unchanged owned effect is not proven.")
    if (not after.get("domainBefore") or not after.get("domainAfter") or
            after["domainBefore"] == after["domainAfter"] or
            after.get("processIdBefore") != after.get("processIdAfter") or
            after.get("processCreatedBefore") != after.get("processCreatedAfter") or
            after.get("serverLifetimeBefore") != after.get("serverLifetimeAfter") or after.get("failure")):
        raise CallPreflightError("Witness identities changed beyond a normal Domain Reload, or witness failed.")
    times = [after.get(name) for name in ("armedAt", "beforeReloadAt", "observedAfterAt", "deadlineAt")]
    if any(not isinstance(value, int) or value <= 0 for value in times) or not times[0] <= times[1] <= times[2] <= times[3]:
        raise CallPreflightError("Witness timing is incomplete or outside its fixed helper window.")
    if times[3] - times[0] != 30000:
        raise CallPreflightError("The helper witness deadline changed.")
    # A cleanup witness records only its own observation callback and marker.
    # Never infer interrupted-command/resource cleanup from this flag.
    if after.get("resourcesReleased") is not True or not isinstance(after.get("endedAt"), int) or after["endedAt"] < times[2]:
        raise CallPreflightError("Owned witness cleanup receipt is missing or unverified.")
    return files


async def observe_original_command(session, directory, nonce, report):
    """One read-only query even when Reload files are missing; never replay Arm."""
    evidence = {"status": "unknown", "reason": "owned_arm_identity_unverified"}
    report["serverCommandEvidence"] = evidence
    armed = (report.get("witnessFiles", {}).get("armed.json", {}) or {}).get("data")
    if (not isinstance(armed, dict) or armed.get("nonce") != nonce or
            armed.get("directory") != str(directory) or armed.get("commandName") != "reflection.call" or
            not isinstance(armed.get("originalCommandId"), str) or not armed["originalCommandId"] or
            not isinstance(armed.get("serverLifetimeBefore"), str) or not armed["serverLifetimeBefore"]):
        return
    command_id = armed["originalCommandId"]
    evidence["commandId"] = command_id
    try:
        result = await invoke(session, "unity_operation_get", {"commandId": command_id}, report)
    except Exception as exc:
        evidence.update(reason="command_observation_unavailable", errorType=type(exc).__name__)
        return  # Do not replace the original business error with a diagnostic error.
    data = result.get("data") if result.get("ok") else (result.get("error") or {}).get("detail")
    observation = data.get("serverCommandObservation") if isinstance(data, dict) else None
    evidence["queryOk"] = result.get("ok") is True
    if not isinstance(observation, dict):
        evidence["reason"] = "server_command_observation_missing"
        return
    if (observation.get("commandId") != command_id or
            observation.get("serviceLifecycleId") != armed["serverLifetimeBefore"]):
        evidence["reason"] = "server_command_identity_mismatch"
        return
    scopes = {
        "current-service-lifetime; response-wait-only; send-attempts-not-execution-count",
        "current-service-lifetime; call-budget-ready-send-response; send-attempts-not-execution-count",
    }
    if observation.get("source") != "server_command_record" or observation.get("scope") not in scopes:
        evidence["reason"] = "server_command_observation_scope_unverified"
        return
    fields = ("source", "scope", "commandId", "serviceLifecycleId", "status", "reason", "requestId",
              "commandName", "recordStatus", "createdAt", "completedAt", "sendAttempts",
              "responseWaitStartedAt", "responseWaitDeadlineAt", "responseWaitTimeoutMs",
              "callStartedAt", "callDeadlineAt", "callTimeoutMs")
    evidence["observation"] = {key: observation[key] for key in fields if key in observation}
    if observation.get("status") != "observed":
        evidence["reason"] = "command_record_not_observed"
        return
    if observation.get("commandName") != "reflection.call":
        evidence["reason"] = "server_command_identity_mismatch"
        return
    evidence.update(status="observed", reason="", missingFacts=[key for key in
        ("sendAttempts", "responseWaitStartedAt", "responseWaitDeadlineAt", "responseWaitTimeoutMs",
         "callStartedAt", "callDeadlineAt", "callTimeoutMs")
        if observation.get(key) is None])
    facts = evidence["observation"]
    total_fields = ("callStartedAt", "callDeadlineAt", "callTimeoutMs")
    total = [facts.get(key) for key in total_fields]
    evidence["totalBudgetStatus"] = "unknown"
    if all(isinstance(value, int) and not isinstance(value, bool) and value > 0 for value in total):
        start, deadline, timeout = total
        evidence["totalBudgetStatus"] = "observed" if deadline - start == timeout else "unverified"
        if facts.get("responseWaitDeadlineAt") is not None and facts["responseWaitDeadlineAt"] > deadline:
            evidence["totalBudgetStatus"] = "unverified"



def terminal_projection(result, operation_id, report=None):
    data = result.get("data") if result.get("ok") else (result.get("error") or {}).get("detail")
    if not isinstance(data, dict) or data.get("operationId") != operation_id:
        raise CallPreflightError("Original reflection observation identity is missing or mismatched.")
    if data.get("terminal") is not True:
        return None
    if report is not None:
        report["originalTerminal"] = data
        report["terminal"] = data  # Keep the prior report field, before any file validation.
    if data.get("status") not in {"Failed", "Canceled"}:
        raise CallPreflightError("Interrupted Arm unexpectedly succeeded; no recovery is permitted.")
    return data


async def exercise(port, nonce, report):
    directory = owned_path(PROJECT / "Log" / "Stage4" / "CommandRealReload" / nonce)
    if directory.exists():
        raise CallPreflightError("The owned witness directory must be new; never replay an Arm.")
    async with connect(port) as session:
        before = require_data(await invoke(session, "unity_mcp_status", {"forceFresh": True, "includeCapabilities": False}, report))
        report["identityBefore"] = require_status(before)
        inventory = require_data(await invoke(session, "unity_queue_cleanup", {}, report))
        captures = require_data(await invoke(session, "unity_console_capture_list", {"activeOnly": True, "count": 20}, report))
        if (inventory.get("complete") is not True or inventory.get("items") or captures.get("activeCount") != 0):
            raise CallPreflightError("Shared work or unknown Capture state prevents controlled Reload.")
        probe = require_data(await invoke(session, "unity_type_exists", {"typeName": PROBE}, report))
        if probe.get("exists") is not True:
            raise CallPreflightError("The already compiled owned Reload probe is unavailable.")
        await ensure_ready(session, report)
        report["armSent"] = True
        started = require_data(await invoke(session, "unity_reflection_call", {
            "typeName": PROBE, "methodName": "ArmAndWait", "isStatic": True,
            "parameters": [nonce], "parameterTypeNames": ["System.String"],
            "forceAsync": True, "operationTimeoutSec": 45, "awaitTimeoutMs": AWAIT_TIMEOUT_MS,
        }, report))
        operation_id = started.get("operationId")
        if not isinstance(operation_id, str) or not operation_id:
            raise CallPreflightError("No original reflection handle received; Arm is never resent.")
        report["ownedReflectionOperationId"] = operation_id
        terminal = None
        for _ in range(60):
            try:
                result = await invoke(session, "unity_reflection_operation_status", {"operationId": operation_id}, report)
            except Exception:
                report["observationGaps"] += 1
            else:
                terminal = terminal_projection(result, operation_id, report)
            if terminal is not None:
                break
            await asyncio.sleep(1)
        if terminal is None:
            raise CallPreflightError("Original outcome remains unknown within the fixed observation budget.")
        # Do not query a frozen terminal another 60 times just because a file is missing.
        # Only an observed before-Reload boundary warrants a short file-only wait for
        # the new domain's delayCall; it neither renews nor replays the original action.
        for _ in range(5):
            if (not owned_path(directory / "before-reload.json").is_file() or
                    owned_path(directory / "after-reload.json").is_file()):
                break
            await asyncio.sleep(1)
        report["witnessFiles"] = collect_witness(directory)
        report["reloadEvidence"] = {"status": "unverified", "missingFiles": [
            name for name in WITNESS_NAMES if name not in report["witnessFiles"]]}
        await observe_original_command(session, directory, nonce, report)
        report["witnessFiles"] = read_witness(directory, nonce)
        # Observe reconnect readiness within the original total budget; never re-arm.
        await ensure_ready(session, report)
        after = require_data(await invoke(session, "unity_mcp_status", {"forceFresh": True, "includeCapabilities": False}, report))
        report["identityAfter"] = require_status(after)
        for key in ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch"):
            if report["identityBefore"][key] != report["identityAfter"][key]:
                raise CallPreflightError("The observed service/Unity lifetime changed, not just its managed domain.")
        if report["identityBefore"]["domainGeneration"] == report["identityAfter"]["domainGeneration"]:
            raise CallPreflightError("The authoritative managed domain did not change.")
        # Bridge reconnect may assign a new sessionId during a real Reload;
        # retain both identities rather than confusing reconnect with service restart.
        report["bridgeReconnected"] = report["identityBefore"]["bridgeSession"] != report["identityAfter"]["bridgeSession"]
        witness = report["witnessFiles"]["after-reload.json"]["data"]
        if (witness["processIdAfter"] != report["identityAfter"]["unityPid"] or
                witness["processCreatedAfter"] != report["identityAfter"]["unityCreatedAt"]):
            raise CallPreflightError("File witnesses and authoritative process identity disagree.")
        again = terminal_projection(await invoke(session, "unity_reflection_operation_status", {"operationId": operation_id}, report), operation_id)
        frozen = ("operationId", "requestId", "startedAt", "status", "terminal", "resultError")
        if again is None or any(again.get(key) != terminal.get(key) for key in frozen) or read_witness(directory, nonce) != report["witnessFiles"]:
            raise CallPreflightError("Original terminal or witness files changed on a later observation.")
        # These independent projections have changing elapsedSec/observed timestamps;
        # callers compare frozen business fields, not the whole envelope.
        report["reloadEvidence"] = {"status": "verified", "missingFiles": []}
        report["result"] = "observed"
        before = report["witnessFiles"]["before-reload.json"]["data"]
        count_fields = ("dispatchCountBefore", "executionCountBefore", "mainThreadActionCountBefore", "retainedInvocationsBefore")
        counts = {key: before.get(key) for key in count_fields}
        report["unityCommandEvidence"] = {"source": "owned-before-reload-witness",
            "scope": before.get("countScope"), **counts,
            "status": "observed" if all(value == 1 for value in counts.values()) else "unverified"}
        report["witnessCleanupEvidence"] = {"scope": "owned-witness-callback-and-marker-only",
            "resourcesReleased": witness.get("resourcesReleased"), "endedAt": witness.get("endedAt")}
        report["notCovered"] = ["Unity business-effect/whole-history execution count",
                                "all Server modules/globals/dependencies loaded", "other real fault combinations"]
        if report["unityCommandEvidence"]["status"] != "observed":
            report["notCovered"].append("Unity retained-invocation entry counts")
        evidence = report["serverCommandEvidence"]
        if evidence["status"] != "observed" or evidence.get("missingFacts"):
            report["notCovered"].append("Server response-wait/send-entry/total-budget facts incomplete")
        if evidence.get("totalBudgetStatus") != "observed":
            report["notCovered"].append("Server ready-send-response observation budget")


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute", action="store_true", help="Send exactly one owned Arm and request real script Reload")
    parser.add_argument("--port", type=int, default=8011)
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "scope": "V01 actual Reload witness only",
              "calls": [], "observationGaps": 0, "armSent": False, "result": "unknown",
              "startedAtUtcMs": int(time.time() * 1000)}
    error = None
    try:
        async with asyncio.timeout(90):
            await exercise(args.port, nonce, report)
    except BaseException as exc:
        error = exc
        if report.get("originalTerminal"):
            report["result"] = "failed"
        report["error"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                           else "Observation failed; raw exception text omitted and Arm is not replayed."}
    finally:
        if report["armSent"] and "witnessFiles" not in report:
            try:
                directory = owned_path(PROJECT / "Log" / "Stage4" / "CommandRealReload" / nonce)
                report["witnessFiles"] = collect_witness(directory)
                report["reloadEvidence"] = {"status": "unverified", "missingFiles": [
                    name for name in WITNESS_NAMES if name not in report["witnessFiles"]]}
            except Exception as witness_error:
                # Evidence collection must not replace the original operation error.
                report["witnessCollectionError"] = {"type": type(witness_error).__name__}
        report.setdefault("serverCommandEvidence", {"status": "unknown",
            "reason": "command_observation_not_reached; no new connection or replay"})
        report["endedAtUtcMs"] = int(time.time() * 1000)
        output, size, digest = _write_observation(owned_path(PROJECT / "Log" / "P0P1" / ("v01-reload-" + nonce + ".json")), report)
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return 1 if error else 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))