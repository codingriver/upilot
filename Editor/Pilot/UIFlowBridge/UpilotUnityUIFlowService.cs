// -----------------------------------------------------------------------
// Upilot Editor - UnityUIFlow MCP bridge
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityUIFlow;

namespace codingriver.upilot
{
    [Serializable]
    public sealed class UnityUIFlowRunMessage
    {
        public UnityUIFlowRunPayload payload;
    }

    [Serializable]
    public sealed class UnityUIFlowRunPayload
    {
        public string[] yamlPaths;
        public string yamlDirectory = "";
        public bool headed = false;
        public bool stopOnFirstFailure = false;
        public bool continueOnStepFailure = false;
        public bool screenshotOnFailure = true;
        public int defaultTimeoutMs = 3000;
        public bool enableVerboseLog = false;
        public bool debugOnFailure = false;
        public string reportPath = "Reports/upilot/UIFlowMcp";
        public int batchSize = 10;
        public int batchOffset = 0;
        public int totalAll = 0;
    }

    [Serializable]
    public sealed class UnityUIFlowResultsMessage
    {
        public UnityUIFlowResultsPayload payload;
    }

    [Serializable]
    public sealed class UnityUIFlowResultsPayload
    {
        public string executionId = "";
    }

    [Serializable]
    public sealed class UnityUIFlowCancelMessage
    {
        public UnityUIFlowCancelPayload payload;
    }

    [Serializable]
    public sealed class UnityUIFlowCancelPayload
    {
        public string executionId = "";
    }

    [Serializable]
    public sealed class UnityUIFlowStepResultPayload
    {
        public int stepIndex;
        public string stepName;
        public string status;
        public int durationMs;
        public string errorCode;
        public string errorMessage;
        public string screenshotPath;
        public List<string> attachments = new List<string>();
    }

    [Serializable]
    public sealed class UnityUIFlowCaseResultPayload
    {
        public string yamlPath;
        public string caseName;
        public string status;
        public int durationMs;
        public string errorCode;
        public string errorMessage;
        public string reportJsonPath;
        public string reportMarkdownPath;
        public List<string> attachments = new List<string>();
        public List<UnityUIFlowStepResultPayload> stepResults = new List<UnityUIFlowStepResultPayload>();
        public UnityUIFlowStepResultPayload failedStep;
    }

    [Serializable]
    public sealed class UnityUIFlowExecutionResultPayload
    {
        public string executionId;
        public string status;
        public string startedAtUtc;
        public string endedAtUtc;
        public string currentYamlPath;
        public string currentCaseName;
        public string reportPath;
        public string errorCode;
        public string errorMessage;
        public int total;
        public int passed;
        public int failed;
        public int errors;
        public int skipped;
        public bool hasMore;
        public int nextOffset;
        public int totalAll;
        // Full list of all yaml paths across all batches (set at run start, never changes).
        public List<string> allYamlPaths = new List<string>();
        public List<UnityUIFlowCaseResultPayload> cases = new List<UnityUIFlowCaseResultPayload>();
    }

    public sealed class UpilotUnityUIFlowService
    {
        private const int MaxYamlPaths = 1000;

        private readonly UpilotBridge _bridge;
        private readonly ConcurrentDictionary<string, UnityUIFlowExecutionResultPayload> _executions = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _executionCts = new();
        private readonly object _stateLock = new object();

        private bool _isRunning;
        private string _activeExecutionId;
        private UnityUIFlow.ExecutionContext _activeContext;

        public UpilotUnityUIFlowService(UpilotBridge bridge)
        {
            _bridge = bridge;
            Logger.Log("[UnityUIFlow] UnityUIFlowService 初始化");
            // Register the supplier that feeds a freshly-opened TestRunnerWindow
            // with the current execution snapshot.
            UnityUIFlow.TestRunnerWindow.OnWindowOpened = SyncWindowOnOpen;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("unityuiflow.run", HandleRunAsync);
            _bridge.Router.Register("unityuiflow.results", HandleResultsAsync);
            _bridge.Router.Register("unityuiflow.cancel", HandleCancelAsync);
            _bridge.Router.Register("unityuiflow.force_reset", HandleForceResetAsync);
        }

        private async Task HandleRunAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UnityUIFlow", $"开始运行测试 id={id}");
            var payload = JsonUtility.FromJson<UnityUIFlowRunMessage>(json)?.payload ?? new UnityUIFlowRunPayload();
            var opCtx = UpilotOperationTracker.Instance.GetContext(id);

            bool isBusy;
            lock (_stateLock)
            {
                isBusy = _isRunning;
            }

            if (isBusy)
            {
                await _bridge.SendErrorAsync(id, "EDITOR_BUSY", "A UnityUIFlow execution is already running.", token, "unityuiflow.run");
                return;
            }

            List<string> yamlPaths;
            try
            {
                yamlPaths = ResolveYamlPaths(payload);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", ex.Message, token, "unityuiflow.run");
                return;
            }

            // Apply batching: slice the resolved list according to batchOffset/batchSize
            int batchSize = Math.Max(1, payload.batchSize);
            int batchOffset = Math.Max(0, payload.batchOffset);
            int totalAll = payload.totalAll > 0 ? payload.totalAll : yamlPaths.Count;
            var batchedPaths = new List<string>();
            for (int i = batchOffset; i < batchOffset + batchSize && i < yamlPaths.Count; i++)
            {
                batchedPaths.Add(yamlPaths[i]);
            }

            string executionId = Guid.NewGuid().ToString("N");
            string reportPath = BuildExecutionReportPath(payload.reportPath, executionId);
            var execution = new UnityUIFlowExecutionResultPayload
            {
                executionId = executionId,
                status = "queued",
                startedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                reportPath = reportPath,
                total = batchedPaths.Count,
                totalAll = totalAll,
                hasMore = batchOffset + batchSize < totalAll,
                nextOffset = batchOffset + batchSize < totalAll ? batchOffset + batchSize : totalAll,
            };
            // Store the full resolved yaml list so SyncWindowOnOpen can restore all paths
            execution.allYamlPaths.AddRange(yamlPaths);

            _executions[executionId] = execution;
            bool claimedExecutionSlot = false;
            lock (_stateLock)
            {
                if (!_isRunning)
                {
                    _isRunning = true;
                    _activeExecutionId = executionId;
                    claimedExecutionSlot = true;
                }
            }

            if (!claimedExecutionSlot)
            {
                _executions.TryRemove(executionId, out _);
                await _bridge.SendErrorAsync(id, "EDITOR_BUSY", "A UnityUIFlow execution is already running.", token, "unityuiflow.run");
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _executionCts[executionId] = cts;
            opCtx?.Step("Queued UnityUIFlow execution", $"executionId={executionId} files={yamlPaths.Count}");

            _bridge.EnqueueTracked(id, () =>
            {
                _ = ExecuteRunAsync(executionId, batchedPaths, payload, cts.Token);
            });

            await _bridge.SendResultAsync(id, "unityuiflow.run", CloneExecution(execution), token);
        }

        private readonly Dictionary<string, string> _lastResultStatus = new();

        private async Task HandleResultsAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UnityUIFlowResultsMessage>(json)?.payload ?? new UnityUIFlowResultsPayload();
            if (string.IsNullOrWhiteSpace(payload.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "unityuiflow.results");
                return;
            }

            if (!_executions.TryGetValue(payload.executionId, out UnityUIFlowExecutionResultPayload execution))
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found: {payload.executionId}", token, "unityuiflow.results");
                return;
            }

            string prevStatus = _lastResultStatus.TryGetValue(payload.executionId, out string s) ? s : "";
            string currStatus = execution.status ?? "";
            if (prevStatus != currStatus)
            {
                _lastResultStatus[payload.executionId] = currStatus;
                Logger.Log("UnityUIFlow", $"执行状态变化 executionId={payload.executionId[..Math.Min(8, payload.executionId.Length)]} {prevStatus}->{currStatus} 用例={execution.currentCaseName}");
                if (currStatus is "completed" or "failed" or "aborted")
                {
                    _lastResultStatus.Remove(payload.executionId);
                }
            }

            await _bridge.SendResultAsync(id, "unityuiflow.results", CloneExecution(execution), token);
        }

        private async Task HandleForceResetAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UnityUIFlow", $"强制重置执行状态: id={id}");

            UnityUIFlow.ExecutionContext contextToDispose = null;
            string capturedExecutionId;
            lock (_stateLock)
            {
                contextToDispose = _activeContext;
                _activeContext = null;
                capturedExecutionId = _activeExecutionId;
                _activeExecutionId = null;
                _isRunning = false;
            }

            // Dispose must run on the Unity main thread to safely close EditorWindow
            if (contextToDispose != null)
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        contextToDispose.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("UnityUIFlow", $"Dispose during force reset: {ex.Message}");
                    }
                };
            }

            foreach (var kvp in _executionCts)
            {
                try { kvp.Value.Cancel(); } catch { /* ignored */ }
                try { kvp.Value.Dispose(); } catch { /* ignored */ }
            }
            _executionCts.Clear();

            lock (_stateLock)
            {
                foreach (var execution in _executions.Values)
                {
                    if (execution.status == "queued" || execution.status == "running")
                    {
                        execution.status = "aborted";
                        execution.errorCode = ErrorCodes.TestRunAborted;
                        execution.errorMessage = "Execution was force-reset.";
                        execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                        execution.currentCaseName = null;
                        execution.currentYamlPath = null;
                    }
                }
            }

            var payload = new UnityUIFlowExecutionResultPayload
            {
                executionId = capturedExecutionId ?? string.Empty,
                status = "aborted",
                errorCode = ErrorCodes.TestRunAborted,
                errorMessage = "Execution was force-reset.",
                endedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            await _bridge.SendResultAsync(id, "unityuiflow.force_reset", payload, token);
        }

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UnityUIFlow", $"取消测试执行: id={id}");
            var payload = JsonUtility.FromJson<UnityUIFlowCancelMessage>(json)?.payload ?? new UnityUIFlowCancelPayload();
            if (string.IsNullOrWhiteSpace(payload.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "unityuiflow.cancel");
                return;
            }

            if (!_executions.TryGetValue(payload.executionId, out UnityUIFlowExecutionResultPayload execution))
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found: {payload.executionId}", token, "unityuiflow.cancel");
                return;
            }

            bool shouldResetActiveState = false;
            if (_executionCts.TryGetValue(payload.executionId, out CancellationTokenSource cts))
            {
                cts.Cancel();
            }

            lock (_stateLock)
            {
                if (string.Equals(_activeExecutionId, payload.executionId, StringComparison.Ordinal))
                {
                    shouldResetActiveState = true;
                    _activeContext?.RuntimeController?.Stop();
                    _activeContext = null;
                    _activeExecutionId = null;
                    _isRunning = false;
                }

                if (execution.status == "queued" || execution.status == "running")
                {
                    execution.status = "aborted";
                    execution.errorCode = ErrorCodes.TestRunAborted;
                    execution.errorMessage = "Execution was cancelled by user.";
                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                }
            }

            if (shouldResetActiveState)
            {
                Logger.Log("UnityUIFlow", $"已释放活动执行槽: executionId={payload.executionId}");
            }

            await _bridge.SendResultAsync(id, "unityuiflow.cancel", CloneExecution(execution), token);
        }

        private async Task ExecuteRunAsync(string executionId, List<string> yamlPaths, UnityUIFlowRunPayload payload, CancellationToken cancellationToken)
        {
            bool shouldAbortBeforeStart = false;
            UpdateExecution(executionId, execution =>
            {
                shouldAbortBeforeStart = cancellationToken.IsCancellationRequested || execution.status == "aborted";
                if (shouldAbortBeforeStart)
                {
                    execution.status = "aborted";
                    execution.errorCode = ErrorCodes.TestRunAborted;
                    execution.errorMessage = "Execution was cancelled before run start.";
                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                    return;
                }

                execution.status = "running";
                execution.startedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            });

            if (shouldAbortBeforeStart)
            {
                return;
            }

            // Notify TestRunnerWindow (if open) that a new run is starting.
            // Calculate batch info for progress display
            int batchIdx = 1;
            int batchTotal = 1;
            int batchSize = payload.batchSize > 0 ? payload.batchSize : yamlPaths.Count;
            int batchOffset = payload.batchOffset;
            int overallTotal = yamlPaths.Count;
            IEnumerable<string> allPathsForWindow = yamlPaths;
            if (_executions.TryGetValue(executionId, out var execForBatch))
            {
                if (execForBatch.allYamlPaths.Count > 0)
                    allPathsForWindow = execForBatch.allYamlPaths;
                if (batchSize > 0 && execForBatch.totalAll > 0)
                {
                    overallTotal = execForBatch.totalAll;
                    batchIdx = batchOffset / batchSize + 1;
                    batchTotal = (execForBatch.totalAll + batchSize - 1) / batchSize;
                }
            }
            UnityUIFlow.TestRunnerWindow.ExternalRunStarted(executionId, allPathsForWindow, overallTotal, batchIdx, batchTotal, batchSize);

            try
            {
                var runner = new UnityUIFlow.TestRunner();
                var reportPaths = new ReportPathBuilder();

                foreach (string yamlPath in yamlPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    UpdateExecution(executionId, execution =>
                    {
                        execution.currentYamlPath = MakeProjectRelative(yamlPath);
                        execution.currentCaseName = null;
                    });

                    // Notify window that this case is now executing.
                    UnityUIFlow.TestRunnerWindow.ExternalCaseStarted(executionId, yamlPath);

                    var testOptions = BuildTestOptions(payload, executionId);
                    testOptions.GenerateSingleReport = yamlPaths.Count == 1;
                    int caseIndex = 1;
                    if (_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload executionBeforeRun))
                    {
                        caseIndex = executionBeforeRun.cases.Count + 1;
                    }
                    testOptions.CaseIndex = caseIndex;
                    testOptions.TotalCases = yamlPaths.Count;
                    TestResult caseResult;
                    try
                    {
                        caseResult = await runner.RunFileAsync(
                            yamlPath,
                            testOptions,
                            null,
                            context =>
                            {
                                lock (_stateLock)
                                {
                                    _activeContext = context;
                                    if (_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload current))
                                    {
                                        current.currentCaseName = context.CaseName;
                                    }
                                }
                            });
                    }
                    catch (UnityUIFlowException ex)
                    {
                        string caseName = Path.GetFileNameWithoutExtension(yamlPath);
                        caseResult = new TestResult
                        {
                            CaseName = caseName,
                            Status = TestStatus.Error,
                            ErrorCode = ex.ErrorCode,
                            ErrorMessage = ex.Message,
                            StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                            EndedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        };
                    }
                    catch (Exception ex)
                    {
                        string caseName = Path.GetFileNameWithoutExtension(yamlPath);
                        caseResult = new TestResult
                        {
                            CaseName = caseName,
                            Status = TestStatus.Error,
                            ErrorCode = ErrorCodes.CliExecutionError,
                            ErrorMessage = ex.Message,
                            StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                            EndedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        };
                    }

                    lock (_stateLock)
                    {
                        _activeContext = null;
                    }

                    UnityUIFlowCaseResultPayload casePayload = null;
                    if (_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload executionForCase))
                    {
                        casePayload = ToCasePayload(caseResult, yamlPath, executionForCase.reportPath, reportPaths);
                    }
                    else
                    {
                        casePayload = ToCasePayload(caseResult, yamlPath, payload.reportPath, reportPaths);
                    }
                    UpdateExecution(executionId, execution =>
                    {
                        execution.cases.Add(casePayload);
                        execution.currentCaseName = null;
                        execution.currentYamlPath = null;
                        ApplyCaseCounters(execution, caseResult.Status);
                    });

                    // Notify TestRunnerWindow that this case finished.
                    UnityUIFlow.TestRunnerWindow.ExternalCaseFinished(
                        executionId,
                        yamlPath,
                        caseResult.CaseName,
                        caseResult.Status,
                        caseResult.DurationMs,
                        caseResult.ErrorCode,
                        caseResult.ErrorMessage,
                        caseResult.StepResults,
                        casePayload.reportMarkdownPath,
                        casePayload.reportJsonPath);

                    // Push per-case completion event to Bridge (forwarded to MCP clients if supported)
                    int caseCountAfter = 0;
                    if (_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload executionAfterCase))
                    {
                        caseCountAfter = executionAfterCase.cases.Count;
                    }
                    try
                    {
                        await _bridge.SendEventAsync(
                            $"evt-uiflow-case-{executionId}-{caseCountAfter}",
                            "unityuiflow.case_finished",
                            casePayload,
                            cancellationToken);
                    }
                    catch (Exception evtEx)
                    {
                        Logger.LogWarning("UnityUIFlow", $"发送 case_finished 事件失败: {evtEx.Message}");
                    }

                    if (payload.stopOnFirstFailure
                        && (caseResult.Status == TestStatus.Failed || caseResult.Status == TestStatus.Error))
                    {
                        break;
                    }
                }

                UpdateExecution(executionId, execution =>
                {
                    if (execution.status != "aborted")
                    {
                        execution.status = cancellationToken.IsCancellationRequested ? "aborted" : "completed";
                    }

                    if (execution.status == "aborted" && string.IsNullOrWhiteSpace(execution.errorCode))
                    {
                        execution.errorCode = ErrorCodes.TestRunAborted;
                        execution.errorMessage = "Execution was cancelled.";
                    }

                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                });

                // Notify TestRunnerWindow that the entire run finished.
                if (_executions.TryGetValue(executionId, out var execForWindow))
                {
                    UnityUIFlow.TestRunnerWindow.ExternalRunFinished(
                        executionId, execForWindow.status,
                        execForWindow.passed, execForWindow.failed,
                        execForWindow.errors, execForWindow.skipped);
                }

                try
                {
                    if (_executions.TryGetValue(executionId, out var finalExecution))
                    {
                        await _bridge.SendEventAsync(
                            $"evt-uiflow-completed-{executionId}",
                            "unityuiflow.run_completed",
                            CloneExecution(finalExecution),
                            cancellationToken);
                    }
                }
                catch (Exception evtEx)
                {
                    Logger.LogWarning("UnityUIFlow", $"发送 run_completed 事件失败: {evtEx.Message}");
                }

                if (yamlPaths.Count > 1)
                {
                    GenerateSuiteReport(executionId, reportPaths);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateExecution(executionId, execution =>
                {
                    execution.status = "aborted";
                    execution.errorCode = ErrorCodes.TestRunAborted;
                    execution.errorMessage = "Execution was cancelled.";
                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                });
                if (_executions.TryGetValue(executionId, out var abortedExec))
                    UnityUIFlow.TestRunnerWindow.ExternalRunFinished(executionId, "aborted",
                        abortedExec.passed, abortedExec.failed, abortedExec.errors, abortedExec.skipped);
            }
            catch (Exception ex)
            {
                UpdateExecution(executionId, execution =>
                {
                    execution.status = "failed";
                    execution.errorCode = ErrorCodes.CliExecutionError;
                    execution.errorMessage = ex.Message;
                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                });
                if (_executions.TryGetValue(executionId, out var failedExec))
                    UnityUIFlow.TestRunnerWindow.ExternalRunFinished(executionId, "failed",
                        failedExec.passed, failedExec.failed, failedExec.errors, failedExec.skipped);
            }
            finally
            {
                lock (_stateLock)
                {
                    _activeContext = null;
                    _activeExecutionId = null;
                    _isRunning = false;
                }

                if (_executionCts.TryRemove(executionId, out CancellationTokenSource cts))
                {
                    cts.Dispose();
                }
            }
        }

        /// <summary>
        /// Called when TestRunnerWindow opens while an MCP run may be in progress.
        /// Reconstructs a snapshot from _executions so the window can show partial results.
        /// </summary>
        private void SyncWindowOnOpen(UnityUIFlow.TestRunnerWindow _)
        {
            string activeId;
            UnityUIFlowExecutionResultPayload snap;
            lock (_stateLock)
            {
                if (!_isRunning || string.IsNullOrEmpty(_activeExecutionId)) return;
                activeId = _activeExecutionId;
                if (!_executions.TryGetValue(activeId, out snap)) return;
                // shallow clone under lock to avoid mutation during iteration
                snap = CloneExecution(snap);
            }

            // Prefer the stored full list; fall back to completed+current paths
            List<string> allYamlPaths;
            if (snap.allYamlPaths.Count > 0)
            {
                allYamlPaths = snap.allYamlPaths;
            }
            else
            {
                allYamlPaths = snap.cases
                    .Select(c => c.yamlPath)
                    .Concat(string.IsNullOrEmpty(snap.currentYamlPath)
                        ? Enumerable.Empty<string>()
                        : new[] { snap.currentYamlPath })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Compute batch info from the snapshot
            int batchSizeForSync = snap.total > 0 ? snap.total : 1;
            int batchIdxForSync = 1;
            int batchTotalForSync = 1;
            int totalAll = snap.totalAll > 0 ? snap.totalAll : allYamlPaths.Count;
            if (batchSizeForSync > 0 && totalAll > 0)
            {
                // batchIdx is derived from how many cases are already done vs batch size
                int completedCaseCount = snap.cases.Count;
                batchIdxForSync = completedCaseCount / batchSizeForSync + 1;
                batchTotalForSync = (totalAll + batchSizeForSync - 1) / batchSizeForSync;
            }

            var completedCases = snap.cases.Select(c =>
            {
                Enum.TryParse<UnityUIFlow.TestStatus>(c.status, out var st);
                return (
                    yamlPath: c.yamlPath,
                    caseName: c.caseName,
                    status: st,
                    durationMs: c.durationMs,
                    errorCode: c.errorCode,
                    errorMessage: c.errorMessage,
                    stepResults: (List<UnityUIFlow.StepResult>)null,  // not available in snapshot
                    reportMarkdownPath: c.reportMarkdownPath,
                    reportJsonPath: c.reportJsonPath
                );
            });

            UnityUIFlow.TestRunnerWindow.TrySyncActiveRun(
                activeId,
                allYamlPaths,
                totalAll,
                snap.currentYamlPath,
                snap.passed, snap.failed, snap.errors, snap.skipped,
                completedCases,
                batchIdxForSync, batchTotalForSync, batchSizeForSync);
        }

        private void GenerateSuiteReport(string executionId, ReportPathBuilder reportPaths)        {
            if (!_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload execution))
            {
                return;
            }

            var suiteResult = new UnityUIFlow.TestSuiteResult
            {
                StartedAtUtc = execution.startedAtUtc,
                EndedAtUtc = execution.endedAtUtc,
                Total = execution.total,
                Passed = execution.passed,
                Failed = execution.failed,
                Errors = execution.errors,
                Skipped = execution.skipped,
            };

            foreach (UnityUIFlowCaseResultPayload casePayload in execution.cases)
            {
                if (Enum.TryParse<UnityUIFlow.TestStatus>(casePayload.status, out UnityUIFlow.TestStatus status))
                {
                    suiteResult.CaseResults.Add(new UnityUIFlow.TestResult
                    {
                        CaseName = casePayload.caseName,
                        Status = status,
                        DurationMs = casePayload.durationMs,
                        ErrorCode = casePayload.errorCode,
                        ErrorMessage = casePayload.errorMessage,
                        ReportMarkdownPath = casePayload.reportMarkdownPath,
                    });
                }
            }

            var reporter = new UnityUIFlow.MarkdownReporter(new UnityUIFlow.ReporterOptions
            {
                ReportRootPath = execution.reportPath,
                ScreenshotRootPath = Path.Combine(execution.reportPath, "Screenshots"),
            });

            try
            {
                reporter.WriteSuiteReport(suiteResult);
                Logger.Log("UnityUIFlow", $"生成套件报告 full_reports.md 路径={execution.reportPath}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("UnityUIFlow", $"生成套件报告失败: {ex.Message}");
            }

            // Overwrite unified suite report with batch results
            try
            {
                UnityUIFlow.MarkdownReporter.WriteUnifiedSuiteReport(suiteResult, overwrite: true);
                Logger.Log("UnityUIFlow", "生成统一套件报告 Reports/full_reports.md");
            }
            catch (Exception unifiedEx)
            {
                Logger.LogWarning("UnityUIFlow", $"生成统一套件报告失败: {unifiedEx.Message}");
            }
        }

        private static TestOptions BuildTestOptions(UnityUIFlowRunPayload payload, string executionId)
        {
            string reportPath = BuildExecutionReportPath(payload.reportPath, executionId);
            return new TestOptions
            {
                Headed = payload.headed,
                StopOnFirstFailure = payload.stopOnFirstFailure,
                ContinueOnStepFailure = payload.continueOnStepFailure,
                ScreenshotOnFailure = payload.screenshotOnFailure,
                DefaultTimeoutMs = payload.defaultTimeoutMs,
                EnableVerboseLog = payload.enableVerboseLog,
                DebugOnFailure = payload.debugOnFailure,
                ReportOutputPath = reportPath,
                ScreenshotPath = Path.Combine(reportPath, "Screenshots"),
            };
        }

        public static List<string> ResolveYamlPaths(UnityUIFlowRunPayload payload)
        {
            bool hasPaths = payload.yamlPaths != null && payload.yamlPaths.Length > 0;
            bool hasDirectory = !string.IsNullOrWhiteSpace(payload.yamlDirectory);
            if (hasPaths == hasDirectory)
            {
                throw new InvalidOperationException("Specify either yamlPaths or yamlDirectory.");
            }

            var resolved = new List<string>();
            if (hasPaths)
            {
                foreach (string rawPath in payload.yamlPaths)
                {
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(rawPath);
                    if (!File.Exists(fullPath))
                    {
                        throw new FileNotFoundException($"YAML file not found: {rawPath}");
                    }

                    if (!fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"YAML file path is invalid: {rawPath}");
                    }

                    resolved.Add(fullPath);
                }
            }
            else
            {
                string directory = Path.GetFullPath(payload.yamlDirectory);
                if (!Directory.Exists(directory))
                {
                    throw new DirectoryNotFoundException($"YAML directory not found: {payload.yamlDirectory}");
                }

                resolved.AddRange(Directory.GetFiles(directory, "*.yaml", SearchOption.AllDirectories));
                resolved.Sort(StringComparer.OrdinalIgnoreCase);
            }

            if (resolved.Count == 0)
            {
                throw new InvalidOperationException("No YAML files were resolved.");
            }

            if (resolved.Count > MaxYamlPaths)
            {
                throw new InvalidOperationException($"Too many YAML files. Maximum supported count is {MaxYamlPaths}.");
            }

            return resolved;
        }

        public static UnityUIFlowCaseResultPayload ToCasePayload(TestResult result, string yamlPath, string reportPath, ReportPathBuilder reportPaths)
        {
            var payload = new UnityUIFlowCaseResultPayload
            {
                yamlPath = MakeProjectRelative(yamlPath),
                caseName = result.CaseName,
                status = result.Status.ToString(),
                durationMs = result.DurationMs,
                errorCode = result.ErrorCode,
                errorMessage = result.ErrorMessage,
                reportJsonPath = MakeProjectRelative(reportPaths.BuildCaseJsonPath(reportPath, result.CaseName)),
                reportMarkdownPath = MakeProjectRelative(reportPaths.BuildCaseMarkdownPath(reportPath, result.CaseName)),
            };

            if (result.Attachments != null)
            {
                payload.attachments.AddRange(NormalizeArtifactPaths(reportPath, result.Attachments));
            }

            if (result.StepResults != null)
            {
                for (int index = 0; index < result.StepResults.Count; index++)
                {
                    StepResult stepResult = result.StepResults[index];
                    var stepPayload = new UnityUIFlowStepResultPayload
                    {
                        stepIndex = index + 1,
                        stepName = stepResult.DisplayName,
                        status = stepResult.Status.ToString(),
                        durationMs = stepResult.DurationMs,
                        errorCode = stepResult.ErrorCode,
                        errorMessage = stepResult.ErrorMessage,
                        screenshotPath = NormalizeArtifactPath(reportPath, stepResult.ScreenshotPath),
                    };

                    if (stepResult.Attachments != null)
                    {
                        stepPayload.attachments.AddRange(NormalizeArtifactPaths(reportPath, stepResult.Attachments));
                    }

                    payload.stepResults.Add(stepPayload);
                    if (payload.failedStep == null
                        && (stepResult.Status == TestStatus.Failed || stepResult.Status == TestStatus.Error))
                    {
                        payload.failedStep = stepPayload;
                    }
                }
            }

            return payload;
        }

        public static void ApplyCaseCounters(UnityUIFlowExecutionResultPayload execution, TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:
                    execution.passed++;
                    break;
                case TestStatus.Failed:
                    execution.failed++;
                    break;
                case TestStatus.Error:
                    execution.errors++;
                    break;
                case TestStatus.Skipped:
                    execution.skipped++;
                    break;
            }
        }

        private void UpdateExecution(string executionId, Action<UnityUIFlowExecutionResultPayload> updater)
        {
            if (!_executions.TryGetValue(executionId, out UnityUIFlowExecutionResultPayload execution))
            {
                return;
            }

            lock (_stateLock)
            {
                updater(execution);
            }
        }

        private UnityUIFlowExecutionResultPayload CloneExecution(UnityUIFlowExecutionResultPayload source)
        {
            lock (_stateLock)
            {
                var clone = new UnityUIFlowExecutionResultPayload
                {
                    executionId = source.executionId,
                    status = source.status,
                    startedAtUtc = source.startedAtUtc,
                    endedAtUtc = source.endedAtUtc,
                    currentYamlPath = source.currentYamlPath,
                    currentCaseName = source.currentCaseName,
                    reportPath = source.reportPath,
                    errorCode = source.errorCode,
                    errorMessage = source.errorMessage,
                    total = source.total,
                    passed = source.passed,
                    failed = source.failed,
                    errors = source.errors,
                    skipped = source.skipped,
                    hasMore = source.hasMore,
                    nextOffset = source.nextOffset,
                    totalAll = source.totalAll,
                };

                clone.allYamlPaths.AddRange(source.allYamlPaths);

                foreach (UnityUIFlowCaseResultPayload caseResult in source.cases)
                {
                    clone.cases.Add(CloneCase(caseResult));
                }

                return clone;
            }
        }

        private static UnityUIFlowCaseResultPayload CloneCase(UnityUIFlowCaseResultPayload source)
        {
            var clone = new UnityUIFlowCaseResultPayload
            {
                yamlPath = source.yamlPath,
                caseName = source.caseName,
                status = source.status,
                durationMs = source.durationMs,
                errorCode = source.errorCode,
                errorMessage = source.errorMessage,
                reportJsonPath = source.reportJsonPath,
                reportMarkdownPath = source.reportMarkdownPath,
                failedStep = source.failedStep == null ? null : CloneStep(source.failedStep),
            };
            clone.attachments.AddRange(source.attachments);
            foreach (UnityUIFlowStepResultPayload step in source.stepResults)
            {
                clone.stepResults.Add(CloneStep(step));
            }

            return clone;
        }

        private static UnityUIFlowStepResultPayload CloneStep(UnityUIFlowStepResultPayload source)
        {
            var clone = new UnityUIFlowStepResultPayload
            {
                stepIndex = source.stepIndex,
                stepName = source.stepName,
                status = source.status,
                durationMs = source.durationMs,
                errorCode = source.errorCode,
                errorMessage = source.errorMessage,
                screenshotPath = source.screenshotPath,
            };
            clone.attachments.AddRange(source.attachments);
            return clone;
        }

        public static string BuildExecutionReportPath(string baseReportPath, string executionId)
        {
            string root = string.IsNullOrWhiteSpace(baseReportPath) ? "Reports/upilot/UIFlowMcp" : baseReportPath;
            return Path.Combine(root, executionId);
        }

        public static string NormalizeArtifactPath(string reportPath, string artifactPath)
        {
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                return artifactPath;
            }

            string combinedPath = Path.IsPathRooted(artifactPath)
                ? artifactPath
                : Path.Combine(reportPath, artifactPath);
            return MakeProjectRelative(combinedPath);
        }

        public static List<string> NormalizeArtifactPaths(string reportPath, IEnumerable<string> artifactPaths)
        {
            var normalized = new List<string>();
            if (artifactPaths == null)
            {
                return normalized;
            }

            foreach (string artifactPath in artifactPaths)
            {
                if (string.IsNullOrWhiteSpace(artifactPath))
                {
                    continue;
                }

                normalized.Add(NormalizeArtifactPath(reportPath, artifactPath));
            }

            return normalized;
        }

        public static string MakeProjectRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            string fullPath = Path.GetFullPath(path);
            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            string rootWithSeparator = projectRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? projectRoot
                : projectRoot + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(rootWithSeparator.Length);
            }

            return fullPath;
        }
    }
}
