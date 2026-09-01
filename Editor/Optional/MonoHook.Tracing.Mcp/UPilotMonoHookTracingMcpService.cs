// -----------------------------------------------------------------------
// UPilot Editor - thin MCP adapter for manually managed MonoHook tracing.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class UPilotMonoHookTracingConfigureMessage
    {
        public UPilotMonoHookTracingConfigurePayload payload;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingConfigurePayload
    {
        public bool setMasterEnabled;
        public bool masterEnabled;
        public bool updateAutoInjectEnabled;
        public bool autoInjectEnabled;
        public bool updateAutoApplyOnPlayMode;
        public bool autoApplyOnPlayMode;
        public string[] pointIds;
        public bool updatePointEnabled;
        public bool enabled;
        public bool updateExecutionMode;
        public UPilotMonoHookExecutionMode executionMode;
        public bool setStackTraceCaptureMode;
        public UPilotStackTraceCaptureMode stackTraceCaptureMode;
        public bool updatePointStackTraceSelection;
        public bool updateCaptureStackTrace;
        public bool captureStackTrace;
        public bool updatePerObjectRateLimit;
        public bool enablePerObjectRateLimit;
        public int maxEventsPerObjectPerSecond;
        public bool updateDuplicateSuppression;
        public bool suppressDuplicateEvents;
        public int duplicateEventWindowMilliseconds;
        public bool setGlobalFilterProfile;
        public string globalFilterProfileId;
        public bool updatePointFilterOverridesEnabled;
        public bool pointFilterOverridesEnabled;
        public bool updatePointFilterProfile;
        public string pointFilterProfileId;
        public bool replaceFilterProfiles;
        public UPilotTraceFilterProfile[] filterProfiles;
        public bool resetFilterStatistics;
        public bool apply;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingEventsMessage
    {
        public UPilotMonoHookTracingEventsPayload payload;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingEventsPayload
    {
        public int maxCount = 100;
        public bool consume;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingInstallEntryPayload
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
    public sealed class UPilotMonoHookTracingPointPayload
    {
        public string pointId;
        public string displayName;
        public string categoryId;
        public bool highFrequency;
        public bool configuredEnabled;
        public string configuredExecutionMode;
        public string appliedExecutionMode;
        public bool guaranteesPassThrough;
        public bool supportsInterception;
        public bool captureStackTraceSelected;
        public bool captureStackTraceEffective;
        public string configuredFilterProfileId;
        public string effectiveFilterProfileId;
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
        public List<UPilotMonoHookTracingInstallEntryPayload> entries =
            new List<UPilotMonoHookTracingInstallEntryPayload>();
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingStatusPayload
    {
        public bool ok = true;
        public bool masterEnabled;
        public bool autoInjectEnabled;
        public bool autoApplyOnEditorLoad;
        public bool autoApplyOnPlayMode;
        public int maxEventsPerSecond;
        public bool enablePerObjectRateLimit;
        public int maxEventsPerObjectPerSecond;
        public bool suppressDuplicateEvents;
        public int duplicateEventWindowMilliseconds;
        public bool captureStackTrace;
        public string stackTraceCaptureMode;
        public int stackTraceMaxFrames;
        public int stackTraceSampleEveryN;
        public string globalFilterProfileId;
        public bool pointFilterOverridesEnabled;
        public List<UPilotTraceFilterProfile> filterProfiles = new List<UPilotTraceFilterProfile>();
        public List<UPilotTraceFilterStatistics> filterStatistics = new List<UPilotTraceFilterStatistics>();
        public int eventCount;
        public int droppedCount;
        public int consoleDroppedCount;
        public int perObjectDroppedCount;
        public int duplicateDroppedCount;
        public int traceFailureCount;
        public List<UPilotMonoHookTracingPointPayload> points = new List<UPilotMonoHookTracingPointPayload>();
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingConfigureResultPayload
    {
        public bool ok = true;
        public bool applied;
        public List<string> changedPointIds = new List<string>();
        public List<string> enabled = new List<string>();
        public List<string> disabled = new List<string>();
        public List<string> unchanged = new List<string>();
        public List<string> partial = new List<string>();
        public List<string> unsupported = new List<string>();
        public List<string> failed = new List<string>();
        public UPilotMonoHookTracingStatusPayload status;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingEventsResultPayload
    {
        public bool ok = true;
        public bool consumed;
        public int count;
        public int remainingCount;
        public int droppedCount;
        public List<UPilotMonoHookEvent> items = new List<UPilotMonoHookEvent>();
    }

    public sealed class UPilotMonoHookTracingMcpService
    {
        private readonly UPilotBridge _bridge;
        private readonly UPilotMonoHookController _controller = new UPilotMonoHookController();

        public UPilotMonoHookTracingMcpService(UPilotBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("monohook.tracing.status", HandleStatusAsync);
            _bridge.Router.Register("monohook.tracing.configure", HandleConfigureAsync);
            _bridge.Router.Register("monohook.tracing.events", HandleEventsAsync);
        }

        private Task HandleStatusAsync(string id, string json, CancellationToken token) =>
            RunOnMainThread(id, "monohook.tracing.status", BuildStatus, token);

        private Task HandleConfigureAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UPilotMonoHookTracingConfigureMessage>(json)?.payload ??
                          new UPilotMonoHookTracingConfigurePayload();
            return RunOnMainThread(id, "monohook.tracing.configure", () => Configure(payload), token);
        }

        private Task HandleEventsAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UPilotMonoHookTracingEventsMessage>(json)?.payload ??
                          new UPilotMonoHookTracingEventsPayload();
            return RunOnMainThread(id, "monohook.tracing.events", () => ReadEvents(payload), token);
        }

        private async Task RunOnMainThread<T>(string id, string command, Func<T> action, CancellationToken token)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, command, await completion.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MONOHOOK_TRACING_FAILED", ex.GetBaseException().Message, token, command);
            }
        }

        internal UPilotMonoHookTracingStatusPayload BuildStatus()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _controller.RefreshRuntime();

            var result = new UPilotMonoHookTracingStatusPayload
            {
                masterEnabled = settings.masterEnabled,
                autoInjectEnabled = settings.autoInjectEnabled,
                autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad,
                autoApplyOnPlayMode = settings.autoApplyOnPlayMode,
                maxEventsPerSecond = settings.maxEventsPerSecond,
                enablePerObjectRateLimit = settings.enablePerObjectRateLimit,
                maxEventsPerObjectPerSecond = settings.maxEventsPerObjectPerSecond,
                suppressDuplicateEvents = settings.suppressDuplicateEvents,
                duplicateEventWindowMilliseconds = settings.duplicateEventWindowMilliseconds,
                captureStackTrace = settings.captureStackTrace,
                stackTraceCaptureMode = settings.stackTraceCaptureMode.ToString(),
                stackTraceMaxFrames = settings.stackTraceMaxFrames,
                stackTraceSampleEveryN = settings.stackTraceSampleEveryN,
                globalFilterProfileId = settings.globalFilterProfileId,
                pointFilterOverridesEnabled = settings.pointFilterOverridesEnabled,
                eventCount = UPilotMonoHookTelemetry.Count,
                droppedCount = UPilotMonoHookTelemetry.DroppedCount,
                consoleDroppedCount = UPilotMonoHookTelemetry.ConsoleDroppedCount,
                perObjectDroppedCount = UPilotMonoHookTelemetry.PerObjectDroppedCount,
                duplicateDroppedCount = UPilotMonoHookTelemetry.DuplicateDroppedCount,
                traceFailureCount = UPilotMonoHookTelemetry.TraceFailureCount,
            };
            result.filterProfiles.AddRange(settings.filterProfiles ?? new List<UPilotTraceFilterProfile>());
            result.filterStatistics.AddRange(UPilotTraceFilterEngine.SnapshotStatistics());

            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                _controller.Runtime.TryGetValue(definition.Id, out var runtime);
                string effectiveFilterId = settings.GetEffectiveFilterProfileId(definition.Id);
                var filterStats = UPilotTraceFilterEngine.GetStatistics(definition.Id, effectiveFilterId);
                var coverage = runtime?.Coverage;
                result.points.Add(new UPilotMonoHookTracingPointPayload
                {
                    pointId = definition.Id,
                    displayName = definition.DisplayName,
                    categoryId = definition.CategoryId,
                    highFrequency = definition.HighFrequency,
                    configuredEnabled = settings.IsConfiguredEnabled(definition.Id),
                    configuredExecutionMode = settings.GetExecutionMode(definition.Id).ToString(),
                    appliedExecutionMode = runtime?.AppliedExecutionMode.ToString() ?? UPilotMonoHookExecutionMode.PassThrough.ToString(),
                    guaranteesPassThrough = runtime?.GuaranteesPassThrough ?? false,
                    supportsInterception = runtime?.SupportsInterception ?? false,
                    captureStackTraceSelected = settings.points.FirstOrDefault(point =>
                        point != null && string.Equals(point.Id, definition.Id, StringComparison.Ordinal))?.CaptureStackTrace ?? false,
                    captureStackTraceEffective = settings.ShouldCaptureStackTrace(definition.Id),
                    configuredFilterProfileId = settings.GetConfiguredFilterProfileId(definition.Id),
                    effectiveFilterProfileId = effectiveFilterId,
                    filterEvaluated = filterStats?.evaluated ?? 0,
                    filterAccepted = filterStats?.accepted ?? 0,
                    filterRejected = filterStats?.rejected ?? 0,
                    filterLastReason = filterStats?.lastReason ?? string.Empty,
                    installState = runtime?.InstallState.ToString() ?? UPilotMonoHookInstallState.NotInstalled.ToString(),
                    message = runtime?.Message ?? string.Empty,
                    candidateCount = coverage?.CandidateCount ?? 0,
                    installedCount = coverage?.InstalledCount ?? 0,
                    installedTypeCount = coverage?.InstalledTypeCount ?? 0,
                    installedMethodCount = coverage?.InstalledMethodCount ?? 0,
                    trampolineCount = coverage?.TrampolineCount ?? 0,
                    skippedCount = coverage?.SkippedCount ?? 0,
                    failedCount = coverage?.FailedCount ?? 0,
                });
                var point = result.points[result.points.Count - 1];
                if (coverage?.Entries != null)
                {
                    foreach (var entry in coverage.Entries)
                    {
                        if (entry == null) continue;
                        point.entries.Add(new UPilotMonoHookTracingInstallEntryPayload
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
                }
            }
            return result;
        }

        internal UPilotMonoHookTracingConfigureResultPayload Configure(UPilotMonoHookTracingConfigurePayload payload)
        {
            payload ??= new UPilotMonoHookTracingConfigurePayload();
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            var pointIds = (payload.pointIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var unknown = pointIds.Where(id => UPilotMonoHookCatalog.Find(id) == null).ToArray();
            if (unknown.Length > 0)
                throw new ArgumentException("未知追踪点位：" + string.Join(", ", unknown));

            // Validate profile references before mutating settings. A rejected MCP
            // request must not leave a partially-applied global/profile selection.
            IEnumerable<UPilotTraceFilterProfile> candidateProfiles =
                payload.replaceFilterProfiles && payload.filterProfiles != null
                    ? payload.filterProfiles
                    : (settings.filterProfiles ?? new List<UPilotTraceFilterProfile>());
            var availableProfileIds = new HashSet<string>(
                candidateProfiles
                .Where(profile => profile != null && !string.IsNullOrEmpty(profile.Id))
                .Select(profile => profile.Id),
                StringComparer.Ordinal);
            if (payload.setGlobalFilterProfile &&
                !string.IsNullOrEmpty(payload.globalFilterProfileId) &&
                !string.Equals(payload.globalFilterProfileId, UPilotTraceFilterProfileIds.None, StringComparison.Ordinal) &&
                !availableProfileIds.Contains(payload.globalFilterProfileId))
                throw new ArgumentException("未知追踪器过滤器：" + payload.globalFilterProfileId);
            if (payload.updatePointFilterProfile &&
                !string.IsNullOrEmpty(payload.pointFilterProfileId) &&
                !string.Equals(payload.pointFilterProfileId, UPilotTraceFilterProfileIds.None, StringComparison.Ordinal) &&
                !availableProfileIds.Contains(payload.pointFilterProfileId))
                throw new ArgumentException("未知追踪器过滤器：" + payload.pointFilterProfileId);
            if ((payload.updatePointStackTraceSelection || payload.updatePointFilterProfile) && pointIds.Length == 0)
                throw new ArgumentException("更新点位堆栈或过滤器覆盖时必须提供 pointIds。");
            var result = new UPilotMonoHookTracingConfigureResultPayload();
            if (payload.setMasterEnabled)
                settings.masterEnabled = payload.masterEnabled;
            if (payload.updateAutoInjectEnabled)
                settings.autoInjectEnabled = payload.autoInjectEnabled;
            if (payload.updateAutoApplyOnPlayMode)
                settings.autoApplyOnPlayMode = payload.autoApplyOnPlayMode;
            if (payload.setStackTraceCaptureMode)
                settings.stackTraceCaptureMode = payload.stackTraceCaptureMode;
            else if (payload.updateCaptureStackTrace)
                settings.SetCaptureStackTrace(payload.captureStackTrace);
            if (payload.updatePerObjectRateLimit)
            {
                settings.enablePerObjectRateLimit = payload.enablePerObjectRateLimit;
                settings.maxEventsPerObjectPerSecond = payload.maxEventsPerObjectPerSecond;
            }
            if (payload.updateDuplicateSuppression)
            {
                settings.suppressDuplicateEvents = payload.suppressDuplicateEvents;
                settings.duplicateEventWindowMilliseconds = payload.duplicateEventWindowMilliseconds;
            }
            if (payload.setGlobalFilterProfile)
                settings.globalFilterProfileId = string.IsNullOrEmpty(payload.globalFilterProfileId)
                    ? UPilotTraceFilterProfileIds.None
                    : payload.globalFilterProfileId;
            if (payload.updatePointFilterOverridesEnabled)
                settings.pointFilterOverridesEnabled = payload.pointFilterOverridesEnabled;
            if (payload.replaceFilterProfiles && payload.filterProfiles != null)
                settings.filterProfiles = payload.filterProfiles.ToList();
            settings.EnsureDefaults();
            foreach (var pointId in pointIds)
            {
                if (payload.updatePointEnabled)
                    settings.SetEnabled(pointId, payload.enabled);
                if (payload.updateExecutionMode)
                    settings.SetExecutionMode(pointId, payload.executionMode);
                if (payload.updatePointStackTraceSelection)
                    settings.SetCaptureStackTrace(pointId, payload.captureStackTrace);
                if (payload.updatePointFilterProfile)
                    settings.SetFilterProfileId(pointId, payload.pointFilterProfileId);
                result.changedPointIds.Add(pointId);
            }
            if (payload.resetFilterStatistics)
                UPilotTraceFilterEngine.ClearStatistics();
            settings.SaveSettings();

            if (payload.apply)
            {
                var report = _controller.Apply(false);
                result.applied = true;
                result.enabled.AddRange(report.Enabled);
                result.disabled.AddRange(report.Disabled);
                result.unchanged.AddRange(report.Unchanged);
                result.partial.AddRange(report.Partial);
                result.unsupported.AddRange(report.Unsupported);
                result.failed.AddRange(report.Failed);
            }
            result.status = BuildStatus();
            return result;
        }

        internal static UPilotMonoHookTracingEventsResultPayload ReadEvents(UPilotMonoHookTracingEventsPayload payload)
        {
            payload ??= new UPilotMonoHookTracingEventsPayload();
            int maxCount = Math.Max(1, Math.Min(1000, payload.maxCount <= 0 ? 100 : payload.maxCount));
            var items = payload.consume
                ? UPilotMonoHookTelemetry.Read(maxCount)
                : UPilotMonoHookTelemetry.Snapshot(maxCount);
            return new UPilotMonoHookTracingEventsResultPayload
            {
                consumed = payload.consume,
                count = items.Count,
                remainingCount = UPilotMonoHookTelemetry.Count,
                droppedCount = UPilotMonoHookTelemetry.DroppedCount,
                items = items,
            };
        }
    }

    [InitializeOnLoad]
    internal static class UPilotMonoHookTracingMcpBootstrap
    {
        private static UPilotMonoHookTracingMcpService _service;

        static UPilotMonoHookTracingMcpBootstrap()
        {
            EditorApplication.delayCall += Register;
        }

        private static void Register()
        {
            if (_service != null) return;
            _service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            _service.RegisterCommands();
        }
    }
}
