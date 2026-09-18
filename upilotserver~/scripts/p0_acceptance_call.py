"""Bounded evidence client for the two authorized repository acceptance projects."""
import argparse
import asyncio
import hashlib
import json
import os
import sys
import time
import uuid
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient


def _write_observation(output: Path, observation: dict) -> tuple[Path, int, str]:
    """Write one immutable observation even when the MCP call was never sent."""
    output.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(observation, ensure_ascii=False, indent=2).encode("utf-8")
    for attempt in range(1000):
        suffix = "" if attempt == 0 else "-" + observation["observationId"] + ("" if attempt == 1 else "-" + str(attempt))
        candidate = output.with_name(output.stem + suffix + output.suffix)
        try:
            with candidate.open("xb") as handle:
                handle.write(encoded)
                handle.flush()
                os.fsync(handle.fileno())
            return candidate, len(encoded), hashlib.sha256(encoded).hexdigest()
        except FileExistsError:
            continue
    raise FileExistsError("Could not allocate a unique evidence observation path.")


def _decode_arguments(raw: str) -> dict:
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("--args must decode to a JSON object.")
    return value


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--project", default="UPilotTest", choices=["UPilotTest", "UPilotTest2022"])
    parser.add_argument("--tool", required=True)
    parser.add_argument("--args", default="{}")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    project = (root / "Tests~" / args.project).resolve()
    output = (project / "Log" / "P0P1" / args.output).resolve()
    if not output.is_relative_to(project / "Log" / "P0P1"):
        raise ValueError("Evidence must remain in the acceptance evidence directory.")
    observation = {
        "schemaVersion": 1,
        "observationId": "observation-" + uuid.uuid4().hex,
        "startedAtUtcMs": int(time.time() * 1000),
        "requestedProject": str(project),
        "port": args.port,
        "tool": args.tool,
        # Decode inside the guarded observation scope.  A malformed command
        # still receives an immutable, uniquely named preflight record.
        "args": args.args,
        "transport": {"sendState": "not_sent", "stage": "connection_preflight"},
        "connection": {},
    }
    failure: BaseException | None = None
    result: dict = {}
    try:
        parsed_args = _decode_arguments(args.args)
        observation["args"] = parsed_args
        async with asyncio.timeout(650):
            async with streamable_http_client(f"http://127.0.0.1:{args.port}/mcp") as (read, write, _):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    client = DiscoveryProxyClient(session)
                    status = await client.call("unity_tool_call", {"toolName": "unity_mcp_status", "args": {"includeCapabilities": False}})
                    status_data = status.get("data") or {}
                    actual = (status_data.get("paths") or {}).get("unityProjectAbsolute", "")
                    observation["connection"] = {
                        "statusReceived": True,
                        "actualProject": actual,
                        "connected": bool(status_data.get("connected")),
                        "serverReady": bool(status_data.get("serverReady")),
                    }
                    if not actual or Path(actual).resolve() != project:
                        raise ValueError("Project identity mismatch.")
                    if not status.get("ok") or not status_data.get("connected") or not status_data.get("serverReady"):
                        raise ValueError("The exact project Bridge and server must be connected and ready.")
                    observation["transport"] = {"sendState": "sent_unknown", "stage": "tool_call"}
                    result = await client.call("unity_tool_call", {"toolName": args.tool, "args": observation["args"]})
                    observation["transport"] = {"sendState": "response_received", "stage": "tool_call"}
                    observation["result"] = result
    except BaseException as exc:
        failure = exc
        observation["error"] = {"type": type(exc).__name__, "message": str(exc)}
    finally:
        observation["endedAtUtcMs"] = int(time.time() * 1000)
        try:
            written_path, written_bytes, written_sha256 = _write_observation(output, observation)
        except OSError as write_error:
            # Do not imply that a connection observation exists when its
            # exclusive write failed.  stderr retains both target and cause
            # for manual recovery without overwriting an older observation.
            print(json.dumps({
                "ok": False,
                "observationId": observation["observationId"],
                "evidenceWritten": False,
                "path": str(output),
                "writeError": {"type": type(write_error).__name__, "message": str(write_error)},
                "observationError": observation.get("error"),
            }, ensure_ascii=False), file=sys.stderr)
            raise
    if failure is not None:
        print(json.dumps({"ok": False, "error": observation["error"], "observationId": observation["observationId"],
                          "transport": observation["transport"], "path": str(written_path), "bytes": written_bytes,
                          "sha256": written_sha256}, ensure_ascii=False))
        raise failure
    data = result.get("data") or (result.get("error") or {}).get("detail") or {}
    selected = {key: data[key] for key in (
        "status", "phase", "terminal", "outcome", "errorsVerified", "correlationVerified", "lastCompileVerifiedAt",
        "runGuid", "taskId", "operationId", "acceptancePassed", "cleanupVerified", "testIdentityVerified",
        "sourceUnchanged", "total", "passed", "failed", "cleanupSucceeded", "resultAuthoritative",
        "cleanupPending", "unresolvedResources", "runnerState", "recoveryDiagnostic", "artifact",
        "completedLeafCount", "passedSoFar", "failedSoFar", "skippedSoFar", "lastCompletedTest",
        "firstFailure", "intermediate", "preflightOnly", "preflightPassed", "importState",
        "blockingReasons", "runnerStartAttempted",
    ) if key in data}
    nested = data.get("result", {}).get("result", {}) if isinstance(data.get("result"), dict) else {}
    for key in ("acceptancePassed", "runGuid", "cleanupVerified", "testIdentityVerified", "sourceUnchanged", "artifact"):
        if key in nested:
            selected[key] = nested[key]
    print(json.dumps({"ok": result.get("ok"), "error": result.get("error", {}).get("code") if result.get("error") else None,
                      "data": selected, "observationId": observation["observationId"],
                      "transport": observation["transport"], "path": str(written_path), "bytes": written_bytes,
                      "sha256": written_sha256}, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
