// -----------------------------------------------------------------------
// UPilot Editor tests - recommended versus all-safe overload policies.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookOverloadPolicyTests
    {
        private bool _masterEnabled;
        private bool _suppressUnchangedValues;
        private int _maxEventsPerSecond;
        private List<UPilotMonoHookPointState> _points;

        private static readonly string[] PolicyPointIds =
        {
            UPilotMonoHookPointId.GameObjectInstantiate,
            UPilotMonoHookPointId.GameObjectDestroy,
            UPilotMonoHookPointId.TransformSetParent,
            UPilotMonoHookPointId.TransformTranslate,
            UPilotMonoHookPointId.TransformRotate,
            UPilotMonoHookPointId.TransformRotateAround,
            UPilotMonoHookPointId.TransformLookAt,
        };

        [SetUp]
        public void SetUp()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _suppressUnchangedValues = settings.suppressUnchangedValues;
            _maxEventsPerSecond = settings.maxEventsPerSecond;
            _points = settings.points.Select(point => new UPilotMonoHookPointState(
                point.Id,
                point.Enabled,
                point.CaptureStackTrace,
                point.HookAllSafeOverloads)).ToList();
            UPilotMonoHookInstallationService.UninstallAll();
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
        public void OnlyOverloadedBuiltInPointsExposeOverloadPolicy()
        {
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                if (!UPilotMonoHookPointId.IsBuiltIn(definition.Id))
                    continue;

                var provider = UPilotMonoHookRegistry.Instance.Find(definition.Id)?.Provider;
                var policy = provider as IUPilotMonoHookOverloadPolicyProvider;
                bool expected = PolicyPointIds.Contains(definition.Id);
                Assert.That(policy, Is.Not.Null, definition.Id);
                Assert.That(policy.SupportsHookAllSafeOverloads, Is.EqualTo(expected), definition.Id);
            }
        }

        [Test]
        public void InstantiateRecommendedSuppressesNestedForwardingAndAllSafeReportsIt()
        {
            const string pointId = UPilotMonoHookPointId.GameObjectInstantiate;
            var source = new GameObject("UPilotMonoHookInstantiateSource");
            var clones = new List<UnityEngine.Object>();
            var controller = ApplyRecommended(pointId);
            try
            {
                UPilotMonoHookTelemetry.Clear();
                clones.Add(UnityEngine.Object.Instantiate(
                    (UnityEngine.Object)source,
                    (Transform)null,
                    false));
                var recommended = Events(pointId);
                Assert.That(recommended.Count, Is.EqualTo(1));
                Assert.That(recommended[0].methodSignature, Is.EqualTo("Instantiate(Object,Transform,bool)"));

                ApplyAllSafe(controller, pointId);
                UPilotMonoHookTelemetry.Clear();
                clones.Add(UnityEngine.Object.Instantiate(
                    (UnityEngine.Object)source,
                    (Transform)null,
                    false));
                var allSafe = Events(pointId);
                Assert.That(allSafe.Count, Is.EqualTo(2));
                Assert.That(allSafe.Select(item => item.methodSignature), Does.Contain("Instantiate(Object)"));
                Assert.That(allSafe.Select(item => item.methodSignature), Does.Contain("Instantiate(Object,Transform,bool)"));
            }
            finally
            {
                controller.UninstallAll();
                foreach (var clone in clones)
                    if (clone != null) Object.DestroyImmediate(clone);
                Object.DestroyImmediate(source);
            }
        }

        [TestCase(UPilotMonoHookPointId.TransformTranslate)]
        [TestCase(UPilotMonoHookPointId.TransformRotate)]
        public void TransformWrapperFamiliesUseCanonicalRecommendedOverloads(string pointId)
        {
            var gameObject = new GameObject("UPilotMonoHookTransformOverload");
            var controller = ApplyRecommended(pointId);
            try
            {
                UPilotMonoHookTelemetry.Clear();
                InvokeWrapper(pointId, gameObject.transform);
                var recommended = Events(pointId);
                Assert.That(recommended.Count, Is.EqualTo(1));
                Assert.That(
                    recommended[0].methodSignature,
                    Is.EqualTo(pointId == UPilotMonoHookPointId.TransformTranslate
                        ? "Translate(Vector3,Space)"
                        : "Rotate(Vector3,Space)"));

                ApplyAllSafe(controller, pointId);
                UPilotMonoHookTelemetry.Clear();
                InvokeWrapper(pointId, gameObject.transform);
                var allSafe = Events(pointId);
                Assert.That(allSafe.Count, Is.EqualTo(2));
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(gameObject);
            }
        }

        [TestCase(UPilotMonoHookPointId.TransformSetParent)]
        [TestCase(UPilotMonoHookPointId.TransformLookAt)]
        public void VersionSensitiveTransformPoliciesCanBeReapplied(string pointId)
        {
            var parent = new GameObject("UPilotMonoHookPolicyParent");
            var target = new GameObject("UPilotMonoHookPolicyTarget");
            target.transform.position = Vector3.forward * 10f;
            var gameObject = new GameObject("UPilotMonoHookPolicyObject");
            var controller = ApplyRecommended(pointId);
            try
            {
                UPilotMonoHookTelemetry.Clear();
                InvokeVersionSensitive(pointId, gameObject.transform, parent.transform, target.transform);
                Assert.That(Events(pointId).Count, Is.GreaterThanOrEqualTo(1));

                ApplyAllSafe(controller, pointId);
                Assert.That(
                    controller.Runtime[pointId].AppliedHookAllSafeOverloads,
                    Is.True,
                    pointId);
                Assert.That(controller.Runtime[pointId].Coverage.CandidateCount, Is.GreaterThan(1));

                UPilotMonoHookTelemetry.Clear();
                InvokeVersionSensitive(pointId, gameObject.transform, parent.transform, target.transform);
                var allSafe = Events(pointId);
                Assert.That(allSafe.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(allSafe.All(item => !string.IsNullOrEmpty(item.methodSignature)), Is.True);
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void RotateAroundRecommendedUsesCurrentApiAndAllSafeAddsLegacyApi()
        {
            const string pointId = UPilotMonoHookPointId.TransformRotateAround;
            var gameObject = new GameObject("UPilotMonoHookRotateAroundOverload");
            var controller = ApplyRecommended(pointId);
            try
            {
                Assert.That(controller.Runtime[pointId].Coverage.CandidateCount, Is.EqualTo(1));

                UPilotMonoHookTelemetry.Clear();
                gameObject.transform.RotateAround(Vector3.zero, Vector3.up, 15f);
                var recommended = Events(pointId);
                Assert.That(recommended.Count, Is.EqualTo(1));
                Assert.That(
                    recommended[0].methodSignature,
                    Is.EqualTo("RotateAround(Vector3,Vector3,float)"));

                ApplyAllSafe(controller, pointId);
                Assert.That(controller.Runtime[pointId].Coverage.CandidateCount, Is.EqualTo(2));

                var legacy = typeof(Transform).GetMethod(
                    "RotateAround",
                    new[] { typeof(Vector3), typeof(float) });
                Assert.That(legacy, Is.Not.Null);
                UPilotMonoHookTelemetry.Clear();
                legacy.Invoke(gameObject.transform, new object[] { Vector3.up, 5f });
                var allSafe = Events(pointId);
                Assert.That(allSafe.Count, Is.EqualTo(1));
                Assert.That(
                    allSafe[0].methodSignature,
                    Is.EqualTo("RotateAround(Vector3,float)"));
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(gameObject);
            }
        }

        private static UPilotMonoHookController ApplyRecommended(string pointId)
        {
            var descriptor = UPilotMonoHookRegistry.Instance.Find(pointId);
            Assert.That(descriptor, Is.Not.Null, pointId);
            var support = descriptor.Provider.CheckSupport(UPilotMonoHookRegistry.Instance.Context);
            if (!support.IsSupported)
                Assert.Ignore(pointId + " 条件跳过：" + support.Message);

            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = true;
            settings.suppressUnchangedValues = false;
            settings.maxEventsPerSecond = 1000;
            foreach (var definition in UPilotMonoHookCatalog.All)
                settings.SetEnabled(definition.Id, false);
            settings.SetEnabled(pointId, true);
            settings.SetHookAllSafeOverloads(pointId, false);

            var controller = new UPilotMonoHookController();
            var report = controller.Apply(false);
            Assert.That(report.Failed, Does.Not.Contain(pointId), pointId);
            return controller;
        }

        private static void ApplyAllSafe(UPilotMonoHookController controller, string pointId)
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.SetHookAllSafeOverloads(pointId, true);
            controller.RefreshRuntime();
            Assert.That(controller.Runtime[pointId].Message, Is.EqualTo("未应用"), pointId);
            var report = controller.Apply(false);
            Assert.That(report.Enabled, Does.Contain(pointId), pointId);
            Assert.That(report.Failed, Does.Not.Contain(pointId), pointId);
            controller.RefreshRuntime();
        }

        private static List<UPilotMonoHookEvent> Events(string pointId)
        {
            return UPilotMonoHookTelemetry.Read(64)
                .Where(item => item.pointId == pointId)
                .ToList();
        }

        private static void InvokeWrapper(string pointId, Transform transform)
        {
            if (pointId == UPilotMonoHookPointId.TransformTranslate)
                transform.Translate(1f, 2f, 3f, Space.World);
            else
                transform.Rotate(5f, 10f, 15f, Space.Self);
        }

        private static void InvokeVersionSensitive(
            string pointId,
            Transform transform,
            Transform parent,
            Transform target)
        {
            if (pointId == UPilotMonoHookPointId.TransformSetParent)
                transform.SetParent(parent);
            else
                transform.LookAt(target);
        }
    }
}
