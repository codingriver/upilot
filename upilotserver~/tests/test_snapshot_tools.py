from __future__ import annotations

import asyncio
import hashlib
from types import SimpleNamespace

import pytest
from PIL import Image
from mcp.server.fastmcp.exceptions import ToolError

from upilot_mcp import mcp_stdio_server as runtime
from upilot_mcp.domain.snapshot_service import SnapshotDomainService
from upilot_mcp.mcp_tools import snapshot_tools
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY, dispatch_public_tool


class _Dispatcher:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls: list[tuple[str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict):
        self.calls.append((name, payload))
        if not self.responses:
            raise AssertionError(f"Unexpected dispatcher call: {name}")
        return self.responses.pop(0)


class _RunningDispatcher:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict]] = []

    async def call(self, request_id: str, name: str, payload: dict):
        self.calls.append((name, payload))
        if name == "snapshot.start":
            return ok("start", {"snapshotId": "snapshot-2", "status": "queued", "terminal": False})
        return ok("status", {"snapshotId": "snapshot-2", "status": "running", "terminal": False})


def _service(*responses):
    service = SnapshotDomainService()
    service.dispatcher = _Dispatcher(responses)
    return service


def _service_with_project(project_root, *responses):
    service = _service(*responses)
    service.server = SimpleNamespace(
        session_manager=SimpleNamespace(
            active=SimpleNamespace(project_path=str(project_root))
        )
    )
    return service


def _persisted_snapshot(
    *,
    snapshot_id: str = "snapshot-persisted",
    status: str = "completed",
    terminal: bool = True,
    persistence_status: str = "verified",
    schema: object = 2,
    sequence: object = 7,
    manifest_bytes: object = 1200,
    manifest_sha256: str = "a" * 64,
    persistence_error: str = "",
    recovered: bool = False,
) -> dict:
    return {
        "snapshotId": snapshot_id,
        "status": status,
        "terminal": terminal,
        "persistenceSchemaVersion": schema,
        "snapshotSequence": sequence,
        "persistenceStatus": persistence_status,
        "persistenceError": persistence_error,
        "persistenceRecovered": recovered,
        "manifestBytes": manifest_bytes,
        "manifestSha256": manifest_sha256,
    }


def test_snapshot_capture_rejects_missing_and_excessive_targets_without_dispatch() -> None:
    missing = _service()
    missing_result = asyncio.run(missing.snapshot_capture([]))
    assert missing_result.ok is False
    assert missing_result.error.code == "SNAPSHOT_TARGETS_REQUIRED"
    assert missing.dispatcher.calls == []

    excessive = _service()
    excessive_result = asyncio.run(
        excessive.snapshot_capture([{"kind": "camera"}] * 17)
    )
    assert excessive_result.ok is False
    assert excessive_result.error.code == "SNAPSHOT_TARGET_LIMIT_EXCEEDED"
    assert excessive.dispatcher.calls == []


def test_snapshot_capture_applies_strict_defaults_and_polls_to_terminal() -> None:
    service = _service(
        ok("start", {"snapshotId": "snapshot-1", "status": "queued", "terminal": False}),
        ok("status", {"snapshotId": "snapshot-1", "status": "completed", "terminal": True}),
    )

    result = asyncio.run(
        service.snapshot_capture(
            [{"targetId": "main", "kind": "camera", "exactName": "Main Camera"}],
            wait_ms=100,
        )
    )

    assert result.ok is True
    assert result.data["status"] == "completed"
    assert [call[0] for call in service.dispatcher.calls] == [
        "snapshot.start",
        "snapshot.status",
    ]
    start_payload = service.dispatcher.calls[0][1]
    assert start_payload["channels"] == ["color"]
    assert start_payload["syncMode"] == "sameFrame"
    assert start_payload["completionPolicy"] == "allOrNothing"
    assert start_payload["capturePolicy"] == {
        "requireVerifiedPixels": True,
        "allowFallback": False,
        "allowOcclusionSensitive": False,
        "allowStaleFrame": False,
        "maxStaleFrameAgeMs": 0,
    }


def test_snapshot_capture_nested_schema_rejects_before_native_or_proxy_dispatch(monkeypatch) -> None:
    """P2-WP-04-T03: public nested schema is strict before snapshot.start."""

    schema = {
        tool.name: tool for tool in asyncio.run(runtime._original_mcp_list_tools())
    }["unity_snapshot_capture"].inputSchema
    target = schema["properties"]["targets"]["items"]
    policy = schema["properties"]["capturePolicy"]["anyOf"][0]
    assert schema["properties"]["syncMode"]["const"] == "sameFrame"
    assert schema["properties"]["completionPolicy"]["enum"] == ["allOrNothing", "bestEffort"]
    assert target["additionalProperties"] is False
    assert target["properties"]["kind"]["default"] == "camera"
    assert target["properties"]["width"]["default"] == 1280
    assert target["properties"]["height"]["default"] == 720
    assert target["properties"]["domainGeneration"]["default"] == ""
    assert target["properties"]["channels"]["type"] == "array"
    assert policy["additionalProperties"] is False
    assert policy["properties"]["requireVerifiedPixels"]["default"] is True
    assert policy["properties"]["allowStaleFrame"]["default"] is False

    calls: list[dict] = []

    class Facade:
        async def snapshot_capture(
            self,
            targets,
            *,
            channels=None,
            sync_mode="sameFrame",
            completion_policy="allOrNothing",
            capture_policy=None,
            output_directory="",
            wait_ms=5000,
            request_key="",
        ):
            calls.append({
                "targets": targets,
                "channels": channels,
                "capturePolicy": capture_policy,
            })
            return ok("snapshot", {"targetCount": len(targets)})

    facade = Facade()
    monkeypatch.setattr(snapshot_tools, "_get_facade", lambda: facade)

    native = asyncio.run(runtime.mcp._tool_manager.call_tool(
        "unity_snapshot_capture", {"targets": [{"exactName": "Main Camera"}]}
    ))
    assert native.structuredContent["data"]["targetCount"] == 1
    assert calls[0]["targets"] == [{
        "targetId": "", "instanceId": "", "hierarchyPath": "",
        "exactName": "Main Camera", "cameraName": "", "fullTypeName": "",
        "title": "", "kind": "camera", "targetDisplay": 0, "channels": [],
        "depthPreview": False, "width": 1280, "height": 720,
        "domainGeneration": "", "requireContentRect": False,
    }]
    assert calls[0]["capturePolicy"] == {
        "requireVerifiedPixels": True, "allowFallback": False,
        "allowOcclusionSensitive": False, "allowStaleFrame": False,
        "maxStaleFrameAgeMs": 0,
    }

    with pytest.raises(ToolError):
        asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_snapshot_capture", {"targets": [{"exactName": "Never", "unknown": True}]}
        ))
    with pytest.raises(ToolError):
        asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_snapshot_capture", {"targets": [{"width": "1280"}]}
        ))
    with pytest.raises(ToolError):
        asyncio.run(runtime.mcp._tool_manager.call_tool(
            "unity_snapshot_capture", {
                "targets": [{"exactName": "Never"}],
                "capturePolicy": {"unknown": True},
            }
        ))
    assert len(calls) == 1

    proxy = asyncio.run(dispatch_public_tool(
        facade,
        "unity_snapshot_capture",
        {"targets": [{"exactName": "Proxy"}], "capturePolicy": {"allowFallback": True}},
    ))
    assert proxy.ok is True
    assert calls[1]["targets"][0]["width"] == 1280
    assert calls[1]["capturePolicy"] == {
        "requireVerifiedPixels": True, "allowFallback": True,
        "allowOcclusionSensitive": False, "allowStaleFrame": False,
        "maxStaleFrameAgeMs": 0,
    }

    rejected_proxy = asyncio.run(dispatch_public_tool(
        facade,
        "unity_snapshot_capture",
        {"targets": [{"exactName": "Never", "unknown": True}]},
    ))
    assert rejected_proxy.ok is False
    assert rejected_proxy.error.code == "INVALID_TOOL_ARGUMENTS"
    assert rejected_proxy.error.detail["sideEffectsMayHaveOccurred"] is False
    assert len(calls) == 2


def test_editor_window_target_normalization_returns_structured_failure_without_name_error() -> None:
    service = _service()
    result = asyncio.run(service.snapshot_capture([
        {"kind": "editorWindow", "instanceId": "42", "docked": True, "requireContentRect": False}
    ]))

    assert result.ok is False
    assert result.error.code == "SNAPSHOT_DOCKED_WINDOW_REQUIRES_CONTENT_RECT"
    assert service.dispatcher.calls == []

    calls: list[dict] = []

    class Facade:
        async def snapshot_capture(
            self,
            targets,
            *,
            channels=None,
            sync_mode="sameFrame",
            completion_policy="allOrNothing",
            capture_policy=None,
            output_directory="",
            wait_ms=5000,
            request_key="",
        ):
            calls.append({
                "targets": targets,
                "channels": channels,
                "capturePolicy": capture_policy,
            })
            return ok("snapshot", {"targetCount": len(targets)})

    facade = Facade()
    rejected_policy_proxy = asyncio.run(dispatch_public_tool(
        facade,
        "unity_snapshot_capture",
        {"targets": [{"exactName": "Never"}], "capturePolicy": {"allowFallback": "true"}},
    ))
    assert rejected_policy_proxy.ok is False
    assert rejected_policy_proxy.error.code == "INVALID_TOOL_ARGUMENTS"
    assert rejected_policy_proxy.error.detail["sideEffectsMayHaveOccurred"] is False
    assert len(calls) == 0


def test_snapshot_capture_wait_window_is_not_a_terminal_timeout() -> None:
    service = SnapshotDomainService()
    service.dispatcher = _RunningDispatcher()

    result = asyncio.run(
        service.snapshot_capture(
            [{"targetId": "main", "kind": "camera", "exactName": "Main Camera"}],
            wait_ms=1,
        )
    )

    assert result.ok is True
    assert result.data["snapshotId"] == "snapshot-2"
    assert result.data["waitWindowElapsed"] is True
    assert result.data["terminal"] is False


def test_snapshot_status_cancel_and_collect_use_exact_job_identity() -> None:
    service = _service(
        ok("status", {"snapshotId": "snapshot-3"}),
        ok("cancel", {"snapshotId": "snapshot-3", "cancelRequested": True}),
        ok("collect", {"snapshotId": "snapshot-3", "artifacts": []}),
    )

    asyncio.run(service.snapshot_status("snapshot-3", "full"))
    asyncio.run(service.snapshot_cancel("snapshot-3"))
    asyncio.run(service.snapshot_collect_artifacts("snapshot-3"))

    assert service.dispatcher.calls == [
        ("snapshot.status", {"snapshotId": "snapshot-3", "detailLevel": "full"}),
        ("snapshot.cancel", {"snapshotId": "snapshot-3"}),
        ("snapshot.collect", {"snapshotId": "snapshot-3"}),
    ]


@pytest.mark.parametrize(
    ("case", "payload", "expected_verified"),
    [
        # T01: a terminal v2 state may be accepted only with real manifest metadata.
        ("P2-WP-06-T01", _persisted_snapshot(), True),
        # T02: a leftover tmp is never authority; interrupted persistence remains unverified.
        ("P2-WP-06-T02", _persisted_snapshot(persistence_status="unverified", persistence_error="MANIFEST_REPLACE_FAILED"), False),
        # T03: Unity recovered a newer terminal manifest and rebuilt its state sidecar.
        ("P2-WP-06-T03", _persisted_snapshot(recovered=True), True),
        # T04: missing state/manifest or unequal same-sequence projection cannot be signed.
        ("P2-WP-06-T04", _persisted_snapshot(persistence_status="unverified", persistence_error="MANIFEST_STATE_MISMATCH"), False),
        # T05: a delayed stale sequence remains a persistence diagnostic, not a new capture.
        ("P2-WP-06-T05", _persisted_snapshot(persistence_status="unverified", persistence_error="MANIFEST_SEQUENCE_STALE"), False),
        # T06: legacy schema and path/requestKey rejection are not silently upgraded.
        ("P2-WP-06-T06", _persisted_snapshot(persistence_status="unknown", schema=1, sequence=0, manifest_bytes=0, manifest_sha256="", persistence_error="LEGACY_PERSISTENCE_SCHEMA"), False),
        # T07: cancellation preserves its business outcome while persistence stays separate.
        ("P2-WP-06-T07", _persisted_snapshot(status="cancelled", persistence_status="unverified", persistence_error="STATE_REPLACE_FAILED"), False),
        # T08: unknown schema/rebuild failure must never be inferred from a terminal status.
        ("P2-WP-06-T08", _persisted_snapshot(persistence_status="unverified", persistence_error="STATE_REBUILD_FAILED", manifest_sha256="not-a-hash"), False),
        # A Bridge must not be trusted when it claims both a verified state and
        # a persistence failure; this is a persistence contradiction, not a
        # reason for the Server to rewrite the underlying business outcome.
        ("verified-status-with-persistence-error", _persisted_snapshot(persistence_error="STATE_REPLACE_FAILED"), False),
        ("nonterminal-cannot-be-persistence-verified", _persisted_snapshot(status="running", terminal=False), False),
    ],
)
def test_snapshot_status_exposes_conservative_persistence_verdict(case, payload, expected_verified) -> None:
    service = _service(ok(case, payload))

    result = asyncio.run(service.snapshot_status("snapshot-persisted"))

    assert result.ok is True, case
    assert result.data["persistenceVerified"] is expected_verified, case
    assert result.data["persistenceStatus"] == payload["persistenceStatus"], case
    assert result.data["persistenceError"] == payload["persistenceError"], case
    assert [name for name, _ in service.dispatcher.calls] == ["snapshot.status"], case


@pytest.mark.parametrize("field,value", [
    ("persistenceSchemaVersion", "2"),
    ("snapshotSequence", "7"),
    ("manifestBytes", "1200"),
    ("snapshotSequence", True),
])
def test_snapshot_persistence_metadata_requires_exact_integer_types(field, value) -> None:
    payload = _persisted_snapshot()
    payload[field] = value
    service = _service(ok("status", payload))

    result = asyncio.run(service.snapshot_status("snapshot-persisted"))

    assert result.ok is True
    assert result.data["persistenceVerified"] is False


def test_snapshot_persistence_requires_exact_job_and_request_key_without_retrying() -> None:
    wrong_status = _service(ok("status", _persisted_snapshot(snapshot_id="snapshot-other")))
    status = asyncio.run(wrong_status.snapshot_status("snapshot-expected"))
    assert status.ok is False
    assert status.error.code == "SNAPSHOT_IDENTITY_MISMATCH"
    assert status.error.detail["expectedSnapshotId"] == "snapshot-expected"
    assert status.error.detail["actualSnapshotId"] == "snapshot-other"
    assert [name for name, _ in wrong_status.dispatcher.calls] == ["snapshot.status"]

    wrong_request_key = _service(ok("start", {
        "snapshotId": "snapshot-start",
        "status": "queued",
        "terminal": False,
        "requestKey": "other-request",
    }))
    started = asyncio.run(wrong_request_key.snapshot_capture(
        [{"targetId": "main", "kind": "camera", "exactName": "Main Camera"}],
        wait_ms=0,
        request_key="expected-request",
    ))
    assert started.ok is False
    assert started.error.code == "SNAPSHOT_REQUEST_KEY_MISMATCH"
    assert [name for name, _ in wrong_request_key.dispatcher.calls] == ["snapshot.start"]


def test_snapshot_identity_missing_is_rejected_without_retry_or_new_capture() -> None:
    service = _service(
        ok("status", {"status": "completed", "terminal": True}),
        ok("cancel", {"cancelRequested": True}),
        ok("collect", {"artifacts": []}),
    )

    status = asyncio.run(service.snapshot_status("snapshot-original"))
    cancel = asyncio.run(service.snapshot_cancel("snapshot-original"))
    collected = asyncio.run(service.snapshot_collect_artifacts("snapshot-original"))

    for result in (status, cancel, collected):
        assert result.ok is False
        assert result.error.code == "SNAPSHOT_IDENTITY_MISSING"
        assert result.error.detail["expectedSnapshotId"] == "snapshot-original"
    assert [name for name, _ in service.dispatcher.calls] == [
        "snapshot.status", "snapshot.cancel", "snapshot.collect",
    ]

    empty_payload = _service(ok("status", None))
    empty_result = asyncio.run(empty_payload.snapshot_status("snapshot-original"))
    assert empty_result.ok is False
    assert empty_result.error.code == "SNAPSHOT_IDENTITY_MISSING"
    assert [name for name, _ in empty_payload.dispatcher.calls] == ["snapshot.status"]


def test_snapshot_server_preserves_unverified_pixel_evidence_without_fabricating_pass() -> None:
    payload = _persisted_snapshot(persistence_status="unverified", persistence_error="STATE_REPLACE_FAILED")
    payload.update({"acceptedAsEvidence": False, "pixelSourceVerified": False, "occlusionSensitive": True})
    service = _service(ok("collect", payload))

    result = asyncio.run(service.snapshot_collect_artifacts("snapshot-persisted"))

    assert result.ok is True
    assert result.data["persistenceVerified"] is False
    assert result.data["acceptedAsEvidence"] is False
    assert result.data["pixelSourceVerified"] is False
    assert result.data["occlusionSensitive"] is True
    assert [name for name, _ in service.dispatcher.calls] == ["snapshot.collect"]


def test_snapshot_public_tools_are_registered_with_correct_idempotency() -> None:
    from upilot_mcp import mcp_stdio_server as _runtime  # noqa: F401

    names = {
        "unity_camera_list",
        "unity_snapshot_capture",
        "unity_snapshot_status",
        "unity_snapshot_cancel",
        "unity_snapshot_collect_artifacts",
        "unity_snapshot_baseline_list",
        "unity_snapshot_baseline_compare",
        "unity_snapshot_baseline_update",
    }
    descriptors = {name: REGISTRY.resolve(name) for name in names}

    assert all(descriptors.values())
    assert all(item.category == "snapshot" for item in descriptors.values())
    assert descriptors["unity_snapshot_capture"].idempotent is False
    assert descriptors["unity_snapshot_cancel"].idempotent is False
    assert descriptors["unity_snapshot_status"].idempotent is True
    assert descriptors["unity_snapshot_baseline_update"].idempotent is False
    assert descriptors["unity_snapshot_baseline_update"].destructive is True
    assert descriptors["unity_snapshot_baseline_update"].requires_write_access is True


def test_snapshot_baseline_update_requires_current_confirm_token_and_compare_emits_evidence(tmp_path) -> None:
    candidate = tmp_path / "Temp" / "snapshot" / "candidate.png"
    candidate.parent.mkdir(parents=True)
    image = Image.new("RGBA", (16, 16), (32, 96, 160, 255))
    image.save(candidate, format="PNG")
    raw = candidate.read_bytes()
    artifact = {
        "artifactId": "artifact-color",
        "targetId": "main",
        "role": "color",
        "path": candidate.relative_to(tmp_path).as_posix(),
        "mimeType": "image/png",
        "sha256": hashlib.sha256(raw).hexdigest(),
        "acceptedAsEvidence": True,
    }

    def collected():
        return ok("collect", {
            "snapshotId": "snapshot-baseline",
            "status": "completed",
            "artifacts": [artifact],
        })

    service = _service_with_project(
        tmp_path,
        collected(),
        collected(),
        collected(),
        collected(),
    )

    preview = asyncio.run(service.snapshot_baseline_update(
        "ui/main",
        "snapshot-baseline",
        "main",
    ))
    assert preview.ok is True
    assert preview.data["dryRun"] is True
    assert preview.data["operation"] == "create"
    assert preview.data["confirmToken"]
    assert not (tmp_path / ".upilot" / "snapshots" / "baselines" / "ui" / "main.png").exists()

    rejected = asyncio.run(service.snapshot_baseline_update(
        "ui/main",
        "snapshot-baseline",
        "main",
        dry_run=False,
        confirm_token="wrong",
    ))
    assert rejected.ok is False
    assert rejected.error.code == "SNAPSHOT_BASELINE_CONFIRM_TOKEN_INVALID"

    applied = asyncio.run(service.snapshot_baseline_update(
        "ui/main",
        "snapshot-baseline",
        "main",
        dry_run=False,
        confirm_token=preview.data["confirmToken"],
    ))
    assert applied.ok is True
    assert applied.data["confirmTokenAccepted"] is True

    listed = asyncio.run(service.snapshot_baseline_list("ui/"))
    assert listed.ok is True
    assert listed.data["count"] == 1
    assert listed.data["baselines"][0]["baselineKey"] == "ui/main"

    compared = asyncio.run(service.snapshot_baseline_compare(
        "ui/main",
        "snapshot-baseline",
        "main",
        output_directory="Temp/comparison",
    ))
    assert compared.ok is True
    assert compared.data["passed"] is True
    assert compared.data["metrics"]["differentPixelRatio"] == 0.0
    assert compared.data["metrics"]["ssim"] == 1.0
    assert {item["role"] for item in compared.data["artifacts"]} == {"diff", "heatmap"}


def test_snapshot_baseline_rejects_unsafe_key_and_unaccepted_artifact(tmp_path) -> None:
    service = _service_with_project(tmp_path)
    invalid = asyncio.run(service.snapshot_baseline_update(
        "../escape",
        "snapshot-1",
        "main",
    ))
    assert invalid.ok is False
    assert invalid.error.code == "SNAPSHOT_BASELINE_KEY_INVALID"

    candidate = tmp_path / "candidate.png"
    Image.new("RGBA", (2, 2), (0, 0, 0, 255)).save(candidate)
    raw = candidate.read_bytes()
    service.dispatcher = _Dispatcher([ok("collect", {
        "snapshotId": "snapshot-1",
        "artifacts": [{
            "artifactId": "rejected",
            "targetId": "main",
            "role": "color",
            "path": "candidate.png",
            "mimeType": "image/png",
            "sha256": hashlib.sha256(raw).hexdigest(),
            "acceptedAsEvidence": False,
        }],
    })])
    rejected = asyncio.run(service.snapshot_baseline_update(
        "safe/key",
        "snapshot-1",
        "main",
    ))
    assert rejected.ok is False
    assert rejected.error.code == "SNAPSHOT_ARTIFACT_NOT_ACCEPTED"


@pytest.mark.parametrize("selector", [{}, {"instanceId": ""}, {"instanceId": "0"}, {"instanceId": "42"}])
def test_gameview_model_through_real_dispatcher_preserves_selector_intent(selector):
    from unittest.mock import Mock
    from upilot_mcp.dispatcher import CommandDispatcher

    class Transport:
        def is_ready(self):
            return True

        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future

        async def send_command(self, *, command_id, name, payload):
            self.payload = payload
            self.future.set_result({"type": "result", "payload": {"snapshotId": "test", "terminal": True}})

    transport = Transport()
    service = SnapshotDomainService()
    service.dispatcher = CommandDispatcher(transport, Mock())
    target = snapshot_tools._SnapshotTargetSchema.model_validate({"kind": "gameview", **selector}).model_dump()
    asyncio.run(service.snapshot_capture([target], wait_ms=0))
    wired = transport.payload["targets"][0]
    if selector.get("instanceId", "") == "":
        assert "instanceId" not in wired
    else:
        # The Unity selector guard must still see explicit zero/nonempty IDs.
        assert wired["instanceId"] == int(selector["instanceId"])
    assert target["instanceId"] == selector.get("instanceId", "")


def test_gameview_null_selector_remains_invalid():
    from pydantic import ValidationError
    with pytest.raises(ValidationError):
        snapshot_tools._SnapshotTargetSchema.model_validate({"kind": "gameview", "instanceId": None})

@pytest.mark.parametrize("route", ["native", "proxy"])
@pytest.mark.parametrize("selector", [
    {"exactName": "NamedCamera"},
    {"hierarchyPath": "Root/Camera"},
    {"cameraName": "LegacyCamera"},
    {"exactName": "NamedCamera", "hierarchyPath": "Root/Camera"},
    {},
], ids=["exact-name", "hierarchy", "legacy-name", "multiple", "none"])
@pytest.mark.parametrize("instance_id", [
    {}, {"instanceId": ""}, {"instanceId": "0"},
    {"instanceId": "568105584918842838"},
], ids=["omitted", "empty", "explicit-zero", "nonempty"])
def test_camera_public_model_through_real_dispatcher_preserves_selector_intent(
    monkeypatch, route, selector, instance_id,
):
    """Both public routes must preserve Camera selector intent before Unity."""
    from copy import deepcopy
    from unittest.mock import Mock
    from upilot_mcp.dispatcher import CommandDispatcher

    class Transport:
        def __init__(self):
            self.commands = []

        def is_ready(self):
            return True

        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future

        async def send_command(self, *, command_id, name, payload):
            self.commands.append((name, payload))
            self.future.set_result({"type": "result", "payload": {
                "snapshotId": "camera-intent", "terminal": True,
            }})

    transport = Transport()
    service = SnapshotDomainService()
    service.dispatcher = CommandDispatcher(transport, Mock())
    monkeypatch.setattr(snapshot_tools, "_get_facade", lambda: service)
    # Omit kind too: the real public model defaults it to Camera.
    arguments = {"targets": [{"targetId": "camera", **selector, **instance_id}], "waitMs": 0}
    original = deepcopy(arguments)
    if route == "native":
        result = asyncio.run(runtime.mcp._tool_manager.call_tool("unity_snapshot_capture", arguments))
        assert result.structuredContent["data"]["snapshotId"] == "camera-intent"
    else:
        result = asyncio.run(dispatch_public_tool(service, "unity_snapshot_capture", arguments))
        assert result.ok and result.data["snapshotId"] == "camera-intent"

    assert arguments == original
    assert len(transport.commands) == 1
    name, payload = transport.commands[0]
    assert name == "snapshot.start"
    wired = payload["targets"][0]
    assert wired["kind"] == "camera"
    if instance_id.get("instanceId", "") == "":
        assert "instanceId" not in wired
    else:
        # Keep explicit IDs, including zero and multi-selector requests, visible
        # to Unity's existing selector validation. Do not silently fix them.
        assert wired["instanceId"] == int(instance_id["instanceId"])
    for field in ("exactName", "hierarchyPath", "cameraName"):
        assert wired[field] == selector.get(field, "")


@pytest.mark.parametrize("route", ["native", "proxy"])
def test_camera_public_null_selector_rejected_without_dispatch(monkeypatch, route):
    service = _service()
    monkeypatch.setattr(snapshot_tools, "_get_facade", lambda: service)
    arguments = {"targets": [{"exactName": "NamedCamera", "instanceId": None}], "waitMs": 0}
    if route == "native":
        with pytest.raises(ToolError):
            asyncio.run(runtime.mcp._tool_manager.call_tool("unity_snapshot_capture", arguments))
    else:
        result = asyncio.run(dispatch_public_tool(service, "unity_snapshot_capture", arguments))
        assert not result.ok and result.error.code == "INVALID_TOOL_ARGUMENTS"
    assert service.dispatcher.calls == []


@pytest.mark.parametrize("opt_in", [False, True])
def test_native_tooltip_listing_is_explicitly_opt_in(opt_in):
    from upilot_mcp.domain.status_service import StatusDomainService
    service = StatusDomainService()
    service.dispatcher = _Dispatcher([ok("list", {"windows": []})])
    result = asyncio.run(service.editor_windows_list(include_native_tooltips=opt_in))
    assert result.ok
    assert service.dispatcher.calls == [("editor.windows.list", {"includeNativeTooltips": True} if opt_in else {})]


def test_tooltip_model_through_dispatcher_preserves_exact_identity_and_domain():
    from unittest.mock import Mock
    from upilot_mcp.dispatcher import CommandDispatcher

    class Transport:
        def is_ready(self): return True
        def register_pending(self, command_id):
            self.future = asyncio.get_running_loop().create_future()
            return self.future
        async def send_command(self, *, command_id, name, payload):
            self.payload = payload
            self.future.set_result({"type": "result", "payload": {"snapshotId": "tip", "terminal": True}})

    transport = Transport()
    service = SnapshotDomainService()
    service.dispatcher = CommandDispatcher(transport, Mock())
    target = snapshot_tools._SnapshotTargetSchema.model_validate({
        "kind": "tooltip", "instanceId": "18446744073709550001", "domainGeneration": "1234"}).model_dump()
    asyncio.run(service.snapshot_capture([target], wait_ms=0))
    wired = transport.payload["targets"][0]
    assert wired["instanceId"] == 18446744073709550001
    assert wired["domainGeneration"] == "1234"
    assert wired["kind"] == "tooltip"


@pytest.mark.parametrize("route", ["native", "proxy"])
@pytest.mark.parametrize("wait_ms", [0, -1])
@pytest.mark.parametrize("completion_policy", ["allOrNothing", "bestEffort"])
def test_sceneview_nonwaiting_start_exposes_exact_snapshot_for_cancellation(
    monkeypatch, route, wait_ms, completion_policy,
):
    """A non-waiting public start must expose the job, not a blocking Task handle."""
    started = {"snapshotId": "snapshot-own-scene", "requestKey": "own-request",
               "status": "running", "phase": "rendering", "terminal": False}
    service = _service(
        ok("start", started),
        ok("cancel", {**started, "cancelRequested": True}),
        ok("status", {**started, "status": "cancelled", "terminal": True,
                      "success": False, "cancelRequested": True}),
    )

    async def forbidden_task(*args, **kwargs):
        pytest.fail("waitMs<=0 must not hide snapshotId behind a SceneView Task")

    service.task_start = forbidden_task
    monkeypatch.setattr(snapshot_tools, "_get_facade", lambda: service)
    arguments = {
        "targets": [{"targetId": "own-scene", "kind": "sceneView", "instanceId": "42",
                     "domainGeneration": "domain-own", "requireContentRect": True}],
        "waitMs": wait_ms, "requestKey": "own-request",
        "completionPolicy": completion_policy,
    }

    async def run():
        if route == "native":
            response = await runtime.mcp._tool_manager.call_tool("unity_snapshot_capture", arguments)
            envelope = response.structuredContent
            assert envelope["ok"] is True
            data = envelope["data"]
        else:
            response = await dispatch_public_tool(service, "unity_snapshot_capture", arguments)
            assert response.ok is True
            data = response.data
        assert data["snapshotId"] == "snapshot-own-scene"
        assert data["requestKey"] == "own-request"
        assert data["terminal"] is False
        assert data["persistenceVerified"] is False
        assert "taskId" not in data
        assert len(service.dispatcher.calls) == 1
        cancelled = await service.snapshot_cancel(data["snapshotId"])
        assert cancelled.ok and cancelled.data["cancelRequested"]
        terminal = await service.snapshot_status(data["snapshotId"], "full")
        assert terminal.ok and terminal.data["status"] == "cancelled"
        assert terminal.data["success"] is False

    asyncio.run(run())
    assert [name for name, _ in service.dispatcher.calls] == [
        "snapshot.start", "snapshot.cancel", "snapshot.status",
    ]
    payload = service.dispatcher.calls[0][1]
    assert payload["targets"][0]["instanceId"] == "42"
    assert payload["targets"][0]["domainGeneration"] == "domain-own"
    assert payload["targets"][0]["requireContentRect"] is True
    assert payload["completionPolicy"] == completion_policy
    assert payload["requestKey"] == "own-request"
    assert started == {"snapshotId": "snapshot-own-scene", "requestKey": "own-request",
                       "status": "running", "phase": "rendering", "terminal": False}
