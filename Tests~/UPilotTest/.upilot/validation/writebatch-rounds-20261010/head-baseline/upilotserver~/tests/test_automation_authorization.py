from upilot_mcp.automation_authorization import CATALOG_SCOPE_VERSION, authorization_status, catalog, catalog_hash, normalize_scopes


def test_catalog_select_all_requires_matching_version_and_hash() -> None:
    keys = [item.key for item in catalog()]
    status = authorization_status(keys, catalog_hash(), CATALOG_SCOPE_VERSION)
    assert status["fullAuthorization"] is True
    assert status["mixedAuthorization"] is False
    stale = authorization_status(keys, "old-catalog", CATALOG_SCOPE_VERSION - 1)
    assert stale["fullAuthorization"] is False
    assert stale["mixedAuthorization"] is True


def test_catalog_normalizes_unknown_and_partial_grants() -> None:
    keys = [item.key for item in catalog()]
    assert normalize_scopes([keys[0], "unknown", keys[0]]) == (keys[0],)
    partial = authorization_status([keys[0]], catalog_hash(), CATALOG_SCOPE_VERSION)
    assert partial["mixedAuthorization"] is True
    assert keys[1] in partial["missingScopes"]
