// -----------------------------------------------------------------------
// UPilot Editor - project configuration.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
        public UPilotAiServiceMaintenanceConfig aiServiceMaintenance = new();
        public UPilotUpdateConfig updates = new();
        public UPilotAgentsConfig agents = new();
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
    public sealed class UPilotAiServiceMaintenanceConfig
    {
        public bool approved;
        public string approvedAtUtc = "";
        public string projectPath = "";
        public int restartTimeoutSeconds = 120;
    }

    [Serializable]
    public sealed class UPilotSafetyConfig
    {
        public const string UnsavedScenePolicyBlock = "block";
        public const string UnsavedScenePolicyAutoSave = "autoSave";
        public const string UnsavedScenePolicyIgnore = "ignore";

        public bool writeAccessApproved;
        public string writeAccessApprovedAtUtc = "";
        public string unsavedScenePolicy = UnsavedScenePolicyBlock;
        public string[] automationAuthorizationScopes = Array.Empty<string>();
        public string automationAuthorizationCatalogHash = "";
        public int automationAuthorizationScopeVersion;
        public string automationAuthorizationApprovedAtUtc = "";

        public static string NormalizeUnsavedScenePolicy(string value)
        {
            return string.Equals(value, UnsavedScenePolicyAutoSave, StringComparison.OrdinalIgnoreCase)
                ? UnsavedScenePolicyAutoSave
                : string.Equals(value, UnsavedScenePolicyIgnore, StringComparison.OrdinalIgnoreCase)
                    ? UnsavedScenePolicyIgnore
                    : UnsavedScenePolicyBlock;
        }
    }

    public sealed class UPilotAutomationAuthorizationScope
    {
        public string key, label, risk, tools, targetRequirement;
        public UPilotAutomationAuthorizationScope(string k, string l, string r, string t, string target)
        { key = k; label = l; risk = r; tools = t; targetRequirement = target; }
    }

    /// <summary>Finite UI mirror of the service catalog. New catalog versions cannot inherit select-all.</summary>
    public static class UPilotAutomationAuthorizationCatalog
    {
        public const int ScopeVersion = 1;
        private static readonly UPilotAutomationAuthorizationScope[] Entries =
        {
            new("editorModeTransition", "自动切换 EditMode / PlayMode", "改变编辑器运行状态；不会恢复旧模式。", "ensure_ready, playmode", "当前已验证项目和目标模式"),
            new("scenePolicyExecution", "执行已选未保存场景策略", "autoSave 写入，ignore 丢弃修改；block 永不覆盖。", "test, acceptance, scene.prepareForAutomation", "当前项目已加载场景"),
            new("captureForceStop", "强制停止无 ownerToken 的 Capture", "仅精确 sessionId，停止后保留证据。", "console_capture_stop", "当前项目 active sessionId"),
            new("captureAcceptanceClearance", "自动停止验收前阻塞的 Capture", "逐个处理，失败即停止验收。", "upilot_acceptance_run", "精确 active sessionId"),
            new("captureArtifactCleanup", "清理过期 Capture 产物", "仍需 dry-run 和 confirmToken。", "console_capture_cleanup", "预览列出的项目内目录"),
            new("destructiveProjectWrite", "项目写入授权", "仅已建模、当前项目写入。", "write-gated tools", "精确项目内路径"),
            new("configCsvApply", "确认配置 CSV 写入", "仍需 preview/confirmToken。", "config_csv_patch", "预览哈希与目标记录"),
            new("prefabPatchApply", "确认 Prefab patch", "仍需 preview/confirmToken。", "prefab_patch", "精确 Prefab/组件"),
            new("textureImporterApply", "确认 Texture Importer patch", "仍需 preview/confirmToken。", "texture_importer_patch", "精确资产"),
            new("snapshotBaselineUpdate", "确认 Snapshot baseline 更新", "仍需 preview/confirmToken。", "snapshot_baseline_update", "精确基线和哈希"),
            new("assetMoveDelete", "精确资产移动/删除", "仅当前项目已检查路径。", "asset tools", "精确源/目标路径"),
            new("editorWindowForceDiscard", "强制关闭窗口/丢弃草稿", "仅已识别 Unity 窗口。", "editor window", "精确 instanceId"),
            new("hangRestart", "声明的 Unity 挂起恢复或重启", "仍须精确 Editor 身份和诊断。", "hang/restart", "当前项目 Editor PID/身份"),
        };
        public static IReadOnlyList<UPilotAutomationAuthorizationScope> All => Entries;
        public static string Hash
        {
            get { var canonical = string.Join("|", Entries.Select(x => x.key)); using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant(); }
        }
        public static bool Has(UPilotSafetyConfig safety, string scope) => (safety?.automationAuthorizationScopes ?? Array.Empty<string>()).Contains(scope, StringComparer.Ordinal);
        public static bool IsFull(UPilotSafetyConfig safety) => safety != null && safety.automationAuthorizationCatalogHash == Hash && safety.automationAuthorizationScopeVersion == ScopeVersion && Entries.All(x => Has(safety, x.key));
        public static void SetAll(UPilotSafetyConfig safety, bool enabled)
        { safety.automationAuthorizationScopes = enabled ? Entries.Select(x => x.key).ToArray() : Array.Empty<string>(); safety.automationAuthorizationCatalogHash = enabled ? Hash : ""; safety.automationAuthorizationScopeVersion = enabled ? ScopeVersion : 0; safety.automationAuthorizationApprovedAtUtc = enabled ? DateTimeOffset.UtcNow.ToString("O") : ""; }
        public static void SetScope(UPilotSafetyConfig safety, string scope, bool enabled)
        { var values = new HashSet<string>(safety.automationAuthorizationScopes ?? Array.Empty<string>(), StringComparer.Ordinal); if (enabled) values.Add(scope); else values.Remove(scope); safety.automationAuthorizationScopes = values.OrderBy(x => x, StringComparer.Ordinal).ToArray(); safety.automationAuthorizationCatalogHash = ""; safety.automationAuthorizationScopeVersion = 0; safety.automationAuthorizationApprovedAtUtc = DateTimeOffset.UtcNow.ToString("O"); }
    }

    [Serializable]
    public sealed class UPilotUpdateConfig
    {
        public string manifestUrl = "";
        public string channel = "auto";
    }

    [Serializable]
    public sealed class UPilotAgentsConfig
    {
        public bool selectionInitialized;
        public string[] enabledClients = Array.Empty<string>();
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

        public static void Save(UPilotProjectConfigData config, bool updateEndpoints = false)
        {
            try
            {
                UPilotPortRegistry.ForUser().Commit(ProjectRoot, config, preserveExistingPorts: !updateEndpoints);
                _cached = config;
            }
            catch (Exception ex)
            {
                _cached = null;
                UPilotPortRegistration.Report("保存工程配置及端口预留", ex);
                throw;
            }
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
