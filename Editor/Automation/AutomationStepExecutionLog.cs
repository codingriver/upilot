using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Best-effort daily diagnostics. Never owns execution, evidence or recovery.</summary>
    internal sealed class AutomationStepExecutionLog
    {
        private readonly UPilotDailyLogFile _file;
        private readonly string _directory;
        private readonly Action<string> _diagnostic;
        private readonly Func<DateTimeOffset> _writeClock;

        internal AutomationStepExecutionLog(string projectRoot = null, Action<string> diagnostic = null,
            Func<DateTimeOffset> writeClock = null, Func<int> retention = null)
        {
            _directory = UPilotOutputPaths.Resolve(UPilotOutputPaths.Root, projectRoot);
            _diagnostic = diagnostic;
            _file = new UPilotDailyLogFile(UPilotOutputPaths.Resolve(UPilotOutputPaths.StepLog, projectRoot), retention);
            _writeClock = writeClock ?? (() => DateTimeOffset.Now);
        }

        internal void Write(AutomationStepRun run, long atUtcMs, string eventType, string label, string fields)
        {
            try
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(atUtcMs);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(run.executionLogTimeZoneId);
                string line = "[" + TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)
                    + "] runId=" + Quote(run.runId) + " operationId=" + Quote(run.operationId)
                    + " " + label + " " + eventType + (string.IsNullOrEmpty(fields) ? "" : " " + fields) + "\n";
                _file.Append(line, _writeClock().ToLocalTime().Date,
                    (operation, path, ex) => Report(run, operation + " path=" + path, ex));
            }
            catch (Exception ex) { Report(run, "write", ex); }
        }

        internal void Report(AutomationStepRun run, string operation, Exception ex)
        {
            try
            {
                string message = "runId=" + Quote(run.runId) + " operationId=" + Quote(run.operationId)
                    + " 执行日志不完整 operation=" + operation;
                if (_diagnostic != null) _diagnostic(message + " error=" + Quote(ex.ToString()));
                else Logger.ReportFailure("STEP", message, ex, run.runId, fingerprint: "execution-log|" + operation);
            }
            catch (Exception diagnosticError) { UPilotDailyLogFile.ReportError(operation, _directory, ex); UPilotDailyLogFile.ReportError("diagnostic", _directory, diagnosticError); }
        }

        internal static void RequireSafePath(string path) => UPilotDailyLogFile.RequireSafePath(path);

        internal static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '\\': text.Append("\\\\"); break;
                    case '"': text.Append("\\\""); break;
                    case '\r': text.Append("\\r"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (char.IsControl(c) || c == '\u2028' || c == '\u2029') text.Append("\\u").Append(((int)c).ToString("x4"));
                        else text.Append(c);
                        break;
                }
            }
            return text.Append('"').ToString();
        }

        internal static string Elapsed(long start, long finish) => start > 0 && finish >= start
            ? ((finish - start) / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + "s" : "未知";

        internal static string Timestamp(AutomationStepRun run, long at)
        {
            if (at <= 0) return "未知";
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at),
                TimeZoneInfo.FindSystemTimeZoneById(run.executionLogTimeZoneId))
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }
    }
}
