// -----------------------------------------------------------------------
// UPilot Editor - MCP server runtime discovery, download, and version info.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CodingRiver.UPilot
{
    public enum UPilotServerRuntimeMode
    {
        Python,
        StandaloneExe,
    }

    public sealed class UPilotPythonProbeResult
    {
        public bool IsUsable;
        public bool InterpreterUsable;
        public bool DependenciesComplete;
        public string PythonPath = "";
        public string VersionText = "";
        public int Major;
        public int Minor;
        public string Message = "";
        public readonly Dictionary<string, bool> Dependencies = new();
    }

    public sealed class UPilotServerDownloadInfo
    {
        public string Platform = "";
        public string Architecture = "";
        public string Url = "";
        public string Sha256 = "";
        public long SizeBytes;
        public string FileName = "";
    }

    public sealed class UPilotReleaseManifest
    {
        public string UpmVersion = "";
        public string ServerVersion = "";
        public string ProtocolVersion = "";
        public string Channel = "";
        public string CommitSha = "";
        public string MinCompatibleUpm = "";
        public string MinCompatibleServer = "";
        public readonly List<UPilotServerDownloadInfo> Downloads = new();
    }

    public sealed class UPilotCompatibilityStatus
    {
        public bool IsCompatible;
        public string Reason = "";
        public string CurrentUpmVersion = "";
        public string CurrentServerVersion = "";
        public string CurrentProtocolVersion = "";
        public string ManifestChannel = "";
        public string ManifestUpmVersion = "";
        public string ManifestServerVersion = "";
        public string ManifestProtocolVersion = "";
        public string ManifestMinCompatibleUpm = "";
        public string ManifestMinCompatibleServer = "";
    }

    public sealed class UPilotDownloadState
    {
        public bool IsRunning;
        public bool IsComplete;
        public bool IsCancelled;
        public string Phase = "";
        public string WarningMessage = "";
        public string ErrorMessage = "";
        public string Version = "";
        public string DownloadUrl = "";
        public string Sha256 = "";
        public string TargetPath = "";
        public string PlatformDisplayName = "";
        public long BytesReceived;
        public long TotalBytes;
        public int SegmentCount;
        public int CompletedSegments;
        public double StartedAt;
        public double FinishedAt;

        public float Progress
        {
            get
            {
                if (TotalBytes <= 0) return IsComplete ? 1f : 0f;
                return Mathf.Clamp01((float)((double)BytesReceived / TotalBytes));
            }
        }
    }

    public sealed class UPilotPreparedServerDownload
    {
        public string Version = "";
        public string TargetPath = "";
        public string Sha256 = "";
        public string PlatformDisplayName = "";
    }

    public sealed class UPilotPythonEnvironmentState
    {
        public bool IsRunning;
        public bool IsComplete;
        public bool IsCancelled;
        public string Phase = "";
        public string ErrorMessage = "";
        public string PythonPath = "";
        public string VenvPath = "";
        public string InterpreterPath = "";
        public double StartedAt;
        public double FinishedAt;
    }

    public sealed class UPilotServerRuntimeService
    {
        public static UPilotServerRuntimeService Instance { get; } = new();

        private const string PackageName = "io.github.codingriver.upilot";
        private const string ManifestFileName = "manifest.json";
        private const string LegacyManifestFileName = "upilot-release-manifest.json";
        private const string ReleaseManifestUrl = "https://github.com/codingriver/upilot/releases/latest/download/manifest.json";
        private const int ParallelDownloadThresholdBytes = 8 * 1024 * 1024;
        private const int ParallelDownloadSegments = 4;
        private const int SegmentRetryCount = 2;
        private const int FileOperationRetryCount = 3;
        private const int FileOperationRetryDelayMs = 2000;
        private const long DiskSpaceSafetyMarginBytes = 64L * 1024 * 1024;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private static bool _verifiedServerHashErrorLogged;
        private static bool _chmodErrorLogged;
        private static bool _projectKeyErrorLogged;
        private static bool _downloadErrorDialogPending;

        private sealed class UPilotDownloadUserActionException : IOException
        {
            public UPilotDownloadUserActionException(string message, Exception innerException = null)
                : base(message, innerException)
            {
            }
        }

        private CancellationTokenSource _downloadCts;
        private CancellationTokenSource _pythonEnvCts;
        private readonly object _stateLock = new();
        private readonly object _pythonEnvLock = new();
        private UPilotDownloadState _downloadState = new();
        private UPilotPythonEnvironmentState _pythonEnvState = new();

        public UPilotDownloadState DownloadState
        {
            get
            {
                lock (_stateLock)
                    return CopyState(_downloadState);
            }
        }

        private static void LogRuntimeProbeErrorOnce(ref bool logged, string context, Exception ex)
        {
            if (logged)
                return;

            logged = true;
            Debug.LogError("[UPilot] " + context + "：" + ex.Message + "\n" + ex);
        }

        public UPilotPythonEnvironmentState PythonEnvironmentState
        {
            get
            {
                lock (_pythonEnvLock)
                    return CopyState(_pythonEnvState);
            }
        }

        public string RuntimeCacheRoot
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    local = Path.GetTempPath();
                return Path.Combine(local, "CodingRiver", "UPilot", "servers");
            }
        }

        public string RuntimeModeLabel
        {
            get
            {
                return GetConfiguredMode() == UPilotServerRuntimeMode.StandaloneExe
                    ? "自动管理"
                    : "本机 Python";
            }
        }

        public static string CurrentPlatformDisplayName => BuildPlatformDisplayName(
            GetCurrentPlatformKey(),
            GetCurrentArchitectureKey());

        public static string CurrentPlatformFileExtension =>
            string.Equals(GetCurrentPlatformKey(), "windows", StringComparison.OrdinalIgnoreCase) ? "exe" : "";

        public static string UpmVersion
        {
            get
            {
                try
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
                    if (package != null && !string.IsNullOrEmpty(package.version))
                        return package.version;
                }
                catch { }

                try
                {
                    var packageJson = Path.Combine(GetPackageRoot(), "package.json");
                    if (File.Exists(packageJson))
                        return ReadJsonString(File.ReadAllText(packageJson), "version");
                }
                catch { }

                return "unknown";
            }
        }

        public static string GetPackageRoot()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
                if (package != null && !string.IsNullOrWhiteSpace(package.resolvedPath))
                    return package.resolvedPath;
            }
            catch { }

            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        public UPilotServerRuntimeMode GetConfiguredMode()
        {
            if (IsSourceUpdateChannel())
                return UPilotServerRuntimeMode.Python;

            var mode = UPilotProjectConfig.Current.runtime?.mode ?? "python";
            return string.Equals(mode, "exe", StringComparison.OrdinalIgnoreCase)
                ? UPilotServerRuntimeMode.StandaloneExe
                : UPilotServerRuntimeMode.Python;
        }

        public void SetPythonRuntime(string pythonPath)
        {
            var config = UPilotProjectConfig.Current;
            config.runtime ??= new UPilotRuntimeConfig();
            config.runtime.mode = "python";
            config.runtime.pythonPath = pythonPath ?? "";
            if (IsSourceUpdateChannel())
            {
                config.runtime.serverExePath = "";
                config.runtime.serverVersion = "";
            }
            UPilotProjectConfig.Save(config);
        }

        public void SetStandaloneExeRuntime(string exePath, string serverVersion = "")
        {
            var config = UPilotProjectConfig.Current;
            config.runtime ??= new UPilotRuntimeConfig();
            if (IsSourceUpdateChannel())
            {
                config.runtime.mode = "python";
                config.runtime.serverExePath = "";
                config.runtime.serverVersion = "";
                UPilotProjectConfig.Save(config);
                return;
            }

            config.runtime.mode = "exe";
            config.runtime.serverExePath = exePath ?? "";
            if (!string.IsNullOrWhiteSpace(serverVersion))
                config.runtime.serverVersion = serverVersion;
            UPilotProjectConfig.Save(config);
        }

        public bool IsStandaloneExeConfigured(out string exePath)
        {
            exePath = UPilotProjectConfig.Current.runtime?.serverExePath ?? "";
            return !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        }

        public void GetConfiguredStandaloneRuntime(out string exePath, out string serverVersion)
        {
            var runtime = UPilotProjectConfig.Current.runtime;
            exePath = runtime?.serverExePath ?? "";
            serverVersion = runtime?.serverVersion ?? "";
        }

        public bool IsPythonRuntimeConfigured(out string pythonPath)
        {
            pythonPath = UPilotProjectConfig.Current.runtime?.pythonPath ?? "";
            return !string.IsNullOrWhiteSpace(pythonPath) && File.Exists(pythonPath);
        }

        public UPilotPythonProbeResult ProbePython()
        {
            var configPython = UPilotProjectConfig.Current.runtime?.pythonPath ?? "";
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configPython))
                candidates.Add(configPython);
            candidates.AddRange(FindExecutables("python"));
            candidates.AddRange(FindExecutables("py"));
            candidates.AddRange(FindExecutables("python3"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UPilotPythonProbeResult firstUsableInterpreter = null;
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                    continue;

                var result = ProbePythonCandidate(candidate);
                if (result.IsUsable)
                    return result;
                if (result.InterpreterUsable && firstUsableInterpreter == null)
                    firstUsableInterpreter = result;
            }

            if (firstUsableInterpreter != null)
                return firstUsableInterpreter;

            return new UPilotPythonProbeResult
            {
                IsUsable = false,
                InterpreterUsable = false,
                DependenciesComplete = false,
                Message = "未找到满足 Python 3.11+ 的解释器。",
            };
        }

        public Task<UPilotReleaseManifest> FetchReleaseManifestAsync()
        {
            return FetchReleaseManifestAsync(ResolveManifestUrl());
        }

        public async Task<UPilotReleaseManifest> FetchReleaseManifestAsync(string url)
        {
            var resolvedUrl = string.IsNullOrWhiteSpace(url) ? ResolveManifestUrl() : url;
            try
            {
                var json = await Http.GetStringAsync(resolvedUrl);
                return ParseManifest(json);
            }
            catch when (CanFallbackToLegacyManifest(resolvedUrl))
            {
                var json = await Http.GetStringAsync(ToLegacyManifestUrl(resolvedUrl));
                return ParseManifest(json);
            }
        }

        public static string ResolveManifestUrl()
        {
            var updates = UPilotProjectConfig.Current.updates ?? new UPilotUpdateConfig();
            if (!string.IsNullOrWhiteSpace(updates.manifestUrl))
                return updates.manifestUrl;

            return ReleaseManifestUrl;
        }

        public static string ResolveUpdateChannel()
        {
            var updates = UPilotProjectConfig.Current.updates ?? new UPilotUpdateConfig();
            if (IsDevelopmentPackageInstall())
                return "source";

            var configured = (updates.channel ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(configured) &&
                !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (IsReleaseChannel(configured))
                    return "release";
                if (IsSourceChannel(configured))
                    return "source";
                return configured;
            }

            return InferDefaultUpdateChannel();
        }

        private static string InferDefaultUpdateChannel()
        {
            if (IsDevelopmentPackageInstall())
                return "source";

            var version = UpmVersion;
            if (IsMainChannel(version) || version.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0)
                return "source";

            return IsStrictSemver(version) ? "release" : "source";
        }

        public static bool IsDevelopmentPackageInstall()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
                if (package == null)
                    return false;

                if (package.source == PackageSource.Local || package.source == PackageSource.Embedded)
                    return true;

                var packageId = package.packageId ?? "";
                if (packageId.IndexOf("#main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    packageId.IndexOf("main-nightly", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }

            return false;
        }

        public void StartDownloadLatestServerExe()
        {
            if (IsSourceUpdateChannel())
            {
                lock (_stateLock)
                {
                    _downloadState = new UPilotDownloadState
                    {
                        IsRunning = false,
                        Phase = "已跳过",
                        ErrorMessage = "开发版仅支持本机 Python，不下载自动管理 MCP 服务。",
                        PlatformDisplayName = CurrentPlatformDisplayName,
                        FinishedAt = EditorApplication.timeSinceStartup,
                    };
                }
                return;
            }

            lock (_stateLock)
            {
                if (_downloadState.IsRunning)
                    return;
                _downloadState = new UPilotDownloadState
                {
                    IsRunning = true,
                    Phase = "正在获取服务",
                    PlatformDisplayName = CurrentPlatformDisplayName,
                    StartedAt = EditorApplication.timeSinceStartup,
                };
            }

            _downloadCts = new CancellationTokenSource();
            _ = RunDownloadLatestServerExeAsync(activateOnComplete: true, manifest: null, _downloadCts.Token);
        }

        public async Task<UPilotPreparedServerDownload> PrepareLatestServerExeAsync(UPilotReleaseManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (IsSourceUpdateChannel() || IsSourceChannel(manifest.Channel) || IsSourceChannel(manifest.ServerVersion))
                throw new InvalidOperationException("开发版仅支持本机 Python，不准备自动管理 MCP 服务。");

            lock (_stateLock)
            {
                if (_downloadState.IsRunning)
                    throw new InvalidOperationException("已有服务下载任务正在进行。");
                _downloadState = new UPilotDownloadState
                {
                    IsRunning = true,
                    Phase = "正在准备服务文件",
                    PlatformDisplayName = CurrentPlatformDisplayName,
                    StartedAt = EditorApplication.timeSinceStartup,
                };
            }

            _downloadCts = new CancellationTokenSource();
            return await DownloadLatestServerExeAsync(manifest, activateOnComplete: false, _downloadCts.Token);
        }

        public bool ActivatePreparedStandaloneExe(
            string exePath,
            string serverVersion,
            out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(exePath))
            {
                error = "未找到已准备好的服务文件。";
                return false;
            }

            if (!File.Exists(exePath))
            {
                error = "已准备好的服务文件不存在：" + exePath;
                return false;
            }

            try
            {
                EnsureExecutablePermission(exePath);
                SetStandaloneExeRuntime(exePath, serverVersion);
                return true;
            }
            catch (Exception ex)
            {
                error = "启用服务文件失败：" + ex.Message;
                Debug.LogError("[UPilot] " + error + "\n" + ex);
                return false;
            }
        }

        public void CancelDownload()
        {
            _downloadCts?.Cancel();
        }

        public void StartAutoConfigurePythonEnvironment()
        {
            lock (_pythonEnvLock)
            {
                if (_pythonEnvState.IsRunning)
                    return;
                _pythonEnvState = new UPilotPythonEnvironmentState
                {
                    IsRunning = true,
                    Phase = "检测 Python 环境",
                    StartedAt = EditorApplication.timeSinceStartup,
                };
            }

            _pythonEnvCts = new CancellationTokenSource();
            _ = ConfigurePythonEnvironmentAsync(_pythonEnvCts.Token);
        }

        public void CancelPythonEnvironmentSetup()
        {
            _pythonEnvCts?.Cancel();
        }

        public UPilotCompatibilityStatus EvaluateManifestCompatibility(UPilotReleaseManifest manifest)
        {
            var status = new UPilotCompatibilityStatus
            {
                CurrentUpmVersion = UpmVersion,
                CurrentServerVersion = UPilotMcpServerManager.Instance.GetStatus().ServerVersion,
                CurrentProtocolVersion = "1",
                ManifestChannel = manifest?.Channel ?? "",
                ManifestUpmVersion = manifest?.UpmVersion ?? "",
                ManifestServerVersion = manifest?.ServerVersion ?? "",
                ManifestProtocolVersion = manifest?.ProtocolVersion ?? "",
                ManifestMinCompatibleUpm = manifest?.MinCompatibleUpm ?? "",
                ManifestMinCompatibleServer = manifest?.MinCompatibleServer ?? "",
            };

            if (manifest == null)
            {
                status.Reason = "未提供发布清单。";
                return status;
            }

            if (IsSourceChannel(manifest.Channel) || IsSourceChannel(manifest.ServerVersion))
            {
                status.Reason = "开发/source 通道不提供自动管理 MCP 服务清单。";
                return status;
            }

            if (!string.IsNullOrWhiteSpace(manifest.ProtocolVersion) &&
                !string.Equals(manifest.ProtocolVersion, status.CurrentProtocolVersion, StringComparison.OrdinalIgnoreCase))
            {
                status.Reason = $"协议版本不匹配：manifest {manifest.ProtocolVersion} / current {status.CurrentProtocolVersion}";
                return status;
            }

            if (!string.IsNullOrWhiteSpace(status.CurrentUpmVersion) &&
                !IsVersionAtLeast(status.CurrentUpmVersion, manifest.MinCompatibleUpm))
            {
                status.Reason = $"UPM 版本过低：current {status.CurrentUpmVersion} < min {manifest.MinCompatibleUpm}";
                return status;
            }

            if (!string.IsNullOrWhiteSpace(status.CurrentServerVersion) &&
                !IsVersionAtLeast(status.CurrentServerVersion, manifest.MinCompatibleServer))
            {
                status.Reason = $"MCP Server 版本过低：current {status.CurrentServerVersion} < min {manifest.MinCompatibleServer}";
                return status;
            }

            status.IsCompatible = true;
            status.Reason = "release 清单兼容。";
            return status;
        }

        private async Task RunDownloadLatestServerExeAsync(
            bool activateOnComplete,
            UPilotReleaseManifest manifest,
            CancellationToken token)
        {
            try
            {
                await DownloadLatestServerExeAsync(manifest, activateOnComplete, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] MCP server exe background download failed: " + ex.Message + "\n" + ex);
                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.ErrorMessage = ex.Message;
                    state.Phase = "下载失败";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
            }
        }

        private async Task<UPilotPreparedServerDownload> DownloadLatestServerExeAsync(
            UPilotReleaseManifest manifest,
            bool activateOnComplete,
            CancellationToken token)
        {
            string activeTmpPath = null;
            var preserveVerifiedDownload = false;
            try
            {
                manifest ??= await FetchReleaseManifestAsync();
                var download = PickCurrentPlatformDownload(manifest);
                if (download == null)
                    throw new InvalidOperationException($"没有找到适用于 {CurrentPlatformDisplayName} 的服务。");

                UpdateState(state =>
                {
                    state.Version = manifest.ServerVersion;
                    state.DownloadUrl = download.Url;
                    state.Sha256 = download.Sha256;
                    state.TotalBytes = download.SizeBytes;
                    state.PlatformDisplayName = CurrentPlatformDisplayName;
                    state.Phase = "正在下载安装";
                });

                var versionDir = Path.Combine(
                    RuntimeCacheRoot,
                    "managed-servers",
                    SafePathSegment(GetProjectKey()),
                    SafePathSegment(manifest.ServerVersion));
                Directory.CreateDirectory(versionDir);
                var fileName = string.IsNullOrWhiteSpace(download.FileName)
                    ? BuildDefaultServerFileName(manifest.ServerVersion)
                    : download.FileName;
                var finalPath = Path.Combine(versionDir, fileName);
                var tmpPath = finalPath + ".download";
                activeTmpPath = tmpPath;
                var alreadyReady = IsVerifiedServerFileReady(finalPath, download.Sha256);
                var reusableDownload = !alreadyReady && IsVerifiedServerFileReady(tmpPath, download.Sha256);
                if (!alreadyReady)
                {
                    if (!reusableDownload)
                    {
                        await RetryFileOperationAsync(
                            () => CleanupDownloadFiles(tmpPath, ParallelDownloadSegments),
                            "清理旧的服务下载文件",
                            token,
                            updateProgress: false);

                        var totalBytes = download.SizeBytes;
                        EnsureSufficientDiskSpace(versionDir, totalBytes, includeWorkingCopy: true);
                        var supportsRanges = totalBytes >= ParallelDownloadThresholdBytes &&
                                             await SupportsRangeDownloadAsync(download.Url, totalBytes, token);
                        if (supportsRanges)
                            await DownloadInSegmentsAsync(download.Url, tmpPath, totalBytes, token);
                        else
                            await DownloadSingleStreamAsync(download.Url, tmpPath, totalBytes, token);

                        token.ThrowIfCancellationRequested();
                        UpdateState(state => state.Phase = "正在验证文件");
                        var actualSha = ComputeSha256(tmpPath);
                        if (!string.IsNullOrWhiteSpace(download.Sha256) &&
                            !string.Equals(actualSha, download.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            await RetryFileOperationAsync(
                                () => File.Delete(tmpPath),
                                "删除校验失败的服务文件",
                                CancellationToken.None,
                                throwOnFailure: false,
                                updateProgress: false);
                            throw new UPilotDownloadUserActionException(
                                $"下载的 MCP 服务文件未通过完整性校验，请重新执行更新。期望 {download.Sha256}，实际 {actualSha}");
                        }
                        preserveVerifiedDownload = true;
                    }
                    else
                    {
                        preserveVerifiedDownload = true;
                        UpdateState(state =>
                        {
                            state.BytesReceived = state.TotalBytes;
                            state.CompletedSegments = state.SegmentCount;
                            state.Phase = "正在恢复已下载文件";
                        });
                    }

                    await ReplaceVerifiedDownloadAsync(tmpPath, finalPath, download.Sha256, token);
                    preserveVerifiedDownload = false;
                }
                else
                {
                    UpdateState(state =>
                    {
                        state.BytesReceived = state.TotalBytes;
                        state.CompletedSegments = state.SegmentCount;
                        state.Phase = activateOnComplete ? "正在启用服务" : "服务文件已准备";
                    });
                }

                EnsureExecutablePermission(finalPath);
                if (activateOnComplete)
                    SetStandaloneExeRuntime(finalPath, manifest.ServerVersion);

                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.IsComplete = true;
                    state.Phase = activateOnComplete ? "安装完成" : "服务文件已准备";
                    state.TargetPath = finalPath;
                    state.BytesReceived = state.TotalBytes;
                    state.CompletedSegments = state.SegmentCount;
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });

                return new UPilotPreparedServerDownload
                {
                    Version = manifest.ServerVersion,
                    TargetPath = finalPath,
                    Sha256 = download.Sha256,
                    PlatformDisplayName = CurrentPlatformDisplayName,
                };
            }
            catch (OperationCanceledException)
            {
                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.IsCancelled = true;
                    state.Phase = "已取消";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
                throw;
            }
            catch (Exception ex)
            {
                Exception reportedException = ex;
                if (IsDiskFullException(ex))
                {
                    reportedException = new UPilotDownloadUserActionException(
                        "磁盘空间不足，MCP 服务下载或合并未完成。请释放磁盘空间后重新更新。",
                        ex);
                }
                else if (ex is UnauthorizedAccessException)
                {
                    reportedException = new UPilotDownloadUserActionException(
                        "没有权限写入 MCP 服务目录。请检查目录权限或安全软件设置后重新更新。",
                        ex);
                }
                else if (ex is DirectoryNotFoundException || ex is PathTooLongException || ex is NotSupportedException)
                {
                    reportedException = new UPilotDownloadUserActionException(
                        "MCP 服务目标路径不可用。请检查路径和磁盘状态后重新更新。",
                        ex);
                }
                Debug.LogError("[UPilot] MCP server exe download failed: " + reportedException.Message +
                               "\n" + reportedException);
                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.ErrorMessage = reportedException.Message;
                    state.Phase = "下载失败";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
                if (reportedException is UPilotDownloadUserActionException)
                    ScheduleDownloadErrorDialog(reportedException.Message);
                throw reportedException;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(activeTmpPath))
                {
                    if (preserveVerifiedDownload && File.Exists(activeTmpPath))
                    {
                        Debug.LogWarning("[UPilot] 已保留通过 SHA256 校验的下载文件，后续更新可直接复用。" +
                                         "\nPath=" + activeTmpPath);
                        await RetryFileOperationAsync(
                            () => CleanupSegmentFiles(activeTmpPath, ParallelDownloadSegments),
                            "清理服务下载分片文件",
                            CancellationToken.None,
                            throwOnFailure: false,
                            updateProgress: false);
                    }
                    else
                    {
                        await RetryFileOperationAsync(
                            () => CleanupDownloadFiles(activeTmpPath, ParallelDownloadSegments),
                            "清理服务下载临时文件",
                            CancellationToken.None,
                            throwOnFailure: false,
                            updateProgress: false);
                    }
                }
            }
        }

        private static bool IsVerifiedServerFileReady(string path, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            if (string.IsNullOrWhiteSpace(expectedSha256))
                return true;
            try
            {
                var actual = ComputeSha256(path);
                return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LogRuntimeProbeErrorOnce(ref _verifiedServerHashErrorLogged, "校验已下载 MCP 服务文件失败", ex);
                return false;
            }
        }

        private async Task DownloadSingleStreamAsync(
            string url,
            string targetPath,
            long expectedBytes,
            CancellationToken token)
        {
            UpdateState(state =>
            {
                state.BytesReceived = 0;
                state.TotalBytes = expectedBytes;
                state.SegmentCount = 1;
                state.CompletedSegments = 0;
            });

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedBytes;
            UpdateState(state => state.TotalBytes = total);
            using var input = await response.Content.ReadAsStreamAsync();
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyDownloadStreamAsync(input, output, token);
                output.Flush();
            }
            UpdateState(state => state.CompletedSegments = 1);
        }

        private async Task DownloadInSegmentsAsync(
            string url,
            string targetPath,
            long totalBytes,
            CancellationToken token)
        {
            UpdateState(state =>
            {
                state.BytesReceived = 0;
                state.TotalBytes = totalBytes;
                state.SegmentCount = ParallelDownloadSegments;
                state.CompletedSegments = 0;
            });

            var tasks = new List<Task>(ParallelDownloadSegments);
            var segmentSize = totalBytes / ParallelDownloadSegments;
            for (var index = 0; index < ParallelDownloadSegments; index++)
            {
                var start = index * segmentSize;
                var end = index == ParallelDownloadSegments - 1
                    ? totalBytes - 1
                    : start + segmentSize - 1;
                var segmentPath = targetPath + ".part" + index;
                tasks.Add(DownloadSegmentWithRetryAsync(url, segmentPath, start, end, token));
            }

            try
            {
                await Task.WhenAll(tasks);
                using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    for (var index = 0; index < ParallelDownloadSegments; index++)
                    {
                        var segmentPath = targetPath + ".part" + index;
                        using (var input = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            await input.CopyToAsync(output, 128 * 1024, token);
                    }
                    output.Flush();
                }
            }
            finally
            {
                CleanupSegmentFiles(targetPath, ParallelDownloadSegments);
            }
        }

        private async Task DownloadSegmentWithRetryAsync(
            string url,
            string segmentPath,
            long start,
            long end,
            CancellationToken token)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt <= SegmentRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(segmentPath))
                        File.Delete(segmentPath);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new InvalidOperationException("下载源未返回分片内容。");

                    using var input = await response.Content.ReadAsStreamAsync();
                    using var output = new FileStream(segmentPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await CopyDownloadStreamAsync(input, output, token);
                    UpdateState(state => state.CompletedSegments++);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (File.Exists(segmentPath))
                    {
                        var partialBytes = new FileInfo(segmentPath).Length;
                        UpdateState(state => state.BytesReceived = Math.Max(0, state.BytesReceived - partialBytes));
                        File.Delete(segmentPath);
                    }
                    lastError = ex;
                    if (attempt < SegmentRetryCount)
                        await Task.Delay(350 * (attempt + 1), token);
                }
            }

            throw new InvalidOperationException("服务分片下载失败。", lastError);
        }

        private async Task CopyDownloadStreamAsync(Stream input, Stream output, CancellationToken token)
        {
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, token);
                if (read <= 0)
                    break;
                await output.WriteAsync(buffer, 0, read, token);
                UpdateState(state => state.BytesReceived += read);
            }
        }

        private static async Task<bool> SupportsRangeDownloadAsync(
            string url,
            long expectedBytes,
            CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var rangeLength = response.Content.Headers.ContentRange?.Length ?? expectedBytes;
                return response.StatusCode == HttpStatusCode.PartialContent && rangeLength > 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupDownloadFiles(string targetPath, int segmentCount)
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            CleanupSegmentFiles(targetPath, segmentCount);
        }

        private static void CleanupSegmentFiles(string targetPath, int segmentCount)
        {
            for (var index = 0; index < segmentCount; index++)
            {
                var segmentPath = targetPath + ".part" + index;
                if (File.Exists(segmentPath))
                    File.Delete(segmentPath);
            }
        }

        private static async Task<bool> CleanupOrQuarantineFailedTargetAsync(
            string targetPath,
            CancellationToken token)
        {
            if (!File.Exists(targetPath))
                return true;

            try
            {
                File.Delete(targetPath);
                Debug.LogWarning("[UPilot] 已删除不完整的 MCP 服务目标文件。\nTarget=" + targetPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogFileOperationFailure("Delete failed target", 0, ex, null, targetPath, IsDiskFullException(ex));
            }

            token.ThrowIfCancellationRequested();
            var quarantinePath = targetPath + ".failed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try
            {
                File.Move(targetPath, quarantinePath);
                Debug.LogWarning("[UPilot] 无法删除不完整目标文件，已将其隔离。" +
                                 $"\nTarget={targetPath}\nQuarantine={quarantinePath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogFileOperationFailure("Quarantine failed target", 0, ex, targetPath, quarantinePath, false);
                Debug.LogError("[UPilot] 删除和隔离不完整目标文件均失败，已停止更新。" +
                               $"\nTarget={targetPath}\nException={ex}");
                return false;
            }
        }

        private static void EnsureSufficientDiskSpace(
            string directoryPath,
            long expectedBytes,
            bool includeWorkingCopy)
        {
            if (expectedBytes <= 0 || string.IsNullOrWhiteSpace(directoryPath))
                return;

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
                if (string.IsNullOrWhiteSpace(root))
                    return;

                var drive = new DriveInfo(root);
                var multiplier = includeWorkingCopy ? 2L : 1L;
                var payloadBytes = expectedBytes > (long.MaxValue - DiskSpaceSafetyMarginBytes) / multiplier
                    ? long.MaxValue
                    : expectedBytes * multiplier;
                var requiredBytes = payloadBytes == long.MaxValue
                    ? long.MaxValue
                    : payloadBytes + DiskSpaceSafetyMarginBytes;
                if (drive.AvailableFreeSpace >= requiredBytes)
                    return;

                var message =
                    $"磁盘空间不足，MCP 服务更新至少需要 {FormatFileSize(requiredBytes)} 可用空间，" +
                    $"当前仅剩 {FormatFileSize(drive.AvailableFreeSpace)}。请释放磁盘空间后重新更新。";
                Debug.LogError("[UPilot] " + message + $"\nDirectory={directoryPath}");
                throw new UPilotDownloadUserActionException(message);
            }
            catch (UPilotDownloadUserActionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UPilot] 无法预检查 MCP 服务下载磁盘空间，将继续并依赖写入错误检测。" +
                                 $"\nDirectory={directoryPath}\nException={ex}");
            }
        }

        private static bool IsDiskFullException(Exception ex)
        {
            if (!(ex is IOException))
                return false;
            var errorCode = ex.HResult & 0xFFFF;
            return errorCode == 0x27 || errorCode == 0x70;
        }

        private static void LogFileOperationFailure(
            string operation,
            int attempt,
            Exception ex,
            string sourcePath,
            string targetPath,
            bool hardError)
        {
            var message =
                $"[UPilot] 文件操作失败。\nOperation={operation}" +
                $"\nAttempt={attempt + 1}/{FileOperationRetryCount + 1}" +
                (string.IsNullOrWhiteSpace(sourcePath) ? "" : "\nSource=" + sourcePath) +
                (string.IsNullOrWhiteSpace(targetPath) ? "" : "\nTarget=" + targetPath) +
                $"\nException={ex.GetType().Name}" +
                $"\nHResult=0x{ex.HResult:X8}" +
                $"\nMessage={ex.Message}";
            if (hardError)
                Debug.LogError(message + "\n" + ex);
            else
                Debug.LogWarning(message);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024)
                return (bytes / (1024d * 1024)).ToString("0.##") + " MB";
            return (bytes / 1024d).ToString("0.##") + " KB";
        }

        private static void ScheduleDownloadErrorDialog(string message)
        {
            if (_downloadErrorDialogPending)
                return;

            _downloadErrorDialogPending = true;
            EditorApplication.delayCall += () =>
            {
                _downloadErrorDialogPending = false;
                EditorUtility.DisplayDialog(
                    "UPilot 更新失败",
                    message + "\n\n请检查磁盘空间、目录权限和文件占用情况后重新更新。",
                    "确定");
            };
        }

        private static void EnsureExecutablePermission(string path)
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            try
            {
                RunProcess("chmod", $"+x \"{path}\"", 5000);
            }
            catch (Exception ex)
            {
                LogRuntimeProbeErrorOnce(ref _chmodErrorLogged, "设置 MCP 服务可执行权限失败", ex);
            }
#endif
        }

        private static async Task<bool> ReplaceVerifiedDownloadAsync(
            string verifiedPath,
            string finalPath,
            string expectedSha256,
            CancellationToken token = default)
        {
            var moved = await RetryFileOperationAsync(
                () => ReplaceVerifiedDownload(verifiedPath, finalPath),
                "移动已验证的 MCP 服务文件",
                token,
                throwOnFailure: false,
                sourcePath: verifiedPath,
                targetPath: finalPath);
            if (moved)
                return true;

            const string fallbackWarning = "服务文件持续被系统占用，正在使用兼容复制方式完成更新。";
            Instance.UpdateState(state =>
            {
                state.Phase = "正在使用兼容复制方式";
                state.WarningMessage = fallbackWarning;
            });
            Debug.LogWarning("[UPilot] Move 重试已耗尽，正在使用兼容复制方式完成更新。" +
                             $"\nSource={verifiedPath}\nTarget={finalPath}");

            await CopyVerifiedDownloadWithRetryAsync(verifiedPath, finalPath, expectedSha256, token);
            Instance.UpdateState(state =>
            {
                state.Phase = "兼容复制已完成";
                state.WarningMessage = "服务文件已通过兼容复制方式安装，并完成完整性校验。";
            });
            return true;
        }

        private static async Task CopyVerifiedDownloadWithRetryAsync(
            string verifiedPath,
            string finalPath,
            string expectedSha256,
            CancellationToken token = default)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt <= FileOperationRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(finalPath) &&
                        !await CleanupOrQuarantineFailedTargetAsync(finalPath, token))
                    {
                        throw new UPilotDownloadUserActionException(
                            "无法删除或隔离不完整的 MCP 服务文件。请关闭占用该文件的程序后重新更新。");
                    }

                    EnsureSufficientDiskSpace(
                        Path.GetDirectoryName(finalPath),
                        new FileInfo(verifiedPath).Length,
                        includeWorkingCopy: false);
                    Instance.UpdateState(state => state.Phase = "正在复制服务文件");
                    File.Copy(verifiedPath, finalPath, overwrite: false);

                    Instance.UpdateState(state => state.Phase = "正在校验复制文件");
                    var actualSha256 = await ComputeSha256WithRetryAsync(finalPath, token);
                    if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                        !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"复制后的文件 SHA256 不一致：期望 {expectedSha256}，实际 {actualSha256}");
                    }

                    Debug.LogWarning("[UPilot] MCP 服务文件已通过兼容复制方式安装并完成 SHA256 校验。" +
                                     $"\nSource={verifiedPath}\nTarget={finalPath}");
                    return;
                }
                catch (UPilotDownloadUserActionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    var hardError = IsDiskFullException(ex);
                    LogFileOperationFailure("Copy", attempt, ex, verifiedPath, finalPath, hardError);
                    if (File.Exists(finalPath) &&
                        !await CleanupOrQuarantineFailedTargetAsync(finalPath, token))
                    {
                        throw new UPilotDownloadUserActionException(
                            "复制失败后无法删除或隔离不完整的 MCP 服务文件。请关闭占用该文件的程序后重新更新。",
                            ex);
                    }

                    if (hardError)
                    {
                        throw new UPilotDownloadUserActionException(
                            "磁盘空间不足，无法复制 MCP 服务文件。请释放磁盘空间后重新更新。",
                            ex);
                    }

                    if (attempt >= FileOperationRetryCount)
                        break;

                    var delayMs = FileOperationRetryDelayMs * (attempt + 1);
                    var retryNumber = attempt + 1;
                    Instance.UpdateState(state =>
                        state.Phase = $"复制失败，{delayMs / 1000} 秒后重试（{retryNumber}/{FileOperationRetryCount}）");
                    Debug.LogWarning(
                        $"[UPilot] Copy 失败，已清理或隔离不完整目标文件，{delayMs / 1000} 秒后进行第 {retryNumber}/{FileOperationRetryCount} 次重试。" +
                        $"\nSource={verifiedPath}\nTarget={finalPath}");
                    await Task.Delay(delayMs, token);
                }
            }

            throw new UPilotDownloadUserActionException(
                "兼容复制重试仍然失败。请检查磁盘空间、目录权限及文件占用情况后重新更新。",
                lastError);
        }

        private static async Task<string> ComputeSha256WithRetryAsync(
            string path,
            CancellationToken token)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt <= FileOperationRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return ComputeSha256(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    LogFileOperationFailure("Verify copied file", attempt, ex, path, null, false);
                    if (attempt >= FileOperationRetryCount)
                        break;

                    var delayMs = FileOperationRetryDelayMs * (attempt + 1);
                    var retryNumber = attempt + 1;
                    Instance.UpdateState(state =>
                        state.Phase = $"校验文件暂时被占用，{delayMs / 1000} 秒后重试（{retryNumber}/{FileOperationRetryCount}）");
                    Debug.LogWarning(
                        $"[UPilot] 复制文件校验暂时失败，{delayMs / 1000} 秒后进行第 {retryNumber}/{FileOperationRetryCount} 次重试。" +
                        $"\nPath={path}");
                    await Task.Delay(delayMs, token);
                }
            }

            throw new UPilotDownloadUserActionException(
                "无法读取复制后的 MCP 服务文件进行完整性校验。请关闭占用该文件的程序后重新更新。",
                lastError);
        }

        private static async Task<bool> RetryFileOperationAsync(
            Action operation,
            string description,
            CancellationToken token,
            bool throwOnFailure = true,
            string sourcePath = null,
            string targetPath = null,
            bool updateProgress = true)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt <= FileOperationRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    operation();
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    var hardError = IsDiskFullException(ex);
                    LogFileOperationFailure(description, attempt, ex, sourcePath, targetPath, hardError);
                    if (hardError)
                    {
                        throw new UPilotDownloadUserActionException(
                            "磁盘空间不足，无法完成 MCP 服务更新。请释放磁盘空间后重新更新。",
                            ex);
                    }

                    if (attempt < FileOperationRetryCount)
                    {
                        var delayMs = FileOperationRetryDelayMs * (attempt + 1);
                        var retryNumber = attempt + 1;
                        if (updateProgress)
                        {
                            Instance.UpdateState(state =>
                                state.Phase = $"{description}失败，{delayMs / 1000} 秒后重试（{retryNumber}/{FileOperationRetryCount}）");
                        }
                        Debug.LogWarning(
                            $"[UPilot] {description}失败，{delayMs / 1000} 秒后进行第 {retryNumber}/{FileOperationRetryCount} 次重试。");
                        await Task.Delay(delayMs, token);
                        continue;
                    }
                }
            }

            if (throwOnFailure)
                throw new UPilotDownloadUserActionException(description + "失败：" + lastError?.Message, lastError);

            Debug.LogWarning("[UPilot] " + description + "重试已耗尽：" + lastError?.Message);
            return false;
        }

        private static void ReplaceVerifiedDownload(string verifiedPath, string finalPath)
        {
            if (!File.Exists(finalPath))
            {
                File.Move(verifiedPath, finalPath);
                return;
            }

            var backupPath = finalPath + ".backup";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(finalPath, backupPath);
            try
            {
                File.Move(verifiedPath, finalPath);
                File.Delete(backupPath);
            }
            catch
            {
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                if (File.Exists(backupPath))
                    File.Move(backupPath, finalPath);
                throw;
            }
        }

        public static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string ReadJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : "";
        }

        private static UPilotReleaseManifest ParseManifest(string json)
        {
            var manifest = new UPilotReleaseManifest
            {
                UpmVersion = ReadJsonString(json, "upmVersion"),
                ServerVersion = ReadJsonString(json, "serverVersion"),
                ProtocolVersion = ReadJsonString(json, "protocolVersion"),
                Channel = ReadJsonString(json, "channel"),
                CommitSha = ReadJsonString(json, "commitSha"),
                MinCompatibleUpm = ReadJsonString(json, "minCompatibleUpm"),
                MinCompatibleServer = ReadJsonString(json, "minCompatibleServer"),
            };

            var downloadMatches = Regex.Matches(json, "\\{[^\\{\\}]*\"url\"\\s*:\\s*\"[^\"]+\"[^\\{\\}]*\\}");
            foreach (Match match in downloadMatches)
            {
                var block = match.Value;
                var info = new UPilotServerDownloadInfo
                {
                    Platform = ReadJsonString(block, "platform"),
                    Architecture = ReadJsonString(block, "architecture"),
                    Url = ReadJsonString(block, "url"),
                    Sha256 = ReadJsonString(block, "sha256"),
                    FileName = ReadJsonString(block, "fileName"),
                };
                var sizeMatch = Regex.Match(block, "\"sizeBytes\"\\s*:\\s*(\\d+)");
                if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out var size))
                    info.SizeBytes = size;
                if (!string.IsNullOrWhiteSpace(info.Url))
                    manifest.Downloads.Add(info);
            }

            return manifest;
        }

        private static UPilotServerDownloadInfo PickCurrentPlatformDownload(UPilotReleaseManifest manifest)
        {
            return PickDownloadForPlatform(
                manifest,
                GetCurrentPlatformKey(),
                GetCurrentArchitectureKey());
        }

        private static UPilotServerDownloadInfo PickDownloadForPlatform(
            UPilotReleaseManifest manifest,
            string platform,
            string architecture)
        {
            if (manifest == null)
                return null;
            UPilotServerDownloadInfo best = null;
            var bestScore = -1;
            foreach (var item in manifest.Downloads)
            {
                if (PlatformMatches(item.Platform, platform) &&
                    ArchitectureMatches(item.Architecture, architecture))
                {
                    var score = (string.IsNullOrWhiteSpace(item.Platform) ? 0 : 2) +
                                (string.IsNullOrWhiteSpace(item.Architecture) ? 0 : 2);
                    if (score > bestScore)
                    {
                        best = item;
                        bestScore = score;
                    }
                }
            }

            return best;
        }

        private static string GetCurrentPlatformKey()
        {
#if UNITY_EDITOR_WIN
            return "windows";
#elif UNITY_EDITOR_OSX
            return "macos";
#elif UNITY_EDITOR_LINUX
            return "linux";
#else
            return "unknown";
#endif
        }

        private static string GetCurrentArchitectureKey()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.Arm64:
                    return "arm64";
                case Architecture.X86:
                    return "x86";
                case Architecture.Arm:
                    return "arm";
                default:
                    return "x64";
            }
        }

        private static bool PlatformMatches(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return true;
            candidate = candidate.ToLowerInvariant();
            if (current == "windows") return candidate.Contains("win");
            if (current == "macos") return candidate.Contains("mac") || candidate.Contains("osx") || candidate.Contains("darwin");
            if (current == "linux") return candidate.Contains("linux");
            return candidate.Contains(current);
        }

        private static bool ArchitectureMatches(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return true;
            candidate = candidate.ToLowerInvariant().Replace("-", "").Replace("_", "");
            if (current == "x64") return candidate.Contains("x64") || candidate.Contains("amd64") || candidate.Contains("win64");
            if (current == "arm64") return candidate.Contains("arm64") || candidate.Contains("aarch64") || candidate.Contains("applesilicon");
            return candidate.Contains(current);
        }

        private static string BuildPlatformDisplayName(string platform, string architecture)
        {
            var platformName = platform == "windows" ? "Windows" : platform == "macos" ? "macOS" : platform == "linux" ? "Linux" : "当前平台";
            var architectureName = architecture == "arm64" && platform == "macos"
                ? "Apple Silicon"
                : architecture == "x64" ? "x64" : architecture.ToUpperInvariant();
            return platformName + " " + architectureName;
        }

        private static string BuildDefaultServerFileName(string version)
        {
            var platform = GetCurrentPlatformKey();
            var architecture = GetCurrentArchitectureKey();
            var extension = platform == "windows" ? ".exe" : "";
            return $"upilot-mcp-server-{version}-{platform}-{architecture}{extension}";
        }

        private static UPilotPythonProbeResult ProbePythonCandidate(string python)
        {
            var result = new UPilotPythonProbeResult { PythonPath = python };
            var versionOutput = RunProcess(python, "--version", 3000).Trim();
            result.VersionText = versionOutput;
            var match = Regex.Match(versionOutput, @"Python\s+(\d+)\.(\d+)");
            int major;
            int minor;
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out major) ||
                !int.TryParse(match.Groups[2].Value, out minor))
            {
                result.Message = $"无法识别 Python 版本：{versionOutput}";
                return result;
            }
            result.Major = major;
            result.Minor = minor;

            if (result.Major < 3 || result.Major == 3 && result.Minor < 11)
            {
                result.Message = $"Python 版本过低：{versionOutput}";
                return result;
            }
            result.InterpreterUsable = true;

            const string script = "import importlib.util; mods=['mcp','websockets','yaml','PIL']; print('\\n'.join(f'{m}:{importlib.util.find_spec(m) is not None}' for m in mods))";
            var depsOutput = RunProcess(python, "-c \"" + script.Replace("\"", "\\\"") + "\"", 5000);
            var allDeps = true;
            foreach (var raw in depsOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Split(':');
                if (parts.Length != 2)
                    continue;
                var ok = string.Equals(parts[1].Trim(), "True", StringComparison.OrdinalIgnoreCase);
                result.Dependencies[parts[0].Trim()] = ok;
                if (!ok) allDeps = false;
            }

            result.DependenciesComplete = allDeps;
            result.IsUsable = result.InterpreterUsable && result.DependenciesComplete;
            result.Message = allDeps
                ? "Python 环境可用。"
                : "Python 3.11+ 可用，但 MCP server 依赖不完整，将自动创建 venv 并安装依赖。";
            return result;
        }

        private static IEnumerable<string> FindExecutables(string name)
        {
            var result = new List<string>();
            try
            {
                var output = RunProcess("where", name, 2000);
                foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        result.Add(path);
                }
            }
            catch { }
            return result;
        }

        private sealed class ProcessRunResult
        {
            public int ExitCode;
            public string Output = "";
            public bool TimedOut;
        }

        private static ProcessRunResult RunProcessWithResult(string fileName, string arguments, int timeoutMs, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return new ProcessRunResult { ExitCode = -1 };
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return new ProcessRunResult { ExitCode = -1, TimedOut = true };
            }
            token.ThrowIfCancellationRequested();
            var text = (proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd()).Trim();
            return new ProcessRunResult { ExitCode = proc.ExitCode, Output = text };
        }

        private static string RunProcess(string fileName, string arguments, int timeoutMs)
        {
            return RunProcessWithResult(fileName, arguments, timeoutMs, CancellationToken.None).Output;
        }

        private void UpdateState(Action<UPilotDownloadState> update)
        {
            lock (_stateLock)
            {
                update(_downloadState);
            }
        }

        private void UpdatePythonEnvState(Action<UPilotPythonEnvironmentState> update)
        {
            lock (_pythonEnvLock)
            {
                update(_pythonEnvState);
            }
        }

        private static UPilotDownloadState CopyState(UPilotDownloadState source)
        {
            return new UPilotDownloadState
            {
                IsRunning = source.IsRunning,
                IsComplete = source.IsComplete,
                IsCancelled = source.IsCancelled,
                Phase = source.Phase,
                WarningMessage = source.WarningMessage,
                ErrorMessage = source.ErrorMessage,
                Version = source.Version,
                DownloadUrl = source.DownloadUrl,
                Sha256 = source.Sha256,
                TargetPath = source.TargetPath,
                PlatformDisplayName = source.PlatformDisplayName,
                BytesReceived = source.BytesReceived,
                TotalBytes = source.TotalBytes,
                SegmentCount = source.SegmentCount,
                CompletedSegments = source.CompletedSegments,
                StartedAt = source.StartedAt,
                FinishedAt = source.FinishedAt,
            };
        }

        private static UPilotPythonEnvironmentState CopyState(UPilotPythonEnvironmentState source)
        {
            return new UPilotPythonEnvironmentState
            {
                IsRunning = source.IsRunning,
                IsComplete = source.IsComplete,
                IsCancelled = source.IsCancelled,
                Phase = source.Phase,
                ErrorMessage = source.ErrorMessage,
                PythonPath = source.PythonPath,
                VenvPath = source.VenvPath,
                InterpreterPath = source.InterpreterPath,
                StartedAt = source.StartedAt,
                FinishedAt = source.FinishedAt,
            };
        }

        private async Task ConfigurePythonEnvironmentAsync(CancellationToken token)
        {
            try
            {
                var probe = ProbePython();
                var python = probe.PythonPath;
                if (!probe.InterpreterUsable || string.IsNullOrWhiteSpace(python) || !File.Exists(python))
                    throw new InvalidOperationException("未找到可用的 Python 解释器。");

                var venvRoot = Path.Combine(RuntimeCacheRoot, "python-envs", SafePathSegment(GetProjectKey()));
                var venvPath = Path.Combine(venvRoot, "venv");
                var interpreterPath = GetVenvPythonPath(venvPath);
                Directory.CreateDirectory(venvRoot);

                UpdatePythonEnvState(state =>
                {
                    state.PythonPath = python;
                    state.VenvPath = venvPath;
                    state.InterpreterPath = interpreterPath;
                    state.Phase = "创建虚拟环境";
                });

                await Task.Run(() => RunProcessChecked(python, $"-m venv \"{venvPath}\"", 120000, token), token);
                token.ThrowIfCancellationRequested();

                UpdatePythonEnvState(state => state.Phase = "升级 pip 与构建工具");
                await Task.Run(() => RunProcessChecked(interpreterPath, "-m pip install --upgrade pip setuptools wheel", 180000, token), token);
                token.ThrowIfCancellationRequested();

                UpdatePythonEnvState(state => state.Phase = "安装 MCP server 依赖");
                var requirements = GetRequirementsPath();
                if (File.Exists(requirements))
                    await Task.Run(() => RunProcessChecked(interpreterPath, $"-m pip install -r \"{requirements}\"", 300000, token), token);

                SetPythonRuntime(interpreterPath);
                UpdatePythonEnvState(state =>
                {
                    state.IsRunning = false;
                    state.IsComplete = true;
                    state.Phase = "环境已配置";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
            }
            catch (OperationCanceledException)
            {
                UpdatePythonEnvState(state =>
                {
                    state.IsRunning = false;
                    state.IsCancelled = true;
                    state.Phase = "已取消";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilot] Python environment setup failed: {ex.Message}");
                UpdatePythonEnvState(state =>
                {
                    state.IsRunning = false;
                    state.ErrorMessage = ex.Message;
                    state.Phase = "配置失败";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
            }
        }

        private static string GetVenvPythonPath(string venvPath)
        {
            return Path.Combine(venvPath, "Scripts", "python.exe");
        }

        private static string GetRequirementsPath()
        {
            return Path.Combine(GetPackageRoot(), "upilotserver~", "requirements.txt");
        }

        private static string GetProjectKey()
        {
            try
            {
                var root = UPilotProjectConfig.ProjectRoot.Replace('\\', '/').TrimEnd('/');
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(root));
                var sb = new StringBuilder(24);
                for (var i = 0; i < 12 && i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LogRuntimeProbeErrorOnce(ref _projectKeyErrorLogged, "计算当前项目更新缓存 key 失败", ex);
                return "default";
            }
        }

        private static void RunProcessChecked(string fileName, string arguments, int timeoutMs, CancellationToken token)
        {
            var result = RunProcessWithResult(fileName, arguments, timeoutMs, token);
            if (result.TimedOut)
                throw new TimeoutException($"命令超时：{fileName} {arguments}");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Output) ? $"命令失败：{fileName}" : result.Output);
        }

        private static string SafePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }

        private static bool IsMainChannel(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsSourceUpdateChannel()
        {
            return IsSourceChannel(ResolveUpdateChannel());
        }

        public static bool IsSourceChannel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var normalized = value.Trim();
            return string.Equals(normalized, "source", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "python", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "dev", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "development", StringComparison.OrdinalIgnoreCase) ||
                   IsMainChannel(normalized);
        }

        private static bool IsReleaseChannel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            return string.Equals(value, "release", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStrictSemver(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Regex.IsMatch(value.Trim(), @"^v?\d+\.\d+\.\d+$");
        }

        private static bool CanFallbackToLegacyManifest(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   url.EndsWith("/" + ManifestFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToLegacyManifestUrl(string url)
        {
            return url.Substring(0, url.Length - ManifestFileName.Length) + LegacyManifestFileName;
        }

        public static bool IsVersionAtLeast(string current, string minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum))
                return true;
            if (string.IsNullOrWhiteSpace(current))
                return false;
            if (!TryParseVersionParts(current, out _, out _, out _) ||
                !TryParseVersionParts(minimum, out _, out _, out _))
                return false;
            return CompareVersions(current, minimum) >= 0;
        }

        public static bool IsVersionNewer(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            if (string.IsNullOrWhiteSpace(current))
                return true;
            if (!TryParseVersionParts(candidate, out _, out _, out _))
                return false;
            if (!TryParseVersionParts(current, out _, out _, out _))
                return true;
            return CompareVersions(candidate, current) > 0;
        }

        public static int CompareVersions(string left, string right)
        {
            if (!TryParseVersionParts(left, out var leftMajor, out var leftMinor, out var leftPatch))
                return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
            if (!TryParseVersionParts(right, out var rightMajor, out var rightMinor, out var rightPatch))
                return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);

            var result = leftMajor.CompareTo(rightMajor);
            if (result != 0) return result;
            result = leftMinor.CompareTo(rightMinor);
            if (result != 0) return result;
            result = leftPatch.CompareTo(rightPatch);
            if (result != 0) return result;

            var leftIsMain = TryParseMainBuild(left, out var leftMainBuild);
            var rightIsMain = TryParseMainBuild(right, out var rightMainBuild);
            if (leftIsMain && rightIsMain)
                return leftMainBuild.CompareTo(rightMainBuild);
            if (leftIsMain != rightIsMain)
                return leftIsMain ? -1 : 1;
            return 0;
        }

        private static bool TryParseMainBuild(string value, out int build)
        {
            build = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var match = Regex.Match(value, @"-main\.(\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out build);
        }

        private static bool TryParseVersionParts(string value, out int major, out int minor, out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var match = Regex.Match(value, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!match.Success)
                return false;
            if (!int.TryParse(match.Groups[1].Value, out major))
                return false;
            if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out minor))
                return false;
            if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out patch))
                return false;
            return true;
        }
    }
}
