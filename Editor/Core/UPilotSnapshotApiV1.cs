using System;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class SnapshotApiCapabilitiesV1
    {
        public int apiVersion = 1;
        public bool available;
        public bool asynchronous = true;
        public bool requiresEditorMainThread = true;
        public string unavailableReason;
        public string[] operations = { "Start", "Status", "Cancel", "Collect" };
    }

    [Serializable]
    public sealed class SnapshotApiResultV1
    {
        public bool ok;
        public string snapshotId;
        public SnapshotJobPayload job;
        public string errorCode;
        public string errorMessage;
    }

    /// <summary>Non-blocking bridge API over the same jobs used by MCP Snapshot tools.</summary>
    public static class UPilotSnapshotApiV1
    {
        private static UPilotSnapshotService s_service;
        private static int s_mainThreadId;

        internal static void Bind(UPilotSnapshotService service)
        {
            s_service = service;
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static SnapshotApiCapabilitiesV1 Capabilities() => new SnapshotApiCapabilitiesV1
        {
            available = s_service != null,
            unavailableReason = s_service == null ? "Snapshot service has not been initialized in this Editor domain." : "",
        };

        public static SnapshotApiResultV1 Start(SnapshotCapturePayload request) =>
            Invoke(() => s_service.StartFromApi(JsonUtility.FromJson<SnapshotCapturePayload>(JsonUtility.ToJson(request))));

        public static SnapshotApiResultV1 Status(string snapshotId) => Invoke(() => s_service.ReadFromApi(snapshotId), snapshotId);
        public static SnapshotApiResultV1 Cancel(string snapshotId) => Invoke(() => s_service.CancelJob(snapshotId), snapshotId);
        public static SnapshotApiResultV1 Collect(string snapshotId) => Status(snapshotId);

        private static SnapshotApiResultV1 Invoke(Func<SnapshotJobPayload> action, string snapshotId = "")
        {
            if (s_service == null) return Error("SNAPSHOT_UNAVAILABLE", Capabilities().unavailableReason, snapshotId);
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                return Error("SNAPSHOT_MAIN_THREAD_REQUIRED", "Call this bridge API on the Editor main thread; do not synchronously wait for capture.", snapshotId);
            try
            {
                var job = action();
                if (job == null) return Error("SNAPSHOT_NOT_FOUND", "Snapshot identity was not found.", snapshotId);
                return new SnapshotApiResultV1
                {
                    ok = true, snapshotId = job.snapshotId,
                    job = JsonUtility.FromJson<SnapshotJobPayload>(JsonUtility.ToJson(job)),
                };
            }
            catch (UPilotSnapshotService.SnapshotRequestException ex) { return Error(ex.Code, ex.Message, snapshotId); }
            catch (Exception ex) { return Error("SNAPSHOT_API_FAILED", ex.Message, snapshotId); }
        }

        private static SnapshotApiResultV1 Error(string code, string message, string snapshotId) =>
            new SnapshotApiResultV1 { ok = false, snapshotId = snapshotId, errorCode = code, errorMessage = message };
    }
}
