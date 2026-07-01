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
            EditorApplication.update += TryStartBridge;
            EditorApplication.update += TryStartMcpServer;
            EditorApplication.quitting += () => UpilotBridge.Instance.Stop();
        }

        private static void TryStartBridge()
        {
            if (!IsEnabled)
                return;

            UnityEngine.Debug.Log("[UpilotBootstrap] TryStartBridge -> EnsureStarted");
            EditorApplication.update -= TryStartBridge;
            UpilotBridge.Instance.EnsureStarted();
        }

        private static void TryStartMcpServer()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

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
