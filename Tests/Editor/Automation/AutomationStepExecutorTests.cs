using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationStepExecutorTests
    {
        internal static readonly List<string> Calls = new();
        private static Action BeforeSave;
        private static Action<string> ObserveError;
        private static Action<string, string, string> RegisterAttachment;
        public sealed class FinallyFailureProbe : AutomationStepBase
        {
            internal static bool ConstructorFails;
            public FinallyFailureProbe() { if (ConstructorFails) throw new InvalidOperationException("constructor failure"); }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("final.execute:" + arguments);
                if (arguments == "execute_throw") throw new InvalidOperationException("execute failure");
            }
            public override string Poll(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("final.poll:" + arguments);
                if (arguments == "poll_throw") throw new InvalidOperationException("poll failure");
                if (arguments == "poll_invalid") return "not-json";
                if (arguments == "failed") return ResultJson("Failed", "FINAL_FAILURE");
                if (arguments == "skipped") return ResultJson("Skipped");
                if (arguments == "warning") return ResultJson("SucceededWithWarnings");
                if (arguments == "canceled" || arguments == "error_throw" || arguments == "error_invalid" || arguments == "cancel_throw")
                    return ResultJson("Canceled", "STEP_CANCELED");
                return ResultJson("Succeeded");
            }
            public override string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments)
            {
                Calls.Add("final.error:" + arguments);
                if (arguments == "error_throw") throw new InvalidOperationException("diagnostic failure");
                return arguments == "error_invalid" ? "not-json" : AutomationStepJsonCodec.ErrorJson(errorCode);
            }
            public override void Cancel(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("final.cancel:" + arguments);
                if (arguments == "cancel_throw") throw new InvalidOperationException("cancel failure");
            }
            public override string Cleanup(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("final.cleanup:" + arguments);
                if (arguments == "cleanup_throw") throw new InvalidOperationException("cleanup failure");
                if (arguments == "cleanup_invalid") return "not-json";
                return ResultJson(arguments == "cleanup_wait" ? "Running" : arguments == "cleanup_failed" ? "Failed" : "Succeeded", "FINAL_CLEANUP_FAILED");
            }
            public override string Restore(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("final.restore:" + arguments);
                if (arguments == "restore_throw") throw new InvalidOperationException("restore failure");
                if (arguments == "restore_invalid") return "not-json";
                return RestoreResultJson(arguments == "restore_unsupported" ? "Unsupported"
                    : arguments == "restore_failed" ? "Failed" : "Restored", arguments == "restore_failed" ? "FINAL_RESTORE_FAILED" : "");
            }
        }
        private sealed class FinallyGizmosAccess : IAutomationGizmosAccess
        {
            internal int calls;
            internal bool hasWindow, enabled;
            internal int writes;
            internal Action onRead;
            public AutomationGizmosWindow[] Find(bool scene, bool game)
            { calls++; return hasWindow ? new[] { new AutomationGizmosWindow { id = "deadline-view", view = "scene" } } : Array.Empty<AutomationGizmosWindow>(); }
            public bool Read(AutomationGizmosWindow window) { calls++; onRead?.Invoke(); return enabled; }
            public void Set(AutomationGizmosWindow window, bool value) { calls++; writes++; enabled = value; }
        }
        public sealed class ArtifactProbe : AutomationStepBase
        {
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("artifact.execute");
                RegisterAttachment(runId, instanceId, arguments);
                if (arguments == "fail") Fail("ORIGINAL_FAILURE", "Original business failure.");
                else Succeed();
            }
            public override string Restore(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("artifact.restore");
                RegisterAttachment(runId, instanceId, arguments);
                Succeed();
                return "{\"status\":\"Restored\"}";
            }
        }
        public class BaseProbe : AutomationStepBase
        {
            public override string Validate(string runId, string instanceId, string contextJson, string arguments)
            {
                if (arguments == "invalid") return ValidationError("TEST_ARGUMENT", "Invalid argument.");
                Assert.That(runId, Is.Empty);
                Assert.That(instanceId, Is.Not.Empty);
                Assert.That(AutomationStepJsonCodec.ParseObject(contextJson).Element("plan"), Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => SaveCheckpoint(runId, instanceId, "{}"));
                return ValidationOk();
            }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Assert.That(runId, Is.Not.Empty);
                Assert.That(AutomationStepJsonCodec.ParseObject(contextJson).Element("plan"), Is.Null);
                Calls.Add("execute:" + arguments); SaveCheckpoint(runId, instanceId, "{\"started\":true}");
                if (arguments == "throw") throw new InvalidOperationException("execute");
                if (arguments.StartsWith("fail")) Fail("TEST_FAILURE", "Readable failure.");
                else if (arguments == "skip") Skip();
                else if (arguments != "wait" && arguments != "cleanup_wait") Succeed(arguments == "warn");
                else if (arguments == "cleanup_wait") Succeed();
            }
            public override string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments)
            {
                Assert.Throws<InvalidOperationException>(() => SaveCheckpoint(runId, instanceId, "{}"));
                if (arguments == "fail_error") throw new InvalidOperationException("error callback");
                return base.GetError(runId, instanceId, contextJson, errorCode, arguments);
            }
            public override void Cancel(string runId, string instanceId, string contextJson, string arguments) { Calls.Add("cancel:" + arguments); }
            public override string Cleanup(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("cleanup:" + arguments);
                if (arguments == "fail_cleanup") throw new InvalidOperationException("cleanup");
                return ResultJson(arguments == "cleanup_wait" ? "Running" : "Succeeded");
            }
        }
        public sealed class DirectProbe : IAutomationStep
        {
            public string Validate(string runId, string instanceId, string contextJson, string arguments) => "{\"ok\":true}";
            public void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                UPilotAutomationStepService.SaveCheckpoint(runId, instanceId, "{\"started\":true}");
                Calls.Add("execute:" + arguments);
            }
            public string Poll(string runId, string instanceId, string contextJson, string arguments) =>
                arguments == "wait" ? "{\"status\":\"Running\"}" : "{\"status\":\"Succeeded\"}";
            public string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments) =>
                AutomationStepJsonCodec.ErrorJson(errorCode);
            public void Cancel(string runId, string instanceId, string contextJson, string arguments) { Calls.Add("cancel:" + arguments); }
            public string Cleanup(string runId, string instanceId, string contextJson, string arguments)
            { Calls.Add("cleanup:" + arguments); return "{\"status\":\"Succeeded\"}"; }
            public string Restore(string runId, string instanceId, string contextJson, string arguments) => "{\"status\":\"Unsupported\"}";
        }
        public sealed class RecoverCleanupProbe : BaseProbe
        {
            public static bool Released;
            public static bool ThrowOnCleanup;
            public override string Restore(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("recover-restore:" + instanceId);
                Assert.That(contextJson, Does.Contain("started"));
                return "{\"status\":\"Restored\"}";
            }
            public override string Cleanup(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("recover-cleanup:" + instanceId);
                SaveCheckpoint(runId, instanceId, "{\"started\":true,\"cleaning\":true}");
                if (ThrowOnCleanup) throw new InvalidOperationException("uncertain cleanup");
                return ResultJson(Released ? "Succeeded" : "Running");
            }
        }
        public sealed class RestoreProbe : BaseProbe
        {
            public override string Restore(string runId, string instanceId, string contextJson, string arguments)
            { Calls.Add("restore"); Succeed(); return "{\"status\":\"Restored\"}"; }
        }
        public sealed class JsonProbe : AutomationStepBase
        {
            private bool _validated;
            public override string Validate(string runId, string instanceId, string contextJson, string arguments)
            { _validated = true; return ValidationOk(); }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Assert.That(_validated, Is.False, "Execution must use a new instance.");
                if (arguments.StartsWith("save"))
                {
                    BeforeSave?.Invoke();
                    SaveCheckpoint(runId, instanceId, "{\"local\":true}");
                    if (arguments == "save-one")
                    {
                        SaveSharedValue(runId, instanceId, "project.logging", "{\"mask\":7}");
                        SaveSharedValue(runId, instanceId, "other.owner", "\"keep\"");
                    }
                    else SaveSharedValue(runId, instanceId, "project.logging", "{\"mask\":9}");
                    Calls.Add("saved:" + arguments);
                    Succeed();
                }
                else if (arguments == "read")
                {
                    var root = AutomationStepJsonCodec.ParseObject(contextJson);
                    Assert.That(root.Element("checkpoint").Elements(), Is.Empty);
                    Assert.That(root.Element("shared").Element("project.logging").Element("mask").Value, Is.EqualTo("9"));
                    Assert.That(root.Element("shared").Element("other.owner").Value, Is.EqualTo("keep"));
                    Succeed();
                }
                else if (arguments == "wrong-id")
                    SaveCheckpoint(runId, "another-item", "{}");
                else if (arguments == "worker")
                {
                    var error = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { SaveCheckpoint(runId, instanceId, "{}"); return ""; }
                        catch (InvalidOperationException ex) { return ex.Message; }
                    }).GetAwaiter().GetResult();
                    Assert.That(error, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
                    Succeed();
                }
            }
            public override string Poll(string runId, string instanceId, string contextJson, string arguments)
            {
                if (arguments.StartsWith("{") || arguments == "null") return arguments;
                return base.Poll(runId, instanceId, contextJson, arguments);
            }
            public override string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments)
            {
                ObserveError?.Invoke(errorCode);
                Assert.Throws<InvalidOperationException>(() => SaveSharedValue(runId, instanceId, "project.logging", "null"));
                return "{\"message\":\"readable\",\"code\":\"MUST_NOT_REPLACE\"}";
            }
        }
        private AutomationStepExecutor _executor;
        private string _store;
        private readonly List<string> _reports = new();
        private long _now;
        private AutomationStepRegistry _registry;
        [SetUp]
        public void SetUp()
        {
            RecoverCleanupProbe.Released = false; RecoverCleanupProbe.ThrowOnCleanup = false;
            FinallyFailureProbe.ConstructorFails = false;
            Calls.Clear(); BeforeSave = null; ObserveError = null; RegisterAttachment = null;
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _store = Path.Combine(Application.dataPath, "../Library/UPilot/StepTests/" + Guid.NewGuid().ToString("N") + ".json");
            _registry = Registry(Entry<BaseProbe>("base"), Entry<DirectProbe>("direct"), Entry<RestoreProbe>("restore"),
                Entry<JsonProbe>("json"), Entry<ArtifactProbe>("artifact"), Entry<RecoverCleanupProbe>("recover"), Entry<FinallyFailureProbe>("final-probe"));
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
        }
        [TearDown]
        public void TearDown()
        {
            if (_executor.State != null) new AutomationStepRunStore(_store).ClearFinallyFailureFence(_executor.State);
            _executor.Dispose();
            FinallyFailureProbe.ConstructorFails = false;
            if (File.Exists(_store)) File.Delete(_store);
            if (File.Exists(_store + ".capture")) File.Delete(_store + ".capture");
            if (Directory.Exists(_store + ".dispositions")) Directory.Delete(_store + ".dispositions", true);
            if (Directory.Exists(_store + ".tmp")) Directory.Delete(_store + ".tmp");
            foreach (var report in _reports) if (Directory.Exists(report)) Directory.Delete(report, true);
            _reports.Clear();
        }
        private void Start(params AutomationStepItem[] items)
        {
            var state = _executor.Start(Plan(items), "test-operation");
            _reports.Add(state.reportDirectory);
            Assert.That(state.reportDirectory, Is.EqualTo("Logs/UPilot/Reports/Steps/" + state.runId));
        }
        private void Pump(int ticks = 50) { for (int i = 0; i < ticks; i++) _executor.Tick(); }
        private AutomationStepRun LiveRun => (AutomationStepRun)typeof(AutomationStepExecutor).GetField("_run",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(_executor);

        [TestCase("execute_throw", "STEP_EXECUTE_EXCEPTION")]
        [TestCase("poll_throw", "STEP_POLL_EXCEPTION")]
        [TestCase("poll_invalid", "STEP_RESULT_INVALID")]
        [TestCase("failed", "FINAL_FAILURE")]
        [TestCase("skipped", "STEP_SKIP_NOT_ALLOWED")]
        [TestCase("cleanup_throw", "STEP_CLEANUP_EXCEPTION")]
        [TestCase("cleanup_invalid", "STEP_CLEANUP_RESULT_INVALID")]
        [TestCase("cleanup_failed", "FINAL_CLEANUP_FAILED")]
        [TestCase("error_throw", "STEP_ERROR_EXCEPTION")]
        [TestCase("error_invalid", "STEP_ERROR_INVALID")]
        [TestCase("cancel_throw", "STEP_CANCEL_EXCEPTION")]
        public void FinallyHardFailureFreezesCallbacksSkipsSuffixAndWritesOneFailedReport(string mode, string code)
        {
            Start(Item("final-probe", mode, "Finally"), Item("base", "never", "Finally")); Pump();
            var state = _executor.State;
            Assert.That(state.terminal, Is.True);
            Assert.That(state.status, Is.EqualTo("Failed"));
            Assert.That(state.steps[0].error.code, Is.EqualTo(code));
            Assert.That(state.steps[0].stage, Is.EqualTo("Completed"));
            Assert.That(state.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(state.steps[1].startedAtUtcMs, Is.Zero);
            Assert.That(state.steps[1].cleanupState, Is.EqualTo("SkippedByFinallyFailure"));
            Assert.That(state.cleanupOutcome, Is.EqualTo("NotRequired"));
            Assert.That(state.cleanupPending, Is.False);
            Assert.That(state.reportOutcome, Is.EqualTo("Written"));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls, Does.Not.Contain("execute:never"));
            if (mode == "failed" || mode == "poll_throw" || mode == "execute_throw" || mode == "poll_invalid" || mode == "skipped")
            {
                Assert.That(Calls, Does.Not.Contain("final.error:" + mode));
                Assert.That(Calls, Does.Not.Contain("final.cancel:" + mode));
                Assert.That(Calls, Does.Not.Contain("final.cleanup:" + mode));
            }
            string summaryPath = Path.Combine(state.reportDirectory, "summary.json");
            string beforeReport = File.ReadAllText(summaryPath);
            var summary = JsonUtility.FromJson<AutomationReportSummary>(beforeReport);
            Assert.That(summary.outcome, Is.EqualTo("Failed"));
            Assert.That(summary.cases[1].outcome, Is.EqualTo("Skipped"));
            Assert.That(summary.cases[1].startedAtUtcMs, Is.Zero);
            Assert.That(summary.cases[1].detail, Does.Contain(state.steps[0].instanceId));
            Assert.That(summary.detail, Does.Contain("secondaryErrors"));
            if (mode.EndsWith("throw")) Assert.That(summary.detail, Does.Contain("Exception"));
            int count = Calls.Count;
            _executor.RequestCancel(); _now += 60001; Pump();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count, Is.EqualTo(count));
            Assert.That(File.ReadAllText(summaryPath), Is.EqualTo(beforeReport));
            Assert.That(_executor.State.finishedAtUtcMs, Is.EqualTo(state.finishedAtUtcMs));
        }

        [Test]
        public void FinallyConstructorFailureRecordsUnexecutedBusinessCleanupAndSkippedSuffix()
        {
            Start(Item("final-probe", "", "Finally"), Item("base", "never", "Finally"));
            FinallyFailureProbe.ConstructorFails = true; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_CONSTRUCTION_FAILED"));
            Assert.That(_executor.State.steps[0].startedAtUtcMs, Is.Zero);
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("SkippedByFinallyFailure"));
            Assert.That(_executor.State.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
            Assert.That(Calls, Is.Empty);
        }

        [TestCase("canceled", "Canceled")]
        [TestCase("warning", "SucceededWithWarnings")]
        [TestCase("skipped", "SucceededWithWarnings")]
        public void FinallyCancellationWarningsAndAllowedSkipKeepTheirOrdinaryContract(string mode, string outcome)
        {
            var first = Item("final-probe", mode, "Finally"); first.allowSkipped = true;
            Start(first, Item("base", "after", "Finally")); Pump();
            Assert.That(_executor.State.status, Is.EqualTo(outcome));
            Assert.That(_executor.State.finallyFailureAtUtcMs, Is.Zero);
            Assert.That(Calls, Does.Contain("final.cleanup:" + mode));
            Assert.That(Calls, Does.Contain("execute:after"));
        }

        [Test]
        public void FinallyFailureKeepsAnyEarlierTimeoutAsRunOutcome()
        {
            var normal = Item("base", "wait"); normal.timeoutSeconds = 1;
            Start(normal, Item("final-probe", "failed", "Finally"), Item("base", "never", "Finally"));
            _executor.Tick(); _now += 1001; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_TIMEOUT"));
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "FINAL_FAILURE"), Is.True);
            Assert.That(_executor.State.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Failed));
            Assert.That(_executor.State.steps[2].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
        }

        [Test]
        public void FinallyCleanupExpiredBudgetDispatchesNoResourceMutation()
        {
            var access = new FinallyGizmosAccess();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            var first = Item("final-probe", "cleanup_wait", "Finally"); first.cleanupTimeoutSeconds = 1;
            Start(first, Item("base", "never", "Finally")); Pump(3);
            var live = LiveRun;
            live.captureOwned = true; live.captureStartIntent = true; live.captureSessionId = "expired-capture";
            live.snapshots.Add(new AutomationSnapshotRecord { instanceId = first.instanceId, evidenceKey = "expired",
                snapshotId = "expired-snapshot", status = "Running" });
            long cleanupStart = live.steps[0].cleanupStartedAtUtcMs;
            int calls = access.calls;
            _now = cleanupStart + 1000; _executor.Tick();
            var state = _executor.State;
            Assert.That(access.calls, Is.EqualTo(calls), "No Gizmos enumeration/read/set may run after the original budget.");
            Assert.That(state.snapshots[0].cancelIntent, Is.False);
            Assert.That(File.Exists(_store + ".capture"), Is.False);
            Assert.That(state.finallyCleanupDeadlineUtcMs, Is.EqualTo(cleanupStart + 1000));
            Assert.That(state.status, Is.EqualTo("TimedOut"));
            Assert.That(state.cleanupOutcome, Is.EqualTo("Unverified"));
            Assert.That(state.unresolvedResources, Does.Contain("capture:expired-capture"));
            Assert.That(state.unresolvedResources, Does.Contain("snapshot:expired-snapshot"));
            Assert.That(state.unresolvedResources, Does.Contain("gizmos:final-close"));
            Assert.That(state.reportOutcome, Is.EqualTo("Written"));
            Assert.That(state.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
        }

        [Test]
        public void FinallyFailureReloadKeepsOriginalDeadlineAndOnlyObservesOwnedResources()
        {
            var first = Item("final-probe", "failed", "Finally"); first.cleanupTimeoutSeconds = 2;
            Start(first, Item("base", "never", "Finally")); _executor.Tick();
            LiveRun.captureOwned = true; LiveRun.captureStartIntent = true; LiveRun.captureSessionId = "original-capture";
            _executor.Tick();
            var pending = _executor.State;
            Assert.That(pending.terminal, Is.True);
            Assert.That(pending.cleanupPending, Is.True);
            Assert.That(pending.cleanupOutcome, Is.EqualTo("Pending"));
            Assert.That(pending.reportOutcome, Is.EqualTo("Pending"));
            Assert.That(_executor.Busy, Is.True);
            int calls = Calls.Count;
            _now += 500;
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); _executor.Tick();
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.State.finallyCleanupDeadlineUtcMs, Is.EqualTo(pending.finallyCleanupDeadlineUtcMs));
            _now = pending.finallyCleanupDeadlineUtcMs; Pump();
            Assert.That(_executor.State.cleanupOutcome, Is.EqualTo("Unverified"));
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
            Assert.That(_executor.State.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count, Is.EqualTo(calls));
        }

        [Test]
        public void FinallyFailureReportWriteFailureEndsAttemptWithoutRetryOrChangingBusinessError()
        {
            Start(Item("final-probe", "failed", "Finally"), Item("base", "never", "Finally"));
            Directory.CreateDirectory(Path.Combine(_executor.State.reportDirectory, "summary.json"));
            Pump();
            var state = _executor.State;
            Assert.That(state.status, Is.EqualTo("Failed"));
            Assert.That(state.error.code, Is.EqualTo("FINAL_FAILURE"));
            Assert.That(state.reportOutcome, Is.EqualTo("Failed"));
            Assert.That(state.secondaryErrors.Any(e => e.code == "STEP_FINALLY_FAILURE_REPORT_FAILED"), Is.True);
            Assert.That(state.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
            string frozen = JsonUtility.ToJson(state);
            Pump();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(JsonUtility.ToJson(_executor.State), Is.EqualTo(frozen));
        }

        [TestCase("restore_throw", "STEP_RESTORE_EXCEPTION")]
        [TestCase("restore_invalid", "STEP_RESTORE_INVALID")]
        [TestCase("restore_unsupported", "STEP_RESTORE_UNSUPPORTED")]
        [TestCase("restore_failed", "FINAL_RESTORE_FAILED")]
        public void FinallyUnrecoverableReloadFreezesWithoutFollowingBusinessCallbacks(string mode, string code)
        {
            Start(Item("final-probe", mode, "Finally"), Item("base", "never", "Finally")); _executor.Tick();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo(code));
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
            Assert.That(_executor.State.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(Calls, Is.EqualTo(new[] { "final.execute:" + mode, "final.restore:" + mode }));
        }

        [Test]
        public void FinallyStoreFailureFencePreventsReloadFromRestoringOldNonterminalDiskRecord()
        {
            Start(Item("final-probe", "failed", "Finally"), Item("base", "never", "Finally")); _executor.Tick();
            Directory.CreateDirectory(_store + ".tmp");
            _executor.Tick();
            var store = new AutomationStepRunStore(_store);
            var oldDisk = store.Load();
            Assert.That(oldDisk.terminal, Is.False);
            var fence = store.FinallyFailureFence(oldDisk);
            Assert.That(fence.terminal, Is.True);
            Assert.That(fence.cleanupPending, Is.False);
            Assert.That(fence.reportOutcome, Is.EqualTo("Written"));
            int calls = Calls.Count;
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.error.code, Is.EqualTo("FINAL_FAILURE"));
            Assert.That(_executor.State.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
        }

        [Test]
        public void FinallyReloadBetweenResourceDecisionAndReportOnlyCompletesReport()
        {
            var first = Item("final-probe", "failed", "Finally"); first.cleanupTimeoutSeconds = 5;
            Start(first, Item("base", "never", "Finally")); _executor.Tick();
            LiveRun.captureOwned = true; LiveRun.captureStartIntent = true; LiveRun.captureSessionId = "pending-capture";
            _executor.Tick();
            var intermediate = _executor.State;
            intermediate.cleanupOutcome = "Unverified";
            var store = new AutomationStepRunStore(_store);
            store.ClearFinallyFailureFence(intermediate); store.Save(intermediate);
            int calls = Calls.Count;
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); _executor.Tick();
            Assert.That(_now, Is.LessThan(intermediate.finallyCleanupDeadlineUtcMs));
            Assert.That(_executor.State.cleanupPending, Is.False);
            Assert.That(_executor.State.cleanupOutcome, Is.EqualTo("Unverified"));
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count, Is.EqualTo(calls));
        }

        [Test]
        public void FinallyFailedSnapshotFreezesWhileOtherSnapshotIsRunningAndPreservesVerifiedBusinessCleanup()
        {
            var first = Item("final-probe", "", "Finally"); first.cleanupTimeoutSeconds = 1;
            Start(first, Item("base", "never", "Finally")); Pump(2);
            var live = LiveRun;
            live.snapshots.Add(new AutomationSnapshotRecord
            {
                instanceId = first.instanceId, evidenceKey = "failed", requestedAt = _now,
                requestKey = AutomationSnapshotEvidence.RequestKey(live.runId, first.instanceId, "failed"),
                snapshotId = "failed-snapshot", status = "Failed", errorCode = "SNAPSHOT_FAILURE", message = "Snapshot failed.",
            });
            live.snapshots.Add(new AutomationSnapshotRecord
            {
                instanceId = first.instanceId, evidenceKey = "running", requestedAt = _now,
                requestKey = AutomationSnapshotEvidence.RequestKey(live.runId, first.instanceId, "running"),
                snapshotId = "running-snapshot", status = "Running",
            });
            _executor.Tick();
            var state = _executor.State;
            Assert.That(state.terminal, Is.True);
            Assert.That(state.status, Is.EqualTo("Failed"));
            Assert.That(state.error.code, Is.EqualTo("SNAPSHOT_FAILURE"));
            Assert.That(state.steps[0].cleanupState, Is.EqualTo("Verified"));
            Assert.That(state.snapshots[1].status, Is.EqualTo("Running"));
            Assert.That(state.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
            Assert.That(Calls, Does.Not.Contain("execute:never"));
            _now = state.finallyCleanupDeadlineUtcMs; Pump();
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
            Assert.That(_executor.State.cleanupOutcome, Is.EqualTo("Unverified"));
        }

        [Test]
        public void FinallyGizmosReadCrossingDeadlineDoesNotDispatchSetter()
        {
            var access = new FinallyGizmosAccess { hasWindow = true };
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            var first = Item("final-probe", "failed", "Finally"); first.cleanupTimeoutSeconds = 1;
            Start(first); _executor.Tick();
            access.enabled = true;
            long expectedDeadline = _now + 1000;
            access.onRead = () => _now = expectedDeadline;
            int writes = access.writes;
            _executor.Tick();
            Assert.That(access.writes, Is.EqualTo(writes));
            Assert.That(_executor.State.finallyCleanupDeadlineUtcMs, Is.EqualTo(expectedDeadline));
            Assert.That(_executor.State.cleanupOutcome, Is.EqualTo("Unverified"));
            Assert.That(_executor.State.gizmosState.finalCloseVerified, Is.False);
            Assert.That(_executor.State.reportOutcome, Is.EqualTo("Written"));
        }

        [Test]
        public void FinallyResourceIntentSaveUpdatesOlderFenceAndReloadNeverReplaysSnapshotCancelOrGizmosSet()
        {
            var access = new FinallyGizmosAccess { hasWindow = true };
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            var first = Item("final-probe", "failed", "Finally");
            Start(first); _executor.Tick();
            LiveRun.captureOwned = true; LiveRun.captureStartIntent = true; LiveRun.captureSessionId = "fence-capture";
            _executor.Tick();
            var state = _executor.State;
            string key = AutomationSnapshotEvidence.RequestKey(state.runId, first.instanceId, "fence-snapshot");
            state.snapshots.Add(new AutomationSnapshotRecord { instanceId = first.instanceId, evidenceKey = "fence-snapshot",
                requestKey = key, snapshotId = "fence-snapshot", requestedAt = _now, status = "Running", unresolved = true });
            var oldFence = AutomationStepJson.Copy(state);
            oldFence.gizmosState.finalCloseAttempted = false; oldFence.gizmosState.finalCloseVerified = false;
            var store = new AutomationStepRunStore(_store);
            store.FenceFinallyFailure(oldFence);
            state.snapshots[0].cancelIntent = true; state.snapshots[0].cancelRequestedAt = _now;
            store.Save(state); // Simulate the native-intent save immediately before its one dispatch.
            Assert.That(store.FinallyFailureFence(state).snapshots[0].cancelIntent, Is.True);
            Assert.That(store.FinallyFailureFence(state).gizmosState.finalCloseAttempted, Is.True);
            access.enabled = true; int writes = access.writes;
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: access,
                failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored();
            Assert.That(_executor.State.snapshots[0].cancelIntent, Is.True);
            var observed = new SnapshotJobPayload { snapshotId = "fence-snapshot", requestKey = key,
                outputDirectory = Path.Combine(state.reportDirectory, "snapshots", key.Substring("step:".Length)) };
            int cancels = 0;
            var snapshots = new AutomationSnapshotEvidence(LiveRun, () => store.Save(LiveRun), () => _now);
            snapshots.StopAndObserveRelease(LiveRun.snapshots[0], _ => new SnapshotApiResultV1 { ok = true, job = observed },
                _ => { cancels++; return new(); });
            _executor.Tick();
            Assert.That(cancels, Is.Zero);
            Assert.That(access.writes, Is.EqualTo(writes));
            _now = state.finallyCleanupDeadlineUtcMs; Pump();
            Assert.That(_executor.State.cleanupPending, Is.False);
        }

        [Test]
        public void FinallyResourceCleanupTimeoutPreservesVerifiedBusinessCleanup()
        {
            var first = Item("final-probe", "", "Finally"); first.cleanupTimeoutSeconds = 1;
            Start(first, Item("base", "never", "Finally")); Pump(2);
            var live = LiveRun;
            live.steps[0].stage = "ResourceCleaning"; live.steps[0].cleanupState = "Verified";
            live.snapshots.Add(new AutomationSnapshotRecord { instanceId = first.instanceId,
                snapshotId = "expired-resource", status = "Running" });
            long cleanupStart = live.steps[0].cleanupStartedAtUtcMs;
            _now = cleanupStart + 1000; _executor.Tick();
            var state = _executor.State;
            Assert.That(state.status, Is.EqualTo("TimedOut"));
            Assert.That(state.steps[0].cleanupState, Is.EqualTo("Verified"));
            Assert.That(state.finallyCleanupDeadlineUtcMs, Is.EqualTo(cleanupStart + 1000));
            Assert.That(state.snapshots[0].cancelIntent, Is.False);
            Assert.That(state.cleanupOutcome, Is.EqualTo("Unverified"));
            Assert.That(state.reportOutcome, Is.EqualTo("Written"));
        }
        [Test]
        public void LifecycleFinallyExecuteTimeoutEndsOnceWithoutFollowingFinally()
        {
            var first = Item("base", "wait", "Finally"); first.timeoutSeconds = 1;
            Start(first, Item("base", "after", "Finally"));
            _executor.Tick(); _now += 1001; Pump();
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait" }));
            var finished = _executor.State.finishedAtUtcMs;
            _executor.RequestCancel(); _executor.Finish("Aborted", "LATE", "late"); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.State.finishedAtUtcMs, Is.EqualTo(finished));
        }

        [Test]
        public void LifecycleFinallyCleanupTimeoutSkipsRestAndDoesNotRenew()
        {
            var first = Item("base", "cleanup_wait", "Finally"); first.cleanupTimeoutSeconds = 1;
            Start(first, Item("base", "after", "Finally"));
            Pump(3); var deadlineStart = _executor.State.steps[0].cleanupStartedAtUtcMs;
            _executor.RequestCancel(); _executor.RequestCancel();
            _now = deadlineStart + 1001; Pump();
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(Calls.Count(c => c == "execute:cleanup_wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Not.Contain("execute:after"));
            Assert.That(_executor.State.steps[0].cleanupStartedAtUtcMs, Is.EqualTo(deadlineStart));
        }

        [Test]
        public void LifecycleNormalCleanupTimeoutRunsFinallyOnceAndReleasesSlot()
        {
            var normal = Item("base", "cleanup_wait"); normal.cleanupTimeoutSeconds = 1;
            Start(normal, Item("base", "after", "Finally"));
            Pump(3); _now += 1001; Pump();
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count(c => c == "execute:after"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ConsolePolicyPublishesNormalAndFinallyEvidence() => VerifyConsolePolicyEvidence(false);

        [UnityTest]
        public IEnumerator ConsolePolicyDoesNotReplaceEarlierFailure() => VerifyConsolePolicyEvidence(true);

        private IEnumerator VerifyConsolePolicyEvidence(bool firstFailure)
        {
            var owned = AutomationEvidenceSession.StartOwned(new ConsoleCaptureStartPayload
            { title = "step-policy-evidence", ownerId = "step-policy-tests", ownerToken = Guid.NewGuid().ToString("N"),
                requestKey = Guid.NewGuid().ToString("N"), flushIntervalMs = 50 });
            try
            {
                RegisterAttachment = (run, item, args) => Debug.LogWarning("Step policy diagnostic " + item);
                var normal = Item("artifact", firstFailure ? "fail" : "");
                normal.instanceId = "normal";
                var final = Item("artifact", "", "Finally");
                final.instanceId = "exit";
                var plan = Plan(normal, final);
                plan.logPolicy = new[] { new AutomationLogRule
                    { id = "deny-probe", action = "deny", messageContains = "Step policy diagnostic" } };
                var initial = _executor.Start(plan, "policy-operation", owned.SessionId);
                _reports.Add(initial.reportDirectory);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!_executor.State.terminal && DateTime.UtcNow < deadline)
                { _executor.Tick(); yield return null; }
                var state = _executor.State;
                Assert.That(state.terminal, Is.True, state.error?.message);
                Assert.That(state.status, Is.EqualTo("Failed"));
                Assert.That(state.error.code, Is.EqualTo(firstFailure ? "ORIGINAL_FAILURE" : "STEP_CONSOLE_POLICY_FAILED"));
                if (firstFailure) Assert.That(state.secondaryErrors.Any(e => e.code == "STEP_CONSOLE_POLICY_FAILED"), Is.True);
                Assert.That(state.logSummary.blockedCount, Is.EqualTo(2));
                Assert.That(state.logSummary.blockingSamples.Select(s => s.instanceId), Is.EqualTo(new[] { "normal", "exit" }));
                Assert.That(state.logSummary.blockingSamples.Select(s => s.phaseId), Is.EqualTo(new[] { "Normal", "Finally" }));
                Assert.That(state.logSummary.blockingSamples.All(s => s.ruleId == "deny-probe"), Is.True);
                Assert.That(state.logSummary.sessionId, Is.EqualTo(owned.SessionId));
                Assert.That(state.logSummary.evidenceComplete, Is.True);
                var artifact = UPilotAutomationStepService.Public(state).artifacts.attachments.Single(a => a.artifactKind == "consolePolicy");
                Assert.That(artifact.bytes, Is.GreaterThan(0));
                Assert.That(artifact.sha256.Length, Is.EqualTo(64));
                var summary = JsonUtility.FromJson<AutomationReportSummary>(File.ReadAllText(Path.Combine(state.reportDirectory, "summary.json")));
                Assert.That(summary.logSummary.blockedCount, Is.EqualTo(2));
                Assert.That(summary.artifacts.Single(a => a.kind == "consolePolicy").sha256, Is.EqualTo(artifact.sha256));
                Assert.That(UPilotConsoleCaptureApi.Status(owned.SessionId).session.active, Is.True);
                _executor.Dispose();
                _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
                _executor.RestoreStored();
                Pump();
                Assert.That(_executor.State.logSummary.blockedCount, Is.EqualTo(2));
                Assert.That(Calls.Count(c => c == "artifact.execute"), Is.EqualTo(2));
                Assert.That(UPilotAutomationStepService.Public(_executor.State).artifacts.attachments
                    .Single(a => a.artifactKind == "consolePolicy").sha256, Is.EqualTo(artifact.sha256));
            }
            finally { Assert.That(owned.StopOwned().ok, Is.True); }
        }

        [Test]
        public void TextArtifactIsUtf8WithoutBomDurableAndIdempotent()
        {
            string path = "";
            const string content = "{\"value\":\"中文\"}\n";
            RegisterAttachment = (run, item, args) =>
            {
                path = UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", content);
                var saved = new AutomationStepRunStore(_store).Load().registeredArtifacts.Single();
                Assert.That(saved.path, Is.EqualTo(path));
                var modified = File.GetLastWriteTimeUtc(path);
                Assert.That(UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", content), Is.EqualTo(path));
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
            };
            var item = Item("artifact"); item.instanceId = "../危险\\实例:1";
            Start(item); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new System.Text.UTF8Encoding(false).GetBytes(content)));
            Assert.That(Path.GetFileName(Path.GetDirectoryName(path)), Is.EqualTo(AutomationStepRunStore.HashText(item.instanceId)));
            Assert.That(_executor.State.registeredArtifacts.Count, Is.EqualTo(1));
            Assert.That(UPilotAutomationStepService.Public(_executor.State).artifacts.attachments.Single(a => a.artifactKind == "evidence").bytes,
                Is.EqualTo(File.ReadAllBytes(path).LongLength));
        }
        [TestCase("../escape.json")] [TestCase("folder/file.json")] [TestCase("folder\\file.json")]
        [TestCase("C:\\escape.json")] [TestCase("evidence.json:stream")] [TestCase(".")] [TestCase("..")]
        [TestCase("")] [TestCase("CON")] [TestCase("nul.json")] [TestCase("COM1.txt")] [TestCase("LPT9")]
        [TestCase("bad.")] [TestCase("bad ")] [TestCase("bad\n.json")] [TestCase("AUX .txt")]
        public void TextArtifactRejectsUnsafeNamesBeforeAnyFileEffects(string fileName)
        {
            RegisterAttachment = (run, item, args) =>
            {
                try { UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", fileName, "{}"); }
                catch (AutomationStepException) { }
            };
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
            Assert.That(Directory.Exists(Path.Combine(_executor.State.reportDirectory, "attachments")), Is.False);
        }
        [TestCase("owner")] [TestCase("kind")] [TestCase("content")] [TestCase("unregistered")] [TestCase("directory")]
        public void TextArtifactConflictsPreserveOriginalBytesAndCannotBeCaughtAsSuccess(string mode)
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
            {
                path = Path.GetFullPath(Path.Combine(_executor.State.reportDirectory, "attachments",
                    AutomationStepRunStore.HashText(item), "evidence.json"));
                if (mode == "unregistered" || mode == "directory")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (mode == "directory") Directory.CreateDirectory(path); else File.WriteAllText(path, "original");
                }
                else
                {
                    UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "original");
                    if (mode == "owner")
                    {
                        var live = (AutomationStepRun)typeof(AutomationStepExecutor).GetField("_run",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(_executor);
                        live.registeredArtifacts[0].instanceId = "another-owner";
                    }
                }
                try { UPilotAutomationStepService.WriteTextArtifact(run, item, mode == "kind" ? "other" : "evidence",
                    "evidence.json", mode == "content" ? "changed" : "original"); }
                catch (AutomationStepException) { }
            };
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
            if (mode == "directory") Assert.That(Directory.Exists(path), Is.True);
            else Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
        }
        [Test]
        public void TextArtifactsFromDifferentInstancesHaveIndependentNamesAndFinallyCanWrite()
        {
            var paths = new List<string>();
            RegisterAttachment = (run, item, args) => paths.Add(
                UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "same.json", args));
            Start(Item("artifact", "first"), Item("artifact", "last", "Finally")); Pump();
            Assert.That(paths.Distinct().Count(), Is.EqualTo(2));
            Assert.That(paths.Select(File.ReadAllText), Is.EqualTo(new[] { "first", "last" }));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }
        [TestCase("wrong-run")] [TestCase("wrong-item")] [TestCase("empty-kind")] [TestCase("null-content")]
        public void TextArtifactRejectsBadIdentityAndPayloadBeforeCreatingDirectories(string mode)
        {
            RegisterAttachment = (run, item, args) => UPilotAutomationStepService.WriteTextArtifact(
                mode == "wrong-run" ? "wrong" : run, mode == "wrong-item" ? "wrong" : item,
                mode == "empty-kind" ? "" : "evidence", "evidence.json", mode == "null-content" ? null : "{}");
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
            Assert.That(Directory.Exists(Path.Combine(_executor.State.reportDirectory, "attachments")), Is.False);
        }
        [TestCase(false)] [TestCase(true)]
        public void ReusingRegisteredTextDetectsChangedOrMissingEvidence(bool missing)
        {
            RegisterAttachment = (run, item, args) =>
            {
                string path = UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "original");
                if (missing) File.Delete(path); else File.WriteAllText(path, "tampered");
                try { UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "original"); }
                catch (AutomationStepException) { }
                Assert.That(File.Exists(path), Is.EqualTo(!missing));
                if (!missing) Assert.That(File.ReadAllText(path), Is.EqualTo("tampered"));
            };
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
        }
        [Test] public void InvalidUnicodeTextIsRejectedBeforeDirectoriesAreCreated()
        {
            RegisterAttachment = (run, item, args) => UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "\ud800");
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
            Assert.That(Directory.Exists(Path.Combine(_executor.State.reportDirectory, "attachments")), Is.False);
        }
        [Test]
        public void TextArtifactRequiresWritableMainThreadCallback()
        {
            Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.WriteTextArtifact("run", "item", "evidence", "file.json", "{}"));
            using (UPilotAutomationStepService.EnterCallback(_executor, false))
                Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.WriteTextArtifact("run", "item", "evidence", "file.json", "{}"));
            string error = System.Threading.Tasks.Task.Run(() =>
            {
                try { UPilotAutomationStepService.WriteTextArtifact("run", "item", "evidence", "file.json", "{}"); return ""; }
                catch (InvalidOperationException ex) { return ex.Message; }
            }).GetAwaiter().GetResult();
            Assert.That(error, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
            Assert.That(File.Exists(_store), Is.False);
        }
        [Test] public void TextArtifactWriteFailureLeavesNoRegistrationOrTemporaryFile()
        {
            string blocker = "";
            RegisterAttachment = (run, item, args) =>
            {
                blocker = Path.Combine(_executor.State.reportDirectory, "attachments");
                File.WriteAllText(blocker, "parent is a file");
                UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "{}");
            };
            Start(Item("artifact")); Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
            Assert.That(_executor.State.registeredArtifacts, Is.Empty);
            Assert.That(File.ReadAllText(blocker), Is.EqualTo("parent is a file"));
        }
        [Test] public void TextArtifactRejectsAnActualDirectoryJunctionBeforeWriting()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows junction regression.");
            Start(Item("artifact"));
            string root = Path.GetFullPath(_executor.State.reportDirectory);
            string target = Path.Combine(root, "junction-target"), link = Path.Combine(root, "attachments");
            Directory.CreateDirectory(target);
            string command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"
                + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    if (!process.WaitForExit(10000)) { process.Kill(); Assert.Fail("Junction creation timed out."); }
                    Assert.That(process.ExitCode, Is.Zero);
                }
                RegisterAttachment = (run, item, args) => UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "{}");
                Pump();
                Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_WRITE_FAILED"));
                Assert.That(Directory.GetFileSystemEntries(target), Is.Empty);
                Assert.That(_executor.State.registeredArtifacts, Is.Empty);
            }
            finally
            {
                Assert.That(Path.GetDirectoryName(link), Is.EqualTo(root));
                if (Directory.Exists(link)) Directory.Delete(link, false);
            }
        }
        [Test]
        public void PublishedTextWithFailedRegistrationSaveNeverReturnsSuccess()
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
            {
                path = Path.Combine(_executor.State.reportDirectory, "attachments", AutomationStepRunStore.HashText(item), "evidence.json");
                Directory.CreateDirectory(_store + ".tmp");
                UPilotAutomationStepService.WriteTextArtifact(run, item, "evidence", "evidence.json", "orphan evidence");
                Calls.Add("after-text-write");
            };
            Start(Item("artifact")); Pump();
            Assert.That(Calls, Does.Not.Contain("after-text-write"));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(File.ReadAllText(path), Is.EqualTo("orphan evidence"));
            Assert.That(new AutomationStepRunStore(_store).Load().registeredArtifacts, Is.Empty);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp"), Is.Empty);
        }

        [Test]
        public void ArtifactRegistrationPersistsAcrossRestoreAndPublicSerialization()
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
                UPilotAutomationStepService.RegisterArtifact(run, item, "evidence", path);
            Start(Item("artifact"));
            path = Path.Combine(_executor.State.reportDirectory, "evidence.json");
            File.WriteAllText(path, "{\"value\":1}");
            _executor.Tick();
            var saved = new AutomationStepRunStore(_store).Load().registeredArtifacts.Single();
            Assert.That(saved.sha256.Length, Is.EqualTo(64));
            Assert.That(UPilotAutomationStepService.Public(_executor.State).artifacts.attachments.Single().sha256,
                Is.EqualTo(saved.sha256));
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored();
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.registeredArtifacts.Count, Is.EqualTo(1));
            Assert.That(Calls.Count(x => x == "artifact.execute"), Is.EqualTo(1));
            Assert.That(Calls.Count(x => x == "artifact.restore"), Is.EqualTo(1));
            var json = JsonUtility.ToJson(UPilotAutomationStepService.Public(_executor.State));
            var result = JsonUtility.FromJson<UPilotAutomationStepService.StateResult>(json);
            var attachment = result.artifacts.attachments.Single(a => a.artifactKind == "evidence");
            Assert.That(attachment.kind, Is.EqualTo("file"));
            Assert.That(attachment.instanceId, Is.EqualTo(saved.instanceId));
            Assert.That(attachment.sha256, Is.EqualTo(saved.sha256));
            Assert.That(result.artifacts.attachments.Select(a => a.artifactKind),
                Is.EquivalentTo(new[] { "report", "timing", "evidence" }));
            Assert.That(result.artifacts.summary.path, Does.EndWith("summary.json"));
            string summaryPath = Path.Combine(_executor.State.reportDirectory, "summary.json");
            string summary = File.ReadAllText(summaryPath);
            Assert.That(JsonUtility.FromJson<AutomationReportSummary>(summary).artifacts.Single().sha256, Is.EqualTo(saved.sha256));
            Assert.Throws<AutomationStepException>(() => _executor.RegisterArtifact(result.runId, saved.instanceId, "evidence", path));
            Pump();
            Assert.That(File.ReadAllText(summaryPath), Is.EqualTo(summary));
        }

        [TestCase("wrong-run")] [TestCase("wrong-item")] [TestCase("missing")] [TestCase("directory")]
        [TestCase("outside")] [TestCase("empty-kind")] [TestCase("reserved")] [TestCase("changed")]
        public void ArtifactRegistrationRejectsInvalidInputsEvenWhenStepCatchesFailure(string mode)
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
            {
                try
                {
                    if (mode == "changed")
                    {
                        UPilotAutomationStepService.RegisterArtifact(run, item, "evidence", path);
                        File.WriteAllText(path, "changed");
                    }
                    UPilotAutomationStepService.RegisterArtifact(mode == "wrong-run" ? "wrong" : run,
                        mode == "wrong-item" ? "wrong" : item, mode == "empty-kind" ? "" : "evidence", path);
                }
                catch (AutomationStepException) { }
            };
            Start(Item("artifact"), Item("base", "finally", "Finally"));
            string directory = _executor.State.reportDirectory;
            path = mode == "outside" ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../outside"))
                : mode == "directory" ? directory
                : Path.Combine(directory, mode == "reserved" ? "events.jsonl" : "evidence.json");
            if (mode != "outside" && mode != "directory" && mode != "reserved" && mode != "missing")
                File.WriteAllText(path, "original");
            Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_ARTIFACT_REGISTER_FAILED"));
            Assert.That(Calls, Does.Contain("execute:finally"));
            Assert.That(_executor.State.terminal, Is.True);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void FinalArtifactVerificationDetectsChangedOrMissingFilesWithoutReplacingFirstError(bool firstError, bool missing)
        {
            string path = "";
            RegisterAttachment = (run, item, args) => UPilotAutomationStepService.RegisterArtifact(run, item, "evidence", path);
            Start(Item("artifact", firstError ? "fail" : ""));
            path = Path.Combine(_executor.State.reportDirectory, "evidence.json");
            File.WriteAllText(path, "original");
            _executor.Tick();
            if (missing) File.Delete(path); else File.WriteAllText(path, "modified");
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo(firstError ? "ORIGINAL_FAILURE" : "STEP_ARTIFACT_VERIFICATION_FAILED"));
            var summary = JsonUtility.FromJson<AutomationReportSummary>(
                File.ReadAllText(Path.Combine(_executor.State.reportDirectory, "summary.json")));
            Assert.That(summary.artifacts.Single().diagnostic, Is.Not.Empty);
            Assert.That(_executor.State.artifacts.Select(a => a.kind),
                Is.EquivalentTo(new[] { "events", "summary", "report", "timing", "evidence" }));
        }

        [Test]
        public void ArtifactRegistrationRequiresWritableMainThreadCallback()
        {
            Assert.Throws<InvalidOperationException>(() =>
                UPilotAutomationStepService.RegisterArtifact("run", "item", "evidence", "missing"));
            using (UPilotAutomationStepService.EnterCallback(_executor, false))
                Assert.Throws<InvalidOperationException>(() =>
                    UPilotAutomationStepService.RegisterArtifact("run", "item", "evidence", "missing"));
            string workerError = System.Threading.Tasks.Task.Run(() =>
            {
                try { UPilotAutomationStepService.RegisterArtifact("run", "item", "evidence", "missing"); return ""; }
                catch (InvalidOperationException ex) { return ex.Message; }
            }).GetAwaiter().GetResult();
            Assert.That(workerError, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
            Assert.That(File.Exists(_store), Is.False);
        }

        [Test]
        public void FailedArtifactPersistencePreventsFollowingEffects()
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
            {
                Directory.CreateDirectory(_store + ".tmp");
                UPilotAutomationStepService.RegisterArtifact(run, item, "evidence", path);
                Calls.Add("after-artifact-save");
            };
            Start(Item("artifact"));
            path = Path.Combine(_executor.State.reportDirectory, "evidence.json");
            File.WriteAllText(path, "{}");
            Pump();
            Assert.That(Calls, Does.Not.Contain("after-artifact-save"));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
        }

        [Test]
        public void FinallyCanRegisterReleaseEvidenceAfterEarlierCleanupFailure()
        {
            string path = "";
            RegisterAttachment = (run, item, args) =>
                UPilotAutomationStepService.RegisterArtifact(run, item, "release-evidence", path);
            Start(Item("base", "fail_cleanup"), Item("artifact", "", "Finally"));
            path = Path.Combine(_executor.State.reportDirectory, "release.json");
            File.WriteAllText(path, "{\"released\":true}");
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(_executor.State.registeredArtifacts.Single().kind, Is.EqualTo("release-evidence"));
            Assert.That(_executor.State.steps[1].outcome, Is.EqualTo(AutomationStepStatus.Succeeded));
            Assert.That(UPilotAutomationStepService.Public(_executor.State).artifacts.attachments.Select(a => a.artifactKind),
                Is.EquivalentTo(new[] { "report", "timing", "release-evidence" }));
        }
        [Test]
        public void ServiceObservationDistinguishesInitializationFromMissingRun()
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                UPilotAutomationStepService.ReadState(null, "run", "operation")).Message,
                Is.EqualTo("STEP_SERVICE_INITIALIZING"));
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                UPilotAutomationStepService.ReadState(_executor, "run", "operation")).Message,
                Is.EqualTo("STEP_RUN_IDENTITY_MISMATCH"));
            Assert.That(Calls, Is.Empty);
            Assert.That(File.Exists(_store), Is.False);
        }
        [Test]
        public void ServiceObservationPreservesExactIdentityAndNeverAdvances()
        {
            Start(Item("base", "wait"));
            var run = _executor.State;
            string stored = File.ReadAllText(_store);
            var observed = UPilotAutomationStepService.ReadState(_executor, run.runId, run.operationId);
            Assert.That(observed.cursor, Is.EqualTo(0));
            Assert.That(observed.steps[0].stage, Is.EqualTo("Pending"));
            observed.status = "ChangedByCaller";
            Assert.That(_executor.State.status, Is.Not.EqualTo("ChangedByCaller"));
            foreach (var identity in new[] { new[] { "other", run.operationId }, new[] { run.runId, "other" },
                new[] { "", run.operationId }, new[] { run.runId, "" } })
                Assert.That(Assert.Throws<InvalidOperationException>(() =>
                    UPilotAutomationStepService.ReadState(_executor, identity[0], identity[1])).Message,
                    Is.EqualTo("STEP_RUN_IDENTITY_MISMATCH"));
            Assert.That(Calls, Is.Empty);
            Assert.That(File.ReadAllText(_store), Is.EqualTo(stored));
        }
        [TestCase("base")] [TestCase("direct")]
        public void InterfaceAndBaseHaveIdenticalSequencingAndReadOnlyStatus(string id)
        {
            const string raw = "  {\"text\":\"${start.x}\"}  ";
            Start(Item(id, raw), Item(id, "last"));
            for (int i = 0; i < 5; i++) { var ignored = _executor.State; }
            Assert.That(Calls, Is.Empty);
            Pump();
            Assert.That(Calls, Is.EqualTo(new[] { "execute:" + raw, "cleanup:" + raw, "execute:last", "cleanup:last" }));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.artifacts.Select(a => a.kind), Is.EquivalentTo(new[] { "events", "summary", "report", "timing" }));
            Assert.That(File.ReadAllText(_executor.State.artifacts.Single(a => a.kind == "report").path),
                Does.Contain("Console: Not captured; not validated"));
        }
        [Test]
        public void ReportCommitRestoresFrozenTimeWithoutReplayingSteps()
        {
            Start(Item("base", "fail"), Item("base", "last", "Finally"));
            Pump();
            var state = _executor.State;
            Assert.That(state.reportCommitJson, Is.Not.Empty);
            var before = state.artifacts.ToDictionary(a => a.kind, a => a.sha256);
            int calls = Calls.Count;
            long finished = state.finishedAtUtcMs;
            // Simulate reload after files committed but before the final run-store save.
            state.terminal = false; state.stage = "Finalizing"; state.artifacts = Array.Empty<AutomationReportArtifact>();
            new AutomationStepRunStore(_store).Save(state);
            _executor.Dispose(); _now += 1000;
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.finishedAtUtcMs, Is.EqualTo(finished));
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.State.artifacts.All(a => before[a.kind] == a.sha256), Is.True);
            Assert.That(File.ReadAllText(_executor.State.artifacts.Single(a => a.kind == "report").path),
                Does.Contain("Console: Not captured; not validated"));
        }

        [Test]
        public void FrozenReportRecoveryRevalidatesEvidence(
            [Values("evidence", "consolePolicy")] string kind,
            [Values("unchanged", "modified", "missing")] string mutation,
            [Values(false, true)] bool firstError,
            [Values(false, true)] bool committed)
        {
            string path = "";
            RegisterAttachment = (run, item, args) => UPilotAutomationStepService.RegisterArtifact(run, item, "evidence", path);
            Start(Item("artifact", firstError ? "fail" : ""));
            path = Path.Combine(_executor.State.reportDirectory, "evidence.json");
            File.WriteAllText(path, "original");
            string target = path;
            if (kind == "consolePolicy")
            {
                // Isolate commit verification without borrowing or stopping a live Capture.
                var writer = AutomationReportWriter.OpenExisting(_executor.State.reportDirectory, _executor.State.runId);
                writer.WriteConsolePolicy("fixture-capture", 0, 0, AutomationLogPolicy.Evaluate(null, null, null), 0);
                target = Path.Combine(writer.DirectoryPath, AutomationReportWriter.ConsolePolicyFileName);
            }
            Pump();
            var state = _executor.State;
            var originalHashes = state.artifacts.ToDictionary(a => a.kind, a => a.sha256);
            string summaryPath = Path.Combine(state.reportDirectory, "summary.json");
            byte[] frozenBytes = File.ReadAllBytes(summaryPath);
            if (!committed)
            {
                File.Delete(summaryPath);
                File.Delete(Path.Combine(state.reportDirectory, AutomationReportWriter.TextReportFileName));
                File.Delete(Path.Combine(state.reportDirectory, AutomationReportWriter.TimingFileName));
            }
            string frozenIntent = state.reportCommitJson;
            int calls = Calls.Count;
            state.terminal = false; state.stage = "Finalizing"; state.artifacts = Array.Empty<AutomationReportArtifact>();
            new AutomationStepRunStore(_store).Save(state);
            if (mutation == "missing") File.Delete(target);
            else if (mutation == "modified") File.WriteAllText(target, "tampered");
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            var recovered = _executor.State;
            Assert.That(recovered.reportCommitJson, Is.EqualTo(frozenIntent));
            Assert.That(Calls.Count, Is.EqualTo(calls));
            if (mutation == "unchanged")
            {
                Assert.That(recovered.status, Is.EqualTo(firstError ? "Failed" : "Succeeded"));
                Assert.That(recovered.terminal, Is.True);
                Assert.That(recovered.artifacts.Select(a => a.kind), Is.EquivalentTo(originalHashes.Keys));
                Assert.That(recovered.artifacts.All(a => originalHashes[a.kind] == a.sha256), Is.True);
                Assert.That(File.ReadAllBytes(summaryPath), Is.EqualTo(frozenBytes));
                return;
            }
            Assert.That(recovered.status, Is.EqualTo("Aborted"));
            Assert.That(recovered.terminal, Is.True);
            Assert.That(_executor.Busy, Is.False);
            Assert.That(recovered.error.code, Is.EqualTo(firstError ? "ORIGINAL_FAILURE" : "STEP_ARTIFACT_VERIFICATION_FAILED"));
            if (firstError) Assert.That(recovered.secondaryErrors.Any(e => e.code == "STEP_ARTIFACT_VERIFICATION_FAILED"), Is.True);
            var error = firstError ? recovered.secondaryErrors.Single(e => e.code == "STEP_ARTIFACT_VERIFICATION_FAILED") : recovered.error;
            Assert.That(error.message.Replace('\\', '/'), Does.Contain(kind == "consolePolicy" ? "console-policy.json" : "evidence.json"));
            Assert.That(recovered.artifacts, Is.Empty);
            Assert.That(File.Exists(summaryPath), Is.EqualTo(committed));
            if (committed) Assert.That(File.ReadAllBytes(summaryPath), Is.EqualTo(frozenBytes));
            if (mutation == "modified") Assert.That(File.ReadAllText(target), Is.EqualTo("tampered"));
        }

        [TestCase("base")] [TestCase("direct")]
        public void TimeoutAndCancelApplyWithoutBaseClass(string id)
        {
            var item = Item(id, "wait"); item.timeoutSeconds = 1;
            Start(item, Item(id, "finally", "Finally"));
            _executor.Tick(); _now += 1001; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(Calls, Does.Contain("cancel:wait"));
            Assert.That(Calls, Does.Contain("execute:finally"));
        }
        [Test]
        public void PreflightRejectsWholeListBeforeExecuteOrReport()
        {
            Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("base"), Item("base", "invalid")), "op"));
            Assert.That(Calls, Is.Empty);
            Assert.That(_executor.State, Is.Null);
            Assert.That(File.Exists(_store), Is.False);
        }
        [Test]
        public void PolicyWithoutCaptureFailsBeforeExecutionOrPersistence()
        {
            var plan = Plan(Item("base"));
            plan.logPolicy = new[] { new AutomationLogRule
                { id = "test-rule", action = "deny", messageContains = "blocked" } };
            var error = Assert.Throws<InvalidOperationException>(() => _executor.Start(plan, "op"));
            Assert.That(error.Message, Does.StartWith("STEP_CAPTURE_REQUIRED"));
            Assert.That(Calls, Is.Empty);
            Assert.That(File.Exists(_store), Is.False);
        }
        [TestCase("fail")] [TestCase("fail_error")] [TestCase("throw")]
        public void FailureSkipsNormalRunsFinallyAndFreezesFirstError(string failure)
        {
            Start(Item("base", failure), Item("base", "not-run"), Item("base", "fail", "Finally"), Item("base", "last", "Finally"));
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo(failure == "throw" ? "STEP_EXECUTE_EXCEPTION" : "TEST_FAILURE"));
            Assert.That(Calls, Does.Not.Contain("execute:not-run"));
            Assert.That(Calls, Does.Not.Contain("execute:last"));
            Assert.That(_executor.State.steps[3].outcome, Is.EqualTo(AutomationStepStatus.Skipped));
        }
        [TestCase(false, "Failed")] [TestCase(true, "SucceededWithWarnings")]
        public void SkipRequiresOptInAndIsNotPassed(bool allow, string status)
        {
            var item = Item("base", "skip"); item.allowSkipped = allow;
            Start(item); Pump();
            Assert.That(_executor.State.status, Is.EqualTo(status));
        }
        [Test]
        public void CleanupTimeoutRunsFinallyReleasesSlotAndPreservesFailureReport()
        {
            Start(Item("recover", "fail"), Item("base", "finally", "Finally"));
            Pump(3); _now += 11000; Pump();
            var before = _executor.State;
            string report = File.ReadAllText(Path.Combine(before.reportDirectory, "summary.json"));
            Assert.That(before.status, Is.EqualTo("TimedOut"));
            Assert.That(before.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(before.secondaryErrors.Any(e => e.code == "STEP_CLEANUP_TIMEOUT"), Is.True);
            Assert.That(before.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
            RecoverCleanupProbe.Released = true;
            _executor.RequestCancel(); Pump();
            Assert.That(_executor.State.finishedAtUtcMs, Is.EqualTo(before.finishedAtUtcMs));
            Assert.That(Calls.Count(c => c == "execute:fail"), Is.EqualTo(1));
            Assert.That(Calls.Count(c => c == "execute:finally"), Is.EqualTo(1));
            Assert.That(Calls.Count(c => c.StartsWith("recover-restore:")), Is.Zero);
            Assert.That(File.ReadAllText(Path.Combine(before.reportDirectory, "summary.json")), Is.EqualTo(report));
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.Busy, Is.False);
            Start(Item("base", "next")); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }

        [TestCase("unknown")] [TestCase("barrier")] [TestCase("resource")] [TestCase("missing-report")]
        public void LegacyRecoveryHistoryAbortsWithoutProofOrReplayingCleanup(string fault)
        {
            Start(Item("recover", "fail")); Pump(3); _now += 11000; Pump();
            var state = _executor.State;
            // Simulate a pre-migration record, not a new execution or a new service lifetime.
            state.status = "RecoveryRequired"; state.terminal = false; state.cleanupPending = true;
            if (fault == "unknown") state.cleanupTrackingVersion = 0;
            if (fault == "barrier") state.recoveryBlockedReason = "STEP_STORE_RECOVERY_FAILED";
            if (fault == "resource") state.steps[0].cleanupState = "Unknown";
            if (fault == "missing-report") File.Delete(Path.Combine(state.reportDirectory, "summary.json"));
            new AutomationStepRunStore(_store).Save(state);
            int calls = Calls.Count;
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(_executor.State.cleanupPending, Is.False);
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo(state.steps[0].cleanupState));
        }

        [TestCase(false)] [TestCase(true)]
        public void LateCleanupAvailabilityOrExceptionCannotReopenTerminal(bool throws)
        {
            Start(Item("recover", "fail")); Pump(3); _now += 11000; Pump();
            var before = JsonUtility.ToJson(_executor.State);
            int calls = Calls.Count;
            RecoverCleanupProbe.Released = !throws; RecoverCleanupProbe.ThrowOnCleanup = throws;
            _now += 11000; _executor.RequestCancel(); Pump();
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(JsonUtility.ToJson(_executor.State), Is.EqualTo(before));
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.Busy, Is.False);
        }

        [Test]
        public void FinallyCleanupTimeoutDoesNotRevisitEarlierUnreleasedResource()
        {
            Start(Item("recover", "fail"), Item("base", "cleanup_wait", "Finally"), Item("base", "never", "Finally"));
            Pump(3); _now += 11000; Pump(3); _now += 11000; Pump();
            RecoverCleanupProbe.Released = true;
            int calls = Calls.Count;
            _executor.RequestCancel(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(_executor.State.steps[0].cleanupState, Is.EqualTo("Unresolved"));
            Assert.That(_executor.State.steps[1].cleanupState, Is.EqualTo("Unresolved"));
            Assert.That(_executor.State.terminal, Is.True);
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(Calls.Count(c => c == "execute:cleanup_wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Not.Contain("execute:never"));
        }

        [Test]
        public void RepeatedStopDoesNotExtendOriginalShortCleanupBudget()
        {
            var item = Item("recover", "fail"); item.cleanupTimeoutSeconds = 1;
            Start(item); Pump(3);
            long began = _executor.State.steps[0].cleanupStartedAtUtcMs;
            _executor.RequestCancel(); _now += 500; _executor.RequestCancel(); Pump();
            Assert.That(_executor.State.steps[0].cleanupStartedAtUtcMs, Is.EqualTo(began));
            _now += 501; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(_executor.Busy, Is.False);
        }

        [TestCase("modified")] [TestCase("missing")]
        public void DamagedTerminalReportCannotReviveOrRewriteExecution(string fault)
        {
            Start(Item("recover", "fail")); Pump(3); _now += 11000; Pump();
            string path = Path.Combine(_executor.State.reportDirectory, "summary.json");
            if (fault == "missing") File.Delete(path); else File.AppendAllText(path, "modified");
            int calls = Calls.Count;
            var before = JsonUtility.ToJson(_executor.State);
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); _executor.RequestCancel(); Pump();
            Assert.That(JsonUtility.ToJson(_executor.State), Is.EqualTo(before));
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(_executor.Busy, Is.False);
            Assert.That(File.Exists(path), Is.EqualTo(fault != "missing"));
            if (fault != "missing") Assert.That(File.ReadAllText(path), Does.EndWith("modified"));
        }

        [Test]
        public void PreviousServiceLifetimeNeverRestoresActiveStepOrFinally()
        {
            Start(Item("restore", "wait"), Item("base", "never", "Finally")); _executor.Tick();
            var state = _executor.State; state.serviceLifecycleId = "previous-service";
            new AutomationStepRunStore(_store).Save(state);
            string history = File.ReadAllText(_store); int calls = Calls.Count;
            _executor.Dispose(); _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); _executor.RequestCancel(); Pump();
            Assert.That(_executor.State, Is.Null);
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Calls.Count, Is.EqualTo(calls));
            Assert.That(File.ReadAllText(_store), Is.EqualTo(history));
        }

        [Test]
        public void CancelActiveStepThenFinally()
        {
            Start(Item("base", "wait"), Item("base", "not-run"), Item("base", "last", "Finally"));
            _executor.Tick(); _executor.RequestCancel(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Canceled"));
            Assert.That(Calls, Does.Contain("cancel:wait"));
            Assert.That(Calls, Does.Not.Contain("execute:not-run"));
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void StateIsDetachedAndWarningsContinue()
        {
            Start(Item("base", "warn"), Item("base", "last"));
            var snapshot = _executor.State;
            snapshot.plan.steps[0].arguments = "fail";
            snapshot.cursor = 2;
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("SucceededWithWarnings"));
            Assert.That(Calls, Does.Contain("execute:warn"));
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void BridgeEnvelopePreservesConcreteRunIdentity()
        {
            Start(Item("base"));
            var state = _executor.State;
            var envelope = new ResultMessage<UPilotAutomationStepService.StateResult>
            { payload = UPilotAutomationStepService.Public(state) };
            var json = JsonUtility.ToJson(envelope);
            var decoded = JsonUtility.FromJson<ResultMessage<UPilotAutomationStepService.StateResult>>(json);
            Assert.That(decoded.payload.runId, Is.EqualTo(state.runId));
            Assert.That(decoded.payload.operationId, Is.EqualTo("test-operation"));
            Assert.That(decoded.payload.domain.plan.steps[0].stepId, Is.EqualTo("base"));
            Pump();
            envelope.payload = UPilotAutomationStepService.Public(_executor.State);
            decoded = JsonUtility.FromJson<ResultMessage<UPilotAutomationStepService.StateResult>>(JsonUtility.ToJson(envelope));
            Assert.That(decoded.payload.artifacts.summary.kind, Is.EqualTo("file"));
            Assert.That(decoded.payload.artifacts.summary.path, Does.EndWith("summary.json"));
            Assert.That(decoded.payload.artifacts.summary.bytes, Is.GreaterThan(0));
            Assert.That(decoded.payload.artifacts.summary.sha256.Length, Is.EqualTo(64));
            Assert.That(decoded.payload.artifacts.events.kind, Is.EqualTo("file"));
        }
        [Test]
        public void CleanupExceptionPreservesOriginalErrorAndRunsFinally()
        {
            Start(Item("base", "fail_cleanup"), Item("base", "last", "Finally"));
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "STEP_CLEANUP_EXCEPTION"), Is.True);
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void RestoreNeverReplaysExecute()
        {
            Start(Item("restore", "wait")); _executor.Tick();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count(x => x == "execute:wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Contain("restore"));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }
        [Test]
        public void UnsupportedRestoreRunsFinallyAndAbortsWithoutOccupancy()
        {
            Start(Item("base", "wait"), Item("base", "last", "Finally")); _executor.Tick();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count(x => x == "execute:wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Contain("execute:last"));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
        }
        [Test]
        public void EditorRestartDoesNotResumeEvenRecoverableSteps()
        {
            Start(Item("restore", "wait")); _executor.Tick();
            var persisted = _executor.State; persisted.editorIdentity = "another-process";
            new AutomationStepRunStore(_store).Save(persisted);
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(Calls, Does.Not.Contain("restore"));
        }
        [Test]
        public void SharedWritesAreDurableAndPreserveOtherOwnersAcrossFinally()
        {
            Start(Item("json", "save-one"), Item("json", "save-two"), Item("json", "read", "Finally"));
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            var stored = new AutomationStepRunStore(_store).Load();
            Assert.That(stored.sharedCheckpointJson, Does.Contain("other.owner"));
            Assert.That(stored.steps[0].checkpointJson, Does.Contain("local"));
            Assert.That(stored.steps[2].checkpointJson, Is.EqualTo("{}"));
        }
        [Test]
        public void FailedCheckpointSaveStopsFollowingSideEffect()
        {
            BeforeSave = () => Directory.CreateDirectory(_store + ".tmp");
            Start(Item("json", "save-one"));
            Pump();
            Assert.That(Calls, Does.Not.Contain("saved:save-one"));
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.Busy, Is.False);
        }
        [Test]
        public void WrongIdentityFailsAndWorkerCannotSave()
        {
            Start(Item("json", "worker"), Item("json", "wrong-id"), Item("base", "last", "Finally"));
            Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_CHECKPOINT_SAVE_FAILED"));
            Assert.That(_executor.State.steps[0].outcome, Is.EqualTo(AutomationStepStatus.Succeeded));
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void FailureIsPersistedBeforeGetErrorAndCodeCannotChange()
        {
            ObserveError = code =>
            {
                Assert.That(code, Is.EqualTo("ORIGINAL"));
                Assert.That(new AutomationStepRunStore(_store).Load().error.code, Is.EqualTo("ORIGINAL"));
            };
            Start(Item("json", "{\"status\":\"Failed\",\"errorCode\":\"ORIGINAL\"}"));
            Pump();
            Assert.That(_executor.State.error.code, Is.EqualTo("ORIGINAL"));
            Assert.That(_executor.State.error.message, Is.EqualTo("readable"));
        }
        [TestCase("{}")] [TestCase("null")] [TestCase("{\"status\":\"Failed\"}")] [TestCase("{\"status\":0}")]
        public void InvalidJsonResultRunsCleanupAndFinally(string json)
        {
            Start(Item("json", json), Item("base", "last", "Finally"));
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo("STEP_RESULT_INVALID"));
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void RestoredCleanupResumesCleanupWithoutExecute()
        {
            Start(Item("restore", "last"));
            Pump(2);
            Assert.That(_executor.State.steps[0].stage, Is.EqualTo("Cleaning"));
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count(x => x == "execute:last"), Is.EqualTo(1));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }

        [Test]
        public void ResourceCleanupRestoresWithoutReplayingStepAndAdvancesFinally()
        {
            Start(Item("base", "last"), Item("base", "finally", "Finally"));
            Pump(2);
            var state = _executor.State;
            state.steps[0].stage = "ResourceCleaning";
            new AutomationStepRunStore(_store).Save(state);
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(Calls.Count(c => c == "execute:last"), Is.EqualTo(1));
            Assert.That(Calls, Does.Contain("execute:finally"));
        }

        [Test]
        public void CorruptSnapshotStillRunsFinallyAndPreservesBusinessFailure()
        {
            Start(Item("base", "fail"), Item("base", "finally", "Finally"));
            Pump(2);
            var state = _executor.State;
            state.steps[0].stage = "ResourceCleaning";
            state.snapshots.Add(new AutomationSnapshotRecord
            { instanceId = state.steps[0].instanceId, evidenceKey = "bad", requestKey = "x", requestedAt = 1 });
            new AutomationStepRunStore(_store).Save(state);
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now, failureDiagnostic: message => TestContext.WriteLine(message));
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(Calls, Does.Contain("execute:finally"));
        }
    }
}
