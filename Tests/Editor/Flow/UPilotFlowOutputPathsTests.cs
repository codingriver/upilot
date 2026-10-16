#if UPILOT_ENABLE_FLOW
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Flow.Tests
{
    public sealed class UPilotFlowOutputPathsTests
    {
        [Test]
        public void DefaultOptionsUseProjectRootAndExplicitOldPathRemainsExplicit()
        {
            string before = Directory.GetCurrentDirectory();
            string directory = Path.Combine(Path.GetTempPath(), "upilot-flow-default-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Directory.SetCurrentDirectory(directory);
                var options = new TestOptions();
                var settings = new UPilotFlowExecutionSettings();
                Assert.That(options.ReportOutputPath, Is.EqualTo(UPilotOutputPaths.Resolve(UPilotOutputPaths.FlowDefaultReports)));
                Assert.That(settings.ReportOutputPath, Is.EqualTo(options.ReportOutputPath));
                Assert.That(options.ScreenshotPath, Is.EqualTo(Path.Combine(options.ReportOutputPath, "Screenshots")));
                settings.ReportOutputPath = "Reports/UPilot/Flow";
                var resolved = UPilotFlowConfigurationService.Resolve(settings);
                Assert.That(resolved.ReportOutputPath, Is.EqualTo("Reports/UPilot/Flow"));
                Assert.That(resolved.ScreenshotPath, Is.EqualTo(Path.Combine("Reports/UPilot/Flow", "Screenshots")));
                Assert.That(Directory.GetFileSystemEntries(directory), Is.Empty);
            }
            finally { Directory.SetCurrentDirectory(before); Directory.Delete(directory, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CaseSingleAndSuiteLinksResolveFromTheirActualReportLocation(bool relativeScreenshot)
        {
            string directory = Path.Combine(Path.GetTempPath(), "upilot-flow-links-" + Guid.NewGuid().ToString("N"));
            string screenshot = Path.Combine(directory, "Screenshots", "shot.png");
            try
            {
                var reporter = new MarkdownReporter(new ReporterOptions {
                    ReportRootPath = directory, ScreenshotRootPath = Path.GetDirectoryName(screenshot), SuiteName = "test" });
                File.WriteAllBytes(screenshot, new byte[] { 1 });
                reporter.RecordStepResult("example", new StepResult {
                    DisplayName = "step", Status = TestStatus.Passed,
                    ScreenshotPath = relativeScreenshot ? "Screenshots/shot.png" : screenshot }, Array.Empty<string>());
                var result = new TestResult { CaseName = "example", Status = TestStatus.Passed,
                    ReportMarkdownPath = Path.Combine(directory, "Cases", "example.md") };
                reporter.WriteCaseReport(result);
                reporter.WriteSingleReport(result);
                reporter.WriteSuiteReport(new TestSuiteResult { CaseResults = { result } });
                Assert.That(File.ReadAllText(result.ReportMarkdownPath), Does.Contain("[View](../Screenshots/shot.png)"));
                Assert.That(File.ReadAllText(Path.Combine(directory, "single_reports.md")), Does.Contain("[View](Screenshots/shot.png)"));
                Assert.That(File.ReadAllText(Path.Combine(directory, "full_reports.md")), Does.Contain("[example.md](Cases/example.md)"));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void UnifiedHistoryDoesNotImportLegacyHistoryOrUseWorkingDirectory()
        {
            string before = Directory.GetCurrentDirectory();
            string directory = Path.Combine(Path.GetTempPath(), "upilot-flow-history-" + Guid.NewGuid().ToString("N"));
            string root = UPilotOutputPaths.Resolve(UPilotOutputPaths.FlowHistory);
            string[] outputs = { Path.Combine(root, "Cases", "unified-history.json"),
                Path.Combine(root, "Cases", "suite-unified.json"), Path.Combine(root, "full_reports.md") };
            byte[][] saved = outputs.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
            string legacyCase = "legacy-" + Guid.NewGuid().ToString("N");
            string currentCase = "current-" + Guid.NewGuid().ToString("N");
            string legacy = Path.Combine(directory, "Reports", "Cases", "unified-history.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacy));
                File.WriteAllText(legacy, JsonUtility.ToJson(new TestSuiteResult {
                    CaseResults = { new TestResult { CaseName = legacyCase } } }));
                byte[] oldBytes = File.ReadAllBytes(legacy);
                Directory.SetCurrentDirectory(directory);
                MarkdownReporter.WriteUnifiedSuiteReport(new TestSuiteResult {
                    CaseResults = { new TestResult { CaseName = currentCase, Status = TestStatus.Passed } } }, false);
                string history = File.ReadAllText(outputs[0]);
                Assert.That(history, Does.Contain(currentCase).And.Not.Contain(legacyCase));
                Assert.That(File.ReadAllBytes(legacy), Is.EqualTo(oldBytes));
                Assert.That(File.Exists(Path.Combine(directory, "Reports", "full_reports.md")), Is.False);
            }
            finally
            {
                Directory.SetCurrentDirectory(before);
                for (int i = 0; i < outputs.Length; i++)
                    if (saved[i] != null) File.WriteAllBytes(outputs[i], saved[i]);
                    else if (File.Exists(outputs[i])) File.Delete(outputs[i]);
                Directory.Delete(directory, true);
            }
        }
    }
}
#endif
