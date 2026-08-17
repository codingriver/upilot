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
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "screenshot.gameView",
            {
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
        )

    async def screenshot_scene_view(
        self,
        width: int = 1280,
        height: int = 720,
        format: str = "png",
        quality: int = 75,
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "screenshot.sceneView",
            {
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
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
        return await self.dispatcher.call(
            request_id,
            "screenshot.camera",
            {
                "cameraName": camera_name,
                "width": width,
                "height": height,
                "format": format,
                "quality": quality,
            },
        )

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

        bridge_save = await self.dispatcher.call(
            new_id("req"),
            "screenshot.save",
            {
                "path": str(target_path),
                "source": normalized_source,
                "overwrite": overwrite,
                "width": width,
                "height": height,
                "format": image_format,
                "quality": quality,
                "cameraName": camera_name,
                "windowTitle": window_title,
                "allowOutsideProject": allow_outside_project,
                "degrade": degrade,
                "fallbackSources": fallback_sources or [],
            },
        )
        if bridge_save.ok:
            data = bridge_save.data or {}
            result = {
                    "path": data.get("path", str(target_path)),
                    "source": data.get("source", normalized_source),
                    "bytes": data.get("bytes", 0),
                    "width": data.get("width", width),
                    "height": data.get("height", height),
                    "format": data.get("format", image_format),
                    "sha256": data.get("sha256", ""),
                    "overwritten": data.get("overwritten", overwrite),
                    "degraded": bool(data.get("degraded", False)),
                    "degradeReason": data.get("degradeReason", ""),
                    "requestedSource": data.get("requestedSource", normalized_source),
                    "savedBy": "unity_bridge",
                }
            for key in (
                "captureApi", "windowHandle", "unityProcessId", "foreground",
                "occlusionSensitive", "pixelSourceVerified", "repaintRequestedAtUtcMs",
                "repaintObservedAtUtcMs", "repaintSequence", "includesSceneGui",
                "includesHandles", "matchedFullTypeName", "matchedInstanceId",
                "capturedAtUtcMs",
            ):
                if key in data:
                    result[key] = data[key]
            return ok(request_id, result)
        return bridge_save

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
        """Capture an editor window; optional degradation when capture is unavailable.

        * degrade=none — only Bridge `screenshot.editorWindow` (strict).
        * degrade=auto — editor → (unless WINDOW_NOT_FOUND) Scene view fallback → 1×1 placeholder.
        * degrade=scene — editor then Scene view; no placeholder.
        * degrade=minimal — editor then 1×1 placeholder (no Scene).

        WINDOW_NOT_FOUND is never upgraded: unknown titles must still fail for T-M26-04.
        """
        request_id = new_id("req")
        mode = self._screenshot_degrade_mode(degrade)

        primary = await self.dispatcher.call(
            new_id("req"),
            "screenshot.editorWindow",
            {"windowTitle": window_title},
        )

        if mode == "none":
            return primary

        if self._response_has_screenshot_payload(primary):
            data = dict(primary.data or {})
            data.setdefault("source", "editorWindow")
            data.setdefault("degraded", False)
            data.setdefault("degradeLevel", "")
            data.setdefault("degradeReason", "")
            return ok(primary.request_id, data)

        err_code, err_detail = self._screenshot_error_detail(primary)
        if err_code == "WINDOW_NOT_FOUND":
            return primary

        if mode in ("auto", "scene"):
            sv = await self.screenshot_scene_view(
                width=320, height=180, format="png", quality=75
            )
            if self._response_has_screenshot_payload(sv):
                d = sv.data or {}
                return ok(
                    request_id,
                    {
                        "imageData": d.get("imageData"),
                        "width": d.get("width", 320),
                        "height": d.get("height", 180),
                        "format": d.get("format", "png"),
                        "source": "sceneView",
                        "degraded": True,
                        "degradeLevel": "scene_view_fallback",
                        "degradeReason": err_code or "EDITOR_WINDOW_CAPTURE_EMPTY",
                        "requestedWindowTitle": window_title,
                        "note": "Editor window capture missing; substituted Scene view.",
                        "originalError": err_detail,
                    },
                )
            if mode == "scene":
                return sv

        if mode in ("auto", "minimal"):
            return ok(
                request_id,
                {
                    "imageData": _MIN_PLACEHOLDER_PNG_B64,
                    "width": 1,
                    "height": 1,
                    "format": "png",
                    "source": "minimalPlaceholder",
                    "degraded": True,
                    "degradeLevel": "minimal_placeholder",
                    "degradeReason": err_code or "EDITOR_WINDOW_CAPTURE_EMPTY",
                    "requestedWindowTitle": window_title,
                    "note": "Placeholder PNG; set UPILOT_SCREENSHOT_DEGRADE=none for strict errors only.",
                    "originalError": err_detail,
                },
            )

        return primary
