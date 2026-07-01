using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace codingriver.upilot.UIFlow
{
    [Serializable]
    public sealed class TestRunnerCaseItem
    {
        public string YamlPath;
        public string CaseName;
        public TestStatus Status;
        public int DurationMs;
        public string ErrorCode;
        public string ErrorMessage;
        public string FailedStepName;
        public string FailedStepError;
        public string ReportMarkdownPath;
        public string ReportJsonPath;
        public bool IsChecked = true;
        public bool IsRunning;
        public int TotalSteps;
        public List<StepResult> StepResults = new List<StepResult>();

        // Grouping
        public bool IsGroupHeader;
        public bool IsExpanded = true;
        public int ChildCount;
        public bool GroupMixedChecked;


    }

    [Serializable]
    public sealed class TestRunnerViewState
    {
        public string TargetDirectory = "Assets/Examples/Yaml";
        public string ReportPath = "Reports/TestRunner";
        public string SearchFilter = string.Empty;
        public bool Headed = true;
        public bool StopOnFirstFailure;
        public bool ContinueOnStepFailure;
        public bool ScreenshotOnFailure = true;
        public bool EnableVerboseLog;
        public bool RequireOfficialHost;
        public bool RequireOfficialPointerDriver;
        public bool RequireInputSystemKeyboardDriver;
        public int DefaultTimeoutMs = 3000;
        public bool IsRunning;
        public string StatusText = "Idle";
        public string CurrentYamlPath;
        public string CurrentCaseName;
        // Current-batch counters
        public int Total;
        public int Passed;
        public int Failed;
        public int Errors;
        public int Skipped;
        public HeadedFailurePolicy FailurePolicy;
        public List<TestRunnerCaseItem> Cases = new List<TestRunnerCaseItem>();

        // Batch progress info (set by external runner)
        public int BatchIndex;
        public int BatchTotal;
        public int BatchSize;

        // Overall (cross-batch) counters — set when totalAll is known
        public int OverallTotal;
        public int OverallPassed;
        public int OverallFailed;
        public int OverallErrors;
        public int OverallSkipped;
    }

    public sealed class TestRunnerWindow : EditorWindow
    {
        // ── External run sync (MCP-triggered runs) ────────────────────────────
        // All methods are safe to call from any context; they silently no-op when
        // the window is not open.  All UI mutations run on the main thread via
        // EditorApplication.delayCall so callers need not worry about threading.

        /// <summary>
        /// Notify the window (if open) that an external run is starting.
        /// Populates the case list and switches the window into "watching" mode.
        /// </summary>
        public static void ExternalRunStarted(string executionId, IEnumerable<string> yamlPaths, int total, int batchIndex = 0, int batchTotal = 0, int batchSize = 0)
        {
            EditorApplication.delayCall += () =>
            {
                var win = FindOpenWindow();
                if (win == null) return;
                win.OnExternalRunStarted(executionId, yamlPaths, total, batchIndex, batchTotal, batchSize);
            };
        }

        /// <summary>
        /// Notify the window that a single YAML case is about to start executing.
        /// </summary>
        public static void ExternalCaseStarted(string executionId, string yamlPath)
        {
            EditorApplication.delayCall += () =>
            {
                var win = FindOpenWindow();
                if (win == null || win._externalExecutionId != executionId) return;
                win.OnExternalCaseStarted(yamlPath);
            };
        }

        /// <summary>
        /// Notify the window that a case finished.  Mirrors how ExecuteBatchAsync
        /// updates a caseItem after RunFileAsync returns.
        /// </summary>
        public static void ExternalCaseFinished(
            string executionId, string yamlPath, string caseName,
            TestStatus status, int durationMs,
            string errorCode, string errorMessage,
            List<StepResult> stepResults,
            string reportMarkdownPath, string reportJsonPath)
        {
            EditorApplication.delayCall += () =>
            {
                var win = FindOpenWindow();
                if (win == null || win._externalExecutionId != executionId) return;
                win.OnExternalCaseFinished(yamlPath, caseName, status, durationMs,
                    errorCode, errorMessage, stepResults, reportMarkdownPath, reportJsonPath);
            };
        }

        /// <summary>
        /// Notify the window that the entire run finished (completed / aborted / failed).
        /// </summary>
        public static void ExternalRunFinished(string executionId, string finalStatus,
            int passed, int failed, int errors, int skipped)
        {
            EditorApplication.delayCall += () =>
            {
                var win = FindOpenWindow();
                if (win == null || win._externalExecutionId != executionId) return;
                win.OnExternalRunFinished(finalStatus, passed, failed, errors, skipped);
            };
        }

        /// <summary>
        /// Called from OnEnable: if an external run is currently active, let the caller
        /// push the current snapshot so the freshly-opened window can show partial results.
        /// Returns true when the window is open and an external run is active.
        /// </summary>
        public static bool TrySyncActiveRun(
            string executionId, IEnumerable<string> allYamlPaths, int total,
            string currentYamlPath, int passed, int failed, int errors, int skipped,
            IEnumerable<(string yamlPath, string caseName, TestStatus status, int durationMs,
                string errorCode, string errorMessage, List<StepResult> stepResults,
                string reportMarkdownPath, string reportJsonPath)> completedCases,
            int batchIndex = 0, int batchTotal = 0, int batchSize = 0)
        {
            var win = FindOpenWindow();
            if (win == null) return false;

            win.OnExternalRunStarted(executionId, allYamlPaths, total, batchIndex, batchTotal, batchSize);

            // Replay already-completed cases
            foreach (var c in completedCases)
            {
                win.OnExternalCaseFinished(c.yamlPath, c.caseName, c.status, c.durationMs,
                    c.errorCode, c.errorMessage, c.stepResults, c.reportMarkdownPath, c.reportJsonPath);
            }

            // Mark the currently-running case
            if (!string.IsNullOrEmpty(currentYamlPath))
                win.OnExternalCaseStarted(currentYamlPath);

            return true;
        }

        private static TestRunnerWindow FindOpenWindow() =>
            Resources.FindObjectsOfTypeAll<TestRunnerWindow>()
                .FirstOrDefault(w => w != null);

        // ── External run instance state ───────────────────────────────────────
        private string _externalExecutionId;

        private void OnExternalRunStarted(string executionId, IEnumerable<string> yamlPaths, int total, int batchIndex = 0, int batchTotal = 0, int batchSize = 0)
        {
            if (_state.IsRunning) return; // don't clobber an in-progress manual run
            _externalExecutionId = executionId;
            _state.IsRunning = true;
            _state.StatusText = $"MCP run in progress…";
            // total here is the overall total (totalAll), set per batch
            _state.Total = total;
            _state.OverallTotal = total; // always sync — totalAll is authoritative
            if (_state.Cases.Count == 0)
            {
                // Fresh start — reset all counters
                _state.Passed = 0;
                _state.Failed = 0;
                _state.Errors = 0;
                _state.Skipped = 0;
                _state.OverallPassed = 0;
                _state.OverallFailed = 0;
                _state.OverallErrors = 0;
                _state.OverallSkipped = 0;
            }
            else
            {
                // New batch within an ongoing multi-batch run — reset per-batch counters only
                _state.Passed = 0;
                _state.Failed = 0;
                _state.Errors = 0;
                _state.Skipped = 0;
                // Overall counters accumulate from previous batches — recompute from case list
                _state.OverallPassed = _state.Cases.Count(c => c.Status == TestStatus.Passed);
                _state.OverallFailed = _state.Cases.Count(c => c.Status == TestStatus.Failed);
                _state.OverallErrors = _state.Cases.Count(c => c.Status == TestStatus.Error);
                _state.OverallSkipped = _state.Cases.Count(c => c.Status == TestStatus.Skipped);
            }
            _state.CurrentYamlPath = null;
            _state.CurrentCaseName = null;
            _state.BatchIndex = batchIndex;
            _state.BatchTotal = batchTotal;
            _state.BatchSize = batchSize;

            // Build case items from the yaml path list
            // yamlPaths is now the full list across all batches (passed from allYamlPaths).
            // Preserve existing case results — only add new entries.
            var existing = _state.Cases.ToDictionary(
                c => c.YamlPath, c => c, StringComparer.OrdinalIgnoreCase);

            foreach (string raw in yamlPaths)
            {
                string rel = TestRunnerPathUtility.MakeProjectRelative(raw);
                if (!existing.TryGetValue(rel, out var item))
                {
                    item = new TestRunnerCaseItem
                    {
                        YamlPath = rel,
                        CaseName = Path.GetFileNameWithoutExtension(rel),
                        IsGroupHeader = true,
                        IsChecked = true,
                    };
                    _state.Cases.Add(item);
                }
                // Don't reset status of existing cases - preserve previous batch results
            }
            RefreshUi();
        }

        private void OnExternalCaseStarted(string yamlPath)
        {
            string rel = TestRunnerPathUtility.MakeProjectRelative(yamlPath);
            _state.CurrentYamlPath = rel;
            var item = _state.Cases.FirstOrDefault(c =>
                StringComparer.OrdinalIgnoreCase.Equals(c.YamlPath, rel));
            if (item != null) item.IsRunning = true;
            _state.StatusText = $"Running: {Path.GetFileNameWithoutExtension(rel)}";
            RefreshUi();
        }

        private void OnExternalCaseFinished(
            string yamlPath, string caseName, TestStatus status, int durationMs,
            string errorCode, string errorMessage,
            List<StepResult> stepResults,
            string reportMarkdownPath, string reportJsonPath)
        {
            string rel = TestRunnerPathUtility.MakeProjectRelative(yamlPath);
            var item = _state.Cases.FirstOrDefault(c =>
                StringComparer.OrdinalIgnoreCase.Equals(c.YamlPath, rel));
            if (item != null)
            {
                item.IsRunning = false;
                item.CaseName = string.IsNullOrWhiteSpace(caseName)
                    ? Path.GetFileNameWithoutExtension(rel) : caseName;
                item.Status = status;
                item.DurationMs = durationMs;
                item.ErrorCode = errorCode;
                item.ErrorMessage = errorMessage;
                item.StepResults = stepResults ?? new List<StepResult>();
                var failedStep = item.StepResults.FirstOrDefault(
                    s => s.Status == TestStatus.Failed || s.Status == TestStatus.Error);
                item.FailedStepName = failedStep?.DisplayName;
                item.FailedStepError = failedStep?.ErrorMessage;
                item.ReportMarkdownPath = reportMarkdownPath;
                item.ReportJsonPath = reportJsonPath;
            }

            // Update counters
            switch (status)
            {
                case TestStatus.Passed:  _state.Passed++;  _state.OverallPassed++;  break;
                case TestStatus.Failed:  _state.Failed++;  _state.OverallFailed++;  break;
                case TestStatus.Error:   _state.Errors++;  _state.OverallErrors++;  break;
                case TestStatus.Skipped: _state.Skipped++; _state.OverallSkipped++; break;
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(_state.CurrentYamlPath, rel))
                _state.CurrentYamlPath = null;

            _state.StatusText = status == TestStatus.Passed
                ? $"Passed: {caseName}"
                : $"{status}: {caseName}";
            RefreshUi();
            RefreshDetailPanel();
        }

        private void OnExternalRunFinished(string finalStatus, int passed, int failed, int errors, int skipped)
        {
            _state.IsRunning = false;
            _state.CurrentYamlPath = null;
            _state.CurrentCaseName = null;
            _state.Passed = passed;
            _state.Failed = failed;
            _state.Errors = errors;
            _state.Skipped = skipped;
            // Sync overall counters with whatever the batch reported as final
            _state.OverallPassed = _state.Cases.Count(c => c.Status == TestStatus.Passed);
            _state.OverallFailed = _state.Cases.Count(c => c.Status == TestStatus.Failed);
            _state.OverallErrors = _state.Cases.Count(c => c.Status == TestStatus.Error);
            _state.OverallSkipped = _state.Cases.Count(c => c.Status == TestStatus.Skipped);
            _state.StatusText = finalStatus switch
            {
                "completed" => failed > 0 || errors > 0 ? "Failed" : "Completed",
                "aborted"   => "Aborted",
                _           => "Failed",
            };
            _externalExecutionId = null;
            RefreshUi();
            RefreshDetailPanel();
        }

        // ─────────────────────────────────────────────────────────────────────

        private readonly TestRunnerViewState _state = new TestRunnerViewState();
        private CancellationTokenSource _runCts;
        private ExecutionContext _activeContext;
        private HighlightOverlayRenderer _overlayRenderer;
        private readonly Dictionary<uint, Texture2D> _progressTextures = new Dictionary<uint, Texture2D>();

        // Left panel
        private TextField _searchField;
        private Button _runAllButton;
        private Button _runSelectedButton;
        private Button _runStepButton;
        private Button _rerunFailedButton;
        private Button _cancelButton;
        private Button _refreshButton;
        private Button _selectAllButton;
        private Button _selectNoneButton;
        private Button _selectFailedButton;
        private Label _statsLabel;
        private ListView _caseListView;
        private int _lastCheckedIndex = -1;

        // Right panel (details)
        private VisualElement _detailControlRow;
        private Button _pauseButton;
        private Button _resumeButton;
        private EnumField _failurePolicyField;
        private VisualElement _detailNameRow;
        private VisualElement _detailPathRow;
        private VisualElement _detailStatusRow;
        private VisualElement _detailDurationRow;
        private VisualElement _detailStepsRow;
        private Label _detailErrorLabel;
        private VisualElement _detailStepsContainer;
        private Button _detailOpenReportButton;
        private Button _detailOpenYamlButton;

        // Bottom status
        private Label _statusLabel;
        private Label _currentCaseLabel;

        [MenuItem("upilot/UIFlow/Test Runner", priority = 102)]
        public static void Open()
        {
            TestRunnerWindow window = GetWindow<TestRunnerWindow>();
            window.titleContent = new GUIContent("upilot UIFlow");
            window.minSize = new Vector2(900f, 600f);
            window.Show();
        }

        public static void OpenLegacy()
        {
            Open();
        }

        /// <summary>
        /// Optional supplier: registered by the MCP service so that when the window
        /// opens mid-run it can immediately pull the active execution snapshot.
        /// Signature: action(window) — implementor calls TrySyncActiveRun.
        /// </summary>
        public static Action<TestRunnerWindow> OnWindowOpened;

        private void OnEnable()
        {
            TestRunnerPreferences.Load(_state);
            if (string.IsNullOrWhiteSpace(_state.TargetDirectory))
            {
                _state.TargetDirectory = "Assets/Examples/Yaml";
            }

            if (string.IsNullOrWhiteSpace(_state.ReportPath))
            {
                _state.ReportPath = "Reports/TestRunner";
            }

            _overlayRenderer = new HighlightOverlayRenderer();
            SubscribeEvents();
            BuildUi();
            RefreshCaseList();
            RefreshUi();

            // If an external (MCP) run is in progress, sync its current state.
            try { OnWindowOpened?.Invoke(this); } catch { /* never break OnEnable */ }
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
            _overlayRenderer?.Detach();
            _overlayRenderer = null;
            foreach (var tex in _progressTextures.Values)
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
            _progressTextures.Clear();
            TestRunnerPreferences.Save(_state);
        }

        private void SubscribeEvents()
        {
            HeadedRunEventBus.RunAttached += OnRunAttached;
            HeadedRunEventBus.StepStarted += OnStepStarted;
            HeadedRunEventBus.StepCompleted += OnStepCompleted;
            HeadedRunEventBus.HighlightedElementChanged += OnHighlightedElementChanged;
            HeadedRunEventBus.Failure += OnFailure;
            HeadedRunEventBus.RunFinished += OnRunFinished;
        }

        private void UnsubscribeEvents()
        {
            HeadedRunEventBus.RunAttached -= OnRunAttached;
            HeadedRunEventBus.StepStarted -= OnStepStarted;
            HeadedRunEventBus.StepCompleted -= OnStepCompleted;
            HeadedRunEventBus.HighlightedElementChanged -= OnHighlightedElementChanged;
            HeadedRunEventBus.Failure -= OnFailure;
            HeadedRunEventBus.RunFinished -= OnRunFinished;
        }

        private void BuildUi()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            // ── Top Toolbar ──
            var toolbar = new VisualElement();
            toolbar.name = "test-runner-toolbar";
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.paddingLeft = 12;
            toolbar.style.paddingRight = 12;
            toolbar.style.paddingTop = 8;
            toolbar.style.paddingBottom = 8;
            toolbar.style.height = 48;
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new Color(0.15f, 0.15f, 0.15f);
            toolbar.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f);
            toolbar.style.flexShrink = 0;
            rootVisualElement.Add(toolbar);

            _runAllButton = CreateToolbarButton("Run All", RunAll);
            _runSelectedButton = CreateToolbarButton("Run Selected", RunSelected);
            _runStepButton = CreateToolbarButton("Run Step", RunStep);
            _rerunFailedButton = CreateToolbarButton("Rerun Failed", RunFailed);
            _cancelButton = CreateToolbarButton("Cancel", CancelRun);
            _pauseButton = CreateToolbarButton("Pause", () => _activeContext?.RuntimeController?.Pause());
            _pauseButton.style.marginLeft = 8;
            _pauseButton.style.marginRight = 4;
            _resumeButton = CreateToolbarButton("Resume", () => _activeContext?.RuntimeController?.Resume());
            _resumeButton.style.marginRight = 4;
            _refreshButton = CreateToolbarButton("Refresh", RefreshCaseList);
            var clearButton = CreateToolbarButton("Clear Results", ClearResults);
            toolbar.Add(_runAllButton);
            toolbar.Add(_runSelectedButton);
            toolbar.Add(_runStepButton);
            toolbar.Add(_rerunFailedButton);
            toolbar.Add(_cancelButton);
            toolbar.Add(_pauseButton);
            toolbar.Add(_resumeButton);
            toolbar.Add(_refreshButton);
            toolbar.Add(clearButton);

            toolbar.Add(new VisualElement { style = { flexGrow = 1 } });

            _statsLabel = new Label("分组: 0  用例: 0  通过: 0  失败: 0  错误: 0  跳过: 0") { name = "test-runner-stats" };
            _statsLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _statsLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            toolbar.Add(_statsLabel);

            // ── Split View (Left list + Right details) ──
            var splitView = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal);
            splitView.name = "test-runner-split";
            splitView.style.flexGrow = 1;
            rootVisualElement.Add(splitView);

            // Left: Case list
            var leftPanel = new VisualElement();
            leftPanel.name = "test-runner-left";
            leftPanel.style.flexDirection = FlexDirection.Column;
            leftPanel.style.minWidth = 200;

            // Selection toolbar
            var selectionBar = new VisualElement();
            selectionBar.style.flexDirection = FlexDirection.Row;
            selectionBar.style.alignItems = Align.Center;
            selectionBar.style.paddingLeft = 8;
            selectionBar.style.paddingRight = 8;
            selectionBar.style.paddingTop = 6;
            selectionBar.style.paddingBottom = 6;
            selectionBar.style.borderBottomWidth = 1;
            selectionBar.style.borderBottomColor = new Color(0.18f, 0.18f, 0.18f);
            leftPanel.Add(selectionBar);

            _selectAllButton = CreateSmallButton("Select All", SelectAll);
            _selectNoneButton = CreateSmallButton("Select None", SelectNone);
            _selectFailedButton = CreateSmallButton("Select Failed", SelectFailed);
            selectionBar.Add(_selectAllButton);
            selectionBar.Add(_selectNoneButton);
            selectionBar.Add(_selectFailedButton);

            selectionBar.Add(new VisualElement { style = { flexGrow = 1 } });

            var timeoutField = new IntegerField("Timeout(ms)")
            {
                name = "test-runner-timeout",
                value = _state.DefaultTimeoutMs,
            };
            timeoutField.style.width = 90;
            timeoutField.SetEnabled(!_state.IsRunning);
            timeoutField.RegisterValueChangedCallback(evt =>
            {
                _state.DefaultTimeoutMs = Mathf.Clamp(evt.newValue, 100, 600000);
                TestRunnerPreferences.Save(_state);
            });
            selectionBar.Add(timeoutField);

            _searchField = new TextField { name = "test-runner-search" };
            _searchField.style.width = 160;
            _searchField.tooltip = "Search by case name or YAML path";
            _searchField.SetValueWithoutNotify(_state.SearchFilter);
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _state.SearchFilter = evt.newValue ?? string.Empty;
                RefreshCaseList();
            });
            selectionBar.Add(_searchField);

            _caseListView = new ListView
            {
                name = "test-runner-list",
                selectionType = SelectionType.Single,
                reorderable = false,
                showBorder = false,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                fixedItemHeight = 26,
                style = { flexGrow = 1 },
            };
            _caseListView.makeItem = MakeCaseListItem;
            _caseListView.bindItem = BindCaseListItem;
            _caseListView.selectionChanged += OnSelectionChanged;
            _caseListView.RegisterCallback<KeyDownEvent>(OnListKeyDown);
            leftPanel.Add(_caseListView);
            splitView.Add(leftPanel);

            // Right: Details panel
            var rightPanel = new VisualElement();
            rightPanel.name = "test-runner-right";
            rightPanel.style.flexDirection = FlexDirection.Column;
            rightPanel.style.paddingLeft = 12;
            rightPanel.style.paddingRight = 12;
            rightPanel.style.paddingTop = 12;
            rightPanel.style.paddingBottom = 12;
            rightPanel.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);

            // ── Debug control row (sticky header above scroll view) ──
            _detailControlRow = new VisualElement();
            _detailControlRow.name = "test-runner-debug-controls";
            _detailControlRow.style.flexDirection = FlexDirection.Row;
            _detailControlRow.style.alignItems = Align.Center;
            _detailControlRow.style.paddingBottom = 8;
            _detailControlRow.style.marginBottom = 8;
            _detailControlRow.style.borderBottomWidth = 1;
            _detailControlRow.style.borderBottomColor = new Color(0.18f, 0.18f, 0.18f);
            rightPanel.Add(_detailControlRow);

            var failurePolicyContainer = new VisualElement();
            failurePolicyContainer.style.flexDirection = FlexDirection.Row;
            failurePolicyContainer.style.alignItems = Align.Center;
            failurePolicyContainer.style.marginRight = 8;
            var failurePolicyLabel = new Label("On Fail") { style = { marginRight = 2, color = new Color(0.6f, 0.6f, 0.6f), fontSize = 10 } };
            _failurePolicyField = new EnumField(_state.FailurePolicy) { style = { fontSize = 10 } };
            _failurePolicyField.RegisterValueChangedCallback(evt =>
            {
                _state.FailurePolicy = (HeadedFailurePolicy)evt.newValue;
                TestRunnerPreferences.Save(_state);
            });
            failurePolicyContainer.Add(failurePolicyLabel);
            failurePolicyContainer.Add(_failurePolicyField);
            _detailControlRow.Add(failurePolicyContainer);

            var scrollView = new ScrollView();
            scrollView.name = "test-runner-detail-scroll";
            scrollView.style.flexGrow = 1;
            scrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
            rightPanel.Add(scrollView);

            var detailTitle = new Label("Test Details") { name = "test-runner-detail-title" };
            detailTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            detailTitle.style.fontSize = 14;
            detailTitle.style.marginBottom = 10;
            scrollView.Add(detailTitle);

            _detailNameRow = CreateDetailRow("Name:", "-");
            _detailPathRow = CreateDetailRow("YAML Path:", "-");
            _detailStatusRow = CreateDetailRow("Status:", "-");
            _detailDurationRow = CreateDetailRow("Duration:", "-");
            _detailStepsRow = CreateDetailRow("Steps:", "-");
            scrollView.Add(_detailNameRow);
            scrollView.Add(_detailPathRow);
            scrollView.Add(_detailStatusRow);
            scrollView.Add(_detailDurationRow);
            scrollView.Add(_detailStepsRow);

            var errorTitle = new Label("Error") { name = "test-runner-error-title" };
            errorTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            errorTitle.style.marginTop = 12;
            errorTitle.style.marginBottom = 4;
            scrollView.Add(errorTitle);

            _detailErrorLabel = new Label("No error") { name = "test-runner-error" };
            _detailErrorLabel.style.whiteSpace = WhiteSpace.Normal;
            _detailErrorLabel.style.color = new Color(0.9f, 0.3f, 0.3f);
            scrollView.Add(_detailErrorLabel);

            var stepsTitle = new Label("Steps") { name = "test-runner-steps-title" };
            stepsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            stepsTitle.style.marginTop = 12;
            stepsTitle.style.marginBottom = 4;
            scrollView.Add(stepsTitle);

            _detailStepsContainer = new VisualElement { name = "test-runner-steps" };
            scrollView.Add(_detailStepsContainer);

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.marginTop = 10;
            rightPanel.Add(actionRow);

            _detailOpenYamlButton = new Button(() =>
            {
                var selected = GetSelectedItem();
                if (selected != null && File.Exists(selected.YamlPath))
                {
                    AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(selected.YamlPath));
                }
            })
            { text = "Open YAML" };
            _detailOpenYamlButton.style.marginRight = 6;

            _detailOpenReportButton = new Button(() =>
            {
                var selected = GetSelectedItem();
                if (selected != null && !string.IsNullOrWhiteSpace(selected.ReportMarkdownPath) && File.Exists(selected.ReportMarkdownPath))
                {
                    EditorUtility.OpenWithDefaultApp(selected.ReportMarkdownPath);
                }
            })
            { text = "Open Report" };
            _detailOpenReportButton.style.marginRight = 6;

            actionRow.Add(_detailOpenYamlButton);
            actionRow.Add(_detailOpenReportButton);
            splitView.Add(rightPanel);

            // ── Progress Bar ──
            var progressBar = new ProgressBar { name = "test-runner-progress" };
            progressBar.style.height = 18;
            progressBar.style.marginTop = 0;
            progressBar.style.marginBottom = 0;
            progressBar.style.flexShrink = 0;
            progressBar.lowValue = 0;
            progressBar.highValue = 100;
            progressBar.value = 0;
            rootVisualElement.Add(progressBar);

            // ── Bottom Status Bar ──
            var statusBar = new VisualElement();
            statusBar.name = "test-runner-status-bar";
            statusBar.style.flexDirection = FlexDirection.Row;
            statusBar.style.alignItems = Align.Center;
            statusBar.style.height = 36;
            statusBar.style.flexShrink = 0;
            statusBar.style.paddingLeft = 12;
            statusBar.style.paddingRight = 12;
            statusBar.style.paddingTop = 0;
            statusBar.style.paddingBottom = 0;
            statusBar.style.borderTopWidth = 1;
            statusBar.style.borderTopColor = new Color(0.15f, 0.15f, 0.15f);
            statusBar.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f);
            rootVisualElement.Add(statusBar);

            _statusLabel = new Label("Status: Idle") { name = "test-runner-status" };
            _statusLabel.style.fontSize = 13;
            _currentCaseLabel = new Label("Current: -") { name = "test-runner-current" };
            _currentCaseLabel.style.fontSize = 13;
            statusBar.Add(_statusLabel);
            statusBar.Add(new VisualElement { style = { width = 20 } });
            statusBar.Add(_currentCaseLabel);
        }

        private Button CreateToolbarButton(string text, Action onClick)
        {
            var btn = new Button(onClick) { text = text };
            btn.style.marginRight = 4;
            btn.style.height = 24;
            btn.style.fontSize = 10;
            return btn;
        }

        private Button CreateSmallButton(string text, Action onClick)
        {
            var btn = new Button(onClick) { text = text };
            btn.style.marginRight = 4;
            btn.style.height = 20;
            btn.style.fontSize = 10;
            return btn;
        }

        private VisualElement CreateDetailRow(string label, string value)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 4;

            var labelElem = new Label(label);
            labelElem.style.width = 80;
            labelElem.style.color = new Color(0.6f, 0.6f, 0.6f);
            labelElem.style.minWidth = 80;

            var valueElem = new Label(value);
            valueElem.style.flexGrow = 1;
            valueElem.style.whiteSpace = WhiteSpace.Normal;

            row.Add(labelElem);
            row.Add(valueElem);
            return row;
        }

        // ── ListView item rendering ──

        private VisualElement MakeCaseListItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;
            row.style.paddingRight = 4;
            row.style.height = 26;

            var expandLabel = new Label { name = "case-expand" };
            expandLabel.style.width = 16;
            expandLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

            var checkToggle = new Toggle { name = "case-check" };
            checkToggle.style.marginRight = 4;
            checkToggle.style.marginLeft = 2;
            checkToggle.style.width = 18;

            var statusLabel = new VisualElement { name = "case-status" };
            statusLabel.style.width = 16;
            statusLabel.style.height = 16;
            statusLabel.style.marginRight = 4;
            statusLabel.style.flexShrink = 0;

            var nameLabel = new Label { name = "case-name" };
            nameLabel.style.flexGrow = 1;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;

            var countLabel = new Label { name = "case-count" };
            countLabel.style.width = 45;
            countLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            countLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            countLabel.style.fontSize = 11;

            var durationLabel = new Label { name = "case-duration" };
            durationLabel.style.width = 55;
            durationLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            durationLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            durationLabel.style.fontSize = 10;

            row.Add(expandLabel);
            row.Add(checkToggle);
            row.Add(statusLabel);
            row.Add(nameLabel);
            row.Add(countLabel);
            row.Add(durationLabel);

            row.RegisterCallback<PointerDownEvent>(OnCaseItemPointerDown);

            checkToggle.RegisterValueChangedCallback(evt =>
            {
                int idx = (int)(row.userData ?? -1);
                if (idx < 0) return;
                var filtered = GetFilteredCases();
                if (idx >= filtered.Count) return;
                var item = filtered[idx];
                item.IsChecked = evt.newValue;
                _caseListView?.Rebuild();
            });
            checkToggle.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

            return row;
        }

        private void OnCaseItemPointerDown(PointerDownEvent evt)
        {
            int idx = (int)((evt.currentTarget as VisualElement)?.userData ?? -1);
            if (idx < 0) return;

            var filtered = GetFilteredCases();
            if (idx >= filtered.Count) return;

            bool isCtrl = (evt.modifiers & EventModifiers.Control) != 0;
            bool isShift = (evt.modifiers & EventModifiers.Shift) != 0;

            if (isShift && _lastCheckedIndex >= 0 && _lastCheckedIndex < filtered.Count)
            {
                int start = Mathf.Min(_lastCheckedIndex, idx);
                int end = Mathf.Max(_lastCheckedIndex, idx);
                for (int i = start; i <= end; i++)
                {
                    filtered[i].IsChecked = true;
                }
            }
            else if (isCtrl)
            {
                filtered[idx].IsChecked = !filtered[idx].IsChecked;
                _lastCheckedIndex = idx;
            }
            else
            {
                foreach (var item in filtered)
                    item.IsChecked = false;
                filtered[idx].IsChecked = true;
                _lastCheckedIndex = idx;
            }

            _caseListView?.Rebuild();
        }

        private void OnListKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Space || _state.IsRunning) return;
            evt.StopPropagation();

            var selected = GetSelectedItem();
            if (selected == null) return;

            selected.IsChecked = !selected.IsChecked;
            _caseListView?.Rebuild();
        }

        private void BindCaseListItem(VisualElement element, int index)
        {
            element.userData = index;
            var filtered = GetFilteredCases();
            if (index < 0 || index >= filtered.Count)
            {
                // Hide empty rows that are beyond the data range
                element.style.display = DisplayStyle.None;
                return;
            }

            // Ensure visible for valid data rows
            element.style.display = DisplayStyle.Flex;

            TestRunnerCaseItem item = filtered[index];

            var expandLabel = element.Q<Label>("case-expand");
            var checkToggle = element.Q<Toggle>("case-check");
            var statusLabel = element.Q<VisualElement>("case-status");
            var nameLabel = element.Q<Label>("case-name");
            var countLabel = element.Q<Label>("case-count");
            var durationLabel = element.Q<Label>("case-duration");

            if (item.IsGroupHeader)
            {
                expandLabel.style.visibility = Visibility.Hidden;

                checkToggle.style.visibility = Visibility.Visible;
                checkToggle.SetValueWithoutNotify(item.IsChecked);
                checkToggle.showMixedValue = item.GroupMixedChecked;
                checkToggle.SetEnabled(!_state.IsRunning);

                statusLabel.style.visibility = Visibility.Visible;
                if (item.IsRunning && item.TotalSteps > 0)
                {
                    double progress = (double)item.StepResults.Count / item.TotalSteps;
                    var tex = GetProgressTexture((float)progress, new Color(0.3f, 0.6f, 1f));
                    statusLabel.style.backgroundImage = Background.FromTexture2D(tex);
                }
                else
                {
                    var tex = GetStatusTexture(item.Status, item.IsRunning);
                    statusLabel.style.backgroundImage = tex != null ? Background.FromTexture2D(tex) : null;
                }

                nameLabel.text = item.CaseName;
                nameLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                nameLabel.style.marginLeft = 0;

                if (item.IsRunning && item.TotalSteps > 0)
                {
                    countLabel.style.visibility = Visibility.Visible;
                    countLabel.text = $"{item.StepResults.Count}/{item.TotalSteps}";
                }
                else
                {
                    countLabel.style.visibility = Visibility.Hidden;
                }

                durationLabel.style.visibility = Visibility.Hidden;
            }
            else
            {
                expandLabel.style.visibility = Visibility.Hidden;

                checkToggle.style.visibility = Visibility.Visible;
                checkToggle.SetValueWithoutNotify(item.IsChecked);
                checkToggle.showMixedValue = false;
                checkToggle.SetEnabled(!_state.IsRunning);

                statusLabel.style.visibility = Visibility.Visible;
                var tex = GetStatusTexture(item.Status, item.IsRunning);
                statusLabel.style.backgroundImage = tex != null ? Background.FromTexture2D(tex) : null;

                nameLabel.text = item.CaseName ?? Path.GetFileNameWithoutExtension(item.YamlPath);
                nameLabel.style.marginLeft = 8;
                if (item.IsRunning)
                {
                    nameLabel.style.color = new Color(0.4f, 0.7f, 1f);
                    nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
                else if (item.Status == TestStatus.Failed || item.Status == TestStatus.Error)
                {
                    nameLabel.style.color = new Color(1f, 0.35f, 0.35f);
                    nameLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
                }
                else if (item.Status == TestStatus.Passed)
                {
                    nameLabel.style.color = new Color(0.5f, 1f, 0.5f);
                    nameLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
                }
                else
                {
                    nameLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                    nameLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
                }

                countLabel.style.visibility = Visibility.Hidden;

                durationLabel.style.visibility = Visibility.Visible;
                durationLabel.text = item.DurationMs > 0 ? $"{item.DurationMs}ms" : string.Empty;
            }
        }

        private static string GetStatusIcon(TestStatus status, bool isRunning)
        {
            if (isRunning) return "◐";
            switch (status)
            {
                case TestStatus.Passed: return "●";
                case TestStatus.Failed: return "●";
                case TestStatus.Error: return "●";
                case TestStatus.Skipped: return "○";
                case TestStatus.None: return "○";
                default: return "○";
            }
        }

        private Texture2D GetProgressTexture(float progress, Color tint)
        {
            progress = Mathf.Clamp01(progress);
            float discrete = progress <= 0f ? 0f : progress <= 0.25f ? 0.25f : progress <= 0.5f ? 0.5f : progress <= 0.75f ? 0.75f : 1f;
            uint key = ((uint)(discrete * 100f) << 24)
                     | ((uint)(tint.r * 255f) << 16)
                     | ((uint)(tint.g * 255f) << 8)
                     | ((uint)(tint.b * 255f));
            if (_progressTextures.TryGetValue(key, out var existing) && existing != null)
                return existing;

            int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.hideFlags = HideFlags.HideAndDontSave;
            var pixels = new Color[size * size];

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float outerR = size * 0.5f - 1f;
            float innerR = outerR - 1.5f;

            Color borderColor = new Color(tint.r, tint.g, tint.b, 0.45f);
            Color fillColor = new Color(tint.r, tint.g, tint.b, 0.9f);
            Color emptyColor = new Color(0.12f, 0.12f, 0.12f, 0.35f);
            Color bgColor = Color.clear;

            float maxAngle = discrete * 360f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    int idx = y * size + x;

                    if (dist > outerR)
                    {
                        pixels[idx] = bgColor;
                        continue;
                    }

                    if (dist >= innerR)
                    {
                        pixels[idx] = borderColor;
                        continue;
                    }

                    // Inside the circle: determine if this pixel is within the filled sector
                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    angle = (angle + 450f) % 360f; // Rotate so 0 is at top, clockwise

                    if (angle <= maxAngle)
                        pixels[idx] = fillColor;
                    else
                        pixels[idx] = emptyColor;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _progressTextures[key] = tex;
            return tex;
        }

        private Texture2D GetStatusTexture(TestStatus status, bool isRunning)
        {
            if (isRunning)
                return GetProgressTexture(0.5f, new Color(0.3f, 0.6f, 1f));

            switch (status)
            {
                case TestStatus.Passed:
                    return GetProgressTexture(1.0f, new Color(0.3f, 0.9f, 0.3f));
                case TestStatus.Failed:
                    return GetProgressTexture(1.0f, new Color(0.95f, 0.3f, 0.3f));
                case TestStatus.Error:
                    return GetProgressTexture(1.0f, new Color(0.9f, 0.15f, 0.15f));
                case TestStatus.Skipped:
                    return GetProgressTexture(0.0f, new Color(0.5f, 0.5f, 0.5f));
                case TestStatus.None:
                default:
                    return GetProgressTexture(0.0f, new Color(0.45f, 0.45f, 0.45f));
            }
        }

        private static Color GetStatusColor(TestStatus status, bool isRunning)
        {
            if (isRunning) return new Color(0.3f, 0.6f, 1f);
            switch (status)
            {
                case TestStatus.Passed: return new Color(0.3f, 0.9f, 0.3f);
                case TestStatus.Failed: return new Color(0.95f, 0.3f, 0.3f);
                case TestStatus.Error: return new Color(0.9f, 0.15f, 0.15f);
                case TestStatus.Skipped: return new Color(0.5f, 0.5f, 0.5f);
                case TestStatus.None: return new Color(0.45f, 0.45f, 0.45f);
                default: return new Color(0.45f, 0.45f, 0.45f);
            }
        }

        // ── Group check state helpers ──

        private void SetGroupChecks(string yamlPath, bool value)
        {
            foreach (var item in _state.Cases)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(item.YamlPath, yamlPath))
                {
                    item.IsChecked = value;
                }
            }
        }

        private void RefreshGroupCheckState(string yamlPath)
        {
            // No-op: flat list, each item manages its own check state
        }

        private void RefreshAllGroupCheckStates()
        {
            // No-op: flat list
        }

        // ── Selection & Filtering ──

        private List<TestRunnerCaseItem> GetFilteredCases()
        {
            if (string.IsNullOrWhiteSpace(_state.SearchFilter))
            {
                return _state.Cases.ToList();
            }

            string filter = _state.SearchFilter.Trim();
            return _state.Cases.Where(c =>
                (c.CaseName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private TestRunnerCaseItem GetSelectedItem()
        {
            var filtered = GetFilteredCases();
            int index = _caseListView?.selectedIndex ?? -1;
            if (index >= 0 && index < filtered.Count)
            {
                return filtered[index];
            }
            return null;
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            RefreshDetailPanel();
        }

        private void RefreshDetailPanel()
        {
            var selected = GetSelectedItem();
            if (selected == null)
            {
                SetDetailValue(_detailNameRow, "-");
                SetDetailValue(_detailPathRow, "-");
                SetDetailValue(_detailStatusRow, "-");
                SetDetailValue(_detailDurationRow, "-");
                SetDetailValue(_detailStepsRow, "-");
                _detailErrorLabel.text = "No error";
                _detailStepsContainer.Clear();
                _detailOpenYamlButton.SetEnabled(false);
                _detailOpenReportButton.SetEnabled(false);
                return;
            }

            // Resolve the actual test case name from YAML
            string caseNameFromYaml = Path.GetFileNameWithoutExtension(selected.YamlPath);
            try
            {
                var parser = new YamlTestCaseParser();
                TestCaseDefinition definition = parser.ParseFile(selected.YamlPath);
                if (!string.IsNullOrWhiteSpace(definition.Name))
                {
                    caseNameFromYaml = definition.Name;
                }
            }
            catch
            {
                // Fallback to file name
            }

            SetDetailValue(_detailNameRow, caseNameFromYaml);
            SetDetailValue(_detailPathRow, selected.YamlPath);
            SetDetailValue(_detailStatusRow, selected.Status.ToString());
            SetDetailValue(_detailDurationRow, selected.DurationMs > 0 ? $"{selected.DurationMs}ms" : "-");

            if (!string.IsNullOrWhiteSpace(selected.ErrorMessage))
            {
                string errorText = selected.ErrorMessage;
                if (!string.IsNullOrWhiteSpace(selected.FailedStepName))
                {
                    errorText = $"Step '{selected.FailedStepName}': {errorText}";
                }
                _detailErrorLabel.text = errorText;
            }
            else
            {
                _detailErrorLabel.text = "No error";
            }

            _detailStepsContainer.Clear();
            int stepCount = RenderStepDetails(selected);
            SetDetailValue(_detailStepsRow, stepCount > 0 ? $"{stepCount}" : "-");

            _detailOpenYamlButton.SetEnabled(File.Exists(selected.YamlPath));
            _detailOpenReportButton.SetEnabled(!string.IsNullOrWhiteSpace(selected.ReportMarkdownPath) && File.Exists(selected.ReportMarkdownPath));
        }

        private int RenderStepDetails(TestRunnerCaseItem selected)
        {
            TestCaseDefinition definition = null;
            try
            {
                var parser = new YamlTestCaseParser();
                definition = parser.ParseFile(selected.YamlPath);
            }
            catch
            {
                _detailStepsContainer.Add(new Label("Unable to parse step definitions from YAML."));
                return 0;
            }

            var setupInfos = new List<(string Name, string PhaseLabel)>();
            var mainInfos = new List<(string Name, string PhaseLabel)>();
            var teardownInfos = new List<(string Name, string PhaseLabel)>();

            if (definition.Fixture?.Setup != null)
            {
                foreach (var step in definition.Fixture.Setup)
                    setupInfos.Add((step.Name ?? step.Action ?? "setup", "Setup"));
            }
            if (definition.Steps != null)
            {
                foreach (var step in definition.Steps)
                    mainInfos.Add((step.Name ?? step.Action ?? "step", "Main"));
            }
            if (definition.Fixture?.Teardown != null)
            {
                foreach (var step in definition.Fixture.Teardown)
                    teardownInfos.Add((step.Name ?? step.Action ?? "teardown", "Teardown"));
            }

            int templateStepCount = setupInfos.Count + mainInfos.Count + teardownInfos.Count;
            if (templateStepCount == 0)
            {
                _detailStepsContainer.Add(new Label("No steps defined in this test case."));
                return 0;
            }

            // Resolve actual rows (including CSV/JSON data sources)
            List<Dictionary<string, string>> resolvedRows;
            try
            {
                resolvedRows = TestDataResolver.ResolveRows(definition);
            }
            catch
            {
                resolvedRows = definition.Data?.Rows ?? new List<Dictionary<string, string>>();
            }

            int rowCount = resolvedRows.Count;
            if (rowCount == 0) rowCount = 1;

            // Group results by iteration index
            var resultsByIteration = new Dictionary<int, List<StepResult>>();
            if (selected.StepResults != null)
            {
                foreach (var sr in selected.StepResults)
                {
                    int idx = sr.IterationIndex;
                    if (!resultsByIteration.ContainsKey(idx))
                        resultsByIteration[idx] = new List<StepResult>();
                    resultsByIteration[idx].Add(sr);
                }
            }

            // Ensure we iterate over all iterations that have results or template rows
            int maxIteration = Math.Max(rowCount, resultsByIteration.Count > 0 ? resultsByIteration.Keys.Max() + 1 : 0);
            bool hasMultipleIterations = maxIteration > 1;
            int displayedStepCount = 0;

            for (int iteration = 0; iteration < maxIteration; iteration++)
            {
                if (hasMultipleIterations)
                {
                    string iterationTitle = $"Iteration {iteration + 1}";
                    if (iteration < resolvedRows.Count && resolvedRows[iteration].Count > 0)
                    {
                        var rowData = resolvedRows[iteration];
                        string dataSummary = string.Join(", ", rowData.Select(kv => $"{kv.Key}={kv.Value}"));
                        iterationTitle += $" ({dataSummary})";
                    }

                    var headerRow = new VisualElement();
                    headerRow.style.flexDirection = FlexDirection.Row;
                    headerRow.style.marginTop = iteration == 0 ? 0 : 10;
                    headerRow.style.marginBottom = 4;
                    headerRow.style.alignItems = Align.Center;

                    var headerIcon = new Label("▸");
                    headerIcon.style.width = 16;
                    headerIcon.style.color = new Color(0.6f, 0.8f, 1f);
                    headerIcon.style.unityFontStyleAndWeight = FontStyle.Bold;

                    var headerLabel = new Label(iterationTitle);
                    headerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    headerLabel.style.color = new Color(0.6f, 0.8f, 1f);
                    headerLabel.style.fontSize = 12;

                    headerRow.Add(headerIcon);
                    headerRow.Add(headerLabel);
                    _detailStepsContainer.Add(headerRow);
                }

                resultsByIteration.TryGetValue(iteration, out var iterationResults);
                var setupResults = iterationResults?.Where(r => r.Phase == StepPhase.Setup).ToList();
                var mainResults = iterationResults?.Where(r => r.Phase == StepPhase.Main).ToList();
                var teardownResults = iterationResults?.Where(r => r.Phase == StepPhase.Teardown).ToList();

                for (int i = 0; i < setupInfos.Count; i++)
                {
                    var (name, phaseLabel) = setupInfos[i];
                    StepResult result = setupResults != null && i < setupResults.Count ? setupResults[i] : null;
                    RenderStepRow(name, phaseLabel, result, i + 1);
                    displayedStepCount++;
                }

                for (int i = 0; i < mainInfos.Count; i++)
                {
                    var (name, phaseLabel) = mainInfos[i];
                    StepResult result = mainResults != null && i < mainResults.Count ? mainResults[i] : null;
                    RenderStepRow(name, phaseLabel, result, setupInfos.Count + i + 1);
                    displayedStepCount++;
                }

                for (int i = 0; i < teardownInfos.Count; i++)
                {
                    var (name, phaseLabel) = teardownInfos[i];
                    StepResult result = teardownResults != null && i < teardownResults.Count ? teardownResults[i] : null;
                    RenderStepRow(name, phaseLabel, result, setupInfos.Count + mainInfos.Count + i + 1);
                    displayedStepCount++;
                }
            }

            return displayedStepCount;
        }

        private void RenderStepRow(string name, string phaseLabel, StepResult result, int stepNumber)
        {
            string displayName = string.IsNullOrWhiteSpace(name) ? $"{phaseLabel} step {stepNumber}" : name;
            if (result == null && phaseLabel != "Main")
                displayName = $"[{phaseLabel}] {displayName}";

            var stepRow = new VisualElement();
            stepRow.style.flexDirection = FlexDirection.Row;
            stepRow.style.marginBottom = 2;
            stepRow.style.alignItems = Align.Center;

            var icon = new Label(result != null ? GetStatusIcon(result.Status, false) : "○");
            icon.style.width = 16;
            icon.style.color = result != null ? GetStatusColor(result.Status, false) : new Color(0.45f, 0.45f, 0.45f);

            var nameLabel = new Label(displayName);
            nameLabel.style.flexGrow = 1;
            nameLabel.style.color = result != null ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.5f, 0.5f, 0.5f);

            var durLabel = new Label(result != null && result.DurationMs > 0 ? $"{result.DurationMs}ms" : "");
            durLabel.style.width = 50;
            durLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            durLabel.style.fontSize = 10;
            durLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            stepRow.Add(icon);
            stepRow.Add(nameLabel);
            stepRow.Add(durLabel);
            _detailStepsContainer.Add(stepRow);
        }

        private static void SetDetailValue(VisualElement row, string value)
        {
            // Row is [Label, Label]; set the second child
            if (row.childCount > 1 && row.ElementAt(1) is Label valueLabel)
            {
                valueLabel.text = value;
            }
        }

        // ── Case discovery ──

        private void RefreshCaseList()
        {
            string directory = _state.TargetDirectory;
            if (!Directory.Exists(directory))
            {
                _state.Cases.Clear();
                _caseListView.itemsSource = GetFilteredCases();
                _caseListView.Rebuild();
                RefreshUi();
                return;
            }

            var yamlFiles = Directory.GetFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Preserve existing items when possible
            var existingGroups = new Dictionary<string, TestRunnerCaseItem>(StringComparer.OrdinalIgnoreCase);
            var existingChildren = new Dictionary<string, List<TestRunnerCaseItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _state.Cases)
            {
                string key = TestRunnerPathUtility.MakeProjectRelative(item.YamlPath);
                if (item.IsGroupHeader)
                {
                    existingGroups[key] = item;
                }
                else
                {
                    if (!existingChildren.ContainsKey(key))
                        existingChildren[key] = new List<TestRunnerCaseItem>();
                    existingChildren[key].Add(item);
                }
            }

            // Preserve expanded states
            var expandedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _state.Cases)
            {
                if (item.IsGroupHeader && item.IsExpanded)
                {
                    expandedGroups.Add(item.YamlPath);
                }
            }

            _state.Cases.Clear();
            var parser = new YamlTestCaseParser();
            foreach (string yamlPath in yamlFiles)
            {
                string rel = TestRunnerPathUtility.MakeProjectRelative(yamlPath);
                string fileName = Path.GetFileName(yamlPath);

                if (existingGroups.TryGetValue(rel, out var oldGroup))
                {
                    if (oldGroup.TotalSteps == 0)
                    {
                        try
                        {
                            TestCaseDefinition definition = parser.ParseFile(yamlPath);
                            oldGroup.TotalSteps = ComputeTotalSteps(definition);
                        }
                        catch
                        {
                            // Ignore parse errors
                        }
                    }
                    _state.Cases.Add(oldGroup);
                    continue;
                }

                // Parse YAML to get case name
                string caseName = Path.GetFileNameWithoutExtension(yamlPath);
                try
                {
                    TestCaseDefinition definition = parser.ParseFile(yamlPath);
                    if (!string.IsNullOrWhiteSpace(definition.Name))
                    {
                        caseName = definition.Name;
                    }
                }
                catch
                {
                    // Fallback to file name if parse fails
                }

                // One item per file, displaying file name; case name shown in detail panel
                int totalSteps = 0;
                try
                {
                    TestCaseDefinition definition = parser.ParseFile(yamlPath);
                    totalSteps = ComputeTotalSteps(definition);
                }
                catch
                {
                    // Ignore parse errors for step counting
                }

                _state.Cases.Add(new TestRunnerCaseItem
                {
                    YamlPath = rel,
                    CaseName = caseName,
                    IsGroupHeader = true,
                    IsChecked = true,
                    TotalSteps = totalSteps,
                });
            }

            _caseListView.itemsSource = GetFilteredCases();
            _caseListView.Rebuild();
            RefreshUi();
        }

        // ── Selection actions ──

        private void SelectAll()
        {
            foreach (var item in _state.Cases) item.IsChecked = true;
            RefreshAllGroupCheckStates();
            _caseListView?.Rebuild();
        }

        private void SelectNone()
        {
            foreach (var item in _state.Cases) item.IsChecked = false;
            RefreshAllGroupCheckStates();
            _caseListView?.Rebuild();
        }

        private void SelectFailed()
        {
            foreach (var item in _state.Cases)
                item.IsChecked = item.Status == TestStatus.Failed || item.Status == TestStatus.Error;
            _caseListView?.Rebuild();
        }

        // ── Execution ──

        private void ClearResults()
        {
            foreach (var item in _state.Cases)
            {
                item.Status = default;
                item.DurationMs = 0;
                item.ErrorCode = null;
                item.ErrorMessage = null;
                item.FailedStepName = null;
                item.FailedStepError = null;
                item.ReportMarkdownPath = null;
                item.ReportJsonPath = null;
                item.IsRunning = false;
                item.StepResults.Clear();
            }

            _state.Passed = 0;
            _state.Failed = 0;
            _state.Errors = 0;
            _state.Skipped = 0;
            _state.OverallTotal = 0;
            _state.OverallPassed = 0;
            _state.OverallFailed = 0;
            _state.OverallErrors = 0;
            _state.OverallSkipped = 0;
            _state.BatchIndex = 0;
            _state.BatchTotal = 0;
            _state.StatusText = "Idle";

            RefreshUi();
            RefreshDetailPanel();
        }

        private async void RunAll()
        {
            await RunFilteredAsync(c => true);
        }

        private async void RunSelected()
        {
            var selectedPaths = _state.Cases
                .Where(c => c.IsChecked)
                .Select(c => c.YamlPath)
                .Distinct()
                .Select(p => Path.GetFullPath(p))
                .ToList();

            if (selectedPaths.Count == 0)
            {
                ShowNotification(new GUIContent("No cases selected."));
                return;
            }

            await ExecuteBatchAsync(selectedPaths, _ => true);
        }

        private async void RunStep()
        {
            if (_state.IsRunning)
            {
                _activeContext?.RuntimeController?.StepOnce();
                return;
            }

            var selected = GetSelectedItem();
            if (selected == null)
            {
                ShowNotification(new GUIContent("No case selected."));
                return;
            }

            string yamlPath = Path.GetFullPath(selected.YamlPath);
            if (!File.Exists(yamlPath))
            {
                ShowNotification(new GUIContent("Selected YAML file does not exist."));
                return;
            }

            await ExecuteBatchAsync(new List<string> { yamlPath }, _ => true, HeadedRunMode.Step);
        }

        private async void RunFailed()
        {
            var failedPaths = _state.Cases
                .Where(c => c.Status == TestStatus.Failed || c.Status == TestStatus.Error)
                .Select(c => c.YamlPath)
                .Distinct()
                .Select(p => Path.GetFullPath(p))
                .ToList();

            if (failedPaths.Count == 0)
            {
                ShowNotification(new GUIContent("No failed cases to rerun."));
                return;
            }

            await ExecuteBatchAsync(failedPaths, _ => true);
        }

        private async System.Threading.Tasks.Task RunFilteredAsync(Func<TestRunnerCaseItem, bool> filter)
        {
            var yamlPaths = _state.Cases
                .Where(filter)
                .Select(c => c.YamlPath)
                .Distinct()
                .Select(p => Path.GetFullPath(p))
                .ToList();

            if (yamlPaths.Count == 0)
            {
                ShowNotification(new GUIContent("No YAML cases found."));
                return;
            }

            await ExecuteBatchAsync(yamlPaths, _ => true);
        }

        private async System.Threading.Tasks.Task ExecuteBatchAsync(List<string> yamlPaths, Func<string, bool> filter, HeadedRunMode? runModeOverride = null)
        {
            if (_state.IsRunning)
            {
                ShowNotification(new GUIContent("A test run is already in progress."));
                return;
            }

            _runCts = new CancellationTokenSource();
            _state.IsRunning = true;
            _state.StatusText = "Running";
            _state.Total = yamlPaths.Count;
            _state.Passed = 0;
            _state.Failed = 0;
            _state.Errors = 0;
            _state.Skipped = 0;
            _state.CurrentYamlPath = null;
            _state.CurrentCaseName = null;

            // Reset states for files in this run
            foreach (var item in _state.Cases)
            {
                if (yamlPaths.Any(p => StringComparer.OrdinalIgnoreCase.Equals(p, Path.GetFullPath(item.YamlPath))))
                {
                    item.Status = TestStatus.None;
                    item.DurationMs = 0;
                    item.ErrorCode = null;
                    item.ErrorMessage = null;
                    item.IsRunning = false;
                    item.StepResults.Clear();
                }
            }

            RefreshUi();

            string reportRoot = string.IsNullOrWhiteSpace(_state.ReportPath) ? "Reports/TestRunner" : _state.ReportPath;
            string screenshotRoot = Path.Combine(reportRoot, "Screenshots");
            var suite = new TestSuiteResult { StartedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            var runner = new TestRunner();
            var reportPaths = new ReportPathBuilder();

            try
            {
                foreach (string yamlPath in yamlPaths)
                {
                    if (_runCts.Token.IsCancellationRequested) break;

                    _state.CurrentYamlPath = TestRunnerPathUtility.MakeProjectRelative(yamlPath);
                    _state.CurrentCaseName = null;

                    var caseItem = _state.Cases.FirstOrDefault(c =>
                        StringComparer.OrdinalIgnoreCase.Equals(c.YamlPath, _state.CurrentYamlPath));
                    if (caseItem != null) caseItem.IsRunning = true;
                    RefreshUi();

                    TestResult result;
                    try
                    {
                        result = await runner.RunFileAsync(
                            yamlPath,
                            new TestOptions
                            {
                                Headed = _state.Headed,
                                DebugOnFailure = _state.FailurePolicy == HeadedFailurePolicy.Pause,
                                ReportOutputPath = reportRoot,
                                ScreenshotPath = screenshotRoot,
                                ScreenshotOnFailure = _state.ScreenshotOnFailure,
                                StopOnFirstFailure = _state.StopOnFirstFailure,
                                ContinueOnStepFailure = _state.ContinueOnStepFailure,
                                DefaultTimeoutMs = _state.DefaultTimeoutMs,
                                RequireOfficialHost = _state.RequireOfficialHost,
                                RequireOfficialPointerDriver = _state.RequireOfficialPointerDriver,
                                RequireInputSystemKeyboardDriver = _state.RequireInputSystemKeyboardDriver,
                                EnableVerboseLog = _state.EnableVerboseLog,
                            },
                            null,
                            context =>
                            {
                                _activeContext = context;
                                if (runModeOverride.HasValue)
                                {
                                    context.RuntimeController.RunMode = runModeOverride.Value;
                                }
                                _state.CurrentCaseName = context.CaseName;
                                if (caseItem != null) caseItem.CaseName = context.CaseName;
                                RefreshUi();
                            });
                    }
                    catch (Exception ex)
                    {
                        string fallbackCaseName = Path.GetFileNameWithoutExtension(yamlPath);
                        result = new TestResult
                        {
                            CaseName = fallbackCaseName,
                            Status = TestStatus.Error,
                            StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                            EndedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                            ErrorCode = ex is UIFlowException flowEx ? flowEx.ErrorCode : ErrorCodes.CliExecutionError,
                            ErrorMessage = ex.Message,
                            ReportMarkdownPath = TestRunnerPathUtility.MakeProjectRelative(reportPaths.BuildCaseMarkdownPath(reportRoot, fallbackCaseName)),
                        };
                    }
                    suite.CaseResults.Add(result);
                    ApplyCounters(result.Status);

                    if (caseItem != null)
                    {
                        caseItem.IsRunning = false;
                        caseItem.CaseName = result.CaseName;
                        caseItem.Status = result.Status;
                        caseItem.DurationMs = result.DurationMs;
                        caseItem.ErrorCode = result.ErrorCode;
                        caseItem.ErrorMessage = result.ErrorMessage;
                        caseItem.StepResults = result.StepResults ?? new List<StepResult>();
                        var failedStep = result.StepResults?.FirstOrDefault(s => s.Status == TestStatus.Failed || s.Status == TestStatus.Error);
                        caseItem.FailedStepName = failedStep?.DisplayName;
                        caseItem.FailedStepError = failedStep?.ErrorMessage;
                        caseItem.ReportMarkdownPath = TestRunnerPathUtility.MakeProjectRelative(reportPaths.BuildCaseMarkdownPath(reportRoot, result.CaseName));
                        caseItem.ReportJsonPath = TestRunnerPathUtility.MakeProjectRelative(reportPaths.BuildCaseJsonPath(reportRoot, result.CaseName));
                        result.ReportMarkdownPath = caseItem.ReportMarkdownPath;
                    }

                    _state.CurrentCaseName = null;
                    _state.StatusText = result.Status == TestStatus.Passed
                        ? $"Passed: {result.CaseName}"
                        : $"Failed: {result.CaseName}";
                    RefreshUi();

                    if (_state.StopOnFirstFailure && (result.Status == TestStatus.Failed || result.Status == TestStatus.Error))
                        break;
                }

                suite.Total = suite.CaseResults.Count;
                suite.Passed = _state.Passed;
                suite.Failed = _state.Failed;
                suite.Errors = _state.Errors;
                suite.Skipped = _state.Skipped;
                suite.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                suite.ExitCode = ExitCodeResolver.Resolve(suite);

                var reporter = new MarkdownReporter(new ReporterOptions
                {
                    ReportRootPath = reportRoot,
                    ScreenshotRootPath = screenshotRoot,
                    SuiteName = "test-runner",
                });
                reporter.WriteSuiteReport(suite);
                new CiArtifactManifestWriter().Write(reportRoot);

                // Overwrite unified suite report with batch results
                try
                {
                    MarkdownReporter.WriteUnifiedSuiteReport(suite, overwrite: true);
                }
                catch (Exception unifiedEx)
                {
                    Codingriver.Logger.LogWarning($"[UIFlow] 统一套件报告写入失败: {unifiedEx.Message}");
                }

                _state.StatusText = _runCts.Token.IsCancellationRequested ? "Aborted"
                    : suite.Failed > 0 || suite.Errors > 0 ? "Failed"
                    : "Completed";
            }
            catch (Exception ex)
            {
                _state.StatusText = "Failed";
                Codingriver.Logger.LogError($"[UIFlow] Test Runner batch error: {ex.Message}");
            }
            finally
            {
                _state.IsRunning = false;
                _state.CurrentYamlPath = null;
                _state.CurrentCaseName = null;
                _runCts?.Dispose();
                _runCts = null;
                _activeContext = null;
                RefreshUi();
                RefreshDetailPanel();
            }
        }

        private void CancelRun()
        {
            if (!_state.IsRunning)
            {
                ShowNotification(new GUIContent("No active test run."));
                return;
            }
            _runCts?.Cancel();
            _activeContext?.RuntimeController?.Stop();
            ShowNotification(new GUIContent("Cancellation requested."));
        }

        private void ApplyCounters(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed: _state.Passed++; break;
                case TestStatus.Failed: _state.Failed++; break;
                case TestStatus.Error: _state.Errors++; break;
                case TestStatus.Skipped: _state.Skipped++; break;
            }
        }

        private static int ComputeTotalSteps(TestCaseDefinition definition)
        {
            if (definition == null) return 0;
            int setupCount = definition.Fixture?.Setup?.Count ?? 0;
            int stepsCount = definition.Steps?.Count ?? 0;
            int teardownCount = definition.Fixture?.Teardown?.Count ?? 0;

            int rowCount = 1;
            try
            {
                rowCount = TestDataResolver.ResolveRows(definition).Count;
            }
            catch
            {
                rowCount = definition.Data?.Rows?.Count ?? 0;
            }
            if (rowCount == 0) rowCount = 1;
            return rowCount * (setupCount + stepsCount + teardownCount);
        }

        private void RefreshUi()
        {
            if (_statusLabel == null) return;

            _statusLabel.text = $"Status: {_state.StatusText}";
            _currentCaseLabel.text = $"Current: {(_state.CurrentCaseName ?? "-")}";
            int totalCases = _state.Cases.Count;

            // Show overall cross-batch stats when a multi-batch run is active or completed
            bool hasOverall = _state.OverallTotal > 0 && _state.BatchTotal > 1;
            if (hasOverall)
            {
                int overallDone = _state.OverallPassed + _state.OverallFailed + _state.OverallErrors + _state.OverallSkipped;
                _statsLabel.text = $"用例: {totalCases}  [总体 {overallDone}/{_state.OverallTotal}] " +
                    $"通过: {_state.OverallPassed}  失败: {_state.OverallFailed}  错误: {_state.OverallErrors}  跳过: {_state.OverallSkipped}";
            }
            else
            {
                _statsLabel.text = $"用例: {totalCases}  通过: {_state.Passed}  失败: {_state.Failed}  错误: {_state.Errors}  跳过: {_state.Skipped}";
            }

            _runAllButton?.SetEnabled(!_state.IsRunning);
            _runSelectedButton?.SetEnabled(!_state.IsRunning && _state.Cases.Any(c => c.IsChecked));
            _rerunFailedButton?.SetEnabled(!_state.IsRunning && _state.Cases.Any(c => c.Status == TestStatus.Failed || c.Status == TestStatus.Error));
            _cancelButton?.SetEnabled(_state.IsRunning);
            _refreshButton?.SetEnabled(!_state.IsRunning);

            var timeoutField = rootVisualElement.Q<IntegerField>("test-runner-timeout");
            timeoutField?.SetEnabled(!_state.IsRunning);

            // Runtime control buttons state
            bool isRunning = _state.IsRunning;
            bool isPaused = _activeContext?.RuntimeController?.IsPaused ?? false;
            _runStepButton?.SetEnabled((!isRunning && GetSelectedItem() != null) || (isRunning && isPaused));
            _pauseButton?.SetEnabled(isRunning && !isPaused);
            _resumeButton?.SetEnabled(isRunning && isPaused);
            _failurePolicyField?.SetEnabled(!isRunning);

            // Update progress bar — use overall totals for multi-batch, per-batch otherwise
            var progressBar = rootVisualElement.Q<ProgressBar>("test-runner-progress");
            if (progressBar != null)
            {
                if (_state.IsRunning)
                {
                    if (hasOverall)
                    {
                        // Multi-batch: progress against overall total
                        int overallDone = _state.OverallPassed + _state.OverallFailed + _state.OverallErrors + _state.OverallSkipped;
                        progressBar.value = (float)overallDone / _state.OverallTotal * 100f;
                        int batchCompleted = _state.Passed + _state.Failed + _state.Errors + _state.Skipped;
                        progressBar.title = $"批次 {_state.BatchIndex}/{_state.BatchTotal}  本批 {batchCompleted}/{_state.Total}  总体 {overallDone}/{_state.OverallTotal}";
                    }
                    else if (_state.Total > 0)
                    {
                        int completed = _state.Passed + _state.Failed + _state.Errors + _state.Skipped;
                        progressBar.value = (float)completed / _state.Total * 100f;
                        if (_state.BatchTotal > 1)
                            progressBar.title = $"Batch {_state.BatchIndex}/{_state.BatchTotal}: {completed}/{_state.Total}";
                        else
                            progressBar.title = $"{completed} / {_state.Total}";
                    }
                    else
                    {
                        progressBar.value = 0;
                        progressBar.title = string.Empty;
                    }
                }
                else if (_state.OverallTotal > 0 && totalCases > 0)
                {
                    // Run finished — show final overall result
                    int overallDone = _state.OverallPassed + _state.OverallFailed + _state.OverallErrors + _state.OverallSkipped;
                    progressBar.value = _state.OverallTotal > 0 ? (float)overallDone / _state.OverallTotal * 100f : 0f;
                    progressBar.title = $"总体 {overallDone}/{_state.OverallTotal}  通过: {_state.OverallPassed}  失败: {_state.OverallFailed}";
                }
                else
                {
                    progressBar.value = 0;
                    progressBar.title = string.Empty;
                }
            }

            _caseListView?.Rebuild();
            Repaint();
        }

        // ── HeadedRunEventBus handlers ──

        private void OnRunAttached(RuntimeController controller, string caseName)
        {
            _state.CurrentCaseName = caseName;
            RefreshUi();
        }

        private void OnStepStarted(ExecutableStep step)
        {
            _state.StatusText = $"Running: {step.DisplayName}";
            _state.CurrentCaseName = step.DisplayName;
            RefreshUi();
        }

        private void OnStepCompleted(ExecutableStep step, StepResult result, VisualElement element)
        {
            if (element != null)
            {
                var targetWindow = Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .FirstOrDefault(w => w != null && w.rootVisualElement?.panel == element.panel);
                if (targetWindow != null)
                    _overlayRenderer.Attach(targetWindow);
                _overlayRenderer.Highlight(element);
            }
            else if (result.Status != TestStatus.Failed)
            {
                _overlayRenderer.Clear();
            }

            // Update current case item with step result for real-time UI refresh
            var caseItem = _state.Cases.FirstOrDefault(c =>
                StringComparer.OrdinalIgnoreCase.Equals(c.YamlPath, _state.CurrentYamlPath));
            if (caseItem != null)
            {
                if (caseItem.StepResults == null)
                    caseItem.StepResults = new List<StepResult>();
                var existing = caseItem.StepResults.FirstOrDefault(s => s.StepId == result.StepId);
                if (existing != null)
                    caseItem.StepResults.Remove(existing);
                caseItem.StepResults.Add(result);
            }

            if (_activeContext?.RuntimeController?.IsPaused == true)
            {
                _state.StatusText = $"Paused at: {step.DisplayName}";
            }
            else
            {
                _state.StatusText = result.Status == TestStatus.Passed
                    ? $"Step passed: {step.DisplayName}"
                    : $"Step {result.Status}: {step.DisplayName}";
            }

            RefreshUi();
            RefreshDetailPanel();
        }

        private void OnHighlightedElementChanged(ExecutableStep step, VisualElement element)
        {
            if (element == null) return;
            var targetWindow = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .FirstOrDefault(w => w != null && w.rootVisualElement?.panel == element.panel);
            if (targetWindow != null && targetWindow != this)
                _overlayRenderer.Attach(targetWindow);
            _overlayRenderer.Highlight(element);
        }

        private void OnFailure(ExecutableStep step, StepResult result)
        {
            _state.StatusText = $"Step Failed: {step.DisplayName}";
            RefreshUi();
            RefreshDetailPanel();
        }

        private void OnRunFinished(TestResult result)
        {
            _overlayRenderer.Clear();
            RefreshUi();
            RefreshDetailPanel();
        }
    }

    public static class TestRunnerPathUtility
    {
        public static string MakeProjectRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string fullPath = Path.GetFullPath(path);
            string projectRoot = UIFlowUtility.AppendDirectorySeparator(Path.GetFullPath(Directory.GetCurrentDirectory()));
            if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(projectRoot.Length).Replace('\\', '/');
            return fullPath;
        }
    }

    public static class TestRunnerPreferences
    {
        private const string Prefix = "UIFlow.TestRunner.";

        public static void Load(TestRunnerViewState state)
        {
            state.TargetDirectory = EditorPrefs.GetString(Prefix + nameof(TestRunnerViewState.TargetDirectory), "Assets/Examples/Yaml");
            state.ReportPath = EditorPrefs.GetString(Prefix + nameof(TestRunnerViewState.ReportPath), "Reports/TestRunner");
            state.SearchFilter = EditorPrefs.GetString(Prefix + nameof(TestRunnerViewState.SearchFilter), string.Empty);
            state.Headed = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.Headed), true);
            state.StopOnFirstFailure = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.StopOnFirstFailure), false);
            state.ContinueOnStepFailure = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.ContinueOnStepFailure), false);
            state.ScreenshotOnFailure = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.ScreenshotOnFailure), true);
            state.EnableVerboseLog = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.EnableVerboseLog), UIFlowMenuItems.IsVerboseLogEnabled);
            state.RequireOfficialHost = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.RequireOfficialHost), UIFlowProjectSettings.instance.RequireOfficialHostByDefault);
            state.RequireOfficialPointerDriver = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.RequireOfficialPointerDriver), UIFlowProjectSettings.instance.RequireOfficialPointerDriverByDefault);
            state.RequireInputSystemKeyboardDriver = EditorPrefs.GetBool(Prefix + nameof(TestRunnerViewState.RequireInputSystemKeyboardDriver), UIFlowProjectSettings.instance.RequireInputSystemKeyboardDriverByDefault);
            state.DefaultTimeoutMs = EditorPrefs.GetInt(Prefix + nameof(TestRunnerViewState.DefaultTimeoutMs), 3000);
            state.FailurePolicy = (HeadedFailurePolicy)EditorPrefs.GetInt(Prefix + nameof(TestRunnerViewState.FailurePolicy), (int)HeadedFailurePolicy.Pause);
        }

        public static void Save(TestRunnerViewState state)
        {
            EditorPrefs.SetString(Prefix + nameof(TestRunnerViewState.TargetDirectory), state.TargetDirectory ?? "Assets/Examples/Yaml");
            EditorPrefs.SetString(Prefix + nameof(TestRunnerViewState.ReportPath), state.ReportPath ?? "Reports/TestRunner");
            EditorPrefs.SetString(Prefix + nameof(TestRunnerViewState.SearchFilter), state.SearchFilter ?? string.Empty);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.Headed), state.Headed);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.StopOnFirstFailure), state.StopOnFirstFailure);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.ContinueOnStepFailure), state.ContinueOnStepFailure);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.ScreenshotOnFailure), state.ScreenshotOnFailure);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.EnableVerboseLog), state.EnableVerboseLog);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.RequireOfficialHost), state.RequireOfficialHost);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.RequireOfficialPointerDriver), state.RequireOfficialPointerDriver);
            EditorPrefs.SetBool(Prefix + nameof(TestRunnerViewState.RequireInputSystemKeyboardDriver), state.RequireInputSystemKeyboardDriver);
            EditorPrefs.SetInt(Prefix + nameof(TestRunnerViewState.DefaultTimeoutMs), state.DefaultTimeoutMs);
            EditorPrefs.SetInt(Prefix + nameof(TestRunnerViewState.FailurePolicy), (int)state.FailurePolicy);
        }
    }
}
