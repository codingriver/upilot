"""Probe low-overhead collector allocation on an exact authorized Unity project."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import os
import time
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient


def now_ms() -> int:
    return int(time.time() * 1000)


def atomic_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.{time.time_ns()}.tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def same_path(left: str, right: Path) -> bool:
    return os.path.normcase(str(Path(left).resolve())) == os.path.normcase(str(right.resolve()))


async def main(args) -> int:
    project = Path(args.project).resolve()
    output = project / "Log" / "P0P1" / "ProfilerAllocation" / f"{args.label}.json"
    if output.exists():
        raise ValueError(f"Evidence already exists: {output}")
    report = {
        "schemaVersion": 1,
        "status": "running",
        "passed": False,
        "allocationVerified": False,
        "allocationUnverified": False,
        "startedAt": now_ms(),
        "endpoint": args.url,
        "projectPath": str(project),
        "clientExposedTools": list(DiscoveryProxyClient.exposed_tools),
        "clientToolListInjected": "unknown",
        "settings": {"rounds": args.rounds, "durationSec": args.duration_sec,
                     "captureMode": "lowOverhead", "sampleEveryFrames": 1,
                     "maxSamples": 16384, "maxMarkers": 0,
                     "includeDefaultAiMarkers": False},
        "calls": [],
        "rounds": [],
        "errors": [],
    }

    def save() -> None:
        atomic_json(output, report)

    save()
    async with streamable_http_client(args.url) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            client = DiscoveryProxyClient(session)

            async def call(tool: str, arguments: dict | None = None) -> dict:
                response = await client.call("unity_tool_call", {
                    "toolName": tool, "args": arguments or {},
                })
                report["calls"].append({"at": now_ms(), "tool": tool,
                                        "args": arguments or {}, "response": response})
                save()
                if response.get("ok") is not True:
                    raise RuntimeError(f"{tool}: {response.get('error')}")
                return response.get("data") or {}

            capture_id = ""
            try:
                status = await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False})
                actual = (status.get("paths") or {}).get("unityProjectAbsolute") or ""
                state = status.get("executionState") or {}
                if not same_path(actual, project):
                    raise ValueError(f"Exact project identity mismatch: {actual}")
                if (state.get("ready") is not True or state.get("authoritative") is not True
                        or state.get("isStale") is not False or state.get("playModeState") != "edit"):
                    raise ValueError("A fresh authoritative EditMode baseline is required.")
                report["unityVersion"] = (status.get("session") or {}).get("unityVersion")
                discovery = await client.call("unity_tools_find", {
                    "query": "unity_profiler_capture", "limit": 10, "availability": "available",
                })
                report["calls"].append({"at": now_ms(), "tool": "unity_tools_find",
                                        "args": {"query": "unity_profiler_capture"}, "response": discovery})
                save()
                discovered = {item.get("name") for item in ((discovery.get("data") or {}).get("tools") or [])}
                required = {"unity_profiler_capture_start", "unity_profiler_capture_status",
                            "unity_profiler_capture_stop"}
                if not required.issubset(discovered):
                    raise ValueError(f"Profiler tools are unavailable: {sorted(required - discovered)}")
                for round_number in range(1, args.rounds + 1):
                    started = await call("unity_profiler_capture_start", {
                        "durationSec": args.duration_sec,
                        "sampleEveryFrames": 1,
                        "title": f"P1-allocation-{args.label}-round-{round_number}",
                        "outputDirectory": str(output.parent / args.label / f"round-{round_number}"),
                        "maxMarkers": 0,
                        "captureMode": "lowOverhead",
                        "maxSamples": 16384,
                        "includeDefaultAiMarkers": False,
                    })
                    capture_id = started.get("captureId") or ""
                    if not capture_id:
                        raise ValueError("Profiler capture identity missing.")
                    await asyncio.sleep(args.duration_sec + 1)
                    terminal = await call("unity_profiler_capture_status", {"captureId": capture_id})
                    deadline = time.monotonic() + 15
                    while terminal.get("status") == "Running" and time.monotonic() < deadline:
                        await asyncio.sleep(.5)
                        terminal = await call("unity_profiler_capture_status", {"captureId": capture_id})
                    if terminal.get("status") == "Running":
                        raise TimeoutError("Profiler capture did not terminate.")
                    capture_id = ""
                    artifact_path = Path(terminal.get("jsonPath") or "").resolve()
                    if project not in artifact_path.parents or not artifact_path.is_file():
                        raise ValueError(f"Profiler artifact is missing or outside project: {artifact_path}")
                    raw = artifact_path.read_bytes()
                    artifact = json.loads(raw.decode("utf-8-sig"))
                    summaries = {item.get("name"): item for item in artifact.get("summaries") or []}
                    allocation = summaries.get("collectorAllocatedBytes") or {}
                    item = {
                        "round": round_number,
                        "captureId": artifact.get("captureId"),
                        "status": artifact.get("status"),
                        "allocationMeasurementAvailable": artifact.get("allocationMeasurementAvailable") is True,
                        "allocationMeasurementSource": artifact.get("allocationMeasurementSource") or "",
                        "collectorP95Ms": (summaries.get("collectorMs") or {}).get("p95"),
                        "collectorAllocationP95Bytes": allocation.get("p95") if allocation else None,
                        "sampleCount": artifact.get("sampleCount"),
                        "droppedSamples": artifact.get("droppedSamples"),
                        "actualSamplesPerSec": artifact.get("actualSamplesPerSec"),
                        "samplingClock": artifact.get("samplingClock"),
                        "selectedCounters": artifact.get("selectedCounters") or [],
                        "suppressedCounters": artifact.get("suppressedCounters") or [],
                        "unavailableCounters": artifact.get("unavailableCounters") or [],
                        "artifact": {"path": str(artifact_path), "bytes": len(raw),
                                     "sha256": hashlib.sha256(raw).hexdigest()},
                    }
                    item["allocationUnder10KiB"] = (float(allocation.get("p95", 1e100)) < 10240
                                                     if item["allocationMeasurementAvailable"] else None)
                    report["rounds"].append(item)
                    save()
                    print(f"ROUND {round_number}/{args.rounds} allocationAvailable="
                          f"{item['allocationMeasurementAvailable']} p95={item['collectorAllocationP95Bytes']}",
                          flush=True)
                availability = [item["allocationMeasurementAvailable"] for item in report["rounds"]]
                allocation_checks = [item["allocationUnder10KiB"] for item in report["rounds"]]
                report["allocationVerified"] = len(availability) == args.rounds and all(availability) \
                    and all(value is True for value in allocation_checks)
                report["allocationUnverified"] = len(availability) == args.rounds and not any(availability)
                report["passed"] = report["allocationVerified"]
                report["status"] = "completed" if report["passed"] else "completed_unverified_or_failed"
            except Exception as exc:
                report["status"] = "failed"
                report["errors"].append({"at": now_ms(), "error": f"{type(exc).__name__}: {exc}"})
            finally:
                if capture_id:
                    try:
                        terminal = await call("unity_profiler_capture_status", {"captureId": capture_id})
                        if terminal.get("status") == "Running":
                            stopped = await call("unity_profiler_capture_stop", {"captureId": capture_id})
                            report["captureCleanupSucceeded"] = stopped.get("status") != "Running"
                        else:
                            report["captureCleanupSucceeded"] = True
                    except Exception as exc:
                        report["captureCleanupError"] = f"{type(exc).__name__}: {exc}"
                final = await call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False})
                final_state = final.get("executionState") or {}
                report["finalStateVerified"] = final_state.get("ready") is True \
                    and final_state.get("authoritative") is True and final_state.get("isStale") is False \
                    and final_state.get("playModeState") == "edit"
                report["endedAt"] = now_ms()
                report["elapsedSec"] = (report["endedAt"] - report["startedAt"]) / 1000
                save()
    print(json.dumps({"path": str(output), "status": report["status"], "passed": report["passed"],
                      "allocationVerified": report["allocationVerified"],
                      "allocationUnverified": report["allocationUnverified"],
                      "errors": report["errors"]}, ensure_ascii=False), flush=True)
    return 0 if report["passed"] else 2


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--duration-sec", type=float, default=60)
    values = parser.parse_args()
    if values.rounds < 1 or values.duration_sec < 1:
        parser.error("rounds and duration-sec must be positive.")
    raise SystemExit(asyncio.run(main(values)))
