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

class StatusDomainService:
    @staticmethod
    def _command_requests_batchmode(command: str) -> bool:
        cmd = (command or "").strip()
        if not cmd:
            return False

        try:
            tokens = shlex.split(cmd, posix=False)
        except ValueError:
            tokens = cmd.split()

        normalized = [token.strip().strip("'\"").lower() for token in tokens]
        return any(token in ("-batchmode", "/batchmode") for token in normalized)

    async def open_editor(
        self, command: str = "", wait_for_connect_ms: int = 60000
    ) -> ToolResponse:
        request_id = new_id("req")

        # Already connected — return current session info immediately
        if self.server.session_manager.is_connected():
            self._sync_workspace_root_from_session()
            session = self.server.session_manager.active
            return ok(
                request_id,
                {
                    "started": False,
                    "connected": True,
                    "sessionId": session.session_id if session else "",
                },
            )

        # Launch Unity only when a non-empty command is provided (after trim); otherwise wait-only.
        cmd = (command or "").strip()
        started = False
        wait_only = not bool(cmd)
        if cmd:
            if self._command_requests_batchmode(cmd):
                return fail(
                    request_id,
                    "BATCHMODE_DISABLED",
                    "Batch mode is temporarily disabled for UPilot MCP. Start Unity without -batchmode.",
                    {"command": cmd},
                )
            try:
                subprocess.Popen(cmd, shell=True)
                started = True
            except Exception as ex:
                return fail(
                    request_id,
                    "OPEN_EDITOR_FAILED",
                    f"启动 Unity 失败: {ex}",
                    {"command": cmd},
                )

        # Poll until Unity connects or timeout
        deadline = now_ms() + wait_for_connect_ms
        while now_ms() < deadline:
            await asyncio.sleep(0.5)
            if self.server.session_manager.is_connected():
                self._sync_workspace_root_from_session()
                session = self.server.session_manager.active
                return ok(
                    request_id,
                    {
                        "started": started,
                        "connected": True,
                        "sessionId": session.session_id if session else "",
                        "waitOnly": wait_only,
                    },
                )

        msg = (
            "等待 Unity 连接超时（未提供启动命令：请手动打开项目并连接 Bridge）"
            if wait_only
            else "等待 Unity 连接超时"
        )
        return fail(
            request_id,
            "UNITY_NOT_CONNECTED",
            msg,
            {
                "waitForConnectMs": wait_for_connect_ms,
                "command": cmd,
                "waitOnly": wait_only,
            },
        )

    async def editor_state(self) -> ToolResponse:
        request_id = new_id("req")
        if self.server.is_ready():
            live = await self.dispatcher.call(
                request_id, "resource.editorState", {}, timeout_ms=5000
            )
            if live.ok and live.data is not None:
                state = self._update_editor_cache_from_resource_state(live.data)
                return ok(
                    request_id,
                    {
                        "connected": True,
                        "isCompiling": state["isCompiling"],
                        "playModeState": state["playModeState"],
                        "activeScene": state["activeScene"],
                        "isPlaying": state["isPlaying"],
                        "isPaused": state["isPaused"],
                        "source": "resource",
                    },
                )

        s = self.server.state.editor
        connected = s.connected or self.server.is_ready()
        return ok(
            request_id,
            {
                "connected": connected,
                "isCompiling": s.is_compiling,
                "playModeState": s.play_mode_state,
                "activeScene": s.active_scene,
                "source": "cache-fallback",
            },
        )

    async def mcp_status(self, force_fresh: bool = False, include_capabilities: bool = True) -> ToolResponse:
        request_id = new_id("req")
        live_state: dict | None = None
        if force_fresh:
            state_r = await self.editor_state()
            if state_r.ok:
                live_state = state_r.data or {}
        session = self.server.session_manager.active
        compile_state = self.server.state.compile
        unity_abs = ""
        if session and session.project_path:
            try:
                unity_abs = str(Path(session.project_path).resolve())
            except OSError:
                unity_abs = session.project_path

        data = {
                "connected": self.server.session_manager.is_connected(),
                "serverReady": self.server.is_ready(),
                "session": {
                    "sessionId": session.session_id if session else "",
                    "projectPath": session.project_path if session else "",
                    "unityVersion": session.unity_version if session else "",
                    "platform": session.platform if session else "",
                    "lastHeartbeatAt": session.last_heartbeat_at if session else 0,
                },
                "paths": {
                    "unityProjectAbsolute": unity_abs,
                    "mcpProcessWorkingDirectory": str(Path.cwd().resolve()),
                },
                "compile": {
                    "status": compile_state.status,
                    "errorCount": compile_state.error_count,
                    "warningCount": compile_state.warning_count,
                    "startedAt": compile_state.started_at,
                    "finishedAt": compile_state.finished_at,
                },
                "editor": live_state or {
                    "playModeState": self.server.state.editor.play_mode_state,
                    "activeScene": self.server.state.editor.active_scene,
                    "isCompiling": self.server.state.editor.is_compiling,
                    "source": "cache",
                },
                "timeouts": self.dispatcher.timeout_policy_snapshot(),
                "mcp": {
                    "label": self.server.mcp_label,
                    "host": self.server.host,
                    "port": self.server.port,
                },
            }
        if include_capabilities:
            data["capabilities"] = {
                "registryVersion": REGISTRY_VERSION,
                "toolCount": len([item for item in REGISTRY.list() if item.feature == "core" or CONFIG.flow_enabled]),
                "reflectionCallAvailable": REGISTRY.resolve("unity_reflection_call") is not None,
                "screenshotSaveAvailable": REGISTRY.resolve("unity_screenshot_save") is not None,
                "asyncTaskAvailable": REGISTRY.resolve("unity_task_start") is not None,
                "states": {
                    "serviceRegistered": True,
                    "clientToolListInjected": None,
                    "actualCallSucceeded": self._last_command_succeeded("reflection.call"),
                    "note": "Client injection is client-owned; refresh the MCP client after the server tool list changes.",
                },
                "flow": {
                    "enabled": CONFIG.flow_enabled,
                    "available": CONFIG.flow_enabled,
                    "reason": "" if CONFIG.flow_enabled else "UPilot Flow is disabled by project configuration",
                },
            }
        return ok(
            request_id,
            data,
        )

    async def capabilities_get(self, force_fresh: bool = False) -> ToolResponse:
        status = await self.mcp_status(force_fresh=force_fresh, include_capabilities=True)
        if not status.ok:
            return status
        data = status.data or {}
        return ok(
            status.request_id,
            {
                "registryVersion": REGISTRY_VERSION,
                "tools": [item.to_dict() for item in REGISTRY.list()],
                "capabilities": data.get("capabilities", {}),
                "session": data.get("session", {}),
                "paths": data.get("paths", {}),
            },
        )

    async def tools_find(
        self,
        query: str = "",
        category: str = "",
        availability: str = "all",
        limit: int = 20,
    ) -> ToolResponse:
        items = REGISTRY.find(
            query=query,
            category=category,
            availability=availability,
            limit=limit,
            flow_enabled=CONFIG.flow_enabled,
        )
        return ok(new_id("req"), {"count": len(items), "tools": items})

    def _last_command_succeeded(self, command_name: str) -> bool | None:
        matches = [item for item in self.server.state.commands.values() if item.name == command_name]
        if not matches:
            return None
        latest = max(matches, key=lambda item: item.created_at)
        return latest.status == "success"

    async def client_config_diagnose(self) -> ToolResponse:
        return ok(new_id("req"), diagnose_client_configs())

    @staticmethod
    def _find_unity_hwnd() -> int:
        """Find Unity Editor main window handle (Windows only)."""
        if sys.platform != "win32":
            return 0
        try:
            import ctypes

            # Try common Unity Editor window class names
            hwnd = 0
            for class_name in ("UnityContainerWndClass", "UnityWndClass"):
                hwnd = ctypes.windll.user32.FindWindowW(class_name, None)
                if hwnd:
                    break
            if not hwnd:
                # Fallback: search by window title containing "Unity"
                def _enum_callback(hwnd_extra, _):
                    buf = ctypes.create_unicode_buffer(256)
                    ctypes.windll.user32.GetWindowTextW(hwnd_extra, buf, 256)
                    if "Unity" in buf.value and "Editor" in buf.value:
                        hwnd_extra_list.append(hwnd_extra)
                    return True

                hwnd_extra_list: list[int] = []
                EnumWindowsProc = ctypes.WINFUNCTYPE(
                    ctypes.c_bool, ctypes.c_int, ctypes.c_void_p
                )
                ctypes.windll.user32.EnumWindows(EnumWindowsProc(_enum_callback), 0)
                if hwnd_extra_list:
                    hwnd = hwnd_extra_list[0]
            return hwnd
        except Exception:
            return 0

    @classmethod
    def _wake_unity_editor(cls) -> bool:
        """Windows: post a harmless WM_NULL to the Unity Editor window to prevent background throttling.

        Uses PostMessageW so we do NOT steal foreground focus or interrupt user typing.
        """
        hwnd = cls._find_unity_hwnd()
        if not hwnd:
            return False
        try:
            import ctypes

            # Post WM_NULL — does nothing functionally, but wakes the message pump
            ctypes.windll.user32.PostMessageW(hwnd, 0x0000, 0, 0)
            return True
        except Exception:
            return False

    async def editor_focus(self) -> ToolResponse:
        """将 Unity Editor 窗口设置为前台焦点窗口（Windows 平台）。

        使用 SetForegroundWindow 强制将 Unity 窗口带到前台，解决无焦点导致的编译延迟问题。
        仅在 Windows 平台有效，其他平台返回不支持。
        """
        import sys

        request_id = new_id("req")
        if sys.platform != "win32":
            return fail(
                request_id,
                "PLATFORM_NOT_SUPPORTED",
                "editor_focus 仅在 Windows 平台可用",
                {},
            )
        hwnd = self._find_unity_hwnd()
        if not hwnd:
            return fail(
                request_id,
                "WINDOW_NOT_FOUND",
                "未找到 Unity Editor 窗口，请确保 Unity 已启动",
                {},
            )
        try:
            import ctypes

            # SW_RESTORE = 9，恢复窗口并激活
            ctypes.windll.user32.ShowWindow(hwnd, 9)
            # 强制将窗口设置为前台焦点
            result = ctypes.windll.user32.SetForegroundWindow(hwnd)
            return ok(
                request_id,
                {
                    "focused": True,
                    "hwnd": hwnd,
                    "setForegroundResult": result,
                },
            )
        except Exception as ex:
            return fail(
                request_id,
                "FOCUS_FAILED",
                f"设置 Unity 焦点失败: {ex}",
                {"hwnd": hwnd},
            )

    async def editor_focus_state(self) -> ToolResponse:
        """查询 Unity Editor 窗口的焦点状态（Windows 平台）。

        返回 Unity 窗口是否拥有当前焦点、窗口标题、当前焦点窗口标题等信息，
        用于判断是否需要调用 editor_focus。
        """
        import sys

        request_id = new_id("req")
        if sys.platform != "win32":
            return fail(
                request_id,
                "PLATFORM_NOT_SUPPORTED",
                "editor_focus_state 仅在 Windows 平台可用",
                {},
            )
        hwnd = self._find_unity_hwnd()
        if not hwnd:
            return fail(
                request_id,
                "WINDOW_NOT_FOUND",
                "未找到 Unity Editor 窗口",
                {},
            )
        try:
            import ctypes

            # 获取当前前台窗口
            fg_hwnd = ctypes.windll.user32.GetForegroundWindow()

            # 获取 Unity 窗口标题
            buf = ctypes.create_unicode_buffer(256)
            ctypes.windll.user32.GetWindowTextW(hwnd, buf, 256)
            unity_title = buf.value

            # 获取前台窗口标题
            fg_buf = ctypes.create_unicode_buffer(256)
            ctypes.windll.user32.GetWindowTextW(fg_hwnd, fg_buf, 256)
            fg_title = fg_buf.value

            # 获取窗口类名（用于调试）
            class_buf = ctypes.create_unicode_buffer(256)
            ctypes.windll.user32.GetClassNameW(hwnd, class_buf, 256)
            unity_class = class_buf.value

            return ok(
                request_id,
                {
                    "unityFocused": hwnd == fg_hwnd,
                    "unityHwnd": hwnd,
                    "unityTitle": unity_title,
                    "unityClass": unity_class,
                    "foregroundHwnd": fg_hwnd,
                    "foregroundTitle": fg_title,
                },
            )
        except Exception as ex:
            return fail(
                request_id,
                "QUERY_FAILED",
                f"查询焦点状态失败: {ex}",
                {},
            )

    # Editor state, input, windows, console, and selection operations.
    def _update_editor_cache_from_resource_state(self, data: dict) -> dict:
        is_playing = bool(data.get("isPlaying", False))
        is_paused = bool(data.get("isPaused", False))
        play_mode_state = "pause" if is_paused else ("play" if is_playing else "edit")
        active_scene = str(data.get("activeSceneName", ""))
        is_compiling = bool(data.get("isCompiling", False))
        self.server.state.update_editor_state(
            {
                "connected": True,
                "isCompiling": is_compiling,
                "playModeState": play_mode_state,
                "activeScene": active_scene,
            }
        )
        return {
            "isPlaying": is_playing,
            "isPaused": is_paused,
            "playModeState": play_mode_state,
            "activeScene": active_scene,
            "isCompiling": is_compiling,
        }

    async def playmode_start(self) -> ToolResponse:
        request_id = new_id("req")
        result = await self.dispatcher.call(
            request_id, "playmode.set", {"action": "play"}
        )

        # PlayMode can trigger domain reload — handle reconnect
        if (
            not result.ok
            and result.error
            and result.error.code in ("CONNECTION_LOST", "DOMAIN_RELOAD_TIMEOUT")
        ):
            import asyncio

            for _ in range(15):
                await asyncio.sleep(2)
                if self.server.session_manager.is_connected():
                    s = self.server.state.editor
                    return ok(
                        request_id,
                        {
                            "ok": True,
                            "state": s.play_mode_state or "play",
                            "reconnected": True,
                        },
                    )
            return fail(
                request_id,
                "PLAYMODE_RECONNECT_TIMEOUT",
                "进入 PlayMode 后 Unity 未能重连",
                {"requestId": request_id},
            )

        return result

    async def playmode_stop(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "playmode.set", {"action": "stop"}
        )

    async def mouse_event(
        self,
        action: str,
        button: str,
        x: float,
        y: float,
        target_window: str,
        modifiers: list[str] | None = None,
        scroll_delta_x: float = 0.0,
        scroll_delta_y: float = 0.0,
        element_name: str = "",
        element_index: int = -1,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "action": action,
            "button": button,
            "x": x,
            "y": y,
            "targetWindow": target_window,
            "modifiers": modifiers or [],
            "scrollDeltaX": scroll_delta_x,
            "scrollDeltaY": scroll_delta_y,
        }
        if element_name:
            payload["elementName"] = element_name
        if element_index >= 0:
            payload["elementIndex"] = element_index
        return await self.dispatcher.call(request_id, "mouse.event", payload)

    async def uitoolkit_dump(
        self, target_window: str, max_depth: int = 10
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_query(
        self,
        target_window: str,
        name_filter: str = "",
        class_filter: str = "",
        type_filter: str = "",
        text_filter: str = "",
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_event(
        self,
        target_window: str,
        event_type: str,
        element_name: str = "",
        element_index: int = -1,
        key_code: str = "",
        character: str = "",
        mouse_button: int = 0,
        mouse_x: float = 0,
        mouse_y: float = 0,
        wheel_delta_x: float = 0,
        wheel_delta_y: float = 0,
        modifiers: list[str] | None = None,
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_scroll(
        self,
        target_window: str,
        element_name: str = "",
        element_index: int = -1,
        scroll_to_x: float = -1,
        scroll_to_y: float = -1,
        delta_x: float = 0,
        delta_y: float = 0,
        mode: str = "absolute",
        scroll_view_name_path: str = "",
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_scrollbar_drag(
        self,
        target_window: str,
        scroll_view_element_name: str = "",
        scrollbar_axis: str = "vertical",
        normalized_thumb_position: float = 0.5,
        drag_steps: int = 5,
        scroll_view_name_path: str = "",
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_set_value(
        self,
        target_window: str,
        value: str,
        element_name: str = "",
        element_index: int = -1,
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def uitoolkit_interact(
        self,
        target_window: str,
        action: str = "click",
        element_name: str = "",
        element_index: int = -1,
    ) -> ToolResponse:
        return fail(
            new_id("req"),
            "UITOOLKIT_DISABLED",
            "UIToolkit Bridge commands are disabled in this build.",
            {},
        )

    async def drag_drop(
        self,
        source_window: str,
        target_window: str,
        drag_type: str,
        from_x: float,
        from_y: float,
        to_x: float,
        to_y: float,
        asset_paths: list[str] | None = None,
        game_object_ids: list[int] | None = None,
        custom_data: str = "",
        modifiers: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload = {
            "sourceWindow": source_window,
            "targetWindow": target_window,
            "dragType": drag_type,
            "fromX": from_x,
            "fromY": from_y,
            "toX": to_x,
            "toY": to_y,
            "assetPaths": asset_paths or [],
            "gameObjectIds": game_object_ids or [],
            "customData": custom_data,
            "modifiers": modifiers or [],
        }
        return await self.dispatcher.call(request_id, "dragdrop.execute", payload)

    async def keyboard_event(
        self,
        action: str,
        target_window: str,
        key_code: str = "",
        character: str = "",
        text: str = "",
        modifiers: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload = {
            "action": action,
            "targetWindow": target_window,
            "keyCode": key_code,
            "character": character,
            "text": text,
            "modifiers": modifiers or [],
        }
        return await self.dispatcher.call(request_id, "keyboard.event", payload)

    async def editor_windows_list(
        self, type_filter: str = "", title_filter: str = ""
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if type_filter:
            payload["typeFilter"] = type_filter
        if title_filter:
            payload["titleFilter"] = title_filter
        return await self.dispatcher.call(request_id, "editor.windows.list", payload)

    async def editor_window_close(
        self, window_title: str, match_mode: str = "exact"
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "editor.window.close",
            {
                "windowTitle": window_title,
                "matchMode": match_mode,
            },
        )

    async def editor_window_set_rect(
        self,
        window_title: str,
        x: float,
        y: float,
        width: float,
        height: float,
        match_mode: str = "exact",
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "editor.window.setRect",
            {
                "windowTitle": window_title,
                "matchMode": match_mode,
                "x": x,
                "y": y,
                "width": width,
                "height": height,
            },
        )

    async def console_get_logs(
        self, log_type: str = "", count: int = 100
    ) -> ToolResponse:
        # Compatibility for internal E2E helpers. The public MCP tool was
        # replaced by mark/tail/search to avoid fixed-window log loss.
        request_id = new_id("req")
        payload: dict = {
            "count": max(1, min(count, 5000)),
            "newestFirst": True,
            "excludeUPilot": False,
            "includeStackTrace": True,
        }
        if log_type:
            payload["logType"] = log_type
        return await self.dispatcher.call(request_id, "console.logs.search", payload)

    async def console_mark_logs(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "console.logs.mark", {})

    async def console_tail_logs(
        self,
        cursor: int = -1,
        count: int = 200,
        log_type: str = "",
        include_stack_trace: bool = False,
        exclude_upilot: bool = True,
        contains: list[str] | None = None,
        contains_all: bool = False,
        regex: str = "",
        newest_first: bool = False,
        max_message_length: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "cursor": cursor,
            "count": max(1, min(count, 5000)),
            "includeStackTrace": include_stack_trace,
            "excludeUPilot": exclude_upilot,
            "containsAll": contains_all,
            "newestFirst": newest_first,
            "maxMessageLength": max(0, max_message_length),
        }
        if log_type:
            payload["logType"] = log_type
        if contains:
            payload["contains"] = contains
        if regex:
            payload["regex"] = regex
        return await self.dispatcher.call(request_id, "console.logs.tail", payload)

    async def console_search_logs(
        self,
        count: int = 200,
        log_type: str = "",
        include_stack_trace: bool = False,
        exclude_upilot: bool = True,
        contains: list[str] | None = None,
        contains_all: bool = False,
        regex: str = "",
        newest_first: bool = True,
        max_message_length: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "count": max(1, min(count, 5000)),
            "includeStackTrace": include_stack_trace,
            "excludeUPilot": exclude_upilot,
            "containsAll": contains_all,
            "newestFirst": newest_first,
            "maxMessageLength": max(0, max_message_length),
        }
        if log_type:
            payload["logType"] = log_type
        if contains:
            payload["contains"] = contains
        if regex:
            payload["regex"] = regex
        return await self.dispatcher.call(request_id, "console.logs.search", payload)

    async def console_clear(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "console.clear", {})

    async def selection_get(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "selection.get", {})

    async def selection_set(
        self,
        game_object_ids: list[int] | None = None,
        asset_paths: list[str] | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if game_object_ids:
            payload["gameObjectIds"] = game_object_ids
        if asset_paths:
            payload["assetPaths"] = asset_paths
        return await self.dispatcher.call(request_id, "selection.set", payload)

    async def selection_clear(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "selection.clear", {})

    async def editor_delay(self, delay_ms: int) -> ToolResponse:
        """Main-thread delay in Unity Editor (for UIToolkit layout; M26 E2E)."""
        request_id = new_id("req")
        dm = max(0, min(int(delay_ms), 120000))
        return await self.dispatcher.call(
            request_id,
            "editor.delay",
            {"delayMs": dm},
            timeout_ms=dm + 30000,
        )

    async def editor_undo(self, steps: int = 1) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "editor.undo", {"steps": steps})

    async def editor_redo(self, steps: int = 1) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "editor.redo", {"steps": steps})

    async def editor_execute_command(self, command_name: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "editor.executeCommand", {"commandName": command_name}
        )

    async def sceneview_navigate(
        self,
        look_at_instance_id: int = 0,
        pivot: dict | None = None,
        size: float = -1,
        rotation: dict | None = None,
        orthographic: bool | None = None,
        in_2d_mode: bool | None = None,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if look_at_instance_id:
            payload["lookAtInstanceId"] = look_at_instance_id
        if pivot is not None:
            payload["pivot"] = pivot
        if size >= 0:
            payload["size"] = size
        if rotation is not None:
            payload["rotation"] = rotation
        if orthographic is not None:
            payload["orthographic"] = 1 if orthographic else 0
        if in_2d_mode is not None:
            payload["in2DMode"] = 1 if in_2d_mode else 0
        return await self.dispatcher.call(request_id, "sceneview.navigate", payload)

