// -----------------------------------------------------------------------
// upilot Editor — simplified main-window state and actions.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace CodingRiver.UPilot
{
    internal enum UPilotMainState
    {
        SetupRequired,
        Stopped,
        CheckingStatus,
        Starting,
        Restarting,
        Stopping,
        Updating,
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

    internal enum UPilotRepairAction
    {
        None,
        WaitForStatus,
        RestartBridge,
        RestartServer,
        SwitchPorts,
        StartServices,
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

    [InitializeOnLoad]
    internal static class UPilotQuickStart
    {
        private const double StartTimeoutSeconds = 15d;
        private const double RestartTimeoutSeconds = 20d;
        private const double StopTimeoutSeconds = 12d;

        private static UPilotServiceOperation _operation;
        private static double _operationStartedAt;
        private static Task<string> _repairTask;
        private static string _repairPhase = "";
        private static string _repairCause = "";
        private static double _nextDiagnosticAt;
        private static bool _explicitlyStopped;
        private static Action _afterRepairStart;
        private static bool _recoveryProbeRunning;
        private static int _repairGeneration;
        private static string PendingAttemptKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.PendingAttempt");
        private static string PendingCauseKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.PendingCause");
        private static string AutoAttemptKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Attempted");
        private static string FailureKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Failure");
        private static string FailureDialogKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Dialog");
        internal static bool IsRepairing => _repairTask != null && !_repairTask.IsCompleted;
        internal static bool LastRepairSucceeded { get; private set; }
        internal static void SuppressAutomaticRepairForMaintenance() => EditorPrefs.SetBool(AutoAttemptKey, true);
        internal static string DiagnosticDetails => UPilotDeploymentDiagnostics.Details;

        static UPilotQuickStart()
        {
            if (UPilotBridge.IsMainEditorProcess())
            {
                var loadedVersion = UPilotDeploymentDiagnostics.Version;
                EditorApplication.update += ObserveDeployment;
                EditorApplication.delayCall += RecoverRepairObservation;
            }
        }

        private static void RecoverRepairObservation()
        {
            if (IsRepairing) return;
            var pending = SessionState.GetString(PendingAttemptKey, "");
            if (string.IsNullOrEmpty(pending)) return;
            var record = UPilotServerRestartDiagnostics.Current;
            if (record?.status == "running")
            {
                _repairPhase = "重启中：" + record.phase;
                _repairCause = SessionState.GetString(PendingCauseKey, "");
                return;
            }
            if (record?.status != "succeeded" || record.operationId != pending)
                ShowRepairFailureOnce(pending, "重载中断了修复准备或验证；没有重放停启。请检查状态后点击“重新启动”。");
            SessionState.EraseString(PendingAttemptKey);
        }

        private static void ObserveDeployment()
        {
            if (UPilotServiceMaintenance.IsActive) return;
            if (EditorApplication.timeSinceStartup < _nextDiagnosticAt) return;
            _nextDiagnosticAt = EditorApplication.timeSinceStartup + 2;
            if (!UPilotSetupState.IsCompleted || !UPilotBootstrap.IsEnabled || _explicitlyStopped) return;
            try
            {
                var server = UPilotMcpServerManager.Instance.GetStatus();
                var bridge = UPilotBridge.Instance.GetStatus();
                var issues = UPilotDeploymentDiagnostics.Observe(bridge, server);
                var record = UPilotServerRestartDiagnostics.Current;
                if (record?.status == "failed")
                    ShowRepairFailureOnce(record.operationId, record.failurePhase + "\n" + record.error);
                if (IsRepairing || record?.status == "running") return;
                if (issues.Length == 0 && bridge.IsAuthenticated && server.HealthEndpointResponded)
                {
                    if (!_recoveryProbeRunning && (EditorPrefs.GetBool(AutoAttemptKey, false) ||
                        !string.IsNullOrEmpty(EditorPrefs.GetString(FailureKey, ""))))
                        _ = ConfirmObservedRecoveryAsync(server, bridge.SessionId, _repairGeneration);
                    return;
                }
                if (!bridge.IsStarted || !server.StatusQueryCompleted || issues.Length == 0 ||
                    EditorPrefs.GetBool(AutoAttemptKey, false)) return;
                // A fresh explicit mismatch or an exhausted observation window starts one repair.
                if (!issues.Any(issue => issue.Code == "authentication" || issue.Code == "timeout" ||
                    issue.Code == "version" || issue.Code == "channel" || issue.Code == "protocol" ||
                    issue.Code == "entry" || issue.Code == "configured_entry" || issue.Code == "module" || issue.Code == "project" ||
                    issue.Code == "instance")) return;
                _ = AutoRepairAsync(null);
            }
            catch (Exception ex)
            {
                EditorPrefs.SetString(FailureKey, "状态诊断失败：" + ex.Message);
            }
        }

        private static async Task ConfirmObservedRecoveryAsync(McpServerStatus server, string sessionId, int generation)
        {
            _recoveryProbeRunning = true;
            try
            {
                await UPilotMcpServerManager.Instance.VerifyReadOnlyRoundTripAsync(server, sessionId);
                if (generation != _repairGeneration || IsRepairing ||
                    UPilotBridge.Instance.GetStatus().SessionId != sessionId) return;
                EditorPrefs.DeleteKey(FailureKey);
                EditorPrefs.DeleteKey(AutoAttemptKey);
            }
            catch { /* Observation never hides the original failure or opens another dialog. */ }
            finally { _recoveryProbeRunning = false; }
        }

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

            var issues = UPilotDeploymentDiagnostics.Observe(bridgeStatus, mcpStatus);
            if (IsRepairing || UPilotServerRestartDiagnostics.TryGetActive(out _))
                return new UPilotMainSnapshot(UPilotMainState.Restarting, "正在重启 UPilot",
                    _repairPhase + "\n" + _repairCause, bridgeStatus.IsStarted, mcpStatus.IsRunning);
            var failure = EditorPrefs.GetString(FailureKey, "");
            if (issues.Length > 0 || !string.IsNullOrEmpty(failure))
                return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "UPilot 需要修复",
                    issues.Length > 0 ? issues[0].Message : failure, bridgeStatus.IsStarted, mcpStatus.IsRunning);

            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            var ready = mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated;
            var operationSnapshot = EvaluateOperation(bridgeStatus, mcpStatus, ready);
            if (operationSnapshot.HasValue)
                return operationSnapshot.Value;

            var manager = UPilotMcpServerManager.Instance;
            var runtime = UPilotServerRuntimeService.Instance;
            var needsPythonEntry = runtime.GetConfiguredMode() == UPilotServerRuntimeMode.Python;
            if (needsPythonEntry && !manager.IsPythonEntryValid(out _))
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务文件未找到",
                    "可以尝试自动修复，无需手动设置路径。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            return EvaluateServiceState(bridgeStatus, mcpStatus);
        }

        internal static UPilotMainSnapshot EvaluateServiceState(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus)
        {
            if (!string.IsNullOrEmpty(bridgeStatus.AuthenticationError) || !string.IsNullOrEmpty(mcpStatus.ErrorMessage))
                return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "服务状态异常",
                    bridgeStatus.AuthenticationError + "\n" + mcpStatus.ErrorMessage,
                    bridgeStatus.IsStarted, mcpStatus.IsRunning);
            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            var ready = mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated;
            if (ready)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.Ready,
                    "已就绪",
                    "现在可以直接让 Agent 操作 Unity。",
                    bridgeStatus.IsStarted,
                    mcpStatus.IsRunning);
            }

            if (mcpStatus.IsRunning && mcpStatus.DiagnosisPending)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.CheckingStatus,
                    "正在确认服务",
                    "端口已启动，正在确认 MCP 服务身份，请稍候。",
                    bridgeStatus.IsStarted,
                    true);
            }

            if (mcpStatus.IsRunning &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Foreign)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "端口被其他程序占用",
                    "已确认端口属于其他程序。修复不会停止该进程，也不会自动修改端口。",
                    bridgeStatus.IsStarted,
                    true);
            }

            if (mcpStatus.IsRunning &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Unknown)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务身份尚未确认",
                    "端口正在监听，但暂时无法确认所属进程。UPilot 不会自动切换端口。",
                    bridgeStatus.IsStarted,
                    true);
            }

            if (mcpStatus.IsRunning && !mcpHealthy)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务未能正常启动",
                    "自动修复会清理当前连接并重新启动服务。",
                    bridgeStatus.IsStarted,
                    true);
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
                UPilotMainState.CheckingStatus,
                "正在获取状态",
                "正在读取 UPilot 服务状态，请稍候。",
                bridgeStatus.IsStarted,
                mcpStatus.IsRunning);
        }

        public static string ConfigureAndStart(bool codex, bool claudeCode, bool cursor)
        {
            return ConfigureAndStart(codex, claudeCode, cursor, openCode: false);
        }

        public static string ConfigureAndStart(bool codex, bool claudeCode, bool cursor, bool openCode)
        {
            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                return UPilotUpdateService.ServiceStartBlockedMessage;

            if (!codex && !claudeCode && !cursor && !openCode)
                codex = true;

            var enabledAgentClients = new List<string>();
            if (codex) enabledAgentClients.Add("Codex");
            if (claudeCode) enabledAgentClients.Add("Claude Code");
            if (cursor) enabledAgentClients.Add("Cursor");
            if (openCode) enabledAgentClients.Add("OpenCode");
            UPilotAgentSetup.SetEnabledAgentClients(enabledAgentClients);

            if (!EnsureAvailablePortsWhenStopped())
                return "端口配置未完成，未启动服务。";

            var result = new StringBuilder();
            result.AppendLine(UPilotAgentSetup.WriteAgentRules(overwriteExisting: false));
            if (codex)
                result.AppendLine(UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false));
            if (claudeCode)
                result.AppendLine(UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false));
            if (cursor)
                result.AppendLine(UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false));
            if (openCode)
                result.AppendLine(UPilotAgentSetup.WriteOpenCodeMcpConfig(promptBeforeOverwrite: false));

            UPilotAgentSetup.MarkAgentRulesHandledForCurrentProject();
            UPilotSetupState.MarkCompleted();
            UPilotBootstrap.IsEnabled = true;
            Start();
            return result.ToString().TrimEnd();
        }

        public static void Start()
        {
            if (UPilotServiceMaintenance.IsActive) return;
            _explicitlyStopped = false;
            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                Logger.LogWarning("SYSTEM", UPilotUpdateService.ServiceStartBlockedMessage);
                return;
            }

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
            _ = AutoRepairAsync(null);
        }

        public static void Stop()
        {
            if (UPilotServiceMaintenance.IsActive) return;
            if (IsRepairing) return;
            _explicitlyStopped = true;
            BeginOperation(UPilotServiceOperation.Stopping);
            UPilotBridge.Instance.Stop();
            var manager = UPilotMcpServerManager.Instance;
            manager.StopServer();
            manager.InvalidateStatusCache();
        }

        public static Task<string> AutoRepairAsync(AgentMcpConfigStatus[] agentConfigs, Action afterStart = null)
        {
            if (UPilotServiceMaintenance.IsActive)
                return Task.FromResult("AI service maintenance is active: " + UPilotServiceMaintenance.Current.maintenanceId);
            if (afterStart != null) _afterRepairStart += afterStart;
            if (IsRepairing) return _repairTask;
            _repairGeneration++;
            _explicitlyStopped = false;
            EditorPrefs.SetBool(AutoAttemptKey, true);
            _repairTask = RepairCoreAsync();
            return _repairTask;
        }

        private static async Task<string> RepairCoreAsync()
        {
            LastRepairSucceeded = false;
            var attemptId = Guid.NewGuid().ToString("N");
            SessionState.SetString(PendingAttemptKey, attemptId);
            _repairCause = EditorPrefs.GetString(FailureKey, "");
            try
            {
                var manager = UPilotMcpServerManager.Instance;
                _repairPhase = "确认项目、进程与真实来源";
                var status = await manager.GetFreshStatusAsync();
                var issues = UPilotDeploymentDiagnostics.Observe(UPilotBridge.Instance.GetStatus(), status);
                _repairCause = string.Join("\n", issues.Select(issue => issue.Message));
                SessionState.SetString(PendingCauseKey, _repairCause);
                if (!string.IsNullOrEmpty(status.ErrorMessage) && !status.IsRunning)
                    throw new InvalidOperationException(status.ErrorMessage);
                if (status.IsRunning && status.ProcessOwnership != McpProcessOwnership.CurrentUPilot)
                    throw new InvalidOperationException("无法安全确定当前项目 Server，未停止任何进程。\n" + status.ProcessOwnershipEvidence);
                if (!string.IsNullOrWhiteSpace(status.Health?.configured_project_path) &&
                    !UPilotDeploymentDiagnostics.SamePath(status.Health.configured_project_path, UPilotProjectConfig.ProjectRoot))
                    throw new InvalidOperationException("Server 返回其他项目身份，拒绝停止该进程。");
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                    throw new InvalidOperationException(UPilotUpdateService.ServiceStartBlockedMessage);
                _repairPhase = "准备当前包的配套 Server";
                UPilotServerRuntimeService.RepairOwnsFailureDialog = true;
                await UPilotServerRuntimeService.Instance.PrepareMatchingServerForRepairAsync();
                _repairPhase = "停止旧 Bridge / Server，确认进程退出及端口释放";
                var previousId = UPilotServerRestartDiagnostics.Current?.operationId;
                manager.RestartPreparedServer(() => _afterRepairStart?.Invoke());
                var record = UPilotServerRestartDiagnostics.Current;
                if (record == null || record.operationId == previousId)
                    throw new InvalidOperationException("未能建立新的重启操作，请检查进程角色和更新状态。");
                attemptId = record.operationId;
                SessionState.SetString(PendingAttemptKey, attemptId);
                while (UPilotServerRestartDiagnostics.IsActive(attemptId))
                {
                    _repairPhase = "重启中：" + record.phase;
                    await Task.Delay(100);
                    record = UPilotServerRestartDiagnostics.Current;
                    if (record?.operationId != attemptId)
                        throw new InvalidOperationException("重启操作身份已改变，停止观察；不会重放操作。");
                }
                if (record.status != "succeeded")
                    throw new InvalidOperationException(record.failurePhase + "\n" + record.error + "\n" + record.nextAction);
                EditorPrefs.DeleteKey(FailureKey);
                EditorPrefs.DeleteKey(AutoAttemptKey);
                _repairCause = "";
                LastRepairSucceeded = true;
                return "UPilot 已重新启动，配套身份、握手和实际只读调用均已通过。";
            }
            catch (Exception ex)
            {
                var failure = "修复失败，阶段：" + _repairPhase + "\n" + ex.Message;
                EditorPrefs.SetString(FailureKey, failure);
                ShowRepairFailureOnce(attemptId, failure);
                return failure;
            }
            finally
            {
                UPilotServerRuntimeService.RepairOwnsFailureDialog = false;
                _afterRepairStart = null;
                SessionState.EraseString(PendingAttemptKey);
                SessionState.EraseString(PendingCauseKey);
                ClearOperation();
            }
        }

        private static void ShowRepairFailureOnce(string attemptId, string failure)
        {
            if (EditorPrefs.GetString(FailureDialogKey, "") == attemptId) return;
            EditorPrefs.SetString(FailureDialogKey, attemptId);
            EditorPrefs.SetString(FailureKey, failure);
            EditorPrefs.SetBool(AutoAttemptKey, true);
            var record = UPilotServerRestartDiagnostics.Current;
            var diagnosis = record != null && record.operationId == attemptId
                ? UPilotRestartDiagnosticView.Full(record)
                : "失败时间：" + UPilotRestartDiagnosticView.Time(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) +
                  "\n尝试 ID：" + attemptId + "（尚无对应重启 operation ID）" +
                  "\n最后通过的验证门：未知；首个未通过的验证门：未知；status probe：未采集" +
                  "\n重启诊断记录与此次尝试不匹配；请打开高级设置核对 operation ID。";
            UPilotScrollableDialog.ShowDialog("UPilot 自动修复失败", failure + "\n\n" +
                diagnosis +
                "\n被中断的任务不会自动重放。请确认当前身份和维护授权后，再显式点击‘重新启动’。");
        }

        internal static UPilotRepairAction DetermineRepairAction(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus)
        {
            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            var ready = mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated;
            if (ready)
                return UPilotRepairAction.None;
            if (mcpStatus.IsRunning && mcpStatus.DiagnosisPending)
                return UPilotRepairAction.RestartServer;
            if (mcpStatus.IsRunning &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Foreign)
                return UPilotRepairAction.RestartServer;
            if (mcpStatus.IsRunning &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Unknown)
                return UPilotRepairAction.RestartServer;
            if (mcpStatus.IsRunning && !mcpHealthy)
                return UPilotRepairAction.RestartServer;
            if (mcpHealthy && (!bridgeStatus.IsStarted || !bridgeStatus.IsAuthenticated))
                return UPilotRepairAction.RestartServer;
            return UPilotRepairAction.RestartServer;
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

        private static bool EnsureAvailablePortsWhenStopped()
        {
            try
            {
                var bridge = UPilotBridge.Instance;
                var status = UPilotMcpServerManager.Instance.GetStatus();
                if (status.ProcessOwnership == McpProcessOwnership.CurrentUPilot)
                    return UPilotPortRegistration.TrySyncCurrent();
                if (UPilotPortAllocator.IsPortAvailable(bridge.WsPort) &&
                    UPilotPortAllocator.IsPortAvailable(bridge.HttpPort) &&
                    bridge.WsPort != bridge.HttpPort &&
                    string.IsNullOrEmpty(UPilotPortRegistry.ForUser().Check(
                        UPilotProjectConfig.ProjectRoot, bridge.WsPort, bridge.HttpPort)))
                    return UPilotPortRegistration.TrySyncCurrent();

                var pair = UPilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
                if (!EditorUtility.DisplayDialog("修改当前工程端口？",
                        $"当前端口存在占用或预留冲突。将工程配置改为 WS {pair.wsPort} / HTTP {pair.httpPort}。",
                        "修改端口", "取消"))
                    return false;
                bridge.SetProjectEndpoints(UPilotBridge.DefaultWsHost, pair.wsPort, pair.httpPort);
                return true;
            }
            catch (Exception ex) { UPilotPortRegistration.Report("配置启动端口", ex); return false; }
        }

        internal static void RewriteExistingAgentConfigs(AgentMcpConfigStatus[] statuses)
        {
            if (statuses == null) return;
            foreach (var status in statuses)
            {
                if (!status.IsEnabled || !status.HasUPilotEntry)
                    continue;

                if (status.ClientName == "Codex")
                    UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Claude Code")
                    UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "Cursor")
                    UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: false);
                else if (status.ClientName == "OpenCode")
                    UPilotAgentSetup.WriteOpenCodeMcpConfig(promptBeforeOverwrite: false);
            }
        }
    }
}
