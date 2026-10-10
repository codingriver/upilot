from __future__ import annotations

from .models import ToolError, ToolResponse
from .protocol import now_ms


def ok(
    request_id: str,
    data: dict,
    *,
    context: dict | None = None,
    timing: dict | None = None,
) -> ToolResponse:
    return ToolResponse(
        ok=True,
        data=data,
        error=None,
        request_id=request_id,
        timestamp=now_ms(),
        context=context,
        timing=timing,
    )


def fail(
    request_id: str,
    code: str,
    message: str,
    detail: dict | None = None,
    *,
    context: dict | None = None,
    timing: dict | None = None,
) -> ToolResponse:
    return ToolResponse(
        ok=False,
        data=None,
        error=ToolError(code=code, message=message, detail=detail or {}),
        request_id=request_id,
        timestamp=now_ms(),
        context=context,
        timing=timing,
    )
