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
    public sealed class UPilotCompileService
    {
        private static readonly string CompileLogDir = Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Logs", "UPilot"
        );
        private static readonly string CompileErrorsPath = Path.Combine(CompileLogDir, "CompileErrors.json");

        private readonly List<CompileErrorItemPayload> _lastErrors = new();
        private int _lastWarningCount;
        private string _lastRequestId = string.Empty;

        /// <summary>Active MCP compile.request id, or empty when compile was not started via MCP.</summary>
        public string LastRequestId => _lastRequestId;

        private TaskCompletionSource<bool> _compileTcs;

        public bool IsCompiling { get; private set; }
        public string Status { get; private set; } = "idle";
        public string Phase { get; private set; } = "idle";
        public long CompileStartedAt { get; private set; }
        public long CompileFinishedAt { get; private set; }
        public long LastProgressAt { get; private set; }
        public int LastErrorCount => _lastErrors.Count;
        public bool HasCompileErrors { get; private set; }
        public bool IsRequestCompileActive { get; private set; }

        public UPilotCompileService()
        {
            Logger.Log("COMPILE", "CompileService 初始化");
            // Always track compilation results so resource.editorState can report errors
            // even for compiles triggered outside of compile.request (e.g. AssetDatabase.Refresh).
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // Restore persistent errors after domain reload
            TryRestoreFromDisk();
            // AssemblyReloadEvents subscriptions do not survive into the new AppDomain.
            // Normalize a restored reload snapshot during construction as the reliable path.
            MarkVerifyingAfterReload();
        }

        /// <summary>
        /// Persist compile errors to disk so they survive domain reload.
        /// </summary>
        private void PersistToDisk()
        {
            try
            {
                Directory.CreateDirectory(CompileLogDir);
                var payload = new CompileErrorsPayload
                {
                    requestId = _lastRequestId,
                    status = Status,
                    phase = Phase,
                    total = _lastErrors.Count,
                    warningCount = _lastWarningCount,
                    startedAt = CompileStartedAt,
                    finishedAt = CompileFinishedAt,
                    lastProgressAt = LastProgressAt,
                    errors = new List<CompileErrorItemPayload>(_lastErrors),
                };
                var json = JsonUtility.ToJson(payload, true);
                File.WriteAllText(CompileErrorsPath, json);
                Logger.Log("COMPILE", $"编译错误已持久化: {CompileErrorsPath} errors={_lastErrors.Count}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("COMPILE", $"持久化编译错误失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore compile errors from disk after domain reload.
        /// </summary>
        public void TryRestoreFromDisk()
        {
            try
            {
                if (!File.Exists(CompileErrorsPath))
                    return;

                var json = File.ReadAllText(CompileErrorsPath);
                var payload = JsonUtility.FromJson<CompileErrorsPayload>(json);
                if (payload == null)
                    return;

                _lastRequestId = payload.requestId ?? string.Empty;
                Status = string.IsNullOrEmpty(payload.status) ? "finished" : payload.status;
                Phase = string.IsNullOrEmpty(payload.phase)
                    ? (payload.total > 0 ? "failed" : "completed")
                    : payload.phase;
                IsCompiling = string.Equals(Phase, "queued", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Phase, "compiling", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase);
                IsRequestCompileActive = IsCompiling && !string.IsNullOrEmpty(_lastRequestId);
                _lastWarningCount = payload.warningCount;
                CompileStartedAt = payload.startedAt;
                CompileFinishedAt = payload.finishedAt;
                LastProgressAt = payload.lastProgressAt > 0
                    ? payload.lastProgressAt
                    : Math.Max(CompileStartedAt, CompileFinishedAt);
                _lastErrors.Clear();
                if (payload.errors != null)
                    _lastErrors.AddRange(payload.errors);
                HasCompileErrors = _lastErrors.Count > 0;
                Logger.Log("COMPILE", $"编译错误已从磁盘恢复: errors={_lastErrors.Count} requestId={_lastRequestId}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("COMPILE", $"恢复编译错误失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear persistent compile errors on disk.
        /// </summary>
        public void ClearPersistentErrors()
        {
            try
            {
                if (File.Exists(CompileErrorsPath))
                {
                    File.Delete(CompileErrorsPath);
                    Logger.Log("COMPILE", "已清除持久化编译错误");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("COMPILE", $"清除持久化编译错误失败: {ex.Message}");
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

        public bool TryBeginCompile(string requestId)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Logger.LogWarning("COMPILE", $"编译失败: Unity 正在 PlayMode 或切换 PlayMode，跳过脚本编译 requestId={requestId}");
                return false;
            }

            if (IsCompiling || EditorApplication.isCompiling)
            {
                Logger.LogWarning("COMPILE", $"编译失败: 已在编译中 requestId={requestId}");
                return false;
            }

            var focus = GetFocusStateString();
            Logger.Log("COMPILE", $"开始编译: requestId={requestId} 焦点={focus}");

            _lastRequestId = requestId;
            IsCompiling = true;
            IsRequestCompileActive = true;
            Status = "queued";
            Phase = "queued";
            LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CompileStartedAt = 0;
            CompileFinishedAt = 0;
            _compileTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Clear previous persistent errors when starting a new compile
            ClearPersistentErrors();

            // Refresh to detect newly added scripts before compiling
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

        // Called when compilation starts (both auto and request-scoped)
        private void OnCompilationStarted(object _)
        {
            IsCompiling = true;
            Status = "compiling";
            Phase = "compiling";
            _lastErrors.Clear();
            _lastWarningCount = 0;
            HasCompileErrors = false;
            CompileStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastProgressAt = CompileStartedAt;
            CompileFinishedAt = 0;
            Logger.Log("COMPILE", "编译流水线开始");
        }

        public void ClearStaleCompileBusy(string reason, bool ignoreEditorCompiling = false)
        {
            if (!IsCompiling || (!ignoreEditorCompiling && EditorApplication.isCompiling))
                return;

            IsCompiling = false;
            IsRequestCompileActive = false;
            CompileFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastProgressAt = CompileFinishedAt;
            Status = "failed";
            Phase = "failed";
            _compileTcs?.TrySetResult(false);
            Logger.LogWarning("COMPILE", $"清理编译忙状态: {reason}");
        }

        // Called on main thread for each assembly as it finishes (both auto and request-scoped)
        private void OnAssemblyCompilationFinished(string assembly, CompilerMessage[] messages)
        {
            bool hadError = false;
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Warning)
                {
                    _lastWarningCount++;
                    continue;
                }
                if (msg.type != CompilerMessageType.Error) continue;

                hadError = true;
                _lastErrors.Add(new CompileErrorItemPayload
                {
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

        // Called on main thread when all assemblies are done (both auto and request-scoped)
        private void OnCompilationFinished(object _)
        {
            CompileFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastProgressAt = CompileFinishedAt;
            IsCompiling = false;
            IsRequestCompileActive = false;
            Status = _lastErrors.Count > 0 ? "failed" : "finished";
            Phase = _lastErrors.Count > 0 ? "failed" : "completed";
            var elapsed = CompileFinishedAt - CompileStartedAt;
            var focus = GetFocusStateString();
            Logger.Log("COMPILE", $"编译流水线完成: requestId={_lastRequestId} errors={_lastErrors.Count} warnings={_lastWarningCount} elapsed={elapsed}ms 焦点={focus}");

            // Persist errors to disk so they survive domain reload
            PersistToDisk();

            _compileTcs?.TrySetResult(true);
        }

        public void MarkDomainReload()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var recentCompile = CompileFinishedAt > 0 && now - CompileFinishedAt <= 30000;
            if (!IsCompiling && !IsRequestCompileActive && !recentCompile)
                return;

            Status = "compiling";
            Phase = "domain_reload";
            LastProgressAt = now;
            PersistToDisk();
        }

        public void MarkVerifyingAfterReload()
        {
            if (!string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase))
                return;

            IsCompiling = false;
            Status = "verifying";
            Phase = "verifying";
            LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistToDisk();
        }

        public void CompleteVerification()
        {
            if (!string.Equals(Phase, "verifying", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Phase, "domain_reload", StringComparison.OrdinalIgnoreCase))
                return;

            IsCompiling = false;
            IsRequestCompileActive = false;
            Status = _lastErrors.Count > 0 ? "failed" : "finished";
            Phase = _lastErrors.Count > 0 ? "failed" : "completed";
            CompileFinishedAt = CompileFinishedAt > 0
                ? CompileFinishedAt
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistToDisk();
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

        public CompileErrorsPayload BuildCompileErrorsPayload(string requestId) =>
            new CompileErrorsPayload
            {
                requestId = requestId,
                status = Status,
                phase = Phase,
                total = _lastErrors.Count,
                warningCount = _lastWarningCount,
                startedAt = CompileStartedAt,
                finishedAt = CompileFinishedAt,
                lastProgressAt = LastProgressAt,
                errors = new List<CompileErrorItemPayload>(_lastErrors),
            };

        public CompileErrorsPayload BuildLastCompileErrorsPayload() =>
            BuildCompileErrorsPayload(_lastRequestId);
    }
}
