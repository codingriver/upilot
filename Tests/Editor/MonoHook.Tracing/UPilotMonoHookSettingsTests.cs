// -----------------------------------------------------------------------
// UPilot Editor tests - manually managed MonoHook configuration.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    [UPilotMonoHookPoint(
        "Attribute test point",
        "tests.custom",
        Id = AttributeDiscoveredTestHookPoint.PointId,
        CategoryDisplayName = "Tests Custom",
        CategoryOrder = 9000,
        Order = 10)]
    internal sealed class AttributeDiscoveredTestHookPoint : UPilotMonoHookPointBase
    {
        public const string PointId = "tests.custom.attribute-provider";

        protected override void InstallCore(UPilotMonoHookContext context) { }

        protected override void UninstallCore(UPilotMonoHookContext context) { }
    }

    [UPilotMonoHookPoint(
        "Implicit ID test point",
        "tests.custom",
        CategoryDisplayName = "Tests Custom",
        CategoryOrder = 9000,
        Order = 20)]
    internal sealed class ImplicitIdTestHookPoint : UPilotMonoHookPointBase
    {
        protected override void InstallCore(UPilotMonoHookContext context) { }

        protected override void UninstallCore(UPilotMonoHookContext context) { }
    }

    public sealed class UPilotMonoHookSettingsTests
    {
        private bool _masterEnabled;
        private bool _autoApplyOnEditorLoad;
        private bool _suppressUnchangedValues;
        private int _maxEventsPerSecond;
        private bool _logEventsToConsole;
        private int _maxConsoleLogsPerSecond;
        private int _stackTraceMaxFrames;
        private int _stackTraceSampleEveryN;
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
            _autoApplyOnEditorLoad = settings.autoApplyOnEditorLoad;
            _suppressUnchangedValues = settings.suppressUnchangedValues;
            _maxEventsPerSecond = settings.maxEventsPerSecond;
            _logEventsToConsole = settings.logEventsToConsole;
            _maxConsoleLogsPerSecond = settings.maxConsoleLogsPerSecond;
            _stackTraceMaxFrames = settings.stackTraceMaxFrames;
            _stackTraceSampleEveryN = settings.stackTraceSampleEveryN;
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
                    point.HookAllSafeOverloads))
                .ToList();
        }

        [TearDown]
        public void TearDown()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.masterEnabled = _masterEnabled;
            settings.autoApplyOnEditorLoad = _autoApplyOnEditorLoad;
            settings.suppressUnchangedValues = _suppressUnchangedValues;
            settings.maxEventsPerSecond = _maxEventsPerSecond;
            settings.logEventsToConsole = _logEventsToConsole;
            settings.maxConsoleLogsPerSecond = _maxConsoleLogsPerSecond;
            settings.stackTraceMaxFrames = _stackTraceMaxFrames;
            settings.stackTraceSampleEveryN = _stackTraceSampleEveryN;
            settings.lifecycleAssemblyIncludes = _lifecycleAssemblyIncludes;
            settings.lifecycleAssemblyExcludes = _lifecycleAssemblyExcludes;
            settings.lifecycleNamespaceIncludes = _lifecycleNamespaceIncludes;
            settings.lifecycleNamespaceExcludes = _lifecycleNamespaceExcludes;
            settings.lifecycleTypeIncludes = _lifecycleTypeIncludes;
            settings.lifecycleTypeExcludes = _lifecycleTypeExcludes;
            settings.points = _points;
            settings.EnsureDefaults();
        }

        [Test]
        public void CatalogContainsUniqueStableIds()
        {
            var ids = UPilotMonoHookCatalog.All.Select(definition => definition.Id).ToList();
            Assert.That(ids.Count, Is.GreaterThan(0));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleOnEnable), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.TransformLocalScale), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.TransformSetPositionAndRotation), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.TransformSetLocalPositionAndRotation), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.GameObjectAddComponent), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.ComponentRendererEnabled), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.ComponentColliderEnabled), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.ComponentCollider2DEnabled), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.TransformTranslate), Is.Not.Null);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleUpdate).HighFrequency, Is.True);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleFixedUpdate).DefaultEnabled, Is.False);
            Assert.That(UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleLateUpdate).DefaultEnabled, Is.False);
        }

        [Test]
        public void CatalogDefinitionsComeFromAttributedProviders()
        {
            UPilotMonoHookCatalog.Refresh();

            foreach (var descriptor in UPilotMonoHookRegistry.Instance.Points)
            {
                Assert.That(descriptor.ProviderType, Is.Not.Null, descriptor.Definition.Id);
                Assert.That(
                    descriptor.ProviderType.GetCustomAttribute<UPilotMonoHookPointAttribute>(false),
                    Is.Not.Null,
                    descriptor.Definition.Id);
                Assert.That(descriptor.IsValid, Is.True, descriptor.DiscoveryError);
            }
        }

        [Test]
        public void ProviderInConsumerEditorAssemblyIsDiscoveredAutomatically()
        {
            UPilotMonoHookCatalog.Refresh();

            var descriptor = UPilotMonoHookRegistry.Instance.Find(AttributeDiscoveredTestHookPoint.PointId);

            Assert.That(descriptor, Is.Not.Null);
            Assert.That(descriptor.IsValid, Is.True, descriptor.DiscoveryError);
            Assert.That(descriptor.ProviderType, Is.EqualTo(typeof(AttributeDiscoveredTestHookPoint)));
            Assert.That(descriptor.Definition.CategoryId, Is.EqualTo("tests.custom"));
            Assert.That(descriptor.Definition.CategoryDisplayName, Is.EqualTo("Tests Custom"));
            Assert.That(descriptor.Definition.DefaultEnabled, Is.False);
            Assert.That(descriptor.Provider, Is.InstanceOf<AttributeDiscoveredTestHookPoint>());
        }

        [Test]
        public void ProviderCanOmitIdAndUsesAssemblyAndFullTypeName()
        {
            UPilotMonoHookCatalog.Refresh();

            string expectedId = UPilotMonoHookPointIdentity.FromProviderType(
                typeof(ImplicitIdTestHookPoint));
            var descriptor = UPilotMonoHookRegistry.Instance.Find(expectedId);
            var attribute = typeof(ImplicitIdTestHookPoint)
                .GetCustomAttribute<UPilotMonoHookPointAttribute>(false);

            Assert.That(expectedId, Is.EqualTo(
                $"provider:{typeof(ImplicitIdTestHookPoint).Assembly.GetName().Name}:{typeof(ImplicitIdTestHookPoint).FullName}"));
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.Id, Is.Null.Or.Empty);
            Assert.That(descriptor, Is.Not.Null);
            Assert.That(descriptor.IsValid, Is.True, descriptor.DiscoveryError);
            Assert.That(descriptor.ProviderType, Is.EqualTo(typeof(ImplicitIdTestHookPoint)));
            Assert.That(descriptor.Definition.Id, Is.EqualTo(expectedId));
            Assert.That(descriptor.Definition.DefaultEnabled, Is.False);

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            var state = settings.points.FirstOrDefault(point => point.Id == expectedId);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.Enabled, Is.False);
            Assert.That(state.CaptureStackTrace, Is.False);
        }

        [Test]
        public void EnsureDefaultsAddsMissingCatalogEntries()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.points = new List<UPilotMonoHookPointState>();
            settings.EnsureDefaults();

            Assert.That(settings.points.Count, Is.EqualTo(UPilotMonoHookCatalog.All.Count));
            Assert.That(settings.points.All(point => !point.Enabled), Is.True);
            Assert.That(settings.points.All(point => !point.CaptureStackTrace), Is.True);
            Assert.That(settings.points.All(point => !point.HookAllSafeOverloads), Is.True);
            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleOnEnable), Is.False);
            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.TransformPosition), Is.False);
        }

        [Test]
        public void CategoryToggleOnlyChangesMatchingPoints()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnEnable, false);
            settings.SetCategoryEnabled(UPilotMonoHookPointCategory.Transform, true);

            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.TransformPosition), Is.True);
            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.TransformLocalScale), Is.True);
            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleOnEnable), Is.False);

            settings.SetCategoryEnabled(UPilotMonoHookPointCategory.Transform, false);
            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.TransformPosition), Is.False);
        }

        [Test]
        public void MasterSwitchAffectsEffectiveStateWithoutChangingPointSelection()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.SetEnabled(UPilotMonoHookPointId.LifecycleOnEnable, true);
            settings.masterEnabled = false;

            Assert.That(settings.IsConfiguredEnabled(UPilotMonoHookPointId.LifecycleOnEnable), Is.True);
            Assert.That(settings.IsEnabled(UPilotMonoHookPointId.LifecycleOnEnable), Is.False);
        }

        [Test]
        public void RuntimeProtectionSettingsAreClampedAndRemainOptIn()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.autoApplyOnEditorLoad = false;
            settings.maxEventsPerSecond = 0;
            settings.logEventsToConsole = false;
            settings.maxConsoleLogsPerSecond = 0;
            settings.stackTraceMaxFrames = 0;
            settings.stackTraceSampleEveryN = 0;

            settings.EnsureDefaults();

            Assert.That(settings.autoApplyOnEditorLoad, Is.False);
            Assert.That(settings.maxEventsPerSecond, Is.EqualTo(1));
            Assert.That(settings.logEventsToConsole, Is.False);
            Assert.That(settings.maxConsoleLogsPerSecond, Is.EqualTo(1));
            Assert.That(settings.stackTraceMaxFrames, Is.EqualTo(1));
            Assert.That(settings.stackTraceSampleEveryN, Is.EqualTo(1));
        }

        [Test]
        public void StackTraceCaptureIsConfiguredPerPointAndDefaultsOff()
        {
            var settings = UPilotMonoHookSettings.instance;
            Assert.That(settings.ShouldCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive), Is.False);

            settings.SetCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive, true);

            Assert.That(settings.ShouldCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive), Is.True);
            Assert.That(settings.ShouldCaptureStackTrace(UPilotMonoHookPointId.TransformPosition), Is.False);
        }

        [Test]
        public void HookAllSafeOverloadsIsConfiguredPerPointAndDefaultsOff()
        {
            var settings = UPilotMonoHookSettings.instance;
            Assert.That(settings.ShouldHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectDestroy), Is.False);

            settings.SetHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectDestroy, true);

            Assert.That(settings.ShouldHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectDestroy), Is.True);
            Assert.That(settings.ShouldHookAllSafeOverloads(UPilotMonoHookPointId.GameObjectInstantiate), Is.False);
        }

        [Test]
        public void LifecycleScopeSupportsAssemblyNamespaceAndTypePatterns()
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.lifecycleAssemblyIncludes = "UPilot.*.Tests";
            settings.lifecycleNamespaceIncludes = "CodingRiver.UPilot.*";
            settings.lifecycleTypeIncludes = "*UPilotMonoHookSettingsTests";

            Assert.That(
                UPilotMonoHookLifecycleFilter.Includes(typeof(UPilotMonoHookSettingsTests), settings, out var includedReason),
                Is.True,
                includedReason);

            settings.lifecycleTypeExcludes = "*SettingsTests";
            Assert.That(
                UPilotMonoHookLifecycleFilter.Includes(typeof(UPilotMonoHookSettingsTests), settings, out var excludedReason),
                Is.False);
            Assert.That(excludedReason, Does.Contain("类型命中排除范围"));
        }

        [Test]
        public void TracingAssemblyDoesNotReferenceMainEditorAssembly()
        {
            var references = typeof(UPilotMonoHookController).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ToArray();

            Assert.That(typeof(UPilotMonoHookController).Assembly.GetName().Name,
                Is.EqualTo("UPilot.MonoHook.Tracing.Editor"));
            Assert.That(references, Does.Contain("UPilot.MonoHook.Editor"));
            Assert.That(references, Does.Contain("UPilot.MonoHook.Tracing.Contracts.Editor"));
            Assert.That(references, Does.Not.Contain("UPilot.Editor"));
        }

        [Test]
        public void ExtensionContractsAreInDedicatedAssembly()
        {
            Assert.That(typeof(UPilotMonoHookPointAttribute).Assembly.GetName().Name,
                Is.EqualTo("UPilot.MonoHook.Tracing.Contracts.Editor"));
            Assert.That(typeof(UPilotMonoHookPointBase).Assembly, Is.EqualTo(typeof(UPilotMonoHookEvent).Assembly));
        }

        [Test]
        public void SettingsDeclareProjectSettingsPersistencePath()
        {
            var attribute = typeof(UPilotMonoHookSettings).GetCustomAttribute<UnityEditor.FilePathAttribute>();

            Assert.That(attribute, Is.Not.Null);
            Assert.That(UPilotMonoHookSettings.GetAssetPath(), Is.EqualTo("ProjectSettings/UPilotMonoHookSettings.asset"));
        }
    }
}
