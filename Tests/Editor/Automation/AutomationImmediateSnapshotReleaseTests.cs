using System;
using System.Collections.Generic;
using System.IO;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class AutomationImmediateSnapshotReleaseTests
    {
        private AutomationStepRun _run;
        private AutomationSnapshotRecord _record;
        private SnapshotJobPayload _job;
        private string _directory;
        [SetUp] public void SetUp()
        {
            _directory = Path.Combine("Log/UPilotSteps/tests", Guid.NewGuid().ToString("N"));
            _run = new AutomationStepRun { runId = "original", reportDirectory = _directory };
            string key = AutomationSnapshotEvidence.RequestKey(_run.runId, "item", "evidence");
            _record = new AutomationSnapshotRecord { instanceId = "item", evidenceKey = "evidence", requestKey = key,
                snapshotId = "owned-snapshot", requestedAt = 1, status = "Running" };
            _run.snapshots.Add(_record);
            _job = new SnapshotJobPayload { snapshotId = _record.snapshotId, requestKey = key,
                outputDirectory = Path.Combine(_directory, "snapshots", key.Substring("step:".Length)) };
        }
        [TearDown] public void TearDown() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        private SnapshotApiResultV1 Status(string id) => new() { ok = true, snapshotId = id, job = _job };
        [Test] public void UncertainResponseIsObservedWithoutReplayingStopEvenAfterReload()
        {
            int saves = 0, cancels = 0;
            var release = new AutomationSnapshotEvidence(_run, () => saves++, () => 100);
            SnapshotApiResultV1 Cancel(string id) { cancels++; Assert.That(_record.cancelIntent, Is.True); Assert.That(saves, Is.EqualTo(1)); throw new IOException("lost response"); }
            Assert.That(release.StopAndObserveRelease(_record, Status, Cancel), Is.False);
            var restored = new AutomationSnapshotEvidence(_run, () => saves++, () => 9999);
            for (int i = 0; i < 5; i++) Assert.That(restored.StopAndObserveRelease(_record, Status, Cancel), Is.False);
            Assert.That(cancels, Is.EqualTo(1)); Assert.That(_record.cancelRequestedAt, Is.EqualTo(100));
            Assert.That(_record.unresolved, Is.True);
        }
        [Test] public void UnpersistedIntentNeverDispatchesNativeCancel()
        {
            int cancels = 0;
            var release = new AutomationSnapshotEvidence(_run, () => throw new IOException("disk full"), () => 100);
            Assert.Throws<IOException>(() => release.StopAndObserveRelease(_record, Status, id => { cancels++; return new(); }));
            Assert.That(cancels, Is.Zero);
        }
        [Test] public void StaleResourceIdentityNeverDispatchesStop()
        {
            int cancels = 0; _job.snapshotId = "someone-else";
            var release = new AutomationSnapshotEvidence(_run, () => { }, () => 100);
            Assert.Throws<InvalidDataException>(() => release.StopAndObserveRelease(_record, Status, id => { cancels++; return new(); }));
            Assert.That(cancels, Is.Zero);
        }
        [Test] public void VerifiedTerminalSnapshotReleasesAndRegistersEvidenceWithoutNativeCancel()
        {
            Directory.CreateDirectory(_job.outputDirectory);
            _job.manifestPath = Path.Combine(_job.outputDirectory, "manifest.json"); File.WriteAllText(_job.manifestPath, "{}");
            var file = AutomationReportWriter.GetArtifactMetadata("snapshot.manifest", _job.manifestPath);
            _job.manifestBytes = file.bytes; _job.manifestSha256 = file.sha256;
            _job.persistenceStatus = "verified"; _job.terminal = true; _job.artifacts = new List<SnapshotArtifactPayload>();
            int cancels = 0;
            var release = new AutomationSnapshotEvidence(_run, () => { }, () => 100);
            Assert.That(release.StopAndObserveRelease(_record, Status, id => { cancels++; return new(); }), Is.True);
            Assert.That(cancels, Is.Zero); Assert.That(_record.unresolved, Is.False);
            Assert.That(_record.status, Is.EqualTo("Failed")); Assert.That(_run.registeredArtifacts.Count, Is.EqualTo(1));
        }
    }
}
