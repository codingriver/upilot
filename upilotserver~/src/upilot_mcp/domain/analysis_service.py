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
import shlex
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

from ..config import CONFIG
from ..protocol import new_id, now_ms
from ..responses import fail, ok


class CsvContextError(ValueError):
    def __init__(self, message: str, diagnostic: dict[str, Any] | None = None) -> None:
        super().__init__(message)
        self.diagnostic = diagnostic or {}


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

_MIB = 1024 * 1024
_GIB = 1024 * _MIB
_DEFAULT_DUMP_RESERVE_BYTES = 2 * _GIB
_DUMP_TYPES = {"mini", "heap", "full"}


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
        capture_mode: str = "lowOverhead",
        max_samples: int = 4096,
        include_default_ai_markers: bool = True,
    ):
        if capture_mode not in ("lowOverhead", "legacy"):
            return fail(new_id("req"), "INVALID_PAYLOAD", "captureMode must be lowOverhead or legacy.")
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
                "captureMode": capture_mode,
                "maxSamples": max(1, min(int(max_samples), 16384)),
                "includeDefaultAiMarkers": include_default_ai_markers,
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
        persisted = str(getattr(self.server.state, "project_path", "") or "").strip()
        if persisted:
            return Path(persisted).resolve()
        working_directory = Path.cwd()
        if (working_directory / "Assets").is_dir() and (working_directory / "ProjectSettings").is_dir():
            return working_directory.resolve()
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
            if data.startswith(b"\xef\xbb\xbf") and codec in {"utf-8", "utf-8-sig"}:
                return "utf-8", b"\xef\xbb\xbf", "explicit-bom"
            if data.startswith(b"\xff\xfe") and codec in {"utf-16", "utf-16-le"}:
                return "utf-16-le", b"\xff\xfe", "explicit-bom"
            if data.startswith(b"\xfe\xff") and codec in {"utf-16", "utf-16-be"}:
                return "utf-16-be", b"\xfe\xff", "explicit-bom"
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
    def _csv_record_location(text: str, start: int, record: str, record_index: int) -> dict[str, int]:
        """Expose decoded-text offsets and physical (not logical) line positions."""
        prefix = text[:start]
        start_line = 1 + sum(1 for index, char in enumerate(prefix) if char == "\n" or (char == "\r" and (index + 1 == len(prefix) or prefix[index + 1] != "\n")))
        line_breaks = sum(1 for index, char in enumerate(record) if char == "\n" or (char == "\r" and (index + 1 == len(record) or record[index + 1] != "\n")))
        return {
            "recordIndex": record_index,
            "physicalStartLine": start_line,
            "physicalEndLine": start_line + line_breaks,
            "characterStartOffset": start,
            "characterEndOffsetExclusive": start + len(record),
        }

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
        if header_row_index < 0:
            raise ValueError("headerRowIndex must be 0 (auto) or a positive 1-based logical record index")
        records = ProjectAnalysisDomainService._logical_csv_records(text)
        delimiter = ProjectAnalysisDomainService._detect_delimiter(records)
        required = {str(key) for key in keys} | {str(field) for field in fields}
        header_index = header_row_index - 1 if header_row_index > 0 else -1
        header: list[str] = []
        header_candidates: list[dict[str, Any]] = []
        if header_index >= 0:
            if header_index >= len(records):
                raise ValueError("headerRowIndex is outside the CSV record range")
            header = ProjectAnalysisDomainService._parse_csv_record(records[header_index][2], delimiter)
            aliases = set(header) | {ProjectAnalysisDomainService._normalize_csv_header_name(value) for value in header}
            location = ProjectAnalysisDomainService._csv_record_location(
                text, records[header_index][0], records[header_index][2], header_index + 1
            )
            header_candidates.append({
                "recordIndex": header_index + 1, "selection": "explicit",
                "matchedRequiredColumns": sorted(required & aliases), "satisfiesRequired": required.issubset(aliases),
                "missingFields": sorted(required - aliases),
                "location": location,
                **location,
            })
        else:
            for index, (_, _, record, _) in enumerate(records[:10]):
                values = ProjectAnalysisDomainService._parse_csv_record(record, delimiter)
                aliases = set(values) | {
                    ProjectAnalysisDomainService._normalize_csv_header_name(value)
                    for value in values
                }
                location = ProjectAnalysisDomainService._csv_record_location(
                    text, records[index][0], record, index + 1
                )
                header_candidates.append({
                    "recordIndex": index + 1, "selection": "auto",
                    "matchedRequiredColumns": sorted(required & aliases), "satisfiesRequired": required.issubset(aliases),
                    "missingFields": sorted(required - aliases),
                    "location": location,
                    **location,
                })
                # Keep the first matching logical record as the legacy default,
                # but finish this bounded scan so callers can see later candidates.
                if header_index < 0 and required.issubset(aliases):
                    header_index = index
                    header = values
        if header_index < 0:
            raise CsvContextError(
                f"Could not find a header row containing fields: {sorted(required)}",
                {
                    "reason": "HEADER_NOT_FOUND",
                    "requiredColumns": sorted(required),
                    "headerCandidates": header_candidates,
                    "writeAttempted": False,
                },
            )
        indices: dict[str, int] = {}
        header_names: dict[str, list[int]] = {}
        for index, name in enumerate(header):
            indices.setdefault(name, index)
            normalized = ProjectAnalysisDomainService._normalize_csv_header_name(name)
            indices.setdefault(normalized, index)
            header_names.setdefault(normalized, []).append(index)
        missing = sorted(required - set(indices))
        if missing:
            raise CsvContextError(
                f"CSV header does not contain fields: {missing}",
                {
                    "reason": "HEADER_NOT_FOUND",
                    "requiredColumns": sorted(required),
                    "headerCandidates": header_candidates,
                    "writeAttempted": False,
                },
            )

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
            "headerCandidates": header_candidates,
            "indices": indices,
            "ambiguousColumns": [
                {"name": name, "columnIndices": [value + 1 for value in positions]}
                for name, positions in header_names.items() if len(positions) > 1
            ],
            "ambiguousNames": {name for name, positions in header_names.items() if len(positions) > 1},
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
        if (
            not isinstance(path, str)
            or not isinstance(keys, dict)
            or any(not isinstance(name, str) or not name for name in keys)
            or (fields is not None and (
                not isinstance(fields, list)
                or any(not isinstance(field, str) or not field for field in fields)
            ))
            or not isinstance(header_row_index, int)
            or isinstance(header_row_index, bool)
            or header_row_index < 0
            or not isinstance(encoding, str)
        ):
            return fail(
                request_id,
                "CSV_READ_FAILED",
                "CSV path, keys, fields, headerRowIndex, and encoding have invalid types.",
                {"path": path if isinstance(path, str) else "", "writeAttempted": False},
            )
        target = self._resolve_analysis_path(path)
        if target is None or not target.is_file():
            return fail(request_id, "CSV_PATH_INVALID", "CSV path must be an existing file under the Unity project.", {"path": path, "writeAttempted": False})
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
            detail = {"path": str(target)}
            if isinstance(ex, CsvContextError):
                detail.update(ex.diagnostic)
            return fail(request_id, "CSV_READ_FAILED", str(ex), detail)
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
                "ambiguousColumns": context["ambiguousColumns"],
                "headerCandidates": context["headerCandidates"],
                "headerLocation": self._csv_record_location(
                    text, context["records"][context["headerIndex"]][0], context["records"][context["headerIndex"]][2], context["headerIndex"] + 1),
                "rowLocations": [
                    self._csv_record_location(text, match["start"], match["content"], match["recordIndex"])
                    for match in context["matches"]
                ],
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
        if (
            not isinstance(path, str)
            or not isinstance(keys, dict)
            or not isinstance(changes, dict)
            or (expected_values is not None and not isinstance(expected_values, dict))
            or any(not isinstance(name, str) or not name for name in keys)
            or any(not isinstance(name, str) or not name for name in changes)
            or any(not isinstance(name, str) or not name for name in (expected_values or {}))
            or not isinstance(header_row_index, int)
            or isinstance(header_row_index, bool)
            or header_row_index < 0
            or not isinstance(encoding, str)
            or not isinstance(dry_run, bool)
            or not isinstance(confirm_token, str)
        ):
            return fail(
                request_id,
                "CSV_PATCH_INVALID",
                "CSV patch arguments have invalid types.",
                {"path": path if isinstance(path, str) else "", "writeAttempted": False},
            )
        if not dry_run and not CONFIG.write_access_approved:
            return fail(request_id, "WRITE_ACCESS_NOT_APPROVED", "CSV patch apply requires project write access.", {"path": path, "writeAttempted": False})
        target = self._resolve_analysis_path(path)
        if target is None or not target.is_file():
            return fail(request_id, "CSV_PATH_INVALID", "CSV path must be an existing file under the Unity project.", {"path": path, "writeAttempted": False})
        if not keys or not changes:
            return fail(request_id, "CSV_PATCH_INVALID", "keys and changes are required.", {"path": path, "writeAttempted": False})
        write_attempted = False
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
            ambiguous = context["ambiguousNames"]
            targeted = set(changes) | set(expected_values or {}) | set(keys)
            conflicts = sorted(name for name in targeted if self._normalize_csv_header_name(name) in ambiguous)
            if conflicts:
                raise ValueError(f"CSV patch cannot target ambiguous columns: {conflicts}")
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
                {
                    "path": str(target),
                    "before": before_hash,
                    "keys": keys,
                    "changes": changes,
                    "expectedValues": expected_values or {},
                    "headerRowIndex": header_row_index,
                    "encoding": encoding,
                },
                ensure_ascii=False,
                sort_keys=True,
                separators=(",", ":"),
            ).encode("utf-8")
            expected_token = hashlib.sha256(token_payload).hexdigest()
            prefix_raw = bom + text[: match["start"]].encode(codec)
            suffix_raw = text[match["end"] :].encode(codec)
            header_location = self._csv_record_location(
                text,
                context["records"][context["headerIndex"]][0],
                context["records"][context["headerIndex"]][2],
                context["headerIndex"] + 1,
            )
            row_location = self._csv_record_location(
                text, match["start"], match["content"], match["recordIndex"]
            )
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
                "headerLocation": header_location,
                "recordIndex": match["recordIndex"],
                # A preview's physical position explains the selected logical
                # record; it is not accepted as an apply locator.  The token
                # remains bound to the original byte hash and request shape.
                "rowLocation": row_location,
                "columnCount": len(context["header"]),
                "unique": True,
                "beforeValues": before_values,
                "afterValues": {name: str(value) for name, value in changes.items()},
                "beforeSha256": before_hash,
                "afterSha256": after_hash,
                "outsideTargetBytesUnchanged": outside_unchanged,
                "changedFields": sorted(changes),
                "confirmToken": expected_token,
                "writeAttempted": False,
            }
            if dry_run:
                return ok(request_id, result)
            if confirm_token != expected_token:
                return fail(request_id, "CSV_CONFIRM_TOKEN_INVALID", "confirmToken does not match the current file and requested changes.", result)
            if not outside_unchanged:
                return fail(request_id, "CSV_BYTE_PRESERVATION_FAILED", "Bytes outside the target record would change.", result)
            target.parent.mkdir(parents=True, exist_ok=True)
            temp_path: Path | None = None
            try:
                with tempfile.NamedTemporaryFile(delete=False, dir=str(target.parent), prefix=f".{target.name}.", suffix=".tmp") as tmp:
                    tmp.write(updated_raw)
                    temp_path = Path(tmp.name)
                write_attempted = True
                os.replace(temp_path, target)
                temp_path = None
            finally:
                if temp_path is not None:
                    try:
                        temp_path.unlink(missing_ok=True)
                    except OSError:
                        pass
            result["applied"] = True
            result["writeAttempted"] = True
            return ok(request_id, result)
        except (OSError, UnicodeError, csv.Error, ValueError) as ex:
            detail = {"path": str(target), "writeAttempted": write_attempted}
            if isinstance(ex, CsvContextError):
                detail.update(ex.diagnostic)
            return fail(request_id, "CSV_PATCH_FAILED", str(ex), detail)

    async def hang_status(self, sample_window_sec: float = 0.5):
        request_id = new_id("req")
        session = self.server.session_manager.active
        pid, pid_diagnostics = await asyncio.to_thread(self._resolve_live_unity_pid)
        if pid <= 0:
            return fail(request_id, "UNITY_PROCESS_UNKNOWN", "A live Unity processId matching the connected project was not found.", pid_diagnostics)
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
                **pid_diagnostics,
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

    async def hang_capture(
        self,
        output_path: str = "",
        dump_type: str = "mini",
        reserve_bytes: int = _DEFAULT_DUMP_RESERVE_BYTES,
    ):
        request_id = new_id("req")
        if not self._windows_dump_supported():
            return fail(request_id, "HANG_DUMP_UNSUPPORTED", "Non-terminating dump capture is currently supported on Windows only.")
        normalized_dump_type = str(dump_type or "").strip().lower()
        if normalized_dump_type not in _DUMP_TYPES:
            return fail(
                request_id,
                "HANG_DUMP_TYPE_INVALID",
                "dumpType must be mini, heap or full.",
                {
                    "dumpType": dump_type,
                    "allowedDumpTypes": sorted(_DUMP_TYPES),
                    "dumpAttempted": False,
                    "processTerminated": False,
                },
            )
        try:
            requested_reserve = int(reserve_bytes)
        except (TypeError, ValueError, OverflowError):
            requested_reserve = -1
        if requested_reserve < 0:
            return fail(
                request_id,
                "HANG_DUMP_RESERVE_INVALID",
                "reserveBytes must be a non-negative integer.",
                {
                    "reserveBytes": reserve_bytes,
                    "minimumReserveBytes": _DEFAULT_DUMP_RESERVE_BYTES,
                    "dumpAttempted": False,
                    "processTerminated": False,
                },
            )
        pid, pid_diagnostics = await asyncio.to_thread(self._resolve_live_unity_pid)
        if pid <= 0:
            return fail(request_id, "UNITY_PROCESS_UNKNOWN", "A live Unity processId matching the connected project was not found.", pid_diagnostics)
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

        effective_reserve = max(requested_reserve, _DEFAULT_DUMP_RESERVE_BYTES)
        memory = await asyncio.to_thread(
            self._process_memory_usage,
            pid,
            pid_diagnostics["processCreatedAt"],
        )
        preflight = {
            "path": str(target),
            "processId": pid,
            **pid_diagnostics,
            "dumpType": normalized_dump_type,
            "requestedReserveBytes": requested_reserve,
            "reserveBytes": effective_reserve,
            "minimumReserveBytes": _DEFAULT_DUMP_RESERVE_BYTES,
            "dumpAttempted": False,
            "processTerminated": False,
        }
        memory_details = {key: value for key, value in memory.items() if key != "ok"}
        if not memory.get("ok"):
            preflight.update(memory_details)
            preflight["preflightPassed"] = False
            preflight["nextAction"] = "Restore permission to inspect the exact Unity process, then retry without changing the target PID."
            return fail(
                request_id,
                "HANG_DUMP_PREFLIGHT_FAILED",
                "Could not read the verified Unity process memory counters before dump capture.",
                preflight,
            )
        try:
            estimate = self._estimate_dump_bytes(normalized_dump_type, memory)
        except (TypeError, ValueError, OverflowError) as ex:
            preflight.update(memory_details)
            preflight.update({
                "preflightPassed": False,
                "memoryProbeError": str(ex),
                "nextAction": "Retry after the verified Unity process exposes usable memory counters.",
            })
            return fail(
                request_id,
                "HANG_DUMP_PREFLIGHT_FAILED",
                "The verified Unity process did not provide usable memory counters for dump-size estimation.",
                preflight,
            )
        preflight.update(memory_details)
        preflight.update(estimate)
        volume_path = self._existing_ancestor(target.parent)
        try:
            space = await asyncio.to_thread(self._volume_space, volume_path)
        except OSError as ex:
            preflight.update({
                "preflightPassed": False,
                "volumePath": str(volume_path),
                "spaceProbeError": str(ex),
                "nextAction": "Restore access to the target volume and retry the dump preflight.",
            })
            return fail(
                request_id,
                "HANG_DUMP_PREFLIGHT_FAILED",
                "Could not read free space for the target dump volume.",
                preflight,
            )
        required_free = int(estimate["expectedBytes"]) + effective_reserve
        preflight.update(space)
        preflight.update({
            "volumePath": str(volume_path),
            "requiredFreeBytes": required_free,
            "preflightPassed": int(space["freeBytes"]) >= required_free,
        })
        if not preflight["preflightPassed"]:
            preflight["nextAction"] = (
                "Free space on the target volume, choose a smaller dumpType, or select another project-local output path."
            )
            return fail(
                request_id,
                "HANG_DUMP_INSUFFICIENT_SPACE",
                "The target volume does not have enough free space for the estimated dump and safety reserve.",
                preflight,
            )

        target.parent.mkdir(parents=True, exist_ok=True)
        preflight["dumpAttempted"] = True
        success, error_code = await asyncio.to_thread(
            self._write_windows_minidump,
            pid,
            target,
            normalized_dump_type,
            pid_diagnostics["processCreatedAt"],
        )
        if not success:
            partial = self._partial_dump_metadata(target)
            return fail(
                request_id,
                "HANG_DUMP_FAILED",
                f"MiniDumpWriteDump failed with Win32 error {error_code}.",
                {
                    **preflight,
                    **partial,
                    "win32Error": error_code,
                    "nextAction": "Preserve the partial-file metadata, inspect permissions and free space, then choose one bounded retry.",
                },
            )
        try:
            actual_bytes = target.stat().st_size
            sha256 = self._file_sha256(target)
            after_space = await asyncio.to_thread(self._volume_space, self._existing_ancestor(target.parent))
        except OSError as ex:
            partial = self._partial_dump_metadata(target)
            return fail(
                request_id,
                "HANG_DUMP_ARTIFACT_READ_FAILED",
                f"The dump was written but its final artifact metadata could not be read: {ex}",
                {
                    **preflight,
                    **partial,
                    "nextAction": "Preserve the dump and inspect it directly before any cleanup or retry.",
                },
            )
        result = {
            **preflight,
            "bytes": actual_bytes,
            "sha256": sha256,
            "freeBytesAfter": after_space["freeBytes"],
            "reserveMaintained": int(after_space["freeBytes"]) >= effective_reserve,
            "estimateExceeded": actual_bytes > int(estimate["expectedBytes"]),
        }
        if not result["reserveMaintained"]:
            result["nextAction"] = "Preserve the completed dump, free target-volume space, and do not start another capture."
            return fail(
                request_id,
                "HANG_DUMP_RESERVE_BREACHED",
                "The dump completed, but the configured post-capture safety reserve was not maintained.",
                result,
            )
        return ok(request_id, result)

    @staticmethod
    def _windows_dump_supported() -> bool:
        return os.name == "nt"

    @staticmethod
    def _existing_ancestor(path: Path) -> Path:
        candidate = path
        while not candidate.exists() and candidate.parent != candidate:
            candidate = candidate.parent
        return candidate

    @staticmethod
    def _volume_space(path: Path) -> dict[str, int]:
        usage = shutil.disk_usage(path)
        return {
            "volumeTotalBytes": int(usage.total),
            "volumeUsedBytes": int(usage.used),
            "freeBytes": int(usage.free),
        }

    @staticmethod
    def _estimate_dump_bytes(dump_type: str, memory: dict[str, Any]) -> dict[str, Any]:
        working_set = max(0, int(memory.get("workingSetBytes") or 0))
        private = max(0, int(memory.get("privateBytes") or 0))
        if working_set <= 0 and private <= 0:
            raise ValueError("Process memory counters did not provide a usable dump-size basis.")
        if dump_type == "mini":
            minimum = 16 * _MIB
            maximum = max(128 * _MIB, min(512 * _MIB, max(working_set, private) // 8 + 64 * _MIB))
            basis = "bounded normal minidump estimate"
        elif dump_type == "heap":
            minimum = max(128 * _MIB, private // 2)
            maximum = max(minimum, private + private // 4 + 256 * _MIB)
            basis = "private bytes plus 25 percent and 256 MiB overhead"
        else:
            minimum = max(256 * _MIB, working_set, private)
            combined = working_set + private
            maximum = max(minimum, combined + combined // 4 + 512 * _MIB)
            basis = "private bytes plus working set, 25 percent and 512 MiB overhead"
        return {
            "expectedBytesMin": minimum,
            "expectedBytesMax": maximum,
            "expectedBytes": maximum,
            "estimateBasis": basis,
        }

    @staticmethod
    def _process_memory_usage(pid: int, expected_created_at: int) -> dict[str, Any]:
        if os.name != "nt" or pid <= 0:
            return {"ok": False, "memoryProbeError": "unsupported", "memoryProbeWin32Error": 0}

        class ProcessMemoryCountersEx(ctypes.Structure):
            _fields_ = [
                ("cb", ctypes.c_ulong),
                ("PageFaultCount", ctypes.c_ulong),
                ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t),
            ]

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
        process = kernel32.OpenProcess(0x0410, False, pid)
        if not process:
            error = ctypes.get_last_error()
            return {"ok": False, "memoryProbeError": "OpenProcess failed", "memoryProbeWin32Error": error}
        try:
            if not expected_created_at or ProjectAnalysisDomainService._process_creation_time(pid, process) != expected_created_at:
                return {"ok": False, "memoryProbeError": "process identity changed", "memoryProbeWin32Error": 6}
            counters = ProcessMemoryCountersEx()
            counters.cb = ctypes.sizeof(counters)
            if not psapi.GetProcessMemoryInfo(process, ctypes.byref(counters), counters.cb):
                error = ctypes.get_last_error()
                return {"ok": False, "memoryProbeError": "GetProcessMemoryInfo failed", "memoryProbeWin32Error": error}
            return {
                "ok": True,
                "workingSetBytes": int(counters.WorkingSetSize),
                "peakWorkingSetBytes": int(counters.PeakWorkingSetSize),
                "privateBytes": int(counters.PrivateUsage),
                "pagefileBytes": int(counters.PagefileUsage),
                "peakPagefileBytes": int(counters.PeakPagefileUsage),
            }
        finally:
            kernel32.CloseHandle(process)

    @staticmethod
    def _file_sha256(path: Path) -> str:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()

    @classmethod
    def _partial_dump_metadata(cls, path: Path) -> dict[str, Any]:
        try:
            size = path.stat().st_size
        except OSError:
            return {"partialFileExists": False, "partialBytes": 0, "partialSha256": "", "partialHashDeferred": False}
        result = {
            "partialFileExists": True,
            "partialBytes": size,
            "partialSha256": "",
            "partialHashDeferred": size > 64 * _MIB,
        }
        if size <= 64 * _MIB:
            try:
                result["partialSha256"] = cls._file_sha256(path)
            except OSError:
                pass
        return result

    def _resolve_live_unity_pid(self) -> tuple[int, dict[str, Any]]:
        session = self.server.session_manager.active
        root = self._analysis_project_root()
        query_result = self._query_unity_processes()
        if isinstance(query_result, tuple):
            rows, query_diagnostics = query_result
        else:
            # Preserve compatibility with focused tests and embedders that replace the
            # process query with a simple list-returning probe.
            rows = query_result
            query_diagnostics = {
                "processQuerySucceeded": True,
                "processQueryError": "",
                "processQueryExitCode": 0,
            }
        classified = [self._classify_unity_process(row, root) for row in rows]
        matches = [row for row, details in zip(rows, classified) if details["eligibleMainEditor"]]
        candidates = [
            ("session", int(session.process_id if session else 0)),
            ("editorState", int(self.server.state.editor.process_id or 0)),
        ]
        valid_rows = {int(row.get("ProcessId") or 0): row for row in matches if int(row.get("ProcessId") or 0) > 0}
        valid_ids = set(valid_rows)
        stale = list(dict.fromkeys(pid for _, pid in candidates if pid > 0 and pid not in valid_ids))
        discovered = next(iter(valid_ids)) if len(valid_ids) == 1 else 0
        creation = self._process_creation_time(discovered) if discovered else 0
        discovered_creation = int(valid_rows[discovered].get("ProcessCreatedAt") or 0) if discovered else 0
        # CIM dates have microsecond precision; kernel FILETIME has 100 ns precision.
        same_process = bool(creation and discovered_creation and creation // 10 == discovered_creation // 10)
        candidate_details = classified[:32]
        common = {
            **query_diagnostics,
            "sessionProcessId": int(session.process_id if session else 0),
            "cachedProcessId": int(self.server.state.editor.process_id or 0),
            "resolvedProcessId": discovered if same_process else 0,
            "candidateCount": len(classified),
            "candidateTruncated": len(classified) > len(candidate_details),
            "candidateProcessIds": sorted(valid_ids),
            "excludedProcessIds": sorted(
                int(item["processId"])
                for item in classified
                if int(item["processId"]) > 0 and not item["eligibleMainEditor"]
            ),
            "candidates": candidate_details,
        }
        if discovered > 0 and same_process:
            source = next((source for source, pid in candidates if pid == discovered), "processDiscovery")
            if session is not None:
                session.process_id = discovered
            self.server.state.editor.process_id = discovered
            return discovered, {
                **common,
                "pidSource": source,
                "pidRefreshed": source != "session",
                "pidWasRefreshed": source != "session",
                "staleProcessIds": stale,
                "projectIdentityVerified": True,
                "processCreatedAt": creation,
                "processRole": "editor",
            }
        if not query_diagnostics.get("processQuerySucceeded"):
            reason = "process_query_failed"
        elif len(valid_ids) > 1:
            reason = "ambiguous"
        elif len(valid_ids) == 1:
            reason = "identity_unverified"
        else:
            reason = "main_editor_not_found"
        return 0, {
            **common,
            "pidSource": "unavailable",
            "pidRefreshed": False,
            "pidWasRefreshed": False,
            "staleProcessIds": stale,
            "projectPath": str(root or ""),
            "projectIdentityVerified": False,
            "reason": reason,
            "nextAction": "Reconnect the project Bridge or restart the managed MCP Server, then retry hang diagnostics.",
        }

    @staticmethod
    def _process_creation_time(pid: int, handle=None) -> int:
        if os.name != "nt" or pid <= 0:
            return 0
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel.GetProcessTimes.argtypes = [ctypes.c_void_p, *([ctypes.POINTER(ctypes.c_ulonglong)] * 4)]
        owned = handle is None
        handle = kernel.OpenProcess(0x1000, False, pid) if owned else handle
        if not handle:
            return 0
        try:
            times = [ctypes.c_ulonglong() for _ in range(4)]
            return times[0].value if kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)) else 0
        finally:
            if owned:
                kernel.CloseHandle(handle)

    @staticmethod
    def _command_line_args(command_line: str) -> list[str]:
        if os.name != "nt":
            return shlex.split(command_line)
        shell = ctypes.WinDLL("shell32", use_last_error=True)
        shell.CommandLineToArgvW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_int)]
        shell.CommandLineToArgvW.restype = ctypes.POINTER(ctypes.c_wchar_p)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.LocalFree.argtypes = [ctypes.c_void_p]
        count = ctypes.c_int()
        arguments = shell.CommandLineToArgvW(command_line, ctypes.byref(count))
        if not arguments:
            return []
        try:
            return [arguments[i] for i in range(count.value)]
        finally:
            kernel.LocalFree(arguments)

    @classmethod
    def _matches_editor_process(cls, row: dict, project_root: Path | None) -> bool:
        return bool(cls._classify_unity_process(row, project_root)["eligibleMainEditor"])

    @classmethod
    def _classify_unity_process(cls, row: dict, project_root: Path | None) -> dict[str, Any]:
        process_id = int(row.get("ProcessId") or 0)
        executable_path = str(row.get("ExecutablePath") or "")
        command_line = str(row.get("CommandLine") or "")
        details: dict[str, Any] = {
            "processId": process_id,
            "executablePath": executable_path,
            "processCreatedAt": int(row.get("ProcessCreatedAt") or 0),
            "processRole": "unknown",
            "projectPath": "",
            "projectPathMatches": False,
            "executableVerified": False,
            "eligibleMainEditor": False,
            "exclusionReasons": [],
        }
        reasons: list[str] = details["exclusionReasons"]
        if process_id <= 0:
            reasons.append("invalid_process_id")
        if not executable_path or Path(executable_path).name.lower() != "unity.exe":
            reasons.append("executable_not_unity")
        else:
            details["executableVerified"] = True
        if not command_line:
            reasons.append("command_line_unavailable")
            return details
        try:
            args = cls._command_line_args(command_line)
            lowered = [arg.lower() for arg in args]
            asset_import_worker = any("assetimportworker" in arg for arg in lowered)
            worker_switch = any(
                arg in ("-adb2", "-batchmode", "/batchmode", "-ump") or arg.startswith("-worker")
                for arg in lowered
            )
            if asset_import_worker:
                details["processRole"] = "assetImportWorker"
                reasons.append("worker_role")
            elif worker_switch:
                details["processRole"] = "worker"
                reasons.append("worker_role")
            else:
                details["processRole"] = "editor"
            indexes = [i for i, arg in enumerate(lowered) if arg == "-projectpath"]
            if len(indexes) != 1 or indexes[0] + 1 >= len(args):
                reasons.append("project_path_missing_or_ambiguous")
                return details
            actual = Path(args[indexes[0] + 1]).resolve()
            details["projectPath"] = str(actual)
            if project_root is None:
                reasons.append("project_path_unavailable")
            elif os.path.normcase(str(actual)) == os.path.normcase(str(project_root.resolve())):
                details["projectPathMatches"] = True
            else:
                reasons.append("project_path_mismatch")
        except (OSError, ValueError) as ex:
            reasons.append("command_line_parse_failed")
            details["parseError"] = str(ex)
        details["eligibleMainEditor"] = not reasons
        return details

    @staticmethod
    def _process_exists(pid: int) -> bool:
        if pid <= 0:
            return False
        if os.name == "nt":
            handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
            if not handle:
                return False
            ctypes.windll.kernel32.CloseHandle(handle)
            return True
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False

    @staticmethod
    def _query_unity_processes() -> tuple[list[dict], dict[str, Any]]:
        if os.name != "nt":
            return [], {
                "processQuerySucceeded": False,
                "processQueryError": "unsupported_platform",
                "processQueryExitCode": None,
            }
        command = (
            "@(Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | "
            "Select-Object ProcessId,CommandLine,ExecutablePath,"
            "@{Name='ProcessCreatedAt';Expression={$_.CreationDate.ToFileTimeUtc()}}) | ConvertTo-Json -Compress"
        )
        try:
            completed = subprocess.run(
                ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
            if completed.returncode != 0:
                return [], {
                    "processQuerySucceeded": False,
                    "processQueryError": (completed.stderr or "process query failed").strip()[:2048],
                    "processQueryExitCode": completed.returncode,
                }
            if not completed.stdout.strip():
                return [], {
                    "processQuerySucceeded": True,
                    "processQueryError": "",
                    "processQueryExitCode": 0,
                }
            parsed = json.loads(completed.stdout)
            rows = parsed if isinstance(parsed, list) else [parsed]
            return [row for row in rows if isinstance(row, dict)], {
                "processQuerySucceeded": True,
                "processQueryError": "",
                "processQueryExitCode": 0,
            }
        except subprocess.TimeoutExpired:
            return [], {
                "processQuerySucceeded": False,
                "processQueryError": "process_query_timeout",
                "processQueryExitCode": None,
            }
        except json.JSONDecodeError as ex:
            return [], {
                "processQuerySucceeded": False,
                "processQueryError": f"invalid_process_query_json: {ex}",
                "processQueryExitCode": None,
            }
        except (OSError, ValueError, subprocess.SubprocessError) as ex:
            return [], {
                "processQuerySucceeded": False,
                "processQueryError": str(ex),
                "processQueryExitCode": None,
            }

    @staticmethod
    def _write_windows_minidump(pid: int, target: Path, dump_type: str, expected_created_at: int) -> tuple[bool, int]:
        import msvcrt

        dump_flags = {
            "mini": 0x00000000,
            "heap": 0x00000200 | 0x00000001 | 0x00000004 | 0x00000100,
            "full": 0x00000002 | 0x00000004 | 0x00000020 | 0x00000100,
        }.get(dump_type.lower())
        if dump_flags is None:
            return False, 87

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        dbghelp = ctypes.WinDLL("dbghelp", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        dbghelp.MiniDumpWriteDump.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_void_p,
                                            ctypes.c_ulong, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
        process = kernel32.OpenProcess(0x0410, False, pid)
        if not process:
            return False, ctypes.get_last_error()
        try:
            if not expected_created_at or ProjectAnalysisDomainService._process_creation_time(pid, process) != expected_created_at:
                return False, 6
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
