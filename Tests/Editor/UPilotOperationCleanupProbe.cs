using System;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    /// <summary>Explicitly invoked generic MCP fixture; no business-project dependency.</summary>
    public static class UPilotOperationCleanupProbe
    {
        private const string Key = "UPilot.OperationCleanupProbe";
        [Serializable]
        private sealed class State
        {
            public string operationId, status, origin;
            public long businessEndedAt, exitDueAt, exitRequestedAt;
        }

        [InitializeOnLoadMethod]
        private static void RestoreObservation()
        {
            EditorApplication.update -= Tick;
            if (Read()?.exitDueAt > 0) EditorApplication.update += Tick;
        }

        public static string Start(string operationId, string origin)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Probe requires explicit PlayMode entry.");
            if (origin != "project" && origin != "unknown" && origin != "unexpected")
                throw new ArgumentException("Expected project, unknown, or unexpected.");
            if (Read()?.exitDueAt > 0) throw new InvalidOperationException("Probe already pending; do not replay start.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var state = new State
            {
                operationId = operationId, origin = origin,
                status = origin == "unexpected" ? "Running" : "Succeeded",
                businessEndedAt = origin == "unexpected" ? 0 : now, exitDueAt = now + 3000,
            };
            Write(state);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Debug.Log("UPilot Operation cleanup probe: " + JsonUtility.ToJson(state));
            return JsonUtility.ToJson(state);
        }

        public static string Status() => JsonUtility.ToJson(Read() ?? new State { status = "None" });

        private static void Tick()
        {
            var state = Read();
            if (state == null || state.exitDueAt <= 0) { EditorApplication.update -= Tick; return; }
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < state.exitDueAt) return;
            state.exitDueAt = 0;
            state.exitRequestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Write(state); // Persist the intent as consumed before requesting an exit; never replay after Reload.
            EditorApplication.update -= Tick;
            if (!EditorApplication.isPlaying) return;
            if (state.origin == "project") UPilotPlayModeTransitions.RequestProjectExit(state.operationId);
            else EditorApplication.isPlaying = false;
        }

        private static State Read()
        {
            var json = SessionState.GetString(Key, "");
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<State>(json);
        }
        private static void Write(State state) => SessionState.SetString(Key, JsonUtility.ToJson(state));
    }
}
