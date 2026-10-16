// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using StopManager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotResidualServerStopTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "UPilot Residual Stop Tests");
        private static readonly string Project = Path.Combine(Root, "project");
        private static readonly string Python = Path.Combine(Root, "venv", "python.exe");
        private static readonly string Script = Path.Combine(Root, "server", "run_upilot_mcp.py");
        private static readonly string ServerExe = Path.Combine(Root, "server", "upilot-mcp-server.exe");
        private const int HttpPort = 18011, WsPort = 18765;
        private const string StopSessionKey = "UPilot.ServiceStopObservation.v1";
        private const string LaunchSessionKey = "UPilot.ServerLaunchIdentity.v1";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static StopManager.ServerStopScope Scope() => new(Project, Script, ServerExe, HttpPort, WsPort);
        private static string Quote(string value) => "\"" + value + "\"";
        private static string PythonCommand(string arguments) => Quote(Python) + " -I -B " + Quote(Script) + " " + arguments;

        [TestCase("--http-port 18011 --port 18765")]
        [TestCase("--port 18765 --http-port 18011")]
        [TestCase("--http-port=18011 --port=18765")]
        [TestCase("--http-port=18011 --port 18765 --transport http")]
        public void PythonEntryAndUniqueCompletePortPairDoNotRequireSavedProcessIdentityOrHealth(string arguments)
        {
            // No PID, creation time, parent, SessionState, health response or log path is available here.
            Assert.That(Scope().Matches(PythonCommand(arguments), Python), Is.True);
        }

        [TestCase("log/mcp-server.log")]
        [TestCase("Logs/UPilot/mcp-server.log")]
        [TestCase("another-project/other.log")]
        public void LogDirectoryChangesCannotBlockAnOtherwiseMatchingResidual(string logRelativePath)
        {
            var command = PythonCommand("--http-port 18011 --port 18765 --log-file " +
                                        Quote(Path.Combine(Project, logRelativePath)));
            Assert.That(Scope().Matches(command, Python), Is.True);
        }

        [TestCase("--http-port 18011")]
        [TestCase("--port 18765")]
        [TestCase("--http-port 18012 --port 18765")]
        [TestCase("--http-port 18011 --port 18766")]
        [TestCase("--http-port 18011 --port 18765 --http-port 18011")]
        [TestCase("--http-port=18011 --port=18765 --http-port=18012")]
        [TestCase("--http-port 18011 --port 18765 --port 18765")]
        [TestCase("--http-port 18011 --port 18765 --port")]
        [TestCase("--http-port=18011=18012 --port 18765")]
        [TestCase("--http-port 18011x --port 18765")]
        [TestCase("--http-port 0 --port 18765")]
        [TestCase("\"--http-port 18011\" --port 18765")]
        public void MissingMismatchedDuplicateAndAmbiguousPortArgumentsNeverAuthorizeStopping(string arguments)
        {
            Assert.That(Scope().Matches(PythonCommand(arguments), Python), Is.False);
        }

        [TestCase("relative")]
        [TestCase("other_directory")]
        [TestCase("other_script_first")]
        [TestCase("command_string")]
        [TestCase("module")]
        [TestCase("log_value_only")]
        [TestCase("script_suffix")]
        public void PythonMustActuallyExecuteTheExactAbsoluteConfiguredScript(string scenario)
        {
            var other = Path.Combine(Root, "other", "run_upilot_mcp.py");
            var prefix = Quote(Python) + " ";
            string command;
            switch (scenario)
            {
                case "relative": command = prefix + "run_upilot_mcp.py"; break;
                case "other_directory": command = prefix + Quote(other); break;
                case "other_script_first": command = prefix + Quote(other) + " " + Quote(Script); break;
                case "command_string": command = prefix + "-c " + Quote(Script); break;
                case "module": command = prefix + "-m upilot_mcp " + Quote(Script); break;
                case "log_value_only": command = prefix + Quote(other) + " --log-file " + Quote(Script); break;
                default: command = prefix + Quote(Script + ".other"); break;
            }
            Assert.That(Scope().Matches(command + " --http-port 18011 --port 18765", Python), Is.False);
        }

        [TestCase("")]
        [TestCase(" -u")]
        [TestCase(" -W ignore")]
        [TestCase(" -X utf8")]
        public void InterpreterOptionsBeforeTheActualScriptRemainSupported(string options)
        {
            var command = Quote(Python) + options + " " + Quote(Script) + " --http-port 18011 --port 18765";
            Assert.That(Scope().Matches(command, Python), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExeIdentityUsesTheActualImageAndCannotBeSpoofedByCommandText(bool fakeImage)
        {
            var command = Quote(ServerExe) + " --http-port 18011 --port 18765";
            var actual = fakeImage ? Path.Combine(Root, "other", "upilot-mcp-server.exe") : ServerExe;
            Assert.That(Scope().Matches(command, actual), Is.EqualTo(!fakeImage));
        }

        [Test]
        public void SameExecutableNameAndDirectorySubstringAreInsufficient()
        {
            var other = Path.Combine(Root, "server-backup", "upilot-mcp-server.exe");
            Assert.That(Scope().Matches(Quote(other) + " --http-port 18011 --port 18765", other), Is.False);
        }

        [TestCase(false, false, false, true)]
        [TestCase(false, false, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, true, false)]
        public void OnlyANewEditorWithoutSameSessionStateStartsIndependentRecovery(bool initialized, bool hasLaunch, bool hasStop, bool expected)
        {
            Assert.That(StopManager.IsNewEditorRecoverySession(initialized, hasLaunch, hasStop), Is.EqualTo(expected));
        }

        [TestCase("valid", true)]
        [TestCase("not_alive", false)]
        [TestCase("missing_http_owner", false)]
        [TestCase("missing_ws_owner", false)]
        [TestCase("missing_health", false)]
        [TestCase("not_upilot", false)]
        [TestCase("different_pid", false)]
        [TestCase("missing_instance", false)]
        [TestCase("missing_project", false)]
        [TestCase("other_project", false)]
        public void ReuseRequiresCurrentLiveEndpointEvidenceWithoutOriginalLauncherOrCreationInformation(string scenario, bool expected)
        {
            const int pid = 123;
            var status = new McpServerStatus
            {
                HealthEndpointResponded = true, HealthIdentifiesUPilot = true,
                HealthServerProcessId = pid, HealthProjectPath = Project,
                Health = new UPilotServerHealth { server_instance_id = "current-live-instance" },
            };
            if (scenario == "missing_health") status.HealthEndpointResponded = false;
            if (scenario == "not_upilot") status.HealthIdentifiesUPilot = false;
            if (scenario == "different_pid") status.HealthServerProcessId++;
            if (scenario == "missing_instance") status.Health.server_instance_id = "";
            if (scenario == "missing_project") status.HealthProjectPath = "";
            if (scenario == "other_project") status.HealthProjectPath = Path.Combine(Root, "other-project");
            Assert.That(StopManager.CanReuseExistingService(status, Project, pid, scenario != "not_alive",
                scenario != "missing_http_owner", scenario != "missing_ws_owner"), Is.EqualTo(expected));
            Assert.That(StopManager.ExistingServiceReuseBudgetMs, Is.EqualTo(8000));
        }

        [TestCase("unconfirmed")]
        [TestCase("ports_unavailable")]
        [TestCase("observation_failed")]
        [TestCase("stale_operation")]
        [TestCase("already_started")]
        [TestCase("expired")]
        public void ReplacementGuardsRejectUnverifiedStaleDuplicateOrExpiredStartsBeforeAnyProcessLaunch(string scenario)
        {
            var manager = IsolatedManager(null);
            var record = new StopManager.ExistingServiceRecoveryRecord
            {
                id = "isolated-recovery", projectPath = Project, status = "cleaning",
                deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
            };
            if (scenario == "already_started") record.replacementStarted = true;
            if (scenario == "expired") record.deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            var original = JsonUtility.ToJson(record);
            var stopped = new StopManager.ServerStopResult(scenario != "unconfirmed", scenario != "ports_unavailable",
                0, scenario == "stale_operation" ? 1 : 0, 0, scenario != "observation_failed");
            var method = typeof(StopManager).GetMethod("StartExistingServiceReplacement", PrivateInstance);
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(manager, new object[] { record, stopped }));
            Assert.That(error.InnerException, scenario == "expired" ? Is.InstanceOf<TimeoutException>() : Is.InstanceOf<InvalidOperationException>());
            Assert.That(JsonUtility.ToJson(record), Is.EqualTo(original));
            Assert.That(Get<object>(manager, "_pendingLaunchSpec"), Is.Null);
            Assert.That(Get<long>(manager, "_pendingLaunchDeadline"), Is.Zero);
        }

        private static StopManager IsolatedManager(ServiceStopObservationRecord record)
        {
            var manager = (StopManager)FormatterServices.GetUninitializedObject(typeof(StopManager));
            Set(manager, "_statusLock", new object());
            Set(manager, "_stoppingProcesses", new List<Process>());
            Set(manager, "_restartOperationId", "");
            Set(manager, "_initialServiceRecoveryCompleted", true);
            Set(manager, "_stopObservation", record);
            Set(manager, "_stopCompletion", new TaskCompletionSource<StopManager.ServerStopResult>());
            if (record != null)
                Set(manager, "_stopScope", new StopManager.ServerStopScope(record.project, Script, ServerExe, record.httpPort, record.wsPort));
            return manager;
        }

        private static T Get<T>(StopManager manager, string name) => (T)typeof(StopManager).GetField(name, PrivateInstance).GetValue(manager);
        private static void Set(StopManager manager, string name, object value) => typeof(StopManager).GetField(name, PrivateInstance).SetValue(manager, value);
        private static object Call(StopManager manager, string name) => typeof(StopManager).GetMethod(name, PrivateInstance).Invoke(manager, null);

#if UNITY_EDITOR_WIN
        [Test]
        public void HeldHandleTerminatesAndObservesOnlyTheOwnedTestProcess()
        {
            using var child = OwnedProcess.StartSleeping();
            using var target = StopManager.ServerStopTarget.Open(child.Process.Id);
            Assert.That(target, Is.Not.Null);
            Assert.That(target.ProcessId, Is.EqualTo(child.Process.Id));
            Assert.That(target.HandleValue, Is.Not.Zero);
            Assert.That(target.HasExited, Is.False);
            target.Terminate();
            Assert.That(SpinWait.SpinUntil(() => target.HasExited, 5000), Is.True);
            Assert.That(child.Process.WaitForExit(5000), Is.True);
            target.Terminate();
            Assert.That(target.HasExited, Is.True, "A duplicate terminal observation must not open another process by PID.");
        }

        [Test]
        public void DetachedHandleSurvivesOldWrapperDisposalAndRestoresTheSameOwnedProcess()
        {
            using var child = OwnedProcess.StartSleeping();
            var original = StopManager.ServerStopTarget.Open(child.Process.Id);
            Assert.That(original, Is.Not.Null);
            var handle = original.DetachHandle();
            Assert.That(handle, Is.Not.Zero);
            Assert.That(original.HandleValue, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => original.Terminate());
            original.Dispose();
            using var restored = StopManager.ServerStopTarget.RestoreHandle(handle, child.Process.Id);
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.HandleValue, Is.EqualTo(handle));
            Assert.That(restored.ProcessId, Is.EqualTo(child.Process.Id));
            Assert.That(restored.HasExited, Is.False);
            restored.Terminate();
            Assert.That(SpinWait.SpinUntil(() => restored.HasExited, 5000), Is.True);
            Assert.That(child.Process.WaitForExit(5000), Is.True);
        }

        [Test]
        public void CurrentEditorCannotBeOpenedAsAStopTarget()
        {
            Assert.Throws<InvalidOperationException>(() => StopManager.ServerStopTarget.Open(Process.GetCurrentProcess().Id));
        }

        [Test]
        public void LiveDiscoveryWithoutSavedPidFindsAndStopsSeparateMatchingHttpAndWsListeners()
        {
            var http = FreePort();
            int ws;
            do { ws = FreePort(); } while (ws == http);
            using var httpChild = OwnedProcess.StartListener(http, ws, http);
            using var wsChild = OwnedProcess.StartListener(http, ws, ws);
            var scope = new StopManager.ServerStopScope(UPilotProjectConfig.ProjectRoot, "", OwnedProcess.Executable, http, ws);
            using var discovery = StopManager.DiscoverStopTargets(scope, Array.Empty<int>(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000);

            Assert.That(discovery.Targets.ConvertAll(target => target.ProcessId),
                Is.EquivalentTo(new[] { httpChild.Process.Id, wsChild.Process.Id }));
            Assert.That(discovery.UnmatchedPortOwners, Is.Empty);
            Assert.That(discovery.ListeningPorts[httpChild.Process.Id], Does.Contain(http));
            Assert.That(discovery.ListeningPorts[wsChild.Process.Id], Does.Contain(ws));
            foreach (var target in discovery.Targets)
            {
                Assert.That(target.EntryPath, Is.EqualTo(scope.ServerExe));
                target.Terminate();
            }
            Assert.That(httpChild.Process.WaitForExit(5000), Is.True);
            Assert.That(wsChild.Process.WaitForExit(5000), Is.True);
            Assert.That(SpinWait.SpinUntil(() => UPilotPortAllocator.IsPortAvailable(http) &&
                UPilotPortAllocator.IsPortAvailable(ws), 5000), Is.True);
        }

        [Test]
        public void ExplicitlyForeignListenerRemainsAliveEvenWhenEntryAndPortsMatch()
        {
            var http = FreePort();
            int ws;
            do { ws = FreePort(); } while (ws == http);
            using var child = OwnedProcess.StartListener(http, ws, http);
            var scope = new StopManager.ServerStopScope(UPilotProjectConfig.ProjectRoot, "", OwnedProcess.Executable, http, ws);
            using var discovery = StopManager.DiscoverStopTargets(scope, Array.Empty<int>(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000, excludedForeignProjectPid: child.Process.Id);
            Assert.That(discovery.Targets, Is.Empty);
            Assert.That(discovery.ExcludedProcessIds, Is.EqualTo(new[] { child.Process.Id }));
            Assert.That(discovery.UnmatchedPortOwners, Is.EqualTo(new[] { child.Process.Id }));
            Assert.That(child.Process.HasExited, Is.False);
        }

        [Test]
        public void ResidualScanningContinuesWhileOriginalTargetIsAliveWithoutReplayingItsDispatchOrRenewingDeadline()
        {
            using var originalChild = OwnedProcess.StartSleeping();
            using var residualChild = OwnedProcess.StartSleeping();
            var originalTarget = StopManager.ServerStopTarget.Open(originalChild.Process.Id);
            var record = StopRecord();
            record.dispatched = true;
            record.processIds = new[] { originalChild.Process.Id };
            var manager = IsolatedManager(record);
            Set(manager, "_stoppingTargets", new List<StopManager.ServerStopTarget> { originalTarget });
            var savedStop = SessionState.GetString(StopSessionKey, "");
            var savedLaunch = SessionState.GetString(LaunchSessionKey, "");
            var calls = 0;
            var observedDeadlines = new List<long>();
            var originalDeadline = record.deadline;
            manager.StopDiscoveryOverride = (scope, knownPids, deadline) =>
            {
                Assert.That(knownPids, Does.Contain(originalChild.Process.Id));
                observedDeadlines.Add(deadline);
                var result = EmptyDiscovery();
                result.Targets.Add(StopManager.ServerStopTarget.Open(originalChild.Process.Id));
                if (Interlocked.Increment(ref calls) == 1)
                    result.Targets.Add(StopManager.ServerStopTarget.Open(residualChild.Process.Id));
                return result;
            };
            try
            {
                LogAssert.Expect(LogType.Log, new Regex("正在关闭 MCP Server；目标 PID=\\[" + originalChild.Process.Id + ", " + residualChild.Process.Id));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：正在关闭服务器；PID=" + residualChild.Process.Id + "；operationId=" + record.id));
                LogAssert.Expect(LogType.Log, new Regex("已请求关闭 MCP Server 进程 PID=" + residualChild.Process.Id));
                CompleteOneDiscovery(manager);

                Assert.That(residualChild.Process.WaitForExit(5000), Is.True);
                Assert.That(originalChild.Process.HasExited, Is.False, "The already-dispatched original PID must only be observed.");
                Assert.That(record.Active, Is.True, "A still-live original target prevents successful completion.");
                CompleteOneDiscovery(manager);
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(originalChild.Process.HasExited, Is.False);
                Assert.That(record.processIds, Is.EquivalentTo(new[] { originalChild.Process.Id, residualChild.Process.Id }));
                Assert.That(record.deadline, Is.EqualTo(originalDeadline));
                Assert.That(observedDeadlines, Is.All.EqualTo(originalDeadline));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                SessionState.SetString(StopSessionKey, savedStop);
                SessionState.SetString(LaunchSessionKey, savedLaunch);
            }
        }

        [Test]
        public void PreparationCompletingAfterOriginalDeadlineCannotTerminateItsTarget()
        {
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            record.deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            var manager = IsolatedManager(record);
            var savedStop = SessionState.GetString(StopSessionKey, "");
            var completion = Get<TaskCompletionSource<StopManager.ServerStopResult>>(manager, "_stopCompletion");
            SetCompletedPreparation(manager, record, StopManager.ServerStopTarget.Open(child.Process.Id));
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=timed_out.*stop_observation_timeout"));
                Call(manager, "PollStopObservation");
                Assert.That(record.status, Is.EqualTo("timed_out"));
                Assert.That(record.dispatched, Is.False);
                Assert.That(child.Process.HasExited, Is.False);
                Assert.That(completion.Task.IsCompleted, Is.True);
                Assert.That(completion.Task.Result.StopConfirmed, Is.False);
                var generation = Get<int>(manager, "_statusGeneration");
                Call(manager, "PollStopObservation");
                Assert.That(Get<int>(manager, "_statusGeneration"), Is.EqualTo(generation));
                Assert.That(Get<Task>(manager, "_stopPreparation"), Is.Null);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                SessionState.SetString(StopSessionKey, savedStop);
            }
        }

        [Test]
        public void DomainReloadTransfersOriginalHandlesAndOnlyObservesPreviouslyDispatchedTargets()
        {
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            record.dispatched = true;
            record.processIds = new[] { child.Process.Id };
            record.originalStartGeneration = 17;
            record.originalRestartGeneration = 23;
            var before = IsolatedManager(record);
            Set(before, "_stoppingTargets", new List<StopManager.ServerStopTarget> { StopManager.ServerStopTarget.Open(child.Process.Id) });
            var after = IsolatedManager(null);
            var savedStop = SessionState.GetString(StopSessionKey, "");
            after.StopDiscoveryOverride = (scope, knownPids, deadline) =>
            {
                Assert.That(knownPids, Is.EquivalentTo(new[] { child.Process.Id }));
                Assert.That(deadline, Is.EqualTo(record.deadline));
                var result = EmptyDiscovery();
                result.Targets.Add(StopManager.ServerStopTarget.Open(child.Process.Id));
                return result;
            };
            try
            {
                Call(before, "BeforeStopDomainReload");
                Assert.That(record.handlesTransferred, Is.True);
                Assert.That(record.processHandles, Has.Length.EqualTo(1));
                Assert.That(Get<List<StopManager.ServerStopTarget>>(before, "_stoppingTargets"), Is.Empty);
                var transferred = record.processHandles[0];
                LogAssert.Expect(LogType.Log, new Regex("生命周期：正在关闭服务器；PID=0；operationId=" + record.id));
                Assert.That((bool)Call(after, "RestoreStopObservation"), Is.True);
                var restored = after.StopObservation;
                Assert.That(restored.id, Is.EqualTo(record.id));
                Assert.That(restored.deadline, Is.EqualTo(record.deadline));
                Assert.That(restored.originalStartGeneration, Is.EqualTo(17));
                Assert.That(restored.originalRestartGeneration, Is.EqualTo(23));
                Assert.That(restored.processHandles, Is.EqualTo(new[] { transferred }));
                Assert.That(restored.handlesTransferred, Is.False);
                CompleteOneDiscovery(after);
                Assert.That(child.Process.HasExited, Is.False, "Restoring a dispatched stop must not issue the old termination again.");
                Assert.That(after.StopObservation.Active, Is.True);
                Assert.That(Get<List<StopManager.ServerStopTarget>>(after, "_stoppingTargets"), Has.Count.EqualTo(1));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(after);
                // The original domain relinquished ownership; do not finish it and overwrite restored state.
                SessionState.SetString(StopSessionKey, savedStop);
            }
        }

        [Test]
        public void NonPendingWin32RejectionEndsStopOnceWithoutTerminatingOrRetryingTheOwnedTarget()
        {
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            var originalDeadline = record.deadline;
            var manager = IsolatedManager(record);
            var target = StopManager.ServerStopTarget.Open(child.Process.Id);
            SetCompletedPreparation(manager, record, target);
            var completion = Get<TaskCompletionSource<StopManager.ServerStopResult>>(manager, "_stopCompletion");
            var rejection = new Win32Exception(1314, "isolated-nonpending-termination-rejection");
            var attempts = 0;
            var discoveries = 0;
            var savedStop = SessionState.GetString(StopSessionKey, "");
            var savedLaunch = SessionState.GetString(LaunchSessionKey, "");
            manager.StopTerminationOverride = entry =>
            {
                Assert.That(entry.ProcessId, Is.EqualTo(child.Process.Id));
                Interlocked.Increment(ref attempts);
                throw rejection;
            };
            manager.StopDiscoveryOverride = (scope, knownPids, deadline) =>
            {
                Interlocked.Increment(ref discoveries);
                throw new InvalidOperationException("An ended stop must not discover or dispatch again.");
            };
            try
            {
                LogAssert.Expect(LogType.Log, new Regex("正在关闭 MCP Server；目标 PID=\\[" + child.Process.Id));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：正在关闭服务器；PID=" + child.Process.Id + "；operationId=" + record.id));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：生命周期操作结束；PID=" + child.Process.Id + "；operationId=" + record.id));
                // The established lifecycle diagnostic precedes the single terminal failure.
                LogAssert.Expect(LogType.Error, new Regex("生命周期失败.*stop_failed"));
                LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*stopId=" + record.id +
                    ".*状态=failed[\\s\\S]*System.ComponentModel.Win32Exception[\\s\\S]*isolated-nonpending-termination-rejection"));

                Call(manager, "StartStopObserver");

                Assert.That(completion.Task.IsCompleted, Is.True);
                Assert.That(completion.Task.Result.StopConfirmed, Is.False);
                Assert.That(completion.Task.Result.ObservationSucceeded, Is.False);
                Assert.That(record.status, Is.EqualTo("failed"));
                Assert.That(record.failureException, Is.SameAs(rejection));
                Assert.That(rejection.NativeErrorCode, Is.EqualTo(1314));
                Assert.That(rejection.StackTrace, Is.Not.Null.And.Not.Empty);
                Assert.That(record.pendingTerminationDiagnostic, Is.Empty);
                Assert.That(record.Active, Is.False);
                Assert.That(record.deadline, Is.EqualTo(originalDeadline));
                Assert.That(target.HandleValue, Is.Zero);
                Assert.That(child.Process.HasExited, Is.False);
                Assert.That(Get<object>(manager, "_stopObserver"), Is.Null);
                Assert.That(Get<Task>(manager, "_stopPreparation"), Is.Null);
                Assert.That(Get<List<StopManager.ServerStopTarget>>(manager, "_stoppingTargets"), Is.Empty);
                var endedAt = record.endedAt;
                var generation = Get<int>(manager, "_statusGeneration");

                Call(manager, "PollStopObservation");
                manager.ResetStopObservation("late-nonpending-rejection-cleanup");

                Assert.That(record.endedAt, Is.EqualTo(endedAt));
                Assert.That(Get<int>(manager, "_statusGeneration"), Is.EqualTo(generation));
                Assert.That(record.failureException, Is.SameAs(rejection));
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(discoveries, Is.Zero);
                Assert.That(child.Process.HasExited, Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                target.Dispose();
                SessionState.SetString(StopSessionKey, savedStop);
                SessionState.SetString(LaunchSessionKey, savedLaunch);
            }
        }

        [TestCase("exited")]
        [TestCase("timed_out")]
        [TestCase("discovery_failed")]
        public void RejectedTerminationIsDispatchedOnceAndRetainsItsOriginalEvidenceUntilTheStopEnds(string outcome)
        {
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            var originalDeadline = record.deadline;
            var manager = IsolatedManager(record);
            var target = StopManager.ServerStopTarget.Open(child.Process.Id);
            SetCompletedPreparation(manager, record, target);
            var completion = Get<TaskCompletionSource<StopManager.ServerStopResult>>(manager, "_stopCompletion");
            var pending = new Win32Exception(5, "isolated-pending-termination");
            var queryFailure = new InvalidOperationException("isolated-residual-query-failure");
            var attempts = 0;
            var failDiscovery = false;
            var savedStop = SessionState.GetString(StopSessionKey, "");
            var savedLaunch = SessionState.GetString(LaunchSessionKey, "");
            manager.StopTerminationOverride = entry =>
            {
                Assert.That(entry.ProcessId, Is.EqualTo(child.Process.Id));
                Interlocked.Increment(ref attempts);
                throw pending;
            };
            manager.StopDiscoveryOverride = (scope, knownPids, deadline) =>
            {
                Assert.That(knownPids, Does.Contain(child.Process.Id));
                Assert.That(deadline, Is.EqualTo(originalDeadline));
                if (failDiscovery) throw queryFailure;
                var result = EmptyDiscovery();
                if (!child.Process.HasExited) result.Targets.Add(StopManager.ServerStopTarget.Open(child.Process.Id));
                return result;
            };
            try
            {
                LogAssert.Expect(LogType.Log, new Regex("正在关闭 MCP Server；目标 PID=\\[" + child.Process.Id));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：正在关闭服务器；PID=" + child.Process.Id + "；operationId=" + record.id));
                LogAssert.Expect(LogType.Warning, new Regex("Server 终止调用被 OS 拒绝.*Win32=5；PID=" + child.Process.Id));
                Call(manager, "PollStopObservation");
                Assert.That(record.Active, Is.True);
                Assert.That(record.failureException, Is.SameAs(pending));
                Assert.That(record.pendingTerminationDiagnostic, Does.Contain(pending.ToString()).And.Contain(record.id));
                Assert.That(target.HandleValue, Is.Not.Zero, "Pending termination must retain the original OS handle.");
                Assert.That(completion.Task.IsCompleted, Is.False);

                CompleteOneDiscovery(manager);
                Assert.That(attempts, Is.EqualTo(1), "A still-live pending target must be observed without another Terminate call.");
                Assert.That(record.Active, Is.True);
                Assert.That(child.Process.HasExited, Is.False);
                Assert.That(completion.Task.IsCompleted, Is.False);
                LogAssert.Expect(LogType.Log, new Regex("生命周期：生命周期操作结束；PID=" + child.Process.Id + "；operationId=" + record.id));
                if (outcome == "exited")
                {
                    // Only the fixture's owned process exits here; the manager must observe it.
                    child.Process.Kill();
                    Assert.That(child.Process.WaitForExit(5000), Is.True);
                    LogAssert.Expect(LogType.Log, new Regex("MCP 服务停止成功.*状态=succeeded"));
                    CompleteOneDiscovery(manager);
                    Assert.That(completion.Task.Result.StopConfirmed, Is.True);
                    Assert.That(completion.Task.Result.PortsAvailable, Is.True);
                    Assert.That(completion.Task.Result.ObservationSucceeded, Is.True);
                    Assert.That(record.failureException, Is.SameAs(pending));
                }
                else if (outcome == "timed_out")
                {
                    LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=timed_out.*stop_observation_timeout[\\s\\S]*isolated-pending-termination"));
                    // Exercise the observer's real deadline branch with its injected clock;
                    // do not change or extend the originally accepted deadline or wait 30 seconds.
                    using var observer = new ServiceStopObserver(record, () => originalDeadline, () => true,
                        () => Assert.Fail("An expired observer must not query or dispatch again."),
                        (terminal, reason) => typeof(StopManager).GetMethod("FinishStopObservation", PrivateInstance)
                            .Invoke(manager, new object[] { terminal, reason }));
                    observer.Tick();
                    Assert.That(record.status, Is.EqualTo("timed_out"));
                    Assert.That(record.failureException, Is.SameAs(pending));
                    Assert.That(child.Process.HasExited, Is.False);
                    Assert.That(completion.Task.Result.StopConfirmed, Is.False);
                }
                else
                {
                    failDiscovery = true;
                    LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=failed.*isolated-residual-query-failure[\\s\\S]*isolated-pending-termination"));
                    CompleteOneDiscovery(manager);
                    Assert.That(record.status, Is.EqualTo("failed"));
                    Assert.That(record.failureException, Is.InstanceOf<AggregateException>());
                    var failures = ((AggregateException)record.failureException).Flatten().InnerExceptions;
                    CollectionAssert.Contains(failures, queryFailure);
                    CollectionAssert.Contains(failures, pending);
                    Assert.That(child.Process.HasExited, Is.False);
                    Assert.That(completion.Task.Result.StopConfirmed, Is.False);
                }
                Assert.That(record.deadline, Is.EqualTo(originalDeadline));
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(target.HandleValue, Is.Zero);
                Assert.That(record.pendingTerminationDiagnostic, Does.Contain(pending.ToString()));
                Assert.That(completion.Task.IsCompleted, Is.True);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                target.Dispose();
                SessionState.SetString(StopSessionKey, savedStop);
                SessionState.SetString(LaunchSessionKey, savedLaunch);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedHandleRecoveryStillReclaimsLaterValidTransfersWithoutTerminatingAnyTarget(bool mismatchedPid)
        {
            using var firstChild = OwnedProcess.StartSleeping();
            using var laterChild = OwnedProcess.StartSleeping();
            using var first = StopManager.ServerStopTarget.Open(firstChild.Process.Id);
            using var later = StopManager.ServerStopTarget.Open(laterChild.Process.Id);
            var record = StopRecord();
            record.dispatched = true;
            record.handlesTransferred = true;
            var manager = IsolatedManager(null);
            var savedStop = SessionState.GetString(StopSessionKey, "");
            long firstRaw = 0, laterRaw = 0;
            try
            {
                firstRaw = mismatchedPid ? first.DetachHandle() : 0;
                laterRaw = later.DetachHandle();
                record.heldProcessIds = new[] { mismatchedPid ? laterChild.Process.Id : firstChild.Process.Id, laterChild.Process.Id };
                record.processHandles = new[] { firstRaw, laterRaw };
                SessionState.SetString(StopSessionKey, JsonUtility.ToJson(record));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：正在关闭服务器；PID=0；operationId=" + record.id));
                LogAssert.Expect(LogType.Log, new Regex("生命周期：生命周期操作结束；PID=0；operationId=" + record.id));
                LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=failed.*部分 Server 停止句柄未能核实"));

                Assert.That((bool)Call(manager, "RestoreStopObservation"), Is.True);

                Assert.That(manager.StopObservation.status, Is.EqualTo("failed"));
                Assert.That(manager.StopObservation.failureException, Is.InstanceOf<AggregateException>());
                Assert.That(manager.StopObservation.handlesTransferred, Is.False);
                Assert.That(manager.StopObservation.processHandles, Is.Empty);
                Assert.That(Get<List<StopManager.ServerStopTarget>>(manager, "_stoppingTargets"), Is.Empty);
                Assert.That(NativeGetProcessId(new IntPtr(laterRaw)), Is.Zero,
                    "A bad earlier transfer must not leak a later valid process handle.");
                Assert.That(firstChild.Process.HasExited || laterChild.Process.HasExited, Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                // Only handles detached by this fixture may be reclaimed here.
                CloseOwnedTransferredHandle(firstRaw, firstChild.Process.Id);
                CloseOwnedTransferredHandle(laterRaw, laterChild.Process.Id);
                SessionState.SetString(StopSessionKey, savedStop);
            }
        }

        [Test]
        public void SavedEndpointPidIsOnlyADiscoveryHintAndNeedNotAppearInPreparedResidualTargets()
        {
            using var oldEndpoint = OwnedProcess.StartSleeping();
            using var residual = OwnedProcess.StartSleeping();
            var manager = IsolatedManager(null);
            Set(manager, "_pythonEntryPath", Script);
            var target = StopManager.ServerStopTarget.Open(residual.Process.Id);
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000;
            manager.StopDiscoveryOverride = (scope, knownPids, observedDeadline) =>
            {
                Assert.That(knownPids, Does.Contain(oldEndpoint.Process.Id));
                Assert.That(observedDeadline, Is.EqualTo(deadline));
                var result = EmptyDiscovery();
                result.Targets.Add(target);
                return result;
            };
            try
            {
                var method = typeof(StopManager).GetMethod("PrepareCurrentProjectStop", PrivateInstance);
                using var prepared = (StopManager.PreparedProcessStop)method.Invoke(manager,
                    new object[] { oldEndpoint.Process.Id, deadline });
                Assert.That(prepared.Processes.ConvertAll(entry => entry.ProcessId),
                    Is.EqualTo(new[] { residual.Process.Id }));
                Assert.That(oldEndpoint.Process.HasExited || residual.Process.HasExited, Is.False);
            }
            finally { target.Dispose(); }
            Assert.That(target.HandleValue, Is.Zero);
        }

        [TestCase("identity_changed")]
        [TestCase("terminal")]
        [TestCase("expired")]
        public void ResidualPollCannotTerminateAfterItsOriginalMaintenanceIsReplacedOrEnds(string scenario)
        {
            Assert.That(UPilotServiceMaintenance.IsActive, Is.False, "Do not replace an active maintenance journal.");
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            var manager = IsolatedManager(record);
            var target = StopManager.ServerStopTarget.Open(child.Process.Id);
            SetCompletedPreparation(manager, record, target);
            var journalField = typeof(UPilotServiceMaintenance).GetField("_journal", BindingFlags.NonPublic | BindingFlags.Static);
            var originalJournal = journalField.GetValue(null);
            var savedStop = SessionState.GetString(StopSessionKey, "");
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "upilot-residual-maintenance-" + Guid.NewGuid().ToString("N"));
            var journalPath = Path.Combine(temporaryDirectory, "journal.json");
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var journal = new ServiceMaintenanceJournal(journalPath, () => now);
                var maintenance = journal.Accept(new ServiceRestartRequest
                {
                    maintenanceId = Guid.NewGuid().ToString("D"), target = "server", reason = "isolated residual stop fence",
                    expectedProjectPath = UPilotProjectConfig.ProjectRoot, expectedBridgeSessionId = "isolated-fixture",
                    expectedServerProcessId = child.Process.Id,
                }, 120, Process.GetCurrentProcess().Id, Array.Empty<string>(), humanConfirmed: true);
                record.maintenanceId = scenario == "identity_changed" ? Guid.NewGuid().ToString("D") : maintenance.maintenanceId;
                if (scenario == "terminal") maintenance.Finish("failed", "fixture_finished", "fixture finished", now);
                if (scenario == "expired") maintenance.deadlineAtUtcMs = now - 1;
                journalField.SetValue(null, journal);
                LogAssert.Expect(LogType.Log, new Regex("正在关闭 MCP Server；目标 PID=\\[" + child.Process.Id));
                LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=failed"));

                Call(manager, "PollStopObservation");

                Assert.That(record.status, Is.EqualTo("failed"));
                Assert.That(record.failureException, Is.InstanceOf<ServiceMaintenanceException>());
                Assert.That(((ServiceMaintenanceException)record.failureException).Code,
                    Is.EqualTo(scenario == "identity_changed" ? "SERVICE_RESTART_IDENTITY_CHANGED" : "SERVICE_RESTART_TIMEOUT"));
                Assert.That(child.Process.HasExited, Is.False,
                    "A residual target with no expected endpoint PID still requires the original active maintenance.");
                Assert.That(target.HandleValue, Is.Zero);
                Assert.That(Get<TaskCompletionSource<StopManager.ServerStopResult>>(manager, "_stopCompletion").Task.Result.StopConfirmed, Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                journalField.SetValue(null, originalJournal);
                FinishIsolatedManager(manager);
                target.Dispose();
                SessionState.SetString(StopSessionKey, savedStop);
                File.Delete(journalPath);
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory);
            }
        }

        [Test]
        public void MergingAnActiveStopDisposesTheUnusedPreparedTargetsWithoutTerminatingThem()
        {
            using var child = OwnedProcess.StartSleeping();
            var record = StopRecord();
            var manager = IsolatedManager(record);
            using var prepared = PreparedStop(record, StopManager.ServerStopTarget.Open(child.Process.Id));
            var target = prepared.Processes[0];
            var originalCompletion = Get<TaskCompletionSource<StopManager.ServerStopResult>>(manager, "_stopCompletion");

            var task = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, scope: prepared.Scope, prepared: prepared);

            Assert.That(task, Is.SameAs(originalCompletion.Task));
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(prepared.Processes, Is.Empty);
            Assert.That(target.HandleValue, Is.Zero);
            Assert.That(child.Process.HasExited, Is.False);
            Assert.That(manager.StopObservation, Is.SameAs(record));
        }

        [Test]
        public void StopDeadlineFailureBeforePreparationTransferDisposesTargetsWithoutTerminatingThem()
        {
            Assert.That(UPilotServiceMaintenance.IsActive, Is.False, "Do not enter stop during real maintenance.");
            using var child = OwnedProcess.StartSleeping();
            var manager = IsolatedManager(null);
            using var prepared = PreparedStop(StopRecord(), StopManager.ServerStopTarget.Open(child.Process.Id));
            var target = prepared.Processes[0];
            var savedStop = SessionState.GetString(StopSessionKey, "");
            try
            {
                LogAssert.Expect(LogType.Log, new Regex("开始停止 MCP 服务；stopId="));
                LogAssert.Expect(LogType.Error, new Regex("MCP 服务停止观察结束.*状态=timed_out.*Stop deadline already elapsed"));
                var task = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, timeoutMs: 30000,
                    parentDeadlineUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1, scope: prepared.Scope, prepared: prepared);

                Assert.That(task.IsCompleted, Is.True);
                Assert.That(task.Result.StopConfirmed, Is.False);
                Assert.That(manager.StopObservation.status, Is.EqualTo("timed_out"));
                Assert.That(manager.StopObservation.failureException, Is.InstanceOf<TimeoutException>());
                Assert.That(prepared.Processes, Is.Empty);
                Assert.That(target.HandleValue, Is.Zero);
                Assert.That(child.Process.HasExited, Is.False);
                Assert.That(Get<Task>(manager, "_stopPreparation"), Is.Null);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FinishIsolatedManager(manager);
                SessionState.SetString(StopSessionKey, savedStop);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
        private static extern uint NativeGetProcessId(IntPtr handle);

        private static void CloseOwnedTransferredHandle(long raw, int processId)
        {
            if (raw != 0 && NativeGetProcessId(new IntPtr(raw)) == processId)
                StopManager.ServerStopTarget.RestoreHandle(raw, processId).Dispose();
        }

        private static StopManager.PreparedProcessStop PreparedStop(ServiceStopObservationRecord record,
            params StopManager.ServerStopTarget[] targets)
        {
            var prepared = new StopManager.PreparedProcessStop
            {
                Scope = new StopManager.ServerStopScope(record.project, record.pythonEntry, record.serverExe, record.httpPort, record.wsPort),
                HttpPort = record.httpPort, WsPort = record.wsPort, VerifiedPorts = new Dictionary<int, List<int>>(),
            };
            prepared.Processes.AddRange(targets);
            return prepared;
        }

        private static ServiceStopObservationRecord StopRecord()
        {
            var record = ServiceStopObservationRecord.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 30, 100);
            record.project = UPilotProjectConfig.ProjectRoot;
            record.lifetime = UPilotServiceLifetime.Id;
            record.unityPid = Process.GetCurrentProcess().Id;
            record.httpPort = FreePort();
            do { record.wsPort = FreePort(); } while (record.wsPort == record.httpPort);
            record.pythonEntry = Script;
            record.serverExe = ServerExe;
            return record;
        }

        private static StopManager.ServerStopDiscovery EmptyDiscovery() => new()
        {
            ListeningPorts = new Dictionary<int, List<int>>(),
        };

        private static void CompleteOneDiscovery(StopManager manager)
        {
            if (Get<Task>(manager, "_stopPreparation") == null) Call(manager, "PollStopObservation");
            Assert.That(SpinWait.SpinUntil(() => Get<Task>(manager, "_stopPreparation")?.IsCompleted == true, 5000), Is.True);
            Call(manager, "PollStopObservation");
        }

        private static void SetCompletedPreparation(StopManager manager, ServiceStopObservationRecord record,
            params StopManager.ServerStopTarget[] targets)
        {
            var type = typeof(StopManager).GetNestedType("PreparedProcessStop", BindingFlags.NonPublic);
            var prepared = Activator.CreateInstance(type, true);
            ((List<StopManager.ServerStopTarget>)type.GetField("Processes", PrivateInstance).GetValue(prepared)).AddRange(targets);
            type.GetField("HttpPort", PrivateInstance).SetValue(prepared, record.httpPort);
            type.GetField("WsPort", PrivateInstance).SetValue(prepared, record.wsPort);
            type.GetField("Scope", PrivateInstance).SetValue(prepared, Get<StopManager.ServerStopScope>(manager, "_stopScope"));
            type.GetField("VerifiedPorts", PrivateInstance).SetValue(prepared, new Dictionary<int, List<int>>());
            var task = typeof(Task).GetMethod("FromResult").MakeGenericMethod(type).Invoke(null, new[] { prepared });
            Set(manager, "_stopPreparation", task);
        }

        private static void FinishIsolatedManager(StopManager manager)
        {
            if (manager.HasActiveStopObservation)
            {
                if (Get<long>(manager, "_stopLifecycleGeneration") > 0)
                    LogAssert.Expect(LogType.Log, new Regex("生命周期：生命周期操作结束.*operationId=" + manager.StopObservation.id));
                LogAssert.Expect(LogType.Log, new Regex("MCP 服务停止观察结束.*状态=stopped.*fixture_finished"));
                manager.ResetStopObservation("fixture_finished");
            }
            var cancellation = Get<CancellationTokenSource>(manager, "_lifecycleQueryCancellation");
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        // The tests own every started process and never target the actual Unity Editor or MCP Server.
        private sealed class OwnedProcess : IDisposable
        {
            internal readonly Process Process;
            private readonly string _scriptPath;
            internal static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            private OwnedProcess(Process process, string scriptPath) { Process = process; _scriptPath = scriptPath; }
            internal static OwnedProcess StartSleeping() => Start(
                "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::WriteLine('UPILOT_OWNED_PROCESS_READY'); [Console]::Out.Flush(); Start-Sleep -Seconds 60\"");
            internal static OwnedProcess StartListener(int http, int ws, int listeningPort)
            {
                var script = Path.Combine(Path.GetTempPath(), "upilot-owned-listener-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(script,
                    "$ErrorActionPreference='Stop'\n" +
                    "$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, " + listeningPort + ")\n" +
                    "try { $listener.Start(); [Console]::WriteLine('UPILOT_OWNED_PROCESS_READY'); [Console]::Out.Flush(); Start-Sleep -Seconds 60 } finally { $listener.Stop() }\n",
                    new UTF8Encoding(false));
                return Start("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) +
                    " --http-port " + http + " --port " + ws, script);
            }
            private static OwnedProcess Start(string arguments, string scriptPath = null)
            {
                Process process;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = Executable, Arguments = arguments,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                }
                catch { if (scriptPath != null) File.Delete(scriptPath); throw; }
                if (process == null && scriptPath != null) File.Delete(scriptPath);
                Assert.That(process, Is.Not.Null);
                var owned = new OwnedProcess(process, scriptPath);
                try
                {
                    var ready = process.StandardOutput.ReadLineAsync();
                    Assert.That(ready.Wait(5000), Is.True, "The controlled test child did not become ready.");
                    Assert.That(ready.Result, Is.EqualTo("UPILOT_OWNED_PROCESS_READY"));
                    Assert.That(process.HasExited, Is.False);
                    return owned;
                }
                catch { owned.Dispose(); throw; }
            }
            public void Dispose()
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        Process.Kill();
                        if (!Process.WaitForExit(5000)) throw new TimeoutException("The owned test child did not exit during cleanup.");
                    }
                }
                finally
                {
                    Process.Dispose();
                    if (_scriptPath != null) File.Delete(_scriptPath);
                }
            }
        }
#endif
    }
}
