using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotDiagnosticModuleTests
    {
        [Test]
        public void InstalledToolchainMetadataIsHashedDeduplicatedAndNotClaimedAsExecuted()
        {
            string folder = Path.GetFullPath(Path.Combine("Library", "UPilotAnalyzerTest-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            try
            {
                string dll = Path.Combine(folder, "Unity.Toolchain.dll");
                byte[] bytes = new byte[] { 1, 2, 3 }; File.WriteAllBytes(dll, bytes);
                var observed = UPilotDiagnosticModuleService.InspectAnalyzerPaths(false, new[] { dll, dll }, folder);
                Assert.That(observed, Has.Length.EqualTo(1));
                Assert.That(observed[0].path, Is.EqualTo(dll));
                using (var sha = SHA256.Create())
                    Assert.That(observed[0].sha256, Is.EqualTo(string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")))));
                Assert.That(observed[0].mvid, Is.Empty); // Not loaded metadata; does not claim analyzer execution.
                Assert.That(observed[0].unityReferenceAssembly, Is.False);
                var restored = UnityEngine.JsonUtility.FromJson<DiagnosticModuleResult>(UnityEngine.JsonUtility.ToJson(
                    new DiagnosticModuleResult { observedUnityToolchainAnalyzers = observed }));
                Assert.That(restored.observedUnityToolchainAnalyzers[0].sha256, Is.EqualTo(observed[0].sha256));
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void ProjectAnalyzerLabelIsRejectedEvenWithNoAssemblyAnalyzerMetadata()
        {
            Assert.That(Assert.Throws<ExecutionContractException>(() => UPilotDiagnosticModuleService.InspectAnalyzerPaths(
                true, Array.Empty<string>(), EditorApplication.applicationContentsPath)).Code,
                Is.EqualTo("MODULE_ANALYZERS_UNSUPPORTED"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("relative.dll")]
        [TestCase("missing.dll")]
        [TestCase("outside.dll")]
        [TestCase("near-prefix.dll")]
        [TestCase("traversal.dll")]
        [TestCase("not-a-dll.txt")]
        public void UnknownProjectAndNearPrefixAnalyzerPathsStayRejected(string kind)
        {
            string folder = Path.GetFullPath(Path.Combine("Library", "UPilotAnalyzerBoundary-" + Guid.NewGuid().ToString("N")));
            string editor = Path.Combine(folder, "EditorData"); Directory.CreateDirectory(editor);
            try
            {
                string path = kind;
                if (kind == "missing.dll") path = Path.Combine(editor, kind);
                else if (kind == "outside.dll") path = Path.Combine(folder, kind);
                else if (kind == "near-prefix.dll") { Directory.CreateDirectory(editor + "Other"); path = Path.Combine(editor + "Other", kind); }
                else if (kind == "traversal.dll") path = Path.Combine(editor, "..", kind);
                else if (kind == "not-a-dll.txt") path = Path.Combine(editor, kind);
                if (!string.IsNullOrEmpty(kind) && kind != "missing.dll" && kind != "relative.dll") File.WriteAllText(path, "not loaded");
                Assert.That(UPilotDiagnosticModuleService.IsUnityToolchainAnalyzer(path, editor), Is.False);
                Assert.That(Assert.Throws<ExecutionContractException>(() => UPilotDiagnosticModuleService.InspectAnalyzerPaths(false, new[] { path }, editor)).Code,
                    Is.EqualTo("MODULE_ANALYZERS_UNSUPPORTED"));
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void RootStatusDoesNotOpenSessionsCompileLoadOrRun()
        {
            var service = new UPilotDiagnosticModuleService(new ExecutionSessionRegistry());
            var first = service.Execute(new DiagnosticModulePayload());
            var second = service.Execute(new DiagnosticModulePayload());
            Assert.That(first.status, Is.EqualTo("available"));
            Assert.That(second.loadedAssemblyCount, Is.EqualTo(first.loadedAssemblyCount));
            Assert.That(second.loadedAssemblyBytes, Is.EqualTo(first.loadedAssemblyBytes));
            Assert.That(second.playSessionId, Is.EqualTo(first.playSessionId));
            Assert.That(second.domainGeneration, Is.EqualTo(first.domainGeneration));
            Assert.That(second.compilerStillRunning, Is.False);
            Assert.That(second.activeInvocation, Is.False);
            Assert.That(second.physicalUnloadSupported, Is.False);
            service.HardClose();
        }

        [TestCase("compile")]
        [TestCase("run")]
        [TestCase("close")]
        public void MutationsRequireExplicitFullSourceOptIn(string action)
        {
            var service = new UPilotDiagnosticModuleService(new ExecutionSessionRegistry());
            Assert.That(Assert.Throws<ExecutionContractException>(() => service.Execute(
                new DiagnosticModulePayload { action = action })).Code, Is.EqualTo("MODULE_OPT_IN_REQUIRED"));
            service.HardClose();
        }

        [TestCase("project")]
        [TestCase("domain")]
        [TestCase("playSession")]
        public void WrongIdentityIsRejectedBeforeCompilerOrEntry(string field)
        {
            var service = new UPilotDiagnosticModuleService(new ExecutionSessionRegistry());
            var status = service.Execute(new DiagnosticModulePayload());
            var payload = new DiagnosticModulePayload { action = "compile", allowFullCSharp = true,
                moduleId = "test", expectedProjectPath = status.projectPath,
                expectedDomainGeneration = status.domainGeneration, expectedPlaySessionId = status.playSessionId };
            if (field == "project") payload.expectedProjectPath += ".other";
            if (field == "domain") payload.expectedDomainGeneration += ".old";
            if (field == "playSession") payload.expectedPlaySessionId += ".old";
            Assert.That(Assert.Throws<ExecutionContractException>(() => service.Execute(payload)).Code,
                Is.EqualTo("MODULE_IDENTITY_MISMATCH"));
            service.HardClose();
        }

        [Test]
        public void ModeExitInvalidatesPlaySessionAndHardResetFencesQueries()
        {
            var service = new UPilotDiagnosticModuleService(new ExecutionSessionRegistry());
            var first = service.Execute(new DiagnosticModulePayload());
            service.OnPlayModeChanged(PlayModeStateChange.ExitingPlayMode);
            var second = service.Execute(new DiagnosticModulePayload());
            Assert.That(second.playSessionId, Is.Not.EqualTo(first.playSessionId));
            Assert.That(second.domainGeneration, Is.EqualTo(first.domainGeneration));
            service.HardClose(); service.HardClose();
            Assert.That(Assert.Throws<ExecutionContractException>(() => service.Execute(new DiagnosticModulePayload())).Code,
                Is.EqualTo("MODULE_LIFETIME_EXPIRED"));
        }

        [Test]
        public void ContextCleanupIsReverseOrderedOnceAndPreservesIndependentErrors()
        {
            var registry = new ExecutionSessionRegistry(); var session = registry.Open("module", 60, 8, 2, 2);
            var context = new DiagnosticModuleContext(session, "project", "play");
            var calls = new List<int>();
            context.RegisterCleanup(() => calls.Add(1));
            context.RegisterCleanup(() => { calls.Add(2); throw new InvalidOperationException("cleanup first"); });
            context.RegisterCleanup(() => { calls.Add(3); throw new InvalidOperationException("cleanup second"); });
            try
            {
                context.Close(true); context.Close(true);
                Assert.That(calls, Is.EqualTo(new[] { 3, 2, 1 }));
                Assert.That(context.CleanupErrors.Length, Is.EqualTo(2));
                Assert.That(context.Cancellation.IsCancellationRequested, Is.True);
                Assert.That(context.IsClosed, Is.True);
                Assert.Throws<InvalidOperationException>(() => context.RegisterCleanup(() => calls.Add(4)));
            }
            finally { registry.Close(session.Id); }
        }

        [Test]
        public void HardCloseSkipsUserCleanupAndFencesContext()
        {
            var registry = new ExecutionSessionRegistry(); var session = registry.Open("hard", 60, 8, 2, 2);
            var context = new DiagnosticModuleContext(session, "project", "play"); int calls = 0;
            context.RegisterCleanup(() => calls++);
            context.Close(false); context.Close(true);
            Assert.That(context.IsClosed, Is.True);
            Assert.That(calls, Is.Zero);
            registry.CloseAll();
        }

        [Test]
        public void ContextResourceLimitDoesNotGrowUnbounded()
        {
            var registry = new ExecutionSessionRegistry(); var session = registry.Open("limit", 60, 8, 2, 2);
            var context = new DiagnosticModuleContext(session, "project", "play");
            try
            {
                for (int i = 0; i < 64; ++i) context.RegisterCleanup(() => { });
                Assert.Throws<InvalidOperationException>(() => context.RegisterCleanup(() => { }));
            }
            finally { context.Close(true); registry.Close(session.Id); }
        }

        [TestCase(typeof(DiagnosticSyncFixture))]
        [TestCase(typeof(DiagnosticAsyncFixture))]
        public void ExactAssemblyEntryAcceptsOnlyOriginalContract(Type type)
        {
            var entry = UPilotDiagnosticModuleService.BindEntry(type.Assembly, type.FullName);
            Assert.That(entry.DeclaringType, Is.SameAs(type));
            Assert.That(entry.GetParameters()[0].ParameterType, Is.SameAs(typeof(DiagnosticModuleContext)));
            Assert.That(entry.IsStatic, Is.True);
        }

        [TestCase(typeof(DiagnosticWrongParameterFixture))]
        [TestCase(typeof(DiagnosticWrongReturnFixture))]
        [TestCase(typeof(DiagnosticOverloadedFixture))]
        [TestCase(typeof(DiagnosticGenericFixture<>))]
        public void AmbiguousGenericAndWrongSignatureEntriesAreRejected(Type type)
        {
            Assert.That(Assert.Throws<ExecutionContractException>(() => UPilotDiagnosticModuleService.BindEntry(type.Assembly, type.FullName)).Code,
                Is.EqualTo("MODULE_ENTRY_SIGNATURE"));
        }

        [Test]
        public void ReopenedRevisionClearsPreviousInvocationProjection()
        {
            var type = typeof(UPilotDiagnosticModuleService).GetNestedType("Module", BindingFlags.NonPublic);
            var module = Activator.CreateInstance(type, true);
            foreach (string field in new[] { "Invoked", "RunTimedOut" }) type.GetField(field).SetValue(module, true);
            foreach (string field in new[] { "Error", "Result", "InvocationId" }) type.GetField(field).SetValue(module, "previous");
            foreach (string field in new[] { "RunDeadline", "RunFinishedAt" }) type.GetField(field).SetValue(module, 1L);
            type.GetField("Revision").SetValue(module, 3);
            typeof(UPilotDiagnosticModuleService).GetMethod("ResetInvocation", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new[] { module });
            foreach (string field in new[] { "Invoked", "RunTimedOut" }) Assert.That(type.GetField(field).GetValue(module), Is.False);
            foreach (string field in new[] { "Error", "Result", "InvocationId" }) Assert.That(type.GetField(field).GetValue(module), Is.EqualTo(""));
            foreach (string field in new[] { "RunDeadline", "RunFinishedAt" }) Assert.That(type.GetField(field).GetValue(module), Is.EqualTo(0L));
            Assert.That(type.GetField("Revision").GetValue(module), Is.EqualTo(3));
        }

        [TestCase("compiler")]
        [TestCase("task")]
        public void PhysicalInFlightWorkCannotBeReplaced(string kind)
        {
            var type = typeof(UPilotDiagnosticModuleService).GetNestedType("Module", BindingFlags.NonPublic);
            var module = Activator.CreateInstance(type, true);
            if (kind == "compiler") type.GetField("BuildPending").SetValue(module, true);
            else type.GetField("Running").SetValue(module, new TaskCompletionSource<string>().Task);
            var method = typeof(UPilotDiagnosticModuleService).GetMethod("RequireReplaceable", BindingFlags.NonPublic | BindingFlags.Static);
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new[] { module }));
            Assert.That(((ExecutionContractException)error.InnerException).Code, Is.EqualTo("MODULE_BUSY"));
        }

        [Test]
        public void MissingTypeIsNotResolvedFromAnotherLoadedAssembly()
        {
            Assert.That(Assert.Throws<ExecutionContractException>(() => UPilotDiagnosticModuleService.BindEntry(
                typeof(DiagnosticModuleContext).Assembly, typeof(DiagnosticSyncFixture).FullName)).Code,
                Is.EqualTo("MODULE_ENTRY_SIGNATURE"));
        }

#pragma warning disable 0618
        [Test]
        public void OnlyExactOwnedCompilerAndPathAreFilteredWithoutStartingBuild()
        {
            var owned = new AssemblyBuilder("Library/UPilotDiagnosticOwned.dll", "unused.cs");
            var unrelated = new AssemblyBuilder("Library/UPilotUnrelated.dll", "unused.cs");
            Assert.That(UPilotDiagnosticModuleService.IsCompilationContext(owned), Is.False);
            using (UPilotDiagnosticModuleService.ObserveCompiler(owned))
            {
                Assert.That(UPilotDiagnosticModuleService.IsCompilationContext(owned), Is.True);
                Assert.That(UPilotDiagnosticModuleService.IsCompilationPath(owned.assemblyPath), Is.True);
                Assert.That(UPilotDiagnosticModuleService.IsCompilationContext(unrelated), Is.False);
                Assert.That(UPilotDiagnosticModuleService.IsCompilationContext(new object()), Is.False);
                Assert.That(owned.status, Is.EqualTo(AssemblyBuilderStatus.NotStarted));
            }
            Assert.That(UPilotDiagnosticModuleService.IsCompilationContext(owned), Is.False);
            Assert.That(UPilotDiagnosticModuleService.IsCompilationPath(owned.assemblyPath), Is.False);
        }

        [Test]
        public void DiagnosticCallbacksCannotOverwriteExistingProjectCompileEvidence()
        {
            var bridge = UPilotBridge.Instance; var compile = bridge.CompileService;
            string operation = compile.CompileOperationId;
            string phase = compile.Phase; bool terminal = compile.Terminal, verified = compile.ErrorsVerified;
            long timestamp = compile.LastCompileVerifiedAt;
            var owned = new AssemblyBuilder("Library/UPilotDiagnosticFilter.dll", "unused.cs");
            using (UPilotDiagnosticModuleService.ObserveCompiler(owned))
            {
                foreach (string name in new[] { "OnCompilationStarted", "OnCompilationFinished" })
                {
                    typeof(UPilotCompileService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(compile, new object[] { owned });
                    typeof(UPilotBridge).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bridge, new object[] { owned });
                }
                typeof(UPilotCompileService).GetMethod("OnAssemblyCompilationFinished", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(compile, new object[] { owned.assemblyPath, new[] { new CompilerMessage { type = CompilerMessageType.Error, message = "diagnostic only" } } });
            }
            Assert.That(compile.CompileOperationId, Is.EqualTo(operation)); Assert.That(compile.Phase, Is.EqualTo(phase));
            Assert.That(compile.Terminal, Is.EqualTo(terminal)); Assert.That(compile.ErrorsVerified, Is.EqualTo(verified));
            Assert.That(compile.LastCompileVerifiedAt, Is.EqualTo(timestamp));
        }
#pragma warning restore 0618
    }
    public static class DiagnosticSyncFixture { public static string Run(DiagnosticModuleContext context, string argsJson) => argsJson; }
    public static class DiagnosticAsyncFixture { public static Task<string> Run(DiagnosticModuleContext context, string argsJson) => Task.FromResult(argsJson); }
    public static class DiagnosticWrongParameterFixture { public static string Run(object context, string argsJson) => argsJson; }
    public static class DiagnosticWrongReturnFixture { public static object Run(DiagnosticModuleContext context, string argsJson) => argsJson; }
    public static class DiagnosticOverloadedFixture
    {
        public static string Run(DiagnosticModuleContext context, string argsJson) => argsJson;
        public static string Run(string argsJson) => argsJson;
    }
    public static class DiagnosticGenericFixture<T> { public static string Run(DiagnosticModuleContext context, string argsJson) => argsJson; }
}
