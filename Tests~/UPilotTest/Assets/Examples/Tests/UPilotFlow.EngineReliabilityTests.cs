using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public sealed class UPilotFlowEngineReliabilityTests : UPilotFlowFixture<SampleLoginWindow>
    {
        [UnityTest]
        public IEnumerator CancellationToken_CancelsRunAsyncImmediately()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(
                "name: Cancelled Case\nsteps:\n  - action: wait\n    duration: '10ms'\n",
                "inline.yaml",
                Root,
                new TestOptions { Headed = false, DebugOnFailure = false, ScreenshotOnFailure = false },
                context => context.CancellationToken = cts.Token);

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.TestRunAborted)));
        }

        [UnityTest]
        public IEnumerator RunAsync_FromString_ExecutesFullLifecycle()
        {
            const string yaml = @"
name: Inline String Case
fixture:
  setup:
    - action: type_text_fast
      selector: '#username-input'
      value: 'inline-user'
  teardown:
    - action: click
      selector: '#reset-button'
steps:
  - action: assert_text
    selector: '#status-label'
    expected: 'Idle'
";

            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(
                yaml,
                "inline-string.yaml",
                Root,
                new TestOptions { Headed = false, DebugOnFailure = false, ScreenshotOnFailure = false });

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                Assert.That(result.StepResults.Count, Is.GreaterThanOrEqualTo(2));
            });
        }

        [UnityTest]
        public IEnumerator RetryCount_DoesNotCrashAndPreservesOption()
        {
            const string yaml = @"
name: Retry Count Case
steps:
  - action: wait
    duration: '10ms'
";

            var options = new TestOptions
            {
                Headed = false,
                DebugOnFailure = false,
                ScreenshotOnFailure = false,
                RetryCount = 3,
            };

            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(yaml, "retry-case.yaml", Root, options);

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
            });
        }

        [UnityTest]
        public IEnumerator PreStepDelayMs_ActuallyDelaysExecution()
        {
            const string yaml = @"
name: Delay Case
steps:
  - action: wait
    duration: '10ms'
";

            var options = new TestOptions
            {
                Headed = false,
                DebugOnFailure = false,
                ScreenshotOnFailure = false,
                PreStepDelayMs = 120,
            };

            var runner = new TestRunner();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task<TestResult> task = runner.RunAsync(yaml, "delay-case.yaml", Root, options);

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
            {
                sw.Stop();
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(100));
            });
        }

        [UnityTest]
        public IEnumerator HeadedFailurePolicyPause_ActuallyPausesController()
        {
            const string yaml = @"
name: Pause On Failure
steps:
  - action: assert_text
    selector: '#status-label'
    expected: 'This will definitely fail'
";

            var options = new TestOptions
            {
                Headed = true,
                DebugOnFailure = true,
            };

            var runner = new TestRunner();
            RuntimeController capturedController = null;
            Task<TestResult> task = runner.RunAsync(yaml, "pause-failure.yaml", Root, options, ctx =>
            {
                capturedController = ctx.RuntimeController;
            });

            float deadline = UnityEngine.Time.realtimeSinceStartup + 5f;
            while (capturedController == null || !capturedController.IsPausedForFailure)
            {
                if (UnityEngine.Time.realtimeSinceStartup >= deadline)
                    Assert.Fail("RuntimeController did not enter failure-pause state within 5 seconds.");
                yield return null;
            }

            Assert.That(capturedController, Is.Not.Null);
            Assert.That(capturedController.IsPausedForFailure, Is.True);
            capturedController.Resume();

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
                Assert.That(result.Status, Is.EqualTo(TestStatus.Failed)));
        }

        [UnityTest]
        public IEnumerator ExecutionRegistry_DirectRunIsVisibleAndCompletes()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            var options = new TestOptions
            {
                Headed = false,
                DebugOnFailure = false,
                ScreenshotOnFailure = false,
                ExecutionId = executionId,
                ExecutionSource = "direct-test",
            };

            Task<TestResult> task = new TestRunner().RunAsync(
                "name: Registry Visible\nsteps:\n  - action: wait\n    duration: '50ms'\n",
                "registry-visible.yaml",
                Root,
                options);

            yield return null;
            UPilotFlowExecutionSnapshot running = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(running, Is.Not.Null);
            Assert.That(running.source, Is.EqualTo("direct-test"));
            Assert.That(running.status, Is.EqualTo("running"));

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed)));

            UPilotFlowExecutionSnapshot completed = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(completed.status, Is.EqualTo("completed"));
            Assert.That(completed.activeLeaseCount, Is.Zero);
            Assert.That(completed.cleanupPending, Is.False);
        }

        [UnityTest]
        public IEnumerator ExecutionRegistry_StopDirectRunIsIdempotentAndCleansUp()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            var options = new TestOptions
            {
                Headed = false,
                DebugOnFailure = false,
                ScreenshotOnFailure = false,
                ExecutionId = executionId,
                ExecutionSource = "direct-test",
            };

            Task<TestResult> task = new TestRunner().RunAsync(
                "name: Registry Stop\nsteps:\n  - action: wait\n    duration: '10000ms'\n",
                "registry-stop.yaml",
                Root,
                options);

            yield return null;
            Assert.That(UPilotFlowExecutionRegistry.RequestStop(executionId), Is.True);
            Assert.That(UPilotFlowExecutionRegistry.RequestStop(executionId), Is.True);
            UPilotFlowExecutionSnapshot stopping = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(stopping.cancelRequested, Is.True);
            Assert.That(stopping.cancelAccepted, Is.True);
            Assert.That(stopping.cancelAttemptCount, Is.EqualTo(1));
            Assert.That(stopping.status, Is.EqualTo("stopping").Or.EqualTo("cleanup"));
            Assert.That(stopping.cleanupPending, Is.True);
            Assert.That(stopping.activeLeaseCount, Is.GreaterThan(0));
            Assert.That(stopping.unresolvedResources, Is.Not.Empty);

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.TestRunAborted)));

            UPilotFlowExecutionSnapshot stopped = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(stopped.status, Is.EqualTo("aborted"));
            Assert.That(stopped.activeLeaseCount, Is.Zero);
            Assert.That(stopped.cleanupPending, Is.False);
            Assert.That(stopped.unresolvedResources, Is.Empty);
            Assert.That(stopped.endedAt, Is.GreaterThan(0));
        }

        [Test]
        public void ExecutionRegistry_TerminalWaitsForEveryLeaseAndReportsCleanupErrors()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            System.IDisposable outer = UPilotFlowExecutionRegistry.Register(
                executionId,
                "registry-cleanup-test",
                () => throw new System.InvalidOperationException("expected cleanup probe"));
            System.IDisposable inner = UPilotFlowExecutionRegistry.Register(executionId, "registry-cleanup-test");

            Assert.That(UPilotFlowExecutionRegistry.RequestStop(executionId), Is.True);
            UPilotFlowExecutionSnapshot requested = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(requested.cancelAttemptCount, Is.EqualTo(1));
            Assert.That(requested.cleanupPending, Is.True);
            Assert.That(requested.activeLeaseCount, Is.EqualTo(2));
            Assert.That(requested.cleanupErrors, Has.Count.EqualTo(1));
            Assert.That(requested.status, Is.EqualTo("stopping"));

            UPilotFlowExecutionRegistry.MarkTerminal(executionId, "aborted");
            inner.Dispose();
            UPilotFlowExecutionSnapshot cleaning = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(cleaning.status, Is.EqualTo("cleanup"));
            Assert.That(cleaning.cleanupPending, Is.True);
            Assert.That(cleaning.activeLeaseCount, Is.EqualTo(1));
            Assert.That(cleaning.endedAt, Is.Zero);

            outer.Dispose();
            UPilotFlowExecutionSnapshot terminal = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(terminal.status, Is.EqualTo("aborted"));
            Assert.That(terminal.cleanupPending, Is.False);
            Assert.That(terminal.activeLeaseCount, Is.Zero);
            Assert.That(terminal.unresolvedResources, Is.Empty);
            Assert.That(terminal.endedAt, Is.GreaterThan(0));
        }

        [Test]
        public void ModalWatchdog_RegistersBoundedCleanupResourceAndClearsItOnDispose()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            var options = new TestOptions
            {
                ExecutionId = executionId,
                ExecutionSource = "modal-watchdog-test",
                EnableModalWatchdog = true,
                ModalWatchdogTimeoutMs = 60000,
            };
            System.IDisposable runLease = UPilotFlowExecutionRegistry.Register(executionId, options.ExecutionSource);
            var context = new ActionContext
            {
                Options = options,
                CurrentCaseName = "Modal Watchdog",
                CurrentStepId = "open-menu",
            };

            System.IDisposable watchdog = UPilotFlowModalWatchdog.Arm(context, "popup-menu");
            UPilotFlowExecutionSnapshot waiting = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(waiting.waitingForModalUi, Is.True);
            Assert.That(waiting.activeModalCount, Is.EqualTo(1));
            Assert.That(waiting.phase, Is.EqualTo("waiting_for_modal_ui"));
            Assert.That(waiting.modalOwner, Is.EqualTo("open-menu"));
            Assert.That(waiting.modalType, Is.EqualTo("popup-menu"));
            Assert.That(waiting.modalDeadline, Is.GreaterThan(waiting.modalStartedAt));
            Assert.That(waiting.unresolvedResources, Has.Some.EqualTo("modal-ui:1"));

            watchdog.Dispose();
            UPilotFlowExecutionSnapshot cleared = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(cleared.waitingForModalUi, Is.False);
            Assert.That(cleared.activeModalCount, Is.Zero);
            Assert.That(cleared.unresolvedResources, Has.None.EqualTo("modal-ui:1"));

            runLease.Dispose();
        }

        [Test]
        public void ExecutionRegistry_ActionDeadlineAndProgressAreObservable()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            System.IDisposable lease = UPilotFlowExecutionRegistry.Register(executionId, "action-progress-test");
            var step = new ExecutableStep
            {
                DisplayName = "Long action",
                ActionName = "wait",
                TimeoutMs = 1234,
                Phase = StepPhase.Main,
            };

            UPilotFlowExecutionRegistry.StepStarted(executionId, 2, step);
            UPilotFlowExecutionSnapshot started = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(started.actionTimeoutMs, Is.EqualTo(1234));
            Assert.That(started.actionDeadline, Is.GreaterThan(started.actionStartedAt));
            Assert.That(started.progressDetail, Is.EqualTo("action_started"));

            UPilotFlowExecutionRegistry.ActionProgress(executionId, "wait:500/1234ms");
            Assert.That(UPilotFlowExecutionRegistry.Get(executionId).progressDetail, Is.EqualTo("wait:500/1234ms"));

            UPilotFlowExecutionRegistry.StepCompleted(executionId);
            UPilotFlowExecutionSnapshot completed = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(completed.actionDeadline, Is.Zero);
            Assert.That(completed.progressDetail, Is.EqualTo("action_completed"));
            lease.Dispose();
        }

        [Test]
        public void RuntimeController_UnattendedManualPauseIsCoercedToBoundedAutoAbort()
        {
            var controller = new RuntimeController();
            controller.Configure(new TestOptions
            {
                Unattended = true,
                PauseTimeoutMs = 100,
                PauseTimeoutPolicy = "manual",
            });
            controller.Pause("test-owner", "manual-probe");

            Assert.That(controller.PauseTimeoutPolicy, Is.EqualTo("auto_abort"));
            Assert.That(controller.PauseDeadline, Is.GreaterThan(controller.PausedAt));
            Assert.That(controller.PauseToken, Is.Not.Null.And.Not.Empty);
            controller.Dispose();
        }

        [UnityTest]
        public IEnumerator UnattendedFailurePause_AutoAbortsAtDeadline()
        {
            string executionId = System.Guid.NewGuid().ToString("N");
            var options = new TestOptions
            {
                Headed = true,
                DebugOnFailure = true,
                ScreenshotOnFailure = false,
                ExecutionId = executionId,
                ExecutionSource = "unattended-test",
                Unattended = true,
                PauseTimeoutMs = 200,
                PauseTimeoutPolicy = "auto_abort",
            };

            Task<TestResult> task = new TestRunner().RunAsync(
                "name: Timed Failure Pause\nsteps:\n  - action: assert_text\n    selector: '#status-label'\n    expected: 'This will fail'\n",
                "timed-failure-pause.yaml",
                Root,
                options);

            yield return UPilotFlowTestTaskUtility.Await(task, result =>
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.TestRunAborted)));

            UPilotFlowExecutionSnapshot snapshot = UPilotFlowExecutionRegistry.Get(executionId);
            Assert.That(snapshot.status, Is.EqualTo("aborted"));
            Assert.That(snapshot.paused, Is.False);
        }

    }

    public sealed class UPilotTestRunnerCancellationAcceptanceTests
    {
        [UnityTest, Explicit("UPilot Test Runner cancellation acceptance probe")]
        public IEnumerator LongRunningExplicitProbe()
        {
            float deadline = UnityEngine.Time.realtimeSinceStartup + 60f;
            while (UnityEngine.Time.realtimeSinceStartup < deadline)
                yield return null;
        }
    }
}
