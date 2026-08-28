// -----------------------------------------------------------------------
// UPilot Editor tests - physical MonoHook integration.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookIntegrationTests
    {
        private bool _masterEnabled;
        private bool _suppressUnchangedValues;
        private int _maxEventsPerSecond;
        private string _lifecycleAssemblyIncludes;
        private string _lifecycleAssemblyExcludes;
        private string _lifecycleNamespaceIncludes;
        private string _lifecycleNamespaceExcludes;
        private string _lifecycleTypeIncludes;
        private string _lifecycleTypeExcludes;
        private List<UPilotMonoHookPointState> _points;

        [SetUp]
        public void SetUp()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _suppressUnchangedValues = settings.suppressUnchangedValues;
            _maxEventsPerSecond = settings.maxEventsPerSecond;
            _lifecycleAssemblyIncludes = settings.lifecycleAssemblyIncludes;
            _lifecycleAssemblyExcludes = settings.lifecycleAssemblyExcludes;
            _lifecycleNamespaceIncludes = settings.lifecycleNamespaceIncludes;
            _lifecycleNamespaceExcludes = settings.lifecycleNamespaceExcludes;
            _lifecycleTypeIncludes = settings.lifecycleTypeIncludes;
            _lifecycleTypeExcludes = settings.lifecycleTypeExcludes;
            _points = settings.points
                .Select(point => new UPilotMonoHookPointState(
                    point.Id,
                    point.Enabled,
                    point.CaptureStackTrace,
                    point.HookAllSafeOverloads,
                    point.FilterProfileId))
                .ToList();
            UPilotMonoHookTelemetry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.suppressUnchangedValues = _suppressUnchangedValues;
            settings.maxEventsPerSecond = _maxEventsPerSecond;
            settings.lifecycleAssemblyIncludes = _lifecycleAssemblyIncludes;
            settings.lifecycleAssemblyExcludes = _lifecycleAssemblyExcludes;
            settings.lifecycleNamespaceIncludes = _lifecycleNamespaceIncludes;
            settings.lifecycleNamespaceExcludes = _lifecycleNamespaceExcludes;
            settings.lifecycleTypeIncludes = _lifecycleTypeIncludes;
            settings.lifecycleTypeExcludes = _lifecycleTypeExcludes;
            settings.points = _points;
            settings.EnsureDefaults();
            UPilotMonoHookTelemetry.Clear();
        }

        [Test]
        public void SetActivePointCanBeInstalledAndCapturesStateChange()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            var requested = new[]
            {
                UPilotMonoHookPointId.GameObjectSetActive,
                UPilotMonoHookPointId.GameObjectInstantiate,
                UPilotMonoHookPointId.TransformLocalPosition,
                UPilotMonoHookPointId.TransformSetPositionAndRotation,
                UPilotMonoHookPointId.TransformSetLocalPositionAndRotation,
                UPilotMonoHookPointId.TransformSetParent,
            };
            var supported = new HashSet<string>(requested.Where(pointId =>
                UPilotMonoHookRegistry.Instance.Find(pointId)?.Provider
                    ?.CheckSupport(UPilotMonoHookRegistry.Instance.Context).IsSupported == true));
            foreach (var pointId in supported)
                settings.SetEnabled(pointId, true);
            if (supported.Count == 0)
                Assert.Ignore("当前 Unity 版本没有可安全安装的基础 GameObject/Transform 点位。");
            settings.suppressUnchangedValues = true;
            settings.maxEventsPerSecond = 1000;

            var controller = new UPilotMonoHookController();
            var report = controller.Apply(false);
            foreach (var pointId in supported)
                Assert.That(report.Failed, Does.Not.Contain(pointId), pointId);

            var gameObject = new GameObject("UPilotMonoHookIntegrationObject");
            var parent = new GameObject("UPilotMonoHookIntegrationParent");
            var clones = new List<UnityEngine.Object>();
            try
            {
                UPilotMonoHookTelemetry.Clear();
                if (supported.Contains(UPilotMonoHookPointId.GameObjectSetActive))
                {
                    gameObject.SetActive(false);
                    gameObject.SetActive(true);
                }
                if (supported.Contains(UPilotMonoHookPointId.TransformLocalPosition))
                    gameObject.transform.localPosition = new Vector3(1f, 2f, 3f);
                if (supported.Contains(UPilotMonoHookPointId.TransformSetPositionAndRotation))
                    gameObject.transform.SetPositionAndRotation(new Vector3(4f, 5f, 6f), Quaternion.Euler(10f, 20f, 30f));
                if (supported.Contains(UPilotMonoHookPointId.TransformSetLocalPositionAndRotation))
                    gameObject.transform.SetLocalPositionAndRotation(new Vector3(7f, 8f, 9f), Quaternion.Euler(30f, 20f, 10f));
                if (supported.Contains(UPilotMonoHookPointId.TransformSetParent))
                    gameObject.transform.SetParent(parent.transform, false);
                if (supported.Contains(UPilotMonoHookPointId.GameObjectInstantiate))
                {
                    clones.Add(UnityEngine.Object.Instantiate((UnityEngine.Object)gameObject));
                    clones.Add(UnityEngine.Object.Instantiate(
                        (UnityEngine.Object)gameObject,
                        new Vector3(2f, 3f, 4f),
                        Quaternion.identity));
                    clones.Add(UnityEngine.Object.Instantiate(
                        (UnityEngine.Object)gameObject,
                        parent.transform,
                        false));
                }

                var events = UPilotMonoHookTelemetry.Read(32);
                var expectedKinds = new Dictionary<string, string>
                {
                    { UPilotMonoHookPointId.GameObjectSetActive, "gameObject.setActive" },
                    { UPilotMonoHookPointId.GameObjectInstantiate, "gameObject.instantiate" },
                    { UPilotMonoHookPointId.TransformLocalPosition, "transform.localPosition" },
                    { UPilotMonoHookPointId.TransformSetPositionAndRotation, "transform.setPositionAndRotation" },
                    { UPilotMonoHookPointId.TransformSetLocalPositionAndRotation, "transform.setLocalPositionAndRotation" },
                    { UPilotMonoHookPointId.TransformSetParent, "transform.setParent" },
                };
                foreach (var pointId in supported)
                    Assert.That(events.Any(item => item.kind == expectedKinds[pointId]), Is.True, pointId);
                Assert.That(events.All(item => item.instanceId != 0), Is.True);
            }
            finally
            {
                controller.UninstallAll();
                foreach (var clone in clones)
                {
                    if (clone != null)
                        Object.DestroyImmediate(clone);
                }
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void LifecycleAndDestroyPointsCanBeInstalledAndCaptureEvents()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleAwake, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnEnable, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleStart, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnDisable, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnDestroy, true);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectDestroy, true);

            var controller = new UPilotMonoHookController();
            var report = controller.Apply(false);
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleAwake));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleOnEnable));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleStart));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleOnDisable));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleOnDestroy));
            Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.GameObjectDestroy));
            Assert.That(controller.Runtime[UPilotMonoHookPointId.LifecycleAwake].Coverage, Is.Not.Null);
            Assert.That(controller.Runtime[UPilotMonoHookPointId.LifecycleAwake].Coverage.SkippedCount, Is.GreaterThan(0));

            GameObject gameObject = null;
            try
            {
                UPilotMonoHookTelemetry.Clear();
                gameObject = new GameObject("UPilotMonoHookLifecycleObject");
                gameObject.AddComponent<MonoHookLifecycleProbe>();

                var creationEvents = UPilotMonoHookTelemetry.Read(32);
                Assert.That(creationEvents.Any(item => item.kind == "lifecycle.awake"), Is.True);
                Assert.That(creationEvents.Any(item => item.kind == "lifecycle.onEnable"), Is.True);

                UPilotMonoHookTelemetry.Clear();
                Object.DestroyImmediate(gameObject);
                gameObject = null;

                var destructionEvents = UPilotMonoHookTelemetry.Read(32);
                Assert.That(destructionEvents.Any(item => item.kind == "gameObject.destroy"), Is.True);
                Assert.That(destructionEvents.Any(item => item.kind == "lifecycle.onDestroy"), Is.True);
            }
            finally
            {
                controller.UninstallAll();
                if (gameObject != null)
                    Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DestroyImmediateOverloadModeIsAppliedExplicitly()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectDestroy, true);
            settings.SetHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectDestroy, false);

            var controller = new UPilotMonoHookController();
            GameObject recommendedTarget = null;
            GameObject allTarget = null;
            GameObject directTarget = null;
            try
            {
                var recommendedReport = controller.Apply(false);
                Assert.That(recommendedReport.Failed, Does.Not.Contain(UPilotMonoHookPointId.GameObjectDestroy));
                Assert.That(
                    controller.Runtime[UPilotMonoHookPointId.GameObjectDestroy].Coverage.CandidateCount,
                    Is.EqualTo(2));

                recommendedTarget = new GameObject("UPilotMonoHookDestroyRecommended");
                UPilotMonoHookTelemetry.Clear();
                Object.DestroyImmediate(recommendedTarget);
                recommendedTarget = null;
                var recommendedEvents = UPilotMonoHookTelemetry.Read(16)
                    .Where(item => item.kind == "gameObject.destroy")
                    .ToList();
                Assert.That(recommendedEvents.Count, Is.EqualTo(1));
                Assert.That(recommendedEvents[0].methodSignature, Is.EqualTo("DestroyImmediate(Object,bool)"));

                settings.SetHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectDestroy, true);
                controller.RefreshRuntime();
                Assert.That(
                    controller.Runtime[UPilotMonoHookPointId.GameObjectDestroy].Message,
                    Is.EqualTo("未应用"));

                var allReport = controller.Apply(false);
                Assert.That(allReport.Enabled, Does.Contain(UPilotMonoHookPointId.GameObjectDestroy));
                Assert.That(allReport.Failed, Does.Not.Contain(UPilotMonoHookPointId.GameObjectDestroy));
                Assert.That(
                    controller.Runtime[UPilotMonoHookPointId.GameObjectDestroy].Coverage.CandidateCount,
                    Is.EqualTo(4));

                allTarget = new GameObject("UPilotMonoHookDestroyAllSafeOverloads");
                UPilotMonoHookTelemetry.Clear();
                Object.DestroyImmediate(allTarget);
                allTarget = null;
                var allSafeEvents = UPilotMonoHookTelemetry.Read(16)
                    .Where(item => item.kind == "gameObject.destroy")
                    .ToList();
                Assert.That(allSafeEvents.Count, Is.EqualTo(2));
                Assert.That(allSafeEvents.Select(item => item.methodSignature), Does.Contain("DestroyImmediate(Object)"));
                Assert.That(allSafeEvents.Select(item => item.methodSignature), Does.Contain("DestroyImmediate(Object,bool)"));

                directTarget = new GameObject("UPilotMonoHookDestroyDirectFinalOverload");
                UPilotMonoHookTelemetry.Clear();
                Object.DestroyImmediate(directTarget, false);
                directTarget = null;
                var directEvents = UPilotMonoHookTelemetry.Read(16)
                    .Where(item => item.kind == "gameObject.destroy")
                    .ToList();
                Assert.That(directEvents.Count, Is.EqualTo(1));
                Assert.That(directEvents[0].methodSignature, Is.EqualTo("DestroyImmediate(Object,bool)"));
            }
            finally
            {
                controller.UninstallAll();
                if (recommendedTarget != null) Object.DestroyImmediate(recommendedTarget);
                if (allTarget != null) Object.DestroyImmediate(allTarget);
                if (directTarget != null) Object.DestroyImmediate(directTarget);
            }
        }

        [Test]
        public void LifecycleTypeFilterRestrictsPhysicalInstallation()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleAwake, true);
            settings.lifecycleAssemblyIncludes = typeof(MonoHookLifecycleProbe).Assembly.GetName().Name;
            settings.lifecycleNamespaceIncludes = typeof(MonoHookLifecycleProbe).Namespace;
            settings.lifecycleTypeIncludes = typeof(MonoHookLifecycleProbe).FullName;
            settings.lifecycleAssemblyExcludes = string.Empty;
            settings.lifecycleNamespaceExcludes = string.Empty;
            settings.lifecycleTypeExcludes = string.Empty;

            var controller = new UPilotMonoHookController();
            var included = new GameObject("UPilotMonoHookIncludedLifecycle");
            var excluded = new GameObject("UPilotMonoHookExcludedLifecycle");
            try
            {
                var report = controller.Apply(false);
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleAwake));
                var coverage = controller.Runtime[UPilotMonoHookPointId.LifecycleAwake].Coverage;
                Assert.That(coverage, Is.Not.Null);
                Assert.That(coverage.InstalledCount, Is.EqualTo(1));
                Assert.That(coverage.SkippedCount, Is.GreaterThan(0));

                UPilotMonoHookTelemetry.Clear();
                included.AddComponent<MonoHookLifecycleProbe>();
                excluded.AddComponent<ShortLifecycleProbe>();

                var events = UPilotMonoHookTelemetry.Read(16);
                Assert.That(events.Count(item => item.kind == "lifecycle.awake"), Is.EqualTo(1));
                Assert.That(events.Single(item => item.kind == "lifecycle.awake").objectName,
                    Is.EqualTo(included.name));
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(included);
                Object.DestroyImmediate(excluded);
            }
        }

        [Test]
        public void UpdateLifecyclePointsAreOptInAndCaptureDirectInvocations()
        {
            var settings = UPilotMonoHookSettings.instance;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleUpdate, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleFixedUpdate, true);
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleLateUpdate, true);
            settings.lifecycleTypeIncludes = typeof(MonoHookLifecycleProbe).FullName;

            var controller = new UPilotMonoHookController();
            var gameObject = new GameObject("UPilotMonoHookUpdateLifecycleObject");
            try
            {
                var report = controller.Apply(false);
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleUpdate));
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleFixedUpdate));
                Assert.That(report.Failed, Does.Not.Contain(UPilotMonoHookPointId.LifecycleLateUpdate));

                var probe = gameObject.AddComponent<MonoHookLifecycleProbe>();
                UPilotMonoHookTelemetry.Clear();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MonoHookLifecycleProbe).GetMethod("Update", flags).Invoke(probe, null);
                typeof(MonoHookLifecycleProbe).GetMethod("FixedUpdate", flags).Invoke(probe, null);
                typeof(MonoHookLifecycleProbe).GetMethod("LateUpdate", flags).Invoke(probe, null);

                var events = UPilotMonoHookTelemetry.Read(16);
                Assert.That(events.Any(item => item.kind == UPilotMonoHookPointId.LifecycleUpdate), Is.True);
                Assert.That(events.Any(item => item.kind == UPilotMonoHookPointId.LifecycleFixedUpdate), Is.True);
                Assert.That(events.Any(item => item.kind == UPilotMonoHookPointId.LifecycleLateUpdate), Is.True);
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(gameObject);
            }
        }

        [ExecuteAlways]
        private sealed class MonoHookLifecycleProbe : MonoBehaviour
        {
            private int _marker;

            private void Awake() { Touch(1); Touch(11); }
            private void Start() { Touch(2); Touch(12); }
            private void OnEnable() { Touch(3); Touch(13); }
            private void OnDisable() { Touch(4); Touch(14); }
            private void OnDestroy() { Touch(5); Touch(15); }
            private void Update() { Touch(6); Touch(16); }
            private void FixedUpdate() { Touch(7); Touch(17); }
            private void LateUpdate() { Touch(8); Touch(18); }

            private void Touch(int value)
            {
                _marker += value;
                _marker ^= value << 2;
                if (_marker == int.MinValue)
                    _marker = 0;
            }
        }

        private sealed class ShortLifecycleProbe : MonoBehaviour
        {
            private void Awake() { }
        }
    }
}
