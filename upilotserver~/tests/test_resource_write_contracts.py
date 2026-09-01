from __future__ import annotations

import asyncio

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.resource_service import (
    ResourceDomainService,
    _normalize_component_property_writes,
    _require_mutation_success,
)
from upilot_mcp.responses import ok


class _RecordingDispatcher:
    def __init__(self) -> None:
        self.calls: list[tuple[str, str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict):
        self.calls.append((request_id, name, payload))
        return ok(
            request_id,
            {"ok": True, "modifiedCount": len(payload.get("properties", []))},
        )


def test_component_property_object_is_normalized_to_typed_write_list() -> None:
    assert _normalize_component_property_writes(
        {
            "m_Name": "probe",
            "enabled": True,
            "count": 7,
            "nested.position": {"x": 1.5, "y": 2},
        }
    ) == [
        {"propertyPath": "m_Name", "value": "probe"},
        {"propertyPath": "enabled", "value": "true"},
        {"propertyPath": "count", "value": "7"},
        {"propertyPath": "nested.position", "value": '{"x":1.5,"y":2}'},
    ]


def test_component_modify_dispatches_bridge_compatible_property_writes(monkeypatch) -> None:
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    dispatcher = _RecordingDispatcher()
    service = ResourceDomainService()
    service.dispatcher = dispatcher

    result = asyncio.run(
        service.component_modify(
            game_object_id=42,
            component_type="WriteContractProbe",
            properties={"text": "updated", "flag": False, "mode": "Second"},
            component_index=1,
        )
    )

    assert result.ok is True
    assert len(dispatcher.calls) == 1
    _, name, payload = dispatcher.calls[0]
    assert name == "component.modify"
    assert payload == {
        "gameObjectId": 42,
        "componentType": "WriteContractProbe",
        "properties": [
            {"propertyPath": "text", "value": "updated"},
            {"propertyPath": "flag", "value": "false"},
            {"propertyPath": "mode", "value": "Second"},
        ],
        "componentIndex": 1,
    }


def test_component_modify_rejects_empty_property_object_before_dispatch(monkeypatch) -> None:
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    dispatcher = _RecordingDispatcher()
    service = ResourceDomainService()
    service.dispatcher = dispatcher

    result = asyncio.run(
        service.component_modify(
            game_object_id=42,
            component_type="WriteContractProbe",
            properties={},
        )
    )

    assert result.ok is False
    assert result.error.code == "COMPONENT_MODIFY_INVALID_PROPERTIES"
    assert dispatcher.calls == []


def test_asset_get_data_forwards_depth_node_limit_and_continuation() -> None:
    dispatcher = _RecordingDispatcher()
    service = ResourceDomainService()
    service.dispatcher = dispatcher

    result = asyncio.run(
        service.asset_get_data(
            asset_path="Assets/Probe.asset",
            max_depth=3,
            max_nodes=25,
            continuation_token="v1:25",
        )
    )

    assert result.ok is True
    _, name, payload = dispatcher.calls[0]
    assert name == "asset.getData"
    assert payload == {
        "assetPath": "Assets/Probe.asset",
        "maxDepth": 3,
        "maxNodes": 25,
        "continuationToken": "v1:25",
    }


def test_mutation_success_requires_matching_inner_ok_and_verification() -> None:
    accepted = _require_mutation_success(
        ok("req-good", {"ok": True, "verified": True}),
        "unity_asset_copy",
    )
    assert accepted.ok is True

    rejected = _require_mutation_success(
        ok("req-bad", {"ok": False, "verified": True, "status": "ok"}),
        "unity_asset_copy",
    )
    assert rejected.ok is False
    assert rejected.error.code == "RESULT_CONTRACT_VIOLATION"
    assert rejected.error.detail["bridgeData"]["ok"] is False


def test_prefab_physics_audit_dispatches_one_bounded_read_only_batch() -> None:
    dispatcher = _RecordingDispatcher()
    service = ResourceDomainService()
    service.dispatcher = dispatcher

    result = asyncio.run(
        service.prefab_physics_audit(
            ["Assets/A.prefab", "Assets/B.prefab"],
            max_results_per_prefab=250,
            sort_by="triggerCount",
            descending=False,
        )
    )

    assert result.ok is True
    _, name, payload = dispatcher.calls[0]
    assert name == "prefab.physicsAudit"
    assert payload == {
        "prefabPaths": ["Assets/A.prefab", "Assets/B.prefab"],
        "maxResultsPerPrefab": 250,
        "sortBy": "triggerCount",
        "descending": False,
    }
