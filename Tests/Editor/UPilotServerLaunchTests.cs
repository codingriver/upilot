// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

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
            return new ServerLaunchSpec
            {
                executablePath = executable, projectPath = Path.Combine(root, "project"),
                expectedEntry = mode == "python" ? Path.Combine(root, "run_upilot_mcp.py") : executable,
                expectedModuleRoot = mode == "python" ? Path.Combine(root, "src/upilot_mcp") : "",
                serviceExecutablePaths = mode == "python" ? new[] { executable, Path.Combine(root, "base/python.exe") } : new[] { executable },
                httpPort = 18011, wsPort = 18765, arguments = "test",
                environmentKeys = new[] { "UPILOT_INSTALL_SOURCE" }, environmentValues = new[] { "Local" },
            };
        }

        private static void Evidence(ServerLaunchSpec spec, bool child, out ServerProcessIdentity launch,
            out ServerProcessIdentity service, out McpServerStatus status)
        {
            launch = new ServerProcessIdentity { pid = 42, parentPid = 10, createdAt = 100,
                alive = true, executable = spec.executablePath };
            service = child ? new ServerProcessIdentity { pid = 43, parentPid = 42, createdAt = 102,
                alive = true, executable = spec.serviceExecutablePaths[spec.serviceExecutablePaths.Length - 1] } : launch;
            status = new McpServerStatus
            {
                ProcessId = service.pid, ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                ProcessOwnershipEvidence = "监听端口与启动参数匹配", HttpPortListening = true, WsPortListening = true,
                HealthEndpointResponded = true, HealthIdentifiesUPilot = true, HealthServerProcessId = service.pid,
                HealthProjectPath = spec.projectPath,
                ProcessCommandLine = $"\"{service.executable}\" \"{spec.expectedEntry}\" --http-port {spec.httpPort} --port {spec.wsPort} --log-file \"{Path.Combine(spec.projectPath, "log", "mcp-server.log")}\"",
                Health = new UPilotServerHealth { server_pid = service.pid, server_instance_id = "instance",
                    configured_project_path = spec.projectPath, server_entry_path = spec.expectedEntry,
                    server_module_root = spec.expectedModuleRoot }
            };
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
