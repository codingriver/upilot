from contextvars import ContextVar

OPERATION_ID = ContextVar("upilot_operation_id", default="")
TASK_TOOL = ContextVar("upilot_task_tool", default="")
