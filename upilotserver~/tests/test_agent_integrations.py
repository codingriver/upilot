"""Focused integration engine safety and shared C#/Python golden contract."""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import pytest

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    path = ROOT / "skills/upilot-unity-mcp/scripts" / (name + ".py")
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


engine = load("sync_integrations")
installer = load("install_upilot")
renderer = load("render_skill_pack")
CONTRACT = json.loads((ROOT / "Tests/Fixtures/agent-integrations-contract.json").read_text(encoding="utf-8"))


@pytest.fixture
def env(tmp_path):
    package, project = tmp_path / "package", tmp_path / "project"
    project.mkdir()
    for entry in CONTRACT["files"]:
        path = package / entry["path"]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(entry["content"].encode("utf-8"))
    return project, package


def sync(env, **kwargs):
    project, package = env
    return engine.synchronize(project, package, helpers=installer, http_port=8021, **kwargs)


def contents(root):
    return {p.relative_to(root).as_posix(): (p.read_bytes(), p.stat().st_mtime_ns) for p in root.rglob("*") if p.is_file()}


def test_read_only_first_install_and_golden_contract(env):
    project, _ = env
    before = contents(project)
    check = sync(env)
    assert check["ok"] and len(check["targets"]) == 5
    assert contents(project) == before and not (project / ".upilot").exists()
    report = sync(env, apply=True)
    assert report["ok"] and report["status"] == "synced"
    assert report["templateSha256"] == CONTRACT["templateSha256"]
    for relative in engine.SKILLS:
        skill = project / relative
        assert (skill / "SKILL.md").read_bytes() == CONTRACT["expectedSkill"].encode()
        assert (skill / "agents/openai.yaml").read_bytes() == CONTRACT["expectedOpenai"].encode()
        metadata = json.loads((skill / ".upilot-install.json").read_bytes())
        assert metadata["contentSha256"] == CONTRACT["contentSha256"]
        assert metadata["renderContext"]["mcpUrl"].endswith(":8021/mcp")
    before = contents(project)
    assert sync(env, apply=True)["status"] == "current"
    assert contents(project) == before


def test_old_custom_rule_preserves_outside_bytes_and_bom(env):
    project, _ = env
    original = b"\xef\xbb\xbf# business\r\n \t\r\n" + engine.START + b"\nrulesVersion: 1\ncustom\n" + engine.END + b"\r\n \r\nend  "
    (project / "AGENTS.md").write_bytes(original)
    report = sync(env, apply=True)
    assert report["ok"]
    target = report["targets"][0]
    assert target["status"] == "backed_up_and_synced"
    assert (Path(target["backupPath"]) / "AGENTS.md").read_bytes() == original
    result = (project / "AGENTS.md").read_bytes()
    assert result.split(engine.START)[0] == original.split(engine.START)[0]
    assert result.split(engine.END)[1] == original.split(engine.END)[1]
    assert not sync(env)["targets"][0]["needsUpdate"]


@pytest.mark.parametrize("bad", [engine.START, engine.END, engine.END + engine.START,
                               engine.START + engine.START + engine.END])
def test_bad_markers_fail_only_the_affected_target(env, bad):
    project, _ = env
    (project / "AGENTS.md").write_bytes(bad)
    report = sync(env, apply=True)
    assert not report["ok"] and report["status"] == "partial_failure"
    assert (project / "AGENTS.md").read_bytes() == bad
    assert all(t["status"] == "synced" for t in report["targets"][1:])


@pytest.mark.parametrize("failure", ["backup", "replace", "final"])
def test_target_failure_rolls_back_and_continues(env, monkeypatch, failure):
    project, _ = env
    original = b"custom business text"
    (project / "AGENTS.md").write_bytes(original)
    if failure == "backup":
        def backup(*args, **kwargs):
            raise OSError("injected backup failure")
        monkeypatch.setattr(installer, "_backup_skill_target", backup)
    elif failure == "replace":
        real = Path.replace
        def replace(path, target):
            if path.name == "candidate" and Path(target).name == "AGENTS.md":
                raise OSError("injected replace failure")
            return real(path, target)
        monkeypatch.setattr(Path, "replace", replace)
    else:
        real = engine._verify
        def verify(path, *args):
            if path == project / "AGENTS.md":
                raise OSError("injected final verification failure")
            return real(path, *args)
        monkeypatch.setattr(engine, "_verify", verify)
    report = sync(env, apply=True)
    assert not report["ok"] and report["targets"][0]["status"] == "failed"
    assert (project / "AGENTS.md").read_bytes() == original
    assert report["targets"][-1]["status"] == "synced"


def test_commit_guard_preserves_concurrent_edit(env, monkeypatch):
    project, _ = env
    target = project / "AGENTS.md"
    target.write_bytes(b"original")
    real = installer._backup_skill_target
    def backup(*args, **kwargs):
        result = real(*args, **kwargs)
        target.write_bytes(b"concurrent edit")
        return result
    monkeypatch.setattr(installer, "_backup_skill_target", backup)
    assert not sync(env, apply=True)["ok"]
    assert target.read_bytes() == b"concurrent edit"


def test_process_lock_is_busy_and_preview_does_not_write(env):
    project, package = env
    lock_path = project / ".upilot/agent-integrations.lock"
    lock_path.parent.mkdir()
    lock_path.touch()
    before = contents(project)
    with engine.project_lock(project, True):
        code = ("import sys,json;sys.path.insert(0,sys.argv[1]);import sync_integrations as s;"
                "print(json.dumps(s.synchronize(__import__('pathlib').Path(sys.argv[2]),"
                "__import__('pathlib').Path(sys.argv[3]))))")
        result = subprocess.run([sys.executable, "-B", "-c", code,
                                 str(ROOT / "skills/upilot-unity-mcp/scripts"), str(project), str(package)],
                                capture_output=True, text=True, timeout=20)
        assert result.returncode == 0, result.stderr
        assert json.loads(result.stdout)["status"] == "busy"
    assert contents(project) == before


@pytest.mark.parametrize("bad", ["missing", "unknown", "residual", "duplicate", "timestamp"])
def test_source_errors_never_write_any_targets(env, bad):
    project, package = env
    source = package / "skills/upilot-unity-mcp"
    path = source / "SKILL.md.template"
    if bad == "missing":
        path.unlink()
    elif bad == "duplicate":
        manifest_path = source / "template-manifest.json"
        manifest = json.loads(manifest_path.read_bytes())
        manifest["templates"]["skill"] = manifest["templates"]["openai"]
        manifest_path.write_bytes(json.dumps(manifest).encode())
    else:
        path.write_text({"unknown": "{{wrong}}", "residual": "{{bad-token}}", "timestamp": "{{generatedAt}}"}[bad])
    before = contents(project)
    assert not sync(env, apply=True)["ok"]
    assert contents(project) == before


def test_scope_and_context_changes(env):
    project, package = env
    assert sync(env, apply=True, scope="shared")["ok"]
    assert not (project / "CLAUDE.md").exists()
    assert not (project / ".agents").exists()
    assert sync(env, apply=True)["ok"]
    config = project / ".upilot/config.json"
    config.write_text('{"mcp":{"httpPort":8123}}')
    report = engine.synchronize(project, package, helpers=installer)
    assert report["renderContext"]["mcpUrl"].endswith(":8123/mcp")
    assert sum(t["needsUpdate"] for t in report["targets"]) == 5
    report = engine.synchronize(project, package, helpers=installer, http_port=8021)
    assert report["status"] == "current"


def test_cli_integrations_only_json_has_no_side_effects(env, capsys):
    project, package = env
    (project / "Packages").mkdir()
    (project / "Packages/manifest.json").write_text('{"dependencies":{}}')
    before = contents(project)
    args = ["--integrations-only", "--unity-project", str(project), "--upilot-dir", str(package), "--json"]
    assert installer.main(args + ["--dry-run"]) == 0
    assert len(json.loads(capsys.readouterr().out)["targets"]) == 5
    assert contents(project) == before
    assert installer.main(args) == 0
    assert json.loads(capsys.readouterr().out)["ok"]
    assert (project / "Packages/manifest.json").read_bytes() == before["Packages/manifest.json"][0]
    assert not (project / ".codex/config.toml").exists()


def test_generated_check_detects_newline_only_drift(env):
    _, package = env
    source = package / "skills/upilot-unity-mcp"
    assert renderer.main(["--root", str(source), "--write"]) == 0
    path = source / "SKILL.md"
    path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n"))
    assert renderer.main(["--root", str(source), "--check"]) == 1


def test_online_requires_bridge_and_never_uses_server_templates(env):
    from upilot_mcp.domain.task_service import TaskDomainService
    from upilot_mcp.responses import ok, fail
    service = TaskDomainService()
    service.server = SimpleNamespace(session_manager=SimpleNamespace(active=SimpleNamespace(
        session_id="fixture-session", identity_verified=True)))
    async def unsupported(*args):
        return fail("req", "UNKNOWN_COMMAND", "Old bridge")
    service.dispatcher = SimpleNamespace(call=unsupported)
    assert not asyncio.run(service.agent_integrations_sync(True)).ok
    assert contents(env[0]) == {}
    async def bridge(*args):
        return ok("req", {"schemaVersion": 1, "ok": True, "targets": [], "agentRulesVersion": 789,
                          "templateSource": "actual-UPM-not-server"})
    service.dispatcher = SimpleNamespace(call=bridge)
    result = asyncio.run(service.agent_integrations_check())
    assert result.data["agentRulesVersion"] == 789
    assert result.data["sourceIdentity"]["templateSource"] == "actual-UPM-not-server"


def test_junction_target_is_rejected_without_modifying_destination(env, tmp_path):
    project, _ = env
    external = tmp_path / "external"
    external.mkdir()
    (external / "business").write_bytes(b"do not touch")
    junction = project / ".agents"
    if os.name == "nt":
        # Native Windows junction, removed non-recursively in finally.
        import _winapi
        _winapi.CreateJunction(str(external), str(junction))
    else:
        junction.symlink_to(external, target_is_directory=True)
    try:
        before = contents(external)
        report = sync(env, apply=True)
        assert not report["ok"]
        assert report["targets"][3]["status"] == "failed"
        assert contents(external) == before
    finally:
        if os.name == "nt":
            os.rmdir(junction)
        else:
            junction.unlink()


def test_new_mcp_preview_and_write_authorization(monkeypatch):
    from upilot_mcp.mcp_tools import task_tools
    from upilot_mcp.responses import ok
    calls = []
    async def sync_call(apply=False):
        calls.append(apply)
        return ok("req", {"ok": True})
    monkeypatch.setattr(task_tools, "_get_facade", lambda: SimpleNamespace(agent_integrations_sync=sync_call))
    monkeypatch.setattr(task_tools, "_reject_write_if_unapproved", lambda _: {"rejected": True})
    assert asyncio.run(task_tools.unity_agent_integrations_sync(True)) == {"rejected": True}
    assert calls == []
    asyncio.run(task_tools.unity_agent_integrations_sync(False))
    assert calls == [False]


def test_proxy_preview_uses_conditional_authorization(monkeypatch):
    from upilot_mcp import tool_registry
    from upilot_mcp.config import CONFIG
    from upilot_mcp.responses import ok
    from upilot_mcp.mcp_tools import task_tools
    calls = []
    async def method(apply=False):
        calls.append(apply)
        return ok("req", {})
    monkeypatch.setattr(tool_registry, "refresh_config_if_changed", lambda: None)
    monkeypatch.setattr(CONFIG, "write_access_approved", False)
    facade = SimpleNamespace(agent_integrations_sync=method)
    assert asyncio.run(tool_registry.dispatch_public_tool(facade, "unity_agent_integrations_sync", {})).ok
    assert not asyncio.run(tool_registry.dispatch_public_tool(facade, "unity_agent_integrations_sync", {"apply": True})).ok
    assert calls == [False]


def test_distill_target_routing_and_maintenance_constraints():
    source = ROOT / "skills/upilot-unity-mcp"
    template = (source / "AGENTS.md.template").read_text(encoding="utf-8")
    workflow = (source / "references/workflows.md").read_text(encoding="utf-8")
    safety = (source / "references/safety.md").read_text(encoding="utf-8")
    assert "By default, verify `paths.unityProjectAbsolute` matches `{{projectPath}}`" in template
    assert "current task explicitly selects it" in template
    assert "existing project business rules explicitly authorize an alternate project" in template
    assert "A matching basename is never authorization" in template
    assert "Never automatically switch projects" in template
    assert "UPilotTest2022" not in template  # Repository-specific allowlist stays project-owned.
    assert "Do not broaden the existing exact-path acceptance allowlist" in workflow
    assert "an incomplete inventory cannot establish an idle maintenance window" in workflow
    assert "independently approved `unity_service_restart` exception" in workflow
    assert "This grant explicitly permits interruption" in safety
