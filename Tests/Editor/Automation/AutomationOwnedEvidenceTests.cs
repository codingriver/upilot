using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class AutomationOwnedEvidenceTests
    {
        [TestCase("{\"required\":null}")]
        [TestCase("{\"required\":\"false\"}")]
        [TestCase("{\"timeoutSeconds\":0}")]
        [TestCase("{\"cancelGraceSeconds\":-1}")]
        [TestCase("{\"capture\":{\"requestKey\":\"external\"}}")]
        [TestCase("{\"capture\":{\"outputDirectory\":\"Assets/elsewhere\"}}")]
        [TestCase("{\"capture\":{\"capturePolicy\":{\"allowFallback\":true}}}")]
        [TestCase("{\"capture\":{\"targets\":[{\"width\":\"1280\"}]}}")]
        [TestCase("{\"unknown\":true}")]
        public void SnapshotRejectsInvalidOptionsWithoutSideEffects(string arguments) =>
            Assert.Throws<FormatException>(() => AutomationSnapshotEvidence.Parse(arguments));

        [Test]
        public void SnapshotDefaultsAreTrustedGameView()
        {
            var options = AutomationSnapshotEvidence.Parse("");
            Assert.That(options.capture.targets.Single().kind, Is.EqualTo("gameView"));
            Assert.That(options.capture.targets.Single().width, Is.EqualTo(1280));
            Assert.That(options.capture.targets.Single().height, Is.EqualTo(720));
            Assert.That(options.timeoutSeconds, Is.EqualTo(3));
            Assert.That(options.cancelGraceSeconds, Is.EqualTo(2));
            Assert.That(options.required, Is.True);
        }

        [Test]
        public void CaptureMustBeFirstNormalAndCannotRepeat()
        {
            var registry = Registry(Entry<ConsoleCaptureStartStep>("upilot.console_capture_start"),
                Entry<AutomationStepExecutorTests.BaseProbe>("probe"));
            AutomationStepItem Item(string id, string step, string phase = "Normal") =>
                new() { instanceId = id, stepId = step, phase = phase };
            foreach (var plan in new[]
            {
                new[] { Item("p", "probe"), Item("c", "upilot.console_capture_start") },
                new[] { Item("c", "upilot.console_capture_start", "Finally") },
                new[] { Item("c", "upilot.console_capture_start"), Item("c2", "upilot.console_capture_start") }
            })
            {
                var result = AutomationStepPlanValidator.Validate(new AutomationStepPlan { steps = plan }, registry);
                Assert.That(result.diagnostics.Any(d => d.code == "STEP_CAPTURE_ORDER_INVALID"), Is.True);
            }
        }

        [Test]
        public void SnapshotTamperingIsNotRehashedIntoTrustedEvidence()
        {
            string directory = Path.Combine("Log/UPilotSteps/tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string image = Path.Combine(directory, "image.png"), manifest = Path.Combine(directory, "manifest.json");
                File.WriteAllText(image, "original");
                File.WriteAllText(manifest, "{}");
                var i = AutomationReportWriter.GetArtifactMetadata("image", image);
                var m = AutomationReportWriter.GetArtifactMetadata("manifest", manifest);
                var job = new SnapshotJobPayload
                {
                    terminal = true, success = true, persistenceStatus = "verified",
                    outputDirectory = directory,
                    manifestPath = manifest, manifestBytes = m.bytes, manifestSha256 = m.sha256,
                    artifacts = new List<SnapshotArtifactPayload>
                    { new() { path = image, bytes = i.bytes, sha256 = i.sha256, acceptedAsEvidence = true } }
                };
                Assert.That(AutomationSnapshotEvidence.VerifyFiles(job).Length, Is.EqualTo(2));
                job.outputDirectory = Path.Combine(directory, "other");
                Assert.Throws<InvalidDataException>(() => AutomationSnapshotEvidence.VerifyFiles(job));
                job.outputDirectory = directory;
                File.WriteAllText(image, "tampered");
                Assert.Throws<InvalidDataException>(() => AutomationSnapshotEvidence.VerifyFiles(job));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void CorruptSnapshotIdentityIsUnresolvedWithoutSkippingOtherResourceCleanup()
        {
            var run = new AutomationStepRun { runId = "run" };
            run.snapshots.Add(new AutomationSnapshotRecord { instanceId = "item", evidenceKey = "bad",
                requestedAt = 1, requestKey = "x" });
            run.snapshots.Add(new AutomationSnapshotRecord { instanceId = "item", evidenceKey = "lost",
                requestedAt = 1, requestKey = AutomationSnapshotEvidence.RequestKey("run", "item", "lost"), arguments = "" });
            int saves = 0;
            var evidence = new AutomationSnapshotEvidence(run, () => saves++, () => 10);
            Assert.That(evidence.CompleteItem("item"), Is.True);
            Assert.That(run.snapshots.All(s => s.unresolved && s.status == "Failed"), Is.True);
            Assert.That(saves, Is.EqualTo(2));
            Assert.That(evidence.Error("item", "bad"), Does.Contain("STEP_SNAPSHOT_CHECKPOINT_INVALID"));
        }

        [Test]
        public void SnapshotLateReleaseRequiresOriginalIdentityAndPreservesFailure()
        {
            string directory = Path.Combine("Log/UPilotSteps/tests", Guid.NewGuid().ToString("N"));
            var run = new AutomationStepRun { runId = "original", reportDirectory = directory };
            string key = AutomationSnapshotEvidence.RequestKey(run.runId, "item", "evidence");
            string output = Path.Combine(directory, "snapshots", key.Substring("step:".Length));
            Directory.CreateDirectory(output);
            try
            {
                string manifest = Path.Combine(output, "manifest.json"); File.WriteAllText(manifest, "{}");
                var file = AutomationReportWriter.GetArtifactMetadata("snapshot.manifest", manifest);
                var record = new AutomationSnapshotRecord { instanceId = "item", evidenceKey = "evidence",
                    requestKey = key, snapshotId = "original-snapshot", requestedAt = 1,
                    status = "Failed", errorCode = "ORIGINAL_TIMEOUT", unresolved = true };
                run.snapshots.Add(record);
                var evidence = new AutomationSnapshotEvidence(run, () => { }, () => 100);
                var job = new SnapshotJobPayload { snapshotId = "other", requestKey = key, terminal = true,
                    success = false, outputDirectory = output, persistenceStatus = "verified",
                    manifestPath = manifest, manifestBytes = file.bytes, manifestSha256 = file.sha256,
                    artifacts = new List<SnapshotArtifactPayload>() };
                Assert.Throws<InvalidDataException>(() => evidence.AcceptReleased(record, job));
                job.snapshotId = record.snapshotId; job.terminal = false;
                Assert.That(evidence.AcceptReleased(record, job), Is.False);
                Assert.That(record.unresolved, Is.True);
                job.terminal = true;
                Assert.That(evidence.AcceptReleased(record, job), Is.True);
                Assert.That(record.unresolved, Is.False);
                Assert.That(record.status, Is.EqualTo("Failed"));
                Assert.That(record.errorCode, Is.EqualTo("ORIGINAL_TIMEOUT"));
                Assert.That(run.registeredArtifacts.Count, Is.EqualTo(1));
            }
            finally { Directory.Delete(directory, true); }
        }

        [UnityTest]
        public IEnumerator CaptureOwnershipSurvivesReloadObservationAndIncludesFinallyLogs()
        {
            string id = Guid.NewGuid().ToString("N");
            string store = Path.GetFullPath("Library/UPilot/StepTests/" + id + ".capture");
            var capture = new AutomationRunCapture(store, id, false);
            try
            {
                capture.Start();
                string sessionId = capture.SessionId;
                Assert.That(capture.Observe().active, Is.True);
                var restored = new AutomationRunCapture(store, id, true);
                Assert.That(restored.SessionId, Is.EqualTo(sessionId));
                Assert.Throws<InvalidOperationException>(() => restored.Start());
                Debug.Log("Owned capture Finally evidence " + id);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Assert.That(restored.StopAndVerify(now, out var manifest, out var files), Is.True);
                Assert.That(manifest.active, Is.False);
                Assert.That(files.Any(f => f.kind == "consoleCapture.segment"), Is.True);
                using var collector = new AutomationConsoleCollector(sessionId, 0, manifest.nextSequence);
                while (!collector.Complete) { collector.Poll(); yield return null; }
                Assert.That(collector.Records.Any(r => r.message.Contains(id)), Is.True);
                Assert.That(restored.StopAndVerify(now + 1, out _, out _), Is.True);
                // A persisted unknown run may not adopt the token or session.
                Assert.Throws<InvalidDataException>(() => new AutomationRunCapture(store, "other-run", true));
            }
            finally
            {
                if (!string.IsNullOrEmpty(capture.SessionId) && capture.Observe().active)
                    capture.StopAndVerify(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out _, out _);
                if (File.Exists(store)) File.Delete(store);
            }
        }
    }
}
