from __future__ import annotations

import asyncio
import hashlib
import json
import os
from pathlib import Path

from upilot_mcp.config import CONFIG
from upilot_mcp.domain.analysis_service import ProjectAnalysisDomainService
from upilot_mcp.state_store import StateStore
from upilot_mcp.responses import ok


class _Session:
    def __init__(self, project_path: Path) -> None:
        self.project_path = str(project_path)
        self.process_id = 0
        self.last_heartbeat_at = 0


class _SessionManager:
    def __init__(self, project_path: Path) -> None:
        self.active = _Session(project_path)


class _Server:
    def __init__(self, project_path: Path) -> None:
        self.session_manager = _SessionManager(project_path)
        self.state = StateStore()


class _Service(ProjectAnalysisDomainService):
    def __init__(self, project_path: Path) -> None:
        self.server = _Server(project_path)


class _RecordingDispatcher:
    def __init__(self) -> None:
        self.calls = []

    async def call(self, request_id: str, name: str, payload: dict):
        self.calls.append((name, payload))
        return ok(request_id, {"name": name, "payload": payload})


def test_runtime_diagnostics_routes_are_structured_and_bounded() -> None:
    service = ProjectAnalysisDomainService.__new__(ProjectAnalysisDomainService)
    service.dispatcher = _RecordingDispatcher()

    sample = asyncio.run(service.navmesh_sample([{"x": 1, "y": 2, "z": 3}], max_distance=0, area_mask=7))
    profiler = asyncio.run(service.profiler_capture_start(duration_sec=99999, sample_every_frames=0, title="battle", marker_names=["Battle.Tick"], marker_name_regex="URP", max_markers=999, telemetry_type_name="Game.Telemetry", telemetry_method_name="Sample"))

    assert sample.ok
    assert service.dispatcher.calls[0][0] == "navmesh.sample"
    assert service.dispatcher.calls[0][1]["maxDistance"] == 0.001
    assert profiler.ok
    assert service.dispatcher.calls[1][0] == "profiler.capture.start"
    assert service.dispatcher.calls[1][1]["durationSec"] == 3600.0
    assert service.dispatcher.calls[1][1]["sampleEveryFrames"] == 1
    assert service.dispatcher.calls[1][1]["markerNames"] == ["Battle.Tick"]
    assert service.dispatcher.calls[1][1]["maxMarkers"] == 256
    assert service.dispatcher.calls[1][1]["telemetryTypeName"] == "Game.Telemetry"


def test_texture_importer_patch_preview_binds_asset_and_meta_hash(tmp_path: Path) -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    asset = tmp_path / "Assets" / "line.png"
    asset.parent.mkdir(parents=True)
    asset.write_bytes(b"png")
    asset.with_suffix(".png.meta").write_text("meta", encoding="utf-8")

    class _Session:
        project_path = str(tmp_path)

    class _SessionManager:
        active = _Session()

    class _Server:
        session_manager = _SessionManager()

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.server = _Server()

    async def _get(_: str):
        return ok("req-get", {"mipmapEnabled": True})

    service.texture_importer_get = _get
    preview = asyncio.run(service.texture_importer_patch("Assets/line.png", {"mipmapEnabled": False}, dry_run=True))

    assert preview.ok
    assert preview.data["writeAttempted"] is False
    assert preview.data["before"]["mipmapEnabled"] is True
    expected_source_hash = hashlib.sha256(b"png").hexdigest()
    expected_meta_hash = hashlib.sha256(b"meta").hexdigest()
    assert preview.data["sourceSha256"] == expected_source_hash
    assert preview.data["metaSha256"] == expected_meta_hash
    expected_token_payload = json.dumps(
        {
            "assetPath": "Assets/line.png",
            "sourceSha256": expected_source_hash,
            "metaSha256": expected_meta_hash,
            "changes": {"mipmapEnabled": False},
            "reimport": True,
        },
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    assert preview.data["confirmToken"] == hashlib.sha256(expected_token_payload).hexdigest()


def test_asset_dependencies_route_is_read_only() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict):
            assert name == "asset.dependencies"
            assert payload == {"assetPath": "Assets/Test.prefab", "recursive": False}
            return ok(request_id, {"dependencies": []})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.asset_dependencies("Assets/Test.prefab", recursive=False))
    assert result.ok


def test_object_dependency_reverse_query_is_validated_before_unity_dispatch() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        def __init__(self):
            self.calls = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {"dependencies": []})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()

    invalid = asyncio.run(service.asset_dependencies(
        asset_path="",
        evidence_mode="object",
        direction="reverse",
        reference_query={"kind": "guid", "value": "fixture-guid"},
        scope=[],
    ))
    assert not invalid.ok
    assert invalid.error.code == "REFERENCE_QUERY_INVALID"
    assert service.dispatcher.calls == []

    result = asyncio.run(service.asset_dependencies(
        asset_path="",
        evidence_mode="object",
        direction="reverse",
        reference_query={"kind": "object", "guid": "fixture-guid", "localFileId": "11400000"},
        scope=["Assets/Fixtures"],
        max_nodes=17,
        time_budget_ms=250,
    ))
    assert result.ok
    assert service.dispatcher.calls == [(
        "asset.dependencies",
        {
            "assetPath": "", "recursive": True, "evidenceMode": "object", "runtimeBoundary": "none",
            "direction": "reverse", "referenceQuery": {"kind": "object", "guid": "fixture-guid", "localFileId": "11400000"},
            "scope": ["Assets/Fixtures"], "maxNodes": 17, "timeBudgetMs": 250, "continuationToken": "",
        },
    )]


def test_dependency_continuation_token_is_rejected_for_forward_query() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        async def call(self, request_id: str, name: str, payload: dict):
            raise AssertionError("invalid dependency query must not reach Unity")

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    result = asyncio.run(service.asset_dependencies(
        asset_path="Assets/Test.prefab",
        continuation_token="cursor:1",
    ))
    assert not result.ok
    assert result.error.code == "REFERENCE_QUERY_INVALID"
    assert "continuationToken" in result.error.message


def test_prefab_reference_options_reject_invalid_combinations_without_dispatch() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        calls = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    invalid_depth = asyncio.run(service.prefab_query_components("Assets/Test.prefab", "Probe", reference_depth=5))
    invalid_nested = asyncio.run(service.prefab_query_components("Assets/Test.prefab", "Probe", include_nested_prefab_contents=True))
    assert invalid_depth.error.code == "REFERENCE_DEPTH_INVALID"
    assert invalid_nested.error.code == "REFERENCE_QUERY_INVALID"
    assert service.dispatcher.calls == []


def test_dependency_reverse_query_canonicalizes_scope_and_literal_fields_for_cursor_resume() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        def __init__(self):
            self.calls = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {"coverageComplete": False, "nextContinuationToken": "opaque:1"})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    first = asyncio.run(service.asset_dependencies(
        evidence_mode="object",
        direction="reverse",
        reference_query={"kind": "stringLiteral", "value": "fixture-key", "propertyPaths": ["m_second", "m_first", "m_first"]},
        scope=["Assets/Z/", "Assets/A", "Assets/A"],
        max_nodes=1,
        time_budget_ms=1,
    ))
    resumed = asyncio.run(service.asset_dependencies(
        evidence_mode="object",
        direction="reverse",
        reference_query={"kind": "stringLiteral", "value": "fixture-key", "propertyPaths": ["m_first", "m_second"]},
        scope=["Assets/A", "Assets/Z"],
        max_nodes=1,
        time_budget_ms=1,
        continuation_token="opaque:1",
    ))

    assert first.ok and resumed.ok
    first_payload = service.dispatcher.calls[0][1]
    resumed_payload = service.dispatcher.calls[1][1]
    assert first_payload["scope"] == resumed_payload["scope"] == ["Assets/A", "Assets/Z"]
    assert first_payload["referenceQuery"]["propertyPaths"] == resumed_payload["referenceQuery"]["propertyPaths"] == ["m_first", "m_second"]
    assert resumed_payload["continuationToken"] == "opaque:1"


def test_dependency_invalid_cursor_scope_and_budget_never_reach_unity() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        calls = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    invalid_requests = [
        {"asset_path": "Assets/Test.prefab", "recursive": 1},
        {"asset_path": "Assets/Test.prefab", "continuation_token": 1},
        {"asset_path": "", "evidence_mode": "object", "direction": "reverse", "reference_query": {"kind": "guid", "value": "id"}, "scope": ["Assets/../Editor"]},
        {"asset_path": "", "evidence_mode": "object", "direction": "reverse", "reference_query": {"kind": "guid", "value": "id"}, "scope": ["Assets\\Fixtures"]},
        {"asset_path": "", "evidence_mode": "object", "direction": "reverse", "reference_query": {"kind": "guid", "value": "id"}, "scope": ["Assets/Fixtures"], "max_nodes": True},
        {"asset_path": "", "evidence_mode": "object", "direction": "reverse", "reference_query": {"kind": "guid", "value": "id"}, "scope": ["Assets/Fixtures"], "time_budget_ms": 30001},
    ]

    results = [asyncio.run(service.asset_dependencies(**request)) for request in invalid_requests]

    assert [result.error.code for result in results] == [
        "DEPENDENCY_RECURSIVE_INVALID", "REFERENCE_QUERY_INVALID", "REFERENCE_SCOPE_INVALID",
        "REFERENCE_SCOPE_INVALID", "DEPENDENCY_NODE_BUDGET_INVALID", "DEPENDENCY_TIME_BUDGET_INVALID",
    ]
    assert service.dispatcher.calls == []


def test_prefab_reference_query_shape_is_explicit_and_invalid_inputs_do_not_load() -> None:
    from upilot_mcp.domain.resource_service import ResourceDomainService

    class _Dispatcher:
        def __init__(self):
            self.calls = []

        async def call(self, request_id: str, name: str, payload: dict):
            self.calls.append((name, payload))
            return ok(request_id, {"readOnly": True, "changedEditorState": False})

    service = ResourceDomainService.__new__(ResourceDomainService)
    service.dispatcher = _Dispatcher()
    invalid = [
        asyncio.run(service.prefab_query_components("", "Probe")),
        asyncio.run(service.prefab_query_components("Assets/Test.asset", "Probe")),
        asyncio.run(service.prefab_query_components("Assets/Test.prefab", "")),
        asyncio.run(service.prefab_query_components("Assets/Test.prefab", "Probe", follow_object_references=1)),
        asyncio.run(service.prefab_query_components("Assets/Test.prefab", "Probe", reference_depth=2)),
    ]
    tracked = asyncio.run(service.prefab_query_components(
        "Assets/Test.prefab", "Probe", follow_object_references=True,
        include_nested_prefab_contents=True, reference_depth=4,
    ))

    assert [result.error.code for result in invalid] == [
        "INVALID_PREFAB_PATH", "INVALID_PREFAB_PATH", "INVALID_COMPONENT_TYPE",
        "REFERENCE_QUERY_INVALID", "REFERENCE_QUERY_INVALID",
    ]
    assert tracked.ok
    assert service.dispatcher.calls == [("prefab.queryComponents", {
        "prefabPath": "Assets/Test.prefab", "componentType": "Probe", "includeSerializedFields": True,
        "maxDepth": 6, "maxResults": 50, "followObjectReferences": True,
        "includeNestedPrefabContents": True, "referenceDepth": 4,
    })]


def test_hang_pid_resolution_replaces_stale_session_pid_from_editor_state(tmp_path: Path) -> None:
    service = _Service(tmp_path)
    service.server.session_manager.active.process_id = 111
    service.server.state.editor.process_id = 222
    service._query_unity_processes = lambda: [{"ProcessId": 222, "ProcessCreatedAt": 100, "ExecutablePath": "C:/Unity/Unity.exe",
                                               "CommandLine": f'Unity.exe -projectPath "{tmp_path}"'}]
    service._process_creation_time = lambda pid: 100 if pid == 222 else 0

    pid, diagnostics = service._resolve_live_unity_pid()

    assert pid == 222
    assert diagnostics["pidSource"] == "editorState"
    assert diagnostics["pidRefreshed"] is True
    assert diagnostics["staleProcessIds"] == [111]
    assert diagnostics["projectIdentityVerified"] is True
    assert service.server.session_manager.active.process_id == 222
    assert service.server.state.editor.process_id == 222


def test_hang_pid_resolution_discovers_project_process_after_all_cached_pids_stale(tmp_path: Path) -> None:
    service = _Service(tmp_path)
    service.server.session_manager.active.process_id = 111
    service.server.state.editor.process_id = 222
    service._query_unity_processes = lambda: [{"ProcessId": 333, "ProcessCreatedAt": 100, "ExecutablePath": "C:/Unity/Unity.exe",
                                               "CommandLine": f'Unity.exe -projectPath "{tmp_path}"'}]
    service._process_creation_time = lambda pid: 100 if pid == 333 else 0

    pid, diagnostics = service._resolve_live_unity_pid()

    assert pid == 333
    assert diagnostics["pidSource"] == "processDiscovery"
    assert diagnostics["pidRefreshed"] is True
    assert diagnostics["staleProcessIds"] == [111, 222]
    assert diagnostics["projectIdentityVerified"] is True
    assert service.server.session_manager.active.process_id == 333
    assert service.server.state.editor.process_id == 333


def test_csv_get_and_confirmed_patch_preserve_gbk_crlf(tmp_path: Path) -> None:
    config_dir = tmp_path / "Assets" / "Config"
    config_dir.mkdir(parents=True)
    path = config_dir / "levels.csv"
    original = (
        "关卡ID,名称,出生点,陷阱\r\n"
        "uint,string,uint[],uint[]\r\n"
        "#level_id,name,born_list,trap_list\r\n"
        '10044,"测试",1;2,3;4\r\n'
        "10045,其它,5,6\r\n"
    ).encode("gbk")
    path.write_bytes(original)
    service = _Service(tmp_path)

    read = asyncio.run(
        service.config_csv_get(
            path="Assets/Config/levels.csv",
            keys={"level_id": "10044"},
            fields=["born_list", "trap_list"],
        )
    )
    assert read.ok
    assert read.data["encoding"] == "gb18030"
    assert read.data["newline"] == "CRLF"
    assert read.data["unique"] is True
    assert read.data["rows"][0]["trap_list"] == "3;4"
    assert read.data["headerLocation"]["recordIndex"] == 3
    assert read.data["rowLocations"][0]["recordIndex"] == 4
    assert read.data["rowLocations"][0]["physicalStartLine"] == 4
    assert read.data["rowLocations"][0]["characterEndOffsetExclusive"] > read.data["rowLocations"][0]["characterStartOffset"]

    preview = asyncio.run(
        service.config_csv_patch(
            path="Assets/Config/levels.csv",
            keys={"level_id": "10044"},
            changes={"trap_list": "3;4;1004406"},
            expected_values={"trap_list": "3;4"},
            dry_run=True,
        )
    )
    assert preview.ok
    assert preview.data["outsideTargetBytesUnchanged"] is True
    assert preview.data["headerRowIndex"] == 3
    assert path.read_bytes() == original

    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        applied = asyncio.run(
            service.config_csv_patch(
                path="Assets/Config/levels.csv",
                keys={"level_id": "10044"},
                changes={"trap_list": "3;4;1004406"},
                expected_values={"trap_list": "3;4"},
                dry_run=False,
                confirm_token=preview.data["confirmToken"],
            )
        )
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert applied.ok and applied.data["applied"] is True
    updated = path.read_bytes()
    assert b"\r\n" in updated
    assert updated.endswith("10045,其它,5,6\r\n".encode("gbk"))
    assert '10044,"测试",1;2,3;4;1004406\r\n' in updated.decode("gbk")
    assert "3;4;1004406" in updated.decode("gbk")


def test_csv_duplicate_header_is_diagnostic_and_patch_is_rejected(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    (config / "duplicate.csv").write_text("Id,Name,Name\n7,A,B\n", encoding="utf-8")
    service = _Service(tmp_path)

    read = asyncio.run(service.config_csv_get("Assets/Config/duplicate.csv", {"Id": "7"}))
    patch = asyncio.run(service.config_csv_patch("Assets/Config/duplicate.csv", {"Id": "7"}, {"Name": "C"}))

    assert read.ok and read.data["ambiguousColumns"] == [{"name": "Name", "columnIndices": [2, 3]}]
    assert not patch.ok and patch.error.code == "CSV_PATCH_FAILED"


def test_csv_header_candidates_include_physical_locations_and_failed_inference(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    (config / "candidates.csv").write_text(
        "schema,comment\r\nstring,string\r\nId,Name\r\n7,ok\r\n",
        encoding="utf-8-sig", newline="",
    )
    service = _Service(tmp_path)

    read = asyncio.run(service.config_csv_get("Assets/Config/candidates.csv", {"Id": "7"}))
    missing = asyncio.run(service.config_csv_get("Assets/Config/candidates.csv", {"Unknown": "7"}))

    assert read.ok
    assert read.data["headerCandidates"][0]["location"]["physicalStartLine"] == 1
    assert read.data["headerCandidates"][2]["satisfiesRequired"] is True
    assert read.data["headerCandidates"][2]["matchedRequiredColumns"] == ["Id"]
    assert not missing.ok and missing.error.code == "CSV_READ_FAILED"
    assert missing.error.detail["reason"] == "HEADER_NOT_FOUND"
    assert missing.error.detail["requiredColumns"] == ["Unknown"]
    assert len(missing.error.detail["headerCandidates"]) == 4
    assert missing.error.detail["headerCandidates"][2]["physicalStartLine"] == 3


def test_csv_explicit_bad_header_returns_bounded_location_diagnostic_without_writing(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "explicit-header.csv"
    original = b"Id,Name\n7,ok\n"
    path.write_bytes(original)
    service = _Service(tmp_path)

    invalid = asyncio.run(service.config_csv_get(
        "Assets/Config/explicit-header.csv", {"Missing": "7"}, header_row_index=1,
    ))
    out_of_range = asyncio.run(service.config_csv_get(
        "Assets/Config/explicit-header.csv", {"Id": "7"}, header_row_index=3,
    ))
    wrong_codec = asyncio.run(service.config_csv_get(
        "Assets/Config/explicit-header.csv", {"Id": "7"}, encoding="utf-16-le",
    ))

    assert not invalid.ok and invalid.error.code == "CSV_READ_FAILED"
    assert invalid.error.detail["reason"] == "HEADER_NOT_FOUND"
    assert invalid.error.detail["writeAttempted"] is False
    candidate = invalid.error.detail["headerCandidates"]
    assert len(candidate) == 1
    assert candidate[0]["recordIndex"] == 1
    assert candidate[0]["physicalStartLine"] == 1
    assert candidate[0]["missingFields"] == ["Missing"]
    assert not out_of_range.ok and out_of_range.error.code == "CSV_READ_FAILED"
    assert not wrong_codec.ok and wrong_codec.error.code == "CSV_READ_FAILED"
    assert path.read_bytes() == original


def test_csv_auto_header_keeps_first_match_but_reports_later_candidates(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    (config / "multiple_headers.csv").write_text(
        "Id,Name\n7,First\nId,Name\n8,Second\n",
        encoding="utf-8",
    )
    service = _Service(tmp_path)

    result = asyncio.run(service.config_csv_get("Assets/Config/multiple_headers.csv", {"Id": "7"}))

    assert result.ok
    assert result.data["headerRowIndex"] == 1
    assert [candidate["recordIndex"] for candidate in result.data["headerCandidates"]] == [1, 2, 3, 4]
    assert [candidate["recordIndex"] for candidate in result.data["headerCandidates"] if candidate["satisfiesRequired"]] == [1, 3]


def test_csv_patch_replace_failure_keeps_original_file_and_removes_temp(tmp_path: Path, monkeypatch) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "replace_failure.csv"
    original = b"Id,Name\r\n7,Before\r\n"
    path.write_bytes(original)
    service = _Service(tmp_path)
    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        preview = asyncio.run(service.config_csv_patch(
            "Assets/Config/replace_failure.csv", {"Id": "7"}, {"Name": "After"}, dry_run=True,
        ))
        monkeypatch.setattr(os, "replace", lambda *_args: (_ for _ in ()).throw(OSError("replace failed")))
        applied = asyncio.run(service.config_csv_patch(
            "Assets/Config/replace_failure.csv", {"Id": "7"}, {"Name": "After"},
            dry_run=False, confirm_token=preview.data["confirmToken"],
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert not applied.ok and applied.error.code == "CSV_PATCH_FAILED"
    assert applied.error.detail["writeAttempted"] is True
    assert path.read_bytes() == original
    assert list(config.glob(".replace_failure.csv.*.tmp")) == []


def test_csv_locations_keep_multiline_physical_lines_and_reject_negative_header_index(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "multiline.csv"
    path.write_bytes("\ufeffId,Text\r\n7,\"first\r\nsecond\"\r\n".encode("utf-8"))
    service = _Service(tmp_path)

    read = asyncio.run(service.config_csv_get("Assets/Config/multiline.csv", {"Id": "7"}, fields=["Text"]))
    negative = asyncio.run(service.config_csv_get("Assets/Config/multiline.csv", {"Id": "7"}, header_row_index=-1))
    missing = asyncio.run(service.config_csv_get("Assets/Config/multiline.csv", {"Missing": "7"}))

    assert read.ok
    assert read.data["bom"] is True
    assert read.data["rows"] == [{"Text": "first\r\nsecond"}]
    assert read.data["headerLocation"] == {
        "recordIndex": 1, "physicalStartLine": 1, "physicalEndLine": 1,
        "characterStartOffset": 0, "characterEndOffsetExclusive": 7,
    }
    assert read.data["rowLocations"][0]["physicalStartLine"] == 2
    assert read.data["rowLocations"][0]["physicalEndLine"] == 3
    assert read.data["rowLocations"][0]["characterStartOffset"] == 9
    assert not negative.ok and negative.error.code == "CSV_READ_FAILED"
    assert not missing.ok and missing.error.detail["headerCandidates"][0]["missingFields"] == ["Missing"]


def test_csv_bom_crlf_multiline_header_and_unterminated_business_record_keep_logical_indexes(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "bom-multiline.csv"
    text = 'metadata,"type\r\nname"\r\nId,"Text\r\nNotes"\r\n7,"first\r\nsecond"'
    path.write_bytes(b"\xef\xbb\xbf" + text.encode("utf-8"))
    service = _Service(tmp_path)

    read = asyncio.run(service.config_csv_get("Assets/Config/bom-multiline.csv", {"Id": "7"}))
    explicit = asyncio.run(service.config_csv_get(
        "Assets/Config/bom-multiline.csv", {"Id": "7"}, header_row_index=read.data["headerRowIndex"],
    ))

    assert read.ok and explicit.ok
    assert read.data["bom"] is True
    assert read.data["headerRowIndex"] == 2
    assert read.data["headerLocation"] == {
        "recordIndex": 2,
        "physicalStartLine": 3,
        "physicalEndLine": 4,
        "characterStartOffset": text.index("Id,"),
        "characterEndOffsetExclusive": text.index("Id,") + len('Id,"Text\r\nNotes"'),
    }
    assert read.data["rowLocations"] == [{
        "recordIndex": 3,
        "physicalStartLine": 5,
        "physicalEndLine": 6,
        "characterStartOffset": text.index("7,"),
        "characterEndOffsetExclusive": len(text),
    }]
    assert explicit.data["rows"] == read.data["rows"]


def test_csv_explicit_utf8_encoding_still_excludes_bom_from_header_and_offsets(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "explicit-bom.csv"
    path.write_bytes(b"\xef\xbb\xbfId,Name\r\n7,ok\r\n")
    service = _Service(tmp_path)

    result = asyncio.run(service.config_csv_get(
        "Assets/Config/explicit-bom.csv", {"Id": "7"}, encoding="utf-8",
    ))

    assert result.ok
    assert result.data["encoding"] == "utf-8"
    assert result.data["encodingConfidence"] == "explicit-bom"
    assert result.data["bom"] is True
    assert result.data["headerLocation"]["characterStartOffset"] == 0
    assert result.data["rows"] == [{"Id": "7", "Name": "ok"}]


def test_csv_auto_and_explicit_header_selection_skip_empty_records_and_empty_pages(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "empty-records.csv"
    path.write_text("\n\nId,Name\n7,First\nId,Name\n8,Second\n", encoding="utf-8", newline="")
    service = _Service(tmp_path)

    automatic = asyncio.run(service.config_csv_get("Assets/Config/empty-records.csv", {"Id": "7"}))
    explicit = asyncio.run(service.config_csv_get(
        "Assets/Config/empty-records.csv", {"Id": "8"}, header_row_index=5,
    ))
    empty_page = asyncio.run(service.config_csv_get("Assets/Config/empty-records.csv", {"Id": "missing"}))

    assert automatic.ok and automatic.data["headerRowIndex"] == 3
    assert automatic.data["rows"] == [{"Id": "7", "Name": "First"}]
    assert explicit.ok and explicit.data["headerRowIndex"] == 5
    assert explicit.data["rows"] == [{"Id": "8", "Name": "Second"}]
    assert empty_page.ok and empty_page.data["rows"] == []
    assert empty_page.data["rowLocations"] == []


def test_csv_duplicate_business_key_refuses_patch_without_changing_bytes(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "duplicate-key.csv"
    original = b"Id,Name\n7,First\n7,Second\n"
    path.write_bytes(original)
    service = _Service(tmp_path)

    read = asyncio.run(service.config_csv_get("Assets/Config/duplicate-key.csv", {"Id": "7"}))
    patch = asyncio.run(service.config_csv_patch(
        "Assets/Config/duplicate-key.csv", {"Id": "7"}, {"Name": "Changed"},
    ))

    assert read.ok and read.data["matchCount"] == 2 and read.data["unique"] is False
    assert not patch.ok and patch.error.code == "CSV_PATCH_FAILED"
    assert patch.error.detail["writeAttempted"] is False
    assert path.read_bytes() == original


def test_csv_apply_response_loss_is_resolved_by_hash_observation_without_replay(tmp_path: Path, monkeypatch) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "response-loss.csv"
    path.write_bytes(b"Id,Name,Other\r\n7,Before,untouched\r\n")
    service = _Service(tmp_path)
    preview = asyncio.run(service.config_csv_patch(
        "Assets/Config/response-loss.csv", {"Id": "7"}, {"Name": "After"}, dry_run=True,
    ))
    assert preview.ok

    replace_calls = 0
    real_replace = os.replace

    def replace_then_lose_response(source, destination):
        nonlocal replace_calls
        replace_calls += 1
        real_replace(source, destination)
        raise ConnectionError("simulated response lost after completed replacement")

    monkeypatch.setattr(os, "replace", replace_then_lose_response)
    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        uncertain = asyncio.run(service.config_csv_patch(
            "Assets/Config/response-loss.csv", {"Id": "7"}, {"Name": "After"},
            dry_run=False, confirm_token=preview.data["confirmToken"],
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    # A caller must inspect the file identity after an unknown result; it must not replay the write.
    observed = asyncio.run(service.config_csv_get("Assets/Config/response-loss.csv", {"Id": "7"}))

    assert not uncertain.ok and uncertain.error.code == "CSV_PATCH_FAILED"
    assert uncertain.error.detail["writeAttempted"] is True
    assert observed.ok
    assert observed.data["sha256"] == preview.data["afterSha256"]
    assert observed.data["rows"] == [{"Id": "7", "Name": "After", "Other": "untouched"}]
    assert replace_calls == 1


def test_csv_locations_keep_character_offsets_for_mixed_newlines_escaped_quotes_and_non_bmp(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "mixed.csv"
    text = 'Id,Text\r7,"first ""quote"" 😀"\n'
    path.write_bytes(text.encode("utf-8"))
    service = _Service(tmp_path)

    result = asyncio.run(service.config_csv_get("Assets/Config/mixed.csv", {"Id": "7"}, fields=["Text"]))

    assert result.ok
    assert result.data["rows"] == [{"Text": 'first "quote" 😀'}]
    assert result.data["headerLocation"] == {
        "recordIndex": 1, "physicalStartLine": 1, "physicalEndLine": 1,
        "characterStartOffset": 0, "characterEndOffsetExclusive": 7,
    }
    assert result.data["rowLocations"] == [{
        "recordIndex": 2, "physicalStartLine": 2, "physicalEndLine": 2,
        "characterStartOffset": 8, "characterEndOffsetExclusive": len(text) - 1,
    }]


def test_csv_normalized_duplicate_header_and_stale_preview_token_are_rejected(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    collision = config / "normalized-collision.csv"
    collision.write_text("Id,#Name,Name\n7,A,B\n", encoding="utf-8")
    service = _Service(tmp_path)

    collision_read = asyncio.run(service.config_csv_get("Assets/Config/normalized-collision.csv", {"Id": "7"}))
    collision_patch = asyncio.run(service.config_csv_patch(
        "Assets/Config/normalized-collision.csv", {"Id": "7"}, {"Name": "C"},
    ))

    assert collision_read.ok
    assert collision_read.data["ambiguousColumns"] == [{"name": "Name", "columnIndices": [2, 3]}]
    assert not collision_patch.ok and collision_patch.error.code == "CSV_PATCH_FAILED"

    path = config / "stale-token.csv"
    path.write_text("Id,Name,Other\n7,Before,one\n", encoding="utf-8")
    preview = asyncio.run(service.config_csv_patch(
        "Assets/Config/stale-token.csv", {"Id": "7"}, {"Name": "After"}, dry_run=True,
    ))
    path.write_text("Id,Name,Other\n7,Before,two\n", encoding="utf-8")
    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        stale_apply = asyncio.run(service.config_csv_patch(
            "Assets/Config/stale-token.csv", {"Id": "7"}, {"Name": "After"},
            dry_run=False, confirm_token=preview.data["confirmToken"],
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert not stale_apply.ok and stale_apply.error.code == "CSV_CONFIRM_TOKEN_INVALID"
    assert path.read_text(encoding="utf-8") == "Id,Name,Other\n7,Before,two\n"


def test_csv_confirm_token_binds_expected_values_and_logical_header_selection(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "token-request.csv"
    original = b"Id,Name,Other\n7,First,one\n"
    path.write_bytes(original)
    service = _Service(tmp_path)

    preview = asyncio.run(service.config_csv_patch(
        "Assets/Config/token-request.csv", {"Id": "7"}, {"Name": "Changed"},
        expected_values={"Other": "one"}, header_row_index=1, dry_run=True,
    ))
    assert preview.ok

    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        missing_expected = asyncio.run(service.config_csv_patch(
            "Assets/Config/token-request.csv", {"Id": "7"}, {"Name": "Changed"},
            header_row_index=1, dry_run=False, confirm_token=preview.data["confirmToken"],
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    header_path = config / "header-token-request.csv"
    header_original = b"Id,Name\n7,First\nName,Id\nSecond,7\n"
    header_path.write_bytes(header_original)
    header_preview = asyncio.run(service.config_csv_patch(
        "Assets/Config/header-token-request.csv", {"Id": "7"}, {"Name": "Changed"},
        header_row_index=1, dry_run=True,
    ))
    assert header_preview.ok

    previous = CONFIG.write_access_approved
    object.__setattr__(CONFIG, "write_access_approved", True)
    try:
        different_header = asyncio.run(service.config_csv_patch(
            "Assets/Config/header-token-request.csv", {"Id": "7"}, {"Name": "Changed"},
            header_row_index=3, dry_run=False, confirm_token=header_preview.data["confirmToken"],
        ))
    finally:
        object.__setattr__(CONFIG, "write_access_approved", previous)

    assert not missing_expected.ok and missing_expected.error.code == "CSV_CONFIRM_TOKEN_INVALID"
    assert not different_header.ok and different_header.error.code == "CSV_CONFIRM_TOKEN_INVALID"
    assert path.read_bytes() == original
    assert header_path.read_bytes() == header_original


def test_csv_patch_preview_reports_physical_location_without_using_it_as_apply_identity(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "preview-location.csv"
    original = b"Id,Name\r\n7,Before\r\n"
    path.write_bytes(original)
    service = _Service(tmp_path)

    preview = asyncio.run(service.config_csv_patch(
        "Assets/Config/preview-location.csv", {"Id": "7"}, {"Name": "After"}, dry_run=True,
    ))

    assert preview.ok
    assert preview.data["writeAttempted"] is False
    assert preview.data["headerLocation"] == {
        "recordIndex": 1, "physicalStartLine": 1, "physicalEndLine": 1,
        "characterStartOffset": 0, "characterEndOffsetExclusive": 7,
    }
    assert preview.data["rowLocation"] == {
        "recordIndex": 2, "physicalStartLine": 2, "physicalEndLine": 2,
        "characterStartOffset": 9, "characterEndOffsetExclusive": 17,
    }
    assert path.read_bytes() == original


def test_csv_gb18030_offsets_are_decoded_character_positions_not_byte_positions(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "gb18030-offsets.csv"
    text = 'Id,Text\r7,"𠀀中"\n'
    raw = text.encode("gb18030")
    path.write_bytes(raw)
    service = _Service(tmp_path)

    result = asyncio.run(service.config_csv_get(
        "Assets/Config/gb18030-offsets.csv", {"Id": "7"}, fields=["Text"],
    ))

    assert result.ok
    assert result.data["encoding"] == "gb18030"
    assert result.data["rows"] == [{"Text": "𠀀中"}]
    assert result.data["rowLocations"] == [{
        "recordIndex": 2, "physicalStartLine": 2, "physicalEndLine": 2,
        "characterStartOffset": 8, "characterEndOffsetExclusive": len(text) - 1,
    }]
    assert result.data["rowLocations"][0]["characterEndOffsetExclusive"] != len(raw) - 1


def test_csv_invalid_direct_shapes_never_write_or_coerce_dry_run(tmp_path: Path) -> None:
    config = tmp_path / "Assets" / "Config"
    config.mkdir(parents=True)
    path = config / "invalid-shapes.csv"
    original = b"Id,Name\n7,Before\n"
    path.write_bytes(original)
    service = _Service(tmp_path)

    invalid_read = asyncio.run(service.config_csv_get(
        "Assets/Config/invalid-shapes.csv", {1: "7"}, header_row_index=True,
    ))
    invalid_patch = asyncio.run(service.config_csv_patch(
        "Assets/Config/invalid-shapes.csv", {"Id": "7"}, {"Name": "After"},
        dry_run="false",  # type: ignore[arg-type]
    ))

    assert not invalid_read.ok and invalid_read.error.code == "CSV_READ_FAILED"
    assert invalid_read.error.detail["writeAttempted"] is False
    assert not invalid_patch.ok and invalid_patch.error.code == "CSV_PATCH_INVALID"
    assert invalid_patch.error.detail["writeAttempted"] is False
    assert path.read_bytes() == original


def test_project_stack_and_script_analysis_are_explicitly_heuristic(tmp_path: Path) -> None:
    (tmp_path / "ProjectSettings").mkdir()
    (tmp_path / "ProjectSettings" / "ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f2\n", encoding="utf-8")
    (tmp_path / "Packages").mkdir()
    (tmp_path / "Packages" / "manifest.json").write_text('{"dependencies":{"com.unity.test-framework":"1.1.0"}}', encoding="utf-8")
    scripts = tmp_path / "Assets" / "Scripts"
    scripts.mkdir(parents=True)
    (scripts / "Foo.cs").write_text("public class Foo { private Bar bar; public void Run() {} }", encoding="utf-8")
    (scripts / "Bar.cs").write_text("public class Bar {}", encoding="utf-8")
    service = _Service(tmp_path)

    analyzed = asyncio.run(service.script_analyze("Foo"))
    graph = asyncio.run(service.script_dependency_graph(["Assets/Scripts/Foo.cs"]))
    stack = asyncio.run(service.project_stack_detect())

    assert analyzed.ok and analyzed.data["confidence"] == "heuristic"
    assert graph.ok and graph.data["confidence"] == "heuristic"
    assert graph.data["resolvedRoots"] == ["Foo"]
    assert graph.data["nodes"][0]["assembly"] == "Assembly-CSharp"
    assert any(edge["target"] == "Bar" for edge in graph.data["edges"])
    assert stack.ok and stack.data["unityVersion"] == "2022.3.62f2"
