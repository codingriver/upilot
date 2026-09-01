from __future__ import annotations

import asyncio

from upilot_mcp.domain.task_service import TaskDomainService
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import fail, ok
from upilot_mcp.state_store import StateStore
from upilot_mcp.wire_ids import (
    MAX_SAFE_INTEGER,
    normalize_wire_ids_for_unity,
    stringify_wire_ids,
)


class _QueuedDispatcher:
    def __init__(self, responses: list) -> None:
        self.responses = list(responses)
        self.calls: list[tuple[str, dict, int | None]] = []

    async def call(self, _request_id: str, name: str, payload: dict, timeout_ms: int | None = None):
        self.calls.append((name, payload, timeout_ms))
        assert self.responses, f"unexpected call: {name}"
        return self.responses.pop(0)


def _test_service(*responses) -> tuple[TestDomainService, _QueuedDispatcher]:
    service = TestDomainService()
    dispatcher = _QueuedDispatcher(list(responses))
    service.dispatcher = dispatcher
    return service, dispatcher


def test_wire_ids_preserve_large_decimal_strings_and_stringify_outputs() -> None:
    large = "568105589213741302"

    normalized = normalize_wire_ids_for_unity(
        {"parentId": large, "nested": {"gameObjectIds": [large, "42"]}}
    )
    assert normalized == {
        "parentId": 568105589213741302,
        "nested": {"gameObjectIds": [568105589213741302, 42]},
    }
    assert stringify_wire_ids(normalized) == {
        "parentId": large,
        "nested": {"gameObjectIds": [large, "42"]},
    }


def test_wire_ids_stringify_prefixed_selection_and_window_identity_fields() -> None:
    large = 568105589213741302

    assert stringify_wire_ids(
        {
            "activeGameObjectId": large,
            "selectedGameObjectIds": [large],
            "matchedWindowId": large,
        }
    ) == {
        "activeGameObjectId": str(large),
        "selectedGameObjectIds": [str(large)],
        "matchedWindowId": str(large),
    }


def test_wire_ids_reject_unsafe_integer_input() -> None:
    try:
        normalize_wire_ids_for_unity({"instanceId": MAX_SAFE_INTEGER + 1})
    except ValueError as exc:
        assert "decimal string" in str(exc)
    else:
        raise AssertionError("unsafe JavaScript integer input must be rejected")


def test_operation_milestones_persist_deduplicate_and_invalidate() -> None:
    service = TaskDomainService()
    state = {
        "operationId": "op-1",
        "milestones": [],
        "endedAt": 300,
        "failureSignature": "fixture_failed",
    }
    service._merge_operation_milestones(
        state,
        {
            "milestones": [
                {
                    "name": "Ready",
                    "occurrenceId": "ready-1",
                    "reachedAt": 100,
                    "invalidateOnFailure": True,
                    "evidence": {"frame": 17},
                }
            ]
        },
    )
    service._merge_operation_milestones(
        state,
        {"milestone": {"name": "Ready", "occurrenceId": "ready-1", "reachedAt": 100}},
    )

    assert len(state["milestones"]) == 1
    assert state["milestones"][0]["operationId"] == "op-1"
    assert state["milestones"][0]["evidence"] == {"frame": 17}

    service._invalidate_milestones_on_failure(state)
    milestone = state["milestones"][0]
    assert milestone["valid"] is False
    assert milestone["invalidatedAt"] == 300
    assert milestone["invalidatedReason"] == "fixture_failed"


def test_operation_milestone_remains_visible_in_public_state_after_later_poll() -> None:
    service = TaskDomainService()
    state = {
        "operationId": "op-public",
        "displayName": "fixture",
        "status": "Running",
        "phase": "Working",
        "startedAt": 100,
        "updatedAt": 150,
        "endedAt": 0,
        "timeoutSec": 60,
        "pollIntervalSec": 1,
        "artifacts": {},
        "timing": {},
        "milestones": [],
    }

    service._merge_operation_status(
        state,
        {
            "status": "Running",
            "milestone": {
                "name": "EnteredBattle",
                "occurrenceId": "battle-1",
                "reachedAt": 125,
                "evidence": {"frame": 42},
            },
        },
    )
    service._merge_operation_status(state, {"status": "Running", "phase": "Leaving"})

    public = service._public_operation_state(state, detail_level="summary")
    assert public["milestoneCount"] == 1
    assert public["milestones"] == [
        {
            "name": "EnteredBattle",
            "occurrenceId": "battle-1",
            "operationId": "op-public",
            "reachedAt": 125,
            "source": "project-status",
            "valid": True,
            "invalidatedAt": 0,
            "invalidatedReason": "",
            "invalidateOnFailure": False,
            "evidence": {"frame": 42},
        }
    ]


def test_compile_error_snapshot_updates_current_warning_count() -> None:
    state = StateStore()

    state.update_compile_errors(
        {"errors": [], "total": 0, "warningCount": 34}
    )

    assert state.compile.error_count == 0
    assert state.compile.warning_count == 34


def test_test_run_blocks_dirty_scenes_without_starting_tests() -> None:
    service, dispatcher = _test_service(
        ok(
            "scene-list",
            {
                "scenes": [
                    {
                        "scenePath": "Assets/Main.unity",
                        "sceneName": "Main",
                        "isDirty": True,
                        "isActive": True,
                    }
                ]
            },
        )
    )

    result = asyncio.run(service.test_run(test_filter="UPilotCoreTests"))

    assert result.ok is False
    assert result.error.code == "UNSAVED_SCENES"
    assert result.error.detail["blockedReason"] == "UnsavedScenes"
    assert result.error.detail["dirtyScenes"][0]["scenePath"] == "Assets/Main.unity"
    assert [call[0] for call in dispatcher.calls] == ["scene.list"]


def test_test_run_resolves_unique_short_class_name() -> None:
    service, dispatcher = _test_service(
        ok("scene-list", {"scenes": []}),
        ok(
            "test-list",
            {
                "discoveredCount": 2,
                "tests": [
                    "CodingRiver.UPilot.Tests.UPilotCoreTests.First",
                    "CodingRiver.UPilot.Tests.UPilotCoreTests.Second",
                ],
            },
        ),
        ok("test-run", {"status": "running", "runGuid": "run-1"}),
    )

    result = asyncio.run(service.test_run(test_filter="UPilotCoreTests"))

    assert result.ok is True
    assert dispatcher.calls[-1][0] == "test.run"
    assert dispatcher.calls[-1][1]["testFilter"] == "CodingRiver.UPilot.Tests.UPilotCoreTests"
    assert result.data["requestedTestFilter"] == "UPilotCoreTests"
    assert result.data["normalizedTestFilter"] == "CodingRiver.UPilot.Tests.UPilotCoreTests"


def test_test_run_reports_ambiguous_short_class_name() -> None:
    service, dispatcher = _test_service(
        ok("scene-list", {"scenes": []}),
        ok(
            "test-list",
            {
                "tests": [
                    "NamespaceA.SharedTests.First",
                    "NamespaceB.SharedTests.Second",
                ]
            },
        ),
    )

    result = asyncio.run(service.test_run(test_filter="SharedTests"))

    assert result.ok is False
    assert result.error.code == "TEST_FILTER_AMBIGUOUS"
    assert result.error.detail["filterCandidates"] == [
        "NamespaceA.SharedTests",
        "NamespaceB.SharedTests",
    ]
    assert [call[0] for call in dispatcher.calls] == ["scene.list", "test.list"]


def test_test_run_reports_short_class_no_match() -> None:
    service, _ = _test_service(
        ok("scene-list", {"scenes": []}),
        ok("test-list", {"discoveredCount": 1, "tests": ["Namespace.RealTests.First"]}),
    )

    result = asyncio.run(service.test_run(test_filter="MissingTests"))

    assert result.ok is False
    assert result.error.code == "TEST_FILTER_NO_MATCH"
    assert result.error.detail["noTestsReason"] == "FilterSyntaxOrScopeMismatch"


def test_test_run_preserves_fully_qualified_and_regex_filters() -> None:
    for test_filter in ("Namespace.RealTests", "regex:^Namespace\\."):
        service, dispatcher = _test_service(
            ok("scene-list", {"scenes": []}),
            ok("test-run", {"status": "running"}),
        )

        result = asyncio.run(service.test_run(test_filter=test_filter))

        assert result.ok is True
        assert [call[0] for call in dispatcher.calls] == ["scene.list", "test.run"]
        assert dispatcher.calls[-1][1]["testFilter"] == test_filter


def test_test_run_fails_closed_when_scene_preflight_is_unavailable() -> None:
    service, dispatcher = _test_service(
        fail("scene-list", "UNITY_NOT_CONNECTED", "not connected")
    )

    result = asyncio.run(service.test_run())

    assert result.ok is False
    assert result.error.code == "TEST_PREFLIGHT_FAILED"
    assert result.error.detail["blockedReason"] == "SceneStateUnavailable"
    assert [call[0] for call in dispatcher.calls] == ["scene.list"]
