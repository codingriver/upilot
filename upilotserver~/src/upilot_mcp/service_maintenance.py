"""Read-only projection of the Unity-owned maintenance configuration and journal."""
from __future__ import annotations

import json
import os
import time
from pathlib import Path


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON field: {key}")
        result[key] = value
    return result


def same_path(left: object, right: object) -> bool:
    if not isinstance(left, (str, Path)) or not isinstance(right, (str, Path)) or not str(left) or not str(right):
        return False
    try:
        return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))
    except (OSError, ValueError):
        return False


def read_summary(project: Path | None, session=None, *, now_ms: int | None = None) -> dict:
    result = {
        "approved": False, "effectiveApproved": False, "restartTimeoutSeconds": 120,
        "configError": "", "unavailableReason": "SERVICE_MAINTENANCE_NOT_APPROVED",
        "nextAction": "Enable AI service maintenance in the Unity settings; never self-authorize.",
        "recordStatus": "missing", "latest": None, "deadlineExceeded": False,
        "terminalConfirmed": False, "expectedMaintenanceId": "",
        "expectedProjectPath": str(project or ""), "expectedServerProcessId": os.getpid(),
        "expectedBridgeSessionId": str(getattr(session, "session_id", "") or ""),
    }
    if project is None:
        result["unavailableReason"] = "PROJECT_IDENTITY_UNAVAILABLE"
        return result
    try:
        path = project / ".upilot" / "config.json"
        config = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=_unique_object) if path.exists() else {}
        section = config.get("aiServiceMaintenance", {})
        if not isinstance(section, dict):
            raise ValueError("aiServiceMaintenance must be an object.")
        timeout = section.get("restartTimeoutSeconds", 120)
        if type(timeout) is not int or not 30 <= timeout <= 600:
            raise ValueError("restartTimeoutSeconds must be an integer between 30 and 600.")
        if type(section.get("approved", False)) is not bool:
            raise ValueError("approved must be a boolean.")
        for field in ("projectPath", "approvedAtUtc"):
            if not isinstance(section.get(field, ""), str):
                raise ValueError(f"{field} must be a string.")
        result.update(
            approved=section.get("approved", False),
            effectiveApproved=section.get("approved", False) and same_path(section.get("projectPath"), project),
            restartTimeoutSeconds=timeout,
        )
        if result["effectiveApproved"]:
            result.update(unavailableReason="", nextAction="Inspect current EditMode and identities before requesting maintenance.")
    except (OSError, ValueError, TypeError, AttributeError) as ex:
        result.update(configError=str(ex)[:512], unavailableReason="SERVICE_MAINTENANCE_CONFIG_INVALID")

    path = project / "Library" / "UPilot" / "service-maintenance.json"
    if not path.exists():
        return result
    try:
        record = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=_unique_object)
        if not isinstance(record, dict) or record.get("schemaVersion") != 1 or not record.get("maintenanceId"):
            raise ValueError("Invalid maintenance journal.")
        if not same_path(record.get("projectPath"), project):
            raise ValueError("Maintenance journal belongs to a different project.")
        if record.get("status") not in {"accepted", "running", "succeeded", "failed", "timed_out"}:
            raise ValueError("Invalid maintenance status.")
        deadline = record.get("deadlineAtUtcMs")
        accepted = record.get("acceptedAtUtcMs")
        if type(deadline) is not int or type(accepted) is not int or deadline <= accepted:
            raise ValueError("Invalid maintenance deadline.")
        result.update(recordStatus="current", latest=record, expectedMaintenanceId=record["maintenanceId"])
        result["terminalConfirmed"] = record.get("status") in {"succeeded", "failed", "timed_out"}
        current = int(time.time() * 1000) if now_ms is None else now_ms
        result["deadlineExceeded"] = current >= deadline and not result["terminalConfirmed"]
        result["elapsedMs"] = max(0, (record.get("endedAtUtcMs") or current) - accepted)
        result["remainingMs"] = max(0, deadline - current) if not result["terminalConfirmed"] else 0
        if result["deadlineExceeded"]:
            result["observationStatus"] = "deadline_exceeded_unconfirmed"
            result["nextAction"] = "Deadline exceeded but Unity has not confirmed a terminal result. Inspect service identity; do not replay."
    except (OSError, ValueError, TypeError, AttributeError) as ex:
        result.update(recordStatus="corrupt", recordError=str(ex)[:512],
                      unavailableReason="SERVICE_RESTART_RECOVERY_REQUIRED",
                      nextAction="Inspect the persisted maintenance record; do not replay or replace it automatically.")
    return result
