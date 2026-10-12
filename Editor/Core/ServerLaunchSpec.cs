// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>本次启动的冻结描述。运行时差异只在创建描述时处理。</summary>
    [Serializable]
    internal sealed class ServerLaunchSpec
    {
        public string executablePath;
        public string arguments;
        public string projectPath;
        public string expectedEntry;
        public string expectedModuleRoot;
        public string[] serviceExecutablePaths;
        public string[] environmentKeys;
        public string[] environmentValues;
        public int httpPort, wsPort;

        internal ServerLaunchSpec Snapshot() => JsonUtility.FromJson<ServerLaunchSpec>(JsonUtility.ToJson(this));

        internal string Validate()
        {
            if (!Absolute(executablePath) || !Absolute(projectPath) || !Absolute(expectedEntry) ||
                (!string.IsNullOrEmpty(expectedModuleRoot) && !Absolute(expectedModuleRoot)))
                return "启动描述的路径身份缺失或不是绝对路径";
            if (serviceExecutablePaths == null || serviceExecutablePaths.Length == 0 ||
                serviceExecutablePaths.Length > 4 || serviceExecutablePaths.Any(p => !Absolute(p)))
                return "服务可执行文件身份缺失";
            if (httpPort <= 0 || httpPort > 65535 || wsPort <= 0 || wsPort > 65535 || httpPort == wsPort)
                return "启动描述的端口无效";
            if (environmentKeys == null || environmentValues == null || environmentKeys.Length != environmentValues.Length)
                return "启动环境描述不完整";
            return "";
        }

        private static bool Absolute(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

        internal ProcessStartInfo StartInfo()
        {
            var error = Validate();
            if (error.Length != 0) throw new InvalidOperationException(error);
            var info = new ProcessStartInfo
            {
                FileName = executablePath, Arguments = arguments, WorkingDirectory = projectPath,
                UseShellExecute = false, CreateNoWindow = true,
            };
            for (var i = 0; i < environmentKeys.Length; i++)
                info.EnvironmentVariables[environmentKeys[i]] = environmentValues[i];
            return info;
        }

        /// <summary>唯一运行模式分支；不启动或接管服务进程。</summary>
        internal static ServerLaunchSpec Create(UPilotServerRuntimeMode mode, string python, string entry,
            string exe, string project, string module, int http, int ws, string logLevel,
            string channel, string installSource, long deadlineUtcMs)
        {
            var spec = new ServerLaunchSpec
            {
                projectPath = Path.GetFullPath(project), httpPort = http, wsPort = ws,
                expectedModuleRoot = "", environmentKeys = new[] { "UPILOT_BUILD_CHANNEL", "UPILOT_INSTALL_SOURCE" },
                environmentValues = new[] { channel, installSource },
            };
            var common = $"--transport http --http-port {http} --port {ws} --log-file \"{Path.Combine(spec.projectPath, "log", "mcp-server.log")}\" --log-level {logLevel}";
            if (mode == UPilotServerRuntimeMode.Python)
            {
                if (string.IsNullOrWhiteSpace(python)) python = FindPython();
                if (!Absolute(python) || !File.Exists(python)) throw new FileNotFoundException("Python 解释器不存在", python);
                if (!Absolute(entry) || !File.Exists(entry)) throw new FileNotFoundException("Server 入口不存在", entry);
                var identity = ProbePython(python, deadlineUtcMs);
                spec.executablePath = Path.GetFullPath(python);
                spec.expectedEntry = Path.GetFullPath(entry);
                spec.expectedModuleRoot = Path.GetFullPath(module);
                spec.serviceExecutablePaths = new[] { identity.executable, identity.baseExecutable }
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                spec.arguments = $"\"{spec.expectedEntry}\" {common}";
            }
            else
            {
                if (!Absolute(exe) || !File.Exists(exe)) throw new FileNotFoundException("独立 Server 文件不存在", exe);
                spec.executablePath = spec.expectedEntry = Path.GetFullPath(exe);
                spec.serviceExecutablePaths = new[] { spec.executablePath };
                spec.arguments = common;
                spec.environmentValues[1] = "ManagedExe";
            }
            var error = spec.Validate();
            if (error.Length != 0) throw new InvalidOperationException(error);
            return spec;
        }

        [Serializable]
        private sealed class PythonIdentity
        {
            public string executable, baseExecutable, prefix;
        }

        private static PythonIdentity ProbePython(string python, long deadlineUtcMs)
        {
            var budget = ProbeBudget(deadlineUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            const string script = "import sys,json;print(json.dumps(dict(executable=sys.executable,baseExecutable=getattr(sys,'_base_executable',sys.executable),prefix=sys.prefix)))";
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = python, Arguments = "-I -B -c \"" + script + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            });
            if (process == null) throw new InvalidOperationException("Python 身份探测未启动");
            var output = ReadBoundedAsync(process.StandardOutput, 4096);
            var error = ReadBoundedAsync(process.StandardError, 4096);
            if (!process.WaitForExit(Math.Max(0, budget - (int)clock.ElapsedMilliseconds)) || !Task.WaitAll(new Task[] { output, error },
                    Math.Max(0, budget - (int)clock.ElapsedMilliseconds)))
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                _ = output.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _ = error.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("Python 身份探测超过 5 秒或本次维护剩余期限");
            }
            if (process.ExitCode != 0 || output.Result.Length > 4096)
                throw new InvalidOperationException("Python 身份探测失败，退出码 " + process.ExitCode);
            PythonIdentity identity;
            try { identity = JsonUtility.FromJson<PythonIdentity>(output.Result.Trim()); }
            catch (Exception) { throw new InvalidOperationException("Python 身份探测未返回有效 JSON"); }
            if (identity == null || !Absolute(identity.executable) || !Absolute(identity.baseExecutable) ||
                !Absolute(identity.prefix) || !File.Exists(identity.executable) || !File.Exists(identity.baseExecutable))
                throw new InvalidOperationException("Python 身份探测缺少可核实的解释器路径");
            // 探测的是本次实际选择的解释器，不把 py.exe 等多级启动器隐式纳入信任链。
            if (!UPilotDeploymentDiagnostics.SamePath(python, identity.executable))
                throw new InvalidOperationException("Python 探测路径与启动路径不一致");
            ProbeBudget(deadlineUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return identity;
        }

        private static string FindPython()
        {
            // 只搜索真实可执行文件，不运行 py.exe 等另一层启动器。
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                foreach (var name in new[] { "python.exe", "python3.exe", "python3", "python" })
                {
                    try
                    {
                        var path = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
                        if (File.Exists(path)) return path;
                    }
                    catch { }
                }
            return "";
        }

        internal static int ProbeBudget(long deadlineUtcMs, long now)
        {
            var remaining = deadlineUtcMs > 0 ? deadlineUtcMs - now : 5000;
            if (remaining <= 0) throw new TimeoutException("启动身份探测前本次期限已到");
            return (int)Math.Min(5000, remaining);
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
        {
            var text = new StringBuilder();
            var buffer = new char[512];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var keep = Math.Min(count, Math.Max(0, limit + 1 - text.Length));
                if (keep > 0) text.Append(buffer, 0, keep);
            }
            return text.ToString();
        }
    }

    internal struct ServerProcessIdentity
    {
        public int pid, parentPid;
        public long createdAt;
        public string executable;
        public bool alive;

        internal static ServerProcessIdentity Read(Process process) => new()
        {
            pid = process.Id, parentPid = UPilotMcpServerManager.ReadParentProcessId(process.Id),
            createdAt = process.StartTime.ToUniversalTime().Ticks,
            executable = process.MainModule.FileName, alive = !process.HasExited,
        };

        /// <summary>只校验证据；不读取运行模式、配置或自行寻找其他进程。</summary>
        internal static string Verify(ServerLaunchSpec spec, int launchPid, long launchCreatedAt,
            ServerProcessIdentity launch, ServerProcessIdentity service, McpServerStatus status)
        {
            if (spec == null || spec.Validate().Length != 0) return "launch_spec_missing";
            if (!launch.alive || !service.alive) return "process_exited";
            if (launchPid <= 0 || launchCreatedAt <= 0 || launch.pid != launchPid || launch.createdAt != launchCreatedAt)
                return "launcher_identity_changed";
            if (!UPilotDeploymentDiagnostics.SamePath(launch.executable, spec.executablePath)) return "launcher_executable_mismatch";
            if (service.pid <= 0 || service.createdAt <= 0 ||
                (service.pid == launchPid ? service.createdAt != launchCreatedAt :
                 service.parentPid != launchPid || service.createdAt < launchCreatedAt)) return "service_relationship_mismatch";
            if (!spec.serviceExecutablePaths.Any(p => UPilotDeploymentDiagnostics.SamePath(p, service.executable)))
                return "service_executable_mismatch";
            if (!UPilotMcpServerManager.IsVerifiedStartupHealth(status) || !status.HttpPortListening || !status.WsPortListening ||
                status.ProcessId != service.pid || status.Health?.server_pid != service.pid) return "service_ownership_unverified";
            if (!UPilotDeploymentDiagnostics.SamePath(spec.projectPath, status.HealthProjectPath) ||
                !UPilotDeploymentDiagnostics.SamePath(spec.projectPath, status.Health.configured_project_path)) return "project_identity_mismatch";
            if (!UPilotDeploymentDiagnostics.SamePath(spec.expectedEntry, status.Health.server_entry_path)) return "entry_identity_mismatch";
            if (!string.IsNullOrEmpty(spec.expectedModuleRoot) &&
                !UPilotDeploymentDiagnostics.SamePath(spec.expectedModuleRoot, status.Health.server_module_root)) return "module_identity_mismatch";
            if (string.IsNullOrEmpty(status.Health.server_instance_id)) return "server_instance_missing";
            var tokens = Regex.Matches(status.ProcessCommandLine ?? "", "\"([^\"]*)\"|(\\S+)").Cast<Match>()
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (!tokens.Any(t => Path.IsPathRooted(t) && UPilotDeploymentDiagnostics.SamePath(t, spec.expectedEntry))) return "entry_command_line_mismatch";
            if (!MatchesCommandLine(spec, status.ProcessCommandLine))
                return "command_line_identity_mismatch";
            return "";
        }

        internal static bool MatchesCommandLine(ServerLaunchSpec spec, string command)
        {
            if (spec == null || string.IsNullOrWhiteSpace(command)) return false;
            string Argument(string name)
            {
                var matches = Regex.Matches(command, "(?:^|\\s)" + Regex.Escape(name) + "(?:\\s+|=)(?:\"([^\"]+)\"|(\\S+))");
                if (matches.Count != 1) return "";
                return matches[0].Groups[1].Success ? matches[0].Groups[1].Value : matches[0].Groups[2].Value;
            }
            var log = Argument("--log-file");
            return Argument("--http-port") == spec.httpPort.ToString() && Argument("--port") == spec.wsPort.ToString() &&
                Path.IsPathRooted(log) && UPilotDeploymentDiagnostics.SamePath(log, Path.Combine(spec.projectPath, "log", "mcp-server.log"));
        }

        internal static string VerifyUnchanged(ServerProcessIdentity launch, ServerProcessIdentity launchAfter,
            ServerProcessIdentity service, ServerProcessIdentity serviceAfter) =>
            !launchAfter.alive || !serviceAfter.alive ? "process_exited" :
            launch.pid != launchAfter.pid || launch.createdAt != launchAfter.createdAt ||
            service.pid != serviceAfter.pid || service.createdAt != serviceAfter.createdAt ||
            service.parentPid != serviceAfter.parentPid ||
            !UPilotDeploymentDiagnostics.SamePath(launch.executable, launchAfter.executable) ||
            !UPilotDeploymentDiagnostics.SamePath(service.executable, serviceAfter.executable)
                ? "process_identity_changed_during_probe" : "";
    }

    /// <summary>只跨同一 Editor 的 Domain Reload 保存进程观察；不恢复任务或重放启动。</summary>
    [Serializable]
    internal sealed class ServerLaunchState
    {
        public ServerLaunchSpec spec;
        public int launcherPid, servicePid;
        public long launcherCreatedAt, serviceCreatedAt, deadlineUtcMs;
        public bool verified, terminal;
        public string instanceId = "", error = "";

        internal ServerLaunchState Snapshot() => JsonUtility.FromJson<ServerLaunchState>(JsonUtility.ToJson(this));
        internal bool CanObserve(long now) => !terminal && (verified || now < deadlineUtcMs);
        internal bool TryConfirm(int pid, long createdAt, string instance, long now, bool stopping = false)
        {
            if ((!stopping && !CanObserve(now)) || pid <= 0 || createdAt <= 0 || string.IsNullOrEmpty(instance) ||
                !IsSameService(pid, createdAt, instance)) return false;
            servicePid = pid; serviceCreatedAt = createdAt; instanceId = instance;
            verified = true;
            if (!stopping) error = "";
            return true;
        }

        internal bool IsSameService(int pid, long createdAt, string instance) =>
            !verified || servicePid == pid && serviceCreatedAt == createdAt && instanceId == instance;
    }
}
