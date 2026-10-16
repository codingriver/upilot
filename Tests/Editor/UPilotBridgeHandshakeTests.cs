// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    // EditMode runs synchronously; every case restores the shared SessionState before yielding.
    public sealed class UPilotBridgeHandshakeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string LifetimeKey = "UPilot.ServerLifetime";
        private string _savedLifetime;
        private Logger.LogLevel _savedLevel;
        private bool _savedConsole;
        private StackTraceLogType _savedStack;
        private UPilotBridge _bridge;
        private UPilotMcpServerManager _manager;
        private ConcurrentQueue<Action> _queue;
        private int _resetCount;
        private int _dispatchCount;
        private readonly List<string> _blocked = new();
        private readonly List<string> _retirementDrops = new();

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static T Get<T>(object target, string name) =>
            (T)target.GetType().GetField(name, Private).GetValue(target);

        [SetUp]
        public void SetUp()
        {
            _savedLifetime = SessionState.GetString(LifetimeKey, "");
            _savedLevel = Logger.MinLevel;
            _savedConsole = Logger.LogToUnityConsole;
            _savedStack = Application.GetStackTraceLogType(LogType.Log);
            Logger.MinLevel = Logger.LogLevel.Error;
            Logger.LogToUnityConsole = false;
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            SessionState.SetString(LifetimeKey, "old-lifetime");
            _bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            _manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            _queue = new ConcurrentQueue<Action>();
            Set(_bridge, "_controlQueue", _queue);
            Set(_bridge, "_lifecycleManager", _manager);
            Set(_bridge, "_sessionId", "owned-session");
            Set(_bridge, "_started", true);
            Set(_bridge, "_isAuthenticated", false);
            Set(_bridge, "_admissionClosed", true);
            Set(_bridge, "_mainThreadQueue", new ConcurrentQueue<Action>());
            Set(_bridge, "_compileService", FormatterServices.GetUninitializedObject(typeof(UPilotCompileService)));
            _resetCount = 0;
            _dispatchCount = 0;
            var router = new UPilotCommandRouter();
            foreach (var command in new[] { "compile.errors.get", "resource.editorState", "test.status", "test.results",
                "compile.request", "test.run", "service.restart" })
                router.Register(command, (id, json, token) => { ++_dispatchCount; return Task.CompletedTask; });
            Set(_bridge, "_router", router);
            _blocked.Clear();
            _retirementDrops.Clear();
            _bridge.ActiveWorkResetOverride = _ => { ++_resetCount; return "partial-cleanup-error"; };
            _bridge.HelloAppliedOverride = () => { };
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;
            SessionState.SetString(LifetimeKey, _savedLifetime);
            Logger.MinLevel = _savedLevel;
            Logger.LogToUnityConsole = _savedConsole;
            Application.SetStackTraceLogType(LogType.Log, _savedStack);
        }

        private void Capture(string message, string stack, LogType type)
        {
            if (type == LogType.Log && message.Contains("event=service_reset_blocked")) _blocked.Add(message);
            if (type == LogType.Log && message.Contains("event=connection_cycle_retirement_dropped")) _retirementDrops.Add(message);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetiringOldCycleCannotCloseCurrentSocketOrClearItsAuthentication(bool sameSocketNewEpoch)
        {
            using var oldSocket = new ClientWebSocket();
            using var replacementSocket = new ClientWebSocket();
            var currentSocket = sameSocketNewEpoch ? oldSocket : replacementSocket;
            Set(_bridge, "_ws", currentSocket);
            Set(_bridge, "_helloConnectionEpoch", 2L);
            Set(_bridge, "_isAuthenticated", true);
            Set(_bridge, "_admissionClosed", false);
            Set(_bridge, "_mcpLabelFromServer", "current-server");
            Set(_bridge, "_lastHeartbeatSentAt", 123L);
            var retired = (bool)typeof(UPilotBridge).GetMethod("RetireConnectionCycle", Private).Invoke(
                _bridge, new object[] { oldSocket, 1L });
            Assert.That(retired, Is.False);
            Assert.That(Get<ClientWebSocket>(_bridge, "_ws"), Is.SameAs(currentSocket));
            Assert.That(currentSocket.State, Is.EqualTo(WebSocketState.None), "The replacement socket must never be aborted or disposed.");
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.False);
            Assert.That(Get<string>(_bridge, "_mcpLabelFromServer"), Is.EqualTo("current-server"));
            Assert.That(Get<long>(_bridge, "_lastHeartbeatSentAt"), Is.EqualTo(123));
            Assert.That(Get<long>(_bridge, "_helloConnectionEpoch"), Is.EqualTo(2));
            Assert.That(_retirementDrops.Count, Is.EqualTo(1));
            Assert.That(_retirementDrops[0], Does.Contain("connectionEpoch=1 currentConnectionEpoch=2"));
        }

        [Test]
        public void RetiringCurrentCycleClosesOnlyItsOwnAuthenticationAndAdmission()
        {
            using var socket = new ClientWebSocket();
            Set(_bridge, "_ws", socket);
            Set(_bridge, "_helloConnectionEpoch", 2L);
            Set(_bridge, "_isAuthenticated", true);
            Set(_bridge, "_admissionClosed", false);
            var retired = (bool)typeof(UPilotBridge).GetMethod("RetireConnectionCycle", Private).Invoke(
                _bridge, new object[] { socket, 2L });
            Assert.That(retired, Is.True);
            Assert.That(Get<ClientWebSocket>(_bridge, "_ws"), Is.Null);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(_retirementDrops, Is.Empty);
        }

        [TestCase("token")]
        [TestCase("socket")]
        [TestCase("epoch")]
        [TestCase("session")]
        [TestCase("stopped")]
        public void LateConnectionContinuationCannotMutateCurrentState(string changed)
        {
            using var oldSocket = new ClientWebSocket();
            using var replacementSocket = new ClientWebSocket();
            using var cancellation = new CancellationTokenSource();
            Set(_bridge, "_ws", changed == "socket" ? replacementSocket : oldSocket);
            Set(_bridge, "_helloConnectionEpoch", changed == "epoch" ? 2L : 1L);
            Set(_bridge, "_isAuthenticated", true);
            Set(_bridge, "_admissionClosed", false);
            if (changed == "token") cancellation.Cancel();
            if (changed == "session") Set(_bridge, "_sessionId", "replacement-session");
            if (changed == "stopped") Set(_bridge, "_started", false);
            var changedState = false;
            Action update = () => { changedState = true; Set(_bridge, "_isAuthenticated", false); };
            var applied = (bool)typeof(UPilotBridge).GetMethod("TryUpdateConnectionCycle", Private).Invoke(
                _bridge, new object[] { oldSocket, 1L, cancellation.Token, update, "owned-session" });
            Assert.That(applied, Is.False);
            Assert.That(changedState, Is.False);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.False);
        }

        [Test]
        public void SupersededLoopTokenCannotPublishAConnectionOverTheCurrentCycle()
        {
            using var oldSocket = new ClientWebSocket();
            using var currentSocket = new ClientWebSocket();
            using var oldCancellation = new CancellationTokenSource();
            using var currentCancellation = new CancellationTokenSource();
            Set(_bridge, "_cts", currentCancellation);
            Set(_bridge, "_ws", currentSocket);
            Set(_bridge, "_helloConnectionEpoch", 2L);
            Set(_bridge, "_isAuthenticated", true);
            Set(_bridge, "_admissionClosed", false);
            var arguments = new object[] { oldSocket, oldCancellation.Token, 0L, null, false };
            var published = (bool)typeof(UPilotBridge).GetMethod("TryPublishConnectionCycle", Private).Invoke(_bridge, arguments);
            Assert.That(published, Is.False);
            Assert.That(Get<ClientWebSocket>(_bridge, "_ws"), Is.SameAs(currentSocket));
            Assert.That(Get<long>(_bridge, "_helloConnectionEpoch"), Is.EqualTo(2));
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.False);
        }

        private void Receive(string lifetime = "new-lifetime", CancellationToken token = default, string invalidAck = null)
        {
            var ack = new HelloAckMessage
            {
                id = "owned-hello", type = "result", name = "session.hello", sessionId = "owned-session",
                payload = new HelloAckPayload { accepted = true, identityContractVersion = 1,
                    verificationLevel = "windows-pid-creation-executable-role-project", serverLifecycleId = lifetime,
                    server_instance_id = "owned-instance" }
            };
            if (invalidAck == "missing_payload") ack.payload = null;
            else if (invalidAck == "rejected") ack.payload.accepted = false;
            else if (invalidAck == "identity_contract") ack.payload.identityContractVersion = 2;
            else if (invalidAck == "verification_missing") ack.payload.verificationLevel = "";
            var json = JsonUtility.ToJson(ack);
            var task = (Task)typeof(UPilotBridge).GetMethod("HandleMessageJsonAsync", Private).Invoke(
                _bridge, new object[] { json, JsonUtility.FromJson<BridgeEnvelope>(json), token });
            Assert.That(task.IsCompleted, Is.True, "Hello receipt must not wait for the Editor pump.");
            task.GetAwaiter().GetResult();
        }

        private void Pump()
        {
            while (_queue.TryDequeue(out var action)) action();
        }

        private void ReceiveCommand(string name, CancellationToken token = default, string runGuid = "owned-run")
        {
            var envelope = new BridgeEnvelope { id = Guid.NewGuid().ToString("N"), type = "command",
                name = name, sessionId = "owned-session" };
            var json = JsonUtility.ToJson(envelope);
            if (name == "test.results" && runGuid != null)
            {
                var payload = JsonUtility.ToJson(new TestResultsMessage { payload = new TestResultsPayload { runGuid = runGuid } });
                json = json.Substring(0, json.Length - 1) + "," + payload.Substring(1);
            }
            var task = (Task)typeof(UPilotBridge).GetMethod("HandleMessageJsonAsync", Private).Invoke(
                _bridge, new object[] { json, envelope, token });
            Assert.That(task.IsCompleted, Is.True, "Pre-auth classification must not wait for the Editor pump.");
            task.GetAwaiter().GetResult();
        }

        private bool IsReadWaiting(CancellationToken token = default, ClientWebSocket socket = null,
            long epoch = 0, string sessionId = "owned-session") =>
            (bool)typeof(UPilotBridge).GetMethod("IsInfrastructureReadWaitingForHello", Private).Invoke(
                _bridge, new object[] { "", new BridgeEnvelope { name = "compile.errors.get", sessionId = sessionId },
                    token, socket, epoch });

        private static void ExpectLifecycle(params string[] events)
        {
            foreach (var eventName in events)
                LogAssert.Expect(LogType.Log, new Regex(@"^\[UPilot\]\[Lifecycle\][^\r\n]* event=" +
                    Regex.Escape(eventName) + @"\n"));
        }

        [TestCase("compile.errors.get")]
        [TestCase("resource.editorState")]
        [TestCase("test.status")]
        [TestCase("test.results")]
        public void ReadAfterAcceptedHelloWaitsWithoutDispatchUntilMainThreadAuthentication(string command)
        {
            Logger.MinLevel = Logger.LogLevel.Warning;
            ExpectLifecycle("hello_received", "hello_enqueued");
            Receive();
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Not.Null);
            LogAssert.Expect(LogType.Warning, new Regex("\\[UPILOT_RECONNECT_PENDING\\][\\s\\S]*cmd=" + Regex.Escape(command)));
            ReceiveCommand(command);
            Assert.That(_dispatchCount, Is.Zero);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(_resetCount, Is.Zero);
            ExpectLifecycle("service_reset_started", "service_reset_finished", "hello_applied");
            Pump();
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Null);
            ReceiveCommand(command);
            Assert.That(_dispatchCount, Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("missing_payload")]
        [TestCase("rejected")]
        [TestCase("identity_contract")]
        [TestCase("verification_missing")]
        public void InvalidHelloDoesNotClassifyAuthenticationFailureAsReconnectPending(string invalidAck)
        {
            ExpectLifecycle("hello_received", "hello_enqueued");
            Receive(invalidAck: invalidAck);
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Null);
            Assert.That(IsReadWaiting(), Is.False);
            LogAssert.Expect(LogType.Error, new Regex("\\[INTERNAL_ERROR\\][\\s\\S]*cmd=compile\\.errors\\.get"));
            ReceiveCommand("compile.errors.get");
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(_dispatchCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("hello_token")]
        [TestCase("command_token")]
        [TestCase("different_command_token")]
        [TestCase("command_session")]
        [TestCase("command_epoch")]
        [TestCase("command_socket")]
        [TestCase("current_session")]
        [TestCase("current_epoch")]
        [TestCase("current_socket")]
        [TestCase("stopped")]
        [TestCase("shutdown_generation")]
        public void PendingClassificationRequiresCurrentHelloCommandAndShutdownContext(string changed)
        {
            using var helloCancellation = new CancellationTokenSource();
            using var commandCancellation = new CancellationTokenSource();
            using var otherSocket = new ClientWebSocket();
            Receive(token: helloCancellation.Token);
            ClientWebSocket socket = null;
            long epoch = 0;
            string sessionId = "owned-session";
            var commandToken = helloCancellation.Token;
            switch (changed)
            {
                case "hello_token": helloCancellation.Cancel(); break;
                case "command_token": commandCancellation.Cancel(); commandToken = commandCancellation.Token; break;
                case "different_command_token": commandToken = commandCancellation.Token; break;
                case "command_session": sessionId = "old-session"; break;
                case "command_epoch": epoch = -1; break;
                case "command_socket": socket = otherSocket; break;
                case "current_session": Set(_bridge, "_sessionId", "replacement-session"); break;
                case "current_epoch": Set(_bridge, "_helloConnectionEpoch", 1L); break;
                case "current_socket": Set(_bridge, "_ws", otherSocket); break;
                case "stopped": Set(_bridge, "_started", false); break;
                case "shutdown_generation":
                    _manager.BeginServiceShutdown("owned-stop", "stopping");
                    _manager.FinishServiceShutdown("owned-stop", "finished"); break;
            }
            Assert.That(IsReadWaiting(commandToken, socket, epoch, sessionId), Is.False);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
        }

        [TestCase("compile.request")]
        [TestCase("test.run")]
        [TestCase("service.restart")]
        public void PendingHelloDoesNotReclassifyBusinessCommands(string command)
        {
            ExpectLifecycle("hello_received", "hello_enqueued");
            Receive();
            LogAssert.Expect(LogType.Error, new Regex("\\[INTERNAL_ERROR\\][\\s\\S]*cmd=" + Regex.Escape(command)));
            ReceiveCommand(command);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(_dispatchCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void PendingHelloDoesNotClassifyLatestOrEmptyRunTestResultsAsRetryable(string runGuid)
        {
            ExpectLifecycle("hello_received", "hello_enqueued");
            Receive();
            LogAssert.Expect(LogType.Error, new Regex("\\[INTERNAL_ERROR\\][\\s\\S]*cmd=test\\.results"));
            ReceiveCommand("test.results", runGuid: runGuid);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(_dispatchCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("_resetExecuting")]
        [TestCase("_resettingWork")]
        public void ActiveResetKeepsPriorityOverPendingAuthentication(string resetField)
        {
            ExpectLifecycle("hello_received", "hello_enqueued");
            Receive();
            Set(_bridge, resetField, true);
            Assert.That(IsReadWaiting(), Is.False);
            LogAssert.Expect(LogType.Error, new Regex("\\[UPILOT_RESET_IN_PROGRESS\\][\\s\\S]*cmd=compile\\.errors\\.get"));
            ReceiveCommand("compile.errors.get");
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(_dispatchCount, Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OwnedLaunchVerificationRetainsPendingReadClassificationOnlyWithinOriginalDeadline()
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            _manager.ServiceWorkResetOverride = _ => "";
            _manager.BeginServiceShutdown("owner", "stopping", deadline);
            _manager.ResetActiveWorkForServiceOwner("owner", "maintenance");
            Set(_manager, "_launchState", new ServerLaunchState { deadlineUtcMs = deadline });
            typeof(UPilotMcpServerManager).GetMethod("AllowServiceReplacement", Private).Invoke(
                _manager, new object[] { "owner" });
            Receive();
            Pump();
            Assert.That(Get<object>(_bridge, "_pendingOwnedHello"), Is.Not.Null);
            Assert.That(IsReadWaiting(), Is.True);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(_manager.CaptureServiceShutdown().DeadlineUtcMs, Is.EqualTo(deadline));
            Set(_manager, "_shutdownDeadlineUtcMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
            Assert.That(IsReadWaiting(), Is.False);
        }

        private void AssertDiscardedOnce()
        {
            Assert.That(_resetCount, Is.Zero);
            Assert.That(SessionState.GetString(LifetimeKey, ""), Is.EqualTo("old-lifetime"));
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Null);
            Assert.That(_blocked.Count, Is.EqualTo(1));
            Assert.That(_blocked[0], Does.Contain("requestStack:\n").And.Contain("executionStack:\n"));
            using var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var contents = reader.ReadToEnd();
            Assert.That(contents, Does.Contain(_blocked[0]));
            var eventId = _blocked[0].Split(new[] { "eventId=" }, StringSplitOptions.None)[1].Substring(0, 32);
            Assert.That(contents.Split(new[] { eventId }, StringSplitOptions.None).Length - 1, Is.EqualTo(1));
        }

        [TestCase("", "new-lifetime", 0)]
        [TestCase("new-lifetime", "new-lifetime", 0)]
        [TestCase("old-lifetime", "new-lifetime", 1)]
        [TestCase("old-lifetime", "", 0)]
        public void ActualHelloCommitsOnlyOnMainThreadAndResetsOnlyForKnownLifetimeChange(
            string previous, string incoming, int resets)
        {
            SessionState.SetString(LifetimeKey, previous);
            Receive(incoming);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(SessionState.GetString(LifetimeKey, ""), Is.EqualTo(previous));
            Pump();
            Assert.That(_resetCount, Is.EqualTo(resets));
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.False);
            Assert.That(SessionState.GetString(LifetimeKey, ""), Is.EqualTo(incoming == "" ? previous : incoming));
            Assert.That(_blocked, Is.Empty);
        }

        [TestCase("normal_stop")]
        [TestCase("forced_stop")]
        [TestCase("bridge_close")]
        public void AlreadyQueuedHelloCannotResetOrBindDuringAnyShutdown(string phase)
        {
            Receive();
            Assert.That(_queue.TryDequeue(out var callback), Is.True);
            if (phase == "bridge_close") _manager.MarkBridgeClosing();
            else _manager.BeginServiceShutdown("owned-stop", phase);
            callback();
            callback();
            AssertDiscardedOnce();
        }

        [Test]
        public void HelloArrivingDuringShutdownIsDiscardedBeforeEnqueue()
        {
            _manager.BeginServiceShutdown("owned-stop", "stop_preparing");
            Receive();
            Assert.That(_queue.IsEmpty, Is.True);
            AssertDiscardedOnce();
        }

        [Test]
        public void DuplicateBlockedProtocolRequestProducesOnlyOneDiagnostic()
        {
            _manager.BeginServiceShutdown("owned-stop", "stop_preparing");
            Receive();
            Receive();
            AssertDiscardedOnce();
        }

        [Test]
        public void ApplicationFailureRemainsAnErrorAndDoesNotOpenAdmission()
        {
            _bridge.ActiveWorkResetOverride = _ => throw new IOException("owned-hello-apply-failure");
            ExpectLifecycle("hello_received", "hello_enqueued", "service_reset_started");
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex("hello_apply_failed[\\s\\S]*owned-hello-apply-failure"));
            Receive();
            Pump();
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(Get<string>(_bridge, "_authenticationErrorCode"), Is.EqualTo("hello_apply_failed"));
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Null);
            Assert.That(SessionState.GetString(LifetimeKey, ""), Is.EqualTo("old-lifetime"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void CleanupReenteringShutdownCannotCommitAuthentication()
        {
            _bridge.ActiveWorkResetOverride = _ => {
                _manager.BeginServiceShutdown("reentrant-stop", "stop_preparing");
                return "cleanup-reentered-shutdown";
            };
            Receive();
            Pump();
            AssertDiscardedOnce();
        }

        [Test]
        public void ReceiveContextFromOldSocketCycleIsNotRelabelledAsCurrent()
        {
            Set(_bridge, "_helloConnectionEpoch", 2L);
            var ack = new HelloAckMessage { type = "result", name = "session.hello", sessionId = "owned-session",
                payload = new HelloAckPayload { accepted = true, identityContractVersion = 1,
                    verificationLevel = "verified", serverLifecycleId = "new-lifetime" } };
            var json = JsonUtility.ToJson(ack);
            var task = (Task)typeof(UPilotBridge).GetMethod("HandleReceivedMessageAsync", Private).Invoke(
                _bridge, new object[] { json, JsonUtility.FromJson<BridgeEnvelope>(json), CancellationToken.None, null, 1L,
                    _manager.CaptureServiceShutdown() });
            task.GetAwaiter().GetResult();
            Assert.That(_queue.IsEmpty, Is.True);
            AssertDiscardedOnce();
            Assert.That(_blocked[0], Does.Contain("reason=connection_cycle_changed"));
        }

        [Test]
        public void HelloFromPreStopConnectionCannotAdoptFinishedOperationsGeneration()
        {
            var connectionShutdown = _manager.CaptureServiceShutdown();
            _manager.BeginServiceShutdown("owned-stop", "stopping");
            _manager.FinishServiceShutdown("owned-stop", "finished");
            var ack = new HelloAckMessage { id = "old-wire-hello", type = "result", name = "session.hello", sessionId = "owned-session",
                payload = new HelloAckPayload { accepted = true, identityContractVersion = 1,
                    verificationLevel = "verified", serverLifecycleId = "new-lifetime" } };
            var json = JsonUtility.ToJson(ack);
            var task = (Task)typeof(UPilotBridge).GetMethod("HandleReceivedMessageAsync", Private).Invoke(
                _bridge, new object[] { json, JsonUtility.FromJson<BridgeEnvelope>(json), CancellationToken.None,
                    null, 0L, connectionShutdown });
            task.GetAwaiter().GetResult();
            Assert.That(_queue.IsEmpty, Is.True);
            AssertDiscardedOnce();
            Assert.That(_blocked[0], Does.Contain("reason=shutdown_generation_changed"));
        }

        [TestCase("connection_cancelled")]
        [TestCase("session_changed")]
        [TestCase("connection_cycle_changed")]
        [TestCase("socket_changed")]
        [TestCase("bridge_stopped")]
        [TestCase("shutdown_finished")]
        public void LateCallbackAfterNewCycleCannotReopenAdmission(string change)
        {
            using var cts = new CancellationTokenSource();
            using var replacementSocket = new ClientWebSocket();
            Receive(token: cts.Token);
            switch (change)
            {
                case "connection_cancelled": cts.Cancel(); break;
                case "session_changed": Set(_bridge, "_sessionId", "replacement-session"); break;
                case "connection_cycle_changed": Set(_bridge, "_helloConnectionEpoch", 1L); break;
                case "socket_changed": Set(_bridge, "_ws", replacementSocket); break;
                case "bridge_stopped": Set(_bridge, "_started", false); break;
                case "shutdown_finished":
                    _manager.BeginServiceShutdown("owned-stop", "stopping");
                    _manager.FinishServiceShutdown("owned-stop", "new_start"); break;
            }
            Pump();
            AssertDiscardedOnce();
        }

        [Test]
        public void NewOwnedHelloWaitsForOriginalLaunchProofThenReusesSinglePartialCleanup()
        {
            int ownedResets = 0;
            _manager.ServiceWorkResetOverride = _ => { ++ownedResets; return "partial-cleanup-error"; };
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            _manager.BeginServiceShutdown("owner", "stopping", deadline);
            _manager.ResetActiveWorkForServiceOwner("owner", "maintenance");
            var launch = new ServerLaunchState { deadlineUtcMs = deadline };
            Set(_manager, "_launchState", launch);
            typeof(UPilotMcpServerManager).GetMethod("AllowServiceReplacement", Private).Invoke(
                _manager, new object[] { "owner" });
            Receive();
            Pump();
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.False);
            Assert.That(Get<object>(_bridge, "_pendingOwnedHello"), Is.Not.Null);
            using var process = Process.GetCurrentProcess();
            launch.servicePid = process.Id;
            launch.serviceCreatedAt = process.StartTime.ToUniversalTime().Ticks;
            launch.instanceId = "owned-instance";
            launch.verified = true;
            Set(_manager, "_trackedProcess", process);
            typeof(UPilotBridge).GetMethod("ApplyPendingOwnedHello", Private).Invoke(_bridge, null);
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<object>(_bridge, "_pendingOwnedHello"), Is.Null);
            Assert.That(Get<object>(_bridge, "_pendingAuthenticationHello"), Is.Null);
            Assert.That(ownedResets, Is.EqualTo(1));
            Assert.That(_resetCount, Is.Zero);
            Assert.That(_manager.CaptureServiceShutdown().DeadlineUtcMs, Is.EqualTo(deadline));
            Assert.That(_blocked, Is.Empty);
        }
    }
}
