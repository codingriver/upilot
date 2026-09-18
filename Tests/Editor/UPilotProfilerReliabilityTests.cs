using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotProfilerReliabilityTests
    {
        private static object Call(string method, params object[] args) =>
            typeof(UPilotRuntimeDiagnosticsService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

        [Test]
        public void BoundedBufferAndCheapSummaryPreserveTerminalIdentity()
        {
            var start = (ProfilerCaptureResultPayload)Call("StartProfiler", new ProfilerCaptureStartPayload
            {
                maxSamples = 2, maxMarkers = 0, includeDefaultAiMarkers = false, title = "P1-profiler-bounded",
            });
            try
            {
                Call("SampleProfiler");
                Thread.Sleep(20);
                Call("SampleProfiler");
                Thread.Sleep(20);
                Call("SampleProfiler");
                var status = (ProfilerCaptureResultPayload)Call("GetProfilerStatus", start.captureId);
                Assert.That(status.sampleCount, Is.EqualTo(2));
                Assert.That(status.droppedSamples, Is.EqualTo(1));
                Assert.That(status.samples, Is.Empty);
                var stopped = (ProfilerCaptureResultPayload)Call("StopProfiler", start.captureId, "Stopped");
                var persisted = JsonUtility.FromJson<ProfilerCaptureResultPayload>(File.ReadAllText(stopped.jsonPath));
                Assert.That(persisted.samples, Has.Count.EqualTo(2));
                Assert.That(persisted.samples.All(sample => sample.navMeshAgents == -1), Is.True);
                Assert.That(stopped.summaries.Any(item => item.name == "navMeshAgents"), Is.False);
                Assert.That(stopped.suppressedCounters, Does.Contain("mainThreadMs"));
                Assert.That(stopped.summaries.Any(item => item.name == "renderThreadMs"), Is.False);
                Assert.That(stopped.summaries.Any(item => item.name == "mainThreadMs"), Is.False);
                Assert.That(stopped.comparableBaseline, Is.False);
                var second = (ProfilerCaptureResultPayload)Call("StopProfiler", start.captureId, "Completed");
                Assert.That(second.endedAt, Is.EqualTo(stopped.endedAt));
                Assert.That(second.status, Is.EqualTo("Stopped"));
            }
            finally { Call("StopProfiler", start.captureId, "Stopped"); }
        }

        public static string ExpensiveTelemetry()
        {
            Thread.Sleep(20);
            return new string('x', 20000);
        }

        [Test]
        public void HighCostSamplerCannotClaimComparableBaseline()
        {
            var start = (ProfilerCaptureResultPayload)Call("StartProfiler", new ProfilerCaptureStartPayload
            {
                maxSamples = 4, maxMarkers = 0, includeDefaultAiMarkers = false, title = "P1-profiler-overhead",
                telemetryTypeName = typeof(UPilotProfilerReliabilityTests).FullName,
                telemetryMethodName = nameof(ExpensiveTelemetry),
            });
            try
            {
                for (int index = 0; index < 4; index++) Call("SampleProfiler");
                var stopped = (ProfilerCaptureResultPayload)Call("StopProfiler", start.captureId, "Stopped");
                Assert.That(stopped.observerEffectDetected, Is.True);
                Assert.That(stopped.observerEffectVerification, Is.EqualTo("budgetExceeded"));
                Assert.That(stopped.comparableBaseline, Is.False);
                Assert.That(stopped.summaries.Single(item => item.name == "collectorMs").p95, Is.GreaterThan(15));
                if (stopped.allocationMeasurementAvailable)
                    Assert.That(stopped.summaries.Single(item => item.name == "collectorAllocatedBytes").p95, Is.GreaterThan(10240));
                else
                {
                    Assert.That(stopped.unavailableCounters, Does.Contain("collectorAllocatedBytes"));
                    Assert.That(stopped.summaries.Any(item => item.name == "collectorAllocatedBytes"), Is.False);
                    var persisted = JsonUtility.FromJson<ProfilerCaptureResultPayload>(File.ReadAllText(stopped.jsonPath));
                    Assert.That(persisted.samples.All(sample => sample.collectorAllocatedBytes == -1), Is.True);
                }
            }
            finally { Call("StopProfiler", start.captureId, "Stopped"); }
        }

        public class TelemetryWindow : EditorWindow { }

        [Test]
        public void AllocationMeasurementCountsKnownManagedAllocation()
        {
            Assert.That(UPilotAllocationMeasurement.Available, Is.True);
            var token = UPilotAllocationMeasurement.Begin();
            var probe = new byte[16384];
            long allocated = UPilotAllocationMeasurement.End(token);
            GC.KeepAlive(probe);
            Assert.That(UPilotAllocationMeasurement.Source,
                Is.EqualTo("gcThreadCounter").Or.EqualTo("monoAllocationCallback"));
            Assert.That(allocated, Is.GreaterThanOrEqualTo(16384));
        }

        [Test]
        public void ExplicitScopesSeparateTwoWindowInstances()
        {
            var first = ScriptableObject.CreateInstance<TelemetryWindow>();
            var second = ScriptableObject.CreateInstance<TelemetryWindow>();
            try
            {
                UPilotWindowTelemetry.BeginCapture();
                using (UPilotWindowTelemetry.OnGUI(first)) Thread.Sleep(2);
                using (UPilotWindowTelemetry.OnGUI(second)) Thread.Sleep(3);
                UPilotWindowTelemetry.RequestRepaint(first);
                var result = UPilotWindowTelemetry.EndCapture(out int dropped);
                Assert.That(dropped, Is.Zero);
                Assert.That(result, Has.Count.EqualTo(2));
                Assert.That(result.Select(item => item.instanceId).Distinct().Count(), Is.EqualTo(2));
                Assert.That(result.All(item => item.onGuiCalls == 1 && item.onGuiMs >= 1), Is.True);
                Assert.That(result.Single(item => item.instanceId == UPilotEntityIds.ToWireId(first).ToString()).repaintRequests, Is.EqualTo(1));
                Assert.That(result.All(item => item.frameSource == "editorOnGuiSequence"), Is.True);
            }
            finally
            {
                UPilotWindowTelemetry.EndCapture(out _);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void EditModeScopesKeepPerCallFrameIdentity()
        {
            var window = ScriptableObject.CreateInstance<TelemetryWindow>();
            try
            {
                UPilotWindowTelemetry.BeginCapture();
                using (UPilotWindowTelemetry.OnGUI(window)) { }
                UPilotWindowTelemetry.RequestRepaint(window);
                using (UPilotWindowTelemetry.OnGUI(window)) { }
                var result = UPilotWindowTelemetry.EndCapture(out int dropped);
                Assert.That(dropped, Is.Zero);
                Assert.That(result, Has.Count.EqualTo(2));
                Assert.That(result.Select(item => item.frame).Distinct().Count(), Is.EqualTo(2));
                Assert.That(result.Sum(item => item.onGuiCalls), Is.EqualTo(2));
                Assert.That(result.Sum(item => item.repaintRequests), Is.EqualTo(1));
                Assert.That(result.All(item => item.frameSource == "editorOnGuiSequence"), Is.True);
            }
            finally
            {
                UPilotWindowTelemetry.EndCapture(out _);
                UnityEngine.Object.DestroyImmediate(window);
            }
        }
    }
}
