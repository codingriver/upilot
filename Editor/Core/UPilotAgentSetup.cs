// -----------------------------------------------------------------------
// upilot Editor — Agent discovery and MCP client setup helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public readonly struct AgentMcpConfigStatus
    {
        public AgentMcpConfigStatus(
            string clientName,
            string configPath,
            bool fileExists,
            bool hasUPilotEntry,
            bool usesCurrentUrl,
            string errorMessage = "")
        {
            ClientName = clientName;
            ConfigPath = configPath;
            FileExists = fileExists;
            HasUPilotEntry = hasUPilotEntry;
            UsesCurrentUrl = usesCurrentUrl;
            ErrorMessage = errorMessage ?? "";
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public bool FileExists { get; }
        public bool HasUPilotEntry { get; }
        public bool UsesCurrentUrl { get; }
        public string ErrorMessage { get; }
        public bool IsConfigured => FileExists && HasUPilotEntry && UsesCurrentUrl && string.IsNullOrEmpty(ErrorMessage);

        public string StateText
        {
            get
            {
                if (!string.IsNullOrEmpty(ErrorMessage)) return "读取失败";
                if (!FileExists) return "未配置";
                if (!HasUPilotEntry) return "缺少 UPilot 配置";
                if (!UsesCurrentUrl) return "端口已变化，需更新";
                return "已配置";
            }
        }
    }

    public enum AgentRuleConfigState
    {
        Missing,
        Current,
        UpdateAvailable,
        Customized,
        Error,
    }

    public readonly struct AgentRuleConfigStatus
    {
        public AgentRuleConfigStatus(
            string clientName,
            string configPath,
            AgentRuleConfigState state,
            string errorMessage = "")
        {
            ClientName = clientName;
            ConfigPath = configPath;
            State = state;
            ErrorMessage = errorMessage ?? "";
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public AgentRuleConfigState State { get; }
        public string ErrorMessage { get; }
        public bool IsCurrent => State == AgentRuleConfigState.Current;
        public bool HasLocalCustomization => State == AgentRuleConfigState.Customized;

        public string StateText
        {
            get
            {
                if (State == AgentRuleConfigState.Error) return "读取失败";
                if (State == AgentRuleConfigState.Missing)
                    return ClientName == "Codex" ? "未安装" : "未同步";
                if (State == AgentRuleConfigState.UpdateAvailable) return "有新版本";
                if (State == AgentRuleConfigState.Customized)
                    return "需要确认";
                return ClientName == "Codex" ? "已安装" : "已同步";
            }
        }
    }

    [InitializeOnLoad]
    public static class UPilotAgentSetup
    {
        private const string PackageName = "io.github.codingriver.upilot";
        private const string SkillName = "upilot-unity-mcp";
        private const string AgentRulesTemplateFileName = "AGENTS.md.template";
        private const string AutoSetupKeyPrefix = "CodingRiver.UPilot.AgentSetup.AutoRulesWritten.";
        private const int AgentRulesTemplateVersion = 22;
        private const int SkillInstallTemplateVersion = 17;
        private const string SkillInstallMetadataFileName = ".upilot-install.json";
        private const string ManagedBlockStart = "<!-- upilot:start -->";
        private const string ManagedBlockEnd = "<!-- upilot:end -->";

        public static string McpUrl => GetMcpUrl(UPilotBridge.Instance.HttpPort);
        public static string HealthUrl => GetHealthUrl(UPilotBridge.Instance.HttpPort);

        public static string GetMcpUrl(int httpPort) => $"http://127.0.0.1:{httpPort}/mcp";
        public static string GetHealthUrl(int httpPort) => $"http://127.0.0.1:{httpPort}/health";

        public static AgentMcpConfigStatus[] GetMcpConfigStatuses()
        {
            var projectRoot = GetProjectRoot();
            return new[]
            {
                InspectTomlConfig("Codex", Path.Combine(projectRoot, ".codex", "config.toml")),
                InspectJsonConfig("Claude Code", Path.Combine(projectRoot, ".mcp.json")),
                InspectJsonConfig("Cursor", Path.Combine(projectRoot, ".cursor", "mcp.json")),
            };
        }

        public static AgentRuleConfigStatus[] GetRuleConfigStatuses()
        {
            var projectRoot = GetProjectRoot();
            return new[]
            {
                InspectCodexRuleConfig(projectRoot),
                InspectClaudeRuleConfig(projectRoot),
                InspectCursorRuleConfig(projectRoot),
            };
        }

        static UPilotAgentSetup()
        {
            EditorApplication.delayCall += EnsureAgentRulesOnce;
        }

        [MenuItem("UPilot/Advanced/Agent Setup/Write Agent Rules", false, 310)]
        public static void MenuWriteAgentRules()
        {
            var result = WriteAgentRules(overwriteExisting: false);
            ReportResult("Agent rules", result);
        }

        [MenuItem("UPilot/Advanced/Agent Setup/Write Codex MCP Config", false, 320)]
        public static void MenuWriteCodexMcpConfig()
        {
            var result = WriteCodexMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Codex MCP config", result);
        }

        [MenuItem("UPilot/Advanced/Agent Setup/Write Claude Code MCP Config", false, 321)]
        public static void MenuWriteClaudeCodeMcpConfig()
        {
            var result = WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Claude Code MCP config", result);
        }

        [MenuItem("UPilot/Advanced/Agent Setup/Write Cursor MCP Config", false, 322)]
        public static void MenuWriteCursorMcpConfig()
        {
            var result = WriteCursorMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Cursor MCP config", result);
        }

        public static string WriteAgentRules(bool overwriteExisting)
        {
            var projectRoot = GetProjectRoot();
            var result = new StringBuilder();
            var agentsPath = Path.Combine(projectRoot, "AGENTS.md");
            var claudePath = Path.Combine(projectRoot, "CLAUDE.md");
            var cursorPath = Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc");

            WriteManagedTextFile(
                agentsPath,
                BuildAgentsMd(agentsPath, logRender: true),
                overwriteExisting,
                result);

            WriteManagedTextFile(
                claudePath,
                "@AGENTS.md\n",
                overwriteExisting,
                result);

            WriteCursorRuleFile(
                cursorPath,
                overwriteExisting,
                result);

            CopySkillInstall(projectRoot, overwriteExisting, result);

            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string WriteCodexMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".codex", "config.toml");
            return WriteTomlMcpConfig(path, promptBeforeOverwrite);
        }

        public static string WriteClaudeCodeMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".mcp.json");
            return WriteJsonMcpConfig(path, includeType: true, promptBeforeOverwrite);
        }

        public static string WriteCursorMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".cursor", "mcp.json");
            return WriteJsonMcpConfig(path, includeType: false, promptBeforeOverwrite);
        }

        public static string WriteAgentMcpConfig(string clientName, bool promptBeforeOverwrite)
        {
            if (clientName == "Codex")
                return WriteCodexMcpConfig(promptBeforeOverwrite);
            if (clientName == "Claude Code")
                return WriteClaudeCodeMcpConfig(promptBeforeOverwrite);
            if (clientName == "Cursor")
                return WriteCursorMcpConfig(promptBeforeOverwrite);
            return "Unsupported Agent: " + clientName;
        }

        public static string UpdateAgentRules(string clientName, bool forceSkillOverwrite)
        {
            var projectRoot = GetProjectRoot();
            var result = new StringBuilder();

            if (clientName == "Codex")
            {
                WriteSharedAgentsRule(projectRoot, result);
                CopySkillInstall(projectRoot, forceSkillOverwrite, result);
            }
            else if (clientName == "Claude Code")
            {
                WriteSharedAgentsRule(projectRoot, result);
                WriteManagedTextFile(
                    Path.Combine(projectRoot, "CLAUDE.md"),
                    "@AGENTS.md\n",
                    overwriteExisting: false,
                    result);
            }
            else if (clientName == "Cursor")
            {
                WriteCursorRuleFile(
                    Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc"),
                    overwriteExisting: false,
                    result);
            }
            else
            {
                return "Unsupported Agent: " + clientName;
            }

            MarkAgentRulesHandledForCurrentProject();
            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string UpdateAllAgentRules(bool forceCodexSkillOverwrite)
        {
            var projectRoot = GetProjectRoot();
            var result = new StringBuilder();
            WriteSharedAgentsRule(projectRoot, result);
            WriteManagedTextFile(
                Path.Combine(projectRoot, "CLAUDE.md"),
                "@AGENTS.md\n",
                overwriteExisting: false,
                result);
            WriteCursorRuleFile(
                Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc"),
                overwriteExisting: false,
                result);
            CopySkillInstall(projectRoot, forceCodexSkillOverwrite, result);
            MarkAgentRulesHandledForCurrentProject();
            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static void MarkAgentRulesHandledForCurrentProject()
        {
            EditorPrefs.SetBool(GetAgentRulesSetupKey(), true);
        }

        private static void WriteSharedAgentsRule(string projectRoot, StringBuilder result)
        {
            var agentsPath = Path.Combine(projectRoot, "AGENTS.md");
            WriteManagedTextFile(
                agentsPath,
                BuildAgentsMd(agentsPath, logRender: true),
                overwriteExisting: false,
                result);
        }

        private static AgentRuleConfigStatus InspectCodexRuleConfig(string projectRoot)
        {
            var skillPath = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            try
            {
                var agentsState = InspectManagedRuleFile(
                    Path.Combine(projectRoot, "AGENTS.md"),
                    BuildAgentsMd());
                if (!Directory.Exists(skillPath))
                    return new AgentRuleConfigStatus("Codex", skillPath, AgentRuleConfigState.Missing);

                if (!TryReadSkillInstallMetadata(skillPath, out var templateVersion, out var contentHash) ||
                    !string.Equals(contentHash, ComputeSkillInstallHash(skillPath), StringComparison.OrdinalIgnoreCase))
                {
                    return new AgentRuleConfigStatus("Codex", skillPath, AgentRuleConfigState.Customized);
                }

                if (templateVersion < SkillInstallTemplateVersion ||
                    agentsState != AgentRuleConfigState.Current)
                {
                    return new AgentRuleConfigStatus("Codex", skillPath, AgentRuleConfigState.UpdateAvailable);
                }

                return new AgentRuleConfigStatus("Codex", skillPath, AgentRuleConfigState.Current);
            }
            catch (Exception ex)
            {
                return new AgentRuleConfigStatus("Codex", skillPath, AgentRuleConfigState.Error, ex.Message);
            }
        }

        private static AgentRuleConfigStatus InspectClaudeRuleConfig(string projectRoot)
        {
            var path = Path.Combine(projectRoot, "CLAUDE.md");
            try
            {
                var agentsState = InspectManagedRuleFile(
                    Path.Combine(projectRoot, "AGENTS.md"),
                    BuildAgentsMd());
                var claudeState = InspectManagedRuleFile(path, "@AGENTS.md\n");
                if (agentsState == AgentRuleConfigState.Missing || claudeState == AgentRuleConfigState.Missing)
                    return new AgentRuleConfigStatus("Claude Code", path, AgentRuleConfigState.Missing);
                if (agentsState != AgentRuleConfigState.Current || claudeState != AgentRuleConfigState.Current)
                    return new AgentRuleConfigStatus("Claude Code", path, AgentRuleConfigState.UpdateAvailable);
                return new AgentRuleConfigStatus("Claude Code", path, AgentRuleConfigState.Current);
            }
            catch (Exception ex)
            {
                return new AgentRuleConfigStatus("Claude Code", path, AgentRuleConfigState.Error, ex.Message);
            }
        }

        private static AgentRuleConfigStatus InspectCursorRuleConfig(string projectRoot)
        {
            var path = Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc");
            try
            {
                var state = InspectManagedRuleFile(path, BuildAgentsMd());
                return new AgentRuleConfigStatus("Cursor", path, state);
            }
            catch (Exception ex)
            {
                return new AgentRuleConfigStatus("Cursor", path, AgentRuleConfigState.Error, ex.Message);
            }
        }

        private static AgentRuleConfigState InspectManagedRuleFile(string path, string content)
        {
            if (!File.Exists(path))
                return AgentRuleConfigState.Missing;

            var original = File.ReadAllText(path, Encoding.UTF8);
            var pattern = Regex.Escape(ManagedBlockStart) + ".*?" + Regex.Escape(ManagedBlockEnd);
            var match = Regex.Match(original, pattern, RegexOptions.Singleline);
            if (!match.Success)
                return AgentRuleConfigState.Missing;

            var expected = WrapManagedBlock(content).TrimEnd();
            var actual = match.Value.TrimEnd();
            return string.Equals(
                NormalizeRuleForComparison(actual),
                NormalizeRuleForComparison(expected),
                StringComparison.Ordinal)
                ? AgentRuleConfigState.Current
                : AgentRuleConfigState.UpdateAvailable;
        }

        private static string NormalizeRuleForComparison(string value)
        {
            var normalized = NormalizeLineEndings(value);
            return Regex.Replace(normalized, "(?m)^generatedAt: .*$", "generatedAt: <ignored>");
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static AgentMcpConfigStatus InspectTomlConfig(string clientName, string path)
        {
            if (!File.Exists(path))
                return new AgentMcpConfigStatus(clientName, path, false, false, false);

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var section = Regex.Match(
                    text,
                    "(?ms)^\\[mcp_servers\\.upilot\\]\\s*(.*?)(?=^\\[|\\z)");
                if (!section.Success)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false);

                var urlMatch = Regex.Match(section.Value, "(?m)^\\s*url\\s*=\\s*\"([^\"]+)\"");
                var usesCurrentUrl = urlMatch.Success &&
                                     string.Equals(urlMatch.Groups[1].Value, McpUrl, StringComparison.OrdinalIgnoreCase);
                return new AgentMcpConfigStatus(clientName, path, true, true, usesCurrentUrl);
            }
            catch (Exception ex)
            {
                return new AgentMcpConfigStatus(clientName, path, true, false, false, ex.Message);
            }
        }

        private static AgentMcpConfigStatus InspectJsonConfig(string clientName, string path)
        {
            if (!File.Exists(path))
                return new AgentMcpConfigStatus(clientName, path, false, false, false);

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var mcpMatch = Regex.Match(text, "\"mcpServers\"\\s*:");
                if (!mcpMatch.Success)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false);

                var mcpObjectOpen = text.IndexOf('{', mcpMatch.Index + mcpMatch.Length);
                if (mcpObjectOpen < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "mcpServers 格式无效");
                var mcpObjectClose = FindMatchingBrace(text, mcpObjectOpen);
                if (mcpObjectClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "mcpServers 格式无效");

                var mcpBody = text.Substring(mcpObjectOpen + 1, mcpObjectClose - mcpObjectOpen - 1);
                var upilotMatch = Regex.Match(mcpBody, "\"upilot\"\\s*:");
                if (!upilotMatch.Success)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false);

                var upilotPropertyStart = mcpObjectOpen + 1 + upilotMatch.Index;
                var upilotObjectOpen = text.IndexOf('{', upilotPropertyStart + upilotMatch.Length);
                if (upilotObjectOpen < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "UPilot 配置格式无效");
                var upilotObjectClose = FindMatchingBrace(text, upilotObjectOpen);
                if (upilotObjectClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "UPilot 配置格式无效");

                var upilotBody = text.Substring(upilotObjectOpen, upilotObjectClose - upilotObjectOpen + 1);
                var urlMatch = Regex.Match(upilotBody, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                var usesCurrentUrl = urlMatch.Success &&
                                     string.Equals(urlMatch.Groups[1].Value, McpUrl, StringComparison.OrdinalIgnoreCase);
                return new AgentMcpConfigStatus(clientName, path, true, true, usesCurrentUrl);
            }
            catch (Exception ex)
            {
                return new AgentMcpConfigStatus(clientName, path, true, false, false, ex.Message);
            }
        }

        private static void EnsureAgentRulesOnce()
        {
            try
            {
                if (!UPilotSetupState.IsCompleted)
                    return;

                var key = GetAgentRulesSetupKey();
                if (EditorPrefs.GetBool(key, false))
                    return;

                var result = WriteAgentRules(overwriteExisting: false);
                MarkAgentRulesHandledForCurrentProject();

                if (!string.Equals(result, "No changes needed.", StringComparison.Ordinal))
                    Debug.Log("[UPilot] Agent discovery rules installed:\n" + result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UPilot] Agent discovery setup failed: " + ex.Message);
            }
        }

        internal static string[] GetAgentRulesPreferenceKeysForCurrentProject()
        {
            string projectHash = StableHash(GetProjectRoot());
            var keys = new List<string>();

            // Legacy keys are retained so the Preferences reset action can clean markers written by older versions.
            for (int version = 1; version <= AgentRulesTemplateVersion; version++)
                keys.Add(AutoSetupKeyPrefix + projectHash + ".v" + version);

            for (int rulesVersion = 1; rulesVersion <= AgentRulesTemplateVersion; rulesVersion++)
            {
                for (int skillVersion = 1; skillVersion <= SkillInstallTemplateVersion; skillVersion++)
                    keys.Add(BuildAgentRulesSetupKey(projectHash, rulesVersion, skillVersion));
            }

            return keys.ToArray();
        }

        private static string GetAgentRulesSetupKey()
        {
            return BuildAgentRulesSetupKey(
                StableHash(GetProjectRoot()),
                AgentRulesTemplateVersion,
                SkillInstallTemplateVersion);
        }

        private static string BuildAgentRulesSetupKey(string projectHash, int rulesVersion, int skillVersion)
        {
            return AutoSetupKeyPrefix + projectHash + ".rules.v" + rulesVersion + ".skill.v" + skillVersion;
        }

        private static string WriteJsonMcpConfig(string path, bool includeType, bool promptBeforeOverwrite)
        {
            var content = BuildMcpJson(includeType);
            if (!File.Exists(path))
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return "Wrote " + NormalizePathForLog(path);
            }

            if (promptBeforeOverwrite)
            {
                var ok = EditorUtility.DisplayDialog(
                    "Update UPilot MCP config?",
                    "This will update only the UPilot MCP server entry in:\n\n" + path,
                    "Update",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertJsonMcpServer(original, includeType);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return "Updated UPilot entry in " + NormalizePathForLog(path);
        }

        private static string WriteTomlMcpConfig(string path, bool promptBeforeOverwrite)
        {
            var content = BuildCodexConfig();
            if (!File.Exists(path))
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return "Wrote " + NormalizePathForLog(path);
            }

            if (promptBeforeOverwrite)
            {
                var ok = EditorUtility.DisplayDialog(
                    "Update UPilot MCP config?",
                    "This will update only the [mcp_servers.upilot] section in:\n\n" + path,
                    "Update",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertTomlSection(original, "[mcp_servers.upilot]", content);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return "Updated UPilot section in " + NormalizePathForLog(path);
        }

        private static void WriteManagedTextFile(string path, string content, bool overwriteExisting, StringBuilder result)
        {
            var managedContent = WrapManagedBlock(content);
            var existed = File.Exists(path);
            LogAgentRulesFileProcessing(
                "Begin managed text file",
                path,
                sourcePath: "",
                $"exists={existed}; overwriteExisting={overwriteExisting}; managedBytes={Encoding.UTF8.GetByteCount(managedContent)}");
            if (!File.Exists(path))
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, managedContent, new UTF8Encoding(false));
                LogAgentRulesFileProcessing("Wrote managed text file", path, sourcePath: "");
                result.AppendLine("Wrote " + NormalizePathForLog(path));
                return;
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            if (overwriteExisting)
            {
                File.WriteAllText(path, managedContent, new UTF8Encoding(false));
                LogAgentRulesFileProcessing(
                    "Replaced managed text file",
                    path,
                    sourcePath: "",
                    $"oldBytes={Encoding.UTF8.GetByteCount(original)}; newBytes={Encoding.UTF8.GetByteCount(managedContent)}");
                result.AppendLine("Replaced " + NormalizePathForLog(path));
                return;
            }

            var updated = UpsertManagedBlock(original, content);
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                LogAgentRulesFileProcessing("Kept managed text file", path, sourcePath: "", "reason=no-change");
                result.AppendLine("Kept existing " + NormalizePathForLog(path));
                return;
            }

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            LogAgentRulesFileProcessing(
                "Updated managed text file",
                path,
                sourcePath: "",
                $"oldBytes={Encoding.UTF8.GetByteCount(original)}; newBytes={Encoding.UTF8.GetByteCount(updated)}");
            result.AppendLine("Updated UPilot block in " + NormalizePathForLog(path));
        }

        private static void WriteCursorRuleFile(string path, bool overwriteExisting, StringBuilder result)
        {
            var content = BuildCursorRule(path, logRender: true);
            var existed = File.Exists(path);
            LogAgentRulesFileProcessing(
                "Begin Cursor rule file",
                path,
                sourcePath: "",
                $"exists={existed}; overwriteExisting={overwriteExisting}; bytes={Encoding.UTF8.GetByteCount(content)}");
            if (!existed || overwriteExisting)
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                LogAgentRulesFileProcessing(
                    existed && overwriteExisting ? "Replaced Cursor rule file" : "Wrote Cursor rule file",
                    path,
                    sourcePath: "");
                result.AppendLine((existed && overwriteExisting ? "Replaced " : "Wrote ") + NormalizePathForLog(path));
                return;
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertManagedBlock(original, BuildAgentsMd(path, logRender: true));
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                LogAgentRulesFileProcessing("Kept Cursor rule file", path, sourcePath: "", "reason=no-change");
                result.AppendLine("Kept existing " + NormalizePathForLog(path));
                return;
            }

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            LogAgentRulesFileProcessing(
                "Updated Cursor rule file",
                path,
                sourcePath: "",
                $"oldBytes={Encoding.UTF8.GetByteCount(original)}; newBytes={Encoding.UTF8.GetByteCount(updated)}");
            result.AppendLine("Updated UPilot block in " + NormalizePathForLog(path));
        }

        private static void CopySkillInstall(string projectRoot, bool overwriteExisting, StringBuilder result)
        {
            var target = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            var source = Path.Combine(ResolvePackageRoot(), "skills", SkillName);
            LogAgentRulesFileProcessing(
                "Begin Skill install",
                target,
                source,
                $"targetExists={Directory.Exists(target)}; sourceExists={Directory.Exists(source)}; overwriteExisting={overwriteExisting}; templateVersion={SkillInstallTemplateVersion}");
            if (Directory.Exists(target) && !overwriteExisting)
            {
                var isUnmodifiedManagedInstall = TryReadSkillInstallMetadata(
                    target,
                    out var installedTemplateVersion,
                    out var installedContentHash) &&
                    string.Equals(
                        installedContentHash,
                        ComputeSkillInstallHash(target),
                        StringComparison.OrdinalIgnoreCase);
                LogAgentRulesFileProcessing(
                    "Inspected existing Skill install",
                    target,
                    source,
                    $"managed={isUnmodifiedManagedInstall}; installedTemplateVersion={installedTemplateVersion}; expectedTemplateVersion={SkillInstallTemplateVersion}; installedHash={installedContentHash}");

                if (isUnmodifiedManagedInstall &&
                    installedTemplateVersion < SkillInstallTemplateVersion &&
                    Directory.Exists(source))
                {
                    LogAgentRulesFileProcessing("Deleting old managed Skill install", target, source);
                    Directory.Delete(target, recursive: true);
                    CopyDirectoryWithoutMeta(source, target);
                    RewriteCopiedSkillEndpoint(target);
                    WriteSkillInstallMetadata(target);
                    LogAgentRulesFileProcessing("Updated managed Skill install", target, source);
                    result.AppendLine("Updated managed " + NormalizePathForLog(target));
                    return;
                }

                RewriteCopiedSkillEndpoint(target);
                if (isUnmodifiedManagedInstall)
                {
                    WriteSkillInstallMetadata(target);
                    LogAgentRulesFileProcessing("Kept current managed Skill install", target, source);
                    result.AppendLine("Kept current managed " + NormalizePathForLog(target));
                }
                else
                {
                    LogAgentRulesFileProcessing("Kept customized Skill install", target, source);
                    result.AppendLine("Kept existing unmanaged or customized " + NormalizePathForLog(target));
                }
                return;
            }

            if (!Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                var fallbackSkillPath = Path.Combine(target, "SKILL.md");
                File.WriteAllText(
                    fallbackSkillPath,
                    BuildFallbackSkill(fallbackSkillPath, logRender: true),
                    new UTF8Encoding(false));
                LogAgentRulesFileProcessing("Wrote fallback Skill file", fallbackSkillPath, sourcePath: "");
                RewriteCopiedSkillEndpoint(target);
                WriteSkillInstallMetadata(target);
                LogAgentRulesFileProcessing("Wrote fallback Skill install", target, source);
                result.AppendLine("Wrote fallback " + NormalizePathForLog(target));
                return;
            }

            if (Directory.Exists(target))
            {
                LogAgentRulesFileProcessing("Deleting Skill install before overwrite", target, source);
                Directory.Delete(target, recursive: true);
            }

            CopyDirectoryWithoutMeta(source, target);
            RewriteCopiedSkillEndpoint(target);
            WriteSkillInstallMetadata(target);
            LogAgentRulesFileProcessing("Wrote Skill install", target, source);
            result.AppendLine("Wrote " + NormalizePathForLog(target));
        }

        private static bool TryReadSkillInstallMetadata(
            string target,
            out int templateVersion,
            out string contentHash)
        {
            templateVersion = 0;
            contentHash = "";
            var metadataPath = Path.Combine(target, SkillInstallMetadataFileName);
            if (!File.Exists(metadataPath))
                return false;

            try
            {
                var json = File.ReadAllText(metadataPath, Encoding.UTF8);
                var versionMatch = Regex.Match(json, "\"templateVersion\"\\s*:\\s*(\\d+)");
                var hashMatch = Regex.Match(json, "\"contentSha256\"\\s*:\\s*\"([0-9a-fA-F]{64})\"");
                if (!versionMatch.Success || !hashMatch.Success)
                    return false;

                templateVersion = int.Parse(versionMatch.Groups[1].Value);
                contentHash = hashMatch.Groups[1].Value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteSkillInstallMetadata(string target)
        {
            var metadataPath = Path.Combine(target, SkillInstallMetadataFileName);
            var contentHash = ComputeSkillInstallHash(target);
            var json = "{\n" +
                       $"  \"templateVersion\": {SkillInstallTemplateVersion},\n" +
                       $"  \"contentSha256\": \"{contentHash}\"\n" +
                       "}\n";
            File.WriteAllText(metadataPath, json, new UTF8Encoding(false));
            LogAgentRulesFileProcessing(
                "Wrote Skill install metadata",
                metadataPath,
                sourcePath: "",
                $"templateVersion={SkillInstallTemplateVersion}; contentSha256={contentHash}");
        }

        private static string ComputeSkillInstallHash(string target)
        {
            using var sha256 = SHA256.Create();
            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), SkillInstallMetadataFileName, StringComparison.OrdinalIgnoreCase) ||
                    ShouldSkipSkillInstallPath(file))
                    continue;

                var relativePath = file.Substring(target.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                var pathBytes = Encoding.UTF8.GetBytes(relativePath);
                sha256.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);
                var separator = new byte[] { 0 };
                sha256.TransformBlock(separator, 0, separator.Length, separator, 0);

                var contentBytes = File.ReadAllBytes(file);
                sha256.TransformBlock(contentBytes, 0, contentBytes.Length, contentBytes, 0);
                sha256.TransformBlock(separator, 0, separator.Length, separator, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var sb = new StringBuilder(sha256.Hash.Length * 2);
            foreach (var b in sha256.Hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void RewriteCopiedSkillEndpoint(string target)
        {
            var skillPath = Path.Combine(target, "SKILL.md");
            if (!File.Exists(skillPath))
                return;

            var text = File.ReadAllText(skillPath, Encoding.UTF8);
            text = Regex.Replace(text, "http://127\\.0\\.0\\.1:\\d+/mcp", McpUrl);
            text = Regex.Replace(text, "http://127\\.0\\.0\\.1:\\d+/health", HealthUrl);
            File.WriteAllText(skillPath, text, new UTF8Encoding(false));
            LogAgentRulesFileProcessing(
                "Rewrote Skill endpoint",
                skillPath,
                sourcePath: "",
                $"mcpUrl={McpUrl}; healthUrl={HealthUrl}");
        }

        private static void CopyDirectoryWithoutMeta(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                if (ShouldSkipSkillInstallPath(file))
                    continue;

                var dest = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                LogAgentRulesFileProcessing("Copied Skill file", dest, file);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                if (ShouldSkipSkillInstallPath(dir))
                    continue;

                var dest = Path.Combine(target, Path.GetFileName(dir));
                CopyDirectoryWithoutMeta(dir, dest);
            }
        }

        private static bool ShouldSkipSkillInstallPath(string path)
        {
            var name = Path.GetFileName(path);
            return string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePackageRoot()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
                return packageInfo.resolvedPath;

            var projectRoot = GetProjectRoot();
            var embedded = Path.Combine(projectRoot, "Packages", PackageName);
            if (Directory.Exists(embedded))
                return embedded;

            var cacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(cacheRoot))
            {
                foreach (var dir in Directory.GetDirectories(cacheRoot, PackageName + "*"))
                    return dir;
            }

            return projectRoot;
        }

        private static string BuildAgentsMd()
        {
            return BuildAgentsMd(targetPath: "", logRender: false);
        }

        private static string BuildAgentsMd(string targetPath, bool logRender)
        {
            var projectRoot = GetProjectRoot();
            var packageVersion = string.IsNullOrEmpty(UPilotServerRuntimeService.UpmVersion)
                ? "unknown"
                : UPilotServerRuntimeService.UpmVersion;
            var template = LoadAgentRulesTemplate(out var templatePath);
            var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var parentAgentRulesPath = FindParentAgentRulesRelativePath(projectRoot);
            if (logRender)
                LogAgentRulesTemplateRender(templatePath, targetPath, projectRoot, packageVersion, generatedAt, parentAgentRulesPath);
            return RenderAgentRulesTemplate(
                template,
                projectRoot,
                packageVersion,
                generatedAt,
                parentAgentRulesPath);
        }

        private static string LoadAgentRulesTemplate(out string templatePath)
        {
            templatePath = Path.Combine(
                ResolvePackageRoot(),
                "skills",
                SkillName,
                AgentRulesTemplateFileName);
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("UPilot Agent rules template is missing.", templatePath);

            return File.ReadAllText(templatePath, Encoding.UTF8);
        }

        private static string RenderAgentRulesTemplate(
            string template,
            string projectRoot,
            string packageVersion,
            string generatedAt,
            string parentAgentRulesPath)
        {
            return template
                .Replace("{{rulesVersion}}", AgentRulesTemplateVersion.ToString())
                .Replace("{{upilotPackageVersion}}", packageVersion)
                .Replace("{{projectPath}}", projectRoot)
                .Replace("{{generatedAt}}", generatedAt)
                .Replace("{{parentAgentRulesPath}}", parentAgentRulesPath)
                .Replace("{{mcpUrl}}", McpUrl)
                .Replace("{{healthUrl}}", HealthUrl)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd() + "\n";
        }

        private static void LogAgentRulesTemplateRender(
            string templatePath,
            string targetPath,
            string projectRoot,
            string packageVersion,
            string generatedAt,
            string parentAgentRulesPath)
        {
            Debug.Log(
                "[UPilot] Rendering Agent rules template." +
                "\nTemplate=" + NormalizePathForLog(templatePath) +
                "\nTarget=" + (string.IsNullOrWhiteSpace(targetPath) ? "(none)" : NormalizePathForLog(targetPath)) +
                "\nParameters:" +
                $"\n  rulesVersion={AgentRulesTemplateVersion}" +
                $"\n  skillInstallTemplateVersion={SkillInstallTemplateVersion}" +
                "\n  upilotPackageVersion=" + packageVersion +
                "\n  projectPath=" + NormalizePathForLog(projectRoot) +
                "\n  generatedAt=" + generatedAt +
                "\n  parentAgentRulesPath=" + parentAgentRulesPath +
                "\n  mcpUrl=" + McpUrl +
                "\n  healthUrl=" + HealthUrl);
        }

        private static void LogAgentRulesFileProcessing(
            string action,
            string targetPath,
            string sourcePath,
            string detail = "")
        {
            Debug.Log(
                "[UPilot] Agent rules file processing: " + action +
                "\nTarget=" + (string.IsNullOrWhiteSpace(targetPath) ? "(none)" : NormalizePathForLog(targetPath)) +
                "\nSource=" + (string.IsNullOrWhiteSpace(sourcePath) ? "(none)" : NormalizePathForLog(sourcePath)) +
                (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail));
        }

        private static string BuildCursorRule()
        {
            return BuildCursorRule(targetPath: "", logRender: false);
        }

        private static string BuildCursorRule(string targetPath, bool logRender)
        {
            return "---\n" +
                   "description: Use UPilot MCP for Unity Editor automation\n" +
                   "alwaysApply: true\n" +
                   "---\n\n" +
                   WrapManagedBlock(BuildAgentsMd(targetPath, logRender));
        }

        private static string BuildFallbackSkill()
        {
            return BuildFallbackSkill(targetPath: "", logRender: false);
        }

        private static string BuildFallbackSkill(string targetPath, bool logRender)
        {
            return "---\n" +
                   "name: upilot-unity-mcp\n" +
                   "description: Unity Editor automation through the UPilot MCP server.\n" +
                   "---\n\n" +
                   BuildAgentsMd(targetPath, logRender);
        }

        private static string BuildCodexConfig()
        {
            return "[mcp_servers.upilot]\n" +
                   $"url = \"{McpUrl}\"\n" +
                   "startup_timeout_sec = 10\n" +
                   "tool_timeout_sec = 300\n";
        }

        private static string BuildMcpJson(bool includeType)
        {
            var typeLine = includeType ? "      \"type\": \"http\",\n" : "";
            return "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"upilot\": {\n" +
                   typeLine +
                   $"      \"url\": \"{McpUrl}\"\n" +
                   "    }\n" +
                   "  }\n" +
                   "}\n";
        }

        private static string BuildMcpServerEntry(bool includeType, int indentSpaces)
        {
            var indent = new string(' ', indentSpaces);
            var inner = new string(' ', indentSpaces + 2);
            var typeLine = includeType ? inner + "\"type\": \"http\",\n" : "";
            return indent + "\"upilot\": {\n" +
                   typeLine +
                   inner + $"\"url\": \"{McpUrl}\"\n" +
                   indent + "}";
        }

        private static string WrapManagedBlock(string content)
        {
            return ManagedBlockStart + "\n" +
                   (content ?? string.Empty).TrimEnd() + "\n" +
                   ManagedBlockEnd + "\n";
        }

        private static string UpsertManagedBlock(string original, string content)
        {
            var block = WrapManagedBlock(content);
            if (string.IsNullOrWhiteSpace(original))
                return block;

            var pattern = Regex.Escape(ManagedBlockStart) + ".*?" + Regex.Escape(ManagedBlockEnd) + "\\s*";
            if (Regex.IsMatch(original, pattern, RegexOptions.Singleline))
                return Regex.Replace(original, pattern, block, RegexOptions.Singleline);

            var separator = original.EndsWith("\n") ? "\n" : "\n\n";
            return original.TrimEnd() + separator + block;
        }

        private static string UpsertJsonMcpServer(string original, bool includeType)
        {
            if (string.IsNullOrWhiteSpace(original))
                return BuildMcpJson(includeType);

            var rootOpen = original.IndexOf('{');
            if (rootOpen < 0)
                return BuildMcpJson(includeType);

            var rootClose = FindMatchingBrace(original, rootOpen);
            if (rootClose < 0)
                return BuildMcpJson(includeType);

            var mcpMatch = Regex.Match(original, "\"mcpServers\"\\s*:");
            if (!mcpMatch.Success)
                return InsertMcpServersObject(original, rootOpen, rootClose, includeType);

            var mcpObjectOpen = original.IndexOf('{', mcpMatch.Index + mcpMatch.Length);
            if (mcpObjectOpen < 0)
                return BuildMcpJson(includeType);

            var mcpObjectClose = FindMatchingBrace(original, mcpObjectOpen);
            if (mcpObjectClose < 0)
                return BuildMcpJson(includeType);

            var bodyStart = mcpObjectOpen + 1;
            var bodyLength = mcpObjectClose - bodyStart;
            var body = original.Substring(bodyStart, bodyLength);
            var upilotMatch = Regex.Match(body, "\"upilot\"\\s*:");
            var entry = BuildMcpServerEntry(includeType, 4);

            if (!upilotMatch.Success)
            {
                var bodyHasContent = !string.IsNullOrWhiteSpace(body);
                var insertion = "\n" + entry + (bodyHasContent ? "," : "") + "\n  ";
                return original.Insert(bodyStart, insertion);
            }

            var upilotPropertyStart = bodyStart + upilotMatch.Index;
            var upilotObjectOpen = original.IndexOf('{', upilotPropertyStart + upilotMatch.Length);
            if (upilotObjectOpen < 0)
                return BuildMcpJson(includeType);

            var upilotObjectClose = FindMatchingBrace(original, upilotObjectOpen);
            if (upilotObjectClose < 0)
                return BuildMcpJson(includeType);

            return original.Substring(0, upilotPropertyStart) +
                   entry +
                   original.Substring(upilotObjectClose + 1);
        }

        private static string InsertMcpServersObject(string original, int rootOpen, int rootClose, bool includeType)
        {
            var rootBody = original.Substring(rootOpen + 1, rootClose - rootOpen - 1);
            var rootHasContent = !string.IsNullOrWhiteSpace(rootBody);
            var block = (rootHasContent ? ",\n" : "\n") +
                        "  \"mcpServers\": {\n" +
                        BuildMcpServerEntry(includeType, 4) + "\n" +
                        "  }\n";
            return original.Insert(rootClose, block);
        }

        private static int FindMatchingBrace(string text, int openIndex)
        {
            var depth = 0;
            var inString = false;
            var escape = false;

            for (var i = openIndex; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static string UpsertTomlSection(string original, string sectionName, string sectionContent)
        {
            var normalizedSection = sectionContent.TrimEnd() + "\n";
            if (string.IsNullOrWhiteSpace(original))
                return normalizedSection;

            var pattern = "(?ms)^" + Regex.Escape(sectionName) + "\\s*.*?(?=^\\[|\\z)";
            if (Regex.IsMatch(original, pattern))
                return Regex.Replace(original, pattern, normalizedSection);

            var separator = original.EndsWith("\n") ? "\n" : "\n\n";
            return original + separator + normalizedSection;
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        private static string FindParentAgentRulesRelativePath(string projectRoot)
        {
            try
            {
                var root = new DirectoryInfo(projectRoot);
                for (var current = root.Parent; current != null; current = current.Parent)
                {
                    var candidate = Path.Combine(current.FullName, "AGENTS.md");
                    if (File.Exists(candidate))
                        return MakeRelativeAgentRulesPath(root.FullName, candidate);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UPilot] Failed to resolve parent Agent rules path: " + ex.Message);
            }

            return "(none)";
        }

        private static string MakeRelativeAgentRulesPath(string fromDirectory, string targetPath)
        {
            try
            {
                var from = Path.GetFullPath(fromDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(targetPath);
                var relative = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(target)).ToString());
                return relative.Replace('\\', '/');
            }
            catch
            {
                return NormalizePathForLog(targetPath);
            }
        }

        private static string NormalizePathForLog(string path)
        {
            return path.Replace('\\', '/');
        }

        private static void EnsureParentDirectory(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
        }

        private static string StableHash(string value)
        {
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void ReportResult(string title, string result)
        {
            Debug.Log("[UPilot] " + title + ":\n" + result);
            EditorUtility.DisplayDialog("UPilot", result, "OK");
        }
    }
}
