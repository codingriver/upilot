using System;
using System.IO;
using System.Text.RegularExpressions;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotDailyLogTests
    {
        private string _root;
        private UPilotLoggingConfig _original;
        private readonly DateTime _today = new(2026, 10, 15);

        [SetUp] public void SetUp()
        {
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UPilot/DailyLogTests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
            _original = UPilotProjectConfig.Current.logging;
            UPilotProjectConfig.Current.logging = new UPilotLoggingConfig();
        }

        [TearDown] public void TearDown()
        {
            UPilotProjectConfig.Current.logging = _original;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestCase("{}", 3)] [TestCase("{\"logging\":null}", 3)]
        [TestCase("{\"logging\":{}}", 3)]
        [TestCase("{\"logging\":{\"retentionDays\":1}}", 1)]
        [TestCase("{\"logging\":{\"retentionDays\":365}}", 365)]
        [TestCase("{\"logging\":{\"retentionDays\":0}}", 3)]
        [TestCase("{\"logging\":{\"retentionDays\":-1}}", 3)]
        [TestCase("{\"logging\":{\"retentionDays\":366}}", 3)]
        public void ConfigurationUsesSharedDefaultAndValidRange(string json, int expected) =>
            Assert.That(UPilotProjectConfig.Parse(json).logging.retentionDays, Is.EqualTo(expected));

        [Test] public void CoreAndStepUseSameSettingAndRecleanWhenChangedWithinDay()
        {
            var directory = Path.Combine(_root, "Logs", "UPilot");
            Directory.CreateDirectory(directory);
            var core = new UPilotDailyLogFile(Path.Combine(directory, "upilot.log"));
            var at = new DateTimeOffset(_today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(_today));
            var run = new AutomationStepRun { runId = "shared", operationId = "shared", executionLogTimeZoneId = TimeZoneInfo.Local.Id };
            var step = new AutomationStepExecutionLog(_root, writeClock: () => at);
            core.Append("first\n", _today);
            step.Write(run, at.ToUnixTimeMilliseconds(), "test", "测试", "first");
            string oldCore = Path.Combine(directory, "upilot-2026-10-14.log"), oldStep = Path.Combine(directory, "step-2026-10-14.log");
            File.WriteAllText(oldCore, "old"); File.WriteAllText(oldStep, "old");
            UPilotProjectConfig.Current.logging.retentionDays = 1;
            core.Append("second\n", _today);
            step.Write(run, at.ToUnixTimeMilliseconds(), "test", "测试", "second");
            Assert.That(File.Exists(oldCore), Is.False);
            Assert.That(File.Exists(oldStep), Is.False);
            Assert.That(File.ReadAllText(core.PathFor(_today)), Is.EqualTo("first\nsecond\n"));
        }

        [Test] public void DailyCleanupPreservesLegacyUnknownFutureDirectoriesAndOtherLogKinds()
        {
            var log = new UPilotDailyLogFile(Path.Combine(_root, "upilot.log"));
            foreach (int day in new[] { 11, 12, 13, 14, 15, 16 })
                File.WriteAllText(log.PathFor(new DateTime(2026, 10, day)), "prior\n");
            var protectedNames = new[] { "upilot.log", "upilot.log.1", "upilot.log.2", "upilot-2026-02-30.log",
                "upilot-2026-1-01.log", "upilot-2026-10-01.log.1", "step-2026-10-01.log", "report.json" };
            foreach (var name in protectedNames) File.WriteAllText(Path.Combine(_root, name), "keep");
            var directory = Path.Combine(_root, "upilot-2026-01-01.log"); Directory.CreateDirectory(directory);
            Assert.That(log.Append("current\n", _today), Is.True);
            Assert.That(File.Exists(log.PathFor(_today.AddDays(-3))), Is.False);
            foreach (int offset in new[] { -2, -1, 0, 1 }) Assert.That(File.Exists(log.PathFor(_today.AddDays(offset))), Is.True);
            foreach (var name in protectedNames) Assert.That(File.ReadAllText(Path.Combine(_root, name)), Is.EqualTo("keep"));
            Assert.That(Directory.Exists(directory), Is.True);
            string late = log.PathFor(_today.AddDays(-5)); File.WriteAllText(late, "late");
            log.Append("same day\n", _today);
            Assert.That(File.Exists(late), Is.True);
            log.Append("midnight\n", _today.AddDays(1));
            Assert.That(File.Exists(late), Is.False);
            Assert.That(File.ReadAllText(log.PathFor(_today.AddDays(1))), Does.EndWith("midnight\n"));
        }

        [Test] public void LargeRecordsAndNewWriterAppendWithoutRotationOrTruncation()
        {
            string basePath = Path.Combine(_root, "upilot.log");
            var writer = new UPilotDailyLogFile(basePath);
            string path = writer.PathFor(_today);
            File.WriteAllText(path, "partial", new System.Text.UTF8Encoding(false));
            string payload = new string('中', 4 * 1024 * 1024) + "\n🙂\n";
            Assert.That(writer.Append(payload, _today), Is.True);
            Assert.That(new UPilotDailyLogFile(basePath).Append("after reload\n", _today), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("partial\n" + payload + "after reload\n"));
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(10 * 1024 * 1024));
            Assert.That(File.Exists(basePath), Is.False);
            Assert.That(File.Exists(basePath + ".1"), Is.False);
        }

        private static string ReadActiveLog()
        {
            using var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [Test] public void CoreLoggerPersistsFullEntryWhileConsoleCanRemainBounded()
        {
            string marker = Guid.NewGuid().ToString("N"), payload = new string('中', 90000) + "\n🙂";
            Logger.AppendLine(marker + payload);
            Assert.That(ReadActiveLog(), Does.Contain(marker + payload));
        }

        [Test] public void ResolveTodayLatestWithoutLegacyFallbackOrCachingAcrossMidnight()
        {
            string basePath = Path.Combine(_root, "mcp-server.log");
            var writer = new UPilotDailyLogFile(basePath);
            File.WriteAllText(basePath, "legacy");
            Assert.That(UPilotDailyLogFile.ResolveExisting(basePath, _today), Is.EqualTo(writer.PathFor(_today)));
            Assert.That(File.ReadAllText(basePath), Is.EqualTo("legacy"));
            string old = writer.PathFor(_today.AddDays(-1)); File.WriteAllText(old, "latest");
            Assert.That(UPilotDailyLogFile.ResolveExisting(basePath, _today), Is.EqualTo(old));
            string current = writer.PathFor(_today); File.WriteAllText(current, "today");
            Assert.That(UPilotDailyLogFile.ResolveExisting(basePath, _today), Is.EqualTo(current));
            string next = writer.PathFor(_today.AddDays(1)); File.WriteAllText(next, "next");
            Assert.That(UPilotDailyLogFile.ResolveExisting(basePath, _today.AddDays(1)), Is.EqualTo(next));
        }

        [Test] public void ServerIdentityRequiresUnifiedLogicalPathAndRejectsLegacyDatedOrForeignPath()
        {
            var spec = new ServerLaunchSpec { projectPath = _root, httpPort = 18011, wsPort = 18765 };
            string prefix = "--http-port 18011 --port 18765 --log-file ";
            string stable = UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, _root);
            spec.arguments = prefix + "\"" + stable + "\"";
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, prefix + "\"" + Path.Combine(_root, "log", "mcp-server.log") + "\""), Is.False);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, prefix + "\"" + stable + "\""), Is.True);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, prefix + "\"" + new UPilotDailyLogFile(stable).PathFor(_today) + "\""), Is.False);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, prefix + "\"" + Path.Combine(_root, "other", "log", "mcp-server.log") + "\""), Is.False);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, prefix + "\"" + stable + "\" --log-file \"" + stable + "\""), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void BridgePayloadReachesPersistentLogWithoutUpstreamTruncation(bool inbound)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var bridge = (UPilotBridge)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            var enabled = typeof(UPilotBridge).GetField("_debugWireLogsEnabled", flags);
            bool before = (bool)enabled.GetValue(bridge);
            var loggerWire = typeof(Logger).GetField("_debugWireLogsEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var loggerVerbose = typeof(Logger).GetField("_verboseLogsEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            bool wireBefore = (bool)loggerWire.GetValue(null), verboseBefore = (bool)loggerVerbose.GetValue(null);
            var console = Logger.LogToUnityConsole;
            var level = Logger.MinLevel;
            try
            {
                enabled.SetValue(bridge, true);
                Logger.SetRuntimeLogOptions(true, false);
                Logger.LogToUnityConsole = false;
                Logger.MinLevel = Logger.LogLevel.Info;
                string text = Guid.NewGuid().ToString("N") + new string('中', 90000) + "🙂";
                string json = "{\"type\":\"event\",\"name\":\"fixture.full-log\",\"id\":\"full\",\"payload\":{\"text\":\"" + text + "\"}}";
                var method = typeof(UPilotBridge).GetMethod(inbound ? "LogInboundCommand" : "LogOutboundCommand", flags);
                if (inbound)
                    method.Invoke(bridge, new[] { JsonUtility.FromJson(json, method.GetParameters()[0].ParameterType), json });
                else method.Invoke(bridge, new object[] { json });
                Assert.That(ReadActiveLog(), Does.Contain(text));
            }
            finally
            {
                enabled.SetValue(bridge, before);
                Logger.SetRuntimeLogOptions(wireBefore, verboseBefore);
                Logger.LogToUnityConsole = console;
                Logger.MinLevel = level;
            }
        }

        [Test] public void WriteAndCleanupFailuresAreVisibleAndDoNotEscapeOrReplay()
        {
            var writer = new UPilotDailyLogFile(Path.Combine(_root, "upilot.log"));
            string expired = writer.PathFor(_today.AddDays(-3)); File.WriteAllText(expired, "locked");
            using (var held = new FileStream(expired, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                LogAssert.Expect(LogType.Error, new Regex("日志文件异常 operation=cleanup.*", RegexOptions.Singleline));
                Assert.That(writer.Append("current\n", _today), Is.True);
            }
            string current = writer.PathFor(_today);
            using (var held = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                LogAssert.Expect(LogType.Error, new Regex("日志文件异常 operation=write.*", RegexOptions.Singleline));
                Assert.That(writer.Append("lost\n", _today), Is.False);
            }
            writer.Append("later\n", _today);
            Assert.That(File.ReadAllText(current), Is.EqualTo("current\nlater\n"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test] public void StepDefaultFailureSinkIsAnErrorWithRunPathAndOriginalException()
        {
            string directory = Path.Combine(_root, "Logs"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "UPilot"), "blocked");
            var run = new AutomationStepRun { runId = "visible-" + Guid.NewGuid().ToString("N"), operationId = "visible-op", executionLogTimeZoneId = TimeZoneInfo.Local.Id };
            string error = null;
            Application.LogCallback observe = (message, stack, type) =>
            {
                if (type == LogType.Error && message.Contains(run.runId)) error = message;
            };
            Application.logMessageReceived += observe;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(run.runId)));
                Assert.DoesNotThrow(() => new AutomationStepExecutionLog(_root).Write(run, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "test", "测试", ""));
                Assert.That(error, Does.Contain("operationId=\"visible-op\""));
                Assert.That(error, Does.Contain("operation=write path=" + Path.Combine(directory, "UPilot", "step-")));
                Assert.That(error, Does.Contain("System.IO.IOException:"));
                Assert.That(error, Does.Contain("UPilotDailyLogFile.Append"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= observe; }
        }
    }
}
