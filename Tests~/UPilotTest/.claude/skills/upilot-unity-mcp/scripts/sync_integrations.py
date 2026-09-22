"""Project-scoped, template-authoritative integration synchronization.

The wire format and byte-range lock are shared with UPilotAgentSetup.Integrations.cs.
No Unity/Python runtime dependency is introduced by either implementation.
"""
from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import uuid

START = b"<!-- upilot:start -->"
END = b"<!-- upilot:end -->"
STATE = ".upilot/agent-integrations.json"
CURSOR = b"---\ndescription: Use UPilot MCP for Unity Editor automation\nalwaysApply: true\n---\n\n"
RULES = ("AGENTS.md", ".cursor/rules/upilot-unity-mcp.mdc", "CLAUDE.md")
SKILLS = (".agents/skills/upilot-unity-mcp", ".claude/skills/upilot-unity-mcp")


class BusyError(OSError):
    pass


def safe_path(root: Path, path: Path) -> None:
    root, path = Path(os.path.abspath(root)), Path(os.path.abspath(path))
    if path != root and root not in path.parents:
        raise OSError(f"integration path escapes root: {path}")
    for part in (path, *path.parents):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise OSError(f"integration path contains symbolic link or junction: {part}")


def safe_tree(path: Path) -> None:
    safe_path(path, path)
    if path.is_dir():
        for child in path.iterdir():
            safe_path(path, child)
            if child.is_dir():
                safe_tree(child)


@contextmanager
def project_lock(project: Path, apply: bool):
    path = project / ".upilot/agent-integrations.lock"
    safe_path(project, path)
    if apply:
        path.parent.mkdir(parents=True, exist_ok=True)
    if not apply and not path.exists():
        yield
        return
    # Do not truncate: Windows and Unix lock the same first byte, even for an empty file.
    stream = path.open("a+b" if apply else "r+b")
    locked = False
    try:
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.lockf(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB, 1, 0)
            locked = True
        except OSError as exc:
            raise BusyError("another integration sync holds the project lock") from exc
        yield
    finally:
        if locked:
            stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.lockf(stream.fileno(), fcntl.LOCK_UN, 1, 0)
        stream.close()


def normalized_block(block: bytes) -> bytes:
    block = block.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return re.sub(rb"(?m)^generatedAt:[^\n]*", b"generatedAt:", block).rstrip()


def replace_block(original: bytes, block: bytes, preamble: bytes = b"") -> tuple[bytes, bytes]:
    original.decode("utf-8", errors="strict")
    starts, ends = original.count(START), original.count(END)
    if starts == ends == 0:
        separator = preamble if not original else b"\n" if original.endswith(b"\n") else b"\n\n"
        return original + separator + block + b"\n", b""
    if starts != 1 or ends != 1 or original.index(START) >= original.index(END):
        raise ValueError("expected one ordered pair of UPilot managed markers")
    begin, end = original.index(START), original.index(END) + len(END)
    current = original[begin:end]
    if normalized_block(current) == normalized_block(block):
        return original, current
    return original[:begin] + block + original[end:], current


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def files_hash(files: dict[str, bytes]) -> str:
    digest = hashlib.sha256()
    for name in sorted(files, key=str.lower):
        digest.update(name.encode("utf-8") + b"\0" + files[name] + b"\0")
    return digest.hexdigest()


def json_bytes(value: dict) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def parent_rules(project: Path) -> str:
    for parent in project.parents:
        if (parent / "AGENTS.md").is_file():
            return os.path.relpath(parent / "AGENTS.md", project).replace("\\", "/")
    return "(none)"


def resolve_port(project: Path, explicit: int | None, manifest: dict) -> int:
    if explicit is not None:
        return explicit
    config = project / ".upilot/config.json"
    if config.is_file():
        data = json.loads(config.read_text(encoding="utf-8-sig"))
        return data.get("mcp", {}).get("httpPort", manifest["defaultHttpPort"])
    return manifest["defaultHttpPort"]


def _remove(path: Path):
    if path.is_dir():
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


def _write_state(path: Path, data: bytes, work: Path):
    candidate = work / "state.json"
    candidate.write_bytes(data)
    candidate.replace(path)


def _verify(path: Path, item: dict, helpers, metadata: dict):
    actual = sha(path.read_bytes()) if item["kind"] == "rule" else helpers._skill_content_hash(path)
    if actual != item["expectedSha256"]:
        raise OSError(f"final content verification failed: {item['path']}")
    if item["kind"] == "skill" and helpers._read_skill_metadata(path) != metadata:
        raise OSError(f"final metadata verification failed: {item['path']}")


def synchronize(project: Path, package: Path, *, apply=False, scope="all", http_port=None,
                trigger="python", helpers=None, context_project: Path | None = None) -> dict:
    if helpers is None:
        import install_upilot as helpers
    report = dict(schemaVersion=1, ok=True, dryRun=not apply, changed=False, status="current",
                  templateSource="", templateSha256="", error="", targets=[])
    try:
        if scope not in ("all", "rules", "shared", "skills"):
            raise ValueError(f"unknown integration scope: {scope}")
        project, package = Path(os.path.abspath(project)), Path(os.path.abspath(package))
        context_project = context_project or project
        safe_path(project, project)
        source = package / "skills/upilot-unity-mcp"
        safe_tree(source)
        report["templateSource"] = str(source)
        renderer = helpers._skill_renderer()
        manifest = renderer.load_manifest(source)
        version = json.loads((package / "package.json").read_text(encoding="utf-8-sig"))["version"]
        context = renderer.build_context(manifest, http_port=resolve_port(context_project, http_port, manifest),
                                        project_path=str(context_project), package=version,
                                        parent_agent_rules_path=parent_rules(context_project))
        compact_context = {key: context[key] for key in
                           ("projectPath", "mcpUrl", "healthUrl", "upilotPackageVersion")}
        record_context = dict(compact_context, parentAgentRulesPath=context["parentAgentRulesPath"])
        report.update(agentRulesVersion=manifest["agentRulesVersion"], skillPackVersion=manifest["skillPackVersion"],
                      templateSha256=renderer.template_sha256(source, manifest), renderContext=record_context)
        body = renderer.render_template(source, manifest, "agentRules", context).encode("utf-8")
        # Freeze resource bytes and generated outputs before any target/metadata/backup write.
        files = {}
        for path in source.rglob("*"):
            relative = path.relative_to(source)
            if path.is_file() and path.name.lower() != ".upilot-install.json" and not any(
                p.lower() == "__pycache__" or p.lower().endswith((".meta", ".pyc", ".pyo")) for p in relative.parts
            ):
                files[relative.as_posix()] = path.read_bytes()
        for path, content in renderer.render_skill_outputs(source, context, manifest).items():
            files[path.relative_to(source).as_posix()] = content.encode("utf-8")
        expected_skill_hash = files_hash(files)
        metadata = dict(schemaVersion=2, templateVersion=manifest["skillPackVersion"],
                        templateSha256=report["templateSha256"], contentSha256=expected_skill_hash,
                        renderContext=compact_context, renderedAt=context["generatedAt"])
        files[".upilot-install.json"] = json_bytes(metadata)
        specs = list(RULES if scope in ("all", "rules") else ("AGENTS.md",) if scope == "shared" else ())
        if scope in ("all", "skills"):
            specs.extend(SKILLS)
        with project_lock(project, apply):
            state_path = project / STATE
            safe_path(project, state_path)
            try:
                records = json.loads(state_path.read_text(encoding="utf-8"))
                if records.get("schemaVersion") != 1 or not isinstance(records.get("rules"), list):
                    raise ValueError("invalid records")
            except (FileNotFoundError, ValueError):
                records = dict(schemaVersion=1, rules=[])
            for relative in specs:
                target = project / relative
                item = dict(relativePath=relative, path=str(target), kind="rule" if relative in RULES else "skill",
                            status="current", beforeSha256="", expectedSha256="", afterSha256="", backupPath="",
                            error="", recoveryPath="", currentBlock="", recommendedBlock="", needsUpdate=False, needsBackup=False,
                            hasUpilotBlock=False, reasons=[])
                report["targets"].append(item)
                try:
                    safe_path(project, target)
                    safe_tree(target)
                    exists = target.exists()
                    guard = helpers._exact_target_hash(target)[0] if exists else ""
                    next_record = None
                    candidate = None
                    if item["kind"] == "rule":
                        original = target.read_bytes() if exists else b""
                        block = START + b"\n" + (b"@AGENTS.md" if relative == "CLAUDE.md" else body.rstrip()) + b"\n" + END
                        candidate, current = replace_block(original, block, CURSOR if not exists and relative.endswith(".mdc") else b"")
                        item.update(currentBlock=current.decode("utf-8"), recommendedBlock=block.decode("utf-8"),
                                    hasUpilotBlock=bool(current), beforeSha256=sha(original) if exists else "",
                                    expectedSha256=sha(candidate))
                        record = next((r for r in records["rules"] if r.get("relativePath") == relative), None)
                        next_record = dict(relativePath=relative, normalizedManagedSha256=sha(normalized_block(block)),
                                           templateVersion=manifest["agentRulesVersion"],
                                           templateSha256=report["templateSha256"], renderContext=record_context)
                        if original != candidate:
                            item["reasons"].append("managed_block_differs" if exists else "missing")
                        if record != next_record:
                            item["reasons"].append("management_record_differs")
                        item["needsBackup"] = exists and original != candidate and (
                            not current or not record or record.get("normalizedManagedSha256") != sha(normalized_block(current)))
                    else:
                        old = helpers._read_skill_metadata(target) if target.is_dir() else None
                        actual = helpers._skill_content_hash(target) if target.is_dir() else guard
                        clean = old is not None and old["contentSha256"] == actual
                        item.update(beforeSha256=actual, expectedSha256=expected_skill_hash, needsBackup=exists and not clean)
                        if not exists:
                            item["reasons"].append("missing")
                        elif not clean:
                            item["reasons"].append("unmanaged_or_modified")
                        if actual != expected_skill_hash:
                            item["reasons"].append("content_differs")
                        if not old or old.get("schemaVersion") != 2 or old["templateVersion"] != manifest["skillPackVersion"]:
                            item["reasons"].append("version_differs")
                        if not old or old.get("templateSha256") != report["templateSha256"]:
                            item["reasons"].append("template_differs")
                        if not old or old.get("renderContext") != compact_context:
                            item["reasons"].append("context_differs")
                    item.update(needsUpdate=bool(item["reasons"]), afterSha256=item["beforeSha256"])
                    if not item["needsUpdate"]:
                        continue
                    item["status"] = "needs_sync"
                    if not apply:
                        continue
                    work = project / ".upilot/agent-integration-staging" / uuid.uuid4().hex
                    safe_path(project, work)
                    work.mkdir(parents=True)
                    stage, rollback = work / "candidate", work / "rollback"
                    item["recoveryPath"] = str(work)
                    old_state = state_path.read_bytes() if state_path.exists() else None
                    old_records = json.loads(json.dumps(records))
                    moved = committed = state_written = success = False
                    try:
                        if candidate is not None:
                            stage.write_bytes(candidate)
                        else:
                            for name, content in files.items():
                                output = stage / name
                                output.parent.mkdir(parents=True, exist_ok=True)
                                output.write_bytes(content)
                        _verify(stage, item, helpers, metadata)
                        if item["needsBackup"]:
                            backup_root = project / ".upilot/backups/agent-integrations"
                            safe_path(project, backup_root)
                            item["backupPath"] = str(helpers._backup_skill_target(target, project, trigger=trigger,
                                reason=",".join(item["reasons"]), agent_rules_version=manifest["agentRulesVersion"],
                                skill_pack_version=manifest["skillPackVersion"]))
                        safe_path(project, target)
                        safe_tree(target)
                        if (helpers._exact_target_hash(target)[0] if target.exists() else "") != guard:
                            raise OSError("target changed during synchronization; retry after inspection")
                        # Metadata adoption alone must not touch the rule file or its timestamp.
                        if item["kind"] != "rule" or candidate != original:
                            target.parent.mkdir(parents=True, exist_ok=True)
                            if exists:
                                target.replace(rollback)
                                moved = True
                            stage.replace(target)
                            committed = True
                        _verify(target, item, helpers, metadata)
                        if next_record is not None:
                            records["rules"] = [r for r in records["rules"] if r.get("relativePath") != relative] + [next_record]
                            current_state = state_path.read_bytes() if state_path.exists() else None
                            if current_state != old_state:
                                raise OSError("management records changed during synchronization")
                            data = json_bytes(records)
                            state_written = True
                            _write_state(state_path, data, work)
                            if state_path.read_bytes() != data:
                                raise OSError("management record verification failed")
                        item.update(afterSha256=item["expectedSha256"],
                                    status="backed_up_and_synced" if item["needsBackup"] else "synced")
                        report["changed"] = success = True
                    except Exception:
                        if committed:
                            safe_path(project, target)
                            safe_tree(target)
                            _remove(target)
                        if moved:
                            rollback.replace(target)
                        if state_written:
                            if old_state is None:
                                state_path.unlink(missing_ok=True)
                            else:
                                state_path.write_bytes(old_state)
                        records = old_records
                        raise
                    finally:
                        # Failed rollback keeps recovery material; never hide it in a discovery directory.
                        safe_path(project, work)
                        safe_tree(work)
                        if success or not rollback.exists():
                            shutil.rmtree(work)
                            item["recoveryPath"] = ""
                except Exception as exc:
                    item.update(status="failed", error=str(exc))
                    report["ok"] = False
        report["status"] = "partial_failure" if not report["ok"] else "synced" if report["changed"] else (
            "needs_sync" if any(t["needsUpdate"] for t in report["targets"]) else "current")
    except Exception as exc:
        report.update(ok=False, status="busy" if isinstance(exc, BusyError) else "failed", error=str(exc))
    if scope != "shared":
        for target in report["targets"]:
            target.update(currentBlock="", recommendedBlock="")
    return report
