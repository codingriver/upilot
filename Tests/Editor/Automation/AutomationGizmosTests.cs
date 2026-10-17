using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class AutomationGizmosTests
    {
        private sealed class Access : IAutomationGizmosAccess
        {
            internal readonly Dictionary<string, bool> values = new();
            internal readonly List<AutomationGizmosWindow> windows = new();
            internal int finds, reads, writes;
            internal string failure;
            internal void Add(string id, string view, bool enabled = true)
            { windows.Add(new AutomationGizmosWindow { id = id, view = view }); values[id] = enabled; }
            public AutomationGizmosWindow[] Find(bool scene, bool game)
            {
                finds++;
                if (failure == "ENUMERATION") throw new InvalidOperationException("unsupported enumeration");
                return windows.Where(w => w.view == "scene" ? scene : game).ToArray();
            }
            public bool Read(AutomationGizmosWindow window)
            {
                reads++;
                if (failure == "READ" || (failure == "VERIFY_READ" && writes > 0)) throw new InvalidOperationException("unavailable getter");
                return values[window.id];
            }
            public void Set(AutomationGizmosWindow window, bool enabled)
            {
                writes++;
                if (failure == "SET") throw new InvalidOperationException("unavailable setter");
                if (failure != "VERIFY") values[window.id] = enabled;
            }
        }

        // Independent pre-feature wire fixture: it must retain JsonUtility's original escaping and field order.
        [Serializable] private sealed class OldItem
        {
            public string instanceId, stepId, arguments, phase;
            public double timeoutSeconds, pollIntervalSeconds, cleanupTimeoutSeconds;
            public bool allowSkipped;
        }
        [Serializable] private sealed class OldPlan
        {
            public int version = 1;
            public OldItem[] steps;
            public AutomationLogRule[] logPolicy = Array.Empty<AutomationLogRule>();
        }

        public sealed class Probe : AutomationStepBase
        {
            internal static bool done, cleaned;
            internal static int executions;
            public override void Execute(string runId, string instanceId, string contextJson, string arguments)
            {
                executions++;
                if (arguments == "fail") Fail("BUSINESS_FAILED", "Original business failure.");
                else if (arguments != "hold" && arguments != "cleanup_hold") Succeed();
                else if (arguments == "cleanup_hold") Succeed();
            }
            public override string Poll(string runId, string instanceId, string contextJson, string arguments)
            { if (arguments == "hold" && done) Succeed(); return base.Poll(runId, instanceId, contextJson, arguments); }
            public override string Cleanup(string runId, string instanceId, string contextJson, string arguments) =>
                ResultJson(arguments == "cleanup_hold" && !cleaned ? "Running" : "Succeeded");
            public override string Restore(string runId, string instanceId, string contextJson, string arguments) => "{\"status\":\"Restored\"}";
        }

        private Access _access;
        private AutomationStepExecutor _executor;
        private AutomationStepRegistry _registry;
        private string _directory, _store;
        private long _now;
        private readonly List<string> _reports = new();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Application.dataPath, "../Library/UPilot/GizmosTests/" + Guid.NewGuid().ToString("N"));
            _store = Path.Combine(_directory, "run.json");
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _access = new Access(); _access.Add("scene-1", "scene"); _access.Add("game-1", "game");
            _registry = Registry(Entry<Probe>("gizmo-probe"));
            Probe.done = Probe.cleaned = false; Probe.executions = 0;
            CreateExecutor();
        }

        private void CreateExecutor() => _executor = new AutomationStepExecutor(_registry, _store, () => _now, gizmosAccess: _access, failureDiagnostic: message => TestContext.WriteLine(message));
        private void Start(params AutomationStepItem[] items)
        {
            var run = _executor.Start(Plan(items), Guid.NewGuid().ToString("N"));
            _reports.Add(run.reportDirectory);
        }
        private static AutomationStepItem Step(string arguments = "", string phase = "Normal", bool scene = false, bool game = false)
        {
            var item = Item("gizmo-probe", arguments, phase);
            if (scene || game) item.gizmos = new AutomationStepGizmos { scene = scene, game = game };
            return item;
        }
        private void Pump(int count = 30) { for (int i = 0; i < count; i++) _executor.Tick(); }

        [TearDown]
        public void TearDown()
        {
            _executor.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            foreach (var report in _reports) if (Directory.Exists(report)) Directory.Delete(report, true);
            _reports.Clear();
        }

        [Test]
        public void InitializesOnceAndDoesNotInspectOrdinaryStepsOrPolling()
        {
            var first = Step("hold");
            Start(first, Step(), Step());
            Assert.That(_access.finds, Is.EqualTo(1));
            Assert.That(_access.values.Values, Is.All.False);
            var reads = _access.reads;
            Pump(200); _now += 5000; Pump(200);
            Assert.That(_access.finds, Is.EqualTo(1)); Assert.That(_access.reads, Is.EqualTo(reads));
            Probe.done = true; Pump();
            Assert.That(Probe.executions, Is.EqualTo(3));
            Assert.That(_access.finds, Is.EqualTo(2), "Only task-start and task-end scans are allowed.");
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.gizmosState.finalCloseVerified, Is.True);
            Assert.That(_access.writes, Is.EqualTo(2), "Already-closed windows must not be written again.");
        }

        [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void ExplicitDemandPersistsThroughCleanupThenClosesOnlyOwnedWindows(bool scene, bool game)
        {
            _access.Add("scene-2", "scene");
            var item = Step("cleanup_hold", scene: scene, game: game);
            Start(item, Step("hold"));
            Pump(8);
            Assert.That(_executor.State.steps[0].stage, Is.EqualTo("Cleaning"));
            Assert.That(_access.values["scene-1"], Is.EqualTo(scene));
            Assert.That(_access.values["scene-2"], Is.EqualTo(scene));
            Assert.That(_access.values["game-1"], Is.EqualTo(game));
            _access.Add("new-scene", "scene");
            Probe.cleaned = true; Pump(8);
            Assert.That(_access.values["scene-1"], Is.False); Assert.That(_access.values["game-1"], Is.False);
            Assert.That(_access.values["new-scene"], Is.True, "Step release must not take over newly created windows.");
            Probe.done = true; Pump();
            Assert.That(_access.values.Values, Is.All.False);
        }

        [Test]
        public void NewWindowsRemainUntouchedUntilFinalCloseAndConsecutiveRunsInitializeAgain()
        {
            Start(Step("hold")); Pump(2);
            _access.Add("new-game", "game"); Pump(30);
            Assert.That(_access.values["new-game"], Is.True); Assert.That(_access.finds, Is.EqualTo(1));
            Probe.done = true; Pump(); Assert.That(_access.values["new-game"], Is.False);
            Start(Step()); Pump(); Assert.That(_access.finds, Is.EqualTo(4));
        }

        [TestCase("READ", "GIZMOS_READ_FAILED")]
        [TestCase("SET", "GIZMOS_SET_FAILED")]
        [TestCase("VERIFY", "GIZMOS_VERIFY_FAILED")]
        [TestCase("VERIFY_READ", "GIZMOS_VERIFY_FAILED")]
        [TestCase("ENUMERATION", "GIZMOS_ENUMERATION_FAILED")]
        public void AccessFailuresWarnAndContinueWithoutBusinessErrors(string failure, string code)
        {
            _access.failure = failure; Start(Step(), Step()); Pump(100);
            var state = _executor.State;
            Assert.That(Probe.executions, Is.EqualTo(2)); Assert.That(state.status, Is.EqualTo("SucceededWithWarnings"));
            Assert.That(state.error?.code, Is.Null.Or.Empty);
            Assert.That(state.gizmosState.finalCloseVerified, Is.False);
            Assert.That(state.gizmosState.observations.Any(o => o.code == code), Is.True);
            var count = state.gizmosState.observations.Count; Pump(100);
            Assert.That(_executor.State.gizmosState.observations.Count, Is.EqualTo(count));
            var summary = JsonUtility.FromJson<AutomationReportSummary>(File.ReadAllText(Path.Combine(state.reportDirectory, "summary.json")));
            Assert.That(summary.gizmos.finalCloseVerified, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(state.reportDirectory, "report.txt")), Does.Contain(code == "" ? "Gizmos" : "Warning"));
        }

        [Test]
        public void WarningsNeverReplaceBusinessFailure()
        {
            _access.failure = "READ"; Start(Step("fail")); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("Failed"));
            Assert.That(_executor.State.error.code, Is.EqualTo("BUSINESS_FAILED"));
        }

        [TestCase(false)] [TestCase(true)]
        public void CancelAndImmediateStopCloseWithoutRepeatingFinalization(bool immediate)
        {
            Start(Step("hold", scene: true, game: true)); Pump(2);
            if (immediate) _executor.StopImmediately(); else _executor.RequestCancel();
            Pump(); var finds = _access.finds; Pump();
            Assert.That(_executor.State.terminal, Is.True); Assert.That(_access.values.Values, Is.All.False);
            Assert.That(_executor.State.gizmosState.finalCloseVerified, Is.True); Assert.That(_access.finds, Is.EqualTo(finds));
        }

        [TestCase("Normal", "hold")] [TestCase("Finally", "hold")]
        [TestCase("Normal", "cleanup_hold")] [TestCase("Finally", "cleanup_hold")]
        public void ExecutionAndCleanupTimeoutsCloseGizmos(string phase, string arguments)
        {
            var step = Step(arguments, phase, true, true); step.timeoutSeconds = step.cleanupTimeoutSeconds = 1;
            Start(step); Pump(6);
            int finds = _access.finds, reads = _access.reads, writes = _access.writes;
            _now += 2000; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("TimedOut"));
            if (phase == "Finally" && arguments == "cleanup_hold")
            {
                Assert.That(_access.values.Values, Is.All.True);
                Assert.That(_access.finds, Is.EqualTo(finds));
                Assert.That(_access.reads, Is.EqualTo(reads));
                Assert.That(_access.writes, Is.EqualTo(writes));
                Assert.That(_executor.State.gizmosState.finalCloseAttempted, Is.False);
                Assert.That(_executor.State.gizmosState.finalCloseVerified, Is.False);
                Assert.That(_executor.State.cleanupOutcome, Is.EqualTo("Unverified"));
                Assert.That(_executor.State.unresolvedResources, Does.Contain("gizmos:final-close"));
                Assert.That(_executor.State.cleanupPending, Is.False);
            }
            else Assert.That(_access.values.Values, Is.All.False);
        }

        [Test]
        public void SameLifetimeRestoreDoesNotRepeatInitializationOrEnable()
        {
            Start(Step("hold", scene: true)); Pump(2);
            var finds = _access.finds; var writes = _access.writes;
            _executor.Dispose(); CreateExecutor(); _executor.RestoreStored(); Pump(20);
            Assert.That(_access.finds, Is.EqualTo(finds)); Assert.That(_access.writes, Is.EqualTo(writes));
            Assert.That(Probe.executions, Is.EqualTo(1));
            Probe.done = true; Pump(); Assert.That(_access.values.Values, Is.All.False);
        }

        [Test]
        public void EmptyWindowsAreReportedAndNoWindowIsCreated()
        {
            _access.windows.Clear(); _access.values.Clear(); Start(Step(scene: true, game: true)); Pump();
            Assert.That(_access.writes, Is.Zero); Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.gizmosState.observations.All(o => o.status == "NoTargets"), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void InterruptedAttemptsStayUnverifiedWithoutReplayingInitialization(bool finalClose)
        {
            Start(Step("hold")); Pump(2);
            var run = _executor.State;
            if (finalClose) run.gizmosState.finalCloseAttempted = true;
            else run.gizmosState.initializationCompleted = false;
            new AutomationStepRunStore(_store).Save(run);
            var finds = _access.finds; _executor.Dispose(); CreateExecutor(); _executor.RestoreStored();
            Assert.That(_access.finds, Is.EqualTo(finds));
            Probe.done = true; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("SucceededWithWarnings"));
            Assert.That(_executor.State.gizmosState.observations.Any(o => o.code ==
                (finalClose ? "GIZMOS_FINAL_CLOSE_UNVERIFIED" : "GIZMOS_INITIALIZATION_UNVERIFIED")), Is.True);
            Assert.That(_access.finds, Is.EqualTo(finds + (finalClose ? 0 : 1)));
            Assert.That(_executor.State.gizmosState.finalCloseVerified, Is.EqualTo(!finalClose));
        }

        [TestCase("null")] [TestCase("false")] [TestCase("[]")]
        [TestCase("{\"scene\":1}")] [TestCase("{\"game\":\"true\"}")]
        [TestCase("{\"scene\":null}")] [TestCase("{\"unknown\":true}")]
        public void InvalidGizmosJsonIsRejectedBeforeExecution(string json)
        {
            var result = AutomationStepPlanValidator.Parse("{\"steps\":[{\"instanceId\":\"a\",\"stepId\":\"gizmo-probe\",\"gizmos\":" + json + "}]}", out _);
            Assert.That(result.ok, Is.False); Assert.That(_access.finds, Is.Zero);
        }

        [Test]
        public void ValidOptionalFieldsAndPureValidation()
        {
            var result = AutomationStepPlanValidator.Parse("{\"steps\":[{\"instanceId\":\"a\",\"stepId\":\"gizmo-probe\",\"gizmos\":{\"scene\":true}},{\"instanceId\":\"b\",\"stepId\":\"gizmo-probe\"}]}", out var plan);
            Assert.That(result.ok, Is.True); Assert.That(_executor.Validate(plan).ok, Is.True);
            Assert.That(plan.steps[0].gizmos.scene, Is.True); Assert.That(plan.steps[0].gizmos.game, Is.False);
            Assert.That(_access.finds, Is.Zero);
        }

        [Test]
        public void LegacyHashLoadsAndDoesNotEnableNewPolicy()
        {
            Start(Step("hold")); Pump(2);
            var run = _executor.State;
            var item = run.plan.steps[0];
            var legacyJson = JsonUtility.ToJson(new OldPlan { steps = new[] { new OldItem
            {
                instanceId = item.instanceId, stepId = item.stepId, arguments = item.arguments, phase = item.phase,
                timeoutSeconds = item.timeoutSeconds, pollIntervalSeconds = item.pollIntervalSeconds,
                cleanupTimeoutSeconds = item.cleanupTimeoutSeconds, allowSkipped = item.allowSkipped,
            } } });
            run.planHash = AutomationStepRunStore.HashText(legacyJson);
            run.gizmosPolicyVersion = 0; run.gizmosState = null;
            new AutomationStepRunStore(_store).Save(run);
            var finds = _access.finds; _executor.Dispose(); CreateExecutor(); _executor.RestoreStored();
            Probe.done = true; Pump();
            Assert.That(_access.finds, Is.EqualTo(finds)); Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(_executor.State.planHash, Is.EqualTo(run.planHash));
        }

        [Test]
        public void RealEditorAdapterClosesExistingSceneAndGameWithoutChangingWindowSet()
        {
            var access = new UnityAutomationGizmosAccess();
            var before = access.Find(true, true);
            Assert.That(before.Any(w => w.view == "scene"), Is.True, "Canonical fixture requires an existing SceneView.");
            Assert.That(before.Any(w => w.view == "game"), Is.True, "Canonical fixture requires an existing GameView.");
            foreach (var window in before) access.Set(window, true);
            using var executor = new AutomationStepExecutor(_registry, Path.Combine(_directory, "real.json"), gizmosAccess: access, failureDiagnostic: message => TestContext.WriteLine(message));
            var state = executor.Start(Plan(Step(scene: true, game: true)), Guid.NewGuid().ToString("N"));
            _reports.Add(state.reportDirectory);
            try
            {
                Assert.That(before.All(w => !access.Read(w)), Is.True);
                executor.Tick(); Assert.That(before.All(access.Read), Is.True);
                for (int i = 0; i < 30; i++) executor.Tick();
                Assert.That(executor.State.status, Is.EqualTo("Succeeded"));
                Assert.That(before.All(w => !access.Read(w)), Is.True);
                Assert.That(access.Find(true, true).Select(w => w.id), Is.EquivalentTo(before.Select(w => w.id)));
            }
            finally { executor.Finish("Aborted", "TEST_CLEANUP", "Bounded test cleanup."); foreach (var window in access.Find(true, true)) access.Set(window, false); }
        }
    }
}
