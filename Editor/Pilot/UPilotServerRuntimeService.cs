// -----------------------------------------------------------------------
// UPilot Editor - MCP server runtime discovery, download, and version info.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        public string ErrorMessage = "";
        public string Version = "";
        public string DownloadUrl = "";
        public string Sha256 = "";
        public string TargetPath = "";
        public long BytesReceived;
        public long TotalBytes;
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
        private const string MainManifestUrl = "https://github.com/codingriver/upilot/releases/download/main-nightly/manifest.json";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
                var mode = UPilotProjectConfig.Current.runtime?.mode ?? "python";
                return string.Equals(mode, "exe", StringComparison.OrdinalIgnoreCase)
                    ? "Standalone exe"
                    : "Python";
            }
        }

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
            UPilotProjectConfig.Save(config);
        }

        public void SetStandaloneExeRuntime(string exePath, string serverVersion = "")
        {
            var config = UPilotProjectConfig.Current;
            config.runtime ??= new UPilotRuntimeConfig();
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
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                    continue;

                var result = ProbePythonCandidate(candidate);
                if (result.IsUsable)
                    return result;
            }

            return new UPilotPythonProbeResult
            {
                IsUsable = false,
                Message = "未找到满足 Python 3.11+ 且依赖完整的环境。",
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

            var channel = ResolveUpdateChannel();
            return IsMainChannel(channel) ? MainManifestUrl : ReleaseManifestUrl;
        }

        public static string ResolveUpdateChannel()
        {
            var updates = UPilotProjectConfig.Current.updates ?? new UPilotUpdateConfig();
            var configured = (updates.channel ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(configured) &&
                !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return IsReleaseChannel(configured) ? "release" : configured;
            }

            return InferDefaultUpdateChannel();
        }

        private static string InferDefaultUpdateChannel()
        {
            if (IsLocalOrMainPackageInstall())
                return "main";

            var version = UpmVersion;
            if (IsMainChannel(version) || version.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0)
                return "main";

            return IsStrictSemver(version) ? "release" : "main";
        }

        private static bool IsLocalOrMainPackageInstall()
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
            lock (_stateLock)
            {
                if (_downloadState.IsRunning)
                    return;
                _downloadState = new UPilotDownloadState
                {
                    IsRunning = true,
                    Phase = "读取发布清单",
                    StartedAt = EditorApplication.timeSinceStartup,
                };
            }

            _downloadCts = new CancellationTokenSource();
            _ = DownloadLatestServerExeAsync(_downloadCts.Token);
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

            if (IsMainChannel(manifest.Channel) || IsMainChannel(manifest.ServerVersion))
            {
                if (!string.IsNullOrWhiteSpace(manifest.ProtocolVersion) &&
                    !string.Equals(manifest.ProtocolVersion, status.CurrentProtocolVersion, StringComparison.OrdinalIgnoreCase))
                {
                    status.Reason = $"main 分支协议版本不匹配：manifest {manifest.ProtocolVersion} / current {status.CurrentProtocolVersion}";
                    return status;
                }

                status.IsCompatible = true;
                status.Reason = "main 分支按协议兼容。";
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

        private async Task DownloadLatestServerExeAsync(CancellationToken token)
        {
            try
            {
                var manifest = await FetchReleaseManifestAsync();
                var download = PickWindowsDownload(manifest);
                if (download == null)
                    throw new InvalidOperationException("发布清单中没有 Windows x64 server exe。");

                UpdateState(state =>
                {
                    state.Version = manifest.ServerVersion;
                    state.DownloadUrl = download.Url;
                    state.Sha256 = download.Sha256;
                    state.TotalBytes = download.SizeBytes;
                    state.Phase = "下载 MCP Server";
                });

                var versionDir = Path.Combine(RuntimeCacheRoot, SafePathSegment(manifest.ServerVersion));
                Directory.CreateDirectory(versionDir);
                var fileName = string.IsNullOrWhiteSpace(download.FileName)
                    ? $"upilot-mcp-server-{manifest.ServerVersion}-win-x64.exe"
                    : download.FileName;
                var finalPath = Path.Combine(versionDir, fileName);
                var tmpPath = finalPath + ".download";
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);

                using (var response = await Http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? download.SizeBytes;
                    UpdateState(state => state.TotalBytes = total);
                    using var input = await response.Content.ReadAsStreamAsync();
                    using var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
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

                token.ThrowIfCancellationRequested();
                UpdateState(state => state.Phase = "校验 SHA256");
                var actualSha = ComputeSha256(tmpPath);
                if (!string.IsNullOrWhiteSpace(download.Sha256) &&
                    !string.Equals(actualSha, download.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmpPath);
                    throw new InvalidOperationException($"SHA256 校验失败：期望 {download.Sha256}，实际 {actualSha}");
                }

                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
                SetStandaloneExeRuntime(finalPath, manifest.ServerVersion);

                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.IsComplete = true;
                    state.Phase = "安装完成";
                    state.TargetPath = finalPath;
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
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
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilot] MCP server exe download failed: {ex.Message}");
                UpdateState(state =>
                {
                    state.IsRunning = false;
                    state.ErrorMessage = ex.Message;
                    state.Phase = "下载失败";
                    state.FinishedAt = EditorApplication.timeSinceStartup;
                });
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

        private static UPilotServerDownloadInfo PickWindowsDownload(UPilotReleaseManifest manifest)
        {
            foreach (var item in manifest.Downloads)
            {
                if (item.Url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(item.Platform) || item.Platform.IndexOf("win", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (string.IsNullOrEmpty(item.Architecture) || item.Architecture.IndexOf("64", StringComparison.OrdinalIgnoreCase) >= 0))
                    return item;
            }

            return null;
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

            result.IsUsable = allDeps;
            result.Message = allDeps ? "Python 环境可用。" : "Python 可用，但 MCP server 依赖不完整。";
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
                ErrorMessage = source.ErrorMessage,
                Version = source.Version,
                DownloadUrl = source.DownloadUrl,
                Sha256 = source.Sha256,
                TargetPath = source.TargetPath,
                BytesReceived = source.BytesReceived,
                TotalBytes = source.TotalBytes,
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
                if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
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
            catch
            {
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

        private static bool IsVersionAtLeast(string current, string minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum))
                return true;
            if (string.IsNullOrWhiteSpace(current))
                return false;
            if (!TryParseVersionParts(current, out var cMajor, out var cMinor, out var cPatch))
                return false;
            if (!TryParseVersionParts(minimum, out var mMajor, out var mMinor, out var mPatch))
                return false;
            if (cMajor != mMajor) return cMajor > mMajor;
            if (cMinor != mMinor) return cMinor > mMinor;
            return cPatch >= mPatch;
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
