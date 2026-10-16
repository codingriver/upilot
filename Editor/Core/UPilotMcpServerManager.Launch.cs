// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMcpServerManager
    {
        private const string LaunchSessionKey = "UPilot.ServerLaunchIdentity.v1";
        private ServerLaunchSpec _pendingLaunchSpec;
        private ServerLaunchState _launchState;
        private Process _launcherProcess;
        private CancellationTokenSource _launchObservationCancellation;
        private bool _launchProbeRunning;
        private bool _launchNeedsRevalidation;
        private bool _launchReloadRevalidation;
        private long _launchNextProbeAt;
        private long _pendingLaunchDeadline;
        private int _stoppingHttpPort, _stoppingWsPort;
        private bool _initialServiceRecoveryCompleted;
        private bool _initialServiceRecoveryHadPersistedState;
        private CancellationTokenSource _stoppedIdentityRecoveryCancellation;
        private Task _stoppedIdentityRecoveryTask;
        private string _restoredRestartResetReceiptId = "";

        internal bool InitialServiceRecoveryCompleted => _initialServiceRecoveryCompleted;
        internal Task StoppedIdentityRecovery => _stoppedIdentityRecoveryTask ?? Task.CompletedTask;
        internal bool CanAutomaticallyStartServer =>
            _initialServiceRecoveryCompleted && !_initialServiceRecoveryHadPersistedState &&
            !ExistingServiceRecoveryOwnsStartup && !StopBlocksAutomaticRecovery;
        internal bool CanAutomaticallyRepairDeployment => _initialServiceRecoveryCompleted && !StopBlocksAutomaticRecovery &&
            (!ExistingServiceRecoveryOwnsStartup || HasVerifiedRecoveredAttachment) &&
            (!_initialServiceRecoveryHadPersistedState || (PeriodicHealthChecksAllowed &&
             (HasVerifiedAttachment || string.IsNullOrEmpty(_launchState?.reloadRevalidation?.observationId) ||
              _launchState.reloadRevalidation.status == "succeeded")));

        // Called after Instance has been published, by the first Editor callback or an explicit action.
        // Every route restores once before a new Start/Repair can replace the persisted identity.
        internal void EnsureInitialServiceRecovery()
        {
            EditorApplication.update -= EnsureInitialServiceRecovery;
            EditorApplication.delayCall -= EnsureInitialServiceRecovery;
            if (_initialServiceRecoveryCompleted) return;
            if (BeginCurrentEditorRecoverySession())
            {
                // Disk history belongs to the former Unity process. It is evidence, not a task
                // to resume in this Editor; live discovery below does not need a former PID.
                if (UPilotServerRestartDiagnostics.TryGetActive(out var formerRestart))
                    UPilotServerRestartDiagnostics.RecordCanceled(formerRestart.operationId,
                        "Unity Editor 会话已变化；保留旧重启记录，新会话独立检查当前配置的遗留服务。");
                UPilotQuickStart.ClearPriorEditorRecoveryFailures();
            }
            RestoreExistingServiceRecovery();
            _initialServiceRecoveryHadPersistedState =
                !string.IsNullOrEmpty(SessionState.GetString(LaunchSessionKey, "")) ||
                UPilotServerRestartDiagnostics.TryGetActive(out _);
            try
            {
                if (RestoreStopObservation())
                {
                    _initialServiceRecoveryHadPersistedState = true;
                    RestoreRestartStopObservation();
                    return;
                }
                RestoreLaunchObservation();
                ResumePersistedRestartObservation();
            }
            finally { _initialServiceRecoveryCompleted = true; }
            NotifyStatusChanged();
        }

        // 描述只通过副本暴露；调用方不能改变本次操作的身份依据。
        internal ServerLaunchState LaunchIdentity => _launchState?.Snapshot();
        internal ServerLaunchSpec LaunchSpec => (_launchState?.spec ?? _recoveredServiceSpec)?.Snapshot();

        private ServerLaunchSpec PrepareLaunchSpec(long deadline)
        {
            var runtime = UPilotProjectConfig.Current.runtime;
            var project = UPilotProjectConfig.ProjectRoot;
            var entry = Path.IsPathRooted(_pythonEntryPath) ? _pythonEntryPath : Path.Combine(project, _pythonEntryPath);
            return ServerLaunchSpec.Create(UPilotServerRuntimeService.Instance.GetConfiguredMode(),
                runtime?.pythonPath, entry, runtime?.serverExePath, project,
                Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "upilotserver~", "src", "upilot_mcp"),
                HttpPort, WsPort, _logLevel, UPilotDeploymentDiagnostics.Channel,
                UPilotDeploymentDiagnostics.InstallSource, deadline);
        }

        private long LaunchDeadline()
        {
            if (IsRestartObservationActive()) return _restartVerificationDeadlineUtcMs;
            if (_pendingLaunchDeadline <= 0)
                _pendingLaunchDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                    Math.Max(30, Math.Min(600, UPilotProjectConfig.Current.aiServiceMaintenance?.restartTimeoutSeconds ?? 120)) * 1000L;
            return _pendingLaunchDeadline;
        }

        /// <summary>所有启动描述共用唯一服务启动入口。</summary>
        private void LaunchServer(ServerLaunchSpec spec, long deadline)
        {
            var shutdownOwner = CaptureServiceShutdown().OwnerId;
            ReleaseRecoveredServiceAttachment(resetPriorRecovery: true);
            spec = spec.Snapshot();
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                throw new TimeoutException("本次启动期限已到，未启动服务");
            Directory.CreateDirectory(Path.Combine(spec.projectPath, "log"));
            if (_restartMaintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
            var launchStarted = Stopwatch.GetTimestamp();
            Logger.Lifecycle("server_launch_dispatching", $"{CaptureServiceShutdown().Detail} httpPort={spec.httpPort} wsPort={spec.wsPort} remainingMs={Math.Max(0, deadline - StopNow)}");
            var lifecycleId = IsRestartObservationActive() ? _restartOperationId : Guid.NewGuid().ToString("N");
            SetLifecyclePhase(UPilotServerLifecyclePhase.Starting, 0, lifecycleId, deadline);
            var process = Process.Start(spec.StartInfo());
            if (process == null) throw new InvalidOperationException("服务启动未返回进程句柄");
            // 保留真实句柄，不能以后仅凭 PID 重新认领进程。
            var handle = process.Handle;
            StopLaunchObservation();
            _launcherProcess?.Dispose();
            DisposeTrackedProcess();
            _launcherProcess = process;
            _lifecycleProcessId = process.Id;
            _launchReloadRevalidation = false;
            _launchState = new ServerLaunchState
            {
                spec = spec, launcherPid = process.Id,
                launcherCreatedAt = process.StartTime.ToUniversalTime().Ticks, deadlineUtcMs = deadline,
            };
            AllowServiceReplacement(shutdownOwner);
            RecordExistingServiceReplacement(process.Id);
            _pendingLaunchDeadline = 0;
            _launchNeedsRevalidation = false;
            _trackedProcess = Process.GetProcessById(process.Id);
            _trackedProcessId = process.Id;
            _trackedProcessCreatedAtTicks = _launchState.launcherCreatedAt;
            PersistLaunchState();
            UPilotStartupDiagnostics.RecordServerProcessStarted(process.Id);
            if (IsRestartObservationActive())
            {
                UPilotServerRestartDiagnostics.RecordLaunchSpec(_restartOperationId, spec);
                UPilotServerRestartDiagnostics.RecordProcessStarted(_restartOperationId, process.Id, _launchState.launcherCreatedAt);
            }
            Logger.Lifecycle("server_launch_dispatched", $"{CaptureServiceShutdown().Detail} operationId={lifecycleId} launcherPid={process.Id} launcherCreatedAtTicks={_launchState.launcherCreatedAt} httpPort={spec.httpPort} wsPort={spec.wsPort} elapsedMs={Logger.LifecycleElapsedMs(launchStarted)} remainingMs={Math.Max(0, deadline - StopNow)} awaitingIdentityVerification=true");
            BeginLaunchObservation();
        }

        private void PersistLaunchState()
        {
            SessionState.SetString(LaunchSessionKey, _launchState == null ? "" : JsonUtility.ToJson(_launchState));
            ObserveExistingServiceReplacementOutcome();
        }

        private void BeforeLaunchDomainReload()
        {
            EditorApplication.update -= EnsureInitialServiceRecovery;
            EditorApplication.delayCall -= EnsureInitialServiceRecovery;
            // A second Reload can precede the first restore callback; preserve the saved identity.
            if (_initialServiceRecoveryCompleted && (_launchState != null ||
                (_stopObservation?.status != "failed" && _stopObservation?.status != "timed_out")))
                PersistLaunchState();
            _lifecycleQueryCancellation?.Cancel();
            _stoppedIdentityRecoveryCancellation?.Cancel();
            BeforeExistingServiceRecoveryReload();
            StopLaunchObservation();
            _launcherProcess?.Dispose();
            _launcherProcess = null;
            DisposeTrackedProcess();
        }

        private void BeginLaunchObservation()
        {
            StopLaunchObservation();
            _launchObservationCancellation = new CancellationTokenSource();
            _launchNextProbeAt = 0;
            EditorApplication.update += ObserveLaunch;
        }

        private void StopLaunchObservation()
        {
            EditorApplication.update -= ObserveLaunch;
            _launchObservationCancellation?.Cancel();
            _launchObservationCancellation?.Dispose();
            _launchObservationCancellation = null;
            _launchProbeRunning = false;
        }

        private void ObserveLaunch()
        {
            var state = _launchState;
            if (state == null || (state.verified && !_launchNeedsRevalidation && _lifecyclePhase == UPilotServerLifecyclePhase.None) || state.terminal) { StopLaunchObservation(); return; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_launchReloadRevalidation && state.reloadRevalidation != null &&
                (now >= state.reloadRevalidation.deadlineUtcMs || (!_launchProbeRunning && state.reloadRevalidation.attemptsUsed >= state.reloadRevalidation.maxAttempts)))
            {
                EndReloadRevalidation(state, state.reloadRevalidation.LimitReason(now));
                return;
            }
            // 重启使用原观察器及其验证门；普通/自动启动使用完全相同的身份解析器。
            if (IsRestartObservationActive() || _restartPending || _launchProbeRunning) return;
            if (IsLaunchVerificationExpired(state, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                EndLifecyclePhase(_lifecycleGeneration, "launch_identity_timeout: " + state.error, true);
                state.error = "launch_identity_timeout: " + state.error;
                state.terminal = true;
                PersistLaunchState();
                UPilotStartupDiagnostics.RecordBlockingReason("launch_identity_timeout", state.error);
                StopLaunchObservation();
                return;
            }
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _launchNextProbeAt) return;
            _launchProbeRunning = true;
            _ = ProbeLaunchAsync(state, _launchObservationCancellation.Token);
        }

        internal bool IsLaunchVerificationExpired(ServerLaunchState state, long nowUtcMs) =>
            nowUtcMs >= (_launchReloadRevalidation ? state.reloadRevalidation?.deadlineUtcMs ?? state.deadlineUtcMs : state.deadlineUtcMs);

        internal long LaunchProbeDeadline(ServerLaunchState state, long nowUtcMs) =>
            _launchReloadRevalidation ? state.reloadRevalidation?.RoundDeadline(nowUtcMs) ?? state.deadlineUtcMs : state.deadlineUtcMs;

        private void EndReloadRevalidation(ServerLaunchState state, string reason)
        {
            if (!ReferenceEquals(state, _launchState)) return;
            state.reloadRevalidation?.Finish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, reason);
            state.error = reason + ": " + state.error;
            state.terminal = true;
            _launchNeedsRevalidation = true;
            EndLifecyclePhase(_lifecycleGeneration, state.error, reason == "reload_revalidation_timeout");
            PersistLaunchState();
            StopLaunchObservation();
        }

        private async Task ProbeLaunchAsync(ServerLaunchState state, CancellationToken token)
        {
            try
            {
                // 生命周期延后不消耗轮数；观察总期限仍保持原值。
                if (!await PrepareLifecycleProbeAsync(token)) return;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_launchReloadRevalidation && !state.reloadRevalidation.TryBeginRound(now)) return;
                PersistLaunchState();
                var probeDeadline = LaunchProbeDeadline(state, now);
                using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                roundCancellation.CancelAfter((int)Math.Max(1, Math.Min(int.MaxValue, probeDeadline - now)));
                var roundToken = roundCancellation.Token;
                var lifecycleGeneration = _lifecycleGeneration;
                var status = await GetRestartStatusAsync(probeDeadline, roundToken, state.spec, _launchReloadRevalidation);
                if (token.IsCancellationRequested || lifecycleGeneration != _lifecycleGeneration || !ReferenceEquals(state, _launchState)) return;
                if (!TryResolveStartedService(state, status, out var error))
                {
                    state.error = error;
                    if (error == "process_exited" || status.ProcessOwnership == McpProcessOwnership.Foreign)
                        state.terminal = true;
                    PersistLaunchState();
                    if (state.terminal)
                    {
                        if (_launchReloadRevalidation) EndReloadRevalidation(state, state.error);
                        else EndLifecyclePhase(lifecycleGeneration, state.error);
                    }
                }
                else
                {
                    _launchNeedsRevalidation = true;
                    var bridge = UPilotBridge.Instance.GetStatus();
                    if (!bridge.IsWsOpen || !bridge.IsAuthenticated ||
                        UPilotDeploymentDiagnostics.Observe(bridge, status, state.spec).Length > 0) return;
                    await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, probeDeadline, roundToken, _launchReloadRevalidation);
                    var after = UPilotBridge.Instance.GetStatus();
                    if (token.IsCancellationRequested || lifecycleGeneration != _lifecycleGeneration ||
                        !ReferenceEquals(state, _launchState) || !after.IsWsOpen || !after.IsAuthenticated ||
                        after.SessionId != bridge.SessionId) return;
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= probeDeadline) return;
                    state.readyVerified = true;
                    state.reloadRevalidation?.Finish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, "verified");
                    PersistLaunchState();
                    _launchNeedsRevalidation = false;
                    if (EndLifecyclePhase(lifecycleGeneration))
                        CacheVerifiedStartupStatus(status);
                    StopLaunchObservation();
                }
            }
            catch (LifecycleProbeDeferredException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(state, _launchState))
                {
                    state.error = DescribeProbeException(ex);
                    PersistLaunchState();
                }
            }
            finally
            {
                if (!token.IsCancellationRequested && ReferenceEquals(state, _launchState))
                {
                    _launchProbeRunning = false;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _launchNextProbeAt = now + (_launchReloadRevalidation ? state.reloadRevalidation.intervalSeconds * 1000L : RestartProbeIntervalMs);
                    if (_launchReloadRevalidation)
                    {
                        state.reloadRevalidation.RoundEnded(now, state.error);
                        PersistLaunchState();
                        if (state.reloadRevalidation.status == "observing" && state.reloadRevalidation.LimitReason(now).Length != 0)
                            EndReloadRevalidation(state, state.reloadRevalidation.LimitReason(now));
                    }
                }
            }
        }

        /// <summary>单进程和直接子进程的共同认领路径，不接受运行模式专用策略。</summary>
        private bool TryResolveStartedService(ServerLaunchState state, McpServerStatus status, out string error, bool stopping = false)
        {
            error = "launch_identity_missing";
            Process candidate = null;
            try
            {
                if (state == null || !ReferenceEquals(state, _launchState) || (!stopping && !state.CanObserve(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))) return false;
                if (_launcherProcess == null || _launcherProcess.HasExited) { error = "process_exited"; return false; }
                if (status.ProcessId.GetValueOrDefault() <= 0) { error = "service_ownership_unverified"; return false; }
                candidate = Process.GetProcessById(status.ProcessId.Value);
                var handle = candidate.Handle;
                var launcher = ServerProcessIdentity.Read(_launcherProcess);
                var service = ServerProcessIdentity.Read(candidate);
                error = ServerProcessIdentity.Verify(state.spec, state.launcherPid, state.launcherCreatedAt, launcher, service, status);
                if (error.Length != 0) return false;
                // 采集期间 PID 可能被复用；在提交前复核两个保留句柄和创建时间。
                _launcherProcess.Refresh();
                candidate.Refresh();
                var launchAfter = ServerProcessIdentity.Read(_launcherProcess);
                var serviceAfter = ServerProcessIdentity.Read(candidate);
                error = ServerProcessIdentity.VerifyUnchanged(launcher, launchAfter, service, serviceAfter);
                if (error.Length != 0) return false;
                if (state.servicePid > 0 && state.serviceCreatedAt > 0 &&
                    (service.pid != state.servicePid || service.createdAt != state.serviceCreatedAt))
                { error = "service_identity_changed_or_expired"; return false; }
                var wasVerified = state.verified;
                if (!state.TryConfirm(service.pid, service.createdAt, status.Health.server_instance_id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stopping)) { error = "service_identity_changed_or_expired"; return false; }
                DisposeTrackedProcess();
                _trackedProcess = candidate;
                _trackedProcessId = candidate.Id;
                _trackedProcessCreatedAtTicks = service.createdAt;
                candidate = null;
                _launchNeedsRevalidation = false;
                if (!wasVerified)
                    Logger.Lifecycle("server_launch_identity_verified", $"{CaptureServiceShutdown().Detail} serverPid={service.pid} serverCreatedAtTicks={service.createdAt} serverInstanceId={state.instanceId} launcherPid={state.launcherPid} startGeneration={_startAttemptGeneration} remainingMs={Math.Max(0, state.deadlineUtcMs - StopNow)}");
                PersistLaunchState();
                if (!stopping && IsRestartObservationActive())
                    UPilotServerRestartDiagnostics.RecordServiceIdentity(_restartOperationId, state);
                return true;
            }
            catch (ArgumentException) { error = "process_exited"; return false; }
            catch (Exception ex) { error = "process_evidence_unavailable: " + ex.GetType().Name; return false; }
            finally { candidate?.Dispose(); }
        }

        private bool TryTrackExistingProcess(McpServerStatus status, ServerLaunchSpec spec, long deadline, out string error)
        {
            // Reload 优先恢复本 Editor 的冻结身份。首次附着已有服务时只采集精确 OS 证据，绝不启动。
            error = "service_ownership_unverified";
            if (_launchState != null)
                return TryResolveStartedService(_launchState, status, out error);
            if (!IsVerifiedStartupHealth(status)) return false;
            Process launcher = null;
            try
            {
                using var service = Process.GetProcessById(status.ProcessId.Value);
                var identity = ServerProcessIdentity.Read(service);
                var launcherId = UPilotDeploymentDiagnostics.SamePath(identity.executable, spec.executablePath)
                    ? identity.pid : identity.parentPid;
                // 同路径的子进程（如 one-file）也保留其精确直接父进程，便于退出观察。
                if (identity.parentPid > 0)
                {
                    try
                    {
                        using var parent = Process.GetProcessById(identity.parentPid);
                        if (UPilotDeploymentDiagnostics.SamePath(parent.MainModule.FileName, spec.executablePath) &&
                            IsCurrentProjectMcpCommandLine(GetProcessCommandLineForDiagnostics(parent.Id), spec.httpPort, spec.wsPort))
                            launcherId = parent.Id;
                    }
                    catch { /* 父身份未知不作为子进程依据。 */ }
                }
                launcher = Process.GetProcessById(launcherId);
                var handle = launcher.Handle;
                var state = new ServerLaunchState { spec = spec.Snapshot(), launcherPid = launcher.Id,
                    launcherCreatedAt = launcher.StartTime.ToUniversalTime().Ticks, deadlineUtcMs = deadline };
                _launcherProcess = launcher;
                _launchState = state;
                if (TryResolveStartedService(state, status, out error)) { launcher = null; return true; }
                _launchState = null;
                _launcherProcess = null;
                return false;
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex)
            {
                error = "process_evidence_unavailable: " + DescribeProbeException(ex);
                _launchState = null; _launcherProcess = null; return false;
            }
            finally { launcher?.Dispose(); }
        }

        internal static bool HasCompletedLaunchVerification(ServerLaunchState state, UPilotServerRestartRecord record) =>
            state.verified && (state.readyVerified ||
                (record?.status == "succeeded" && record.readOnlyVerified &&
                 record.launcherProcessId == state.launcherPid && record.launcherProcessCreatedAtTicks == state.launcherCreatedAt &&
                 record.newProcessId == state.servicePid && record.newProcessCreatedAtTicks == state.serviceCreatedAt));

        private void RestoreLaunchObservation()
        {
            _restoredRestartResetReceiptId = "";
            if (UPilotServerRestartDiagnostics.TryGetActive(out var record))
            {
                var owner = record.resetReceipt?.ownerId;
                if (string.IsNullOrEmpty(owner)) owner = record.operationId;
                var current = CaptureServiceShutdown();
                // Initial recovery must not displace a different live owner.
                if ((current.Stopping || current.ReplacementEligible) && current.OwnerId != owner &&
                    !current.OwnerId.StartsWith("bridge:", StringComparison.Ordinal)) return;
                _restartShutdownOwnerId = owner;
                BeginServiceShutdown(owner, "restart_reload_receipt_verifying", UPilotServerRestartDiagnostics.Deadline(record));
            }
            RestoreLaunchState(observe: true);
        }

        // Restores only the original reset receipt after frozen launch handles were recovered.
        // No queue, stop, start or cleanup is executed here.
        internal bool RestoreRestartResetReceipt(UPilotServerRestartRecord record,
            ServiceMaintenanceRecord maintenance, out string reason)
        {
            var receipt = record?.resetReceipt;
            var state = _launchState;
            using var editor = Process.GetCurrentProcess();
            var deadline = record == null ? 0 : UPilotServerRestartDiagnostics.Deadline(record);
            reason = "restart_reset_receipt_context_mismatch";
            if (record?.status != "running" || string.IsNullOrEmpty(record.operationId) || receipt?.performed != true ||
                receipt.operationId != record.operationId || receipt.restartGeneration <= 0 ||
                receipt.restartGeneration != record.restartGeneration || receipt.unityProcessId != editor.Id ||
                string.IsNullOrEmpty(receipt.serviceLifetime) || receipt.serviceLifetime != UPilotServiceLifetime.Id ||
                !SameProjectPath(receipt.projectPath, record.projectPath) ||
                !SameProjectPath(record.projectPath, UPilotProjectConfig.ProjectRoot) ||
                (receipt.oldBridgeSessionId ?? "") != (record.oldBridgeSessionId ?? "") ||
                receipt.deadlineUtcMs != deadline || deadline <= StopNow) return false;
            if (record.maintenanceDeadlineUtcMs > 0)
            {
                if (maintenance?.status != "running" || maintenance.maintenanceId != receipt.ownerId ||
                    maintenance.serverRestartId != record.operationId || maintenance.unityProcessId != editor.Id ||
                    maintenance.deadlineAtUtcMs != deadline || maintenance.acceptedAtUtcMs != record.requestedAtUtcMs ||
                    !SameProjectPath(maintenance.projectPath, record.projectPath) ||
                    (maintenance.oldBridgeSessionId ?? "") != (record.oldBridgeSessionId ?? "")) return false;
            }
            else if (receipt.ownerId != record.operationId || maintenance?.Active == true) return false;
            reason = "restart_reset_receipt_launch_mismatch";
            if (state?.spec == null || state.terminal || state.spec.Validate().Length != 0 || record.launchSpec == null ||
                !SameProjectPath(state.spec.projectPath, record.projectPath) ||
                JsonUtility.ToJson(state.spec) != JsonUtility.ToJson(record.launchSpec) ||
                state.launcherPid <= 0 || state.launcherCreatedAt <= 0 ||
                state.launcherPid != record.launcherProcessId || state.launcherCreatedAt != record.launcherProcessCreatedAtTicks ||
                state.deadlineUtcMs != deadline || _launcherProcess == null || _trackedProcess == null ||
                (state.verified ? record.newProcessId != state.servicePid || record.newProcessCreatedAtTicks != state.serviceCreatedAt :
                    record.newProcessId != state.launcherPid || record.newProcessCreatedAtTicks != state.launcherCreatedAt)) return false;
            var shutdown = CaptureServiceShutdown();
            if (shutdown.OwnerId != receipt.ownerId ||
                !RestoreServiceWorkResetReceipt(shutdown, receipt.performed, receipt.error, deadline, out reason)) return false;
            // Keep persisted verified identity values for the next full OS/health comparison.
            _launchNeedsRevalidation = true;
            AllowServiceReplacement(receipt.ownerId);
            _restartShutdownOwnerId = receipt.ownerId;
            _restoredRestartResetReceiptId = record.operationId;
            reason = "restored";
            return true;
        }

        internal static bool TryCreateStoppedIdentityRecoveryCandidate(UPilotServerRestartRecord record,
            ServiceStopObservationRecord stop, out ServerLaunchState candidate)
        {
            candidate = null;
            if (stop == null || (stop.status != "failed" && stop.status != "timed_out") ||
                !stop.MatchesScope(UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, Process.GetCurrentProcess().Id) ||
                record?.status != "succeeded" || !record.healthVerified || !record.projectIdentityVerified ||
                !record.bridgeVerified || !record.deploymentVerified || !record.readOnlyVerified ||
                record.launcherProcessId <= 0 || record.launcherProcessCreatedAtTicks <= 0 ||
                record.newProcessId <= 0 || record.newProcessCreatedAtTicks <= 0 || record.healthProcessId != record.newProcessId ||
                record.launchSpec == null || record.launchSpec.Validate().Length != 0 ||
                !UPilotDeploymentDiagnostics.SamePath(record.projectPath, stop.project) ||
                !UPilotDeploymentDiagnostics.SamePath(record.healthProjectPath, stop.project) ||
                !UPilotDeploymentDiagnostics.SamePath(record.launchSpec.projectPath, stop.project) ||
                record.launchSpec.httpPort != stop.httpPort || record.launchSpec.wsPort != stop.wsPort ||
                !record.launchSpec.TryGetLogFilePath(out _) ||
                !UPilotDeploymentDiagnostics.SamePath(record.actualEntry, record.launchSpec.expectedEntry) ||
                (!string.IsNullOrEmpty(record.launchSpec.expectedModuleRoot) &&
                 !UPilotDeploymentDiagnostics.SamePath(record.actualModule, record.launchSpec.expectedModuleRoot))) return false;
            candidate = new ServerLaunchState { spec = record.launchSpec.Snapshot(), launcherPid = record.launcherProcessId,
                launcherCreatedAt = record.launcherProcessCreatedAtTicks, servicePid = record.newProcessId,
                serviceCreatedAt = record.newProcessCreatedAtTicks,
                deadlineUtcMs = record.deadlineAtUtcMs > 0 ? record.deadlineAtUtcMs : record.maintenanceDeadlineUtcMs };
            return true; // Historical evidence selects a candidate; only fresh OS/health can verify it.
        }

        private async Task RestoreStoppedIdentityFromHistoryAsync(ServerLaunchState candidate,
            ServiceStopObservationRecord stop, int startGeneration, long restartGeneration,
            CancellationTokenSource cancellation)
        {
            bool Current() => ReferenceEquals(_stopObservation, stop) && _launchState == null &&
                _startAttemptGeneration == startGeneration && _restartGeneration == restartGeneration &&
                stop.MatchesScope(UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, Process.GetCurrentProcess().Id);
            Process launcher = null;
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000;
            try
            {
                cancellation.CancelAfter(8000);
                var status = await GetRestartStatusAsync(deadline,
                    cancellation.Token, candidate.spec);
                if (!Current() || cancellation.IsCancellationRequested) return;
                if (status.ProcessId != candidate.servicePid)
                    throw new InvalidOperationException("停止失败后的历史服务候选与当前端口或 health PID 不符");
                launcher = Process.GetProcessById(candidate.launcherPid);
                var launcherHandle = launcher.Handle;
                if (launcher.HasExited || launcher.StartTime.ToUniversalTime().Ticks != candidate.launcherCreatedAt ||
                    !UPilotDeploymentDiagnostics.SamePath(launcher.MainModule.FileName, candidate.spec.executablePath))
                    throw new InvalidOperationException("停止失败后的历史启动器候选已退出或身份变化");
                using var service = Process.GetProcessById(candidate.servicePid);
                var serviceHandle = service.Handle;
                var expectedCreatedAt = candidate.serviceCreatedAt;
                if (service.HasExited || service.StartTime.ToUniversalTime().Ticks != expectedCreatedAt)
                    throw new InvalidOperationException("停止失败后的历史服务候选已退出或创建时间变化");
                _launcherProcess = launcher;
                _launchState = candidate;
                if (!TryResolveStartedService(candidate, status, out var error, stopping: true) ||
                    candidate.serviceCreatedAt != expectedCreatedAt)
                    throw new InvalidOperationException("停止失败后的历史候选未通过完整实时身份核验：" + error);
                launcher = null;
                _launchNeedsRevalidation = true;
                NotifyStatusChanged();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested &&
                (!Current() || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)) { }
            catch (Exception ex)
            {
                if (ReferenceEquals(_launchState, candidate))
                {
                    _launchState = null;
                    _launcherProcess = null;
                    DisposeTrackedProcess();
                    _trackedProcessId = null;
                }
                if (ReferenceEquals(_stopObservation, stop) && _startAttemptGeneration == startGeneration &&
                    _restartGeneration == restartGeneration)
                    Logger.ReportFailure("SERVER", "停止失败后的历史候选实时核验失败；未重放停启，也未修改历史结果。", ex,
                        scope: stop.id, terminal: true);
            }
            finally
            {
                launcher?.Dispose();
                if (ReferenceEquals(_stoppedIdentityRecoveryCancellation, cancellation)) _stoppedIdentityRecoveryCancellation = null;
                cancellation.Dispose();
            }
        }

        private void RestoreLaunchState(bool observe)
        {
            var json = SessionState.GetString(LaunchSessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var state = JsonUtility.FromJson<ServerLaunchState>(json);
                if (state?.spec == null || state.spec.Validate().Length != 0 ||
                    !UPilotDeploymentDiagnostics.SamePath(state.spec.projectPath, UPilotProjectConfig.ProjectRoot)) return;
                if (!observe && !TryGetVerifiedOwnershipLogPath(state, UPilotProjectConfig.ProjectRoot,
                    _stopObservation.httpPort, _stopObservation.wsPort, out _)) return;
                _launchState = state;
                // 不更新期限，不重放启动，不恢复任何业务任务。
                _launchNeedsRevalidation = state.verified;
                _launchReloadRevalidation = observe && HasCompletedLaunchVerification(state, UPilotServerRestartDiagnostics.Current);
                if (_launchReloadRevalidation && !state.terminal)
                {
                    // Unity's inline class serialization can materialize null as an empty object.
                    state.reloadRevalidation = ReloadRevalidationObservation.ForReload(state.reloadRevalidation,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (state.reloadRevalidation.status != "observing") state.terminal = true;
                    PersistLaunchState();
                }
                _launcherProcess = Process.GetProcessById(state.launcherPid);
                var handle = _launcherProcess.Handle;
                if (_launcherProcess.HasExited || _launcherProcess.StartTime.ToUniversalTime().Ticks != state.launcherCreatedAt)
                    throw new InvalidOperationException("启动进程在 Reload 后身份不符");
                if (!observe && !UPilotDeploymentDiagnostics.SamePath(_launcherProcess.MainModule.FileName, state.spec.executablePath))
                    throw new InvalidOperationException("启动进程可执行文件在 Reload 后身份不符");
                _trackedProcessId = state.verified ? state.servicePid : state.launcherPid;
                _trackedProcessCreatedAtTicks = state.verified ? state.serviceCreatedAt : state.launcherCreatedAt;
                _trackedProcess = Process.GetProcessById(_trackedProcessId.Value);
                handle = _trackedProcess.Handle;
                if (_trackedProcess.HasExited || _trackedProcess.StartTime.ToUniversalTime().Ticks != _trackedProcessCreatedAtTicks)
                    throw new InvalidOperationException("服务进程在 Reload 后身份不符");
                if (!observe && !Array.Exists(state.spec.serviceExecutablePaths,
                    path => UPilotDeploymentDiagnostics.SamePath(path, _trackedProcess.MainModule.FileName)))
                    throw new InvalidOperationException("服务进程可执行文件在 Reload 后身份不符");
                if (observe)
                {
                    if (UPilotServerRestartDiagnostics.TryGetActive(out var restart))
                    {
                        if (!RestoreRestartResetReceipt(restart, UPilotServiceMaintenance.Current, out var receiptError))
                            throw new InvalidOperationException("Reload 后原重启 reset receipt 无法核实；未重放清理或停启：" + receiptError);
                    }
                    else if (ExistingServiceReplacementNeedsReceiptRestore &&
                        !RestoreExistingServiceReplacementResetReceipt(out var recoveryReceiptError))
                        throw new InvalidOperationException("Reload 后原遗留服务恢复 reset receipt 无法核实；未重放清理或停启：" + recoveryReceiptError);
                }
                if (observe && !state.terminal)
                    SetLifecyclePhase(state.verified ? UPilotServerLifecyclePhase.Verifying : UPilotServerLifecyclePhase.Starting,
                        state.launcherPid, IsRestartObservationActive() ? _restartOperationId : _launchReloadRevalidation ? state.reloadRevalidation.observationId : Guid.NewGuid().ToString("N"),
                        _launchReloadRevalidation ? state.reloadRevalidation.deadlineUtcMs : state.deadlineUtcMs);
                if (observe && !state.terminal)
                {
                    BeginLaunchObservation();
                    if (_launchReloadRevalidation) _launchNextProbeAt = state.reloadRevalidation.nextProbeAtUtcMs;
                }
            }
            catch (Exception ex)
            {
                StopLaunchObservation();
                if (!observe)
                {
                    _launchState = null;
                    Logger.ReportFailure("SERVER", "停止失败后恢复冻结启动身份失败；未恢复进程归属或重放停启。", ex,
                        scope: _stopObservation.id, terminal: true);
                }
                else if (_launchState != null)
                {
                    _launchState.error = "reload_identity_unverified: " + ex.Message;
                    _launchState.terminal = true;
                    _launchNeedsRevalidation = true;
                    _launchState.reloadRevalidation?.Finish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        false, "reload_identity_unverified");
                    EndLifecyclePhase(_lifecycleGeneration, _launchState.error);
                }
                _launcherProcess?.Dispose(); _launcherProcess = null;
                DisposeTrackedProcess(); _trackedProcessId = null;
                if (observe) PersistLaunchState();
            }
        }

        private void ReleaseLaunchAfterStop()
        {
            _launchState?.reloadRevalidation?.Finish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, "service_stopped");
            StopLaunchObservation();
            // The current entry/ports discovery owns every stop handle, including launchers.
            _launcherProcess?.Dispose();
            _launcherProcess = null;
            _launchState = null;
            PersistLaunchState();
        }
    }
}
