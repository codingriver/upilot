using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class AutomationImmediateStopTests
    {
        private static readonly List<string> Calls = new();
        private static Action Reenter;
        private static string ReenterAt;
        public sealed class Probe : AutomationStepBase
        {
            private static void Call(string stage, string arguments)
            {
                Calls.Add(stage + ":" + arguments);
                if (stage == ReenterAt) { var callback = Reenter; Reenter = null; callback?.Invoke(); }
            }
            public override void Execute(string run, string item, string context, string arguments)
            { Call("execute", arguments); }
            public override string Poll(string run, string item, string context, string arguments)
            { Call("poll", arguments); return ResultJson(arguments == "wait" ? "Running" : arguments == "fail" ? "Failed" : "Succeeded", "PROBE_FAILURE"); }
            public override void Cancel(string run, string item, string context, string arguments) => Call("cancel", arguments);
            public override string Cleanup(string run, string item, string context, string arguments)
            { Call("cleanup", arguments); return ResultJson(arguments == "cleanup_wait" ? "Running" : "Succeeded"); }
            public override string GetError(string run, string item, string context, string code, string arguments)
            { Call("error", arguments); return AutomationStepJsonCodec.ErrorJson("probe diagnostic", code); }
            public override string Restore(string run, string item, string context, string arguments)
            { Call("restore", arguments); return "{\"status\":\"Restored\"}"; }
        }
        private AutomationStepExecutor _executor;
        private AutomationStepRegistry _registry;
        private string _store;
        private long _now;
        private readonly List<string> _reports = new();
        private AutomationStepRun Live => (AutomationStepRun)typeof(AutomationStepExecutor)
            .GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_executor);
        [SetUp] public void SetUp()
        {
            Calls.Clear(); Reenter = null; ReenterAt = "";
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _store = Path.Combine(Application.dataPath, "../Library/UPilot/StepTests/" + Guid.NewGuid().ToString("N") + ".json");
            _registry = Registry(Entry<Probe>("probe"), Entry<ConsoleCaptureStartStep>("upilot.console_capture_start"));
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
        }
        [TearDown] public void TearDown()
        {
            _executor.Dispose(); Reenter = null;
            foreach (string path in new[] { _store, _store + ".capture", _store + ".tmp" })
            { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(path)) Directory.Delete(path, true); }
            foreach (string path in _reports) if (Directory.Exists(path)) Directory.Delete(path, true);
            _reports.Clear();
        }
        private void Start(params AutomationStepItem[] items)
        {
            var run = _executor.Start(Plan(items), Guid.NewGuid().ToString("N")); _reports.Add(run.reportDirectory);
        }
        private void Pump(int count) { for (int i = 0; i < count; i++) _executor.Tick(); }
        [TestCase(0, "wait", "Normal")]
        [TestCase(1, "wait", "Normal")]
        [TestCase(2, "cleanup_wait", "Normal")]
        [TestCase(3, "cleanup_wait", "Normal")]
        [TestCase(1, "wait", "Finally")]
        [TestCase(3, "cleanup_wait", "Finally")]
        [TestCase(3, "success", "Normal")]
        public void ImmediateStopFencesEveryBusinessStage(int ticks, string arguments, string phase)
        {
            Start(Item("probe", arguments, phase), Item("probe", "following", "Finally")); Pump(ticks);
            var completed = _executor.State.steps.Where(s => s.stage == "Completed").Select(s => s.outcome).ToArray();
            int calls = Calls.Count;
            Assert.That(_executor.StopImmediately(), Is.True); Pump(20);
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_STOPPED_IMMEDIATELY"));
            Assert.That(_executor.State.immediateStopRequestedAtUtcMs, Is.EqualTo(_now));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(_executor.State.steps.Take(completed.Length).Select(s => s.outcome), Is.EqualTo(completed));
            Assert.That(_executor.State.steps.Last().cleanupState, Is.EqualTo("SkippedByImmediateStop"));
            Assert.That(_executor.State.steps.Last().outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(_executor.StopImmediately(), Is.False);
            _executor.RequestCancel(); Pump(5); Assert.That(Calls.Count, Is.EqualTo(calls));
        }
        [TestCase("execute", "success")]
        [TestCase("poll", "success")]
        [TestCase("cleanup", "success")]
        [TestCase("error", "fail")]
        [TestCase("cancel", "fail")]
        public void ReentrantStopCannotOverwriteTerminalOrCallNextBusinessCallback(string stage, string arguments)
        {
            Start(Item("probe", arguments), Item("probe", "following", "Finally"));
            ReenterAt = stage; Reenter = () => {
                Assert.That(_executor.StopImmediately(), Is.True);
                Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe")), "nested-run"));
            };
            Pump(20);
            int stopped = Calls.FindIndex(c => c.StartsWith(stage + ":", StringComparison.Ordinal));
            Assert.That(stopped, Is.GreaterThanOrEqualTo(0));
            Assert.That(Calls.Count, Is.EqualTo(stopped + 1));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("SkippedByImmediateStop"));
            Assert.That(_executor.State.reportCommitJson, Is.Empty);
        }
        [Test] public void StopDuringRestoreFencesCallbackAndKeepsAdmissionClosedUntilItReturns()
        {
            Start(Item("probe", "success"), Item("probe", "following", "Finally")); _executor.Tick();
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            ReenterAt = "restore"; Reenter = () => {
                Assert.That(_executor.StopImmediately(), Is.True);
                Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe")), "nested-run"));
            };
            _executor.RestoreStored(); Pump(10);
            Assert.That(Calls, Is.EqualTo(new[] { "execute:success", "restore:success" }));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.Busy, Is.False);
        }
        [Test] public void EvidenceFinalizationIsBusyAndStopDoesNotCommitSuccess()
        {
            Start(Item("probe", "success")); Pump(3);
            Assert.That(_executor.State.stage, Is.EqualTo("Finalizing"));
            Assert.That(UPilotAutomationStepService.ObserveCurrent(_executor).busy, Is.True);
            _executor.StopImmediately(); Pump(10);
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.State.steps[0].outcome, Is.EqualTo(AutomationStepStatus.Succeeded));
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("Verified"));
            Assert.That(File.Exists(Path.Combine(_executor.State.reportDirectory, "summary.json")), Is.False);
        }
        [Test] public void ExistingFailureIsPreservedAndStopIsAnAdditionalDiagnostic()
        {
            Start(Item("probe", "fail"), Item("probe", "following", "Finally")); Pump(2);
            string failure = _executor.State.error.code;
            _executor.StopImmediately();
            Assert.That(_executor.State.error.code, Is.EqualTo(failure));
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "STEP_STOPPED_IMMEDIATELY"), Is.True);
        }
        [Test] public void PersistFailureDoesNotResumeBusinessOrPoisonNextRun()
        {
            Start(Item("probe", "wait")); Directory.CreateDirectory(_store + ".tmp");
            Assert.That(_executor.StopImmediately(), Is.True); Pump(10);
            Assert.That(_executor.State.status, Is.EqualTo("Aborted")); Assert.That(Calls, Is.Empty);
            Assert.That(_executor.State.resourceRecoveryError, Does.Contain("STEP_STORE_FAILED"));
            Directory.Delete(_store + ".tmp");
            Start(Item("probe", "success")); Pump(10);
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }
        [Test] public void UnverifiedReleaseHasFixedDeadlineAcrossReloadAndRejectsAdmission()
        {
            Start(Item("probe", "wait"));
            Live.captureOwned = true; Live.captureStartIntent = true; Live.captureSessionId = "unverified-private-owner";
            Assert.That(_executor.StopImmediately(), Is.True);
            long stopped = _executor.State.immediateStopRequestedAtUtcMs;
            Assert.That(_executor.State.terminal, Is.True); Assert.That(_executor.Busy, Is.True);
            Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe")), "rejected"));
            _now = stopped + 9000; _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now); _executor.RestoreStored();
            Assert.That(Calls, Is.Empty); Assert.That(_executor.Busy, Is.True);
            Assert.That(_executor.StopImmediately(), Is.False); _executor.Tick();
            _now = stopped + 10000; _executor.Tick();
            Assert.That(_executor.Busy, Is.False); Assert.That(_executor.State.cleanupPending, Is.False);
            Assert.That(_executor.State.captureStopVerified, Is.False);
            Assert.That(_executor.State.resourceRecoveryError, Does.Contain("STEP_IMMEDIATE_STOP_RELEASE_TIMEOUT"));
            Assert.That(_executor.State.immediateStopRequestedAtUtcMs, Is.EqualTo(stopped));
            Start(Item("probe", "success")); Pump(10); Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }
        [Test] public void NewLifetimeDoesNotReplayOldRelease()
        {
            Start(Item("probe", "wait")); Live.captureOwned = true; Live.captureStartIntent = true;
            _executor.StopImmediately(); Live.serviceLifecycleId = "old-lifetime";
            new AutomationStepRunStore(_store).Save(Live);
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            _executor.RestoreStored(); Pump(5);
            Assert.That(_executor.State, Is.Null); Assert.That(_executor.Busy, Is.False); Assert.That(Calls, Is.Empty);
        }
        [Test] public void BorrowedCaptureIsNotStoppedByStepExecutor()
        {
            Start(Item("probe", "wait")); Live.captureSessionId = "outer-operation-owned"; Live.captureOwned = false;
            _executor.StopImmediately();
            Assert.That(_executor.Busy, Is.False); Assert.That(_executor.State.captureStopVerified, Is.False);
            Assert.That(_executor.State.resourceRecoveryError, Is.Empty);
        }
        [Test] public void OwnedCaptureIsReleasedThroughFrameworkWithoutBusinessCleanup()
        {
            if (UPilotConsoleCaptureApi.GetBoundary().ok) Assert.Ignore("Unrelated Capture is active; do not change ownership.");
            Start(Item("upilot.console_capture_start"), Item("probe", "wait"), Item("probe", "following", "Finally"));
            _executor.Tick();
            string session = _executor.State.captureSessionId;
            Assert.That(session, Is.Not.Empty);
            try
            {
                _executor.StopImmediately(); Pump(10);
                Assert.That(_executor.State.captureStopVerified, Is.True);
                Assert.That(_executor.Busy, Is.False);
                Assert.That(UPilotConsoleCaptureApi.Status(session).session.active, Is.False);
                Assert.That(Calls, Is.Empty);
                Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            }
            finally
            {
                var owned = new AutomationRunCapture(_store + ".capture", _executor.State.runId, true);
                if (owned.Observe().active) owned.StopAndVerify(_now, out _, out _);
            }
        }
        [Test] public void ReadOnlySnapshotAndExactStopIdentityDoNotInitializeOrAffectAnotherRun()
        {
            Assert.That(UPilotAutomationStepService.ObserveCurrent(null).initialized, Is.False);
            Start(Item("probe", "wait")); var expected = _executor.State;
            for (int i = 0; i < 10; i++)
            {
                var snapshot = UPilotAutomationStepService.ObserveCurrent(_executor);
                snapshot.run.status = "tampered";
            }
            Assert.That(Calls, Is.Empty); Assert.That(_executor.State.status, Is.EqualTo("Running"));
            var field = typeof(UPilotAutomationStepService).GetField("s_executor", BindingFlags.NonPublic | BindingFlags.Static);
            var original = (AutomationStepExecutor)field.GetValue(null);
            if (original?.Busy == true) Assert.Ignore("Official executor is occupied; do not replace it.");
            field.SetValue(null, _executor);
            try
            {
                Assert.That(UPilotAutomationStepService.StopCurrentImmediately(expected.runId, "wrong", expected.serviceLifecycleId).changed, Is.False);
                Assert.That(UPilotAutomationStepService.StopCurrentImmediately(expected.runId, expected.operationId, "old-lifetime").changed, Is.False);
                Assert.That(UPilotAutomationStepService.StopCurrentImmediately(expected.runId, expected.operationId, expected.serviceLifecycleId).changed, Is.True);
                Assert.That(UPilotAutomationStepService.StopCurrentImmediately(expected.runId, expected.operationId, expected.serviceLifecycleId).changed, Is.False);
                Start(Item("probe", "wait"));
                Assert.That(UPilotAutomationStepService.StopCurrentImmediately(expected.runId, expected.operationId, expected.serviceLifecycleId).changed, Is.False);
                Assert.That(_executor.State.terminal, Is.False);
            }
            finally { field.SetValue(null, original); }
        }
        [Test] public void OldRecordsDefaultToNoImmediateStopMarker()
        { Assert.That(JsonUtility.FromJson<AutomationStepRun>("{\"version\":1}").immediateStopRequestedAtUtcMs, Is.Zero); }
    }
}
