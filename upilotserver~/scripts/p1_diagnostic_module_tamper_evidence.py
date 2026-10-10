"""N01 opt-in source/reference tamper evidence. One Run; no mode switch or replay."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import re
import time
import uuid

from p0_acceptance_call import CallPreflightError, _write_observation
from p1_diagnostic_module_evidence import data, identity, invoke
from p1_request_origin_evidence import connect, PROJECT

PROBE = "CodingRiver.UPilot.Tests.UPilotDiagnosticModuleTamperProbe"
EXPECTED = {
    "source-during-compile": "MODULE_SOURCE_CHANGED",
    "reference-during-compile": "MODULE_REFERENCE_CHANGED",
    "reference-mvid-before-compile": "MODULE_REFERENCE_IDENTITY",
}


def require(condition, message):
    if not condition:
        raise CallPreflightError(message)


def nonnegative(value):
    return type(value) is int and value >= 0


def detail(envelope):
    value = envelope.get("data") if envelope.get("ok") is True else (envelope.get("error") or {}).get("detail")
    return value if isinstance(value, dict) else {}


def returned_report(terminal):
    require(terminal.get("ok") is True, "Original reflection failed; never replay.")
    value = detail(terminal)
    require(value.get("terminal") is True and value.get("status") == "Succeeded", "No successful reflection terminal.")
    result = value.get("result") or {}
    require(result.get("typeName") == PROBE and result.get("methodName") == "Run" and
            result.get("wasAwaitable") is True and result.get("awaitableStatus") == "Completed",
            "Original reflection result is not the expected awaited probe.")
    raw = result.get("result")
    require(isinstance(raw, str), "Probe JSON return is missing.")
    encoded = (result.get("resultValue") or {}).get("valueJson")
    require(isinstance(encoded, str) and json.loads(encoded) == raw, "Typed and string return receipts disagree.")
    value = json.loads(raw)
    require(isinstance(value, dict), "Probe report must be an object.")
    return value


def verify_report(value, nonce, unity_pid):
    """Read only this nonce's files and its two precisely identified candidate sources."""
    folder = PROJECT / "Log" / "P0P1" / ("n01-tamper-" + nonce)
    require(value.get("nonce") == nonce and Path(value.get("project", "")).resolve() == PROJECT and
            type(value.get("unityPid")) is int and value["unityPid"] == unity_pid, "Probe identity mismatch.")
    require(value.get("succeeded") is True and value.get("ownedOnly") is True and
            value.get("compilerStillRunning") is False and value.get("firstError") == "" and
            value.get("cleanupErrors") == [], "Probe reported failure or incomplete owned cleanup.")
    require(type(value.get("startedAt")) is int and type(value.get("endedAt")) is int and
            0 < value["startedAt"] <= value["endedAt"], "Probe timing is incomplete.")
    close = value.get("sessionClose") or {}
    require(bool(close.get("sessionId")) and close.get("closed") is True and close.get("cleanupErrors") == [] and
            type(close.get("activeAsyncOperations")) is int and close["activeAsyncOperations"] == 0 and
            type(close.get("asyncOperationsStillRunning")) is int and close["asyncOperationsStillRunning"] == 0,
            "Owned session was not closed with all async work stopped.")
    scenarios = value.get("scenarios")
    require(isinstance(scenarios, list) and len(scenarios) == 3 and all(isinstance(s, dict) for s in scenarios),
            "Exactly three real tamper scenarios are required.")
    by_name = {s.get("name"): s for s in scenarios}
    require(set(by_name) == set(EXPECTED), "Missing or duplicate tamper scenario.")
    sources = {}
    for name, code in EXPECTED.items():
        scenario = by_name[name]
        require(scenario.get("rejectionCode") == code and all(scenario.get(k) is True for k in
                ("rejectedBeforeLoad", "invocationNotStarted", "originalReferenceRestored")),
                "Tamper did not establish rejection, no invocation and reference restoration: " + name)
        before, after = scenario.get("before") or {}, scenario.get("after") or {}
        require(all(nonnegative(v.get(k)) for v in (before, after) for k in ("loadedAssemblyCount", "loadedAssemblyBytes")) and
                all(before[k] == after[k] for k in ("loadedAssemblyCount", "loadedAssemblyBytes")),
                "Rejected candidate changed resident DLL accounting.")
        require(after.get("compilerStillRunning") is False and after.get("activeInvocation") is False and
                after.get("sideEffectsMayHaveOccurred") is False and not after.get("invocationId") and after.get("cleanupErrors") == [],
                "Rejected candidate loaded/invoked or has unknown cleanup.")
        require(all(before.get(k) == after.get(k) for k in
                ("projectPath", "domainGeneration", "playSessionId", "sessionId", "moduleId")) and
                Path(after.get("projectPath", "")).resolve() == PROJECT and after.get("domainGeneration") and after.get("playSessionId"),
                "Candidate lifetime changed.")
        if name != "reference-mvid-before-compile":
            require(before.get("sessionId") == close["sessionId"] and before.get("moduleId") == nonce + "-" +
                    ("source" if name.startswith("source") else "reference") and
                    before.get("compilationStatus") == "compiling" and before.get("compilerStillRunning") is True and
                    after.get("compilationStatus") == "failed" and code in after.get("compilationError", "") and
                    type(after.get("revision")) is int and after["revision"] == 0, "No live rejected compile was observed.")
            candidate = Path(scenario.get("candidateSource", "")).resolve()
            base = PROJECT / ".upilot" / "diagnostic-modules"
            require(candidate.parent.parent == base and re.fullmatch(r"UPilotDiagnostic_[0-9a-f]{32}", candidate.parent.name) and
                    candidate.name == candidate.parent.name + ".cs", "Candidate source escaped its exact module directory.")
            sources[name + "-source-final"] = candidate
        else:
            require(before.get("moduleId") == "" and after.get("moduleId") == "", "MVID failure created a module.")
    require(all(isinstance(value.get(k), str) and re.fullmatch(r"[0-9a-f]{32}", value[k]) for k in
                ("referenceMvid", "changedMvid")) and value["referenceMvid"] != value["changedMvid"], "No distinct MVID evidence.")
    require(value.get("loadedMvidAtIdentityCheck") == value["referenceMvid"], "Disk-only MVID mismatch was not established against unchanged loaded metadata.")
    files = {
        "reference-original": "reference-original.dll", "reference-restored": "OwnedReference.dll",
        "reference-during-compile-changed": "reference-content-changed.dll", "reference-mvid-changed": "reference-changed-mvid.dll",
        "source-during-compile-source-original": "source-during-compile-original.cs",
        "reference-during-compile-source-original": "reference-during-compile-original.cs",
    }
    expected_paths = {role: (folder / name).resolve() for role, name in files.items()} | sources
    artifacts = value.get("artifacts")
    require(isinstance(artifacts, list) and all(isinstance(a, dict) for a in artifacts) and
            len(artifacts) == len(expected_paths) and {a.get("role") for a in artifacts} == set(expected_paths),
            "Artifact matrix is missing or duplicated.")
    observed, content = [], {}
    for artifact in artifacts:
        role = artifact["role"]
        path = Path(artifact.get("path", "")).resolve()
        require(path == expected_paths[role], "Artifact is outside its exact owned path: " + role)
        raw = path.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        require(nonnegative(artifact.get("bytes")) and artifact["bytes"] == len(raw) and artifact.get("sha256") == digest,
                "Artifact bytes/hash do not match the actual file: " + role)
        content[role] = raw
        observed.append({"role": role, "path": str(path), "bytes": len(raw), "sha256": digest})
    original = content["reference-original"]
    require(original and content["reference-restored"] == original and content["reference-mvid-changed"] != original and
            content["reference-during-compile-changed"] == original[:-1] + bytes([original[-1] ^ 1]),
            "Reference mutation/restoration bytes are not the expected real files.")
    require(content["source-during-compile-source-final"] == content["source-during-compile-source-original"] +
            ("\n// owned tamper " + nonce + "\n").encode() and
            content["reference-during-compile-source-final"] == content["reference-during-compile-source-original"],
            "Source mutation was not observed, or the reference scenario changed source too.")
    report_path = folder / "report.json"
    raw = report_path.read_bytes()
    require(json.loads(raw) == value, "Disk report differs from the original reflection return.")
    return {"scenariosVerified": list(EXPECTED), "artifacts": observed,
            "report": {"path": str(report_path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}}


async def run(port, nonce):
    require(isinstance(nonce, str) and re.fullmatch(r"[0-9a-f]{32}", nonce), "A fresh lowercase 32-hex nonce is required.")
    require(not (PROJECT / "Log" / "P0P1" / ("n01-tamper-" + nonce)).exists(), "Nonce already used; never replay.")
    report = {"observationId": uuid.uuid4().hex, "nonce": nonce, "calls": [], "succeeded": False,
              "firstError": "", "cleanupErrors": [], "replayAttempted": False,
              "scope": "Current Windows/Unity; local real module service and owned references; selected-entry loading only, not full Server attestation."}
    deadline = time.monotonic() + 180
    try:
        async with asyncio.timeout(180):
            async with connect(port) as session:
                original = identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report)))
                report["initialIdentity"] = original
                inventory = data(await invoke(session, "unity_queue_cleanup", {}, report))
                require(inventory.get("complete") is True and inventory.get("items") == [], "Shared inventory active or incomplete.")
                capture = data(await invoke(session, "unity_console_capture_list", {"activeOnly": True}, report))
                require(type(capture.get("activeCount")) is int and capture["activeCount"] == 0, "Another Capture is active or unknown.")
                tests = data(await invoke(session, "unity_test_status", {}, report))
                require(tests.get("isRunning") is False and tests.get("cleanupPending") is not True, "Runner activity is not known idle.")
                ready = data(await invoke(session, "unity_ensure_ready", {"requiredEditorMode": "play", "editorModeControl": "manual", "timeoutS": 5}, report))
                require(ready.get("ready") is True and ready.get("blocked") is False and ready.get("contextAuthoritative") is True and
                        ready.get("contextStale") is False, "Require fresh unpaused PlayMode; no automatic switch.")
                require(identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report))) == original,
                        "Identity changed during preflight; Run was not dispatched.")
                initial = data(await invoke(session, "unity_reflection_call", {"typeName": PROBE, "methodName": "Run",
                    "isStatic": True, "parameters": [nonce], "parameterTypeNames": ["System.String"], "forceAsync": True,
                    "awaitMode": "always", "awaitTimeoutMs": 30000, "operationTimeoutSec": 150}, report))
                operation = initial.get("operationId")
                if isinstance(operation, str) and operation.startswith("reflection-op-"):
                    report["operationId"] = operation
                require(isinstance(operation, str) and operation.startswith("reflection-op-") and initial.get("executedOnce") is True,
                        "No original reflection handle; outcome unknown, do not dispatch again.")
                while time.monotonic() < deadline:
                    terminal = await invoke(session, "unity_reflection_operation_status", {"operationId": operation}, report)
                    value = detail(terminal)
                    require(value.get("operationId") == operation, "Original reflection handle missing or changed; never replay.")
                    if value.get("terminal") is True:
                        report["reflectionTerminal"] = terminal
                        break
                    require(value.get("status") == "Running", "Original reflection state unknown; never replay.")
                    await asyncio.sleep(0.25)
                else:
                    raise CallPreflightError("Original observation window ended; retain operationId and inspect it, never restart Run.")
                returned = returned_report(terminal)
                report["probeReport"] = returned
                require(returned.get("succeeded") is True, "Owned probe failed: " + str(returned.get("firstError", "unknown")))
                report["finalIdentity"] = identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report)))
                require(report["finalIdentity"] == original, "Identity changed during the probe; retain original evidence.")
                report["verification"] = verify_report(returned, nonce, original[0])
                report["succeeded"] = True
    except Exception as ex:
        report["succeeded"] = False
        # TaskGroup can wrap the business/preflight failure. Preserve its first leaf,
        # not the uninformative "unhandled errors in a TaskGroup" envelope.
        while isinstance(ex, BaseExceptionGroup) and ex.exceptions:
            ex = ex.exceptions[0]
        report["firstError"] = type(ex).__name__ + ": " + str(ex)
    report["endedAt"] = int(time.time() * 1000)
    return report


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--execute", action="store_true", help="Opt in to one owned probe in existing PlayMode.")
    args = parser.parse_args(argv)
    if not args.execute:
        parser.error("--execute is required; this script never switches modes, restarts or replays.")
    nonce = uuid.uuid4().hex
    report = await run(args.port, nonce)
    path, size, digest = _write_observation(PROJECT / "Log" / "P0P1" / ("n01-tamper-observation-" + nonce + ".json"), report)
    print(json.dumps({"succeeded": report["succeeded"], "firstError": report["firstError"],
                      "operationId": report.get("operationId"), "path": str(path), "bytes": size, "sha256": digest}))
    return 0 if report["succeeded"] else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
