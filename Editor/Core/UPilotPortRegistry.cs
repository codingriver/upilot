using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotPortReservation
    {
        public string projectPath;
        public int wsPort;
        public int httpPort;
        public int[] pendingPorts;
        public string configSha256;
        public string syncedAtUtc;
    }

    [Serializable]
    internal sealed class UPilotPortRegistryData
    {
        public int schemaVersion;
        public List<UPilotPortReservation> projects;
    }

    // The project config is authoritative; this index never rewrites another project.
    internal sealed class UPilotPortRegistry
    {
        internal readonly string RegistryPath;
        internal string LockPath => Path.Combine(Path.GetDirectoryName(RegistryPath), "ports.lock");
        private readonly Func<int, bool> _available;
        private readonly int _lockTimeoutMs;
        private readonly Action<string, string> _write;

        internal UPilotPortRegistry(string directory, Func<int, bool> available = null, int lockTimeoutMs = 1000,
            Action<string, string> write = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new IOException("无法取得用户端口登记目录，拒绝回退到临时目录。");
            RegistryPath = Path.Combine(Path.GetFullPath(directory), "ports.json");
            _available = available ?? UPilotPortAllocator.IsPortAvailable;
            _lockTimeoutMs = lockTimeoutMs;
            _write = write ?? AtomicWrite;
        }

        internal static UPilotPortRegistry ForUser()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                throw new IOException("无法取得当前系统用户目录。");
            return new UPilotPortRegistry(Path.Combine(profile, ".upilot"));
        }

        internal static string NormalizeProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new IOException("工程路径必须是绝对路径。");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.DirectorySeparatorChar == '\\' ? full.ToUpperInvariant() : full;
        }

        internal static string ConfigPath(string project) => Path.Combine(project, ".upilot", "config.json");

        internal static void ValidatePorts(int ws, int http)
        {
            if (ws < 1 || ws > 65535 || http < 1 || http > 65535 || ws == http)
                throw new InvalidDataException($"HTTP/WS 必须是不同的有效端口：WS={ws}, HTTP={http}。");
        }

        internal static UPilotProjectConfigData ReadConfig(string project)
        {
            var path = ConfigPath(project);
            // Missing fields must not silently become the default ports.
            var config = new UPilotProjectConfigData { mcp = new UPilotMcpConfig { wsPort = -1, httpPort = -1 } };
            JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
            if (config.mcp == null)
                throw new InvalidDataException($"工程缺少端口配置：{path}");
            ValidatePorts(config.mcp.wsPort, config.mcp.httpPort);
            return config;
        }

        private FileStream AcquireLock()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex)
                {
                    if (timer.ElapsedMilliseconds >= _lockTimeoutMs)
                        throw new IOException($"端口登记锁等待超时或不可访问：{LockPath}", ex);
                    Thread.Sleep(25);
                }
            }
        }

        private UPilotPortRegistryData Read()
        {
            if (!File.Exists(RegistryPath))
                return new UPilotPortRegistryData { schemaVersion = 1, projects = new List<UPilotPortReservation>() };
            var data = JsonUtility.FromJson<UPilotPortRegistryData>(File.ReadAllText(RegistryPath));
            if (data == null || data.schemaVersion != 1 || data.projects == null)
                throw new InvalidDataException($"端口登记表损坏或版本不受支持，原文件已保留：{RegistryPath}");
            var paths = new HashSet<string>();
            foreach (var entry in data.projects)
            {
                if (entry == null || !paths.Add(NormalizeProject(entry.projectPath)))
                    throw new InvalidDataException($"端口登记项为空或工程重复：{RegistryPath}");
                ValidatePorts(entry.wsPort, entry.httpPort);
                if (entry.pendingPorts == null || entry.pendingPorts.Any(p => p < 1 || p > 65535))
                    throw new InvalidDataException($"无效的待提交预留：{entry.projectPath}");
            }
            return data;
        }

        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            // On failure retain the candidate for diagnosis. Never delete the destination first.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                throw new IOException($"原子写入失败：source={temporary}, target={path}；临时文件已保留。", ex);
            }
        }

        private void Write(UPilotPortRegistryData data) => _write(RegistryPath, JsonUtility.ToJson(data, true));

        private static HashSet<int> ReservedPorts(UPilotPortReservation entry)
        {
            var ports = new HashSet<int>(entry.pendingPorts) { entry.wsPort, entry.httpPort };
            try
            {
                var config = ReadConfig(entry.projectPath).mcp;
                ports.Add(config.wsPort);
                ports.Add(config.httpPort);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                       ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // An offline disk, deleted project or unreadable config never expires its reservation.
            }
            return ports;
        }

        private static string Conflict(UPilotPortRegistryData data, string project, int ws, int http)
        {
            foreach (var entry in data.projects)
            {
                if (NormalizeProject(entry.projectPath) == project) continue;
                var ports = ReservedPorts(entry);
                if (ports.Contains(ws) || ports.Contains(http))
                    return $"端口已由工程预留（即使未启动也保留）：{entry.projectPath}\n" +
                           $"冲突端口：{string.Join(", ", new[] { ws, http }.Where(ports.Contains))}";
            }
            return "";
        }

        internal string Check(string project, int ws, int http)
        {
            ValidatePorts(ws, http);
            using var guard = AcquireLock();
            return Conflict(Read(), NormalizeProject(project), ws, http);
        }

        internal (int wsPort, int httpPort) Recommend(string project, int ws, int http, int attempts)
        {
            using var guard = AcquireLock();
            var data = Read();
            project = NormalizeProject(project);
            if (ws == http) http = http < 65535 ? http + 1 : http - 1;
            var reserved = new HashSet<int>(data.projects
                .Where(e => NormalizeProject(e.projectPath) != project).SelectMany(ReservedPorts));
            for (var i = 0; i < attempts && ws <= 65535 && http <= 65535; i++, ws++, http++)
            {
                if (ws > 0 && http > 0 && ws != http && !reserved.Contains(ws) && !reserved.Contains(http) &&
                    _available(ws) && _available(http))
                    return (ws, http);
            }
            throw new IOException("未找到未预留且可监听的 HTTP/WS 端口对，请手动指定其他端口范围。");
        }

        internal void Sync(string project, bool requireAvailable = false)
        {
            Commit(project, null, requireAvailable);
        }

        internal void Commit(string project, UPilotProjectConfigData requested, bool requireAvailable = false,
            bool preserveExistingPorts = false)
        {
            using var guard = AcquireLock();
            project = NormalizeProject(project);
            var data = Read();
            var configPath = ConfigPath(project);
            var existing = File.Exists(configPath) ? ReadConfig(project) : null;
            var config = requested ?? existing ?? throw new IOException($"工程配置不存在：{configPath}");
            if (preserveExistingPorts && existing != null) config.mcp = existing.mcp;
            var ports = config.mcp ?? throw new InvalidDataException("工程缺少 mcp 配置。");
            ValidatePorts(ports.wsPort, ports.httpPort);
            var conflict = Conflict(data, project, ports.wsPort, ports.httpPort);
            if (conflict.Length > 0) throw new IOException(conflict);
            var pairChanged = requested != null && existing != null &&
                              (ports.wsPort != existing.mcp.wsPort || ports.httpPort != existing.mcp.httpPort);
            foreach (var port in new[] { ports.wsPort, ports.httpPort })
            {
                var unchanged = existing != null && (existing.mcp.wsPort == port || existing.mcp.httpPort == port);
                if ((requireAvailable || pairChanged || !unchanged) && !_available(port))
                    throw new IOException($"端口 {port} 已被系统进程占用，请选择推荐端口。");
            }

            var entry = data.projects.Find(e => NormalizeProject(e.projectPath) == project);
            if (entry == null)
            {
                entry = new UPilotPortReservation { projectPath = project, pendingPorts = Array.Empty<int>() };
                data.projects.Add(entry);
            }
            var held = new HashSet<int>(entry.pendingPorts);
            if (entry.wsPort > 0) held.Add(entry.wsPort);
            if (entry.httpPort > 0) held.Add(entry.httpPort);
            if (existing != null) { held.Add(existing.mcp.wsPort); held.Add(existing.mcp.httpPort); }
            entry.wsPort = ports.wsPort;
            entry.httpPort = ports.httpPort;
            entry.pendingPorts = held.ToArray();
            // Write-ahead reservation protects both pairs across a crash between the two files.
            Write(data);
            if (requested != null) _write(configPath, JsonUtility.ToJson(config, true));
            var verified = ReadConfig(project).mcp;
            if (verified.wsPort != ports.wsPort || verified.httpPort != ports.httpPort)
                throw new IOException($"工程端口在登记期间发生变化，待提交预留已保留：{configPath}");
            entry.configSha256 = Hash(File.ReadAllText(configPath));
            entry.syncedAtUtc = DateTime.UtcNow.ToString("O");
            entry.pendingPorts = Array.Empty<int>();
            Write(data);
        }

        internal List<UPilotPortReservation> Snapshot()
        {
            using var guard = AcquireLock();
            return Read().projects;
        }

        internal static string Hash(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        internal void Release(UPilotPortReservation expected, string currentProject)
        {
            using var guard = AcquireLock();
            var data = Read();
            var path = NormalizeProject(expected.projectPath);
            if (path == NormalizeProject(currentProject))
                throw new IOException("不能释放当前工程的预留。请在其他工程中确认释放。");
            var entry = data.projects.Find(e => NormalizeProject(e.projectPath) == path);
            if (entry == null || JsonUtility.ToJson(entry) != JsonUtility.ToJson(expected))
                throw new IOException("登记项已变化，请刷新后重新确认。");
            if (ReservedPorts(entry).Any(p => !_available(p)))
                throw new IOException($"工程预留端口仍被监听，拒绝释放：{entry.projectPath}");
            data.projects.Remove(entry);
            Write(data);
        }
    }

    internal static class UPilotPortRegistration
    {
        internal static string LastError { get; set; } = "";
        private static readonly HashSet<string> PendingDialogs = new HashSet<string>();
        internal static Action<string> ShowErrorDialog = message =>
            EditorUtility.DisplayDialog("UPilot 端口配置失败",
                message + "\n\n未自动更改冲突工程。详情请查看 Console。", "确定");

        internal static bool TrySyncCurrent(bool requireAvailable = false)
        {
            try
            {
                UPilotPortRegistry.ForUser().Sync(UPilotProjectConfig.ProjectRoot, requireAvailable);
                UPilotProjectConfig.Reload();
                UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                if (requireAvailable)
                {
                    try
                    {
                        var ports = UPilotPortRegistry.ReadConfig(UPilotProjectConfig.ProjectRoot).mcp;
                        var pair = UPilotPortAllocator.FindAvailablePair(ports.wsPort, ports.httpPort);
                        ex = new IOException(ex.Message + $"\n当前 WS {ports.wsPort} / HTTP {ports.httpPort}。" +
                                             $"\n建议 WS {pair.wsPort} / HTTP {pair.httpPort}；请在设置中确认修改。", ex);
                    }
                    catch (Exception recommendationError)
                    {
                        ex = new IOException(ex.Message + "\n无法推荐端口：" + recommendationError.Message, ex);
                    }
                }
                Report("同步/启动前校验", ex);
                return false;
            }
        }

        internal static void Report(string operation, Exception ex)
        {
            LastError = ex.Message;
            Debug.LogError($"[UPilotPorts] operation={operation}; project={UPilotProjectConfig.ProjectRoot}; " +
                           $"config={UPilotProjectConfig.ConfigPath}; userProfile={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}; " +
                           $"registry=.upilot/ports.json; error={ex}");
            if (Application.isBatchMode || !PendingDialogs.Add(ex.Message)) return;
            EditorApplication.CallbackFunction show = null;
            show = () =>
            {
                EditorApplication.update -= show;
                try { ShowErrorDialog(ex.Message); }
                finally { PendingDialogs.Remove(ex.Message); }
            };
            EditorApplication.update += show;
        }
    }
}
