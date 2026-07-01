from __future__ import annotations

import asyncio
import logging
from typing import Any

from ..models import ToolResponse
from ..protocol import new_id
from ..responses import fail
from ..tool_facade import McpToolFacade

logger = logging.getLogger("upilot.editor_e2e.actions")


def merge_payload(step: dict[str, Any], defaults: dict[str, Any]) -> dict[str, Any]:
    out = dict(defaults)
    for k, v in step.items():
        if k in ("action", "assert", "optional", "snapshotConsole"):
            continue
        out[k] = v
    return out


async def run_action(
    facade: McpToolFacade,
    action: str,
    payload: dict[str, Any],
) -> ToolResponse:
    a = (action or "").strip()
    p = payload

    if a == "wait_condition" or a.startswith("uitoolkit."):
        return fail(new_id("e2e"), "UITOOLKIT_DISABLED", "UIToolkit / wait_condition E2E actions are disabled.", {"action": a})

    if a == "wait":
        ms = int(p.get("ms") or p.get("delayMs") or 0)
        if ms < 0:
            ms = 0
        if ms > 120000:
            ms = 120000
        use_editor = bool(p.get("unity", p.get("editorThread", True)))
        if use_editor:
            return await facade.editor_delay(ms)
        await asyncio.sleep(ms / 1000.0)
        return ToolResponse(ok=True, data={"sleptMs": ms, "mode": "asyncio"}, error=None, request_id="local")

    if a == "menu":
        path = str(p.get("menuPath") or p.get("menu_path") or "")
        return await facade.menu_execute(path)

    if a == "editor.state":
        return await facade.editor_state()

    if a == "editor.windows.list":
        return await facade.editor_windows_list(
            type_filter=str(p.get("typeFilter") or ""),
            title_filter=str(p.get("titleFilter") or ""),
        )

    if a == "ensure_ready":
        return await facade.ensure_ready(timeout_s=float(p.get("timeoutS") or 120))

    if a == "compile_wait":
        return await facade.compile_wait(
            timeout_s=float(p.get("timeoutS") or 120),
            poll_interval_s=float(p.get("pollIntervalS") or 1.0),
        )

    if a == "compile_wait_editor":
        return await facade.compile_wait_editor(timeout_ms=int(p.get("timeoutMs") or 120000))

    if a == "editor.delay":
        return await facade.editor_delay(int(p.get("delayMs") or p.get("ms") or 0))

    if a == "editor.window.close":
        return await facade.editor_window_close(
            window_title=str(p.get("windowTitle") or ""),
            match_mode=str(p.get("matchMode") or "exact"),
        )

    if a == "editor.window.setRect":
        return await facade.editor_window_set_rect(
            window_title=str(p.get("windowTitle") or ""),
            x=float(p.get("x") or 0),
            y=float(p.get("y") or 0),
            width=float(p.get("width") or 100),
            height=float(p.get("height") or 100),
            match_mode=str(p.get("matchMode") or "exact"),
        )

    if a == "console.clear":
        return await facade.console_clear()

    if a == "console.get_logs":
        return await facade.console_get_logs(
            log_type=str(p.get("logType") or ""),
            count=int(p.get("count") or 200),
        )

    if a == "mouse":
        return await facade.mouse_event(
            action=str(p.get("mouseAction") or p.get("action") or "click"),
            button=str(p.get("button") or "left"),
            x=float(p.get("x") or 0),
            y=float(p.get("y") or 0),
            target_window=str(p.get("targetWindow") or ""),
            modifiers=list(p.get("modifiers") or []),
            scroll_delta_x=float(p.get("scrollDeltaX") or 0),
            scroll_delta_y=float(p.get("scrollDeltaY") or 0),
            element_name=str(p.get("elementName") or ""),
            element_index=int(p.get("elementIndex") if p.get("elementIndex") is not None else -1),
        )

    if a == "keyboard":
        return await facade.keyboard_event(
            action=str(p.get("keyboardAction") or p.get("keyAction") or "keydown"),
            target_window=str(p.get("targetWindow") or ""),
            key_code=str(p.get("keyCode") or ""),
            character=str(p.get("character") or ""),
            text=str(p.get("text") or ""),
            modifiers=list(p.get("modifiers") or []),
        )

    if a == "screenshot.window":
        return await facade.screenshot_editor_window(
            window_title=str(p.get("windowTitle") or p.get("targetWindow") or "upilot"),
            degrade=str(p.get("screenshotDegrade") or p.get("degrade") or "auto"),
        )

    if a == "test.run":
        return await facade.test_run(
            test_mode=str(p.get("testMode") or "EditMode"),
            test_filter=str(p.get("testFilter") or ""),
        )

    if a == "drag_drop":
        return await facade.drag_drop(
            source_window=str(p.get("sourceWindow") or ""),
            target_window=str(p.get("targetWindow") or ""),
            drag_type=str(p.get("dragType") or ""),
            from_x=float(p.get("fromX") or 0),
            from_y=float(p.get("fromY") or 0),
            to_x=float(p.get("toX") or 0),
            to_y=float(p.get("toY") or 0),
            asset_paths=list(p.get("assetPaths") or []),
            game_object_ids=list(p.get("gameObjectIds") or []),
            custom_data=str(p.get("customData") or ""),
            modifiers=list(p.get("modifiers") or []),
        )

    if a == "reflection.find":
        return await facade.reflection_find(
            type_name=str(p.get("typeName") or ""),
            method_name=str(p.get("methodName") or ""),
        )

    if a == "reflection.call":
        return await facade.reflection_call(
            type_name=str(p.get("typeName") or ""),
            method_name=str(p.get("methodName") or ""),
            parameters=list(p.get("parameters") or []),
            is_static=bool(p.get("isStatic", True)),
            target_instance_path=str(p.get("targetInstancePath") or ""),
            target_static_type_name=str(p.get("targetStaticTypeName") or ""),
            target_static_member_path=str(p.get("targetStaticMemberPath") or ""),
        )

    if a == "reflection.eval":
        variables = p.get("variables") if isinstance(p.get("variables"), dict) else None
        options = p.get("options") if isinstance(p.get("options"), dict) else None
        return await facade.reflection_eval(
            code=str(p.get("code") or ""),
            variables=variables,
            options=options,
        )

    return fail(new_id("e2e"), "E2E_UNKNOWN_ACTION", f"Unknown action: {a}", {"action": a})
