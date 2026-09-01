from __future__ import annotations

import json
import urllib.error
import urllib.request

from upilot_mcp.compile_driver import run_compile_driver


class _Response:
    def __init__(self, payload: bytes) -> None:
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        return None

    def read(self) -> bytes:
        return self.payload


def _sse(structured: dict) -> bytes:
    envelope = {
        "jsonrpc": "2.0",
        "id": 1,
        "result": {"structuredContent": structured},
    }
    return ("event: message\r\ndata: " + json.dumps(envelope) + "\r\n\r\n").encode()


def _unity_project(tmp_path):
    (tmp_path / "Assets").mkdir()
    (tmp_path / "ProjectSettings").mkdir()
    (tmp_path / "ProjectSettings" / "ProjectVersion.txt").write_text(
        "m_EditorVersion: 6000.0.0f1\n",
        encoding="utf-8",
    )
    (tmp_path / ".upilot").mkdir()
    (tmp_path / ".upilot" / "config.json").write_text(
        json.dumps({"mcp": {"httpHost": "127.0.0.1", "httpPort": 8123}}),
        encoding="utf-8",
    )
    return tmp_path


def test_compile_driver_attaches_existing_editor_and_runs_real_compile_contract(tmp_path, monkeypatch) -> None:
    project = _unity_project(tmp_path)

    def fake_urlopen(request: urllib.request.Request, timeout: float):
        if request.full_url.endswith("/health"):
            return _Response(
                json.dumps(
                    {
                        "status": "ok",
                        "unity_connected": True,
                        "project_path": str(project),
                        "unity_version": "6000.0.0f1",
                        "server_pid": 101,
                    }
                ).encode()
            )
        body = json.loads(request.data.decode())
        tool = body["params"]["name"]
        if tool == "unity_mcp_status":
            return _Response(
                _sse(
                    {
                        "ok": True,
                        "data": {
                            "paths": {"unityProjectAbsolute": str(project)},
                            "session": {"processId": 202},
                        },
                    }
                )
            )
        if tool == "unity_sync_after_disk_write":
            return _Response(_sse({"ok": True, "data": {"status": "compiling"}}))
        assert tool == "unity_safe_compile_and_wait"
        return _Response(
            _sse(
                {
                    "ok": True,
                    "data": {
                        "status": "success",
                        "phase": "completed",
                        "compileRequestId": "req-compile",
                        "errorCount": 0,
                        "warningCount": 0,
                        "reconnectedAfterReload": True,
                        "sessionChangedDuringCompile": True,
                    },
                }
            )
        )

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)
    result = run_compile_driver(str(project), timeout_s=10)

    assert result["ok"] is True
    assert result["httpPort"] == 8123
    assert result["serverPid"] == 101
    assert result["unityPid"] == 202
    assert result["attachedToExistingEditor"] is True
    assert result["startedSecondUnityInstance"] is False
    assert result["compileRequestId"] == "req-compile"
    assert result["errorCount"] == 0
    assert result["reconnectedAfterReload"] is True


def test_compile_driver_reports_unavailable_service_without_starting_second_unity(tmp_path, monkeypatch) -> None:
    project = _unity_project(tmp_path)

    def unavailable(request: urllib.request.Request, timeout: float):
        raise urllib.error.URLError("connection refused")

    monkeypatch.setattr(urllib.request, "urlopen", unavailable)
    result = run_compile_driver(str(project), timeout_s=10)

    assert result["ok"] is False
    assert result["code"] == "MCP_UNAVAILABLE"
    assert result["mcpUnavailable"] is True
    assert result["attachedToExistingEditor"] is False
    assert result["startedSecondUnityInstance"] is False
    assert "Do not launch a second Unity instance" in result["nextAction"]
