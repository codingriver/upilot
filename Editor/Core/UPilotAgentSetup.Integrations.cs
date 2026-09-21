// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public enum AgentIntegrationScope { All, Rules, SharedAgents, Skills }

    [Serializable]
    public sealed class AgentIntegrationContext
    {
        public string projectPath, mcpUrl, healthUrl, upilotPackageVersion, parentAgentRulesPath;
    }

    [Serializable]
    public sealed class AgentIntegrationTargetResult
    {
        public string relativePath, path, kind, status = "current", beforeSha256 = "", expectedSha256 = "",
            afterSha256 = "", backupPath = "", recoveryPath = "", error = "", currentBlock = "", recommendedBlock = "";
        public bool needsUpdate, needsBackup, hasUpilotBlock;
        public List<string> reasons = new List<string>();
    }

    [Serializable]
    public sealed class AgentIntegrationResult
    {
        public int schemaVersion = 1, agentRulesVersion, skillPackVersion;
        public bool ok = true, dryRun, changed;
        public string status = "current", templateSource = "", templateSha256 = "", error = "";
        public AgentIntegrationContext renderContext;
        public List<AgentIntegrationTargetResult> targets = new List<AgentIntegrationTargetResult>();
    }

    public static partial class UPilotAgentSetup
    {
        [Serializable]
        private sealed class RuleRecord
        {
            public string relativePath, normalizedManagedSha256, templateSha256;
            public int templateVersion;
            public AgentIntegrationContext renderContext;
        }

        [Serializable]
        private sealed class IntegrationRecords
        {
            public int schemaVersion = 1;
            public List<RuleRecord> rules = new List<RuleRecord>();
        }

        [Serializable] private sealed class PackageVersionRecord { public string version; }
        private static readonly object IntegrationGate = new object();
        private static readonly UTF8Encoding IntegrationUtf8 = new UTF8Encoding(false, true);
        private const string RuleStateRelative = ".upilot/agent-integrations.json";
        private const string CursorPreamble = "---\ndescription: Use UPilot MCP for Unity Editor automation\nalwaysApply: true\n---\n\n";

        public static AgentIntegrationResult SyncAgentIntegrations(
            bool apply = false, AgentIntegrationScope scope = AgentIntegrationScope.All, string trigger = "unity")
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
                return IntegrationFailure(new AgentIntegrationResult { dryRun = !apply }, "template_source_unavailable",
                    "The installed UPilot UPM package could not be resolved; synchronization was not attempted.");
            return SyncAgentIntegrationsAt(GetProjectRoot(), package.resolvedPath, UPilotBridge.Instance.HttpPort,
                apply, scope, trigger);
        }

        // Explicit roots also make the production engine testable without touching the open project.
        internal static AgentIntegrationResult SyncAgentIntegrationsAt(string projectRoot, string packageRoot,
            int httpPort, bool apply, AgentIntegrationScope scope = AgentIntegrationScope.All, string trigger = "unity",
            Action<string, string> testCheckpoint = null)
        {
            var report = new AgentIntegrationResult { dryRun = !apply };
            if (!Monitor.TryEnter(IntegrationGate))
                return IntegrationFailure(report, "busy", "Another integration sync is running.");
            FileStream fileLock = null;
            try
            {
                projectRoot = Path.GetFullPath(projectRoot);
                var source = Path.GetFullPath(Path.Combine(packageRoot, "skills", SkillName));
                report.templateSource = source;
                ValidateIntegrationPath(projectRoot, projectRoot);
                ValidateIntegrationPath(source, source);
                ValidateIntegrationTree(source);
                var manifest = LoadTemplateManifest(source);
                var version = JsonUtility.FromJson<PackageVersionRecord>(
                    File.ReadAllText(Path.Combine(packageRoot, "package.json"), IntegrationUtf8))?.version;
                if (string.IsNullOrEmpty(version) || httpPort < 1 || httpPort > 65535)
                    throw new InvalidDataException("Invalid package version or HTTP port.");
                var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var context = BuildTemplateContext(manifest, projectRoot, version, generatedAt,
                    FindParentAgentRulesRelativePath(projectRoot));
                context["mcpUrl"] = GetMcpUrl(httpPort);
                context["healthUrl"] = GetHealthUrl(httpPort);
                if (context.Values.Any(v => v.Contains("\r") || v.Contains("\n")))
                    throw new InvalidDataException("Template context must be single-line.");
                report.agentRulesVersion = manifest.agentRulesVersion;
                report.skillPackVersion = manifest.skillPackVersion;
                report.templateSha256 = ComputeSkillTemplateHash(source);
                report.renderContext = new AgentIntegrationContext {
                    projectPath = projectRoot, mcpUrl = context["mcpUrl"], healthUrl = context["healthUrl"],
                    upilotPackageVersion = version, parentAgentRulesPath = context["parentAgentRulesPath"] };

                // Materialize and validate every source before touching any official target.
                var body = RenderTemplateStrict(File.ReadAllText(
                    ResolveTemplatePath(source, manifest.templates.agentRules, "agentRules"), IntegrationUtf8),
                    context, "agentRules");
                var skillFiles = ReadIntegrationFiles(source);
                skillFiles.Remove(SkillInstallMetadataFileName);
                foreach (var key in new[] { manifest.templates.skill, manifest.templates.openai })
                    if (TemplateTokenPattern.Matches(File.ReadAllText(Path.Combine(source, key), IntegrationUtf8))
                        .Cast<Match>().Any(m => m.Groups[1].Value == "generatedAt"))
                        throw new InvalidDataException("Skill outputs must not use generatedAt.");
                skillFiles["SKILL.md"] = IntegrationUtf8.GetBytes(RenderTemplateStrict(File.ReadAllText(
                    ResolveTemplatePath(source, manifest.templates.skill, "skill"), IntegrationUtf8), context, "skill"));
                skillFiles["agents/openai.yaml"] = IntegrationUtf8.GetBytes(RenderTemplateStrict(File.ReadAllText(
                    ResolveTemplatePath(source, manifest.templates.openai, "openai"), IntegrationUtf8), context, "openai"));
                var expectedSkillHash = HashIntegrationFiles(skillFiles);
                var skillMetadata = new SkillInstallMetadata {
                    schemaVersion = 2, templateVersion = manifest.skillPackVersion,
                    templateSha256 = report.templateSha256, contentSha256 = expectedSkillHash,
                    renderContext = new SkillInstallRenderContext { projectPath = projectRoot, mcpUrl = context["mcpUrl"],
                        healthUrl = context["healthUrl"], upilotPackageVersion = version }, renderedAt = generatedAt };
                skillFiles[SkillInstallMetadataFileName] = IntegrationUtf8.GetBytes(JsonUtility.ToJson(skillMetadata, true) + "\n");

                var lockPath = Path.Combine(projectRoot, ".upilot", "agent-integrations.lock");
                ValidateIntegrationPath(projectRoot, lockPath);
                if (apply) Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
                if (apply || File.Exists(lockPath))
                {
                    try
                    {
                        fileLock = new FileStream(lockPath, apply ? FileMode.OpenOrCreate : FileMode.Open,
                            FileAccess.ReadWrite, FileShare.ReadWrite);
                        // Same byte-range lock as Python msvcrt.locking / fcntl.lockf.
                        fileLock.Lock(0, 1);
                    }
                    catch (IOException ex) { return IntegrationFailure(report, "busy", ex.Message); }
                }
                var statePath = Path.Combine(projectRoot, RuleStateRelative);
                ValidateIntegrationPath(projectRoot, statePath);
                var records = ReadIntegrationRecords(statePath);
                var specs = new List<(string path, string kind, string body)>();
                if (scope != AgentIntegrationScope.Skills)
                {
                    specs.Add(("AGENTS.md", "rule", body));
                    if (scope != AgentIntegrationScope.SharedAgents)
                    {
                        specs.Add((".cursor/rules/upilot-unity-mcp.mdc", "rule", body));
                        specs.Add(("CLAUDE.md", "rule", "@AGENTS.md\n"));
                    }
                }
                if (scope == AgentIntegrationScope.All || scope == AgentIntegrationScope.Skills)
                {
                    specs.Add((".agents/skills/upilot-unity-mcp", "skill", ""));
                    specs.Add((".claude/skills/upilot-unity-mcp", "skill", ""));
                }
                foreach (var spec in specs)
                {
                    var item = new AgentIntegrationTargetResult {
                        relativePath = spec.path, path = Path.Combine(projectRoot, spec.path), kind = spec.kind };
                    report.targets.Add(item);
                    try
                    {
                        ValidateIntegrationPath(projectRoot, item.path);
                        ValidateIntegrationTree(item.path);
                        var exists = File.Exists(item.path) || Directory.Exists(item.path);
                        var guardHash = ExactIntegrationHash(item.path);
                        byte[] candidate = null;
                        RuleRecord nextRecord = null;
                        if (item.kind == "rule")
                        {
                            if (Directory.Exists(item.path)) throw new IOException("Rule target is a directory.");
                            var original = exists ? File.ReadAllBytes(item.path) : Array.Empty<byte>();
                            item.recommendedBlock = WrapManagedBlock(spec.body).TrimEnd('\n');
                            candidate = ReplaceIntegrationBlock(original, item.recommendedBlock,
                                !exists && spec.path.EndsWith(".mdc") ? CursorPreamble : "", out var current);
                            item.currentBlock = current;
                            item.hasUpilotBlock = current.Length > 0;
                            item.beforeSha256 = exists ? HashIntegrationBytes(original) : "";
                            item.expectedSha256 = HashIntegrationBytes(candidate);
                            var record = records.rules.FirstOrDefault(r => r.relativePath == spec.path);
                            var managedHash = HashIntegrationBytes(IntegrationUtf8.GetBytes(NormalizeIntegrationBlock(current)));
                            var expectedManagedHash = HashIntegrationBytes(IntegrationUtf8.GetBytes(NormalizeIntegrationBlock(item.recommendedBlock)));
                            var contentChanged = !original.SequenceEqual(candidate);
                            var recordCurrent = record != null && record.normalizedManagedSha256 == expectedManagedHash
                                && record.templateVersion == report.agentRulesVersion && record.templateSha256 == report.templateSha256
                                && JsonUtility.ToJson(record.renderContext) == JsonUtility.ToJson(report.renderContext);
                            item.needsUpdate = contentChanged || !recordCurrent;
                            item.needsBackup = exists && contentChanged &&
                                (record == null || record.normalizedManagedSha256 != managedHash || !item.hasUpilotBlock);
                            if (contentChanged) item.reasons.Add(exists ? "managed_block_differs" : "missing");
                            if (!recordCurrent) item.reasons.Add("management_record_differs");
                            nextRecord = new RuleRecord { relativePath = spec.path, normalizedManagedSha256 = expectedManagedHash,
                                templateVersion = report.agentRulesVersion, templateSha256 = report.templateSha256,
                                renderContext = report.renderContext };
                        }
                        else
                        {
                            var metadataValid = TryLoadSkillInstallMetadata(item.path, out var metadata);
                            item.beforeSha256 = Directory.Exists(item.path) ? ComputeSkillInstallHash(item.path) : guardHash;
                            item.expectedSha256 = expectedSkillHash;
                            var clean = metadataValid && metadata.contentSha256 == item.beforeSha256;
                            item.needsBackup = exists && !clean;
                            if (!exists) item.reasons.Add("missing");
                            else if (!clean) item.reasons.Add("unmanaged_or_modified");
                            if (item.beforeSha256 != expectedSkillHash) item.reasons.Add("content_differs");
                            if (!metadataValid || metadata.schemaVersion != 2 || metadata.templateVersion != report.skillPackVersion)
                                item.reasons.Add("version_differs");
                            if (!metadataValid || metadata.templateSha256 != report.templateSha256) item.reasons.Add("template_differs");
                            if (!metadataValid || JsonUtility.ToJson(metadata.renderContext) != JsonUtility.ToJson(skillMetadata.renderContext))
                                item.reasons.Add("context_differs");
                            item.needsUpdate = item.reasons.Count != 0;
                        }
                        item.afterSha256 = item.beforeSha256;
                        if (!item.needsUpdate) continue;
                        item.status = "needs_sync";
                        if (!apply) continue;
                        var work = Path.Combine(projectRoot, ".upilot", "agent-integration-staging", Guid.NewGuid().ToString("N"));
                        ValidateIntegrationPath(projectRoot, work);
                        Directory.CreateDirectory(work);
                        var stage = Path.Combine(work, "candidate");
                        var rollback = Path.Combine(work, "rollback");
                        item.recoveryPath = work;
                        var oldState = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
                        var oldRecordsJson = JsonUtility.ToJson(records);
                        var moved = false;
                        var committed = false;
                        var stateWritten = false;
                        try
                        {
                            if (item.kind == "rule") File.WriteAllBytes(stage, candidate);
                            else
                            {
                                foreach (var file in skillFiles)
                                {
                                    var path = Path.Combine(stage, file.Key);
                                    EnsureParentDirectory(path);
                                    File.WriteAllBytes(path, file.Value);
                                }
                            }
                            VerifyIntegrationCandidate(stage, item, skillMetadata);
                            testCheckpoint?.Invoke("beforeBackup", item.path);
                            if (item.needsBackup)
                                item.backupPath = BackupIntegrationTargetAt(projectRoot, item.path, trigger,
                                    string.Join(",", item.reasons), report);
                            testCheckpoint?.Invoke("beforeCommit", item.path);
                            ValidateIntegrationPath(projectRoot, item.path);
                            ValidateIntegrationTree(item.path);
                            if (ExactIntegrationHash(item.path) != guardHash)
                                throw new IOException("Target changed during synchronization; retry after inspection.");
                            if (item.kind != "rule" || !exists || !File.ReadAllBytes(item.path).SequenceEqual(candidate))
                            {
                                EnsureParentDirectory(item.path);
                                if (exists) { MoveIntegrationTarget(item.path, rollback); moved = true; }
                                MoveIntegrationTarget(stage, item.path);
                                committed = true;
                            }
                            testCheckpoint?.Invoke("afterCommit", item.path);
                            VerifyIntegrationCandidate(item.path, item, skillMetadata);
                            if (nextRecord != null)
                            {
                                records.rules.RemoveAll(r => r.relativePath == spec.path);
                                records.rules.Add(nextRecord);
                                var recordBytes = IntegrationUtf8.GetBytes(JsonUtility.ToJson(records, true) + "\n");
                                if (File.Exists(statePath) != (oldState != null) ||
                                    (oldState != null && !File.ReadAllBytes(statePath).SequenceEqual(oldState)))
                                    throw new IOException("Management records changed during synchronization.");
                                stateWritten = true;
                                WriteIntegrationState(statePath, recordBytes, work);
                                if (!File.ReadAllBytes(statePath).SequenceEqual(recordBytes))
                                    throw new IOException("Management record verification failed.");
                            }
                            item.afterSha256 = item.expectedSha256;
                            item.status = item.needsBackup ? "backed_up_and_synced" : "synced";
                            report.changed = true;
                        }
                        catch
                        {
                            if (committed)
                            {
                                ValidateIntegrationPath(projectRoot, item.path);
                                ValidateIntegrationTree(item.path);
                                RemoveIntegrationTarget(item.path);
                            }
                            if (moved) MoveIntegrationTarget(rollback, item.path);
                            if (stateWritten)
                            {
                                if (oldState == null) File.Delete(statePath);
                                else File.WriteAllBytes(statePath, oldState);
                            }
                            records = JsonUtility.FromJson<IntegrationRecords>(oldRecordsJson);
                            // Keep recovery material if recovery itself throws.
                            throw;
                        }
                        finally
                        {
                            ValidateIntegrationPath(projectRoot, work);
                            ValidateIntegrationTree(work);
                            if (!File.Exists(rollback) && !Directory.Exists(rollback))
                                Directory.Delete(work, true);
                            else if (item.status == "synced" || item.status == "backed_up_and_synced")
                                Directory.Delete(work, true);
                            if (!Directory.Exists(work)) item.recoveryPath = "";
                        }
                    }
                    catch (Exception ex)
                    {
                        item.status = "failed"; item.error = ex.Message; report.ok = false;
                    }
                }
                report.status = !report.ok ? "partial_failure" : report.changed ? "synced" :
                    report.targets.Any(t => t.needsUpdate) ? "needs_sync" : "current";
                if (scope != AgentIntegrationScope.SharedAgents)
                    foreach (var target in report.targets) { target.currentBlock = ""; target.recommendedBlock = ""; }
                return report;
            }
            catch (Exception ex) { return IntegrationFailure(report, "failed", ex.Message); }
            finally { fileLock?.Dispose(); Monitor.Exit(IntegrationGate); }
        }

        private static AgentIntegrationResult IntegrationFailure(AgentIntegrationResult report, string status, string error)
        { report.ok = false; report.status = status; report.error = error; return report; }

        private static string IntegrationSummary(AgentIntegrationResult report)
        {
            var text = report.status + ": " + report.error + "\n" + string.Join("\n", report.targets.Select(t =>
                t.status + " " + t.path + (t.backupPath.Length > 0 ? " (backup: " + t.backupPath + ")" : "") +
                (t.error.Length > 0 ? ": " + t.error : "")));
            if (!report.ok) throw new IOException(text);
            return text.TrimEnd();
        }

        private static string NormalizeIntegrationBlock(string block) =>
            Regex.Replace(block.Replace("\r\n", "\n").Replace('\r', '\n'), @"(?m)^generatedAt:[^\n]*", "generatedAt:").TrimEnd();

        internal static byte[] ReplaceIntegrationBlock(byte[] original, string block, string preamble, out string current)
        {
            // Strict UTF-8 preserves BOM and every outside byte on roundtrip; unknown encodings fail closed.
            var text = IntegrationUtf8.GetString(original);
            var starts = Regex.Matches(text, Regex.Escape(ManagedBlockStart));
            var ends = Regex.Matches(text, Regex.Escape(ManagedBlockEnd));
            current = "";
            if (starts.Count == 0 && ends.Count == 0)
                return IntegrationUtf8.GetBytes(text + (text.Length == 0 ? preamble : text.EndsWith("\n") ? "\n" : "\n\n") + block + "\n");
            if (starts.Count != 1 || ends.Count != 1 || starts[0].Index >= ends[0].Index)
                throw new InvalidDataException("Expected one ordered pair of UPilot managed markers.");
            var end = ends[0].Index + ManagedBlockEnd.Length;
            current = text.Substring(starts[0].Index, end - starts[0].Index);
            if (NormalizeIntegrationBlock(current) == NormalizeIntegrationBlock(block)) return original;
            return IntegrationUtf8.GetBytes(text.Substring(0, starts[0].Index) + block + text.Substring(end));
        }

        private static void ValidateIntegrationPath(string root, string path)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath(path);
            if (!string.Equals(root, path, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Integration path escapes root: " + path);
            for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                    (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Integration path contains a symbolic link or junction: " + cursor);
        }

        private static void ValidateIntegrationTree(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var child in Directory.GetFileSystemEntries(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Integration tree contains a symbolic link or junction: " + child);
                if (Directory.Exists(child)) ValidateIntegrationTree(child);
            }
        }

        private static Dictionary<string, byte[]> ReadIntegrationFiles(string root)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (relative.Split('/').Any(ShouldSkipSkillInstallPath) ||
                    string.Equals(Path.GetFileName(file), SkillInstallMetadataFileName, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(relative, File.ReadAllBytes(file));
            }
            return files;
        }

        private static string HashIntegrationBytes(byte[] bytes)
        { using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }

        private static string HashIntegrationFiles(Dictionary<string, byte[]> files)
        {
            using var stream = new MemoryStream();
            foreach (var entry in files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var name = IntegrationUtf8.GetBytes(entry.Key);
                stream.Write(name, 0, name.Length); stream.WriteByte(0);
                stream.Write(entry.Value, 0, entry.Value.Length); stream.WriteByte(0);
            }
            return HashIntegrationBytes(stream.ToArray());
        }

        private static string ExactIntegrationHash(string path) =>
            File.Exists(path) || Directory.Exists(path) ? ComputeBackupTargetHash(path, out _, out _) : "";

        private static IntegrationRecords ReadIntegrationRecords(string path)
        {
            if (!File.Exists(path)) return new IntegrationRecords();
            try
            {
                var value = JsonUtility.FromJson<IntegrationRecords>(File.ReadAllText(path, IntegrationUtf8));
                return value != null && value.schemaVersion == 1 && value.rules != null ? value : new IntegrationRecords();
            }
            catch { return new IntegrationRecords(); }
        }

        private static void VerifyIntegrationCandidate(string path, AgentIntegrationTargetResult item, SkillInstallMetadata expected)
        {
            var hash = item.kind == "rule" ? HashIntegrationBytes(File.ReadAllBytes(path)) : ComputeSkillInstallHash(path);
            if (hash != item.expectedSha256) throw new IOException("Final content verification failed: " + item.path);
            if (item.kind == "skill" && (!TryLoadSkillInstallMetadata(path, out var metadata) ||
                JsonUtility.ToJson(metadata) != JsonUtility.ToJson(expected)))
                throw new IOException("Final metadata verification failed: " + item.path);
        }

        private static string BackupIntegrationTargetAt(string root, string target, string trigger, string reason, AgentIntegrationResult report)
        {
            var session = Path.Combine(root, ".upilot", "backups", AgentIntegrationBackupDirectory,
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
            ValidateIntegrationPath(root, session);
            var copy = Path.Combine(session, GetBackupRelativeTargetPath(root, target));
            var originalHash = ComputeBackupTargetHash(target, out var count, out var bytes);
            EnsureParentDirectory(copy);
            if (Directory.Exists(target)) CopyDirectoryExact(target, copy); else File.Copy(target, copy);
            if (ComputeBackupTargetHash(copy, out var copiedCount, out var copiedBytes) != originalHash ||
                count != copiedCount || bytes != copiedBytes || ExactIntegrationHash(target) != originalHash)
                throw new IOException("Backup verification failed; target was not changed.");
            var manifest = new AgentIntegrationBackupManifest { trigger = trigger, reason = reason,
                originalTargetPath = target, originalContentSha256 = originalHash, fileCount = count, totalBytes = bytes,
                agentRulesVersion = report.agentRulesVersion, skillPackVersion = report.skillPackVersion,
                backedUpAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
            File.WriteAllText(Path.Combine(session, "manifest.json"), JsonUtility.ToJson(manifest, true) + "\n", IntegrationUtf8);
            return session;
        }

        private static void WriteIntegrationState(string target, byte[] data, string work)
        {
            var candidate = Path.Combine(work, "state.json");
            File.WriteAllBytes(candidate, data);
            if (File.Exists(target)) File.Replace(candidate, target, null);
            else File.Move(candidate, target);
        }
        private static void MoveIntegrationTarget(string source, string target)
        { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }
        private static void RemoveIntegrationTarget(string path)
        { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
    }
}
