import asyncio
from upilot_mcp.domain.snapshot_service import SnapshotDomainService
from upilot_mcp.responses import ok


def test_scene_capture_returns_one_pollable_task_without_replaying_start():
    async def run():
        service = SnapshotDomainService()
        calls = []
        async def start(*args, **kwargs):
            calls.append((args, kwargs))
            return ok("req", {"taskId": "capture-task"})
        async def status(*args, **kwargs):
            return ok("req", {"taskId": "capture-task", "terminal": False})
        service.task_start, service.task_status = start, status
        result = await service.snapshot_capture([{"kind": "sceneView", "instanceId": "42"}], wait_ms=5)
        assert result.ok and result.data["terminal"] is False
        assert result.data["taskId"] == "capture-task"
        assert result.data["repaintPending"]
        assert len(calls) == 1
        assert calls[0][1]["retry_count"] == 0
    asyncio.run(run())


def test_failed_snapshot_task_preserves_failure_and_manifest_without_retry():
    from upilot_mcp.domain.task_service import TaskDomainService
    async def run():
        service = TaskDomainService()
        calls = []
        async def dispatch(*args):
            calls.append(args)
            return ok("req", {"snapshotId": "snap-failed", "terminal": True, "success": False,
                              "status": "failed", "manifestPath": "Log/capture/manifest.json",
                              "failures": [{"code": "FRAME_ADVANCED_DURING_CAPTURE", "message": "frame changed"}]})
        service._dispatch_tool = dispatch
        result = await service.task_execute("capture", "unity_snapshot_capture", retry_count=0)
        assert not result.ok
        assert "FRAME_ADVANCED_DURING_CAPTURE" in result.error.message
        assert result.error.detail["snapshot"]["snapshotId"] == "snap-failed"
        assert result.error.detail["snapshot"]["manifestPath"] == "Log/capture/manifest.json"
        assert len(calls) == 1
    asyncio.run(run())


def test_completed_task_unwraps_real_snapshot_result():
    async def run():
        service = SnapshotDomainService()
        async def start(*args, **kwargs):
            return ok("req", {"taskId": "capture-task"})
        async def status(*args, **kwargs):
            return ok("req", {"terminal": True, "status": "completed",
                             "result": {"result": {"snapshotId": "snap", "success": True, "terminal": True}}})
        service.task_start, service.task_status = start, status
        result = await service.snapshot_capture([{"kind": "sceneView", "instanceId": "42"}], wait_ms=50)
        assert result.data["snapshotId"] == "snap"
        assert result.data["success"]
    asyncio.run(run())
