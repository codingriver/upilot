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
        private readonly string _settingsPath;
        private readonly string _legacyPrefsKey;
        private readonly Func<int, bool> _available;
        private readonly Action<string, string> _write;
        private readonly Func<string, Dictionary<string, int[]>> _readRegistration;
        private readonly Action<string, string, int, int> _writeRegistration;

        // Explicit settings paths isolate callers/tests from the real user settings and legacy key.
        internal UPilotPortRegistry(string settingsPath = null, Func<int, bool> available = null,
            Action<string, string> write = null, Func<string, Dictionary<string, int[]>> readRegistration = null,
            Action<string, string, int, int> writeRegistration = null, string legacyPrefsKey = null)
        {
            if (settingsPath != null && (string.IsNullOrWhiteSpace(settingsPath) || !Path.IsPathRooted(settingsPath)))
                throw new ArgumentException("用户配置文件必须使用绝对路径。", nameof(settingsPath));
            _settingsPath = Path.GetFullPath(settingsPath ?? UPilotUserSettings.ConfigPath);
            _legacyPrefsKey = legacyPrefsKey ?? (settingsPath == null ? PrefsKey : null);
            _available = available ?? UPilotPortAllocator.IsPortAvailable;
            _write = write ?? AtomicWrite;
            _readRegistration = readRegistration ?? UPilotUserSettings.ReadProjectPorts;
            _writeRegistration = writeRegistration ?? UPilotUserSettings.SetProjectPorts;
        }

        internal static UPilotPortRegistry ForUser() => new UPilotPortRegistry();

        internal static string NormalizeProject(string path) => UPilotUserSettings.NormalizeProjectPath(path);

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
            var projects = _readRegistration(_settingsPath) ?? throw new InvalidDataException($"端口登记读取结果为空：{_settingsPath}");
            var ports = new HashSet<int>();
            foreach (var pair in projects.Values)
            {
                ValidatePorts(pair[0], pair[1]);
                ports.Add(pair[0]);
                ports.Add(pair[1]);
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
                _writeRegistration(_settingsPath, project, ports.wsPort, ports.httpPort);
                if (_legacyPrefsKey != null) EditorPrefs.DeleteKey(_legacyPrefsKey);
                UPilotPortRegistration.Recovered("同步参考端口登记", _settingsPath);
            }
            catch (Exception ex) { UPilotPortRegistration.Report("同步参考端口登记", ex, _settingsPath); }
        }

        internal void Print()
        {
            try
            {
                var projects = _readRegistration(_settingsPath);
                var names = new List<string>(projects.Keys);
                names.Sort(StringComparer.Ordinal);
                var value = new StringBuilder();
                foreach (var project in names)
                {
                    var ports = projects[project];
                    if (value.Length != 0) value.Append('\n');
                    value.Append(project).Append(": WS=").Append(ports[0]).Append(", HTTP=").Append(ports[1]);
                }
                Debug.Log($"[UPilotPorts] {_settingsPath}\n" + (value.Length == 0 ? "暂无端口登记" : value.ToString()));
                UPilotPortRegistration.Recovered("打印参考端口登记", _settingsPath);
            }
            catch (Exception ex) { UPilotPortRegistration.Report("打印参考端口登记", ex, _settingsPath); }
        }

        internal void Clear()
        {
            try
            {
                UPilotUserSettings.ClearProjectPorts(_settingsPath);
                if (_legacyPrefsKey != null) EditorPrefs.DeleteKey(_legacyPrefsKey);
                Debug.Log($"[UPilotPorts] 已清除参考端口登记：{_settingsPath}；工程配置和运行中的服务不变。");
                UPilotPortRegistration.Recovered("清除参考端口登记", _settingsPath);
            }
            catch (Exception ex) { UPilotPortRegistration.Report("清除参考端口登记", ex, _settingsPath); }
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

        internal static void Report(string operation, Exception ex, string settingsPath = null)
        {
            settingsPath ??= UPilotUserSettings.ConfigPath;
            LastWarning = ex.Message;
            Logger.ReportFailure("Ports", $"[UPilotPorts] operation={operation}; project={UPilotProjectConfig.ProjectRoot}; " +
                $"registry={settingsPath}; 端口登记读写失败；工程配置保存与实际端口校验结果独立。", ex,
                scope: DiagnosticScope(operation, settingsPath));
        }

        internal static void Recovered(string operation, string settingsPath)
        {
            Logger.CloseDiagnostics(DiagnosticScope(operation, settingsPath));
            LastWarning = "";
        }

        private static string DiagnosticScope(string operation, string settingsPath) =>
            "ports|" + operation + "|" + settingsPath;
    }
}
