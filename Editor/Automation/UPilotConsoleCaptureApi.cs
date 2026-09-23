using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class ConsoleCaptureApiCapabilities
    {
        public int apiVersion = 1;
        public bool available;
        public bool asynchronousRead = true;
        public bool requiresEditorMainThread = true;
        public bool forceStopExposed;
        public string unavailableReason;
        public string[] operations = { "Start", "Status", "GetBoundary", "ReadAsync", "Stop" };
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
