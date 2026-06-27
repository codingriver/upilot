// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace codingriver.unity.pilot
{
    [InitializeOnLoad]
    public static class UnityPilotBootstrap
    {
        public const string EnabledPrefKey = "codingriver.unity.pilot.BridgeEnabled";

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static UnityPilotBootstrap()
        {
            UnityEngine.Debug.Log("[UnityPilotBootstrap] static constructor");
            EditorApplication.update += TryStartBridge;
            EditorApplication.update += TryStartMcpServer;
            EditorApplication.quitting += () => UnityPilotBridge.Instance.Stop();
        }

        private static void TryStartBridge()
        {
            if (!IsEnabled)
                return;

            UnityEngine.Debug.Log("[UnityPilotBootstrap] TryStartBridge -> EnsureStarted");
            EditorApplication.update -= TryStartBridge;
            UnityPilotBridge.Instance.EnsureStarted();
        }

        private static void TryStartMcpServer()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EditorApplication.update -= TryStartMcpServer;

            var mgr = UnityPilotMcpServerManager.Instance;
            if (!mgr.AutoStartEnabled)
            {
                UnityEngine.Debug.Log("[UnityPilotBootstrap] MCP server auto start disabled.");
                return;
            }

            UnityEngine.Debug.Log("[UnityPilotBootstrap] TryStartMcpServer -> StartServer");
            mgr.ValidateAndAutoFixPath();
            mgr.StartServer();
        }
    }
}
