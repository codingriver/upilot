// -----------------------------------------------------------------------
// UPilot Editor tests - MonoHook configuration controller.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookControllerTests
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
        }

        [TearDown]
        public void TearDown()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.points = _points;
            settings.EnsureDefaults();
        }

        [Test]
        public void ApplyInstallsOnlyEnabledRegisteredPoint()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, true);

            var installCount = 0;
            var uninstallCount = 0;
            var controller = new UPilotMonoHookController();
            controller.RegisterInstaller(
                UPilotMonoHookPointId.GameObjectSetActive,
                () => installCount++,
                () => uninstallCount++);

            var report = controller.Apply(false);

            Assert.That(installCount, Is.EqualTo(1));
            Assert.That(report.Enabled, Does.Contain(UPilotMonoHookPointId.GameObjectSetActive));
            Assert.That(controller.Runtime[UPilotMonoHookPointId.GameObjectSetActive].InstallState,
                Is.EqualTo(UPilotMonoHookInstallState.Installed));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.GameObjectSetActive));
        }

        [Test]
        public void ApplyingTwiceDoesNotReinstallExistingPoint()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectInstantiate, true);

            var installCount = 0;
            var controller = new UPilotMonoHookController();
            controller.RegisterInstaller(
                UPilotMonoHookPointId.GameObjectInstantiate,
                () => installCount++,
                () => { });

            controller.Apply(false);
            var second = controller.Apply(false);

            Assert.That(installCount, Is.EqualTo(1));
            Assert.That(second.Unchanged, Does.Contain(UPilotMonoHookPointId.GameObjectInstantiate));
        }

        [Test]
        public void DisabledPointIsUninstalledAfterItWasInstalled()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, true);

            var uninstallCount = 0;
            var controller = new UPilotMonoHookController();
            controller.RegisterInstaller(
                UPilotMonoHookPointId.GameObjectSetActive,
                () => { },
                () => uninstallCount++);

            controller.Apply(false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, false);
            var report = controller.Apply(false);

            Assert.That(uninstallCount, Is.EqualTo(1));
            Assert.That(report.Disabled, Does.Contain(UPilotMonoHookPointId.GameObjectSetActive));
            Assert.That(controller.Runtime[UPilotMonoHookPointId.GameObjectSetActive].InstallState,
                Is.EqualTo(UPilotMonoHookInstallState.NotInstalled));
        }

        [Test]
        public void UnsupportedProviderIsReportedAsUnsupported()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.ComponentBehaviourEnabled, true);

            var controller = new UPilotMonoHookController();
            var report = controller.Apply(false);

            var descriptor = UPilotMonoHookRegistry.Instance.Find(UPilotMonoHookPointId.ComponentBehaviourEnabled);
            Assert.That(descriptor, Is.Not.Null);
            Assert.That(descriptor.Provider, Is.Not.Null);
            var support = descriptor.Provider.CheckSupport(UPilotMonoHookRegistry.Instance.Context);
            if (support.IsSupported)
            {
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.ComponentBehaviourEnabled));
                Assert.That(controller.Runtime[UPilotMonoHookPointId.ComponentBehaviourEnabled].InstallState,
                    Is.EqualTo(UPilotMonoHookInstallState.Installed));
                controller.UninstallAll();
            }
            else
            {
                Assert.That(report.Unsupported, Does.Contain(UPilotMonoHookPointId.ComponentBehaviourEnabled));
                Assert.That(report.Failed, Does.Contain(UPilotMonoHookPointId.ComponentBehaviourEnabled));
                Assert.That(controller.Runtime[UPilotMonoHookPointId.ComponentBehaviourEnabled].InstallState,
                    Is.EqualTo(UPilotMonoHookInstallState.Unsupported));
            }
        }

        [Test]
        public void DiagnosticsExportContainsEveryCatalogPoint()
        {
            string path = Path.Combine(Path.GetTempPath(), "UPilotMonoHookDiagnostics_" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var controller = new UPilotMonoHookController();
                int count = controller.ExportDiagnosticsJsonLines(path);
                var lines = File.ReadAllLines(path);

                Assert.That(count, Is.EqualTo(UPilotMonoHookCatalog.All.Count));
                Assert.That(lines.Length, Is.EqualTo(count));
                Assert.That(lines.Any(line => line.Contains(UPilotMonoHookPointId.LifecycleOnEnable)), Is.True);
                Assert.That(lines.All(line => line.Contains("\"unityVersion\"")), Is.True);
                Assert.That(lines.All(line => line.Contains("\"installState\"")), Is.True);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
