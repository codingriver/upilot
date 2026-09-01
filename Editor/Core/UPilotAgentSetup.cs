// -----------------------------------------------------------------------
// upilot Editor — Agent discovery and MCP client setup helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            string errorMessage = "",
            string configuredUrl = "",
            string configurationIssue = "")
        {
            ClientName = clientName;
            ConfigPath = configPath;
            FileExists = fileExists;
            HasUPilotEntry = hasUPilotEntry;
            UsesCurrentUrl = usesCurrentUrl;
            ErrorMessage = errorMessage ?? "";
            ConfiguredUrl = configuredUrl ?? "";
            ConfigurationIssue = configurationIssue ?? "";
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public bool FileExists { get; }
        public bool HasUPilotEntry { get; }
        public bool UsesCurrentUrl { get; }
        public string ErrorMessage { get; }
        public string ConfiguredUrl { get; }
        public string ConfigurationIssue { get; }
        public bool IsConfigured => FileExists && HasUPilotEntry && UsesCurrentUrl &&
                                    string.IsNullOrEmpty(ErrorMessage) &&
                                    string.IsNullOrEmpty(ConfigurationIssue);

        public string StateText
        {
            get
            {
                if (!string.IsNullOrEmpty(ErrorMessage)) return "读取失败";
                if (!FileExists) return "未配置";
                if (!HasUPilotEntry) return "缺少 UPilot 配置";
                if (!UsesCurrentUrl) return "端口已变化，需更新";
                if (!string.IsNullOrEmpty(ConfigurationIssue)) return ConfigurationIssue;
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
            string errorMessage = "",
            string[] configPaths = null,
            int installedVersion = 0,
            int targetVersion = 0,
            string sourcePath = "",
            string contentHash = "",
            string expectedHash = "",
            string[] issuePaths = null)
        {
            ClientName = clientName;
            ConfigPath = configPath;
            State = state;
            ErrorMessage = errorMessage ?? "";
            ConfigPaths = configPaths ?? (string.IsNullOrEmpty(configPath) ? Array.Empty<string>() : new[] { configPath });
            InstalledVersion = installedVersion;
            TargetVersion = targetVersion;
            SourcePath = sourcePath ?? "";
            ContentHash = contentHash ?? "";
            ExpectedHash = expectedHash ?? "";
            IssuePaths = issuePaths ??
                         (state == AgentRuleConfigState.Current
                             ? Array.Empty<string>()
                             : ConfigPaths);
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public AgentRuleConfigState State { get; }
        public string ErrorMessage { get; }
        public string[] ConfigPaths { get; }
        public int InstalledVersion { get; }
        public int TargetVersion { get; }
        public string SourcePath { get; }
        public string ContentHash { get; }
        public string ExpectedHash { get; }
        public string[] IssuePaths { get; }
        public bool IsCurrent => State == AgentRuleConfigState.Current;
        public bool HasLocalCustomization => State == AgentRuleConfigState.Customized;

        public string StateText
        {
            get
            {
                if (State == AgentRuleConfigState.Error) return "读取失败";
                if (State == AgentRuleConfigState.Missing)
                    return "未同步";
                if (State == AgentRuleConfigState.UpdateAvailable) return "有新版本";
                if (State == AgentRuleConfigState.Customized)
                    return "需要确认";
                return "已同步";
            }
        }
    }

    public enum AgentSkillConfigState
    {
        NotProvided,
        Missing,
        Current,
        UpdateAvailable,
        Customized,
        Conflict,
        Error,
    }

    public readonly struct AgentSkillConfigStatus
    {
        public AgentSkillConfigStatus(
            string clientName,
            string configPath,
            AgentSkillConfigState state,
            string errorMessage = "",
            string sourcePath = "",
            int installedVersion = 0,
            int targetVersion = 0,
            string recordedHash = "",
            string contentHash = "",
            string applicabilityExplanation = "",
            string skillRootPath = "",
            int installedSkillCount = 0,
            string[] installedSkillNames = null,
            int upilotSkillCount = 0,
            string[] capabilityLabels = null,
            int associatedToolCount = 0,
            string[] primaryToolNames = null,
            string[] skillRootPaths = null,
            string[] duplicateSkillPaths = null,
            string skillConflictSummary = "")
        {
            ClientName = clientName;
            ConfigPath = configPath ?? "";
            State = state;
            ErrorMessage = errorMessage ?? "";
            SourcePath = sourcePath ?? "";
            InstalledVersion = installedVersion;
            TargetVersion = targetVersion;
            RecordedHash = recordedHash ?? "";
            ContentHash = contentHash ?? "";
            ApplicabilityExplanation = applicabilityExplanation ?? "";
            SkillRootPath = skillRootPath ?? "";
            InstalledSkillCount = installedSkillCount;
            InstalledSkillNames = installedSkillNames ?? Array.Empty<string>();
            UpilotSkillCount = upilotSkillCount;
            CapabilityLabels = capabilityLabels ?? Array.Empty<string>();
            AssociatedToolCount = associatedToolCount;
            PrimaryToolNames = primaryToolNames ?? Array.Empty<string>();
            SkillRootPaths = skillRootPaths ??
                             (string.IsNullOrEmpty(SkillRootPath)
                                 ? Array.Empty<string>()
                                 : new[] { SkillRootPath });
            DuplicateSkillPaths = duplicateSkillPaths ?? Array.Empty<string>();
            SkillConflictSummary = skillConflictSummary ?? "";
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public AgentSkillConfigState State { get; }
        public string ErrorMessage { get; }
        public string SourcePath { get; }
        public int InstalledVersion { get; }
        public int TargetVersion { get; }
        public string RecordedHash { get; }
        public string ContentHash { get; }
        public string ApplicabilityExplanation { get; }
        public string SkillRootPath { get; }
        public string[] SkillRootPaths { get; }
        public int InstalledSkillCount { get; }
        public string[] InstalledSkillNames { get; }
        public int UpilotSkillCount { get; }
        public string[] CapabilityLabels { get; }
        public int AssociatedToolCount { get; }
        public string[] PrimaryToolNames { get; }
        public string[] DuplicateSkillPaths { get; }
        public string SkillConflictSummary { get; }
        public bool HasSkillConflict => State == AgentSkillConfigState.Conflict;
        public bool IsApplicable => State != AgentSkillConfigState.NotProvided;
        public bool IsCurrent => State == AgentSkillConfigState.Current;
        public bool IsSatisfied => State == AgentSkillConfigState.Current || State == AgentSkillConfigState.NotProvided;
        public bool HasLocalCustomization => State == AgentSkillConfigState.Customized ||
                                             State == AgentSkillConfigState.Conflict;

        public string StateText
        {
            get
            {
                if (State == AgentSkillConfigState.NotProvided) return "当前未提供";
                if (State == AgentSkillConfigState.Error) return "读取失败";
                if (State == AgentSkillConfigState.Missing) return "未安装";
                if (State == AgentSkillConfigState.UpdateAvailable) return "有新版本";
                if (State == AgentSkillConfigState.Customized) return "需要确认";
                if (State == AgentSkillConfigState.Conflict) return "发现冲突";
                return "已安装";
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
        private const int SkillInstallTemplateVersion = 19;
        private const int OpenCodeMcpTimeoutMs = 30000;
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
                InspectOpenCodeConfig("OpenCode", ResolveOpenCodeConfigPath(projectRoot)),
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
                InspectOpenCodeRuleConfig(projectRoot),
            };
        }

        public static AgentSkillConfigStatus[] GetSkillConfigStatuses()
        {
            var projectRoot = GetProjectRoot();
            return new[]
            {
                InspectSkillConfig(projectRoot, "Codex"),
                InspectSkillConfig(projectRoot, "Claude Code"),
                InspectSkillConfig(projectRoot, "Cursor"),
                InspectSkillConfig(projectRoot, "OpenCode"),
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

        [MenuItem("UPilot/Advanced/Agent Setup/Write OpenCode MCP Config", false, 323)]
        public static void MenuWriteOpenCodeMcpConfig()
        {
            var result = WriteOpenCodeMcpConfig(promptBeforeOverwrite: true);
            ReportResult("OpenCode MCP config", result);
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

            CopyAllSkillInstalls(projectRoot, overwriteExisting, result);

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

        public static string WriteOpenCodeMcpConfig(bool promptBeforeOverwrite)
        {
            var path = ResolveOpenCodeConfigPath(GetProjectRoot());
            return WriteOpenCodeJsonMcpConfig(path, promptBeforeOverwrite);
        }

        public static string WriteAgentMcpConfig(string clientName, bool promptBeforeOverwrite)
        {
            if (clientName == "Codex")
                return WriteCodexMcpConfig(promptBeforeOverwrite);
            if (clientName == "Claude Code")
                return WriteClaudeCodeMcpConfig(promptBeforeOverwrite);
            if (clientName == "Cursor")
                return WriteCursorMcpConfig(promptBeforeOverwrite);
            if (clientName == "OpenCode")
                return WriteOpenCodeMcpConfig(promptBeforeOverwrite);
            return "Unsupported Agent: " + clientName;
        }

        public static string UpdateAgentRules(string clientName)
        {
            var projectRoot = GetProjectRoot();
            var result = new StringBuilder();

            if (clientName == "Codex")
            {
                WriteSharedAgentsRule(projectRoot, result);
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
            else if (clientName == "OpenCode")
            {
                WriteSharedAgentsRule(projectRoot, result);
            }
            else
            {
                return "Unsupported Agent: " + clientName;
            }

            MarkAgentRulesHandledForCurrentProject();
            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string UpdateAgentRules(string clientName, bool forceSkillOverwrite)
        {
            var result = UpdateAgentRules(clientName);
            var skillResult = UpdateAgentSkill(clientName, forceSkillOverwrite);
            return CombineResults(result, skillResult);
        }

        public static string UpdateAgentSkill(string clientName, bool forceOverwrite)
        {
            var projectRoot = GetProjectRoot();
            var target = GetAgentSkillInstallPath(projectRoot, clientName);
            if (string.IsNullOrEmpty(target))
                return "Unsupported Agent: " + clientName;

            var result = new StringBuilder();
            CopySkillInstall(target, clientName, forceOverwrite, result);
            MarkAgentRulesHandledForCurrentProject();
            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string UpdateAllAgentRules()
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
            MarkAgentRulesHandledForCurrentProject();
            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string UpdateAllAgentRules(bool forceCodexSkillOverwrite)
        {
            return CombineResults(
                UpdateAllAgentRules(),
                UpdateAllAgentSkills(forceCodexSkillOverwrite));
        }

        public static string UpdateAllAgentSkills(bool forceCustomizedSkillOverwrite)
        {
            var result = new StringBuilder();
            CopyAllSkillInstalls(GetProjectRoot(), forceCustomizedSkillOverwrite, result);
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
            var path = Path.Combine(projectRoot, "AGENTS.md");
            try
            {
                var expected = BuildAgentsMd();
                return CreateRuleStatus(
                    "Codex",
                    new[] { path },
                    new[] { InspectManagedRuleFile(path, expected) },
                    path,
                    new[] { expected });
            }
            catch (Exception ex)
            {
                return CreateRuleErrorStatus("Codex", new[] { path }, ex);
            }
        }

        private static AgentRuleConfigStatus InspectClaudeRuleConfig(string projectRoot)
        {
            var path = Path.Combine(projectRoot, "CLAUDE.md");
            var agentsPath = Path.Combine(projectRoot, "AGENTS.md");
            try
            {
                var expectedAgents = BuildAgentsMd();
                const string expectedClaude = "@AGENTS.md\n";
                return CreateRuleStatus(
                    "Claude Code",
                    new[] { agentsPath, path },
                    new[]
                    {
                        InspectManagedRuleFile(agentsPath, expectedAgents),
                        InspectManagedRuleFile(path, expectedClaude),
                    },
                    agentsPath,
                    new[] { expectedAgents, expectedClaude });
            }
            catch (Exception ex)
            {
                return CreateRuleErrorStatus("Claude Code", new[] { agentsPath, path }, ex);
            }
        }

        private static AgentRuleConfigStatus InspectCursorRuleConfig(string projectRoot)
        {
            var path = Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc");
            try
            {
                var expected = BuildAgentsMd();
                return CreateRuleStatus(
                    "Cursor",
                    new[] { path },
                    new[] { InspectManagedRuleFile(path, expected) },
                    path,
                    new[] { expected });
            }
            catch (Exception ex)
            {
                return CreateRuleErrorStatus("Cursor", new[] { path }, ex);
            }
        }

        private static AgentRuleConfigStatus InspectOpenCodeRuleConfig(string projectRoot)
        {
            var path = Path.Combine(projectRoot, "AGENTS.md");
            try
            {
                var expected = BuildAgentsMd();
                return CreateRuleStatus(
                    "OpenCode",
                    new[] { path },
                    new[] { InspectManagedRuleFile(path, expected) },
                    path,
                    new[] { expected });
            }
            catch (Exception ex)
            {
                return CreateRuleErrorStatus("OpenCode", new[] { path }, ex);
            }
        }

        private static AgentRuleConfigStatus CreateRuleStatus(
            string clientName,
            string[] paths,
            AgentRuleConfigState[] states,
            string versionPath,
            string[] expectedContents)
        {
            var state = AgentRuleConfigState.Current;
            var issuePaths = new List<string>();
            for (var i = 0; i < states.Length; i++)
            {
                var candidate = states[i];
                if (candidate != AgentRuleConfigState.Current && i < paths.Length)
                    issuePaths.Add(paths[i]);
                if (candidate == AgentRuleConfigState.Missing)
                {
                    state = AgentRuleConfigState.Missing;
                    continue;
                }

                if (candidate != AgentRuleConfigState.Current && state != AgentRuleConfigState.Missing)
                    state = AgentRuleConfigState.UpdateAvailable;
            }

            return new AgentRuleConfigStatus(
                clientName,
                paths.Length > 0 ? paths[0] : "",
                state,
                configPaths: paths,
                installedVersion: ReadManagedRuleVersion(versionPath),
                targetVersion: AgentRulesTemplateVersion,
                sourcePath: GetAgentRulesTemplatePath(),
                contentHash: ComputeFilesHash(paths),
                expectedHash: ComputeTextHash(BuildExpectedManagedContent(expectedContents)),
                issuePaths: issuePaths.ToArray());
        }

        private static AgentRuleConfigStatus CreateRuleErrorStatus(string clientName, string[] paths, Exception ex)
        {
            return new AgentRuleConfigStatus(
                clientName,
                paths.Length > 0 ? paths[0] : "",
                AgentRuleConfigState.Error,
                ex.Message,
                paths,
                targetVersion: AgentRulesTemplateVersion,
                sourcePath: GetAgentRulesTemplatePath(),
                issuePaths: paths);
        }

        private static AgentSkillConfigStatus InspectSkillConfig(string projectRoot, string clientName)
        {
            var target = GetAgentSkillInstallPath(projectRoot, clientName);
            var roots = GetAgentSkillDiscoveryRoots(projectRoot, clientName);
            var source = Path.Combine(ResolvePackageRoot(), "skills", SkillName);
            var explanation = GetAgentSkillApplicabilityExplanation(clientName);

            try
            {
                var inventory = InspectSkillInventory(roots, target);
                if (!Directory.Exists(target))
                {
                    return CreateSkillStatus(
                        clientName,
                        target,
                        AgentSkillConfigState.Missing,
                        inventory,
                        sourcePath: source,
                        targetVersion: SkillInstallTemplateVersion,
                        applicabilityExplanation: explanation);
                }

                var contentHash = ComputeSkillInstallHash(target);
                if (!TryReadSkillInstallMetadata(target, out var templateVersion, out var recordedHash))
                {
                    return CreateSkillStatus(
                        clientName,
                        target,
                        AgentSkillConfigState.Customized,
                        inventory,
                        sourcePath: source,
                        targetVersion: SkillInstallTemplateVersion,
                        contentHash: contentHash,
                        applicabilityExplanation: explanation);
                }

                if (!string.Equals(recordedHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return CreateSkillStatus(
                        clientName,
                        target,
                        AgentSkillConfigState.Customized,
                        inventory,
                        sourcePath: source,
                        installedVersion: templateVersion,
                        targetVersion: SkillInstallTemplateVersion,
                        recordedHash: recordedHash,
                        contentHash: contentHash,
                        applicabilityExplanation: explanation);
                }

                var state = templateVersion < SkillInstallTemplateVersion
                    ? AgentSkillConfigState.UpdateAvailable
                    : AgentSkillConfigState.Current;
                if (inventory.HasUpilotSkillConflict)
                    state = AgentSkillConfigState.Conflict;
                return CreateSkillStatus(
                    clientName,
                    target,
                    state,
                    inventory,
                    sourcePath: source,
                    installedVersion: templateVersion,
                    targetVersion: SkillInstallTemplateVersion,
                    recordedHash: recordedHash,
                    contentHash: contentHash,
                    applicabilityExplanation: explanation);
            }
            catch (Exception ex)
            {
                var emptyInventory = new SkillInventory(
                    roots,
                    Array.Empty<string>(),
                    0,
                    Array.Empty<string>(),
                    0,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    "");
                return CreateSkillStatus(
                    clientName,
                    target,
                    AgentSkillConfigState.Error,
                    emptyInventory,
                    errorMessage: ex.Message,
                    sourcePath: source,
                    targetVersion: SkillInstallTemplateVersion,
                    applicabilityExplanation: explanation);
            }
        }

        private static string GetAgentSkillApplicabilityExplanation(string clientName)
        {
            if (clientName == "Claude Code")
                return "Claude Code 使用项目级 .claude/skills 目录加载 UPilot Skill。";
            if (clientName == "Cursor")
                return "Cursor 使用官方支持的 .agents/skills 目录，与 Codex 共享同一受管 UPilot Skill；项目 Skill 数量按 Cursor 可发现目录去重统计。";
            if (clientName == "OpenCode")
                return "OpenCode 使用官方支持的 .agents/skills 目录，与 Codex、Cursor 共享同一受管 UPilot Skill；同时检查 .claude/skills 与 .opencode/skills 中的同名 Skill 冲突。";
            return "Codex 使用项目级 .agents/skills 目录加载 UPilot Skill。";
        }

        private readonly struct SkillInventory
        {
            public SkillInventory(
                string[] rootPaths,
                string[] names,
                int upilotSkillCount,
                string[] capabilityLabels,
                int associatedToolCount,
                string[] primaryToolNames,
                string[] duplicateSkillPaths,
                string skillConflictSummary)
            {
                RootPaths = rootPaths ?? Array.Empty<string>();
                RootPath = RootPaths.Length > 0 ? RootPaths[0] : "";
                Names = names ?? Array.Empty<string>();
                UpilotSkillCount = upilotSkillCount;
                CapabilityLabels = capabilityLabels ?? Array.Empty<string>();
                AssociatedToolCount = associatedToolCount;
                PrimaryToolNames = primaryToolNames ?? Array.Empty<string>();
                DuplicateSkillPaths = duplicateSkillPaths ?? Array.Empty<string>();
                SkillConflictSummary = skillConflictSummary ?? "";
            }

            public string RootPath { get; }
            public string[] RootPaths { get; }
            public string[] Names { get; }
            public int UpilotSkillCount { get; }
            public string[] CapabilityLabels { get; }
            public int AssociatedToolCount { get; }
            public string[] PrimaryToolNames { get; }
            public string[] DuplicateSkillPaths { get; }
            public string SkillConflictSummary { get; }
            public bool HasUpilotSkillConflict => !string.IsNullOrEmpty(SkillConflictSummary);
        }

        private static AgentSkillConfigStatus CreateSkillStatus(
            string clientName,
            string configPath,
            AgentSkillConfigState state,
            SkillInventory inventory,
            string errorMessage = "",
            string sourcePath = "",
            int installedVersion = 0,
            int targetVersion = 0,
            string recordedHash = "",
            string contentHash = "",
            string applicabilityExplanation = "")
        {
            return new AgentSkillConfigStatus(
                clientName,
                configPath,
                state,
                errorMessage,
                sourcePath,
                installedVersion,
                targetVersion,
                recordedHash,
                contentHash,
                applicabilityExplanation,
                inventory.RootPath,
                inventory.Names.Length,
                inventory.Names,
                inventory.UpilotSkillCount,
                inventory.CapabilityLabels,
                inventory.AssociatedToolCount,
                inventory.PrimaryToolNames,
                inventory.RootPaths,
                inventory.DuplicateSkillPaths,
                inventory.SkillConflictSummary);
        }

        internal static string GetAgentSkillInstallPath(string projectRoot, string clientName)
        {
            var root = GetPrimaryAgentSkillRoot(projectRoot, clientName);
            return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, SkillName);
        }

        private static string GetPrimaryAgentSkillRoot(string projectRoot, string clientName)
        {
            if (clientName == "Codex" || clientName == "Cursor" || clientName == "OpenCode")
                return Path.Combine(projectRoot, ".agents", "skills");
            if (clientName == "Claude Code")
                return Path.Combine(projectRoot, ".claude", "skills");
            return "";
        }

        internal static string[] GetAgentSkillDiscoveryRoots(string projectRoot, string clientName)
        {
            if (clientName == "Codex")
                return new[] { Path.Combine(projectRoot, ".agents", "skills") };
            if (clientName == "Claude Code")
                return new[] { Path.Combine(projectRoot, ".claude", "skills") };
            if (clientName == "Cursor")
            {
                return new[]
                {
                    Path.Combine(projectRoot, ".agents", "skills"),
                    Path.Combine(projectRoot, ".cursor", "skills"),
                    Path.Combine(projectRoot, ".claude", "skills"),
                    Path.Combine(projectRoot, ".codex", "skills"),
                };
            }
            if (clientName == "OpenCode")
            {
                return new[]
                {
                    Path.Combine(projectRoot, ".agents", "skills"),
                    Path.Combine(projectRoot, ".claude", "skills"),
                    Path.Combine(projectRoot, ".opencode", "skills"),
                };
            }
            return Array.Empty<string>();
        }

        private static SkillInventory InspectSkillInventory(string[] skillRoots, string upilotSkillPath)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visibleRoots = new List<string>();
            var upilotPaths = new List<string>();
            var upilotHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skillRoot in skillRoots ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(skillRoot) || !Directory.Exists(skillRoot))
                    continue;
                visibleRoots.Add(skillRoot);

                var skillFiles = Directory.GetFiles(skillRoot, "SKILL.md", SearchOption.AllDirectories);
                Array.Sort(skillFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var skillFile in skillFiles)
                {
                    var directory = Path.GetDirectoryName(skillFile) ?? "";
                    var name = ReadSkillName(skillFile, Path.GetFileName(directory));
                    names.Add(name);
                    if (!string.Equals(name, SkillName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    upilotPaths.Add(directory);
                    upilotHashes.Add(ComputeSkillInstallHash(directory));
                }
            }

            var primaryRoot = string.IsNullOrEmpty(upilotSkillPath)
                ? ""
                : Path.GetDirectoryName(upilotSkillPath) ?? "";
            if (!string.IsNullOrEmpty(primaryRoot) &&
                !visibleRoots.Exists(path => string.Equals(path, primaryRoot, StringComparison.OrdinalIgnoreCase)))
            {
                visibleRoots.Insert(0, primaryRoot);
            }

            var tools = string.IsNullOrEmpty(upilotSkillPath) || !Directory.Exists(upilotSkillPath)
                ? Array.Empty<string>()
                : CollectSkillToolNames(upilotSkillPath);
            var installedNames = new List<string>(names).ToArray();
            var conflictSummary = upilotPaths.Count > 1 && upilotHashes.Count > 1
                ? $"发现 {upilotPaths.Count} 个同名 UPilot Skill，且内容 SHA256 不一致。请同步或移除冲突副本。"
                : "";
            return new SkillInventory(
                visibleRoots.ToArray(),
                installedNames,
                Array.Exists(
                    installedNames,
                    name => string.Equals(name, SkillName, StringComparison.OrdinalIgnoreCase)) ? 1 : 0,
                InferSkillCapabilities(tools),
                tools.Length,
                TakeFirst(tools, 10),
                upilotPaths.ToArray(),
                conflictSummary);
        }

        private static string ReadSkillName(string skillFile, string fallback)
        {
            try
            {
                var text = File.ReadAllText(skillFile, Encoding.UTF8);
                var match = Regex.Match(text, "(?m)^name:\\s*([^\\r\\n]+)\\s*$");
                if (match.Success)
                    return match.Groups[1].Value.Trim().Trim('\'', '"');
            }
            catch
            {
            }
            return fallback ?? "(unnamed)";
        }

        private static string[] CollectSkillToolNames(string skillPath)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(skillPath, "*.md", SearchOption.AllDirectories);
            Array.Sort(files, (left, right) =>
            {
                var leftPrimary = string.Equals(Path.GetFileName(left), "SKILL.md", StringComparison.OrdinalIgnoreCase);
                var rightPrimary = string.Equals(Path.GetFileName(right), "SKILL.md", StringComparison.OrdinalIgnoreCase);
                if (leftPrimary != rightPrimary)
                    return leftPrimary ? -1 : 1;
                return StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            foreach (var file in files)
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var matches = Regex.Matches(text, "(?<![A-Za-z0-9_])(unity_[a-z0-9_]+|reflection_eval)(?![A-Za-z0-9_])");
                foreach (Match match in matches)
                {
                    var name = match.Groups[1].Value;
                    if (seen.Add(name))
                        result.Add(name);
                }
            }
            return result.ToArray();
        }

        private static string[] InferSkillCapabilities(string[] tools)
        {
            var result = new List<string>();
            AddCapabilityIfAny(result, tools, "连接与工具发现", "mcp", "ensure", "capabilities", "tools_find", "type_exists");
            AddCapabilityIfAny(result, tools, "脚本与编译", "script", "compile", "sync_after_disk_write");
            AddCapabilityIfAny(result, tools, "测试、构建与长任务", "test", "build", "operation", "task");
            AddCapabilityIfAny(result, tools, "场景与对象", "scene", "gameobject", "component", "prefab", "selection");
            AddCapabilityIfAny(result, tools, "资源与渲染", "asset", "material", "shader", "texture", "package");
            AddCapabilityIfAny(result, tools, "截图与窗口验收", "screenshot", "editor_window", "verify_window");
            AddCapabilityIfAny(result, tools, "诊断与故障恢复", "console", "hang", "profiler", "navmesh", "monohook");
            return result.ToArray();
        }

        private static void AddCapabilityIfAny(
            List<string> result,
            string[] tools,
            string label,
            params string[] markers)
        {
            foreach (var tool in tools ?? Array.Empty<string>())
            {
                foreach (var marker in markers)
                {
                    if (tool.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    result.Add(label);
                    return;
                }
            }
        }

        private static string[] TakeFirst(string[] values, int count)
        {
            if (values == null || values.Length == 0 || count <= 0)
                return Array.Empty<string>();
            var length = Math.Min(values.Length, count);
            var result = new string[length];
            Array.Copy(values, result, length);
            return result;
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

        private static int ReadManagedRuleVersion(string path)
        {
            if (!File.Exists(path))
                return 0;

            var text = File.ReadAllText(path, Encoding.UTF8);
            var pattern = Regex.Escape(ManagedBlockStart) + ".*?" + Regex.Escape(ManagedBlockEnd);
            var managedBlock = Regex.Match(text, pattern, RegexOptions.Singleline);
            if (!managedBlock.Success)
                return 0;

            var version = Regex.Match(managedBlock.Value, "(?m)^rulesVersion:\\s*(\\d+)\\s*$");
            return version.Success && int.TryParse(version.Groups[1].Value, out var parsed)
                ? parsed
                : 0;
        }

        private static string ComputeFilesHash(string[] paths)
        {
            var content = new StringBuilder();
            foreach (var path in paths ?? Array.Empty<string>())
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    var pattern = Regex.Escape(ManagedBlockStart) + ".*?" + Regex.Escape(ManagedBlockEnd);
                    var managedBlock = Regex.Match(text, pattern, RegexOptions.Singleline);
                    if (managedBlock.Success)
                        content.Append(managedBlock.Value);
                }
                content.Append("\n---\n");
            }

            return ComputeTextHash(content.ToString());
        }

        private static string BuildExpectedManagedContent(string[] expectedContents)
        {
            var content = new StringBuilder();
            foreach (var expected in expectedContents ?? Array.Empty<string>())
                content.Append(WrapManagedBlock(expected)).Append("\n---\n");
            return content.ToString();
        }

        private static string ComputeTextHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(NormalizeRuleForComparison(content ?? ""));
            var hash = sha256.ComputeHash(bytes);
            var result = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                result.Append(b.ToString("x2"));
            return result.ToString();
        }

        private static string GetAgentRulesTemplatePath()
        {
            return Path.Combine(ResolvePackageRoot(), "skills", SkillName, AgentRulesTemplateFileName);
        }

        private static string CombineResults(string first, string second)
        {
            var firstHasChanges = !string.IsNullOrWhiteSpace(first) &&
                                  !string.Equals(first, "No changes needed.", StringComparison.Ordinal);
            var secondHasChanges = !string.IsNullOrWhiteSpace(second) &&
                                   !string.Equals(second, "No changes needed.", StringComparison.Ordinal);
            if (!firstHasChanges && !secondHasChanges)
                return "No changes needed.";
            if (!firstHasChanges)
                return second;
            if (!secondHasChanges)
                return first;
            return first.TrimEnd() + "\n" + second.TrimEnd();
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
                return new AgentMcpConfigStatus(
                    clientName,
                    path,
                    true,
                    true,
                    usesCurrentUrl,
                    configuredUrl: urlMatch.Success ? urlMatch.Groups[1].Value : "");
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
                return new AgentMcpConfigStatus(
                    clientName,
                    path,
                    true,
                    true,
                    usesCurrentUrl,
                    configuredUrl: urlMatch.Success ? urlMatch.Groups[1].Value : "");
            }
            catch (Exception ex)
            {
                return new AgentMcpConfigStatus(clientName, path, true, false, false, ex.Message);
            }
        }

        private static AgentMcpConfigStatus InspectOpenCodeConfig(string clientName, string path)
        {
            if (!File.Exists(path))
                return new AgentMcpConfigStatus(clientName, path, false, false, false);

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var masked = MaskJsonComments(text);
                var rootOpen = masked.IndexOf('{');
                if (rootOpen < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "OpenCode 配置缺少根对象");
                var rootClose = FindMatchingBrace(masked, rootOpen);
                if (rootClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "OpenCode 配置根对象格式无效");

                var mcpMatch = FindDirectJsonProperty(masked, rootOpen, rootClose, "mcp");
                if (!mcpMatch.Success)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false);
                var mcpOpen = FindJsonObjectValueOpen(masked, mcpMatch);
                if (mcpOpen < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "OpenCode mcp 配置格式无效");
                var mcpClose = FindMatchingBrace(masked, mcpOpen);
                if (mcpClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false, "OpenCode mcp 配置格式无效");

                var upilotMatch = FindDirectJsonProperty(masked, mcpOpen, mcpClose, "upilot");
                if (!upilotMatch.Success)
                    return new AgentMcpConfigStatus(clientName, path, true, false, false);
                var upilotOpen = FindJsonObjectValueOpen(masked, upilotMatch);
                if (upilotOpen < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "OpenCode UPilot 配置格式无效");
                var upilotClose = FindMatchingBrace(masked, upilotOpen);
                if (upilotClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "OpenCode UPilot 配置格式无效");

                var body = masked.Substring(upilotOpen, upilotClose - upilotOpen + 1);
                var urlMatch = Regex.Match(body, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                var typeMatch = Regex.Match(body, "\"type\"\\s*:\\s*\"([^\"]+)\"");
                var enabledMatch = Regex.Match(body, "\"enabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                var timeoutMatch = Regex.Match(body, "\"timeout\"\\s*:\\s*(\\d+)");
                var configuredUrl = urlMatch.Success ? urlMatch.Groups[1].Value : "";
                var usesCurrentUrl = urlMatch.Success &&
                                     string.Equals(configuredUrl, McpUrl, StringComparison.OrdinalIgnoreCase);
                var issues = new List<string>();
                if (!typeMatch.Success || !string.Equals(typeMatch.Groups[1].Value, "remote", StringComparison.OrdinalIgnoreCase))
                    issues.Add("连接类型需设为 remote");
                if (enabledMatch.Success && string.Equals(enabledMatch.Groups[1].Value, "false", StringComparison.OrdinalIgnoreCase))
                    issues.Add("配置当前已禁用");
                if (!timeoutMatch.Success || !int.TryParse(timeoutMatch.Groups[1].Value, out var timeout) || timeout < OpenCodeMcpTimeoutMs)
                    issues.Add($"工具发现超时需至少为 {OpenCodeMcpTimeoutMs} ms");

                return new AgentMcpConfigStatus(
                    clientName,
                    path,
                    true,
                    true,
                    usesCurrentUrl,
                    configuredUrl: configuredUrl,
                    configurationIssue: string.Join("；", issues));
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

        private static string WriteOpenCodeJsonMcpConfig(string path, bool promptBeforeOverwrite)
        {
            if (!File.Exists(path))
            {
                EnsureParentDirectory(path);
                File.WriteAllText(path, BuildOpenCodeMcpJson(), new UTF8Encoding(false));
                return "Wrote " + NormalizePathForLog(path);
            }

            if (promptBeforeOverwrite)
            {
                var ok = EditorUtility.DisplayDialog(
                    "Update OpenCode UPilot MCP config?",
                    "This will update only the mcp.upilot entry in:\n\n" + path,
                    "Update",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            if (!TryUpsertOpenCodeMcpServer(original, out var updated, out var error))
                throw new InvalidDataException("OpenCode 配置未更新：" + error);
            if (string.Equals(original, updated, StringComparison.Ordinal))
                return "Kept existing " + NormalizePathForLog(path);

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

        private static void CopyAllSkillInstalls(string projectRoot, bool overwriteExisting, StringBuilder result)
        {
            var installedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clientName in new[] { "Codex", "Claude Code", "Cursor", "OpenCode" })
            {
                var target = GetAgentSkillInstallPath(projectRoot, clientName);
                if (string.IsNullOrEmpty(target) || !installedTargets.Add(target))
                    continue;
                CopySkillInstall(target, clientName, overwriteExisting, result);
            }
        }

        private static void CopySkillInstall(
            string target,
            string clientName,
            bool overwriteExisting,
            StringBuilder result)
        {
            var source = Path.Combine(ResolvePackageRoot(), "skills", SkillName);
            LogAgentRulesFileProcessing(
                "Begin Skill install",
                target,
                source,
                $"client={clientName}; targetExists={Directory.Exists(target)}; sourceExists={Directory.Exists(source)}; overwriteExisting={overwriteExisting}; templateVersion={SkillInstallTemplateVersion}");
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

        private static string BuildOpenCodeMcpJson()
        {
            return "{\n" +
                   "  \"$schema\": \"https://opencode.ai/config.json\",\n" +
                   "  \"mcp\": {\n" +
                   BuildOpenCodeMcpServerEntry(4) + "\n" +
                   "  }\n" +
                   "}\n";
        }

        private static string BuildOpenCodeMcpServerEntry(int indentSpaces)
        {
            var indent = new string(' ', indentSpaces);
            var inner = new string(' ', indentSpaces + 2);
            return indent + "\"upilot\": {\n" +
                   inner + "\"type\": \"remote\",\n" +
                   inner + $"\"url\": \"{McpUrl}\",\n" +
                   inner + "\"enabled\": true,\n" +
                   inner + $"\"timeout\": {OpenCodeMcpTimeoutMs}\n" +
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

        private static bool TryUpsertOpenCodeMcpServer(string original, out string updated, out string error)
        {
            updated = original;
            error = "";
            if (string.IsNullOrWhiteSpace(original))
            {
                updated = BuildOpenCodeMcpJson();
                return true;
            }

            var masked = MaskJsonComments(original);
            var rootOpen = masked.IndexOf('{');
            if (rootOpen < 0)
            {
                error = "缺少 JSON 根对象";
                return false;
            }
            var rootClose = FindMatchingBrace(masked, rootOpen);
            if (rootClose < 0)
            {
                error = "JSON 根对象未闭合";
                return false;
            }

            var mcpMatch = FindDirectJsonProperty(masked, rootOpen, rootClose, "mcp");
            if (!mcpMatch.Success)
            {
                updated = InsertJsonObjectProperty(
                    original,
                    masked,
                    rootOpen,
                    rootClose,
                    "  \"mcp\": {\n" + BuildOpenCodeMcpServerEntry(4) + "\n  }");
                return true;
            }

            var mcpOpen = FindJsonObjectValueOpen(masked, mcpMatch);
            if (mcpOpen < 0)
            {
                error = "mcp 必须是 JSON 对象";
                return false;
            }
            var mcpClose = FindMatchingBrace(masked, mcpOpen);
            if (mcpClose < 0)
            {
                error = "mcp 对象未闭合";
                return false;
            }

            var upilotMatch = FindDirectJsonProperty(masked, mcpOpen, mcpClose, "upilot");
            if (!upilotMatch.Success)
            {
                var indent = GetLineIndentation(original, mcpClose) + "  ";
                updated = InsertJsonObjectProperty(
                    original,
                    masked,
                    mcpOpen,
                    mcpClose,
                    BuildOpenCodeMcpServerEntry(indent.Length));
                return true;
            }

            var upilotOpen = FindJsonObjectValueOpen(masked, upilotMatch);
            if (upilotOpen < 0)
            {
                error = "mcp.upilot 必须是 JSON 对象";
                return false;
            }
            var upilotClose = FindMatchingBrace(masked, upilotOpen);
            if (upilotClose < 0)
            {
                error = "mcp.upilot 对象未闭合";
                return false;
            }

            var replacementStart = GetJsonPropertyLineStart(original, upilotMatch.Index);
            var indentText = original.Substring(replacementStart, upilotMatch.Index - replacementStart);
            updated = original.Substring(0, replacementStart) +
                      BuildOpenCodeMcpServerEntry(indentText.Length) +
                      original.Substring(upilotClose + 1);
            return true;
        }

        private static Match FindDirectJsonProperty(
            string maskedJson,
            int objectOpen,
            int objectClose,
            string propertyName)
        {
            var matches = Regex.Matches(
                maskedJson.Substring(objectOpen + 1, objectClose - objectOpen - 1),
                "\"" + Regex.Escape(propertyName) + "\"\\s*:");
            foreach (Match relative in matches)
            {
                var absoluteIndex = objectOpen + 1 + relative.Index;
                if (GetJsonObjectDepth(maskedJson, objectOpen, absoluteIndex) != 1)
                    continue;
                var regex = new Regex(
                    "\"" + Regex.Escape(propertyName) + "\"\\s*:",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1));
                return regex.Match(maskedJson, absoluteIndex);
            }
            return Match.Empty;
        }

        private static int GetJsonObjectDepth(string maskedJson, int objectOpen, int targetIndex)
        {
            var depth = 0;
            var inString = false;
            var escape = false;
            for (var i = objectOpen; i < targetIndex; i++)
            {
                var c = maskedJson[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth;
        }

        private static int FindJsonObjectValueOpen(string maskedJson, Match propertyMatch)
        {
            if (!propertyMatch.Success)
                return -1;
            var index = propertyMatch.Index + propertyMatch.Length;
            while (index < maskedJson.Length && char.IsWhiteSpace(maskedJson[index]))
                index++;
            return index < maskedJson.Length && maskedJson[index] == '{' ? index : -1;
        }

        private static string InsertJsonObjectProperty(
            string original,
            string masked,
            int objectOpen,
            int objectClose,
            string propertyText)
        {
            var hasProperties = Regex.Matches(masked.Substring(objectOpen + 1, objectClose - objectOpen - 1), "\"(?:\\\\.|[^\"])*\"\\s*:")
                .Cast<Match>()
                .Any(match => GetJsonObjectDepth(masked, objectOpen, objectOpen + 1 + match.Index) == 1);
            var lastSignificant = GetLastSignificantJsonChar(masked, objectOpen + 1, objectClose);
            var prefix = hasProperties && lastSignificant != ',' ? ",\n" : "\n";
            var suffix = "\n" + GetLineIndentation(original, objectClose);
            return original.Insert(objectClose, prefix + propertyText + suffix);
        }

        private static char GetLastSignificantJsonChar(string masked, int start, int end)
        {
            for (var i = end - 1; i >= start; i--)
            {
                if (!char.IsWhiteSpace(masked[i]))
                    return masked[i];
            }
            return '\0';
        }

        private static int GetJsonPropertyLineStart(string text, int propertyStart)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, propertyStart - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            for (var i = lineStart; i < propertyStart; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                    return propertyStart;
            }
            return lineStart;
        }

        private static string GetLineIndentation(string text, int index)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var cursor = lineStart;
            while (cursor < text.Length && cursor < index && (text[cursor] == ' ' || text[cursor] == '\t'))
                cursor++;
            return text.Substring(lineStart, cursor - lineStart);
        }

        private static string MaskJsonComments(string text)
        {
            var chars = (text ?? "").ToCharArray();
            var inString = false;
            var escape = false;
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c != '/' || i + 1 >= chars.Length)
                    continue;
                if (chars[i + 1] == '/')
                {
                    chars[i] = chars[i + 1] = ' ';
                    i += 2;
                    while (i < chars.Length && chars[i] != '\n' && chars[i] != '\r')
                        chars[i++] = ' ';
                    i--;
                }
                else if (chars[i + 1] == '*')
                {
                    chars[i] = chars[i + 1] = ' ';
                    i += 2;
                    while (i + 1 < chars.Length && !(chars[i] == '*' && chars[i + 1] == '/'))
                    {
                        if (chars[i] != '\n' && chars[i] != '\r') chars[i] = ' ';
                        i++;
                    }
                    if (i + 1 < chars.Length)
                        chars[i] = chars[i + 1] = ' ';
                    i++;
                }
            }
            return new string(chars);
        }

        private static int FindMatchingBrace(string text, int openIndex)
        {
            var depth = 0;
            var inString = false;
            var escape = false;
            var inLineComment = false;
            var inBlockComment = false;

            for (var i = openIndex; i < text.Length; i++)
            {
                var c = text[i];
                if (inLineComment)
                {
                    if (c == '\n' || c == '\r') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }
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

                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        inLineComment = true;
                        i++;
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i++;
                        continue;
                    }
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

        private static string ResolveOpenCodeConfigPath(string projectRoot)
        {
            var jsonPath = Path.Combine(projectRoot, "opencode.json");
            var jsoncPath = Path.Combine(projectRoot, "opencode.jsonc");
            return File.Exists(jsonPath) || !File.Exists(jsoncPath) ? jsonPath : jsoncPath;
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
