"""Frozen pre-suspension admission fixture for historical-operation regressions.

Only fake services under tests use this to construct pre-existing accepted/uncertain
records. Production start/validate are NOT overridden or re-enabled. No real Unity,
Capture or business call is dispatched; those are provided by each isolated fake.
"""
import copy
import json
import math
import secrets
import time

from upilot_mcp.domain.task_service import _is_terminal_status, _is_success_status, _operation_cleanup_pending
from upilot_mcp.models import ToolResponse
from upilot_mcp.protocol import new_id, now_ms
from upilot_mcp.responses import fail, ok
from upilot_mcp.tool_registry import REGISTRY


class LegacyOperationFixture:
    async def validate_legacy_spec(
        self, job_spec: dict | None, inspect_reflection: bool = True, strict_tool_registry: bool = True,
    ) -> ToolResponse:
        request_id = new_id("req")
        errors: list[dict] = []
        warnings: list[dict] = []
        if not isinstance(job_spec, dict):
            return fail(request_id, "INVALID_JOB_SPEC", "jobSpec must be an object.", {
                "valid": False, "errors": [{"path": "jobSpec", "code": "type", "message": "Expected an object."}]
            })

        job_spec = copy.deepcopy(job_spec)
        normalized = dict(job_spec)
        step_plan = job_spec.get("stepPlan")
        has_step_plan = "stepPlan" in job_spec
        step_budget = 0.0
        if has_step_plan:
            if any(field in job_spec for field in ("startCall", "statusCall", "cancelCall")):
                errors.append({"path": "stepPlan", "code": "STEP_PLAN_CALLS_CONFLICT",
                               "message": "stepPlan and handwritten calls are mutually exclusive."})
            if not isinstance(step_plan, dict):
                errors.append({"path": "stepPlan", "code": "STEP_PLAN_INVALID", "message": "Expected a version 1 plan object."})
            else:
                capture = job_spec.get("consoleCapture")
                items = step_plan.get("steps")
                plan_capture = isinstance(items, list) and any(
                    isinstance(item, dict) and item.get("stepId") == "upilot.console_capture_start" for item in items)
                operation_capture = isinstance(capture, dict) and capture.get("enabled") is True
                if plan_capture and (not isinstance(capture, dict) or capture.get("enabled") is not False):
                    errors.append({"path": "consoleCapture.enabled", "code": "STEP_CAPTURE_OWNERSHIP_CONFLICT",
                                   "message": "Plan-owned Capture requires consoleCapture.enabled=false."})
                if step_plan.get("logPolicy") and not operation_capture and not plan_capture:
                    errors.append({"path": "consoleCapture.enabled", "code": "STEP_CAPTURE_REQUIRED",
                                   "message": "A step logPolicy requires one plan-owned or Operation-owned Capture."})
                # Unity owns discovery and per-step argument validation. Never create Capture here.
                try:
                    plan_json = json.dumps(step_plan, ensure_ascii=False, allow_nan=False)
                except (TypeError, ValueError):
                    return fail(request_id, "INVALID_JOB_SPEC", "stepPlan must contain finite JSON data.", {
                        "valid": False, "errors": errors + [{"path": "stepPlan", "code": "STEP_PLAN_INVALID",
                                                          "message": "Non-finite or non-JSON values are unsupported."}]
                    })
                response = await self.dispatcher.call(request_id, "automation.steps.validate",
                                                      {"planJson": plan_json})
                data = response.data if isinstance(response.data, dict) else {}
                if not response.ok:
                    errors.append({"path": "stepPlan", "code": response.error.code if response.error else "STEP_VALIDATION_FAILED",
                                   "message": response.error.message if response.error else "Step validation failed."})
                elif data.get("ok") is not True:
                    errors.extend({"path": f"stepPlan.steps[{item.get('index', -1)}]",
                                   "code": item.get("code", "STEP_PLAN_INVALID"), "message": item.get("message", "")}
                                  for item in data.get("diagnostics", []) if item.get("severity", "error") == "error")
                    if not errors:
                        errors.append({"path": "stepPlan", "code": "STEP_PLAN_INVALID", "message": "Plan validation failed."})
                else:
                    step_budget = float(data.get("budgetSeconds") or 0)
                    if not math.isfinite(step_budget) or step_budget <= 0:
                        errors.append({"path": "stepPlan", "code": "STEP_BUDGET_INVALID", "message": "Bridge returned no valid plan budget."})
        if not isinstance(job_spec.get("failOnUnexpectedPlayModeExit", False), bool):
            errors.append({"path": "failOnUnexpectedPlayModeExit", "code": "type", "message": "Expected a boolean."})
        normalized["displayName"] = str(job_spec.get("displayName") or "Unity operation")
        normalized["timeoutSec"] = job_spec.get("timeoutSec", max(300, step_budget))
        normalized["pollIntervalSec"] = job_spec.get("pollIntervalSec", 3)
        cleanup = job_spec.get("cleanup", {})
        if not isinstance(cleanup, dict):
            errors.append({"path": "cleanup", "code": "type", "message": "Expected an object."})
        else:
            cleanup = dict(cleanup)
            cleanup.setdefault("requireEditMode", False)
            cleanup.setdefault("timeoutSec", 30)
            if not isinstance(cleanup["requireEditMode"], bool):
                errors.append({"path": "cleanup.requireEditMode", "code": "type", "message": "Expected a boolean."})
            value = cleanup["timeoutSec"]
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                errors.append({"path": "cleanup.timeoutSec", "code": "range", "message": "Expected a finite positive number."})
            normalized["cleanup"] = cleanup
            if cleanup.get("requireEditMode") is True:
                normalized["_editorVerificationExpected"] = (
                    "After business terminal, cleanup verifies authoritative "
                    "EditMode readiness before declaring editorTerminal=true."
                )
        for field in ("timeoutSec", "pollIntervalSec"):
            value = normalized[field]
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                errors.append({"path": field, "code": "range", "message": "Expected a finite positive number."})
            elif field == "timeoutSec" and has_step_plan and value < step_budget:
                errors.append({"path": field, "code": "STEP_BUDGET_TOO_SMALL",
                               "message": f"Operation timeout must cover the plan, cleanup and evidence budget ({step_budget} seconds)."})

        for field, required in (("startCall", True), ("statusCall", True), ("cancelCall", False)):
            if has_step_plan:
                continue
            call = job_spec.get(field)
            if call is None and not required:
                continue
            if not isinstance(call, dict):
                errors.append({"path": field, "code": "type", "message": f"{field} must be an object."})
                continue
            await self._validate_operation_call(
                field, call, errors, warnings, inspect_reflection, strict_tool_registry,
            )

        mapping = job_spec.get("terminalStatusMapping")
        if mapping is not None and not isinstance(mapping, dict):
            errors.append({"path": "terminalStatusMapping", "code": "type", "message": "terminalStatusMapping must be an object."})
        elif isinstance(mapping, dict):
            for key, value in mapping.items():
                if not isinstance(value, (str, list, tuple)):
                    errors.append({"path": f"terminalStatusMapping.{key}", "code": "type", "message": "Status mapping values must be a string or array."})

        rules = job_spec.get("artifactRules")
        if rules is not None and not isinstance(rules, dict):
            errors.append({"path": "artifactRules", "code": "type", "message": "artifactRules must be an object."})
        elif isinstance(rules, dict):
            fields = rules.get("fromStatusFields")
            if fields is not None and (not isinstance(fields, list) or not all(isinstance(item, str) for item in fields)):
                errors.append({"path": "artifactRules.fromStatusFields", "code": "type", "message": "fromStatusFields must be an array of field names."})
            field_kinds = rules.get("fieldKinds")
            if field_kinds is not None and (not isinstance(field_kinds, dict) or not all(
                    isinstance(key, str) and str(value).lower() in {"file", "metadata", "sha256", "bytes"}
                    for key, value in field_kinds.items())):
                errors.append({"path": "artifactRules.fieldKinds", "code": "type", "message": "fieldKinds must map field names to file, metadata, sha256, or bytes."})
            elif field_kinds is None:
                rules = dict(rules)
                rules["_fieldKindsDefault"] = "file for absolute/relative paths, metadata otherwise"
                normalized["artifactRules"] = rules

        for field in ("resultPath", "statusPath", "phasePath", "errorPath", "detailPath", "progressPath", "failureSignaturePath", "artifactsPath"):
            value = job_spec.get(field)
            if value is not None and (not isinstance(value, str) or not value.strip()):
                errors.append({"path": field, "code": "type", "message": f"{field} must be a non-empty path string."})

        valid = not errors
        data = {
            "valid": valid,
            "normalizedJobSpec": normalized,
            "errors": errors,
            "warnings": warnings,
            "nextAction": "Call unity_operation_start with normalizedJobSpec." if valid else "Fix the reported jobSpec fields and validate again.",
        }
        return ok(request_id, data) if valid else fail(request_id, "INVALID_JOB_SPEC", "jobSpec validation failed.", data)

    async def _validate_operation_call(
        self, field: str, call: dict, errors: list[dict], warnings: list[dict], inspect_reflection: bool,
        strict_tool_registry: bool,
    ) -> None:
        kind = str(call.get("kind") or call.get("type") or "").strip().lower()
        if kind not in {"reflection", "mcp", "tool", "mcp_tool", "menu", "native", "route", "bridge"}:
            errors.append({"path": f"{field}.kind", "code": "unsupported", "message": f"Unsupported operation call kind: {kind or '(empty)'}"})
            return
        self._validate_operation_placeholders(field, call, errors)
        if kind == "reflection":
            type_name = str(call.get("typeName") or call.get("type_name") or "").strip()
            method_name = str(call.get("methodName") or call.get("method_name") or "").strip()
            if not type_name:
                errors.append({"path": f"{field}.typeName", "code": "required", "message": "typeName is required."})
            if not method_name:
                errors.append({"path": f"{field}.methodName", "code": "required", "message": "methodName is required."})
            parameters = call.get("parameters", [])
            if not isinstance(parameters, list):
                errors.append({"path": f"{field}.parameters", "code": "type", "message": "parameters must be an array."})
            if inspect_reflection and type_name and method_name:
                result = await self.reflection_find(type_name=type_name, method_name=method_name)
                if not result.ok:
                    errors.append({"path": field, "code": "reflection_not_found", "message": result.error.message if result.error else "Reflection entry point was not found."})
        elif kind in {"mcp", "tool", "mcp_tool"}:
            tool_name = str(call.get("toolName") or call.get("name") or "").strip()
            if not tool_name:
                errors.append({"path": f"{field}.toolName", "code": "required", "message": "toolName is required."})
            elif strict_tool_registry and REGISTRY.resolve(tool_name) is None:
                errors.append({"path": f"{field}.toolName", "code": "tool_not_found", "message": f"Registered tool not found: {tool_name}"})
            tool_args = call.get("toolArgs", call.get("args", call.get("arguments", {})))
            if not isinstance(tool_args, dict):
                errors.append({"path": f"{field}.toolArgs", "code": "type", "message": "toolArgs must be an object."})
        elif kind == "menu" and not str(call.get("commandName") or call.get("menuPath") or "").strip():
            errors.append({"path": f"{field}.commandName", "code": "required", "message": "commandName or menuPath is required."})
        elif kind in {"native", "route", "bridge"}:
            if not str(call.get("route") or call.get("command") or "").strip():
                errors.append({"path": f"{field}.route", "code": "required", "message": "route or command is required."})
            if not isinstance(call.get("payload", {}), dict):
                errors.append({"path": f"{field}.payload", "code": "type", "message": "payload must be an object."})

    @staticmethod
    def _validate_operation_placeholders(path: str, value: object, errors: list[dict]) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                LegacyOperationFixture._validate_operation_placeholders(f"{path}.{key}", child, errors)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                LegacyOperationFixture._validate_operation_placeholders(f"{path}[{index}]", child, errors)
        elif isinstance(value, str) and "${" in value:
            if not (value.startswith("${") and value.endswith("}")):
                errors.append({"path": path, "code": "placeholder_format", "message": "Placeholders must occupy the complete string value."})
                return
            parts = value[2:-1].strip().split(".")
            if len(parts) < 2 or parts[0] not in {"start", "status", "operation"} or any(not part for part in parts):
                errors.append({"path": path, "code": "placeholder_path", "message": f"Unsupported placeholder: {value}"})

    async def seed_legacy_operation(self, job_spec: dict | None) -> ToolResponse:
        self._recover_operations()
        request_id = new_id("req")
        validation = await self.validate_legacy_spec(
            job_spec, inspect_reflection=False, strict_tool_registry=False,
        )
        if not validation.ok:
            detail = validation.error.detail if validation.error else {}
            return fail(request_id, "INVALID_JOB_SPEC", "jobSpec validation failed.", detail)
        job_spec = dict((validation.data or {}).get("normalizedJobSpec") or job_spec or {})
        if "stepPlan" in job_spec:
            # Materialize only after validation. JSON text is opaque to placeholder expansion.
            job_spec["startCall"] = {"kind": "bridge", "route": "automation.steps.start", "payload": {
                "planJson": json.dumps(job_spec["stepPlan"], ensure_ascii=False, allow_nan=False),
                "operationId": "${operation.operationId}",
            }}
            for field, route in (("statusCall", "state"), ("cancelCall", "cancel")):
                job_spec[field] = {"kind": "bridge", "route": "automation.steps." + route, "payload": {
                    "operationId": "${operation.operationId}", "runId": "${start.runId}",
                }}
            job_spec["terminalStatusMapping"] = {
                "success": ["Succeeded", "SucceededWithWarnings"],
                "failure": ["Failed"], "canceled": ["Canceled"], "timeout": ["TimedOut"],
            }
        start_call = job_spec["startCall"]

        operation_id = new_id("op")
        now = now_ms()
        timeout_sec = float(job_spec.get("timeoutSec") or 300)
        state = {
            "projectPath": str(self._project_root()),
            "durable": True,
            "operationId": operation_id,
            "displayName": str(job_spec.get("displayName") or operation_id),
            "jobSpec": job_spec,
            "status": "Starting",
            "phase": "Starting",
            "error": "",
            "detail": "",
            "progress": 0,
            "failureSignature": "",
            "repeatFailure": False,
            "startAttemptCount": 0,
            "cancelRequested": False,
            "cancelAccepted": False,
            "cancelRequestedAt": 0,
            "cancelAttemptCount": 0,
            "cleanupPending": False,
            "editorVerification": "not_requested",
            "startedAt": now,
            "updatedAt": now,
            "endedAt": 0,
            "timeoutSec": timeout_sec,
            "pollIntervalSec": float(job_spec.get("pollIntervalSec") or 3),
            "lastStatusData": {},
            "lastStatusAt": 0,
            "changes": [],
            "milestones": [],
            "artifacts": {},
            "consoleCapture": {},
            "timing": {
                "totalWallMs": 0,
                "mcpQueueMs": 0,
                "bridgeMs": 0,
                "unityMainThreadMs": 0,
                "projectElapsedMs": 0,
                "agentPollGapMs": 0,
                "artifactReadMs": 0,
            },
            "_startedMono": time.monotonic(),
            "_lastPollMono": 0.0,
        }
        self._operations[operation_id] = state
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))

        capture = job_spec.get("consoleCapture") if isinstance(job_spec.get("consoleCapture"), dict) else {}
        if capture.get("enabled"):
            state["consoleCapture"].update({
                "ownerId": operation_id,
                "ownerToken": secrets.token_urlsafe(32),
                "requestKey": operation_id,
            })
            state["captureIntentSent"] = True
            if not self._save_operation(state):
                return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
            try:
                capture_result = await self._start_owned_operation_capture(
                    state,
                    title=str(capture.get("title") or state["displayName"]),
                    path=str(capture.get("path") or ""),
                    include_stack_trace=bool(capture.get("includeStackTrace", True)),
                    exclude_upilot=bool(capture.get("excludeUPilot", True)),
                    clear_unity_console=bool(capture.get("clearUnityConsole", False)),
                )
            except Exception as exc:
                state.update(status="RecoveryRequired", phase="capture_identity_unknown",
                             error=str(exc), cleanupPending=True,
                             nextAction="Inspect capture sessions for the original start intent; business was not started. Do not replay capture start.")
                self._save_operation(state)
                return fail(request_id, "OPERATION_CAPTURE_START_UNKNOWN", str(exc), self._public_operation_state(state))
            state["consoleCapture"]["start"] = self._tool_response_summary(capture_result)
            if capture_result.ok and isinstance(capture_result.data, dict):
                session_data = capture_result.data.get("session") if isinstance(capture_result.data.get("session"), dict) else {}
                session_data = self._redact_operation_secrets(session_data)
                state["consoleCapture"]["sessionId"] = str(
                    capture_result.data.get("sessionId") or session_data.get("sessionId") or ""
                )
                state["consoleCapture"]["nextSequence"] = int(
                    capture_result.data.get("nextSequence") or session_data.get("nextSequence") or -1
                )
                state["consoleCapture"]["session"] = session_data
            if not capture_result.ok or not state["consoleCapture"].get("sessionId"):
                state.update(status="RecoveryRequired", phase="capture_identity_unknown", cleanupPending=True,
                             error="Required console capture did not establish a session; business was not started.",
                             failureSignature="OperationCaptureStartFailed",
                             nextAction="Inspect original capture intent and active sessions; do not replay capture start.")
                self._save_operation(state)
                return fail(request_id, "OPERATION_CAPTURE_START_FAILED", state["error"], self._public_operation_state(state))

        if "stepPlan" in job_spec:
            start_call["payload"]["captureSessionId"] = state["consoleCapture"].get("sessionId", "")
        state["startIntentSent"] = True
        state["startAttemptCount"] = int(state.get("startAttemptCount") or 0) + 1
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        try:
            start_result = await self._operation_invoke(self._resolve_operation_call(start_call, state), operation_id)
        except Exception as exc:
            state.update(status="RecoveryRequired", phase="start_result_unknown", error=str(exc),
                         nextAction="Inspect original operation evidence; start was not replayed.")
            self._save_operation(state)
            return fail(request_id, "OPERATION_START_UNKNOWN", str(exc), self._public_operation_state(state))
        self._accumulate_timing(state, start_result)
        state["startResult"] = self._tool_response_summary(start_result)
        if not start_result.ok:
            state.update(status="RecoveryRequired", phase="start_result_unknown",
                         error=start_result.error.message if start_result.error else "startCall failed",
                         nextAction="A failed tool response does not prove that business never started. Inspect original start evidence; do not replay.")
            self._save_operation(state)
            return fail(request_id, "OPERATION_START_UNKNOWN", state["error"], self._public_operation_state(state))

        payload, payload_error, payload_diagnostic = self._operation_adapt_payload(start_result, start_call, job_spec)
        if payload_error:
            state["parseDiagnostic"] = payload_diagnostic
            state["status"] = "RecoveryRequired"
            state["phase"] = "StartResultInvalid"
            state["error"] = payload_error
            state["failureSignature"] = "OperationResultInvalid"
            state["nextAction"] = "Inspect the original start response; business may have started. Do not replay start."
            self._finalize_operation_timing(state)
            self._save_operation(state)
            return fail(request_id, "OPERATION_RESULT_INVALID", payload_error, self._public_operation_state(state))
        if payload:
            state["startData"] = payload
            self._merge_operation_status(state, payload)
        state["resolvedStatusCall"] = self._resolve_operation_call(job_spec["statusCall"], state)
        status_spec = json.dumps(job_spec["statusCall"], sort_keys=True)
        state["recoveryIdentityEstablished"] = bool(
            "${start." in status_spec or "${operation.operationId}" in status_spec
        ) and "${" not in json.dumps(state["resolvedStatusCall"], sort_keys=True)
        state["startEstablished"] = True
        if self._operation_call_has_missing_identity(job_spec["statusCall"], state["resolvedStatusCall"]):
            state.update(status="RecoveryRequired", phase="status_identity_unknown", endedAt=0,
                         nextAction="The original start did not resolve status identity. Inspect its response; do not replay start or query an unspecified job.")
            self._save_operation(state)
            return ok(request_id, self._public_operation_state(state))
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        mapping = job_spec.get("terminalStatusMapping")
        if _is_terminal_status(str(state["status"]), mapping):
            state["businessCleanupPending"] = _operation_cleanup_pending(payload)
            if _is_success_status(str(state["status"]), mapping):
                state["status"] = "Succeeded"
            elif str(state["status"]).strip().lower() in {"canceled", "cancelled", "aborted"}:
                state["status"] = "Canceled"
            elif str(state["status"]).strip().lower() in {"timeout", "timedout"}:
                state["status"] = "Timeout"
            else:
                state["status"] = "Failed"
            state["endedAt"] = now_ms()
            self._mark_repeat_failure(state)
            await self._operation_stop_console_capture(state)
            await self.operation_collect_artifacts(operation_id)
        else:
            state["status"] = "Running"
            state["phase"] = state.get("phase") or "Running"
        state["updatedAt"] = now_ms()
        self._finalize_operation_timing(state)
        if not self._save_operation(state):
            return fail(request_id, "OPERATION_PERSIST_FAILED", state["error"], self._public_operation_state(state))
        self._resume_operation_observer(state)
        return ok(request_id, self._public_operation_state(state))

