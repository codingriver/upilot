using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    // Unity EditMode executes these synchronous cases sequentially; settings are restored in TearDown.
    public sealed class UPilotLifecycleLoggingTests
    {
        private bool _console, _wire, _verbose;
        private Logger.LogLevel _level;
        private StackTraceLogType _stackType;

        [SetUp]
        public void SaveSettings()
        {
            _console = Logger.LogToUnityConsole;
            _wire = ReadRuntimeFlag("_debugWireLogsEnabled");
            _verbose = ReadRuntimeFlag("_verboseLogsEnabled");
            _level = Logger.MinLevel;
            _stackType = Application.GetStackTraceLogType(LogType.Log);
        }

        [TearDown]
        public void RestoreSettings()
        {
            Logger.LogToUnityConsole = _console;
            Logger.SetRuntimeLogOptions(_wire, _verbose);
            Logger.MinLevel = _level;
            Application.SetStackTraceLogType(LogType.Log, _stackType);
        }

        [TestCase(Logger.LogLevel.Info)]
        [TestCase(Logger.LogLevel.Error)]
        public void RequiredLogReachesBothSinksWithExplicitStacksAndInfoFiltersDisabled(Logger.LogLevel level)
        {
            var marker = "lifecycle-required-" + Guid.NewGuid().ToString("N");
            var messages = new List<string>();
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Log && message.Contains(marker)) messages.Add(message);
            };
            Logger.MinLevel = level;
            Logger.SetRuntimeLogOptions(false, false);
            Logger.LogToUnityConsole = false;
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Log, new Regex(marker));
                Logger.Lifecycle("reset.blocked", marker, Logger.CaptureLifecycleStack(), true);
                Assert.That(messages.Count, Is.EqualTo(1));
                var message = messages[0];
                Assert.That(message, Does.Contain("requestStack:\n").And.Contain("executionStack:\n"));
                Assert.That(message, Does.Contain(nameof(RequiredLogReachesBothSinksWithExplicitStacksAndInfoFiltersDisabled)));
                Assert.That(message, Does.Match(@"utc=\d{4}-\d{2}-\d{2}T[^\s]+Z pid=\d+ thread=\d+ eventId=[0-9a-f]{32} event=reset\.blocked"));
                Assert.That(ReadGeneralLog(), Does.Contain(message));
                Assert.That(UPilotConsoleService.IsUPilotOwnedLog(message, ""), Is.True);
                Assert.That(Logger.MinLevel, Is.EqualTo(level));
                Assert.That(ReadRuntimeFlag("_debugWireLogsEnabled"), Is.False);
                Assert.That(ReadRuntimeFlag("_verboseLogsEnabled"), Is.False);
                Assert.That(Logger.LogToUnityConsole, Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [Test]
        public void EachBlockedDecisionIsWrittenImmediatelyWithoutAggregation()
        {
            var marker = "lifecycle-repeat-" + Guid.NewGuid().ToString("N");
            LogAssert.Expect(LogType.Log, new Regex("event=" + marker));
            LogAssert.Expect(LogType.Log, new Regex("event=" + marker));
            Logger.Lifecycle(marker, "reason=stop_active");
            Logger.Lifecycle(marker, "reason=stop_active");
            Assert.That(Regex.Matches(ReadGeneralLog(), "event=" + marker).Count, Is.EqualTo(2));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SecretsAreRedactedFromDetailAndExplicitStackBeforeBothSinks()
        {
            var marker = "lifecycle-redact-" + Guid.NewGuid().ToString("N");
            var secrets = new[] { "access-value", "plain-token", "password with spaces", "header-value", "cookie-value",
                "other-cookie-value", "api-secret", "refresh-secret", "loose-bearer", "stack-secret", "escaped-secret" };
            var detail = marker + "\naccessToken=access-value\ntoken=plain-token\npassword=\"password with spaces\"" +
                "\nAuthorization: Bearer header-value\nCookie: sid=cookie-value; session=other-cookie-value" +
                "\n{\"apiKey\":\"api-secret\",\"refresh_token\":\"refresh-secret\"}\nBearer loose-bearer" +
                "\n{\"password\":\"escaped\\\"escaped-secret\"}";
            string observed = null;
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Log && message.Contains(marker)) observed = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Log, new Regex(marker));
                Logger.Lifecycle("reset.blocked", detail, "at Sample.Run() token=stack-secret");
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed, Does.Contain("[REDACTED]"));
                foreach (var secret in secrets) Assert.That(observed, Does.Not.Contain(secret), secret);
                Assert.That(ReadGeneralLog(), Does.Contain(observed));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [Test]
        public void NonceUrlsAndCommandArgumentsInExceptionsAreRedactedBeforeBothSinks()
        {
            var marker = "lifecycle-request-redact-" + Guid.NewGuid().ToString("N");
            var requestUrl = "https://url-user:url-password@127.0.0.1:8012/health?probe=bridge&nonce=url-nonce&" +
                "access%5Ftoken=encoded-key-secret&redirect=https%3A%2F%2Fhost%2F%3Ftoken%3Dnested-secret";
            var command = "server.exe --nonce \"nonce with spaces\" --owner-token owner-cli-secret " +
                "--password 'password cli secret' /apiKey:windows-cli-secret --confirmToken=confirm-cli-secret";
            var error = new InvalidOperationException(marker + " request=" + requestUrl,
                new IOException("command=" + command + "\nnonce=plain-nonce\nbridge_probe_nonce=probe-nonce"));
            var secrets = new[] { "url-user", "url-password", "url-nonce", "encoded-key-secret", "nested-secret",
                "nonce with spaces", "owner-cli-secret", "password cli secret", "windows-cli-secret",
                "confirm-cli-secret", "plain-nonce", "probe-nonce", "stack-query-secret" };
            string observed = null;
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Log && message.Contains(marker)) observed = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Log, new Regex(marker));
                Logger.Lifecycle("request.failed", error.ToString(),
                    "at Request() url=ws://127.0.0.1:8765/mcp?n%6Fnce=stack-query-secret");
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed, Does.Contain("https://[REDACTED]@127.0.0.1:8012/health?[REDACTED]")
                    .And.Contain("ws://127.0.0.1:8765/mcp?[REDACTED]")
                    .And.Contain("server.exe --nonce [REDACTED]")
                    .And.Contain(nameof(InvalidOperationException)).And.Contain(nameof(IOException)));
                foreach (var secret in secrets) Assert.That(observed, Does.Not.Contain(secret), secret);
                Assert.That(ReadGeneralLog(), Does.Contain(observed));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [Test]
        public void LifecycleFailureIsVisibleAsErrorAndPreservesRedactedOriginalExceptionStack()
        {
            var marker = "lifecycle-failure-" + Guid.NewGuid().ToString("N");
            Logger.MinLevel = Logger.LogLevel.Error;
            Logger.LogToUnityConsole = false;
            Logger.SetRuntimeLogOptions(false, false);
            Exception failure;
            try
            {
                throw new InvalidOperationException("request=http://127.0.0.1:8012/health?nonce=private-failure-nonce",
                    new IOException("server.exe --owner-token private-failure-owner"));
            }
            catch (Exception ex) { failure = ex; }
            string observed = null;
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Error && message.Contains(marker)) observed = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(marker));
                Logger.LifecycleFailure("recovery.failed", marker, failure, marker, terminal: true);
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed, Does.Contain("http://127.0.0.1:8012/health?[REDACTED]")
                    .And.Contain("server.exe --owner-token [REDACTED]")
                    .And.Contain(nameof(InvalidOperationException)).And.Contain(nameof(IOException))
                    .And.Contain(nameof(LifecycleFailureIsVisibleAsErrorAndPreservesRedactedOriginalExceptionStack))
                    .And.Not.Contain("private-failure-nonce").And.Not.Contain("private-failure-owner"));
                Assert.That(ReadGeneralLog(), Does.Contain(observed));
                Assert.That(Logger.LogToUnityConsole, Is.False);
                Assert.That(Logger.MinLevel, Is.EqualTo(Logger.LogLevel.Error));
                Assert.That(ReadRuntimeFlag("_debugWireLogsEnabled"), Is.False);
                Assert.That(ReadRuntimeFlag("_verboseLogsEnabled"), Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Application.logMessageReceived -= capture;
                Logger.CloseDiagnostics(marker);
            }
        }

        [Test]
        public void LifecycleFailureAggregatesRepeatedRootAndKeepsDifferentMessagesIndependent()
        {
            var marker = "lifecycle-failure-roots-" + Guid.NewGuid().ToString("N");
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Error && message.Contains(marker)) errors.Add(message);
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("root-a"));
                Logger.LifecycleFailure("probe.failed", marker,
                    new IOException("root-a http://127.0.0.1:8012/health?nonce=first-private-nonce"), marker);
                Logger.LifecycleFailure("probe.failed", marker,
                    new IOException("root-a http://127.0.0.1:8012/health?nonce=second-private-nonce"), marker);
                LogAssert.Expect(LogType.Error, new Regex("root-b"));
                Logger.LifecycleFailure("probe.failed", marker, new IOException("root-b"), marker);
                Assert.That(errors.Count, Is.EqualTo(2));
                LogAssert.Expect(LogType.Error, new Regex("合并重复日志 count=1"));
                Logger.CloseDiagnostics(marker);
                Assert.That(errors.Count, Is.EqualTo(3));
                Assert.That(errors[2], Does.Contain("root-a").And.Contain("count=1")
                    .And.Not.Contain("first-private-nonce").And.Not.Contain("second-private-nonce"));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Application.logMessageReceived -= capture;
                Logger.CloseDiagnostics(marker);
            }
        }

        [Test]
        public void LifecycleFailureRetainsOriginalAndRootStacksAfterLongOuterMessage()
        {
            var marker = "lifecycle-long-failure-" + Guid.NewGuid().ToString("N");
            Exception root;
            try { throw new IOException("root failure evidence"); }
            catch (Exception ex) { root = ex; }
            Exception failure;
            try { throw new InvalidOperationException(new string('x', 100000), root); }
            catch (Exception ex) { failure = ex; }
            string observed = null;
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Error && message.Contains(marker)) observed = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(marker));
                Logger.LifecycleFailure("probe.failed", marker, failure, marker, terminal: true);
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed, Does.Contain("[truncated, total=")
                    .And.Contain("originalExceptionStack:\n").And.Contain("rootExceptionStack:\n")
                    .And.Contain("root failure evidence")
                    .And.Contain(nameof(LifecycleFailureRetainsOriginalAndRootStacksAfterLongOuterMessage)));
                Assert.That(observed.Length, Is.LessThan(Logger.MaxLogEntryChars));
                Assert.That(ReadGeneralLog(), Does.Contain(observed));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Application.logMessageReceived -= capture;
                Logger.CloseDiagnostics(marker);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LifecycleFailureKeepsIdenticalWrappersWithDifferentInnerRootsIndependent(bool differentType)
        {
            var marker = "lifecycle-inner-roots-" + Guid.NewGuid().ToString("N");
            var errors = new List<string>();
            var firstRoot = new IOException("inner-root-a");
            Exception secondRoot = differentType
                ? new InvalidOperationException("inner-root-a")
                : new IOException("inner-root-b");
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Error && message.Contains(marker)) errors.Add(message);
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(marker));
                LogAssert.Expect(LogType.Error, new Regex(marker));
                Logger.LifecycleFailure("probe.failed", marker, new ApplicationException("same wrapper", firstRoot), marker);
                Logger.LifecycleFailure("probe.failed", marker, new ApplicationException("same wrapper", secondRoot), marker);
                Assert.That(errors.Count, Is.EqualTo(2));
                Assert.That(errors[0], Does.Contain(nameof(IOException)).And.Contain("inner-root-a"));
                Assert.That(errors[1], Does.Contain(differentType ? nameof(InvalidOperationException) : "inner-root-b"));
                Logger.CloseDiagnostics(marker);
                Assert.That(errors.Count, Is.EqualTo(2));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Application.logMessageReceived -= capture;
                Logger.CloseDiagnostics(marker);
            }
        }

        [Test]
        public void LongDetailIsBoundedWithoutDroppingRequestedStackEvidence()
        {
            var marker = "lifecycle-bound-" + Guid.NewGuid().ToString("N");
            string observed = null;
            Application.LogCallback capture = (message, stack, type) => {
                if (type == LogType.Log && message.Contains(marker)) observed = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Log, new Regex(marker));
                Logger.Lifecycle("reset.blocked", marker + new string('x', 100000), "request-stack-evidence", true);
                Assert.That(observed, Does.Contain("[truncated, total=").And.Contain("request-stack-evidence")
                    .And.Contain("executionStack:\n"));
                Assert.That(observed.Length, Is.LessThan(Logger.MaxLogEntryChars));
                Assert.That(ReadGeneralLog(), Does.Contain(observed));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [Test]
        public void FileSinkFailureStillEmitsNormalConsoleLogAndSeparateRedactedError()
        {
            var marker = "lifecycle-file-failure-" + Guid.NewGuid().ToString("N");
            var message = "[UPilot][Lifecycle] event=" + marker;
            Logger.LogToUnityConsole = false;
            Logger.MinLevel = Logger.LogLevel.Error;
            string observedError = null;
            Application.LogCallback capture = (entry, stack, type) => {
                if (type == LogType.Error && entry.Contains(marker)) observedError = entry;
            };
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(@"\[UPilot\]\[Lifecycle\] 日志输出失败 sink=file[\s\S]*password=\[REDACTED\]"));
                LogAssert.Expect(LogType.Log, message);
                Assert.DoesNotThrow(() => Logger.EmitLifecycleEntry(message,
                    _ => throw new IOException("password=file-secret marker=" + marker +
                        "\nrequest=http://127.0.0.1:8012/health?nonce=failure-query-secret" +
                        "\ncommand=server.exe --nonce failure-command-secret"), line => UnityEngine.Debug.Log(line)));
                Assert.That(observedError, Is.Not.Null);
                Assert.That(observedError, Does.Contain(nameof(IOException)).And.Not.Contain("file-secret")
                    .And.Not.Contain("failure-query-secret").And.Not.Contain("failure-command-secret"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [Test]
        public void ExplicitStackAndElapsedUseCallerAndMonotonicTimestamp()
        {
            Assert.That(Logger.CaptureLifecycleStack(), Does.Contain(nameof(ExplicitStackAndElapsedUseCallerAndMonotonicTimestamp)));
            var started = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 4;
            Assert.That(Logger.LifecycleElapsedMs(started), Is.GreaterThanOrEqualTo(200).And.LessThan(5000));
            Assert.That(Logger.LifecycleElapsedMs(0), Is.Zero);
        }

        private static bool ReadRuntimeFlag(string name) =>
            (bool)typeof(Logger).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);

        private static string ReadGeneralLog()
        {
            using var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
