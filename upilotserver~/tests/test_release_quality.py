import importlib.util
import json
from pathlib import Path

import pytest

from upilot_mcp.source_identity import acceptance_import_inputs, diff_acceptance_import_inputs, source_identity


def gate():
    path = Path(__file__).resolve().parents[1] / "scripts" / "check_release_quality.py"
    spec = importlib.util.spec_from_file_location("check_release_quality", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_source_identity_detects_uncommitted_code_but_ignores_generated_caches(tmp_path):
    folder = tmp_path / "Editor"
    folder.mkdir()
    code = folder / "Probe.cs"
    code.write_bytes(b"class Probe {}\r\n")
    first = source_identity(tmp_path)
    code.write_bytes(b"class Probe {}\n")
    assert source_identity(tmp_path) == first
    cache = folder / "__pycache__"
    cache.mkdir()
    (cache / "ignored.pyc").write_bytes(b"cache")
    assert source_identity(tmp_path) == first
    code.write_bytes(b"class Probe { int changed; }\n")
    assert source_identity(tmp_path)["sourceSha256"] != first["sourceSha256"]


def test_unity_gate_rejects_stale_and_missing_evidence():
    verify = gate().verify_unity_summary
    source = {"sourceSha256": "checked-source"}
    report = {
        "acceptancePassed": True, "cleanupVerified": True, "testIdentityVerified": True,
        "sourceUnchanged": True, "sourceIdentity": source, "sourceIdentityAfter": source,
        "runGuid": "run-1", "endedAt": 1,
    }
    verify(report, source)
    with pytest.raises(ValueError):
        verify(report, {"sourceSha256": "new-source"})
    for key in ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged"):
        with pytest.raises(ValueError):
            verify({**report, key: False}, source)


def test_source_identity_includes_non_editor_test_fixtures(tmp_path):
    folder = tmp_path / "Tests" / "Fixtures"
    folder.mkdir(parents=True)
    probe = folder / "Probe.cs"
    probe.write_text("class Probe {}", encoding="utf-8")
    before = source_identity(tmp_path)
    probe.write_text("class Probe { int value; }", encoding="utf-8")
    assert source_identity(tmp_path)["sourceSha256"] != before["sourceSha256"]


def test_acceptance_import_inputs_track_meta_without_calling_equal_hashes_ready(tmp_path):
    (tmp_path / "Editor").mkdir()
    (tmp_path / "Editor" / "Probe.cs").write_text("class Probe {}", encoding="utf-8")
    project = tmp_path / "Tests~" / "UPilotTest"
    asset = project / "Assets" / "Fixture.cs.meta"
    asset.parent.mkdir(parents=True)
    asset.write_text("MonoImporter:\r\n  value: one\r\n", encoding="utf-8", newline="")

    before = acceptance_import_inputs(tmp_path, project)
    asset.write_text("MonoImporter:\n  value: two\n", encoding="utf-8", newline="")
    after = acceptance_import_inputs(tmp_path, project)
    diff = diff_acceptance_import_inputs(before, after)

    assert before["inputCount"] == after["inputCount"]
    assert diff == {
        "changed": [{
            "path": "project:Assets/Fixture.cs.meta",
            "beforeSha256": before["inputs"]["project:Assets/Fixture.cs.meta"],
            "afterSha256": after["inputs"]["project:Assets/Fixture.cs.meta"],
        }],
        "changeCount": 1,
        "changesTruncated": False,
    }


def test_tool_inventory_uses_registry_and_real_mcp_schemas():
    inventory = gate().tool_inventory()
    tools = {tool["name"]: tool for tool in inventory["tools"]}
    assert inventory["registeredCount"] == len(tools)
    assert inventory["exposedCount"] == sum(tool["exposedInCurrentConfiguration"] for tool in tools.values())
    assert "confirmToken" in tools["unity_prefab_patch"]["inputSchema"]["properties"]
    assert "maxNodes" in tools["unity_scene_summary"]["inputSchema"]["properties"]
    assert {"testNames", "fixtures"} <= set(tools["unity_test_run"]["inputSchema"]["properties"])
    assert "detailLevel" in tools["unity_task_status"]["inputSchema"]["properties"]
    for name in ("unity_prefab_patch", "unity_scene_summary", "unity_test_run", "unity_task_status"):
        assert tools[name]["proxyHandlerAvailable"]
    assert inventory["proxyHandlerGaps"] == sorted(
        name for name, tool in tools.items() if not tool["proxyHandlerAvailable"]
    )
    assert inventory["proxyHandlerGaps"] == []


def test_tool_inventory_rejects_missing_proxy_handler(monkeypatch):
    from dataclasses import replace
    from upilot_mcp.tool_registry import REGISTRY

    descriptor = REGISTRY.resolve("unity_compile_errors_get")
    monkeypatch.setitem(REGISTRY._items, descriptor.name, replace(descriptor, facade_method="missing_handler"))
    with pytest.raises(ValueError, match="missing proxy handler"):
        gate().tool_inventory()


def _write_wp12_repository(root: Path) -> Path:
    (root / "Documentation~").mkdir(parents=True)
    (root / "upilotserver~" / "src" / "upilot_mcp" / "domain").mkdir(parents=True)
    (root / "upilotserver~" / "src" / "upilot_mcp").mkdir(exist_ok=True)
    (root / "skills" / "upilot-unity-mcp").mkdir(parents=True)
    (root / "TODO_UPilot.mcd").write_text("# UPilot Improvement Backlog\n\n## UP-001 Demo\n", encoding="utf-8")
    (root / "Documentation~" / "P2-Development-Plan-20260915.md").write_text(
        "当前开发与验收状态仍以 [根 TODO](../TODO_UPilot.mcd) 为准。\n"
        "<a id=\"P2-WP-01\"></a>\n",
        encoding="utf-8",
    )
    (root / "Documentation~" / "TODO_UPilot_Integrated.md").write_text("# Archive\n", encoding="utf-8")
    (root / "Documentation~" / "ToolStatus.md").write_text("Current generated Registry v7.\n", encoding="utf-8")
    (root / "upilotserver~" / "src" / "upilot_mcp" / "domain" / "task_service.py").write_text("# fixture\n", encoding="utf-8")
    (root / "skills" / "upilot-unity-mcp" / "template-manifest.json").write_text(
        json.dumps({"agentRulesVersion": 30, "skillPackVersion": 29}), encoding="utf-8"
    )
    (root / "upilotserver~" / "src" / "upilot_mcp" / "tool_registry.py").write_text("REGISTRY_VERSION = 7\n", encoding="utf-8")
    return root


def _inventory():
    return {"proxyHandlerGaps": [], "tools": [{"name": "unity_probe"}, {"name": "csharp_eval"}]}


def _check(checks, check_id):
    return next(item for item in checks["checks"] if item["checkId"] == check_id)


def test_documentation_checks_keep_historical_source_identity_separate_from_head(tmp_path):
    root = _write_wp12_repository(tmp_path)
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "sourceIdentity": {"files": [{"path": "upilotserver~/src/upilot_mcp/tool_registry.py", "bytes": 1, "sha256": "0" * 64}]},
    }), encoding="utf-8")

    checks = gate().documentation_checks(root, inventory_factory=_inventory)

    assert checks["errorCheckIds"] == []
    assert _check(checks, "docs.evidence")["status"] == "passed"
    assert _check(checks, "docs.install")["status"] == "unknown"


def test_documentation_checks_registry_snapshot_is_stable_across_two_repositories(tmp_path):
    first = gate().documentation_checks(_write_wp12_repository(tmp_path / "first"), inventory_factory=_inventory)
    second = gate().documentation_checks(_write_wp12_repository(tmp_path / "second"), inventory_factory=_inventory)

    assert _check(first, "docs.registry")["actual"] == _check(second, "docs.registry")["actual"]
    assert _check(first, "docs.registry")["generatedDiffCount"] == 0


def test_documentation_checks_reports_precise_registry_and_local_link_errors(tmp_path):
    root = _write_wp12_repository(tmp_path)
    plan = root / "Documentation~" / "P2-Development-Plan-20260915.md"
    plan.write_text(plan.read_text(encoding="utf-8") + "[bad](#missing)\n", encoding="utf-8")

    checks = gate().documentation_checks(
        root,
        inventory_factory=lambda: {"proxyHandlerGaps": [], "tools": [{"name": "legacy tool"}]},
    )

    registry = _check(checks, "docs.registry")
    todo = _check(checks, "docs.todo-id")
    assert registry["status"] == "failed"
    assert registry["actual"]["invalid"] == ["legacy tool"]
    assert todo["status"] == "failed"
    assert todo["actual"]["linkErrors"] == ["MISSING_ANCHOR:P2-Development-Plan-20260915.md#missing"]


def test_documentation_checks_reports_documented_registry_version_drift(tmp_path):
    root = _write_wp12_repository(tmp_path)
    status = root / "Documentation~" / "ToolStatus.md"
    status.write_text("Current generated Registry v6.\n", encoding="utf-8")

    registry = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.registry")

    assert registry["status"] == "failed"
    assert registry["sourcePath"] == "Documentation~/ToolStatus.md"
    assert registry["expected"]["registryVersion"] == "7"
    assert registry["actual"]["documentedRegistryVersion"] == "6"


def test_documentation_checks_reject_current_artifact_hash_mismatch(tmp_path):
    root = _write_wp12_repository(tmp_path)
    artifact = root / "Documentation~" / "artifact.txt"
    artifact.write_text("actual", encoding="utf-8")
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "artifact": {"path": "Documentation~/artifact.txt", "bytes": 6, "sha256": "0" * 64},
    }), encoding="utf-8")

    checks = gate().documentation_checks(root, inventory_factory=_inventory)

    evidence = _check(checks, "docs.evidence")
    assert evidence["status"] == "failed"
    assert evidence["severity"] == "error"
    assert "HASH_OR_SIZE_MISMATCH" in evidence["actual"][0]


def test_documentation_checks_reports_installed_manifest_without_claiming_client_injection(tmp_path):
    root = _write_wp12_repository(tmp_path)
    installed = tmp_path / "installed.json"
    installed.write_text(json.dumps({
        "packageId": "io.github.codingriver.upilot",
        "rulesVersion": 30,
        "sourceSha256": "recorded-source",
        "mapping": "supported",
        "clientInjectionState": "visible",
    }), encoding="utf-8")

    checks = gate().documentation_checks(root, installed_manifests=[installed], inventory_factory=_inventory)

    install = _check(checks, "docs.install")
    assert install["status"] == "passed"
    assert install["actual"][0]["clientInjectionState"] == "unknown"


def test_documentation_checks_validates_read_only_installed_skill_metadata_without_inferring_source_or_injection(tmp_path):
    root = _write_wp12_repository(tmp_path / "repo")
    installed_root = tmp_path / "client" / "upilot-unity-mcp"
    installed_root.mkdir(parents=True)
    (installed_root / "SKILL.md").write_text("installed skill\n", encoding="utf-8")
    recorded_hash = gate()._installed_skill_content_hash(installed_root)
    manifest = installed_root / ".upilot-install.json"
    manifest.write_text(json.dumps({"templateVersion": 29, "contentSha256": recorded_hash}), encoding="utf-8")

    checks = gate().documentation_checks(root, installed_manifests=[manifest], inventory_factory=_inventory)

    install = _check(checks, "docs.install")
    assert install["status"] == "unknown"
    item = install["actual"][0]
    assert item["actualContentSha256"] == recorded_hash
    assert item["sourceProvenance"] == "unknown"
    assert item["clientInjectionState"] == "unknown"
    assert manifest.read_text(encoding="utf-8") == json.dumps({"templateVersion": 29, "contentSha256": recorded_hash})

    (installed_root / "SKILL.md").write_text("modified outside installer\n", encoding="utf-8")
    changed = _check(gate().documentation_checks(root, installed_manifests=[manifest], inventory_factory=_inventory), "docs.install")
    assert changed["status"] == "failed"
    assert changed["actual"][0]["reason"] == "INSTALLED_SKILL_HASH_MISMATCH"


def test_documentation_checks_marks_installed_skill_mutation_as_input_changed(tmp_path):
    root = _write_wp12_repository(tmp_path / "repo")
    installed_root = tmp_path / "client" / "upilot-unity-mcp"
    installed_root.mkdir(parents=True)
    skill = installed_root / "SKILL.md"
    skill.write_text("before\n", encoding="utf-8")
    manifest = installed_root / ".upilot-install.json"
    manifest.write_text(json.dumps({
        "templateVersion": 29,
        "contentSha256": gate()._installed_skill_content_hash(installed_root),
    }), encoding="utf-8")

    def mutate_during_check():
        skill.write_text("after\n", encoding="utf-8")
        return _inventory()

    checks = gate().documentation_checks(
        root, installed_manifests=[manifest], inventory_factory=mutate_during_check,
    )

    assert checks["inputUnchanged"] is False
    assert all(item["reason"] == "INPUT_CHANGED" for item in checks["checks"])


def test_documentation_checks_preserves_external_old_skill_names_as_mapped_or_unsupported(tmp_path):
    root = _write_wp12_repository(tmp_path)
    installed = tmp_path / "installed.json"
    installed.write_text(json.dumps({
        "packageId": "io.github.codingriver.upilot",
        "rulesVersion": 30,
        "externalSkill": {
            "source": "unity-perception@1.2.3",
            "tools": ["scene_summarize", "scene_analyze"],
            "mappings": {"scene_summarize": "unity_probe"},
        },
    }), encoding="utf-8")

    install = _check(gate().documentation_checks(root, installed_manifests=[installed], inventory_factory=_inventory), "docs.install")

    assert install["status"] == "passed"
    assert install["actual"][0]["externalSkill"]["toolMappings"] == [
        {"externalTool": "scene_summarize", "upilotTool": "unity_probe", "mapping": "mapped"},
        {"externalTool": "scene_analyze", "upilotTool": None, "mapping": "unsupported"},
    ]
    assert install["actual"][0]["clientInjectionState"] == "unknown"


def test_documentation_checks_external_skill_without_source_is_unknown_not_missing_tool(tmp_path):
    root = _write_wp12_repository(tmp_path)
    installed = tmp_path / "installed.json"
    installed.write_text(json.dumps({
        "packageId": "io.github.codingriver.upilot",
        "rulesVersion": 30,
        "externalSkill": {"tools": ["scene_analyze"]},
    }), encoding="utf-8")

    install = _check(gate().documentation_checks(root, installed_manifests=[installed], inventory_factory=_inventory), "docs.install")

    assert install["status"] == "unknown"
    assert install["severity"] == "warning"
    assert install["actual"][0]["reason"] == "MISSING_EXTERNAL_SKILL_SOURCE"
    assert install["actual"][0]["externalSkill"]["toolMappings"][0]["mapping"] == "unsupported"


def test_documentation_checks_empty_external_skill_is_unknown_not_an_absent_declaration(tmp_path):
    root = _write_wp12_repository(tmp_path)
    installed = tmp_path / "installed.json"
    installed.write_text(json.dumps({
        "packageId": "io.github.codingriver.upilot",
        "rulesVersion": 30,
        "externalSkill": {},
    }), encoding="utf-8")

    install = _check(
        gate().documentation_checks(root, installed_manifests=[installed], inventory_factory=_inventory),
        "docs.install",
    )

    assert install["status"] == "unknown"
    assert install["actual"][0]["reason"] == "MISSING_EXTERNAL_SKILL_SOURCE"
    assert install["actual"][0]["externalSkill"] == {
        "source": None,
        "version": None,
        "toolMappings": [],
    }


def test_documentation_checks_detect_input_change(tmp_path):
    root = _write_wp12_repository(tmp_path)
    plan = root / "Documentation~" / "P2-Development-Plan-20260915.md"

    def mutate_during_check():
        plan.write_text(plan.read_text(encoding="utf-8") + "changed\n", encoding="utf-8")
        return _inventory()

    checks = gate().documentation_checks(root, inventory_factory=mutate_during_check)

    evidence = _check(checks, "docs.evidence")
    assert checks["inputUnchanged"] is False
    assert evidence["reason"] == "INPUT_CHANGED"
    assert evidence["status"] == "failed"


def test_documentation_checks_detects_concurrent_evidence_manifest_change(tmp_path):
    root = _write_wp12_repository(tmp_path)
    evidence_path = root / "Documentation~" / "FixtureEvidence.json"
    evidence_path.write_text("{}", encoding="utf-8")

    def mutate_during_check():
        evidence_path.write_text('{"changed": true}', encoding="utf-8")
        return _inventory()

    checks = gate().documentation_checks(root, inventory_factory=mutate_during_check)

    evidence = _check(checks, "docs.evidence")
    assert checks["inputUnchanged"] is False
    assert evidence["reason"] == "INPUT_CHANGED"


def test_documentation_checks_never_reports_an_artifact_check_as_stable_when_artifact_changes(tmp_path):
    root = _write_wp12_repository(tmp_path)
    artifact = root / "Documentation~" / "artifact.txt"
    artifact.write_text("before", encoding="utf-8")
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "artifact": {
            "path": "Documentation~/artifact.txt",
            "bytes": len(b"before"),
            "sha256": gate()._sha256(b"before"),
        },
    }), encoding="utf-8")

    def mutate_during_check():
        artifact.write_text("after", encoding="utf-8")
        return _inventory()

    checks = gate().documentation_checks(root, inventory_factory=mutate_during_check)

    assert checks["inputUnchanged"] is False
    assert _check(checks, "docs.evidence")["reason"] == "INPUT_CHANGED"
    assert all(item["status"] != "passed" for item in checks["checks"])


def test_documentation_checks_reports_missing_and_malformed_artifacts_without_crashing(tmp_path):
    root = _write_wp12_repository(tmp_path)
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "missing": {"path": "Documentation~/absent.txt", "sha256": "0" * 64},
        "malformed": {"path": "Documentation~/also-absent.txt", "sha256": 4},
    }), encoding="utf-8")

    evidence = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.evidence")

    assert evidence["status"] == "failed"
    assert evidence["severity"] == "error"
    assert any(item.endswith("INVALID_ARTIFACT_CLAIM") for item in evidence["actual"])


def test_docs_only_mode_skips_contract_commands_and_docs_strict_escalates_unknown(tmp_path, monkeypatch):
    module = gate()
    root = _write_wp12_repository(tmp_path / "repo")
    (root / "Documentation~" / "P2-Development-Plan-20260915.md").unlink()
    monkeypatch.setattr(module, "REPO", root)
    monkeypatch.setattr(module, "tool_inventory", lambda: {"proxyHandlerGaps": [], "tools": [{"name": "legacy tool"}]})

    output = tmp_path / "report"
    assert module.main(["--docs-only", "--output", str(output)]) == 0
    report = json.loads((output / "quality.json").read_text(encoding="utf-8"))
    assert report["testScope"] == []
    assert report["steps"] == []
    assert report["documentation"]["passed"] is True
    assert {item["checkId"] for item in report["documentation"]["checks"]} == {
        "docs.archive", "docs.evidence", "docs.install",
    }
    assert module.main([
        "--docs-only", "--enable-docs-registry", "--enable-docs-todo-id",
        "--output", str(tmp_path / "optional"),
    ]) == 1
    assert module.main(["--docs-only", "--docs-strict", "--output", str(tmp_path / "strict")]) == 1


def test_docs_arguments_are_mutually_exclusive_and_atomic_report_failure_preserves_old_file(tmp_path, monkeypatch):
    module = gate()
    with pytest.raises(SystemExit):
        module.main(["--docs-only", "--skip-docs"])

    report = tmp_path / "quality.json"
    report.write_bytes(b"old-report")
    monkeypatch.setattr(module.os, "replace", lambda *_: (_ for _ in ()).throw(OSError("replace denied")))
    with pytest.raises(OSError, match="replace denied"):
        module._atomic_write_bytes(report, b"new-report")
    assert report.read_bytes() == b"old-report"
    assert not list(tmp_path.glob(".quality.json.*.tmp"))


def test_archive_only_rejects_same_id_same_provenance_and_evidence(tmp_path):
    root = _write_wp12_repository(tmp_path)
    archive = root / "Documentation~" / "TODO_UPilot_Integrated.md"
    archive.write_text(
        "## UP-001 First historical record\n"
        "- sourceIdentity: build-a\n- evidenceSha256: " + "1" * 64 + "\n\n"
        "## UP-001 Second citation\n"
        "- sourceIdentity: build-b\n- evidenceSha256: " + "1" * 64 + "\n",
        encoding="utf-8",
    )
    assert _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.archive")["status"] == "passed"

    archive.write_text(archive.read_text(encoding="utf-8") + (
        "\n## UP-001 Duplicate declaration\n"
        "- sourceIdentity: build-a\n- evidenceSha256: " + "1" * 64 + "\n"
    ), encoding="utf-8")
    conflict = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.archive")
    assert conflict["status"] == "failed"
    assert conflict["actual"] == [{"stableId": "UP-001", "source": "build-a", "evidenceSha256": "1" * 64}]


def test_root_authoritative_status_table_rejects_duplicate_ids_but_keeps_different_ids(tmp_path):
    root = _write_wp12_repository(tmp_path)
    todo = root / "TODO_UPilot.mcd"
    todo.write_text(
        "# UPilot Improvement Backlog\n\n"
        "| ID | Current status | Title |\n|---|---|---|\n"
        "| UP-010 | Open | Same title |\n| UP-011 | Open | Same title |\n",
        encoding="utf-8",
    )
    assert _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.todo-id")["status"] == "passed"

    todo.write_text(todo.read_text(encoding="utf-8") + "| UP-010 | Closed | Revised title |\n", encoding="utf-8")
    conflict = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.todo-id")
    assert conflict["status"] == "failed"
    assert conflict["actual"]["duplicateStableIds"] == ["UP-010"]


def test_documentation_checks_marks_missing_historical_source_provenance_unknown(tmp_path):
    root = _write_wp12_repository(tmp_path)
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "artifact": {"path": "Documentation~/missing.txt", "sha256": "0" * 64},
    }), encoding="utf-8")

    evidence = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.evidence")

    assert evidence["status"] == "unknown"
    assert evidence["severity"] == "warning"
    assert any(item.endswith("MISSING_SOURCE_IDENTITY") for item in evidence["actual"]["unavailableClaims"])


def test_documentation_checks_accepts_historical_per_file_source_hash_provenance(tmp_path):
    root = _write_wp12_repository(tmp_path)
    (root / "Documentation~" / "FixtureEvidence.json").write_text(json.dumps({
        "sourceSha256": {"Editor/Probe.cs": "a" * 64},
    }), encoding="utf-8")

    evidence = _check(gate().documentation_checks(root, inventory_factory=_inventory), "docs.evidence")

    assert evidence["status"] == "passed"


def test_installed_manifest_input_order_and_duplicates_do_not_change_documentation_report(tmp_path):
    root = _write_wp12_repository(tmp_path / "repo")
    first = tmp_path / "first.json"
    second = tmp_path / "second.json"
    for path, external_tool in ((first, "legacy_first"), (second, "legacy_second")):
        path.write_text(json.dumps({
            "packageId": "io.github.codingriver.upilot",
            "rulesVersion": 30,
            "externalSkill": {"source": "external@1", "tools": [external_tool]},
        }), encoding="utf-8")

    forward = gate().documentation_checks(root, installed_manifests=[first, second, first], inventory_factory=_inventory)
    reverse = gate().documentation_checks(root, installed_manifests=[second, first], inventory_factory=_inventory)

    assert _check(forward, "docs.install")["actual"] == _check(reverse, "docs.install")["actual"]
    assert forward["inputSnapshot"] == reverse["inputSnapshot"]
