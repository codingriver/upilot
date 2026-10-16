// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMcpServerManager
    {
        // Stop ownership deliberately does not depend on startup history, parentage or log paths.
        internal sealed class ServerStopScope
        {
            internal string ProjectPath { get; }
            internal string PythonEntry { get; }
            internal string ServerExe { get; }
            internal int HttpPort { get; }
            internal int WsPort { get; }

            internal ServerStopScope(string project, string pythonEntry, string serverExe, int httpPort, int wsPort)
            {
                ProjectPath = StopAbsolutePath(project, nameof(project));
                PythonEntry = string.IsNullOrWhiteSpace(pythonEntry) ? "" : StopAbsolutePath(pythonEntry, nameof(pythonEntry));
                ServerExe = string.IsNullOrWhiteSpace(serverExe) ? "" : StopAbsolutePath(serverExe, nameof(serverExe));
                if (PythonEntry.Length == 0 && ServerExe.Length == 0)
                    throw new ArgumentException("停止范围缺少当前 Server 入口。");
                if (httpPort <= 0 || httpPort > 65535 || wsPort <= 0 || wsPort > 65535 || httpPort == wsPort)
                    throw new ArgumentException("停止范围的 HTTP/WS 端口无效。");
                HttpPort = httpPort;
                WsPort = wsPort;
            }

            internal bool Matches(string command, string actualExecutable) =>
                TryMatch(command, actualExecutable, out _);

            internal bool IsCommandCandidate(string command)
            {
                if (!TryStopTokens(command, out var tokens) || tokens.Count == 0) return false;
                var executable = Path.IsPathRooted(tokens[0]) ? tokens[0] : Path.Combine(ProjectPath, Path.GetFileName(tokens[0]));
                if (TryMatch(command, executable, out _)) return true;
                return ServerExe.Length > 0 && string.Equals(Path.GetFileName(tokens[0]), Path.GetFileName(ServerExe),
                    StopPathComparison) && TryMatch(command, ServerExe, out _);
            }

            internal bool HasMatchingPorts(string command) => TryStopTokens(command, out var tokens) &&
                TryStopPort(tokens, 1, "--http-port", HttpPort) && TryStopPort(tokens, 1, "--port", WsPort);

            internal bool TryMatch(string command, string actualExecutable, out string entry)
            {
                entry = "";
                if (!TryStopTokens(command, out var tokens) || tokens.Count == 0 ||
                    !TryStopAbsolutePath(actualExecutable, out var actual)) return false;
                var argumentsStart = 1;
                if (ServerExe.Length > 0 && StopSamePath(actual, ServerExe)) entry = ServerExe;
                else
                {
                    if (PythonEntry.Length == 0 || tokens.Count < 2 || !IsStopPythonImage(actual)) return false;
                    // argv[0] must describe the actual interpreter, not a shell containing a script string.
                    if (Path.IsPathRooted(tokens[0]))
                    {
                        if (!StopSamePath(tokens[0], actual)) return false;
                    }
                    else if (!string.Equals(Path.GetFileNameWithoutExtension(tokens[0]),
                        Path.GetFileNameWithoutExtension(actual), StopPathComparison)) return false;
                    var script = 1;
                    while (script < tokens.Count && tokens[script].StartsWith("-", StringComparison.Ordinal))
                    {
                        var option = tokens[script++];
                        if (option == "--") break;
                        if (option == "-W" || option == "-X")
                        {
                            if (script >= tokens.Count) return false;
                            script++;
                        }
                        else if (option.StartsWith("-W", StringComparison.Ordinal) || option.StartsWith("-X", StringComparison.Ordinal)) { }
                        else if (option != "-I" && option != "-B" && option != "-E" && option != "-s" && option != "-S" &&
                            option != "-u" && option != "-O" && option != "-OO" && option != "-q" && option != "-v" &&
                            option != "-b" && option != "-bb" && option != "-d" && option != "-x" && option != "-P") return false;
                    }
                    if (script >= tokens.Count || !StopSamePath(tokens[script], PythonEntry)) return false;
                    entry = PythonEntry;
                    argumentsStart = script + 1;
                }
                return TryStopPort(tokens, argumentsStart, "--http-port", HttpPort) &&
                    TryStopPort(tokens, argumentsStart, "--port", WsPort);
            }
        }

        internal ServerStopScope GetStopScope()
        {
            var project = UPilotProjectConfig.ProjectRoot;
            var pythonEntry = Path.IsPathRooted(_pythonEntryPath ?? "") ? _pythonEntryPath :
                Path.Combine(project, _pythonEntryPath ?? "");
            return new ServerStopScope(project, pythonEntry, UPilotProjectConfig.Current.runtime?.serverExePath,
                HttpPort, WsPort);
        }

        // This only rejects non-Python images. Authorization still requires the actual image
        // to agree with argv[0], the exact absolute script, and both unique configured ports.
        private static bool IsStopPythonImage(string executable) =>
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(executable),
                @"^(?:pythonw?(?:[0-9]+(?:\.[0-9]+)*)?|py)(?:\.exe)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static StringComparison StopPathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string StopAbsolutePath(string value, string name)
        {
            if (!TryStopAbsolutePath(value, out var result)) throw new ArgumentException("停止范围要求绝对路径。", name);
            return result;
        }

        private static bool TryStopAbsolutePath(string value, out string result)
        {
            result = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { if (!Path.IsPathRooted(value)) return false; result = Path.GetFullPath(value); return true; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static bool StopSamePath(string first, string second) =>
            TryStopAbsolutePath(first, out var left) && TryStopAbsolutePath(second, out var right) &&
            string.Equals(left, right, StopPathComparison);

        private static bool TryStopPort(IReadOnlyList<string> tokens, int start, string name, int expected)
        {
            var found = false;
            for (var i = start; i < tokens.Count; i++)
            {
                string value;
                if (tokens[i] == name)
                {
                    if (++i >= tokens.Count) return false;
                    value = tokens[i];
                }
                else if (tokens[i].StartsWith(name + "=", StringComparison.Ordinal)) value = tokens[i].Substring(name.Length + 1);
                else continue;
                if (found || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port != expected)
                    return false;
                found = true;
            }
            return found;
        }

        // Windows argv quoting, including backslashes immediately preceding quotes.
        private static bool TryStopTokens(string command, out List<string> tokens)
        {
            tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(command) || command.IndexOf('\0') >= 0) return false;
            var position = 0;
            while (position < command.Length)
            {
                while (position < command.Length && char.IsWhiteSpace(command[position])) position++;
                if (position >= command.Length) break;
                var token = new StringBuilder();
                var quoted = false;
                while (position < command.Length && (quoted || !char.IsWhiteSpace(command[position])))
                {
                    var slashes = 0;
                    while (position < command.Length && command[position] == '\\') { slashes++; position++; }
                    if (position < command.Length && command[position] == '"')
                    {
                        token.Append('\\', slashes / 2);
                        if ((slashes & 1) != 0) token.Append('"');
                        else if (quoted && position + 1 < command.Length && command[position + 1] == '"')
                        { token.Append('"'); position++; }
                        else quoted = !quoted;
                        position++;
                    }
                    else
                    {
                        token.Append('\\', slashes);
                        if (position < command.Length && (quoted || !char.IsWhiteSpace(command[position]))) token.Append(command[position++]);
                    }
                }
                if (quoted) return false;
                tokens.Add(token.ToString());
            }
            return tokens.Count > 0;
        }

        internal sealed class ServerStopTarget : IDisposable
        {
            private readonly object _gate = new object();
            private StopProcessHandle _handle;
            internal int ProcessId { get; }
            internal string ExecutablePath { get; }
            internal string EntryPath { get; set; } = "";
            internal long HandleValue { get { lock (_gate) return _handle == null || _handle.IsClosed ? 0 : _handle.DangerousGetHandle().ToInt64(); } }

            private ServerStopTarget(int processId, StopProcessHandle handle, string executable)
            { ProcessId = processId; _handle = handle; ExecutablePath = executable; }

            internal static ServerStopTarget Open(int processId)
            {
#if UNITY_EDITOR_WIN
                if (processId <= 4 || processId == Process.GetCurrentProcess().Id)
                    throw new InvalidOperationException("当前 Unity 或系统进程不是可关闭 Server 目标。");
                var raw = StopOpenProcess(0x00100000 | 0x1000 | 0x0001, false, processId);
                if (raw == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 87) return null; // The candidate already exited.
                    throw new Win32Exception(error, "无法为 Server 停止目标打开 OS 句柄；PID=" + processId);
                }
                var handle = new StopProcessHandle(raw);
                try
                {
                    if (StopHandleExited(handle)) { handle.Dispose(); return null; }
                    string executable;
                    try { executable = StopImagePath(handle, processId); }
                    catch (Win32Exception) when (StopHandleExited(handle)) { handle.Dispose(); return null; }
                    return new ServerStopTarget(processId, handle, executable);
                }
                catch { handle.Dispose(); throw; }
#else
                throw new PlatformNotSupportedException("当前平台尚未实现 Server 的 OS 句柄终止与退出观察。");
#endif
            }

            internal static ServerStopTarget RestoreHandle(long raw, int expectedPid)
            {
#if UNITY_EDITOR_WIN
                if (raw == 0 || expectedPid <= 4 || expectedPid == Process.GetCurrentProcess().Id)
                    throw new InvalidOperationException("保存的 Server OS 句柄或 PID 无效。");
                var value = new IntPtr(raw);
                var actualPid = StopGetProcessId(value);
                if (actualPid == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法接管保存的 Server OS 句柄。");
                if (actualPid != expectedPid) throw new InvalidOperationException("保存的 Server OS 句柄与预期 PID 不符。");
                var handle = new StopProcessHandle(value);
                try
                {
                    string executable;
                    if (StopHandleExited(handle)) executable = "";
                    else
                    {
                        try { executable = StopImagePath(handle, expectedPid); }
                        catch (Win32Exception) when (StopHandleExited(handle)) { executable = ""; }
                    }
                    return new ServerStopTarget(expectedPid, handle, executable);
                }
                catch { handle.Dispose(); throw; }
#else
                throw new PlatformNotSupportedException("当前平台尚未实现 Server OS 句柄的 Domain Reload 转移。");
#endif
            }

            internal bool HasExited
            {
                get { lock (_gate) { RequireHandle(); return StopHandleExited(_handle); } }
            }

            internal void Terminate()
            {
                lock (_gate)
                {
                    RequireHandle();
                    if (StopHandleExited(_handle)) return;
                    if (!StopTerminateProcess(_handle, 1))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (!StopHandleExited(_handle))
                            throw new Win32Exception(error, "通过持有的 OS 句柄终止 Server 失败；PID=" + ProcessId +
                                "；Win32=" + error + "；入口=" + EntryPath);
                    }
                }
            }

            internal long DetachHandle()
            {
                lock (_gate)
                {
                    RequireHandle();
                    var result = _handle.DangerousGetHandle().ToInt64();
                    _handle.SetHandleAsInvalid();
                    _handle.Dispose();
                    _handle = null;
                    return result;
                }
            }

            private void RequireHandle()
            {
                if (_handle == null || _handle.IsClosed || _handle.IsInvalid) throw new ObjectDisposedException(nameof(ServerStopTarget));
            }

            public void Dispose() { lock (_gate) { _handle?.Dispose(); _handle = null; } }
        }

        internal sealed class ServerStopDiscovery : IDisposable
        {
            internal readonly List<ServerStopTarget> Targets = new List<ServerStopTarget>();
            internal Dictionary<int, List<int>> ListeningPorts;
            internal int[] UnmatchedPortOwners = Array.Empty<int>();
            internal int[] ExcludedProcessIds = Array.Empty<int>();
            public void Dispose() { foreach (var target in Targets) target.Dispose(); Targets.Clear(); }
        }

        internal static ServerStopDiscovery DiscoverStopTargets(ServerStopScope scope, IEnumerable<int> knownPids,
            long deadlineUtcMs, int excludedForeignProjectPid = 0, Action beforeFinalPortSnapshot = null)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
#if UNITY_EDITOR_WIN
            var deadline = Math.Min(deadlineUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000);
            StopRemainingMs(deadline, "候选发现");
            var result = new ServerStopDiscovery();
            var opened = new List<ServerStopTarget>();
            void VerifyOpenedTargets(IReadOnlyCollection<ServerStopTarget> targets)
            {
                if (targets.Count == 0) return;
                var clock = Stopwatch.StartNew();
                var commands = QueryCandidateCommandLines(targets.Select(target => target.ProcessId), clock,
                    StopRemainingMs(deadline, "读取目标命令行"), out _);
                foreach (var target in targets)
                {
                    StopRemainingMs(deadline, "核验当前入口和双端口");
                    if (target.HasExited) continue;
                    if (!commands.TryGetValue(target.ProcessId, out var command) || !IsUsableCommandLine(command))
                        throw new InvalidOperationException("Server 停止候选的命令行不可读；PID=" + target.ProcessId);
                    if (scope.TryMatch(command, target.ExecutablePath, out var entry))
                    {
                        target.EntryPath = entry;
                        result.Targets.Add(target);
                    }
                }
            }
            try
            {
                result.ListeningPorts = SafeGetListeningPortsByPid(out var portsKnown, deadline);
                if (!portsKnown) throw new InvalidOperationException("Server 停止前监听端口归属查询失败。");
                var owners = result.ListeningPorts.Where(item => item.Value.Contains(scope.HttpPort) || item.Value.Contains(scope.WsPort))
                    .Select(item => item.Key).ToHashSet();
                var candidates = new HashSet<int>((knownPids ?? Array.Empty<int>()).Where(pid => pid > 0));
                candidates.UnionWith(owners);
                var preliminary = FindStopCommandCandidates(scope, deadline);
                var missing = candidates.Where(pid => !preliminary.ContainsKey(pid)).ToArray();
                if (missing.Length > 0)
                {
                    var initialClock = Stopwatch.StartNew();
                    foreach (var item in QueryCandidateCommandLines(missing, initialClock,
                        StopRemainingMs(deadline, "读取已知目标和端口候选"), out _)) preliminary[item.Key] = item.Value;
                }
                candidates.UnionWith(preliminary.Where(item => scope.IsCommandCandidate(item.Value)).Select(item => item.Key));
                if (candidates.Count > 128) throw new InvalidOperationException("Server 停止候选超过 128 个，未关闭任何进程。");
                if (excludedForeignProjectPid > 0 && result.ListeningPorts.TryGetValue(excludedForeignProjectPid, out var foreignPorts) &&
                    foreignPorts.Contains(scope.HttpPort) && candidates.Remove(excludedForeignProjectPid))
                    result.ExcludedProcessIds = new[] { excludedForeignProjectPid };
                foreach (var pid in candidates.OrderBy(value => value))
                {
                    StopRemainingMs(deadline, "打开目标句柄");
                    var readable = preliminary.TryGetValue(pid, out var beforeCommand) && IsUsableCommandLine(beforeCommand);
                    if (readable && (!scope.HasMatchingPorts(beforeCommand) ||
                        (!owners.Contains(pid) && !scope.IsCommandCandidate(beforeCommand)))) continue;
                    if (!readable && !owners.Contains(pid)) continue; // A stale PID is only a hint, never an authorization.
                    var target = ServerStopTarget.Open(pid);
                    if (target == null) continue;
                    opened.Add(target);
                    if (!readable) throw new InvalidOperationException("Server 目标端口拥有者的命令行不可读；PID=" + pid);
                }
                VerifyOpenedTargets(opened);
                // This callback is only a deterministic test seam for a listener appearing after discovery.
                beforeFinalPortSnapshot?.Invoke();
                var inspectedOwners = new HashSet<int>(owners);
                while (true)
                {
                    result.ListeningPorts = SafeGetListeningPortsByPid(out portsKnown, deadline);
                    if (!portsKnown) throw new InvalidOperationException("Server 停止候选核验后的监听端口归属查询失败。");
                    var newOwners = result.ListeningPorts.Where(item => item.Value.Contains(scope.HttpPort) || item.Value.Contains(scope.WsPort))
                        .Select(item => item.Key).Where(pid => !inspectedOwners.Contains(pid) &&
                            !result.Targets.Any(target => target.ProcessId == pid && !target.HasExited)).OrderBy(pid => pid).ToArray();
                    if (newOwners.Length == 0) break;
                    inspectedOwners.UnionWith(newOwners);
                    candidates.UnionWith(newOwners);
                    if (candidates.Count > 128) throw new InvalidOperationException("Server 停止候选超过 128 个，未关闭任何进程。");
                    var newClock = Stopwatch.StartNew();
                    var newCommands = QueryCandidateCommandLines(newOwners, newClock,
                        StopRemainingMs(deadline, "读取新增监听进程命令行"), out _);
                    var newTargets = new List<ServerStopTarget>();
                    foreach (var pid in newOwners)
                    {
                        StopRemainingMs(deadline, "打开新增监听进程句柄");
                        var readable = newCommands.TryGetValue(pid, out var command) && IsUsableCommandLine(command);
                        if (readable && !scope.HasMatchingPorts(command)) continue;
                        var target = ServerStopTarget.Open(pid);
                        if (target == null) continue;
                        opened.Add(target);
                        if (!readable) throw new InvalidOperationException("Server 新增端口拥有者的命令行不可读；PID=" + pid);
                        newTargets.Add(target);
                    }
                    VerifyOpenedTargets(newTargets);
                    // Recheck ownership after validation, retaining this operation's deadline and candidate limit.
                }
                result.UnmatchedPortOwners = result.ListeningPorts.Where(item => item.Value.Contains(scope.HttpPort) || item.Value.Contains(scope.WsPort))
                    .Select(item => item.Key).Where(pid => !result.Targets.Any(target => target.ProcessId == pid)).OrderBy(pid => pid).ToArray();
                foreach (var target in opened) if (!result.Targets.Contains(target)) target.Dispose();
                return result;
            }
            catch
            {
                foreach (var target in opened) target.Dispose();
                result.Targets.Clear();
                throw;
            }
#else
            throw new PlatformNotSupportedException("当前平台尚未实现 Server 停止所需的监听端口和命令行查询。");
#endif
        }

        private static int StopRemainingMs(long deadline, string stage)
        {
            var remaining = deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remaining <= 0) throw new TimeoutException("Server 停止原定期限已到；阶段=" + stage);
            return (int)Math.Min(int.MaxValue, remaining);
        }

        private static Dictionary<int, string> FindStopCommandCandidates(ServerStopScope scope, long deadline)
        {
#if UNITY_EDITOR_WIN
            // This is only a bounded candidate filter. Exact entry/ports are checked on held targets.
            string Literal(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
            string Like(string value) => Literal(value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]"));
            var entries = new List<string>();
            if (scope.PythonEntry.Length > 0)
                entries.Add("CommandLine like '%" + Like(Path.GetFileName(scope.PythonEntry)) + "%'");
            if (scope.ServerExe.Length > 0) entries.Add("ExecutablePath='" + Literal(scope.ServerExe) + "'");
            var filter = "(" + string.Join(" or ", entries) + ") and CommandLine like '%--http-port%' and CommandLine like '%--port%'";
            var info = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "process where \"" + filter + "\" get ProcessId,CommandLine /format:list",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var query = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Server 停止候选查询。");
            var helperHandle = query.Handle;
            try
            {
                var stdout = query.StandardOutput.ReadToEndAsync();
                var stderr = query.StandardError.ReadToEndAsync();
                if (!query.WaitForExit(StopRemainingMs(deadline, "发现同入口候选")) ||
                    !stdout.Wait(StopRemainingMs(deadline, "读取候选结果")) || !stderr.Wait(StopRemainingMs(deadline, "读取查询诊断")))
                    throw new TimeoutException("Server 停止候选查询超过原定期限。");
                if (query.ExitCode != 0) throw new InvalidOperationException("Server 停止候选查询失败；退出码=" + query.ExitCode);
                if (stdout.Result.Length > MaxCandidateQueryOutputChars)
                    throw new InvalidOperationException("Server 停止候选查询输出超过安全上限。");
                var result = new HashSet<int>();
                foreach (var raw in stdout.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim().TrimStart('\ufeff');
                    if (!line.StartsWith("ProcessId=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(line.Substring("ProcessId=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                        throw new FormatException("Server 停止候选 PID 查询返回无效记录。");
                    result.Add(pid);
                }
                if (result.Count == 0) return new Dictionary<int, string>(); // A successful WMI query may report no instances.
                return ParseCandidateCommandLines(stdout.Result, result);
            }
            catch (Exception failure)
            {
                if (!query.HasExited && !StopTerminateRawProcess(helperHandle, 1))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!query.HasExited)
                        throw new AggregateException(failure, new Win32Exception(error, "停止候选查询失败，且无法关闭查询辅助进程。"));
                }
                throw;
            }
#else
            throw new PlatformNotSupportedException("当前平台尚未实现 Server 停止命令行候选发现。");
#endif
        }

        private sealed class StopProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal StopProcessHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() => StopCloseHandle(handle);
        }

        private static bool StopHandleExited(StopProcessHandle handle)
        {
            if (!StopGetExitCodeProcess(handle, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 Server OS 句柄退出状态失败。");
            var wait = StopWaitForSingleObject(handle, 0);
            if (wait == 0) return true;
            if (wait == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "观察 Server OS 句柄退出状态失败。");
        }

        private static string StopImagePath(StopProcessHandle handle, int pid)
        {
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            if (!StopQueryFullProcessImageName(handle, 0, path, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 Server OS 句柄实际镜像失败；PID=" + pid);
            return Path.GetFullPath(path.ToString());
        }

        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        private static extern IntPtr StopOpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StopCloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
        private static extern int StopGetProcessId(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StopQueryFullProcessImageName(StopProcessHandle handle, uint flags, StringBuilder image, ref int length);
        [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StopGetExitCodeProcess(StopProcessHandle handle, out uint exitCode);
        [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
        private static extern uint StopWaitForSingleObject(StopProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StopTerminateProcess(StopProcessHandle handle, uint exitCode);
        [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StopTerminateRawProcess(IntPtr handle, uint exitCode);
    }
}
