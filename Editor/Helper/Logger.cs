using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Codingriver
{
    /// <summary>
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
        private static readonly string SessionMarkerPath = "./log/.session";

        public static string LogFilePath = "./log/UnityUIFlow.log";

        /// <summary>
        /// 最低输出日志级别。低于此级别的日志将被忽略。默认 Debug。
        /// </summary>
        public static LogLevel MinLevel = LogLevel.Debug;

        /// <summary>
        /// 是否同时将日志输出到 Unity Editor Console。默认开启。
        /// </summary>
        public static bool LogToUnityConsole = true;

        /// <summary>
        /// 当前线程是否为主线程。
        /// </summary>
        private static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        static Logger()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;

            var process = System.Diagnostics.Process.GetCurrentProcess();
            CurrentProcessId = process.Id;
            CurrentProcessStartTime = process.StartTime;
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (IsNewSession())
            {
                InitializeNewSession();
            }
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
                File.WriteAllText(SessionMarkerPath, $"{CurrentProcessId}|{CurrentProcessStartTime:O}", Encoding.UTF8);

                var header = new StringBuilder();
                header.AppendLine("════════════════════════════════════════════════════════════");
                header.AppendLine($"Session Start");
                header.AppendLine($"Timestamp  : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                header.AppendLine($"Unity Ver  : {Application.unityVersion}");
                header.AppendLine($"Platform   : {Application.platform}");
                header.AppendLine($"Process Id : {CurrentProcessId}");
                header.AppendLine($"MainThreadId : {MainThreadId}");
                header.AppendLine("════════════════════════════════════════════════════════════");

                File.WriteAllText(LogFilePath, header.ToString() + Environment.NewLine, Encoding.UTF8);
                UnityEngine.Debug.Log(header.ToString());
            }
            catch
            {
                // 避免初始化失败阻塞编辑器
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
        private static void AppendLine(string line, params string[] tags)
        {
            if (string.IsNullOrEmpty(line)) return;

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

        // ── 带标签的写入接口 ──────────────────────────────────────────────────

        /// <summary>调试日志（带标签）。格式：[timestamp] [frame] [DEBUG] message [tags]</summary>
        public static void Debug(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Debug)) return;
            AppendLine($"[DEBUG] {message}", tags);
            WriteToUnityConsole($"[DEBUG] {message}", LogType.Log, tags);
        }

        /// <summary>普通信息日志（带标签）。格式：[timestamp] [frame] [INFO] message [tags]</summary>
        public static void Log(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO] {message}", tags);
            WriteToUnityConsole($"[INFO] {message}", LogType.Log, tags);
        }
        public static void LogUI(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[UI] {message}", tags);
            WriteToUnityConsole($"[UI] {message}", LogType.Log, tags);
        }
        /// <summary>警告日志（带标签）。格式：[timestamp] [frame] [WARN] message [tags]</summary>
        public static void LogWarning(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            AppendLine($"[WARN] {message}", tags);
            WriteToUnityConsole($"[WARN] {message}", LogType.Warning, tags);
        }

        /// <summary>错误日志（带标签）。格式：[timestamp] [frame] [ERROR] message [tags]</summary>
        public static void LogError(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            AppendLine($"[ERROR] {message}", tags);
            WriteToUnityConsole($"[ERROR] {message}", LogType.Error, tags);
        }

        /// <summary>异常日志（带标签）。</summary>
        public static void LogException(Exception ex, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            var line = $"[ERROR] Exception: {ex}";
            AppendLine(line, tags);
            WriteToUnityConsole(line, LogType.Exception, tags);
        }

        // ── 网络通信日志 ───────────────────────────────────────────────────────

        public static void LogNetSend(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO] [SEND] {message}", tags);
            WriteToUnityConsole($"[INFO] [SEND] {message}", LogType.Log, tags);
        }

        public static void LogNetRecv(string message, params string[] tags)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            AppendLine($"[INFO] [RECV] {message}", tags);
            WriteToUnityConsole($"[INFO] [RECV] {message}", LogType.Log, tags);
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
    }
}
