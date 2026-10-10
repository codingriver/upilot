from __future__ import annotations

import asyncio
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.responses import ok
from upilot_mcp.state_store import StateStore


def _service(project: Path) -> ProjectAnalysisDomainService:
    state = StateStore()
    state.configure_project(str(project))
    service = ProjectAnalysisDomainService()
    service.server = SimpleNamespace(state=state)
    service._analysis_project_root = lambda: project
    return service


def test_capture_returns_bounded_running_state_and_status_is_durable(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)
        release = asyncio.Event()

        async def execute(**_kwargs):
            await release.wait()
            return ok("capture", {"path": str(tmp_path / "capture.dmp"), "bytes": 12, "sha256": "abc"})

        service._hang_capture_execute = execute
        started = await service.hang_capture(wait_timeout_sec=0)
        assert started.ok and not started.data["terminal"]
        capture_id = started.data["captureId"]

        running = await service.hang_capture_status(capture_id)
        assert running.ok and running.data["status"] == "running"
        release.set()
        await service._hang_capture_tasks[capture_id]

        completed = await service.hang_capture_status(capture_id)
        assert completed.ok and completed.data["terminal"] is True
        assert completed.data["status"] == "completed"
        assert completed.data["path"].endswith("capture.dmp")

    asyncio.run(scenario())


def test_duplicate_capture_is_busy_and_returns_existing_identity(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)
        release = asyncio.Event()

        async def execute(**_kwargs):
            await release.wait()
            return ok("capture", {"path": "capture.dmp"})

        service._hang_capture_execute = execute
        first = await service.hang_capture(wait_timeout_sec=0)
        second = await service.hang_capture(wait_timeout_sec=0)
        assert not second.ok and second.error.code == "HANG_CAPTURE_BUSY"
        assert second.error.detail["captureId"] == first.data["captureId"]
        release.set()
        await service._hang_capture_tasks[first.data["captureId"]]

    asyncio.run(scenario())


def test_waiter_cancellation_does_not_cancel_native_capture_task(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)
        entered = asyncio.Event()
        release = asyncio.Event()

        async def execute(**_kwargs):
            entered.set()
            await release.wait()
            return ok("capture", {"path": "capture.dmp"})

        service._hang_capture_execute = execute
        waiter = asyncio.create_task(service.hang_capture(wait_timeout_sec=30))
        await entered.wait()
        waiter.cancel()
        try:
            await waiter
        except asyncio.CancelledError:
            pass
        capture_id = next(iter(service._hang_capture_tasks))
        assert not service._hang_capture_tasks[capture_id].cancelled()
        release.set()
        await service._hang_capture_tasks[capture_id]
        status = await service.hang_capture_status(capture_id)
        assert status.data["status"] == "completed"

    asyncio.run(scenario())


def test_restart_marks_unfinished_capture_interrupted(tmp_path: Path) -> None:
    first = StateStore()
    first.configure_project(str(tmp_path))
    partial = tmp_path / "capture.dmp"
    partial.write_bytes(b"partial-dump")
    state = {
        "captureId": "hang-active",
        "projectPath": str(tmp_path.resolve()),
        "status": "running",
        "terminal": False,
        "success": False,
        "startedAt": 1,
        "endedAt": 0,
        "outputPath": str(partial),
        "result": {"path": str(partial), "dumpAttempted": True, "processTerminated": False},
        "error": None,
    }
    assert first.create_hang_capture(state)[0] == "created"

    restarted = StateStore()
    restarted.configure_project(str(tmp_path))
    recovered = restarted.get_hang_capture("hang-active")
    assert recovered["status"] == "interrupted"
    assert recovered["terminal"] is True
    assert recovered["error"]["code"] == "HANG_CAPTURE_INTERRUPTED"
    assert recovered["outputPath"] == str(partial)
    assert recovered["result"]["dumpAttempted"] is True
    assert recovered["partialArtifact"] == {
        "path": str(partial),
        "exists": True,
        "bytes": len(b"partial-dump"),
        "sha256": "",
        "hashVerified": False,
    }


def test_capture_persists_target_and_dump_intent_before_native_write(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)
        target = tmp_path / "capture.dmp"
        progress = []

        service._windows_dump_supported = lambda: True
        service._resolve_live_unity_pid = lambda: (123, {
            "processCreatedAt": 456,
            "projectIdentityVerified": True,
        })
        service._process_memory_usage = lambda *_args: {
            "ok": True,
            "workingSetBytes": 64 * 1024 * 1024,
            "privateBytes": 64 * 1024 * 1024,
        }
        service._volume_space = lambda _path: {
            "volumeTotalBytes": 16 * 1024 * 1024 * 1024,
            "volumeUsedBytes": 0,
            "freeBytes": 16 * 1024 * 1024 * 1024,
        }
        service._write_windows_minidump = lambda *_args: (False, 5)

        result = await service._hang_capture_execute(
            output_path=str(target),
            dump_type="mini",
            reserve_bytes=2 * 1024 * 1024 * 1024,
            progress_callback=lambda item: progress.append(dict(item)),
        )

        assert not result.ok and result.error.code == "HANG_DUMP_FAILED"
        assert [item["dumpAttempted"] for item in progress] == [False, True]
        assert all(item["path"] == str(target) for item in progress)

    asyncio.run(scenario())


def test_initial_persistence_failure_never_starts_dump(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)
        execute_calls = []
        service.server.state.create_hang_capture = lambda _state: (_ for _ in ()).throw(
            RuntimeError("database unavailable")
        )

        async def execute(**kwargs):
            execute_calls.append(kwargs)
            return ok("capture", {})

        service._hang_capture_execute = execute
        result = await service.hang_capture(wait_timeout_sec=0)

        assert not result.ok and result.error.code == "HANG_CAPTURE_PERSIST_FAILED"
        assert result.error.detail["dumpAttempted"] is False
        assert execute_calls == []
        assert not hasattr(service, "_hang_capture_tasks")

    asyncio.run(scenario())


def test_terminal_persistence_failure_does_not_claim_capture_success(tmp_path: Path) -> None:
    async def scenario() -> None:
        service = _service(tmp_path)

        async def execute(**_kwargs):
            return ok("capture", {"path": "capture.dmp", "bytes": 12, "sha256": "abc"})

        service._hang_capture_execute = execute
        service.server.state.save_hang_capture = lambda _state: (_ for _ in ()).throw(
            RuntimeError("database unavailable")
        )

        result = await service.hang_capture(wait_timeout_sec=30)

        assert not result.ok and result.error.code == "HANG_CAPTURE_PERSIST_FAILED"
        assert result.error.detail["captureId"].startswith("hang-")
        assert result.error.detail["status"] == "persistence_failed"
        assert result.error.detail["terminal"] is False
        assert result.error.detail["path"] == "capture.dmp"
        durable = service.server.state.get_hang_capture(result.error.detail["captureId"])
        assert durable["status"] == "running" and durable["terminal"] is False

    asyncio.run(scenario())
