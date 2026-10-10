"""Finite, auditable permissions for modeled UPilot safety gates.

Unknown dialogs, another Unity process and arbitrary business actions are not in
this catalog.  A scope never replaces the exact target checks owned by a tool.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from typing import Iterable

CATALOG_SCOPE_VERSION = 1

@dataclass(frozen=True, slots=True)
class AutomationAuthorizationScope:
    key: str
    label: str
    risk: str
    tools: tuple[str, ...]
    target_requirement: str

_SCOPES = (
    AutomationAuthorizationScope("editorModeTransition", "自动切换 EditMode / PlayMode", "会改变当前编辑器运行状态；任务结束后不恢复旧模式。", ("unity_ensure_ready", "unity_playmode_start", "unity_playmode_stop"), "当前已验证 Unity 项目和精确目标模式"),
    AutomationAuthorizationScope("scenePolicyExecution", "执行已选未保存场景策略", "autoSave 会写入场景，ignore 会丢弃场景修改；block 永不被覆盖。", ("unity_test_run", "unity_upilot_acceptance_run", "scene.prepareForAutomation"), "当前项目中已加载的精确场景"),
    AutomationAuthorizationScope("captureForceStop", "强制停止无 ownerToken 的 Capture", "只能对精确 sessionId 执行 forceStop，停止后保留并核验产物。", ("unity_console_capture_stop",), "当前项目、精确 active sessionId"),
    AutomationAuthorizationScope("captureAcceptanceClearance", "自动停止验收前阻塞的 Capture", "逐个处理 active Capture；任一变更或失败即停止验收。", ("unity_upilot_acceptance_run",), "当前项目列举到的精确 active sessionId"),
    AutomationAuthorizationScope("captureArtifactCleanup", "清理过期 Capture 产物", "仍要求 dry-run、路径和 confirmToken 校验。", ("unity_console_capture_cleanup",), "当前项目内预览列出的目录和哈希"),
    AutomationAuthorizationScope("destructiveProjectWrite", "项目写入授权", "允许已建模工具修改当前项目文件，不能写入其他项目。", ("write-gated tools",), "当前项目和工具报告的精确路径"),
    AutomationAuthorizationScope("configCsvApply", "确认配置 CSV 写入", "仍要求 dry-run、值和 confirmToken 不变。", ("unity_config_csv_patch",), "预览哈希、目标记录和值"),
    AutomationAuthorizationScope("prefabPatchApply", "确认 Prefab patch", "仍要求 dry-run、Prefab 路径和 confirmToken 不变。", ("unity_prefab_patch",), "精确 Prefab、子节点和组件"),
    AutomationAuthorizationScope("textureImporterApply", "确认 Texture Importer patch", "仍要求 dry-run、资产路径和 confirmToken 不变。", ("unity_texture_importer_patch",), "精确纹理资产和导入设置"),
    AutomationAuthorizationScope("snapshotBaselineUpdate", "确认 Snapshot baseline 更新", "仍要求 dry-run、基线路径和 confirmToken 不变。", ("unity_snapshot_baseline_update",), "精确基线、当前状态哈希"),
    AutomationAuthorizationScope("assetMoveDelete", "精确资产移动或删除", "仅限当前项目中已检查的精确源和目标。", ("asset move/delete tools",), "当前项目内精确源/目标路径"),
    AutomationAuthorizationScope("editorWindowForceDiscard", "强制关闭 EditorWindow / 丢弃草稿", "仅限已识别窗口和已建模草稿。", ("unity_editor_window_close",), "精确 Unity window instanceId"),
    AutomationAuthorizationScope("hangRestart", "声明的 Unity 挂起恢复或重启", "仍要求精确 Editor 身份和既有诊断流程。", ("unity_hang_*", "server restart"), "当前项目的已验证 Editor PID/身份"),
)

def catalog() -> tuple[AutomationAuthorizationScope, ...]: return _SCOPES

def catalog_hash() -> str:
    # Scope keys are immutable authorization identities. Adding/removing one changes
    # this hash; wording edits retain the human choices without widening authority.
    return hashlib.sha256("|".join(item.key for item in _SCOPES).encode("utf-8")).hexdigest()

def normalize_scopes(values: Iterable[object] | None) -> tuple[str, ...]:
    known = {item.key for item in _SCOPES}
    return tuple(sorted({str(value).strip() for value in (values or ()) if str(value).strip() in known}))

def authorization_status(values: Iterable[object] | None, stored_hash: str = "", stored_version: int = 0) -> dict:
    enabled = normalize_scopes(values); expected_hash = catalog_hash(); expected = {item.key for item in _SCOPES}
    current = stored_hash == expected_hash and int(stored_version or 0) == CATALOG_SCOPE_VERSION
    full = current and set(enabled) == expected
    return {"catalog": [{**asdict(item), "tools": list(item.tools)} for item in _SCOPES], "catalogHash": expected_hash,
            "scopeVersion": CATALOG_SCOPE_VERSION, "enabledScopes": list(enabled), "fullAuthorization": full,
            "mixedAuthorization": bool(enabled) and not full, "catalogCurrent": current,
            "missingScopes": sorted(expected - set(enabled))}

def has_scope(key: str, values: Iterable[object] | None) -> bool: return key in normalize_scopes(values)
