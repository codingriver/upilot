// -----------------------------------------------------------------------
// upilot Editor — Agent discovery and MCP client setup helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
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
                if (State == AgentRuleConfigState.UpdateAvailable) return "有更新";
                if (State == AgentRuleConfigState.Customized)
                    return ClientName == "Codex" ? "已安装" : "已同步";
                return ClientName == "Codex" ? "已安装" : "已同步";
            }
        }
    }

    [InitializeOnLoad]
    public static class UPilotAgentSetup
    {
        private const string PackageName = "io.github.codingriver.upilot";
        private const string SkillName = "upilot-unity-mcp";
        private const string AutoSetupKeyPrefix = "CodingRiver.UPilot.AgentSetup.AutoRulesWritten.";
        private const int AgentRulesTemplateVersion = 3;
        private const int SkillInstallTemplateVersion = 1;
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

            WriteManagedTextFile(
                Path.Combine(projectRoot, "AGENTS.md"),
                BuildAgentsMd(),
                overwriteExisting,
                result);

            WriteManagedTextFile(
                Path.Combine(projectRoot, "CLAUDE.md"),
                "@AGENTS.md\n",
                overwriteExisting,
                result);

            WriteCursorRuleFile(
                Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc"),
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
            WriteManagedTextFile(
                Path.Combine(projectRoot, "AGENTS.md"),
                BuildAgentsMd(),
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
                NormalizeLineEndings(actual),
                NormalizeLineEndings(expected),
                StringComparison.Ordinal)
                ? AgentRuleConfigState.Current
                : AgentRuleConfigState.UpdateAvailable;
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

        private static string GetAgentRulesSetupKey()
        {
            return AutoSetupKeyPrefix + StableHash(GetProjectRoot()) + ".v" + AgentRulesTemplateVersion;
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
            if (!File.Exists(path))
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, managedContent, new UTF8Encoding(false));
                result.AppendLine("Wrote " + NormalizePathForLog(path));
                return;
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            if (overwriteExisting)
            {
                File.WriteAllText(path, managedContent, new UTF8Encoding(false));
                result.AppendLine("Replaced " + NormalizePathForLog(path));
                return;
            }

            var updated = UpsertManagedBlock(original, content);
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                result.AppendLine("Kept existing " + NormalizePathForLog(path));
                return;
            }

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            result.AppendLine("Updated UPilot block in " + NormalizePathForLog(path));
        }

        private static void WriteCursorRuleFile(string path, bool overwriteExisting, StringBuilder result)
        {
            var content = BuildCursorRule();
            var existed = File.Exists(path);
            if (!existed || overwriteExisting)
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                result.AppendLine((existed && overwriteExisting ? "Replaced " : "Wrote ") + NormalizePathForLog(path));
                return;
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertManagedBlock(original, BuildAgentsMd());
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                result.AppendLine("Kept existing " + NormalizePathForLog(path));
                return;
            }

            File.WriteAllText(path, updated, new UTF8Encoding(false));
            result.AppendLine("Updated UPilot block in " + NormalizePathForLog(path));
        }

        private static void CopySkillInstall(string projectRoot, bool overwriteExisting, StringBuilder result)
        {
            var target = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            var source = Path.Combine(ResolvePackageRoot(), "skills", SkillName);
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

                if (isUnmodifiedManagedInstall &&
                    installedTemplateVersion < SkillInstallTemplateVersion &&
                    Directory.Exists(source))
                {
                    Directory.Delete(target, recursive: true);
                    CopyDirectoryWithoutMeta(source, target);
                    RewriteCopiedSkillEndpoint(target);
                    WriteSkillInstallMetadata(target);
                    result.AppendLine("Updated managed " + NormalizePathForLog(target));
                    return;
                }

                RewriteCopiedSkillEndpoint(target);
                if (isUnmodifiedManagedInstall)
                {
                    WriteSkillInstallMetadata(target);
                    result.AppendLine("Kept current managed " + NormalizePathForLog(target));
                }
                else
                {
                    result.AppendLine("Kept existing unmanaged or customized " + NormalizePathForLog(target));
                }
                return;
            }

            if (!Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "SKILL.md"), BuildFallbackSkill(), new UTF8Encoding(false));
                RewriteCopiedSkillEndpoint(target);
                WriteSkillInstallMetadata(target);
                result.AppendLine("Wrote fallback " + NormalizePathForLog(target));
                return;
            }

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            CopyDirectoryWithoutMeta(source, target);
            RewriteCopiedSkillEndpoint(target);
            WriteSkillInstallMetadata(target);
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
        }

        private static string ComputeSkillInstallHash(string target)
        {
            using var sha256 = SHA256.Create();
            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), SkillInstallMetadataFileName, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
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
        }

        private static void CopyDirectoryWithoutMeta(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(dir));
                CopyDirectoryWithoutMeta(dir, dest);
            }
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
            var mcpUrl = McpUrl;
            var healthUrl = HealthUrl;
            var projectRoot = GetProjectRoot();
            var text = new StringBuilder();
            text.AppendLine("# UPilot Unity MCP");
            text.AppendLine();
            text.AppendLine("This Unity project has the `io.github.codingriver.upilot` UPM package installed.");
            text.AppendLine();
            text.AppendLine("## Connection");
            text.AppendLine();
            text.AppendLine($"- Streamable HTTP: `{mcpUrl}`");
            text.AppendLine($"- Health check: `{healthUrl}`");
            text.AppendLine("- Never configure an MCP client with the internal Unity Bridge WebSocket port.");
            text.AppendLine("1. Call `unity_mcp_status`.");
            text.AppendLine("2. Require `connected: true` and `serverReady: true`.");
            text.AppendLine($"3. Verify `paths.unityProjectAbsolute` matches `{projectRoot}` (allow equivalent slash normalization).");
            text.AppendLine("4. Stop and report the mismatch if another Unity project is connected.");
            text.AppendLine();
            text.AppendLine("## Capabilities");
            text.AppendLine();
            text.AppendLine("- Distinguish server registration, client tool-list injection, and a successful real call; they are different states.");
            text.AppendLine("- If a native tool is not visible in the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable.");
            text.AppendLine("- After enabling an optional feature or changing tool registration, restart or refresh the MCP client tool list.");
            text.AppendLine("- Use the narrowest dedicated semantic tool. Use `unity_reflection_call` for existing compiled entry points.");
            text.AppendLine("- Only after `unity_reflection_call` actually fails may you fall back to one bounded `reflection_eval` expression.");
            text.AppendLine("- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.");
            text.AppendLine("- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.");
            text.AppendLine();
            text.AppendLine("## Writes And Compile");
            text.AppendLine();
            text.AppendLine("- Call `unity_ensure_ready` before Editor mutations and inspect the exact target before destructive changes.");
            text.AppendLine("- After one batch of disk writes, call `unity_sync_after_disk_write` once.");
            text.AppendLine("- Compile only after C# or assembly-related changes. Do not compile again when no code changed.");
            text.AppendLine("- After compilation, read structured compile errors and relevant Console errors before editing again.");
            text.AppendLine();
            text.AppendLine("## Project Workflows");
            text.AppendLine();
            text.AppendLine("- When a project exposes an authoritative compiled orchestration entry point for a test, build, or workflow, call that entry point and poll its state. Do not reconstruct the workflow with shell commands, temporary scripts, menu calls, or UI automation.");
            text.AppendLine("- Keep business orchestration in project code. MCP should start, poll, diagnose, capture logs, and collect artifacts.");
            text.AppendLine();
            text.AppendLine("## Persistent Console Capture");
            text.AppendLine();
            text.AppendLine("- For long-running or audit-sensitive operations, call `unity_console_capture_start` before the operation, use `unity_console_capture_status` and incremental `unity_console_capture_read`, and always call `unity_console_capture_stop` on success or failure.");
            text.AppendLine("- Keep raw Console capture separate from domain-specific reports. Prefer project-relative output paths and do not allow paths outside the project unless the user explicitly requests one.");
            text.AppendLine("- Console capture cleanup must use dry-run, target inspection, and confirm-token execution.");
            text.AppendLine();
            text.AppendLine("## Acceptance");
            text.AppendLine();
            text.AppendLine("- Starting a test, build, or async task is not success; poll its status until a terminal result.");
            text.AppendLine("- For long tasks, report only phase changes, errors, or suspected-stuck state from `unity_operation_*`/task status.");
            text.AppendLine("- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.");
            text.AppendLine("- For acceptance work, prefer dedicated project-relative artifact or screenshot save tools that return metadata or hashes. If capture falls back to base64, window capture, or OS-level automation, report the reason.");
            text.AppendLine("- Retry automatically only when the registry marks the operation idempotent and non-destructive.");
            text.AppendLine("- On timeout, inspect status, operation timing, and last progress before choosing one bounded retry or a documented fallback.");
            return text.ToString();
        }

        private static string BuildCursorRule()
        {
            return "---\n" +
                   "description: Use UPilot MCP for Unity Editor automation\n" +
                   "alwaysApply: true\n" +
                   "---\n\n" +
                   WrapManagedBlock(BuildAgentsMd());
        }

        private static string BuildFallbackSkill()
        {
            return "---\n" +
                   "name: upilot-unity-mcp\n" +
                   "description: Unity Editor automation through the UPilot MCP server.\n" +
                   "---\n\n" +
                   BuildAgentsMd();
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
