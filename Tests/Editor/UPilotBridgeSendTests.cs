using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    // Actual production serialization and ClientWebSocket send, on test-owned loopback only.
    public sealed class UPilotBridgeSendTests
    {
        private const int Limit = 4 * 1024 * 1024;
        private const string Session = "isolated-bridge-send";
        private const string ServerLifetime = "isolated-send-lifetime", ServerInstance = "isolated-send-instance";
        private const string Producer = "isolated-send-producer", Hello = "isolated-send-hello";
        private const string Nonce = "0123456789abcdef0123456789abcdef";
        private const long Epoch = 7;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] public sealed class TextPayload { public string text; }
        [Serializable] public sealed class Envelope { public string id; public string type; public string name; }
        [Serializable] public sealed class Receipt
        {
            public string id, type, name, path, sha256;
            public int bytes;
        }
        [Serializable] public sealed class ReceiptList { public Receipt[] messages; }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);

        private static object Call(object target, string method, params object[] arguments) =>
            target.GetType().GetMethod(method, Private).Invoke(target, arguments);

        // No Bridge constructor or live compiler subscriptions; the compile service uses an unused temporary path.
        private static UPilotBridge IsolatedBridge(ClientWebSocket socket, SemaphoreSlim gate)
        {
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            Set(bridge, "_ws", socket);
            Set(bridge, "_sessionId", Session);
            Set(bridge, "_started", true);
            Set(bridge, "_isAuthenticated", true);
            Set(bridge, "_admissionClosed", true);
            Set(bridge, "_helloConnectionEpoch", Epoch);
            Set(bridge, "_boundServerLifetime", ServerLifetime);
            Set(bridge, "_producerEpoch", Producer);
            Set(bridge, "_domainGeneration", 3L);
            Set(bridge, "_mainThreadId", Thread.CurrentThread.ManagedThreadId);
            Set(bridge, "_lastMainThreadPumpAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Set(bridge, "_cachedPlayModeState", "edit");
            Set(bridge, "_controlQueue", new ConcurrentQueue<Action>());
            Set(bridge, "_sendLock", gate);
            Set(bridge, "_mainThreadQueue", new ConcurrentQueue<Action>());
            Set(bridge, "_compileService", new UPilotCompileService(
                Path.Combine(Path.GetTempPath(), "UPilotCompileFixture-" + Guid.NewGuid().ToString("N") + ".json"),
                Path.GetTempPath(), 123, 456, "isolated-producer"));
            return bridge;
        }

        private sealed class Wire : IDisposable
        {
            public readonly CancellationTokenSource Deadline = new CancellationTokenSource(15000);
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly ClientWebSocket _client = new ClientWebSocket();
            private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
            private TcpClient _accepted;
            private WebSocket _server;
            private Task<List<byte[]>> _reading;
            private Task _accepting;
            private readonly CancellationTokenRegistration _abortRegistration;
            private readonly string _directory;
            public UPilotBridge Bridge;
            private readonly Dictionary<string, object> _origins = new Dictionary<string, object>();
            public bool IsSocketOpen => _client.State == WebSocketState.Open;
            public int QueuedControlCount => ((ConcurrentQueue<Action>)typeof(UPilotBridge)
                .GetField("_controlQueue", Private).GetValue(Bridge)).Count;

            public object BindRequest(string id)
            {
                if (!_origins.TryGetValue(id, out var origin))
                {
                    origin = Call(Bridge, "CaptureOrigin", Deadline.Token);
                    _origins.Add(id, origin);
                }
                var commands = typeof(UPilotBridge).GetProperty("CommandOrigins", Private).GetValue(Bridge);
                commands.GetType().GetProperty("Item").SetValue(commands, origin, new object[] { id });
                return origin;
            }

            public Task HoldSendLock() => _gate.WaitAsync(Deadline.Token);
            public void ReleaseSendLock() => _gate.Release();
            public Task<bool> SendFromOrigin(object origin, string name) => (Task<bool>)Call(Bridge,
                "SendOriginJsonAsync", JsonUtility.ToJson(new Envelope { id = name, type = "event", name = name }),
                Deadline.Token, origin, true);
            public Task SendConnection(string json, CancellationToken token) => (Task)Call(Bridge,
                "SendConnectionJsonAsync", json, token, _client, Epoch);

            public async Task Synchronize(bool prepare = true)
            {
                if (prepare)
                {
                    Set(Bridge, "_readyHelloId", Hello);
                    Set(Bridge, "_readyServerInstanceId", ServerInstance);
                    Set(Bridge, "_readyProjectPath", "isolated-send-project");
                    Set(Bridge, "_readyDeadlineTick", System.Diagnostics.Stopwatch.GetTimestamp() + 15L * System.Diagnostics.Stopwatch.Frequency);
                    Set(Bridge, "_localReadyAnnounced", true);
                    Set(Bridge, "_bootstrapRequestId", "bootstrap-send-fixture");
                    Set(Bridge, "_bootstrapNonce", Nonce);
                    Set(Bridge, "_bootstrapSnapshotId", "send-snapshot");
                    Set(Bridge, "_bootstrapSnapshot", new EditorExecutionStatePayload {
                        snapshotId = "send-snapshot", producerEpoch = Producer, domainGeneration = 3,
                        sequence = 11, processId = 123, processCreatedAt = 456,
                    });
                }
                var ack = new BridgeBootstrapMessage {
                    id = "bootstrap-send-ack", type = "event", name = "session.bootstrap.ack", sessionId = Session,
                    payload = new BridgeBootstrapPayload {
                        bridgeReadyContractVersion = UPilotBridge.BridgeReadyContractVersion,
                        helloId = Hello, serverLifecycleId = ServerLifetime, serverInstanceId = ServerInstance,
                        bootstrapId = "bootstrap-send-fixture", snapshotId = "send-snapshot", nonce = Nonce,
                        producerEpoch = Producer, domainGeneration = 3, sequence = 11,
                    },
                };
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(ack));
                await Bounded(_server.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, Deadline.Token), "server bootstrap ACK");
                using var received = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult part;
                do
                {
                    part = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), Deadline.Token);
                    Assert.That(part.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                    received.Write(buffer, 0, part.Count);
                    Assert.That(received.Length, Is.LessThan(8192));
                } while (!part.EndOfMessage);
                var json = Encoding.UTF8.GetString(received.ToArray());
                var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
                Assert.That((bool)Call(Bridge, "TryHandleReadinessControl", json, envelope, Deadline.Token, _client, Epoch), Is.True);
                Assert.That(Bridge.IsStateSynchronized, Is.True);
                Assert.That(Bridge.TryGetBootstrapReceipt(Session, ServerInstance, out var receipt), Is.True);
                Assert.That(receipt.SnapshotId, Is.EqualTo("send-snapshot"));
                Assert.That(receipt.ConnectionEpoch, Is.EqualTo(Epoch));
                Assert.That(receipt.ProcessId, Is.EqualTo(123));
            }

            private Wire()
            {
                _directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Log", "Stage4", "BridgeSend",
                    TestContext.CurrentContext.Test.Name.Replace('"', '_') + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                _abortRegistration = Deadline.Token.Register(AbortTransport);
            }

            private void Trace(string phase) => File.AppendAllText(Path.Combine(_directory, "transport.txt"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + " " + phase + Environment.NewLine);

            private async Task Bounded(Task task, string phase)
            {
                Trace(phase);
                if (await Task.WhenAny(task, Task.Delay(Timeout.Infinite, Deadline.Token)).ConfigureAwait(false) != task)
                    throw new TimeoutException("Bridge send fixture deadline: " + phase);
                await task.ConfigureAwait(false);
                Trace(phase + " completed");
            }

            private void AbortTransport()
            {
                _client.Abort();
                _server?.Abort();
                _accepted?.Dispose();
                _listener.Stop();
            }

            public static async Task<Wire> Open(bool synchronize = true)
            {
                var wire = new Wire();
                try
                {
                    wire._listener.Start();
                    var port = ((IPEndPoint)wire._listener.LocalEndpoint).Port;
                    wire.Trace("listen " + port);
                    // Transport continuations must not depend on the Editor/Test Runner context.
                    wire._accepting = Task.Run(wire.Accept);
                    await wire.Bounded(wire._client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), wire.Deadline.Token), "connect");
                    await wire.Bounded(wire._accepting, "accept");
                    wire.Bridge = IsolatedBridge(wire._client, wire._gate);
                    wire._reading = wire.Read();
                    if (synchronize) await wire.Synchronize();
                    return wire;
                }
                catch { wire.Dispose(); throw; }
            }

            private async Task Accept()
            {
                _accepted = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                Trace("accepted");
                var stream = _accepted.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(header.Length, Is.LessThan(16384));
                    Assert.That(await stream.ReadAsync(one, 0, 1, Deadline.Token).ConfigureAwait(false), Is.EqualTo(1));
                    header.Append((char)one[0]);
                }
                var key = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)
                    .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Substring("Sec-WebSocket-Key:".Length).Trim();
                using var sha = SHA1.Create();
                var accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(
                    key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length, Deadline.Token).ConfigureAwait(false);
                Trace("handshake written");
                _server = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            }

            private async Task<List<byte[]>> Read()
            {
                var messages = new List<byte[]>();
                var buffer = new byte[32768];
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await _server.ReceiveAsync(new ArraySegment<byte>(buffer), Deadline.Token).ConfigureAwait(false);
                        Assert.That(part.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                        message.Write(buffer, 0, part.Count);
                        Assert.That(message.Length, Is.LessThanOrEqualTo(Limit));
                    } while (!part.EndOfMessage);
                    var bytes = message.ToArray();
                    var envelope = JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(bytes));
                    if (envelope.name == "fixture.end") return messages;
                    messages.Add(bytes);
                    Assert.That(messages.Count, Is.LessThanOrEqualTo(3), "Unexpected duplicate send.");
                }
            }

            public async Task<List<byte[]>> Finish()
            {
                // Ordered marker verifies all earlier messages, without a timing-based no-message assertion.
                await Bridge.SendEventAsync("end", "fixture.end", new TextPayload { text = "end" }, Deadline.Token);
                await Bounded(_reading, "receive through end marker");
                var messages = await _reading;
                var directory = _directory;
                var receipts = new List<Receipt>();
                for (int i = 0; i < messages.Count; i++)
                {
                    var bytes = messages[i];
                    var envelope = JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(bytes));
                    var path = Path.Combine(directory, i + ".json");
                    File.WriteAllBytes(path, bytes);
                    using var sha = SHA256.Create();
                    receipts.Add(new Receipt { id = envelope.id, type = envelope.type, name = envelope.name,
                        path = path, bytes = bytes.Length, sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() });
                }
                File.WriteAllText(Path.Combine(directory, "receipt.json"), JsonUtility.ToJson(new ReceiptList { messages = receipts.ToArray() }, true));
                TestContext.WriteLine("BridgeSend evidence: " + directory);
                return messages;
            }

            public void Dispose()
            {
                Deadline.Cancel();
                _abortRegistration.Dispose();
                _client.Dispose(); _server?.Dispose();
                // Observe aborted test-owned transport tasks even if setup failed before Finish.
                foreach (var task in new Task[] { _accepting, _reading })
                    task?.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _gate.Dispose(); Deadline.Dispose();
                Trace("disposed");
            }
        }

        [TestCase(-1, false)] [TestCase(0, false)] [TestCase(1, false)]
        [TestCase(-1, true)] [TestCase(0, true)] [TestCase(1, true)]
        public async Task EventFinalUtf8BoundarySendsExactlyOneMessage(int delta, bool unicode)
        {
            using var wire = await Wire.Open();
            var payload = new TextPayload { text = unicode ? "🙂" : "" };
            var model = new EventMessage<TextPayload> { id = "event", name = "fixture.event", payload = payload,
                sessionId = Session, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            int remaining = Limit + delta - Encoding.UTF8.GetByteCount(JsonUtility.ToJson(model));
            payload.text += unicode ? new string('中', remaining / 3) + new string('x', remaining % 3) : new string('x', remaining);
            Assert.That(Encoding.UTF8.GetByteCount(JsonUtility.ToJson(model)), Is.EqualTo(Limit + delta));
            await wire.Bridge.SendEventAsync("event", "fixture.event", payload, wire.Deadline.Token);
            var messages = await wire.Finish();
            Assert.That(messages.Count, Is.EqualTo(1));
            string json = Encoding.UTF8.GetString(messages[0]);
            if (delta <= 0)
            {
                Assert.That(messages[0].Length, Is.EqualTo(Limit + delta));
                Assert.That(JsonUtility.FromJson<EventMessage<TextPayload>>(json).payload.text, Is.EqualTo(payload.text));
            }
            else
            {
                var diagnostic = JsonUtility.FromJson<EventMessage<BridgeOversizeEventPayload>>(json);
                Assert.That(diagnostic.name, Is.EqualTo("bridge.payload_oversize"));
                Assert.That(diagnostic.payload.sourceEvent, Is.EqualTo("fixture.event"));
                Assert.That(diagnostic.payload.actualBytes, Is.EqualTo(Limit + 1));
                Assert.That(diagnostic.payload.limitBytes, Is.EqualTo(Limit));
                Assert.That(messages[0].Length, Is.LessThan(8192));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public async Task OversizeResultCountsEnvelopeAndKeepsFirstError(bool priorFailure)
        {
            using var wire = await Wire.Open();
            string id = "send-test-" + Guid.NewGuid().ToString("N");
            var tracker = UPilotOperationTracker.Instance;
            var context = tracker.BeginOperation(id, "fixture.result");
            try
            {
                string expected = priorFailure ? "FIXTURE_FIRST_ERROR" : "BRIDGE_RESPONSE_TOO_LARGE";
                if (Logger.MinLevel <= Logger.LogLevel.Error)
                    LogAssert.Expect(LogType.Error, new Regex("FAIL fixture.result id=" + id + ".*" + expected));
                if (priorFailure)
                {
                    LogAssert.Expect(LogType.Error, new Regex("\\[COMMAND .*FIXTURE_FIRST_ERROR"));
                    context.Fail(expected, "original failure");
                    wire.BindRequest(id);
                    await wire.Bridge.SendErrorAsync(id, expected, "original failure", wire.Deadline.Token, "fixture.result");
                }
                // Raw payload fits; only the final serialized envelope exceeds the limit.
                wire.BindRequest(id);
                await wire.Bridge.SendResultAsync(id, "fixture.result", new TextPayload { text = new string('x', Limit - 1) }, wire.Deadline.Token);
                var messages = await wire.Finish();
                Assert.That(messages.Count, Is.EqualTo(priorFailure ? 2 : 1));
                if (priorFailure)
                    Assert.That(JsonUtility.FromJson<ErrorMessage>(Encoding.UTF8.GetString(messages[0])).payload.code, Is.EqualTo(expected));
                var error = JsonUtility.FromJson<ErrorMessage>(Encoding.UTF8.GetString(messages.Last()));
                Assert.That(error.payload.code, Is.EqualTo("BRIDGE_RESPONSE_TOO_LARGE"));
                Assert.That(error.payload.detail.commandId, Is.EqualTo(id));
                Assert.That(error.payload.detail.actualBytes, Is.GreaterThan(Limit));
                Assert.That(error.payload.detail.limitBytes, Is.EqualTo(Limit));
                Assert.That(error.payload.detail.outcome, Is.EqualTo("unknown"));
                Assert.That(error.payload.detail.replayAttempted, Is.False);
                Assert.That(error.payload.detail.commandSubmitted, Is.True);
                Assert.That(error.payload.detail.sideEffectsMayHaveOccurred, Is.True);
                Assert.That(error.payload.detail.stage, Is.EqualTo("response_serialization"));
                Assert.That(messages.Last().Length, Is.LessThanOrEqualTo(8192));
                var entry = tracker.GetEntriesCopy().Single(item => item.CommandId == id);
                Assert.That(entry.ErrorCode, Is.EqualTo(expected));
                Assert.That(entry.ResultReported, Is.True);
            }
            finally { tracker.EndOperation(id); }
        }

        [Test]
        public async Task NormalResultPreservesContextTimingAndUtf8Payload()
        {
            using var wire = await Wire.Open();
            var payload = new TextPayload { text = "中文🙂" + new string('x', Limit - 8192) };
            wire.BindRequest("normal");
            await wire.Bridge.SendResultAsync("normal", "fixture.result", payload, wire.Deadline.Token);
            var messages = await wire.Finish();
            Assert.That(messages.Count, Is.EqualTo(1));
            var result = JsonUtility.FromJson<ResultMessage<TextPayload>>(Encoding.UTF8.GetString(messages[0]));
            Assert.That(result.type, Is.EqualTo("result"));
            Assert.That(result.payload.text, Is.EqualTo(payload.text));
            Assert.That(result.context.sessionId, Is.EqualTo(Session));
            Assert.That(result.timing, Is.Not.Null);
            Assert.That(messages[0].Length, Is.LessThanOrEqualTo(Limit));
        }

        [Test]
        public async Task OversizeErrorPreservesCodeInOneBoundedDiagnostic()
        {
            using var wire = await Wire.Open();
            LogAssert.Expect(LogType.Error, new Regex("FIXTURE_ERROR"));
            wire.BindRequest("error");
            await wire.Bridge.SendErrorAsync("error", "FIXTURE_ERROR", new string('中', Limit / 3 + 1), wire.Deadline.Token, "fixture.error");
            var messages = await wire.Finish();
            Assert.That(messages.Count, Is.EqualTo(1));
            var error = JsonUtility.FromJson<ErrorMessage>(Encoding.UTF8.GetString(messages[0]));
            Assert.That(error.payload.code, Is.EqualTo("FIXTURE_ERROR"));
            Assert.That(error.payload.message.Length, Is.LessThanOrEqualTo(512));
            Assert.That(error.payload.detail.actualBytes, Is.GreaterThan(Limit));
            Assert.That(messages[0].Length, Is.LessThanOrEqualTo(8192));
        }

        [Test]
        public async Task ActualSocketAckOpensReadinessExactlyOnce()
        {
            using var wire = await Wire.Open(synchronize: false);
            Assert.That(wire.IsSocketOpen, Is.True);
            Assert.That(wire.Bridge.IsStateSynchronized, Is.False);
            Assert.That(wire.Bridge.TryGetBootstrapReceipt(Session, ServerInstance, out _), Is.False);
            await wire.Synchronize();
            Assert.That(wire.QueuedControlCount, Is.EqualTo(1));
            await wire.Synchronize(prepare: false);
            Assert.That(wire.QueuedControlCount, Is.EqualTo(1), "Duplicate wire ACK cannot repeat Ready completion.");
            await wire.Bridge.SendEventAsync("ready", "fixture.ready", new TextPayload { text = "ready" }, wire.Deadline.Token);
            Assert.That((await wire.Finish()).Count, Is.EqualTo(1));
        }

        [Test]
        public async Task ClosingAdmissionWhileWaitingForSendLockDropsOriginalFrameWithoutRebinding()
        {
            using var wire = await Wire.Open();
            var original = wire.BindRequest("revoked");
            await wire.HoldSendLock();
            Task<bool> sending;
            try
            {
                sending = wire.SendFromOrigin(original, "fixture.revoked");
                Assert.That(sending.IsCompleted, Is.False, "The owned gate holds a valid admitted frame before its second check.");
                wire.Bridge.CloseOrdinaryAdmission("owned-send-lock-regression");
            }
            finally { wire.ReleaseSendLock(); }
            Assert.That(await sending, Is.False);
            Assert.That(wire.IsSocketOpen, Is.True);
            await wire.Synchronize();
            Assert.That(await wire.SendFromOrigin(original, "fixture.obsolete-after-ack"), Is.False,
                "A new ACK cannot rebind the original request to the current admission generation.");
            Assert.That(wire.BindRequest("revoked"), Is.SameAs(original));
            await wire.Bridge.SendResultAsync("revoked", "fixture.revoked-result", new TextPayload { text = "old" }, wire.Deadline.Token);
            wire.BindRequest("replacement");
            await wire.Bridge.SendResultAsync("replacement", "fixture.replacement", new TextPayload { text = "new" }, wire.Deadline.Token);
            var messages = await wire.Finish();
            Assert.That(messages.Count, Is.EqualTo(1));
            Assert.That(JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(messages[0])).name, Is.EqualTo("fixture.replacement"));
        }

        [Test]
        public async Task CancellingConnectionSendWhileWaitingForLockPreservesTheOwnedGateAndSocket()
        {
            using var wire = await Wire.Open();
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(wire.Deadline.Token);
            await wire.HoldSendLock();
            try
            {
                var sending = wire.SendConnection("{\"type\":\"event\",\"name\":\"fixture.cancelled\"}", cancelled.Token);
                Assert.That(sending.IsCompleted, Is.False);
                cancelled.Cancel();
                bool cancellationObserved = false;
                try { await sending; }
                catch (OperationCanceledException) { cancellationObserved = true; }
                Assert.That(cancellationObserved, Is.True);
            }
            finally { wire.ReleaseSendLock(); }
            Assert.That(wire.IsSocketOpen, Is.True);
            Assert.That((await wire.Finish()).Count, Is.Zero);
        }

        [Test]
        public async Task LowLevelOversizeGuardSendsNothingAndDoesNotCloseOwnedConnection()
        {
            using var wire = await Wire.Open();
            var sending = wire.SendConnection(new string('x', Limit + 1), wire.Deadline.Token);
            Assert.ThrowsAsync<InvalidDataException>(async () => await sending);
            Assert.That((await wire.Finish()).Count, Is.Zero);
        }
    }
}
