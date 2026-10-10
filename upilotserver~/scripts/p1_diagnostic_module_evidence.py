"""N01 opt-in PlayMode evidence. No mode switch, Reload, service restart or request replay."""
import argparse
import asyncio
import json
from pathlib import Path
import time
import uuid

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p1_request_origin_evidence import connect, PROJECT
from upilot_mcp.client_probe import _payload


async def invoke(session, tool, arguments, report):
    # Keep every received receipt; an exception never triggers a second mutation.
    envelope = _payload(await session.call_tool("unity_tool_call", {"toolName": tool, "args": arguments}))
    report["calls"].append(_redact({"tool": tool, "arguments": arguments, "result": envelope,
                                    "receivedAt": int(time.time() * 1000)}))
    if tool == "diagnostic_module" and arguments.get("moduleId") and envelope.get("ok") is True:
        check_module(data(envelope), arguments)
    return envelope


def check_module(value, arguments):
    if any(value.get(field) != arguments[expected] for field, expected in (
            ("moduleId", "moduleId"), ("sessionId", "sessionId"),
            ("projectPath", "expectedProjectPath"), ("domainGeneration", "expectedDomainGeneration"),
            ("playSessionId", "expectedPlaySessionId"))):
        raise CallPreflightError("Module identity changed; do not route old work to a new lifetime.")


def data(envelope):
    if envelope.get("ok") is not True or not isinstance(envelope.get("data"), dict):
        raise CallPreflightError("Owned request failed or has no trustworthy receipt; do not replay.")
    return envelope["data"]


def identity(status):
    state = status.get("executionState") or {}
    process = status.get("session") or {}
    runtime = status.get("runtimeIdentity") or {}
    server = runtime.get("mcpServer") or {}
    unity = runtime.get("unityEditor") or {}
    loaded = server.get("loadedSource") or {}
    relevant = {(item.get("module"), item.get("entry")): item.get("status")
                for item in loaded.get("entries", [])}
    # Gate only this matrix's actual entries. The raw aggregate remains in the report;
    # unrelated Task differences are not proof that either N01 entry is stale.
    required = (("upilot_mcp.domain.execution_service", "ExecutionDomainService.diagnostic_module"),
                ("upilot_mcp.mcp_tools.execution_tools", "diagnostic_module"))
    # Global readiness is EditMode-oriented. A fresh PlayMode-only block is
    # compatible with the dedicated requiredEditorMode="play" readiness check below.
    mode_ready = state.get("ready") is True or (
        state.get("blocked") is True and state.get("blockedReason") == "PlayMode" and
        state.get("compilePhase") == "completed" and state.get("isCompiling") is False and
        state.get("isUpdating") is False)
    project = (status.get("paths") or {}).get("unityProjectAbsolute")
    pid = process.get("processId")  # A reported PID is not a verified OS identity fallback.
    created = process.get("processCreatedAt")
    if (not project or Path(project).resolve() != PROJECT or status.get("connected") is not True or
            status.get("serverReady") is not True or not mode_ready or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            state.get("playModeState") != "play" or state.get("isPaused") is not False or
            process.get("identityVerified") is not True or not isinstance(pid, int) or pid <= 0 or
            isinstance(pid, bool) or not isinstance(created, int) or isinstance(created, bool) or created <= 0 or
            unity.get("verified") is not True or unity.get("processId") != pid or
            not process.get("sessionId") or unity.get("bridgeSessionId") != process.get("sessionId") or
            not isinstance(server.get("processId"), int) or isinstance(server.get("processId"), bool) or server["processId"] <= 0 or
            not state.get("producerEpoch") or not state.get("domainGeneration") or
            any(relevant.get(k) != "equivalent" for k in required)):
        raise CallPreflightError("Require exact verified canonical PlayMode and both loaded N01 entries; never switch modes.")
    return (pid, created, server["processId"], state["producerEpoch"], state["domainGeneration"], process["sessionId"])


def source(version, axis="x", asynchronous=False):
    # Both versions intentionally have the same Entry/DTO names. They must bind in their own Assembly.
    signature = "async Task<string>" if asynchronous else "string"
    body = "await Task.Delay(10000, context.Cancellation);" if asynchronous else """
        if (!registered) {
            context.SubscribeEditorUpdate(() => {
                if (go != null) { var p = go.transform.localPosition; p.AXIS += 1; go.transform.localPosition = p; }
            });
            registered = true;
        }
    """.replace("AXIS", axis)
    return """
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using CodingRiver.UPilot;
public static class OwnedDiagnosticEntry {
    static bool registered;
    [Serializable] public sealed class Receipt { public int version; public ulong instanceId; public float x; public float y; }
    public static SIGNATURE Run(DiagnosticModuleContext context, string argsJson) {
        var wireId = ulong.Parse(argsJson, System.Globalization.CultureInfo.InvariantCulture);
        var go = UPilotGameObjectService.FindByInstanceId(wireId);
        if (go == null) throw new InvalidOperationException("Owned object is missing.");
        BODY
        return JsonUtility.ToJson(new Receipt { version = VERSION, instanceId = UPilotEntityIds.ToWireId(go),
            x = go.transform.localPosition.x, y = go.transform.localPosition.y });
    }
}
""".replace("SIGNATURE", signature).replace("BODY", body).replace("VERSION", str(version))


async def observe(session, arguments, report, deadline, compiling=False, invocation_id=None):
    while time.monotonic() < deadline:
        value = data(await invoke(session, "diagnostic_module", {**arguments, "action": "status"}, report))
        if invocation_id is not None and value.get("invocationId") != invocation_id:
            raise CallPreflightError("The original invocation identity changed.")
        pending = value.get("compilerStillRunning") if compiling else value.get("activeInvocation")
        if pending is False:
            if value.get("compilationStatus") != "completed" or (not compiling and value.get("status") != "completed"):
                raise CallPreflightError("Original module work failed; retain its receipt and do not replay.")
            return value
        await asyncio.sleep(0.25)
    raise CallPreflightError("Original observation budget ended; physical compiler/code may still be running.")


def receipt(value, version, object_id):
    try:
        result = json.loads(value["result"])
    except (KeyError, ValueError, TypeError):
        raise CallPreflightError("Module did not return the expected owned JSON receipt.") from None
    if not isinstance(result, dict) or result.get("version") != version or result.get("instanceId") != object_id:
        raise CallPreflightError("Entry revision or retained PlayMode object identity changed.")
    return result


async def exercise(port, report):
    deadline = time.monotonic() + 200  # fixed total, including five seconds reserved for owned cleanup
    arguments = None
    owner_session = None
    object_id = None
    original_identity = None
    observed = False
    report.update(calls=[], result="unknown", cleanupErrors=[])
    async with connect(port) as session:
        try:
            original_identity = identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report)))
            inventory = data(await invoke(session, "unity_queue_cleanup", {}, report))
            if inventory.get("complete") is not True or inventory.get("items"):
                raise CallPreflightError("Shared queue is active or unknown; do not interfere.")
            capture = data(await invoke(session, "unity_console_capture_list", {"activeOnly": True}, report))
            if capture.get("activeCount") != 0:
                raise CallPreflightError("Another Capture is active or unknown; do not take ownership.")
            ready = data(await invoke(session, "unity_ensure_ready", {"requiredEditorMode": "play",
                "editorModeControl": "manual", "timeoutS": 5}, report))
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh PlayMode readiness is required; no automatic mode switch.")
            module_root = data(await invoke(session, "diagnostic_module", {}, report))
            if (module_root.get("projectPath") != str(PROJECT) or not module_root.get("domainGeneration") or
                    not module_root.get("playSessionId")):
                raise CallPreflightError("Module root identity is incomplete or belongs to another project.")
            opened = data(await invoke(session, "execution_session", {"action": "open", "title": "N01-owned",
                "ttlSec": 240, "maxAsyncOperations": 8}, report))
            owner_session = opened.get("sessionId")  # Retain the owner even if its next field is invalid.
            if (not owner_session or opened.get("closed") is not False or
                    opened.get("domainGeneration") != module_root["domainGeneration"]):
                raise CallPreflightError("Owned session and module execution-domain identity do not match.")
            arguments = {"sessionId": owner_session, "moduleId": "n01_" + uuid.uuid4().hex,
                "expectedProjectPath": module_root["projectPath"],
                "expectedDomainGeneration": module_root["domainGeneration"],
                "expectedPlaySessionId": module_root["playSessionId"], "allowFullCSharp": True}
            created = data(await invoke(session, "unity_gameobject_create", {"name": arguments["moduleId"]}, report))
            object_id = created.get("instanceId")
            if isinstance(object_id, bool) or not isinstance(object_id, (int, str)) or not str(object_id).isdigit() or not 0 < int(object_id) < 2**64:
                raise CallPreflightError("No exact owned object ID was received; never create it twice.")
            object_id = int(object_id)
            report["ownedObjectId"] = object_id
            for version, axis in ((1, "x"), (2, "y")):
                code = source(version, axis)
                compiled = data(await invoke(session, "diagnostic_module", {**arguments, "action": "compile",
                    "source": code, "entryType": "OwnedDiagnosticEntry"}, report))
                if compiled.get("compilerStillRunning") is not False:
                    compiled = await observe(session, arguments, report, min(deadline - 5, time.monotonic() + 65), compiling=True)
                if compiled.get("compilationStatus") != "completed":
                    raise CallPreflightError("Unity refused or failed the independent compiler; this platform is not verified.")
                before = data(await invoke(session, "diagnostic_module", {**arguments, "action": "run", "argsJson": str(object_id)}, report))
                first = receipt(before, version, object_id)
                await asyncio.sleep(0.4)
                after = data(await invoke(session, "diagnostic_module", {**arguments, "action": "run", "argsJson": str(object_id)}, report))
                second = receipt(after, version, object_id)
                if second[axis] <= first[axis] or (version == 2 and second["x"] != first["x"]):
                    raise CallPreflightError("Owned update did not advance or old revision subscription was not removed.")
                cached = data(await invoke(session, "diagnostic_module", {**arguments, "action": "compile",
                    "source": code, "entryType": "OwnedDiagnosticEntry"}, report))
                if any(cached.get(k) != after.get(k) for k in ("revision", "assemblyName", "dllSha256", "invocationId", "loadedAssemblyCount")):
                    raise CallPreflightError("Unchanged source unexpectedly recompiled, loaded or replayed.")
                report[f"revision{version}"] = {"compile": compiled, "first": first, "second": second}
            async_code = source(3, asynchronous=True)
            compiled = data(await invoke(session, "diagnostic_module", {**arguments, "action": "compile",
                "source": async_code, "entryType": "OwnedDiagnosticEntry"}, report))
            if compiled.get("compilerStillRunning") is not False:
                compiled = await observe(session, arguments, report, min(deadline - 5, time.monotonic() + 65), compiling=True)
            if compiled.get("compilationStatus") != "completed":
                raise CallPreflightError("Async candidate was not compiled; do not invoke a retained old revision.")
            running = data(await invoke(session, "diagnostic_module", {**arguments, "action": "run", "argsJson": str(object_id)}, report))
            if running.get("activeInvocation") is not True or not running.get("invocationId"):
                raise CallPreflightError("The asynchronous in-flight interval was not observed.")
            rejected = await invoke(session, "diagnostic_module", {**arguments, "action": "compile",
                "source": source(2, "y"), "entryType": "OwnedDiagnosticEntry"}, report)
            if rejected.get("ok") is not False or (rejected.get("error") or {}).get("code") != "MODULE_BUSY":
                raise CallPreflightError("An in-flight module was not rejected precisely; do not try again.")
            finished = await observe(session, arguments, report, min(deadline - 5, time.monotonic() + 31),
                                     invocation_id=running["invocationId"])
            receipt(finished, 3, object_id)
            if identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report))) != original_identity:
                raise CallPreflightError("Process, service, mode or domain changed; retained-PlayMode matrix is not proven.")
            closed = data(await invoke(session, "diagnostic_module", {**arguments, "action": "close"}, report))
            if closed.get("status") != "closed" or closed.get("cleanupErrors"):
                raise CallPreflightError("Owned module cleanup failed.")
            observed = True  # Publish observed only after session/object cleanup is verified.
            report["coverage"] = "same-named revisions, old update unsubscription, same-source reuse, async busy rejection, retained PlayMode identity"
            report["notCovered"] = ["all Server modules loaded", "physical DLL unload", "forced stop of arbitrary code",
                "source/reference tampering", "Analyzer/Generator support", "other Unity/platform combinations"]
        except Exception as ex:
            report["businessError"] = type(ex).__name__ + (": " + str(ex) if isinstance(ex, CallPreflightError) else "")
        finally:
            # Only fresh same-lifetime cleanup; no force stop, domain recovery, replay or deadline renewal.
            if original_identity is not None and time.monotonic() < deadline:
                try:
                    async with asyncio.timeout(min(5, deadline - time.monotonic())):
                        current = identity(data(await invoke(session, "unity_mcp_status", {"forceFresh": True}, report)))
                        if current != original_identity:
                            raise CallPreflightError("Identity changed; old cleanup was not dispatched.")
                        ready = data(await invoke(session, "unity_ensure_ready", {"requiredEditorMode": "play",
                            "editorModeControl": "manual", "timeoutS": 1}, report))
                        if (ready.get("ready") is not True or ready.get("blocked") is not False or
                                ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                            raise CallPreflightError("Fresh PlayMode readiness was lost; old cleanup was not dispatched.")
                        if owner_session:
                            closed_session = data(await invoke(session, "execution_session", {"action": "close", "sessionId": owner_session}, report))
                            if (closed_session.get("sessionId") != owner_session or closed_session.get("closed") is not True or
                                    closed_session.get("cleanupErrors") != [] or
                                    closed_session.get("activeAsyncOperations") != 0 or
                                    closed_session.get("asyncOperationsStillRunning") != 0):
                                report["cleanupErrors"].append("Session cleanup is incomplete; arbitrary code may still be running.")
                        if object_id is not None:
                            found = data(await invoke(session, "unity_gameobject_find", {"instanceId": str(object_id)}, report))
                            matches = found.get("gameObjects")
                            if (found.get("exactInstanceIdLookup") is not True or found.get("truncated") is not False or
                                    not isinstance(matches, list) or len(matches) != 1 or
                                    str(matches[0].get("instanceId")) != str(object_id) or
                                    matches[0].get("name") != arguments["moduleId"]):
                                raise CallPreflightError("Owned object could not be reverified; no deletion dispatched.")
                            removed = data(await invoke(session, "unity_gameobject_delete", {"instanceId": str(object_id)}, report))
                            if (removed.get("ok") is not True or (removed.get("target") or {}).get("name") != arguments["moduleId"] or
                                    str((removed.get("target") or {}).get("instanceId")) != str(object_id) or
                                    removed.get("beforeExists") is not True or removed.get("afterExists") is not False or
                                    removed.get("businessEffectVerified") is not True):
                                raise CallPreflightError("Owned deletion was not verified; never issue it again.")
                except Exception as ex:
                    report["cleanupErrors"].append(type(ex).__name__ + (": " + str(ex) if isinstance(ex, CallPreflightError) else ""))
            elif owner_session or object_id is not None:
                report["cleanupErrors"].append("Original cleanup deadline expired; retained resource outcome unknown.")
            report["result"] = "observed" if observed and not report["cleanupErrors"] and not report.get("businessError") else "unknown"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--port", type=int, default=8011)
    options = parser.parse_args()
    if not options.execute:
        parser.error("Explicit --execute is required; the current canonical project must already be in PlayMode.")
    report = {"observationId": uuid.uuid4().hex, "startedAt": int(time.time() * 1000), "calls": [], "result": "unknown"}
    try:
        asyncio.run(asyncio.wait_for(exercise(options.port, report), timeout=200))
    except Exception as ex:
        report["clientError"] = type(ex).__name__
        report["result"] = "unknown"  # Includes connection teardown/outer timeout, not successful cleanup.
    directory = PROJECT / "Log" / "P0P1"
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / ("n01-module-" + uuid.uuid4().hex + ".json")
    actual_path, size, digest = _write_observation(path, _redact(report))
    print(json.dumps({"path": str(actual_path), "bytes": size, "sha256": digest,
                      "result": report["result"]}, ensure_ascii=False))
    return 0 if report["result"] == "observed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
