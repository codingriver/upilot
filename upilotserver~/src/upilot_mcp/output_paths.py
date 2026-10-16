"""Default visible outputs owned by one Unity project; no legacy discovery."""
from __future__ import annotations

from pathlib import Path

ROOT = Path("Logs") / "UPilot"
UNITY_LOG = ROOT / "upilot.log"
STEP_LOG = ROOT / "step.log"
SERVER_LOG = ROOT / "mcp-server.log"
MCP_CALLS = ROOT / "McpCalls"
STEP_REPORTS = ROOT / "Reports" / "Steps"
ACCEPTANCE_REPORTS = ROOT / "Reports" / "Acceptance"
FLOW_REPORTS = ROOT / "Reports" / "Flow"
FLOW_DEFAULT_REPORTS = FLOW_REPORTS / "Default"
FLOW_TEST_RUNNER_REPORTS = FLOW_REPORTS / "TestRunner"
FLOW_HEADED_REPORTS = FLOW_REPORTS / "HeadedAll"
FLOW_HISTORY = FLOW_REPORTS / "History"
E2E_REPORTS = ROOT / "Reports" / "E2E"
CONSOLE_CAPTURES = ROOT / "Console"
QUICK_DEBUG_CHECKS = ROOT / "Diagnostics" / "QuickDebug" / "ToolChecks"
QUICK_DEBUG_OBJECT_DUMP = ROOT / "Diagnostics" / "QuickDebug" / "csharp_object_dump.log"
PROFILER = ROOT / "Diagnostics" / "Profiler"
SNAPSHOTS = ROOT / "Diagnostics" / "Snapshots"
SNAPSHOT_COMPARISONS = ROOT / "Diagnostics" / "SnapshotComparisons"
SCREENSHOTS = ROOT / "Diagnostics" / "Screenshots"
DUMPS = ROOT / "Diagnostics" / "Dumps"


def output_path(project_root: str | Path, relative_path: str | Path) -> Path:
    root = Path(project_root).expanduser()
    if not root.is_absolute():
        raise ValueError("UPilot output requires an absolute Unity project path")
    return root.resolve() / relative_path
