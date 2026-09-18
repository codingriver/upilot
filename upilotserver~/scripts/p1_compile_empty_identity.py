"""Exercise unbatched Unity-auto compilation via a reversible define change."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import re
import sqlite3

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient
from upilot_mcp.source_identity import source_identity


def identity_checks(report):
    terminals = [report.get(key, {}) for key in ("unbatchedTerminal", "restoreTerminal")]
    baseline = report.get("steps", {}).get("baseline", {}).get("response", {})
    snapshots = report.get("lifecycleSnapshots", [])
    operations = [t.get("compileOperationId") for t in terminals]
    starts = [[s for s in snapshots if s.get("compileOperationId") == operation
               and s.get("transition") == "compile_started"] for operation in operations]
    required = {"compile_started", "compiler_finished", "domain_reload_starting",
                "domain_reload_recovered", "compile_completed"}
    return {
        "twoIndependentOperations": all(operations) and operations[0] != operations[1],
        "explicitEmptyBatch": all("writeBatchId" in t and t["writeBatchId"] == ""
            and t.get("writeBatchCreatedAt") == 0 and t.get("compileRequestId") == ""
            and t.get("compileOrigin") == "unity_auto" and t.get("correlationVerified") is False for t in terminals),
        "ownVerifiedTerminals": all(t.get("compilePhase") == "completed"
            and t.get("terminal") is True and t.get("errorsVerified") is True
            and 0 < baseline.get("timestamp", 0) < t.get("lastCompileStartedAt", 0)
            <= t.get("lastCompileVerifiedAt", 0) for t in terminals),
        "freshStartedEvidence": all(len(events) == 1 and all(
            events[0].get(key) == 0 for key in
            ("lastCompilerFinishedAt", "lastCompileVerifiedAt", "lastTerminalCompileAt", "lastCompileRequestedAt"))
            and events[0].get("terminal") is False and events[0].get("errorsVerified") is False
            for events in starts),
        "completeLifecycle": all(required.issubset({
            s.get("transition") for s in snapshots if s.get("compileOperationId") == operation})
            for operation in operations),
        "oldBatchUnchanged": report.get("oldBatchUnchanged") is True,
        "noManualCompile": ">>> compile.request " not in report.get("serverLogDelta", ""),
        "settingsRestored": report.get("definesRestored") is True
            and bool(report.get("settingsSha256Before"))
            and report.get("settingsSha256After") == report["settingsSha256Before"],
        "sourceUnchanged": bool(report.get("sourceIdentity"))
            and report["sourceIdentity"] == report.get("sourceIdentityAfter"),
        "captureStopped": report.get("captureStopped") is True,
        "zeroErrors": all(report.get("steps", {}).get(key, {}).get("response", {}).get("data", {}).get("total") == 0
                          for key in ("errorsAfterAdd", "finalErrors")),
    }


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stamp", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9-]+", args.stamp):
        raise ValueError("Expected numeric date/attempt stamp.")
    root = Path(__file__).resolve().parents[2]
    project = root / "Tests~" / "UPilotTest"
    output = project / "Log" / "P0P1" / f"compile-empty-live-{args.stamp}.json"
    if output.exists():
        raise ValueError("Refusing to overwrite prior evidence.")
    settings = project / "ProjectSettings" / "ProjectSettings.asset"
    settings_hash = hashlib.sha256(settings.read_bytes()).hexdigest()
    log = project / "Log" / "mcp-server.log"
    log_offset = log.stat().st_size
    report = {"acceptancePassed": False, "sourceIdentity": source_identity(root),
              "settingsSha256Before": settings_hash, "steps": {}}
    symbol = "UPILOT_ACCEPTANCE_EMPTY_BATCH_" + args.stamp.replace("-", "_")

    def save():
        output.write_text(json.dumps({"ok": True, "data": report}, indent=2) + "\n", encoding="utf-8")

    async with streamable_http_client("http://127.0.0.1:8011/mcp") as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            client = DiscoveryProxyClient(session)

            async def call(label, tool, arguments=None):
                response = await client.call("unity_tool_call", {"toolName": tool, "args": arguments or {}})
                report["steps"][label] = {"tool": tool, "args": arguments or {}, "response": response}
                save()
                if response.get("ok") is not True:
                    raise RuntimeError(f"{label}: {response.get('error')}")
                return response["data"]

            async def defines(label, value=None):
                arguments = [{"value": {"kind": "literal", "typeName": "UnityEditor.BuildTargetGroup",
                                        "value": "Standalone"}}]
                types = ["UnityEditor.BuildTargetGroup"]
                if value is not None:
                    arguments.append({"value": {"kind": "literal", "typeName": "System.String", "value": value}})
                    types.append("System.String")
                return await call(label, "unity_reflection_call", {
                    "typeName": "UnityEditor.PlayerSettings",
                    "methodName": ("Get" if value is None else "Set") + "ScriptingDefineSymbolsForGroup",
                    "arguments": arguments, "parameterTypeNames": types, "resultMode": "inline"})

            async def wait_terminal(label, previous):
                async with asyncio.timeout(240):
                    while True:
                        status = await call(label, "unity_mcp_status", {"includeCapabilities": False})
                        if Path(status["paths"]["unityProjectAbsolute"]).resolve() != project.resolve():
                            raise ValueError("Project identity changed.")
                        state = status["executionState"]
                        if (state.get("compileOperationId") and state["compileOperationId"] != previous
                                and state.get("terminal") and state.get("errorsVerified")
                                and state.get("authoritative") and not state.get("isStale")):
                            return state
                        await asyncio.sleep(1)

            capture_id = None
            original = None
            attempted = False
            try:
                status = await call("baseline", "unity_mcp_status", {"includeCapabilities": False})
                if (Path(status["paths"]["unityProjectAbsolute"]).resolve() != project.resolve()
                        or not status.get("connected") or not status.get("serverReady")):
                    raise ValueError("Exact project must be connected.")
                baseline = (await call("ready", "unity_ensure_ready"))["executionState"]
                if not baseline.get("ready") or not baseline.get("writeBatchId"):
                    raise ValueError("Require a ready, previously batched baseline.")
                old_batch = await call("oldBatch", "unity_write_batch_status", {"writeBatchId": baseline["writeBatchId"]})
                target = await call("target", "unity_reflection_call", {
                    "expression": "UnityEditor.BuildPipeline.GetBuildTargetGroup(UnityEditor.EditorUserBuildSettings.activeBuildTarget)",
                    "resultMode": "inline"})
                if target.get("result") != "Standalone":
                    raise ValueError("Only the inspected Standalone group is authorized.")
                original = (await defines("definesBefore"))["result"]
                if symbol in original.split(";"):
                    raise ValueError("Temporary symbol already exists.")
                capture = await call("captureStart", "unity_console_capture_start", {
                    "title": f"compile-empty-{args.stamp}", "excludeUpilot": False})
                capture_id = capture["session"]["sessionId"]
                attempted = True
                await defines("addSymbol", ";".join(filter(None, (original, symbol))))
                report["unbatchedTerminal"] = await wait_terminal("observeAdd", baseline["compileOperationId"])
                await call("errorsAfterAdd", "unity_compile_errors")
                await defines("definesAfterAdd")
                print("UNBATCHED_TERMINAL_OBSERVED", flush=True)
            except Exception as exc:
                report["error"] = f"{type(exc).__name__}: {exc}"
            finally:
                if attempted:
                    try:
                        current = (await defines("definesBeforeRestore"))["result"]
                        expected = ";".join(filter(None, (original, symbol)))
                        if current != expected:
                            raise ValueError("Define list changed; refusing to overwrite concurrent edits.")
                        state = (await call("restoreReady", "unity_ensure_ready"))["executionState"]
                        if not state.get("ready"):
                            raise ValueError("Editor not ready for restoration.")
                        await defines("restoreSymbol", original)
                        report["restoreTerminal"] = await wait_terminal("observeRestore", state["compileOperationId"])
                        report["definesRestored"] = (await defines("definesAfterRestore"))["result"] == original
                        await call("finalErrors", "unity_compile_errors")
                        report["oldBatchUnchanged"] = old_batch == await call(
                            "oldBatchAfter", "unity_write_batch_status", {"writeBatchId": baseline["writeBatchId"]})
                    except Exception as exc:
                        report["restoreError"] = f"{type(exc).__name__}: {exc}"
                if capture_id:
                    try:
                        stopped = await call("captureStop", "unity_console_capture_stop", {"sessionId": capture_id})
                        report["captureStopped"] = stopped["session"]["active"] is False
                    except Exception as exc:
                        report["captureError"] = str(exc)
                with log.open("rb") as stream:
                    stream.seek(log_offset)
                    report["serverLogDelta"] = stream.read().decode("utf-8", errors="replace")
                db_path = project / "Library" / "UPilot" / "ServerState" / "state-v2.sqlite3"
                with sqlite3.connect(db_path.as_uri() + "?mode=ro", uri=True) as db:
                    rows = db.execute("SELECT snapshot_json FROM state_events WHERE observed_at>? ORDER BY observed_at",
                                      (report["steps"]["baseline"]["response"]["timestamp"],)).fetchall()
                report["lifecycleSnapshots"] = [json.loads(row[0]) for row in rows]
                report["sourceIdentityAfter"] = source_identity(root)
                report["settingsSha256After"] = hashlib.sha256(settings.read_bytes()).hexdigest()
                report["checks"] = identity_checks(report)
                report["acceptancePassed"] = all(report["checks"].values()) and not any(
                    report.get(key) for key in ("error", "restoreError", "captureError"))
                save()
                print(json.dumps({"path": str(output), "passed": report["acceptancePassed"],
                                  "checks": report["checks"], "error": report.get("error"),
                                  "restoreError": report.get("restoreError")}), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
