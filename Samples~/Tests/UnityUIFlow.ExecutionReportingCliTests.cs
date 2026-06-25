using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowExecutionFixtureTests : UnityUIFlowFixture<SampleLoginWindow>
    {
        [UnityTest]
        public IEnumerator ContinueOnStepFailure_RunsTeardownAndMarksCaseFailed()
        {
            const string yaml = @"
name: Continue On Step Failure
fixture:
  teardown:
    - action: click
      selector: '#reset-button'
steps:
  - action: type_text_fast
    selector: '#username-input'
    value: 'alice'
  - action: type_text_fast
    selector: '#password-input'
    value: 'secret'
  - action: assert_text
    selector: '#status-label'
    expected: 'Should Fail'
";

            CurrentOptions.ContinueOnStepFailure = false;

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(yaml, "continue-on-step-failure.yaml"), result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Failed));
                Assert.That(Root.Q<TextField>("username-input").value, Is.EqualTo(string.Empty));
                Assert.That(Root.Q<TextField>("password-input").value, Is.EqualTo(string.Empty));
            });
        }

        [UnityTest]
        public IEnumerator StepTimeout_MarksStepAsFailedWithTimeoutCode()
        {
            const string yaml = @"
name: Timeout Case
steps:
  - name: Short timeout wait
    action: wait
    duration: '500ms'
    timeout: '100ms'
";

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(yaml, "timeout-case.yaml"), result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Failed));
                Assert.That(result.StepResults[0].ErrorCode, Is.EqualTo(ErrorCodes.StepTimeout));
            });
        }

        [UnityTest]
        public IEnumerator Fixture_CapturesCurrentContextDuringYamlExecution()
        {
            const string yaml = @"
name: Fixture Context
steps:
  - action: wait
    duration: '16ms'
";

            yield return UnityUIFlowTestTaskUtility.Await(ExecuteYamlStepsAsync(yaml, "fixture-context.yaml"), result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed));
                Assert.That(CurrentContext, Is.Not.Null);
                Assert.That(CurrentContext.Root, Is.SameAs(Root));
                Assert.That(CurrentContext.Finder, Is.Not.Null);
            });
        }
    }

    public sealed class UnityUIFlowReportingAndCliTests
    {
        [Test]
        public void MarkdownReporter_WritesCaseSuiteAndArtifactManifest()
        {
            string reportRoot = CreateTempDirectory();
            string screenshotRoot = Path.Combine(reportRoot, "Screenshots");

            var reporter = new MarkdownReporter(new ReporterOptions
            {
                ReportRootPath = reportRoot,
                ScreenshotRootPath = screenshotRoot,
                SuiteName = "editor-suite",
            });

            string screenshotPath = Path.Combine(screenshotRoot, "case-001.png");
            Directory.CreateDirectory(screenshotRoot);
            File.WriteAllText(screenshotPath, "png");

            var step = new StepResult
            {
                DisplayName = "Click Login",
                Status = TestStatus.Passed,
                StartedAtUtc = "2026-04-09T00:00:00.0000000Z",
                EndedAtUtc = "2026-04-09T00:00:01.0000000Z",
                DurationMs = 1000,
                ScreenshotPath = "Screenshots/case-001.png",
                ScreenshotSource = ScreenshotManager.SourceWindowReadScreenPixel,
                HostDriver = "OfficialEditorWindowPanelSimulator",
                PointerDriver = "PanelSimulator",
                KeyboardDriver = "PanelSimulator",
                DriverDetails = "host=OfficialEditorWindowPanelSimulator; pointer=PanelSimulator; keyboard=PanelSimulator; official=UnityEditor.UIElements.TestFramework.EditorWindowUITestFixture`1 + UnityEditor.UIElements.TestFramework.EditorWindowPanelSimulator + UnityEngine.UIElements.TestFramework.PanelSimulator (available via com.unity.ui.test-framework)",
                Attachments = new List<string> { "Screenshots/case-001.png" },
            };

            var caseResult = new TestResult
            {
                CaseName = "Reporter Case",
                Status = TestStatus.Passed,
                StartedAtUtc = "2026-04-09T00:00:00.0000000Z",
                EndedAtUtc = "2026-04-09T00:00:01.0000000Z",
                DurationMs = 1000,
                StepResults = new List<StepResult> { step },
            };

            reporter.RecordStepResult(caseResult.CaseName, step, step.Attachments);
            reporter.WriteCaseReport(caseResult);
            reporter.WriteSuiteReport(new TestSuiteResult
            {
                Total = 1,
                Passed = 1,
                CaseResults = new List<TestResult> { caseResult },
            });

            new CiArtifactManifestWriter().Write(reportRoot);

            Assert.That(File.Exists(Path.Combine(reportRoot, "Reporter Case.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(reportRoot, "Reporter Case.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(reportRoot, "suite-editor-suite.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(reportRoot, "suite-editor-suite.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(reportRoot, "artifacts.json")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(reportRoot, "Reporter Case.md")), Does.Contain("H=OfficialEditorWindowPanelSimulator; P=PanelSimulator; K=PanelSimulator"));
            Assert.That(File.ReadAllText(Path.Combine(reportRoot, "Reporter Case.md")), Does.Contain("host=OfficialEditorWindowPanelSimulator; pointer=PanelSimulator; keyboard=PanelSimulator"));
            Assert.That(File.ReadAllText(Path.Combine(reportRoot, "Reporter Case.md")), Does.Contain(ScreenshotManager.SourceWindowReadScreenPixel));
        }

        [Test]
        public void CommandLineParser_PrefersCliValuesOverConfig()
        {
            string tempDir = CreateTempDirectory();
            string configPath = Path.Combine(tempDir, ".unityuiflow.json");
            File.WriteAllText(configPath, @"
headed: true
reportPath: ConfigReports
screenshotOnFailure: false
defaultTimeoutMs: 1500
preStepDelayMs: 80
requireOfficialHost: true
requireOfficialPointerDriver: true
requireInputSystemKeyboardDriver: true
");

            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.configFile", configPath,
                "-unityUIFlow.headed", "false",
                "-unityUIFlow.reportPath", "CliReports",
                "-unityUIFlow.screenshotOnFailure", "true",
                "-unityUIFlow.defaultTimeoutMs", "3000",
                "-unityUIFlow.preStepDelayMs", "120",
                "-unityUIFlow.requireOfficialHost", "false",
                "-unityUIFlow.requireOfficialPointerDriver", "false",
                "-unityUIFlow.requireInputSystemKeyboardDriver", "false",
            };

            CliOptions options = new CommandLineOptionsParser().Parse(args);

            Assert.That(options.Headed, Is.False);
            Assert.That(options.ReportPath, Is.EqualTo("CliReports"));
            Assert.That(options.ScreenshotOnFailure, Is.True);
            Assert.That(options.DefaultTimeoutMs, Is.EqualTo(3000));
            Assert.That(options.PreStepDelayMs, Is.EqualTo(120));
            Assert.That(options.RequireOfficialHost, Is.False);
            Assert.That(options.RequireOfficialPointerDriver, Is.False);
            Assert.That(options.RequireInputSystemKeyboardDriver, Is.False);
            Assert.That(options.ScreenshotPath, Is.EqualTo(Path.Combine("CliReports", "Screenshots")));
        }

        [Test]
        public void CommandLineParser_PrefersEnvironmentValuesOverConfig()
        {
            string tempDir = CreateTempDirectory();
            string configPath = Path.Combine(tempDir, "env-config.json");
            File.WriteAllText(configPath, @"
headed: true
reportPath: ConfigReports
screenshotOnFailure: false
defaultTimeoutMs: 1500
");

            var environment = new Dictionary<string, string>
            {
                ["UNITY_UI_FLOW_CONFIG_FILE"] = configPath,
                ["UNITY_UI_FLOW_HEADED"] = "false",
                ["UNITY_UI_FLOW_REPORT_PATH"] = "EnvReports",
                ["UNITY_UI_FLOW_SCREENSHOT_ON_FAILURE"] = "true",
                ["UNITY_UI_FLOW_DEFAULT_TIMEOUT_MS"] = "2200",
            };

            CliOptions options = new CommandLineOptionsParser().Parse(new[] { "Unity.exe" }, environment);

            Assert.That(options.ConfigFile, Is.EqualTo(configPath));
            Assert.That(options.Headed, Is.False);
            Assert.That(options.ReportPath, Is.EqualTo("EnvReports"));
            Assert.That(options.ScreenshotOnFailure, Is.True);
            Assert.That(options.DefaultTimeoutMs, Is.EqualTo(2200));
            Assert.That(options.ScreenshotPath, Is.EqualTo(Path.Combine("EnvReports", "Screenshots")));
        }

        [Test]
        public void CommandLineParser_PrefersCliValuesOverEnvironment()
        {
            var environment = new Dictionary<string, string>
            {
                ["UNITY_UI_FLOW_HEADED"] = "false",
                ["UNITY_UI_FLOW_REPORT_PATH"] = "EnvReports",
                ["UNITY_UI_FLOW_DEFAULT_TIMEOUT_MS"] = "2200",
            };

            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.headed", "true",
                "-unityUIFlow.reportPath", "CliReports",
                "-unityUIFlow.defaultTimeoutMs", "3300",
            };

            CliOptions options = new CommandLineOptionsParser().Parse(args, environment);

            Assert.That(options.Headed, Is.True);
            Assert.That(options.ReportPath, Is.EqualTo("CliReports"));
            Assert.That(options.DefaultTimeoutMs, Is.EqualTo(3300));
        }

        [Test]
        public void CommandLineParser_ToTestOptions_MapsStrictFlags()
        {
            CliOptions cliOptions = new CommandLineOptionsParser().Parse(new[]
            {
                "Unity.exe",
                "-unityUIFlow.requireOfficialHost", "true",
                "-unityUIFlow.requireOfficialPointerDriver", "true",
                "-unityUIFlow.requireInputSystemKeyboardDriver", "true",
                "-unityUIFlow.preStepDelayMs", "240",
            });

            TestOptions options = new CommandLineOptionsParser().ToTestOptions(cliOptions);

            Assert.That(options.RequireOfficialHost, Is.True);
            Assert.That(options.RequireOfficialPointerDriver, Is.True);
            Assert.That(options.RequireInputSystemKeyboardDriver, Is.True);
            Assert.That(options.PreStepDelayMs, Is.EqualTo(240));
        }

        [Test]
        public void CommandLineParser_RejectsDuplicateArguments()
        {
            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.headed", "true",
                "-unityUIFlow.headed", "false",
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new CommandLineOptionsParser().Parse(args));

            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliArgumentInvalid));
        }

        [Test]
        public void CommandLineParser_RejectsExplicitYamlPathAndDirectoryTogether()
        {
            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.yamlPath", "Assets/Examples/Yaml/01-basic-login.yaml",
                "-unityUIFlow.yamlDirectory", "Assets/Examples/Yaml",
            };

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new CommandLineOptionsParser().Parse(args));

            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliArgumentInvalid));
        }

        [Test]
        public void CommandLineParser_ReadsExplicitYamlPath()
        {
            string[] args =
            {
                "Unity.exe",
                "-unityUIFlow.yamlPath", "Assets/Examples/Yaml/01-basic-login.yaml",
            };

            CliOptions options = new CommandLineOptionsParser().Parse(args);

            Assert.That(options.YamlPath, Is.EqualTo("Assets/Examples/Yaml/01-basic-login.yaml"));
            Assert.That(options.YamlDirectory, Is.Null);
        }

        [Test]
        public void ProjectSettings_AlwaysEnableVerboseLog_OverridesRuntimeFlags()
        {
            UnityUIFlowProjectSettings settings = UnityUIFlowProjectSettings.instance;
            bool previousVerbose = settings.AlwaysEnableVerboseLog;
            int previousDelay = settings.PreStepDelayMs;
            bool previousRequireOfficialHost = settings.RequireOfficialHostByDefault;
            bool previousRequireOfficialPointerDriver = settings.RequireOfficialPointerDriverByDefault;
            bool previousRequireInputSystemKeyboardDriver = settings.RequireInputSystemKeyboardDriverByDefault;

            try
            {
                settings.AlwaysEnableVerboseLog = true;
                settings.PreStepDelayMs = 1000;
                settings.RequireOfficialHostByDefault = true;
                settings.RequireOfficialPointerDriverByDefault = true;
                settings.RequireInputSystemKeyboardDriverByDefault = true;

                TestOptions resolved = UnityUIFlowProjectSettingsUtility.ApplyOverrides(new TestOptions
                {
                    Headed = true,
                    EnableVerboseLog = false,
                    PreStepDelayMs = 0,
                    RequireOfficialHost = false,
                    RequireOfficialPointerDriver = false,
                    RequireInputSystemKeyboardDriver = false,
                });

                Assert.That(resolved.EnableVerboseLog, Is.True);
                Assert.That(resolved.PreStepDelayMs, Is.EqualTo(1000));
                Assert.That(resolved.RequireOfficialHost, Is.True);
                Assert.That(resolved.RequireOfficialPointerDriver, Is.True);
                Assert.That(resolved.RequireInputSystemKeyboardDriver, Is.True);
            }
            finally
            {
                settings.AlwaysEnableVerboseLog = previousVerbose;
                settings.PreStepDelayMs = previousDelay;
                settings.RequireOfficialHostByDefault = previousRequireOfficialHost;
                settings.RequireOfficialPointerDriverByDefault = previousRequireOfficialPointerDriver;
                settings.RequireInputSystemKeyboardDriverByDefault = previousRequireInputSystemKeyboardDriver;
            }
        }

        [Test]
        public void ProjectSettings_ProjectDelay_DoesNotOverrideNonHeadedRuns()
        {
            UnityUIFlowProjectSettings settings = UnityUIFlowProjectSettings.instance;
            bool previousVerbose = settings.AlwaysEnableVerboseLog;
            int previousDelay = settings.PreStepDelayMs;
            bool previousRequireOfficialHost = settings.RequireOfficialHostByDefault;
            bool previousRequireOfficialPointerDriver = settings.RequireOfficialPointerDriverByDefault;
            bool previousRequireInputSystemKeyboardDriver = settings.RequireInputSystemKeyboardDriverByDefault;

            try
            {
                settings.AlwaysEnableVerboseLog = false;
                settings.PreStepDelayMs = 1000;
                settings.RequireOfficialHostByDefault = false;
                settings.RequireOfficialPointerDriverByDefault = false;
                settings.RequireInputSystemKeyboardDriverByDefault = false;

                TestOptions resolved = UnityUIFlowProjectSettingsUtility.ApplyOverrides(new TestOptions
                {
                    Headed = false,
                    EnableVerboseLog = false,
                    PreStepDelayMs = 0,
                });

                Assert.That(resolved.EnableVerboseLog, Is.False);
                Assert.That(resolved.PreStepDelayMs, Is.EqualTo(0));
            }
            finally
            {
                settings.AlwaysEnableVerboseLog = previousVerbose;
                settings.PreStepDelayMs = previousDelay;
                settings.RequireOfficialHostByDefault = previousRequireOfficialHost;
                settings.RequireOfficialPointerDriverByDefault = previousRequireOfficialPointerDriver;
                settings.RequireInputSystemKeyboardDriverByDefault = previousRequireInputSystemKeyboardDriver;
            }
        }

        [Test]
        public void ProjectSettings_KeepRuntimeDelayWhenProjectDelayDisabled()
        {
            UnityUIFlowProjectSettings settings = UnityUIFlowProjectSettings.instance;
            bool previousVerbose = settings.AlwaysEnableVerboseLog;
            int previousDelay = settings.PreStepDelayMs;
            bool previousRequireOfficialHost = settings.RequireOfficialHostByDefault;
            bool previousRequireOfficialPointerDriver = settings.RequireOfficialPointerDriverByDefault;
            bool previousRequireInputSystemKeyboardDriver = settings.RequireInputSystemKeyboardDriverByDefault;

            try
            {
                settings.AlwaysEnableVerboseLog = false;
                settings.PreStepDelayMs = 0;
                settings.RequireOfficialHostByDefault = false;
                settings.RequireOfficialPointerDriverByDefault = false;
                settings.RequireInputSystemKeyboardDriverByDefault = false;

                TestOptions resolved = UnityUIFlowProjectSettingsUtility.ApplyOverrides(new TestOptions
                {
                    EnableVerboseLog = false,
                    PreStepDelayMs = 250,
                    RequireOfficialHost = true,
                    RequireOfficialPointerDriver = true,
                    RequireInputSystemKeyboardDriver = true,
                });

                Assert.That(resolved.EnableVerboseLog, Is.False);
                Assert.That(resolved.PreStepDelayMs, Is.EqualTo(250));
                Assert.That(resolved.RequireOfficialHost, Is.True);
                Assert.That(resolved.RequireOfficialPointerDriver, Is.True);
                Assert.That(resolved.RequireInputSystemKeyboardDriver, Is.True);
            }
            finally
            {
                settings.AlwaysEnableVerboseLog = previousVerbose;
                settings.PreStepDelayMs = previousDelay;
                settings.RequireOfficialHostByDefault = previousRequireOfficialHost;
                settings.RequireOfficialPointerDriverByDefault = previousRequireOfficialPointerDriver;
                settings.RequireInputSystemKeyboardDriverByDefault = previousRequireInputSystemKeyboardDriver;
            }
        }

        [Test]
        public void YamlTestCaseFilter_MatchesFileNameAndCaseName()
        {
            Assert.That(YamlTestCaseFilter.Match("01-*", "Assets/Examples/Yaml/sample-01-basic-login.yaml", "Basic Login"), Is.True);
            Assert.That(YamlTestCaseFilter.Match("*Selectors", "Assets/Examples/Yaml/03-assertions-and-selectors.yaml", "Assertions And Selectors"), Is.True);
            Assert.That(YamlTestCaseFilter.Match("NoMatch*", "Assets/Examples/Yaml/sample-01-basic-login.yaml", "Basic Login"), Is.False);
        }

        [Test]
        public void CustomActionWhitelist_LoadsConfiguredAssemblies()
        {
            HashSet<string> assemblies = UnityUIFlowConfigResolver.GetCustomActionAssemblyWhitelist();
            var registry = new ActionRegistry();

            Assert.That(assemblies, Does.Contain("Assembly-CSharp"));
            Assert.That(assemblies, Does.Contain("UnityUIFlow.Tests"));
            Assert.That(registry.HasAction("custom_login"), Is.True);
        }

        [Test]
        public void ExitCodeResolver_PrioritizesErrorsOverFailures()
        {
            Assert.That(ExitCodeResolver.Resolve(new TestSuiteResult { Failed = 1 }), Is.EqualTo(1));
            Assert.That(ExitCodeResolver.Resolve(new TestSuiteResult { Errors = 1, Failed = 1 }), Is.EqualTo(2));
            Assert.That(ExitCodeResolver.Resolve(new TestSuiteResult { Passed = 1 }), Is.EqualTo(0));
        }

        [Test]
        public void ActionContext_AddAttachment_CapsAtTen()
        {
            var context = new ActionContext();

            for (int index = 0; index < 12; index++)
            {
                context.AddAttachment($"attachment-{index}.png");
            }

            Assert.That(context.CurrentAttachments, Has.Count.EqualTo(10));
            Assert.That(context.CurrentAttachments[0], Is.EqualTo("attachment-0.png"));
            Assert.That(context.CurrentAttachments[9], Is.EqualTo("attachment-9.png"));
        }

        [Test]
        public void ConfigFileLoader_ReturnsEmptyForMissingFile()
        {
            var loader = new ConfigFileLoader();
            Dictionary<string, object> result = loader.Load(Path.Combine(Path.GetTempPath(), "nonexistent-config.json"));
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ConfigFileLoader_ThrowsOnInvalidYaml()
        {
            string tempDir = CreateTempDirectory();
            string badPath = Path.Combine(tempDir, "bad-config.json");
            File.WriteAllText(badPath, "{ not valid json");

            UnityUIFlowException ex = Assert.Throws<UnityUIFlowException>(() => new ConfigFileLoader().Load(badPath));
            Assert.That(ex.ErrorCode, Is.EqualTo(ErrorCodes.CliConfigFileInvalid));
        }

        [Test]
        public void JsonResultWriter_ProducesValidJson()
        {
            string tempDir = CreateTempDirectory();
            string path = Path.Combine(tempDir, "case.json");
            var result = new TestResult
            {
                CaseName = "Json Case",
                Status = TestStatus.Passed,
                StepResults = new List<StepResult>
                {
                    new StepResult { DisplayName = "Step1", Status = TestStatus.Passed },
                },
            };

            new JsonResultWriter().WriteCaseJson(result, path);
            string content = File.ReadAllText(path);
            Assert.That(content, Does.Contain("\"CaseName\""));
            Assert.That(content, Does.Contain("Json Case"));
            Assert.That(content, Does.Contain("\"StepResults\""));
        }

        [Test]
        public void CommandLineParser_ParsesNographicsFlag()
        {
            CliOptions options = new CommandLineOptionsParser().Parse(new[] { "Unity.exe", "-nographics" });
            Assert.That(options.Nographics, Is.True);

            CliOptions options2 = new CommandLineOptionsParser().Parse(new[] { "Unity.exe" });
            Assert.That(options2.Nographics, Is.False);
        }

        [Test]
        public void YamlTestCaseFilter_EmptyFilterMatchesAll()
        {
            Assert.That(YamlTestCaseFilter.Match(string.Empty, "case.yaml", "Case Name"), Is.True);
            Assert.That(YamlTestCaseFilter.Match(null, "case.yaml", "Case Name"), Is.True);
        }

        [Test]
        public void YamlTestCaseFilter_WildcardMatchesAny()
        {
            Assert.That(YamlTestCaseFilter.Match("*", "any.yaml", "Anything"), Is.True);
            Assert.That(YamlTestCaseFilter.Match("A*", "alpha.yaml", "Beta"), Is.True);
            Assert.That(YamlTestCaseFilter.Match("Z*", "alpha.yaml", "Beta"), Is.False);
        }

        [Test]
        public void CommandLineParser_ResolvesComplexPriority()
        {
            string tempDir = CreateTempDirectory();
            string configPath = Path.Combine(tempDir, ".unityuiflow.json");
            File.WriteAllText(configPath, "headed: true\nreportPath: Config\ndefaultTimeoutMs: 2000");

            var env = new Dictionary<string, string>
            {
                ["UNITY_UI_FLOW_CONFIG_FILE"] = configPath,
                ["UNITY_UI_FLOW_HEADED"] = "false",
                ["UNITY_UI_FLOW_REPORT_PATH"] = "Env",
            };

            string[] args = { "Unity.exe", "-unityUIFlow.headed", "true", "-unityUIFlow.reportPath", "Cli" };
            CliOptions options = new CommandLineOptionsParser().Parse(args, env);

            Assert.That(options.Headed, Is.True);
            Assert.That(options.ReportPath, Is.EqualTo("Cli"));
            Assert.That(options.DefaultTimeoutMs, Is.EqualTo(2000));
        }

        [Test]
        public void ReportPathBuilder_BuildsExpectedPaths()
        {
            var builder = new ReportPathBuilder();
            string root = CreateTempDirectory();
            string screenshot = builder.BuildScreenshotPath(root, "Case/Name", 5, "tag");
            Assert.That(Path.GetDirectoryName(screenshot), Is.EqualTo(root));
            Assert.That(Path.GetFileName(screenshot), Does.StartWith("Case_Name-005-tag-"));

            string md = builder.BuildCaseMarkdownPath(root, "Case|Name");
            Assert.That(Path.GetFileName(md), Is.EqualTo("Case_Name.md"));

            string suiteMd = builder.BuildSuiteMarkdownPath(root, "Suite");
            Assert.That(Path.GetFileName(suiteMd), Is.EqualTo("suite-Suite.md"));
        }

        [UnityTest]
        public IEnumerator CliEntry_RunAllAsync_RunsSingleYamlAndProducesArtifacts()
        {
            string tempDir = CreateTempDirectory();
            string yamlPath = Path.Combine(tempDir, "cli-case.yaml");
            File.WriteAllText(yamlPath, @"
name: CLI Smoke
fixture:
  host_window:
    type: SampleLoginWindow
    reopen_if_open: true
steps:
  - action: wait
    duration: '10ms'
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

            Assert.That(task.Result, Is.EqualTo(0));
            Assert.That(File.Exists(Path.Combine(tempDir, "artifacts.json")), Is.True);
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "UnityUIFlowTests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
