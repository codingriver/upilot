from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class E2ESpec:
    spec_version: str
    name: str
    timeout_s: float
    defaults: dict[str, Any]
    setup: list[dict[str, Any]]
    steps: list[dict[str, Any]]
    teardown: list[dict[str, Any]]
    visual: dict[str, Any]


def load_spec_dict(raw: dict[str, Any], source_path: Path) -> E2ESpec:
    if not isinstance(raw, dict):
        raise ValueError("Spec root must be a mapping")
    ver = str(raw.get("specVersion") or raw.get("spec_version") or "1")
    name = str(raw.get("name") or source_path.stem)
    timeout_s = float(raw.get("timeoutS") or raw.get("timeout_s") or 300.0)
    defaults = raw.get("defaults") if isinstance(raw.get("defaults"), dict) else {}
    setup = _as_step_list(raw.get("setup"))
    steps = _as_step_list(raw.get("steps"))
    teardown = _as_step_list(raw.get("teardown"))
    visual = raw.get("visual") if isinstance(raw.get("visual"), dict) else {}
    if not steps and not setup:
        raise ValueError("Spec must define at least one step in setup or steps")
    return E2ESpec(
        spec_version=ver,
        name=name,
        timeout_s=timeout_s,
        defaults=defaults,
        setup=setup,
        steps=steps,
        teardown=teardown,
        visual=visual,
    )


def _as_step_list(v: Any) -> list[dict[str, Any]]:
    if v is None:
        return []
    if not isinstance(v, list):
        raise ValueError("setup/steps/teardown must be lists")
    out: list[dict[str, Any]] = []
    for i, item in enumerate(v):
        if not isinstance(item, dict):
            raise ValueError(f"Step {i} must be a mapping")
        out.append(item)
    return out


@dataclass
class StepRecord:
    phase: str
    index: int
    kind: str  # action | assert
    action: str | None
    ok: bool
    duration_ms: float
    detail: dict[str, Any] = field(default_factory=dict)
    error: str | None = None


@dataclass
class E2EReport:
    spec_name: str
    passed: bool
    started_at: str
    ended_at: str
    steps: list[StepRecord]
    failure: dict[str, Any] | None
    artifact_dir: str
    report_path: str
