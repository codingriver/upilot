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

from .server import WsOrchestratorServer
from .tool_facade import McpToolFacade
from .models import ToolResponse
from .protocol import new_id
from .responses import fail, ok

logger = logging.getLogger("unitypilot.mcp")
stdio_logger = logging.getLogger("unitypilot.stdio")

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


def _log_stdio_message(direction: str, tool_name: str, payload: str) -> None:
    stdio_logger.debug("STDIO %s %s %s", direction, tool_name, payload)


def _resolve_transport() -> str:
    transport = (
        os.environ.get("UNITYPILOT_TRANSPORT", "stdio").strip().lower() or "stdio"
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
    host = os.environ.get("UNITYPILOT_HTTP_HOST", "127.0.0.1")
    port_str = os.environ.get("UNITYPILOT_HTTP_PORT", "8000")

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
        logger.error("Invalid HTTP port value: %s, falling back to 8000", port_str)
        port = 8000

    return host, port


# ── Shared server state ──────────────────────────────────────────────────────

_orchestrator: WsOrchestratorServer | None = None
_facade: McpToolFacade | None = None
_http_server: Server | None = None


def _resolve_config() -> tuple[str, int]:
    """Resolve host/port from CLI args (--host/--port) or env vars (UNITYPILOT_HOST/UNITYPILOT_PORT)."""
    host = os.environ.get("UNITYPILOT_HOST", "127.0.0.1")
    port_str = os.environ.get("UNITYPILOT_PORT", "8765")

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
            "unitypilot Unity bridge listener ready  ws=%s:%s", server.host, server.port
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
            "name": "upilot",
            "message": "upilot MCP server is running. Configure MCP clients to use the mcpEndpoint URL.",
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
            "name": "upilot",
            "message": "This is the upilot MCP endpoint. GET is informational only; MCP JSON-RPC calls require POST.",
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
        return JSONResponse({
            "status": "ok",
            "unity_connected": _orchestrator.is_ready() if _orchestrator else False,
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
        _orchestrator.start(), name=f"unitypilot-ws-{host}:{port}"
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

    logger.info("unitypilot MCP server started  ws=%s:%s", host, port)
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
    "unity_roslyn_execute",
    "unity_roslyn_status",
    "unity_roslyn_abort",
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
            "timestamp": int(time.time() * 1000),
        }

    server = _facade.server
    session = server.session_manager.active
    editor = server.state.editor
    compile_state = server.state.compile
    play_mode_state = editor.play_mode_state or "unknown"

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
        "timestamp": int(time.time() * 1000),
    }


def _payload(r) -> str:
    response_context = getattr(r, "context", None) or _response_context()
    return json.dumps(
        {
            "ok": r.ok,
            "data": r.data,
            "context": response_context,
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
        },
        ensure_ascii=False,
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


async def _reject_roslyn_in_playmode(tool_name: str):
    if not await _unity_is_playmode():
        return None
    return _log_tool_result(
        tool_name,
        _payload(
            fail(
                new_id("req"),
                "EDITOR_IN_PLAY_MODE",
                "Unity 正在 PlayMode 或暂停状态，Roslyn 动态代码执行工具不可用。",
                {"tool": tool_name},
            )
        ),
    )


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


def _log_tool_result(tool_name: str, result_payload: str) -> str:
    _log_stdio_message("RESULT", tool_name, result_payload)
    return result_payload


def _log_tool_call(tool_name: str, args: dict[str, Any]) -> None:
    _log_stdio_message("CALL", tool_name, json.dumps(args, ensure_ascii=False))


# ── Tool definitions ─────────────────────────────────────────────────────────


@mcp.tool(
    description="检查 Unity 连接并返回会话信息。command 留空或仅空白：不启动进程，仅等待已有 Unity 连接 Bridge（手动打开项目）。非空则 shell 启动后再等待连接。"
)
async def unity_open_editor(command: str = "", waitForConnectMs: int = 60000) -> str:
    _log_tool_call(
        "unity_open_editor", {"command": command, "waitForConnectMs": waitForConnectMs}
    )
    r = await _get_facade().open_editor(
        command=command, wait_for_connect_ms=waitForConnectMs
    )
    return _log_tool_result("unity_open_editor", _payload(r))


@mcp.tool(description="触发 Unity 编译。")
async def unity_compile() -> str:
    _log_tool_call("unity_compile", {})
    rejected = await _reject_compile_in_playmode("unity_compile")
    if rejected is not None:
        return rejected
    r = await _get_facade().compile()
    return _log_tool_result("unity_compile", _payload(r))


@mcp.tool(description="获取最近一次编译状态。")
async def unity_compile_status(compileRequestId: str = "") -> str:
    _log_tool_call("unity_compile_status", {"compileRequestId": compileRequestId})
    r = await _get_facade().compile_status(compile_request_id=compileRequestId)
    return _log_tool_result("unity_compile_status", _payload(r))


@mcp.tool(description="获取最近一次结构化编译错误（仅 live，不回退缓存）。")
async def unity_compile_errors(compileRequestId: str = "") -> str:
    _log_tool_call("unity_compile_errors", {"compileRequestId": compileRequestId})
    r = await _get_facade().compile_errors(compile_request_id=compileRequestId)
    return _log_tool_result("unity_compile_errors", _payload(r))


@mcp.tool(
    description=(
        "诊断 MCP 连接/会话/超时/编译状态。"
        "返回 paths.unityProjectAbsolute（当前 Unity 工程绝对路径）与 paths.mcpProcessWorkingDirectory（MCP Python 进程当前工作目录，多为 Cursor 工作区根目录）。"
    ),
)
async def unity_mcp_status() -> str:
    _log_tool_call("unity_mcp_status", {})
    r = await _get_facade().mcp_status()
    return _log_tool_result("unity_mcp_status", _payload(r))


@mcp.tool(description="启动自动修复循环。")
async def unity_auto_fix_start(
    maxIterations: int = 20, stopWhenNoError: bool = True
) -> str:
    _log_tool_call(
        "unity_auto_fix_start",
        {"maxIterations": maxIterations, "stopWhenNoError": stopWhenNoError},
    )
    rejected = await _reject_compile_in_playmode("unity_auto_fix_start")
    if rejected is not None:
        return rejected
    r = await _get_facade().auto_fix_start(
        max_iterations=maxIterations, stop_when_no_error=stopWhenNoError
    )
    return _log_tool_result("unity_auto_fix_start", _payload(r))


@mcp.tool(description="停止自动修复循环。")
async def unity_auto_fix_stop(loopId: str) -> str:
    _log_tool_call("unity_auto_fix_stop", {"loopId": loopId})
    r = await _get_facade().auto_fix_stop(loop_id=loopId)
    return _log_tool_result("unity_auto_fix_stop", _payload(r))


@mcp.tool(description="读取自动修复循环状态。")
async def unity_auto_fix_status() -> str:
    _log_tool_call("unity_auto_fix_status", {})
    r = await _get_facade().auto_fix_status()
    return _log_tool_result("unity_auto_fix_status", _payload(r))


@mcp.tool(description="进入 PlayMode。")
async def unity_playmode_start() -> str:
    _log_tool_call("unity_playmode_start", {})
    r = await _get_facade().playmode_start()
    return _log_tool_result("unity_playmode_start", _payload(r))


@mcp.tool(description="退出 PlayMode。")
async def unity_playmode_stop() -> str:
    _log_tool_call("unity_playmode_stop", {})
    r = await _get_facade().playmode_stop()
    return _log_tool_result("unity_playmode_stop", _payload(r))


@mcp.tool(
    description="执行 Unity 编辑器鼠标动作。支持 elementName 按名称自动定位元素中心坐标（无需手动算坐标）。"
)
async def unity_mouse_event(
    action: str,
    button: str,
    x: float = 0,
    y: float = 0,
    targetWindow: str = "",
    modifiers: list[str] | None = None,
    scrollDeltaX: float = 0.0,
    scrollDeltaY: float = 0.0,
    elementName: str = "",
    elementIndex: int = -1,
) -> str:
    _log_tool_call(
        "unity_mouse_event",
        {
            "action": action,
            "button": button,
            "x": x,
            "y": y,
            "targetWindow": targetWindow,
            "modifiers": modifiers,
            "scrollDeltaX": scrollDeltaX,
            "scrollDeltaY": scrollDeltaY,
            "elementName": elementName,
            "elementIndex": elementIndex,
        },
    )
    r = await _get_facade().mouse_event(
        action=action,
        button=button,
        x=x,
        y=y,
        target_window=targetWindow,
        modifiers=modifiers,
        scroll_delta_x=scrollDeltaX,
        scroll_delta_y=scrollDeltaY,
        element_name=elementName,
        element_index=elementIndex,
    )
    return _log_tool_result("unity_mouse_event", _payload(r))


# --- UIToolkit 相关 MCP 工具已关闭：unity_uitoolkit_*、unity_wait_condition（恢复请查 git 历史） ---


@mcp.tool(
    description="预检测试环境就绪：检查 Unity 连接 + 编译完成 + 编辑模式。返回 ready=true/false 及各项状态。"
)
async def unity_ensure_ready(timeoutS: float = 120) -> str:
    _log_tool_call("unity_ensure_ready", {"timeoutS": timeoutS})
    r = await _get_facade().ensure_ready(timeout_s=timeoutS)
    return _log_tool_result("unity_ensure_ready", _payload(r))


@mcp.tool(
    description="带超时看门狗执行 MCP 工具。超时→尝试重连 Unity→重试→总时间超限则跳过。用于自动化测试流水线防卡死。"
)
async def unity_task_execute(
    taskName: str,
    toolName: str,
    toolArgs: dict | None = None,
    timeoutS: float = 600,
    maxTotalS: float = 1200,
    retryCount: int = 1,
    restartUnityOnTimeout: bool = True,
) -> str:
    _log_tool_call(
        "unity_task_execute",
        {
            "taskName": taskName,
            "toolName": toolName,
            "toolArgs": toolArgs,
            "timeoutS": timeoutS,
            "maxTotalS": maxTotalS,
            "retryCount": retryCount,
            "restartUnityOnTimeout": restartUnityOnTimeout,
        },
    )
    r = await _get_facade().task_execute(
        task_name=taskName,
        tool_name=toolName,
        tool_args=toolArgs,
        timeout_s=timeoutS,
        max_total_s=maxTotalS,
        retry_count=retryCount,
        restart_unity_on_timeout=restartUnityOnTimeout,
    )
    return _log_tool_result("unity_task_execute", _payload(r))


@mcp.tool(description="执行 Unity 编辑器拖放操作。")
async def unity_drag_drop(
    sourceWindow: str,
    targetWindow: str,
    dragType: str,
    fromX: float,
    fromY: float,
    toX: float,
    toY: float,
    assetPaths: list[str] | None = None,
    gameObjectIds: list[int] | None = None,
    customData: str = "",
    modifiers: list[str] | None = None,
) -> str:
    _log_tool_call(
        "unity_drag_drop",
        {
            "sourceWindow": sourceWindow,
            "targetWindow": targetWindow,
            "dragType": dragType,
            "fromX": fromX,
            "fromY": fromY,
            "toX": toX,
            "toY": toY,
            "assetPaths": assetPaths,
            "gameObjectIds": gameObjectIds,
            "customData": customData,
            "modifiers": modifiers,
        },
    )
    r = await _get_facade().drag_drop(
        source_window=sourceWindow,
        target_window=targetWindow,
        drag_type=dragType,
        from_x=fromX,
        from_y=fromY,
        to_x=toX,
        to_y=toY,
        asset_paths=assetPaths,
        game_object_ids=gameObjectIds,
        custom_data=customData,
        modifiers=modifiers,
    )
    return _log_tool_result("unity_drag_drop", _payload(r))


@mcp.tool(description="执行 Unity 编辑器键盘动作。")
async def unity_keyboard_event(
    action: str,
    targetWindow: str,
    keyCode: str = "",
    character: str = "",
    text: str = "",
    modifiers: list[str] | None = None,
) -> str:
    _log_tool_call(
        "unity_keyboard_event",
        {
            "action": action,
            "targetWindow": targetWindow,
            "keyCode": keyCode,
            "character": character,
            "text": text,
            "modifiers": modifiers,
        },
    )
    r = await _get_facade().keyboard_event(
        action=action,
        target_window=targetWindow,
        key_code=keyCode,
        character=character,
        text=text,
        modifiers=modifiers,
    )
    return _log_tool_result("unity_keyboard_event", _payload(r))


@mcp.tool(
    description=(
        "列出 Unity 编辑器中打开的窗口（可按类型/标题过滤）。"
        "每项含 instanceId、标题、位置、docked、closable、closeDeniedReason（M27）等。"
    ),
)
async def unity_editor_windows_list(typeFilter: str = "", titleFilter: str = "") -> str:
    _log_tool_call(
        "unity_editor_windows_list",
        {"typeFilter": typeFilter, "titleFilter": titleFilter},
    )
    r = await _get_facade().editor_windows_list(
        type_filter=typeFilter, title_filter=titleFilter
    )
    return _log_tool_result("unity_editor_windows_list", _payload(r))


@mcp.tool(
    description=(
        "M27：按标题关闭可关闭的编辑器窗口（非停靠、非黑名单）。"
        "matchMode：exact | contains。"
    ),
)
async def unity_editor_window_close(windowTitle: str, matchMode: str = "exact") -> str:
    _log_tool_call(
        "unity_editor_window_close",
        {"windowTitle": windowTitle, "matchMode": matchMode},
    )
    r = await _get_facade().editor_window_close(
        window_title=windowTitle, match_mode=matchMode
    )
    return _log_tool_result("unity_editor_window_close", _payload(r))


@mcp.tool(
    description=(
        "M27：设置浮动编辑器窗口的位置与大小（像素）。"
        "已停靠或不可浮动的窗口可能返回 WINDOW_DOCKED 等错误。"
    ),
)
async def unity_editor_window_set_rect(
    windowTitle: str,
    x: float,
    y: float,
    width: float,
    height: float,
    matchMode: str = "exact",
) -> str:
    _log_tool_call(
        "unity_editor_window_set_rect",
        {
            "windowTitle": windowTitle,
            "x": x,
            "y": y,
            "width": width,
            "height": height,
            "matchMode": matchMode,
        },
    )
    r = await _get_facade().editor_window_set_rect(
        window_title=windowTitle,
        x=x,
        y=y,
        width=width,
        height=height,
        match_mode=matchMode,
    )
    return _log_tool_result("unity_editor_window_set_rect", _payload(r))


@mcp.tool(description="获取 Unity 编辑器状态快照。")
async def unity_editor_state() -> str:
    _log_tool_call("unity_editor_state", {})
    r = await _get_facade().editor_state()
    return _log_tool_result("unity_editor_state", _payload(r))


@mcp.tool(
    description=(
        "将 Unity Editor 窗口设置为前台焦点窗口（仅 Windows）。"
        "调用后会恢复窗口并执行 SetForegroundWindow，用于解决 Unity 在后台时编译延迟的问题。"
    ),
)
async def unity_editor_focus() -> str:
    _log_tool_call("unity_editor_focus", {})
    r = await _get_facade().editor_focus()
    return _log_tool_result("unity_editor_focus", _payload(r))


@mcp.tool(
    description=(
        "查询 Unity Editor 窗口的焦点状态（仅 Windows）。"
        "返回 unityFocused、unityTitle、foregroundTitle 等字段，用于判断 Unity 是否处于前台焦点。"
    ),
)
async def unity_editor_focus_state() -> str:
    _log_tool_call("unity_editor_focus_state", {})
    r = await _get_facade().editor_focus_state()
    return _log_tool_result("unity_editor_focus_state", _payload(r))


# ── M07 Console 日志读取 ─────────────────────────────────────────────────────


@mcp.tool(description="标记 Unity 控制台当前末尾游标，用于后续 tail 读取新增日志。")
async def unity_console_mark_logs() -> str:
    _log_tool_call("unity_console_mark_logs", {})
    r = await _get_facade().console_mark_logs()
    return _log_tool_result("unity_console_mark_logs", _payload(r))


@mcp.tool(
    description=(
        "从 Unity 控制台游标之后读取新增日志，支持服务端过滤。"
        "默认不返回堆栈并排除 UnityPilot/MCP 自身日志。"
    )
)
async def unity_console_tail_logs(
    cursor: int = -1,
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUnityPilot: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = False,
    maxMessageLength: int = 0,
) -> str:
    _log_tool_call(
        "unity_console_tail_logs",
        {
            "cursor": cursor,
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "excludeUnityPilot": excludeUnityPilot,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "maxMessageLength": maxMessageLength,
        },
    )
    r = await _get_facade().console_tail_logs(
        cursor=cursor,
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_unity_pilot=excludeUnityPilot,
        contains=contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
    )
    return _log_tool_result("unity_console_tail_logs", _payload(r))


@mcp.tool(
    description=(
        "搜索 Unity 控制台全量日志，支持关键词/正则和日志类型过滤。"
        "默认不返回堆栈并排除 UnityPilot/MCP 自身日志。"
    )
)
async def unity_console_search_logs(
    count: int = 200,
    logType: str = "",
    includeStackTrace: bool = False,
    excludeUnityPilot: bool = True,
    contains: list[str] | None = None,
    containsAll: bool = False,
    regex: str = "",
    newestFirst: bool = True,
    maxMessageLength: int = 0,
) -> str:
    _log_tool_call(
        "unity_console_search_logs",
        {
            "count": count,
            "logType": logType,
            "includeStackTrace": includeStackTrace,
            "excludeUnityPilot": excludeUnityPilot,
            "contains": contains,
            "containsAll": containsAll,
            "regex": regex,
            "newestFirst": newestFirst,
            "maxMessageLength": maxMessageLength,
        },
    )
    r = await _get_facade().console_search_logs(
        count=count,
        log_type=logType,
        include_stack_trace=includeStackTrace,
        exclude_unity_pilot=excludeUnityPilot,
        contains=contains,
        contains_all=containsAll,
        regex=regex,
        newest_first=newestFirst,
        max_message_length=maxMessageLength,
    )
    return _log_tool_result("unity_console_search_logs", _payload(r))


@mcp.tool(description="清空 Unity 控制台日志。")
async def unity_console_clear() -> str:
    _log_tool_call("unity_console_clear", {})
    r = await _get_facade().console_clear()
    return _log_tool_result("unity_console_clear", _payload(r))


# ── M08 GameObject 操作 ──────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 场景中创建新的 GameObject。")
async def unity_gameobject_create(
    name: str = "New GameObject",
    parentId: int = 0,
    primitiveType: str = "",
) -> str:
    _log_tool_call(
        "unity_gameobject_create",
        {"name": name, "parentId": parentId, "primitiveType": primitiveType},
    )
    r = await _get_facade().gameobject_create(
        name=name, parent_id=parentId, primitive_type=primitiveType
    )
    return _log_tool_result("unity_gameobject_create", _payload(r))


@mcp.tool(
    description="在 Unity 场景中查找 GameObject，支持按名称、标签或 InstanceID 查找。"
)
async def unity_gameobject_find(
    name: str = "", tag: str = "", instanceId: int = 0
) -> str:
    _log_tool_call(
        "unity_gameobject_find", {"name": name, "tag": tag, "instanceId": instanceId}
    )
    r = await _get_facade().gameobject_find(name=name, tag=tag, instance_id=instanceId)
    return _log_tool_result("unity_gameobject_find", _payload(r))


@mcp.tool(
    description="修改 Unity 场景中 GameObject 的属性（名称、标签、层级、激活状态等）。"
)
async def unity_gameobject_modify(
    instanceId: int,
    name: str | None = None,
    tag: str | None = None,
    layer: int | None = None,
    activeSelf: bool | None = None,
    isStatic: bool | None = None,
    parentId: int | None = None,
) -> str:
    _log_tool_call(
        "unity_gameobject_modify",
        {
            "instanceId": instanceId,
            "name": name,
            "tag": tag,
            "layer": layer,
            "activeSelf": activeSelf,
            "isStatic": isStatic,
            "parentId": parentId,
        },
    )
    r = await _get_facade().gameobject_modify(
        instance_id=instanceId,
        name=name,
        tag=tag,
        layer=layer,
        active_self=activeSelf,
        is_static=isStatic,
        parent_id=parentId,
    )
    return _log_tool_result("unity_gameobject_modify", _payload(r))


@mcp.tool(description="销毁 Unity 场景中的 GameObject。")
async def unity_gameobject_delete(instanceId: int) -> str:
    _log_tool_call("unity_gameobject_delete", {"instanceId": instanceId})
    r = await _get_facade().gameobject_delete(instance_id=instanceId)
    return _log_tool_result("unity_gameobject_delete", _payload(r))


@mcp.tool(description="修改 Unity 场景中 GameObject 的变换（位置、旋转、缩放）。")
async def unity_gameobject_move(
    instanceId: int,
    position: dict | None = None,
    rotation: dict | None = None,
    scale: dict | None = None,
) -> str:
    _log_tool_call(
        "unity_gameobject_move",
        {
            "instanceId": instanceId,
            "position": position,
            "rotation": rotation,
            "scale": scale,
        },
    )
    r = await _get_facade().gameobject_move(
        instance_id=instanceId,
        position=position,
        rotation=rotation,
        scale=scale,
    )
    return _log_tool_result("unity_gameobject_move", _payload(r))


@mcp.tool(description="复制 Unity 场景中的 GameObject（包含所有子对象和组件）。")
async def unity_gameobject_duplicate(instanceId: int) -> str:
    _log_tool_call("unity_gameobject_duplicate", {"instanceId": instanceId})
    r = await _get_facade().gameobject_duplicate(instance_id=instanceId)
    return _log_tool_result("unity_gameobject_duplicate", _payload(r))


# ── M09 Scene 管理 ──────────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 中新建空场景。")
async def unity_scene_create(sceneName: str = "") -> str:
    _log_tool_call("unity_scene_create", {"sceneName": sceneName})
    r = await _get_facade().scene_create(scene_name=sceneName)
    return _log_tool_result("unity_scene_create", _payload(r))


@mcp.tool(description="在 Unity 中打开指定路径的场景。")
async def unity_scene_open(scenePath: str, mode: str = "single") -> str:
    _log_tool_call("unity_scene_open", {"scenePath": scenePath, "mode": mode})
    r = await _get_facade().scene_open(scene_path=scenePath, mode=mode)
    return _log_tool_result("unity_scene_open", _payload(r))


@mcp.tool(description="保存当前 Unity 场景或指定路径的场景。")
async def unity_scene_save(scenePath: str = "") -> str:
    _log_tool_call("unity_scene_save", {"scenePath": scenePath})
    r = await _get_facade().scene_save(scene_path=scenePath)
    return _log_tool_result("unity_scene_save", _payload(r))


@mcp.tool(description="加载 Unity 场景（支持叠加模式或单场景模式）。")
async def unity_scene_load(scenePath: str, mode: str = "additive") -> str:
    _log_tool_call("unity_scene_load", {"scenePath": scenePath, "mode": mode})
    r = await _get_facade().scene_load(scene_path=scenePath, mode=mode)
    return _log_tool_result("unity_scene_load", _payload(r))


@mcp.tool(description="设置指定场景为 Unity 当前激活场景。")
async def unity_scene_set_active(scenePath: str) -> str:
    _log_tool_call("unity_scene_set_active", {"scenePath": scenePath})
    r = await _get_facade().scene_set_active(scene_path=scenePath)
    return _log_tool_result("unity_scene_set_active", _payload(r))


@mcp.tool(description="获取 Unity 当前所有已打开场景列表。")
async def unity_scene_list() -> str:
    _log_tool_call("unity_scene_list", {})
    r = await _get_facade().scene_list()
    return _log_tool_result("unity_scene_list", _payload(r))


@mcp.tool(description="卸载 Unity 场景（可选择从层级视图中移除）。")
async def unity_scene_unload(scenePath: str, removeScene: bool = False) -> str:
    _log_tool_call(
        "unity_scene_unload", {"scenePath": scenePath, "removeScene": removeScene}
    )
    r = await _get_facade().scene_unload(scene_path=scenePath, remove_scene=removeScene)
    return _log_tool_result("unity_scene_unload", _payload(r))


@mcp.tool(
    description=(
        "确保并打开用于自动化/验收的空场景：若磁盘上已有资源则单场景打开；否则新建 EmptyScene 并保存。"
        "默认 Assets/unitypilot-test.unity。返回 ensureAction: opened|created 与 scene 信息。"
    ),
)
async def unity_scene_ensure_test(
    sceneName: str = "unitypilot-test",
    scenePath: str = "",
) -> str:
    _log_tool_call(
        "unity_scene_ensure_test", {"sceneName": sceneName, "scenePath": scenePath}
    )
    r = await _get_facade().scene_ensure_test(
        scene_name=sceneName, scene_path=scenePath
    )
    return _log_tool_result("unity_scene_ensure_test", _payload(r))


# ── M10 Component 操作 ──────────────────────────────────────────────────────


@mcp.tool(description="在指定 GameObject 上添加组件。")
async def unity_component_add(gameObjectId: int, componentType: str) -> str:
    _log_tool_call(
        "unity_component_add",
        {"gameObjectId": gameObjectId, "componentType": componentType},
    )
    r = await _get_facade().component_add(
        game_object_id=gameObjectId, component_type=componentType
    )
    return _log_tool_result("unity_component_add", _payload(r))


@mcp.tool(description="从指定 GameObject 上移除组件。")
async def unity_component_remove(
    gameObjectId: int, componentType: str, componentIndex: int = 0
) -> str:
    _log_tool_call(
        "unity_component_remove",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_remove(
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_remove", _payload(r))


@mcp.tool(description="获取指定 GameObject 上组件的序列化属性。")
async def unity_component_get(
    gameObjectId: int, componentType: str, componentIndex: int = 0
) -> str:
    _log_tool_call(
        "unity_component_get",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_get(
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_get", _payload(r))


@mcp.tool(description="修改指定 GameObject 上组件的属性。")
async def unity_component_modify(
    gameObjectId: int,
    componentType: str,
    properties: dict,
    componentIndex: int = 0,
) -> str:
    _log_tool_call(
        "unity_component_modify",
        {
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "properties": properties,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().component_modify(
        game_object_id=gameObjectId,
        component_type=componentType,
        properties=properties,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_component_modify", _payload(r))


@mcp.tool(description="列出指定 GameObject 上的所有组件。")
async def unity_component_list(gameObjectId: int) -> str:
    _log_tool_call("unity_component_list", {"gameObjectId": gameObjectId})
    r = await _get_facade().component_list(game_object_id=gameObjectId)
    return _log_tool_result("unity_component_list", _payload(r))


# ── M11 截图能力 ────────────────────────────────────────────────────────────


@mcp.tool(description="截取 Unity Game 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_game_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    _log_tool_call(
        "unity_screenshot_game_view",
        {"width": width, "height": height, "format": format, "quality": quality},
    )
    r = await _get_facade().screenshot_game_view(
        width=width, height=height, format=format, quality=quality
    )
    return _log_tool_result("unity_screenshot_game_view", _payload(r))


@mcp.tool(description="截取 Unity Scene 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_scene_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    _log_tool_call(
        "unity_screenshot_scene_view",
        {"width": width, "height": height, "format": format, "quality": quality},
    )
    r = await _get_facade().screenshot_scene_view(
        width=width, height=height, format=format, quality=quality
    )
    return _log_tool_result("unity_screenshot_scene_view", _payload(r))


@mcp.tool(description="截取指定 Camera 的画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_camera(
    cameraName: str,
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    _log_tool_call(
        "unity_screenshot_camera",
        {
            "cameraName": cameraName,
            "width": width,
            "height": height,
            "format": format,
            "quality": quality,
        },
    )
    r = await _get_facade().screenshot_camera(
        camera_name=cameraName,
        width=width,
        height=height,
        format=format,
        quality=quality,
    )
    return _log_tool_result("unity_screenshot_camera", _payload(r))


# ── M12 Asset 管理 ──────────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 资源数据库中搜索资源，支持按名称和类型过滤。")
async def unity_asset_find(query: str, assetType: str = "") -> str:
    _log_tool_call("unity_asset_find", {"query": query, "assetType": assetType})
    r = await _get_facade().asset_find(query=query, asset_type=assetType)
    return _log_tool_result("unity_asset_find", _payload(r))


@mcp.tool(description="在 Unity Assets 目录下创建新文件夹。")
async def unity_asset_create_folder(parentFolder: str, newFolderName: str) -> str:
    _log_tool_call(
        "unity_asset_create_folder",
        {"parentFolder": parentFolder, "newFolderName": newFolderName},
    )
    r = await _get_facade().asset_create_folder(
        parent_folder=parentFolder, new_folder_name=newFolderName
    )
    return _log_tool_result("unity_asset_create_folder", _payload(r))


@mcp.tool(description="复制 Unity 资源到指定路径。")
async def unity_asset_copy(sourcePath: str, destinationPath: str) -> str:
    _log_tool_call(
        "unity_asset_copy",
        {"sourcePath": sourcePath, "destinationPath": destinationPath},
    )
    r = await _get_facade().asset_copy(
        source_path=sourcePath, destination_path=destinationPath
    )
    return _log_tool_result("unity_asset_copy", _payload(r))


@mcp.tool(description="移动 Unity 资源到指定路径。")
async def unity_asset_move(sourcePath: str, destinationPath: str) -> str:
    _log_tool_call(
        "unity_asset_move",
        {"sourcePath": sourcePath, "destinationPath": destinationPath},
    )
    r = await _get_facade().asset_move(
        source_path=sourcePath, destination_path=destinationPath
    )
    return _log_tool_result("unity_asset_move", _payload(r))


@mcp.tool(description="删除指定路径的 Unity 资源。")
async def unity_asset_delete(assetPath: str) -> str:
    _log_tool_call("unity_asset_delete", {"assetPath": assetPath})
    r = await _get_facade().asset_delete(asset_path=assetPath)
    return _log_tool_result("unity_asset_delete", _payload(r))


@mcp.tool(description="触发 Unity 资源数据库刷新（AssetDatabase.Refresh）。")
async def unity_asset_refresh() -> str:
    _log_tool_call("unity_asset_refresh", {})
    r = await _get_facade().asset_refresh()
    return _log_tool_result("unity_asset_refresh", _payload(r))


@mcp.tool(
    description=(
        "在 Cursor/IDE 中改完或新建完本轮所有脚本并全部保存后，再调用一次（不要每文件一调）："
        "先等待 delayS 秒（默认 2，缓解落盘延迟），再 AssetDatabase.Refresh；"
        "triggerCompile=true 时再触发 unity_compile（含 Refresh+脚本编译）。"
        "随后可 unity_compile_wait 确认编译结束。避免 Unity 无焦点时迟迟不导入。"
    ),
)
async def unity_sync_after_disk_write(
    delayS: float = 2.0, triggerCompile: bool = False
) -> str:
    _log_tool_call(
        "unity_sync_after_disk_write",
        {"delayS": delayS, "triggerCompile": triggerCompile},
    )
    if triggerCompile:
        rejected = await _reject_compile_in_playmode("unity_sync_after_disk_write")
        if rejected is not None:
            return rejected
    r = await _get_facade().sync_after_disk_write(
        delay_s=delayS, trigger_compile=triggerCompile
    )
    return _log_tool_result("unity_sync_after_disk_write", _payload(r))


@mcp.tool(description="获取 Unity 资源的元数据信息（GUID、类型、大小等）。")
async def unity_asset_get_info(assetPath: str) -> str:
    _log_tool_call("unity_asset_get_info", {"assetPath": assetPath})
    r = await _get_facade().asset_get_info(asset_path=assetPath)
    return _log_tool_result("unity_asset_get_info", _payload(r))


@mcp.tool(description="搜索 Unity 内置资源（如默认材质、Shader、字体等）。")
async def unity_asset_find_built_in(query: str = "", assetType: str = "") -> str:
    _log_tool_call(
        "unity_asset_find_built_in", {"query": query, "assetType": assetType}
    )
    r = await _get_facade().asset_find_built_in(query=query, asset_type=assetType)
    return _log_tool_result("unity_asset_find_built_in", _payload(r))


@mcp.tool(description="获取 Unity 资源的序列化属性数据（SerializedObject 深度读取）。")
async def unity_asset_get_data(
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
    maxDepth: int = 10,
) -> str:
    _log_tool_call(
        "unity_asset_get_data",
        {
            "assetPath": assetPath,
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
            "maxDepth": maxDepth,
        },
    )
    r = await _get_facade().asset_get_data(
        asset_path=assetPath,
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
        max_depth=maxDepth,
    )
    return _log_tool_result("unity_asset_get_data", _payload(r))


@mcp.tool(description="修改 Unity 资源的序列化属性数据（SerializedObject 深度写入）。")
async def unity_asset_modify_data(
    properties: list[dict],
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
) -> str:
    _log_tool_call(
        "unity_asset_modify_data",
        {
            "properties": properties,
            "assetPath": assetPath,
            "gameObjectId": gameObjectId,
            "componentType": componentType,
            "componentIndex": componentIndex,
        },
    )
    r = await _get_facade().asset_modify_data(
        properties=properties,
        asset_path=assetPath,
        game_object_id=gameObjectId,
        component_type=componentType,
        component_index=componentIndex,
    )
    return _log_tool_result("unity_asset_modify_data", _payload(r))


# ── M13 Prefab 操作 ─────────────────────────────────────────────────────────


@mcp.tool(description="将场景中的 GameObject 创建为 Prefab 资源。")
async def unity_prefab_create(sourceGameObjectId: int, prefabPath: str) -> str:
    _log_tool_call(
        "unity_prefab_create",
        {"sourceGameObjectId": sourceGameObjectId, "prefabPath": prefabPath},
    )
    r = await _get_facade().prefab_create(
        source_game_object_id=sourceGameObjectId, prefab_path=prefabPath
    )
    return _log_tool_result("unity_prefab_create", _payload(r))


@mcp.tool(description="在场景中实例化指定路径的 Prefab。")
async def unity_prefab_instantiate(prefabPath: str, parentId: int = 0) -> str:
    _log_tool_call(
        "unity_prefab_instantiate", {"prefabPath": prefabPath, "parentId": parentId}
    )
    r = await _get_facade().prefab_instantiate(
        prefab_path=prefabPath, parent_id=parentId
    )
    return _log_tool_result("unity_prefab_instantiate", _payload(r))


@mcp.tool(description="进入 Prefab 编辑模式。")
async def unity_prefab_open(prefabPath: str) -> str:
    _log_tool_call("unity_prefab_open", {"prefabPath": prefabPath})
    r = await _get_facade().prefab_open(prefab_path=prefabPath)
    return _log_tool_result("unity_prefab_open", _payload(r))


@mcp.tool(description="退出 Prefab 编辑模式。")
async def unity_prefab_close() -> str:
    _log_tool_call("unity_prefab_close", {})
    r = await _get_facade().prefab_close()
    return _log_tool_result("unity_prefab_close", _payload(r))


@mcp.tool(description="保存当前 Prefab 编辑模式下的修改。")
async def unity_prefab_save() -> str:
    _log_tool_call("unity_prefab_save", {})
    r = await _get_facade().prefab_save()
    return _log_tool_result("unity_prefab_save", _payload(r))


# ── M14 Material 与 Shader ──────────────────────────────────────────────────


@mcp.tool(description="创建新的 Unity 材质资源并指定 Shader。")
async def unity_material_create(materialPath: str, shaderName: str = "Standard") -> str:
    _log_tool_call(
        "unity_material_create",
        {"materialPath": materialPath, "shaderName": shaderName},
    )
    r = await _get_facade().material_create(
        material_path=materialPath, shader_name=shaderName
    )
    return _log_tool_result("unity_material_create", _payload(r))


@mcp.tool(description="修改 Unity 材质的属性（颜色、纹理、数值等）。")
async def unity_material_modify(materialPath: str, properties: dict) -> str:
    _log_tool_call(
        "unity_material_modify",
        {"materialPath": materialPath, "properties": properties},
    )
    r = await _get_facade().material_modify(
        material_path=materialPath, properties=properties
    )
    return _log_tool_result("unity_material_modify", _payload(r))


@mcp.tool(description="将材质分配给场景中 GameObject 的渲染器。")
async def unity_material_assign(
    targetGameObjectId: int, materialPath: str, materialIndex: int = 0
) -> str:
    _log_tool_call(
        "unity_material_assign",
        {
            "targetGameObjectId": targetGameObjectId,
            "materialPath": materialPath,
            "materialIndex": materialIndex,
        },
    )
    r = await _get_facade().material_assign(
        target_game_object_id=targetGameObjectId,
        material_path=materialPath,
        material_index=materialIndex,
    )
    return _log_tool_result("unity_material_assign", _payload(r))


@mcp.tool(description="获取 Unity 材质的详细属性信息。")
async def unity_material_get(materialPath: str) -> str:
    _log_tool_call("unity_material_get", {"materialPath": materialPath})
    r = await _get_facade().material_get(material_path=materialPath)
    return _log_tool_result("unity_material_get", _payload(r))


@mcp.tool(description="列出 Unity 中所有可用的 Shader。")
async def unity_shader_list() -> str:
    _log_tool_call("unity_shader_list", {})
    r = await _get_facade().shader_list()
    return _log_tool_result("unity_shader_list", _payload(r))


# ── M15 菜单项执行 ──────────────────────────────────────────────────────────


@mcp.tool(description="执行 Unity 编辑器中指定路径的菜单项。")
async def unity_menu_execute(menuPath: str) -> str:
    _log_tool_call("unity_menu_execute", {"menuPath": menuPath})
    r = await _get_facade().menu_execute(menu_path=menuPath)
    return _log_tool_result("unity_menu_execute", _payload(r))


@mcp.tool(description="列出 Unity 编辑器中所有可用的菜单项。")
async def unity_menu_list() -> str:
    _log_tool_call("unity_menu_list", {})
    r = await _get_facade().menu_list()
    return _log_tool_result("unity_menu_list", _payload(r))


# ── M16 Package 管理 ────────────────────────────────────────────────────────


@mcp.tool(description="通过 Unity Package Manager 添加包（支持名称、版本或 Git URL）。")
async def unity_package_add(packageName: str, version: str = "") -> str:
    _log_tool_call(
        "unity_package_add", {"packageName": packageName, "version": version}
    )
    r = await _get_facade().package_add(package_name=packageName, version=version)
    return _log_tool_result("unity_package_add", _payload(r))


@mcp.tool(description="通过 Unity Package Manager 移除已安装的包。")
async def unity_package_remove(packageName: str) -> str:
    _log_tool_call("unity_package_remove", {"packageName": packageName})
    r = await _get_facade().package_remove(package_name=packageName)
    return _log_tool_result("unity_package_remove", _payload(r))


@mcp.tool(description="列出 Unity 项目中所有已安装的包。")
async def unity_package_list() -> str:
    _log_tool_call("unity_package_list", {})
    r = await _get_facade().package_list()
    return _log_tool_result("unity_package_list", _payload(r))


@mcp.tool(description="在 Unity Package Manager 注册表中搜索包。")
async def unity_package_search(query: str) -> str:
    _log_tool_call("unity_package_search", {"query": query})
    r = await _get_facade().package_search(query=query)
    return _log_tool_result("unity_package_search", _payload(r))


# ── M17 测试运行 ────────────────────────────────────────────────────────────


@mcp.tool(description="运行 Unity 测试（支持 EditMode 和 PlayMode）。")
async def unity_test_run(testMode: str = "EditMode", testFilter: str = "") -> str:
    _log_tool_call("unity_test_run", {"testMode": testMode, "testFilter": testFilter})
    r = await _get_facade().test_run(test_mode=testMode, test_filter=testFilter)
    return _log_tool_result("unity_test_run", _payload(r))


@mcp.tool(description="获取最近一次 Unity 测试运行的结果。")
async def unity_test_results() -> str:
    _log_tool_call("unity_test_results", {})
    r = await _get_facade().test_results()
    return _log_tool_result("unity_test_results", _payload(r))


@mcp.tool(description="列出 Unity 项目中所有可用的测试用例。")
async def unity_test_list(testMode: str = "EditMode") -> str:
    _log_tool_call("unity_test_list", {"testMode": testMode})
    r = await _get_facade().test_list(test_mode=testMode)
    return _log_tool_result("unity_test_list", _payload(r))


# ── M18 脚本读写 ────────────────────────────────────────────────────────────


@mcp.tool(description="读取 Unity 项目中指定路径的 C# 脚本内容。")
async def unity_script_read(scriptPath: str) -> str:
    _log_tool_call("unity_script_read", {"scriptPath": scriptPath})
    r = await _get_facade().script_read(script_path=scriptPath)
    return _log_tool_result("unity_script_read", _payload(r))


@mcp.tool(description="在 Unity 项目中创建新的 C# 脚本文件。")
async def unity_script_create(scriptPath: str, content: str = "") -> str:
    _log_tool_call(
        "unity_script_create", {"scriptPath": scriptPath, "content": content}
    )
    r = await _get_facade().script_create(script_path=scriptPath, content=content)
    return _log_tool_result("unity_script_create", _payload(r))


@mcp.tool(description="更新 Unity 项目中已有 C# 脚本文件的内容。")
async def unity_script_update(scriptPath: str, content: str) -> str:
    _log_tool_call(
        "unity_script_update", {"scriptPath": scriptPath, "content": content}
    )
    r = await _get_facade().script_update(script_path=scriptPath, content=content)
    return _log_tool_result("unity_script_update", _payload(r))


@mcp.tool(description="删除 Unity 项目中指定路径的 C# 脚本文件。")
async def unity_script_delete(scriptPath: str) -> str:
    _log_tool_call("unity_script_delete", {"scriptPath": scriptPath})
    r = await _get_facade().script_delete(script_path=scriptPath)
    return _log_tool_result("unity_script_delete", _payload(r))


# ── M19 Roslyn 代码执行 ─────────────────────────────────────────────────────


@mcp.tool(
    description="通过 Roslyn 动态编译并执行 C# 代码片段，返回执行结果。适合临时诊断；调用已有业务方法请优先使用 unity_reflection_call。"
)
async def unity_roslyn_execute(code: str, timeoutSeconds: int = 10) -> str:
    _log_tool_call(
        "unity_roslyn_execute", {"code": code, "timeoutSeconds": timeoutSeconds}
    )
    rejected = await _reject_roslyn_in_playmode("unity_roslyn_execute")
    if rejected is not None:
        return rejected
    r = await _get_facade().roslyn_execute(code=code, timeout_seconds=timeoutSeconds)
    return _log_tool_result("unity_roslyn_execute", _payload(r))


@mcp.tool(description="查询 Roslyn 动态代码执行任务的当前状态。")
async def unity_roslyn_status(executionId: str) -> str:
    _log_tool_call("unity_roslyn_status", {"executionId": executionId})
    rejected = await _reject_roslyn_in_playmode("unity_roslyn_status")
    if rejected is not None:
        return rejected
    r = await _get_facade().roslyn_status(execution_id=executionId)
    return _log_tool_result("unity_roslyn_status", _payload(r))


@mcp.tool(description="终止正在运行的 Roslyn 动态代码执行任务。")
async def unity_roslyn_abort(executionId: str) -> str:
    _log_tool_call("unity_roslyn_abort", {"executionId": executionId})
    rejected = await _reject_roslyn_in_playmode("unity_roslyn_abort")
    if rejected is not None:
        return rejected
    r = await _get_facade().roslyn_abort(execution_id=executionId)
    return _log_tool_result("unity_roslyn_abort", _payload(r))


# ── M20 反射调用 ────────────────────────────────────────────────────────────


@mcp.tool(description="通过反射搜索 Unity 程序集中的指定类型，可选按方法名过滤。")
async def unity_reflection_find(
    typeName: Annotated[
        str,
        Field(description="要查找的 C# 类型名，可使用完整命名空间或不带命名空间的类名。"),
    ],
    methodName: Annotated[
        str,
        Field(description="可选的方法名过滤条件；留空时返回该类型的所有方法。"),
    ] = "",
) -> str:
    _log_tool_call(
        "unity_reflection_find", {"typeName": typeName, "methodName": methodName}
    )
    r = await _get_facade().reflection_find(type_name=typeName, method_name=methodName)
    return _log_tool_result("unity_reflection_find", _payload(r))


@mcp.tool(description="通过反射动态调用 Unity 程序集中的方法。")
async def unity_reflection_call(
    typeName: str,
    methodName: str,
    parameters: list | None = None,
    isStatic: bool = True,
    targetInstancePath: str = "",
    targetStaticTypeName: str = "",
    targetStaticMemberPath: str = "",
) -> str:
    _log_tool_call(
        "unity_reflection_call",
        {
            "typeName": typeName,
            "methodName": methodName,
            "parameters": parameters,
            "isStatic": isStatic,
            "targetInstancePath": targetInstancePath,
            "targetStaticTypeName": targetStaticTypeName,
            "targetStaticMemberPath": targetStaticMemberPath,
        },
    )
    r = await _get_facade().reflection_call(
        type_name=typeName,
        method_name=methodName,
        parameters=parameters,
        is_static=isStatic,
        target_instance_path=targetInstancePath,
        target_static_type_name=targetStaticTypeName,
        target_static_member_path=targetStaticMemberPath,
    )
    return _log_tool_result("unity_reflection_call", _payload(r))


@mcp.tool(
    description=(
        "执行一条受限 C#-like 反射表达式语句。支持成员访问、索引、链式方法调用、"
        "常见运算符、三元、cast/as/is、null 条件访问和 typed array 字面量；"
        "不支持 lambda/LINQ/async/控制语句/ref/out/任意对象构造。"
    ),
)
async def reflection_eval(
    code: str,
    variables: dict | None = None,
    options: dict | None = None,
) -> str:
    _log_tool_call(
        "reflection_eval",
        {"code": code, "variables": variables, "options": options},
    )
    r = await _get_facade().reflection_eval(
        code=code,
        variables=variables,
        options=options,
    )
    return _log_tool_result("reflection_eval", _payload(r))


# ── M21 批量操作 ────────────────────────────────────────────────────────────


@mcp.tool(description="批量执行多个 Unity 操作指令（支持顺序或并行模式）。")
async def unity_batch_execute(
    operations: list,
    mode: str = "sequential",
    stopOnError: bool = True,
) -> str:
    _log_tool_call(
        "unity_batch_execute",
        {"operations": operations, "mode": mode, "stopOnError": stopOnError},
    )
    r = await _get_facade().batch_execute(
        operations=operations, mode=mode, stop_on_error=stopOnError
    )
    return _log_tool_result("unity_batch_execute", _payload(r))


@mcp.tool(description="取消正在执行的批量操作。")
async def unity_batch_cancel(batchId: str) -> str:
    _log_tool_call("unity_batch_cancel", {"batchId": batchId})
    r = await _get_facade().batch_cancel(batch_id=batchId)
    return _log_tool_result("unity_batch_cancel", _payload(r))


@mcp.tool(description="查询批量操作的执行结果。")
async def unity_batch_results(batchId: str) -> str:
    _log_tool_call("unity_batch_results", {"batchId": batchId})
    r = await _get_facade().batch_results(batch_id=batchId)
    return _log_tool_result("unity_batch_results", _payload(r))


# ── M22 Selection 管理 ──────────────────────────────────────────────────────


@mcp.tool(description="获取 Unity 编辑器当前选中的 GameObject 和资源列表。")
async def unity_selection_get() -> str:
    _log_tool_call("unity_selection_get", {})
    r = await _get_facade().selection_get()
    return _log_tool_result("unity_selection_get", _payload(r))


@mcp.tool(
    description="设置 Unity 编辑器的选中项（支持 InstanceID 列表或资源路径列表）。"
)
async def unity_selection_set(
    gameObjectIds: list[int] | None = None,
    assetPaths: list[str] | None = None,
) -> str:
    _log_tool_call(
        "unity_selection_set",
        {"gameObjectIds": gameObjectIds, "assetPaths": assetPaths},
    )
    r = await _get_facade().selection_set(
        game_object_ids=gameObjectIds, asset_paths=assetPaths
    )
    return _log_tool_result("unity_selection_set", _payload(r))


@mcp.tool(description="清空 Unity 编辑器的当前选中项。")
async def unity_selection_clear() -> str:
    _log_tool_call("unity_selection_clear", {})
    r = await _get_facade().selection_clear()
    return _log_tool_result("unity_selection_clear", _payload(r))


# ── M23 MCP Resources ──────────────────────────────────────────────────────


@mcp.resource(
    "unity://scenes/hierarchy", description="Unity 当前场景的 GameObject 层级树。"
)
async def resource_scene_hierarchy() -> str:
    r = await _get_facade().resource_scene_hierarchy()
    return _log_tool_result("resource_scene_hierarchy", _payload(r))


@mcp.resource("unity://console/logs", description="Unity 控制台最近日志。")
async def resource_console_logs() -> str:
    r = await _get_facade().resource_console_logs()
    return _log_tool_result("resource_console_logs", _payload(r))


@mcp.resource("unity://editor/state", description="Unity 编辑器当前状态快照。")
async def resource_editor_state() -> str:
    r = await _get_facade().resource_editor_state()
    return _log_tool_result("resource_editor_state", _payload(r))


@mcp.resource("unity://packages/list", description="Unity 项目已安装的包列表。")
async def resource_packages() -> str:
    r = await _get_facade().resource_packages()
    return _log_tool_result("resource_packages", _payload(r))


@mcp.resource("unity://build/status", description="Unity 当前构建状态。")
async def resource_build_status() -> str:
    r = await _get_facade().resource_build_status()
    return _log_tool_result("resource_build_status", _payload(r))


@mcp.resource(
    "unity://diagnostics/unitypilot-logs-tab",
    description="upilot 诊断日志标签页布局快照（横向滚动风险、滚动位置等，需打开窗口并切到该标签）。",
)
async def resource_unitypilot_logs_tab() -> str:
    r = await _get_facade().resource_unitypilot_logs_tab()
    return _log_tool_result("resource_unitypilot_logs_tab", _payload(r))


@mcp.resource(
    "unity://diagnostics/window",
    description="upilot 全窗口级布局诊断快照（健康分、各区域宽度溢出检测、编译状态、代码版本、Domain Reload 纪元）。",
)
async def resource_window_diagnostics() -> str:
    r = await _get_facade().resource_window_diagnostics()
    return _log_tool_result("resource_window_diagnostics", _payload(r))


@mcp.resource(
    "unity://console/summary",
    description="Unity 控制台日志按类型统计（logCount/warningCount/errorCount/assertCount）。",
)
async def resource_console_summary() -> str:
    r = await _get_facade().resource_console_summary()
    return _log_tool_result("resource_console_summary", _payload(r))


# ── M26 验收自动化工具 ─────────────────────────────────────────────────────


@mcp.tool(
    description=(
        "等待 Unity 脚本编译结束。优先使用 Bridge 推送的 compile.started/finished 与 compile.pipeline.* 信号（wait_for_compile_idle），"
        "再以指数退避轮询 resource.editorState。preferEvents=false 可仅用轮询。返回 waitMode：immediate|event|poll|timeout。"
    ),
)
async def unity_compile_wait(
    timeoutS: float = 300,
    pollIntervalS: float = 1.0,
    preferEvents: bool = True,
) -> str:
    _log_tool_call(
        "unity_compile_wait",
        {
            "timeoutS": timeoutS,
            "pollIntervalS": pollIntervalS,
            "preferEvents": preferEvents,
        },
    )
    r = await _get_facade().compile_wait(
        timeout_s=timeoutS,
        poll_interval_s=pollIntervalS,
        prefer_events=preferEvents,
    )
    return _log_tool_result("unity_compile_wait", _payload(r))


@mcp.tool(
    description=(
        "在 Unity 编辑器侧单命令阻塞等待：直到 EditorApplication.isCompiling 为 false（任意来源的编译）。"
        "timeoutMs 默认 120000。适合需要与编辑器内部状态严格对齐的场景。"
    ),
)
async def unity_compile_wait_editor(timeoutMs: int = 300000) -> str:
    _log_tool_call("unity_compile_wait_editor", {"timeoutMs": timeoutMs})
    r = await _get_facade().compile_wait_editor(timeout_ms=timeoutMs)
    return _log_tool_result("unity_compile_wait_editor", _payload(r))


@mcp.tool(
    description=(
        "安全编译等待：触发编译后等待完成，并在 Domain Reload 后双重验证编译错误。"
        "Unity 侧会将编译错误持久化到磁盘，Domain Reload 后从磁盘恢复，避免假编译成功。"
        "修改代码后应优先使用本工具替代 unity_compile + unity_compile_wait 的组合。"
    ),
)
async def unity_safe_compile_and_wait(
    timeoutS: float = 300,
    pollIntervalS: float = 1.0,
    preferEvents: bool = True,
    postCompileDelayS: float = 3.0,
) -> str:
    _log_tool_call(
        "unity_safe_compile_and_wait",
        {
            "timeoutS": timeoutS,
            "pollIntervalS": pollIntervalS,
            "preferEvents": preferEvents,
            "postCompileDelayS": postCompileDelayS,
        },
    )
    rejected = await _reject_compile_in_playmode("unity_safe_compile_and_wait")
    if rejected is not None:
        return rejected
    r = await _get_facade().safe_compile_and_wait(
        timeout_s=timeoutS,
        poll_interval_s=pollIntervalS,
        prefer_events=preferEvents,
        post_compile_delay_s=postCompileDelayS,
    )
    return _log_tool_result("unity_safe_compile_and_wait", _payload(r))


@mcp.tool(
    description="截取 Unity 编辑器窗口（EditorWindow）画面，返回 Base64 编码的 PNG。通过窗口标题匹配。screenshotDegrade: none|auto|scene|minimal — auto 在无法截取窗口时降级为 Scene 视图或占位图。"
)
async def unity_screenshot_editor_window(
    windowTitle: str = "upilot",
    screenshotDegrade: str = "auto",
) -> str:
    _log_tool_call(
        "unity_screenshot_editor_window",
        {"windowTitle": windowTitle, "screenshotDegrade": screenshotDegrade},
    )
    r = await _get_facade().screenshot_editor_window(
        window_title=windowTitle, degrade=screenshotDegrade
    )
    return _log_tool_result("unity_screenshot_editor_window", _payload(r))


@mcp.tool(
    description="一次性获取全部诊断信息：窗口布局诊断 + 控制台摘要 + 编辑器状态。免去多次调用。"
)
async def unity_batch_diagnostics() -> str:
    _log_tool_call("unity_batch_diagnostics", {})
    r = await _get_facade().batch_diagnostics()
    return _log_tool_result("unity_batch_diagnostics", _payload(r))


@mcp.tool(
    description="全自动窗口验收：等编译完成 → 截图（可选） + 窗口布局诊断 + 控制台摘要，一次调用完成所有验收步骤。screenshotDegrade 同 unity_screenshot_editor_window。"
)
async def unity_verify_window(
    windowTitle: str = "upilot",
    includeScreenshot: bool = True,
    screenshotDegrade: str = "auto",
) -> str:
    _log_tool_call(
        "unity_verify_window",
        {
            "windowTitle": windowTitle,
            "includeScreenshot": includeScreenshot,
            "screenshotDegrade": screenshotDegrade,
        },
    )
    r = await _get_facade().verify_window(
        window_title=windowTitle,
        include_screenshot=includeScreenshot,
        screenshot_degrade=screenshotDegrade,
    )
    return _log_tool_result("unity_verify_window", _payload(r))


# ── M24 Build Pipeline ─────────────────────────────────────────────────────


@mcp.tool(description="启动 Unity Player 构建（支持指定平台、输出路径和场景列表）。")
async def unity_build_start(
    buildTarget: str = "StandaloneWindows64",
    outputPath: str = "Builds/",
    scenes: list[str] | None = None,
) -> str:
    _log_tool_call(
        "unity_build_start",
        {"buildTarget": buildTarget, "outputPath": outputPath, "scenes": scenes},
    )
    r = await _get_facade().build_start(
        build_target=buildTarget, output_path=outputPath, scenes=scenes
    )
    return _log_tool_result("unity_build_start", _payload(r))


@mcp.tool(description="获取当前 Unity 构建任务的状态和进度。")
async def unity_build_status() -> str:
    _log_tool_call("unity_build_status", {})
    r = await _get_facade().build_status()
    return _log_tool_result("unity_build_status", _payload(r))


@mcp.tool(description="取消正在进行的 Unity 构建任务。")
async def unity_build_cancel() -> str:
    _log_tool_call("unity_build_cancel", {})
    r = await _get_facade().build_cancel()
    return _log_tool_result("unity_build_cancel", _payload(r))


@mcp.tool(description="获取当前 Unity 安装中支持的构建目标平台列表。")
async def unity_build_targets() -> str:
    _log_tool_call("unity_build_targets", {})
    r = await _get_facade().build_targets()
    return _log_tool_result("unity_build_targets", _payload(r))


# ── M25 Editor Commands ─────────────────────────────────────────────────────


@mcp.tool(description="执行 Unity 撤销操作（Undo）。")
async def unity_editor_undo(steps: int = 1) -> str:
    _log_tool_call("unity_editor_undo", {"steps": steps})
    r = await _get_facade().editor_undo(steps=steps)
    return _log_tool_result("unity_editor_undo", _payload(r))


@mcp.tool(description="执行 Unity 重做操作（Redo）。")
async def unity_editor_redo(steps: int = 1) -> str:
    _log_tool_call("unity_editor_redo", {"steps": steps})
    r = await _get_facade().editor_redo(steps=steps)
    return _log_tool_result("unity_editor_redo", _payload(r))


@mcp.tool(description="执行 Unity 编辑器命令（通过菜单路径，如 'Edit/Play'）。")
async def unity_editor_execute_command(commandName: str) -> str:
    _log_tool_call("unity_editor_execute_command", {"commandName": commandName})
    r = await _get_facade().editor_execute_command(command_name=commandName)
    return _log_tool_result("unity_editor_execute_command", _payload(r))


@mcp.tool(
    description=(
        "M26：从磁盘路径加载 YAML 规格并执行编辑器 E2E（setup/steps/teardown），"
        "断言 console/截图等；失败时在 artifactDir 写入 report.json 与附件。"
        "M27：exportZip 打包 e2e-bundle.zip；webhookOnFailure 在失败时 POST UNITYPILOT_E2E_WEBHOOK_URL。"
    ),
)
async def unity_editor_e2e_run(
    specPath: str,
    artifactDir: str = "",
    stopOnFirstFailure: bool = True,
    exportZip: bool = False,
    webhookOnFailure: bool = False,
) -> str:
    _log_tool_call(
        "unity_editor_e2e_run",
        {
            "specPath": specPath,
            "artifactDir": artifactDir,
            "stopOnFirstFailure": stopOnFirstFailure,
            "exportZip": exportZip,
            "webhookOnFailure": webhookOnFailure,
        },
    )
    r = await _get_facade().editor_e2e_run(
        spec_path=specPath,
        artifact_dir=artifactDir or None,
        stop_on_first_failure=stopOnFirstFailure,
        export_zip=exportZip,
        webhook_on_failure=webhookOnFailure,
    )
    return _log_tool_result("unity_editor_e2e_run", _payload(r))


@mcp.tool(
    description=(
        "通过 Unity 内置 C# TestRunner.RunFileAsync 执行 UnityUIFlow YAML 文件。"
        "要求项目内可解析 UnityUIFlow.TestRunner 与有效 host_window。"
    ),
)
async def unity_uiflow_run_file(
    yamlPath: str,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = True,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
) -> str:
    _log_tool_call(
        "unity_uiflow_run_file",
        {
            "yamlPath": yamlPath,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    r = await _run_unity_uiflow_file(
        yaml_path=yamlPath,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
        debug_on_failure=debugOnFailure,
    )
    return _log_tool_result("unity_uiflow_run_file", _payload(r))


@mcp.tool(
    description=(
        "通过 Unity 内置 C# TestRunner.RunSuiteAsync 执行 UnityUIFlow YAML 目录。"
        "directoryPath 应指向包含 .yaml 的目录。"
    ),
)
async def unity_uiflow_run_suite(
    directoryPath: str,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
) -> str:
    _log_tool_call(
        "unity_uiflow_run_suite",
        {
            "directoryPath": directoryPath,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
        },
    )
    r = await _run_unity_uiflow_suite(
        directory_path=directoryPath,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
    )
    return _log_tool_result("unity_uiflow_run_suite", _payload(r))


@mcp.tool(
    description=(
        "批量执行指定的 UnityUIFlow YAML 文件列表，支持分批次运行以避免超时。"
        "yamlPaths 为文件路径列表；batchSize 控制每批数量（默认 10），"
        "batchOffset 为起始偏移；totalAll 为所有批次的总文件数（用于显示整体进度）。"
        "返回结果中包含 hasMore/nextOffset，客户端可根据 hasMore 继续发起下一批。"
    ),
)
async def unity_uiflow_run_batch(
    yamlPaths: list[str],
    batchSize: int = 10,
    batchOffset: int = 0,
    totalAll: int = 0,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
) -> str:
    _log_tool_call(
        "unity_uiflow_run_batch",
        {
            "yamlPaths": yamlPaths,
            "batchSize": batchSize,
            "batchOffset": batchOffset,
            "totalAll": totalAll,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    r = await _run_unity_uiflow_batch(
        yaml_paths=yamlPaths,
        batch_size=batchSize,
        batch_offset=batchOffset,
        headed=headed,
        report_output_path=reportOutputPath,
        screenshot_path=screenshotPath,
        screenshot_on_failure=screenshotOnFailure,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        default_timeout_ms=defaultTimeoutMs,
        pre_step_delay_ms=preStepDelayMs,
        enable_verbose_log=enableVerboseLog,
        debug_on_failure=debugOnFailure,
        total_all=totalAll,
    )
    return _log_tool_result("unity_uiflow_run_batch", _payload(r))


@mcp.tool(
    description=(
        "强制重置 UnityUIFlow 执行状态。无需 executionId，直接释放 EDITOR_BUSY 锁，"
        "Dispose 当前 ExecutionContext，关闭测试窗口，并将所有进行中的执行标记为 aborted。"
    ),
)
async def unity_uiflow_force_reset() -> str:
    _log_tool_call("unity_uiflow_force_reset", {})
    r = await _get_facade().unityuiflow_force_reset()
    return _log_tool_result("unity_uiflow_force_reset", _payload(r))


@mcp.tool(
    description=(
        "异步启动 UnityUIFlow 批量测试，立即返回 executionId，不等待执行完成。"
        "客户端拿到 executionId 后需自行调用 unity_uiflow_results 轮询进度。"
        "参数与 unity_uiflow_run_batch 相同。"
    ),
)
async def unity_uiflow_run_async(
    yamlPaths: list[str],
    batchSize: int = 10,
    batchOffset: int = 0,
    headed: bool = True,
    reportOutputPath: str = "",
    screenshotPath: str = "",
    screenshotOnFailure: bool = True,
    stopOnFirstFailure: bool = False,
    continueOnStepFailure: bool = False,
    defaultTimeoutMs: int = 10000,
    preStepDelayMs: int = 0,
    enableVerboseLog: bool = True,
    debugOnFailure: bool = False,
) -> str:
    _log_tool_call(
        "unity_uiflow_run_async",
        {
            "yamlPaths": yamlPaths,
            "batchSize": batchSize,
            "batchOffset": batchOffset,
            "headed": headed,
            "reportOutputPath": reportOutputPath,
            "screenshotPath": screenshotPath,
            "screenshotOnFailure": screenshotOnFailure,
            "stopOnFirstFailure": stopOnFirstFailure,
            "continueOnStepFailure": continueOnStepFailure,
            "defaultTimeoutMs": defaultTimeoutMs,
            "preStepDelayMs": preStepDelayMs,
            "enableVerboseLog": enableVerboseLog,
            "debugOnFailure": debugOnFailure,
        },
    )
    resolved = []
    for p in yamlPaths:
        rp = str(Path(p).expanduser().resolve())
        if not Path(rp).is_file():
            return _payload(
                fail(
                    new_id("uiflow"),
                    "UIFLOW_YAML_NOT_FOUND",
                    f"YAML file not found: {rp}",
                    {"yamlPath": rp},
                )
            )
        resolved.append(rp)

    report_root = reportOutputPath.strip() or "Reports/upilot/UIFlowMcp"
    run_resp = await _get_facade().unityuiflow_run(
        yaml_paths=resolved,
        headed=headed,
        stop_on_first_failure=stopOnFirstFailure,
        continue_on_step_failure=continueOnStepFailure,
        screenshot_on_failure=screenshotOnFailure,
        default_timeout_ms=defaultTimeoutMs,
        enable_verbose_log=enableVerboseLog,
        report_path=report_root,
        debug_on_failure=debugOnFailure,
        batch_size=batchSize,
        batch_offset=batchOffset,
    )
    if not run_resp.ok:
        return _log_tool_result("unity_uiflow_run_async", _payload(run_resp))

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return _log_tool_result(
            "unity_uiflow_run_async",
            _payload(
                fail(
                    new_id("uiflow"),
                    "UIFLOW_EXECUTION_ID_MISSING",
                    "unityuiflow.run did not return executionId",
                    {"response": run_data},
                )
            ),
        )

    return _log_tool_result(
        "unity_uiflow_run_async",
        json.dumps(
            {
                "ok": True,
                "data": {
                    "executionId": execution_id,
                    "status": run_data.get("status", "queued"),
                    "total": int(run_data.get("total") or 0),
                    "hasMore": bool(run_data.get("hasMore")),
                    "nextOffset": int(run_data.get("nextOffset") or 0),
                    "totalAll": int(run_data.get("totalAll") or 0),
                    "reportOutputPath": report_root,
                    "screenshotPath": screenshotPath.strip() or str((Path(report_root) / "Screenshots").as_posix()),
                },
                "error": None,
                "requestId": run_resp.request_id,
                "timestamp": run_resp.timestamp,
            }
        ),
    )


@mcp.tool(
    description=(
        "查询指定 executionId 的 UnityUIFlow 执行状态和结果。"
        "返回包含 status、cases 列表、passed/failed/errors/skipped 计数等字段。"
    ),
)
async def unity_uiflow_results(
    executionId: str,
) -> str:
    _log_tool_call("unity_uiflow_results", {"executionId": executionId})
    r = await _get_facade().unityuiflow_results(execution_id=executionId)
    return _log_tool_result("unity_uiflow_results", _payload(r))


async def _run_unity_uiflow_file(
    yaml_path: str,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
    debug_on_failure: bool,
) -> ToolResponse:
    resolved_yaml = str(Path(yaml_path).expanduser().resolve())
    if not Path(resolved_yaml).is_file():
        return fail(
            new_id("uiflow"),
            "UIFLOW_YAML_NOT_FOUND",
            f"YAML file not found: {resolved_yaml}",
            {"yamlPath": resolved_yaml},
        )

    report_root = report_output_path.strip() or "Reports/upilot/UIFlowMcp"
    run_resp = await _get_facade().unityuiflow_run(
        yaml_paths=[resolved_yaml],
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
        debug_on_failure=debug_on_failure,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("uiflow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "unityuiflow.run did not return executionId",
            {"response": run_data},
        )

    deadline = time.monotonic() + max(60.0, default_timeout_ms / 1000.0 + 180.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().unityuiflow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[unityuiflow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[unityuiflow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(60.0, default_timeout_ms / 1000.0 + 120.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            case = ((last_data.get("cases") or [None])[0]) or {}
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_root) / execution_id / "Screenshots").as_posix()
            )
            return ok(
                new_id("uiflow"),
                {
                    "yamlPath": resolved_yaml,
                    "reportOutputPath": str(last_data.get("reportPath") or report_root),
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "caseName": case.get("caseName")
                        or last_data.get("currentCaseName")
                        or Path(resolved_yaml).stem,
                        "errorCode": case.get("errorCode")
                        or last_data.get("errorCode")
                        or "",
                        "errorMessage": case.get("errorMessage")
                        or last_data.get("errorMessage")
                        or "",
                        "reportPath": last_data.get("reportPath") or report_root,
                        "raw": last_data,
                    },
                },
            )

    logger.error("[unityuiflow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(60.0, default_timeout_ms / 1000.0 + 120.0), last_status)
    await _get_facade().unityuiflow_cancel(execution_id)
    return fail(
        new_id("uiflow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for unityuiflow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )


async def _run_unity_uiflow_suite(
    directory_path: str,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
) -> ToolResponse:
    resolved_dir = str(Path(directory_path).expanduser().resolve())
    if not Path(resolved_dir).is_dir():
        return fail(
            new_id("uiflow"),
            "UIFLOW_SUITE_DIR_NOT_FOUND",
            f"Suite directory not found: {resolved_dir}",
            {"directoryPath": resolved_dir},
        )

    report_root = report_output_path.strip() or "Reports/upilot/UIFlowMcp"
    run_resp = await _get_facade().unityuiflow_run(
        yaml_directory=resolved_dir,
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("uiflow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "unityuiflow.run did not return executionId",
            {"response": run_data},
        )

    deadline = time.monotonic() + max(120.0, default_timeout_ms / 1000.0 + 360.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().unityuiflow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[unityuiflow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[unityuiflow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(120.0, default_timeout_ms / 1000.0 + 300.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            report_path = str(last_data.get("reportPath") or report_root)
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_path) / "Screenshots").as_posix()
            )
            failed = int(last_data.get("failed") or 0)
            errors = int(last_data.get("errors") or 0)
            exit_code = (
                0 if status == "completed" and failed == 0 and errors == 0 else 1
            )
            return ok(
                new_id("uiflow"),
                {
                    "directoryPath": resolved_dir,
                    "reportOutputPath": report_path,
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "total": int(last_data.get("total") or 0),
                        "passed": int(last_data.get("passed") or 0),
                        "failed": failed,
                        "errors": errors,
                        "skipped": int(last_data.get("skipped") or 0),
                        "exitCode": exit_code,
                        "raw": last_data,
                    },
                },
            )

    logger.error("[unityuiflow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(120.0, default_timeout_ms / 1000.0 + 300.0), last_status)
    await _get_facade().unityuiflow_cancel(execution_id)
    return fail(
        new_id("uiflow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for unityuiflow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )


async def _run_unity_uiflow_batch(
    yaml_paths: list[str],
    batch_size: int,
    batch_offset: int,
    headed: bool,
    report_output_path: str,
    screenshot_path: str,
    screenshot_on_failure: bool,
    stop_on_first_failure: bool,
    continue_on_step_failure: bool,
    default_timeout_ms: int,
    pre_step_delay_ms: int,
    enable_verbose_log: bool,
    debug_on_failure: bool,
    total_all: int = 0,
) -> ToolResponse:
    resolved = []
    for p in yaml_paths:
        rp = str(Path(p).expanduser().resolve())
        if not Path(rp).is_file():
            return fail(
                new_id("uiflow"),
                "UIFLOW_YAML_NOT_FOUND",
                f"YAML file not found: {rp}",
                {"yamlPath": rp},
            )
        resolved.append(rp)

    # totalAll is the overall number of yaml files across all batches.
    # If not provided by caller, infer: resolved list is all files (no external slicing).
    effective_total_all = total_all if total_all > 0 else len(resolved)

    report_root = report_output_path.strip() or "Reports/upilot/UIFlowMcp"
    run_resp = await _get_facade().unityuiflow_run(
        yaml_paths=resolved,
        headed=headed,
        stop_on_first_failure=stop_on_first_failure,
        continue_on_step_failure=continue_on_step_failure,
        screenshot_on_failure=screenshot_on_failure,
        default_timeout_ms=default_timeout_ms,
        enable_verbose_log=enable_verbose_log,
        report_path=report_root,
        debug_on_failure=debug_on_failure,
        batch_size=batch_size,
        batch_offset=batch_offset,
        total_all=effective_total_all,
    )
    if not run_resp.ok:
        return run_resp

    run_data = run_resp.data or {}
    execution_id = str(run_data.get("executionId") or "")
    if not execution_id:
        return fail(
            new_id("uiflow"),
            "UIFLOW_EXECUTION_ID_MISSING",
            "unityuiflow.run did not return executionId",
            {"response": run_data},
        )

    # Deadline: allow enough time for the current batch (batch_size files)
    deadline = time.monotonic() + max(120.0, default_timeout_ms / 1000.0 * batch_size + 180.0)
    last_data = run_data
    last_status = ""
    last_progress_log = time.monotonic()
    while time.monotonic() < deadline:
        await asyncio.sleep(0.5)
        status_resp = await _get_facade().unityuiflow_results(execution_id)
        if not status_resp.ok:
            return status_resp
        last_data = status_resp.data or {}
        status = str(last_data.get("status") or "")
        if status != last_status:
            logger.info("[unityuiflow] execution %s status %s -> %s", execution_id[:8], last_status or "queued", status)
            last_status = status
        elif time.monotonic() - last_progress_log >= 10.0:
            current_yaml = last_data.get("currentYamlPath") or ""
            current_case = last_data.get("currentCaseName") or ""
            logger.info("[unityuiflow] execution %s polling 已等待=%.0fs 状态=%s 当前用例=%s", execution_id[:8], time.monotonic() - (deadline - max(120.0, default_timeout_ms / 1000.0 * batch_size + 120.0)), status, current_case or Path(current_yaml).name if current_yaml else "")
            last_progress_log = time.monotonic()
        if status in {"completed", "failed", "aborted"}:
            report_path = str(last_data.get("reportPath") or report_root)
            screenshots_root = screenshot_path.strip() or str(
                (Path(report_path) / "Screenshots").as_posix()
            )
            return ok(
                new_id("uiflow"),
                {
                    "yamlPaths": resolved,
                    "batchSize": batch_size,
                    "batchOffset": batch_offset,
                    "reportOutputPath": report_path,
                    "screenshotPath": screenshots_root,
                    "result": {
                        "executionId": execution_id,
                        "status": status,
                        "total": int(last_data.get("total") or 0),
                        "passed": int(last_data.get("passed") or 0),
                        "failed": int(last_data.get("failed") or 0),
                        "errors": int(last_data.get("errors") or 0),
                        "skipped": int(last_data.get("skipped") or 0),
                        "hasMore": bool(last_data.get("hasMore")),
                        "nextOffset": int(last_data.get("nextOffset") or 0),
                        "totalAll": int(last_data.get("totalAll") or 0),
                        "raw": last_data,
                    },
                },
            )

    logger.error("[unityuiflow] execution %s timed out after %.0fs, lastStatus=%s", execution_id[:8], max(120.0, default_timeout_ms / 1000.0 * batch_size + 120.0), last_status)
    await _get_facade().unityuiflow_cancel(execution_id)
    return fail(
        new_id("uiflow"),
        "UIFLOW_WAIT_TIMEOUT",
        f"Timed out waiting for unityuiflow execution: {execution_id}",
        {"executionId": execution_id, "lastStatus": last_data.get("status")},
    )


@mcp.tool(
    description=(
        "在 Unity 主线程延迟指定毫秒（不阻塞 Python）。用于等待编辑器布局；"
        "delayMs 最大 120000。"
    ),
)
async def unity_editor_delay(delayMs: int = 100) -> str:
    _log_tool_call("unity_editor_delay", {"delayMs": delayMs})
    r = await _get_facade().editor_delay(delay_ms=delayMs)
    return _log_tool_result("unity_editor_delay", _payload(r))


@mcp.tool(
    description="导航 Unity SceneView 视图（聚焦对象、设置视角、正交/透视切换等）。"
)
async def unity_sceneview_navigate(
    lookAtInstanceId: int = 0,
    pivot: dict | None = None,
    size: float = -1,
    rotation: dict | None = None,
    orthographic: bool | None = None,
    in2DMode: bool | None = None,
) -> str:
    _log_tool_call(
        "unity_sceneview_navigate",
        {
            "lookAtInstanceId": lookAtInstanceId,
            "pivot": pivot,
            "size": size,
            "rotation": rotation,
            "orthographic": orthographic,
            "in2DMode": in2DMode,
        },
    )
    r = await _get_facade().sceneview_navigate(
        look_at_instance_id=lookAtInstanceId,
        pivot=pivot,
        size=size,
        rotation=rotation,
        orthographic=orthographic,
        in_2d_mode=in2DMode,
    )
    return _log_tool_result("unity_sceneview_navigate", _payload(r))


_original_mcp_list_tools = mcp.list_tools


async def _list_tools_filtered_for_playmode():
    tools = await _original_mcp_list_tools()
    if not await _unity_is_playmode():
        return tools
    return [tool for tool in tools if tool.name not in _PLAYMODE_HIDDEN_TOOLS]


mcp.list_tools = _list_tools_filtered_for_playmode
mcp._mcp_server.list_tools()(_list_tools_filtered_for_playmode)


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
            _orchestrator.start(), name=f"unitypilot-ws-{ws_host}:{ws_port}"
        )
        try:
            await _wait_for_ws_listener(_orchestrator)
            logger.info(
                "unitypilot MCP server started  transport=%s  ws=%s:%s  http=%s:%s  path=%s",
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
        "unitypilot MCP server started  transport=%s  ws=%s:%s",
        transport,
        ws_host,
        ws_port,
    )
    await mcp.run_stdio_async()


if __name__ == "__main__":
    asyncio.run(main())
