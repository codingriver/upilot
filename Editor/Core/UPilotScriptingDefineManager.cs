// -----------------------------------------------------------------------
// UPilot Editor - maintains the public UPILOT scripting define.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [InitializeOnLoad]
    internal static class UPilotScriptingDefineManager
    {
        internal const string Define = "UPILOT";

        private const string OwnedTargetsKeyPrefix = "CodingRiver.UPilot.ScriptingDefine.OwnedTargets.";

        static UPilotScriptingDefineManager()
        {
            EditorApplication.delayCall += EnsureForActiveBuildTarget;
        }

        internal static void EnsureForActiveBuildTarget()
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown)
                return;

            NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(group);
            string current = PlayerSettings.GetScriptingDefineSymbols(target);
            string updated = AddDefine(current);
            if (string.Equals(current, updated, StringComparison.Ordinal))
                return;

            AddOwnedTarget(group);
            PlayerSettings.SetScriptingDefineSymbols(target, updated);
        }

        internal static void RemoveOwnedDefines()
        {
            var remaining = new List<string>();
            foreach (string targetName in GetOwnedTargetNames())
            {
                if (!Enum.TryParse(targetName, out BuildTargetGroup group) || group == BuildTargetGroup.Unknown)
                {
                    remaining.Add(targetName);
                    continue;
                }

                NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(group);
                string current = PlayerSettings.GetScriptingDefineSymbols(target);
                string updated = RemoveDefine(current);
                if (!string.Equals(current, updated, StringComparison.Ordinal))
                    PlayerSettings.SetScriptingDefineSymbols(target, updated);
            }

            SaveOwnedTargetNames(remaining);
        }

        internal static bool ShouldRemoveForPackageRegistration(bool packageRemoved, bool replacementPresent)
        {
            return packageRemoved && !replacementPresent;
        }

        internal static string AddDefine(string symbols)
        {
            var values = SplitSymbols(symbols);
            if (!values.Contains(Define))
                values.Add(Define);
            return string.Join(";", values);
        }

        internal static string RemoveDefine(string symbols)
        {
            var values = SplitSymbols(symbols);
            values.RemoveAll(value => string.Equals(value, Define, StringComparison.Ordinal));
            return string.Join(";", values);
        }

        private static List<string> SplitSymbols(string symbols)
        {
            var values = new List<string>();
            if (string.IsNullOrEmpty(symbols))
                return values;

            string[] parts = symbols.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i].Trim();
                if (!string.IsNullOrEmpty(value))
                    values.Add(value);
            }
            return values;
        }

        private static void AddOwnedTarget(BuildTargetGroup group)
        {
            var names = GetOwnedTargetNames();
            string name = group.ToString();
            if (!names.Contains(name))
            {
                names.Add(name);
                SaveOwnedTargetNames(names);
            }
        }

        private static List<string> GetOwnedTargetNames()
        {
            return SplitSymbols(EditorPrefs.GetString(OwnedTargetsKey, string.Empty));
        }

        private static void SaveOwnedTargetNames(List<string> names)
        {
            if (names == null || names.Count == 0)
                EditorPrefs.DeleteKey(OwnedTargetsKey);
            else
                EditorPrefs.SetString(OwnedTargetsKey, string.Join(";", names));
        }

        private static string OwnedTargetsKey =>
            OwnedTargetsKeyPrefix + Application.dataPath.Replace('\\', '/').ToLowerInvariant();
    }

    internal sealed class UPilotActiveBuildTargetChanged : IActiveBuildTargetChanged
    {
        public int callbackOrder => 0;

        public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
        {
            EditorApplication.delayCall += UPilotScriptingDefineManager.EnsureForActiveBuildTarget;
        }
    }
}
