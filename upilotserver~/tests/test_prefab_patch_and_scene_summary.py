import asyncio

from upilot_mcp.domain.resource_service import ResourceDomainService
from upilot_mcp.responses import ok, fail
from upilot_mcp.tool_registry import REGISTRY
from upilot_mcp.mcp_tools import resource_tools  # noqa: F401


class Service(ResourceDomainService):
    def __init__(self):
        self.dispatcher = self
        self.calls = []
        self.approved = True
        self.reply = {"persistenceVerified": True}

    async def call(self, request_id, name, payload, **kwargs):
        self.calls.append((name, payload))
        return ok(request_id, self.reply)

    def _reject_write_if_unapproved(self, request_id, name):
        return None if self.approved else fail(request_id, "WRITE_ACCESS_NOT_APPROVED", name)

    async def ensure_ready(self, **kwargs):
        return ok("ready", {"ready": True})


def test_prefab_preview_and_apply_use_one_semantic_command():
    target = Service()
    target.approved = False
    args = {"asset_path": "Assets/Test.prefab", "component_type": "Probe", "properties": [{"propertyPath": "value", "value": "2"}]}
    assert asyncio.run(target.prefab_patch(**args)).ok
    assert target.calls[-1][1]["dryRun"] is True
    assert not asyncio.run(target.prefab_patch(**args, dry_run=False, confirm_token="token")).ok
    assert len(target.calls) == 1
    target.approved = True
    assert not asyncio.run(target.prefab_patch(**args, dry_run=False)).ok
    target.reply = {"error": "Changed after preview", "recoveryStatus": "external_change_preserved"}
    result = asyncio.run(target.prefab_patch(**args, dry_run=False, confirm_token="token"))
    assert not result.ok and result.error.detail["recoveryStatus"] == "external_change_preserved"


def test_scene_summary_preserves_budgets_and_is_read_only():
    target = Service()
    assert not asyncio.run(target.scene_summary(max_nodes=0)).ok
    assert target.calls == []
    assert asyncio.run(target.scene_summary(max_nodes=10, max_milliseconds=20, max_examples=2)).ok
    assert target.calls == [("resource.sceneSummary", {"maxNodes": 10, "maxMilliseconds": 20, "maxExamples": 2})]
    descriptor = REGISTRY.resolve("unity_scene_summary")
    assert descriptor.idempotent and not descriptor.destructive and not descriptor.requires_write_access
    assert REGISTRY.resolve("unity_prefab_patch").idempotent is False
