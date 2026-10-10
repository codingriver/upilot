from __future__ import annotations

import base64
import io
from dataclasses import dataclass
from pathlib import Path
from typing import Any

try:
    from PIL import Image, ImageChops
except ImportError:  # pragma: no cover
    Image = None  # type: ignore[misc, assignment]
    ImageChops = None  # type: ignore[misc, assignment]


@dataclass
class VisualCompareResult:
    match: bool
    message: str
    detail: dict[str, Any]


def compare_b64_to_png_file(b64_data: str, baseline_path: Path, tolerance: int = 5) -> VisualCompareResult:
    """Compare a PNG (base64) to an on-disk PNG. Different sizes => fail."""
    if Image is None or ImageChops is None:
        return VisualCompareResult(
            match=False,
            message="pillow (PIL) not installed",
            detail={},
        )
    raw = base64.b64decode(b64_data)
    cur = Image.open(io.BytesIO(raw)).convert("RGBA")
    base = Image.open(baseline_path).convert("RGBA")
    if cur.size != base.size:
        return VisualCompareResult(
            match=False,
            message=f"size mismatch: current {cur.size} vs baseline {base.size}",
            detail={"currentSize": cur.size, "baselineSize": base.size},
        )
    diff = ImageChops.difference(cur, base)
    extrema = diff.getextrema()
    max_delta = max(ch[1] for ch in extrema)
    ok = max_delta <= tolerance
    return VisualCompareResult(
        match=ok,
        message="within tolerance" if ok else f"max channel delta {max_delta} > {tolerance}",
        detail={"maxDelta": max_delta, "tolerance": tolerance},
    )


def save_png_b64(b64_data: str, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(base64.b64decode(b64_data))
