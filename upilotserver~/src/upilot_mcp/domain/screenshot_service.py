from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import json
import logging
import os
import shlex
import subprocess
import sys
import time
from dataclasses import asdict
from datetime import datetime
from pathlib import Path
from PIL import Image

from ..config import CONFIG, diagnose_client_configs
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import REGISTRY, REGISTRY_VERSION, dispatch_public_tool

logger = logging.getLogger("upilot.mcp")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="


def _normalize_reflection_parameters(parameters: list | None) -> list:
    if not parameters:
        return []
    normalized = []
    for value in parameters:
        if value is None:
            normalized.append(None)
        elif isinstance(value, (list, dict)):
            normalized.append(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
        else:
            normalized.append(str(value))
    return normalized


def _json_dumps_or_empty(value: object | None) -> str:
    if value is None:
        return ""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))

class ScreenshotDomainService:
    def _resolve_image_analysis_path(self, raw_path: str) -> Path | ToolResponse:
        request_id = new_id("req")
        session = self.server.session_manager.active
        root = Path(session.project_path).expanduser().resolve() if session and session.project_path else None
        if root is None:
            return fail(request_id, "PROJECT_PATH_UNAVAILABLE", "Unity project path is unavailable.", {})
        candidate = Path(raw_path)
        if not candidate.is_absolute():
            candidate = root / candidate
        try:
            resolved = candidate.expanduser().resolve()
            resolved.relative_to(root)
        except (OSError, ValueError):
            return fail(request_id, "IMAGE_PATH_OUTSIDE_PROJECT", "Image analysis only reads files under the Unity project.", {"path": raw_path})
        if not resolved.is_file() or resolved.suffix.lower() != ".png":
            return fail(request_id, "PNG_PATH_INVALID", "Path must be an existing PNG under the Unity project.", {"path": str(resolved)})
        return resolved

    async def screenshot_pixel_stats(
        self,
        path: str,
        region: dict | None = None,
        near_black_threshold: int = 16,
        alpha_threshold: int = 8,
        histogram_bins: int = 16,
    ) -> ToolResponse:
        request_id = new_id("req")
        target = self._resolve_image_analysis_path(path)
        if isinstance(target, ToolResponse):
            return target
        return await asyncio.to_thread(
            self._analyze_png,
            request_id,
            target,
            region or {},
            max(0, min(int(near_black_threshold), 255)),
            max(0, min(int(alpha_threshold), 255)),
            max(2, min(int(histogram_bins), 256)),
        )

    async def screenshot_compare(
        self,
        baseline_path: str,
        candidate_path: str,
        region: dict | None = None,
        channel_tolerance: int = 0,
        near_black_threshold: int = 16,
    ) -> ToolResponse:
        request_id = new_id("req")
        baseline = self._resolve_image_analysis_path(baseline_path)
        candidate = self._resolve_image_analysis_path(candidate_path)
        if isinstance(baseline, ToolResponse):
            return baseline
        if isinstance(candidate, ToolResponse):
            return candidate
        return await asyncio.to_thread(
            self._compare_png,
            request_id,
            baseline,
            candidate,
            region or {},
            max(0, min(int(channel_tolerance), 255)),
            max(0, min(int(near_black_threshold), 255)),
        )

    @staticmethod
    def _crop_rgba(image: Image.Image, region: dict) -> tuple[Image.Image, dict]:
        rgba = image.convert("RGBA")
        if not region:
            return rgba, {"x": 0, "y": 0, "width": rgba.width, "height": rgba.height}
        x = max(0, int(region.get("x", 0)))
        y = max(0, int(region.get("y", 0)))
        width = max(1, int(region.get("width", rgba.width - x)))
        height = max(1, int(region.get("height", rgba.height - y)))
        right = min(rgba.width, x + width)
        bottom = min(rgba.height, y + height)
        if x >= right or y >= bottom:
            raise ValueError("Region does not intersect the image.")
        return rgba.crop((x, y, right, bottom)), {"x": x, "y": y, "width": right - x, "height": bottom - y}

    @staticmethod
    def _analyze_png(request_id: str, target: Path, region: dict, near_black: int, alpha_threshold: int, bins: int) -> ToolResponse:
        try:
            raw = target.read_bytes()
            with Image.open(target) as source:
                full_size = {"width": source.width, "height": source.height}
                image, effective_region = ScreenshotDomainService._crop_rgba(source, region)
                pixels = list(image.get_flattened_data())
            total = max(1, len(pixels))
            near_black_count = sum(1 for r, g, b, a in pixels if a > alpha_threshold and max(r, g, b) <= near_black)
            transparent_count = sum(1 for _, _, _, a in pixels if a <= alpha_threshold)
            step = 256 / bins
            luminance_histogram = [0] * bins
            alpha_histogram = [0] * bins
            for r, g, b, a in pixels:
                luminance = int(0.2126 * r + 0.7152 * g + 0.0722 * b)
                luminance_histogram[min(bins - 1, int(luminance / step))] += 1
                alpha_histogram[min(bins - 1, int(a / step))] += 1
            return ok(request_id, {
                "path": str(target), "sha256": hashlib.sha256(raw).hexdigest(), "size": full_size,
                "region": effective_region, "pixelCount": len(pixels), "nearBlackThreshold": near_black,
                "nearBlackPixelCount": near_black_count, "nearBlackRatio": near_black_count / total,
                "alphaThreshold": alpha_threshold, "transparentPixelCount": transparent_count,
                "transparentRatio": transparent_count / total, "histogramBins": bins,
                "luminanceHistogram": luminance_histogram, "alphaHistogram": alpha_histogram,
            })
        except (OSError, ValueError) as ex:
            return fail(request_id, "PNG_ANALYSIS_FAILED", str(ex), {"path": str(target)})

    @staticmethod
    def _compare_png(request_id: str, baseline: Path, candidate: Path, region: dict, tolerance: int, near_black: int) -> ToolResponse:
        try:
            baseline_raw = baseline.read_bytes()
            candidate_raw = candidate.read_bytes()
            with Image.open(baseline) as first, Image.open(candidate) as second:
                if first.size != second.size:
                    return fail(request_id, "PNG_SIZE_MISMATCH", "Baseline and candidate dimensions differ.", {"baselineSize": first.size, "candidateSize": second.size})
                first_crop, effective_region = ScreenshotDomainService._crop_rgba(first, region)
                second_crop, _ = ScreenshotDomainService._crop_rgba(second, region)
                first_pixels = list(first_crop.get_flattened_data())
                second_pixels = list(second_crop.get_flattened_data())
            changed = 0
            total_abs = 0
            first_black = 0
            second_black = 0
            for a, b in zip(first_pixels, second_pixels):
                deltas = [abs(int(a[index]) - int(b[index])) for index in range(4)]
                total_abs += sum(deltas)
                if max(deltas) > tolerance:
                    changed += 1
                if a[3] > 8 and max(a[0], a[1], a[2]) <= near_black:
                    first_black += 1
                if b[3] > 8 and max(b[0], b[1], b[2]) <= near_black:
                    second_black += 1
            count = max(1, len(first_pixels))
            return ok(request_id, {
                "baselinePath": str(baseline), "candidatePath": str(candidate),
                "baselineSha256": hashlib.sha256(baseline_raw).hexdigest(), "candidateSha256": hashlib.sha256(candidate_raw).hexdigest(),
                "size": {"width": first_crop.width, "height": first_crop.height}, "region": effective_region,
                "channelTolerance": tolerance, "differentPixelCount": changed, "differentPixelRatio": changed / count,
                "meanAbsoluteChannelDifference": total_abs / (count * 4.0), "nearBlackThreshold": near_black,
                "baselineNearBlackRatio": first_black / count, "candidateNearBlackRatio": second_black / count,
                "nearBlackRatioDelta": (second_black - first_black) / count,
            })
        except (OSError, ValueError) as ex:
            return fail(request_id, "PNG_COMPARE_FAILED", str(ex), {"baselinePath": str(baseline), "candidatePath": str(candidate)})

    async def screenshot_game_view(
        self,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        if (format or "png").strip().lower() != "png":
            return fail(new_id("req"), "INVALID_SCREENSHOT_FORMAT", "Snapshot-backed screenshots support PNG only.", {"format": format})
        return await self._capture_snapshot_screenshot(
            "gameView",
            {"targetId": "game-view", "kind": "gameView", "targetDisplay": 0, "width": width, "height": height},
        )

    async def screenshot_scene_view(
        self,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        if (format or "png").strip().lower() != "png":
            return fail(new_id("req"), "INVALID_SCREENSHOT_FORMAT", "Snapshot-backed screenshots support PNG only.", {"format": format})
        resolved = await self._resolve_snapshot_window("sceneView", type_filter="UnityEditor.SceneView")
        if isinstance(resolved, ToolResponse):
            return resolved
        return await self._capture_snapshot_screenshot(
            "sceneView",
            {"targetId": "scene-view", "kind": "sceneView", "instanceId": str(resolved["instanceId"]), "width": width, "height": height},
        )

    async def screenshot_camera(
        self,
        camera_name: str,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        request_id = new_id("req")
        if (format or "png").strip().lower() != "png":
            return fail(request_id, "INVALID_SCREENSHOT_FORMAT", "Snapshot-backed screenshots support PNG only.", {"format": format})
        if not str(camera_name or "").strip():
            return fail(request_id, "CAMERA_NAME_REQUIRED", "Snapshot-backed camera screenshots require an exact cameraName; use unity_camera_list for stable instanceId capture.", {})
        return await self._capture_snapshot_screenshot(
            "camera",
            {"targetId": "camera", "kind": "camera", "exactName": camera_name, "width": width, "height": height},
        )

    async def _resolve_snapshot_window(
        self,
        source: str,
        *,
        type_filter: str = "",
        title_filter: str = "",
    ) -> dict | ToolResponse:
        request_id = new_id("req")
        listed = await self.editor_windows_list(type_filter=type_filter, title_filter=title_filter)
        if not listed.ok or not isinstance(listed.data, dict):
            return listed
        windows = [item for item in listed.data.get("windows", []) if isinstance(item, dict)]
        if source == "sceneView":
            windows = [item for item in windows if str(item.get("fullTypeName") or "") == "UnityEditor.SceneView"]
        if not windows:
            return fail(request_id, "WINDOW_NOT_FOUND", f"No {source} window matched the request.", {"typeFilter": type_filter, "titleFilter": title_filter})
        focused = [item for item in windows if item.get("hasFocus") is True]
        if len(focused) == 1:
            return focused[0]
        if len(windows) == 1:
            return windows[0]
        exact = [item for item in windows if str(item.get("title") or "").casefold() == str(title_filter or "").casefold()]
        if len(exact) == 1:
            return exact[0]
        return fail(request_id, "AMBIGUOUS_EDITOR_WINDOW", "Multiple Unity EditorWindow instances matched; use unity_snapshot_capture with an exact instanceId.", {"matches": windows})

    async def _capture_snapshot_screenshot(self, source: str, target: dict) -> ToolResponse:
        request_id = new_id("req")
        captured = await self.snapshot_capture(
            [target],
            channels=["color"],
            sync_mode="sameFrame",
            completion_policy="allOrNothing",
            capture_policy={
                "requireVerifiedPixels": True,
                "allowFallback": False,
                "allowOcclusionSensitive": False,
                "allowStaleFrame": False,
                "maxStaleFrameAgeMs": 0,
            },
            wait_ms=10000,
        )
        if not captured.ok or not isinstance(captured.data, dict):
            return captured
        snapshot = captured.data
        artifacts = [
            item for item in snapshot.get("artifacts", [])
            if isinstance(item, dict) and item.get("role") == "color" and item.get("acceptedAsEvidence") is True
        ]
        if len(artifacts) != 1:
            if snapshot.get("terminal") is not True:
                return ok(request_id, {"source": source, "snapshot": snapshot})
            return fail(request_id, "SNAPSHOT_SCREENSHOT_ARTIFACT_MISSING", "Snapshot completed without exactly one accepted color artifact.", {"snapshot": snapshot})
        artifact = artifacts[0]
        root = self._active_project_root()
        if root is None:
            return fail(request_id, "PROJECT_PATH_UNAVAILABLE", "Unity project path is unavailable.", {"snapshot": snapshot})
        path = (root / str(artifact.get("path") or "")).resolve()
        try:
            path.relative_to(root)
            raw = path.read_bytes()
        except (OSError, ValueError) as ex:
            return fail(request_id, "SNAPSHOT_SCREENSHOT_READ_FAILED", str(ex), {"snapshot": snapshot, "artifact": artifact})
        actual_sha = hashlib.sha256(raw).hexdigest()
        if actual_sha != str(artifact.get("sha256") or ""):
            return fail(request_id, "SNAPSHOT_ARTIFACT_HASH_MISMATCH", "Snapshot color artifact hash changed before wrapper response.", {"snapshot": snapshot, "artifact": artifact, "actualSha256": actual_sha})
        target_result = next((item for item in snapshot.get("targets", []) if isinstance(item, dict) and item.get("success") is True), {})
        provenance = target_result.get("provenance") if isinstance(target_result, dict) else {}
        provenance = provenance if isinstance(provenance, dict) else {}
        return ok(request_id, {
            "snapshot": snapshot,
            "artifact": artifact,
            "imageData": base64.b64encode(raw).decode("ascii"),
            "width": artifact.get("width", 0),
            "height": artifact.get("height", 0),
            "format": "png",
            "source": source,
            "degraded": bool(provenance.get("degraded", False)),
            "degradeReason": provenance.get("degradeReason", ""),
            **provenance,
        })

    async def screenshot_save(
        self,
        path: str = "",
        source: str = "gameView",
        overwrite: bool = False,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
        camera_name: str = "",
        window_title: str = "Game",
        allow_outside_project: bool = False,
        degrade: str = "none",
        fallback_sources: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        image_format = (format or "png").strip().lower()
        if image_format != "png":
            return fail(
                request_id,
                "INVALID_SCREENSHOT_FORMAT",
                "保存型截图工具当前仅支持 format=png。",
                {"format": format, "path": path},
            )

        source_key = (source or "gameView").strip().lower()
        normalized_source = self._normalize_screenshot_source(source_key)
        if not normalized_source:
            return fail(
                request_id,
                "INVALID_SCREENSHOT_SOURCE",
                "source 仅支持 gameView、sceneView、camera、editorWindow。",
                {"source": source, "path": path},
            )
        if normalized_source == "camera" and not camera_name:
            return fail(
                request_id,
                "CAMERA_NAME_REQUIRED",
                "source=camera 时必须提供 cameraName。",
                {"path": path},
            )

        target_result = self._resolve_screenshot_save_path(
            path, normalized_source, allow_outside_project
        )
        if isinstance(target_result, ToolResponse):
            return target_result
        target_path = target_result
        if target_path.exists() and not overwrite:
            return fail(
                request_id,
                "FILE_EXISTS",
                "目标截图文件已存在，若需要覆盖请传 overwrite=true。",
                {"path": str(target_path)},
            )

        if fallback_sources:
            return fail(
                request_id,
                "SNAPSHOT_FALLBACKS_UNSUPPORTED",
                "Snapshot-backed screenshot wrappers do not silently fall back to another visual source.",
                {"fallbackSources": fallback_sources},
            )
        if normalized_source == "gameView":
            captured = await self.screenshot_game_view(width, height, image_format, quality)
        elif normalized_source == "sceneView":
            captured = await self.screenshot_scene_view(width, height, image_format, quality)
        elif normalized_source == "camera":
            captured = await self.screenshot_camera(camera_name, width, height, image_format, quality)
        else:
            captured = await self.screenshot_editor_window(window_title, degrade="none")
        if not captured.ok or not isinstance(captured.data, dict):
            return captured
        data = captured.data
        image_data = str(data.get("imageData") or "")
        if not image_data:
            return fail(request_id, "SNAPSHOT_SCREENSHOT_ARTIFACT_MISSING", "Snapshot wrapper did not return image bytes.", {"snapshot": data.get("snapshot")})
        try:
            raw = self._decode_screenshot_image_data(image_data)
            target_path.parent.mkdir(parents=True, exist_ok=True)
            temporary = target_path.with_name(f".{target_path.name}.{os.getpid()}.tmp")
            try:
                temporary.write_bytes(raw)
                os.replace(temporary, target_path)
            finally:
                temporary.unlink(missing_ok=True)
        except (OSError, ValueError, binascii.Error) as ex:
            return fail(request_id, "SCREENSHOT_WRITE_FAILED", str(ex), {"path": str(target_path), "snapshot": data.get("snapshot")})
        return ok(request_id, {
            **data,
            "path": str(target_path),
            "source": normalized_source,
            "bytes": len(raw),
            "sha256": hashlib.sha256(raw).hexdigest(),
            "overwritten": overwrite,
            "requestedSource": normalized_source,
            "savedBy": "snapshot_wrapper",
        })

    @staticmethod
    def _normalize_screenshot_source(source_key: str) -> str:
        if source_key in ("gameview", "game_view", "game"):
            return "gameView"
        if source_key in ("sceneview", "scene_view", "scene"):
            return "sceneView"
        if source_key == "camera":
            return "camera"
        if source_key in ("editorwindow", "editor_window", "window"):
            return "editorWindow"
        return ""

    @staticmethod
    def _is_command_not_found(resp: ToolResponse) -> bool:
        if resp.ok or not resp.error:
            return False
        return (resp.error.code or "").upper() == "COMMAND_NOT_FOUND"

    def _resolve_screenshot_save_path(
        self, path: str, normalized_source: str, allow_outside_project: bool
    ) -> Path | ToolResponse:
        request_id = new_id("req")
        raw_path = (path or "").strip()
        if not raw_path:
            project_root = self._active_project_root()
            if not project_root:
                return fail(
                    request_id,
                    "PROJECT_PATH_UNAVAILABLE",
                    "path 为空时需要当前 Unity 工程路径来生成默认截图保存路径。",
                    {},
                )

            timestamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S-%f")[:-3]
            safe_source = "".join(
                ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in normalized_source
            )
            target_path = project_root / "Log" / "UPilotScreenshots" / f"{timestamp}_{safe_source}.png"
            return target_path.expanduser().resolve()

        target_path = Path(raw_path)
        if not target_path.is_absolute():
            project_root = self._active_project_root()
            target_path = (project_root / target_path) if project_root else target_path
        target_path = target_path.expanduser().resolve()

        if target_path.suffix.lower() != ".png":
            return fail(
                request_id,
                "INVALID_SCREENSHOT_EXTENSION",
                "当前保存型截图工具仅允许写入 .png 文件。",
                {"path": str(target_path)},
            )

        if allow_outside_project:
            return target_path

        project_root = self._active_project_root()
        if not project_root:
            return fail(
                request_id,
                "PROJECT_PATH_UNAVAILABLE",
                "当前没有可用 Unity 工程路径，无法校验截图保存目录。",
                {"path": str(target_path)},
            )

        try:
            target_path.relative_to(project_root)
        except ValueError:
            return fail(
                request_id,
                "SCREENSHOT_PATH_OUTSIDE_PROJECT",
                "默认只允许将截图保存到当前 Unity 工程目录内。",
                {"path": str(target_path), "projectRoot": str(project_root)},
            )

        return target_path

    @staticmethod
    def _decode_screenshot_image_data(image_data: str) -> bytes:
        value = image_data.strip()
        if "," in value and value.lower().startswith("data:"):
            value = value.split(",", 1)[1]
        return base64.b64decode(value, validate=True)

    @staticmethod
    def _screenshot_degrade_mode(explicit: str | None) -> str:
        v = (
            (explicit or getenv("UPILOT_SCREENSHOT_DEGRADE", "auto"))
            .strip()
            .lower()
        )
        if v not in ("none", "auto", "scene", "minimal"):
            return "auto"
        return v

    @staticmethod
    def _response_has_screenshot_payload(resp: ToolResponse) -> bool:
        if not resp.ok or not resp.data:
            return False
        img = resp.data.get("imageData") or resp.data.get("image_data")
        return bool(img and len(str(img)) > 48)

    @staticmethod
    def _screenshot_error_detail(resp: ToolResponse) -> tuple[str, str]:
        if resp.error:
            return resp.error.code or "", resp.error.message or resp.error.code or ""
        return "", "empty_or_missing_imageData"

    async def screenshot_editor_window(
        self,
        window_title: str = "upilot",
        degrade: str | None = None,
    ) -> ToolResponse:
        """Thin compatibility wrapper over strict EditorWindow Snapshot capture."""
        request_id = new_id("req")
        mode = self._screenshot_degrade_mode(degrade)
        if mode != "none":
            logger.info("Ignoring legacy screenshot degrade=%s; Snapshot wrappers require explicit source identity.", mode)
        resolved = await self._resolve_snapshot_window(
            "editorWindow",
            title_filter=window_title,
        )
        if isinstance(resolved, ToolResponse):
            return resolved
        if str(resolved.get("fullTypeName") or "") == "UnityEditor.SceneView":
            return fail(
                request_id,
                "EDITORWINDOW_KIND_MISMATCH",
                "Use unity_screenshot_scene_view for SceneView so repaint and Handles evidence is verified.",
                {"window": resolved},
            )
        return await self._capture_snapshot_screenshot(
            "editorWindow",
            {
                "targetId": "editor-window",
                "kind": "editorWindow",
                "instanceId": str(resolved["instanceId"]),
                "width": int(resolved.get("width") or 1),
                "height": int(resolved.get("height") or 1),
            },
        )
