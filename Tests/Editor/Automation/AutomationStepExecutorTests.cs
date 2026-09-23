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
                        SaveSharedValue(runId, instanceId, "ksb.logging", "{\"mask\":7}");
                        SaveSharedValue(runId, instanceId, "other.owner", "\"keep\"");
                    }
                    else SaveSharedValue(runId, instanceId, "ksb.logging", "{\"mask\":9}");
                    Calls.Add("saved:" + arguments);
                    Succeed();
                }
                else if (arguments == "read")
                {
                    var root = AutomationStepJsonCodec.ParseObject(contextJson);
                    Assert.That(root.Element("checkpoint").Elements(), Is.Empty);
                    Assert.That(root.Element("shared").Element("ksb.logging").Element("mask").Value, Is.EqualTo("9"));
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
                Assert.Throws<InvalidOperationException>(() => SaveSharedValue(runId, instanceId, "ksb.logging", "null"));
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
            Calls.Clear(); BeforeSave = null; ObserveError = null; RegisterAttachment = null;
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _store = Path.Combine(Application.dataPath, "../Library/UPilot/StepTests/" + Guid.NewGuid().ToString("N") + ".json");
            _registry = Registry(Entry<BaseProbe>("base"), Entry<DirectProbe>("direct"), Entry<RestoreProbe>("restore"),
                Entry<JsonProbe>("json"), Entry<ArtifactProbe>("artifact"));
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
        }
        [TearDown]
        public void TearDown()
        {
            _executor.Dispose();
            if (File.Exists(_store)) File.Delete(_store);
            if (Directory.Exists(_store + ".tmp")) Directory.Delete(_store + ".tmp");
            foreach (var report in _reports) if (Directory.Exists(report)) Directory.Delete(report, true);
            _reports.Clear();
        }
        private void Start(params AutomationStepItem[] items)
        {
            var state = _executor.Start(Plan(items), "test-operation");
            _reports.Add(state.reportDirectory);
        }
        private void Pump(int ticks = 50) { for (int i = 0; i < ticks; i++) _executor.Tick(); }
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
                _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
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
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            Assert.That(recovered.status, Is.EqualTo("RecoveryRequired"));
            Assert.That(recovered.terminal, Is.False);
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
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [TestCase(false, "Failed")] [TestCase(true, "SucceededWithWarnings")]
        public void SkipRequiresOptInAndIsNotPassed(bool allow, string status)
        {
            var item = Item("base", "skip"); item.allowSkipped = allow;
            Start(item); Pump();
            Assert.That(_executor.State.status, Is.EqualTo(status));
        }
        [Test]
        public void CleanupTimeoutStillRunsFinallyAndBlocksNewRun()
        {
            Start(Item("base", "cleanup_wait"), Item("base", "last", "Finally"));
            Pump(3); _now += 11000; Pump();
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
            Assert.That(_executor.State.cleanupPending, Is.True);
            Assert.That(Calls, Does.Contain("execute:last"));
            Assert.Throws<InvalidOperationException>(() => Start(Item("base")));
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
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(_executor.State.secondaryErrors.Any(e => e.code == "STEP_CLEANUP_EXCEPTION"), Is.True);
            Assert.That(Calls, Does.Contain("execute:last"));
        }
        [Test]
        public void RestoreNeverReplaysExecute()
        {
            Start(Item("restore", "wait")); _executor.Tick();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count(x => x == "execute:wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Contain("restore"));
            Assert.That(_executor.State.status, Is.EqualTo("Succeeded"));
        }
        [Test]
        public void UnsupportedRestoreStillRunsFinallyButRequiresRecovery()
        {
            Start(Item("base", "wait"), Item("base", "last", "Finally")); _executor.Tick();
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            _executor.RestoreStored(); Pump();
            Assert.That(Calls.Count(x => x == "execute:wait"), Is.EqualTo(1));
            Assert.That(Calls, Does.Contain("execute:last"));
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
        }
        [Test]
        public void EditorRestartDoesNotResumeEvenRecoverableSteps()
        {
            Start(Item("restore", "wait")); _executor.Tick();
            var persisted = _executor.State; persisted.editorIdentity = "another-process";
            new AutomationStepRunStore(_store).Save(persisted);
            _executor.Dispose();
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
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
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
            Assert.That(_executor.Busy, Is.True);
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
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
            _executor = new AutomationStepExecutor(_registry, _store, () => _now);
            _executor.RestoreStored(); Pump();
            Assert.That(_executor.State.status, Is.EqualTo("RecoveryRequired"));
            Assert.That(_executor.State.error.code, Is.EqualTo("TEST_FAILURE"));
            Assert.That(Calls, Does.Contain("execute:finally"));
        }
    }
}
