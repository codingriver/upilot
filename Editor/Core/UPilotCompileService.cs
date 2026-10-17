// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotCompileService
    {
        private static readonly string CompileLogDir = Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Logs", "UPilot"
        );
        private static readonly string CompileErrorsPath = Path.Combine(CompileLogDir, "CompileErrors.json");

        private readonly List<CompileErrorItemPayload> _lastErrors = new();
        private const int MaxPersistedWarnings = 1000;
        private readonly List<CompileErrorItemPayload> _lastWarnings = new();
        // Old persisted records have no warning detail list.  Keep that state
        // distinct from a newly observed compilation with zero warnings.
        private bool _warningDetailsAvailable = false;
        private bool _warningsTruncated;
        private int _lastWarningCount;
        private string _lastRequestId = string.Empty;
        private string _compileOperationId = string.Empty;
        private string _writeBatchId = string.Empty;
        private long _writeBatchCreatedAt;
        private string _compileOrigin = string.Empty;
        private string _reloadId = string.Empty;

        /// <summary>Active MCP compile.request id, or empty when compile was not started via MCP.</summary>
        public string LastRequestId => _lastRequestId;
        public string CompileOperationId => _compileOperationId;
        public string WriteBatchId => _writeBatchId;
        public long WriteBatchCreatedAt => _writeBatchCreatedAt;
        public string CompileOrigin => _compileOrigin;
        public string ReloadId => _reloadId;

        private TaskCompletionSource<bool> _compileTcs;

        public bool IsCompiling { get; private set; }
        public string Status { get; private set; } = "idle";
        public string Phase { get; private set; } = "idle";
        public long CompileStartedAt { get; private set; }
        public long CompileFinishedAt { get; private set; }
        public long LastProgressAt { get; private set; }
        public long LastCompileRequestedAt { get; private set; }
        public long LastCompileStartedAt { get; private set; }
        public long LastCompilerFinishedAt { get; private set; }
        public long LastCompileVerifiedAt { get; private set; }
        public long LastTerminalCompileAt { get; private set; }
        public int LastErrorCount => _lastErrors.Count;
        public int LastWarningCount => _lastWarningCount;
        public bool HasCompileErrors { get; private set; }
        public bool IsRequestCompileActive { get; private set; }
        public bool Terminal { get; private set; }
        public bool VerificationPending { get; private set; }
        public bool ErrorsVerified { get; private set; }
        public bool DomainReloadObserved { get; private set; }
        public string LastStartFailureCode { get; private set; } = string.Empty;

        public UPilotCompileService()
        {
            Logger.Log("COMPILE", "CompileService 初始化");
            // Always track compilation results so resource.editorState can report errors
            // even for compiles triggered outside of compile.request (e.g. AssetDatabase.Refresh).
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += MarkDomainReload;

            // Restore persistent errors after domain reload
            TryRestoreFromDisk();
            // ConfigureEvidenceIdentity restores/verifies the current producer before normalizing reload.
        }

        /// <summary>
        /// Restore compile errors from disk after domain reload.
        /// </summary>
        public void TryRestoreFromDisk()
        {
            lock (CompileEvidenceLock)
            {
                try
                {
                    if (!File.Exists(EvidencePath))
                        return;

                    var json = File.ReadAllText(EvidencePath);
                    var payload = JsonUtility.FromJson<CompileErrorsPayload>(json);
                    if (payload == null)
                        throw new InvalidDataException("Compile evidence payload is empty.");

                    _lastRequestId = payload.requestId ?? string.Empty;
                    _compileOperationId = payload.compileOperationId ?? string.Empty;
                    _writeBatchId = payload.writeBatchId ?? string.Empty;
                    _writeBatchCreatedAt = payload.writeBatchCreatedAt;
                    _compileOrigin = payload.compileOrigin ?? string.Empty;
                    _reloadId = payload.reloadId ?? string.Empty;
                    Status = string.IsNullOrEmpty(payload.status) ? "finished" : payload.status;
                    Phase = string.IsNullOrEmpty(payload.phase)
                        ? (payload.total > 0 ? "failed" : "completed")
                        : payload.phase;
                    IsCompiling = string.Equals(Phase, "queued", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(Phase, "compiling", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase);
                    IsRequestCompileActive = IsCompiling && !string.IsNullOrEmpty(_lastRequestId);
                    Terminal = payload.terminal;
                    VerificationPending = payload.verificationPending;
                    ErrorsVerified = payload.errorsVerified;
                    DomainReloadObserved = payload.domainReloadObserved;
                    _lastWarningCount = payload.warningCount;
                    // JsonUtility uses false for a field absent from legacy payloads.
                    // Do not infer availability from an empty/missing list: it may mean
                    // that the historical compile only persisted its aggregate count.
                    _warningDetailsAvailable = payload.warningDetailsAvailable;
                    CompileStartedAt = payload.startedAt;
                    CompileFinishedAt = payload.finishedAt;
                    LastProgressAt = payload.lastProgressAt > 0
                        ? payload.lastProgressAt
                        : Math.Max(CompileStartedAt, CompileFinishedAt);
                    LastCompileRequestedAt = payload.lastCompileRequestedAt;
                    LastCompileStartedAt = payload.lastCompileStartedAt;
                    LastCompilerFinishedAt = payload.lastCompilerFinishedAt;
                    LastCompileVerifiedAt = payload.lastCompileVerifiedAt;
                    LastTerminalCompileAt = payload.lastTerminalCompileAt;
                    _lastErrors.Clear();
                    if (payload.errors != null)
                        _lastErrors.AddRange(payload.errors);
                    _lastWarnings.Clear();
                    if (_warningDetailsAvailable && payload.warnings != null)
                    {
                        var retained = Math.Min(payload.warnings.Count, MaxPersistedWarnings);
                        for (var index = 0; index < retained; index++)
                            _lastWarnings.Add(payload.warnings[index]);
                        _warningsTruncated = payload.warningsTruncated ||
                            payload.warnings.Count > retained || _lastWarningCount > retained;
                    }
                    else
                    {
                        // An unavailable legacy detail list is unknown, not truncated.
                        _warningsTruncated = false;
                    }
                    HasCompileErrors = _lastErrors.Count > 0;
                    ApplyRestoredEvidence(payload);
                    Logger.Log("COMPILE", $"编译错误已从磁盘恢复: errors={_lastErrors.Count} requestId={_lastRequestId}");
                }
                catch (Exception ex)
                {
                    _compileEvidence = null;
                    ErrorsVerified = false; Terminal = false; VerificationPending = false;
                    IsCompiling = false; IsRequestCompileActive = false;
                    Phase = "unknown"; Status = "unknown";
                    Logger.ReportFailure("COMPILE", "恢复编译错误失败", ex);
                }

            }
        }

        /// <summary>
        /// Clear persistent compile errors on disk.
        /// </summary>
        public void ClearPersistentErrors()
        {
            lock (CompileEvidenceLock)
            {
                try
                {
                    if (File.Exists(EvidencePath))
                    {
                        File.Delete(EvidencePath);
                        Logger.Log("COMPILE", "已清除持久化编译错误");
                    }
                    _compileEvidence = null;
                    ErrorsVerified = false; Terminal = false;
                    if (!IsCompiling)
                    {
                        Phase = "unknown"; Status = "unknown"; VerificationPending = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.ReportFailure("COMPILE", "清除持久化编译错误失败", ex);
                }

            }
        }

        /// <summary>
        /// Tries to begin a new compilation. Must be called from the main thread.
        /// Returns false (EDITOR_BUSY) if a compile is already in progress.
        /// </summary>
        private static string GetFocusStateString()
        {
            try
            {
                var fw = EditorWindow.focusedWindow;
                if (fw != null)
                    return $"focused({fw.GetType().Name})";
                return "unfocused";
            }
            catch
            {
                return "unknown";
            }
        }

        public bool TryBeginCompile(
            string requestId,
            string writeBatchId = "",
            long writeBatchCreatedAt = 0,
            Func<bool> beforeRequest = null)
        {
            bool queuedPersisted;
            lock (CompileEvidenceLock)
            {
                LastStartFailureCode = string.Empty;
                if (UPilotPlayModeCompilePolicy.CurrentMode != "edit")
                {
                    LastStartFailureCode = "EditorInPlayMode";
                    return false;
                }
                if (IsCompiling || EditorApplication.isCompiling)
                {
                    LastStartFailureCode = "EditorBusy";
                    return false;
                }

                Logger.Log("COMPILE", $"开始编译: requestId={requestId} 焦点={GetFocusStateString()}");
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _lastRequestId = requestId;
                _compileOperationId = Guid.NewGuid().ToString("N");
                _writeBatchId = writeBatchId ?? string.Empty;
                _writeBatchCreatedAt = Math.Max(0, writeBatchCreatedAt);
                _compileOrigin = "mcp";
                _reloadId = string.Empty;
                IsCompiling = true;
                IsRequestCompileActive = true;
                Status = "queued";
                Phase = "queued";
                Terminal = false;
                VerificationPending = true;
                ErrorsVerified = false;
                DomainReloadObserved = false;
                LastCompileRequestedAt = now;
                LastProgressAt = now;
                ResetCompileCompletionEvidence();
                _compileTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Replace prior evidence before notifying observers or requesting compilation.
                queuedPersisted = RecordMilestoneAndPersist("queued");
            }
            NotifyLocalState("queued");
            if (!queuedPersisted || (beforeRequest != null && !beforeRequest()))
            {
                lock (CompileEvidenceLock)
                {
                    IsCompiling = false;
                    IsRequestCompileActive = false;
                    Status = "failed";
                    Phase = "failed";
                    Terminal = true;
                    VerificationPending = false;
                    ErrorsVerified = false;
                    LastTerminalCompileAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    LastProgressAt = LastTerminalCompileAt;
                    LastStartFailureCode = "StatePersistenceFailed";
                    if (queuedPersisted) RecordMilestoneAndPersist("terminal");
                    _compileTcs?.TrySetResult(false);
                }
                NotifyLocalState("terminal");
                Logger.LogError("COMPILE", $"编译未触发: queued 状态持久化失败 requestId={requestId}");
                return false;
            }
            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation();
            return true;
        }

        /// <summary>
        /// Returns a Task that completes when the current compilation finishes.
        /// If no compile is running, returns an already-completed Task.
        /// </summary>
        public Task WaitForCompileAsync() =>
            _compileTcs?.Task ?? Task.CompletedTask;

        private void ResetCompileCompletionEvidence()
        {
            CompileStartedAt = 0;
            CompileFinishedAt = 0;
            LastCompileStartedAt = 0;
            LastCompilerFinishedAt = 0;
            LastCompileVerifiedAt = 0;
            LastTerminalCompileAt = 0;
            _lastErrors.Clear();
            _lastWarnings.Clear();
            _lastWarningCount = 0;
            _warningDetailsAvailable = true;
            _warningsTruncated = false;
            HasCompileErrors = false;
        }

        // Called when compilation starts (both auto and request-scoped)
        private void OnCompilationStarted(object context)
        {
            lock (CompileEvidenceLock)
            {
                if (UPilotDiagnosticModuleService.IsCompilationContext(context)) return;
                if (!IsRequestCompileActive || string.IsNullOrEmpty(_compileOperationId))
                {
                    _lastRequestId = string.Empty;
                    _compileOperationId = Guid.NewGuid().ToString("N");
                    _writeBatchId = string.Empty;
                    _writeBatchCreatedAt = 0;
                    _compileOrigin = "unity_auto";
                    _reloadId = string.Empty;
                    LastCompileRequestedAt = 0;
                    DomainReloadObserved = false;
                }
                IsCompiling = true;
                Status = "compiling";
                Phase = "compiling";
                Terminal = false;
                VerificationPending = true;
                ErrorsVerified = false;
                ResetCompileCompletionEvidence();
                CompileStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                LastCompileStartedAt = CompileStartedAt;
                LastProgressAt = CompileStartedAt;
                RecordMilestoneAndPersist("compiling");
                Logger.Log("COMPILE", "编译流水线开始");

            }
            NotifyLocalState("compiling");
        }

        public void ClearStaleCompileBusy(string reason, bool ignoreEditorCompiling = false)
        {
            lock (CompileEvidenceLock)
            {
                if (!IsCompiling || (!ignoreEditorCompiling && EditorApplication.isCompiling))
                    return;

                IsCompiling = false;
                IsRequestCompileActive = false;
                LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Status = "failed";
                Phase = "failed";
                Terminal = true;
                VerificationPending = false;
                ErrorsVerified = false;
                LastTerminalCompileAt = LastProgressAt;
                RecordMilestoneAndPersist("terminal");
                _compileTcs?.TrySetResult(false);
                Logger.LogError("COMPILE", $"清理未完成的编译忙状态: {reason}");

            }
            NotifyLocalState("terminal");
        }

        // Called on main thread for each assembly as it finishes (both auto and request-scoped)
        private void OnAssemblyCompilationFinished(string assembly, CompilerMessage[] messages)
        {
            lock (CompileEvidenceLock)
            {
                if (UPilotDiagnosticModuleService.IsCompilationPath(assembly)) return;
                bool hadError = false;
                foreach (var msg in messages)
                {
                    if (msg.type == CompilerMessageType.Warning)
                    {
                        _lastWarningCount++;
                        if (_lastWarnings.Count < MaxPersistedWarnings)
                        {
                            _lastWarnings.Add(new CompileErrorItemPayload
                            {
                                assemblyName = assembly,
                                file = msg.file,
                                line = msg.line,
                                column = msg.column,
                                message = msg.message,
                                severity = "warning",
                            });
                        }
                        else _warningsTruncated = true;
                        continue;
                    }
                    if (msg.type != CompilerMessageType.Error) continue;

                    hadError = true;
                    _lastErrors.Add(new CompileErrorItemPayload
                    {
                        assemblyName = assembly,
                        file = msg.file,
                        line = msg.line,
                        column = msg.column,
                        message = msg.message,
                        severity = "error",
                    });
                }
                if (hadError)
                {
                    HasCompileErrors = true;
                }

            }
        }

        // Called on main thread when all assemblies are done (both auto and request-scoped)
        private void OnCompilationFinished(object context)
        {
            lock (CompileEvidenceLock)
            {
                if (UPilotDiagnosticModuleService.IsCompilationContext(context)) return;
                CompileFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                LastProgressAt = CompileFinishedAt;
                IsCompiling = false;
                IsRequestCompileActive = false;
                Status = "compiler_finished";
                Phase = "compiler_finished";
                Terminal = false;
                VerificationPending = true;
                ErrorsVerified = false;
                LastCompilerFinishedAt = CompileFinishedAt;
                var elapsed = CompileFinishedAt - CompileStartedAt;
                var focus = GetFocusStateString();
                Logger.Log("COMPILE", $"编译流水线完成: requestId={_lastRequestId} errors={_lastErrors.Count} warnings={_lastWarningCount} elapsed={elapsed}ms 焦点={focus}");

                RecordMilestoneAndPersist("compiler_finished");

                _compileTcs?.TrySetResult(true);

            }
            NotifyLocalState("compiler_finished");
        }

        public void MarkDomainReload()
        {
            lock (CompileEvidenceLock)
            {
                if (!MatchesEvidenceIdentity(_compileEvidence) || Phase == "domain_reload") return;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var recentCompile = CompileFinishedAt > 0 && now - CompileFinishedAt <= 30000;
                if (!IsCompiling && !IsRequestCompileActive && !recentCompile &&
                    (Terminal || !VerificationPending))
                    return;

                Status = "domain_reload";
                Phase = "domain_reload";
                Terminal = false;
                VerificationPending = true;
                DomainReloadObserved = true;
                _reloadId = Guid.NewGuid().ToString("N");
                LastProgressAt = now;
                RecordMilestoneAndPersist("domain_reload");

            }
            NotifyLocalState("domain_reload");
        }

        public void MarkVerifyingAfterReload()
        {
            lock (CompileEvidenceLock)
            {
                if (!MatchesEvidenceIdentity(_compileEvidence) ||
                    !string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase))
                    return;

                IsCompiling = false;
                Status = "verifying";
                Phase = "verifying";
                Terminal = false;
                VerificationPending = true;
                DomainReloadObserved = true;
                LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RecordMilestoneAndPersist("verifying");

            }
            NotifyLocalState("verifying");
        }

        public bool CompleteVerification()
        {
            lock (CompileEvidenceLock)
            {
                if (!string.Equals(Phase, "verifying", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Phase, "compiler_finished", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (IsCompiling || EditorApplication.isCompiling || !VerifyPersistedCompilerResult()) return false;

                // Build a candidate without publishing success in memory before its checked persistence.
                var candidate = BuildLastCompileErrorsPayload(includeWarnings: true);
                var verifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                candidate.status = _lastErrors.Count > 0 ? "failed" : "finished";
                candidate.phase = _lastErrors.Count > 0 ? "failed" : "completed";
                candidate.lastProgressAt = verifiedAt;
                candidate.lastCompileVerifiedAt = verifiedAt;
                candidate.lastTerminalCompileAt = verifiedAt;
                candidate.terminal = true;
                candidate.verificationPending = false;
                candidate.errorsVerified = true;
                var evidence = candidate.compileEvidence;
                var terminal = new CompileMilestonePayload
                {
                    stage = "terminal", phase = candidate.phase, revision = ++evidence.revision,
                    observedAt = verifiedAt, terminal = true, errorsVerified = true, errorCount = candidate.total,
                };
                var index = evidence.milestones.FindIndex(value => value.stage == "terminal");
                if (index < 0) evidence.milestones.Add(terminal); else evidence.milestones[index] = terminal;
                if (!PersistToDisk(candidate)) return false;

                IsCompiling = false; IsRequestCompileActive = false;
                Status = candidate.status; Phase = candidate.phase;
                LastProgressAt = verifiedAt; LastCompileVerifiedAt = verifiedAt; LastTerminalCompileAt = verifiedAt;
                Terminal = true; VerificationPending = false; ErrorsVerified = true;
                _compileEvidence = CloneEvidence(evidence);
            }
            NotifyLocalState("terminal");
            return true;
        }

        public CompileStatusPayload BuildStartedStatusPayload(string requestId) =>
            new CompileStatusPayload
            {
                requestId = requestId,
                status = "started",
                errorCount = 0,
                warningCount = 0,
                startedAt = CompileStartedAt,
                finishedAt = 0,
            };

        public CompileStatusPayload BuildFinishedStatusPayload(string requestId) =>
            new CompileStatusPayload
            {
                requestId = requestId,
                status = "finished",
                errorCount = _lastErrors.Count,
                warningCount = _lastWarningCount,
                startedAt = CompileStartedAt,
                finishedAt = CompileFinishedAt,
            };

        public CompileErrorsPayload BuildCompileErrorsPayload(string requestId, bool includeWarnings = false)
        {
            lock (CompileEvidenceLock) return new CompileErrorsPayload
            {
                requestId = requestId,
                compileEvidence = BuildCompileEvidencePayload(),
                compileOperationId = _compileOperationId,
                writeBatchId = _writeBatchId,
                writeBatchCreatedAt = _writeBatchCreatedAt,
                compileOrigin = _compileOrigin,
                status = Status,
                phase = Phase,
                terminal = Terminal,
                verificationPending = VerificationPending,
                errorsVerified = ErrorsVerified,
                total = _lastErrors.Count,
                warningCount = _lastWarningCount,
                warningDetailsAvailable = _warningDetailsAvailable,
                warningsTruncated = _warningsTruncated,
                startedAt = CompileStartedAt,
                finishedAt = CompileFinishedAt,
                lastProgressAt = LastProgressAt,
                lastCompileRequestedAt = LastCompileRequestedAt,
                lastCompileStartedAt = LastCompileStartedAt,
                lastCompilerFinishedAt = LastCompilerFinishedAt,
                lastCompileVerifiedAt = LastCompileVerifiedAt,
                lastTerminalCompileAt = LastTerminalCompileAt,
                reloadId = _reloadId,
                domainReloadObserved = DomainReloadObserved,
                errors = new List<CompileErrorItemPayload>(_lastErrors),
                warnings = includeWarnings && _warningDetailsAvailable
                    ? new List<CompileErrorItemPayload>(_lastWarnings)
                    : null,
            };
        }

        public CompileErrorsPayload BuildLastCompileErrorsPayload(bool includeWarnings = false) =>
            BuildCompileErrorsPayload(_lastRequestId, includeWarnings);
    }
}
