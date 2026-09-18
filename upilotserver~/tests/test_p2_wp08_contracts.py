import asyncio
from pathlib import Path
from types import SimpleNamespace

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.source_identity import acceptance_import_inputs, diff_acceptance_import_inputs
from upilot_mcp.test_job_context import TEST_JOB_CONTEXT
from upilot_mcp.mcp_tools import test_tools  # noqa: F401


class _TestWire(TestDomainService):
    def __init__(self, project: Path, responses=None):
        self.server = SimpleNamespace(state=SimpleNamespace(_project_path=str(project)))
        self.responses = list(responses or [])
        self.calls = []
        self.dispatcher = self

    async def call(self, _request_id, name, payload, **_kwargs):
        self.calls.append((name, payload))
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


def _with_job_context(state):
    persisted = []

    def persist(value):
        persisted.append(dict(value))

    return TEST_JOB_CONTEXT.set((state, persist)), persisted


def test_cursor_is_signed_project_and_run_bound_and_preserves_gap():
    project = Path("project-one").resolve()
    first = _TestWire(project, [ok("results", {
        "runGuid": "run-a", "resultStreamVersion": 1,
        "lastDeliveredEventSequence": 2,
        "events": [{"sequence": 1, "kind": "leaf_completed"}, {"sequence": 2, "kind": "leaf_completed"}],
    })])
    page = asyncio.run(first.test_results("run-a", cursor="begin", count=2))
    assert page.ok
    assert first.calls[0] == ("test.results", {
        "runGuid": "run-a", "incremental": True, "afterEventSequence": 0,
        "expectedResultStreamVersion": 0, "eventCount": 2,
    })

    wrong_project = _TestWire(Path("project-two").resolve())
    mismatch = asyncio.run(wrong_project.test_results("run-a", cursor=page.data["nextCursor"]))
    assert not mismatch.ok and mismatch.error.code == "TEST_RESULT_CURSOR_MISMATCH"
    assert mismatch.error.detail["sideEffectsMayHaveOccurred"] is False
    assert wrong_project.calls == []

    gap = _TestWire(project, [fail("results", "TEST_RESULT_CURSOR_GAP", "Read complete results.")])
    gap_result = asyncio.run(gap.test_results("run-a", cursor=page.data["nextCursor"]))
    assert not gap_result.ok and gap_result.error.code == "TEST_RESULT_CURSOR_GAP"
    assert gap_result.error.detail["sideEffectsMayHaveOccurred"] is False


def test_cursor_rejections_are_read_only_and_invalid_types_do_not_dispatch():
    project = Path("project-invalid-cursor").resolve()
    service = _TestWire(project)

    result = asyncio.run(service.test_results("run-a", cursor=None))

    assert not result.ok and result.error.code == "TEST_RESULT_CURSOR_INVALID"
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False
    assert service.calls == []


def test_incremental_cursor_preserves_leaf_summary_and_stream_identity():
    project = Path("project-leaf-summary").resolve()
    service = _TestWire(project, [ok("results", {
        "runGuid": "run-a", "resultStreamVersion": 7,
        "lastDeliveredEventSequence": 1,
        "events": [{"sequence": 1, "kind": "leaf_completed", "leafKey": "fixture.pass"}],
        "completedLeafCount": 1, "passedSoFar": 1, "failedSoFar": 0,
        "skippedSoFar": 0, "lastCompletedTest": "Fixture.Pass",
        "firstFailure": "", "intermediate": True, "terminal": False,
    })])

    page = asyncio.run(service.test_results("run-a", cursor="begin"))

    assert page.ok
    assert page.data["resultStreamVersion"] == 7
    assert page.data["completedLeafCount"] == page.data["passedSoFar"] == 1
    assert page.data["intermediate"] is True
    assert page.data["nextCursor"]


def test_incremental_cursor_preserves_live_leaf_table_and_correction_events():
    project = Path("project-leaf-correction").resolve()
    service = _TestWire(project, [ok("results", {
        "runGuid": "run-a", "resultStreamVersion": 3,
        "lastDeliveredEventSequence": 2,
        "events": [
            {"sequence": 1, "kind": "leaf_completed", "leafKey": "case:one", "testStatus": "Passed"},
            {"sequence": 2, "kind": "leaf_corrected", "leafKey": "case:one", "testStatus": "Failed"},
        ],
        "results": [{"leafKey": "case:one", "testName": "Fixture.Case", "testStatus": "Failed"}],
        "completedLeafCount": 1, "passedSoFar": 1, "failedSoFar": 0, "terminal": False,
    })])

    page = asyncio.run(service.test_results("run-a", cursor="begin", count=2))

    assert page.ok
    assert page.data["results"] == [{
        "leafKey": "case:one", "testName": "Fixture.Case", "testStatus": "Failed",
    }]
    assert [event["kind"] for event in page.data["events"]] == ["leaf_completed", "leaf_corrected"]
    assert page.data["nextCursor"]


def test_incremental_stream_rejection_is_read_only():
    project = Path("project-stream").resolve()
    first = _TestWire(project, [ok("results", {
        "runGuid": "run-a", "resultStreamVersion": 1,
        "lastDeliveredEventSequence": 0, "events": [],
    })])
    cursor = asyncio.run(first.test_results("run-a", cursor="begin")).data["nextCursor"]
    replacement = _TestWire(project, [ok("results", {
        "runGuid": "run-a", "resultStreamVersion": 2,
        "lastDeliveredEventSequence": 0, "events": [],
    })])

    result = asyncio.run(replacement.test_results("run-a", cursor=cursor))

    assert not result.ok and result.error.code == "TEST_RESULT_CURSOR_STREAM_MISMATCH"
    assert result.error.detail["sideEffectsMayHaveOccurred"] is False


def test_wp08_native_tool_schemas_expose_cursor_and_preflight_contracts():
    from upilot_mcp.mcp_stdio_server import mcp

    exposed = {tool.name: tool for tool in asyncio.run(mcp.list_tools())}
    results_schema = exposed["unity_test_results"].inputSchema["properties"]
    acceptance_schema = exposed["unity_upilot_acceptance_run"].inputSchema["properties"]

    assert results_schema["cursor"]["default"] == ""
    assert results_schema["count"]["default"] == 100
    assert acceptance_schema["preflightOnly"]["default"] is False


def test_import_input_identity_includes_meta_and_reports_its_exact_change(tmp_path):
    project = tmp_path / "Tests~" / "UPilotTest2022"
    meta = project / "Assets" / "Imported.cs.meta"
    meta.parent.mkdir(parents=True)
    meta.write_text("guid: before\n", encoding="utf-8")
    before = acceptance_import_inputs(tmp_path, project)
    meta.write_text("guid: after\n", encoding="utf-8")
    after = acceptance_import_inputs(tmp_path, project)

    changes = diff_acceptance_import_inputs(before, after)

    assert "project:Assets/Imported.cs.meta" in before["inputs"]
    assert changes["changed"] == [{
        "path": "project:Assets/Imported.cs.meta",
        "beforeSha256": before["inputs"]["project:Assets/Imported.cs.meta"],
        "afterSha256": after["inputs"]["project:Assets/Imported.cs.meta"],
    }]


def test_start_send_state_is_unknown_until_a_response_and_is_never_replayed():
    project = Path("project-start").resolve()
    state = {"cancelRequested": False, "startIntentSent": False, "runGuid": ""}
    token, persisted = _with_job_context(state)
    try:
        interrupted = _TestWire(project, [
            ok("prepare", {"prepared": True, "action": "none", "items": []}),
            ConnectionError("response lost"),
        ])
        try:
            asyncio.run(interrupted.test_run())
        except ConnectionError:
            pass
        else:
            raise AssertionError("transport interruption must remain observable")
    finally:
        TEST_JOB_CONTEXT.reset(token)

    assert state["startIntentSent"] is True
    assert state["startSendState"] == "sent_unknown"
    assert state["runGuid"] == ""
    assert [snapshot.get("startSendState") for snapshot in persisted] == ["sent_unknown"]
    assert [name for name, _ in interrupted.calls].count("test.run") == 1


def test_start_response_marks_response_received_with_the_established_guid():
    project = Path("project-response").resolve()
    state = {"cancelRequested": False, "startIntentSent": False, "runGuid": ""}
    token, _ = _with_job_context(state)
    try:
        service = _TestWire(project, [
            ok("prepare", {"prepared": True, "action": "none", "items": []}),
            ok("run", {"runGuid": "run-established", "status": "running"}),
        ])
        result = asyncio.run(service.test_run())
    finally:
        TEST_JOB_CONTEXT.reset(token)

    assert result.ok
    assert state["startSendState"] == "response_received"
    assert state["runGuid"] == "run-established"


def test_unknown_cancel_send_state_observes_the_original_run_without_resend():
    project = Path("project-cancel").resolve()
    state = {
        "projectPath": str(project), "cancelRequested": True,
        "cancelSendState": "sent_unknown", "cleanupDeadlineAt": 0,
    }
    token, _ = _with_job_context(state)
    try:
        service = _TestWire(project, [ok("results", {"runGuid": "run-a", "status": "running"})])
        result = asyncio.run(service._wait_for_test_result("run-a", 10**15))
    finally:
        TEST_JOB_CONTEXT.reset(token)

    assert not result.ok and result.error.code == "TEST_RECOVERY_REQUIRED"
    assert result.error.detail["cancelSendState"] == "sent_unknown"
    assert [name for name, _ in service.calls] == ["test.results"]


def test_preflight_only_never_enters_capture_compile_or_runner():
    class Preflight(TestDomainService):
        def __init__(self):
            self.calls = []
            self.dispatcher = self

        async def mcp_status(self, **_kwargs):
            expected = Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest"
            return ok("status", {
                "connected": True, "serverReady": True,
                "paths": {"unityProjectAbsolute": str(expected)},
                "executionState": {"ready": False, "authoritative": False, "isStale": True},
            })

        async def ensure_ready(self, **_kwargs):
            self.calls.append("ensure_ready")
            raise AssertionError("preflight must not request readiness/compile")

        async def console_capture_list(self, **_kwargs):
            self.calls.append("capture_list")
            return ok("captures", {"sessions": []})

        async def call(self, _request_id, name, payload, **_kwargs):
            self.calls.append((name, payload))
            if name == "scene.list":
                return ok("scenes", {"scenes": []})
            raise AssertionError("preflight must not prepare scenes, compile, or start TestRunner")

        async def test_run(self, **_kwargs):
            self.calls.append("test_run")
            raise AssertionError("preflight must not start TestRunner")

    service = Preflight()
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))
    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["runnerStartAttempted"] is False
    assert result.error.detail["importState"] == "unknown"
    assert service.calls == ["capture_list", ("scene.list", {})]


def test_preflight_only_reports_active_capture_and_dirty_scene_without_preparing_or_stopping():
    class Preflight(TestDomainService):
        def __init__(self):
            self.dispatcher = self
            self.calls = []

        async def mcp_status(self, **_kwargs):
            expected = Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest"
            return ok("status", {
                "connected": True, "serverReady": True,
                "paths": {"unityProjectAbsolute": str(expected)},
                "executionState": {
                    "ready": True, "authoritative": True, "isStale": False,
                    "terminal": True, "errorsVerified": True, "compilePhase": "completed",
                    "lastCompileVerifiedAt": 10**15,
                },
            })

        async def console_capture_list(self, **_kwargs):
            self.calls.append("capture_list")
            return ok("captures", {"sessions": [{
                "sessionId": "capture-other", "ownerId": "other", "active": True,
            }]})

        async def call(self, _request_id, name, payload, **_kwargs):
            self.calls.append((name, payload))
            if name == "scene.list":
                return ok("scenes", {"scenes": [{"scenePath": "Assets/Dirty.unity", "isDirty": True}]})
            raise AssertionError("preflight must not prepare scenes, compile, or start TestRunner")

    service = Preflight()
    result = asyncio.run(service.upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["preflightPassed"] is False
    assert "ActiveConsoleCapture" in result.error.detail["blockingReasons"]
    assert "DirtyScenes" in result.error.detail["blockingReasons"]
    assert result.error.detail["activeConsoleCaptures"] == [{"sessionId": "capture-other", "ownerId": "other"}]
    assert result.error.detail["dirtyScenes"] == [{"scenePath": "Assets/Dirty.unity", "isDirty": True}]
    assert service.calls == ["capture_list", ("scene.list", {})]


def test_preflight_only_does_not_treat_a_historical_compile_as_current_import_evidence():
    class Preflight(TestDomainService):
        def __init__(self):
            self.dispatcher = self

        async def mcp_status(self, **_kwargs):
            expected = Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest"
            return ok("status", {
                "connected": True, "serverReady": True,
                "paths": {"unityProjectAbsolute": str(expected)},
                "executionState": {
                    "ready": True, "authoritative": True, "isStale": False,
                    "terminal": True, "errorsVerified": True, "compilePhase": "completed",
                    "lastCompileVerifiedAt": 1,
                },
            })

        async def console_capture_list(self, **_kwargs):
            return ok("captures", {"sessions": []})

        async def call(self, _request_id, name, _payload, **_kwargs):
            assert name == "scene.list"
            return ok("scenes", {"scenes": []})

    result = asyncio.run(Preflight().upilot_acceptance_run(preflight_only=True, write_artifact=False))

    assert not result.ok and result.error.code == "UPILOT_ACCEPTANCE_PREFLIGHT_ONLY"
    assert result.error.detail["importState"] == "unknown"
    assert "ImportNotVerified" in result.error.detail["blockingReasons"]
    assert result.error.detail["latestImportInputMtime"] > 1


def test_task_cancel_persist_failure_does_not_dispatch_or_retry():
    class FailingStore:
        _project_path = "project"

        def load_test_jobs(self):
            return []

        def save_test_job(self, _state):
            raise OSError("disk unavailable")

    service = TaskDomainService()
    service.server = SimpleNamespace(state=FailingStore())
    service._async_tasks = {
        "task-1": {
            "taskId": "task-1", "durable": True, "projectPath": "project", "terminal": False,
            "status": "running", "phase": "running", "startIntentSent": True, "runGuid": "run-a",
        }
    }
    service._async_task_handles = {}
    result = asyncio.run(service.task_cancel("task-1"))

    assert not result.ok and result.error.code == "TEST_TASK_PERSIST_FAILED"
    assert service._async_tasks["task-1"]["status"] == "RecoveryRequired"
    assert service._async_tasks["task-1"].get("cancelSendState", "not_sent") == "not_sent"
