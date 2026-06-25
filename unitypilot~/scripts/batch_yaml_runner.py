#!/usr/bin/env python3
"""
UnityUIFlow Batch YAML Runner — Agent-side batch executor via MCP HTTP.

Usage:
    python batch_yaml_runner.py --yaml-dir Assets/Examples/Yaml --batch-size 10 --report-dir Reports/AgentBatch
    python batch_yaml_runner.py --retry-from Reports/AgentBatch/batch_002_failed.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.request
from pathlib import Path
from typing import Any

DEFAULT_MCP_URL = "http://127.0.0.1:8011/mcp"


class McpHttpClient:
    def __init__(self, url: str = DEFAULT_MCP_URL):
        self.url = url
        self._req_counter = 0

    def _next_id(self) -> int:
        self._req_counter += 1
        return self._req_counter

    def call(self, method: str, params: dict | None = None) -> dict[str, Any]:
        req = urllib.request.Request(self.url, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        body = json.dumps({
            "jsonrpc": "2.0",
            "id": self._next_id(),
            "method": method,
            "params": params or {},
        }).encode()
        resp = urllib.request.urlopen(req, body, timeout=600)
        data = resp.read().decode()
        events = re.findall(r"data: (.+)", data)
        if events:
            return json.loads(events[0])
        return {}

    def call_tool(self, name: str, arguments: dict | None = None) -> dict[str, Any]:
        return self.call("tools/call", {"name": name, "arguments": arguments or {}})

    def ensure_ready(self, timeout_s: float = 120) -> bool:
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            result = self.call_tool("unity_mcp_status", {})
            text = self._extract_text(result)
            try:
                data = json.loads(text)
                if data.get("ok") and data.get("data", {}).get("connected"):
                    return True
            except Exception:
                pass
            time.sleep(2.0)
        return False

    @staticmethod
    def _extract_text(result: dict) -> str:
        try:
            contents = result["result"]["content"]
            for c in contents:
                if c.get("type") == "text":
                    return c["text"]
        except Exception:
            pass
        return ""


def discover_yaml_files(yaml_dir: str) -> list[str]:
    p = Path(yaml_dir)
    files = sorted([str(f.resolve()) for f in p.glob("*.yaml")])
    return files


def is_negative_test(yaml_path: str) -> bool:
    return "-negative-" in Path(yaml_path).name.lower()


def run_batch(
    client: McpHttpClient,
    yaml_paths: list[str],
    report_dir: str,
    headed: bool = True,
    stop_on_first_failure: bool = False,
    default_timeout_ms: int = 10000,
) -> dict[str, Any]:
    """Legacy synchronous batch call (no per-case progress)."""
    print(f"[Batch] Running {len(yaml_paths)} file(s) -> {report_dir}")
    for yp in yaml_paths:
        marker = " [NEG]" if is_negative_test(yp) else ""
        print(f"  - {yp}{marker}")

    has_negative = any(is_negative_test(yp) for yp in yaml_paths)
    continue_on_step_failure = has_negative  # allow negative tests to run all steps

    result = client.call_tool(
        "unity_uiflow_run_batch",
        {
            "yamlPaths": yaml_paths,
            "batchSize": len(yaml_paths),
            "batchOffset": 0,
            "headed": headed,
            "reportOutputPath": report_dir,
            "screenshotOnFailure": True,
            "stopOnFirstFailure": stop_on_first_failure,
            "continueOnStepFailure": continue_on_step_failure,
            "defaultTimeoutMs": default_timeout_ms,
            "enableVerboseLog": True,
            "debugOnFailure": False,
        },
    )
    return result


def _extract_execution_payload(result: dict) -> dict:
    """Extract the UnityUIFlow payload from an MCP tool result."""
    text = McpHttpClient._extract_text(result)
    if not text:
        return {}
    try:
        parsed = json.loads(text)
        if isinstance(parsed, dict) and parsed.get("ok"):
            data = parsed.get("data", {})
            if "result" in data:
                return data["result"]
            return data
        return parsed
    except Exception:
        return {}


def run_batch_with_polling(
    client: McpHttpClient,
    yaml_paths: list[str],
    report_dir: str,
    headed: bool = True,
    stop_on_first_failure: bool = False,
    default_timeout_ms: int = 10000,
    poll_interval_s: float = 2.0,
    max_total_timeout_s: float = 600.0,
    event_timeout_s: float = 30.0,
    total_all: int = 0,
    batch_offset: int = 0,
) -> dict[str, Any]:
    """Async batch execution with per-case progress via polling.

    1. Calls unityuiflow.run to start the batch and get an executionId.
    2. Polls unityuiflow.results every poll_interval_s.
    3. Prints progress whenever a new case finishes.
    4. If no new case appears for event_timeout_s, prints a warning and
       triggers an extra poll (fallback).
    5. Returns the final aggregated result.

    Falls back to legacy run_batch() if unityuiflow.run is unavailable.
    """
    print(f"[Batch] Running {len(yaml_paths)} file(s) with polling -> {report_dir}")
    for yp in yaml_paths:
        marker = " [NEG]" if is_negative_test(yp) else ""
        print(f"  - {yp}{marker}")

    has_negative = any(is_negative_test(yp) for yp in yaml_paths)
    continue_on_step_failure = has_negative

    # ── 1. Start batch via unity_uiflow_run_batch ──
    start_result = client.call_tool(
        "unity_uiflow_run_batch",
        {
            "yamlPaths": yaml_paths,
            "batchSize": len(yaml_paths),
            "batchOffset": 0,
            "headed": headed,
            "reportOutputPath": report_dir,
            "screenshotOnFailure": True,
            "stopOnFirstFailure": stop_on_first_failure,
            "continueOnStepFailure": continue_on_step_failure,
            "defaultTimeoutMs": default_timeout_ms,
            "enableVerboseLog": True,
            "debugOnFailure": False,
            "totalAll": total_all if total_all > 0 else len(yaml_paths),
        },
    )

    start_payload = _extract_execution_payload(start_result)
    execution_id = start_payload.get("executionId", "")

    if not execution_id:
        error_msg = start_payload.get("error", {}).get("message", "")
        if error_msg and ("Unknown command" in error_msg or "not found" in error_msg.lower()):
            print("[Batch] unity_uiflow_run_batch not available, falling back to legacy unity_uiflow_run_batch")
            legacy_result = run_batch(client, yaml_paths, report_dir, headed, stop_on_first_failure, default_timeout_ms)
            # Convert legacy result to parsed format
            return parse_batch_result(legacy_result, yaml_paths)
        # Handle EDITOR_BUSY: retry after waiting
        if error_msg and "EDITOR_BUSY" in error_msg:
            print(f"[Batch] EDITOR_BUSY detected, waiting before retry...")
            for retry in range(30):
                time.sleep(2.0)
                start_result = client.call_tool(
                    "unity_uiflow_run_batch",
                    {
                        "yamlPaths": yaml_paths,
                        "batchSize": len(yaml_paths),
                        "batchOffset": 0,
                        "headed": headed,
                        "reportOutputPath": report_dir,
                        "screenshotOnFailure": True,
                        "stopOnFirstFailure": stop_on_first_failure,
                        "continueOnStepFailure": continue_on_step_failure,
                        "defaultTimeoutMs": default_timeout_ms,
                        "enableVerboseLog": True,
                        "debugOnFailure": False,
                        "totalAll": total_all if total_all > 0 else len(yaml_paths),
                    },
                )
                start_payload = _extract_execution_payload(start_result)
                execution_id = start_payload.get("executionId", "")
                if execution_id:
                    print(f"[Batch] Retry succeeded, executionId={execution_id}")
                    break
                error_msg = start_payload.get("error", {}).get("message", "")
                if error_msg and "EDITOR_BUSY" in error_msg:
                    print(f"[Batch] EDITOR_BUSY retry {retry + 1}/30...")
                    continue
                # Other error
                break
            if not execution_id:
                print(f"[Batch] Failed to start batch after retries: {start_payload}")
                return {"ok": False, "error": "Failed to obtain executionId after EDITOR_BUSY retries", "raw": start_payload}
        else:
            print(f"[Batch] Failed to start batch: {start_payload}")
            return {"ok": False, "error": "Failed to obtain executionId", "raw": start_payload}

    print(f"[Batch] Started executionId={execution_id}")

    # ── 2. Poll loop ──
    known_cases: set[str] = set()
    final_payload: dict = {}
    deadline = time.monotonic() + max_total_timeout_s
    last_new_case_time = time.monotonic()

    while time.monotonic() < deadline:
        time.sleep(poll_interval_s)

        poll_result = client.call_tool(
            "unity_uiflow_results",
            {"executionId": execution_id},
        )
        poll_payload = _extract_execution_payload(poll_result)
        final_payload = poll_payload

        status = poll_payload.get("status", "")
        total = poll_payload.get("total", len(yaml_paths))
        cases = poll_payload.get("cases", [])
        current_yaml = poll_payload.get("currentYamlPath", "")

        # Detect newly finished cases
        new_cases = []
        for case in cases:
            case_key = f"{case.get('yamlPath','')}#{case.get('caseName','')}"
            if case_key not in known_cases:
                known_cases.add(case_key)
                new_cases.append(case)

        if new_cases:
            last_new_case_time = time.monotonic()
            for case in new_cases:
                case_name = case.get("caseName", "")
                case_status = case.get("status", "")
                progress_str = f"[{len(known_cases)}/{total}]"
                print(f"[Progress] {progress_str} case_finished: {case_name} -> {case_status}")

        # Event-timeout fallback: if no new case for a while, warn and poll immediately once more
        elapsed_since_last = time.monotonic() - last_new_case_time
        if elapsed_since_last > event_timeout_s and status in ("running", "queued"):
            print(f"[Fallback] No new case for {elapsed_since_last:.0f}s, triggering extra progress poll...")
            # Next loop iteration will poll again immediately (sleep already done above)
            last_new_case_time = time.monotonic()  # reset to avoid spam

        if status in ("completed", "failed", "aborted"):
            print(f"[Batch] Execution finished status={status} cases={len(known_cases)}/{total}")
            break

        if current_yaml:
            print(f"[Polling] status={status} completed={len(known_cases)}/{total} current={current_yaml}")

    else:
        print(f"[Batch] Polling timed out after {max_total_timeout_s}s")
        return {"ok": False, "error": "Polling timeout", "raw": final_payload}

    # ── 3. Build result compatible with parse_batch_result ──
    return {
        "ok": True,
        "status": final_payload.get("status"),
        "total": final_payload.get("total", 0),
        "passed": final_payload.get("passed", 0),
        "failed": final_payload.get("failed", 0),
        "errors": final_payload.get("errors", 0),
        "skipped": final_payload.get("skipped", 0),
        "reportPath": final_payload.get("reportPath", report_dir),
        "raw": final_payload,
    }


def parse_batch_result(result: dict, yaml_paths: list[str]) -> dict[str, Any]:
    text = McpHttpClient._extract_text(result)
    if text:
        try:
            payload = json.loads(text)
        except Exception as e:
            return {"ok": False, "error": f"Failed to parse result: {e}", "raw": text}

        if not payload.get("ok"):
            return {"ok": False, "error": payload.get("error", {}).get("message", "Unknown error"), "raw": payload}

        data = payload.get("data", {})
        result_data = data.get("result", {})
    else:
        # Direct result from run_batch_with_polling
        result_data = result.get("raw", {})

    # Reconcile negative tests: case-level Failed/Error is expected.
    # When Unity side converts negative tests to Passed (with [Expected failure] marker),
    # we treat them as passed. Recompute totals directly from cases for accuracy.
    raw_cases = result_data.get("cases", []) or result_data.get("raw", {}).get("cases", [])
    passed = 0
    failed = 0
    errors = 0
    skipped = 0
    negative_expected_failures = 0

    for case in raw_cases:
        case_name = case.get("caseName", "")
        case_status = case.get("status", "")
        # Find matching yaml path by case name heuristics
        matched_yaml = None
        for yp in yaml_paths:
            if case_name.replace(" ", "-").lower() in yp.lower() or Path(yp).stem.replace("-", " ").lower() in case_name.lower():
                matched_yaml = yp
                break

        is_neg = matched_yaml and is_negative_test(matched_yaml)
        if is_neg:
            error_msg = case.get("errorMessage", "")
            if case_status in ("Failed", "Error") or (case_status == "Passed" and "[Expected failure]" in error_msg):
                # Expected failure (either raw or converted by Unity side)
                passed += 1
                negative_expected_failures += 1
            elif case_status == "Passed":
                # Negative test unexpectedly passed -> count as fail
                failed += 1
            elif case_status == "Skipped":
                skipped += 1
            else:
                passed += 1
        else:
            if case_status == "Passed":
                passed += 1
            elif case_status == "Failed":
                failed += 1
            elif case_status == "Error":
                errors += 1
            elif case_status == "Skipped":
                skipped += 1

    total = len(raw_cases)
    status = "completed" if (failed == 0 and errors == 0) else "failed"

    return {
        "ok": True,
        "status": status,
        "total": total,
        "passed": passed,
        "failed": failed,
        "errors": errors,
        "skipped": skipped,
        "negativeExpectedFailures": negative_expected_failures,
        "reportPath": result_data.get("reportPath", result_data.get("reportOutputPath", "")),
        "screenshotPath": result_data.get("screenshotPath", ""),
        "raw": result_data if "cases" in result_data else result_data.get("raw", {}),
    }


def save_failed_manifest(path: Path, yaml_paths: list[str], batch_idx: int, report_dir: str, raw: dict) -> None:
    manifest = {
        "batchIndex": batch_idx,
        "yamlPaths": yaml_paths,
        "reportDir": report_dir,
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "rawResult": raw,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"[Batch] Failed manifest saved to {path}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Batch YAML test runner for UnityUIFlow")
    parser.add_argument("--yaml-dir", default="Assets/Examples/Yaml", help="Directory containing YAML test files")
    parser.add_argument("--batch-size", type=int, default=10, help="Files per batch")
    parser.add_argument("--report-dir", default="Reports/AgentBatch", help="Base report directory")
    parser.add_argument("--headed", type=lambda x: x.lower() in ("1", "true", "yes"), default=True, help="Run in headed mode")
    parser.add_argument("--mcp-url", default=DEFAULT_MCP_URL, help="MCP HTTP endpoint")
    parser.add_argument("--retry-from", default="", help="Path to a failed-manifest JSON to retry")
    parser.add_argument("--stop-on-first-failure", action="store_true", help="Stop immediately on first failure")
    parser.add_argument("--timeout-ms", type=int, default=10000, help="Default step timeout in ms")
    parser.add_argument("--wait-ready", type=int, default=120, help="Seconds to wait for Unity ready")
    args = parser.parse_args()

    client = McpHttpClient(args.mcp_url)

    print("[Setup] Waiting for Unity to be ready...")
    if not client.ensure_ready(timeout_s=args.wait_ready):
        print("[Error] Unity is not connected. Please start Unity Editor and ensure the Bridge is active.")
        return 2
    print("[Setup] Unity ready.")

    if args.retry_from:
        manifest_path = Path(args.retry_from)
        if not manifest_path.exists():
            print(f"[Error] Retry manifest not found: {manifest_path}")
            return 2
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        all_files = manifest["yamlPaths"]
        base_report_dir = manifest["reportDir"]
        start_batch = manifest["batchIndex"]
        print(f"[Retry] Resuming batch {start_batch} with {len(all_files)} file(s)")
    else:
        all_files = discover_yaml_files(args.yaml_dir)
        if not all_files:
            print(f"[Error] No YAML files found in {args.yaml_dir}")
            return 2
        base_report_dir = args.report_dir
        start_batch = 0
        print(f"[Setup] Discovered {len(all_files)} YAML file(s)")

    overall_passed = 0
    overall_failed = 0
    overall_errors = 0
    failed_batches: list[Path] = []

    for i in range(start_batch, (len(all_files) + args.batch_size - 1) // args.batch_size):
        offset = i * args.batch_size
        batch_files = all_files[offset:offset + args.batch_size]
        batch_report_dir = f"{base_report_dir}/batch_{i:03d}"

        mcp_result = run_batch_with_polling(
            client,
            batch_files,
            batch_report_dir,
            headed=args.headed,
            stop_on_first_failure=args.stop_on_first_failure,
            default_timeout_ms=args.timeout_ms,
            total_all=len(all_files),
            batch_offset=offset,
        )
        if "raw" in mcp_result and mcp_result.get("ok"):
            parsed = parse_batch_result(mcp_result, batch_files)
        else:
            parsed = mcp_result

        if not parsed["ok"]:
            print(f"[Batch {i}] MCP call failed: {parsed.get('error')}")
            manifest_path = Path(f"{base_report_dir}/batch_{i:03d}_failed.json")
            save_failed_manifest(manifest_path, batch_files, i, batch_report_dir, parsed.get("raw", {}))
            failed_batches.append(manifest_path)
            if args.stop_on_first_failure:
                return 1
            continue

        neg_info = f" (neg_expected={parsed.get('negativeExpectedFailures', 0)})" if parsed.get('negativeExpectedFailures', 0) > 0 else ""
        print(f"[Batch {i}] Status={parsed['status']} total={parsed['total']} passed={parsed['passed']} failed={parsed['failed']} errors={parsed['errors']} skipped={parsed['skipped']}{neg_info}")
        overall_passed += parsed["passed"]
        overall_failed += parsed["failed"]
        overall_errors += parsed["errors"]

        if parsed["status"] != "completed" or parsed["failed"] > 0 or parsed["errors"] > 0:
            manifest_path = Path(f"{base_report_dir}/batch_{i:03d}_failed.json")
            save_failed_manifest(manifest_path, batch_files, i, batch_report_dir, parsed.get("raw", {}))
            failed_batches.append(manifest_path)
            if args.stop_on_first_failure:
                return 1

    summary = {
        "totalFiles": len(all_files),
        "totalPassed": overall_passed,
        "totalFailed": overall_failed,
        "totalErrors": overall_errors,
        "failedBatches": [str(p) for p in failed_batches],
        "baseReportDir": base_report_dir,
    }
    summary_path = Path(f"{base_report_dir}/summary.json")
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

    print("\n" + "=" * 60)
    print(f"Total files : {len(all_files)}")
    print(f"Passed      : {overall_passed}")
    print(f"Failed      : {overall_failed}")
    print(f"Errors      : {overall_errors}")
    print(f"Summary     : {summary_path}")
    if failed_batches:
        print(f"Failed batches ({len(failed_batches)}):")
        for p in failed_batches:
            print(f"  - {p}")
        print("\nTo retry a failed batch:")
        print(f"  python batch_yaml_runner.py --retry-from {failed_batches[0]}")
        return 1
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())
