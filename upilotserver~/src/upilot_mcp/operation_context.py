from collections.abc import Mapping
from contextvars import ContextVar
import unicodedata

OPERATION_ID = ContextVar("upilot_operation_id", default="")
TASK_TOOL = ContextVar("upilot_task_tool", default="")
REQUEST_ORIGIN = ContextVar("upilot_request_origin", default=None)
ORIGIN_META_KEY = "io.github.codingriver.upilot/origin"


def normalize_request_origin(value) -> dict:
    """Bounded self-declared labels, never authorization or a transport identity."""
    value = value if isinstance(value, Mapping) else {}
    result = {"status": "unknown", "clientId": "", "chatId": ""}
    for key in ("clientId", "chatId"):
        label = value.get(key)
        if (not isinstance(label, str) or len(label) > 128
                or any(unicodedata.category(char).startswith("C") for char in label)):
            continue
        result[key] = label.strip()
    if result["clientId"] or result["chatId"]:
        result["status"] = "self_declared"
    return result


def request_origin_from_context(context) -> dict:
    """Read only namespaced MCP metadata; missing request context is ordinary."""
    try:
        meta = context.request_context.meta
        if not isinstance(meta, Mapping):
            # RequestParams.Meta is a Pydantic model whose extra keys carry metadata.
            meta = getattr(meta, "model_extra", None)
        return normalize_request_origin(meta.get(ORIGIN_META_KEY) if isinstance(meta, Mapping) else None)
    except Exception:
        return normalize_request_origin(None)
