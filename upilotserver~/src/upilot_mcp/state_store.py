from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(slots=True)
class CommandRecord:
    command_id: str
    request_id: str
    name: str
    payload: dict[str, Any]
    status: str = "pending"
    result: dict[str, Any] | None = None
    error: dict[str, Any] | None = None
    created_at: int = 0
    sent_at: int = 0
    completed_at: int = 0


@dataclass(slots=True)
class CompileSnapshot:
    status: str = "idle"
    phase: str = "idle"
    compile_request_id: str = ""
    error_count: int = 0
    warning_count: int = 0
    started_at: int = 0
    finished_at: int = 0
    last_duration_ms: int = 0
    pipeline_phase: str = ""  # started | finished when last compile.pipeline.* received
    command_queued_at: int = 0
    unity_accepted_at: int = 0
    last_progress_at: int = 0
    suspected_stuck: bool = False
    errors: list[dict[str, Any]] = field(default_factory=list)


@dataclass(slots=True)
class EditorSnapshot:
    connected: bool = False
    is_compiling: bool = False
    play_mode_state: str = "edit"
    active_scene: str = ""
    updated_at: int = 0
    authoritative: bool = False
    source: str = "cache"
    session_id: str = ""
    last_main_thread_pump_at: int = 0
    main_thread_queue_depth: int = 0
    last_dequeued_command_id: str = ""
    process_id: int = 0


class StateStore:
    def __init__(self) -> None:
        self.commands: dict[str, CommandRecord] = {}
        self.compile = CompileSnapshot()
        self.editor = EditorSnapshot()
        self.auto_fix = None

    def create_command(self, command_id: str, request_id: str, name: str, payload: dict[str, Any]) -> CommandRecord:
        record = CommandRecord(
            command_id=command_id,
            request_id=request_id,
            name=name,
            payload=payload,
            status="sent",
            created_at=_now_ms(),
            sent_at=_now_ms(),
        )
        self.commands[command_id] = record
        return record

    def mark_success(self, command_id: str, payload: dict[str, Any]) -> None:
        cmd = self.commands.get(command_id)
        if not cmd:
            return
        cmd.status = "success"
        cmd.result = payload
        cmd.completed_at = _now_ms()

    def mark_failed(self, command_id: str, error: dict[str, Any]) -> None:
        cmd = self.commands.get(command_id)
        if not cmd:
            return
        cmd.status = "failed"
        cmd.error = error
        cmd.completed_at = _now_ms()

    def update_compile_status(self, payload: dict[str, Any]) -> None:
        incoming_request_id = str(payload.get("requestId") or "")
        if (
            incoming_request_id
            and incoming_request_id != self.compile.compile_request_id
        ):
            self.compile.unity_accepted_at = 0
            self.compile.started_at = 0
            self.compile.finished_at = 0
        if incoming_request_id:
            self.compile.compile_request_id = incoming_request_id
        status = str(payload.get("status", "")).strip().lower()
        terminal = status in ("finished", "done", "complete", "completed")
        if status in ("queued",):
            self.compile.status = "queued"
            self.compile.phase = "queued"
            self.compile.unity_accepted_at = 0
            self.compile.started_at = 0
            self.compile.finished_at = 0
        elif status in ("accepted",):
            self.compile.status = "accepted"
            self.compile.phase = "accepted"
            self.editor.is_compiling = bool(payload.get("isCompiling", True))
        elif status in ("started", "in_progress", "compiling"):
            self.compile.status = "compiling"
            self.compile.phase = "compiling"
            self.editor.is_compiling = True
        elif terminal:
            self.compile.status = "finished"
            self.compile.phase = "completed"
            self.editor.is_compiling = False
        self.compile.error_count = int(payload.get("errorCount", self.compile.error_count))
        self.compile.warning_count = int(payload.get("warningCount", self.compile.warning_count))
        self.compile.started_at = int(payload.get("startedAt", self.compile.started_at))
        if self.compile.started_at > 0 and self.compile.finished_at < self.compile.started_at:
            self.compile.finished_at = 0
        incoming_finished_at = int(payload.get("finishedAt") or 0)
        if incoming_finished_at > 0:
            self.compile.finished_at = incoming_finished_at
        elif terminal and (
            self.compile.finished_at <= 0
            or (
                self.compile.started_at > 0
                and self.compile.finished_at < self.compile.started_at
            )
        ):
            self.compile.finished_at = _now_ms()
        self.compile.last_progress_at = _now_ms()
        sa, fa = self.compile.started_at, self.compile.finished_at
        if fa > 0 and sa > 0 and fa >= sa:
            self.compile.last_duration_ms = int(fa - sa)

    def update_compile_pipeline(self, payload: dict[str, Any]) -> None:
        phase = str(payload.get("phase", "")).lower()
        self.compile.pipeline_phase = phase
        if phase == "started":
            self.compile.status = "compiling"
            self.compile.phase = "compiling"
            self.editor.is_compiling = True
            self.compile.finished_at = 0
        elif phase == "finished":
            self.compile.status = "finished"
            self.compile.phase = "completed"
            self.editor.is_compiling = False
            self.compile.last_duration_ms = int(payload.get("durationMs", self.compile.last_duration_ms))
            if self.compile.finished_at <= 0:
                self.compile.finished_at = _now_ms()
        self.compile.last_progress_at = _now_ms()

    def update_compile_lifecycle(self, payload: dict[str, Any]) -> None:
        phase = str(payload.get("phase", "")).lower()
        if phase == "started":
            self.compile.status = "compiling"
            self.compile.phase = "compiling"
            self.editor.is_compiling = True
            self.compile.compile_request_id = str(payload.get("requestId", self.compile.compile_request_id))
            self.compile.started_at = int(payload.get("startedAt", self.compile.started_at))
            self.compile.finished_at = 0
        elif phase == "finished":
            self.compile.status = "finished"
            self.compile.phase = "completed"
            self.editor.is_compiling = False
            incoming_finished_at = int(payload.get("finishedAt") or 0)
            self.compile.finished_at = (
                incoming_finished_at
                if incoming_finished_at > 0
                else max(self.compile.started_at, _now_ms())
            )
            self.compile.error_count = int(payload.get("errorCount", self.compile.error_count))
            self.compile.warning_count = int(payload.get("warningCount", self.compile.warning_count))
            self.compile.last_duration_ms = int(payload.get("durationMs", self.compile.last_duration_ms))
        self.compile.last_progress_at = _now_ms()

    def update_compile_errors(self, payload: dict[str, Any]) -> None:
        errors = payload.get("errors") or []
        self.compile.errors = list(errors)
        self.compile.error_count = int(payload.get("total", len(self.compile.errors)))

    def update_editor_state(self, payload: dict[str, Any]) -> None:
        self.editor.connected = bool(payload.get("connected", self.editor.connected))
        self.editor.is_compiling = bool(payload.get("isCompiling", self.editor.is_compiling))
        self.editor.play_mode_state = str(payload.get("playModeState", self.editor.play_mode_state))
        self.editor.active_scene = str(payload.get("activeScene", self.editor.active_scene))
        self.editor.updated_at = int(payload.get("updatedAt") or _now_ms())
        self.editor.authoritative = bool(payload.get("authoritative", True))
        self.editor.source = str(payload.get("source") or "bridge")
        self.editor.session_id = str(payload.get("sessionId") or self.editor.session_id)
        self.editor.last_main_thread_pump_at = int(
            payload.get("lastMainThreadPumpAt") or self.editor.last_main_thread_pump_at
        )
        self.editor.main_thread_queue_depth = int(
            payload.get("mainThreadQueueDepth") or 0
        )
        self.editor.last_dequeued_command_id = str(
            payload.get("lastDequeuedCommandId") or self.editor.last_dequeued_command_id
        )
        self.editor.process_id = int(payload.get("processId") or self.editor.process_id)

    def reset_editor_session(self, session_id: str, process_id: int = 0) -> None:
        self.editor = EditorSnapshot(
            connected=True,
            play_mode_state="unknown",
            session_id=session_id,
            process_id=process_id,
            source="session.hello",
            authoritative=False,
            updated_at=_now_ms(),
        )

    def response_context(self, *, stale_after_ms: int = 2000) -> dict[str, Any]:
        now = _now_ms()
        updated_at = int(self.editor.updated_at or 0)
        age_ms = max(0, now - updated_at) if updated_at else 0
        play_state = self.editor.play_mode_state or "unknown"
        is_stale = not updated_at or age_ms > stale_after_ms or not self.editor.authoritative
        return {
            "unityConnected": self.editor.connected,
            "authoritative": bool(self.editor.authoritative and not is_stale),
            "source": self.editor.source or "cache",
            "sessionId": self.editor.session_id,
            "updatedAt": updated_at,
            "ageMs": age_ms,
            "isStale": is_stale,
            "playModeState": play_state,
            "isPlaying": play_state == "play",
            "isPaused": play_state == "pause",
            "isCompiling": self.editor.is_compiling,
            "activeScene": self.editor.active_scene,
            "lastMainThreadPumpAt": self.editor.last_main_thread_pump_at,
            "mainThreadQueueDepth": self.editor.main_thread_queue_depth,
            "lastDequeuedCommandId": self.editor.last_dequeued_command_id,
            "processId": self.editor.process_id,
            "timestamp": now,
        }


def _now_ms() -> int:
    import time

    return int(time.time() * 1000)
