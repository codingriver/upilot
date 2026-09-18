"""Generate two bounded probe revisions and register each once over HTTP."""
import argparse
import asyncio
import hashlib
import json
import re
import time
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[a-z0-9-]+", args.label):
        raise ValueError("Use a unique lowercase evidence label.")
    root = Path(__file__).resolve().parents[2]
    project = root / "Tests~" / "UPilotTest"
    output = project / "Log" / "P0P1" / f"{args.label}.json"
    if output.exists():
        raise ValueError("Evidence already exists; never overwrite a prior attempt.")
    probes = [project / "Assets" / "UPilotAcceptance" / "Editor" / f"UPilotCompileOverlap{k}.cs"
              for k in ("A", "B")]
    originals = [path.read_bytes() for path in probes]
    hashes = [hashlib.sha256(content).hexdigest() for content in originals]
    log = project / "Log" / "mcp-server.log"
    log_offset = log.stat().st_size
    report = {"acceptancePassed": False, "projectPath": str(project), "calls": [],
              "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "beforeHashes": hashes, "startCounts": {"A": 0, "B": 0}}

    def save():
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps({"ok": True, "data": report}, indent=2) + "\n", encoding="utf-8")

    async with streamable_http_client("http://127.0.0.1:8011/mcp") as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            client = DiscoveryProxyClient(session)

            async def call(tool, arguments=None):
                response = await client.call("unity_tool_call", {"toolName": tool, "args": arguments or {}})
                report["calls"].append({"tool": tool, "args": arguments or {}, "at": int(time.time() * 1000),
                                        "response": response})
                save()
                if response.get("ok") is not True:
                    raise RuntimeError(f"{tool}: {response.get('error')}")
                return response["data"]

            def verify_project(status):
                actual = (status.get("paths") or {}).get("unityProjectAbsolute")
                if not actual or Path(actual).resolve() != project.resolve():
                    raise ValueError("Exact project identity changed.")

            def revise_probe(index):
                if probes[index].read_bytes() != originals[index]:
                    raise ValueError("Probe changed after baseline; do not overwrite concurrent edits.")
                replacement = f'internal const string Identity = "{args.label}-{index}";'.encode("ascii")
                revised, count = re.subn(rb'internal const string Identity = "[a-z0-9-]+";',
                                         replacement, originals[index])
                if count != 1 or revised == originals[index]:
                    raise ValueError("Expected one changed compile-only probe identity.")
                probes[index].write_bytes(revised)

            capture_id = None
            try:
                async with asyncio.timeout(240):
                    status = await call("unity_mcp_status", {"includeCapabilities": False})
                    verify_project(status)
                    if not status.get("connected") or not status.get("serverReady"):
                        raise ValueError("Bridge must be connected before the scenario.")
                    ready = (await call("unity_ensure_ready"))["executionState"]
                    if not ready.get("ready") or not ready.get("authoritative") or ready.get("isStale"):
                        raise ValueError("A fresh authoritative baseline is required.")
                    report["baseline"] = ready
                    capture = await call("unity_console_capture_start", {"title": args.label})
                    capture_id = (capture.get("session") or capture).get("sessionId")
                    if not capture_id:
                        raise ValueError("Console capture identity missing.")
                    revise_probe(0)
                    report["startCounts"]["A"] += 1
                    a = await call("unity_write_batch_register", {
                        "paths": [str(probes[0])], "compileWhenEditMode": True})
                    report["registeredA"] = a
                    print("A_REGISTERED " + a["writeBatchId"], flush=True)
                    # Only server-side status/persistence is used during the authorized Reload.
                    for _ in range(100):
                        status = await call("unity_mcp_status", {"includeCapabilities": False})
                        verify_project(status)
                        state = status["executionState"]
                        if state.get("writeBatchId") == a["writeBatchId"]:
                            if state.get("terminal"):
                                raise ValueError("A completed before B registration; overlap not established.")
                            if state.get("compilePhase") == "compiling" and state.get("lastCompileStartedAt", 0) > 0:
                                break
                        await asyncio.sleep(0.1)
                    else:
                        raise ValueError("A's active compile identity was not observed.")
                    report["beforeB"] = state
                    revise_probe(1)
                    report["startCounts"]["B"] += 1
                    b = await call("unity_write_batch_register", {
                        "paths": [str(probes[1])], "compileWhenEditMode": True})
                    report["registeredB"] = b
                    print("B_REGISTERED " + b["writeBatchId"], flush=True)
                    first_a = None
                    for _ in range(180):
                        a_end = await call("unity_write_batch_status", {"writeBatchId": a["writeBatchId"]})
                        b_end = await call("unity_write_batch_status", {"writeBatchId": b["writeBatchId"]})
                        if a_end.get("correlationVerified") and first_a is None:
                            first_a = a_end
                            report["firstTerminalA"] = a_end
                        if a_end.get("terminal") and b_end.get("terminal"):
                            break
                        await asyncio.sleep(1)
                    report["finalA"], report["finalB"] = a_end, b_end
                    report["checks"] = {
                        "separateBatches": a["writeBatchId"] != b["writeBatchId"],
                        "separateOperations": a["operationId"] != b["operationId"],
                        "separateFiles": a["paths"] != b["paths"] and a["filesSha256"] != b["filesSha256"],
                        "registeredDuringA": b["executionState"].get("writeBatchId") == a["writeBatchId"]
                            and b["executionState"].get("terminal") is False,
                        "compilerAlreadyStarted": state.get("compilePhase") == "compiling"
                            and 0 < state.get("lastCompileStartedAt", 0) <= b["writeBatchCreatedAt"]
                            < a_end.get("lastCompileVerifiedAt", 0),
                        "ownTerminals": all(end.get("correlationVerified") and end.get("outcome") == "passed"
                            and end["terminalSnapshot"]["writeBatchId"] == registered["writeBatchId"]
                            and end["filesSha256"] == registered["filesSha256"]
                            for registered, end in ((a, a_end), (b, b_end))),
                        "separateCompileRequests": bool(a_end.get("compileRequestId"))
                            and bool(b_end.get("compileRequestId")) and a_end["compileRequestId"] != b_end["compileRequestId"],
                        "separateCompileOperations": bool(a_end.get("compileOperationId"))
                            and bool(b_end.get("compileOperationId")) and a_end["compileOperationId"] != b_end["compileOperationId"],
                        "aUnchangedAfterB": first_a is not None and first_a == a_end,
                        "noImplicitReplacement": not a_end.get("supersededBy") and not b_end.get("supersededBy"),
                    }
            except Exception as exc:
                report["error"] = f"{type(exc).__name__}: {exc}"
            finally:
                if capture_id:
                    try:
                        stopped = await call("unity_console_capture_stop", {"sessionId": capture_id})
                        report["captureStopped"] = (stopped.get("session") or stopped).get("active") is False
                    except Exception as exc:
                        report["captureCleanupError"] = str(exc)
                with log.open("rb") as stream:
                    stream.seek(log_offset)
                    delta = stream.read()
                report["serverLogDelta"] = delta.decode("utf-8", errors="replace")
                report["compileRequestCount"] = delta.count(b">>> compile.request ")
                report["acceptancePassed"] = bool(report.get("checks")) and all(report["checks"].values()) \
                    and report["compileRequestCount"] == 2 and report.get("captureStopped") is True \
                    and not report.get("error")
                save()
                print(json.dumps({"path": str(output), "passed": report["acceptancePassed"],
                                  "checks": report.get("checks"), "error": report.get("error")}), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
