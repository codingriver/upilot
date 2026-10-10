"""Bounded evidence client for the two authorized repository acceptance projects."""
import argparse
import asyncio
import hashlib
import json
import os
import re
import sys
import time
import uuid
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient


class CallPreflightError(ValueError):
    """A fixed, locally authored diagnostic safe to include in evidence."""


def _sensitive_key(key: str) -> bool:
    normalized = re.sub(r"[^a-z0-9]", "", key.lower())
    return normalized.endswith(("token", "password", "secret", "apikey", "cookie")) or normalized == "authorization"


def _redact(value):
    """Redact structured credentials and their echoes without mutating call arguments."""
    secrets = set()

    def collect(item, sensitive=False):
        if isinstance(item, dict):
            for key, child in item.items():
                collect(child, sensitive or _sensitive_key(str(key)))
        elif isinstance(item, list):
            for child in item:
                collect(child, sensitive)
        elif sensitive and isinstance(item, str) and item:
            secrets.add(item)
        elif isinstance(item, str):
            try:
                nested = json.loads(item)
            except ValueError:
                return
            if isinstance(nested, (dict, list)):
                collect(nested)

    collect(value)

    def clean(item):
        if isinstance(item, dict):
            return {key: "[REDACTED]" if _sensitive_key(str(key)) else clean(child)
                    for key, child in item.items()}
        if isinstance(item, list):
            return [clean(child) for child in item]
        if isinstance(item, str):
            # Some tools embed another JSON payload in text.
            try:
                nested = json.loads(item)
            except (ValueError, TypeError):
                nested = None
            if isinstance(nested, (dict, list)):
                item = json.dumps(_redact(nested), ensure_ascii=False)
            for secret in sorted(secrets, key=len, reverse=True):
                item = item.replace(secret, "[REDACTED]")
            item = re.sub(r"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", r"\1 [REDACTED]", item)
            return re.sub(
                r"""(?ix)(\b(?:\w*token|password|secret|api[_-]?key|authorization|(?:set-)?cookie)
                \b["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\r\n,;}]+)""",
                r"\1[REDACTED]", item)
        return item

    return clean(value)


def _validate_call(tool: str, arguments: dict) -> None:
    # A one-shot evidence client cannot safely retain capture ownership. Reject
    # known ownership workflows, including nested task/proxy calls, before sending.
    blocked = {"unity_console_capture_start", "unity_console_capture_stop"}
    if tool in blocked:
        raise CallPreflightError("Capture ownership workflows require a native client with secure token handling.")

    def inspect(item):
        if isinstance(item, dict):
            for key, child in item.items():
                if _sensitive_key(str(key)):
                    raise CallPreflightError("Credential-bearing arguments require a native client; do not pass tokens on the command line.")
                if key in {"toolName", "tool"} and isinstance(child, str) and child in blocked:
                    raise CallPreflightError("Nested capture ownership workflows require a native client.")
                inspect(child)
        elif isinstance(item, list):
            for child in item:
                inspect(child)

    inspect(arguments)


def _connection_summary(data: dict) -> dict:
    runtime = data.get("runtimeIdentity") or {}
    session = data.get("session") or {}
    return {
        "statusReceived": True,
        "actualProject": (data.get("paths") or {}).get("unityProjectAbsolute", ""),
        "connected": data.get("connected") is True,
        "serverReady": data.get("serverReady") is True,
        "serverProcessId": (runtime.get("mcpServer") or {}).get("processId"),
        "unityProcessId": (runtime.get("unityEditor") or {}).get("processId"),
        "bridgeSessionId": session.get("sessionId"),
        "bridgeIdentityContractVersion": session.get("identityContractVersion"),
        "unityVersion": session.get("unityVersion"),
        # Current status exposes a Server PID, not loaded-source attestation.
        "deploymentFreshness": "unverified",
    }


def _print_report(report: dict, observation: dict, *, stream=None) -> None:
    safe = _redact({"observation": observation, "report": report})["report"]
    print(json.dumps(safe, ensure_ascii=False), file=stream or sys.stdout)


def _write_observation(output: Path, observation: dict) -> tuple[Path, int, str]:
    """Write one immutable observation even when the MCP call was never sent."""
    output.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(_redact(observation), ensure_ascii=False, indent=2).encode("utf-8")
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


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--project", default="UPilotTest", choices=["UPilotTest", "UPilotTest2022"])
    parser.add_argument("--tool", required=True)
    parser.add_argument("--args", default="{}")
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
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
        "args": "[Unparsed arguments omitted]",
        "transport": {"sendState": "not_sent", "stage": "argument_preflight"},
        "connection": {},
    }
    failure: BaseException | None = None
    result: dict = {}
    try:
        parsed_args = _decode_arguments(args.args)
        observation["args"] = parsed_args
        _validate_call(args.tool, parsed_args)
        observation["transport"]["stage"] = "connection_preflight"
        async with asyncio.timeout(650):
            async with streamable_http_client(f"http://127.0.0.1:{args.port}/mcp") as (read, write, _):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    client = DiscoveryProxyClient(session)
                    status = await client.call("unity_tool_call", {"toolName": "unity_mcp_status", "args": {"forceFresh": True, "includeCapabilities": False}})
                    status_data = status.get("data") or {}
                    actual = (status_data.get("paths") or {}).get("unityProjectAbsolute", "")
                    observation["connection"] = _connection_summary(status_data)
                    if not actual or Path(actual).resolve() != project:
                        raise CallPreflightError("Project identity mismatch.")
                    if not status.get("ok") or not status_data.get("connected") or not status_data.get("serverReady"):
                        raise CallPreflightError("The exact project Bridge and server must be connected and ready.")
                    observation["transport"] = {"sendState": "sent_unknown", "stage": "tool_call"}
                    result = await client.call("unity_tool_call", {"toolName": args.tool, "args": observation["args"]})
                    observation["transport"] = {"sendState": "response_received", "stage": "tool_call"}
                    observation["result"] = result
    except BaseException as exc:
        failure = exc
        # Protocol exceptions can echo complete request/result payloads. Do not
        # persist them or re-raise them into an unredacted CLI traceback.
        observation["error"] = {
            "type": type(exc).__name__,
            "message": str(exc) if isinstance(exc, CallPreflightError) else
            "Call failed; inspect connection and transport stage. Raw exception text omitted.",
        }
    finally:
        observation["endedAtUtcMs"] = int(time.time() * 1000)
        try:
            written_path, written_bytes, written_sha256 = _write_observation(output, observation)
        except OSError as write_error:
            # Do not imply that a connection observation exists when its
            # exclusive write failed.  stderr retains the target and error type
            # for manual recovery without overwriting an older observation.
            _print_report({
                "ok": False,
                "observationId": observation["observationId"],
                "evidenceWritten": False,
                "path": str(output),
                "writeError": {"type": type(write_error).__name__},
                "observationError": observation.get("error"),
            }, observation, stream=sys.stderr)
            return 1
    if failure is not None:
        _print_report({"ok": False, "error": observation["error"], "observationId": observation["observationId"],
                          "connection": observation["connection"],
                          "transport": observation["transport"], "path": str(written_path), "bytes": written_bytes,
                          "sha256": written_sha256}, observation)
        return 1
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
    _print_report({"ok": result.get("ok"), "error": result.get("error", {}).get("code") if result.get("error") else None,
                      "data": selected, "observationId": observation["observationId"],
                      "connection": observation["connection"],
                      "transport": observation["transport"], "path": str(written_path), "bytes": written_bytes,
                      "sha256": written_sha256}, observation)
    return 0 if result.get("ok") is True else 1


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
