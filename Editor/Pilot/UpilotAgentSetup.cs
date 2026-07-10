// -----------------------------------------------------------------------
// upilot Editor — Agent discovery and MCP client setup helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    [InitializeOnLoad]
    public static class UpilotAgentSetup
    {
        private const string PackageName = "io.github.codingriver.upilot";
        private const string SkillName = "upilot-unity-mcp";
        private const string AutoSetupKeyPrefix = "codingriver.upilot.AgentSetup.AutoRulesWritten.";

        public const string McpUrl = "http://127.0.0.1:8011/mcp";
        public const string HealthUrl = "http://127.0.0.1:8011/health";

        static UpilotAgentSetup()
        {
            EditorApplication.delayCall += EnsureAgentRulesOnce;
        }

        [MenuItem("upilot/Agent Setup/Write Agent Rules", false, 310)]
        public static void MenuWriteAgentRules()
        {
            var result = WriteAgentRules(overwriteExisting: false);
            ReportResult("Agent rules", result);
        }

        [MenuItem("upilot/Agent Setup/Write Codex MCP Config", false, 320)]
        public static void MenuWriteCodexMcpConfig()
        {
            var result = WriteCodexMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Codex MCP config", result);
        }

        [MenuItem("upilot/Agent Setup/Write Claude Code MCP Config", false, 321)]
        public static void MenuWriteClaudeCodeMcpConfig()
        {
            var result = WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Claude Code MCP config", result);
        }

        [MenuItem("upilot/Agent Setup/Write Cursor MCP Config", false, 322)]
        public static void MenuWriteCursorMcpConfig()
        {
            var result = WriteCursorMcpConfig(promptBeforeOverwrite: true);
            ReportResult("Cursor MCP config", result);
        }

        public static string WriteAgentRules(bool overwriteExisting)
        {
            var projectRoot = GetProjectRoot();
            var result = new StringBuilder();

            WriteTextFile(
                Path.Combine(projectRoot, "AGENTS.md"),
                BuildAgentsMd(),
                overwriteExisting,
                result);

            WriteTextFile(
                Path.Combine(projectRoot, "CLAUDE.md"),
                "@AGENTS.md\n",
                overwriteExisting,
                result);

            WriteTextFile(
                Path.Combine(projectRoot, ".cursor", "rules", "upilot-unity-mcp.mdc"),
                BuildCursorRule(),
                overwriteExisting,
                result);

            CopySkillInstall(projectRoot, overwriteExisting, result);

            return result.Length == 0 ? "No changes needed." : result.ToString().TrimEnd();
        }

        public static string WriteCodexMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".codex", "config.toml");
            return WriteExplicitConfig(path, BuildCodexConfig(), promptBeforeOverwrite);
        }

        public static string WriteClaudeCodeMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".mcp.json");
            return WriteExplicitConfig(path, BuildMcpJson(includeType: true), promptBeforeOverwrite);
        }

        public static string WriteCursorMcpConfig(bool promptBeforeOverwrite)
        {
            var path = Path.Combine(GetProjectRoot(), ".cursor", "mcp.json");
            return WriteExplicitConfig(path, BuildMcpJson(includeType: false), promptBeforeOverwrite);
        }

        private static void EnsureAgentRulesOnce()
        {
            try
            {
                var key = AutoSetupKeyPrefix + StableHash(GetProjectRoot());
                if (EditorPrefs.GetBool(key, false))
                    return;

                var result = WriteAgentRules(overwriteExisting: false);
                EditorPrefs.SetBool(key, true);

                if (!string.Equals(result, "No changes needed.", StringComparison.Ordinal))
                    Debug.Log("[upilot] Agent discovery rules installed:\n" + result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[upilot] Agent discovery setup failed: " + ex.Message);
            }
        }

        private static string WriteExplicitConfig(string path, string content, bool promptBeforeOverwrite)
        {
            if (File.Exists(path) && promptBeforeOverwrite)
            {
                var ok = EditorUtility.DisplayDialog(
                    "Write upilot MCP config?",
                    "This will replace the existing file:\n\n" + path,
                    "Replace",
                    "Cancel");
                if (!ok)
                    return "Cancelled.";
            }

            EnsureParentDirectory(path);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return "Wrote " + NormalizePathForLog(path);
        }

        private static void WriteTextFile(string path, string content, bool overwriteExisting, StringBuilder result)
        {
            if (File.Exists(path) && !overwriteExisting)
            {
                result.AppendLine("Kept existing " + NormalizePathForLog(path));
                return;
            }

            EnsureParentDirectory(path);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            result.AppendLine("Wrote " + NormalizePathForLog(path));
        }

        private static void CopySkillInstall(string projectRoot, bool overwriteExisting, StringBuilder result)
        {
            var target = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            if (Directory.Exists(target) && !overwriteExisting)
            {
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
            result.AppendLine("Wrote " + NormalizePathForLog(target));
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
            return "# upilot Unity MCP\n\n" +
                   "This Unity project has the `io.github.codingriver.upilot` UPM package installed.\n\n" +
                   "Use the upilot MCP server for Unity Editor automation when available.\n\n" +
                   "MCP endpoints:\n\n" +
                   "- Streamable HTTP: `http://127.0.0.1:8011/mcp`\n" +
                   "- Health check: `http://127.0.0.1:8011/health`\n\n" +
                   "Do not configure MCP clients to use port `8765` directly. Port `8765` is the internal Unity bridge WebSocket.\n\n" +
                   "Before Unity Editor mutations:\n\n" +
                   "- Check MCP status.\n" +
                   "- Verify the Unity project path.\n" +
                   "- Prefer upilot MCP tools over manual Unity YAML edits.\n";
        }

        private static string BuildCursorRule()
        {
            return "---\n" +
                   "description: Use upilot MCP for Unity Editor automation\n" +
                   "alwaysApply: true\n" +
                   "---\n\n" +
                   BuildAgentsMd();
        }

        private static string BuildFallbackSkill()
        {
            return "---\n" +
                   "name: upilot-unity-mcp\n" +
                   "description: Unity Editor automation through the upilot MCP server.\n" +
                   "---\n\n" +
                   "# upilot Unity MCP\n\n" +
                   "Use the MCP endpoint `http://127.0.0.1:8011/mcp` when working in this Unity project.\n" +
                   "Do not use port `8765` as an MCP client URL; it is the internal Unity bridge WebSocket.\n";
        }

        private static string BuildCodexConfig()
        {
            return "[mcp_servers.upilot]\n" +
                   "url = \"http://127.0.0.1:8011/mcp\"\n" +
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
                   "      \"url\": \"http://127.0.0.1:8011/mcp\"\n" +
                   "    }\n" +
                   "  }\n" +
                   "}\n";
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
