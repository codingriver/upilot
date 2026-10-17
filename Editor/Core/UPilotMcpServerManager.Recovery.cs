// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMcpServerManager
    {
        private const string EditorRecoverySessionKey = "UPilot.ServerRecovery.EditorSession.v1";
        private const string ExistingRecoverySessionKey = "UPilot.ServerRecovery.Existing.v1";
        internal const int ExistingServiceReuseBudgetMs = 8000;

        [Serializable]
        internal sealed class ExistingServiceRecoveryRecord
        {
            public string id, projectPath, status, error = "", stopObservationId = "";
            public string pythonEntry, serverExe;
            public ServerLaunchSpec spec;
            public long deadline, reuseDeadline;
            public bool automatic, replacementStarted, revalidateOnly;
            public int replacementPid;
            public string validationStage = "", lastBlockReason = "";
            public string resetError = "", resetLifetime = "";
            public bool resetPerformed;
            public int resetUnityPid;
            public ServerLaunchSpec replacementSpec;
            public long replacementLauncherCreatedAt;
            public bool replacementRestoreBlocked;
            public int validationAttemptCount;
            public bool discoveryCompleted;
            public long discoveryWorkMs, discoveryDispatchDelayMs, discoveryResumeDelayMs, healthElapsedMs, readOnlyElapsedMs;
            [NonSerialized] internal long validationStartedTicks;
            [NonSerialized] internal string lastLoggedValidationState;
            [NonSerialized] internal ServerStopResult? verifiedStopResult;
        }

        // Isolated acceptance supplies a real process launcher without touching the global Bridge.
        internal Action<ExistingServiceRecoveryRecord> ExistingServiceReplacementStartOverride;
        internal Action<ExistingServiceRecoveryRecord> ExistingServiceShutdownOverride;
        private ExistingServiceRecoveryRecord _existingServiceRecovery;
        private CancellationTokenSource _existingServiceRecoveryCancellation;
        private bool _existingServiceRecoveryResumePending;
        private ServerStopTarget _recoveredServiceTarget;
        // Borrowed only during the existing discovery round; no ownership transfer or new observer.
        private ServerStopTarget _helloCandidateTarget;
        private string _helloCandidateInstance;
        private ServerLaunchSpec _recoveredServiceSpec;
        private string _recoveredServiceInstanceId;
        private bool _automaticStartRequest;
        private ServiceStopObservationRecord _restoredRestartStop;

        internal static bool IsRestoredStopForRestart(UPilotServerRestartRecord restart,
            ServiceStopObservationRecord stop, string project, string lifetime, int unityPid) =>
            restart?.status == "running" && restart.newProcessId <= 0 && stop != null &&
            !string.IsNullOrEmpty(restart.operationId) && stop.requestId == restart.operationId &&
            stop.originalRestartGeneration == restart.restartGeneration &&
            stop.MatchesScope(project, lifetime, unityPid) &&
            UPilotDeploymentDiagnostics.SamePath(restart.projectPath, project);

        // A Reload loses the caller's stop->launch waiter. Resume only the already dispatched
        // stop observer, then end the associated restart instead of leaving it running forever.
        private void RestoreRestartStopObservation()
        {
            if (!IsRestoredStopForRestart(UPilotServerRestartDiagnostics.Current, _stopObservation,
                UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, System.Diagnostics.Process.GetCurrentProcess().Id)) return;
            _restoredRestartStop = _stopObservation;
            EditorApplication.update -= ObserveRestoredRestartStop;
            EditorApplication.update += ObserveRestoredRestartStop;
            CompleteRestoredRestartStop();
        }

        private void ObserveRestoredRestartStop() => CompleteRestoredRestartStop();

        internal bool CompleteRestoredRestartStop(Action<string, string, string> recordFailure = null,
            Action<string, string> recordCanceled = null)
        {
            var stop = _restoredRestartStop;
            if (stop == null) return false;
            var restart = UPilotServerRestartDiagnostics.Current;
            if (!ReferenceEquals(stop, _stopObservation) || !IsRestoredStopForRestart(restart, stop,
                UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, System.Diagnostics.Process.GetCurrentProcess().Id))
            {
                ClearRestoredRestartStopObservation();
                return false;
            }
            if (stop.Active) return false;
            // End this observation before publishing the terminal; notification callbacks may reenter.
            ClearRestoredRestartStopObservation();
            var canceled = stop.status == "stopped";
            var message = $"Domain Reload 中断了原重启的停止后启动观察；原停止已结束，" +
                $"restartId={restart.operationId}；stopId={stop.id}；stopStatus={stop.status}；原因={stop.reason}。" +
                (canceled ? "本次重启已取消（restart_canceled）；未重放停止或启动。" :
                    "本次重启以 restart_recovery_required 结束；未重放停止或启动，请检查当前状态后明确发起新操作。");
            if (canceled)
            {
                if (recordCanceled != null) recordCanceled(restart.operationId, message);
                else UPilotServerRestartDiagnostics.RecordCanceled(restart.operationId, message);
                Logger.CloseDiagnostics(restart.operationId);
                Debug.Log(message);
            }
            else
            {
                if (recordFailure != null) recordFailure(restart.operationId, "restart_recovery_required", message);
                else UPilotServerRestartDiagnostics.RecordFailure(restart.operationId, "restart_recovery_required", message);
                Logger.ReportFailure("SERVER", message, stop.failureException, restart.operationId, terminal: true);
            }
            FinishServiceShutdown(stop.shutdownOwnerId, "restored_restart_ended");
            NotifyStatusChanged();
            return true;
        }

        private void ClearRestoredRestartStopObservation()
        {
            EditorApplication.update -= ObserveRestoredRestartStop;
            _restoredRestartStop = null;
        }

        internal bool ExistingServiceRecoveryOwnsStartup => _existingServiceRecovery != null;
        internal bool ExistingServiceReplacementNeedsReceiptRestore =>
            _existingServiceRecovery?.status == "replacement_started" || _existingServiceRecovery?.replacementRestoreBlocked == true;
        private bool HasExistingServiceRecoveryFailure => _existingServiceRecovery?.status == "failed" ||
            (_existingServiceRecovery?.status == "stopped" && !string.IsNullOrEmpty(_existingServiceRecovery.error));
        internal string ExistingServiceRecoveryFailure => HasExistingServiceRecoveryFailure
            ? _existingServiceRecovery.error : "";
        internal string ExistingServiceRecoveryFailureId => HasExistingServiceRecoveryFailure
            ? _existingServiceRecovery.id ?? "" : "";
        internal bool HasVerifiedRecoveredAttachment
        {
            get
            {
                lock (_statusLock) return _recoveredServiceTarget != null && !_recoveredServiceTarget.HasExited &&
                    _recoveredServiceSpec != null && _recoveredServiceSpec.httpPort == HttpPort &&
                    _recoveredServiceSpec.wsPort == WsPort && !string.IsNullOrEmpty(_recoveredServiceInstanceId);
            }
        }

        internal static bool IsNewEditorRecoverySession(bool initialized, bool hasLaunch, bool hasStop) =>
            !initialized && !hasLaunch && !hasStop;

        private bool BeginCurrentEditorRecoverySession()
        {
            var fresh = IsNewEditorRecoverySession(SessionState.GetBool(EditorRecoverySessionKey, false),
                !string.IsNullOrEmpty(SessionState.GetString(LaunchSessionKey, "")),
                !string.IsNullOrEmpty(SessionState.GetString(StopSessionKey, "")));
            SessionState.SetBool(EditorRecoverySessionKey, true);
            return fresh;
        }

        internal void StartServerAutomatically()
        {
            _automaticStartRequest = true;
            try { StartServer(); }
            finally { _automaticStartRequest = false; }
        }

        private bool TryBeginExistingServiceRecovery(ServerLaunchSpec spec)
        {
            if (_existingServiceRecoveryCancellation != null) return true;
            var scope = GetStopScope();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var deadline = LaunchDeadline();
            var record = new ExistingServiceRecoveryRecord {
                id = Guid.NewGuid().ToString("N"), projectPath = scope.ProjectPath,
                pythonEntry = scope.PythonEntry, serverExe = scope.ServerExe,
                spec = (spec ?? ExistingServiceComparisonSpec(scope)).Snapshot(), status = "verifying", automatic = _automaticStartRequest,
                deadline = deadline, reuseDeadline = Math.Min(deadline, now + ExistingServiceReuseBudgetMs),
            };
            StopLaunchObservation();
            ReleaseRecoveredServiceAttachment();
            _launcherProcess?.Dispose(); _launcherProcess = null;
            DisposeTrackedProcess(); _trackedProcessId = null;
            _launchState = null; PersistLaunchState();
            _launchNeedsRevalidation = false;
            _existingServiceRecovery = record;
            SaveExistingServiceRecovery();
            _startInProgress = true;
            var generation = Interlocked.Increment(ref _startAttemptGeneration);
            var cancellation = new CancellationTokenSource();
            _existingServiceRecoveryCancellation = cancellation;
            _ = RecoverExistingServiceAsync(record, scope, generation, _restartGeneration, cancellation);
            return true;
        }

        // Reuse needs current endpoint/entry expectations, never a Python launcher probe.
        private ServerLaunchSpec ExistingServiceComparisonSpec(ServerStopScope scope)
        {
            var python = UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.Python;
            return new ServerLaunchSpec {
                projectPath = scope.ProjectPath, httpPort = scope.HttpPort, wsPort = scope.WsPort,
                expectedEntry = python ? scope.PythonEntry : scope.ServerExe,
                expectedModuleRoot = python ? Path.Combine(UPilotDeploymentDiagnostics.PackageRoot,
                    "upilotserver~", "src", "upilot_mcp") : "",
            };
        }

        private bool OwnsExistingServiceRecovery(ExistingServiceRecoveryRecord record,
            CancellationTokenSource cancellation) => ReferenceEquals(record, _existingServiceRecovery) &&
            ReferenceEquals(cancellation, _existingServiceRecoveryCancellation) && !cancellation.IsCancellationRequested &&
            !UPilotQuickStart.IsExplicitlyStopped && (!record.automatic || AutoStartEnabled) &&
            record.spec.httpPort == HttpPort && record.spec.wsPort == WsPort &&
            UPilotDeploymentDiagnostics.SamePath(record.projectPath, UPilotProjectConfig.ProjectRoot);

        private async Task<ServerStopDiscovery> DiscoverExistingServiceAsync(ServerStopScope scope,
            long deadline, CancellationToken token, ExistingServiceRecoveryRecord record)
        {
            var queuedAt = Stopwatch.GetTimestamp();
            long startedAt = 0, completedAt = 0;
            var discovery = Task.Run(() => {
                Interlocked.Exchange(ref startedAt, Stopwatch.GetTimestamp());
                try { return DiscoverStopTargets(scope, Array.Empty<int>(), deadline); }
                finally { Interlocked.Exchange(ref completedAt, Stopwatch.GetTimestamp()); }
            });
            var remaining = Math.Max(0, deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (await Task.WhenAny(discovery, Task.Delay((int)Math.Min(int.MaxValue, remaining), token)) == discovery)
            {
                try { return await discovery; }
                finally
                {
                    var scanStartedAt = Interlocked.Read(ref startedAt);
                    var scanCompletedAt = Interlocked.Read(ref completedAt);
                    record.discoveryCompleted = true;
                    record.discoveryDispatchDelayMs = RecoveryElapsedBetween(queuedAt, scanStartedAt);
                    record.discoveryWorkMs = RecoveryElapsedBetween(scanStartedAt, scanCompletedAt);
                    record.discoveryResumeDelayMs = RecoveryElapsedBetween(scanCompletedAt, Stopwatch.GetTimestamp());
                    Logger.Lifecycle("recovery_discovery_completed", $"recoveryId={record.id} " +
                        $"dispatchDelayMs={record.discoveryDispatchDelayMs} scanMs={record.discoveryWorkMs} " +
                        $"resumeDelayMs={record.discoveryResumeDelayMs} deadlineUtcMs={deadline}");
                }
            }
            // OS inspection may complete after cancellation. Its handles must never escape.
            _ = discovery.ContinueWith(t => {
                if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
                else { var ignored = t.Exception; }
            }, TaskScheduler.Default);
            var scanStarted = Interlocked.Read(ref startedAt);
            var scanCompleted = Interlocked.Read(ref completedAt);
            var resumedAt = Stopwatch.GetTimestamp();
            record.discoveryCompleted = scanCompleted > 0;
            record.discoveryDispatchDelayMs = RecoveryElapsedBetween(queuedAt, scanStarted);
            record.discoveryWorkMs = RecoveryElapsedBetween(scanStarted, scanCompleted > 0 ? scanCompleted : resumedAt);
            record.discoveryResumeDelayMs = RecoveryElapsedBetween(scanCompleted, resumedAt);
            Logger.Lifecycle("recovery_discovery_incomplete", $"recoveryId={record.id} " +
                $"started={scanStarted > 0} workerCompleted={record.discoveryCompleted} " +
                $"dispatchDelayMs={RecoveryDuration(record.discoveryDispatchDelayMs, scanStarted > 0)} " +
                $"scanMs={RecoveryDuration(record.discoveryWorkMs, record.discoveryCompleted)} " +
                $"scanObservedMs={RecoveryDuration(record.discoveryWorkMs, scanStarted > 0)} " +
                $"resumeDelayMs={RecoveryDuration(record.discoveryResumeDelayMs, record.discoveryCompleted)} deadlineUtcMs={deadline}");
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("遗留服务进程发现超过本次复用验证期限。");
        }

        private static long RecoveryElapsedBetween(long start, long end) =>
            start <= 0 || end <= 0 ? 0 : Math.Max(0, (long)((end - start) * 1000d / Stopwatch.Frequency));

        private static string RecoveryDuration(long durationMs, bool observed) =>
            observed ? durationMs.ToString() : "unknown";

        private static void RecordExistingServiceValidation(ExistingServiceRecoveryRecord record, string stage, string blockReason = "")
        {
            record.validationStage = stage;
            record.lastBlockReason = blockReason;
            var key = stage + ":" + blockReason;
            if (record.lastLoggedValidationState == key) return;
            record.lastLoggedValidationState = key;
            Logger.Lifecycle("recovery_validation_stage", $"recoveryId={record.id} stage={stage} blockReason={blockReason} " +
                $"attempt={record.validationAttemptCount} elapsedMs={Logger.LifecycleElapsedMs(record.validationStartedTicks)} " +
                $"reuseDeadlineUtcMs={record.reuseDeadline}");
        }

        internal static bool CanReuseExistingService(McpServerStatus status, string projectPath, int processId,
            bool alive, bool ownsHttp, bool ownsWs) => alive && ownsHttp && ownsWs && processId > 0 &&
            status.IdentityAvailable && status.IdentityIdentifiesUPilot && status.IdentityServerProcessId == processId &&
            !string.IsNullOrEmpty(status.Identity?.server_instance_id) &&
            UPilotDeploymentDiagnostics.SamePath(status.IdentityProjectPath, projectPath);

        private static string ExistingServiceOwnershipBlock(McpServerStatus status, string projectPath,
            ServerStopTarget target, bool ownsHttp, bool ownsWs, bool unmatchedOwners)
        {
            if (!status.IdentityAvailable) return "hello_identity_unavailable";
            if (!status.IdentityIdentifiesUPilot) return "hello_identity_missing";
            if (target == null) return "process_not_discovered";
            if (target.HasExited) return "process_exited";
            if (!ownsHttp) return "http_owner_unverified";
            if (!ownsWs) return "ws_owner_unverified";
            if (status.IdentityServerProcessId != target.ProcessId || target.ProcessId <= 0) return "hello_pid_mismatch";
            if (string.IsNullOrEmpty(status.Identity?.server_instance_id)) return "server_instance_missing";
            if (!UPilotDeploymentDiagnostics.SamePath(status.IdentityProjectPath, projectPath)) return "project_mismatch";
            return unmatchedOwners ? "unmatched_port_owner" : "";
        }

        private async Task RecoverExistingServiceAsync(ExistingServiceRecoveryRecord record, ServerStopScope scope,
            int startGeneration, long restartGeneration, CancellationTokenSource cancellation, bool revalidateOnly = false)
        {
            var token = cancellation.Token;
            var lifecycle = SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, 0, record.id, record.reuseDeadline);
            Exception validationError = null;
            record.validationStartedTicks = Stopwatch.GetTimestamp();
            record.validationAttemptCount = 0;
            record.healthElapsedMs = record.readOnlyElapsedMs = 0;
            record.discoveryWorkMs = record.discoveryDispatchDelayMs = record.discoveryResumeDelayMs = 0;
            record.discoveryCompleted = false;
            record.lastLoggedValidationState = null;
            RecordExistingServiceValidation(record, "discovery");
            try
            {
                using (var discovery = await DiscoverExistingServiceAsync(scope, record.reuseDeadline, token, record))
                using (var client = CreateLoopbackStatusClient())
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    while (OwnsExistingServiceRecovery(record, cancellation) && startGeneration == _startAttemptGeneration &&
                           restartGeneration == _restartGeneration && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < record.reuseDeadline)
                    {
                        try
                        {
                            record.validationAttemptCount++;
                            record.validationStage = "identity";
                            var healthStartedAt = Stopwatch.GetTimestamp();
                            McpServerStatus status;
                            try { status = await GetRestartHealthAsync(client, scope.HttpPort, record.reuseDeadline, token, this, 1000); }
                            finally { record.healthElapsedMs += Logger.LifecycleElapsedMs(healthStartedAt); }
                            token.ThrowIfCancellationRequested();
                            var target = discovery.Targets.FirstOrDefault(p => p.ProcessId == status.IdentityServerProcessId);
                            var ownsHttp = discovery.ListeningPorts.TryGetValue(status.IdentityServerProcessId, out var ports) &&
                                ports.Contains(scope.HttpPort);
                            var ownsWs = ports != null && ports.Contains(scope.WsPort);
                            var ownershipBlock = ExistingServiceOwnershipBlock(status, scope.ProjectPath, target,
                                ownsHttp, ownsWs, discovery.UnmatchedPortOwners.Length > 0);
                            if (ownershipBlock.Length == 0)
                            {
                                _helloCandidateInstance = status.Identity.server_instance_id;
                                _helloCandidateTarget = target;
                                status.ProcessId = target.ProcessId;
                                status.IsRunning = status.HttpPortListening = status.WsPortListening = true;
                                status.ProcessOwnership = McpProcessOwnership.CurrentUPilot;
                                status.ProcessOwnershipEvidence = "当前精确入口及双端口参数、活体句柄与当前 ACK PID 已核实";
                                var bridge = UPilotBridge.Instance.GetStatus();
                                var bridgeBlock = !bridge.IsWsOpen ? "bridge_closed" : !bridge.IsAuthenticated ? "bridge_not_authenticated" :
                                    string.IsNullOrEmpty(bridge.SessionId) ? "bridge_session_missing" :
                                    status.Identity?.bridge_session_id != bridge.SessionId ? "bridge_session_mismatch" : "";
                                var deployment = bridgeBlock.Length == 0 ? UPilotDeploymentDiagnostics.Observe(bridge, status, record.spec) : null;
                                if (bridgeBlock.Length == 0 && !deployment.Any(issue => issue.Blocking))
                                {
                                    record.validationStage = "readonly";
                                    var readOnlyStartedAt = Stopwatch.GetTimestamp();
                                    try { await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, record.reuseDeadline, token); }
                                    finally { record.readOnlyElapsedMs += Logger.LifecycleElapsedMs(readOnlyStartedAt); }
                                    var after = UPilotBridge.Instance.GetStatus();
                                    if (OwnsExistingServiceRecovery(record, cancellation) && startGeneration == _startAttemptGeneration &&
                                        restartGeneration == _restartGeneration && !target.HasExited &&
                                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < record.reuseDeadline &&
                                        after.IsWsOpen && after.IsAuthenticated && after.SessionId == bridge.SessionId)
                                    {
                                        if (!CacheVerifiedStartupStatus(status, lifecycle, bridge.SessionId, () =>
                                        {
                                            discovery.Targets.Remove(target);
                                            _recoveredServiceTarget = target;
                                            _recoveredServiceSpec = record.spec.Snapshot();
                                            _recoveredServiceInstanceId = status.Identity.server_instance_id;
                                            record.status = "reused"; record.error = "";
                                            RecordExistingServiceValidation(record, "verified");
                                            SaveExistingServiceRecovery();
                                            ResetStopObservation("new_service_start_or_restart");
                                            return true;
                                        })) throw new LifecycleProbeDeferredException();
                                        Logger.CloseDiagnostics(record.id);
                                        UnityEngine.Debug.Log($"[UPilotMcpServerManager] 已复用遗留服务；PID={target.ProcessId}；HTTP={scope.HttpPort}；WS={scope.WsPort}；recoveryId={record.id}；当前 Bridge 及实际只读调用已验证。");
                                        FinishExistingServiceRecoveryWork(record, cancellation);
                                        return;
                                    }
                                    RecordExistingServiceValidation(record, "readonly", "identity_or_deadline_changed_after_probe");
                                }
                                else if (bridgeBlock.Length > 0) RecordExistingServiceValidation(record, "bridge", bridgeBlock);
                                else RecordExistingServiceValidation(record, "deployment", string.Join(",", deployment.Select(issue => issue.Code)));
                            }
                            else RecordExistingServiceValidation(record, "ownership", ownershipBlock);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            validationError = ex;
                            RecordExistingServiceValidation(record, record.validationStage, ex.GetType().Name);
                        }
                        var remaining = record.reuseDeadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (remaining > 0) await Task.Delay((int)Math.Min(250, remaining), token);
                    }
                }
                if (record.validationAttemptCount == 0)
                    RecordExistingServiceValidation(record, "discovery", "reuse_deadline_before_health");
                validationError = new TimeoutException("遗留服务未在最多8秒内通过当前入口、项目、Bridge 握手及实际只读验证。", validationError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                validationError = ex;
                RecordExistingServiceValidation(record, record.validationStage, ex.GetType().Name);
            }
            finally
            {
                _helloCandidateTarget = null;
                _helloCandidateInstance = null;
                Logger.Lifecycle("recovery_validation_completed", $"recoveryId={record.id} status={record.status} " +
                    $"stage={record.validationStage} lastBlockReason={record.lastBlockReason} attempts={record.validationAttemptCount} " +
                    $"elapsedMs={RecoveryDuration(Logger.LifecycleElapsedMs(record.validationStartedTicks), record.validationStartedTicks > 0)} " +
                    $"discoveryCompleted={record.discoveryCompleted} scanMs={RecoveryDuration(record.discoveryWorkMs, record.discoveryCompleted)} " +
                    $"dispatchDelayMs={RecoveryDuration(record.discoveryDispatchDelayMs, record.discoveryCompleted || record.discoveryWorkMs > 0)} " +
                    $"resumeDelayMs={RecoveryDuration(record.discoveryResumeDelayMs, record.discoveryCompleted)} " +
                    $"healthAwaitMs={RecoveryDuration(record.healthElapsedMs, record.validationAttemptCount > 0)} " +
                    $"readonlyAwaitMs={RecoveryDuration(record.readOnlyElapsedMs, record.readOnlyElapsedMs > 0 || record.validationStage == "readonly" || record.status == "reused")} " +
                    $"reuseDeadlineUtcMs={record.reuseDeadline} deadlineUtcMs={record.deadline}");
            }

            try
            {
                if (!OwnsExistingServiceRecovery(record, cancellation) || startGeneration != _startAttemptGeneration ||
                    restartGeneration != _restartGeneration) return;
                if (record.status == "reused") return;
                if (revalidateOnly) throw validationError ?? new InvalidOperationException("重载后的遗留服务附着未通过验证。");
                var authenticationCode = UPilotBridge.Instance.GetStatus().AuthenticationErrorCode;
                if (authenticationCode == "READY_CONTRACT_MISMATCH" || authenticationCode == "READY_IDENTITY_CONTRACT_MISMATCH" ||
                    authenticationCode == "identity_contract_mismatch" || authenticationCode == "READY_PROJECT_IDENTITY_MISMATCH")
                    throw new InvalidOperationException("配套就绪协议或项目身份不兼容，未自动重启或重新安装：" + authenticationCode);
                Logger.ReportWarning("SERVER", $"遗留服务复用验证失败，将在原期限内清理并启动一次；recoveryId={record.id}；" + validationError,
                    record.id, "existing-service-reuse");
                record.status = "cleaning";
                SaveExistingServiceRecovery();
                BeginServiceShutdown(record.id, "recovery_preparing", record.deadline);
                if (ExistingServiceShutdownOverride != null) ExistingServiceShutdownOverride(record);
                else
                {
                    var reset = ResetActiveWorkForServiceOwner(record.id, "existing_service_recovery", record.id);
                    record.resetError = reset.Error ?? "";
                    record.resetPerformed = reset.Performed;
                    record.resetLifetime = UPilotServiceLifetime.Id;
                    record.resetUnityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    SaveExistingServiceRecovery();
                    if (!string.IsNullOrEmpty(reset.Error))
                        Logger.Lifecycle("recovery_activity_reset", $"recoveryId={record.id} outcome=partial_reset");
                    UPilotBridge.Instance.Stop();
                }
                var stopTask = StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, record.id,
                    parentDeadlineUtcMs: record.deadline, scope: scope, preserveShutdown: true);
                record.stopObservationId = _stopObservation?.id ?? "";
                SaveExistingServiceRecovery();
                var stopped = await stopTask;
                if (!OwnsExistingServiceRecovery(record, cancellation)) return;
                StartExistingServiceReplacement(record, stopped);
            }
            catch (Exception ex)
            {
                if (OwnsExistingServiceRecovery(record, cancellation)) FailExistingServiceRecovery(record, ex);
            }
            finally
            {
                FinishExistingServiceRecoveryWork(record, cancellation);
            }
        }

        private void StartExistingServiceReplacement(ExistingServiceRecoveryRecord record, ServerStopResult stopped)
        {
            Logger.Lifecycle("recovery_replacement_gate", $"recoveryId={record.id} stopId={record.stopObservationId} " +
                $"stopConfirmed={stopped.StopConfirmed} portsAvailable={stopped.PortsAvailable} " +
                $"observationSucceeded={stopped.ObservationSucceeded} current={IsStopResultCurrent(stopped)} " +
                $"replacementStarted={record.replacementStarted} deadlineUtcMs={record.deadline}");
            if (!stopped.StopConfirmed || !stopped.PortsAvailable || !stopped.ObservationSucceeded || !IsStopResultCurrent(stopped))
                throw new InvalidOperationException("遗留服务清理没有取得进程退出、双端口释放和当前操作身份的完整证据；未启动新服务。");
            if (record.replacementStarted) throw new InvalidOperationException("本次遗留服务恢复已派发一次启动，不重复派发。");
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= record.deadline)
                throw new TimeoutException("遗留服务清理后原恢复期限已到，未启动新服务。");
            record.verifiedStopResult = stopped;
            record.replacementStarted = true; record.status = "replacement_starting";
            SaveExistingServiceRecovery(); // Persist intent before the single Process.Start.
            if (ExistingServiceReplacementStartOverride != null)
                ExistingServiceReplacementStartOverride(record);
            else
            {
                _pendingLaunchSpec = PrepareLaunchSpec(record.deadline);
                _pendingLaunchDeadline = record.deadline;
                _startInProgress = false;
                StartNewServer();
                UPilotBridge.Instance.EnsureStarted();
            }
            if (record.replacementPid <= 0)
                throw new InvalidOperationException("遗留服务已清理，但本次新服务启动未返回进程；不会循环重启。");
            Logger.CloseDiagnostics(record.id);
        }

        private void FailExistingServiceRecovery(ExistingServiceRecoveryRecord record, Exception exception, bool preserveShutdown = false)
        {
            record.status = "failed"; record.error = exception.Message;
            SaveExistingServiceRecovery();
            if (!preserveShutdown) FinishServiceShutdown(record.id, "recovery_failed");
            if (_lifecycleOperationId == record.id)
                EndLifecyclePhase(_lifecycleGeneration, exception.Message, exception is TimeoutException,
                    emitDiagnostic: false, failureException: exception);
            Logger.ReportFailure("SERVER", $"遗留服务恢复结束；recoveryId={record.id}；HTTP={record.spec?.httpPort.ToString() ?? "unknown"}；WS={record.spec?.wsPort.ToString() ?? "unknown"}。",
                exception, record.id, terminal: true);
            UPilotStartupDiagnostics.RecordBlockingReason("existing_service_recovery_failed", exception.Message);
            NotifyStatusChanged();
        }

        private void FinishExistingServiceRecoveryWork(ExistingServiceRecoveryRecord record, CancellationTokenSource cancellation)
        {
            if (ReferenceEquals(cancellation, _existingServiceRecoveryCancellation))
            {
                _existingServiceRecoveryCancellation = null;
                _startInProgress = false; _pendingLaunchSpec = null; _pendingLaunchDeadline = 0;
                if (record.status == "verifying" || record.status == "cleaning")
                {
                    record.status = "stopped";
                    SaveExistingServiceRecovery();
                    if (_lifecycleOperationId == record.id) EndLifecyclePhase(_lifecycleGeneration);
                }
            }
            if (record.status == "stopped" || record.status == "failed" || record.status == "reused")
                FinishServiceShutdown(record.id, "recovery_" + record.status);
            cancellation.Dispose();
        }

        private void SaveExistingServiceRecovery() => SessionState.SetString(ExistingRecoverySessionKey,
            _existingServiceRecovery == null ? "" : JsonUtility.ToJson(_existingServiceRecovery));

        private void RestoreExistingServiceRecovery()
        {
            var json = SessionState.GetString(ExistingRecoverySessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var record = JsonUtility.FromJson<ExistingServiceRecoveryRecord>(json);
                if (record == null || !UPilotDeploymentDiagnostics.SamePath(record.projectPath, UPilotProjectConfig.ProjectRoot)) return;
                _existingServiceRecovery = record;
                if ((record.status == "replacement_starting" || record.status == "replacement_started" || record.replacementRestoreBlocked) &&
                    !UPilotServerRestartDiagnostics.TryGetActive(out _) && !UPilotServiceMaintenance.IsActive)
                {
                    var current = CaptureServiceShutdown();
                    if ((current.Stopping || current.ReplacementEligible) &&
                        current.OwnerId != record.id && !current.OwnerId.StartsWith("bridge:", StringComparison.Ordinal))
                    { record.replacementRestoreBlocked = true; SaveExistingServiceRecovery(); return; }
                    BeginServiceShutdown(string.IsNullOrEmpty(record.id) ? "recovery:unknown" : record.id,
                        "recovery_reload_receipt_verifying", record.deadline);
                }
                if (record.status == "replacement_starting" || (record.status == "replacement_started" &&
                    (record.spec == null || string.IsNullOrEmpty(SessionState.GetString(LaunchSessionKey, "")))))
                {
                    record.replacementRestoreBlocked = true;
                    FailExistingServiceRecovery(record, new InvalidOperationException(
                        "Reload 中断了本次唯一新服务启动，缺少完整启动证据；不重放启动，请检查后手动启动。"), preserveShutdown: true);
                    return;
                }
                if (record.spec == null) return;
                if (record.status == "verifying" || record.status == "cleaning" || record.status == "reused")
                {
                    // Recovery must not wait for an Inspector-driven delayCall after Reload.
                    _existingServiceRecoveryResumePending = true;
                    EditorApplication.update -= ResumeExistingServiceRecovery;
                    EditorApplication.update += ResumeExistingServiceRecovery;
                }
            }
            catch (Exception ex) { Logger.ReportFailure("SERVER", "恢复遗留服务观察记录失败；没有重放停止或启动。", ex, terminal: true); }
        }

        internal static bool CanRestoreExistingServiceResetReceipt(ExistingServiceRecoveryRecord record,
            ServiceStopObservationRecord stop, string project, string lifetime, int unityPid, long now, out string reason)
        {
            if (record?.resetPerformed != true) { reason = "reset_receipt_unknown"; return false; }
            if (record.status != "cleaning" || record.replacementStarted || string.IsNullOrEmpty(record.id) ||
                !UPilotDeploymentDiagnostics.SamePath(record.projectPath, project) ||
                string.IsNullOrEmpty(lifetime) || unityPid <= 0 ||
                record.resetLifetime != lifetime || record.resetUnityPid != unityPid)
            { reason = "reset_receipt_recovery_identity_mismatch"; return false; }
            if (stop == null || stop.id != record.stopObservationId || string.IsNullOrEmpty(stop.id) ||
                stop.requestId != record.id || stop.shutdownOwnerId != record.id || !stop.preserveShutdown ||
                !stop.MatchesScope(project, lifetime, unityPid))
            { reason = "reset_receipt_stop_identity_mismatch"; return false; }
            if (now >= record.deadline || stop.deadline <= 0 || stop.deadline > record.deadline)
            { reason = "reset_receipt_deadline_mismatch"; return false; }
            reason = "current";
            return true;
        }

        internal static bool CanRestoreExistingServiceReplacementResetReceipt(ExistingServiceRecoveryRecord record,
            ServerLaunchState state, string project, string lifetime, int unityPid, long now, out string reason)
        {
            reason = "reset_receipt_unknown";
            if (record?.resetPerformed != true) return false;
            reason = "reset_receipt_recovery_identity_mismatch";
            if (record.status != "replacement_started" || record.replacementRestoreBlocked || !record.replacementStarted ||
                string.IsNullOrEmpty(record.id) || !UPilotDeploymentDiagnostics.SamePath(record.projectPath, project) ||
                string.IsNullOrEmpty(lifetime) || unityPid <= 0 || record.resetLifetime != lifetime || record.resetUnityPid != unityPid)
                return false;
            reason = "reset_receipt_deadline_mismatch";
            if (record.deadline <= now || state == null || state.deadlineUtcMs != record.deadline) return false;
            reason = "reset_receipt_replacement_launch_mismatch";
            if (record.replacementSpec == null || state.spec == null || state.terminal ||
                record.replacementSpec.Validate().Length != 0 || state.spec.Validate().Length != 0 ||
                !UPilotDeploymentDiagnostics.SamePath(record.replacementSpec.projectPath, project) ||
                JsonUtility.ToJson(record.replacementSpec) != JsonUtility.ToJson(state.spec) ||
                record.replacementPid <= 0 || record.replacementLauncherCreatedAt <= 0 ||
                record.replacementPid != state.launcherPid || record.replacementLauncherCreatedAt != state.launcherCreatedAt)
                return false;
            reason = "current";
            return true;
        }

        internal bool RestoreExistingServiceReplacementResetReceipt(out string reason)
        {
            var record = _existingServiceRecovery;
            var state = _launchState;
            bool Reject(string failure)
            {
                if (record != null) { record.replacementRestoreBlocked = true; SaveExistingServiceRecovery(); }
                Logger.Lifecycle("recovery_reset_receipt_restore_rejected", $"recoveryId={record?.id ?? "unknown"} reason={failure} {CaptureServiceShutdown().Detail}");
                return false;
            }
            using var editor = System.Diagnostics.Process.GetCurrentProcess();
            if (!CanRestoreExistingServiceReplacementResetReceipt(record, state, UPilotProjectConfig.ProjectRoot,
                UPilotServiceLifetime.Id, editor.Id, StopNow, out reason)) return Reject(reason);
            reason = "reset_receipt_replacement_handles_unverified";
            try
            {
                if (_launcherProcess == null || _trackedProcess == null || _launcherProcess.HasExited || _trackedProcess.HasExited ||
                    _launcherProcess.Id != state.launcherPid || _launcherProcess.StartTime.ToUniversalTime().Ticks != state.launcherCreatedAt ||
                    _trackedProcess.Id != (state.verified ? state.servicePid : state.launcherPid) ||
                    _trackedProcess.StartTime.ToUniversalTime().Ticks != (state.verified ? state.serviceCreatedAt : state.launcherCreatedAt))
                    return Reject(reason);
            }
            catch (Exception) { return Reject(reason); }
            var shutdown = CaptureServiceShutdown();
            if (shutdown.OwnerId != record.id) { reason = "shutdown_owner_or_generation_changed"; return Reject(reason); }
            if (!RestoreServiceWorkResetReceipt(shutdown, record.resetPerformed, record.resetError, record.deadline, out reason))
                return Reject(reason);
            _launchNeedsRevalidation = true;
            AllowServiceReplacement(record.id);
            reason = "restored";
            return true;
        }

        private async void ResumeExistingServiceRecovery()
        {
            EditorApplication.update -= ResumeExistingServiceRecovery;
            if (!_existingServiceRecoveryResumePending) return;
            _existingServiceRecoveryResumePending = false;
            var record = _existingServiceRecovery;
            if (record == null || _existingServiceRecoveryCancellation != null || UPilotQuickStart.IsExplicitlyStopped) return;
            var cancellation = new CancellationTokenSource();
            _existingServiceRecoveryCancellation = cancellation;
            try
            {
                if (record.automatic && !AutoStartEnabled) return;
                if (record.status == "cleaning")
                {
                    if (!CanRestoreExistingServiceResetReceipt(record, _stopObservation, UPilotProjectConfig.ProjectRoot,
                        UPilotServiceLifetime.Id, System.Diagnostics.Process.GetCurrentProcess().Id,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var receiptReason))
                        throw new InvalidOperationException("Reload 后原清理凭据未知或身份不符；reason=" + receiptReason +
                            "；不重新清理，不重放停止或启动。");
                    var shutdown = BeginServiceShutdown(record.id, "recovery_cleaning_restored", record.deadline);
                    if (!RestoreServiceWorkResetReceipt(shutdown, record.resetPerformed, record.resetError,
                        record.deadline, out receiptReason))
                        throw new InvalidOperationException("Reload 后原清理结果无法恢复；reason=" + receiptReason +
                            "；不重新清理，不重放停止或启动。");
                    ServerStopResult stopped;
                    if (_stopObservation.Active && _stopCompletion != null) stopped = await _stopCompletion.Task;
                    else stopped = new ServerStopResult(_stopObservation.processesExited, _stopObservation.portsAvailable,
                        _statusGeneration, _startAttemptGeneration, _restartGeneration, _stopObservation.status == "succeeded");
                    if (OwnsExistingServiceRecovery(record, cancellation)) StartExistingServiceReplacement(record, stopped);
                    return;
                }
                var revalidateOnly = record.revalidateOnly || record.status == "reused";
                if (revalidateOnly)
                {
                    var wasReused = record.status == "reused";
                    record.revalidateOnly = true;
                    record.status = "verifying";
                    if (wasReused) record.reuseDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ExistingServiceReuseBudgetMs;
                    SaveExistingServiceRecovery();
                }
                var scope = new ServerStopScope(record.projectPath, record.pythonEntry, record.serverExe,
                    record.spec.httpPort, record.spec.wsPort);
                _startInProgress = true;
                await RecoverExistingServiceAsync(record, scope, _startAttemptGeneration, _restartGeneration, cancellation, revalidateOnly);
                cancellation = null; // The worker disposes its own source.
            }
            catch (Exception ex) { if (OwnsExistingServiceRecovery(record, cancellation)) FailExistingServiceRecovery(record, ex); }
            finally { if (cancellation != null) FinishExistingServiceRecoveryWork(record, cancellation); }
        }

        private void RecordExistingServiceReplacement(int processId)
        {
            if (_existingServiceRecovery?.status != "replacement_starting") return;
            _existingServiceRecovery.replacementPid = processId;
            _existingServiceRecovery.replacementSpec = _launchState?.spec?.Snapshot();
            _existingServiceRecovery.replacementLauncherCreatedAt = _launchState?.launcherCreatedAt ?? 0;
            _existingServiceRecovery.status = "replacement_started";
            SaveExistingServiceRecovery();
        }

        private void ObserveExistingServiceReplacementOutcome()
        {
            var record = _existingServiceRecovery;
            if (record?.status != "replacement_started" || _launchState?.launcherPid != record.replacementPid) return;
            if (_launchState.readyVerified) record.status = "succeeded";
            else if (_launchState.terminal) { record.status = "failed"; record.error = _launchState.error; }
            else return;
            SaveExistingServiceRecovery();
            if (!record.replacementRestoreBlocked) FinishServiceShutdown(record.id, "replacement_" + record.status);
        }

        private void ReleaseRecoveredServiceAttachment(bool resetPriorRecovery = false)
        {
            lock (_statusLock)
            {
                _recoveredServiceTarget?.Dispose(); _recoveredServiceTarget = null;
                _recoveredServiceSpec = null; _recoveredServiceInstanceId = null;
            }
            if (resetPriorRecovery && _existingServiceRecovery?.status != "replacement_starting")
            {
                _existingServiceRecovery = null;
                SaveExistingServiceRecovery();
            }
        }

        private void CancelExistingServiceRecoveryForStop(string requestId)
        {
            if (requestId == _existingServiceRecovery?.id) return;
            _existingServiceRecoveryResumePending = false;
            EditorApplication.update -= ResumeExistingServiceRecovery;
            _existingServiceRecoveryCancellation?.Cancel();
            _existingServiceRecoveryCancellation = null;
            if (_existingServiceRecovery != null && (_existingServiceRecovery.status == "verifying" ||
                _existingServiceRecovery.status == "cleaning"))
            {
                _existingServiceRecovery.status = "stopped";
                SaveExistingServiceRecovery();
            }
            ReleaseRecoveredServiceAttachment();
        }

        // Revoke the waiter before a reset completes its stop Task. That continuation must
        // observe lost ownership, while a replacement launch keeps its own identity intact.
        internal void CancelExistingServiceRecoveryForReset(string preservedOwnerId)
        {
            var record = _existingServiceRecovery;
            if (record == null || (!string.IsNullOrEmpty(preservedOwnerId) && record.id == preservedOwnerId) ||
                (record.status != "verifying" && record.status != "cleaning")) return;
            _existingServiceRecoveryResumePending = false;
            EditorApplication.update -= ResumeExistingServiceRecovery;
            var cancellation = _existingServiceRecoveryCancellation;
            _existingServiceRecoveryCancellation = null;
            record.status = "stopped";
            record.error = "service_reset：服务生命周期已变化，本次恢复验证已中断；尚未确认服务就绪，请检查当前状态后明确启动或重启。";
            _startInProgress = false;
            cancellation?.Cancel();
            SaveExistingServiceRecovery();
            if (_lifecycleOperationId == record.id) EndLifecyclePhase(_lifecycleGeneration);
            ReleaseRecoveredServiceAttachment();
            Logger.Lifecycle("recovery_superseded", $"recoveryId={record.id} stopId={record.stopObservationId} " +
                $"reason=service_reset deadlineUtcMs={record.deadline}");
            Logger.ReportFailure("SERVER", $"服务恢复验证被生命周期重置中断；recoveryId={record.id}；project={record.projectPath}；" +
                $"HTTP={record.spec?.httpPort.ToString() ?? "unknown"}；WS={record.spec?.wsPort.ToString() ?? "unknown"}；" +
                $"deadlineUtcMs={record.deadline}；reason=service_reset。{record.error}", scope: record.id, terminal: true);
        }

        private void BeforeExistingServiceRecoveryReload()
        {
            ClearRestoredRestartStopObservation();
            _existingServiceRecoveryResumePending = false;
            EditorApplication.update -= ResumeExistingServiceRecovery;
            _existingServiceRecoveryCancellation?.Cancel();
            _existingServiceRecoveryCancellation = null;
            ReleaseRecoveredServiceAttachment();
        }

        private bool TryApplyRecoveredProcessOwnership(ref McpServerStatus status)
        {
            lock (_statusLock)
            {
                if (!HasVerifiedRecoveredAttachment || !status.IdentityAvailable || !status.IdentityIdentifiesUPilot ||
                    status.IdentityServerProcessId != _recoveredServiceTarget.ProcessId ||
                    status.Identity?.server_instance_id != _recoveredServiceInstanceId ||
                    !UPilotDeploymentDiagnostics.SamePath(status.IdentityProjectPath, _recoveredServiceSpec.projectPath)) return false;
                status.ProcessId = _recoveredServiceTarget.ProcessId;
                status.IsRunning = true;
                status.ProcessOwnership = McpProcessOwnership.CurrentUPilot;
                status.ProcessOwnershipEvidence = "精确入口及端口参数的存活句柄，当前 health 实例与项目一致";
                return true;
            }
        }
    }
}
