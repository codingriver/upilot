#!/usr/bin/env python3
"""Bounded release gate. Does not run Unity, modify versions, commit, tag or publish."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import subprocess
import sys
import time

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "upilotserver~" / "src"))
from upilot_mcp.source_identity import source_identity
from upilot_mcp.release_evidence import verify_release_evidence

CONTRACT_TESTS = (
    "test_acceptance_compact_shader.py", "test_test_selectors.py",
    "test_prefab_patch_and_scene_summary.py", "test_persistent_test_jobs.py",
    "test_operation_runner_and_agent_rules.py", "test_remaining_upilot_todos.py",
    "test_release_quality.py",
    "test_tool_proxy_parity.py",
    "test_write_batch_changes.py", "test_editor_execution_state_v2.py",
    "test_resource_write_contracts.py", "test_operation_cleanup_barriers.py", "test_release_evidence.py",
    "test_release_evidence_collector.py", "test_release_version_sync.py",
    "test_actions_runtime_contracts.py", "test_snapshot_short_tasks.py",
    "test_skill_validation.py", "test_install_upilot.py",
)


def verify_unity_summary(report: dict, source: dict) -> None:
    for key in ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged"):
        if report.get(key) is not True:
            raise ValueError("Unity summary has missing or unsuccessful evidence: " + key)
    if report.get("sourceIdentity") != source or report.get("sourceIdentityAfter") != source:
        raise ValueError("Unity summary belongs to a different source identity.")
    if not report.get("runGuid") or not report.get("endedAt"):
        raise ValueError("Unity summary lacks run identity or completion time.")


def tool_inventory() -> dict:
    from upilot_mcp.mcp_stdio_server import mcp
    from upilot_mcp.tool_facade import McpToolFacade
    from upilot_mcp.tool_registry import REGISTRY, REGISTRY_VERSION, proxy_argument_schema

    exposed = {tool.name: tool for tool in asyncio.run(mcp.list_tools())}
    descriptors = REGISTRY.list()
    unregistered = set(exposed) - {item.name for item in descriptors}
    if unregistered:
        raise ValueError("MCP tools missing registry metadata: " + ", ".join(sorted(unregistered)))
    entries = []
    for item in descriptors:
        method = getattr(McpToolFacade, item.facade_method, None)
        if not callable(method):
            raise ValueError("Registry tool missing proxy handler: " + item.name)
        tool = exposed.get(item.name)
        if item.feature == "core" and tool is None:
            raise ValueError("Core registry tool missing MCP schema: " + item.name)
        entries.append({
            **item.to_dict(), "exposedInCurrentConfiguration": tool is not None,
            "proxyHandlerAvailable": callable(method),
            "proxyArguments": proxy_argument_schema(method) if callable(method) else None,
            "inputSchema": tool.inputSchema if tool else None,
        })
    return {"registryVersion": REGISTRY_VERSION, "registeredCount": len(entries),
            "exposedCount": len(exposed),
            "proxyHandlerGaps": [item["name"] for item in entries if not item["proxyHandlerAvailable"]],
            "tools": entries}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=REPO / "artifacts" / "reliability-quality")
    parser.add_argument("--unity-summary", type=Path)
    parser.add_argument("--require-unity-summary", action="store_true")
    parser.add_argument("--release-tag", default="")
    args = parser.parse_args(argv)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    started = int(time.time() * 1000)
    before = source_identity(REPO)
    steps = []
    env = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1"}
    commands = [
        ("contracts", [sys.executable, "-m", "pytest", "-q", *["tests/" + name for name in CONTRACT_TESTS],
                       "--junitxml=" + str(output / "contracts.xml")], REPO / "upilotserver~"),
        ("skill", [sys.executable, str(REPO / "skills/upilot-unity-mcp/scripts/check_skill_pack.py")], REPO),
    ]
    for name, command, cwd in commands:
        log_path = output / (name + ".log")
        try:
            with log_path.open("w", encoding="utf-8") as log:
                result = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=300)
            exit_code = result.returncode
        except (OSError, subprocess.TimeoutExpired) as exc:
            exit_code = -1
            with log_path.open("a", encoding="utf-8") as log:
                log.write(str(exc))
        content = log_path.read_bytes()
        steps.append({"name": name, "exitCode": exit_code, "log": str(log_path),
                      "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()})
    inventory_path = output / "tools.json"
    try:
        inventory = tool_inventory()
        inventory_path.write_text(json.dumps(inventory, indent=2), encoding="utf-8")
        inventory_exit_code = 0
    except Exception as exc:
        inventory_path.write_text(json.dumps({"error": str(exc)}, indent=2), encoding="utf-8")
        inventory_exit_code = 1
    content = inventory_path.read_bytes()
    steps.append({"name": "toolInventory", "exitCode": inventory_exit_code, "log": str(inventory_path),
                  "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()})
    after = source_identity(REPO)
    unity = {"checked": False, "skipReason": "No Unity summary supplied; this gate is not Unity acceptance."}
    if args.require_unity_summary:
        try:
            unity = verify_release_evidence(REPO, os.environ.get("UPILOT_ACCEPTANCE_RUN_ID", ""), args.release_tag)
        except (OSError, ValueError, subprocess.SubprocessError) as exc:
            unity = {"checked": True, "passed": False, "error": str(exc)}
    elif args.unity_summary:
        try:
            content = args.unity_summary.read_bytes()
            verify_unity_summary(json.loads(content), before)
            unity = {"checked": True, "passed": True, "path": str(args.unity_summary),
                     "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)}
        except (OSError, ValueError) as exc:
            unity = {"checked": True, "passed": False, "error": str(exc)}
    report = {
        "schemaVersion": 1, "sourceIdentity": before, "sourceIdentityAfter": after,
        "sourceUnchanged": before == after, "startedAt": started, "endedAt": int(time.time() * 1000),
        "testScope": list(CONTRACT_TESTS), "steps": steps, "unity": unity,
        "dependencies": {name: importlib.metadata.version(name) for name in ("mcp", "websockets", "pytest", "PyYAML", "pillow")},
        "passed": before == after and all(step["exitCode"] == 0 for step in steps) and unity.get("passed", True),
    }
    content = json.dumps(report, indent=2).encode()
    path = output / "quality.json"
    temporary = path.with_suffix(".json.tmp")
    temporary.write_bytes(content)
    os.replace(temporary, path)
    path.with_suffix(".json.sha256").write_text(hashlib.sha256(content).hexdigest() + "\n", encoding="ascii")
    print(json.dumps({"passed": report["passed"], "path": str(path), "sourceIdentity": before, "steps": steps}))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
