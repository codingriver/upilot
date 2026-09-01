// -----------------------------------------------------------------------
// UPilot Editor tests - bounded MonoHook stability checks.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookStabilityTests
    {
        private bool _masterEnabled;
        private bool _suppressUnchangedValues;
        private int _maxEventsPerSecond;
        private List<UPilotMonoHookPointState> _points;

        [SetUp]
        public void SetUp()
        {
            UPilotMonoHookInstallationService.UninstallAll();
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _suppressUnchangedValues = settings.suppressUnchangedValues;
            _maxEventsPerSecond = settings.maxEventsPerSecond;
            _points = settings.points.Select(point => new UPilotMonoHookPointState(
                point.Id,
                point.Enabled,
                point.CaptureStackTrace,
                point.HookAllSafeOverloads,
                point.FilterProfileId,
                point.ExecutionMode)).ToList();
            UPilotMonoHookTelemetry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UPilotMonoHookInstallationService.UninstallAll();
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.suppressUnchangedValues = _suppressUnchangedValues;
            settings.maxEventsPerSecond = _maxEventsPerSecond;
            settings.points = _points;
            settings.EnsureDefaults();
            UPilotMonoHookTelemetry.Clear();
        }

        [Test]
        public void SetActiveCanBeInstalledAndUninstalledRepeatedly()
        {
            var descriptor = UPilotMonoHookRegistry.Instance.Find(UPilotMonoHookPointId.GameObjectSetActive);
            var support = descriptor?.Provider?.CheckSupport(UPilotMonoHookRegistry.Instance.Context);
            if (support == null || !support.IsSupported)
                Assert.Ignore("gameObject.setActive 条件跳过：" + (support?.Message ?? "点位未注册"));

            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = true;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, true);
            settings.suppressUnchangedValues = false;
            settings.maxEventsPerSecond = 1000;

            var gameObject = new GameObject("UPilotMonoHookRepeatedInstall");
            var controller = new UPilotMonoHookController();
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    var report = controller.Apply(false);
                    Assert.That(report.Failed, Is.Empty, "install iteration " + i);
                    gameObject.SetActive(!gameObject.activeSelf);
                    controller.UninstallAll();
                    Assert.That(UPilotMonoHookInstallationService.IsInstalled(UPilotMonoHookPointId.GameObjectSetActive), Is.False);
                }

                Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(20));
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HighEventVolumeRemainsBoundedAndReportsDrops()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.suppressUnchangedValues = false;
            settings.maxEventsPerSecond = 10000;
            var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;

            for (int i = 0; i < 5000; i++)
                sink.Publish(new UPilotMonoHookEvent { kind = "stability.volume", afterValue = i.ToString() });

            Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(2048));
            Assert.That(UPilotMonoHookTelemetry.DroppedCount, Is.EqualTo(5000 - 2048));
            var events = UPilotMonoHookTelemetry.Snapshot(2048);
            Assert.That(events.First().afterValue, Is.EqualTo((5000 - 2048).ToString()));
            Assert.That(events.Last().afterValue, Is.EqualTo("4999"));
        }
    }
}
