// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CodingRiver.UPilot
{
    internal sealed class BridgeBootstrapReceipt
    {
        internal string ProjectPath, ServerLifecycleId, ServerInstanceId, SessionId;
        internal string SnapshotId, BootstrapId, Nonce;
        internal long ConnectionEpoch, ProcessCreatedAt;
        internal int ProcessId, ServerProcessId;
    }

    [Serializable]
    internal sealed class BridgeReadyPayload
    {
        public int bridgeReadyContractVersion = 1;
        public string helloId, serverLifecycleId, serverInstanceId;
    }

    [Serializable]
    internal sealed class BridgeBootstrapPayload
    {
        public int bridgeReadyContractVersion;
        public string helloId, serverLifecycleId, serverInstanceId, nonce;
        public string controlKind, expectedBridgeSessionId;
        public string bootstrapId, snapshotId, producerEpoch;
        public long domainGeneration, sequence;
    }

    [Serializable]
    internal sealed class BridgeBootstrapMessage : BridgeEnvelope
    {
        public BridgeBootstrapPayload payload;
    }

    [Serializable]
    internal sealed class BridgeBootstrapResult
    {
        public int bridgeReadyContractVersion = 1;
        public string helloId, serverLifecycleId, serverInstanceId, nonce;
        public EditorExecutionStatePayload state;
        public CompileErrorsPayload compileEvidence;
    }

    public sealed partial class UPilotBridge
    {
        internal const int BridgeReadyContractVersion = 1;
        private bool _stateSynchronized, _localReadyAnnounced;
        private PendingHello _initializingHello;
        private string _readyHelloId, _readyServerInstanceId, _readyProjectPath;
        private string _bootstrapRequestId, _bootstrapNonce, _bootstrapSnapshotId;
        private long _readyDeadlineTick, _admissionGeneration;
        private BridgeBootstrapReceipt _bootstrapReceipt;
        private EditorExecutionStatePayload _bootstrapSnapshot;
        private AsyncLocal<ConnectionOrigin> _requestOrigin;
        private ConcurrentDictionary<string, ConnectionOrigin> _commandOrigins;
        private AsyncLocal<ConnectionOrigin> RequestOrigin => LazyInitializer.EnsureInitialized(ref _requestOrigin);
        private ConcurrentDictionary<string, ConnectionOrigin> CommandOrigins =>
            LazyInitializer.EnsureInitialized(ref _commandOrigins);

        private sealed class ConnectionOrigin
        {
            internal ClientWebSocket Socket;
            internal long Epoch, AdmissionGeneration;
            internal string SessionId, ServerLifetime, ServiceLifetime;
            internal CancellationToken Token;
            internal bool MaintenanceControl;
        }

        public bool IsStateSynchronized
        {
            get { lock (ConnectionStateLock) return _stateSynchronized && _started && _isAuthenticated &&
                !_admissionClosed && !_resettingWork && _ws?.State == WebSocketState.Open; }
        }

        internal void CloseOrdinaryAdmission(string reason)
        {
            lock (ConnectionStateLock) CloseOrdinaryAdmissionLocked();
        }

        private void CloseOrdinaryAdmissionLocked()
        {
            ++_admissionGeneration;
            _stateSynchronized = false;
            _admissionClosed = true;
            _localReadyAnnounced = false;
            _initializingHello = null;
            _bootstrapReceipt = null;
            _bootstrapSnapshot = null;
            _bootstrapRequestId = _bootstrapNonce = _bootstrapSnapshotId = null;
        }

        internal bool TryGetBootstrapReceipt(string expectedSessionId, string expectedServerInstanceId,
            out BridgeBootstrapReceipt receipt)
        {
            lock (ConnectionStateLock)
            {
                receipt = _bootstrapReceipt;
                if (!_stateSynchronized || _admissionClosed || !_started || !_isAuthenticated || _resettingWork ||
                    _ws?.State != WebSocketState.Open || receipt == null ||
                    receipt.ConnectionEpoch != _helloConnectionEpoch || receipt.SessionId != _sessionId ||
                    receipt.ServerLifecycleId != _boundServerLifetime ||
                    receipt.SessionId != expectedSessionId || receipt.ServerInstanceId != expectedServerInstanceId)
                { receipt = null; return false; }
                return true;
            }
        }

        private ConnectionOrigin CaptureOrigin(CancellationToken token)
        {
            lock (ConnectionStateLock) return new ConnectionOrigin {
                Socket = _ws, Epoch = _helloConnectionEpoch, SessionId = _sessionId,
                ServerLifetime = _boundServerLifetime, ServiceLifetime = UPilotServiceLifetime.Id,
                AdmissionGeneration = _admissionGeneration, Token = token,
            };
        }

        private bool IsOriginCurrentLocked(ConnectionOrigin origin, bool requireReady)
        {
            return origin != null && _isAuthenticated && IsConnectionCycleCurrentLocked(origin.Socket, origin.Epoch, origin.Token) &&
                origin.SessionId == _sessionId && origin.ServerLifetime == _boundServerLifetime &&
                origin.ServiceLifetime == UPilotServiceLifetime.Id && origin.AdmissionGeneration == _admissionGeneration &&
                (!requireReady || (_stateSynchronized && !_admissionClosed && !_resettingWork));
        }

        private bool IsOriginCurrent(ConnectionOrigin origin, bool requireReady = true)
        {
            lock (ConnectionStateLock) return IsOriginCurrentLocked(origin, requireReady);
        }

        private ConnectionOrigin OriginForSend(string id, CancellationToken token, bool commandResult)
        {
            if (RequestOrigin.Value != null) return RequestOrigin.Value;
            if (!string.IsNullOrEmpty(id) && CommandOrigins.TryGetValue(id, out var origin)) return origin;
            // Only state/events created locally may adopt the current connection.
            return commandResult ? null : CaptureOrigin(token);
        }

        private void RefreshMainThreadObservation()
        {
            _lastMainThreadPumpAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _activeSceneName = SceneManager.GetActiveScene().name;
            _cachedIsCompiling = CurrentIsCompiling();
            _cachedIsUpdating = EditorApplication.isUpdating;
            _maintenanceInitialization = UPilotServiceMaintenance.Initialization;
            _cachedIsPlaying = EditorApplication.isPlaying;
            _cachedIsPaused = EditorApplication.isPaused;
            _cachedPlayModeState = _cachedIsPaused ? "pause" : (_cachedIsPlaying ? "play" : "edit");
        }

        private void PublishReadyIfInitialized()
        {
            PendingHello hello;
            ConnectionOrigin origin;
            lock (ConnectionStateLock)
            {
                hello = _initializingHello;
                if (hello == null || _localReadyAnnounced || !_isAuthenticated || _resettingWork ||
                    !IsConnectionCycleCurrentLocked(hello.Socket, hello.ConnectionEpoch, hello.Token) ||
                    _maintenanceInitialization == null || !_maintenanceInitialization.recoveryChecked) return;
                if (Stopwatch.GetTimestamp() >= _readyDeadlineTick) return;
                _localReadyAnnounced = true;
                origin = CaptureOrigin(hello.Token);
            }
            _ = SendReadyControlAsync(hello, origin);
        }

        private async Task SendReadyControlAsync(PendingHello hello, ConnectionOrigin origin)
        {
            try
            {
                var message = new EventMessage<BridgeReadyPayload> {
                    id = "ready-" + hello.Ack.id, name = "session.ready", sessionId = origin.SessionId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    payload = new BridgeReadyPayload { helloId = hello.Ack.id,
                        serverLifecycleId = origin.ServerLifetime, serverInstanceId = hello.Ack.payload.server_instance_id },
                };
                await SendOriginJsonAsync(JsonUtility.ToJson(message), hello.Token, origin, false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (hello.Token.IsCancellationRequested) { }
            catch (Exception ex) { FailBootstrap(origin, "ready_notice_failed", ex); }
        }

        private bool TryHandleReadinessControl(string json, BridgeEnvelope envelope, CancellationToken token,
            ClientWebSocket socket, long epoch)
        {
            if (envelope.name != "session.bootstrap" && envelope.name != "session.bootstrap.ack") return false;
            var message = JsonUtility.FromJson<BridgeBootstrapMessage>(json);
            var p = message?.payload;
            ConnectionOrigin origin;
            lock (ConnectionStateLock)
            {
                if (!IsConnectionCycleCurrentLocked(socket, epoch, token) || envelope.sessionId != _sessionId ||
                    !_isAuthenticated || !_localReadyAnnounced || _resettingWork || p == null ||
                    p.bridgeReadyContractVersion != BridgeReadyContractVersion ||
                    p.helloId != _readyHelloId || p.serverLifecycleId != _boundServerLifetime ||
                    p.serverInstanceId != _readyServerInstanceId ||
                    Stopwatch.GetTimestamp() >= _readyDeadlineTick) return true;
                origin = CaptureOrigin(token);
                if (envelope.name == "session.bootstrap.ack")
                {
                    if (envelope.type != "event" || _stateSynchronized || string.IsNullOrEmpty(_bootstrapSnapshotId) ||
                        p.bootstrapId != _bootstrapRequestId || p.nonce != _bootstrapNonce ||
                        p.snapshotId != _bootstrapSnapshotId || p.producerEpoch != _producerEpoch ||
                        p.domainGeneration != _domainGeneration || _bootstrapSnapshot == null ||
                        p.sequence != _bootstrapSnapshot.sequence) return true;
                    _bootstrapReceipt = new BridgeBootstrapReceipt {
                        ProjectPath = _readyProjectPath, SessionId = _sessionId,
                        ServerLifecycleId = _boundServerLifetime, ServerInstanceId = _readyServerInstanceId,
                        SnapshotId = p.snapshotId, BootstrapId = p.bootstrapId, Nonce = p.nonce,
                        ConnectionEpoch = epoch, ProcessId = _bootstrapSnapshot.processId,
                        ProcessCreatedAt = _bootstrapSnapshot.processCreatedAt,
                        ServerProcessId = _identityHello?.Ack?.payload?.server_pid ?? 0,
                    };
                    _admissionClosed = false;
                    _stateSynchronized = true;
                    _initializingHello = null;
                    // No Unity API or main-thread work may precede opening this gate.
                    EnqueueControl(() => {
                        if (!IsOriginCurrent(origin)) return;
                        Logger.CloseDiagnostics("bridge-connection");
                        LifecycleManager.MarkReadyRecoveryCompleted();
                        UPilotQuickStart.NotifyDeploymentChanged();
                        _ = PublishExecutionStateAsync("session_synchronized", token);
                    });
                    return true;
                }
                if (envelope.type != "command" || string.IsNullOrEmpty(envelope.id) ||
                    string.IsNullOrEmpty(p.nonce) || p.nonce.Length != 32 || _stateSynchronized) return true;
                if (!string.IsNullOrEmpty(_bootstrapRequestId)) return true;
                _bootstrapRequestId = envelope.id;
                _bootstrapNonce = p.nonce;
            }
            EnqueueControl(() => CompleteBootstrapOnMainThread(envelope.id, p, origin));
            return true;
        }

        private void CompleteBootstrapOnMainThread(string id, BridgeBootstrapPayload request, ConnectionOrigin origin)
        {
            if (!IsOriginCurrent(origin, false)) return;
            try
            {
                RefreshMainThreadObservation();
                if (!_cachedIsCompiling && !_cachedIsUpdating) _compileService.CompleteVerification();
                if (!TryBuildAndPersistExecutionState("session_bootstrap", out var snapshot))
                    throw new IOException("Bootstrap Editor state could not be persisted.");
                var result = new ResultMessage<BridgeBootstrapResult> {
                    id = id, name = "session.bootstrap", sessionId = origin.SessionId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    payload = new BridgeBootstrapResult { helloId = request.helloId,
                        serverLifecycleId = request.serverLifecycleId, serverInstanceId = request.serverInstanceId,
                        nonce = request.nonce, state = snapshot,
                        compileEvidence = _compileService.BuildLastCompileErrorsPayload(includeWarnings: true) },
                };
                lock (ConnectionStateLock)
                {
                    if (!IsOriginCurrentLocked(origin, false) || _bootstrapRequestId != id) return;
                    _bootstrapSnapshotId = snapshot.snapshotId;
                    _bootstrapSnapshot = snapshot;
                }
                _ = SendBootstrapResultAsync(SerializeBootstrapResult(result), origin);
            }
            catch (Exception ex) { FailBootstrap(origin, "bootstrap_snapshot_failed", ex); }
        }

        private async Task SendBootstrapResultAsync(string json, ConnectionOrigin origin)
        {
            try { await SendOriginJsonAsync(json, origin.Token, origin, false).ConfigureAwait(false); }
            catch (OperationCanceledException) when (origin.Token.IsCancellationRequested) { }
            catch (Exception ex) { FailBootstrap(origin, "bootstrap_send_failed", ex); }
        }

        internal static string SerializeBootstrapResult(ResultMessage<BridgeBootstrapResult> result)
        {
            var json = JsonUtility.ToJson(result);
            if (result.payload.state.compileEvidence == null && result.payload.compileEvidence.compileEvidence == null)
            {
                // Unity inline serialization materializes null Serializable classes as default objects.
                // Both bootstrap ledgers are absent here; preserve absence, not an invalid identity.
                var emptyLedger = "\"compileEvidence\":" + JsonUtility.ToJson(new CompileEvidencePayload());
                json = json.Replace(emptyLedger, "\"compileEvidence\":null");
            }
            return json;
        }

        private void FailBootstrap(ConnectionOrigin origin, string stage, Exception exception)
        {
            lock (ConnectionStateLock)
            {
                if (!IsOriginCurrentLocked(origin, false)) return;
                CloseOrdinaryAdmissionLocked();
            }
            Logger.ReportFailure("BRIDGE", $"{stage} sessionId={origin.SessionId} epoch={origin.Epoch}", exception,
                scope: "bootstrap|" + origin.SessionId);
            try { origin.Socket?.Abort(); } catch { }
        }

        private void ObserveLocalCompileState(string transition)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _controlQueue.Enqueue(() => ObserveLocalCompileState(transition));
                return;
            }
            if (transition == "domain_reload_starting") CloseOrdinaryAdmission("domain_reload");
            var previous = RequestOrigin.Value;
            try
            {
                RequestOrigin.Value = null;
                RefreshMainThreadObservation();
                _ = PublishExecutionStateAsync(transition, _cts?.Token ?? CancellationToken.None);
            }
            finally { RequestOrigin.Value = previous; }
        }

        // Compiler verification reads Unity APIs and must run on the Editor thread.
        // Complete the waiter even when its original connection has been revoked.
        private async Task<T> ObserveCompileOnMainThreadAsync<T>(Func<T> observation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var origin = RequestOrigin.Value;
            if (origin != null && !IsOriginCurrent(origin)) throw new OperationCanceledException(token);
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) return observation();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => completion.TrySetCanceled()))
            {
                _controlQueue.Enqueue(() => {
                    if (completion.Task.IsCompleted) return;
                    if (token.IsCancellationRequested || (origin != null && !IsOriginCurrent(origin)))
                    { completion.TrySetCanceled(); return; }
                    try { completion.TrySetResult(observation()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
                return await completion.Task.ConfigureAwait(false);
            }
        }

        private static bool IsCompileObservation(string name) => name == "compile.wait" || name == "compile.errors.get" ||
            name == "editor.state" || name == "resource.editorState" || name == "test.status" ||
            name == "test.results" || name == "queue.snapshot";

        private bool IsMaintenanceControl(string json, BridgeEnvelope envelope)
        {
            if (envelope.type != "command" || envelope.name != "service.restart") return false;
            var payload = JsonUtility.FromJson<BridgeBootstrapMessage>(json)?.payload;
            lock (ConnectionStateLock) return _isAuthenticated && !_resettingWork && payload != null &&
                payload.controlKind == "service.maintenance" && payload.bridgeReadyContractVersion == BridgeReadyContractVersion &&
                payload.helloId == _readyHelloId && payload.serverLifecycleId == _boundServerLifetime &&
                payload.serverInstanceId == _readyServerInstanceId && payload.expectedBridgeSessionId == _sessionId;
            // The existing service.restart handler verifies permission, project, PID and maintenance identity on the main thread.
        }

        private Task<bool> SendOriginJsonAsync(string json, CancellationToken token, ConnectionOrigin origin, bool requireReady)
        {
            if (origin == null) return Task.FromResult(false);
            return SendFrameAsync(json, token, origin.Socket, origin.Epoch, origin, requireReady);
        }
    }
}
