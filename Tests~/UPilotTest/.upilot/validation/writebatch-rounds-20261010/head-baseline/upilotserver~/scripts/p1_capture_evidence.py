"""V04 opt-in two-client Capture evidence; retains only its own one-time token in memory."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import time
import uuid

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p0_reload_evidence import owned_path, require_data, require_status, ensure_ready
from p1_request_origin_evidence import connect, PROJECT
from upilot_mcp.client_probe import _payload


async def invoke(session, tool, arguments, report):
    result = _payload(await session.call_tool("unity_tool_call", {"toolName": tool, "args": arguments}))
    # Redact while the one-time token and its possible echoes are still together.
    report["calls"].append(_redact({"tool": tool, "arguments": arguments, "result": result,
                                   "receivedAtUtcMs": int(time.time() * 1000)}))
    return result


def capture_session(data, session_id=None, active=None):
    value = data.get("session")
    if (not isinstance(value, dict) or not value.get("sessionId") or data.get("ok") is False or
            (session_id is not None and value["sessionId"] != session_id) or
            (active is not None and value.get("active") is not active)):
        raise CallPreflightError("Owned Capture identity, actual state or business response is not verified.")
    return value


def attachment(data, session_id, attachment_id=None, closed=None):
    if (data.get("sessionId") != session_id or not data.get("attachmentId") or
            data.get("readOnlyAttachment") is not True or data.get("recoveryRequired") is True or
            (attachment_id is not None and data["attachmentId"] != attachment_id) or
            (closed is not None and data.get("rangeClosed") is not closed)):
        raise CallPreflightError("Read-only attachment identity or frozen range is not verified.")
    return data


def verify_artifacts(directory, manifest, session_id):
    directory = owned_path(directory)
    if manifest.get("sessionId") != session_id or manifest.get("active") is not False:
        raise CallPreflightError("Only the exact owned stopped Capture may be verified.")
    files = []
    disk = {}
    for name in ("session.json", "summary.json"):
        path = owned_path(directory / name)
        if not path.is_file() or path.stat().st_size > 2 * 1024 * 1024:
            raise CallPreflightError("Required bounded Capture manifest/summary is missing.")
        raw = path.read_bytes()
        disk[name] = json.loads(raw)
        files.append({"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})
    pieces = sorted(directory.glob("console*.jsonl"), key=lambda path: (path.name.lower() != "console.jsonl", str(path).lower()))
    if not pieces or not (directory / "console.jsonl").is_file():
        raise CallPreflightError("A real initial JSONL must exist even for zero logs.")
    total = 0
    combined = hashlib.sha256()
    records = []
    for path in pieces:
        owned_path(path)
        if path.stat().st_size > 8 * 1024 * 1024 or total + path.stat().st_size > 16 * 1024 * 1024:
            raise CallPreflightError("Owned evidence exceeds this script's bounded artifact budget.")
        raw = path.read_bytes()
        total += len(raw)
        combined.update(raw)
        files.append({"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})
        records.extend(json.loads(line) for line in raw.splitlines() if line.strip())
    for value in (manifest, disk["session.json"], disk["summary.json"]):
        if (value.get("sessionId") != session_id or value.get("active") is not False or
                value.get("fileBytes") != total or value.get("segmentCount") != len(pieces) or
                value.get("sha256") != combined.hexdigest() or value.get("totalCount") != len(records) or
                value.get("lastError") or value.get("droppedCount") != 0):
            raise CallPreflightError("Capture API and actual manifest/summary/segments disagree.")
    sequences = [record.get("sequence") for record in records]
    if sequences != list(range(len(records))) or manifest.get("nextSequence") != len(records):
        raise CallPreflightError("Owned Capture sequences contain a gap, duplicate or reordering.")
    return {"files": files, "fileBytes": total, "sha256": combined.hexdigest(), "segmentCount": len(pieces),
            "recordCount": len(records)}, records


async def emit(session, message, report):
    # Targeted discovery must establish the absence of a dedicated writer before
    # this exact compiled UnityEngine.Debug.Log(object) fallback.
    require_data(await invoke(session, "unity_reflection_call", {
        "typeName": "UnityEngine.Debug", "methodName": "Log", "isStatic": True,
        "parameters": [message], "parameterTypeNames": ["System.Object"],
    }, report))


async def observe_reload(session, before, deadline, report):
    # A wait window never renews the original execution/cleanup budget.
    end = min(deadline - 5, asyncio.get_running_loop().time() + 30)
    while asyncio.get_running_loop().time() < end:
        try:
            async with asyncio.timeout(max(0.001, end - asyncio.get_running_loop().time())):
                result = await invoke(session, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False,
                }, report)
        except (TimeoutError, ConnectionError, OSError):
            report["reloadObservationGaps"] += 1
        else:
            if result.get("ok") is True and isinstance(result.get("data"), dict):
                data = result["data"]
                path = (data.get("paths") or {}).get("unityProjectAbsolute")
                if path and Path(path).resolve() != PROJECT:
                    raise CallPreflightError("Reload observation returned a different project; no cleanup was dispatched.")
                facts = data.get("session") or {}
                state = data.get("executionState") or {}
                server_pid = ((data.get("runtimeIdentity") or {}).get("mcpServer") or {}).get("processId")
                known = {"unityPid": facts.get("processId"), "unityCreatedAt": facts.get("processCreatedAt"),
                         "serverPid": server_pid, "producerEpoch": state.get("producerEpoch")}
                if any(value and value != before[key] for key, value in known.items()):
                    raise CallPreflightError("Reload observation changed the Unity/service lifetime; no old cleanup was dispatched.")
                try:
                    after = require_status(data)
                except CallPreflightError:
                    report["reloadObservationGaps"] += 1
                else:
                    if after["domainGeneration"] != before["domainGeneration"]:
                        report["identityAfter"] = after
                        report["bridgeReconnected"] = after["bridgeSession"] != before["bridgeSession"]
                        return
            else:
                report["reloadObservationGaps"] += 1
        await asyncio.sleep(min(0.25, max(0, end - asyncio.get_running_loop().time())))
    raise CallPreflightError("A fresh changed domain was not verified within the fixed Reload observation window.")


async def exercise(port, nonce, mode, report):
    deadline = asyncio.get_running_loop().time() + 110  # execution + owned cleanup; fixed once
    directory = owned_path(PROJECT / "Log" / "P0P1" / ("v04-capture-" + nonce))
    if directory.exists():
        raise CallPreflightError("Owned Capture directory must be new.")
    token = None
    session_id = None
    stop_attempted = False
    business_error = None
    cleanup_identity_verified = True
    identities = []
    async with connect(port) as owner, connect(port) as observer:
        for session in (owner, observer):
            status = require_data(await invoke(session, "unity_mcp_status", {"forceFresh": True, "includeCapabilities": False}, report))
            identities.append(require_status(status))
        if mode == "reload" and identities[0] != identities[1]:
            raise CallPreflightError("Both clients must observe the same verified pre-Reload identity.")
        inventory = require_data(await invoke(observer, "unity_queue_cleanup", {}, report))
        captures = require_data(await invoke(observer, "unity_console_capture_list", {"activeOnly": True, "count": 20}, report))
        if inventory.get("complete") is not True or inventory.get("items") or captures.get("activeCount") != 0:
            raise CallPreflightError("Shared/unknown work or an existing Capture prevents this owned exercise.")
        if mode != "empty":
            discovered = require_data(await invoke(owner, "unity_tools_find", {"query": "console", "limit": 200}, report))
            report["consoleWriteDiscovery"] = discovered
            # Abort rather than bypass a newly registered dedicated write tool.
            if any(item.get("name", "").startswith("unity_console_") and
                   item.get("name", "").split("_")[-1] in {"log", "write", "emit", "append"}
                   for item in discovered.get("tools", [])):
                raise CallPreflightError("A dedicated Console writer now exists; update this bounded client before using reflection.")
        if mode == "reload":
            discovered = require_data(await invoke(owner, "unity_tools_find", {"query": "reload", "limit": 200}, report))
            report["reloadDiscovery"] = discovered
            if any("reload" in item.get("name", "").lower() for item in discovered.get("tools", [])):
                raise CallPreflightError("A dedicated Reload tool exists; do not bypass it with reflection.")
            methods = require_data(await invoke(owner, "unity_reflection_find", {
                "typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload",
            }, report)).get("methods", [])
            if not any(item.get("typeName") == "UnityEditor.EditorUtility" and
                       item.get("methodName") == "RequestScriptReload" and item.get("isStatic") is True and
                       item.get("returnType") == "System.Void" and item.get("parameters") == [] for item in methods):
                raise CallPreflightError("The exact zero-argument Unity Reload entry is not verified.")
        await ensure_ready(owner, report)
        try:
            started = require_data(await invoke(owner, "unity_console_capture_start", {
                "title": "V04-owned-" + nonce, "path": str(directory), "ownerId": "V04-owned-" + nonce,
                "requestKey": "V04-start-" + nonce, "clearUnityConsole": False, "excludeUPilot": True,
                "includeStackTrace": False, "flushIntervalMs": 100, "maxFileBytes": 1024 * 1024,
            }, report))
            token = started.get("ownerToken")
            session_id = capture_session(started, active=True)["sessionId"]
            report["ownedSessionId"] = session_id
            if not isinstance(token, str) or not token:
                raise CallPreflightError("Start ownership receipt is missing; no stop or start is replayed.")
            capture_session(started, session_id, True)
            attached = attachment(require_data(await invoke(observer, "unity_console_capture_attach", {
                "sessionId": session_id, "requestKey": "V04-attach-" + nonce,
            }, report)), session_id, closed=False)
            attachment_id = attached["attachmentId"]
            report["ownedAttachmentId"] = attachment_id
            for supplied in (None, "V04-owned-wrong-token-" + nonce):
                args = {"sessionId": session_id}
                if supplied is not None:
                    args["ownerToken"] = supplied
                refused = await invoke(observer, "unity_console_capture_stop", args, report)
                if refused.get("ok") is not False and (refused.get("data") or {}).get("ok") is not False:
                    raise CallPreflightError("Non-owner stop was not explicitly rejected.")
                capture_session(require_data(await invoke(observer, "unity_console_capture_status", {"sessionId": session_id}, report)), session_id, True)
            if mode != "empty":
                for index in range(3):
                    await emit(owner, "V04-owned-" + nonce + "-marker-" + str(index), report)
                if mode == "rotate":
                    for index in range(20):
                        await emit(owner, "V04-owned-" + nonce + "-rotate-" + str(index) + ":" + "x" * 65536, report)
                # Flush the owned pending records before fixing the read/export range.
                require_data(await invoke(owner, "unity_editor_delay", {"delayMs": 250}, report))
            if mode == "reload":
                before = capture_session(require_data(await invoke(owner, "unity_console_capture_status", {
                    "sessionId": session_id,
                }, report)), session_id, True)
                if type(before.get("nextSequence")) is not int or before["nextSequence"] < 3 or before.get("lastError"):
                    raise CallPreflightError("Pre-Reload owned markers were not flushed into a healthy Capture.")
                cleanup_identity_verified = False
                identity_before = require_status(require_data(await invoke(owner, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False,
                }, report)))
                if any(identity_before[key] != identities[0][key] for key in
                       ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch")):
                    raise CallPreflightError("The original Capture service lifetime changed before the Reload request.")
                cleanup_identity_verified = True
                report["identityBefore"] = identity_before
                report["captureSequenceBeforeReload"] = before["nextSequence"]
                report["reloadObservationGaps"] = 0
                # Fence uncertainty before sending; never resend this action.
                cleanup_identity_verified = False
                report["reloadRequest"] = {"attempted": True, "acknowledgement": "unknown"}
                try:
                    async with asyncio.timeout(min(5, max(0.001, deadline - asyncio.get_running_loop().time()))):
                        require_data(await invoke(owner, "unity_reflection_call", {
                            "typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload",
                            "isStatic": True, "parameters": [], "awaitMode": "never", "operationTimeoutSec": 5,
                        }, report))
                    report["reloadRequest"]["acknowledgement"] = "received"
                except (CallPreflightError, TimeoutError, ConnectionError, OSError) as exc:
                    report["reloadRequest"]["errorType"] = type(exc).__name__
                await observe_reload(owner, identity_before, deadline, report)
                cleanup_identity_verified = True
                resumed = capture_session(require_data(await invoke(owner, "unity_console_capture_status", {
                    "sessionId": session_id,
                }, report)), session_id, True)
                if (type(resumed.get("nextSequence")) is not int or
                        resumed["nextSequence"] < before["nextSequence"] or resumed.get("lastError")):
                    raise CallPreflightError("The original Capture did not retain its healthy sequence across Reload.")
                report["captureSequenceAfterReload"] = resumed["nextSequence"]
                await ensure_ready(owner, report)
                await emit(owner, "V04-owned-" + nonce + "-after-reload", report)
                require_data(await invoke(owner, "unity_editor_delay", {"delayMs": 250}, report))
            closed = attachment(require_data(await invoke(observer, "unity_console_capture_detach", {
                "attachmentId": attachment_id, "requestKey": "V04-detach-" + nonce, "export": True,
            }, report)), session_id, attachment_id, True)
            frozen_range = (closed["fromSequence"], closed["toSequence"])
            if frozen_range[0] != attached["fromSequence"] or not isinstance(frozen_range[1], int):
                raise CallPreflightError("Attachment range changed its initial boundary or did not freeze.")
            capture_session(require_data(await invoke(observer, "unity_console_capture_status", {"sessionId": session_id}, report)), session_id, True)
            for _ in range(30):
                if closed.get("exportComplete") is True:
                    break
                cursor = closed.get("continuationToken")
                if not cursor:
                    raise CallPreflightError("Incomplete attachment export has no continuation cursor.")
                closed = attachment(require_data(await invoke(observer, "unity_console_capture_detach", {
                    "attachmentId": attachment_id, "requestKey": "V04-detach-" + nonce,
                    "export": True, "continuationToken": cursor,
                }, report)), session_id, attachment_id, True)
                if (closed["fromSequence"], closed["toSequence"]) != frozen_range:
                    raise CallPreflightError("Continuation changed the frozen attachment range.")
            if closed.get("exportComplete") is not True or closed.get("gapCount") != 0:
                raise CallPreflightError("Owned attachment export is incomplete or has gaps.")
            report["attachment"] = _redact(closed)
            # A post-detach log proves that the export range stays frozen.
            if mode != "empty":
                await emit(owner, "V04-owned-" + nonce + "-after-detach", report)
            stop_attempted = True
            stopped = capture_session(require_data(await invoke(owner, "unity_console_capture_stop", {
                "sessionId": session_id, "ownerToken": token,
            }, report)), session_id, False)
            report["artifacts"], records = verify_artifacts(directory, stopped, session_id)
            frozen_files = report["artifacts"]["files"]
            repeated = capture_session(require_data(await invoke(owner, "unity_console_capture_stop", {
                "sessionId": session_id, "ownerToken": token,
            }, report)), session_id, False)
            again, _ = verify_artifacts(directory, repeated, session_id)
            if again["files"] != frozen_files:
                raise CallPreflightError("Repeated legal stop changed owned disk artifacts.")
            exported_path = Path(closed.get("exportPath", ""))
            # Public attachment paths may be project-relative, not client-cwd-relative.
            if not exported_path.is_absolute():
                exported_path = PROJECT / exported_path
            exported_path = owned_path(exported_path)
            expected_export = owned_path(directory / "attachments" / (attachment_id + ".jsonl"))
            if exported_path != expected_export:
                raise CallPreflightError("Export path does not belong to the exact owned attachment.")
            if not exported_path.is_file() or exported_path.stat().st_size > 16 * 1024 * 1024:
                raise CallPreflightError("Owned frozen export file is missing or exceeds its budget.")
            exported = exported_path.read_bytes()
            expected = [record for record in records if frozen_range[0] <= record["sequence"] < frozen_range[1]]
            actual = [json.loads(line) for line in exported.splitlines() if line.strip()]
            if (actual != expected or len(exported) != closed.get("exportBytes") or
                    hashlib.sha256(exported).hexdigest() != closed.get("exportSha256")):
                raise CallPreflightError("Frozen export content, byte count or hash disagrees with owned source files.")
            report["exportFile"] = {"path": str(exported_path), "bytes": len(exported), "sha256": hashlib.sha256(exported).hexdigest()}
            # Read the original stopped source through the real paging API.
            cursor = None
            sequences = []
            for _ in range(40):
                args = {"sessionId": session_id, "count": 1, "includeStackTrace": False}
                if cursor:
                    args["continuationToken"] = cursor
                page = require_data(await invoke(observer, "unity_console_capture_read", args, report))
                if page.get("sessionId") != session_id or page.get("ok") is False:
                    raise CallPreflightError("Paged source read has a different or failed identity.")
                sequences.extend(record.get("sequence") for record in page.get("logs", []))
                cursor = page.get("continuationToken")
                if not cursor:
                    break
            if cursor or sequences != list(range(len(records))):
                raise CallPreflightError("Source pagination is incomplete, duplicated or out of order.")
            if mode == "empty" and records:
                raise CallPreflightError("Ambient logs were observed; the zero-log case is not covered.")
            if mode == "rotate" and again["segmentCount"] < 2:
                raise CallPreflightError("Real owned logs did not rotate; do not claim the rotation case.")
            if mode != "empty":
                messages = [record.get("message", "") for record in records]
                if not all("V04-owned-" + nonce + "-marker-" + str(index) in messages for index in range(3)):
                    raise CallPreflightError("Owned source markers are missing.")
            report["notCovered"] = ["physical write failure", "other platform versions",
                                    "other clients' sessions", "all Server modules loaded"]
            if mode == "reload":
                for number in range(3):
                    matches = [record for record in records if record.get("message") ==
                               "V04-owned-" + nonce + "-marker-" + str(number)]
                    if len(matches) != 1 or matches[0]["sequence"] >= report["captureSequenceBeforeReload"]:
                        raise CallPreflightError("Pre-Reload owned markers are duplicated or outside their observed boundary.")
                marker = "V04-owned-" + nonce + "-after-reload"
                if sum(record.get("message") == marker for record in records) != 1:
                    raise CallPreflightError("The exact post-Reload owned marker is missing or duplicated.")
                index = next(record["sequence"] for record in records if record.get("message") == marker)
                if index < report["captureSequenceBeforeReload"]:
                    raise CallPreflightError("The post-Reload marker precedes the observed pre-Reload sequence boundary.")
                report["reloadEvidence"] = {"status": "observed", "postReloadMarkerSequence": index,
                                            "causality": "observed_after_single_request_not_causal_proof"}
                report["notCovered"].append("all logs during the Reload subscription gap")
            else:
                report["notCovered"].append("true Reload identity continuity")
            report["result"] = "observed"
        except BaseException as exc:
            business_error = exc
            # HTTP context cleanup may wrap the original error in ExceptionGroup.
            report["businessError"] = {"type": type(exc).__name__,
                                       "message": str(exc) if isinstance(exc, CallPreflightError)
                                       else "Owned action failed; raw exception text omitted."}
            raise
        finally:
            cleanup_end = min(deadline, asyncio.get_running_loop().time() + 5)
            if session_id and token and not stop_attempted and not cleanup_identity_verified:
                # A missing changed-domain witness does not itself forbid owned cleanup.
                # Re-observe the exact lifetime once, sharing the original five-second cleanup budget.
                try:
                    remaining = min(2, cleanup_end - asyncio.get_running_loop().time())
                    if remaining <= 0:
                        raise CallPreflightError("Original owned cleanup budget expired; no identity query was sent.")
                    async with asyncio.timeout(remaining):
                        current = require_status(require_data(await invoke(owner, "unity_mcp_status", {
                            "forceFresh": True, "includeCapabilities": False,
                        }, report)))
                    cleanup_identity_verified = all(current[key] == identities[0][key] for key in
                                                    ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch"))
                except Exception:
                    cleanup_identity_verified = False
            if session_id and token and not stop_attempted and cleanup_identity_verified:
                stop_attempted = True
                try:
                    remaining = cleanup_end - asyncio.get_running_loop().time()
                    if remaining <= 0:
                        raise CallPreflightError("Fixed owned cleanup budget exhausted; stop was not resent.")
                    async with asyncio.timeout(remaining):
                        capture_session(require_data(await invoke(owner, "unity_console_capture_stop", {
                            "sessionId": session_id, "ownerToken": token,
                        }, report)), session_id, False)
                    report["cleanupStoppedOwnedCapture"] = True
                except BaseException:
                    report["cleanupError"] = "Owned stop outcome unknown; inspect the exact session. No retry or force stop was sent."
                    if business_error is None:
                        raise
            if session_id and token and not stop_attempted and not cleanup_identity_verified:
                report["cleanupError"] = "Reload lifetime unverified; inspect the exact owned session. No stop/retry/force stop was sent."
            if session_id and not token:
                report["cleanupError"] = "Ownership receipt unavailable; only observe this exact session, never force stop."
            token = None


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute", action="store_true", help="Explicitly create and stop one owned Capture")
    parser.add_argument("--mode", choices=("ownership", "empty", "rotate", "reload"), default="ownership")
    parser.add_argument("--allow-reload", action="store_true", help="Explicitly permit one controlled managed-domain Reload")
    parser.add_argument("--port", type=int, default=8011)
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    if args.mode == "reload" and not args.allow_reload:
        parser.error("--mode reload also requires --allow-reload; no action was sent")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "scope": "V04 owned two-client Capture only",
              "mode": args.mode, "calls": [], "result": "unknown", "startedAtUtcMs": int(time.time() * 1000)}
    error = None
    try:
        async with asyncio.timeout(120):
            await exercise(args.port, nonce, args.mode, report)
    except BaseException as exc:
        error = exc
        report["error"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                           else "Owned exercise failed; raw exception text deliberately omitted."}
    finally:
        report["endedAtUtcMs"] = int(time.time() * 1000)
        output, size, digest = _write_observation(owned_path(PROJECT / "Log" / "P0P1" / ("v04-capture-" + nonce + ".json")), report)
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return 1 if error else 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))