from __future__ import annotations

import asyncio
import json
import logging
import os
import sqlite3
import time
import uuid
from dataclasses import dataclass, field
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
from .socket_shutdown import enable_abortive_close
from .state_store import CompileSnapshot, StateStore
from .version import version_payload

logger = logging.getLogger("upilot.server")
wire_logger = logging.getLogger("upilot.wire")

IDENTITY_CONTRACT_VERSION = 1
BRIDGE_READY_CONTRACT_VERSION = 1
_CANDIDATE_HANDSHAKE_TIMEOUT_S = 10.0
_MAX_BRIDGE_MESSAGE_BYTES = 4 * 1024 * 1024
_EDITOR_PUMP_STALE_MS = 5000
_EDITOR_STALL_LOG_INTERVAL_MS = 10000


@dataclass
class _BridgeConnection:
    websocket: Any
    session: Any
    hello_id: str
    lifecycle_id: str
    instance_id: str
    deadline: float
    accepted_at: int = field(default_factory=now_ms)
    generation: str = field(default_factory=lambda: uuid.uuid4().hex)
    bootstrap_id: str = ""
    bootstrap_deadline: float = 0.0
    nonce: str = ""
    phase: str = "waiting_bridge_ready"
    receipt: dict[str, Any] | None = None
    timeout_task: asyncio.Task | None = None
    resume_queries: bool = False


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

    - Domain reloads suspend pending futures; only allowlisted read-only queries
      may resume after bootstrap acknowledgement, within their original observation budget.
    - Ordinary disconnects fail pending commands. Replaying an arbitrary command
      after a socket timeout can duplicate writes or revive a timed-out request.
    - **domain_reload.starting**: optional extra grace (``UPILOT_DOMAIN_RELOAD_BONUS_S``).
    - Stale socket handlers must not clear ``_ws`` or session if a newer connection took over.
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8765,
        heartbeat_interval_ms: int = 3000,
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
        self._pending_generation: dict[str, str] = {}
        self._suspended: dict[str, asyncio.Future] = {}
        self._domain_reloading = False
        self._compile_idle_event = asyncio.Event()
        self._compile_idle_event.set()
        self._server = None
        self._listening_event = asyncio.Event()
        self._stop_event = asyncio.Event()
        self._shutting_down = False
        self._write_batch_deadline_timer: asyncio.TimerHandle | None = None
        self._write_batch_deadline_at: int | None = None
        self._write_batch_deadline_loop: asyncio.AbstractEventLoop | None = None
        self._expiring_write_batches = False
        self._reconnect_grace_task: asyncio.Task[None] | None = None
        self._grace_deadline_monotonic: float | None = None
        self._active_ws_connections: set[WebSocketServerProtocol] = set()
        self._promotion_lock = asyncio.Lock()
        self._send_lock = asyncio.Lock()
        self._maintenance_pending: dict[str, str] = {}
        self._connection: _BridgeConnection | None = None
        self._producer_process: tuple[int, int] | None = None
        self._bound_producer_epoch = ""
        self._retired_producer_epochs: set[str] = set()
        self._reload_grace_extended = False
        self._grace_disconnect_started = False
        self.state.live_update_allowed = self.is_ready
        self._latest_session_rejection: dict[str, Any] = {}
        self._last_bridge_connected_at_ms = 0
        self._last_bridge_authenticated_at_ms = 0
        self._last_bridge_disconnected_at_ms = 0
        self._last_bridge_close_code = ""
        self._last_bridge_close_reason = ""
        self._editor_stall_active: dict[str, Any] | None = None
        self._recent_editor_stall: dict[str, Any] | None = None
        # Only live, admitted, ordered Unity snapshots establish a lifecycle wait.
        self._editor_lifecycle: dict[str, Any] | None = None
        self._editor_lifecycle_recovery: dict[str, Any] | None = None
        self._editor_lifecycle_failure: dict[str, Any] | None = None
        self._editor_lifecycle_producer: tuple[int, int, str, int, int] | None = None

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
        transport = getattr(websocket, "transport", None)
        verified_linger = None
        # fail_connection() or abort() may invalidate the socket immediately.
        # Configure through the public transport API BEFORE either operation.
        try:
            sock = transport.get_extra_info("socket") if transport is not None else None
            if sock is None:
                raise RuntimeError("transport socket unavailable")
            verified_linger = enable_abortive_close(sock)
        except Exception:
            logger.warning(
                "WebSocket abortive close unverified for %s (%s); still aborting",
                remote, reason, exc_info=True,
            )

        fail_connection = getattr(websocket, "fail_connection", None)
        if fail_connection is not None:
            try:
                fail_connection()
            except Exception as ex:
                logger.debug("fail_connection skipped for %s (%s): %s", remote, reason, ex)

        if transport is None:
            logger.debug("No transport available for force disconnect %s (%s)", remote, reason)
            return
        try:
            transport.abort()
        except Exception:
            logger.warning("transport.abort failed for %s (%s)", remote, reason, exc_info=True)
            return
        # Only the transport closes its socket; no private _sock / double close.
        logger.info(
            "Force-aborted WebSocket to %s (%s), linger=%s verified=%s",
            remote, reason, verified_linger, verified_linger is not None,
        )

    async def start(self) -> None:
        self._stop_event.clear()
        self._listening_event.clear()
        self._shutting_down = False
        try:
            self._server = await websockets.serve(
                self._handle, self.host, self.port, max_size=_MAX_BRIDGE_MESSAGE_BYTES,
                # Unity's ClientWebSocket doesn't use compression. Declining it also
                # keeps the byte limit independent of compressed continuation frames.
                compression=None, start_serving=False,
            )
            if not self._server.sockets:
                raise OSError("No sockets available for WebSocket listener")
            for listener in self._server.sockets:
                linger = enable_abortive_close(listener)
                logger.info(
                    "WebSocket socket policy=abortive linger=%s address=%s",
                    linger, listener.getsockname(),
                )
            await self._server.start_serving()
            self._listening_event.set()
            logger.info("WebSocket server listening on %s:%s", self.host, self.port)
            self._write_batch_deadline_loop = asyncio.get_running_loop()
            self.state.write_batch_deadline_changed = self._schedule_write_batch_deadline
            self._schedule_write_batch_deadline()
            await self._stop_event.wait()
        except Exception:
            logger.exception("WebSocket server failed on %s:%s", self.host, self.port)
            raise
        finally:
            self._listening_event.clear()
            self._stop_write_batch_deadlines()
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

    def _schedule_write_batch_deadline(self, retry_delay: float = 0.0) -> None:
        if self._expiring_write_batches:
            return
        deadline = self.state.next_write_batch_deadline
        if deadline == self._write_batch_deadline_at and self._write_batch_deadline_timer is not None:
            return
        if self._write_batch_deadline_timer is not None:
            self._write_batch_deadline_timer.cancel()
        self._write_batch_deadline_timer = None
        self._write_batch_deadline_at = None
        loop = self._write_batch_deadline_loop
        if deadline is None or loop is None or loop.is_closed() or self._shutting_down:
            return
        self._write_batch_deadline_at = deadline
        self._write_batch_deadline_timer = loop.call_later(
            max(retry_delay, (deadline - now_ms()) / 1000.0), self._expire_due_write_batches
        )

    def _expire_due_write_batches(self) -> None:
        self._write_batch_deadline_timer = None
        self._write_batch_deadline_at = None
        if self._shutting_down or self._write_batch_deadline_loop is None:
            return
        self._expiring_write_batches = True
        retry_delay = 0.0
        try:
            # A single nearest-deadline timer works in PlayMode/disconnection,
            # independently of Bridge heartbeat and client polling.
            self.state.expire_write_batches()
        except Exception:
            logger.exception("Write batch deadline persistence failed")
            retry_delay = 1.0  # Avoid spinning if an active deadline cannot be processed.
        finally:
            self._expiring_write_batches = False
            self._schedule_write_batch_deadline(retry_delay)

    def _stop_write_batch_deadlines(self) -> None:
        self.state.write_batch_deadline_changed = None
        self._write_batch_deadline_loop = None
        if self._write_batch_deadline_timer is not None:
            self._write_batch_deadline_timer.cancel()
        self._write_batch_deadline_timer = None
        self._write_batch_deadline_at = None

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
        self.session_manager.close_gate("stopping")
        self._invalidate_connection("stopping")
        self._stop_write_batch_deadlines()
        self._stop_event.set()

    def _cancel_reconnect_grace(self) -> None:
        if self._reconnect_grace_task and not self._reconnect_grace_task.done():
            self._reconnect_grace_task.cancel()
        self._reconnect_grace_task = None
        self._grace_deadline_monotonic = None

    def _schedule_reconnect_grace(self) -> None:
        """Start or replace grace timer until suspended commands are failed."""
        if self._grace_disconnect_started:
            return
        self._grace_disconnect_started = True
        previous_deadline = self._grace_deadline_monotonic
        self._cancel_reconnect_grace()
        grace_s = env_float("UPILOT_DISCONNECT_GRACE_S", 3600.0)
        if grace_s <= 0:
            logger.info(
                "UPILOT_DISCONNECT_GRACE_S=%s — no reconnect deadline; suspended commands wait until Unity returns",
                grace_s,
            )
            return
        loop = asyncio.get_running_loop()
        deadline = max(previous_deadline or 0.0, loop.time() + grace_s)
        self._grace_deadline_monotonic = deadline
        self._reconnect_grace_task = asyncio.create_task(self._reconnect_grace_at_deadline(deadline, grace_s))

    def _extend_grace_for_domain_reload(self) -> None:
        if self._reload_grace_extended:
            return
        self._reload_grace_extended = True
        bonus = env_float("UPILOT_DOMAIN_RELOAD_BONUS_S", 600.0)
        if bonus <= 0:
            return
        loop = asyncio.get_running_loop()
        now = loop.time()
        prev = self._grace_deadline_monotonic
        new_deadline = max(prev if prev is not None else now, now + bonus)
        self._cancel_reconnect_grace()
        self._grace_deadline_monotonic = new_deadline
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
        self._pending_generation.clear()
        self._maintenance_pending.clear()

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
            if self._shutting_down:
                return False
            if self.is_ready():
                return True
            if deadline is not None and loop.time() >= deadline:
                return self.is_ready()
            await asyncio.sleep(min(0.2, (deadline - loop.time()) if deadline else 0.2))

    def is_ready(self) -> bool:
        connection = self._connection
        if (self._shutting_down or connection is None
                or connection.phase != "ready" or connection.receipt is None
                or connection.websocket is not self._ws
                or connection.session is not self.session_manager.active):
            return False
        if self.session_manager.is_ready():
            return True
        if self.session_manager.phase == "lease_expired":
            self._invalidate_connection("lease_expired")
            logger.error("Bridge heartbeat lease expired session=%s; reconnect and bootstrap required",
                         connection.session.session_id)
            self._force_disconnect_websocket(connection.websocket, reason="Bridge heartbeat lease expired")
        return False

    def bridge_readiness_status(self) -> dict[str, Any]:
        connection = self._connection
        ready = self.is_ready()
        phase = connection.phase if connection else "disconnected"
        if connection and phase == "ready" and not ready:
            phase = self.session_manager.phase
        return {
            "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
            "bridgeReady": ready,
            "connectionPhase": "stopping" if self._shutting_down else (
                phase),
            "bootstrapReceipt": dict(connection.receipt) if connection and connection.receipt else None,
        }

    def _connection_current(self, connection: _BridgeConnection) -> bool:
        return bool(not self._shutting_down and self._connection is connection
                    and self._ws is connection.websocket
                    and self.session_manager.active is connection.session
                    and connection.session.authenticated and connection.session.connected)

    def result_connection_current(self, result: dict[str, Any]) -> bool:
        token = result.get("_connectionGeneration")
        return bool(token and self._connection and self._connection_current(self._connection)
                    and self._connection.generation == token
                    and (self.is_ready() or result.get("_maintenanceControl") is True))

    def _invalidate_connection(self, phase: str) -> None:
        connection = self._connection
        if connection:
            connection.phase = phase
            try:
                current_task = asyncio.current_task()
            except RuntimeError:
                current_task = None
            if connection.timeout_task and connection.timeout_task is not current_task:
                connection.timeout_task.cancel()
        self.session_manager.close_gate(phase)

    async def _readiness_timeout(self, connection: _BridgeConnection) -> None:
        deadline = connection.bootstrap_deadline or connection.deadline
        await asyncio.sleep(max(0.0, deadline - time.monotonic()))
        if self._connection_current(connection) and connection.phase != "ready":
            self._invalidate_connection("bootstrap_timed_out")
            logger.error("Bridge readiness deadline expired session=%s helloId=%s phase=%s",
                         connection.session.session_id, connection.hello_id, connection.phase)
            await self._close_websocket(connection.websocket, reason="Bridge readiness deadline expired")

    def _bootstrap_identity(self, connection: _BridgeConnection) -> dict[str, Any]:
        return {"helloId": connection.hello_id, "serverLifecycleId": connection.lifecycle_id,
                "serverInstanceId": connection.instance_id}

    async def _begin_bootstrap(self, connection: _BridgeConnection, payload: dict[str, Any]) -> None:
        if not self._connection_current(connection) or connection.phase != "waiting_bridge_ready":
            return
        if (type(payload.get("bridgeReadyContractVersion")) is not int
                or payload["bridgeReadyContractVersion"] != BRIDGE_READY_CONTRACT_VERSION
                or any(payload.get(key) != value for key, value in self._bootstrap_identity(connection).items())):
            return
        if time.monotonic() >= connection.deadline:
            return
        connection.phase = "bootstrapping"
        connection.bootstrap_deadline = min(connection.deadline, time.monotonic() + 7.0)
        if connection.timeout_task:
            connection.timeout_task.cancel()
        connection.timeout_task = asyncio.create_task(self._readiness_timeout(connection))
        self.session_manager.phase = connection.phase
        connection.bootstrap_id = "bootstrap-" + uuid.uuid4().hex
        connection.nonce = uuid.uuid4().hex
        envelope = {
            "id": connection.bootstrap_id, "type": "command", "name": "session.bootstrap",
            "payload": {**self._bootstrap_identity(connection), "nonce": connection.nonce,
                        "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION},
            "timestamp": now_ms(), "sessionId": connection.session.session_id,
            "protocolVersion": PROTOCOL_VERSION,
        }
        async with self._send_lock:
            if self._connection_current(connection) and time.monotonic() < connection.bootstrap_deadline:
                await connection.websocket.send(json.dumps(envelope, ensure_ascii=False))

    def _validate_bootstrap(self, connection: _BridgeConnection, payload: dict[str, Any]) -> bool:
        if (type(payload.get("bridgeReadyContractVersion")) is not int
                or payload["bridgeReadyContractVersion"] != BRIDGE_READY_CONTRACT_VERSION
                or payload.get("nonce") != connection.nonce
                or any(payload.get(key) != value for key, value in self._bootstrap_identity(connection).items())):
            return False
        snapshot, diagnostics = payload.get("state"), payload.get("compileEvidence")
        if not isinstance(snapshot, dict) or not isinstance(diagnostics, dict):
            return False
        if (snapshot.get("stateContractVersion") != 2
                or snapshot.get("sessionId") != connection.session.session_id
                or snapshot.get("processId") != connection.session.process_id
                or snapshot.get("processCreatedAt") != connection.session.process_created_at
                or snapshot.get("connected") is not True or snapshot.get("authoritative") is not True
                or snapshot.get("isStale") is not False
                or not isinstance(snapshot.get("projectId"), str) or not snapshot["projectId"]
                or not isinstance(snapshot.get("producerEpoch"), str) or not snapshot["producerEpoch"]
                or not isinstance(snapshot.get("snapshotId"), str) or not snapshot["snapshotId"]
                or type(snapshot.get("sequence")) is not int or snapshot["sequence"] <= 0
                or type(snapshot.get("domainGeneration")) is not int or snapshot["domainGeneration"] < 0
                or type(snapshot.get("observedAt")) is not int or snapshot["observedAt"] <= 0
                or snapshot["observedAt"] < connection.accepted_at
                or type(snapshot.get("lastMainThreadPumpAt")) is not int or snapshot["lastMainThreadPumpAt"] <= 0):
            return False
        process = (connection.session.process_id, connection.session.process_created_at)
        epoch = snapshot["producerEpoch"]
        if epoch in self._retired_producer_epochs:
            return False
        if self._producer_process == process and self._bound_producer_epoch and epoch != self._bound_producer_epoch:
            return False
        if self.state.project_id and self.state.project_id != snapshot["projectId"]:
            return False
        if epoch == self.state.producer_epoch and (snapshot["domainGeneration"], snapshot["sequence"]) <= (self.state.domain_generation, self.state.sequence):
            return False
        identities = {"compileRequestId": "requestId", "compileOperationId": "compileOperationId",
                      "writeBatchId": "writeBatchId", "writeBatchCreatedAt": "writeBatchCreatedAt",
                      "compileOrigin": "compileOrigin", "compilePhase": "phase",
                      "terminal": "terminal", "verificationPending": "verificationPending",
                      "errorsVerified": "errorsVerified", "lastCompileRequestedAt": "lastCompileRequestedAt",
                      "lastCompileStartedAt": "lastCompileStartedAt", "lastCompilerFinishedAt": "lastCompilerFinishedAt",
                      "lastCompileVerifiedAt": "lastCompileVerifiedAt", "lastTerminalCompileAt": "lastTerminalCompileAt",
                      "errorCount": "total", "warningCount": "warningCount"}
        if any(key not in snapshot or other not in diagnostics or snapshot[key] != diagnostics[other]
               for key, other in identities.items()):
            return False
        if any(type(snapshot.get(key)) is not bool for key in ("terminal", "verificationPending", "errorsVerified", "isCompiling", "isUpdating")):
            return False
        if any(type(snapshot.get(key)) is not int or snapshot[key] < 0 for key in (
                "lastCompileRequestedAt", "lastCompileStartedAt", "lastCompilerFinishedAt",
                "lastCompileVerifiedAt", "lastTerminalCompileAt", "writeBatchCreatedAt", "errorCount", "warningCount")):
            return False
        if (not isinstance(diagnostics.get("errors"), list)
                or type(diagnostics.get("total")) is not int or diagnostics["total"] < 0
                or diagnostics["total"] != len(diagnostics["errors"])
                or any(not isinstance(item, dict) for item in diagnostics["errors"])
                or type(diagnostics.get("warningCount")) is not int or diagnostics["warningCount"] < 0):
            return False
        if diagnostics.get("errorsVerified") is True and (
                diagnostics.get("terminal") is not True or diagnostics.get("verificationPending") is not False
                or diagnostics.get("phase") not in {"completed", "failed"}
                or not diagnostics.get("compileOperationId")
                or type(diagnostics.get("lastCompileVerifiedAt")) is not int
                or diagnostics["lastCompileVerifiedAt"] <= 0):
            return False
        evidence = snapshot.get("compileEvidence")
        if evidence != diagnostics.get("compileEvidence"):
            return False
        if evidence is None:
            return diagnostics.get("errorsVerified") is not True
        if not isinstance(evidence, dict):
            return False
        if (type(evidence.get("version")) is not int or evidence["version"] != 1
                or not paths_equal(str(evidence.get("projectPath") or ""), self.expected_project_path)
                or evidence.get("processId") != connection.session.process_id
                or evidence.get("processCreatedAt") != connection.session.process_created_at
                or evidence.get("producerEpoch") != epoch
                or evidence.get("compileOperationId") != snapshot["compileOperationId"]
                or type(evidence.get("revision")) is not int or evidence["revision"] <= 0
                or type(evidence.get("persistenceVerified")) is not bool):
            return False
        milestones = evidence.get("milestones")
        if not isinstance(milestones, list) or not 1 <= len(milestones) <= 6:
            return False
        stages = set()
        revisions = set()
        terminal = None
        for milestone in milestones:
            if not isinstance(milestone, dict):
                return False
            stage = milestone.get("stage")
            if (stage not in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying", "terminal"}
                    or stage in stages or type(milestone.get("revision")) is not int
                    or not 0 < milestone["revision"] <= evidence["revision"]
                    or milestone["revision"] in revisions
                    or type(milestone.get("observedAt")) is not int or milestone["observedAt"] <= 0
                    or type(milestone.get("terminal")) is not bool
                    or type(milestone.get("errorsVerified")) is not bool
                    or type(milestone.get("errorCount")) is not int or milestone["errorCount"] < 0):
                return False
            stages.add(stage)
            revisions.add(milestone["revision"])
            if stage == "terminal":
                terminal = milestone
        if max(revisions) != evidence["revision"]:
            return False
        current_evidence = self.state.compile.compile_evidence
        if (isinstance(current_evidence, dict) and current_evidence.get("producerEpoch") == epoch
                and current_evidence.get("compileOperationId") == evidence["compileOperationId"]):
            if evidence["revision"] < int(current_evidence.get("revision") or 0):
                return False
            if evidence["revision"] == current_evidence.get("revision") and evidence != current_evidence:
                return False
        if diagnostics["errorsVerified"] and (
                evidence["persistenceVerified"] is not True or not terminal
                or terminal.get("terminal") is not True or terminal.get("errorsVerified") is not True
                or terminal.get("phase") != snapshot["compilePhase"]
                or terminal.get("errorCount") != snapshot["errorCount"]
                or terminal["observedAt"] != snapshot["lastCompileVerifiedAt"]):
            return False
        return True

    async def _complete_bootstrap(self, connection: _BridgeConnection, message) -> None:
        if (not self._connection_current(connection) or connection.phase != "bootstrapping"
                or message.id != connection.bootstrap_id
                or time.monotonic() >= min(connection.deadline, connection.bootstrap_deadline)):
            return
        if message.type == "error":
            self._invalidate_connection("bootstrap_failed")
            logger.error("Bridge bootstrap failed session=%s helloId=%s code=%s",
                         connection.session.session_id, connection.hello_id, message.payload.get("code", "unknown"))
            await self._close_websocket(connection.websocket, reason="Bridge bootstrap failed")
            return
        try:
            valid = self._validate_bootstrap(connection, message.payload)
        except (ValueError, TypeError, KeyError):
            valid = False
        if not valid:
            self._invalidate_connection("bootstrap_rejected")
            logger.error("Rejected Bridge bootstrap identity or evidence session=%s helloId=%s",
                         connection.session.session_id, connection.hello_id)
            await self._close_websocket(connection.websocket, reason="Bridge bootstrap evidence rejected")
            return
        snapshot = message.payload["state"]
        process = (connection.session.process_id, connection.session.process_created_at)
        epoch = snapshot["producerEpoch"]
        previous_bound = self.state.active_producer_epoch
        self.state.active_producer_epoch = epoch
        if self._producer_process is not None and self._producer_process != process:
            self.state.compile = CompileSnapshot()
        try:
            committed = self.state.commit_bridge_bootstrap(snapshot, message.payload["compileEvidence"])
        except (OSError, sqlite3.Error, ValueError, TypeError):
            logger.exception("Bridge bootstrap state commit failed session=%s", connection.session.session_id)
            committed = False
        if not committed:
            self.state.active_producer_epoch = previous_bound
            self._invalidate_connection("bootstrap_persistence_failed")
            logger.error("Bridge bootstrap was not durably committed session=%s", connection.session.session_id)
            await self._close_websocket(connection.websocket, reason="Bridge bootstrap commit failed")
            return
        receipt = {"bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
                   "bootstrapId": connection.bootstrap_id, "nonce": connection.nonce,
                   **self._bootstrap_identity(connection),
                   **{key: snapshot[key] for key in ("snapshotId", "producerEpoch", "domainGeneration", "sequence")}}
        ack = {"id": connection.bootstrap_id, "type": "event", "name": "session.bootstrap.ack",
               "payload": receipt, "timestamp": now_ms(), "sessionId": connection.session.session_id,
               "protocolVersion": PROTOCOL_VERSION}
        async with self._send_lock:
            if not self._connection_current(connection) or time.monotonic() >= min(connection.deadline, connection.bootstrap_deadline):
                return
            await connection.websocket.send(json.dumps(ack, ensure_ascii=False))
            if not self._connection_current(connection) or time.monotonic() >= min(connection.deadline, connection.bootstrap_deadline):
                return
            if not self.session_manager.mark_ready(connection.session):
                return
            connection.receipt = receipt
            connection.phase = "ready"
            if connection.timeout_task:
                connection.timeout_task.cancel()
            if self._bound_producer_epoch and self._bound_producer_epoch != epoch:
                self._retired_producer_epochs.add(self._bound_producer_epoch)
            self._bound_producer_epoch, self._producer_process = epoch, process
        if connection.resume_queries:
            self._pending.update(self._suspended)
            self._suspended.clear()
        self._domain_reloading = False
        self._cancel_reconnect_grace()
        self._reload_grace_extended = False
        self._grace_disconnect_started = False
        self._observe_editor_lifecycle(snapshot)
        self._notify_editor_execution_state()
        self._note_compile_busy(self.state.compile.phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"})
        if connection.resume_queries:
            await self._resend_pending_commands()

    def editor_observation(
        self,
        *,
        observed_at_ms: int | None = None,
        network_connected: bool | None = None,
    ) -> dict[str, Any]:
        """Return cached, read-only Editor responsiveness evidence.

        This never sends a command to Unity. A stale main-thread pump with a fresh
        background heartbeat is only a suspected Editor update stall, not proof of
        an exact giant-frame duration or of UPilot responsibility.
        """
        observed_at = int(observed_at_ms or now_ms())
        session = self.session_manager.active
        state = self.state
        editor = getattr(state, "editor", None)
        heartbeat_at = int(getattr(session, "last_heartbeat_at", 0) or 0) if session else 0
        heartbeat_age = max(0, observed_at - heartbeat_at) if heartbeat_at else None
        if network_connected is None:
            network_connected = self.is_ready()
        heartbeat_timeout = int(getattr(self.session_manager, "heartbeat_timeout_ms", 0) or 0)
        network_fresh = bool(
            session
            and network_connected
            and heartbeat_at
            and (heartbeat_timeout <= 0 or heartbeat_age <= heartbeat_timeout)
        )

        session_id = str(getattr(session, "session_id", "") or "") if session else ""
        state_session_id = str(getattr(editor, "session_id", "") or "") if editor else ""
        producer_epoch = str(getattr(state, "producer_epoch", "") or "")
        domain_generation = int(getattr(state, "domain_generation", 0) or 0)
        pump_at = int(getattr(editor, "last_main_thread_pump_at", 0) or 0) if editor else 0
        pump_age = max(0, observed_at - pump_at) if pump_at else None
        snapshot_received_at = int(getattr(state, "received_at", 0) or 0)
        snapshot_age = max(0, observed_at - snapshot_received_at) if snapshot_received_at else None
        identity_consistent = bool(
            session_id
            and state_session_id == session_id
            and producer_epoch
            # Freshness is not process/project identity. A blocked pump deliberately
            # makes authoritative false while retaining its admitted producer.
        )

        status = "unknown"
        reason = "evidence_unavailable"
        lifecycle, lifecycle_error = self._current_editor_lifecycle()
        if lifecycle_error:
            reason = lifecycle_error
        elif lifecycle:
            status = "waiting_lifecycle"
            reason = "confirmed_unity_lifecycle"
        elif not network_fresh:
            reason = "network_heartbeat_not_fresh"
        elif not identity_consistent:
            reason = "identity_transition_or_missing"
        elif not pump_at:
            reason = "main_thread_pump_missing"
        elif snapshot_age is None or snapshot_age > _EDITOR_PUMP_STALE_MS:
            # A long configured heartbeat interval leaves old pump samples in the
            # cache. Only a newly received sample can evidence a current stall.
            reason = "editor_snapshot_not_fresh"
        elif pump_age > _EDITOR_PUMP_STALE_MS:
            status = "waiting_editor"
            reason = "main_thread_pump_stale"
        elif self._editor_lifecycle_recovery and str(self.state.compile.phase or "").lower() in {
                "queued", "compiling", "compiler_finished", "domain_reload", "verifying", "failed"}:
            reason = "terminal_snapshot_not_ready"
        elif not bool(getattr(editor, "authoritative", False)):
            reason = "snapshot_not_authoritative"
        else:
            status = "responsive"
            reason = "fresh_same_identity_evidence"

        identity = (session_id, producer_epoch, domain_generation)
        payload: dict[str, Any] = {
            "status": status,
            "reason": reason,
            "observed_at_ms": observed_at,
            "session_id": session_id,
            "producer_epoch": producer_epoch,
            "domain_generation": domain_generation,
            "last_main_thread_pump_at_ms": pump_at,
            "pump_age_ms": pump_age,
            "heartbeat_at_ms": heartbeat_at,
            "heartbeat_age_ms": heartbeat_age,
            "snapshot_received_at_ms": snapshot_received_at,
            "snapshot_age_ms": snapshot_age,
            "main_thread_queue_depth": int(getattr(editor, "main_thread_queue_depth", 0) or 0) if editor else 0,
            "last_dequeued_command_id": str(getattr(editor, "last_dequeued_command_id", "") or "")[:128] if editor else "",
            "waiting_since_ms": 0,
            "waiting_duration_ms": 0,
            "observation_count": 0,
            "recent_stall": dict(self._recent_editor_stall) if self._recent_editor_stall else None,
        }
        self._track_editor_stall(payload, identity)
        evidence = lifecycle or self._editor_lifecycle_recovery
        if evidence and not lifecycle_error:
            payload.update(
                lifecycle_phase=evidence["phase"] if lifecycle else "verifying_recovery",
                lifecycle_id=evidence["id"], lifecycle_started_at_ms=evidence["started_at_ms"],
            )
            if lifecycle:
                payload.update(waiting_since_ms=evidence["started_at_ms"],
                               waiting_duration_ms=max(0, observed_at - evidence["started_at_ms"]))
        return payload

    def _current_editor_lifecycle(self) -> tuple[dict[str, Any] | None, str]:
        record = self._editor_lifecycle or self._editor_lifecycle_recovery or self._editor_lifecycle_failure
        if not record:
            return None, ""
        session = self.session_manager.active
        if (not session or not session.identity_verified
                or not paths_equal(session.project_path, record["project"])
                or session.process_id != record["pid"]
                or session.process_created_at != record["created_at"]):
            self._editor_lifecycle = self._editor_lifecycle_recovery = self._editor_lifecycle_failure = None
            return None, "lifecycle_identity_changed"
        if self._editor_lifecycle_failure:
            return None, self._editor_lifecycle_failure["failure_reason"]
        # Kernel queries only; never run a CIM scan for every /health observation.
        if not process_exists(record["pid"]):
            self._editor_lifecycle_failure = dict(record, failure_reason="unity_process_exited")
            self._editor_lifecycle = self._editor_lifecycle_recovery = None
            return None, "unity_process_exited"
        created_at = process_creation_time(record["pid"])
        if created_at and created_at // 10 != record["created_at"] // 10:
            self._editor_lifecycle_failure = dict(record, failure_reason="unity_process_identity_mismatch")
            self._editor_lifecycle = self._editor_lifecycle_recovery = None
            return None, "unity_process_identity_mismatch"
        if os.name == "nt" and not created_at:
            return None, "process_identity_unconfirmed"
        return self._editor_lifecycle, ""

    def _observe_editor_lifecycle(self, payload: dict[str, Any]) -> None:
        """Consume only snapshots already admitted by StateStore's ordering gate."""
        session = self.session_manager.active
        if (not session or not session.identity_verified or not session.process_created_at
                or payload.get("sessionId") != session.session_id
                or int(payload.get("processId") or 0) != session.process_id
                or int(payload.get("processCreatedAt") or 0) != session.process_created_at
                or not paths_equal(session.project_path, self.expected_project_path or session.project_path)):
            return
        epoch = str(payload.get("producerEpoch") or "")
        domain = int(payload.get("domainGeneration") or 0)
        observed = int(payload.get("observedAt") or 0)
        if not epoch or not observed:
            return
        sequence = int(payload.get("sequence") or 0)
        failure = self._editor_lifecycle_failure
        if failure:
            if (session.process_id == failure["pid"] and session.process_created_at == failure["created_at"]
                    and paths_equal(session.project_path, failure["project"])):
                return  # A snapshot cannot revive a confirmed exited or reused process.
            self._editor_lifecycle_failure = None
        producer = self._editor_lifecycle_producer
        if producer and producer[:2] == (session.process_id, session.process_created_at):
            if epoch != producer[2] or (domain, sequence) <= producer[3:]:
                return
        self._editor_lifecycle_producer = (session.process_id, session.process_created_at, epoch, domain, sequence)
        record = self._editor_lifecycle
        if record and (record["pid"] != session.process_id or record["created_at"] != session.process_created_at
                       or not paths_equal(record["project"], session.project_path)):
            self._editor_lifecycle = self._editor_lifecycle_recovery = record = None
        if record and (epoch != record["epoch"] or domain < record["domain"]
                       or observed < record["started_at_ms"]):
            return
        transition = payload.get("playModeTransition") or {}
        current_transition = (isinstance(transition, dict)
                              and transition.get("processId") == session.process_id
                              and transition.get("processCreatedAt") == session.process_created_at
                              and paths_equal(str(transition.get("projectPath") or ""), session.project_path))
        to_state = transition.get("toState") if current_transition else None
        transition_at = int(transition.get("at") or 0) if current_transition else 0
        phase = str(payload.get("compilePhase") or "").lower()
        event = str(payload.get("transition") or "")
        compile_id = str(payload.get("compileOperationId") or "")
        reload_id = str(payload.get("reloadId") or "")
        authoritative = payload.get("authoritative") is True
        if record and record["phase"] in {"enteringPlay", "exitingPlay"}:
            # A current terminal Unity event also covers cancellation back to EditMode.
            if (to_state in {"play", "edit"} and transition.get("unitySessionId") == session.session_id
                    and transition_at >= record["started_at_ms"] and authoritative):
                record["ended_at_ms"] = observed
                self._editor_lifecycle_recovery = record
                self._editor_lifecycle = None
            return
        # Compilation -> reload belongs to the same compile, not a new wait/deadline.
        same_compile = bool(record and record.get("compile_id") and compile_id == record["compile_id"])
        same_reload = bool(record and record.get("reload_id") and reload_id == record["reload_id"])
        if record and compile_id and not same_compile and authoritative and payload.get("isStale") is not True:
            # A lost terminal event must not pin /health to an obsolete compile.
            # Ordered Unity evidence may supersede that wait; it never verifies
            # the old compile or changes its persisted identity/result.
            started_at = max(int(payload.get(key) or 0) for key in (
                "compileStartedAt", "lastCompileStartedAt", "lastCompileRequestedAt"))
            verified_at = int(payload.get("lastCompileVerifiedAt") or 0)
            if (phase in {"completed", "failed"} and payload.get("terminal") is True
                    and payload.get("errorsVerified") is True
                    and record["started_at_ms"] < started_at <= verified_at <= observed):
                record.update(ended_at_ms=observed, superseded_by_compile_id=compile_id)
                self._editor_lifecycle_recovery = record
                self._editor_lifecycle = None
                return
            if (event in {"compile_queued", "compiling", "compile_started", "domain_reload_starting"}
                    and phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"}
                    and observed > record["started_at_ms"]
                    and (not started_at or record["started_at_ms"] < started_at <= observed)):
                # Follow the newer compile while keeping it non-ready until its
                # own verified terminal arrives, even when isCompiling is false.
                self._editor_lifecycle = self._editor_lifecycle_recovery = record = None
        if record and authoritative and (
                (same_compile and phase in {"completed", "failed"} and payload.get("terminal") is True
                 and payload.get("errorsVerified") is True)
                or (not record.get("compile_id")
                    and ((same_reload and domain > record["domain"]
                          and event in {"domain_reload_recovered", "connected_snapshot"})
                         or (event == "connected_snapshot" and session.session_id != record["session_id"]
                             and domain >= record["domain"]))
                    and phase not in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"})):
            record["ended_at_ms"] = observed
            self._editor_lifecycle_recovery = record
            self._editor_lifecycle = None
            return
        if (to_state in {"enteringPlay", "exitingPlay"}
                and transition.get("unitySessionId") == session.session_id
                and 0 < transition_at <= observed and authoritative):
            lifecycle_phase, lifecycle_id, started = to_state, str(transition.get("transitionId") or ""), transition_at
        elif authoritative and event in {"compile_queued", "compiling", "compile_started", "domain_reload_starting"}:
            lifecycle_phase = "domain_reload" if event == "domain_reload_starting" else "compiling"
            # A reload outside a compile must not inherit the last completed compile's ID.
            if phase not in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"}:
                compile_id = ""
            lifecycle_id = compile_id or reload_id or str(payload.get("snapshotId") or "")
            started = observed
        else:
            return
        if not lifecycle_id:
            return
        if record:
            if same_compile or (not record.get("compile_id") and same_reload):
                record["phase"] = lifecycle_phase
                if reload_id:
                    record["reload_id"] = reload_id
            return
        recovered = self._editor_lifecycle_recovery
        if recovered and (lifecycle_id == recovered["id"] or started <= recovered["ended_at_ms"]):
            return  # A later heartbeat cannot resurrect the ended start record.
        self._editor_lifecycle_recovery = None
        self._editor_lifecycle = dict(phase=lifecycle_phase, id=lifecycle_id, started_at_ms=started,
                                     project=session.project_path, pid=session.process_id,
                                     created_at=session.process_created_at, epoch=epoch, domain=domain,
                                     session_id=session.session_id, compile_id=compile_id, reload_id=reload_id)

    def _track_editor_stall(self, payload: dict[str, Any], identity: tuple[str, str, int]) -> None:
        status = payload["status"]
        observed_at = int(payload["observed_at_ms"] or 0)
        active = self._editor_stall_active

        if status == "waiting_editor":
            pump_at = int(payload.get("last_main_thread_pump_at_ms") or 0)
            if active is None or active.get("identity") != identity:
                if active is not None:
                    self._finish_editor_stall(active, observed_at, "interrupted", payload.get("reason", "identity_changed"))
                active = {
                    "identity": identity,
                    "started_at_ms": observed_at,
                    "initial_pump_at_ms": pump_at,
                    "last_log_at_ms": observed_at,
                    "observation_count": 0,
                    "max_pump_age_ms": 0,
                    "last_pump_at_ms": pump_at,
                    "last_heartbeat_at_ms": int(payload.get("heartbeat_at_ms") or 0),
                    "last_heartbeat_age_ms": int(payload.get("heartbeat_age_ms") or 0),
                    "last_queue_depth": int(payload.get("main_thread_queue_depth") or 0),
                    "last_command_id": str(payload.get("last_dequeued_command_id") or "")[:128],
                    "project_path": str(getattr(self.session_manager.active, "project_path", "") or "") if self.session_manager.active else "",
                }
                self._editor_stall_active = active
                logger.warning(
                    "Editor main thread has not advanced; suspected long update stall (cause unknown) "
                    "project=%s session=%s epoch=%s domain=%s observedAtMs=%s pumpAtMs=%s "
                    "pumpAgeMs=%s heartbeatAtMs=%s heartbeatAgeMs=%s queueDepth=%s lastCommand=%s",
                    active["project_path"], identity[0], identity[1], identity[2], observed_at, pump_at,
                    payload.get("pump_age_ms"), payload.get("heartbeat_at_ms"), payload.get("heartbeat_age_ms"),
                    payload.get("main_thread_queue_depth"), payload.get("last_dequeued_command_id") or "?",
                )
            active["observation_count"] += 1
            active["max_pump_age_ms"] = max(
                int(active.get("max_pump_age_ms") or 0), int(payload.get("pump_age_ms") or 0)
            )
            active["last_pump_at_ms"] = pump_at
            active["last_heartbeat_at_ms"] = int(payload.get("heartbeat_at_ms") or 0)
            active["last_heartbeat_age_ms"] = int(payload.get("heartbeat_age_ms") or 0)
            active["last_queue_depth"] = int(payload.get("main_thread_queue_depth") or 0)
            active["last_command_id"] = str(payload.get("last_dequeued_command_id") or "")[:128]
            if observed_at - int(active.get("last_log_at_ms") or 0) >= _EDITOR_STALL_LOG_INTERVAL_MS:
                active["last_log_at_ms"] = observed_at
                logger.warning(
                    "Editor main-thread update stall continues project=%s session=%s epoch=%s domain=%s "
                    "observedAtMs=%s observedIntervalMs=%s observations=%s pumpAtMs=%s "
                    "heartbeatAtMs=%s heartbeatAgeMs=%s queueDepth=%s lastCommand=%s",
                    active["project_path"], identity[0], identity[1], identity[2], observed_at,
                    active["max_pump_age_ms"], active["observation_count"], pump_at,
                    payload.get("heartbeat_at_ms"), payload.get("heartbeat_age_ms"),
                    payload.get("main_thread_queue_depth"), payload.get("last_dequeued_command_id") or "?",
                )
            payload["waiting_since_ms"] = int(active["started_at_ms"] or 0)
            payload["waiting_duration_ms"] = max(0, observed_at - int(active["started_at_ms"] or 0))
            payload["observation_count"] = int(active["observation_count"] or 0)
            return

        if active is None:
            return
        if status == "responsive" and active.get("identity") == identity and int(payload.get("last_main_thread_pump_at_ms") or 0) > int(active.get("initial_pump_at_ms") or 0):
            self._finish_editor_stall(active, observed_at, "recovered", payload.get("reason", "responsive"))
        else:
            self._finish_editor_stall(active, observed_at, "interrupted", payload.get("reason", "evidence_unknown"))
        payload["recent_stall"] = dict(self._recent_editor_stall) if self._recent_editor_stall else None

    def _finish_editor_stall(self, active: dict[str, Any], ended_at: int, outcome: str, reason: str) -> None:
        identity = active.get("identity") or ("", "", 0)
        summary = {
            "outcome": outcome,
            "reason": str(reason or "")[:128],
            "started_at_ms": int(active.get("started_at_ms") or 0),
            "ended_at_ms": ended_at,
            "duration_ms": max(0, ended_at - int(active.get("started_at_ms") or 0)),
            "observed_update_gap_ms": int(active.get("max_pump_age_ms") or 0),
            "observation_count": int(active.get("observation_count") or 0),
            "session_id": str(identity[0] or ""),
            "producer_epoch": str(identity[1] or ""),
            "domain_generation": int(identity[2] or 0),
        }
        self._recent_editor_stall = summary
        self._editor_stall_active = None
        if outcome == "recovered":
            logger.info(
                "Editor main thread resumed project=%s session=%s epoch=%s domain=%s "
                "startedAtMs=%s endedAtMs=%s observedUpdateGapMs=%s stallObservationMs=%s "
                "observations=%s pumpAtMs=%s heartbeatAtMs=%s heartbeatAgeMs=%s queueDepth=%s lastCommand=%s",
                active.get("project_path", ""), identity[0], identity[1], identity[2],
                summary["started_at_ms"], summary["ended_at_ms"], summary["observed_update_gap_ms"],
                summary["duration_ms"], summary["observation_count"], active.get("last_pump_at_ms", 0),
                active.get("last_heartbeat_at_ms", 0), active.get("last_heartbeat_age_ms", 0),
                active.get("last_queue_depth", 0), active.get("last_command_id") or "?",
            )
        else:
            logger.warning(
                "Editor main-thread stall observation interrupted without claiming recovery "
                "project=%s session=%s epoch=%s domain=%s reason=%s startedAtMs=%s endedAtMs=%s "
                "observedUpdateGapMs=%s observations=%s pumpAtMs=%s heartbeatAtMs=%s heartbeatAgeMs=%s "
                "queueDepth=%s lastCommand=%s",
                active.get("project_path", ""), identity[0], identity[1], identity[2], summary["reason"],
                summary["started_at_ms"], summary["ended_at_ms"], summary["observed_update_gap_ms"],
                summary["observation_count"], active.get("last_pump_at_ms", 0),
                active.get("last_heartbeat_at_ms", 0), active.get("last_heartbeat_age_ms", 0),
                active.get("last_queue_depth", 0), active.get("last_command_id") or "?",
            )

    def register_pending(self, command_id: str) -> asyncio.Future:
        loop = asyncio.get_running_loop()
        fut = loop.create_future()
        self._pending[command_id] = fut
        if self._connection:
            self._pending_generation[command_id] = self._connection.generation
        return fut

    def unregister_pending(self, command_id: str) -> None:
        self._pending_generation.pop(command_id, None)
        self._maintenance_pending.pop(command_id, None)
        future = self._pending.pop(command_id, None)
        if future is None:
            future = self._suspended.pop(command_id, None)
        if future is not None and not future.done():
            future.cancel()

    async def send_command(self, command_id: str, name: str, payload: dict[str, Any]) -> None:
        async with self._send_lock:
            await self._send_command_locked(command_id, name, payload)

    async def _send_command_locked(self, command_id: str, name: str, payload: dict[str, Any], *, maintenance: bool = False) -> None:
        websocket = self._ws
        active = self.session_manager.active
        if websocket is None or active is None or self._shutting_down:
            raise ConnectionError("Bridge transport is unavailable")
        if not self.is_ready() and not (maintenance and self.maintenance_control_allowed(payload)):
            raise ConnectionError("UPILOT_RECONNECT_PENDING: Bridge bootstrap is incomplete")
        self._pending_generation[command_id] = self._connection.generation
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
        record = self.state.commands.get(command_id)
        if record is not None:
            if record.status in {"success", "failed"}:
                raise ConnectionError("Command is already terminal; no redispatch was attempted")
            deadline = getattr(record, "call_deadline_monotonic", 0.0)
            if deadline > 0 and time.monotonic() >= deadline:
                raise asyncio.TimeoutError("Original command observation budget expired before socket send")
            if record.send_attempts is not None:
                # Count the actual socket send entry, even when it raises. This
                # does not prove delivery, Unity dispatch or business execution.
                record.send_attempts += 1
        await websocket.send(raw)

    def maintenance_control_allowed(self, payload: dict[str, Any]) -> bool:
        """Exact authenticated maintenance control; never a general business bypass."""
        from .service_maintenance import read_summary
        connection = self._connection
        if not connection or not self._connection_current(connection):
            return False
        try:
            maintenance_id = str(payload.get("maintenanceId") or "")
            if str(uuid.UUID(maintenance_id)) != maintenance_id.lower():
                return False
            summary = read_summary(Path(self.expected_project_path), connection.session)
            return bool(summary["effectiveApproved"] and not summary["unavailableReason"]
                        and payload.get("target") in {"bridge", "server"}
                        and isinstance(payload.get("reason"), str) and 0 < len(payload["reason"].strip()) <= 512
                        and paths_equal(str(payload.get("expectedProjectPath") or ""), self.expected_project_path)
                        and payload.get("expectedServerProcessId") == os.getpid()
                        and payload.get("expectedBridgeSessionId") == connection.session.session_id
                        and payload.get("expectedMaintenanceId", "") == summary["expectedMaintenanceId"])
        except (OSError, ValueError, TypeError):
            return False

    async def send_maintenance_control(self, command_id: str, payload: dict[str, Any]) -> None:
        async with self._send_lock:
            if not self.maintenance_control_allowed(payload):
                raise ConnectionError("Service maintenance owner or authorization changed")
            connection = self._connection
            self._maintenance_pending[command_id] = connection.generation
            control_payload = {**payload, **self._bootstrap_identity(connection),
                               "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
                               "controlKind": "service.maintenance"}
            await self._send_command_locked(command_id, "service.restart", control_payload, maintenance=True)

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
            if rec is not None and getattr(rec, "status", "") in {"success", "failed"}:
                self._pending.pop(cmd_id, None)
                future.set_result({"id": cmd_id, "type": "error" if rec.status == "failed" else "result",
                                   "name": rec.name, "payload": rec.error if rec.status == "failed" else rec.result})
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
            send_attempts_before = getattr(rec, "send_attempts", None)
            try:
                deadline = getattr(rec, "call_deadline_monotonic", 0.0)
                if deadline > 0:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise asyncio.TimeoutError("Original command observation budget expired before reload recovery")
                    await asyncio.wait_for(self.send_command(cmd_id, rec.name, rec.payload), timeout=remaining)
                else:
                    # Legacy records have no monotonic fact; do not fabricate a new deadline.
                    await self.send_command(cmd_id, rec.name, rec.payload)
            except asyncio.TimeoutError:
                self._pending.pop(cmd_id, None)
                send_attempts_after = getattr(rec, "send_attempts", None)
                replay_attempted = (send_attempts_after > send_attempts_before
                                    if send_attempts_before is not None and send_attempts_after is not None else None)
                error = {"code": "COMMAND_TIMEOUT", "message": "Original command observation budget expired",
                         "detail": {"command": rec.name, "commandId": cmd_id, "waitPhase": "reload_recovery",
                                    "outcome": "unknown", "replayAttempted": replay_attempted,
                                    "nextAction": "Observe the original operation identity; timeout does not stop Unity execution."}}
                self.state.mark_failed(cmd_id, error)
                if not future.done():
                    future.set_result({"id": cmd_id, "type": "error" if rec.status == "failed" else "result",
                                       "name": rec.name, "payload": rec.error if rec.status == "failed" else rec.result})
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
        async with self._send_lock:
            if self._shutting_down:
                return
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
        if self._shutting_down:
            return False, {"accepted": False, "code": "SERVER_STOPPING", "reason": "Server is stopping."}
        if (type(message.payload.get("bridgeReadyContractVersion")) is not int
                or message.payload["bridgeReadyContractVersion"] != BRIDGE_READY_CONTRACT_VERSION):
            return False, {"accepted": False, "code": "BRIDGE_READY_CONTRACT_MISMATCH",
                           "reason": "Server and Bridge readiness contracts must be upgraded together."}
        ready_budget = message.payload.get("readyBudgetMs")
        if type(ready_budget) is not int or not 1000 <= ready_budget <= 600000:
            return False, {"accepted": False, "code": "BRIDGE_READY_BUDGET_INVALID",
                           "reason": "readyBudgetMs must be an integer between 1000 and 600000."}
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
            if self._shutting_down:
                return False, {"accepted": False, "code": "SERVER_STOPPING", "reason": "Server is stopping."}
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
            if previous is not None and previous is not websocket and not self._domain_reloading:
                self._fail_all_pending_and_suspended("CONNECTION_LOST", "Bridge connection was replaced; commands are never replayed.")
            self._invalidate_connection("superseded")
            self._ws = websocket
            message.payload["verificationLevel"] = str(probe.get("verificationLevel") or "")
            message.payload["identityVerified"] = bool(probe.get("identityVerified"))
            session = self.session_manager.on_hello(message.session_id, message.payload)
            self._connection = _BridgeConnection(
                websocket, session, message.id, self.state.lifecycle_id,
                str(version_payload().get("server_instance_id") or ""),
                time.monotonic() + ready_budget / 1000,
            )
            self._connection.timeout_task = asyncio.create_task(self._readiness_timeout(self._connection))
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
                    if auth_box[0] and not candidate_timeout_task.done():
                        candidate_timeout_task.cancel()
            except (ConnectionClosed, ConnectionResetError, OSError) as ex:
                received_close_code = getattr(getattr(ex, "rcvd", None), "code", None)
                sent_close_code = getattr(getattr(ex, "sent", None), "code", None)
                close_code = received_close_code if received_close_code is not None else getattr(websocket, "close_code", None)
                # A locally rejected frame may send 1009 without receiving a close
                # response. ConnectionClosed.code would then report only 1006.
                if 1009 in (received_close_code, sent_close_code):
                    logger.error("Bridge size-limit close observed session=%s at=%s commandId=unknown limitBytes=%s sentCloseCode=%s receivedCloseCode=%s",
                                 auth_box[0] or "unknown", now_ms(), _MAX_BRIDGE_MESSAGE_BYTES,
                                 sent_close_code, received_close_code)
                    self._oversize_count = getattr(self, "_oversize_count", 0) + 1
                    self._oversize_observations = (getattr(self, "_oversize_observations", []) + [
                        {"sessionId": auth_box[0], "sourceEvent": "unknown", "observedAt": now_ms(),
                         "limitBytes": _MAX_BRIDGE_MESSAGE_BYTES, "commandId": "unknown", "closeCode": 1009,
                         "sentCloseCode": sent_close_code, "receivedCloseCode": received_close_code}])[-8:]
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
            self._invalidate_connection("disconnected")
            self.session_manager.disconnect(auth_session_id)
            self.editor_observation(network_connected=False)
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

    def _apply_heartbeat_interval(self, payload: dict[str, Any]) -> None:
        interval = payload.get("heartbeatIntervalMs")
        if type(interval) is not int or not 1000 <= interval <= 600000:
            return
        self.heartbeat_interval_ms = interval
        self.session_manager.heartbeat_timeout_ms = interval * 3

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
                    "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
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
                    "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
                    "verificationLevel": "rejected",
                    "rejectionCode": code,
                    "rejectionReason": reason,
                })
                await self._close_websocket(candidate, reason=f"identity rejected: {code}")
                return
            self._apply_heartbeat_interval(message.payload)
            preserve_compile_snapshot = self._domain_reloading
            if preserve_compile_snapshot:
                logger.info("Unity reconnected during domain reload; waiting for verified bootstrap")
                self._suspend_or_fail_pending_on_disconnect(message.session_id)
            raw_project_for_state = str(message.payload.get("projectPath", "") or "").strip()
            if raw_project_for_state:
                try:
                    self.state.configure_project(raw_project_for_state)
                except (OSError, sqlite3.Error) as ex:
                    logger.warning("Could not configure persistent Editor state: %s", ex)
            previous_process_id = self.state.editor.process_id
            incoming_process_id = int(message.payload.get("processId") or 0)
            if self._suspended:
                if preserve_compile_snapshot and (
                    not previous_process_id
                    or not incoming_process_id
                    or previous_process_id == incoming_process_id
                ):
                    logger.info(
                        "[%s] Keeping %d commands suspended until bootstrap acknowledgement",
                        message.session_id[:12],
                        len(self._suspended),
                    )
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
                    self._domain_reloading = False
                    preserve_compile_snapshot = False
            elif self._pending:
                logger.info(
                    "[%s] session.hello with %d in-flight pending — latest connection wins; no arbitrary replay",
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
                "bridgeReadyContractVersion": BRIDGE_READY_CONTRACT_VERSION,
                "readyBudgetMs": message.payload["readyBudgetMs"],
                "verificationLevel": str(identity.get("verificationLevel") or ""),
                "heartbeatIntervalMs": self.heartbeat_interval_ms,
                "mcpHost": self.host,
                "mcpPort": self.port,
                "unityProjectPath": unity_project_path,
                "mcpWorkingDirectory": mcp_cwd,
                "serverLifecycleId": self.state.lifecycle_id,
                "server_pid": os.getpid(),
                "configured_project_path": self.expected_project_path,
            }
            hello_payload.update(version_payload())
            if self.mcp_label:
                hello_payload["mcpLabel"] = self.mcp_label
            connection = self._connection
            if connection:
                connection.resume_queries = preserve_compile_snapshot
            await self._send_candidate_ack(candidate, message, hello_payload)
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
        if self._shutting_down:
            return
        connection = self._connection
        if websocket is not None and connection is not None:
            if message.type == "event" and message.name == "session.ready":
                await self._begin_bootstrap(connection, message.payload)
                return
            if message.name == "session.bootstrap" and message.type in {"result", "error"}:
                await self._complete_bootstrap(connection, message)
                return
        maintenance_result = bool(connection and message.type in {"result", "error"}
                                  and message.name == "service.restart"
                                  and self._maintenance_pending.get(message.id) == connection.generation
                                  and self._connection_current(connection))
        if websocket is not None and not self.is_ready() and not maintenance_result:
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
                        if (heartbeat_context.get("sessionId") == message.session_id
                                and heartbeat_context.get("authoritative") is True
                                and heartbeat_context.get("isStale") is not True):
                            self._apply_heartbeat_interval(heartbeat_context)
                        self._observe_editor_lifecycle(heartbeat_context)
                        self.editor_observation()
                        self._notify_editor_execution_state()
                else:
                    self.state.update_editor_state(heartbeat_context)
            return

        if message.type in ("result", "error"):
            if connection and self._pending_generation.get(message.id) != connection.generation:
                return
            fut = self._pending.pop(message.id, None)
            self._pending_generation.pop(message.id, None)
            if fut and not fut.done():
                result = to_wire(message)
                if connection:
                    result["_connectionGeneration"] = connection.generation
                    if maintenance_result:
                        result["_maintenanceControl"] = True
                fut.set_result(result)
            self._maintenance_pending.pop(message.id, None)
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
                if (message.payload.get("sessionId") == message.session_id
                        and message.payload.get("authoritative") is True
                        and message.payload.get("isStale") is not True):
                    self._apply_heartbeat_interval(message.payload)
                self._observe_editor_lifecycle(message.payload)
                phase = str(self.state.compile.phase or "").lower()
                self._note_compile_busy(phase in {"queued", "compiling", "compiler_finished", "domain_reload", "verifying"})
                if str(self.state.transition).lower() == "domain_reload_starting":
                    self._domain_reloading = True
                    self._extend_grace_for_domain_reload()
                self.editor_observation()
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
