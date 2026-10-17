using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCommandPhaseTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static double WatchdogAt(UPilotBridge bridge) =>
            (double)typeof(UPilotBridge).GetField("_lastWatchdogCheck", Private).GetValue(bridge);
        private static long PumpAt(UPilotBridge bridge) =>
            (long)typeof(UPilotBridge).GetField("_lastMainThreadPumpAt", Private).GetValue(bridge);
        private static UPilotBridge IsolatedWatchdogBridge()
        {
            // Constructors restore live state and subscribe callbacks; this instance only checks our tracker.
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            typeof(UPilotBridge).GetField("_watchdogIntervalMs", Private).SetValue(bridge, 2000);
            return bridge;
        }

        [Test]
        public void WatchdogOnlyChecksWhileOwnedTrackerHasActiveContexts()
        {
            var tracker = new UPilotOperationTracker();
            var bridge = IsolatedWatchdogBridge();
            bridge.CheckWatchdog(tracker, 100);
            Assert.That(tracker.HasActiveCommands, Is.False);
            Assert.That(WatchdogAt(bridge), Is.Zero);
            var first = tracker.BeginOperation("watchdog-owned-first", "fixture.watchdog_first");
            var second = tracker.BeginOperation("watchdog-owned-second", "fixture.watchdog_second");
            Assert.That(tracker.HasActiveCommands, Is.True);
            bridge.CheckWatchdog(tracker, 100);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(100), "The first activity after an overdue idle period is eligible immediately.");
            bridge.CheckWatchdog(tracker, 102);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(100));
            bridge.CheckWatchdog(tracker, 102.01);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(102.01));
            first.Complete("owned watchdog cleanup"); tracker.EndOperation(first.CommandId);
            Assert.That(tracker.HasActiveCommands, Is.True);
            bridge.CheckWatchdog(tracker, 105);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(105));
            second.Complete("owned watchdog cleanup"); tracker.EndOperation(second.CommandId);
            Assert.That(tracker.HasActiveCommands, Is.False);
            bridge.CheckWatchdog(tracker, 1000);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(105));
            tracker.BeginOperation("watchdog-owned-reset", "fixture.watchdog_reset");
            bridge.CheckWatchdog(tracker, 1001);
            tracker.AbortActive(); // This tracker is isolated; no shared activity is cleared.
            Assert.That(tracker.HasActiveCommands, Is.False);
            bridge.CheckWatchdog(tracker, 2000);
            Assert.That(WatchdogAt(bridge), Is.EqualTo(1001));
        }

        [Test]
        public void WatchdogHotIntervalChangesUseLastRealCheckWithoutChangingCommandClock()
        {
            var key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.WatchdogIntervalSeconds));
            bool hadKey = EditorPrefs.HasKey(key);
            int original = EditorPrefs.GetInt(key);
            var bridge = IsolatedWatchdogBridge();
            using var reconnectChanged = new SemaphoreSlim(0, 1);
            typeof(UPilotBridge).GetField("_reconnectIntervalChanged", Private).SetValue(bridge, reconnectChanged);
            var changed = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), bridge,
                typeof(UPilotBridge).GetMethod("ApplyTimingPreferenceChange", Private));
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("watchdog-owned-schedule", "fixture.watchdog_schedule");
            var entry = tracker.GetEntriesCopy().Single();
            var received = entry.ReceivedAt;
            UPilotTimingSettings.Changed += changed;
            try
            {
                UPilotTimingSettings.WatchdogIntervalSeconds = 2;
                bridge.CheckWatchdog(tracker, 100);
                UPilotTimingSettings.WatchdogIntervalSeconds = 10;
                bridge.CheckWatchdog(tracker, 103);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(100), "Lengthening postpones from the last actual check.");
                UPilotTimingSettings.WatchdogIntervalSeconds = 1;
                bridge.CheckWatchdog(tracker, 103);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(103), "Shortening makes an overdue check eligible.");
                bridge.CheckWatchdog(tracker, 104);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(103));
                bridge.CheckWatchdog(tracker, 104.01);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(104.01));
                EditorPrefs.DeleteKey(key);
                UPilotTimingSettings.PreferencesReset();
                bridge.CheckWatchdog(tracker, 105.5);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(104.01));
                bridge.CheckWatchdog(tracker, 106.02);
                Assert.That(WatchdogAt(bridge), Is.EqualTo(106.02));
                Assert.That(entry.ReceivedAt, Is.EqualTo(received));
            }
            finally
            {
                UPilotTimingSettings.Changed -= changed;
                context.Complete("owned scheduling fixture cleanup"); tracker.EndOperation(context.CommandId);
                if (hadKey) EditorPrefs.SetInt(key, original); else EditorPrefs.DeleteKey(key);
                UPilotTimingSettings.PreferencesReset();
            }
        }

        [TestCase("queued", 10)]
        [TestCase("executing", 30)]
        [TestCase("waiting", 120)]
        [TestCase("received", 300)]
        public void WatchdogRetainsPhaseThresholdFromOriginalReceivedTime(string phase, int thresholdSeconds)
        {
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("watchdog-owned-threshold", "fixture.watchdog_threshold");
            var entry = tracker.GetEntriesCopy().Single();
            // Inject elapsed age into this isolated fixture; no claim of real long-running execution.
            entry.ReceivedAt = DateTime.Now.AddSeconds(-(thresholdSeconds - 5));
            context.SetPhase(phase);
            Assert.That(tracker.RunWatchdog(), Is.Empty);
            Assert.That(entry.IsStuck, Is.False);
            entry.ReceivedAt = DateTime.Now.AddSeconds(-(thresholdSeconds + 5));
            context.SetPhase(phase); // A phase write must not reset the received clock.
            Assert.That(tracker.RunWatchdog(), Is.EqualTo(new[] { context.CommandName }));
            Assert.That(entry.IsStuck, Is.True);
            var warnedSteps = entry.Steps.Count;
            Assert.That(tracker.RunWatchdog(), Is.Empty);
            Assert.That(entry.Steps.Count, Is.EqualTo(warnedSteps));
            context.Complete("owned threshold fixture cleanup"); tracker.EndOperation(context.CommandId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WatchdogSkipsTerminalContextBeforeHandlerFinally(bool failed)
        {
            var tracker = new UPilotOperationTracker();
            var bridge = IsolatedWatchdogBridge();
            var context = tracker.BeginOperation("watchdog-owned-terminal", "fixture.watchdog_terminal");
            var entry = tracker.GetEntriesCopy().Single();
            entry.ReceivedAt = DateTime.Now.AddSeconds(-305);
            if (failed)
            {
                if (Logger.MinLevel <= Logger.LogLevel.Error)
                    LogAssert.Expect(LogType.Error, new Regex("FAIL fixture.watchdog_terminal id=" + context.CommandId));
                context.Fail("fixture_failure", "owned terminal filter fixture");
            }
            else context.Complete("owned terminal filter fixture");
            var steps = entry.Steps.Count;
            Assert.That(tracker.HasActiveCommands, Is.True, "The handler's finally has not yet ended this context.");
            bridge.CheckWatchdog(tracker, 100);
            Assert.That(entry.IsStuck, Is.False);
            Assert.That(entry.Steps.Count, Is.EqualTo(steps));
            Assert.That(tracker.GetCriticallyStuckCommandIds(), Is.Empty);
            tracker.EndOperation(context.CommandId);
            Assert.That(tracker.HasActiveCommands, Is.False);
        }

        [Test]
        public void CriticalWatchdogRetainsAbsoluteThresholdWithoutRestartingUnity()
        {
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("watchdog-owned-critical", "fixture.watchdog_critical");
            var entry = tracker.GetEntriesCopy().Single();
            context.SetPhase("waiting");
            entry.ReceivedAt = DateTime.Now.AddSeconds(-295);
            Assert.That(tracker.GetCriticallyStuckCommandIds(), Is.Empty);
            entry.ReceivedAt = DateTime.Now.AddSeconds(-305);
            Assert.That(entry.IsStuck, Is.False);
            Assert.That(tracker.GetCriticallyStuckCommandIds(), Is.EqualTo(new[] { context.CommandId }),
                "The critical criterion is independent of the warning marker and phase.");
            context.Complete("owned critical fixture cleanup");
            Assert.That(tracker.GetCriticallyStuckCommandIds(), Is.Empty);
            tracker.EndOperation(context.CommandId);
        }

        [Test]
        public void WaitDelayUsesTheSameFixedMonotonicDeadline()
        {
            var deadline = OperationContext.CreateWaitDeadline(600000);
            var remaining = OperationContext.RemainingWaitMilliseconds(deadline);
            Assert.That(remaining, Is.GreaterThan(0).And.LessThanOrEqualTo(600000));
            Assert.That(OperationContext.RemainingWaitMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - 1), Is.Zero);
            Assert.That(OperationContext.RemainingWaitMilliseconds(long.MaxValue), Is.EqualTo(int.MaxValue));
        }

        [TestCase(1)]
        [TestCase(120)]
        [TestCase(600)]
        public void ExplicitCompileWaitHonorsItsFixedMonotonicDeadline(int timeoutSeconds)
        {
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("bounded-wait-" + timeoutSeconds, "compile.wait");
            var entry = tracker.GetEntriesCopy().Single();
            entry.ReceivedAt = DateTime.Now.AddSeconds(-605);
            var originalReceivedAt = entry.ReceivedAt;
            long deadline = System.Diagnostics.Stopwatch.Frequency * timeoutSeconds;
            context.SetWaitingUntil(deadline);
            context.SetPhase("waiting");
            context.SetWaitingUntil(deadline + System.Diagnostics.Stopwatch.Frequency * 600);
            Assert.That(entry.WaitingDeadlineTick, Is.EqualTo(deadline));
            Assert.That(entry.ReceivedAt, Is.EqualTo(originalReceivedAt));
            Assert.That(tracker.RunWatchdog(deadline - 1), Is.Empty);
            Assert.That(tracker.GetCriticallyStuckCommandIds(deadline - 1), Is.Empty);
            Assert.That(tracker.RunWatchdog(deadline), Is.EqualTo(new[] { "compile.wait" }));
            Assert.That(tracker.GetCriticallyStuckCommandIds(deadline), Is.EqualTo(new[] { context.CommandId }));
            var steps = entry.Steps.Count;
            Assert.That(tracker.RunWatchdog(deadline + 1), Is.Empty);
            Assert.That(entry.Steps.Count, Is.EqualTo(steps));
            context.Complete();
            Assert.That(tracker.GetCriticallyStuckCommandIds(deadline + 1), Is.Empty);
            tracker.EndOperation(context.CommandId);
        }

        [TestCase("queued")]
        [TestCase("executing")]
        public void WaitAllowanceDoesNotExemptQueueOrResultSending(string phase)
        {
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("wait-exit-" + phase, "compile.request");
            var entry = tracker.GetEntriesCopy().Single();
            entry.ReceivedAt = DateTime.Now.AddSeconds(-305);
            context.SetWaitingUntil(1000);
            context.SetPhase(phase);
            Assert.That(tracker.RunWatchdog(999), Is.EqualTo(new[] { "compile.request" }));
            Assert.That(tracker.GetCriticallyStuckCommandIds(999), Is.EqualTo(new[] { context.CommandId }));
            context.Complete();
            tracker.EndOperation(context.CommandId);
        }

        [Test]
        public void ThirtyOneSecondCompileRequestWaitIsNotExecutingStuck()
        {
            var tracker = new UPilotOperationTracker();
            var context = tracker.BeginOperation("compile-wait-31", "compile.request");
            var entry = tracker.GetEntriesCopy().Single();
            entry.ReceivedAt = DateTime.Now.AddSeconds(-31);
            context.SetWaitingUntil(120);
            Assert.That(tracker.RunWatchdog(31), Is.Empty);
            Assert.That(entry.Phase, Is.EqualTo("waiting"));
            context.Complete();
            context.SetWaitingUntil(600);
            Assert.That(entry.Phase, Is.EqualTo("completed"));
            Assert.That(entry.WaitingDeadlineTick, Is.EqualTo(120));
            tracker.EndOperation(context.CommandId);
        }

        [Test]
        public void BridgeReportsOneDetailedWarningForOneStuckOperation()
        {
            var tracker = new UPilotOperationTracker();
            var bridge = IsolatedWatchdogBridge();
            var context = tracker.BeginOperation("watchdog-one-warning", "fixture.one_watchdog_warning");
            var entry = tracker.GetEntriesCopy().Single();
            entry.ReceivedAt = DateTime.Now.AddSeconds(-35);
            context.SetPhase("executing");
            int warnings = 0;
            string warning = null;
            void Observe(string message, string stack, LogType type)
            {
                if (type == LogType.Warning && message.Contains(context.CommandName)) { warnings++; warning = message; }
            }
            var originalLevel = Logger.MinLevel;
            try
            {
                Logger.MinLevel = Logger.LogLevel.Warning;
                Application.logMessageReceived += Observe;
                bridge.CheckWatchdog(tracker, 100);
                bridge.CheckWatchdog(tracker, 103);
                Assert.That(warnings, Is.EqualTo(1));
                Assert.That(warning, Does.Contain(context.CommandId).And.Contain("phase=executing"));
            }
            finally
            {
                Application.logMessageReceived -= Observe;
                Logger.MinLevel = originalLevel;
                context.Complete(); tracker.EndOperation(context.CommandId);
            }
        }

        [Serializable]
        private sealed class WatchdogPumpWitness
        {
            public string project, commandId;
            public double activeFirst, activeSecond, idleBefore, idleAfter, idleObservedSeconds;
            public long idlePumpBefore, idlePumpAfter;
            public bool activityObserved, idleObserved, ownedReleased, settingsRestored;
        }

        [UnityTest]
        public IEnumerator ProductionWatchdogChecksActiveCommandsAndStopsScanningWhenIdle()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!string.Equals(project, @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("Production watchdog acceptance requires the exact canonical project.");
            var key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.WatchdogIntervalSeconds));
            bool hadKey = EditorPrefs.HasKey(key);
            int original = EditorPrefs.GetInt(key);
            var tracker = UPilotOperationTracker.Instance;
            var bridge = UPilotBridge.Instance;
            var id = "watchdog-pump-" + Guid.NewGuid().ToString("N");
            var context = tracker.BeginOperation(id, "fixture.watchdog_pump");
            var witness = new WatchdogPumpWitness { project = project, commandId = id };
            double deadline = EditorApplication.timeSinceStartup + 15;
            try
            {
                UPilotTimingSettings.WatchdogIntervalSeconds = 1;
                double before = WatchdogAt(bridge);
                while (WatchdogAt(bridge) <= before && EditorApplication.timeSinceStartup < deadline) yield return null;
                witness.activeFirst = WatchdogAt(bridge);
                Assert.That(witness.activeFirst, Is.GreaterThan(before), "The actual production pump must check our active command.");
                while (WatchdogAt(bridge) <= witness.activeFirst && EditorApplication.timeSinceStartup < deadline) yield return null;
                witness.activeSecond = WatchdogAt(bridge);
                Assert.That(witness.activeSecond - witness.activeFirst, Is.GreaterThanOrEqualTo(1));
                witness.activityObserved = true;
                context.Complete("production watchdog activity observed"); tracker.EndOperation(id);

                // Wait for unrelated commands to finish naturally; never clear shared activity.
                while (!witness.idleObserved && EditorApplication.timeSinceStartup < deadline)
                {
                    while (tracker.HasActiveCommands && EditorApplication.timeSinceStartup < deadline) yield return null;
                    if (EditorApplication.timeSinceStartup >= deadline) break;
                    witness.idleBefore = WatchdogAt(bridge); witness.idlePumpBefore = PumpAt(bridge);
                    double idleStarted = EditorApplication.timeSinceStartup;
                    bool interrupted = false;
                    while (EditorApplication.timeSinceStartup - idleStarted < 1.25 && EditorApplication.timeSinceStartup < deadline)
                    {
                        yield return null;
                        if (tracker.HasActiveCommands || WatchdogAt(bridge) != witness.idleBefore) { interrupted = true; break; }
                    }
                    witness.idleAfter = WatchdogAt(bridge); witness.idlePumpAfter = PumpAt(bridge);
                    witness.idleObservedSeconds = EditorApplication.timeSinceStartup - idleStarted;
                    witness.idleObserved = !interrupted && witness.idleObservedSeconds >= 1.25 && witness.idlePumpAfter > witness.idlePumpBefore;
                }
                Assert.That(witness.idleObserved, Is.True, "Within the fixed deadline, observe natural idle across one interval while the production pump continues.");
                Assert.That(witness.idleAfter, Is.EqualTo(witness.idleBefore));
            }
            finally
            {
                context.Complete("production watchdog fixture cleanup"); tracker.EndOperation(id);
                if (hadKey) EditorPrefs.SetInt(key, original); else EditorPrefs.DeleteKey(key);
                UPilotTimingSettings.PreferencesReset();
                witness.ownedReleased = tracker.GetContext(id) == null;
                witness.settingsRestored = EditorPrefs.HasKey(key) == hadKey && (!hadKey || EditorPrefs.GetInt(key) == original);
                var directory = Path.Combine(project, "Log", "UPilotValidation", "Watchdog_20261014");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, id + ".json"), JsonUtility.ToJson(witness, true));
            }
            Assert.That(witness.ownedReleased, Is.True);
            Assert.That(witness.settingsRestored, Is.True);
        }

        [Test]
        public void PhaseAndStepsCannotChangeCompletedEntry()
        {
            var entry = new OperationLogEntry { ReceivedAt = DateTime.Now, Phase = "received" };
            var context = new OperationContext(entry, UPilotOperationTracker.Instance);
            context.SetPhase("queued");
            Assert.That(entry.Phase, Is.EqualTo("queued"));
            context.SetPhase("executing");
            context.SetPhase("waiting");
            Assert.That(entry.Phase, Is.EqualTo("waiting"));
            context.Complete("owned phase fixture");
            var completedAt = entry.CompletedAt;
            var steps = entry.Steps.Count;
            context.SetPhase("executing");
            context.Step("late dispatch");
            context.Complete("late completion");
            Assert.That(entry.Phase, Is.EqualTo("completed"));
            Assert.That(entry.CompletedAt, Is.EqualTo(completedAt));
            Assert.That(entry.Steps.Count, Is.EqualTo(steps));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task RouterRecordsAndRethrowsOriginalFailureWithoutDuplicatingBridgeDiagnostics(bool cancelled)
        {
            var router = new UPilotCommandRouter();
            var id = "router-failure-" + Guid.NewGuid().ToString("N");
            Exception expected = cancelled
                ? new OperationCanceledException(new System.Threading.CancellationToken(true))
                : new InvalidOperationException("router original failure");
            router.Register("fixture.router-failure", (_, __, ___) =>
                System.Threading.Tasks.Task.FromException(expected));
            Exception actual = null;
            try
            {
                await router.TryHandleAsync("fixture.router-failure", id, "{}", default);
            }
            catch (Exception ex)
            {
                actual = ex;
            }
            Assert.That(actual, Is.SameAs(expected));
            var entry = UPilotOperationTracker.Instance.GetEntriesCopy().Single(value => value.CommandId == id);
            Assert.That(entry.Phase, Is.EqualTo("failed"));
            Assert.That(entry.ErrorCode, Is.EqualTo(cancelled ? "CANCELLED" : "EXCEPTION"));
            Assert.That(entry.CompletedAt.HasValue, Is.True);
            Assert.That(entry.ExecutionCount, Is.EqualTo(1));
            Assert.That(UPilotOperationTracker.Instance.GetContext(id), Is.Null);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public async System.Threading.Tasks.Task RouterCountsActualHandlerEntryAndProjectsIt()
        {
            var id = "counts-router-" + Guid.NewGuid().ToString("N");
            var router = new UPilotCommandRouter();
            int effects = 0;
            router.Register("fixture.counts", (_, __, ___) =>
            {
                effects++;
                return System.Threading.Tasks.Task.CompletedTask;
            });
            Assert.That(await router.TryHandleAsync("fixture.counts", id, "{}", default), Is.True);
            var entry = UPilotOperationTracker.Instance.GetEntriesCopy().Single(value => value.CommandId == id);
            Assert.That(effects, Is.EqualTo(1));
            Assert.That(entry.DispatchCount, Is.EqualTo(1));
            Assert.That(entry.ExecutionCount, Is.EqualTo(1));
            Assert.That(entry.MainThreadActionCount, Is.Zero);
            var project = typeof(UPilotOperationService).GetMethod("ToPayload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var payload = (OperationPayload)project.Invoke(null, new object[] { entry });
            Assert.That(payload.dispatchCount, Is.EqualTo(1));
            Assert.That(payload.executionCount, Is.EqualTo(1));
            Assert.That(payload.mainThreadActionCount, Is.Zero);
            Assert.That(payload.countScope, Does.Contain("handler-entry-not-business-effect"));
            Assert.That(payload.terminal, Is.True);
            var context = new OperationContext(entry, UPilotOperationTracker.Instance);
            context.RecordDispatch();
            context.RecordHandlerExecution();
            context.RecordMainThreadAction();
            Assert.That(entry.DispatchCount, Is.EqualTo(1));
            Assert.That(entry.ExecutionCount, Is.EqualTo(1));
            Assert.That(entry.MainThreadActionCount, Is.Zero);
        }

        [Test]
        public void UnobservedEntryCountsAreNotProjectedAsZero()
        {
            var entry = new OperationLogEntry();
            var project = typeof(UPilotOperationService).GetMethod("ToPayload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var payload = (OperationPayload)project.Invoke(null, new object[] { entry });
            Assert.That(payload.dispatchCount, Is.EqualTo(-1));
            Assert.That(payload.executionCount, Is.EqualTo(-1));
            Assert.That(payload.mainThreadActionCount, Is.EqualTo(-1));
        }

        [Test]
        public void SharedWatchdogUsesExplicitPhaseAndDoesNotWarnAfterCompletion()
        {
            var tracker = UPilotOperationTracker.Instance;
            var id = "phase-watchdog-" + Guid.NewGuid().ToString("N");
            var context = tracker.BeginOperation(id, "fixture.phase_watchdog");
            try
            {
                var entry = tracker.GetEntriesCopy().Single(value => value.CommandId == id);
                // Threshold injection, not a claim of 15 seconds of real queueing.
                entry.ReceivedAt = DateTime.Now.AddSeconds(-15);
                context.SetPhase("executing");
                tracker.RunWatchdog();
                Assert.That(entry.IsStuck, Is.False);
                context.SetPhase("queued");
                tracker.RunWatchdog();
                Assert.That(entry.IsStuck, Is.True);
                var warnedSteps = entry.Steps.Count;
                tracker.RunWatchdog();
                Assert.That(entry.Steps.Count, Is.EqualTo(warnedSteps));
                context.Complete("owned watchdog fixture");
                var completedSteps = entry.Steps.Count;
                context.SetPhase("queued");
                tracker.RunWatchdog();
                Assert.That(entry.Phase, Is.EqualTo("completed"));
                Assert.That(entry.Steps.Count, Is.EqualTo(completedSteps));
            }
            finally
            {
                context.Complete("fixture cleanup");
                tracker.EndOperation(id);
            }
        }

        [UnityTest]
        public IEnumerator ProductionEditorPumpPublishesQueueAndExecutionPhasesOnce()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!string.Equals(project, @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("Production pump acceptance requires the exact canonical project.");
            var tracker = UPilotOperationTracker.Instance;
            var id = "phase-pump-" + Guid.NewGuid().ToString("N");
            var context = tracker.BeginOperation(id, "fixture.phase_pump");
            var entry = tracker.GetEntriesCopy().Single(value => value.CommandId == id);
            int effectCount = 0;
            string executionPhase = null;
            bool active = true;
            try
            {
                UPilotBridge.Instance.EnqueueTracked(id, () =>
                {
                    if (!active) return;
                    executionPhase = entry.Phase;
                    effectCount++;
                    context.Complete("owned production pump fixture");
                });
                Assert.That(entry.Phase, Is.EqualTo("queued"));
                float expires = Time.realtimeSinceStartup + 5;
                while (effectCount == 0 && Time.realtimeSinceStartup < expires) yield return null;
                Assert.That(effectCount, Is.EqualTo(1));
                Assert.That(entry.MainThreadActionCount, Is.EqualTo(1));
                Assert.That(entry.DispatchCount, Is.Zero); // This fixture bypasses the router.
                Assert.That(entry.ExecutionCount, Is.Zero);
                Assert.That(executionPhase, Is.EqualTo("executing"));
                Assert.That(entry.Phase, Is.EqualTo("completed"));
                // Complete exposes the localized step; the supplied summary is its Detail.
                Assert.That(entry.CurrentStep, Is.EqualTo("完成"));
                Assert.That(entry.Steps.Last().Step, Is.EqualTo("完成"));
                Assert.That(entry.Steps.Last().Detail, Is.EqualTo("owned production pump fixture"));
                var completedAt = entry.CompletedAt;
                var completedSteps = entry.Steps.Count;
                yield return null;
                Assert.That(effectCount, Is.EqualTo(1));
                Assert.That(entry.CompletedAt, Is.EqualTo(completedAt));
                Assert.That(entry.Steps.Count, Is.EqualTo(completedSteps));
            }
            finally
            {
                active = false;
                context.Complete("fixture cleanup");
                tracker.EndOperation(id);
            }
        }
    }
}
