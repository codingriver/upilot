// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    internal enum UPilotServerLifecyclePhase { None, Starting, Stopping, Verifying }

    public sealed partial class UPilotMcpServerManager
    {
        private UPilotServerLifecyclePhase _lifecyclePhase;
        private long _lifecycleGeneration;
        private CancellationTokenSource _lifecycleQueryCancellation;
        private string _lifecycleOperationId = "";
        private int _lifecycleProcessId;
        private long _lifecycleDeadlineUtcMs;
        private long _lifecycleStartedAtUtcMs;

        internal sealed class LifecycleProbeDeferredException : OperationCanceledException { }

        internal long SetLifecyclePhase(UPilotServerLifecyclePhase phase, int processId,
            string operationId, long deadlineUtcMs = 0)
        {
            lock (_statusLock)
            {
                if (_lifecyclePhase == phase && _lifecycleOperationId == operationId)
                {
                    _lifecycleProcessId = processId;
                    return _lifecycleGeneration;
                }
                _lifecycleQueryCancellation?.Cancel();
                _lifecycleQueryCancellation?.Dispose();
                _lifecycleQueryCancellation = new CancellationTokenSource();
                _activeStatusRefresh?.Cancel("lifecycle_phase");
                _periodicHealthNeedsVerification = true;
                PausePeriodicHealthObservation();
                ++_statusGeneration;
                ++_lifecycleGeneration;
                _lifecyclePhase = phase;
                _lifecycleProcessId = processId;
                _lifecycleOperationId = operationId;
                _lifecycleDeadlineUtcMs = deadlineUtcMs;
                _lifecycleStartedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _lastRefreshMs = 0;
                _nextEditorHealthObservationAtUtcMs = 0;
                // 不沿用上一进程的 health；外层启动和重启观察器保留。
                _cachedStatus = new McpServerStatus {
                    LifecyclePhase = phase, StatusGeneration = _statusGeneration,
                    ProcessId = processId > 0 ? processId : (int?)null,
                    IsRunning = phase != UPilotServerLifecyclePhase.None && processId > 0,
                    DiagnosisPending = phase != UPilotServerLifecyclePhase.None,
                };
                UnityEngine.Debug.Log($"[UPilotMcpServerManager] 生命周期：{LifecycleTitle(phase)}；PID={processId}；operationId={operationId}。");
                if (phase != UPilotServerLifecyclePhase.None && UPilotServerRestartDiagnostics.IsActive(operationId))
                    UPilotServerRestartDiagnostics.RecordPhase(operationId, phase.ToString().ToLowerInvariant());
                NotifyStatusChanged();
                return _lifecycleGeneration;
            }
        }

        internal bool EndLifecyclePhase(long generation, string reason = "", bool timeout = false, bool emitDiagnostic = true,
            Exception failureException = null)
        {
            lock (_statusLock)
            {
                if (generation != _lifecycleGeneration || _lifecyclePhase == UPilotServerLifecyclePhase.None) return false;
                var phase = _lifecyclePhase;
                var elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lifecycleStartedAtUtcMs);
                var pid = _lifecycleProcessId;
                var operationId = _lifecycleOperationId;
                SetLifecyclePhase(UPilotServerLifecyclePhase.None, pid, operationId);
                if (!string.IsNullOrEmpty(reason))
                {
                    _cachedStatus.ErrorMessage = reason;
                    _cachedStatus.StatusFailureStage = phase.ToString().ToLowerInvariant();
                    _cachedStatus.FailureKind = failureException == null
                        ? McpStatusFailureKind.Transient : ClassifyStatusException(failureException);
                    if (emitDiagnostic && timeout)
                        Logger.ReportFailure("SERVER", $"生命周期终态超时 source=Unity客户端 PID={pid} operationId={operationId} phase={phase} elapsedMs={elapsed} reason={reason}", failureException);
                    else if (emitDiagnostic)
                        Logger.ReportFailure("SERVER", $"[UPilotMcpServerManager] 生命周期失败 source=Unity客户端 PID={pid} operationId={operationId} phase={phase} reason={reason}。", failureException);
                }
                NotifyStatusChanged();
                return true;
            }
        }

        internal static string LifecycleTitle(UPilotServerLifecyclePhase phase) => phase switch
        {
            UPilotServerLifecyclePhase.Starting => "正在启动服务器",
            UPilotServerLifecyclePhase.Stopping => "正在关闭服务器",
            UPilotServerLifecyclePhase.Verifying => "正在验证服务",
            _ => "生命周期操作结束",
        };

        private bool TryGetDeferredLifecycleStatus(out McpServerStatus status)
        {
            lock (_statusLock)
            {
                status = _cachedStatus;
                status.LifecyclePhase = _lifecyclePhase;
                return _lifecyclePhase != UPilotServerLifecyclePhase.None;
            }
        }

        internal bool AdvanceStartingPhase(long generation, bool alive, bool httpListening, bool wsListening, long nowUtcMs)
        {
            lock (_statusLock)
            {
                if (generation != _lifecycleGeneration) return false;
                if (_lifecyclePhase != UPilotServerLifecyclePhase.Starting)
                    return _lifecyclePhase != UPilotServerLifecyclePhase.Stopping;
                if (!alive || (_lifecycleDeadlineUtcMs > 0 && nowUtcMs >= _lifecycleDeadlineUtcMs))
                {
                    EndLifecyclePhase(generation, alive ? "launch_identity_timeout" : "process_exited", alive);
                    return false;
                }
                _cachedStatus.HttpPortListening = httpListening;
                _cachedStatus.WsPortListening = wsListening;
                if (!httpListening || !wsListening) return false;
                // 双端口只表示允许验证，不能直接显示已就绪。
                SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, _lifecycleProcessId, _lifecycleOperationId, _lifecycleDeadlineUtcMs);
                return true;
            }
        }

        private async Task<bool> PrepareLifecycleProbeAsync(CancellationToken token = default)
        {
            long generation;
            lock (_statusLock)
            {
                if (_lifecyclePhase == UPilotServerLifecyclePhase.Stopping) return false;
                if (_lifecyclePhase != UPilotServerLifecyclePhase.Starting) return true;
                generation = _lifecycleGeneration;
            }
            var state = _launchState;
            var process = _launcherProcess;
            token.ThrowIfCancellationRequested();
            var alive = process != null && !process.HasExited;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool http = false, ws = false;
            if (alive && state != null && now < state.deadlineUtcMs)
            {
                var httpTask = IsPortListeningAsync("127.0.0.1", state.spec.httpPort);
                var wsTask = IsPortListeningAsync("127.0.0.1", state.spec.wsPort);
                http = await httpTask;
                ws = await wsTask;
                token.ThrowIfCancellationRequested();
                alive = !process.HasExited;
            }
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ready = AdvanceStartingPhase(generation, alive, http, ws, now);
            if (!ready && ReferenceEquals(state, _launchState) && state != null && (!alive || now >= state.deadlineUtcMs))
            {
                state.terminal = true;
                state.error = alive ? "launch_identity_timeout" : "process_exited";
                PersistLaunchState();
                UPilotStartupDiagnostics.RecordBlockingReason(state.error, state.error);
            }
            return ready;
        }

        internal async Task<System.Net.Http.HttpResponseMessage> SendLifecycleStatusAsync(
            System.Net.Http.HttpClient client, string url, long deadlineUtcMs, CancellationToken token = default,
            int requestTimeoutMs = 2000, long expectedGeneration = -1)
        {
            CancellationTokenSource linked;
            Task<System.Net.Http.HttpResponseMessage> send;
            long generation;
            var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_statusLock)
            {
                if (_lifecyclePhase == UPilotServerLifecyclePhase.Starting || _lifecyclePhase == UPilotServerLifecyclePhase.Stopping ||
                    (expectedGeneration >= 0 && expectedGeneration != _lifecycleGeneration)) throw new LifecycleProbeDeferredException();
                generation = _lifecycleGeneration;
                _lifecycleQueryCancellation ??= new CancellationTokenSource();
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifecycleQueryCancellation.Token);
                // 最终发送与阶段切换共用锁；旧 health 不能跨代接着发 stats。
                send = SendBoundedStatusAsync(client, url, deadlineUtcMs, linked.Token, requestTimeoutMs);
            }
            try
            {
                var response = await send;
                lock (_statusLock)
                {
                    if (generation != _lifecycleGeneration || linked.IsCancellationRequested)
                    {
                        response.Dispose();
                        token.ThrowIfCancellationRequested();
                        throw new LifecycleProbeDeferredException();
                    }
                }
                return response;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && generation != _lifecycleGeneration)
            { throw new LifecycleProbeDeferredException(); }
            catch (Exception ex) when ((ex is TimeoutException || ex is TaskCanceledException) && !linked.IsCancellationRequested)
            {
                LogRestartProbeException(_lifecycleOperationId, new Uri(url).AbsolutePath, started, ex);
                throw;
            }
            finally { linked.Dispose(); }
        }
    }
}
