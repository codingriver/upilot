from __future__ import annotations

import asyncio
from collections import Counter
import csv
import ctypes
import hashlib
import io
import json
import os
import re
import tempfile
import time
from pathlib import Path
from typing import Any

from ..config import CONFIG
from ..protocol import new_id, now_ms
from ..responses import fail, ok


_CS_TYPE_RE = re.compile(
    r"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)"
)
_CS_USING_RE = re.compile(r"^\s*using\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;", re.MULTILINE)
_CS_METHOD_RE = re.compile(
    r"\b(?:public|private|protected|internal|static|virtual|override|async|sealed|partial|new|extern|unsafe|\s)+"
    r"[A-Za-z_][A-Za-z0-9_<>,.\[\]?\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.MULTILINE,
)
_CS_IDENTIFIER_RE = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]*\b")


class ProjectAnalysisDomainService:
    async def navmesh_status(
        self,
        include_surfaces: bool = True,
        include_agents: bool = True,
        include_triangulation: bool = True,
    ):
        return await self.dispatcher.call(
            new_id("req"),
            "navmesh.status",
            {
                "includeSurfaces": include_surfaces,
                "includeAgents": include_agents,
                "includeTriangulation": include_triangulation,
            },
        )

    async def navmesh_sample(
        self,
        points: list[dict],
        max_distance: float = 10.0,
        area_mask: int = -1,
        agent_type_id: int = -1,
    ):
        return await self.dispatcher.call(
            new_id("req"),
            "navmesh.sample",
            {
                "points": points,
                "maxDistance": max(0.001, float(max_distance)),
                "areaMask": int(area_mask),
                "agentTypeId": int(agent_type_id),
            },
        )

    async def navmesh_triangulation_summary(self):
        return await self.dispatcher.call(new_id("req"), "navmesh.triangulationSummary", {})

    async def profiler_capture_start(
        self,
        duration_sec: float = 30.0,
        sample_every_frames: int = 1,
        title: str = "runtime-profiler",
        output_directory: str = "",
        marker_names: list[str] | None = None,
        marker_name_regex: str = "",
        max_markers: int = 64,
        telemetry_type_name: str = "",
        telemetry_method_name: str = "",
        baseline_json_path: str = "",
    ):
        return await self.dispatcher.call(
            new_id("req"),
            "profiler.capture.start",
            {
                "durationSec": max(1.0, min(float(duration_sec), 3600.0)),
                "sampleEveryFrames": max(1, min(int(sample_every_frames), 600)),
                "title": title,
                "outputDirectory": output_directory,
                "markerNames": marker_names or [],
                "markerNameRegex": marker_name_regex,
                "maxMarkers": max(0, min(int(max_markers), 256)),
                "telemetryTypeName": telemetry_type_name,
                "telemetryMethodName": telemetry_method_name,
                "baselineJsonPath": baseline_json_path,
            },
        )

    async def profiler_capture_status(self, capture_id: str = ""):
        return await self.dispatcher.call(new_id("req"), "profiler.capture.status", {"captureId": capture_id})

    async def profiler_capture_stop(self, capture_id: str = ""):
        return await self.dispatcher.call(new_id("req"), "profiler.capture.stop", {"captureId": capture_id})

    def _analysis_project_root(self) -> Path | None:
        session = self.server.session_manager.active
        if session and session.project_path:
            return Path(session.project_path).resolve()
        return None

    def _resolve_analysis_path(self, raw_path: str, *, must_exist: bool = True) -> Path | None:
        root = self._analysis_project_root()
        if root is None:
            return None
        candidate = Path(raw_path)
        if not candidate.is_absolute():
            candidate = root / candidate
        try:
            resolved = candidate.resolve()
            resolved.relative_to(root)
        except (OSError, ValueError):
            return None
        if must_exist and not resolved.exists():
            return None
        return resolved

    @staticmethod
    def _detect_text_encoding(data: bytes, requested: str = "auto") -> tuple[str, bytes, str]:
        requested_key = (requested or "auto").strip().lower().replace("_", "-")
        if requested_key not in {"", "auto"}:
            codec = "gb18030" if requested_key in {"gbk", "gb2312"} else requested_key
            return codec, b"", "explicit"
        if data.startswith(b"\xef\xbb\xbf"):
            return "utf-8", b"\xef\xbb\xbf", "bom"
        if data.startswith(b"\xff\xfe"):
            return "utf-16-le", b"\xff\xfe", "bom"
        if data.startswith(b"\xfe\xff"):
            return "utf-16-be", b"\xfe\xff", "bom"
        try:
            data.decode("utf-8", errors="strict")
            return "utf-8", b"", "strict-utf8"
        except UnicodeDecodeError:
            data.decode("gb18030", errors="strict")
            return "gb18030", b"", "strict-gb18030"

    @staticmethod
    def _logical_csv_records(text: str) -> list[tuple[int, int, str, str]]:
        records: list[tuple[int, int, str, str]] = []
        start = 0
        in_quotes = False
        index = 0
        while index < len(text):
            ch = text[index]
            if ch == '"':
                if in_quotes and index + 1 < len(text) and text[index + 1] == '"':
                    index += 2
                    continue
                in_quotes = not in_quotes
            if not in_quotes and ch in "\r\n":
                end = index
                if ch == "\r" and index + 1 < len(text) and text[index + 1] == "\n":
                    terminator = "\r\n"
                    index += 2
                else:
                    terminator = ch
                    index += 1
                records.append((start, index, text[start:end], terminator))
                start = index
                continue
            index += 1
        if start < len(text) or not records:
            records.append((start, len(text), text[start:], ""))
        return records

    @staticmethod
    def _parse_csv_record(record: str, delimiter: str) -> list[str]:
        reader = csv.reader(io.StringIO(record), delimiter=delimiter)
        return next(reader, [])

    @staticmethod
    def _normalize_csv_header_name(value: str) -> str:
        return str(value or "").strip().lstrip("#").strip()

    @staticmethod
    def _csv_field_spans(record: str, delimiter: str) -> list[tuple[int, int]]:
        spans: list[tuple[int, int]] = []
        start = 0
        in_quotes = False
        index = 0
        while index < len(record):
            ch = record[index]
            if ch == '"':
                if in_quotes and index + 1 < len(record) and record[index + 1] == '"':
                    index += 2
                    continue
                in_quotes = not in_quotes
            elif ch == delimiter and not in_quotes:
                spans.append((start, index))
                start = index + 1
            index += 1
        spans.append((start, len(record)))
        return spans

    @staticmethod
    def _encode_csv_field(value: Any, delimiter: str, original_token: str) -> str:
        text = str(value)
        preserve_quotes = len(original_token) >= 2 and original_token.startswith('"') and original_token.endswith('"')
        requires_quotes = any(char in text for char in (delimiter, '"', "\r", "\n"))
        if preserve_quotes or requires_quotes:
            return '"' + text.replace('"', '""') + '"'
        return text

    @staticmethod
    def _detect_delimiter(records: list[tuple[int, int, str, str]]) -> str:
        candidates = [",", "\t", ";", "|"]
        sample = "\n".join(record for _, _, record, _ in records[:5])
        try:
            return csv.Sniffer().sniff(sample, delimiters="".join(candidates)).delimiter
        except csv.Error:
            return max(candidates, key=lambda value: sample.count(value))

    @staticmethod
    def _csv_context(
        text: str,
        *,
        keys: dict[str, Any],
        fields: list[str],
        header_row_index: int,
    ) -> dict[str, Any]:
        records = ProjectAnalysisDomainService._logical_csv_records(text)
        delimiter = ProjectAnalysisDomainService._detect_delimiter(records)
        required = {str(key) for key in keys} | {str(field) for field in fields}
        header_index = header_row_index - 1 if header_row_index > 0 else -1
        header: list[str] = []
        if header_index >= 0:
            if header_index >= len(records):
                raise ValueError("headerRowIndex is outside the CSV record range")
            header = ProjectAnalysisDomainService._parse_csv_record(records[header_index][2], delimiter)
        else:
            for index, (_, _, record, _) in enumerate(records[:10]):
                values = ProjectAnalysisDomainService._parse_csv_record(record, delimiter)
                aliases = set(values) | {
                    ProjectAnalysisDomainService._normalize_csv_header_name(value)
                    for value in values
                }
                if required.issubset(aliases):
                    header_index = index
                    header = values
                    break
        if header_index < 0:
            raise ValueError(f"Could not find a header row containing fields: {sorted(required)}")
        indices: dict[str, int] = {}
        for index, name in enumerate(header):
            indices.setdefault(name, index)
            indices.setdefault(ProjectAnalysisDomainService._normalize_csv_header_name(name), index)
        missing = sorted(required - set(indices))
        if missing:
            raise ValueError(f"CSV header does not contain fields: {missing}")

        matches: list[dict[str, Any]] = []
        for index in range(header_index + 1, len(records)):
            start, end, record, terminator = records[index]
            if not record.strip():
                continue
            values = ProjectAnalysisDomainService._parse_csv_record(record, delimiter)
            if len(values) < len(header):
                values += [""] * (len(header) - len(values))
            if all(str(values[indices[key]]) == str(value) for key, value in keys.items()):
                matches.append(
                    {
                        "recordIndex": index + 1,
                        "start": start,
                        "end": end,
                        "content": record,
                        "terminator": terminator,
                        "values": values,
                    }
                )
        return {
            "records": records,
            "delimiter": delimiter,
            "headerIndex": header_index,
            "header": header,
            "indices": indices,
            "matches": matches,
        }

    async def config_csv_get(
        self,
        path: str,
        keys: dict[str, Any],
        fields: list[str] | None = None,
        header_row_index: int = 0,
        encoding: str = "auto",
    ):
        request_id = new_id("req")
        target = self._resolve_analysis_path(path)
        if target is None or not target.is_file():
            return fail(request_id, "CSV_PATH_INVALID", "CSV path must be an existing file under the Unity project.", {"path": path})
        try:
            raw = target.read_bytes()
            codec, bom, confidence = self._detect_text_encoding(raw, encoding)
            text = raw[len(bom):].decode(codec, errors="strict")
            requested_fields = fields or []
            context = self._csv_context(
                text,
                keys=keys or {},
                fields=requested_fields,
                header_row_index=header_row_index,
            )
        except (OSError, UnicodeError, csv.Error, ValueError) as ex:
            return fail(request_id, "CSV_READ_FAILED", str(ex), {"path": str(target)})
        rows = []
        for match in context["matches"]:
            values = match["values"]
            selected = requested_fields or context["header"]
            rows.append({name: values[context["indices"][name]] for name in selected})
        newline = "CRLF" if "\r\n" in text else ("LF" if "\n" in text else ("CR" if "\r" in text else "none"))
        return ok(
            request_id,
            {
                "path": str(target),
                "encoding": codec,
                "encodingConfidence": confidence,
                "bom": bool(bom),
                "newline": newline,
                "delimiter": context["delimiter"],
                "headerRowIndex": context["headerIndex"] + 1,
                "columnCount": len(context["header"]),
                "matchCount": len(rows),
                "unique": len(rows) == 1,
                "rows": rows,
                "sha256": hashlib.sha256(raw).hexdigest(),
            },
        )

    async def config_csv_patch(
        self,
        path: str,
        keys: dict[str, Any],
        changes: dict[str, Any],
        expected_values: dict[str, Any] | None = None,
        header_row_index: int = 0,
        encoding: str = "auto",
        dry_run: bool = True,
        confirm_token: str = "",
    ):
        request_id = new_id("req")
        if not dry_run and not CONFIG.write_access_approved:
            return fail(request_id, "WRITE_ACCESS_NOT_APPROVED", "CSV patch apply requires project write access.", {"path": path})
        target = self._resolve_analysis_path(path)
        if target is None or not target.is_file():
            return fail(request_id, "CSV_PATH_INVALID", "CSV path must be an existing file under the Unity project.", {"path": path})
        if not keys or not changes:
            return fail(request_id, "CSV_PATCH_INVALID", "keys and changes are required.", {"path": path})
        try:
            raw = target.read_bytes()
            before_hash = hashlib.sha256(raw).hexdigest()
            codec, bom, confidence = self._detect_text_encoding(raw, encoding)
            text = raw[len(bom):].decode(codec, errors="strict")
            context = self._csv_context(
                text,
                keys=keys,
                fields=list(changes) + list((expected_values or {}).keys()),
                header_row_index=header_row_index,
            )
            if len(context["matches"]) != 1:
                raise ValueError(f"CSV patch requires exactly one matching row; found {len(context['matches'])}")
            match = context["matches"][0]
            values = list(match["values"])
            for name, expected in (expected_values or {}).items():
                actual = values[context["indices"][name]]
                if str(actual) != str(expected):
                    raise ValueError(f"Expected {name}={expected!r}, found {actual!r}")
            before_values = {name: values[context["indices"][name]] for name in changes}
            for name, value in changes.items():
                values[context["indices"][name]] = str(value)
            spans = self._csv_field_spans(match["content"], context["delimiter"])
            if len(spans) != len(context["header"]):
                raise ValueError(
                    f"CSV field span count {len(spans)} does not match header column count {len(context['header'])}"
                )
            replacements: dict[int, str] = {}
            for name, value in changes.items():
                column_index = context["indices"][name]
                field_start, field_end = spans[column_index]
                original_token = match["content"][field_start:field_end]
                replacements[column_index] = self._encode_csv_field(
                    value,
                    context["delimiter"],
                    original_token,
                )
            pieces: list[str] = []
            cursor = 0
            for column_index in sorted(replacements):
                field_start, field_end = spans[column_index]
                pieces.append(match["content"][cursor:field_start])
                pieces.append(replacements[column_index])
                cursor = field_end
            pieces.append(match["content"][cursor:])
            replacement_record = "".join(pieces)
            replacement = replacement_record + match["terminator"]
            updated_text = text[: match["start"]] + replacement + text[match["end"] :]
            updated_raw = bom + updated_text.encode(codec, errors="strict")
            after_hash = hashlib.sha256(updated_raw).hexdigest()
            token_payload = json.dumps(
                {"path": str(target), "before": before_hash, "keys": keys, "changes": changes},
                ensure_ascii=False,
                sort_keys=True,
                separators=(",", ":"),
            ).encode("utf-8")
            expected_token = hashlib.sha256(token_payload).hexdigest()
            prefix_raw = bom + text[: match["start"]].encode(codec)
            suffix_raw = text[match["end"] :].encode(codec)
            outside_unchanged = (
                updated_raw.startswith(prefix_raw)
                and updated_raw.endswith(suffix_raw)
                and self._parse_csv_record(replacement_record, context["delimiter"]) == values
            )
            result = {
                "path": str(target),
                "dryRun": dry_run,
                "applied": False,
                "encoding": codec,
                "encodingConfidence": confidence,
                "delimiter": context["delimiter"],
                "headerRowIndex": context["headerIndex"] + 1,
                "recordIndex": match["recordIndex"],
                "columnCount": len(context["header"]),
                "unique": True,
                "beforeValues": before_values,
                "afterValues": {name: str(value) for name, value in changes.items()},
                "beforeSha256": before_hash,
                "afterSha256": after_hash,
                "outsideTargetBytesUnchanged": outside_unchanged,
                "changedFields": sorted(changes),
                "confirmToken": expected_token,
            }
            if dry_run:
                return ok(request_id, result)
            if confirm_token != expected_token:
                return fail(request_id, "CSV_CONFIRM_TOKEN_INVALID", "confirmToken does not match the current file and requested changes.", result)
            if not outside_unchanged:
                return fail(request_id, "CSV_BYTE_PRESERVATION_FAILED", "Bytes outside the target record would change.", result)
            target.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(delete=False, dir=str(target.parent), prefix=f".{target.name}.", suffix=".tmp") as tmp:
                tmp.write(updated_raw)
                temp_path = Path(tmp.name)
            os.replace(temp_path, target)
            result["applied"] = True
            return ok(request_id, result)
        except (OSError, UnicodeError, csv.Error, ValueError) as ex:
            return fail(request_id, "CSV_PATCH_FAILED", str(ex), {"path": str(target)})

    async def hang_status(self, sample_window_sec: float = 0.5):
        request_id = new_id("req")
        session = self.server.session_manager.active
        pid = int(session.process_id if session else 0) or int(self.server.state.editor.process_id or 0)
        if pid <= 0:
            return fail(request_id, "UNITY_PROCESS_UNKNOWN", "Unity processId is not available; reconnect a Bridge that reports processId.")
        editor = self.server.state.editor
        now = now_ms()
        cpu_percent = await asyncio.to_thread(self._sample_process_cpu_percent, pid, max(0.1, min(sample_window_sec, 3.0)))
        pump_age = max(0, now - int(editor.last_main_thread_pump_at or 0)) if editor.last_main_thread_pump_at else None
        heartbeat_age = max(0, now - int(session.last_heartbeat_at or 0)) if session else None
        main_thread_unresponsive = pump_age is None or pump_age > 10000
        return ok(
            request_id,
            {
                "processId": pid,
                "processCpuPercent": cpu_percent,
                "mainThreadHeartbeatAt": editor.last_main_thread_pump_at,
                "mainThreadHeartbeatAgeMs": pump_age,
                "networkHeartbeatAgeMs": heartbeat_age,
                "mainThreadUnresponsive": main_thread_unresponsive,
                "cpuBusy": cpu_percent is not None and cpu_percent >= 80.0,
                "suspectedBusyLoop": bool(main_thread_unresponsive and cpu_percent is not None and cpu_percent >= 80.0),
                "mainThreadQueueDepth": editor.main_thread_queue_depth,
                "lastDequeuedCommandId": editor.last_dequeued_command_id,
                "lastCompile": self._compile_diagnostics(),
                "nextAction": "Call unity_hang_capture before restarting Unity." if main_thread_unresponsive else "",
            },
        )

    @staticmethod
    def _sample_process_cpu_percent(pid: int, interval_sec: float) -> float | None:
        if os.name != "nt":
            return None
        kernel32 = ctypes.windll.kernel32
        process = kernel32.OpenProcess(0x0400, False, pid)
        if not process:
            return None
        try:
            def sample() -> int | None:
                creation = ctypes.c_ulonglong()
                exit_time = ctypes.c_ulonglong()
                kernel = ctypes.c_ulonglong()
                user = ctypes.c_ulonglong()
                if not kernel32.GetProcessTimes(
                    process,
                    ctypes.byref(creation),
                    ctypes.byref(exit_time),
                    ctypes.byref(kernel),
                    ctypes.byref(user),
                ):
                    return None
                return int(kernel.value + user.value)

            first = sample()
            wall_start = time.perf_counter()
            time.sleep(interval_sec)
            second = sample()
            wall = time.perf_counter() - wall_start
            if first is None or second is None or wall <= 0:
                return None
            cpu_seconds = (second - first) / 10_000_000
            return round(cpu_seconds / wall * 100.0, 2)
        finally:
            kernel32.CloseHandle(process)

    async def hang_capture(self, output_path: str = "", dump_type: str = "mini"):
        request_id = new_id("req")
        if os.name != "nt":
            return fail(request_id, "HANG_DUMP_UNSUPPORTED", "Non-terminating dump capture is currently supported on Windows only.")
        session = self.server.session_manager.active
        pid = int(session.process_id if session else 0) or int(self.server.state.editor.process_id or 0)
        if pid <= 0:
            return fail(request_id, "UNITY_PROCESS_UNKNOWN", "Unity processId is not available.")
        root = self._analysis_project_root()
        if root is None:
            return fail(request_id, "UNITY_PROJECT_UNKNOWN", "Unity project path is not available.")
        target = Path(output_path) if output_path else root / "Log" / "UPilotDiagnostics" / f"unity-{pid}-{now_ms()}.dmp"
        if not target.is_absolute():
            target = root / target
        try:
            target = target.resolve()
            target.relative_to(root)
        except (OSError, ValueError):
            return fail(request_id, "HANG_DUMP_PATH_INVALID", "Dump path must stay under the Unity project.", {"path": str(target)})
        target.parent.mkdir(parents=True, exist_ok=True)
        success, error_code = await asyncio.to_thread(self._write_windows_minidump, pid, target, dump_type)
        if not success:
            return fail(request_id, "HANG_DUMP_FAILED", f"MiniDumpWriteDump failed with Win32 error {error_code}.", {"path": str(target), "processId": pid})
        data = target.read_bytes()
        return ok(request_id, {"path": str(target), "processId": pid, "dumpType": dump_type, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "processTerminated": False})

    @staticmethod
    def _write_windows_minidump(pid: int, target: Path, dump_type: str) -> tuple[bool, int]:
        import msvcrt

        kernel32 = ctypes.windll.kernel32
        dbghelp = ctypes.windll.dbghelp
        process = kernel32.OpenProcess(0x0410, False, pid)
        if not process:
            return False, ctypes.get_last_error()
        dump_flags = 0x00000000 if dump_type.lower() == "mini" else 0x00000002
        try:
            with target.open("wb") as stream:
                file_handle = msvcrt.get_osfhandle(stream.fileno())
                succeeded = bool(dbghelp.MiniDumpWriteDump(process, pid, file_handle, dump_flags, None, None, None))
                return succeeded, 0 if succeeded else ctypes.get_last_error()
        finally:
            kernel32.CloseHandle(process)

    def _iter_csharp_files(self) -> list[Path]:
        root = self._analysis_project_root()
        if root is None:
            return []
        files: list[Path] = []
        for directory_name in ("Assets", "Packages"):
            directory = root / directory_name
            if directory.exists():
                files.extend(path for path in directory.rglob("*.cs") if "Library" not in path.parts)
        return files

    async def script_analyze(
        self,
        path_or_type: str,
        include_members: bool = True,
        include_usages: bool = True,
    ):
        request_id = new_id("req")
        files = self._iter_csharp_files()
        target = self._resolve_analysis_path(path_or_type) if path_or_type.lower().endswith(".cs") else None
        if target is None:
            for candidate in files:
                if candidate.stem == path_or_type:
                    target = candidate
                    break
        if target is None or not target.is_file():
            return fail(request_id, "SCRIPT_NOT_FOUND", f"Could not resolve C# script or type: {path_or_type}")
        text = target.read_text(encoding="utf-8-sig", errors="replace")
        types = sorted(set(_CS_TYPE_RE.findall(text)))
        usages: list[dict[str, str]] = []
        if include_usages and types:
            token_re = re.compile(r"\b(?:" + "|".join(re.escape(value) for value in types) + r")\b")
            for candidate in files:
                if candidate == target:
                    continue
                candidate_text = candidate.read_text(encoding="utf-8-sig", errors="replace")
                matched = sorted(set(token_re.findall(candidate_text)))
                if matched:
                    usages.append({"path": str(candidate), "symbols": matched, "resolution": "heuristic"})
        return ok(request_id, {"path": str(target), "types": types, "usings": sorted(set(_CS_USING_RE.findall(text))), "members": sorted(set(_CS_METHOD_RE.findall(text))) if include_members else [], "usages": usages, "resolution": "source-regex", "confidence": "heuristic"})

    async def script_dependency_graph(
        self,
        roots: list[str],
        direction: str = "outgoing",
        max_depth: int = 3,
        include_editor: bool = True,
    ):
        request_id = new_id("req")
        files = self._iter_csharp_files()
        index: dict[str, dict[str, Any]] = {}
        texts: dict[Path, str] = {}
        root = self._analysis_project_root()
        asmdef_roots: list[tuple[Path, str]] = []
        if root is not None:
            for asmdef_path in root.rglob("*.asmdef"):
                if "Library" in asmdef_path.parts:
                    continue
                try:
                    asmdef_data = json.loads(asmdef_path.read_text(encoding="utf-8-sig"))
                except (OSError, json.JSONDecodeError):
                    asmdef_data = {}
                asmdef_roots.append((asmdef_path.parent, str(asmdef_data.get("name") or asmdef_path.stem)))
            asmdef_roots.sort(key=lambda item: len(item[0].parts), reverse=True)

        def assembly_for(path: Path) -> str:
            for directory, assembly_name in asmdef_roots:
                try:
                    path.relative_to(directory)
                    return assembly_name
                except ValueError:
                    continue
            return "Assembly-CSharp-Editor" if "Editor" in path.parts else "Assembly-CSharp"

        for path in files:
            if not include_editor and "Editor" in path.parts:
                continue
            text = path.read_text(encoding="utf-8-sig", errors="replace")
            texts[path] = text
            for type_name in _CS_TYPE_RE.findall(text):
                index.setdefault(
                    type_name,
                    {
                        "type": type_name,
                        "path": str(path),
                        "file": str(path),
                        "assembly": assembly_for(path),
                        "editor": "Editor" in path.parts,
                    },
                )
        known_types = set(index)
        forward: dict[str, set[str]] = {type_name: set() for type_name in known_types}
        reverse: dict[str, set[str]] = {type_name: set() for type_name in known_types}
        for path, text in texts.items():
            declared = list(_CS_TYPE_RE.findall(text))
            if not declared:
                continue
            identifier_counts = Counter(_CS_IDENTIFIER_RE.findall(text))
            declaration_counts = Counter(declared)
            referenced = {
                type_name
                for type_name in (set(identifier_counts) & known_types)
                if identifier_counts.get(type_name, 0) > declaration_counts.get(type_name, 0)
            }
            for source_type in declared:
                if source_type not in index:
                    continue
                for target_type in referenced:
                    if target_type == source_type:
                        continue
                    forward[source_type].add(target_type)
                    reverse[target_type].add(source_type)
        resolved_roots: list[str] = []
        root_mappings: list[dict[str, Any]] = []
        missing: list[str] = []
        for requested_root in roots:
            resolved_types: list[str] = []
            if requested_root in index:
                resolved_types = [requested_root]
            else:
                path = self._resolve_analysis_path(requested_root) if requested_root.lower().endswith(".cs") else None
                if path is not None:
                    resolved_types = sorted(
                        type_name for type_name, node in index.items() if Path(node["path"]) == path
                    )
            if not resolved_types:
                missing.append(requested_root)
                continue
            root_mappings.append({"requested": requested_root, "types": resolved_types})
            for type_name in resolved_types:
                if type_name not in resolved_roots:
                    resolved_roots.append(type_name)
        queue: list[tuple[str, int]] = [(type_name, 0) for type_name in resolved_roots]
        visited: set[str] = set()
        edges: list[dict[str, Any]] = []
        while queue:
            current, depth = queue.pop(0)
            depth_limit = max(0, min(max_depth, 8))
            if current in visited or depth > depth_limit:
                continue
            visited.add(current)
            if depth >= depth_limit:
                continue
            related: list[tuple[str, str, str]] = []
            if direction in {"outgoing", "both"}:
                related.extend((current, candidate, "outgoing") for candidate in sorted(forward.get(current, set())))
            if direction in {"incoming", "both"}:
                related.extend((candidate, current, "incoming") for candidate in sorted(reverse.get(current, set())))
            for source, target, reference_direction in related:
                edge = {
                    "source": source,
                    "target": target,
                    "kind": "type-token-reference",
                    "referenceDirection": reference_direction,
                    "basis": "source-token",
                    "resolution": "heuristic",
                    "confidence": 0.6,
                }
                if edge not in edges:
                    edges.append(edge)
                adjacent = target if reference_direction == "outgoing" else source
                if depth < max_depth and adjacent not in visited:
                    queue.append((adjacent, depth + 1))
        nodes = [index[name] for name in sorted(visited | {edge["source"] for edge in edges} | {edge["target"] for edge in edges}) if name in index]
        return ok(request_id, {"roots": roots, "resolvedRoots": resolved_roots, "rootMappings": root_mappings, "missingRoots": missing, "direction": direction, "maxDepth": max_depth, "nodes": nodes, "edges": edges, "resolution": "source-regex", "confidence": "heuristic"})

    async def project_stack_detect(self):
        request_id = new_id("req")
        root = self._analysis_project_root()
        if root is None:
            return fail(request_id, "UNITY_PROJECT_UNKNOWN", "Unity project path is not available.")
        unity_version = ""
        version_path = root / "ProjectSettings" / "ProjectVersion.txt"
        if version_path.exists():
            match = re.search(r"m_EditorVersion:\s*(.+)", version_path.read_text(encoding="utf-8", errors="replace"))
            unity_version = match.group(1).strip() if match else ""
        packages: dict[str, Any] = {}
        manifest = root / "Packages" / "manifest.json"
        if manifest.exists():
            try:
                packages = json.loads(manifest.read_text(encoding="utf-8-sig")).get("dependencies", {})
            except (OSError, json.JSONDecodeError):
                packages = {}
        asmdefs = []
        for path in root.rglob("*.asmdef"):
            if "Library" in path.parts:
                continue
            try:
                data = json.loads(path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError):
                data = {}
            asmdefs.append({"path": str(path), "name": data.get("name", path.stem), "includePlatforms": data.get("includePlatforms", []), "references": data.get("references", [])})
        return ok(request_id, {"projectPath": str(root), "unityVersion": unity_version, "packages": packages, "asmdefs": asmdefs, "hasEditorCode": (root / "Assets" / "Editor").exists(), "testAssemblies": [item for item in asmdefs if "Test" in str(item.get("name")) or "TestAssemblies" in item.get("references", [])]})
