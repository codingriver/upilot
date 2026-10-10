using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCommandPhaseTests
    {
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
