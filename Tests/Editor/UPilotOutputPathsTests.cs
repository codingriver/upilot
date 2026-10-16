using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Reflection;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotOutputPathsTests
    {
        [Test]
        public void DefaultDiagnosticsUseOwningProjectEvenWhenWorkingDirectoryChanges()
        {
            string cwd = Directory.GetCurrentDirectory();
            string temporary = Path.Combine(Path.GetTempPath(), "upilot-output-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                Directory.SetCurrentDirectory(temporary);
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                var profiler = typeof(UPilotRuntimeDiagnosticsService).GetMethod("ResolveProfilerPath", flags);
                var snapshot = typeof(UPilotSnapshotService).GetMethod("ResolveOutputDirectory", flags);
                string profilerPath = (string)profiler.Invoke(null, new object[] { "", "test", ".json" });
                string snapshotPath = (string)snapshot.Invoke(null, new object[] { "", "snapshot-test" });
                Assert.That(profilerPath, Does.StartWith(UPilotOutputPaths.Resolve(UPilotOutputPaths.Profiler) + Path.DirectorySeparatorChar));
                Assert.That(snapshotPath, Does.StartWith(UPilotOutputPaths.Resolve(UPilotOutputPaths.Snapshots) + Path.DirectorySeparatorChar));
                Assert.That(Logger.ProjectLogsDirectory, Is.EqualTo(UPilotOutputPaths.Resolve(UPilotOutputPaths.Root)));
                Assert.That(Directory.GetFileSystemEntries(temporary), Is.Empty);
            }
            finally
            {
                Directory.SetCurrentDirectory(cwd);
                Directory.Delete(temporary, true);
            }
        }

        [Test]
        public void DefaultConsoleDiscoveryIgnoresLegacyDirectoryAndKeepsItsFiles()
        {
            string id = "output-path-" + Guid.NewGuid().ToString("N");
            string oldDirectory = UPilotOutputPaths.Resolve("Log/UPilotConsole/" + id);
            string newDirectory = UPilotOutputPaths.Resolve(UPilotOutputPaths.ConsoleCaptures + "/" + id);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ConsoleCaptureManifest { sessionId = id }));
            try
            {
                Directory.CreateDirectory(oldDirectory);
                File.WriteAllBytes(Path.Combine(oldDirectory, "session.json"), bytes);
                var method = typeof(UPilotConsoleCaptureService).GetMethod("LoadDefaultRootManifests", BindingFlags.Static | BindingFlags.NonPublic);
                var before = (List<ConsoleCaptureManifest>)method.Invoke(null, null);
                Assert.That(before.Any(item => item.sessionId == id), Is.False);
                Directory.CreateDirectory(newDirectory);
                File.WriteAllBytes(Path.Combine(newDirectory, "session.json"), bytes);
                var after = (List<ConsoleCaptureManifest>)method.Invoke(null, null);
                Assert.That(after.Count(item => item.sessionId == id), Is.EqualTo(1));
                Assert.That(File.ReadAllBytes(Path.Combine(oldDirectory, "session.json")), Is.EqualTo(bytes));
            }
            finally
            {
                if (Directory.Exists(oldDirectory)) Directory.Delete(oldDirectory, true);
                if (Directory.Exists(newDirectory)) Directory.Delete(newDirectory, true);
            }
        }

        [Test]
        public void SnapshotDiscoveryIgnoresLegacyManifests()
        {
            string id = "output-path-" + Guid.NewGuid().ToString("N");
            string oldDirectory = UPilotOutputPaths.Resolve("Log/UPilotSnapshots/" + id);
            string newDirectory = UPilotOutputPaths.Resolve(UPilotOutputPaths.Snapshots + "/" + id);
            try
            {
                Directory.CreateDirectory(oldDirectory);
                Directory.CreateDirectory(newDirectory);
                string oldPath = Path.Combine(oldDirectory, "manifest.json");
                string newPath = Path.Combine(newDirectory, "manifest.json");
                File.WriteAllText(oldPath, JsonUtility.ToJson(new SnapshotJobPayload {
                    snapshotId = id + "-old", outputDirectory = "Log/UPilotSnapshots/" + id,
                    manifestPath = "Log/UPilotSnapshots/" + id + "/manifest.json" }));
                File.WriteAllText(newPath, JsonUtility.ToJson(new SnapshotJobPayload {
                    snapshotId = id + "-new", outputDirectory = UPilotOutputPaths.Snapshots + "/" + id,
                    manifestPath = UPilotOutputPaths.Snapshots + "/" + id + "/manifest.json" }));
                var serviceType = typeof(UPilotSnapshotService);
                var record = serviceType.GetNestedType("PersistedSnapshotRecord", BindingFlags.NonPublic);
                var states = Array.CreateInstance(record, 0);
                var service = new UPilotSnapshotService(null);
                var method = serviceType.GetMethod("ReadPersistedManifests", BindingFlags.NonPublic | BindingFlags.Instance);
                var records = (System.Collections.IDictionary)method.Invoke(service, new object[] { states });
                Assert.That(records.Contains(id + "-old"), Is.False);
                Assert.That(records.Contains(id + "-new"), Is.True);
                Assert.That(File.Exists(oldPath), Is.True);
            }
            finally
            {
                if (Directory.Exists(oldDirectory)) Directory.Delete(oldDirectory, true);
                if (Directory.Exists(newDirectory)) Directory.Delete(newDirectory, true);
            }
        }

        [Test]
        public void ExplicitDiagnosticDirectoryIsPreservedAndOutsideProjectIsRejected()
        {
            string custom = Path.Combine(UPilotProjectConfig.ProjectRoot, "Temp", "custom-output");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var profiler = typeof(UPilotRuntimeDiagnosticsService).GetMethod("ResolveProfilerPath", flags);
            var snapshot = typeof(UPilotSnapshotService).GetMethod("ResolveOutputDirectory", flags);
            Assert.That((string)profiler.Invoke(null, new object[] { custom, "test", ".csv" }),
                Does.StartWith(custom + Path.DirectorySeparatorChar));
            Assert.That((string)snapshot.Invoke(null, new object[] { custom, "snapshot-test" }), Is.EqualTo(custom));
            string outside = Path.GetFullPath(Path.Combine(UPilotProjectConfig.ProjectRoot, "..", "outside-output"));
            Assert.That(Assert.Throws<TargetInvocationException>(() => profiler.Invoke(null, new object[] { outside, "test", ".json" })).InnerException,
                Is.InstanceOf<InvalidOperationException>());
            Assert.Throws<TargetInvocationException>(() => snapshot.Invoke(null, new object[] { outside, "snapshot-test" }));
        }
    }
}
