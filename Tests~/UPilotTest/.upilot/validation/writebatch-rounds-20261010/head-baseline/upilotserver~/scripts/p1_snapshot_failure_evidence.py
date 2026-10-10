"""V09 owned Snapshot failures; opt-in persistence denial, timeout or witnessed in-flight Reload."""
import argparse
import asyncio
from contextlib import contextmanager, ExitStack
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path
import re
import time
import uuid

from PIL import Image

from upilot_mcp.wire_ids import stringify_wire_ids

from p0_acceptance_call import CallPreflightError, _redact, _write_observation
from p0_reload_evidence import PROJECT, owned_path, require_status
from p1_request_origin_evidence import call, call_envelope, connect

ORIGIN = {"clientId": "owned-v09-failure", "chatId": "owned-v09-failure"}
FROZEN = ("snapshotId", "requestKey", "requestHash", "serviceLifecycleId", "startedAtUtcMs",
          "outputDirectory", "manifestPath", "completionPolicy", "request")
TERMINAL = (*FROZEN, "status", "phase", "terminal", "success", "cancelRequested", "endedAtUtcMs",
            "snapshotSequence", "targets", "failures", "artifacts", "persistenceStatus", "persistenceError",
            "manifestBytes", "manifestSha256")
ID = re.compile(r"snapshot-[0-9a-f]{32}")
CASES = (("missing-camera", "allOrNothing", "CAMERA_NOT_FOUND", "resolving"),
         ("gameview-edit", "bestEffort", "SNAPSHOT_GAMEVIEW_REQUIRES_PLAYMODE", "rendering"))


def check_admission(data, request, relative_directory):
    if (not ID.fullmatch(str(data.get("snapshotId") or "")) or
            data.get("requestKey") != request["requestKey"] or
            data.get("outputDirectory") != relative_directory or
            data.get("manifestPath") != relative_directory + "/manifest.json" or
            not re.fullmatch(r"[0-9a-f]{64}", str(data.get("requestHash") or "")) or
            not data.get("serviceLifecycleId") or type(data.get("startedAtUtcMs")) is not int or
            data["startedAtUtcMs"] <= 0 or data.get("completionPolicy") != request["completionPolicy"]):
        raise CallPreflightError("Snapshot admission identity, request or owned paths are unverified; never replay start.")
    submitted = data.get("request") or {}
    targets = submitted.get("targets") or []
    if (submitted.get("requestKey") != request["requestKey"] or len(targets) != 1 or
            any(targets[0].get(key) != value for key, value in request["targets"][0].items()) or
            submitted.get("completionPolicy") != request["completionPolicy"] or
            submitted.get("channels") != ["color"]):
        raise CallPreflightError("The Snapshot receipt does not contain the exact owned request.")
    return {key: data.get(key) for key in FROZEN}


def check_failed(data, baseline, case, *, manifest_failure=False, state_failure=False):
    if any(data.get(key) != baseline[key] for key in FROZEN):
        raise CallPreflightError("Original Snapshot identity, request or start time changed.")
    _, _, code, stage = case
    targets, failures = data.get("targets"), data.get("failures")
    if (data.get("terminal") is not True or data.get("status") != "failed" or data.get("phase") != "failed" or
            data.get("success") is not False or data.get("cancelRequested") is not False or
            type(data.get("endedAtUtcMs")) is not int or data["endedAtUtcMs"] < baseline["startedAtUtcMs"] or
            data.get("artifacts") != [] or not isinstance(targets, list) or len(targets) != 1 or
            targets[0].get("targetId") != case[0] or targets[0].get("status") != "failed" or
            targets[0].get("success") is not False or targets[0].get("artifactIds") != [] or
            not isinstance(failures, list) or len(failures) != 1 or
            failures[0].get("targetId") != case[0] or failures[0].get("code") != code or
            failures[0].get("stage") != stage):
        raise CallPreflightError("Exact expected failure or absence of accepted image artifacts is unverified.")
    if manifest_failure and state_failure:
        raise CallPreflightError("Select one exact persistence failure boundary.")
    expected_status, expected_error = (("unverified", "STATE_REPLACE_FAILED") if state_failure else
                                       (("unverified", "MANIFEST_REPLACE_FAILED") if manifest_failure else ("verified", "")))
    if (data.get("persistenceStatus") != expected_status or data.get("persistenceVerified") is not (not (manifest_failure or state_failure)) or
            data.get("persistenceError") != expected_error or data.get("persistenceSchemaVersion") != 2 or
            type(data.get("snapshotSequence")) is not int or data["snapshotSequence"] <= 0):
        raise CallPreflightError("Capture failure and persistence evidence must have their exact separate outcomes.")
    if (manifest_failure or state_failure) and (type(data.get("manifestBytes")) is not int or data["manifestBytes"] != 0 or
                             data.get("manifestSha256") != ""):
        raise CallPreflightError("An uncommitted manifest must not expose verified bytes or a hash.")



def check_unaccepted_pixels(data, baseline, camera_id, unity_pid):
    """Capture success is not a verified persistence/evidence receipt."""
    targets, artifacts = data.get("targets"), data.get("artifacts")
    if (any(data.get(key) != baseline[key] for key in FROZEN) or
            data.get("terminal") is not True or data.get("status") != "completed" or data.get("phase") != "completed" or
            data.get("success") is not True or data.get("cancelRequested") is not False or data.get("failures") != [] or
            type(data.get("endedAtUtcMs")) is not int or data["endedAtUtcMs"] < baseline["startedAtUtcMs"] or
            not isinstance(targets, list) or len(targets) != 1 or not isinstance(artifacts, list) or len(artifacts) != 1):
        raise CallPreflightError("Exact successful Camera capture and original terminal identity are unverified.")
    target, artifact = targets[0], artifacts[0]
    camera, provenance = target.get("camera") or {}, target.get("provenance") or {}
    if (target.get("targetId") != "camera-pixels" or target.get("kind") != "camera" or
            target.get("status") != "completed" or target.get("success") is not True or
            camera.get("instanceId") != camera_id or provenance.get("matchedInstanceId") != camera_id or
            provenance.get("unityProcessId") != unity_pid or provenance.get("pixelSourceVerified") is not True or
            provenance.get("degraded") is not False or provenance.get("occlusionSensitive") is not False or
            provenance.get("captureApi") != "Camera.Render(RenderTextureDescriptor)" or
            not re.fullmatch(r"artifact-[0-9a-f]{32}", str(artifact.get("artifactId") or "")) or
            target.get("artifactIds") != [artifact.get("artifactId")] or artifact.get("targetId") != "camera-pixels" or
            artifact.get("role") != "color" or artifact.get("mimeType") != "image/png" or
            artifact.get("encoding") != "rgba8" or artifact.get("units") != "" or
            type(artifact.get("width")) is not int or artifact["width"] != 32 or
            type(artifact.get("height")) is not int or artifact["height"] != 32 or
            artifact.get("acceptedAsEvidence") is not False):
        raise CallPreflightError("The exact Camera pixel source or unaccepted color artifact is unverified.")
    if (data.get("persistenceStatus") != "unverified" or data.get("persistenceError") != "MANIFEST_REPLACE_FAILED" or
            data.get("persistenceVerified") is not False or data.get("persistenceSchemaVersion") != 2 or
            type(data.get("snapshotSequence")) is not int or data["snapshotSequence"] <= 0 or
            type(data.get("manifestBytes")) is not int or data["manifestBytes"] != 0 or data.get("manifestSha256") != ""):
        raise CallPreflightError("Successful pixels must not override the failed manifest persistence receipt.")


def check_unaccepted_gameview_pixels(data, baseline, unity_pid, *, recovered=False):
    """One real Display 0 capture; pixel success never substitutes for persistence."""
    targets, artifacts = data.get("targets"), data.get("artifacts")
    if (any(data.get(key) != baseline[key] for key in FROZEN) or
            data.get("terminal") is not True or data.get("status") != "completed" or data.get("phase") != "completed" or
            data.get("success") is not True or data.get("cancelRequested") is not False or data.get("failures") != [] or
            type(data.get("endedAtUtcMs")) is not int or data["endedAtUtcMs"] < baseline["startedAtUtcMs"] or
            not isinstance(targets, list) or len(targets) != 1 or not isinstance(artifacts, list) or len(artifacts) != 1):
        raise CallPreflightError("Exact successful GameView capture and original terminal identity are unverified.")
    target, artifact = targets[0], artifacts[0]
    view, provenance = target.get("gameView") or {}, target.get("provenance") or {}
    if (target.get("targetId") != "gameview-state-pixels" or target.get("kind") != "gameView" or
            target.get("status") != "completed" or target.get("success") is not True or
            view.get("targetDisplay") != 0 or view.get("playMode") is not True or
            type(view.get("screenWidth")) is not int or view["screenWidth"] <= 0 or
            type(view.get("screenHeight")) is not int or view["screenHeight"] <= 0 or
            view.get("requestedWidth") != 32 or view.get("requestedHeight") != 32 or
            view.get("outputWidth") != 32 or view.get("outputHeight") != 32 or
            provenance.get("matchedInstanceId") != "display:0" or provenance.get("unityProcessId") != unity_pid or
            provenance.get("pixelSourceVerified") is not True or provenance.get("degraded") is not False or
            provenance.get("occlusionSensitive") is not False or
            provenance.get("captureApi") != "WaitForEndOfFrame+ScreenCapture.CaptureScreenshotIntoRenderTexture" or
            not re.fullmatch(r"artifact-[0-9a-f]{32}", str(artifact.get("artifactId") or "")) or
            target.get("artifactIds") != [artifact.get("artifactId")] or artifact.get("targetId") != target["targetId"] or
            artifact.get("role") != "color" or artifact.get("mimeType") != "image/png" or
            artifact.get("encoding") != "rgba8" or artifact.get("units") != "" or
            type(artifact.get("width")) is not int or artifact["width"] != 32 or
            type(artifact.get("height")) is not int or artifact["height"] != 32 or
            artifact.get("acceptedAsEvidence") is not False):
        raise CallPreflightError("Exact Display 0 provenance and unaccepted GameView artifact are unverified.")
    if (data.get("persistenceStatus") != ("verified" if recovered else "unverified") or
            data.get("persistenceError") != ("" if recovered else "STATE_REPLACE_FAILED") or
            data.get("persistenceVerified") is not recovered or data.get("persistenceSchemaVersion") != 2 or
            type(data.get("snapshotSequence")) is not int or data["snapshotSequence"] <= 0 or
            (recovered and data.get("persistenceRecovered") is not True) or
            (not recovered and (type(data.get("manifestBytes")) is not int or data["manifestBytes"] != 0 or
                                data.get("manifestSha256") != ""))):
        raise CallPreflightError("Successful pixels must not override the exact state persistence boundary.")


def verify_unaccepted_png(artifact, directory):
    path = owned_path(PROJECT / str(artifact.get("path") or ""))
    if path.parent != directory or path.suffix != ".png" or not path.is_file() or not 0 < path.stat().st_size <= 1048576:
        raise CallPreflightError("Color PNG must be a bounded regular file in the exact owned directory.")
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if (type(artifact.get("bytes")) is not int or artifact["bytes"] != len(raw) or artifact.get("sha256") != digest):
        raise CallPreflightError("Color PNG bytes/hash do not match the public unaccepted artifact.")
    with Image.open(BytesIO(raw)) as image:
        if image.format != "PNG" or image.size != (32, 32):
            raise CallPreflightError("Color artifact is not the exact requested 32x32 PNG.")
        image.load()  # Complete decode using the existing Pillow dependency; no visual correctness claim.
    return {"path": str(path), "bytes": len(raw), "sha256": digest, "width": 32, "height": 32,
            "decoded": True, "acceptedAsEvidence": False}

def read_json(path):
    path = owned_path(path)
    if not path.is_file() or not 0 < path.stat().st_size <= 1048576:
        raise CallPreflightError("Snapshot receipt must be a bounded existing regular file.")
    raw = path.read_bytes()
    return {"path": str(path), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}, json.loads(raw)


def verify_files(data, baseline, directory, *, pixel_artifact=None):
    manifest_path = owned_path(PROJECT / data["manifestPath"])
    if manifest_path != directory / "manifest.json":
        raise CallPreflightError("Manifest is outside this exact owned case directory.")
    manifest_file, manifest = read_json(manifest_path)
    state_file, state = read_json(PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (baseline["snapshotId"] + ".json"))
    if (type(data.get("manifestBytes")) is not int or data["manifestBytes"] != manifest_file["bytes"] or
            data.get("manifestSha256") != manifest_file["sha256"]):
        raise CallPreflightError("Public manifest bytes/hash do not match the actual file.")
    # The manifest deliberately cannot contain its final self-hash; the sidecar owns that receipt.
    if (manifest.get("persistenceStatus") != "pending" or manifest.get("manifestSha256") != "" or
            manifest.get("manifestBytes") != 0 or manifest.get("persistenceError") != ""):
        raise CallPreflightError("Unexpected manifest self-receipt shape.")
    business_fields = (*FROZEN, "status", "phase", "terminal", "success", "cancelRequested", "endedAtUtcMs",
                       "snapshotSequence", "targets", "failures", "artifacts", "persistenceSchemaVersion")
    # Public MCP stringifies only wire IDs. Keep raw byte/hash checks above;
    # compare the disk payload through that same existing public projection.
    public_manifest, public_state = stringify_wire_ids(manifest), stringify_wire_ids(state)
    if (any(public_manifest.get(key) != data.get(key) for key in business_fields) or
            any(public_state.get(key) != data.get(key) for key in (*business_fields, "persistenceStatus", "persistenceError",
                                                                 "manifestBytes", "manifestSha256"))):
        raise CallPreflightError("Manifest/state do not match the original public failed Snapshot.")
    pixel_file = verify_unaccepted_png(pixel_artifact, directory) if pixel_artifact is not None else None
    expected = {"manifest.json"} | ({Path(pixel_file["path"]).name} if pixel_file else set())
    if {path.name for path in directory.iterdir()} != expected:
        raise CallPreflightError("Snapshot wrote unexpected images or extra staging files.")
    return {"manifest": manifest_file, "state": state_file, "verified": True,
            **({"pixel": pixel_file} if pixel_file else {})}


@contextmanager
def hold_owned_state(baseline):
    """Deny replacement of only this received Snapshot's existing state; never write it."""
    if os.name != "nt" or not ID.fullmatch(str(baseline.get("snapshotId") or "")):
        raise CallPreflightError("An exact received owned Snapshot ID and Windows are required.")
    import ctypes
    from ctypes import wintypes
    path = owned_path(PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (baseline["snapshotId"] + ".json"))
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create, close = kernel.CreateFileW, kernel.CloseHandle
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close.argtypes, close.restype = [wintypes.HANDLE], wintypes.BOOL
    # GENERIC_READ / FILE_SHARE_READ / OPEN_EXISTING: no create, write, delete or root-directory mutation.
    handle = create(str(path), 0x80000000, 1, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value or handle is None:
        raise CallPreflightError("Cannot acquire the exact existing owned state read handle.")
    try:
        file, state = read_json(path)
        public = stringify_wire_ids(state)
        if (any(public.get(key) != baseline[key] for key in FROZEN) or state.get("terminal") is not False or
                state.get("persistenceStatus") != "verified" or state.get("persistenceError") != "" or
                type(state.get("snapshotSequence")) is not int or state["snapshotSequence"] <= 0):
            raise CallPreflightError("Existing state does not prove this exact owned nonterminal Snapshot.")
        yield {"file": file, "state": public, "replacementDenied": True}
    finally:
        if not close(handle):
            raise CallPreflightError("Owned state handle release is unverified.")


def verify_state_failure_files(data, baseline, directory, locked, *, pixel_artifact=None):
    manifest_path = owned_path(PROJECT / str(data.get("manifestPath") or ""))
    if manifest_path != directory / "manifest.json":
        raise CallPreflightError("Manifest must be inside the exact owned output directory.")
    manifest_file, manifest = read_json(manifest_path)
    state_file, state = read_json(PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (baseline["snapshotId"] + ".json"))
    if (state_file != locked["file"] or stringify_wire_ids(state) != locked["state"] or
            locked.get("replacementDenied") is not True or state.get("terminal") is not False):
        raise CallPreflightError("The denied replacement must leave the exact old state bytes unchanged.")
    pixel_file = verify_unaccepted_png(pixel_artifact, directory) if pixel_artifact is not None else None
    expected = {"manifest.json"} | ({Path(pixel_file["path"]).name} if pixel_file else set())
    public = stringify_wire_ids(manifest)
    business = (*FROZEN, "status", "phase", "terminal", "success", "cancelRequested", "endedAtUtcMs",
                "snapshotSequence", "targets", "failures", "artifacts", "persistenceSchemaVersion")
    if (any(public.get(key) != data.get(key) for key in business) or
            manifest.get("persistenceStatus") != "pending" or manifest.get("persistenceError") != "" or
            manifest.get("manifestSha256") != "" or manifest.get("manifestBytes") != 0 or
            data["snapshotSequence"] <= state["snapshotSequence"] or
            {path.name for path in directory.iterdir()} != expected or
            list(Path(state_file["path"]).parent.glob(baseline["snapshotId"] + ".json.*.tmp"))):
        raise CallPreflightError("New terminal manifest or exact state-staging cleanup is unverified.")
    return {"manifest": manifest_file, "oldState": state_file, "verified": False,
            "failureEvidenceVerified": True, "replacementDenied": True, **({"pixel": pixel_file} if pixel_file else {})}


def verify_manifest_failure_files(data, baseline, directory, *, pixel_artifact=None):
    """Verify real uncommitted staging, without repairing or promoting it."""
    manifest_path = owned_path(PROJECT / data["manifestPath"])
    if manifest_path != directory / "manifest.json" or not manifest_path.is_dir() or any(manifest_path.iterdir()):
        raise CallPreflightError("The exact owned empty manifest obstruction changed.")
    state_path = owned_path(PROJECT / "Library" / "UPilot" / "SnapshotJobs" / (baseline["snapshotId"] + ".json"))
    if state_path.exists():
        raise CallPreflightError("A failed manifest replacement unexpectedly published a state receipt.")
    pixel_file = verify_unaccepted_png(pixel_artifact, directory) if pixel_artifact is not None else None
    paths = sorted(path for path in directory.iterdir() if pixel_file is None or str(path) != pixel_file["path"])
    if not 2 <= len(paths) <= 33 or any(path != manifest_path and not re.fullmatch(r"manifest\.json\.[0-9a-f]{32}\.tmp", path.name)
                                      for path in paths):
        raise CallPreflightError("Expected only the owned obstruction and bounded manifest staging files, never an image.")
    fields = (*FROZEN, "status", "phase", "terminal", "success", "cancelRequested", "endedAtUtcMs",
              "snapshotSequence", "targets", "failures", "artifacts", "persistenceSchemaVersion")
    staging, terminal_staging = [], []
    for path in paths:
        if path == manifest_path:
            continue
        file, record = read_json(path)
        public = stringify_wire_ids(record)
        if (any(public.get(key) != baseline[key] for key in FROZEN) or record.get("persistenceStatus") != "pending" or
                record.get("persistenceError") != "" or record.get("manifestSha256") != "" or
                type(record.get("manifestBytes")) is not int or record["manifestBytes"] != 0 or
                record.get("persistenceSchemaVersion") != 2 or (pixel_artifact is None and record.get("artifacts") != [])):
            raise CallPreflightError("An uncommitted staging file has foreign identity or false persistence evidence.")
        if pixel_artifact is not None:
            staged_artifacts = public.get("artifacts")
            if staged_artifacts:
                # Staging precedes RejectArtifacts: only this bool may differ.
                # Pending .tmp files are never authoritative accepted evidence.
                if (len(staged_artifacts) != 1 or type(staged_artifacts[0].get("acceptedAsEvidence")) is not bool or
                        {**staged_artifacts[0], "acceptedAsEvidence": False} != pixel_artifact):
                    raise CallPreflightError("Staging color metadata differs from the actual unaccepted artifact.")
                public["artifacts"] = [{**staged_artifacts[0], "acceptedAsEvidence": False}]
            elif staged_artifacts != []:
                raise CallPreflightError("Staging artifact list is unverified.")
        staging.append(file)
        if public.get("terminal") is True and public.get("snapshotSequence") == data.get("snapshotSequence"):
            if any(public.get(key) != data.get(key) for key in fields):
                raise CallPreflightError("Terminal staging does not match the original public failed Snapshot.")
            terminal_staging.append(file)
    if len(terminal_staging) != 1:
        raise CallPreflightError("Exactly one matching terminal uncommitted manifest is required.")
    result = {"obstruction": str(manifest_path), "statePath": str(state_path), "stateExists": False,
            "staging": staging, "terminalStaging": terminal_staging[0], "verified": False,
            "failureEvidenceVerified": True}
    if pixel_file is not None:
        result["unacceptedPng"] = pixel_file
    return result


async def exercise(port, report, *, manifest_failure=False, pixels_camera_id=""):
    if pixels_camera_id and not re.fullmatch(r"[1-9][0-9]*", pixels_camera_id):
        raise CallPreflightError("An exact nonzero decimal Camera instanceId is required before any call.")
    manifest_failure = manifest_failure or bool(pixels_camera_id)
    deadline = asyncio.get_running_loop().time() + 90
    report.update(result="unknown", ownedSnapshotIds=[], cases=[])
    nonce = uuid.uuid4().hex
    async with asyncio.timeout_at(deadline):
        async with connect(port) as session:
            async def query(tool, arguments=None):
                if asyncio.get_running_loop().time() >= deadline:
                    raise CallPreflightError("Original observation budget expired; never renew it.")
                return await call(session, tool, arguments or {}, ORIGIN, report)

            async def inventory():
                queue = await query("unity_queue_cleanup")
                captures = await query("unity_console_capture_list", {"activeOnly": True})
                runner = await query("unity_test_status")
                if (queue.get("complete") is not True or queue.get("isStale") is not False or queue.get("items") != [] or
                        captures.get("activeCount") != 0 or runner.get("isRunning") is not False or
                        runner.get("cleanupPending") is True):
                    raise CallPreflightError("Fresh complete queue, Capture and Runner must be idle.")

            status_args = {"forceFresh": True, "includeCapabilities": False}
            identity = require_status(await query("unity_mcp_status", status_args))
            report["identityBefore"] = identity
            await inventory()
            ready = await query("unity_ensure_ready", {"timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual"})
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh manual EditMode readiness is required; no mode transitions are allowed.")
            cameras = await query("unity_camera_list")
            missing_name = "UPilot_V09_Missing_" + nonce
            if (not isinstance(cameras.get("cameras"), list) or cameras.get("count") != len(cameras["cameras"]) or
                    any(camera.get("name") == missing_name for camera in cameras["cameras"])):
                raise CallPreflightError("Cannot confirm that the owned camera selector is absent.")
            if pixels_camera_id:
                selected = [camera for camera in cameras["cameras"] if camera.get("instanceId") == pixels_camera_id]
                if len(selected) != 1 or selected[0].get("active") is not True or selected[0].get("enabled") is not True:
                    raise CallPreflightError("The exact active/enabled Camera is absent or ambiguous; no owned files or start created.")
                report["selectedCamera"] = selected[0]
            cases = (("camera-pixels", "allOrNothing", "MANIFEST_REPLACE_FAILED", "writing"),) if pixels_camera_id else (CASES[:1] if manifest_failure else CASES)
            for case in cases:
                if require_status(await query("unity_mcp_status", status_args)) != identity:
                    raise CallPreflightError("Service/domain identity changed before the owned start.")
                await inventory()
                relative = "Log/P0P1/" + ("v09-manifest-failure-" if manifest_failure else "v09-failure-") + nonce + "/" + case[0]
                directory = owned_path(PROJECT / relative)
                if directory.exists():
                    raise CallPreflightError("Owned output directory already exists; never overwrite it.")
                target = {"targetId": case[0], "kind": "camera" if case[0] in ("missing-camera", "camera-pixels") else "gameview", "width": 32, "height": 32}
                if target["kind"] == "camera":
                    if pixels_camera_id:
                        target["instanceId"] = pixels_camera_id
                    else:
                        target["cameraName"] = missing_name
                request = {"targets": [target], "channels": ["color"], "completionPolicy": case[1],
                           "outputDirectory": relative, "waitMs": 0, "requestKey": "v09-" + nonce + "-" + case[0]}
                observed = {"case": case[0], "expectedCode": case[2], "request": request}
                report["cases"].append(observed)
                baseline, terminal = None, None
                try:
                    if manifest_failure:
                        # Only a new, exact owned Log path. No permissions, hooks or shared files change.
                        obstruction = owned_path(directory / "manifest.json")
                        obstruction.mkdir(parents=True, exist_ok=False)
                        observed["manifestWriteObstruction"] = {"path": str(obstruction), "kind": "empty-owned-directory"}
                    receipt = await query("unity_snapshot_capture", request)  # exactly one start per case
                    baseline = check_admission(receipt, request, relative)
                    report["ownedSnapshotIds"].append(baseline["snapshotId"])
                    observed["admission"] = receipt
                    data = receipt
                    while data.get("terminal") is not True:
                        await asyncio.sleep(min(0.1, max(0, deadline - asyncio.get_running_loop().time())))
                        data = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                        if any(data.get(key) != baseline[key] for key in FROZEN):
                            raise CallPreflightError("Pending Snapshot identity changed.")
                    terminal = data
                    observed["terminal"] = terminal
                    def check(value):
                        if pixels_camera_id:
                            check_unaccepted_pixels(value, baseline, pixels_camera_id, identity["unityPid"])
                        else:
                            check_failed(value, baseline, case, manifest_failure=manifest_failure)

                    def verify(value, base, output):
                        if manifest_failure:
                            return verify_manifest_failure_files(value, base, output,
                                pixel_artifact=value["artifacts"][0] if pixels_camera_id else None)
                        return verify_files(value, base, output)

                    check(terminal)
                    files = verify(terminal, baseline, directory)
                    collected = await query("unity_snapshot_collect_artifacts", {"snapshotId": baseline["snapshotId"]})
                    again = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                    for value in (collected, again):
                        check(value)
                        if any(value.get(key) != terminal.get(key) for key in TERMINAL):
                            raise CallPreflightError("Collect/status changed the original terminal receipt.")
                    if verify(again, baseline, directory) != files:
                        raise CallPreflightError("Read-only public queries changed the persisted files.")
                    observed.update(files=files, collected=collected, repeatedStatus=again, result="observed")
                except BaseException as exc:
                    report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                                      else "Outcome unknown; raw exception omitted; never replay start."})
                    raise
                finally:
                    # Only observe a received verified owned identity; no cancel or shared-resource cleanup.
                    if baseline and terminal is None and asyncio.get_running_loop().time() < deadline:
                        try:
                            last = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                            observed["lastObservation"] = last
                            if last.get("snapshotId") != baseline["snapshotId"] or last.get("terminal") is not True:
                                report["cleanupError"] = {"message": "Original owned Snapshot terminal remains unverified."}
                        except BaseException as exc:
                            report["cleanupError"] = {"type": type(exc).__name__, "message": "Original owned Snapshot could not be observed."}
            report["identityAfter"] = require_status(await query("unity_mcp_status", status_args))
            if report["identityAfter"] != identity:
                raise CallPreflightError("Final project/service/domain identity changed.")
            await inventory()
            report["coverage"] = ("real exact Camera PNG decoded and hashed; capture completed; manifest failed; artifact not accepted; stable read-only receipt" if pixels_camera_id else
                                  "real owned manifest replacement failure; unverified receipt; actual uncommitted staging bytes/hash; no state publication; read-only stability"
                                  if manifest_failure else "public Snapshot selector and execution failures; original identity; actual manifest/state bytes/hash; read-only terminal stability")
            report["notCovered"] = ["state write failure", "successful pixels rejected by persistence failure", "capture timeout", "in-flight Reload", "successful pixel capture", "optional-package absence",
                                    "all Server modules loaded", "arbitrary resource cleanup"]
            if pixels_camera_id:
                report["notCovered"].remove("successful pixels rejected by persistence failure")
                report["notCovered"].remove("successful pixel capture")
                report["notCovered"].append("business visual correctness or orientation")
    report["result"] = "observed"


TIMEOUT_CASE = ("gameview-paused-timeout", "allOrNothing", "SNAPSHOT_TARGET_CAPTURE_FAILED", "rendering")
TIMEOUT_MESSAGE = "GameView did not reach an end-of-frame capture point within the Snapshot timeout."
RUNNER_NAME = "UPilot Snapshot GameView Capture"


def require_play_status(data, before, mode):
    # Local runtime check; the shared require_status remains strictly EditMode-only.
    state, session = data.get("executionState") or {}, data.get("session") or {}
    server = (data.get("runtimeIdentity") or {}).get("mcpServer") or {}
    if (Path((data.get("paths") or {}).get("unityProjectAbsolute") or "").resolve() != PROJECT or
            data.get("connected") is not True or data.get("serverReady") is not True or
            session.get("identityVerified") is not True or session.get("processId") != before["unityPid"] or
            session.get("processCreatedAt") != before["unityCreatedAt"] or server.get("processId") != before["serverPid"] or
            (server.get("loadedSource") or {}).get("status") != "equivalent" or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            state.get("isCompiling") is not False or state.get("playModeState") != mode or
            not session.get("sessionId") or not state.get("producerEpoch") or not state.get("domainGeneration")):
        raise CallPreflightError("Exact fresh runtime mode, process and selected-entry source are unverified.")
    return {"bridgeSession": session["sessionId"], "producerEpoch": state["producerEpoch"],
            "domainGeneration": state["domainGeneration"]}


def check_owned_play(data, request_id, command_id):
    transition = data.get("playModeTransition") or {}
    # Reload can retain the original enteringPlay marker after fresh state confirms play.
    state = data.get("executionState") or {}
    if (not request_id or not command_id or transition.get("requestId") != request_id or
            transition.get("commandId") != command_id or transition.get("origin") != "upilot" or
            transition.get("toolName") != "unity_playmode_start" or transition.get("toState") not in ("enteringPlay", "play") or
            state.get("authoritative") is not True or state.get("isStale") is not False or
            state.get("playModeState") not in ("play", "pause") or not transition.get("transitionId")):
        raise CallPreflightError("Current PlayMode is not attributable to this exact owned start; do not stop it.")


INFLIGHT_CASE = ("gameview-inflight-reload", "allOrNothing", "SNAPSHOT_INTERRUPTED_BY_DOMAIN_RELOAD", "rendering")
RELOAD_WITNESS = "CodingRiver.UPilot.Tests.UPilotSnapshotReloadWitness"


def reflection_value(data, type_name):
    value = data.get("resultValue") or {}
    if (data.get("status") != "Succeeded" or value.get("typeName") != type_name or
            value.get("serializationStatus") != "inline" or not isinstance(value.get("valueJson"), str)):
        raise CallPreflightError("Exact inline witness result is unverified; never replay its action.")
    return json.loads(value["valueJson"])


def check_inflight_witness(value, baseline, nonce, directory, stage, *, armed=None):
    job = stringify_wire_ids(value.get("job") or {})
    if (value.get("nonce") != nonce or Path(value.get("directory") or "") != directory or
            value.get("stage") != stage or value.get("error") != "" or
            value.get("isPlaying") is not True or value.get("isPaused") is not True or
            type(value.get("hostCount")) is not int or value["hostCount"] != 1 or
            type(value.get("armedAtUtcMs")) is not int or value["armedAtUtcMs"] < baseline["startedAtUtcMs"] or
            any(job.get(key) != baseline[key] for key in FROZEN) or
            job.get("terminal") is not False or job.get("cancelRequested") is not False or
            job.get("status") != "running" or job.get("phase") != "rendering" or
            job.get("artifacts") != [] or job.get("failures") != []):
        raise CallPreflightError("Actual owned running/rendering Snapshot at the Reload boundary is unverified.")
    if armed is not None and (value["armedAtUtcMs"] != armed["armedAtUtcMs"] or
            type(value.get("observedAtUtcMs")) is not int or
            not 0 <= value["observedAtUtcMs"] - armed["armedAtUtcMs"] <= 10000):
        raise CallPreflightError("Original ten-second witness boundary or arm identity changed.")
    return job


def check_interrupted(value, baseline, boundary):
    failures = value.get("failures") or []
    if (any(value.get(key) != baseline[key] for key in FROZEN) or
            value.get("terminal") is not True or value.get("status") != "failed" or value.get("phase") != "failed" or
            value.get("success") is not False or value.get("cancelRequested") is not False or
            type(value.get("endedAtUtcMs")) is not int or value["endedAtUtcMs"] < boundary["observedAtUtcMs"] or
            value.get("targets") != [] or value.get("artifacts") != [] or len(failures) != 1 or
            failures[0].get("targetId") != "" or failures[0].get("code") != INFLIGHT_CASE[2] or
            failures[0].get("stage") != "rendering" or
            value.get("persistenceStatus") != "verified" or value.get("persistenceVerified") is not True or
            value.get("persistenceError") != "" or value.get("persistenceSchemaVersion") != 2 or
            type(value.get("snapshotSequence")) is not int or value["snapshotSequence"] <= 0):
        raise CallPreflightError("Original pending Snapshot's persisted Reload interruption is unverified.")


async def exercise_paused_timeout(port, report, *, state_failure=False, successful_pixels=False, in_flight_reload=False):
    if in_flight_reload and (state_failure or successful_pixels):
        raise CallPreflightError("Select one exact owned Snapshot boundary.")
    if in_flight_reload and os.name != "nt":
        raise CallPreflightError("The owned in-flight Reload witness requires Windows before mode changes.")
    if successful_pixels and not state_failure:
        raise CallPreflightError("Successful pixels require the exact owned state denial boundary.")
    case = (INFLIGHT_CASE if in_flight_reload else
            (("gameview-state-pixels", "allOrNothing", "STATE_REPLACE_FAILED", "writing") if successful_pixels else TIMEOUT_CASE))
    if state_failure and os.name != "nt":
        raise CallPreflightError("Real state replacement denial requires Windows before any mode change.")
    # One original 120-second budget includes mode entry, observation and cleanup.
    deadline = asyncio.get_running_loop().time() + 120
    report.update(result="unknown", ownedSnapshotIds=[], cases=[])
    nonce, start_request, start_command = uuid.uuid4().hex, None, None
    state_resources = ExitStack()
    locked = None
    witness_attempted = False
    boundary = None
    async with asyncio.timeout_at(deadline):
        async with connect(port) as session:
            async def query(tool, arguments=None):
                if asyncio.get_running_loop().time() >= deadline:
                    raise CallPreflightError("Original timeout-scene budget expired; never replay actions.")
                return await call(session, tool, arguments or {}, ORIGIN, report)

            async def inventory():
                queue = await query("unity_queue_cleanup")
                capture = await query("unity_console_capture_list", {"activeOnly": True})
                runner = await query("unity_test_status")
                if (queue.get("complete") is not True or queue.get("isStale") is not False or queue.get("items") != [] or
                        capture.get("activeCount") != 0 or runner.get("isRunning") is not False or runner.get("cleanupPending") is True):
                    raise CallPreflightError("Fresh complete empty queue, Capture and Runner are required.")

            async def hosts():
                data = await query("unity_gameobject_find", {"name": RUNNER_NAME, "includeHidden": True,
                                                          "includeInactive": True, "limit": 10})
                if data.get("truncated") is not False or not isinstance(data.get("gameObjects"), list):
                    raise CallPreflightError("Hidden capture host coverage is incomplete.")
                return data

            async def background():
                data = await query("unity_reflection_call", {"expression": "UnityEngine.Application.runInBackground"})
                value = data.get("resultValue") or {}
                if (data.get("status") != "Succeeded" or value.get("typeName") != "System.Boolean" or
                        value.get("serializationStatus") != "inline" or value.get("valueJson") not in ("true", "false")):
                    raise CallPreflightError("runInBackground getter result is unverified.")
                return value["valueJson"] == "true"

            status_args = {"forceFresh": True, "includeCapabilities": False}
            initial = await query("unity_mcp_status", status_args)
            before = require_status(initial)
            report["identityBefore"] = before
            grant = initial.get("automationAuthorization") or {}
            if grant.get("catalogCurrent") is not True or "editorModeTransition" not in grant.get("enabledScopes", []):
                raise CallPreflightError("Existing current mode-transition authorization is required; never change grants.")
            await inventory()
            ready = await query("unity_ensure_ready", {"timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual"})
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh manual EditMode readiness is required.")
            scenes = await query("unity_scene_list")
            if not scenes.get("scenes") or any(x.get("isDirty") is not False or x.get("isLoaded") is not True or
                                              not x.get("scenePath") for x in scenes["scenes"]):
                raise CallPreflightError("Only loaded, saved, clean scenes may enter this owned PlayMode cycle.")
            report["scenesBefore"] = scenes
            if (await hosts())["gameObjects"]:
                raise CallPreflightError("A capture host already exists; do not take ownership.")
            found = await query("unity_tools_find", {"query": "runInBackground", "limit": 5})
            if found.get("count") != 0:
                raise CallPreflightError("A semantic background-state tool may exist; resolve it before using reflection.")
            report["getterFallback"] = "No dedicated runInBackground tool; one bounded read-only property expression."
            report["backgroundBeforeEdit"] = await background()
            if in_flight_reload:
                discovery = await query("unity_tools_find", {"query": "RequestScriptReload", "limit": 5})
                if discovery.get("count") != 0 or discovery.get("tools", []) != []:
                    raise CallPreflightError("Resolve a possible dedicated Reload tool before using reflection.")
                for type_name, method, return_type, names in (
                        ("UnityEditor.EditorUtility", "RequestScriptReload", "System.Void", []),
                        (RELOAD_WITNESS, "Arm", "System.String", ["snapshotId", "nonce"]),
                        (RELOAD_WITNESS, "Disarm", "System.Boolean", ["nonce"])):
                    signature = await query("unity_reflection_find", {"typeName": type_name, "methodName": method})
                    methods = signature.get("methods") or []
                    if (len(methods) != 1 or methods[0].get("typeName") != type_name or
                            methods[0].get("methodName") != method or methods[0].get("isStatic") is not True or
                            methods[0].get("returnType") != return_type or
                            methods[0].get("parameters") != [{"name": name, "type": "System.String"} for name in names]):
                        raise CallPreflightError("Exact compiled Reload/witness signatures must exist before mode changes.")
                report["reloadFallback"] = "No dedicated Reload tool; verified zero-argument EditorUtility method, once."
                final_before = require_status(await query("unity_mcp_status", status_args))
                if any(final_before[key] != before[key] for key in ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch", "domainGeneration", "bridgeSession")):
                    raise CallPreflightError("Reload preflight target changed before mode entry.")
                await inventory()
                if await query("unity_scene_list") != scenes:
                    raise CallPreflightError("Scene state changed during Reload preflight; do not enter mode.")
            try:
                # Reserve 30 seconds of the original budget for owned mode cleanup.
                async with asyncio.timeout_at(deadline - 30):
                    receipt = await query("unity_playmode_start")  # once; no replay on uncertain delivery
                    start_request = report["calls"][-1]["result"].get("requestId")
                    start_command = receipt.get("commandId")
                    if receipt.get("confirmed") is not True or receipt.get("playModeState") != "play":
                        raise CallPreflightError("Owned PlayMode entry is unconfirmed.")
                    playing = await query("unity_mcp_status", status_args)
                    require_play_status(playing, before, "play")
                    check_owned_play(playing, start_request, start_command)
                    paused = await query("unity_playmode_pause", {"wait": True, "timeoutMs": 5000})
                    if paused.get("confirmed") is not True:
                        raise CallPreflightError("Owned pause is unconfirmed.")
                    current = await query("unity_mcp_status", status_args)
                    identity = require_play_status(current, before, "pause")
                    check_owned_play(current, start_request, start_command)
                    await inventory()
                    if (await hosts())["gameObjects"]:
                        raise CallPreflightError("Another capture host appeared before this Snapshot.")
                    report["backgroundBeforeCapture"] = await background()
                    relative = "Log/P0P1/" + ("v09-inflight-" if in_flight_reload else ("v09-state-pixels-" if successful_pixels else ("v09-state-failure-" if state_failure else "v09-timeout-"))) + nonce + "/" + case[0]
                    directory = owned_path(PROJECT / relative)
                    if directory.exists():
                        raise CallPreflightError("Owned timeout directory must not exist before capture.")
                    request = {"targets": [{"targetId": case[0], "kind": "gameview", "width": 32, "height": 32}],
                               "channels": ["color"], "completionPolicy": case[1], "waitMs": 0,
                               "requestKey": ("v09-inflight-" if in_flight_reload else "v09-timeout-") + nonce, "outputDirectory": relative}
                    observed = {"case": case[0], "request": request, "identityPaused": identity}
                    report["cases"].append(observed)
                    data = await query("unity_snapshot_capture", request)  # one owned Snapshot
                    baseline = check_admission(data, request, relative)
                    report["ownedSnapshotIds"].append(baseline["snapshotId"])
                    observed["admission"] = data
                    if state_failure:
                        if data.get("terminal") is not False:
                            raise CallPreflightError("Snapshot already ended before state denial; do not repeat it.")
                        locked = state_resources.enter_context(hold_owned_state(baseline))
                        observed["lockedState"] = locked
                    capture_mode = "pause"
                    if in_flight_reload:
                        boundary_directory = owned_path(directory.parent / "boundary")
                        if boundary_directory.exists():
                            raise CallPreflightError("Witness nonce/path was used; never replay Arm.")
                        while data.get("status") != "running" or data.get("phase") != "rendering":
                            if data.get("terminal") is not False:
                                raise CallPreflightError("Snapshot ended before it could be armed; do not recapture.")
                            await asyncio.sleep(0.05)
                            data = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                            if any(data.get(key) != baseline[key] for key in FROZEN):
                                raise CallPreflightError("Pending Snapshot identity changed before Arm.")
                        observed["pending"] = data
                        observed["pendingFiles"] = verify_files(data, baseline, directory)
                        current = await query("unity_mcp_status", status_args)
                        if require_play_status(current, before, "pause") != identity:
                            raise CallPreflightError("Managed identity changed before the owned witness.")
                        check_owned_play(current, start_request, start_command)
                        report["hostsBeforeArm"] = await hosts()
                        if len(report["hostsBeforeArm"]["gameObjects"]) != 1:
                            raise CallPreflightError("Exactly one owned capture host is required before Arm.")
                        witness_attempted = True  # even a lost receipt may have installed this nonce's observer
                        receipt = await query("unity_reflection_call", {"kind": "method", "typeName": RELOAD_WITNESS,
                            "methodName": "Arm", "isStatic": True, "parameters": [baseline["snapshotId"], nonce],
                            "parameterTypeNames": ["System.String", "System.String"]})
                        arm_value = reflection_value(receipt, "System.String")
                        if not isinstance(arm_value, str):
                            raise CallPreflightError("Arm must return the exact serialized witness string.")
                        armed = json.loads(arm_value)
                        check_inflight_witness(armed, baseline, nonce, boundary_directory, "armed")
                        armed_file, disk_armed = read_json(boundary_directory / "armed.json")
                        if armed != disk_armed:
                            raise CallPreflightError("Arm receipt does not match the actual owned boundary file.")
                        observed["armedWitness"] = {"file": armed_file, "data": armed}
                        report["reloadDispatchAttempted"] = True
                        try:
                            report["reloadReceipt"] = await asyncio.wait_for(call_envelope(session, "unity_reflection_call", {
                                "kind": "method", "typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload",
                                "isStatic": True, "parameters": [],
                            }, ORIGIN, report), timeout=min(10, deadline - 30 - asyncio.get_running_loop().time()))
                        except Exception as exc:
                            report["reloadReceiptError"] = {"type": type(exc).__name__,
                                "message": "Dispatch receipt unknown; observe original Snapshot and witness only, never resend."}
                        while True:
                            try:
                                current = await query("unity_mcp_status", status_args)
                            except (CallPreflightError, ConnectionError, TimeoutError):
                                await asyncio.sleep(0.2)
                                continue
                            state = current.get("executionState") or {}
                            if (current.get("connected") is not True or state.get("authoritative") is not True or
                                    state.get("isStale") is not False or state.get("isCompiling") is not False):
                                await asyncio.sleep(0.2)
                                continue
                            after_reload = require_play_status(current, before, "pause")
                            if after_reload["producerEpoch"] != identity["producerEpoch"]:
                                raise CallPreflightError("Service restart is not a same-lifetime capture interruption.")
                            check_owned_play(current, start_request, start_command)
                            if after_reload["domainGeneration"] == identity["domainGeneration"]:
                                # RequestScriptReload can be deferred in PlayMode; absent boundary is not a pass.
                                await asyncio.sleep(0.2)
                                continue
                            if after_reload["bridgeSession"] == identity["bridgeSession"]:
                                raise CallPreflightError("Changed domain lacks a reconnected Bridge identity.")
                            report["identityAfterReload"] = after_reload
                            break
                        boundary_file, boundary = read_json(boundary_directory / "before-reload.json")
                        check_inflight_witness(boundary, baseline, nonce, boundary_directory, "beforeAssemblyReload", armed=armed)
                        if (boundary_directory / "boundary-error.json").exists():
                            raise CallPreflightError("Reload witness recorded an error; retain it without claiming coverage.")
                        observed["reloadBoundary"] = {"file": boundary_file, "data": boundary}
                        identity = after_reload
                        data = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                    if successful_pixels:
                        observed["resume"] = await query("unity_playmode_resume", {"wait": True, "timeoutMs": 5000})
                        if observed["resume"].get("confirmed") is not True:
                            raise CallPreflightError("Owned resume is unconfirmed; never replay resume.")
                        current = await query("unity_mcp_status", status_args)
                        if require_play_status(current, before, "play") != identity:
                            raise CallPreflightError("Managed identity changed while resuming the owned capture.")
                        check_owned_play(current, start_request, start_command)
                        capture_mode = "play"
                    while data.get("terminal") is not True:
                        await asyncio.sleep(0.2)
                        current = await query("unity_mcp_status", status_args)
                        if require_play_status(current, before, capture_mode) != identity:
                            raise CallPreflightError("Managed identity changed during the owned capture.")
                        check_owned_play(current, start_request, start_command)
                        data = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                        if any(data.get(key) != baseline[key] for key in FROZEN):
                            raise CallPreflightError("Original pending timeout Snapshot identity changed.")
                    observed["terminal"] = data
                    def check_terminal(value, *, recovered=False):
                        if in_flight_reload:
                            check_interrupted(value, baseline, boundary)
                        elif successful_pixels:
                            check_unaccepted_gameview_pixels(value, baseline, before["unityPid"], recovered=recovered)
                        else:
                            check_failed(value, baseline, TIMEOUT_CASE, state_failure=state_failure and not recovered)
                    check_terminal(data)
                    if not successful_pixels and not in_flight_reload and (data["failures"][0].get("message") != TIMEOUT_MESSAGE or
                            data["endedAtUtcMs"] - data["startedAtUtcMs"] < 9000):
                        raise CallPreflightError("Exact real ten-second end-of-frame timeout is unverified.")
                    def files_for(value, *, recovered=False):
                        pixel = {"pixel_artifact": value["artifacts"][0]} if successful_pixels else {}
                        return (verify_state_failure_files(value, baseline, directory, locked, **pixel)
                                if state_failure and not recovered else verify_files(value, baseline, directory, **pixel))
                    files = files_for(data)
                    for tool in ("unity_snapshot_collect_artifacts", "unity_snapshot_status"):
                        args = {"snapshotId": baseline["snapshotId"]}
                        if tool == "unity_snapshot_status":
                            args["detailLevel"] = "full"
                        again = await query(tool, args)
                        check_terminal(again)
                        if any(again.get(key) != data.get(key) for key in TERMINAL) or files_for(again) != files:
                            raise CallPreflightError("Read-only timeout queries changed terminal receipt or files.")
                    if state_failure:
                        # Retain exact failure-boundary bytes before the later normal mode Reload can rebuild state.
                        evidence = owned_path(directory.parent / "failure-boundary")
                        evidence.mkdir()
                        copies = []
                        for role, original in (("manifest", files["manifest"]), ("old-state", files["oldState"])):
                            raw = Path(original["path"]).read_bytes()
                            if len(raw) != original["bytes"] or hashlib.sha256(raw).hexdigest() != original["sha256"]:
                                raise CallPreflightError("Failure-boundary file changed before its owned evidence copy.")
                            destination = evidence / (role + ".json")
                            with destination.open("xb") as output:
                                output.write(raw)
                            copies.append({"role": role, **read_json(destination)[0]})
                        observed["failureBoundaryCopies"] = copies
                    observed.update(files=files, result="observed")
                    report["hostsAfterTimeoutPaused"] = await hosts()  # Destroy may defer while paused.
                    report["backgroundAfterTimeout"] = await background()
                    if report["backgroundAfterTimeout"] != report["backgroundBeforeCapture"]:
                        raise CallPreflightError("Capture did not restore runInBackground before mode exit.")
            except BaseException as exc:
                report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                                  else "Original action outcome unknown; no start replayed."})
                raise
            finally:
                try:
                    state_resources.close()  # Release the exact file before owned mode cleanup.
                    if locked is not None:
                        report["stateHandleReleased"] = True
                except BaseException as exc:
                    report.setdefault("cleanupError", {"type": type(exc).__name__, "message": "Owned state handle release failed."})
                if witness_attempted:
                    try:
                        current = await query("unity_mcp_status", status_args)
                        require_play_status(current, before, "pause")
                        if (current.get("executionState") or {}).get("producerEpoch") != identity["producerEpoch"]:
                            raise CallPreflightError("Witness cleanup service lifetime changed; do not touch another lifetime.")
                        check_owned_play(current, start_request, start_command)
                        receipt = await query("unity_reflection_call", {"kind": "method", "typeName": RELOAD_WITNESS,
                            "methodName": "Disarm", "isStatic": True, "parameters": [nonce], "parameterTypeNames": ["System.String"]})
                        disarmed = reflection_value(receipt, "System.Boolean")
                        if type(disarmed) is not bool:
                            raise CallPreflightError("Owned witness Disarm result is unverified.")
                        report["witnessCleanup"] = {"verified": True, "changed": disarmed}
                    except BaseException as exc:
                        report.setdefault("cleanupError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                            else "Exact owned witness cleanup unverified; Disarm is not replayed."})
                # Only stop this exact attributed PlayMode; never cancel, repeat resume, or restart services.
                starts = [x for x in report["calls"] if x["tool"] == "unity_playmode_start"]
                if starts:
                    start_request = start_request or starts[-1]["result"].get("requestId")
                    start_command = start_command or (starts[-1]["result"].get("data") or {}).get("commandId")
                if start_request:
                    try:
                        current = await query("unity_mcp_status", status_args)
                        mode = (current.get("executionState") or {}).get("playModeState")
                        require_play_status(current, before, mode)
                        check_owned_play(current, start_request, start_command)
                        if in_flight_reload and (current.get("executionState") or {}).get("producerEpoch") != identity["producerEpoch"]:
                            raise CallPreflightError("Original service lifetime changed; do not stop a replacement lifetime.")
                        if mode not in ("play", "pause"):
                            raise CallPreflightError("Owned mode changed externally; no cleanup stop dispatched.")
                        report["modeCleanup"] = await query("unity_playmode_stop")  # once
                        if (report["modeCleanup"].get("confirmed") is not True or
                                report["modeCleanup"].get("playModeState") != "edit"):
                            raise CallPreflightError("Owned mode exit is unconfirmed; do not replay stop.")
                        after = await query("unity_mcp_status", status_args)
                        report["identityAfter"] = require_status(after)
                        if any(report["identityAfter"][key] != before[key] for key in ("unityPid", "unityCreatedAt", "serverPid")):
                            raise CallPreflightError("Process identity changed during cleanup.")
                        report["hostsAfterEdit"] = await hosts()
                        report["backgroundAfterEdit"] = await background()
                        report["scenesAfter"] = await query("unity_scene_list")
                        if (report["hostsAfterEdit"]["gameObjects"] or report["backgroundAfterEdit"] != report["backgroundBeforeEdit"] or
                                report["scenesAfter"] != scenes):
                            raise CallPreflightError("Owned mode cleanup, capture host removal or scene restoration is unverified.")
                        await inventory()
                        if in_flight_reload and report["cases"] and report["cases"][0].get("result") == "observed":
                            after_snapshot = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                            check_terminal(after_snapshot)
                            if (any(after_snapshot.get(key) != data.get(key) for key in TERMINAL) or
                                    files_for(after_snapshot) != files or
                                    read_json(boundary_directory / "armed.json")[0] != armed_file or
                                    read_json(boundary_directory / "before-reload.json")[0] != boundary_file or
                                    report["identityAfter"]["producerEpoch"] != before["producerEpoch"]):
                                raise CallPreflightError("Owned cleanup changed terminal files, witness or service lifetime.")
                            report["postCleanupSnapshot"] = after_snapshot
                            report["postCleanupFiles"] = files_for(after_snapshot)
                        if state_failure and report["cases"] and report["cases"][0].get("result") == "observed":
                            after_snapshot = await query("unity_snapshot_status", {"snapshotId": baseline["snapshotId"], "detailLevel": "full"})
                            recovered = after_snapshot.get("persistenceStatus") == "verified"
                            check_terminal(after_snapshot, recovered=recovered)
                            if any(after_snapshot.get(key) != data.get(key) for key in TERMINAL
                                   if key not in {"persistenceStatus", "persistenceError", "manifestBytes", "manifestSha256"}):
                                raise CallPreflightError("Mode cleanup changed the original terminal business outcome.")
                            if recovered and after_snapshot.get("persistenceRecovered") is not True:
                                raise CallPreflightError("Verified after cleanup lacks explicit persistence recovery evidence.")
                            report["postCleanupSnapshot"] = after_snapshot
                            report["postCleanupFiles"] = files_for(after_snapshot, recovered=recovered)
                            report["persistenceRecoveryObserved"] = recovered
                        report["cleanupVerified"] = True
                    except BaseException as exc:
                        report.setdefault("cleanupError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                                                  else "Owned mode cleanup outcome unknown; no stop replayed."})
                else:
                    report.setdefault("cleanupError", {"message": "No received start request identity; do not stop an unattributed PlayMode."})
    if report.get("cleanupVerified") is not True or report.get("cleanupError"):
        raise CallPreflightError("Owned timeout scene cleanup is unverified.")
    if state_failure and report.get("stateHandleReleased") is not True:
        raise CallPreflightError("Owned state denial handle release is unverified.")
    if in_flight_reload:
        if (report.get("witnessCleanup") or {}).get("verified") is not True:
            raise CallPreflightError("Owned Reload witness cleanup is unverified.")
        report.update(result="observed", coverage="real beforeAssemblyReload running/rendering witness; same-lifetime original Snapshot interrupted; persisted terminal; owned mode cleanup",
                      notCovered=["optional-package absence", "all Server modules loaded", "content orientation/composition/crop",
                                  "other Unity/platform combinations", "other Snapshot failure shapes"])
        return
    report.update(result="observed", coverage=("real GameView pixels rejected as evidence after Windows state replacement denial; old state retained; terminal manifest; owned mode cleanup"
                  if successful_pixels else ("real Windows state replacement denial; old state retained; terminal manifest; exact original failure; owned mode cleanup"
                  if state_failure else "real paused GameView timeout; exact identity; manifest/state bytes/hash; owned mode cleanup")),
                  notCovered=["in-flight Reload", "optional-package absence", "all Server modules loaded",
                              "content orientation/composition/crop", "other Unity/platform combinations"] +
                             ([] if successful_pixels else ["successful pixels", "immediate physical Destroy while paused"]))


def read_owned_recovery_report(path):
    """Reuse only our released state-failure record, not an arbitrary Snapshot selector."""
    path = owned_path(Path(path))
    evidence_root = owned_path(PROJECT / "Log" / "P0P1")
    if path.parent != evidence_root or not re.fullmatch(r"v09-failure-observation-[0-9a-f]{32}\.json", path.name):
        raise CallPreflightError("Recovery requires the original owned V09 observation in Log/P0P1.")
    source_file, source = read_json(path)
    cases = source.get("cases") or []
    if (source.get("result") != "observed" or source.get("cleanupVerified") is not True or
            source.get("stateHandleReleased") is not True or source.get("cleanupError") or source.get("firstError") or
            source.get("persistenceRecoveryObserved") is not False or len(cases) != 1 or
            cases[0].get("result") != "observed"):
        raise CallPreflightError("The original state-failure observation or owned cleanup is unverified.")
    case = cases[0]
    terminal = case.get("terminal") or {}
    directory = owned_path(PROJECT / str(terminal.get("outputDirectory") or ""))
    if (directory.parent.parent != evidence_root or directory.name != "gameview-state-pixels" or
            not re.fullmatch(r"v09-state-pixels-[0-9a-f]{32}", directory.parent.name)):
        raise CallPreflightError("Recovery must stay in the original owned successful GameView directory.")
    baseline = check_admission(case.get("admission") or {}, case.get("request") or {},
                               directory.relative_to(PROJECT).as_posix())
    if source.get("ownedSnapshotIds") != [baseline["snapshotId"]]:
        raise CallPreflightError("Original report must contain exactly its owned Snapshot ID.")
    original_identity = source.get("identityAfter") or {}
    check_unaccepted_gameview_pixels(terminal, baseline, original_identity.get("unityPid"))
    if any((source.get("postCleanupSnapshot") or {}).get(k) != terminal.get(k) for k in TERMINAL):
        raise CallPreflightError("Original owned cleanup changed the frozen Snapshot outcome.")
    files = verify_state_failure_files(terminal, baseline, directory, case.get("lockedState") or {},
                                       pixel_artifact=terminal["artifacts"][0])
    if files != case.get("files") or files != source.get("postCleanupFiles"):
        raise CallPreflightError("Original failure-boundary files no longer match their recorded bytes/hash.")
    return source_file, source, baseline, directory, files


async def exercise_state_recovery(port, report, original_path):
    """One normal Reload; no recapture, mode changes, compile, service restart or replay."""
    deadline = asyncio.get_running_loop().time() + 90
    source_file, source, baseline, directory, original_files = read_owned_recovery_report(original_path)
    report.update(result="unknown", originalReport=source_file, ownedSnapshotIds=[baseline["snapshotId"]],
                  originalFiles=original_files, reloadDispatchAttempted=False, persistenceRecoveryObserved=False)
    async with asyncio.timeout_at(deadline):
        async with connect(port) as session:
            async def query(tool, arguments=None):
                if asyncio.get_running_loop().time() >= deadline:
                    raise CallPreflightError("Original recovery observation budget expired; never renew it.")
                return await call(session, tool, arguments or {}, ORIGIN, report)

            async def inventory():
                queue = await query("unity_queue_cleanup")
                captures = await query("unity_console_capture_list", {"activeOnly": True})
                runner = await query("unity_test_status")
                if (queue.get("complete") is not True or queue.get("isStale") is not False or queue.get("items") != [] or
                        captures.get("activeCount") != 0 or runner.get("isRunning") is not False or
                        runner.get("cleanupPending") is True):
                    raise CallPreflightError("Fresh complete queue, Capture and Runner must be idle before owned Reload.")

            status_args = {"forceFresh": True, "includeCapabilities": False}
            before = require_status(await query("unity_mcp_status", status_args))
            report["identityBefore"] = before
            same_lifetime = ("unityPid", "unityCreatedAt", "serverPid", "producerEpoch")
            if any(before[k] != (source.get("identityAfter") or {}).get(k) for k in same_lifetime):
                raise CallPreflightError("Original Unity/Server process and service lifetime are not current.")
            await inventory()
            scenes = await query("unity_scene_list")
            if (not isinstance(scenes.get("scenes"), list) or not scenes["scenes"] or
                    any(s.get("isDirty") is not False or s.get("isLoaded") is not True for s in scenes["scenes"])):
                raise CallPreflightError("All existing scenes must be known, loaded and clean before owned Reload.")
            ready = await query("unity_ensure_ready", {"timeoutS": 5, "requiredEditorMode": "edit", "editorModeControl": "manual"})
            if (ready.get("ready") is not True or ready.get("blocked") is not False or
                    ready.get("contextAuthoritative") is not True or ready.get("contextStale") is not False):
                raise CallPreflightError("Fresh authoritative manual EditMode readiness is required.")
            selector = {"snapshotId": baseline["snapshotId"], "detailLevel": "full"}
            current = await query("unity_snapshot_status", selector)
            if any(current.get(k) != source["cases"][0]["terminal"].get(k) for k in TERMINAL):
                raise CallPreflightError("Original Snapshot terminal changed before Reload; no dispatch permitted.")
            check_unaccepted_gameview_pixels(current, baseline, before["unityPid"])
            if verify_state_failure_files(current, baseline, directory, source["cases"][0]["lockedState"],
                                          pixel_artifact=current["artifacts"][0]) != original_files:
                raise CallPreflightError("Original Snapshot files changed before Reload.")
            discovery = await query("unity_tools_find", {"query": "RequestScriptReload", "limit": 5})
            if discovery.get("count") != 0 or discovery.get("tools", []) != []:
                raise CallPreflightError("A dedicated Reload tool may exist; do not bypass semantic discovery.")
            signature = await query("unity_reflection_find", {"typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload"})
            methods = signature.get("methods") or []
            if (len(methods) != 1 or methods[0].get("typeName") != "UnityEditor.EditorUtility" or
                    methods[0].get("methodName") != "RequestScriptReload" or methods[0].get("isStatic") is not True or
                    methods[0].get("returnType") != "System.Void" or methods[0].get("parameters") != []):
                raise CallPreflightError("Exact zero-argument static RequestScriptReload signature is unverified.")
            # Recheck identity/resources at the last read-only boundary. Never replay this action.
            final_before = require_status(await query("unity_mcp_status", status_args))
            if any(final_before[k] != before[k] for k in (*same_lifetime, "domainGeneration", "bridgeSession")):
                raise CallPreflightError("Reload target changed during preflight.")
            await inventory()
            report["reloadDispatchAttempted"] = True
            try:
                report["reloadReceipt"] = await asyncio.wait_for(call_envelope(session, "unity_reflection_call", {
                    "kind": "method", "typeName": "UnityEditor.EditorUtility", "methodName": "RequestScriptReload",
                    "isStatic": True, "parameters": [],
                }, ORIGIN, report), timeout=min(10, deadline - asyncio.get_running_loop().time()))
            except Exception as exc:
                report["reloadReceiptError"] = {"type": type(exc).__name__,
                                               "message": "Dispatch receipt unknown; observe original ID only, never resend."}
            while True:
                try:
                    status = await query("unity_mcp_status", status_args)
                except (CallPreflightError, ConnectionError, TimeoutError):
                    await asyncio.sleep(0.5)
                    continue
                state = status.get("executionState") or {}
                if status.get("connected") is not True or state.get("ready") is not True or state.get("isStale") is not False:
                    await asyncio.sleep(0.5)
                    continue
                after = require_status(status)
                if any(after[k] != before[k] for k in same_lifetime):
                    raise CallPreflightError("A service/process change is not same-lifetime Snapshot recovery.")
                if after["domainGeneration"] == before["domainGeneration"]:
                    await asyncio.sleep(0.5)
                    continue
                report["identityAfter"] = after
                break
            restored = await query("unity_snapshot_status", selector)
            # Receipt fields may recover; business outcome, sequence, time and rejected PNG may not change.
            business = tuple(k for k in TERMINAL if k not in ("persistenceStatus", "persistenceError", "manifestBytes", "manifestSha256"))
            if any(restored.get(k) != current.get(k) for k in business):
                raise CallPreflightError("Recovery changed the original frozen business outcome.")
            check_unaccepted_gameview_pixels(restored, baseline, after["unityPid"], recovered=True)
            files = verify_files(restored, baseline, directory, pixel_artifact=restored["artifacts"][0])
            if files["manifest"] != original_files["manifest"] or files["pixel"] != original_files["pixel"]:
                raise CallPreflightError("Recovery rewrote the original terminal manifest or PNG.")
            report.update(recoveredSnapshot=restored, recoveredFiles=files)
            for tool, arguments in (("unity_snapshot_status", selector),
                                    ("unity_snapshot_collect_artifacts", {"snapshotId": baseline["snapshotId"]})):
                again = await query(tool, arguments)
                if (any(again.get(k) != restored.get(k) for k in (*TERMINAL, "persistenceRecovered", "persistenceVerified")) or
                        verify_files(again, baseline, directory, pixel_artifact=again["artifacts"][0]) != files):
                    raise CallPreflightError("Recovered public receipt or files changed after a later read.")
            final_identity = require_status(await query("unity_mcp_status", status_args))
            if any(final_identity[k] != after[k] for k in (*same_lifetime, "domainGeneration")):
                raise CallPreflightError("Recovered identity changed during readback verification.")
            report["identityAfterReadback"] = final_identity
            await inventory()
            if await query("unity_scene_list") != scenes:
                raise CallPreflightError("Owned Reload changed scene state.")
            if read_json(Path(source_file["path"]))[0] != source_file:
                raise CallPreflightError("Recovery rewrote the original failure observation.")
    report.update(result="observed", persistenceRecoveryObserved=True,
                  coverage="one normal same-lifetime Reload; original state-failed Snapshot rebuilt without recapture; actual state/manifest/PNG hashes",
                  notCovered=["in-flight capture Reload", "optional-package absence", "all Server modules loaded", "other Unity/platform combinations"])


async def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8011)
    parser.add_argument("--execute", action="store_true", help="Explicitly permit the selected owned Snapshot scenario; default has no mode changes")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--recover-state-report", type=Path, help="One normal Reload to recover the exact owned original successful-pixels state-failure observation; no recapture or mode transitions")
    mode.add_argument("--state-pixels-failure", action="store_true", help="One owned pause/lock/resume/stop cycle; successful Display 0 pixels with exact state replacement denial")
    mode.add_argument("--state-write-failure", action="store_true", help="One owned paused GameView cycle; deny replacement of only its existing exact state file on Windows")
    mode.add_argument("--in-flight-reload", action="store_true", help="One owned paused GameView capture, real beforeAssemblyReload witness, one Reload request and exact owned cleanup; Windows only")
    mode.add_argument("--paused-timeout", action="store_true", help="One owned PlayMode/pause/timeout/stop cycle; requires existing mode grant and clean scenes")
    mode.add_argument("--manifest-write-failure", action="store_true", help="One EditMode missing-camera failure with a real owned empty-directory manifest obstruction; retain staging evidence")
    mode.add_argument("--manifest-pixels-camera-id", default=None, help="One exact active/enabled Camera color capture with an owned manifest obstruction; no scene/mode changes")
    args = parser.parse_args(argv)
    if not args.execute or not 1 <= args.port <= 65535:
        parser.error("--execute and a valid local HTTP port are required")
    if args.manifest_pixels_camera_id is not None and not re.fullmatch(r"[1-9][0-9]*", args.manifest_pixels_camera_id):
        parser.error("--manifest-pixels-camera-id requires an exact nonzero decimal instanceId")
    nonce = uuid.uuid4().hex
    report = {"schemaVersion": 1, "observationId": nonce, "startedAtUtcMs": int(time.time() * 1000),
              "scope": "V09 public Snapshot failure only", "calls": [], "result": "unknown"}
    failed = False
    try:
        if args.recover_state_report is not None:
            report["scope"] = "V09 original state-failure persistence recovery after one normal Reload"
            await exercise_state_recovery(args.port, report, args.recover_state_report)
        elif args.state_pixels_failure:
            report["scope"] = "V09 successful GameView pixels rejected as evidence after exact state failure"
            await exercise_paused_timeout(args.port, report, state_failure=True, successful_pixels=True)
        elif args.state_write_failure:
            report["scope"] = "V09 real owned state replacement denial and original timeout; no product fault hook"
            await exercise_paused_timeout(args.port, report, state_failure=True)
        elif args.in_flight_reload:
            report["scope"] = "V09 genuinely in-flight Snapshot Reload with exact owned boundary witness"
            await exercise_paused_timeout(args.port, report, in_flight_reload=True)
        elif args.paused_timeout:
            report["scope"] = "V09 real paused GameView timeout and owned mode cleanup"
            await exercise_paused_timeout(args.port, report)
        elif args.manifest_pixels_camera_id is not None:
            report["scope"] = "V09 successful exact Camera pixels rejected as evidence after manifest failure"
            await exercise(args.port, report, pixels_camera_id=args.manifest_pixels_camera_id)
        elif args.manifest_write_failure:
            report["scope"] = "V09 real owned manifest replacement failure; no mode changes"
            await exercise(args.port, report, manifest_failure=True)
        else:
            await exercise(args.port, report)
    except BaseException as exc:
        failed = True
        report["result"] = "unknown"
        report.setdefault("firstError", {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, CallPreflightError)
                          else "Outcome unknown; no start, cancel or maintenance replayed."})
    finally:
        report["endedAtUtcMs"] = int(time.time() * 1000)
        path = owned_path(PROJECT / "Log" / "P0P1" / ("v09-failure-observation-" + nonce + ".json"))
        output, size, digest = _write_observation(path, _redact(report))
        print(json.dumps({"path": str(output), "bytes": size, "sha256": digest, "result": report["result"]}))
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
