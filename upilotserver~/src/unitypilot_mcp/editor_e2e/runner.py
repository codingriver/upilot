from __future__ import annotations

import asyncio
import json
import logging
import os
import time
import uuid
import zipfile
from pathlib import Path
from typing import Any

import yaml

from ..models import ToolResponse
from ..responses import fail, ok
from ..protocol import new_id
from ..tool_facade import McpToolFacade

from . import visual
from .actions import merge_payload, run_action


def _flatten_pointer_sequences(items: list[dict[str, Any]], defaults: dict[str, Any]) -> list[dict[str, Any]]:
    """Expand uitoolkit.pointerSequence into multiple uitoolkit.event steps (M27 BL-05)."""
    out: list[dict[str, Any]] = []
    for step in items:
        if step.get("action") == "uitoolkit.pointerSequence":
            merged = merge_payload(step, defaults)
            merged.pop("action", None)
            for ev in step.get("events") or []:
                if not isinstance(ev, dict):
                    continue
                evstep = dict(merged)
                evstep["action"] = "uitoolkit.event"
                for k, v in ev.items():
                    evstep[k] = v
                out.append(evstep)
        else:
            out.append(step)
    return out


from .assertions import run_assert
from .models import E2EReport, StepRecord, load_spec_dict
from .report import iso_now, write_manifest, write_report_json

logger = logging.getLogger("unitypilot.editor_e2e.runner")


def _tool_ok(r: ToolResponse) -> bool:
    return bool(r.ok and r.error is None)


async def run_editor_e2e_from_path(
    facade: McpToolFacade,
    spec_path: str | Path,
    artifact_dir: str | Path | None = None,
    stop_on_first_failure: bool = True,
    export_zip: bool = False,
    webhook_on_failure: bool = False,
) -> ToolResponse:
    """Load YAML spec from disk, execute against Unity, write report + artifacts."""
    path = Path(spec_path).expanduser().resolve()
    if not path.is_file():
        return fail(
            new_id("e2e"),
            "E2E_SPEC_NOT_FOUND",
            f"Spec file not found: {path}",
            {},
        )

    raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    if raw is None:
        return fail(
            new_id("e2e"),
            "E2E_SPEC_EMPTY",
            "Spec file is empty",
            {},
        )

    try:
        spec = load_spec_dict(raw, path)
    except ValueError as e:
        return fail(
            new_id("e2e"),
            "E2E_SPEC_INVALID",
            str(e),
            {},
        )

    spec.setup = _flatten_pointer_sequences(spec.setup, spec.defaults)
    spec.steps = _flatten_pointer_sequences(spec.steps, spec.defaults)
    spec.teardown = _flatten_pointer_sequences(spec.teardown, spec.defaults)

    spec_dir = path.parent
    run_id = uuid.uuid4().hex[:12]
    art = Path(artifact_dir).expanduser().resolve() if artifact_dir else (spec_dir / ".unitypilot-e2e-artifacts" / run_id)
    art.mkdir(parents=True, exist_ok=True)

    started = iso_now()
    steps_out: list[StepRecord] = []
    failure: dict[str, Any] | None = None
    last_b64: str | None = None
    last_title: str | None = None
    console_snap: str | None = None
    manifest_files: list[dict[str, Any]] = []

    async def _run_section(phase: str, items: list[dict[str, Any]], si: list[int]) -> bool:
        nonlocal failure, last_b64, last_title, console_snap
        for j, step in enumerate(items):
            idx = si[0]
            si[0] += 1

            if "assert" in step and "action" not in step:
                t0 = time.perf_counter()
                ast = step["assert"]
                if not isinstance(ast, dict):
                    failure = {"stepIndex": idx, "phase": phase, "message": "assert must be a mapping"}
                    steps_out.append(
                        StepRecord(phase, idx, "assert", None, False, 0, {}, str(failure["message"]))
                    )
                    return False
                ok_a, msg, det = await run_assert(
                    facade, spec_dir, ast, spec.defaults, last_b64, last_title, console_snap
                )
                dur = (time.perf_counter() - t0) * 1000
                steps_out.append(
                    StepRecord(phase, idx, "assert", None, ok_a, dur, {"detail": det, "message": msg})
                )
                if not ok_a:
                    failure = {"stepIndex": idx, "phase": phase, "message": msg, "detail": det}
                    await _write_fail_artifacts(phase, j, idx)
                    return False
                continue

            if "action" not in step:
                failure = {"stepIndex": idx, "phase": phase, "message": "step must have action or assert"}
                steps_out.append(StepRecord(phase, idx, "?", None, False, 0, {}, str(failure["message"])))
                return False

            action = str(step["action"])
            payload = merge_payload(step, spec.defaults)

            if step.get("snapshotConsole"):
                r0 = await facade.console_get_logs(log_type="", count=500)
                if _tool_ok(r0):
                    from .assertions import format_logs_text

                    logs = (r0.data or {}).get("logs") or []
                    console_snap = format_logs_text(logs)

            t0 = time.perf_counter()
            resp = await run_action(facade, action, payload)
            dur = (time.perf_counter() - t0) * 1000

            detail: dict[str, Any] = {"ok": resp.ok}
            if resp.error:
                detail["error"] = {"code": resp.error.code, "message": resp.error.message}
            action_ok = _tool_ok(resp)
            skip_optional_action = bool(step.get("optional")) or (
                action == "screenshot.window"
                and bool(spec.visual.get("screenshotOptional"))
            )
            if not action_ok and skip_optional_action:
                detail = {"ok": False, "skipped": True, "optionalSkip": True}
                if resp.error:
                    detail["error"] = {"code": resp.error.code, "message": resp.error.message}
                steps_out.append(
                    StepRecord(phase, idx, "action", action, True, dur, detail)
                )
                continue

            if action == "screenshot.window" and _tool_ok(resp) and resp.data:
                last_b64 = str(resp.data.get("imageData") or "")
                last_title = str(payload.get("windowTitle") or payload.get("targetWindow") or "")
                if last_b64 and spec.visual.get("saveAllScreenshots"):
                    p = art / f"{phase}_{j:03d}_screenshot.png"
                    try:
                        visual.save_png_b64(last_b64, p)
                        manifest_files.append({"path": str(p.relative_to(art)), "role": "screenshot"})
                    except OSError as e:
                        detail["screenshotSaveError"] = str(e)

            steps_out.append(StepRecord(phase, idx, "action", action, action_ok, dur, detail))
            if not action_ok:
                failure = {
                    "stepIndex": idx,
                    "phase": phase,
                    "message": resp.error.message if resp.error else "action failed",
                    "detail": detail,
                }
                await _write_fail_artifacts(phase, j, idx)
                return False

            if "assert" in step:
                t1 = time.perf_counter()
                ast2 = step["assert"]
                ok_a, msg, det = await run_assert(
                    facade, spec_dir, ast2, spec.defaults, last_b64, last_title, console_snap
                )
                dur_a = (time.perf_counter() - t1) * 1000
                steps_out.append(
                    StepRecord(phase, idx, "assert", action, ok_a, dur_a, {"detail": det, "message": msg})
                )
                if not ok_a:
                    failure = {"stepIndex": idx, "phase": phase, "message": msg, "detail": det}
                    await _write_fail_artifacts(phase, j, idx)
                    return False

        return True

    async def _write_fail_artifacts(phase: str, j: int, idx: int) -> None:
        prefix = f"{phase}_{j:03d}_step{idx}"
        try:
            tw = str(spec.defaults.get("targetWindow") or "")
            if tw:
                rd = await facade.uitoolkit_dump(target_window=tw, max_depth=8)
                if _tool_ok(rd) and rd.data:
                    dp = art / f"{prefix}_uitoolkit_dump.json"
                    dp.write_text(json.dumps(rd.data, ensure_ascii=False, indent=2), encoding="utf-8")
                    manifest_files.append({"path": dp.name, "role": "uitoolkit.dump"})
        except Exception as ex:
            logger.warning("fail artifact dump: %s", ex)
        try:
            rlog = await facade.console_get_logs(log_type="", count=200)
            if _tool_ok(rlog) and rlog.data:
                lp = art / f"{prefix}_console.json"
                lp.write_text(json.dumps(rlog.data, ensure_ascii=False, indent=2), encoding="utf-8")
                manifest_files.append({"path": lp.name, "role": "console"})
        except Exception as ex:
            logger.warning("fail artifact console: %s", ex)

    counter = [0]

    async def _body() -> None:
        nonlocal failure
        try:
            if not await _run_section("setup", spec.setup, counter):
                return
            if not await _run_section("steps", spec.steps, counter):
                return
        finally:
            prior_failure = failure
            td_ok = await _run_section("teardown", spec.teardown, counter)
            if not td_ok and prior_failure is not None:
                failure = prior_failure

    try:
        await asyncio.wait_for(_body(), timeout=spec.timeout_s)
    except asyncio.TimeoutError:
        failure = {"stepIndex": counter[0], "message": f"spec timeout after {spec.timeout_s}s"}

    ended = iso_now()
    passed = failure is None

    report_path = art / "report.json"
    rep = E2EReport(
        spec_name=spec.name,
        passed=passed,
        started_at=started,
        ended_at=ended,
        steps=steps_out,
        failure=failure,
        artifact_dir=str(art),
        report_path=str(report_path),
    )
    write_report_json(report_path, rep)
    write_manifest(art / "manifest.json", manifest_files)

    zip_path_str = ""
    if export_zip:
        zpath = art / "e2e-bundle.zip"
        try:
            with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as zf:
                for p in art.rglob("*"):
                    if p.is_file():
                        zf.write(p, p.relative_to(art))
            zip_path_str = str(zpath)
        except OSError as ex:
            logger.warning("e2e zip failed: %s", ex)

    if not passed and webhook_on_failure:
        wh = os.environ.get("UNITYPILOT_E2E_WEBHOOK_URL", "").strip()
        if wh:
            try:
                _post_e2e_webhook(wh, report_path, art)
            except OSError as ex:
                logger.warning("e2e webhook failed: %s", ex)

    out_data: dict[str, Any] = {
        "passed": passed,
        "specName": spec.name,
        "specPath": str(path),
        "artifactDir": str(art),
        "reportPath": str(report_path),
        "failure": failure,
        "stepCount": len(steps_out),
    }
    if zip_path_str:
        out_data["zipPath"] = zip_path_str

    return ok(new_id("e2e"), out_data)


def _post_e2e_webhook(url: str, report_path: Path, artifact_dir: Path) -> None:
    import urllib.request

    boundary = f"----unitypilot-{uuid.uuid4().hex}"
    crlf = "\r\n"
    parts: list[str] = []
    if report_path.is_file():
        parts.append(f"--{boundary}")
        parts.append(f'Content-Disposition: form-data; name="report"; filename="{report_path.name}"')
        parts.append("Content-Type: application/json")
        parts.append("")
        parts.append(report_path.read_text(encoding="utf-8"))
    parts.append(f"--{boundary}--")
    body = (crlf.join(parts)).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        resp.read(4096)


