// -----------------------------------------------------------------------
// UPilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class EditorWindowHistoryEvent
    {
        public long sequence;
        public long observedAtUtcMs;
        public string kind;
        public string instanceId;
        public string domainGeneration;
        public string fullTypeName;
        public string title;
        public float x;
        public float y;
        public float width;
        public float height;
        public bool docked;
        // Kept for clients that consumed the pre-v2 event shape. -1 means unavailable.
        public long consoleSequence;
        public bool consoleSequenceAvailable;
        public string consoleSessionId;
        public long consoleSequenceStart;
        public long consoleSequenceEndExclusive;
        public string failureReason;
        public bool failureReasonAuthoritative;
        // Only the self-owned safe probe supplies this opaque lifecycle key. It links
        // its pre- and post-reload observations without treating an instance ID as
        // cross-domain identity.
        public string lifecycleKey;
    }

    [Serializable]
    public sealed class EditorWindowHistoryResultPayload
    {
        public List<EditorWindowHistoryEvent> events = new List<EditorWindowHistoryEvent>();
        public long earliestSequence;
        public long latestSequence;
        public bool truncated;
        public bool restoredPreviousDomain;
        public bool gap;
        public string gapReason;
        public long gapAfterSequence;
        public string samplingError;
        public string persistenceError;
        public int samplingIntervalMs;
        public int observationLimit;
    }

    [Serializable]
    internal sealed class EditorWindowHistoryStore
    {
        public int schemaVersion = 2;
        public long lastSequence;
        public List<EditorWindowHistoryEvent> events = new List<EditorWindowHistoryEvent>();
    }

    [InitializeOnLoad]
    internal static class UPilotWindowHistory
    {
        private const int Capacity = 512;
        private const double SampleIntervalSeconds = 0.25d;
        private const string RelativePath = "Library/UPilot/window-history.json";
        private static readonly List<EditorWindowHistoryEvent> Events = new List<EditorWindowHistoryEvent>(Capacity);
        private static readonly Dictionary<string, WindowState> Known = new Dictionary<string, WindowState>(StringComparer.Ordinal);
        private static long _lastSequence;
        private static double _nextSampleAt;
        private static string _persistenceError = string.Empty;
        private static bool _restoredPreviousDomain;
        private static long _restoredDomainBoundarySequence;
        private static bool _samplingGap;
        private static long _samplingGapAfterSequence;
        private static string _samplingGapReason = string.Empty;
        private static string _samplingError = string.Empty;

        static UPilotWindowHistory()
        {
            RestoreLastDomainSegment();
            EditorApplication.update -= SampleOnUpdate;
            EditorApplication.update += SampleOnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= PersistAtReloadBoundary;
            AssemblyReloadEvents.beforeAssemblyReload += PersistAtReloadBoundary;
            EditorApplication.quitting -= PersistAtReloadBoundary;
            EditorApplication.quitting += PersistAtReloadBoundary;
        }

        internal static EditorWindowHistoryResultPayload Query(string instanceId, long afterSequence, int count)
        {
            var earliest = Events.Count == 0 ? _lastSequence + 1 : Events[0].sequence;
            var truncated = afterSequence > 0 && Events.Count > 0 && afterSequence < earliest - 1;
            var crossesRestoredDomainBoundary = _restoredPreviousDomain
                && _restoredDomainBoundarySequence > 0
                && afterSequence <= _restoredDomainBoundarySequence;
            var gapReasons = new List<string>();
            var gapBoundaries = new List<long>();
            if (truncated)
            {
                gapReasons.Add("history_truncated");
                gapBoundaries.Add(Math.Max(0, earliest - 1));
            }
            if (crossesRestoredDomainBoundary)
            {
                gapReasons.Add("domain_reload_boundary");
                gapBoundaries.Add(_restoredDomainBoundarySequence);
            }
            if (_samplingGap)
            {
                gapReasons.Add(string.IsNullOrEmpty(_samplingGapReason)
                    ? "window_sampling_failed"
                    : _samplingGapReason);
                gapBoundaries.Add(_samplingGapAfterSequence);
            }
            var result = new EditorWindowHistoryResultPayload
            {
                earliestSequence = earliest,
                latestSequence = _lastSequence,
                truncated = truncated,
                restoredPreviousDomain = _restoredPreviousDomain,
                gap = gapReasons.Count > 0,
                gapReason = string.Join(",", gapReasons),
                gapAfterSequence = gapBoundaries.Count > 0 ? gapBoundaries.Min() : 0,
                samplingError = _samplingError,
                persistenceError = _persistenceError,
                samplingIntervalMs = (int)(SampleIntervalSeconds * 1000d),
                observationLimit = Capacity,
            };
            foreach (var item in Events)
            {
                if (item.sequence <= afterSequence) continue;
                if (!string.IsNullOrWhiteSpace(instanceId)
                    && !string.Equals(item.instanceId, instanceId, StringComparison.Ordinal)) continue;
                result.events.Add(item);
                if (result.events.Count == count) break;
            }
            return result;
        }

        // Explicit hooks keep tests deterministic without making the public history
        // query mutate observations or persist files.
        internal static void SampleForTests() => Sample(force: true);

        internal static void PersistForTests() => Persist();

        private static void SampleOnUpdate() => Sample(force: false);

        private static void Sample(bool force)
        {
            var now = EditorApplication.timeSinceStartup;
            if (!force && now < _nextSampleAt) return;
            _nextSampleAt = now + SampleIntervalSeconds;
            var current = new Dictionary<string, WindowState>(StringComparer.Ordinal);
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null) continue;
                EditorWindowInfo info;
                try
                {
                    info = UPilotWindowService.BuildWindowInfo(window);
                }
                catch (Exception ex)
                {
                    RecordObservedGap("window_sampling_failed", ex.GetType().Name);
                    continue;
                }
                if (info == null || info.instanceId == 0) continue;
                var state = WindowState.From(info);
                current[state.instanceId] = state;
                if (!Known.TryGetValue(state.instanceId, out var before))
                {
                    Append("observed-open", state);
                    continue;
                }
                if (!string.Equals(before.title, state.title, StringComparison.Ordinal))
                    Append("title-changed", state);
                if (!before.HasSameGeometry(state))
                    Append("geometry-changed", state);
            }
            foreach (var pair in Known)
            {
                if (!current.ContainsKey(pair.Key))
                    Append("closed-or-lost", pair.Value, "not_observed_on_next_sample", failureReasonAuthoritative: false);
            }
            Known.Clear();
            foreach (var pair in current) Known[pair.Key] = pair.Value;
        }

        internal static void RecordSafeProbeEnabled(EditorWindow window, string lifecycleKey, bool rebuiltAfterReload)
        {
            try
            {
                if (window == null) return;
                var info = UPilotWindowService.BuildWindowInfo(window);
                if (info == null || info.instanceId == 0) return;
                Append(
                    rebuiltAfterReload ? "probe-rebuilt" : "probe-enabled",
                    WindowState.From(info),
                    lifecycleKey: lifecycleKey);
            }
            catch (Exception ex)
            {
                // This is an observation diagnostic only. A failed enable/sample cannot
                // establish why a window later disappeared.
                RecordObservedGap("window_lifecycle_observation_failed", ex.GetType().Name);
            }
        }

        internal static void RecordObservedGap(string reason, string exceptionType)
        {
            _samplingGap = true;
            _samplingGapAfterSequence = _lastSequence;
            _samplingGapReason = string.IsNullOrEmpty(reason) ? "window_sampling_failed" : reason;
            _samplingError = exceptionType ?? string.Empty;
        }

        private static void Append(
            string kind,
            WindowState state,
            string failureReason = "",
            bool failureReasonAuthoritative = false,
            string lifecycleKey = "")
        {
            bool consoleSequenceAvailable = UPilotConsoleCaptureService.TryGetActiveSequenceBoundary(
                out var consoleSessionId, out var consoleSequenceEndExclusive);
            Events.Add(new EditorWindowHistoryEvent
            {
                sequence = ++_lastSequence,
                observedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                kind = kind,
                instanceId = state.instanceId,
                domainGeneration = state.domainGeneration,
                fullTypeName = state.fullTypeName,
                title = state.title,
                x = state.x,
                y = state.y,
                width = state.width,
                height = state.height,
                docked = state.docked,
                consoleSequence = consoleSequenceAvailable ? consoleSequenceEndExclusive : -1,
                consoleSequenceAvailable = consoleSequenceAvailable,
                consoleSessionId = consoleSequenceAvailable ? consoleSessionId : string.Empty,
                // A history event is an instantaneous observation. Its range contains all
                // capture records known to precede that observation, not an inferred error span.
                consoleSequenceStart = consoleSequenceAvailable ? 0 : -1,
                consoleSequenceEndExclusive = consoleSequenceAvailable ? consoleSequenceEndExclusive : -1,
                failureReason = failureReason ?? string.Empty,
                failureReasonAuthoritative = failureReasonAuthoritative,
                lifecycleKey = lifecycleKey ?? string.Empty,
            });
            if (Events.Count > Capacity) Events.RemoveAt(0);
        }

        private static void PersistAtReloadBoundary()
        {
            Sample(force: true);
            Persist();
        }

        private static void Persist()
        {
            try
            {
                var store = new EditorWindowHistoryStore { lastSequence = _lastSequence, events = Events };
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(store, true));
                var path = Path.Combine(ProjectRoot(), RelativePath);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    _persistenceError = string.Empty;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception ex)
            {
                _persistenceError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void RestoreLastDomainSegment()
        {
            try
            {
                var path = Path.Combine(ProjectRoot(), RelativePath);
                if (!File.Exists(path)) return;
                var store = JsonUtility.FromJson<EditorWindowHistoryStore>(File.ReadAllText(path));
                if (store == null || (store.schemaVersion != 1 && store.schemaVersion != 2) || store.events == null) return;
                if (store.schemaVersion == 1)
                {
                    foreach (var item in store.events)
                    {
                        item.consoleSequence = -1;
                        item.consoleSequenceAvailable = false;
                        item.consoleSessionId = string.Empty;
                        item.consoleSequenceStart = -1;
                        item.consoleSequenceEndExclusive = -1;
                        item.failureReason = string.Empty;
                        item.failureReasonAuthoritative = false;
                    }
                }
                _lastSequence = Math.Max(0, store.lastSequence);
                var last = store.events.LastOrDefault();
                if (last == null || string.IsNullOrEmpty(last.domainGeneration)) return;
                var segment = store.events
                    .AsEnumerable()
                    .Reverse()
                    .TakeWhile(item => string.Equals(item.domainGeneration, last.domainGeneration, StringComparison.Ordinal))
                    .Reverse()
                    .Take(Capacity)
                    .ToList();
                Events.AddRange(segment);
                _restoredPreviousDomain = Events.Count > 0;
                _restoredDomainBoundarySequence = _restoredPreviousDomain
                    ? Events[Events.Count - 1].sequence
                    : 0;
            }
            catch (Exception ex)
            {
                _persistenceError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private sealed class WindowState
        {
            public string instanceId;
            public string domainGeneration;
            public string fullTypeName;
            public string title;
            public float x;
            public float y;
            public float width;
            public float height;
            public bool docked;

            public static WindowState From(EditorWindowInfo info) => new WindowState
            {
                instanceId = info.instanceId.ToString(CultureInfo.InvariantCulture), domainGeneration = info.domainGeneration,
                fullTypeName = info.fullTypeName, title = info.title, x = info.posX, y = info.posY,
                width = info.width, height = info.height, docked = info.docked,
            };

            public bool HasSameGeometry(WindowState other) =>
                x.Equals(other.x) && y.Equals(other.y) && width.Equals(other.width) && height.Equals(other.height) && docked == other.docked;
        }
    }
}
