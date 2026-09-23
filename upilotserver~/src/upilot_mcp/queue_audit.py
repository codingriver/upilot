"""Critical cancellation notifications. Never log credentials or change business results."""
from __future__ import annotations

import asyncio
from contextvars import ContextVar
from functools import wraps
import logging
import re

from .protocol import new_id

AUDIT = ContextVar("queue_cleanup_audit", default=None)
QUEUE_GUARD = ContextVar("queue_cleanup_target_guard", default=None)
logger = logging.getLogger("upilot.mcp")


def safe_text(value, limit=256):
    text = str(value or "").replace("\r", " ").replace("\n", " ")
    return re.sub(r"""(?i)(ownerToken|confirmToken|password|authorization|secret)["']?\s*[:=]\s*["']?\S+""",
                  r"\1=[REDACTED]", text)[:limit]


async def notice(service, request, kind, target, action, phase, result, reason="", code=""):
    events = service.__dict__.setdefault("_queue_audit_events", {})
    key = (request, kind, target, action, phase)
    if key in events:
        return
    events[key] = True
    if len(events) > 512:
        del events[next(iter(events))]
    payload = dict(requestId=safe_text(request), targetType=safe_text(kind),
                   targetId=safe_text(target), action=safe_text(action), phase=phase,
                   result=result, reason=safe_text(reason), errorCode=safe_text(code))
    error = phase in {"failed", "unconfirmed"}
    message = "[UPilot][QueueCleanup] " + " ".join(f"{k}={v}" for k, v in payload.items())
    logger.log(logging.ERROR if error else logging.INFO, message)
    server = getattr(service, "server", None)
    # No reconnect wait, business replay, or dependency on verbose logging.
    try:
        if not server or not callable(getattr(server, "is_ready", None)) or not server.is_ready():
            return
        await asyncio.wait_for(service.dispatcher.call(
            new_id("req"), "queue.cleanup.log", payload, timeout_ms=1000), timeout=1.1)
    except (Exception, asyncio.CancelledError):
        pass


def outcome(response):
    data = response.data if isinstance(response.data, dict) else {}
    error_detail = response.error.detail if response.error and isinstance(response.error.detail, dict) else {}
    cancel_result = error_detail.get("cancelResult") or data.get("cancelResult") or {}
    cancel_error = cancel_result.get("error") if isinstance(cancel_result, dict) else {}
    nested_code = cancel_error.get("code", "") if isinstance(cancel_error, dict) else ""
    if not response.ok:
        code = response.error.code if response.error else "CLEANUP_FAILED"
        unknown = any(s in (code + nested_code).upper() for s in ("TIMEOUT", "UNKNOWN", "DISCONNECT", "RECOVERY", "CANCELLED", "NOT_CONNECTED"))
        return ("unconfirmed" if unknown else "failed", "结果未确认" if unknown else "取消或停止失败", code)
    if data.get("cleanupStatus") == "failed" or data.get("cleanupErrors"):
        return "failed", "清理失败", "CLEANUP_FAILED"
    if data.get("status") == "RecoveryRequired":
        return "unconfirmed", "结果未确认", "CLEANUP_UNCONFIRMED"
    if data.get("completionSource") == "persistedManifest":
        return "noop", "已结束，无需操作", ""
    if data.get("cleanupPending") or data.get("cancelAccepted") and not (
        data.get("terminal") or data.get("endedAt") or data.get("cleanupSucceeded")
    ):
        return "accepted", "尚未停止", ""
    if data.get("terminal") or data.get("endedAt") or (
        data.get("resultAuthoritative") and data.get("cleanupSucceeded")
    ):
        return "completed", "已确认停止且清理完成", ""
    return "accepted", "尚未停止", ""


def audited(kind, action, identity):
    """Wrap actual domain cancellation, not just MCP envelopes."""
    def decorate(method):
        @wraps(method)
        async def invoke(self, *args, **kwargs):
            existing = AUDIT.get()
            if existing:
                return await method(self, *args, **kwargs)
            request = new_id("cleanup")
            raw_target = kwargs.get(identity, args[0] if args else "")
            target = raw_target if isinstance(raw_target, str) else "<invalid>"
            states = getattr(self, "_operations" if kind == "Operation" else "_async_tasks", {})
            before = states.get(target, {})
            already_terminal = bool(before.get("terminal") or before.get("endedAt")) and not before.get("cleanupPending")
            context = (request, kind, target, action, "direct")
            token = AUDIT.set(context)
            await notice(self, *context[:4], "started", "开始取消或停止", context[4])
            try:
                response = await method(self, *args, **kwargs)
                if not target and isinstance(response.data, dict):
                    target = str(response.data.get("runGuid") or response.data.get("sessionId") or "")
                    context = (request, kind, target, action, "direct")
                phase, result, code = outcome(response)
                if response.ok and already_terminal:
                    phase, result, code = "noop", "已结束，无需操作", ""
                await notice(self, *context[:4], phase, result, context[4], code)
                if kind == "Test" and phase == "accepted" and target:
                    pending = self.__dict__.setdefault("_queue_test_audits", {})
                    pending[target] = context
                    if len(pending) > 128:
                        del pending[next(iter(pending))]
                return response
            except BaseException:
                await notice(self, *context[:4], "unconfirmed", "结果未确认", context[4], "CLEANUP_RESPONSE_UNKNOWN")
                raise
            finally:
                AUDIT.reset(token)
        return invoke
    return decorate


def observed_test(method):
    @wraps(method)
    async def invoke(self, *args, **kwargs):
        response = await method(self, *args, **kwargs)
        data = response.data if isinstance(response.data, dict) else {}
        identity = data.get("runGuid")
        pending = self.__dict__.get("_queue_test_audits", {})
        context = pending.get(identity)
        if context:
            phase, result, code = outcome(response)
            if phase in {"failed", "unconfirmed", "completed", "noop"}:
                await notice(self, *context[:4], phase, result, context[4], code)
                pending.pop(identity, None)
        return response
    return invoke


def observe(service, kind, state):
    """Called at existing observer checkpoints; emit only terminal/recovery transitions."""
    if not state.get("cancelRequested"):
        return
    terminal = bool(state.get("terminal") or state.get("endedAt")) and not state.get("cleanupPending")
    unknown = state.get("status") == "RecoveryRequired" or state.get("cleanupStatus") == "failed"
    if not terminal and not unknown:
        return
    phase = "unconfirmed" if unknown else "completed"
    if state.get("_queueAuditPhase") == phase:
        return
    state["_queueAuditPhase"] = phase
    request = state.setdefault("queueAuditRequest", new_id("cleanup"))
    target = state.get("taskId") if kind == "Task" else state.get("operationId")
    try:
        asyncio.get_running_loop().create_task(notice(
            service, request, kind, target, "cancel", phase,
            "结果未确认" if unknown else "已确认停止且清理完成",
            state.get("queueAuditReason", "observer"), "CLEANUP_UNCONFIRMED" if unknown else ""))
    except RuntimeError:
        pass
