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
    public sealed class AutomationReportCreateRequestV1
    {
        public string outputDirectory;
        public string runId;
        public long startedAtUtcMs;
    }

    [Serializable]
    public sealed class AutomationReportEventV1
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
    public sealed class AutomationReportPhaseV1
    {
        public string id;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationReportCaseV1
    {
        public string id;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationReportLogSummaryV1
    {
        public bool evidenceComplete;
        public int totalCount;
        public int allowedCount;
        public int blockedCount;
        public int ignoredCount;
        public long lostRecordCount;
    }

    [Serializable]
    public sealed class AutomationReportArtifactReferenceV1
    {
        public string kind;
        public string path;
        public bool external;
        public long bytes;
        public string sha256;
        public string diagnostic;
    }

    [Serializable]
    public sealed class AutomationReportSummaryV1
    {
        public int version = 1;
        public string runId;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public string outcome;
        public string failureSignature;
        public string detail;
        public AutomationReportPhaseV1[] phases = Array.Empty<AutomationReportPhaseV1>();
        public AutomationReportCaseV1[] cases = Array.Empty<AutomationReportCaseV1>();
        public AutomationReportLogSummaryV1 logSummary;
        public AutomationReportArtifactReferenceV1[] artifacts = Array.Empty<AutomationReportArtifactReferenceV1>();
    }

    [Serializable]
    public sealed class AutomationReportArtifactV1
    {
        public string kind;
        public string path;
        public string projectRelativePath;
        public long bytes;
        public string sha256;
    }

    /// <summary>Append-only evidence writer. It never resumes or drives business execution.</summary>
    public sealed class AutomationReportWriterV1
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private readonly string _projectRoot;
        private readonly string _directory;
        private readonly string _eventsPath;
        private readonly string _summaryPath;
        private readonly string _runId;
        private readonly long _startedAtUtcMs;
        private long _nextSequence;
        private string _completedSummaryJson;

        private AutomationReportWriterV1(
            string projectRoot,
            string directory,
            string runId,
            long startedAtUtcMs,
            long nextSequence,
            string completedSummaryJson)
        {
            _projectRoot = projectRoot;
            _directory = directory;
            _eventsPath = Path.Combine(directory, "events.jsonl");
            _summaryPath = Path.Combine(directory, "summary.json");
            _runId = runId;
            _startedAtUtcMs = startedAtUtcMs;
            _nextSequence = nextSequence;
            _completedSummaryJson = completedSummaryJson;
        }

        public string RunId => _runId;
        public string DirectoryPath => _directory;
        public bool IsComplete => !string.IsNullOrEmpty(_completedSummaryJson);

        public static AutomationReportWriterV1 Create(AutomationReportCreateRequestV1 request)
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
            var writer = new AutomationReportWriterV1(
                projectRoot,
                directory,
                runId,
                request.startedAtUtcMs > 0 ? request.startedAtUtcMs : UtcNowMs(),
                0,
                null);
            writer.Append(new AutomationReportEventV1
            {
                eventType = "report.created",
                timestampUtcMs = writer._startedAtUtcMs,
            });
            return writer;
        }

        public static AutomationReportWriterV1 OpenExisting(string outputDirectory, string expectedRunId)
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
                AutomationReportEventV1 item = ParseEvent(line);
                if (item.version != 1 || item.runId != expectedRunId || item.sequence != nextSequence)
                    throw new InvalidDataException("events.jsonl identity, version, or sequence is invalid.");
                if (startedAtUtcMs == 0 || item.timestampUtcMs < startedAtUtcMs)
                    startedAtUtcMs = item.timestampUtcMs;
                nextSequence++;
            }

            string summaryPath = Path.Combine(directory, "summary.json");
            string summaryJson = null;
            if (File.Exists(summaryPath))
            {
                summaryJson = File.ReadAllText(summaryPath, Encoding.UTF8);
                AutomationReportSummaryV1 summary = ParseSummary(summaryJson);
                if (summary.version != 1 || summary.runId != expectedRunId || summary.finishedAtUtcMs <= 0)
                    throw new InvalidDataException("summary.json identity, version, or terminal time is invalid.");
                startedAtUtcMs = summary.startedAtUtcMs;
            }
            if (startedAtUtcMs <= 0)
                throw new InvalidDataException("The report does not contain a valid start time.");
            return new AutomationReportWriterV1(
                projectRoot,
                directory,
                expectedRunId,
                startedAtUtcMs,
                nextSequence,
                summaryJson);
        }

        public AutomationReportEventV1 Append(AutomationReportEventV1 item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (IsComplete) throw new InvalidOperationException("A completed report is immutable.");
            if (string.IsNullOrWhiteSpace(item.eventType))
                throw new ArgumentException("Event type is required.", nameof(item));

            AutomationReportEventV1 stored = Clone(item);
            stored.version = 1;
            stored.runId = _runId;
            stored.sequence = _nextSequence;
            stored.timestampUtcMs = stored.timestampUtcMs > 0 ? stored.timestampUtcMs : UtcNowMs();
            string line = JsonUtility.ToJson(stored);
            File.AppendAllText(_eventsPath, line + "\n", Utf8NoBom);
            _nextSequence++;
            return Clone(stored);
        }

        public AutomationReportSummaryV1 Complete(AutomationReportSummaryV1 requested)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            if (string.IsNullOrWhiteSpace(requested.outcome))
                throw new ArgumentException("Terminal outcome is required.", nameof(requested));

            if (IsComplete)
            {
                AutomationReportSummaryV1 existing = ParseSummary(_completedSummaryJson);
                if (requested.finishedAtUtcMs > 0 && requested.finishedAtUtcMs != existing.finishedAtUtcMs)
                    throw new InvalidOperationException("A completed report cannot change terminal time.");
                AutomationReportSummaryV1 candidate = NormalizeSummary(requested, existing.finishedAtUtcMs);
                string candidateJson = JsonUtility.ToJson(candidate, true);
                if (!string.Equals(candidateJson, _completedSummaryJson, StringComparison.Ordinal))
                    throw new InvalidOperationException("A completed report cannot change terminal content.");
                return Clone(existing);
            }

            AutomationReportSummaryV1 summary = NormalizeSummary(requested, requested.finishedAtUtcMs > 0
                ? requested.finishedAtUtcMs
                : UtcNowMs());
            string json = JsonUtility.ToJson(summary, true);
            WriteNewFileAtomically(_summaryPath, json);
            _completedSummaryJson = json;
            return Clone(summary);
        }

        public AutomationReportArtifactV1[] GetArtifacts()
        {
            var artifacts = new List<AutomationReportArtifactV1>();
            AddArtifact(artifacts, "events", _eventsPath);
            if (File.Exists(_summaryPath)) AddArtifact(artifacts, "summary", _summaryPath);
            return artifacts.ToArray();
        }

        private AutomationReportSummaryV1 NormalizeSummary(AutomationReportSummaryV1 requested, long finishedAtUtcMs)
        {
            AutomationReportSummaryV1 summary = Clone(requested);
            summary.version = 1;
            summary.runId = _runId;
            summary.startedAtUtcMs = _startedAtUtcMs;
            summary.finishedAtUtcMs = finishedAtUtcMs;
            summary.phases ??= Array.Empty<AutomationReportPhaseV1>();
            summary.cases ??= Array.Empty<AutomationReportCaseV1>();
            summary.artifacts ??= Array.Empty<AutomationReportArtifactReferenceV1>();
            return summary;
        }

        private void AddArtifact(List<AutomationReportArtifactV1> artifacts, string kind, string path)
        {
            var info = new FileInfo(path);
            artifacts.Add(new AutomationReportArtifactV1
            {
                kind = kind,
                path = path,
                projectRelativePath = path.Substring(_projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                bytes = info.Length,
                sha256 = ComputeSha256(path),
            });
        }

        private static AutomationReportEventV1 ParseEvent(string json)
        {
            if (!LooksLikeJsonObject(json)) throw new InvalidDataException("Invalid JSONL event record.");
            try
            {
                AutomationReportEventV1 item = JsonUtility.FromJson<AutomationReportEventV1>(json);
                if (item == null || string.IsNullOrWhiteSpace(item.eventType))
                    throw new InvalidDataException("JSONL event is missing required fields.");
                return item;
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            {
                throw new InvalidDataException("Invalid JSONL event record.", ex);
            }
        }

        private static AutomationReportSummaryV1 ParseSummary(string json)
        {
            if (!LooksLikeJsonObject(json)) throw new InvalidDataException("Invalid summary JSON.");
            try
            {
                AutomationReportSummaryV1 item = JsonUtility.FromJson<AutomationReportSummaryV1>(json);
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
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Report directory must be inside the current Unity project.");
            return full;
        }

        private static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

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
