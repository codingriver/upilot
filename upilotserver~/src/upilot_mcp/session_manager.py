from __future__ import annotations

import time

from .models import SessionState
from .protocol import now_ms


class SessionManager:
    def __init__(self, heartbeat_timeout_ms: int = 9000) -> None:
        self._active: SessionState | None = None
        self.heartbeat_timeout_ms = heartbeat_timeout_ms
        self.phase = "disconnected"
        self._ready = False
        self._ready_deadline = 0.0

    @property
    def active(self) -> SessionState | None:
        return self._active

    def on_hello(self, session_id: str, payload: dict) -> SessionState:
        self.phase = "waiting_bridge_ready"
        self._ready = False
        self._ready_deadline = time.monotonic() + int(payload.get("readyBudgetMs") or 300000) / 1000
        self._active = SessionState(
            session_id=session_id,
            unity_version=str(payload.get("unityVersion", "")),
            project_path=str(payload.get("projectPath", "")),
            platform=str(payload.get("platform", "")),
            process_id=int(payload.get("processId") or 0),
            process_created_at=int(payload.get("processCreatedAt") or 0),
            process_role=str(payload.get("processRole") or ""),
            identity_contract_version=int(payload.get("identityContractVersion") or 0),
            verification_level=str(payload.get("verificationLevel") or ""),
            identity_verified=bool(payload.get("identityVerified")),
            connected=True,
            authenticated=True,
            last_heartbeat_at=now_ms(),
        )
        return self._active

    def mark_ready(self, session: SessionState) -> bool:
        if self._active is not session or not session.connected or time.monotonic() >= self._ready_deadline:
            return False
        self.phase = "ready"
        self._ready = True
        session.last_heartbeat_at = now_ms()
        return True

    def is_ready(self) -> bool:
        return self._ready and self.is_connected()

    def close_gate(self, phase: str = "disconnected") -> None:
        self._ready = False
        self.phase = phase

    def on_heartbeat(self, session_id: str) -> None:
        self.touch(session_id)

    def touch(self, session_id: str) -> None:
        if not self._ready or not self._active or self._active.session_id != session_id:
            return
        self._active.last_heartbeat_at = now_ms()
        self._active.connected = True

    def is_connected(self) -> bool:
        if not self._active:
            return False
        if not self._ready:
            if time.monotonic() >= self._ready_deadline:
                self._active.connected = False
            return self._active.connected
        if now_ms() - self._active.last_heartbeat_at > self.heartbeat_timeout_ms:
            self._active.connected = False
            self.close_gate("lease_expired")
        return self._active.connected

    def disconnect(self, session_id: str | None = None, *, force: bool = False) -> None:
        """Mark session disconnected.

        - ``force=True`` (server shutdown): always clear *connected*.
        - ``session_id`` set: only clear when it matches *active* (stale socket after reconnect).
        - ``session_id`` is None and not force: no-op (socket died before ``session.hello``).
        """
        if not self._active:
            return
        if force:
            self._active.connected = False
            self.close_gate()
            return
        if session_id is None:
            return
        if self._active.session_id != session_id:
            return
        self._active.connected = False
        self.close_gate()
