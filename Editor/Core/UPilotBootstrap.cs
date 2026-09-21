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
        private const int ServerStartMaxAttempts = 4;
        private static readonly int[] ServerStartRetryDelaysMs = { 2000, 8000, 20000 };
        private static readonly string ServerStartCycleId = Guid.NewGuid().ToString("N");

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(UPilotPreferences.BridgeEnabledKey, true);
            set => EditorPrefs.SetBool(UPilotPreferences.BridgeEnabledKey, value);
        }

        static UPilotBootstrap()
        {
            try
            {
                var processRole = UPilotBridge.DetermineProcessRole();
                if (!UPilotBridge.IsMainEditorProcess(processRole))
                {
                    UnityEngine.Debug.Log(
                        $"[UPilotBootstrap] Startup skipped for auxiliary Unity process role '{processRole}'.");
                    return;
                }

                UPilotStartupDiagnostics.EnterBootstrap();
                UPilotStartupDiagnostics.BeginServerStartCycle(ServerStartCycleId);
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
                if (UnityEngine.Application.isBatchMode)
                    return;
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
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "bridge_disabled",
                        "UPilot Bridge is disabled in Editor preferences.");
                    EditorApplication.update -= TryStartBridge;
                    return;
                }

                if (!UPilotSetupState.IsCompleted)
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "setup_required",
                        "UPilot first setup is not completed.");
                    EditorApplication.update -= TryStartBridge;
                    return;
                }

                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "bridge_update_blocked",
                        UPilotUpdateService.ServiceStartBlockedMessage);
                    if (UPilotStartupDiagnostics.IsServerStartRetryFinished)
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

                var processRole = UPilotBridge.DetermineProcessRole();
                if (!UPilotBridge.IsMainEditorProcess(processRole))
                {
                    var reason = "MCP Server automatic startup is disabled for auxiliary Unity process role '" +
                                 processRole + "'.";
                    UPilotStartupDiagnostics.RecordBlockingReason("server_auxiliary_process_role", reason);
                    UPilotStartupDiagnostics.MarkServerStartRetryBlocked("server_auxiliary_process_role");
                    UnityEngine.Debug.Log("[UPilotBootstrap] " + reason);
                    EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                if (!IsEnabled)
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "bridge_disabled",
                        "UPilot Bridge is disabled in Editor preferences.");
                    UPilotStartupDiagnostics.MarkServerStartRetryBlocked("bridge_disabled");
                    EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                if (!UPilotSetupState.IsCompleted)
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "setup_required",
                        "UPilot first setup is not completed.");
                    UPilotStartupDiagnostics.MarkServerStartRetryBlocked("setup_required");
                    EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                var mgr = UPilotMcpServerManager.Instance;
                if (!mgr.AutoStartEnabled)
                {
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "server_auto_start_disabled",
                        "MCP Server automatic startup is disabled.");
                    UPilotStartupDiagnostics.MarkServerStartRetryBlocked("server_auto_start_disabled");
                    UnityEngine.Debug.Log("[UPilotBootstrap] MCP server auto start disabled.");
                    EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                if (UPilotStartupDiagnostics.IsServerStartRetryFinished)
                {
                    EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!UPilotStartupDiagnostics.TryBeginServerStartAttempt(
                        now,
                        ServerStartMaxAttempts,
                        ServerStartRetryDelaysMs,
                        out var attemptNumber))
                {
                    if (UPilotStartupDiagnostics.IsServerStartRetryFinished)
                        EditorApplication.update -= TryStartMcpServer;
                    return;
                }

                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    UPilotStartupDiagnostics.RecordServerStartAttemptReason("server_update_blocked");
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "server_update_blocked",
                        UPilotUpdateService.ServiceStartBlockedMessage);
                    return;
                }

                if (System.IO.File.Exists(UPilotProjectConfig.ConfigPath) &&
                    !UPilotPortRegistration.TrySyncCurrent())
                {
                    UPilotStartupDiagnostics.RecordServerStartAttemptReason("server_port_registration_failed");
                    UPilotStartupDiagnostics.RecordBlockingReason(
                        "server_port_registration_failed",
                        "Current project port registration could not be synchronized.");
                    return;
                }

                UnityEngine.Debug.Log($"[UPilotBootstrap] TryStartMcpServer attempt {attemptNumber}/{ServerStartMaxAttempts} -> StartServer");
                mgr.ValidateAndAutoFixPath();
                UPilotStartupDiagnostics.BeginServerObservation(mgr, restartProbeWindow: attemptNumber > 1);
                mgr.StartServer();
            }
            catch (Exception ex)
            {
                UPilotStartupDiagnostics.RecordServerStartAttemptReason("server_start_exception");
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
