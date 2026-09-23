"""Finite, exact-target cleanup; deliberately not a scheduler or generic queue reset."""
from __future__ import annotations

import asyncio
import copy
import hashlib
import json
from pathlib import Path
import secrets
import time

from ..protocol import new_id
from ..queue_audit import AUDIT, QUEUE_GUARD, notice, outcome, safe_text
from ..responses import fail, ok
from ..service_maintenance import same_path, _unique_object


def authorization(project):
    try:
        path = Path(project) / ".upilot" / "config.json"
        config = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=_unique_object) if path.exists() else {}
        enabled = config.get("aiQueueCleanupAllowed", True)
        if type(enabled) is not bool:
            raise ValueError("aiQueueCleanupAllowed must be boolean")
        return enabled, "" if enabled else "QUEUE_CLEANUP_NOT_APPROVED"
    except (OSError, ValueError, AttributeError, TypeError):
        return False, "QUEUE_CONFIG_INVALID"


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, default=str).encode()).hexdigest()


def current(value):
    return bool(value.get("cleanupPending") or str(value.get("status", "")).lower() == "recoveryrequired"
                or not (value.get("terminal") or value.get("endedAt")))


def item(kind, identity, value, action="", unsupported=""):
    error = value.get("error") or value.get("failureSignature") or ""
    if isinstance(error, dict):
        error = error.get("code", "")
    return dict(type=kind, id=str(identity), name=safe_text(value.get("taskName") or value.get("displayName")
                or value.get("title") or identity), status=str(value.get("status") or value.get("phase") or "unknown"),
                lastProgress=str(value.get("updatedAt") or value.get("lastProgressAt") or value.get("stage") or value.get("phase") or "unknown"),
                blockingReason=safe_text(error), action=action, unsupportedReason=unsupported)


class QueueDomainService:
    def _queue_project(self):
        session = self.server.session_manager.active
        project = self.server.state.project_path
        if not session or not same_path(session.project_path, project):
            raise ValueError("QUEUE_PROJECT_MISMATCH")
        return project

    async def queue_inventory(self):
        request = new_id("req")
        entries, issues = [], []
        test_ids = set()
        store = self.server.state
        try:
            project = self._queue_project()
        except ValueError as ex:
            return fail(request, str(ex), "Cannot verify the current project; queue is unknown.")
        execution = store.execution_state()
        connected = self.server.is_ready()
        if not connected:
            issues.append("UNITY_NOT_CONNECTED")
        if execution.get("isStale") or not execution.get("authoritative"):
            issues.append("EDITOR_STATE_STALE_OR_UNKNOWN")
        try:
            # Never call task_status / operation_status / recovery to populate the window.
            for kind, key, persisted, memory in (
                ("Task", "taskId", store.load_test_jobs(), getattr(self, "_async_tasks", {})),
                ("Operation", "operationId", store.load_operations(), getattr(self, "_operations", {})),
            ):
                records = {v[key]: v for v in persisted}
                records.update({k: v for k, v in memory.items()
                                if not v.get("projectPath") or same_path(v["projectPath"], project)})
                for identity, value in records.items():
                    if not current(value):
                        continue
                    supported = bool(value.get("durable")) if kind == "Task" else bool(
                        value.get("startEstablished") and (value.get("jobSpec") or {}).get("cancelCall"))
                    entries.append(item(kind, identity, value, "cancel" if supported else "",
                                        "" if supported else "No established safe cancellation adapter."))
                    if value.get("runGuid"):
                        test_ids.add(value["runGuid"])
            batches = {v["writeBatchId"]: v for v in store.pending_write_batches()}
            for identity in (store.pending_write_batch_id, store.compile.write_batch_id):
                if identity:
                    value = store.get_write_batch(identity)
                    if value and not value.get("terminal") and not value.get("disposition") and not value.get("supersededBy"):
                        batches[identity] = value
            for identity, value in batches.items():
                historical = value["status"] == "recovery_required"
                entries.append(item("WriteBatch", identity, value, "release" if historical else "",
                                    "" if historical else "Batch may still execute; cancellation is unsupported."))
            for command in store.commands.values():
                if command.status in {"pending", "sent", "running"} and command.name != "queue.cleanup.log":
                    entries.append(item("Command", command.command_id, dict(displayName=command.name,
                        status=command.status, updatedAt=command.sent_at or command.created_at),
                        unsupported="No generic Bridge command revocation in v1."))
            step_path = Path(project) / "Library" / "UPilot" / "step-run.json"
            if step_path.exists():
                step = json.loads(step_path.read_text(encoding="utf-8-sig"))
                if current(step):
                    entry = item("StepRun", step.get("runId", ""), step, unsupported="Cancel its associated Operation; no forced Step recovery.")
                    entry["operationId"] = step.get("operationId", "")
                    entry["source"] = "persisted"
                    entries.append(entry)
                    issues.append("STEP_LIVE_STATE_UNVERIFIED")
                    for record in step.get("steps", []):
                        if record.get("finishedAtUtcMs"):
                            continue
                        entry = item("Step", record.get("instanceId", ""), {
                            "displayName": record.get("typeIdentity"),
                            "status": record.get("stage"),
                            "updatedAt": record.get("cleanupStartedAtUtcMs") or record.get("startedAtUtcMs"),
                            "error": record.get("error"),
                        }, unsupported="Cancel its associated Operation; no forced Step recovery.")
                        entry.update(operationId=step.get("operationId", ""), runId=step.get("runId", ""), source="persisted")
                        entries.append(entry)
        except Exception:
            issues.append("PERSISTED_QUEUE_DATA_INCOMPLETE")
        if connected:
            try:
                tests = await asyncio.wait_for(
                    self.dispatcher.call(new_id("req"), "test.status", {}, timeout_ms=3000), 3.5)
                test = tests.data if tests.ok and isinstance(tests.data, dict) else None
                if test is None or "status" not in test:
                    issues.append("TEST_STATE_UNAVAILABLE")
                elif test.get("runGuid") not in test_ids and (
                    test.get("cleanupPending") or str(test.get("status", "")).lower() in
                    {"running", "queued", "cancel_requested", "recoveryrequired", "recovering"}
                ):
                    entries.append(item("Test", test.get("runGuid", ""), test, "cancel",
                                        "" if test.get("runGuid") else "Test identity is unknown."))
            except Exception:
                issues.append("TEST_STATE_UNCONFIRMED")
            try:
                captures = await asyncio.wait_for(self.console_capture_list(count=200, active_only=True), 5)
                if not captures.ok or not isinstance(captures.data, dict):
                    issues.append("CAPTURE_STATE_UNAVAILABLE")
                else:
                    data = captures.data
                    values = data.get("sessions")
                    if not isinstance(values, list) or data.get("activeCount") != len(values):
                        issues.append("CAPTURE_STATE_INCOMPLETE")
                    if isinstance(values, list):
                        for value in values:
                            entries.append(item("Capture", value.get("sessionId", ""), {**value, "status": "active"}, "stop"))
            except Exception:
                issues.append("CAPTURE_STATE_UNCONFIRMED")
        if execution.get("mainThreadQueueDepth", 0) > 0:
            issues.append("BRIDGE_QUEUE_NOT_FULLY_ENUMERATED")
        if not same_path(project, self.server.state.project_path):
            return fail(request, "QUEUE_PROJECT_MISMATCH", "Project changed during observation.")
        connected = self.server.is_ready()
        latest = store.execution_state()
        if not connected and "UNITY_NOT_CONNECTED" not in issues:
            issues.append("UNITY_NOT_CONNECTED")
        if latest.get("isStale") or not latest.get("authoritative"):
            if "EDITOR_STATE_STALE_OR_UNKNOWN" not in issues:
                issues.append("EDITOR_STATE_STALE_OR_UNKNOWN")
        if len(entries) > 500:
            entries = entries[:500]
            issues.append("QUEUE_ITEMS_TRUNCATED")
        return ok(request, dict(projectPath=project, observedAt=int(time.time() * 1000),
                                connected=connected, complete=not issues, isStale=bool(latest.get("isStale")),
                                issues=issues, items=entries, authorization=dict(zip(("allowed", "reason"), authorization(project)))))

    async def _queue_target(self, kind, target):
        store = self.server.state
        if kind == "Test":
            if not self.server.is_ready():
                raise ValueError("UNITY_NOT_CONNECTED")
            result = await self.test_results(run_guid=target)
            value = result.data if result.ok else None
            if not value or value.get("runGuid") != target:
                return None
            return {k: value.get(k) for k in ("runGuid", "status", "phase", "cleanupPending",
                                             "cleanupSucceeded", "resultAuthoritative", "cancelRequested")}
        if kind == "WriteBatch":
            return store.get_write_batch(target)
        if kind in {"Task", "Operation"}:
            memory = getattr(self, "_async_tasks" if kind == "Task" else "_operations", {})
            key = "taskId" if kind == "Task" else "operationId"
            values = store.load_test_jobs() if kind == "Task" else store.load_operations()
            value = memory.get(target) or next((v for v in values if v[key] == target), None)
            if value and value.get("projectPath") and not same_path(value["projectPath"], store.project_path):
                raise ValueError("QUEUE_PROJECT_MISMATCH")
            # Bind only safety-relevant state, not poll timestamps/progress/capture counters.
            return None if value is None else copy.deepcopy({
                k: value.get(k) for k in (key, "projectPath", "status", "phase", "terminal", "endedAt",
                                         "runGuid", "durable", "startEstablished", "cancelIntentSent",
                                         "startIntentSent", "startSendState", "cancelSendState",
                                         "cancelRequested", "cleanupPending", "jobSpec")})
        if kind == "Capture":
            root = Path(store.project_path) / "Log" / "UPilotConsole"
            for path in root.glob("*/session.json"):
                value = json.loads(path.read_text(encoding="utf-8-sig"))
                if value.get("sessionId") == target:
                    return {k: value.get(k) for k in ("sessionId", "active", "startedAtUtcMs", "ownerId", "ownerTokenSha256")}
        return None

    async def _queue_operation_guard_valid(self, identity, expected, project):
        return (same_path(self._queue_project(), project) and authorization(project)[0]
                and self.server.is_ready() and await self._queue_target("Operation", identity) == expected)

    def _queue_release_safe(self, value):
        state = self.server.state
        ex = state.execution_state()
        if not self.server.is_ready() or not ex.get("authoritative") or ex.get("isStale"):
            return False
        if ex.get("playModeState") != "edit" or ex.get("isCompiling") or ex.get("mainThreadQueueDepth") != 0:
            return False
        if ex.get("compilePhase") not in {"idle", "completed", "failed"} or ex.get("suspectedStuck"):
            return False
        if getattr(self.server, "_pending", {}) or getattr(self.server, "_suspended", {}):
            return False
        task = getattr(self, "_write_batch_resume_task", None)
        if task and not task.done():
            return False
        if any(c.status in {"pending", "sent"} and c.name in {"compile.request", "asset.refresh"}
               for c in state.commands.values()):
            return False
        return (value.get("status") == "recovery_required" and not value.get("terminalSnapshot")
                and value.get("outcome") == "unknown" and not value.get("supersededBy")
                and ex.get("lastMainThreadPumpAt", 0) > value.get("updatedAt", 0))

    async def queue_cleanup(self, target_type: str = "", target_id: str = "", action: str = "",
                            reason: str = "", dry_run: bool = True, confirm_token: str = "",
                            expected_project_path: str = ""):
        request = new_id("cleanup")
        if type(dry_run) is not bool or any(not isinstance(v, str) for v in (
            target_type, target_id, action, reason, confirm_token, expected_project_path
        )):
            await notice(self, request, "", "", "", "failed", "参数无效", "", "QUEUE_INVALID_ARGUMENTS")
            return fail(request, "QUEUE_INVALID_ARGUMENTS", "Expected strings and a boolean dryRun.")
        if dry_run and not target_type and not target_id:
            return await self.queue_inventory()
        dispatched = False
        async def reject(code, message):
            await notice(self, request, target_type, target_id, action, "failed", message, reason, code)
            return fail(request, code, message, {"dispatchAttempted": dispatched})
        try:
            project = self._queue_project()
            if expected_project_path and not same_path(project, expected_project_path):
                return await reject("QUEUE_PROJECT_MISMATCH", "Target project changed.")
            if not dry_run and not expected_project_path:
                return await reject("QUEUE_PROJECT_REQUIRED", "Apply requires the preview project identity.")
            if not target_id or not reason.strip() or len(reason) > 256:
                return await reject("QUEUE_TARGET_REQUIRED", "Exact target and a short reason are required.")
            if (target_type, action) not in {("Task", "cancel"), ("Task", "cleanup"), ("Test", "cancel"),
                                          ("Test", "cleanup"), ("Operation", "cancel"),
                                          ("Capture", "stop"), ("WriteBatch", "release")}:
                return await reject("QUEUE_CLEANUP_UNSUPPORTED", "No safe adapter for this target/action; records were not removed.")
            value = await self._queue_target(target_type, target_id)
            if value is None:
                return await reject("QUEUE_TARGET_NOT_FOUND", "Exact target not found in this project.")
            signature = fingerprint([project, target_type, target_id, action, reason, value])
            previews = self.__dict__.setdefault("_queue_previews", {})
            now = time.monotonic()
            for key in list(previews):
                if previews[key][0] < now:
                    del previews[key]
            if dry_run:
                if len(previews) >= 128:
                    return await reject("QUEUE_PREVIEW_LIMIT", "Too many outstanding previews; wait for expiration.")
                token = secrets.token_urlsafe(32)
                previews[token] = (now + 120, signature)
                # No raw state, job parameters, or owner secrets in the public preview.
                return ok(request, dict(dryRun=True, projectPath=project, target=item(target_type, target_id, value, action),
                                        confirmToken=token, expiresInSeconds=120, allowed=authorization(project)[0]))
            allowed, denial = authorization(project)
            if not allowed:
                return await reject(denial, "AI queue cleanup is disabled or configuration is invalid.")
            preview = previews.pop(confirm_token, None)  # one shot, including uncertain dispatch
            if not preview or preview[0] < now or preview[1] != signature:
                return await reject("QUEUE_PREVIEW_CHANGED", "Preview expired, consumed, or target changed; nothing dispatched.")
            if not self.server.is_ready():
                return await reject("UNITY_NOT_CONNECTED", "结果未确认：Unity 断连，未发送清理。")
            await notice(self, request, target_type, target_id, action, "started",
                         "开始备份并解除历史阻断" if target_type == "WriteBatch" else "开始取消或停止", reason)
            # Logging crosses an await boundary; recheck the target and grant immediately before dispatch.
            if await self._queue_target(target_type, target_id) != value or authorization(project)[0] is not True:
                return await reject("QUEUE_TARGET_CHANGED", "Target or authorization changed before dispatch.")
            if not same_path(self._queue_project(), project):
                return await reject("QUEUE_PROJECT_MISMATCH", "Project changed before dispatch.")
            if not self.server.is_ready():
                return await reject("UNITY_NOT_CONNECTED", "结果未确认：Unity 已断连，未发送清理。")
            if value.get("disposition") or (value.get("terminal") or value.get("endedAt")) and not value.get("cleanupPending") or (
                target_type == "Capture" and value.get("active") is False
            ) or (
                target_type == "Test" and value.get("resultAuthoritative") is True
                and value.get("cleanupSucceeded") is True and value.get("cleanupPending") is False
                and value.get("status") in {"completed", "failed", "aborted", "no_tests"}
            ):
                await notice(self, request, target_type, target_id, action, "noop", "已结束，无需操作", reason)
                return ok(request, dict(status="noop", terminal=True, targetId=target_id))
            context = AUDIT.set((request, target_type, target_id, action, reason))
            guard = QUEUE_GUARD.set((target_id, value, project) if target_type == "Operation" else None)
            try:
                if target_type == "WriteBatch":
                    if not self._queue_release_safe(value):
                        return await reject("QUEUE_EXECUTION_NOT_EXCLUDED", "Cannot exclude pending execution; original evidence retained.")
                    disposition = self.server.state.dispose_write_batch(target_id, value, reason=safe_text(reason), request_id=request)
                    response = ok(request, dict(status="released", terminal=True, outcome=value["outcome"],
                                                originalTerminal=False, disposition=disposition, targetId=target_id))
                elif target_type == "Task":
                    cancel_before_start = action == "cancel" and value.get("startIntentSent") is False
                    if not value.get("durable") or not (value.get("runGuid") or cancel_before_start):
                        return await reject("QUEUE_CLEANUP_UNSUPPORTED", "Task has no established test run; observer and record retained.")
                    dispatched = True
                    response = await (self.task_cancel(target_id) if action == "cancel"
                                      else self.test_force_cleanup(run_guid=value["runGuid"]))
                    if action == "cleanup" and response.ok:
                        # Reuse the existing evidence-only observer to clear the Task
                        # only when its original run confirms terminal cleanup.
                        response = await self.task_status(target_id)
                elif target_type == "Test":
                    dispatched = True
                    response = await (self.test_cancel(run_guid=target_id) if action == "cancel"
                                      else self.test_force_cleanup(run_guid=target_id))
                elif target_type == "Operation":
                    dispatched = True
                    response = await self.operation_cancel(target_id)
                else:
                    dispatched = True
                    response = await self.console_capture_stop(session_id=target_id, force_stop=True)
                phase, result, code = outcome(response)
                if target_type == "WriteBatch" and response.ok:
                    result = "已解除阻断；原执行结果仍为 unknown"
                await notice(self, request, target_type, target_id, action, phase, result, reason, code)
                if isinstance(response.data, dict):
                    response.data["queueCleanupRequestId"] = request
                response.request_id = request
                return response
            finally:
                QUEUE_GUARD.reset(guard)
                AUDIT.reset(context)
        except (Exception, asyncio.CancelledError):
            if dispatched:
                await notice(self, request, target_type, target_id, action, "unconfirmed",
                             "结果未确认", reason, "QUEUE_RESPONSE_UNKNOWN")
                return fail(request, "QUEUE_RESPONSE_UNKNOWN", "结果未确认；不要重放操作。",
                            {"dispatchAttempted": True, "targetId": target_id})
            return await reject("QUEUE_CLEANUP_FAILED", "清理校验、备份或处置失败；请检查原目标记录。")
