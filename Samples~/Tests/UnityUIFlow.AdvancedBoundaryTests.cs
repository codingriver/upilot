using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowAdvancedBoundaryTests : UnityUIFlowFixture<SampleLoginWindow>
    {
        private class FailingScreenshotManager : ScreenshotManager
        {
            public FailingScreenshotManager() : base(new TestOptions { ScreenshotPath = Path.GetTempPath() }) { }
            public override async Task<string> CaptureAsync(string caseName, int stepIndex, string tag, CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw new System.InvalidOperationException("screenshot-failure");
            }
        }

        [UnityTest]
        public IEnumerator StepExecutor_CapturesScreenshotExceptionInErrorMessage()
        {
            yield return null;
            var step = new ExecutableStep
            {
                DisplayName = "Fail With Screenshot Error",
                ActionName = "assert_text",
                Parameters = new Dictionary<string, string>
                {
                    ["selector"] = "#status-label",
                    ["expected"] = "ThisWillFail",
                },
                TimeoutMs = 500,
            };

            var context = new ExecutionContext
            {
                Root = Root,
                Finder = new ElementFinder(),
                Options = new TestOptions { ScreenshotOnFailure = true },
                ActionRegistry = new ActionRegistry(),
                CancellationToken = CancellationToken.None,
                CaseName = "ScreenshotExceptionTest",
                ScreenshotManager = new FailingScreenshotManager(),
            };

            Task<StepResult> task = new StepExecutor().ExecuteStepAsync(step, context, 1);
            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Failed));
                Assert.That(result.ErrorMessage, Does.Contain("screenshot-failure"));
            });
        }


    }
}
