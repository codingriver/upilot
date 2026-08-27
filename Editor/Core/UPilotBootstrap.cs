// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using System;

namespace CodingRiver.UPilot
{
    [InitializeOnLoad]
    public static class UPilotBootstrap
    {
        public const string EnabledPrefKey = "CodingRiver.UPilot.BridgeEnabled";

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(UPilotPreferences.BridgeEnabledKey, true);
            set => EditorPrefs.SetBool(UPilotPreferences.BridgeEnabledKey, value);
        }

        static UPilotBootstrap()
        {
            try
            {
                UnityEngine.Debug.Log("[UPilotBootstrap] static constructor");
                UPilotProjectConfig.Reload();
                UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                EditorApplication.delayCall += ShowFirstSetupIfNeeded;
                EditorApplication.update += TryStartBridge;
                EditorApplication.update += TryStartMcpServer;
                EditorApplication.quitting += StopBridgeOnQuit;
            }
            catch (Exception ex)
            {
                ReportBootstrapError("UPilot 初始化失败", ex);
            }
        }

        private static void ShowFirstSetupIfNeeded()
        {
            try
            {
                if (!IsEnabled || UPilotSetupState.IsCompleted)
                    return;

                UnityEngine.Debug.Log("[UPilotBootstrap] First setup is not completed; opening UPilot first setup wizard.");
                UPilotMainWindow.OpenSetup();
            }
            catch (Exception ex)
            {
                ReportBootstrapError("首次设置向导打开失败", ex);
            }
        }

        private static void TryStartBridge()
        {
            try
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
            catch (Exception ex)
            {
                EditorApplication.update -= TryStartBridge;
                ReportBootstrapError("自动启动 Unity 桥接器失败", ex);
            }
        }

        private static void TryStartMcpServer()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                EditorApplication.update -= TryStartMcpServer;
                if (!UPilotSetupState.IsCompleted)
                    return;

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
            catch (Exception ex)
            {
                EditorApplication.update -= TryStartMcpServer;
                ReportBootstrapError("自动启动 MCP 服务失败", ex);
            }
        }

        private static void StopBridgeOnQuit()
        {
            try
            {
                UPilotBridge.Instance.Stop();
            }
            catch (Exception ex)
            {
                ReportBootstrapError("Unity 退出时停止 UPilot 桥接器失败", ex);
            }
        }

        private static void ReportBootstrapError(string context, Exception ex)
        {
            UnityEngine.Debug.LogError("[UPilotBootstrap] " + context + "：" + ex.Message + "\n" + ex);
        }
    }
}
