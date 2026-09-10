// -----------------------------------------------------------------------
// UPilot Editor - Windows update file-occupancy diagnostics and recovery.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotOccupyingProcess
    {
        public int ProcessId;
        public string ProcessName = "";
        public string CommandLine = "";
        public readonly List<string> Resources = new();
        public bool CanTerminate;
        public string CannotTerminateReason = "";
        public bool IsSemanticUPilotProcess;

        public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
            ? "未知进程"
            : ProcessName;
    }

    internal sealed class UPilotOccupancyScanResult
    {
        public readonly List<UPilotOccupyingProcess> Processes = new();
        public readonly List<string> Diagnostics = new();

        public bool HasTerminableProcesses => Processes.Any(process => process.CanTerminate);
    }

    internal sealed class UPilotOccupancyTerminationResult
    {
        public readonly List<string> Succeeded = new();
        public readonly List<string> Failed = new();
    }

    /// <summary>
    /// Finds processes that block a managed-server update. Restart Manager is
    /// used for concrete Windows file locks. Python processes are additionally
    /// identified by command line because CPython normally releases .py handles
    /// after import while still keeping the old code live in memory.
    /// </summary>
    internal static class UPilotProcessOccupancyService
    {
        private const int ErrorMoreData = 234;
        private const int CchRmSessionKey = 32;
        private const int CchRmMaxAppName = 255;
        private const int CchRmMaxSvcName = 63;

        internal static UPilotOccupancyScanResult Scan(IEnumerable<string> resourcePaths)
        {
            var result = new UPilotOccupancyScanResult();
            var byPid = new Dictionary<int, UPilotOccupyingProcess>();
            var normalizedResources = (resourcePaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

#if UNITY_EDITOR_WIN
            ScanWindowsFileLocks(normalizedResources, byPid, result.Diagnostics);
#else
            result.Diagnostics.Add("当前平台未实现 Restart Manager 文件占用扫描。");
#endif
            ScanSemanticUPilotProcesses(byPid, result.Diagnostics);
            result.Processes.AddRange(byPid.Values.OrderBy(process => process.ProcessId));
            return result;
        }

        internal static bool IsUPilotServerCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;
            return commandLine.IndexOf("run_upilot_mcp.py", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   commandLine.IndexOf("upilot_mcp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string BuildConfirmationMessage(UPilotOccupancyScanResult scan)
        {
            var builder = new StringBuilder();
            builder.AppendLine("以下所有可结束进程都会被强制终止，并自动重试更新。");
            builder.AppendLine("这可能中断其他 Unity 工程的 UPilot 服务。");
            builder.AppendLine();
            foreach (var process in scan.Processes)
            {
                builder.Append("• ").Append(process.DisplayName).Append(" (PID ")
                    .Append(process.ProcessId).Append(')');
                if (!process.CanTerminate)
                    builder.Append(" — 无法结束：").Append(process.CannotTerminateReason);
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(process.CommandLine))
                    builder.AppendLine("  " + Truncate(process.CommandLine, 220));
            }
            return builder.ToString().TrimEnd();
        }

        internal static UPilotOccupancyTerminationResult TerminateAll(UPilotOccupancyScanResult scan)
        {
            var result = new UPilotOccupancyTerminationResult();
            if (scan == null)
                return result;

            foreach (var occupant in scan.Processes.Where(process => process.CanTerminate))
            {
                try
                {
                    if (TerminateProcessTree(occupant.ProcessId, out var error))
                        result.Succeeded.Add($"{occupant.DisplayName} (PID {occupant.ProcessId})");
                    else
                        result.Failed.Add($"{occupant.DisplayName} (PID {occupant.ProcessId}): {error}");
                }
                catch (Exception ex)
                {
                    result.Failed.Add($"{occupant.DisplayName} (PID {occupant.ProcessId}): {ex.Message}");
                }
            }
            return result;
        }

        private static void ScanSemanticUPilotProcesses(
            IDictionary<int, UPilotOccupyingProcess> byPid,
            ICollection<string> diagnostics)
        {
            var currentPid = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentPid)
                        continue;
                    var name = process.ProcessName ?? "";
                    // Only Python needs semantic treatment: a normal versioned
                    // EXE can safely keep running from its own cache path and is
                    // included only when Restart Manager proves a real file lock.
                    if (name.IndexOf("python", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var commandLine = UPilotMcpServerManager.GetProcessCommandLineForDiagnostics(process.Id);
                    if (!IsUPilotServerCommandLine(commandLine))
                        continue;

                    var occupant = GetOrCreate(byPid, process.Id, name, commandLine);
                    occupant.IsSemanticUPilotProcess = true;
                    AddResource(occupant, "UPilot Python/Server 运行进程（源码语义占用）");
                }
                catch (Exception ex)
                {
                    diagnostics.Add("读取 UPilot 进程信息失败：" + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

#if UNITY_EDITOR_WIN
        private static void ScanWindowsFileLocks(
            string[] resources,
            IDictionary<int, UPilotOccupyingProcess> byPid,
            ICollection<string> diagnostics)
        {
            if (resources.Length == 0)
                return;

            uint session = 0;
            var key = new StringBuilder(CchRmSessionKey + 1);
            var start = RmStartSession(out session, 0, key);
            if (start != 0)
            {
                diagnostics.Add("Restart Manager 初始化失败，错误码=" + start);
                return;
            }

            try
            {
                var register = RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null);
                if (register != 0)
                {
                    diagnostics.Add("Restart Manager 注册文件失败，错误码=" + register);
                    return;
                }

                uint needed = 0;
                uint count = 0;
                uint rebootReasons = 0;
                var status = RmGetList(session, out needed, ref count, null, ref rebootReasons);
                if (status == ErrorMoreData)
                {
                    var entries = new RmProcessInfo[needed];
                    count = needed;
                    status = RmGetList(session, out needed, ref count, entries, ref rebootReasons);
                    if (status == 0)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            var entry = entries[index];
                            var pid = entry.Process.dwProcessId;
                            if (pid <= 0)
                                continue;
                            var commandLine = UPilotMcpServerManager.GetProcessCommandLineForDiagnostics(pid);
                            var occupant = GetOrCreate(byPid, pid, entry.AppName, commandLine);
                            foreach (var resource in resources)
                                AddResource(occupant, resource);
                        }
                    }
                }
                else if (status != 0)
                {
                    diagnostics.Add("Restart Manager 查询文件占用失败，错误码=" + status);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("Restart Manager 查询异常：" + ex.Message);
            }
            finally
            {
                RmEndSession(session);
            }
        }

        private static bool TerminateProcessTree(int processId, out string error)
        {
            error = "";
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {processId} /T /F",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(info);
                if (process == null)
                {
                    error = "无法启动 taskkill";
                    return false;
                }
                process.WaitForExit(5000);
                if (!process.HasExited)
                {
                    error = "等待进程树退出超时";
                    return false;
                }
                if (process.ExitCode == 0)
                    return true;
                error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                    error = process.StandardOutput.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                    error = "taskkill 返回 " + process.ExitCode;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint sessionHandle,
            uint fileCount,
            string[] fileNames,
            uint applicationCount,
            RmUniqueProcess[] applications,
            uint serviceCount,
            string[] serviceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint sessionHandle,
            out uint processInfoNeeded,
            ref uint processInfo,
            [In, Out] RmProcessInfo[] affectedApplications,
            ref uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint sessionHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
            public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
            public string ServiceShortName;
            public uint ApplicationType;
            public uint AppStatus;
            public uint TssessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }
#else
        private static bool TerminateProcessTree(int processId, out string error)
        {
            error = "当前平台不支持进程树结束。";
            return false;
        }
#endif

        private static UPilotOccupyingProcess GetOrCreate(
            IDictionary<int, UPilotOccupyingProcess> processes,
            int processId,
            string processName,
            string commandLine)
        {
            if (processes.TryGetValue(processId, out var existing))
                return existing;

            var occupant = new UPilotOccupyingProcess
            {
                ProcessId = processId,
                ProcessName = string.IsNullOrWhiteSpace(processName) ? GetProcessName(processId) : processName,
                CommandLine = commandLine ?? "",
                CanTerminate = CanTerminate(processId, out var reason),
                CannotTerminateReason = reason,
            };
            processes.Add(processId, occupant);
            return occupant;
        }

        private static bool CanTerminate(int processId, out string reason)
        {
            reason = "";
            if (processId == Process.GetCurrentProcess().Id)
            {
                reason = "当前 Unity Editor 进程不能结束";
                return false;
            }
            if (processId <= 4)
            {
                reason = "系统受保护进程不能结束";
                return false;
            }
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    reason = "进程已退出";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "无法访问进程：" + ex.Message;
                return false;
            }
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName ?? "未知进程";
            }
            catch
            {
                return "未知进程";
            }
        }

        private static void AddResource(UPilotOccupyingProcess process, string resource)
        {
            if (!string.IsNullOrWhiteSpace(resource) &&
                !process.Resources.Contains(resource, StringComparer.OrdinalIgnoreCase))
                process.Resources.Add(resource);
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? ""; }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? "";
            return value.Substring(0, maxLength - 1) + "…";
        }
    }
}
