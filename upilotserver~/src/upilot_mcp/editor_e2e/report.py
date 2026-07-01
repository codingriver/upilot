from __future__ import annotations

import json
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .models import E2EReport, StepRecord


def iso_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def step_record_to_dict(s: StepRecord) -> dict[str, Any]:
    d = asdict(s)
    return d


def write_report_json(path: Path, report: E2EReport) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "specName": report.spec_name,
        "passed": report.passed,
        "startedAt": report.started_at,
        "endedAt": report.ended_at,
        "artifactDir": report.artifact_dir,
        "reportPath": report.report_path,
        "steps": [step_record_to_dict(s) for s in report.steps],
        "failure": report.failure,
    }
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def write_manifest(path: Path, files: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"files": files}, ensure_ascii=False, indent=2), encoding="utf-8")
