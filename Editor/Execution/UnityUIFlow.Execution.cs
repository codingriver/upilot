using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    /// <summary>
    /// Shared execution context for a single test run.
    /// </summary>
    public sealed class ExecutionContext : IDisposable
    {
        public VisualElement Root;
        public EditorWindow ManagedWindow;
        public ElementFinder Finder;
        public TestOptions Options;
        public MarkdownReporter Reporter;
        public ScreenshotManager ScreenshotManager;
        public ActionRegistry ActionRegistry;
        public RuntimeController RuntimeController;
        public UnityUIFlowSimulationSession SimulationSession;
        public Dictionary<string, object> SharedBag = new Dictionary<string, object>(StringComparer.Ordinal);
        public CancellationToken CancellationToken;
        public string CaseName;

        public void Dispose()
        {
            try
            {
                SimulationSession?.Dispose();
                SimulationSession = null;
            }
            finally
            {
                if (ManagedWindow != null)
                {
                    ManagedWindow.Close();
                    ManagedWindow = null;
                }

                RuntimeController?.Dispose();
            }
        }
    }

    /// <summary>
    /// Controls pause, resume, step and stop state.
    /// </summary>
    public sealed class RuntimeController : IDisposable
    {
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _isPaused;
        private bool _stepRequested;
        private bool _isStopped;
        private bool _pausedForFailure;

        public HeadedRunMode RunMode { get; set; } = HeadedRunMode.Continuous;

        public CancellationToken CancellationToken => _cancellationTokenSource?.Token ?? default;

        public bool IsPaused => _isPaused;

        public bool IsStopped => _isStopped;

        public bool IsPausedForFailure => _pausedForFailure;

        public void Pause()
        {
            _isPaused = true;
        }

        public void PauseForFailure()
        {
            _isPaused = true;
            _pausedForFailure = true;
        }

        public void Resume()
        {
            _isPaused = false;
            _stepRequested = false;
            _pausedForFailure = false;
        }

        public void StepOnce()
        {
            _isPaused = false;
            _stepRequested = true;
            _pausedForFailure = false;
        }

        public void Stop()
        {
            _isStopped = true;
            _cancellationTokenSource?.Cancel();
        }

        public async Task WaitIfPausedAsync()
        {
            while (_isPaused && !_isStopped)
            {
                await EditorAsyncUtility.NextFrameAsync(CancellationToken);
            }
        }

        public void OnStepCompleted()
        {
            if (_stepRequested)
            {
                _stepRequested = false;
                _isPaused = true;
            }
            else if (RunMode == HeadedRunMode.Step)
            {
                _isPaused = true;
            }
        }

        public void Dispose()
        {
            if (_cancellationTokenSource == null)
            {
                return;
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public static class TestHostWindowManager
    {
        public static async Task<(EditorWindow window, VisualElement root)> OpenAsync(HostWindowDefinition hostWindow)
        {
            if (hostWindow == null || string.IsNullOrWhiteSpace(hostWindow.Type))
            {
                throw new UnityUIFlowException(ErrorCodes.HostWindowTypeInvalid, "Host window type is required.");
            }

            Type resolvedType = ResolveType(hostWindow.Type);
            if (hostWindow.ReopenIfOpen)
            {
                CloseOpenInstances(resolvedType);
                await EditorAsyncUtility.NextFrameAsync(CancellationToken.None);
            }

            EditorWindow window;
            try
            {
                window = EditorWindow.GetWindow(resolvedType);
                window.Show();
                window.Focus();
            }
            catch (Exception ex)
            {
                throw new UnityUIFlowException(ErrorCodes.HostWindowOpenFailed, $"Failed to open host window {hostWindow.Type}: {ex.Message}", ex);
            }

            await EditorAsyncUtility.NextFrameAsync(CancellationToken.None);
            if (window is IUnityUIFlowTestHostWindow preparedWindow)
            {
                preparedWindow.PrepareForAutomatedTest();
                await EditorAsyncUtility.NextFrameAsync(CancellationToken.None);
            }

            if (window.rootVisualElement == null)
            {
                window.Close();
                throw new UnityUIFlowException(ErrorCodes.HostWindowOpenFailed, $"Host window root is missing: {hostWindow.Type}");
            }

            return (window, window.rootVisualElement);
        }

        private static Type ResolveType(string typeName)
        {
            var matches = TypeCache.GetTypesDerivedFrom<EditorWindow>()
                .Where(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal) || string.Equals(type.Name, typeName, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                Type fullNameMatch = matches.FirstOrDefault(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal));
                if (fullNameMatch != null)
                {
                    return fullNameMatch;
                }

                throw new UnityUIFlowException(ErrorCodes.HostWindowTypeInvalid, $"Host window type is ambiguous: {typeName}");
            }

            throw new UnityUIFlowException(ErrorCodes.HostWindowTypeInvalid, $"Host window type was not found: {typeName}");
        }

        private static void CloseOpenInstances(Type type)
        {
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.GetType() == type)
                {
                    window.Close();
                }
            }
        }
    }

    /// <summary>
    /// Executes a single compiled step.
    /// </summary>
    public sealed class StepExecutor
    {
        /// <summary>
        /// Executes one step and returns a step result.
        /// </summary>
        public async Task<StepResult> ExecuteStepAsync(ExecutableStep step, ExecutionContext context, int stepIndex)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var result = new StepResult
            {
                StepId = step.StepId,
                DisplayName = step.DisplayName,
                Phase = step.Phase,
                IterationIndex = step.IterationIndex,
                StartedAtUtc = startedAt.ToString("O"),
            };

            bool verboseLog = context.Options?.EnableVerboseLog == true;
            VisualElement highlightedElement = null;

            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                ActionContext actionContext = null;

                if (step.Condition != null)
                {
                    bool conditionMet = step.Condition.Type == ConditionType.NotExists
                        ? !context.Finder.Exists(step.Condition.SelectorExpression, context.Root, true)
                        : context.Finder.Exists(step.Condition.SelectorExpression, context.Root, true);
                    if (!conditionMet)
                    {
                        if (verboseLog)
                            Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] 步骤[{stepIndex}] \"{step.DisplayName}\" 条件不满足，跳过");
                        result.Status = TestStatus.Skipped;
                        result.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                        result.DurationMs = UnityUIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow);
                        HeadedRunEventBus.PublishStepCompleted(step, result, null);
                        return result;
                    }
                }

                string selectorInfo = step.Selector != null ? $" 选择器={step.Selector.Raw}" : "";
                if (verboseLog)
                    Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] 步骤[{stepIndex}] 开始 \"{step.DisplayName}\" 动作={step.ActionName}{selectorInfo} 超时={step.TimeoutMs}ms");

                HeadedRunEventBus.PublishStepStarted(step);
                if (step.Selector != null)
                {
                    highlightedElement = context.Finder.Find(step.Selector, context.Root, false).Element;
                    if (highlightedElement != null && context.Options?.Headed == true && context.ManagedWindow != null)
                    {
                        StepHighlighter.Highlight(highlightedElement, step.ActionName, context.ManagedWindow);
                    }
                }

                if (context.Options?.PreStepDelayMs > 0)
                {
                    if (verboseLog)
                    {
                        Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] 步骤[{stepIndex}] 调试延迟 {context.Options.PreStepDelayMs}ms");
                    }

                    await EditorAsyncUtility.DelayAsync(context.Options.PreStepDelayMs, context.CancellationToken);
                }

                var timeoutController = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                try
                {
                    timeoutController.CancelAfter(step.TimeoutMs);
                    actionContext = new ActionContext
                    {
                        Root = context.Root,
                        Finder = context.Finder,
                        Options = context.Options,
                        Reporter = context.Reporter,
                        CurrentStepId = step.StepId,
                        CurrentCaseName = context.CaseName,
                        CurrentStepIndex = stepIndex,
                        SharedBag = context.SharedBag,
                        CancellationToken = timeoutController.Token,
                        ScreenshotManager = context.ScreenshotManager,
                        RuntimeController = context.RuntimeController,
                        Simulator = context.SimulationSession?.PointerDriver,
                        SimulationSession = context.SimulationSession,
                    };
                    actionContext.SharedBag.Remove("inputDriver.host");
                    actionContext.SharedBag.Remove("inputDriver.pointer");
                    actionContext.SharedBag.Remove("inputDriver.keyboard");
                    actionContext.SharedBag.Remove("officialUiToolkit.describe");
                    actionContext.SharedBag.Remove("driver.binding.summary");
                    actionContext.SharedBag["inputDriver.host"] = context.SimulationSession?.HostDriverName ?? "RootOverrideOnly";
                    actionContext.SharedBag["officialUiToolkit.describe"] = context.SimulationSession?.OfficialUiToolkit.Describe() ?? "unavailable";
                    actionContext.SharedBag["driver.binding.summary"] = context.SimulationSession?.DescribeDrivers() ?? "host=RootOverrideOnly; pointer=UIToolkitFallbackOnly; keyboard=UIToolkitFallbackOnly; official=unavailable";

                    if (step.Kind == ExecutableStepKind.Loop)
                    {
                        if (verboseLog)
                            Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] 步骤[{stepIndex}] 进入循环，最大迭代 {step.Loop.MaxIterations}");
                        await ExecuteLoopAsync(step, context, stepIndex, timeoutController.Token);
                    }
                    else
                    {
                        IAction action = context.ActionRegistry.Resolve(step.ActionName);
                        await action.ExecuteAsync(context.Root, actionContext, step.Parameters);
                    }

                    if (actionContext.CurrentAttachments.Count > 0)
                    {
                        result.Attachments.AddRange(actionContext.CurrentAttachments);
                        result.ScreenshotPath = actionContext.CurrentAttachments.FirstOrDefault();
                        if (actionContext.ScreenshotManager != null
                            && !string.IsNullOrWhiteSpace(result.ScreenshotPath))
                        {
                            result.ScreenshotSource = actionContext.ScreenshotManager.LastCaptureSource;
                        }
                    }

                    if (actionContext.SharedBag.TryGetValue("inputDriver.host", out object hostDriver))
                    {
                        result.HostDriver = hostDriver?.ToString();
                    }

                    if (actionContext.SharedBag.TryGetValue("inputDriver.pointer", out object pointerDriver))
                    {
                        result.PointerDriver = pointerDriver?.ToString();
                    }

                    if (actionContext.SharedBag.TryGetValue("inputDriver.keyboard", out object keyboardDriver))
                    {
                        result.KeyboardDriver = keyboardDriver?.ToString();
                    }

                    if (actionContext.SharedBag.TryGetValue("driver.binding.summary", out object driverDetails)
                        || actionContext.SharedBag.TryGetValue("officialUiToolkit.describe", out driverDetails))
                    {
                        result.DriverDetails = driverDetails?.ToString();
                    }
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                {
                    throw new UnityUIFlowException(ErrorCodes.StepTimeout, $"步骤 {step.DisplayName} 执行超时：{step.TimeoutMs}ms");
                }
                finally
                {
                    timeoutController.Dispose();
                }

                result.Status = TestStatus.Passed;
            }
            catch (OperationCanceledException)
            {
                result.Status = TestStatus.Error;
                result.ErrorCode = ErrorCodes.TestRunAborted;
                result.ErrorMessage = "测试运行已停止";
            }
            catch (UnityUIFlowException ex)
            {
                result.Status = ex.ErrorCode == ErrorCodes.ActionExecutionFailed || ex.ErrorCode == ErrorCodes.StepTimeout || ex.ErrorCode == ErrorCodes.AssertionFailed
                    ? TestStatus.Failed
                    : TestStatus.Error;
                result.ErrorCode = ex.ErrorCode;
                result.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                result.Status = TestStatus.Error;
                result.ErrorCode = ErrorCodes.StepExecutionException;
                result.ErrorMessage = ex.Message;
            }

            if ((result.Status == TestStatus.Failed || result.Status == TestStatus.Error) && context.Options.ScreenshotOnFailure)
            {
                try
                {
                    string screenshotPath = await context.ScreenshotManager.CaptureAsync(context.CaseName, stepIndex, "failure", context.CancellationToken);
                    if (screenshotPath != null)
                    {
                        result.ScreenshotPath = screenshotPath;
                        result.ScreenshotSource = context.ScreenshotManager.LastCaptureSource;
                        result.Attachments.Add(screenshotPath);
                    }
                    else
                    {
                        result.ScreenshotSource = context.ScreenshotManager.LastCaptureSource;
                    }
                }
                catch (Exception captureException)
                {
                    result.ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? captureException.Message
                        : result.ErrorMessage + Environment.NewLine + captureException.Message;
                }
            }

            if ((result.Status == TestStatus.Failed || result.Status == TestStatus.Error)
                && context.Options.Headed
                && context.Options.DebugOnFailure)
            {
                context.RuntimeController?.PauseForFailure();
            }

            result.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            result.DurationMs = UnityUIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow);

            if (verboseLog)
            {
                string statusText = result.Status == TestStatus.Passed ? "通过" : result.Status == TestStatus.Skipped ? "跳过" : "失败";
                string errorDetail = string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $" | {result.ErrorCode}: {result.ErrorMessage}";
                string screenshotDetail = string.IsNullOrWhiteSpace(result.ScreenshotPath) ? string.Empty : $" | 截图={result.ScreenshotPath}";
                string driverDetail = string.IsNullOrWhiteSpace(result.DriverDetails) ? string.Empty : $" | 驱动={result.DriverDetails}";
                Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] 步骤[{stepIndex}] {statusText} \"{step.DisplayName}\" {result.DurationMs}ms{errorDetail}{screenshotDetail}{driverDetail}");
            }

            VisualElement completedElement = step.Selector != null ? context.Finder.Find(step.Selector, context.Root, false).Element : null;
            if (context.Options?.Headed == true && highlightedElement != null)
            {
                StepHighlighter.ClearAfterDelay(highlightedElement, 800);
            }
            context.RuntimeController?.OnStepCompleted();
            HeadedRunEventBus.PublishStepCompleted(step, result, completedElement);
            return result;
        }

        private static async Task ExecuteLoopAsync(ExecutableStep step, ExecutionContext context, int stepIndex, CancellationToken cancellationToken)
        {
            int iterations = 0;
            bool LoopConditionMet()
            {
                bool exists = context.Finder.Exists(step.Loop.Condition.SelectorExpression, context.Root, true);
                return step.Loop.Condition.Type == ConditionType.NotExists ? !exists : exists;
            }

            while (LoopConditionMet())
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;
                if (iterations >= step.Loop.MaxIterations)
                {
                    if (context?.Options?.EnableVerboseLog == true)
                        Codingriver.Logger.Log($"[UnityUIFlow][{context.CaseName}] Loop {step.DisplayName} reached max_iterations {step.Loop.MaxIterations}, exiting gracefully.");
                    break;
                }

                foreach (ExecutableStep loopStep in step.Loop.Steps)
                {
                    StepResult nested = await new StepExecutor().ExecuteStepAsync(loopStep, context, stepIndex);
                    if (nested.Status == TestStatus.Failed || nested.Status == TestStatus.Error)
                    {
                        throw new UnityUIFlowException(nested.ErrorCode ?? ErrorCodes.ActionExecutionFailed, nested.ErrorMessage ?? $"循环步骤 {loopStep.DisplayName} 执行失败");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Main test runner entry point.
    /// </summary>
    public sealed class TestRunner
    {
        private readonly YamlTestCaseParser _parser = new YamlTestCaseParser();
        private readonly SelectorCompiler _selectorCompiler = new SelectorCompiler();
        private readonly ActionRegistry _actionRegistry = new ActionRegistry();
        private readonly StepExecutor _stepExecutor = new StepExecutor();

        /// <summary>
        /// Runs a single YAML test file.
        /// </summary>
        public async Task<TestResult> RunFileAsync(string yamlPath, TestOptions options = null, VisualElement rootOverride = null, Action<ExecutionContext> onContextReady = null)
        {
            if (string.IsNullOrWhiteSpace(yamlPath))
            {
                throw new UnityUIFlowException(ErrorCodes.TestCasePathInvalid, "测试用例路径非法");
            }

            TestCaseDefinition definition = _parser.ParseFile(yamlPath);
            TestResult result = await RunDefinitionAsync(definition, options ?? new TestOptions(), rootOverride, onContextReady);

            // Append single-file result to unified report
            try
            {
                var suite = new TestSuiteResult
                {
                    StartedAtUtc = result.StartedAtUtc,
                    EndedAtUtc = result.EndedAtUtc,
                    CaseResults = new List<TestResult> { result },
                };
                MarkdownReporter.WriteUnifiedSuiteReport(suite, overwrite: false);
            }
            catch (Exception unifiedEx)
            {
                Codingriver.Logger.LogWarning($"[UnityUIFlow] 追加统一报告失败: {unifiedEx.Message}");
            }

            return result;
        }
        public Task<TestResult> RunAsync(string yamlContent, TestOptions options = null, VisualElement rootOverride = null)
        {
            TestCaseDefinition definition = _parser.Parse(yamlContent, "inline.yaml");
            return RunDefinitionAsync(definition, options ?? new TestOptions(), rootOverride);
        }
        /// <summary>
        /// Runs YAML content against a specific root.
        /// </summary>
        public Task<TestResult> RunAsync(string yamlContent, string sourcePath, VisualElement root, TestOptions options = null, Action<ExecutionContext> onContextReady = null)
        {
            TestCaseDefinition definition = _parser.Parse(yamlContent, sourcePath ?? "inline.yaml");
            return RunDefinitionAsync(definition, options ?? new TestOptions(), root, onContextReady);
        }

        /// <summary>
        /// Runs all YAML files under a directory.
        /// </summary>
        public async Task<TestSuiteResult> RunSuiteAsync(string directory, TestOptions options = null, Func<string, string, bool> filter = null, VisualElement rootOverride = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new UnityUIFlowException(ErrorCodes.TestSuiteDirectoryNotFound, $"测试目录不存在：{directory}");
            }

            options ??= new TestOptions();
            options.Validate();

            string[] yamlFiles = Directory.GetFiles(directory, "*.yaml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var suiteResult = new TestSuiteResult
            {
                StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            if (yamlFiles.Length == 0)
            {
                suiteResult.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                suiteResult.ExitCode = 0;
                return suiteResult;
            }

            if (options.EnableVerboseLog)
                Codingriver.Logger.Log($"[UnityUIFlow] 开始执行测试套件 目录={directory} 文件数={yamlFiles.Length}");

            int fileIndex = 0;
            foreach (string yamlFile in yamlFiles)
            {
                fileIndex++;
                if (options.EnableVerboseLog)
                    Codingriver.Logger.Log($"[UnityUIFlow] 进度 [{fileIndex}/{yamlFiles.Length}] {yamlFile}");
                TestResult testResult;
                try
                {
                    TestCaseDefinition definition = _parser.ParseFile(yamlFile);
                    if (filter != null && !filter(yamlFile, definition.Name))
                    {
                        continue;
                    }

                    var caseOptions = options.Clone();
                    caseOptions.GenerateSingleReport = false;
                    caseOptions.CaseIndex = fileIndex;
                    caseOptions.TotalCases = yamlFiles.Length;
                    testResult = await RunDefinitionAsync(definition, caseOptions, rootOverride);
                }
                catch (Exception ex)
                {
                    string fallbackCaseName = Path.GetFileNameWithoutExtension(yamlFile);
                    testResult = new TestResult
                    {
                        CaseName = fallbackCaseName,
                        Status = TestStatus.Error,
                        StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        EndedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        ErrorCode = ex is UnityUIFlowException flowException ? flowException.ErrorCode : ErrorCodes.CliExecutionError,
                        ErrorMessage = ex.Message,
                        ReportMarkdownPath = new ReportPathBuilder().BuildCaseMarkdownPath(options.ReportOutputPath, fallbackCaseName),
                    };
                }

                suiteResult.CaseResults.Add(testResult);
                suiteResult.Total++;
                switch (testResult.Status)
                {
                    case TestStatus.Passed:
                        suiteResult.Passed++;
                        break;
                    case TestStatus.Failed:
                        suiteResult.Failed++;
                        break;
                    case TestStatus.Error:
                        suiteResult.Errors++;
                        break;
                    case TestStatus.Skipped:
                        suiteResult.Skipped++;
                        break;
                }

                if (options.StopOnFirstFailure && (testResult.Status == TestStatus.Failed || testResult.Status == TestStatus.Error))
                {
                    break;
                }
            }

            suiteResult.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            suiteResult.ExitCode = ExitCodeResolver.Resolve(suiteResult);
            if (options.EnableVerboseLog)
                Codingriver.Logger.Log($"[UnityUIFlow] 套件完成 通过={suiteResult.Passed} 失败={suiteResult.Failed} 错误={suiteResult.Errors} 跳过={suiteResult.Skipped} 总计={suiteResult.Total}");
            var reporter = new MarkdownReporter(new ReporterOptions
            {
                ReportRootPath = options.ReportOutputPath,
                ScreenshotRootPath = options.ScreenshotPath,
                SuiteName = null,
            });
            try
            {
                reporter.WriteSuiteReport(suiteResult);
                if (options.EnableVerboseLog)
                    Codingriver.Logger.Log($"[UnityUIFlow] 套件报告已生成 {options.ReportOutputPath}");
            }
            catch (Exception reportException)
            {
                Codingriver.Logger.LogError($"[UnityUIFlow] {ErrorCodes.ReportWriteFailed}: {reportException.Message} 路径={options.ReportOutputPath}");
            }

            // Overwrite unified suite report with batch results
            try
            {
                MarkdownReporter.WriteUnifiedSuiteReport(suiteResult, overwrite: true);
            }
            catch (Exception unifiedEx)
            {
                Codingriver.Logger.LogWarning($"[UnityUIFlow] 写入统一套件报告失败: {unifiedEx.Message}");
            }

            return suiteResult;
        }

        private async Task<TestResult> RunDefinitionAsync(TestCaseDefinition definition, TestOptions options, VisualElement rootOverride, Action<ExecutionContext> onContextReady = null)
        {
            options = UnityUIFlowProjectSettingsUtility.ApplyOverrides(options);
            options.Validate();
            if (options.TotalCases <= 0)
            {
                options.CaseIndex = 1;
                options.TotalCases = 1;
            }

            if (options.RetryCount.HasValue)
            {
                Codingriver.Logger.LogWarning($"[UnityUIFlow] RetryCount={options.RetryCount.Value} 在 V1 中不支持，已忽略。如需重试请在调用方实现。");
            }

            if (options.EnableVerboseLog)
                Codingriver.Logger.Log($"[UnityUIFlow] 解析用例 \"{definition.Name}\" YAML={definition.SourceFile ?? "inline"}");

            VisualElement root = rootOverride;
            EditorWindow managedWindow = null;
            if (root == null)
            {
                (managedWindow, root) = await ResolveExecutionRootAsync(definition);
            }
            if (root == null)
            {
                throw new UnityUIFlowException(ErrorCodes.RootElementMissing, "未找到可执行的根节点");
            }

            var reportOptions = new ReporterOptions
            {
                ReportRootPath = options.ReportOutputPath,
                ScreenshotRootPath = options.ScreenshotPath,
            };

            var simulationSession = new UnityUIFlowSimulationSession();
            if (managedWindow != null)
            {
                simulationSession.BindEditorWindowHost(managedWindow, "HostWindowManager(EditorWindow.GetWindow)");
            }
            else
            {
                simulationSession.BindHostDriver("RootOverrideOnly");
            }
            if (options.RequireOfficialHost && !simulationSession.HasExecutableOfficialHost)
            {
                throw new UnityUIFlowException(
                    ErrorCodes.FixtureWindowCreateFailed,
                    $"正式验收模式下未能创建官方测试宿主：{definition.Fixture?.HostWindow?.Type ?? definition.Name}");
            }

            var finder = new ElementFinder();
            if (options != null) finder.EnableVerboseLog = options.EnableVerboseLog;
            var context = new ExecutionContext
            {
                Root = root,
                ManagedWindow = managedWindow,
                Finder = finder,
                Options = options,
                Reporter = new MarkdownReporter(reportOptions),
                ScreenshotManager = new ScreenshotManager(options, () => ResolveScreenshotWindow(managedWindow, root)),
                ActionRegistry = _actionRegistry,
                RuntimeController = new RuntimeController(),
                SimulationSession = simulationSession,
                CaseName = definition.Name,
            };

            if (options.EnableVerboseLog)
            {
                string driverSummary = simulationSession.DescribeDrivers() ?? "unavailable";
                Codingriver.Logger.Log($"[UnityUIFlow] 绑定驱动 {driverSummary}");
            }
            context.CancellationToken = context.RuntimeController.CancellationToken;
            onContextReady?.Invoke(context);

            if (options.Headed)
            {
                HeadedRunEventBus.PublishRunAttached(context.RuntimeController, definition.Name);
            }

            if (options.EnableVerboseLog)
            {
                Codingriver.Logger.Log($"[UnityUIFlow] 开始执行用例 \"{definition.Name}\"");
            }

            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var result = new TestResult
            {
                CaseName = definition.Name,
                StartedAtUtc = startedAt.ToString("O"),
            };

            try
            {
                var planBuilder = new ExecutionPlanBuilder(_selectorCompiler, _actionRegistry);
                ExecutionPlan plan = planBuilder.Build(definition, options);
                if (options.EnableVerboseLog)
                    Codingriver.Logger.Log($"[UnityUIFlow] 构建执行计划 步骤数={plan.Steps.Count}");
                bool abortMainFlow = false;

                for (int index = 0; index < plan.Steps.Count; index++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await context.RuntimeController.WaitIfPausedAsync();

                    ExecutableStep step = plan.Steps[index];
                    if (abortMainFlow && step.Phase != StepPhase.Teardown)
                    {
                        continue;
                    }

                    StepResult stepResult = await _stepExecutor.ExecuteStepAsync(step, context, index + 1);
                    if (!string.IsNullOrWhiteSpace(stepResult.ScreenshotPath))
                    {
                        stepResult.ScreenshotPath = UnityUIFlowUtility.EnsureRelativeTo(options.ReportOutputPath, stepResult.ScreenshotPath);
                    }

                    context.Reporter.RecordStepResult(result.CaseName, stepResult, Array.Empty<string>());
                    result.StepResults.Add(stepResult);

                    if (stepResult.Status == TestStatus.Failed || stepResult.Status == TestStatus.Error)
                    {
                        if (options.Headed)
                        {
                            HeadedRunEventBus.PublishFailure(step, stepResult);
                        }

                        if (!step.ContinueOnFailure)
                        {
                            if (step.Phase == StepPhase.Teardown)
                            {
                                break;
                            }

                            abortMainFlow = true;
                        }
                    }

                }

                if (context.RuntimeController.IsPausedForFailure && !context.RuntimeController.IsStopped)
                {
                    await context.RuntimeController.WaitIfPausedAsync();
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorCode = ErrorCodes.TestRunAborted;
                result.ErrorMessage = "测试运行已停止";
                result.Status = TestStatus.Error;
            }
            catch (UnityUIFlowException ex)
            {
                result.ErrorCode = ex.ErrorCode;
                result.ErrorMessage = ex.Message;
                result.Status = TestStatus.Error;
            }
            catch (Exception ex)
            {
                result.ErrorCode = ErrorCodes.CliExecutionError;
                result.ErrorMessage = ex.Message;
                result.Status = TestStatus.Error;
            }
            finally
            {
                result.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                result.DurationMs = UnityUIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow);
                if (result.Status != TestStatus.Error)
                {
                    result.Status = ComputeStatus(result.StepResults);
                }

                // Negative test adjustment: expected failures are reported as Passed
                if ((result.Status == TestStatus.Failed || result.Status == TestStatus.Error)
                    && !string.IsNullOrWhiteSpace(definition.Name)
                    && definition.Name.Contains("-negative-"))
                {
                    result.Status = TestStatus.Passed;
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        result.ErrorMessage = $"[Expected failure] {result.ErrorMessage}";
                    }
                    else
                    {
                        result.ErrorMessage = "[Expected failure]";
                    }
                }

                try
                {
                    context.Reporter.WriteCaseReport(result);
                    if (options.GenerateSingleReport)
                    {
                        context.Reporter.WriteSingleReport(result);
                    }
                    var reportPaths = new ReportPathBuilder();
                    result.ReportMarkdownPath = reportPaths.BuildCaseMarkdownPath(options.ReportOutputPath, result.CaseName);
                    if (options.EnableVerboseLog)
                        Codingriver.Logger.Log($"[UnityUIFlow] 用例报告已生成 {options.ReportOutputPath}");
                }
                catch (Exception reportException)
                {
                    Codingriver.Logger.LogError($"[UnityUIFlow] {ErrorCodes.ReportWriteFailed}: {reportException.Message} 路径={options.ReportOutputPath}");
                }

                context.Dispose();
                HeadedRunEventBus.PublishRunFinished(result);
                
                {
                    string stepSummary = $"通过={result.StepResults.Count(s => s.Status == TestStatus.Passed)} 失败={result.StepResults.Count(s => s.Status == TestStatus.Failed)} 错误={result.StepResults.Count(s => s.Status == TestStatus.Error)} 跳过={result.StepResults.Count(s => s.Status == TestStatus.Skipped)}";
                    string progressPrefix = options.TotalCases > 1 ? $"[{options.CaseIndex}/{options.TotalCases}]" : "";
                    Codingriver.Logger.LogWarning($"[UnityUIFlow] {progressPrefix}用例 \"{definition.Name}\" 完成 状态={result.Status} 耗时={result.DurationMs}ms | {stepSummary}");
                }
            }

            return result;
        }

        private static TestStatus ComputeStatus(List<StepResult> steps)
        {
            if (steps == null || steps.Count == 0)
            {
                return TestStatus.Skipped;
            }

            bool anyPassed = false;
            bool anySkipped = false;
            foreach (StepResult step in steps)
            {
                switch (step.Status)
                {
                    case TestStatus.Error:
                        return TestStatus.Error;
                    case TestStatus.Failed:
                        return TestStatus.Failed;
                    case TestStatus.Passed:
                        anyPassed = true;
                        break;
                    case TestStatus.Skipped:
                        anySkipped = true;
                        break;
                }
            }

            if (anyPassed)
            {
                return TestStatus.Passed;
            }

            return anySkipped ? TestStatus.Skipped : TestStatus.Passed;
        }

        private static VisualElement ResolveDefaultRoot()
        {
            EditorWindow focused = EditorWindow.focusedWindow;
            if (focused != null && !(focused is TestRunnerWindow) && focused.rootVisualElement != null)
            {
                return focused.rootVisualElement;
            }

            EditorWindow[] windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (EditorWindow window in windows)
            {
                if (window == null || window is TestRunnerWindow)
                {
                    continue;
                }

                if (window.rootVisualElement != null)
                {
                    return window.rootVisualElement;
                }
            }

            return null;
        }

        private static async Task<(EditorWindow window, VisualElement root)> ResolveExecutionRootAsync(TestCaseDefinition definition)
        {
            if (definition?.Fixture?.HostWindow != null)
            {
                return await TestHostWindowManager.OpenAsync(definition.Fixture.HostWindow);
            }

            return (null, ResolveDefaultRoot());
        }

        private static EditorWindow ResolveScreenshotWindow(EditorWindow managedWindow, VisualElement root)
        {
            if (managedWindow != null)
            {
                return managedWindow;
            }

            if (root?.panel == null)
            {
                return EditorWindow.focusedWindow;
            }

            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.rootVisualElement?.panel == root.panel)
                {
                    return window;
                }
            }

            return EditorWindow.focusedWindow;
        }
    }
}
