using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using UnityUIFlow.Examples;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowExamplesAcceptanceTests
    {
        [Test]
        public void Parser_ReadsHostWindowDefinition()
        {
            const string yaml = @"
name: Host Window Case
fixture:
  host_window:
    type: UnityUIFlow.Examples.ExampleBasicLoginWindow
    reopen_if_open: true
steps:
  - action: wait
    duration: '10ms'
";

            TestCaseDefinition definition = new YamlTestCaseParser().Parse(yaml, "inline.yaml");

            Assert.That(definition.Fixture.HostWindow, Is.Not.Null);
            Assert.That(definition.Fixture.HostWindow.Type, Is.EqualTo("UnityUIFlow.Examples.ExampleBasicLoginWindow"));
            Assert.That(definition.Fixture.HostWindow.ReopenIfOpen, Is.True);
        }

        [Test]
        public void ExampleAssets_CanBeLoadedFromProject()
        {
            foreach (string uxmlPath in Directory.GetFiles("Assets/Examples/Uxml", "*.uxml", SearchOption.TopDirectoryOnly))
            {
                string normalizedPath = uxmlPath.Replace('\\', '/');
                Assert.That(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(normalizedPath), Is.Not.Null, normalizedPath);
            }

            Assert.That(AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Examples/Uss/ExampleCommon.uss"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator HostWindowManager_ReopensWindowAndClearsDirtyState()
        {
            HostWindowDefinition definition = new HostWindowDefinition
            {
                Type = typeof(ExampleBasicLoginWindow).FullName,
                ReopenIfOpen = true,
            };

            Task<(EditorWindow window, VisualElement root)> openFirstTask = TestHostWindowManager.OpenAsync(definition);
            EditorWindow firstWindow = null;
            VisualElement firstRoot = null;
            yield return UnityUIFlowTestTaskUtility.Await(openFirstTask, tuple =>
            {
                firstWindow = tuple.window;
                firstRoot = tuple.root;
            });

            TextField username = firstRoot.Q<TextField>("username-input");
            Assert.That(username, Is.Not.Null);
            username.value = "dirty-state";

            Task<(EditorWindow window, VisualElement root)> openSecondTask = TestHostWindowManager.OpenAsync(definition);
            EditorWindow secondWindow = null;
            VisualElement secondRoot = null;
            yield return UnityUIFlowTestTaskUtility.Await(openSecondTask, tuple =>
            {
                secondWindow = tuple.window;
                secondRoot = tuple.root;
            });

            TextField reopenedUsername = secondRoot.Q<TextField>("username-input");
            Assert.That(reopenedUsername.value, Is.EqualTo(string.Empty));
            Assert.That(secondWindow, Is.Not.Null);
            Assert.That(Resources.FindObjectsOfTypeAll<ExampleBasicLoginWindow>().Length, Is.EqualTo(1));

            secondWindow.Close();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Example_BasicLogin_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("01-basic-login.yaml");
        }

        [UnityTest]
        public IEnumerator Example_SelectorsAndAssertions_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("02-selectors-and-assertions.yaml");
        }

        [UnityTest]
        public IEnumerator Example_WaitForElement_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("03-wait-for-element.yaml");
        }

        [UnityTest]
        public IEnumerator Example_ConditionalAndLoop_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("04-conditional-and-loop.yaml");
        }

        [UnityTest]
        public IEnumerator Example_DataDrivenCsv_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("05-data-driven-csv.yaml");
        }

        [UnityTest]
        public IEnumerator Example_CustomActionAndJson_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("06-custom-action-and-json.yaml");
        }

        [UnityTest]
        public IEnumerator Example_DoubleClick_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("07-double-click.yaml");
        }

        [UnityTest]
        public IEnumerator Example_PressKey_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("08-press-key.yaml");
        }

        [UnityTest]
        public IEnumerator Example_Hover_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("09-hover.yaml");
        }

        [UnityTest]
        public IEnumerator Example_Drag_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("10-drag.yaml");
        }

        [UnityTest]
        public IEnumerator Example_Scroll_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("11-scroll.yaml");
        }

        [UnityTest]
        public IEnumerator Example_TypeText_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("12-type-text.yaml");
        }

        [UnityTest]
        public IEnumerator Example_AdvancedControls_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("13-advanced-controls.yaml");
        }

        [UnityTest]
        public IEnumerator Example_AllYamlCasesInDirectory_RunSuccessfully()
        {
            string[] yamlFiles = Directory.GetFiles("Assets/Examples/Yaml", "*.yaml", SearchOption.TopDirectoryOnly);
            System.Array.Sort(yamlFiles, System.StringComparer.OrdinalIgnoreCase);

            foreach (string yamlPath in yamlFiles)
            {
                string fileName = Path.GetFileName(yamlPath);
                yield return RunExampleAndAssertPassed(fileName);
            }
        }

        private static IEnumerator RunExampleAndAssertPassed(string fileName)
        {
            Task<TestResult> task = RunExampleAsync(fileName);
            yield return UnityUIFlowTestTaskUtility.Await(task, result =>
            {
                Assert.That(result.Status, Is.EqualTo(TestStatus.Passed), $"{fileName}: {result.ErrorCode} {result.ErrorMessage}");
                Assert.That(result.StepResults, Is.Not.Empty);
            });
        }

        private static Task<TestResult> RunExampleAsync(string fileName)
        {
            var runner = new TestRunner();
            return runner.RunFileAsync(
                Path.GetFullPath(Path.Combine("Assets/Examples/Yaml", fileName)),
                new TestOptions
                {
                    Headed = false,
                    ReportOutputPath = "Reports/Examples",
                    ScreenshotPath = "Reports/Examples/Screenshots",
                    ScreenshotOnFailure = true,
                });
        }

        [UnityTest]
        public IEnumerator Example_EngineReliabilityCombo_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("58-engine-reliability.yaml");
        }

        [UnityTest]
        public IEnumerator Example_NestedLoops_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("59-nested-loops.yaml");
        }

        [UnityTest]
        public IEnumerator Example_HostWindowSwitch_RunsSuccessfully()
        {
            yield return RunExampleAndAssertPassed("60-host-window-switch.yaml");
            yield return RunExampleAndAssertPassed("61-host-window-switch-b.yaml");
        }

        [UnityTest]
        public IEnumerator HostWindowManager_CallsPrepareForAutomatedTest()
        {
            HostWindowDefinition definition = new HostWindowDefinition
            {
                Type = typeof(ExampleBasicLoginWindow).FullName,
                ReopenIfOpen = true,
            };

            Task<(EditorWindow window, VisualElement root)> openTask = TestHostWindowManager.OpenAsync(definition);
            EditorWindow window = null;
            yield return UnityUIFlowTestTaskUtility.Await(openTask, tuple =>
            {
                window = tuple.window;
            });

            Assert.That(window, Is.Not.Null);
            Assert.That(window.titleContent.text, Is.EqualTo(nameof(ExampleBasicLoginWindow)));
            window.Close();
            yield return null;
        }
    }
}
