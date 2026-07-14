// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace codingriver.upilot
{
    [InitializeOnLoad]
    public static class UpilotBootstrap
    {
        public const string EnabledPrefKey = "codingriver.upilot.BridgeEnabled";

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static UpilotBootstrap()
        {
            UnityEngine.Debug.Log("[UpilotBootstrap] static constructor");
            EditorApplication.delayCall += ShowFirstSetupIfNeeded;
            EditorApplication.update += TryStartBridge;
            EditorApplication.update += TryStartMcpServer;
            EditorApplication.quitting += () => UpilotBridge.Instance.Stop();
        }

        private static void ShowFirstSetupIfNeeded()
        {
            if (!IsEnabled || UpilotSetupState.IsCompleted)
                return;

            UnityEngine.Debug.Log("[UpilotBootstrap] First setup is not completed; opening simplified upilot setup.");
            UpilotMainWindow.Open();
        }

        private static void TryStartBridge()
        {
            if (!IsEnabled)
                return;

            if (!UpilotSetupState.IsCompleted)
            {
                EditorApplication.update -= TryStartBridge;
                return;
            }

            UnityEngine.Debug.Log("[UpilotBootstrap] TryStartBridge -> EnsureStarted");
            EditorApplication.update -= TryStartBridge;
            UpilotBridge.Instance.EnsureStarted();
        }

        private static void TryStartMcpServer()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (!UpilotSetupState.IsCompleted)
            {
                EditorApplication.update -= TryStartMcpServer;
                return;
            }

            EditorApplication.update -= TryStartMcpServer;

            var mgr = UpilotMcpServerManager.Instance;
            if (!mgr.AutoStartEnabled)
            {
                UnityEngine.Debug.Log("[UpilotBootstrap] MCP server auto start disabled.");
                return;
            }

            UnityEngine.Debug.Log("[UpilotBootstrap] TryStartMcpServer -> StartServer");
            mgr.ValidateAndAutoFixPath();
            mgr.StartServer();
        }
    }
}
