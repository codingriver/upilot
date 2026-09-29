"""Finite local Editor queue UI transport; shares the MCP authorization and token checks."""
from starlette.responses import JSONResponse


async def cleanup_endpoint(request, facade):
    # No browser-origin callers, proxy headers or remote peers. The custom header also
    # prevents a browser form from dispatching a state-changing request.
    if (not request.client or request.client.host not in {"127.0.0.1", "::1"}
            or request.url.hostname not in {"127.0.0.1", "localhost", "::1"}
            or request.headers.get("origin") or request.headers.get("x-forwarded-for")
            or request.headers.get("x-upilot-queue") != "1"
            or request.headers.get("content-type", "").split(";")[0] != "application/json"):
        return JSONResponse({"ok": False, "error": "QUEUE_UI_REQUEST_REFUSED"}, status_code=403)
    try:
        body = bytearray()
        async for chunk in request.stream():
            body.extend(chunk)
            if len(body) > 16384:
                raise ValueError("Request too large")
        import json
        args = json.loads(body)
        if (not isinstance(args, dict) or set(args) - {"targetType", "targetId", "action", "reason",
                "dryRun", "confirmToken", "expectedProjectPath"}
                or args.get("targetType") != "All"
                or args.get("action") not in {"force_clear_all", "force_clear_status"}
                or not args.get("expectedProjectPath")):
            raise ValueError("Exact project and finite bulk action required")
        response = await facade.queue_cleanup(target_type=args["targetType"], target_id=args.get("targetId", ""),
            action=args["action"], reason=args.get("reason", ""), dry_run=args.get("dryRun", True),
            confirm_token=args.get("confirmToken", ""), expected_project_path=args["expectedProjectPath"])
        return JSONResponse({"ok": response.ok, "data": response.data,
                             "error": response.error.code if response.error else None})
    except (ValueError, TypeError, KeyError):
        return JSONResponse({"ok": False, "error": "QUEUE_UI_INVALID_REQUEST"}, status_code=400)
