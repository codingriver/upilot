// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEditor;

namespace CodingRiver.UPilot
{
    /// <summary>Project-local scheduling preferences. Finite operations copy values when admitted.</summary>
    internal static class UPilotTimingSettings
    {
        internal static string Key(string name) => UPilotPreferences.ProjectKey("upilot.Timing." + name);
        private static int Read(string name, int fallback, int min, int max) =>
            Math.Max(min, Math.Min(max, EditorPrefs.GetInt(Key(name), fallback)));
        internal static event Action<string> Changed;
        private static void Save(string name, int value, int min, int max)
        {
            value = Math.Max(min, Math.Min(max, value));
            if (EditorPrefs.HasKey(Key(name)) && EditorPrefs.GetInt(Key(name)) == value) return;
            EditorPrefs.SetInt(Key(name), value);
            Changed?.Invoke(name);
        }
        internal static int WatchdogIntervalSeconds { get => Read(nameof(WatchdogIntervalSeconds), 2, 1, 60); set => Save(nameof(WatchdogIntervalSeconds), value, 1, 60); }
        internal static int ReloadIntervalSeconds { get => Read(nameof(ReloadIntervalSeconds), 2, 1, 60); set => Save(nameof(ReloadIntervalSeconds), value, 1, 60); }
        internal static int ReloadRoundTimeoutSeconds
        {
            get => Read(nameof(ReloadRoundTimeoutSeconds), 5, 1, 60);
            set { Save(nameof(ReloadRoundTimeoutSeconds), value, 1, 60); if (ReloadTotalTimeoutSeconds < ReloadRoundTimeoutSeconds) ReloadTotalTimeoutSeconds = ReloadRoundTimeoutSeconds; }
        }
        internal static int ReloadTotalTimeoutSeconds { get => Math.Max(ReloadRoundTimeoutSeconds, Read(nameof(ReloadTotalTimeoutSeconds), 120, 5, 600)); set => Save(nameof(ReloadTotalTimeoutSeconds), Math.Max(ReloadRoundTimeoutSeconds, value), 5, 600); }
        internal static int ReloadMaxAttempts { get => Read(nameof(ReloadMaxAttempts), 30, 1, 100); set => Save(nameof(ReloadMaxAttempts), value, 1, 100); }
        internal static int UpmRequestObservationTimeoutSeconds { get => Read(nameof(UpmRequestObservationTimeoutSeconds), 600, 30, 3600); set => Save(nameof(UpmRequestObservationTimeoutSeconds), value, 30, 3600); }
        internal static int UpmCompletionCheckIntervalMs { get => Read(nameof(UpmCompletionCheckIntervalMs), 100, 50, 1000); set => Save(nameof(UpmCompletionCheckIntervalMs), value, 50, 1000); }
        internal static int PackageUpdateRecoveryTimeoutSeconds { get => Read(nameof(PackageUpdateRecoveryTimeoutSeconds), 600, 30, 3600); set => Save(nameof(PackageUpdateRecoveryTimeoutSeconds), value, 30, 3600); }
        internal static int ProfilerCaptureDefaultDurationSeconds { get => Read(nameof(ProfilerCaptureDefaultDurationSeconds), 30, 1, 3600); set => Save(nameof(ProfilerCaptureDefaultDurationSeconds), value, 1, 3600); }
        internal static int WindowObservationTimeoutMs { get => Read(nameof(WindowObservationTimeoutMs), 5000, 500, 30000); set { Save(nameof(WindowObservationTimeoutMs), value, 500, 30000); ModalCheckIntervalMs = ModalCheckIntervalMs; } }
        internal static int ModalCheckIntervalMs { get => Math.Min(WindowObservationTimeoutMs / 2, Read(nameof(ModalCheckIntervalMs), 100, 50, 1000)); set => Save(nameof(ModalCheckIntervalMs), Math.Min(value, WindowObservationTimeoutMs / 2), 50, 1000); }
        internal static int BridgeReconnectDelaySeconds { get => Read(nameof(BridgeReconnectDelaySeconds), 1, 1, 60); set => Save(nameof(BridgeReconnectDelaySeconds), value, 1, 60); }
        internal static int MainWindowRefreshMs { get => Read(nameof(MainWindowRefreshMs), 400, 100, 5000); set => Save(nameof(MainWindowRefreshMs), value, 100, 5000); }
        internal static int DebugWindowRefreshMs { get => Read(nameof(DebugWindowRefreshMs), 300, 100, 5000); set => Save(nameof(DebugWindowRefreshMs), value, 100, 5000); }
        internal static int UpdateCenterPostOperationDelaySeconds { get => Read(nameof(UpdateCenterPostOperationDelaySeconds), 10, 1, 60); set => Save(nameof(UpdateCenterPostOperationDelaySeconds), value, 1, 60); }
        internal static int ServiceStopObservationTimeoutSeconds { get => Read(nameof(ServiceStopObservationTimeoutSeconds), 30, 1, 600); set => Save(nameof(ServiceStopObservationTimeoutSeconds), value, 1, 600); }
        internal static int ServiceStopCheckIntervalMs { get => Read(nameof(ServiceStopCheckIntervalMs), 100, 50, 1000); set => Save(nameof(ServiceStopCheckIntervalMs), value, 50, 1000); }
        internal static int StepWindowRefreshMs { get => Read(nameof(StepWindowRefreshMs), 1000, 100, 5000); set => Save(nameof(StepWindowRefreshMs), value, 100, 5000); }
        internal static IEnumerable<string> PreferenceKeys
        {
            get { foreach (var name in new[] { nameof(WatchdogIntervalSeconds), nameof(ReloadIntervalSeconds), nameof(ReloadRoundTimeoutSeconds), nameof(ReloadTotalTimeoutSeconds), nameof(ReloadMaxAttempts), nameof(PackageUpdateRecoveryTimeoutSeconds), nameof(UpmRequestObservationTimeoutSeconds), nameof(UpmCompletionCheckIntervalMs), nameof(ProfilerCaptureDefaultDurationSeconds), nameof(WindowObservationTimeoutMs), nameof(ModalCheckIntervalMs), nameof(BridgeReconnectDelaySeconds), nameof(MainWindowRefreshMs), nameof(DebugWindowRefreshMs), nameof(StepWindowRefreshMs), nameof(UpdateCenterPostOperationDelaySeconds), nameof(ServiceStopObservationTimeoutSeconds), nameof(ServiceStopCheckIntervalMs) }) yield return Key(name); }
        }
        internal static void ResetDefaults() { UPilotPreferences.DeleteKeys(PreferenceKeys); Changed?.Invoke(""); }
        internal static void PreferencesReset() => Changed?.Invoke("");
    }

    [Serializable]
    internal sealed class ReloadRevalidationObservation
    {
        public string observationId = "", status = "observing", endReason = "", lastReason = "";
        public int intervalSeconds, roundTimeoutSeconds, totalTimeoutSeconds, maxAttempts, attemptsUsed;
        public long startedAtUtcMs, deadlineUtcMs, nextProbeAtUtcMs, roundStartedAtUtcMs, endedAtUtcMs;
        internal static ReloadRevalidationObservation Start(long now) => new ReloadRevalidationObservation {
            observationId = Guid.NewGuid().ToString("N"), startedAtUtcMs = now,
            intervalSeconds = UPilotTimingSettings.ReloadIntervalSeconds,
            roundTimeoutSeconds = UPilotTimingSettings.ReloadRoundTimeoutSeconds,
            totalTimeoutSeconds = UPilotTimingSettings.ReloadTotalTimeoutSeconds,
            maxAttempts = UPilotTimingSettings.ReloadMaxAttempts,
            deadlineUtcMs = now + UPilotTimingSettings.ReloadTotalTimeoutSeconds * 1000L,
        };
        internal static ReloadRevalidationObservation ForReload(ReloadRevalidationObservation previous, long now) =>
            previous == null || string.IsNullOrEmpty(previous.observationId) || previous.status == "succeeded" ? Start(now) : previous;
        internal string LimitReason(long now) => now >= deadlineUtcMs ? "reload_revalidation_timeout" : attemptsUsed >= maxAttempts ? "reload_revalidation_attempts_exhausted" : "";
        internal long RoundDeadline(long now) => Math.Min(deadlineUtcMs, now + roundTimeoutSeconds * 1000L);
        internal bool TryBeginRound(long now)
        {
            if (status != "observing" || LimitReason(now).Length != 0 || now < nextProbeAtUtcMs) return false;
            ++attemptsUsed; roundStartedAtUtcMs = now; return true;
        }
        internal void RoundEnded(long now, string reason) { lastReason = reason ?? ""; nextProbeAtUtcMs = now + intervalSeconds * 1000L; }
        internal void Finish(long now, bool success, string reason)
        { if (status != "observing") return; status = success ? "succeeded" : "unverified"; endReason = reason ?? ""; endedAtUtcMs = now; }
    }
}
