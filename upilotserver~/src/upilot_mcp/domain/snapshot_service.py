from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import os
import re
import time
from pathlib import Path
from typing import Any

from PIL import Image

from ..protocol import new_id
from ..responses import fail, ok
from ..operation_context import TASK_TOOL


_TERMINAL_SNAPSHOT_STATES = {"completed", "partial", "failed", "cancelled"}


class SnapshotDomainService:
    """Facade methods for the versioned Unity Snapshot job contract."""

    async def _bounded_snapshot_task(self, tool_name: str, arguments: dict, wait_ms: int = 4500):
        deadline = time.monotonic() + min(max(wait_ms, 0), 4500) / 1000
        started = await self.task_start("SceneView capture", tool_name, arguments, timeout_s=45, retry_count=0)
        if not started.ok:
            return started
        task_id = started.data["taskId"]
        task_deadline = int(started.data.get("startedAt") or time.time() * 1000) + 45000
        while time.monotonic() < deadline:
            status = await self.task_status(task_id, detail_level="full")
            data = status.data or {}
            if data.get("terminal"):
                if data.get("status") == "completed":
                    return ok(started.request_id, (data.get("result") or {}).get("result") or {})
                return fail(started.request_id, "SNAPSHOT_TASK_FAILED", str(data.get("error") or ""),
                            {"taskId": task_id, "terminal": True, "cause": data.get("error")})
            await asyncio.sleep(min(0.05, max(0, deadline - time.monotonic())))
        return ok(started.request_id, {"taskId": task_id, "terminal": False, "repaintPending": True,
                  "waitWindowElapsed": True, "deadline": task_deadline,
                  "deadlineSource": "task-timeout", "repaintDeadline": None,
                  "lastRepaintAt": None, "nextAction": "Poll unity_task_status with this taskId; do not start another capture."})

    async def camera_list(self):
        return await self.dispatcher.call(new_id("req"), "snapshot.cameraList", {})

    async def snapshot_capture(
        self,
        targets: list[dict[str, Any]],
        *,
        channels: list[str] | None = None,
        sync_mode: str = "sameFrame",
        completion_policy: str = "allOrNothing",
        capture_policy: dict[str, Any] | None = None,
        output_directory: str = "",
        wait_ms: int = 5000,
        request_key: str = "",
    ):
        request_id = new_id("req")
        if not isinstance(targets, list) or not targets:
            return fail(
                request_id,
                "SNAPSHOT_TARGETS_REQUIRED",
                "targets must contain at least one Snapshot target.",
                {},
            )
        if len(targets) > 16:
            return fail(
                request_id,
                "SNAPSHOT_TARGET_LIMIT_EXCEEDED",
                "A Snapshot may contain at most 16 targets.",
                {"targetCount": len(targets), "maxTargets": 16},
            )

        if any(str(t.get("kind", "")).lower() == "sceneview" for t in targets) and not TASK_TOOL.get():
            return await self._bounded_snapshot_task("unity_snapshot_capture", {
                "targets": targets, "channels": channels, "syncMode": sync_mode, "completionPolicy": completion_policy,
                "capturePolicy": capture_policy, "outputDirectory": output_directory, "waitMs": 30000,
                "requestKey": request_key,
            }, wait_ms)

        started = await self.dispatcher.call(
            new_id("req"),
            "snapshot.start",
            {
                "targets": targets,
                "channels": channels or ["color"],
                "syncMode": sync_mode,
                "completionPolicy": completion_policy,
                "capturePolicy": capture_policy
                or {
                    "requireVerifiedPixels": True,
                    "allowFallback": False,
                    "allowOcclusionSensitive": False,
                    "allowStaleFrame": False,
                    "maxStaleFrameAgeMs": 0,
                },
                "outputDirectory": output_directory,
                "requestKey": request_key,
            },
        )
        if not started.ok or not isinstance(started.data, dict):
            return started

        snapshot_id = str(started.data.get("snapshotId") or "")
        if not snapshot_id or wait_ms <= 0:
            return started

        deadline = time.monotonic() + min(max(int(wait_ms), 0), 30000) / 1000.0
        current = started
        while time.monotonic() < deadline:
            status = str((current.data or {}).get("status") or "").lower()
            if status in _TERMINAL_SNAPSHOT_STATES:
                return current
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            await asyncio.sleep(min(0.05, remaining))
            if time.monotonic() >= deadline:
                break
            current = await self.dispatcher.call(
                new_id("req"), "snapshot.status", {"snapshotId": snapshot_id, "detailLevel": "standard"}
            )
            if not current.ok:
                return current
        data = dict(current.data or {})
        data["waitWindowElapsed"] = True
        data["terminal"] = False
        data.setdefault("snapshotId", snapshot_id)
        current.data = data
        return current

    async def snapshot_status(self, snapshot_id: str, detail_level: str = "summary"):
        return await self.dispatcher.call(
            new_id("req"),
            "snapshot.status",
            {"snapshotId": snapshot_id, "detailLevel": detail_level},
        )

    async def snapshot_cancel(self, snapshot_id: str):
        return await self.dispatcher.call(
            new_id("req"), "snapshot.cancel", {"snapshotId": snapshot_id}
        )

    async def snapshot_collect_artifacts(self, snapshot_id: str):
        return await self.dispatcher.call(
            new_id("req"), "snapshot.collect", {"snapshotId": snapshot_id}
        )

    def _snapshot_project_root(self) -> Path | None:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return None
        try:
            return Path(session.project_path).expanduser().resolve()
        except OSError:
            return None

    @staticmethod
    def _normalize_baseline_key(raw_key: str) -> str | None:
        value = str(raw_key or "").strip().replace("\\", "/").strip("/")
        if not value or value.endswith(".png"):
            value = value[:-4] if value.lower().endswith(".png") else value
        if not value or len(value) > 240:
            return None
        parts = value.split("/")
        if any(part in {"", ".", ".."} or not re.fullmatch(r"[A-Za-z0-9._-]+", part) for part in parts):
            return None
        return "/".join(parts)

    def _baseline_paths(self, baseline_key: str) -> tuple[Path, Path, Path] | None:
        root = self._snapshot_project_root()
        key = self._normalize_baseline_key(baseline_key)
        if root is None or key is None:
            return None
        baseline_root = (root / ".upilot" / "snapshots" / "baselines").resolve()
        target = (baseline_root / f"{key}.png").resolve()
        try:
            target.relative_to(baseline_root)
        except ValueError:
            return None
        return baseline_root, target, target.with_suffix(".json")

    @staticmethod
    def _project_relative(root: Path, path: Path) -> str:
        return path.resolve().relative_to(root.resolve()).as_posix()

    async def _resolve_snapshot_png(
        self,
        snapshot_id: str,
        target_id: str,
        role: str,
    ) -> tuple[Path, dict[str, Any], dict[str, Any]] | Any:
        request_id = new_id("req")
        root = self._snapshot_project_root()
        if root is None:
            return fail(request_id, "PROJECT_PATH_UNAVAILABLE", "Unity project path is unavailable.", {})
        collected = await self.snapshot_collect_artifacts(snapshot_id)
        if not collected.ok or not isinstance(collected.data, dict):
            return collected
        matches = [
            item
            for item in collected.data.get("artifacts", [])
            if isinstance(item, dict)
            and str(item.get("targetId") or "") == str(target_id or "")
            and str(item.get("role") or "").lower() == str(role or "color").lower()
        ]
        if len(matches) != 1:
            return fail(
                request_id,
                "SNAPSHOT_ARTIFACT_NOT_UNIQUE",
                "Exactly one Snapshot artifact must match targetId and role.",
                {"snapshotId": snapshot_id, "targetId": target_id, "role": role, "matchCount": len(matches)},
            )
        artifact = matches[0]
        if artifact.get("acceptedAsEvidence") is not True:
            return fail(
                request_id,
                "SNAPSHOT_ARTIFACT_NOT_ACCEPTED",
                "Baseline operations require acceptedAsEvidence=true.",
                {"snapshotId": snapshot_id, "artifact": artifact},
            )
        if str(artifact.get("mimeType") or "").lower() != "image/png":
            return fail(request_id, "SNAPSHOT_BASELINE_PNG_REQUIRED", "Snapshot baselines currently require PNG artifacts.", {"artifact": artifact})
        candidate = (root / str(artifact.get("path") or "")).resolve()
        try:
            candidate.relative_to(root)
        except ValueError:
            return fail(request_id, "SNAPSHOT_ARTIFACT_PATH_INVALID", "Snapshot artifact is outside the Unity project.", {"path": str(candidate)})
        if not candidate.is_file():
            return fail(request_id, "SNAPSHOT_ARTIFACT_MISSING", "Snapshot artifact file does not exist.", {"path": str(candidate)})
        raw = candidate.read_bytes()
        actual_sha = hashlib.sha256(raw).hexdigest()
        expected_sha = str(artifact.get("sha256") or "")
        if not expected_sha or actual_sha != expected_sha:
            return fail(
                request_id,
                "SNAPSHOT_ARTIFACT_HASH_MISMATCH",
                "Snapshot artifact bytes no longer match the manifest hash.",
                {"path": str(candidate), "expectedSha256": expected_sha, "actualSha256": actual_sha},
            )
        return candidate, artifact, collected.data

    async def snapshot_baseline_list(self, prefix: str = ""):
        request_id = new_id("req")
        root = self._snapshot_project_root()
        if root is None:
            return fail(request_id, "PROJECT_PATH_UNAVAILABLE", "Unity project path is unavailable.", {})
        baseline_root = (root / ".upilot" / "snapshots" / "baselines").resolve()
        normalized_prefix = str(prefix or "").strip().replace("\\", "/").strip("/")
        items: list[dict[str, Any]] = []
        if baseline_root.is_dir():
            for path in sorted(baseline_root.rglob("*.png")):
                key = path.relative_to(baseline_root).with_suffix("").as_posix()
                if normalized_prefix and not key.startswith(normalized_prefix):
                    continue
                raw = path.read_bytes()
                with Image.open(path) as image:
                    size = {"width": image.width, "height": image.height}
                items.append({
                    "baselineKey": key,
                    "path": self._project_relative(root, path),
                    "bytes": len(raw),
                    "sha256": hashlib.sha256(raw).hexdigest(),
                    "size": size,
                })
        return ok(request_id, {
            "baselineRoot": self._project_relative(root, baseline_root),
            "prefix": normalized_prefix,
            "count": len(items),
            "baselines": items,
        })

    async def snapshot_baseline_compare(
        self,
        baseline_key: str,
        snapshot_id: str,
        target_id: str,
        *,
        role: str = "color",
        channel_tolerance: int = 0,
        max_different_pixel_ratio: float = 0.0,
        min_ssim: float = 1.0,
        output_directory: str = "",
    ):
        request_id = new_id("req")
        paths = self._baseline_paths(baseline_key)
        if paths is None:
            return fail(request_id, "SNAPSHOT_BASELINE_KEY_INVALID", "baselineKey must contain only safe path segments and omit the .png suffix.", {"baselineKey": baseline_key})
        _, baseline, _ = paths
        if not baseline.is_file():
            return fail(request_id, "SNAPSHOT_BASELINE_NOT_FOUND", "Snapshot baseline does not exist.", {"baselineKey": baseline_key})
        resolved = await self._resolve_snapshot_png(snapshot_id, target_id, role)
        if not isinstance(resolved, tuple):
            return resolved
        candidate, artifact, job = resolved
        root = self._snapshot_project_root()
        assert root is not None
        if output_directory:
            output_root = (root / output_directory).resolve()
            try:
                output_root.relative_to(root)
            except ValueError:
                return fail(request_id, "SNAPSHOT_COMPARISON_PATH_OUTSIDE_PROJECT", "outputDirectory must stay under the Unity project.", {"outputDirectory": output_directory})
        else:
            safe_key = (self._normalize_baseline_key(baseline_key) or "baseline").replace("/", "_")
            output_root = root / ".upilot" / "snapshots" / "comparisons" / f"{int(time.time() * 1000)}-{safe_key}"
        tolerance = max(0, min(int(channel_tolerance), 255))
        ratio_threshold = max(0.0, min(float(max_different_pixel_ratio), 1.0))
        ssim_threshold = max(-1.0, min(float(min_ssim), 1.0))
        return await asyncio.to_thread(
            self._compare_baseline_png,
            request_id,
            root,
            baseline,
            candidate,
            artifact,
            job,
            output_root,
            tolerance,
            ratio_threshold,
            ssim_threshold,
            self._normalize_baseline_key(baseline_key) or "",
        )

    @staticmethod
    def _tile_ssim(first: Image.Image, second: Image.Image, tile_size: int = 8) -> float:
        first_values = list(first.convert("L").get_flattened_data())
        second_values = list(second.convert("L").get_flattened_data())
        width, height = first.size
        c1 = (0.01 * 255.0) ** 2
        c2 = (0.03 * 255.0) ** 2
        weighted = 0.0
        total = 0
        for top in range(0, height, tile_size):
            for left in range(0, width, tile_size):
                a: list[float] = []
                b: list[float] = []
                for y in range(top, min(top + tile_size, height)):
                    start = y * width + left
                    end = y * width + min(left + tile_size, width)
                    a.extend(first_values[start:end])
                    b.extend(second_values[start:end])
                count = len(a)
                if not count:
                    continue
                mean_a = sum(a) / count
                mean_b = sum(b) / count
                var_a = sum((value - mean_a) ** 2 for value in a) / count
                var_b = sum((value - mean_b) ** 2 for value in b) / count
                covariance = sum((a[index] - mean_a) * (b[index] - mean_b) for index in range(count)) / count
                numerator = (2 * mean_a * mean_b + c1) * (2 * covariance + c2)
                denominator = (mean_a * mean_a + mean_b * mean_b + c1) * (var_a + var_b + c2)
                weighted += (numerator / denominator if denominator else 1.0) * count
                total += count
        return weighted / total if total else 1.0

    @staticmethod
    def _compare_baseline_png(
        request_id: str,
        project_root: Path,
        baseline: Path,
        candidate: Path,
        artifact: dict[str, Any],
        job: dict[str, Any],
        output_root: Path,
        tolerance: int,
        ratio_threshold: float,
        ssim_threshold: float,
        baseline_key: str,
    ):
        try:
            baseline_raw = baseline.read_bytes()
            candidate_raw = candidate.read_bytes()
            with Image.open(baseline) as baseline_image, Image.open(candidate) as candidate_image:
                first = baseline_image.convert("RGBA")
                second = candidate_image.convert("RGBA")
                if first.size != second.size:
                    return fail(request_id, "SNAPSHOT_BASELINE_SIZE_MISMATCH", "Baseline and candidate dimensions differ.", {
                        "baselineSize": {"width": first.width, "height": first.height},
                        "candidateSize": {"width": second.width, "height": second.height},
                    })
                first_pixels = list(first.get_flattened_data())
                second_pixels = list(second.get_flattened_data())
                different = 0
                diff_pixels: list[tuple[int, int, int, int]] = []
                heat_pixels: list[tuple[int, int, int, int]] = []
                total_abs = 0
                max_delta = 0
                for before, after in zip(first_pixels, second_pixels):
                    deltas = tuple(abs(int(before[index]) - int(after[index])) for index in range(4))
                    peak = max(deltas)
                    max_delta = max(max_delta, peak)
                    total_abs += sum(deltas)
                    if peak > tolerance:
                        different += 1
                    diff_pixels.append((deltas[0], deltas[1], deltas[2], 255))
                    heat_pixels.append((peak, min(255, peak * 4), 0, 255))
                pixel_count = max(1, len(first_pixels))
                ratio = different / pixel_count
                ssim = SnapshotDomainService._tile_ssim(first, second)
                output_root.mkdir(parents=True, exist_ok=True)
                diff_path = output_root / "diff.png"
                heatmap_path = output_root / "heatmap.png"
                diff = Image.new("RGBA", first.size)
                diff.putdata(diff_pixels)
                diff.save(diff_path, format="PNG")
                heatmap = Image.new("RGBA", first.size)
                heatmap.putdata(heat_pixels)
                heatmap.save(heatmap_path, format="PNG")
            diff_raw = diff_path.read_bytes()
            heat_raw = heatmap_path.read_bytes()
            passed = ratio <= ratio_threshold and ssim >= ssim_threshold
            return ok(request_id, {
                "baselineKey": baseline_key,
                "passed": passed,
                "thresholds": {
                    "channelTolerance": tolerance,
                    "maxDifferentPixelRatio": ratio_threshold,
                    "minSsim": ssim_threshold,
                },
                "metrics": {
                    "pixelCount": len(first_pixels),
                    "differentPixelCount": different,
                    "differentPixelRatio": ratio,
                    "meanAbsoluteChannelDifference": total_abs / (pixel_count * 4.0),
                    "maxChannelDifference": max_delta,
                    "ssim": ssim,
                    "ssimMethod": "8x8-nonoverlapping-luminance",
                },
                "baseline": {
                    "path": SnapshotDomainService._project_relative(project_root, baseline),
                    "sha256": hashlib.sha256(baseline_raw).hexdigest(),
                },
                "candidate": {
                    "snapshotId": str(job.get("snapshotId") or ""),
                    "artifact": artifact,
                    "path": SnapshotDomainService._project_relative(project_root, candidate),
                    "sha256": hashlib.sha256(candidate_raw).hexdigest(),
                },
                "artifacts": [
                    {"role": "diff", "path": SnapshotDomainService._project_relative(project_root, diff_path), "bytes": len(diff_raw), "sha256": hashlib.sha256(diff_raw).hexdigest(), "acceptedAsEvidence": False},
                    {"role": "heatmap", "path": SnapshotDomainService._project_relative(project_root, heatmap_path), "bytes": len(heat_raw), "sha256": hashlib.sha256(heat_raw).hexdigest(), "acceptedAsEvidence": False},
                ],
            })
        except (OSError, ValueError) as ex:
            return fail(request_id, "SNAPSHOT_BASELINE_COMPARE_FAILED", str(ex), {"baselineKey": baseline_key})

    async def snapshot_baseline_update(
        self,
        baseline_key: str,
        snapshot_id: str,
        target_id: str,
        *,
        role: str = "color",
        dry_run: bool = True,
        confirm_token: str = "",
    ):
        request_id = new_id("req")
        paths = self._baseline_paths(baseline_key)
        if paths is None:
            return fail(request_id, "SNAPSHOT_BASELINE_KEY_INVALID", "baselineKey must contain only safe path segments and omit the .png suffix.", {"baselineKey": baseline_key})
        baseline_root, target, metadata_path = paths
        resolved = await self._resolve_snapshot_png(snapshot_id, target_id, role)
        if not isinstance(resolved, tuple):
            return resolved
        candidate, artifact, job = resolved
        root = self._snapshot_project_root()
        assert root is not None
        candidate_raw = candidate.read_bytes()
        candidate_sha = hashlib.sha256(candidate_raw).hexdigest()
        existing_raw = target.read_bytes() if target.is_file() else b""
        existing_sha = hashlib.sha256(existing_raw).hexdigest() if existing_raw else ""
        token_document = {
            "schemaVersion": 1,
            "baselineKey": self._normalize_baseline_key(baseline_key),
            "targetPath": self._project_relative(root, target),
            "candidateSha256": candidate_sha,
            "existingSha256": existing_sha,
            "snapshotId": snapshot_id,
            "targetId": target_id,
            "role": role,
        }
        expected_token = hashlib.sha256(json.dumps(token_document, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()
        preview = {
            "dryRun": dry_run,
            "baselineKey": token_document["baselineKey"],
            "operation": "replace" if existing_sha else "create",
            "path": token_document["targetPath"],
            "existingSha256": existing_sha,
            "candidateSha256": candidate_sha,
            "candidate": {"snapshotId": snapshot_id, "artifact": artifact},
            "confirmToken": expected_token,
        }
        if dry_run:
            return ok(request_id, preview)
        if not confirm_token or not hmac.compare_digest(confirm_token, expected_token):
            return fail(request_id, "SNAPSHOT_BASELINE_CONFIRM_TOKEN_INVALID", "confirmToken does not match the current baseline and Snapshot artifact.", preview)
        baseline_root.mkdir(parents=True, exist_ok=True)
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_name(f".{target.name}.{os.getpid()}.tmp")
        metadata_temporary = metadata_path.with_name(f".{metadata_path.name}.{os.getpid()}.tmp")
        try:
            temporary.write_bytes(candidate_raw)
            os.replace(temporary, target)
            metadata = {
                "snapshotBaselineSchemaVersion": 1,
                "baselineKey": token_document["baselineKey"],
                "path": token_document["targetPath"],
                "sha256": candidate_sha,
                "bytes": len(candidate_raw),
                "sourceSnapshotId": snapshot_id,
                "sourceTargetId": target_id,
                "sourceRole": role,
                "sourceArtifactId": artifact.get("artifactId"),
                "updatedAtUtcMs": int(time.time() * 1000),
            }
            metadata_temporary.write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            os.replace(metadata_temporary, metadata_path)
        finally:
            temporary.unlink(missing_ok=True)
            metadata_temporary.unlink(missing_ok=True)
        return ok(request_id, {
            **preview,
            "dryRun": False,
            "confirmTokenAccepted": True,
            "bytes": len(candidate_raw),
            "sha256": candidate_sha,
            "metadataPath": self._project_relative(root, metadata_path),
        })
