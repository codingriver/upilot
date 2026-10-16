// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotUpdateWindowSchedulingTests
    {
        [Test] public void IdleWindowReadsOnlyAtConfiguredCadenceAndDirtyEventIsImmediate()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life");
            Assert.That(s.ShouldRead(1000, 300), Is.True);
            for (long now = 1001; now < 1300; now++) Assert.That(s.ShouldRead(now, 300), Is.False);
            Assert.That(s.ShouldRead(1300, 300), Is.True);
            s.Dirty(); Assert.That(s.ShouldRead(1301, 300), Is.True);
            Assert.That(s.ShouldRead(1302, 300), Is.False);
        }
        [Test] public void CadenceChangeMovesNextReadWithoutResettingLastRead()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life"); s.ShouldRead(1000, 300);
            s.AdjustInterval(1000); Assert.That(s.ShouldRead(1300, 1000), Is.False);
            s.AdjustInterval(100); Assert.That(s.ShouldRead(1301, 100), Is.True);
        }
        [Test] public void DuplicateTerminalNotificationDoesNotExtendOrRecreateRefresh()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life");
            Assert.That(s.Schedule("op", 1000, 10), Is.True);
            Assert.That(s.Schedule("op", 4000, 60), Is.False);
            Assert.That(s.RefreshDeadline, Is.EqualTo(11000)); Assert.That(s.FrozenDelaySeconds, Is.EqualTo(10));
            Assert.That(s.ConsumeDue(10999), Is.False); Assert.That(s.ConsumeDue(11000), Is.True);
            Assert.That(s.Schedule("op", 12000, 10), Is.False); Assert.That(s.ConsumeDue(12000), Is.False);
        }
        [Test] public void ManualCheckConsumesDelayAndLateNetworkCannotClearNewCheck()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life"); s.Schedule("op", 1000, 10);
            var old = s.BeginCheck(); var current = s.BeginCheck();
            Assert.That(s.ConsumeDue(11000), Is.False); Assert.That(s.Schedule("op", 12000, 10), Is.False);
            Assert.That(s.IsCurrent(old, "life"), Is.False); Assert.That(s.IsCurrent(current, "life"), Is.True);
        }
        [Test] public void NewOperationInvalidatesOriginalFetchAndPendingRefresh()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life"); var fetch = s.BeginCheck(); s.Schedule("old", 1000, 10);
            s.ObserveOperation("new", true);
            Assert.That(s.IsCurrent(fetch, "life"), Is.False); Assert.That(s.ConsumeDue(11000), Is.False);
            var generation = s.Generation; s.ObserveOperation("new", true); Assert.That(s.Generation, Is.EqualTo(generation));
        }
        [Test] public void CloseReloadAndServiceResetRejectLateResponses()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life"); var fetch = s.BeginCheck(); s.Schedule("op", 1000, 10);
            s.Close(); Assert.That(s.ConsumeDue(11000), Is.False); Assert.That(s.IsCurrent(fetch, "life"), Is.False);
            s.Open("life"); Assert.That(s.IsCurrent(fetch, "life"), Is.False);
            Assert.That(s.Schedule("op", 12000, 10), Is.False, "Closing must consume the old delayed check.");
            fetch = s.BeginCheck(); Assert.That(s.IsCurrent(fetch, "next-life"), Is.False);
        }
        [Test] public void RestoredWindowDiscardsHistoricalCompletionAndManifestCancellationDoesNotFetch()
        {
            var s = new UPilotUpdateWindowObservation(); s.Open("life"); s.DiscardCompletion("old");
            Assert.That(s.Schedule("old", 1000, 10), Is.False);
            Assert.That(s.Schedule("new", 1000, 10), Is.True);
            using var cancellation = new System.Threading.CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () =>
                await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync("https://example.invalid/manifest.json", cancellation.Token));
        }
        [Test] public void ActualWindowUpdateAndGuiSnapshotDoNotReadOnEveryCallback()
        {
            var key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.DebugWindowRefreshMs));
            int? saved = EditorPrefs.HasKey(key) ? EditorPrefs.GetInt(key) : (int?)null;
            UPilotUpdateWindow window = null;
            try
            {
                UPilotTimingSettings.DebugWindowRefreshMs = 5000;
                window = ScriptableObject.CreateInstance<UPilotUpdateWindow>();
                var tick = typeof(UPilotUpdateWindow).GetMethod("OnEditorUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                tick.Invoke(window, null); var count = window.SnapshotReadCount;
                for (int i = 0; i < 200; i++) tick.Invoke(window, null);
                Assert.That(window.SnapshotReadCount, Is.EqualTo(count));
                var schedule = (UPilotUpdateWindowObservation)typeof(UPilotUpdateWindow).GetField("_observation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                UnityEngine.Object.DestroyImmediate(window); window = null;
                Assert.That(schedule.Enabled, Is.False); Assert.That(schedule.PendingOperation, Is.Empty);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                if (saved.HasValue) EditorPrefs.SetInt(key, saved.Value); else EditorPrefs.DeleteKey(key);
                UPilotTimingSettings.PreferencesReset();
            }
        }
        [Test] public void NewTimingDefaultsRangesAndProjectKeysRestore()
        {
            var names = new[] { nameof(UPilotTimingSettings.UpdateCenterPostOperationDelaySeconds), nameof(UPilotTimingSettings.ServiceStopObservationTimeoutSeconds), nameof(UPilotTimingSettings.ServiceStopCheckIntervalMs) };
            var saved = new Dictionary<string, int?>();
            foreach (var name in names) { var key = UPilotTimingSettings.Key(name); saved[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetInt(key) : (int?)null; EditorPrefs.DeleteKey(key); }
            try
            {
                Assert.That(UPilotTimingSettings.UpdateCenterPostOperationDelaySeconds, Is.EqualTo(10));
                Assert.That(UPilotTimingSettings.ServiceStopObservationTimeoutSeconds, Is.EqualTo(30));
                Assert.That(UPilotTimingSettings.ServiceStopCheckIntervalMs, Is.EqualTo(100));
                foreach (var key in saved.Keys) Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Contain(key));
                UPilotTimingSettings.UpdateCenterPostOperationDelaySeconds = 999; UPilotTimingSettings.ServiceStopObservationTimeoutSeconds = 0; UPilotTimingSettings.ServiceStopCheckIntervalMs = 9999;
                Assert.That(UPilotTimingSettings.UpdateCenterPostOperationDelaySeconds, Is.EqualTo(60));
                Assert.That(UPilotTimingSettings.ServiceStopObservationTimeoutSeconds, Is.EqualTo(1));
                Assert.That(UPilotTimingSettings.ServiceStopCheckIntervalMs, Is.EqualTo(1000));
                UPilotPreferences.DeleteKeys(saved.Keys);
                Assert.That(UPilotTimingSettings.ServiceStopObservationTimeoutSeconds, Is.EqualTo(30));
                Assert.That(UPilotTimingSettings.ServiceStopCheckIntervalMs, Is.EqualTo(100));
                Assert.That(UPilotTimingSettings.UpdateCenterPostOperationDelaySeconds, Is.EqualTo(10));
            }
            finally { foreach (var pair in saved) { if (pair.Value.HasValue) EditorPrefs.SetInt(pair.Key, pair.Value.Value); else EditorPrefs.DeleteKey(pair.Key); } UPilotTimingSettings.PreferencesReset(); }
        }
        [Test] public void RepeatedUnchangedStatusNotificationKeepsCurrentCheckButClosingCancelsIt()
        {
            if (UPilotUpdateService.Instance.GetOperationStatus().IsRunning) Assert.Ignore("Preserve the active update.");
            UPilotUpdateWindow window = null;
            using var cancellation = new System.Threading.CancellationTokenSource();
            try
            {
                window = ScriptableObject.CreateInstance<UPilotUpdateWindow>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(UPilotUpdateWindow).GetField("_checkCancellation", flags).SetValue(window, cancellation);
                var notice = typeof(UPilotUpdateWindow).GetMethod("OnOperationChanged", flags);
                notice.Invoke(window, null); notice.Invoke(window, null);
                Assert.That(cancellation.IsCancellationRequested, Is.False);
                UnityEngine.Object.DestroyImmediate(window); window = null;
                Assert.That(cancellation.IsCancellationRequested, Is.True);
            }
            finally { if (window != null) UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
