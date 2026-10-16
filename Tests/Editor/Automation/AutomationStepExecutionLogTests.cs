using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationStepExecutionLogTests
    {
        private string _root, _directory;
        private AutomationStepRun _run;
        private AutomationStepExecutionLog _log;
        private long _writeAt;
        private readonly List<string> _diagnostics = new();

        [SetUp] public void SetUp()
        {
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UPilot/StepLogTests/", Guid.NewGuid().ToString("N")));
            _directory = Path.Combine(_root, "Logs", "UPilot");
            Directory.CreateDirectory(_directory);
            _run = new AutomationStepRun { runId = Guid.NewGuid().ToString("N"), operationId = "log-test",
                executionLogTimeZoneId = TimeZoneInfo.Local.Id };
            _diagnostics.Clear();
            _log = new AutomationStepExecutionLog(_root, _diagnostics.Add, () => DateTimeOffset.FromUnixTimeMilliseconds(_writeAt));
        }
        [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        private static long At(int day, int hour = 12, int minute = 0, int second = 0)
        {
            var date = new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Unspecified);
            return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)).ToUnixTimeMilliseconds();
        }
        private string FilePath(int day) => Path.Combine(_directory, "step-2026-10-" + day.ToString("00") + ".log");
        private void Write(long at, string fields = "")
        {
            _writeAt = at;
            _log.Write(_run, at, "test.event", "测试事件", fields);
        }

        [Test] public void DailyFilesAppendAcrossRunsAndMidnightWithUtf8AndLf()
        {
            Write(At(15, 23, 59, 59), "中文🙂");
            string first = _run.runId;
            _run.runId = Guid.NewGuid().ToString("N");
            Write(At(15, 23, 59, 59));
            Write(At(16, 0));
            Assert.That(File.ReadAllLines(FilePath(15)).Length, Is.EqualTo(2));
            Assert.That(File.ReadAllText(FilePath(15)), Does.Contain(first).And.Contain(_run.runId).And.Contain("中文🙂"));
            Assert.That(File.ReadAllText(FilePath(16)), Does.Contain(_run.runId));
            byte[] bytes = File.ReadAllBytes(FilePath(15));
            Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.That(bytes.Contains((byte)'\r'), Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        [Test] public void DelayedEventsUseWriteDateForFileAndRetentionButKeepOriginalTimestamp()
        {
            File.WriteAllText(FilePath(13), "expired\n");
            _writeAt = At(16, 0);
            _log.Write(_run, At(15, 23, 59, 59), "test.delayed", "延迟事件", "");
            Assert.That(File.Exists(FilePath(15)), Is.False);
            Assert.That(File.Exists(FilePath(13)), Is.False);
            Assert.That(File.ReadAllText(FilePath(16)), Does.Contain("2026-10-15T23:59:59.000").And.Contain("test.delayed"));
        }

        [Test] public void CleanupKeepsTodayAndPreviousTwoDatesAndIgnoresOtherNames()
        {
            foreach (int day in new[] { 11, 12, 13, 14, 15, 16 }) File.WriteAllText(FilePath(day), "existing\n");
            foreach (string name in new[] { "upilot.log", "step-2026-02-30.log", "step-2026-10-01.log.1", "step-2026-1-01.log" })
                File.WriteAllText(Path.Combine(_directory, name), "keep");
            Directory.CreateDirectory(Path.Combine(_directory, "step-2026-01-01.log"));
            Write(At(15));
            Assert.That(File.Exists(FilePath(11)), Is.False);
            Assert.That(File.Exists(FilePath(12)), Is.False);
            foreach (int day in new[] { 13, 14, 15, 16 }) Assert.That(File.Exists(FilePath(day)), Is.True);
            Assert.That(Directory.GetFiles(_directory).Length, Is.EqualTo(8));
            Assert.That(Directory.Exists(Path.Combine(_directory, "step-2026-01-01.log")), Is.True);
            Assert.That(_diagnostics, Is.Empty);
        }

        [Test] public void CleanupRunsOncePerDateAndAgainAtMidnight()
        {
            Write(At(15));
            File.WriteAllText(FilePath(12), "late old file");
            Write(At(15, 13));
            Assert.That(File.Exists(FilePath(12)), Is.True);
            Write(At(16, 0));
            Assert.That(File.Exists(FilePath(12)), Is.False);
            Assert.That(File.Exists(FilePath(16)), Is.True);
        }

        [Test] public void FailedDeletionDoesNotPreventOtherDeletionOrCurrentEvent()
        {
            File.WriteAllText(FilePath(11), "locked");
            File.WriteAllText(FilePath(12), "old");
            using (var locked = new FileStream(FilePath(11), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Write(At(15));
                Assert.That(File.Exists(FilePath(11)), Is.True);
                Assert.That(File.Exists(FilePath(12)), Is.False);
                Assert.That(File.ReadAllText(FilePath(15)), Does.Contain("test.event"));
                Assert.That(_diagnostics.Count, Is.EqualTo(1));
            }
            LogAssert.NoUnexpectedReceived();
        }

        [Test] public void FailedWritesAreNotReplayedAndLaterEventsContinueWithDeduplicatedDiagnostics()
        {
            File.WriteAllText(FilePath(15), "prior\n");
            using (var locked = new FileStream(FilePath(15), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Write(At(15), "lost-one");
                Write(At(15), "lost-two");
                // The injected observer sees every file fault; the production Logger aggregates output.
                _log = new AutomationStepExecutionLog(_root, _diagnostics.Add, () => DateTimeOffset.FromUnixTimeMilliseconds(_writeAt));
                Write(At(15), "lost-three");
                Assert.That(_diagnostics.Count, Is.EqualTo(3));
            }
            Write(At(15), "later");
            Assert.That(File.ReadAllText(FilePath(15)), Does.Contain("later").And.Not.Contain("lost-"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test] public void LongArgumentsRemainCompleteAndReversiblyEscapedOnOneLine()
        {
            string argument = new string('中', 90000) + "\r\n\t\"\\🙂";
            var plan = new AutomationStepPlan { steps = new[] { new AutomationStepItem { instanceId = "one", stepId = "test", arguments = argument } } };
            string json = JsonUtility.ToJson(plan);
            Write(At(15), "计划=" + json);
            string line = File.ReadAllLines(FilePath(15)).Single();
            var restored = JsonUtility.FromJson<AutomationStepPlan>(line.Substring(line.IndexOf("计划=", StringComparison.Ordinal) + 3));
            Assert.That(restored.steps[0].arguments, Is.EqualTo(argument));
            Assert.That(line, Does.Not.Contain("[truncated"));
            Assert.That(AutomationStepExecutionLog.Quote("\u0001\r\n\t\"\\"), Is.EqualTo("\"\\u0001\\r\\n\\t\\\"\\\\\""));
        }

        [Test] public void PartialTailIsPreservedAndNewEventStartsOnANewLine()
        {
            File.WriteAllText(FilePath(15), "partial tail", new UTF8Encoding(false));
            Write(At(15));
            var lines = File.ReadAllLines(FilePath(15));
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(lines[0], Is.EqualTo("partial tail"));
            Assert.That(lines[1], Does.StartWith("["));
        }

        [Test] public void MissingAndBackwardsTimeNeverBecomeZeroDurations()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
                Assert.That(AutomationStepExecutionLog.Elapsed(100, 1234), Is.EqualTo("1.134s"));
                Assert.That(AutomationStepExecutionLog.Elapsed(0, 1234), Is.EqualTo("未知"));
                Assert.That(AutomationStepExecutionLog.Elapsed(1000, 999), Is.EqualTo("未知"));
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        [Test] public void FileOnlyDiagnosticsReportWriteFailuresAsErrors()
        {
            string path = Path.Combine(_directory, "diagnostic.log");
            Assert.That(Logger.TryAppendFileOnly("diagnostic", path), Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain("diagnostic"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("日志文件异常 operation=write"));
            Assert.That(Logger.TryAppendFileOnly("cannot append to directory", _directory), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
