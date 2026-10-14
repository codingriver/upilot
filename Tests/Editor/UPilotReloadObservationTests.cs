// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotReloadObservationTests
    {
        private const string LaunchKey = "UPilot.ServerLaunchIdentity.v1";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private string _savedLaunch;
        private readonly List<UPilotMcpServerManager> _managers = new List<UPilotMcpServerManager>();

        [SetUp]
        public void SaveLaunchState() => _savedLaunch = SessionState.GetString(LaunchKey, "");

        [TearDown]
        public void RestoreLaunchState()
        {
            foreach (var manager in _managers)
            {
                Call(manager, "StopLaunchObservation");
                var cancellation = Field<CancellationTokenSource>(manager, "_lifecycleQueryCancellation");
                cancellation?.Cancel();
                cancellation?.Dispose();
            }
            _managers.Clear();
            if (string.IsNullOrEmpty(_savedLaunch)) SessionState.EraseString(LaunchKey);
            else SessionState.SetString(LaunchKey, _savedLaunch);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RealLaunchObserverStopsAtAttemptsOrDeadlineIncludingAnInFlightRound(bool deadlineDuringRound)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var observation = new ReloadRevalidationObservation
            {
                observationId = "reload-limit-" + Guid.NewGuid().ToString("N"),
                intervalSeconds = 2, roundTimeoutSeconds = 5, totalTimeoutSeconds = 120,
                maxAttempts = 2, attemptsUsed = deadlineDuringRound ? 1 : 2,
                startedAtUtcMs = now - 1000, deadlineUtcMs = deadlineDuringRound ? now - 1 : now + 60000,
            };
            var state = new ServerLaunchState
            {
                verified = true, readyVerified = true, deadlineUtcMs = now - 200000,
                reloadRevalidation = observation,
            };
            var manager = Manager();
            Set(manager, "_launchState", state);
            Set(manager, "_launchNeedsRevalidation", true);
            Set(manager, "_launchReloadRevalidation", true);
            manager.SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, 0, observation.observationId, observation.deadlineUtcMs);
            Call(manager, "BeginLaunchObservation");
            var token = Field<CancellationTokenSource>(manager, "_launchObservationCancellation").Token;
            Set(manager, "_launchProbeRunning", deadlineDuringRound);
            Assert.That(HasObserver(manager), Is.True);

            Call(manager, "ObserveLaunch");

            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(HasObserver(manager), Is.False);
            Assert.That(Field<bool>(manager, "_launchProbeRunning"), Is.False);
            Assert.That(Field<CancellationTokenSource>(manager, "_launchObservationCancellation"), Is.Null);
            Assert.That(state.terminal, Is.True);
            Assert.That(observation.status, Is.EqualTo("unverified"));
            Assert.That(observation.endReason, Is.EqualTo(deadlineDuringRound
                ? "reload_revalidation_timeout" : "reload_revalidation_attempts_exhausted"));
            Assert.That(observation.endedAtUtcMs, Is.GreaterThan(0));
            Assert.That(manager.PeriodicHealthChecksAllowed, Is.False);
            var persisted = JsonUtility.FromJson<ServerLaunchState>(SessionState.GetString(LaunchKey, ""));
            Assert.That(persisted.reloadRevalidation.observationId, Is.EqualTo(observation.observationId));
            Assert.That(persisted.reloadRevalidation.deadlineUtcMs, Is.EqualTo(observation.deadlineUtcMs));
            Assert.That(persisted.reloadRevalidation.maxAttempts, Is.EqualTo(2));
            Assert.That(persisted.reloadRevalidation.attemptsUsed, Is.EqualTo(deadlineDuringRound ? 1 : 2));
            observation.Finish(now + 1, true, "late verification");
            Call(manager, "ObserveLaunch");
            Assert.That(observation.status, Is.EqualTo("unverified"));
            Assert.That(HasObserver(manager), Is.False);
        }

        [TestCase("observing")]
        [TestCase("succeeded")]
        [TestCase("unverified")]
        public void RestoreIdentityFailureEndsPreparedRecordWithoutRenewingActiveOrFailedBudget(string previousStatus)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var previous = new ReloadRevalidationObservation
            {
                observationId = "previous-" + Guid.NewGuid().ToString("N"), status = previousStatus,
                intervalSeconds = 2, roundTimeoutSeconds = 5, totalTimeoutSeconds = 120,
                maxAttempts = 4, attemptsUsed = 3, startedAtUtcMs = now - 1000,
                deadlineUtcMs = now + 60000, endedAtUtcMs = previousStatus == "observing" ? 0 : now - 10,
                endReason = previousStatus == "unverified" ? "previous_failure" : "",
            };
            string executable = Path.Combine(UPilotProjectConfig.ProjectRoot, "ReloadIdentityFixture.exe");
            var state = new ServerLaunchState
            {
                // Validate accepts frozen absolute identity without launching a process.
                spec = new ServerLaunchSpec
                {
                    executablePath = executable, expectedEntry = executable,
                    projectPath = UPilotProjectConfig.ProjectRoot, serviceExecutablePaths = new[] { executable },
                    environmentKeys = Array.Empty<string>(), environmentValues = Array.Empty<string>(),
                    httpPort = 18011, wsPort = 18765,
                },
                // Negative PIDs fail deterministically before any real process handle is acquired.
                launcherPid = -1, servicePid = -2, launcherCreatedAt = 1, serviceCreatedAt = 2,
                verified = true, readyVerified = true, deadlineUtcMs = now - 200000,
                terminal = previousStatus == "unverified", reloadRevalidation = previous,
            };
            Assert.That(state.spec.Validate(), Is.Empty);
            SessionState.SetString(LaunchKey, JsonUtility.ToJson(state));
            var manager = Manager();
            Call(manager, "BeginLaunchObservation");
            var token = Field<CancellationTokenSource>(manager, "_launchObservationCancellation").Token;

            Call(manager, "RestoreLaunchObservation");

            var restored = manager.LaunchIdentity;
            Assert.That(restored.terminal, Is.True);
            Assert.That(restored.error, Does.StartWith("reload_identity_unverified:"));
            Assert.That(restored.reloadRevalidation.status, Is.EqualTo("unverified"));
            Assert.That(restored.reloadRevalidation.endedAtUtcMs, Is.GreaterThan(0));
            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(HasObserver(manager), Is.False);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_launcherProcess"), Is.Null);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_trackedProcess"), Is.Null);
            Assert.That(manager.PeriodicHealthChecksAllowed, Is.False);
            if (previousStatus == "succeeded")
            {
                Assert.That(restored.reloadRevalidation.observationId, Is.Not.EqualTo(previous.observationId));
                Assert.That(restored.reloadRevalidation.attemptsUsed, Is.Zero);
                Assert.That(restored.reloadRevalidation.endReason, Is.EqualTo("reload_identity_unverified"));
            }
            else
            {
                Assert.That(restored.reloadRevalidation.observationId, Is.EqualTo(previous.observationId));
                Assert.That(restored.reloadRevalidation.deadlineUtcMs, Is.EqualTo(previous.deadlineUtcMs));
                Assert.That(restored.reloadRevalidation.attemptsUsed, Is.EqualTo(3));
                Assert.That(restored.reloadRevalidation.maxAttempts, Is.EqualTo(4));
                Assert.That(restored.reloadRevalidation.endReason, Is.EqualTo(previousStatus == "unverified"
                    ? "previous_failure" : "reload_identity_unverified"));
            }
            var persisted = JsonUtility.FromJson<ServerLaunchState>(SessionState.GetString(LaunchKey, ""));
            Assert.That(persisted.reloadRevalidation.status, Is.EqualTo("unverified"));
            Assert.That(persisted.reloadRevalidation.observationId, Is.EqualTo(restored.reloadRevalidation.observationId));
        }

        private UPilotMcpServerManager Manager()
        {
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            _managers.Add(manager);
            return manager;
        }
        private static bool HasObserver(UPilotMcpServerManager manager) =>
            (EditorApplication.update?.GetInvocationList() ?? Array.Empty<Delegate>())
                .Any(callback => ReferenceEquals(callback.Target, manager) && callback.Method.Name == "ObserveLaunch");
        private static T Field<T>(UPilotMcpServerManager manager, string name) =>
            (T)typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).GetValue(manager);
        private static void Set(UPilotMcpServerManager manager, string name, object value) =>
            typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).SetValue(manager, value);
        private static void Call(UPilotMcpServerManager manager, string name) =>
            typeof(UPilotMcpServerManager).GetMethod(name, PrivateInstance).Invoke(manager, null);
    }
}
