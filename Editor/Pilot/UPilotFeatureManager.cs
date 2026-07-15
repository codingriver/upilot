// -----------------------------------------------------------------------
// UPilot Editor - optional feature management.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public static class UPilotFeatureManager
    {
        public const string FlowDefine = "UPILOT_ENABLE_FLOW";
        private static readonly Dictionary<string, string> FlowPackages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["com.unity.inputsystem"] = "1.19.0",
            ["com.unity.ui"] = "2.0.0",
            ["com.unity.ui.test-framework"] = "6.3.0",
            ["com.unity.test-framework"] = "1.7.0",
        };
        private static Queue<string> _pendingPackages;
        private static AddRequest _addRequest;

        public static bool IsFlowRequested => UPilotProjectConfig.Current.features?.flow?.enabled == true;

        public static bool IsUnity6OrNewer
        {
            get
            {
                var first = Application.unityVersion.Split('.')[0];
                return int.TryParse(first, out var major) && major >= 6000;
            }
        }

        public static bool IsFlowCompiled
        {
            get
            {
#if UNITY_6000_0_OR_NEWER && UPILOT_ENABLE_FLOW
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsFlowEnabled => IsFlowRequested && IsFlowCompiled;

        public static string FlowUnavailableReason
        {
            get
            {
                if (!IsFlowRequested) return "Disabled by .upilot/config.json";
                if (!IsUnity6OrNewer) return $"Requires Unity 6+, current version is {Application.unityVersion}";
                if (!HasRequiredPackages(out var missing)) return "Missing packages: " + string.Join(", ", missing);
                if (!IsFlowCompiled) return $"Scripting define {FlowDefine} is not active";
                return string.Empty;
            }
        }

        public static bool HasRequiredPackages(out List<string> missing)
        {
            var installed = new HashSet<string>(
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(package => package.name),
                StringComparer.OrdinalIgnoreCase);
            missing = FlowPackages.Keys.Where(package => !installed.Contains(package)).ToList();
            return missing.Count == 0;
        }

        [MenuItem("UPilot/Optional Flow/Enable", false, 400)]
        private static void EnableFlow()
        {
            if (!IsUnity6OrNewer)
            {
                EditorUtility.DisplayDialog("UPilot Flow", "UPilot Flow requires Unity 6 or newer.", "OK");
                return;
            }
            HasRequiredPackages(out var missing);
            if (!EditorUtility.DisplayDialog(
                    "Enable UPilot Flow?",
                    $"This installs {missing.Count} missing optional package(s), enables features.flow, adds {FlowDefine}, and triggers compilation. No YAML or reports will be deleted.",
                    "Enable",
                    "Cancel"))
                return;
            if (missing.Count > 0)
            {
                _pendingPackages = new Queue<string>(missing.Select(package => $"{package}@{FlowPackages[package]}"));
                EditorApplication.update += InstallNextFlowPackage;
                InstallNextFlowPackage();
                return;
            }
            SetFlowEnabledState(true);
        }

        [MenuItem("UPilot/Optional Flow/Disable", false, 401)]
        private static void DisableFlow()
        {
            if (!EditorUtility.DisplayDialog(
                    "Disable UPilot Flow?",
                    $"This removes {FlowDefine} and stops registering Flow commands. Existing YAML and reports are preserved.",
                    "Disable",
                    "Cancel"))
                return;
            SetFlowEnabledState(false);
        }

        private static void InstallNextFlowPackage()
        {
            if (_addRequest != null && !_addRequest.IsCompleted) return;
            if (_addRequest != null && _addRequest.Status == StatusCode.Failure)
            {
                EditorApplication.update -= InstallNextFlowPackage;
                EditorUtility.DisplayDialog("UPilot Flow", $"Package installation failed: {_addRequest.Error?.message}", "OK");
                _addRequest = null;
                _pendingPackages = null;
                return;
            }
            _addRequest = null;
            if (_pendingPackages == null || _pendingPackages.Count == 0)
            {
                EditorApplication.update -= InstallNextFlowPackage;
                _pendingPackages = null;
                SetFlowEnabledState(true);
                return;
            }
            _addRequest = Client.Add(_pendingPackages.Dequeue());
        }

        private static void SetFlowEnabledState(bool enabled)
        {
            var config = UPilotProjectConfig.Current;
            config.features ??= new UPilotFeaturesConfig();
            config.features.flow ??= new UPilotFlowFeatureConfig();
            config.features.flow.enabled = enabled;
            UPilotProjectConfig.Save(config);
            SetFlowDefine(enabled);
        }

        private static void SetFlowDefine(bool enabled)
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.Equals(value, FlowDefine, StringComparison.Ordinal))
                .ToList();
            if (enabled)
                defines.Add(FlowDefine);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defines.Distinct()));
        }
    }
}
