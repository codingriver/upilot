import asyncio
import json
import os
from pathlib import Path

from upilot_mcp.responses import ok
from upilot_mcp.domain.task_service import _operation_parse_object, _read_stable_artifact
from test_operation_runner_and_agent_rules import _OperationService


def _spec(**extra):
    return {
        "startCall": {"kind": "tool", "toolName": "start", "toolArgs": {}},
        "statusCall": {"kind": "tool", "toolName": "status", "toolArgs": {}},
        **extra,
    }


def test_direct_tool_json_parse_failure_has_unicode_diagnostic_on_start_and_is_persisted(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        malformed = '{"中文": }'

        async def broken_start(*_args, **_kwargs):
            return ok("start", {"result": malformed})

        service._operation_invoke = broken_start
        result = await service.operation_start(_spec())

        assert not result.ok and result.error.code == "OPERATION_RESULT_INVALID"
        diagnostic = result.error.detail["parseDiagnostic"]
        with __import__("pytest").raises(json.JSONDecodeError) as expected:
            json.loads(malformed)
        assert diagnostic["offset"] == expected.value.pos
        assert diagnostic["offsetUnit"] == "unicodeCharacter"
        stored = service.server.state.load_operations()[0]
        assert stored["parseDiagnostic"] == diagnostic
        assert stored["status"] == "RecoveryRequired"

    asyncio.run(run())


def test_direct_tool_json_parse_failure_on_status_never_promotes_terminal_state(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(_spec())
        operation_id = started.data["operationId"]
        original = service._operation_invoke

        async def broken_status(call, *args, **kwargs):
            if call["toolName"] == "status":
                return ok("status", {"payload": '{"状态": }'})
            return await original(call, *args, **kwargs)

        service._operation_invoke = broken_status
        result = await service.operation_status(operation_id)

        assert not result.ok and result.error.code == "OPERATION_RESULT_INVALID"
        assert result.error.detail["status"] == "RecoveryRequired"
        assert result.error.detail["terminal"] is False
        assert result.error.detail["parseDiagnostic"]["path"] is None

    asyncio.run(run())


def test_parse_diagnostic_keeps_unicode_offset_and_bounds_a_long_snippet() -> None:
    # Do not derive offsets from encoded bytes: callers need to index the
    # original Python/JSON text, including a multi-byte character prefix.
    malformed = '{"前缀":"' + ("值" * 300)
    _payload, error, diagnostic = _operation_parse_object(malformed)

    with __import__("pytest").raises(json.JSONDecodeError) as expected:
        json.loads(malformed)
    assert error
    assert diagnostic["offset"] == expected.value.pos
    assert diagnostic["offsetUnit"] == "unicodeCharacter"
    assert diagnostic["line"] == expected.value.lineno
    assert diagnostic["column"] == expected.value.colno
    assert len(diagnostic["snippet"]) == 256
    assert diagnostic["truncated"] is True


def test_project_mismatch_blocks_status_cancel_and_artifact_read_without_dispatch(tmp_path, monkeypatch):
    async def run():
        first_project = tmp_path / "first"
        second_project = tmp_path / "second"
        first_project.mkdir()
        second_project.mkdir()
        service = _OperationService(first_project, [])
        started = await service.operation_start(_spec(cancelCall={"kind": "tool", "toolName": "cancel", "toolArgs": {}}))
        operation_id = started.data["operationId"]
        service.server.session_manager.active.project_path = str(second_project)
        reads = []
        monkeypatch.setattr(
            "upilot_mcp.domain.task_service._read_stable_artifact",
            lambda path: reads.append(path) or ({}, ""),
        )

        status = await service.operation_status(operation_id)
        cancel = await service.operation_cancel(operation_id)
        collected = await service.operation_collect_artifacts(operation_id)
        waited = await service.operation_wait(operation_id, timeout_s=0.01)

        for result in (status, cancel, collected, waited):
            assert not result.ok and result.error.code == "OPERATION_PROJECT_MISMATCH"
            assert result.error.detail["sideEffectsMayHaveOccurred"] is False
            assert result.error.detail["operationProject"] == str(first_project.resolve())
            assert result.error.detail["connectedProject"] == str(second_project.resolve())
        assert [name for name, _ in service.calls] == ["start"]
        assert reads == []

    asyncio.run(run())


def test_artifact_changed_during_single_handle_read_is_rejected(tmp_path, monkeypatch):
    report = tmp_path / "changing.bin"
    report.write_bytes(b"before")
    original_stat = report.stat()
    original_fstat = os.fstat
    fstat_calls = 0

    def changing_fstat(fd):
        nonlocal fstat_calls
        value = original_fstat(fd)
        fstat_calls += 1
        if fstat_calls == 1:
            os.utime(report, ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns + 1_000_000_000))
        return value

    monkeypatch.setattr("upilot_mcp.domain.task_service.os.fstat", changing_fstat)
    evidence, error = _read_stable_artifact(report)

    assert evidence == {}
    assert error == "ARTIFACT_CHANGED_DURING_READ"


def test_canceling_artifact_collection_keeps_last_persisted_snapshot_and_business_running(tmp_path, monkeypatch):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(_spec())
        operation_id = started.data["operationId"]
        entered = asyncio.Event()
        never = asyncio.Event()

        async def pending_read(*_args, **_kwargs):
            entered.set()
            await never.wait()

        monkeypatch.setattr(asyncio, "to_thread", pending_read)
        collecting = asyncio.create_task(service.operation_collect_artifacts(operation_id))
        await entered.wait()
        collecting.cancel()
        with __import__("pytest").raises(asyncio.CancelledError):
            await collecting

        state = service._operations[operation_id]
        assert state.get("artifactCollectionSequence", 0) == 0
        assert state["artifacts"] == {}
        assert state["status"] == "Running"
        assert [name for name, _ in service.calls] == ["start"]

    asyncio.run(run())


def test_late_artifact_collection_cannot_overwrite_a_newer_persisted_snapshot(tmp_path, monkeypatch):
    async def run():
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(_spec())
        operation_id = started.data["operationId"]
        first_started = asyncio.Event()
        release_first = asyncio.Event()
        calls = 0

        async def staggered_to_thread(_collector, _snapshot, _max_tail_chars):
            nonlocal calls
            calls += 1
            if calls == 1:
                first_started.set()
                await release_first.wait()
                return {"report": {"kind": "metadata", "value": "old"}}, []
            return {"report": {"kind": "metadata", "value": "new"}}, []

        monkeypatch.setattr(asyncio, "to_thread", staggered_to_thread)
        first = asyncio.create_task(service.operation_collect_artifacts(operation_id))
        await first_started.wait()
        newer = await service.operation_collect_artifacts(operation_id)
        release_first.set()
        older = await first

        assert newer.ok and newer.data["artifactCollectionSequence"] == 2
        assert newer.data["artifacts"]["report"]["value"] == "new"
        assert older.ok and older.data["collectionSuperseded"] is True
        state = service.server.state.load_operations()[0]
        assert state["artifactCollectionSequence"] == 2
        assert state["artifacts"]["report"]["value"] == "new"

    asyncio.run(run())


def test_terminal_status_surfaces_deferred_artifact_persistence_failure_without_changing_business_result(tmp_path):
    async def run():
        service = _OperationService(tmp_path, [{
            "status": "Failed", "failureSignature": "business-failure",
        }])
        started = await service.operation_start(_spec())
        operation_id = started.data["operationId"]
        original_save = service.server.state.save_operation

        def fail_collection_save(state):
            if state.get("artifactCollectionSequence") == 1:
                raise OSError("injected SQLite failure")
            return original_save(state)

        service.server.state.save_operation = fail_collection_save
        result = await service.operation_status(operation_id)

        assert result.ok
        assert result.data["status"] == "Failed"
        assert result.data["failureSignature"] == "business-failure"
        assert result.data["collectionPersisted"] is False
        assert result.data["artifactPersistenceError"] == "injected SQLite failure"
        assert result.data["artifactCollectionSequence"] == 0
        assert service._operations[operation_id].get("artifactCollectionSequence", 0) == 0

    asyncio.run(run())


def test_status_artifact_scalars_are_not_reinterpreted_as_project_paths(tmp_path, monkeypatch):
    """Keep the report path/file read boundary independent from scalar siblings.

    This reproduces the StartupSmoke shape that previously let a SHA256 value
    become a project-relative path during collection.
    """
    async def run():
        report = tmp_path / "report.json"
        report.write_text('{"ok":true}', encoding="utf-8")
        service = _OperationService(tmp_path, [])
        started = await service.operation_start(_spec(artifactRules={
            "fieldKinds": {
                "reportPath": "file",
                "reportBytes": "bytes",
                "reportSha256": "sha256",
            },
        }))
        operation_id = started.data["operationId"]
        service._operations[operation_id]["lastStatusData"] = {
            "artifacts": {
                "reportPath": str(report),
                "reportBytes": report.stat().st_size,
                "reportSha256": "a" * 64,
            },
        }
        reads = []
        original_read = _read_stable_artifact

        def count_reads(path):
            reads.append(Path(path))
            return original_read(path)

        monkeypatch.setattr("upilot_mcp.domain.task_service._read_stable_artifact", count_reads)
        collected = await service.operation_collect_artifacts(operation_id)

        assert collected.ok
        assert reads == [report]
        assert collected.data["artifactErrors"] == []
        assert collected.data["artifacts"]["reportPath"]["kind"] == "file"
        assert collected.data["artifacts"]["reportBytes"] == {
            "kind": "bytes", "value": report.stat().st_size,
        }
        assert collected.data["artifacts"]["reportSha256"] == {
            "kind": "sha256", "value": "a" * 64,
        }

    asyncio.run(run())
