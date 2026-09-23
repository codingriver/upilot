using System;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>Finite critical notifications; never executes cleanup or changes logger settings.</summary>
    internal static class UPilotQueueCleanupLog
    {
        [Serializable] internal sealed class Entry
        {
            public string requestId, targetType, targetId, action, phase, result, reason, errorCode;
        }
        [Serializable] private sealed class Message { public Entry payload; }

        internal static void Register(UPilotBridge bridge)
        {
            bridge.Router.Register("queue.cleanup.log", async (id, json, token) =>
            {
                var entry = JsonUtility.FromJson<Message>(json)?.payload;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.EnqueueTracked(id, () =>
                {
                    try { Write(entry); completion.TrySetResult(true); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
                await completion.Task;
                await bridge.SendResultAsync(id, "queue.cleanup.log", new GenericOkPayload { ok = true }, token);
            });
        }

        private static string Bound(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length > 256 ? text.Substring(0, 256) : text;
        }

        internal static void Write(Entry entry)
        {
            if (entry == null) throw new ArgumentException("QUEUE_LOG_INVALID");
            bool error = entry.phase == "failed" || entry.phase == "unconfirmed";
            string message = $"[UPilot][QueueCleanup] requestId={Bound(entry.requestId)} targetType={Bound(entry.targetType)} " +
                $"targetId={Bound(entry.targetId)} action={Bound(entry.action)} phase={Bound(entry.phase)} " +
                $"result={Bound(entry.result)} errorCode={Bound(entry.errorCode)} reason={Bound(entry.reason)}";
            // AppendLine bypasses MinLevel/mirror flags without mutating user preferences.
            Logger.AppendLine(message);
            if (error) Debug.LogError(message);
            else Debug.Log(message);
        }
    }
}
