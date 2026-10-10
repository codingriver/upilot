"""V04 opt-in missing-segment evidence; changes only one owned, stopped Capture."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import uuid

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p0_reload_evidence import owned_path, require_data, require_status, ensure_ready
from p1_capture_evidence import invoke, capture_session, verify_artifacts, emit
from p1_request_origin_evidence import connect, PROJECT


def file_hash(path):
    path = owned_path(path)
    if not path.is_file() or path.stat().st_size > 8 * 1024 * 1024:
        raise CallPreflightError("Owned segment is missing or exceeds the evidence byte budget.")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def restore_segment(segment, backup, expected_hash):
    # Synchronous, before any await: cancellation cannot strand the moved file.
    owned_path(segment)
    owned_path(backup)
    if segment.exists() or file_hash(backup) != expected_hash:
        raise CallPreflightError("Owned restore conflict; preserve both paths, never overwrite.")
    backup.rename(segment)  # Windows rename refuses an existing destination.
    if file_hash(segment) != expected_hash:
        raise CallPreflightError("Restored owned segment hash differs.")


def require_missing(data, session_id, segment_name="console.jsonl"):
    if (data.get("ok") is not False or data.get("sessionId") != session_id or
            data.get("scanComplete") is not False or data.get("logs") != [] or
            data.get("continuationToken") or
            data.get("error") != "CONSOLE_CAPTURE_SEGMENT_MISSING: " + segment_name):
        raise CallPreflightError("Missing Capture segment was not explicitly rejected without a successful page.")


async def exercise(port, report, deadline, segment_kind="initial"):
    if segment_kind not in {"initial", "middle", "final"}:
        raise CallPreflightError("An explicit initial, middle or final owned segment is required.")
    nonce = uuid.uuid4().hex
    directory = owned_path(PROJECT / "Log" / "P0P1" / ("v04-missing-" + nonce))
    if directory.exists():
        raise CallPreflightError("Owned Capture directory must be new.")
    session_id = token = None
    stop_attempted = moved = False
    segment = directory / "console.jsonl"
    backup = directory / "console.jsonl.owned-backup"
    async with connect(port) as client:
        identity = require_status(require_data(await invoke(client, "unity_mcp_status", {
            "forceFresh": True, "includeCapabilities": False,
        }, report)))
        report["identityBefore"] = identity
        queue = require_data(await invoke(client, "unity_queue_cleanup", {}, report))
        captures = require_data(await invoke(client, "unity_console_capture_list", {
            "activeOnly": True, "count": 20,
        }, report))
        if (queue.get("complete") is not True or queue.get("isStale") is not False or
                queue.get("items") != [] or captures.get("activeCount") != 0):
            raise CallPreflightError("Shared/unknown work prevents the owned Capture exercise.")
        discovered = require_data(await invoke(client, "unity_tools_find", {"query": "console", "limit": 200}, report))
        if any(item.get("name", "").startswith("unity_console_") and
               item.get("name", "").split("_")[-1] in {"log", "write", "emit", "append"}
               for item in discovered.get("tools", [])):
            raise CallPreflightError("Use the dedicated Console writer instead of reflection.")
        await ensure_ready(client, report)
        try:
            started = require_data(await invoke(client, "unity_console_capture_start", {
                "title": "V04-missing-" + nonce, "path": str(directory), "ownerId": "V04-missing-" + nonce,
                "requestKey": "V04-missing-" + nonce, "clearUnityConsole": False,
                "excludeUPilot": True, "includeStackTrace": False, "flushIntervalMs": 100,
                "maxFileBytes": 1024 * 1024,
            }, report))
            session_id = capture_session(started, active=True)["sessionId"]
            token = started.get("ownerToken")
            if not isinstance(token, str) or not token:
                raise CallPreflightError("Owned token missing; do not replay Start or force-stop.")
            report["ownedSessionId"] = session_id
            for index in range(3):
                await emit(client, "V04-missing-" + nonce + "-marker-" + str(index), report)
            if segment_kind != "initial":
                # Three bounded groups with real flush opportunities; no product hook or forced rotation.
                for group in range(3):
                    for index in range(12):
                        await emit(client, "V04-missing-" + nonce + "-rotate-" + str(group * 12 + index)
                                   + ":" + "x" * 65536, report)
                    require_data(await invoke(client, "unity_editor_delay", {"delayMs": 150}, report))
            current = require_status(require_data(await invoke(client, "unity_mcp_status", {
                "forceFresh": True, "includeCapabilities": False,
            }, report)))
            if current != identity:
                raise CallPreflightError("Unity/service identity changed; no old Stop or disk change dispatched.")
            stop_attempted = True
            stopped = capture_session(require_data(await invoke(client, "unity_console_capture_stop", {
                "sessionId": session_id, "ownerToken": token,
            }, report)), session_id, False)
            before, records = verify_artifacts(directory, stopped, session_id)
            report["artifactsBefore"] = before
            pieces = [Path(item["path"]) for item in before["files"] if Path(item["path"]).suffix == ".jsonl"]
            if segment_kind != "initial":
                if len(pieces) < 3:
                    raise CallPreflightError("Real rotation did not create at least three segments; no file moved.")
                segment = pieces[len(pieces) // 2 if segment_kind == "middle" else -1]
                backup = segment.with_name(segment.name + ".owned-backup")
            report["missingSegment"] = {"position": segment_kind, "name": segment.name,
                                        "index": pieces.index(segment), "segmentCount": len(pieces)}
            warm = require_data(await invoke(client, "unity_console_capture_read", {
                "sessionId": session_id, "count": 1, "contains": ["V04-missing-" + nonce],
            }, report))
            if warm.get("ok") is not True or warm.get("sessionId") != session_id or not warm.get("continuationToken"):
                raise CallPreflightError("A successful cached first page is required before moving the owned segment.")
            expected_hash = file_hash(segment)
            owned_path(backup)
            if backup.exists():
                raise CallPreflightError("Owned backup destination already exists.")
            segment.rename(backup)
            moved = True
            try:
                for extra in ({}, {"continuationToken": warm["continuationToken"]}):
                    data = require_data(await invoke(client, "unity_console_capture_read", {
                        "sessionId": session_id, "count": 1, **extra,
                    }, report))
                    require_missing(data, session_id, segment.name)
                for item in before["files"]:
                    if Path(item["path"]).name in {"session.json", "summary.json"} and file_hash(item["path"]) != item["sha256"]:
                        raise CallPreflightError("Read rewrote the historical manifest or summary.")
            except BaseException as exc:
                report.setdefault("businessError", {"type": type(exc).__name__,
                    "message": str(exc) if isinstance(exc, CallPreflightError) else "Owned Read failed; raw text omitted."})
                raise
            finally:
                try:
                    restore_segment(segment, backup, expected_hash)
                    moved = False
                    report["segmentRestored"] = True
                except BaseException as exc:
                    report["cleanupError"] = {"type": type(exc).__name__, "message": "Owned restore failed; backup retained."}
                    if not report.get("businessError"):
                        raise
            after, restored = verify_artifacts(directory, stopped, session_id)
            if after != before or restored != records:
                raise CallPreflightError("Owned Capture bytes changed across missing-file exercise.")
            recovered = require_data(await invoke(client, "unity_console_capture_read", {
                "sessionId": session_id, "count": 1, "continuationToken": warm["continuationToken"],
            }, report))
            if (recovered.get("ok") is not True or recovered.get("sessionId") != session_id or
                    recovered.get("returnedCount") != 1 or not recovered.get("logs")):
                raise CallPreflightError("The original continuation did not recover after exact restoration.")
            report["artifactsAfter"] = after
            report["result"] = "observed"
        except BaseException as exc:
            report.setdefault("businessError", {"type": type(exc).__name__,
                "message": str(exc) if isinstance(exc, CallPreflightError) else "Owned action failed; raw text omitted."})
            raise
        finally:
            if moved:
                report.setdefault("cleanupError", {"type": "RestoreUnverified", "message": "Owned backup retained; no unsafe overwrite."})
            if session_id and token and not stop_attempted:
                try:
                    async with asyncio.timeout(max(0.001, min(5, deadline - asyncio.get_running_loop().time()))):
                        now = require_status(require_data(await invoke(client, "unity_mcp_status", {
                            "forceFresh": True, "includeCapabilities": False,
                        }, report)))
                        if now != identity:
                            raise CallPreflightError("Changed lifetime; no old cleanup dispatched.")
                        stop_attempted = True
                        capture_session(require_data(await invoke(client, "unity_console_capture_stop", {
                            "sessionId": session_id, "ownerToken": token,
                        }, report)), session_id, False)
                except BaseException as exc:
                    report["cleanupError"] = {"type": type(exc).__name__, "message": "Owned cleanup was not verified; no replay."}


async def main(args):
    report = {"calls": [], "result": "unknown", "scope": "owned stopped Capture missing " + getattr(args, "segment", "initial") + " segment and cached continuation",
              "notCovered": ["physical write failure", "missing positions not selected in this run", "concurrent removal during scanning", "all Server modules loaded"]}
    end = asyncio.get_running_loop().time() + 60
    try:
        async with asyncio.timeout(55):
            await exercise(args.port, report, end, getattr(args, "segment", "initial"))
    except BaseException as exc:
        report["result"] = "unknown"
        report.setdefault("businessError", {"type": type(exc).__name__, "message": "Evidence incomplete; raw text omitted."})
    _write_observation(owned_path(args.output), _redact(report))
    return 0 if report["result"] == "observed" and not report.get("cleanupError") else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--segment", choices=("initial", "middle", "final"), default="initial")
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
