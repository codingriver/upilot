// -----------------------------------------------------------------------
// upilot Editor — simplified main-window state and actions.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Text;
using UnityEditor;

namespace CodingRiver.UPilot
{
    internal enum UPilotMainState
    {
        SetupRequired,
        Stopped,
        Starting,
        Restarting,
        Stopping,
        Ready,
        NeedsRepair,
    }

    internal enum UPilotServiceOperation
    {
        None,
        Starting,
        Restarting,
        Stopping,
    }

    internal readonly struct UPilotMainSnapshot
    {
        public UPilotMainSnapshot(
            UPilotMainState state,
            string title,
            string message,
            bool bridgeActive,
            bool mcpActive)
        {
            State = state;
            Title = title;
            Message = message;
            BridgeActive = bridgeActive;
            McpActive = mcpActive;
        }

        public UPilotMainState State { get; }
        public string Title { get; }
        public string Message { get; }
        public bool BridgeActive { get; }
        public bool McpActive { get; }
        public bool AnyServiceActive => BridgeActive || McpActive;
    }

    internal static class UPilotQuickStart
    {
        private const double StartTimeoutSeconds = 15d;
        private const double RestartTimeoutSeconds = 20d;
        private const double StopTimeoutSeconds = 12d;

        private static UPilotServiceOperation _operation;
        private static double _operationStartedAt;

        public static UPilotMainSnapshot Evaluate(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus,
            AgentMcpConfigStatus[] agentConfigs)
        {
            if (!UPilotSetupState.IsCompleted)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.SetupRequired,
                    "完成一次简单设置",
                    "选择你使用的 Agent，UPilot 会自动完成配置并启动。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            var ready = mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated;
            var operationSnapshot = EvaluateOperation(bridgeStatus, mcpStatus, ready);
            if (operationSnapshot.HasValue)
                return operationSnapshot.Value;

            var manager = UPilotMcpServerManager.Instance;
            if (!manager.IsPythonEntryValid(out _))
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务文件未找到",
                    "可以尝试自动修复，无需手动设置路径。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (mcpStatus.IsRunning && !mcpStatus.ProcessId.HasValue)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "端口被其他程序占用",
                    "自动修复会切换到新的空闲端口并更新 Agent 配置。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (mcpStatus.IsRunning && !mcpHealthy)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务未能正常启动",
                    "自动修复会清理当前连接并重新启动服务。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (ready)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.Ready,
                    "已就绪",
                    "现在可以直接让 Agent 操作 Unity。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (!mcpStatus.IsRunning && !bridgeStatus.IsStarted)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.Stopped,
                    "UPilot 当前已停止",
                    "启动后，Agent 才能连接并操作 Unity。",
                    false,
                    false);
            }

            return new UPilotMainSnapshot(
                UPilotMainState.Starting,
                "正在连接 Unity",
                "通常只需要几秒钟，请稍候。",
                bridgeStatus.IsStarted,
                mcpStatus.IsRunning);
        }

        public static string ConfigureAndStart(bool codex, bool claudeCode, bool cursor)
        {
            if (!codex && !claudeCode && !cursor)
                codex = true;

            EnsureAvailablePortsWhenStopped();

            var result = new StringBuilder();
            result.AppendLine(UPilotAgentSetup.WriteAgentRules(overwriteExisting: false));
            if (codex)
                result.AppendLine(UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false));
            if (claudeCode)
                result.AppendLine(UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false));
            if (cursor)
                result.AppendLine(UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false));

            UPilotAgentSetup.MarkAgentRulesHandledForCurrentProject();
            UPilotSetupState.MarkCompleted();
            UPilotBootstrap.IsEnabled = true;
            Start();
            return result.ToString().TrimEnd();
        }

        public static void Start()
        {
            BeginOperation(UPilotServiceOperation.Starting);
            var manager = UPilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            UPilotBridge.Instance.EnsureStarted();
            if (!manager.GetStatus().IsRunning)
                manager.StartServer();
            manager.InvalidateStatusCache();
        }

        public static void Restart()
        {
            BeginOperation(UPilotServiceOperation.Restarting);
            UPilotBridge.Instance.Restart();
            var manager = UPilotMcpServerManager.Instance;
            manager.RestartServer();
            manager.InvalidateStatusCache();
        }

        public static void Stop()
        {
            BeginOperation(UPilotServiceOperation.Stopping);
            UPilotBridge.Instance.Stop();
            var manager = UPilotMcpServerManager.Instance;
            manager.StopServer();
            manager.InvalidateStatusCache();
        }

        public static string AutoRepair(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus,
            AgentMcpConfigStatus[] agentConfigs)
        {
            var manager = UPilotMcpServerManager.Instance;
            if (!manager.IsPythonEntryValid(out _))
            {
                manager.ValidateAndAutoFixPath();
                if (!manager.IsPythonEntryValid(out _))
                {
                    manager.ResetPythonEntryPathToDefaultAbsolute();
                    manager.ValidateAndAutoFixPath();
                }

                if (!manager.IsPythonEntryValid(out _))
                    return "未能自动找到服务文件，请打开高级设置检查 Python 入口。";
            }

            if (mcpStatus.IsRunning && !mcpStatus.ProcessId.HasValue)
            {
                BeginOperation(UPilotServiceOperation.Restarting);
                SwitchToAvailablePortsAndRestart(agentConfigs);
                return "已切换到空闲端口并重新启动。";
            }

            if (mcpStatus.IsRunning &&
                (!mcpStatus.HttpPortListening || !mcpStatus.WsPortListening))
            {
                BeginOperation(UPilotServiceOperation.Restarting);
                manager.RestartServer();
                if (!bridgeStatus.IsStarted)
                    UPilotBridge.Instance.EnsureStarted();
                manager.InvalidateStatusCache();
                return "服务正在重新启动。";
            }

            BeginOperation(
                mcpStatus.IsRunning || bridgeStatus.IsStarted
                    ? UPilotServiceOperation.Restarting
                    : UPilotServiceOperation.Starting);

            if (!mcpStatus.IsRunning)
                manager.StartServer();

            if (!bridgeStatus.IsStarted)
                UPilotBridge.Instance.EnsureStarted();
            else if (!bridgeStatus.IsAuthenticated)
                UPilotBridge.Instance.Restart();

            manager.InvalidateStatusCache();

            return "正在重新连接 Unity。";
        }

        private static UPilotMainSnapshot? EvaluateOperation(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus,
            bool ready)
        {
            if (_operation == UPilotServiceOperation.None)
                return null;

            var elapsed = EditorApplication.timeSinceStartup - _operationStartedAt;
            if (_operation == UPilotServiceOperation.Stopping)
            {
                if (!bridgeStatus.IsStarted && !mcpStatus.IsRunning)
                {
                    ClearOperation();
                    return new UPilotMainSnapshot(
                        UPilotMainState.Stopped,
                        "UPilot 已停止",
                        "Agent 当前无法操作 Unity。",
                        false,
                        false);
                }

                if (elapsed >= StopTimeoutSeconds)
                {
                    ClearOperation();
                    return new UPilotMainSnapshot(
                        UPilotMainState.NeedsRepair,
                        "停止未完成",
                        "仍有服务未能停止，可以重试或打开高级设置处理。",
                        bridgeStatus.IsStarted,
                        mcpStatus.IsRunning);
                }

                return new UPilotMainSnapshot(
                    UPilotMainState.Stopping,
                    "正在停止 UPilot",
                    "请稍候…",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (ready)
            {
                ClearOperation();
                return new UPilotMainSnapshot(
                    UPilotMainState.Ready,
                    "已就绪",
                    "现在可以直接让 Agent 操作 Unity。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            var timeout = _operation == UPilotServiceOperation.Restarting
                ? RestartTimeoutSeconds
                : StartTimeoutSeconds;
            if (elapsed >= timeout)
            {
                var wasRestarting = _operation == UPilotServiceOperation.Restarting;
                ClearOperation();
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    wasRestarting ? "重启未完成" : "启动未完成",
                    "可以自动检查并恢复服务连接。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            var state = _operation == UPilotServiceOperation.Restarting
                ? UPilotMainState.Restarting
                : UPilotMainState.Starting;
            return new UPilotMainSnapshot(
                state,
                state == UPilotMainState.Restarting ? "正在重启 UPilot" : "正在启动 UPilot",
                "通常只需要几秒钟，请稍候。",
                bridgeStatus.IsStarted,
                mcpStatus.IsRunning);
        }

        private static void BeginOperation(UPilotServiceOperation operation)
        {
            _operation = operation;
            _operationStartedAt = EditorApplication.timeSinceStartup;
        }

        private static void ClearOperation()
        {
            _operation = UPilotServiceOperation.None;
            _operationStartedAt = 0d;
        }

        private static void EnsureAvailablePortsWhenStopped()
        {
            var bridge = UPilotBridge.Instance;
            var manager = UPilotMcpServerManager.Instance;
            var status = manager.GetStatus();
            if (bridge.IsStarted || status.IsRunning)
                return;

            if (UPilotPortAllocator.IsPortAvailable(bridge.WsPort) &&
                UPilotPortAllocator.IsPortAvailable(bridge.HttpPort) &&
                bridge.WsPort != bridge.HttpPort)
                return;

            var pair = UPilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
            bridge.SetWsEndpoint(UPilotBridge.DefaultWsHost, pair.wsPort);
            bridge.HttpPort = pair.httpPort;
            manager.InvalidateStatusCache();
        }

        private static void SwitchToAvailablePortsAndRestart(AgentMcpConfigStatus[] agentConfigs)
        {
            var bridge = UPilotBridge.Instance;
            var manager = UPilotMcpServerManager.Instance;
            bridge.Stop();

            var pair = UPilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
            bridge.SetWsEndpoint(UPilotBridge.DefaultWsHost, pair.wsPort);
            bridge.HttpPort = pair.httpPort;
            manager.InvalidateStatusCache();

            RewriteExistingAgentConfigs(agentConfigs);
            bridge.EnsureStarted();
            EditorApplication.delayCall += manager.StartServer;
        }

        private static void RewriteExistingAgentConfigs(AgentMcpConfigStatus[] statuses)
        {
            if (statuses == null) return;
            foreach (var status in statuses)
            {
                if (!status.FileExists && !status.HasUPilotEntry)
                    continue;

                if (status.ClientName == "Codex")
                    UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Claude Code")
                    UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Cursor")
                    UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false);
            }
        }
    }
}
