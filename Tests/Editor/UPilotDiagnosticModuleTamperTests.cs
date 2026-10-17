using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Opt-in real AssemblyBuilder acceptance only. Mutates files owned by this nonce,
    // never Unity/package references, shared sessions, modes or service lifetimes.
    public static class UPilotDiagnosticModuleTamperProbe
    {
        private const string Project = @"D:\upilot\Tests~\UPilotTest";
        [Serializable] public sealed class Artifact { public string role, path, sha256; public long bytes; }
        [Serializable] public sealed class Scenario
        {
            public string name, rejectionCode, candidateSource;
            public DiagnosticModuleResult before, after;
            public bool rejectedBeforeLoad, invocationNotStarted, originalReferenceRestored;
        }
        [Serializable] public sealed class Report
        {
            public string nonce, project, unityVersion, firstError = "", referenceMvid = "", changedMvid = "", loadedMvidAtIdentityCheck = "";
            public int unityPid;
            public bool succeeded, ownedOnly = true, compilerStillRunning;
            public long startedAt, endedAt;
            public Scenario[] scenarios;
            public Artifact[] artifacts;
            public ExecutionSessionCloseResult sessionClose;
            public string[] cleanupErrors;
        }
        internal static void ValidateNonce(string nonce)
        {
            if (nonce == null || nonce.Length != 32 || nonce.Any(c => !"0123456789abcdef".Contains(c)))
                throw new ArgumentException("A fresh lowercase 32-hex nonce is required.");
        }
        public static async Task<string> Run(string nonce)
        {
            ValidateNonce(nonce);
            if (!EditorApplication.isPlaying || EditorApplication.isPaused || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Require stable unpaused PlayMode; this probe never switches modes.");
            string actual = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).TrimEnd('\\', '/');
            if (!string.Equals(actual, Project, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the exact canonical acceptance project is supported.");
            string folder = Path.Combine(Project, "Log", "P0P1", "n01-tamper-" + nonce);
            if (Directory.Exists(folder)) throw new InvalidOperationException("Nonce already used; never replay.");
            Directory.CreateDirectory(folder);
            var report = new Report { nonce = nonce, project = actual, unityVersion = Application.unityVersion,
                unityPid = Process.GetCurrentProcess().Id, startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var artifacts = new List<Artifact>(); var scenarios = new List<Scenario>(); var cleanup = new List<string>();
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("N01-owned-tamper-" + nonce, 180, 8, 2, 8, 8);
            var service = new UPilotDiagnosticModuleService(registry);
            string referencePath = Path.Combine(folder, "OwnedReference.dll"); byte[] originalReference = null;
            bool replacedReference = false;
            var clock = Stopwatch.StartNew();
            try
            {
                string referenceType = "OwnedN01Reference_" + nonce;
                await BuildReference(folder, referencePath, referenceType, clock, report);
                // Own overlay is created before LoadFile. Later writes preserve length: mapped DLLs may reject truncation.
                using (var markerStream = new FileStream(referencePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                { byte[] marker = Encoding.UTF8.GetBytes("owned-reference-marker-" + nonce); markerStream.Write(marker, 0, marker.Length); }
                originalReference = File.ReadAllBytes(referencePath);
                SaveBytes(folder, "reference-original.dll", originalReference, "reference-original", artifacts);
                var assembly = System.Reflection.Assembly.LoadFile(referencePath);
                Check(string.Equals(Path.GetFullPath(assembly.Location), referencePath, StringComparison.OrdinalIgnoreCase), "Owned reference location changed.");
                report.referenceMvid = assembly.ManifestModule.ModuleVersionId.ToString("N");
                var root = service.Execute(new DiagnosticModulePayload());
                string source = "using CodingRiver.UPilot; public static class OwnedTamperEntry { public static string Run(DiagnosticModuleContext context, string argsJson) { return " + referenceType + ".Value.ToString(); } }";
                foreach (string name in new[] { "source-during-compile", "reference-during-compile" })
                {
                    var payload = Payload(root, session.Id, nonce + "-" + (name.StartsWith("source") ? "source" : "reference"), source, referenceType);
                    var before = service.Execute(payload);
                    Check(before.compilationStatus == "compiling" && before.compilerStillRunning, "No live owned compile; do not replay.");
                    var modules = (IDictionary)typeof(UPilotDiagnosticModuleService).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                    object module = modules[payload.moduleId];
                    // Keep this compatibility probe on Unity's own assembly compilation pipeline.
#pragma warning disable 0618
                    var builder = (AssemblyBuilder)module.GetType().GetField("Builder").GetValue(module);
                    string candidateSource = Path.ChangeExtension(builder.assemblyPath, ".cs");
#pragma warning restore 0618
                    string ownedRoot = Path.Combine(Project, ".upilot", "diagnostic-modules") + Path.DirectorySeparatorChar;
                    Check(Path.GetFullPath(candidateSource).StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase), "Candidate source escaped its owned module directory.");
                    byte[] initialSource = File.ReadAllBytes(candidateSource);
                    SaveBytes(folder, name + "-original.cs", initialSource, name + "-source-original", artifacts);
                    if (name.StartsWith("source")) File.AppendAllText(candidateSource, "\n// owned tamper " + nonce + "\n", new UTF8Encoding(false));
                    else
                    {
                        byte[] changed = (byte[])originalReference.Clone(); changed[changed.Length - 1] ^= 1;
                        WriteReferenceInPlace(referencePath, changed);
                        SaveBytes(folder, "reference-content-changed.dll", File.ReadAllBytes(referencePath), "reference-during-compile-changed", artifacts);
                    }
                    artifacts.Add(Observe(candidateSource, name + "-source-final"));
                    payload.action = "status";
                    DiagnosticModuleResult after;
                    do
                    {
                        Check(clock.Elapsed.TotalSeconds < 120, "Original probe observation deadline expired; compiler outcome unknown.");
                        await Task.Delay(30); after = service.Execute(payload);
                    } while (after.compilerStillRunning);
                    string expected = name.StartsWith("source") ? "MODULE_SOURCE_CHANGED" : "MODULE_REFERENCE_CHANGED";
                    Check(after.compilationStatus == "failed" && after.compilationError.Contains(expected), "Candidate was not rejected with the expected tamper diagnostic: " + after.compilationError);
                    Check(after.revision == 0 && after.loadedAssemblyCount == before.loadedAssemblyCount && after.loadedAssemblyBytes == before.loadedAssemblyBytes,
                        "Rejected candidate affected resident DLL count or installed revision.");
                    Check(string.IsNullOrEmpty(after.invocationId) && !after.activeInvocation && !after.sideEffectsMayHaveOccurred,
                        "A rejected candidate was loaded/invoked.");
                    payload.action = "run";
                    string runCode = "";
                    try { service.Execute(payload); } catch (ExecutionContractException ex) { runCode = ex.Code; }
                    Check(runCode == "MODULE_ENTRY_INVALID", "Rejected candidate remained callable.");
                    RestoreReference(referencePath, originalReference);
                    var item = new Scenario { name = name, before = before, after = after, rejectionCode = expected, candidateSource = candidateSource,
                        rejectedBeforeLoad = true, invocationNotStarted = true,
                        originalReferenceRestored = Hash(File.ReadAllBytes(referencePath)) == Hash(originalReference) };
                    Check(item.originalReferenceRestored, "Owned reference restore failed."); scenarios.Add(item);
                    payload.action = "close"; var closed = service.Execute(payload);
                    Check(closed.status == "closed" && closed.cleanupErrors.Length == 0, "Owned module close failed.");
                }
                // MVID replacement remains a valid DLL, but cannot impersonate the already-loaded reference.
                var changedMvid = Guid.NewGuid(); report.changedMvid = changedMvid.ToString("N");
                byte[] changedImage = ReplaceMvid(originalReference, assembly.ManifestModule.ModuleVersionId, changedMvid);
                string changedPath = Path.Combine(folder, "reference-changed-mvid.dll");
                SaveBytes(folder, "reference-changed-mvid.dll", changedImage, "reference-mvid-changed", artifacts);
                ReplaceReferenceFile(referencePath, changedImage); replacedReference = true;
                report.loadedMvidAtIdentityCheck = assembly.ManifestModule.ModuleVersionId.ToString("N");
                Check(report.loadedMvidAtIdentityCheck == report.referenceMvid, "Owned loaded MVID changed too; no disk-only mismatch was established.");
                var identityBefore = service.Execute(new DiagnosticModulePayload());
                var identityPayload = Payload(root, session.Id, nonce + "-mvid", source, referenceType);
                string code = "";
                try { service.Execute(identityPayload); } catch (ExecutionContractException ex) { code = ex.Code; }
                Check(code == "MODULE_REFERENCE_IDENTITY", "Changed reference MVID was not rejected before compilation.");
                identityPayload.action = "status";
                string missingCode = "";
                try { service.Execute(identityPayload); } catch (ExecutionContractException ex) { missingCode = ex.Code; }
                Check(missingCode == "MODULE_NOT_FOUND", "Identity mismatch created a candidate.");
                var identityAfter = service.Execute(new DiagnosticModulePayload());
                Check(!identityAfter.compilerStillRunning && !identityAfter.sideEffectsMayHaveOccurred &&
                    identityAfter.loadedAssemblyCount == identityBefore.loadedAssemblyCount &&
                    identityAfter.loadedAssemblyBytes == identityBefore.loadedAssemblyBytes,
                    "Identity mismatch loaded or started a candidate.");
                ReplaceReferenceFile(referencePath, originalReference); replacedReference = false;
                bool restored = Hash(File.ReadAllBytes(referencePath)) == Hash(originalReference);
                Check(restored, "Owned MVID reference restore failed.");
                scenarios.Add(new Scenario { name = "reference-mvid-before-compile", rejectionCode = code,
                    before = identityBefore, after = identityAfter, rejectedBeforeLoad = true,
                    invocationNotStarted = true, originalReferenceRestored = restored });
            }
            catch (Exception ex) { report.firstError = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                try { if (originalReference != null) { if (replacedReference) ReplaceReferenceFile(referencePath, originalReference); else RestoreReference(referencePath, originalReference); } }
                catch (Exception ex) { cleanup.Add("owned-reference-restore: " + ex.GetType().Name + ": " + ex.Message); }
                try
                {
                    var modules = (IDictionary)typeof(UPilotDiagnosticModuleService).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                    foreach (DictionaryEntry pair in modules)
                        if ((bool)pair.Value.GetType().GetField("BuildPending").GetValue(pair.Value)) report.compilerStillRunning = true;
                    service.HardClose(); report.sessionClose = registry.Close(session.Id);
                    if (!report.sessionClose.closed || report.sessionClose.cleanupErrors.Length != 0 || report.sessionClose.activeAsyncOperations != 0 || report.sessionClose.asyncOperationsStillRunning != 0)
                        cleanup.Add("Owned session cleanup is incomplete; compiler cannot be forcibly stopped.");
                }
                catch (Exception ex) { cleanup.Add("owned-session-close: " + ex.GetType().Name); }
                try { if (File.Exists(referencePath)) artifacts.Add(Observe(referencePath, "reference-restored")); }
                catch (Exception ex) { cleanup.Add("owned-reference-evidence: " + ex.GetType().Name); }
                report.scenarios = scenarios.ToArray(); report.artifacts = artifacts.ToArray(); report.cleanupErrors = cleanup.ToArray();
                report.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                report.succeeded = scenarios.Count == 3 && report.firstError == "" && cleanup.Count == 0 && !report.compilerStillRunning;
            }
            string result = JsonUtility.ToJson(report);
            using (var file = new FileStream(Path.Combine(folder, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { byte[] bytes = Encoding.UTF8.GetBytes(result); file.Write(bytes, 0, bytes.Length); file.Flush(); }
            return result;
        }
        // No truncate, resize or loader bypass. An OS refusal stays a real failed/uncovered matrix.
        internal static void WriteReferenceInPlace(string path, byte[] value)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Check(file.Length == value.Length, "Owned reference size changed; do not resize a mapped DLL.");
                file.Write(value, 0, value.Length); file.Flush();
            }
            Check(Hash(File.ReadAllBytes(path)) == Hash(value), "Owned in-place bytes were not persisted.");
        }
        // Disk-only replacement must not mutate the already mapped loaded metadata.
        // The exact owned adjacent file is never reused or retried on OS refusal.
        internal static void ReplaceReferenceFile(string path, byte[] value)
        {
            string replacement = path + ".replacement";
            bool created = false;
            try
            {
                using (var file = new FileStream(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { created = true; file.Write(value, 0, value.Length); file.Flush(); }
                File.Replace(replacement, path, null);
                Check(Hash(File.ReadAllBytes(path)) == Hash(value), "Owned replacement bytes were not persisted.");
            }
            finally { if (created && File.Exists(replacement)) File.Delete(replacement); }
        }
        private static void RestoreReference(string path, byte[] original)
        {
            if (Hash(File.ReadAllBytes(path)) != Hash(original)) WriteReferenceInPlace(path, original);
        }
        internal static byte[] ReplaceMvid(byte[] original, Guid from, Guid to)
        {
            byte[] old = from.ToByteArray(), replacement = to.ToByteArray(), value = (byte[])original.Clone();
            int found = -1;
            for (int i = 0; i <= value.Length - old.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < old.Length; j++) if (value[i + j] != old[j]) { match = false; break; }
                if (!match) continue;
                Check(found < 0, "Owned MVID bytes are ambiguous; do not modify the image."); found = i;
            }
            Check(found >= 0 && from != to, "A unique distinct owned MVID is required.");
            Array.Copy(replacement, 0, value, found, replacement.Length); return value;
        }
        private static DiagnosticModulePayload Payload(DiagnosticModuleResult root, string session, string id, string source, string referenceType)
            => new DiagnosticModulePayload { action = "compile", allowFullCSharp = true, sessionId = session, moduleId = id,
                expectedProjectPath = root.projectPath, expectedDomainGeneration = root.domainGeneration, expectedPlaySessionId = root.playSessionId,
                source = source, entryType = "OwnedTamperEntry", referenceTypeNames = new[] { typeof(DiagnosticModuleContext).FullName, referenceType } };
#pragma warning disable 0618
        private static async Task BuildReference(string folder, string dll, string type, Stopwatch clock, Report report)
        {
            string source = Path.Combine(folder, "OwnedReference.cs");
            File.WriteAllText(source, "public static class " + type + " { public const int Value = 7; }", new UTF8Encoding(false));
            var builder = new AssemblyBuilder(dll, source) { flags = AssemblyBuilderFlags.EditorAssembly, referencesOptions = ReferencesOptions.UseEngineModules };
            var completion = new TaskCompletionSource<CompilerMessage[]>();
            var scope = UPilotDiagnosticModuleService.ObserveCompiler(builder);
            report.compilerStillRunning = true;
            builder.buildFinished += (_, messages) => { try { report.compilerStillRunning = false; completion.TrySetResult(messages); } finally { scope.Dispose(); } };
            try { if (!builder.Build()) throw new InvalidOperationException("Owned reference build was not accepted; no retry."); }
            catch { report.compilerStillRunning = false; scope.Dispose(); throw; }
            while (!completion.Task.IsCompleted)
            { Check(clock.Elapsed.TotalSeconds < 60, "Owned reference compiler observation expired; no physical stop claimed."); await Task.Delay(30); }
            Check(!(await completion.Task).Any(m => m.type == CompilerMessageType.Error), "Owned reference compilation failed.");
        }
#pragma warning restore 0618
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
        private static Artifact Observe(string path, string role)
        { byte[] bytes = File.ReadAllBytes(path); return new Artifact { role = role, path = Path.GetFullPath(path), bytes = bytes.LongLength, sha256 = Hash(bytes) }; }
        private static void SaveBytes(string folder, string name, byte[] bytes, string role, List<Artifact> artifacts)
        {
            string path = Path.Combine(folder, name);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes, 0, bytes.Length);
            artifacts.Add(Observe(path, role));
        }
    }
    public sealed class UPilotDiagnosticModuleTamperTests
    {
        [Test] public void DiskReplacementChangesOwnedFileWithoutOverwritingExistingCandidate()
        {
            string folder = Path.Combine(Path.GetTempPath(), "UPilotTamper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "owned.dll");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                UPilotDiagnosticModuleTamperProbe.ReplaceReferenceFile(path, new byte[] { 4, 5, 6 });
                CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
                Assert.That(File.Exists(path + ".replacement"), Is.False);
                File.WriteAllBytes(path + ".replacement", new byte[] { 9 });
                Assert.Throws<IOException>(() => UPilotDiagnosticModuleTamperProbe.ReplaceReferenceFile(path, new byte[] { 7, 8, 9 }));
                CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(path + ".replacement"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".replacement")) File.Delete(path + ".replacement");
                Directory.Delete(folder);
            }
        }
        [Test]
        public void AsyncDiagnosticTextPreservesContractCodeIncludingWrappedException()
        {
            var method = typeof(UPilotDiagnosticModuleService).GetMethod("ExceptionText", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (string code in new[] { "MODULE_SOURCE_CHANGED", "MODULE_REFERENCE_CHANGED", "MODULE_REFERENCE_IDENTITY" })
            {
                var error = new ExecutionContractException(code, "owned fixture message");
                string text = (string)method.Invoke(null, new object[] { new TargetInvocationException(error) });
                StringAssert.Contains("[" + code + "]", text); StringAssert.Contains("owned fixture message", text);
            }
            string ordinary = (string)method.Invoke(null, new object[] { new InvalidOperationException("ordinary") });
            Assert.AreEqual("System.InvalidOperationException: ordinary", ordinary);
        }
        [Test]
        public void MvidReplacementPreservesSizeAndDoesNotMutateInput()
        {
            var old = new Guid("00112233-4455-6677-8899-aabbccddeeff");
            var next = new Guid("10213243-5465-7687-98a9-bacbdcedfe0f");
            var original = new byte[] { 90, 91 }.Concat(old.ToByteArray()).Concat(new byte[] { 92, 93 }).ToArray();
            var changed = UPilotDiagnosticModuleTamperProbe.ReplaceMvid(original, old, next);
            Assert.AreEqual(original.Length, changed.Length);
            CollectionAssert.AreEqual(new byte[] { 90, 91 }.Concat(next.ToByteArray()).Concat(new byte[] { 92, 93 }), changed);
            CollectionAssert.AreEqual(old.ToByteArray(), original.Skip(2).Take(16));
            Assert.Throws<InvalidOperationException>(() => UPilotDiagnosticModuleTamperProbe.ReplaceMvid(original.Concat(original).ToArray(), old, next));
        }
        [Test]
        public void InPlaceWriteRefusesSizeChangeWithoutTruncatingOwnedFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "upilot-owned-inplace-" + Guid.NewGuid().ToString("N"));
            byte[] original = { 1, 2, 3 }, changed = { 4, 5, 6 };
            try
            {
                File.WriteAllBytes(path, original);
                Assert.Throws<InvalidOperationException>(() => UPilotDiagnosticModuleTamperProbe.WriteReferenceInPlace(path, new byte[] { 7 }));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                UPilotDiagnosticModuleTamperProbe.WriteReferenceInPlace(path, changed);
                CollectionAssert.AreEqual(changed, File.ReadAllBytes(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        [TestCase(null)] [TestCase("../escape")] [TestCase("0123456789abcdef0123456789abcde")]
        [TestCase("0123456789ABCDEF0123456789ABCDEF")]
        public void UnsafeNonceShapeIsRejectedBeforeFilesOrCompiler(string nonce)
            => Assert.Throws<ArgumentException>(() => UPilotDiagnosticModuleTamperProbe.ValidateNonce(nonce));
        [Test]
        public void RealProbeRefusesEditModeWithoutSwitchingMode()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.ThrowsAsync<InvalidOperationException>(() => UPilotDiagnosticModuleTamperProbe.Run("0123456789abcdef0123456789abcdef"));
            Assert.That(EditorApplication.isPlaying, Is.False);
        }
    }
}
