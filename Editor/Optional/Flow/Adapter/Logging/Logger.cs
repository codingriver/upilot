using System;
using CoreLogger = CodingRiver.UPilot.Logger;

namespace Codingriver
{
    /// <summary>
    /// UPilot Flow 的兼容日志门面。所有日志统一交给核心 Logger，继承同一套 Console 开关、
    /// 详细/通信日志策略、会话识别、文件滚动、长度限制与文件异常诊断。
    /// </summary>
    public static class Logger
    {
        public static string LogFilePath
        {
            get => CoreLogger.LogFilePath;
            set
            {
                if (!string.Equals(value, CoreLogger.LogFilePath, StringComparison.OrdinalIgnoreCase))
                    CoreLogger.LogWarning("UPilot.Flow", "旧版自定义日志路径已忽略；UPilot Flow 统一写入 " + CoreLogger.LogFilePath);
            }
        }

        public static LogLevel MinLevel
        {
            get => (LogLevel)(int)CoreLogger.MinLevel;
            set => CoreLogger.MinLevel = (CoreLogger.LogLevel)(int)value;
        }

        public static bool LogToUnityConsole
        {
            get => CoreLogger.LogToUnityConsole;
            set => CoreLogger.SetLogToUnityConsole(value);
        }

        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3
        }

        public static void Debug(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Debug) return;
            CoreLogger.Log("UPilot.Flow", "[DEBUG] " + message, tags);
        }

        public static void Log(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Info) return;
            CoreLogger.Log("UPilot.Flow", message, tags);
        }

        public static void LogUI(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Info) return;
            CoreLogger.Log("UPilot.Flow", "[UI] " + message, tags);
        }

        public static void LogWarning(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Warning) return;
            CoreLogger.LogWarning("UPilot.Flow", message, tags);
        }

        public static void LogError(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Error) return;
            CoreLogger.LogError("UPilot.Flow", message, tags);
        }

        public static void LogException(Exception ex, params string[] tags)
        {
            if (MinLevel > LogLevel.Error) return;
            CoreLogger.LogError("UPilot.Flow", "Exception: " + ex, tags);
        }

        public static void LogNetSend(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Info) return;
            CoreLogger.LogNetwork("UPilot.Flow", AppendTags(message, tags), true);
        }

        public static void LogNetRecv(string message, params string[] tags)
        {
            if (MinLevel > LogLevel.Info) return;
            CoreLogger.LogNetwork("UPilot.Flow", AppendTags(message, tags), false);
        }

        public static string TruncatePayload(string json, int maxLen = 800)
        {
            return CoreLogger.TruncatePayload(json, maxLen);
        }

        private static string AppendTags(string message, string[] tags)
        {
            return tags == null || tags.Length == 0
                ? message
                : message + " [" + string.Join(",", tags) + "]";
        }
    }
}
