// -----------------------------------------------------------------------
// UPilot Editor - registered EditorPrefs keys and reset operations.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEditor;

namespace CodingRiver.UPilot
{
    public static class UPilotPreferences
    {
        private const string McpManagerPrefix = "upilot.McpMgr.";
        private const string SetupCompletedPrefix = "upilot.SetupCompleted.";

        internal static string ProjectSuffix => UPilotBridge.WsEndpointEditorPrefsKeySuffix;

        public static string McpPythonEntryKey => $"{McpManagerPrefix}PythonEntry.{ProjectSuffix}";
        public static string McpLogLevelKey => $"{McpManagerPrefix}LogLevel.{ProjectSuffix}";
        public static string McpAutoStartKey => $"{McpManagerPrefix}AutoStart.{ProjectSuffix}";
        public static string SetupCompletedKey => SetupCompletedPrefix + ProjectSuffix;
        public static string BridgeEnabledKey => ProjectKey("CodingRiver.UPilot.BridgeEnabled");
        public static string DebugWireLogsKey => ProjectKey("upilot.DebugWireLogs");
        public static string VerboseLogsKey => ProjectKey("upilot.VerboseLogs");
        public static string AutoRestartOnStuckKey => ProjectKey("upilot.AutoRestartOnStuck");
        public static string LogToUnityConsoleKey => ProjectKey("CodingRiver.UPilot.Logger.LogToUnityConsole");

        internal static string ProjectKey(string key)
        {
            return $"{key}.{ProjectSuffix}";
        }

        public static IReadOnlyList<string> CurrentProjectKeys
        {
            get
            {
                var keys = new List<string>
                {
                    McpPythonEntryKey,
                    McpLogLevelKey,
                    McpAutoStartKey,
                    SetupCompletedKey,
                    BridgeEnabledKey,
                    DebugWireLogsKey,
                    VerboseLogsKey,
                    AutoRestartOnStuckKey,
                    LogToUnityConsoleKey,

                    // Retained for cleanup of preferences written by older package versions.
                    $"upilot.WsHost.{ProjectSuffix}",
                    $"upilot.WsPort.{ProjectSuffix}",
                    $"upilot.HttpPort.{ProjectSuffix}",
                };
                keys.AddRange(UPilotAgentSetup.GetAgentRulesPreferenceKeysForCurrentProject());
                keys.AddRange(UPilotUpdateService.GetPreferenceKeysForCurrentProject());
                keys.AddRange(UPilotPackageUpdateLifecycle.GetPreferenceKeysForCurrentProject());
                return keys;
            }
        }

        public static int ResetCurrentProject()
        {
            var manager = UPilotMcpServerManager.Instance;
            int deletedCount = DeleteKeys(CurrentProjectKeys);
            manager.ResetPreferencesToDefaultsInMemory();
            return deletedCount;
        }

        internal static int DeleteKeys(IEnumerable<string> keys)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));

            int deletedCount = 0;
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key) || !EditorPrefs.HasKey(key))
                    continue;

                EditorPrefs.DeleteKey(key);
                deletedCount++;
            }
            return deletedCount;
        }
    }
}
