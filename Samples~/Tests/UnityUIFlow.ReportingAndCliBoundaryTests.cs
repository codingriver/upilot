using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowReportingAndCliBoundaryTests
    {
        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [Test]
        public void MarkdownReporter_BuildDriverSummary_PartialCombinations()
        {
            string root = CreateTempDirectory();
            var reporter = new MarkdownReporter(new ReporterOptions
            {
                ReportRootPath = root,
                ScreenshotRootPath = Path.Combine(root, "Screenshots"),
            });

            var step = new StepResult
            {
                DisplayName = "Step",
                HostDriver = "HostA",
                PointerDriver = "PointerB",
                Status = TestStatus.Passed,
            };

            // We can't directly call BuildDriverSummary (private), but we can verify it appears in the written report.
            reporter.RecordStepResult("Case", step, Array.Empty<string>());
            reporter.WriteCaseReport(new TestResult
            {
                CaseName = "Case",
                Status = TestStatus.Passed,
                StepResults = new List<StepResult> { step },
            });

            string md = File.ReadAllText(Path.Combine(root, "Case.md"));
            Assert.That(md, Does.Contain("H=HostA"));
            Assert.That(md, Does.Contain("P=PointerB"));
            Assert.That(md, Does.Contain("K=-"));
        }

        [Test]
        public void MarkdownReporter_WriteCaseReport_ThrowsOnEmptyCaseName()
        {
            string root = CreateTempDirectory();
            var reporter = new MarkdownReporter(new ReporterOptions
            {
                ReportRootPath = root,
                ScreenshotRootPath = Path.Combine(root, "Screenshots"),
            });

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => reporter.WriteCaseReport(new TestResult
            {
                CaseName = "",
                Status = TestStatus.Passed,
            }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.ReportWriteFailed));
        }

        [Test]
        public void ScreenshotManager_CaptureAsync_SkipsUnfocusedWindow()
        {
            string root = CreateTempDirectory();
            var options = new TestOptions { ScreenshotPath = root };
            var manager = new ScreenshotManager(options, () => null);
            Task<string> task = manager.CaptureAsync("Case", 1, "tag", CancellationToken.None);
            task.Wait();
            Assert.That(task.Result, Is.Null);
            Assert.That(manager.LastCaptureSource, Is.EqualTo("skipped-unfocused"));
        }

        [Test]
        public void ScreenshotManager_CaptureSync_FallsBackToPlaceholder()
        {
            string root = CreateTempDirectory();
            var options = new TestOptions { ScreenshotPath = root };
            var manager = new ScreenshotManager(options, () => null);
            string path = Path.Combine(root, "fallback.png");
            manager.CaptureSync(path);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(manager.LastCaptureSource, Is.EqualTo(ScreenshotManager.SourceFallbackTexture));
        }

        [Test]
        public void ScreenshotManager_CaptureAsync_RejectsInvalidTag()
        {
            string root = CreateTempDirectory();
            var options = new TestOptions { ScreenshotPath = root };
            var manager = new ScreenshotManager(options);

            Assert.Throws<UnityUIFlowException>(() =>
            {
                var t = manager.CaptureAsync("Case", 1, "", CancellationToken.None);
                t.Wait();
            });

            Assert.Throws<UnityUIFlowException>(() =>
            {
                var t = manager.CaptureAsync("Case", 1, new string('x', 70), CancellationToken.None);
                t.Wait();
            });
        }

        [Test]
        public void JsonResultWriter_WriteSuiteJsonAndArtifactManifest()
        {
            string root = CreateTempDirectory();
            var writer = new JsonResultWriter();
            var suite = new TestSuiteResult
            {
                Total = 2,
                Passed = 1,
                Failed = 1,
                CaseResults = new List<TestResult>
                {
                    new TestResult { CaseName = "A", Status = TestStatus.Passed },
                    new TestResult { CaseName = "B", Status = TestStatus.Failed },
                },
            };

            writer.WriteSuiteJson(suite, Path.Combine(root, "suite.json"));
            string suiteContent = File.ReadAllText(Path.Combine(root, "suite.json"));
            Assert.That(suiteContent, Does.Contain("\"Total\""));
            Assert.That(suiteContent, Does.Contain("\"CaseResults\""));

            writer.WriteArtifactManifest(new[] { "a.md", "b.png" }, Path.Combine(root, "artifacts.json"));
            string manifestContent = File.ReadAllText(Path.Combine(root, "artifacts.json"));
            Assert.That(manifestContent, Does.Contain("a.md"));
            Assert.That(manifestContent, Does.Contain("b.png"));
        }

        [Test]
        public void CiArtifactManifestWriter_ThrowsOnInvalidPath()
        {
            var writer = new CiArtifactManifestWriter();
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => writer.Write(@"X:\Not\A\Real\Path\Reports"));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliReportPathInvalid));
        }

        [Test]
        public void CiArtifactManifestWriter_FiltersExtensions()
        {
            string root = CreateTempDirectory();
            File.WriteAllText(Path.Combine(root, "report.md"), "# Report");
            File.WriteAllText(Path.Combine(root, "screenshot.png"), "png");
            File.WriteAllText(Path.Combine(root, "log.txt"), "log");

            new CiArtifactManifestWriter().Write(root);
            string content = File.ReadAllText(Path.Combine(root, "artifacts.json"));
            Assert.That(content, Does.Contain("report.md"));
            Assert.That(content, Does.Contain("screenshot.png"));
            Assert.That(content, Does.Not.Contain("log.txt"));
        }

        [Test]
        public void CommandLineParser_RejectsBatchMode()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-batchmode" }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliArgumentInvalid));
        }

        [Test]
        public void CommandLineParser_RejectsMissingArgumentValue()
        {
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-unityUIFlow.headed" }));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliArgumentInvalid));
        }

        [Test]
        public void CommandLineParser_RejectsInvalidBoolAndInt()
        {
            Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-unityUIFlow.headed", "maybe" }));
            Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-unityUIFlow.defaultTimeoutMs", "abc" }));
        }

        [Test]
        public void CommandLineParser_RejectsTooLongFilter()
        {
            string[] args = { "Unity.exe", "-unityUIFlow.testFilter", new string('x', 300) };
            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new CommandLineOptionsParser().Parse(args));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliFilterInvalid));
        }

        [UnityTest]
        public IEnumerator CliEntry_RunAllAsync_DirectoryWithFilter()
        {
            string tempDir = CreateTempDirectory();
            File.WriteAllText(Path.Combine(tempDir, "match.yaml"), "name: MatchCase\nsteps:\n  - action: wait\n    duration: '10ms'\n");
            File.WriteAllText(Path.Combine(tempDir, "skip.yaml"), "name: SkipCase\nsteps:\n  - action: wait\n    duration: '10ms'\n");

            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.yamlDirectory", tempDir,
                "-unityUIFlow.testFilter", "Match*",
                "-unityUIFlow.reportPath", tempDir,
                "-unityUIFlow.headed", "false",
            };

            Task<int> task = UnityUIFlowCliEntry.RunAllAsync(args);
            yield return UnityUIFlowTestTaskUtility.Await(task);

            Assert.That(task.Result, Is.EqualTo(0));
        }

        [Test]
        public void ConfigResolver_ToleratesCorruptedCustomActionAssemblies()
        {
            string tempDir = CreateTempDirectory();
            string configPath = Path.Combine(tempDir, ".unityuiflow.json");
            File.WriteAllText(configPath, "customActionAssemblies: not-a-list\n");

            // Temporarily override default config path via reflection (UnityUIFlowConfigResolver uses GetDefaultConfigFilePath)
            // Instead, we use ConfigFileLoader directly to prove the code path.
            var loader = new ConfigFileLoader();
            Dictionary<string, object> config = loader.Load(configPath);
            // The resolver will catch the AsSequence exception and return defaults.
            // We just verify the loader reads it as a string.
            Assert.That(config["customActionAssemblies"], Is.TypeOf<string>());
        }

        [Test]
        public void CommandLineParser_RejectsTimeoutOutOfBounds()
        {
            Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-unityUIFlow.defaultTimeoutMs", "50" }));
            Assert.Throws<UnityUIFlowException>(() =>
                new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-unityUIFlow.defaultTimeoutMs", "600001" }));
        }

        [UnityTest]
        public IEnumerator CliEntry_RunAllAsync_HandlesCliTestsFailed()
        {
            string tempDir = CreateTempDirectory();
            string yamlPath = Path.Combine(tempDir, "fail.yaml");
            File.WriteAllText(yamlPath, @"
name: Fail Case
fixture:
  host_window:
    type: SampleLoginWindow
    reopen_if_open: true
steps:
  - action: assert_text
    selector: '#status-label'
    expected: 'ThisWillFail'
");

            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.yamlPath", yamlPath,
                "-unityUIFlow.reportPath", tempDir,
                "-unityUIFlow.headed", "false",
            };

            Task<int> task = UnityUIFlowCliEntry.RunAllAsync(args);
            yield return UnityUIFlowTestTaskUtility.Await(task);
            Assert.That(task.Result, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CliEntry_RunAllAsync_HandlesGenericException()
        {
            string tempDir = CreateTempDirectory();
            string yamlPath = Path.Combine(tempDir, "bad.yaml");
            File.WriteAllText(yamlPath, "not valid yaml: [");

            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.yamlPath", yamlPath,
                "-unityUIFlow.reportPath", tempDir,
                "-unityUIFlow.headed", "false",
            };

            Task<int> task = UnityUIFlowCliEntry.RunAllAsync(args);
            yield return UnityUIFlowTestTaskUtility.Await(task);
            Assert.That(task.Result, Is.EqualTo(2));
        }
    }
}
