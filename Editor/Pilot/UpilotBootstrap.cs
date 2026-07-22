// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace CodingRiver.UPilot
{
    [InitializeOnLoad]
    public static class UPilotBootstrap
    {
        public const string EnabledPrefKey = "CodingRiver.UPilot.BridgeEnabled";

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static UPilotBootstrap()
        {
            UnityEngine.Debug.Log("[UPilotBootstrap] static constructor");
            UPilotProjectConfig.Reload();
            UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
            EditorApplication.delayCall += ShowFirstSetupIfNeeded;
            EditorApplication.update += TryStartBridge;
            EditorApplication.update += TryStartMcpServer;
            EditorApplication.quitting += () => UPilotBridge.Instance.Stop();
        }

        private static void ShowFirstSetupIfNeeded()
        {
            if (!IsEnabled || UPilotSetupState.IsCompleted)
                return;

            UnityEngine.Debug.Log("[UPilotBootstrap] First setup is not completed; opening UPilot first setup wizard.");
            UPilotFirstSetupWindow.Open();
        }

        private static void TryStartBridge()
        {
            if (!IsEnabled)
                return;

            if (!UPilotSetupState.IsCompleted)
            {
                EditorApplication.update -= TryStartBridge;
                return;
            }

            UnityEngine.Debug.Log("[UPilotBootstrap] TryStartBridge -> EnsureStarted");
            EditorApplication.update -= TryStartBridge;
            UPilotBridge.Instance.EnsureStarted();
        }

        private static void TryStartMcpServer()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (!UPilotSetupState.IsCompleted)
            {
                EditorApplication.update -= TryStartMcpServer;
                return;
            }

            EditorApplication.update -= TryStartMcpServer;

            var mgr = UPilotMcpServerManager.Instance;
            if (!mgr.AutoStartEnabled)
            {
                UnityEngine.Debug.Log("[UPilotBootstrap] MCP server auto start disabled.");
                return;
            }

            UnityEngine.Debug.Log("[UPilotBootstrap] TryStartMcpServer -> StartServer");
            mgr.ValidateAndAutoFixPath();
            mgr.StartServer();
        }
    }
}
