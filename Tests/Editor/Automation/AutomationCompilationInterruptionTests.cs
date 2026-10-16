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
    public sealed class AutomationCompilationInterruptionTests
    {
        private static readonly List<string> Calls = new();
        private static Action Reenter;
        private static string ReenterAt;
        private sealed class GizmosAccess : IAutomationGizmosAccess
        {
            private readonly AutomationGizmosWindow _window = new() { id = "owned-view", view = "scene" };
            private bool _enabled;
            internal Action OnDisable;
            public AutomationGizmosWindow[] Find(bool scene, bool game) => scene ? new[] { _window } : Array.Empty<AutomationGizmosWindow>();
            public bool Read(AutomationGizmosWindow window) => _enabled;
            public void Set(AutomationGizmosWindow window, bool enabled)
            {
                _enabled = enabled;
                if (!enabled) { var callback = OnDisable; OnDisable = null; callback?.Invoke(); }
            }
        }
        public sealed class Probe : AutomationStepBase
        {
            private static void Call(string stage, string arguments)
            {
                Calls.Add(stage + ":" + arguments);
                if (stage == ReenterAt) { var callback = Reenter; Reenter = null; callback?.Invoke(); }
            }
            public override void Execute(string run, string item, string context, string arguments) => Call("execute", arguments);
            public override string Poll(string run, string item, string context, string arguments)
            { Call("poll", arguments); return ResultJson(arguments == "wait" ? "Running" : arguments == "fail" ? "Failed" : "Succeeded", "PROBE_FAILURE"); }
            public override void Cancel(string run, string item, string context, string arguments) => Call("cancel", arguments);
            public override string Cleanup(string run, string item, string context, string arguments)
            { Call("cleanup", arguments); return ResultJson(arguments == "cleanup_wait" ? "Running" : "Succeeded"); }
            public override string GetError(string run, string item, string context, string code, string arguments)
            { Call("error", arguments); return AutomationStepJsonCodec.ErrorJson("probe diagnostic", code); }
            public override string Restore(string run, string item, string context, string arguments)
            { Call("restore", arguments); return RestoreResultJson("Restored"); }
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
            Recreate(false);
        }
        private void Recreate(bool restore = true)
        {
            _executor?.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            if (restore) _executor.RestoreStored();
        }
        [TearDown] public void TearDown()
        {
            if (_executor.State != null) new AutomationStepRunStore(_store).ClearCompilationFence(_executor.State);
            _executor.Dispose(); Reenter = null;
            foreach (string path in new[] { _store, _store + ".capture", _store + ".tmp" })
            { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(path)) Directory.Delete(path, true); }
            foreach (string path in _reports) if (Directory.Exists(path)) Directory.Delete(path, true);
            _reports.Clear();
        }
        private void Start(params AutomationStepItem[] items)
        { var run = _executor.Start(Plan(items), Guid.NewGuid().ToString("N")); _reports.Add(run.reportDirectory); }
        private void Pump(int count = 20) { for (int i = 0; i < count; i++) _executor.Tick(); }
        private void AssertCompilationFailure()
        {
            var run = _executor.State;
            Assert.That(run.status, Is.EqualTo("Failed"));
            Assert.That(run.error.code, Is.EqualTo("STEP_COMPILATION_STARTED"));
            Assert.That(run.error.message, Is.EqualTo("任务执行期间触发 Unity 脚本编译，任务已强制终止。"));
            Assert.That(run.terminal, Is.True);
            var state = UPilotAutomationStepService.Public(run);
            Assert.That(state.status, Is.EqualTo("Failed"));
            Assert.That(state.failureSignature, Is.EqualTo(run.error.code));
            Assert.That(state.error, Is.EqualTo(run.error.code));
            Assert.That(state.detail, Is.EqualTo(run.error.message));
            Assert.That(state.operationId, Is.EqualTo(run.operationId));
            var summary = JsonUtility.FromJson<AutomationReportSummary>(File.ReadAllText(Path.Combine(run.reportDirectory, "summary.json")));
            Assert.That(summary.outcome, Is.EqualTo("Failed"));
            Assert.That(summary.failureSignature, Is.EqualTo(run.error.code));
            Assert.That(run.compilationReportState, Is.EqualTo("Written"));
        }
        [TestCase(0, "wait", "Normal")] [TestCase(1, "wait", "Normal")]
        [TestCase(2, "cleanup_wait", "Normal")] [TestCase(3, "cleanup_wait", "Normal")]
        [TestCase(1, "wait", "Finally")] [TestCase(3, "cleanup_wait", "Finally")]
        [TestCase(3, "success", "Normal")]
        public void CompilationFencesAllNonterminalStages(int ticks, string arguments, string phase)
        {
            Start(Item("probe", arguments, phase), Item("probe", "following", "Finally")); Pump(ticks);
            int calls = Calls.Count;
            string run = _executor.State.runId, operation = _executor.State.operationId;
            Assert.That(_executor.InterruptForCompilation(), Is.True); Pump();
            Assert.That(Calls.Count, Is.EqualTo(calls));
            AssertCompilationFailure();
            Assert.That(_executor.State.runId, Is.EqualTo(run)); Assert.That(_executor.State.operationId, Is.EqualTo(operation));
            Assert.That(_executor.State.compilationPhase, Is.Not.Empty);
            Assert.That(_executor.State.steps.Last().outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(_executor.State.steps.Last().cleanupState, Is.EqualTo("SkippedByCompilation"));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(_executor.InterruptForCompilation(), Is.False);
        }
        [TestCase("execute", "success")] [TestCase("poll", "success")]
        [TestCase("cleanup", "success")] [TestCase("error", "fail")] [TestCase("cancel", "fail")]
        public void ReentrantCompilationIgnoresLateCallbackAndBlocksWrites(string stage, string arguments)
        {
            Start(Item("probe", arguments), Item("probe", "following", "Finally"));
            ReenterAt = stage; Reenter = () =>
            {
                // Exercise the actual service event handler and its active-callback executor selection.
                typeof(UPilotAutomationStepService).GetMethod("OnCompilationStarted", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { new object() });
                Assert.That(_executor.State.terminal, Is.True);
                Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe")), "nested"));
                Assert.Throws<AutomationStepException>(() => _executor.WriteTextArtifact(_executor.State.runId,
                    _executor.State.steps[0].instanceId, "evidence", "late.json", "{}"));
            };
            Pump();
            int stopped = Calls.FindIndex(c => c.StartsWith(stage + ":", StringComparison.Ordinal));
            Assert.That(stopped, Is.GreaterThanOrEqualTo(0)); Assert.That(Calls.Count, Is.EqualTo(stopped + 1));
            Assert.That(_executor.State.steps[0].outcome, Is.EqualTo(AutomationStepStatus.Failed));
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("SkippedByCompilation"));
            AssertCompilationFailure();
            Assert.That(Directory.Exists(Path.Combine(_executor.State.reportDirectory, "attachments")), Is.False);
        }
        [Test] public void CompilationReplacesPrimaryFailureAndPreservesOriginalDiagnostic()
        {
            Start(Item("probe", "fail"), Item("probe", "following", "Finally")); Pump(2);
            string original = _executor.State.error.code;
            _executor.InterruptForCompilation(); Pump(); AssertCompilationFailure();
            Assert.That(_executor.State.secondaryErrors.Count(e => e.code == original), Is.EqualTo(1));
            Assert.That(File.ReadAllText(Path.Combine(_executor.State.reportDirectory, "events.jsonl")), Does.Contain(original));
        }
        [Test] public void FailedDiskSaveCannotReplayOldRunAfterReload()
        {
            Start(Item("probe", "wait"), Item("probe", "following", "Finally")); Pump(1);
            Directory.CreateDirectory(_store + ".tmp");
            _executor.InterruptForCompilation(); int calls = Calls.Count;
            Assert.That(new AutomationStepRunStore(_store).Load().terminal, Is.False);
            Recreate(); Pump(); Assert.That(Calls.Count, Is.EqualTo(calls));
            AssertCompilationFailure();
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "STEP_STORE_FAILED"), Is.True);
            Directory.Delete(_store + ".tmp"); Recreate(); Pump(); AssertCompilationFailure();
            Assert.That(new AutomationStepRunStore(_store).Load().terminal, Is.True);
            Assert.That(new AutomationStepRunStore(_store).CompilationFence(_executor.State), Is.Null);
        }
        [Test] public void ReleaseDeadlineAndFailureSurviveReloadAndRepeatedCompilation()
        {
            Start(Item("probe", "wait"));
            Live.captureOwned = true; Live.captureStartIntent = true; Live.captureSessionId = "unverified-private-owner";
            _executor.InterruptForCompilation(); long started = _executor.State.compilationStartedAtUtcMs;
            _now = started + 9000; Recreate();
            Assert.That(_executor.Busy, Is.True); Assert.That(_executor.InterruptForCompilation(), Is.False);
            Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe")), "nested"));
            _now = started + 10000; Pump(); AssertCompilationFailure();
            Assert.That(_executor.Busy, Is.False); Assert.That(_executor.State.captureStopVerified, Is.False);
            Assert.That(_executor.State.resourceRecoveryError, Does.Contain("STEP_COMPILATION_RELEASE_TIMEOUT"));
            Assert.That(_executor.State.compilationStartedAtUtcMs, Is.EqualTo(started)); Assert.That(Calls, Is.Empty);
        }
        [Test] public void CompilationWithoutReloadStaysFailedAndDoesNotPoisonTheNextRun()
        {
            Start(Item("probe", "wait")); _executor.InterruptForCompilation(); Pump();
            AssertCompilationFailure(); _now += 60000; Pump(); AssertCompilationFailure();
            Start(Item("probe", "success")); Pump(); Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.compilationStartedAtUtcMs, Is.Zero);
        }
        [Test] public void StandaloneReloadRestoresExistingBehaviorAndCompletedRunsAreUnchanged()
        {
            Start(Item("probe", "success")); Pump(1); Recreate(); Pump();
            Assert.That(Calls, Does.Contain("restore:success")); Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            string json = JsonUtility.ToJson(_executor.State);
            Assert.That(_executor.InterruptForCompilation(), Is.False); Assert.That(JsonUtility.ToJson(_executor.State), Is.EqualTo(json));
        }
        [Test] public void CompilationDuringRestoreUsesUnpublishedExecutorAndNeverContinues()
        {
            Start(Item("probe", "success")); Pump(1);
            ReenterAt = "restore"; Reenter = () => typeof(UPilotAutomationStepService)
                .GetMethod("OnCompilationStarted", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new object() });
            Recreate(); Pump(); AssertCompilationFailure();
            Assert.That(Calls, Is.EqualTo(new[] { "execute:success", "restore:success" }));
        }
        [Test] public void PartialNormalReportExportsArePreservedAndFailureReportIsAuthoritative()
        {
            Start(Item("probe", "success")); Pump(3);
            string previous = _executor.State.reportDirectory;
            Assert.That(_executor.State.stage, Is.EqualTo("Finalizing"));
            File.WriteAllText(Path.Combine(previous, AutomationReportWriter.TextReportFileName), "partial original report");
            _executor.InterruptForCompilation(); Pump(); AssertCompilationFailure();
            Assert.That(File.ReadAllText(Path.Combine(previous, AutomationReportWriter.TextReportFileName)), Is.EqualTo("partial original report"));
            Assert.That(_executor.State.reportDirectory, Is.EqualTo(Path.Combine(previous, "compilation-interruption")));
            Recreate(); Pump(); AssertCompilationFailure();
        }
        [Test] public void BorrowedCaptureAndBusinessCleanupAreNotClaimedAsRestored()
        {
            Start(Item("probe", "wait")); Pump(1); Live.captureSessionId = "borrowed"; Live.captureOwned = false;
            _executor.InterruptForCompilation(); Pump(); AssertCompilationFailure();
            Assert.That(_executor.State.captureStopVerified, Is.False);
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("SkippedByCompilation"));
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait" }));
        }
        [Test] public void OwnedCaptureIsReleasedWithoutAnyBusinessCleanup()
        {
            if (UPilotConsoleCaptureApi.GetBoundary().ok) Assert.Ignore("Unrelated Capture is active; preserve its ownership.");
            Start(Item("upilot.console_capture_start"), Item("probe", "wait"), Item("probe", "following", "Finally"));
            Pump(1); string session = _executor.State.captureSessionId;
            Assert.That(session, Is.Not.Empty);
            try
            {
                _executor.InterruptForCompilation(); Pump(); AssertCompilationFailure();
                Assert.That(_executor.State.captureStopVerified, Is.True); Assert.That(_executor.Busy, Is.False);
                Assert.That(UPilotConsoleCaptureApi.Status(session).session.active, Is.False); Assert.That(Calls, Is.Empty);
            }
            finally
            {
                var owned = new AutomationRunCapture(_store + ".capture", _executor.State.runId, true);
                if (owned.Observe().active) owned.StopAndVerify(_now, out _, out _);
            }
        }
        [Test] public void CompilationDuringFailureDiagnosticsCannotBeOverwrittenByFinish()
        {
            Start(Item("probe", "wait"));
            _executor.Dispose(); bool triggered = false;
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message =>
            {
                if (triggered) return; triggered = true; _executor.InterruptForCompilation();
            });
            _executor.RestoreStored();
            _executor.Finish("Aborted", "PROBE_FINISH", "diagnostic callback"); Pump(); AssertCompilationFailure();
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "PROBE_FINISH"), Is.True);
        }
        [Test] public void CompilationDuringGizmosCloseCannotAdvanceOrReplaceResourceReleaseStage()
        {
            var access = new GizmosAccess();
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            var item = Item("probe", "success"); item.gizmos = new AutomationStepGizmos { scene = true };
            Start(item, Item("probe", "following", "Finally"));
            access.OnDisable = () => _executor.InterruptForCompilation();
            Pump(); AssertCompilationFailure();
            Assert.That(Calls, Does.Not.Contain("execute:following")); Assert.That(_executor.Busy, Is.False);
        }
        [Test] public void CompilationDuringStartupDiagnosticsDoesNotResumeStartupOrCreateBusinessSteps()
        {
            bool triggered = false;
            var textLog = new AutomationStepExecutionLog(writeClock: () =>
            {
                if (!triggered) { triggered = true; _executor.InterruptForCompilation(); }
                return DateTimeOffset.Now;
            });
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, textLog: textLog,
                failureDiagnostic: message => TestContext.WriteLine(message));
            Start(Item("probe", "wait"), Item("probe", "following", "Finally")); Pump();
            AssertCompilationFailure(); Assert.That(Calls, Is.Empty);
            Assert.That(new AutomationStepRunStore(_store).Load().terminal, Is.True);
        }
        [Test] public void CompilationDuringReusedExecutorStartupDoesNotWriteIntoPreviousRunReport()
        {
            bool interruptStartup = false;
            var textLog = new AutomationStepExecutionLog(writeClock: () =>
            {
                if (interruptStartup) { interruptStartup = false; _executor.InterruptForCompilation(); }
                return DateTimeOffset.Now;
            });
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, textLog: textLog,
                failureDiagnostic: message => TestContext.WriteLine(message));
            Start(Item("probe", "wait")); Pump(1);
            _executor.StopImmediately(); Pump();
            var previous = _executor.State;
            string previousEvents = File.ReadAllText(Path.Combine(previous.reportDirectory, "events.jsonl"));
            Calls.Clear(); interruptStartup = true;
            Start(Item("probe", "wait"), Item("probe", "following", "Finally")); Pump();
            AssertCompilationFailure(); Assert.That(Calls, Is.Empty);
            var current = _executor.State;
            Assert.That(current.runId, Is.Not.EqualTo(previous.runId));
            Assert.That(current.reportDirectory, Is.Not.EqualTo(previous.reportDirectory));
            Assert.That(File.ReadAllText(Path.Combine(previous.reportDirectory, "events.jsonl")), Is.EqualTo(previousEvents));
            var summary = JsonUtility.FromJson<AutomationReportSummary>(File.ReadAllText(Path.Combine(current.reportDirectory, "summary.json")));
            Assert.That(summary.runId, Is.EqualTo(current.runId));
        }
        [Test] public void ExpiredReleaseDoesNotClaimUnattemptedGizmosClosureWasVerified()
        {
            var access = new GizmosAccess();
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            var item = Item("probe", "wait"); item.gizmos = new AutomationStepGizmos { scene = true };
            Start(item);
            ReenterAt = "execute"; Reenter = () => { _executor.InterruptForCompilation(); _now += 10000; };
            Pump(); AssertCompilationFailure();
            var summary = JsonUtility.FromJson<AutomationReportSummary>(File.ReadAllText(Path.Combine(_executor.State.reportDirectory, "summary.json")));
            Assert.That(_executor.State.gizmosState.finalCloseAttempted, Is.False);
            Assert.That(summary.phases.Single(p => p.id == "frameworkRelease").outcome, Is.EqualTo("Unverified"));
        }
    }
}
