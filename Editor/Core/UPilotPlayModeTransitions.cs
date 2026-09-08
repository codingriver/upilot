using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class PlayModeTransitionRecord
    {
        public string transitionId;
        public string fromState;
        public string toState;
        public long at;
        public string unitySessionId;
        public string origin;
        public string requestId;
        public string commandId;
        public string operationId;
        public string toolName;
        public string runGuid;
    }

    /// <summary>Known request provenance, never inferred from Console errors or window focus.</summary>
    public static class UPilotPlayModeTransitions
    {
        [Serializable] private sealed class Ledger
        {
            public List<PlayModeTransitionRecord> records = new List<PlayModeTransitionRecord>();
            public PlayModeTransitionRecord intent;
            public int processId;
        }
        private static readonly string PathName = Path.Combine("Library", "UPilot", "playmode-transitions.json");
        private static Ledger ledger;
        private static Ledger State
        {
            get
            {
                if (ledger != null) return ledger;
                try { ledger = JsonUtility.FromJson<Ledger>(File.ReadAllText(PathName)); } catch { }
                ledger = ledger ?? new Ledger();
                ledger.records = ledger.records ?? new List<PlayModeTransitionRecord>();
                if (ledger.processId != System.Diagnostics.Process.GetCurrentProcess().Id) ledger.intent = null;
                ledger.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                return ledger;
            }
        }
        public static PlayModeTransitionRecord Latest => State.records.LastOrDefault();
        public static PlayModeTransitionRecord[] Recent => State.records.ToArray();

        public static void RequestProjectExit(string operationId = "")
        {
            RegisterIntent("project", "edit", "", "", operationId, "project.RequestProjectExit");
            EditorApplication.isPlaying = false;
        }

        internal static void RegisterIntent(string origin, string toState, string requestId = "", string commandId = "",
            string operationId = "", string toolName = "", string runGuid = "")
        {
            State.intent = new PlayModeTransitionRecord
            {
                origin = origin, toState = toState, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                requestId = requestId, commandId = commandId, operationId = operationId, toolName = toolName, runGuid = runGuid,
            };
            Persist();
        }

        internal static void AttachRunIdentity(string commandId, string runGuid)
        {
            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(runGuid)) return;
            if (State.intent?.commandId == commandId) State.intent.runGuid = runGuid;
            foreach (var record in State.records)
                if (record.commandId == commandId && string.IsNullOrEmpty(record.runGuid)) record.runGuid = runGuid;
            Persist();
        }

        internal static void Observe(PlayModeStateChange change, string sessionId)
        {
            string from = change == PlayModeStateChange.ExitingEditMode ? "edit" :
                change == PlayModeStateChange.EnteredPlayMode ? "enteringPlay" :
                change == PlayModeStateChange.ExitingPlayMode ? "play" : "exitingPlay";
            string to = change == PlayModeStateChange.ExitingEditMode ? "enteringPlay" :
                change == PlayModeStateChange.EnteredPlayMode ? "play" :
                change == PlayModeStateChange.ExitingPlayMode ? "exitingPlay" : "edit";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var intent = State.intent;
            string destination = to == "play" || to == "enteringPlay" ? "play" : "edit";
            if (intent != null && (intent.toState != destination || now - intent.at > 15000)) intent = null;
            var record = Build(from, to, sessionId, now, intent);
            State.records.Add(record);
            if (State.records.Count > 256) State.records.RemoveRange(0, State.records.Count - 256);
            if (change == PlayModeStateChange.EnteredEditMode || change == PlayModeStateChange.EnteredPlayMode)
                State.intent = null;
            Persist();
            UPilotTestService.Instance?.ObservePlayModeTransition(record);
        }

        internal static PlayModeTransitionRecord Build(string from, string to, string session, long at, PlayModeTransitionRecord intent)
        {
            return new PlayModeTransitionRecord
            {
                transitionId = Guid.NewGuid().ToString("N"), fromState = from, toState = to, unitySessionId = session, at = at,
                origin = intent?.origin ?? "unknown", requestId = intent?.requestId ?? "", commandId = intent?.commandId ?? "",
                operationId = intent?.operationId ?? "", toolName = intent?.toolName ?? "", runGuid = intent?.runGuid ?? "",
            };
        }

        private static void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                File.WriteAllText(PathName + ".tmp", JsonUtility.ToJson(State));
                if (File.Exists(PathName)) File.Replace(PathName + ".tmp", PathName, null);
                else File.Move(PathName + ".tmp", PathName);
            }
            catch (Exception ex) { Debug.LogWarning("[UPilot] PlayMode ledger persistence failed: " + ex.Message); }
        }
    }
}
