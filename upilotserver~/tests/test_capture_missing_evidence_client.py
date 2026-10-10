"""Missing-segment evidence client contracts; real tool signatures, owned tmp files only."""
import asyncio
import copy
import importlib
import inspect
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from test_capture_evidence_client import FakeCapture


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1] / "scripts"))
    module = importlib.import_module("p1_capture_missing_evidence")
    monkeypatch.setattr(module, "PROJECT", tmp_path)
    monkeypatch.setattr(importlib.import_module("p0_reload_evidence"), "PROJECT", tmp_path)
    return module


class FakeMissing(FakeCapture):
    def __init__(self, project, *, failure=None, segment="initial"):
        super().__init__(project, failure=failure)
        self.segment = segment

    class Session(FakeCapture.Session):
        async def call_tool(self, proxy, request):
            fixture = self.fixture
            name, args = request["toolName"], request["args"]
            if name == "unity_console_capture_read":
                from upilot_mcp.mcp_tools.status_tools import unity_console_capture_read
                inspect.signature(unity_console_capture_read).bind(**args)
                fixture.calls.append((self.index, name, copy.deepcopy(args)))
                expected_count = (fixture.manifest or {}).get("segmentCount", 1)
                names = ["console.jsonl" if i == 0 else "console." + format(i, "03d") + ".jsonl"
                         for i in range(expected_count)]
                missing = next((name for name in names if not (fixture.directory / name).is_file()), None)
                data = {"ok": True, "sessionId": "owned", "logs": [fixture.records[0]],
                        "returnedCount": 1, "scanComplete": True, "continuationToken": "owned-cursor"}
                if missing:
                    data.update(ok=False, logs=[], returnedCount=0, scanComplete=False, continuationToken="",
                                error="CONSOLE_CAPTURE_SEGMENT_MISSING: " + missing)
                    if fixture.failure == "bad-ok":
                        data["ok"] = True
                    elif fixture.failure == "bad-scan":
                        data["scanComplete"] = True
                    elif fixture.failure == "bad-logs":
                        data["logs"] = [fixture.records[0]]
                    elif fixture.failure == "bad-cursor":
                        data["continuationToken"] = "wrong-success"
                    elif fixture.failure == "bad-session":
                        data["sessionId"] = "another"
                    elif fixture.failure in {"read-error", "restore-conflict"}:
                        if fixture.failure == "restore-conflict":
                            (fixture.directory / missing).write_bytes(b"do-not-overwrite")
                        raise RuntimeError("first business error")
                    elif fixture.failure == "cancel-read":
                        raise asyncio.CancelledError()
                    elif fixture.failure == "manifest-rewrite":
                        (fixture.directory / "summary.json").write_text("{}")
                return SimpleNamespace(isError=False, structuredContent={"ok": True, "data": data})
            if (name == "unity_console_capture_stop" and args.get("ownerToken") == fixture.token
                    and fixture.segment != "initial" and fixture.manifest is None):
                # Owned test artifacts model three files; live evidence must independently prove real rotation.
                count = 2 if fixture.failure == "insufficient-rotation" else 3
                groups = [fixture.records[i * len(fixture.records) // count:(i + 1) * len(fixture.records) // count]
                          for i in range(count)]
                blobs = [b"".join((json.dumps(record) + "\n").encode() for record in group) for group in groups]
                for i, blob in enumerate(blobs):
                    (fixture.directory / ("console.jsonl" if i == 0 else "console." + format(i, "03d") + ".jsonl")).write_bytes(blob)
                fixture.manifest = {"sessionId": "owned", "active": False,
                                    "fileBytes": sum(map(len, blobs)), "segmentCount": count,
                                    "sha256": hashlib.sha256(b"".join(blobs)).hexdigest(),
                                    "totalCount": len(fixture.records), "nextSequence": len(fixture.records),
                                    "lastError": "", "droppedCount": 0}
                for file in ("session.json", "summary.json"):
                    (fixture.directory / file).write_text(json.dumps(fixture.manifest))
            result = await super().call_tool(proxy, request)
            data = result.structuredContent.get("data", {})
            if name == "unity_queue_cleanup":
                data["isStale"] = False
                if fixture.failure == "queue-unknown":
                    data["complete"] = False
                elif fixture.failure == "shared-work":
                    data["items"] = [{"taskId": "other"}]
            elif name == "unity_console_capture_list" and fixture.failure == "existing-capture":
                data["activeCount"] = 1
            return result


def run(client, monkeypatch, tmp_path, failure=None, segment="initial"):
    fake = FakeMissing(tmp_path, failure=failure, segment=segment)
    monkeypatch.setattr(client, "connect", fake.connect)
    report = {"calls": [], "result": "unknown"}
    async def execute():
        await client.exercise(8011, report, asyncio.get_running_loop().time() + 60, segment)
    try:
        asyncio.run(execute())
    except BaseException as exc:
        return fake, report, exc
    return fake, report, None


def test_missing_file_and_cached_page_are_rejected_then_restored(client, monkeypatch, tmp_path):
    fake, report, error = run(client, monkeypatch, tmp_path)
    assert error is None and report["result"] == "observed"
    assert report["segmentRestored"] is True
    assert report["artifactsBefore"] == report["artifactsAfter"]
    assert fake.legitimate_stops == 1 and fake.active is False
    assert not (fake.directory / "console.jsonl.owned-backup").exists()
    reads = [args for _, name, args in fake.calls if name == "unity_console_capture_read"]
    assert len(reads) == 4 and reads[2]["continuationToken"] == reads[3]["continuationToken"]
    assert fake.token not in json.dumps(report)


@pytest.mark.parametrize("failure", ["bad-ok", "bad-scan", "bad-logs", "bad-cursor", "bad-session", "read-error", "cancel-read", "manifest-rewrite"])
def test_false_success_or_read_error_cannot_pass_and_file_is_restored(client, monkeypatch, tmp_path, failure):
    fake, report, error = run(client, monkeypatch, tmp_path, failure)
    assert error is not None and report["result"] == "unknown"
    assert report["segmentRestored"] is True
    assert (fake.directory / "console.jsonl").is_file()
    assert not (fake.directory / "console.jsonl.owned-backup").exists()
    assert fake.legitimate_stops == 1 and not fake.active


@pytest.mark.parametrize("failure", ["queue-unknown", "shared-work", "existing-capture", "not-ready", "dedicated-writer"])
def test_unsafe_preconditions_prevent_start(client, monkeypatch, tmp_path, failure):
    fake, report, error = run(client, monkeypatch, tmp_path, failure)
    assert error is not None
    assert not any(name == "unity_console_capture_start" for _, name, _ in fake.calls)
    assert fake.legitimate_stops == 0


@pytest.mark.parametrize("failure", ["lost-start", "missing-token", "lost-stop"])
def test_unknown_nonidempotent_receipts_are_never_replayed(client, monkeypatch, tmp_path, failure):
    fake, report, error = run(client, monkeypatch, tmp_path, failure)
    assert error is not None
    assert sum(name == "unity_console_capture_start" for _, name, _ in fake.calls) == 1
    assert fake.legitimate_stops == (1 if failure == "lost-stop" else 0)
    assert not (fake.directory / "console.jsonl.owned-backup").exists()
    assert fake.token not in json.dumps(report)


def test_restore_conflict_preserves_new_file_backup_and_first_error(client, monkeypatch, tmp_path):
    fake, report, error = run(client, monkeypatch, tmp_path, "restore-conflict")
    assert isinstance(error, RuntimeError)
    assert report["businessError"]["type"] == "RuntimeError" and report.get("cleanupError")
    assert (fake.directory / "console.jsonl").read_bytes() == b"do-not-overwrite"
    assert (fake.directory / "console.jsonl.owned-backup").is_file()
    assert fake.legitimate_stops == 1


def test_outer_transport_exit_cannot_leave_success_report(client, monkeypatch, tmp_path):
    async def exit_error(port, report, deadline, segment="initial"):
        report["result"] = "observed"
        raise ConnectionError("private text")
    monkeypatch.setattr(client, "exercise", exit_error)
    output = tmp_path / "evidence.json"
    code = asyncio.run(client.main(SimpleNamespace(port=8011, output=output)))
    assert code == 1 and json.loads(output.read_text())["result"] == "unknown"
    assert "private text" not in output.read_text()


@pytest.mark.parametrize("segment,index", [("middle", 1), ("final", 2)])
def test_rotated_position_and_cached_page_are_rejected_then_restored(client, monkeypatch, tmp_path, segment, index):
    fake, report, error = run(client, monkeypatch, tmp_path, segment=segment)
    assert error is None and report["result"] == "observed"
    assert report["missingSegment"] == {"position": segment, "name": "console." + format(index, "03d") + ".jsonl",
                                        "index": index, "segmentCount": 3}
    assert report["segmentRestored"] is True and report["artifactsBefore"] == report["artifactsAfter"]
    assert len(fake.records) == 39 and fake.legitimate_stops == 1 and not fake.active
    assert not list(fake.directory.glob("*.owned-backup"))
    reads = [args for _, name, args in fake.calls if name == "unity_console_capture_read"]
    assert len(reads) == 4 and reads[2]["continuationToken"] == reads[3]["continuationToken"]
    assert fake.token not in json.dumps(report)


@pytest.mark.parametrize("segment", ["middle", "final"])
def test_insufficient_real_rotation_never_moves_a_file(client, monkeypatch, tmp_path, segment):
    fake, report, error = run(client, monkeypatch, tmp_path, "insufficient-rotation", segment)
    assert isinstance(error, client.CallPreflightError)
    assert "at least three segments" in str(error)
    assert fake.legitimate_stops == 1 and not fake.active
    assert not list(fake.directory.glob("*.owned-backup"))
    assert not any(name == "unity_console_capture_read" for _, name, _ in fake.calls)


@pytest.mark.parametrize("segment", ["middle", "final"])
@pytest.mark.parametrize("failure", ["bad-cursor", "cancel-read", "manifest-rewrite"])
def test_rotated_failure_restores_exact_segment(client, monkeypatch, tmp_path, segment, failure):
    fake, report, error = run(client, monkeypatch, tmp_path, failure, segment)
    assert error is not None and report["result"] == "unknown"
    assert report["segmentRestored"] is True and fake.legitimate_stops == 1
    assert (fake.directory / report["missingSegment"]["name"]).is_file()
    assert not list(fake.directory.glob("*.owned-backup"))


@pytest.mark.parametrize("segment", ["middle", "final"])
def test_rotated_restore_conflict_never_overwrites(client, monkeypatch, tmp_path, segment):
    fake, report, error = run(client, monkeypatch, tmp_path, "restore-conflict", segment)
    assert isinstance(error, RuntimeError) and report["businessError"]["type"] == "RuntimeError"
    assert report.get("cleanupError") and fake.legitimate_stops == 1
    target = fake.directory / report["missingSegment"]["name"]
    assert target.read_bytes() == b"do-not-overwrite" and target.with_name(target.name + ".owned-backup").is_file()


def test_invalid_position_is_rejected_before_any_tool_call(client, monkeypatch, tmp_path):
    fake, report, error = run(client, monkeypatch, tmp_path, segment="../other")
    assert isinstance(error, client.CallPreflightError) and fake.calls == []


@pytest.mark.parametrize("name", ["console.jsonl", "console.001.jsonl", "console.002.jsonl"])
def test_raw_missing_contract_requires_exact_segment_and_empty_cursor(client, name):
    data = {"ok": False, "sessionId": "owned", "scanComplete": False, "logs": [],
            "continuationToken": "", "error": "CONSOLE_CAPTURE_SEGMENT_MISSING: " + name}
    client.require_missing(data, "owned", name)
    for change in ({"error": "CONSOLE_CAPTURE_SEGMENT_MISSING: another.jsonl"}, {"continuationToken": "unexpected"}):
        with pytest.raises(client.CallPreflightError):
            client.require_missing({**data, **change}, "owned", name)
