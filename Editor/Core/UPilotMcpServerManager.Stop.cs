// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class ServiceStopObservationRecord
    {
        public string id, project, lifetime, origin, requestId = "", ownerOperationId = "", status = "observing", reason = "";
        public long startedAt, deadline, nextCheckAt, endedAt;
        public int originalStartGeneration;
        public long originalRestartGeneration;
        public int timeoutSeconds, intervalMs, unityPid, httpPort, wsPort;
        public bool dispatched, processesExited, portsAvailable;
        public int[] processIds = Array.Empty<int>();
        public long[] processCreatedAt = Array.Empty<long>();
        public string pythonEntry = "", serverExe = "";
        public string maintenanceId = "";
        public string pendingTerminationDiagnostic = "";
        public int[] heldProcessIds = Array.Empty<int>();
        public long[] processHandles = Array.Empty<long>();
        public bool handlesTransferred;
        public string shutdownOwnerId = "";
        public bool preserveShutdown;
        // SessionState is scoped to this Unity process; Stopwatch ticks survive Domain Reload.
        public long monotonicStartedAt;
        internal string MonotonicElapsedMs => monotonicStartedAt > 0
            ? Logger.LifecycleElapsedMs(monotonicStartedAt).ToString() : "unknown";
        [NonSerialized] internal string lastEvidence = "";
        [NonSerialized] internal Exception failureException;
        internal bool Active => status == "observing";
        internal static ServiceStopObservationRecord Start(long now, int seconds, int interval, long parentDeadline = 0) => new ServiceStopObservationRecord {
            id = Guid.NewGuid().ToString("N"), startedAt = now, timeoutSeconds = seconds, intervalMs = interval,
            deadline = parentDeadline > 0 ? Math.Min(now + seconds * 1000L, parentDeadline) : now + seconds * 1000L,
        };
        internal bool Due(long now) => Active && now >= nextCheckAt;
        internal bool Expired(long now) => now >= deadline;
        internal void Checked(long now) => nextCheckAt = now + intervalMs;
        internal void End(long now, string terminal, string why)
        {
            if (!Active) return;
            if (terminal == "succeeded" && Expired(now)) { terminal = "timed_out"; why = "stop_observation_timeout"; }
            status = terminal; reason = why; endedAt = now;
        }
        internal bool MatchesScope(string projectPath, string serviceLifetime, int editorPid) =>
            unityPid == editorPid && lifetime == serviceLifetime &&
            UPilotDeploymentDiagnostics.SamePath(project, projectPath);
        internal bool PreventsAutomaticUpdateRetry(string operationId) =>
            !Active && !string.IsNullOrEmpty(operationId) && ownerOperationId == operationId;
        internal string StartMessage => $"[UPilot] 开始停止 MCP 服务；stopId={id}；来源={origin}；默认最多等待30秒，本次配置{timeoutSeconds}秒，有效预算{Math.Max(0, deadline - startedAt) / 1000d:F2}秒。";
        internal string TerminalMessage => $"[UPilot] {EndMessage}；stopId={id}；总耗时{Math.Max(0, endedAt - startedAt) / 1000d:F2}秒；状态={status}；进程退出={processesExited}；端口释放={portsAvailable}；原因={reason}。";
        internal string EndMessage => status == "succeeded" && reason == "already_stopped"
            ? "MCP 服务已停止，无需再次停止" : processesExited && portsAvailable && status == "succeeded"
            ? "MCP 服务停止成功" : processesExited && !portsAvailable ? "目标进程已退出，但端口仍不可用" : "MCP 服务停止观察结束";
    }

    internal sealed class ServiceStopObserver : IDisposable
    {
        private readonly ServiceStopObservationRecord _record;
        private readonly Func<long> _clock;
        private readonly Func<bool> _current;
        private readonly Action _probe;
        private readonly Action<string, string> _finish;
        internal bool Subscribed { get; private set; }
        internal ServiceStopObserver(ServiceStopObservationRecord record, Func<long> clock, Func<bool> current,
            Action probe, Action<string, string> finish)
        { _record = record; _clock = clock; _current = current; _probe = probe; _finish = finish; }
        internal void Start() { if (Subscribed || !_record.Active) return; EditorApplication.update += Tick; Subscribed = true; Tick(); }
        internal void Tick()
        {
            if (!_record.Active) { Dispose(); return; }
            try
            {
                if (!_current()) { _finish("stopped", "service_identity_or_lifetime_changed"); return; }
                var now = _clock();
                if (_record.Expired(now)) { _finish("timed_out", "stop_observation_timeout"); return; }
                if (!_record.Due(now)) return;
                _record.Checked(now); _probe();
            }
            catch (Exception ex)
            {
                _record.failureException = _record.failureException == null ? ex : new AggregateException(ex, _record.failureException);
                _finish("failed", ex.Message);
            }
            finally { if (!_record.Active) Dispose(); }
        }
        public void Dispose() { if (Subscribed) EditorApplication.update -= Tick; Subscribed = false; }
    }

    public sealed partial class UPilotMcpServerManager
    {
        private const string StopSessionKey = "UPilot.ServiceStopObservation.v1";
        private ServiceStopObservationRecord _stopObservation;
        private TaskCompletionSource<ServerStopResult> _stopCompletion;
        private Task<PreparedProcessStop> _stopPreparation;
        private int _stopStartGeneration;
        private long _stopRestartGeneration, _stopLifecycleGeneration;
        private ServiceStopObserver _stopObserver;
        private ServerStopScope _stopScope;
        internal Func<ServerStopScope, IEnumerable<int>, long, ServerStopDiscovery> StopDiscoveryOverride;
        internal Action<ServerStopTarget> StopTerminationOverride;
        internal bool HasActiveStopObservation => _stopObservation?.Active == true;
        internal ServiceStopObservationRecord StopObservation => _stopObservation;
        internal bool StopBlocksAutomaticRecovery => _stopObservation != null &&
            _stopObservation.MatchesScope(UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, Process.GetCurrentProcess().Id);
        private static long StopNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal Task<ServerStopResult> StopServerAndObserveExitAsync(UPilotServerStopOrigin origin,
            string requestId = "", int timeoutMs = 0, long parentDeadlineUtcMs = 0, ServerStopScope scope = null,
            PreparedProcessStop prepared = null, bool preserveRestart = false, string maintenanceId = "", bool preserveShutdown = false)
        {
            try { EnsureInitialServiceRecovery(); }
            catch { prepared?.Dispose(); throw; }
            if (HasActiveStopObservation) { prepared?.Dispose(); return _stopCompletion.Task; }
            var seconds = timeoutMs > 0 ? Math.Max(1, (timeoutMs + 999) / 1000) : UPilotTimingSettings.ServiceStopObservationTimeoutSeconds;
            var parent = parentDeadlineUtcMs > 0 ? parentDeadlineUtcMs : _restartMaintenanceDeadlineUtcMs;
            var record = ServiceStopObservationRecord.Start(StopNow, seconds, UPilotTimingSettings.ServiceStopCheckIntervalMs, parent);
            record.project = UPilotProjectConfig.ProjectRoot; record.lifetime = UPilotServiceLifetime.Id;
            record.unityPid = Process.GetCurrentProcess().Id; record.origin = origin.ToString();
            record.requestId = requestId ?? "";
            record.maintenanceId = maintenanceId ?? "";
            record.ownerOperationId = UPilotUpdateService.Instance.GetOperationStatus().OperationId;
            record.originalStartGeneration = _startAttemptGeneration;
            record.originalRestartGeneration = _restartGeneration;
            record.httpPort = scope?.HttpPort ?? HttpPort; record.wsPort = scope?.WsPort ?? WsPort;
            var shutdown = CaptureServiceShutdown();
            record.shutdownOwnerId = shutdown.Stopping ? shutdown.OwnerId :
                !string.IsNullOrEmpty(requestId) ? requestId : record.id;
            record.preserveShutdown = preserveRestart || preserveShutdown;
            record.monotonicStartedAt = Stopwatch.GetTimestamp();
            BeginServiceShutdown(record.shutdownOwnerId, "stop_preparing", record.deadline);
            _stopObservation = record;
            _stopCompletion = new TaskCompletionSource<ServerStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopStartGeneration = _startAttemptGeneration; _stopRestartGeneration = _restartGeneration;
            _stopLifecycleGeneration = 0;
            var completion = _stopCompletion.Task;
            var preparationTransferred = false;
            Logger.Lifecycle("server_stop_started", record.StartMessage + $" requestId={record.requestId} maintenanceId={record.maintenanceId} shutdownOwnerId={record.shutdownOwnerId} lifetime={record.lifetime} unityPid={record.unityPid} deadlineUtcMs={record.deadline} remainingMs={Math.Max(0, record.deadline - StopNow)}");
            try
            {
                if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting)
                    throw new InvalidOperationException("Service maintenance owns the current service.");
                if (record.Expired(StopNow)) throw new TimeoutException("Stop deadline already elapsed.");
                CancelExistingServiceRecoveryForStop(record.requestId);
                // Capture Unity-owned values once; all OS discovery runs off the Editor thread.
                var knownPid = _launchState?.servicePid > 0 ? _launchState.servicePid : _trackedProcessId ?? 0;
                _stopScope = scope ?? GetStopScope();
                record.pythonEntry = _stopScope.PythonEntry; record.serverExe = _stopScope.ServerExe;
                if (!preserveRestart)
                    CancelPendingRestart(true, origin, record.requestId.Length > 0 ? record.requestId : record.id,
                        _restartOperationId, _restartGeneration);
                StopLaunchObservation();
                System.Threading.Interlocked.Increment(ref _startAttemptGeneration);
                _startInProgress = false;
                _stopStartGeneration = _startAttemptGeneration; _stopRestartGeneration = _restartGeneration;
                _stopLifecycleGeneration = SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, knownPid, record.id, record.deadline);
                var capturedScope = _stopScope;
                _stopPreparation = prepared != null ? Task.FromResult(prepared) :
                    Task.Run(() => PrepareStopScope(capturedScope, new[] { knownPid }, record.deadline));
                preparationTransferred = true;
                SaveStopObservation();
                StartStopObserver();
            }
            catch (Exception ex)
            {
                if (!preparationTransferred) prepared?.Dispose();
                record.failureException = ex;
                FinishStopObservation(record.Expired(StopNow) ? "timed_out" : "failed", ex.Message);
            }
            return completion;
        }

        private PreparedProcessStop PrepareStopScope(ServerStopScope scope, IEnumerable<int> knownPids, long deadline, bool logLifecycle = true)
        {
            var started = Stopwatch.GetTimestamp();
            if (logLifecycle)
                Logger.Lifecycle("server_stop_discovery_started", $"{CaptureServiceShutdown().Detail} httpPort={scope.HttpPort} wsPort={scope.WsPort} remainingMs={Math.Max(0, deadline - StopNow)}");
            var prepared = new PreparedProcessStop { Scope = scope, HttpPort = scope.HttpPort, WsPort = scope.WsPort };
            try
            {
                using var discovery = StopDiscoveryOverride != null
                    ? StopDiscoveryOverride(scope, knownPids, deadline)
                    : DiscoverStopTargets(scope, knownPids, deadline, FindExplicitForeignStopOwner(scope, deadline));
                prepared.Processes.AddRange(discovery.Targets);
                discovery.Targets.Clear();
                prepared.VerifiedPorts = discovery.ListeningPorts;
                prepared.UnmatchedPortOwners = discovery.UnmatchedPortOwners;
                if (logLifecycle)
                    Logger.Lifecycle("server_stop_discovery_finished", $"{CaptureServiceShutdown().Detail} elapsedMs={Logger.LifecycleElapsedMs(started)} targetPids=[{string.Join(",", prepared.Processes.Select(x => x.ProcessId))}] unmatchedPortOwners=[{string.Join(",", prepared.UnmatchedPortOwners)}] remainingMs={Math.Max(0, deadline - StopNow)}");
                return prepared;
            }
            catch { prepared.Dispose(); throw; }
        }

        private static int FindExplicitForeignStopOwner(ServerStopScope scope, long deadline)
        {
            if (StopNow >= deadline) throw new TimeoutException("停止前项目证据检查已超过原期限。");
            return UPilotBridge.Instance.TryGetServerIdentity(out var identity) &&
                !UPilotDeploymentDiagnostics.SamePath(identity.configured_project_path, scope.ProjectPath)
                ? identity.server_pid : 0;
        }

        internal bool VerifyMaintenanceEndpointIdentity(int expectedProcessId, long deadlineUtcMs = 0)
        {
            if (expectedProcessId <= 0) return false;
            var scope = GetStopScope();
            var deadline = deadlineUtcMs > 0 ? Math.Min(StopNow + 8000, deadlineUtcMs) : StopNow + 8000;
            if (StopNow >= deadline) throw new TimeoutException("维护端点核验已超过原定期限。");
            using var target = ServerStopTarget.Open(expectedProcessId);
            if (target == null || target.HasExited) return false;
            var command = GetProcessCommandLineForDiagnostics(expectedProcessId, deadline);
            if (!scope.Matches(command, target.ExecutablePath)) return false;
            var ports = SafeGetListeningPortsByPid(out var known, deadline);
            if (!known) throw new InvalidOperationException("维护端点的监听归属查询失败。");
            return ports.TryGetValue(expectedProcessId, out var owned) && owned.Contains(scope.HttpPort) &&
                FindExplicitForeignStopOwner(scope, deadline) != expectedProcessId && !target.HasExited;
        }

        private void PollStopObservation()
        {
            var r = _stopObservation;
            if (r?.Active != true) return;
            try
            {
                if (_stopPreparation == null)
                {
                    var scope = _stopScope;
                    var known = StoppingTargets.Select(x => x.ProcessId).ToArray();
                    _stopPreparation = Task.Run(() => PrepareStopScope(scope, known, r.deadline, logLifecycle: false));
                    return;
                }
                if (!_stopPreparation.IsCompleted) return;
                using var prepared = _stopPreparation.GetAwaiter().GetResult();
                _stopPreparation = null;
                if (r.Expired(StopNow)) { FinishStopObservation("timed_out", "stop_observation_timeout"); return; }
                var firstDispatch = !r.dispatched;
                r.dispatched = true;
                r.processIds = r.processIds.Concat(prepared.Processes.Select(x => x.ProcessId)).Distinct().ToArray();
                SaveStopObservation();
                StopPreparedProcesses(prepared, 0, stopObservationId: r.id, stopDeadline: r.deadline,
                    initialDispatch: firstDispatch, maintenanceId: r.maintenanceId);
                _stopStartGeneration = _startAttemptGeneration; _stopRestartGeneration = _restartGeneration;
                _stopLifecycleGeneration = _lifecycleGeneration;
                SaveStopObservation();
                r.processesExited = StoppedProcessesExited();
                // Always inspect both ports, including while a launcher is still alive.
                var httpReleased = UPilotPortAllocator.IsPortAvailable(r.httpPort);
                var wsReleased = UPilotPortAllocator.IsPortAvailable(r.wsPort);
                r.portsAvailable = httpReleased && wsReleased;
                var evidence = $"processExited={r.processesExited} httpReleased={httpReleased} wsReleased={wsReleased} targetPids=[{string.Join(",", r.processIds)}] unmatchedPortOwners=[{string.Join(",", prepared.UnmatchedPortOwners)}]";
                if (evidence != r.lastEvidence)
                {
                    r.lastEvidence = evidence;
                    Logger.Lifecycle("server_stop_evidence_changed", $"stopId={r.id} requestId={r.requestId} {CaptureServiceShutdown().Detail} {evidence} elapsedMs={r.MonotonicElapsedMs} remainingMs={Math.Max(0, r.deadline - StopNow)}");
                }
                if (prepared.UnmatchedPortOwners.Length > 0)
                    throw new InvalidOperationException("停止后端口由其他程序或无法核实入口的进程占用；PID=[" + string.Join(",", prepared.UnmatchedPortOwners) + "]。");
                if (r.processesExited && !r.portsAvailable && prepared.Processes.Count == 0)
                    throw new InvalidOperationException("目标进程已退出，但端口仍不可绑定；未发现可关闭的匹配监听进程。");
                if (r.processesExited && r.portsAvailable) FinishStopObservation("succeeded", r.processIds.Length == 0 ? "already_stopped" : "exit_and_ports_verified");
            }
            catch (Exception ex)
            {
                r.failureException = r.failureException == null ? ex : new AggregateException(ex, r.failureException);
                FinishStopObservation(r.Expired(StopNow) ? "timed_out" : "failed", ex.Message);
            }
        }

        private void SaveStopObservation()
        {
            _stopObservation.heldProcessIds = StoppingTargets.Select(x => x.ProcessId).ToArray();
            _stopObservation.processHandles = StoppingTargets.Select(x => x.HandleValue).ToArray();
            SessionState.SetString(StopSessionKey, JsonUtility.ToJson(_stopObservation));
        }
        private void StartStopObserver()
        {
            _stopObserver?.Dispose();
            _stopObserver = new ServiceStopObserver(_stopObservation, () => StopNow,
                () => _stopObservation.lifetime == UPilotServiceLifetime.Id &&
                    _stopStartGeneration == _startAttemptGeneration && _stopRestartGeneration == _restartGeneration,
                PollStopObservation, FinishStopObservation);
            _stopObserver.Start();
        }
        private void FinishStopObservation(string terminal, string reason)
        {
            var r = _stopObservation;
            if (r?.Active != true) return;
            r.End(StopNow, terminal, reason);
            terminal = r.status; reason = r.reason;
            _stopObserver?.Dispose(); _stopObserver = null;
            var preparation = _stopPreparation; _stopPreparation = null;
            if (preparation != null) _ = preparation.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            foreach (var process in _stoppingProcesses) process.Dispose();
            _stoppingProcesses.Clear();
            foreach (var target in StoppingTargets) target.Dispose();
            StoppingTargets.Clear();
            SaveStopObservation();
            var failed = terminal == "failed" || terminal == "timed_out";
            if (_stopLifecycleGeneration > 0) EndLifecyclePhase(_stopLifecycleGeneration, failed ? reason : "",
                terminal == "timed_out", emitDiagnostic: false, failureException: failed ? r.failureException : null);
            var message = r.TerminalMessage;
            Logger.Lifecycle("server_stop_finished", $"{message} requestId={r.requestId} maintenanceId={r.maintenanceId} shutdownOwnerId={r.shutdownOwnerId} deadlineUtcMs={r.deadline} elapsedMs={r.MonotonicElapsedMs} remainingMs={Math.Max(0, r.deadline - StopNow)}");
            if (failed)
                Logger.ReportFailure("SERVER", message, r.failureException, r.id, terminal: true);
            else Logger.CloseDiagnostics(r.id);
            var result = new ServerStopResult(r.processesExited && (terminal == "succeeded" || terminal == "timed_out"), r.portsAvailable,
                _statusGeneration, _stopStartGeneration, _stopRestartGeneration, terminal == "succeeded");
            if (!r.preserveShutdown || terminal != "succeeded") FinishServiceShutdown(r.shutdownOwnerId, "stop_" + terminal);
            _stopCompletion?.TrySetResult(result);
            InvalidateStatusCache();
        }

        internal void ResetStopObservation(string reason)
        {
            FinishStopObservation("stopped", reason);
            if (reason == "new_service_start_or_restart")
            {
                _stopObservation = null;
                SessionState.EraseString(StopSessionKey);
            }
        }
        private void BeforeStopDomainReload()
        {
            if (!HasActiveStopObservation) return;
            if (!_stopObservation.dispatched) { FinishStopObservation("stopped", "reload_before_stop_dispatch"); return; }
            SaveStopObservation();
            foreach (var target in StoppingTargets) target.DetachHandle();
            StoppingTargets.Clear();
            _stopObservation.handlesTransferred = true;
            SessionState.SetString(StopSessionKey, JsonUtility.ToJson(_stopObservation));
            _stopObserver?.Dispose(); _stopObserver = null;
            foreach (var process in _stoppingProcesses) process.Dispose();
            _stoppingProcesses.Clear();
            _stopCompletion?.TrySetResult(default);
        }

        private bool RestoreStopObservation()
        {
            var json = SessionState.GetString(StopSessionKey, "");
            if (json.Length == 0) return false;
            var r = JsonUtility.FromJson<ServiceStopObservationRecord>(json);
            if (r == null || !r.MatchesScope(UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, Process.GetCurrentProcess().Id)) return false;
            _stopObservation = r;
            if (!r.Active) return true; // Retain the stop outcome; only an explicit new start clears the intent.
            if (string.IsNullOrEmpty(r.shutdownOwnerId)) r.shutdownOwnerId = !string.IsNullOrEmpty(r.requestId) ? r.requestId : r.id;
            BeginServiceShutdown(r.shutdownOwnerId, "stop_restored", r.deadline);
            _stopCompletion = new TaskCompletionSource<ServerStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Domain-local counters restart after Reload. Original counters remain in the record;
            // the restored observer fences new actions against this domain's runtime baseline.
            _stopStartGeneration = _startAttemptGeneration; _stopRestartGeneration = _restartGeneration;
            _stopLifecycleGeneration = SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, 0, r.id, r.deadline);
            try
            {
                if (!r.dispatched || r.lifetime != UPilotServiceLifetime.Id || r.unityPid != Process.GetCurrentProcess().Id ||
                    !UPilotDeploymentDiagnostics.SamePath(r.project, UPilotProjectConfig.ProjectRoot) ||
                    r.heldProcessIds.Length != r.processHandles.Length)
                    throw new InvalidOperationException("Saved stop observation expired or its identity is unknown.");
                if (r.processHandles.Length > 0 && !r.handlesTransferred)
                    throw new InvalidOperationException("Saved stop handles were not transferred by the original domain.");
                var failures = new List<Exception>();
                var restoredHandles = new HashSet<long>();
                for (var i = 0; i < r.processHandles.Length; i++)
                {
                    try
                    {
                        if (!restoredHandles.Add(r.processHandles[i]))
                            throw new InvalidOperationException("保存的 Server OS 句柄重复，未重复接管。");
                        StoppingTargets.Add(ServerStopTarget.RestoreHandle(r.processHandles[i], r.heldProcessIds[i]));
                    }
                    catch (Exception ex)
                    {
                        // A failed adoption may already have closed its handle. Never retry or
                        // close an unverified number; still reclaim every other transferred handle.
                        failures.Add(new InvalidOperationException($"Server 停止句柄恢复失败；PID={r.heldProcessIds[i]}；handle={r.processHandles[i]}；未重试该句柄。", ex));
                    }
                }
                r.handlesTransferred = false;
                SaveStopObservation();
                if (failures.Count > 0) throw new AggregateException("部分 Server 停止句柄未能核实；已释放其他可接管的句柄，未重放终止。", failures);
                _stopScope = new ServerStopScope(r.project, r.pythonEntry, r.serverExe, r.httpPort, r.wsPort);
                if (!string.IsNullOrEmpty(r.pendingTerminationDiagnostic))
                    r.failureException = new InvalidOperationException("原停止派发异常及堆栈：\n" + r.pendingTerminationDiagnostic);
                if (r.Expired(StopNow)) throw new TimeoutException("Saved stop observation reached its original deadline.");
                StartStopObserver();
            }
            catch (Exception ex)
            {
                r.failureException = r.failureException == null ? ex : new AggregateException(ex, r.failureException);
                FinishStopObservation(r.Expired(StopNow) ? "timed_out" : "failed", ex.Message);
            }
            return true; // A saved explicit stop must suppress bootstrap auto-start even after it ends.
        }
    }
}
