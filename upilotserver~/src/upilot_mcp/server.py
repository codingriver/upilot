from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
import sqlite3
import struct
import time
from pathlib import Path
from typing import Any

import websockets
from websockets.exceptions import ConnectionClosed
from websockets.server import WebSocketServerProtocol

from .dispatcher import WsTransport
from .env import env_float
from .protocol import PROTOCOL_VERSION, from_wire, now_ms, to_wire
from .process_identity import (
    MAIN_EDITOR_ROLE,
    classify_unity_process,
    paths_equal,
    process_creation_time,
    process_exists,
    query_unity_processes,
)
from .session_manager import SessionManager
from .state_store import CompileSnapshot, StateStore
from .version import version_payload

logger = logging.getLogger("upilot.server")
wire_logger = logging.getLogger("upilot.wire")

IDENTITY_CONTRACT_VERSION = 1
_CANDIDATE_HANDSHAKE_TIMEOUT_S = 10.0
_MAX_BRIDGE_MESSAGE_BYTES = 4 * 1024 * 1024


def _short_session_id(session_id: str | None) -> str:
    return session_id[:12] if session_id else "?"


def _is_heartbeat_message_name(name: str | None) -> bool:
    return name == "session.heartbeat"


def _log_ws_message(direction: str, raw: str, *, session_id: str | None = None, message_type: str | None = None, name: str | None = None) -> None:
    if message_type == "heartbeat" or _is_heartbeat_message_name(name):
        return
    wire_logger.debug("WS %s [%s] %s", direction, _short_session_id(session_id), raw)


class WsOrchestratorServer(WsTransport):
    """WebSocket server with unified disconnect handling.

    - Domain reloads suspend pending command futures and resend them after the
      new Unity domain announces ``session.hello``.
    - Ordinary disconnects fail pending commands. Replaying an arbitrary command
      after a socket timeout can duplicate writes or revive a timed-out request.
    - **domain_reload.starting**: optional extra grace (``UPILOT_DOMAIN_RELOAD_BONUS_S``).
    - Stale socket handlers must not clear ``_ws`` or session if a newer connection took over.
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8765,
        heartbeat_interval_ms: int = 2000,
        mcp_label: str = "",
        expected_project_path: str = "",
    ) -> None:
        self.host = host
        self.port = port
        self.heartbeat_interval_ms = heartbeat_interval_ms
        self.mcp_label = (mcp_label or "").strip()
        self.expected_project_path = self._resolve_expected_project_path(expected_project_path)
        self.session_manager = SessionManager(heartbeat_timeout_ms=heartbeat_interval_ms * 3)
        self.state = StateStore()
        if self.expected_project_path:
            self.state.configure_project(self.expected_project_path)
        self._ws: WebSocketServerProtocol | None = None
        self._pending: dict[str, asyncio.Future] = {}
        self._suspended: dict[str, asyncio.Future] = {}
        self._domain_reloading = False
        self._reconnected_after_domain_reload = False
        self._compile_idle_event = asyncio.Event()
        self._compile_idle_event.set()
        self._server = None
        self._listening_event = asyncio.Event()
        self._stop_event = asyncio.Event()
        self._shutting_down = False
        self._reconnect_grace_task: asyncio.Task[None] | None = None
        self._grace_deadline_monotonic: float | None = None
        self._active_ws_connections: set[WebSocketServerProtocol] = set()
        self._promotion_lock = asyncio.Lock()
        self._latest_session_rejection: dict[str, Any] = {}
        self._last_bridge_connected_at_ms = 0
        self._last_bridge_authenticated_at_ms = 0
        self._last_bridge_disconnected_at_ms = 0
        self._last_bridge_close_code = ""
        self._last_bridge_close_reason = ""

    @staticmethod
    def _resolve_expected_project_path(configured: str) -> str:
        raw = str(configured or "").strip()
        if not raw:
            explicit_config = os.getenv("UPILOT_CONFIG", "").strip()
            if explicit_config:
                try:
                    raw = str(Path(explicit_config).expanduser().resolve().parent.parent)
                except OSError:
                    raw = ""
        if not raw:
            candidate = Path.cwd()
            if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings").is_dir():
                raw = str(candidate)
        if not raw:
            return ""
        try:
            return str(Path(raw).resolve())
        except OSError:
            return os.path.abspath(raw)

    def session_identity_status(self) -> dict[str, Any]:
        session = self.session_manager.active
        current = {
            "sessionId": session.session_id if session else "",
            "processId": session.process_id if session else 0,
            "processCreatedAt": session.process_created_at if session else 0,
            "processRole": session.process_role if session else "",
            "identityContractVersion": session.identity_contract_version if session else 0,
            "verificationLevel": session.verification_level if session else "",
            "identityVerified": bool(session and session.identity_verified),
            "expectedProjectPath": self.expected_project_path,
        }
        return {"current": current, "latestRejection": dict(self._latest_session_rejection)}

    @staticmethod
    def _abortive_linger_bytes() -> bytes:
        if os.name == "nt":
            return struct.pack("HH", 1, 0)
        return struct.pack("ii", 1, 0)

    @staticmethod
    def _server_close_timeout_seconds() -> float:
        return max(0.1, env_float("UPILOT_SERVER_CLOSE_TIMEOUT_S", 0.5))

    async def _close_websocket(self, websocket: WebSocketServerProtocol | None, *, reason: str) -> None:
        if websocket is None:
            return

        self._force_disconnect_websocket(websocket, reason=reason)
        await asyncio.sleep(0)

    def _force_disconnect_websocket(self, websocket: WebSocketServerProtocol | None, *, reason: str) -> None:
        if websocket is None:
            return

        remote = getattr(websocket, "remote_address", None)
        try:
            websocket.fail_connection()
        except Exception as ex:
            logger.debug("fail_connection skipped for %s (%s): %s", remote, reason, ex)

        transport = getattr(websocket, "transport", None)
        if transport is None:
            logger.debug("No transport available for force disconnect %s (%s)", remote, reason)
            return

        try:
            transport.abort()
        except Exception as ex:
            logger.debug("transport.abort failed for %s (%s): %s", remote, reason, ex)

        raw_socket = getattr(transport, "_sock", None)
        if raw_socket is None:
            raw_socket = transport.get_extra_info("socket")
        if raw_socket is not None:
            try:
                raw_socket.setsockopt(
                    socket.SOL_SOCKET,
                    socket.SO_LINGER,
                    self._abortive_linger_bytes(),
                )
            except OSError as ex:
                logger.debug("set SO_LINGER=0 failed for %s (%s): %s", remote, reason, ex)
            try:
                if raw_socket.fileno() != -1:
                    raw_socket.close()
            except OSError as ex:
                logger.debug("raw socket close failed for %s (%s): %s", remote, reason, ex)

        logger.info("Force-aborted WebSocket to %s (%s)", remote, reason)

    async def start(self) -> None:
        self._stop_event.clear()
        self._listening_event.clear()
        self._server = await websockets.serve(
            self._handle, self.host, self.port, max_size=_MAX_BRIDGE_MESSAGE_BYTES,
        )
        self._listening_event.set()
        logger.info("WebSocket server listening on %s:%s", self.host, self.port)
        try:
            await self._stop_event.wait()
        finally:
            logger.info("WebSocket server shutting down on %s:%s", self.host, self.port)
            self._shutting_down = True
            self._cancel_reconnect_grace()
            active_connections = list(self._active_ws_connections)
            self._ws = None
            for websocket in active_connections:
                self._force_disconnect_websocket(websocket, reason="server shutdown")
            if self._server is not None:
                self._server.close()
                try:
                    await asyncio.wait_for(
                        self._server.wait_closed(),
                        timeout=self._server_close_timeout_seconds(),
                    )
                except asyncio.TimeoutError:
                    logger.warning(
                        "Timed out waiting for WebSocket server socket to close; continuing shutdown"
                    )
                self._server = None
            self.session_manager.disconnect(force=True)
            self._fail_all_pending_and_suspended("SERVER_STOPPED", "MCP 服务器已关闭")
            logger.info("WebSocket server stopped")

    async def wait_until_listening(self, timeout_s: float | None = None) -> bool:
        if self._listening_event.is_set():
            return True
        if timeout_s is None or timeout_s <= 0:
            await self._listening_event.wait()
            return True
        try:
            await asyncio.wait_for(self._listening_event.wait(), timeout=timeout_s)
            return True
        except asyncio.TimeoutError:
            return False

    def stop(self) -> None:
        self._shutting_down = True
        self._stop_event.set()

    def _cancel_reconnect_grace(self) -> None:
        if self._reconnect_grace_task and not self._reconnect_grace_task.done():
            self._reconnect_grace_task.cancel()
        self._reconnect_grace_task = None
        self._grace_deadline_monotonic = None

    def _schedule_reconnect_grace(self) -> None:
        """Start or replace grace timer until suspended commands are failed."""
        self._cancel_reconnect_grace()
        grace_s = env_float("UPILOT_DISCONNECT_GRACE_S", 3600.0)
        if grace_s <= 0:
            logger.info(
                "UPILOT_DISCONNECT_GRACE_S=%s — no reconnect deadline; suspended commands wait until Unity returns",
                grace_s,
            )
            return
        loop = asyncio.get_running_loop()
        deadline = loop.time() + grace_s
        self._grace_deadline_monotonic = deadline
        self._reconnect_grace_task = asyncio.create_task(self._reconnect_grace_at_deadline(deadline, grace_s))

    def _extend_grace_for_domain_reload(self) -> None:
        bonus = env_float("UPILOT_DOMAIN_RELOAD_BONUS_S", 600.0)
        if bonus <= 0:
            return
        loop = asyncio.get_running_loop()
        now = loop.time()
        prev = self._grace_deadline_monotonic
        new_deadline = max(prev if prev is not None else now, now + bonus)
        self._grace_deadline_monotonic = new_deadline
        self._cancel_reconnect_grace()
        self._reconnect_grace_task = asyncio.create_task(
            self._reconnect_grace_at_deadline(new_deadline, bonus),
        )
        logger.info(
            "Domain reload — extended reconnect grace deadline by %.0fs (bonus), new deadline in %.0fs",
            bonus,
            max(0.0, new_deadline - now),
        )

    async def _reconnect_grace_at_deadline(self, deadline: float, label_s: float) -> None:
        try:
            loop = asyncio.get_running_loop()
            delay = max(0.0, deadline - loop.time())
            await asyncio.sleep(delay)
            if self._shutting_down:
                return
            if not self._suspended:
                return
            logger.warning(
                "Reconnect grace expired (%.0fs window) — failing %d suspended commands",
                label_s,
                len(self._suspended),
            )
            for fut in self._suspended.values():
                if not fut.done():
                    fut.set_result({
                        "id": "",
                        "type": "error",
                        "name": "domain_reload_timeout",
                        "payload": {
                            "code": "DOMAIN_RELOAD_TIMEOUT",
                            "message": "Unity 重连/域重载等待超时，请检查编辑器或增大 UPILOT_DISCONNECT_GRACE_S",
                        },
                        "timestamp": now_ms(),
                        "sessionId": "",
                        "protocolVersion": PROTOCOL_VERSION,
                    })
            self._suspended.clear()
            self._domain_reloading = False
        except asyncio.CancelledError:
            raise

    def _fail_all_pending_and_suspended(self, code: str, message: str) -> None:
        err = {
            "id": "",
            "type": "error",
            "name": "connection_lost",
            "payload": {"code": code, "message": message},
            "timestamp": now_ms(),
            "sessionId": "",
            "protocolVersion": PROTOCOL_VERSION,
        }
        for fut in list(self._pending.values()) + list(self._suspended.values()):
            if not fut.done():
                fut.set_result(err)
        self._pending.clear()
        self._suspended.clear()

    def _suspend_or_fail_pending_on_disconnect(self, sid_log: str) -> None:
        """Only a declared domain reload may replay in-flight commands."""
        if self._domain_reloading:
            n = len(self._pending)
            if n:
                logger.info(
                    "[%s] Disconnect — suspending %d pending commands (await reconnect)",
                    sid_log[:12],
                    n,
                )
            self._suspended.update(self._pending)
            self._pending.clear()
            self._schedule_reconnect_grace()
            return

        n = len(self._pending) + len(self._suspended)
        if n:
            logger.warning(
                "[%s] Ordinary disconnect — failing %d pending commands instead of replaying them",
                sid_log[:12],
                n,
            )
        self._fail_all_pending_and_suspended(
            "CONNECTION_LOST",
            "Unity 连接中断，未完成命令不会在普通重连后自动重放",
        )

    def _note_compile_busy(self, busy: bool) -> None:
        if busy:
            self._compile_idle_event.clear()
        else:
            self._compile_idle_event.set()

    def _sync_compile_idle_from_compile_status(self, payload: dict[str, Any]) -> None:
        status = str(payload.get("status", "")).lower()
        if status in ("started", "in_progress", "compiling"):
            self._note_compile_busy(True)
        elif status in ("finished", "done", "complete"):
            self._note_compile_busy(False)

    def reconcile_editor_compile_busy(self, is_compiling: bool) -> None:
        if is_compiling and self._compile_idle_event.is_set():
            self._note_compile_busy(True)

    def sync_compile_state_from_editor(self, is_compiling: bool) -> None:
        self._note_compile_busy(is_compiling)

    async def wait_for_compile_idle(self, timeout: float | None) -> bool:
        if self._compile_idle_event.is_set():
            return True
        if timeout is None or timeout <= 0:
            await self._compile_idle_event.wait()
            return True
        try:
            await asyncio.wait_for(self._compile_idle_event.wait(), timeout=timeout)
            return True
        except asyncio.TimeoutError:
            return False

    async def wait_until_ready(self, timeout_s: float | None = None) -> bool:
        """Wait until a live WebSocket + authenticated session exists (e.g. reconnect after domain reload)."""
        if timeout_s is None:
            timeout_s = env_float("UPILOT_CALL_WAIT_READY_S", 300.0)
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout_s if timeout_s > 0 else None
        while True:
            if self.is_ready():
                return True
            if deadline is not None and loop.time() >= deadline:
                return self.is_ready()
            await asyncio.sleep(min(0.2, (deadline - loop.time()) if deadline else 0.2))

    def is_ready(self) -> bool:
        return self._ws is not None and self.session_manager.is_connected()

    def register_pending(self, command_id: str) -> asyncio.Future:
        loop = asyncio.get_running_loop()
        fut = loop.create_future()
        self._pending[command_id] = fut
        return fut

    def unregister_pending(self, command_id: str) -> None:
        future = self._pending.pop(command_id, None)
        if future is None:
            future = self._suspended.pop(command_id, None)
        if future is not None and not future.done():
            future.cancel()

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        websocket = self._ws
        active = self.session_manager.active
        if websocket is None or active is None:
            return
        session_id = active.session_id
        if name == "upilot_flow.results":
            logger.debug("[%s] >>> %s  cmd=%s", session_id[:12], name, command_id[:16])
        else:
            logger.info("[%s] >>> %s  cmd=%s", session_id[:12], name, command_id[:16])
        msg = {
            "id": command_id,
            "type": "command",
            "name": name,
            "payload": payload,
            "timestamp": now_ms(),
            "sessionId": session_id,
            "protocolVersion": PROTOCOL_VERSION,
        }
        raw = json.dumps(msg, ensure_ascii=False)
        _log_ws_message("SEND", raw, session_id=session_id, message_type=msg["type"], name=msg["name"])
        if websocket is not self._ws or self.session_manager.active is not active:
            raise ConnectionError("Editor session changed before command dispatch")
        await websocket.send(raw)

    @staticmethod
    def _reload_query_can_replay(name: str, payload: dict[str, Any]) -> bool:
        if name in {"resource.editorState", "test.status", "compile.errors.get"}:
            return True
        return name == "test.results" and bool(str(payload.get("runGuid") or "").strip())

    async def _resend_pending_commands(self) -> None:
        """Resume only explicitly safe queries after an authenticated domain reload."""
        for cmd_id in list(self._pending.keys()):
            rec = self.state.commands.get(cmd_id)
            future = self._pending.get(cmd_id)
            if future is None:
                continue
            if future.done():
                self._pending.pop(cmd_id, None)
                continue
            if rec is None or not self._reload_query_can_replay(rec.name, rec.payload):
                self._pending.pop(cmd_id, None)
                maintenance = rec is not None and rec.name == "service.restart"
                error = {
                    "code": "SERVICE_RESTART_RECOVERY_REQUIRED" if maintenance else "COMMAND_RECOVERY_REQUIRED",
                    "message": ("Service maintenance is never replayed after reload; inspect its persisted identity."
                                if maintenance else "Command outcome is unknown after domain reload; do not replay it."),
                    "detail": ({"maintenanceId": rec.payload.get("maintenanceId", ""),
                                "nextAction": "Query unity_mcp_status.aiServiceMaintenance."}
                               if maintenance else {
                                   "commandId": cmd_id, "commandName": rec.name if rec else "unknown",
                                   "outcome": "unknown", "replayAttempted": False,
                                   "nextAction": "Observe the original operation or task identity; do not retry its start.",
                               }),
                }
                if rec is None:
                    logger.warning("Missing CommandRecord during reload recovery cmd=%s", cmd_id[:16])
                future.set_result({"id": cmd_id, "type": "error", "name": "connection_lost",
                                   "payload": error})
                if rec is not None:
                    self.state.mark_failed(cmd_id, error)
                continue
            try:
                await self.send_command(cmd_id, rec.name, rec.payload)
            except Exception as ex:
                logger.warning("resend failed cmd=%s: %s", cmd_id[:16], ex)

    def _fail_all_pending(self, code: str, message: str) -> None:
        for fut in list(self._pending.values()):
            if not fut.done():
                fut.set_result({
                    "id": "",
                    "type": "error",
                    "name": "connection_lost",
                    "payload": {"code": code, "message": message},
                    "timestamp": now_ms(),
                    "sessionId": "",
                    "protocolVersion": PROTOCOL_VERSION,
                })
        self._pending.clear()

    @property
    def ws_connection_count(self) -> int:
        return len(self._active_ws_connections)

    async def _candidate_timeout(
        self,
        websocket: WebSocketServerProtocol,
        auth_box: list[str | None],
    ) -> None:
        try:
            await asyncio.sleep(_CANDIDATE_HANDSHAKE_TIMEOUT_S)
            if auth_box[0] is None and websocket is not self._ws:
                self._record_session_rejection(
                    "SESSION_HANDSHAKE_TIMEOUT",
                    "Candidate did not complete the required identity handshake within 10 seconds.",
                    {},
                )
                await self._close_websocket(websocket, reason="candidate identity handshake timeout")
        except asyncio.CancelledError:
            raise

    def _record_session_rejection(self, code: str, reason: str, detail: dict[str, Any]) -> None:
        self._latest_session_rejection = {
            "code": code,
            "reason": reason,
            "detail": detail,
            "rejectedAt": now_ms(),
        }

    async def _send_candidate_ack(
        self,
        websocket: WebSocketServerProtocol,
        message,
        payload: dict[str, Any],
    ) -> None:
        ack = {
            "id": message.id,
            "type": "result",
            "name": "session.hello",
            "payload": payload,
            "timestamp": now_ms(),
            "sessionId": message.session_id,
            "protocolVersion": PROTOCOL_VERSION,
        }
        raw = json.dumps(ack, ensure_ascii=False)
        _log_ws_message(
            "SEND",
            raw,
            session_id=message.session_id,
            message_type=ack["type"],
            name=ack["name"],
        )
        await websocket.send(raw)

    def _probe_candidate_identity(self, payload: dict[str, Any]) -> dict[str, Any]:
        contract_version = int(payload.get("identityContractVersion") or 0)
        project_path = str(payload.get("projectPath") or "").strip()
        process_id = int(payload.get("processId") or 0)
        process_created_at = int(payload.get("processCreatedAt") or 0)
        process_role = str(payload.get("processRole") or "").strip()
        common = {
            "identityContractVersion": contract_version,
            "projectPath": project_path,
            "processId": process_id,
            "processCreatedAt": process_created_at,
            "processRole": process_role,
            "expectedProjectPath": self.expected_project_path,
        }
        if contract_version != IDENTITY_CONTRACT_VERSION:
            return {**common, "accepted": False, "code": "IDENTITY_CONTRACT_MISMATCH", "reason": "Server and Bridge identity contracts must be upgraded together."}
        if not self.expected_project_path:
            return {**common, "accepted": False, "code": "EXPECTED_PROJECT_UNKNOWN", "reason": "The Server has no trusted startup project path."}
        if not project_path or not paths_equal(project_path, self.expected_project_path):
            return {**common, "accepted": False, "code": "PROJECT_IDENTITY_MISMATCH", "reason": "Candidate project does not match the Server startup project."}
        if process_id <= 0 or process_created_at <= 0:
            return {**common, "accepted": False, "code": "PROCESS_IDENTITY_INCOMPLETE", "reason": "Candidate PID and process creation time are required."}
        if process_role != MAIN_EDITOR_ROLE:
            return {**common, "accepted": False, "code": "AUXILIARY_EDITOR_ROLE", "reason": "Only the main Unity Editor may own the Bridge session."}

        if os.name != "nt":
            return {
                **common,
                "accepted": True,
                "identityVerified": True,
                "verificationLevel": "contract-role-project-no-os-process-proof",
                "platformVerificationLimited": True,
            }

        rows, query_diagnostics = query_unity_processes()
        if not query_diagnostics.get("processQuerySucceeded"):
            return {
                **common,
                **query_diagnostics,
                "accepted": False,
                "code": "PROCESS_QUERY_FAILED",
                "reason": "The Windows Unity process identity could not be queried.",
            }
        row = next((item for item in rows if int(item.get("ProcessId") or 0) == process_id), None)
        if row is None:
            return {**common, **query_diagnostics, "accepted": False, "code": "UNITY_PROCESS_NOT_FOUND", "reason": "The reported Unity process is not running."}
        classification = classify_unity_process(row, Path(self.expected_project_path))
        if not classification.get("eligibleMainEditor"):
            return {
                **common,
                **query_diagnostics,
                "classification": classification,
                "accepted": False,
                "code": "UNITY_PROCESS_NOT_MAIN_EDITOR",
                "reason": "The reported process is not the verified main Editor for this project.",
            }
        kernel_created_at = process_creation_time(process_id)
        cim_created_at = int(row.get("ProcessCreatedAt") or 0)
        same_kernel_identity = bool(kernel_created_at and kernel_created_at // 10 == process_created_at // 10)
        same_cim_identity = bool(cim_created_at and cim_created_at // 10 == process_created_at // 10)
        if not same_kernel_identity or not same_cim_identity:
            return {
                **common,
                **query_diagnostics,
                "classification": classification,
                "kernelProcessCreatedAt": kernel_created_at,
                "queriedProcessCreatedAt": cim_created_at,
                "accepted": False,
                "code": "PROCESS_CREATION_TIME_MISMATCH",
                "reason": "The reported PID creation identity is stale or does not match Windows.",
            }
        return {
            **common,
            **query_diagnostics,
            "classification": classification,
            "accepted": True,
            "identityVerified": True,
            "verificationLevel": "windows-pid-creation-executable-role-project",
        }

    def _active_process_liveness(self) -> str:
        active = self.session_manager.active
        if active is None or active.process_id <= 0:
            return "dead"
        if os.name != "nt":
            return "alive" if process_exists(active.process_id) else "dead"
        rows, diagnostics = query_unity_processes()
        if not diagnostics.get("processQuerySucceeded"):
            return "unknown"
        row = next((item for item in rows if int(item.get("ProcessId") or 0) == active.process_id), None)
        if row is None:
            return "dead"
        observed = process_creation_time(active.process_id)
        if not observed:
            return "unknown"
        return "alive" if observed // 10 == active.process_created_at // 10 else "dead"

    async def _admit_candidate(self, websocket: WebSocketServerProtocol, message) -> tuple[bool, dict[str, Any]]:
        try:
            probe = await asyncio.wait_for(
                asyncio.to_thread(self._probe_candidate_identity, dict(message.payload)),
                timeout=_CANDIDATE_HANDSHAKE_TIMEOUT_S,
            )
        except asyncio.TimeoutError:
            probe = {"accepted": False, "code": "PROCESS_QUERY_TIMEOUT", "reason": "Candidate identity verification exceeded 10 seconds."}
        except (OSError, TypeError, ValueError) as exc:
            probe = {"accepted": False, "code": "PROCESS_IDENTITY_INVALID", "reason": str(exc)}
        if not probe.get("accepted"):
            return False, probe

        incoming_identity = (
            int(message.payload.get("processId") or 0),
            int(message.payload.get("processCreatedAt") or 0),
            str(message.payload.get("projectPath") or ""),
        )
        async with self._promotion_lock:
            active = self.session_manager.active
            if active is not None and active.process_id > 0:
                active_identity = (active.process_id, active.process_created_at, active.project_path)
                same_process = (
                    incoming_identity[:2] == active_identity[:2]
                    and paths_equal(incoming_identity[2], active_identity[2])
                )
                if not same_process:
                    liveness = await asyncio.to_thread(self._active_process_liveness)
                    if liveness != "dead":
                        return False, {
                            **probe,
                            "accepted": False,
                            "code": "ACTIVE_EDITOR_CONFLICT",
                            "reason": "A different verified main Editor still owns the session.",
                            "activeProcessId": active.process_id,
                            "activeProcessCreatedAt": active.process_created_at,
                            "activeProcessLiveness": liveness,
                        }
            previous = self._ws
            self._ws = websocket
            message.payload["verificationLevel"] = str(probe.get("verificationLevel") or "")
            message.payload["identityVerified"] = bool(probe.get("identityVerified"))
            self.session_manager.on_hello(message.session_id, message.payload)
            self._last_bridge_authenticated_at_ms = now_ms()
            if previous is not None and previous is not websocket:
                asyncio.create_task(self._close_websocket(previous, reason="replaced by verified reconnect"))
        return True, probe

    async def _handle(self, websocket: WebSocketServerProtocol) -> None:
        self._active_ws_connections.add(websocket)
        self._last_bridge_connected_at_ms = now_ms()
        remote = websocket.remote_address
        logger.info("Unity client connected from %s (total ws=%d)", remote, len(self._active_ws_connections))
        auth_box: list[str | None] = [None]
        heartbeat_task: asyncio.Task[None] | None = None
        candidate_timeout_task = asyncio.create_task(self._candidate_timeout(websocket, auth_box))
        try:
            try:
                async for raw in websocket:
                    incoming = from_wire(json.loads(raw))
                    _log_ws_message(
                        "RECV",
                        raw,
                        session_id=incoming.session_id,
                        message_type=incoming.type,
                        name=incoming.name,
                    )
                    await self._handle_message(incoming, auth_box, websocket)
                    if auth_box[0] and heartbeat_task is None:
                        candidate_timeout_task.cancel()
                        heartbeat_task = asyncio.create_task(self._heartbeat_loop(websocket, auth_box[0]))
            except (ConnectionClosed, ConnectionResetError, OSError) as ex:
                close_code = getattr(ex, "code", None)
                if close_code == 1009:
                    logger.error("Bridge frame exceeded 4 MiB session=%s at=%s commandId=unknown limitBytes=%s",
                                 auth_box[0] or "unknown", now_ms(), _MAX_BRIDGE_MESSAGE_BYTES)
                    self._oversize_count = getattr(self, "_oversize_count", 0) + 1
                    self._oversize_observations = (getattr(self, "_oversize_observations", []) + [
                        {"sessionId": auth_box[0], "sourceEvent": "unknown", "observedAt": now_ms(),
                         "limitBytes": _MAX_BRIDGE_MESSAGE_BYTES, "commandId": "unknown", "closeCode": 1009}])[-8:]
                if self._shutting_down:
                    logger.debug("WebSocket closed during shutdown from %s: %s", remote, ex)
                else:
                    logger.info(
                        "WebSocket disconnected from %s (code=%s): %s",
                        remote,
                        close_code if close_code is not None else "?",
                        ex,
                    )
        finally:
            self._active_ws_connections.discard(websocket)
            candidate_timeout_task.cancel()
            if heartbeat_task is not None:
                heartbeat_task.cancel()
            auth_session_id = auth_box[0]
            sid_log = auth_session_id or (
                self.session_manager.active.session_id if self.session_manager.active else "unknown"
            )
            logger.info("[%s] Unity client disconnected from %s", sid_log[:12], remote)
            active = self.session_manager.active
            owns_active_session = bool(
                auth_session_id
                and self._ws is websocket
                and active is not None
                and active.session_id == auth_session_id
            )
            if not owns_active_session:
                logger.info("[%s] Candidate/stale socket closed without changing active Editor state", sid_log[:12])
                return
            self._last_bridge_disconnected_at_ms = now_ms()
            close_code = getattr(websocket, "close_code", None)
            self._last_bridge_close_code = str(close_code) if close_code is not None else ""
            self._last_bridge_close_reason = str(getattr(websocket, "close_reason", "") or "")[:256]
            self._ws = None
            self.session_manager.disconnect(auth_session_id)
            if self.state.compile.status in ("queued", "accepted", "compiling", "verifying"):
                if self._domain_reloading:
                    self.state.compile.status = "compiling"
                    self.state.compile.phase = "domain_reload"
                self.state.compile.last_progress_at = now_ms()
            self._compile_idle_event.set()
            self.state.update_editor_state(
                {
                    "connected": False,
                    "authoritative": False,
                    "source": "bridge-disconnected",
                    "sessionId": auth_session_id or self.state.editor.session_id,
                    "updatedAt": now_ms(),
                }
            )

            if self._shutting_down:
                self._fail_all_pending_and_suspended("SERVER_STOPPED", "MCP 服务器已关闭")
            else:
                self._suspend_or_fail_pending_on_disconnect(sid_log)

    def _notify_editor_execution_state(self) -> None:
        callback = getattr(self, "on_editor_execution_state", None)
        if not callable(callback):
            return
        result = callback(self.state.execution_state())
        if asyncio.iscoroutine(result):
            asyncio.create_task(result)

    async def _handle_message(
        self,
        message,
        auth_box: list[str | None] | None = None,
        websocket: WebSocketServerProtocol | None = None,
    ) -> None:
        if message.type == "hello" and message.name == "session.hello":
            candidate = websocket or self._ws
            if candidate is None:
                return
            if not message.session_id:
                rejection = {
                    "accepted": False,
                    "code": "SESSION_ID_REQUIRED",
                    "reason": "sessionId is required for the identity handshake.",
                }
                self._record_session_rejection(rejection["code"], rejection["reason"], {})
                await self._send_candidate_ack(candidate, message, {
                    "accepted": False,
                    "identityContractVersion": IDENTITY_CONTRACT_VERSION,
                    "verificationLevel": "rejected",
                    "rejectionCode": rejection["code"],
                    "rejectionReason": rejection["reason"],
                })
                return
            if auth_box is not None and auth_box[0] is not None:
                return
            admitted, identity = await self._admit_candidate(candidate, message)
            if not admitted:
                code = str(identity.get("code") or "SESSION_IDENTITY_REJECTED")
                reason = str(identity.get("reason") or "Candidate identity was rejected.")
                detail = {key: value for key, value in identity.items() if key not in {"accepted", "code", "reason"}}
                self._record_session_rejection(code, reason, detail)
                active = self.session_manager.active
                logger.warning(
                    "Rejected Unity Bridge candidate: code=%s reason=%s candidatePid=%s "
                    "candidateRole=%s candidateProject=%s activeSessionId=%s activePid=%s",
                    code,
                    reason,
                    message.payload.get("processId", 0),
                    message.payload.get("processRole", ""),
                    message.payload.get("projectPath", ""),
                    getattr(active, "session_id", "") if active else "",
                    getattr(active, "process_id", 0) if active else 0,
                )
                await self._send_candidate_ack(candidate, message, {
                    "accepted": False,
                    "identityContractVersion": IDENTITY_CONTRACT_VERSION,
                    "verificationLevel": "rejected",
                    "rejectionCode": code,
                    "rejectionReason": reason,
                })
                await self._close_websocket(candidate, reason=f"identity rejected: {code}")
                return
            self._cancel_reconnect_grace()
            preserve_compile_snapshot = self._domain_reloading
            if preserve_compile_snapshot:
                logger.info("Unity reconnected during domain reload after identity verification")
                self._reconnected_after_domain_reload = True
                self._domain_reloading = False
            raw_project_for_state = str(message.payload.get("projectPath", "") or "").strip()
            if raw_project_for_state:
                try:
                    self.state.configure_project(raw_project_for_state)
                except (OSError, sqlite3.Error) as ex:
                    logger.warning("Could not configure persistent Editor state: %s", ex)
            preserve_compile_snapshot = self._reconnected_after_domain_reload
            self._reconnected_after_domain_reload = False
            previous_process_id = self.state.editor.process_id
            incoming_process_id = int(message.payload.get("processId") or 0)
            if self._suspended:
                if preserve_compile_snapshot and (
                    not previous_process_id
                    or not incoming_process_id
                    or previous_process_id == incoming_process_id
                ):
                    logger.info(
                        "[%s] Restoring %d suspended commands after domain reload",
                        message.session_id[:12],
                        len(self._suspended),
                    )
                    self._pending.update(self._suspended)
                    self._suspended.clear()
                else:
                    logger.warning(
                        "[%s] Unity process changed without a domain-reload contract; failing %d suspended commands",
                        message.session_id[:12],
                        len(self._suspended),
                    )
                    self._fail_all_pending_and_suspended(
                        "EDITOR_RESTARTED",
                        "Unity 进程已重启，未完成命令不会自动重放",
                    )
            elif self._pending:
                logger.info(
                    "[%s] session.hello with %d in-flight pending — latest connection wins, will resend",
                    message.session_id[:12],
                    len(self._pending),
                )
            if not preserve_compile_snapshot and not self.state.producer_epoch:
                self.state.compile = CompileSnapshot()
            if preserve_compile_snapshot and self.state.compile.status in (
                "queued",
                "accepted",
                "compiling",
                "compiler_finished",
                "domain_reload",
                "verifying",
            ):
                self._compile_idle_event.clear()
            else:
                self._compile_idle_event.set()
            self.state.reset_editor_session(
                message.session_id,
                incoming_process_id,
            )
            if auth_box is not None:
                auth_box[0] = message.session_id
            logger.info(
                "[%s] Session established  unity=%s  project=%s  platform=%s",
                message.session_id[:12],
                message.payload.get("unityVersion", "?"),
                message.payload.get("projectPath", "?"),
                message.payload.get("platform", "?"),
            )
            raw_project = str(message.payload.get("projectPath", "") or "").strip()
            unity_project_path = raw_project
            if raw_project:
                try:
                    unity_project_path = str(Path(raw_project).resolve())
                except OSError:
                    unity_project_path = raw_project

            try:
                mcp_cwd = str(Path.cwd().resolve())
            except OSError:
                mcp_cwd = str(Path.cwd())

            hello_payload: dict[str, Any] = {
                "accepted": True,
                "identityContractVersion": IDENTITY_CONTRACT_VERSION,
                "verificationLevel": str(identity.get("verificationLevel") or ""),
                "heartbeatIntervalMs": self.heartbeat_interval_ms,
                "mcpHost": self.host,
                "mcpPort": self.port,
                "unityProjectPath": unity_project_path,
                "mcpWorkingDirectory": mcp_cwd,
            }
            hello_payload.update(version_payload())
            if self.mcp_label:
                hello_payload["mcpLabel"] = self.mcp_label
            await self._send_candidate_ack(candidate, message, hello_payload)
            if preserve_compile_snapshot:
                await self._resend_pending_commands()
            return

        if websocket is not None:
            active = self.session_manager.active
            authenticated_session = auth_box[0] if auth_box is not None else None
            if (
                websocket is not self._ws
                or active is None
                or not authenticated_session
                or message.session_id != authenticated_session
                or active.session_id != authenticated_session
            ):
                logger.debug("Ignoring message from unauthenticated or stale socket: %s", message.name)
                return
        if message.session_id:
            self.session_manager.touch(message.session_id)

        if message.type == "heartbeat":
            self.session_manager.on_heartbeat(message.session_id)
            if message.payload:
                heartbeat_context = dict(message.payload)
                heartbeat_context.setdefault("connected", True)
                heartbeat_context.setdefault("sessionId", message.session_id)
                heartbeat_context.setdefault("source", "bridge-heartbeat")
                heartbeat_context.setdefault("authoritative", True)
                if int(heartbeat_context.get("stateContractVersion") or 0) >= 2:
                    accepted = self.state.update_editor_execution_state(heartbeat_context)
                    if accepted:
                        self._notify_editor_execution_state()
                else:
                    self.state.update_editor_state(heartbeat_context)
            return

        if message.type in ("result", "error"):
            fut = self._pending.pop(message.id, None)
            if fut and not fut.done():
                fut.set_result(to_wire(message))
            if message.name == "upilot_flow.results":
                logger.debug(
                    "[%s] <<< %s  type=%s  cmd=%s",
                    message.session_id[:12] if message.session_id else "?",
                    message.name,
                    message.type,
                    message.id[:16] if message.id else "?",
                )
            else:
                log_fn = logger.info if message.type == "result" else logger.warning
                log_fn(
                    "[%s] <<< %s  type=%s  cmd=%s",
                    message.session_id[:12] if message.session_id else "?",
                    message.name,
                    message.type,
                    message.id[:16] if message.id else "?",
                )
            return

        if message.type == "event":
            if message.name == "bridge.payload_oversize":
                logger.error("Bridge event lost due to size session=%s source=%s bytes=%s limit=%s",
                             message.session_id, message.payload.get("sourceEvent"),
                             message.payload.get("actualBytes"), message.payload.get("limitBytes"))
                self._oversize_count = getattr(self, "_oversize_count", 0) + 1
                self._oversize_observations = (getattr(self, "_oversize_observations", []) + [
                    {"sessionId": message.session_id, "sourceEvent": message.payload.get("sourceEvent"),
                     "observedAt": now_ms(), "actualBytes": message.payload.get("actualBytes"),
                     "limitBytes": message.payload.get("limitBytes"),
                     "commandId": "unknown"}])[-8:]
                return
            logger.debug(
                "[%s] <<< EVENT %s  payload=%s",
                message.session_id[:12] if message.session_id else "?",
                message.name,
                json.dumps(message.payload, ensure_ascii=False) if message.payload else "{}",
            )
            if message.name == "editor.execution_state":
                accepted = self.state.update_editor_execution_state(message.payload)
                if not accepted:
                    logger.debug("Rejected stale/duplicate execution snapshot: %s", message.payload.get("snapshotId"))
                    return
                phase = str(self.state.compile.phase or "").lower()
                self._note_compile_busy(phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"})
                if str(self.state.transition).lower() == "domain_reload_starting":
                    self._domain_reloading = True
                    self._extend_grace_for_domain_reload()
                self._notify_editor_execution_state()
                return
            if message.name == "domain_reload.starting":
                logger.info(
                    "[%s] Domain reload starting — suspension mode (grace may extend)",
                    message.session_id[:12] if message.session_id else "?",
                )
                self._domain_reloading = True
                compile_phase = str(message.payload.get("compilePhase") or "").strip().lower()
                if compile_phase in ("domain_reload", "domainreload") or bool(message.payload.get("isCompiling")) or self.state.compile.status in (
                    "queued",
                    "accepted",
                    "compiling",
                    "verifying",
                ):
                    self.state.compile.status = "compiling"
                    self.state.compile.phase = "domain_reload"
                    self.state.compile.last_progress_at = now_ms()
                    self.state.editor.is_compiling = True
                self._extend_grace_for_domain_reload()
                return
            if message.name == "compile.status":
                if not self.state.producer_epoch:
                    self.state.update_compile_status(message.payload)
                    self._sync_compile_idle_from_compile_status(message.payload)
            elif message.name == "compile.started":
                if not self.state.producer_epoch:
                    self.state.update_compile_lifecycle(message.payload)
                    self._note_compile_busy(True)
            elif message.name == "compile.finished":
                if not self.state.producer_epoch:
                    self.state.update_compile_lifecycle(message.payload)
                    self._note_compile_busy(False)
            elif message.name == "compile.pipeline.started":
                if not self.state.producer_epoch:
                    self.state.update_compile_pipeline(message.payload)
                    self._note_compile_busy(True)
            elif message.name == "compile.pipeline.finished":
                if not self.state.producer_epoch:
                    self.state.update_compile_pipeline(message.payload)
                    self._note_compile_busy(False)
            elif message.name == "compile.errors":
                self.state.update_compile_errors(message.payload)
            elif message.name == "editor.state":
                if not self.state.producer_epoch:
                    self.state.update_editor_state(message.payload)
            elif message.name == "playmode.changed":
                if not self.state.producer_epoch:
                    state = str(message.payload.get("state", self.state.editor.play_mode_state))
                    self.state.update_editor_state(
                        {
                            "connected": True,
                            "playModeState": state,
                            "authoritative": True,
                            "source": "playmode.changed",
                            "sessionId": message.session_id,
                        }
                    )

    async def _heartbeat_loop(
        self,
        websocket: WebSocketServerProtocol,
        session_id: str,
    ) -> None:
        while True:
            try:
                await asyncio.sleep(self.heartbeat_interval_ms / 1000)
                active = self.session_manager.active
                if websocket is not self._ws or active is None or active.session_id != session_id:
                    return
                hb = {
                    "id": f"hb-{now_ms()}",
                    "type": "heartbeat",
                    "name": "session.heartbeat",
                    "payload": {},
                    "timestamp": now_ms(),
                    "sessionId": session_id,
                    "protocolVersion": PROTOCOL_VERSION,
                }
                hb_raw = json.dumps(hb, ensure_ascii=False)
                await websocket.send(hb_raw)
            except asyncio.CancelledError:
                raise
            except (ConnectionClosed, ConnectionResetError, OSError) as ex:
                logger.debug("Heartbeat loop stopping after socket close: %s", ex)
                return
