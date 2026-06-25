"""Screenshot assert branches (no Unity)."""

from __future__ import annotations

import asyncio
import base64
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from unitypilot_mcp.editor_e2e import assertions


def _run(coro):
    return asyncio.run(coro)


def test_screenshot_optional_no_baseline() -> None:
    facade = MagicMock()
    ok, msg, det = _run(
        assertions.run_assert(
            facade, Path("."), {"type": "screenshot", "optional": True}, {}, None, None, None
        )
    )
    assert ok is True
    assert det.get("skipped") is True


def test_screenshot_optional_missing_baseline_file(tmp_path: Path) -> None:
    facade = MagicMock()
    ok, msg, det = _run(
        assertions.run_assert(
            facade,
            tmp_path,
            {"type": "screenshot", "optional": True, "baseline": "nope.png"},
            {},
            "abcd",
            None,
            None,
        )
    )
    assert ok is True
    assert det.get("reason") == "baseline_not_found"


def test_screenshot_optional_mismatch_still_passes_with_warning(tmp_path: Path) -> None:
    facade = MagicMock()
    # 1x1 PNG via tiny valid base64 — use PIL to write matching baseline then compare different b64
    from PIL import Image
    import io

    p = tmp_path / "b.png"
    img = Image.new("RGBA", (2, 2), (255, 0, 0, 255))
    img.save(p, format="PNG")
    buf = io.BytesIO()
    img2 = Image.new("RGBA", (2, 2), (0, 255, 0, 255))
    img2.save(buf, format="PNG")
    b64 = base64.b64encode(buf.getvalue()).decode("ascii")

    ok, msg, det = _run(
        assertions.run_assert(
            facade,
            tmp_path,
            {"type": "screenshot", "optional": True, "baseline": "b.png", "tolerance": 0},
            {},
            b64,
            None,
            None,
        )
    )
    assert ok is True
    assert det.get("optional") is True
    assert "warning" in det


def test_screenshot_required_mismatch_fails(tmp_path: Path) -> None:
    from PIL import Image
    import io

    p = tmp_path / "b.png"
    img = Image.new("RGBA", (2, 2), (255, 0, 0, 255))
    img.save(p, format="PNG")
    buf = io.BytesIO()
    img2 = Image.new("RGBA", (2, 2), (0, 255, 0, 255))
    img2.save(buf, format="PNG")
    b64 = base64.b64encode(buf.getvalue()).decode("ascii")

    facade = MagicMock()
    ok, msg, det = _run(
        assertions.run_assert(
            facade,
            tmp_path,
            {"type": "screenshot", "baseline": "b.png", "tolerance": 0},
            {},
            b64,
            None,
            None,
        )
    )
    assert ok is False
