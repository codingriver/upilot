#!/usr/bin/env python3
"""Bounded release gate. Does not run Unity, modify versions, commit, tag or publish."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "upilotserver~" / "src"))
from upilot_mcp.source_identity import source_identity
from upilot_mcp.release_evidence import verify_release_evidence

CONTRACT_TESTS = (
    "test_acceptance_compact_shader.py", "test_test_selectors.py",
    "test_prefab_patch_and_scene_summary.py", "test_persistent_test_jobs.py",
    "test_operation_runner_and_agent_rules.py", "test_remaining_upilot_todos.py",
    "test_release_quality.py",
    "test_tool_proxy_parity.py",
    "test_write_batch_changes.py", "test_editor_execution_state_v2.py",
    "test_resource_write_contracts.py", "test_operation_cleanup_barriers.py", "test_release_evidence.py",
    "test_release_evidence_collector.py", "test_release_version_sync.py",
    "test_actions_runtime_contracts.py", "test_snapshot_short_tasks.py",
    "test_skill_validation.py", "test_install_upilot.py",
)

_DOC_CHECK_IDS = ("docs.registry", "docs.todo-id", "docs.archive", "docs.evidence", "docs.install")
_ROOT_TODO = "TODO_UPilot.mcd"
_P2_PLAN = Path("Documentation~") / "P2-Development-Plan-20260915.md"
_ARCHIVE = Path("Documentation~") / "TODO_UPilot_Integrated.md"
_TOOL_STATUS = Path("Documentation~") / "ToolStatus.md"
_RULES_SOURCE = Path("upilotserver~") / "src" / "upilot_mcp" / "domain" / "task_service.py"
_TEMPLATE_MANIFEST = Path("skills") / "upilot-unity-mcp" / "template-manifest.json"
_REGISTRY_SOURCE = Path("upilotserver~") / "src" / "upilot_mcp" / "tool_registry.py"
_FORMAL_NON_UNITY_TOOLS = frozenset({
    "csharp_eval", "csharp_object_dump", "csharp_validate", "execution_session", "reflection_emit_type",
})


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _relative_path(repo: Path, path: Path) -> str:
    try:
        return path.resolve().relative_to(repo.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def _documentation_result(
    check_id: str,
    status: str,
    *,
    severity: str = "info",
    source_path: str = "",
    stable_id: str = "",
    expected: object = None,
    actual: object = None,
    provenance: str = "repository",
    next_action: str = "",
    **details: object,
) -> dict:
    """Use one bounded schema for static-documentation diagnostics."""
    return {
        "checkId": check_id,
        "status": status,
        "severity": severity,
        "sourcePath": source_path,
        "stableId": stable_id,
        "expected": expected,
        "actual": actual,
        "provenance": provenance,
        "nextAction": next_action,
        **details,
    }


def _snapshot_paths(repo: Path, installed_manifests: list[Path]) -> dict[str, dict]:
    paths = [repo / _ROOT_TODO, repo / _P2_PLAN, repo / _ARCHIVE, repo / _TOOL_STATUS, repo / _RULES_SOURCE, repo / _REGISTRY_SOURCE]
    # Evidence is an explicit input set as well.  Without hashing the manifests
    # themselves, a concurrent replacement could leave a check reporting a
    # result for evidence it never actually inspected.
    paths.extend(sorted((repo / "Documentation~").glob("*Evidence*.json")))
    paths.extend(installed_manifests)
    # Artifact claims are input too.  A check that reads an artifact while it is
    # being replaced must not report a final, stable documentation result merely
    # because the evidence manifest itself did not change.
    for evidence_path in sorted((repo / "Documentation~").glob("*Evidence*.json")):
        try:
            payload = json.loads(evidence_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        for claim in _iter_artifact_claims(payload):
            claimed_path = claim.get("path") if isinstance(claim, dict) else None
            if not isinstance(claimed_path, str):
                continue
            candidate = (repo / claimed_path).resolve() if not Path(claimed_path).is_absolute() else Path(claimed_path).resolve()
            if _is_within(candidate, repo):
                paths.append(candidate)
    snapshot = {}
    for path in dict.fromkeys(paths):
        key = _relative_path(repo, path)
        try:
            data = path.read_bytes()
        except OSError as exc:
            snapshot[key] = {"available": False, "error": type(exc).__name__}
        else:
            snapshot[key] = {"available": True, "bytes": len(data), "sha256": _sha256(data)}
    # A managed install manifest names a bounded, explicit Skill root.  Its
    # declared hash is not enough for INPUT_CHANGED: the copied content can
    # change while the metadata remains untouched.  Snapshot the same content
    # boundary used by the installer, without following anything outside it.
    for manifest_path in installed_manifests:
        if manifest_path.name.casefold() != ".upilot-install.json":
            continue
        key = _relative_path(repo, manifest_path.parent) + "/<installed-skill-content>"
        try:
            snapshot[key] = {"available": True, "sha256": _installed_skill_content_hash(manifest_path.parent)}
        except OSError as exc:
            snapshot[key] = {"available": False, "error": type(exc).__name__}
    return snapshot


def _markdown_link_errors(repo: Path, document: Path, text: str) -> list[str]:
    """Validate local Markdown links without treating historical prose as an API contract."""
    errors = []
    anchors = set(re.findall(r'<a id="([^"]+)"></a>', text))
    for target, fragment in re.findall(r"\]\(([^)#]*)(?:#([^)]+))?\)", text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        target_path = (document.parent / target).resolve() if target else document.resolve()
        if not _is_within(target_path, repo):
            errors.append(f"OUTSIDE_REPOSITORY:{target or '#'}")
            continue
        if not target_path.is_file():
            errors.append(f"MISSING_TARGET:{target or document.name}")
            continue
        if fragment:
            target_text = target_path.read_text(encoding="utf-8")
            if target_path == document.resolve():
                target_anchors = anchors
            else:
                target_anchors = set(re.findall(r'<a id="([^"]+)"></a>', target_text))
            if fragment not in target_anchors:
                errors.append(f"MISSING_ANCHOR:{target or document.name}#{fragment}")
    return errors


def _declared_ids(text: str) -> list[str]:
    """Only headings declare a stable TODO/archive id; ordinary evidence may cite it repeatedly."""
    return re.findall(r"(?m)^#{2,6}\s+(?:\[[^\]]+\]\s*)?(UP-[A-Za-z0-9][A-Za-z0-9._-]*|P2-WP-\d{2})", text)


def _root_authoritative_ids(text: str) -> list[str]:
    """Include IDs in the root's status table, but never ordinary prose citations."""
    return _declared_ids(text) + re.findall(
        r"(?m)^\|\s*(UP-[A-Za-z0-9][A-Za-z0-9._-]*)\s*\|",
        text,
    )


def _archive_duplicate_declarations(text: str) -> list[dict[str, str]]:
    """Find only duplicate archive declarations with the same provenance.

    An archive can legitimately cite a stable ID many times.  It becomes a
    conflict only when it declares that ID twice for the same recorded source
    and evidence hash.  Missing provenance is not silently collapsed: it is
    returned as an unknown identity so the caller can report it without
    inventing a relationship between historical records.
    """
    headings = list(re.finditer(
        r"(?m)^#{2,6}\s+(?:\[[^\]]+\]\s*)?(UP-[A-Za-z0-9][A-Za-z0-9._-]*|P2-WP-\d{2})[^\n]*$",
        text,
    ))
    seen: set[tuple[str, str, str]] = set()
    duplicates = []
    for index, heading in enumerate(headings):
        section = text[heading.end():headings[index + 1].start() if index + 1 < len(headings) else len(text)]
        stable_id = heading.group(1)
        source_match = re.search(r'(?im)^\s*[-*]?\s*(?:source(?:Identity|Sha256)?|来源)\s*[:：]\s*([^\n]+)', section)
        hash_match = re.search(r'(?i)\b(?:evidence)?sha256\s*[:=]\s*([0-9a-f]{64})\b', section)
        source = source_match.group(1).strip() if source_match else ""
        evidence_hash = hash_match.group(1).lower() if hash_match else ""
        if not source or not evidence_hash:
            continue
        identity = (stable_id, source, evidence_hash)
        if identity in seen:
            duplicates.append({"stableId": stable_id, "source": source, "evidenceSha256": evidence_hash})
        seen.add(identity)
    return duplicates


def _iter_artifact_claims(value: object, *, source_context: bool = False):
    if isinstance(value, dict):
        # Yield malformed claims too, so the checker can issue a bounded
        # diagnostic instead of silently downgrading corrupt evidence to an
        # unavailable artifact.
        if not source_context and isinstance(value.get("path"), str) and "sha256" in value:
            yield value
        for key, child in value.items():
            yield from _iter_artifact_claims(
                child,
                source_context=source_context or key in {"source", "sourceIdentity", "sourceIdentityAfter", "sourceFiles"},
            )
    elif isinstance(value, list):
        for child in value:
            yield from _iter_artifact_claims(child, source_context=source_context)


def _current_rules_version(repo: Path) -> str:
    try:
        manifest = json.loads((repo / _TEMPLATE_MANIFEST).read_text(encoding="utf-8"))
        value = manifest.get("agentRulesVersion")
    except (OSError, ValueError):
        return ""
    return str(value) if type(value) is int and value > 0 else ""


def _current_registry_version(repo: Path) -> str:
    try:
        text = (repo / _REGISTRY_SOURCE).read_text(encoding="utf-8")
    except OSError:
        return ""
    match = re.search(r"REGISTRY_VERSION\s*=\s*(\d+)", text)
    return match.group(1) if match else ""


def _current_skill_template_version(repo: Path) -> str:
    try:
        manifest = json.loads((repo / _TEMPLATE_MANIFEST).read_text(encoding="utf-8"))
        value = manifest.get("skillPackVersion")
    except (OSError, ValueError):
        return ""
    return str(value) if type(value) is int and value > 0 else ""


def _installed_skill_content_hash(root: Path) -> str:
    """Match the installer hash without loading code from an installed Skill."""
    digest = hashlib.sha256()
    for path in sorted((item for item in root.rglob("*") if item.is_file()),
                       key=lambda item: item.relative_to(root).as_posix().casefold()):
        relative = path.relative_to(root)
        if path.name.casefold() == ".upilot-install.json" or any(
            part.casefold() == "__pycache__" or part.casefold().endswith((".meta", ".pyc", ".pyo"))
            for part in relative.parts
        ):
            continue
        digest.update(relative.as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def _evidence_provenance_state(value: object) -> str:
    """Classify recorded historical source provenance without comparing HEAD.

    Historical evidence is allowed to describe an older source identity.  A
    missing or malformed identity is therefore unknown, not a claim that the
    historical result belongs to current source.
    """
    found = False
    malformed = False

    def visit(item: object) -> None:
        nonlocal found, malformed
        if isinstance(item, dict):
            for key in ("sourceIdentity", "sourceIdentityAfter"):
                if key not in item:
                    continue
                identity = item[key]
                if isinstance(identity, dict) and identity:
                    found = True
                elif isinstance(identity, list) and identity and all(isinstance(entry, dict) and entry for entry in identity):
                    found = True
                else:
                    malformed = True
            source_hash = item.get("sourceSha256")
            if source_hash is not None:
                if isinstance(source_hash, str) and re.fullmatch(r"[0-9a-fA-F]{64}", source_hash):
                    found = True
                elif (
                    isinstance(source_hash, dict)
                    and source_hash
                    and all(
                        isinstance(path, str)
                        and isinstance(recorded_hash, str)
                        and re.fullmatch(r"[0-9a-fA-F]{64}", recorded_hash)
                        for path, recorded_hash in source_hash.items()
                    )
                ):
                    # Older evidence records a per-source-file hash map rather
                    # than one aggregate source hash.  It is still historical
                    # provenance and must not be mistaken for malformed data.
                    found = True
                else:
                    malformed = True
            source = item.get("source")
            if isinstance(source, dict) and source and (
                isinstance(source.get("headCommit"), str)
                or isinstance(source.get("files"), list)
            ):
                found = True
            for child in item.values():
                visit(child)
        elif isinstance(item, list):
            for child in item:
                visit(child)

    visit(value)
    return "present" if found else ("malformed" if malformed else "missing")


def documentation_checks(
    repo: Path,
    *,
    installed_manifests: list[Path] | None = None,
    inventory_factory=None,
    include_registry: bool = True,
    include_todo_id: bool = True,
) -> dict:
    """Run bounded, read-only WP-12 checks over repository-owned documentation.

    Installed manifests are explicit user-provided inputs. They are never followed to
    write into client directories, and their presence cannot prove client tool injection.
    """
    # The caller may list the same manifest twice or pass paths in a different
    # order.  Normalize the explicit read-only input set before both snapshot
    # and report generation so equivalent invocations are deterministic.
    manifests = sorted(
        {path.resolve() for path in (installed_manifests or [])},
        key=lambda path: str(path).casefold(),
    )
    before = _snapshot_paths(repo, manifests)
    checks = []

    if include_registry:
        try:
            inventory = inventory_factory() if inventory_factory else tool_inventory()
            tools = inventory.get("tools", [])
            names = [tool.get("name") for tool in tools if isinstance(tool, dict)]
            invalid = [
                name for name in names
                if not isinstance(name, str)
                or not (re.fullmatch(r"unity_[a-z0-9_]+", name) or name in _FORMAL_NON_UNITY_TOOLS)
            ]
            generated = json.dumps(inventory, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
            registry_version = _current_registry_version(repo)
            tool_status_path = repo / _TOOL_STATUS
            tool_status_text = tool_status_path.read_text(encoding="utf-8")
            documented_versions = re.findall(r"(?i)Registry\s+v(\d+)", tool_status_text)
            current_status_version = documented_versions[0] if documented_versions else ""
            if invalid or len(names) != len(set(names)) or inventory.get("proxyHandlerGaps") or not registry_version or current_status_version != registry_version:
                checks.append(_documentation_result(
                    "docs.registry", "failed", severity="error", source_path=_TOOL_STATUS.as_posix(),
                    expected={"registryVersion": registry_version, "uniqueRegisteredPublicTools": True, "proxyHandlers": "complete"},
                    actual={
                        "documentedRegistryVersion": current_status_version or None,
                        "invalid": invalid,
                        "proxyHandlerGaps": inventory.get("proxyHandlerGaps", []),
                    },
                    next_action=("Review the formal tool names in check_release_quality.py and tool_registry.py; "
                                 "update ToolStatus.md only when its documented Registry version differs."),
                ))
            else:
                checks.append(_documentation_result(
                    "docs.registry", "passed", source_path=_REGISTRY_SOURCE.as_posix(), expected="deterministic Registry snapshot",
                    actual={"registeredCount": len(names), "snapshotSha256": _sha256(generated)}, generatedDiffCount=0,
                ))
        except Exception as exc:
            checks.append(_documentation_result(
                "docs.registry", "failed", severity="error", source_path=_REGISTRY_SOURCE.as_posix(),
                expected="readable Registry snapshot", actual=type(exc).__name__, next_action=str(exc),
            ))

    if include_todo_id:
        todo_path = repo / _ROOT_TODO
        plan_path = repo / _P2_PLAN
        try:
            todo_text = todo_path.read_text(encoding="utf-8")
            plan_text = plan_path.read_text(encoding="utf-8")
            declared = _root_authoritative_ids(todo_text)
            duplicates = sorted({item for item in declared if declared.count(item) > 1})
            link_errors = _markdown_link_errors(repo, plan_path, plan_text)
            authority = "当前开发与验收状态仍以 [根 TODO]" in plan_text
            if duplicates or link_errors or not authority:
                checks.append(_documentation_result(
                    "docs.todo-id", "failed", severity="error", source_path=_ROOT_TODO,
                    expected="root TODO is sole current-status authority with resolvable local links",
                    actual={"duplicateStableIds": duplicates, "linkErrors": link_errors, "authorityStatement": authority},
                    next_action="Keep current status in TODO_UPilot.mcd and repair the reported stable ID or local link.",
                ))
            else:
                checks.append(_documentation_result(
                    "docs.todo-id", "passed", source_path=_ROOT_TODO, expected="unique declared TODO IDs",
                    actual={"declaredStableIdCount": len(declared), "planAnchors": len(re.findall(r'<a id="P2-WP-\d{2}"></a>', plan_text))},
                ))
        except OSError as exc:
            checks.append(_documentation_result(
                "docs.todo-id", "failed", severity="error", source_path=_ROOT_TODO,
                expected="readable root TODO and P2 plan", actual=type(exc).__name__, next_action=str(exc),
            ))

    archive_path = repo / _ARCHIVE
    try:
        archive_text = archive_path.read_text(encoding="utf-8")
        archive_ids = _declared_ids(archive_text)
        archive_duplicates = _archive_duplicate_declarations(archive_text)
        if archive_duplicates:
            checks.append(_documentation_result(
                "docs.archive", "failed", severity="error", source_path=_ARCHIVE.as_posix(),
                expected="one archive declaration per stable ID, source identity, and evidence hash", actual=archive_duplicates,
                next_action="Keep citations, but merge only declarations with the same stable ID, provenance, and evidence hash.",
            ))
        else:
            checks.append(_documentation_result(
                "docs.archive", "passed", source_path=_ARCHIVE.as_posix(), expected="deduplicated archive declarations",
                actual={"declaredStableIdCount": len(archive_ids)},
            ))
    except OSError as exc:
        checks.append(_documentation_result(
            "docs.archive", "unknown", severity="warning", source_path=_ARCHIVE.as_posix(),
            expected="readable optional archive", actual=type(exc).__name__, provenance="historical",
            next_action="Provide a readable archive before claiming archive consistency.",
        ))

    evidence_errors = []
    evidence_unknown = []
    evidence_checked = 0
    for path in sorted((repo / "Documentation~").glob("*Evidence*.json")):
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            evidence_errors.append(f"{_relative_path(repo, path)}:{type(exc).__name__}")
            continue
        evidence_checked += 1
        provenance_state = _evidence_provenance_state(payload)
        if provenance_state != "present":
            evidence_unknown.append(
                f"{_relative_path(repo, path)}:{'MISSING_SOURCE_IDENTITY' if provenance_state == 'missing' else 'INVALID_SOURCE_IDENTITY'}"
            )
        for claim in _iter_artifact_claims(payload):
            if not isinstance(claim.get("path"), str) or not isinstance(claim.get("sha256"), str):
                evidence_errors.append(f"{_relative_path(repo, path)}:INVALID_ARTIFACT_CLAIM")
                continue
            claimed_path = Path(claim["path"])
            candidate = (repo / claimed_path).resolve() if not claimed_path.is_absolute() else claimed_path.resolve()
            if not _is_within(candidate, repo):
                evidence_unknown.append(f"{_relative_path(repo, path)}:{claim['path']}")
                continue
            if "Library" in candidate.parts:
                evidence_unknown.append(f"{_relative_path(repo, path)}:{claim['path']}:VOLATILE_RUNTIME_PATH")
                continue
            try:
                data = candidate.read_bytes()
            except OSError:
                evidence_unknown.append(f"{_relative_path(repo, path)}:{claim['path']}")
                continue
            expected_hash = claim["sha256"].lower()
            expected_bytes = claim.get("bytes")
            if not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
                evidence_errors.append(f"{_relative_path(repo, path)}:{claim['path']}:INVALID_SHA256")
            elif expected_hash != _sha256(data) or (isinstance(expected_bytes, int) and expected_bytes != len(data)):
                evidence_errors.append(f"{_relative_path(repo, path)}:{claim['path']}:HASH_OR_SIZE_MISMATCH")
    if evidence_errors:
        checks.append(_documentation_result(
            "docs.evidence", "failed", severity="error", source_path="Documentation~/*Evidence*.json",
            expected="readable historical evidence with matching local artifact claims", actual=evidence_errors,
            provenance="historical", next_action="Preserve the original evidence and investigate the mismatched artifact; do not rewrite its hash.",
        ))
    elif evidence_unknown:
        checks.append(_documentation_result(
            "docs.evidence", "unknown", severity="warning", source_path="Documentation~/*Evidence*.json",
            expected="verifiable local artifact claim", actual={"evidenceFiles": evidence_checked, "unavailableClaims": evidence_unknown[:64]},
            provenance="historical", next_action="Retain unavailable historical evidence as unknown; its source identity is not required to match HEAD.",
        ))
    else:
        checks.append(_documentation_result(
            "docs.evidence", "passed", source_path="Documentation~/*Evidence*.json",
            expected="valid JSON and matching local artifact claims", actual={"evidenceFiles": evidence_checked}, provenance="historical",
        ))

    rules_version = _current_rules_version(repo)
    skill_template_version = _current_skill_template_version(repo)
    install_results = []
    for manifest_path in manifests:
        item = {"path": _relative_path(repo, manifest_path), "clientInjectionState": "unknown"}
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if not isinstance(manifest, dict):
                raise ValueError("manifest root is not an object")
        except (OSError, ValueError) as exc:
            item.update({"status": "failed", "reason": type(exc).__name__})
            install_results.append(item)
            continue
        metadata_install = "templateVersion" in manifest or "contentSha256" in manifest
        if metadata_install:
            recorded_template = manifest.get("templateVersion")
            recorded_hash = manifest.get("contentSha256")
            target = manifest_path.parent
            item.update({
                "installPath": _relative_path(repo, target),
                "templateVersion": recorded_template,
                "contentSha256": recorded_hash,
                "sourceSha256": None,
                "sourceProvenance": "unknown",
                "controlledBoundary": "installed Skill root",
            })
            if type(recorded_template) is not int or not isinstance(recorded_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", recorded_hash):
                item.update({"status": "failed", "reason": "INVALID_INSTALLED_SKILL_MANIFEST"})
            else:
                try:
                    actual_hash = _installed_skill_content_hash(target)
                except OSError as exc:
                    item.update({"status": "failed", "reason": type(exc).__name__})
                else:
                    item["actualContentSha256"] = actual_hash
                    if recorded_hash.casefold() != actual_hash:
                        item.update({"status": "failed", "reason": "INSTALLED_SKILL_HASH_MISMATCH"})
                    elif not skill_template_version or str(recorded_template) != skill_template_version:
                        item.update({"status": "unknown", "reason": "INSTALLED_SKILL_TEMPLATE_VERSION_LAG"})
                    else:
                        # Legacy installation metadata proves only the copied
                        # files' declared integrity. It has no package/rules
                        # provenance and cannot establish client injection.
                        item.update({"status": "unknown", "reason": "INSTALLED_SKILL_SOURCE_PROVENANCE_UNKNOWN"})
            install_results.append(item)
            continue
        package_id = manifest.get("packageId", manifest.get("package", manifest.get("packageName")))
        manifest_rules = str(manifest.get("rulesVersion", ""))
        source_hash = manifest.get("sourceSha256", manifest.get("sourceHash"))
        external_value = manifest.get("externalSkill", manifest.get("externalSkillMapping"))
        external_declared = external_value is not None
        # An explicitly declared-but-empty external Skill is not proof that it
        # has no provenance; it is an incomplete external input and remains
        # unknown.  Do not collapse it into the no-external-Skill case.
        external = external_value if isinstance(external_value, dict) else {}
        external_source = external.get("source", external.get("package", external.get("name")))
        external_version = external.get("version", external.get("packageVersion"))
        declared_tools = external.get("tools", external.get("declaredTools", []))
        mappings = external.get("mappings", manifest.get("toolMappings", {}))
        if not isinstance(declared_tools, list):
            declared_tools = []
        if not isinstance(mappings, dict):
            mappings = {}
        mapping_results = []
        current_names = {tool.get("name") for tool in inventory.get("tools", []) if isinstance(tool, dict)}
        for external_name in declared_tools:
            if not isinstance(external_name, str):
                continue
            mapped_name = mappings.get(external_name)
            if external_name in current_names:
                mapping_state = "current"
            elif isinstance(mapped_name, str) and mapped_name in current_names:
                mapping_state = "mapped"
            else:
                mapping_state = "unsupported"
            mapping_results.append({
                "externalTool": external_name,
                "upilotTool": mapped_name if isinstance(mapped_name, str) else None,
                "mapping": mapping_state,
            })
        external_unknown = external_declared and (not isinstance(external_source, str) or not external_source.strip())
        item.update({
            "status": "passed" if package_id == "io.github.codingriver.upilot" and manifest_rules == rules_version and not external_unknown else "unknown",
            "packageId": package_id,
            "rulesVersion": manifest_rules,
            "sourceSha256": source_hash,
            "mapping": manifest.get("mapping", "unsupported"),
            "externalSkill": {
                "source": external_source if isinstance(external_source, str) and external_source.strip() else None,
                "version": external_version if isinstance(external_version, str) else None,
                "toolMappings": mapping_results,
            } if external_declared else None,
        })
        if external_unknown:
            item["reason"] = "MISSING_EXTERNAL_SKILL_SOURCE"
        elif item["status"] != "passed":
            item["reason"] = "PACKAGE_OR_RULES_VERSION_UNVERIFIED"
        install_results.append(item)
    if not manifests:
        checks.append(_documentation_result(
            "docs.install", "unknown", severity="warning", source_path="", expected="explicit read-only installed manifest",
            actual={"installedManifestCount": 0, "clientInjectionState": "unknown"}, provenance="external",
            next_action="Supply --installed-manifest for a read-only provenance comparison; refresh remains an external client action.",
        ))
    elif any(item["status"] == "failed" for item in install_results):
        checks.append(_documentation_result(
            "docs.install", "failed", severity="error", source_path="--installed-manifest", expected="readable JSON manifest",
            actual=install_results, provenance="external", next_action="Correct the explicit manifest input; this checker will not alter installed Skills.",
        ))
    elif any(item["status"] != "passed" for item in install_results):
        checks.append(_documentation_result(
            "docs.install", "unknown", severity="warning", source_path="--installed-manifest", expected={"packageId": "io.github.codingriver.upilot", "rulesVersion": rules_version},
            actual=install_results, provenance="external", next_action="Refresh or map the installed client explicitly; registered tools do not prove injection.",
        ))
    else:
        checks.append(_documentation_result(
            "docs.install", "passed", source_path="--installed-manifest", expected={"packageId": "io.github.codingriver.upilot", "rulesVersion": rules_version},
            actual=install_results, provenance="external", next_action="Client injection remains unknown until a real client call succeeds.",
        ))

    after = _snapshot_paths(repo, manifests)
    if before != after:
        for check in checks:
            # A stable result is meaningful only for the exact input snapshot
            # inspected by this invocation.  Retain already discovered errors,
            # but never leave another check marked passed after a concurrent
            # replacement.  This is especially important for registry and
            # artifact reads, whose own manifest may have remained unchanged.
            if check["status"] == "passed":
                check.update({
                    "status": "failed", "severity": "error", "expected": before, "actual": after,
                    "nextAction": "Rerun after all documentation and Registry inputs are stable.", "reason": "INPUT_CHANGED",
                })
            else:
                # Preserve a concrete failure (for example, a hash mismatch)
                # while making its concurrent-input qualification explicit.
                check["reason"] = "INPUT_CHANGED"
    errors = [check["checkId"] for check in checks if check["status"] == "failed" and check["severity"] == "error"]
    warnings = [check["checkId"] for check in checks if check["severity"] == "warning"]
    return {
        "checks": checks,
        "inputSnapshot": before,
        "inputUnchanged": before == after,
        "errorCheckIds": errors,
        "warningCheckIds": warnings,
    }


def _atomic_write_bytes(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False) as temporary:
            temporary_path = Path(temporary.name)
            temporary.write(content)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_path, path)
    except Exception:
        if temporary_path is not None:
            try:
                temporary_path.unlink(missing_ok=True)
            except OSError:
                pass
        raise


def verify_unity_summary(report: dict, source: dict) -> None:
    for key in ("acceptancePassed", "cleanupVerified", "testIdentityVerified", "sourceUnchanged"):
        if report.get(key) is not True:
            raise ValueError("Unity summary has missing or unsuccessful evidence: " + key)
    if report.get("sourceIdentity") != source or report.get("sourceIdentityAfter") != source:
        raise ValueError("Unity summary belongs to a different source identity.")
    if not report.get("runGuid") or not report.get("endedAt"):
        raise ValueError("Unity summary lacks run identity or completion time.")


def tool_inventory() -> dict:
    from upilot_mcp.mcp_stdio_server import mcp
    from upilot_mcp.tool_facade import McpToolFacade
    from upilot_mcp.tool_registry import REGISTRY, REGISTRY_VERSION, proxy_argument_schema

    exposed = {tool.name: tool for tool in asyncio.run(mcp.list_tools())}
    descriptors = REGISTRY.list()
    unregistered = set(exposed) - {item.name for item in descriptors}
    if unregistered:
        raise ValueError("MCP tools missing registry metadata: " + ", ".join(sorted(unregistered)))
    entries = []
    for item in descriptors:
        method = getattr(McpToolFacade, item.facade_method, None)
        if not callable(method):
            raise ValueError("Registry tool missing proxy handler: " + item.name)
        tool = exposed.get(item.name)
        if item.feature == "core" and tool is None:
            raise ValueError("Core registry tool missing MCP schema: " + item.name)
        entries.append({
            **item.to_dict(), "exposedInCurrentConfiguration": tool is not None,
            "proxyHandlerAvailable": callable(method),
            "proxyArguments": proxy_argument_schema(method, item.name) if callable(method) else None,
            "inputSchema": tool.inputSchema if tool else None,
        })
    return {"registryVersion": REGISTRY_VERSION, "registeredCount": len(entries),
            "exposedCount": len(exposed),
            "proxyHandlerGaps": [item["name"] for item in entries if not item["proxyHandlerAvailable"]],
            "tools": entries}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=REPO / "artifacts" / "reliability-quality")
    parser.add_argument("--unity-summary", type=Path)
    parser.add_argument("--require-unity-summary", action="store_true")
    parser.add_argument("--release-tag", default="")
    documentation_mode = parser.add_mutually_exclusive_group()
    documentation_mode.add_argument("--docs-only", action="store_true", help="Run only read-only documentation checks.")
    documentation_mode.add_argument("--skip-docs", action="store_true", help="Skip read-only documentation checks.")
    parser.add_argument("--enable-docs-registry", action="store_true",
                        help="Enable the optional tool Registry documentation check.")
    parser.add_argument("--enable-docs-todo-id", action="store_true",
                        help="Enable the optional TODO ID and development-plan link check.")
    parser.add_argument("--installed-manifest", action="append", type=Path, default=[],
                        help="Explicit read-only installed Skill/rules manifest; may be provided more than once.")
    parser.add_argument("--docs-strict", action="store_true",
                        help="Treat documentation unknown/warning results as gate failures.")
    args = parser.parse_args(argv)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    started = int(time.time() * 1000)
    before = source_identity(REPO)
    steps = []
    env = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1"}
    commands = [] if args.docs_only else [
        ("contracts", [sys.executable, "-m", "pytest", "-q", *["tests/" + name for name in CONTRACT_TESTS],
                       "--junitxml=" + str(output / "contracts.xml")], REPO / "upilotserver~"),
        ("skill", [sys.executable, str(REPO / "skills/upilot-unity-mcp/scripts/check_skill_pack.py")], REPO),
    ]
    for name, command, cwd in commands:
        log_path = output / (name + ".log")
        try:
            with log_path.open("w", encoding="utf-8") as log:
                result = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=300)
            exit_code = result.returncode
        except (OSError, subprocess.TimeoutExpired) as exc:
            exit_code = -1
            with log_path.open("a", encoding="utf-8") as log:
                log.write(str(exc))
        content = log_path.read_bytes()
        steps.append({"name": name, "exitCode": exit_code, "log": str(log_path),
                      "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()})
    inventory = None

    def get_inventory() -> dict:
        nonlocal inventory
        if inventory is None:
            inventory = tool_inventory()
        return inventory

    documentation = {"checked": False, "skipReason": "Requested --skip-docs."}
    if not args.skip_docs:
        documentation = documentation_checks(
            REPO,
            installed_manifests=[path.resolve() for path in args.installed_manifest],
            inventory_factory=get_inventory,
            include_registry=args.enable_docs_registry,
            include_todo_id=args.enable_docs_todo_id,
        )
        documentation["checked"] = True
        documentation["strict"] = args.docs_strict
        documentation["passed"] = not documentation["errorCheckIds"] and (
            not args.docs_strict or not documentation["warningCheckIds"]
        )

    if not args.docs_only:
        inventory_path = output / "tools.json"
        try:
            inventory_content = json.dumps(get_inventory(), indent=2).encode("utf-8")
            _atomic_write_bytes(inventory_path, inventory_content)
            inventory_exit_code = 0
        except Exception as exc:
            inventory_content = json.dumps({"error": str(exc)}, indent=2).encode("utf-8")
            try:
                _atomic_write_bytes(inventory_path, inventory_content)
            except OSError:
                pass
            inventory_exit_code = 1
        steps.append({"name": "toolInventory", "exitCode": inventory_exit_code, "log": str(inventory_path),
                      "bytes": len(inventory_content), "sha256": _sha256(inventory_content)})
    after = source_identity(REPO)
    unity = {"checked": False, "skipReason": "No Unity summary supplied; this gate is not Unity acceptance."}
    if args.require_unity_summary:
        try:
            unity = verify_release_evidence(REPO, os.environ.get("UPILOT_ACCEPTANCE_RUN_ID", ""), args.release_tag)
        except (OSError, ValueError, subprocess.SubprocessError) as exc:
            unity = {"checked": True, "passed": False, "error": str(exc)}
    elif args.unity_summary:
        try:
            content = args.unity_summary.read_bytes()
            verify_unity_summary(json.loads(content), before)
            unity = {"checked": True, "passed": True, "path": str(args.unity_summary),
                     "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)}
        except (OSError, ValueError) as exc:
            unity = {"checked": True, "passed": False, "error": str(exc)}
    report = {
        "schemaVersion": 1, "sourceIdentity": before, "sourceIdentityAfter": after,
        "sourceUnchanged": before == after, "startedAt": started, "endedAt": int(time.time() * 1000),
        "testScope": [] if args.docs_only else list(CONTRACT_TESTS), "steps": steps, "unity": unity,
        "documentation": documentation,
        "dependencies": {name: importlib.metadata.version(name) for name in ("mcp", "websockets", "pytest", "PyYAML", "pillow")},
        "passed": (
            before == after
            and all(step["exitCode"] == 0 for step in steps)
            and unity.get("passed", True)
            and documentation.get("passed", True)
        ),
    }
    content = json.dumps(report, indent=2).encode()
    path = output / "quality.json"
    _atomic_write_bytes(path, content)
    _atomic_write_bytes(path.with_suffix(".json.sha256"), (_sha256(content) + "\n").encode("ascii"))
    print(json.dumps({"passed": report["passed"], "path": str(path), "sourceIdentity": before, "steps": steps,
                      "documentation": {"checked": documentation["checked"], "passed": documentation.get("passed")}}))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
