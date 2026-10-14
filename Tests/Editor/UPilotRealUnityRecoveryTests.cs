using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    // Real Unity identity/socket/router; isolated production Python Server and owned SQLite.
    // Never apply the peer's hello to shared SessionState or cause a real Reload from this fixture.
    // The legacy receive-loop leaf uses an owned-only manual pump.
    // The shared-pump leaf enqueues only owned actions on the existing Bridge; no shared fields/queues are replaced.
    public sealed class UPilotRealUnityRecoveryTests
    {
        private const string Project = @"D:\upilot\Tests~\UPilotTest";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] private sealed class Ready { public int port; }
        [Serializable] private sealed class Count { public int effectCount; }
        [Serializable] private sealed class Reload { public string reloadId; }
        [Serializable] private sealed class Probe
        {
            public string originalId;
            public int effectCount, queueDepth, routeThreadId, mainThreadId;
        }
        [Serializable] private sealed class SharedPump
        {
            public string originalId, executionPhase, watchdogCommandId;
            public int effectCount, routeThreadId, executionThreadId, mainThreadId, watchdogIntervalSeconds;
            public long pumpAt;
            public double watchdogBefore, watchdogAfter;
            public bool executingIdentityVerified, watchdogObserverReleased, watchdogPreferenceRestored;
        }
        [Serializable] private sealed class LoopEnd { public string phase, status, failure; }
        [Serializable] private sealed class OwnedTasks
        {
            public string[] loops, handlers, commandIds, unreleasedIds;
            public int queued, tracked, pumped, sharedOwnedPending;
        }
        [Serializable] private sealed class Cleanup { public bool serverTaskStopped; public int connections, pending, suspended; }
        [Serializable] private sealed class Report
        {
            public bool succeeded, frozen, realDomainReload, productionReceiveLoopCovered, productionMainThreadPumpCovered, sharedWatchdogObserved;
            public int effectCount, pendingCount, suspendedCount;
            public string scenario, originalError, originalStatus, failure, cleanupError;
            public Cleanup cleanup;
        }
        [Serializable] private sealed class Source { public string path, sha256; }
        [Serializable] private sealed class Sources { public Source[] files; }
        [Serializable] private sealed class Identity
        {
            public string project, unityVersion, sharedServerLifetime;
            public int processId, mainThreadId;
            public long processCreatedAt;
        }
        [Serializable] private sealed class UnityCleanup
        {
            public string scenario, originalId, failure, cleanupError;
            public string sharedServerLifetimeBefore, sharedServerLifetimeAfter;
            public int effectCount, peerProcessId, peerExitCode, routeThreadId, executionThreadId, mainThreadId;
            public bool peerStopped, peerKilled, deadlineExpired;
            public string[] socketStates;
        }

        [UnityTest] public IEnumerator ResponseLostNeverReplaysRealUnityMutation() => Run("response_lost");
        [UnityTest] public IEnumerator CompletedMutationIsFrozenAcrossSocketLoss() => Run("completed");
        [UnityTest] public IEnumerator TimedOutMutationIsFrozenAcrossSocketLoss() => Run("timed_out");
        [UnityTest] public IEnumerator CancelledMutationIsFrozenAcrossSocketLoss() => Run("cancelled");
        [UnityTest] public IEnumerator DeclaredReloadDoesNotReplayRealUnityMutation() => Run("declared_reload");

        [UnityTest] public IEnumerator ProductionReceiveLoopDoesNotBlockOrReplayRealUnityMutation() => Run("response_lost", true);

        [UnityTest] public IEnumerator SharedProductionPumpDoesNotReplayRealUnityMutation() => Run("response_lost", true, true);

        private static IEnumerator Run(string scenario, bool productionReceiveLoop = false, bool productionMainThreadPump = false)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                !string.Equals(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), Project, StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("Real identity acceptance is restricted to the exact canonical Windows project.");
            var task = Exercise(scenario, productionReceiveLoop, productionMainThreadPump);
            while (!task.IsCompleted) yield return null;
            task.GetAwaiter().GetResult();
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static void Save(string directory, string name, object value) =>
            File.WriteAllText(Path.Combine(directory, name), JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        private static async Task WaitFor(string directory, string name, Process peer, CancellationToken token)
        {
            while (!File.Exists(Path.Combine(directory, name)))
            {
                token.ThrowIfCancellationRequested();
                if (peer.HasExited) throw new InvalidOperationException("Peer exited while waiting for " + name);
                await Task.Delay(20, token);
            }
        }
        private static async Task<string> Receive(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16384];
            using var output = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Unexpected owned peer close");
                if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Non-text peer message");
                output.Write(buffer, 0, frame.Count);
                if (output.Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversize fixture message");
            } while (!frame.EndOfMessage);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        private static Task Route(UPilotBridge bridge, string json, CancellationToken token) =>
            (Task)typeof(UPilotBridge).GetMethod("HandleMessageJsonAsync", Private).Invoke(bridge,
                new object[] { json, JsonUtility.FromJson<BridgeEnvelope>(json), token });

        private static async Task<ClientWebSocket> Authenticate(UPilotBridge bridge, int port, string directory,
            string phase, Identity identity, List<ClientWebSocket> sockets, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            sockets.Add(socket);
            var session = "real-unity-recovery-" + Guid.NewGuid().ToString("N");
            Set(bridge, "_ws", socket);
            Set(bridge, "_sessionId", session);
            Set(bridge, "_isAuthenticated", false);
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), token);
            var hello = new HelloMessage
            {
                id = Guid.NewGuid().ToString("N"), type = "hello", name = "session.hello", sessionId = session,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new HelloPayload
                {
                    identityContractVersion = 1, unityVersion = identity.unityVersion, projectPath = identity.project,
                    platform = "windows", processId = identity.processId, processCreatedAt = identity.processCreatedAt,
                    processRole = "mainEditor"
                }
            };
            var json = JsonUtility.ToJson(hello);
            File.WriteAllText(Path.Combine(directory, phase + "-hello.json"), json, new UTF8Encoding(false));
            await (Task)typeof(UPilotBridge).GetMethod("SendJsonAsync", Private).Invoke(bridge, new object[] { json, token });
            var incoming = await Receive(socket, token);
            File.WriteAllText(Path.Combine(directory, phase + "-ack.json"), incoming, new UTF8Encoding(false));
            var ack = JsonUtility.FromJson<HelloAckMessage>(incoming);
            Assert.That(ack.type, Is.EqualTo("result"));
            Assert.That(ack.name, Is.EqualTo("session.hello"));
            Assert.That(ack.sessionId, Is.EqualTo(session));
            Assert.That(ack.payload.accepted, Is.True, incoming);
            Assert.That(ack.payload.identityContractVersion, Is.EqualTo(1));
            Assert.That(ack.payload.verificationLevel, Is.EqualTo("windows-pid-creation-executable-role-project"));
            Assert.That(ack.payload.serverLifecycleId, Is.Not.Empty);
            // Production hello application resets project-wide resources on a new lifetime.
            // Validate the real ack, but authenticate only this object without applying it.
            Set(bridge, "_isAuthenticated", true);
            Save(directory, "authenticated-" + phase, new Count());
            return socket;
        }

        private static Sources FreezeSources(string repository)
        {
            var paths = new[]
            {
                "Tests/Editor/UPilotRealUnityRecoveryTests.cs", "Editor/Core/UPilotBridge.cs",
                "Editor/Core/UPilotTimingSettings.cs", "Editor/Core/UPilotOperationTracker.cs",
                "Editor/Core/UPilotCommandRouter.cs", "Editor/Core/UPilotProtocol.cs",
                "upilotserver~/tests/real_unity_recovery_peer.py", "upilotserver~/src/upilot_mcp/server.py",
                "upilotserver~/src/upilot_mcp/dispatcher.py", "upilotserver~/src/upilot_mcp/state_store.py"
            };
            var files = new List<Source>();
            using var sha = SHA256.Create();
            foreach (var relative in paths)
            {
                var path = Path.Combine(repository, relative.Replace('/', Path.DirectorySeparatorChar));
                files.Add(new Source { path = path, sha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant() });
            }
            return new Sources { files = files.ToArray() };
        }

        private static async Task Exercise(string scenario, bool productionReceiveLoop, bool productionMainThreadPump)
        {
            var repository = Directory.GetParent(Directory.GetParent(Project).FullName).FullName;
            var python = Path.Combine(repository, "upilotserver~", ".venv", "Scripts", "python.exe");
            var script = Path.Combine(repository, "upilotserver~", "tests", "real_unity_recovery_peer.py");
            Assert.That(File.Exists(python) && File.Exists(script), Is.True, "Missing canonical peer/runtime");
            var directory = Path.Combine(Project, "Log", "Stage4", "RealUnityRecovery", (productionMainThreadPump ? "shared-production-pump-" : productionReceiveLoop ? "production-receive-loop-" : "") + scenario + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var unity = Process.GetCurrentProcess();
            var identity = new Identity
            {
                project = Project, unityVersion = Application.unityVersion, processId = unity.Id,
                processCreatedAt = unity.StartTime.ToUniversalTime().ToFileTimeUtc(),
                mainThreadId = Thread.CurrentThread.ManagedThreadId,
                sharedServerLifetime = SessionState.GetString("UPilot.ServerLifetime", "")
            };
            Save(directory, "unity-start.json", identity);
            Save(directory, "source-before.json", FreezeSources(repository));
            using var deadline = new CancellationTokenSource(65000);
            using var gate = new SemaphoreSlim(1, 1);
            var sockets = new List<ClientWebSocket>();
            var cleanup = new UnityCleanup { scenario = scenario, peerExitCode = -1, mainThreadId = identity.mainThreadId, sharedServerLifetimeBefore = identity.sharedServerLifetime };
            var queue = new ConcurrentQueue<Action>();
            var tracked = new ConcurrentDictionary<Action, string>();
            var queueLock = new object();
            var loops = new List<Task>();
            var handlers = new ConcurrentBag<Task>();
            var ownedIds = new ConcurrentBag<string>();
            UPilotBridge bridge = null;
            var pumped = 0;
            var sharedOwned = new ConcurrentDictionary<string, byte>();
            var sharedBridge = productionMainThreadPump ? UPilotBridge.Instance : null;
            var sharedPump = new SharedPump
            {
                mainThreadId = identity.mainThreadId
            };
            void PumpOwnedQueue()
            {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(identity.mainThreadId));
                while (true)
                {
                    Action action;
                    lock (queueLock)
                    {
                        if (!queue.TryDequeue(out action)) break;
                        Set(bridge, "_executingQueueAction", action);
                    }
                    try { pumped++; action(); }
                    finally
                    {
                        lock (queueLock)
                        {
                            Set(bridge, "_executingQueueAction", null);
                            tracked.TryRemove(action, out _);
                        }
                    }
                }
            }
            async Task OnOwnedMainThread(string id, Action action, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellation = token.Register(() => completion.TrySetCanceled());
                var dispatchBridge = productionMainThreadPump ? sharedBridge : bridge;
                if (productionMainThreadPump) sharedOwned.TryAdd(id, 0);
                try
                {
                    dispatchBridge.EnqueueTracked(id, () =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested || completion.Task.IsCompleted) return;
                            if (productionMainThreadPump)
                            {
                                Interlocked.Increment(ref pumped);
                                if (id == cleanup.originalId)
                                {
                                    var entry = UPilotOperationTracker.Instance.GetEntriesCopy().Single(value => value.CommandId == id);
                                    sharedPump.originalId = id;
                                    sharedPump.executionPhase = entry.Phase;
                                    sharedPump.executingIdentityVerified = sharedBridge.ObserveQueue("").executingCommandId == id;
                                    sharedPump.pumpAt = (long)typeof(UPilotBridge).GetField("_lastMainThreadPumpAt", Private).GetValue(sharedBridge);
                                }
                            }
                            action();
                            completion.TrySetResult(true);
                        }
                        catch (Exception ex) { completion.TrySetException(ex); }
                        finally { if (productionMainThreadPump) sharedOwned.TryRemove(id, out _); }
                    });
                }
                catch
                {
                    sharedOwned.TryRemove(id, out _);
                    throw;
                }
                if (!productionMainThreadPump && id == cleanup.originalId && cleanup.effectCount == 0)
                    Save(directory, "mutation-queued.json", new Probe
                    {
                        originalId = id, effectCount = 0, queueDepth = queue.Count,
                        routeThreadId = Thread.CurrentThread.ManagedThreadId, mainThreadId = identity.mainThreadId
                    });
                await completion.Task.ConfigureAwait(false);
            }
            Task Track(string id, Func<Task> body)
            {
                ownedIds.Add(id);
                var task = body();
                handlers.Add(task);
                return task;
            }
            Task StartReceiveLoop()
            {
                var task = (Task)typeof(UPilotBridge).GetMethod("ReceiveLoopAsync", Private).Invoke(bridge, new object[] { deadline.Token });
                loops.Add(task);
                return task;
            }
            async Task ObserveLoop(Task task, string phase, CancellationToken token)
            {
                while (!task.IsCompleted) await Task.Delay(20, token);
                var result = new LoopEnd { phase = phase, status = task.Status.ToString() };
                try { await task; }
                catch (WebSocketException ex) { result.failure = ex.ToString(); }
                catch (OperationCanceledException ex) { result.failure = ex.ToString(); }
                finally { Save(directory, "receive-loop-ended-" + phase + ".json", result); }
            }
            using var peer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "\"" + script + "\" --directory \"" + directory + "\" --project \"" + Project + "\" --scenario " + scenario + (productionReceiveLoop ? " --production-receive-loop" : "") + (productionMainThreadPump ? " --production-main-thread-pump" : ""),
                    WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            Task<string> stdout = null, stderr = null;
            var started = false;
            try
            {
                started = peer.Start();
                Assert.That(started, Is.True);
                cleanup.peerProcessId = peer.Id;
                stdout = peer.StandardOutput.ReadToEndAsync();
                stderr = peer.StandardError.ReadToEndAsync();
                await WaitFor(directory, "ready.json", peer, deadline.Token);
                var port = JsonUtility.FromJson<Ready>(File.ReadAllText(Path.Combine(directory, "ready.json"))).port;
                Assert.That(port, Is.GreaterThan(0));
                var router = new UPilotCommandRouter();
                bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
                Set(bridge, "_sendLock", gate);
                Set(bridge, "_mainThreadQueue", queue);
                Set(bridge, "_trackedQueueActions", tracked);
                Set(bridge, "_queueObservationLock", queueLock);
                Set(bridge, "_compileService", FormatterServices.GetUninitializedObject(typeof(UPilotCompileService)));
                Set(bridge, "_router", router);
                router.Register("fixture.mutation", (id, json, token) => Track(id, async () =>
                {
                    cleanup.routeThreadId = Thread.CurrentThread.ManagedThreadId;
                    if (cleanup.originalId == null) cleanup.originalId = id;
                    void Mutate()
                    {
                        token.ThrowIfCancellationRequested();
                        cleanup.executionThreadId = Thread.CurrentThread.ManagedThreadId;
                        Assert.That(cleanup.executionThreadId, Is.EqualTo(identity.mainThreadId));
                        cleanup.effectCount++;
                        Save(directory, "mutation-" + cleanup.effectCount + ".json", cleanup);
                    }
                    if (productionReceiveLoop) await OnOwnedMainThread(id, Mutate, token);
                    else Mutate();
                    if (scenario == "completed") await bridge.SendResultAsync(id, "fixture.mutation", new Count { effectCount = cleanup.effectCount }, token);
                    if (scenario == "declared_reload" && cleanup.effectCount == 1)
                        await bridge.SendEventAsync(id, "domain_reload.starting", new Reload { reloadId = "declared-only-" + id }, token);
                    Save(directory, "effect", new Count { effectCount = cleanup.effectCount });
                }));
                var inspected = false;
                router.Register("fixture.inspect", (id, json, token) => Track(id, async () =>
                {
                    Task send = null;
                    void Inspect()
                    {
                        Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(identity.mainThreadId));
                        send = bridge.SendResultAsync(id, "fixture.inspect", new Count { effectCount = cleanup.effectCount }, token);
                    }
                    if (productionReceiveLoop) await OnOwnedMainThread(id, Inspect, token);
                    else Inspect();
                    await send;
                    Volatile.Write(ref inspected, true);
                }));
                if (productionReceiveLoop && !productionMainThreadPump)
                    router.Register("fixture.receive_probe", (id, json, token) => Track(id, async () =>
                    {
                        var payload = new Probe
                        {
                            originalId = cleanup.originalId, effectCount = cleanup.effectCount, queueDepth = queue.Count,
                            routeThreadId = Thread.CurrentThread.ManagedThreadId, mainThreadId = identity.mainThreadId
                        };
                        Assert.That(payload.effectCount, Is.EqualTo(0));
                        Assert.That(payload.queueDepth, Is.EqualTo(1));
                        Assert.That(payload.routeThreadId, Is.Not.EqualTo(payload.mainThreadId));
                        // A background-only probe must not build Unity Editor context or sample Unity APIs.
                        var response = JsonUtility.ToJson(new ResultMessage<Probe>
                        {
                            id = id, name = "fixture.receive_probe", payload = payload,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            sessionId = (string)typeof(UPilotBridge).GetField("_sessionId", Private).GetValue(bridge)
                        });
                        File.WriteAllText(Path.Combine(directory, "receive-probe-result.json"), response, new UTF8Encoding(false));
                        await (Task)typeof(UPilotBridge).GetMethod("SendJsonAsync", Private).Invoke(bridge, new object[] { response, token });
                    }));
                var socket = await Authenticate(bridge, port, directory, "initial", identity, sockets, deadline.Token);
                var received = 0;
                Task initialLoop = null;
                if (productionReceiveLoop)
                {
                    initialLoop = StartReceiveLoop();
                    if (!productionMainThreadPump)
                    {
                        await WaitFor(directory, "pump-now", peer, deadline.Token);
                        Assert.That(cleanup.effectCount, Is.EqualTo(0));
                        Assert.That(queue.Count, Is.EqualTo(1));
                        Assert.That(handlers.Count, Is.EqualTo(2));
                        PumpOwnedQueue();
                    }
                    await WaitFor(directory, "effect", peer, deadline.Token);
                    if (productionMainThreadPump)
                    {
                        // Hold owned activity while observing one actual shared-pump check.
                        // A bounded fixture interval preserves the original peer/test budgets.
                        var watchdogKey = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.WatchdogIntervalSeconds));
                        var hadWatchdogPreference = EditorPrefs.HasKey(watchdogKey);
                        var watchdogPreference = EditorPrefs.GetInt(watchdogKey);
                        var tracker = UPilotOperationTracker.Instance;
                        OperationContext watchdogContext = null;
                        sharedPump.watchdogCommandId = "fixture-watchdog-" + Guid.NewGuid().ToString("N");
                        ownedIds.Add(sharedPump.watchdogCommandId);
                        try
                        {
                            UPilotTimingSettings.WatchdogIntervalSeconds = 1;
                            sharedPump.watchdogIntervalSeconds = UPilotTimingSettings.WatchdogIntervalSeconds;
                            sharedPump.watchdogBefore = (double)typeof(UPilotBridge).GetField("_lastWatchdogCheck", Private).GetValue(sharedBridge);
                            watchdogContext = tracker.BeginOperation(sharedPump.watchdogCommandId, "fixture.watchdog_observation");
                            while ((double)typeof(UPilotBridge).GetField("_lastWatchdogCheck", Private).GetValue(sharedBridge) <= sharedPump.watchdogBefore)
                                await Task.Delay(20, deadline.Token);
                            sharedPump.watchdogAfter = (double)typeof(UPilotBridge).GetField("_lastWatchdogCheck", Private).GetValue(sharedBridge);
                        }
                        finally
                        {
                            try { watchdogContext?.Complete("owned shared-pump watchdog observation ended"); }
                            finally
                            {
                                try { tracker.EndOperation(sharedPump.watchdogCommandId); }
                                finally
                                {
                                    if (hadWatchdogPreference) EditorPrefs.SetInt(watchdogKey, watchdogPreference);
                                    else EditorPrefs.DeleteKey(watchdogKey);
                                    UPilotTimingSettings.PreferencesReset();
                                }
                            }
                        }
                        sharedPump.watchdogObserverReleased = tracker.GetContext(sharedPump.watchdogCommandId) == null;
                        sharedPump.watchdogPreferenceRestored = EditorPrefs.HasKey(watchdogKey) == hadWatchdogPreference &&
                            (!hadWatchdogPreference || EditorPrefs.GetInt(watchdogKey) == watchdogPreference);
                        Assert.That(sharedPump.watchdogObserverReleased && sharedPump.watchdogPreferenceRestored, Is.True);
                        sharedPump.effectCount = cleanup.effectCount;
                        sharedPump.routeThreadId = cleanup.routeThreadId;
                        sharedPump.executionThreadId = cleanup.executionThreadId;
                        Save(directory, "shared-pump.json", sharedPump);
                    }
                }
                while (!productionReceiveLoop && cleanup.effectCount == 0)
                {
                    var incoming = await Receive(socket, deadline.Token);
                    File.WriteAllText(Path.Combine(directory, "received-" + (++received) + ".json"), incoming, new UTF8Encoding(false));
                    await Route(bridge, incoming, deadline.Token);
                }
                await WaitFor(directory, "drop-now", peer, deadline.Token);
                socket.Abort();
                if (productionReceiveLoop) await ObserveLoop(initialLoop, "initial", deadline.Token);
                socket.Dispose();
                Save(directory, "owned-socket-dropped.json", cleanup);
                await WaitFor(directory, "reconnect-now", peer, deadline.Token);
                socket = await Authenticate(bridge, port, directory, "reconnect", identity, sockets, deadline.Token);
                Task reconnectLoop = productionReceiveLoop ? StartReceiveLoop() : null;
                await WaitFor(directory, "terminal-frozen", peer, deadline.Token);
                // Contradict the original result on the new authenticated socket. The following
                // inspector reply is the ordered barrier proving that this late frame was consumed.
                await bridge.SendResultAsync(cleanup.originalId, "fixture.mutation", new Count { effectCount = 999 }, deadline.Token);
                while (!Volatile.Read(ref inspected))
                {
                    if (productionReceiveLoop)
                    {
                        if (!productionMainThreadPump) PumpOwnedQueue();
                        await Task.Delay(20, deadline.Token);
                        continue;
                    }
                    var incoming = await Receive(socket, deadline.Token);
                    File.WriteAllText(Path.Combine(directory, "received-" + (++received) + ".json"), incoming, new UTF8Encoding(false));
                    await Route(bridge, incoming, deadline.Token);
                }
                // The peer owns server.stop(), which force-aborts its sockets.
                // Wait for that teardown; dispose our sockets once in finally.
                while (!peer.HasExited) await Task.Delay(20, deadline.Token);
                await stdout;
                await stderr;
                if (productionReceiveLoop) await ObserveLoop(reconnectLoop, "reconnect", deadline.Token);
                var report = JsonUtility.FromJson<Report>(File.ReadAllText(Path.Combine(directory, "report.json")));
                Assert.That(peer.ExitCode, Is.EqualTo(0), report.failure + report.cleanupError);
                Assert.That(report.succeeded && report.frozen, Is.True);
                Assert.That(report.scenario, Is.EqualTo(scenario));
                Assert.That(report.effectCount, Is.EqualTo(1));
                Assert.That(cleanup.effectCount, Is.EqualTo(1));
                Assert.That(report.pendingCount + report.suspendedCount, Is.EqualTo(0));
                Assert.That(report.realDomainReload, Is.False);
                Assert.That(report.productionReceiveLoopCovered, Is.EqualTo(productionReceiveLoop));
                Assert.That(report.productionMainThreadPumpCovered, Is.EqualTo(productionMainThreadPump));
                Assert.That(report.sharedWatchdogObserved, Is.EqualTo(productionMainThreadPump));
                Assert.That(report.cleanup.serverTaskStopped, Is.True);
                Assert.That(report.cleanup.connections + report.cleanup.pending + report.cleanup.suspended, Is.EqualTo(0));
                Save(directory, "source-after.json", FreezeSources(repository));
                Assert.That(File.ReadAllText(Path.Combine(directory, "source-after.json")), Is.EqualTo(File.ReadAllText(Path.Combine(directory, "source-before.json"))));
                Assert.That(SessionState.GetString("UPilot.ServerLifetime", ""), Is.EqualTo(identity.sharedServerLifetime));
            }
            catch (Exception ex)
            {
                cleanup.failure = ex.ToString();
                throw;
            }
            finally
            {
                var states = new List<string>();
                foreach (var socket in sockets)
                {
                    states.Add(socket.State.ToString());
                    socket.Abort();
                    socket.Dispose();
                }
                cleanup.socketStates = states.ToArray();
                cleanup.deadlineExpired = deadline.IsCancellationRequested;
                if (productionReceiveLoop)
                {
                    deadline.Cancel();
                    // Cancel pending completions; discard only this clone's unexecuted queue.
                    lock (queueLock)
                    {
                        while (queue.TryDequeue(out var discarded)) tracked.TryRemove(discarded, out _);
                    }
                    var wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 5000 &&
                        (loops.Any(t => !t.IsCompleted) || handlers.Any(t => !t.IsCompleted) ||
                         ownedIds.Any(id => UPilotOperationTracker.Instance.GetContext(id) != null) || !sharedOwned.IsEmpty))
                        await Task.Delay(20);
                    foreach (var loop in loops)
                    {
                        if (!loop.IsCompleted) continue;
                        try { await loop; }
                        catch (WebSocketException) { }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { cleanup.cleanupError += ex.ToString(); }
                    }
                    foreach (var task in handlers)
                    {
                        if (!task.IsCompleted) continue;
                        try { await task; }
                        catch (OperationCanceledException) when (!string.IsNullOrEmpty(cleanup.failure)) { }
                        catch (Exception ex) { cleanup.cleanupError += ex.ToString(); }
                    }
                    var taskReport = new OwnedTasks
                    {
                        loops = loops.Select(t => t.Status.ToString()).ToArray(),
                        handlers = handlers.Select(t => t.Status.ToString()).ToArray(),
                        commandIds = ownedIds.ToArray(),
                        unreleasedIds = ownedIds.Where(id => UPilotOperationTracker.Instance.GetContext(id) != null).ToArray(),
                        queued = queue.Count, tracked = tracked.Count, pumped = pumped, sharedOwnedPending = sharedOwned.Count
                    };
                    Save(directory, "owned-tasks.json", taskReport);
                    if (loops.Any(t => !t.IsCompleted) || handlers.Any(t => !t.IsCompleted) || taskReport.unreleasedIds.Length != 0 || !sharedOwned.IsEmpty)
                        cleanup.cleanupError += "Owned receive-loop/handler/tracker cleanup did not finish within 5 seconds.";
                    if (string.IsNullOrEmpty(cleanup.failure) && (handlers.Count != (productionMainThreadPump ? 2 : 3) || pumped != 2 || taskReport.queued + taskReport.tracked + taskReport.sharedOwnedPending != 0))
                        cleanup.cleanupError += "Unexpected owned dispatch/pump counts.";
                }
                if (started)
                {
                    try
                    {
                        if (!peer.HasExited) { cleanup.peerKilled = true; peer.Kill(); }
                        var wait = Stopwatch.StartNew();
                        while (!peer.HasExited && wait.ElapsedMilliseconds < 5000) await Task.Delay(20);
                        cleanup.peerStopped = peer.HasExited;
                        if (peer.HasExited)
                        {
                            cleanup.peerExitCode = peer.ExitCode;
                            File.WriteAllText(Path.Combine(directory, "peer-stdout.txt"), await stdout, new UTF8Encoding(false));
                            File.WriteAllText(Path.Combine(directory, "peer-stderr.txt"), await stderr, new UTF8Encoding(false));
                        }
                    }
                    catch (Exception ex) { cleanup.cleanupError = ex.ToString(); }
                }
                cleanup.sharedServerLifetimeAfter = SessionState.GetString("UPilot.ServerLifetime", "");
                Save(directory, "unity-cleanup.json", cleanup);
            }
            Assert.That(cleanup.peerStopped && !cleanup.peerKilled, Is.True, "Peer did not exit naturally");
            Assert.That(cleanup.cleanupError, Is.Null.Or.Empty);
            UnityEngine.Debug.Log("Real Unity recovery evidence: " + directory);
        }
    }

    // Guards never arm a valid live probe or request a script Reload.
    public sealed class UPilotCommandReloadProbeGuardTests
    {
        private const string Key = "UPilot.Tests.OwnedCommandReloadWitness";
        [SetUp]
        public void RequireCanonicalWindowsProject()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                !string.Equals(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("Reload witness guards require the exact canonical Windows project.");
        }
        [TestCase(null)]
        [TestCase("")]
        [TestCase("../escape")]
        [TestCase("0000000000000000000000000000000x")]
        public void InvalidNonceIsRejectedWithoutChangingWitness(string nonce)
        {
            var before = SessionState.GetString(Key, "");
            Assert.Throws<ArgumentException>(() => UPilotCommandReloadProbe.ArmAndWait(nonce));
            Assert.That(SessionState.GetString(Key, ""), Is.EqualTo(before));
        }
        [Test]
        public void MissingExecutingCommandIsRejectedBeforeAnyOwnedFileWrite()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(Key, "")) ||
                !string.IsNullOrEmpty(UPilotBridge.Instance.ObserveQueue("").executingCommandId))
                Assert.Ignore("A live witness or another command is present; do not touch it.");
            var nonce = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Application.dataPath, "..", "Log", "Stage4", "CommandRealReload", nonce);
            Assert.That(Directory.Exists(directory), Is.False);
            Assert.Throws<InvalidOperationException>(() => UPilotCommandReloadProbe.ArmAndWait(nonce));
            Assert.That(Directory.Exists(directory), Is.False);
            Assert.That(SessionState.GetString(Key, ""), Is.Empty);
            Assert.That(UPilotCommandReloadProbe.Disarm(nonce), Is.False);
        }

        [Serializable]
        private sealed class WitnessReceipt
        {
            public bool resourcesReleased;
            public long endedAt;
            public string failure;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoredWitnessReleasesOnlyOwnedObservationEvenIfEffectReadFails(bool missingEffect)
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(Key, "")))
                Assert.Ignore("Do not replace a live witness.");
            var nonce = Guid.NewGuid().ToString("N");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var directory = (string)typeof(UPilotCommandReloadProbe).GetMethod("OwnedDirectory", flags)
                .Invoke(null, new object[] { nonce });
            Assert.That(Directory.Exists(directory), Is.False);
            Directory.CreateDirectory(directory);
            var effect = Path.Combine(directory, "effect.json");
            var after = Path.Combine(directory, "after-reload.json");
            try
            {
                if (!missingEffect) File.WriteAllText(effect, "{\"effectCount\":1}");
                var escapedDirectory = directory.Replace("\\", "\\\\");
                SessionState.SetString(Key, "{\"nonce\":\"" + nonce + "\",\"directory\":\"" +
                    escapedDirectory + "\",\"domainBefore\":\"fixture-not-a-real-reload\",\"beforeReloadAt\":1}");
                typeof(UPilotCommandReloadProbe).GetMethod("ObserveAfterReload", flags).Invoke(null, null);
                Assert.That(SessionState.GetString(Key, ""), Is.Empty);
                var receipt = JsonUtility.FromJson<WitnessReceipt>(File.ReadAllText(after));
                Assert.That(receipt.resourcesReleased, Is.True);
                Assert.That(receipt.endedAt, Is.GreaterThan(0));
                if (missingEffect) Assert.That(receipt.failure, Is.EqualTo(nameof(FileNotFoundException)));
                else Assert.That(receipt.failure, Is.Null.Or.Empty);
                var original = File.ReadAllText(after);
                typeof(UPilotCommandReloadProbe).GetMethod("ObserveAfterReload", flags).Invoke(null, null);
                Assert.That(File.ReadAllText(after), Is.EqualTo(original));
            }
            finally
            {
                SessionState.EraseString(Key);
                // Only this test's two known files, then the empty GUID directory.
                if (File.Exists(effect)) File.Delete(effect);
                if (File.Exists(after)) File.Delete(after);
                Directory.Delete(directory);
            }
        }
    }

    // Opt-in live probe, not a Test Runner leaf. Only a witness survives Reload;
    // no old Task, command, queue or completion is reconstructed in the new domain.
    [InitializeOnLoad]
    public static class UPilotCommandReloadProbe
    {
        private const string Project = @"D:\upilot\Tests~\UPilotTest";
        private const string Key = "UPilot.Tests.OwnedCommandReloadWitness";
        private static readonly string DomainToken = Guid.NewGuid().ToString("N");
        private static TaskCompletionSource<string> _pending;
        private static Witness _witness;
        [Serializable] private sealed class Witness
        {
            public string nonce, directory, originalCommandId, commandName, phaseAtArm, phaseBeforeReload;
            public string domainBefore, domainAfter, serverLifetimeBefore, serverLifetimeAfter, failure;
            public int processIdBefore, processIdAfter, effectCount;
            public int dispatchCountBefore = -1, executionCountBefore = -1, mainThreadActionCountBefore = -1;
            public int retainedInvocationsBefore = -1;
            public string countScope = "current-domain-retained-invocation; handler-entry-not-business-effect";
            public long processCreatedBefore, processCreatedAfter, armedAt, beforeReloadAt, observedAfterAt, deadlineAt;
            public long reloadRequestStartedAt, reloadRequestReturnedAt, endedAt;
            public string reloadRequestError;
            public bool resourcesReleased;
            public bool activeBeforeReload, realDomainReload, ownedEffectUnchanged;
        }
        static UPilotCommandReloadProbe()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(Key, ""))) EditorApplication.update += ObserveAfterReload;
        }
        private static string OwnedDirectory(string nonce)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                !string.Equals(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), Project, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reload witness requires the exact canonical Windows project.");
            if (nonce == null || nonce.Length != 32 || !Guid.TryParseExact(nonce, "N", out _))
                throw new ArgumentException("An exact test-owned GUID is required.", nameof(nonce));
            var path = Path.Combine(Project, "Log", "Stage4", "CommandRealReload", nonce);
            for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reload witness does not follow reparse directories.");
                if (string.Equals(current.FullName, Project, StringComparison.OrdinalIgnoreCase)) break;
            }
            return path;
        }
        public static Task<string> ArmAndWait(string nonce)
        {
            var directory = OwnedDirectory(nonce);
            if (_pending != null || !string.IsNullOrEmpty(SessionState.GetString(Key, "")))
                throw new InvalidOperationException("An owned Reload witness is already armed.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Reload witness requires stable EditMode.");
            var id = UPilotBridge.Instance.ObserveQueue("").executingCommandId;
            var entry = UPilotOperationTracker.Instance.GetEntriesCopy().SingleOrDefault(value => value.CommandId == id);
            if (entry == null || entry.CommandName != "reflection.call" || entry.CompletedAt.HasValue)
                throw new InvalidOperationException("Arm only through the currently executing reflection command.");
            if (Directory.Exists(directory)) throw new InvalidOperationException("Witness directory must be new; do not replay.");
            Directory.CreateDirectory(directory);
            using var process = Process.GetCurrentProcess();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var witness = new Witness
            {
                nonce = nonce, directory = directory, originalCommandId = id, commandName = entry.CommandName,
                phaseAtArm = entry.Phase, domainBefore = DomainToken,
                serverLifetimeBefore = SessionState.GetString("UPilot.ServerLifetime", ""),
                processIdBefore = process.Id, processCreatedBefore = process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                armedAt = now, deadlineAt = now + 30000, effectCount = 1
            };
            File.WriteAllText(Path.Combine(directory, "effect.json"), "{\"effectCount\":1}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "armed.json"), JsonUtility.ToJson(witness, true), new UTF8Encoding(false));
            _witness = witness;
            _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            SessionState.SetString(Key, JsonUtility.ToJson(witness));
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.update += CheckDeadline;
            // One update callback, not a repeated request; the fixed deadline is unchanged.
            EditorApplication.update += RequestOwnedReload;
            return _pending.Task;
        }
        private static void RequestOwnedReload()
        {
            EditorApplication.update -= RequestOwnedReload;
            var witness = _witness;
            if (_pending == null || witness == null) return;
            // These witnesses distinguish an uncalled update callback, request failure,
            // and a request that returned but never reached beforeAssemblyReload.
            witness.reloadRequestStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                SaveWitness(witness, "reload-requested.json");
                EditorUtility.RequestScriptReload();
                witness.reloadRequestReturnedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SaveWitness(witness, "reload-requested.json");
            }
            catch (Exception ex)
            {
                witness.reloadRequestError = ex.GetType().FullName;
                witness.failure = "Owned Reload request failed.";
                try { SaveWitness(witness, "reload-requested.json"); }
                finally { Disarm(witness.nonce); }
            }
        }
        private static void SaveWitness(Witness witness, string name) =>
            File.WriteAllText(Path.Combine(witness.directory, name), JsonUtility.ToJson(witness, true), new UTF8Encoding(false));
        private static void BeforeReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.update -= CheckDeadline;
            var witness = _witness;
            if (witness == null) return;
            witness.beforeReloadAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entry = UPilotOperationTracker.Instance.GetEntriesCopy().SingleOrDefault(value => value.CommandId == witness.originalCommandId);
            witness.activeBeforeReload = entry != null && !entry.CompletedAt.HasValue;
            witness.phaseBeforeReload = entry == null ? "unknown" : entry.Phase;
            witness.retainedInvocationsBefore = UPilotOperationTracker.Instance.GetEntriesCopy()
                .Count(value => value.CommandId == witness.originalCommandId);
            if (entry != null)
            {
                lock (entry)
                {
                    witness.dispatchCountBefore = entry.DispatchCount;
                    witness.executionCountBefore = entry.ExecutionCount;
                    witness.mainThreadActionCountBefore = entry.MainThreadActionCount;
                }
            }
            try { File.WriteAllText(Path.Combine(witness.directory, "before-reload.json"), JsonUtility.ToJson(witness, true), new UTF8Encoding(false)); }
            catch (Exception ex) { witness.failure = ex.GetType().Name; }
            SessionState.SetString(Key, JsonUtility.ToJson(witness));
        }
        private static void ObserveAfterReload()
        {
            EditorApplication.update -= ObserveAfterReload;
            var raw = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(raw)) return;
            SessionState.EraseString(Key); // Witness finalization only, never original task recovery.
            Witness witness;
            string directory;
            try
            {
                witness = JsonUtility.FromJson<Witness>(raw);
                if (witness == null) return;
                directory = OwnedDirectory(witness.nonce);
                if (!string.Equals(directory, witness.directory, StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (Exception ex)
            {
                // Untrusted/corrupt metadata never authorizes a file write or task recovery.
                UnityEngine.Debug.LogWarning("[UPilot] Owned Reload witness metadata rejected: " + ex.GetType().Name);
                return;
            }
            try
            {
                using var process = Process.GetCurrentProcess();
                witness.domainAfter = DomainToken;
                witness.processIdAfter = process.Id;
                witness.processCreatedAfter = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                witness.serverLifetimeAfter = SessionState.GetString("UPilot.ServerLifetime", "");
                witness.observedAfterAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                witness.realDomainReload = witness.beforeReloadAt > 0 && witness.domainBefore != witness.domainAfter;
                witness.ownedEffectUnchanged = File.ReadAllText(Path.Combine(directory, "effect.json")) == "{\"effectCount\":1}";
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(witness.failure)) witness.failure = ex.GetType().Name;
            }
            finally
            {
                // Only this witness's callback/SessionState are released. This is not
                // proof that the interrupted command or arbitrary Unity resources stopped.
                witness.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                witness.resourcesReleased = true;
                try { SaveWitness(witness, "after-reload.json"); }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[UPilot] Owned Reload witness receipt write failed: " + ex.GetType().Name);
                }
            }
        }

        private static void CheckDeadline()
        {
            if (_witness != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _witness.deadlineAt) Disarm(_witness.nonce);
        }
        public static bool Disarm(string nonce)
        {
            OwnedDirectory(nonce);
            if (_witness == null || _witness.nonce != nonce) return false;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.update -= CheckDeadline;
            EditorApplication.update -= RequestOwnedReload;
            SessionState.EraseString(Key);
            var pending = _pending;
            var witness = _witness;
            _pending = null;
            _witness = null;
            witness.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            witness.resourcesReleased = true;
            if (string.IsNullOrEmpty(witness.failure)) witness.failure = "Owned witness ended without observing Reload.";
            try { SaveWitness(witness, "probe-ended.json"); }
            finally { pending?.TrySetException(new TimeoutException("Owned witness ended without recreating the original command.")); }
            return true;
        }
    }
}
