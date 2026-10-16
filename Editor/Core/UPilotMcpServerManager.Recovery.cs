// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        }

        // Isolated acceptance supplies a real process launcher without touching the global Bridge.
        internal Action<ExistingServiceRecoveryRecord> ExistingServiceReplacementStartOverride;
        private ExistingServiceRecoveryRecord _existingServiceRecovery;
        private CancellationTokenSource _existingServiceRecoveryCancellation;
        private ServerStopTarget _recoveredServiceTarget;
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
            NotifyStatusChanged();
            return true;
        }

        private void ClearRestoredRestartStopObservation()
        {
            EditorApplication.update -= ObserveRestoredRestartStop;
            _restoredRestartStop = null;
        }

        internal bool ExistingServiceRecoveryOwnsStartup => _existingServiceRecovery != null;
        internal string ExistingServiceRecoveryFailure => _existingServiceRecovery?.status == "failed"
            ? _existingServiceRecovery.error : "";
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
            long deadline, CancellationToken token)
        {
            var discovery = Task.Run(() => DiscoverStopTargets(scope, Array.Empty<int>(), deadline));
            var remaining = Math.Max(0, deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (await Task.WhenAny(discovery, Task.Delay((int)Math.Min(int.MaxValue, remaining), token)) == discovery)
                return await discovery;
            // OS inspection may complete after cancellation. Its handles must never escape.
            _ = discovery.ContinueWith(t => {
                if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
                else { var ignored = t.Exception; }
            }, TaskScheduler.Default);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("遗留服务进程发现超过本次复用验证期限。");
        }

        internal static bool CanReuseExistingService(McpServerStatus status, string projectPath, int processId,
            bool alive, bool ownsHttp, bool ownsWs) => alive && ownsHttp && ownsWs && processId > 0 &&
            status.HealthEndpointResponded && status.HealthIdentifiesUPilot && status.HealthServerProcessId == processId &&
            !string.IsNullOrEmpty(status.Health?.server_instance_id) &&
            UPilotDeploymentDiagnostics.SamePath(status.HealthProjectPath, projectPath);

        private async Task RecoverExistingServiceAsync(ExistingServiceRecoveryRecord record, ServerStopScope scope,
            int startGeneration, long restartGeneration, CancellationTokenSource cancellation, bool revalidateOnly = false)
        {
            var token = cancellation.Token;
            var lifecycle = SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, 0, record.id, record.reuseDeadline);
            Exception validationError = null;
            try
            {
                using (var discovery = await DiscoverExistingServiceAsync(scope, record.reuseDeadline, token))
                using (var client = CreateLoopbackStatusClient())
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    while (OwnsExistingServiceRecovery(record, cancellation) && startGeneration == _startAttemptGeneration &&
                           restartGeneration == _restartGeneration && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < record.reuseDeadline)
                    {
                        try
                        {
                            var status = await GetRestartHealthAsync(client, scope.HttpPort, record.reuseDeadline,
                                token, this, 1000);
                            token.ThrowIfCancellationRequested();
                            var target = discovery.Targets.FirstOrDefault(p => p.ProcessId == status.HealthServerProcessId);
                            var ownsHttp = discovery.ListeningPorts.TryGetValue(status.HealthServerProcessId, out var ports) &&
                                ports.Contains(scope.HttpPort);
                            var ownsWs = ports != null && ports.Contains(scope.WsPort);
                            if (target != null && CanReuseExistingService(status, scope.ProjectPath, target.ProcessId,
                                !target.HasExited, ownsHttp, ownsWs) && discovery.UnmatchedPortOwners.Length == 0)
                            {
                                status.ProcessId = target.ProcessId;
                                status.IsRunning = status.HttpPortListening = status.WsPortListening = true;
                                status.ProcessOwnership = McpProcessOwnership.CurrentUPilot;
                                status.ProcessOwnershipEvidence = "当前精确入口及双端口参数、活体句柄与 health PID 已核实";
                                var bridge = UPilotBridge.Instance.GetStatus();
                                if (bridge.IsWsOpen && bridge.IsAuthenticated && !string.IsNullOrEmpty(bridge.SessionId) &&
                                    status.Health?.bridge_session_id == bridge.SessionId &&
                                    UPilotDeploymentDiagnostics.Observe(bridge, status, record.spec).Length == 0)
                                {
                                    await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, record.reuseDeadline, token);
                                    var after = UPilotBridge.Instance.GetStatus();
                                    if (OwnsExistingServiceRecovery(record, cancellation) && startGeneration == _startAttemptGeneration &&
                                        restartGeneration == _restartGeneration && !target.HasExited &&
                                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < record.reuseDeadline &&
                                        after.IsWsOpen && after.IsAuthenticated && after.SessionId == bridge.SessionId)
                                    {
                                        discovery.Targets.Remove(target);
                                        lock (_statusLock)
                                        {
                                            _recoveredServiceTarget = target;
                                            _recoveredServiceSpec = record.spec.Snapshot();
                                            _recoveredServiceInstanceId = status.Health.server_instance_id;
                                        }
                                        record.status = "reused"; record.error = "";
                                        SaveExistingServiceRecovery();
                                        ResetStopObservation("new_service_start_or_restart");
                                        if (EndLifecyclePhase(lifecycle)) CacheVerifiedStartupStatus(status);
                                        Logger.CloseDiagnostics(record.id);
                                        UnityEngine.Debug.Log($"[UPilotMcpServerManager] 已复用遗留服务；PID={target.ProcessId}；HTTP={scope.HttpPort}；WS={scope.WsPort}；recoveryId={record.id}；当前 Bridge 及实际只读调用已验证。");
                                        FinishExistingServiceRecoveryWork(record, cancellation);
                                        return;
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { validationError = ex; }
                        var remaining = record.reuseDeadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (remaining > 0) await Task.Delay((int)Math.Min(250, remaining), token);
                    }
                }
                validationError = new TimeoutException("遗留服务未在最多8秒内通过当前入口、项目、Bridge 握手及实际只读验证。", validationError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { validationError = ex; }

            try
            {
                if (!OwnsExistingServiceRecovery(record, cancellation) || startGeneration != _startAttemptGeneration ||
                    restartGeneration != _restartGeneration) return;
                if (record.status == "reused") return;
                if (revalidateOnly) throw validationError ?? new InvalidOperationException("重载后的遗留服务附着未通过验证。");
                Logger.ReportWarning("SERVER", $"遗留服务复用验证失败，将在原期限内清理并启动一次；recoveryId={record.id}；" + validationError,
                    record.id, "existing-service-reuse");
                record.status = "cleaning";
                SaveExistingServiceRecovery();
                var stopTask = StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, record.id,
                    parentDeadlineUtcMs: record.deadline, scope: scope);
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
            if (!stopped.StopConfirmed || !stopped.PortsAvailable || !stopped.ObservationSucceeded || !IsStopResultCurrent(stopped))
                throw new InvalidOperationException("遗留服务清理没有取得进程退出、双端口释放和当前操作身份的完整证据；未启动新服务。");
            if (record.replacementStarted) throw new InvalidOperationException("本次遗留服务恢复已派发一次启动，不重复派发。");
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= record.deadline)
                throw new TimeoutException("遗留服务清理后原恢复期限已到，未启动新服务。");
            record.replacementStarted = true; record.status = "replacement_starting";
            SaveExistingServiceRecovery(); // Persist intent before the single Process.Start.
            if (ExistingServiceReplacementStartOverride != null)
                ExistingServiceReplacementStartOverride(record);
            else
            {
                _pendingLaunchSpec = PrepareLaunchSpec(record.deadline);
                _pendingLaunchDeadline = record.deadline;
                _startInProgress = false;
                UPilotBridge.Instance.EnsureStarted();
                StartNewServer();
            }
            if (record.replacementPid <= 0)
                throw new InvalidOperationException("遗留服务已清理，但本次新服务启动未返回进程；不会循环重启。");
            Logger.CloseDiagnostics(record.id);
        }

        private void FailExistingServiceRecovery(ExistingServiceRecoveryRecord record, Exception exception)
        {
            record.status = "failed"; record.error = exception.Message;
            SaveExistingServiceRecovery();
            if (_lifecycleOperationId == record.id)
                EndLifecyclePhase(_lifecycleGeneration, exception.Message, exception is TimeoutException,
                    emitDiagnostic: false, failureException: exception);
            Logger.ReportFailure("SERVER", $"遗留服务恢复结束；recoveryId={record.id}；HTTP={record.spec.httpPort}；WS={record.spec.wsPort}。",
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
                if (record?.spec == null || !UPilotDeploymentDiagnostics.SamePath(record.projectPath, UPilotProjectConfig.ProjectRoot)) return;
                _existingServiceRecovery = record;
                if (record.status == "replacement_starting" || (record.status == "replacement_started" &&
                    string.IsNullOrEmpty(SessionState.GetString(LaunchSessionKey, ""))))
                {
                    FailExistingServiceRecovery(record, new InvalidOperationException(
                        "Reload 中断了本次唯一新服务启动，缺少完整启动证据；不重放启动，请检查后手动启动。"));
                    return;
                }
                if (record.status == "verifying" || record.status == "cleaning" || record.status == "reused")
                    EditorApplication.delayCall += ResumeExistingServiceRecovery;
            }
            catch (Exception ex) { Logger.ReportFailure("SERVER", "恢复遗留服务观察记录失败；没有重放停止或启动。", ex, terminal: true); }
        }

        private async void ResumeExistingServiceRecovery()
        {
            var record = _existingServiceRecovery;
            if (record == null || _existingServiceRecoveryCancellation != null || UPilotQuickStart.IsExplicitlyStopped) return;
            var cancellation = new CancellationTokenSource();
            _existingServiceRecoveryCancellation = cancellation;
            try
            {
                if (record.automatic && !AutoStartEnabled) return;
                if (record.status == "cleaning")
                {
                    if (_stopObservation?.id != record.stopObservationId)
                        throw new InvalidOperationException("Reload 后原遗留服务清理观察缺失，不重放停止或启动。");
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
            EditorApplication.delayCall -= ResumeExistingServiceRecovery;
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

        private void BeforeExistingServiceRecoveryReload()
        {
            ClearRestoredRestartStopObservation();
            EditorApplication.delayCall -= ResumeExistingServiceRecovery;
            _existingServiceRecoveryCancellation?.Cancel();
            _existingServiceRecoveryCancellation = null;
            ReleaseRecoveredServiceAttachment();
        }

        private bool TryApplyRecoveredProcessOwnership(ref McpServerStatus status)
        {
            lock (_statusLock)
            {
                if (!HasVerifiedRecoveredAttachment || !status.HealthEndpointResponded || !status.HealthIdentifiesUPilot ||
                    status.HealthServerProcessId != _recoveredServiceTarget.ProcessId ||
                    status.Health?.server_instance_id != _recoveredServiceInstanceId ||
                    !UPilotDeploymentDiagnostics.SamePath(status.HealthProjectPath, _recoveredServiceSpec.projectPath)) return false;
                status.ProcessId = _recoveredServiceTarget.ProcessId;
                status.IsRunning = true;
                status.ProcessOwnership = McpProcessOwnership.CurrentUPilot;
                status.ProcessOwnershipEvidence = "精确入口及端口参数的存活句柄，当前 health 实例与项目一致";
                return true;
            }
        }
    }
}
