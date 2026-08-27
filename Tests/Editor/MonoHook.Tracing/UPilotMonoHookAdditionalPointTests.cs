// -----------------------------------------------------------------------
// UPilot Editor tests - individually verified optional MonoHook points.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookAdditionalPointTests
    {
        private bool _masterEnabled;
        private bool _suppressUnchangedValues;
        private int _maxEventsPerSecond;
        private List<UPilotMonoHookPointState> _points;

        private static IEnumerable<TestCaseData> PointCases()
        {
            yield return Point(UPilotMonoHookPointId.GameObjectAddComponent, "gameObject.addComponent");
            yield return Point(UPilotMonoHookPointId.ComponentBehaviourEnabled, "component.behaviourEnabled");
            yield return Point(UPilotMonoHookPointId.ComponentRendererEnabled, "component.rendererEnabled");
            yield return Point(UPilotMonoHookPointId.ComponentColliderEnabled, "component.colliderEnabled");
            yield return Point(UPilotMonoHookPointId.ComponentCollider2DEnabled, "component.collider2DEnabled");
            yield return Point(UPilotMonoHookPointId.TransformTranslate, "transform.translate");
            yield return Point(UPilotMonoHookPointId.TransformRotate, "transform.rotate");
            yield return Point(UPilotMonoHookPointId.TransformRotateAround, "transform.rotateAround");
            yield return Point(UPilotMonoHookPointId.TransformLookAt, "transform.lookAt");
            yield return Point(UPilotMonoHookPointId.TransformSetAsFirstSibling, "transform.setAsFirstSibling");
            yield return Point(UPilotMonoHookPointId.TransformSetAsLastSibling, "transform.setAsLastSibling");
            yield return Point(UPilotMonoHookPointId.TransformDetachChildren, "transform.detachChildren");
        }

        private static TestCaseData Point(string pointId, string eventKind)
        {
            return new TestCaseData(pointId, eventKind).SetName("Point_" + pointId.Replace('.', '_'));
        }

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

        [TestCaseSource(nameof(PointCases))]
        public void SupportedPointInstallsAndCapturesItsOperation(string pointId, string eventKind)
        {
            var descriptor = UPilotMonoHookRegistry.Instance.Find(pointId);
            Assert.That(descriptor, Is.Not.Null, "点位未注册：" + pointId);
            var support = descriptor.Provider.CheckSupport(UPilotMonoHookRegistry.Instance.Context);
            if (!support.IsSupported)
                Assert.Ignore(pointId + " 条件跳过：" + support.Message);

            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = true;
            foreach (var point in UPilotMonoHookCatalog.All)
                settings.SetEnabled(point.Id, false);
            settings.SetEnabled(pointId, true);
            settings.suppressUnchangedValues = false;
            settings.maxEventsPerSecond = 1000;

            var parent = new GameObject("UPilotMonoHookPointParent");
            var first = new GameObject("UPilotMonoHookPointFirst");
            var target = new GameObject("UPilotMonoHookPointTarget");
            var behaviour = first.AddComponent<MonoHookBehaviourProbe>();
            first.transform.SetParent(parent.transform, false);
            target.transform.SetParent(parent.transform, false);
            target.transform.position = new Vector3(10f, 2f, 3f);

            var controller = new UPilotMonoHookController();
            try
            {
                var report = controller.Apply(false);
                Assert.That(report.Failed, Does.Not.Contain(pointId), pointId);
                UPilotMonoHookTelemetry.Clear();

                Invoke(pointId, parent, first, target, behaviour);

                var events = UPilotMonoHookTelemetry.Read(64);
                Assert.That(events.Any(item => item.kind == eventKind), Is.True, pointId);
            }
            finally
            {
                controller.UninstallAll();
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(parent);
            }
        }

        private static void Invoke(
            string pointId,
            GameObject parent,
            GameObject first,
            GameObject target,
            MonoHookBehaviourProbe behaviour)
        {
            switch (pointId)
            {
                case UPilotMonoHookPointId.GameObjectAddComponent:
                    first.AddComponent<BoxCollider>();
                    return;
                case UPilotMonoHookPointId.ComponentBehaviourEnabled:
                    behaviour.enabled = false;
                    return;
                case UPilotMonoHookPointId.ComponentRendererEnabled:
                    first.AddComponent<MeshRenderer>().enabled = false;
                    return;
                case UPilotMonoHookPointId.ComponentColliderEnabled:
                    first.AddComponent<BoxCollider>().enabled = false;
                    return;
                case UPilotMonoHookPointId.ComponentCollider2DEnabled:
                    first.AddComponent<BoxCollider2D>().enabled = false;
                    return;
                case UPilotMonoHookPointId.TransformTranslate:
                    first.transform.Translate(Vector3.one, Space.World);
                    return;
                case UPilotMonoHookPointId.TransformRotate:
                    first.transform.Rotate(new Vector3(5f, 10f, 15f), Space.Self);
                    return;
                case UPilotMonoHookPointId.TransformRotateAround:
                    first.transform.RotateAround(Vector3.zero, Vector3.up, 15f);
                    return;
                case UPilotMonoHookPointId.TransformLookAt:
                    first.transform.LookAt(target.transform, Vector3.up);
                    return;
                case UPilotMonoHookPointId.TransformSetAsFirstSibling:
                    first.transform.SetAsFirstSibling();
                    return;
                case UPilotMonoHookPointId.TransformSetAsLastSibling:
                    first.transform.SetAsLastSibling();
                    return;
                case UPilotMonoHookPointId.TransformDetachChildren:
                    parent.transform.DetachChildren();
                    return;
                default:
                    Assert.Fail("缺少点位调用器：" + pointId);
                    return;
            }
        }

        private sealed class MonoHookBehaviourProbe : MonoBehaviour { }
    }
}
