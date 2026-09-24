using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class ConsoleCaptureApiCapabilities
    {
        public int apiVersion = 2;
        public bool available;
        public bool asynchronousRead = true;
        public bool requiresEditorMainThread = true;
        public bool forceStopExposed;
        public string unavailableReason;
        public string[] operations = { "Start", "Status", "GetBoundary", "ReadAsync", "Stop", "VerifyStoppedAsync" };
    }

    [Serializable]
    public sealed class ConsoleCaptureBoundaryResult
    {
        public bool ok;
        public string sessionId;
        public long nextSequence = -1;
        public string errorCode;
        public string errorMessage;
    }

    [Serializable]
    public sealed class ConsoleCaptureVerificationResult
    {
        public bool ok;
        public string sessionId;
        public string stopState = "unknown";
        public bool stopped;
        public bool artifactsVerified;
        public string verifiedAt;
        public long recordCount;
        public long fileBytes;
        public string sha256;
        public string errorCode;
        public string errorMessage;
    }

    /// <summary>Public, ownership-preserving facade over the persistent Console Capture service.</summary>
    public static class UPilotConsoleCaptureApi
    {
        private static UPilotConsoleCaptureService s_service;
        private static int s_mainThreadId;

        internal static void Bind(UPilotConsoleCaptureService service)
        {
            s_service = service;
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static ConsoleCaptureApiCapabilities Capabilities() => new()
        {
            available = s_service != null,
            unavailableReason = s_service == null
                ? "Console Capture service has not been initialized in this Editor domain."
                : string.Empty,
        };

        public static ConsoleCaptureResult Start(ConsoleCaptureStartPayload request)
        {
            if (!TryEnterMainThread(out ConsoleCaptureResult error))
                return error;
            if (request == null)
                return Error("StartCapture", "CONSOLE_CAPTURE_REQUEST_REQUIRED", "Capture request is required.");
            if (string.IsNullOrEmpty(request.ownerToken))
                return Error("StartCapture", "CONSOLE_CAPTURE_OWNER_TOKEN_REQUIRED", "Public Capture sessions require an ownership token so the caller can stop them without force-stop.");
            return UPilotConsoleCaptureService.StartCapture(Clone(request));
        }

        public static ConsoleCaptureResult Status(string sessionId)
        {
            if (!TryEnterMainThread(out ConsoleCaptureResult error))
                return error;
            return UPilotConsoleCaptureService.GetStatus(sessionId);
        }

        public static Task<ConsoleCaptureVerificationResult> VerifyStoppedAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            var result = new ConsoleCaptureVerificationResult { sessionId = sessionId ?? string.Empty };
            if (s_service == null || Thread.CurrentThread.ManagedThreadId != s_mainThreadId
                || string.IsNullOrWhiteSpace(sessionId))
            {
                result.errorCode = s_service == null ? "CONSOLE_CAPTURE_UNAVAILABLE"
                    : Thread.CurrentThread.ManagedThreadId != s_mainThreadId
                        ? "CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED" : "CONSOLE_CAPTURE_SESSION_REQUIRED";
                result.errorMessage = result.errorCode;
                return Task.FromResult(result);
            }
            ConsoleCaptureManifest manifest;
            string expectedJson;
            try
            {
                manifest = UPilotConsoleCaptureService.ObservePersistedCapture(sessionId);
                if (manifest == null || !string.Equals(manifest.sessionId, sessionId, StringComparison.Ordinal))
                {
                    result.errorCode = "CONSOLE_CAPTURE_NOT_FOUND";
                    result.errorMessage = "The persisted session cannot be found or read.";
                    return Task.FromResult(result);
                }
                result.ok = true;
                result.stopState = manifest.active ? "active" : manifest.finishedAtUtcMs > 0 ? "stopped" : "unknown";
                result.stopped = result.stopState == "stopped";
                if (!result.stopped) return Task.FromResult(result);
                manifest = Clone(manifest);
                expectedJson = JsonUtility.ToJson(manifest);
            }
            catch (Exception ex)
            {
                result.errorCode = "CONSOLE_CAPTURE_OBSERVATION_FAILED";
                result.errorMessage = BoundedError(ex);
                return Task.FromResult(result);
            }
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string root = Path.GetFullPath(manifest.directory ?? string.Empty)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrEmpty(manifest.directory)
                        || !string.Equals(Path.GetFullPath(manifest.manifestPath), Path.Combine(root, "session.json"), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Path.GetFullPath(manifest.summaryPath), Path.Combine(root, "summary.json"), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Path.GetFullPath(manifest.jsonlPath), Path.Combine(root, "console.jsonl"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("CONSOLE_CAPTURE_PATH_MISMATCH");
                    AutomationRunCapture.VerifyStoppedFiles(manifest, expectedJson,
                        (kind, path) => CaptureMetadata(root, kind, path), null, cancellationToken);
                    result.artifactsVerified = true;
                    result.recordCount = manifest.totalCount;
                    result.fileBytes = manifest.fileBytes;
                    result.sha256 = manifest.sha256;
                    result.verifiedAt = DateTimeOffset.UtcNow.ToString("O");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.errorCode = "CONSOLE_CAPTURE_ARTIFACTS_UNVERIFIED";
                    result.errorMessage = BoundedError(ex);
                }
                return result;
            }, cancellationToken);
        }

        private static string BoundedError(Exception ex) => ex.GetType().Name + ": "
            + (ex.Message ?? string.Empty).Substring(0, Math.Min(256, (ex.Message ?? string.Empty).Length));

        private static AutomationReportArtifact CaptureMetadata(string root, string kind, string path)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Capture artifact escaped its registered directory.");
            for (string part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Capture artifact traverses a reparse point.");
                if (string.Equals(part, Path.GetPathRoot(part), StringComparison.OrdinalIgnoreCase)) break;
            }
            var info = new FileInfo(full);
            long modified = info.LastWriteTimeUtc.Ticks;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            using var sha = SHA256.Create();
            string digest = string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != modified)
                throw new IOException("Capture artifact changed during observation.");
            return new AutomationReportArtifact { kind = kind, path = full, bytes = length, sha256 = digest };
        }

        public static ConsoleCaptureBoundaryResult GetBoundary(string expectedSessionId = "")
        {
            if (s_service == null)
                return BoundaryError("CONSOLE_CAPTURE_UNAVAILABLE", Capabilities().unavailableReason);
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                return BoundaryError("CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED", "Call this API on the Editor main thread.");
            if (!UPilotConsoleCaptureService.TryGetActiveSequenceBoundary(out string sessionId, out long nextSequence))
                return BoundaryError("CONSOLE_CAPTURE_NOT_ACTIVE", "There is no active Console Capture session.");
            if (!string.IsNullOrEmpty(expectedSessionId)
                && !string.Equals(expectedSessionId, sessionId, StringComparison.Ordinal))
                return BoundaryError("CONSOLE_CAPTURE_IDENTITY_MISMATCH", "The active Capture session does not match the expected identity.", sessionId);
            return new ConsoleCaptureBoundaryResult
            {
                ok = true,
                sessionId = sessionId,
                nextSequence = nextSequence,
            };
        }

        public static Task<ConsoleCaptureReadResult> ReadAsync(
            ConsoleCaptureReadPayload request,
            CancellationToken cancellationToken = default)
        {
            if (s_service == null)
                return Task.FromResult(ReadError(string.Empty, "CONSOLE_CAPTURE_UNAVAILABLE: " + Capabilities().unavailableReason));
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                return Task.FromResult(ReadError(request?.sessionId, "CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED: Call ReadAsync on the Editor main thread."));
            if (request == null)
                return Task.FromResult(ReadError(string.Empty, "CONSOLE_CAPTURE_REQUEST_REQUIRED: Capture read request is required."));

            ConsoleCaptureReadPayload snapshot = Clone(request);
            UPilotConsoleCaptureService.ConsoleCaptureReadPreparation preparation =
                UPilotConsoleCaptureService.PrepareReadCapture(snapshot);
            if (!string.IsNullOrEmpty(preparation.Error))
                return Task.FromResult(ReadError(snapshot.sessionId, preparation.Error));

            // Preparation snapshots main-thread-owned state. Buffered records are reported so callers can wait
            // for the final boundary to become readable; file scanning stays off the Editor loop.
            return Task.Run(() =>
            {
                ConsoleCaptureReadResult result = UPilotConsoleCaptureService.ReadCaptureFiles(
                    preparation.Manifest,
                    snapshot,
                    cancellationToken);
                result.bufferedRecordCount = preparation.BufferedRecordCount;
                result.diskSnapshotNextSequence = preparation.Manifest.nextSequence;
                return result;
            }, cancellationToken);
        }

        public static ConsoleCaptureResult Stop(string sessionId, string ownerToken)
        {
            if (!TryEnterMainThread(out ConsoleCaptureResult error))
                return error;
            if (string.IsNullOrWhiteSpace(sessionId))
                return Error("StopCapture", "CONSOLE_CAPTURE_SESSION_REQUIRED", "An exact session ID is required.");
            return UPilotConsoleCaptureService.StopCapture(sessionId, ownerToken, false);
        }

        private static bool TryEnterMainThread(out ConsoleCaptureResult error)
        {
            if (s_service == null)
            {
                error = Error("ConsoleCaptureApi", "CONSOLE_CAPTURE_UNAVAILABLE", Capabilities().unavailableReason);
                return false;
            }
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
            {
                error = Error("ConsoleCaptureApi", "CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED", "Call this API on the Editor main thread.");
                return false;
            }
            error = null;
            return true;
        }

        private static ConsoleCaptureResult Error(string action, string code, string message) => new()
        {
            ok = false,
            action = action,
            error = code + ": " + message,
        };

        private static ConsoleCaptureReadResult ReadError(string sessionId, string message) => new()
        {
            ok = false,
            action = "ReadCapture",
            error = message,
            sessionId = sessionId ?? string.Empty,
        };

        private static ConsoleCaptureBoundaryResult BoundaryError(string code, string message, string sessionId = "") => new()
        {
            ok = false,
            sessionId = sessionId,
            errorCode = code,
            errorMessage = message,
        };

        private static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
    }
}
