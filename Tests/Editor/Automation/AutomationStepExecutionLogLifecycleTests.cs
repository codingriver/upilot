using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    // Isolated executor unit fixtures, not another business Step launcher.
    public class AutomationStepExecutionLogLifecycleTests
    {
        public sealed class Probe : AutomationStepBase
        {
            internal static int Executions;
            internal static bool Release, ThrowConstruction;
            internal static Action OnExecute;
            public Probe() { if (ThrowConstruction) throw new InvalidOperationException("construction failure"); }
            public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
                arguments == "invalid" ? ValidationError("TEST_INVALID", "invalid") : ValidationOk();
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Executions++;
                OnExecute?.Invoke();
                if (arguments == "execute_throw") throw new InvalidOperationException("execution failure");
                if (arguments == "fail") Fail("BUSINESS_FAILED", "业务失败\n详情");
                else if (arguments != "wait") Succeed();
            }
            public override string Poll(string runId, string instanceId, string contextJson, string arguments)
            { if (arguments == "wait" && Release) Succeed(); return base.Poll(runId, instanceId, contextJson, arguments); }
            public override string Cleanup(string runId, string instanceId, string contextJson, string arguments)
            {
                if (arguments == "cleanup_throw") throw new InvalidOperationException("cleanup failure");
                return ResultJson(arguments == "cleanup_wait" ? "Running" : "Succeeded");
            }
            public override string Restore(string runId, string instanceId, string contextJson, string arguments) => "{\"status\":\"Restored\"}";
        }
        private string _root, _store;
        private AutomationStepRegistry _registry;
        private AutomationStepExecutor _executor;
        private long _now;
        private readonly List<string> _reports = new();
        private readonly List<string> _diagnostics = new();
        private AutomationStepRun Live => (AutomationStepRun)typeof(AutomationStepExecutor).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_executor);
        private string LogDirectory => Path.Combine(_root, "Logs", "UPilot");
        private string[] Lines => Directory.Exists(LogDirectory) ? Directory.GetFiles(LogDirectory, "step-*.log").OrderBy(x => x).SelectMany(File.ReadAllLines).ToArray() : Array.Empty<string>();

        [SetUp] public void SetUp()
        {
            Probe.Executions = 0; Probe.Release = false; Probe.ThrowConstruction = false; Probe.OnExecute = null;
            _reports.Clear(); _diagnostics.Clear();
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UPilot/StepLogLifecycle/", Guid.NewGuid().ToString("N")));
            _store = Path.Combine(_root, "run.json");
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _registry = Registry(Entry<Probe>("probe"));
            CreateExecutor();
        }
        private void CreateExecutor() => _executor = new AutomationStepExecutor(_registry, _store, () => _now,
            new AutomationStepExecutionLog(_root, _diagnostics.Add), failureDiagnostic: message => TestContext.WriteLine(message));
        [TearDown] public void TearDown()
        {
            _executor.Dispose();
            foreach (string path in _reports) if (Directory.Exists(path)) Directory.Delete(path, true);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            Probe.ThrowConstruction = false; Probe.OnExecute = null;
        }
        private void Start(params AutomationStepItem[] items) => _reports.Add(_executor.Start(Plan(items), Guid.NewGuid().ToString("N")).reportDirectory);
        private void Pump(int count = 30) { for (int i = 0; i < count; i++) { _executor.Tick(); _now += 100; } }
        private string Event(string name) => Lines.Single(line => line.Contains(" " + name + " "));
        private int Count(string name) => Lines.Count(line => line.Contains(" " + name + " ") || line.EndsWith(" " + name, StringComparison.Ordinal));

        [TestCase(false, "cleanup_throw", "STEP_CLEANUP_EXCEPTION", "cleanup failure", "Probe.Cleanup")]
        [TestCase(true, "cleanup_throw", "STEP_CLEANUP_EXCEPTION", "cleanup failure", "Probe.Cleanup")]
        [TestCase(false, "execute_throw", "STEP_EXECUTE_EXCEPTION", "execution failure", "Probe.Execute")]
        [TestCase(true, "execute_throw", "STEP_EXECUTE_EXCEPTION", "execution failure", "Probe.Execute")]
        public void ProductionStepExceptionReachesConsoleAndGeneralFileWithStack(bool console, string arguments, string code, string error, string origin)
        {
            bool previous = Logger.LogToUnityConsole;
            try
            {
                Logger.LogToUnityConsole = console;
                _executor.Dispose();
                _executor = new AutomationStepExecutor(_registry, _store, () => _now, new AutomationStepExecutionLog(_root, _diagnostics.Add));
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(code + "[\\s\\S]*" + error + "[\\s\\S]*" + origin));
                Start(Item("probe", arguments, "Finally")); Pump();
                Assert.That(_executor.State.status, Is.EqualTo("Failed"));
                using (var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    Assert.That(reader.ReadToEnd(), Does.Contain(_executor.State.runId).And.Contain(origin));
                Assert.That(Event("step.finished"), Does.Contain(code));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Logger.LogToUnityConsole = previous; }
        }

        [Test] public void FullPlanIsImmediateAndTimingSeparatesExecutionCleanupAndFinalization()
        {
            var item = Item("probe", "中文\nparameter"); item.timeoutSeconds = -1; item.pollIntervalSeconds = -1;
            Start(item);
            Assert.That(Count("run.started"), Is.EqualTo(1));
            string line = Event("run.plan");
            var plan = JsonUtility.FromJson<AutomationStepPlan>(line.Substring(line.IndexOf("计划=", StringComparison.Ordinal) + 3));
            Assert.That(plan.steps[0].arguments, Is.EqualTo(item.arguments));
            Assert.That(plan.steps[0].timeoutSeconds, Is.GreaterThan(0));
            Assert.That(plan.steps[0].pollIntervalSeconds, Is.GreaterThanOrEqualTo(0));
            _executor.Tick(); _now += 500; _executor.Tick(); _now += 25; _executor.Tick();
            string finished = Event("step.finished");
            Assert.That(finished, Does.Contain("执行耗时=0.500s").And.Contain("清理耗时=0.025s").And.Contain("总耗时=0.525s"));
            Assert.That(Count("run.finished"), Is.Zero);
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(Array.FindIndex(Lines, x => x.Contains("run.finished")), Is.GreaterThan(Array.FindIndex(Lines, x => x.Contains("run.report_committed"))));
            Assert.That(_executor.State.artifacts.Any(a => a.path.EndsWith(".log")), Is.False);
            _executor.Finish("Aborted", "LATE", "late"); Pump();
            Assert.That(Count("run.finished"), Is.EqualTo(1));
        }

        [Test] public void FailureSkipsNormalAndStillLogsFinallyWithoutLosingFirstError()
        {
            Start(Item("probe", "fail"), Item("probe", "unused"), Item("probe", "cleanup_throw", "Finally"));
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(Probe.Executions, Is.EqualTo(2));
            Assert.That(Count("step.finished"), Is.EqualTo(3));
            Assert.That(Count("step.skipped"), Is.EqualTo(1));
            Assert.That(Lines.Any(x => x.Contains("状态=未执行") && x.Contains("总耗时=未知")), Is.True);
            Assert.That(Lines.Any(x => x.Contains("阶段=Finally") && x.Contains("step.started")), Is.True);
            Assert.That(Event("run.finished"), Does.Contain("BUSINESS_FAILED").And.Contain("STEP_CLEANUP_EXCEPTION"));
            Assert.That(Lines, Has.None.Contains("详情\n"));
        }

        [TestCase(false)] [TestCase(true)] public void FinallyExecutionOrCleanupTimeoutClosesLogOnce(bool cleanup)
        {
            var item = Item("probe", cleanup ? "cleanup_wait" : "wait", "Finally");
            item.timeoutSeconds = 1; item.cleanupTimeoutSeconds = 1;
            Start(item, Item("probe", "unused", "Finally"));
            if (cleanup) Pump(3); else _executor.Tick();
            _now += 1100; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            Assert.That(Count("run.finished"), Is.EqualTo(1));
            Assert.That(Count("step.finished"), Is.EqualTo(2));
            Assert.That(Lines.Any(x => x.Contains("状态=未执行")), Is.True);
        }

        [Test] public void CancellationLogsRequestAndKeepsCleanupAndFinally()
        {
            Start(Item("probe", "wait"), Item("probe", "ok", "Finally")); _executor.Tick();
            _executor.RequestCancel(); _executor.RequestCancel(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Canceled"));
            Assert.That(Count("run.cancel_requested"), Is.EqualTo(1));
            Assert.That(Count("step.cleanup_finished"), Is.EqualTo(2));
            Assert.That(Count("run.finished"), Is.EqualTo(1));
        }

        [Test] public void ConstructionFailureHasAnExplicitUnstartedRecord()
        {
            Start(Item("probe")); Probe.ThrowConstruction = true; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(Count("step.started"), Is.Zero);
            Assert.That(Event("step.finished"), Does.Contain("Failed（未执行）").And.Contain("总耗时=未知"));
            Assert.That(Count("step.construction_failed"), Is.EqualTo(1));
        }

        [Test] public void ReentrantImmediateStopClosesOnlyAfterCallbackUnwinds()
        {
            Start(Item("probe"), Item("probe", "unused", "Finally"));
            Probe.OnExecute = () => { _executor.StopImmediately(); Assert.That(Count("run.finished"), Is.Zero); };
            Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Aborted"));
            Assert.That(Probe.Executions, Is.EqualTo(1));
            Assert.That(Count("run.finished"), Is.EqualTo(1));
            Assert.That(Count("step.cleanup_started"), Is.Zero);
            Assert.That(Count("step.finished"), Is.EqualTo(2));
        }

        [Test] public void ImmediateStopWaitsForBoundedFrameworkReleaseBeforeFinalLog()
        {
            Start(Item("probe", "wait")); _executor.Tick();
            Live.captureOwned = true; Live.captureStartIntent = true; Live.captureSessionId = "unverified-log-test";
            _executor.StopImmediately();
            Assert.That(_executor.Busy, Is.True);
            Assert.That(Count("run.finished"), Is.Zero);
            _now += AutomationStepExecutor.ImmediateStopReleaseTimeoutMs; _executor.Tick();
            Assert.That(_executor.Busy, Is.False);
            Assert.That(Event("run.finished"), Does.Contain("资源收尾=\"未验证\"").And.Contain("STEP_IMMEDIATE_STOP_RELEASE_TIMEOUT"));
            Assert.That(Count("run.finished"), Is.EqualTo(1));
        }

        [Test] public void ServiceHardStopDuringReleaseClosesLogWithoutClaimingRelease()
        {
            var field = typeof(UPilotAutomationStepService).GetField("s_executor", BindingFlags.Static | BindingFlags.NonPublic);
            var original = (AutomationStepExecutor)field.GetValue(null);
            Assert.That(original?.Busy ?? false, Is.False);
            Start(Item("probe", "wait")); Live.captureOwned = true; Live.captureStartIntent = true; Live.captureSessionId = "unverified-log-test";
            _executor.StopImmediately();
            try
            {
                field.SetValue(null, _executor);
                UPilotAutomationStepService.ResetActive();
                Assert.That(Count("run.service_stopping"), Is.EqualTo(1));
                Assert.That(Event("run.finished"), Does.Contain("资源释放结果未验证"));
                Assert.That(Count("run.finished"), Is.EqualTo(1));
            }
            finally { ((AutomationStepExecutor)field.GetValue(null))?.Dispose(); field.SetValue(null, original); }
        }

        [Test] public void SameLifetimeRestoreContinuesFileWithoutRepeatingStartOrExecute()
        {
            Start(Item("probe", "wait")); _executor.Tick();
            string runId = _executor.State.runId;
            _executor.TraceInterruption("run.reloading", "脚本域重载"); _executor.Dispose();
            _now += 2000; CreateExecutor(); _executor.RestoreStored(); Probe.Release = true; Pump();
            Assert.That(_executor.State.runId, Is.EqualTo(runId));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(Probe.Executions, Is.EqualTo(1));
            Assert.That(Count("run.started"), Is.EqualTo(1));
            Assert.That(Count("step.started"), Is.EqualTo(1));
            Assert.That(Count("step.restored"), Is.EqualTo(1));
            Assert.That(Count("run.finished"), Is.EqualTo(1));
        }

        [Test] public void DisposingAnUnfinishedRunAndStartingNewLifetimeNeverFabricatesEnd()
        {
            Start(Item("probe", "wait")); _executor.Tick();
            Live.serviceLifecycleId = "ended-lifetime"; new AutomationStepRunStore(_store).Save(Live);
            _executor.Dispose(); CreateExecutor(); _executor.RestoreStored(); Pump();
            Assert.That(_executor.State, Is.Null);
            Assert.That(Count("run.finished"), Is.Zero);
            Assert.That(Probe.Executions, Is.EqualTo(1));
        }

        [Test] public void InvalidPlanCreatesNoLogAndWriteFailureDoesNotFailBusiness()
        {
            Assert.Throws<InvalidOperationException>(() => _executor.Start(Plan(Item("probe", "invalid")), "invalid"));
            Assert.That(Directory.Exists(LogDirectory), Is.False);
            Directory.CreateDirectory(Path.Combine(_root, "Logs")); File.WriteAllText(LogDirectory, "blocks directory");
            Start(Item("probe")); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_diagnostics.Count, Is.GreaterThanOrEqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [Test] public void LoggingClockAndFormattingFailuresCannotEscapeOrChangeTerminalResult()
        {
            Start(Item("probe")); Pump();
            string status = _executor.State.status;
            using var broken = new AutomationStepExecutor(_registry, _store, () => throw new InvalidOperationException("clock unavailable"),
                new AutomationStepExecutionLog(_root, _diagnostics.Add), failureDiagnostic: message => TestContext.WriteLine(message));
            typeof(AutomationStepExecutor).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(broken, _executor.State);
            Assert.DoesNotThrow(() => broken.FinishExecutionLog());
            Assert.That(broken.State.status, Is.EqualTo(status));
            Assert.That(_diagnostics.Count, Is.EqualTo(1));
            Assert.DoesNotThrow(() => broken.FinishExecutionLog());
            LogAssert.NoUnexpectedReceived();
        }
    }
}
