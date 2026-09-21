using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotAgentIntegrationTests
    {
        [Serializable] private sealed class Entry { public string path, content; }
        [Serializable] private sealed class Contract
        {
            public Entry[] files;
            public int port;
            public string expectedSkill, expectedOpenai, templateSha256, contentSha256;
        }
        private string root, project, package;
        private Contract contract;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "upilot-integrations-" + Guid.NewGuid().ToString("N"));
            project = Path.Combine(root, "project");
            package = Path.Combine(root, "package");
            Directory.CreateDirectory(project);
            var packageRoot = PackageInfo.FindForAssetPath("Packages/io.github.codingriver.upilot/package.json").resolvedPath;
            contract = JsonUtility.FromJson<Contract>(File.ReadAllText(
                Path.Combine(packageRoot, "Tests/Fixtures/agent-integrations-contract.json")));
            foreach (var file in contract.files)
            {
                var path = Path.Combine(package, file.path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, Utf8.GetBytes(file.content));
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private AgentIntegrationResult Sync(bool apply = true, AgentIntegrationScope scope = AgentIntegrationScope.All,
            Action<string, string> checkpoint = null) =>
            UPilotAgentSetup.SyncAgentIntegrationsAt(project, package, contract.port, apply, scope, "test", checkpoint);

        [UnityTest]
        public IEnumerator BridgePreviewFromWorkerThreadResolvesInstalledPackageOnEditorThread()
        {
            var service = new UPilotAgentIntegrationService(UPilotBridge.Instance);
            var pending = Task.Run(() => service.EvaluateAsync("integration-thread-test", false, "all",
                "agent.integrations.check", CancellationToken.None));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(pending.IsCompleted, Is.True, "Bridge main-thread dispatch did not complete.");
            Assert.That(pending.IsFaulted, Is.False, pending.Exception?.ToString());
            Assert.That(pending.Result.ok, Is.True, pending.Result.error);
            Assert.That(pending.Result.dryRun, Is.True);
            Assert.That(pending.Result.targets.Count, Is.EqualTo(5));
            Assert.That(pending.Result.templateSource, Does.EndWith(Path.Combine("skills", "upilot-unity-mcp")));
        }

        [Test]
        public void PreviewIsReadOnlyAndFirstInstallMatchesSharedGoldenContract()
        {
            var preview = Sync(false);
            Assert.That(preview.ok, Is.True, preview.error);
            Assert.That(preview.targets.Count, Is.EqualTo(5));
            Assert.That(Directory.GetFileSystemEntries(project), Is.Empty);
            var result = Sync();
            Assert.That(result.ok, Is.True, result.error + string.Join("\n", result.targets.Select(t => t.error)));
            Assert.That(result.templateSha256, Is.EqualTo(contract.templateSha256));
            foreach (var target in result.targets.Where(t => t.kind == "skill"))
            {
                Assert.That(target.afterSha256, Is.EqualTo(contract.contentSha256));
                Assert.That(File.ReadAllBytes(Path.Combine(target.path, "SKILL.md")), Is.EqualTo(Utf8.GetBytes(contract.expectedSkill)));
                Assert.That(File.ReadAllBytes(Path.Combine(target.path, "agents/openai.yaml")), Is.EqualTo(Utf8.GetBytes(contract.expectedOpenai)));
            }
        }

        [Test]
        public void RepeatedSyncIsNoOpIncludingTimestampsAndBackups()
        {
            Assert.That(Sync().ok, Is.True);
            var files = Directory.GetFiles(project, "*", SearchOption.AllDirectories);
            var times = files.ToDictionary(p => p, File.GetLastWriteTimeUtc);
            var result = Sync();
            Assert.That(result.status, Is.EqualTo("current"), result.error);
            foreach (var file in files) Assert.That(File.GetLastWriteTimeUtc(file), Is.EqualTo(times[file]));
            Assert.That(Directory.Exists(Path.Combine(project, ".upilot/backups")), Is.False);
        }

        [Test]
        public void OldCustomAgentIsBackedUpAndOutsideBytesAndBomArePreserved()
        {
            var prefix = "\ufeff# business\r\n \t\r\n";
            var suffix = "\r\n \r\nend  ";
            var original = Utf8.GetBytes(prefix + "<!-- upilot:start -->\nrulesVersion: 1\ncustom\n<!-- upilot:end -->" + suffix);
            var path = Path.Combine(project, "AGENTS.md");
            File.WriteAllBytes(path, original);
            var result = Sync();
            Assert.That(result.ok, Is.True, result.error);
            var target = result.targets[0];
            Assert.That(target.status, Is.EqualTo("backed_up_and_synced"), target.error);
            Assert.That(File.ReadAllBytes(Path.Combine(target.backupPath, "AGENTS.md")), Is.EqualTo(original));
            var updated = Utf8.GetString(File.ReadAllBytes(path));
            Assert.That(updated, Does.StartWith(prefix).And.EndWith(suffix));
        }

        [TestCase("<!-- upilot:start -->")]
        [TestCase("<!-- upilot:end -->")]
        [TestCase("<!-- upilot:end --><!-- upilot:start -->")]
        [TestCase("<!-- upilot:start --><!-- upilot:start --><!-- upilot:end -->")]
        public void MalformedMarkersFailOnlyOneTarget(string original)
        {
            var path = Path.Combine(project, "AGENTS.md");
            File.WriteAllText(path, original, Utf8);
            var result = Sync();
            Assert.That(result.ok, Is.False);
            Assert.That(result.status, Is.EqualTo("partial_failure"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
            Assert.That(result.targets.Skip(1).All(t => t.status == "synced"), Is.True);
        }

        [TestCase("beforeBackup")]
        [TestCase("beforeCommit")]
        [TestCase("afterCommit")]
        public void FailureRestoresTargetAndContinuesIndependentTargets(string phase)
        {
            var path = Path.Combine(project, "AGENTS.md");
            File.WriteAllText(path, "business", Utf8);
            var result = Sync(checkpoint: (step, target) => {
                if (target == path && step == phase) throw new IOException("injected " + phase);
            });
            Assert.That(result.ok, Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("business"));
            Assert.That(result.targets.Last().status, Is.EqualTo("synced"));
        }

        [Test]
        public void ConcurrentEditIsNotOverwritten()
        {
            var path = Path.Combine(project, "AGENTS.md");
            File.WriteAllText(path, "business", Utf8);
            var result = Sync(checkpoint: (step, target) => {
                if (target == path && step == "beforeCommit") File.WriteAllText(path, "concurrent");
            });
            Assert.That(result.ok, Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("concurrent"));
        }

        [Test]
        public void SharedScopeDoesNotWriteOtherFourTargets()
        {
            var result = Sync(scope: AgentIntegrationScope.SharedAgents);
            Assert.That(result.ok, Is.True, result.error);
            Assert.That(result.targets.Count, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(project, "CLAUDE.md")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(project, ".agents")), Is.False);
        }

        [Test]
        public void MetadataAdoptionDoesNotRewriteRule()
        {
            Assert.That(Sync().ok, Is.True);
            var path = Path.Combine(project, "AGENTS.md");
            var time = File.GetLastWriteTimeUtc(path);
            File.Delete(Path.Combine(project, ".upilot/agent-integrations.json"));
            var result = Sync();
            Assert.That(result.ok, Is.True);
            Assert.That(result.targets[0].needsBackup, Is.False);
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
        }

        [TestCase("{{unknown}}")]
        [TestCase("{{bad-token}}")]
        [TestCase("{{generatedAt}}")]
        public void SourceValidationPrecedesEveryTargetWrite(string template)
        {
            File.WriteAllText(Path.Combine(package, "skills/upilot-unity-mcp/SKILL.md.template"), template, Utf8);
            Assert.That(Sync().ok, Is.False);
            Assert.That(Directory.GetFileSystemEntries(project), Is.Empty);
        }

        [Test]
        public void SourceVersionOrPortChangeUpdatesCleanCopiesWithoutBackup()
        {
            Assert.That(Sync().ok, Is.True);
            var result = UPilotAgentSetup.SyncAgentIntegrationsAt(project, package, 8123, true);
            Assert.That(result.ok, Is.True);
            Assert.That(result.targets.All(t => t.needsUpdate && !t.needsBackup), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(project, "AGENTS.md")), Does.Contain(":8123/mcp"));
        }

        [Test]
        public void WindowsOperatingSystemLockIsSharedAcrossFileHandles()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Windows locks are handle-owned; Unix cross-process coverage is in Python.");
            var path = Path.Combine(project, ".upilot/agent-integrations.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Lock(0, 1);
            Assert.That(Sync(false).status, Is.EqualTo("busy"));
            Assert.That(File.Exists(Path.Combine(project, "AGENTS.md")), Is.False);
        }

        [TestCase("missing")]
        [TestCase("duplicatePath")]
        [TestCase("duplicateKey")]
        public void InvalidManifestFailsBeforeTargets(string scenario)
        {
            var source = Path.Combine(package, "skills/upilot-unity-mcp");
            var manifest = Path.Combine(source, "template-manifest.json");
            if (scenario == "missing") File.Delete(Path.Combine(source, "SKILL.md.template"));
            if (scenario == "duplicatePath")
                File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("SKILL.md.template", "AGENTS.md.template"));
            if (scenario == "duplicateKey")
                File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
            Assert.That(Sync().ok, Is.False);
            Assert.That(Directory.GetFileSystemEntries(project), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyV1SkillCleanUpgradeAndCustomizedBackup(bool customize)
        {
            Assert.That(Sync().ok, Is.True);
            var path = Path.Combine(project, ".agents/skills/upilot-unity-mcp");
            File.WriteAllText(Path.Combine(path, ".upilot-install.json"),
                "{\"templateVersion\":34,\"contentSha256\":\"" + contract.contentSha256 + "\"}", Utf8);
            if (customize) File.AppendAllText(Path.Combine(path, "SKILL.md"), "\ncustom");
            var report = Sync();
            Assert.That(report.ok, Is.True, report.error);
            Assert.That(report.targets[3].needsBackup, Is.EqualTo(customize));
            Assert.That(report.targets[3].afterSha256, Is.EqualTo(contract.contentSha256));
        }
    }
}
