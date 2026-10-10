import asyncio
from pathlib import Path

import pytest

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY
from upilot_mcp.mcp_tools import test_tools  # noqa: F401


@pytest.fixture(autouse=True)
def _acceptance_write_grant(monkeypatch):
    monkeypatch.setattr(CONFIG, "write_access_approved", True)


class Dispatcher:
    def __init__(self):
        self.calls = []

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, payload))
        if name == "scene.prepareForAutomation":
            return ok(request_id, {"prepared": True, "action": "none", "items": []})
        if name == "test.list":
            return ok(request_id, {
                "scenes": [], "runGuid": "one-run", "tests": [],
                "selectionDomain": "domain-one", "selectionSnapshotId": "snapshot-one",
            })
        return ok(request_id, {"scenes": [], "runGuid": "one-run", "tests": []})


def service():
    result = TestDomainService()
    result.dispatcher = Dispatcher()
    return result


@pytest.mark.parametrize("method", ["test_list", "test_run"])
def test_selector_arrays_reach_unity_without_reinterpreting_exact_names(method):
    target = service()
    result = asyncio.run(getattr(target, method)(
        test_names=['Demo.Fixture.Method("a.b")'], fixtures=["Demo.AnotherFixture"],
    ))
    assert result.ok
    name, payload = target.dispatcher.calls[-1]
    assert payload["testNames"] == ['Demo.Fixture.Method("a.b")']
    assert payload["fixtures"] == ["Demo.AnotherFixture"]
    assert payload["requireAllSelectorsMatch"] is True


@pytest.mark.parametrize("args", [
    {"test_names": []},
    {"fixtures": []},
    {"fixtures": [""]},
    {"fixtures": [None]},
    {"test_names": ["A"], "test_filter": "regex:.*"},
    {"test_names": ["A"] * 257},
    {"test_names": "A"},
    {"assemblies": []},
    {"categories": []},
    {"assemblies": ["A"], "categories": []},
    {"categories": ["Slow"], "match_mode": "invalid"},
    {"categories": ["Slow"], "require_all_selectors_match": "yes"},
    {"assemblies": ["A"], "test_filter": "Legacy"},
])
@pytest.mark.parametrize("method", ["test_list", "test_run"])
def test_bad_selectors_fail_before_any_editor_command(method, args):
    target = service()
    result = asyncio.run(getattr(target, method)(**args))
    assert not result.ok and result.error.code == "TEST_SELECTORS_INVALID"
    assert target.dispatcher.calls == []


def test_selectors_extend_existing_registered_tools():
    for name in ("unity_test_list", "unity_test_run", "unity_upilot_acceptance_run"):
        descriptor = REGISTRY.resolve(name)
        assert descriptor is not None


@pytest.mark.parametrize("method", ["test_list", "test_run"])
def test_assembly_category_intersection_is_forwarded(method):
    target = service()
    result = asyncio.run(getattr(target, method)(
        assemblies=["Example.Tests"], categories=["Smoke", "Fast"], match_mode="intersection"))
    assert result.ok
    payload = target.dispatcher.calls[-1][1]
    assert payload["assemblies"] == ["Example.Tests"]
    assert payload["categories"] == ["Smoke", "Fast"]
    assert payload["matchMode"] == "intersection"


@pytest.mark.parametrize("method", ["test_list", "test_run"])
def test_null_filter_normalizes_and_compatibility_flag_reaches_unity(method):
    target = service()
    result = asyncio.run(getattr(target, method)(
        test_filter=None, test_names=["Exact.Name"], require_all_selectors_match=False,
    ))
    assert result.ok
    name, payload = target.dispatcher.calls[-1]
    if name == "test.list":
        assert payload["testFilter"] == ""
    else:
        assert "testFilter" not in payload
    assert payload["requireAllSelectorsMatch"] is False


def test_default_strict_run_rejects_unmatched_selection_before_scene_preflight():
    class StrictDispatcher(Dispatcher):
        async def call(self, request_id, name, payload, **kwargs):
            self.calls.append((name, payload))
            if name == "test.list":
                return ok(request_id, {
                    "selectionValid": False,
                    "unmatchedSelectors": [{"kind": "fixture", "selector": "Missing", "candidates": []}],
                })
            pytest.fail("scene state and runner must not be queried after an unmatched strict selector")

    target = service()
    target.dispatcher = StrictDispatcher()
    result = asyncio.run(target.test_run(fixtures=["Missing"]))
    assert not result.ok
    assert result.error.code == "TEST_SELECTOR_UNMATCHED"
    assert result.error.detail["runnerStartAttempted"] is False
    assert target.dispatcher.calls == [("test.list", {
        "testMode": "EditMode", "testFilter": "", "fixtures": ["Missing"],
        "matchMode": "union", "requireAllSelectorsMatch": True,
    })]


def test_compatibility_run_does_not_preflight_discovery_twice():
    target = service()
    result = asyncio.run(target.test_run(
        fixtures=["Missing"], require_all_selectors_match=False,
    ))
    assert result.ok
    assert [name for name, _ in target.dispatcher.calls] == ["scene.prepareForAutomation", "test.run"]


@pytest.mark.parametrize("test_filter", [None, ""])
def test_omitted_or_null_filter_has_the_same_explicit_bridge_value(test_filter):
    target = service()
    result = asyncio.run(target.test_list(test_filter=test_filter))
    assert result.ok
    assert target.dispatcher.calls == [(
        "test.list",
        {"testMode": "EditMode", "testFilter": "", "requireAllSelectorsMatch": True},
    )]


def test_duplicate_selectors_are_preserved_for_bridge_diagnostics_without_reinterpreting_them():
    target = service()
    result = asyncio.run(target.test_list(
        fixtures=["Demo.Fixture", "Demo.Fixture"],
        assemblies=["Example.Tests.dll", "Example.Tests.dll"],
    ))
    assert result.ok
    _, payload = target.dispatcher.calls[-1]
    # The authoritative discovery layer deduplicates using its per-kind
    # normalization and reports duplicateSelectors.  Python must not erase the
    # input before that diagnostic can be produced.
    assert payload["fixtures"] == ["Demo.Fixture", "Demo.Fixture"]
    assert payload["assemblies"] == ["Example.Tests.dll", "Example.Tests.dll"]


def test_strict_intersection_without_common_leaf_finishes_no_tests_without_preflight_or_runner():
    class EmptyIntersectionDispatcher(Dispatcher):
        async def call(self, request_id, name, payload, **kwargs):
            self.calls.append((name, payload))
            if name == "test.list":
                return ok(request_id, {
                    "selectionValid": True,
                    "matchedCount": 0,
                    "discoveryStatus": "filter_no_match",
                    "tests": [],
                })
            pytest.fail("an empty, valid intersection must not prepare scenes or start the Runner")

    target = service()
    target.dispatcher = EmptyIntersectionDispatcher()
    result = asyncio.run(target.test_run(
        fixtures=["FixtureA"], categories=["CategoryB"], match_mode="intersection",
    ))
    assert result.ok
    assert result.data["status"] == "no_tests"
    assert result.data["terminal"] is True
    assert result.data["runnerStartAttempted"] is False
    assert [name for name, _ in target.dispatcher.calls] == ["test.list"]


def test_strict_discovery_failure_never_prepares_or_starts_runner():
    class DiscoveryFailureDispatcher(Dispatcher):
        async def call(self, request_id, name, payload, **kwargs):
            self.calls.append((name, payload))
            assert name == "test.list"
            return type("Response", (), {
                "ok": False,
                "data": None,
                "error": type("Error", (), {
                    "code": "BRIDGE_UNAVAILABLE", "message": "offline", "detail": {},
                })(),
            })()

    target = service()
    target.dispatcher = DiscoveryFailureDispatcher()
    result = asyncio.run(target.test_run(fixtures=["Demo.Fixture"]))
    assert not result.ok and result.error.code == "TEST_SELECTOR_DISCOVERY_FAILED"
    assert result.error.detail["runnerStartAttempted"] is False
    assert [name for name, _ in target.dispatcher.calls] == ["test.list"]


def test_strict_run_rejects_a_stale_caller_snapshot_before_scene_preflight_or_runner():
    class SnapshotDispatcher(Dispatcher):
        async def call(self, request_id, name, payload, **kwargs):
            self.calls.append((name, payload))
            if name == "test.list":
                return ok(request_id, {
                    "selectionValid": True, "matchedCount": 1,
                    "selectionDomain": "current-domain", "selectionSnapshotId": "current-snapshot",
                })
            pytest.fail("a stale selection must not prepare scenes or dispatch test.run")

    target = service()
    target.dispatcher = SnapshotDispatcher()
    result = asyncio.run(target.test_run(
        fixtures=["Demo.Fixture"],
        expected_selection_domain="old-domain",
        expected_selection_snapshot_id="old-snapshot",
    ))
    assert not result.ok and result.error.code == "TEST_SELECTION_STALE"
    assert result.error.detail["runnerStartAttempted"] is False
    assert result.error.detail["actualSelectionSnapshotId"] == "current-snapshot"
    assert [name for name, _ in target.dispatcher.calls] == ["test.list"]


def test_strict_run_forwards_its_discovery_snapshot_to_the_single_runner_start():
    class SnapshotDispatcher(Dispatcher):
        async def call(self, request_id, name, payload, **kwargs):
            self.calls.append((name, payload))
            if name == "test.list":
                return ok(request_id, {
                    "selectionValid": True, "matchedCount": 1,
                    "selectionDomain": "domain-one", "selectionSnapshotId": "snapshot-one",
                })
            if name == "scene.prepareForAutomation":
                return ok(request_id, {"prepared": True, "action": "none", "items": []})
            assert name == "test.run"
            return ok(request_id, {"runGuid": "one-run"})

    target = service()
    target.dispatcher = SnapshotDispatcher()
    result = asyncio.run(target.test_run(fixtures=["Demo.Fixture"]))
    assert result.ok
    name, payload = target.dispatcher.calls[-1]
    assert name == "test.run"
    assert payload["expectedSelectionDomain"] == "domain-one"
    assert payload["expectedSelectionSnapshotId"] == "snapshot-one"


def test_partial_expected_selection_identity_rejects_without_any_editor_command():
    target = service()
    result = asyncio.run(target.test_run(
        fixtures=["Demo.Fixture"], expected_selection_domain="domain-only",
    ))
    assert not result.ok and result.error.code == "TEST_SELECTORS_INVALID"
    assert target.dispatcher.calls == []


def test_acceptance_forwards_discovery_snapshot_and_preserves_stale_error_without_runner(monkeypatch):
    from upilot_mcp.tool_facade import McpToolFacade

    target = McpToolFacade.__new__(McpToolFacade)
    canonical_project = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()

    async def status(**_kwargs):
        return ok("status", {
            "connected": True, "serverReady": True,
            "paths": {"unityProjectAbsolute": str(canonical_project)},
            "session": {}, "executionState": {},
        })

    async def ready(**_kwargs):
        return ok("ready", {"ready": True, "executionState": {}})

    async def captures(**_kwargs):
        return ok("captures", {"sessions": []})

    async def compile_wait(**_kwargs):
        return ok("compile", {"errorsVerified": True, "status": "success", "errorTotal": 0})

    async def listed(**_kwargs):
        return ok("list", {
            "selectionValid": True, "matchedCount": 1, "tests": ["Demo.Fixture.Test"],
            "selectionDomain": "acceptance-domain", "selectionSnapshotId": "acceptance-snapshot",
        })

    run_calls = []

    async def run(**kwargs):
        run_calls.append(kwargs)
        return target._selection_stale_response(
            "run", {"selectionDomain": "new-domain", "selectionSnapshotId": "new-snapshot"},
            (kwargs["expected_selection_domain"], kwargs["expected_selection_snapshot_id"]),
            "The selection changed after discovery; TestRunnerApi.Execute was not called.",
        )

    monkeypatch.setattr(target, "mcp_status", status)
    monkeypatch.setattr(target, "ensure_ready", ready)
    monkeypatch.setattr(target, "console_capture_list", captures)
    monkeypatch.setattr(target, "safe_compile_and_wait", compile_wait)
    monkeypatch.setattr(target, "test_list", listed)
    monkeypatch.setattr(target, "test_run", run)

    result = asyncio.run(target._execute_upilot_acceptance_run(
        fixtures=["Demo.Fixture"], write_artifact=False,
    ))
    assert not result.ok and result.error.code == "TEST_SELECTION_STALE"
    assert result.data is None
    assert run_calls == [{
        "test_mode": "EditMode", "test_names": None, "fixtures": ["Demo.Fixture"],
        "assemblies": None, "categories": None, "match_mode": "union",
        "require_all_selectors_match": True, "test_filter": "",
        "expected_selection_domain": "acceptance-domain",
        "expected_selection_snapshot_id": "acceptance-snapshot",
    }]


def test_acceptance_strict_selector_rejection_never_starts_the_runner(monkeypatch):
    from upilot_mcp.tool_facade import McpToolFacade

    target = McpToolFacade.__new__(McpToolFacade)
    canonical_project = (Path(__file__).resolve().parents[2] / "Tests~" / "UPilotTest").resolve()

    async def status(**_kwargs):
        return ok("status", {
            "connected": True, "serverReady": True,
            "paths": {"unityProjectAbsolute": str(canonical_project)},
            "session": {}, "executionState": {},
        })

    async def ready(**_kwargs):
        return ok("ready", {"ready": True, "executionState": {}})

    async def captures(**_kwargs):
        return ok("captures", {"sessions": []})

    async def compile_wait(**_kwargs):
        return ok("compile", {"errorsVerified": True, "status": "success", "errorTotal": 0})

    async def listed(**_kwargs):
        return ok("list", {
            "selectionValid": False,
            "unmatchedSelectors": [{"kind": "fixture", "selector": "Missing", "candidates": []}],
        })

    async def forbidden_run(**_kwargs):
        raise AssertionError("a strict unmatched acceptance selector must not call test.run")

    monkeypatch.setattr(target, "mcp_status", status)
    monkeypatch.setattr(target, "ensure_ready", ready)
    monkeypatch.setattr(target, "console_capture_list", captures)
    monkeypatch.setattr(target, "safe_compile_and_wait", compile_wait)
    monkeypatch.setattr(target, "test_list", listed)
    monkeypatch.setattr(target, "test_run", forbidden_run)

    result = asyncio.run(target._execute_upilot_acceptance_run(
        fixtures=["Missing"], write_artifact=False,
    ))

    assert not result.ok and result.error.code == "TEST_SELECTOR_UNMATCHED"
    assert result.error.detail["runnerStartAttempted"] is False


def test_native_and_proxy_selector_schemas_match():
    from upilot_mcp.mcp_stdio_server import mcp
    from upilot_mcp.tool_facade import McpToolFacade
    import inspect
    exposed = {tool.name: tool for tool in asyncio.run(mcp.list_tools())}
    for name in ("unity_test_list", "unity_test_run", "unity_upilot_acceptance_run"):
        schema = exposed[name].inputSchema["properties"]
        assert all(key in schema for key in ("assemblies", "categories", "matchMode", "requireAllSelectorsMatch"))
        assert schema["matchMode"]["default"] == "union"
        assert schema["requireAllSelectorsMatch"]["default"] is True
        parameters = inspect.signature(getattr(McpToolFacade, REGISTRY.resolve(name).facade_method)).parameters
        assert all(key in parameters for key in ("assemblies", "categories", "match_mode", "require_all_selectors_match"))
    for name in ("unity_test_run", "unity_upilot_acceptance_run"):
        schema = exposed[name].inputSchema["properties"]
        assert all(key in schema for key in ("expectedSelectionDomain", "expectedSelectionSnapshotId"))


def test_native_and_proxy_list_forward_null_and_selector_contract_identically(monkeypatch):
    from upilot_mcp import mcp_stdio_server as runtime
    from upilot_mcp.tool_registry import dispatch_public_tool

    calls = []

    class Facade:
        async def test_list(
            self, test_mode="EditMode", test_filter=None, test_names=None, fixtures=None,
            assemblies=None, categories=None, match_mode="union", require_all_selectors_match=True,
        ):
            kwargs = {
                "test_mode": test_mode, "test_filter": test_filter, "test_names": test_names,
                "fixtures": fixtures, "assemblies": assemblies, "categories": categories,
                "match_mode": match_mode, "require_all_selectors_match": require_all_selectors_match,
            }
            calls.append(kwargs)
            return ok("list", {"received": kwargs})

    facade = Facade()
    monkeypatch.setattr(test_tools, "_get_facade", lambda: facade)
    arguments = {
        "testFilter": None,
        "fixtures": ["Demo.Fixture"],
        "assemblies": ["Example.Tests"],
        "categories": ["Smoke"],
        "matchMode": "intersection",
        "requireAllSelectorsMatch": False,
    }
    native = asyncio.run(runtime.mcp._tool_manager.call_tool("unity_test_list", arguments))
    proxy = asyncio.run(dispatch_public_tool(facade, "unity_test_list", arguments))

    assert native.structuredContent["data"]["received"]["test_filter"] is None
    assert proxy.ok
    assert calls == [
        {
            "test_mode": "EditMode", "test_filter": None, "test_names": None,
            "fixtures": ["Demo.Fixture"], "assemblies": ["Example.Tests"],
            "categories": ["Smoke"], "match_mode": "intersection",
            "require_all_selectors_match": False,
        },
        {
            "test_mode": "EditMode", "test_filter": None, "test_names": None,
            "fixtures": ["Demo.Fixture"], "assemblies": ["Example.Tests"],
            "categories": ["Smoke"], "match_mode": "intersection",
            "require_all_selectors_match": False,
        },
    ]
