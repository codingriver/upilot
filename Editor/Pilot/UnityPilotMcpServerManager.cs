// -----------------------------------------------------------------------
// upilot Editor — MCP Server Process Manager
// Manages the external upilot MCP server process independently of Unity.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace codingriver.unity.pilot
{
    public struct McpServerStatus
    {
        public bool IsRunning;
        public bool HttpPortListening;
        public bool WsPortListening;
        public int? ProcessId;
        public string ProcessCommandLine;
        public string ErrorMessage;
        public int WsClientCount;
        public int HttpClientCount;
    }

    public sealed class UnityPilotMcpServerManager
    {
        public static UnityPilotMcpServerManager Instance { get; } = new();

        private static string DefaultPythonEntry => ResolveDefaultPythonEntry();
        private const string DefaultLogLevel = "INFO";
        private const string PackageName = "io.github.codingriver.upilot";
        private const string LegacyPackageName = "io.github.codingriver.unityuiflow";

        private static string HashSuffix => UnityPilotBridge.WsEndpointEditorPrefsKeySuffix;

        // Legacy keys — used only for one-time migration to UnityPilotBridge shared keys.

        private static string PythonEntryKey => $"UnityPilot.McpMgr.PythonEntry.{HashSuffix}";
        private static string LogLevelKey => $"UnityPilot.McpMgr.LogLevel.{HashSuffix}";
        private static string AutoStartKey => $"UnityPilot.McpMgr.AutoStart.{HashSuffix}";

        private string _pythonEntryPath = DefaultPythonEntry;
        private string _logLevel = DefaultLogLevel;
        private bool _autoStart = true;

        /// <summary>HTTP port is stored and managed by UnityPilotBridge (single source of truth).</summary>
        public int HttpPort => UnityPilotBridge.Instance?.HttpPort ?? 8011;
        /// <summary>WS port is stored and managed by UnityPilotBridge (single source of truth).</summary>
        public int WsPort => UnityPilotBridge.Instance?.WsPort ?? 8765;
        public string PythonEntryPath => _pythonEntryPath;

        public void SetPythonEntryPath(string path)
        {
            if (_pythonEntryPath == path) return;
            _pythonEntryPath = path;
            SavePrefs();
        }

        /// <summary>
        /// Validates the current persisted path on window open.
        /// If the path is invalid, attempts auto-discovery.
        /// Only updates when auto-discovery succeeds; leaves user-defined paths untouched on failure.
        /// </summary>
        public void ValidateAndAutoFixPath()
        {
            Debug.Log($"[UnityPilotMcpServerManager] ValidateAndAutoFixPath called. Current path: {_pythonEntryPath}");
            if (string.IsNullOrEmpty(_pythonEntryPath))
            {
                Debug.Log("[UnityPilotMcpServerManager] Current path is null or empty, skipping validation.");
                return;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string fullPath = Path.IsPathRooted(_pythonEntryPath)
                ? _pythonEntryPath
                : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));
            Debug.Log($"[UnityPilotMcpServerManager] Resolved fullPath: {fullPath}, exists={File.Exists(fullPath)}");

            // Current path is valid — nothing to do.
            if (File.Exists(fullPath))
            {
                Debug.Log("[UnityPilotMcpServerManager] Current path is valid, no action needed.");
                return;
            }

            // Current path is invalid — try to discover the default.
            Debug.Log("[UnityPilotMcpServerManager] Current path is invalid, attempting auto-discovery...");
            string discovered = ResolveDefaultPythonEntry();
            string discoveredFull = Path.IsPathRooted(discovered)
                ? discovered
                : Path.GetFullPath(Path.Combine(projectRoot, discovered));
            Debug.Log($"[UnityPilotMcpServerManager] Discovered path: {discovered}, resolved: {discoveredFull}, exists={File.Exists(discoveredFull)}");

            // Only overwrite if discovery actually found an existing file.
            if (File.Exists(discoveredFull))
            {
                _pythonEntryPath = discovered;
                SavePrefs();
                Debug.Log($"[UnityPilotMcpServerManager] Auto-fixed path to: {discovered}");
            }
            else
            {
                Debug.LogWarning("[UnityPilotMcpServerManager] Auto-discovery failed, keeping existing path for manual correction.");
            }
        }
        public string LogLevel { get => _logLevel; set { if (_logLevel != value) { _logLevel = value; SavePrefs(); } } }
        public bool AutoStartEnabled { get => _autoStart; set { if (_autoStart != value) { _autoStart = value; SavePrefs(); } } }

        // ── Cached status (background refresh) ────────────────────────────────

        private const int RefreshIntervalMs = 2000;
        private readonly object _statusLock = new();
        private McpServerStatus _cachedStatus;
        private volatile bool _refreshRunning;
        private long _lastRefreshMs;

        private static readonly System.Net.Http.HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private static string ResolveDefaultPythonEntry()
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                Debug.Log($"[UnityPilotMcpServerManager] ResolveDefaultPythonEntry: projectRoot={projectRoot}");

                bool TryCandidate(string candidatePath, string source, out string result)
                {
                    result = null;
                    if (string.IsNullOrEmpty(candidatePath)) return false;
                    if (!File.Exists(candidatePath))
                    {
                        Debug.Log($"[UnityPilotMcpServerManager]   Checking candidate ({source}): {candidatePath}, exists=False");
                        return false;
                    }
                    result = candidatePath.Replace('\\', '/');
                    Debug.Log($"[UnityPilotMcpServerManager]   -> FOUND via {source}: {result}");
                    return true;
                }

                // 0. Read manifest to locate file: referenced package paths
                string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        string manifestJson = File.ReadAllText(manifestPath);
                        // Robust regex to extract the dependency value
                        string pattern = "\"(?:" + Regex.Escape(PackageName) + "|" + Regex.Escape(LegacyPackageName) + ")\"\\s*:\\s*\"([^\"]+)\"";
                        var match = Regex.Match(manifestJson, pattern, RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string depValue = match.Groups[1].Value;
                            Debug.Log($"[UnityPilotMcpServerManager] Manifest dep value: {depValue}");

                            if (depValue.StartsWith("file:"))
                            {
                                string filePath = depValue.Substring(5);
                                
                                // Normalize URI-style file paths: file:///path → /path
                                while (filePath.Length >= 2 && filePath[0] == '/' && filePath[1] == '/')
                                    filePath = filePath.Substring(1);

                                // Windows: /D:/path → D:/path
                                if (filePath.Length > 2 && filePath[0] == '/' && filePath[2] == ':' && char.IsLetter(filePath[1]))
                                    filePath = filePath.Substring(1);

                                // Tarball not supported for auto-discovery
                                if (filePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || 
                                    filePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                                {
                                    Debug.LogWarning($"[UnityPilotMcpServerManager] Tarball installation ({depValue}) is not auto-discoverable. Please extract it or set path manually.");
                                }
                                else
                                {
                                    string absPath;
                                    if (Path.IsPathRooted(filePath))
                                        absPath = filePath;
                                    else
                                        absPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
                                    
                                    Debug.Log($"[UnityPilotMcpServerManager] Resolved file: reference to: {absPath}");
                                    string candidate = Path.Combine(absPath, "unitypilot~", "run_upilot_mcp.py");
                                    if (TryCandidate(candidate, "manifest file: ref", out string result))
                                        return result;

                                    string legacyCandidate = Path.Combine(absPath, "unitypilot~", "run_unitypilot_mcp.py");
                                    if (TryCandidate(legacyCandidate, "manifest file: ref legacy launcher", out result))
                                        return result;
                                }
                            }
                            else
                            {
                                // Registry version like "1.0.0" — will be in PackageCache
                                Debug.Log($"[UnityPilotMcpServerManager] Registry/Git version reference: {depValue}, will search PackageCache");
                            }
                        }
                        else
                        {
                            Debug.Log($"[UnityPilotMcpServerManager] Package '{PackageName}' not found in manifest.json");
                        }
                    }
                    catch (Exception manifestEx)
                    {
                        Debug.LogWarning($"[UnityPilotMcpServerManager] Failed to parse manifest.json: {manifestEx.Message}");
                    }
                }

                // 1. Search local embedded packages directly under Packages/
                string packagesDir = Path.Combine(projectRoot, "Packages");
                Debug.Log($"[UnityPilotMcpServerManager] Checking Packages dir: {packagesDir}, exists={Directory.Exists(packagesDir)}");
                if (Directory.Exists(packagesDir))
                {
                    foreach (var dir in Directory.GetDirectories(packagesDir))
                    {
                        string candidate = Path.Combine(dir, "unitypilot~", "run_upilot_mcp.py");
                        if (TryCandidate(candidate, "Packages dir scan", out string result))
                            return result;

                        string legacyCandidate = Path.Combine(dir, "unitypilot~", "run_unitypilot_mcp.py");
                        if (TryCandidate(legacyCandidate, "Packages dir scan legacy launcher", out result))
                            return result;
                    }
                }

                // 2. Search package cache (Git URL / registry installs)
                string cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");
                Debug.Log($"[UnityPilotMcpServerManager] Checking PackageCache dir: {cacheDir}, exists={Directory.Exists(cacheDir)}");
                if (Directory.Exists(cacheDir))
                {
                    foreach (var dir in Directory.GetDirectories(cacheDir, "io.github.codingriver.upilot*"))
                    {
                        string candidate = Path.Combine(dir, "unitypilot~", "run_upilot_mcp.py");
                        if (TryCandidate(candidate, "PackageCache top-level", out string result))
                            return result;
                    }

                    foreach (var dir in Directory.GetDirectories(cacheDir, "io.github.codingriver.unityuiflow*"))
                    {
                        string candidate = Path.Combine(dir, "unitypilot~", "run_unitypilot_mcp.py");
                        if (TryCandidate(candidate, "PackageCache top-level legacy package", out string result))
                            return result;
                    }
                    
                    // Some Unity versions nest packages in subdirectories
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(cacheDir))
                        {
                            foreach (var dir in Directory.GetDirectories(subDir, "io.github.codingriver.upilot*"))
                            {
                                string candidate = Path.Combine(dir, "unitypilot~", "run_upilot_mcp.py");
                                if (TryCandidate(candidate, "PackageCache nested", out string result))
                                    return result;
                            }

                            foreach (var dir in Directory.GetDirectories(subDir, "io.github.codingriver.unityuiflow*"))
                            {
                                string candidate = Path.Combine(dir, "unitypilot~", "run_unitypilot_mcp.py");
                                if (TryCandidate(candidate, "PackageCache nested legacy package", out string result))
                                    return result;
                            }
                        }
                    }
                    catch { /* ignore nested search errors */ }
                }

                // 3. Search project root directly (legacy / alternative layout)
                string rootCandidate = Path.Combine(projectRoot, "unitypilot~", "run_upilot_mcp.py");
                if (TryCandidate(rootCandidate, "project root", out string rootResult))
                    return rootResult;

                string rootLegacyCandidate = Path.Combine(projectRoot, "unitypilot~", "run_unitypilot_mcp.py");
                if (TryCandidate(rootLegacyCandidate, "project root legacy launcher", out rootResult))
                    return rootResult;

                // 4. Search parent directories (monorepo fallback)
                try
                {
                    var currentDir = new DirectoryInfo(projectRoot);
                    for (int i = 0; i < 3 && currentDir.Parent != null; i++)
                    {
                        currentDir = currentDir.Parent;
                        string parentCandidate = Path.Combine(currentDir.FullName, "Packages", "com.upilot", "unitypilot~", "run_upilot_mcp.py");
                        if (TryCandidate(parentCandidate, $"parent dir (level {i + 1})", out string parentResult))
                            return parentResult;
                    }
                }
                catch { /* ignore */ }

                Debug.LogWarning("[UnityPilotMcpServerManager] No valid python entry found, falling back to ./unitypilot~/run_upilot_mcp.py");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityPilotMcpServerManager] ResolveDefaultPythonEntry exception: {ex}");
            }
            return "./unitypilot~/run_upilot_mcp.py";
        }

        private UnityPilotMcpServerManager()
        {
            LoadPrefs();
        }

        private void LoadPrefs()
        {

            _pythonEntryPath = EditorPrefs.GetString(PythonEntryKey, DefaultPythonEntry);
            _logLevel = EditorPrefs.GetString(LogLevelKey, DefaultLogLevel);
            _autoStart = EditorPrefs.GetBool(AutoStartKey, true);
            var bridge = UnityPilotBridge.Instance;
            Debug.Log($"[UnityPilotMcpServerManager] LoadPrefs loaded: HttpPort={bridge?.HttpPort ?? 8011}, WsPort={bridge?.WsPort ?? 8765}, PythonEntryPath={_pythonEntryPath}, LogLevel={_logLevel}, AutoStart={_autoStart}");

            // If the persisted path points to a file that no longer exists
            // (e.g. old root-level unitypilot/ was removed), or if the path
            // mistakenly uses the UPM package name as the directory name
            // (e.g. "Packages/io.github.codingriver.unityuiflow/..."), re-discover.
            if (!string.IsNullOrEmpty(_pythonEntryPath))
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string fullPath = Path.IsPathRooted(_pythonEntryPath)
                    ? _pythonEntryPath
                    : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));
                bool needsRediscover = !File.Exists(fullPath)
                    || _pythonEntryPath.IndexOf(LegacyPackageName, StringComparison.OrdinalIgnoreCase) >= 0;
                Debug.Log($"[UnityPilotMcpServerManager] LoadPrefs validation: fullPath={fullPath}, exists={File.Exists(fullPath)}, needsRediscover={needsRediscover}");
                if (needsRediscover)
                {
                    string discovered = DefaultPythonEntry;
                    Debug.Log($"[UnityPilotMcpServerManager] LoadPrefs rediscovering: {discovered}");
                    _pythonEntryPath = discovered;
                    SavePrefs();
                }
            }
        }
        
        private void SavePrefs()
        {
            EditorPrefs.SetString(PythonEntryKey, _pythonEntryPath ?? DefaultPythonEntry);
            EditorPrefs.SetString(LogLevelKey, _logLevel ?? DefaultLogLevel);
            EditorPrefs.SetBool(AutoStartKey, _autoStart);
        }

        // ── Status ──────────────────────────────────────────────────────────

        public McpServerStatus GetStatus()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!_refreshRunning && (now - _lastRefreshMs > RefreshIntervalMs))
            {
                // Ensure the async state machine starts on a background thread,
                // so the main thread is never blocked by synchronous work before
                // the first await inside RefreshStatusAsync.
                Task.Run(RefreshStatusAsync);
            }
            lock (_statusLock) { return _cachedStatus; }
        }

        private async Task RefreshStatusAsync()
        {
            if (_refreshRunning) return;
            _refreshRunning = true;
            try
            {
                var status = new McpServerStatus();
                var httpTask = IsPortListeningAsync("127.0.0.1", HttpPort);
                var wsTask = IsPortListeningAsync("127.0.0.1", WsPort);
                status.HttpPortListening = await httpTask;
                status.WsPortListening = await wsTask;
                status.IsRunning = status.HttpPortListening || status.WsPortListening;

                if (status.IsRunning)
                {
                    var (pid, cmdLine) = await Task.Run(() => FindMcpProcessByPorts());
                    if (pid.HasValue)
                    {
                        status.ProcessId = pid;
                        status.ProcessCommandLine = cmdLine;
                    }
                    var (wsCount, httpCount) = await FetchServerStatsAsync();
                    status.WsClientCount = wsCount;
                    status.HttpClientCount = httpCount;
                }

                lock (_statusLock) { _cachedStatus = status; }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityPilotMcpServerManager] Status refresh failed: {ex.Message}");
            }
            finally
            {
                _lastRefreshMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _refreshRunning = false;
            }
        }

        private async Task<(int wsCount, int httpCount)> FetchServerStatsAsync()
        {
            try
            {
                var url = $"http://127.0.0.1:{HttpPort}/stats";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return (0, 0);
                var json = await response.Content.ReadAsStringAsync();
                int wsCount = ParseIntFromJson(json, "ws_connections");
                int httpCount = ParseIntFromJson(json, "http_sessions");
                return (wsCount, httpCount);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static int ParseIntFromJson(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            var pattern = $"\"{key}\"\\s*:\\s*(\\d+)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success && int.TryParse(match.Groups[1].Value, out var val) ? val : 0;
        }

        // ── Start ───────────────────────────────────────────────────────────

        public void StartServer()
        {
            var status = GetStatus();
            if (status.IsRunning)
            {
                Debug.LogWarning("[UnityPilotMcpServerManager] MCP server already running.");
                return;
            }

            // Auto-sync Bridge WS endpoint to match MCP server port before starting.
            // Bridge and MCP manager store their ports in separate EditorPrefs keys,
            // so they can drift apart after manual edits or version upgrades.
            var bridge = UnityPilotBridge.Instance;
            if (bridge != null && (bridge.WsPort != WsPort || bridge.WsHost != "127.0.0.1"))
            {
                int oldPort = bridge.WsPort;
                string oldHost = bridge.WsHost;
                bridge.SetWsEndpoint("127.0.0.1", WsPort);
                Debug.LogWarning($"[UnityPilotMcpServerManager] Auto-synced Bridge endpoint from ws://{oldHost}:{oldPort} to ws://127.0.0.1:{WsPort} to match MCP server.");
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

            try
            {
                StartViaDirectPython(projectRoot);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityPilotMcpServerManager] Failed to start server: {ex.Message}");
            }
        }

        private void StartViaDirectPython(string projectRoot)
        {
            string entryFullPath = Path.IsPathRooted(_pythonEntryPath)
                ? _pythonEntryPath
                : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));

            if (!File.Exists(entryFullPath))
            {
                Debug.LogError($"[UnityPilotMcpServerManager] Python entry not found: {entryFullPath}");
                return;
            }

            string pythonExe = FindPythonExecutable();
            if (string.IsNullOrEmpty(pythonExe))
            {
                Debug.LogError("[UnityPilotMcpServerManager] No Python interpreter found. Please install Python and ensure 'python', 'py', or 'python3' is available in PATH.");
                return;
            }

            string logDir = Path.Combine(projectRoot, "log");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "mcp-server.log");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{entryFullPath}\" --transport http --http-port {HttpPort} --port {WsPort} --log-file \"{logFile}\" --log-level {_logLevel}",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            Debug.Log($"[UnityPilotMcpServerManager] Started python process PID={proc?.Id} via {pythonExe} for {entryFullPath} (HTTP={HttpPort}, WS={WsPort})");
        }

        private static string FindPythonExecutable()
        {
            string[] candidates = new[] { "python", "py", "python3" };
            foreach (var name in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) continue;
                    proc.WaitForExit(2000);
                    if (proc.ExitCode != 0) continue;
                    string output = proc.StandardOutput.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
                catch { }
            }
            return null;
        }

        // ── Stop ────────────────────────────────────────────────────────────

        public void StopServer()
        {
            var (pid, cmdLine) = FindMcpProcessByPorts();
            if (!pid.HasValue)
            {
                Debug.LogWarning("[UnityPilotMcpServerManager] No MCP server process found listening on configured ports.");
                return;
            }

            try
            {
                var proc = Process.GetProcessById(pid.Value);
                proc.Kill();
                Debug.Log($"[UnityPilotMcpServerManager] Killed MCP server process PID={pid.Value}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityPilotMcpServerManager] Failed to kill process PID={pid.Value}: {ex.Message}");
            }
        }

        // ── Port & Process Helpers ─────────────────────────────────────────

        private static async Task<bool> IsPortListeningAsync(string host, int port, int timeoutMs = 300)
        {
            try
            {
                // Run synchronous Connect on thread pool to avoid any potential
                // mono-runtime quirks with TcpClient.ConnectAsync on the main thread.
                using var client = new TcpClient();
                var connectTask = Task.Run(() =>
                {
                    try { client.Connect(host, port); return true; }
                    catch { return false; }
                });
                var timeoutTask = Task.Delay(timeoutMs);
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    return false;
                return connectTask.Result;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPortListening(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(host, port);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private (int? pid, string cmdLine) FindMcpProcessByPorts()
        {
            var portsByPid = SafeGetListeningPortsByPid(out bool success);
            if (!success) return (null, null);

            // First try: look for PID listening on HTTP port
            foreach (var kv in portsByPid)
            {
                if (kv.Value.Contains(HttpPort) || kv.Value.Contains(WsPort))
                {
                    string cmdLine = SafeGetCommandLine(kv.Key);
                    if (IsUnityPilotMcpLike(cmdLine))
                        return (kv.Key, cmdLine);
                }
            }

            // Second try: scan all python processes for command line match
            var p1 = Process.GetProcessesByName("python");
            var p2 = Process.GetProcessesByName("python3");
            foreach (var p in p1)
            {
                string cmdLine = SafeGetCommandLine(p.Id);
                if (IsUnityPilotMcpLike(cmdLine))
                    return (p.Id, cmdLine);
            }
            foreach (var p in p2)
            {
                string cmdLine = SafeGetCommandLine(p.Id);
                if (IsUnityPilotMcpLike(cmdLine))
                    return (p.Id, cmdLine);
            }

            return (null, null);
        }

        private static bool IsUnityPilotMcpLike(string cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine)) return false;
            return cmdLine.IndexOf("run_upilot_mcp.py", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("upilot-mcp", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("run_unitypilot_mcp.py", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("unitypilot_mcp", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("unitypilot-mcp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<int, List<int>> SafeGetListeningPortsByPid(out bool success)
        {
            var result = new Dictionary<int, List<int>>();
            success = false;

#if UNITY_EDITOR_WIN
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return result;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                if (string.IsNullOrWhiteSpace(output))
                    return result;

                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = (raw ?? string.Empty).Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                        continue;

                    var state = parts[3];
                    if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!int.TryParse(parts[4], out var pid) || pid <= 0)
                        continue;

                    int port = SafeParsePortFromEndpoint(parts[1]);
                    if (port <= 0)
                        continue;

                    if (!result.TryGetValue(pid, out var list))
                    {
                        list = new List<int>();
                        result[pid] = list;
                    }
                    if (!list.Contains(port))
                        list.Add(port);
                }

                success = true;
            }
            catch
            {
                success = false;
            }
#endif
            return result;
        }

        private static int SafeParsePortFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return -1;

            int idx = endpoint.LastIndexOf(':');
            if (idx < 0 || idx >= endpoint.Length - 1)
                return -1;

            var portText = endpoint.Substring(idx + 1).Trim();
            return int.TryParse(portText, out var port) ? port : -1;
        }

        private static string SafeGetCommandLine(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get CommandLine /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return "(读取命令行失败)";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                if (string.IsNullOrWhiteSpace(output)) return "(空)";
                var marker = "CommandLine=";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return output.Trim();

                var cmd = output.Substring(idx + marker.Length).Trim();
                return string.IsNullOrWhiteSpace(cmd) ? "(空)" : cmd;
            }
            catch
            {
                return "(读取命令行失败)";
            }
#else
            return "(当前平台未实现命令行读取)";
#endif
        }
    }
}
