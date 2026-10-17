using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class UPilotAutomationStepServiceTests
    {
        private static readonly List<string> Calls = new();
        private static Action OnConstruct;
        private static readonly Type Service = typeof(UPilotAutomationStepService);
        private static readonly FieldInfo ExecutorField = Field("s_executor");
        private static readonly FieldInfo RegistryField = Field("s_registry");
        private static readonly FieldInfo StoreField = Field("s_storePath");
        private static readonly FieldInfo ErrorField = Field("s_initializationError");
        private readonly List<string> _reports = new();
        private AutomationStepExecutor _originalExecutor;
        private object _originalRegistry, _originalStore, _originalError, _originalAdmission;
        private string _directory, _store;
        private bool _isolated;
        private static AutomationStepExecutor Executor => (AutomationStepExecutor)ExecutorField.GetValue(null);
        private static FieldInfo Field(string name) => Service.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

        public sealed class Probe : AutomationStepBase
        {
            public Probe() { var callback = OnConstruct; OnConstruct = null; callback?.Invoke(); }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("execute:" + arguments);
                SaveCheckpoint(runId, instanceId, "{\"started\":true}");
                if (arguments != "wait") Succeed();
            }
            public override string Restore(string runId, string instanceId, string contextJson, string arguments)
            {
                Calls.Add("restore:" + arguments);
                return "{\"status\":\"Restored\"}";
            }
        }

        [SetUp]
        public void SetUp()
        {
            _originalExecutor = Executor;
            if (_originalExecutor?.Busy == true) Assert.Ignore("Official executor is occupied; do not replace it.");
            _originalRegistry = RegistryField.GetValue(null);
            _originalStore = StoreField.GetValue(null);
            _originalError = ErrorField.GetValue(null);
            _originalAdmission = Field("s_readAdmission").GetValue(null);
            _directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Library/UPilot/StepServiceTests/" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_directory);
            _store = Path.Combine(_directory, "step-run.json");
            Calls.Clear();
            OnConstruct = null;
            _isolated = true;
            var registry = Registry(Entry<Probe>("probe"));
            RegistryField.SetValue(null, registry);
            StoreField.SetValue(null, _store);
            ErrorField.SetValue(null, "");
            ExecutorField.SetValue(null, new AutomationStepExecutor(registry, _store, failureDiagnostic: message => TestContext.WriteLine(message)));
            Field("s_readAdmission").SetValue(null, new Func<UPilotAutomationStepService.AdmissionObservation>(() =>
                AdmissionObservation("edit", UPilotAutomationStepService.GetCurrentSnapshot())));
            Invoke("RefreshUpdateSubscription");
        }

        [TearDown]
        public void TearDown()
        {
            OnConstruct = null;
            if (!_isolated) return;
            try { Executor?.Dispose(); }
            finally
            {
                ExecutorField.SetValue(null, _originalExecutor);
                RegistryField.SetValue(null, _originalRegistry);
                StoreField.SetValue(null, _originalStore);
                ErrorField.SetValue(null, _originalError);
                Field("s_readAdmission").SetValue(null, _originalAdmission);
                Invoke("RefreshUpdateSubscription");
                _isolated = false;
            }
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            foreach (var report in _reports) if (Directory.Exists(report)) Directory.Delete(report, true);
            _reports.Clear();
        }

        private static void Invoke(string name, params object[] arguments)
        {
            try { Service.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments); }
            catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        }
        private AutomationStepRun Start(params AutomationStepItem[] items)
        {
            string json = JsonUtility.ToJson(Plan(items));
            Assert.That(JsonUtility.FromJson<AutomationStepValidation>(UPilotAutomationStepService.ValidateJson(json)).ok, Is.True);
            UPilotAutomationStepService.StartJson(Guid.NewGuid().ToString("N"), "", json);
            var state = Executor.State;
            _reports.Add(state.reportDirectory);
            return state;
        }
        private static int UpdateSubscriptions => EditorApplication.update?.GetInvocationList().Count(callback =>
            callback.Method.DeclaringType == Service && callback.Method.Name == "Update") ?? 0;

        private static UPilotAutomationStepService.AdmissionObservation AdmissionObservation(string mode = "edit",
            UPilotAutomationStepService.CurrentSnapshot executor = null) => new()
        {
            currentMode = mode,
            context = new EditorContextPayload { connected = true, authoritative = true,
                compilePhase = "completed", playModeState = mode },
            executor = executor ?? new UPilotAutomationStepService.CurrentSnapshot { initialized = true },
        };

        [TestCase("queued")] [TestCase("compiling")] [TestCase("compiler_finished")]
        [TestCase("domain_reload")] [TestCase("verifying")]
        public void AdmissionRejectsEveryActiveCompilePhaseEvenWhenRawCompilingIsFalse(string phase)
        {
            var observation = AdmissionObservation();
            observation.context.compilePhase = phase;
            var result = UPilotAutomationStepService.CheckAdmission(Plan(Item("probe")), observation);
            Assert.That(result.ok, Is.False);
            Assert.That(result.started, Is.False);
            Assert.That(result.code, Is.EqualTo("STEP_EDITOR_COMPILING"));
        }

        [TestCase("enteringPlay")] [TestCase("exitingPlay")]
        public void AdmissionRejectsModeTransitions(string mode)
        {
            var result = UPilotAutomationStepService.CheckAdmission(Plan(Item("probe")), AdmissionObservation(mode));
            Assert.That(result.code, Is.EqualTo("STEP_MODE_TRANSITION_BUSY"));
        }

        [TestCase("stale", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("disconnected", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("unauthoritative", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("unknown_compile", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("failed_compile", "STEP_EDITOR_COMPILE_FAILED")]
        [TestCase("unknown_mode", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("mode_mismatch", "STEP_EDITOR_STATE_UNAVAILABLE")]
        [TestCase("updating", "STEP_EDITOR_UPDATING")]
        [TestCase("busy", "STEP_EXECUTOR_BUSY")]
        [TestCase("initializing", "STEP_SERVICE_INITIALIZING")]
        public void AdmissionRejectsUnreadyFacts(string reason, string code)
        {
            var observation = AdmissionObservation();
            switch (reason)
            {
                case "stale": observation.context.isStale = true; break;
                case "disconnected": observation.context.connected = false; break;
                case "unauthoritative": observation.context.authoritative = false; break;
                case "unknown_compile": observation.context.compilePhase = "unknown"; break;
                case "failed_compile": observation.context.compilePhase = "failed"; break;
                case "unknown_mode": observation.currentMode = "unknown"; break;
                case "mode_mismatch": observation.context.playModeState = "play"; break;
                case "updating": observation.isUpdating = true; break;
                case "busy": observation.executor.busy = true; break;
                case "initializing": observation.executor.initialized = false; break;
            }
            var result = UPilotAutomationStepService.CheckAdmission(Plan(Item("probe")), observation);
            Assert.That(result.ok, Is.False);
            Assert.That(result.code, Is.EqualTo(code));
        }

        [TestCase("edit")] [TestCase("play")] [TestCase("pause")]
        public void AdmissionPreservesStableCustomPlansAndDoesNotApplyEditOnlyReadiness(string mode)
        {
            var observation = AdmissionObservation(mode);
            observation.context.ready = mode == "edit";
            observation.context.blocked = mode != "edit";
            observation.context.blockedReason = mode == "edit" ? "" : "PlayMode";
            Assert.That(UPilotAutomationStepService.CheckAdmission(Plan(Item("probe")), observation).ok, Is.True);
        }

        [TestCase("upilot.open_scene", "Assets/Scene.unity")]
        [TestCase("upilot.enter_play_mode", "Assets/Scene.unity")]
        public void AdmissionRequiresExplicitEditTransitionThroughOnlyKnownNeutralPrefix(string stepId, string arguments)
        {
            var target = Item(stepId, arguments);
            var plan = Plan(Item("upilot.console_capture_start"), Item("upilot.wait_seconds", "1"), target);
            var rejected = UPilotAutomationStepService.CheckAdmission(plan, AdmissionObservation("play"));
            Assert.That(rejected.ok, Is.False);
            Assert.That(rejected.code, Is.EqualTo("STEP_EDIT_MODE_REQUIRED"));
            Assert.That(rejected.currentMode, Is.EqualTo("play"));
            Assert.That(rejected.instanceId, Is.EqualTo(target.instanceId));
            Assert.That(UPilotAutomationStepService.CheckAdmission(
                Plan(Item("upilot.enter_edit_mode"), target), AdmissionObservation("play")).ok, Is.True);
        }

        [TestCase("probe", "")]
        [TestCase("upilot.capture_snapshot", "")]
        [TestCase("upilot.enter_play_mode", "")]
        public void AdmissionStopsModeInferenceAtCustomSnapshotOrExplicitPlayStep(string stepId, string arguments)
        {
            var plan = Plan(Item(stepId, arguments), Item("upilot.open_scene", "Assets/Scene.unity"));
            Assert.That(UPilotAutomationStepService.CheckAdmission(plan, AdmissionObservation("pause")).ok, Is.True);
        }

        [Test]
        public void FinallyDoesNotImposeStartupEditMode()
        {
            var plan = Plan(Item("upilot.wait_seconds", "1"), Item("upilot.open_scene", "Assets/Scene.unity", "Finally"));
            Assert.That(UPilotAutomationStepService.CheckAdmission(plan, AdmissionObservation("play")).ok, Is.True);
        }

        [Test]
        public void RuntimeAdmissionDoesNotCreateRunReportOrAdvanceCallbacks()
        {
            var before = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);
            var result = JsonUtility.FromJson<UPilotAutomationStepService.AdmissionResult>(
                UPilotAutomationStepService.AdmissionJson(JsonUtility.ToJson(Plan(Item("probe")))));
            Assert.That(result.ok, Is.True);
            Assert.That(result.started, Is.False);
            Assert.That(Executor.State, Is.Null);
            Assert.That(Calls, Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories), Is.EqualTo(before));
            Assert.That(UpdateSubscriptions, Is.Zero);
        }

        [Test]
        public void StaticValidateDoesNotReadRuntimeAdmission()
        {
            Field("s_readAdmission").SetValue(null, new Func<UPilotAutomationStepService.AdmissionObservation>(
                () => throw new InvalidOperationException("Runtime admission must not be read by static validation.")));
            Assert.That(JsonUtility.FromJson<AutomationStepValidation>(UPilotAutomationStepService.ValidateJson(
                JsonUtility.ToJson(Plan(Item("probe"))))).ok, Is.True);
        }

        [TestCase("Verified", "Verified", true)]
        [TestCase("NotRequired", "NotRequired", true)]
        [TestCase("Verified", "SkippedByFinallyFailure", false)]
        [TestCase("Verified", "Unresolved", false)]
        [TestCase("Unverified", "Verified", false)]
        [TestCase("", "Verified", false)]
        public void PublicCleanupRequiresBothFrameworkAndBusinessVerification(string framework, string business, bool expected)
        {
            var run = new AutomationStepRun { terminal = true, status = "Failed", cleanupOutcome = framework,
                reportOutcome = "Written", runId = "failed-run", operationId = "operation" };
            run.steps.Add(new AutomationStepRecord { cleanupState = business, stage = "Completed" });
            var result = UPilotAutomationStepService.Public(run);
            Assert.That(result.businessTerminal, Is.True);
            Assert.That(result.cleanupOutcome, Is.EqualTo(framework));
            Assert.That(result.reportOutcome, Is.EqualTo("Written"));
            Assert.That(result.cleanupSucceeded, Is.EqualTo(expected));
            run.unresolvedResources = new[] { "capture:unverified" };
            Assert.That(UPilotAutomationStepService.Public(run).cleanupSucceeded, Is.False);
        }

        [Test]
        public void StartRechecksAdmissionAndPreservesDeterministicNotStartedResult()
        {
            string plan = JsonUtility.ToJson(Plan(Item("probe")));
            Assert.That(JsonUtility.FromJson<UPilotAutomationStepService.AdmissionResult>(
                UPilotAutomationStepService.AdmissionJson(plan)).ok, Is.True);
            var observation = AdmissionObservation("play");
            observation.context.compilePhase = "compiler_finished";
            Field("s_readAdmission").SetValue(null, new Func<UPilotAutomationStepService.AdmissionObservation>(() => observation));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("STEP_EDITOR_COMPILING"));
            var rejected = JsonUtility.FromJson<UPilotAutomationStepService.StateResult>(
                UPilotAutomationStepService.StartJson("race-" + Guid.NewGuid().ToString("N"), "", plan));
            Assert.That(rejected.ok, Is.False);
            Assert.That(rejected.started, Is.False);
            Assert.That(rejected.code, Is.EqualTo("STEP_EDITOR_COMPILING"));
            Assert.That(rejected.currentMode, Is.EqualTo("play"));
            Assert.That(rejected.runId, Is.Null.Or.Empty);
            Assert.That(Executor.State, Is.Null);
            Assert.That(Calls, Is.Empty);
            Assert.That(File.Exists(_store), Is.False);
        }

        [TestCase("admission")] [TestCase("start")]
        public void StepAdmissionRoutesReachTheirOwnCompileCheckAndAllowPlayMode(string action)
        {
            string route = "automation.steps." + action;
            Assert.That((bool)typeof(UPilotBridge).GetMethod("IsCompileObservation", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { route }), Is.True);
            Assert.That(UPilotCommandRouter.AutomationStepDescriptor(route, action).PlayModePolicy, Is.EqualTo("allowed"));
        }

        [Test] public void CompilationDuringRestoreConstructionInterruptsTheUnpublishedExecutor()
        {
            Start(Item("probe", "wait"), Item("probe", "finally", "Finally")); Invoke("Update");
            Executor.Dispose(); ExecutorField.SetValue(null, null);
            OnConstruct = () => Invoke("OnCompilationStarted", new object());
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("STEP_COMPILATION_STARTED"));
            Invoke("Initialize"); Invoke("Update");
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait" }));
            Assert.That(Executor.State.status, Is.EqualTo("Failed"));
            Assert.That(Executor.State.error.code, Is.EqualTo("STEP_COMPILATION_STARTED"));
            Assert.That(Executor.State.compilationReportState, Is.EqualTo("Written"));
            Assert.That(Field("s_initializingExecutor").GetValue(null), Is.Null);
            Assert.That(Executor.Busy, Is.False);
        }

        private static void AssertReadyIdle()
        {
            var snapshot = UPilotAutomationStepService.GetCurrentSnapshot();
            Assert.That(snapshot.initialized, Is.True);
            Assert.That(snapshot.busy, Is.False);
            Assert.That(snapshot.run, Is.Null);
            Assert.That(snapshot.error, Is.Empty);
            Assert.That(UpdateSubscriptions, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetReturnsANewReadyIdleExecutorEvenWhenPreviousIsMissing(bool missing)
        {
            var previous = Executor;
            if (missing) { previous.Dispose(); ExecutorField.SetValue(null, null); }
            UPilotAutomationStepService.ResetActive();
            Assert.That(Executor, Is.Not.Null.And.Not.SameAs(previous));
            AssertReadyIdle();
        }

        [Test]
        public void ResetAbortsOldRunWithoutExecutingFinallyOrRestoringIt()
        {
            Start(Item("probe", "wait"), Item("probe", "finally", "Finally"));
            Invoke("Update");
            var previous = Executor;
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait" }));
            UPilotAutomationStepService.ResetActive();
            Assert.That(previous.State.status, Is.EqualTo("Aborted"));
            Assert.That(previous.State.error.code, Is.EqualTo("SERVICE_HARD_STOP"));
            AssertReadyIdle();
            Invoke("Update");
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait" }));
        }

        [Test]
        public void ResetNeverLoadsOrDeletesRetainedStorageAndInitializeDoesNotRestoreAfterReset()
        {
            File.WriteAllText(_store, "{}");
            Executor.Dispose(); ExecutorField.SetValue(null, null);
            UPilotAutomationStepService.ResetActive();
            var rebuilt = Executor;
            Invoke("Initialize"); Invoke("Update");
            Assert.That(Executor, Is.SameAs(rebuilt));
            AssertReadyIdle();
            Assert.That(File.ReadAllText(_store), Is.EqualTo("{}"));
            Assert.That(Calls, Is.Empty);
        }

        [Test]
        public void RepeatedResetsAndInitializationsDoNotDuplicateUpdateSubscription()
        {
            for (int i = 0; i < 5; i++)
            {
                var previous = Executor;
                UPilotAutomationStepService.ResetActive();
                Assert.That(Executor, Is.Not.SameAs(previous));
                Invoke("Initialize");
                AssertReadyIdle();
            }
            Assert.That(UpdateSubscriptions, Is.Zero);
        }

        [Test]
        public void OfficialStartAfterResetExecutesNewTaskOnceAndCompletes()
        {
            UPilotAutomationStepService.ResetActive();
            var rebuilt = Executor;
            var run = Start(Item("probe", "new"));
            Assert.That(Executor, Is.SameAs(rebuilt));
            Assert.That(run.serviceLifecycleId, Is.EqualTo(UPilotServiceLifetime.Id));
            Invoke("Initialize"); Invoke("Initialize");
            Assert.That(UpdateSubscriptions, Is.EqualTo(1));
            for (int i = 0; i < 40; i++) Invoke("Update");
            Assert.That(Executor.State.terminal, Is.True);
            Assert.That(Executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(Executor.Busy, Is.False);
            Assert.That(Calls, Is.EqualTo(new[] { "execute:new" }));
            Assert.That(UpdateSubscriptions, Is.Zero);
        }

        [Test]
        public void OrdinaryInitializationStillRestoresSameLifetimeRunWithoutReexecuting()
        {
            var run = Start(Item("probe", "wait"));
            Invoke("Update");
            Executor.Dispose(); ExecutorField.SetValue(null, null);
            Invoke("Initialize");
            Assert.That(Executor.State.runId, Is.EqualTo(run.runId));
            Assert.That(Executor.Busy, Is.True);
            Assert.That(Calls, Is.EqualTo(new[] { "execute:wait", "restore:wait" }));
            Assert.That(UpdateSubscriptions, Is.EqualTo(1));
        }

        [Test]
        public void TerminalImmediateStopKeepsTickUntilItsOriginalResourceReleaseDeadline()
        {
            long now = AutomationStepJson.Now;
            Executor.Dispose();
            ExecutorField.SetValue(null, new AutomationStepExecutor((AutomationStepRegistry)RegistryField.GetValue(null),
                _store, () => now, failureDiagnostic: message => TestContext.WriteLine(message)));
            var run = Start(Item("probe", "wait"));
            var live = (AutomationStepRun)typeof(AutomationStepExecutor).GetField("_run",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Executor);
            // An isolated missing private identity exercises pending framework release without touching a Capture.
            live.captureOwned = true;
            live.captureStartIntent = true;
            live.captureSessionId = "unverified-private-owner";
            var stopped = UPilotAutomationStepService.StopCurrentImmediately(run.runId, run.operationId, run.serviceLifecycleId);
            Assert.That(stopped.changed, Is.True);
            Assert.That(Executor.State.terminal, Is.True);
            Assert.That(Executor.ReleasingImmediateStopResources, Is.True);
            Assert.That(UpdateSubscriptions, Is.EqualTo(1));
            long originalStop = Executor.State.immediateStopRequestedAtUtcMs;
            now = originalStop + AutomationStepExecutor.ImmediateStopReleaseTimeoutMs;
            Invoke("Update");
            Assert.That(Executor.Busy, Is.False);
            Assert.That(UpdateSubscriptions, Is.Zero);
            Assert.That(Executor.State.immediateStopRequestedAtUtcMs, Is.EqualTo(originalStop));
            Assert.That(Executor.State.resourceRecoveryError, Does.Contain("STEP_IMMEDIATE_STOP_RELEASE_TIMEOUT"));
            Assert.That(Calls, Is.Empty);
        }

        [Test]
        public void InitializationFailureIsObservedWithoutRetryAndResetClearsIt()
        {
            Executor.Dispose(); ExecutorField.SetValue(null, null);
            // Exercise a real creation failure without changing the executor's store-recovery policy.
            // Registry access rejects the worker thread before touching Unity or constructing an executor.
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { Invoke("CreateExecutor", true); }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            Assert.That(thread.Join(3000), Is.True);
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.Message, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
            var snapshot = UPilotAutomationStepService.GetCurrentSnapshot();
            Assert.That(snapshot.initialized, Is.False);
            Assert.That(snapshot.error, Is.EqualTo("STEP_SERVICE_INITIALIZATION_FAILED: STEP_MAIN_THREAD_REQUIRED"));
            Invoke("Update");
            Assert.That(Executor, Is.Null);
            Assert.That(UPilotAutomationStepService.GetCurrentSnapshot().error, Is.EqualTo(snapshot.error));
            UPilotAutomationStepService.ResetActive();
            AssertReadyIdle();
        }

        [Test]
        public void OldFinalizationFailureDoesNotPreventNewExecutorCreation()
        {
            bool failClock = false;
            Executor.Dispose();
            ExecutorField.SetValue(null, new AutomationStepExecutor((AutomationStepRegistry)RegistryField.GetValue(null),
                _store, () => failClock ? throw new InvalidOperationException("old finish failed") : AutomationStepJson.Now, failureDiagnostic: message => TestContext.WriteLine(message)));
            Start(Item("probe", "wait"));
            var previous = Executor;
            failClock = true;
            // Interruption tracing and final log closure each encounter the injected clock fault.
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("执行日志不完整[\\s\\S]*old finish failed"));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("执行日志不完整[\\s\\S]*old finish failed"));
            var error = Assert.Throws<AggregateException>(() => UPilotAutomationStepService.ResetActive());
            Assert.That(error.InnerExceptions.Any(ex => ex.Message == "old finish failed"), Is.True);
            Assert.That(Executor, Is.Not.SameAs(previous));
            AssertReadyIdle();
        }

        [Test]
        public void ManagementRefreshOnlyObservesTheRebuiltIdleExecutor()
        {
            UPilotAutomationStepService.ResetActive();
            var rebuilt = Executor;
            var window = ScriptableObject.CreateInstance<UPilotStepManagementWindow>();
            try
            {
                window.ReadCurrent = UPilotAutomationStepService.GetCurrentSnapshot;
                window.Refresh(0); window.Refresh(1);
                Assert.That(window.Latest.initialized, Is.True);
                Assert.That(window.Latest.busy, Is.False);
                Assert.That(window.Latest.run, Is.Null);
                Assert.That(Executor, Is.SameAs(rebuilt));
                Assert.That(Calls, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void ResetFromWorkerThreadIsRejectedWithoutReplacingExecutor()
        {
            var previous = Executor;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { UPilotAutomationStepService.ResetActive(); }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            Assert.That(thread.Join(3000), Is.True);
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.Message, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
            Assert.That(Executor, Is.SameAs(previous));
        }
    }
}
