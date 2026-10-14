// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotUpmObservationTests
    {
        private readonly object _owner = new object();
        private readonly Dictionary<string, int?> _saved = new Dictionary<string, int?>();
        private long _now;
        private int _starts, _checks, _successes, _failures;
        private bool _completed;
        private Exception _error, _failure;

        [SetUp] public void SetUp()
        {
            foreach (var name in new[] { nameof(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds), nameof(UPilotTimingSettings.UpmCompletionCheckIntervalMs) })
            {
                var key = UPilotTimingSettings.Key(name);
                _saved[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetInt(key) : (int?)null;
                EditorPrefs.DeleteKey(key);
            }
            _now = 1000; _starts = _checks = _successes = _failures = 0;
            _completed = false; _error = _failure = null;
        }
        [TearDown] public void TearDown()
        {
            UPilotUpmRequestObservation.ResetOwner(_owner, "fixture_end");
            foreach (var pair in _saved) { if (pair.Value.HasValue) EditorPrefs.SetInt(pair.Key, pair.Value.Value); else EditorPrefs.DeleteKey(pair.Key); }
            _saved.Clear(); UPilotTimingSettings.PreferencesReset();
        }
        private UPilotUpmRequestObservation Create(CancellationToken token = default, Action start = null, Action success = null) =>
            UPilotUpmRequestObservation.Create(_owner, "fixture-" + Guid.NewGuid().ToString("N"), "package.list",
                start ?? (() => ++_starts), () => { ++_checks; return _completed; }, () => _error,
                success ?? (() => ++_successes), ex => { ++_failures; _failure = ex; }, token, () => _now);

        [Test] public void DiagnosticsAndQueuedResetNeverStartAnUpmRequest()
        {
            var observation = Create();
            Assert.That(UPilotUpmRequestObservation.Diagnostics(), Does.Contain(observation));
            Assert.That(_starts, Is.Zero);
            Assert.That(observation.IsSubscribed, Is.False);
            UPilotUpmRequestObservation.ResetOwner(_owner, "reset_queued");
            observation.Start();
            Assert.That(_starts, Is.Zero); Assert.That(_failures, Is.EqualTo(1));
            Assert.That(_failure, Is.TypeOf<OperationCanceledException>());
        }
        [Test] public void IncompleteRequestReturnsImmediatelyAndChecksOnlyAtFrozenInterval()
        {
            var observation = Create(); observation.Start(); observation.Start();
            Assert.That(_starts, Is.EqualTo(1)); Assert.That(_checks, Is.EqualTo(1));
            _now += 99; observation.Tick(); Assert.That(_checks, Is.EqualTo(1));
            ++_now; observation.Tick(); Assert.That(_checks, Is.EqualTo(2));
            Assert.That(observation.IsSubscribed, Is.True);
        }
        [UnityTest] public IEnumerator AnUncompletedObservationAllowsOtherEditorCallbacksToRun()
        {
            var observation = Create(); observation.Start();
            bool ran = false;
            EditorApplication.CallbackFunction callback = () => ran = true;
            EditorApplication.update += callback;
            try
            {
                for (var i = 0; i < 20 && !ran; ++i) yield return null;
                Assert.That(ran, Is.True, "The Editor must keep pumping while UPM is incomplete.");
                Assert.That(observation.Status, Is.EqualTo("observing"));
                Assert.That(_starts, Is.EqualTo(1));
            }
            finally { EditorApplication.update -= callback; observation.Cancel("fixture_end"); }
        }
        [Test] public void RunningConfigurationChangesCannotExtendOrAccelerateCurrentObservation()
        {
            UPilotTimingSettings.UpmRequestObservationTimeoutSeconds = 30;
            UPilotTimingSettings.UpmCompletionCheckIntervalMs = 1000;
            var observation = Create(); observation.Start();
            UPilotTimingSettings.UpmRequestObservationTimeoutSeconds = 3600;
            UPilotTimingSettings.UpmCompletionCheckIntervalMs = 50;
            _now += 100; observation.Tick(); Assert.That(_checks, Is.EqualTo(1));
            Assert.That(observation.DeadlineUtcMs, Is.EqualTo(31000));
            _now = 31000; observation.Tick();
            Assert.That(_failure, Is.TypeOf<UPilotUpmRequestException>());
            Assert.That(((UPilotUpmRequestException)_failure).Code, Is.EqualTo("UPM_OBSERVATION_TIMEOUT"));
            Assert.That(observation.IsSubscribed, Is.False); Assert.That(_starts, Is.EqualTo(1));
            var next = Create(); next.Start(); Assert.That(next.TimeoutSeconds, Is.EqualTo(3600)); Assert.That(next.CheckIntervalMs, Is.EqualTo(50));
        }
        [Test] public void CancellationIsCheckedBeforeCompletionInterval()
        {
            using var cancellation = new CancellationTokenSource();
            UPilotTimingSettings.UpmCompletionCheckIntervalMs = 1000;
            var observation = Create(cancellation.Token); observation.Start(); cancellation.Cancel();
            ++_now; observation.Tick();
            Assert.That(observation.IsSubscribed, Is.False); Assert.That(_checks, Is.EqualTo(1)); Assert.That(_failures, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)] public void CompletionReleasesBeforeDeliveringSuccessOrFailure(bool failed)
        {
            var observation = Create(); observation.Start();
            _completed = true; if (failed) _error = new InvalidOperationException("UPM failed");
            _now += 100; observation.Tick(); observation.Tick(); observation.Cancel("late_stop");
            Assert.That(observation.IsSubscribed, Is.False);
            Assert.That(_starts, Is.EqualTo(1)); Assert.That(_successes + _failures, Is.EqualTo(1));
            Assert.That(observation.Status, Is.EqualTo(failed ? "failed" : "succeeded"));
        }
        [Test] public void ThrowingCompletionCallbackCannotLeaveSubscriptionOrSuppressFailureDelivery()
        {
            var observation = Create(success: () => throw new InvalidOperationException("projection failed"));
            observation.Start(); _completed = true; _now += 100; observation.Tick();
            Assert.That(observation.IsSubscribed, Is.False); Assert.That(_failures, Is.EqualTo(1));
            Assert.That(_failure.Message, Is.EqualTo("projection failed"));
        }
        [Test] public void StartFailureEndsWithoutSubscription()
        {
            var observation = Create(start: () => throw new InvalidOperationException("start failed")); observation.Start();
            Assert.That(observation.IsSubscribed, Is.False); Assert.That(_failures, Is.EqualTo(1)); Assert.That(_checks, Is.Zero);
        }
        [Test] public void ResetAndLateResultsPreserveSuccessorObservation()
        {
            var old = Create(); old.Start(); UPilotUpmRequestObservation.ResetOwner(_owner, "reset");
            var next = Create(); next.Start(); _completed = true; _now += 100; old.Tick();
            Assert.That(_successes, Is.Zero); Assert.That(next.IsSubscribed, Is.True);
            next.Tick(); Assert.That(_successes, Is.EqualTo(1)); Assert.That(_failures, Is.EqualTo(1));
        }
        [Test] public void MissingPreferencesRangesAndDefaultRestoreAreProjectLocal()
        {
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(600));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(100));
            UPilotTimingSettings.UpmRequestObservationTimeoutSeconds = int.MaxValue;
            UPilotTimingSettings.UpmCompletionCheckIntervalMs = int.MinValue;
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(3600));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(50));
            foreach (var key in _saved.Keys) Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Contain(key));
            foreach (var key in _saved.Keys) EditorPrefs.DeleteKey(key);
            Assert.That(UPilotTimingSettings.UpmRequestObservationTimeoutSeconds, Is.EqualTo(600));
            Assert.That(UPilotTimingSettings.UpmCompletionCheckIntervalMs, Is.EqualTo(100));
        }
    }
}
