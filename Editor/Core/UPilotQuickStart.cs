// -----------------------------------------------------------------------
// upilot Editor — simplified main-window state and actions.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        private static long _operationStartedAtUtcMs;
        private static string _operationStopObservationId = "";
        private static Task<string> _repairTask;
        private static string _repairPhase = "";
        private static string _repairCause = "";
        private static int _deploymentNotificationPending;
        private static bool _explicitlyStopped;
        private static Action _afterRepairStart;
        private static bool _recoveryProbeRunning;
        private static long _lastRecoveryProbeAtUtcMs;
        private static int _repairGeneration;
        private static string PendingAttemptKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.PendingAttempt");
        private static string PendingCauseKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.PendingCause");
        private static string AutoAttemptKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Attempted");
        private static string FailureKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Failure");
        private static string HealthRecoveryKey => UPilotPreferences.ProjectKey("UPilot.Health.PendingRecovery");
        private static string VerifiedHealthKey => UPilotPreferences.ProjectKey("UPilot.Health.VerifiedRecovery");
        private static string VerifiedRestartHistoryKey => UPilotPreferences.ProjectKey("UPilot.Health.VerifiedRestartHistory");
        private static string VerifiedExistingRecoveryKey => UPilotPreferences.ProjectKey("UPilot.Health.VerifiedExistingRecovery");
        private static string FailureDialogKey => UPilotPreferences.ProjectKey("UPilot.DirectRepair.Dialog");

        [Serializable]
        private sealed class ExistingRecoveryVerification
        {
            public string recoveryId, serverInstanceId, bridgeSessionId;
            public int processId;
        }
        internal static bool IsRepairing => _repairTask != null && !_repairTask.IsCompleted;
        internal static bool IsExplicitlyStopped => _explicitlyStopped;
        internal static bool LastRepairSucceeded { get; private set; }
        internal static void SuppressAutomaticRepairForMaintenance() => EditorPrefs.SetBool(AutoAttemptKey, true);
        internal static string DiagnosticDetails => UPilotDeploymentDiagnostics.Details;

        static UPilotQuickStart()
        {
            if (UPilotBridge.IsMainEditorProcess())
            {
                var loadedVersion = UPilotDeploymentDiagnostics.Version;
                EditorApplication.delayCall += () =>
                {
                    RecoverRepairObservation();
                    NotifyDeploymentChanged();
                };
            }
        }

        private static void RecoverRepairObservation()
        {
            if (IsRepairing) return;
            var pending = SessionState.GetString(PendingAttemptKey, "");
            if (string.IsNullOrEmpty(pending)) return;
            var record = UPilotServerRestartDiagnostics.Current;
            if (record?.status == "running" && record.operationId == pending)
            {
                _repairPhase = UPilotServerRestartDiagnostics.WaitingMessage(record, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _repairCause = SessionState.GetString(PendingCauseKey, "");
                return;
            }
            if (record?.status != "succeeded" || record.operationId != pending)
                ShowRepairFailureOnce(pending, "重载中断了修复准备或验证；没有重放停启。请检查状态后点击“重新启动”。");
            SessionState.EraseString(PendingAttemptKey);
        }

        // Called only for a new Unity process, never for Domain Reload or reconnect.
        internal static void ClearPriorEditorRecoveryFailures()
        {
            EditorPrefs.DeleteKey(FailureKey);
            EditorPrefs.DeleteKey(AutoAttemptKey);
            SessionState.EraseString(PendingAttemptKey);
            SessionState.EraseString(PendingCauseKey);
            SessionState.EraseString(HealthRecoveryKey);
            SessionState.EraseString(VerifiedExistingRecoveryKey);
            var history = UPilotServerRestartDiagnostics.Current;
            if (!string.IsNullOrEmpty(history?.operationId))
                SessionState.SetString(VerifiedRestartHistoryKey, history.operationId);
        }

        // Producers may run on HTTP/WebSocket workers; all Editor work stays in the control queue.
        internal static void NotifyDeploymentChanged()
        {
            if (Interlocked.Exchange(ref _deploymentNotificationPending, 1) != 0) return;
            try { UPilotBridge.Instance.EnqueueControl(ProcessDeploymentNotification); }
            catch
            {
                Interlocked.Exchange(ref _deploymentNotificationPending, 0);
                throw;
            }
        }

        private static void ProcessDeploymentNotification()
        {
            Interlocked.Exchange(ref _deploymentNotificationPending, 0);
            UPilotMcpServerManager.Instance.ObserveDeploymentChange();
            ObserveDeployment();
        }

        private static void ObserveDeployment()
        {
            if (UPilotServiceMaintenance.IsActive) return;
            if (!UPilotSetupState.IsCompleted || !UPilotBootstrap.IsEnabled || _explicitlyStopped ||
                _operation != UPilotServiceOperation.None) return;
            try
            {
                var manager = UPilotMcpServerManager.Instance;
                var canRepair = manager.CanAutomaticallyRepairDeployment;
                var canObserveFailedRecovery = CanObserveFailedExistingServiceRecovery(manager);
                if (!canRepair && !canObserveFailedRecovery) return;
                var server = manager.GetStatus();
                var bridge = UPilotBridge.Instance.GetStatus();
                if (server.LifecyclePhase != UPilotServerLifecyclePhase.None) return;
                var issues = UPilotDeploymentDiagnostics.Observe(bridge, server);
                TrackLifecycleRecovery(server);
                var disposition = UPilotHealthAssessment.Classify(bridge, server);
                var record = UPilotServerRestartDiagnostics.Current;
                if (disposition == UPilotHealthDisposition.WaitingLifecycle && !issues.Any(issue => issue.Confirmed)) return;
                if (record?.status == "failed" && SessionState.GetString(VerifiedRestartHistoryKey, "") != record.operationId)
                    ShowRepairFailureOnce(record.operationId, record.failurePhase + "\n" + record.error);
                if (IsRepairing || record?.status == "running") return;
                if (!issues.Any(issue => issue.Confirmed) && UPilotHealthAssessment.CanVerifyRecovery(bridge, server))
                {
                    if (!_recoveryProbeRunning && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastRecoveryProbeAtUtcMs >= 10000 &&
                        (IsHealthRecoveryPending || (canObserveFailedRecovery
                            ? !IsExistingServiceRecoveryVerified(manager, bridge, server)
                            : EditorPrefs.GetBool(AutoAttemptKey, false)) ||
                        !string.IsNullOrEmpty(EditorPrefs.GetString(FailureKey, ""))))
                        _ = ConfirmObservedRecoveryAsync(server, bridge.SessionId, _repairGeneration);
                    return;
                }
                if (!canRepair) return;
                if (!bridge.IsStarted || !server.StatusQueryCompleted || issues.Length == 0 ||
                    EditorPrefs.GetBool(AutoAttemptKey, false)) return;
                // Only an explicit confirmed mismatch can start repair, never an observation timeout.
                if (disposition == UPilotHealthDisposition.Unconfirmed) return;
                if (!issues.Any(issue => issue.Confirmed && (issue.Code == "authentication" ||
                    issue.Code == "version" || issue.Code == "channel" || issue.Code == "protocol" ||
                    issue.Code == "entry" || issue.Code == "configured_entry" || issue.Code == "module" || issue.Code == "project" ||
                    issue.Code == "instance"))) return;
                _ = AutoRepairAsync(null);
            }
            catch (Exception ex)
            {
                EditorPrefs.SetString(FailureKey, "状态诊断失败：" + ex.Message);
            }
        }

        internal static string LifecycleRecoveryToken(McpServerStatus server) =>
            string.IsNullOrEmpty(server.EditorObservationLifecycleId) ? "" :
                server.Health?.server_instance_id + ":" + server.EditorObservationLifecycleId;

        private static bool IsHealthRecoveryPending => !string.IsNullOrEmpty(SessionState.GetString(HealthRecoveryKey, ""));
        internal static void TrackLifecycleRecovery(McpServerStatus server)
        {
            if (!UPilotHealthAssessment.HasServiceIdentity(server)) return;
            var token = LifecycleRecoveryToken(server);
            if (!string.IsNullOrEmpty(token) && token != SessionState.GetString(VerifiedHealthKey, ""))
                SessionState.SetString(HealthRecoveryKey, token);
        }

        private static async Task ConfirmObservedRecoveryAsync(McpServerStatus server, string sessionId, int generation)
        {
            _recoveryProbeRunning = true;
            _lastRecoveryProbeAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = SessionState.GetString(HealthRecoveryKey, "");
            var manager = UPilotMcpServerManager.Instance;
            var recoveryId = manager.ExistingServiceRecoveryFailureId;
            var diagnosticScope = "recovery-ui|" + recoveryId + "|" + sessionId;
            var probeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Logger.Lifecycle("recovery_ui_probe_started", $"recoveryId={recoveryId} pid={server.ProcessId} " +
                $"bridgeSessionId={sessionId} repairGeneration={generation}");
            try
            {
                await manager.VerifyReadOnlyRoundTripAsync(server, sessionId);
                var verified = CompleteObservedRecovery(server, sessionId, generation, pending,
                    UPilotBridge.Instance.GetStatus(), manager.GetStatus(), manager, recoveryId);
                Logger.CloseDiagnostics(diagnosticScope);
                Logger.Lifecycle("recovery_ui_probe_completed", $"recoveryId={recoveryId} pid={server.ProcessId} " +
                    $"bridgeSessionId={sessionId} result={(verified ? "verified" : "identity_changed_or_blocked")} " +
                    $"elapsedMs={Logger.LifecycleElapsedMs(probeStartedAt)}");
            }
            catch (Exception ex)
            {
                // HTTP exceptions can contain probe credentials; the Error boundary redacts
                // the original exception while retaining its stack and aggregating repeats.
                Logger.LifecycleFailure("recovery_ui_probe_completed", $"recoveryId={recoveryId} pid={server.ProcessId} " +
                    $"bridgeSessionId={sessionId} result=failed errorType={ex.GetType().FullName} " +
                    $"elapsedMs={Logger.LifecycleElapsedMs(probeStartedAt)}", ex,
                    scope: diagnosticScope);
            }
            finally { _recoveryProbeRunning = false; }
        }

        // Called only after the actual read-only probe succeeded. Recheck current facts
        // before clearing transient UI errors; never rewrite a failed restart record.
        internal static bool CompleteObservedRecovery(McpServerStatus expected, string sessionId, int generation,
            string pending, BridgeStatus bridge, McpServerStatus latest,
            UPilotMcpServerManager manager = null, string existingRecoveryId = "")
        {
            manager = manager ?? UPilotMcpServerManager.Instance;
            if (generation != _repairGeneration || IsRepairing || _explicitlyStopped ||
                _operation != UPilotServiceOperation.None || UPilotServiceMaintenance.IsActive ||
                manager.HasActiveStopObservation || manager.ExistingServiceRecoveryFailureId != existingRecoveryId ||
                latest.LifecyclePhase != UPilotServerLifecyclePhase.None || bridge.SessionId != sessionId ||
                pending != SessionState.GetString(HealthRecoveryKey, "") || latest.ProcessId != expected.ProcessId ||
                latest.Health?.server_instance_id != expected.Health?.server_instance_id ||
                !UPilotHealthAssessment.CanVerifyRecovery(bridge, latest) ||
                UPilotDeploymentDiagnostics.Observe(bridge, latest).Any(issue => issue.Confirmed)) return false;
            if (!string.IsNullOrEmpty(existingRecoveryId))
            {
                var verified = ExistingServiceRecoveryToken(existingRecoveryId, bridge, latest);
                if (string.IsNullOrEmpty(verified)) return false;
                if (SessionState.GetString(VerifiedExistingRecoveryKey, "") != verified)
                {
                    SessionState.SetString(VerifiedExistingRecoveryKey, verified);
                    Logger.Lifecycle("recovery_ui_verified", $"recoveryId={existingRecoveryId} pid={latest.ProcessId} " +
                        $"serverInstanceId={latest.Health.server_instance_id} bridgeSessionId={sessionId}");
                }
            }
            if (!string.IsNullOrEmpty(pending)) SessionState.SetString(VerifiedHealthKey, pending);
            SessionState.EraseString(HealthRecoveryKey);
            UPilotDeploymentDiagnostics.ClearTransientHealthIssues();
            var history = UPilotServerRestartDiagnostics.Current;
            if (history?.status == "failed") SessionState.SetString(VerifiedRestartHistoryKey, history.operationId);
            EditorPrefs.DeleteKey(FailureKey);
            // A verified observation changes the UI, not the failed recovery's replay policy.
            if (string.IsNullOrEmpty(existingRecoveryId)) EditorPrefs.DeleteKey(AutoAttemptKey);
            return true;
        }

        internal static bool CanObserveFailedExistingServiceRecovery(UPilotMcpServerManager manager) =>
            manager.InitialServiceRecoveryCompleted && !manager.HasActiveStopObservation &&
            !string.IsNullOrEmpty(manager.ExistingServiceRecoveryFailureId);

        private static string ExistingServiceRecoveryToken(string recoveryId, BridgeStatus bridge, McpServerStatus server)
        {
            if (string.IsNullOrEmpty(recoveryId) || !UPilotHealthAssessment.HasServiceIdentity(server) ||
                string.IsNullOrEmpty(bridge.SessionId) || bridge.SessionId != server.Health.bridge_session_id) return "";
            return UnityEngine.JsonUtility.ToJson(new ExistingRecoveryVerification {
                recoveryId = recoveryId, processId = server.ProcessId.GetValueOrDefault(),
                serverInstanceId = server.Health.server_instance_id, bridgeSessionId = bridge.SessionId,
            });
        }

        private static bool IsExistingServiceRecoveryVerified(UPilotMcpServerManager manager,
            BridgeStatus bridge, McpServerStatus server)
        {
            var token = ExistingServiceRecoveryToken(manager.ExistingServiceRecoveryFailureId, bridge, server);
            return !string.IsNullOrEmpty(token) && SessionState.GetString(VerifiedExistingRecoveryKey, "") == token;
        }

        internal static UPilotMainSnapshot? EvaluateExistingServiceRecoveryFailure(UPilotMcpServerManager manager,
            BridgeStatus bridge, McpServerStatus server)
        {
            var failure = manager.ExistingServiceRecoveryFailure;
            if (string.IsNullOrEmpty(failure) || IsExistingServiceRecoveryVerified(manager, bridge, server)) return null;
            return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "遗留服务恢复失败",
                failure + "\n本次恢复已结束，可检查原因后手动启动或重新启动。", bridge.IsStarted, server.IsRunning);
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

            var lifecycle = EvaluateLifecycle(mcpStatus, bridgeStatus.IsStarted);
            if (lifecycle.HasValue) return lifecycle.Value;
            var issues = UPilotDeploymentDiagnostics.Observe(bridgeStatus, mcpStatus);
            var activeRestart = UPilotServerRestartDiagnostics.Current;
            if (IsRepairing || activeRestart?.status == "running")
                return new UPilotMainSnapshot(UPilotMainState.Restarting, "正在重启 UPilot",
                    activeRestart?.status == "running"
                        ? UPilotServerRestartDiagnostics.WaitingMessage(activeRestart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        : _repairPhase + "\n" + _repairCause, bridgeStatus.IsStarted, mcpStatus.IsRunning);
            var stopFailure = EvaluateStopFailure(UPilotMcpServerManager.Instance.StopObservation,
                bridgeStatus.IsStarted, mcpStatus.IsRunning);
            if (stopFailure.HasValue) return stopFailure.Value;
            TrackLifecycleRecovery(mcpStatus);
            var disposition = UPilotHealthAssessment.Classify(bridgeStatus, mcpStatus);
            var attachmentFailure = EvaluateAttachmentFailure(UPilotMcpServerManager.Instance.AttachmentFailure,
                mcpStatus, bridgeStatus.IsStarted);
            if (attachmentFailure.HasValue) return attachmentFailure.Value;
            var recoveryFailure = EvaluateExistingServiceRecoveryFailure(UPilotMcpServerManager.Instance, bridgeStatus, mcpStatus);
            if (recoveryFailure.HasValue) return recoveryFailure.Value;
            var failure = EditorPrefs.GetString(FailureKey, "");
            if (issues.Any(issue => issue.Confirmed) || disposition == UPilotHealthDisposition.ConfirmedFailure ||
                (!string.IsNullOrEmpty(failure) && disposition == UPilotHealthDisposition.Normal && !IsHealthRecoveryPending))
                return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "UPilot 需要修复",
                    BuildRepairMessage(bridgeStatus, mcpStatus,
                        issues.FirstOrDefault(issue => issue.Confirmed)?.Message ?? (mcpStatus.ErrorMessage ?? failure)),
                    bridgeStatus.IsStarted, mcpStatus.IsRunning);

            if (disposition == UPilotHealthDisposition.WaitingLifecycle || disposition == UPilotHealthDisposition.Unconfirmed)
                return EvaluateServiceState(bridgeStatus, mcpStatus);
            if (IsHealthRecoveryPending || issues.Length > 0)
                return new UPilotMainSnapshot(UPilotMainState.CheckingStatus, "正在验证恢复",
                    "正在确认当前身份、Bridge 握手、新鲜终态快照和实际只读往返；尚未恢复可用。", bridgeStatus.IsStarted, mcpStatus.IsRunning);

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

        internal static UPilotMainSnapshot? EvaluateAttachmentFailure(UPilotMcpServerManager.ServerAttachmentFailure failure,
            McpServerStatus server, bool bridgeStarted)
        {
            if (failure == null || server.LifecyclePhase != UPilotServerLifecyclePhase.None) return null;
            return new UPilotMainSnapshot(UPilotMainState.NeedsRepair,
                failure.TimedOut ? "附着验证超时" : "附着验证失败",
                "本次启动的显式附着验证已结束；服务健康仍待确认。\n" + failure.Reason +
                "\noperationId=" + failure.OperationId, bridgeStarted, server.IsRunning);
        }

        internal static UPilotMainSnapshot? EvaluateStopFailure(ServiceStopObservationRecord stop,
            bool bridgeStarted, bool serverRunning)
        {
            if (stop == null || stop.Active || (stop.status != "failed" && stop.status != "timed_out") ||
                !stop.MatchesScope(UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id,
                    System.Diagnostics.Process.GetCurrentProcess().Id)) return null;
            return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "停止未确认", stop.TerminalMessage,
                bridgeStarted, serverRunning);
        }

        internal static UPilotMainSnapshot? EvaluateLifecycle(McpServerStatus status, bool bridgeStarted)
        {
            if (status.LifecyclePhase == UPilotServerLifecyclePhase.None) return null;
            var state = status.LifecyclePhase == UPilotServerLifecyclePhase.Starting ? UPilotMainState.Starting :
                status.LifecyclePhase == UPilotServerLifecyclePhase.Stopping ? UPilotMainState.Stopping : UPilotMainState.CheckingStatus;
            var restart = UPilotServerRestartDiagnostics.Current;
            var message = status.LifecyclePhase == UPilotServerLifecyclePhase.Starting ? "等待启动进程和 HTTP、WS 双端口；尚未进行健康检查。" :
                status.LifecyclePhase == UPilotServerLifecyclePhase.Stopping ? "正在确认进程退出和端口释放。" :
                restart?.status == "running" ? UPilotServerRestartDiagnostics.WaitingMessage(restart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) :
                "正在核实服务身份、Bridge 连接和实际只读往返。";
            if (status.LifecyclePhase == UPilotServerLifecyclePhase.Verifying && !string.IsNullOrEmpty(status.ErrorMessage))
                message += " 最近失败：" + status.StatusFailureStage + "；原因：" + status.ErrorMessage;
            return new UPilotMainSnapshot(state, UPilotMcpServerManager.LifecycleTitle(status.LifecyclePhase), message, bridgeStarted, status.IsRunning);
        }

        // 部署一致性与通信状态分别展示；此摘要不改变修复状态、恢复验证或命令准入。
        internal static string BuildRepairMessage(BridgeStatus bridge, McpServerStatus server, string detail, long nowUtcMs = 0)
        {
            var now = nowUtcMs > 0 ? nowUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var identityMatches = UPilotHealthAssessment.HasServiceIdentity(server);
            var cacheAllowanceMs = UPilotMcpServerManager.Instance.HealthCheckIntervalMs;
            var healthFresh = server.LastSuccessfulStatusAtUtcMs > 0 &&
                              Math.Max(0, now - server.LastSuccessfulStatusAtUtcMs) <= cacheAllowanceMs + 6000;
            var serverHealthy = server.IsRunning && server.HttpPortListening && server.WsPortListening &&
                                server.StatusQueryCompleted && server.HealthEndpointResponded &&
                                !server.DiagnosisPending && identityMatches && healthFresh &&
                                server.FailureKind == McpStatusFailureKind.None && string.IsNullOrEmpty(server.ErrorMessage);
            var serverText = "待确认";
            if (server.ProcessOwnership == McpProcessOwnership.Foreign)
                serverText = "身份不符";
            else if (server.FailureKind == McpStatusFailureKind.Confirmed ||
                     (!string.IsNullOrEmpty(server.ErrorMessage) && server.FailureKind != McpStatusFailureKind.Transient))
                serverText = "状态异常";
            else if (server.FailureKind == McpStatusFailureKind.Transient || server.DiagnosisPending || !server.StatusQueryCompleted)
                serverText = "待确认";
            else if (server.StatusQueryCompleted && !server.IsRunning)
                serverText = "未运行";
            else if (server.IsRunning && (!server.HttpPortListening || !server.WsPortListening))
                serverText = "监听异常";
            else if (serverHealthy)
                serverText = "正常";

            var bridgeConnected = bridge.IsStarted && bridge.IsWsOpen && bridge.IsAuthenticated &&
                                  string.IsNullOrEmpty(bridge.AuthenticationError) &&
                                  string.IsNullOrEmpty(bridge.AuthenticationErrorCode);
            var sameSession = !string.IsNullOrEmpty(bridge.SessionId) && bridge.SessionId == server.Health?.bridge_session_id;
            var observationMatches = server.EditorObservationAvailable && server.EditorObservationObservedAtUtcMs > 0 &&
                                     sameSession && server.EditorObservationSessionId == bridge.SessionId;
            var observationAge = Math.Max(0, now - server.EditorObservationObservedAtUtcMs);
            var heartbeatTimeoutMs = UPilotBridge.Instance.HeartbeatTimeoutMs;
            var heartbeatFresh = observationMatches && server.EditorObservationHeartbeatAgeMs >= 0 &&
                                 server.EditorObservationHeartbeatAgeMs <= heartbeatTimeoutMs &&
                                 server.EditorObservationHeartbeatAgeMs + observationAge <= cacheAllowanceMs + heartbeatTimeoutMs;
            var bridgeText = !string.IsNullOrEmpty(bridge.AuthenticationError) || !string.IsNullOrEmpty(bridge.AuthenticationErrorCode)
                ? "认证失败" : !bridge.IsStarted ? "未启动" : !bridge.IsWsOpen ? "未连接" : !bridge.IsAuthenticated
                    ? "等待认证" : !serverHealthy || !sameSession ? "已连接（服务身份待确认）"
                        : heartbeatFresh ? "已连接（心跳新鲜）" : "已连接（心跳待确认）";

            var unityText = "待确认";
            if (serverHealthy && bridgeConnected && observationMatches && observationAge <= cacheAllowanceMs + 6000)
            {
                if (server.EditorObservationReason == "unity_process_exited")
                    unityText = "进程已退出";
                else if (server.EditorObservationReason == "unity_process_identity_mismatch")
                    unityText = "身份不符";
                else if (UPilotHealthAssessment.Classify(bridge, server) == UPilotHealthDisposition.WaitingLifecycle)
                    unityText = UPilotHealthAssessment.LifecycleTitle(server.EditorObservationLifecyclePhase).Substring("Unity ".Length);
                else if (server.EditorObservationStatus == "waiting_editor")
                    unityText = "UPilot 观测待更新";
                else if (server.EditorObservationStatus == "responsive" && heartbeatFresh &&
                         UPilotHealthAssessment.CanVerifyRecovery(bridge, server, now))
                    unityText = "响应正常";
            }
            return $"Server：{serverText}｜Bridge：{bridgeText}｜Unity：{unityText}\n\n{detail}";
        }

        internal static UPilotMainSnapshot EvaluateServiceState(
            BridgeStatus bridgeStatus,
            McpServerStatus mcpStatus)
        {
            var disposition = UPilotHealthAssessment.Classify(bridgeStatus, mcpStatus);
            if (mcpStatus.ProcessOwnership == McpProcessOwnership.Foreign)
                return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "端口被其他程序占用",
                    "已确认端口属于其他项目或程序。修复不会停止该进程，也不会自动修改端口。", bridgeStatus.IsStarted, mcpStatus.IsRunning);
            if (disposition == UPilotHealthDisposition.ConfirmedFailure)
                return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "服务状态异常",
                    bridgeStatus.AuthenticationError + "\n" + mcpStatus.ErrorMessage,
                    bridgeStatus.IsStarted, mcpStatus.IsRunning);

            if (disposition == UPilotHealthDisposition.WaitingLifecycle)
                return new UPilotMainSnapshot(UPilotMainState.CheckingStatus,
                    UPilotHealthAssessment.LifecycleTitle(mcpStatus.EditorObservationLifecyclePhase),
                    $"已等待 {mcpStatus.EditorObservationWaitingDurationMs / 1000} 秒；Server HTTP、Bridge 心跳与 Editor 响应分别观测；未执行自动重启。",
                    bridgeStatus.IsStarted, mcpStatus.IsRunning);
            if (disposition == UPilotHealthDisposition.Unconfirmed)
            {
                var confirmingIdentity = mcpStatus.IsRunning &&
                    (mcpStatus.DiagnosisPending || mcpStatus.ProcessOwnership == McpProcessOwnership.Unknown);
                return new UPilotMainSnapshot(UPilotMainState.CheckingStatus,
                    confirmingIdentity ? "正在确认服务" : "Unity 响应状态暂无法确认",
                    confirmingIdentity
                        ? "端口已启动，正在确认 MCP 服务身份；未执行自动重启。"
                        : "正在等待新的身份、主线程与网络证据；未执行自动重启。",
                    bridgeStatus.IsStarted, mcpStatus.IsRunning);
            }

            var mcpHealthy = mcpStatus.IsRunning &&
                             mcpStatus.HttpPortListening &&
                             mcpStatus.WsPortListening;
            var ready = mcpHealthy && bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated;

            if (ready && !mcpStatus.DiagnosisPending &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Foreign)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "端口被其他程序占用",
                    "已确认端口属于其他程序。修复不会停止该进程，也不会自动修改端口。",
                    bridgeStatus.IsStarted,
                    true);
            }

            if (ready && !mcpStatus.DiagnosisPending &&
                mcpStatus.ProcessOwnership == McpProcessOwnership.Unknown)
            {
                return new UPilotMainSnapshot(
                    UPilotMainState.NeedsRepair,
                    "服务身份尚未确认",
                    "端口正在监听，但暂时无法确认所属进程。UPilot 不会自动切换端口。",
                    bridgeStatus.IsStarted,
                    true);
            }

            if (ready && mcpStatus.EditorObservationAvailable)
            {
                if (mcpStatus.EditorObservationStatus == "waiting_editor")
                {
                    return new UPilotMainSnapshot(
                        UPilotMainState.CheckingStatus,
                        "UPilot 主线程观测待更新",
                        "正在每秒检查；观测过期不能据此认定 Unity 或游戏卡死，未执行自动重启。",
                        bridgeStatus.IsStarted,
                        mcpStatus.IsRunning);
                }

                if (mcpStatus.EditorObservationStatus == "unknown")
                {
                    return new UPilotMainSnapshot(
                        UPilotMainState.CheckingStatus,
                        "Unity 响应状态暂无法确认",
                        "正在等待新的主线程与网络证据；未执行自动重启。",
                        bridgeStatus.IsStarted,
                        mcpStatus.IsRunning);
                }
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
            NotifyDeploymentChanged();
            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                Logger.LogWarning("SYSTEM", UPilotUpdateService.ServiceStartBlockedMessage);
                return;
            }

            BeginOperation(UPilotServiceOperation.Starting);
            var manager = UPilotMcpServerManager.Instance;
            manager.EnsureInitialServiceRecovery();
            manager.ValidateAndAutoFixPath();
            UPilotBridge.Instance.EnsureStarted();
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
            _explicitlyStopped = true;
            NotifyDeploymentChanged();
            UPilotPackageUpdateLifecycle.CancelDeferredRepairForUserStop();
            BeginOperation(UPilotServiceOperation.Stopping);
            UPilotBridge.Instance.Stop();
            var manager = UPilotMcpServerManager.Instance;
            manager.StopServer();
            _operationStopObservationId = manager.StopObservation?.id ?? "";
            manager.InvalidateStatusCache();
        }

        public static Task<string> AutoRepairAsync(AgentMcpConfigStatus[] agentConfigs, Action afterStart = null)
        {
            if (UPilotServiceMaintenance.IsActive)
                return Task.FromResult("AI service maintenance is active: " + UPilotServiceMaintenance.Current.maintenanceId);
            if (afterStart != null) _afterRepairStart += afterStart;
            if (IsRepairing) return _repairTask;
            return BeginRepair(null);
        }

        internal static bool CanSwitchRuntime => !IsRepairing && CanApplyRuntimeSelection;

        private static bool CanApplyRuntimeSelection => !UPilotServiceMaintenance.IsActive &&
            !UPilotMcpServerManager.Instance.IsServiceTransitionActive &&
            !UPilotUpdateService.Instance.IsServiceStartBlocked && !UPilotUpdateService.Instance.IsUpdateRunning &&
            !UPilotServerRuntimeService.Instance.DownloadState.IsRunning &&
            !UPilotServerRuntimeService.Instance.PythonEnvironmentState.IsRunning &&
            !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        internal static Task<string> ApplyRuntimeAndRestartAsync(UPilotServerRuntimeMode mode, string path)
        {
            if (!CanSwitchRuntime)
                throw new InvalidOperationException("UPilot 正在处理编译、安装、更新或服务操作，请完成后再切换。");
            return BeginRepair(async () =>
            {
                if (mode == UPilotServerRuntimeMode.Python)
                    UPilotMcpServerManager.Instance.PreparePythonEntryForRepair();
                await UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(mode, path, SaveSelectedRuntime);
            });
        }

        internal static void SaveSelectedRuntime(UPilotProjectConfigData config)
        {
            // 验证可能跨越多帧；保存前复查用户停止及其间开始的更新/维护。
            if (_explicitlyStopped) throw new OperationCanceledException("已停止切换，运行方式未保存。");
            if (!CanApplyRuntimeSelection)
                throw new InvalidOperationException("运行环境状态已变化，未保存所选运行方式；请等待当前操作完成后重试。");
            UPilotProjectConfig.Save(config);
        }

        private static Task<string> BeginRepair(Func<Task> prepareRuntime)
        {
            _repairGeneration++;
            _explicitlyStopped = false;
            NotifyDeploymentChanged();
            EditorPrefs.SetBool(AutoAttemptKey, true);
            _repairTask = RepairCoreAsync(prepareRuntime);
            return _repairTask;
        }

        private static async Task<string> RepairCoreAsync(Func<Task> prepareRuntime)
        {
            LastRepairSucceeded = false;
            var runtimeSaved = false;
            var attemptId = Guid.NewGuid().ToString("N");
            McpServerStatus? preflightStatus = null;
            var preflightGate = "本地状态采集";
            var restartSubmitted = false;
            var restartDispatchAttempted = false;
            SessionState.SetString(PendingAttemptKey, attemptId);
            _repairCause = EditorPrefs.GetString(FailureKey, "");
            try
            {
                var manager = UPilotMcpServerManager.Instance;
                manager.EnsureInitialServiceRecovery();
                _repairPhase = "确认项目、进程与真实来源";
                var status = await manager.GetFreshStatusAsync();
                preflightStatus = status;
                if (_explicitlyStopped) return "已按请求停止 UPilot 服务。";
                var issues = UPilotDeploymentDiagnostics.Observe(UPilotBridge.Instance.GetStatus(), status);
                _repairCause = string.Join("\n", issues.Select(issue => issue.Message));
                SessionState.SetString(PendingCauseKey, _repairCause);
                if (!string.IsNullOrEmpty(status.ErrorMessage) && !status.IsRunning)
                    throw new InvalidOperationException(status.ErrorMessage);
                preflightGate = "当前项目进程归属核验";
                if (status.IsRunning && status.ProcessOwnership != McpProcessOwnership.CurrentUPilot)
                    throw new InvalidOperationException("无法安全确定当前项目 Server，未停止任何进程。\n" + status.ProcessOwnershipEvidence);
                preflightGate = "Server 返回项目身份核验";
                if (!string.IsNullOrWhiteSpace(status.Health?.configured_project_path) &&
                    !UPilotDeploymentDiagnostics.SamePath(status.Health.configured_project_path, UPilotProjectConfig.ProjectRoot))
                    throw new InvalidOperationException("Server 返回其他项目身份，拒绝停止该进程。");
                preflightGate = "服务更新状态核验";
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                    throw new InvalidOperationException(UPilotUpdateService.ServiceStartBlockedMessage);
                _repairPhase = prepareRuntime == null ? "准备当前包的配套 Server" : "验证并保存所选运行方式";
                preflightGate = _repairPhase;
                UPilotServerRuntimeService.RepairOwnsFailureDialog = true;
                await PrepareRepairRuntimeAsync(prepareRuntime,
                    () => UPilotServerRuntimeService.Instance.PrepareMatchingServerForRepairAsync());
                runtimeSaved = prepareRuntime != null;
                if (_explicitlyStopped) return "已按请求停止 UPilot 服务。";
                _repairPhase = "停止旧 Bridge / Server，确认进程退出及端口释放";
                preflightGate = "建立重启操作";
                var previousId = UPilotServerRestartDiagnostics.Current?.operationId;
                restartDispatchAttempted = true;
                manager.RestartPreparedServer(() => _afterRepairStart?.Invoke());
                var record = UPilotServerRestartDiagnostics.Current;
                if (record == null || record.operationId == previousId)
                    throw new InvalidOperationException("未能建立新的重启操作，请检查进程角色和更新状态。");
                restartSubmitted = true;
                attemptId = record.operationId;
                SessionState.SetString(PendingAttemptKey, attemptId);
                while (UPilotServerRestartDiagnostics.IsActive(attemptId))
                {
                    _repairPhase = UPilotServerRestartDiagnostics.WaitingMessage(record, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
                if (_explicitlyStopped || IsRepairSuperseded(restartSubmitted, attemptId, UPilotServerRestartDiagnostics.Current))
                    return _explicitlyStopped ? "已按请求停止 UPilot 服务。" : "包生命周期已替代原重启；等待部署稳定后按需恢复。";
                var failure = (runtimeSaved ? "运行配置已保存，重启未完成。\n" : "") +
                    "修复失败，阶段：" + _repairPhase + "\n" + ex.Message;
                EditorPrefs.SetString(FailureKey, failure);
                ShowRepairFailureOnce(attemptId, failure, preflightDiagnosis: restartDispatchAttempted ? null :
                    RepairPreflightDiagnosis(attemptId, _repairPhase, preflightGate, preflightStatus));
                return failure;
            }
            finally
            {
                UPilotServerRuntimeService.RepairOwnsFailureDialog = false;
                _afterRepairStart = null;
                SessionState.EraseString(PendingAttemptKey);
                SessionState.EraseString(PendingCauseKey);
                ClearOperation();
                NotifyDeploymentChanged();
            }
        }

        internal static Task PrepareRepairRuntimeAsync(Func<Task> explicitSelection, Func<Task> matchingRuntime) =>
            (explicitSelection ?? matchingRuntime)();

        internal static bool IsRepairSuperseded(bool restartSubmitted, string attemptId, UPilotServerRestartRecord record) =>
            restartSubmitted && record?.operationId == attemptId && record.status == "superseded";

        internal static string RepairPreflightDiagnosis(string attemptId, string phase, string gate,
            McpServerStatus? observed)
        {
            var diagnosis = "失败时间：" + UPilotRestartDiagnosticView.Time(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) +
                "\n尝试 ID：" + attemptId + "（尚无对应重启 operation ID）" +
                "\n本次在提交重启前失败，尚未产生重启记录。" +
                "\n当前阶段：" + phase + "；首个未通过的验证门：" + gate +
                "\n实际只读 status probe：未执行；本次未进入重启后验证。";
            if (!observed.HasValue)
                return diagnosis + "\n本地状态采集：未返回；最后通过的验证门：未知。";
            var status = observed.Value;
            return diagnosis + "\n本地状态采集：" + (status.StatusQueryCompleted ? "已完成" : "已返回但未完成") +
                "；statusGeneration=" + (status.StatusGeneration > 0 ? status.StatusGeneration.ToString() : "未知") +
                "\nServer PID：" + (status.ProcessId > 0 ? status.ProcessId.Value.ToString() : "未知") +
                "；进程归属：" + status.ProcessOwnership +
                "\nHTTP 监听：" + (status.StatusQueryCompleted ? status.HttpPortListening.ToString() : "未知") +
                "；WS 监听：" + (status.StatusQueryCompleted ? status.WsPortListening.ToString() : "未知") +
                "；Health 响应：" + (status.HealthEndpointResponded ? "已收到" : "未证实") +
                "\n最后完成的观测：本地状态返回；其他安全验证门的通过状态不作推断。";
        }

        internal static void ShowRepairFailureOnce(string attemptId, string failure,
            Action<string> openSetup = null, Action<string, string> showDialog = null,
            UPilotServerRestartRecord diagnosticRecord = null, string preflightDiagnosis = null)
        {
            if (EditorPrefs.GetString(FailureDialogKey, "") == attemptId) return;
            LastRepairSucceeded = false;
            EditorPrefs.SetString(FailureDialogKey, attemptId);
            EditorPrefs.SetString(FailureKey, failure);
            EditorPrefs.SetBool(AutoAttemptKey, true);
            var record = diagnosticRecord ?? UPilotServerRestartDiagnostics.Current;
            var diagnosis = record != null && record.operationId == attemptId
                ? UPilotRestartDiagnosticView.Full(record)
                : preflightDiagnosis ?? "失败时间：" + UPilotRestartDiagnosticView.Time(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) +
                  "\n尝试 ID：" + attemptId + "（尚无对应重启 operation ID）" +
                  "\n最后通过的验证门：未知；首个未通过的验证门：未知；status probe：未采集" +
                  "\n重启诊断记录与此次尝试不匹配；请打开高级设置核对 operation ID。";
            var healthFailure = record?.operationId == attemptId && UPilotServerRestartDiagnostics.IsHealthFailure(record);
            // 与弹窗共用尝试去重；先输出红色错误，避免向导或弹窗异常时丢失失败依据。
            UnityEngine.Debug.LogError("[UPilot] 自动修复失败\n" + failure + "\n\n" + diagnosis);
            // A failed health request is not evidence that the configured port is occupied.
            // Other setup failures retain the explicit configuration recovery entry point.
            if (!healthFailure) (openSetup ?? UPilotMainWindow.OpenSetupForRecovery)(failure);
            (showDialog ?? UPilotScrollableDialog.ShowDialog)("UPilot 自动修复失败", failure + "\n\n" +
                diagnosis +
                (healthFailure ? "\n请查看上述健康检查诊断；本次不更改配置，不自动重装服务。" :
                    "\n已重新打开安装向导；请按已确认的故障证据选择恢复步骤。未确认的原因不作结论。") +
                "\n只有服务身份、Bridge 握手和实际只读调用验证通过后才算完成。" +
                "\n不会停止归属未知或其他项目的进程，也不会自动重放重启或被中断的任务。");
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
                var stop = UPilotMcpServerManager.Instance.StopObservation;
                if (stop != null && (stop.id == _operationStopObservationId || stop.startedAt >= _operationStartedAtUtcMs))
                {
                    if (stop.Active)
                        return new UPilotMainSnapshot(UPilotMainState.Stopping, "正在停止 UPilot",
                            $"已用 {(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stop.startedAt) / 1000d:F1} 秒；本次最多 {stop.timeoutSeconds} 秒。", bridgeStatus.IsStarted, mcpStatus.IsRunning);
                    ClearOperation();
                    if (stop.status != "succeeded")
                        return new UPilotMainSnapshot(UPilotMainState.NeedsRepair, "停止未确认", stop.reason,
                            bridgeStatus.IsStarted, mcpStatus.IsRunning);
                    return new UPilotMainSnapshot(UPilotMainState.Stopped, "UPilot 已停止", "进程退出与端口释放已确认。", false, false);
                }
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

            var restart = UPilotServerRestartDiagnostics.Current;
            if (_operation == UPilotServiceOperation.Restarting && restart != null &&
                restart.requestedAtUtcMs >= _operationStartedAtUtcMs)
                return EvaluateRestartRecord(restart, bridgeStatus.IsStarted, mcpStatus.IsRunning);

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

        internal static UPilotMainSnapshot EvaluateRestartRecord(UPilotServerRestartRecord record, bool bridgeStarted, bool serverRunning)
        {
            if (record.status == "running")
                return new UPilotMainSnapshot(UPilotMainState.Restarting, "正在重启 UPilot",
                    UPilotServerRestartDiagnostics.WaitingMessage(record, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), bridgeStarted, serverRunning);
            ClearOperation();
            return new UPilotMainSnapshot(record.status == "succeeded" ? UPilotMainState.Ready : UPilotMainState.NeedsRepair,
                record.status == "succeeded" ? "已就绪" : "重启验证异常", record.error ?? "", bridgeStarted, serverRunning);
        }

        private static void BeginOperation(UPilotServiceOperation operation)
        {
            _operation = operation;
            _operationStartedAt = EditorApplication.timeSinceStartup;
            _operationStartedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static void ClearOperation()
        {
            _operationStopObservationId = "";
            _operation = UPilotServiceOperation.None;
            _operationStartedAt = 0d;
            _operationStartedAtUtcMs = 0;
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
                    bridge.WsPort != bridge.HttpPort)
                    return UPilotPortRegistration.TrySyncCurrent();

                var pair = UPilotPortAllocator.FindAvailablePair(bridge.WsPort, bridge.HttpPort);
                if (!EditorUtility.DisplayDialog("修改当前工程端口？",
                        $"当前端口不可绑定。将工程配置改为 WS {pair.wsPort} / HTTP {pair.httpPort}。",
                        "修改端口", "取消"))
                    return false;
                bridge.SetProjectEndpoints(UPilotBridge.DefaultWsHost, pair.wsPort, pair.httpPort);
                return true;
            }
            catch (Exception ex) { UnityEngine.Debug.LogError($"[UPilotPorts] 配置启动端口失败：{ex}"); return false; }
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
