using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Observe the production producer, but never enqueue work into the connected Bridge.
    public sealed class UPilotBridgeQueueTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Set(UPilotBridge bridge, string name, object value) =>
            typeof(UPilotBridge).GetField(name, Private).SetValue(bridge, value);

        [TestCase(0, false, false, false)]
        [TestCase(200, false, false, false)]
        [TestCase(201, false, false, false)]
        [TestCase(1, true, false, false)]
        [TestCase(1, false, true, false)]
        [TestCase(1, false, true, true)]
        [TestCase(201, true, true, true)]
        public void ObserveOwnedQueuePreservesCountsGapsAndActions(
            int trackedCount, bool unknownQueued, bool executing, bool unknownExecuting)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Assert.That(project.Replace('\\', '/'), Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            string prefix = "queue-fixture-" + Guid.NewGuid().ToString("N") + "-";
            string excluded = prefix + "observer";
            var queue = new ConcurrentQueue<Action>();
            var tracked = new ConcurrentDictionary<Action, string>();
            int invoked = 0;
            // Do not call the Bridge constructor: it restores state and subscribes callbacks.
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            Set(bridge, "_mainThreadQueue", queue);
            Set(bridge, "_trackedQueueActions", tracked);
            Set(bridge, "_queueObservationLock", new object());
            for (int i = 0; i < trackedCount; i++)
            {
                int unique = i;
                Action action = () => invoked += unique + 1;
                queue.Enqueue(action);
                Assert.That(tracked.TryAdd(action, prefix + i), Is.True);
            }
            Action observer = () => invoked += 10000;
            queue.Enqueue(observer);
            tracked[observer] = excluded;
            if (unknownQueued) queue.Enqueue(() => invoked += 20000);
            Action running = () => invoked += 30000;
            if (executing)
            {
                Set(bridge, "_executingQueueAction", running);
                if (!unknownExecuting) tracked[running] = prefix + "running";
            }
            var before = queue.ToArray();
            var trackingBefore = tracked.ToArray();
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = bridge.ObserveQueue(excluded);
            string json = JsonUtility.ToJson(snapshot, true);
            var roundTrip = JsonUtility.FromJson<BridgeQueueSnapshot>(json);
            Assert.That(roundTrip.observedAt, Is.InRange(started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.That(roundTrip.serviceLifecycleId, Is.EqualTo(UPilotServiceLifetime.Id));
            Assert.That(roundTrip.queuedCount, Is.EqualTo(trackedCount));
            Assert.That(roundTrip.queuedCommands.Count, Is.EqualTo(Math.Min(trackedCount, 200)));
            Assert.That(roundTrip.queuedCommands.Select(item => item.commandId),
                Is.EqualTo(Enumerable.Range(0, Math.Min(trackedCount, 200)).Select(i => prefix + i)));
            Assert.That(roundTrip.executingCount, Is.EqualTo(executing ? 1 : 0));
            if (executing && !unknownExecuting)
                Assert.That(roundTrip.executingCommandId, Is.EqualTo(prefix + "running"));
            else Assert.That(string.IsNullOrEmpty(roundTrip.executingCommandId), Is.True);
            Assert.That(roundTrip.untrackedCount, Is.EqualTo((unknownQueued ? 1 : 0) + (executing && unknownExecuting ? 1 : 0)));
            bool truncated = trackedCount > 200 || roundTrip.activeCount > roundTrip.activeCommands.Count;
            Assert.That(roundTrip.truncated, Is.EqualTo(truncated));
            Assert.That(roundTrip.complete, Is.EqualTo(!truncated && roundTrip.untrackedCount == 0));
            Assert.That(queue.ToArray(), Is.EqualTo(before), "Observation must not dequeue or execute actions.");
            Assert.That(tracked.ToArray(), Is.EquivalentTo(trackingBefore), "Observation must not repair tracking state.");
            Assert.That(invoked, Is.Zero);
            var repeated = bridge.ObserveQueue(excluded);
            Assert.That(repeated.queuedCommands.Select(item => item.commandId),
                Is.EqualTo(roundTrip.queuedCommands.Select(item => item.commandId)));
            Assert.That(repeated.untrackedCount, Is.EqualTo(roundTrip.untrackedCount));
            // The queried command is excluded from executing counts, not silently removed.
            if (executing && !unknownExecuting)
            {
                var self = bridge.ObserveQueue(prefix + "running");
                Assert.That(self.executingCount, Is.Zero);
                Assert.That(self.queuedCount, Is.EqualTo(trackedCount + 1));
                Assert.That(queue.ToArray(), Is.EqualTo(before));
                Assert.That(invoked, Is.Zero);
            }
            string directory = Path.Combine(project, "Log", "Stage4", "BridgeQueue",
                TestContext.CurrentContext.Test.Name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "snapshot.json"), json);
            File.WriteAllText(Path.Combine(directory, "checks.txt"),
                "production ObserveQueue; JsonUtility roundtrip; owned queue unchanged; invoked=0; no live queue injection");
        }
    }
}
