using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public static class UPilotPersistenceStressProbe
    {
        // Two authorized Editors call once with the same evidence key; neither starts another job.
        public static Task<string> Run(string evidenceKey)
        {
            if (!Guid.TryParse(evidenceKey, out _)) throw new ArgumentException("A unique evidence GUID is required.");
            var project = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            string canonical = Path.GetFullPath(Path.Combine(project.Parent.FullName, "UPilotTest"));
            string secondary = Path.GetFullPath(Path.Combine(project.Parent.FullName, "UPilotTest2022"));
            if (!string.Equals(canonical, @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase)
                || (project.FullName != canonical && project.FullName != secondary))
                throw new InvalidOperationException("Only repository acceptance Editors may run this probe.");
            string identity = project.Name;
            string peer = identity == "UPilotTest" ? "UPilotTest2022" : "UPilotTest";
            string directory = Path.Combine(canonical, "Log", "P0P1", "PersistenceStress", evidenceKey);
            int processId = Process.GetCurrentProcess().Id;
            return Task.Run(async () =>
            {
                Directory.CreateDirectory(directory);
                string ready = Path.Combine(directory, identity + ".ready");
                using (new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, peer + ".ready")))
                {
                    if (clock.ElapsedMilliseconds > 30000) throw new TimeoutException("The second authorized Editor did not join.");
                    await Task.Delay(10);
                }
                var snapshot = new TestRunResultPayload { runGuid = identity, status = "running" };
                long lastPeerSequence = 0;
                int peerReads = 0;
                for (int round = 0; round < 100; round++)
                {
                    UPilotTestRunStore.Save(directory, snapshot, false, false);
                    var own = UPilotTestRunStore.Read(Path.Combine(directory, identity + ".json"));
                    if (own.snapshotSequence != round + 1) throw new IOException("Own snapshot sequence mismatch.");
                    var other = UPilotTestRunStore.Read(Path.Combine(directory, peer + ".json"));
                    if (other != null)
                    {
                        if (other.runGuid != peer || other.snapshotSequence < lastPeerSequence)
                            throw new IOException("Peer identity or monotonicity violated.");
                        lastPeerSequence = other.snapshotSequence;
                        peerReads++;
                    }
                    await Task.Delay(10);
                }
                snapshot.status = "completed";
                snapshot.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                UPilotTestRunStore.Save(directory, snapshot, false, false);
                if (peerReads == 0) throw new IOException("No cross-process reads were observed.");
                return JsonUtility.ToJson(new Report
                {
                    processId = processId, project = project.FullName, rounds = 100,
                    peerReads = peerReads, finalSequence = snapshot.snapshotSequence, directory = directory,
                });
            });
        }

        // This is intentionally restricted to the disposable Unity 2022 acceptance Editor.
        // The external harness terminates that exact PID only after the durable boundary marker.
        public static Task<string> ArmCrashAtReplace(string evidenceKey)
        {
            if (!Guid.TryParse(evidenceKey, out _)) throw new ArgumentException("A unique evidence GUID is required.");
            var project = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            if (!string.Equals(project.FullName, @"D:\upilot\Tests~\UPilotTest2022", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the authorized Unity 2022 acceptance Editor may arm the crash probe.");
            string directory = Path.Combine(@"D:\upilot\Tests~\UPilotTest", "Log", "P0P1", "PersistenceCrash", evidenceKey);
            const string runGuid = "crash-writer";
            int processId = Process.GetCurrentProcess().Id;
            return Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(Path.Combine(directory, "writer-started.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonUtility.ToJson(new CrashReport
                    {
                        processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                    }, true));
                    writer.Flush();
                    stream.Flush(true);
                }
                var snapshot = new TestRunResultPayload { runGuid = runGuid, status = "running", phase = "baseline" };
                UPilotTestRunStore.Save(directory, snapshot, true, false);
                UPilotTestRunStore.BeforeAtomicReplaceForTests = path =>
                {
                    if (!string.Equals(path, Path.Combine(directory, runGuid + ".json"), StringComparison.OrdinalIgnoreCase)) return;
                    using (var stream = new FileStream(Path.Combine(directory, "replace-boundary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(JsonUtility.ToJson(new CrashReport
                        {
                            processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                            baselineSequence = snapshot.snapshotSequence,
                        }, true));
                        writer.Flush();
                        stream.Flush(true);
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(30));
                };
                try
                {
                    snapshot.phase = "replace_boundary";
                    UPilotTestRunStore.Save(directory, snapshot, true, false);
                }
                finally
                {
                    UPilotTestRunStore.BeforeAtomicReplaceForTests = null;
                }
                return JsonUtility.ToJson(new CrashReport
                {
                    processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                    baselineSequence = snapshot.snapshotSequence,
                });
            });
        }

        public static string RecoverAfterWriterCrash(string evidenceKey)
        {
            if (!Guid.TryParse(evidenceKey, out _)) throw new ArgumentException("A unique evidence GUID is required.");
            var project = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            if (!string.Equals(project.FullName, @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the canonical acceptance Editor may recover the crash probe.");
            string directory = Path.Combine(project.FullName, "Log", "P0P1", "PersistenceCrash", evidenceKey);
            const string runGuid = "crash-writer";
            string resultPath = Path.Combine(directory, runGuid + ".json");
            if (!File.Exists(Path.Combine(directory, "replace-boundary.json")))
                throw new InvalidOperationException("Crash boundary was not reached.");
            var baseline = UPilotTestRunStore.Read(resultPath);
            if (baseline == null || baseline.runGuid != runGuid || baseline.snapshotSequence != 1 || baseline.phase != "baseline")
                throw new InvalidOperationException("The pre-crash result was not retained as the complete baseline snapshot.");
            baseline.phase = "recovered";
            UPilotTestRunStore.Save(directory, baseline, true, false);
            var recovered = UPilotTestRunStore.Read(resultPath);
            if (recovered == null || recovered.snapshotSequence != 2 || recovered.phase != "recovered")
                throw new InvalidOperationException("Crash recovery did not persist a monotonic successor snapshot.");
            return JsonUtility.ToJson(new CrashReport
            {
                processId = Process.GetCurrentProcess().Id, project = project.FullName, runGuid = runGuid,
                directory = directory, baselineSequence = 1, recoveredSequence = recovered.snapshotSequence,
            });
        }

        public static Task<string> ArmCrashBeforeActiveClear(string evidenceKey)
        {
            if (!Guid.TryParse(evidenceKey, out _)) throw new ArgumentException("A unique evidence GUID is required.");
            var project = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            if (!string.Equals(project.FullName, @"D:\upilot\Tests~\UPilotTest2022", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the authorized Unity 2022 acceptance Editor may arm the pointer crash probe.");
            string directory = Path.Combine(@"D:\upilot\Tests~\UPilotTest", "Log", "P0P1", "PersistenceCrash", evidenceKey);
            const string runGuid = "pointer-writer";
            int processId = Process.GetCurrentProcess().Id;
            return Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(Path.Combine(directory, "pointer-writer-started.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonUtility.ToJson(new CrashReport
                    {
                        processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                        stage = "writer_started",
                    }, true));
                    writer.Flush();
                    stream.Flush(true);
                }
                var snapshot = new TestRunResultPayload { runGuid = runGuid, status = "running", phase = "baseline" };
                UPilotTestRunStore.Save(directory, snapshot, true, false);
                UPilotTestRunStore.BeforeActivePointerClearForTests = path =>
                {
                    if (!string.Equals(path, Path.Combine(directory, "active-run.txt"), StringComparison.OrdinalIgnoreCase)) return;
                    using (var stream = new FileStream(Path.Combine(directory, "active-clear-boundary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(JsonUtility.ToJson(new CrashReport
                        {
                            processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                            baselineSequence = snapshot.snapshotSequence, stage = "before_active_pointer_clear",
                        }, true));
                        writer.Flush();
                        stream.Flush(true);
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(30));
                };
                try
                {
                    snapshot.status = "completed";
                    snapshot.phase = "completed";
                    snapshot.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    UPilotTestRunStore.Save(directory, snapshot, false, true);
                }
                finally
                {
                    UPilotTestRunStore.BeforeActivePointerClearForTests = null;
                }
                return JsonUtility.ToJson(new CrashReport
                {
                    processId = processId, project = project.FullName, runGuid = runGuid, directory = directory,
                    baselineSequence = snapshot.snapshotSequence, stage = "completed_without_crash",
                });
            });
        }

        public static string RecoverAfterActivePointerCrash(string evidenceKey)
        {
            if (!Guid.TryParse(evidenceKey, out _)) throw new ArgumentException("A unique evidence GUID is required.");
            var project = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            if (!string.Equals(project.FullName, @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the canonical acceptance Editor may recover the pointer crash probe.");
            string directory = Path.Combine(project.FullName, "Log", "P0P1", "PersistenceCrash", evidenceKey);
            const string runGuid = "pointer-writer";
            string activePath = Path.Combine(directory, "active-run.txt");
            if (!File.Exists(Path.Combine(directory, "active-clear-boundary.json")))
                throw new InvalidOperationException("Active pointer crash boundary was not reached.");
            var terminal = UPilotTestRunStore.Read(Path.Combine(directory, runGuid + ".json"));
            if (terminal == null || terminal.runGuid != runGuid || terminal.snapshotSequence != 2
                || terminal.phase != "completed" || terminal.endedAt <= 0
                || !File.Exists(activePath) || File.ReadAllText(activePath).Trim() != runGuid)
                throw new InvalidOperationException("The interrupted terminal result/pointer state was not retained.");
            UPilotTestRunStore.Save(directory, terminal, false, true);
            var recovered = UPilotTestRunStore.Read(Path.Combine(directory, runGuid + ".json"));
            if (recovered == null || recovered.snapshotSequence != 3 || File.Exists(activePath))
                throw new InvalidOperationException("Pointer crash recovery did not finish monotonically.");
            return JsonUtility.ToJson(new CrashReport
            {
                processId = Process.GetCurrentProcess().Id, project = project.FullName, runGuid = runGuid,
                directory = directory, baselineSequence = 2, recoveredSequence = recovered.snapshotSequence,
                stage = "active_pointer_cleared",
            });
        }

        [Serializable] private sealed class CrashReport
        {
            public int processId;
            public string project;
            public string runGuid;
            public string directory;
            public long baselineSequence;
            public long recoveredSequence;
            public string stage;
        }

        [Serializable] private sealed class Report
        {
            public int processId;
            public string project;
            public int rounds;
            public int peerReads;
            public long finalSequence;
            public string directory;
        }
    }
}
