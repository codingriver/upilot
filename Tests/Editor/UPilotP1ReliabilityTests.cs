using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotP1ReliabilityTests
    {
        private string _directory;
        [SetUp] public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "UPilot-P1-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Test]
        public void SnapshotSequenceRejectsOlderWriterAndProtectsOtherActiveRun()
        {
            var first = new TestRunResultPayload { runGuid = "first", status = "running" };
            UPilotTestRunStore.Save(_directory, first, true, false);
            var stale = UPilotTestRunStore.Read(Path.Combine(_directory, "first.json"));
            UPilotTestRunStore.Save(_directory, first, true, false);
            Assert.Throws<IOException>(() => UPilotTestRunStore.Save(_directory, stale, true, false));
            var second = new TestRunResultPayload { runGuid = "second", status = "running" };
            UPilotTestRunStore.Save(_directory, second, true, false);
            first.endedAt = 100;
            UPilotTestRunStore.Save(_directory, first, false, true);
            Assert.That(File.ReadAllText(Path.Combine(_directory, "active-run.txt")), Is.EqualTo("second"));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
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
