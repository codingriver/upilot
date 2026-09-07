import asyncio
import hashlib
import json
import os
import sqlite3
from types import SimpleNamespace

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.responses import ok
from upilot_mcp.state_store import StateStore


class Service(ResourceDomainService):
    def __init__(self, root, state):
        self.server = SimpleNamespace(
            state=state, session_manager=SimpleNamespace(active=SimpleNamespace(project_path=str(root))),
        )
        self.scheduled = 0

    def _schedule_write_batch_resume(self):
        self.scheduled += 1


@pytest.fixture
def service(tmp_path, monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    state = StateStore()
    state.configure_project(str(tmp_path))
    state.editor.authoritative = True
    state.editor.connected = True
    state.editor.play_mode_state = "play"
    state.editor.updated_at = 10**15
    return Service(tmp_path, state)


def register(service, paths=None, deleted=None, automatic=True):
    return asyncio.run(service.write_batch_register(paths or [], automatic, deleted_paths=deleted))


def test_deleted_only_batch_is_durable_and_uses_server_timestamp(service, tmp_path, monkeypatch):
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 10**15)
    deleted = str(tmp_path / "Gone.cs")
    result = register(service, deleted=[deleted])
    assert result.ok and result.data["status"] == "deferred"
    assert result.data["writeBatchCreatedAt"] >= 10**15
    assert result.data["paths"] == []
    assert result.data["deletedPaths"] == [deleted]
    assert result.data["changes"] == [{"path": deleted, "kind": "delete", "contentSha256": ""}]
    assert result.data["filesHashScope"] == "changes-v1"
    assert service.scheduled == 0
    restored = StateStore()
    restored.configure_project(str(tmp_path))
    recovered = restored.get_write_batch(result.data["writeBatchId"])
    assert recovered["deletedPaths"] == [deleted]
    assert recovered["filesSha256"] == result.data["filesSha256"]
    assert recovered["changes"] == result.data["changes"]
    assert restored.pending_write_batches()[0]["deletedPaths"] == [deleted]


def test_move_and_mixed_writes_preserve_complete_manifest_hash(service, tmp_path):
    old = tmp_path / "Old.cs"
    new = tmp_path / "New.cs"
    other = tmp_path / "Other.asmdef"
    new.write_text("class New {}", encoding="utf-8")
    other.write_text("{}", encoding="utf-8")
    first = register(service, [str(new)], [str(old)]).data
    second = register(service, [str(other)]).data
    assert second["writeBatchId"] == first["writeBatchId"] and second["coalesced"]
    assert second["paths"] == sorted([str(new), str(other)])
    assert second["deletedPaths"] == [str(old)]
    assert len(second["changes"]) == 3
    encoded = json.dumps(second["changes"], sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    assert second["filesSha256"] == hashlib.sha256(encoded).hexdigest()
    assert second["filesSha256"] != first["filesSha256"]
    assert {item["contentSha256"] for item in second["changes"] if item["kind"] == "write"} == {
        hashlib.sha256(new.read_bytes()).hexdigest(), hashlib.sha256(other.read_bytes()).hexdigest(),
    }


def test_last_effective_change_wins_when_delete_is_followed_by_recreate(service, tmp_path):
    path = tmp_path / "A.cs"
    first = register(service, deleted=[str(path)]).data
    path.write_text("class A {}", encoding="utf-8")
    recreated = register(service, [str(path), str(path)]).data
    assert recreated["writeBatchId"] == first["writeBatchId"]
    assert recreated["deletedPaths"] == [] and recreated["paths"] == [str(path)]
    assert len(recreated["changes"]) == 1
    path.unlink()
    deleted = register(service, deleted=[str(path)]).data
    assert deleted["writeBatchId"] == first["writeBatchId"]
    assert deleted["paths"] == [] and deleted["deletedPaths"] == [str(path)]


@pytest.mark.parametrize("case,code", [
    ("empty", "INVALID_WRITE_BATCH"), ("bad_type", "INVALID_WRITE_BATCH"),
    ("blank", "INVALID_WRITE_BATCH"), ("missing_write", "WRITE_BATCH_FILE_NOT_FOUND"),
    ("existing_delete", "WRITE_BATCH_DELETED_PATH_EXISTS"),
    ("overlap", "WRITE_BATCH_PATH_CONFLICT"), ("unsupported", "UNSUPPORTED_WRITE_BATCH_FILE"),
    ("outside", "WRITE_BATCH_PATH_OUTSIDE_PROJECT"),
])
def test_invalid_changes_never_register_or_schedule(service, tmp_path, case, code):
    path = tmp_path / "A.cs"
    path.write_text("class A {}", encoding="utf-8")
    requests = {
        "empty": ([], []), "bad_type": ([], "Gone.cs"), "blank": ([], [" "]),
        "missing_write": (["Gone.cs"], []), "existing_delete": ([], [str(path)]),
        "overlap": ([str(path)], [str(path)]), "unsupported": ([], ["A.cs.meta"]),
        "outside": ([], [str(tmp_path.parent / "Outside.cs")]),
    }
    paths, deleted = requests[case]
    result = register(service, paths, deleted)
    assert not result.ok and result.error.code == code
    assert service.server.state.pending_write_batches() == []
    assert service.scheduled == 0 and path.read_text() == "class A {}"


def test_write_permission_is_required_even_for_deleted_paths(service, monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", False)
    result = register(service, deleted=["Gone.cs"])
    assert not result.ok and result.error.code == "WRITE_ACCESS_NOT_APPROVED"
    assert service.server.state.pending_write_batches() == []


def test_local_upm_root_allows_absent_paths_but_not_escaped_neighbors(tmp_path, monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)
    root, package = tmp_path / "project", tmp_path / "local-package"
    packages = root / "Packages"
    packages.mkdir(parents=True)
    package.mkdir()
    (packages / "manifest.json").write_text(
        json.dumps({"dependencies": {"com.test": "file:../../local-package"}}), encoding="utf-8",
    )
    state = StateStore()
    target = Service(root, state)
    result = register(target, deleted=[str(package / "Gone.cs")], automatic=False)
    assert result.ok and result.data["deletedPaths"] == [str(package / "Gone.cs")]
    result = register(target, deleted=[str(package / ".." / "Outside.cs")], automatic=False)
    assert not result.ok and result.error.code == "WRITE_BATCH_PATH_OUTSIDE_PROJECT"


def test_legacy_database_migration_keeps_old_records_separate(tmp_path, monkeypatch):
    monkeypatch.setattr("upilot_mcp.state_store._now_ms", lambda: 1000)
    database = tmp_path / "Library/UPilot/ServerState/state-v2.sqlite3"
    database.parent.mkdir(parents=True)
    with sqlite3.connect(database) as db:
        db.execute("CREATE TABLE write_batches (write_batch_id TEXT PRIMARY KEY, project_path TEXT NOT NULL, operation_id TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, status TEXT NOT NULL, compile_when_edit_mode INTEGER NOT NULL, paths_json TEXT NOT NULL, files_sha256 TEXT NOT NULL, compile_operation_id TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '')")
        db.execute("INSERT INTO write_batches VALUES(?,?,?,?,?,?,?,?,?,?,?)", (
            "legacy", str(tmp_path.resolve()), "op-old", 900, 950, "deferred", 1,
            json.dumps([str(tmp_path / "Old.cs")]), "legacy-digest", "", "",
        ))
    state = StateStore()
    state.configure_project(str(tmp_path))
    legacy = state.get_write_batch("legacy")
    assert legacy["filesSha256"] == "legacy-digest"
    assert legacy["changes"] is None and legacy["deletedPaths"] == []
    new = state.register_write_batch([], created_at=0, files_sha256="", compile_when_edit_mode=True,
                                     changes=[{"path": str(tmp_path / "Gone.cs"), "kind": "delete", "contentSha256": ""}])
    assert new["writeBatchId"] != "legacy" and not new["coalesced"]
    assert len(state.pending_write_batches()) == 2
    assert state.get_write_batch("legacy") == legacy


def test_new_batches_do_not_mix_compile_authorization(service):
    first = register(service, deleted=["A.cs"], automatic=False).data
    second = register(service, deleted=["B.cs"], automatic=True).data
    assert second["writeBatchId"] != first["writeBatchId"]
    assert service.server.state.get_write_batch(first["writeBatchId"])["compileWhenEditMode"] is False


def test_delete_batch_resumes_once_after_editmode(service, monkeypatch):
    result = register(service, deleted=["Gone.cs"])
    calls = []

    async def no_sleep(_delay):
        pass

    async def compiled(**args):
        calls.append(args)
        return ok("compile", {"phase": "completed", "correlationVerified": True})

    monkeypatch.setattr(asyncio, "sleep", no_sleep)
    monkeypatch.setattr(service, "safe_compile_and_wait", compiled, raising=False)
    asyncio.run(service._resume_pending_write_batches())
    assert calls == []
    service.server.state.editor.play_mode_state = "edit"
    asyncio.run(service._resume_pending_write_batches())
    asyncio.run(service._resume_pending_write_batches())
    assert len(calls) == 1 and calls[0]["write_batch_id"] == result.data["writeBatchId"]
    assert service.server.state.get_write_batch(result.data["writeBatchId"])["status"] == "verified"


def test_new_input_time_is_not_older_than_future_file_mtime(service, tmp_path):
    path = tmp_path / "Future.cs"
    path.write_text("class Future {}", encoding="utf-8")
    os.utime(path, (2_000_000_000, 2_000_000_000))
    result = register(service, [str(path)])
    assert result.ok and result.data["writeBatchCreatedAt"] >= 2_000_000_000_000
