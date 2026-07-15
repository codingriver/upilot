// -----------------------------------------------------------------------
// UPilot Editor - UPilot Flow MCP bridge
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
using CodingRiver.UPilot.Flow;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class UPilotFlowRunMessage
    {
        public UPilotFlowRunPayload payload;
    }

    [Serializable]
    public sealed class UPilotFlowRunPayload
    {
        public string[] yamlPaths;
        public string yamlDirectory = "";
        public bool headed = false;
        public bool stopOnFirstFailure = false;
        public bool continueOnStepFailure = false;
        public bool screenshotOnFailure = true;
        public int defaultTimeoutMs = UPilotFlowSchema.DefaultStepTimeoutMs;
        public bool enableVerboseLog = false;
        public bool debugOnFailure = false;
        public string reportPath = "Reports/UPilot/Flow";
        public int batchSize = 10;
        public int batchOffset = 0;
        public int totalAll = 0;
    }

    [Serializable]
    public sealed class UPilotFlowResultsMessage
    {
        public UPilotFlowResultsPayload payload;
    }

    [Serializable]
    public sealed class UPilotFlowResultsPayload
    {
        public string executionId = "";
    }

    [Serializable]
    public sealed class UPilotFlowCancelMessage
    {
        public UPilotFlowCancelPayload payload;
    }

    [Serializable]
    public sealed class UPilotFlowCancelPayload
    {
        public string executionId = "";
    }

    [Serializable]
    public sealed class UPilotFlowValidateMessage
    {
        public UPilotFlowValidatePayload payload;
    }

    [Serializable]
    public sealed class UPilotFlowValidatePayload
    {
        public string yamlPath = "";
    }

    [Serializable]
    public sealed class UPilotFlowMigrateMessage
    {
        public UPilotFlowMigratePayload payload;
    }

    [Serializable]
    public sealed class UPilotFlowMigratePayload
    {
        public string[] yamlPaths;
        public string yamlDirectory = "";
        public string targetDirectory = "";
        public bool dryRun = true;
    }

    [Serializable]
    public sealed class UPilotFlowMigrationBatchResult
    {
        public bool dryRun;
        public int total;
        public int changed;
        public int failed;
        public List<UPilotFlowMigrationResult> results = new List<UPilotFlowMigrationResult>();
    }

    [Serializable]
    public sealed class UPilotFlowStepResultPayload
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
    public sealed class UPilotFlowCaseResultPayload
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
        public List<UPilotFlowStepResultPayload> stepResults = new List<UPilotFlowStepResultPayload>();
        public UPilotFlowStepResultPayload failedStep;
    }

    [Serializable]
    public sealed class UPilotFlowExecutionResultPayload
    {
        public string executionId;
        public string status;
        public string startedAtUtc;
        public string endedAtUtc;
        public string currentYamlPath;
        public string currentCaseName;
        public string currentStepName;
        public int currentStepIndex = -1;
        public string phase;
        public long phaseStartedAt;
        public long lastProgressAt;
        public int phaseElapsedMs;
        public bool suspectedStuck;
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
        public List<UPilotFlowCaseResultPayload> cases = new List<UPilotFlowCaseResultPayload>();
    }

    public sealed class UPilotFlowService
    {
        private const int MaxYamlPaths = 1000;

        private readonly UPilotBridge _bridge;
        private readonly ConcurrentDictionary<string, UPilotFlowExecutionResultPayload> _executions = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _executionCts = new();
        private readonly object _stateLock = new object();

        private bool _isRunning;
        private string _activeExecutionId;
        private CodingRiver.UPilot.Flow.ExecutionContext _activeContext;

        public UPilotFlowService(UPilotBridge bridge)
        {
            _bridge = bridge;
            Logger.Log("[UPilot Flow] UPilot Flow service 初始化");
            // Register the supplier that feeds a freshly-opened TestRunnerWindow
            // with the current execution snapshot.
            CodingRiver.UPilot.Flow.TestRunnerWindow.OnWindowOpened = SyncWindowOnOpen;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("upilot_flow.run", HandleRunAsync);
            _bridge.Router.Register("upilot_flow.validate", HandleValidateAsync);
            _bridge.Router.Register("upilot_flow.migrate", HandleMigrateAsync);
            _bridge.Router.Register("upilot_flow.results", HandleResultsAsync);
            _bridge.Router.Register("upilot_flow.cancel", HandleCancelAsync);
            _bridge.Router.Register("upilot_flow.force_reset", HandleForceResetAsync);
        }

        private async Task HandleValidateAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UPilotFlowValidateMessage>(json)?.payload ?? new UPilotFlowValidatePayload();
            if (string.IsNullOrWhiteSpace(payload.yamlPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "yamlPath is required.", token, "upilot_flow.validate");
                return;
            }

            var result = new UPilotFlowMigrationService().Validate(payload.yamlPath);
            if (!result.ok)
            {
                await _bridge.SendErrorAsync(id, "FLOW_VALIDATION_FAILED", string.Join("; ", result.errors), token, "upilot_flow.validate");
                return;
            }
            await _bridge.SendResultAsync(id, "upilot_flow.validate", result, token);
        }

        private async Task HandleMigrateAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UPilotFlowMigrateMessage>(json)?.payload ?? new UPilotFlowMigratePayload();
            List<string> yamlPaths;
            try
            {
                yamlPaths = ResolveYamlPaths(new UPilotFlowRunPayload
                {
                    yamlPaths = payload.yamlPaths,
                    yamlDirectory = payload.yamlDirectory,
                });
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", ex.Message, token, "upilot_flow.migrate");
                return;
            }

            var service = new UPilotFlowMigrationService();
            var batch = new UPilotFlowMigrationBatchResult { dryRun = payload.dryRun, total = yamlPaths.Count };
            foreach (var sourcePath in yamlPaths)
            {
                token.ThrowIfCancellationRequested();
                var targetPath = string.IsNullOrWhiteSpace(payload.targetDirectory)
                    ? sourcePath
                    : Path.Combine(payload.targetDirectory, Path.GetFileName(sourcePath));
                var result = service.Migrate(sourcePath, targetPath, payload.dryRun);
                batch.results.Add(result);
                if (!string.IsNullOrEmpty(result.error)) batch.failed++;
                else if (result.changed) batch.changed++;
            }

            if (!payload.dryRun && batch.changed > 0)
                AssetDatabase.Refresh();
            await _bridge.SendResultAsync(id, "upilot_flow.migrate", batch, token);
        }

        private async Task HandleRunAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UPilot.Flow", $"开始运行测试 id={id}");
            var payload = JsonUtility.FromJson<UPilotFlowRunMessage>(json)?.payload ?? new UPilotFlowRunPayload();
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);

            bool isBusy;
            lock (_stateLock)
            {
                isBusy = _isRunning;
            }

            if (isBusy)
            {
                await _bridge.SendErrorAsync(id, "EDITOR_BUSY", "A UPilot Flow execution is already running.", token, "upilot_flow.run");
                return;
            }

            List<string> yamlPaths;
            try
            {
                yamlPaths = ResolveYamlPaths(payload);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", ex.Message, token, "upilot_flow.run");
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
            var execution = new UPilotFlowExecutionResultPayload
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
                await _bridge.SendErrorAsync(id, "EDITOR_BUSY", "A UPilot Flow execution is already running.", token, "upilot_flow.run");
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _executionCts[executionId] = cts;
            opCtx?.Step("Queued UPilot Flow execution", $"executionId={executionId} files={yamlPaths.Count}");

            _bridge.EnqueueTracked(id, () =>
            {
                _ = ExecuteRunAsync(executionId, batchedPaths, payload, cts.Token);
            });

            await _bridge.SendResultAsync(id, "upilot_flow.run", CloneExecution(execution), token);
        }

        private readonly Dictionary<string, string> _lastResultStatus = new();

        private async Task HandleResultsAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<UPilotFlowResultsMessage>(json)?.payload ?? new UPilotFlowResultsPayload();
            if (string.IsNullOrWhiteSpace(payload.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "upilot_flow.results");
                return;
            }

            if (!_executions.TryGetValue(payload.executionId, out UPilotFlowExecutionResultPayload execution))
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found: {payload.executionId}", token, "upilot_flow.results");
                return;
            }

            string prevStatus = _lastResultStatus.TryGetValue(payload.executionId, out string s) ? s : "";
            string currStatus = execution.status ?? "";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            execution.phaseElapsedMs = execution.phaseStartedAt > 0 ? (int)Math.Min(int.MaxValue, Math.Max(0, now - execution.phaseStartedAt)) : 0;
            execution.suspectedStuck = execution.status == "running" && execution.lastProgressAt > 0 && now - execution.lastProgressAt > 30000;
            if (prevStatus != currStatus)
            {
                _lastResultStatus[payload.executionId] = currStatus;
                Logger.Log("UPilot.Flow", $"执行状态变化 executionId={payload.executionId[..Math.Min(8, payload.executionId.Length)]} {prevStatus}->{currStatus} 用例={execution.currentCaseName}");
                if (currStatus is "completed" or "failed" or "aborted")
                {
                    _lastResultStatus.Remove(payload.executionId);
                }
            }

            await _bridge.SendResultAsync(id, "upilot_flow.results", CloneExecution(execution), token);
        }

        private async Task HandleForceResetAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UPilot.Flow", $"强制重置执行状态: id={id}");

            CodingRiver.UPilot.Flow.ExecutionContext contextToDispose = null;
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
                        Logger.LogWarning("UPilot.Flow", $"Dispose during force reset: {ex.Message}");
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

            var payload = new UPilotFlowExecutionResultPayload
            {
                executionId = capturedExecutionId ?? string.Empty,
                status = "aborted",
                errorCode = ErrorCodes.TestRunAborted,
                errorMessage = "Execution was force-reset.",
                endedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            await _bridge.SendResultAsync(id, "upilot_flow.force_reset", payload, token);
        }

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            Logger.Log("UPilot.Flow", $"取消测试执行: id={id}");
            var payload = JsonUtility.FromJson<UPilotFlowCancelMessage>(json)?.payload ?? new UPilotFlowCancelPayload();
            if (string.IsNullOrWhiteSpace(payload.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "upilot_flow.cancel");
                return;
            }

            if (!_executions.TryGetValue(payload.executionId, out UPilotFlowExecutionResultPayload execution))
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found: {payload.executionId}", token, "upilot_flow.cancel");
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
                    execution.currentStepName = null;
                    execution.currentStepIndex = -1;
                    execution.phase = execution.status;
                    execution.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    execution.suspectedStuck = false;
                }
            }

            if (shouldResetActiveState)
            {
                Logger.Log("UPilot.Flow", $"已释放活动执行槽: executionId={payload.executionId}");
            }

            await _bridge.SendResultAsync(id, "upilot_flow.cancel", CloneExecution(execution), token);
        }

        private async Task ExecuteRunAsync(string executionId, List<string> yamlPaths, UPilotFlowRunPayload payload, CancellationToken cancellationToken)
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
                    execution.currentStepName = null;
                    execution.currentStepIndex = -1;
                    execution.phase = "aborted";
                    execution.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    execution.suspectedStuck = false;
                    return;
                }

                execution.status = "running";
                execution.startedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                execution.phase = "run";
                execution.phaseStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                execution.lastProgressAt = execution.phaseStartedAt;
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
            CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalRunStarted(executionId, allPathsForWindow, overallTotal, batchIdx, batchTotal, batchSize);

            try
            {
                var reportPaths = new ReportPathBuilder();
                TestOptions testOptions = BuildTestOptions(payload, executionId);
                UPilotFlowExecutionBatchResult batchResult = await new UPilotFlowExecutionService().RunAsync(
                    new UPilotFlowExecutionRequest
                    {
                        YamlPaths = yamlPaths,
                        Options = testOptions,
                        CancellationToken = cancellationToken,
                        SuiteName = "mcp",
                        CaseIndexOffset = payload.batchOffset,
                        TotalCases = yamlPaths.Count,
                        CaseStarted = (caseIndex, totalCases, yamlPath) =>
                        {
                            UpdateExecution(executionId, execution =>
                            {
                                execution.currentYamlPath = MakeProjectRelative(yamlPath);
                                execution.currentCaseName = null;
                                execution.currentStepName = null;
                                execution.currentStepIndex = -1;
                                execution.phase = "case";
                                execution.phaseStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                execution.lastProgressAt = execution.phaseStartedAt;
                                execution.suspectedStuck = false;
                            });
                            CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalCaseStarted(executionId, yamlPath);
                        },
                        ContextReady = (_, context) =>
                        {
                            lock (_stateLock)
                            {
                                _activeContext = context;
                                if (_executions.TryGetValue(executionId, out UPilotFlowExecutionResultPayload current))
                                {
                                    current.currentCaseName = context.CaseName;
                                    context.StepStarted = (stepIndex, step) => UpdateExecution(executionId, state =>
                                    {
                                        state.currentStepIndex = stepIndex;
                                        state.currentStepName = step.DisplayName;
                                        state.phase = step.Phase.ToString().ToLowerInvariant();
                                        state.phaseStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        state.lastProgressAt = state.phaseStartedAt;
                                        state.suspectedStuck = false;
                                    });
                                    context.StepCompleted = (stepIndex, step, result) => UpdateExecution(executionId, state =>
                                    {
                                        state.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        state.suspectedStuck = false;
                                    });
                                }
                            }
                        },
                        CaseCompleted = (caseIndex, totalCases, yamlPath, caseResult) =>
                        {
                            lock (_stateLock)
                            {
                                _activeContext = null;
                            }

                            string reportPath = _executions.TryGetValue(executionId, out UPilotFlowExecutionResultPayload executionForCase)
                                ? executionForCase.reportPath
                                : payload.reportPath;
                            UPilotFlowCaseResultPayload casePayload = ToCasePayload(
                                caseResult,
                                yamlPath,
                                reportPath,
                                reportPaths);
                            UpdateExecution(executionId, execution =>
                            {
                                execution.cases.Add(casePayload);
                                execution.currentCaseName = null;
                                execution.currentYamlPath = null;
                                execution.currentStepName = null;
                                execution.currentStepIndex = -1;
                                execution.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                ApplyCaseCounters(execution, caseResult.Status);
                            });

                            CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalCaseFinished(
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
                            _ = SendCaseFinishedEventAsync(executionId, casePayload, cancellationToken);
                        },
                    });

                UpdateExecution(executionId, execution =>
                {
                    if (execution.status != "aborted")
                    {
                        execution.status = batchResult.Cancelled || cancellationToken.IsCancellationRequested
                            ? "aborted"
                            : "completed";
                    }

                    if (execution.status == "aborted" && string.IsNullOrWhiteSpace(execution.errorCode))
                    {
                        execution.errorCode = ErrorCodes.TestRunAborted;
                        execution.errorMessage = "Execution was cancelled.";
                    }

                    execution.endedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    execution.currentCaseName = null;
                    execution.currentYamlPath = null;
                    execution.currentStepName = null;
                    execution.currentStepIndex = -1;
                    execution.phase = execution.status;
                    execution.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    execution.suspectedStuck = false;
                });

                if (_executions.TryGetValue(executionId, out UPilotFlowExecutionResultPayload executionForWindow))
                {
                    CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalRunFinished(
                        executionId,
                        executionForWindow.status,
                        executionForWindow.passed,
                        executionForWindow.failed,
                        executionForWindow.errors,
                        executionForWindow.skipped);
                    await _bridge.SendEventAsync(
                        $"evt-upilot_flow-completed-{executionId}",
                        "upilot_flow.run_completed",
                        CloneExecution(executionForWindow),
                        cancellationToken);
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
                    CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalRunFinished(executionId, "aborted",
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
                    execution.currentStepName = null;
                    execution.currentStepIndex = -1;
                    execution.phase = "failed";
                    execution.lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    execution.suspectedStuck = false;
                });
                if (_executions.TryGetValue(executionId, out var failedExec))
                    CodingRiver.UPilot.Flow.TestRunnerWindow.ExternalRunFinished(executionId, "failed",
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
        private void SyncWindowOnOpen(CodingRiver.UPilot.Flow.TestRunnerWindow _)
        {
            string activeId;
            UPilotFlowExecutionResultPayload snap;
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
                Enum.TryParse<CodingRiver.UPilot.Flow.TestStatus>(c.status, out var st);
                return (
                    yamlPath: c.yamlPath,
                    caseName: c.caseName,
                    status: st,
                    durationMs: c.durationMs,
                    errorCode: c.errorCode,
                    errorMessage: c.errorMessage,
                    stepResults: (List<CodingRiver.UPilot.Flow.StepResult>)null,  // not available in snapshot
                    reportMarkdownPath: c.reportMarkdownPath,
                    reportJsonPath: c.reportJsonPath
                );
            });

            CodingRiver.UPilot.Flow.TestRunnerWindow.TrySyncActiveRun(
                activeId,
                allYamlPaths,
                totalAll,
                snap.currentYamlPath,
                snap.passed, snap.failed, snap.errors, snap.skipped,
                completedCases,
                batchIdxForSync, batchTotalForSync, batchSizeForSync);
        }

        private async Task SendCaseFinishedEventAsync(
            string executionId,
            UPilotFlowCaseResultPayload casePayload,
            CancellationToken cancellationToken)
        {
            try
            {
                int completed = _executions.TryGetValue(executionId, out UPilotFlowExecutionResultPayload execution)
                    ? execution.cases.Count
                    : 0;
                await _bridge.SendEventAsync(
                    $"evt-upilot_flow-case-{executionId}-{completed}",
                    "upilot_flow.case_finished",
                    casePayload,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("UPilot.Flow", $"发送 case_finished 事件失败: {ex.Message}");
            }
        }

        private static TestOptions BuildTestOptions(UPilotFlowRunPayload payload, string executionId)
        {
            string reportPath = BuildExecutionReportPath(payload.reportPath, executionId);
            return UPilotFlowConfigurationService.Resolve(new UPilotFlowExecutionSettings
            {
                Headed = payload.headed,
                DebugOnFailure = payload.debugOnFailure,
                StopOnFirstFailure = payload.stopOnFirstFailure,
                ContinueOnStepFailure = payload.continueOnStepFailure,
                ScreenshotOnFailure = payload.screenshotOnFailure,
                DefaultTimeoutMs = payload.defaultTimeoutMs,
                EnableVerboseLog = payload.enableVerboseLog,
                ReportOutputPath = reportPath,
                ScreenshotPath = Path.Combine(reportPath, "Screenshots"),
            });
        }

        public static List<string> ResolveYamlPaths(UPilotFlowRunPayload payload)
        {
            return UPilotFlowPathResolver.Resolve(payload.yamlPaths, payload.yamlDirectory, MaxYamlPaths);
        }

        public static UPilotFlowCaseResultPayload ToCasePayload(TestResult result, string yamlPath, string reportPath, ReportPathBuilder reportPaths)
        {
            var payload = new UPilotFlowCaseResultPayload
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
                    var stepPayload = new UPilotFlowStepResultPayload
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

        public static void ApplyCaseCounters(UPilotFlowExecutionResultPayload execution, TestStatus status)
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

        private void UpdateExecution(string executionId, Action<UPilotFlowExecutionResultPayload> updater)
        {
            if (!_executions.TryGetValue(executionId, out UPilotFlowExecutionResultPayload execution))
            {
                return;
            }

            lock (_stateLock)
            {
                updater(execution);
            }
        }

        private UPilotFlowExecutionResultPayload CloneExecution(UPilotFlowExecutionResultPayload source)
        {
            lock (_stateLock)
            {
                var clone = new UPilotFlowExecutionResultPayload
                {
                    executionId = source.executionId,
                    status = source.status,
                    startedAtUtc = source.startedAtUtc,
                    endedAtUtc = source.endedAtUtc,
                    currentYamlPath = source.currentYamlPath,
                    currentCaseName = source.currentCaseName,
                    currentStepName = source.currentStepName,
                    currentStepIndex = source.currentStepIndex,
                    phase = source.phase,
                    phaseStartedAt = source.phaseStartedAt,
                    lastProgressAt = source.lastProgressAt,
                    phaseElapsedMs = source.phaseElapsedMs,
                    suspectedStuck = source.suspectedStuck,
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

                foreach (UPilotFlowCaseResultPayload caseResult in source.cases)
                {
                    clone.cases.Add(CloneCase(caseResult));
                }

                return clone;
            }
        }

        private static UPilotFlowCaseResultPayload CloneCase(UPilotFlowCaseResultPayload source)
        {
            var clone = new UPilotFlowCaseResultPayload
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
            foreach (UPilotFlowStepResultPayload step in source.stepResults)
            {
                clone.stepResults.Add(CloneStep(step));
            }

            return clone;
        }

        private static UPilotFlowStepResultPayload CloneStep(UPilotFlowStepResultPayload source)
        {
            var clone = new UPilotFlowStepResultPayload
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
            string root = string.IsNullOrWhiteSpace(baseReportPath) ? "Reports/UPilot/Flow" : baseReportPath;
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
