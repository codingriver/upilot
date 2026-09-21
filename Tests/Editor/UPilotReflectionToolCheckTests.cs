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
    public sealed class UPilotReflectionToolCheckTests
    {
        [UnityTest]
        public IEnumerator RealChecksPassWithoutEvaluationAndCloseOwnedSession()
        {
            var execution = new UPilotExecutionService(null);
            var reflection = new UPilotReflectionService(null, execution);
            var task = UPilotReflectionToolCheck.RunAsync(execution, reflection, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True);
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed), result.BuildFullReport());
            Assert.That(result.Checks, Has.Count.EqualTo(11));
            Assert.That(result.SkippedChecks, Is.Empty);
            Assert.That(result.SessionClosed && result.CleanupSucceeded, Is.True);
            Assert.Throws<ExecutionContractException>(() => execution.Sessions.Get(result.SessionId));
            Assert.That(result.BuildFullReport(), Does.Contain("sideEffectsMayHaveOccurred=True"));
            Assert.That(result.Inputs.Select(input => input.Name), Is.EqualTo(result.Checks.Select(check => check.Name)));
            var staticInput = JsonUtility.FromJson<ReflectionCallPayload>(result.Inputs[0].PayloadJson);
            Assert.That(staticInput.typeName, Is.EqualTo(typeof(TestUPilotQuickDebugObject).FullName));
            Assert.That(staticInput.methodName, Is.EqualTo("Add"));
            Assert.That(staticInput.isStatic, Is.True);
            Assert.That(staticInput.argumentsJson, Does.Contain("\"name\":\"second\"").And.Contain("\"name\":\"first\"")
                .And.Contain("\"name\":\"counter\"").And.Contain("\"kind\":\"handle\""));
            var overloadInput = JsonUtility.FromJson<ReflectionCallPayload>(
                result.Inputs.Single(input => input.Name == "精确重载").PayloadJson);
            Assert.That(overloadInput.parameterTypeNames, Is.EqualTo(new[] { "System.Int32" }));
            Assert.That(overloadInput.targetHandle, Is.Not.Empty);
            Assert.That(overloadInput.sessionId, Is.EqualTo(result.SessionId));
            Assert.That(JsonUtility.FromJson<ReflectionCallPayload>(
                result.Inputs.Single(input => input.Name == "显式泛型").PayloadJson).genericTypeArguments,
                Is.EqualTo(new[] { "System.Int32" }));
            Assert.That(JsonUtility.FromJson<ReflectionCallPayload>(
                result.Inputs.Single(input => input.Name == "ref/out").PayloadJson).argumentsJson,
                Does.Contain("\"direction\":\"ref\"").And.Contain("\"direction\":\"out\""));
            Assert.That(JsonUtility.FromJson<ReflectionCallPayload>(
                result.Inputs.Single(input => input.Name == "非法参数预检").PayloadJson).awaitMode, Is.EqualTo("invalid"));
            Assert.That(result.Inputs.Single(input => input.Name == "预取消").PreCancelled, Is.True);
            Assert.That(result.BuildFullReport(), Does.Contain("输入：").And.Contain("\"methodName\": \"Add\""));
        }

        [Test]
        public void MissingServicesAreUnableToCheck()
        {
            var execution = new UPilotExecutionService(null);
            var result = UPilotReflectionToolCheck.RunAsync(execution, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck));
            Assert.That(result.SessionId, Is.Empty);
            Assert.That(result.SkippedChecks, Has.Count.EqualTo(11));
            Assert.That(result.Inputs, Is.Empty);
        }

        [Test]
        public void PreCancelledCheckDoesNotCreateSession()
        {
            var execution = new UPilotExecutionService(null);
            var result = UPilotReflectionToolCheck.RunAsync(execution, new UPilotReflectionService(null, execution),
                new CancellationToken(true)).GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Cancelled));
            Assert.That(result.SessionId, Is.Empty);
            Assert.That(result.Inputs, Is.Empty);
        }

        [Test]
        public void SampleCountersArePerInstanceAndThrowIsCountedOnce()
        {
            var first = new TestUPilotQuickDebugObject();
            var second = new TestUPilotQuickDebugObject();
            Assert.Throws<InvalidOperationException>(() => first.Throw());
            Assert.That(first.Calls, Is.EqualTo(1));
            Assert.That(second.Calls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CancellationWaitsForOwnedAsyncMethodBeforeClosingSession()
        {
            var execution = new UPilotExecutionService(null);
            var reflection = new UPilotReflectionService(null, execution);
            var release = new TaskCompletionSource<bool>();
            var sample = new TestUPilotQuickDebugObject(release.Task);
            using var cancel = new CancellationTokenSource();
            var task = UPilotReflectionToolCheck.RunAsync(execution, reflection, cancel.Token, sample: sample);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (sample.PendingOperation.IsCompleted && !task.IsCompleted && DateTime.UtcNow < deadline)
                    yield return null;
                Assert.That(sample.PendingOperation.IsCompleted, Is.False);
                cancel.Cancel();
                for (var frame = 0; frame < 3; frame++) yield return null;
                Assert.That(task.IsCompleted, Is.False, "Do not publish or release state while the owned method is running.");
            }
            finally
            {
                cancel.Cancel();
                release.TrySetResult(true);
            }
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < cleanupDeadline) yield return null;
            Assert.That(task.IsCompleted, Is.True);
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Cancelled), result.BuildFullReport());
            Assert.That(sample.PendingOperation.IsCompleted, Is.True);
            Assert.That(result.SessionClosed && result.CleanupSucceeded, Is.True);
            Assert.Throws<ExecutionContractException>(() => execution.Sessions.Get(result.SessionId));
            Assert.That(result.Inputs.Any(input => input.Name == "Task"), Is.True);
            Assert.That(result.BuildFullReport(), Does.Contain("[Task] 调用输入").And.Contain("\"methodName\": \"TaskValue\""));
        }
    }
}
