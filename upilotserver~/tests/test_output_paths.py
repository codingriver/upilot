from __future__ import annotations

import asyncio
import json
import re
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp import output_paths as paths
from upilot_mcp.daily_logs import DailyLogWriter
from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.domain.screenshot_service import ScreenshotDomainService
from upilot_mcp.domain.status_service import StatusDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.editor_e2e.runner import run_editor_e2e_from_path
from upilot_mcp.responses import fail, ok


def test_csharp_and_python_default_output_contracts_match():
    # Compare the real definitions across both runtimes rather than duplicate a path table.
    source = Path(__file__).resolve().parents[2] / "Editor" / "Core" / "UPilotOutputPaths.cs"
    values = {}
    for name, expression in re.findall(r"public const string (\w+) = ([^;]+);", source.read_text(encoding="utf-8")):
        values[name] = "".join(
            json.loads(part) if part.startswith('"') else values[part]
            for part in (token.strip() for token in expression.split("+"))
        )
    aliases = {
        "Root": "ROOT", "UnityLog": "UNITY_LOG", "StepLog": "STEP_LOG",
        "ServerLog": "SERVER_LOG", "McpCalls": "MCP_CALLS", "StepReports": "STEP_REPORTS",
        "AcceptanceReports": "ACCEPTANCE_REPORTS", "FlowReports": "FLOW_REPORTS",
        "FlowDefaultReports": "FLOW_DEFAULT_REPORTS", "FlowTestRunnerReports": "FLOW_TEST_RUNNER_REPORTS",
        "FlowHeadedReports": "FLOW_HEADED_REPORTS", "FlowHistory": "FLOW_HISTORY",
        "E2EReports": "E2E_REPORTS", "ConsoleCaptures": "CONSOLE_CAPTURES",
        "QuickDebugChecks": "QUICK_DEBUG_CHECKS", "QuickDebugObjectDump": "QUICK_DEBUG_OBJECT_DUMP",
        "Profiler": "PROFILER", "Snapshots": "SNAPSHOTS", "SnapshotComparisons": "SNAPSHOT_COMPARISONS",
        "Screenshots": "SCREENSHOTS", "Dumps": "DUMPS",
    }
    assert set(values) == set(aliases)
    for csharp_name, python_name in aliases.items():
        assert getattr(paths, python_name).as_posix() == values[csharp_name]


def test_paths_and_retention_ignore_legacy_files_and_working_directory(tmp_path, monkeypatch):
    project, cwd = tmp_path / "project", tmp_path / "cwd"
    project.mkdir()
    cwd.mkdir()
    old = project / "log" / "mcp-server-2000-01-01.log"
    old.parent.mkdir()
    old.write_bytes(b"old server log")
    fixed = project / paths.SERVER_LOG
    fixed.parent.mkdir(parents=True)
    fixed.write_bytes(b"old fixed file")
    monkeypatch.chdir(cwd)
    writer = DailyLogWriter(paths.output_path(project, paths.SERVER_LOG))
    assert writer.append("new server log\n")
    assert writer.path_for(writer.clock().date()).read_text() == "new server log\n"
    assert old.read_bytes() == b"old server log"
    assert fixed.read_bytes() == b"old fixed file"
    assert list(cwd.iterdir()) == []
    with pytest.raises(ValueError):
        paths.output_path("relative-project", paths.SCREENSHOTS)


def test_screenshot_default_and_explicit_output_remain_distinct(tmp_path, monkeypatch):
    service = ScreenshotDomainService()
    service._active_project_root = lambda: tmp_path
    cwd = tmp_path / "unrelated-cwd"
    cwd.mkdir()
    monkeypatch.chdir(cwd)
    default = service._resolve_screenshot_save_path("", "game-view", False)
    assert isinstance(default, Path)
    assert default.parent == tmp_path / paths.SCREENSHOTS
    assert default.suffix == ".png"
    custom = service._resolve_screenshot_save_path("Log/user-choice.png", "game-view", False)
    assert custom == tmp_path / "Log" / "user-choice.png"
    assert list(cwd.iterdir()) == []


@pytest.mark.parametrize("active", [False, True])
def test_console_discovery_never_reads_old_directory(tmp_path, active):
    session_id = "legacy-capture"
    old = tmp_path / "Log" / "UPilotConsole" / "capture"
    old.mkdir(parents=True)
    manifest = {"sessionId": session_id, "active": active, "finishedAtUtcMs": 123, "sha256": "hash"}
    content = json.dumps(manifest).encode()
    (old / "session.json").write_bytes(content)
    (old / "summary.json").write_text("{}")
    service = StatusDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(tmp_path))))
    failed = fail("req", "OFFLINE", "not dispatched")
    assert service._active_console_capture_is_explicitly_known(session_id) is False
    assert service._recover_completed_console_capture(session_id, "", failed) is None
    assert (old / "session.json").read_bytes() == content

    current = tmp_path / paths.CONSOLE_CAPTURES / "capture"
    current.mkdir(parents=True)
    (current / "session.json").write_bytes(content)
    (current / "summary.json").write_text("{}")
    assert service._active_console_capture_is_explicitly_known(session_id) is active
    recovered = service._recover_completed_console_capture(session_id, "", failed)
    assert (recovered is not None) is (not active)
    assert (old / "session.json").read_bytes() == content


def _spec(directory):
    directory.mkdir(parents=True)
    spec = directory / "spec.yaml"
    spec.write_text("name: output-path-test\nsteps:\n  - action: wait\n    unity: false\n    ms: 0\n", encoding="utf-8")
    return spec


def test_e2e_default_uses_project_not_spec_or_cwd_and_preserves_legacy(tmp_path, monkeypatch):
    project = tmp_path / "unity-project"
    project.mkdir()
    spec = _spec(tmp_path / "external-spec")
    old = spec.parent / ".upilot-e2e-artifacts" / "old"
    old.mkdir(parents=True)
    (old / "report.json").write_bytes(b"old report")
    monkeypatch.chdir(spec.parent)
    facade = SimpleNamespace(server=SimpleNamespace(expected_project_path=project))
    result = asyncio.run(run_editor_e2e_from_path(facade, spec))
    assert result.ok and result.data["passed"] is True
    directory = Path(result.data["artifactDir"])
    assert directory.parent == project / paths.E2E_REPORTS
    assert Path(result.data["reportPath"]) == directory / "report.json"
    assert (directory / "manifest.json").is_file()
    report = json.loads((directory / "report.json").read_text())
    assert report["artifactDir"] == str(directory)
    assert report["reportPath"] == str(directory / "report.json")
    assert (old / "report.json").read_bytes() == b"old report"
    assert list((spec.parent / ".upilot-e2e-artifacts").iterdir()) == [old]


def test_e2e_explicit_artifact_directory_stays_exact_without_project(tmp_path):
    spec = _spec(tmp_path / "spec")
    custom = tmp_path / "user-selected"
    result = asyncio.run(run_editor_e2e_from_path(SimpleNamespace(), spec, artifact_dir=custom))
    assert result.ok
    assert Path(result.data["artifactDir"]) == custom
    assert sorted(p.name for p in custom.iterdir()) == ["manifest.json", "report.json"]


@pytest.mark.parametrize("root", [None, "", "relative-project"])
def test_e2e_missing_project_never_creates_default_output_or_runs_actions(tmp_path, monkeypatch, root):
    spec = _spec(tmp_path / "spec")
    calls = []
    async def unexpected(*args):
        calls.append(args)
        raise AssertionError("must reject missing project before an action")
    monkeypatch.setattr("upilot_mcp.editor_e2e.runner.run_action", unexpected)
    facade = SimpleNamespace(server=SimpleNamespace(expected_project_path=root))
    result = asyncio.run(run_editor_e2e_from_path(facade, spec))
    assert not result.ok and result.error.code == "PROJECT_PATH_UNAVAILABLE"
    assert calls == []
    assert sorted(p.name for p in spec.parent.iterdir()) == ["spec.yaml"]


def test_flow_defaults_change_but_explicit_legacy_string_is_not_rewritten():
    calls = []
    class Dispatcher:
        async def call(self, request_id, name, payload, **kwargs):
            calls.append((name, payload))
            return ok(request_id, payload)
    service = TestDomainService()
    service.dispatcher = Dispatcher()
    default = asyncio.run(service.upilot_flow_run(yaml_paths=["sample.yaml"]))
    explicit = asyncio.run(service.upilot_flow_run(yaml_paths=["sample.yaml"], report_path="Reports/UPilot/Flow"))
    assert default.ok and explicit.ok
    assert calls[0][1]["reportPath"] == paths.FLOW_REPORTS.as_posix()
    assert calls[1][1]["reportPath"] == "Reports/UPilot/Flow"
    assert len(calls) == 2


def test_default_dump_preflight_targets_unified_directory_without_writing_dump(tmp_path):
    service = ProjectAnalysisDomainService()
    service._windows_dump_supported = lambda: True
    service._analysis_project_root = lambda: tmp_path
    service._resolve_live_unity_pid = lambda: (123, {"processCreatedAt": 456, "projectIdentityVerified": True})
    # Stop at the existing preflight boundary: this test never attempts a native dump.
    service._process_memory_usage = lambda *args: {"ok": False, "error": "test preflight"}
    result = asyncio.run(service._hang_capture_execute(output_path="", dump_type="mini", reserve_bytes=0))
    assert not result.ok and result.error.code == "HANG_DUMP_PREFLIGHT_FAILED"
    target = Path(result.error.detail["path"])
    assert target.parent == tmp_path / paths.DUMPS
    assert target.name.startswith("unity-123-") and target.suffix == ".dmp"
    assert not (tmp_path / "Log").exists()
    assert not target.exists()
