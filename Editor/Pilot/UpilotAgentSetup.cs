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

namespace codingriver.upilot
{
    public readonly struct AgentMcpConfigStatus
    {
        public AgentMcpConfigStatus(
            string clientName,
            string configPath,
            bool fileExists,
            bool hasUpilotEntry,
            bool usesCurrentUrl,
            string errorMessage = "")
        {
            ClientName = clientName;
            ConfigPath = configPath;
            FileExists = fileExists;
            HasUpilotEntry = hasUpilotEntry;
            UsesCurrentUrl = usesCurrentUrl;
            ErrorMessage = errorMessage ?? "";
        }

        public string ClientName { get; }
        public string ConfigPath { get; }
        public bool FileExists { get; }
        public bool HasUpilotEntry { get; }
        public bool UsesCurrentUrl { get; }
        public string ErrorMessage { get; }
        public bool IsConfigured => FileExists && HasUpilotEntry && UsesCurrentUrl && string.IsNullOrEmpty(ErrorMessage);

        public string StateText
        {
            get
            {
                if (!string.IsNullOrEmpty(ErrorMessage)) return "读取失败";
                if (!FileExists) return "未配置";
                if (!HasUpilotEntry) return "缺少 upilot 配置";
                if (!UsesCurrentUrl) return "端口已变化，需更新";
                return "已配置";
            }
        }
    }

    [InitializeOnLoad]
    public static class UpilotAgentSetup
    {
        private const string PackageName = "io.github.codingriver.upilot";
        private const string SkillName = "upilot-unity-mcp";
        private const string AutoSetupKeyPrefix = "codingriver.upilot.AgentSetup.AutoRulesWritten.";
        private const int AgentRulesTemplateVersion = 2;
        private const string ManagedBlockStart = "<!-- upilot:start -->";
        private const string ManagedBlockEnd = "<!-- upilot:end -->";

        public static string McpUrl => GetMcpUrl(UpilotBridge.Instance.HttpPort);
        public static string HealthUrl => GetHealthUrl(UpilotBridge.Instance.HttpPort);

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

        static UpilotAgentSetup()
        {
            EditorApplication.delayCall += EnsureAgentRulesOnce;
        }

        [MenuItem("upilot/Advanced/Agent Setup/Write Agent Rules", false, 310)]
        public static void MenuWriteAgentRules()
        {
            var result = WriteAgentRules(overwriteExisting: false);
            ReportResult("Agent rules", result);
        }

        [MenuItem("upilot/Advanced/Agent Setup/Write Codex MCP Config", false, 320)]
        public static void MenuWriteCodexMcpConfig()
        {
            var result = WriteCodexMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Codex MCP config", result);
        }

        [MenuItem("upilot/Advanced/Agent Setup/Write Claude Code MCP Config", false, 321)]
        public static void MenuWriteClaudeCodeMcpConfig()
        {
            var result = WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Claude Code MCP config", result);
        }

        [MenuItem("upilot/Advanced/Agent Setup/Write Cursor MCP Config", false, 322)]
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

        public static void MarkAgentRulesHandledForCurrentProject()
        {
            EditorPrefs.SetBool(GetAgentRulesSetupKey(), true);
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
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "upilot 配置格式无效");
                var upilotObjectClose = FindMatchingBrace(text, upilotObjectOpen);
                if (upilotObjectClose < 0)
                    return new AgentMcpConfigStatus(clientName, path, true, true, false, "upilot 配置格式无效");

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
                if (!UpilotSetupState.IsCompleted)
                    return;

                var key = GetAgentRulesSetupKey();
                if (EditorPrefs.GetBool(key, false))
                    return;

                var result = WriteAgentRules(overwriteExisting: false);
                MarkAgentRulesHandledForCurrentProject();

                if (!string.Equals(result, "No changes needed.", StringComparison.Ordinal))
                    Debug.Log("[upilot] Agent discovery rules installed:\n" + result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[upilot] Agent discovery setup failed: " + ex.Message);
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
                    "Update upilot MCP config?",
                    "This will update only the upilot MCP server entry in:\n\n" + path,
                    "Update",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertJsonMcpServer(original, includeType);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return "Updated upilot entry in " + NormalizePathForLog(path);
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
                    "Update upilot MCP config?",
                    "This will update only the [mcp_servers.upilot] section in:\n\n" + path,
                    "Update",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var updated = UpsertTomlSection(original, "[mcp_servers.upilot]", content);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return "Updated upilot section in " + NormalizePathForLog(path);
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
            result.AppendLine("Updated upilot block in " + NormalizePathForLog(path));
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
            result.AppendLine("Updated upilot block in " + NormalizePathForLog(path));
        }

        private static void CopySkillInstall(string projectRoot, bool overwriteExisting, StringBuilder result)
        {
            var target = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            if (Directory.Exists(target) && !overwriteExisting)
            {
                RewriteCopiedSkillEndpoint(target);
                result.AppendLine("Kept existing " + NormalizePathForLog(target));
                return;
            }

            var source = Path.Combine(ResolvePackageRoot(), "skills", SkillName);
            if (!Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "SKILL.md"), BuildFallbackSkill(), new UTF8Encoding(false));
                result.AppendLine("Wrote fallback " + NormalizePathForLog(target));
                return;
            }

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            CopyDirectoryWithoutMeta(source, target);
            RewriteCopiedSkillEndpoint(target);
            result.AppendLine("Wrote " + NormalizePathForLog(target));
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
            var unityVersion = Application.unityVersion;
            var unityMajorText = unityVersion.Split('.')[0];
            var isUnity6OrNewer = int.TryParse(unityMajorText, out var unityMajor) && unityMajor >= 6000;

            var text = new StringBuilder();
            text.AppendLine("# upilot Unity MCP");
            text.AppendLine();
            text.AppendLine("This Unity project has the `io.github.codingriver.upilot` UPM package installed.");
            text.AppendLine();
            text.AppendLine("Use the upilot MCP server for Unity Editor inspection, diagnostics, automation, and mutation when available. Its supported areas include Editor state, Console and compile diagnostics, scenes, GameObjects, components, assets, prefabs, packages, tests, builds, screenshots, Editor windows, reflection, and supported UI automation.");
            text.AppendLine();
            text.AppendLine("## Connection and project identity");
            text.AppendLine();
            text.AppendLine("Project MCP endpoints:");
            text.AppendLine();
            text.AppendLine($"- Streamable HTTP: `{mcpUrl}`");
            text.AppendLine($"- Health check: `{healthUrl}`");
            text.AppendLine();
            text.AppendLine("These project-local endpoints and a successful health check take precedence over generic examples that use another HTTP port.");
            text.AppendLine();
            text.AppendLine("MCP clients must not connect directly to port `8765` or any other internal Unity bridge WebSocket port. Internal bridge ports may vary when multiple Unity projects are open; do not hardcode them. Use `unity_mcp_status` and its session/project information to identify the connected Editor.");
            text.AppendLine();
            text.AppendLine("## Standard Unity task workflow");
            text.AppendLine();
            text.AppendLine("For every Unity Editor task:");
            text.AppendLine();
            text.AppendLine("1. Call `unity_mcp_status`.");
            text.AppendLine("2. Require `connected: true` and `serverReady: true`.");
            text.AppendLine($"3. Verify `paths.unityProjectAbsolute` matches `{projectRoot}` (allow equivalent slash normalization).");
            text.AppendLine("4. Stop and report the mismatch if another Unity project is connected.");
            text.AppendLine("5. Call `unity_ensure_ready` before Editor mutations.");
            text.AppendLine("6. Use the narrowest dedicated upilot tool that matches the task.");
            text.AppendLine();
            text.AppendLine("Prefer tools in this order:");
            text.AppendLine();
            text.AppendLine("1. Dedicated semantic tools for scenes, objects, components, assets, prefabs, packages, tests, builds, windows, and screenshots.");
            text.AppendLine("2. Serialized-data tools only when no dedicated tool exposes the required property.");
            text.AppendLine("3. `unity_reflection_call` for existing compiled entry points.");
            text.AppendLine("4. `reflection_eval` for one bounded diagnostic or invocation expression.");
            text.AppendLine("5. Focus-dependent mouse, keyboard, and drag-and-drop tools only after verifying the target window, focus, layout, and coordinates.");
            text.AppendLine("6. Manual Unity YAML editing only as a last resort, preserving GUID and fileID integrity.");
            text.AppendLine();
            text.AppendLine("## Persistent and destructive operations");
            text.AppendLine();
            text.AppendLine("Before deleting, moving, overwriting, unloading, removing, installing, or persistently saving anything:");
            text.AppendLine();
            text.AppendLine("- Read, find, get, or list the exact target first.");
            text.AppendLine("- Confirm the connected project, scene, asset path, GameObject, component, prefab, or package is the intended target.");
            text.AppendLine("- Do not hide destructive operations inside `unity_batch_execute` or automatic retries unless the user approved the complete batch.");
            text.AppendLine("- Use `unity_task_execute` retries only for idempotent operations. Do not automatically retry non-idempotent destructive operations.");
            text.AppendLine("- Save scenes and prefabs only when the requested task requires persistent changes. Scene-only changes are not persistent until explicitly saved.");
            text.AppendLine("- Treat package changes, builds, script writes, asset writes, and saved screenshots as persistent operations.");
            text.AppendLine();
            text.AppendLine("## Writes, compilation, and verification");
            text.AppendLine();
            text.AppendLine("- Read existing scripts or assets before overwriting them and preserve local project conventions.");
            text.AppendLine("- After completing a batch of code or asset disk writes, call `unity_sync_after_disk_write` once for the batch.");
            text.AppendLine("- After C# changes, call `unity_safe_compile_and_wait`, then re-read `unity_compile_errors` and relevant Console logs.");
            text.AppendLine("- Read `unity_compile_errors` before changing files to fix compilation failures.");
            text.AppendLine("- Avoid triggering compilation while Unity is in PlayMode unless the user explicitly requested a PlayMode workflow.");
            text.AppendLine("- For tests and builds, poll their result/status tools until completion; starting an operation is not verification of success.");
            text.AppendLine();
            text.AppendLine("## Tool boundaries");
            text.AppendLine();
            text.AppendLine("- `unity_reflection_call` may call existing compiled methods; it is not a general script runner.");
            text.AppendLine("- `reflection_eval` accepts one bounded expression only. Do not use local declarations, `var`, loops, branches, lambdas/LINQ, async/await, helper definitions, arbitrary object construction, or dynamic compilation.");
            text.AppendLine("- If reflection reports a syntax or capability boundary, do not retry random C# variants. Switch to a dedicated tool, call a stable compiled helper, or ask for an appropriate helper method.");
            text.AppendLine("- Prefer semantic tools over raw input tools. Keyboard, mouse, and drag-and-drop operations interact with the real Unity UI and depend on focus and layout.");
            text.AppendLine("- Screenshot tools are observational except `unity_screenshot_save`, which writes a PNG. Writing outside the project requires explicit user intent.");
            text.AppendLine();
            text.AppendLine("## UIFlow");
            text.AppendLine();
            text.AppendLine("- Use `unity_uiflow_*` only for YAML-driven Unity EditorWindow automation, not for Game View or runtime UI testing.");
            if (isUnity6OrNewer)
                text.AppendLine($"- This project uses Unity `{unityVersion}`. UIFlow still requires `UPILOT_ENABLE_UIFLOW` and its optional packages; verify availability before using it.");
            else
                text.AppendLine($"- UIFlow requires Unity 6+ and `UPILOT_ENABLE_UIFLOW`. This project uses Unity `{unityVersion}`, so prefer core upilot tools.");
            text.AppendLine();
            text.AppendLine("## Failure and recovery");
            text.AppendLine();
            text.AppendLine("- If a tool times out, call `unity_mcp_status`, inspect connection and compile state, and retry at most once through `unity_task_execute` only when the operation is idempotent.");
            text.AppendLine("- If Unity remains disconnected, busy, or connected to the wrong project, stop and report the state instead of continuing with filesystem or UI workarounds.");
            text.AppendLine("- If a dedicated upilot tool reports that a feature is unavailable, respect the boundary and use the documented fallback rather than repeatedly probing unsupported variants.");
            return text.ToString();
        }

        private static string BuildCursorRule()
        {
            return "---\n" +
                   "description: Use upilot MCP for Unity Editor automation\n" +
                   "alwaysApply: true\n" +
                   "---\n\n" +
                   WrapManagedBlock(BuildAgentsMd());
        }

        private static string BuildFallbackSkill()
        {
            return "---\n" +
                   "name: upilot-unity-mcp\n" +
                   "description: Unity Editor automation through the upilot MCP server.\n" +
                   "---\n\n" +
                   BuildAgentsMd();
        }

        private static string BuildCodexConfig()
        {
            return "[mcp_servers.upilot]\n" +
                   $"url = \"{McpUrl}\"\n" +
                   "startup_timeout_sec = 10\n" +
                   "tool_timeout_sec = 60\n";
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
            Debug.Log("[upilot] " + title + ":\n" + result);
            EditorUtility.DisplayDialog("upilot", result, "OK");
        }
    }
}
