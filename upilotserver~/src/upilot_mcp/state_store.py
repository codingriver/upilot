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
    initial_session_id: str = ""
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
    play_mode_state: str = "unknown"
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
            self.compile.initial_session_id = self.editor.session_id
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
            self.compile.phase = "failed" if int(payload.get("errorCount", 0)) > 0 else "completed"
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
            self.compile.phase = "failed" if self.compile.error_count > 0 else "completed"
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
            incoming_error_count = int(payload.get("errorCount", self.compile.error_count))
            self.compile.phase = "failed" if incoming_error_count > 0 else "completed"
            self.editor.is_compiling = False
            incoming_finished_at = int(payload.get("finishedAt") or 0)
            self.compile.finished_at = (
                incoming_finished_at
                if incoming_finished_at > 0
                else max(self.compile.started_at, _now_ms())
            )
            self.compile.error_count = incoming_error_count
            self.compile.warning_count = int(payload.get("warningCount", self.compile.warning_count))
            self.compile.last_duration_ms = int(payload.get("durationMs", self.compile.last_duration_ms))
        self.compile.last_progress_at = _now_ms()

    def update_compile_errors(self, payload: dict[str, Any]) -> None:
        errors = payload.get("errors") or []
        self.compile.errors = list(errors)
        self.compile.error_count = int(payload.get("total", len(self.compile.errors)))
        self.compile.warning_count = int(
            payload.get("currentCompileWarningCount", payload.get("warningCount", self.compile.warning_count))
        )
        if self.compile.status in ("finished", "completed") or self.compile.phase in (
            "verifying",
            "completed",
            "failed",
        ):
            self.compile.status = "finished"
            self.compile.phase = "failed" if self.compile.error_count > 0 else "completed"
            self.editor.is_compiling = False
            self.compile.finished_at = self.compile.finished_at or _now_ms()
            self.compile.last_progress_at = _now_ms()

    def update_editor_state(self, payload: dict[str, Any]) -> bool:
        incoming_session_id = str(payload.get("sessionId") or "")
        if (
            incoming_session_id
            and self.editor.session_id
            and incoming_session_id != self.editor.session_id
        ):
            return False

        incoming_updated_at = int(payload.get("updatedAt") or _now_ms())
        if (
            incoming_session_id
            and incoming_session_id == self.editor.session_id
            and self.editor.updated_at
            and incoming_updated_at < self.editor.updated_at
        ):
            return False

        self.editor.connected = bool(payload.get("connected", self.editor.connected))
        self.editor.is_compiling = bool(payload.get("isCompiling", self.editor.is_compiling))
        incoming_compile_phase = str(payload.get("compilePhase") or "").strip()
        incoming_compile_status = str(payload.get("compileStatus") or "").strip()
        if incoming_compile_phase or incoming_compile_status:
            normalized_phase = self._normalize_compile_phase(
                incoming_compile_status,
                incoming_compile_phase,
            )
            self.compile.phase = normalized_phase
            self.compile.status = incoming_compile_status or normalized_phase
            self.compile.compile_request_id = str(
                payload.get("compileRequestId") or self.compile.compile_request_id
            )
            self.compile.started_at = int(
                payload.get("compileStartedAt") or self.compile.started_at
            )
            self.compile.finished_at = int(
                payload.get("compileFinishedAt") or self.compile.finished_at
            )
            self.compile.last_progress_at = int(
                payload.get("lastProgressAt") or self.compile.last_progress_at
            )
            if normalized_phase in ("queued", "compiling", "domain_reload", "verifying"):
                self.editor.is_compiling = True
            elif normalized_phase in ("completed", "failed"):
                self.editor.is_compiling = False
        play_mode_state = str(payload.get("playModeState") or "").strip().lower()
        if not play_mode_state:
            if bool(payload.get("isPaused", False)):
                play_mode_state = "pause"
            elif bool(payload.get("isPlaying", False)):
                play_mode_state = "play"
        if play_mode_state in ("playing",):
            play_mode_state = "play"
        elif play_mode_state in ("paused",):
            play_mode_state = "pause"
        elif play_mode_state not in ("edit", "play", "pause", "unknown"):
            play_mode_state = "unknown"
        if play_mode_state:
            self.editor.play_mode_state = play_mode_state
        self.editor.active_scene = str(payload.get("activeScene", self.editor.active_scene))
        self.editor.updated_at = incoming_updated_at
        self.editor.authoritative = bool(payload.get("authoritative", self.editor.authoritative))
        self.editor.source = str(payload.get("source") or "bridge")
        self.editor.session_id = incoming_session_id or self.editor.session_id
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
        if (
            self.compile.phase in ("domainReload", "domain_reload")
            and self.editor.authoritative
            and not self.editor.is_compiling
        ):
            self.compile.phase = "verifying"
            self.compile.status = "verifying"
            self.compile.last_progress_at = _now_ms()
        return True

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

    @staticmethod
    def _normalize_compile_phase(status: str, phase: str) -> str:
        normalized = (phase or status or "idle").strip()
        folded = normalized.replace("-", "_").lower()
        if folded in ("accepted", "queue", "queued"):
            return "queued"
        if folded in ("started", "in_progress", "compiling"):
            return "compiling"
        if folded in ("domainreload", "domain_reload", "recovering_after_reload"):
            return "domain_reload"
        if folded in ("verify", "verifying"):
            return "verifying"
        if folded in ("finish", "finished", "complete", "completed", "success"):
            return "completed"
        if folded in ("error", "failed", "failure"):
            return "failed"
        return "idle" if folded in ("", "idle", "ready") else folded

    def execution_state(self, *, stale_after_ms: int = 5000) -> dict[str, Any]:
        now = _now_ms()
        updated_at = int(self.editor.updated_at or 0)
        age_ms = max(0, now - updated_at) if updated_at else 0
        play_state = (self.editor.play_mode_state or "unknown").strip().lower()
        if play_state not in ("edit", "play", "pause"):
            play_state = "unknown"
        is_stale = (
            not updated_at
            or age_ms > max(250, int(stale_after_ms))
            or not self.editor.authoritative
        )
        authoritative = bool(self.editor.connected and self.editor.authoritative and not is_stale)
        compile_phase = self._normalize_compile_phase(
            self.compile.status,
            self.compile.phase,
        )
        if self.editor.is_compiling and compile_phase not in (
            "queued",
            "compiling",
            "domain_reload",
            "verifying",
        ):
            compile_phase = "compiling"
        is_compiling = bool(
            self.editor.is_compiling or compile_phase in ("queued", "compiling", "domain_reload", "verifying")
        )

        blocked_reason = ""
        next_action = ""
        status = "ready"
        if not self.editor.connected:
            status = "disconnected"
            blocked_reason = "UnityDisconnected"
            next_action = "Reconnect the intended Unity project and call unity_mcp_status(forceFresh=true)."
        elif not authoritative:
            status = "recovering_after_reload" if self.editor.source == "session.hello" else "unknown"
            blocked_reason = "EditorContextStale" if is_stale and updated_at else "EditorContextUnknown"
            next_action = "Call unity_mcp_status(forceFresh=true) and wait for a live authoritative Editor response."
        elif play_state in ("play", "pause"):
            status = "blocked"
            blocked_reason = "PlayMode"
            next_action = "Exit PlayMode after user confirmation, then retry the operation."
        elif play_state != "edit":
            status = "unknown"
            blocked_reason = "EditorModeUnknown"
            next_action = "Wait for an authoritative EditMode response before mutating the Editor."
        elif compile_phase == "failed":
            status = "failed"
            blocked_reason = "CompileErrors"
            next_action = "Read unity_compile_errors and fix the reported compiler errors."
        elif is_compiling:
            status = compile_phase
            blocked_reason = "CompilationInProgress"
            next_action = "Continue with unity_compile_wait until compilation reaches a terminal state."

        ready = status == "ready"
        last_progress = int(self.compile.last_progress_at or self.compile.started_at or 0)
        pump_age_ms = (
            max(0, now - int(self.editor.last_main_thread_pump_at or 0))
            if self.editor.last_main_thread_pump_at
            else 0
        )
        suspected_stuck = bool(
            is_compiling
            and last_progress
            and now - last_progress > 60000
        )
        self.compile.suspected_stuck = suspected_stuck
        if suspected_stuck:
            next_action = "Inspect unity_hang_status before retrying or restarting Unity."

        return {
            "status": status,
            "ready": ready,
            "blocked": bool(blocked_reason),
            "blockedReason": blocked_reason,
            "nextAction": next_action,
            "unityConnected": self.editor.connected,
            "authoritative": authoritative,
            "source": self.editor.source or "cache",
            "sessionId": self.editor.session_id,
            "contextUpdatedAt": updated_at,
            "updatedAt": updated_at,
            "ageMs": age_ms,
            "isStale": is_stale,
            "playModeState": play_state,
            "isPlaying": play_state == "play",
            "isPaused": play_state == "pause",
            "isCompiling": is_compiling,
            "compileStatus": self.compile.status,
            "compilePhase": compile_phase,
            "compileRequestId": self.compile.compile_request_id,
            "compileStartedAt": self.compile.started_at,
            "compileFinishedAt": self.compile.finished_at,
            "lastProgressAt": last_progress,
            "compileErrorCount": self.compile.error_count,
            "compileWarningCount": self.compile.warning_count,
            "activeScene": self.editor.active_scene,
            "lastMainThreadPumpAt": self.editor.last_main_thread_pump_at,
            "editorPumpAgeMs": pump_age_ms,
            "mainThreadQueueDepth": self.editor.main_thread_queue_depth,
            "lastDequeuedCommandId": self.editor.last_dequeued_command_id,
            "suspectedStuck": suspected_stuck,
            "processId": self.editor.process_id,
            "timestamp": now,
        }

    def response_context(self, *, stale_after_ms: int = 5000) -> dict[str, Any]:
        return self.execution_state(stale_after_ms=stale_after_ms)


def _now_ms() -> int:
    import time

    return int(time.time() * 1000)
