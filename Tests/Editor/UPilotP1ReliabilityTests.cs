using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotP1ReliabilityTests
    {
        private string _directory;
        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "UPilot-P1-" + Guid.NewGuid().ToString("N"));
            UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
            UPilotTestRunStore.BeforeActivePointerClearForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
            UPilotTestRunStore.BeforeActivePointerClearForTests = null;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestCase("snapshot")]
        [TestCase("last-pointer")]
        [TestCase("active-clear")]
        public void FinalCommitFailureRetainsActivePointerAndCanResume(string failure)
        {
            var original = new TestRunResultPayload { runGuid = "commit-boundary", status = "cleanup",
                phase = "cleanup", cleanupPending = true, resultAuthoritative = true, outcomeStatus = "failed",
                cleanupResourcesReleased = true };
            UPilotTestRunStore.Save(_directory, original, true, false);
            File.WriteAllText(Path.Combine(_directory, "last-run.txt"), "older-run");
            var candidate = original.ShallowCopyForPersistence();
            candidate.status = "failed";
            candidate.phase = "failed";
            candidate.cleanupPending = false;
            candidate.cleanupSucceeded = true;
            candidate.cleanupStatus = "completed";
            candidate.endedAt = 100;
            UPilotTestRunStore.BeforeAtomicReplaceForTests = path =>
            {
                if ((failure == "snapshot" && Path.GetFileName(path) == "commit-boundary.json")
                    || (failure == "last-pointer" && Path.GetFileName(path) == "last-run.txt"))
                    throw new IOException("injected " + failure);
            };
            UPilotTestRunStore.BeforeActivePointerClearForTests = _ =>
            {
                if (failure == "active-clear") throw new IOException("injected active clear");
            };
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, candidate, false, true));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(original.runGuid));
            var read = UPilotTestRunStore.Read(Path.Combine(_directory, "commit-boundary.json"));
            Assert.That(read.cleanupSucceeded, Is.False);
            Assert.That(read.cleanupPending, Is.True);
            Assert.That(read.outcomeStatus, Is.EqualTo("failed"));
            if (failure != "snapshot")
            {
                Assert.That(read.cleanupStatus, Is.EqualTo("committing"));
                Assert.That(read.endedAt, Is.EqualTo(100));
            }
            UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
            UPilotTestRunStore.BeforeActivePointerClearForTests = null;
            UPilotTestRunStore.Save(_directory, candidate, false, true);
            var committed = UPilotTestRunStore.Read(Path.Combine(_directory, "commit-boundary.json"));
            Assert.That(committed.cleanupSucceeded, Is.True);
            Assert.That(committed.status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
        }

        [Test]
        public void SnapshotSequenceRejectsOlderWriterAndProtectsOtherActiveRun()
        {
            var first = new TestRunResultPayload { runGuid = "first", status = "running" };
            UPilotTestRunStore.Save(_directory, first, true, false);
            var stale = UPilotTestRunStore.Read(Path.Combine(_directory, "first.json"));
            UPilotTestRunStore.Save(_directory, first, true, false);
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, stale, true, false));
            var second = new TestRunResultPayload { runGuid = "second", status = "running" };
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, second, true, false));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo("first"));
            // A new owner may enter only after the original owner commits its release.
            first.endedAt = 50;
            UPilotTestRunStore.Save(_directory, first, false, true);
            UPilotTestRunStore.Save(_directory, second, true, false);
            first.endedAt = 100;
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, first, false, true));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo("second"));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void HeaderScanIgnoresFieldNamesInsideNestedStringValues()
        {
            var snapshot = new TestRunResultPayload
            {
                runGuid = "header-scan",
                status = "running",
                events = new System.Collections.Generic.List<TestRunEventPayload>
                {
                    new TestRunEventPayload
                    {
                        kind = "\"snapshotSequence\": 9999, \"endedAt\": 9999",
                    },
                },
            };
            UPilotTestRunStore.Save(_directory, snapshot, true, false);

            Assert.DoesNotThrow(() => UPilotTestRunStore.Save(_directory, snapshot, true, false));
            Assert.That(snapshot.snapshotSequence, Is.EqualTo(2));
        }

        [Test]
        public void UnchangedPointersAreNotRewrittenOnRepeatedSave()
        {
            var snapshot = new TestRunResultPayload { runGuid = "stable-pointers", status = "running" };
            UPilotTestRunStore.Save(_directory, snapshot, true, false);
            var replaced = new System.Collections.Generic.List<string>();
            UPilotTestRunStore.BeforeAtomicReplaceForTests = path => replaced.Add(Path.GetFileName(path));

            UPilotTestRunStore.Save(_directory, snapshot, true, false);

            Assert.That(replaced, Is.EqualTo(new[] { "stable-pointers.json" }));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "last-run.txt")), Is.EqualTo(snapshot.runGuid));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(snapshot.runGuid));
        }

        [Test]
        public void AtomicReplaceFailureKeepsPreviousReadableResult()
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "result.json");
            UPilotTestRunStore.AtomicWrite(path, "old");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => UPilotTestRunStore.AtomicWrite(path, "new"));
            Assert.That(File.ReadAllText(path), Is.EqualTo("old"));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void ConcurrentIndependentRunsAndReadersNeverObservePartialJson()
        {
            Directory.CreateDirectory(_directory);
            Parallel.For(0, 8, index =>
            {
                var snapshot = new TestRunResultPayload { runGuid = "parallel-" + index, status = "running" };
                for (int sequence = 0; sequence < 8; sequence++)
                {
                    UPilotTestRunStore.Save(_directory, snapshot, false, false);
                    var read = UPilotTestRunStore.Read(Path.Combine(_directory, snapshot.runGuid + ".json"));
                    Assert.That(read.runGuid, Is.EqualTo(snapshot.runGuid));
                    Assert.That(read.snapshotSequence, Is.EqualTo(sequence + 1));
                }
                snapshot.endedAt = 100;
                UPilotTestRunStore.Save(_directory, snapshot, false, false);
            });
            Assert.That(Directory.GetFiles(_directory, "*.json").Length, Is.EqualTo(8));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void InterruptedPointerUpdateKeepsResultAndCanResume()
        {
            var snapshot = new TestRunResultPayload { runGuid = "interrupted", status = "running" };
            UPilotTestRunStore.Save(_directory, snapshot, true, false);
            snapshot.endedAt = 100;
            snapshot.status = "completed";
            UPilotTestRunStore.AtomicWrite(Path.Combine(_directory, "last-run.txt"), "another-run");
            using (var locked = new FileStream(Path.Combine(_directory, "last-run.txt"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, snapshot, false, true));
            var saved = UPilotTestRunStore.Read(Path.Combine(_directory, snapshot.runGuid + ".json"));
            Assert.That(saved.endedAt, Is.EqualTo(100));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(snapshot.runGuid));
            UPilotTestRunStore.Save(_directory, saved, false, true);
            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory,
                new TestRunResultPayload { runGuid = snapshot.runGuid, snapshotSequence = saved.snapshotSequence + 1 }, true, false));
        }

        private static TestRunResultPayload NewAdmission(string lifecycle = null)
        {
            string identity = "admission-" + Guid.NewGuid().ToString("N");
            return new TestRunResultPayload
            {
                runGuid = identity, runAdmissionId = identity,
                serviceLifecycleId = lifecycle ?? Guid.NewGuid().ToString("N"),
                status = "started", phase = "admitted", isRunning = true,
                startedAt = 100, cleanupAttemptDeadlineAt = 200, outcomeStatus = "pending",
            };
        }

        [Test]
        public void PreviousLifetimeRetiresOnlyActiveIndexAndPreservesHistory()
        {
            var previous = new TestRunResultPayload
            {
                runGuid = "previous-lifetime", serviceLifecycleId = Guid.NewGuid().ToString("N"),
                status = "stopping", isRunning = true, cleanupAttemptDeadlineAt = 200,
                resultAuthoritative = false, cleanupSucceeded = false,
            };
            UPilotTestRunStore.Save(_directory, previous, true, false);
            string path = Path.Combine(_directory, previous.runGuid + ".json");
            byte[] history = File.ReadAllBytes(path);
            byte[] last = File.ReadAllBytes(Path.Combine(_directory, "last-run.txt"));

            UPilotTestRunStore.RetirePreviousLifetime(_directory, Guid.NewGuid().ToString("N"));

            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(history));
            Assert.That(File.ReadAllBytes(Path.Combine(_directory, "last-run.txt")), Is.EqualTo(last));
            Assert.That(UPilotTestRunStore.Read(path).cleanupAttemptDeadlineAt, Is.EqualTo(200));
            Assert.That(UPilotTestRunStore.Read(path).cleanupSucceeded, Is.False);
            var next = NewAdmission();
            UPilotTestRunStore.Admit(_directory, next);
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(next.runGuid));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(history));
        }

        [Test]
        public void CurrentLifetimeOwnerSurvivesReloadAndRejectsAnotherAdmission()
        {
            var owner = NewAdmission();
            UPilotTestRunStore.Admit(_directory, owner);
            byte[] history = File.ReadAllBytes(Path.Combine(_directory, owner.runGuid + ".json"));
            UPilotTestRunStore.RetirePreviousLifetime(_directory, owner.serviceLifecycleId);
            var next = NewAdmission(owner.serviceLifecycleId);

            Assert.Throws<IOException>(() => UPilotTestRunStore.Admit(_directory, next));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(owner.runGuid));
            Assert.That(File.ReadAllBytes(Path.Combine(_directory, owner.runGuid + ".json")), Is.EqualTo(history));
            Assert.That(File.Exists(Path.Combine(_directory, next.runGuid + ".json")), Is.False);
        }

        [TestCase("missing-snapshot")]
        [TestCase("malformed-snapshot")]
        [TestCase("wrong-run")]
        [TestCase("missing-lifetime")]
        [TestCase("invalid-lifetime")]
        [TestCase("unsafe-pointer")]
        public void UnknownActiveOwnershipFailsClosedBeforeAdmission(string failure)
        {
            var previous = new TestRunResultPayload
            {
                runGuid = "unconfirmed-owner", serviceLifecycleId = Guid.NewGuid().ToString("N"), status = "running",
            };
            if (failure == "missing-lifetime") previous.serviceLifecycleId = null;
            if (failure == "invalid-lifetime") previous.serviceLifecycleId = "not-a-service-lifetime";
            UPilotTestRunStore.Save(_directory, previous, true, false);
            string path = Path.Combine(_directory, previous.runGuid + ".json");
            string pointer = Path.Combine(_directory, "active-run.txt");
            if (failure == "missing-snapshot") File.Delete(path);
            if (failure == "malformed-snapshot") File.WriteAllText(path, "{ malformed");
            if (failure == "wrong-run") File.WriteAllText(path, "{\"runGuid\":\"another-owner\"}");
            if (failure == "unsafe-pointer") File.WriteAllText(pointer, "../another-project");
            byte[] pointerBefore = File.ReadAllBytes(pointer);
            byte[] historyBefore = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var next = NewAdmission();

            Assert.Throws<IOException>(() => UPilotTestRunStore.RetirePreviousLifetime(_directory, next.serviceLifecycleId));
            Assert.Throws<IOException>(() => UPilotTestRunStore.Admit(_directory, next));
            Assert.That(File.ReadAllBytes(pointer), Is.EqualTo(pointerBefore));
            if (historyBefore != null) Assert.That(File.ReadAllBytes(path), Is.EqualTo(historyBefore));
            Assert.That(File.Exists(Path.Combine(_directory, next.runGuid + ".json")), Is.False);
        }

        [TestCase("snapshot")]
        [TestCase("last-run.txt")]
        [TestCase("active-run.txt")]
        public void AdmissionWriteFailureNeverCrossesRunnerStartBoundary(string failure)
        {
            var admission = NewAdmission();
            bool runnerStarted = false;
            UPilotTestRunStore.BeforeAtomicReplaceForTests = path =>
            {
                if (Path.GetFileName(path) == (failure == "snapshot" ? admission.runGuid + ".json" : failure))
                    throw new IOException("injected admission " + failure);
            };
            Assert.Throws<IOException>(() =>
            {
                UPilotTestRunStore.Admit(_directory, admission);
                runnerStarted = true;
            });
            Assert.That(runnerStarted, Is.False);
            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
            UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
            var next = NewAdmission(admission.serviceLifecycleId);
            Assert.DoesNotThrow(() => UPilotTestRunStore.Admit(_directory, next));
        }

        [Test]
        public void DurableAdmissionHandsOffToOfficialGuidAndLateAdmissionCannotTakeOwnership()
        {
            var admission = NewAdmission();
            UPilotTestRunStore.Admit(_directory, admission);
            var saved = UPilotTestRunStore.Read(Path.Combine(_directory, admission.runGuid + ".json"));
            Assert.That(saved.runAdmissionId, Is.EqualTo(admission.runGuid));
            Assert.That(saved.runnerStartAttempted, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(admission.runGuid));
            var official = saved.ShallowCopyForPersistence();
            official.runGuid = Guid.NewGuid().ToString();
            official.runnerStartAttempted = true;
            official.status = "running";
            UPilotTestRunStore.Save(_directory, official, true, false);

            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(official.runGuid));
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, admission, true, false));
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, admission, false, true));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(official.runGuid));
            official.endedAt = 300;
            official.terminal = true;
            UPilotTestRunStore.Save(_directory, official, false, true);
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, admission, true, false));
            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
        }

        [TestCase("active-pointer")]
        [TestCase("active-clear")]
        public void InterruptedAdmissionHandoffKeepsOwnerAndTerminalCommitUnconfirmed(string failure)
        {
            var admission = NewAdmission();
            UPilotTestRunStore.Admit(_directory, admission);
            var official = admission.ShallowCopyForPersistence();
            official.runGuid = Guid.NewGuid().ToString();
            official.status = "completed"; official.phase = "completed";
            official.endedAt = 300; official.terminal = true;
            official.cleanupSucceeded = true; official.cleanupResourcesReleased = true;
            official.resultAuthoritative = true; official.outcomeStatus = "completed";
            UPilotTestRunStore.BeforeAtomicReplaceForTests = path =>
            {
                if (failure == "active-pointer" && Path.GetFileName(path) == "active-run.txt")
                    throw new IOException("injected GUID handoff");
            };
            UPilotTestRunStore.BeforeActivePointerClearForTests = _ =>
            {
                if (failure == "active-clear") throw new IOException("injected admission clear");
            };
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, official,
                active: failure == "active-pointer", clearActive: failure == "active-clear"));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(admission.runGuid));
            Assert.Throws<IOException>(() => UPilotTestRunStore.Admit(_directory, NewAdmission(admission.serviceLifecycleId)));
            var incomplete = UPilotTestRunStore.Read(Path.Combine(_directory, official.runGuid + ".json"));
            Assert.That(incomplete.terminal, Is.False);
            Assert.That(incomplete.cleanupSucceeded, Is.False);
            Assert.That(incomplete.cleanupStatus, Is.EqualTo("committing"));
            UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
            UPilotTestRunStore.BeforeActivePointerClearForTests = null;
            UPilotTestRunStore.Save(_directory, official, false, true);
            Assert.That(File.Exists(Path.Combine(_directory, "active-run.txt")), Is.False);
            Assert.That(UPilotTestRunStore.Read(Path.Combine(_directory, official.runGuid + ".json")).cleanupSucceeded, Is.True);
        }

        [Test]
        public void AdmissionHandoffRejectsAnotherServiceLifetime()
        {
            var admission = NewAdmission();
            UPilotTestRunStore.Admit(_directory, admission);
            var foreign = admission.ShallowCopyForPersistence();
            foreign.runGuid = Guid.NewGuid().ToString();
            foreign.serviceLifecycleId = Guid.NewGuid().ToString("N");
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, foreign, true, false));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(admission.runGuid));
            Assert.That(File.Exists(Path.Combine(_directory, foreign.runGuid + ".json")), Is.False);
        }

        [Test]
        public void RetirementNeverDeletesAChangedOwner()
        {
            var previous = NewAdmission();
            UPilotTestRunStore.Admit(_directory, previous);
            byte[] history = File.ReadAllBytes(Path.Combine(_directory, previous.runGuid + ".json"));
            UPilotTestRunStore.BeforeActivePointerClearForTests = path => File.WriteAllText(path, "replacement-owner");
            Assert.Throws<IOException>(() => UPilotTestRunStore.RetirePreviousLifetime(_directory, Guid.NewGuid().ToString("N")));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo("replacement-owner"));
            Assert.That(File.ReadAllBytes(Path.Combine(_directory, previous.runGuid + ".json")), Is.EqualTo(history));
        }

        [Test]
        public void ConcurrentAdmissionsHaveOneOwnerAndSeparateProjectDirectoriesAreIsolated()
        {
            string lifecycle = Guid.NewGuid().ToString("N");
            int admitted = 0;
            Parallel.For(0, 8, _ =>
            {
                try
                {
                    UPilotTestRunStore.Admit(_directory, NewAdmission(lifecycle));
                    System.Threading.Interlocked.Increment(ref admitted);
                }
                catch (IOException) { }
            });
            Assert.That(admitted, Is.EqualTo(1));
            string owner = File.ReadAllText(Path.Combine(_directory, "active-run.txt"));
            Assert.That(UPilotTestRunStore.Read(Path.Combine(_directory, owner + ".json")).serviceLifecycleId,
                Is.EqualTo(lifecycle));
            var otherProject = NewAdmission(lifecycle);
            string otherDirectory = Path.Combine(_directory, "project-b");
            UPilotTestRunStore.Admit(otherDirectory, otherProject);
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo(owner));
            Assert.That(File.ReadAllText(Path.Combine(otherDirectory, "active-run.txt")), Is.EqualTo(otherProject.runGuid));
        }

        public class ValidCancel { public static bool CancelTestRun(string guid) => true; }
        public class WrongCancel { public static void CancelTestRun(string guid) {} }
        [Test]
        public void CancelBindingChecksExactApiAndReturnType()
        {
            Assert.That(UPilotTestService.ResolveCancelMethod(typeof(ValidCancel)).ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(Assert.Throws<InvalidOperationException>(() => UPilotTestService.ResolveCancelMethod(typeof(WrongCancel))).Message,
                Does.Contain("TEST_CANCEL_BINDING_UNAVAILABLE"));
        }
    }
}
