from __future__ import annotations

from typing import Any

from .protocol import new_id, now_ms


def _identity(state) -> dict[str, Any]:
    execution = state.execution_state()
    return {
        "sessionId": str(execution.get("sessionId") or ""),
        "producerEpoch": str(execution.get("producerEpoch") or ""),
        "domainGeneration": int(execution.get("domainGeneration") or 0),
        "snapshotId": str(execution.get("snapshotId") or ""),
        "observedAt": int(execution.get("observedAt") or 0),
    }


def _capture_manifest(data: dict[str, Any]) -> dict[str, Any] | None:
    sessions = data.get("sessions")
    if not isinstance(sessions, list):
        sessions = []
    active = [item for item in sessions if isinstance(item, dict) and bool(item.get("active"))]
    return active[0] if len(active) == 1 else None


async def begin_console_evidence(dispatcher, state) -> dict[str, Any]:
    identity = _identity(state)
    listed = await dispatcher.call(
        new_id("req"),
        "console.capture.list",
        {"count": 20, "includeActive": True},
        timeout_ms=60000,
    )
    manifest = _capture_manifest(dict(listed.data or {})) if listed.ok else None
    if manifest is not None:
        return {
            "source": "persistentCapture",
            "captureSessionId": str(manifest.get("sessionId") or ""),
            "startSequence": int(manifest.get("nextSequence") or 0),
            "endSequence": None,
            "coverage": "pending",
            "gapReason": "",
            "startedAt": now_ms(),
            "endedAt": 0,
            "startIdentity": identity,
            "endIdentity": {},
            "logs": [],
        }

    marked = await dispatcher.call(new_id("req"), "console.logs.mark", {})
    if marked.ok and isinstance(marked.data, dict):
        return {
            "source": "consoleDelta",
            "captureSessionId": "",
            "startSequence": int(marked.data.get("cursor") or 0),
            "endSequence": None,
            "coverage": "pending",
            "gapReason": "",
            "startedAt": now_ms(),
            "endedAt": 0,
            "startIdentity": identity,
            "endIdentity": {},
            "logs": [],
        }
    return {
        "source": "unavailable",
        "captureSessionId": "",
        "startSequence": None,
        "endSequence": None,
        "coverage": "unavailable",
        "gapReason": "console_boundary_unavailable",
        "startedAt": now_ms(),
        "endedAt": now_ms(),
        "startIdentity": identity,
        "endIdentity": identity,
        "logs": [],
    }


async def finish_console_evidence(dispatcher, state, evidence: dict[str, Any]) -> dict[str, Any]:
    result = dict(evidence or {})
    if not result or result.get("coverage") == "unavailable":
        return result
    end_identity = _identity(state)
    result.update({"endedAt": now_ms(), "endIdentity": end_identity})
    start_identity = result.get("startIdentity") if isinstance(result.get("startIdentity"), dict) else {}
    domain_changed = (
        start_identity.get("producerEpoch") != end_identity.get("producerEpoch")
        or start_identity.get("domainGeneration") != end_identity.get("domainGeneration")
    )

    if result.get("source") == "persistentCapture":
        session_id = str(result.get("captureSessionId") or "")
        status = await dispatcher.call(
            new_id("req"),
            "console.capture.status",
            {"sessionId": session_id},
            timeout_ms=60000,
        )
        data = dict(status.data or {}) if status.ok else {}
        manifest = data.get("session") if isinstance(data.get("session"), dict) else data
        same_session = str(manifest.get("sessionId") or session_id) == session_id
        result["endSequence"] = int(manifest.get("nextSequence") or result.get("startSequence") or 0)
        dropped = int(manifest.get("droppedCount") or 0)
        write_failed = bool(manifest.get("writeFailed") or manifest.get("error"))
        if not status.ok or not same_session:
            result.update(coverage="partial", gapReason="capture_boundary_unavailable")
        elif dropped:
            result.update(coverage="partial", gapReason="capture_dropped_records", droppedCount=dropped)
        elif write_failed:
            result.update(coverage="partial", gapReason="capture_write_failure")
        elif domain_changed:
            result.update(coverage="partial", gapReason="domain_reload_boundary")
        else:
            result.update(coverage="complete", gapReason="")
        return result

    cursor = int(result.get("startSequence") or 0)
    tail = await dispatcher.call(new_id("req"), "console.logs.tail", {
        "cursor": cursor,
        "count": 500,
        "includeStackTrace": True,
        "excludeUPilot": True,
        "newestFirst": False,
    })
    data = dict(tail.data or {}) if tail.ok else {}
    total = int(data.get("totalCount") or 0)
    result["endSequence"] = int(data.get("nextCursor") or total)
    result["logs"] = list(data.get("logs") or [])[:500]
    if not tail.ok:
        result.update(coverage="partial", gapReason="console_delta_unavailable")
    elif total < cursor:
        result.update(coverage="partial", gapReason="console_cleared")
    elif bool(data.get("truncated")):
        result.update(coverage="partial", gapReason="console_delta_truncated")
    elif domain_changed:
        result.update(coverage="partial", gapReason="domain_reload_boundary")
    else:
        result.update(coverage="complete", gapReason="")
    return result
