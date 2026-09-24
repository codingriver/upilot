// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityEditor.PackageManager;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotServerHealth
    {
        public string server_version;
        public string protocol_version;
        public string build_channel;
        public string build_commit;
        public int identity_contract_version;
        public int server_pid;
        public string server_instance_id;
        public long server_started_at_ms;
        public string server_entry_path;
        public string server_module_root;
        public string server_install_source;
        public string configured_project_path;
        public string project_path;
        public string bridge_session_id;
        public bool bridge_probe_ok;
        public string bridge_probe_nonce;
        public string bridge_probe_error;
        public UPilotBridgeHealthDiagnostic bridge_diagnostics;
    }

    [Serializable]
    internal sealed class UPilotBridgeHealthDiagnostic
    {
        public long connected_at_ms;
        public long authenticated_at_ms;
        public long disconnected_at_ms;
        public string last_close_reason;
        public int oversize_count;
        public long oversize_at_ms;
        public string oversize_source;
        public int oversize_actual_bytes;
        public int oversize_limit_bytes;
        public string last_close_code;
    }

    internal sealed class UPilotDeploymentIssue
    {
        public string Code;
        public string Message;
        public bool Confirmed;
    }

    // A domain's Bridge and its package identity stay together until reload.
    internal static class UPilotDeploymentDiagnostics
    {
        private static readonly Dictionary<string, UPilotDeploymentIssue> Issues = new();
        private static string _version;
        private static string _root;
        private static string _channel;
        private static string _source;
        private static bool _main;
        private static long _checkingSince;
        private static string _lastDetails = "";

        internal static string Version { get { Initialize(); return _version; } }
        internal static string PackageRoot { get { Initialize(); return _root; } }
        internal static string Channel { get { Initialize(); return _channel; } }
        internal static string InstallSource { get { Initialize(); return _source; } }
        internal static bool IsMain { get { Initialize(); return _main; } }
        internal static string PythonEntry => Path.Combine(PackageRoot, "upilotserver~", "run_upilot_mcp.py");
        internal static string Details => _lastDetails;

        private static void Initialize()
        {
            if (_version != null) return;
            var package = PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
            _root = RealPath(UPilotServerRuntimeService.GetPackageRoot());
            _version = UPilotServerRuntimeService.UpmVersion;
            _source = package?.source.ToString() ?? "Unknown";
            var id = package?.packageId ?? "";
            var hash = id.LastIndexOf('#');
            var gitRef = hash >= 0 ? id.Substring(hash + 1) : "";
            _main = package?.source == PackageSource.Git && hash >= 0 &&
                    string.Equals(id.Substring(hash + 1), "main", StringComparison.Ordinal);
            if (package?.source == PackageSource.Local || package?.source == PackageSource.Embedded)
                _main = IsLocalMain(_root);
            _channel = package?.source == PackageSource.Local || package?.source == PackageSource.Embedded ||
                       (package?.source == PackageSource.Git && !UPilotServerRuntimeService.IsStrictSemver(gitRef)) ||
                       _main || !UPilotServerRuntimeService.IsStrictSemver(_version) ? "source" : "release";
        }

        private static bool IsLocalMain(string root)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git", Arguments = "symbolic-ref --quiet --short HEAD",
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (process == null) return false;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(1500)) { try { process.Kill(); } catch { } return false; }
                return output.Wait(100) && process.ExitCode == 0 &&
                       string.Equals(output.Result.Trim(), "main", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        internal static string ExpectedEntry
        {
            get
            {
                if (UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.Python)
                    return RealPath(PythonEntry);
                return RealPath(UPilotProjectConfig.Current.runtime?.serverExePath);
            }
        }

        internal static UPilotDeploymentIssue[] Observe(BridgeStatus bridge, McpServerStatus server)
        {
            Initialize();
            if (!bridge.IsStarted && !server.IsRunning && server.StatusQueryCompleted &&
                string.IsNullOrEmpty(server.ErrorMessage))
            {
                _checkingSince = 0;
                return Issues.Values.ToArray();
            }
            if (_checkingSince == 0) _checkingSince = Stopwatch.GetTimestamp();
            Check("authentication", bridge.IsAuthenticated ? true :
                    string.IsNullOrEmpty(bridge.AuthenticationError) ? (bool?)null : false,
                bridge.AuthenticationError ?? "Bridge 尚未完成身份握手", missingIsError: false);
            Check("status", string.IsNullOrEmpty(server.ErrorMessage) ? server.StatusQueryCompleted ? true : (bool?)null : false,
                server.ErrorMessage ?? "状态尚未返回", missingIsError: false);
            if (server.StatusQueryCompleted && server.IsRunning)
            {
                Check("ownership", server.ProcessOwnership == McpProcessOwnership.CurrentUPilot,
                    $"项目或进程身份未通过：期望 {UPilotProjectConfig.ProjectRoot}；PID={server.ProcessId?.ToString() ?? "未知"}；{server.ProcessOwnershipEvidence}");
                Check("health", server.HealthEndpointResponded,
                    "状态获取失败：/health 未成功返回；" + server.StatusFailureStage);
                if (UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.Python)
                    Check("configured_entry", SamePath(
                        Path.IsPathRooted(UPilotMcpServerManager.Instance.PythonEntryPath)
                            ? UPilotMcpServerManager.Instance.PythonEntryPath
                            : Path.Combine(UPilotProjectConfig.ProjectRoot, UPilotMcpServerManager.Instance.PythonEntryPath ?? ""),
                        PythonEntry),
                        $"配置入口与当前包不一致：配置={UPilotMcpServerManager.Instance.PythonEntryPath}；配套={PythonEntry}。自定义入口需要明确修改。");
            }
            var health = server.Health;
            if (server.HealthEndpointResponded && health != null)
            {
                Compare("version", IsMain ? true : Known(health.server_version)
                    ? string.Equals(Version, health.server_version, StringComparison.Ordinal) : (bool?)null,
                    $"版本不一致：Bridge={Version}；Server={Text(health.server_version)}");
                Compare("channel", Known(health.build_channel) ? Channel == health.build_channel : (bool?)null,
                    $"渠道不一致：Bridge={Channel} ({InstallSource})；Server={Text(health.build_channel)} ({Text(health.server_install_source)})");
                Compare("protocol", health.identity_contract_version == 0 || !Known(health.protocol_version) ? (bool?)null :
                    health.identity_contract_version == UPilotBridge.IdentityContractVersion && health.protocol_version == "1",
                    $"协议不兼容：期望身份契约={UPilotBridge.IdentityContractVersion}、协议=1；实际身份契约=" +
                    $"{(health.identity_contract_version == 0 ? "未知/未返回" : health.identity_contract_version.ToString())}、协议={Text(health.protocol_version)}");
                Compare("entry", Known(health.server_entry_path) ? SamePath(ExpectedEntry, health.server_entry_path) : (bool?)null,
                    $"Server 入口错误：当前包配套入口={ExpectedEntry}；实际入口={Text(health.server_entry_path)}");
                if (UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.Python)
                    Compare("module", Known(health.server_module_root) ?
                        SamePath(Path.Combine(PackageRoot, "upilotserver~", "src", "upilot_mcp"), health.server_module_root) : (bool?)null,
                        $"Server 模块来源错误：当前包={PackageRoot}；实际模块={Text(health.server_module_root)}");
                Compare("project", Known(health.configured_project_path) ?
                    SamePath(UPilotProjectConfig.ProjectRoot, health.configured_project_path) : (bool?)null,
                    $"项目身份冲突：期望={UPilotProjectConfig.ProjectRoot}；Server={Text(health.configured_project_path)}");
                Compare("instance", health.server_pid > 0 && Known(health.server_instance_id) && health.server_started_at_ms > 0 ?
                    server.ProcessId == health.server_pid : (bool?)null,
                    $"进程身份不完整或冲突：监听 PID={server.ProcessId?.ToString() ?? "未知"}；Server PID={health.server_pid}；实例={Text(health.server_instance_id)}");
            }
            var complete = server.StatusQueryCompleted && server.HealthEndpointResponded &&
                           bridge.IsAuthenticated && bridge.IsWsOpen && server.HttpPortListening && server.WsPortListening &&
                           Issues.Keys.All(key => key == "timeout");
            if (complete)
            {
                Issues.Remove("timeout");
                _checkingSince = 0;
            }
            else if ((Stopwatch.GetTimestamp() - _checkingSince) / (double)Stopwatch.Frequency >= 30)
            {
                Check("timeout", false, "状态获取超时（30 秒）：未完成连接、进程身份、健康信息或握手检查。");
            }
            _lastDetails = $"Bridge：{Version}；渠道={Channel}；安装方式={InstallSource}；明确 main={IsMain}\n" +
                $"当前包：{PackageRoot}\n配套入口：{ExpectedEntry}\n" +
                $"Server：{Text(health?.server_version)}；渠道={Text(health?.build_channel)}；安装来源={Text(health?.server_install_source)}\n" +
                $"实际入口：{Text(health?.server_entry_path)}\n实际模块：{Text(health?.server_module_root)}\n" +
                $"期望项目：{UPilotProjectConfig.ProjectRoot}\n实际项目：{Text(health?.configured_project_path)}\n" +
                $"PID：{server.ProcessId?.ToString() ?? "未知"}；实例：{Text(health?.server_instance_id)}\n" +
                $"最后成功状态：{(server.LastSuccessfulStatusAtUtcMs > 0 ? UPilotRestartDiagnosticView.Time(server.LastSuccessfulStatusAtUtcMs) : "未知")}\n" +
                string.Join("\n", Issues.Values.Select(issue => issue.Code + "： " + issue.Message));
            return Issues.Values.ToArray();
        }

        private static void Compare(string key, bool? passed, string message) =>
            Check(key, passed, passed.HasValue ? message : "协议字段缺失或未返回；" + message);

        private static void Check(string key, bool? passed, string message, bool missingIsError = true)
        {
            if (passed == true) { Issues.Remove(key); return; }
            if (!passed.HasValue && (Issues.ContainsKey(key) || !missingIsError)) return;
            Issues[key] = new UPilotDeploymentIssue { Code = key, Message = message, Confirmed = passed.HasValue };
        }

        private static bool Known(string value) => !string.IsNullOrWhiteSpace(value) && value != "unknown";
        private static string Text(string value) => Known(value) ? value : "未知/未返回";

        internal static bool SamePath(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
            string.Equals(RealPath(left), RealPath(right),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        internal static string RealPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var full = Path.GetFullPath(path);
#if UNITY_EDITOR_WIN
            using var handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length > 0 && length < buffer.Capacity)
                {
                    full = buffer.ToString();
                    if (full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) full = @"\\" + full.Substring(8);
                    else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full.Substring(4);
                }
            }
#endif
            return full.Replace('\\', '/').TrimEnd('/');
        }

#if UNITY_EDITOR_WIN
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
#endif
    }
}
