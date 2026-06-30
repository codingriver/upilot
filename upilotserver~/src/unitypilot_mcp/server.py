from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
import struct
from pathlib import Path
from typing import Any

import websockets
from websockets.exceptions import ConnectionClosed
from websockets.server import WebSocketServerProtocol

from .dispatcher import WsTransport
from .protocol import PROTOCOL_VERSION, from_wire, now_ms, to_wire
from .session_manager import SessionManager
from .state_store import CompileSnapshot, StateStore

logger = logging.getLogger("unitypilot.server")
wire_logger = logging.getLogger("unitypilot.wire")


def _short_session_id(session_id: str | None) -> str:
    return session_id[:12] if session_id else "?"


def _is_heartbeat_message_name(name: str | None) -> bool:
    return name == "session.heartbeat"


def _log_ws_message(direction: str, raw: str, *, session_id: str | None = None, message_type: str | None = None, name: str | None = None) -> None:
    if message_type == "heartbeat" or _is_heartbeat_message_name(name):
        return
    wire_logger.debug("WS %s [%s] %s", direction, _short_session_id(session_id), raw)


def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, str(default)))
    except ValueError:
        return default


class WsOrchestratorServer(WsTransport):
    """WebSocket server with unified disconnect handling.

    - **Any disconnect** (not only domain reload): pending command futures move to *suspended* and
      wait for reconnect + optional grace timer (``UNITYPILOT_DISCONNECT_GRACE_S``; ``0`` = wait forever).
    - **session.hello** after reconnect: restores suspended futures and **re-sends** commands so Unity
      (new domain) receives them again.
    - **domain_reload.starting**: optional extra grace (``UNITYPILOT_DOMAIN_RELOAD_BONUS_S``).
    - Stale socket handlers must not clear ``_ws`` or session if a newer connection took over.
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8765,
        heartbeat_interval_ms: int = 2000,
        mcp_label: str = "",
    ) -> None:
        self.host = host
        self.port = port
        self.heartbeat_interval_ms = heartbeat_interval_ms
        self.mcp_label = (mcp_label or "").strip()
        self.session_manager = SessionManager(heartbeat_timeout_ms=heartbeat_interval_ms * 3)
        self.state = StateStore()
        self._ws: WebSocketServerProtocol | None = None
        self._pending: dict[str, asyncio.Future] = {}
        self._suspended: dict[str, asyncio.Future] = {}
        self._domain_reloading = False
        self._compile_idle_event = asyncio.Event()
        self._compile_idle_event.set()
        self._server = None
        self._listening_event = asyncio.Event()
        self._stop_event = asyncio.Event()
        self._shutting_down = False
        self._reconnect_grace_task: asyncio.Task[None] | None = None
        self._grace_deadline_monotonic: float | None = None
        self._active_ws_connections: set[WebSocketServerProtocol] = set()

    @staticmethod
    def _abortive_linger_bytes() -> bytes:
        if os.name == "nt":
            return struct.pack("HH", 1, 0)
        return struct.pack("ii", 1, 0)

    @staticmethod
    def _close_timeout_seconds() -> float:
        return max(0.1, _env_float("UNITYPILOT_SOCKET_CLOSE_TIMEOUT_S", 1.5))

    async def _close_websocket(self, websocket: WebSocketServerProtocol | None, *, reason: str) -> None:
        if websocket is None:
            return

        remote = getattr(websocket, "remote_address", None)
        close_fn = getattr(websocket, "close", None)
        wait_closed_fn = getattr(websocket, "wait_closed", None)

        if callable(close_fn):
            try:
                await asyncio.wait_for(
                    close_fn(code=1001, reason=reason[:120]),
                    timeout=self._close_timeout_seconds(),
                )
                if callable(wait_closed_fn):
                    await asyncio.wait_for(wait_closed_fn(), timeout=self._close_timeout_seconds())
                logger.info("Closed WebSocket to %s gracefully (%s)", remote, reason)
                return
            except asyncio.TimeoutError:
                logger.warning("Graceful WebSocket close timed out for %s (%s); falling back to abort", remote, reason)
            except Exception as ex:
                logger.warning("Graceful WebSocket close failed for %s (%s): %s", remote, reason, ex)

        self._force_disconnect_websocket(websocket, reason=reason)

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
        self._server = await websockets.serve(self._handle, self.host, self.port)
        self._listening_event.set()
        logger.info("WebSocket server listening on %s:%s", self.host, self.port)
        try:
            await self._stop_event.wait()
        finally:
            logger.info("WebSocket server shutting down on %s:%s", self.host, self.port)
            self._shutting_down = True
            self._cancel_reconnect_grace()
            active_ws = self._ws
            self._ws = None
            await self._close_websocket(active_ws, reason="server shutdown")
            if self._server is not None:
                self._server.close()
                await self._server.wait_closed()
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
        grace_s = _env_float("UNITYPILOT_DISCONNECT_GRACE_S", 3600.0)
        if grace_s <= 0:
            logger.info(
                "UNITYPILOT_DISCONNECT_GRACE_S=%s — no reconnect deadline; suspended commands wait until Unity returns",
                grace_s,
            )
            return
        loop = asyncio.get_running_loop()
        deadline = loop.time() + grace_s
        self._grace_deadline_monotonic = deadline
        self._reconnect_grace_task = asyncio.create_task(self._reconnect_grace_at_deadline(deadline, grace_s))

    def _extend_grace_for_domain_reload(self) -> None:
        bonus = _env_float("UNITYPILOT_DOMAIN_RELOAD_BONUS_S", 600.0)
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
                            "message": "Unity 重连/域重载等待超时，请检查编辑器或增大 UNITYPILOT_DISCONNECT_GRACE_S",
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
            timeout_s = _env_float("UNITYPILOT_CALL_WAIT_READY_S", 300.0)
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

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        if not self._ws or not self.session_manager.active:
            return
        session_id = self.session_manager.active.session_id
        if name == "unityuiflow.results":
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
        await self._ws.send(raw)

    async def _resend_pending_commands(self) -> None:
        """After domain reload, Unity lost in-flight commands — send again with same ids."""
        for cmd_id in list(self._pending.keys()):
            rec = self.state.commands.get(cmd_id)
            if not rec:
                logger.warning("No CommandRecord for resend cmd=%s", cmd_id[:16])
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

    async def _handle(self, websocket: WebSocketServerProtocol) -> None:
        self._active_ws_connections.add(websocket)
        remote = websocket.remote_address
        logger.info("Unity client connected from %s (total ws=%d)", remote, len(self._active_ws_connections))
        prev = self._ws
        if prev is not None and prev is not websocket:
            logger.info("Closing previous WebSocket — new connection supersedes (latest client wins)")
            await self._close_websocket(prev, reason="superseded by newer UnityPilot connection")
        self._ws = websocket
        auth_box: list[str | None] = [None]

        if self._domain_reloading:
            logger.info("Unity TCP reconnected during domain reload — awaiting session.hello")
            self._domain_reloading = False

        heartbeat_task = asyncio.create_task(self._heartbeat_loop())
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
                    await self._handle_message(incoming, auth_box)
            except (ConnectionClosed, ConnectionResetError, OSError) as ex:
                close_code = getattr(ex, "code", None)
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
            heartbeat_task.cancel()
            superseded = self._ws is not websocket
            if self._ws is websocket:
                self._ws = None
            auth_session_id = auth_box[0]
            sid_log = auth_session_id or (
                self.session_manager.active.session_id if self.session_manager.active else "unknown"
            )
            logger.info("[%s] Unity client disconnected from %s", sid_log[:12], remote)
            self.session_manager.disconnect(auth_session_id)
            if self.state.compile.status == "compiling":
                self.state.compile.status = "unknown"
                self.state.compile.errors = []
            self._compile_idle_event.set()
            self.state.editor.connected = False

            if self._shutting_down:
                self._fail_all_pending_and_suspended("SERVER_STOPPED", "MCP 服务器已关闭")
            elif superseded:
                logger.info(
                    "[%s] Previous socket superseded — keeping in-flight pending commands for new session",
                    sid_log[:12],
                )
            else:
                n = len(self._pending)
                if n:
                    logger.info("[%s] Disconnect — suspending %d pending commands (await reconnect)", sid_log[:12], n)
                self._suspended.update(self._pending)
                self._pending.clear()
                self._schedule_reconnect_grace()

    async def _handle_message(self, message, auth_box: list[str | None] | None = None) -> None:
        if message.session_id:
            self.session_manager.touch(message.session_id)

        if message.type == "hello" and message.name == "session.hello":
            self._cancel_reconnect_grace()
            if self._suspended:
                logger.info(
                    "[%s] Restoring %d suspended commands after reconnect",
                    message.session_id[:12],
                    len(self._suspended),
                )
                self._pending.update(self._suspended)
                self._suspended.clear()
            elif self._pending:
                logger.info(
                    "[%s] session.hello with %d in-flight pending — latest connection wins, will resend",
                    message.session_id[:12],
                    len(self._pending),
                )
            self.state.compile = CompileSnapshot()
            self._compile_idle_event.set()
            self.session_manager.on_hello(message.session_id, message.payload)
            self.state.editor.connected = True
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
                "heartbeatIntervalMs": self.heartbeat_interval_ms,
                "mcpHost": self.host,
                "mcpPort": self.port,
                "unityProjectPath": unity_project_path,
                "mcpWorkingDirectory": mcp_cwd,
            }
            if self.mcp_label:
                hello_payload["mcpLabel"] = self.mcp_label
            ack = {
                "id": message.id,
                "type": "result",
                "name": "session.hello",
                "payload": hello_payload,
                "timestamp": now_ms(),
                "sessionId": message.session_id,
                "protocolVersion": PROTOCOL_VERSION,
            }
            if self._ws:
                ack_raw = json.dumps(ack, ensure_ascii=False)
                _log_ws_message(
                    "SEND",
                    ack_raw,
                    session_id=message.session_id,
                    message_type=ack["type"],
                    name=ack["name"],
                )
                await self._ws.send(ack_raw)
            await self._resend_pending_commands()
            return

        if message.type == "heartbeat":
            self.session_manager.on_heartbeat(message.session_id)
            return

        if message.type in ("result", "error"):
            fut = self._pending.pop(message.id, None)
            if fut and not fut.done():
                fut.set_result(to_wire(message))
            if message.name == "unityuiflow.results":
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
            logger.debug(
                "[%s] <<< EVENT %s  payload=%s",
                message.session_id[:12] if message.session_id else "?",
                message.name,
                json.dumps(message.payload, ensure_ascii=False) if message.payload else "{}",
            )
            if message.name == "domain_reload.starting":
                logger.info(
                    "[%s] Domain reload starting — suspension mode (grace may extend)",
                    message.session_id[:12] if message.session_id else "?",
                )
                self._domain_reloading = True
                self._extend_grace_for_domain_reload()
                return
            if message.name == "compile.status":
                self.state.update_compile_status(message.payload)
                self._sync_compile_idle_from_compile_status(message.payload)
            elif message.name == "compile.started":
                self.state.update_compile_lifecycle(message.payload)
                self._note_compile_busy(True)
            elif message.name == "compile.finished":
                self.state.update_compile_lifecycle(message.payload)
                self._note_compile_busy(False)
            elif message.name == "compile.pipeline.started":
                self.state.update_compile_pipeline(message.payload)
                self._note_compile_busy(True)
            elif message.name == "compile.pipeline.finished":
                self.state.update_compile_pipeline(message.payload)
                self._note_compile_busy(False)
            elif message.name == "compile.errors":
                self.state.update_compile_errors(message.payload)
            elif message.name == "editor.state":
                self.state.update_editor_state(message.payload)
            elif message.name == "playmode.changed":
                self.state.editor.play_mode_state = str(message.payload.get("state", self.state.editor.play_mode_state))

    async def _heartbeat_loop(self) -> None:
        while True:
            try:
                await asyncio.sleep(self.heartbeat_interval_ms / 1000)
                if not self._ws or not self.session_manager.active:
                    continue
                hb = {
                    "id": f"hb-{now_ms()}",
                    "type": "heartbeat",
                    "name": "session.heartbeat",
                    "payload": {},
                    "timestamp": now_ms(),
                    "sessionId": self.session_manager.active.session_id,
                    "protocolVersion": PROTOCOL_VERSION,
                }
                hb_raw = json.dumps(hb, ensure_ascii=False)
                await self._ws.send(hb_raw)
            except asyncio.CancelledError:
                raise
            except (ConnectionClosed, ConnectionResetError, OSError) as ex:
                logger.debug("Heartbeat loop stopping after socket close: %s", ex)
                return
