from __future__ import annotations

import asyncio
import hashlib
from types import SimpleNamespace

from PIL import Image

from upilot_mcp.domain.snapshot_service import SnapshotDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY


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
