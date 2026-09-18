"""Run the bounded P1 profiler control matrix through discovery/proxy only."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import math
import os
import time
import traceback
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from upilot_mcp.client_probe import DiscoveryProxyClient


FIXTURE = "CodingRiver.UPilot.Tests.UPilotProfilerAcceptanceFixture"
HIGH_COST_TYPE = "CodingRiver.UPilot.Tests.UPilotProfilerReliabilityTests"
PLAY_MODES = {"lightPlayerLoop", "fixed20ms"}
EDIT_MODES = {"idleWindow", "dualWindow"}
DEFAULT_MODES = ["lightPlayerLoop", "fixed20ms", "idleWindow", "dualWindow"]


def now_ms() -> int:
    return int(time.time() * 1000)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def atomic_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.{time.time_ns()}.tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def exact_path(left: str | Path, right: str | Path) -> bool:
    return os.path.normcase(str(Path(left).resolve())) == os.path.normcase(str(Path(right).resolve()))


class MatrixRunner:
    def __init__(self, client: DiscoveryProxyClient, project: Path, output: Path,
                 warmup_sec: float, phase_sec: float, rounds: int, modes: list[str]):
        self.client = client
        self.project = project.resolve()
        self.output = output.resolve()
        self.warmup_sec = warmup_sec
        self.phase_sec = phase_sec
        self.rounds = rounds
        self.modes = modes
        self.calls_path = self.output / "calls.jsonl"
        self.report_path = self.output / "checkpoint.json"
        self.capture_id = ""
        self.fixture_active = False
        self.entered_play = False
        self.report = {
            "schemaVersion": 1,
            "status": "running",
            "acceptancePassed": False,
            "startedAt": now_ms(),
            "projectPath": str(self.project),
            "endpoint": "http://127.0.0.1:8011/mcp",
            "clientExposedTools": list(DiscoveryProxyClient.exposed_tools),
            "clientToolListInjected": "unknown",
            "settings": {
                "modes": modes,
                "rounds": rounds,
                "warmupSec": warmup_sec,
                "beforeSec": phase_sec,
                "duringSec": phase_sec,
                "afterSec": phase_sec,
                "captureMode": "lowOverhead",
                "sampleEveryFrames": 1,
                "maxSamples": 16384,
                "maxMarkers": 0,
                "includeDefaultAiMarkers": False,
                "repaintHz": 10,
            },
            "sourceIdentity": self.source_identity(),
            "rounds": [],
            "highCost": None,
            "checks": {},
            "errors": [],
            "cleanup": {},
        }

    def source_identity(self) -> dict:
        root = self.project.parents[1]
        paths = [
            root / "Editor" / "Core" / "UPilotRuntimeDiagnosticsService.cs",
            root / "Editor" / "Core" / "UPilotWindowTelemetry.cs",
            root / "Tests" / "Editor" / "UPilotP1AcceptanceFixtures.cs",
            root / "Tests" / "Editor" / "UPilotProfilerReliabilityTests.cs",
            Path(__file__).resolve(),
        ]
        return {"files": [{"path": str(path), "bytes": path.stat().st_size, "sha256": sha256(path)}
                          for path in paths]}

    def save(self) -> None:
        atomic_json(self.report_path, self.report)

    def append_call(self, value: dict) -> None:
        self.calls_path.parent.mkdir(parents=True, exist_ok=True)
        with self.calls_path.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
            stream.flush()
            os.fsync(stream.fileno())

    async def call(self, tool: str, arguments: dict | None = None, *, label: str = "") -> dict:
        record = {"at": now_ms(), "label": label, "tool": tool, "args": arguments or {}}
        try:
            response = await self.client.call("unity_tool_call", {
                "toolName": tool, "args": arguments or {},
            })
            record["response"] = response
            self.append_call(record)
            if response.get("ok") is not True:
                raise RuntimeError(f"{tool}: {response.get('error')}")
            return response.get("data") or {}
        except Exception as exc:
            record["exception"] = f"{type(exc).__name__}: {exc}"
            self.append_call(record)
            raise

    async def reflection(self, method: str, values: list[str] | None = None, *, label: str = "") -> dict:
        args = values or []
        payload = {
            "typeName": FIXTURE,
            "methodName": method,
            "resultMode": "inline",
        }
        if args:
            payload["arguments"] = [{"value": {"kind": "literal", "typeName": "System.String", "value": value}}
                                    for value in args]
            payload["parameterTypeNames"] = ["System.String"] * len(args)
        data = await self.call("unity_reflection_call", payload, label=label)
        result = data.get("result")
        if not isinstance(result, str):
            raise ValueError(f"{method} did not return fixture JSON.")
        return json.loads(result)

    async def status(self, *, label: str) -> dict:
        status = await self.call("unity_mcp_status", {"forceFresh": True, "includeCapabilities": False}, label=label)
        actual = (status.get("paths") or {}).get("unityProjectAbsolute") or ""
        if not actual or not exact_path(actual, self.project):
            raise ValueError(f"Exact project identity changed: {actual}")
        return status

    async def wait_ready_mode(self, expected: str, timeout_sec: float = 120) -> dict:
        deadline = time.monotonic() + timeout_sec
        last = None
        while time.monotonic() < deadline:
            try:
                last = await self.status(label=f"wait-{expected}")
                state = last.get("executionState") or {}
                mode = str(state.get("playModeState") or "").lower()
                mode_ready = state.get("ready") is True if expected == "edit" else state.get("unityConnected") is True
                if (mode == expected and mode_ready and state.get("authoritative") is True
                        and state.get("isStale") is False):
                    return state
            except Exception as exc:
                self.report["errors"].append({"at": now_ms(), "phase": f"wait-{expected}",
                                              "transient": f"{type(exc).__name__}: {exc}"})
                self.save()
            await asyncio.sleep(1)
        raise TimeoutError(f"Unity did not reach authoritative ready {expected}: {last}")

    async def set_mode(self, expected: str) -> None:
        current = await self.status(label=f"before-mode-{expected}")
        state = current.get("executionState") or {}
        if str(state.get("playModeState") or "").lower() == expected and state.get("ready"):
            return
        tool = "unity_playmode_start" if expected == "play" else "unity_playmode_stop"
        await self.call(tool, label=f"set-mode-{expected}")
        if expected == "play":
            self.entered_play = True
        await self.wait_ready_mode(expected)

    async def sleep_phase(self, label: str, seconds: float) -> None:
        started = time.monotonic()
        deadline = started + seconds
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            await asyncio.sleep(min(15, remaining))
            elapsed = time.monotonic() - started
            print(f"PHASE {label} {elapsed:.1f}/{seconds:.1f}s", flush=True)

    async def begin_fixture(self, mode: str, round_number: int) -> dict:
        label = f"{mode}-r{round_number}-begin"
        try:
            result = await self.reflection("Begin", [mode, "10"], label=label)
        except Exception:
            result = await self.reflection("Status", label=label + "-recover")
            if result.get("active") is not True or result.get("mode") != mode:
                raise RuntimeError("Fixture start response was lost and its identity could not be recovered.")
        self.fixture_active = True
        return result

    async def reset_and_measure(self, mode: str, round_number: int, phase: str) -> dict:
        await self.reflection("ResetMeasurement", label=f"{mode}-r{round_number}-{phase}-reset")
        await self.sleep_phase(f"{mode}/r{round_number}/{phase}", self.phase_sec)
        result = await self.reflection("Status", label=f"{mode}-r{round_number}-{phase}-status")
        atomic_json(self.output / mode / f"round-{round_number}" / f"{phase}.json", result)
        return result

    async def start_profiler(self, mode: str, round_number: int) -> dict:
        title = f"P1-{mode}-round-{round_number}"
        directory = self.output / mode / f"round-{round_number}" / "profiler"
        arguments = {
            "durationSec": self.phase_sec,
            "sampleEveryFrames": 1,
            "title": title,
            "outputDirectory": str(directory),
            "maxMarkers": 0,
            "captureMode": "lowOverhead",
            "maxSamples": 16384,
            "includeDefaultAiMarkers": False,
        }
        try:
            result = await self.call("unity_profiler_capture_start", arguments,
                                     label=f"{mode}-r{round_number}-capture-start")
        except Exception:
            result = await self.call("unity_profiler_capture_status", {},
                                     label=f"{mode}-r{round_number}-capture-recover")
            if result.get("status") != "Running" or result.get("title") != title:
                raise RuntimeError("Profiler start response was lost and its identity could not be recovered.")
        self.capture_id = result.get("captureId") or ""
        if not self.capture_id:
            raise ValueError("Profiler capture identity missing.")
        return result

    async def await_profiler_terminal(self, mode: str, round_number: int) -> dict:
        deadline = time.monotonic() + 15
        result = None
        while time.monotonic() < deadline:
            result = await self.call("unity_profiler_capture_status", {"captureId": self.capture_id},
                                     label=f"{mode}-r{round_number}-capture-status")
            if result.get("status") != "Running":
                self.capture_id = ""
                return result
            await asyncio.sleep(.5)
        raise TimeoutError(f"Profiler capture did not terminate: {result}")

    def read_profiler_artifact(self, terminal: dict) -> tuple[dict, dict]:
        path_text = terminal.get("jsonPath") or ((terminal.get("artifacts") or {}).get("jsonPath"))
        if not path_text:
            raise ValueError("Profiler terminal has no JSON artifact path.")
        path = Path(path_text).resolve()
        if self.project not in path.parents or not path.is_file():
            raise ValueError(f"Profiler JSON is missing or outside the project: {path}")
        raw = path.read_bytes()
        return json.loads(raw.decode("utf-8-sig")), {
            "path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
        }

    def rate(self, mode: str, sample: dict) -> float:
        return float(sample.get("framesPerSec") if mode in PLAY_MODES else sample.get("updatesPerSec") or 0)

    def window_summary(self, artifact: dict) -> dict:
        grouped: dict[str, dict] = {}
        for sample in artifact.get("windowTelemetry") or []:
            identity = str(sample.get("instanceId") or "")
            item = grouped.setdefault(identity, {"onGuiDurationsMs": [], "allocationBytes": [],
                                                  "onGuiCalls": 0, "repaintRequests": 0,
                                                  "threadIds": set(), "frameSources": set()})
            calls = int(sample.get("onGuiCalls") or 0)
            item["onGuiCalls"] += calls
            item["repaintRequests"] += int(sample.get("repaintRequests") or 0)
            item["threadIds"].add(int(sample.get("threadId") or 0))
            item["frameSources"].add(str(sample.get("frameSource") or ""))
            if calls > 0:
                item["onGuiDurationsMs"].append(float(sample.get("onGuiMs") or 0) / calls)
                if sample.get("allocationMeasurementAvailable") is True:
                    item["allocationBytes"].append(float(sample.get("allocatedBytes") or 0) / calls)
        result = []
        for identity, item in grouped.items():
            durations = item.pop("onGuiDurationsMs")
            allocations = item.pop("allocationBytes")
            item["instanceId"] = identity
            item["threadIds"] = sorted(item["threadIds"])
            item["frameSources"] = sorted(item["frameSources"])
            item["onGuiP50Ms"] = percentile(durations, .5)
            item["onGuiP95Ms"] = percentile(durations, .95)
            item["allocationP50Bytes"] = percentile(allocations, .5)
            item["allocationP95Bytes"] = percentile(allocations, .95)
            item["allocationVerified"] = bool(allocations)
            result.append(item)
        return {"dropped": int(artifact.get("droppedWindowTelemetry") or 0),
                "instances": sorted(result, key=lambda item: item["instanceId"])}

    def summarize_round(self, mode: str, round_number: int, before: dict, during: dict,
                        after: dict, terminal: dict, artifact: dict, artifact_identity: dict) -> dict:
        summaries = {item.get("name"): item for item in artifact.get("summaries") or []}
        before_rate, during_rate, after_rate = (self.rate(mode, item) for item in (before, during, after))
        deltas = []
        for control in (before_rate, after_rate):
            deltas.append(abs(during_rate - control) * 100 / control if control > 0 else math.inf)
        collector = summaries.get("collectorMs") or {}
        allocation = summaries.get("collectorAllocatedBytes") or {}
        window = self.window_summary(artifact)
        expected_windows = 2 if mode == "dualWindow" else (1 if mode == "idleWindow" else 0)
        fixture_ids = set(during.get("windowInstanceIds") or [])
        measured_ids = {item["instanceId"] for item in window["instances"] if item["onGuiCalls"] > 0}
        checks = {
            "terminalCompleted": terminal.get("status") == "Completed",
            "collectorP95Under1Ms": float(collector.get("p95", math.inf)) < 1,
            "allocationUnder10KiB": (float(allocation.get("p95", math.inf)) < 10240
                                      if artifact.get("allocationMeasurementAvailable") is True else None),
            "rateChangeUnder5Percent": max(deltas) < 5,
            "lowCostNotDisturbed": artifact.get("observerEffectDetected") is False,
            "captureSettingsExact": artifact.get("captureMode") == "lowOverhead"
                and artifact.get("sampleEveryFrames") == 1 and artifact.get("maxSamples") == 16384
                and artifact.get("selectedMarkers") == [] and artifact.get("requestedMarkerPatterns") == [],
            "windowCountExact": len(fixture_ids) == expected_windows,
            "windowTelemetryExact": measured_ids == fixture_ids if expected_windows else len(measured_ids) == 0,
            "windowTelemetryNotDropped": window["dropped"] == 0,
        }
        return {
            "mode": mode, "round": round_number,
            "rateMetric": "framesPerSec" if mode in PLAY_MODES else "updatesPerSec",
            "beforeRate": before_rate, "duringRate": during_rate, "afterRate": after_rate,
            "duringVsBeforePercent": deltas[0], "duringVsAfterPercent": deltas[1],
            "collectorP95Ms": collector.get("p95"),
            "collectorAllocationP95Bytes": allocation.get("p95") if allocation else None,
            "allocationMeasurementAvailable": artifact.get("allocationMeasurementAvailable") is True,
            "allocationMeasurementSource": artifact.get("allocationMeasurementSource") or "",
            "actualSamplesPerSec": artifact.get("actualSamplesPerSec"),
            "sampleCount": artifact.get("sampleCount"), "droppedSamples": artifact.get("droppedSamples"),
            "startupMs": artifact.get("startupMs"), "discoveryMs": artifact.get("discoveryMs"),
            "serializationMs": artifact.get("serializationMs"),
            "samplingClock": artifact.get("samplingClock"),
            "editModeSampleHzCap": artifact.get("editModeSampleHzCap"),
            "selectedCounters": artifact.get("selectedCounters") or [],
            "suppressedCounters": artifact.get("suppressedCounters") or [],
            "selectedMarkers": artifact.get("selectedMarkers") or [],
            "requestedMarkerPatterns": artifact.get("requestedMarkerPatterns") or [],
            "observerEffectDetected": artifact.get("observerEffectDetected"),
            "observerEffectVerification": artifact.get("observerEffectVerification"),
            "unavailableCounters": artifact.get("unavailableCounters") or [],
            "windowTelemetry": window, "artifact": artifact_identity, "checks": checks,
        }

    async def run_round(self, mode: str, round_number: int) -> None:
        print(f"ROUND {mode} {round_number}/{self.rounds} START", flush=True)
        await self.begin_fixture(mode, round_number)
        try:
            await self.sleep_phase(f"{mode}/r{round_number}/warmup", self.warmup_sec)
            before = await self.reset_and_measure(mode, round_number, "before")
            await self.reflection("ResetMeasurement", label=f"{mode}-r{round_number}-during-reset")
            await self.start_profiler(mode, round_number)
            await self.sleep_phase(f"{mode}/r{round_number}/during", self.phase_sec + .25)
            during = await self.reflection("Status", label=f"{mode}-r{round_number}-during-status")
            atomic_json(self.output / mode / f"round-{round_number}" / "during.json", during)
            terminal = await self.await_profiler_terminal(mode, round_number)
            artifact, artifact_identity = self.read_profiler_artifact(terminal)
            atomic_json(self.output / mode / f"round-{round_number}" / "profiler-terminal.json", terminal)
            after = await self.reset_and_measure(mode, round_number, "after")
            summary = self.summarize_round(mode, round_number, before, during, after,
                                           terminal, artifact, artifact_identity)
            self.report["rounds"].append(summary)
            self.save()
            print(f"ROUND {mode} {round_number} COMPLETE "
                  f"collectorP95={summary['collectorP95Ms']} rateDelta="
                  f"{max(summary['duringVsBeforePercent'], summary['duringVsAfterPercent']):.3f}%", flush=True)
        finally:
            if self.fixture_active:
                try:
                    stopped = await self.reflection("Stop", label=f"{mode}-r{round_number}-fixture-stop")
                    atomic_json(self.output / mode / f"round-{round_number}" / "fixture-stop.json", stopped)
                finally:
                    self.fixture_active = False

    async def high_cost(self) -> None:
        title = "P1-profiler-high-cost-public-route"
        directory = self.output / "high-cost"
        result = await self.call("unity_profiler_capture_start", {
            "durationSec": 2,
            "sampleEveryFrames": 1,
            "title": title,
            "outputDirectory": str(directory),
            "maxMarkers": 0,
            "captureMode": "lowOverhead",
            "maxSamples": 128,
            "includeDefaultAiMarkers": False,
            "telemetryTypeName": HIGH_COST_TYPE,
            "telemetryMethodName": "ExpensiveTelemetry",
        }, label="high-cost-start")
        self.capture_id = result.get("captureId") or ""
        if not self.capture_id:
            raise ValueError("High-cost capture identity missing.")
        await self.sleep_phase("high-cost", 3)
        terminal = await self.await_profiler_terminal("highCost", 1)
        artifact, identity = self.read_profiler_artifact(terminal)
        summaries = {item.get("name"): item for item in artifact.get("summaries") or []}
        checks = {
            "observerEffectDetected": artifact.get("observerEffectDetected") is True,
            "budgetExceeded": artifact.get("observerEffectVerification") == "budgetExceeded",
            "notComparable": artifact.get("comparableBaseline") is False,
            "collectorP95Over15Ms": float((summaries.get("collectorMs") or {}).get("p95", 0)) > 15,
        }
        self.report["highCost"] = {
            "captureId": artifact.get("captureId"), "status": artifact.get("status"),
            "observerEffectDetected": artifact.get("observerEffectDetected"),
            "observerEffectVerification": artifact.get("observerEffectVerification"),
            "comparableBaseline": artifact.get("comparableBaseline"),
            "collectorP95Ms": (summaries.get("collectorMs") or {}).get("p95"),
            "artifact": identity, "checks": checks,
        }
        self.save()

    def finalize_checks(self) -> None:
        expected = len(self.modes) * self.rounds
        rounds = self.report["rounds"]
        required_boolean_checks = []
        allocation_checks = []
        for item in rounds:
            for name, value in item["checks"].items():
                if name == "allocationUnder10KiB":
                    allocation_checks.append(value)
                else:
                    required_boolean_checks.append(value)
        high_checks = list((self.report.get("highCost") or {}).get("checks", {}).values())
        allocation_verified = bool(allocation_checks) and all(value is True for value in allocation_checks)
        allocation_unverified = bool(allocation_checks) and all(value is None for value in allocation_checks)
        self.report["checks"] = {
            "roundCountExact": len(rounds) == expected,
            "allMeasuredGatesPassed": len(rounds) == expected and all(value is True for value in required_boolean_checks),
            "allocationGateVerified": allocation_verified,
            "allocationGateUnverified": allocation_unverified,
            "highCostGatePassed": bool(high_checks) and all(value is True for value in high_checks),
        }
        # An unavailable allocation counter is preserved as unverified, never converted into a pass.
        self.report["acceptancePassed"] = all(self.report["checks"].get(name) is True for name in (
            "roundCountExact", "allMeasuredGatesPassed", "allocationGateVerified", "highCostGatePassed"))
        self.report["status"] = "completed" if self.report["acceptancePassed"] else "completed_unverified_or_failed"

    async def cleanup(self) -> None:
        cleanup = self.report["cleanup"]
        if self.capture_id:
            try:
                status = await self.call("unity_profiler_capture_status", {"captureId": self.capture_id},
                                         label="cleanup-capture-status")
                if status.get("status") == "Running":
                    stopped = await self.call("unity_profiler_capture_stop", {"captureId": self.capture_id},
                                              label="cleanup-capture-stop")
                    cleanup["captureStopped"] = stopped.get("status") != "Running"
                else:
                    cleanup["captureStopped"] = True
            except Exception as exc:
                cleanup["captureError"] = f"{type(exc).__name__}: {exc}"
            self.capture_id = ""
        try:
            fixture = await self.reflection("Status", label="cleanup-fixture-status")
            if fixture.get("active") is True:
                await self.reflection("Stop", label="cleanup-fixture-stop")
            final_fixture = await self.reflection("Status", label="cleanup-fixture-final")
            cleanup["fixtureInactive"] = final_fixture.get("active") is False \
                and not final_fixture.get("windowInstanceIds")
        except Exception as exc:
            cleanup["fixtureError"] = f"{type(exc).__name__}: {exc}"
        try:
            status = await self.status(label="cleanup-mode-status")
            if str((status.get("executionState") or {}).get("playModeState") or "").lower() != "edit":
                await self.set_mode("edit")
            final_status = await self.status(label="cleanup-final-status")
            state = final_status.get("executionState") or {}
            cleanup["editModeRestored"] = str(state.get("playModeState") or "").lower() == "edit" \
                and state.get("ready") is True and state.get("authoritative") is True and state.get("isStale") is False
            cleanup["finalExecutionState"] = state
        except Exception as exc:
            cleanup["modeError"] = f"{type(exc).__name__}: {exc}"
        self.save()

    async def run(self) -> None:
        self.output.mkdir(parents=True, exist_ok=False)
        self.save()
        baseline = await self.status(label="baseline")
        state = baseline.get("executionState") or {}
        if str(state.get("playModeState") or "").lower() != "edit" or state.get("ready") is not True:
            raise ValueError("Matrix requires a fresh authoritative EditMode baseline.")
        discovery = await self.client.call("unity_tools_find", {
            "query": "reflection profiler playmode", "limit": 30, "availability": "available",
        })
        self.append_call({"at": now_ms(), "label": "discovery", "tool": "unity_tools_find",
                          "args": {"query": "reflection profiler playmode"}, "response": discovery})
        names = {item.get("name") for item in ((discovery.get("data") or {}).get("tools") or [])}
        required = {"unity_reflection_call", "unity_profiler_capture_start", "unity_profiler_capture_status",
                    "unity_profiler_capture_stop", "unity_playmode_start", "unity_playmode_stop"}
        if not required.issubset(names):
            raise ValueError(f"Required tools are not discoverable: {sorted(required - names)}")
        for mode in self.modes:
            await self.set_mode("play" if mode in PLAY_MODES else "edit")
            for round_number in range(1, self.rounds + 1):
                await self.run_round(mode, round_number)
        await self.set_mode("edit")
        await self.high_cost()
        self.finalize_checks()


async def async_main(args) -> int:
    root = Path(__file__).resolve().parents[2]
    project = (root / "Tests~" / "UPilotTest").resolve()
    output_root = project / "Log" / "P0P1" / "ProfilerAcceptance"
    output = output_root / args.label
    if output.exists():
        raise ValueError(f"Evidence directory already exists: {output}")
    modes = args.modes or DEFAULT_MODES
    invalid = set(modes) - PLAY_MODES - EDIT_MODES
    if invalid:
        raise ValueError(f"Unknown modes: {sorted(invalid)}")
    runner = None
    try:
        async with streamable_http_client(args.url) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                runner = MatrixRunner(DiscoveryProxyClient(session), project, output,
                                      args.warmup_sec, args.phase_sec, args.rounds, modes)
                try:
                    await runner.run()
                except Exception as exc:
                    runner.report["errors"].append({"at": now_ms(), "phase": "run",
                                                    "error": f"{type(exc).__name__}: {exc}",
                                                    "traceback": traceback.format_exc()})
                    runner.report["status"] = "failed"
                finally:
                    await runner.cleanup()
                    runner.report["endedAt"] = now_ms()
                    runner.report["elapsedSec"] = (runner.report["endedAt"] - runner.report["startedAt"]) / 1000
                    runner.save()
    except Exception:
        if runner is not None:
            runner.report["errors"].append({"at": now_ms(), "phase": "transport",
                                            "error": traceback.format_exc()})
            runner.report["status"] = "failed"
            runner.report["endedAt"] = now_ms()
            runner.save()
        raise
    print(json.dumps({"path": str(runner.report_path), "status": runner.report["status"],
                      "passed": runner.report["acceptancePassed"], "checks": runner.report["checks"],
                      "errors": runner.report["errors"]}, ensure_ascii=False), flush=True)
    return 0 if runner.report["acceptancePassed"] else 2


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8011/mcp")
    parser.add_argument("--label", required=True)
    parser.add_argument("--warmup-sec", type=float, default=30)
    parser.add_argument("--phase-sec", type=float, default=60)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--modes", nargs="*", choices=DEFAULT_MODES)
    args = parser.parse_args()
    if args.warmup_sec <= 0 or args.phase_sec < 1 or args.rounds < 1:
        parser.error("warmup-sec and rounds must be positive; phase-sec must be at least 1.")
    return asyncio.run(async_main(args))


if __name__ == "__main__":
    raise SystemExit(main())
