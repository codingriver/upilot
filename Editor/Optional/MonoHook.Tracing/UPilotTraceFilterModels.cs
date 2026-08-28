// -----------------------------------------------------------------------
// UPilot Editor - serializable UPilot Tracer filter models.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace CodingRiver.UPilot
{
    public static class UPilotTraceFilterProfileIds
    {
        public const string None = "__none__";
        public const string SceneObjects = "builtin.scene-objects";
        public const string CurrentSelection = "builtin.current-selection";
        public const string LegacyLifecycle = "migrated.lifecycle-scope";
    }

    public enum UPilotTraceFilterRuleEffect
    {
        Include,
        Exclude,
    }

    public enum UPilotTraceObjectScope
    {
        Any,
        SceneObject,
        Asset,
        EditorTemporary,
        PrefabStage,
    }

    public enum UPilotTraceStringMatchMode
    {
        Any,
        Equals,
        Contains,
        StartsWith,
        Wildcard,
        Regex,
    }

    public enum UPilotTraceHierarchyMatchMode
    {
        Any,
        Equals,
        Contains,
        StartsWith,
        UnderRoot,
        Wildcard,
        Regex,
    }

    public enum UPilotTraceHierarchyRelation
    {
        Any,
        RootObject,
        DirectChild,
    }

    public enum UPilotTraceTagMatchMode
    {
        Equals,
        Wildcard,
        Regex,
    }

    public enum UPilotTraceTypeMatchMode
    {
        Any,
        Exact,
        Assignable,
    }

    public enum UPilotTraceActiveState
    {
        Any,
        Active,
        Inactive,
    }

    public enum UPilotTraceEnabledState
    {
        Any,
        Enabled,
        Disabled,
    }

    public enum UPilotTracePrefabState
    {
        Any,
        PrefabInstance,
        PrefabAsset,
        NonPrefab,
    }

    public enum UPilotTraceSelectionScope
    {
        Any,
        SelectedObject,
        SelectedSubtree,
    }

    public enum UPilotTraceValueCondition
    {
        Any,
        Changed,
        BeforeEquals,
        AfterEquals,
        AfterContains,
        NumericDeltaAtLeast,
    }

    public enum UPilotTracePlayMode
    {
        Any,
        EditMode,
        PlayMode,
    }

    [Serializable]
    public sealed class UPilotTraceFilterRule
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "新规则";
        public bool Enabled = true;
        public UPilotTraceFilterRuleEffect Effect;
        public UPilotTraceObjectScope ObjectScope;
        public string AssemblyPatterns = string.Empty;
        public string NamespacePatterns = string.Empty;
        public string TypePatterns = string.Empty;
        public string TargetTypeName = string.Empty;
        public UPilotTraceTypeMatchMode TargetTypeMatchMode;
        public string RequiredComponentTypeName = string.Empty;
        public bool RequiredComponentIncludeDerived = true;
        public UPilotTraceStringMatchMode NameMatchMode;
        public string NamePattern = string.Empty;
        public bool IgnoreCase = true;
        public UPilotTraceHierarchyMatchMode HierarchyMatchMode;
        public string HierarchyPattern = string.Empty;
        public UPilotTraceHierarchyRelation HierarchyRelation;
        public UPilotTraceStringMatchMode SceneMatchMode;
        public string ScenePattern = string.Empty;
        public UPilotTraceStringMatchMode AssetPathMatchMode;
        public string AssetPathPattern = string.Empty;
        public int LayerMask = -1;
        public string Tags = string.Empty;
        public UPilotTraceTagMatchMode TagMatchMode;
        public UPilotTraceActiveState ActiveState;
        public UPilotTraceEnabledState ComponentEnabledState;
        public UPilotTraceEnabledState RequiredComponentEnabledState;
        public UPilotTracePrefabState PrefabState;
        public string PrefabAssetPathPattern = string.Empty;
        public int TargetInstanceId;
        public string TargetGlobalObjectId = string.Empty;
        public string EventSourcePatterns = string.Empty;
        public UPilotTraceSelectionScope SelectionScope;
        public string PointPatterns = string.Empty;
        public string MethodPatterns = string.Empty;
        public string PhasePatterns = string.Empty;
        public UPilotTracePlayMode PlayMode;
        public UPilotTraceStringMatchMode ParentNameMatchMode;
        public string ParentNamePattern = string.Empty;
        public UPilotTraceStringMatchMode AncestorNameMatchMode;
        public string AncestorNamePattern = string.Empty;
        public int MaxHierarchyDepth = -1;
        public UPilotTraceValueCondition ValueCondition;
        public string ValuePattern = string.Empty;
        public float NumericDeltaThreshold;
        public string CustomFilterId = string.Empty;
        public string CustomFilterArgument = string.Empty;

        public UPilotTraceFilterRule Clone()
        {
            return new UPilotTraceFilterRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Name,
                Enabled = Enabled,
                Effect = Effect,
                ObjectScope = ObjectScope,
                AssemblyPatterns = AssemblyPatterns,
                NamespacePatterns = NamespacePatterns,
                TypePatterns = TypePatterns,
                TargetTypeName = TargetTypeName,
                TargetTypeMatchMode = TargetTypeMatchMode,
                RequiredComponentTypeName = RequiredComponentTypeName,
                RequiredComponentIncludeDerived = RequiredComponentIncludeDerived,
                NameMatchMode = NameMatchMode,
                NamePattern = NamePattern,
                IgnoreCase = IgnoreCase,
                HierarchyMatchMode = HierarchyMatchMode,
                HierarchyPattern = HierarchyPattern,
                HierarchyRelation = HierarchyRelation,
                SceneMatchMode = SceneMatchMode,
                ScenePattern = ScenePattern,
                AssetPathMatchMode = AssetPathMatchMode,
                AssetPathPattern = AssetPathPattern,
                LayerMask = LayerMask,
                Tags = Tags,
                TagMatchMode = TagMatchMode,
                ActiveState = ActiveState,
                ComponentEnabledState = ComponentEnabledState,
                RequiredComponentEnabledState = RequiredComponentEnabledState,
                PrefabState = PrefabState,
                PrefabAssetPathPattern = PrefabAssetPathPattern,
                TargetInstanceId = TargetInstanceId,
                TargetGlobalObjectId = TargetGlobalObjectId,
                EventSourcePatterns = EventSourcePatterns,
                SelectionScope = SelectionScope,
                PointPatterns = PointPatterns,
                MethodPatterns = MethodPatterns,
                PhasePatterns = PhasePatterns,
                PlayMode = PlayMode,
                ParentNameMatchMode = ParentNameMatchMode,
                ParentNamePattern = ParentNamePattern,
                AncestorNameMatchMode = AncestorNameMatchMode,
                AncestorNamePattern = AncestorNamePattern,
                MaxHierarchyDepth = MaxHierarchyDepth,
                ValueCondition = ValueCondition,
                ValuePattern = ValuePattern,
                NumericDeltaThreshold = NumericDeltaThreshold,
                CustomFilterId = CustomFilterId,
                CustomFilterArgument = CustomFilterArgument,
            };
        }
    }

    [Serializable]
    public sealed class UPilotTraceFilterProfile
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "新过滤器";
        public bool Enabled = true;
        public bool BuiltIn;
        public List<UPilotTraceFilterRule> Rules = new List<UPilotTraceFilterRule>();

        public UPilotTraceFilterProfile Clone(string name = null)
        {
            var result = new UPilotTraceFilterProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? Name + " 副本" : name,
                Enabled = Enabled,
                BuiltIn = false,
            };
            foreach (var rule in Rules ?? new List<UPilotTraceFilterRule>())
                if (rule != null) result.Rules.Add(rule.Clone());
            return result;
        }
    }

    [Serializable]
    public sealed class UPilotTraceFilterStatistics
    {
        public string pointId;
        public string profileId;
        public long evaluated;
        public long accepted;
        public long rejected;
        public string lastDecision;
        public string lastReason;
    }

    [Serializable]
    internal sealed class UPilotTraceFilterPresetFile
    {
        public int schemaVersion = 1;
        public List<UPilotTraceFilterProfile> profiles = new List<UPilotTraceFilterProfile>();
    }
}
