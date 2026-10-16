using System;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPlayModeCompilePolicyTests
    {
        private string root;
        [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "upilot-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "Assets")); }
        [TearDown] public void Teardown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [TestCase(false, false, false, "edit", "edit")]
        [TestCase(true, false, true, "play", "play")]
        [TestCase(true, true, true, "play", "pause")]
        [TestCase(false, false, true, "edit", "enteringPlay")]
        [TestCase(true, false, false, "play", "exitingPlay")]
        [TestCase(true, false, true, "exitingPlay", "exitingPlay")]
        public void ModeUsesActualStateAndTransition(bool playing, bool paused, bool pending, string transition, string expected)
        { Assert.That(UPilotPlayModeCompilePolicy.ClassifyMode(playing, paused, pending, transition), Is.EqualTo(expected)); }

        [TestCase("play")][TestCase("pause")][TestCase("enteringPlay")][TestCase("exitingPlay")]
        public void GlobalRefreshAndCompileRefuseBeforeEffects(string mode)
        {
            var error = Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireEditMode("refresh", mode));
            Assert.That(error.Code, Is.EqualTo("EDITOR_IN_PLAY_MODE"));
            Assert.That(error.Detail.blockedReason, Is.EqualTo(mode));
        }

        [TestCase("play")][TestCase("pause")]
        public void EveryPlayWriteReadsPreferenceAndWrongPreferenceRefusesBeforeDisk(string mode)
        {
            int reads = 0, value = 1;
            Func<bool> policy = () => UPilotPlayModeCompilePolicy.RequireScriptWrite(mode, () => { reads++; return value; });
            var result = UPilotScriptService.MutateScript(root, "Assets/Test.cs", "first", "script.create", policy,
                _ => Assert.Fail("Play write imported"));
            Assert.That(result.saved && result.importDeferred && !result.imported, Is.True);
            value = 0;
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotScriptService.MutateScript(root, "Assets/Test.cs", "second", "script.update", policy));
            Assert.That(File.ReadAllText(Path.Combine(root, "Assets/Test.cs")), Is.EqualTo("first"));
            Assert.That(reads, Is.EqualTo(2));
        }

        [TestCase("enteringPlay")][TestCase("exitingPlay")]
        public void QueuedScriptWriteChecksModeWhenExecuted(string mode)
        {
            string actualMode = "edit";
            Action queued = () => UPilotScriptService.MutateScript(root, "Assets/Test.cs", "x", "script.create",
                () => UPilotPlayModeCompilePolicy.RequireScriptWrite(actualMode, () => 1));
            actualMode = mode;
            Assert.Throws<UPilotCompilePolicyException>(() => queued());
            Assert.That(File.Exists(Path.Combine(root, "Assets/Test.cs")), Is.False);
        }

        [Test] public void DeferredDeleteReportsBothPathsWithoutAssetDatabaseOrProjectSync()
        {
            string path = Path.Combine(root, "Assets/Test.cs"); File.WriteAllText(path, "test"); File.WriteAllText(path + ".meta", "meta");
            var result = UPilotScriptService.MutateScript(root, "Assets/Test.cs", null, "script.delete", () => true,
                _ => Assert.Fail("Imported"), _ => { Assert.Fail("AssetDatabase deletion"); return false; },
                () => { Assert.Fail("Project sync"); return false; });
            Assert.That(result.deletedPaths, Is.EqualTo(new[] { "Assets/Test.cs", "Assets/Test.cs.meta" }));
            Assert.That(result.ok && result.deleted && result.importDeferred && !result.projectFilesSynchronized, Is.True);
        }

        [Test] public void PartialDeletePreservesActualEffectsWithoutRollback()
        {
            string path = Path.Combine(root, "Assets/Test.cs"); File.WriteAllText(path, "test"); File.WriteAllText(path + ".meta", "meta");
            var ex = Assert.Throws<UPilotScriptService.ScriptMutationException>(() => UPilotScriptService.MutateScript(root, "Assets/Test.cs", null,
                "script.delete", () => true, deleteFile: file => { if (file.EndsWith(".meta")) throw new IOException("locked meta"); File.Delete(file); }));
            Assert.That(ex.Result.deletedPaths, Is.EqualTo(new[] { "Assets/Test.cs" }));
            Assert.That(ex.Result.deleted && ex.Result.saved && !ex.Result.ok, Is.True);
            Assert.That(File.Exists(path), Is.False); Assert.That(File.Exists(path + ".meta"), Is.True);
        }

        [TestCase("Assets/../Outside.cs")][TestCase("Assets/A/../../Outside.cs")][TestCase("Packages/A.cs")]
        [TestCase("Assets/Test.dll")][TestCase("Assets/C:/Test.cs")]
        public void RejectsUnsafeScriptPaths(string path)
        { Assert.Throws<ArgumentException>(() => UPilotPlayModeCompilePolicy.ValidateScriptPath(root, path)); }

        [TestCase("a.cs")][TestCase("a.asmdef")][TestCase("a.asmref")][TestCase("csc.rsp")][TestCase("a.dll")]
        [TestCase("a.CS.meta")][TestCase("a.dll.meta")]
        public void CodeClassificationIncludesMetaAndExtensions(string path)
        { Assert.That(UPilotPlayModeCompilePolicy.IsCodePath(path), Is.True); }

        [Test] public void DirectoryClassificationUsesDiskIncludingUnimportedNestedFiles()
        {
            var folder = Path.Combine(root, "Assets/Sub"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "data.txt"), "data");
            Assert.That(UPilotPlayModeCompilePolicy.ContainsCodeAsset(folder), Is.False);
            Directory.CreateDirectory(Path.Combine(folder, "Nested"));
            File.WriteAllText(Path.Combine(folder, "Nested/code.asmref.meta"), "meta");
            Assert.That(UPilotPlayModeCompilePolicy.ContainsCodeAsset(folder), Is.True);
        }

        [Test] public void EditWriteDoesNotReadPlayPreferenceAndImportsOnce()
        {
            int imports = 0;
            var result = UPilotScriptService.MutateScript(root, "Assets/Test.cs", "fixed", "script.create",
                () => UPilotPlayModeCompilePolicy.RequireScriptWrite("edit", () => throw new Exception("must not read")), _ => imports++);
            Assert.That(imports, Is.EqualTo(1)); Assert.That(result.imported && !result.importDeferred, Is.True);
        }

        [Test] public void PreferenceReadFailureRefusesBeforeDiskAndKeepsOriginalException()
        {
            LogAssert.Expect(UnityEngine.LogType.Exception, new System.Text.RegularExpressions.Regex("IOException: preference unavailable"));
            var ex = Assert.Throws<UPilotCompilePolicyException>(() => UPilotScriptService.MutateScript(root,
                "Assets/Test.cs", "test", "script.create", () => UPilotPlayModeCompilePolicy.RequireScriptWrite("play",
                    () => throw new IOException("preference unavailable"))));
            Assert.That(ex.Code, Is.EqualTo("PLAYMODE_COMPILATION_POLICY_UNVERIFIED"));
            Assert.That(File.Exists(Path.Combine(root, "Assets/Test.cs")), Is.False);
        }

        [TestCase("play")][TestCase("pause")][TestCase("enteringPlay")][TestCase("exitingPlay")]
        public void GenericAssetGuardsCheckBothPathsAndAllowOrdinaryResources(string mode)
        {
            string ordinary = Path.Combine(root, "Assets/Data.txt"), code = Path.Combine(root, "Assets/Code.cs");
            File.WriteAllText(ordinary, "data");
            UPilotPlayModeCompilePolicy.RequireAssetMutation(mode, path => path, ordinary, ordinary + ".copy");
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireAssetMutation(mode, path => path, ordinary, code));
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireAssetMutation(mode, path => path, code, ordinary));
            File.WriteAllText(code, "unimported");
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireAssetMutation(mode, path => path, Path.Combine(root, "Assets")));
            Assert.That(File.Exists(code), Is.True);
        }

        [Test] public void DirectoryClassificationFailureRefusesRatherThanAssumingOrdinaryAsset()
        {
            LogAssert.Expect(UnityEngine.LogType.Exception, new System.Text.RegularExpressions.Regex("IOException: unreadable directory"));
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireAssetMutation("play",
                path => throw new IOException("unreadable directory"), "Assets/Unknown"));
        }

        [Test] public void ThrowAfterDiskDeletionStillReportsActualDeletedPaths()
        {
            string path = Path.Combine(root, "Assets/Test.cs"); File.WriteAllText(path, "test");
            var ex = Assert.Throws<UPilotScriptService.ScriptMutationException>(() => UPilotScriptService.MutateScript(root,
                "Assets/Test.cs", null, "script.delete", () => true, deleteFile: file => { File.Delete(file); throw new IOException("after deletion"); }));
            Assert.That(ex.Result.deletedPaths, Is.EqualTo(new[] { "Assets/Test.cs" }));
            Assert.That(ex.Result.saved && ex.Result.deleted && !ex.Result.ok, Is.True);
        }
    }
}
