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
    [Serializable]
    public sealed class UPilotMonoHookPointState
    {
        public string Id;
        public bool Enabled;
        public bool CaptureStackTrace;

        public UPilotMonoHookPointState() { }

        public UPilotMonoHookPointState(string id, bool enabled, bool captureStackTrace = false)
        {
            Id = id;
            Enabled = enabled;
            CaptureStackTrace = captureStackTrace;
        }
    }

    /// <summary>
    /// Version-controlled project configuration for manually selected MonoHook points.
    /// </summary>
    [FilePath("ProjectSettings/UPilotMonoHookSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UPilotMonoHookSettings : ScriptableSingleton<UPilotMonoHookSettings>
    {
        private const string AssetPath = "ProjectSettings/UPilotMonoHookSettings.asset";
        public const int CurrentSchemaVersion = 5;

        public int schemaVersion = CurrentSchemaVersion;
        public bool masterEnabled = true;
        public bool autoApplyOnEditorLoad;
        public bool suppressUnchangedValues = true;
        public int maxEventsPerSecond = 1000;
        public bool logEventsToConsole;
        public int maxConsoleLogsPerSecond = 50;
        public int stackTraceMaxFrames = 16;
        public int stackTraceSampleEveryN = 1;
        public string lifecycleAssemblyIncludes = string.Empty;
        public string lifecycleAssemblyExcludes = string.Empty;
        public string lifecycleNamespaceIncludes = string.Empty;
        public string lifecycleNamespaceExcludes = string.Empty;
        public string lifecycleTypeIncludes = string.Empty;
        public string lifecycleTypeExcludes = string.Empty;
        public List<UPilotMonoHookPointState> points = new List<UPilotMonoHookPointState>();

        public void EnsureDefaults()
        {
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
            maxEventsPerSecond = Math.Max(1, maxEventsPerSecond);
            maxConsoleLogsPerSecond = Math.Max(1, Math.Min(200, maxConsoleLogsPerSecond));
            stackTraceMaxFrames = Math.Max(1, stackTraceMaxFrames);
            stackTraceSampleEveryN = Math.Max(1, stackTraceSampleEveryN);
            schemaVersion = CurrentSchemaVersion;
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

        public bool ShouldCaptureStackTrace(string id)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return point != null && point.CaptureStackTrace;
        }

        public void SetCaptureStackTrace(string id, bool capture)
        {
            EnsureDefaults();
            var point = points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (point == null) return;
            point.CaptureStackTrace = capture;
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
            autoApplyOnEditorLoad = false;
            suppressUnchangedValues = true;
            maxEventsPerSecond = 1000;
            logEventsToConsole = false;
            maxConsoleLogsPerSecond = 50;
            stackTraceMaxFrames = 16;
            stackTraceSampleEveryN = 1;
            lifecycleAssemblyIncludes = string.Empty;
            lifecycleAssemblyExcludes = string.Empty;
            lifecycleNamespaceIncludes = string.Empty;
            lifecycleNamespaceExcludes = string.Empty;
            lifecycleTypeIncludes = string.Empty;
            lifecycleTypeExcludes = string.Empty;
            points = new List<UPilotMonoHookPointState>();
            EnsureDefaults();
            SaveSettings();
        }

        public static string GetAssetPath() => AssetPath;
    }
}
