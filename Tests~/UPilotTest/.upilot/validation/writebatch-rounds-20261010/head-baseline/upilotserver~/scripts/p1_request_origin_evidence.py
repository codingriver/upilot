"""D19 opt-in, bounded two-client HTTP evidence; never stops or restarts shared work."""
import argparse
import asyncio
from contextlib import asynccontextmanager
import hashlib
import json
from pathlib import Path
import time
import uuid

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import _payload
from upilot_mcp.operation_context import ORIGIN_META_KEY
from p0_acceptance_call import CallPreflightError, _write_observation, _redact

ROOT = Path(__file__).resolve().parents[2]
PROJECT = (ROOT / "Tests~" / "UPilotTest").resolve()


@asynccontextmanager
async def connect(port):
    async with streamable_http_client(f"http://127.0.0.1:{port}/mcp") as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            yield session


async def call_envelope(session, tool, arguments, origin, report):
    # Metadata belongs to the MCP request, never the tool's strict arguments.
    result = _payload(await session.call_tool("unity_tool_call", {
        "toolName": tool, "args": arguments,
    }, meta={ORIGIN_META_KEY: origin}))
    report["calls"].append(_redact({"tool": tool, "arguments": arguments, "origin": origin,
                            "receivedAt": int(time.time() * 1000), "result": result}))
    return result


async def call(session, tool, arguments, origin, report):
    result = await call_envelope(session, tool, arguments, origin, report)
    if result.get("ok") is not True:
        raise CallPreflightError("An owned call failed; no start request is replayed.")
    return result.get("data") or {}


def check_task(data, task_id, origin):
    if data.get("taskId") != task_id or data.get("requestOrigin") != {
        "status": "self_declared", **origin,
    }:
        raise CallPreflightError("Task identity or frozen creation origin does not match.")


async def exercise(port, report):
    # B remains connected while A disconnects. C is a distinct chat label, not a
    # third transport identity; labels are expressly self-declared, not security.
    a = {"clientId": "owned-client-A", "chatId": "owned-chat-A"}
    b = {"clientId": "owned-client-B", "chatId": "owned-chat-B"}
    c = {"clientId": "owned-client-A", "chatId": "owned-chat-C"}
    owned = {}
    report["ownedTaskIds"] = []
    async with connect(port) as observer:
        async with connect(port) as starter:
            for session in (starter, observer):
                status = await call(session, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False,
                }, {}, report)
                actual = (status.get("paths") or {}).get("unityProjectAbsolute")
                if (not actual or Path(actual).resolve() != PROJECT or
                        not status.get("connected") or not status.get("serverReady")):
                    raise CallPreflightError("Exact canonical project must be connected and ready.")
                loaded = ((status.get("runtimeIdentity") or {}).get("mcpServer") or {}).get("loadedSource") or {}
                if loaded.get("status") != "equivalent":
                    raise CallPreflightError("Selected entry loading is not verified; do not create evidence tasks.")
            inventory = await call(observer, "unity_queue_cleanup", {}, {}, report)
            if inventory.get("complete") is not True or inventory.get("items"):
                raise CallPreflightError("Queue is nonempty or incompletely observed; do not interfere.")

            ready = await call(starter, "unity_ensure_ready", {
                "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual",
            }, {}, report)
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh unblocked readiness is required before the owned delay Tasks.")

            async def start(session, origin, tool, arguments):
                data = await call(session, "unity_task_start", {
                    "taskName": "D19-owned-" + origin["chatId"], "toolName": tool,
                    "toolArgs": arguments, "timeoutS": 20, "retryCount": 0,
                }, origin, report)
                task_id = data.get("taskId")
                if not isinstance(task_id, str) or not task_id or task_id in owned:
                    raise CallPreflightError("No unique owned Task identity was received; never resend.")
                owned[task_id] = origin
                report["ownedTaskIds"].append(task_id)
                check_task(data, task_id, origin)
                return task_id

            # Same tool, different chats/clients, concurrent starts.
            first, second = await asyncio.gather(
                start(starter, a, "unity_editor_delay", {"delayMs": 4000}),
                start(observer, b, "unity_editor_delay", {"delayMs": 4000}),
            )
            # Different tool and chat through A's existing HTTP client.
            await start(starter, c, "unity_mcp_status", {"forceFresh": False, "includeCapabilities": False})
            before = await call(observer, "unity_task_status", {"taskId": first, "detailLevel": "summary"}, b, report)
            check_task(before, first, a)
            report["activeBeforeDisconnect"] = before.get("terminal") is False
        report["clientADisconnectedAt"] = int(time.time() * 1000)
        after = await call(observer, "unity_task_status", {"taskId": first, "detailLevel": "summary"}, b, report)
        check_task(after, first, a)
        report["activeAfterDisconnect"] = after.get("terminal") is False
        report["shortConnectionObserved"] = report["activeBeforeDisconnect"] and report["activeAfterDisconnect"]
        # Fixed budget; polling reads only received identities and never renews a Task.
        terminal = {}
        for _ in range(25):
            for task_id, origin in owned.items():
                if task_id in terminal:
                    continue
                data = await call(observer, "unity_task_status", {"taskId": task_id, "detailLevel": "summary"}, b, report)
                check_task(data, task_id, origin)
                if data.get("terminal") is True:
                    terminal[task_id] = data
            if len(terminal) == len(owned):
                break
            await asyncio.sleep(1)
        report["terminalTasks"] = terminal
        report["allOwnedTasksObservedTerminal"] = len(terminal) == 3
        if not report["allOwnedTasksObservedTerminal"]:
            raise CallPreflightError("Owned task outcome remains unknown; do not cancel another task or replay.")
        # One later read freezes the original creation labels despite B's polling labels.
        for task_id, origin in owned.items():
            data = await call(observer, "unity_task_status", {"taskId": task_id, "detailLevel": "summary"}, b, report)
            check_task(data, task_id, origin)
            if data.get("status") != "completed" or data.get("endedAt") != terminal[task_id].get("endedAt"):
                raise CallPreflightError("Owned terminal result failed or changed after a later read.")
        if not report["shortConnectionObserved"]:
            raise CallPreflightError("Task finished before disconnect could be observed; short-connection case is uncovered.")
        report["result"] = "observed"
        report["coverage"] = "two HTTP clients; same/different tool chat labels; active task survives A disconnect"
        report["notCovered"] = ["spoof-proof identity", "late old request/blocked underlying engine",
                                "direct Capture/WriteBatch/Bridge attribution", "all Server modules loaded"]


BLOCKER_TEST = "CodingRiver.UPilot.Tests.UPilotTestSelectionTests.ExactSelectorsUnionDeduplicatesAndReportsEachMatch"


def check_blocker(envelope, task_id, origin):
    error = envelope.get("error") or {}
    blocker = (error.get("detail") or {}).get("blocker") or {}
    if (envelope.get("ok") is not False or error.get("code") != "TEST_TASK_ALREADY_ACTIVE" or
            blocker.get("type") != "Task" or blocker.get("id") != task_id or
            blocker.get("status") not in {"queued", "running", "cancel_requested", "RecoveryRequired"} or
            blocker.get("action") != "cancel" or
            blocker.get("requestOrigin") != {"status": "self_declared", **origin}):
        raise CallPreflightError("The rejection does not identify the exact owned admission blocker and frozen origin.")
    # action is a possible request, not authorization or proof that cancellation works.
    return blocker


async def exercise_blocker(port, report):
    # Use one existing pure selection leaf, not a new hold helper or production switch.
    # It may finish before B arrives: an accepted B is owned, observed, and never retried.
    from p0_reload_evidence import require_status
    a = {"clientId": "owned-client-A", "chatId": "owned-blocker-A"}
    b = {"clientId": "owned-client-B", "chatId": "owned-blocker-B"}
    owned, terminal = {}, {}
    report.update(ownedTaskIds=[], blockerObserved=False, result="unknown")
    deadline = time.monotonic() + 40  # main also enforces its original 50-second total budget
    business_error = None

    def retain_start(data, origin):
        task_id = data.get("taskId")
        if not isinstance(task_id, str) or not task_id or task_id in owned:
            raise CallPreflightError("No unique owned Task identity was received; never resend.")
        owned[task_id] = origin
        report["ownedTaskIds"].append(task_id)  # retain even if the remaining receipt is invalid
        check_task(data, task_id, origin)
        if (data.get("durable") is not True or not data.get("projectPath") or
                Path(data["projectPath"]).resolve() != PROJECT):
            raise CallPreflightError("The accepted Task is not the expected canonical durable test Task.")
        return task_id

    async with connect(port) as observer:
        async with connect(port) as starter:
            identities = []
            for session in (starter, observer):
                identities.append(require_status(await call(session, "unity_mcp_status", {
                    "forceFresh": True, "includeCapabilities": False,
                }, {}, report)))
            if identities[0] != identities[1]:
                raise CallPreflightError("The clients do not observe the same fresh project/service/domain identity.")
            inventory = await call(observer, "unity_queue_cleanup", {}, {}, report)
            if inventory.get("complete") is not True or inventory.get("items"):
                raise CallPreflightError("Queue is nonempty or incomplete; do not create blocker Tasks.")
            captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, {}, report)
            if captures.get("activeCount") != 0:
                raise CallPreflightError("An existing Capture may receive test logs; do not interfere.")
            selectors = {"testMode": "EditMode", "testNames": [BLOCKER_TEST],
                         "matchMode": "union", "requireAllSelectorsMatch": True}
            selection = await call(starter, "unity_test_list", selectors, {}, report)
            domain, snapshot = selection.get("selectionDomain"), selection.get("selectionSnapshotId")
            if (selection.get("selectionValid") is not True or selection.get("matchedCount") != 1 or
                    selection.get("tests") != [BLOCKER_TEST] or
                    not isinstance(domain, str) or not domain.strip() or
                    not isinstance(snapshot, str) or not snapshot.strip()):
                raise CallPreflightError("Discovery must select exactly the existing pure leaf with a current snapshot.")
            ready = await call(starter, "unity_ensure_ready", {
                "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual",
            }, {}, report)
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh unblocked EditMode readiness is required before owned tests.")
            start_args = {"toolName": "unity_test_run", "toolArgs": {
                **selectors, "expectedSelectionDomain": domain, "expectedSelectionSnapshotId": snapshot,
            }, "timeoutS": 20, "retryCount": 0}
            try:
                if time.monotonic() >= deadline:
                    raise CallPreflightError("Original observation budget expired before A; do not start.")
                first = retain_start(await call(starter, "unity_task_start", {
                    **start_args, "taskName": "D19-owned-blocker-A",
                }, a, report), a)
                if time.monotonic() >= deadline:
                    raise CallPreflightError("Original observation budget expired before B; do not start.")
                reply = await call_envelope(observer, "unity_task_start", {
                    **start_args, "taskName": "D19-owned-blocker-B",
                }, b, report)
                if reply.get("ok") is True:
                    retain_start(reply.get("data") or {}, b)
                    report["acceptedRace"] = True
                    raise CallPreflightError("B was accepted before a blocker could be witnessed; this case is uncovered.")
                report["blocker"] = check_blocker(reply, first, a)
                report["blockerObserved"] = True
            except Exception as exc:
                business_error = exc
                report["businessError"] = {"type": type(exc).__name__, "message": str(exc)
                    if isinstance(exc, CallPreflightError) else "Start acknowledgement unknown; never resend."}
        report["clientADisconnectedAt"] = int(time.time() * 1000)
        # Closing A is not cancellation. Only read received own IDs, no retry/cancel/maintenance.
        while owned and len(terminal) < len(owned) and time.monotonic() < deadline:
            for task_id, origin in owned.items():
                if task_id in terminal:
                    continue
                data = await call(observer, "unity_task_status", {"taskId": task_id, "detailLevel": "summary"}, b, report)
                check_task(data, task_id, origin)
                if data.get("terminal") is True:
                    terminal[task_id] = data
            if len(terminal) < len(owned):
                await asyncio.sleep(min(0.5, max(0, deadline - time.monotonic())))
        report["terminalTasks"] = terminal
        report["allOwnedTasksObservedTerminal"] = bool(owned) and len(terminal) == len(owned)
        if not report["allOwnedTasksObservedTerminal"]:
            raise CallPreflightError("Owned outcomes remain unknown within the original observation budget.")
        for task_id, origin in owned.items():
            data = await call(observer, "unity_task_status", {"taskId": task_id, "detailLevel": "summary"}, b, report)
            check_task(data, task_id, origin)
            tests = data.get("tests") or {}
            if (data.get("terminal") is not True or data.get("status") != "completed" or
                    data.get("endedAt") != terminal[task_id].get("endedAt") or
                    not isinstance(data.get("endedAt"), int) or data["endedAt"] <= 0 or
                    not data.get("runGuid") or data.get("runGuid") != terminal[task_id].get("runGuid") or
                    tests.get("status") != "completed" or tests.get("cleanupSucceeded") is not True or
                    data.get("cleanupSucceeded") is not True or tests.get("resultAuthoritative") is not True or
                    tests.get("total") != 1 or tests.get("passed") != 1 or
                    tests.get("failed") != 0 or tests.get("skipped") != 0):
                raise CallPreflightError("An owned terminal, creation origin, single-leaf outcome or cleanup is unverified.")
        identity_after = require_status(await call(observer, "unity_mcp_status", {
            "forceFresh": True, "includeCapabilities": False,
        }, {}, report))
        if identity_after != identities[0]:
            raise CallPreflightError("Service/domain identity changed; the blocker exercise is not same-lifetime evidence.")
        if business_error is not None:
            raise business_error  # successful cleanup/observation must not replace the original error
        report["result"] = "observed"
        report["coverage"] = "two HTTP clients; exact owned durable admission blocker; frozen origin after A disconnect"
        report["notCovered"] = ["arbitrary underlying engine causality", "late old request racing a successor",
                                "cancellation effectiveness/authorization", "all Server modules loaded", "spoof-proof identity"]


def verify_capture_files(directory, session, owner_token):
    """Verify only this nonce's stopped files; never edit a Capture artifact."""
    from p0_reload_evidence import owned_path
    directory = owned_path(directory)
    documents, files = {}, []
    for name in ("session.json", "summary.json"):
        path = owned_path(directory / name)
        if not path.is_file() or path.stat().st_size > 65536:
            raise CallPreflightError("Owned Capture manifest or summary is missing or unbounded.")
        raw = path.read_bytes()
        documents[name] = json.loads(raw)
        files.append({"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})
    keys = ("sessionId", "ownerId", "requestKey", "directory", "active", "finishedAtUtcMs",
            "segmentCount", "fileBytes", "sha256", "totalCount", "lastError")
    manifest = documents["session.json"]
    expected_owner_hash = hashlib.sha256(owner_token.encode("utf-8")).hexdigest()
    if (not isinstance(manifest, dict) or not isinstance(documents["summary.json"], dict) or
            any(manifest.get(key) != session.get(key) for key in keys) or
            any(manifest.get(key) != documents["summary.json"].get(key) for key in keys) or
            manifest.get("ownerTokenSha256") != expected_owner_hash or
            documents["summary.json"].get("ownerTokenSha256") != expected_owner_hash or
            manifest.get("active") is not False or manifest.get("lastError") or
            not isinstance(manifest.get("finishedAtUtcMs"), int) or manifest["finishedAtUtcMs"] <= 0 or
            Path(manifest.get("directory", "")).absolute() != directory):
        raise CallPreflightError("Owned Capture receipt, terminal files or ownership hash do not agree.")
    segments = sorted(directory.glob("console*.jsonl"),
                      key=lambda path: (path.name.lower() != "console.jsonl", str(path).lower()))
    if (not segments or len(segments) > 16 or len(segments) != manifest.get("segmentCount") or
            segments[0].name != "console.jsonl"):
        raise CallPreflightError("Owned Capture segments are missing or outside this bounded exercise.")
    combined, total = hashlib.sha256(), 0
    for path in segments:
        path = owned_path(path)
        if not path.is_file() or path.stat().st_size > 4 * 1024 * 1024:
            raise CallPreflightError("Owned Capture segment is not a bounded regular file.")
        raw = path.read_bytes()
        total += len(raw)
        if total > 8 * 1024 * 1024:
            raise CallPreflightError("Owned Capture exceeds the exercise's bounded file budget.")
        combined.update(raw)
        files.append({"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})
    if manifest.get("fileBytes") != total or manifest.get("sha256") != combined.hexdigest():
        raise CallPreflightError("Owned Capture actual JSONL bytes/hash do not match the terminal receipt.")
    return {"verified": True, "files": files, "jsonlBytes": total, "combinedSha256": combined.hexdigest()}


async def exercise_capture(port, report):
    """D19 direct Capture attribution only, not a new ownership/cancellation matrix."""
    from p0_reload_evidence import owned_path, require_status
    nonce = report.get("observationId", "")
    if not isinstance(nonce, str) or len(nonce) != 32 or any(c not in "0123456789abcdef" for c in nonce):
        raise CallPreflightError("A fresh hexadecimal nonce is required before creating an owned Capture.")
    directory = owned_path(PROJECT / "Log" / "UPilotConsole" / ("d19-origin-" + nonce))
    if directory.exists():
        raise CallPreflightError("Owned Capture directory already exists; do not reuse a prior exercise.")
    owner_id, request_key = "D19-owned-" + nonce, "D19-capture-" + nonce
    a = {"clientId": "owned-client-A", "chatId": "owned-capture-A"}
    b = {"clientId": "owned-client-B", "chatId": "owned-capture-B"}
    report.update(result="unknown", captureOriginObserved=False, startAttempted=False,
                  stopAttempted=False, cleanupVerified=False, ownedCaptureId="")
    identity, session_id, owner_token = None, "", ""  # secrets stay in memory, never in report
    business_error, cleanup_error = None, None
    async with connect(port) as observer:
        async with connect(port) as starter:
            try:
                # Reserve cleanup time inside main's original 50-second budget.
                async with asyncio.timeout(35):
                    identities = [require_status(await call(session, "unity_mcp_status", {
                        "forceFresh": True, "includeCapabilities": False,
                    }, origin, report)) for session, origin in ((starter, a), (observer, b))]
                    if identities[0] != identities[1]:
                        raise CallPreflightError("The clients do not observe the same verified service/domain.")
                    identity = identities[0]
                    inventory = await call(observer, "unity_queue_cleanup", {}, b, report)
                    if inventory.get("complete") is not True or inventory.get("items") != []:
                        raise CallPreflightError("Queue is nonempty or incomplete; do not create a Capture.")
                    captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, b, report)
                    if captures.get("activeCount") != 0:
                        raise CallPreflightError("Another Capture is active; do not interfere.")
                    ready = await call(starter, "unity_ensure_ready", {
                        "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual",
                    }, a, report)
                    if (ready.get("ready") is not True or ready.get("blocked") is not False or
                            ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                        raise CallPreflightError("Fresh unblocked EditMode is required before owned Capture start.")
                    report["startAttempted"] = True
                    started = await call(starter, "unity_console_capture_start", {
                        "title": owner_id, "path": str(directory), "ownerId": owner_id,
                        "requestKey": request_key, "clearUnityConsole": False,
                        "allowOutsideProject": False, "excludeUPilot": True,
                        "flushIntervalMs": 100, "maxFileBytes": 1024 * 1024,
                    }, a, report)
                    capture = started.get("session") or {}
                    token = started.get("ownerToken")
                    received_id = capture.get("sessionId")
                    if (started.get("ok") is not True or not isinstance(received_id, str) or not received_id or
                            not isinstance(token, str) or not token or capture.get("ownerId") != owner_id or
                            capture.get("requestKey") != request_key or
                            Path(capture.get("directory", "")).absolute() != directory):
                        raise CallPreflightError("Start ownership is unverified; never resend or force-stop.")
                    session_id, owner_token = received_id, token
                    report["ownedCaptureId"] = session_id
                    if capture.get("active") is not True:
                        raise CallPreflightError("Owned Capture did not publish a verified active state.")
                    observations = []
                    for session, origin in ((observer, b), (starter, a)):
                        inventory = await call(session, "unity_queue_cleanup", {}, origin, report)
                        items = inventory.get("items") or []
                        selected = [item for item in items if item.get("type") == "Capture" and item.get("id") == session_id]
                        if (inventory.get("complete") is not True or len(selected) != 1 or
                                selected[0].get("status") != "active" or selected[0].get("requestOrigin") != {
                                    "status": "unknown", "clientId": "", "chatId": ""}):
                            raise CallPreflightError("Direct Capture is absent, incomplete or attributed to a polling client.")
                        observations.append({"poller": origin, "item": selected[0]})
                    report.update(captureOriginObserved=True, captureOriginObservations=observations)
            except BaseException as exc:
                business_error = exc
                report["businessError"] = {"type": type(exc).__name__, "message": str(exc)
                    if isinstance(exc, CallPreflightError) else "Owned exercise failed; raw exception omitted."}
            finally:
                if session_id and owner_token:
                    try:
                        async with asyncio.timeout(10):
                            current = require_status(await call(starter, "unity_mcp_status", {
                                "forceFresh": True, "includeCapabilities": False,
                            }, a, report))
                            if current != identity:
                                raise CallPreflightError("Service/domain changed; do not dispatch an uncertain cleanup.")
                            report["stopAttempted"] = True
                            stopped = await call(starter, "unity_console_capture_stop", {
                                "sessionId": session_id, "ownerToken": owner_token, "forceStop": False,
                            }, a, report)
                            capture = stopped.get("session") or {}
                            if (stopped.get("ok") is not True or stopped.get("terminal") is not True or
                                    capture.get("sessionId") != session_id or capture.get("active") is not False):
                                raise CallPreflightError("Owned Capture stop is not a verified terminal; never resend.")
                            report["captureFiles"] = verify_capture_files(directory, capture, owner_token)
                            inventory = await call(observer, "unity_queue_cleanup", {}, b, report)
                            captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, b, report)
                            if (inventory.get("complete") is not True or inventory.get("items") != [] or
                                    captures.get("activeCount") != 0):
                                raise CallPreflightError("Final inventory/Capture state is not verified empty.")
                            report["cleanupVerified"] = True
                    except BaseException as exc:
                        cleanup_error = exc
                        report["cleanupError"] = {"type": type(exc).__name__, "message": str(exc)
                            if isinstance(exc, CallPreflightError) else "Owned cleanup failed; raw exception omitted."}
                elif report["startAttempted"]:
                    report["cleanupNotAttempted"] = "Start ownership unknown; no replay or force-stop."
            if business_error is not None:
                raise business_error
            if cleanup_error is not None:
                raise cleanup_error
            report["result"] = "observed"


async def exercise_bridge(port, report):
    """One existing finite delay; queue IDs are candidates until requestId matches."""
    from p0_reload_evidence import require_status
    a = {"clientId": "owned-client-A", "chatId": "owned-bridge-A"}
    b = {"clientId": "owned-client-B", "chatId": "owned-bridge-B"}
    began = time.monotonic()
    report.update(startAttempted=False, bridgeOriginObserved=False, cleanupVerified=False,
                  ownedCommandId="", bridgeOriginObservations=[],
                  coverage="two HTTP clients; direct finite Bridge command; anonymous creation origin",
                  notCovered=["WriteBatch origin", "cancellation/authorization", "late successor race",
                              "all Server modules loaded", "cross-domain execution history"])
    work, response, candidate_id, identity = None, None, "", None
    business_error, cleanup_error = None, None
    async with connect(port) as observer:
        async with connect(port) as starter:
            try:
                # A fixed budget for the exercise, with a separate reserved natural-wait tail.
                async with asyncio.timeout_at(began + 30):
                    identities = [require_status(await call(session, "unity_mcp_status", {
                        "forceFresh": True, "includeCapabilities": False,
                    }, origin, report)) for session, origin in ((starter, a), (observer, b))]
                    if identities[0] != identities[1]:
                        raise CallPreflightError("Clients do not observe the same verified service/domain.")
                    identity = identities[0]
                    inventory = await call(observer, "unity_queue_cleanup", {}, b, report)
                    captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, b, report)
                    if (inventory.get("complete") is not True or inventory.get("items") != [] or
                            captures.get("activeCount") != 0):
                        raise CallPreflightError("Queue/Capture is nonempty or unknown; do not start a delay.")
                    ready = await call(starter, "unity_ensure_ready", {
                        "timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual",
                    }, a, report)
                    if (ready.get("ready") is not True or ready.get("blocked") is not False or
                            ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                        raise CallPreflightError("Fresh unblocked EditMode is required before the finite delay.")
                    report["startAttempted"] = True
                    # Shield the single in-flight request: a failed observation never cancels/replays it.
                    work = asyncio.create_task(call_envelope(starter, "unity_editor_delay", {"delayMs": 15000}, a, report))
                    for session, origin in ((observer, b), (starter, a)):
                        while True:
                            inventory = await call(session, "unity_queue_cleanup", {}, origin, report)
                            if inventory.get("complete") is not True:
                                raise CallPreflightError("Bridge queue observation is incomplete.")
                            selected = [item for item in inventory.get("items", [])
                                        if item.get("type") in ("Command", "BridgeCommand")
                                        and item.get("name") == "editor.delay"]
                            if selected:
                                if (len(selected) != 2 or {item.get("type") for item in selected} != {"Command", "BridgeCommand"}
                                        or len({item.get("id") for item in selected}) != 1 or not selected[0].get("id")
                                        or any(item.get("status") not in ("pending", "sent", "running") if item["type"] == "Command"
                                               else item.get("status") not in ("active", "queued", "executing") for item in selected)):
                                    raise CallPreflightError("Delay candidates are ambiguous or not matched on both sides.")
                                current_id = selected[0]["id"]
                                if candidate_id and current_id != candidate_id:
                                    raise CallPreflightError("Polling clients observed different candidate commands.")
                                candidate_id = current_id
                                if any(item.get("requestOrigin") != {"status": "unknown", "clientId": "", "chatId": ""}
                                       for item in selected):
                                    raise CallPreflightError("A polling label contaminated the anonymous command origin.")
                                report["bridgeOriginObservations"].append({"poller": origin, "items": selected})
                                break
                            if work.done():
                                raise CallPreflightError("Delay ended without two live queue observations; do not replay.")
                            await asyncio.sleep(0.1)
                    response = await asyncio.shield(work)
                    if response.get("ok") is not True or (response.get("data") or {}).get("state") != "delayed_15000ms":
                        raise CallPreflightError("Original delay outcome is not successful; do not resend.")
                    operation = await call(observer, "unity_operation_get", {"commandId": candidate_id}, b, report)
                    facts = operation.get("serverCommandObservation") or {}
                    report["commandObservation"] = operation
                    if (not response.get("requestId") or facts.get("requestId") != response["requestId"] or
                            facts.get("commandId") != candidate_id or facts.get("commandName") != "editor.delay" or
                            facts.get("source") != "server_command_record" or facts.get("status") != "observed" or
                            not facts.get("serviceLifecycleId") or facts.get("recordStatus") != "success" or
                            facts.get("sendAttempts") != 1 or
                            operation.get("commandId") != candidate_id or operation.get("commandName") != "editor.delay" or
                            operation.get("terminal") is not True or operation.get("resultReported") is not True or
                            not operation.get("completedAt") or operation.get("errorCode") or operation.get("errorMessage") or
                            any(operation.get(key) != 1 for key in ("dispatchCount", "executionCount", "mainThreadActionCount"))):
                        raise CallPreflightError("Candidate is not the original request with verified single-entry terminal facts.")
                    report.update(ownedCommandId=candidate_id, bridgeOriginObserved=True)
            except BaseException as exc:
                business_error = exc
                report["businessError"] = {"type": type(exc).__name__, "message": str(exc)
                    if isinstance(exc, CallPreflightError) else "Observation failed; raw exception omitted."}
            finally:
                if work is not None:
                    try:
                        async with asyncio.timeout_at(began + 45):
                            # Only await the original request. No cancel, cleanup command or replay.
                            response = await asyncio.shield(work)
                            report["delayAcknowledged"] = response.get("ok") is True and (
                                response.get("data") or {}).get("state") == "delayed_15000ms"
                            if report["delayAcknowledged"] is not True:
                                raise CallPreflightError("Original finite delay acknowledgement is unknown or failed.")
                            current = require_status(await call(observer, "unity_mcp_status", {
                                "forceFresh": True, "includeCapabilities": False,
                            }, b, report))
                            inventory = await call(observer, "unity_queue_cleanup", {}, b, report)
                            captures = await call(observer, "unity_console_capture_list", {"activeOnly": True}, b, report)
                            if (current != identity or inventory.get("complete") is not True or
                                    inventory.get("items") != [] or captures.get("activeCount") != 0):
                                raise CallPreflightError("Natural completion lacks same-lifetime empty queue/Capture evidence.")
                            report["cleanupVerified"] = True
                    except BaseException as exc:
                        cleanup_error = exc
                        report["cleanupError"] = {"type": type(exc).__name__, "message": str(exc)
                            if isinstance(exc, CallPreflightError) else "Natural wait failed; raw exception omitted."}
                        if not work.done():
                            report["outcomeUnknown"] = "Original request still in flight; never cancel/replay or claim cleanup."
            if business_error is not None:
                raise business_error
            if cleanup_error is not None:
                raise cleanup_error
            report["result"] = "observed"


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--execute", action="store_true", help="Explicitly start this mode's owned finite Tasks, Capture or Bridge delay")
    parser.add_argument("--mode", choices=("origin", "blocker", "capture", "bridge"), default="origin")
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "startedAtUtcMs": int(time.time() * 1000),
              "scope": "D19 owned two-client evidence only", "mode": args.mode, "calls": [], "result": "unknown"}
    error = None
    try:
        async with asyncio.timeout(50):
            await {"origin": exercise, "blocker": exercise_blocker, "capture": exercise_capture, "bridge": exercise_bridge}[args.mode](args.port, report)
    except BaseException as exc:
        error = exc
        report["error"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                           else "Call failed; raw protocol/exception text deliberately omitted."}
    finally:
        report["endedAtUtcMs"] = int(time.time() * 1000)
        from p0_reload_evidence import owned_path
        output, size, digest = _write_observation(owned_path(PROJECT / "Log" / "P0P1" / ("d19-origin-" + nonce + ".json")), report)
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return 1 if error else 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
