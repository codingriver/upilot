from __future__ import annotations

import asyncio
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
    assert preview.data["confirmToken"]
    assert preview.data["before"]["mipmapEnabled"] is True


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
