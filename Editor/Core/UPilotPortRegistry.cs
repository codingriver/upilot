using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

    // A busy registry is not evidence of a bad port configuration or a listener conflict.
    internal sealed class UPilotPortRegistryBusyException : IOException
    {
        internal UPilotPortRegistryBusyException(string path, IOException cause)
            : base($"端口登记暂时繁忙：{path}。尚未完成登记校验，不代表端口冲突；请稍后重试。", cause) { }
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

        internal static UPilotPortRegistry ForUser(int lockTimeoutMs = 0)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                throw new IOException("无法取得当前系统用户目录。");
            return new UPilotPortRegistry(Path.Combine(profile, ".upilot"), lockTimeoutMs: lockTimeoutMs);
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
                catch (IOException ex) when (IsLockContention(ex))
                {
                    if (timer.ElapsedMilliseconds >= _lockTimeoutMs)
                        throw new UPilotPortRegistryBusyException(LockPath, ex);
                    Thread.Sleep(25);
                }
                catch (IOException ex)
                {
                    // Storage/path errors cannot be repaired by waiting for another lock owner.
                    throw new IOException($"打开端口登记锁失败：{LockPath}；错误码=0x{ex.HResult:X8}。", ex);
                }
            }
        }

        internal static bool IsLockContention(IOException ex)
        {
            var code = ex.HResult & 0xffff;
            return code == 32 || code == 33; // Sharing violation / lock violation (also mapped by Mono).
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
                    return $"参考登记中存在相同端口（不代表实际监听或占用）：{entry.projectPath}\n" +
                           $"参考重复端口：{string.Join(", ", new[] { ws, http }.Where(ports.Contains))}";
            }
            return "";
        }

        internal string Check(string project, int ws, int http)
        {
            ValidatePorts(ws, http);
            using var guard = AcquireLock();
            return Conflict(Read(), NormalizeProject(project), ws, http);
        }

        internal (int wsPort, int httpPort) Recommend(string project, int ws, int http, int attempts) =>
            UPilotPortAllocator.FindAvailablePair(ws, http, attempts, _available);

        // Only the actual project configuration and bind probes can block saving or startup.
        internal static UPilotProjectConfigData ValidateConfig(string project, UPilotProjectConfigData requested = null,
            bool requireAvailable = false, bool preserveExistingPorts = false, Func<int, bool> available = null)
        {
            project = NormalizeProject(project);
            var configPath = ConfigPath(project);
            var existing = File.Exists(configPath) ? ReadConfig(project) : null;
            var config = requested ?? existing ?? throw new IOException($"工程配置不存在：{configPath}");
            if (preserveExistingPorts && existing != null) config.mcp = existing.mcp;
            var ports = config.mcp ?? throw new InvalidDataException("工程缺少 mcp 配置。");
            ValidatePorts(ports.wsPort, ports.httpPort);
            var pairChanged = requested != null && existing != null &&
                              (ports.wsPort != existing.mcp.wsPort || ports.httpPort != existing.mcp.httpPort);
            available ??= UPilotPortAllocator.IsPortAvailable;
            foreach (var port in new[] { ports.wsPort, ports.httpPort })
            {
                var unchanged = existing != null && (existing.mcp.wsPort == port || existing.mcp.httpPort == port);
                if ((requireAvailable || pairChanged || !unchanged) && !available(port))
                    throw new IOException($"端口 {port} 当前不可绑定，可能被占用或受系统保留/权限限制；请在设置中确认推荐端口。");
            }
            return config;
        }

        internal void Sync(string project, bool requireAvailable = false) =>
            Commit(project, null, requireAvailable);

        internal void Commit(string project, UPilotProjectConfigData requested, bool requireAvailable = false,
            bool preserveExistingPorts = false)
        {
            project = NormalizeProject(project);
            var config = ValidateConfig(project, requested, requireAvailable, preserveExistingPorts, _available);
            var ports = config.mcp;
            var configPath = ConfigPath(project);
            // The reference registry must never prevent the authoritative config write.
            if (requested != null) _write(configPath, JsonUtility.ToJson(config, true));
            var verified = ReadConfig(project).mcp;
            if (verified.wsPort != ports.wsPort || verified.httpPort != ports.httpPort)
                throw new IOException($"工程端口在保存期间发生变化：{configPath}");
            try
            {
                using var guard = AcquireLock();
                var data = Read();
                var conflict = Conflict(data, project, ports.wsPort, ports.httpPort);
                if (conflict.Length > 0)
                    UPilotPortRegistration.Report("参考端口登记", new IOException(conflict));
                var entry = data.projects.Find(e => NormalizeProject(e.projectPath) == project);
                if (entry == null)
                {
                    entry = new UPilotPortReservation { projectPath = project };
                    data.projects.Add(entry);
                }
                entry.wsPort = ports.wsPort;
                entry.httpPort = ports.httpPort;
                entry.pendingPorts = Array.Empty<int>();
                entry.configSha256 = Hash(File.ReadAllText(configPath));
                entry.syncedAtUtc = DateTime.UtcNow.ToString("O");
                Write(data);
            }
            catch (Exception ex)
            {
                UPilotPortRegistration.Report("同步参考端口登记", ex);
            }
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

        internal static void OnEndpointsConfirmed() => LastError = "";

        internal static bool TrySyncCurrent(bool requireAvailable = false)
        {
            try
            {
                UPilotPortRegistry.ValidateConfig(UPilotProjectConfig.ProjectRoot, requireAvailable: requireAvailable);
                UPilotProjectConfig.Reload();
                UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                OnEndpointsConfirmed();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError($"[UPilotPorts] 工程端口校验失败：{ex}");
                return false;
            }
            try { UPilotPortRegistry.ForUser().Sync(UPilotProjectConfig.ProjectRoot); }
            catch (Exception ex) { Report("同步参考端口登记", ex); }
            return true;
        }

        internal static void Report(string operation, Exception ex)
        {
            if (LastError == ex.Message) return;
            LastError = ex.Message;
            Debug.LogWarning($"[UPilotPorts] operation={operation}; project={UPilotProjectConfig.ProjectRoot}; " +
                             $"registry=.upilot/ports.json; error={ex}\n" +
                             "端口登记数据仅供参考；登记读写失败不影响工程配置保存或实际端口校验。");
        }
    }
}
