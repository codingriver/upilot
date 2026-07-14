// -----------------------------------------------------------------------
// upilot Editor — simplified main-window state and actions.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Text;
using UnityEditor;

namespace codingriver.upilot
{
    internal enum UpilotMainState
    {
        SetupRequired,
        Stopped,
        Starting,
        Ready,
        NeedsRepair,
    }

    internal readonly struct UpilotMainSnapshot
    {
        public UpilotMainSnapshot(
            UpilotMainState state,
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

        public UpilotMainState State { get; }
        public string Title { get; }
        public string Message { get; }
        public bool BridgeActive { get; }
        public bool McpActive { get; }
        public bool AnyServiceActive => BridgeActive || McpActive;
    }

    internal static class UpilotQuickStart
    {
        public static UpilotMainSnapshot Evaluate(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus,
            AgentMcpConfigStatus[] agentConfigs)
        {
            var configuredAgents = CountConfiguredAgents(agentConfigs);
            if (!UpilotSetupState.IsCompleted || configuredAgents == 0)
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.SetupRequired,
                    "完成一次简单设置",
                    "选择你使用的 Agent，upilot 会自动完成配置并启动。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            var manager = UpilotMcpServerManager.Instance;
            if (!manager.IsPythonEntryValid(out _))
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.NeedsRepair,
                    "服务文件未找到",
                    "可以尝试自动修复，无需手动设置路径。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (mcpStatus.IsRunning && !mcpStatus.ProcessId.HasValue)
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.NeedsRepair,
                    "端口被其他程序占用",
                    "自动修复会切换到新的空闲端口并更新 Agent 配置。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            if (mcpStatus.IsRunning && !mcpHealthy)
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.NeedsRepair,
                    "服务未能正常启动",
                    "自动修复会清理当前连接并重新启动服务。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated)
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.Ready,
                    "已就绪",
                    "现在可以直接让 Agent 操作 Unity。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (!mcpStatus.IsRunning && !bridgeStatus.IsStarted)
            {
                return new UpilotMainSnapshot(
                    UpilotMainState.Stopped,
                    "upilot 当前已停止",
                    "启动后，Agent 才能连接并操作 Unity。",
                    false,
                    false);
            }

            return new UpilotMainSnapshot(
                UpilotMainState.Starting,
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
            result.AppendLine(UpilotAgentSetup.WriteAgentRules(overwriteExisting: false));
            if (codex)
                result.AppendLine(UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false));
            if (claudeCode)
                result.AppendLine(UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false));
            if (cursor)
                result.AppendLine(UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false));

            UpilotAgentSetup.MarkAgentRulesHandledForCurrentProject();
            UpilotSetupState.MarkCompleted();
            UpilotBootstrap.IsEnabled = true;
            Start();
            return result.ToString().TrimEnd();
        }

        public static void Start()
        {
            var manager = UpilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            UpilotBridge.Instance.EnsureStarted();
            if (!manager.GetStatus().IsRunning)
                manager.StartServer();
        }

        public static void Restart()
        {
            UpilotBridge.Instance.Restart();
            UpilotMcpServerManager.Instance.RestartServer();
        }

        public static void Stop()
        {
            UpilotBridge.Instance.Stop();
            UpilotMcpServerManager.Instance.StopServer();
        }

        public static string AutoRepair(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus,
            AgentMcpConfigStatus[] agentConfigs)
        {
            var manager = UpilotMcpServerManager.Instance;
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
                SwitchToAvailablePortsAndRestart(agentConfigs);
                return "已切换到空闲端口并重新启动。";
            }

            if (mcpStatus.IsRunning &&
                (!mcpStatus.HttpPortListening || !mcpStatus.WsPortListening))
            {
                manager.RestartServer();
                if (!bridgeStatus.IsStarted)
                    UpilotBridge.Instance.EnsureStarted();
                return "服务正在重新启动。";
            }

            if (!mcpStatus.IsRunning)
                manager.StartServer();

            if (!bridgeStatus.IsStarted)
                UpilotBridge.Instance.EnsureStarted();
            else if (!bridgeStatus.IsAuthenticated)
                UpilotBridge.Instance.Restart();

            return "正在重新连接 Unity。";
        }

        private static int CountConfiguredAgents(AgentMcpConfigStatus[] statuses)
        {
            var count = 0;
            if (statuses == null) return count;
            foreach (var status in statuses)
            {
                if (status.IsConfigured)
                    count++;
            }
            return count;
        }

        private static void EnsureAvailablePortsWhenStopped()
        {
            var bridge = UpilotBridge.Instance;
            var manager = UpilotMcpServerManager.Instance;
            var status = manager.GetStatus();
            if (bridge.IsStarted || status.IsRunning)
                return;

            if (UpilotPortAllocator.IsPortAvailable(bridge.WsPort) &&
                UpilotPortAllocator.IsPortAvailable(bridge.HttpPort) &&
                bridge.WsPort != bridge.HttpPort)
                return;

            var pair = UpilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
            bridge.SetWsEndpoint(UpilotBridge.DefaultWsHost, pair.wsPort);
            bridge.HttpPort = pair.httpPort;
            manager.InvalidateStatusCache();
        }

        private static void SwitchToAvailablePortsAndRestart(AgentMcpConfigStatus[] agentConfigs)
        {
            var bridge = UpilotBridge.Instance;
            var manager = UpilotMcpServerManager.Instance;
            bridge.Stop();

            var pair = UpilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
            bridge.SetWsEndpoint(UpilotBridge.DefaultWsHost, pair.wsPort);
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
                if (!status.FileExists && !status.HasUpilotEntry)
                    continue;

                if (status.ClientName == "Codex")
                    UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Claude Code")
                    UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Cursor")
                    UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false);
            }
        }
    }
}
