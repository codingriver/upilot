// -----------------------------------------------------------------------
// UPilot Editor tests - isolated lifecycle trampoline dispatch.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookLifecycleProxyTests
    {
        private bool _masterEnabled;
        private string _lifecycleTypeIncludes;
        private List<UPilotMonoHookPointState> _points;

        [SetUp]
        public void SetUp()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _lifecycleTypeIncludes = settings.lifecycleTypeIncludes;
            _points = settings.points
                .Select(point => new UPilotMonoHookPointState(
                    point.Id,
                    point.Enabled,
                    point.CaptureStackTrace,
                    point.HookAllSafeOverloads,
                    point.FilterProfileId,
                    point.ExecutionMode))
                .ToList();

            settings.masterEnabled = true;
            settings.globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            settings.lifecycleTypeIncludes = string.Join(",", new[]
            {
                typeof(LifecycleProxyProbeA).FullName,
                typeof(LifecycleProxyProbeB).FullName,
            });
            settings.lifecycleTypeExcludes = string.Empty;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            LifecycleProxyProbeA.DisableCount = 0;
            LifecycleProxyProbeB.DisableCount = 0;
            UPilotMonoHookInstallationService.UninstallAll();
            UPilotMonoHookTelemetry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UPilotMonoHookInstallationService.UninstallAll();
            UPilotMonoHookTelemetry.Clear();
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.lifecycleTypeIncludes = _lifecycleTypeIncludes;
            settings.points = _points;
        }

        [Test]
        public void MultipleLifecycleTypesInvokeTheirOwnOriginalMethods()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnDisable, true);
            var controller = new UPilotMonoHookController();
            GameObject objectA = null;
            GameObject objectB = null;
            try
            {
                var report = controller.Apply(false);
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleOnDisable));

                var coverage = controller.Runtime[UPilotMonoHookPointId.LifecycleOnDisable].Coverage;
                Assert.That(coverage, Is.Not.Null);
                Assert.That(coverage.InstalledTypeCount, Is.EqualTo(2));
                Assert.That(coverage.InstalledMethodCount, Is.EqualTo(2));
                Assert.That(coverage.TrampolineCount, Is.EqualTo(2));
                Assert.That(
                    coverage.Entries.Count(entry => entry.Status == "Installed"),
                    Is.EqualTo(2));
                Assert.That(
                    coverage.Entries.Select(entry => entry.TargetTypeName),
                    Does.Contain(typeof(LifecycleProxyProbeA).FullName));
                Assert.That(
                    coverage.Entries.Select(entry => entry.TargetTypeName),
                    Does.Contain(typeof(LifecycleProxyProbeB).FullName));

                var diagnostic = controller.GetDiagnosticSnapshot()
                    .Single(record => record.pointId == UPilotMonoHookPointId.LifecycleOnDisable);
                Assert.That(diagnostic.installedTypeCount, Is.EqualTo(2));
                Assert.That(diagnostic.installedMethodCount, Is.EqualTo(2));
                Assert.That(diagnostic.trampolineCount, Is.EqualTo(2));
                Assert.That(diagnostic.entries.Count(entry => entry.status == "Installed"), Is.EqualTo(2));

                var status = new UPilotMonoHookTracingMcpService(UPilotBridge.Instance).BuildStatus();
                var statusPoint = status.points.Single(point =>
                    point.pointId == UPilotMonoHookPointId.LifecycleOnDisable);
                Assert.That(statusPoint.installedTypeCount, Is.EqualTo(2));
                Assert.That(statusPoint.entries.Count(entry => entry.status == "Installed"), Is.EqualTo(2));

                objectA = new GameObject("UPilotLifecycleProxyA");
                var probeA = objectA.AddComponent<LifecycleProxyProbeA>();
                objectB = new GameObject("UPilotLifecycleProxyB");
                var probeB = objectB.AddComponent<LifecycleProxyProbeB>();
                UPilotMonoHookTelemetry.Clear();

                probeA.enabled = false;
                probeB.enabled = false;

                Assert.That(LifecycleProxyProbeA.DisableCount, Is.EqualTo(1));
                Assert.That(LifecycleProxyProbeB.DisableCount, Is.EqualTo(1));
                var events = UPilotMonoHookTelemetry.Read(16)
                    .Where(item => item.kind == UPilotMonoHookPointId.LifecycleOnDisable)
                    .ToList();
                Assert.That(events.Count, Is.EqualTo(2));
                Assert.That(events.Select(item => item.componentType),
                    Does.Contain(typeof(LifecycleProxyProbeA).FullName));
                Assert.That(events.Select(item => item.componentType),
                    Does.Contain(typeof(LifecycleProxyProbeB).FullName));
            }
            finally
            {
                controller.UninstallAll();
                if (objectA != null) Object.DestroyImmediate(objectA);
                if (objectB != null) Object.DestroyImmediate(objectB);
            }
        }

        [ExecuteAlways]
        private sealed class LifecycleProxyProbeA : MonoBehaviour
        {
            public static int DisableCount;

            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            private void OnDisable()
            {
                DisableCount++;
                if (DisableCount == int.MinValue)
                    DisableCount = 0;
            }
        }

        [ExecuteAlways]
        private sealed class LifecycleProxyProbeB : MonoBehaviour
        {
            public static int DisableCount;

            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            private void OnDisable()
            {
                DisableCount += 2;
                if (DisableCount == int.MinValue)
                    DisableCount = 0;
                DisableCount -= 1;
            }
        }
    }
}
