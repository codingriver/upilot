from __future__ import annotations

import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from upilot_mcp.domain.status_service import StatusDomainService


def _identity(project: Path, pid: int = 42, created_at: int = 123) -> dict:
    return {
        "resolvedProcessId": pid,
        "processCreatedAt": created_at,
        "projectPath": str(project),
        "projectIdentityVerified": True,
        "processRole": "editor",
    }


def _service(project: Path) -> StatusDomainService:
    service = StatusDomainService.__new__(StatusDomainService)
    service.server = SimpleNamespace(
        session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(project))),
        state=SimpleNamespace(project_path=str(project)),
    )
    return service


def test_window_selection_is_stable_across_global_window_order_and_keeps_64_bit_hwnd():
    target = {
        "hwnd": 0x1_0000_1234,
        "title": "UPilotTest - Windows - Unity 6000.6",
        "className": "UnityContainerWndClass",
        "visible": True,
    }
    floating = {
        "hwnd": 9,
        "title": "Inspector",
        "className": "UnityContainerWndClass",
        "visible": True,
    }

    selected_a, basis_a = StatusDomainService._select_unity_window([floating, target])
    selected_b, basis_b = StatusDomainService._select_unity_window([target, floating])

    assert selected_a["hwnd"] == selected_b["hwnd"] == 0x1_0000_1234
    assert basis_a == basis_b == "verified_pid_unique_main_title"


@pytest.mark.parametrize(
    "windows,reason",
    [
        ([], "window_not_found_for_verified_pid"),
        ([
            {"hwnd": 1, "title": "A - Unity 6", "className": "UnityWndClass", "visible": True},
            {"hwnd": 2, "title": "B - Unity 6", "className": "UnityWndClass", "visible": True},
        ], "ambiguous_main_windows"),
    ],
)
def test_window_selection_refuses_missing_or_ambiguous_targets(windows, reason):
    selected, observed_reason = StatusDomainService._select_unity_window(windows)
    assert selected is None
    assert observed_reason == reason


def test_verified_window_enumeration_receives_only_the_project_pid(monkeypatch, tmp_path):
    service = _service(tmp_path)
    calls = []
    service._get_verified_unity_process_identity = lambda _force=False: (42, _identity(tmp_path))
    service._enumerate_windows_for_pid = lambda pid: calls.append(pid) or [{
        "hwnd": 123,
        "title": "UPilotTest - Unity 6",
        "className": "UnityContainerWndClass",
        "visible": True,
    }]
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = service._resolve_verified_unity_window()

    assert result["ok"] is True
    assert result["targetPid"] == 42
    assert calls == [42]


def test_cached_identity_is_reused_only_while_process_creation_matches(tmp_path):
    service = _service(tmp_path)
    service._verified_unity_process_cache = _identity(tmp_path)
    service._process_creation_time = lambda pid: 123
    resolver_calls = []
    service._resolve_live_unity_pid = lambda: resolver_calls.append(True) or (99, _identity(tmp_path, 99, 456))

    pid, cached = service._get_verified_unity_process_identity()
    assert pid == 42 and cached["identityCacheHit"] is True
    assert resolver_calls == []

    service._process_creation_time = lambda pid: 456 if pid == 99 else 999
    pid, refreshed = service._get_verified_unity_process_identity()
    assert pid == 99 and refreshed["identityCacheHit"] is False
    assert resolver_calls == [True]


def test_wake_revalidates_pid_and_checks_post_message_result(monkeypatch, tmp_path):
    service = _service(tmp_path)
    target = {"ok": True, "hwnd": 123, "targetPid": 42, "processCreatedAt": 123}
    service._resolve_verified_unity_window = lambda: target
    service._process_creation_time = lambda pid: 123
    service._window_process_id = lambda hwnd: 42
    post_calls = []
    service._post_window_message = lambda hwnd: post_calls.append(hwnd) or False
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    assert service._wake_unity_editor() is False
    assert post_calls == [123]

    service._process_creation_time = lambda pid: 999
    assert service._wake_unity_editor() is False
    assert post_calls == [123]


def test_focus_failure_is_not_reported_as_focused(monkeypatch, tmp_path):
    service = _service(tmp_path)
    target = {
        "ok": True,
        "hwnd": 123,
        "targetPid": 42,
        "processCreatedAt": 123,
        "projectIdentityVerified": True,
    }
    service._resolve_verified_unity_window = lambda: target
    service._revalidate_window_target = lambda value: (True, "")
    service._show_and_focus_window = lambda hwnd: False
    service._foreground_window = lambda: 456
    service._window_process_id = lambda hwnd: 7
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = asyncio.run(service.editor_focus())

    assert result.ok is False
    assert result.error.code == "FOCUS_NOT_ACQUIRED"
    assert result.error.detail["focused"] is False
    assert result.error.detail["requestIssued"] is True
    assert result.error.detail["setForegroundResult"] is False
    assert result.error.detail["foregroundVerified"] is False
    assert result.error.detail["foregroundPid"] == 7
    assert result.error.detail["targetPid"] == 42


def test_focus_reports_win32_result_separately_from_observed_foreground(monkeypatch, tmp_path):
    service = _service(tmp_path)
    target = {
        "ok": True,
        "hwnd": 123,
        "targetPid": 42,
        "processCreatedAt": 123,
        "projectIdentityVerified": True,
    }
    service._resolve_verified_unity_window = lambda: target
    service._revalidate_window_target = lambda value: (True, "")
    service._show_and_focus_window = lambda hwnd: True
    service._foreground_window = lambda: 456
    service._window_process_id = lambda hwnd: 7
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = asyncio.run(service.editor_focus())

    assert result.ok is False
    assert result.error.code == "FOCUS_NOT_ACQUIRED"
    assert result.error.detail["requestIssued"] is True
    assert result.error.detail["setForegroundResult"] is True
    assert result.error.detail["foregroundVerified"] is False


def test_focus_success_requires_observed_foreground_pid(monkeypatch, tmp_path):
    service = _service(tmp_path)
    target = {
        "ok": True,
        "hwnd": 123,
        "targetPid": 42,
        "processCreatedAt": 123,
        "projectIdentityVerified": True,
    }
    service._resolve_verified_unity_window = lambda: target
    service._revalidate_window_target = lambda value: (True, "")
    service._show_and_focus_window = lambda hwnd: True
    service._foreground_window = lambda: 456
    service._window_process_id = lambda hwnd: 42
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = asyncio.run(service.editor_focus())

    assert result.ok is True
    assert result.data["requestIssued"] is True
    assert result.data["setForegroundResult"] is True
    assert result.data["foregroundVerified"] is True
    assert result.data["foregroundPid"] == 42


def test_focus_rejects_window_identity_change_before_request(monkeypatch, tmp_path):
    service = _service(tmp_path)
    service._resolve_verified_unity_window = lambda: {
        "ok": True,
        "hwnd": 123,
        "targetPid": 42,
        "processCreatedAt": 123,
    }
    service._revalidate_window_target = lambda value: (False, "window_pid_changed")
    calls = []
    service._show_and_focus_window = lambda hwnd: calls.append(hwnd) or True
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = asyncio.run(service.editor_focus())

    assert result.ok is False
    assert result.error.code == "WINDOW_IDENTITY_CHANGED"
    assert result.error.detail["reason"] == "window_pid_changed"
    assert calls == []


def test_focus_state_uses_foreground_pid_so_floating_project_windows_count(monkeypatch, tmp_path):
    service = _service(tmp_path)
    service._resolve_verified_unity_window = lambda: {
        "ok": True,
        "hwnd": 123,
        "title": "UPilotTest - Unity 6",
        "className": "UnityContainerWndClass",
        "targetPid": 42,
        "processCreatedAt": 123,
        "projectIdentityVerified": True,
    }
    service._revalidate_window_target = lambda value: (True, "")
    service._foreground_window = lambda: 999
    service._window_process_id = lambda hwnd: 42
    service._window_text = lambda hwnd: "Inspector"
    monkeypatch.setattr("upilot_mcp.domain.status_service.sys.platform", "win32")

    result = asyncio.run(service.editor_focus_state())

    assert result.ok is True
    assert result.data["unityFocused"] is True
    assert result.data["foregroundHwnd"] == 999
    assert result.data["targetPid"] == 42


def _write_startup(
    project: Path,
    *,
    pid: int = 42,
    created_at: int = 123,
    milestone_names: list[str] | None = None,
):
    path = project / "Library" / "UPilot" / "startup.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({
        "projectPath": str(project),
        "processId": pid,
        "processCreatedAt": created_at,
        "observedAtUtcMs": 1000,
        "milestones": [
            {"name": name}
            for name in (milestone_names or ["bootstrap_entered", "editor_ready"])
        ],
        "blockingReasons": [],
        "serverStartCycleId": "domain-test",
        "serverStartRetryStatus": "observing",
        "serverStartAttemptCount": 2,
        "lastServerStartAttemptAtUtcMs": 900,
        "nextServerStartAttemptAtUtcMs": 2900,
        "lastServerStartReason": "server_update_blocked",
        "backgroundExecution": {"status": "not_observed"},
    }), encoding="utf-8")
    return path


def test_startup_summary_does_not_promote_historical_ready(tmp_path):
    service = _service(tmp_path)
    _write_startup(tmp_path)
    service._process_exists = lambda pid: True

    historical = service._read_startup_summary(tmp_path, _identity(tmp_path, 99, 456))

    assert historical["recordStatus"] == "historical"
    assert historical["current"] is False
    assert historical["historicalEditorReady"] is True
    assert "server_healthy" in historical["missingStages"]


def test_startup_summary_distinguishes_current_exited_and_corrupt(tmp_path):
    service = _service(tmp_path)
    path = _write_startup(tmp_path)
    service._process_exists = lambda pid: False

    current = service._read_startup_summary(tmp_path, _identity(tmp_path))
    assert current["recordStatus"] == "current" and current["current"] is True
    assert current["serverStartRetry"] == {
        "cycleId": "domain-test",
        "status": "observing",
        "attemptCount": 2,
        "lastAttemptAtUtcMs": 900,
        "nextAttemptAtUtcMs": 2900,
        "lastReason": "server_update_blocked",
    }

    exited = service._read_startup_summary(tmp_path, {})
    assert exited["recordStatus"] == "process_exited" and exited["current"] is False

    path.write_text("{broken", encoding="utf-8")
    corrupt = service._read_startup_summary(tmp_path, {})
    assert corrupt["recordStatus"] == "corrupt" and corrupt["current"] is False


@pytest.mark.parametrize(
    "milestones,missing",
    [
        (["bootstrap_entered", "first_editor_update", "server_healthy"], ["bridge_authenticated", "editor_ready"]),
        (["bootstrap_entered", "first_editor_update", "server_healthy", "bridge_authenticated"], ["editor_ready"]),
        (["bootstrap_entered", "first_editor_update", "bridge_authenticated"], ["server_healthy", "editor_ready"]),
    ],
)
def test_startup_summary_keeps_health_handshake_and_ready_as_independent_evidence(
    tmp_path,
    milestones,
    missing,
):
    service = _service(tmp_path)
    _write_startup(tmp_path, milestone_names=milestones)
    service._process_exists = lambda pid: True

    summary = service._read_startup_summary(tmp_path, _identity(tmp_path))

    assert summary["current"] is True
    assert summary["missingStages"] == missing
    assert summary["historicalEditorReady"] is False
