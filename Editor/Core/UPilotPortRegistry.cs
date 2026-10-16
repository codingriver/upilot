using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // Reference data only: actual project config and bind probes remain authoritative.
    internal sealed class UPilotPortRegistry
    {
        internal const string PrefsKey = "upilot_allocate_ports";
        private readonly string _prefsKey;
        private readonly Func<int, bool> _available;
        private readonly Action<string, string> _write;
        private readonly Func<string, string> _readRegistration;
        private readonly Action<string, string> _writeRegistration;

        // Alternate key and delegates are small test seams; production always uses PrefsKey.
        internal UPilotPortRegistry(string prefsKey = PrefsKey, Func<int, bool> available = null,
            Action<string, string> write = null, Func<string, string> readRegistration = null,
            Action<string, string> writeRegistration = null)
        {
            if (string.IsNullOrWhiteSpace(prefsKey)) throw new ArgumentException("登记 key 不能为空。", nameof(prefsKey));
            _prefsKey = prefsKey;
            _available = available ?? UPilotPortAllocator.IsPortAvailable;
            _write = write ?? AtomicWrite;
            _readRegistration = readRegistration ?? (key => EditorPrefs.GetString(key, ""));
            _writeRegistration = writeRegistration ?? EditorPrefs.SetString;
        }

        internal static UPilotPortRegistry ForUser() => new UPilotPortRegistry();

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

        internal (int wsPort, int httpPort) Recommend(string project, int ws, int http, int attempts,
            IEnumerable<int> excluded = null)
        {
            var registered = ReadRegisteredPorts();
            if (excluded != null) registered.UnionWith(excluded);
            return UPilotPortAllocator.FindAvailablePair(ws, http, attempts, _available, registered, wrap: true);
        }

        // Registration remains advisory for manual configuration, but recommendations must avoid it.
        private HashSet<int> ReadRegisteredPorts()
        {
            var value = _readRegistration(_prefsKey) ?? throw new InvalidDataException("端口登记读取结果为空。");
            var ports = new HashSet<int>();
            foreach (var line in value.Split('\n'))
            {
                var entry = line.Trim();
                if (entry.Length == 0) continue;
                var fields = entry.Split(':');
                if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0]) ||
                    !int.TryParse(fields[1], out var ws) || !int.TryParse(fields[2], out var http))
                    throw new InvalidDataException($"端口登记格式损坏：registry={_prefsKey}；请检查或清除端口登记。");
                ValidatePorts(ws, http);
                ports.Add(ws);
                ports.Add(http);
            }
            return ports;
        }

        internal void ValidateRecommendation(int ws, int http)
        {
            ValidatePorts(ws, http);
            var registered = ReadRegisteredPorts();
            foreach (var port in new[] { ws, http })
            {
                if (registered.Contains(port))
                    throw new IOException($"推荐端口 {port} 已被登记，请重新推荐。");
                if (!_available(port))
                    throw new IOException($"推荐端口 {port} 当前不可绑定，请重新推荐。");
            }
        }

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
            if (requested != null) _write(configPath, JsonUtility.ToJson(config, true));
            var verified = ReadConfig(project).mcp;
            if (verified.wsPort != ports.wsPort || verified.httpPort != ports.httpPort)
                throw new IOException($"工程端口在保存期间发生变化：{configPath}");
            try
            {
                var before = _readRegistration(_prefsKey);
                var after = UpdateEntries(before, UPilotBridge.ComputeProjectPathHash(project), ports.wsPort, ports.httpPort);
                if (before != after) _writeRegistration(_prefsKey, after);
                UPilotPortRegistration.LastWarning = "";
            }
            catch (Exception ex) { UPilotPortRegistration.Report("同步参考端口登记", ex); }
        }

        internal static string UpdateEntries(string value, string projectHash, int ws, int http)
        {
            ValidatePorts(ws, http);
            var prefix = projectHash + ":";
            var replacement = prefix + ws + ":" + http;
            var entries = new List<string>();
            var replaced = false;
            foreach (var entry in value.Split('\n'))
            {
                if (entry.Length == 0) continue;
                if (entry.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (!replaced) entries.Add(replacement);
                    replaced = true;
                }
                else entries.Add(entry);
            }
            if (!replaced) entries.Add(replacement);
            return string.Join("\n", entries);
        }

        internal void Print()
        {
            try
            {
                var value = _readRegistration(_prefsKey);
                Debug.Log($"[UPilotPorts] {_prefsKey}\n" + (value.Length == 0 ? "暂无端口登记" : value));
            }
            catch (Exception ex) { UPilotPortRegistration.Report("打印参考端口登记", ex); }
        }

        internal void Clear()
        {
            try
            {
                EditorPrefs.DeleteKey(_prefsKey);
                Debug.Log($"[UPilotPorts] 已清除参考端口登记：{_prefsKey}；工程配置和运行中的服务不变。");
            }
            catch (Exception ex) { UPilotPortRegistration.Report("清除参考端口登记", ex); }
        }
    }

    internal static class UPilotPortRegistration
    {
        internal static string LastError { get; set; } = "";
        internal static string LastWarning { get; set; } = "";

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
            if (LastWarning == ex.Message) return;
            LastWarning = ex.Message;
            Debug.LogWarning($"[UPilotPorts] operation={operation}; project={UPilotProjectConfig.ProjectRoot}; " +
                             $"registry={UPilotPortRegistry.PrefsKey}; error={ex}\n" +
                             "端口登记数据仅供参考；登记读写失败不影响工程配置保存或实际端口校验。");
        }
    }
}
