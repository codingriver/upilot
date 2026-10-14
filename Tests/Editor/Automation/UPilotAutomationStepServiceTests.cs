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
        private static readonly Type Service = typeof(UPilotAutomationStepService);
        private static readonly FieldInfo ExecutorField = Field("s_executor");
        private static readonly FieldInfo RegistryField = Field("s_registry");
        private static readonly FieldInfo StoreField = Field("s_storePath");
        private static readonly FieldInfo ErrorField = Field("s_initializationError");
        private readonly List<string> _reports = new();
        private AutomationStepExecutor _originalExecutor;
        private object _originalRegistry, _originalStore, _originalError;
        private string _directory, _store;
        private bool _isolated;
        private static AutomationStepExecutor Executor => (AutomationStepExecutor)ExecutorField.GetValue(null);
        private static FieldInfo Field(string name) => Service.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

        public sealed class Probe : AutomationStepBase
        {
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
            _directory = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Library/UPilot/StepServiceTests/" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_directory);
            _store = Path.Combine(_directory, "step-run.json");
            Calls.Clear();
            _isolated = true;
            var registry = Registry(Entry<Probe>("probe"));
            RegistryField.SetValue(null, registry);
            StoreField.SetValue(null, _store);
            ErrorField.SetValue(null, "");
            ExecutorField.SetValue(null, new AutomationStepExecutor(registry, _store));
            Invoke("RefreshUpdateSubscription");
        }

        [TearDown]
        public void TearDown()
        {
            if (!_isolated) return;
            try { Executor?.Dispose(); }
            finally
            {
                ExecutorField.SetValue(null, _originalExecutor);
                RegistryField.SetValue(null, _originalRegistry);
                StoreField.SetValue(null, _originalStore);
                ErrorField.SetValue(null, _originalError);
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
                _store, () => now));
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
                _store, () => failClock ? throw new InvalidOperationException("old finish failed") : AutomationStepJson.Now));
            Start(Item("probe", "wait"));
            var previous = Executor;
            failClock = true;
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
