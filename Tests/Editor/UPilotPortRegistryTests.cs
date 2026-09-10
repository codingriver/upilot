using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPortRegistryTests
    {
        private string _root;
        private string _user;
        private string _a;
        private string _b;
        private UPilotPortRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "UPilot-port-tests-" + Guid.NewGuid().ToString("N"));
            _user = Path.Combine(_root, "user", ".upilot");
            _a = Path.Combine(_root, "project-a");
            _b = Path.Combine(_root, "project-b");
            _registry = new UPilotPortRegistry(_user, _ => true, 2000);
        }

        [TearDown]
        public void TearDown()
        {
            var prefix = Path.Combine(Path.GetTempPath(), "UPilot-port-tests-");
            Assert.That(Path.GetFullPath(_root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), Is.True);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static UPilotProjectConfigData Config(int ws, int http) =>
            new UPilotProjectConfigData { mcp = new UPilotMcpConfig { wsPort = ws, httpPort = http } };

        private static void DiskConfig(string project, int ws, int http) =>
            UPilotPortRegistry.AtomicWrite(UPilotPortRegistry.ConfigPath(project), JsonUtility.ToJson(Config(ws, http)));

        [Test]
        public void OfflineReservationSurvivesRestartAndCrossProtocolConflict()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var second = new UPilotPortRegistry(_user, _ => true);
            Assert.That(second.Check(_b, 19001, 20001), Does.Contain(UPilotPortRegistry.NormalizeProject(_a)));
            Assert.That(second.Recommend(_b, 18001, 19001, 20), Is.EqualTo((18002, 19002)));
            Assert.That(second.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [Test]
        public void ProjectConfigWinsAndOtherProjectsAreNeverRewritten()
        {
            _registry.Commit(_a, Config(18001, 19001));
            DiskConfig(_a, 18002, 19002);
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Not.Empty);
            _registry.Sync(_a);
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Empty);
            var bytes = File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a));
            Assert.Throws<IOException>(() => _registry.Commit(_b, Config(19002, 20001)));
            Assert.That(File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a)), Is.EqualTo(bytes));
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_b)), Is.False);
        }

        [Test]
        public void MissingProjectAndInvalidConfigNeverExpireReservation()
        {
            _registry.Commit(_a, Config(18001, 19001));
            File.Delete(UPilotPortRegistry.ConfigPath(_a));
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Not.Empty);
            UPilotPortRegistry.AtomicWrite(UPilotPortRegistry.ConfigPath(_a), "{}");
            Assert.Throws<InvalidDataException>(() => _registry.Sync(_a));
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Not.Empty);
        }

        [Test]
        public void CorruptRegistryAndUnavailablePortsFailClosed()
        {
            Directory.CreateDirectory(_user);
            File.WriteAllText(_registry.RegistryPath, "{}");
            Assert.Throws<InvalidDataException>(() => _registry.Commit(_a, Config(18001, 19001)));
            Assert.That(File.ReadAllText(_registry.RegistryPath), Is.EqualTo("{}"));
            File.Delete(_registry.RegistryPath);
            var occupied = new UPilotPortRegistry(_user, _ => false);
            Assert.Throws<IOException>(() => occupied.Recommend(_a, 18001, 19001, 3));
            Assert.Throws<IOException>(() => occupied.Commit(_a, Config(18001, 19001)));
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_a)), Is.False);
        }

        [Test]
        public void ConcurrentCommitsHaveOnlyOneWinner()
        {
            Func<string, bool> commit = project =>
            {
                try { new UPilotPortRegistry(_user, _ => true, 2000).Commit(project, Config(18001, 19001)); return true; }
                catch (IOException) { return false; }
            };
            var first = Task.Run(() => commit(_a));
            var second = Task.Run(() => commit(_b));
            Assert.That(Task.WaitAll(new Task[] { first, second }, 5000), Is.True);
            Assert.That(new[] { first.Result, second.Result }.Count(x => x), Is.EqualTo(1));
            Assert.That(_registry.Snapshot(), Has.Count.EqualTo(1));
        }

        [Test]
        public void InterruptedConfigWriteRetainsBothPairsUntilReconciled()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var failing = new UPilotPortRegistry(_user, _ => true, write: (path, text) =>
            {
                if (Path.GetFileName(path) == "config.json") throw new IOException("injected config write failure");
                UPilotPortRegistry.AtomicWrite(path, text);
            });
            Assert.Throws<IOException>(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Not.Empty);
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Not.Empty);
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            _registry.Sync(_a);
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Empty);
        }

        [Test]
        public void InterruptedFinalRegistryWriteRecoversFromNewProjectConfig()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var writes = 0;
            var failing = new UPilotPortRegistry(_user, _ => true, write: (path, text) =>
            {
                if (++writes == 3) throw new IOException("injected final registry write failure");
                UPilotPortRegistry.AtomicWrite(path, text);
            });
            Assert.Throws<IOException>(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Not.Empty);
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Not.Empty);
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18002));
            _registry.Sync(_a);
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Empty);
            Assert.That(_registry.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [Test]
        public void RegistryWriteFailureDoesNotTouchProject()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var bytes = File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a));
            var failing = new UPilotPortRegistry(_user, _ => true,
                write: (_, __) => throw new IOException("injected registry failure"));
            Assert.Throws<IOException>(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a)), Is.EqualTo(bytes));
        }

        [Test]
        public void ReleaseRequiresUnchangedSnapshotAndIdlePorts()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var snapshot = _registry.Snapshot().Single();
            Assert.Throws<IOException>(() => _registry.Release(snapshot, _a));
            var occupied = new UPilotPortRegistry(_user, _ => false);
            Assert.Throws<IOException>(() => occupied.Release(snapshot, _b));
            _registry.Commit(_a, Config(18002, 19002));
            Assert.Throws<IOException>(() => _registry.Release(snapshot, _b));
            _registry.Release(_registry.Snapshot().Single(), _b);
            Assert.That(_registry.Snapshot(), Is.Empty);
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18002));
        }

        [Test]
        public void PathNormalizationAndPortValidationAreStrict()
        {
            Assert.Throws<IOException>(() => new UPilotPortRegistry(""));
            Assert.Throws<IOException>(() => UPilotPortRegistry.NormalizeProject("relative"));
            Assert.That(UPilotPortRegistry.NormalizeProject(_a + Path.DirectorySeparatorChar),
                Is.EqualTo(UPilotPortRegistry.NormalizeProject(_a)));
            Assert.Throws<InvalidDataException>(() => _registry.Commit(_a, Config(0, 19001)));
            Assert.Throws<InvalidDataException>(() => _registry.Commit(_a, Config(18001, 18001)));
            Assert.That(_registry.Recommend(_a, 18001, 18001, 2), Is.EqualTo((18001, 18002)));
        }

        [Test]
        public void StartupChecksSystemOccupancyEvenForPreviouslySavedPorts()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var occupied = new UPilotPortRegistry(_user, _ => false);
            Assert.DoesNotThrow(() => occupied.Sync(_a));
            Assert.Throws<IOException>(() => occupied.Sync(_a, requireAvailable: true));
        }

        [Test]
        public void ChangingHalfThePairRechecksBothPorts()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var occupiedWs = new UPilotPortRegistry(_user, port => port != 18001);
            Assert.Throws<IOException>(() => occupiedWs.Commit(_a, Config(18001, 19002)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.httpPort, Is.EqualTo(19001));
        }

        [Test]
        public void SavingOtherSettingsPreservesDiskPortsInsteadOfCachedPorts()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var cached = UPilotPortRegistry.ReadConfig(_a);
            DiskConfig(_a, 18002, 19002);
            cached.runtime.serverVersion = "test-version";
            _registry.Commit(_a, cached, preserveExistingPorts: true);
            var disk = UPilotPortRegistry.ReadConfig(_a);
            Assert.That(disk.mcp.wsPort, Is.EqualTo(18002));
            Assert.That(disk.runtime.serverVersion, Is.EqualTo("test-version"));
            Assert.That(_registry.Snapshot().Single().wsPort, Is.EqualTo(18002));
        }

        [Test]
        [Platform("Win")]
        public void LockedConfigPreservesOriginalBytesAndBothReservations()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var path = UPilotPortRegistry.ConfigPath(_a);
            var bytes = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => _registry.Commit(_a, Config(18002, 19002)));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Not.Empty);
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Not.Empty);
            _registry.Sync(_a);
            Assert.That(_registry.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailureLogsContextAndSchedulesOnlyOneMainThreadDialog()
        {
            var original = UPilotPortRegistration.ShowErrorDialog;
            var originalError = UPilotPortRegistration.LastError;
            var mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var shown = 0;
            var shownThread = -1;
            var message = "port-test-" + Guid.NewGuid().ToString("N");
            UPilotPortRegistration.ShowErrorDialog = text =>
            {
                Assert.That(text, Is.EqualTo(message));
                shown++;
                shownThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            };
            try
            {
                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\[UPilotPorts\] operation=test; project=.*config=.*registry=.*error=.*" + message);
                LogAssert.Expect(LogType.Error, pattern);
                UPilotPortRegistration.Report("test", new IOException(message));
                LogAssert.Expect(LogType.Error, pattern);
                UPilotPortRegistration.Report("test", new IOException(message));
                Assert.That(shown, Is.Zero);
                var until = UnityEditor.EditorApplication.timeSinceStartup + 5;
                while (shown == 0 && UnityEditor.EditorApplication.timeSinceStartup < until)
                    yield return null;
                Assert.That(shown, Is.EqualTo(1));
                Assert.That(shownThread, Is.EqualTo(mainThread));
                Assert.That(UPilotPortRegistration.LastError, Is.EqualTo(message));
            }
            finally
            {
                UPilotPortRegistration.ShowErrorDialog = original;
                UPilotPortRegistration.LastError = originalError;
            }
        }

        [Test]
        [Platform("Win")]
        public void CrossProcessLockTimesOutAndRecoversWithoutDeletingLockFile()
        {
            Directory.CreateDirectory(_user);
            var script = "$f=[IO.File]::Open('" + _registry.LockPath.Replace("'", "''") +
                         "',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                         "[Console]::WriteLine('locked');[Console]::ReadLine()|Out-Null;$f.Dispose()";
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardInput = true
            });
            try
            {
                var ready = child.StandardOutput.ReadLineAsync();
                Assert.That(ready.Wait(5000), Is.True);
                Assert.That(ready.Result, Is.EqualTo("locked"));
                var waiting = new UPilotPortRegistry(_user, _ => true, 75);
                Assert.Throws<IOException>(() => waiting.Snapshot());
                Assert.That(File.Exists(_registry.LockPath), Is.True);
            }
            finally
            {
                child.StandardInput.WriteLine("release");
                if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(3000); }
            }
            Assert.DoesNotThrow(() => _registry.Snapshot());
        }
    }
}
