import asyncio
import copy
import hashlib
import importlib.util
import json
from contextlib import asynccontextmanager
from pathlib import Path
from types import SimpleNamespace

import pytest


spec = importlib.util.spec_from_file_location(
    "acceptance_evidence_client",
    Path(__file__).resolve().parents[1] / "scripts" / "p0_acceptance_call.py",
)
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


@pytest.fixture
def endpoint(tmp_path, monkeypatch):
    project = tmp_path / "Tests~" / "UPilotTest2022"
    monkeypatch.setattr(client, "__file__", str(tmp_path / "upilotserver~" / "scripts" / "p0_acceptance_call.py"))
    state = SimpleNamespace(
        opened=0, calls=[], fail_at=None, exception=TimeoutError("private transport text"),
        status={"ok": True, "data": {
            "connected": True, "serverReady": True,
            "paths": {"unityProjectAbsolute": str(project)},
            "runtimeIdentity": {"mcpServer": {"processId": 123}, "unityEditor": {"processId": 456}},
            "session": {"sessionId": "bridge-id", "identityContractVersion": 1, "unityVersion": "2022.3"},
        }},
        result={"ok": True, "data": {"status": "completed", "runGuid": "run-original"}},
    )

    class Session:
        async def initialize(self):
            pass

        async def call_tool(self, name, args):
            assert name == "unity_tool_call"
            state.calls.append(copy.deepcopy(args))
            if state.fail_at == len(state.calls):
                raise state.exception
            data = state.status if len(state.calls) == 1 else state.result
            return SimpleNamespace(isError=False, content=[], structuredContent=data)

    @asynccontextmanager
    async def transport(url):
        assert url == "http://127.0.0.1:8017/mcp"
        state.opened += 1
        yield None, None, None

    @asynccontextmanager
    async def session(*_):
        yield Session()

    monkeypatch.setattr(client, "streamable_http_client", transport)
    monkeypatch.setattr(client, "ClientSession", session)
    state.project = project
    state.argv = [
        "--project", "UPilotTest2022", "--port", "8017",
        "--tool", "unity_test_results", "--args", '{"runGuid":"run-original"}',
        "--output", "observation.json",
    ]
    state.evidence = lambda: json.loads(
        (project / "Log" / "P0P1" / "observation.json").read_text(encoding="utf-8"))
    return state


def test_alternate_endpoint_identity_and_bounded_summary(endpoint, capsys):
    endpoint.status["data"]["contextPadding"] = "not-for-stdout" * 2000
    assert asyncio.run(client.main(endpoint.argv)) == 0
    output = json.loads(capsys.readouterr().out)
    assert len(endpoint.calls) == 2
    assert endpoint.calls[0]["args"]["forceFresh"] is True
    assert output["connection"]["actualProject"] == str(endpoint.project)
    assert output["connection"]["serverProcessId"] == 123
    assert output["connection"]["unityProcessId"] == 456
    assert output["connection"]["deploymentFreshness"] == "unverified"
    assert output["data"]["runGuid"] == "run-original"
    assert "not-for-stdout" not in json.dumps(output)
    assert output["transport"]["sendState"] == "response_received"
    saved = Path(output["path"]).read_bytes()
    assert output["sha256"] == hashlib.sha256(saved).hexdigest()
    assert output["bytes"] == len(saved)


@pytest.mark.parametrize("problem", ["wrong_project", "disconnected", "server_not_ready"])
def test_identity_or_connection_failure_never_dispatches_requested_tool(endpoint, problem):
    if problem == "wrong_project":
        # Same basename elsewhere does not satisfy the exact repository path.
        endpoint.status["data"]["paths"]["unityProjectAbsolute"] = str(endpoint.project.parent / "other" / endpoint.project.name)
    else:
        endpoint.status["data"]["connected" if problem == "disconnected" else "serverReady"] = False
    assert asyncio.run(client.main(endpoint.argv)) == 1
    assert len(endpoint.calls) == 1
    assert endpoint.evidence()["transport"]["sendState"] == "not_sent"


@pytest.mark.parametrize("fail_at,send_state", [(1, "not_sent"), (2, "sent_unknown")])
@pytest.mark.parametrize("error", [TimeoutError, RuntimeError, asyncio.CancelledError])
def test_transport_failure_has_no_replay_or_raw_traceback(endpoint, capsys, fail_at, send_state, error):
    endpoint.fail_at = fail_at
    endpoint.exception = error("unexpected-secret-plaintext")
    assert asyncio.run(client.main(endpoint.argv)) == 1
    assert len(endpoint.calls) == fail_at
    captured = capsys.readouterr()
    assert "unexpected-secret-plaintext" not in captured.out + captured.err
    assert "unexpected-secret-plaintext" not in json.dumps(endpoint.evidence())
    assert endpoint.evidence()["transport"]["sendState"] == send_state


@pytest.mark.parametrize("tool,args", [
    ("unity_console_capture_start", {}),
    ("unity_console_capture_stop", {"sessionId": "capture"}),
    ("unity_task_start", {"toolName": "unity_console_capture_start", "toolArgs": {}}),
    ("unity_tool_call", {"toolName": "unity_console_capture_stop", "args": {}}),
    ("unity_operation_start", {"jobSpec": {"steps": [{"tool": "unity_console_capture_start"}]}}),
    ("unity_test_results", {"nested": {"owner_token": "private-owner-value"}}),
    ("unity_test_results", {"Authorization": "Bearer private-auth-value"}),
])
def test_ownership_or_credential_calls_are_rejected_before_connection(endpoint, capsys, tool, args):
    endpoint.argv[5] = tool
    endpoint.argv[7] = json.dumps(args)
    assert asyncio.run(client.main(endpoint.argv)) == 1
    assert endpoint.opened == 0 and endpoint.calls == []
    saved = json.dumps(endpoint.evidence())
    output = capsys.readouterr()
    for secret in ("private-owner-value", "private-auth-value"):
        assert secret not in saved + output.out + output.err
    assert endpoint.evidence()["transport"]["sendState"] == "not_sent"


def test_malformed_arguments_are_not_saved(endpoint, capsys):
    endpoint.argv[7] = '{"ownerToken":"malformed-private-value"'
    assert asyncio.run(client.main(endpoint.argv)) == 1
    assert endpoint.opened == 0
    assert "malformed-private-value" not in json.dumps(endpoint.evidence()) + capsys.readouterr().out


def test_redaction_covers_result_error_echoes_and_does_not_mutate_arguments(endpoint, capsys):
    endpoint.result = {
        "ok": False, "data": None,
        "error": {"code": "REJECTED", "message": "echo: private-response-value",
                  "detail": {"ownerToken": "private-response-value", "firstFailure": [
                      {"cookie": "private-cookie-value"},
                      '{"apiKey":"embedded-private-value"}',
                      "Authorization: Bearer header-private-value",
                  ]}},
    }
    original = copy.deepcopy(endpoint.result)
    assert asyncio.run(client.main(endpoint.argv)) == 1
    assert endpoint.result == original
    captured = capsys.readouterr()
    combined = json.dumps(endpoint.evidence()) + captured.out + captured.err
    for secret in ("private-response-value", "private-cookie-value", "embedded-private-value", "header-private-value"):
        assert secret not in combined
    assert endpoint.evidence()["transport"]["sendState"] == "response_received"
    assert json.loads(captured.out)["error"] == "REJECTED"


def test_failed_evidence_write_reports_no_raw_exception(endpoint, monkeypatch, capsys):
    def fail(*_):
        raise PermissionError("private-file-error")

    monkeypatch.setattr(client, "_write_observation", fail)
    assert asyncio.run(client.main(endpoint.argv)) == 1
    captured = capsys.readouterr()
    assert "private-file-error" not in captured.out + captured.err
    assert json.loads(captured.err)["evidenceWritten"] is False


def test_output_cannot_escape_project_evidence_directory(endpoint):
    endpoint.argv[-1] = "../../../../outside.json"
    with pytest.raises(ValueError, match="Evidence must remain"):
        asyncio.run(client.main(endpoint.argv))
    assert endpoint.opened == 0
