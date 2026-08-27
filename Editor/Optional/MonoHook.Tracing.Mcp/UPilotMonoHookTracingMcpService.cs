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
        public string[] pointIds;
        public bool enabled;
        public bool updateCaptureStackTrace;
        public bool captureStackTrace;
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
    public sealed class UPilotMonoHookTracingPointPayload
    {
        public string pointId;
        public string displayName;
        public string categoryId;
        public bool highFrequency;
        public bool configuredEnabled;
        public bool captureStackTrace;
        public string installState;
        public string message;
    }

    [Serializable]
    public sealed class UPilotMonoHookTracingStatusPayload
    {
        public bool ok = true;
        public bool masterEnabled;
        public bool autoApplyOnEditorLoad;
        public int maxEventsPerSecond;
        public int stackTraceMaxFrames;
        public int stackTraceSampleEveryN;
        public int eventCount;
        public int droppedCount;
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
                autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad,
                maxEventsPerSecond = settings.maxEventsPerSecond,
                stackTraceMaxFrames = settings.stackTraceMaxFrames,
                stackTraceSampleEveryN = settings.stackTraceSampleEveryN,
                eventCount = UPilotMonoHookTelemetry.Count,
                droppedCount = UPilotMonoHookTelemetry.DroppedCount,
            };

            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                _controller.Runtime.TryGetValue(definition.Id, out var runtime);
                result.points.Add(new UPilotMonoHookTracingPointPayload
                {
                    pointId = definition.Id,
                    displayName = definition.DisplayName,
                    categoryId = definition.CategoryId,
                    highFrequency = definition.HighFrequency,
                    configuredEnabled = settings.IsConfiguredEnabled(definition.Id),
                    captureStackTrace = settings.ShouldCaptureStackTrace(definition.Id),
                    installState = runtime?.InstallState.ToString() ?? UPilotMonoHookInstallState.NotInstalled.ToString(),
                    message = runtime?.Message ?? string.Empty,
                });
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

            var result = new UPilotMonoHookTracingConfigureResultPayload();
            if (payload.setMasterEnabled)
                settings.masterEnabled = payload.masterEnabled;
            foreach (var pointId in pointIds)
            {
                settings.SetEnabled(pointId, payload.enabled);
                if (payload.updateCaptureStackTrace)
                    settings.SetCaptureStackTrace(pointId, payload.captureStackTrace);
                result.changedPointIds.Add(pointId);
            }
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
