"""Collect bounded canonical acceptance through an already configured HTTP MCP endpoint."""
import asyncio
import hashlib
import json
import os
from pathlib import Path
import time

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client
from upilot_mcp.release_evidence import REQUIRED_FIXTURES, verify_required_summary
from upilot_mcp.source_identity import source_identity

ROOT = Path(__file__).resolve().parents[2]


async def observe_acceptance(call, started, retain, *, timeout_sec=960, cleanup_timeout_sec=60, poll_interval_sec=2):
    """Never replay start; retain identity and request bounded cleanup on observation failure."""
    task_id = started["taskId"]
    state = started
    retain(state)
    deadline = time.monotonic() + timeout_sec
    try:
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Acceptance observation expired.")
            state = await asyncio.wait_for(
                call("unity_task_status", {"taskId": task_id, "detailLevel": "summary"}),
                timeout=min(15, remaining),
            )
            retain(state)
            if state.get("terminal"):
                return state
            await asyncio.sleep(min(poll_interval_sec, max(0, deadline - time.monotonic())))
    except (Exception, asyncio.CancelledError) as original:
        evidence = {"taskId": task_id, "lastState": state, "observationError": str(original),
                    "cleanupVerified": False}
        retain(evidence)
        cleanup_deadline = time.monotonic() + cleanup_timeout_sec
        try:
            evidence["cancelResult"] = await asyncio.wait_for(
                call("unity_task_cancel", {"taskId": task_id}), timeout=min(10, cleanup_timeout_sec))
            retain(evidence)
            while time.monotonic() < cleanup_deadline:
                remaining = cleanup_deadline - time.monotonic()
                cleaned = await asyncio.wait_for(
                    call("unity_task_status", {"taskId": task_id, "detailLevel": "summary"}),
                    timeout=min(10, remaining),
                )
                evidence["lastState"] = cleaned
                # A failed observation is never promoted to release success.
                if cleaned.get("terminal"):
                    evidence["cleanupObservedTerminal"] = True
                    retain(evidence)
                    break
                retain(evidence)
                await asyncio.sleep(min(poll_interval_sec, max(0, cleanup_deadline - time.monotonic())))
        except (Exception, asyncio.CancelledError) as cleanup_error:
            evidence["cleanupError"] = str(cleanup_error)
        finally:
            retain(evidence)
        raise


async def main():
    endpoint = os.environ.get("UPILOT_MCP_URL") or "http://127.0.0.1:8011/mcp"
    async with streamablehttp_client(endpoint) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            async def call(name, args):
                result = await session.call_tool(name, args)
                payload = next(json.loads(item.text) for item in result.content if getattr(item, "type", "") == "text")
                if result.isError or not payload.get("ok"):
                    raise RuntimeError(str(payload))
                return payload["data"]
            status = await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False})
            project = ROOT / "Tests~" / "UPilotTest"
            if not status.get("connected") or not status.get("serverReady") or Path(status["paths"]["unityProjectAbsolute"]).resolve() != project.resolve():
                raise RuntimeError("Connect the canonical checked-out project before running acceptance.")
            started = await call("unity_task_start", {
                "taskName": "Release evidence", "toolName": "unity_upilot_acceptance_run", "retryCount": 0,
                "timeoutS": 900, "toolArgs": {"fixtures": list(REQUIRED_FIXTURES), "requireTests": True,
                                             "timeoutSec": 840, "writeArtifact": True},
            })
            output = ROOT / "artifacts" / "unity-release-evidence"
            output.mkdir(parents=True, exist_ok=True)
            def retain(state):
                (output / "observation.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
            result = await observe_acceptance(call, started, retain)
            if result.get("status") != "completed":
                raise RuntimeError(str(result))
            artifact = result["artifact"]
            summary = Path(artifact["path"]).resolve()
            summary.relative_to(project.resolve())
            content = summary.read_bytes()
            digest = hashlib.sha256(content).hexdigest()
            if artifact["sha256"] != digest or artifact["bytes"] != len(content):
                raise RuntimeError("Acceptance artifact changed after completion.")
            verify_required_summary(json.loads(content), source_identity(ROOT))
            (output / "summary.json").write_bytes(content)
            (output / "evidence.json").write_text(json.dumps({"sha256": digest, "bytes": len(content)}), encoding="utf-8")


if __name__ == "__main__":
    asyncio.run(main())
