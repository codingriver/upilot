from __future__ import annotations

import asyncio

from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.responses import fail, ok


class _RecordingDispatcher:
    def __init__(self, response):
        self.response = response
        self.calls: list[tuple[str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict):
        self.calls.append((name, payload))
        return self.response(request_id) if callable(self.response) else self.response


def _service(dispatcher: _RecordingDispatcher) -> ResourceDomainService:
    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = dispatcher
    return service


def _reverse_query() -> dict:
    return {"kind": "object", "guid": "fixture-guid", "localFileId": "11400000"}


def test_wp11_invalid_public_shapes_are_rejected_before_unity_dispatch() -> None:
    dispatcher = _RecordingDispatcher(lambda request_id: ok(request_id, {}))
    service = _service(dispatcher)

    requests = [
        {"asset_path": "Assets/Test.prefab", "evidence_mode": 1},
        {"asset_path": "Assets/Test.prefab", "runtime_boundary": False},
        {"asset_path": "Assets/Test.prefab", "direction": ["forward"]},
        {
            "asset_path": "",
            "evidence_mode": "object",
            "direction": "reverse",
            "reference_query": _reverse_query(),
            "scope": ["Assets/Fixtures"],
            "max_nodes": 0,
        },
    ]

    results = [asyncio.run(service.asset_dependencies(**request)) for request in requests]

    assert [result.error.code for result in results] == [
        "DEPENDENCY_EVIDENCE_MODE_INVALID",
        "DEPENDENCY_RUNTIME_BOUNDARY_INVALID",
        "DEPENDENCY_DIRECTION_INVALID",
        "DEPENDENCY_NODE_BUDGET_INVALID",
    ]
    assert all(result.error.detail["sideEffectsMayHaveOccurred"] is False for result in results)
    assert dispatcher.calls == []


def test_wp11_dependency_response_preserves_observed_state_and_marks_legacy_unknown() -> None:
    observed_dispatcher = _RecordingDispatcher(
        lambda request_id: ok(request_id, {
            "coverageComplete": False,
            "truncationReason": "nodeBudget",
            "continuationToken": "cursor:0",
            "nextContinuationToken": "cursor:17",
            "readOnly": True,
            "changedEditorState": False,
            "sideEffectsMayHaveOccurred": False,
        })
    )
    observed = asyncio.run(_service(observed_dispatcher).asset_dependencies(
        asset_path="",
        evidence_mode="object",
        direction="reverse",
        reference_query=_reverse_query(),
        scope=["Assets/Fixtures"],
        max_nodes=17,
    ))

    legacy_dispatcher = _RecordingDispatcher(
        lambda request_id: ok(request_id, {"coverageComplete": True, "dependencies": []})
    )
    legacy = asyncio.run(_service(legacy_dispatcher).asset_dependencies("Assets/Test.prefab"))

    assert observed.ok
    assert observed.data["readOnly"] is True
    assert observed.data["changedEditorState"] is False
    assert observed.data["changeStateEvidence"] == "unityPayload"
    assert observed.data["continuationToken"] == "cursor:0"
    assert observed.data["nextContinuationToken"] == "cursor:17"
    assert legacy.ok
    assert legacy.data["readOnly"] is None
    assert legacy.data["changedEditorState"] is None
    assert legacy.data["changeStateEvidence"] == "unknown"


def test_wp11_reverse_resume_canonicalizes_the_bound_query_before_dispatch() -> None:
    dispatcher = _RecordingDispatcher(lambda request_id: ok(request_id, {
        "coverageComplete": False,
        "continuationToken": "cursor:17",
        "readOnly": True,
        "changedEditorState": False,
    }))
    service = _service(dispatcher)

    result = asyncio.run(service.asset_dependencies(
        asset_path="",
        evidence_mode="object",
        direction="reverse",
        reference_query={
            "kind": "stringLiteral",
            "value": "stable-key",
            "propertyPaths": ["m_second", "m_first", "m_second"],
        },
        scope=["Assets/Z", "Assets/A", "Assets/Z"],
        continuation_token="cursor:0",
    ))

    assert result.ok
    assert dispatcher.calls == [("asset.dependencies", {
        "assetPath": "", "recursive": True,
        "evidenceMode": "object", "runtimeBoundary": "none", "direction": "reverse",
        "referenceQuery": {
            "kind": "stringLiteral", "value": "stable-key",
            "propertyPaths": ["m_first", "m_second"],
        },
        "scope": ["Assets/A", "Assets/Z"],
        "maxNodes": 500, "timeBudgetMs": 5000, "continuationToken": "cursor:0",
    })]


def test_wp11_prefab_and_dependency_bridge_errors_are_not_rewritten_or_retried() -> None:
    dependency_error = fail(
        "bridge-dependency",
        "REFERENCE_SOURCE_CHANGED",
        "Candidate assets changed after the first page.",
        {"cursorStatus": "invalidated", "sourceFingerprint": "changed"},
    )
    dependency_dispatcher = _RecordingDispatcher(dependency_error)
    dependency = asyncio.run(_service(dependency_dispatcher).asset_dependencies(
        asset_path="",
        evidence_mode="object",
        direction="reverse",
        reference_query=_reverse_query(),
        scope=["Assets/Fixtures"],
        continuation_token="cursor:0",
    ))

    prefab_error = fail(
        "bridge-prefab",
        "PREFAB_QUERY_COMPONENTS_FAILED",
        "Unload failed.",
        {"cleanupVerified": False, "unresolvedResources": ["prefabContents:Assets/Test.prefab"]},
    )
    prefab_dispatcher = _RecordingDispatcher(prefab_error)
    prefab = asyncio.run(_service(prefab_dispatcher).prefab_query_components("Assets/Test.prefab", "Probe"))

    assert dependency is dependency_error
    assert dependency.error.code == "REFERENCE_SOURCE_CHANGED"
    assert dependency.error.detail["sourceFingerprint"] == "changed"
    assert dependency_dispatcher.calls[0][1]["continuationToken"] == "cursor:0"
    assert len(dependency_dispatcher.calls) == 1
    assert prefab is prefab_error
    assert prefab.error.code == "PREFAB_QUERY_COMPONENTS_FAILED"
    assert prefab.error.detail["cleanupVerified"] is False
    assert len(prefab_dispatcher.calls) == 1
