// -----------------------------------------------------------------------
// UPilot Editor tests - UPilot Tracer target filters.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    [UPilotTraceFilter("tests.name-contains", "测试名称包含")]
    public sealed class TestNameContainsTraceFilter : UPilotTraceFilterBase
    {
        public override bool Matches(UPilotTraceFilterContext context, string argument, out string reason)
        {
            bool matched = (context.ObjectName ?? string.Empty).IndexOf(argument ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
            reason = matched ? "名称命中" : "名称未命中";
            return matched;
        }
    }

    public sealed class UPilotTraceFilterTests
    {
        private UPilotMonoHookSettings _settings;
        private string _oldGlobalProfileId;
        private List<UPilotTraceFilterProfile> _oldProfiles;
        private int _oldSchemaVersion;
        private string _oldAssemblyIncludes;
        private string _oldAssemblyExcludes;
        private string _oldNamespaceIncludes;
        private string _oldNamespaceExcludes;
        private string _oldTypeIncludes;
        private string _oldTypeExcludes;
        private List<UPilotMonoHookPointState> _oldPoints;
        private bool _oldPerObjectRateLimit;
        private int _oldMaxEventsPerObjectPerSecond;
        private bool _oldSuppressDuplicateEvents;
        private int _oldDuplicateEventWindowMilliseconds;
        private GameObject _root;
        private GameObject _child;
        private UnityEngine.Object _oldSelection;

        [SetUp]
        public void SetUp()
        {
            _settings = UPilotMonoHookSettings.instance;
            _oldGlobalProfileId = _settings.globalFilterProfileId;
            _oldProfiles = _settings.filterProfiles;
            _oldSchemaVersion = _settings.schemaVersion;
            _oldAssemblyIncludes = _settings.lifecycleAssemblyIncludes;
            _oldAssemblyExcludes = _settings.lifecycleAssemblyExcludes;
            _oldNamespaceIncludes = _settings.lifecycleNamespaceIncludes;
            _oldNamespaceExcludes = _settings.lifecycleNamespaceExcludes;
            _oldTypeIncludes = _settings.lifecycleTypeIncludes;
            _oldTypeExcludes = _settings.lifecycleTypeExcludes;
            var knownFilterProfileIds = new HashSet<string>(
                (_settings.filterProfiles ?? new List<UPilotTraceFilterProfile>())
                    .Where(profile => profile != null && !string.IsNullOrEmpty(profile.Id))
                    .Select(profile => profile.Id),
                StringComparer.Ordinal);
            _oldPoints = (_settings.points ?? new List<UPilotMonoHookPointState>())
                .Where(point => point != null)
                .Select(point => new UPilotMonoHookPointState(
                    point.Id,
                    point.Enabled,
                    point.CaptureStackTrace,
                    point.HookAllSafeOverloads,
                    knownFilterProfileIds.Contains(point.FilterProfileId) ? point.FilterProfileId : string.Empty,
                    point.ExecutionMode))
                .ToList();
            _oldSelection = Selection.activeObject;
            _oldPerObjectRateLimit = _settings.enablePerObjectRateLimit;
            _oldMaxEventsPerObjectPerSecond = _settings.maxEventsPerObjectPerSecond;
            _oldSuppressDuplicateEvents = _settings.suppressDuplicateEvents;
            _oldDuplicateEventWindowMilliseconds = _settings.duplicateEventWindowMilliseconds;
            _settings.globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            _settings.filterProfiles = new List<UPilotTraceFilterProfile>();
            UPilotTraceFilterEngine.ClearStatistics();
            _root = new GameObject("FilterRoot");
            _child = new GameObject("PlayerUnit");
            _child.transform.SetParent(_root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            Selection.activeObject = _oldSelection;
            if (_child != null) UnityEngine.Object.DestroyImmediate(_child);
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _settings.globalFilterProfileId = _oldGlobalProfileId;
            _settings.filterProfiles = _oldProfiles;
            _settings.schemaVersion = _oldSchemaVersion;
            _settings.lifecycleAssemblyIncludes = _oldAssemblyIncludes;
            _settings.lifecycleAssemblyExcludes = _oldAssemblyExcludes;
            _settings.lifecycleNamespaceIncludes = _oldNamespaceIncludes;
            _settings.lifecycleNamespaceExcludes = _oldNamespaceExcludes;
            _settings.lifecycleTypeIncludes = _oldTypeIncludes;
            _settings.lifecycleTypeExcludes = _oldTypeExcludes;
            _settings.points = _oldPoints;
            _settings.enablePerObjectRateLimit = _oldPerObjectRateLimit;
            _settings.maxEventsPerObjectPerSecond = _oldMaxEventsPerObjectPerSecond;
            _settings.suppressDuplicateEvents = _oldSuppressDuplicateEvents;
            _settings.duplicateEventWindowMilliseconds = _oldDuplicateEventWindowMilliseconds;
            _settings.EnsureDefaults();
            _settings.SaveSettings();
            UPilotTraceFilterEngine.ClearStatistics();
        }

        [Test]
        public void NameAndHierarchyRulesUseAndSemantics()
        {
            var profile = NewProfile("name-hierarchy");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "Player 名称",
                NameMatchMode = UPilotTraceStringMatchMode.Contains,
                NamePattern = "Player",
                HierarchyMatchMode = UPilotTraceHierarchyMatchMode.UnderRoot,
                HierarchyPattern = "FilterRoot",
            });
            UseProfile(profile);

            Assert.That(Evaluate(_child, "tests.filter"), Is.True);
            Assert.That(Evaluate(_root, "tests.filter"), Is.False);
        }

        [Test]
        public void ExcludeRuleHasPriorityOverIncludeRule()
        {
            var profile = NewProfile("include-exclude");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "包含场景对象",
                Effect = UPilotTraceFilterRuleEffect.Include,
                ObjectScope = UPilotTraceObjectScope.SceneObject,
            });
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "排除 Player",
                Effect = UPilotTraceFilterRuleEffect.Exclude,
                NameMatchMode = UPilotTraceStringMatchMode.Contains,
                NamePattern = "Player",
            });
            UseProfile(profile);

            Assert.That(Evaluate(_root, "tests.filter"), Is.True);
            Assert.That(Evaluate(_child, "tests.filter"), Is.False);
        }

        [Test]
        public void TypeAndRequiredComponentRulesMatchDerivedComponents()
        {
            var collider = _child.AddComponent<BoxCollider>();
            var profile = NewProfile("type-component");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                TargetTypeName = "UnityEngine.Collider",
                TargetTypeMatchMode = UPilotTraceTypeMatchMode.Assignable,
                RequiredComponentTypeName = "UnityEngine.BoxCollider",
                RequiredComponentIncludeDerived = true,
            });
            UseProfile(profile);

            Assert.That(Evaluate(collider, "tests.filter"), Is.True);
            Assert.That(Evaluate(_root, "tests.filter"), Is.False);
        }

        [Test]
        public void TemporaryObjectRuleCanSuppressEditorPlaceholders()
        {
            var profile = NewProfile("temporary");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "排除 Editor 临时对象",
                Effect = UPilotTraceFilterRuleEffect.Exclude,
                ObjectScope = UPilotTraceObjectScope.EditorTemporary,
            });
            UseProfile(profile);

            Assert.That(Evaluate(null, "tests.filter", "Missing GameObject for Object Field"), Is.False);
            Assert.That(Evaluate(_root, "tests.filter"), Is.True);
        }

        [Test]
        public void ValueRulesSupportChangedAndNumericDelta()
        {
            var profile = NewProfile("value");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                ValueCondition = UPilotTraceValueCondition.NumericDeltaAtLeast,
                NumericDeltaThreshold = 1.5f,
            });
            UseProfile(profile);

            Assert.That(Evaluate(_root, "tests.filter", null, "(0, 0, 0)", "(2, 0, 0)"), Is.True);
            Assert.That(Evaluate(_root, "tests.filter", null, "(0, 0, 0)", "(1, 0, 0)"), Is.False);
        }

        [Test]
        public void WildcardRegexActiveLayerTagAndPrefabRulesMatch()
        {
            _child.SetActive(true);
            _child.layer = 0;
            _child.tag = "Untagged";
            var collider = _child.AddComponent<BoxCollider>();
            collider.enabled = true;
            var profile = NewProfile("rich-target");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                NameMatchMode = UPilotTraceStringMatchMode.Wildcard,
                NamePattern = "Player*",
                LayerMask = 1,
                Tags = "Untagged",
                ActiveState = UPilotTraceActiveState.Active,
                ComponentEnabledState = UPilotTraceEnabledState.Enabled,
                PrefabState = UPilotTracePrefabState.NonPrefab,
            });
            UseProfile(profile);

            Assert.That(Evaluate(collider, "tests.filter"), Is.True);
            profile.Rules[0].NameMatchMode = UPilotTraceStringMatchMode.Regex;
            profile.Rules[0].NamePattern = "^Player\\w+$";
            Assert.That(Evaluate(collider, "tests.filter"), Is.True);
            collider.enabled = false;
            Assert.That(Evaluate(collider, "tests.filter"), Is.False);
        }

        [Test]
        public void EventAndHierarchyContextRulesMatchPointMethodPhaseParentAndMode()
        {
            var profile = NewProfile("event-context");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                PointPatterns = "tests.event.*",
                MethodPatterns = "DestroyImmediate*",
                PhasePatterns = "before",
                PlayMode = UPilotTracePlayMode.EditMode,
                ParentNameMatchMode = UPilotTraceStringMatchMode.Equals,
                ParentNamePattern = "FilterRoot",
                AncestorNameMatchMode = UPilotTraceStringMatchMode.Contains,
                AncestorNamePattern = "Filter",
                MaxHierarchyDepth = 1,
            });
            UseProfile(profile);

            Assert.That(EvaluateWithContext(_child, "tests.event.destroy", "DestroyImmediate(Object,bool)", "before", false), Is.True);
            Assert.That(EvaluateWithContext(_child, "tests.event.destroy", "DestroyImmediate(Object,bool)", "after", false), Is.False);
            Assert.That(EvaluateWithContext(_child, "tests.other", "DestroyImmediate(Object,bool)", "before", false), Is.False);
            Assert.That(EvaluateWithContext(_child, "tests.event.destroy", "SetActive(bool)", "before", false), Is.False);
            Assert.That(EvaluateWithContext(_child, "tests.event.destroy", "DestroyImmediate(Object,bool)", "before", true), Is.False);
        }

        [Test]
        public void HierarchyRelationAndRequiredComponentStateRulesMatch()
        {
            var collider = _child.AddComponent<BoxCollider>();
            collider.enabled = false;
            var profile = NewProfile("relations");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                HierarchyRelation = UPilotTraceHierarchyRelation.DirectChild,
                RequiredComponentTypeName = "UnityEngine.BoxCollider",
                RequiredComponentEnabledState = UPilotTraceEnabledState.Disabled,
            });
            UseProfile(profile);

            Assert.That(Evaluate(collider, "tests.relations"), Is.True);
            collider.enabled = true;
            Assert.That(Evaluate(collider, "tests.relations"), Is.False);
            Assert.That(Evaluate(_root, "tests.relations"), Is.False);
        }

        [Test]
        public void TagWildcardAndIdentityRulesMatchEventContext()
        {
            _child.tag = "Untagged";
            var profile = NewProfile("identity");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Tags = "Un*",
                TagMatchMode = UPilotTraceTagMatchMode.Wildcard,
                TargetInstanceId = 12345,
                EventSourcePatterns = "EditMode",
            });
            UseProfile(profile);

            var accepted = new UPilotMonoHookEvent
            {
                pointId = "tests.identity",
                kind = "tests.identity",
                target = _child,
                instanceId = 12345,
                eventSource = "EditMode",
            };
            Assert.That(UPilotTraceFilterEngine.Evaluate(accepted, false, out _), Is.True);
            accepted.instanceId = 12346;
            Assert.That(UPilotTraceFilterEngine.Evaluate(accepted, false, out _), Is.False);
        }

        [Test]
        public void DuplicateSuppressionReducesIdenticalEvents()
        {
            _settings.suppressDuplicateEvents = true;
            _settings.duplicateEventWindowMilliseconds = 60000;
            _settings.maxEventsPerSecond = 10000;
            _settings.EnsureDefaults();
            UPilotMonoHookTelemetry.Clear();
            var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;
            var first = new UPilotMonoHookEvent
            {
                pointId = "tests.duplicate",
                kind = "tests.duplicate",
                target = _child,
                objectName = _child.name,
                methodSignature = "Test()",
                phase = "after",
                beforeValue = "a",
                afterValue = "b",
            };
            Assert.That(sink.Publish(first), Is.GreaterThan(0));
            Assert.That(sink.Publish(new UPilotMonoHookEvent
            {
                pointId = first.pointId,
                kind = first.kind,
                target = _child,
                objectName = _child.name,
                methodSignature = first.methodSignature,
                phase = first.phase,
                beforeValue = first.beforeValue,
                afterValue = first.afterValue,
            }), Is.Zero);
            Assert.That(UPilotMonoHookTelemetry.DuplicateDroppedCount, Is.EqualTo(1));
        }

        [Test]
        public void PublishAppliesTargetFilterBeforeBuffering()
        {
            var profile = NewProfile("publish");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                NameMatchMode = UPilotTraceStringMatchMode.Equals,
                NamePattern = "PlayerUnit",
            });
            UseProfile(profile);
            UPilotMonoHookTelemetry.Clear();
            var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;

            Assert.That(sink.Publish(new UPilotMonoHookEvent
            {
                pointId = "tests.publish",
                kind = "tests.publish",
                objectName = _root.name,
                target = _root,
            }), Is.Zero);
            Assert.That(sink.Publish(new UPilotMonoHookEvent
            {
                pointId = "tests.publish",
                kind = "tests.publish",
                objectName = _child.name,
                target = _child,
            }), Is.GreaterThan(0));
            Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(1));
        }

        [Test]
        public void PublishAppliesEventPhaseAndMethodFiltersBeforeBuffering()
        {
            var profile = NewProfile("publish-event");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                PointPatterns = "tests.publish.*",
                MethodPatterns = "SetActive*",
                PhasePatterns = "before",
            });
            UseProfile(profile);
            UPilotMonoHookTelemetry.Clear();
            var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;

            Assert.That(sink.Publish(new UPilotMonoHookEvent
            {
                pointId = "tests.publish.active",
                kind = "tests.publish.active",
                methodSignature = "SetActive(bool)",
                phase = "before",
            }), Is.GreaterThan(0));
            Assert.That(sink.Publish(new UPilotMonoHookEvent
            {
                pointId = "tests.publish.active",
                kind = "tests.publish.active",
                methodSignature = "SetActive(bool)",
                phase = "after",
            }), Is.Zero);
            Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(1));
        }

        [Test]
        public void LegacyLifecyclePatternsMigrateToGlobalFilterProfile()
        {
            _settings.schemaVersion = 6;
            _settings.filterProfiles = new List<UPilotTraceFilterProfile>();
            _settings.globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            _settings.lifecycleAssemblyIncludes = "UPilot.*";
            _settings.lifecycleNamespaceIncludes = "CodingRiver.*";
            _settings.lifecycleTypeIncludes = "*TraceFilterTests";
            _settings.lifecycleTypeExcludes = "*OtherType";
            _settings.EnsureDefaults();

            Assert.That(_settings.FindFilterProfile(UPilotTraceFilterProfileIds.LegacyLifecycle), Is.Not.Null);
            Assert.That(_settings.globalFilterProfileId,
                Is.EqualTo(UPilotTraceFilterProfileIds.LegacyLifecycle));
            Assert.That(_settings.lifecycleTypeIncludes, Is.Empty);
        }

        [Test]
        public void SelectedSubtreeRuleMatchesOnlyCurrentSelectionTree()
        {
            Selection.activeGameObject = _root;
            var profile = NewProfile("selection");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                SelectionScope = UPilotTraceSelectionScope.SelectedSubtree,
            });
            UseProfile(profile);

            Assert.That(Evaluate(_child, "tests.filter"), Is.True);
            var outside = new GameObject("Outside");
            try { Assert.That(Evaluate(outside, "tests.filter"), Is.False); }
            finally { UnityEngine.Object.DestroyImmediate(outside); }
        }

        [Test]
        public void CustomAttributeFilterIsDiscoveredAndExecuted()
        {
            UPilotTraceFilterRegistry.Instance.Refresh();
            var profile = NewProfile("custom");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                CustomFilterId = "tests.name-contains",
                CustomFilterArgument = "Player",
            });
            UseProfile(profile);

            Assert.That(Evaluate(_child, "tests.filter"), Is.True);
            Assert.That(Evaluate(_root, "tests.filter"), Is.False);
        }

        [Test]
        public void FilterStatisticsTrackAcceptedAndRejectedEvents()
        {
            var profile = NewProfile("statistics");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                NameMatchMode = UPilotTraceStringMatchMode.Contains,
                NamePattern = "Player",
            });
            UseProfile(profile);

            Assert.That(Evaluate(_child, "tests.stats"), Is.True);
            Assert.That(Evaluate(_root, "tests.stats"), Is.False);
            var stats = UPilotTraceFilterEngine.GetStatistics("tests.stats", profile.Id);
            Assert.That(stats.evaluated, Is.EqualTo(2));
            Assert.That(stats.accepted, Is.EqualTo(1));
            Assert.That(stats.rejected, Is.EqualTo(1));
        }

        [Test]
        public void FilterPresetsRoundTripProfiles()
        {
            var profile = NewProfile("preset");
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "名称",
                NameMatchMode = UPilotTraceStringMatchMode.Equals,
                NamePattern = "PlayerUnit",
                HierarchyRelation = UPilotTraceHierarchyRelation.DirectChild,
                TagMatchMode = UPilotTraceTagMatchMode.Wildcard,
                Tags = "Player*",
                RequiredComponentEnabledState = UPilotTraceEnabledState.Enabled,
                PrefabAssetPathPattern = "Assets/Prefabs/*",
                TargetInstanceId = 42,
                TargetGlobalObjectId = "GlobalObjectId_V1-test",
                EventSourcePatterns = "EditMode",
            });
            UseProfile(profile);
            string path = Path.Combine(Path.GetTempPath(), "UPilotTraceFilters_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert.That(UPilotTraceFilterPresetService.Export(path, new[] { profile }), Is.EqualTo(1));
                _settings.filterProfiles = new List<UPilotTraceFilterProfile>();
                Assert.That(UPilotTraceFilterPresetService.Import(path, _settings, true), Is.EqualTo(1));
                Assert.That(_settings.FindFilterProfile(profile.Id), Is.Not.Null);
                var loaded = _settings.FindFilterProfile(profile.Id).Rules[0];
                Assert.That(loaded.NamePattern, Is.EqualTo("PlayerUnit"));
                Assert.That(loaded.HierarchyRelation, Is.EqualTo(UPilotTraceHierarchyRelation.DirectChild));
                Assert.That(loaded.TagMatchMode, Is.EqualTo(UPilotTraceTagMatchMode.Wildcard));
                Assert.That(loaded.RequiredComponentEnabledState, Is.EqualTo(UPilotTraceEnabledState.Enabled));
                Assert.That(loaded.PrefabAssetPathPattern, Is.EqualTo("Assets/Prefabs/*"));
                Assert.That(loaded.TargetInstanceId, Is.EqualTo(42));
                Assert.That(loaded.TargetGlobalObjectId, Is.EqualTo("GlobalObjectId_V1-test"));
                Assert.That(loaded.EventSourcePatterns, Is.EqualTo("EditMode"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private UPilotTraceFilterProfile NewProfile(string id)
        {
            return new UPilotTraceFilterProfile { Id = id, Name = id };
        }

        private void UseProfile(UPilotTraceFilterProfile profile)
        {
            _settings.filterProfiles = new List<UPilotTraceFilterProfile> { profile };
            _settings.globalFilterProfileId = profile.Id;
            _settings.EnsureDefaults();
        }

        private bool Evaluate(UnityEngine.Object target, string pointId, string objectName = null, string before = "", string after = "")
        {
            return UPilotTraceFilterEngine.Evaluate(
                pointId,
                target,
                objectName ?? target?.name,
                string.Empty,
                string.Empty,
                target?.GetType().FullName,
                string.Empty,
                before,
                after,
                true,
                out _);
        }

        private bool EvaluateWithContext(
            UnityEngine.Object target,
            string pointId,
            string methodSignature,
            string phase,
            bool isPlaying)
        {
            return UPilotTraceFilterEngine.Evaluate(
                pointId,
                target,
                target?.name,
                string.Empty,
                string.Empty,
                target?.GetType().FullName,
                methodSignature,
                string.Empty,
                string.Empty,
                phase,
                isPlaying,
                true,
                out _);
        }
    }
}
