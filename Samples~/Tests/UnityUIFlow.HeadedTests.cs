using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowHeadedTests
    {
        [Test]
        public void RuntimeController_StepModePausesAfterCompletion()
        {
            using (var controller = new RuntimeController())
            {
                controller.RunMode = HeadedRunMode.Step;
                controller.StepOnce();

                controller.OnStepCompleted();

                Assert.That(controller.IsPaused, Is.True);
                controller.Resume();
                Assert.That(controller.IsPaused, Is.False);
            }
        }

        [Test]
        public void RuntimeController_PauseForFailure_TracksFailurePauseState()
        {
            using (var controller = new RuntimeController())
            {
                controller.PauseForFailure();

                Assert.That(controller.IsPaused, Is.True);
                Assert.That(controller.IsPausedForFailure, Is.True);

                controller.Resume();

                Assert.That(controller.IsPaused, Is.False);
                Assert.That(controller.IsPausedForFailure, Is.False);
            }
        }

        [Test]
        public void HeadedRunEventBus_PublishesRegisteredCallbacks()
        {
            bool runAttached = false;
            bool stepStarted = false;
            bool stepCompleted = false;
            bool highlightChanged = false;
            bool failureRaised = false;
            bool finishedRaised = false;

            RuntimeController controller = new RuntimeController();
            var step = new ExecutableStep
            {
                DisplayName = "Headed Step",
            };
            var result = new StepResult
            {
                DisplayName = "Headed Step",
                Status = TestStatus.Failed,
            };
            var root = new VisualElement();

            void OnRunAttached(RuntimeController _, string __) => runAttached = true;
            void OnStepStarted(ExecutableStep _) => stepStarted = true;
            void OnStepCompleted(ExecutableStep _, StepResult __, VisualElement ___) => stepCompleted = true;
            void OnHighlighted(ExecutableStep _, VisualElement __) => highlightChanged = true;
            void OnFailure(ExecutableStep _, StepResult __) => failureRaised = true;
            void OnFinished(TestResult _) => finishedRaised = true;

            HeadedRunEventBus.RunAttached += OnRunAttached;
            HeadedRunEventBus.StepStarted += OnStepStarted;
            HeadedRunEventBus.StepCompleted += OnStepCompleted;
            HeadedRunEventBus.HighlightedElementChanged += OnHighlighted;
            HeadedRunEventBus.Failure += OnFailure;
            HeadedRunEventBus.RunFinished += OnFinished;

            try
            {
                HeadedRunEventBus.PublishRunAttached(controller, "Headed Case");
                HeadedRunEventBus.PublishStepStarted(step);
                HeadedRunEventBus.PublishStepCompleted(step, result, root);
                HeadedRunEventBus.PublishHighlightedElement(step, root);
                HeadedRunEventBus.PublishFailure(step, result);
                HeadedRunEventBus.PublishRunFinished(new TestResult { CaseName = "Headed Case", Status = TestStatus.Failed });
            }
            finally
            {
                HeadedRunEventBus.RunAttached -= OnRunAttached;
                HeadedRunEventBus.StepStarted -= OnStepStarted;
                HeadedRunEventBus.StepCompleted -= OnStepCompleted;
                HeadedRunEventBus.HighlightedElementChanged -= OnHighlighted;
                HeadedRunEventBus.Failure -= OnFailure;
                HeadedRunEventBus.RunFinished -= OnFinished;
                controller.Dispose();
            }

            Assert.That(runAttached, Is.True);
            Assert.That(stepStarted, Is.True);
            Assert.That(stepCompleted, Is.True);
            Assert.That(highlightChanged, Is.True);
            Assert.That(failureRaised, Is.True);
            Assert.That(finishedRaised, Is.True);
        }

        [UnityTest]
        public IEnumerator HighlightOverlayRenderer_AttachesAndDetachesFromWindow()
        {
            SampleLoginWindow window = EditorWindow.GetWindow<SampleLoginWindow>();
            window.BuildUi();
            yield return null;

            int initialChildren = window.rootVisualElement.childCount;
            var renderer = new HighlightOverlayRenderer();

            renderer.Attach(window);
            renderer.Highlight(window.rootVisualElement.Q<VisualElement>("login-panel"));

            Assert.That(window.rootVisualElement.childCount, Is.EqualTo(initialChildren + 1));

            renderer.Clear();
            renderer.Detach();

            Assert.That(window.rootVisualElement.childCount, Is.EqualTo(initialChildren));
            window.Close();
        }

        [Test]
        public void RuntimeController_Stop_SetsStoppedAndCancelsToken()
        {
            using (var controller = new RuntimeController())
            {
                Assert.That(controller.IsStopped, Is.False);
                controller.Stop();
                Assert.That(controller.IsStopped, Is.True);
                Assert.That(controller.CancellationToken.IsCancellationRequested, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator HighlightOverlayRenderer_UpdatesPositionAndColor()
        {
            SampleLoginWindow window = EditorWindow.GetWindow<SampleLoginWindow>();
            window.BuildUi();
            yield return null;

            var renderer = new HighlightOverlayRenderer();
            renderer.Attach(window);
            VisualElement target = window.rootVisualElement.Q<VisualElement>("login-panel");
            renderer.Highlight(target);
            yield return null;

            VisualElement overlayRoot = window.rootVisualElement[window.rootVisualElement.childCount - 1];
            VisualElement marker = overlayRoot[0];
            Assert.That(marker.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(marker.resolvedStyle.left, Is.GreaterThanOrEqualTo(0));
            Assert.That(marker.resolvedStyle.top, Is.GreaterThanOrEqualTo(0));
            Assert.That(marker.resolvedStyle.width, Is.GreaterThan(0));
            Assert.That(marker.resolvedStyle.height, Is.GreaterThan(0));

            renderer.Detach();
            window.Close();
        }


    }
}
