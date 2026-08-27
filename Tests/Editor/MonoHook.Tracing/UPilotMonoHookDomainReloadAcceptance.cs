// -----------------------------------------------------------------------
// UPilot Editor tests - real Domain Reload acceptance bridge.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public static class UPilotMonoHookDomainReloadAcceptance
    {
        private const string SnapshotKey = "UPilot.MonoHook.Tracing.Tests.DomainReload.Snapshot";

        [Serializable]
        private sealed class SettingsSnapshot
        {
            public bool masterEnabled;
            public bool autoApplyOnEditorLoad;
            public bool suppressUnchangedValues;
            public int maxEventsPerSecond;
            public string lifecycleAssemblyIncludes;
            public string lifecycleAssemblyExcludes;
            public string lifecycleNamespaceIncludes;
            public string lifecycleNamespaceExcludes;
            public string lifecycleTypeIncludes;
            public string lifecycleTypeExcludes;
            public List<UPilotMonoHookPointState> points;
        }

        [Serializable]
        private sealed class Result
        {
            public bool ok;
            public string phase;
            public string detail;
            public bool installed;
            public int capturedEvents;
            public bool autoApplyOnEditorLoad;
            public bool configuredEnabled;
            public string autoApplyResult;
        }

        public static string BeginAndRequestCompilation()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(SnapshotKey, string.Empty)))
                return JsonUtility.ToJson(new Result { ok = false, phase = "begin", detail = "已有未恢复的验收快照，请先调用 Restore。" });

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            SessionState.SetString(SnapshotKey, JsonUtility.ToJson(Capture(settings)));

            settings.masterEnabled = true;
            settings.autoApplyOnEditorLoad = true;
            settings.suppressUnchangedValues = false;
            settings.maxEventsPerSecond = 1000;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, true);
            settings.SaveSettings();

            var report = new UPilotMonoHookController().Apply(false);
            bool installed = UPilotMonoHookInstallationService.IsInstalled(UPilotMonoHookPointId.GameObjectSetActive);
            if (!installed || report.Failed.Contains(UPilotMonoHookPointId.GameObjectSetActive))
                return JsonUtility.ToJson(new Result { ok = false, phase = "begin", detail = "SetActive Hook 安装失败。", installed = installed });

            CompilationPipeline.RequestScriptCompilation();
            return JsonUtility.ToJson(new Result { ok = true, phase = "compile_requested", detail = "等待真实 Domain Reload 后调用 Verify。", installed = true });
        }

        public static string Verify()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(SnapshotKey, string.Empty)))
                return JsonUtility.ToJson(new Result { ok = false, phase = "verify", detail = "未找到 Begin 保存的验收快照。" });

            bool installed = UPilotMonoHookInstallationService.IsInstalled(UPilotMonoHookPointId.GameObjectSetActive);
            var settings = UPilotMonoHookSettings.instance;
            UPilotMonoHookTelemetry.Clear();
            var gameObject = new GameObject("UPilotMonoHookDomainReloadAcceptance");
            try
            {
                gameObject.SetActive(false);
                int captured = UPilotMonoHookTelemetry.Snapshot(16).Count(item => item.kind == "gameObject.setActive");
                return JsonUtility.ToJson(new Result
                {
                    ok = installed && captured == 1,
                    phase = "verified",
                    detail = installed ? "自动恢复已检查。" : "Domain Reload 后 Hook 未自动恢复。",
                    installed = installed,
                    capturedEvents = captured,
                    autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad,
                    configuredEnabled = settings.IsConfiguredEnabled(UPilotMonoHookPointId.GameObjectSetActive),
                    autoApplyResult = UPilotMonoHookAutoApply.LastResult,
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        public static string Restore()
        {
            string json = SessionState.GetString(SnapshotKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return JsonUtility.ToJson(new Result { ok = true, phase = "restored", detail = "没有待恢复快照。" });

            UPilotMonoHookInstallationService.UninstallAll();
            var snapshot = JsonUtility.FromJson<SettingsSnapshot>(json);
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = snapshot.masterEnabled;
            settings.autoApplyOnEditorLoad = snapshot.autoApplyOnEditorLoad;
            settings.suppressUnchangedValues = snapshot.suppressUnchangedValues;
            settings.maxEventsPerSecond = snapshot.maxEventsPerSecond;
            settings.lifecycleAssemblyIncludes = snapshot.lifecycleAssemblyIncludes;
            settings.lifecycleAssemblyExcludes = snapshot.lifecycleAssemblyExcludes;
            settings.lifecycleNamespaceIncludes = snapshot.lifecycleNamespaceIncludes;
            settings.lifecycleNamespaceExcludes = snapshot.lifecycleNamespaceExcludes;
            settings.lifecycleTypeIncludes = snapshot.lifecycleTypeIncludes;
            settings.lifecycleTypeExcludes = snapshot.lifecycleTypeExcludes;
            settings.points = snapshot.points ?? new List<UPilotMonoHookPointState>();
            settings.EnsureDefaults();
            settings.SaveSettings();
            SessionState.EraseString(SnapshotKey);
            UPilotMonoHookTelemetry.Clear();
            return JsonUtility.ToJson(new Result { ok = true, phase = "restored", detail = "设置与 Hook 状态已恢复。" });
        }

        private static SettingsSnapshot Capture(UPilotMonoHookSettings settings)
        {
            return new SettingsSnapshot
            {
                masterEnabled = settings.masterEnabled,
                autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad,
                suppressUnchangedValues = settings.suppressUnchangedValues,
                maxEventsPerSecond = settings.maxEventsPerSecond,
                lifecycleAssemblyIncludes = settings.lifecycleAssemblyIncludes,
                lifecycleAssemblyExcludes = settings.lifecycleAssemblyExcludes,
                lifecycleNamespaceIncludes = settings.lifecycleNamespaceIncludes,
                lifecycleNamespaceExcludes = settings.lifecycleNamespaceExcludes,
                lifecycleTypeIncludes = settings.lifecycleTypeIncludes,
                lifecycleTypeExcludes = settings.lifecycleTypeExcludes,
                points = settings.points.Select(point => new UPilotMonoHookPointState(point.Id, point.Enabled, point.CaptureStackTrace)).ToList(),
            };
        }
    }
}
