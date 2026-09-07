from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import json
import os
from pathlib import Path
import sqlite3
from typing import Any
import uuid


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
    compile_operation_id: str = ""
    originating_command_request_id: str = ""
    write_batch_id: str = ""
    write_batch_created_at: int = 0
    compile_origin: str = ""
    terminal: bool = False
    verification_pending: bool = False
    errors_verified: bool = False
    last_compile_requested_at: int = 0
    last_compile_started_at: int = 0
    last_compiler_finished_at: int = 0
    last_compile_verified_at: int = 0
    last_terminal_compile_at: int = 0
    reload_id: str = ""
    domain_reload_observed: bool = False


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
        self.project_id = ""
        self.producer_epoch = ""
        self.domain_generation = 0
        self.sequence = 0
        self.snapshot_id = ""
        self.transition = ""
        self.observed_at = 0
        self.received_at = 0
        self.pending_write_batch_id = ""
        self.compile_deferred_reason = ""
        self.correlation_verified = False
        self.accepted_snapshot_count = 0
        self.rejected_out_of_order_count = 0
        self.persist_failure_count = 0
        self.pre_reload_publish_failure_count = 0
        self._project_path = ""
        self._db_path: Path | None = None

    def configure_project(self, project_path: str) -> None:
        resolved = str(Path(project_path).resolve())
        if self._project_path == resolved and self._db_path is not None:
            return
        if self._project_path and self._project_path != resolved:
            self.compile = CompileSnapshot()
            self.editor = EditorSnapshot()
            self.project_id = ""
            self.producer_epoch = ""
            self.domain_generation = 0
            self.sequence = 0
            self.snapshot_id = ""
            self.transition = ""
            self.observed_at = 0
            self.received_at = 0
            self.pending_write_batch_id = ""
            self.compile_deferred_reason = ""
            self.correlation_verified = False
        self._project_path = resolved
        self._db_path = Path(resolved) / "Library" / "UPilot" / "ServerState" / "state-v2.sqlite3"
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        with sqlite3.connect(self._db_path) as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("CREATE TABLE IF NOT EXISTS project_state (project_path TEXT PRIMARY KEY, snapshot_json TEXT NOT NULL, received_at INTEGER NOT NULL)")
            db.execute("CREATE TABLE IF NOT EXISTS state_events (project_path TEXT NOT NULL, producer_epoch TEXT NOT NULL, domain_generation INTEGER NOT NULL, sequence INTEGER NOT NULL, transition TEXT NOT NULL, observed_at INTEGER NOT NULL, snapshot_json TEXT NOT NULL, PRIMARY KEY(project_path, producer_epoch, domain_generation, sequence))")
            db.execute("CREATE TABLE IF NOT EXISTS write_batches (write_batch_id TEXT PRIMARY KEY, project_path TEXT NOT NULL, operation_id TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, status TEXT NOT NULL, compile_when_edit_mode INTEGER NOT NULL, paths_json TEXT NOT NULL, files_sha256 TEXT NOT NULL, compile_operation_id TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '')")
            if "changes_json" not in {row[1] for row in db.execute("PRAGMA table_info(write_batches)")}:
                db.execute("ALTER TABLE write_batches ADD COLUMN changes_json TEXT")
            db.execute("CREATE TABLE IF NOT EXISTS test_jobs (project_path TEXT NOT NULL, task_id TEXT NOT NULL, state_json TEXT NOT NULL, PRIMARY KEY(project_path, task_id))")
            db.execute(
                "UPDATE write_batches SET status='deferred',updated_at=? WHERE project_path=? AND status IN ('syncing','compiling')",
                (_now_ms(), resolved),
            )
            row = db.execute("SELECT snapshot_json FROM project_state WHERE project_path = ?", (resolved,)).fetchone()
            pending = db.execute(
                "SELECT write_batch_id FROM write_batches WHERE project_path=? AND status IN ('pending','deferred') ORDER BY updated_at DESC LIMIT 1",
                (resolved,),
            ).fetchone()
        if row:
            try:
                payload = json.loads(row[0])
                self.update_editor_execution_state(payload, restored=True)
                self.editor.authoritative = False
                self.editor.connected = False
                self.editor.source = "server-persisted"
            except (TypeError, ValueError, json.JSONDecodeError):
                self.persist_failure_count += 1
        if pending:
            self.pending_write_batch_id = str(pending[0])

    def register_write_batch(
        self,
        paths: list[str],
        *,
        created_at: int,
        files_sha256: str,
        compile_when_edit_mode: bool,
        changes: list[dict[str, str]] | None = None,
    ) -> dict[str, Any]:
        if self._db_path is None or not self._project_path:
            raise RuntimeError("StateStore project persistence is not configured")
        now = _now_ms()
        if changes is not None:
            created_at = max(now, created_at)
        normalized_paths = sorted(dict.fromkeys(paths))
        with sqlite3.connect(self._db_path) as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute(
                "SELECT write_batch_id,operation_id,created_at,updated_at,paths_json,changes_json,compile_when_edit_mode FROM write_batches WHERE project_path=? AND status IN ('pending','deferred') ORDER BY updated_at DESC LIMIT 1",
                (self._project_path,),
            ).fetchone()
            coalesced = bool(
                row and now - int(row[3]) <= 500
                and (row[5] is None) == (changes is None)
                and bool(row[6]) == compile_when_edit_mode
            )
            if changes is not None:
                previous_changes = json.loads(row[5]) if coalesced else []
                merged = {os.path.normcase(item["path"]): item for item in [*previous_changes, *changes]}
                changes = [merged[key] for key in sorted(merged)]
                normalized_paths = [item["path"] for item in changes if item["kind"] == "write"]
                files_sha256 = hashlib.sha256(
                    json.dumps(changes, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
                ).hexdigest()
            changes_json = json.dumps(changes) if changes is not None else None
            if coalesced:
                batch_id, operation_id = str(row[0]), str(row[1])
                created_at = max(int(row[2]), created_at)
                if changes is None:
                    previous = json.loads(row[4])
                    normalized_paths = sorted(dict.fromkeys([*previous, *normalized_paths]))
                db.execute(
                    "UPDATE write_batches SET created_at=?,updated_at=?,compile_when_edit_mode=?,paths_json=?,files_sha256=?,changes_json=? WHERE write_batch_id=?",
                    (created_at, now, int(compile_when_edit_mode), json.dumps(normalized_paths), files_sha256, changes_json, batch_id),
                )
            else:
                batch_id = f"wb-{uuid.uuid4()}"
                operation_id = f"op-{uuid.uuid4()}"
                db.execute(
                    "INSERT INTO write_batches(write_batch_id,project_path,operation_id,created_at,updated_at,status,compile_when_edit_mode,paths_json,files_sha256,changes_json) VALUES(?,?,?,?,?,?,?,?,?,?)",
                    (batch_id, self._project_path, operation_id, created_at, now, "pending", int(compile_when_edit_mode), json.dumps(normalized_paths), files_sha256, changes_json),
                )
                coalesced = False
        self.pending_write_batch_id = batch_id
        self.compile_deferred_reason = "PlayMode" if self.editor.play_mode_state in ("play", "pause") else ""
        self.correlation_verified = False
        return {
            "writeBatchId": batch_id,
            "operationId": operation_id,
            "writeBatchCreatedAt": created_at,
            "paths": normalized_paths,
            "filesSha256": files_sha256,
            "compileWhenEditMode": compile_when_edit_mode,
            "coalesced": coalesced,
            **self._write_batch_change_fields(changes_json),
        }

    @staticmethod
    def _write_batch_change_fields(changes_json: str | None) -> dict[str, Any]:
        changes = json.loads(changes_json) if changes_json is not None else None
        return {
            "changes": changes,
            "deletedPaths": [item["path"] for item in changes or [] if item["kind"] == "delete"],
            "filesHashScope": "changes-v1" if changes is not None else "legacy",
        }

    def save_test_job(self, state: dict) -> None:
        if self._db_path is None or state.get("projectPath") != self._project_path:
            raise RuntimeError("Test job persistence is not configured for this project.")
        with sqlite3.connect(self._db_path) as db:
            db.execute(
                "INSERT INTO test_jobs(project_path,task_id,state_json) VALUES(?,?,?) "
                "ON CONFLICT(project_path,task_id) DO UPDATE SET state_json=excluded.state_json",
                (self._project_path, state["taskId"], json.dumps(state, ensure_ascii=False)),
            )

    def load_test_jobs(self) -> list[dict]:
        if self._db_path is None:
            return []
        with sqlite3.connect(self._db_path) as db:
            rows = db.execute("SELECT state_json FROM test_jobs WHERE project_path=?", (self._project_path,)).fetchall()
        return [json.loads(row[0]) for row in rows]

    def pending_write_batches(self) -> list[dict[str, Any]]:
        if self._db_path is None or not self._project_path:
            return []
        with sqlite3.connect(self._db_path) as db:
            rows = db.execute(
                "SELECT write_batch_id,operation_id,created_at,updated_at,status,compile_when_edit_mode,paths_json,files_sha256,compile_operation_id,error,changes_json FROM write_batches WHERE project_path=? AND status IN ('pending','deferred') ORDER BY created_at",
                (self._project_path,),
            ).fetchall()
        return [
            {
                "writeBatchId": row[0], "operationId": row[1], "writeBatchCreatedAt": row[2],
                "updatedAt": row[3], "status": row[4], "compileWhenEditMode": bool(row[5]),
                "paths": json.loads(row[6]), "filesSha256": row[7],
                "compileOperationId": row[8], "error": row[9],
                **self._write_batch_change_fields(row[10]),
            }
            for row in rows
        ]

    def mark_write_batch(self, write_batch_id: str, status: str, *, compile_operation_id: str = "", error: str = "") -> None:
        if self._db_path is None or not write_batch_id:
            return
        with sqlite3.connect(self._db_path) as db:
            db.execute(
                "UPDATE write_batches SET status=?,updated_at=?,compile_operation_id=CASE WHEN ?='' THEN compile_operation_id ELSE ? END,error=? WHERE write_batch_id=?",
                (status, _now_ms(), compile_operation_id, compile_operation_id, error, write_batch_id),
            )
        if status in ("verified", "failed", "canceled") and self.pending_write_batch_id == write_batch_id:
            self.pending_write_batch_id = ""
            self.compile_deferred_reason = ""

    def get_write_batch(self, write_batch_id: str) -> dict[str, Any] | None:
        if self._db_path is None or not write_batch_id:
            return None
        with sqlite3.connect(self._db_path) as db:
            row = db.execute(
                "SELECT write_batch_id,operation_id,created_at,updated_at,status,compile_when_edit_mode,paths_json,files_sha256,compile_operation_id,error,changes_json FROM write_batches WHERE project_path=? AND write_batch_id=?",
                (self._project_path, write_batch_id),
            ).fetchone()
        if row is None:
            return None
        return {
            "writeBatchId": row[0], "operationId": row[1], "writeBatchCreatedAt": row[2],
            "updatedAt": row[3], "status": row[4], "compileWhenEditMode": bool(row[5]),
            "paths": json.loads(row[6]), "filesSha256": row[7],
            "compileOperationId": row[8], "error": row[9],
            **self._write_batch_change_fields(row[10]),
        }

    def _update_write_batch_terminal(self, write_batch_id: str, status: str, compile_operation_id: str) -> None:
        current = self.get_write_batch(write_batch_id)
        if (
            current is not None
            and str(current.get("status") or "") == status
            and str(current.get("compileOperationId") or "") == compile_operation_id
        ):
            return
        self.mark_write_batch(write_batch_id, status, compile_operation_id=compile_operation_id)

    def _persist_execution_snapshot(self, payload: dict[str, Any]) -> bool:
        if self._db_path is None or not self._project_path:
            self.persist_failure_count += 1
            return False
        raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        try:
            with sqlite3.connect(self._db_path) as db:
                db.execute(
                    "INSERT INTO project_state(project_path,snapshot_json,received_at) VALUES(?,?,?) ON CONFLICT(project_path) DO UPDATE SET snapshot_json=excluded.snapshot_json,received_at=excluded.received_at",
                    (self._project_path, raw, self.received_at),
                )
                if self.transition != "heartbeat":
                    db.execute(
                        "INSERT OR IGNORE INTO state_events(project_path,producer_epoch,domain_generation,sequence,transition,observed_at,snapshot_json) VALUES(?,?,?,?,?,?,?)",
                        (self._project_path, self.producer_epoch, self.domain_generation, self.sequence, self.transition, self.observed_at, raw),
                    )
                db.execute(
                    "DELETE FROM state_events WHERE rowid IN (SELECT rowid FROM state_events WHERE project_path=? ORDER BY rowid DESC LIMIT -1 OFFSET 500)",
                    (self._project_path,),
                )
            return True
        except sqlite3.Error:
            self.persist_failure_count += 1
            return False

    def update_editor_execution_state(self, payload: dict[str, Any], *, restored: bool = False) -> bool:
        if int(payload.get("stateContractVersion") or 0) < 2:
            return False
        incoming_epoch = str(payload.get("producerEpoch") or "")
        incoming_domain = int(payload.get("domainGeneration") or 0)
        incoming_sequence = int(payload.get("sequence") or 0)
        incoming_session_id = str(payload.get("sessionId") or "")
        if not incoming_epoch or incoming_sequence <= 0:
            return False
        if (
            not restored
            and incoming_session_id
            and self.editor.session_id
            and incoming_session_id != self.editor.session_id
        ):
            self.rejected_out_of_order_count += 1
            return False
        if not restored and incoming_epoch == self.producer_epoch:
            if (incoming_domain, incoming_sequence) <= (self.domain_generation, self.sequence):
                self.rejected_out_of_order_count += 1
                return False

        self.project_id = str(payload.get("projectId") or self.project_id)
        self.producer_epoch = incoming_epoch
        self.domain_generation = incoming_domain
        self.sequence = incoming_sequence
        self.snapshot_id = str(payload.get("snapshotId") or f"{incoming_epoch}:{incoming_domain}:{incoming_sequence}")
        self.transition = str(payload.get("transition") or "snapshot")
        self.observed_at = int(payload.get("observedAt") or payload.get("updatedAt") or 0)
        self.received_at = _now_ms()
        self.pending_write_batch_id = str(payload.get("pendingWriteBatchId") or self.pending_write_batch_id)
        incoming_deferred_reason = str(payload.get("compileDeferredReason") or "")
        if incoming_deferred_reason:
            self.compile_deferred_reason = incoming_deferred_reason
        elif not self.pending_write_batch_id:
            self.compile_deferred_reason = ""
        if bool(payload.get("preReloadPublishFailed")):
            self.pre_reload_publish_failure_count += 1

        self.editor.connected = bool(payload.get("connected", True))
        self.editor.authoritative = bool(payload.get("authoritative", True)) and not restored
        self.editor.source = "server-persisted" if restored else str(payload.get("source") or "editor.execution_state")
        self.editor.session_id = str(payload.get("sessionId") or self.editor.session_id)
        self.editor.updated_at = self.received_at if not restored else self.observed_at
        self.editor.play_mode_state = str(payload.get("playModeState") or "unknown").lower()
        if self.pending_write_batch_id and not self.compile_deferred_reason and self.editor.play_mode_state in ("play", "pause"):
            self.compile_deferred_reason = "PlayMode"
        self.editor.is_compiling = bool(payload.get("isCompiling", False))
        self.editor.active_scene = str(payload.get("activeScene") or self.editor.active_scene)
        self.editor.last_main_thread_pump_at = int(payload.get("lastMainThreadPumpAt") or self.editor.last_main_thread_pump_at)
        self.editor.main_thread_queue_depth = int(payload.get("mainThreadQueueDepth") or 0)
        self.editor.last_dequeued_command_id = str(payload.get("lastDequeuedCommandId") or self.editor.last_dequeued_command_id)
        self.editor.process_id = int(payload.get("processId") or self.editor.process_id)

        compile_state = self.compile
        compile_state.status = str(payload.get("compileStatus") or payload.get("compilePhase") or compile_state.status)
        compile_state.phase = self._normalize_compile_phase(compile_state.status, str(payload.get("compilePhase") or ""))
        compile_state.compile_request_id = str(payload.get("compileRequestId") or compile_state.compile_request_id)
        compile_state.compile_operation_id = str(payload.get("compileOperationId") or compile_state.compile_operation_id)
        compile_state.originating_command_request_id = str(payload.get("originatingCommandRequestId") or compile_state.originating_command_request_id)
        compile_state.write_batch_id = str(payload.get("writeBatchId") or compile_state.write_batch_id)
        compile_state.write_batch_created_at = int(payload.get("writeBatchCreatedAt") or compile_state.write_batch_created_at)
        compile_state.compile_origin = str(payload.get("compileOrigin") or compile_state.compile_origin)
        compile_state.terminal = bool(payload.get("terminal", False))
        compile_state.verification_pending = bool(payload.get("verificationPending", False))
        compile_state.errors_verified = bool(payload.get("errorsVerified", False))
        compile_state.started_at = int(payload.get("compileStartedAt") or compile_state.started_at)
        compile_state.finished_at = int(payload.get("compileFinishedAt") or compile_state.finished_at)
        compile_state.last_compile_requested_at = int(payload.get("lastCompileRequestedAt") or compile_state.last_compile_requested_at)
        compile_state.last_compile_started_at = int(payload.get("lastCompileStartedAt") or compile_state.last_compile_started_at)
        compile_state.last_compiler_finished_at = int(payload.get("lastCompilerFinishedAt") or compile_state.last_compiler_finished_at)
        compile_state.last_compile_verified_at = int(payload.get("lastCompileVerifiedAt") or compile_state.last_compile_verified_at)
        compile_state.last_terminal_compile_at = int(payload.get("lastTerminalCompileAt") or compile_state.last_terminal_compile_at)
        compile_state.reload_id = str(payload.get("reloadId") or compile_state.reload_id)
        compile_state.domain_reload_observed = bool(payload.get("domainReloadObserved", compile_state.domain_reload_observed))
        compile_state.error_count = int(payload.get("errorCount") or 0)
        compile_state.warning_count = int(payload.get("warningCount") or 0)
        compile_state.last_progress_at = max(self.observed_at, compile_state.last_compile_verified_at, compile_state.last_compiler_finished_at, compile_state.last_compile_started_at)
        registered_batch = self.get_write_batch(compile_state.write_batch_id)
        registered_operation_id = str((registered_batch or {}).get("compileOperationId") or "")
        operation_matches = bool(
            compile_state.compile_operation_id
            and (not registered_operation_id or registered_operation_id == compile_state.compile_operation_id)
        )
        if registered_batch and compile_state.compile_operation_id and not registered_operation_id:
            self.mark_write_batch(
                compile_state.write_batch_id,
                "compiling" if not compile_state.terminal else str(registered_batch["status"]),
                compile_operation_id=compile_state.compile_operation_id,
            )
        preserves_verified_terminal = bool(
            not self.pending_write_batch_id
            and (
                self.correlation_verified
                or str((registered_batch or {}).get("status") or "") in {"verified", "failed"}
            )
            and compile_state.terminal
            and compile_state.errors_verified
            and registered_batch is not None
            and operation_matches
        )
        self.correlation_verified = bool(
            compile_state.terminal
            and compile_state.errors_verified
            and compile_state.write_batch_id
            and (
                compile_state.write_batch_id == self.pending_write_batch_id
                or preserves_verified_terminal
            )
            and registered_batch is not None
            and int(registered_batch["writeBatchCreatedAt"]) == compile_state.write_batch_created_at
            and operation_matches
            and compile_state.last_compile_verified_at >= compile_state.write_batch_created_at > 0
        )
        if self.correlation_verified:
            self.pending_write_batch_id = ""
            self.compile_deferred_reason = ""
            self._update_write_batch_terminal(
                compile_state.write_batch_id,
                "verified" if compile_state.phase == "completed" else "failed",
                compile_state.compile_operation_id,
            )
        self.accepted_snapshot_count += 0 if restored else 1
        if not restored and not self._persist_execution_snapshot(payload):
            self.editor.authoritative = False
            self.editor.source = "state-persistence-failed"
            return False
        return True

    def update_editor_context(self, payload: dict[str, Any]) -> bool:
        """Apply legacy context, or only refresh liveness for the current v2 snapshot."""
        if not self.producer_epoch:
            return self.update_editor_state(payload)
        incoming_snapshot_id = str(payload.get("snapshotId") or "")
        if incoming_snapshot_id != self.snapshot_id:
            return False
        self.editor.connected = bool(payload.get("connected", True))
        self.editor.updated_at = _now_ms()
        self.received_at = self.editor.updated_at
        return True

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
        if self.producer_epoch:
            return
        self.compile.compile_operation_id = str(
            payload.get("compileOperationId") or self.compile.compile_operation_id
        )
        self.compile.write_batch_id = str(
            payload.get("writeBatchId") or self.compile.write_batch_id
        )
        self.compile.write_batch_created_at = int(
            payload.get("writeBatchCreatedAt") or self.compile.write_batch_created_at
        )
        self.compile.compile_origin = str(payload.get("compileOrigin") or self.compile.compile_origin)
        self.compile.terminal = bool(payload.get("terminal", self.compile.terminal))
        self.compile.verification_pending = bool(
            payload.get("verificationPending", self.compile.verification_pending)
        )
        self.compile.errors_verified = bool(payload.get("errorsVerified", self.compile.errors_verified))
        self.compile.last_compile_requested_at = int(
            payload.get("lastCompileRequestedAt") or self.compile.last_compile_requested_at
        )
        self.compile.last_compile_started_at = int(
            payload.get("lastCompileStartedAt") or self.compile.last_compile_started_at
        )
        self.compile.last_compiler_finished_at = int(
            payload.get("lastCompilerFinishedAt") or self.compile.last_compiler_finished_at
        )
        self.compile.last_compile_verified_at = int(
            payload.get("lastCompileVerifiedAt") or self.compile.last_compile_verified_at
        )
        self.compile.last_terminal_compile_at = int(
            payload.get("lastTerminalCompileAt") or self.compile.last_terminal_compile_at
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
            if normalized_phase in ("queued", "compiling", "compiler_finished", "domain_reload", "verifying"):
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
            updated_at=0,
        )

    @staticmethod
    def _normalize_compile_phase(status: str, phase: str) -> str:
        normalized = (phase or status or "idle").strip()
        folded = normalized.replace("-", "_").lower()
        if folded in ("accepted", "queue", "queued"):
            return "queued"
        if folded in ("started", "in_progress", "compiling"):
            return "compiling"
        if folded in ("compiler_finished", "compilerfinished"):
            return "compiler_finished"
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
            "compiler_finished",
            "domain_reload",
            "verifying",
        ):
            compile_phase = "compiling"
        is_compiling = bool(
            self.editor.is_compiling or compile_phase in ("queued", "compiling", "compiler_finished", "domain_reload", "verifying")
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
            blocked_reason = (
                "EditorContextStale"
                if is_stale and (updated_at or self.editor.source == "session.hello")
                else "EditorContextUnknown"
            )
            next_action = "Call unity_mcp_status(forceFresh=true) and wait for a live authoritative Editor response."
        elif play_state in ("play", "pause"):
            status = "blocked"
            blocked_reason = "PlayMode"
            next_action = "Exit PlayMode after user confirmation, then retry the operation."
        elif play_state != "edit":
            status = "unknown"
            blocked_reason = "EditorModeUnknown"
            next_action = "Wait for an authoritative EditMode response before mutating the Editor."
        elif self.pending_write_batch_id and not self.correlation_verified:
            status = "pending_code_sync"
            blocked_reason = "PendingCodeSync"
            next_action = "Wait for the registered write batch to synchronize and reach a correlated compile terminal state."
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
        pending_batch_age_ms = 0
        if self.pending_write_batch_id:
            pending_batch = self.get_write_batch(self.pending_write_batch_id)
            if pending_batch:
                pending_batch_age_ms = max(0, now - int(pending_batch["writeBatchCreatedAt"] or 0))

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
            "compileOperationId": self.compile.compile_operation_id,
            "originatingCommandRequestId": self.compile.originating_command_request_id,
            "writeBatchId": self.compile.write_batch_id,
            "writeBatchCreatedAt": self.compile.write_batch_created_at,
            "compileOrigin": self.compile.compile_origin,
            "terminal": self.compile.terminal,
            "verificationPending": self.compile.verification_pending,
            "errorsVerified": self.compile.errors_verified,
            "lastCompileRequestedAt": self.compile.last_compile_requested_at,
            "lastCompileStartedAt": self.compile.last_compile_started_at,
            "lastCompilerFinishedAt": self.compile.last_compiler_finished_at,
            "lastCompileVerifiedAt": self.compile.last_compile_verified_at,
            "lastTerminalCompileAt": self.compile.last_terminal_compile_at,
            "reloadId": self.compile.reload_id,
            "domainReloadObserved": self.compile.domain_reload_observed,
            "pendingWriteBatchId": self.pending_write_batch_id,
            "compileDeferredReason": self.compile_deferred_reason,
            "stateContractVersion": 2 if self.producer_epoch else 1,
            "projectId": self.project_id,
            "producerEpoch": self.producer_epoch,
            "domainGeneration": self.domain_generation,
            "sequence": self.sequence,
            "snapshotId": self.snapshot_id,
            "transition": self.transition,
            "observedAt": self.observed_at,
            "receivedAt": self.received_at,
            "correlationVerified": self.correlation_verified,
            "acceptedSnapshotCount": self.accepted_snapshot_count,
            "rejectedOutOfOrderCount": self.rejected_out_of_order_count,
            "persistFailureCount": self.persist_failure_count,
            "preReloadPublishFailureCount": self.pre_reload_publish_failure_count,
            "pendingBatchAgeMs": pending_batch_age_ms,
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
