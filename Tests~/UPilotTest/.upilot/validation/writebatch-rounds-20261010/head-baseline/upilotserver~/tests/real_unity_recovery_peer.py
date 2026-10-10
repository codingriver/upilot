"""Test-owned production Server peer for UPilotRealUnityRecoveryTests.

No shared endpoint, process restart, fake OS identity, or replacement recovery logic.
The StateStore logical project is canonical; its physical SQLite stays test-owned.
"""
from __future__ import annotations

import argparse
import asyncio
from dataclasses import asdict
import hashlib
import json
import logging
from pathlib import Path
import sys
import time
import traceback
import types

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from upilot_mcp.dispatcher import CommandDispatcher
from upilot_mcp.server import WsOrchestratorServer
from upilot_mcp.state_store import StateStore

CANONICAL_PROJECT = Path(r"D:\upilot\Tests~\UPilotTest").resolve()
SCENARIOS = {"response_lost", "completed", "timed_out", "cancelled", "declared_reload"}


def save(directory: Path, name: str, data: object) -> None:
    target = directory / name
    temporary = target.with_suffix(target.suffix + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(target)


async def until(predicate, label: str) -> None:
    # All waits share the enclosing 45-second lifetime, not a renewed deadline.
    while not predicate():
        await asyncio.sleep(0.02)


def loaded_source_evidence() -> list[dict]:
    """Compare selected live code objects, not inspect.getsource/disk age alone."""
    result = []
    for cls, names in [
        (WsOrchestratorServer, ["_handle_message", "_suspend_or_fail_pending_on_disconnect", "_resend_pending_commands", "send_command"]),
        (CommandDispatcher, ["call"]), (StateStore, ["configure_project"]),
    ]:
        module = sys.modules[cls.__module__]
        path = Path(module.__file__).resolve()
        raw = path.read_bytes()
        reference = compile(raw, str(path), "exec")
        codes = {}
        def collect(code):
            codes[code.co_qualname] = code
            for constant in code.co_consts:
                if isinstance(constant, types.CodeType):
                    collect(constant)
        collect(reference)
        for name in names:
            live = getattr(cls, name).__code__
            matches = live == codes[live.co_qualname]
            assert matches, f"Loaded code mismatch: {live.co_qualname}"
            result.append({"module": module.__name__, "path": str(path),
                           "sha256": hashlib.sha256(raw).hexdigest(),
                           "function": live.co_qualname, "liveCodeMatchesFrozenSource": matches})
    return result


async def run(directory: Path, project: Path, scenario: str, production_receive_loop: bool = False, production_main_thread_pump: bool = False) -> dict:
    owned_store = directory / "owned-store"
    server = WsOrchestratorServer(host="127.0.0.1", port=0,
                                  heartbeat_interval_ms=60000,
                                  expected_project_path=str(owned_store))
    physical_db = server.state._db_path.resolve()
    assert physical_db.is_relative_to(directory)
    # configure_project's existing exact-project early return protects the owned DB
    # on each real hello. Do not initialize the Server against the shared Library DB.
    server.expected_project_path = str(project)
    server.state._project_path = str(project)
    dispatcher = CommandDispatcher(server, server.state)
    server_task = asyncio.create_task(server.start())
    original = None
    report = {"scenario": scenario, "succeeded": False,
              "realDomainReload": False, "productionReceiveLoopCovered": production_receive_loop,
              "productionMainThreadPumpCovered": False, "sharedWatchdogObserved": False,
              "scope": "real Unity socket/identity/command routing; isolated production Server",
              "project": str(project), "dbPath": str(physical_db),
              "serverLifecycleId": server.state.lifecycle_id,
              "loadedSource": loaded_source_evidence()}

    def assert_store():
        assert server.state._db_path.resolve() == physical_db
        assert server.state._project_path == str(project)

    async def exercise():
        nonlocal original
        assert await server.wait_until_listening(5), "Peer did not listen"
        port = server._server.sockets[0].getsockname()[1]
        report["port"] = port
        save(directory, "ready.json", {"port": port})
        await until(lambda: server.is_ready() and (directory / "authenticated-initial").exists(), "initial authentication")
        first_session = asdict(server.session_manager.active)
        report["initialSession"] = first_session
        assert first_session["identity_verified"]
        assert first_session["verification_level"] == "windows-pid-creation-executable-role-project"
        assert_store()
        timeout_ms = 2000 if scenario == "timed_out" else 10000
        original = asyncio.create_task(dispatcher.call("real-unity-recovery-original", "fixture.mutation", {}, timeout_ms=timeout_ms))
        if production_receive_loop and not production_main_thread_pump:
            await until(lambda: (directory / "mutation-queued.json").exists(), "owned main-thread queue")
            queued = json.loads((directory / "mutation-queued.json").read_text(encoding="utf-8"))
            assert queued["effectCount"] == 0 and queued["queueDepth"] == 1
            assert queued["routeThreadId"] != queued["mainThreadId"]
            assert not (directory / "effect").exists()
            probe = await dispatcher.call("real-unity-recovery-probe", "fixture.receive_probe", {}, timeout_ms=5000)
            assert probe.ok, asdict(probe)
            assert probe.data["effectCount"] == 0 and probe.data["queueDepth"] == 1
            assert probe.data["routeThreadId"] != probe.data["mainThreadId"]
            assert probe.data["originalId"] == queued["originalId"]
            assert not original.done() and not (directory / "effect").exists()
            report["receiveProbe"] = asdict(probe)
            report["queuedWitness"] = queued
            save(directory, "pump-now", {"probeCompletedAt": int(time.time() * 1000), "originalId": queued["originalId"]})
        await until(lambda: (directory / "effect").exists(), "Unity mutation")
        records = [r for r in server.state.commands.values()
                   if r.request_id == "real-unity-recovery-original" and r.name == "fixture.mutation"]
        assert len(records) == 1
        record = records[0]
        if production_receive_loop and not production_main_thread_pump:
            assert record.command_id == queued["originalId"]
        identity = (record.command_id, record.created_at, record.sent_at)
        report["originalTimeoutMs"] = timeout_ms
        report["originalAfterEffect"] = asdict(record)

        if scenario == "cancelled":
            original.cancel()
            try:
                await original
                raise AssertionError("Cancellation was swallowed")
            except asyncio.CancelledError:
                report["originalResponse"] = {"cancelled": True}
        elif scenario in {"completed", "timed_out"}:
            report["originalResponse"] = asdict(await original)
        if scenario == "declared_reload":
            await until(lambda: server._domain_reloading, "declared reload received")

        save(directory, "drop-now", {})
        await until(lambda: not server.is_ready(), "socket loss observed")
        report["afterDisconnect"] = {"pending": list(server._pending), "suspended": list(server._suspended)}
        if scenario != "declared_reload" and not original.done():
            report["originalResponse"] = asdict(await original)
        save(directory, "reconnect-now", {})
        await until(lambda: server.is_ready() and (directory / "authenticated-reconnect").exists(), "reconnect authentication")
        second_session = asdict(server.session_manager.active)
        report["reconnectedSession"] = second_session
        assert second_session["session_id"] != first_session["session_id"]
        assert second_session["process_id"] == first_session["process_id"]
        assert second_session["process_created_at"] == first_session["process_created_at"]
        assert second_session["identity_verified"]
        if scenario == "declared_reload":
            report["originalResponse"] = asdict(await original)
        frozen = asdict(record)
        assert (record.command_id, record.created_at, record.sent_at) == identity
        expected = {"response_lost": "CONNECTION_LOST", "completed": "",
                    "timed_out": "COMMAND_TIMEOUT", "cancelled": "CANCELLED",
                    "declared_reload": "COMMAND_RECOVERY_REQUIRED"}[scenario]
        assert record.status == ("success" if scenario == "completed" else "failed")
        assert (record.error or {}).get("code", "") == expected
        if scenario == "completed":
            assert record.result["effectCount"] == 1
        assert not server._pending and not server._suspended
        save(directory, "terminal-frozen", {})
        # Unity sends a contradictory late result before the inspector reply on
        # the same socket. The inspector is an ordered barrier, not a sleep.
        inspection = await dispatcher.call("real-unity-recovery-barrier", "fixture.inspect", {}, timeout_ms=5000)
        assert inspection.ok, asdict(inspection)
        assert inspection.data["effectCount"] == 1, "Mutation was replayed"
        if production_main_thread_pump:
            witness = json.loads((directory / "shared-pump.json").read_text(encoding="utf-8"))
            assert witness["originalId"] == record.command_id
            assert witness["effectCount"] == 1 and witness["executionPhase"] == "executing"
            assert witness["executingIdentityVerified"] and witness["watchdogAfter"] > witness["watchdogBefore"]
            assert witness["pumpAt"] > 0 and witness["routeThreadId"] != witness["mainThreadId"]
            assert witness["executionThreadId"] == witness["mainThreadId"]
            report.update(productionMainThreadPumpCovered=True, sharedWatchdogObserved=True, sharedPumpWitness=witness)
        assert asdict(record) == frozen, "Late result changed frozen original outcome"
        assert not server._pending and not server._suspended
        assert server.state.lifecycle_id == report["serverLifecycleId"]
        assert_store()
        report.update({"succeeded": True, "originalError": expected,
                       "originalStatus": record.status, "effectCount": inspection.data["effectCount"],
                       "frozen": True, "pendingCount": 0, "suspendedCount": 0,
                       "frozenOriginal": frozen, "finalOriginal": asdict(record),
                       "inspection": asdict(inspection)})

    try:
        await asyncio.wait_for(exercise(), timeout=45)
    except BaseException:
        report["failure"] = traceback.format_exc()
    finally:
        if original is not None and not original.done():
            original.cancel()
            await asyncio.gather(original, return_exceptions=True)
        server.stop()
        try:
            await asyncio.wait_for(server_task, timeout=5)
            report["cleanup"] = {"serverTaskStopped": True, "connections": server.ws_connection_count,
                                 "pending": len(server._pending), "suspended": len(server._suspended)}
            assert server.ws_connection_count == 0
            assert not server._pending and not server._suspended
            assert_store()
        except BaseException:
            report["cleanupError"] = traceback.format_exc()
            report["succeeded"] = False
        report["finishedAt"] = int(time.time() * 1000)
        save(directory, "report.json", report)
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--scenario", required=True, choices=sorted(SCENARIOS))
    parser.add_argument("--production-receive-loop", action="store_true")
    parser.add_argument("--production-main-thread-pump", action="store_true")
    args = parser.parse_args()
    if (args.production_receive_loop or args.production_main_thread_pump) and args.scenario != "response_lost":
        parser.error("Production receive-loop probe is restricted to the response_lost fixture")
    project = Path(args.project).resolve()
    if project != CANONICAL_PROJECT:
        parser.error("Only the exact canonical Unity project is authorized")
    directory = Path(args.directory).resolve()
    if not directory.is_relative_to(project / "Log" / "Stage4" / "RealUnityRecovery"):
        parser.error("Evidence must stay in the test-owned canonical project subtree")
    logging.basicConfig(level=logging.DEBUG,
                        filename=str(directory / "peer-wire.log"), encoding="utf-8",
                        format="%(asctime)s %(name)s %(levelname)s %(message)s")
    return 0 if asyncio.run(run(directory, project, args.scenario, args.production_receive_loop or args.production_main_thread_pump, args.production_main_thread_pump))["succeeded"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
