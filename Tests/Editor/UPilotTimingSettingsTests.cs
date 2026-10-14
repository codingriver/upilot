// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotTimingSettingsTests
    {
        private readonly Dictionary<string, int?> saved = new Dictionary<string, int?>();
        [SetUp] public void SetUp() { foreach (var key in UPilotTimingSettings.PreferenceKeys) { saved[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetInt(key) : (int?)null; EditorPrefs.DeleteKey(key); } }
        [TearDown] public void TearDown() { foreach (var pair in saved) { if (pair.Value.HasValue) EditorPrefs.SetInt(pair.Key, pair.Value.Value); else EditorPrefs.DeleteKey(pair.Key); } saved.Clear(); UPilotTimingSettings.PreferencesReset(); }
        [Test] public void DefaultsAndProjectKeysAreRegistered()
        {
            Assert.That(UPilotTimingSettings.ReloadIntervalSeconds, Is.EqualTo(2));
            Assert.That(UPilotTimingSettings.ReloadRoundTimeoutSeconds, Is.EqualTo(5));
            Assert.That(UPilotTimingSettings.ReloadTotalTimeoutSeconds, Is.EqualTo(120));
            Assert.That(UPilotTimingSettings.ReloadMaxAttempts, Is.EqualTo(30));
            Assert.That(UPilotTimingSettings.PackageUpdateRecoveryTimeoutSeconds, Is.EqualTo(600));
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(600));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(100));
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(30));
            Assert.That(UPilotTimingSettings.WindowObservationTimeoutMs, Is.EqualTo(5000));
            Assert.That(UPilotTimingSettings.ModalCheckIntervalMs, Is.EqualTo(100));
            Assert.That(UPilotTimingSettings.WatchdogIntervalSeconds, Is.EqualTo(2));
            Assert.That(UPilotTimingSettings.PreferenceKeys, Does.Contain(UPilotTimingSettings.Key(nameof(UPilotTimingSettings.WatchdogIntervalSeconds))));
            foreach (var key in UPilotTimingSettings.PreferenceKeys) { Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Contain(key)); Assert.That(key, Does.EndWith(UPilotPreferences.ProjectSuffix)); }
        }
        [Test] public void RangeAndCombinationValidationAndReset()
        {
            UPilotTimingSettings.ReloadRoundTimeoutSeconds = 80; UPilotTimingSettings.ReloadTotalTimeoutSeconds = 1;
            Assert.That(UPilotTimingSettings.ReloadRoundTimeoutSeconds, Is.EqualTo(60)); Assert.That(UPilotTimingSettings.ReloadTotalTimeoutSeconds, Is.EqualTo(60));
            UPilotTimingSettings.WindowObservationTimeoutMs = 1; UPilotTimingSettings.ModalCheckIntervalMs = 1000;
            Assert.That(UPilotTimingSettings.WindowObservationTimeoutMs, Is.EqualTo(500)); Assert.That(UPilotTimingSettings.ModalCheckIntervalMs, Is.EqualTo(250));
            UPilotTimingSettings.ReloadMaxAttempts = 0; Assert.That(UPilotTimingSettings.ReloadMaxAttempts, Is.EqualTo(1));
            UPilotTimingSettings.WatchdogIntervalSeconds = int.MinValue; Assert.That(UPilotTimingSettings.WatchdogIntervalSeconds, Is.EqualTo(1));
            UPilotTimingSettings.WatchdogIntervalSeconds = int.MaxValue; Assert.That(UPilotTimingSettings.WatchdogIntervalSeconds, Is.EqualTo(60));
            UPilotTimingSettings.UpmRequestObservationTimeoutSeconds = 1; UPilotTimingSettings.UpmCompletionCheckIntervalMs = int.MaxValue;
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(30));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(1000));
            UPilotTimingSettings.ResetDefaults(); Assert.That(UPilotTimingSettings.ReloadTotalTimeoutSeconds, Is.EqualTo(120));
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(600));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(100));
            Assert.That(UPilotTimingSettings.WatchdogIntervalSeconds, Is.EqualTo(2));
        }
        [Test] public void WatchdogPreferencePersistsAndUpdatesBridgeCacheIndependentlyOfReconnect()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            const string name = nameof(UPilotTimingSettings.WatchdogIntervalSeconds);
            var key = UPilotTimingSettings.Key(name);
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            using var reconnectChanged = new SemaphoreSlim(0, 1);
            typeof(UPilotBridge).GetField("_reconnectIntervalChanged", flags).SetValue(bridge, reconnectChanged);
            var changed = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), bridge,
                typeof(UPilotBridge).GetMethod("ApplyTimingPreferenceChange", flags));
            changed("");
            reconnectChanged.Wait(0);
            UPilotTimingSettings.Changed += changed;
            try
            {
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(2000));
                UPilotTimingSettings.WatchdogIntervalSeconds = 7;
                Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(7));
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(7000));
                Assert.That(bridge.ReconnectDelayMs, Is.EqualTo(1000));
                Assert.That(reconnectChanged.Wait(0), Is.False, "Watchdog changes must not wake the reconnect loop.");
                UPilotTimingSettings.BridgeReconnectDelaySeconds = 3;
                Assert.That(bridge.ReconnectDelayMs, Is.EqualTo(3000));
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(7000));
                Assert.That(reconnectChanged.Wait(0), Is.True);

                // Disk preferences alone do not cause a per-frame cache read.
                EditorPrefs.SetInt(key, 0);
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(7000));
                UPilotTimingSettings.PreferencesReset();
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(1000));
                EditorPrefs.SetInt(key, 99);
                UPilotTimingSettings.PreferencesReset();
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(60000));
                UPilotTimingSettings.ResetDefaults();
                Assert.That(EditorPrefs.HasKey(key), Is.False);
                Assert.That(bridge.WatchdogIntervalMs, Is.EqualTo(2000));
                Assert.That(bridge.ReconnectDelayMs, Is.EqualTo(1000));
            }
            finally { UPilotTimingSettings.Changed -= changed; }
        }
        [Test] public void ProfilerDurationPersistsClampsAndResets()
        {
            string key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds));
            UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds = 45;
            Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(45));
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(45));
            UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds = 0;
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(1));
            UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds = 99999;
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(3600));
            EditorPrefs.SetInt(key, -1);
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(1));
            UPilotTimingSettings.ResetDefaults();
            Assert.That(EditorPrefs.HasKey(key), Is.False);
            Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(30));
        }
        [Test] public void ActiveProfilerDurationSurvivesSettingsChangesAndReset()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var type = typeof(UPilotRuntimeDiagnosticsService);
            var stop = type.GetMethod("StopProfiler", flags);
            UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds = 45;
            var started = (ProfilerCaptureResultPayload)type.GetMethod("StartProfiler", flags).Invoke(null,
                new object[] { new ProfilerCaptureStartPayload { useConfiguredDuration = true, durationSec = 30f,
                    includeDefaultAiMarkers = false, maxMarkers = 0, title = "profiler-duration-reset" } });
            try
            {
                Assert.That(started.durationSec, Is.EqualTo(45f));
                UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds = 60;
                var status = (ProfilerCaptureResultPayload)type.GetMethod("GetProfilerStatus", flags).Invoke(null, new object[] { started.captureId });
                Assert.That(status.durationSec, Is.EqualTo(45f));
                UPilotTimingSettings.ResetDefaults();
                Assert.That(UPilotTimingSettings.ProfilerCaptureDefaultDurationSeconds, Is.EqualTo(30));
                status = (ProfilerCaptureResultPayload)type.GetMethod("GetProfilerStatus", flags).Invoke(null, new object[] { started.captureId });
                Assert.That(status.captureId, Is.EqualTo(started.captureId));
                Assert.That(status.status, Is.EqualTo("Running"));
                Assert.That(status.durationSec, Is.EqualTo(45f));
            }
            finally { stop.Invoke(null, new object[] { started.captureId, "Stopped" }); }
        }
        [Test] public void ReloadObservationFreezesBudgetAcrossSettingsAndSerialization()
        {
            var first = ReloadRevalidationObservation.Start(1000); Assert.That(first.TryBeginRound(1000), Is.True);
            first.RoundEnded(1200, "unavailable"); UPilotTimingSettings.ReloadMaxAttempts = 1; UPilotTimingSettings.ReloadTotalTimeoutSeconds = 600;
            var restored = JsonUtility.FromJson<ReloadRevalidationObservation>(JsonUtility.ToJson(first));
            Assert.That(restored.observationId, Is.EqualTo(first.observationId)); Assert.That(restored.deadlineUtcMs, Is.EqualTo(121000)); Assert.That(restored.attemptsUsed, Is.EqualTo(1)); Assert.That(restored.maxAttempts, Is.EqualTo(30));
            Assert.That(restored.TryBeginRound(3199), Is.False); Assert.That(restored.TryBeginRound(3200), Is.True);
            UPilotTimingSettings.ResetDefaults(); Assert.That(restored.deadlineUtcMs, Is.EqualTo(121000));
        }
        [Test] public void LegacyInlineEmptyRecordStartsFiniteObservationAndActiveOrFailedRecordsNeverRenew()
        {
            var legacy = JsonUtility.FromJson<ServerLaunchState>(JsonUtility.ToJson(new ServerLaunchState { readyVerified = true }));
            var observation = ReloadRevalidationObservation.ForReload(legacy.reloadRevalidation, 1000);
            Assert.That(observation.observationId, Is.Not.Empty); Assert.That(observation.deadlineUtcMs, Is.EqualTo(121000));
            observation.TryBeginRound(1000);
            Assert.That(ReloadRevalidationObservation.ForReload(observation, 2000), Is.SameAs(observation));
            observation.Finish(121000, false, "expired");
            Assert.That(ReloadRevalidationObservation.ForReload(observation, 200000), Is.SameAs(observation));
            Assert.That(new ServerLaunchState().Snapshot().reloadRevalidation, Is.Null);
        }
        [Test] public void ReloadStopsAtFirstLimitAndRoundSharesRemainingDeadline()
        {
            var observation = new ReloadRevalidationObservation { observationId = "fixture", deadlineUtcMs = 10000, maxAttempts = 2, roundTimeoutSeconds = 5, intervalSeconds = 2 };
            Assert.That(observation.TryBeginRound(0), Is.True); observation.RoundEnded(10, "failure");
            Assert.That(observation.TryBeginRound(2010), Is.True); Assert.That(observation.LimitReason(2011), Is.EqualTo("reload_revalidation_attempts_exhausted"));
            Assert.That(observation.RoundDeadline(9000), Is.EqualTo(10000)); Assert.That(observation.LimitReason(10000), Is.EqualTo("reload_revalidation_timeout"));
            observation.Finish(10000, false, observation.LimitReason(10000)); Assert.That(observation.TryBeginRound(10001), Is.False);
            observation.Finish(10002, true, "late success"); Assert.That(observation.status, Is.EqualTo("unverified"));
        }
    }
}
