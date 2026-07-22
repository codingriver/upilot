// -----------------------------------------------------------------------
// UPilot Editor - project configuration.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class UPilotProjectConfigData
    {
        public int schemaVersion = 2;
        public UPilotMcpConfig mcp = new();
        public UPilotCacheConfig cache = new();
        public UPilotFeaturesConfig features = new();
        public UPilotRuntimeConfig runtime = new();
        public UPilotSafetyConfig safety = new();
        public UPilotUpdateConfig updates = new();
    }

    [Serializable]
    public sealed class UPilotMcpConfig
    {
        public string httpHost = "127.0.0.1";
        public int httpPort = 8011;
        public string wsHost = "127.0.0.1";
        public int wsPort = 8765;
    }

    [Serializable]
    public sealed class UPilotCacheConfig
    {
        public int contextStaleMs = 2000;
    }

    [Serializable]
    public sealed class UPilotFeaturesConfig
    {
        public UPilotFlowFeatureConfig flow = new();
    }

    [Serializable]
    public sealed class UPilotFlowFeatureConfig
    {
        public bool enabled;
    }

    [Serializable]
    public sealed class UPilotRuntimeConfig
    {
        public string mode = "python";
        public string pythonPath = "";
        public string serverExePath = "";
        public string serverVersion = "";
    }

    [Serializable]
    public sealed class UPilotSafetyConfig
    {
        public bool writeAccessApproved;
        public string writeAccessApprovedAtUtc = "";
    }

    [Serializable]
    public sealed class UPilotUpdateConfig
    {
        public string manifestUrl = "https://github.com/codingriver/upilot/releases/latest/download/upilot-release-manifest.json";
        public string channel = "release";
    }

    public static class UPilotProjectConfig
    {
        private static UPilotProjectConfigData _cached;

        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        public static string ConfigPath => Path.Combine(ProjectRoot, ".upilot", "config.json");

        public static UPilotProjectConfigData Current => _cached ??= Load();

        public static UPilotProjectConfigData Load()
        {
            var result = new UPilotProjectConfigData();
            if (!File.Exists(ConfigPath))
                return result;
            try
            {
                var parsed = JsonUtility.FromJson<UPilotProjectConfigData>(File.ReadAllText(ConfigPath));
                return parsed ?? result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UPilot] Failed to load {ConfigPath}: {ex.Message}");
                return result;
            }
        }

        public static void Reload()
        {
            _cached = Load();
        }

        public static void Save(UPilotProjectConfigData config)
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(config, true));
            _cached = config;
        }

        public static void ApproveProjectWriteAccess()
        {
            var config = Current;
            config.safety ??= new UPilotSafetyConfig();
            config.safety.writeAccessApproved = true;
            config.safety.writeAccessApprovedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            Save(config);
        }

        public static void RevokeProjectWriteAccess()
        {
            var config = Current;
            config.safety ??= new UPilotSafetyConfig();
            config.safety.writeAccessApproved = false;
            config.safety.writeAccessApprovedAtUtc = "";
            Save(config);
        }

        public static void ApplyEndpoints(UPilotBridge bridge)
        {
            var config = Current.mcp ?? new UPilotMcpConfig();
            bridge.ApplyProjectEndpoints(config.wsHost, config.wsPort, config.httpPort);
        }
    }
}
