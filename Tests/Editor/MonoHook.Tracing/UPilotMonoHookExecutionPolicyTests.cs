// -----------------------------------------------------------------------
// UPilot Editor tests - pass-through execution safety.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;

namespace CodingRiver.UPilot.Tests
{
    [UPilotMonoHookPoint(
        "Unsafe execution policy test point",
        "tests.custom",
        Id = "tests.custom.unsafe-execution-policy",
        CategoryDisplayName = "Tests Custom",
        CategoryOrder = 9000,
        Order = 30)]
    internal sealed class UnsafeExecutionPolicyTestHookPoint : UPilotMethodHookPointBase
    {
        public const string PointId = "tests.custom.unsafe-execution-policy";
        public static int CreateBindingsCalls;

        protected override IEnumerable<UPilotMonoHookBinding> CreateBindings(UPilotMonoHookContext context)
        {
            CreateBindingsCalls++;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            yield return new UPilotMonoHookBinding(
                typeof(UnsafeExecutionPolicyTestHookPoint).GetMethod(nameof(Target), flags),
                typeof(UnsafeExecutionPolicyTestHookPoint).GetMethod(nameof(Replacement), flags),
                null,
                "UPilot.MonoHook.Tests.UnsafeExecutionPolicy");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Target(int value) => value + 1;

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static int Replacement(int value) => value;
    }

    public sealed class UPilotMonoHookExecutionPolicyTests
    {
        private const string PendingPlayModeApplyKey = "UPilot.MonoHook.Tracing.PendingPlayModeApply";
        private bool _masterEnabled;
        private bool _autoInjectEnabled;
        private bool _autoApplyOnEditorLoad;
        private bool _autoApplyOnPlayMode;
        private List<UPilotMonoHookPointState> _points;

        [SetUp]
        public void SetUp()
        {
            UnsafeExecutionPolicyTestHookPoint.CreateBindingsCalls = 0;
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            _masterEnabled = settings.masterEnabled;
            _autoInjectEnabled = settings.autoInjectEnabled;
            _autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad;
            _autoApplyOnPlayMode = settings.autoApplyOnPlayMode;
            _points = settings.points.Select(point => new UPilotMonoHookPointState(
                point.Id,
                point.Enabled,
                point.CaptureStackTrace,
                point.HookAllSafeOverloads,
                point.FilterProfileId,
                point.ExecutionMode)).ToList();
            foreach (var definition in UPilotMonoHookCatalog.All)
                settings.SetEnabled(definition.Id, false);
            settings.SetExecutionMode(
                UnsafeExecutionPolicyTestHookPoint.PointId,
                UPilotMonoHookExecutionMode.PassThrough);
            UPilotMonoHookInstallationService.UninstallAll();
            UPilotMonoHookTelemetry.Clear();
            SessionState.SetBool(PendingPlayModeApplyKey, false);
        }

        [TearDown]
        public void TearDown()
        {
            UPilotMonoHookInstallationService.UninstallAll();
            UPilotMonoHookTelemetry.Clear();
            SessionState.SetBool(PendingPlayModeApplyKey, false);
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.autoInjectEnabled = _autoInjectEnabled;
            settings.autoApplyOnEditorLoad = _autoApplyOnEditorLoad;
            settings.autoApplyOnPlayMode = _autoApplyOnPlayMode;
            settings.points = _points;
        }

        [Test]
        public void BuiltInPointsDeclarePassThroughAndDoNotDeclareInterception()
        {
            foreach (var descriptor in UPilotMonoHookRegistry.Instance.Points)
            {
                if (!UPilotMonoHookPointId.IsBuiltIn(descriptor.Definition.Id))
                    continue;

                var policy = descriptor.Provider as IUPilotMonoHookExecutionPolicyProvider;
                Assert.That(policy, Is.Not.Null, descriptor.Definition.Id);
                Assert.That(policy.GuaranteesPassThrough, Is.True, descriptor.Definition.Id);
                Assert.That(policy.SupportsInterception, Is.False, descriptor.Definition.Id);
                Assert.That(policy.ExecutionMode, Is.EqualTo(UPilotMonoHookExecutionMode.PassThrough), descriptor.Definition.Id);
            }
        }

        [Test]
        public void PassThroughModeRejectsProviderWithoutSafetyDeclaration()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.SetEnabled(UnsafeExecutionPolicyTestHookPoint.PointId, true);

            var report = new UPilotMonoHookController().Apply(false);

            Assert.That(report.Unsupported, Does.Contain(UnsafeExecutionPolicyTestHookPoint.PointId));
            Assert.That(report.Failed, Does.Contain(UnsafeExecutionPolicyTestHookPoint.PointId));
            Assert.That(UnsafeExecutionPolicyTestHookPoint.CreateBindingsCalls, Is.EqualTo(0));
        }

        [Test]
        public void EventSinkDoesNotThrowForInvalidTelemetryInput()
        {
            var sink = new UPilotMonoHookEventSink();

            Assert.DoesNotThrow(() => Assert.That(sink.Publish(null), Is.EqualTo(0)));
        }

        [Test]
        public void AutoInjectMasterSwitchBlocksEditorLoadInjection()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = true;
            settings.autoInjectEnabled = false;
            settings.autoApplyOnEditorLoad = true;
            settings.SetEnabled(UPilotMonoHookPointId.GameObjectSetActive, true);
            SessionState.SetBool(PendingPlayModeApplyKey, true);

            UPilotMonoHookAutoApply.ApplySavedConfiguration();

            Assert.That(UPilotMonoHookInstallationService.IsInstalled(
                UPilotMonoHookPointId.GameObjectSetActive), Is.False);
            Assert.That(SessionState.GetBool(PendingPlayModeApplyKey, true), Is.False);
            Assert.That(UPilotMonoHookAutoApply.LastResult, Does.Contain("自动注入关闭"));
        }
    }
}
