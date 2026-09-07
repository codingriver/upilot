from __future__ import annotations

import asyncio

from .auto_fix_loop import AutoFixLoopService
from .dispatcher import CommandDispatcher
from .domain.build_service import BuildDomainService
from .domain.analysis_service import ProjectAnalysisDomainService
from .domain.compile_service import CompileDomainService
from .domain.execution_service import ExecutionDomainService
from .domain.reflection_service import ReflectionDomainService
from .domain.resource_service import ResourceDomainService
from .domain.screenshot_service import ScreenshotDomainService
from .domain.snapshot_service import SnapshotDomainService
from .domain.status_service import StatusDomainService
from .domain.task_service import TaskDomainService
from .domain.test_service import TestDomainService
from .fix_planner import CompileFixPlanner
from .patch_service import PatchApplyService
from .server import WsOrchestratorServer


class McpToolFacade(
    StatusDomainService,
    ProjectAnalysisDomainService,
    CompileDomainService,
    ExecutionDomainService,
    ResourceDomainService,
    ReflectionDomainService,
    TaskDomainService,
    ScreenshotDomainService,
    SnapshotDomainService,
    TestDomainService,
    BuildDomainService,
):
    """Composition root for the MCP domain services."""

    def __init__(self, server: WsOrchestratorServer) -> None:
        self.server = server
        workspace_root = "."
        session = server.session_manager.active
        if session and session.project_path:
            workspace_root = session.project_path

        self.dispatcher = CommandDispatcher(transport=server, state=server.state)
        self.patch_service = PatchApplyService(workspace_root=workspace_root)
        self.fix_planner = CompileFixPlanner(workspace_root=workspace_root)
        self.auto_fix_loop = AutoFixLoopService(
            dispatcher=self.dispatcher,
            state=server.state,
            patch_service=self.patch_service,
            planner=self.fix_planner,
            post_patch_sync=self._post_patch_sync_after_write,
        )
        self._async_tasks: dict[str, dict] = {}
        self._async_task_handles: dict[str, asyncio.Task] = {}
        self._operations: dict[str, dict] = {}
        self._operation_failure_history: dict[str, int] = {}
        self._write_batch_resume_task: asyncio.Task | None = None
        server.on_editor_execution_state = self._on_execution_state_with_test_recovery

    async def _on_execution_state_with_test_recovery(self, execution: dict) -> None:
        await self._on_editor_execution_state(execution)
        if execution.get("authoritative") and not execution.get("isStale"):
            self._recover_test_jobs()
