from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import hmac
import json
import logging
import os
import shlex
import secrets
import subprocess
import sys
import time
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from ..config import CONFIG, diagnose_client_configs, refresh_config_if_changed
from ..automation_authorization import authorization_status
from ..dispatcher import CommandDispatcher
from ..env import getenv
from ..models import ToolResponse
from ..protocol import new_id, now_ms
from ..responses import fail, ok
from ..tool_registry import (
    REGISTRY,
    REGISTRY_VERSION,
    dispatch_public_tool,
    proxy_argument_schema,
)

logger = logging.getLogger("upilot.mcp")
_KEYBOARD_ACTIONS = ("keydown", "keyup", "keypress", "type")
_MIN_PLACEHOLDER_PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
_CAPTURE_OWNER_SECRET_KEYS = {"ownertoken", "ownertokensha256", "ownerhash"}


def _is_capture_owner_secret_key(key: object) -> bool:
    # Bridge compatibility payloads have used camelCase, but error/detail
    # producers are not guaranteed to preserve that spelling.  Treat harmless
    # separators as equivalent so an ``owner_token`` alias cannot escape.
    normalized = "".join(char for char in str(key).lower() if char.isalnum())
    return normalized in _CAPTURE_OWNER_SECRET_KEYS


def _without_capture_owner_secrets(value):
    """Do not let Bridge/manifest compatibility fields expose capture ownership."""
    if isinstance(value, dict):
        return {
            str(key): _without_capture_owner_secrets(child)
            for key, child in value.items()
            if not _is_capture_owner_secret_key(key)
        }
    if isinstance(value, list):
        return [_without_capture_owner_secrets(item) for item in value]
    return value


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
    _UNITY_WINDOW_CLASSES = ("UnityContainerWndClass", "UnityWndClass")

    @staticmethod
    def _process_exists(pid: int) -> bool:
        if pid <= 0:
            return False
        if sys.platform == "win32":
            import ctypes
            from ctypes import wintypes

            process_query_limited_information = 0x1000
            error_access_denied = 5
            still_active = 259
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
            kernel32.GetExitCodeProcess.restype = wintypes.BOOL
            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel32.CloseHandle.restype = wintypes.BOOL

            handle = kernel32.OpenProcess(
                process_query_limited_information,
                False,
                pid,
            )
            if not handle:
                return ctypes.get_last_error() == error_access_denied
            try:
                exit_code = wintypes.DWORD()
                if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                    # A valid process handle proves that the process existed at
                    # query time even if its exit state cannot be inspected.
                    return True
                return exit_code.value == still_active
            finally:
                kernel32.CloseHandle(handle)
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            # The process identity is still known even when the current user
            # cannot inspect it further.
            return True
        except OSError:
            return False
        return True

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
                execution = self.server.state.execution_state()
                return ok(
                    request_id,
                    {
                        **execution,
                        "connected": True,
                        "isCompiling": state["isCompiling"],
                        "playModeState": state["playModeState"],
                        "activeScene": state["activeScene"],
                        "isPlaying": state["isPlaying"],
                        "isPaused": state["isPaused"],
                        "source": "resource.editorState",
                    },
                )

        execution = self.server.state.execution_state()
        connected = bool(execution["unityConnected"] or self.server.is_ready())
        return ok(
            request_id,
            {
                **execution,
                "connected": connected,
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
        connected = self.server.session_manager.is_connected()
        bridge_reported_process_id = int(session.process_id if session else 0)
        reported_process_id = bridge_reported_process_id
        process_identity: dict = {}
        startup_root = self._status_project_root()
        if (force_fresh or not connected or self._startup_record_exists(startup_root)) and hasattr(self, "_resolve_live_unity_pid"):
            try:
                resolved_process_id, process_identity = await asyncio.to_thread(
                    self._get_verified_unity_process_identity,
                    force_fresh,
                )
                reported_process_id = int(resolved_process_id or 0)
            except Exception as ex:
                reported_process_id = 0
                process_identity = {
                    "projectIdentityVerified": False,
                    "reason": "process_identity_check_failed",
                    "processQuerySucceeded": False,
                    "processQueryError": str(ex),
                    "resolvedProcessId": 0,
                }
        compile_state = self.server.state.compile
        unity_abs = ""
        if session and session.project_path:
            try:
                unity_abs = str(Path(session.project_path).resolve())
            except OSError:
                unity_abs = session.project_path
        elif startup_root is not None:
            unity_abs = str(startup_root)

        execution = self.server.state.execution_state()
        unity_runtime_identity = {
            "processId": reported_process_id,
            "bridgeSessionId": session.session_id if session else "",
        }
        if process_identity:
            unity_runtime_identity.update({
                "verified": bool(process_identity.get("projectIdentityVerified")),
                "processRole": str(process_identity.get("processRole") or ""),
            })
        data = {
                "connected": connected,
                "playModeTransition": getattr(self.server.state, "playmode_transition", {}),
                "serverReady": self.server.is_ready(),
                "session": {
                    "sessionId": session.session_id if session else "",
                    "projectPath": session.project_path if session else "",
                    "unityVersion": session.unity_version if session else "",
                    "platform": session.platform if session else "",
                    "processId": reported_process_id,
                    "reportedProcessId": bridge_reported_process_id,
                    "lastHeartbeatAt": session.last_heartbeat_at if session else 0,
                },
                "paths": {
                    "unityProjectAbsolute": unity_abs,
                    "mcpProcessWorkingDirectory": str(Path.cwd().resolve()),
                },
                "compile": {
                    "status": compile_state.status,
                    "phase": execution["compilePhase"],
                    "errorCount": compile_state.error_count,
                    "warningCount": compile_state.warning_count,
                    "startedAt": compile_state.started_at,
                    "finishedAt": compile_state.finished_at,
                },
                "editor": live_state or {
                    **execution,
                    "source": "cache",
                },
                "executionState": execution,
                "runtimeIdentity": {
                    "unityEditor": unity_runtime_identity,
                    "mcpServer": {
                        "processId": os.getpid(),
                    },
                    "managedDomain": {
                        "producerEpoch": str(execution.get("producerEpoch") or ""),
                        "domainGeneration": int(execution.get("domainGeneration") or 0),
                    },
                },
                "processIdentity": process_identity,
                "startup": self._read_startup_summary(startup_root, process_identity),
                "serverRestart": self._read_server_restart_summary(startup_root, session),
                "timeouts": self.dispatcher.timeout_policy_snapshot(),
                "mcp": {
                    "label": self.server.mcp_label,
                    "host": self.server.host,
                    "port": self.server.port,
                },
                "automationAuthorization": {
                    **authorization_status(
                        CONFIG.automation_authorization_scopes,
                        CONFIG.automation_authorization_catalog_hash,
                        CONFIG.automation_authorization_scope_version,
                    ),
                    "approvedAtUtc": CONFIG.automation_authorization_approved_at_utc,
                },
            }
        if include_capabilities:
            tools = self._registry_tools_snapshot(limit=200)
            data["capabilities"] = {
                "registryVersion": REGISTRY_VERSION,
                "toolCount": len([item for item in tools if item.get("available")]),
                "registeredToolCount": len(REGISTRY.list()),
                "reflectionCallAvailable": REGISTRY.resolve("unity_reflection_call") is not None,
                "screenshotSaveAvailable": REGISTRY.resolve("unity_screenshot_save") is not None,
                "asyncTaskAvailable": REGISTRY.resolve("unity_task_start") is not None,
                "states": {
                    "serviceRegistered": True,
                    "clientToolListInjected": None,
                    "serverReady": self.server.is_ready(),
                    "connected": self.server.session_manager.is_connected(),
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
        tools = self._registry_tools_snapshot(limit=200)
        execution_capabilities: dict = {}
        if self.server.session_manager.is_connected():
            try:
                execution_response = await self.execution_capabilities()
                if execution_response.ok:
                    execution_capabilities = dict(execution_response.data or {})
                elif execution_response.error is not None:
                    execution_capabilities = {
                        "available": False,
                        "unavailableReason": execution_response.error.code,
                        "detail": execution_response.error.message,
                    }
            except Exception as ex:
                execution_capabilities = {
                    "available": False,
                    "unavailableReason": "EXECUTION_CAPABILITY_QUERY_FAILED",
                    "detail": str(ex),
                }
        return ok(
            status.request_id,
            {
                "registryVersion": REGISTRY_VERSION,
                "tools": tools,
                "capabilities": data.get("capabilities", {}),
                "execution": execution_capabilities,
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
        items = self._registry_tools_snapshot(
            query=query,
            category=category,
            availability=availability,
            limit=limit,
        )
        exact_matches = [item for item in items if item.get("exactMatch")]
        approximate_matches = [item for item in items if not item.get("exactMatch")]
        return ok(
            new_id("req"),
            {
                "count": len(items),
                "tools": items,
                "exactMatch": bool(exact_matches),
                "exactMatches": exact_matches,
                "approximateMatches": approximate_matches,
            },
        )

    async def tool_call(self, tool_name: str, args: dict | None = None) -> ToolResponse:
        """Call a registered public tool when the MCP client did not inject its typed wrapper."""
        requested = str(tool_name or "").strip()
        if not requested:
            return fail(new_id("req"), "TOOL_NAME_REQUIRED", "toolName is required.", {})
        if requested == "unity_tool_call":
            return fail(
                new_id("req"),
                "RECURSIVE_TOOL_CALL",
                "unity_tool_call cannot call itself.",
                {"tool": requested},
            )
        descriptor = REGISTRY.resolve(requested)
        if descriptor is None:
            return fail(
                new_id("req"),
                "UNKNOWN_TOOL",
                f"Unknown MCP tool: {requested}",
                {"tool": requested, "nextAction": "Call unity_tools_find with the exact tool name."},
            )
        return await dispatch_public_tool(self, requested, dict(args or {}))

    def _registry_tools_snapshot(
        self,
        query: str = "",
        category: str = "",
        availability: str = "all",
        limit: int = 20,
    ) -> list[dict]:
        refresh_config_if_changed()
        items = REGISTRY.find(
            query=query,
            category=category,
            availability=availability,
            limit=limit,
            flow_enabled=CONFIG.flow_enabled,
            connected=self.server.session_manager.is_connected(),
            server_ready=self.server.is_ready(),
            write_access_approved=CONFIG.write_access_approved,
        )
        for item in items:
            method = getattr(self, str(item.get("facade_method") or ""), None)
            if method is not None:
                item["proxyArguments"] = proxy_argument_schema(
                    method, str(item.get("name") or "")
                )
        return items

    def _last_command_succeeded(self, command_name: str) -> bool | None:
        matches = [item for item in self.server.state.commands.values() if item.name == command_name]
        if not matches:
            return None
        latest = max(matches, key=lambda item: item.created_at)
        return latest.status == "success"

    async def client_config_diagnose(self) -> ToolResponse:
        return ok(new_id("req"), diagnose_client_configs())

    def _status_project_root(self) -> Path | None:
        session = self.server.session_manager.active
        raw = str(session.project_path if session and session.project_path else "").strip()
        if not raw:
            raw = str(getattr(self.server.state, "project_path", "") or "").strip()
        if not raw:
            candidate = Path.cwd()
            if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings").is_dir():
                raw = str(candidate)
        if not raw:
            return None
        try:
            return Path(raw).resolve()
        except OSError:
            return None

    @staticmethod
    def _startup_record_exists(project_root: Path | None) -> bool:
        return bool(project_root and (project_root / "Library" / "UPilot" / "startup.json").is_file())

    @staticmethod
    def _same_path(left: object, right: object) -> bool:
        try:
            return os.path.normcase(str(Path(str(left)).resolve())) == os.path.normcase(str(Path(str(right)).resolve()))
        except (OSError, ValueError):
            return False

    def _get_verified_unity_process_identity(self, force_refresh: bool = False) -> tuple[int, dict]:
        root = self._status_project_root()
        cached = getattr(self, "_verified_unity_process_cache", None)
        if not force_refresh and cached and self._same_path(cached.get("projectPath"), root):
            pid = int(cached.get("resolvedProcessId") or 0)
            expected_created_at = int(cached.get("processCreatedAt") or 0)
            observed_created_at = self._process_creation_time(pid) if pid > 0 else 0
            if expected_created_at > 0 and observed_created_at == expected_created_at:
                return pid, {**cached, "identityCacheHit": True}
            self._verified_unity_process_cache = None

        pid, diagnostics = self._resolve_live_unity_pid()
        diagnostics = dict(diagnostics or {})
        if pid > 0 and diagnostics.get("projectIdentityVerified"):
            diagnostics["projectPath"] = str(root or diagnostics.get("projectPath") or "")
            diagnostics["identityCacheHit"] = False
            self._verified_unity_process_cache = dict(diagnostics)
        return int(pid or 0), diagnostics

    @classmethod
    def _select_unity_window(cls, windows: list[dict]) -> tuple[dict | None, str]:
        eligible = [
            item for item in windows
            if item.get("visible") and item.get("className") in cls._UNITY_WINDOW_CLASSES
        ]
        main_title = [
            item for item in eligible
            if " - Unity " in str(item.get("title") or "") or str(item.get("title") or "").startswith("Unity ")
        ]
        if len(main_title) == 1:
            return main_title[0], "verified_pid_unique_main_title"
        if len(main_title) > 1:
            return None, "ambiguous_main_windows"
        if len(eligible) == 1:
            return eligible[0], "verified_pid_unique_unity_window"
        if not eligible:
            return None, "window_not_found_for_verified_pid"
        return None, "ambiguous_unity_windows"

    @staticmethod
    def _enumerate_windows_for_pid(pid: int) -> list[dict]:
        if sys.platform != "win32" or pid <= 0:
            return []
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
        user32.EnumWindows.restype = wintypes.BOOL
        user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        user32.IsWindowVisible.argtypes = [wintypes.HWND]
        user32.IsWindowVisible.restype = wintypes.BOOL
        user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
        user32.GetWindowTextLengthW.restype = ctypes.c_int
        user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        user32.GetWindowTextW.restype = ctypes.c_int
        user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        user32.GetClassNameW.restype = ctypes.c_int
        windows: list[dict] = []
        def callback(hwnd, _lparam):
            owner_pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner_pid))
            if owner_pid.value != pid:
                return True
            title_length = max(0, user32.GetWindowTextLengthW(hwnd))
            title_buffer = ctypes.create_unicode_buffer(title_length + 1)
            user32.GetWindowTextW(hwnd, title_buffer, len(title_buffer))
            class_buffer = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, class_buffer, len(class_buffer))
            windows.append({
                "hwnd": int(hwnd),
                "title": title_buffer.value,
                "className": class_buffer.value,
                "visible": bool(user32.IsWindowVisible(hwnd)),
            })
            return True

        callback_ref = callback_type(callback)
        if not user32.EnumWindows(callback_ref, 0):
            raise OSError(ctypes.get_last_error(), "EnumWindows failed")
        return windows

    def _resolve_verified_unity_window(self, force_refresh: bool = False) -> dict:
        if sys.platform != "win32":
            return {"ok": False, "reason": "platform_not_supported", "projectIdentityVerified": False}
        try:
            pid, identity = self._get_verified_unity_process_identity(force_refresh)
        except Exception as ex:
            return {"ok": False, "reason": "process_identity_check_failed", "detail": str(ex), "projectIdentityVerified": False}
        if pid <= 0 or not identity.get("projectIdentityVerified"):
            return {
                "ok": False,
                "reason": str(identity.get("reason") or "process_identity_unverified"),
                "targetPid": 0,
                "projectIdentityVerified": False,
                "identity": identity,
            }
        try:
            windows = self._enumerate_windows_for_pid(pid)
        except Exception as ex:
            return {
                "ok": False,
                "reason": "window_enumeration_failed",
                "detail": str(ex),
                "targetPid": pid,
                "projectIdentityVerified": True,
                "identity": identity,
            }
        selected, basis = self._select_unity_window(windows)
        if selected is None:
            return {
                "ok": False,
                "reason": basis,
                "targetPid": pid,
                "processCreatedAt": int(identity.get("processCreatedAt") or 0),
                "projectIdentityVerified": True,
                "candidateCount": len(windows),
                "identity": identity,
            }
        return {
            "ok": True,
            "reason": "",
            "hwnd": int(selected["hwnd"]),
            "title": str(selected.get("title") or ""),
            "className": str(selected.get("className") or ""),
            "targetPid": pid,
            "processCreatedAt": int(identity.get("processCreatedAt") or 0),
            "projectPath": str(identity.get("projectPath") or self._status_project_root() or ""),
            "projectIdentityVerified": True,
            "windowMatch": basis,
            "candidateCount": len(windows),
            "identityCacheHit": bool(identity.get("identityCacheHit")),
        }

    @staticmethod
    def _window_process_id(hwnd: int) -> int:
        if sys.platform != "win32" or not hwnd:
            return 0
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return int(pid.value)

    def _revalidate_window_target(self, target: dict) -> tuple[bool, str]:
        pid = int(target.get("targetPid") or 0)
        created_at = int(target.get("processCreatedAt") or 0)
        hwnd = int(target.get("hwnd") or 0)
        if pid <= 0 or created_at <= 0 or hwnd <= 0:
            return False, "target_identity_incomplete"
        if self._process_creation_time(pid) != created_at:
            self._verified_unity_process_cache = None
            return False, "process_identity_changed"
        if self._window_process_id(hwnd) != pid:
            return False, "window_owner_changed"
        return True, ""

    @staticmethod
    def _post_window_message(hwnd: int) -> bool:
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
        user32.PostMessageW.restype = wintypes.BOOL
        return bool(user32.PostMessageW(hwnd, 0x0000, 0, 0))

    @staticmethod
    def _foreground_window() -> int:
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.GetForegroundWindow.argtypes = []
        user32.GetForegroundWindow.restype = wintypes.HWND
        return int(user32.GetForegroundWindow() or 0)

    @staticmethod
    def _window_text(hwnd: int) -> str:
        if sys.platform != "win32" or not hwnd:
            return ""
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
        user32.GetWindowTextLengthW.restype = ctypes.c_int
        user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        user32.GetWindowTextW.restype = ctypes.c_int
        length = max(0, user32.GetWindowTextLengthW(hwnd))
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, len(buffer))
        return buffer.value

    @staticmethod
    def _show_and_focus_window(hwnd: int) -> bool:
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        user32.ShowWindow.restype = wintypes.BOOL
        user32.SetForegroundWindow.argtypes = [wintypes.HWND]
        user32.SetForegroundWindow.restype = wintypes.BOOL
        user32.ShowWindow(hwnd, 9)
        return bool(user32.SetForegroundWindow(hwnd))

    def _find_unity_hwnd(self) -> int:
        target = self._resolve_verified_unity_window()
        return int(target.get("hwnd") or 0) if target.get("ok") else 0

    def _wake_unity_editor(self) -> bool:
        """Windows: post a harmless WM_NULL to the Unity Editor window to prevent background throttling.

        Uses PostMessageW so we do NOT steal foreground focus or interrupt user typing.
        """
        target = self._resolve_verified_unity_window()
        if not target.get("ok"):
            return False
        try:
            valid, _ = self._revalidate_window_target(target)
            return bool(valid and self._post_window_message(int(target["hwnd"])))
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
        target = self._resolve_verified_unity_window()
        if not target.get("ok"):
            return fail(
                request_id,
                "WINDOW_NOT_FOUND",
                "未找到经过工程与进程身份验证的 Unity Editor 主窗口",
                target,
            )
        try:
            valid, reason = self._revalidate_window_target(target)
            if not valid:
                return fail(request_id, "WINDOW_IDENTITY_CHANGED", "Unity 窗口身份在激活前已变化", {**target, "reason": reason})
            hwnd = int(target["hwnd"])
            result = self._show_and_focus_window(hwnd)
            foreground_hwnd = self._foreground_window()
            foreground_pid = self._window_process_id(foreground_hwnd)
            focused = foreground_pid == int(target["targetPid"])
            data = {
                "focused": focused,
                "hwnd": hwnd,
                "setForegroundResult": result,
                "foregroundHwnd": foreground_hwnd,
                "foregroundPid": foreground_pid,
                **target,
            }
            if not result or not focused:
                return fail(request_id, "FOCUS_NOT_ACQUIRED", "Windows 未将目标 Unity 进程置于前台", data)
            return ok(
                request_id,
                data,
            )
        except Exception as ex:
            return fail(
                request_id,
                "FOCUS_FAILED",
                f"设置 Unity 焦点失败: {ex}",
                target,
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
        target = self._resolve_verified_unity_window()
        if not target.get("ok"):
            return fail(
                request_id,
                "WINDOW_NOT_FOUND",
                "未找到经过工程与进程身份验证的 Unity Editor 主窗口",
                target,
            )
        try:
            valid, reason = self._revalidate_window_target(target)
            if not valid:
                return fail(request_id, "WINDOW_IDENTITY_CHANGED", "Unity 窗口身份在查询前已变化", {**target, "reason": reason})
            hwnd = int(target["hwnd"])
            fg_hwnd = self._foreground_window()
            foreground_pid = self._window_process_id(fg_hwnd)

            return ok(
                request_id,
                {
                    "unityFocused": foreground_pid == int(target["targetPid"]),
                    "unityHwnd": hwnd,
                    "unityTitle": target["title"],
                    "unityClass": target["className"],
                    "foregroundHwnd": fg_hwnd,
                    "foregroundTitle": self._window_text(fg_hwnd),
                    "foregroundPid": foreground_pid,
                    **target,
                },
            )
        except Exception as ex:
            return fail(
                request_id,
                "QUERY_FAILED",
                f"查询焦点状态失败: {ex}",
                {},
            )

    def _read_startup_summary(self, project_root: Path | None, process_identity: dict) -> dict:
        source = "Library/UPilot/startup.json"
        if project_root is None:
            return {"recordStatus": "project_unknown", "current": False, "source": source}
        path = project_root / "Library" / "UPilot" / "startup.json"
        if not path.is_file():
            return {"recordStatus": "missing", "current": False, "source": source}
        try:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(record, dict):
                raise ValueError("startup record is not an object")
        except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as ex:
            return {"recordStatus": "corrupt", "current": False, "source": source, "error": str(ex)[:512]}

        record_pid = int(record.get("processId") or 0)
        record_created_at = int(record.get("processCreatedAt") or 0)
        resolved_pid = int(process_identity.get("resolvedProcessId") or 0)
        resolved_created_at = int(process_identity.get("processCreatedAt") or 0)
        project_matches = self._same_path(record.get("projectPath"), project_root)
        identity_verified = bool(
            process_identity.get("projectIdentityVerified")
            and project_matches
            and record_pid > 0
            and record_pid == resolved_pid
            and record_created_at > 0
            and record_created_at == resolved_created_at
        )
        if identity_verified:
            record_status = "current"
        elif record_pid > 0 and not self._process_exists(record_pid):
            record_status = "process_exited"
        elif not project_matches:
            record_status = "project_mismatch"
        else:
            record_status = "historical"

        milestones = record.get("milestones") if isinstance(record.get("milestones"), list) else []
        observed = [str(item.get("name")) for item in milestones if isinstance(item, dict) and item.get("name")]
        expected = ["bootstrap_entered", "first_editor_update", "server_healthy", "bridge_authenticated", "editor_ready"]
        return {
            "recordStatus": record_status,
            "current": identity_verified,
            "source": source,
            "identity": {
                "projectPath": str(record.get("projectPath") or ""),
                "processId": record_pid,
                "processCreatedAt": record_created_at,
                "verified": identity_verified,
            },
            "observedMilestones": observed,
            "missingStages": [name for name in expected if name not in observed],
            "milestones": milestones,
            "blockingReasons": record.get("blockingReasons") if isinstance(record.get("blockingReasons"), list) else [],
            "healthObservationStatus": str(record.get("healthObservationStatus") or "not_started"),
            "diagnosticsStartedAtUtcMs": int(record.get("diagnosticsStartedAtUtcMs") or 0),
            "healthObservationStartedAtUtcMs": int(record.get("healthObservationStartedAtUtcMs") or 0),
            "healthProbeCount": int(record.get("healthProbeCount") or 0),
            "observedServerProcessId": int(record.get("observedServerProcessId") or 0),
            "serverStartRetry": {
                "cycleId": str(record.get("serverStartCycleId") or ""),
                "status": str(record.get("serverStartRetryStatus") or "unknown"),
                "attemptCount": int(record.get("serverStartAttemptCount") or 0),
                "lastAttemptAtUtcMs": int(record.get("lastServerStartAttemptAtUtcMs") or 0),
                "nextAttemptAtUtcMs": int(record.get("nextServerStartAttemptAtUtcMs") or 0),
                "lastReason": str(record.get("lastServerStartReason") or ""),
            },
            "backgroundExecution": record.get("backgroundExecution") if isinstance(record.get("backgroundExecution"), dict) else {},
            "observedAtUtcMs": int(record.get("observedAtUtcMs") or 0),
            "historicalEditorReady": "editor_ready" in observed,
        }

    def _read_server_restart_summary(self, project_root: Path | None, session: object | None) -> dict:
        source = "Library/UPilot/server-restart.json"
        if project_root is None:
            return {"recordStatus": "project_unknown", "current": False, "source": source}
        path = project_root / "Library" / "UPilot" / "server-restart.json"
        if not path.is_file():
            return {"recordStatus": "missing", "current": False, "source": source}
        try:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(record, dict):
                raise ValueError("server restart record is not an object")
        except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as ex:
            return {
                "recordStatus": "corrupt",
                "current": False,
                "source": source,
                "error": str(ex)[:512],
            }

        operation_id = str(record.get("operationId") or "")
        project_matches = self._same_path(record.get("projectPath"), project_root)
        new_process_id = int(record.get("newProcessId") or 0)
        process_matches = new_process_id > 0 and new_process_id == os.getpid()
        persisted_status = str(record.get("status") or "unknown")
        old_session_id = str(record.get("oldBridgeSessionId") or "")
        new_session_id = str(record.get("newBridgeSessionId") or "")
        active_session_id = str(getattr(session, "session_id", "") or "")
        bridge_matches = bool(
            new_session_id
            and new_session_id != old_session_id
            and active_session_id
            and active_session_id == new_session_id
        )
        bridge_was_replaced = bool(new_session_id and new_session_id != old_session_id)
        verified_succeeded = bool(
            persisted_status == "succeeded"
            and project_matches
            and process_matches
            and bridge_was_replaced
            and record.get("healthVerified") is True
            and record.get("projectIdentityVerified") is True
            and record.get("bridgeVerified") is True
        )
        current = bool(project_matches and process_matches)
        if not operation_id:
            record_status = "invalid"
            current = False
        elif not project_matches:
            record_status = "project_mismatch"
            current = False
        elif not process_matches:
            record_status = "historical"
            current = False
        else:
            record_status = "current"

        return {
            "recordStatus": record_status,
            "current": current,
            "source": source,
            "operationId": operation_id,
            "status": persisted_status,
            "phase": str(record.get("phase") or "unknown"),
            "verifiedSucceeded": verified_succeeded,
            "identity": {
                "projectPath": str(record.get("projectPath") or ""),
                "projectPathMatches": project_matches,
                "oldProcessId": int(record.get("oldProcessId") or 0),
                "newProcessId": new_process_id,
                "currentServerProcessId": os.getpid(),
                "processMatches": process_matches,
                "oldBridgeSessionId": old_session_id,
                "newBridgeSessionId": new_session_id,
                "activeBridgeSessionId": active_session_id,
                "bridgeWasReplaced": bridge_was_replaced,
                "bridgeSessionMatches": bridge_matches,
            },
            "verification": {
                "healthVerified": record.get("healthVerified") is True,
                "projectIdentityVerified": record.get("projectIdentityVerified") is True,
                "bridgeVerified": record.get("bridgeVerified") is True,
                "healthProjectPath": str(record.get("healthProjectPath") or ""),
            },
            "timing": {
                "requestedAtUtcMs": int(record.get("requestedAtUtcMs") or 0),
                "oldProcessStopRequestedAtUtcMs": int(record.get("oldProcessStopRequestedAtUtcMs") or 0),
                "portsReleasedAtUtcMs": int(record.get("portsReleasedAtUtcMs") or 0),
                "newProcessStartedAtUtcMs": int(record.get("newProcessStartedAtUtcMs") or 0),
                "healthVerifiedAtUtcMs": int(record.get("healthVerifiedAtUtcMs") or 0),
                "bridgeVerifiedAtUtcMs": int(record.get("bridgeVerifiedAtUtcMs") or 0),
                "endedAtUtcMs": int(record.get("endedAtUtcMs") or 0),
                "updatedAtUtcMs": int(record.get("updatedAtUtcMs") or 0),
            },
            "processExit": {
                "observed": record.get("exitObserved") is True,
                "exitCode": int(record.get("exitCode") or 0),
            },
            "errorCode": str(record.get("errorCode") or ""),
            "error": str(record.get("error") or "")[:4096],
            "stderrTail": str(record.get("stderrTail") or "")[-4096:],
            "diagnosticSource": str(record.get("diagnosticSource") or ""),
            "nextAction": str(record.get("nextAction") or ""),
        }

    # Editor state, input, windows, console, and selection operations.
    def _update_editor_cache_from_resource_state(self, data: dict) -> dict:
        is_playing = bool(data.get("isPlaying", False))
        is_paused = bool(data.get("isPaused", False))
        play_mode_state = "pause" if is_paused else ("play" if is_playing else "edit")
        active_scene = str(data.get("activeSceneName", ""))
        is_compiling = bool(data.get("isCompiling", False))
        session = self.server.session_manager.active
        self.server.state.update_editor_state(
            {
                "connected": True,
                "isCompiling": is_compiling,
                "playModeState": play_mode_state,
                "activeScene": active_scene,
                "authoritative": bool(data.get("authoritative", True)),
                "source": str(data.get("source") or "resource.editorState"),
                "sessionId": str(data.get("sessionId") or (session.session_id if session else "")),
                "updatedAt": int(data.get("updatedAt") or now_ms()),
                "lastMainThreadPumpAt": int(data.get("lastMainThreadPumpAt") or 0),
                "mainThreadQueueDepth": int(data.get("mainThreadQueueDepth") or 0),
                "processId": int(data.get("processId") or (session.process_id if session else 0)),
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
        connection_transition = bool(
            not result.ok
            and result.error
            and result.error.code in ("CONNECTION_LOST", "DOMAIN_RELOAD_TIMEOUT")
        )
        if not result.ok and not connection_transition:
            return result

        confirmed = await self._wait_for_playmode_state("play", timeout_s=30.0)
        if confirmed is not None:
            state, context = confirmed
            data = dict(result.data or {}) if result.ok else {}
            data.update(
                {
                    "confirmed": True,
                    "playModeState": "play",
                    "editorState": state,
                    "reconnected": connection_transition,
                }
            )
            return ok(request_id, data, context=context)

        return fail(
            request_id,
            "PLAYMODE_START_TIMEOUT",
            "Unity did not confirm PlayMode within 30 seconds.",
            {
                "confirmed": False,
                "nextAction": "Call unity_mcp_status and inspect the authoritative Editor context.",
            },
        )

    async def playmode_stop(self) -> ToolResponse:
        request_id = new_id("req")
        result = await self.dispatcher.call(
            request_id, "playmode.set", {"action": "stop"}
        )
        if not result.ok:
            return result
        confirmed = await self._wait_for_playmode_state("edit", timeout_s=30.0)
        if confirmed is not None:
            state, context = confirmed
            data = dict(result.data or {})
            data.update({"confirmed": True, "playModeState": "edit", "editorState": state})
            return ok(request_id, data, context=context)
        return fail(
            request_id,
            "PLAYMODE_STOP_TIMEOUT",
            "Unity did not confirm EditMode within 30 seconds.",
            {
                "confirmed": False,
                "nextAction": "Call unity_mcp_status and inspect the authoritative Editor context.",
            },
        )

    async def playmode_pause(self, *, wait: bool = True, timeout_ms: int = 5000) -> ToolResponse:
        return await self._set_pause_state(True, wait=wait, timeout_ms=timeout_ms)

    async def playmode_resume(self, *, wait: bool = True, timeout_ms: int = 5000) -> ToolResponse:
        return await self._set_pause_state(False, wait=wait, timeout_ms=timeout_ms)

    async def _set_pause_state(self, paused: bool, *, wait: bool, timeout_ms: int) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(wait, bool):
            return fail(request_id, "INVALID_PAYLOAD", "wait must be a boolean.", {
                "field": "wait", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })
        if not isinstance(timeout_ms, int) or isinstance(timeout_ms, bool) or timeout_ms < 1 or timeout_ms > 30000:
            return fail(request_id, "INVALID_PAYLOAD", "timeoutMs must be an integer between 1 and 30000.", {
                "field": "timeoutMs", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })

        preflight = self._pause_preflight(request_id, paused)
        if preflight is not None:
            return preflight
        target = "pause" if paused else "play"
        result = await self.dispatcher.call(
            request_id,
            "playmode.set",
            {"action": "pause" if paused else "resume"},
            timeout_ms=timeout_ms,
        )
        if not result.ok:
            return result
        data = dict(result.data or {})
        command_observed_state = data.get("observedState") or data.get("state")
        data.update({
            "commandId": str(data.get("commandId") or result.request_id or request_id),
            "requestedState": {"isPaused": paused},
            "commandSubmitted": True,
            "changed": bool(data.get("changed", True)),
            "writeCount": int(data.get("writeCount", 1 if data.get("changed", True) else 0)),
            "commandObservedState": command_observed_state,
        })
        if not wait:
            data.update({"stateObserved": command_observed_state is not None, "confirmed": False, "terminal": False,
                         "nextAction": "Query the original command or unity_mcp_status; do not submit the action again."})
            return ok(request_id, data, context=result.context)
        confirmed = await self._wait_for_playmode_state(
            target, timeout_s=timeout_ms / 1000.0, require_fresh=True
        )
        if confirmed is not None:
            state, context = confirmed
            data.update({"stateObserved": True, "confirmed": True, "terminal": True,
                         "observedState": {"isPaused": state.get("isPaused", False)}, "editorState": state})
            return ok(request_id, data, context=context)
        data.update({"stateObserved": command_observed_state is not None, "confirmed": False, "terminal": False,
                     "waitWindowElapsed": True, "nextAction": "Query the original command or unity_mcp_status; do not submit the action again."})
        return fail(request_id, "PLAYMODE_PAUSE_TIMEOUT", "Unity did not authoritatively confirm the requested pause state.", data)

    def _pause_preflight(self, request_id: str, paused: bool) -> ToolResponse | None:
        """Reject unsafe pause writes from the current authoritative snapshot.

        This uses only the server-owned latest snapshot. It must not query or
        enqueue ``playmode.set`` while the snapshot is stale or transitioning:
        waiting for a later mode and then writing would turn an explicit pause
        request into an unexpected side effect.
        """
        state_store = getattr(getattr(self, "server", None), "state", None)
        execution_state = getattr(state_store, "execution_state", None)
        if not callable(execution_state):
            # Small legacy/unit-test hosts may not expose the state store. The
            # Bridge remains the authoritative preflight in that compatibility
            # path; do not invent a cached success here.
            return None
        state = execution_state()
        if not isinstance(state, dict):
            return None
        detail = {
            "executionState": state,
            "commandSubmitted": False,
            "sideEffectsMayHaveOccurred": False,
        }
        if not bool(state.get("authoritative")) or bool(state.get("isStale")):
            return fail(
                request_id, "EDITOR_STATE_NOT_READY",
                "A fresh authoritative Editor state is required before changing PlayMode pause state.",
                detail,
            )
        transition = "".join(ch for ch in str(state.get("transition") or "").lower() if ch.isalnum())
        if bool(state.get("isCompiling")) or transition in {
            "enteringplaymode", "exitingplaymode", "playmodetransition", "transitioning",
        }:
            return fail(
                request_id, "PLAYMODE_TRANSITION_IN_PROGRESS",
                "PlayMode state is changing or compilation is in progress; no pause command was submitted.",
                detail,
            )
        play_state = str(state.get("playModeState") or "unknown").lower()
        if play_state == "edit":
            return fail(
                request_id, "PLAYMODE_REQUIRED",
                "Pause and resume require PlayMode; this request will not start PlayMode.",
                detail,
            )
        if play_state not in {"play", "pause"}:
            return fail(
                request_id, "EDITOR_STATE_NOT_READY",
                "The current PlayMode state is not safe to change.",
                detail,
            )
        target_state = "pause" if paused else "play"
        if play_state == target_state:
            observed = {"isPaused": paused}
            return ok(request_id, {
                "commandId": request_id,
                "requestedState": observed,
                "observedState": observed,
                "commandSubmitted": False,
                "stateObserved": True,
                "confirmed": True,
                "terminal": True,
                "changed": False,
                "writeCount": 0,
                "sideEffectsMayHaveOccurred": False,
                "editorState": state,
            })
        return None

    async def _wait_for_playmode_state(
        self, target_state: str, *, timeout_s: float, require_fresh: bool = False
    ) -> tuple[dict, dict | None] | None:
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            if self.server.session_manager.is_connected():
                remaining_ms = max(1, int((deadline - time.monotonic()) * 1000))
                state_result = await self.dispatcher.call(
                    new_id("req"), "resource.editorState", {}, timeout_ms=min(5000, remaining_ms)
                )
                fresh_enough = bool(
                    state_result.data
                    and (
                        state_result.data.get("authoritative") is True
                        and state_result.data.get("isStale") is False
                        if require_fresh
                        else bool(state_result.data.get("authoritative", True))
                    )
                )
                if state_result.ok and fresh_enough:
                    state = self._update_editor_cache_from_resource_state(
                        state_result.data
                    )
                    if state.get("playModeState") == target_state:
                        return state, state_result.context
            await asyncio.sleep(0.25)
        return None

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
        window_instance_id: str = "",
        escape_generic_menu: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "action": action,
            "button": button,
            "x": x,
            "y": y,
            "targetWindow": target_window,
            "windowInstanceId": window_instance_id,
            "escapeGenericMenu": escape_generic_menu,
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
        window_instance_id: str = "",
        escape_generic_menu: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        if action not in _KEYBOARD_ACTIONS:
            return fail(
                request_id,
                "INVALID_KEYBOARD_ACTION",
                f"Unsupported keyboard action: {action}",
                {
                    "field": "action",
                    "suppliedValue": action,
                    "candidates": list(_KEYBOARD_ACTIONS),
                    "sideEffectsMayHaveOccurred": False,
                    "nextAction": "Use one of the exact lowercase action values returned in candidates.",
                },
            )
        payload = {
            "action": action,
            "targetWindow": target_window,
            "keyCode": key_code,
            "windowInstanceId": window_instance_id,
            "escapeGenericMenu": escape_generic_menu,
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
        self, window_title: str = "", match_mode: str = "exact",
        instance_id: str = "", domain_generation: str = "", close_mode: str = "requestUserClose",
    ) -> ToolResponse:
        request_id = new_id("req")
        if close_mode not in ("requestUserClose", "forceClose") or not (window_title or instance_id):
            return fail(request_id, "INVALID_PAYLOAD", "Specify instanceId or windowTitle, and closeMode=requestUserClose|forceClose.")
        if not all(isinstance(value, str) for value in (window_title, match_mode, instance_id, domain_generation)):
            return fail(request_id, "INVALID_PAYLOAD", "Window selectors must be strings.")

        # An exact ID with an explicitly supplied title/domain still has to
        # satisfy that secondary selector.  Do this read-only check before the
        # close request and let the Bridge repeat it on its main-thread target
        # to close the TOCTOU window.
        if instance_id and (window_title or domain_generation):
            listed = await self.editor_windows_list()
            if not listed.ok or not isinstance(listed.data, dict):
                return listed
            windows = [item for item in listed.data.get("windows", []) if isinstance(item, dict)]
            matching_id = [item for item in windows if str(item.get("instanceId") or "") == instance_id]
            if len(matching_id) != 1:
                return fail(
                    request_id,
                    "WINDOW_AMBIGUOUS" if matching_id else "WINDOW_NOT_FOUND",
                    "EditorWindow instanceId did not resolve to exactly one current window.",
                    {"instanceId": instance_id, "matchCount": len(matching_id), "sideEffectsMayHaveOccurred": False},
                )
            resolved = matching_id[0]
            if domain_generation and str(resolved.get("domainGeneration") or "") != domain_generation:
                return fail(request_id, "WINDOW_DOMAIN_MISMATCH", "The exact EditorWindow belongs to a different domainGeneration.", {
                    "instanceId": instance_id, "expectedDomainGeneration": domain_generation,
                    "actualDomainGeneration": resolved.get("domainGeneration", ""), "sideEffectsMayHaveOccurred": False,
                })
            if window_title and not self._window_title_matches(str(resolved.get("title") or ""), window_title, match_mode):
                return fail(request_id, "EDITORWINDOW_TITLE_MISMATCH", "The exact EditorWindow has a different title.", {
                    "instanceId": instance_id, "expectedTitle": window_title,
                    "actualTitle": resolved.get("title", ""), "sideEffectsMayHaveOccurred": False,
                })
        return await self.dispatcher.call(
            request_id,
            "editor.window.close",
            {
                "windowTitle": window_title,
                "matchMode": match_mode,
                "instanceId": instance_id,
                "domainGeneration": domain_generation,
                "closeMode": close_mode,
            },
        )

    async def editor_window_set_rect(
        self,
        window_title: str = "",
        x: float | None = None,
        y: float | None = None,
        width: float | None = None,
        height: float | None = None,
        match_mode: str = "exact",
        instance_id: str = "",
        domain_generation: str = "",
        full_type_name: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        if not (window_title or instance_id):
            return fail(request_id, "INVALID_PAYLOAD", "Specify instanceId or windowTitle.")
        if any(value is None for value in (x, y, width, height)):
            return fail(request_id, "INVALID_PAYLOAD", "x, y, width and height are required.")
        if not all(isinstance(value, str) for value in (window_title, match_mode, instance_id, domain_generation, full_type_name)):
            return fail(request_id, "INVALID_PAYLOAD", "Window selectors must be strings.")

        # The Bridge repeats these guards on its own resolved instance to close
        # the time-of-check/time-of-use window.  Do the same read-only check
        # here whenever an exact ID has an explicit secondary selector, so a
        # stale domain or conflicting title/type never reaches the mutating
        # setRect command.
        if instance_id and (window_title or domain_generation or full_type_name.strip()):
            listed = await self.editor_windows_list()
            if not listed.ok or not isinstance(listed.data, dict):
                return listed
            windows = [item for item in listed.data.get("windows", []) if isinstance(item, dict)]
            if instance_id:
                matching_id = [item for item in windows if str(item.get("instanceId") or "") == instance_id]
                if len(matching_id) != 1:
                    return fail(
                        request_id,
                        "WINDOW_AMBIGUOUS" if matching_id else "WINDOW_NOT_FOUND",
                        "EditorWindow instanceId did not resolve to exactly one current window.",
                        {"instanceId": instance_id, "matchCount": len(matching_id), "sideEffectsMayHaveOccurred": False},
                    )
                resolved = matching_id[0]
                if domain_generation and str(resolved.get("domainGeneration") or "") != domain_generation:
                    return fail(request_id, "WINDOW_DOMAIN_MISMATCH", "The exact EditorWindow belongs to a different domainGeneration.", {
                        "instanceId": instance_id, "expectedDomainGeneration": domain_generation,
                        "actualDomainGeneration": resolved.get("domainGeneration", ""), "sideEffectsMayHaveOccurred": False,
                    })
                if full_type_name.strip() and str(resolved.get("fullTypeName") or "") != full_type_name:
                    return fail(request_id, "EDITORWINDOW_TYPE_MISMATCH", "The exact EditorWindow has a different fullTypeName.", {
                        "instanceId": instance_id, "expectedFullTypeName": full_type_name,
                        "actualFullTypeName": resolved.get("fullTypeName", ""), "sideEffectsMayHaveOccurred": False,
                    })
                if window_title and not self._window_title_matches(str(resolved.get("title") or ""), window_title, match_mode):
                    return fail(request_id, "EDITORWINDOW_TITLE_MISMATCH", "The exact EditorWindow has a different title.", {
                        "instanceId": instance_id, "expectedTitle": window_title,
                        "actualTitle": resolved.get("title", ""), "sideEffectsMayHaveOccurred": False,
                    })
        return await self.dispatcher.call(
            request_id,
            "editor.window.setRect",
            {
                "windowTitle": window_title,
                "matchMode": match_mode,
                "instanceId": instance_id,
                "domainGeneration": domain_generation,
                "fullTypeName": full_type_name,
                "x": x,
                "y": y,
                "width": width,
                "height": height,
            },
        )

    @staticmethod
    def _window_title_matches(actual: str, requested: str, match_mode: str) -> bool:
        if match_mode == "contains":
            return requested.lower() in actual.lower()
        return actual == requested

    async def editor_window_history(
        self, instance_id: str = "", after_sequence: int = 0, count: int = 100,
    ) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(instance_id, str):
            return fail(request_id, "INVALID_PAYLOAD", "instanceId must be a string.")
        if isinstance(after_sequence, bool) or not isinstance(after_sequence, int) or after_sequence < 0:
            return fail(request_id, "INVALID_PAYLOAD", "afterSequence must be a non-negative integer.")
        if isinstance(count, bool) or not isinstance(count, int) or count < 1 or count > 512:
            return fail(request_id, "INVALID_PAYLOAD", "count must be an integer between 1 and 512.")
        return await self.dispatcher.call(
            request_id,
            "editor.window.history",
            {"instanceId": instance_id, "afterSequence": after_sequence, "count": count},
        )

    async def editor_window_open(self, type_name: str) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(type_name, str) or not type_name.strip():
            return fail(request_id, "INVALID_PAYLOAD", "typeName is required.")
        return await self.dispatcher.call(request_id, "editor.window.open", {"typeName": type_name})

    async def editor_window_focus(self, instance_id: str, domain_generation: str = "") -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(instance_id, str) or not instance_id.strip():
            return fail(request_id, "INVALID_PAYLOAD", "instanceId is required.")
        if not isinstance(domain_generation, str):
            return fail(request_id, "INVALID_PAYLOAD", "domainGeneration must be a string.")
        return await self.dispatcher.call(
            request_id, "editor.window.focus",
            {"instanceId": instance_id, "domainGeneration": domain_generation},
        )

    async def sceneview_set_maximized(
        self, instance_id: int, maximized: bool, expected_current_maximized: bool | None = None,
        domain_generation: str = "", wait: bool = True, timeout_ms: int = 5000,
        restore_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        if isinstance(instance_id, bool) or not isinstance(instance_id, int):
            return fail(request_id, "INVALID_PAYLOAD", "instanceId must be an integer.", {
                "field": "instanceId", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })
        if instance_id < 0:
            return fail(request_id, "INVALID_PAYLOAD", "instanceId integer must not be negative.", {
                "field": "instanceId", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })
        # The public tool has an integer contract. The legacy Unity DTO still
        # carries its ID field as text, so only this private bridge payload is
        # stringified; no title/string fallback is accepted at the boundary.
        wire_instance_id = str(instance_id)
        if not isinstance(maximized, bool):
            return fail(request_id, "INVALID_PAYLOAD", "maximized must be a boolean.")
        if expected_current_maximized is not None and not isinstance(expected_current_maximized, bool):
            return fail(request_id, "INVALID_PAYLOAD", "expectedCurrentMaximized must be a boolean when supplied.")
        if not isinstance(domain_generation, str):
            return fail(request_id, "INVALID_PAYLOAD", "domainGeneration must be a string.")
        if not isinstance(wait, bool):
            return fail(request_id, "INVALID_PAYLOAD", "wait must be a boolean.", {
                "field": "wait", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })
        if not isinstance(timeout_ms, int) or isinstance(timeout_ms, bool) or timeout_ms < 1 or timeout_ms > 30000:
            return fail(request_id, "INVALID_PAYLOAD", "timeoutMs must be an integer between 1 and 30000.", {
                "field": "timeoutMs", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })
        if not isinstance(restore_token, str) or len(restore_token) > 512:
            return fail(request_id, "INVALID_PAYLOAD", "restoreToken must be a string of at most 512 characters.", {
                "field": "restoreToken", "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
            })

        restore_binding = None
        if restore_token:
            # Restore credentials deliberately live only in this Server process.
            # They are not a layout persistence mechanism, so a reload/restart
            # cannot turn an unknown old command into another setter dispatch.
            now = time.monotonic()
            restore_tokens = {
                token: binding for token, binding in getattr(self, "_sceneview_restore_tokens", {}).items()
                if now - float(binding.get("createdAt", 0.0)) <= 600
            }
            self._sceneview_restore_tokens = restore_tokens
            restore_binding = restore_tokens.get(restore_token)
            if restore_binding is None:
                return fail(request_id, "SCENEVIEW_RESTORE_TOKEN_INVALID", "restoreToken is unknown or expired; do not guess a replacement window.", {
                    "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
                })
            if (
                restore_binding["instanceId"] != instance_id
                or restore_binding["domainGeneration"] != domain_generation
                or restore_binding["restoreMaximized"] != maximized
                or (expected_current_maximized is not None and expected_current_maximized != restore_binding["expectedCurrentMaximized"])
            ):
                return fail(request_id, "SCENEVIEW_RESTORE_TOKEN_MISMATCH", "restoreToken does not belong to this exact SceneView restore request.", {
                    "commandSubmitted": False, "sideEffectsMayHaveOccurred": False,
                })
            expected_current_maximized = restore_binding["expectedCurrentMaximized"]
            if restore_binding.get("recoveryRequired"):
                return fail(
                    request_id,
                    "SCENEVIEW_RESTORE_RECOVERY_REQUIRED",
                    "A prior restore outcome is not safely replayable; inspect the original command and current exact SceneView state.",
                    {
                        "commandId": str(restore_binding.get("restoreCommandId") or ""),
                        "commandSubmitted": False,
                        "sideEffectsMayHaveOccurred": bool(restore_binding.get("sideEffectsMayHaveOccurred")),
                        "nextAction": "Query the original command; do not submit the restore action again.",
                    },
                )
            if restore_binding.get("completedAt") is not None:
                # A successful restore remains replayable for the token TTL.  It
                # represents the original command rather than submitting a new
                # setter, which is important when the client lost its response.
                replay = dict(restore_binding.get("replayData") or {})
                replay.update({
                    "commandId": str(restore_binding.get("restoreCommandId") or request_id),
                    "instanceId": instance_id,
                    "requestedState": {"maximized": maximized},
                    "observedState": {"maximized": maximized},
                    "commandSubmitted": False,
                    "stateObserved": True,
                    "changed": False,
                    "writeCount": 0,
                    "sideEffectsMayHaveOccurred": False,
                    "idempotentReplay": True,
                })
                if not wait:
                    replay.update({
                        "confirmed": False,
                        "terminal": False,
                        "nextAction": "Query the original command; do not submit the action again.",
                    })
                return ok(request_id, replay)

        payload = {"instanceId": wire_instance_id, "domainGeneration": domain_generation, "maximized": maximized}
        if expected_current_maximized is not None:
            payload.update({"hasExpectedCurrentMaximized": True, "expectedCurrentMaximized": expected_current_maximized})
        result = await self.dispatcher.call(request_id, "sceneview.setMaximized", payload, timeout_ms=timeout_ms)
        if not result.ok:
            detail = dict(result.error.detail or {}) if result.error and isinstance(result.error.detail, dict) else {}
            if result.error and result.error.code == "COMMAND_TIMEOUT" and detail.get("commandId"):
                # The command was already sent and the Bridge owns the
                # write-ahead observation.  It must be queried by identity,
                # never recreated as another setter dispatch.
                detail.update({
                    "instanceId": instance_id,
                    "requestedState": {"maximized": maximized},
                    "observationStatus": "submitted",
                    "commandSubmitted": True,
                    "stateObserved": False,
                    "confirmed": False,
                    "terminal": False,
                    "sideEffectsMayHaveOccurred": True,
                    "nextAction": "Call unity_sceneview_command_status with the original commandId; do not submit the setter again.",
                })
                return fail(request_id, result.error.code, result.error.message, detail, context=result.context)
            command_submitted = detail.get("commandSubmitted") is True
            side_effects = detail.get("sideEffectsMayHaveOccurred") is True
            if restore_binding is not None:
                restore_binding.update({
                    "recoveryRequired": True,
                    "restoreCommandId": result.request_id or request_id,
                    "sideEffectsMayHaveOccurred": side_effects,
                })
            if not detail.get("commandId"):
                return result
            detail.update({
                "instanceId": instance_id,
                "requestedState": {"maximized": maximized},
                "commandSubmitted": command_submitted,
                "sideEffectsMayHaveOccurred": side_effects,
                "confirmed": False,
                "terminal": not command_submitted,
                "nextAction": "Query the original command; do not submit the action again.",
            })
            return fail(request_id, result.error.code, result.error.message, detail, context=result.context)

        data = dict(result.data or {})
        if not bool(data.get("ok", True)):
            changed_or_written = bool(data.get("changed") or data.get("writeCount"))
            # Older Bridge payloads did not carry the explicit flags.  Their
            # changed/writeCount evidence remains sufficient to say a setter
            # may have run, while an explicit modern `false` is preserved for
            # pre-setter cancellation.
            command_submitted = data.get("commandSubmitted") is True or (
                "commandSubmitted" not in data and changed_or_written
            )
            side_effects = data.get("sideEffectsMayHaveOccurred") is True or changed_or_written
            if restore_binding is not None:
                restore_binding.update({
                    "recoveryRequired": True,
                    "restoreCommandId": str(data.get("commandId") or result.request_id or request_id),
                    "sideEffectsMayHaveOccurred": side_effects,
                })
            data.update({
                "commandId": str(data.get("commandId") or result.request_id or request_id),
                "instanceId": instance_id,
                "requestedState": {"maximized": maximized},
                "commandSubmitted": command_submitted,
                "stateObserved": data.get("stateObserved") is True,
                "confirmed": False,
                "terminal": (not command_submitted and not bool(data.get("persistenceError"))),
                "changed": bool(data.get("changed", False)),
                "writeCount": int(data.get("writeCount", 0) or 0),
                "sideEffectsMayHaveOccurred": side_effects,
            })
            if data.get("persistenceError"):
                data["nextAction"] = "Query the original command; do not submit the action again."
            return ok(request_id, data, context=result.context)

        changed = bool(data.get("changed", False))
        data.update({
            "commandId": str(data.get("commandId") or result.request_id or request_id),
            "requestedState": {"maximized": maximized},
            "observedState": {"maximized": bool(data.get("maximized", maximized))},
            "instanceId": instance_id,
            "commandSubmitted": True,
            "stateObserved": True,
            "changed": changed,
            "writeCount": int(data.get("writeCount", 1 if changed else 0)),
        })
        if changed and not restore_token:
            restore_tokens = getattr(self, "_sceneview_restore_tokens", {})
            now = time.monotonic()
            restore_tokens = {
                token: binding for token, binding in restore_tokens.items()
                if now - float(binding.get("createdAt", 0.0)) <= 600
            }
            while len(restore_tokens) >= 128:
                oldest = min(restore_tokens, key=lambda token: float(restore_tokens[token].get("createdAt", 0.0)))
                del restore_tokens[oldest]
            observed_domain = str(data.get("domainGeneration") or "").strip()
            if str(data.get("instanceId") or "") == wire_instance_id and observed_domain:
                token_value = secrets.token_urlsafe(24)
                restore_tokens[token_value] = {
                    "instanceId": instance_id,
                    "domainGeneration": observed_domain,
                    "restoreMaximized": not maximized,
                    "expectedCurrentMaximized": maximized,
                    "createdAt": now,
                }
                self._sceneview_restore_tokens = restore_tokens
                data["restoreToken"] = token_value
            else:
                # Do not issue a token whose target/domain identity was not
                # returned by Unity; that would make a later restore unsafe.
                data["restoreTokenUnavailableReason"] = "SCENEVIEW_IDENTITY_UNVERIFIED"
        if restore_token:
            # Keep the binding for its TTL after a successful restore so the
            # exact request can be replayed without a second setter call.
            restore_binding["completedAt"] = time.monotonic()
            restore_binding["restoreCommandId"] = data["commandId"]
            restore_binding["replayData"] = {
                "domainGeneration": str(data.get("domainGeneration") or domain_generation),
                "confirmed": False,
                "terminal": False,
            }
        fresh_authoritative = (
            data.get("authoritative") is True
            and data.get("isStale") is False
            and str(data.get("instanceId") or "") == wire_instance_id
            and bool(data.get("maximized", maximized)) is maximized
            and bool(str(data.get("domainGeneration") or "").strip())
        )
        if wait and fresh_authoritative:
            data.update({"confirmed": True, "terminal": True})
            if restore_token:
                restore_binding["replayData"].update({"confirmed": True, "terminal": True})
        elif wait:
            data.update({
                "confirmed": False,
                "terminal": False,
                "nextAction": "Query the original command; do not submit the action again.",
                "confirmationUnavailableReason": "FRESH_AUTHORITATIVE_SCENEVIEW_STATE_REQUIRED",
            })
        else:
            data.update({"confirmed": False, "terminal": False,
                         "nextAction": "Call unity_sceneview_command_status with the original commandId; do not submit the setter again."})
        return ok(request_id, data, context=result.context)

    async def sceneview_command_status(self, command_id: str) -> ToolResponse:
        """Read one original SceneView mutation observation without re-dispatching it."""
        request_id = new_id("req")
        if not isinstance(command_id, str) or not command_id.strip():
            return fail(request_id, "INVALID_PAYLOAD", "commandId must be a non-empty string.", {
                "field": "commandId", "sideEffectsMayHaveOccurred": False,
            })
        command_id = command_id.strip()
        state = getattr(getattr(self, "server", None), "state", None)
        if state is None:
            state = getattr(getattr(self, "dispatcher", None), "state", None)
        record = getattr(state, "commands", {}).get(command_id) if state is not None else None
        if record is None or getattr(record, "name", "") != "sceneview.setMaximized":
            return fail(
                request_id,
                "SCENEVIEW_COMMAND_RECOVERY_REQUIRED",
                "The original SceneView command is not available in this Server command state; do not resubmit automatically.",
                {
                    "commandId": command_id,
                    "observationStatus": "unknown",
                    "recoveryRequired": True,
                    "terminal": False,
                    "sideEffectsMayHaveOccurred": True,
                    "nextAction": "Inspect the exact current SceneView state. The original command cannot be recovered after Server restart.",
                },
            )
        result = await self.dispatcher.call(
            request_id, "sceneview.commandStatus", {"commandId": command_id}, timeout_ms=5000,
        )
        if not result.ok:
            detail = dict(result.error.detail or {}) if result.error and isinstance(result.error.detail, dict) else {}
            detail.update({
                "commandId": command_id,
                "observationStatus": "unknown",
                "recoveryRequired": True,
                "terminal": False,
                "nextAction": "Inspect the exact current SceneView state; do not resubmit the original setter automatically.",
            })
            detail.setdefault("sideEffectsMayHaveOccurred", True)
            return fail(
                request_id,
                result.error.code if result.error else "SCENEVIEW_COMMAND_STATUS_UNKNOWN",
                result.error.message if result.error else "SceneView command observation could not be read.",
                detail,
                context=result.context,
            )
        data = dict(result.data or {})
        if str(data.get("commandId") or "") != command_id:
            return fail(request_id, "SCENEVIEW_COMMAND_RECOVERY_REQUIRED",
                        "The Bridge returned a different original command identity.", {
                            "commandId": command_id,
                            "observationStatus": "unknown",
                            "recoveryRequired": True,
                            "terminal": False,
                            "sideEffectsMayHaveOccurred": True,
                        }, context=result.context)
        if data.get("recoveryRequired") is True or str(data.get("observationStatus") or "").lower() == "unknown":
            data.update({
                "commandId": command_id,
                "observationStatus": "unknown",
                "recoveryRequired": True,
                "terminal": False,
                "nextAction": "Inspect the exact current SceneView state; do not resubmit the original setter automatically.",
            })
            return fail(request_id, "SCENEVIEW_COMMAND_RECOVERY_REQUIRED",
                        "The Bridge cannot recover the original SceneView command observation.", data,
                        context=result.context)
        data.setdefault("terminal", data.get("observationStatus") == "confirmed")
        data.setdefault("nextAction", "" if data["terminal"] else "Call unity_sceneview_command_status with the same commandId; do not submit the setter again.")
        return ok(request_id, data, context=result.context)

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
        query: str = "",
        log_type: str = "",
        include_stack_trace: bool = False,
        exclude_upilot: bool = True,
        contains: list[str] | None = None,
        contains_all: bool = False,
        regex: str = "",
        newest_first: bool = True,
        max_message_length: int = 0,
        max_count: int = 0,
    ) -> ToolResponse:
        request_id = new_id("req")
        effective_count = max_count if max_count > 0 else count
        if isinstance(contains, str):
            contains = [contains]
        payload: dict = {
            "count": max(1, min(effective_count, 5000)),
            "includeStackTrace": include_stack_trace,
            "excludeUPilot": exclude_upilot,
            "containsAll": contains_all,
            "newestFirst": newest_first,
            "maxMessageLength": max(0, max_message_length),
        }
        if query:
            payload["query"] = query
        if log_type:
            payload["logType"] = log_type
        if contains:
            payload["contains"] = contains
        if regex:
            payload["regex"] = regex
        return await self.dispatcher.call(request_id, "console.logs.search", payload)

    async def console_capture_start(
        self,
        title: str = "",
        path: str = "",
        include_stack_trace: bool = True,
        exclude_upilot: bool = True,
        clear_unity_console: bool = False,
        flush_interval_ms: int = 1000,
        max_file_bytes: int = 50 * 1024 * 1024,
        allow_outside_project: bool = False,
        owner_id: str = "",
        owner_token: str = "",
        request_key: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        requested_owner_id = owner_id.strip()
        request_key = str(request_key or "").strip()
        request_signature = hashlib.sha256(
            json.dumps(
                {
                    "title": title, "path": path, "includeStackTrace": include_stack_trace,
                    "excludeUPilot": exclude_upilot, "clearUnityConsole": clear_unity_console,
                    "flushIntervalMs": max(100, min(flush_interval_ms, 60000)),
                    "maxFileBytes": max(1024 * 1024, max_file_bytes),
                    "allowOutsideProject": allow_outside_project, "ownerId": requested_owner_id,
                },
                ensure_ascii=False, sort_keys=True, separators=(",", ":"),
            ).encode("utf-8")
        ).hexdigest()
        store, project = self._capture_attachment_store()
        if request_key:
            if store is None or not project:
                return fail(request_id, "CAPTURE_START_PERSISTENCE_UNAVAILABLE", "requestKey requires project-local persistence.")
            previous = store.load_capture_start_intent(request_key)
            if previous is not None:
                if str(previous.get("requestSignature") or "") != request_signature:
                    return fail(
                        request_id, "CAPTURE_START_REQUEST_CONFLICT",
                        "requestKey is already bound to a different capture start request.",
                        {"requestKey": request_key, "sessionId": previous.get("sessionId", "")},
                    )
                return fail(
                    request_id, "CAPTURE_OWNERSHIP_RECOVERY_REQUIRED",
                    "The original capture start used this requestKey. The owner token is not replayed; inspect the exact session or use an explicitly authorized force stop.",
                    {
                        "requestKey": request_key,
                        "ownerId": previous.get("ownerId", ""),
                        "sessionId": previous.get("sessionId", ""),
                        "startState": previous.get("startState", "unknown"),
                        "ownershipRecoveryRequired": True,
                    },
                )
        owner_id = requested_owner_id or request_id
        # This public/manual path owns the secret generation.  Operation
        # capture startup uses its separately persisted, internal dispatcher
        # intent so it can recover the exact token after a response loss.
        owner_token = secrets.token_urlsafe(32)
        intent: dict | None = None
        if request_key:
            intent = {
                "schemaVersion": 1,
                "projectPath": project,
                "requestKey": request_key,
                "requestSignature": request_signature,
                "ownerId": owner_id,
                "ownerTokenSha256": hashlib.sha256(owner_token.encode("utf-8")).hexdigest(),
                "startState": "intent_sent",
                "createdAt": now_ms(),
            }
            try:
                store.save_capture_start_intent(intent)
            except (OSError, RuntimeError) as exc:
                return fail(request_id, "CAPTURE_START_PERSIST_FAILED", str(exc), {"requestKey": request_key})
        payload = {
            "title": title,
            "path": path,
            "includeStackTrace": include_stack_trace,
            "excludeUPilot": exclude_upilot,
            "clearUnityConsole": clear_unity_console,
            "flushIntervalMs": max(100, min(flush_interval_ms, 60000)),
            "maxFileBytes": max(1024 * 1024, max_file_bytes),
            "allowOutsideProject": allow_outside_project,
            "ownerId": owner_id,
            "ownerToken": owner_token,
        }
        if request_key:
            payload["requestKey"] = request_key
        result = await self.dispatcher.call(request_id, "console.capture.start", payload)
        self._sanitize_capture_response(result)
        if intent is not None:
            intent["startState"] = "started" if result.ok else "response_unknown"
            if result.ok and isinstance(result.data, dict):
                session = result.data.get("session") if isinstance(result.data.get("session"), dict) else result.data
                intent["sessionId"] = str(session.get("sessionId") or "") if isinstance(session, dict) else ""
            try:
                store.save_capture_start_intent(intent)
            except (OSError, RuntimeError) as exc:
                return fail(
                    request_id, "CAPTURE_START_RESULT_PERSIST_FAILED", str(exc),
                    {"requestKey": request_key, "sideEffectsMayHaveOccurred": True},
                )
        if result.ok and isinstance(result.data, dict):
            result.data["ownerId"] = owner_id
            result.data["ownerToken"] = owner_token
            if request_key:
                result.data["requestKey"] = request_key
        return result

    @staticmethod
    def _sanitize_capture_response(result: ToolResponse) -> ToolResponse:
        """Remove owner credentials from ordinary responses and error summaries."""
        if isinstance(result.data, dict):
            result.data = _without_capture_owner_secrets(result.data)
        if result.error is not None and isinstance(result.error.detail, dict):
            result.error.detail = _without_capture_owner_secrets(result.error.detail)
        return result

    async def console_capture_status(self, session_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(session_id, str):
            return fail(request_id, "INVALID_PAYLOAD", "sessionId must be a string.", {
                "field": "sessionId", "dispatchAttempted": False,
            })
        result = await self.dispatcher.call(
            request_id, "console.capture.status", {"sessionId": session_id}
        )
        return self._sanitize_capture_response(result)

    async def console_capture_read(
        self,
        session_id: str = "",
        after_sequence: int = -1,
        from_sequence: int = -1,
        to_sequence: int = -1,
        count: int = 200,
        log_type: str = "",
        include_stack_trace: bool = True,
        contains: list[str] | None = None,
        contains_all: bool = False,
        regex: str = "",
        newest_first: bool = False,
        continuation_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        invalid_field = next((field for field, value, expected in (
            ("sessionId", session_id, str),
            ("afterSequence", after_sequence, int),
            ("fromSequence", from_sequence, int),
            ("toSequence", to_sequence, int),
            ("count", count, int),
            ("logType", log_type, str),
            ("includeStackTrace", include_stack_trace, bool),
            ("containsAll", contains_all, bool),
            ("regex", regex, str),
            ("newestFirst", newest_first, bool),
            ("continuationToken", continuation_token, str),
        ) if not isinstance(value, expected) or (expected is int and isinstance(value, bool))), None)
        if invalid_field is not None or (contains is not None and (
            not isinstance(contains, list) or any(not isinstance(value, str) for value in contains)
        )):
            return fail(request_id, "INVALID_PAYLOAD", "Console capture read arguments have invalid types.", {
                "field": invalid_field or "contains", "dispatchAttempted": False,
            })
        payload: dict = {
            "sessionId": session_id,
            "afterSequence": after_sequence,
            "fromSequence": from_sequence,
            "toSequence": to_sequence,
            "count": max(1, min(count, 5000)),
            "includeStackTrace": include_stack_trace,
            "containsAll": contains_all,
            "newestFirst": newest_first,
        }
        if log_type:
            payload["logType"] = log_type
        if contains:
            payload["contains"] = contains
        if regex:
            payload["regex"] = regex
        if continuation_token:
            payload["continuationToken"] = continuation_token
        result = await self.dispatcher.call(request_id, "console.capture.read", payload)
        return self._sanitize_capture_response(result)

    async def console_capture_stop(
        self, session_id: str = "", owner_token: str = "", force_stop: bool = False,
    ) -> ToolResponse:
        request_id = new_id("req")
        if not isinstance(session_id, str) or not isinstance(owner_token, str) or not isinstance(force_stop, bool):
            field = (
                "sessionId" if not isinstance(session_id, str)
                else "ownerToken" if not isinstance(owner_token, str)
                else "forceStop"
            )
            return fail(request_id, "INVALID_PAYLOAD", "Console capture stop arguments have invalid types.", {
                "field": field, "stopAttempted": False,
            })
        session_id = session_id.strip()
        if force_stop and not session_id:
            return fail(request_id, "CAPTURE_FORCE_STOP_SESSION_REQUIRED", "forceStop requires an exact sessionId.", {"stopAttempted": False})
        # A persisted terminal is an idempotent observation, including legacy
        # manifests which predate ownership credentials.  Do this before the
        # ownership gate so a repeated stop cannot resurrect a completed
        # capture merely because its original token is no longer available.
        if session_id:
            completed = self._recover_completed_console_capture(
                session_id,
                owner_token,
                fail(request_id, "CAPTURE_STOP_NOT_DISPATCHED", "The completed capture was read from its persisted manifest."),
                allow_without_owner=force_stop,
            )
            if completed is not None:
                return ok(request_id, _without_capture_owner_secrets(completed))
        if not force_stop and (not session_id or not owner_token):
            return fail(
                request_id,
                "CAPTURE_OWNERSHIP_REQUIRED",
                "Stopping a capture requires its exact sessionId and matching ownerToken.",
                {"sessionId": session_id, "stopAttempted": False},
            )
        if force_stop and not self._active_console_capture_is_explicitly_known(session_id):
            return fail(
                request_id,
                "CAPTURE_FORCE_STOP_SESSION_UNKNOWN",
                "forceStop requires an exact active capture session known in the current project.",
                {"sessionId": session_id, "stopAttempted": False},
            )
        payload = {"sessionId": session_id}
        if owner_token:
            payload["ownerToken"] = owner_token
        if force_stop:
            payload["forceStop"] = True
        result = await self.dispatcher.call(request_id, "console.capture.stop", payload)
        self._sanitize_capture_response(result)
        if result.ok:
            data = result.data or {}
            result.data = data
            session = data.get("session") if isinstance(data.get("session"), dict) else {}
            if session:
                active = bool(session.get("active", False))
                data.setdefault("active", active)
                data.setdefault("terminal", not active)
                data.setdefault("completionSource", "unity")
            return result

        recovered = None if force_stop else self._recover_completed_console_capture(session_id, owner_token, result)
        if recovered is None:
            return result
        return ok(
            result.request_id,
            _without_capture_owner_secrets(recovered),
            context=result.context,
            timing=result.timing,
        )

    def _active_console_capture_is_explicitly_known(self, session_id: str) -> bool:
        """Accept force-stop only for one active session persisted by this project.

        ``forceStop`` is an explicit human disposition, not permission to issue a
        destructive stop for an arbitrary identifier.  The session manifest gives
        us a local, exact identity without querying or stopping another capture.
        """
        if not session_id:
            return False
        server = getattr(self, "server", None)
        session_manager = getattr(server, "session_manager", None)
        active_session = getattr(session_manager, "active", None)
        project_path = str(getattr(active_session, "project_path", "") or "").strip()
        if not project_path:
            state = getattr(server, "state", None)
            project_path = str(getattr(state, "_project_path", "") or "").strip()
        if not project_path:
            return False
        capture_root = Path(project_path) / "Log" / "UPilotConsole"
        if not capture_root.is_dir():
            return False
        for manifest_path in capture_root.glob("*/session.json"):
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
            except (OSError, UnicodeError, json.JSONDecodeError):
                continue
            if (
                isinstance(manifest, dict)
                and manifest.get("sessionId") == session_id
                and manifest.get("active") is True
            ):
                return True
        return False

    def _recover_completed_console_capture(
        self, session_id: str, owner_token: str, failed_stop: ToolResponse, *, allow_without_owner: bool = False,
    ) -> dict | None:
        """Return a persisted Stop terminal only for one exact, complete session."""
        if not session_id:
            return None

        server = getattr(self, "server", None)
        session_manager = getattr(server, "session_manager", None)
        active_session = getattr(session_manager, "active", None)
        project_path = str(getattr(active_session, "project_path", "") or "").strip()
        if not project_path:
            state = getattr(server, "state", None)
            project_path = str(getattr(state, "_project_path", "") or "").strip()
        if not project_path:
            return None

        capture_root = Path(project_path) / "Log" / "UPilotConsole"
        if not capture_root.is_dir():
            return None

        for manifest_path in capture_root.glob("*/session.json"):
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
            except (OSError, UnicodeError, json.JSONDecodeError):
                continue
            if not isinstance(manifest, dict) or manifest.get("sessionId") != session_id:
                continue
            expected_owner_hash = str(manifest.get("ownerTokenSha256") or "")
            actual_owner_hash = hashlib.sha256(owner_token.encode("utf-8")).hexdigest() if owner_token else ""
            if expected_owner_hash and not allow_without_owner and not hmac.compare_digest(actual_owner_hash, expected_owner_hash):
                return None

            try:
                finished_at = int(manifest.get("finishedAtUtcMs") or 0)
            except (TypeError, ValueError):
                return None
            summary_path = manifest_path.parent / "summary.json"
            if (
                bool(manifest.get("active", True))
                or finished_at <= 0
                or not str(manifest.get("sha256") or "").strip()
                or not summary_path.is_file()
            ):
                return None
            try:
                summary_bytes = summary_path.stat().st_size
            except OSError:
                return None

            diagnostic = {
                "code": failed_stop.error.code if failed_stop.error else "",
                "message": failed_stop.error.message if failed_stop.error else "",
                "detail": failed_stop.error.detail if failed_stop.error else {},
            }
            return {
                "ok": True,
                "action": "StopCapture",
                "error": "",
                "session": manifest,
                "active": False,
                "terminal": True,
                "completionSource": "persistedManifest",
                "summaryPath": str(summary_path.resolve()),
                "summaryBytes": summary_bytes,
                "sha256": str(manifest.get("sha256") or ""),
                "contextDiagnostic": diagnostic,
            }
        return None

    def _capture_attachment_lock(self) -> asyncio.Lock:
        lock = getattr(self, "_capture_attachment_lock_instance", None)
        if lock is None:
            lock = asyncio.Lock()
            self._capture_attachment_lock_instance = lock
        return lock

    def _capture_attachment_store(self):
        server = getattr(self, "server", None)
        store = getattr(server, "state", None)
        project = str(getattr(store, "project_path", "") or getattr(store, "_project_path", "") or "").strip()
        return store, project

    def _mark_capture_attachments_recovery_unknown(self, store, project: str) -> None:
        """A new Server process must re-verify a source before treating it as live."""
        if getattr(self, "_capture_attachment_recovery_project", "") == project:
            return
        for state in store.load_capture_attachments():
            if bool(state.get("detached")):
                continue
            state.update(sourceStatus="unknown", recoveryRequired=True, sourceVerifiedAt=0)
            store.save_capture_attachment(state)
        # Only acknowledge the recovery pass after every state replacement
        # committed.  Otherwise the next call must retry rather than silently
        # treating a partial recovery write as complete.
        self._capture_attachment_recovery_project = project

    @staticmethod
    def _capture_attachment_public(state: dict) -> dict:
        export = state.get("export") if isinstance(state.get("export"), dict) else {}
        result = {
            "attachmentId": state.get("attachmentId", ""),
            "sessionId": state.get("sessionId", ""),
            "fromSequence": int(state.get("fromSequence") or 0),
            "toSequence": state.get("toSequence"),
            "rangeClosed": bool(state.get("detached")),
            "readOnlyAttachment": True,
            "active": not bool(state.get("detached")),
            "sourceStatus": state.get("sourceStatus", "unknown"),
            "recoveryRequired": bool(state.get("recoveryRequired")),
            "sourceVerifiedAt": int(state.get("sourceVerifiedAt") or 0),
        }
        if export:
            result.update({
                "exportRequested": True,
                "exportComplete": bool(export.get("complete")),
                "exportIncomplete": bool(export.get("incomplete")),
                "exportPath": export.get("path", ""),
                "exportBytes": int(export.get("bytes") or 0),
                "exportSha256": export.get("sha256", ""),
                "continuationToken": export.get("continuationToken", ""),
                "gapCount": int(export.get("gapCount") or 0),
            })
        return result

    async def _verify_capture_attachment_source(
        self, state: dict, store, project: str, request_id: str, *, persist: bool = True,
    ) -> tuple[dict | None, ToolResponse | None]:
        status = await self.console_capture_status(str(state.get("sessionId") or ""))
        data = status.data if status.ok and isinstance(status.data, dict) else {}
        session = data.get("session") if isinstance(data.get("session"), dict) else data
        if not isinstance(session, dict) or str(session.get("sessionId") or "") != str(state.get("sessionId") or ""):
            state.update(sourceStatus="unknown", recoveryRequired=True, sourceVerifiedAt=0)
            if persist:
                try:
                    store.save_capture_attachment(state)
                except (OSError, RuntimeError) as exc:
                    return None, fail(
                        request_id,
                        "CAPTURE_ATTACHMENT_SOURCE_STATE_PERSIST_FAILED",
                        "The source recovery state was not persisted; the source was not adopted or stopped.",
                        {"attachmentId": state.get("attachmentId", ""), "stopAttempted": False, "persistenceError": str(exc)},
                    )
            return None, fail(
                request_id,
                "CAPTURE_ATTACHMENT_SOURCE_UNKNOWN",
                "The exact capture source could not be verified; it was not adopted or stopped.",
                {
                    "attachmentId": state.get("attachmentId", ""),
                    "sessionId": state.get("sessionId", ""),
                    "sourceStatus": "unknown",
                    "rangeClosed": bool(state.get("detached")),
                    "sourceError": status.error.code if status.error else "",
                },
            )
        try:
            next_sequence = int(session.get("nextSequence") or 0)
        except (TypeError, ValueError):
            return None, fail(
                request_id,
                "CAPTURE_ATTACHMENT_SOURCE_INVALID",
                "The exact capture source did not provide a valid nextSequence.",
                {"attachmentId": state.get("attachmentId", ""), "sessionId": state.get("sessionId", "")},
            )
        state.update(
            sourceStatus="active" if bool(session.get("active")) else "inactive",
            recoveryRequired=False,
            sourceVerifiedAt=now_ms(),
            sourceNextSequence=next_sequence,
            sourceDirectory=str(session.get("directory") or ""),
        )
        if persist:
            try:
                store.save_capture_attachment(state)
            except (OSError, RuntimeError) as exc:
                return None, fail(
                    request_id,
                    "CAPTURE_ATTACHMENT_SOURCE_STATE_PERSIST_FAILED",
                    "The verified source state was not persisted; the source was not adopted or stopped.",
                    {"attachmentId": state.get("attachmentId", ""), "stopAttempted": False, "persistenceError": str(exc)},
                )
        return session, None

    @staticmethod
    def _capture_attachment_export_path(project: str, state: dict, session: dict) -> tuple[Path | None, str]:
        source_directory = str(session.get("directory") or state.get("sourceDirectory") or "").strip()
        if not source_directory:
            return None, "Capture source directory is unavailable."
        try:
            root = Path(project).resolve()
            source = Path(source_directory).resolve()
            source.relative_to(root)
        except (OSError, ValueError):
            return None, "Capture source directory is outside the current project."
        attachment_id = str(state.get("attachmentId") or "")
        if not attachment_id or Path(attachment_id).name != attachment_id:
            return None, "Attachment identity is invalid."
        return source / "attachments" / (attachment_id + ".jsonl"), ""

    @staticmethod
    def _append_capture_attachment_export(path: Path, committed_bytes: int, logs: list[dict]) -> int:
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = b"".join(
            json.dumps(log, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
            for log in logs
        )
        with path.open("a+b") as output:
            output.seek(0, os.SEEK_END)
            actual_bytes = output.tell()
            if actual_bytes < committed_bytes:
                raise OSError("Attachment export candidate is shorter than its persisted committed boundary.")
            if actual_bytes > committed_bytes:
                # A previous response may have been lost after the candidate was
                # flushed but before its page index was persisted.  Only adopt
                # that exact page; never truncate or overwrite a different
                # candidate while recovering it.
                output.seek(committed_bytes)
                if output.read() != payload:
                    raise OSError("Attachment export candidate differs from its persisted page boundary.")
                return actual_bytes
            output.truncate(committed_bytes)
            output.seek(committed_bytes)
            output.write(payload)
            output.flush()
            os.fsync(output.fileno())
            return output.tell()

    @staticmethod
    def _sha256_file(path: Path) -> str:
        digest = hashlib.sha256()
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
        return digest.hexdigest()

    async def console_capture_attach(self, session_id: str, request_key: str) -> ToolResponse:
        request_id = new_id("req")
        # Do not coerce arbitrary values into an identity.  In particular,
        # ``True`` becoming the session id ``"True"`` would make an ownership
        # request look precise even though the caller supplied no valid capture
        # identity.  The MCP schema has the same shape, but this domain boundary
        # is also used directly by operation recovery and contract callers.
        if not isinstance(session_id, str) or not isinstance(request_key, str):
            return fail(
                request_id,
                "CAPTURE_ATTACHMENT_INVALID",
                "sessionId and requestKey must be strings.",
                {"field": "sessionId" if not isinstance(session_id, str) else "requestKey", "stopAttempted": False},
            )
        session_id, request_key = session_id.strip(), request_key.strip()
        if not session_id or not request_key:
            return fail(request_id, "CAPTURE_ATTACHMENT_INVALID", "sessionId and requestKey are required.")
        store, project = self._capture_attachment_store()
        if store is None or not project:
            return fail(request_id, "CAPTURE_ATTACHMENT_PERSISTENCE_UNAVAILABLE", "Project attachment persistence is unavailable.")
        async with self._capture_attachment_lock():
            try:
                self._mark_capture_attachments_recovery_unknown(store, project)
            except (OSError, RuntimeError) as exc:
                return fail(request_id, "CAPTURE_ATTACHMENT_RECOVERY_PERSIST_FAILED", "Attachment recovery state was not persisted.", {"stopAttempted": False, "persistenceError": str(exc)})
            existing = next((item for item in store.load_capture_attachments() if item.get("requestKey") == request_key), None)
            if existing is not None and str(existing.get("sessionId") or "") != session_id:
                return fail(
                    request_id, "CAPTURE_ATTACHMENT_REQUEST_CONFLICT",
                    "requestKey is already bound to another capture session.",
                    {"requestKey": request_key, "attachmentId": existing.get("attachmentId", "")},
                )
            if existing is not None:
                session, source_error = await self._verify_capture_attachment_source(existing, store, project, request_id)
                if source_error is not None:
                    return source_error
                return ok(request_id, self._capture_attachment_public(existing))
            candidate = {
                "schemaVersion": 1,
                "attachmentId": new_id("attachment"),
                "projectPath": project,
                "sessionId": session_id,
                "requestKey": request_key,
                "fromSequence": 0,
                "readOnlyAttachment": True,
                "detached": False,
                "sourceStatus": "unknown",
                "recoveryRequired": True,
                "sourceVerifiedAt": 0,
            }
            session, source_error = await self._verify_capture_attachment_source(
                candidate, store, project, request_id, persist=False,
            )
            if source_error is not None:
                return source_error
            candidate["fromSequence"] = int(candidate.get("sourceNextSequence") or 0)
            try:
                created, persisted = store.create_capture_attachment(candidate)
            except (OSError, RuntimeError) as exc:
                return fail(request_id, "CAPTURE_ATTACHMENT_PERSIST_FAILED", str(exc), {"sessionId": session_id})
            if created == "limit":
                return fail(request_id, "CAPTURE_ATTACHMENT_LIMIT", "At most 64 capture attachments may be active.")
            if created == "conflict":
                return fail(
                    request_id, "CAPTURE_ATTACHMENT_REQUEST_CONFLICT",
                    "requestKey is already bound to another capture session.",
                    {"requestKey": request_key, "attachmentId": (persisted or {}).get("attachmentId", "")},
                )
            return ok(request_id, self._capture_attachment_public(persisted or candidate))

    async def console_capture_detach(
        self,
        attachment_id: str,
        export: bool = False,
        request_key: str = "",
        continuation_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        if (
            not isinstance(attachment_id, str)
            or not isinstance(export, bool)
            or not isinstance(request_key, str)
            or not isinstance(continuation_token, str)
        ):
            field = (
                "attachmentId" if not isinstance(attachment_id, str)
                else "export" if not isinstance(export, bool)
                else "requestKey" if not isinstance(request_key, str)
                else "continuationToken"
            )
            return fail(
                request_id,
                "CAPTURE_ATTACHMENT_INVALID",
                "attachmentId, export, requestKey, and continuationToken have invalid types.",
                {"field": field, "stopAttempted": False},
            )
        attachment_id = attachment_id.strip()
        request_key, continuation_token = request_key.strip(), continuation_token.strip()
        if not attachment_id:
            return fail(request_id, "CAPTURE_ATTACHMENT_INVALID", "attachmentId is required.")
        if export and not request_key:
            return fail(request_id, "CAPTURE_ATTACHMENT_EXPORT_REQUEST_KEY_REQUIRED", "export requires a non-empty requestKey.")
        store, project = self._capture_attachment_store()
        if store is None or not project:
            return fail(request_id, "CAPTURE_ATTACHMENT_PERSISTENCE_UNAVAILABLE", "Project attachment persistence is unavailable.")
        async with self._capture_attachment_lock():
            try:
                self._mark_capture_attachments_recovery_unknown(store, project)
            except (OSError, RuntimeError) as exc:
                return fail(request_id, "CAPTURE_ATTACHMENT_RECOVERY_PERSIST_FAILED", "Attachment recovery state was not persisted.", {"stopAttempted": False, "persistenceError": str(exc)})
            state = next((item for item in store.load_capture_attachments() if item.get("attachmentId") == attachment_id), None)
            if state is None:
                return fail(request_id, "CAPTURE_ATTACHMENT_NOT_FOUND", "The attachmentId is unknown.", {"attachmentId": attachment_id})
            export_state = state.get("export") if isinstance(state.get("export"), dict) else {}
            if bool(state.get("detached")) and not export:
                return ok(request_id, self._capture_attachment_public(state))
            if bool(state.get("detached")) and export and bool(export_state.get("complete")):
                existing_request_key = str(export_state.get("requestKey") or "")
                if existing_request_key != request_key:
                    return fail(
                        request_id, "CAPTURE_ATTACHMENT_EXPORT_CONFLICT",
                        "export requestKey is already bound to this attachment export.",
                        {"attachmentId": attachment_id},
                    )
                if continuation_token:
                    return fail(
                        request_id, "CAPTURE_ATTACHMENT_CONTINUATION_INVALID",
                        "A completed attachment export does not accept a continuationToken.",
                        {"attachmentId": attachment_id, "exportComplete": True},
                    )
                return ok(request_id, self._capture_attachment_public(state))
            # Once the detach boundary has been durably fixed, source liveness
            # is irrelevant.  A stopped/reloaded source must remain exportable
            # from that fixed range without being adopted or restarted.
            if bool(state.get("detached")):
                session = {"directory": state.get("sourceDirectory") or ""}
            else:
                session, source_error = await self._verify_capture_attachment_source(state, store, project, request_id)
                if source_error is not None:
                    return source_error
            if not bool(state.get("detached")):
                state.update(
                    detached=True,
                    detachedAt=now_ms(),
                    detachRequestKey=request_key,
                    toSequence=int(state.get("sourceNextSequence") or 0),
                )
                try:
                    store.save_capture_attachment(state)
                except (OSError, RuntimeError) as exc:
                    return fail(
                        request_id,
                        "CAPTURE_ATTACHMENT_DETACH_PERSIST_FAILED",
                        "The attachment boundary was not persisted; the source capture was not stopped.",
                        {"attachmentId": attachment_id, "rangeClosed": False, "persistenceError": str(exc)},
                    )
            if not export:
                return ok(request_id, self._capture_attachment_public(state))

            existing_request_key = str(export_state.get("requestKey") or "")
            if existing_request_key and existing_request_key != request_key:
                return fail(
                    request_id, "CAPTURE_ATTACHMENT_EXPORT_CONFLICT",
                    "export requestKey is already bound to this attachment export.",
                    {"attachmentId": attachment_id},
                )
            expected_token = str(export_state.get("continuationToken") or "")
            if continuation_token != expected_token:
                return fail(
                    request_id, "CAPTURE_ATTACHMENT_CONTINUATION_INVALID",
                    "continuationToken does not match the persisted attachment export page.",
                    {"attachmentId": attachment_id, "exportComplete": bool(export_state.get("complete"))},
                )
            if bool(export_state.get("complete")):
                return ok(request_id, self._capture_attachment_public(state))
            output_path, path_error = self._capture_attachment_export_path(project, state, session)
            if output_path is None:
                return fail(request_id, "CAPTURE_ATTACHMENT_EXPORT_PATH_INVALID", path_error, {"attachmentId": attachment_id})
            if not export_state:
                export_state = {
                    "schemaVersion": 1,
                    "requestKey": request_key,
                    "path": str(output_path.relative_to(Path(project).resolve())).replace("\\", "/"),
                    "bytes": 0,
                    "nextExpectedSequence": int(state.get("fromSequence") or 0),
                    "continuationToken": "",
                    "complete": False,
                    "incomplete": False,
                    "gapCount": 0,
                }
                state["export"] = export_state
                try:
                    store.save_capture_attachment(state)
                except (OSError, RuntimeError) as exc:
                    return fail(
                        request_id,
                        "CAPTURE_ATTACHMENT_EXPORT_INIT_PERSIST_FAILED",
                        "The attachment export intent was not persisted; no export page was read.",
                        {"attachmentId": attachment_id, "rangeClosed": True, "persistenceError": str(exc)},
                    )
            read = await self.console_capture_read(
                session_id=str(state.get("sessionId") or ""),
                from_sequence=int(state.get("fromSequence") or 0),
                to_sequence=max(-1, int(state.get("toSequence") or 0) - 1),
                count=500,
                include_stack_trace=True,
                continuation_token=continuation_token,
            )
            if not read.ok or not isinstance(read.data, dict):
                export_state["lastError"] = read.error.code if read.error else "CAPTURE_READ_FAILED"
                state["export"] = export_state
                try:
                    store.save_capture_attachment(state)
                except (OSError, RuntimeError) as exc:
                    return fail(
                        request_id, "CAPTURE_ATTACHMENT_EXPORT_PAGE_PERSIST_FAILED",
                        "The failed export-page state was not persisted; the source capture remains unchanged.",
                        {"attachmentId": attachment_id, "rangeClosed": True, "exportComplete": False, "stopAttempted": False, "persistenceError": str(exc)},
                    )
                return fail(
                    request_id, "CAPTURE_ATTACHMENT_EXPORT_READ_FAILED",
                    "Attachment export page could not be read; the source capture remains unchanged.",
                    {"attachmentId": attachment_id, "rangeClosed": True, "exportComplete": False,
                     "sourceError": export_state["lastError"]},
                )
            logs = read.data.get("logs") or read.data.get("entries") or []
            if not isinstance(logs, list) or not all(isinstance(item, dict) for item in logs):
                return fail(request_id, "CAPTURE_ATTACHMENT_EXPORT_READ_INVALID", "Capture read returned an invalid log page.", {"attachmentId": attachment_id})
            expected_sequence = int(export_state.get("nextExpectedSequence") or state.get("fromSequence") or 0)
            gaps = int(export_state.get("gapCount") or 0)
            for item in logs:
                try:
                    sequence = int(item.get("sequence"))
                except (TypeError, ValueError):
                    return fail(request_id, "CAPTURE_ATTACHMENT_EXPORT_READ_INVALID", "Capture record has no valid sequence.", {"attachmentId": attachment_id})
                if sequence < expected_sequence or sequence >= int(state.get("toSequence") or 0):
                    return fail(
                        request_id,
                        "CAPTURE_ATTACHMENT_EXPORT_SEQUENCE_INVALID",
                        "Capture export page is outside the fixed attachment range or repeats a sequence.",
                        {"attachmentId": attachment_id, "rangeClosed": True},
                    )
                if sequence != expected_sequence:
                    gaps += 1
                expected_sequence = sequence + 1
            try:
                committed_bytes = await asyncio.to_thread(
                    self._append_capture_attachment_export,
                    output_path,
                    int(export_state.get("bytes") or 0),
                    logs,
                )
            except OSError as exc:
                export_state["lastError"] = str(exc)
                state["export"] = export_state
                try:
                    store.save_capture_attachment(state)
                except (OSError, RuntimeError) as persist_exc:
                    return fail(
                        request_id, "CAPTURE_ATTACHMENT_EXPORT_PAGE_PERSIST_FAILED",
                        "The failed export-page state was not persisted; the source capture remains unchanged.",
                        {"attachmentId": attachment_id, "rangeClosed": True, "exportComplete": False, "stopAttempted": False, "persistenceError": str(persist_exc)},
                    )
                return fail(request_id, "CAPTURE_ATTACHMENT_EXPORT_WRITE_FAILED", str(exc), {"attachmentId": attachment_id, "rangeClosed": True})
            next_token = str(read.data.get("continuationToken") or "")
            export_state.update(
                bytes=committed_bytes,
                nextExpectedSequence=expected_sequence,
                continuationToken=next_token,
                complete=not bool(next_token),
                incomplete=bool(gaps),
                gapCount=gaps,
                lastError="",
            )
            if export_state["complete"]:
                if expected_sequence < int(state.get("toSequence") or 0):
                    export_state["incomplete"] = True
                    export_state["gapCount"] = int(export_state["gapCount"]) + 1
                export_state["sha256"] = await asyncio.to_thread(self._sha256_file, output_path)
            state["export"] = export_state
            try:
                store.save_capture_attachment(state)
            except (OSError, RuntimeError) as exc:
                return fail(
                    request_id,
                    "CAPTURE_ATTACHMENT_EXPORT_INDEX_PERSIST_FAILED",
                    "The export candidate is retained for same-request recovery; its index was not confirmed.",
                    {
                        "attachmentId": attachment_id,
                        "rangeClosed": True,
                        "exportComplete": False,
                        "exportCandidateBytes": committed_bytes,
                        "persistenceError": str(exc),
                    },
                )
            result = self._capture_attachment_public(state)
            if not export_state["complete"]:
                result["nextAction"] = "Call detach again with the same attachmentId, requestKey, and continuationToken; do not stop the source capture."
            return ok(request_id, result)

    async def console_capture_list(
        self, count: int = 20, include_active: bool = True
    ) -> ToolResponse:
        request_id = new_id("req")
        if isinstance(count, bool) or not isinstance(count, int) or not isinstance(include_active, bool):
            return fail(request_id, "INVALID_PAYLOAD", "Console capture list arguments have invalid types.", {
                "field": "count" if isinstance(count, bool) or not isinstance(count, int) else "includeActive",
                "dispatchAttempted": False,
            })
        result = await self.dispatcher.call(
            request_id,
            "console.capture.list",
            {"count": max(1, min(count, 200)), "includeActive": include_active},
        )
        return self._sanitize_capture_response(result)

    async def console_capture_cleanup(
        self,
        older_than_days: int = 14,
        keep_latest: int = 20,
        dry_run: bool = True,
        confirm_token: str = "",
    ) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id,
            "console.capture.cleanup",
            {
                "olderThanDays": max(0, older_than_days),
                "keepLatest": max(0, keep_latest),
                "dryRun": dry_run,
                "confirmToken": confirm_token,
            },
        )

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

    async def editor_execute_command(self, command_name: str, expected_modal: dict | None = None) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(
            request_id, "editor.executeCommand", {"commandName": command_name, **({"expectedModal": expected_modal} if expected_modal is not None else {})}
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
