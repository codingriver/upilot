from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
import sys
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated, Any

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.server import StreamableHTTPASGIApp
from mcp.shared.session import BaseSession
from pydantic import Field
from starlette.applications import Starlette
import starlette.routing
from uvicorn import Config, Server
from anyio import ClosedResourceError
from mcp.types import CallToolResult, TextContent

from .env import getenv
from .server import WsOrchestratorServer
from .tool_facade import McpToolFacade
from .models import ToolResponse
from .protocol import new_id
from .responses import fail, ok
from .config import CONFIG
from .tool_registry import REGISTRY, REGISTRY_VERSION, register_public_tool

logger = logging.getLogger("upilot.mcp")
stdio_logger = logging.getLogger("upilot.stdio")

# ── Patch streamable-http closed-stream race ---------------------------------
_original_base_session_send_response = BaseSession._send_response


async def _patched_base_session_send_response(self, request_id, response):
    try:
        await _original_base_session_send_response(self, request_id, response)
    except ClosedResourceError:
        # Client already closed the streamable HTTP session (e.g. DELETE /mcp).
        # This is a benign race; swallow it so the session doesn't crash.
        logger.debug(
            "Ignoring ClosedResourceError for request %s (stream already closed)",
            request_id,
        )
    except Exception:
        # Re-raise anything else so genuine bugs are not hidden.
        raise


BaseSession._send_response = _patched_base_session_send_response

# ── Patch MCP SDK stateless request leak -------------------------------------
# MCP SDK 1.27.0 _handle_stateless_request doesn't use try/finally around
# handle_request, so if it raises (client disconnect, ASGI error, etc.)
# terminate() is never called and app.run() tasks leak in the task group.
# Over time this can exhaust the task group and cause all new HTTP requests
# to hang.
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager

_original_handle_stateless_request = StreamableHTTPSessionManager._handle_stateless_request


async def _patched_handle_stateless_request(self, scope, receive, send):
    logger.debug("Stateless mode: Creating new transport for this request")
    from mcp.server.streamable_http import StreamableHTTPServerTransport
    from anyio import TASK_STATUS_IGNORED
    from starlette.requests import Request

    http_transport = StreamableHTTPServerTransport(
        mcp_session_id=None,
        is_json_response_enabled=self.json_response,
        event_store=None,
        security_settings=self.security_settings,
    )

    async def run_stateless_server(*, task_status=TASK_STATUS_IGNORED):
        async with http_transport.connect() as streams:
            read_stream, write_stream = streams
            task_status.started()
            try:
                await self.app.run(
                    read_stream,
                    write_stream,
                    self.app.create_initialization_options(),
                    stateless=True,
                )
            except Exception:
                logger.exception("Stateless session crashed")

    assert self._task_group is not None
    await self._task_group.start(run_stateless_server)
    try:
        await http_transport.handle_request(scope, receive, send)
    finally:
        await http_transport.terminate()


StreamableHTTPSessionManager._handle_stateless_request = _patched_handle_stateless_request

_MCP_TRANSPORTS = {"stdio", "http"}
_WS_START_TIMEOUT_S = 10.0
_SERVER_STARTED_AT = time.time()


def _log_stdio_message(direction: str, tool_name: str, payload: str) -> None:
    stdio_logger.debug("STDIO %s %s %s", direction, tool_name, payload)


def _resolve_transport() -> str:
    transport = (
        getenv("UPILOT_TRANSPORT", "stdio").strip().lower() or "stdio"
    )

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--transport" and i + 1 < len(args):
            transport = args[i + 1].strip().lower() or "stdio"
            i += 2
        else:
            i += 1

    if transport not in _MCP_TRANSPORTS:
        logger.warning("Invalid transport value: %s, falling back to stdio", transport)
        return "stdio"
    return transport


def _resolve_http_config() -> tuple[str, int]:
    host = getenv("UPILOT_HTTP_HOST", CONFIG.http_host)
    port_str = getenv("UPILOT_HTTP_PORT", str(CONFIG.http_port))

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--http-host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif args[i] == "--http-port" and i + 1 < len(args):
            port_str = args[i + 1]
            i += 2
        else:
            i += 1

    try:
        port = int(port_str)
    except ValueError:
        logger.error("Invalid HTTP port value: %s, falling back to 8011", port_str)
        port = 8011

    return host, port


# ── Shared server state ──────────────────────────────────────────────────────

_orchestrator: WsOrchestratorServer | None = None
_facade: McpToolFacade | None = None
_http_server: Server | None = None


def _resolve_config() -> tuple[str, int]:
    """Resolve host/port from CLI args or UPILOT_* env vars."""
    host = getenv("UPILOT_HOST", CONFIG.ws_host)
    port_str = getenv("UPILOT_PORT", str(CONFIG.ws_port))

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif args[i] == "--port" and i + 1 < len(args):
            port_str = args[i + 1]
            i += 2
        else:
            i += 1

    try:
        port = int(port_str)
    except ValueError:
        logger.error("Invalid port value: %s, falling back to 8765", port_str)
        port = 8765

    return host, port


def _workspace_folder_label() -> str:
    """Folder name of the current working directory (typically the Cursor workspace root)."""
    try:
        name = Path.cwd().resolve().name
    except OSError:
        return ""
    return (name or "").strip()[:256]


def _resolve_mcp_label() -> str:
    """Display name for Unity / diagnostics.

    If ``--label`` is present on the command line, its value is used; otherwise the
    current working directory's folder name (normally the Cursor workspace root).
    """
    args = sys.argv[1:]
    cli_label: str | None = None
    i = 0
    while i < len(args):
        if args[i] == "--label" and i + 1 < len(args):
            cli_label = args[i + 1].strip()
            i += 2
        else:
            i += 1

    if cli_label is not None:
        return cli_label[:256]

    return _workspace_folder_label()


async def _wait_for_ws_listener(
    server: WsOrchestratorServer, timeout_s: float = _WS_START_TIMEOUT_S
) -> None:
    ready = await server.wait_until_listening(timeout_s=timeout_s)
    if ready:
        logger.info(
            "upilot Unity bridge listener ready  ws=%s:%s", server.host, server.port
        )
        return

    raise RuntimeError(
        f"Unity bridge WebSocket failed to listen on {server.host}:{server.port} within {timeout_s:.1f}s"
    )


async def _run_http_server(
    http_host: str, http_port: int, ws_task: asyncio.Task[None]
) -> None:
    global _http_server
    mcp.settings.host = http_host
    mcp.settings.port = http_port
    app = mcp.streamable_http_app()
    session_manager = mcp.session_manager
    streamable_app = StreamableHTTPASGIApp(session_manager)

    @contextlib.asynccontextmanager
    async def combined_lifespan(_app: Starlette):
        async with session_manager.run():
            try:
                await _wait_for_ws_listener(_orchestrator)
                yield
            except Exception:
                if not ws_task.done():
                    raise
                await ws_task
                raise
            finally:
                if _http_server is not None:
                    _http_server.should_exit = True
                _orchestrator.stop()
                try:
                    await ws_task
                except asyncio.CancelledError:
                    raise
                except Exception:
                    logger.exception(
                        "Unity bridge WebSocket task terminated with error during HTTP shutdown"
                    )

    wrapped_app = Starlette(
        debug=app.debug,
        routes=list(app.routes),
        middleware=list(app.user_middleware),
        lifespan=combined_lifespan,
    )

    for route in wrapped_app.router.routes:
        if getattr(route, "path", None) == mcp.settings.streamable_http_path:
            route.app = streamable_app

    # Landing endpoint for humans/tools that probe the server root.
    # The MCP Streamable HTTP transport remains mounted at /mcp.
    async def root_endpoint(request):
        from starlette.responses import JSONResponse

        endpoint = str(request.url.replace(path=mcp.settings.streamable_http_path, query=""))
        return JSONResponse({
            "status": "ok",
            "name": "UPilot",
            "message": "UPilot MCP server is running. Configure MCP clients to use the mcpEndpoint URL.",
            "mcpEndpoint": endpoint,
            "health": str(request.url.replace(path="/health", query="")),
            "stats": str(request.url.replace(path="/stats", query="")),
        })

    # Friendly endpoint for clients that probe /mcp with a browser-like GET.
    # Actual MCP JSON-RPC requests must still use POST against the same path.
    async def mcp_get_endpoint(request):
        from starlette.responses import JSONResponse

        endpoint = str(request.url.replace(path=mcp.settings.streamable_http_path, query=""))
        return JSONResponse({
            "status": "ok",
            "name": "UPilot",
            "message": "This is the UPilot MCP endpoint. GET is informational only; MCP JSON-RPC calls require POST.",
            "mcpEndpoint": endpoint,
            "requiredMethod": "POST",
            "requiredHeaders": {
                "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream",
            },
            "health": str(request.url.replace(path="/health", query="")),
            "example": {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {},
            },
        })

    # Add a simple health endpoint for liveness probes
    async def health_endpoint(request):
        from starlette.responses import JSONResponse
        session = _orchestrator.session_manager.active if _orchestrator else None
        now_ms = int(time.time() * 1000)
        heartbeat_at = int(session.last_heartbeat_at) if session else 0
        return JSONResponse({
            "status": "ok",
            "unity_connected": _orchestrator.is_ready() if _orchestrator else False,
            "project_path": session.project_path if session else "",
            "unity_version": session.unity_version if session else "",
            "heartbeat_age_ms": max(0, now_ms - heartbeat_at) if heartbeat_at else None,
            "tool_count": len([item for item in REGISTRY.list() if item.feature == "core" or CONFIG.flow_enabled]),
            "registry_version": REGISTRY_VERSION,
            "server_uptime_ms": max(0, int((time.time() - _SERVER_STARTED_AT) * 1000)),
            "http_port": http_port,
            "ws_port": _orchestrator.port if _orchestrator else CONFIG.ws_port,
            "flow_enabled": CONFIG.flow_enabled,
            "timestamp": time.time(),
        })

    # Stats endpoint for the upilot status window
    async def stats_endpoint(request):
        from starlette.responses import JSONResponse
        ws_count = _orchestrator.ws_connection_count if _orchestrator else 0
        http_sessions = 0
        try:
            sm = getattr(mcp, "session_manager", None)
            if sm is not None:
                http_sessions = len(getattr(sm, "_sessions", {}))
        except Exception:
            pass
        return JSONResponse({
            "ws_connections": ws_count,
            "http_sessions": http_sessions,
            "tool_count": len([item for item in REGISTRY.list() if item.feature == "core" or CONFIG.flow_enabled]),
            "registry_version": REGISTRY_VERSION,
            "server_uptime_ms": max(0, int((time.time() - _SERVER_STARTED_AT) * 1000)),
            "project_path": (_orchestrator.session_manager.active.project_path
                             if _orchestrator and _orchestrator.session_manager.active else ""),
        })

    wrapped_app.router.routes.insert(
        0,
        starlette.routing.Route(
            mcp.settings.streamable_http_path,
            endpoint=mcp_get_endpoint,
            methods=["GET"],
        )
    )
    wrapped_app.router.routes.append(
        starlette.routing.Route("/", endpoint=root_endpoint)
    )
    wrapped_app.router.routes.append(
        starlette.routing.Route("/health", endpoint=health_endpoint)
    )
    wrapped_app.router.routes.append(
        starlette.routing.Route("/stats", endpoint=stats_endpoint)
    )

    config = Config(
        app=wrapped_app,
        host=http_host,
        port=http_port,
        log_level=mcp.settings.log_level.lower(),
        timeout_keep_alive=30,
        timeout_notify=30,
        access_log=True,
    )
    server = Server(config)
    _http_server = server
    try:
        await server.serve()
    finally:
        if _http_server is server:
            _http_server = None


@asynccontextmanager
async def _lifespan(app: FastMCP):
    if _resolve_transport() == "http":
        yield
        return

    global _orchestrator, _facade
    host, port = _resolve_config()
    mcp_label = _resolve_mcp_label()
    _orchestrator = WsOrchestratorServer(host=host, port=port, mcp_label=mcp_label)
    _facade = McpToolFacade(_orchestrator)
    task = asyncio.create_task(
        _orchestrator.start(), name=f"upilot-ws-{host}:{port}"
    )
    try:
        await _wait_for_ws_listener(_orchestrator)
    except Exception:
        logger.exception(
            "Failed to start Unity bridge WebSocket listener  ws=%s:%s", host, port
        )
        _orchestrator.stop()
        try:
            await task
        except Exception:
            logger.exception(
                "Unity bridge WebSocket task terminated with error during startup"
            )
        raise

    logger.info("UPilot MCP server started  ws=%s:%s", host, port)
    try:
        yield
    finally:
        _orchestrator.stop()
        try:
            await task
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.exception(
                "Unity bridge WebSocket task terminated with error during shutdown"
            )


mcp = FastMCP("upilot", lifespan=_lifespan, stateless_http=True)

_PLAYMODE_HIDDEN_TOOLS = {
    "unity_compile",
    "unity_auto_fix_start",
    "unity_safe_compile_and_wait",
}


def _get_facade() -> McpToolFacade:
    if _facade is None:
        raise RuntimeError("Server not initialized")
    return _facade


def _response_context() -> dict[str, Any]:
    if _facade is None:
        return {
            "unityConnected": False,
            "playModeState": "unknown",
            "isPlaying": False,
            "isPaused": False,
            "isCompiling": False,
            "compileStatus": "unknown",
            "compileErrorCount": 0,
            "compileWarningCount": 0,
            "activeScene": "",
            "sessionId": "",
            "lastHeartbeatAt": 0,
            "source": "unavailable",
            "updatedAt": 0,
            "ageMs": 0,
            "isStale": True,
            "timestamp": int(time.time() * 1000),
        }

    server = _facade.server
    session = server.session_manager.active
    editor = server.state.editor
    compile_state = server.state.compile
    play_mode_state = editor.play_mode_state or "unknown"
    now = int(time.time() * 1000)
    updated_at = int(editor.updated_at or 0)
    age_ms = max(0, now - updated_at) if updated_at else 0

    return {
        "unityConnected": server.is_ready(),
        "playModeState": play_mode_state,
        "isPlaying": play_mode_state == "play",
        "isPaused": play_mode_state == "pause",
        "isCompiling": editor.is_compiling,
        "compileStatus": compile_state.status,
        "compileErrorCount": compile_state.error_count,
        "compileWarningCount": compile_state.warning_count,
        "activeScene": editor.active_scene,
        "sessionId": session.session_id if session else "",
        "lastHeartbeatAt": session.last_heartbeat_at if session else 0,
        "source": "cache",
        "updatedAt": updated_at,
        "ageMs": age_ms,
        "isStale": not updated_at or age_ms > CONFIG.context_stale_ms,
        "timestamp": now,
    }


def _payload(r) -> CallToolResult:
    response_context = getattr(r, "context", None) or _response_context()
    structured = {
        "schemaVersion": 2,
        "ok": r.ok,
        "data": r.data,
        "context": response_context,
        "timing": getattr(r, "timing", None) or {"totalMs": 0, "queueMs": 0, "bridgeMs": 0, "unityExecutionMs": 0},
        "error": (
            {
                "code": r.error.code,
                "message": r.error.message,
                "detail": r.error.detail,
            }
            if r.error
            else None
        ),
        "requestId": r.request_id,
        "timestamp": r.timestamp,
    }
    text_payload = json.dumps(structured, ensure_ascii=False)
    return CallToolResult(
        content=[TextContent(type="text", text=text_payload)],
        structuredContent=structured,
        isError=not r.ok,
    )


def _state_is_playmode(state: str | None) -> bool:
    return (state or "").strip().lower() in {"play", "playing", "pause", "paused"}


async def _unity_is_playmode() -> bool:
    if _facade is None:
        return False

    try:
        state = await _facade.editor_state()
        if state.ok and state.data:
            if bool(state.data.get("isPlaying", False)) or bool(
                state.data.get("isPaused", False)
            ):
                return True
            return _state_is_playmode(str(state.data.get("playModeState", "")))
    except Exception as ex:
        logger.debug("Failed to query Unity play mode for MCP tool filtering: %s", ex)

    editor = _facade.server.state.editor
    return _state_is_playmode(editor.play_mode_state)


async def _reject_compile_in_playmode(tool_name: str):
    if not await _unity_is_playmode():
        return None
    return _log_tool_result(
        tool_name,
        _payload(
            fail(
                new_id("req"),
                "EDITOR_IN_PLAY_MODE",
                "Unity 正在 PlayMode 或暂停状态，MCP 不允许触发 Unity 编译。",
                {"tool": tool_name},
            )
        ),
    )


def _log_tool_result(tool_name: str, result_payload: CallToolResult | str):
    if isinstance(result_payload, CallToolResult):
        log_text = result_payload.content[0].text if result_payload.content else ""
    else:
        log_text = result_payload
    _log_stdio_message("RESULT", tool_name, log_text)
    return result_payload


def _log_tool_call(tool_name: str, args: dict[str, Any]) -> None:
    _log_stdio_message("CALL", tool_name, json.dumps(args, ensure_ascii=False))


# ── Tool definitions ─────────────────────────────────────────────────────────
# Domain tool modules register themselves against the shared FastMCP instance.
from .mcp_tools import status_tools as _status_tools
from .mcp_tools import compile_tools as _compile_tools
from .mcp_tools import task_tools as _task_tools
from .mcp_tools import screenshot_tools as _screenshot_tools
from .mcp_tools import resource_tools as _resource_tools
from .mcp_tools import reflection_tools as _reflection_tools
from .mcp_tools import test_tools as _test_tools
from .mcp_tools import build_tools as _build_tools
from .mcp_tools import flow_tools as _flow_tools



































# --- UIToolkit 相关 MCP 工具已关闭：unity_uitoolkit_*、unity_wait_condition（恢复请查 git 历史） ---




























# ── M07 Console 日志读取 ─────────────────────────────────────────────────────










# ── M08 GameObject 操作 ──────────────────────────────────────────────────────














# ── M09 Scene 管理 ──────────────────────────────────────────────────────────


















# ── M10 Component 操作 ──────────────────────────────────────────────────────












# ── M11 截图能力 ────────────────────────────────────────────────────────────










# ── M12 Asset 管理 ──────────────────────────────────────────────────────────
























# ── M13 Prefab 操作 ─────────────────────────────────────────────────────────












# ── M14 Material 与 Shader ──────────────────────────────────────────────────












# ── M15 菜单项执行 ──────────────────────────────────────────────────────────






# ── M16 Package 管理 ────────────────────────────────────────────────────────










# ── M17 测试运行 ────────────────────────────────────────────────────────────








# ── M18 脚本读写 ────────────────────────────────────────────────────────────










# ── M20 反射调用 ────────────────────────────────────────────────────────────








# ── M21 批量操作 ────────────────────────────────────────────────────────────








# ── M22 Selection 管理 ──────────────────────────────────────────────────────








# ── M23 MCP Resources ──────────────────────────────────────────────────────






















# ── M26 验收自动化工具 ─────────────────────────────────────────────────────














# ── M24 Build Pipeline ─────────────────────────────────────────────────────










# ── M25 Editor Commands ─────────────────────────────────────────────────────
































_original_mcp_list_tools = mcp.list_tools
_HIDDEN_PUBLIC_TOOLS = {"unity_upilot_flow_run_batch"}


async def _list_tools_stable():
    tools = await _original_mcp_list_tools()
    tools = [tool for tool in tools if tool.name not in _HIDDEN_PUBLIC_TOOLS]
    if CONFIG.flow_enabled:
        return tools
    return [tool for tool in tools if not tool.name.startswith("unity_upilot_flow_")]


mcp.list_tools = _list_tools_stable
mcp._mcp_server.list_tools()(_list_tools_stable)


_DESTRUCTIVE_TOOLS = {
    "unity_asset_delete", "unity_asset_move", "unity_asset_modify_data",
    "unity_script_create", "unity_script_update", "unity_script_delete",
    "unity_package_add", "unity_package_remove", "unity_scene_save",
    "unity_scene_unload", "unity_gameobject_delete", "unity_component_remove",
}

for _tool_name, _value in list(globals().items()):
    if not callable(_value):
        continue
    if not (_tool_name.startswith("unity_") or _tool_name == "reflection_eval"):
        continue
    if _tool_name in _HIDDEN_PUBLIC_TOOLS:
        continue
    register_public_tool(
        _tool_name,
        destructive=_tool_name in _DESTRUCTIVE_TOOLS,
        idempotent=_tool_name not in _DESTRUCTIVE_TOOLS,
        play_mode_policy="blocked" if _tool_name in _PLAYMODE_HIDDEN_TOOLS else "allowed",
        feature="flow" if _tool_name.startswith("unity_upilot_flow_") else "core",
    )


# ── Entry point ───────────────────────────────────────────────────────────────


async def main() -> None:
    transport = _resolve_transport()
    ws_host, ws_port = _resolve_config()

    if transport == "http":
        global _orchestrator, _facade
        http_host, http_port = _resolve_http_config()
        _orchestrator = WsOrchestratorServer(
            host=ws_host, port=ws_port, mcp_label=_resolve_mcp_label()
        )
        _facade = McpToolFacade(_orchestrator)
        ws_task = asyncio.create_task(
            _orchestrator.start(), name=f"upilot-ws-{ws_host}:{ws_port}"
        )
        try:
            await _wait_for_ws_listener(_orchestrator)
            logger.info(
                "UPilot MCP server started  transport=%s  ws=%s:%s  http=%s:%s  path=%s",
                transport,
                ws_host,
                ws_port,
                http_host,
                http_port,
                "/mcp",
            )
            await _run_http_server(http_host, http_port, ws_task)
        finally:
            if _http_server is not None:
                _http_server.should_exit = True
        return

    logger.info(
        "UPilot MCP server started  transport=%s  ws=%s:%s",
        transport,
        ws_host,
        ws_port,
    )
    await mcp.run_stdio_async()


if __name__ == "__main__":
    asyncio.run(main())
