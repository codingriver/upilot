using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationReportCreateRequest
    {
        public string outputDirectory;
        public string runId;
        public long startedAtUtcMs;
    }

    [Serializable]
    public sealed class AutomationReportEvent
    {
        public int version = 1;
        public string runId;
        public long sequence = -1;
        public long timestampUtcMs;
        public string eventType;
        public string phaseId;
        public string caseId;
        public string outcome;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationReportPhase
    {
        public string id;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationReportCase
    {
        public string id;
        public string stepId;
        public string phaseId;
        public string stage;
        public long startedAtUtcMs;
        public long cleanupStartedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string errorCode;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationReportLogSummary
    {
        public string sessionId;
        public long fromSequenceInclusive;
        public long toSequenceExclusive;
        public bool policyValid;
        public bool passed;
        public bool evidenceComplete;
        public int totalCount;
        public int allowedCount;
        public int blockedCount;
        public int ignoredCount;
        public long lostRecordCount;
        public AutomationReportLogSample[] blockingSamples = Array.Empty<AutomationReportLogSample>();
        public int omittedBlockingCount;
        public AutomationDiagnostic[] diagnostics = Array.Empty<AutomationDiagnostic>();
        public int omittedDiagnosticCount;
        public bool diagnosticsTextTruncated;
    }

    [Serializable]
    public sealed class AutomationReportLogSample
    {
        public long sequence;
        public string level;
        public string instanceId;
        public string phaseId;
        public string ruleId;
        public string disposition;
        public string reason;
        public string fingerprint;
        public string message;
        public bool hasStackTrace;
        public bool textTruncated;
    }

    [Serializable]
    public sealed class AutomationReportArtifactReference
    {
        public string kind;
        public string instanceId;
        public string path;
        public bool external;
        public long bytes;
        public string sha256;
        public string diagnostic;
    }

    [Serializable]
    public sealed class AutomationReportSummary
    {
        public int version = 1;
        public int exportVersion;
        public string runId;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string failureSignature;
        public string detail;
        public AutomationReportPhase[] phases = Array.Empty<AutomationReportPhase>();
        public AutomationReportCase[] cases = Array.Empty<AutomationReportCase>();
        public AutomationReportLogSummary logSummary;
        public AutomationReportArtifactReference[] artifacts = Array.Empty<AutomationReportArtifactReference>();
    }

    [Serializable]
    public sealed class AutomationReportArtifact
    {
        public string kind;
        public string instanceId;
        public string path;
        public string projectRelativePath;
        public long bytes;
        public string sha256;
    }

    /// <summary>Append-only evidence writer. It never resumes or drives business execution.</summary>
    public sealed class AutomationReportWriter
    {
        internal const string ConsolePolicyFileName = "console-policy.json";
        internal const string TextReportFileName = "report.txt";
        internal const string TimingFileName = "timing.csv";
        internal const int BlockingSampleLimit = 10;
        internal const int DiagnosticLimit = 10;
        internal const int MessageLimit = 2048;
        private const int LabelLimit = 256;
        [Serializable] private sealed class ConsolePolicyDocument
        {
            public int version = 1;
            public string runId;
            public string sessionId;
            public long fromSequenceInclusive;
            public long toSequenceExclusive;
            public AutomationLogPolicyResult policy;
        }
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private readonly string _projectRoot;
        private readonly string _directory;
        private readonly string _eventsPath;
        private readonly string _summaryPath;
        private readonly string _runId;
        private readonly long _startedAtUtcMs;
        private long _nextSequence;
        private string _completedSummaryJson;
        private byte[] _completedSummaryBytes;

        private AutomationReportWriter(
            string projectRoot,
            string directory,
            string runId,
            long startedAtUtcMs,
            long nextSequence,
            string completedSummaryJson,
            byte[] completedSummaryBytes)
        {
            _projectRoot = projectRoot;
            _directory = directory;
            _eventsPath = Path.Combine(directory, "events.jsonl");
            _summaryPath = Path.Combine(directory, "summary.json");
            _runId = runId;
            _startedAtUtcMs = startedAtUtcMs;
            _nextSequence = nextSequence;
            _completedSummaryJson = completedSummaryJson;
            _completedSummaryBytes = completedSummaryBytes;
        }

        public string RunId => _runId;
        public string DirectoryPath => _directory;
        public bool IsComplete => !string.IsNullOrEmpty(_completedSummaryJson);

        public static AutomationReportWriter Create(AutomationReportCreateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string projectRoot = ProjectRoot();
            string directory = ResolveContainedDirectory(projectRoot, request.outputDirectory);
            string runId = string.IsNullOrWhiteSpace(request.runId) ? Guid.NewGuid().ToString("N") : request.runId.Trim();
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException("Report directory already contains files.");
            Directory.CreateDirectory(directory);
            string eventsPath = Path.Combine(directory, "events.jsonl");
            File.WriteAllText(eventsPath, string.Empty, Utf8NoBom);
            var writer = new AutomationReportWriter(
                projectRoot,
                directory,
                runId,
                request.startedAtUtcMs > 0 ? request.startedAtUtcMs : UtcNowMs(),
                0,
                null,
                null);
            writer.Append(new AutomationReportEvent
            {
                eventType = "report.created",
                timestampUtcMs = writer._startedAtUtcMs,
            });
            return writer;
        }

        public static AutomationReportWriter OpenExisting(string outputDirectory, string expectedRunId)
        {
            string projectRoot = ProjectRoot();
            string directory = ResolveContainedDirectory(projectRoot, outputDirectory);
            string eventsPath = Path.Combine(directory, "events.jsonl");
            if (!File.Exists(eventsPath))
                throw new InvalidDataException("events.jsonl is missing.");
            if (string.IsNullOrWhiteSpace(expectedRunId))
                throw new ArgumentException("Expected run identity is required.", nameof(expectedRunId));

            long nextSequence = 0;
            long startedAtUtcMs = 0;
            foreach (string line in File.ReadLines(eventsPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    throw new InvalidDataException("events.jsonl contains an empty record.");
                AutomationReportEvent item = ParseEvent(line);
                if (item.version != 1 || item.runId != expectedRunId || item.sequence != nextSequence)
                    throw new InvalidDataException("events.jsonl identity, version, or sequence is invalid.");
                if (startedAtUtcMs == 0 || item.timestampUtcMs < startedAtUtcMs)
                    startedAtUtcMs = item.timestampUtcMs;
                nextSequence++;
            }

            string summaryPath = Path.Combine(directory, "summary.json");
            string summaryJson = null;
            byte[] summaryBytes = null;
            if (File.Exists(summaryPath))
            {
                RequireRegularArtifactPath(projectRoot, summaryPath);
                summaryBytes = File.ReadAllBytes(summaryPath);
                // Decode and freeze the same read, preserving any historical encoding preamble.
                using (var stream = new MemoryStream(summaryBytes, false))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    summaryJson = reader.ReadToEnd();
                AutomationReportSummary summary = ParseSummary(summaryJson);
                if (summary.version != 1 || summary.runId != expectedRunId || summary.finishedAtUtcMs <= 0)
                    throw new InvalidDataException("summary.json identity, version, or terminal time is invalid.");
                startedAtUtcMs = summary.startedAtUtcMs;
            }
            if (startedAtUtcMs <= 0)
                throw new InvalidDataException("The report does not contain a valid start time.");
            return new AutomationReportWriter(
                projectRoot,
                directory,
                expectedRunId,
                startedAtUtcMs,
                nextSequence,
                summaryJson,
                summaryBytes);
        }

        public AutomationReportEvent Append(AutomationReportEvent item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (IsComplete) throw new InvalidOperationException("A completed report is immutable.");
            if (string.IsNullOrWhiteSpace(item.eventType))
                throw new ArgumentException("Event type is required.", nameof(item));

            AutomationReportEvent stored = Clone(item);
            stored.version = 1;
            stored.runId = _runId;
            stored.sequence = _nextSequence;
            stored.timestampUtcMs = stored.timestampUtcMs > 0 ? stored.timestampUtcMs : UtcNowMs();
            string line = JsonUtility.ToJson(stored);
            File.AppendAllText(_eventsPath, line + "\n", Utf8NoBom);
            _nextSequence++;
            return Clone(stored);
        }

        public AutomationReportSummary Complete(AutomationReportSummary requested)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            if (string.IsNullOrWhiteSpace(requested.outcome))
                throw new ArgumentException("Terminal outcome is required.", nameof(requested));

            if (IsComplete)
            {
                AutomationReportSummary existing = ParseSummary(_completedSummaryJson);
                if (requested.finishedAtUtcMs > 0 && requested.finishedAtUtcMs != existing.finishedAtUtcMs)
                    throw new InvalidOperationException("A completed report cannot change terminal time.");
                AutomationReportSummary candidate = NormalizeSummary(requested, existing.finishedAtUtcMs, existing.exportVersion);
                string candidateJson = JsonUtility.ToJson(candidate, true);
                string existingJson = JsonUtility.ToJson(NormalizeSummary(existing, existing.finishedAtUtcMs, existing.exportVersion), true);
                if (!string.Equals(candidateJson, existingJson, StringComparison.Ordinal))
                    throw new InvalidOperationException("A completed report cannot change terminal content.");
                VerifyExports(existing);
                return Clone(existing);
            }

            AutomationReportSummary summary = NormalizeSummary(requested, requested.finishedAtUtcMs > 0
                ? requested.finishedAtUtcMs
                : UtcNowMs());
            string json = JsonUtility.ToJson(summary, true);
            // summary.json commits the export set. Partial exports are never published as terminal artifacts.
            WriteExport(TextReportFileName, AutomationReportExport.Text(summary));
            WriteExport(TimingFileName, AutomationReportExport.Timing(summary));
            WriteNewFileAtomically(_summaryPath, json);
            _completedSummaryJson = json;
            _completedSummaryBytes = Utf8NoBom.GetBytes(json);
            return Clone(summary);
        }

        public AutomationReportArtifact[] GetArtifacts()
        {
            var artifacts = new List<AutomationReportArtifact>();
            AddArtifact(artifacts, "events", _eventsPath);
            if (File.Exists(_summaryPath)) AddArtifact(artifacts, "summary", _summaryPath);
            if (IsComplete)
            {
                var summary = ParseSummary(_completedSummaryJson);
                VerifyExports(summary);
                if (summary.exportVersion == 1)
                {
                    AddArtifact(artifacts, "report", Path.Combine(_directory, TextReportFileName));
                    AddArtifact(artifacts, "timing", Path.Combine(_directory, TimingFileName));
                }
            }
            string policyPath = Path.Combine(_directory, ConsolePolicyFileName);
            if (File.Exists(policyPath)) AddArtifact(artifacts, "consolePolicy", policyPath);
            return artifacts.ToArray();
        }

        internal AutomationReportLogSummary WriteConsolePolicy(string sessionId, long from, long to,
            AutomationLogPolicyResult policy, long lostRecordCount)
        {
            if (IsComplete) throw new InvalidOperationException("A completed report is immutable.");
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (string.IsNullOrEmpty(sessionId) || from < 0 || to < from)
                throw new ArgumentException("An exact Capture identity and half-open range are required.");
            string path = Path.Combine(_directory, ConsolePolicyFileName);
            string json = JsonUtility.ToJson(new ConsolePolicyDocument
            { runId = _runId, sessionId = sessionId, fromSequenceInclusive = from, toSequenceExclusive = to, policy = policy });
            // Finalizing can resume after a domain reload; a previous fixed-range result is never overwritten.
            if (File.Exists(path))
            {
                GetArtifactMetadata("consolePolicy", path);
                if (File.ReadAllText(path, Encoding.UTF8) != json)
                    throw new InvalidDataException("Console policy evidence conflicts with the previously saved result.");
            }
            else WriteNewFileAtomically(path, json);

            var samples = policy.classifications.Where(c => c.disposition == "blocked")
                .Take(BlockingSampleLimit).Select(c =>
                {
                    var sample = new AutomationReportLogSample
                    { sequence = c.record.sequence, hasStackTrace = !string.IsNullOrEmpty(c.record.stackTrace) };
                    sample.level = Limit(c.record.logType, LabelLimit, ref sample.textTruncated);
                    sample.instanceId = Limit(c.context?.caseId, LabelLimit, ref sample.textTruncated);
                    sample.phaseId = Limit(c.context?.phaseId, LabelLimit, ref sample.textTruncated);
                    sample.ruleId = Limit(c.ruleId, LabelLimit, ref sample.textTruncated);
                    sample.disposition = Limit(c.disposition, LabelLimit, ref sample.textTruncated);
                    sample.reason = Limit(c.reason, LabelLimit, ref sample.textTruncated);
                    sample.fingerprint = Limit(c.fingerprint, LabelLimit, ref sample.textTruncated);
                    sample.message = Limit(c.record.message, MessageLimit, ref sample.textTruncated);
                    return sample;
                }).ToArray();
            bool diagnosticTextTruncated = false;
            var diagnostics = policy.diagnostics.Take(DiagnosticLimit).Select(d =>
            {
                return new AutomationDiagnostic
                {
                    code = Limit(d.code, LabelLimit, ref diagnosticTextTruncated),
                    message = Limit(d.message, MessageLimit, ref diagnosticTextTruncated),
                    severity = Limit(d.severity, LabelLimit, ref diagnosticTextTruncated),
                    subjectId = Limit(d.subjectId, LabelLimit, ref diagnosticTextTruncated),
                    index = d.index,
                };
            }).ToArray();
            return new AutomationReportLogSummary
            {
                sessionId = sessionId, fromSequenceInclusive = from, toSequenceExclusive = to,
                policyValid = policy.policyValid, passed = policy.passed, evidenceComplete = policy.evidenceComplete,
                totalCount = policy.totalCount, blockedCount = policy.blockedCount,
                allowedCount = policy.allowedCount, ignoredCount = policy.ignoredCount, lostRecordCount = lostRecordCount,
                blockingSamples = samples, omittedBlockingCount = Math.Max(0, policy.blockedCount - samples.Length),
                diagnostics = diagnostics, omittedDiagnosticCount = Math.Max(0, policy.diagnostics.Count - diagnostics.Length),
                diagnosticsTextTruncated = diagnosticTextTruncated,
            };
        }

        private static string Limit(string text, int limit, ref bool truncated)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= limit) return text;
            truncated = true;
            int length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
            return text.Substring(0, length);
        }

        private AutomationReportSummary NormalizeSummary(AutomationReportSummary requested, long finishedAtUtcMs, int exportVersion = 1)
        {
            AutomationReportSummary summary = Clone(requested);
            summary.version = 1;
            summary.exportVersion = exportVersion;
            summary.runId = _runId;
            summary.startedAtUtcMs = _startedAtUtcMs;
            summary.finishedAtUtcMs = finishedAtUtcMs;
            summary.phases ??= Array.Empty<AutomationReportPhase>();
            summary.cases ??= Array.Empty<AutomationReportCase>();
            summary.artifacts ??= Array.Empty<AutomationReportArtifactReference>();
            return summary;
        }

        private void WriteExport(string fileName, string content)
        {
            string path = Path.Combine(_directory, fileName);
            if (File.Exists(path)) VerifyExport(path, content);
            else WriteNewFileAtomically(path, content);
        }

        private void VerifyExports(AutomationReportSummary summary)
        {
            VerifyExport(_summaryPath, _completedSummaryBytes);
            if (summary.exportVersion == 0) return; // Historical reports are not rewritten or backfilled.
            if (summary.exportVersion != 1) throw new InvalidDataException("Unknown report export version.");
            VerifyExport(Path.Combine(_directory, TextReportFileName), AutomationReportExport.Text(summary));
            VerifyExport(Path.Combine(_directory, TimingFileName), AutomationReportExport.Timing(summary));
        }

        private void VerifyExport(string path, string content)
        {
            VerifyExport(path, Utf8NoBom.GetBytes(content));
        }

        private void VerifyExport(string path, byte[] expectedBytes)
        {
            RequireRegularArtifactPath(_projectRoot, path);
            if (!File.ReadAllBytes(path).SequenceEqual(expectedBytes))
                throw new InvalidDataException("Report export differs from the frozen summary: " + Path.GetFileName(path));
        }

        private void AddArtifact(List<AutomationReportArtifact> artifacts, string kind, string path)
        {
            artifacts.Add(GetArtifactMetadata(kind, path));
        }

        /// <summary>Size and hash for an existing project-contained artifact, without changing the report.</summary>
        public static AutomationReportArtifact GetArtifactMetadata(string kind, string path)
        {
            string projectRoot = ProjectRoot();
            path = ResolveContainedDirectory(projectRoot, path);
            RequireRegularArtifactPath(projectRoot, path);
            var info = new FileInfo(path);
            long modified = info.LastWriteTimeUtc.Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            using var sha = SHA256.Create();
            string digest = string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            info.Refresh();
            RequireRegularArtifactPath(projectRoot, path);
            if (!info.Exists || stream.Length != length || info.Length != length || info.LastWriteTimeUtc.Ticks != modified)
                throw new IOException("Artifact changed while its metadata was being read.");
            return new AutomationReportArtifact
            {
                kind = kind,
                path = path,
                projectRelativePath = path.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                bytes = length,
                sha256 = digest,
            };
        }

        private static void RequireRegularArtifactPath(string projectRoot, string path)
        {
            // A lexical project prefix alone does not contain a symlink/junction target.
            for (string current = path; !string.Equals(current, projectRoot, PathComparison); current = Path.GetDirectoryName(current))
            {
                if (string.IsNullOrEmpty(current)) throw new IOException("Artifact path has no project root.");
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Artifact paths cannot traverse a symbolic link or junction.");
                if (current == path && (attributes & FileAttributes.Directory) != 0)
                    throw new IOException("Artifact must be a regular file.");
            }
        }

        private static AutomationReportEvent ParseEvent(string json)
        {
            if (!LooksLikeJsonObject(json)) throw new InvalidDataException("Invalid JSONL event record.");
            try
            {
                AutomationReportEvent item = JsonUtility.FromJson<AutomationReportEvent>(json);
                if (item == null || string.IsNullOrWhiteSpace(item.eventType))
                    throw new InvalidDataException("JSONL event is missing required fields.");
                return item;
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            {
                throw new InvalidDataException("Invalid JSONL event record.", ex);
            }
        }

        private static AutomationReportSummary ParseSummary(string json)
        {
            if (!LooksLikeJsonObject(json)) throw new InvalidDataException("Invalid summary JSON.");
            try
            {
                AutomationReportSummary item = JsonUtility.FromJson<AutomationReportSummary>(json);
                if (item == null || string.IsNullOrWhiteSpace(item.outcome))
                    throw new InvalidDataException("Summary is missing required fields.");
                return item;
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            {
                throw new InvalidDataException("Invalid summary JSON.", ex);
            }
        }

        private static bool LooksLikeJsonObject(string json)
        {
            string value = json?.Trim();
            return !string.IsNullOrEmpty(value) && value[0] == '{' && value[value.Length - 1] == '}';
        }

        private static string ResolveContainedDirectory(string projectRoot, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new ArgumentException("A project-contained output directory is required.", nameof(requested));
            string full = Path.GetFullPath(Path.IsPathRooted(requested)
                ? requested
                : Path.Combine(projectRoot, requested));
            string prefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison))
                throw new InvalidOperationException("Report directory must be inside the current Unity project.");
            return full;
        }

        private static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));

        private static void WriteNewFileAtomically(string path, string content)
        {
            if (File.Exists(path))
                throw new InvalidOperationException("Terminal summary already exists.");
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content, Utf8NoBom);
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
