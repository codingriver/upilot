from __future__ import annotations

import asyncio
import json
import subprocess
import sys
import time
from pathlib import Path

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

WORKSPACE = Path(__file__).resolve().parent.parent
SERVER_SCRIPT = WORKSPACE / "run_unitypilot_mcp.py"
UNITY_PORT = 8765
HTTP_PORT = 8011
LOG_FILE = WORKSPACE / "http-test-8011.log"

async def call_status_until_connected(timeout_seconds: float = 30.0, interval_seconds: float = 3.0) -> tuple[dict, list[dict]]:
    url = f"http://127.0.0.1:{HTTP_PORT}/mcp"
    deadline = time.time() + timeout_seconds
    attempts: list[dict] = []

    async with streamablehttp_client(url) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            while True:
                result = await session.call_tool("unity_mcp_status", {})
                text = result.content[0].text if result.content else ""
                payload = json.loads(text)
                attempts.append(payload)
                if payload.get("data", {}).get("connected"):
                    return payload, attempts
                if time.time() >= deadline:
                    return payload, attempts
                await asyncio.sleep(interval_seconds)

def main() -> int:
    try:
        LOG_FILE.unlink(missing_ok=True)
    except PermissionError:
        pass

    cmd = [
        sys.executable,
        str(SERVER_SCRIPT),
        "--transport",
        "http",
        "--port",
        str(UNITY_PORT),
        "--http-port",
        str(HTTP_PORT),
        "--log-file",
        str(LOG_FILE),
        "--log-level",
        "DEBUG",
    ]

    proc = subprocess.Popen(
        cmd,
        cwd=str(WORKSPACE),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    try:
        time.sleep(3)
        final_payload, attempts = asyncio.run(call_status_until_connected(timeout_seconds=45.0, interval_seconds=3.0))
        print(json.dumps({"attempts": attempts, "final": final_payload}, ensure_ascii=False, indent=2))
        connected = final_payload.get("data", {}).get("connected") is True
        server_ready = final_payload.get("data", {}).get("serverReady") is True
        if connected and server_ready:
            print("[OK] HTTP unity_mcp_status test passed")
            return 0
        print("[FAIL] HTTP unity_mcp_status did not report connected/serverReady within timeout")
        return 1
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

if __name__ == "__main__":
    raise SystemExit(main())
