using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowEngineReliabilityTests : UnityUIFlowFixture<SampleLoginWindow>
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
                new TestOptions(),
                context => context.CancellationToken = cts.Token);

            yield return UnityUIFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<System.OperationCanceledException>().Or.TypeOf<UnityUIFlowException>());
            });
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
    expected: 'Please log in'
";

            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(yaml, "inline-string.yaml", Root);

            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
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
                RetryCount = 3,
            };

            var runner = new TestRunner();
            Task<TestResult> task = runner.RunAsync(yaml, "retry-case.yaml", Root, options);

            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
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
                PreStepDelayMs = 120,
            };

            var runner = new TestRunner();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task<TestResult> task = runner.RunAsync(yaml, "delay-case.yaml", Root, options);

            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
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

            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Failed));
                Assert.That(capturedController, Is.Not.Null);
                Assert.That(capturedController.IsPausedForFailure, Is.True);
                capturedController?.Resume();
            });
        }
    }
}
