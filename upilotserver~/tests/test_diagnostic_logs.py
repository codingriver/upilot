import io
import logging

from upilot_mcp.diagnostic_logs import RepeatDiagnosticFilter


def record(message="failure", level=logging.ERROR, scope="", terminal=False):
    result = logging.LogRecord("upilot.diagnostic-test", level, __file__, 1, message, (), None)
    result.diagnostic_scope = scope
    result.diagnostic_terminal = terminal
    return result


def test_first_immediate_repeat_count_idle_and_new_window():
    now = [0.0]
    output = []
    gate = RepeatDiagnosticFilter(output.append, lambda: now[0])
    assert gate.filter(record())
    for _ in range(17):
        assert not gate.filter(record())
    now[0] = 10
    assert gate.filter(record("ordinary observation", logging.INFO))
    assert len(output) == 1 and "count=17" in output[0].getMessage()
    now[0] = 20
    assert gate.filter(record())  # A new fault after a quiet window is immediate.
    now[0] = 30
    gate.filter(record("ordinary observation", logging.INFO))
    assert len(output) == 1  # No unchanged/healthy periodic messages.


def test_handlers_share_one_decision_and_preserve_exception():
    streams = [io.StringIO(), io.StringIO()]
    gate = RepeatDiagnosticFilter(clock=lambda: 0)
    handlers = [logging.StreamHandler(stream) for stream in streams]
    for handler in handlers:
        handler.addFilter(gate)
    try:
        raise RuntimeError("original stack")
    except RuntimeError:
        import sys
        first = record()
        first.exc_info = sys.exc_info()
        second = record()
        second.exc_info = first.exc_info
    for item in [first, second]:
        for handler in handlers:
            handler.handle(item)
    assert streams[0].getvalue() == streams[1].getvalue()
    assert streams[0].getvalue().count("RuntimeError: original stack") == 1
    assert "test_handlers_share_one_decision" in streams[0].getvalue()


def test_tasks_roots_escalation_terminal_and_capacity():
    summaries = []
    gate = RepeatDiagnosticFilter(summaries.append, lambda: 0)
    assert gate.filter(record(scope="one", level=logging.WARNING))
    assert not gate.filter(record(scope="one", level=logging.WARNING))
    assert gate.filter(record(scope="one", level=logging.ERROR))
    assert gate.filter(record(scope="two"))
    assert gate.filter(record("different root", scope="one"))
    assert gate.filter(record(scope="one", terminal=True))
    assert not gate.filter(record(scope="one", terminal=True))
    assert "count=1" in summaries[0].getMessage()
    gate.flush("one")
    assert all(key[1] != "one" for key in gate._entries)
    for i in range(300):
        gate.filter(record(str(i)))
    assert len(gate._entries) == gate.capacity


def test_file_failure_keeps_stderr_stack_and_aggregates(monkeypatch, capsys, tmp_path):
    from upilot_mcp import daily_logs
    gate = RepeatDiagnosticFilter(daily_logs._stderr_diagnostic, clock=lambda: 0)
    monkeypatch.setattr(daily_logs, "_file_diagnostics", gate)
    try:
        raise OSError("disk blocked")
    except OSError as error:
        daily_logs.report_log_failure("write", tmp_path, error)
        daily_logs.report_log_failure("write", tmp_path, error)
    text = capsys.readouterr().err
    assert "OSError: disk blocked" in text and "test_file_failure" in text
    gate.flush()
    assert "count=1" in capsys.readouterr().err


def test_real_server_logging_sends_same_first_and_summary_to_both_sinks(tmp_path):
    import os
    import subprocess
    import sys
    from datetime import datetime
    base = tmp_path / "mcp-server.log"
    result = subprocess.run([sys.executable, "-c", "\n".join([
        "from upilot_mcp.mcp_main import _setup_logging",
        "import logging",
        "_setup_logging()",
        "logger = logging.getLogger('upilot.integration')",
        "for i in range(4):",
        "    try: raise RuntimeError('repeat-original-stack')",
        "    except RuntimeError: logger.exception('same stable fault')",
    ])], env=dict(os.environ, UPILOT_LOG_FILE=str(base)), capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stderr
    file_text = base.with_name(f"mcp-server-{datetime.now():%Y-%m-%d}.log").read_text(encoding="utf-8")
    for text in (file_text, result.stderr):
        assert text.count("RuntimeError: repeat-original-stack") == 1
        assert "repeated count=3" in text


def test_mcp_call_file_fault_also_reaches_general_server_file_without_recursion(tmp_path):
    import os
    import subprocess
    import sys
    from datetime import datetime
    base = tmp_path / "mcp-server.log"
    (tmp_path / "Logs").write_text("blocks the independent call log")
    result = subprocess.run([sys.executable, "-c", "\n".join([
        "from pathlib import Path",
        "from upilot_mcp.mcp_main import _setup_logging",
        "from upilot_mcp import mcp_stdio_server as runtime",
        "import time, os",
        "_setup_logging()",
        "runtime._mcp_tool_log_project_root = lambda: Path(os.environ['PROBE_PROJECT'])",
        "runtime._write_mcp_tool_call_log('csharp_eval', {'code': '1'}, call_id='probe-call', started_at='now', started_clock=time.perf_counter(), event='call.start')",
    ])], env=dict(os.environ, UPILOT_LOG_FILE=str(base), PROBE_PROJECT=str(tmp_path)), capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stderr
    file_text = base.with_name(f"mcp-server-{datetime.now():%Y-%m-%d}.log").read_text(encoding="utf-8")
    for text in (file_text, result.stderr):
        assert text.count("ERROR log MCP call write path=") == 1
        assert "Traceback" in text and "FileExistsError" in text
