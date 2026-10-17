// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCompileEvidenceTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private string _directory, _path;
        private UPilotCompileService _service;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "UPilotCompileEvidenceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "CompileErrors.json");
            _service = Create();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        private UPilotCompileService Create(long processCreatedAt = 456, string producer = "producer") =>
            new UPilotCompileService(_path, _directory, 123, processCreatedAt, producer);

        private static void Signal(UPilotCompileService service, string method) =>
            typeof(UPilotCompileService).GetMethod(method, Private).Invoke(service, new object[] { null });

        private CompileErrorsPayload Read() => JsonUtility.FromJson<CompileErrorsPayload>(File.ReadAllText(_path));
        private void Save(CompileErrorsPayload payload) => File.WriteAllText(_path, JsonUtility.ToJson(payload));
        private void Start() => Signal(_service, "OnCompilationStarted");
        private void Finish() => Signal(_service, "OnCompilationFinished");
        private static void SetProperty(UPilotCompileService service, string name, object value) =>
            typeof(UPilotCompileService).GetProperty(name).GetSetMethod(true).Invoke(service, new[] { value });

        [Test]
        public void OfflineCompilerAndReloadObservationsPersistOnlyActualMilestones()
        {
            var observed = new System.Collections.Generic.List<string>();
            _service.LocalStateChanged += observed.Add;
            Start();
            Assert.That(Read().compileEvidence.milestones.Select(value => value.stage), Is.EqualTo(new[] { "compiling" }));
            Assert.That(Read().compileEvidence.persistenceVerified, Is.True);
            Finish();
            var finished = _service.CompileFinishedAt;
            var operation = _service.CompileOperationId;
            _service.MarkDomainReload();
            var reloadRevision = Read().compileEvidence.revision;
            _service.MarkDomainReload();
            Assert.That(Read().compileEvidence.revision, Is.EqualTo(reloadRevision), "Duplicate reload notification is not new evidence.");

            var restored = Create();
            Assert.That(restored.Phase, Is.EqualTo("verifying"));
            Assert.That(restored.CompileOperationId, Is.EqualTo(operation));
            Assert.That(restored.CompileFinishedAt, Is.EqualTo(finished));
            Assert.That(restored.CompleteVerification(), Is.True);
            var payload = Read();
            Assert.That(payload.phase, Is.EqualTo("completed"));
            Assert.That(payload.errorsVerified, Is.True);
            Assert.That(payload.total, Is.Zero);
            Assert.That(payload.compileEvidence.milestones.Select(value => value.stage), Is.EqualTo(new[]
                { "compiling", "compiler_finished", "domain_reload", "verifying", "terminal" }));
            Assert.That(payload.compileEvidence.milestones.Any(value => value.stage == "queued"), Is.False,
                "An automatic compile cannot manufacture a queued MCP request.");
            Assert.That(payload.finishedAt, Is.EqualTo(finished));
            Assert.That(observed, Is.EqualTo(new[] { "compile_started", "compiler_finished", "domain_reload_starting" }));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void EmptyErrorListWithoutCompilerFinishedEvidenceCannotBecomeSuccess()
        {
            Start();
            SetProperty(_service, "Phase", "verifying");
            SetProperty(_service, "IsCompiling", false);
            LogAssert.Expect(LogType.Error, new Regex("Compile verification refused"));
            Assert.That(_service.CompleteVerification(), Is.False);
            Assert.That(_service.Terminal, Is.False);
            Assert.That(_service.ErrorsVerified, Is.False);
            Assert.That(_service.CompileFinishedAt, Is.Zero);
            Assert.That(_service.LastCompileVerifiedAt, Is.Zero);
        }

        [Test]
        public void ActualCompilerErrorsRemainAQueryVerifiedFailure()
        {
            Start();
            var messages = new[] { new UnityEditor.Compilation.CompilerMessage
            {
                type = UnityEditor.Compilation.CompilerMessageType.Error,
                file = "Assets/OwnedFixture.cs", line = 7, column = 2, message = "owned compiler diagnostic",
            } };
            typeof(UPilotCompileService).GetMethod("OnAssemblyCompilationFinished", Private)
                .Invoke(_service, new object[] { "OwnedFixture.dll", messages });
            Finish();
            Assert.That(_service.CompleteVerification(), Is.True);
            var payload = Read();
            Assert.That(payload.phase, Is.EqualTo("failed"));
            Assert.That(payload.terminal, Is.True);
            Assert.That(payload.errorsVerified, Is.True);
            Assert.That(payload.errors.Single().message, Is.EqualTo("owned compiler diagnostic"));
            Assert.That(payload.errors.Single().line, Is.EqualTo(7));
        }

        [Test]
        public void RejectedPreRequestKeepsActualQueueEvidenceWithoutStartingCompiler()
        {
            bool inspected = false;
            LogAssert.Expect(LogType.Error, new Regex("编译未触发: queued 状态持久化失败"));
            Assert.That(_service.TryBeginCompile("owned-request", "owned-batch", 123, () =>
            {
                inspected = true;
                var queued = Read();
                Assert.That(queued.phase, Is.EqualTo("queued"));
                Assert.That(queued.compileEvidence.persistenceVerified, Is.True);
                return false; // Refuse before AssetDatabase.Refresh / RequestScriptCompilation.
            }), Is.False);
            Assert.That(inspected, Is.True);
            var payload = Read();
            Assert.That(payload.compileEvidence.milestones.Select(value => value.stage), Is.EqualTo(new[] { "queued", "terminal" }));
            Assert.That(payload.lastCompilerFinishedAt, Is.Zero);
            Assert.That(payload.errorsVerified, Is.False);
        }

        [TestCase("missing")]
        [TestCase("revision")]
        [TestCase("process")]
        [TestCase("producer")]
        [TestCase("finish")]
        [TestCase("errors")]
        public void VerificationRejectsMissingOrMismatchedPersistedResult(string corruption)
        {
            Start(); Finish();
            var payload = Read();
            if (corruption == "missing") File.Delete(_path);
            else
            {
                if (corruption == "revision") payload.compileEvidence.revision++;
                if (corruption == "process") payload.compileEvidence.processCreatedAt++;
                if (corruption == "producer") payload.compileEvidence.producerEpoch = "different";
                if (corruption == "finish") payload.lastCompilerFinishedAt = 0;
                if (corruption == "errors") payload.errors.Add(new CompileErrorItemPayload { message = "changed" });
                Save(payload);
            }
            LogAssert.Expect(LogType.Error, new Regex("Compile verification refused"));
            Assert.That(_service.CompleteVerification(), Is.False);
            Assert.That(_service.ErrorsVerified, Is.False);
            Assert.That(_service.Terminal, Is.False);
            Assert.That(_service.LastCompileVerifiedAt, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RestoringAnotherProcessOrProducerKeepsDiagnosticsUnverified(bool changedProcess)
        {
            Start(); Finish();
            Assert.That(_service.CompleteVerification(), Is.True);
            var restored = changedProcess ? Create(processCreatedAt: 457) : Create(producer: "other-producer");
            Assert.That(restored.Phase, Is.EqualTo("unknown"));
            Assert.That(restored.ErrorsVerified, Is.False);
            Assert.That(restored.Terminal, Is.False);
            Assert.That(restored.CompleteVerification(), Is.False);
            Assert.That(restored.VerificationPending, Is.False);
            Assert.That(restored.IsCompiling, Is.False);
            Assert.That(restored.BuildCompileEvidencePayload(), Is.Null);
            var originalDisk = File.ReadAllText(_path);
            restored.MarkDomainReload();
            Assert.That(File.ReadAllText(_path), Is.EqualTo(originalDisk), "Unknown history is not a current reload milestone.");
        }

        [Test]
        public void LegacyRecordDoesNotBlockANewAuthorizedCompileRequest()
        {
            File.WriteAllText(_path, "{\"compileOperationId\":\"legacy\",\"phase\":\"queued\",\"verificationPending\":true,\"errors\":[]}");
            var restored = Create();
            Assert.That(restored.Phase, Is.EqualTo("unknown"));
            Assert.That(restored.VerificationPending, Is.False);
            Assert.That(restored.IsCompiling, Is.False);
            Assert.That(restored.ErrorsVerified, Is.False);
            bool reachedPreRequest = false;
            LogAssert.Expect(LogType.Error, new Regex("编译未触发: queued 状态持久化失败"));
            restored.TryBeginCompile("new-owned-request", beforeRequest: () =>
            {
                reachedPreRequest = true;
                return false; // Test new request admission without invoking the real compiler.
            });
            Assert.That(reachedPreRequest, Is.True);
            Assert.That(restored.CompileOperationId, Is.Not.EqualTo("legacy"));
            Assert.That(Read().compileEvidence.milestones.Select(value => value.stage),
                Is.EqualTo(new[] { "queued", "terminal" }));
        }

        [Test]
        public void ObserversRunOutsideEvidenceLockAndSeePersistedSnapshots()
        {
            int observed = 0;
            _service.LocalStateChanged += _ =>
            {
                var evidenceLock = typeof(UPilotCompileService).GetField("_compileEvidenceLock", Private).GetValue(_service);
                Assert.That(System.Threading.Monitor.IsEntered(evidenceLock), Is.False);
                Assert.That(Read().compileEvidence.revision, Is.EqualTo(_service.BuildCompileEvidencePayload().revision));
                observed++;
            };
            Start(); Finish();
            Assert.That(_service.CompleteVerification(), Is.True);
            Assert.That(observed, Is.EqualTo(3));
        }

        [Test]
        public void NewCompilationReplacesMilestonesInsteadOfInheritingSuccess()
        {
            Start(); Finish();
            Assert.That(_service.CompleteVerification(), Is.True);
            var prior = _service.CompileOperationId;
            Start();
            var next = Read();
            Assert.That(next.compileOperationId, Is.Not.EqualTo(prior));
            Assert.That(next.compileEvidence.revision, Is.EqualTo(1));
            Assert.That(next.compileEvidence.milestones.Select(value => value.stage), Is.EqualTo(new[] { "compiling" }));
            Assert.That(next.errorsVerified, Is.False);
            Assert.That(next.lastCompilerFinishedAt, Is.Zero);
            Assert.That(next.lastCompileVerifiedAt, Is.Zero);
        }

        [Test]
        public void PersistenceFailureIsVisibleAndDoesNotClaimVerifiedEvidence()
        {
            var obstructedPath = Path.Combine(_directory, "directory-instead-of-file");
            Directory.CreateDirectory(obstructedPath);
            var service = new UPilotCompileService(obstructedPath, _directory, 123, 456, "producer");
            LogAssert.Expect(LogType.Error, new Regex("Persist/readback compile evidence failed"));
            Signal(service, "OnCompilationStarted");
            Assert.That(service.BuildCompileEvidencePayload().persistenceVerified, Is.False);
            Assert.That(service.Terminal, Is.False);
            Assert.That(service.ErrorsVerified, Is.False);
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }
    }
}
