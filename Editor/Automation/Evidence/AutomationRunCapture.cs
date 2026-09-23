using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Private credentials and write-ahead ownership for the executor's one Capture.</summary>
    internal sealed class AutomationRunCapture
    {
        [Serializable] private sealed class Ownership
        {
            public string runId, editorIdentity, ownerToken, sessionId = "";
            public bool startIntent, stopIntent;
            public long stopRequestedAt;
        }
        private readonly string _path;
        private readonly Ownership _ownership;
        internal string SessionId => _ownership.sessionId;
        internal AutomationRunCapture(string path, string runId, bool restore)
        {
            _path = path;
            if (restore)
            {
                _ownership = JsonUtility.FromJson<Ownership>(File.ReadAllText(path));
                if (_ownership == null || _ownership.runId != runId
                    || _ownership.editorIdentity != AutomationStepRunStore.EditorIdentity
                    || string.IsNullOrEmpty(_ownership.ownerToken) || !_ownership.startIntent
                    || _ownership.stopIntent && _ownership.stopRequestedAt <= 0)
                    throw new InvalidDataException("STEP_CAPTURE_OWNERSHIP_INVALID");
            }
            else _ownership = new Ownership
            {
                runId = runId, editorIdentity = AutomationStepRunStore.EditorIdentity,
                ownerToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
            };
        }
        private void Save() => AutomationStepRunStore.SaveJson(_path, JsonUtility.ToJson(_ownership));
        internal void Start()
        {
            if (_ownership.startIntent) throw new InvalidOperationException("STEP_CAPTURE_START_NOT_REPLAYABLE");
            _ownership.startIntent = true;
            Save();
            var result = UPilotConsoleCaptureApi.Start(new ConsoleCaptureStartPayload
            {
                title = "Step plan " + _ownership.runId, ownerId = _ownership.runId,
                ownerToken = _ownership.ownerToken, requestKey = "step-plan:" + _ownership.runId
            });
            if (!result.ok || result.session == null
                || result.session.ownerId != _ownership.runId
                || result.session.requestKey != "step-plan:" + _ownership.runId)
                throw new InvalidOperationException("STEP_CAPTURE_START_UNCONFIRMED");
            _ownership.sessionId = result.session.sessionId;
            Save();
        }
        internal ConsoleCaptureManifest Observe()
        {
            if (string.IsNullOrEmpty(SessionId)) throw new InvalidDataException("STEP_CAPTURE_START_UNCONFIRMED");
            var result = UPilotConsoleCaptureApi.Status(SessionId);
            using var hash = SHA256.Create();
            string expected = string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(_ownership.ownerToken)).Select(b => b.ToString("x2")));
            if (!result.ok || result.session == null || result.session.sessionId != SessionId
                || result.session.ownerId != _ownership.runId
                || result.session.requestKey != "step-plan:" + _ownership.runId
                || !string.Equals(result.session.ownerTokenSha256, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("STEP_CAPTURE_IDENTITY_INVALID");
            return result.session;
        }
        internal bool StopAndVerify(long now, out ConsoleCaptureManifest manifest,
            out AutomationReportArtifact[] artifacts, Action<AutomationReportArtifact[]> register = null)
        {
            manifest = null; artifacts = Array.Empty<AutomationReportArtifact>();
            if (!_ownership.stopIntent)
            {
                Observe();
                _ownership.stopIntent = true; _ownership.stopRequestedAt = now;
                Save();
                // A lost response never authorizes a second Stop. Subsequent ticks only inspect files.
                try { UPilotConsoleCaptureApi.Stop(SessionId, _ownership.ownerToken); }
                catch { /* Confirm the persisted identity below, without repeating Stop. */ }
            }
            try
            {
                manifest = Observe();
                artifacts = VerifyStopped(manifest, register);
                return true;
            }
            catch when (now - _ownership.stopRequestedAt < 10000) { return false; }
        }
        internal static AutomationReportArtifact[] VerifyStopped(ConsoleCaptureManifest manifest,
            Action<AutomationReportArtifact[]> register = null)
        {
            if (manifest == null || manifest.active || manifest.finishedAtUtcMs <= 0
                || string.IsNullOrEmpty(manifest.sessionId) || string.IsNullOrEmpty(manifest.sha256)
                || manifest.segmentCount < 1 || !string.IsNullOrEmpty(manifest.lastError))
                throw new InvalidDataException("STEP_CAPTURE_STOP_UNCONFIRMED");
            var handles = new List<FileStream>();
            var files = new List<AutomationReportArtifact>();
            try
            {
                AutomationReportArtifact Hold(string kind, string path)
                {
                    var metadata = AutomationReportWriter.GetArtifactMetadata(kind, path);
                    handles.Add(new FileStream(metadata.path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    metadata = AutomationReportWriter.GetArtifactMetadata(kind, path);
                    files.Add(metadata);
                    return metadata;
                }
                foreach (var path in new[] { manifest.manifestPath, manifest.summaryPath })
                {
                    var file = Hold(path == manifest.manifestPath ? "consoleCapture.manifest" : "consoleCapture.summary", path);
                    var root = AutomationStepJsonCodec.ParseObject(File.ReadAllText(file.path));
                    foreach (var key in new[] { "sessionId", "active", "finishedAtUtcMs", "nextSequence",
                        "sha256", "fileBytes", "segmentCount", "ownerId", "requestKey" })
                    {
                        var expected = AutomationStepJsonCodec.ParseObject(JsonUtility.ToJson(manifest)).Element(key);
                        var actual = root.Element(key);
                        if (actual == null || expected == null || actual.Value != expected.Value
                            || (string)actual.Attribute("type") != (string)expected.Attribute("type"))
                            throw new InvalidDataException("STEP_CAPTURE_MANIFEST_MISMATCH: " + key);
                    }
                }
                string directory = Path.GetDirectoryName(Path.GetFullPath(manifest.jsonlPath));
                string[] Inventory() => Directory.GetFiles(directory, "console*.jsonl")
                    .OrderBy(p => Path.GetFileName(p).Equals("console.jsonl", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
                var paths = Inventory();
                if (paths.Length != manifest.segmentCount || !paths.Contains(Path.GetFullPath(manifest.jsonlPath),
                    StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("STEP_CAPTURE_SEGMENTS_INVALID");
                using var sha = SHA256.Create();
                long bytes = 0;
                byte[] buffer = new byte[65536];
                foreach (var path in paths)
                {
                    Hold("consoleCapture.segment", path);
                    var stream = handles[handles.Count - 1];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    { sha.TransformBlock(buffer, 0, read, null, 0); bytes += read; }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                string digest = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
                if (bytes != manifest.fileBytes || !string.Equals(digest, manifest.sha256, StringComparison.OrdinalIgnoreCase)
                    || !paths.SequenceEqual(Inventory()))
                    throw new InvalidDataException("STEP_CAPTURE_FILES_CHANGED");
                var result = files.ToArray();
                register?.Invoke(result);
                return result;
            }
            finally { foreach (var stream in handles) stream.Dispose(); }
        }
    }
}
