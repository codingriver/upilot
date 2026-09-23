using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationSnapshotRecord
    {
        public string instanceId, evidenceKey, requestKey, arguments, snapshotId = "";
        public string status = "Running", errorCode = "", message = "";
        public long requestedAt, cancelRequestedAt;
        public bool cancelIntent, unresolved, recorded;
    }

    /// <summary>Run-owned Snapshot intents; independent of business checkpoints and Step cleanup.</summary>
    internal sealed class AutomationSnapshotEvidence
    {
        [Serializable] internal sealed class Options
        {
            public SnapshotCapturePayload capture = new()
            {
                targets = new[] { new SnapshotTargetRequestPayload { targetId = "game", kind = "gameView" } }
            };
            public double timeoutSeconds = 3;
            public double cancelGraceSeconds = 2;
            public bool required = true;
        }
        private readonly AutomationStepRun _run;
        private readonly Action _save;
        private readonly Func<long> _clock;
        internal AutomationSnapshotEvidence(AutomationStepRun run, Action save, Func<long> clock)
        { _run = run; _save = save; _clock = clock; _run.snapshots ??= new(); }
        internal static Options Parse(string arguments)
        {
            if (arguments == null) throw new FormatException("Snapshot arguments must be a string.");
            var options = new Options();
            if (arguments != "")
            {
                var root = AutomationStepJsonCodec.ParseObject(arguments);
                ValidateFields(root, typeof(Options));
                options = JsonUtility.FromJson<Options>(arguments);
            }
            if (options.capture == null || !AutomationStepJson.Finite(options.timeoutSeconds)
                || options.timeoutSeconds <= 0 || options.timeoutSeconds > 3
                || !AutomationStepJson.Finite(options.cancelGraceSeconds)
                || options.cancelGraceSeconds <= 0 || options.cancelGraceSeconds > 2)
                throw new FormatException("Snapshot wait must be in (0,3] seconds and cancel grace in (0,2].");
            if (!string.IsNullOrEmpty(options.capture.requestKey) || !string.IsNullOrEmpty(options.capture.outputDirectory))
                throw new FormatException("Snapshot requestKey and outputDirectory belong to the executor.");
            if (options.capture.capturePolicy == null || !options.capture.capturePolicy.requireVerifiedPixels
                || options.capture.capturePolicy.allowOcclusionSensitive || options.capture.capturePolicy.allowStaleFrame
                || options.capture.capturePolicy.allowFallback)
                throw new FormatException("Automation evidence requires verified, current, non-occluded pixels without fallback.");
            UPilotSnapshotService.ValidateRequest(options.capture);
            if (options.capture.targets.Any(t => t == null
                || !new[] { "gameView", "camera", "sceneView", "editorWindow" }.Contains(t.kind)))
                throw new FormatException("Unsupported Snapshot target kind.");
            return options;
        }
        private static void ValidateFields(XElement value, Type type)
        {
            string tokenType = (string)value.Attribute("type");
            if (type == typeof(string) || type == typeof(bool) || type.IsPrimitive)
            {
                string expected = type == typeof(string) ? "string" : type == typeof(bool) ? "boolean" : "number";
                if (tokenType != expected) throw new FormatException(value.Name + " must be " + expected + ".");
                if (expected == "number")
                {
                    double number = double.Parse(value.Value, CultureInfo.InvariantCulture);
                    if (!AutomationStepJson.Finite(number) || (type == typeof(int) &&
                        (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)))
                        throw new FormatException("Invalid number: " + value.Name);
                }
                return;
            }
            if (type.IsArray)
            {
                if (tokenType != "array") throw new FormatException(value.Name + " must be an array.");
                foreach (var item in value.Elements()) ValidateFields(item, type.GetElementType());
                return;
            }
            if (tokenType != "object") throw new FormatException(value.Name + " must be an object.");
            foreach (var field in value.Elements())
            {
                var member = type.GetField(field.Name.LocalName, BindingFlags.Instance | BindingFlags.Public);
                if (member == null) throw new FormatException("Unknown Snapshot option: " + field.Name);
                ValidateFields(field, member.FieldType);
            }
        }
        private AutomationSnapshotRecord Find(string instanceId, string evidenceKey) =>
            _run.snapshots.FirstOrDefault(s => s.instanceId == instanceId && s.evidenceKey == evidenceKey);
        internal static string RequestKey(string runId, string instanceId, string evidenceKey)
        {
            using var hash = SHA256.Create();
            return "step:" + string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(
                JsonUtility.ToJson(new Identity { runId = runId, instanceId = instanceId, evidenceKey = evidenceKey })))
                .Select(b => b.ToString("x2")));
        }
        internal string Begin(string instanceId, string evidenceKey, string arguments)
        {
            var options = Parse(arguments);
            if (string.IsNullOrWhiteSpace(evidenceKey)) throw new ArgumentException("Evidence key required.");
            var existing = Find(instanceId, evidenceKey);
            if (existing != null)
            {
                if (existing.arguments != arguments) throw new InvalidOperationException("STEP_SNAPSHOT_ARGUMENT_CONFLICT");
                return Result(existing);
            }
            string requestKey = RequestKey(_run.runId, instanceId, evidenceKey);
            string key = requestKey.Substring("step:".Length);
            var record = new AutomationSnapshotRecord
            {
                instanceId = instanceId, evidenceKey = evidenceKey, requestKey = requestKey,
                arguments = arguments, requestedAt = _clock()
            };
            _run.snapshots.Add(record);
            _save();
            options.capture.requestKey = record.requestKey;
            options.capture.outputDirectory = _run.reportDirectory + "/snapshots/" + key;
            SnapshotApiResultV1 result;
            try { result = UPilotSnapshotApiV1.Start(options.capture); }
            catch (Exception ex)
            {
                Finish(record, options, "STEP_SNAPSHOT_START_UNCONFIRMED", ex.Message, true);
                return Result(record);
            }
            if (result.ok && !string.IsNullOrEmpty(result.snapshotId)) record.snapshotId = result.snapshotId;
            else Finish(record, options, result.errorCode ?? "STEP_SNAPSHOT_START_UNCONFIRMED",
                result.errorMessage, result.ok || result.errorCode == "SNAPSHOT_API_FAILED");
            _save();
            return Result(record);
        }
        [Serializable] private sealed class Identity { public string runId, instanceId, evidenceKey; }
        internal string Poll(string instanceId, string evidenceKey, bool cancel = false)
        {
            var record = Find(instanceId, evidenceKey);
            if (record == null) throw new InvalidOperationException("STEP_SNAPSHOT_NOT_STARTED");
            if (record.requestedAt <= 0 || record.requestedAt > _clock()
                || string.IsNullOrWhiteSpace(record.evidenceKey)
                || record.requestKey != RequestKey(_run.runId, instanceId, evidenceKey)
                || !new[] { "Running", "Succeeded", "SucceededWithWarnings", "Failed" }.Contains(record.status)
                || record.cancelIntent && record.cancelRequestedAt <= 0
                || record.status == "Succeeded" && (!record.recorded || record.unresolved)
                || record.status == "Failed" && string.IsNullOrWhiteSpace(record.errorCode))
                throw new InvalidDataException("STEP_SNAPSHOT_CHECKPOINT_INVALID");
            if (record.status != "Running") return Result(record);
            var options = Parse(record.arguments);
            if (string.IsNullOrEmpty(record.snapshotId))
            {
                Finish(record, options, "STEP_SNAPSHOT_START_UNCONFIRMED", "Start identity was not persisted; not replayed.", true);
                return Result(record);
            }
            var observed = UPilotSnapshotApiV1.Status(record.snapshotId);
            var job = observed.job;
            string expectedDirectory = Path.GetFullPath(Path.Combine(_run.reportDirectory, "snapshots",
                record.requestKey.Substring("step:".Length)));
            if (!observed.ok || job == null || job.snapshotId != record.snapshotId || job.requestKey != record.requestKey
                || string.IsNullOrEmpty(job.outputDirectory)
                || !string.Equals(Path.GetFullPath(job.outputDirectory), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Finish(record, options, "STEP_SNAPSHOT_IDENTITY_INVALID", "Cannot observe the original Snapshot.", true);
                return Result(record);
            }
            if (job.terminal)
            {
                try
                {
                    VerifyFiles(job, artifacts =>
                    {
                        foreach (var artifact in artifacts) AddArtifact(record, artifact);
                        _save();
                    });
                    string telemetryPath = Path.Combine(job.outputDirectory, "automation-evidence.json");
                    string json = JsonUtility.ToJson(job, true);
                    if (File.Exists(telemetryPath))
                    {
                        if (File.ReadAllText(telemetryPath) != json)
                            throw new InvalidDataException("Snapshot telemetry content changed.");
                    }
                    else
                    {
                        using var stream = new FileStream(telemetryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                    }
                    AddArtifact(record, AutomationReportWriter.GetArtifactMetadata("snapshot.telemetry", telemetryPath));
                    record.recorded = true;
                    bool accepted = job.success && job.targets.Count > 0 && job.artifacts.Count > 0
                        && job.artifacts.All(a => a.acceptedAsEvidence)
                        && job.targets.All(t => t.success && t.provenance != null
                            && t.provenance.pixelSourceVerified && !t.provenance.occlusionSensitive);
                    if (accepted && !record.cancelIntent) record.status = "Succeeded";
                    else Finish(record, options, record.cancelIntent ? "STEP_SNAPSHOT_CANCELED" : "STEP_SNAPSHOT_NOT_ACCEPTED",
                        "Snapshot did not produce all required trusted evidence.", false);
                    _save();
                }
                catch (Exception ex) { Finish(record, options, "STEP_SNAPSHOT_EVIDENCE_INVALID", ex.Message, true); }
                return Result(record);
            }
            if (cancel || _clock() - record.requestedAt >= options.timeoutSeconds * 1000)
            {
                if (!record.cancelIntent)
                {
                    record.cancelIntent = true; record.cancelRequestedAt = _clock(); _save();
                    try { UPilotSnapshotApiV1.Cancel(record.snapshotId); }
                    catch { /* Cancellation intent is durable; only observe this identity again. */ }
                }
                if (_clock() - record.cancelRequestedAt >= options.cancelGraceSeconds * 1000)
                    Finish(record, options, "STEP_SNAPSHOT_RELEASE_UNCONFIRMED", "Snapshot cancellation was not confirmed.", true);
            }
            return Result(record);
        }
        internal bool CompleteItem(string instanceId)
        {
            foreach (var record in _run.snapshots.Where(s => s.instanceId == instanceId).ToArray())
            {
                try { Poll(instanceId, record.evidenceKey); }
                catch (Exception ex)
                {
                    // An unobservable resource blocks success, not the remaining Finally steps.
                    record.status = "Failed"; record.errorCode = "STEP_SNAPSHOT_OBSERVATION_FAILED";
                    record.message = ex.Message; record.unresolved = true; _save();
                }
            }
            return !_run.snapshots.Any(s => s.instanceId == instanceId && s.status == "Running");
        }
        internal string Error(string instanceId, string evidenceKey)
        {
            var record = Find(instanceId, evidenceKey);
            return AutomationStepJsonCodec.ErrorJson(record?.message ?? "Snapshot evidence was not recorded.",
                record?.errorCode ?? "STEP_SNAPSHOT_NOT_STARTED");
        }
        private void AddArtifact(AutomationSnapshotRecord record, AutomationReportArtifact artifact)
        {
            artifact.instanceId = record.instanceId;
            var existing = _run.registeredArtifacts.FirstOrDefault(a =>
                string.Equals(a.path, artifact.path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (existing.sha256 != artifact.sha256 || existing.bytes != artifact.bytes
                    || existing.instanceId != record.instanceId) throw new InvalidDataException("Snapshot artifact changed.");
                return;
            }
            _run.registeredArtifacts.Add(artifact);
        }
        private void Finish(AutomationSnapshotRecord record, Options options, string code, string message, bool unresolved)
        {
            record.status = unresolved || options.required ? "Failed" : "SucceededWithWarnings";
            record.errorCode = code; record.message = message; record.unresolved = unresolved;
            _save();
        }
        private static string Result(AutomationSnapshotRecord record) => AutomationStepJsonCodec.ResultJson(
            record.status, record.status == "Failed" ? record.errorCode : "");
        internal static AutomationReportArtifact[] VerifyFiles(SnapshotJobPayload job,
            Action<AutomationReportArtifact[]> register = null)
        {
            if (job == null || !job.terminal || job.persistenceStatus != "verified"
                || string.IsNullOrWhiteSpace(job.outputDirectory)
                || job.artifacts == null || (job.success && job.artifacts.Count == 0))
                throw new InvalidDataException("Snapshot persistence is not verified.");
            var handles = new List<FileStream>();
            var files = new List<AutomationReportArtifact>();
            try
            {
                void Verify(string kind, string path, long bytes, string digest)
                {
                    if (bytes <= 0 || digest?.Length != 64 || !digest.All(Uri.IsHexDigit))
                        throw new InvalidDataException("Invalid original Snapshot file metadata.");
                    var actual = AutomationReportWriter.GetArtifactMetadata(kind, path);
                    string root = Path.GetFullPath(job.outputDirectory).TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(actual.path).StartsWith(root,
                        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidDataException("Snapshot artifact is outside its owned output directory.");
                    handles.Add(new FileStream(actual.path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    actual = AutomationReportWriter.GetArtifactMetadata(kind, path);
                    if (actual.bytes != bytes || !string.Equals(actual.sha256, digest, StringComparison.OrdinalIgnoreCase)
                        || files.Any(f => string.Equals(f.path, actual.path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Snapshot file changed or duplicated.");
                    files.Add(actual);
                }
                foreach (var artifact in job.artifacts) Verify("snapshot.file", artifact.path, artifact.bytes, artifact.sha256);
                Verify("snapshot.manifest", job.manifestPath, job.manifestBytes, job.manifestSha256);
                var result = files.ToArray();
                register?.Invoke(result);
                return result;
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }
    }
}
