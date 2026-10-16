using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPortRegistryTests
    {
        private string _root;
        private string _key;
        private string _otherKey;
        private string _a;
        private string _b;
        private UPilotPortRegistry _registry;
        private string _originalError;
        private string _originalWarning;

        [SetUp]
        public void SetUp()
        {
            _originalError = UPilotPortRegistration.LastError;
            _originalWarning = UPilotPortRegistration.LastWarning;
            UPilotPortRegistration.LastError = "";
            UPilotPortRegistration.LastWarning = "";
            _root = Path.Combine(Path.GetTempPath(), "UPilot-port-tests-" + Guid.NewGuid().ToString("N"));
            _key = "upilot.port-tests." + Guid.NewGuid().ToString("N");
            _otherKey = _key + ".other";
            _a = Path.Combine(_root, "project-a");
            _b = Path.Combine(_root, "project-b");
            _registry = new UPilotPortRegistry(_key, _ => true);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(_key);
            EditorPrefs.DeleteKey(_otherKey);
            var prefix = Path.Combine(Path.GetTempPath(), "UPilot-port-tests-");
            Assert.That(Path.GetFullPath(_root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), Is.True);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            UPilotPortRegistration.LastError = _originalError;
            UPilotPortRegistration.LastWarning = _originalWarning;
        }

        private static UPilotProjectConfigData Config(int ws, int http) =>
            new UPilotProjectConfigData { mcp = new UPilotMcpConfig { wsPort = ws, httpPort = http } };

        private static void DiskConfig(string project, int ws, int http) =>
            UPilotPortRegistry.AtomicWrite(UPilotPortRegistry.ConfigPath(project), JsonUtility.ToJson(Config(ws, http)));

        private static string Entry(string project, int ws, int http) =>
            UPilotBridge.ComputeProjectPathHash(project) + ":" + ws + ":" + http;

        private static void ExpectWarning(string message) =>
            LogAssert.Expect(LogType.Warning, new Regex(@"\[UPilotPorts\] operation=.*registry=upilot_allocate_ports; error=.*" + Regex.Escape(message)));

        [Test]
        public void GlobalRegistrationIsNotPartOfCurrentProjectReset()
        {
            Assert.That(UPilotPortRegistry.PrefsKey, Is.EqualTo("upilot_allocate_ports"));
            Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Not.Contain(UPilotPortRegistry.PrefsKey));
        }

        [Test]
        public void HashMatchesExistingProjectPreferenceSuffixAndNormalizedPath()
        {
            Assert.That(UPilotBridge.ComputeProjectPathHash(UPilotProjectConfig.ProjectRoot),
                Is.EqualTo(UPilotBridge.WsEndpointEditorPrefsKeySuffix));
            Assert.That(UPilotBridge.ComputeProjectPathHash("D:\\Example\\Project\\"),
                Is.EqualTo(UPilotBridge.ComputeProjectPathHash("d:/example/project")));
            Assert.That(UPilotBridge.ComputeProjectPathHash(_a), Does.Match("^[0-9a-f]{16}$"));
        }

        [Test]
        public void MissingKeyRegistersFirstProjectAndNewInstanceReadsIt()
        {
            Assert.That(EditorPrefs.HasKey(_key), Is.False);
            _registry.Commit(_a, Config(18001, 19001));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18001, 19001)));
            new UPilotPortRegistry(_key, _ => true).Commit(_b, Config(18002, 19002));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18001, 19001) + "\n" + Entry(_b, 18002, 19002)));
        }

        [Test]
        public void UpdateReplacesCurrentDuplicatesAndPreservesOtherRecords()
        {
            DiskConfig(_a, 18003, 19003);
            EditorPrefs.SetString(_key, Entry(_b, 18002, 19002) + "\n" + Entry(_a, 18001, 19001) + "\n" + Entry(_a, 18004, 19004));
            _registry.Sync(_a);
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_b, 18002, 19002) + "\n" + Entry(_a, 18003, 19003)));
        }

        [Test]
        public void EveryUpdateReadsLatestValueInsteadOfCachedEntries()
        {
            _registry.Commit(_a, Config(18001, 19001));
            EditorPrefs.SetString(_key, Entry(_b, 18002, 19002));
            _registry.Sync(_a);
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_b, 18002, 19002) + "\n" + Entry(_a, 18001, 19001)));
        }

        [Test]
        public void UnchangedRegistrationDoesNotWriteAgain()
        {
            var reads = 0;
            var writes = 0;
            var registry = new UPilotPortRegistry(_key, _ => true,
                readRegistration: key => { reads++; return EditorPrefs.GetString(key, ""); },
                writeRegistration: (key, value) => { writes++; EditorPrefs.SetString(key, value); });
            registry.Commit(_a, Config(18001, 19001));
            registry.Sync(_a);
            registry.Sync(_a);
            Assert.That(reads, Is.EqualTo(3));
            Assert.That(writes, Is.EqualTo(1));
        }

        [Test]
        public void ProjectConfigWinsWithoutRewritingOtherProjectsOrRejectingReferenceDuplicates()
        {
            _registry.Commit(_a, Config(18001, 19001));
            DiskConfig(_a, 18002, 19002);
            _registry.Sync(_a);
            var bytes = File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a));
            Assert.DoesNotThrow(() => _registry.Commit(_b, Config(19002, 20001)));
            Assert.That(File.ReadAllBytes(UPilotPortRegistry.ConfigPath(_a)), Is.EqualTo(bytes));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18002, 19002) + "\n" + Entry(_b, 19002, 20001)));
        }

        [TestCase("")]
        [TestCase("{}")]
        [TestCase("{\"mcp\":{}}")]
        [TestCase("{\"mcp\":null}")]
        [TestCase("{\"mcp\":{\"wsPort\":18001}}")]
        public void InvalidConfigDoesNotWriteRegistration(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UPilotPortRegistry.ConfigPath(_a)));
            File.WriteAllText(UPilotPortRegistry.ConfigPath(_a), json);
            Assert.Catch(() => _registry.Sync(_a));
            Assert.That(EditorPrefs.HasKey(_key), Is.False);
        }

        [Test]
        public void MissingConfigDoesNotRemoveExistingReference()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = EditorPrefs.GetString(_key);
            File.Delete(UPilotPortRegistry.ConfigPath(_a));
            Assert.Throws<IOException>(() => _registry.Sync(_a));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(before));
        }

        [Test]
        public void FailedAuthoritativeConfigWritePreservesConfigAndReference()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = EditorPrefs.GetString(_key);
            var failing = new UPilotPortRegistry(_key, _ => true,
                write: (path, text) => throw new IOException("injected config write failure"));
            Assert.Throws<IOException>(() => failing.Commit(_a, Config(18002, 19002)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(before));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RegistrationReadOrWriteFailureWarnsOnceWithoutBlockingConfig(bool failRead)
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = EditorPrefs.GetString(_key);
            const string message = "injected prefs storage failure";
            var reads = 0;
            var writes = 0;
            var failing = new UPilotPortRegistry(_key, _ => true,
                readRegistration: key =>
                {
                    reads++;
                    if (failRead) throw new IOException(message);
                    return EditorPrefs.GetString(key, "");
                },
                writeRegistration: (key, value) => { writes++; throw new UnauthorizedAccessException(message); });
            UPilotPortRegistration.LastError = "real config diagnostic";
            ExpectWarning(message);
            Assert.DoesNotThrow(() => failing.Commit(_a, Config(18002, 19002)));
            UPilotPortRegistration.OnEndpointsConfirmed();
            Assert.DoesNotThrow(() => failing.Sync(_a));
            Assert.That(reads, Is.EqualTo(2));
            Assert.That(writes, Is.EqualTo(failRead ? 0 : 2), "One attempt per caller; no automatic retry.");
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(before));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18002));
            Assert.That(UPilotPortRegistration.LastError, Is.Empty);
            _registry.Sync(_a);
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18002, 19002)));
            Assert.That(UPilotPortRegistration.LastWarning, Is.Empty);
        }

        [Test]
        public void RegistrationFailureDoesNotPreventFirstProjectSave()
        {
            var failing = new UPilotPortRegistry(_key, _ => true,
                writeRegistration: (key, value) => throw new IOException("injected first registration failure"));
            ExpectWarning("injected first registration failure");
            Assert.DoesNotThrow(() => failing.Commit(_a, Config(18001, 19001)));
            Assert.That(UPilotPortRegistry.ReadConfig(_a).mcp.wsPort, Is.EqualTo(18001));
            Assert.That(EditorPrefs.HasKey(_key), Is.False);
        }

        [Test]
        public void ReferenceWarningDoesNotReplaceRealConfigurationErrorOrScheduleDialog()
        {
            UPilotPortRegistration.LastError = "actual config error";
            ExpectWarning("reference failure");
            UPilotPortRegistration.Report("test", new IOException("reference failure"));
            UPilotPortRegistration.Report("test", new IOException("reference failure"));
            Assert.That(UPilotPortRegistration.LastError, Is.EqualTo("actual config error"));
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            Assert.That(typeof(UPilotPortRegistration).GetField("ShowErrorDialog", flags), Is.Null);
            Assert.That(typeof(UPilotPortRegistration).GetField("PendingDialogs", flags), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PrintUsesOneMultilineLogWithoutChangingData(bool populated)
        {
            var value = Entry(_a, 18001, 19001) + "\n" + Entry(_b, 18002, 19002);
            if (populated) EditorPrefs.SetString(_key, value);
            LogAssert.Expect(LogType.Log, "[UPilotPorts] " + _key + "\n" + (populated ? value : "暂无端口登记"));
            _registry.Print();
            Assert.That(EditorPrefs.HasKey(_key), Is.EqualTo(populated));
            Assert.That(EditorPrefs.GetString(_key, ""), Is.EqualTo(populated ? value : ""));
        }

        [Test]
        public void PrintReadFailureOnlyWarns()
        {
            var registry = new UPilotPortRegistry(_key, readRegistration: key => throw new IOException("injected print failure"));
            ExpectWarning("injected print failure");
            Assert.DoesNotThrow(() => registry.Print());
            Assert.That(EditorPrefs.HasKey(_key), Is.False);
            Assert.That(UPilotPortRegistration.LastError, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClearDeletesOnlyReferenceKeyAndNormalSyncCanRegisterAgain(bool populated)
        {
            DiskConfig(_a, 18001, 19001);
            var path = UPilotPortRegistry.ConfigPath(_a);
            var before = File.ReadAllBytes(path);
            if (populated) EditorPrefs.SetString(_key, Entry(_a, 18001, 19001));
            EditorPrefs.SetString(_otherKey, "preserve");
            var manager = UPilotMcpServerManager.Instance;
            var ws = manager.WsPort;
            var http = manager.HttpPort;
            var started = manager.GetStatus().IsRunning;
            LogAssert.Expect(LogType.Log, "[UPilotPorts] 已清除参考端口登记：" + _key + "；工程配置和运行中的服务不变。");
            _registry.Clear();
            Assert.That(EditorPrefs.HasKey(_key), Is.False);
            Assert.That(EditorPrefs.GetString(_otherKey), Is.EqualTo("preserve"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(manager.WsPort, Is.EqualTo(ws));
            Assert.That(manager.HttpPort, Is.EqualTo(http));
            Assert.That(manager.GetStatus().IsRunning, Is.EqualTo(started));
            _registry.Sync(_a);
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18001, 19001)));
        }

        [Test]
        public void RecommendationKeepsAvailableHttpAcrossExcludedWsRange()
        {
            var probes = 0;
            var registry = new UPilotPortRegistry(_key, port =>
            {
                probes++;
                return !(port >= 8769 && port <= 8868) && !(port >= 8081 && port <= 8180);
            });
            Assert.That(registry.Recommend(_a, 8770, 8017, 100), Is.EqualTo((8869, 8017)));
            Assert.That(probes, Is.LessThanOrEqualTo(200));
            Assert.That(EditorPrefs.HasKey(_key), Is.False, "Recommendation must not reserve ports.");
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_a)), Is.False);
        }

        [Test]
        public void UnbindablePortIsNotMisreportedAsAnOwningProcessOrAutomaticallyReassigned()
        {
            _registry.Commit(_a, Config(8770, 8017));
            var configPath = UPilotPortRegistry.ConfigPath(_a);
            var configBefore = File.ReadAllBytes(configPath);
            var registryBefore = EditorPrefs.GetString(_key, "");
            var restricted = new UPilotPortRegistry(_key, port => port < 8769 || port > 8868);

            var error = Assert.Throws<IOException>(() => restricted.Sync(_a, requireAvailable: true));

            Assert.That(error.Message, Does.Contain("端口 8770 当前不可绑定"));
            Assert.That(error.Message, Does.Contain("系统保留/权限限制"));
            Assert.That(error.Message, Does.Not.Contain("已被系统进程占用"));
            Assert.That(restricted.Recommend(_a, 8770, 8017, 100), Is.EqualTo((8869, 8018)));
            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(configBefore));
            Assert.That(EditorPrefs.GetString(_key, ""), Is.EqualTo(registryBefore));
            // No observed listener is a safe stop no-op even though startup cannot bind.
            Assert.DoesNotThrow(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>(),
                true, Array.Empty<int>(), 8017, 8770));
        }

        [Test]
        public void RecommendationKeepsAvailableWsWhileHttpAdvances()
        {
            var registry = new UPilotPortRegistry(_key, port => port == 18001 || port == 19003);
            Assert.That(registry.Recommend(_a, 18001, 19001, 3), Is.EqualTo((18001, 19003)));
        }

        [TestCase(18000, 18001, 18002, 18001)]
        [TestCase(18001, 18000, 18002, 18001)]
        [TestCase(18000, 18002, 18001, 18002)]
        [TestCase(18002, 18000, 18002, 18001)]
        public void OverlappingSearchRangesDoNotLoseDistinctPair(int ws, int http, int expectedWs, int expectedHttp)
        {
            var registry = new UPilotPortRegistry(_key, port => port == 18001 || port == 18002);
            Assert.That(registry.Recommend(_a, ws, http, 3), Is.EqualTo((expectedWs, expectedHttp)));
        }

        [Test]
        public void OneAvailablePortCannotServeBothProtocols()
        {
            var registry = new UPilotPortRegistry(_key, port => port == 18002);
            Assert.Throws<IOException>(() => registry.Recommend(_a, 18000, 18001, 3));
        }

        [TestCase(65535, 18000, 65535, 18002)]
        [TestCase(18000, 65535, 18002, 65535)]
        [TestCase(65535, 65535, 65535, 65534)]
        public void RecommendationHandlesPortUpperBoundary(int ws, int http, int expectedWs, int expectedHttp)
        {
            var registry = new UPilotPortRegistry(_key, port =>
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
            var registry = new UPilotPortRegistry(_key, port =>
            {
                probes++;
                return port == 18001 || port == 19003;
            });
            Assert.Throws<IOException>(() => registry.Recommend(_a, 18001, 19001, attempts));
            Assert.That(probes, Is.LessThanOrEqualTo(2 * attempts));
        }

        [Test]
        public void RecommendationSkipsStaleCrossProtocolReservationsIncludingCurrentProject()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var before = EditorPrefs.GetString(_key, "");
            var registry = new UPilotPortRegistry(_key, _ => true);
            Assert.That(registry.Recommend(_b, 19001, 18001, 3), Is.EqualTo((19002, 18002)));
            Assert.That(registry.Recommend(_a, 18001, 19001, 3), Is.EqualTo((18002, 19002)));
            Assert.That(EditorPrefs.GetString(_key, ""), Is.EqualTo(before));
        }

        [Test]
        public void RecommendationReadsLatestRegistrationAndExcludesHistoryBeforeBindProbes()
        {
            var probes = new System.Collections.Generic.List<int>();
            var registry = new UPilotPortRegistry(_key, port => { probes.Add(port); return true; });
            Assert.That(registry.Recommend(_a, 18001, 19001, 4), Is.EqualTo((18001, 19001)));
            EditorPrefs.SetString(_key, Entry(_b, 18001, 19001));
            probes.Clear();
            Assert.That(registry.Recommend(_a, 18001, 19001, 4, new[] { 18002, 19002 }),
                Is.EqualTo((18003, 19003)));
            Assert.That(probes, Has.None.EqualTo(18001).And.None.EqualTo(19001));
            Assert.That(probes, Has.None.EqualTo(18002).And.None.EqualTo(19002));
        }

        [TestCase("broken")]
        [TestCase("hash:not-a-port:19001")]
        [TestCase("hash:18001:18001")]
        [TestCase("hash:0:19001")]
        [TestCase("hash:18001:65536")]
        public void CorruptRegistrationRefusesRecommendationWithoutProbingOrWriting(string registration)
        {
            EditorPrefs.SetString(_key, registration);
            var registry = new UPilotPortRegistry(_key, _ => { Assert.Fail("Do not probe unverifiable recommendations."); return true; });
            Assert.Throws<InvalidDataException>(() => registry.Recommend(_a, 18001, 19001, 100));
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(registration));
            Assert.That(File.Exists(UPilotPortRegistry.ConfigPath(_a)), Is.False);
        }

        [Test]
        public void RecommendationReadFailurePreservesOriginalException()
        {
            var failure = new IOException("injected registration read failure");
            var registry = new UPilotPortRegistry(_key, readRegistration: _ => throw failure);
            Assert.That(Assert.Throws<IOException>(() => registry.Recommend(_a, 18001, 19001, 100)), Is.SameAs(failure));
        }

        [Test]
        public void RecommendationWrapsWithinBudgetAndKeepsProtocolsDistinct()
        {
            var registry = new UPilotPortRegistry(_key, _ => true);
            // HTTP reaches 1 first; the second WS candidate preserves a distinct pair.
            Assert.That(registry.Recommend(_a, 65535, 65534, 3, new[] { 65535, 65534 }), Is.EqualTo((2, 1)));
            var error = Assert.Throws<IOException>(() => registry.Recommend(_a, 65535, 65534, 1, new[] { 65535, 65534 }));
            Assert.That(error.Message, Does.Contain("本次搜索范围未找到可用端口"));
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
        public void PathNormalizationAndPortValidationAreStrict()
        {
            Assert.Throws<ArgumentException>(() => new UPilotPortRegistry(""));
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
            var occupied = new UPilotPortRegistry(_key, _ => false);
            Assert.DoesNotThrow(() => occupied.Sync(_a));
            Assert.Throws<IOException>(() => occupied.Sync(_a, requireAvailable: true));
        }

        [Test]
        public void ChangingHalfThePairRechecksBothPorts()
        {
            _registry.Commit(_a, Config(18001, 19001));
            var occupiedWs = new UPilotPortRegistry(_key, port => port != 18001);
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
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18002, 19002)));
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
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18001, 19001)));
            Assert.That(EditorPrefs.GetString(_key), Does.Not.Contain(":18002:19002"));
            _registry.Sync(_a);
            Assert.That(EditorPrefs.GetString(_key), Is.EqualTo(Entry(_a, 18001, 19001)));
        }

    }
}
