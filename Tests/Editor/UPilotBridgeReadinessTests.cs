// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // In-memory protocol tests. No Bridge constructor, Editor callbacks, real sockets or state-file writes.
    // Public IsStateSynchronized/receipt success and actual sends need a real Open ClientWebSocket.
    public sealed class UPilotBridgeReadinessTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Session = "readiness-fixture-session";
        private const string Hello = "readiness-fixture-hello";
        private const string ServerLifetime = "readiness-fixture-lifetime";
        private const string ServerInstance = "readiness-fixture-instance";
        private const string Nonce = "0123456789abcdef0123456789abcdef";
        private UPilotBridge _bridge;
        private ConcurrentQueue<Action> _control, _main;

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Private).SetValue(target, value);
        private static T Get<T>(object target, string field) =>
            (T)target.GetType().GetField(field, Private).GetValue(target);
        private static object Call(object target, string method, params object[] args)
        {
            try { return target.GetType().GetMethod(method, Private).Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        [SetUp]
        public void SetUp()
        {
            _bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            _control = new ConcurrentQueue<Action>();
            _main = new ConcurrentQueue<Action>();
            Set(_bridge, "_controlQueue", _control);
            Set(_bridge, "_mainThreadQueue", _main);
            Set(_bridge, "_trackedQueueActions", new ConcurrentDictionary<Action, string>());
            Set(_bridge, "_queueObservationLock", new object());
            Set(_bridge, "_started", true);
            Set(_bridge, "_isAuthenticated", true);
            Set(_bridge, "_sessionId", Session);
            Set(_bridge, "_helloConnectionEpoch", 7L);
            Set(_bridge, "_boundServerLifetime", ServerLifetime);
            Set(_bridge, "_readyHelloId", Hello);
            Set(_bridge, "_readyServerInstanceId", ServerInstance);
            Set(_bridge, "_readyProjectPath", "D:/upilot/Tests~/UPilotTest");
            Set(_bridge, "_readyDeadlineTick", Stopwatch.GetTimestamp() + 60L * Stopwatch.Frequency);
            Set(_bridge, "_localReadyAnnounced", true);
            Set(_bridge, "_admissionClosed", true);
            Set(_bridge, "_producerEpoch", "producer-fixture");
            Set(_bridge, "_domainGeneration", 3L);
        }

        private BridgeBootstrapMessage Message(bool ack = true) => new BridgeBootstrapMessage {
            id = ack ? "bootstrap-ack-fixture" : "bootstrap-fixture",
            type = ack ? "event" : "command", name = ack ? "session.bootstrap.ack" : "session.bootstrap",
            sessionId = Session,
            payload = new BridgeBootstrapPayload {
                bridgeReadyContractVersion = UPilotBridge.BridgeReadyContractVersion,
                helloId = Hello, serverLifecycleId = ServerLifetime, serverInstanceId = ServerInstance,
                bootstrapId = "bootstrap-fixture", nonce = Nonce, snapshotId = "snapshot-fixture",
                producerEpoch = "producer-fixture", domainGeneration = 3, sequence = 11,
            },
        };

        private void PrepareSnapshot()
        {
            Set(_bridge, "_bootstrapRequestId", "bootstrap-fixture");
            Set(_bridge, "_bootstrapNonce", Nonce);
            Set(_bridge, "_bootstrapSnapshotId", "snapshot-fixture");
            Set(_bridge, "_bootstrapSnapshot", new EditorExecutionStatePayload {
                snapshotId = "snapshot-fixture", producerEpoch = "producer-fixture", domainGeneration = 3,
                sequence = 11, processId = 42, processCreatedAt = 12345,
            });
        }

        private bool Receive(BridgeBootstrapMessage message, CancellationToken token = default,
            ClientWebSocket socket = null, long epoch = 7) =>
            (bool)Call(_bridge, "TryHandleReadinessControl", JsonUtility.ToJson(message), message, token, socket, epoch);

        private object Origin(CancellationToken token = default) => Call(_bridge, "CaptureOrigin", token);
        private bool Current(object origin, bool requireReady = true) =>
            (bool)Call(_bridge, "IsOriginCurrent", origin, requireReady);

        private void SetRequestOrigin(object origin)
        {
            var local = typeof(UPilotBridge).GetProperty("RequestOrigin", Private).GetValue(_bridge);
            local.GetType().GetProperty("Value").SetValue(local, origin);
        }

        [Test]
        public void UnknownBootstrapSerializesMissingLedgersAsNullWithoutChangingDiagnosticText()
        {
            var text = "\"compileEvidence\":" + JsonUtility.ToJson(new CompileEvidencePayload());
            var message = new ResultMessage<BridgeBootstrapResult> { payload = new BridgeBootstrapResult {
                state = new EditorExecutionStatePayload { compilePhase = "unknown", compileEvidence = null, nextAction = text },
                compileEvidence = new CompileErrorsPayload { phase = "unknown", compileEvidence = null },
            } };
            var json = UPilotBridge.SerializeBootstrapResult(message);
            // JsonUtility also materializes ResultMessage.context and its absent ledger.
            Assert.That(json.Split(new[] { "\"compileEvidence\":null" }, StringSplitOptions.None).Length - 1, Is.EqualTo(3));
            Assert.That(JsonUtility.FromJson<ResultMessage<BridgeBootstrapResult>>(json).payload.state.nextAction, Is.EqualTo(text));
        }

        [Test]
        public void ContextProjectsOneCompileSnapshotWithoutReadingNewerServiceFields()
        {
            Set(_bridge, "_lastMainThreadPumpAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var evidence = new CompileEvidencePayload { compileOperationId = "observed-compile", revision = 6 };
            var compile = new CompileErrorsPayload {
                requestId = "observed-request", compileOperationId = "observed-compile", writeBatchId = "observed-batch",
                phase = "completed", status = "finished", terminal = true, errorsVerified = true,
                startedAt = 100, finishedAt = 200, lastCompileStartedAt = 100, lastCompilerFinishedAt = 200,
                lastCompileVerifiedAt = 300, lastTerminalCompileAt = 300, lastProgressAt = 300,
                compileEvidence = evidence,
            };
            // There is deliberately no service on this Bridge: every compile fact must come from the captured snapshot.
            var context = (EditorContextPayload)Call(_bridge, "BuildEditorContextFromCompile", "fixture", compile);
            Assert.That(context.compileEvidence, Is.SameAs(evidence));
            Assert.That(context.compilePhase, Is.EqualTo("completed"));
            Assert.That(context.compileOperationId, Is.EqualTo(evidence.compileOperationId));
            Assert.That(context.compileRequestId, Is.EqualTo("observed-request"));
            Assert.That(context.writeBatchId, Is.EqualTo("observed-batch"));
            Assert.That(context.compileStartedAt, Is.EqualTo(100));
            Assert.That(context.lastCompilerFinishedAt, Is.EqualTo(200));
            Assert.That(context.lastCompileVerifiedAt, Is.EqualTo(300));
            Assert.That(context.terminal && context.errorsVerified, Is.True);

            compile.phase = "unknown"; compile.status = "unknown";
            compile.compileEvidence = null; compile.terminal = false; compile.errorsVerified = false;
            context = (EditorContextPayload)Call(_bridge, "BuildEditorContextFromCompile", "fixture", compile);
            Assert.That(context.compileEvidence, Is.Null);
            Assert.That(context.compilePhase, Is.EqualTo("unknown"));
            Assert.That(context.terminal || context.errorsVerified || context.verificationPending || context.isCompiling, Is.False);
        }

        [Test]
        public void AuthenticationAndLocalReadyNoticeDoNotAdmitOrdinaryCommandsBeforeAck()
        {
            var origin = Origin();
            Assert.That(Get<bool>(_bridge, "_isAuthenticated"), Is.True);
            Assert.That(Get<bool>(_bridge, "_localReadyAnnounced"), Is.True);
            Assert.That(Current(origin, false), Is.True, "Bootstrap controls retain their exact current origin.");
            Assert.That(Current(origin), Is.False);
            foreach (var command in new[] { "editor.state", "compile.errors.get", "test.run", "csharp.eval" })
                Assert.That(Call(_bridge, "GetAdmissionErrorCode", command), Is.EqualTo("UPILOT_RECONNECT_PENDING"));
            Assert.That(_bridge.TryGetBootstrapReceipt(Session, ServerInstance, out _), Is.False);
        }

        [Test]
        public void ExactAckOpensMemoryGateOnceAndRetainsSnapshotIdentity()
        {
            PrepareSnapshot();
            var origin = Origin();
            var ack = Message();
            Assert.That(Receive(ack), Is.True);
            Assert.That(Get<bool>(_bridge, "_stateSynchronized"), Is.True);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.False);
            Assert.That(Current(origin), Is.True);
            var receipt = Get<BridgeBootstrapReceipt>(_bridge, "_bootstrapReceipt");
            Assert.That(receipt.SessionId, Is.EqualTo(Session));
            Assert.That(receipt.ServerLifecycleId, Is.EqualTo(ServerLifetime));
            Assert.That(receipt.ServerInstanceId, Is.EqualTo(ServerInstance));
            Assert.That(receipt.ConnectionEpoch, Is.EqualTo(7));
            Assert.That(receipt.ProcessId, Is.EqualTo(42));
            Assert.That(receipt.ProcessCreatedAt, Is.EqualTo(12345));
            Assert.That(receipt.SnapshotId, Is.EqualTo("snapshot-fixture"));
            Assert.That(receipt.BootstrapId, Is.EqualTo("bootstrap-fixture"));
            Assert.That(_control.Count, Is.EqualTo(1));
            Receive(ack);
            Assert.That(_control.Count, Is.EqualTo(1), "Duplicate ACK cannot schedule a second Ready completion.");
            Assert.That(Get<BridgeBootstrapReceipt>(_bridge, "_bootstrapReceipt"), Is.SameAs(receipt));
            // Memory synchronization alone never claims a live network connection.
            Assert.That(_bridge.IsStateSynchronized, Is.False);
            Assert.That(_bridge.TryGetBootstrapReceipt(Session, ServerInstance, out _), Is.False);
        }

        [TestCase("contract")]
        [TestCase("session")]
        [TestCase("epoch")]
        [TestCase("hello")]
        [TestCase("lifetime")]
        [TestCase("instance")]
        [TestCase("bootstrap")]
        [TestCase("nonce")]
        [TestCase("snapshot")]
        [TestCase("producer")]
        [TestCase("domain")]
        [TestCase("sequence")]
        [TestCase("expired")]
        [TestCase("unauthenticated")]
        [TestCase("not_announced")]
        [TestCase("missing_snapshot")]
        [TestCase("reset")]
        public void StaleMismatchedOrIncompleteAckCannotOpenAdmission(string mismatch)
        {
            PrepareSnapshot();
            var ack = Message();
            var epoch = 7L;
            switch (mismatch)
            {
                case "contract": ack.payload.bridgeReadyContractVersion++; break;
                case "session": ack.sessionId = "older-session"; break;
                case "epoch": epoch--; break;
                case "hello": ack.payload.helloId = "older-hello"; break;
                case "lifetime": ack.payload.serverLifecycleId = "older-lifetime"; break;
                case "instance": ack.payload.serverInstanceId = "older-instance"; break;
                case "bootstrap": ack.payload.bootstrapId = "older-bootstrap"; break;
                case "nonce": ack.payload.nonce = new string('a', 32); break;
                case "snapshot": ack.payload.snapshotId = "older-snapshot"; break;
                case "producer": ack.payload.producerEpoch = "older-producer"; break;
                case "domain": ack.payload.domainGeneration--; break;
                case "sequence": ack.payload.sequence--; break;
                case "expired": Set(_bridge, "_readyDeadlineTick", Stopwatch.GetTimestamp() - 1); break;
                case "unauthenticated": Set(_bridge, "_isAuthenticated", false); break;
                case "not_announced": Set(_bridge, "_localReadyAnnounced", false); break;
                case "missing_snapshot": Set(_bridge, "_bootstrapSnapshot", null); break;
                case "reset": Set(_bridge, "_resettingWork", true); break;
            }
            Assert.That(Receive(ack, epoch: epoch), Is.True);
            Assert.That(Get<bool>(_bridge, "_stateSynchronized"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(Get<BridgeBootstrapReceipt>(_bridge, "_bootstrapReceipt"), Is.Null);
            Assert.That(_control.IsEmpty, Is.True);
        }

        [Test]
        public void DuplicateBootstrapCannotReplaceTheOriginalChallengeOrQueueTwice()
        {
            var first = Message(false);
            Assert.That(Receive(first), Is.True);
            var replacement = Message(false);
            replacement.id = "replacement-bootstrap";
            replacement.payload.nonce = new string('b', 32);
            Receive(replacement);
            Receive(first);
            Assert.That(Get<string>(_bridge, "_bootstrapRequestId"), Is.EqualTo(first.id));
            Assert.That(Get<string>(_bridge, "_bootstrapNonce"), Is.EqualTo(Nonce));
            Assert.That(_control.Count, Is.EqualTo(1));
            Receive(Message());
            Assert.That(Get<bool>(_bridge, "_stateSynchronized"), Is.False, "An ACK cannot bypass snapshot persistence.");
        }

        [Test]
        public void ClosingAdmissionInvalidatesReceiptAndAllPreviouslyCapturedOrigins()
        {
            PrepareSnapshot();
            Receive(Message());
            var origin = Origin();
            var generation = Get<long>(_bridge, "_admissionGeneration");
            _bridge.CloseOrdinaryAdmission("fixture_shutdown");
            Assert.That(Get<long>(_bridge, "_admissionGeneration"), Is.GreaterThan(generation));
            Assert.That(Get<bool>(_bridge, "_stateSynchronized"), Is.False);
            Assert.That(Get<bool>(_bridge, "_admissionClosed"), Is.True);
            Assert.That(Get<BridgeBootstrapReceipt>(_bridge, "_bootstrapReceipt"), Is.Null);
            Assert.That(Get<object>(_bridge, "_bootstrapSnapshot"), Is.Null);
            Assert.That(Current(origin, false), Is.False);
            Receive(Message());
            Assert.That(Get<bool>(_bridge, "_stateSynchronized"), Is.False, "Late ACK cannot reopen shutdown.");
        }

        [TestCase("session")]
        [TestCase("epoch")]
        [TestCase("server_lifetime")]
        [TestCase("service_lifetime")]
        [TestCase("admission")]
        [TestCase("cancelled")]
        public void CurrentOriginRejectsEveryChangedIdentity(string mismatch)
        {
            PrepareSnapshot();
            Receive(Message());
            using var cancellation = new CancellationTokenSource();
            var origin = Origin(cancellation.Token);
            Assert.That(Current(origin), Is.True);
            switch (mismatch)
            {
                case "session": Set(_bridge, "_sessionId", "new-session"); break;
                case "epoch": Set(_bridge, "_helloConnectionEpoch", 8L); break;
                case "server_lifetime": Set(_bridge, "_boundServerLifetime", "new-lifetime"); break;
                case "service_lifetime": Set(origin, "ServiceLifetime", "older-local-lifetime"); break;
                case "admission": _bridge.CloseOrdinaryAdmission("fixture_reconnect"); break;
                case "cancelled": cancellation.Cancel(); break;
            }
            Assert.That(Current(origin), Is.False);
            Assert.That(Current(origin, false), Is.False, "Even bootstrap/error sends retain their original identity.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueuedActionsRecheckOriginBeforeExecuting(bool tracked)
        {
            PrepareSnapshot();
            Receive(Message());
            while (_control.TryDequeue(out _)) { }
            var origin = Origin();
            var calls = 0;
            SetRequestOrigin(origin);
            try
            {
                if (tracked) _bridge.EnqueueTracked("readiness-fixture-" + Guid.NewGuid().ToString("N"), () => ++calls);
                else _bridge.EnqueueControl(() => ++calls);
            }
            finally { SetRequestOrigin(null); }
            _bridge.CloseOrdinaryAdmission("fixture_reconnect");
            var queue = tracked ? _main : _control;
            Assert.That(queue.TryDequeue(out var action), Is.True);
            action();
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public async Task ObsoleteResultsRetainTheirOriginAndNeverAdoptTheReplacementConnection()
        {
            PrepareSnapshot();
            Receive(Message());
            var origin = Origin();
            _bridge.CloseOrdinaryAdmission("fixture_reconnect");
            SetRequestOrigin(origin);
            try
            {
                Assert.That(Call(_bridge, "OriginForSend", "old-command", CancellationToken.None, true), Is.SameAs(origin));
                var sent = await (Task<bool>)Call(_bridge, "SendOriginJsonAsync", "{}", CancellationToken.None, origin, true);
                Assert.That(sent, Is.False);
                // A null transport also refuses the frame. The real Open-socket send gate needs transport coverage.
            }
            finally { SetRequestOrigin(null); }
            Assert.That(Call(_bridge, "OriginForSend", "unknown-command", CancellationToken.None, true), Is.Null,
                "A command result with no original request must never adopt the current connection.");
        }
    }
}
