// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotServerLaunchTests
    {
        // 同一场景矩阵适用于两个描述以及两种真实进程结构；不启动/杀死服务。
        private static readonly string[] Rejections = { "valid", "wrong_parent", "reused_launcher",
            "missing_launcher_time", "older_service", "wrong_launcher_exe", "wrong_service_exe",
            "foreign_port", "unknown_port", "health_only", "wrong_health_pid", "wrong_os_pid",
            "missing_http", "missing_ws", "missing_health", "wrong_project", "wrong_configured_project",
            "wrong_entry", "wrong_module", "missing_instance", "missing_command", "wrong_command_port",
            "wrong_command_project", "wrong_command_entry", "launcher_exited", "service_exited", "missing_spec" };

        private static IEnumerable<TestCaseData> IdentityCases()
        {
            foreach (var mode in new[] { "python", "exe" })
                foreach (var child in new[] { false, true })
                    foreach (var scenario in Rejections)
                        yield return new TestCaseData(mode, child, scenario)
                            .SetName($"UnifiedIdentity_{mode}_{(child ? "child" : "single")}_{scenario}");
        }

        private static ServerLaunchSpec Spec(string mode)
        {
            var root = Path.Combine(Path.GetTempPath(), "upilot-launch-tests");
            var executable = Path.Combine(root, mode == "python" ? "venv/python.exe" : "upilot-mcp-server.exe");
            var spec = new ServerLaunchSpec
            {
                executablePath = executable, projectPath = Path.Combine(root, "project"),
                expectedEntry = mode == "python" ? Path.Combine(root, "run_upilot_mcp.py") : executable,
                expectedModuleRoot = mode == "python" ? Path.Combine(root, "src/upilot_mcp") : "",
                serviceExecutablePaths = mode == "python" ? new[] { executable, Path.Combine(root, "base/python.exe") } : new[] { executable },
                httpPort = 18011, wsPort = 18765,
                environmentKeys = new[] { "UPILOT_INSTALL_SOURCE" }, environmentValues = new[] { "Local" },
            };
            spec.arguments = $"--http-port {spec.httpPort} --port {spec.wsPort} --log-file \"{UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, spec.projectPath)}\"";
            return spec;
        }

        private static void Evidence(ServerLaunchSpec spec, bool child, out ServerProcessIdentity launch,
            out ServerProcessIdentity service, out McpServerStatus status)
        {
            launch = new ServerProcessIdentity { pid = 42, parentPid = 10, createdAt = 100,
                alive = true, executable = spec.executablePath };
            service = child ? new ServerProcessIdentity { pid = 43, parentPid = 42, createdAt = 102,
                alive = true, executable = spec.serviceExecutablePaths[spec.serviceExecutablePaths.Length - 1] } : launch;
            Assert.That(spec.TryGetLogFilePath(out var logPath), Is.True);
            status = new McpServerStatus
            {
                ProcessId = service.pid, ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                ProcessOwnershipEvidence = "监听端口与启动参数匹配", HttpPortListening = true, WsPortListening = true,
                HealthEndpointResponded = true, HealthIdentifiesUPilot = true, HealthServerProcessId = service.pid,
                HealthProjectPath = spec.projectPath,
                ProcessCommandLine = $"\"{service.executable}\" \"{spec.expectedEntry}\" --http-port {spec.httpPort} --port {spec.wsPort} --log-file \"{logPath}\"",
                Health = new UPilotServerHealth { server_pid = service.pid, server_instance_id = "instance",
                    configured_project_path = spec.projectPath, server_entry_path = spec.expectedEntry,
                    server_module_root = spec.expectedModuleRoot }
            };
        }

        [Test]
        public void FrozenLogIdentityKeepsOneExactPathWhenOutputDefaultsChange()
        {
            var spec = Spec("python");
            var currentPath = UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, spec.projectPath);
            var frozenPath = Path.Combine(spec.projectPath, "log", "mcp-server.log");
            spec.arguments = spec.arguments.Replace(currentPath, frozenPath);
            var snapshot = spec.Snapshot();
            spec.arguments = spec.arguments.Replace(frozenPath, currentPath);

            Assert.That(snapshot.TryGetLogFilePath(out var observed), Is.True);
            Assert.That(observed, Is.EqualTo(frozenPath));
            Evidence(snapshot, false, out var launch, out var service, out var status);
            Assert.That(ServerProcessIdentity.Verify(snapshot, launch.pid, launch.createdAt, launch, service, status), Is.Empty);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(snapshot, status.ProcessCommandLine.Replace(frozenPath, currentPath)), Is.False);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec, status.ProcessCommandLine), Is.False);

            var actualLegacyPath = Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log");
            var oldCommand = $"run_upilot_mcp.py --http-port 18011 --port 18765 --log-file \"{actualLegacyPath}\"";
            Assert.That(UPilotMcpServerManager.IsCurrentProjectMcpCommandLine(oldCommand, 18011, 18765), Is.False);
        }

        [TestCase("missing")]
        [TestCase("relative")]
        [TestCase("duplicate")]
        [TestCase("duplicate_without_value")]
        [TestCase("unclosed_quote")]
        [TestCase("quoted_suffix")]
        [TestCase("sibling_project")]
        [TestCase("escaped_project")]
        public void FrozenLogIdentityRejectsIncompleteAmbiguousOrForeignPaths(string scenario)
        {
            var spec = Spec("python");
            var stable = UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, spec.projectPath);
            switch (scenario)
            {
                case "missing": spec.arguments = "--http-port 18011 --port 18765"; break;
                case "relative": spec.arguments = "--log-file log/mcp-server.log"; break;
                case "duplicate": spec.arguments += $" --log-file \"{stable}\""; break;
                case "duplicate_without_value": spec.arguments += " --log-file"; break;
                case "unclosed_quote": spec.arguments = $"--log-file \"{stable}"; break;
                case "quoted_suffix": spec.arguments = $"--log-file \"{stable}\"suffix"; break;
                case "sibling_project": spec.arguments = $"--log-file \"{Path.Combine(spec.projectPath + "-other", "mcp-server.log")}\""; break;
                case "escaped_project": spec.arguments = $"--log-file \"{Path.Combine(spec.projectPath, "..", "mcp-server.log")}\""; break;
            }
            Assert.That(spec.TryGetLogFilePath(out var observed), Is.False);
            Assert.That(observed, Is.Null);
            Assert.That(ServerProcessIdentity.MatchesCommandLine(spec,
                $"--http-port 18011 --port 18765 --log-file \"{stable}\""), Is.False);
        }

        [TestCase("verified", true)]
        [TestCase("unverified", false)]
        [TestCase("missing_created_at", false)]
        [TestCase("missing_instance", false)]
        [TestCase("different_project", false)]
        [TestCase("different_port", false)]
        public void PeriodicOwnershipUsesFrozenPathOnlyForVerifiedExactServiceScope(string scenario, bool accepted)
        {
            var spec = Spec("python");
            var currentPath = UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, spec.projectPath);
            var frozenPath = Path.Combine(spec.projectPath, "log", "mcp-server.log");
            spec.arguments = spec.arguments.Replace(currentPath, frozenPath);
            var state = new ServerLaunchState { spec = spec, verified = true, launcherPid = 42,
                launcherCreatedAt = 100, servicePid = 42, serviceCreatedAt = 100, instanceId = "verified-instance" };
            var intendedProject = spec.projectPath;
            var intendedHttpPort = spec.httpPort;
            switch (scenario)
            {
                case "unverified": state.verified = false; break;
                case "missing_created_at": state.serviceCreatedAt = 0; break;
                case "missing_instance": state.instanceId = ""; break;
                case "different_project": intendedProject += "-other"; break;
                case "different_port": intendedHttpPort++; break;
            }
            Assert.That(UPilotMcpServerManager.TryGetVerifiedOwnershipLogPath(state, intendedProject,
                intendedHttpPort, spec.wsPort, out var observed), Is.EqualTo(accepted));
            Assert.That(observed, accepted ? Is.EqualTo(frozenPath) : Is.Null);
        }

        [TestCase("same_state", true)]
        [TestCase("same_values_new_state", false)]
        [TestCase("changed_service_pid", false)]
        [TestCase("changed_service_creation", false)]
        [TestCase("changed_instance", false)]
        [TestCase("changed_spec", false)]
        public void ExplicitAttachmentProofKeepsHistoryAndBindsExactServiceIdentity(string scenario, bool expected)
        {
            var state = new ServerLaunchState { spec = Spec("python"), verified = true,
                launcherPid = 42, launcherCreatedAt = 100, servicePid = 42, serviceCreatedAt = 100,
                instanceId = "original-instance", deadlineUtcMs = 5000, terminal = true, error = "original terminal" };
            var original = JsonUtility.ToJson(state);
            var verification = new UPilotMcpServerManager.ServerAttachmentVerification(state);
            Assert.That(verification.Matches(state), Is.True);
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(original));
            var candidate = state;
            switch (scenario)
            {
                case "same_values_new_state": candidate = state.Snapshot(); break;
                case "changed_service_pid": state.servicePid++; break;
                case "changed_service_creation": state.serviceCreatedAt++; break;
                case "changed_instance": state.instanceId = "replacement"; break;
                case "changed_spec": state.spec = state.spec.Snapshot(); break;
            }
            Assert.That(verification.Matches(candidate), Is.EqualTo(expected));
            Assert.That(state.deadlineUtcMs, Is.EqualTo(5000));
            Assert.That(state.terminal, Is.True);
            Assert.That(state.error, Is.EqualTo("original terminal"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitAttachmentTerminalFailureRetainsClassificationAndDoesNotWaitForever(bool timedOut)
        {
            var state = new ServerLaunchState { spec = Spec("python"), verified = true,
                launcherPid = 42, launcherCreatedAt = 100, servicePid = 42, serviceCreatedAt = 100,
                instanceId = "attachment-terminal", deadlineUtcMs = 5000, terminal = true, error = "original terminal" };
            var original = JsonUtility.ToJson(state);
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(UPilotMcpServerManager).GetField(name, flags).SetValue(manager, value);
            Set("_statusLock", new object());
            Set("_launchState", state);
            Set("_startAttemptGeneration", 7);
            var operation = "attachment-terminal-" + Guid.NewGuid().ToString("N");
            Exception exception = timedOut ? new TimeoutException("只读往返期限已到") :
                new InvalidOperationException("实际只读往返身份不符");
            var lifecycle = manager.SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, 42, operation,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000);
            try
            {
                Assert.That(UPilotQuickStart.EvaluateAttachmentFailure(
                    new UPilotMcpServerManager.ServerAttachmentFailure(state, 7, operation, exception),
                    new McpServerStatus { LifecyclePhase = UPilotServerLifecyclePhase.Verifying }, true), Is.Null);
                LogAssert.Expect(LogType.Error, new Regex("显式附着失败；operationId=" + operation + ".*"));
                Assert.That(manager.CompleteExplicitAttachmentFailure(state, 7, lifecycle, operation, exception), Is.True);
                var status = (McpServerStatus)typeof(UPilotMcpServerManager).GetField("_cachedStatus", flags).GetValue(manager);
                Assert.That(status.FailureKind, Is.EqualTo(timedOut ? McpStatusFailureKind.Transient : McpStatusFailureKind.Confirmed));
                Assert.That(UPilotHealthAssessment.Classify(default, status), Is.EqualTo(timedOut
                    ? UPilotHealthDisposition.Unconfirmed : UPilotHealthDisposition.ConfirmedFailure));
                for (var i = 0; i < 2; i++)
                {
                    var view = UPilotQuickStart.EvaluateAttachmentFailure(manager.AttachmentFailure, status, true);
                    Assert.That(view.HasValue, Is.True);
                    Assert.That(view.Value.State, Is.EqualTo(UPilotMainState.NeedsRepair));
                    Assert.That(view.Value.Title, Is.EqualTo(timedOut ? "附着验证超时" : "附着验证失败"));
                    Assert.That(view.Value.Message, Does.Contain("服务健康仍待确认"));
                    Assert.That(view.Value.Message, Does.Contain(exception.Message));
                    Assert.That(view.Value.Message, Does.Contain(operation));
                }
                Assert.That(manager.CompleteExplicitAttachmentFailure(state, 7, lifecycle, operation, exception), Is.False);
                Assert.That(JsonUtility.ToJson(state), Is.EqualTo(original));
                typeof(UPilotMcpServerManager).GetMethod("CacheVerifiedStartupStatus", flags).Invoke(manager, new object[] { status });
                Assert.That(manager.AttachmentFailure, Is.Null);
                Assert.That(JsonUtility.ToJson(state), Is.EqualTo(original));
            }
            finally
            {
                ((CancellationTokenSource)typeof(UPilotMcpServerManager).GetField("_lifecycleQueryCancellation", flags)
                    .GetValue(manager))?.Dispose();
            }
        }

        [TestCase("current", true)]
        [TestCase("new_operation", false)]
        [TestCase("new_state", false)]
        [TestCase("new_spec", false)]
        [TestCase("new_instance", false)]
        [TestCase("new_service_pid", false)]
        [TestCase("new_service_creation", false)]
        public void ExplicitAttachmentTerminalFailureBindsOnlyItsOperationAndExactService(string scenario, bool expected)
        {
            var state = new ServerLaunchState { spec = Spec("python"), verified = true,
                launcherPid = 42, launcherCreatedAt = 100, servicePid = 42, serviceCreatedAt = 100,
                instanceId = "terminal-instance", deadlineUtcMs = 5000, terminal = true, error = "original terminal" };
            var failure = new UPilotMcpServerManager.ServerAttachmentFailure(state, 7, "terminal-operation",
                new TimeoutException("fixture timeout"));
            var generation = 7;
            var candidate = state;
            switch (scenario)
            {
                case "new_operation": generation++; break;
                case "new_state": candidate = state.Snapshot(); break;
                case "new_spec": state.spec = state.spec.Snapshot(); break;
                case "new_instance": state.instanceId += "-replaced"; break;
                case "new_service_pid": state.servicePid++; break;
                case "new_service_creation": state.serviceCreatedAt++; break;
            }
            Assert.That(failure.Matches(candidate, generation), Is.EqualTo(expected));
            Assert.That(state.deadlineUtcMs, Is.EqualTo(5000));
            Assert.That(state.terminal, Is.True);
            Assert.That(state.error, Is.EqualTo("original terminal"));
        }

        [Test]
        public async Task ExplicitStartAndFreshStatusWaitForTheOriginalIdentityRecovery()
        {
            var recovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = UPilotMcpServerManager.AwaitStoppedIdentityRecoveryAsync(recovery.Task);
            Assert.That(waiting.IsCompleted, Is.False);
            recovery.SetResult(true);
            await waiting;
            Assert.That(waiting.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        }

        [Test]
        public void FreshStatusIdentityWaitKeepsItsOriginalDeadline()
        {
            var recovery = new TaskCompletionSource<bool>();
            Assert.ThrowsAsync<TimeoutException>(async () =>
                await UPilotMcpServerManager.AwaitStoppedIdentityRecoveryAsync(recovery.Task,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1));
            Assert.That(recovery.Task.IsCompleted, Is.False);
        }

        [TestCaseSource(nameof(IdentityCases))]
        public void IdentityUsesOnlyFrozenDescriptionAndEvidence(string mode, bool child, string scenario)
        {
            var spec = Spec(mode);
            Evidence(spec, child, out var launch, out var service, out var status);
            var expectedTime = 100L;
            switch (scenario)
            {
                case "wrong_parent": service.parentPid = 99; break;
                case "reused_launcher": launch.createdAt++; break;
                case "missing_launcher_time": expectedTime = 0; break;
                case "older_service": service.createdAt = 99; break;
                case "wrong_launcher_exe": launch.executable += ".foreign"; break;
                case "wrong_service_exe": service.executable += ".foreign"; break;
                case "foreign_port": status.ProcessOwnership = McpProcessOwnership.Foreign; break;
                case "unknown_port": status.ProcessOwnership = McpProcessOwnership.Unknown; break;
                case "health_only": status.ProcessOwnershipEvidence = "UPilot 健康检查响应"; break;
                case "wrong_health_pid": status.HealthServerProcessId = status.Health.server_pid = 99; break;
                case "wrong_os_pid": status.ProcessId = 99; break;
                case "missing_http": status.HttpPortListening = false; break;
                case "missing_ws": status.WsPortListening = false; break;
                case "missing_health": status.Health = null; break;
                case "wrong_project": status.HealthProjectPath += "-foreign"; break;
                case "wrong_configured_project": status.Health.configured_project_path += "-foreign"; break;
                case "wrong_entry": status.Health.server_entry_path += ".foreign"; break;
                case "wrong_module": status.Health.server_module_root += ".foreign"; break;
                case "missing_instance": status.Health.server_instance_id = ""; break;
                case "missing_command": status.ProcessCommandLine = ""; break;
                case "wrong_command_port": status.ProcessCommandLine = status.ProcessCommandLine.Replace("18011", "18012"); break;
                case "wrong_command_project": status.ProcessCommandLine = status.ProcessCommandLine.Replace("mcp-server.log", "foreign.log"); break;
                case "wrong_command_entry": status.ProcessCommandLine = status.ProcessCommandLine.Replace(spec.expectedEntry, spec.expectedEntry + ".foreign"); break;
                case "launcher_exited": launch.alive = false; break;
                case "service_exited": service.alive = false; break;
                case "missing_spec": spec = null; break;
            }
            var expected = scenario == "valid" || (scenario == "wrong_parent" && !child) ||
                (scenario == "wrong_module" && mode == "exe"); // 无模块约束的描述不伪造 Python 模块要求。
            var error = ServerProcessIdentity.Verify(spec, 42, expectedTime, launch, service, status);
            Assert.That(error.Length == 0, Is.EqualTo(expected), error);
        }

        [TestCase("python", false)] [TestCase("python", true)]
        [TestCase("exe", false)] [TestCase("exe", true)]
        public void CollectedEvidenceIsRecheckedForPidReuseAndExit(string mode, bool child)
        {
            Evidence(Spec(mode), child, out var launch, out var service, out _);
            Assert.That(ServerProcessIdentity.VerifyUnchanged(launch, launch, service, service), Is.Empty);
            var reused = service; reused.createdAt++;
            Assert.That(ServerProcessIdentity.VerifyUnchanged(launch, launch, service, reused), Is.Not.Empty);
            reused = launch; reused.createdAt++;
            Assert.That(ServerProcessIdentity.VerifyUnchanged(launch, reused, service, service), Is.Not.Empty);
            var exited = service; exited.alive = false;
            Assert.That(ServerProcessIdentity.VerifyUnchanged(launch, launch, service, exited), Is.EqualTo("process_exited"));
        }

        [TestCase("python", false)] [TestCase("python", true)]
        [TestCase("exe", false)] [TestCase("exe", true)]
        public void ReloadAndDuplicateCallbacksPreserveIdentityAndDeadline(string mode, bool child)
        {
            var spec = Spec(mode);
            var state = new ServerLaunchState { spec = spec.Snapshot(), launcherPid = 42,
                launcherCreatedAt = 100, deadlineUtcMs = 5000 };
            spec.serviceExecutablePaths[0] = "modified";
            spec.expectedEntry = "modified";
            var restored = state.Snapshot();
            Assert.That(restored.spec.serviceExecutablePaths[0], Is.Not.EqualTo("modified"));
            Assert.That(restored.spec.expectedEntry, Is.Not.EqualTo("modified"));
            Assert.That(restored.deadlineUtcMs, Is.EqualTo(5000));
            Assert.That(restored.TryConfirm(child ? 43 : 42, child ? 102 : 100, "instance", 2000), Is.True);
            Assert.That(restored.TryConfirm(child ? 43 : 42, child ? 102 : 100, "instance", 3000), Is.True);
            Assert.That(restored.TryConfirm(44, 103, "other", 3000), Is.False);
            Assert.That(restored.TryConfirm(restored.servicePid, restored.serviceCreatedAt + 1, "instance", 3000), Is.False);
            Assert.That(restored.deadlineUtcMs, Is.EqualTo(5000));
            restored.terminal = true;
            Assert.That(restored.TryConfirm(99, 200, "late", 3000), Is.False);
            Assert.That(state.Snapshot().TryConfirm(43, 102, "late", 5000), Is.False);
            Assert.That(state.verified, Is.False);
        }

        [TestCase("python", false)] [TestCase("python", true)]
        [TestCase("exe", false)] [TestCase("exe", true)]
        public void SameDiagnosticProjectionPreservesOriginalGatesAndHistory(string mode, bool child)
        {
            var state = new ServerLaunchState { spec = Spec(mode), launcherPid = 42, launcherCreatedAt = 100, deadlineUtcMs = 5000 };
            Assert.That(state.TryConfirm(child ? 43 : 42, child ? 102 : 100, "instance", 2000), Is.True);
            var record = new UPilotServerRestartRecord { launcherProcessId = 42, launcherProcessCreatedAtTicks = 100,
                newProcessId = 42, newProcessCreatedAtTicks = 100, deadlineAtUtcMs = 5000, newProcessStartedAtUtcMs = 1000 };
            Assert.That(UPilotServerRestartDiagnostics.ApplyServiceIdentity(record, state), Is.True);
            var first = JsonUtility.ToJson(record);
            Assert.That(UPilotServerRestartDiagnostics.ApplyServiceIdentity(record, state), Is.True);
            Assert.That(JsonUtility.ToJson(record), Is.EqualTo(first));
            Assert.That(record.processRelationship, Is.EqualTo(child ? "direct_child" : "same_process"));
            Assert.That(record.newProcessId, Is.EqualTo(state.servicePid));
            Assert.That(record.deadlineAtUtcMs, Is.EqualTo(5000));
            Assert.That(record.newProcessStartedAtUtcMs, Is.EqualTo(1000));
            Assert.That(record.readOnlyVerified || record.deploymentVerified || record.bridgeVerified, Is.False);
            record.status = "failed";
            var previous = JsonUtility.ToJson(record);
            Assert.That(UPilotServerRestartDiagnostics.ApplyServiceIdentity(record, state), Is.False);
            Assert.That(JsonUtility.ToJson(record), Is.EqualTo(previous));
        }

        [TestCase("python", false)] [TestCase("python", true)]
        [TestCase("exe", false)] [TestCase("exe", true)]
        public void IncompleteOwnershipRemainsPendingWithoutPidFallback(string mode, bool child)
        {
            Evidence(Spec(mode), child, out _, out _, out var status);
            status.WsPortListening = false;
            Assert.That(UPilotMcpServerManager.ClassifyLaunchIdentityProbe(status, false, "service_ownership_unverified"),
                Is.EqualTo("process_identity_pending"));
            Assert.That(UPilotMcpServerManager.ClassifyLaunchIdentityProbe(status, false, "service_relationship_mismatch"),
                Is.EqualTo("process_identity_mismatch"));
            status.ProcessOwnership = McpProcessOwnership.Foreign;
            Assert.That(UPilotMcpServerManager.ClassifyLaunchIdentityProbe(status, false, "service_ownership_unverified"),
                Is.EqualTo("process_identity_mismatch"));
        }

        [TestCase("python", false)] [TestCase("python", true)]
        [TestCase("exe", false)] [TestCase("exe", true)]
        public void NewStopObservationNeverRevivesFailedLaunch(string mode, bool child)
        {
            var state = new ServerLaunchState { spec = Spec(mode), launcherPid = 42, launcherCreatedAt = 100,
                terminal = true, error = "original failure", deadlineUtcMs = 5000 };
            Assert.That(state.TryConfirm(child ? 43 : 42, child ? 102 : 100, "instance", 6000, stopping: true), Is.True);
            Assert.That(state.deadlineUtcMs, Is.EqualTo(5000));
            Assert.That(state.terminal, Is.True);
            Assert.That(state.error, Is.EqualTo("original failure"));
            Assert.That(state.CanObserve(2000), Is.False);
            Assert.That(state.TryConfirm(99, 200, "different", 6000, stopping: true), Is.False);
        }

        [TestCase(null, UPilotServerRuntimeMode.Python)]
        [TestCase("python", UPilotServerRuntimeMode.Python)]
        [TestCase("exe", UPilotServerRuntimeMode.StandaloneExe)]
        [TestCase("EXE", UPilotServerRuntimeMode.StandaloneExe)]
        public void ExplicitModeIsNotReinterpretedByUpdateChannel(string configured, UPilotServerRuntimeMode expected) =>
            Assert.That(UPilotServerRuntimeService.ResolveConfiguredMode(configured), Is.EqualTo(expected));

        [TestCase(UPilotServerRuntimeMode.Python)]
        [TestCase(UPilotServerRuntimeMode.StandaloneExe)]
        public void AdapterFreezesActualLaunchDescriptionWithoutStartingService(UPilotServerRuntimeMode mode)
        {
            var python = UPilotProjectConfig.Current.runtime?.pythonPath;
            if (mode == UPilotServerRuntimeMode.Python && !File.Exists(python)) Assert.Ignore("未配置可探测的解释器");
            using var editor = System.Diagnostics.Process.GetCurrentProcess();
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5000;
            var spec = ServerLaunchSpec.Create(mode, python, UPilotDeploymentDiagnostics.PythonEntry,
                editor.MainModule.FileName, UPilotProjectConfig.ProjectRoot,
                Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "upilotserver~", "src", "upilot_mcp"),
                18011, 18765, "INFO", "source", "Local", deadline);
            Assert.That(spec.Validate(), Is.Empty);
            Assert.That(spec.StartInfo().WorkingDirectory, Is.EqualTo(UPilotProjectConfig.ProjectRoot));
            Assert.That(spec.serviceExecutablePaths, Does.Contain(spec.executablePath));
            Assert.That(spec.StartInfo().UseShellExecute, Is.False);
            Assert.That(spec.StartInfo().CreateNoWindow, Is.True);
            Assert.That(spec.arguments, Does.Contain("--http-port 18011 --port 18765"));
        }

        [TestCase(0, 1000, 5000)] [TestCase(7000, 1000, 5000)] [TestCase(1100, 1000, 100)]
        public void InterpreterProbeSharesOriginalBudget(long deadline, long now, int expected) =>
            Assert.That(ServerLaunchSpec.ProbeBudget(deadline, now), Is.EqualTo(expected));

        [Test]
        public void ExpiredProbeDoesNotReceiveAnotherBudget() =>
            Assert.Throws<TimeoutException>(() => ServerLaunchSpec.ProbeBudget(1000, 1000));
    }
}
