// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCSharpEvalToolCheckTests
    {
        [UnityTest]
        public IEnumerator RealChecksPassAndCloseOwnedSession()
        {
            var service = new UPilotExecutionService(null);
            var progress = "";
            var task = UPilotCSharpEvalToolCheck.RunAsync(service, CancellationToken.None, action => action(),
                value => progress = value);
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True);
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed), result.BuildFullReport());
            Assert.That(result.Checks, Has.Count.EqualTo(13));
            Assert.That(result.SkippedChecks, Is.Empty);
            Assert.That(result.Checks.Any(c => c.Actual.Contains("backendUsed=")), Is.True);
            Assert.That(result.Inputs.Select(input => input.Name), Is.EqualTo(result.Checks.Select(check => check.Name)));
            var variableInput = JsonUtility.FromJson<CSharpEvalPayload>(
                result.Inputs.Single(input => input.Name == "输入变量").PayloadJson);
            Assert.That(variableInput.code, Is.EqualTo("return a + b;"));
            Assert.That(variableInput.variablesJson, Does.Contain("\"valueJson\":\"10\"").And.Contain("\"valueJson\":\"20\""));
            Assert.That(variableInput.executionBackend, Is.EqualTo("auto"));
            Assert.That(variableInput.resultMode, Is.EqualTo("inline"));
            var budgetInput = JsonUtility.FromJson<CSharpEvalPayload>(
                result.Inputs.Single(input => input.Name == "预算限制").PayloadJson);
            Assert.That(budgetInput.limitsJson, Is.EqualTo("{\"timeoutMs\":3000,\"maxLoopIterations\":1}"));
            Assert.That(JsonUtility.FromJson<CSharpEvalPayload>(
                result.Inputs.Single(input => input.Name == "语法错误").PayloadJson).code, Is.EqualTo("?invalid syntax"));
            Assert.That(result.Inputs.Single(input => input.Name == "预取消").PreCancelled, Is.True);
            Assert.That(result.BuildFullReport(), Does.Contain("输入：").And.Contain("\"code\": \"return a + b;\""));
            Assert.That(progress, Does.Contain("13/13"));
            Assert.That(result.SessionClosed && result.CleanupSucceeded, Is.True);
            Assert.Throws<ExecutionContractException>(() => service.Sessions.Get(result.SessionId));
        }

        [Test]
        public void MissingServiceAndPreCancellationHaveDistinctTerminalStates()
        {
            var missing = UPilotCSharpEvalToolCheck.RunAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(missing.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck));
            Assert.That(missing.SkippedChecks, Has.Count.EqualTo(13));
            Assert.That(missing.Inputs, Is.Empty);
            var cancelled = UPilotCSharpEvalToolCheck.RunAsync(new UPilotExecutionService(null),
                new CancellationToken(true)).GetAwaiter().GetResult();
            Assert.That(cancelled.Status, Is.EqualTo(UPilotToolCheckStatus.Cancelled));
            Assert.That(cancelled.SessionId, Is.Empty);
            Assert.That(cancelled.Inputs, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InputSnapshotSurvivesPayloadChangesAndMissingResult(bool recordResult)
        {
            var payload = new CSharpEvalPayload { code = "return \"original\";\n", sessionId = "owned-session" };
            var result = new UPilotToolCheckResult("csharp_eval");
            result.Inputs.Add(UPilotQuickDebugToolCheck.CaptureInput("sample", payload, true));
            payload.code = "modified after capture";
            if (recordResult) result.Record("sample", false, "expected-value", "actual-error");
            var captured = JsonUtility.FromJson<CSharpEvalPayload>(result.Inputs[0].PayloadJson);
            Assert.That(captured.code, Is.EqualTo("return \"original\";\n"));
            Assert.That(captured.sessionId, Is.EqualTo("owned-session"));
            var report = result.BuildFullReport();
            Assert.That(report, Does.Contain("输入：").And.Contain("original").And.Contain("callTimeoutMs: 3000")
                .And.Contain("preCancelled: True").And.Not.Contain("modified after capture"));
            if (recordResult)
                Assert.That(report.IndexOf("输入：", StringComparison.Ordinal),
                    Is.LessThan(report.IndexOf("预期：expected-value", StringComparison.Ordinal)));
            Assert.That(result.BuildSummary(), Does.Not.Contain("original"));
        }

        [UnityTest]
        public IEnumerator OwnedRunDistinguishesUserCancellationFromDeadlineAndCleansUp()
        {
            foreach (var userCancellation in new[] { false, true })
            {
                var service = new UPilotExecutionService(null);
                using (var cancel = new CancellationTokenSource())
                {
                    var result = new UPilotToolCheckResult("csharp_eval");
                    result.ExpectedChecks.Add("unfinished");
                    var task = UPilotQuickDebugToolCheck.RunAsync(result, service, cancel.Token,
                        async (_, token) => { await Task.Delay(10000, token); },
                        userCancellation ? 30000 : 20);
                    if (userCancellation) cancel.Cancel();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                    Assert.That(task.IsCompleted, Is.True);
                    task.GetAwaiter().GetResult();
                    Assert.That(result.Status, Is.EqualTo(userCancellation
                        ? UPilotToolCheckStatus.Cancelled : UPilotToolCheckStatus.Failed), result.BuildFullReport());
                    Assert.That(result.SessionClosed && result.CleanupSucceeded, Is.True);
                    Assert.That(result.SkippedChecks, Does.Contain("unfinished"));
                    Assert.Throws<ExecutionContractException>(() => service.Sessions.Get(result.SessionId));
                }
            }
        }

        [Test]
        public void FailedAndSkippedAssertionsCannotProducePass()
        {
            var result = new UPilotToolCheckResult("csharp_eval");
            result.ExpectedChecks.AddRange(new[] { "first", "dependent", "independent" });
            result.Record("first", false, "1", "2");
            result.Record("independent", true, "3", "3");
            result.Complete();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.PassedChecks, Does.Contain("independent"));
            Assert.That(result.SkippedChecks, Does.Contain("dependent"));
            Assert.That(result.BuildFullReport(), Does.Contain("预期：1").And.Contain("实际：2"));
        }

        [Test]
        public void CleanupFailurePreventsSuccess()
        {
            foreach (var close in new[]
            {
                new ExecutionSessionCloseResult { closed = false },
                new ExecutionSessionCloseResult { closed = true, cleanupErrors = new[] { "failure" } },
                new ExecutionSessionCloseResult { closed = true, asyncOperationsStillRunning = 1 },
            })
            {
                var result = new UPilotToolCheckResult("csharp_eval");
                UPilotQuickDebugToolCheck.ApplyCleanup(result, close);
                result.Complete();
                Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
                Assert.That(result.CleanupSucceeded, Is.False);
            }
        }

        [Test]
        public void InitializationExceptionClosesSessionAndReportsUnableToCheck()
        {
            var service = new UPilotExecutionService(null);
            var result = UPilotQuickDebugToolCheck.RunAsync(new UPilotToolCheckResult("csharp_eval"), service,
                CancellationToken.None, (_, __) => throw new InvalidOperationException("sample initialization"))
                .GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck));
            Assert.That(result.SessionClosed, Is.True);
        }
    }
}
