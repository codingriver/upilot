// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Manager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotProcessCommandLineDiagnosticsTests
    {
        private static Manager.ProcessCommandLineQueryObservation Observation() => new()
        { Exited = true, ExitCode = 0, StdoutComplete = true, Stdout = "CommandLine=python example.py", StderrLength = 0 };

        [TestCase("start", "HelperStartFailed")]
        [TestCase("helper_timeout", "HelperTimedOut")]
        [TestCase("helper_cleanup", "HelperTimedOut")]
        [TestCase("success_cleanup", "HelperCleanupFailed")]
        [TestCase("stdout_timeout", "OutputTimedOut")]
        [TestCase("exit_code", "HelperExitFailed")]
        [TestCase("empty", "EmptyValue")]
        [TestCase("format", "InvalidOutput")]
        [TestCase("read_error", "QueryFailed")]
        public void ProductionReaderPreservesFailureReasonWithoutLoggingCommandOrOutput(string scenario,
            string expectedOutcome)
        {
            var expected = (Manager.ProcessCommandLineOutcome)Enum.Parse(typeof(Manager.ProcessCommandLineOutcome), expectedOutcome);
            var observation = Observation();
            const string sensitive = "private-command-or-output-value";
            switch (scenario)
            {
                case "start": observation.FailureStage = "start"; observation.Exception = new Win32Exception(5); break;
                case "helper_timeout": observation.FailureStage = "process_exit"; observation.Exited = false; break;
                case "helper_cleanup": observation.FailureStage = "process_exit"; observation.Exited = false;
                    observation.HelperProcessId = 321; observation.CleanupException = new Win32Exception(5); break;
                case "success_cleanup": observation.CleanupException = new Win32Exception(5); break;
                case "stdout_timeout": observation.FailureStage = "stdout_read"; observation.StdoutComplete = false; break;
                case "exit_code": observation.ExitCode = 5; observation.Stdout = sensitive; break;
                case "empty": observation.Stdout = "\r\nCommandLine= \r\n"; break;
                case "format": observation.Stdout = sensitive; break;
                case "read_error": observation.FailureStage = "stdout_read"; observation.Exception = new System.IO.IOException("simulated pipe read failure"); break;
            }
            var scope = "command-line-test|" + Guid.NewGuid().ToString("N");
            var messages = new List<string>();
            Application.LogCallback capture = (message, stack, type) => messages.Add(message);
            Application.logMessageReceived += capture;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(@"(?s)event=process_command_line_failed.*outcome=" + expected));
                var result = Manager.ProbeProcessCommandLine(123, queryOverride: (_, _) => observation, diagnosticScope: scope);
                Assert.That(result.Outcome, Is.EqualTo(expected));
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.LegacyValue, Is.EqualTo(expected == Manager.ProcessCommandLineOutcome.EmptyValue ? "(空)" : "(读取命令行失败)"));
                Assert.That(result.SafeDetail, Does.Not.Contain(sensitive));
                Assert.That(result.SafeDetail, Does.Contain("targetPid=123"));
                if (scenario == "start") Assert.That(result.OsError, Is.EqualTo(5));
                if (scenario == "exit_code") Assert.That(result.ExitCode, Is.EqualTo(5));
                if (scenario == "helper_cleanup") Assert.That(result.HelperCleanupOsError, Is.EqualTo(5));
                foreach (var message in messages) Assert.That(message, Does.Not.Contain(sensitive));
            }
            finally { Application.logMessageReceived -= capture; Logger.CloseDiagnostics(scope); }
        }

        [Test]
        public void ExpiredOriginalDeadlineDoesNotLaunchAQueryHelper()
        {
            var calls = 0;
            var scope = "command-line-deadline-test|" + Guid.NewGuid().ToString("N");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(@"(?s)event=process_command_line_failed.*outcome=DeadlineElapsed"));
                var result = Manager.ProbeProcessCommandLine(456, 1000,
                    (_, _) => { ++calls; return Observation(); }, () => 1000, scope);
                Assert.That(calls, Is.Zero);
                Assert.That(result.Outcome, Is.EqualTo(Manager.ProcessCommandLineOutcome.DeadlineElapsed));
                Assert.That(result.DeadlineUtcMs, Is.EqualTo(1000));
                Assert.That(result.RemainingBudgetMs, Is.Zero);
            }
            finally { Logger.CloseDiagnostics(scope); }
        }

        [Test]
        public void DeadlineConsumedDuringHelperWaitIsDistinctFromTheFixedHelperTimeout()
        {
            var now = 1000L;
            var scope = "command-line-consumed-deadline-test|" + Guid.NewGuid().ToString("N");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(@"(?s)event=process_command_line_failed.*outcome=DeadlineElapsed"));
                var result = Manager.ProbeProcessCommandLine(456, 1500,
                    (_, _) => { now = 1500; return new Manager.ProcessCommandLineQueryObservation { FailureStage = "process_exit" }; },
                    () => now, scope);
                Assert.That(result.Outcome, Is.EqualTo(Manager.ProcessCommandLineOutcome.DeadlineElapsed));
                Assert.That(result.Stage, Is.EqualTo("process_exit"));
                Assert.That(result.RemainingBudgetMs, Is.Zero);
            }
            finally { Logger.CloseDiagnostics(scope); }
        }

        [Test]
        public void AValidatedPropertyValueStaysInMemoryAndIsNotIncludedInSafeEvidence()
        {
            var observation = Observation();
            observation.Stdout = "\r\n\uFEFFCommandLine=python example.py --token private-token-value\r\n";
            var result = Manager.ProbeProcessCommandLine(789, queryOverride: (_, _) => observation);
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.LegacyValue, Is.EqualTo("python example.py --token private-token-value"));
            Assert.That(result.SafeDetail, Does.Not.Contain("python"));
            Assert.That(result.SafeDetail, Does.Not.Contain("private-token-value"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OwnerCancellationPropagatesWithoutReportingAProcessQueryFailure(bool cancelDuringQuery)
        {
            using var cancellation = new CancellationTokenSource();
            if (!cancelDuringQuery) cancellation.Cancel();
            var calls = 0;
            var scope = "command-line-cancellation-test|" + Guid.NewGuid().ToString("N");
            try
            {
                Assert.Throws<OperationCanceledException>(() => Manager.ProbeProcessCommandLine(123,
                    queryOverride: (_, _) =>
                    {
                        ++calls;
                        cancellation.Cancel();
                        var observed = Observation();
                        observed.Exception = new OperationCanceledException(cancellation.Token);
                        return observed;
                    }, diagnosticScope: scope, token: cancellation.Token));
                Assert.That(calls, Is.EqualTo(cancelDuringQuery ? 1 : 0));
                LogAssert.NoUnexpectedReceived();
            }
            finally { Logger.CloseDiagnostics(scope); }
        }

        [TestCase(0, 8000, 1000, 9000)]
        [TestCase(6000, 8000, 1000, 6000)]
        [TestCase(9000, 5000, 2000, 7000)]
        [TestCase(6000, 5000, 2000, 6000)]
        [TestCase(1500, 8000, 2000, 1500)]
        public void BackgroundRoundAndHelperDeadlinesNeverExtendTheirParent(long parent, int cap, long now, long expected) =>
            Assert.That(Manager.ProcessQueryDeadline(parent, cap, now), Is.EqualTo(expected));

        [Test]
        public void SynchronousDiagnosticReaderRetainsItsOneSecondDefault()
        {
            var timeout = typeof(Manager).GetMethod("ProbeProcessCommandLine", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .GetParameters()[6].DefaultValue;
            Assert.That(timeout, Is.EqualTo(1000));
        }

        [TestCase(0, 1000, 500, 1000)]
        [TestCase(10000, 1000, 500, 1000)]
        [TestCase(750, 1000, 500, 250)]
        [TestCase(500, 1000, 500, 0)]
        [TestCase(10000, 100, 500, 100)]
        public void HelperAndPipeWaitsKeepTheirExistingCapsWithinTheOriginalDeadline(long deadline, int cap, long now, int expected) =>
            Assert.That(Manager.ProcessCommandLineWaitMs(deadline, cap, now), Is.EqualTo(expected));
    }
}
