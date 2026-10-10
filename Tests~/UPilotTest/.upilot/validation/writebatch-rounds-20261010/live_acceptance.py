"""Two scoped live WriteBatch checks; no test/compile/restart dispatch outside WriteBatch."""
import asyncio, hashlib, json, os, time
from datetime import datetime, timezone
from pathlib import Path
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient

ROOT = Path(r"D:\upilot\Tests~\UPilotTest").resolve()
REPO = Path(r"D:\upilot")
OUT = ROOT / ".upilot/validation/writebatch-rounds-20261010"
PROBE = ROOT / "Assets/UPilotAcceptance/WriteBatchFixedRoundsProbe_20261010_c9a1.cs"
META = Path(str(PROBE) + ".meta")
REPORT = OUT / "live-acceptance.json"
STATE_KEYS = ("writeBatchId", "compileRequestId", "compileOperationId", "compileOrigin", "compilePhase", "terminal", "errorsVerified", "lastCompileRequestedAt", "lastCompileStartedAt", "lastCompilerFinishedAt", "lastCompileVerifiedAt", "producerEpoch", "domainGeneration", "snapshotId", "authoritative", "isStale", "ready")
ENTRY_NAMES = {"StateStore.freeze_write_batch_round", "StateStore.record_write_batch_round_dispatch", "StateStore._persist_round_evidence", "StateStore.get_write_batch_round", "StateStore.get_write_batch", "ResourceDomainService._resume_pending_write_batches", "CompileDomainService.compile", "CompileDomainService._safe_compile_and_wait"}
report = {"startedAt": datetime.now(timezone.utc).isoformat(), "project": str(ROOT), "probe": str(PROBE), "headAtStart": None, "scenarios": {}, "registeredBatches": [], "passed": False}
last_probe_hash = None

def save():
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

def selected(state):
    return {k: state.get(k) for k in STATE_KEYS}

def passed_batch(b):
    snap = b.get("terminalSnapshot") or {}
    return bool(b.get("terminal") and b.get("outcome") == "passed" and b.get("correlationVerified") is True and b.get("errorsVerified") is True and b.get("compileOperationId") and b.get("compileRequestId") and b.get("writeBatchId") in b.get("compileMemberWriteBatchIds", []) and snap.get("writeBatchId") == b.get("compileOwnerWriteBatchId") and snap.get("compileOperationId") == b.get("compileOperationId") and b.get("lastCompileVerifiedAt", 0) >= b.get("writeBatchCreatedAt", 0))

def write_probe(label, create=False):
    global last_probe_hash
    assert PROBE.parent.is_dir() and ROOT in PROBE.resolve().parents
    if create:
        assert not PROBE.exists() and not META.exists(), "Probe path is not vacant"
    else:
        assert PROBE.is_file() and hashlib.sha256(PROBE.read_bytes()).hexdigest() == last_probe_hash, "Probe changed by another workflow"
    text = '// Temporary scoped WriteBatch acceptance probe; no Unity callbacks.\nnamespace UPilotWriteBatchAcceptance_20261010_c9a1\n{\n    public static class Probe\n    {\n        public const string Revision = "' + label + '";\n    }\n}\n'
    with PROBE.open("x" if create else "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    last_probe_hash = hashlib.sha256(PROBE.read_bytes()).hexdigest()
    report.setdefault("savedRevisions", []).append({"revision": label, "savedAt": int(time.time()*1000), "sha256": last_probe_hash})

async def main():
    async with streamable_http_client("http://127.0.0.1:8011/mcp") as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            client = DiscoveryProxyClient(session)
            async def call(name, args=None):
                r = await client.call("unity_tool_call", {"toolName": name, "args": args or {}})
                if r.get("ok") is not True:
                    raise RuntimeError(name + ": " + json.dumps(r.get("error"), ensure_ascii=False))
                return r.get("data") or {}
            async def fresh_ready():
                ready = await call("unity_ensure_ready", {"timeoutS": 30, "requiredEditorMode": "edit", "editorModeControl": "require"})
                e = ready.get("executionState", {})
                assert ready.get("ready") is True and e.get("ready") is True and e.get("authoritative") is True and e.get("isStale") is False, "Editor not ready: " + json.dumps(ready, ensure_ascii=False)[:1000]
                return ready
            async def register(label, paths, deleted=None):
                args = {"paths": paths, "compileWhenEditMode": True}
                if deleted is not None:
                    args["deletedPaths"] = deleted
                report["registerIntent"] = {"label": label, "args": args, "at": int(time.time()*1000)}
                save()  # An uncertain mutation is never replayed.
                b = await call("unity_write_batch_register", args)
                assert b.get("writeBatchId") and b.get("writeBatchCreatedAt"), "Missing original registration identity"
                report["registeredBatches"].append({"label": label, "writeBatchId": b["writeBatchId"], "writeBatchCreatedAt": b["writeBatchCreatedAt"]})
                report.pop("registerIntent", None)
                save()
                print(label + " REGISTERED " + b["writeBatchId"], flush=True)
                return b
            async def poll(batches, focus_samples=None, snapshots=None):
                deadline = max(b["writeBatchCreatedAt"] + 600000 for b in batches)
                ends = []
                last_states = set()
                last_focus_at = 0.0
                while int(time.time()*1000) < deadline:
                    ends = [await call("unity_write_batch_status", {"writeBatchId": b["writeBatchId"]}) for b in batches]
                    if snapshots is not None:
                        for b in ends:
                            e = selected(b.get("executionState", {}))
                            key = (e["snapshotId"], e["compilePhase"])
                            if key not in last_states:
                                snapshots.append(e); last_states.add(key)
                    if focus_samples is not None and time.monotonic() - last_focus_at >= 0.25:
                        f = await call("unity_editor_focus_state")
                        focus_samples.append({"at": int(time.time()*1000), **{k: f.get(k) for k in ("unityFocused", "foregroundHwnd", "foregroundPid", "targetPid", "projectIdentityVerified")}})
                        last_focus_at = time.monotonic()
                    if all(b.get("terminal") for b in ends):
                        return ends
                    await asyncio.sleep(0.1)
                raise RuntimeError("Original WriteBatch registration deadline expired without a terminal observation")
            try:
                status = await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False})
                actual = status.get("paths", {}).get("unityProjectAbsolute", "")
                assert status.get("connected") is True and status.get("serverReady") is True and os.path.normcase(str(Path(actual).resolve())) == os.path.normcase(str(ROOT)), "Target identity mismatch"
                loaded = status.get("runtimeIdentity", {}).get("mcpServer", {}).get("loadedSource", {})
                entries = [e for e in loaded.get("entries", []) if e.get("entry") in ENTRY_NAMES]
                assert len(entries) == len(ENTRY_NAMES) and all(e.get("status") == "equivalent" for e in entries), "Relevant loaded source is not verified"
                report["loadedSource"] = entries
                report["runtimeBefore"] = {"session": status.get("session"), "serverPid": status.get("runtimeIdentity", {}).get("mcpServer", {}).get("processId"), "editor": selected(status.get("editor", {}))}
                expected = json.loads((OUT / "delivery-source-hashes.json").read_text(encoding="utf-8"))["files"]
                report["sourceUnchangedBefore"] = {p: hashlib.sha256((REPO / p).read_bytes()).hexdigest() == h for p,h in expected.items()}
                report["documentationVersionOnlyDrift"] = hashlib.sha256((REPO / "Documentation~/AgentRules/AGENTS.upilot.md").read_bytes().replace(b"upilotPackageVersion: 0.3.46", b"upilotPackageVersion: 0.3.45")).hexdigest() == expected["Documentation~/AgentRules/AGENTS.upilot.md"]
                assert all(ok for p,ok in report["sourceUnchangedBefore"].items() if p != "Documentation~/AgentRules/AGENTS.upilot.md") and report["documentationVersionOnlyDrift"], "Relevant delivered source changed since regression evidence"
                queue = await call("unity_queue_cleanup")
                report["queueBefore"] = {k: queue.get(k) for k in ("complete", "isStale", "issues", "items")}
                assert queue.get("complete") is True and queue.get("isStale") is False and not queue.get("issues") and queue.get("items") == [], "Other active work blocks this probe"
                tests = await call("unity_test_status", {"detailLevel": "summary"})
                capture = await call("unity_console_capture_list", {"activeOnly": True, "count": 10})
                assert tests.get("isRunning") is False and tests.get("cleanupPending") is False and capture.get("activeCount") == 0, "Another test or capture is active"
                report["baselineReady"] = await fresh_ready()
                errors = await call("unity_compile_errors", {"includeWarnings": True})
                assert not errors.get("errors"), "Existing compile errors block acceptance"
                report["compileErrorsBefore"] = errors
                report["consoleCursor"] = await call("unity_console_mark_logs")
                focus = await call("unity_editor_focus_state")
                assert focus.get("projectIdentityVerified") is True and focus.get("unityFocused") is False, "Background scenario not established; do not change focus"
                background = report["scenarios"]["unfocusedActiveCompile"] = {"focusBefore": focus, "focusSamples": [], "snapshots": []}
                write_probe("background", create=True)
                bg = await register("BACKGROUND", [str(PROBE)])
                background["registration"] = bg
                end = (await poll([bg], background["focusSamples"], background["snapshots"]))[0]
                background["terminal"] = end
                background["checks"] = {"verifiedTerminal": passed_batch(end), "activeMcpRequest": end.get("terminalSnapshot", {}).get("compileOrigin") == "mcp" and end.get("terminalSnapshot", {}).get("lastCompileRequestedAt", 0) >= bg["writeBatchCreatedAt"], "neverFocusedInObservedSamples": bool(background["focusSamples"]) and all(f.get("unityFocused") is False for f in background["focusSamples"]), "foregroundUnchangedInObservedSamples": all(f.get("foregroundHwnd") == focus.get("foregroundHwnd") for f in background["focusSamples"]), "originalOwner": end.get("compileOwnerWriteBatchId") == bg["writeBatchId"]}
                background["passed"] = all(background["checks"].values())
                save(); print("BACKGROUND " + json.dumps(background["checks"]), flush=True)
                assert background["passed"], "Background compile scenario failed"
                await fresh_ready()
                overlap = report["scenarios"]["registrationDuringCompile"] = {"snapshots": []}
                overlap["baseline"] = selected((await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False}))["editor"])
                write_probe("round-A")
                a = await register("A", [str(PROBE)])
                overlap["registrationA"] = a
                deadline = a["writeBatchCreatedAt"] + 600000
                while int(time.time()*1000) < deadline:
                    observed = await call("unity_write_batch_status", {"writeBatchId": a["writeBatchId"]})
                    e = observed.get("executionState", {})
                    if e.get("writeBatchId") == a["writeBatchId"] and e.get("compilePhase") == "compiling" and e.get("lastCompileStartedAt", 0) > 0 and e.get("terminal") is False:
                        overlap["beforeB"] = selected(e)
                        break
                    if observed.get("terminal"):
                        raise RuntimeError("A already terminal; overlap not established. No automatic replay.")
                    await asyncio.sleep(0.05)
                else:
                    raise RuntimeError("A compilation start not observed within original deadline")
                # Save like an IDE while the real compiler is active; ImportAsset is deliberately not called.
                write_probe("round-B")
                b = await register("B", [str(PROBE)])
                overlap["registrationB"] = b
                save()
                a_end,b_end = await poll([a,b], snapshots=overlap["snapshots"])
                overlap["terminalA"],overlap["terminalB"] = a_end,b_end
                a_snap = a_end.get("terminalSnapshot", {})
                b_snap = b_end.get("terminalSnapshot", {})
                overlap["checks"] = {"differentBatchIds": a["writeBatchId"] != b["writeBatchId"], "savedAndRegisteredDuringActualCompiler": 0 < a_snap.get("lastCompileStartedAt", 0) <= b["writeBatchCreatedAt"] < a_snap.get("lastCompilerFinishedAt", 0), "bResponseStillObservedACompiling": b.get("executionState", {}).get("writeBatchId") == a["writeBatchId"] and b.get("executionState", {}).get("compilePhase") == "compiling", "fixedFirstRoundExcludesB": a_end.get("compileMemberWriteBatchIds") == [a["writeBatchId"]] and b["writeBatchId"] not in a_end.get("compileMemberWriteBatchIds", []), "nextRoundOwnsB": b_end.get("compileOwnerWriteBatchId") == b["writeBatchId"] and b_end.get("compileMemberWriteBatchIds") == [b["writeBatchId"]], "differentRealCompileOperations": bool(a_end.get("compileOperationId")) and a_end.get("compileOperationId") != b_end.get("compileOperationId"), "differentRealRequestIds": bool(a_end.get("compileRequestId")) and a_end.get("compileRequestId") != b_end.get("compileRequestId"), "bothVerifiedTerminals": passed_batch(a_end) and passed_batch(b_end), "nextRoundRequestedAfterASettled": b_snap.get("lastCompileRequestedAt", 0) >= a_snap.get("lastCompileVerifiedAt", 0), "samePathDifferentDiagnosticHashesAccepted": a.get("paths") == b.get("paths") and a.get("filesSha256") != b.get("filesSha256"), "aTerminalStableAfterB": (await call("unity_write_batch_status", {"writeBatchId": a["writeBatchId"]})).get("terminalSnapshot") == a_snap}
                overlap["passed"] = all(overlap["checks"].values())
                print("OVERLAP " + json.dumps(overlap["checks"]), flush=True); save()
                assert overlap["passed"], "Compilation overlap scenario failed"
            except Exception as exc:
                report["error"] = type(exc).__name__ + ": " + str(exc)
                print("ACCEPTANCE_ERROR " + report["error"], flush=True)
            finally:
                try:
                    if last_probe_hash is not None and PROBE.exists():
                        # Settle only our original IDs; never cancel or replay another workflow.
                        own = report["registeredBatches"][:]
                        if own:
                            report["beforeCleanupTerminals"] = await poll(own)
                        report["cleanupReady"] = await fresh_ready()
                        assert hashlib.sha256(PROBE.read_bytes()).hexdigest() == last_probe_hash, "Do not delete a probe modified by another workflow"
                        assert ROOT in PROBE.resolve().parents and ROOT in META.resolve().parents
                        deleted = [str(PROBE)]
                        PROBE.unlink()
                        if META.exists():
                            assert META.is_file() and not META.is_symlink()
                            report["probeMetaSha256"] = hashlib.sha256(META.read_bytes()).hexdigest()
                            META.unlink(); deleted.append(str(META))
                        cleanup = await register("CLEANUP", [], deleted)
                        report["cleanupRegistration"] = cleanup
                        report["cleanupTerminal"] = (await poll([cleanup]))[0]
                        assert passed_batch(report["cleanupTerminal"]), "Probe deletion compile did not verify"
                    report["finalReady"] = await fresh_ready()
                    report["finalCompileErrors"] = await call("unity_compile_errors", {"includeWarnings": True})
                    report["finalConsoleErrors"] = await call("unity_console_tail_logs", {"cursor": report.get("consoleCursor", {}).get("cursor", 0), "logType": "error", "count": 30, "includeStackTrace": False})
                    queue = await call("unity_queue_cleanup")
                    report["queueAfter"] = {k: queue.get(k) for k in ("complete", "isStale", "issues", "items")}
                    after = await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False})
                    report["runtimeAfter"] = {"session": after.get("session"), "serverPid": after.get("runtimeIdentity", {}).get("mcpServer", {}).get("processId"), "editor": selected(after.get("editor", {}))}
                    report["sourceUnchangedAfter"] = {p: hashlib.sha256((REPO / p).read_bytes()).hexdigest() == h for p,h in expected.items()}
                    report["checks"] = {"bothScenariosPassed": len(report["scenarios"]) == 2 and all(s.get("passed") is True for s in report["scenarios"].values()), "probeRemoved": not PROBE.exists() and not META.exists(), "cleanupCompileVerified": passed_batch(report.get("cleanupTerminal", {})), "noCompileErrors": not report["finalCompileErrors"].get("errors"), "sameServerAndUnityLifetime": report["runtimeBefore"]["serverPid"] == report["runtimeAfter"]["serverPid"] and report["runtimeBefore"]["session"]["processId"] == report["runtimeAfter"]["session"]["processId"] and report["runtimeBefore"]["editor"]["producerEpoch"] == report["runtimeAfter"]["editor"]["producerEpoch"], "deliveredSourceUnchanged": all(ok for p,ok in report["sourceUnchangedAfter"].items() if p != "Documentation~/AgentRules/AGENTS.upilot.md") and report["documentationVersionOnlyDrift"]}
                    report["passed"] = not report.get("error") and all(report["checks"].values())
                except Exception as exc:
                    report["cleanupError"] = type(exc).__name__ + ": " + str(exc)
                    print("CLEANUP_ERROR " + report["cleanupError"], flush=True)
                report["finishedAt"] = datetime.now(timezone.utc).isoformat()
                save()
                print(json.dumps({"passed": report["passed"], "checks": report.get("checks"), "error": report.get("error"), "cleanupError": report.get("cleanupError"), "report": str(REPORT)}, ensure_ascii=False), flush=True)
    return 0 if report["passed"] else 1

if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
