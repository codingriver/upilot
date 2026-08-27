// -----------------------------------------------------------------------
// UPilot Editor tests - thin MonoHook tracing MCP adapter.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookMcpAdapterTests
    {
        private bool _masterEnabled;
        private List<UPilotMonoHookPointState> _points;

        [SetUp]
        public void SetUp()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _points = settings.points
                .Select(point => new UPilotMonoHookPointState(
                    point.Id,
                    point.Enabled,
                    point.CaptureStackTrace,
                    point.HookAllSafeOverloads))
                .ToList();
            new UPilotMonoHookController().UninstallAll();
        }

        [TearDown]
        public void TearDown()
        {
            new UPilotMonoHookController().UninstallAll();
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.points = _points;
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
    }
}
