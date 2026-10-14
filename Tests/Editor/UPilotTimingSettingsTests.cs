// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
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
            Assert.That(UPilotTimingSettings.WindowObservationTimeoutMs, Is.EqualTo(5000));
            Assert.That(UPilotTimingSettings.ModalCheckIntervalMs, Is.EqualTo(100));
            foreach (var key in UPilotTimingSettings.PreferenceKeys) { Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Contain(key)); Assert.That(key, Does.EndWith(UPilotPreferences.ProjectSuffix)); }
        }
        [Test] public void RangeAndCombinationValidationAndReset()
        {
            UPilotTimingSettings.ReloadRoundTimeoutSeconds = 80; UPilotTimingSettings.ReloadTotalTimeoutSeconds = 1;
            Assert.That(UPilotTimingSettings.ReloadRoundTimeoutSeconds, Is.EqualTo(60)); Assert.That(UPilotTimingSettings.ReloadTotalTimeoutSeconds, Is.EqualTo(60));
            UPilotTimingSettings.WindowObservationTimeoutMs = 1; UPilotTimingSettings.ModalCheckIntervalMs = 1000;
            Assert.That(UPilotTimingSettings.WindowObservationTimeoutMs, Is.EqualTo(500)); Assert.That(UPilotTimingSettings.ModalCheckIntervalMs, Is.EqualTo(250));
            UPilotTimingSettings.ReloadMaxAttempts = 0; Assert.That(UPilotTimingSettings.ReloadMaxAttempts, Is.EqualTo(1));
            UPilotTimingSettings.ResetDefaults(); Assert.That(UPilotTimingSettings.ReloadTotalTimeoutSeconds, Is.EqualTo(120));
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
