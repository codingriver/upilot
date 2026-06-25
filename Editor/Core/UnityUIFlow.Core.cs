using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    /// <summary>
    /// Well-known error codes used by UnityUIFlow.
    /// </summary>
    public static class ErrorCodes
    {
        public const string AssertionFailed = "ASSERTION_FAILED";
        public const string ActionExecutionFailed = "ACTION_EXECUTION_FAILED";
        public const string ActionNameConflict = "ACTION_NAME_CONFLICT";
        public const string ActionNotFound = "ACTION_NOT_FOUND";
        public const string ActionNotRegistered = "ACTION_NOT_REGISTERED";
        public const string ActionParameterInvalid = "ACTION_PARAMETER_INVALID";
        public const string ActionParameterMissing = "ACTION_PARAMETER_MISSING";
        public const string ActionIndexOutOfRange = "ACTION_INDEX_OUT_OF_RANGE";
        public const string ActionOptionNotFound = "ACTION_OPTION_NOT_FOUND";
        public const string ActionTargetTypeInvalid = "ACTION_TARGET_TYPE_INVALID";
        public const string AttachmentLimitExceeded = "ATTACHMENT_LIMIT_EXCEEDED";
        public const string CliArgumentInvalid = "CLI_ARGUMENT_INVALID";
        public const string CliConfigFileInvalid = "CLI_CONFIG_FILE_INVALID";
        public const string CliExecutionError = "CLI_EXECUTION_ERROR";
        public const string CliFilterInvalid = "CLI_FILTER_INVALID";
        public const string CliReportPathInvalid = "CLI_REPORT_PATH_INVALID";
        public const string CliTestsFailed = "CLI_TESTS_FAILED";
        public const string DurationLiteralInvalid = "DURATION_LITERAL_INVALID";
        public const string ElementDisposedDuringQuery = "ELEMENT_DISPOSED_DURING_QUERY";
        public const string ElementNotVisible = "ELEMENT_NOT_VISIBLE";
        public const string ElementWaitTimeout = "ELEMENT_WAIT_TIMEOUT";
        public const string ExecutionPlanEmpty = "EXECUTION_PLAN_EMPTY";
        public const string FixtureContextNotReady = "FIXTURE_CONTEXT_NOT_READY";
        public const string FixtureRootMissing = "FIXTURE_ROOT_MISSING";
        public const string FixtureTeardownFailed = "FIXTURE_TEARDOWN_FAILED";
        public const string FixtureWindowCreateFailed = "FIXTURE_WINDOW_CREATE_FAILED";
        public const string FixtureYamlEmpty = "FIXTURE_YAML_EMPTY";
        public const string HostWindowOpenFailed = "HOST_WINDOW_OPEN_FAILED";
        public const string HostWindowTypeInvalid = "HOST_WINDOW_TYPE_INVALID";
        public const string HeadedEnvironmentUnsupported = "HEADED_ENVIRONMENT_UNSUPPORTED";
        public const string HeadedFileNotSelected = "HEADED_FILE_NOT_SELECTED";
        public const string HeadedInvalidTransition = "HEADED_INVALID_TRANSITION";
        public const string HeadedStateOutOfSync = "HEADED_STATE_OUT_OF_SYNC";
        public const string HeadedTargetInvalid = "HEADED_TARGET_INVALID";
        public const string InputSystemTestFrameworkUnavailable = "INPUT_SYSTEM_TEST_FRAMEWORK_UNAVAILABLE";
        public const string OfficialUiTestFrameworkUnavailable = "OFFICIAL_UI_TEST_FRAMEWORK_UNAVAILABLE";
        public const string ReportOutputUnavailable = "REPORT_OUTPUT_UNAVAILABLE";
        public const string ReportWriteFailed = "REPORT_WRITE_FAILED";
        public const string RootElementMissing = "ROOT_ELEMENT_MISSING";
        public const string ScreenshotArgumentInvalid = "SCREENSHOT_ARGUMENT_INVALID";
        public const string ScreenshotSaveFailed = "SCREENSHOT_SAVE_FAILED";
        public const string SelectorCompileError = "SELECTOR_COMPILE_ERROR";
        public const string SelectorEmpty = "SELECTOR_EMPTY";
        public const string SelectorInvalid = "SELECTOR_INVALID";
        public const string SelectorSyntaxInvalid = "SELECTOR_SYNTAX_INVALID";
        public const string ElementNotFound = "ELEMENT_NOT_FOUND";
        public const string StepExecutionException = "STEP_EXECUTION_EXCEPTION";
        public const string StepTimeout = "STEP_TIMEOUT";
        public const string TestCaseFileNotFound = "TEST_CASE_FILE_NOT_FOUND";
        public const string TestCasePathInvalid = "TEST_CASE_PATH_INVALID";
        public const string TestCaseSchemaInvalid = "TEST_CASE_SCHEMA_INVALID";
        public const string TestDataFileNotFound = "TEST_DATA_FILE_NOT_FOUND";
        public const string TestDataVariableMissing = "TEST_DATA_VARIABLE_MISSING";
        public const string TestLoopLimitExceeded = "TEST_LOOP_LIMIT_EXCEEDED";
        public const string TestOptionsInvalid = "TEST_OPTIONS_INVALID";
        public const string TestRunAborted = "TEST_RUN_ABORTED";
        public const string TestSuiteDirectoryNotFound = "TEST_SUITE_DIRECTORY_NOT_FOUND";
        public const string TestSuiteEmpty = "TEST_SUITE_EMPTY";
        public const string YamlFieldTypeInvalid = "YAML_FIELD_TYPE_INVALID";
        public const string YamlParseError = "YAML_PARSE_ERROR";
    }

    /// <summary>
    /// Exception raised by UnityUIFlow operations.
    /// </summary>
    public sealed class UnityUIFlowException : Exception
    {
        public UnityUIFlowException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public UnityUIFlowException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Stable machine-readable error code.
        /// </summary>
        public string ErrorCode { get; }
    }

    /// <summary>
    /// Supported test statuses.
    /// </summary>
    public enum TestStatus
    {
        None,
        Passed,
        Failed,
        Error,
        Skipped,
    }

    /// <summary>
    /// Execution phase for a step.
    /// </summary>
    public enum StepPhase
    {
        Setup,
        Main,
        Teardown,
    }

    /// <summary>
    /// Selector combinator type.
    /// </summary>
    public enum SelectorCombinator
    {
        Self,
        Descendant,
        Child,
    }

    /// <summary>
    /// Selector token type.
    /// </summary>
    public enum SelectorTokenType
    {
        Id,
        Class,
        Type,
        Attribute,
        Pseudo,
    }

    /// <summary>
    /// Condition expression type.
    /// </summary>
    public enum ConditionType
    {
        Exists,
        NotExists,
    }

    /// <summary>
    /// Loop execution behavior.
    /// </summary>
    public enum ExecutableStepKind
    {
        Action,
        Loop,
    }

    /// <summary>
    /// Headed runner mode.
    /// </summary>
    public enum HeadedRunMode
    {
        Continuous,
        Step,
    }

    /// <summary>
    /// Headed runner state.
    /// </summary>
    public enum HeadedRunnerState
    {
        Idle,
        Running,
        Paused,
        Failed,
        Stopped,
    }

    /// <summary>
    /// Headed failure policy.
    /// </summary>
    public enum HeadedFailurePolicy
    {
        Pause,
        Continue,
    }

    /// <summary>
    /// Runtime options for a test run.
    /// </summary>
    [Serializable]
    public sealed class TestOptions
    {
        public bool Headed = true;
        public int DefaultTimeoutMs = 3000;
        public bool DebugOnFailure = true;
        public string ReportOutputPath = "Reports";
        public string ScreenshotPath = "Reports/Screenshots";
        public bool StopOnFirstFailure;
        public bool ContinueOnStepFailure;
        public bool ScreenshotOnFailure = true;
        public int? RetryCount;
        public bool RequireOfficialHost = false;
        public bool RequireOfficialPointerDriver = false;
        public bool RequireInputSystemKeyboardDriver = false;

        /// <summary>
        /// Enables verbose per-step and per-action logging to the Unity Console.
        /// </summary>
        public bool EnableVerboseLog = false;

        /// <summary>
        /// Optional delay applied before each step action starts.
        /// This is useful when debugging highlighted elements and simulated input.
        /// </summary>
        public int PreStepDelayMs = 0;

        /// <summary>
        /// When true, RunFileAsync generates single_reports.md after each case.
        /// Suite runs disable this to avoid overwriting.        /// </summary>
        public bool GenerateSingleReport = true;

        /// <summary>
        /// 当前用例在批次中的索引（从 1 开始）。仅用于日志/报告展示。
        /// </summary>
        public int CaseIndex = 0;

        /// <summary>
        /// 批次中用例总数。仅用于日志/报告展示。
        /// </summary>
        public int TotalCases = 0;

        /// <summary>
        /// Creates a defensive copy.
        /// </summary>
        public TestOptions Clone()
        {
            return new TestOptions
            {
                Headed = Headed,
                DefaultTimeoutMs = DefaultTimeoutMs,
                DebugOnFailure = DebugOnFailure,
                ReportOutputPath = ReportOutputPath,
                ScreenshotPath = ScreenshotPath,
                StopOnFirstFailure = StopOnFirstFailure,
                ContinueOnStepFailure = ContinueOnStepFailure,
                ScreenshotOnFailure = ScreenshotOnFailure,
                RetryCount = RetryCount,
                RequireOfficialHost = RequireOfficialHost,
                RequireOfficialPointerDriver = RequireOfficialPointerDriver,
                RequireInputSystemKeyboardDriver = RequireInputSystemKeyboardDriver,
                EnableVerboseLog = EnableVerboseLog,
                PreStepDelayMs = PreStepDelayMs,
                GenerateSingleReport = GenerateSingleReport,
                CaseIndex = CaseIndex,
                TotalCases = TotalCases,
            };
        }

        /// <summary>
        /// Validates the option object.
        /// </summary>
        public void Validate()
        {
            if (DefaultTimeoutMs < 100 || DefaultTimeoutMs > 600000)
            {
                throw new UnityUIFlowException(ErrorCodes.TestOptionsInvalid, "DefaultTimeoutMs 超出允许范围");
            }

            if (string.IsNullOrWhiteSpace(ReportOutputPath) || string.IsNullOrWhiteSpace(ScreenshotPath))
            {
                throw new UnityUIFlowException(ErrorCodes.TestOptionsInvalid, "输出目录不能为空");
            }

            if (RetryCount.HasValue && (RetryCount.Value < 0 || RetryCount.Value > 5))
            {
                throw new UnityUIFlowException(ErrorCodes.TestOptionsInvalid, "RetryCount 超出允许范围");
            }

            if (PreStepDelayMs < 0 || PreStepDelayMs > 60000)
            {
                throw new UnityUIFlowException(ErrorCodes.TestOptionsInvalid, "PreStepDelayMs 超出允许范围");
            }
        }
    }

    /// <summary>
    /// Parsed YAML test case definition.
    /// </summary>
    [Serializable]
    public sealed class TestCaseDefinition
    {
        public string Name;
        public string Description = string.Empty;
        public FixtureDefinition Fixture = new FixtureDefinition();
        public DataSourceDefinition Data = new DataSourceDefinition();
        public List<StepDefinition> Steps = new List<StepDefinition>();
        public string SourceFile;
        public List<string> Tags = new List<string>();
        public int? TimeoutMs;
    }

    /// <summary>
    /// Setup and teardown groups for a case.
    /// </summary>
    [Serializable]
    public sealed class FixtureDefinition
    {
        public List<StepDefinition> Setup = new List<StepDefinition>();
        public List<StepDefinition> Teardown = new List<StepDefinition>();
        public HostWindowDefinition HostWindow;
    }

    /// <summary>
    /// Host window declaration used when YAML runs without a root override.
    /// </summary>
    [Serializable]
    public sealed class HostWindowDefinition
    {
        public string Type;
        public bool ReopenIfOpen = true;

        public HostWindowDefinition Clone()
        {
            return new HostWindowDefinition
            {
                Type = Type,
                ReopenIfOpen = ReopenIfOpen,
            };
        }
    }

    /// <summary>
    /// Data source declaration.
    /// </summary>
    [Serializable]
    public sealed class DataSourceDefinition
    {
        public List<Dictionary<string, string>> Rows = new List<Dictionary<string, string>>();
        public string FromCsv;
        public string FromJson;
        public bool HasInlineRows => Rows != null && Rows.Count > 0;
    }

    /// <summary>
    /// Parsed YAML step definition.
    /// </summary>
    [Serializable]
    public sealed class StepDefinition
    {
        public string Name;
        public string Action;
        public string Selector;
        public string Value;
        public string Expected;
        public string Timeout;
        public string Duration;
        public ConditionDefinition If;
        public LoopDefinition RepeatWhile;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        public StepDefinition Clone()
        {
            return new StepDefinition
            {
                Name = Name,
                Action = Action,
                Selector = Selector,
                Value = Value,
                Expected = Expected,
                Timeout = Timeout,
                Duration = Duration,
                If = If?.Clone(),
                RepeatWhile = RepeatWhile?.Clone(),
                Parameters = new Dictionary<string, string>(Parameters, StringComparer.Ordinal),
            };
        }
    }

    /// <summary>
    /// Conditional execution metadata.
    /// </summary>
    [Serializable]
    public sealed class ConditionDefinition
    {
        public string Exists;
        public string NotExists;

        public ConditionDefinition Clone()
        {
            return new ConditionDefinition
            {
                Exists = Exists,
                NotExists = NotExists,
            };
        }
    }

    /// <summary>
    /// Loop definition metadata.
    /// </summary>
    [Serializable]
    public sealed class LoopDefinition
    {
        public ConditionDefinition Condition = new ConditionDefinition();
        public List<StepDefinition> Steps = new List<StepDefinition>();
        public int MaxIterations = 1000;

        public LoopDefinition Clone()
        {
            var clone = new LoopDefinition
            {
                Condition = Condition?.Clone(),
                MaxIterations = MaxIterations,
            };

            foreach (StepDefinition step in Steps)
            {
                clone.Steps.Add(step.Clone());
            }

            return clone;
        }
    }

    /// <summary>
    /// Compiled plan ready for execution.
    /// </summary>
    [Serializable]
    public sealed class ExecutionPlan
    {
        public string CaseName;
        public List<ExecutableStep> Steps = new List<ExecutableStep>();
        public int DefaultTimeoutMs;
        public string SourcePath;
        public List<CompileDiagnostic> Diagnostics = new List<CompileDiagnostic>();
    }

    /// <summary>
    /// Compiled step ready for execution.
    /// </summary>
    [Serializable]
    public sealed class ExecutableStep
    {
        public string StepId = Guid.NewGuid().ToString("N");
        public string DisplayName;
        public string ActionName;
        public SelectorExpression Selector;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        public int TimeoutMs;
        public bool ContinueOnFailure;
        public ConditionExpression Condition;
        public StepPhase Phase;
        public int IterationIndex;
        public ExecutableStepKind Kind = ExecutableStepKind.Action;
        public LoopExpression Loop;
    }

    /// <summary>
    /// Compiled loop step body.
    /// </summary>
    [Serializable]
    public sealed class LoopExpression
    {
        public ConditionExpression Condition;
        public List<ExecutableStep> Steps = new List<ExecutableStep>();
        public int MaxIterations = 1000;
    }

    /// <summary>
    /// Compiled selector expression.
    /// </summary>
    [Serializable]
    public sealed class SelectorExpression
    {
        public string Raw;
        public List<SelectorSegment> Segments = new List<SelectorSegment>();
    }

    /// <summary>
    /// One selector segment token.
    /// </summary>
    [Serializable]
    public sealed class SelectorSegment
    {
        public SelectorCombinator Combinator = SelectorCombinator.Self;
        public SelectorTokenType TokenType;
        public string TokenValue;
        public string PseudoArguments;
    }

    /// <summary>
    /// Compiled conditional expression.
    /// </summary>
    [Serializable]
    public sealed class ConditionExpression
    {
        public ConditionType Type = ConditionType.Exists;
        public SelectorExpression SelectorExpression;
    }

    /// <summary>
    /// Compile-time diagnostic.
    /// </summary>
    [Serializable]
    public sealed class CompileDiagnostic
    {
        public string Kind;
        public string Code;
        public string Message;
        public int? Line;
        public int? Column;
        public string Suggestion;
    }

    /// <summary>
    /// Wait options for element lookups.
    /// </summary>
    [Serializable]
    public sealed class WaitOptions
    {
        public int TimeoutMs;
        public int PollIntervalMs = 16;
        public bool RequireVisible = true;
    }

    /// <summary>
    /// Result of an element lookup.
    /// </summary>
    public sealed class FindResult
    {
        public VisualElement Element;
        public int? FoundAtMs;
    }

    /// <summary>
    /// One step execution result.
    /// </summary>
    [Serializable]
    public sealed class StepResult
    {
        public string StepId;
        public string DisplayName;
        public TestStatus Status;
        public string StartedAtUtc;
        public string EndedAtUtc;
        public int DurationMs;
        public string ErrorCode;
        public string ErrorMessage;
        public string ScreenshotPath;
        public string ScreenshotSource;
        public string HostDriver;
        public string PointerDriver;
        public string KeyboardDriver;
        public string DriverDetails;
        public StepPhase Phase;
        public int IterationIndex;
        public List<string> Attachments = new List<string>();
    }

    /// <summary>
    /// One test case execution result.
    /// </summary>
    [Serializable]
    public sealed class TestResult
    {
        public string CaseName;
        public TestStatus Status;
        public string StartedAtUtc;
        public string EndedAtUtc;
        public int DurationMs;
        public List<StepResult> StepResults = new List<StepResult>();
        public string ErrorCode;
        public string ErrorMessage;
        public List<string> Attachments = new List<string>();
        /// <summary>
        /// Path to the per-case markdown report, used by unified suite report linking.
        /// </summary>
        public string ReportMarkdownPath;
    }

    /// <summary>
    /// Aggregated suite execution result.
    /// </summary>
    [Serializable]
    public sealed class TestSuiteResult
    {
        public int Total;
        public int Passed;
        public int Failed;
        public int Errors;
        public int Skipped;
        public List<TestResult> CaseResults = new List<TestResult>();
        public string StartedAtUtc;
        public string EndedAtUtc;
        public int ExitCode;
    }

    /// <summary>
    /// Reporter configuration.
    /// </summary>
    [Serializable]
    public sealed class ReporterOptions
    {
        public string ReportRootPath;
        public string ScreenshotRootPath;
        public string SuiteName;
    }

    /// <summary>
    /// One step entry in generated reports.
    /// </summary>
    [Serializable]
    public sealed class StepReportEntry
    {
        public string CaseName;
        public string StepName;
        public TestStatus Status;
        public string StartedAtUtc;
        public string EndedAtUtc;
        public int DurationMs;
        public string ScreenshotPath;
        public string ScreenshotSource;
        public string ErrorCode;
        public string ErrorMessage;
        public string HostDriver;
        public string PointerDriver;
        public string KeyboardDriver;
        public string DriverDetails;
        public List<string> Attachments = new List<string>();
    }

    /// <summary>
    /// Parsed command-line options.
    /// </summary>
    [Serializable]
    public sealed class CliOptions
    {
        public string TestFilter;
        public string YamlPath;
        public string YamlDirectory;
        public bool Headed = true;
        public string ReportPath = "Reports";
        public bool ScreenshotOnFailure = true;
        public string ScreenshotPath;
        public bool Nographics;
        public int DefaultTimeoutMs = 3000;
        public bool StopOnFirstFailure;
        public bool ContinueOnStepFailure;
        public bool RequireOfficialHost;
        public bool RequireOfficialPointerDriver;
        public bool RequireInputSystemKeyboardDriver;
        public bool EnableVerboseLog = false;
        public int PreStepDelayMs;
        public string ConfigFile;
        public string ParsedAtUtc;
    }

    /// <summary>
    /// Highlight state used by the headed overlay.
    /// </summary>
    public sealed class HighlightState
    {
        public VisualElement TargetElement;
        public string HighlightColor = "rgba(255,0,0,0.20)";
        public string OutlineColor = "#FF0000";
        public int OutlineWidthPx = 2;
        public bool IsVisible;
    }

    /// <summary>
    /// Optional hook for windows that want to rebuild their test UI before a run starts.
    /// </summary>
    public interface IUnityUIFlowTestHostWindow
    {
        void PrepareForAutomatedTest();
    }
}
