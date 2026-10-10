"""Content identity for checked source, including uncommitted implementation changes."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess


SOURCE_DIRS = ("Editor", "Runtime", "Tests", "upilotserver~/src", "upilotserver~/tests",
               "upilotserver~/scripts", "upilotserver~/deploy", "skills", ".github/workflows")
SOURCE_FILES = ("package.json", "upilotserver~/pyproject.toml", "upilotserver~/uv.lock")
IMPORT_INPUT_SUFFIXES = {".asmdef", ".asmref", ".cs", ".meta", ".rsp"}


def source_identity(root: Path, revision: str | None = None) -> dict:
    identity, _ = source_identity_with_files(root, revision)
    return identity


def source_identity_with_files(root: Path, revision: str | None = None) -> tuple[dict, dict[str, str]]:
    root = root.resolve()
    files = set()
    for directory in SOURCE_DIRS:
        location = root / directory
        if location.exists():
            files.update(path for path in location.rglob("*") if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc")
    for name in SOURCE_FILES:
        if (root / name).is_file():
            files.add(root / name)
    entries = []
    if revision:
        revision = subprocess.check_output(["git", "rev-parse", "--verify", revision + "^{commit}"], cwd=root, text=True).strip()
        names = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", "-z", revision], cwd=root).decode().split("\0")
        files = {root / name for name in names if name in SOURCE_FILES or any(name.startswith(d + "/") for d in SOURCE_DIRS)}
        files = {p for p in files if "__pycache__" not in p.parts and p.suffix != ".pyc"}
    # Explicit POSIX lexical order is identical on Windows and Linux.
    for path in sorted(files, key=lambda p: p.relative_to(root).as_posix()):
        content = (subprocess.check_output(["git", "show", revision + ":" + path.relative_to(root).as_posix()], cwd=root)
                   if revision else path.read_bytes())
        if path.suffix.lower() in {".cs", ".py", ".json", ".asmdef", ".asmref", ".meta", ".md", ".yml", ".yaml", ".toml", ".lock", ".template", ".shader", ".hlsl"}:
            content = content.replace(b"\r\n", b"\n")
        entries.append([path.relative_to(root).as_posix(), hashlib.sha256(content).hexdigest()])
    try:
        commit = revision or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
    except (OSError, subprocess.CalledProcessError):
        commit = ""
    return {
        "sourceCommit": commit,
        "sourceSha256": hashlib.sha256(json.dumps(entries, separators=(",", ":")).encode()).hexdigest(),
        "fileCount": len(entries),
        "scope": "package-code-tests-skills-workflows-v2",
        "textNormalization": "CRLF-to-LF",
    }, dict(entries)


def acceptance_import_inputs(source_root: Path, project: Path) -> dict:
    """Hash importer-sensitive project inputs without treating equal hashes as import proof."""
    source_root = source_root.resolve()
    project = project.resolve()
    _, package_files = source_identity_with_files(source_root)
    inputs = {"package:" + path: digest for path, digest in package_files.items()}
    for folder in (project / "Assets", project / "Packages"):
        if not folder.is_dir():
            continue
        for path in folder.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in IMPORT_INPUT_SUFFIXES:
                continue
            relative = path.relative_to(project).as_posix()
            content = path.read_bytes()
            if path.suffix.lower() in {".asmdef", ".asmref", ".cs", ".meta", ".rsp"}:
                content = content.replace(b"\r\n", b"\n")
            inputs["project:" + relative] = hashlib.sha256(content).hexdigest()
    return {
        "schemaVersion": 1,
        "projectPath": str(project),
        "inputCount": len(inputs),
        "inputs": inputs,
    }


def diff_acceptance_import_inputs(before: dict, after: dict, limit: int = 200) -> dict:
    before_inputs = before.get("inputs") if isinstance(before.get("inputs"), dict) else {}
    after_inputs = after.get("inputs") if isinstance(after.get("inputs"), dict) else {}
    changes = []
    for path in sorted(set(before_inputs) | set(after_inputs)):
        before_hash, after_hash = before_inputs.get(path, ""), after_inputs.get(path, "")
        if before_hash == after_hash:
            continue
        changes.append({"path": path, "beforeSha256": before_hash, "afterSha256": after_hash})
    return {"changed": changes[:limit], "changeCount": len(changes), "changesTruncated": len(changes) > limit}


# Deliberately limited to this reliability batch, not proof of every loaded module.
# Discovery uses sys.modules only: diagnostics must not import tool registration.
SELECTED_CODE_ENTRIES = (
    ("upilot_mcp.server", "WsOrchestratorServer._handle_message"),
    ("upilot_mcp.server", "WsOrchestratorServer._suspend_or_fail_pending_on_disconnect"),
    ("upilot_mcp.server", "WsOrchestratorServer._resend_pending_commands"),
    ("upilot_mcp.server", "WsOrchestratorServer.send_command"),
    ("upilot_mcp.dispatcher", "CommandDispatcher.call"),
    ("upilot_mcp.domain.execution_service", "ExecutionDomainService.diagnostic_module"),
    ("upilot_mcp.mcp_tools.execution_tools", "diagnostic_module"),
    ("upilot_mcp.domain.task_service", "TaskDomainService.task_start"),
    ("upilot_mcp.domain.task_service", "TaskDomainService.operation_get"),
    ("upilot_mcp.domain.task_service", "TaskDomainService._run_test_task"),
    ("upilot_mcp.domain.queue_service", "QueueDomainService.queue_inventory"),
    ("upilot_mcp.state_store", "StateStore.save_test_job"),
    ("upilot_mcp.state_store", "StateStore.command_observation"),
    ("upilot_mcp.state_store", "StateStore.freeze_write_batch_round"),
    ("upilot_mcp.state_store", "StateStore.record_write_batch_round_dispatch"),
    ("upilot_mcp.state_store", "StateStore._persist_round_evidence"),
    ("upilot_mcp.state_store", "StateStore.get_write_batch_round"),
    ("upilot_mcp.state_store", "StateStore.get_write_batch"),
    ("upilot_mcp.domain.resource_service", "ResourceDomainService._resume_pending_write_batches"),
    ("upilot_mcp.domain.compile_service", "CompileDomainService.compile"),
    ("upilot_mcp.domain.compile_service", "CompileDomainService._safe_compile_and_wait"),
    ("upilot_mcp.mcp_stdio_server", "_call_tool_with_strict_arguments"),
    ("upilot_mcp.operation_context", "request_origin_from_context"),
)
SELECTED_CODE_SCOPE = "selected-entry-code-only; not globals, closure values, dependencies or all modules"


def selected_entry_code_evidence() -> dict:
    """Compare selected live Python code to current source without executing it.

    Code equivalence is not whole-module deployment proof. Missing/frozen source
    remains unknown; ordinary status queries do not invoke this on-demand check.
    """
    import sys
    import types

    entries = []
    sources = {}
    for module_name, member in SELECTED_CODE_ENTRIES:
        entry = {"module": module_name, "entry": member, "status": "unknown", "layers": []}
        entries.append(entry)
        module = sys.modules.get(module_name)
        target = module
        for part in member.split("."):
            if not isinstance(target, (types.ModuleType, type)):
                target = None
                break
            target = vars(target).get(part)
            if isinstance(target, (staticmethod, classmethod)):
                target = target.__func__
        if not isinstance(target, types.FunctionType):
            entry["reason"] = "entry_not_loaded_or_not_python_function"
            continue
        seen = set()
        for _ in range(4):
            if not isinstance(target, types.FunctionType) or id(target) in seen:
                entry["reason"] = "wrapper_chain_unverifiable"
                break
            seen.add(id(target))
            code = target.__code__
            layer = {"function": code.co_qualname, "path": code.co_filename, "status": "unknown"}
            entry["layers"].append(layer)
            # Decorators may copy __module__/__name__; the actual code filename
            # and globals identify the wrapper's source, not the copied labels.
            key = code.co_filename
            if key not in sources:
                try:
                    path = Path(key)
                    if getattr(sys, "frozen", False) or not path.is_absolute() or path.suffix != ".py":
                        raise ValueError("source_unavailable")
                    with path.open("rb") as stream:
                        raw = stream.read(2 * 1024 * 1024 + 1)
                    if len(raw) > 2 * 1024 * 1024:
                        raise ValueError("source_over_budget")
                    root = compile(raw, key, "exec", dont_inherit=True, optimize=sys.flags.optimize)
                    codes = {}
                    pending = [root]
                    while pending:
                        current = pending.pop()
                        codes.setdefault(current.co_qualname, []).append(current)
                        pending.extend(c for c in current.co_consts if isinstance(c, types.CodeType))
                    sources[key] = (hashlib.sha256(raw).hexdigest(), codes, "")
                except (OSError, ValueError, SyntaxError, RecursionError, MemoryError) as ex:
                    # Do not expose arbitrary filesystem/exception payloads.
                    sources[key] = ("", {}, type(ex).__name__)
            digest, codes, error = sources[key]
            layer["sourceSha256"] = digest
            references = codes.get(code.co_qualname, [])
            if error or len(references) != 1:
                layer["reason"] = "source_unavailable_or_ambiguous"
            else:
                layer["status"] = "equivalent" if code == references[0] else "different"
            wrapped = vars(target).get("__wrapped__")
            if wrapped is None:
                break
            target = wrapped
        else:
            entry["reason"] = "wrapper_chain_over_budget"
        statuses = [layer["status"] for layer in entry["layers"]]
        if "different" in statuses:
            entry["status"] = "different"
        elif statuses and all(status == "equivalent" for status in statuses) and not entry.get("reason"):
            entry["status"] = "equivalent"
    statuses = [entry["status"] for entry in entries]
    status = "different" if "different" in statuses else "equivalent" if statuses and all(s == "equivalent" for s in statuses) else "unknown"
    return {"status": status, "scope": SELECTED_CODE_SCOPE, "entries": entries}
