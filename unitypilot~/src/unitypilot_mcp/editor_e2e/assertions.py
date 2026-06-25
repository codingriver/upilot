from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from ..models import ToolResponse
from ..tool_facade import McpToolFacade

from . import visual
from .actions import merge_payload


def _tool_ok(r: ToolResponse) -> bool:
    return bool(r.ok and r.error is None)


async def run_assert(
    facade: McpToolFacade,
    spec_dir: Path,
    ast: dict[str, Any],
    defaults: dict[str, Any],
    last_screenshot_b64: str | None,
    last_screenshot_title: str | None,
    console_snapshot_text: str | None,
) -> tuple[bool, str, dict[str, Any]]:
    """Returns (passed, message, detail)."""
    at = str(ast.get("type") or "").strip().lower()
    if not at:
        return False, "assert.type is required", {}

    if at in ("uitoolkit.query", "uitoolkit.dump", "wait_condition"):
        return False, "UIToolkit assertions are disabled in this build", {}

    if at == "console":
        r = await facade.console_get_logs(
            log_type=str(ast.get("logType") or ""),
            count=int(ast.get("count") or 500),
        )
        if not _tool_ok(r):
            return False, f"console.get_logs failed: {r.error.message if r.error else ''}", {}
        logs = (r.data or {}).get("logs") or []
        text = format_logs_text(logs)
        if ast.get("sinceSnapshot") and console_snapshot_text is not None:
            if text.startswith(console_snapshot_text):
                text = text[len(console_snapshot_text) :]
            else:
                text = text  # fallback: full text
        errs = sum(1 for e in logs if str((e or {}).get("logType") or "") in ("Error", "Exception", "Assert"))
        max_err = ast.get("maxErrors")
        if max_err is not None and errs > int(max_err):
            return False, f"too many errors: {errs} > {max_err}", {"errorCount": errs}
        for s in ast.get("mustContain") or []:
            if str(s) not in text:
                return False, f"mustContain missing: {s!r}", {}
        for s in ast.get("mustNotContain") or []:
            if str(s) in text:
                return False, f"mustNotContain found: {s!r}", {}
        return True, "ok", {"errorCount": errs, "logChars": len(text)}

    if at == "reflection":
        r = await facade.reflection_call(
            type_name=str(ast.get("typeName") or ""),
            method_name=str(ast.get("methodName") or ""),
            parameters=list(ast.get("parameters") or []),
            is_static=bool(ast.get("isStatic", True)),
            target_instance_path=str(ast.get("targetInstancePath") or ""),
        )
        if not _tool_ok(r):
            return False, f"reflection.call failed: {r.error.message if r.error else ''}", {}
        res = str((r.data or {}).get("result") or "")
        if "expectContains" in ast and str(ast["expectContains"]) not in res:
            return False, "result does not contain expected substring", {"result": res[:500]}
        if "expectEquals" in ast and res != str(ast["expectEquals"]):
            return False, "result does not equal expected", {"result": res[:500]}
        return True, "ok", {}

    if at == "screenshot":
        optional = bool(ast.get("optional"))
        baseline = ast.get("baseline")
        if baseline:
            base_path = (spec_dir / str(baseline)).resolve()
            if not base_path.is_file():
                if optional:
                    return True, "skipped (optional): baseline missing", {"skipped": True, "reason": "baseline_not_found"}
                return False, f"baseline not found: {base_path}", {}
            if not last_screenshot_b64:
                if optional:
                    return True, "skipped (optional): no screenshot", {"skipped": True, "reason": "no_screenshot"}
                return False, "no screenshot captured in this run (add a screenshot.window step first)", {}
            tol = int(ast.get("tolerance") or ast.get("pixelTolerance") or 5)
            cmp = visual.compare_b64_to_png_file(last_screenshot_b64, base_path, tolerance=tol)
            if not cmp.match:
                if optional:
                    return True, "visual diff (optional)", {
                        "optional": True,
                        "warning": cmp.message,
                        "diff": cmp.detail,
                    }
                return False, cmp.message, {"diff": cmp.detail}
            return True, "ok", {"visual": cmp.detail}
        if optional:
            return True, "skipped (optional): no baseline", {"skipped": True, "reason": "no_baseline"}
        return False, "screenshot assert requires baseline or optional:true", {}

    if at == "tool_response":
        """Re-assert last action result (runner passes last_response)."""
        return False, "tool_response assert is handled in runner", {}

    return False, f"unknown assert type: {at}", {}


def format_logs_text(logs: list[Any]) -> str:
    lines: list[str] = []
    for e in logs:
        if isinstance(e, dict):
            lines.append(str(e.get("message") or ""))
        else:
            lines.append(str(e))
    return "\n".join(lines)


def _short(r: ToolResponse) -> dict[str, Any]:
    return {"ok": r.ok, "code": r.error.code if r.error else None, "msg": r.error.message if r.error else None}
