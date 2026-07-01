using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using codingriver.upilot.UIFlow;

namespace codingriver.upilot.tests
{
    [Category("Upilot")]
    public sealed class UpilotUIFlowServiceTests
    {
        [Test]
        [Category("Upilot")]
        public void ResolveYamlPaths_RejectsMixedModes()
        {
            var payload = new UIFlowRunPayload
            {
                yamlPaths = new[] { "Assets/upilot/UIFlow/Samples/Yaml/01-basic-login.yaml" },
                yamlDirectory = "Assets/upilot/UIFlow/Samples/Yaml",
            };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => UpilotUIFlowService.ResolveYamlPaths(payload));

            Assert.That(ex.Message, Does.Contain("yamlPaths"));
            Assert.That(ex.Message, Does.Contain("yamlDirectory"));
        }

        [Test]
        [Category("Upilot")]
        public void ResolveYamlPaths_SortsDirectoryResults()
        {
            string directory = CreateTempDirectory();
            string nested = Path.Combine(directory, "Nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(directory, "b-case.yaml"), "name: B");
            File.WriteAllText(Path.Combine(nested, "a-case.yaml"), "name: A");

            var payload = new UIFlowRunPayload
            {
                yamlDirectory = directory,
            };

            List<string> resolved = UpilotUIFlowService.ResolveYamlPaths(payload);

            Assert.That(resolved, Has.Count.EqualTo(2));
            Assert.That(string.Compare(resolved[0], resolved[1], StringComparison.OrdinalIgnoreCase), Is.LessThanOrEqualTo(0));
        }

        [Test]
        [Category("Upilot")]
        public void ToCasePayload_NormalizesRelativeArtifactsIntoProjectRelativePaths()
        {
            string executionId = Guid.NewGuid().ToString("N");
            string reportPath = UpilotUIFlowService.BuildExecutionReportPath("Reports/upilot/UIFlowMcp", executionId);
            string yamlPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "upilot", "UIFlow", "Samples", "Yaml", "01-basic-login.yaml");

            var result = new TestResult
            {
                CaseName = "Basic Login",
                Status = TestStatus.Failed,
                ErrorCode = ErrorCodes.ActionExecutionFailed,
                ErrorMessage = "missing button event",
                Attachments = new List<string> { "Screenshots/top-level.png" },
                StepResults = new List<StepResult>
                {
                    new StepResult
                    {
                        DisplayName = "Click login",
                        Status = TestStatus.Failed,
                        ErrorCode = ErrorCodes.ActionExecutionFailed,
                        ErrorMessage = "click did not trigger",
                        ScreenshotPath = "Screenshots/failure.png",
                        Attachments = new List<string> { "Screenshots/failure.png" },
                    },
                },
            };

            UIFlowCaseResultPayload payload = UpilotUIFlowService.ToCasePayload(result, yamlPath, reportPath, new ReportPathBuilder());

            Assert.That(payload.yamlPath.Replace('\\', '/'), Does.StartWith("Assets/"));
            Assert.That(payload.reportJsonPath.Replace('\\', '/'), Is.EqualTo($"Reports/upilot/UIFlowMcp/{executionId}/Basic Login.json"));
            Assert.That(payload.reportMarkdownPath.Replace('\\', '/'), Is.EqualTo($"Reports/upilot/UIFlowMcp/{executionId}/Basic Login.md"));
            Assert.That(payload.attachments, Has.Count.EqualTo(1));
            Assert.That(payload.attachments[0].Replace('\\', '/'), Is.EqualTo($"Reports/upilot/UIFlowMcp/{executionId}/Screenshots/top-level.png"));
            Assert.That(payload.stepResults, Has.Count.EqualTo(1));
            Assert.That(payload.stepResults[0].screenshotPath.Replace('\\', '/'), Is.EqualTo($"Reports/upilot/UIFlowMcp/{executionId}/Screenshots/failure.png"));
            Assert.That(payload.stepResults[0].attachments[0].Replace('\\', '/'), Is.EqualTo($"Reports/upilot/UIFlowMcp/{executionId}/Screenshots/failure.png"));
            Assert.That(payload.failedStep, Is.Not.Null);
            Assert.That(payload.failedStep.stepName, Is.EqualTo("Click login"));
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "UpilotUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
