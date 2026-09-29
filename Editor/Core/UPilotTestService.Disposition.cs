using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable] public sealed class TestRunDisposition
    {
        public string requestId, runGuid, adapter, reason, originalCallbackDomain, releasedInDomain;
        public string backupPath, backupSha256;
        public long backupBytes, disposedAt;
        // JsonUtility expands a null inline object into default fields. Only that exact
        // empty representation is absence; even a partial/corrupt intent stays protected.
        internal bool HasEvidence => !string.IsNullOrEmpty(requestId) || !string.IsNullOrEmpty(runGuid)
            || !string.IsNullOrEmpty(adapter) || !string.IsNullOrEmpty(reason)
            || !string.IsNullOrEmpty(originalCallbackDomain) || !string.IsNullOrEmpty(releasedInDomain)
            || !string.IsNullOrEmpty(backupPath) || !string.IsNullOrEmpty(backupSha256)
            || backupBytes != 0 || disposedAt != 0;
        // Intentionally NOT cleanup evidence. Original unresolved resources/results stay in the snapshot.
    }

    public partial class UPilotTestService
    {
        [Serializable] internal sealed class TestAbandonRequest
        {
            public string runGuid, requestId, expectedStateHash, reason;
        }
        [Serializable] private sealed class TestAbandonMessage { public TestAbandonRequest payload; }
        [Serializable] internal sealed class TestAbandonProof
        {
            public bool eligible;
            public string runGuid, stateHash, originalCallbackDomain, currentCallbackDomain;
            public string adapter = "test-post-reload-v1";
        }
        internal string DispositionDirectoryForTests;

        private void RegisterDispositionCommands()
        {
            _bridge.Router.Register("test.abandon_preview", (id, json, token) => HandleAbandonAsync(id, json, token, false));
            _bridge.Router.Register("test.abandon", (id, json, token) => HandleAbandonAsync(id, json, token, true));
        }

        private async Task HandleAbandonAsync(string id, string json, CancellationToken token, bool apply)
        {
            string route = apply ? "test.abandon" : "test.abandon_preview";
            var request = JsonUtility.FromJson<TestAbandonMessage>(json)?.payload ?? new TestAbandonRequest();
            var completion = new TaskCompletionSource<object>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var queue = _bridge.ObserveQueue(id);
                    if (!queue.complete || queue.activeCount != 0 || queue.queuedCount != 0 || queue.executingCount != 0)
                        throw new InvalidOperationException("TEST_ABANDON_COMMANDS_UNCONFIRMED");
                    if (apply && !UPilotProjectConfig.Load().aiQueueCleanupAllowed)
                        throw new InvalidOperationException("QUEUE_CLEANUP_NOT_APPROVED");
                    completion.SetResult(apply ? (object)AbandonReloadedRun(request) : PreviewAbandonReloadedRun(request.runGuid));
                }
                catch (Exception ex) { completion.SetException(ex); }
            });
            try
            {
                var result = await completion.Task;
                if (result is TestAbandonProof proof) await _bridge.SendResultAsync(id, route, proof, token);
                else await _bridge.SendResultAsync(id, route, (TestRunResultPayload)result, token);
            }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "TEST_ABANDON_REFUSED", ex.Message, token, route); }
        }

        internal TestAbandonProof PreviewAbandonReloadedRun(string runGuid)
        {
            if (string.IsNullOrEmpty(runGuid) || _lastResults == null || runGuid != _activeRunGuid
                || runGuid != _lastResults.runGuid || !_isRunning || !_cleanupOwnershipUnknown
                || _lastResults.disposition != null || _lastResults.phase != "recovery_required"
                || string.IsNullOrEmpty(_lastResults.callbackDomain) || _lastResults.callbackDomain == CallbackDomain
                || !ReferenceEquals(_activeApi, null) || _activeCallback != null
                || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("TEST_ABANDON_UNSUPPORTED: requires an original post-Reload cleanup orphan in stable EditMode.");
            // Probe without changing the original snapshot or creating a replacement API.
            var adapter = ResolveRunnerAdapter(FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"));
            if (adapter.Probe(runGuid, out _, out var diagnostic) != "inactive")
                throw new InvalidOperationException("TEST_ABANDON_RUNNER_NOT_INACTIVE: " + diagnostic);
            return new TestAbandonProof { eligible = true, runGuid = runGuid,
                originalCallbackDomain = _lastResults.callbackDomain, currentCallbackDomain = CallbackDomain,
                stateHash = DispositionHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(_lastResults))) };
        }

        internal TestRunResultPayload AbandonReloadedRun(TestAbandonRequest request)
        {
            if (string.IsNullOrEmpty(request.requestId) || request.requestId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || request.requestId == "." || request.requestId == ".." || string.IsNullOrWhiteSpace(request.reason)
                || request.reason.Length > 256)
                throw new InvalidOperationException("TEST_ABANDON_INVALID_REQUEST");
            // Idempotent observation of a known disposition; never act on a replacement run.
            if (_lastResults?.runGuid == request.runGuid && _lastResults.disposition?.requestId == request.requestId)
                return _lastResults;
            var proof = PreviewAbandonReloadedRun(request.runGuid);
            if (proof.stateHash != request.expectedStateHash)
                throw new InvalidOperationException("TEST_ABANDON_PREVIEW_CHANGED");
            string directory = DispositionDirectoryForTests ?? Path.Combine(PersistenceDirectory, "dispositions");
            Directory.CreateDirectory(directory);
            string backupPath = Path.Combine(directory, request.requestId + ".json");
            byte[] original = Encoding.UTF8.GetBytes(JsonUtility.ToJson(_lastResults, true));
            // Never overwrite another disposition's evidence. Failed writes retain the slot.
            using (var stream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(original, 0, original.Length); stream.Flush(true); }
            byte[] saved = File.ReadAllBytes(backupPath);
            string hash = DispositionHash(original);
            if (saved.LongLength != original.LongLength || DispositionHash(saved) != hash)
                throw new IOException("TEST_ABANDON_BACKUP_VERIFICATION_FAILED");
            var candidate = _lastResults.ShallowCopyForPersistence();
            candidate.disposition = new TestRunDisposition { requestId = request.requestId, runGuid = request.runGuid,
                adapter = proof.adapter, reason = request.reason, originalCallbackDomain = proof.originalCallbackDomain,
                releasedInDomain = proof.currentCallbackDomain, backupPath = backupPath, backupSha256 = hash,
                backupBytes = saved.LongLength, disposedAt = NowMs() };
            candidate.cleanupAttemptDeadlineAt = NowMs() + 30000;
            candidate.status = "cleanup";
            candidate.phase = "cleanup";
            candidate.cleanupPending = true;
            candidate.cleanupSucceeded = false;
            candidate.terminal = false;
            // Persist the disposition intent first. A Reload can only resume its commit, not resource cleanup.
            _lastResults = candidate; // Retain the exact intent even if a disk commit is partial.
            ContinueDispositionCommit();
            if (!_lastResults.terminal) ScheduleCleanup();
            return _lastResults;
        }

        private void ContinueDispositionCommit()
        {
            if (NowMs() < _nextCleanupProbeAt) return;
            var source = _lastResults;
            if (source.cleanupAttemptDeadlineAt <= NowMs())
            { RequireCleanupRecovery("Disposition commit budget expired; original evidence retained."); return; }
            var candidate = source.ShallowCopyForPersistence();
            try
            {
                var d = candidate.disposition;
                if (_activeRunGuid != d.runGuid || candidate.runGuid != d.runGuid
                    || d.originalCallbackDomain == CallbackDomain || !ReferenceEquals(_activeApi, null)
                    || _activeCallback != null)
                    throw new InvalidOperationException("TEST_ABANDON_IDENTITY_CHANGED");
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                { ScheduleNextCleanupProbe(NowMs(), false); return; }
                var adapter = ResolveRunnerAdapter(FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"));
                if (adapter.Probe(d.runGuid, out _, out _) != "inactive")
                { RequireCleanupRecovery("Original Runner is no longer confirmed inactive; disposition not committed."); return; }
                var bytes = File.ReadAllBytes(d.backupPath);
                if (bytes.LongLength != d.backupBytes || DispositionHash(bytes) != d.backupSha256)
                    throw new IOException("TEST_ABANDON_BACKUP_VERIFICATION_FAILED");
                // Persist/reconcile original intent first; this is idempotent storage, not replayed business.
                candidate.terminal = false;
                candidate.cleanupPending = true;
                SaveSnapshot(candidate, true, false);
                candidate.status = "Released";
                candidate.phase = "disposed";
                candidate.cleanupStatus = "abandoned";
                candidate.cleanupPending = false;
                candidate.cleanupSucceeded = false;
                candidate.terminal = true;
                candidate.isRunning = false;
                candidate.endedAt = Math.Max(candidate.endedAt, NowMs());
                candidate.persistenceError = "";
                candidate.nextAction = "Administrative disposition only; original resource release is not verified.";
                SaveSnapshot(candidate, false, true);
                _lastResults = candidate;
                _isRunning = false;
                _activeRunGuid = null;
                // The old orphan is fenced and committed. Its unknown-ownership flag
                // must not poison the next independently owned run in this service.
                _cleanupOwnershipUnknown = false;
                _editorCleanupPending = false;
                _pendingTerminalStatus = null;
                _forceStopRequested = false;
                StopCleanupScheduling();
            }
            catch (Exception ex)
            {
                source.snapshotSequence = Math.Max(source.snapshotSequence, candidate.snapshotSequence);
                source.endedAt = Math.Max(source.endedAt, candidate.endedAt);
                source.persistenceError = ex.GetType().Name + ": " + ex.Message;
                ScheduleNextCleanupProbe(NowMs(), false);
            }
        }

        private static string DispositionHash(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
