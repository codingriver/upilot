from __future__ import annotations


def main_thread_observation(execution: dict, active_session_id: str, now: int) -> dict:
    """Interpret a pump timestamp only from a fresh, same-session sample.

    Editor readiness is deliberately separate: a real stalled main thread can
    cause a newly received Bridge snapshot to be non-authoritative.
    """
    received_at = int(execution.get("receivedAt") or 0)
    pump_at = int(execution.get("lastMainThreadPumpAt") or 0)
    snapshot_age = max(0, now - received_at) if received_at else None
    pump_age = max(0, now - pump_at) if pump_at else None
    not_pumping = None
    if not active_session_id or execution.get("sessionId") != active_session_id or not execution.get("producerEpoch"):
        reason = "identity_transition_or_missing"
    elif execution.get("unityConnected") is not True:
        reason = "unity_not_connected"
    elif snapshot_age is None or snapshot_age > 5000:
        reason = "editor_snapshot_not_fresh"
    elif pump_age is None:
        reason = "main_thread_pump_missing"
    else:
        not_pumping = pump_age > 10000
        reason = "main_thread_pump_stale" if not_pumping else "fresh_same_identity_evidence"
    return {"notPumping": not_pumping, "reason": reason,
            "snapshotAgeMs": snapshot_age, "pumpAgeMs": pump_age}
