// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotServiceHealthObservationTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static UPilotMcpServerManager Manager(int intervalSeconds = 600)
        {
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            Set(manager, "_healthCheckIntervalSeconds", intervalSeconds);
            Set(manager, "_launchState", new ServerLaunchState { verified = true, readyVerified = true });
            Set(manager, "_cachedStatus", WaitingStatus());
            return manager;
        }

        private static McpServerStatus WaitingStatus() => new McpServerStatus
        {
            IsRunning = true, ProcessId = 42, HealthServerProcessId = 42,
            HealthEndpointResponded = true, StatusQueryCompleted = true,
            Health = new UPilotServerHealth { server_pid = 42, server_instance_id = "health-fixture" },
            EditorObservationAvailable = true, EditorObservationStatus = "waiting_editor",
            EditorObservationSessionId = "fixture-session", EditorObservationProducerEpoch = "fixture-epoch",
            EditorObservationDomainGeneration = 7,
        };

        private static void Set(UPilotMcpServerManager manager, string name, object value) =>
            typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).SetValue(manager, value);

        private static T Get<T>(UPilotMcpServerManager manager, string name) =>
            (T)typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).GetValue(manager);

        private static void CacheVerified(UPilotMcpServerManager manager) =>
            typeof(UPilotMcpServerManager).GetMethod("CacheVerifiedStartupStatus", PrivateInstance)
                .Invoke(manager, new object[] { WaitingStatus() });

        private static async Task Close(UPilotMcpServerManager manager)
        {
            var loop = Get<Task>(manager, "_editorHealthObservationLoopTask");
            var probe = Get<Task>(manager, "_editorHealthObservationTask");
            manager.PausePeriodicHealthObservation();
            foreach (var work in new[] { loop, probe }.Where(task => task != null))
            {
                Assert.That(await Task.WhenAny(work, Task.Delay(2000)), Is.SameAs(work), "An isolated health observer must end before its test returns.");
                await work;
            }
        }

        private static async Task Until(Func<bool> predicate, string message, int timeoutMs = 3000)
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + timeoutMs;
            while (!predicate() && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
                await Task.Delay(10);
            Assert.That(predicate(), Is.True, message);
        }

        [Test]
        public void CachedStatusReadsNeverStartStatusHealthOrRecoveryWork()
        {
            var manager = Manager(1);
            Set(manager, "_editorHealthObservationEnabled", true);
            Set(manager, "_nextEditorHealthObservationAtUtcMs", 1L);
            Set(manager, "_statusGeneration", 2);
            for (var i = 0; i < 20; i++) Assert.That(manager.GetStatus().ProcessId, Is.EqualTo(42));
            Assert.That(Get<Task>(manager, "_statusRefreshTask"), Is.Null);
            Assert.That(Get<Task>(manager, "_editorHealthObservationTask"), Is.Null);
            Assert.That(Get<Task>(manager, "_editorHealthObservationLoopTask"), Is.Null);
            Assert.That(Get<bool>(manager, "_recoveryObservationRunning"), Is.False);
            Assert.That(Get<long>(manager, "_lastRefreshMs"), Is.Zero);
            Assert.That(Get<long>(manager, "_nextEditorHealthObservationAtUtcMs"), Is.EqualTo(1));
        }

        [Test]
        public async Task VerifiedServiceOwnsOneHealthObserverWithoutAnyWindow()
        {
            var manager = Manager();
            try
            {
                CacheVerified(manager);
                var loop = Get<Task>(manager, "_editorHealthObservationLoopTask");
                Assert.That(loop, Is.Not.Null);
                manager.ResumePeriodicHealthObservation();
                manager.ResumePeriodicHealthObservation();
                Assert.That(Get<Task>(manager, "_editorHealthObservationLoopTask"), Is.SameAs(loop));
                Assert.That(Get<long>(manager, "_nextEditorHealthObservationAtUtcMs"),
                    Is.EqualTo(Get<long>(manager, "_lastRefreshMs") + 600000));
                Assert.That(Get<Task>(manager, "_editorHealthObservationTask"), Is.Null);
            }
            finally { await Close(manager); }
        }

        [Test]
        public async Task LifecyclePauseRequiresFreshVerificationBeforeHealthResumes()
        {
            var manager = Manager();
            try
            {
                CacheVerified(manager);
                var previous = Get<Task>(manager, "_editorHealthObservationLoopTask");
                manager.InvalidateStatusCache("domain_reload");
                Assert.That(await Task.WhenAny(previous, Task.Delay(2000)), Is.SameAs(previous));
                await previous;
                manager.ResumePeriodicHealthObservation();
                Assert.That(Get<Task>(manager, "_editorHealthObservationLoopTask"), Is.Null);
                CacheVerified(manager);
                Assert.That(Get<Task>(manager, "_editorHealthObservationLoopTask"), Is.Not.Null.And.Not.SameAs(previous));
            }
            finally { await Close(manager); }
        }

        [Test]
        public async Task TransientHealthFailureRetriesAtTheNextCompletedInterval()
        {
            var manager = Manager(1);
            var handler = new HealthHandler();
            using var client = new HttpClient(handler);
            Set(manager, "_lastRefreshMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000);
            var priorRefresh = Get<long>(manager, "_lastRefreshMs");
            try
            {
                manager.ResumePeriodicHealthObservation(client);
                await Until(() => manager.GetStatus().FailureKind == McpStatusFailureKind.Transient &&
                    Get<long>(manager, "_lastRefreshMs") > priorRefresh,
                    "The first HTTP 503 must remain a transient observation failure.");
                var completed = Get<long>(manager, "_lastRefreshMs");
                Assert.That(Get<long>(manager, "_nextEditorHealthObservationAtUtcMs"), Is.EqualTo(completed + 1000));
                Assert.That(manager.GetStatus().HealthServerProcessId, Is.EqualTo(42));
                await Task.Delay(150);
                Assert.That(handler.Calls, Is.EqualTo(1), "No rapid retry may follow an HTTP failure.");
                await Until(() => manager.GetStatus().HealthEndpointResponded && handler.Calls >= 2,
                    "The existing service observer must retry without a window or another start.");
                Assert.That(handler.SecondRequestAtUtcMs, Is.GreaterThanOrEqualTo(completed + 1000));
                Assert.That(Get<Task>(manager, "_statusRefreshTask"), Is.Null);
            }
            finally { await Close(manager); }
        }

        [Test]
        public async Task PausingServiceCancelsAnInFlightHealthRequestAndItsObserver()
        {
            var manager = Manager(1);
            var handler = new HealthHandler { Stall = true };
            using var client = new HttpClient(handler);
            Set(manager, "_lastRefreshMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000);
            try
            {
                manager.ResumePeriodicHealthObservation(client);
                await Until(() => handler.Calls == 1, "The fake owned observation did not start.");
                await Close(manager);
                Assert.That(handler.Canceled, Is.True);
                Assert.That(Get<bool>(manager, "_editorHealthObservationEnabled"), Is.False);
                Assert.That(Get<Task>(manager, "_editorHealthObservationLoopTask"), Is.Null);
            }
            finally { await Close(manager); }
        }

        [Test]
        public void QuickStartDoesNotRegisterAnIdleEditorUpdateObserver()
        {
            var ignored = UPilotQuickStart.IsExplicitlyStopped;
            Assert.That(EditorApplication.update?.GetInvocationList().Count(callback =>
                callback.Method.DeclaringType == typeof(UPilotQuickStart) && callback.Method.Name == "ObserveDeployment") ?? 0,
                Is.Zero);
        }

        [Test]
        public void ReloadProcessSubqueriesConsumeOneDeadlineWhileOrdinaryQueriesKeepTheirCaps()
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000;
            var ports = UPilotMcpServerManager.ProcessProbeWaitMs(deadline, 2000);
            var command = UPilotMcpServerManager.ProcessProbeWaitMs(deadline, 1000);
            Assert.That(ports, Is.InRange(29000, 30000));
            Assert.That(command, Is.InRange(29000, ports));
            Assert.That(UPilotMcpServerManager.ProcessProbeWaitMs(1, 1000), Is.Zero);
            Assert.That(UPilotMcpServerManager.ProcessProbeWaitMs(0, 2000), Is.EqualTo(2000));
            Assert.That(UPilotMcpServerManager.ProcessProbeWaitMs(0, 1000), Is.EqualTo(1000));
            Assert.That(UPilotMcpServerManager.ProcessProbeWaitMs(0, 100), Is.EqualTo(100));
        }

        private sealed class HealthHandler : HttpMessageHandler
        {
            internal int Calls;
            internal bool Stall;
            internal bool Canceled;
            internal long SecondRequestAtUtcMs;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/health"));
                var count = Interlocked.Increment(ref Calls);
                if (Stall)
                {
                    var pending = new TaskCompletionSource<HttpResponseMessage>();
                    token.Register(() => { Canceled = true; pending.TrySetCanceled(); });
                    return pending.Task;
                }
                if (count == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                SecondRequestAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"server_pid\":42,\"server_instance_id\":\"health-fixture\",\"editor_observation\":{\"status\":\"waiting_editor\",\"session_id\":\"fixture-session\",\"producer_epoch\":\"fixture-epoch\",\"domain_generation\":7}}")
                });
            }
        }
    }
}
