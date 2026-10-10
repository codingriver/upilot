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
        private string _originalError;

        [SetUp]
        public void SetUp()
        {
            _originalError = UPilotPortRegistration.LastError;
            UPilotPortRegistration.LastError = "";
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
            UPilotPortRegistration.LastError = _originalError;
        }

        private static UPilotProjectConfigData Config(int ws, int http) =>
            new UPilotProjectConfigData { mcp = new UPilotMcpConfig { wsPort = ws, httpPort = http } };

        private static void DiskConfig(string project, int ws, int http) =>
            UPilotPortRegistry.AtomicWrite(UPilotPortRegistry.ConfigPath(project), JsonUtility.ToJson(Config(ws, http)));

        [Test]
        public void OfflineReferenceSurvivesRestartWithoutBlockingRecommendation()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var second = new UPilotPortRegistry(_user, _ => true);
            Assert.That(second.Check(_b, 19001, 20001), Does.Contain(UPilotPortRegistry.NormalizeProject(_a)));
            Assert.That(second.Recommend(_b, 18001, 19001, 20), Is.EqualTo((18001, 19001)));
            Assert.That(second.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [Test]
        public void RecommendationKeepsAvailableHttpAcrossExcludedWsRange()
        {
            var probes = 0;
            var registry = new UPilotPortRegistry(_user, port =>
            {
                probes++;
                return !(port >= 8769 && port <= 8868) && !(port >= 8081 && port <= 8180);
            });
            Assert.That(registry.Recommend(_a, 8770, 8017, 100), Is.EqualTo((8869, 8017)));
            Assert.That(probes, Is.LessThanOrEqualTo(200));
            Assert.That(File.Exists(registry.RegistryPath), Is.False, "Recommendation must not reserve ports.");
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_a)), Is.False);
        }

        [Test]
        public void UnbindablePortIsNotMisreportedAsAnOwningProcessOrAutomaticallyReassigned()
        {
            _registry.Commit(_a, Config(8770, 8017));
            var configPath = UPilotPortRegistry.ConfigPath(_a);
            var configBefore = File.ReadAllBytes(configPath);
            var registryBefore = File.ReadAllBytes(_registry.RegistryPath);
            var restricted = new UPilotPortRegistry(_user, port => port < 8769 || port > 8868);

            var error = Assert.Throws<IOException>(() => restricted.Sync(_a, requireAvailable: true));

            Assert.That(error.Message, Does.Contain("端口 8770 当前不可绑定"));
            Assert.That(error.Message, Does.Contain("系统保留/权限限制"));
            Assert.That(error.Message, Does.Not.Contain("已被系统进程占用"));
            Assert.That(restricted.Recommend(_a, 8770, 8017, 100), Is.EqualTo((8869, 8017)));
            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(configBefore));
            Assert.That(File.ReadAllBytes(_registry.RegistryPath), Is.EqualTo(registryBefore));
            // No observed listener is a safe stop no-op even though startup cannot bind.
            Assert.DoesNotThrow(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>(),
                true, Array.Empty<int>(), 8017, 8770));
        }

        [Test]
        public void RecommendationKeepsAvailableWsWhileHttpAdvances()
        {
            var registry = new UPilotPortRegistry(_user, port => port == 18001 || port == 19003);
            Assert.That(registry.Recommend(_a, 18001, 19001, 3), Is.EqualTo((18001, 19003)));
        }

        [TestCase(18000, 18001, 18002, 18001)]
        [TestCase(18001, 18000, 18002, 18001)]
        [TestCase(18000, 18002, 18001, 18002)]
        [TestCase(18002, 18000, 18002, 18001)]
        public void OverlappingSearchRangesDoNotLoseDistinctPair(int ws, int http, int expectedWs, int expectedHttp)
        {
            var registry = new UPilotPortRegistry(_user, port => port == 18001 || port == 18002);
            Assert.That(registry.Recommend(_a, ws, http, 3), Is.EqualTo((expectedWs, expectedHttp)));
        }

        [Test]
        public void OneAvailablePortCannotServeBothProtocols()
        {
            var registry = new UPilotPortRegistry(_user, port => port == 18002);
            Assert.Throws<IOException>(() => registry.Recommend(_a, 18000, 18001, 3));
        }

        [TestCase(65535, 18000, 65535, 18002)]
        [TestCase(18000, 65535, 18002, 65535)]
        [TestCase(65535, 65535, 65535, 65534)]
        public void RecommendationHandlesPortUpperBoundary(int ws, int http, int expectedWs, int expectedHttp)
        {
            var registry = new UPilotPortRegistry(_user, port =>
            {
                Assert.That(port, Is.InRange(1, 65535));
                return port == expectedWs || port == expectedHttp;
            });
            Assert.That(registry.Recommend(_a, ws, http, 3), Is.EqualTo((expectedWs, expectedHttp)));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void RecommendationNeverProbesOutsideAttemptBudget(int attempts)
        {
            var probes = 0;
            var registry = new UPilotPortRegistry(_user, port =>
            {
                probes++;
                return port == 18001 || port == 19003;
            });
            Assert.Throws<IOException>(() => registry.Recommend(_a, 18001, 19001, attempts));
            Assert.That(probes, Is.LessThanOrEqualTo(2 * attempts));
        }

        [Test]
        public void RecommendationIgnoresStaleCrossProtocolReservations()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = File.ReadAllBytes(_registry.RegistryPath);
            var registry = new UPilotPortRegistry(_user, _ => true);
            Assert.That(registry.Recommend(_b, 19001, 18001, 3), Is.EqualTo((19001, 18001)));
            Assert.That(File.ReadAllBytes(_registry.RegistryPath), Is.EqualTo(before));
        }

        [Test]
        public void SetupRecommendationFailurePreservesProblemAndUnderlyingError()
        {
            const string problem = "端口不可监听：WS 8770。";
            const string error = "未找到可监听的 HTTP/WS 端口对";
            var message = UPilotMainWindow.FormatSetupPortMessage(problem, 0, 0, error);
            Assert.That(message, Is.EqualTo(problem + "\n" + error));
            Assert.That(message, Does.Not.Contain("WS 0").And.Not.Contain("HTTP 0"));
        }

        [TestCase(0, 0)]
        [TestCase(8869, 0)]
        [TestCase(8869, 8869)]
        [TestCase(65536, 8017)]
        public void SetupNeverDisplaysInvalidRecommendation(int ws, int http)
        {
            Assert.That(UPilotMainWindow.FormatSetupPortMessage("原始问题", ws, http), Is.EqualTo("原始问题"));
        }

        [Test]
        public void SetupDisplaysValidRecommendationAlongsideOriginalProblem()
        {
            Assert.That(UPilotMainWindow.FormatSetupPortMessage("原始问题", 8869, 8017),
                Is.EqualTo("原始问题\n推荐 WS 8869，HTTP 8017。"));
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
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*参考端口登记"));
            Assert.DoesNotThrow(() => _registry.Commit(_b, Config(19002, 20001)));
            Assert.That(File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a)), Is.EqualTo(bytes));
            Assert.That(UPilotPortRegistry.ReadConfig(_b).mcp.wsPort, Is.EqualTo(19002));
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
        public void CorruptReferenceOnlyWarnsButUnavailablePortsStillBlock()
        {
            Directory.CreateDirectory(_user);
            File.WriteAllText(_registry.RegistryPath, "{}");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*端口登记表损坏"));
            Assert.DoesNotThrow(() => _registry.Commit(_a, Config(18001, 19001)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(File.ReadAllText(_registry.RegistryPath), Is.EqualTo("{}"));
            File.Delete(_registry.RegistryPath);
            File.Delete(UPilotPortRegistry.ConfigPath(_a));
            var occupied = new UPilotPortRegistry(_user, _ => false);
            Assert.Throws<IOException>(() => occupied.Recommend(_a, 18001, 19001, 3));
            Assert.Throws<IOException>(() => occupied.Commit(_a, Config(18001, 19001)));
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_a)), Is.False);
        }

        [Test]
        public void ConcurrentReferenceUpdatesDoNotRejectEitherProjectConfig()
        {
            var first = Task.Run(() => new UPilotPortRegistry(_user, _ => true, 2000).Commit(_a, Config(18001, 19001)));
            var second = Task.Run(() => new UPilotPortRegistry(_user, _ => true, 2000).Commit(_b, Config(18001, 19001)));
            Assert.That(Task.WaitAll(new Task[] { first, second }, 5000), Is.True);
            Assert.That(_registry.Snapshot(), Has.Count.EqualTo(2));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(UPilotPortRegistry.ReadConfig(_b).mcp.wsPort, Is.EqualTo(18001));
        }

        [Test]
        public void FailedAuthoritativeConfigWritePreservesConfigAndReference()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = File.ReadAllBytes(_registry.RegistryPath);
            var failing = new UPilotPortRegistry(_user, _ => true, write: (path, text) =>
            {
                if (Path.GetFileName(path) == "config.json") throw new IOException("injected config write failure");
                UPilotPortRegistry.AtomicWrite(path, text);
            });
            Assert.Throws<IOException>(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(File.ReadAllBytes(_registry.RegistryPath), Is.EqualTo(before));
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Empty);
        }

        [Test]
        public void ReferenceWriteFailureRecoversFromSavedProjectConfig()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = File.ReadAllBytes(_registry.RegistryPath);
            var writes = 0;
            var failing = new UPilotPortRegistry(_user, _ => true, write: (path, text) =>
            {
                if (++writes == 2) throw new IOException("injected registry write failure");
                UPilotPortRegistry.AtomicWrite(path, text);
            });
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*injected registry write failure"));
            Assert.DoesNotThrow(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(writes, Is.EqualTo(2), "Only one best-effort registry write follows the config write.");
            Assert.That(File.ReadAllBytes(_registry.RegistryPath), Is.EqualTo(before));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18002));
            _registry.Sync(_a);
            Assert.That(_registry.Check(_b, 18001, 19001), Is.Empty);
            Assert.That(_registry.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [Test]
        public void RegistryWriteFailureDoesNotPreventFirstProjectSave()
        {
            var failing = new UPilotPortRegistry(_user, _ => true, write: (path, text) =>
            {
                if (Path.GetFileName(path) == "ports.json") throw new UnauthorizedAccessException("injected registry permission failure");
                UPilotPortRegistry.AtomicWrite(path, text);
            });
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*injected registry permission failure"));
            Assert.DoesNotThrow(() => failing.Commit(_a, Config(18001, 19001)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(File.Exists(_registry.RegistryPath), Is.False);
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
        public void LockedConfigPreservesOriginalBytesAndReference()
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
            Assert.That(_registry.Check(_b, 18002, 19002), Is.Empty);
            _registry.Sync(_a);
            Assert.That(_registry.Snapshot().Single().pendingPorts, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ReferenceFailureOnlyWarnsOnceWithoutModalInfrastructure()
        {
            var message = "port-test-" + Guid.NewGuid().ToString("N");
            var warnings = 0;
            Application.LogCallback listener = (text, stack, type) =>
            {
                if (text.Contains(message) && type == LogType.Warning) warnings++;
            };
            Application.logMessageReceived += listener;
            try
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    @"\[UPilotPorts\] operation=test; project=.*registry=.*error=.*" + message));
                UPilotPortRegistration.Report("test", new IOException(message));
                UPilotPortRegistration.Report("test", new IOException(message));
                yield return null;
                yield return null;
                Assert.That(warnings, Is.EqualTo(1));
                Assert.That(UPilotPortRegistration.LastError, Is.EqualTo(message));
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                Assert.That(typeof(UPilotPortRegistration).GetField("ShowErrorDialog", flags), Is.Null);
                Assert.That(typeof(UPilotPortRegistration).GetField("PendingDialogs", flags), Is.Null);
            }
            finally { Application.logMessageReceived -= listener; }
        }

        [UnityTest]
        public IEnumerator ConfirmedConfigurationClearsWarningWithoutSchedulingAnyDialog()
        {
            var message = "confirmed-port-test-" + Guid.NewGuid().ToString("N");
            var pattern = new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*" + message);
            LogAssert.Expect(LogType.Warning, pattern);
            UPilotPortRegistration.Report("test", new IOException(message));
            UPilotPortRegistration.OnEndpointsConfirmed();
            Assert.That(UPilotPortRegistration.LastError, Is.Empty);
            LogAssert.Expect(LogType.Warning, pattern);
            UPilotPortRegistration.Report("test", new IOException(message));
            yield return null;
            Assert.That(UPilotPortRegistration.LastError, Is.EqualTo(message));
        }

        [Test]
        [Platform("Win")]
        public void RealExternalListenerBlocksStartupSyncWithoutChangingConfigOrStoppingOwner()
        {
            const string script =
                "$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);" +
                "$l.Start();[Console]::WriteLine(([Net.IPEndPoint]$l.LocalEndpoint).Port);" +
                "[Console]::Out.Flush();[Console]::ReadLine()|Out-Null;$l.Stop()";
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            });

            try
            {
                var ready = child.StandardOutput.ReadLineAsync();
                Assert.That(ready.Wait(5000), Is.True, "external listener did not publish its port");
                Assert.That(int.TryParse(ready.Result, out int occupiedPort), Is.True, ready.Result);
                int availablePort = Enumerable.Range(30000, 20000)
                    .First(port => port != occupiedPort && UPilotPortAllocator.IsPortAvailable(port));
                DiskConfig(_a, occupiedPort, availablePort);
                string configPath = UPilotPortRegistry.ConfigPath(_a);
                byte[] before = File.ReadAllBytes(configPath);
                var realRegistry = new UPilotPortRegistry(_user, UPilotPortAllocator.IsPortAvailable, 2000);

                var error = Assert.Throws<IOException>(() => realRegistry.Sync(_a, requireAvailable: true));

                Assert.That(error.Message, Does.Contain($"端口 {occupiedPort} 当前不可绑定"));
                Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(before));
                Assert.That(child.HasExited, Is.False, "startup conflict handling terminated the external owner");
                Assert.That(UPilotPortAllocator.IsPortAvailable(occupiedPort), Is.False);
            }
            finally
            {
                if (child != null && !child.HasExited)
                {
                    child.StandardInput.WriteLine("release");
                    if (!child.WaitForExit(3000))
                    {
                        child.Kill();
                        child.WaitForExit(3000);
                    }
                }
            }
        }

        [Test]
        public void BusyReferenceDoesNotBlockSavingSyncOrRecommendation()
        {
            Directory.CreateDirectory(_user);
            var registry = new UPilotPortRegistry(_user, _ => true, 0);
            using (var held = new FileStream(registry.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*端口登记暂时繁忙"));
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Assert.DoesNotThrow(() => registry.Commit(_a, Config(18001, 19001), requireAvailable: true));
                Assert.That(timer.ElapsedMilliseconds, Is.LessThan(500));
                Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
                Assert.DoesNotThrow(() => registry.Sync(_a, requireAvailable: true));
                Assert.That(registry.Recommend(_b, 18001, 19001, 3), Is.EqualTo((18001, 19001)));
                Assert.That(File.Exists(registry.RegistryPath), Is.False);
            }
            Assert.That(File.Exists(registry.LockPath), Is.True);
            registry.Sync(_a);
            Assert.That(registry.Snapshot().Single().wsPort, Is.EqualTo(18001));
        }

        [Test]
        [Platform("Win")]
        public void InaccessibleReferenceLockDoesNotBlockProjectSave()
        {
            Directory.CreateDirectory(_registry.LockPath);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*"));
            Assert.DoesNotThrow(() => _registry.Commit(_a, Config(18001, 19001)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.httpPort, Is.EqualTo(19001));
            Assert.That(Directory.Exists(_registry.LockPath), Is.True);
        }

        [TestCase(32, true)]
        [TestCase(33, true)]
        [TestCase(5, false)]
        [TestCase(3, false)]
        [TestCase(112, false)]
        public void OnlySharingAndLockViolationsAreRetriedAsContention(int code, bool busy)
        {
            Assert.That(UPilotPortRegistry.IsLockContention(new IOException("test", unchecked((int)0x80070000) | code)),
                Is.EqualTo(busy));
        }

        [Test]
        [Platform("Win")]
        public void InaccessibleLockIsNotMisclassifiedAsContentionAndRecovers()
        {
            Directory.CreateDirectory(_registry.LockPath); // Isolated test path, not the user's registry.
            var error = Assert.Throws<UnauthorizedAccessException>(() => _registry.Snapshot());
            Assert.That(error, Is.Not.InstanceOf<UPilotPortRegistryBusyException>());
            Directory.Delete(_registry.LockPath);
            Assert.DoesNotThrow(() => _registry.Snapshot());
        }

        [UnityTest]
        public IEnumerator BusyRegistryAndWrappedBusyErrorOnlyWarn()
        {
            var busy = new UPilotPortRegistryBusyException(_registry.LockPath, new IOException("sharing violation"));
            foreach (var error in new Exception[] { busy, new IOException("wrapped-" + busy.Message, busy) })
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*"));
                UPilotPortRegistration.Report("test-busy", error);
                UPilotPortRegistration.Report("test-busy", error);
                Assert.That(UPilotPortRegistration.LastError, Is.EqualTo(error.Message));
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReferenceStorageFailuresOnlyWarn()
        {
            foreach (var error in new Exception[]
            {
                new IOException("registry-write-failure"),
                new UnauthorizedAccessException("registry-permission-failure"),
                new InvalidDataException("registry-corrupt-data")
            })
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*" + error.Message));
                UPilotPortRegistration.Report("reference-read-write", error);
                Assert.That(UPilotPortRegistration.LastError, Is.EqualTo(error.Message));
            }
            yield return null;
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
                var error = Assert.Throws<UPilotPortRegistryBusyException>(() => waiting.Snapshot());
                Assert.That(error.InnerException, Is.InstanceOf<IOException>());
                Assert.That(error.Message, Does.Contain("不代表端口冲突"));
                Assert.That(File.Exists(_registry.LockPath), Is.True);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[UPilotPorts\].*端口登记暂时繁忙"));
                Assert.DoesNotThrow(() => waiting.Commit(_a, Config(18001, 19001), requireAvailable: true));
                Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
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
