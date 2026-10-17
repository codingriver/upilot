using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotDiagnosticLoggingTests
    {
        private static string ReadGeneralLog()
        {
            using var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ServiceWarningsBypassConsoleAndMinimumLevelAndScopeProvenance(bool console)
        {
            var priorConsole = Logger.LogToUnityConsole;
            var priorLevel = Logger.MinLevel;
            var scope = Guid.NewGuid().ToString("N");
            var observed = new List<string>();
            Application.LogCallback callback = (message, stack, type) => {
                if (message.Contains(scope)) observed.Add(Logger.CaptureDiagnosticSource(message, type));
            };
            try
            {
                Logger.LogToUnityConsole = console; Logger.MinLevel = Logger.LogLevel.Error;
                Application.logMessageReceivedThreaded += callback;
                LogAssert.Expect(LogType.Warning, new Regex(scope));
                Logger.ReportServiceObservation("SERVER", scope, scope: scope);
                Logger.ReportServiceObservation("SERVER", scope, scope: scope);
                LogAssert.Expect(LogType.Warning, new Regex("count=1"));
                Logger.CloseDiagnostics(scope);
                LogAssert.Expect(LogType.Warning, new Regex(scope));
                Debug.LogWarning(scope); // A matching body outside the scoped emission has no provenance.
                Assert.That(observed, Is.EqualTo(new[] { Logger.ServiceObservationSource, Logger.ServiceObservationSource, "" }));
                Assert.That(Logger.CaptureDiagnosticSource(scope, LogType.Warning), Is.Empty);
            }
            finally
            {
                Application.logMessageReceivedThreaded -= callback;
                Logger.CloseDiagnostics(scope); Logger.LogToUnityConsole = priorConsole; Logger.MinLevel = priorLevel;
            }
        }

        [Test]
        public void NestedIdenticalAndOtherThreadLogsCannotConsumeObservationProvenance()
        {
            var scope = "nested-observation-" + Guid.NewGuid().ToString("N");
            var sources = new List<string>();
            var nested = false;
            var callbackEntry = typeof(Application).GetMethod("CallLogCallback",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(callbackEntry, Is.Not.Null, "The Unity Console callback boundary must be observable.");
            Application.LogCallback reenter = (message, stack, type) => {
                if (!message.Contains(scope) || nested) return;
                nested = true;
                Assert.That(System.Threading.Tasks.Task.Run(() => Logger.CaptureDiagnosticSource(message, type))
                    .GetAwaiter().GetResult(), Is.Empty, "Another thread cannot inherit the source scope.");
                // Simulate a reentrant native callback, even on Unity versions that suppress
                // Debug.Log from inside a log callback. The body and severity are identical.
                callbackEntry.Invoke(null, new object[] { message, stack, type, true });
            };
            Application.LogCallback capture = (message, stack, type) => {
                if (message.Contains(scope)) sources.Add(Logger.CaptureDiagnosticSource(message, type));
            };
            try
            {
                Application.logMessageReceived += reenter;
                Application.logMessageReceivedThreaded += capture;
                LogAssert.Expect(LogType.Warning, new Regex(scope));
                LogAssert.Expect(LogType.Warning, new Regex(scope));
                Logger.ReportServiceObservation("SERVER", scope, scope: scope);
                Assert.That(sources, Is.EqualTo(new[] { "", Logger.ServiceObservationSource }));
            }
            finally
            {
                Application.logMessageReceived -= reenter;
                Application.logMessageReceivedThreaded -= capture;
                Logger.CloseDiagnostics(scope);
            }
        }

        [Test]
        public void ExhaustedBridgeInitializationBudgetIsRedAndAggregatesAcrossReconnectSessions()
        {
            var priorConsole = Logger.LogToUnityConsole;
            var priorLevel = Logger.MinLevel;
            var scope = "budget-diagnostic-" + Guid.NewGuid().ToString("N");
            var observed = new List<(string message, LogType type)>();
            UPilotBridge.InitializationBudgetExpiredException failure;
            try { throw new UPilotBridge.InitializationBudgetExpiredException(0, 12345, 1000); }
            catch (UPilotBridge.InitializationBudgetExpiredException exception) { failure = exception; }
            Application.LogCallback callback = (message, stack, type) => {
                if (message.Contains(scope)) observed.Add((message, type));
            };
            try
            {
                Logger.LogToUnityConsole = false;
                Logger.MinLevel = Logger.LogLevel.Error;
                Application.logMessageReceivedThreaded += callback;
                LogAssert.Expect(LogType.Error, new Regex(scope));
                UPilotBridge.ReportInitializationBudgetExpired(failure, scope, "original-session", 7, scope);
                UPilotBridge.ReportInitializationBudgetExpired(failure, scope, "retry-session", 8, scope);
                Assert.That(observed.Count, Is.EqualTo(1), "A repeated expired budget is the same root despite a new socket session.");
                Assert.That(observed[0].type, Is.EqualTo(LogType.Error));
                Assert.That(observed[0].message, Does.Contain("sessionId=original-session")
                    .And.Contain("connectionEpoch=7").And.Contain("remainingBudgetMs=0")
                    .And.Contain("originalDeadlineTick=12345").And.Contain("readyRecoveryDeadlineUtcMs=1000")
                    .And.Contain(failure.ToString()));
                var anotherEpisode = new UPilotBridge.InitializationBudgetExpiredException(0, 23456, 2000);
                LogAssert.Expect(LogType.Error, new Regex(scope));
                UPilotBridge.ReportInitializationBudgetExpired(anotherEpisode, scope, "new-episode-session", 9, scope);
                Assert.That(observed.Count, Is.EqualTo(2), "Different recovery episodes at the same endpoint must not merge.");
                Assert.That(observed[1].message, Does.Contain("readyRecoveryDeadlineUtcMs=2000"));
                LogAssert.Expect(LogType.Error, new Regex("count=1"));
                Logger.CloseDiagnostics(scope);
                Assert.That(observed.Count, Is.EqualTo(3));
                Assert.That(observed[2].type, Is.EqualTo(LogType.Error));
                Assert.That(observed[2].message, Does.Contain("count=1").And.Contain("firstAt=").And.Contain("lastAt="));
                Assert.That(failure.RemainingBudgetMs, Is.Zero);
                Assert.That(failure.OriginalDeadlineTick, Is.EqualTo(12345));
                Assert.That(failure.ReadyRecoveryDeadlineUtcMs, Is.EqualTo(1000));
            }
            finally
            {
                Application.logMessageReceivedThreaded -= callback;
                Logger.CloseDiagnostics(scope);
                Logger.LogToUnityConsole = priorConsole;
                Logger.MinLevel = priorLevel;
            }
        }

        [Test]
        public void FirstIsImmediateAndRepeatsAreCountedWithoutQuietPollingNoise()
        {
            long now = 0;
            var output = new List<string>();
            var buffer = new Logger.DiagnosticBuffer((message, type, file) => output.Add(message), () => now);
            buffer.Report("failure", "original", LogType.Error);
            for (int i = 0; i < 17; i++) buffer.Report("failure", "original", LogType.Error);
            Assert.That(output, Is.EqualTo(new[] { "original" }));
            now = 10000; buffer.Poll();
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[1], Does.Contain("count=17"));
            now = 20000; buffer.Poll();
            Assert.That(output.Count, Is.EqualTo(2));
            now = 70000; buffer.Poll();
            Assert.That(buffer.Count, Is.Zero);
        }

        [Test]
        public void SeparateTasksRootsEscalationAndTerminalAreNeverHidden()
        {
            var output = new List<(string, LogType)>();
            var buffer = new Logger.DiagnosticBuffer((message, type, file) => output.Add((message, type)), () => 0);
            buffer.Report("root", "warn", LogType.Warning, "one");
            buffer.Report("root", "warn", LogType.Warning, "one");
            buffer.Report("root", "error", LogType.Error, "one");
            buffer.Report("root", "other task", LogType.Error, "two");
            buffer.Report("other", "other root", LogType.Error, "one");
            buffer.Report("root", "final", LogType.Error, "one", terminal: true);
            buffer.Report("root", "final", LogType.Error, "one", terminal: true);
            Assert.That(output.Count, Is.EqualTo(6));
            Assert.That(output[1].Item1, Does.Contain("count=1"));
            Assert.That(output[2], Is.EqualTo(("error", LogType.Error)));
            Assert.That(output[5].Item1, Is.EqualTo("final"));
        }

        [Test]
        public void ScopeCloseFlushesPendingCountsAndCapacityIsBounded()
        {
            var output = new List<string>();
            var buffer = new Logger.DiagnosticBuffer((message, type, file) => output.Add(message), () => 0);
            buffer.Report("a", "first", LogType.Error, "scope");
            buffer.Report("a", "first", LogType.Error, "scope");
            buffer.Flush("scope");
            Assert.That(output[1], Does.Contain("count=1"));
            Assert.That(buffer.Count, Is.Zero);
            for (int i = 0; i < 300; i++) buffer.Report(i.ToString(), "distinct", LogType.Warning);
            Assert.That(buffer.Count, Is.EqualTo(Logger.DiagnosticBuffer.Capacity));
            Assert.That(output.Count, Is.EqualTo(302));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailureKeepsOriginalStackOnDiskAndConsoleIndependentlyOfToggle(bool console)
        {
            bool previousConsole = Logger.LogToUnityConsole;
            var previousLevel = Logger.MinLevel;
            string scope = Guid.NewGuid().ToString("N");
            Exception error;
            try { throw new InvalidOperationException(scope); }
            catch (Exception ex) { error = ex; }
            try
            {
                Logger.LogToUnityConsole = console; Logger.MinLevel = Logger.LogLevel.Info;
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(scope)));
                Logger.ReportFailure("TEST", "operation=" + scope, error, scope, terminal: true);
                Assert.That(ReadGeneralLog(), Does.Contain(error.ToString()));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Logger.CloseDiagnostics(scope); Logger.LogToUnityConsole = previousConsole; Logger.MinLevel = previousLevel; }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void BuildFailureAndTimeoutAreRedAndPersistedWhileSuccessAndCancelStayQuiet(bool console)
        {
            bool previous = Logger.LogToUnityConsole;
            string id = Guid.NewGuid().ToString("N");
            Exception error;
            try { throw new InvalidOperationException("build-" + id); }
            catch (Exception ex) { error = ex; }
            try
            {
                Logger.LogToUnityConsole = console;
                var failed = new BuildStatusPayload { status = "failed", buildTarget = "StandaloneWindows64", outputPath = "test-output", summary = "build failed" };
                LogAssert.Expect(LogType.Error, new Regex("构建失败 requestId=" + id));
                UPilotBuildService.ReportBuildFailure(failed, id, 123, error);
                UPilotBuildService.ReportBuildFailure(failed, id, 123, error);
                Assert.That(ReadGeneralLog(), Does.Contain(error.ToString()).And.Contain("elapsedMs=123"));
                failed.summary = "Build timed out";
                LogAssert.Expect(LogType.Error, new Regex("Build timed out"));
                UPilotBuildService.ReportBuildFailure(failed, id + "-timeout", 1000);
                UPilotBuildService.ReportBuildFailure(failed, id + "-cancel", 1000, cancelled: true);
                UPilotBuildService.ReportBuildFailure(new BuildStatusPayload { status = "succeeded" }, id, 1);
                LogAssert.NoUnexpectedReceived();
            }
            finally { Logger.CloseDiagnostics(id); Logger.CloseDiagnostics(id + "-timeout"); Logger.LogToUnityConsole = previous; }
        }

        [Test]
        public void CommandFailurePreservesExceptionWhileBusyIsWarningAndCancelIsQuiet()
        {
            string id = Guid.NewGuid().ToString("N");
            Exception error;
            try { throw new InvalidOperationException(id); } catch (Exception ex) { error = ex; }
            LogAssert.Expect(LogType.Error, new Regex(id));
            UPilotBridge.ReportCommandError(id, "asset.find", "INTERNAL_ERROR", "failed", error);
            UPilotBridge.ReportCommandError(id, "asset.find", "INTERNAL_ERROR", "failed", error);
            LogAssert.Expect(LogType.Warning, new Regex(id + "-busy"));
            UPilotBridge.ReportCommandError(id, "compile.request", "EDITOR_BUSY", id + "-busy");
            UPilotBridge.ReportCommandError(id, "execution", "EXECUTION_CANCELLED", "normal cancellation");
            Assert.That(ReadGeneralLog(), Does.Contain(error.ToString()));
            Logger.CloseDiagnostics(id);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void CompileAdmissionWarningsAggregateByBatchAndKeepIndependentRequestsVisible()
        {
            string batch = "compile|" + Guid.NewGuid().ToString("N");
            string message = "compile admission " + batch;
            try
            {
                LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape("first-" + batch)));
                UPilotBridge.ReportCommandError("first-" + batch, "compile.request", "EDITOR_IN_PLAY_MODE", message, diagnosticScope: batch);
                UPilotBridge.ReportCommandError("retry-" + batch, "compile.request", "EDITOR_IN_PLAY_MODE", message, diagnosticScope: batch);
                LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape("independent-" + batch)));
                UPilotBridge.ReportCommandError("independent-" + batch, "compile.request", "EDITOR_IN_PLAY_MODE", message);
                LogAssert.Expect(LogType.Warning, new Regex("合并重复日志 count=1"));
                Logger.CloseDiagnostics(batch);
                LogAssert.NoUnexpectedReceived();
            }
            finally { Logger.CloseDiagnostics(batch); Logger.CloseDiagnostics("independent-" + batch); }
        }

        [Test]
        public void FileWriterRecoveryFlushesDuplicateFaultCountWithoutReplayingLostEntries()
        {
            string directory = Path.Combine(Application.dataPath, "../Library/UPilot/DiagnosticRecovery", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var writer = new UPilotDailyLogFile(Path.Combine(directory, "probe.log"));
            string path = writer.PathFor(DateTime.Now.Date);
            File.WriteAllText(path, "prior\n");
            try
            {
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    LogAssert.Expect(LogType.Error, new Regex("日志文件异常 operation=write[\\s\\S]*probe-"));
                    Assert.That(writer.Append("lost-one\n", DateTime.Now.Date), Is.False);
                    Assert.That(writer.Append("lost-two\n", DateTime.Now.Date), Is.False);
                }
                LogAssert.Expect(LogType.Error, new Regex("合并重复日志 count=1"));
                Assert.That(writer.Append("recovered\n", DateTime.Now.Date), Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("prior\nrecovered\n"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void SlowSinkDoesNotHoldAggregationLockAndBlockOtherDiagnostics()
        {
            using var entered = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            var buffer = new Logger.DiagnosticBuffer((message, type, file) =>
            { if (message == "first") { entered.Set(); release.Wait(5000); } });
            var first = System.Threading.Tasks.Task.Run(() => buffer.Report("first", "first", LogType.Error));
            System.Threading.Tasks.Task second = null;
            try
            {
                Assert.That(entered.Wait(2000), Is.True);
                second = System.Threading.Tasks.Task.Run(() => buffer.Report("second", "second", LogType.Error));
                Assert.That(second.Wait(2000), Is.True, "A blocked file sink must not hold the diagnostic lock.");
            }
            finally { release.Set(); first.Wait(5000); second?.Wait(5000); }
        }
    }
}
