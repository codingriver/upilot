"""Record source/log baselines and check the bounded deferred-compile scenario."""
import argparse
import hashlib
import json
from pathlib import Path

from upilot_mcp.source_identity import source_identity


def checks(steps, log_lines, unchanged):
    baseline = steps["prewrite-play-baseline"]["data"]["executionState"]
    registered = steps["register"]["data"]
    held = steps["held-batch"]["data"]
    play = steps["held-play"]["data"]["executionState"]
    stopped = steps["play-stop"]["data"]
    terminal = steps["terminal"]["data"]
    final = steps["final-status"]["data"]["executionState"]
    batch_id = registered["writeBatchId"]
    stop_lines = [i for i, line in enumerate(log_lines) if ">>> playmode.set " in line]
    compile_lines = [i for i, line in enumerate(log_lines) if ">>> compile.request " in line]
    capture = steps["capture-stop"]["data"]["session"]
    return {
        "playBaseline": baseline.get("playModeState") == "play"
            and baseline.get("authoritative") is True and baseline.get("isStale") is False,
        "registeredDeferred": registered.get("status") == "deferred"
            and registered.get("terminal") is False
            and registered["executionState"].get("playModeState") == "play",
        "heldUnverified": held.get("writeBatchId") == batch_id and held.get("status") == "deferred"
            and held.get("terminal") is False and held.get("correlationVerified") is False
            and held.get("errorsVerified") is False and held.get("outcome") == "unknown"
            and held.get("terminalSnapshot") is None and not held.get("compileRequestId"),
        "pendingIdentityVisible": play.get("playModeState") == "play"
            and play.get("authoritative") is True and play.get("isStale") is False
            and play.get("pendingWriteBatchId") == batch_id
            and play.get("compileDeferredReason") == "PlayMode" and play.get("correlationVerified") is False,
        "noInferredCompilerPhase": not play.get("isCompiling") or (
            play.get("compilePhase") == baseline.get("compilePhase")
            and play.get("terminal") is False and play.get("errorsVerified") is False),
        "heldAtLeastFiveSeconds": steps["held-play"]["timestamp"] - registered["writeBatchCreatedAt"] >= 5000,
        "editConfirmed": stopped.get("confirmed") is True and stopped.get("playModeState") == "edit",
        "oneCompileAfterStop": len(stop_lines) == 2 and len(compile_lines) == 1
            and compile_lines[0] > stop_lines[1],
        "ownVerifiedTerminal": terminal.get("writeBatchId") == batch_id
            and terminal.get("operationId") == registered.get("operationId")
            and terminal.get("filesSha256") == registered.get("filesSha256")
            and terminal.get("correlationVerified") is True and terminal.get("errorsVerified") is True
            and terminal.get("terminal") is True and terminal.get("outcome") == "passed"
            and terminal.get("writeBatchCreatedAt") == registered["writeBatchCreatedAt"]
            and terminal.get("lastCompileVerifiedAt", 0) >= registered["writeBatchCreatedAt"]
            and bool(terminal.get("compileRequestId"))
            and terminal.get("compileRequestId") != baseline.get("compileRequestId"),
        "finalReady": final.get("ready") is True and final.get("authoritative") is True
            and final.get("isStale") is False and final.get("playModeState") == "edit"
            and final.get("writeBatchId") == batch_id and final.get("correlationVerified") is True
            and not final.get("pendingWriteBatchId"),
        "zeroErrors": steps["final-errors"]["data"].get("total") == 0,
        "captureStopped": capture.get("active") is False,
        "sourceUnchanged": unchanged,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("baseline", "collect"))
    parser.add_argument("--stamp", required=True)
    args = parser.parse_args()
    if not args.stamp or any(char not in "0123456789-" for char in args.stamp):
        raise ValueError("Use a numeric date/attempt stamp.")
    root = Path(__file__).resolve().parents[2]
    project = root / "Tests~" / "UPilotTest"
    folder = project / "Log" / "P0P1"
    log = project / "Log" / "mcp-server.log"
    baseline_path = folder / f"compile-deferred-source-baseline-{args.stamp}.json"
    output = baseline_path if args.mode == "baseline" else folder / f"compile-deferred-report-{args.stamp}.json"
    if output.exists():
        raise ValueError("Refusing to overwrite prior evidence.")
    if args.mode == "baseline":
        data = {"sourceIdentity": source_identity(root), "logOffset": log.stat().st_size}
    else:
        before = json.loads(baseline_path.read_text(encoding="utf-8"))["data"]
        names = ("prewrite-play-baseline", "register", "held-batch", "held-play",
                 "play-stop", "terminal", "final-status", "final-errors", "capture-stop")
        steps = {}
        evidence = []
        for name in names:
            path = folder / f"compile-deferred-{name}-{args.stamp}.json"
            content = path.read_bytes()
            steps[name] = json.loads(content)
            if steps[name].get("ok") is not True:
                raise ValueError(f"{name} did not return a successful tool response.")
            evidence.append({"path": path.relative_to(root).as_posix(), "bytes": len(content),
                             "sha256": hashlib.sha256(content).hexdigest()})
        with log.open("rb") as stream:
            stream.seek(before["logOffset"])
            lines = stream.read().decode("utf-8", errors="replace").splitlines()
        after = source_identity(root)
        verified = checks(steps, lines, before["sourceIdentity"] == after)
        data = {"scenario": "deferred-compile", "acceptancePassed": all(verified.values()),
                "checks": verified, "sourceIdentity": before["sourceIdentity"], "sourceIdentityAfter": after,
                "sourceUnchanged": before["sourceIdentity"] == after,
                "writeBatchId": steps["terminal"]["data"]["writeBatchId"],
                "operationId": steps["terminal"]["data"]["operationId"],
                "terminal": steps["terminal"]["data"], "evidence": evidence,
                "serverLogDelta": lines}
    content = (json.dumps({"ok": True, "data": data}, indent=2) + "\n").encode("utf-8")
    output.write_bytes(content)
    print(json.dumps({"path": str(output), "bytes": len(content),
                      "sha256": hashlib.sha256(content).hexdigest(),
                      "acceptancePassed": data.get("acceptancePassed"), "checks": data.get("checks")}))


if __name__ == "__main__":
    main()
