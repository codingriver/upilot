using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Deterministic exports of frozen report data, without invoking project code.</summary>
    internal static class AutomationReportExport
    {
        internal static string Text(AutomationReportSummary summary)
        {
            var text = new StringBuilder("UPilot Automation Report\r\n");
            void Field(string key, string value) => text.Append(key).Append(": ").Append(Line(value)).Append("\r\n");
            Field("Run", summary.runId);
            Field("Outcome", summary.outcome);
            Field("Failure code", summary.failureSignature);
            Field("Detail", summary.detail);
            Field("Started UTC ms", Number(summary.startedAtUtcMs));
            Field("Finished UTC ms", Number(summary.finishedAtUtcMs));
            Field("Workflow elapsed seconds", Seconds(summary.startedAtUtcMs, summary.finishedAtUtcMs));
            // JsonUtility materializes null objects; only an exact session identifies captured evidence.
            if (string.IsNullOrEmpty(summary.logSummary?.sessionId)) Field("Console", "Not captured; not validated");
            else
            {
                var log = summary.logSummary;
                Field("Console session", log.sessionId);
                Field("Console range", "[" + Number(log.fromSequenceInclusive) + "," + Number(log.toSequenceExclusive) + ")");
                Field("Console policy passed", log.passed ? "true" : "false");
                Field("Console evidence complete", log.evidenceComplete ? "true" : "false");
                Field("Console blocked / allowed / ignored / lost", Number(log.blockedCount) + " / "
                    + Number(log.allowedCount) + " / " + Number(log.ignoredCount) + " / " + Number(log.lostRecordCount));
            }
            text.Append("\r\nSteps / Cases\r\n");
            foreach (var item in summary.cases ?? Array.Empty<AutomationReportCase>())
                text.Append(Line(item.id)).Append(" | ").Append(Line(item.stepId)).Append(" | ")
                    .Append(Line(item.phaseId)).Append(" | ").Append(Line(item.outcome)).Append(" | ")
                    .Append(Seconds(item.startedAtUtcMs, item.finishedAtUtcMs)).Append(" s | ")
                    .Append(Line(item.errorCode)).Append(" | ").Append(Line(item.detail)).Append("\r\n");
            text.Append("\r\nEvidence attachments\r\n");
            foreach (var item in summary.artifacts ?? Array.Empty<AutomationReportArtifactReference>())
                text.Append(Line(item.instanceId)).Append(" | ").Append(Line(item.kind)).Append(" | ")
                    .Append(Line(item.path)).Append(" | ").Append(item.external ? "external" : Number(item.bytes))
                    .Append(" | ").Append(Line(item.sha256)).Append(" | ").Append(Line(item.diagnostic)).Append("\r\n");
            text.Append("\r\nBusiness assertions and metrics remain in project attachments. ")
                .Append("Step success is not proof of Operation-owned Capture shutdown.\r\n");
            return text.ToString();
        }

        internal static string Timing(AutomationReportSummary summary)
        {
            var text = new StringBuilder("instanceId,stepId,phase,stage,outcome,startedAtUtcMs,cleanupStartedAtUtcMs,finishedAtUtcMs,executionElapsedSec,cleanupElapsedSec,totalElapsedSec,errorCode\r\n");
            foreach (var item in summary.cases ?? Array.Empty<AutomationReportCase>())
            {
                long executionEnd = item.cleanupStartedAtUtcMs > 0 ? item.cleanupStartedAtUtcMs : item.finishedAtUtcMs;
                var values = new[]
                {
                    item.id, item.stepId, item.phaseId, item.stage, item.outcome,
                    Timestamp(item.startedAtUtcMs), Timestamp(item.cleanupStartedAtUtcMs), Timestamp(item.finishedAtUtcMs),
                    Seconds(item.startedAtUtcMs, executionEnd), Seconds(item.cleanupStartedAtUtcMs, item.finishedAtUtcMs),
                    Seconds(item.startedAtUtcMs, item.finishedAtUtcMs), item.errorCode
                };
                text.Append(string.Join(",", values.Select(Csv))).Append("\r\n");
            }
            return text.ToString();
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Timestamp(long value) => value > 0 ? Number(value) : "";
        private static string Seconds(long start, long finish) => start > 0 && finish >= start
            ? ((finish - start) / 1000d).ToString("0.000", CultureInfo.InvariantCulture) : "";
        private static string Line(string value) => (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        private static string Csv(string value)
        {
            value ??= "";
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0
                || value.StartsWith("\t", StringComparison.Ordinal) || value.StartsWith("\r", StringComparison.Ordinal))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
