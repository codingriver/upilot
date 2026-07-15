// -----------------------------------------------------------------------
// upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// 将 upilot 编辑器日志写入项目根目录下 <c>Logs/UPilot/UPilot.log</c>。
    /// 每次打开 Unity 编辑器（新会话）首次写入前会清空并写入会话头；同一会话内脚本域重载不清空，继续追加。
    /// 所有写入均立即落盘（无缓冲），时间戳精确到毫秒（本地时区）。
    /// 支持标签、主线程帧号检测、LogLevel 过滤、日志按大小滚动，并双写到 Unity Editor Console。
    /// </summary>
    [InitializeOnLoad]
    public static class Logger
    {
        private static readonly object FileLock = new object();
        private static readonly int MainThreadId;
        private static readonly int CurrentProcessId;
        private static readonly DateTime CurrentProcessStartTime;

        private static readonly string SessionMarkerPath;
        private static bool _sessionPrepared;

        public static string ProjectLogsDirectory
        {
            get
            {
                var root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                return Path.Combine(root, "Logs", "UPilot");
            }
        }

        public static string LogFilePath => Path.Combine(ProjectLogsDirectory, "upilot.log");

        /// <summary>
        /// 最低输出日志级别。低于此级别的日志将被忽略。默认 Debug。
        /// </summary>
        public static LogLevel MinLevel = LogLevel.Debug;

        public const string LogToUnityConsolePrefsKey = "CodingRiver.UPilot.Logger.LogToUnityConsole";

        /// <summary>
        /// 是否同时将日志输出到 Unity Editor Console。默认开启。
        /// </summary>
        public static bool LogToUnityConsole = EditorPrefs.GetBool(LogToUnityConsolePrefsKey, true);

        /// <summary>
        /// 当前线程是否为主线程。
        /// </summary>
        private static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        static Logger()
        {
            LogToUnityConsole = EditorPrefs.GetBool(LogToUnityConsolePrefsKey, true);

            MainThreadId = Thread.CurrentThread.ManagedThreadId;

            var process = System.Diagnostics.Process.GetCurrentProcess();
            CurrentProcessId = process.Id;
            CurrentProcessStartTime = process.StartTime;

            SessionMarkerPath = Path.Combine(ProjectLogsDirectory, ".session");

            if (IsNewSession())
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
            EditorPrefs.SetBool(LogToUnityConsolePrefsKey, enabled);
        }

        // ── 会话检测与初始化 ──────────────────────────────────────────────────

        private static bool IsNewSession()
        {
            try
            {
                if (!File.Exists(SessionMarkerPath)) return true;
                var content = File.ReadAllText(SessionMarkerPath, Encoding.UTF8);
                var parts = content.Split('|');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var pid))
                {
                    return pid != CurrentProcessId;
                }
            }
            catch
            {
                // 标记文件异常时视为新会话
            }
            return true;
        }

        private static void InitializeNewSession()
        {
            try
            {
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

                File.WriteAllText(LogFilePath, header.ToString() + Environment.NewLine, Encoding.UTF8);

                if (LogToUnityConsole)
                    UnityEngine.Debug.Log(header.ToString());

                _sessionPrepared = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilot] 无法准备日志文件: {ex.Message}");
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

        // ── 日志滚动 ──────────────────────────────────────────────────────────

        private const long MaxLogSize = 10L * 1024 * 1024; // 10 MB
        private const int MaxBackups = 2;

        private static void RotateLogIfNeeded()
        {
            if (!File.Exists(LogFilePath)) return;
            var info = new FileInfo(LogFilePath);
            if (info.Length < MaxLogSize) return;

            for (int i = MaxBackups; i > 0; i--)
            {
                var src = i == 1 ? LogFilePath : $"{LogFilePath}.{i - 1}";
                var dst = $"{LogFilePath}.{i}";
                if (File.Exists(dst))
                {
                    try { File.Delete(dst); } catch { }
                }
                if (File.Exists(src))
                {
                    try { File.Move(src, dst); } catch { }
                }
            }
        }

        // ── 核心写入 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将一行文本追加到日志文件。自动在行首添加毫秒级本地时间戳、线程/帧号、标签，立即落盘。
        /// </summary>
        /// <param name="line">日志正文（通常已包含 [LEVEL] message）。</param>
        /// <param name="tags">可选标签列表，会追加在行尾。</param>
        public static void AppendLine(string line, params string[] tags)
        {
            if (string.IsNullOrEmpty(line)) return;
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

            lock (FileLock)
            {
                try
                {
                    RotateLogIfNeeded();
                    File.AppendAllText(LogFilePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 避免日志失败反噬编辑器
                }
            }
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
                    UnityEngine.Debug.LogError($"[UPilot] 无法准备日志文件: {ex.Message}");
                    _sessionPrepared = true;
                }
            }
        }

        // ── Console 双写 ──────────────────────────────────────────────────────

        private static void WriteToUnityConsole(string line, LogType logType, params string[] tags)
        {
            if (!LogToUnityConsole) return;
            if (string.IsNullOrEmpty(line)) return;

            var sb = new StringBuilder();
            sb.Append(line);
            if (tags != null && tags.Length > 0)
            {
                sb.Append($" [{string.Join(",", tags)}]");
            }

            var message = sb.ToString();
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
            WriteToUnityConsole($"[INFO ] [{category,-8}] {message}", LogType.Log);
        }

        /// <summary>普通信息日志（带分类与标签）。</summary>
        public static void Log(string category, string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [{category,-8}] {message}", tags);
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
            var sign = isSend ? "SEND" : "RECV";
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [{category,-8}] [{sign}] {message}");
            WriteToUnityConsole($"[INFO ] [{category,-8}] [{sign}] {message}", LogType.Log);
        }

        /// <summary>兼容旧网络日志接口。</summary>
        public static void LogNetwork(string message, bool isSend = true)
        {
            var sign = isSend ? "SEND" : "RECV";
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO ] [NET     ] [{sign}] {message}");
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
                EnsureSessionLogFile();
                Directory.CreateDirectory(ProjectLogsDirectory);
                if (!File.Exists(LogFilePath))
                    File.WriteAllText(LogFilePath, "", Encoding.UTF8);
                EditorUtility.RevealInFinder(LogFilePath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("UPilot", $"无法打开日志: {ex.Message}", "确定");
            }
        }
    }
}
