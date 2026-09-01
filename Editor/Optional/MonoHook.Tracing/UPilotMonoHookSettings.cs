// -----------------------------------------------------------------------
// UPilot Editor - project-level MonoHook settings.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace CodingRiver.UPilot
{
    public enum UPilotStackTraceCaptureMode
    {
        Disabled,
        SelectedPoints,
        AllEnabledPoints,
    }

    [Serializable]
    public sealed class UPilotMonoHookPointState
    {
        public string Id;
        public bool Enabled;
        // Used when stackTraceCaptureMode is SelectedPoints.
        public bool CaptureStackTrace;
        public bool HookAllSafeOverloads;
        public UPilotMonoHookExecutionMode ExecutionMode;
        // Empty means inheriting the global filter profile.
        public string FilterProfileId;

        public UPilotMonoHookPointState() { }

        public UPilotMonoHookPointState(
            string id,
            bool enabled,
            bool captureStackTrace = false,
            bool hookAllSafeOverloads = false,
            string filterProfileId = "",
            UPilotMonoHookExecutionMode executionMode = UPilotMonoHookExecutionMode.PassThrough)
        {
            Id = id;
            Enabled = enabled;
            CaptureStackTrace = captureStackTrace;
            HookAllSafeOverloads = hookAllSafeOverloads;
            ExecutionMode = executionMode;
            FilterProfileId = filterProfileId ?? string.Empty;
        }
    }

    /// <summary>
    /// Version-controlled project configuration for manually selected MonoHook points.
    /// </summary>
    [FilePath("ProjectSettings/UPilotMonoHookSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UPilotMonoHookSettings : ScriptableSingleton<UPilotMonoHookSettings>
    {
        private const string AssetPath = "ProjectSettings/UPilotMonoHookSettings.asset";
        public const int CurrentSchemaVersion = 12;

        public int schemaVersion = CurrentSchemaVersion;
        public bool masterEnabled = true;
        public bool autoInjectEnabled;
        public bool autoApplyOnEditorLoad;
        public bool autoApplyOnPlayMode;
        public bool suppressUnchangedValues = true;
        public int maxEventsPerSecond = 1000;
        public bool enablePerObjectRateLimit;
        public int maxEventsPerObjectPerSecond = 100;
        public bool suppressDuplicateEvents;
        public int duplicateEventWindowMilliseconds = 100;
        public bool logEventsToConsole;
        public int maxConsoleLogsPerSecond = 50;
        // Kept for serialized/source compatibility. The effective policy is
        // stackTraceCaptureMode; this mirrors whether capture is globally active.
        public bool captureStackTrace;
        public UPilotStackTraceCaptureMode stackTraceCaptureMode;
        public int stackTraceMaxFrames = 16;
        public int stackTraceSampleEveryN = 1;
        public string lifecycleAssemblyIncludes = string.Empty;
        public string lifecycleAssemblyExcludes = string.Empty;
        public string lifecycleNamespaceIncludes = string.Empty;
        public string lifecycleNamespaceExcludes = string.Empty;
        public string lifecycleTypeIncludes = string.Empty;
        public string lifecycleTypeExcludes = string.Empty;
        public string globalFilterProfileId = UPilotTraceFilterProfileIds.None;
        public bool pointFilterOverridesEnabled;
        public List<UPilotTraceFilterProfile> filterProfiles = new List<UPilotTraceFilterProfile>();
        public List<UPilotMonoHookPointState> points = new List<UPilotMonoHookPointState>();

        public void EnsureDefaults()
        {
            if (schemaVersion < 8)
            {
                enablePerObjectRateLimit = false;
                maxEventsPerObjectPerSecond = 100;
                suppressDuplicateEvents = false;
                duplicateEventWindowMilliseconds = 100;
            }
            if (schemaVersion < 9)
                autoApplyOnPlayMode = false;
            if (schemaVersion < 10)
                autoInjectEnabled = autoApplyOnEditorLoad || autoApplyOnPlayMode;
            if (schemaVersion < 11)
                captureStackTrace = false;
            if (schemaVersion < 12)
                stackTraceCaptureMode = captureStackTrace
                    ? UPilotStackTraceCaptureMode.AllEnabledPoints
                    : UPilotStackTraceCaptureMode.Disabled;
            if (points == null)
                points = new List<UPilotMonoHookPointState>();

            var known = new HashSet<string>(points.Where(p => p != null && !string.IsNullOrEmpty(p.Id)).Select(p => p.Id), StringComparer.Ordinal);
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                if (!known.Contains(definition.Id))
                    points.Add(new UPilotMonoHookPointState(definition.Id, definition.DefaultEnabled));
            }

            // Preserve settings for temporarily unavailable custom providers so their
            // choices return when the defining Editor assembly is restored.
            points.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Id));
            captureStackTrace = stackTraceCaptureMode != UPilotStackTraceCaptureMode.Disabled;
            maxEventsPerSecond = Math.Max(1, maxEventsPerSecond);
            maxEventsPerObjectPerSecond = Math.Max(1, Math.Min(10000, maxEventsPerObjectPerSecond));
            duplicateEventWindowMilliseconds = Math.Max(1, Math.Min(60000, duplicateEventWindowMilliseconds));
            maxConsoleLogsPerSecond = Math.Max(1, Math.Min(200, maxConsoleLogsPerSecond));
            stackTraceMaxFrames = Math.Max(1, stackTraceMaxFrames);
            stackTraceSampleEveryN = Math.Max(1, stackTraceSampleEveryN);
            EnsureFilterDefaults();
            MigrateLegacyLifecycleFilters();
            schemaVersion = CurrentSchemaVersion;
        }

        private void EnsureFilterDefaults()
        {
            if (filterProfiles == null)
                filterProfiles = new List<UPilotTraceFilterProfile>();
            filterProfiles.RemoveAll(profile => profile == null || string.IsNullOrWhiteSpace(profile.Id));

            var unique = new HashSet<string>(StringComparer.Ordinal);
            filterProfiles.RemoveAll(profile => !unique.Add(profile.Id));
            foreach (var profile in filterProfiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "未命名过滤器";
                if (profile.Rules == null) profile.Rules = new List<UPilotTraceFilterRule>();
                profile.Rules.RemoveAll(rule => rule == null);
                foreach (var rule in profile.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(rule.Name)) rule.Name = "未命名规则";
                }
            }

            if (FindFilterProfile(UPilotTraceFilterProfileIds.SceneObjects) == null)
                filterProfiles.Insert(0, CreateSceneObjectsProfile());
            if (FindFilterProfile(UPilotTraceFilterProfileIds.CurrentSelection) == null)
                filterProfiles.Insert(Math.Min(1, filterProfiles.Count), CreateCurrentSelectionProfile());

            if (string.IsNullOrWhiteSpace(globalFilterProfileId))
                globalFilterProfileId = UPilotTraceFilterProfileIds.None;
        }

        private void MigrateLegacyLifecycleFilters()
        {
            if (schemaVersion >= CurrentSchemaVersion) return;
            bool hasLegacy = !string.IsNullOrWhiteSpace(lifecycleAssemblyIncludes) ||
                             !string.IsNullOrWhiteSpace(lifecycleAssemblyExcludes) ||
                             !string.IsNullOrWhiteSpace(lifecycleNamespaceIncludes) ||
                             !string.IsNullOrWhiteSpace(lifecycleNamespaceExcludes) ||
                             !string.IsNullOrWhiteSpace(lifecycleTypeIncludes) ||
                             !string.IsNullOrWhiteSpace(lifecycleTypeExcludes);
            if (!hasLegacy) return;

            var profile = FindFilterProfile(UPilotTraceFilterProfileIds.LegacyLifecycle);
            if (profile == null)
            {
                profile = new UPilotTraceFilterProfile
                {
                    Id = UPilotTraceFilterProfileIds.LegacyLifecycle,
                    Name = "迁移的生命周期范围",
                };
                if (!string.IsNullOrWhiteSpace(lifecycleAssemblyIncludes) ||
                    !string.IsNullOrWhiteSpace(lifecycleNamespaceIncludes) ||
                    !string.IsNullOrWhiteSpace(lifecycleTypeIncludes))
                {
                    profile.Rules.Add(new UPilotTraceFilterRule
                    {
                        Name = "原包含范围",
                        Effect = UPilotTraceFilterRuleEffect.Include,
                        AssemblyPatterns = lifecycleAssemblyIncludes,
                        NamespacePatterns = lifecycleNamespaceIncludes,
                        TypePatterns = lifecycleTypeIncludes,
                    });
                }
                AddLegacyExcludeRule(profile, "程序集排除", lifecycleAssemblyExcludes, null, null);
                AddLegacyExcludeRule(profile, "命名空间排除", null, lifecycleNamespaceExcludes, null);
                AddLegacyExcludeRule(profile, "类型排除", null, null, lifecycleTypeExcludes);
                filterProfiles.Add(profile);
            }

            if (string.IsNullOrEmpty(globalFilterProfileId) ||
                string.Equals(globalFilterProfileId, UPilotTraceFilterProfileIds.None, StringComparison.Ordinal))
                globalFilterProfileId = profile.Id;

            lifecycleAssemblyIncludes = string.Empty;
            lifecycleAssemblyExcludes = string.Empty;
            lifecycleNamespaceIncludes = string.Empty;
            lifecycleNamespaceExcludes = string.Empty;
            lifecycleTypeIncludes = string.Empty;
            lifecycleTypeExcludes = string.Empty;
        }

        private static void AddLegacyExcludeRule(
            UPilotTraceFilterProfile profile,
            string name,
            string assembly,
            string namespaceName,
            string type)
        {
            if (string.IsNullOrWhiteSpace(assembly) &&
                string.IsNullOrWhiteSpace(namespaceName) &&
                string.IsNullOrWhiteSpace(type)) return;
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = name,
                Effect = UPilotTraceFilterRuleEffect.Exclude,
                AssemblyPatterns = assembly ?? string.Empty,
                NamespacePatterns = namespaceName ?? string.Empty,
                TypePatterns = type ?? string.Empty,
            });
        }

        private static UPilotTraceFilterProfile CreateSceneObjectsProfile()
        {
            var profile = new UPilotTraceFilterProfile
            {
                Id = UPilotTraceFilterProfileIds.SceneObjects,
                Name = "场景业务对象",
                BuiltIn = true,
            };
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "包含场景对象",
                Effect = UPilotTraceFilterRuleEffect.Include,
                ObjectScope = UPilotTraceObjectScope.SceneObject,
            });
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "排除 Editor 临时对象",
                Effect = UPilotTraceFilterRuleEffect.Exclude,
                ObjectScope = UPilotTraceObjectScope.EditorTemporary,
            });
            return profile;
        }

        private static UPilotTraceFilterProfile CreateCurrentSelectionProfile()
        {
            var profile = new UPilotTraceFilterProfile
            {
                Id = UPilotTraceFilterProfileIds.CurrentSelection,
                Name = "当前选中及子树",
                BuiltIn = true,
            };
            profile.Rules.Add(new UPilotTraceFilterRule
            {
                Name = "选中对象及子树",
                Effect = UPilotTraceFilterRuleEffect.Include,
                SelectionScope = UPilotTraceSelectionScope.SelectedSubtree,
            });
            return profile;
        }

        public bool IsEnabled(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return masterEnabled && point != null && point.Enabled;
        }

        public bool IsConfiguredEnabled(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return point != null && point.Enabled;
        }

        public void SetEnabled(string id, bool enabled)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point == null) return;
            point.Enabled = enabled;
        }

        public bool ShouldCaptureStackTrace()
        {
            EnsureDefaults();
            return stackTraceCaptureMode != UPilotStackTraceCaptureMode.Disabled;
        }

        public void SetCaptureStackTrace(bool capture)
        {
            EnsureDefaults();
            stackTraceCaptureMode = capture
                ? UPilotStackTraceCaptureMode.AllEnabledPoints
                : UPilotStackTraceCaptureMode.Disabled;
            captureStackTrace = capture;
        }

        public bool ShouldCaptureStackTrace(string id)
        {
            EnsureDefaults();
            switch (stackTraceCaptureMode)
            {
                case UPilotStackTraceCaptureMode.SelectedPoints:
                    var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                    return point != null && point.CaptureStackTrace;
                case UPilotStackTraceCaptureMode.AllEnabledPoints:
                    return IsConfiguredEnabled(id);
                default:
                    return false;
            }
        }

        public void SetCaptureStackTrace(string id, bool capture)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point != null)
                point.CaptureStackTrace = capture;
        }

        public bool ShouldHookAllSafeOverloads(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return point != null && point.HookAllSafeOverloads;
        }

        public void SetHookAllSafeOverloads(string id, bool hookAllSafeOverloads)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point == null) return;
            point.HookAllSafeOverloads = hookAllSafeOverloads;
        }

        public UPilotMonoHookExecutionMode GetExecutionMode(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return point?.ExecutionMode ?? UPilotMonoHookExecutionMode.PassThrough;
        }

        public void SetExecutionMode(string id, UPilotMonoHookExecutionMode mode)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point != null)
                point.ExecutionMode = mode;
        }

        public string GetConfiguredFilterProfileId(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return point?.FilterProfileId ?? string.Empty;
        }

        public void SetFilterProfileId(string id, string filterProfileId)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point != null)
                point.FilterProfileId = filterProfileId ?? string.Empty;
        }

        public string GetEffectiveFilterProfileId(string id)
        {
            EnsureDefaults();
            if (pointFilterOverridesEnabled)
            {
                string configured = GetConfiguredFilterProfileId(id);
                if (!string.IsNullOrEmpty(configured))
                    return configured;
            }
            return globalFilterProfileId;
        }

        public UPilotTraceFilterProfile ResolveFilterProfile(string pointId)
        {
            EnsureDefaults();
            string profileId = GetEffectiveFilterProfileId(pointId);
            if (string.IsNullOrEmpty(profileId) || string.Equals(profileId, UPilotTraceFilterProfileIds.None, StringComparison.Ordinal))
                return null;
            return FindFilterProfile(profileId);
        }

        public UPilotTraceFilterProfile FindFilterProfile(string profileId)
        {
            if (filterProfiles == null || string.IsNullOrEmpty(profileId)) return null;
            return filterProfiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));
        }

        public void SetCategoryEnabled(UPilotMonoHookPointCategory category, bool enabled)
        {
            SetCategoryEnabled(UPilotMonoHookCategoryId.FromLegacy(category), enabled);
        }

        public void SetCategoryEnabled(string categoryId, bool enabled)
        {
            EnsureDefaults();
            foreach (var definition in UPilotMonoHookCatalog.All)
            {
                if (string.Equals(definition.CategoryId, categoryId, StringComparison.Ordinal))
                    SetEnabled(definition.Id, enabled);
            }
        }

        public void SaveSettings()
        {
            EnsureDefaults();
            Save(true);
        }

        public void ResetToDefaults()
        {
            masterEnabled = true;
            autoInjectEnabled = false;
            autoApplyOnEditorLoad = false;
            autoApplyOnPlayMode = false;
            suppressUnchangedValues = true;
            maxEventsPerSecond = 1000;
            enablePerObjectRateLimit = false;
            maxEventsPerObjectPerSecond = 100;
            suppressDuplicateEvents = false;
            duplicateEventWindowMilliseconds = 100;
            logEventsToConsole = false;
            maxConsoleLogsPerSecond = 50;
            captureStackTrace = false;
            stackTraceCaptureMode = UPilotStackTraceCaptureMode.Disabled;
            stackTraceMaxFrames = 16;
            stackTraceSampleEveryN = 1;
            lifecycleAssemblyIncludes = string.Empty;
            lifecycleAssemblyExcludes = string.Empty;
            lifecycleNamespaceIncludes = string.Empty;
            lifecycleNamespaceExcludes = string.Empty;
            lifecycleTypeIncludes = string.Empty;
            lifecycleTypeExcludes = string.Empty;
            globalFilterProfileId = UPilotTraceFilterProfileIds.None;
            pointFilterOverridesEnabled = false;
            filterProfiles = new List<UPilotTraceFilterProfile>();
            points = new List<UPilotMonoHookPointState>();
            EnsureDefaults();
            SaveSettings();
        }

        public static string GetAssetPath() => AssetPath;
    }
}
