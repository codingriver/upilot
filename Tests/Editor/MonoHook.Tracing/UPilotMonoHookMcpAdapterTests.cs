// -----------------------------------------------------------------------
// UPilot Editor tests - thin MonoHook tracing MCP adapter.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookMcpAdapterTests
    {
        private bool _masterEnabled;
        private bool _autoInjectEnabled;
        private bool _autoApplyOnPlayMode;
        private List<UPilotMonoHookPointState> _points;
        private string _globalFilterProfileId;
        private List<UPilotTraceFilterProfile> _filterProfiles;
        private bool _enablePerObjectRateLimit;
        private int _maxEventsPerObjectPerSecond;
        private bool _suppressDuplicateEvents;
        private int _duplicateEventWindowMilliseconds;
        private bool _captureStackTrace;
        private UPilotStackTraceCaptureMode _stackTraceCaptureMode;
        private bool _pointFilterOverridesEnabled;

        [SetUp]
        public void SetUp()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _autoInjectEnabled = settings.autoInjectEnabled;
            _autoApplyOnPlayMode = settings.autoApplyOnPlayMode;
            _globalFilterProfileId = settings.globalFilterProfileId;
            _enablePerObjectRateLimit = settings.enablePerObjectRateLimit;
            _maxEventsPerObjectPerSecond = settings.maxEventsPerObjectPerSecond;
            _suppressDuplicateEvents = settings.suppressDuplicateEvents;
            _duplicateEventWindowMilliseconds = settings.duplicateEventWindowMilliseconds;
            _captureStackTrace = settings.captureStackTrace;
            _stackTraceCaptureMode = settings.stackTraceCaptureMode;
            _pointFilterOverridesEnabled = settings.pointFilterOverridesEnabled;
            _filterProfiles = (settings.filterProfiles ?? new List<UPilotTraceFilterProfile>())
                .Where(profile => profile != null)
                .Select(profile => JsonUtility.FromJson<UPilotTraceFilterProfile>(JsonUtility.ToJson(profile)))
                .ToList();
            var knownFilterProfileIds = new HashSet<string>(_filterProfiles.Select(profile => profile.Id), System.StringComparer.Ordinal);
            _points = settings.points
                .Where(point => point != null)
                .Select(point => new UPilotMonoHookPointState(
                    point.Id,
                    point.Enabled,
                    point.CaptureStackTrace,
                    point.HookAllSafeOverloads,
                    knownFilterProfileIds.Contains(point.FilterProfileId) ? point.FilterProfileId : string.Empty,
                    point.ExecutionMode))
                .ToList();
            new UPilotMonoHookController().UninstallAll();
        }

        [TearDown]
        public void TearDown()
        {
            new UPilotMonoHookController().UninstallAll();
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.autoInjectEnabled = _autoInjectEnabled;
            settings.autoApplyOnPlayMode = _autoApplyOnPlayMode;
            settings.globalFilterProfileId = _globalFilterProfileId;
            settings.enablePerObjectRateLimit = _enablePerObjectRateLimit;
            settings.maxEventsPerObjectPerSecond = _maxEventsPerObjectPerSecond;
            settings.suppressDuplicateEvents = _suppressDuplicateEvents;
            settings.duplicateEventWindowMilliseconds = _duplicateEventWindowMilliseconds;
            settings.captureStackTrace = _captureStackTrace;
            settings.stackTraceCaptureMode = _stackTraceCaptureMode;
            settings.pointFilterOverridesEnabled = _pointFilterOverridesEnabled;
            settings.filterProfiles = _filterProfiles;
            settings.points = _points;
            settings.EnsureDefaults();
            settings.SaveSettings();
            UPilotMonoHookTelemetry.Clear();
        }

        [Test]
        public void ConfigureDefaultsToSavingWithoutApplyingHooks()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);

            var result = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                pointIds = new[] { UPilotMonoHookPointId.LifecycleUpdate },
                updatePointEnabled = true,
                enabled = true,
            });

            Assert.That(result.applied, Is.False);
            Assert.That(result.changedPointIds, Is.EqualTo(new[] { UPilotMonoHookPointId.LifecycleUpdate }));
            Assert.That(UPilotMonoHookSettings.instance.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleUpdate), Is.True);
            Assert.That(UPilotMonoHookInstallationService.IsInstalled(UPilotMonoHookPointId.LifecycleUpdate), Is.False);
        }

        [Test]
        public void StatusAndEventsExposeTracingStateWithoutConsumingByDefault()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            UPilotMonoHookRegistry.Instance.Context.EventSink.Publish(new UPilotMonoHookEvent
            {
                pointId = UPilotMonoHookPointId.LifecycleUpdate,
                kind = UPilotMonoHookPointId.LifecycleUpdate,
            });

            var status = service.BuildStatus();
            var events = UPilotMonoHookTracingMcpService.ReadEvents(new UPilotMonoHookTracingEventsPayload());

            Assert.That(status.points.Any(point => point.pointId == UPilotMonoHookPointId.LifecycleUpdate), Is.True);
            Assert.That(events.consumed, Is.False);
            Assert.That(events.items.Count, Is.EqualTo(1));
            Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(1));
        }

        [Test]
        public void ConfigureCanSetGlobalFilterProfileAndExposeProfilesInStatus()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            var profile = new UPilotTraceFilterProfile { Id = "mcp.global", Name = "MCP 全局" };

            var result = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                replaceFilterProfiles = true,
                filterProfiles = new[] { profile },
                setGlobalFilterProfile = true,
                globalFilterProfileId = profile.Id,
            });

            Assert.That(result.status.globalFilterProfileId, Is.EqualTo(profile.Id));
            Assert.That(result.status.filterProfiles.Any(item => item.Id == profile.Id), Is.True);
        }

        [Test]
        public void ConfigureCanSetStackModeAndPointSelectionExplicitly()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            bool wasEnabled = UPilotMonoHookSettings.instance.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleUpdate);

            var result = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                setStackTraceCaptureMode = true,
                stackTraceCaptureMode = UPilotStackTraceCaptureMode.SelectedPoints,
                pointIds = new[] { UPilotMonoHookPointId.LifecycleUpdate },
                updatePointStackTraceSelection = true,
                captureStackTrace = true,
            });

            Assert.That(result.status.captureStackTrace, Is.True);
            Assert.That(result.status.stackTraceCaptureMode,
                Is.EqualTo(UPilotStackTraceCaptureMode.SelectedPoints.ToString()));
            Assert.That(UPilotMonoHookSettings.instance.ShouldCaptureStackTrace(), Is.True);
            Assert.That(UPilotMonoHookSettings.instance.ShouldCaptureStackTrace(UPilotMonoHookPointId.LifecycleUpdate), Is.True);
            Assert.That(result.status.points.First(point => point.pointId == UPilotMonoHookPointId.LifecycleUpdate)
                .captureStackTraceEffective, Is.True);
            Assert.That(UPilotMonoHookSettings.instance.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleUpdate), Is.EqualTo(wasEnabled),
                "仅更新堆栈选择不应改写点位启用状态。");
        }

        [Test]
        public void ConfigureCanEnablePointFilterOverrideAndExposeEffectiveProfile()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            var profile = new UPilotTraceFilterProfile { Id = "mcp.point", Name = "MCP 点位" };
            var result = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                replaceFilterProfiles = true,
                filterProfiles = new[] { profile },
                setGlobalFilterProfile = true,
                globalFilterProfileId = UPilotTraceFilterProfileIds.None,
                updatePointFilterOverridesEnabled = true,
                pointFilterOverridesEnabled = true,
                pointIds = new[] { UPilotMonoHookPointId.LifecycleUpdate },
                updatePointFilterProfile = true,
                pointFilterProfileId = profile.Id,
            });

            var point = result.status.points.First(item => item.pointId == UPilotMonoHookPointId.LifecycleUpdate);
            Assert.That(result.status.pointFilterOverridesEnabled, Is.True);
            Assert.That(point.configuredFilterProfileId, Is.EqualTo(profile.Id));
            Assert.That(point.effectiveFilterProfileId, Is.EqualTo(profile.Id));
        }

        [Test]
        public void ConfigureRejectsUnknownProfileBeforeChangingGlobalSelection()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);

            var exception = Assert.Throws<ArgumentException>(() => service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                setGlobalFilterProfile = true,
                globalFilterProfileId = "mcp.unknown",
            }));

            Assert.That(exception.Message, Does.Contain("未知追踪器过滤器"));
            Assert.That(settings.globalFilterProfileId, Is.EqualTo(UPilotTraceFilterProfileIds.None));
        }

        [Test]
        public void StatusIncludesFilterStatisticsForAcceptedAndRejectedEvents()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            var profile = new UPilotTraceFilterProfile { Id = "mcp.stats", Name = "MCP 统计" };
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                NameMatchMode = UPilotTraceStringMatchMode.Equals,
                NamePattern = "Accepted",
            });
            service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                replaceFilterProfiles = true,
                filterProfiles = new[] { profile },
                setGlobalFilterProfile = true,
                globalFilterProfileId = profile.Id,
                resetFilterStatistics = true,
            });

            UPilotMonoHookRegistry.Instance.Context.EventSink.Publish(new UPilotMonoHookEvent
            {
                pointId = "mcp.stats",
                kind = "mcp.stats",
                objectName = "Accepted",
            });
            UPilotMonoHookRegistry.Instance.Context.EventSink.Publish(new UPilotMonoHookEvent
            {
                pointId = "mcp.stats",
                kind = "mcp.stats",
                objectName = "Rejected",
            });

            var statistics = service.BuildStatus().filterStatistics
                .First(item => item.pointId == "mcp.stats" && item.profileId == profile.Id);
            Assert.That(statistics.evaluated, Is.EqualTo(2));
            Assert.That(statistics.accepted, Is.EqualTo(1));
            Assert.That(statistics.rejected, Is.EqualTo(1));
        }

        [Test]
        public void ConfigureCanSetPerObjectNoiseControlsAndExposeStatus()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);
            var result = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                updatePerObjectRateLimit = true,
                enablePerObjectRateLimit = true,
                maxEventsPerObjectPerSecond = 3,
                updateDuplicateSuppression = true,
                suppressDuplicateEvents = true,
                duplicateEventWindowMilliseconds = 250,
            });

            Assert.That(result.status.enablePerObjectRateLimit, Is.True);
            Assert.That(result.status.maxEventsPerObjectPerSecond, Is.EqualTo(3));
            Assert.That(result.status.suppressDuplicateEvents, Is.True);
            Assert.That(result.status.duplicateEventWindowMilliseconds, Is.EqualTo(250));
        }

        [Test]
        public void ConfigureCanControlAutoInjectionMasterSwitchAndExposeStatus()
        {
            var service = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance);

            var enabled = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                updateAutoInjectEnabled = true,
                autoInjectEnabled = true,
                updateAutoApplyOnPlayMode = true,
                autoApplyOnPlayMode = true,
            });

            Assert.That(enabled.status.autoInjectEnabled, Is.True);
            Assert.That(enabled.status.autoApplyOnPlayMode, Is.True);

            var disabled = service.Configure(new UPilotMonoHookTracingConfigurePayload
            {
                updateAutoInjectEnabled = true,
                autoInjectEnabled = false,
            });

            Assert.That(disabled.status.autoInjectEnabled, Is.False);
            Assert.That(disabled.status.autoApplyOnPlayMode, Is.True,
                "关闭总开关应保留子选项，便于之后重新启用。");
        }
    }
}
