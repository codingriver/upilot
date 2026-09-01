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
        public bool SupportsHookAllSafeOverloads;
        public bool ConfiguredHookAllSafeOverloads;
        public bool AppliedHookAllSafeOverloads;
        public UPilotMonoHookExecutionMode ConfiguredExecutionMode;
        public UPilotMonoHookExecutionMode AppliedExecutionMode;
        public bool GuaranteesPassThrough;
        public bool SupportsInterception;
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
    public sealed class UPilotMonoHookDiagnosticInstallEntry
    {
        public string targetTypeName;
        public string declaringTypeName;
        public string methodSignature;
        public string targetMethodId;
        public string status;
        public string reason;
        public string trampolineKey;
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
        public bool supportsHookAllSafeOverloads;
        public bool configuredHookAllSafeOverloads;
        public bool appliedHookAllSafeOverloads;
        public string configuredExecutionMode;
        public string appliedExecutionMode;
        public bool guaranteesPassThrough;
        public bool supportsInterception;
        public string configuredFilterProfileId;
        public string configuredFilterProfileName;
        public string effectiveFilterProfileId;
        public string effectiveFilterProfileName;
        public long filterEvaluated;
        public long filterAccepted;
        public long filterRejected;
        public string filterLastReason;
        public string installState;
        public string message;
        public int candidateCount;
        public int installedCount;
        public int installedTypeCount;
        public int installedMethodCount;
        public int trampolineCount;
        public int skippedCount;
        public int failedCount;
        public List<string> samples = new List<string>();
        public List<UPilotMonoHookDiagnosticInstallEntry> entries =
            new List<UPilotMonoHookDiagnosticInstallEntry>();
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
            if (UPilotMonoHookCatalog.Find(pointId) == null) throw new ArgumentException("Unknown trace point: " + pointId, nameof(pointId));
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
                state.ConfiguredHookAllSafeOverloads = settings.ShouldHookAllSafeOverloads(definition.Id);
                state.ConfiguredExecutionMode = settings.GetExecutionMode(definition.Id);
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

                if (shouldInstall && isInstalled && HasPendingAppliedConfiguration(definition.Id))
                {
                    ApplyReinstall(definition.Id, state, report);
                    continue;
                }

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
                var settings = UPilotMonoHookSettings.instance;
                string configuredProfileId = settings.GetConfiguredFilterProfileId(definition.Id);
                string effectiveProfileId = settings.GetEffectiveFilterProfileId(definition.Id);
                var configuredProfile = settings.FindFilterProfile(configuredProfileId);
                var effectiveProfile = settings.FindFilterProfile(effectiveProfileId);
                var statistics = UPilotTraceFilterEngine.GetStatistics(definition.Id, effectiveProfileId);
                records.Add(new UPilotMonoHookDiagnosticRecord
                {
                    generatedAtUtc = generatedAtUtc,
                    unityVersion = Application.unityVersion,
                    pointId = definition.Id,
                    displayName = definition.DisplayName,
                    categoryId = definition.CategoryId,
                    configuredEnabled = state.ConfiguredEnabled,
                    supportsHookAllSafeOverloads = state.SupportsHookAllSafeOverloads,
                    configuredHookAllSafeOverloads = state.ConfiguredHookAllSafeOverloads,
                    appliedHookAllSafeOverloads = state.AppliedHookAllSafeOverloads,
                    configuredExecutionMode = state.ConfiguredExecutionMode.ToString(),
                    appliedExecutionMode = state.AppliedExecutionMode.ToString(),
                    guaranteesPassThrough = state.GuaranteesPassThrough,
                    supportsInterception = state.SupportsInterception,
                    configuredFilterProfileId = configuredProfileId,
                    configuredFilterProfileName = configuredProfile?.Name ?? string.Empty,
                    effectiveFilterProfileId = effectiveProfileId,
                    effectiveFilterProfileName = effectiveProfile?.Name ?? string.Empty,
                    filterEvaluated = statistics?.evaluated ?? 0,
                    filterAccepted = statistics?.accepted ?? 0,
                    filterRejected = statistics?.rejected ?? 0,
                    filterLastReason = statistics?.lastReason ?? string.Empty,
                    installState = state.InstallState.ToString(),
                    message = state.Message ?? string.Empty,
                    candidateCount = coverage?.CandidateCount ?? 0,
                    installedCount = coverage?.InstalledCount ?? 0,
                    installedTypeCount = coverage?.InstalledTypeCount ?? 0,
                    installedMethodCount = coverage?.InstalledMethodCount ?? 0,
                    trampolineCount = coverage?.TrampolineCount ?? 0,
                    skippedCount = coverage?.SkippedCount ?? 0,
                    failedCount = coverage?.FailedCount ?? 0,
                    samples = coverage == null ? new List<string>() : new List<string>(coverage.Samples),
                    entries = BuildDiagnosticEntries(coverage),
                });
            }
            return records;
        }

        private static List<UPilotMonoHookDiagnosticInstallEntry> BuildDiagnosticEntries(
            UPilotMonoHookCoverage coverage)
        {
            var entries = new List<UPilotMonoHookDiagnosticInstallEntry>();
            if (coverage?.Entries == null) return entries;
            foreach (var entry in coverage.Entries)
            {
                if (entry == null) continue;
                entries.Add(new UPilotMonoHookDiagnosticInstallEntry
                {
                    targetTypeName = entry.TargetTypeName,
                    declaringTypeName = entry.DeclaringTypeName,
                    methodSignature = entry.MethodSignature,
                    targetMethodId = entry.TargetMethodId,
                    status = entry.Status,
                    reason = entry.Reason,
                    trampolineKey = entry.TrampolineKey,
                });
            }
            return entries;
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
                state.SupportsHookAllSafeOverloads = false;
                state.AppliedHookAllSafeOverloads = false;
                state.InstallState = UPilotMonoHookInstallState.Unsupported;
                state.Message = descriptor?.DiscoveryError ?? "未发现点位 Provider";
                state.Coverage = null;
                return;
            }

            ConfigureOverloadPolicy(pointId, descriptor.Provider, state);
            ConfigureExecutionPolicy(pointId, descriptor.Provider, state);

            if (descriptor.Provider.IsInstalled)
            {
                if (HasPendingAppliedConfiguration(pointId))
                {
                    state.InstallState = UPilotMonoHookInstallState.NotInstalled;
                    state.Message = "未应用";
                    state.Coverage = GetCoverage(descriptor.Provider);
                    return;
                }
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

        private bool HasPendingAppliedConfiguration(string pointId)
        {
            if (_installers.ContainsKey(pointId)) return false;
            var descriptor = _registry.Find(pointId);
            var policy = descriptor?.Provider as IUPilotMonoHookOverloadPolicyProvider;
            bool overloadPending = policy != null &&
                   policy.SupportsHookAllSafeOverloads &&
                   policy.IsHookAllSafeOverloadsApplied !=
                   UPilotMonoHookSettings.instance.ShouldHookAllSafeOverloads(pointId);
            bool executionPending = _runtime.TryGetValue(pointId, out var state) &&
                                     state.AppliedExecutionMode !=
                                     UPilotMonoHookSettings.instance.GetExecutionMode(pointId);
            return overloadPending || executionPending;
        }

        private static void ConfigureOverloadPolicy(
            string pointId,
            IUPilotMonoHookPointProvider provider,
            UPilotMonoHookPointRuntimeState state = null)
        {
            var policy = provider as IUPilotMonoHookOverloadPolicyProvider;
            bool supports = policy != null && policy.SupportsHookAllSafeOverloads;
            bool configured = supports &&
                              UPilotMonoHookSettings.instance.ShouldHookAllSafeOverloads(pointId);
            if (policy != null)
                policy.HookAllSafeOverloads = configured;
            if (state == null) return;
            state.SupportsHookAllSafeOverloads = supports;
            state.ConfiguredHookAllSafeOverloads = configured;
            state.AppliedHookAllSafeOverloads = supports &&
                                                policy.IsHookAllSafeOverloadsApplied;
        }

        private void ApplyReinstall(
            string pointId,
            UPilotMonoHookPointRuntimeState state,
            UPilotMonoHookApplyReport report)
        {
            var uninstallReport = new UPilotMonoHookApplyReport();
            ApplyUninstall(pointId, state, uninstallReport);
            if (uninstallReport.Failed.Count > 0)
            {
                report.Failed.AddRange(uninstallReport.Failed);
                return;
            }

            ApplyInstall(pointId, state, report);
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

                ConfigureOverloadPolicy(pointId, descriptor.Provider, state);
                ConfigureExecutionPolicy(pointId, descriptor.Provider, state);
                if (!CanInstallWithExecutionMode(pointId, descriptor.Provider, out var executionReason))
                {
                    MarkUnsupported(pointId, state, report, executionReason);
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
            var overloadPolicy = provider as IUPilotMonoHookOverloadPolicyProvider;
            if (overloadPolicy != null)
            {
                state.SupportsHookAllSafeOverloads = overloadPolicy.SupportsHookAllSafeOverloads;
                state.AppliedHookAllSafeOverloads =
                    overloadPolicy.SupportsHookAllSafeOverloads &&
                    overloadPolicy.IsHookAllSafeOverloadsApplied;
            }
            var coverage = GetCoverage(provider);
            state.Coverage = coverage;
            state.AppliedExecutionMode = UPilotMonoHookSettings.instance.GetExecutionMode(state.PointId);
            if (coverage != null && coverage.IsPartial)
            {
                state.InstallState = UPilotMonoHookInstallState.PartiallyInstalled;
                state.Message = coverage.BuildSummary();
                return;
            }

            state.InstallState = UPilotMonoHookInstallState.Installed;
            state.Message = coverage != null ? coverage.BuildSummary() : "已安装";
        }

        private static void ConfigureExecutionPolicy(
            string pointId,
            IUPilotMonoHookPointProvider provider,
            UPilotMonoHookPointRuntimeState state = null)
        {
            var policy = provider as IUPilotMonoHookExecutionPolicyProvider;
            var configured = UPilotMonoHookSettings.instance.GetExecutionMode(pointId);
            if (policy != null)
                policy.ExecutionMode = configured;
            bool guaranteesPassThrough = policy != null && policy.GuaranteesPassThrough;
            bool supportsInterception = policy != null && policy.SupportsInterception;
            if (state == null) return;
            state.GuaranteesPassThrough = guaranteesPassThrough;
            state.SupportsInterception = supportsInterception;
        }

        private static bool CanInstallWithExecutionMode(
            string pointId,
            IUPilotMonoHookPointProvider provider,
            out string reason)
        {
            reason = string.Empty;
            var configured = UPilotMonoHookSettings.instance.GetExecutionMode(pointId);
            var policy = provider as IUPilotMonoHookExecutionPolicyProvider;

            if (configured == UPilotMonoHookExecutionMode.PassThrough)
            {
                if (policy != null && policy.GuaranteesPassThrough)
                    return true;
                reason = "默认仅允许透传打点；Provider 未声明 GuaranteesPassThrough。";
                return false;
            }

            if (configured == UPilotMonoHookExecutionMode.Intercept)
            {
                if (policy != null && policy.SupportsInterception)
                    return true;
                reason = "该点位未声明支持行为拦截。";
                return false;
            }

            reason = "未知的点位执行策略：" + configured;
            return false;
        }

        private static UPilotMonoHookCoverage GetCoverage(IUPilotMonoHookPointProvider provider)
        {
            return (provider as IUPilotMonoHookCoverageProvider)?.Coverage;
        }
    }

    internal static class UPilotMonoHookAutoApply
    {
        private const string PendingPlayModeApplyKey = "UPilot.MonoHook.Tracing.PendingPlayModeApply";

        internal static string LastResult { get; private set; } = "未调度";

        [InitializeOnLoadMethod]
        private static void ScheduleApplySavedConfiguration()
        {
            LastResult = "已调度";
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoInjectEnabled)
            {
                CancelPendingPlayModeApply("已跳过：自动注入关闭");
                return;
            }

            if (SessionState.GetBool(PendingPlayModeApplyKey, false))
            {
                EditorApplication.delayCall -= ApplyPendingPlayModeConfiguration;
                EditorApplication.delayCall += ApplyPendingPlayModeConfiguration;
                return;
            }

            EditorApplication.update -= ApplyWhenEditorIsReady;
            EditorApplication.update += ApplyWhenEditorIsReady;
        }

        private static void ApplyWhenEditorIsReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= ApplyWhenEditorIsReady;
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoInjectEnabled)
            {
                CancelPendingPlayModeApply("已跳过：自动注入关闭");
                return;
            }
            if (SessionState.GetBool(PendingPlayModeApplyKey, false))
                ApplyPlayModeConfiguration(true);
            else
                ApplySavedConfiguration();
        }

        internal static void ApplySavedConfiguration()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoInjectEnabled)
            {
                CancelPendingPlayModeApply("已跳过：自动注入关闭");
                return;
            }
            if (!settings.autoApplyOnEditorLoad)
            {
                LastResult = "已跳过：Domain Reload 自动注入关闭";
                return;
            }

            try
            {
                var report = new UPilotMonoHookController().Apply(false);
                LastResult = $"已自动注入：启用 {report.Enabled.Count}，部分 {report.Partial.Count}，失败 {report.Failed.Count}";
            }
            catch (Exception ex)
            {
                LastResult = "失败：" + ex.Message;
                Debug.LogWarning("[UPilot Trace] 自动注入失败：" + ex.Message);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();

            if (!settings.autoInjectEnabled)
            {
                CancelPendingPlayModeApply("已跳过：自动注入关闭");
                return;
            }

            if (state == PlayModeStateChange.ExitingEditMode)
            {
                if (!settings.autoApplyOnPlayMode)
                {
                    SessionState.SetBool(PendingPlayModeApplyKey, false);
                    return;
                }

                // Keep the marker until EnteredPlayMode. If a Domain Reload occurs,
                // the marker survives and the new editor domain reapplies the hooks.
                SessionState.SetBool(PendingPlayModeApplyKey, true);
                ApplyPlayModeConfiguration(false);
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (settings.autoApplyOnPlayMode &&
                    SessionState.GetBool(PendingPlayModeApplyKey, false))
                {
                    ApplyPlayModeConfiguration(true);
                }
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode)
                SessionState.SetBool(PendingPlayModeApplyKey, false);
        }

        private static void ApplyPendingPlayModeConfiguration()
        {
            EditorApplication.delayCall -= ApplyPendingPlayModeConfiguration;
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoInjectEnabled || !settings.autoApplyOnPlayMode)
            {
                CancelPendingPlayModeApply(!settings.autoInjectEnabled
                    ? "已跳过：自动注入关闭"
                    : "已跳过：PlayMode 自动注入关闭");
                return;
            }
            if (!SessionState.GetBool(PendingPlayModeApplyKey, false))
                return;

            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetBool(PendingPlayModeApplyKey, false);
                LastResult = "已取消：PlayMode 未继续";
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += ApplyPendingPlayModeConfiguration;
                return;
            }

            ApplyPlayModeConfiguration(true);
        }

        private static void ApplyPlayModeConfiguration(bool clearPendingMarker)
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (!settings.autoInjectEnabled || !settings.autoApplyOnPlayMode)
            {
                CancelPendingPlayModeApply(!settings.autoInjectEnabled
                    ? "已跳过：自动注入关闭"
                    : "已跳过：PlayMode 自动注入关闭");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall -= ApplyPendingPlayModeConfiguration;
                EditorApplication.delayCall += ApplyPendingPlayModeConfiguration;
                return;
            }

            try
            {
                var report = new UPilotMonoHookController().Apply(false);
                LastResult = $"PlayMode 自动注入：启用 {report.Enabled.Count}，部分 {report.Partial.Count}，失败 {report.Failed.Count}";
                if (clearPendingMarker)
                    SessionState.SetBool(PendingPlayModeApplyKey, false);
            }
            catch (Exception ex)
            {
                LastResult = "PlayMode 自动注入失败：" + ex.Message;
                Debug.LogWarning("[UPilot Trace] PlayMode 自动注入失败：" + ex.Message);
            }
        }

        private static void CancelPendingPlayModeApply(string result)
        {
            SessionState.SetBool(PendingPlayModeApplyKey, false);
            EditorApplication.delayCall -= ApplyPendingPlayModeConfiguration;
            EditorApplication.update -= ApplyWhenEditorIsReady;
            LastResult = result;
        }
    }
}
