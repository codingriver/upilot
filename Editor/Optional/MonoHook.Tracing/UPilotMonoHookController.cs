// -----------------------------------------------------------------------
// UPilot Editor - MonoHook configuration controller.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public enum UPilotMonoHookInstallState
    {
        NotInstalled,
        Installed,
        PartiallyInstalled,
        Unsupported,
        Failed,
    }

    public sealed class UPilotMonoHookPointRuntimeState
    {
        public string PointId;
        public bool ConfiguredEnabled;
        public UPilotMonoHookInstallState InstallState;
        public string Message;
        public UPilotMonoHookCoverage Coverage;
    }

    public sealed class UPilotMonoHookApplyReport
    {
        public readonly List<string> Enabled = new List<string>();
        public readonly List<string> Disabled = new List<string>();
        public readonly List<string> Unchanged = new List<string>();
        public readonly List<string> Partial = new List<string>();
        public readonly List<string> Unsupported = new List<string>();
        public readonly List<string> Failed = new List<string>();
    }

    [Serializable]
    public sealed class UPilotMonoHookDiagnosticRecord
    {
        public string generatedAtUtc;
        public string unityVersion;
        public string pointId;
        public string displayName;
        public string categoryId;
        public bool configuredEnabled;
        public string installState;
        public string message;
        public int candidateCount;
        public int installedCount;
        public int skippedCount;
        public int failedCount;
        public List<string> samples = new List<string>();
    }

    public sealed class UPilotMonoHookController
    {
        private readonly Dictionary<string, UPilotMonoHookPointRuntimeState> _runtime =
            new Dictionary<string, UPilotMonoHookPointRuntimeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action> _installers =
            new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action> _uninstallers =
            new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly UPilotMonoHookRegistry _registry;

        public IReadOnlyDictionary<string, UPilotMonoHookPointRuntimeState> Runtime => _runtime;

        public UPilotMonoHookController()
        {
            _registry = UPilotMonoHookRegistry.Instance;
            UPilotMonoHookSettings.instance.EnsureDefaults();
            RefreshRuntime();
        }

        /// <summary>
        /// Compatibility and test seam. Attribute-discovered providers are used when
        /// no explicit installer override has been registered for the point.
        /// </summary>
        public void RegisterInstaller(string pointId, Action install, Action uninstall)
        {
            if (string.IsNullOrEmpty(pointId)) throw new ArgumentException("Point id is required.", nameof(pointId));
            if (UPilotMonoHookCatalog.Find(pointId) == null) throw new ArgumentException("Unknown MonoHook point: " + pointId, nameof(pointId));
            _installers[pointId] = install;
            _uninstallers[pointId] = uninstall;
        }

        public void RefreshRuntime()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();

            var knownIds = new HashSet<string>(UPilotMonoHookCatalog.All.Select(item => item.Id), StringComparer.Ordinal);
            foreach (var staleId in _runtime.Keys.Where(id => !knownIds.Contains(id)).ToArray())
                _runtime.Remove(staleId);

            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                if (!_runtime.TryGetValue(definition.Id, out var state))
                {
                    state = new UPilotMonoHookPointRuntimeState { PointId = definition.Id };
                    _runtime.Add(definition.Id, state);
                }

                state.ConfiguredEnabled = settings.IsConfiguredEnabled(definition.Id);
                RefreshPointState(definition.Id, state);
            }
        }

        public UPilotMonoHookApplyReport Apply(bool persistSettings = true)
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (persistSettings)
                settings.SaveSettings();
            RefreshRuntime();

            var report = new UPilotMonoHookApplyReport();
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                var state = _runtime[definition.Id];
                bool shouldInstall = settings.IsEnabled(definition.Id);
                bool isInstalled = IsInstalled(definition.Id, state);

                if (shouldInstall == isInstalled)
                {
                    report.Unchanged.Add(definition.Id);
                    continue;
                }

                if (shouldInstall)
                    ApplyInstall(definition.Id, state, report);
                else
                    ApplyUninstall(definition.Id, state, report);
            }
            return report;
        }

        public void UninstallAll()
        {
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                var state = _runtime.TryGetValue(definition.Id, out var runtimeState)
                    ? runtimeState
                    : null;
                var report = new UPilotMonoHookApplyReport();
                ApplyUninstall(definition.Id, state, report);
            }
        }

        public List<UPilotMonoHookDiagnosticRecord> GetDiagnosticSnapshot()
        {
            RefreshRuntime();
            string generatedAtUtc = DateTime.UtcNow.ToString("O");
            var records = new List<UPilotMonoHookDiagnosticRecord>();
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                var state = _runtime[definition.Id];
                var coverage = state.Coverage;
                records.Add(new UPilotMonoHookDiagnosticRecord
                {
                    generatedAtUtc = generatedAtUtc,
                    unityVersion = Application.unityVersion,
                    pointId = definition.Id,
                    displayName = definition.DisplayName,
                    categoryId = definition.CategoryId,
                    configuredEnabled = state.ConfiguredEnabled,
                    installState = state.InstallState.ToString(),
                    message = state.Message ?? string.Empty,
                    candidateCount = coverage?.CandidateCount ?? 0,
                    installedCount = coverage?.InstalledCount ?? 0,
                    skippedCount = coverage?.SkippedCount ?? 0,
                    failedCount = coverage?.FailedCount ?? 0,
                    samples = coverage == null ? new List<string>() : new List<string>(coverage.Samples),
                });
            }
            return records;
        }

        public int ExportDiagnosticsJsonLines(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Export path is required.", nameof(path));

            var records = GetDiagnosticSnapshot();
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(fullPath, false))
            {
                foreach (var record in records)
                    writer.WriteLine(JsonUtility.ToJson(record));
            }
            return records.Count;
        }

        private void RefreshPointState(string pointId, UPilotMonoHookPointRuntimeState state)
        {
            if (_installers.ContainsKey(pointId) &&
                (state.InstallState == UPilotMonoHookInstallState.Installed ||
                 state.InstallState == UPilotMonoHookInstallState.PartiallyInstalled))
                return;

            var descriptor = _registry.Find(pointId);
            if (descriptor == null || !descriptor.IsValid)
            {
                state.InstallState = UPilotMonoHookInstallState.Unsupported;
                state.Message = descriptor?.DiscoveryError ?? "未发现点位 Provider";
                state.Coverage = null;
                return;
            }

            if (descriptor.Provider.IsInstalled)
            {
                SetInstalledState(state, descriptor.Provider);
                return;
            }

            var support = descriptor.Provider.CheckSupport(_registry.Context);
            if (support == null || !support.IsSupported)
            {
                state.InstallState = UPilotMonoHookInstallState.Unsupported;
                state.Message = support?.Message ?? "该点位不受支持";
                state.Coverage = GetCoverage(descriptor.Provider);
                return;
            }

            state.InstallState = UPilotMonoHookInstallState.NotInstalled;
            state.Message = "未应用";
            state.Coverage = GetCoverage(descriptor.Provider);
        }

        private bool IsInstalled(string pointId, UPilotMonoHookPointRuntimeState state)
        {
            if (_installers.ContainsKey(pointId))
                return state.InstallState == UPilotMonoHookInstallState.Installed ||
                       state.InstallState == UPilotMonoHookInstallState.PartiallyInstalled;

            var descriptor = _registry.Find(pointId);
            return descriptor != null && descriptor.Provider != null && descriptor.Provider.IsInstalled;
        }

        private void ApplyInstall(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report)
        {
            try
            {
                if (_installers.TryGetValue(pointId, out var install) && install != null)
                {
                    install();
                    MarkInstalled(pointId, state, report);
                    return;
                }

                var descriptor = _registry.Find(pointId);
                if (descriptor == null || !descriptor.IsValid)
                {
                    MarkUnsupported(pointId, state, report, descriptor?.DiscoveryError ?? "未发现点位 Provider");
                    return;
                }

                var result = descriptor.Provider.Install(_registry.Context);
                switch (result.Status)
                {
                    case UPilotMonoHookOperationStatus.Succeeded:
                    case UPilotMonoHookOperationStatus.Unchanged:
                        if (!descriptor.Provider.IsInstalled)
                        {
                            MarkFailed(pointId, state, report, "Provider 报告成功，但点位未处于已安装状态");
                            return;
                        }
                        MarkInstalled(pointId, state, report, descriptor.Provider);
                        return;
                    case UPilotMonoHookOperationStatus.Unsupported:
                        MarkUnsupported(pointId, state, report, result.Message);
                        return;
                    default:
                        MarkFailed(pointId, state, report, result.Message);
                        return;
                }
            }
            catch (Exception ex)
            {
                MarkFailed(pointId, state, report, ex.Message);
            }
        }

        private void ApplyUninstall(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report)
        {
            try
            {
                if (_uninstallers.TryGetValue(pointId, out var uninstall) && uninstall != null)
                {
                    uninstall();
                }
                else
                {
                    var descriptor = _registry.Find(pointId);
                    if (descriptor?.Provider != null && descriptor.Provider.IsInstalled)
                    {
                        var result = descriptor.Provider.Uninstall(_registry.Context);
                        if (!result.Success)
                        {
                            MarkFailed(pointId, state, report, result.Message);
                            return;
                        }
                    }
                }

                if (state != null)
                {
                    state.InstallState = UPilotMonoHookInstallState.NotInstalled;
                    state.Message = "已卸载";
                    state.Coverage = null;
                }
                report.Disabled.Add(pointId);
            }
            catch (Exception ex)
            {
                MarkFailed(pointId, state, report, ex.Message);
            }
        }

        private static void MarkInstalled(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report,
            IUPilotMonoHookPointProvider provider = null)
        {
            SetInstalledState(state, provider);
            report.Enabled.Add(pointId);
            if (state.InstallState == UPilotMonoHookInstallState.PartiallyInstalled)
                report.Partial.Add(pointId);
        }

        private static void MarkUnsupported(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report,
            string message)
        {
            state.InstallState = UPilotMonoHookInstallState.Unsupported;
            state.Message = string.IsNullOrEmpty(message) ? "该点位不受支持" : message;
            state.Coverage = null;
            report.Unsupported.Add(pointId);
            report.Failed.Add(pointId);
        }

        private static void MarkFailed(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report,
            string message)
        {
            if (state != null)
            {
                state.InstallState = UPilotMonoHookInstallState.Failed;
                state.Message = string.IsNullOrEmpty(message) ? "点位操作失败" : message;
                state.Coverage = null;
            }
            report.Failed.Add(pointId);
        }

        private static void SetInstalledState(
            UPilotMonoHookPointRuntimeState state,
            IUPilotMonoHookPointProvider provider)
        {
            var coverage = GetCoverage(provider);
            state.Coverage = coverage;
            if (coverage != null && coverage.IsPartial)
            {
                state.InstallState = UPilotMonoHookInstallState.PartiallyInstalled;
                state.Message = coverage.BuildSummary();
                return;
            }

            state.InstallState = UPilotMonoHookInstallState.Installed;
            state.Message = coverage != null ? coverage.BuildSummary() : "已安装";
        }

        private static UPilotMonoHookCoverage GetCoverage(IUPilotMonoHookPointProvider provider)
        {
            return (provider as IUPilotMonoHookCoverageProvider)?.Coverage;
        }
    }

    internal static class UPilotMonoHookAutoApply
    {
        internal static string LastResult { get; private set; } = "未调度";

        [InitializeOnLoadMethod]
        private static void ScheduleApplySavedConfiguration()
        {
            LastResult = "已调度";
            EditorApplication.update -= ApplyWhenEditorIsReady;
            EditorApplication.update += ApplyWhenEditorIsReady;
        }

        private static void ApplyWhenEditorIsReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= ApplyWhenEditorIsReady;
            ApplySavedConfiguration();
        }

        internal static void ApplySavedConfiguration()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoApplyOnEditorLoad)
            {
                LastResult = "已跳过：自动应用关闭";
                return;
            }

            try
            {
                var report = new UPilotMonoHookController().Apply(false);
                LastResult = $"已应用：启用 {report.Enabled.Count}，部分 {report.Partial.Count}，失败 {report.Failed.Count}";
            }
            catch (Exception ex)
            {
                LastResult = "失败：" + ex.Message;
                Debug.LogWarning("[UPilot MonoHook] 自动应用失败：" + ex.Message);
            }
        }
    }
}
