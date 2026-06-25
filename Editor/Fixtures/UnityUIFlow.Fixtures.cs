using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    /// <summary>
    /// Base fixture for EditorWindow UI flow tests.
    /// </summary>
    public abstract class UnityUIFlowFixture<TWindow>
        where TWindow : EditorWindow
    {
        private UnityUIFlowSimulationSession _simulationSession;

        /// <summary>
        /// Current test window.
        /// </summary>
        protected TWindow Window { get; private set; }

        /// <summary>
        /// Root visual element of the current window.
        /// </summary>
        protected VisualElement Root { get; private set; }

        /// <summary>
        /// Shared finder instance for the current test.
        /// </summary>
        protected ElementFinder Finder { get; private set; }

        /// <summary>
        /// Shared screenshot manager for the current test.
        /// </summary>
        protected ScreenshotManager Screenshot { get; private set; }

        /// <summary>
        /// Effective options for the current test.
        /// </summary>
        protected TestOptions CurrentOptions { get; private set; }

        /// <summary>
        /// Current execution context when running YAML.
        /// </summary>
        protected ExecutionContext CurrentContext { get; private set; }

        /// <summary>
        /// Last action context created by ExecuteActionAsync.
        /// </summary>
        protected ActionContext LastActionContext { get; private set; }

        /// <summary>
        /// Whether the window is ready.
        /// </summary>
        protected bool IsWindowReady { get; private set; }

        /// <summary>
        /// Last YAML source passed through the fixture.
        /// </summary>
        protected string YamlSource { get; private set; }

        /// <summary>
        /// Creates default test options.
        /// </summary>
        protected virtual TestOptions CreateDefaultOptions()
        {
            return new TestOptions
            {
                Headed = false,
                ReportOutputPath = "Reports",
                ScreenshotPath = "Reports/Screenshots",
                ScreenshotOnFailure = true,
            };
        }

        /// <summary>
        /// Creates the host window and shared tools.
        /// </summary>
        [UnitySetUp]
        public virtual IEnumerator SetUp()
        {
            CurrentOptions = CreateDefaultOptions();
            Window = EditorWindow.GetWindow<TWindow>();
            if (Window == null)
            {
                throw new UnityUIFlowException(ErrorCodes.FixtureWindowCreateFailed, $"测试窗口创建失败：{typeof(TWindow).Name}");
            }

            Window.Show();
            yield return null;

            Root = Window.rootVisualElement;
            if (Root == null)
            {
                throw new UnityUIFlowException(ErrorCodes.FixtureRootMissing, $"测试窗口根节点缺失：{typeof(TWindow).Name}");
            }

            Finder = new ElementFinder();
            Screenshot = new ScreenshotManager(CurrentOptions, () => Window);
            _simulationSession = new UnityUIFlowSimulationSession();
            _simulationSession.BindEditorWindowHost(Window, $"EditorWindow.GetWindow<{typeof(TWindow).Name}>()");
            if (CurrentOptions.RequireOfficialHost && !_simulationSession.HasExecutableOfficialHost)
            {
                throw new UnityUIFlowException(
                    ErrorCodes.FixtureWindowCreateFailed,
                    $"正式验收模式下未能创建官方测试宿主：{typeof(TWindow).Name}");
            }

            IsWindowReady = true;
        }

        /// <summary>
        /// Cleans up the host window and shared tools.
        /// </summary>
        [UnityTearDown]
        public virtual IEnumerator TearDown()
        {
            try
            {
                CurrentContext?.Dispose();
            }
            finally
            {
                try
                {
                    _simulationSession?.Dispose();
                }
                finally
                {
                    _simulationSession = null;
                }

                IsWindowReady = false;
                if (Window != null)
                {
                    Window.Close();
                }

                Window = null;
                Root = null;
                Finder = null;
                Screenshot = null;
                CurrentContext = null;
                LastActionContext = null;
            }

            yield return null;
        }

        /// <summary>
        /// Executes YAML content against the current window root.
        /// </summary>
        protected async Task<TestResult> ExecuteYamlStepsAsync(string yamlContent, string sourcePath = "fixture-inline.yaml")
        {
            if (!IsWindowReady || Root == null)
            {
                throw new UnityUIFlowException(ErrorCodes.FixtureContextNotReady, "测试基座上下文未初始化");
            }

            if (string.IsNullOrWhiteSpace(yamlContent))
            {
                throw new UnityUIFlowException(ErrorCodes.FixtureYamlEmpty, "YAML 内容不能为空");
            }

            YamlSource = yamlContent;
            var runner = new TestRunner();
            return await runner.RunAsync(yamlContent, sourcePath, Root, CurrentOptions, ctx => CurrentContext = ctx);
        }

        /// <summary>
        /// Executes a single action against the current fixture root.
        /// </summary>
        protected Task ExecuteActionAsync(IAction action, Dictionary<string, string> parameters)
        {
            if (!IsWindowReady || Root == null)
            {
                throw new UnityUIFlowException(ErrorCodes.FixtureContextNotReady, "Fixture context is not ready.");
            }

            var context = new ActionContext
            {
                Root = Root,
                Finder = Finder,
                Options = CurrentOptions,
                CurrentCaseName = "Fixture Action Test",
                CurrentStepId = "step-1",
                CurrentStepIndex = 1,
                CancellationToken = System.Threading.CancellationToken.None,
                ScreenshotManager = Screenshot,
                RuntimeController = new RuntimeController(),
                Simulator = _simulationSession?.PointerDriver,
                SimulationSession = _simulationSession,
            };

            LastActionContext = context;
            return action.ExecuteAsync(Root, context, parameters);
        }
    }
}
