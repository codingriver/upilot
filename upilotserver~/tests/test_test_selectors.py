import asyncio

import pytest

from upilot_mcp.domain.test_service import TestDomainService
from upilot_mcp.responses import ok
from upilot_mcp.tool_registry import REGISTRY
from upilot_mcp.mcp_tools import test_tools  # noqa: F401


class Dispatcher:
    def __init__(self):
        self.calls = []

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, payload))
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


@pytest.mark.parametrize("args", [
    {"test_names": []},
    {"fixtures": []},
    {"fixtures": [""]},
    {"test_names": ["A"], "test_filter": "regex:.*"},
    {"test_names": ["A"] * 257},
    {"test_names": "A"},
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
