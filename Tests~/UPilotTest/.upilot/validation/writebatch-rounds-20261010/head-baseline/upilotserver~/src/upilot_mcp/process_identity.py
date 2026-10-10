from __future__ import annotations

import ctypes
import json
import os
import shlex
import subprocess
from pathlib import Path
from typing import Any


MAIN_EDITOR_ROLE = "mainEditor"
AUXILIARY_ROLES = {
    "assetImportWorker",
    "batchMode",
    "profiler",
    "worker",
}


def normalize_path(value: str | Path | None) -> str:
    raw = str(value or "").strip()
    if not raw:
        return ""
    try:
        return os.path.normcase(str(Path(raw).resolve()))
    except OSError:
        return os.path.normcase(os.path.abspath(raw))


def paths_equal(left: str | Path | None, right: str | Path | None) -> bool:
    return bool(normalize_path(left)) and normalize_path(left) == normalize_path(right)


def command_line_args(command_line: str) -> list[str]:
    if os.name != "nt":
        return shlex.split(command_line)
    shell = ctypes.WinDLL("shell32", use_last_error=True)
    shell.CommandLineToArgvW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_int)]
    shell.CommandLineToArgvW.restype = ctypes.POINTER(ctypes.c_wchar_p)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    count = ctypes.c_int()
    arguments = shell.CommandLineToArgvW(command_line, ctypes.byref(count))
    if not arguments:
        return []
    try:
        return [arguments[index] for index in range(count.value)]
    finally:
        kernel.LocalFree(arguments)


def _option_value(args: list[str], option: str) -> str:
    lowered = [value.lower() for value in args]
    indexes = [index for index, value in enumerate(lowered) if value == option]
    if len(indexes) != 1 or indexes[0] + 1 >= len(args):
        return ""
    return str(args[indexes[0] + 1]).strip()


def classify_unity_process(row: dict[str, Any], project_root: Path | None) -> dict[str, Any]:
    process_id = int(row.get("ProcessId") or 0)
    executable_path = str(row.get("ExecutablePath") or "")
    command_line = str(row.get("CommandLine") or "")
    details: dict[str, Any] = {
        "processId": process_id,
        "executablePath": executable_path,
        "processCreatedAt": int(row.get("ProcessCreatedAt") or 0),
        "processRole": "unknown",
        "projectPath": "",
        "projectPathMatches": False,
        "executableVerified": False,
        "eligibleMainEditor": False,
        "exclusionReasons": [],
    }
    reasons: list[str] = details["exclusionReasons"]
    if process_id <= 0:
        reasons.append("invalid_process_id")
    if not executable_path or Path(executable_path).name.lower() != "unity.exe":
        reasons.append("executable_not_unity")
    else:
        details["executableVerified"] = True
    if not command_line:
        reasons.append("command_line_unavailable")
        return details

    try:
        args = command_line_args(command_line)
        lowered = [value.lower() for value in args]
        ump_role = _option_value(args, "-ump-process-role").lower()
        editor_mode = _option_value(args, "-editor-mode").lower()
        if any("assetimportworker" in value for value in lowered):
            role = "assetImportWorker"
        elif "-batchmode" in lowered or "/batchmode" in lowered:
            role = "batchMode"
        elif ump_role == "profiler" or editor_mode == "profiler":
            role = "profiler"
        elif ump_role and ump_role not in {"editor", "main", "main-editor", "maineditor"}:
            role = "worker"
        elif any(value == "-adb2" or value == "-ump" or value.startswith("-worker") for value in lowered):
            role = "worker"
        else:
            role = MAIN_EDITOR_ROLE
        details["processRole"] = role
        if role != MAIN_EDITOR_ROLE:
            reasons.append("worker_role")

        indexes = [index for index, value in enumerate(lowered) if value == "-projectpath"]
        if len(indexes) != 1 or indexes[0] + 1 >= len(args):
            reasons.append("project_path_missing_or_ambiguous")
            return details
        actual = Path(args[indexes[0] + 1]).resolve()
        details["projectPath"] = str(actual)
        if project_root is None:
            reasons.append("project_path_unavailable")
        elif paths_equal(actual, project_root):
            details["projectPathMatches"] = True
        else:
            reasons.append("project_path_mismatch")
    except (OSError, ValueError) as exc:
        reasons.append("command_line_parse_failed")
        details["parseError"] = str(exc)
    details["eligibleMainEditor"] = not reasons
    return details


def process_creation_time(pid: int, handle=None) -> int:
    if os.name != "nt" or pid <= 0:
        return 0
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel.GetProcessTimes.argtypes = [ctypes.c_void_p, *([ctypes.POINTER(ctypes.c_ulonglong)] * 4)]
    owned = handle is None
    handle = kernel.OpenProcess(0x1000, False, pid) if owned else handle
    if not handle:
        return 0
    try:
        times = [ctypes.c_ulonglong() for _ in range(4)]
        return times[0].value if kernel.GetProcessTimes(handle, *(ctypes.byref(value) for value in times)) else 0
    finally:
        if owned:
            kernel.CloseHandle(handle)


def process_exists(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def query_unity_processes() -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if os.name != "nt":
        return [], {
            "processQuerySucceeded": False,
            "processQueryError": "unsupported_platform",
            "processQueryExitCode": None,
        }
    command = (
        "@(Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | "
        "Select-Object ProcessId,CommandLine,ExecutablePath,"
        "@{Name='ProcessCreatedAt';Expression={$_.CreationDate.ToFileTimeUtc()}}) | ConvertTo-Json -Compress"
    )
    try:
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
            capture_output=True,
            text=True,
            timeout=5,
            check=False,
        )
        if completed.returncode != 0:
            return [], {
                "processQuerySucceeded": False,
                "processQueryError": (completed.stderr or "process query failed").strip()[:2048],
                "processQueryExitCode": completed.returncode,
            }
        if not completed.stdout.strip():
            return [], {"processQuerySucceeded": True, "processQueryError": "", "processQueryExitCode": 0}
        parsed = json.loads(completed.stdout)
        rows = parsed if isinstance(parsed, list) else [parsed]
        return [row for row in rows if isinstance(row, dict)], {
            "processQuerySucceeded": True,
            "processQueryError": "",
            "processQueryExitCode": 0,
        }
    except subprocess.TimeoutExpired:
        return [], {"processQuerySucceeded": False, "processQueryError": "process_query_timeout", "processQueryExitCode": None}
    except json.JSONDecodeError as exc:
        return [], {"processQuerySucceeded": False, "processQueryError": f"invalid_process_query_json: {exc}", "processQueryExitCode": None}
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        return [], {"processQuerySucceeded": False, "processQueryError": str(exc), "processQueryExitCode": None}
