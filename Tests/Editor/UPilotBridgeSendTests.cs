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

        // No UPilot constructors: they subscribe to Editor callbacks and restore live state.
        private static UPilotBridge IsolatedBridge(ClientWebSocket socket, SemaphoreSlim gate)
        {
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            Set(bridge, "_ws", socket);
            Set(bridge, "_sessionId", Session);
            Set(bridge, "_sendLock", gate);
            Set(bridge, "_mainThreadQueue", new ConcurrentQueue<Action>());
            Set(bridge, "_compileService", FormatterServices.GetUninitializedObject(typeof(UPilotCompileService)));
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

            public static async Task<Wire> Open()
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
                    await wire.Bridge.SendErrorAsync(id, expected, "original failure", wire.Deadline.Token, "fixture.result");
                }
                // Raw payload fits; only the final serialized envelope exceeds the limit.
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
        public async Task LowLevelOversizeGuardSendsNothingAndDoesNotCloseOwnedConnection()
        {
            using var wire = await Wire.Open();
            var method = typeof(UPilotBridge).GetMethod("SendJsonAsync", Private);
            var sending = (Task)method.Invoke(wire.Bridge, new object[] { new string('x', Limit + 1), wire.Deadline.Token });
            Assert.ThrowsAsync<InvalidDataException>(async () => await sending);
            Assert.That((await wire.Finish()).Count, Is.Zero);
        }
    }
}
