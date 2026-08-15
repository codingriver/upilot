using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using CodingRiver.UPilot.Flow;

namespace CodingRiver.UPilot.tests
{
    [Category("UPilot")]
    public sealed class UPilotFlowServiceTests
    {
        [Test]
        [Category("UPilot")]
        public void ResolveYamlPaths_RejectsMixedModes()
        {
            var payload = new UPilotFlowRunPayload
            {
                yamlPaths = new[] { "Assets/UPilot/Flow/Samples/Yaml/01-basic-login.yaml" },
                yamlDirectory = "Assets/UPilot/Flow/Samples/Yaml",
            };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => UPilotFlowService.ResolveYamlPaths(payload));

            Assert.That(ex.Message, Does.Contain("yamlPaths"));
            Assert.That(ex.Message, Does.Contain("yamlDirectory"));
        }

        [Test]
        [Category("UPilot")]
        public void ResolveYamlPaths_SortsDirectoryResults()
        {
            string directory = CreateTempDirectory();
            string nested = Path.Combine(directory, "Nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(directory, "b-case.yaml"), "name: B");
            File.WriteAllText(Path.Combine(nested, "a-case.yaml"), "name: A");

            var payload = new UPilotFlowRunPayload
            {
                yamlDirectory = directory,
            };

            List<string> resolved = UPilotFlowService.ResolveYamlPaths(payload);

            Assert.That(resolved, Has.Count.EqualTo(2));
            Assert.That(string.Compare(resolved[0], resolved[1], StringComparison.OrdinalIgnoreCase), Is.LessThanOrEqualTo(0));
        }

        [Test]
        [Category("UPilot")]
        public void ToCasePayload_NormalizesRelativeArtifactsIntoProjectRelativePaths()
        {
            string executionId = Guid.NewGuid().ToString("N");
            string reportPath = UPilotFlowService.BuildExecutionReportPath("Reports/UPilot/Flow", executionId);
            string yamlPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "upilot", "UPilot.Flow", "Samples", "Yaml", "01-basic-login.yaml");

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

            UPilotFlowCaseResultPayload payload = UPilotFlowService.ToCasePayload(result, yamlPath, reportPath, new ReportPathBuilder());

            Assert.That(payload.yamlPath.Replace('\\', '/'), Does.StartWith("Assets/"));
            Assert.That(payload.reportJsonPath.Replace('\\', '/'), Is.EqualTo($"Reports/UPilot/Flow/{executionId}/Basic Login.json"));
            Assert.That(payload.reportMarkdownPath.Replace('\\', '/'), Is.EqualTo($"Reports/UPilot/Flow/{executionId}/Basic Login.md"));
            Assert.That(payload.attachments, Has.Count.EqualTo(1));
            Assert.That(payload.attachments[0].Replace('\\', '/'), Is.EqualTo($"Reports/UPilot/Flow/{executionId}/Screenshots/top-level.png"));
            Assert.That(payload.stepResults, Has.Count.EqualTo(1));
            Assert.That(payload.stepResults[0].screenshotPath.Replace('\\', '/'), Is.EqualTo($"Reports/UPilot/Flow/{executionId}/Screenshots/failure.png"));
            Assert.That(payload.stepResults[0].attachments[0].Replace('\\', '/'), Is.EqualTo($"Reports/UPilot/Flow/{executionId}/Screenshots/failure.png"));
            Assert.That(payload.failedStep, Is.Not.Null);
            Assert.That(payload.failedStep.stepName, Is.EqualTo("Click login"));
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "UPilotFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
