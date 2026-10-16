// -----------------------------------------------------------------------
// upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>Daily append-only diagnostics; retention follows the project logging setting.</summary>
    [InitializeOnLoad]
    public static partial class Logger
    {
        private static readonly object FileLock = new object();
        private static readonly int MainThreadId;
        private static readonly int CurrentProcessId;
        private static readonly DateTime CurrentProcessStartTime;
        private static readonly bool IsAssetImportWorker;

        private static readonly string SessionMarkerPath;
        private static bool _sessionPrepared;

        internal const int MaxLogEntryChars = 64 * 1024;

        public static string LastFileError { get; private set; } = string.Empty;

        public static string ProjectLogsDirectory
        {
            get
            {
                return UPilotOutputPaths.Resolve(UPilotOutputPaths.Root);
            }
        }

        private static string BaseLogPath => UPilotOutputPaths.Resolve(UPilotOutputPaths.UnityLog);
        private static readonly UPilotDailyLogFile DailyFile = new(BaseLogPath);
        public static string LogFilePath => UPilotDailyLogFile.ResolveExisting(BaseLogPath);

        /// <summary>
        /// 最低输出日志级别。低于此级别的日志将被忽略。默认 Debug。
        /// </summary>
        public static LogLevel MinLevel = LogLevel.Debug;

        public const string LogToUnityConsolePrefsKey = "CodingRiver.UPilot.Logger.LogToUnityConsole";

        /// <summary>
        /// 是否同时将普通信息日志输出到 Unity Editor Console。默认开启；警告、错误和异常不受此开关影响。
        /// </summary>
        public static bool LogToUnityConsole = EditorPrefs.GetBool(UPilotPreferences.LogToUnityConsoleKey, true);
        private static bool _debugWireLogsEnabled = EditorPrefs.GetBool(UPilotPreferences.DebugWireLogsKey, false);
        private static bool _verboseLogsEnabled = EditorPrefs.GetBool(UPilotPreferences.VerboseLogsKey, false);

        /// <summary>
        /// 当前线程是否为主线程。
        /// </summary>
        private static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        static Logger()
        {
            LogToUnityConsole = EditorPrefs.GetBool(UPilotPreferences.LogToUnityConsoleKey, true);

            MainThreadId = Thread.CurrentThread.ManagedThreadId;

            var process = System.Diagnostics.Process.GetCurrentProcess();
            CurrentProcessId = process.Id;
            CurrentProcessStartTime = process.StartTime;
            IsAssetImportWorker = AssetDatabase.IsAssetImportWorkerProcess();

            SessionMarkerPath = Path.Combine(ProjectLogsDirectory, ".session");
            if (!IsAssetImportWorker)
            {
                EditorApplication.update += PollDiagnostics;
                EditorApplication.quitting += FlushDiagnostics;
                AssemblyReloadEvents.beforeAssemblyReload += FlushDiagnostics;
            }

            if (IsAssetImportWorker)
            {
                _sessionPrepared = true;
            }
            else if (IsNewSession())
            {
                InitializeNewSession();
            }
            else
            {
                _sessionPrepared = true;
            }
        }

        public static void SetLogToUnityConsole(bool enabled)
        {
            LogToUnityConsole = enabled;
            EditorPrefs.SetBool(UPilotPreferences.LogToUnityConsoleKey, enabled);
        }

        internal static void SetRuntimeLogOptions(bool debugWireLogsEnabled, bool verboseLogsEnabled)
        {
            _debugWireLogsEnabled = debugWireLogsEnabled;
            _verboseLogsEnabled = verboseLogsEnabled;
        }

        // ── 会话检测与初始化 ──────────────────────────────────────────────────

        private static bool IsNewSession()
        {
            try
            {
                if (!File.Exists(SessionMarkerPath)) return true;
                var content = File.ReadAllText(SessionMarkerPath, Encoding.UTF8);
                var parts = content.Split('|');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var pid) &&
                    DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out var processStartTime))
                {
                    return pid != CurrentProcessId || processStartTime.ToUniversalTime() != CurrentProcessStartTime.ToUniversalTime();
                }
            }
            catch (Exception ex)
            {
                ReportFileFailure("read session marker", ex);
            }
            return true;
        }

        private static void InitializeNewSession()
        {
            try
            {
                lock (FileLock)
                {
                    UPilotDailyLogFile.RequireSafePath(SessionMarkerPath);
                    Directory.CreateDirectory(ProjectLogsDirectory);
                    File.WriteAllText(SessionMarkerPath, $"{CurrentProcessId}|{CurrentProcessStartTime:O}", Encoding.UTF8);

                    var header = new StringBuilder();
                    header.AppendLine("════════════════════════════════════════════════════════════");
                    header.AppendLine("Session Start");
                    header.AppendLine($"Timestamp    : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    header.AppendLine($"Unity Ver    : {Application.unityVersion}");
                    header.AppendLine($"Platform     : {Application.platform}");
                    header.AppendLine($"Product Name : {Application.productName}");
                    header.AppendLine($"Process Id   : {CurrentProcessId}");
                    header.AppendLine($"MainThreadId : {MainThreadId}");
                    header.AppendLine("════════════════════════════════════════════════════════════");

                    var headerText = header.ToString() + Environment.NewLine;
                    DailyFile.Append(headerText, DateTime.Now.Date, ReportFileFailure);

                    if (LogToUnityConsole)
                        UnityEngine.Debug.Log(header.ToString());

                    _sessionPrepared = true;
                }
            }
            catch (Exception ex)
            {
                ReportFileFailure("准备日志文件", ex);
            }
        }

        // ── 日志级别 ──────────────────────────────────────────────────────────

        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3
        }

        private static bool ShouldLog(LogLevel level) => level >= MinLevel;

        internal static bool ShouldMirrorInfoToUnityConsole(string category, bool verboseLogsEnabled, bool testRunning)
        {
            if (testRunning && (category == "COMMAND" || category == "NETWORK"))
                return false;

            return !IsVerboseConsoleCategory(category) || verboseLogsEnabled;
        }

        private static bool IsVerboseConsoleCategory(string category)
        {
            return string.Equals(category, "COMMAND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "COMPILE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "NETWORK", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(category, "UPilot.Flow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldMirrorInfoToUnityConsole(string category)
        {
            return ShouldMirrorInfoToUnityConsole(
                category,
                _verboseLogsEnabled,
                UPilotTestService.Instance?.IsRunning == true);
        }

        // ── 文件故障 ──────────────────────────────────────────────────────────

        private static void ReportFileFailure(string operation, Exception ex) => ReportFileFailure(operation, BaseLogPath, ex);

        private static void ReportFileFailure(string operation, string path, Exception ex)
        {
            LastFileError = $"{operation} path={path}: {ex}";
            UPilotDailyLogFile.ReportError(operation, path, ex);
        }

        internal static string TruncateLogEntry(string value, int maxChars = MaxLogEntryChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value;
            return value.Substring(0, maxChars) + $" ... [truncated, total={value.Length}]";
        }

        // ── 核心写入 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将一行文本追加到日志文件。自动在行首添加毫秒级本地时间戳、线程/帧号、标签，立即落盘。
        /// </summary>
        /// <param name="line">日志正文（通常已包含 [LEVEL] message）。</param>
        /// <param name="tags">可选标签列表，会追加在行尾。</param>
        public static void AppendLine(string line, params string[] tags)
        {
            if (IsAssetImportWorker || string.IsNullOrEmpty(line)) return;
            EnsureSessionLogFile();

            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]");

            if (IsMainThread)
            {
                sb.Append($" [frame:{Time.frameCount}]");
            }
            else
            {
                sb.Append($" [thread:{Thread.CurrentThread.ManagedThreadId}]");
            }

            sb.Append($" {line}");

            if (tags != null && tags.Length > 0)
            {
                sb.Append($" [{string.Join(",", tags)}]");
            }

            DailyFile.Append(sb.ToString() + Environment.NewLine, DateTime.Now.Date, ReportFileFailure);
        }

        /// <summary>File-only on success; IO failures always enter the Unity Console as errors.</summary>
        internal static bool TryAppendFileOnly(string line, string path = null)
        {
            if (path == null)
                return DailyFile.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\n", DateTime.Now.Date, ReportFileFailure);
            try
            {
                lock (FileLock)
                {
                    UPilotDailyLogFile.RequireSafePath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    UPilotDailyLogFile.RequireSafePath(path);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch (Exception ex) { ReportFileFailure("write", path, ex); return false; }
        }

        private static void EnsureSessionLogFile()
        {
            lock (FileLock)
            {
                if (_sessionPrepared)
                    return;

                try
                {
                    Directory.CreateDirectory(ProjectLogsDirectory);

                    if (IsNewSession())
                    {
                        InitializeNewSession();
                    }
                    else
                    {
                        _sessionPrepared = true;
                    }
                }
                catch (Exception ex)
                {
                    ReportFileFailure("prepare session", ex);
                    _sessionPrepared = true;
                }
            }
        }

        // ── Console 双写 ──────────────────────────────────────────────────────

        private static void WriteToUnityConsole(string line, LogType logType, params string[] tags)
        {
            if (IsAssetImportWorker) return;
            if (!LogToUnityConsole && logType != LogType.Warning && logType != LogType.Error && logType != LogType.Exception) return;
            if (string.IsNullOrEmpty(line)) return;

            var sb = new StringBuilder();
            sb.Append(line);
            if (tags != null && tags.Length > 0)
            {
                sb.Append($" [{string.Join(",", tags)}]");
            }

            var message = TruncateLogEntry(sb.ToString());
            switch (logType)
            {
                case LogType.Warning:
                    UnityEngine.Debug.LogWarning(message);
                    break;
                case LogType.Error:
                    UnityEngine.Debug.LogError(message);
                    break;
                case LogType.Exception:
                    UnityEngine.Debug.LogError(message);
                    break;
                default:
                    UnityEngine.Debug.Log(message);
                    break;
            }
        }

        // ── 带分类的写入接口 ──────────────────────────────────────────────────

        // ── 带分类的写入接口 ──────────────────────────────────────────────────

        /// <summary>普通信息日志（带分类）。格式：[timestamp] [frame] [INFO ] [CATEGORY] message</summary>
        public static void Log(string category, string message)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [{category,-8}] {message}");
            if (ShouldMirrorInfoToUnityConsole(category))
                WriteToUnityConsole($"[INFO ] [{category,-8}] {message}", LogType.Log);
        }

        /// <summary>普通信息日志（带分类与标签）。</summary>
        public static void Log(string category, string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [{category,-8}] {message}", tags);
            if (ShouldMirrorInfoToUnityConsole(category))
                WriteToUnityConsole($"[INFO ] [{category,-8}] {message}", LogType.Log, tags);
        }

        /// <summary>警告日志（带分类）。格式：[timestamp] [frame] [WARN ] [CATEGORY] message</summary>
        public static void LogWarning(string category, string message)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            AppendLine($"[WARN ] [{category,-8}] {message}");
            WriteToUnityConsole($"[WARN ] [{category,-8}] {message}", LogType.Warning);
        }

        /// <summary>警告日志（带分类与标签）。</summary>
        public static void LogWarning(string category, string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            AppendLine($"[WARN ] [{category,-8}] {message}", tags);
            WriteToUnityConsole($"[WARN ] [{category,-8}] {message}", LogType.Warning, tags);
        }

        /// <summary>错误日志（带分类）。格式：[timestamp] [frame] [ERROR] [CATEGORY] message</summary>
        public static void LogError(string category, string message)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            AppendLine($"[ERROR] [{category,-8}] {message}");
            WriteToUnityConsole($"[ERROR] [{category,-8}] {message}", LogType.Error);
        }

        /// <summary>错误日志（带分类与标签）。</summary>
        public static void LogError(string category, string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            AppendLine($"[ERROR] [{category,-8}] {message}", tags);
            WriteToUnityConsole($"[ERROR] [{category,-8}] {message}", LogType.Error, tags);
        }

        // ── 兼容旧接口（无显式分类）────────────────────────────────────────────

        public static void Log(string message)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] {message}");
            WriteToUnityConsole($"[INFO ] {message}", LogType.Log);
        }

        public static void Log(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] {message}", tags);
            WriteToUnityConsole($"[INFO ] {message}", LogType.Log, tags);
        }

        public static void LogWarning(string message)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            AppendLine($"[WARN ] {message}");
            WriteToUnityConsole($"[WARN ] {message}", LogType.Warning);
        }

        public static void LogWarning(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            AppendLine($"[WARN ] {message}", tags);
            WriteToUnityConsole($"[WARN ] {message}", LogType.Warning, tags);
        }

        public static void LogError(string message)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            AppendLine($"[ERROR] {message}");
            WriteToUnityConsole($"[ERROR] {message}", LogType.Error);
        }

        public static void LogError(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            AppendLine($"[ERROR] {message}", tags);
            WriteToUnityConsole($"[ERROR] {message}", LogType.Error, tags);
        }

        public static void LogException(Exception ex)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            var line = $"[ERROR] Exception: {ex}";
            AppendLine(line);
            WriteToUnityConsole(line, LogType.Exception);
        }

        public static void LogException(Exception ex, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            var line = $"[ERROR] Exception: {ex}";
            AppendLine(line, tags);
            WriteToUnityConsole(line, LogType.Exception, tags);
        }

        // ── 网络通信日志 ───────────────────────────────────────────────────────

        public static void LogNetwork(string category, string message, bool isSend = true)
        {
            if (!_debugWireLogsEnabled) return;
            var sign = isSend ? "SEND" : "RECV";
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [{category,-8}] [{sign}] {message}");
            if (UPilotTestService.Instance?.IsRunning != true)
                WriteToUnityConsole($"[INFO ] [{category,-8}] [{sign}] {message}", LogType.Log);
        }

        /// <summary>兼容旧网络日志接口。</summary>
        public static void LogNetwork(string message, bool isSend = true)
        {
            if (!_debugWireLogsEnabled) return;
            var sign = isSend ? "SEND" : "RECV";
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [NET     ] [{sign}] {message}");
            if (UPilotTestService.Instance?.IsRunning != true)
                WriteToUnityConsole($"[INFO ] [NET     ] [{sign}] {message}", LogType.Log);
        }

        // ── 工具方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 截断超长 JSON payload，避免单条日志过大。
        /// </summary>
        public static string TruncatePayload(string json, int maxLen = 800)
        {
            if (string.IsNullOrEmpty(json) || json.Length <= maxLen) return json;
            return json.Substring(0, maxLen) + $" ... [truncated, total={json.Length}]";
        }

        public static void RevealLogFile()
        {
            try
            {
                var path = LogFilePath;
                if (!File.Exists(path))
                {
                    EditorUtility.DisplayDialog("UPilot", "日志尚未生成。", "确定");
                    return;
                }
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                ReportFileFailure("reveal", ex);
                EditorUtility.DisplayDialog("UPilot", $"无法打开日志: {ex.Message}", "确定");
            }
        }
    }
}
