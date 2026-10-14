"""On-demand selected live-code evidence, not full deployment attestation."""
import hashlib
import sys
import types

import pytest

from upilot_mcp import source_identity


def owned_module(tmp_path, monkeypatch, text, member="work"):
    path = tmp_path / "owned_evidence.py"
    path.write_text(text, encoding="utf-8")
    module = types.ModuleType("owned_evidence_fixture")
    module.__file__ = str(path)
    exec(compile(text, str(path), "exec", dont_inherit=True), vars(module))
    monkeypatch.setitem(sys.modules, module.__name__, module)
    monkeypatch.setattr(source_identity, "SELECTED_CODE_ENTRIES", ((module.__name__, member),))
    return path, module


def test_live_code_matches_source_without_invoking_business_or_mutating_module(tmp_path, monkeypatch):
    path, module = owned_module(tmp_path, monkeypatch,
        "calls = 0\ndef work():\n    global calls\n    calls += 1\n    return calls\n")
    before = dict(vars(module))
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "equivalent"
    assert result["entries"][0]["layers"][0]["sourceSha256"] == hashlib.sha256(path.read_bytes()).hexdigest()
    assert vars(module) == before and module.calls == 0
    assert "not globals" in result["scope"]


def test_recent_disk_edit_does_not_prove_old_loaded_code_is_current(tmp_path, monkeypatch):
    path, _ = owned_module(tmp_path, monkeypatch, "def work():\n    return 'loaded-old'\n")
    path.write_text("def work():\n    return 'disk-new'\n", encoding="utf-8")
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "different"
    assert result["entries"][0]["layers"][0]["status"] == "different"


@pytest.mark.parametrize("fault", ["missing", "syntax", "oversize", "frozen"])
def test_unavailable_source_is_unknown_not_match_or_status_query_failure(tmp_path, monkeypatch, fault):
    path, _ = owned_module(tmp_path, monkeypatch, "def work():\n    return 1\n")
    if fault == "missing":
        path.unlink()  # Only this fixture's file, never a shared source.
    elif fault == "syntax":
        path.write_text("def work(:", encoding="utf-8")
    elif fault == "oversize":
        path.write_bytes(b"#" + b"x" * (2 * 1024 * 1024))
    else:
        monkeypatch.setattr(sys, "frozen", True, raising=False)
    assert source_identity.selected_entry_code_evidence()["status"] == "unknown"


def test_no_import_or_property_getter_for_unloaded_and_nonfunction_entries(tmp_path, monkeypatch):
    name = "owned_never_imported_evidence"
    path = tmp_path / (name + ".py")
    path.write_text("raise AssertionError('diagnostic imported business')", encoding="utf-8")
    monkeypatch.syspath_prepend(str(tmp_path))
    monkeypatch.setattr(source_identity, "SELECTED_CODE_ENTRIES", ((name, "work"),))
    assert source_identity.selected_entry_code_evidence()["status"] == "unknown"
    assert name not in sys.modules
    _, module = owned_module(tmp_path, monkeypatch,
        "class Holder:\n    @property\n    def work(self):\n        raise AssertionError('getter invoked')\n", "Holder.work")
    assert source_identity.selected_entry_code_evidence()["status"] == "unknown"


def test_decorator_checks_wrapper_and_original_code_without_unwrapping_away_failures(tmp_path, monkeypatch):
    text = ("from functools import wraps\ndef decorate(fn):\n"
            "    @wraps(fn)\n    def wrapped(*a, **k):\n        return fn(*a, **k)\n"
            "    return wrapped\n@decorate\ndef work():\n    return 1\n")
    path, _ = owned_module(tmp_path, monkeypatch, text)
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "equivalent"
    assert len(result["entries"][0]["layers"]) == 2
    path.write_text(text.replace("return fn(*a, **k)", "return None"), encoding="utf-8")
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "different"
    assert [v["status"] for v in result["entries"][0]["layers"]] == ["different", "equivalent"]


def test_wrapper_cycle_is_unknown_and_remains_bounded(tmp_path, monkeypatch):
    _, module = owned_module(tmp_path, monkeypatch, "def work():\n    return 1\n")
    module.work.__wrapped__ = module.work
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "unknown"
    assert len(result["entries"][0]["layers"]) == 1
    assert result["entries"][0]["reason"] == "wrapper_chain_unverifiable"


def test_ambiguous_redefinitions_are_unknown(tmp_path, monkeypatch):
    owned_module(tmp_path, monkeypatch, "def work():\n    return 1\ndef work():\n    return 2\n")
    assert source_identity.selected_entry_code_evidence()["status"] == "unknown"


def test_equal_code_does_not_claim_globals_or_closure_values_are_verified(tmp_path, monkeypatch):
    _, module = owned_module(tmp_path, monkeypatch, "VALUE = 1\ndef work():\n    return VALUE\n")
    module.VALUE = 999
    result = source_identity.selected_entry_code_evidence()
    assert result["status"] == "equivalent"
    assert "not globals, closure values, dependencies or all modules" in result["scope"]


@pytest.mark.parametrize("fault", [RecursionError, MemoryError])
def test_source_compiler_resource_failure_remains_unknown(tmp_path, monkeypatch, fault):
    owned_module(tmp_path, monkeypatch, "def work():\n    return 1\n")
    def unavailable(*_args, **_kwargs):
        raise fault("owned compiler fault")
    monkeypatch.setattr(source_identity, "compile", unavailable, raising=False)
    assert source_identity.selected_entry_code_evidence()["status"] == "unknown"


@pytest.mark.parametrize("fresh", [False, True])
def test_status_public_projection_checks_selected_code_only_on_explicit_fresh_request(tmp_path, monkeypatch, fresh):
    import asyncio
    import copy
    from types import SimpleNamespace
    from upilot_mcp.domain import status_service
    from upilot_mcp.responses import ok
    from upilot_mcp.state_store import StateStore

    state = StateStore()
    state.configure_project(str(tmp_path))
    session = SimpleNamespace(session_id="owned-session", project_path=str(tmp_path),
        unity_version="fixture", platform="windows", process_id=0, last_heartbeat_at=1)
    service = status_service.StatusDomainService.__new__(status_service.StatusDomainService)
    service.server = SimpleNamespace(state=state,
        session_manager=SimpleNamespace(active=session, is_connected=lambda: True),
        is_ready=lambda: True, mcp_label="owned", host="127.0.0.1", port=0)
    service.dispatcher = SimpleNamespace(timeout_policy_snapshot=lambda: {})
    service._get_verified_unity_process_identity = lambda *_: (0, {"projectIdentityVerified": False})
    live = {"ready": False, "authoritative": False, "isStale": True}
    async def editor_state():
        return ok("owned-state", dict(live))
    service.editor_state = editor_state
    before = copy.deepcopy(state.commands)
    calls = []
    evidence = {"status": "unknown", "scope": source_identity.SELECTED_CODE_SCOPE, "entries": []}
    def check():
        calls.append(True)
        return copy.deepcopy(evidence)
    monkeypatch.setattr(status_service, "selected_entry_code_evidence", check)
    result = asyncio.run(service.mcp_status(force_fresh=fresh, include_capabilities=False))
    assert result.ok
    projection = result.data["runtimeIdentity"]["mcpServer"]["loadedSource"]
    assert calls == ([True] if fresh else [])
    assert projection == (evidence if fresh else {"status": "not_requested", "scope": source_identity.SELECTED_CODE_SCOPE})
    assert state.commands == before
    # unknown evidence is an observation, not permission to refresh/recover code.
    assert result.data["serverReady"] is True


def test_command_observation_entries_are_in_the_existing_limited_loading_scope():
    assert ("upilot_mcp.state_store", "StateStore.command_observation") in source_identity.SELECTED_CODE_ENTRIES
    assert ("upilot_mcp.domain.task_service", "TaskDomainService.operation_get") in source_identity.SELECTED_CODE_ENTRIES
    assert "not globals" in source_identity.SELECTED_CODE_SCOPE
    assert "all modules" in source_identity.SELECTED_CODE_SCOPE


def test_write_batch_round_entries_have_loaded_code_evidence():
    required = {
        ("upilot_mcp.state_store", "StateStore.freeze_write_batch_round"),
        ("upilot_mcp.state_store", "StateStore.record_write_batch_round_dispatch"),
        ("upilot_mcp.state_store", "StateStore._persist_round_evidence"),
        ("upilot_mcp.state_store", "StateStore.get_write_batch_round"),
        ("upilot_mcp.state_store", "StateStore.get_write_batch"),
        ("upilot_mcp.domain.resource_service", "ResourceDomainService._resume_pending_write_batches"),
        ("upilot_mcp.domain.compile_service", "CompileDomainService.compile"),
        ("upilot_mcp.domain.compile_service", "CompileDomainService._safe_compile_and_wait"),
    }
    assert required <= set(source_identity.SELECTED_CODE_ENTRIES)


def test_idle_connection_and_history_pruning_have_real_loaded_code_evidence():
    from upilot_mcp.server import WsOrchestratorServer
    from upilot_mcp.state_store import StateStore

    required = {
        (WsOrchestratorServer.__module__, WsOrchestratorServer._handle.__qualname__),
        (StateStore.__module__, StateStore._persist_execution_snapshot.__qualname__),
    }
    result = source_identity.selected_entry_code_evidence()
    entries = {(item["module"], item["entry"]): item for item in result["entries"]}
    assert required <= entries.keys()
    for key in required:
        assert entries[key]["status"] == "equivalent"
        assert entries[key]["layers"][0]["sourceSha256"]
