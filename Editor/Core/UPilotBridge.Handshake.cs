// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotBridge
    {
        private const string ServerLifetimeSessionKey = "UPilot.ServerLifetime";
        private object _connectionStateLock;
        private object ConnectionStateLock => LazyInitializer.EnsureInitialized(ref _connectionStateLock);
        private long _helloConnectionEpoch;
        private PendingHello _pendingOwnedHello;
        private PendingHello _pendingAuthenticationHello;
        private string _boundServerLifetime;
        private long _lastReceivedHelloEpoch = -1;
        private string _lastReceivedHelloId;
        private UPilotMcpServerManager _lifecycleManager;
        private UPilotMcpServerManager LifecycleManager => _lifecycleManager ?? UPilotMcpServerManager.Instance;

        // 隔离协议应用的外部副作用；正常 Bridge 始终使用真实清理和通知入口。
        internal Func<string, string> ActiveWorkResetOverride;
        internal Action HelloAppliedOverride;

        private sealed class PendingHello
        {
            internal HelloAckMessage Ack;
            internal string SessionId, RequestStack;
            internal CancellationToken Token;
            internal ClientWebSocket Socket;
            internal long ConnectionEpoch, EnqueuedTick;
            internal UPilotMcpServerManager.ServiceShutdownSnapshot Shutdown;
            internal bool WaitingLogged, Discarded;
        }

        private bool IsConnectionCycleCurrentLocked(ClientWebSocket socket, long epoch, CancellationToken token) =>
            _started && !token.IsCancellationRequested && epoch == _helloConnectionEpoch && ReferenceEquals(socket, _ws);

        private bool IsConnectionCycleCurrent(ClientWebSocket socket, long epoch, CancellationToken token)
        {
            lock (ConnectionStateLock) return IsConnectionCycleCurrentLocked(socket, epoch, token);
        }

        private bool TryUpdateConnectionCycle(ClientWebSocket socket, long epoch, CancellationToken token,
            Action update, string sessionId = null)
        {
            lock (ConnectionStateLock)
            {
                if (!IsConnectionCycleCurrentLocked(socket, epoch, token) ||
                    (sessionId != null && sessionId != _sessionId)) return false;
                update();
                return true;
            }
        }

        private bool TryPublishConnectionCycle(ClientWebSocket socket, CancellationToken token,
            out long epoch, out string sessionId, out bool wasAuthenticated)
        {
            lock (ConnectionStateLock)
            {
                epoch = 0; sessionId = _sessionId; wasAuthenticated = false;
                if (!_started || token.IsCancellationRequested || _cts == null || _cts.Token != token) return false;
                epoch = ++_helloConnectionEpoch;
                wasAuthenticated = _isAuthenticated;
                _ws = socket;
                _isAuthenticated = false;
                _admissionClosed = true;
                _pendingAuthenticationHello = null;
                ClearMcpServerDisplayState();
                return true;
            }
        }

        // A cancelled loop owns only its captured socket. It cannot retire a replacement cycle.
        private bool RetireConnectionCycle(ClientWebSocket socket, long epoch)
        {
            bool current, mayClose;
            long currentEpoch;
            lock (ConnectionStateLock)
            {
                currentEpoch = _helloConnectionEpoch;
                current = epoch == currentEpoch && ReferenceEquals(socket, _ws);
                mayClose = current || !ReferenceEquals(socket, _ws);
                if (current)
                {
                    _ws = null;
                    _isAuthenticated = false;
                    _admissionClosed = true;
                    _pendingAuthenticationHello = null;
                    ClearMcpServerDisplayState();
                }
            }
            if (mayClose) AbortAndDisposeWebSocket(socket);
            if (!current)
                Logger.Lifecycle("connection_cycle_retirement_dropped", $"connectionEpoch={epoch} currentConnectionEpoch={currentEpoch} reason=connection_cycle_changed");
            return current;
        }

        private void ReceiveHelloResult(string json, BridgeEnvelope envelope, CancellationToken token,
            ClientWebSocket receivingSocket, long receivingEpoch, UPilotMcpServerManager.ServiceShutdownSnapshot receivingShutdown)
        {
            // A session has one hello exchange; duplicate delivery of its request is not a new decision.
            lock (ConnectionStateLock)
            {
                if (!string.IsNullOrEmpty(envelope.id) && _lastReceivedHelloEpoch == receivingEpoch &&
                    _lastReceivedHelloId == envelope.id) return;
                if (IsConnectionCycleCurrentLocked(receivingSocket, receivingEpoch, token) && envelope.sessionId == _sessionId)
                {
                    _lastReceivedHelloEpoch = receivingEpoch;
                    _lastReceivedHelloId = envelope.id;
                }
            }
            var hello = new PendingHello
            {
                Ack = JsonUtility.FromJson<HelloAckMessage>(json), SessionId = envelope.sessionId,
                Token = token, Socket = receivingSocket, ConnectionEpoch = receivingEpoch,
                Shutdown = receivingShutdown,
                EnqueuedTick = Stopwatch.GetTimestamp(), RequestStack = Logger.CaptureLifecycleStack(),
            };
            Logger.Lifecycle("hello_received", HelloDetail(hello));
            if (!CanApplyHello(hello, out var reason))
            {
                DiscardHello(hello, reason);
                return;
            }
            // 这里只能关闭准入；认证、生命周期绑定和重新放行统一在主线程生效。
            if (!TryUpdateConnectionCycle(hello.Socket, hello.ConnectionEpoch, hello.Token,
                () =>
                {
                    _admissionClosed = true;
                    var payload = hello.Ack?.payload;
                    _pendingAuthenticationHello = payload != null && payload.accepted &&
                        payload.identityContractVersion == IdentityContractVersion &&
                        !string.IsNullOrEmpty(payload.verificationLevel) ? hello : null;
                }, hello.SessionId))
            {
                DiscardHello(hello, "connection_cycle_changed");
                return;
            }
            EnqueueControl(() => ApplyHelloSafely(hello));
            Logger.Lifecycle("hello_enqueued", HelloDetail(hello));
        }

        // Match the Server's existing reload read allowlist. A pending hello never
        // admits a command; original request context and retry budgets stay with the Server.
        private bool IsInfrastructureReadWaitingForHello(string json, BridgeEnvelope command, CancellationToken token,
            ClientWebSocket socket, long epoch)
        {
            if (command.name != "compile.errors.get" && command.name != "resource.editorState" &&
                command.name != "test.status" && command.name != "test.results") return false;
            if (command.name == "test.results")
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(JsonUtility.FromJson<TestResultsMessage>(json)?.payload?.runGuid)) return false;
                }
                catch (ArgumentException) { return false; }
            }
            PendingHello hello;
            lock (ConnectionStateLock)
            {
                hello = _pendingAuthenticationHello;
                if (_isAuthenticated || _resetExecuting || _resettingWork || hello == null || hello.Discarded ||
                    !IsConnectionCycleCurrentLocked(socket, epoch, token) || command.sessionId != _sessionId ||
                    hello.SessionId != command.sessionId || hello.ConnectionEpoch != epoch ||
                    !ReferenceEquals(hello.Socket, socket) || hello.Token != token || hello.Token.IsCancellationRequested) return false;
            }
            if (!CanApplyHello(hello, out _)) return false;
            lock (ConnectionStateLock)
                return ReferenceEquals(_pendingAuthenticationHello, hello) && !hello.Discarded &&
                    !_isAuthenticated && !_resetExecuting && !_resettingWork &&
                    hello.Token == token && !hello.Token.IsCancellationRequested &&
                    IsConnectionCycleCurrentLocked(socket, epoch, token) && command.sessionId == _sessionId;
        }

        private bool CanApplyHello(PendingHello hello, out string reason)
        {
            lock (ConnectionStateLock)
            {
                if (!_started) { reason = "bridge_stopped"; return false; }
                if (hello.Token.IsCancellationRequested) { reason = "connection_cancelled"; return false; }
                if (hello.SessionId != _sessionId) { reason = "session_changed"; return false; }
                if (hello.ConnectionEpoch != _helloConnectionEpoch || !ReferenceEquals(hello.Socket, _ws))
                { reason = "connection_cycle_changed"; return false; }
                if (hello.Socket != null && hello.Socket.State != WebSocketState.Open)
                { reason = "socket_not_open"; return false; }
            }
            return LifecycleManager.CanApplyHello(hello.Shutdown, out reason);
        }

        private static string KnownHelloIdentity(string value) => string.IsNullOrEmpty(value) ? "unknown" : value;

        private string HelloDetail(PendingHello hello)
        {
            return $"source=hello sessionId={KnownHelloIdentity(hello.SessionId)} currentSessionId={KnownHelloIdentity(_sessionId)} " +
                $"connectionEpoch={hello.ConnectionEpoch} currentConnectionEpoch={Interlocked.Read(ref _helloConnectionEpoch)} " +
                $"helloId={KnownHelloIdentity(hello.Ack?.id)} previousServerLifetime={KnownHelloIdentity(_boundServerLifetime)} " +
                $"incomingServerLifetime={KnownHelloIdentity(hello.Ack?.payload?.serverLifecycleId)} serverInstanceId={KnownHelloIdentity(hello.Ack?.payload?.server_instance_id)} " +
                $"started={_started} tokenCancelled={hello.Token.IsCancellationRequested} " +
                $"queueDelayMs={Logger.LifecycleElapsedMs(hello.EnqueuedTick)} {hello.Shutdown.Detail}";
        }

        private void DiscardHello(PendingHello hello, string reason)
        {
            lock (ConnectionStateLock)
            {
                if (hello.Discarded) return;
                hello.Discarded = true;
                if (ReferenceEquals(_pendingOwnedHello, hello)) _pendingOwnedHello = null;
                if (ReferenceEquals(_pendingAuthenticationHello, hello)) _pendingAuthenticationHello = null;
            }
            Logger.Lifecycle("service_reset_blocked",
                $"reason={reason} {HelloDetail(hello)} current={LifecycleManager.CaptureServiceShutdown().Detail}",
                hello.RequestStack, captureExecutionStack: true);
            // A current socket carrying a superseded operation must reconnect, never remain half-authenticated.
            try { hello.Socket?.Abort(); } catch { }
        }

        private void DiscardPendingHello(string reason)
        {
            var hello = _pendingOwnedHello;
            if (hello != null) DiscardHello(hello, reason);
        }

        private void ApplyPendingOwnedHello()
        {
            var hello = _pendingOwnedHello;
            if (hello != null) ApplyHelloSafely(hello);
        }

        private void ApplyHelloSafely(PendingHello hello)
        {
            try { ApplyHello(hello); }
            catch (Exception ex)
            {
                lock (ConnectionStateLock)
                {
                    hello.Discarded = true;
                    if (ReferenceEquals(_pendingOwnedHello, hello)) _pendingOwnedHello = null;
                    if (ReferenceEquals(_pendingAuthenticationHello, hello)) _pendingAuthenticationHello = null;
                }
                TryUpdateConnectionCycle(hello.Socket, hello.ConnectionEpoch, hello.Token, () =>
                {
                    _isAuthenticated = false;
                    _admissionClosed = true;
                    _authenticationErrorCode = "hello_apply_failed";
                    _authenticationError = ex.Message;
                    _authenticationFailureAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }, hello.SessionId);
                Logger.ReportFailure("BRIDGE", $"hello_apply_failed {HelloDetail(hello)}", ex,
                    scope: "hello|" + hello.SessionId);
                try { hello.Socket?.Abort(); } catch { }
            }
        }

        private void ApplyHello(PendingHello hello)
        {
            if (hello.Discarded) return;
            if (!CanApplyHello(hello, out var reason))
            {
                DiscardHello(hello, reason);
                return;
            }
            var payload = hello.Ack?.payload;
            if (payload == null)
            {
                RejectHello(hello, "hello_payload_missing", "协议不兼容：Server 未返回 session.hello payload。");
                return;
            }
            if (!payload.accepted)
            {
                RejectHello(hello, payload.rejectionCode ?? "authentication_rejected",
                    "Bridge 认证被拒绝：" + (payload.rejectionReason ?? "Server 未返回原因"));
                return;
            }
            if (payload.identityContractVersion != IdentityContractVersion || string.IsNullOrEmpty(payload.verificationLevel))
            {
                RejectHello(hello, "identity_contract_mismatch",
                    $"协议不兼容：Bridge 身份契约={IdentityContractVersion}；Server 身份契约={payload.identityContractVersion}。");
                return;
            }

            var serverLifetime = payload.serverLifecycleId;
            var previous = SessionState.GetString(ServerLifetimeSessionKey, "");
            var decision = string.IsNullOrEmpty(serverLifetime) ? "legacy_binding" :
                string.IsNullOrEmpty(previous) ? "first_binding" : previous == serverLifetime ? "same_lifetime" : "lifetime_changed";
            if (hello.Shutdown.ReplacementEligible)
            {
                if (!LifecycleManager.CanReuseOwnedReset(hello.Shutdown, serverLifetime, payload.server_instance_id, out var ownerId))
                {
                    // 新服务的首个 hello 可能早于现有启动观察器的 OS 身份核验。
                    // 只等待当前操作的新握手，使用原期限；旧连接回调永远不进入此等待。
                    if (LifecycleManager.IsOwnedResetVerificationPending(hello.Shutdown))
                    {
                        PendingHello displaced = null;
                        var logWaiting = false;
                        if (!TryUpdateConnectionCycle(hello.Socket, hello.ConnectionEpoch, hello.Token, () =>
                        {
                            displaced = _pendingOwnedHello;
                            _pendingOwnedHello = hello;
                            logWaiting = !hello.WaitingLogged;
                            hello.WaitingLogged = true;
                        }, hello.SessionId))
                        {
                            DiscardHello(hello, "connection_cycle_changed");
                            return;
                        }
                        if (displaced != null && !ReferenceEquals(displaced, hello)) DiscardHello(displaced, "hello_superseded");
                        if (logWaiting) Logger.Lifecycle("hello_waiting_launch_identity", HelloDetail(hello));
                        return;
                    }
                    DiscardHello(hello, "replacement_identity_unverified");
                    return;
                }
                decision = "owned_reset_reused";
                Logger.Lifecycle("hello_reset_receipt_reused", $"ownerId={ownerId} {HelloDetail(hello)}");
            }
            else if (decision == "lifetime_changed")
            {
                var resetStarted = Stopwatch.GetTimestamp();
                Logger.Lifecycle("service_reset_started", $"source=hello previousServerLifetime={previous} {HelloDetail(hello)}");
                var error = ResetActiveWork();
                Logger.Lifecycle("service_reset_finished", $"source=hello elapsedMs={Logger.LifecycleElapsedMs(resetStarted)} error={error} {HelloDetail(hello)}");
            }

            // Cleanup callbacks can synchronously reenter shutdown; never reopen a superseded operation.
            if (!CanApplyHello(hello, out reason))
            {
                DiscardHello(hello, reason);
                return;
            }
            if (!TryUpdateConnectionCycle(hello.Socket, hello.ConnectionEpoch, hello.Token, () =>
            {
                if (!string.IsNullOrEmpty(serverLifetime)) SessionState.SetString(ServerLifetimeSessionKey, serverLifetime);
                _boundServerLifetime = string.IsNullOrEmpty(serverLifetime) ? previous : serverLifetime;
                _serverIdentityContractVersion = payload.identityContractVersion;
                _mcpLabelFromServer = payload.mcpLabel ?? "";
                _mcpHostFromServer = payload.mcpHost ?? "";
                _mcpPortFromServer = payload.mcpPort;
                _mcpWorkspacePathFromServer = payload.mcpWorkingDirectory ?? "";
                _authenticationErrorCode = ""; _authenticationError = ""; _authenticationFailureAtUtcMs = 0;
                _resettingWork = false; _admissionClosed = false; _isAuthenticated = true;
                _lastAuthenticatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (ReferenceEquals(_pendingOwnedHello, hello)) _pendingOwnedHello = null;
                if (ReferenceEquals(_pendingAuthenticationHello, hello)) _pendingAuthenticationHello = null;
            }, hello.SessionId))
            {
                DiscardHello(hello, "connection_cycle_changed");
                return;
            }
            Logger.Lifecycle("hello_applied", $"decision={decision} previousServerLifetime={previous} {HelloDetail(hello)}");
            if (HelloAppliedOverride != null) { HelloAppliedOverride(); return; }
            UPilotStartupDiagnostics.ObserveBridgeAuthenticated(_sessionId);
            UPilotQuickStart.NotifyDeploymentChanged();
            UPilotOperationTracker.Instance.RecordSystemEvent("sys.auth.success", "认证成功",
                $"sessionId={_sessionId} {FormatMcpServerHintForLog()}");
            if (_ws?.State == WebSocketState.Open) _ = SendEditorStateEventAsync(hello.Token);
        }

        private void RejectHello(PendingHello hello, string code, string message)
        {
            if (!TryUpdateConnectionCycle(hello.Socket, hello.ConnectionEpoch, hello.Token, () =>
            {
                if (ReferenceEquals(_pendingOwnedHello, hello)) _pendingOwnedHello = null;
                if (ReferenceEquals(_pendingAuthenticationHello, hello)) _pendingAuthenticationHello = null;
                _isAuthenticated = false;
                _authenticationErrorCode = code; _authenticationError = message;
                _authenticationFailureAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }, hello.SessionId))
            {
                DiscardHello(hello, "connection_cycle_changed");
                return;
            }
            Logger.ReportFailure("BRIDGE", $"hello_rejected reason={code} sessionId={hello.SessionId} {message}",
                scope: "hello|" + hello.SessionId);
            UPilotQuickStart.NotifyDeploymentChanged();
            try { hello.Socket?.Abort(); } catch { }
        }
    }
}
