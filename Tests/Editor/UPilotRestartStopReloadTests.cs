// SPDX-License-Identifier: MIT
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotRestartStopReloadTests
    {
        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo CurrentRecord = typeof(UPilotServerRestartDiagnostics)
            .GetField("s_record", BindingFlags.NonPublic | BindingFlags.Static);
        private object _savedRecord;
        private UPilotMcpServerManager _manager;
        private UPilotServerRestartRecord _restart;
        private ServiceStopObservationRecord _stop;

        [SetUp]
        public void SetUp()
        {
            _savedRecord = CurrentRecord.GetValue(null);
            _manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set("_statusLock", new object());
            // A new domain's counters need not equal the historical operation's generation.
            Set("_startAttemptGeneration", 7);
            Set("_restartGeneration", 17L);
            Set("_stopStartGeneration", 7);
            Set("_stopRestartGeneration", 17L);
            _restart = new UPilotServerRestartRecord {
                operationId = "restart-reload-" + Guid.NewGuid().ToString("N"),
                status = "running", projectPath = UPilotProjectConfig.ProjectRoot,
                restartGeneration = 101, newProcessId = 0,
            };
            _stop = ServiceStopObservationRecord.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 30, 100);
            _stop.project = UPilotProjectConfig.ProjectRoot;
            _stop.lifetime = UPilotServiceLifetime.Id;
            _stop.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _stop.requestId = _restart.operationId;
            _stop.originalRestartGeneration = _restart.restartGeneration;
            Set("_stopObservation", _stop);
            Set("_restoredRestartStop", _stop);
            CurrentRecord.SetValue(null, _restart);
        }

        [TearDown]
        public void TearDown()
        {
            typeof(UPilotMcpServerManager).GetMethod("ClearRestoredRestartStopObservation", Fields)
                .Invoke(_manager, null);
            CurrentRecord.SetValue(null, _savedRecord);
        }

        [Test]
        public void ActiveRestoredStopKeepsItsOriginalObserverAndDoesNotEndOrReplayRestart()
        {
            var failures = 0;
            Assert.That(_manager.CompleteRestoredRestartStop((id, code, message) => failures++), Is.False);
            Assert.That(failures, Is.Zero);
            Assert.That(_restart.status, Is.EqualTo("running"));
            Assert.That(Get("_restoredRestartStop"), Is.SameAs(_stop));
            AssertNoStopFenceOrLaunchMutation();
        }

        [TestCase("succeeded")]
        [TestCase("failed")]
        [TestCase("timed_out")]
        public void OriginalStopTerminalEndsAssociatedRestartOnceWithoutChangingStopFence(string terminal)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _stop.End(now, terminal, "original-stop-result");
            var originalEnd = _stop.endedAt;
            var originalDeadline = _stop.deadline;
            var failures = 0;
            LogAssert.Expect(LogType.Error, new Regex("Domain Reload.*restart_recovery_required", RegexOptions.Singleline));

            Assert.That(_manager.CompleteRestoredRestartStop((id, code, message) => {
                failures++;
                Assert.That(id, Is.EqualTo(_restart.operationId));
                Assert.That(code, Is.EqualTo("restart_recovery_required"));
                Assert.That(message, Does.Contain("stopId=" + _stop.id));
                _restart.status = "failed";
            }), Is.True);

            Assert.That(_manager.CompleteRestoredRestartStop((id, code, message) => failures++), Is.False);
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(_restart.status, Is.EqualTo("failed"));
            Assert.That(_stop.status, Is.EqualTo(terminal));
            Assert.That(_stop.endedAt, Is.EqualTo(originalEnd));
            Assert.That(_stop.deadline, Is.EqualTo(originalDeadline));
            Assert.That(Get("_restoredRestartStop"), Is.Null);
            AssertNoStopFenceOrLaunchMutation();
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OriginalStoppedStopCancelsAssociatedRestartOnceWithoutErrorOrChangingStopFence()
        {
            _stop.End(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "stopped", "original-normal-cancellation");
            var originalEnd = _stop.endedAt;
            var originalDeadline = _stop.deadline;
            var failures = 0;
            var cancellations = 0;
            LogAssert.Expect(LogType.Log, new Regex("Domain Reload.*stopStatus=stopped.*restart_canceled", RegexOptions.Singleline));

            Assert.That(_manager.CompleteRestoredRestartStop(
                (id, code, message) => failures++,
                (id, message) => {
                    cancellations++;
                    Assert.That(id, Is.EqualTo(_restart.operationId));
                    Assert.That(message, Does.Contain("stopId=" + _stop.id));
                    Assert.That(message, Does.Contain("original-normal-cancellation"));
                    Assert.That(message, Does.Not.Contain("restart_recovery_required"));
                    _restart.status = "canceled";
                }), Is.True);

            Assert.That(_manager.CompleteRestoredRestartStop(
                (id, code, message) => failures++, (id, message) => cancellations++), Is.False);
            Assert.That(failures, Is.Zero);
            Assert.That(cancellations, Is.EqualTo(1));
            Assert.That(_restart.status, Is.EqualTo("canceled"));
            Assert.That(_stop.status, Is.EqualTo("stopped"));
            Assert.That(_stop.reason, Is.EqualTo("original-normal-cancellation"));
            Assert.That(_stop.endedAt, Is.EqualTo(originalEnd));
            Assert.That(_stop.deadline, Is.EqualTo(originalDeadline));
            Assert.That(Get("_restoredRestartStop"), Is.Null);
            AssertNoStopFenceOrLaunchMutation();
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("operation")]
        [TestCase("generation")]
        [TestCase("project")]
        [TestCase("lifetime")]
        [TestCase("unity_pid")]
        [TestCase("replacement_started")]
        [TestCase("restart_ended")]
        [TestCase("successor_stop")]
        public void LateRestoredStopCannotEndAnotherOperationOrAnEstablishedReplacement(string mismatch)
        {
            _stop.End(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "succeeded", "old-stop-result");
            if (mismatch == "operation") _stop.requestId += "-other";
            if (mismatch == "generation") _stop.originalRestartGeneration++;
            if (mismatch == "project") _restart.projectPath += "-other";
            if (mismatch == "lifetime") _stop.lifetime += "-other";
            if (mismatch == "unity_pid") _stop.unityPid++;
            if (mismatch == "replacement_started") _restart.newProcessId = 42;
            if (mismatch == "restart_ended") _restart.status = "failed";
            if (mismatch == "successor_stop")
                Set("_stopObservation", ServiceStopObservationRecord.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 30, 100));
            var failures = 0;

            Assert.That(_manager.CompleteRestoredRestartStop((id, code, message) => failures++), Is.False);

            Assert.That(failures, Is.Zero);
            Assert.That(Get("_restoredRestartStop"), Is.Null);
            AssertNoStopFenceOrLaunchMutation();
            LogAssert.NoUnexpectedReceived();
        }

        private void AssertNoStopFenceOrLaunchMutation()
        {
            Assert.That(Get("_restartGeneration"), Is.EqualTo(17L));
            Assert.That(Get("_startAttemptGeneration"), Is.EqualTo(7));
            Assert.That(Get("_stopRestartGeneration"), Is.EqualTo(17L));
            Assert.That(Get("_stopStartGeneration"), Is.EqualTo(7));
            Assert.That(Get("_restartWaitCallback"), Is.Null);
            Assert.That(Get("_pendingLaunchSpec"), Is.Null);
            Assert.That(Get("_startInProgress"), Is.False);
            Assert.That(Get("_restartPending"), Is.False);
            Assert.That(Get("_launchState"), Is.Null);
        }

        private void Set(string name, object value) => typeof(UPilotMcpServerManager).GetField(name, Fields).SetValue(_manager, value);
        private object Get(string name) => typeof(UPilotMcpServerManager).GetField(name, Fields).GetValue(_manager);
    }
}
