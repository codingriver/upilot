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

        internal bool InitialServiceRecoveryCompleted => _initialServiceRecoveryCompleted;
        internal bool CanAutomaticallyStartServer =>
            _initialServiceRecoveryCompleted && !_initialServiceRecoveryHadPersistedState;
        internal bool CanAutomaticallyRepairDeployment => _initialServiceRecoveryCompleted &&
            (!_initialServiceRecoveryHadPersistedState || (PeriodicHealthChecksAllowed &&
             (string.IsNullOrEmpty(_launchState?.reloadRevalidation?.observationId) ||
              _launchState.reloadRevalidation.status == "succeeded")));

        // Called after Instance has been published, by the first Editor callback or an explicit action.
        // Every route restores once before a new Start/Repair can replace the persisted identity.
        internal void EnsureInitialServiceRecovery()
        {
            EditorApplication.update -= EnsureInitialServiceRecovery;
            EditorApplication.delayCall -= EnsureInitialServiceRecovery;
            if (_initialServiceRecoveryCompleted) return;
            _initialServiceRecoveryHadPersistedState =
                !string.IsNullOrEmpty(SessionState.GetString(LaunchSessionKey, "")) ||
                UPilotServerRestartDiagnostics.TryGetActive(out _);
            try
            {
                RestoreLaunchObservation();
                ResumePersistedRestartObservation();
            }
            finally { _initialServiceRecoveryCompleted = true; }
            NotifyStatusChanged();
        }

        // 描述只通过副本暴露；调用方不能改变本次操作的身份依据。
        internal ServerLaunchState LaunchIdentity => _launchState?.Snapshot();
        internal ServerLaunchSpec LaunchSpec => _launchState?.spec?.Snapshot();

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
            spec = spec.Snapshot();
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                throw new TimeoutException("本次启动期限已到，未启动服务");
            Directory.CreateDirectory(Path.Combine(spec.projectPath, "log"));
            if (_restartMaintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
            UnityEngine.Debug.Log($"[UPilotMcpServerManager] 正在启动 MCP Server；入口={spec.expectedEntry}；HTTP={spec.httpPort}；WS={spec.wsPort}。");
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
            UnityEngine.Debug.Log($"[UPilotMcpServerManager] 服务启动 PID={process.Id}；入口={spec.expectedEntry}；HTTP={spec.httpPort}；WS={spec.wsPort}；operationId={lifecycleId}；等待统一身份核验");
            BeginLaunchObservation();
        }

        private void PersistLaunchState() => SessionState.SetString(LaunchSessionKey,
            _launchState == null ? "" : JsonUtility.ToJson(_launchState));

        private void BeforeLaunchDomainReload()
        {
            EditorApplication.update -= EnsureInitialServiceRecovery;
            EditorApplication.delayCall -= EnsureInitialServiceRecovery;
            // A second Reload can precede the first restore callback; preserve the saved identity.
            if (_initialServiceRecoveryCompleted) PersistLaunchState();
            _lifecycleQueryCancellation?.Cancel();
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
                if (!state.TryConfirm(service.pid, service.createdAt, status.Health.server_instance_id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stopping)) { error = "service_identity_changed_or_expired"; return false; }
                DisposeTrackedProcess();
                _trackedProcess = candidate;
                _trackedProcessId = candidate.Id;
                _trackedProcessCreatedAtTicks = service.createdAt;
                candidate = null;
                _launchNeedsRevalidation = false;
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
            var json = SessionState.GetString(LaunchSessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var state = JsonUtility.FromJson<ServerLaunchState>(json);
                if (state?.spec == null || state.spec.Validate().Length != 0 ||
                    !UPilotDeploymentDiagnostics.SamePath(state.spec.projectPath, UPilotProjectConfig.ProjectRoot)) return;
                _launchState = state;
                // 不更新期限，不重放启动，不恢复任何业务任务。
                _launchNeedsRevalidation = state.verified;
                _launchReloadRevalidation = HasCompletedLaunchVerification(state, UPilotServerRestartDiagnostics.Current);
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
                _trackedProcessId = state.verified ? state.servicePid : state.launcherPid;
                _trackedProcessCreatedAtTicks = state.verified ? state.serviceCreatedAt : state.launcherCreatedAt;
                _trackedProcess = Process.GetProcessById(_trackedProcessId.Value);
                handle = _trackedProcess.Handle;
                if (_trackedProcess.HasExited || _trackedProcess.StartTime.ToUniversalTime().Ticks != _trackedProcessCreatedAtTicks)
                    throw new InvalidOperationException("服务进程在 Reload 后身份不符");
                if (!state.terminal)
                    SetLifecyclePhase(state.verified ? UPilotServerLifecyclePhase.Verifying : UPilotServerLifecyclePhase.Starting,
                        state.launcherPid, IsRestartObservationActive() ? _restartOperationId : _launchReloadRevalidation ? state.reloadRevalidation.observationId : Guid.NewGuid().ToString("N"),
                        _launchReloadRevalidation ? state.reloadRevalidation.deadlineUtcMs : state.deadlineUtcMs);
                if (!state.terminal)
                {
                    BeginLaunchObservation();
                    if (_launchReloadRevalidation) _launchNextProbeAt = state.reloadRevalidation.nextProbeAtUtcMs;
                }
            }
            catch (Exception ex)
            {
                StopLaunchObservation();
                if (_launchState != null)
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
                PersistLaunchState();
            }
        }

        private PreparedProcessStop PrepareTrackedServiceStop(PreparedProcessStop prepared, int expectedProcessId)
        {
            var state = _launchState;
            if (!state.verified && _launcherProcess != null && !_launcherProcess.HasExited)
            {
                // 新的显式停止只观察旧服务身份，不复活已失败维护、不修改旧操作/截止时间。
                var status = GetStatus();
                if (expectedProcessId > 0 && status.ProcessId != expectedProcessId)
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "停止观察与期望服务不符");
                if (!TryResolveStartedService(state, status, out var error, stopping: true))
                    throw new InvalidOperationException("停止前服务身份仍未核实：" + error);
            }
            var ports = SafeGetListeningPortsByPid(out var portsKnown);
            if (!state.verified)
            {
                // 失败且启动器已经退出时，只允许无监听的空停止；不猜测子进程。
                if (_launcherProcess == null || !_launcherProcess.HasExited || expectedProcessId > 0)
                    throw new InvalidOperationException("服务身份尚未核实，拒绝停止未知服务进程：" + state.error);
                ValidateStopPortOwnership(ports, portsKnown, Array.Empty<int>(), prepared.HttpPort, prepared.WsPort);
                return prepared;
            }
            ValidateStopPortOwnership(ports, portsKnown, new[] { state.servicePid }, prepared.HttpPort, prepared.WsPort);
            if (expectedProcessId > 0 && expectedProcessId != state.servicePid)
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "服务身份与停止请求不符");
            if (_trackedProcess == null || _trackedProcess.Id != state.servicePid ||
                _trackedProcess.StartTime.ToUniversalTime().Ticks != state.serviceCreatedAt)
                throw new InvalidOperationException("服务句柄身份已变化，未执行停止");
            if (_trackedProcess.HasExited)
            {
                ValidateStopPortOwnership(ports, portsKnown, Array.Empty<int>(), prepared.HttpPort, prepared.WsPort);
                if (expectedProcessId > 0) throw new InvalidOperationException("期望服务已退出");
                return prepared;
            }
            Process service = null;
            try
            {
                service = Process.GetProcessById(state.servicePid);
                var handle = service.Handle;
                var identity = ServerProcessIdentity.Read(service);
                if (!identity.alive || identity.createdAt != state.serviceCreatedAt ||
                    !Array.Exists(state.spec.serviceExecutablePaths, path => UPilotDeploymentDiagnostics.SamePath(path, identity.executable)))
                    throw new InvalidOperationException("服务创建时间或可执行文件与冻结身份不符");
                var command = GetProcessCommandLineForDiagnostics(service.Id);
                if (!ServerProcessIdentity.MatchesCommandLine(state.spec, command))
                    throw new InvalidOperationException("服务启动参数与冻结身份不符");
                service.Refresh();
                if (service.HasExited || service.StartTime.ToUniversalTime().Ticks != state.serviceCreatedAt)
                    throw new InvalidOperationException("停止证据采集期间服务身份变化");
                prepared.Add(service, command);
                service = null;
                return prepared;
            }
            finally { service?.Dispose(); }
        }

        private void ReleaseLaunchAfterStop()
        {
            _launchState?.reloadRevalidation?.Finish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, "service_stopped");
            StopLaunchObservation();
            // 只观察启动器自然退出，不递归杀进程树；未退出会使原退出验证门失败。
            if (_launcherProcess != null)
            {
                _stoppingProcesses.Add(_launcherProcess);
                _launcherProcess = null;
            }
            _launchState = null;
            PersistLaunchState();
        }
    }
}
