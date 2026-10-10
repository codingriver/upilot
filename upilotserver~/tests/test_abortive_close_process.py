"""Isolated real-entrypoint/EXE hard-kill test; never discovers or kills by port/PID.

Startup liveness observations aren't execution retries. Each instance is started
once and each MCP business request is sent once. Only test-owned process handles
(or a private Windows job containing those descendants) can be terminated.
"""
from __future__ import annotations

import asyncio
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import tomllib

import httpx
import websockets
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client


SERVER_ROOT = Path(__file__).resolve().parents[1]


class _OwnedProcess:
    def __init__(self, command, *, cwd, env, output):
        self.job = None
        self.server_handle = None
        self.proc = subprocess.Popen(command, cwd=cwd, env=env, stdout=output, stderr=output,
                                     creationflags=0x4 if os.name == "nt" else 0)
        if os.name != "nt":
            return
        # PyInstaller onefile creates a second process. Attach while suspended,
        # before any descendants exist; no external process can enter our job.
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel = k
        class Basic(ctypes.Structure):
            _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
                        ("flags", wintypes.DWORD), ("minimum", ctypes.c_size_t),
                        ("maximum", ctypes.c_size_t), ("active", wintypes.DWORD),
                        ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD),
                        ("scheduling", wintypes.DWORD)]
        class Extended(ctypes.Structure):
            _fields_ = [("basic", Basic), ("io", ctypes.c_uint64 * 6),
                        ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                        ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]
        class ThreadEntry(ctypes.Structure):
            _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD),
                        ("tid", wintypes.DWORD), ("pid", wintypes.DWORD),
                        ("base", wintypes.LONG), ("delta", wintypes.LONG), ("flags", wintypes.DWORD)]
        signatures = {
            "CreateJobObjectW": ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
            "SetInformationJobObject": ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD], wintypes.BOOL),
            "AssignProcessToJobObject": ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
            "TerminateJobObject": ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
            "OpenProcess": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "IsProcessInJob": ([wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)], wintypes.BOOL),
            "WaitForSingleObject": ([wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            "CreateToolhelp32Snapshot": ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
            "Thread32First": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
            "Thread32Next": ([wintypes.HANDLE, ctypes.POINTER(ThreadEntry)], wintypes.BOOL),
            "OpenThread": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "ResumeThread": ([wintypes.HANDLE], wintypes.DWORD),
        }
        for name, (args, result) in signatures.items():
            getattr(k, name).argtypes, getattr(k, name).restype = args, result
        snapshot = None
        try:
            self.job = k.CreateJobObjectW(None, None)
            if not self.job:
                raise ctypes.WinError(ctypes.get_last_error())
            limits = Extended()
            limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if not k.SetInformationJobObject(self.job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
                raise ctypes.WinError(ctypes.get_last_error())
            if not k.AssignProcessToJobObject(self.job, int(self.proc._handle)):
                raise ctypes.WinError(ctypes.get_last_error())
            snapshot = k.CreateToolhelp32Snapshot(4, 0)
            if snapshot == ctypes.c_void_p(-1).value:
                snapshot = None
                raise ctypes.WinError(ctypes.get_last_error())
            entry = ThreadEntry()
            entry.size = ctypes.sizeof(entry)
            found = False
            more = k.Thread32First(snapshot, ctypes.byref(entry))
            while more:
                if entry.pid == self.proc.pid:  # exact test-owned suspended process
                    thread = k.OpenThread(2, False, entry.tid)
                    if not thread:
                        raise ctypes.WinError(ctypes.get_last_error())
                    try:
                        if k.ResumeThread(thread) == 0xFFFFFFFF:
                            raise ctypes.WinError(ctypes.get_last_error())
                        found = True
                    finally:
                        k.CloseHandle(thread)
                    break  # only the initial suspended thread can exist
                more = k.Thread32Next(snapshot, ctypes.byref(entry))
            if not found:
                raise RuntimeError("Owned suspended process has no initial thread")
        except BaseException:
            self.proc.kill()
            self.proc.wait(timeout=5)
            self.close()
            raise
        finally:
            if snapshot:
                k.CloseHandle(snapshot)

    def track_server(self, pid):
        if self.job is None:
            assert pid == self.proc.pid
            return
        handle = self.kernel.OpenProcess(0x100000 | 0x1000, False, pid)  # wait/query only
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        in_job = wintypes.BOOL()
        if not self.kernel.IsProcessInJob(handle, self.job, ctypes.byref(in_job)) or not in_job.value:
            self.kernel.CloseHandle(handle)
            raise RuntimeError("Observed Server is not in the test-owned job")
        self.server_handle = handle

    def wait(self):
        if self.server_handle is not None:
            result = self.kernel.WaitForSingleObject(self.server_handle, 5000)
            if result != 0:
                raise TimeoutError(f"Test-owned Server handle didn't exit within 5 seconds: {result}")
        self.proc.wait(timeout=5)

    def kill(self):
        if self.job is not None:
            if not self.kernel.TerminateJobObject(self.job, 1):
                raise ctypes.WinError(ctypes.get_last_error())
        else:
            self.proc.kill()

    def close(self):
        if self.server_handle is not None:
            self.kernel.CloseHandle(self.server_handle)
            self.server_handle = None
        if self.job is not None:
            self.kernel.CloseHandle(self.job)
            self.job = None


def _ports():
    held = [socket.socket(), socket.socket()]
    try:
        for sock in held:
            sock.bind(("127.0.0.1", 0))
        return [sock.getsockname()[1] for sock in held]
    finally:
        for sock in held:
            sock.close()


async def _observe_startup(owned, port):
    # Read-only liveness polling, fixed deadline; never restart or replay a tool.
    async with asyncio.timeout(20):
        async with httpx.AsyncClient(timeout=0.5, trust_env=False) as client:
            while True:
                if owned.proc.poll() is not None:
                    raise RuntimeError(f"Owned server exited during startup: {owned.proc.returncode}")
                try:
                    response = await client.get(f"http://127.0.0.1:{port}/health")
                except (httpx.ConnectError, httpx.TimeoutException):
                    await asyncio.sleep(0.05)
                    continue
                response.raise_for_status()
                return response.json()


async def _mcp_roundtrip(port):
    async with asyncio.timeout(10):
        async with streamablehttp_client(f"http://127.0.0.1:{port}/mcp") as (read, write, _):
            async with ClientSession(read, write) as session:
                initialized = await session.initialize()
                assert initialized.serverInfo.name
                result = await session.call_tool("unity_mcp_status", {})
                assert not result.isError
                payload = json.loads(result.content[0].text)
                assert payload["ok"] is True
                # No Unity is fabricated: only Server/MCP transport is tested.
                assert payload["data"]["connected"] is False


async def _active_http(port):
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    writer.write(b"GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n")
    await writer.drain()
    header = await reader.readuntil(b"\r\n\r\n")
    assert b"200 OK" in header
    length = next(int(line.split(b":", 1)[1]) for line in header.split(b"\r\n")
                  if line.lower().startswith(b"content-length:"))
    assert json.loads(await reader.readexactly(length))["status"] == "ok"
    return reader, writer


def test_ten_hard_kills_rebind_same_http_ws_ports_and_new_instances_communicate(tmp_path):
    exe = os.getenv("UPILOT_ABORTIVE_TEST_EXE", "")
    mode = "exe" if exe else "source"
    evidence = Path(os.getenv("UPILOT_ABORTIVE_TEST_ARTIFACTS", str(tmp_path))) / mode
    evidence.mkdir(parents=True, exist_ok=True)
    http_port, ws_port = _ports()
    project = tmp_path / "isolated-project"
    config_path = project / ".upilot" / "config.json"
    config_path.parent.mkdir(parents=True)
    config_path.write_text(json.dumps({"mcp": {"httpPort": http_port, "wsPort": ws_port}}))
    command = ([str(Path(exe).resolve())] if exe else
               [sys.executable, "-m", "upilot_mcp.mcp_main"])
    command += ["--transport", "http", "--host", "127.0.0.1", "--port", str(ws_port),
                "--http-host", "127.0.0.1", "--http-port", str(http_port)]
    env = {key: value for key, value in os.environ.items() if not key.startswith("UPILOT_")}
    env.update(UPILOT_CONFIG=str(config_path), UPILOT_LOG_LEVEL="INFO",
               PYTHONPATH=str(SERVER_ROOT / "src"))
    expected_version = tomllib.loads((SERVER_ROOT / "pyproject.toml").read_text())["project"]["version"]
    records = []

    async def exercise():
        # 11 instances prove all 10 killed instances have a communicating successor.
        for index in range(11):
            record = {"instance": index, "stage": "spawn", "http_port": http_port, "ws_port": ws_port}
            records.append(record)
            owned = None
            writer = websocket = None
            with (evidence / f"instance-{index:02d}.log").open("wb") as log:
                try:
                    owned = _OwnedProcess(command, cwd=str(project), env=env, output=log)
                    record["launcher_pid"] = owned.proc.pid
                    record["stage"] = "observe_startup"
                    health = await _observe_startup(owned, http_port)
                    assert health["server_version"] == expected_version
                    assert Path(health["configured_project_path"]).resolve() == project.resolve()
                    record["server_pid"] = health["server_pid"]
                    owned.track_server(health["server_pid"])
                    record["build_commit"] = health["build_commit"]
                    record["server_instance_id"] = health["server_instance_id"]
                    record["stage"] = "normal_communication"
                    await _mcp_roundtrip(http_port)
                    async with asyncio.timeout(5):
                        reader, writer = await _active_http(http_port)
                        websocket = await websockets.connect(f"ws://127.0.0.1:{ws_port}", close_timeout=0.1)
                        pong = await websocket.ping(b"test-owned-ping")
                        await pong
                    record["http_mcp_roundtrip"] = record["ws_ping_pong"] = True
                    record["stage"] = "hard_kill"
                    before = time.monotonic()
                    owned.kill()  # no shutdown request; only our own process/job
                    await asyncio.to_thread(owned.wait)
                    record["exit_ms"] = round((time.monotonic() - before) * 1000, 2)
                    record["exit_observed"] = True
                    record["server_exit_observed"] = True
                    async with asyncio.timeout(5):
                        try:
                            assert await reader.read() == b""
                            record["http_close"] = "eof"
                        except ConnectionResetError:
                            record["http_close"] = "reset"
                        await websocket.wait_closed()
                    record["stage"] = "rebind"
                    for port in (http_port, ws_port):
                        with socket.socket() as probe:
                            # No reuse options: must bind without stealing ownership.
                            probe.bind(("127.0.0.1", port))
                    record["both_ports_rebound"] = True
                    record["stage"] = "passed"
                except BaseException as exc:
                    record.update(error=repr(exc), errno=getattr(exc, "errno", None),
                                  winerror=getattr(exc, "winerror", None))
                    raise
                finally:
                    if owned is not None:
                        if owned.proc.poll() is None:
                            owned.kill()
                            await asyncio.to_thread(owned.wait)
                        owned.close()
                    if websocket is not None:
                        websocket.transport.abort()
                    if writer is not None:
                        writer.close()
                        try:
                            await writer.wait_closed()
                        except (ConnectionResetError, BrokenPipeError):
                            pass
                    (evidence / "hard-kill-rebind.json").write_text(json.dumps({
                        "mode": mode, "platform": sys.platform, "command": command,
                        "rounds": records, "restart_retries": 0, "business_replays": 0,
                    }, indent=2), encoding="utf-8")
    asyncio.run(exercise())
    assert len(records) == 11
    assert len({record["server_instance_id"] for record in records}) == 11
    assert all(record["stage"] == "passed" for record in records)
    for index in range(10):
        records[index]["successor_verified"] = records[index + 1]["http_mcp_roundtrip"]
    (evidence / "hard-kill-rebind.json").write_text(json.dumps({
        "mode": mode, "platform": sys.platform, "command": command,
        "rounds": records, "restart_retries": 0, "business_replays": 0,
    }, indent=2), encoding="utf-8")
