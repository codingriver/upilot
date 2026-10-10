using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace CodingRiver.UPilot
{
    // A separate, explicit full-source tool. Eval/Emit policies are deliberately unchanged.
    [Serializable] public sealed class DiagnosticModuleMessage { public DiagnosticModulePayload payload; }
    [Serializable] public sealed class DiagnosticModulePayload
    {
        public string action = "status";
        public string sessionId = "";
        public string moduleId = "";
        public string source = "";
        public string entryType = "";
        public string argsJson = "{}";
        public string expectedProjectPath = "";
        public string expectedDomainGeneration = "";
        public string expectedPlaySessionId = "";
        public string[] referenceTypeNames = Array.Empty<string>();
        public bool allowFullCSharp;
    }
    [Serializable] public sealed class DiagnosticModuleResult
    {
        public string projectPath = "";
        public string domainGeneration = "";
        public string playSessionId = "";
        public string sessionId = "";
        public string moduleId = "";
        public string status = "available";
        public string compilationStatus = "not_started";
        public int revision;
        public int compilationRevision;
        public string sourceSha256 = "";
        public string dllSha256 = "";
        public string assemblyName = "";
        public string entryType = "";
        public string invocationId = "";
        public string result = "";
        public string error = "";
        public string compilationError = "";
        public string[] compilerMessages = Array.Empty<string>();
        public string[] cleanupErrors = Array.Empty<string>();
        public DiagnosticModuleReference[] references = Array.Empty<DiagnosticModuleReference>();
        // Observed compiler metadata, not proof that every listed analyzer executed.
        public DiagnosticModuleReference[] observedUnityToolchainAnalyzers = Array.Empty<DiagnosticModuleReference>();
        public int loadedAssemblyCount;
        public long loadedAssemblyBytes;
        public int maxLoadedAssemblies = 32;
        public long maxLoadedAssemblyBytes = 67108864;
        public bool activeInvocation;
        public bool sideEffectsMayHaveOccurred;
        public bool physicalUnloadSupported;
        public string compiler = "UnityEditor.Compilation.AssemblyBuilder";
        public long compileDeadlineAt;
        public long invocationDeadlineAt;
        public bool compilerStillRunning;
        public string support = "Editor only; project/package Analyzer/Generator unsupported; verified current Unity installation toolchain permitted; existing loaded Mono.Cecil metadata reader required; runtime support unverified until acceptance";
    }
    [Serializable] public sealed class DiagnosticModuleReference
    {
        public string path = "";
        public string assemblyName = "";
        public string mvid = "";
        public string sha256 = "";
        public bool unityReferenceAssembly;
    }

    /// <summary>Trusted diagnostic code, not a sandbox. Register every resource that needs cleanup.</summary>
    public sealed class DiagnosticModuleContext
    {
        private readonly ExecutionSession _session;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<Action> _cleanup = new List<Action>();
        private readonly List<EditorApplication.CallbackFunction> _ticks = new List<EditorApplication.CallbackFunction>();
        private readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;
        private readonly List<string> _errors = new List<string>();
        private bool _closed;
        public string ProjectPath { get; }
        public string DomainGeneration { get; }
        public string PlaySessionId { get; }
        public CancellationToken Cancellation => _cancellation.Token;
        public bool IsClosed => _closed || _session.IsClosed;
        public string[] CleanupErrors => _errors.ToArray();
        internal DiagnosticModuleContext(ExecutionSession session, string project, string playSession)
        {
            _session = session; ProjectPath = project; DomainGeneration = session.DomainGeneration;
            PlaySessionId = playSession;
        }
        public void RegisterCleanup(Action cleanup)
        {
            RequireOpen();
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            if (_cleanup.Count + _ticks.Count >= 64) throw new InvalidOperationException("Module resource limit is 64.");
            _cleanup.Add(cleanup);
        }
        public void Track(IDisposable resource)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            RegisterCleanup(resource.Dispose);
        }
        public void SubscribeEditorUpdate(Action tick)
        {
            RequireOpen();
            if (tick == null) throw new ArgumentNullException(nameof(tick));
            if (_cleanup.Count + _ticks.Count >= 64) throw new InvalidOperationException("Module resource limit is 64.");
            EditorApplication.CallbackFunction gated = () => { if (!IsClosed) tick(); };
            _ticks.Add(gated); EditorApplication.update += gated;
        }
        private void RequireOpen()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                throw new InvalidOperationException("Register resources on the Unity main thread.");
            if (IsClosed) throw new InvalidOperationException("Module context is closed.");
        }
        internal void Close(bool runUserCleanup)
        {
            if (_closed) return;
            _closed = true; // Fence before cancellation, arbitrary cleanup or late callbacks.
            foreach (var tick in _ticks) EditorApplication.update -= tick;
            _ticks.Clear();
            if (!runUserCleanup)
            {
                _cleanup.Clear();
                _ = Task.Run(() => { try { _cancellation.Cancel(); } catch { } });
                return;
            }
            try { _cancellation.Cancel(); } catch (Exception ex) { Record(ex); }
            for (int i = _cleanup.Count - 1; i >= 0; --i)
                try { _cleanup[i](); } catch (Exception ex) { Record(ex); }
            _cleanup.Clear();
        }
        private void Record(Exception ex)
        {
            if (_errors.Count < 32) _errors.Add(UPilotDiagnosticModuleService.Bounded(ex.GetType().FullName + ": " + ex.Message));
        }
    }

#pragma warning disable 0618 // Unity's own independent compiler; no external compiler or dependency install.
    internal sealed class UPilotDiagnosticModuleService
    {
        // Domain lifetime, not service lifetime: a service restart cannot reclaim resident DLL memory.
        private static int _loadedCount;
        private static long _loadedBytes;
        private static readonly HashSet<AssemblyBuilder> Compilers = new HashSet<AssemblyBuilder>();
        private static readonly HashSet<string> CompilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ExecutionSessionRegistry _sessions;
        private readonly Dictionary<string, Module> _modules = new Dictionary<string, Module>(StringComparer.Ordinal);
        private readonly string _project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).TrimEnd('\\', '/');
        private string _playSession = Guid.NewGuid().ToString("N");
        private bool _closed;
        private sealed class Module
        {
            public string Id, SessionId, EntryType, SourceHash, DllHash, Key, AssemblyName;
            public string CompilationStatus = "not_started", CompilationError = "", Error = "", InvocationId = "", Result = "";
            public int Revision, CompilationRevision;
            public string[] Messages = Array.Empty<string>();
            public DiagnosticModuleReference[] References = Array.Empty<DiagnosticModuleReference>();
            public DiagnosticModuleReference[] ObservedAnalyzers = Array.Empty<DiagnosticModuleReference>();
            public MethodInfo Entry;
            public DiagnosticModuleContext Context;
            public Task<string> Running;
            public AssemblyBuilder Builder;
            public bool Invoked, Closed, BuildPending, LoadedAttempt, RunTimedOut;
            public long CompileDeadline, RunDeadline, RunFinishedAt;
        }
        internal UPilotDiagnosticModuleService(ExecutionSessionRegistry sessions) { _sessions = sessions; }
        internal static bool IsCompilationContext(object context) => context is AssemblyBuilder builder && Compilers.Contains(builder);
        internal static bool IsCompilationPath(string path) => CompilePaths.Contains(Path.GetFullPath(path));
        internal static IDisposable ObserveCompiler(AssemblyBuilder builder)
        {
            Compilers.Add(builder); CompilePaths.Add(Path.GetFullPath(builder.assemblyPath));
            return new CompilerScope(builder);
        }
        private sealed class CompilerScope : IDisposable
        {
            private AssemblyBuilder _builder;
            public CompilerScope(AssemblyBuilder builder) { _builder = builder; }
            public void Dispose()
            {
                if (_builder == null) return;
                Compilers.Remove(_builder); CompilePaths.Remove(Path.GetFullPath(_builder.assemblyPath)); _builder = null;
            }
        }
        internal void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode && change != PlayModeStateChange.ExitingPlayMode) return;
            foreach (var m in _modules.Values) m.Context?.Close(true);
            _playSession = Guid.NewGuid().ToString("N");
        }
        internal void HardClose()
        {
            _closed = true;
            foreach (var m in _modules.Values) m.Context?.Close(false);
        }
        internal DiagnosticModuleResult Execute(DiagnosticModulePayload p)
        {
            if (_closed) throw Error("MODULE_LIFETIME_EXPIRED", "Service lifetime ended.");
            if (p.action == "status" && string.IsNullOrEmpty(p.moduleId)) return Snapshot(null);
            if (p.action != "status" && !p.allowFullCSharp)
                throw Error("MODULE_OPT_IN_REQUIRED", "Set allowFullCSharp=true for trusted full-source code. This is not a sandbox.");
            if (p.action != "status") ValidateIdentity(p);
            if (string.IsNullOrEmpty(p.moduleId) || p.moduleId.Length > 64 ||
                p.moduleId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw Error("INVALID_MODULE_ID", "moduleId must contain 1-64 letters, digits, '-' or '_'.");
            _modules.TryGetValue(p.moduleId, out var module);
            if (module != null && module.SessionId != p.sessionId)
                throw Error("MODULE_SESSION_MISMATCH", "The exact owning session is required.");
            if (p.action == "status" && module != null) return Snapshot(module);
            if (p.action == "close" && module != null) { module.Closed = true; module.Context?.Close(true); return Snapshot(module); }
            var session = _sessions.Get(p.sessionId);
            if (p.action == "compile") return Compile(p, session, module);
            if (module == null) throw Error("MODULE_NOT_FOUND", "No module with this exact ID in this lifetime.");
            if (p.action == "run")
            {
                RequireReplaceable(module);
                if (module.Closed || module.Entry == null || module.Context == null || module.Context.IsClosed)
                    throw Error("MODULE_ENTRY_INVALID", "Compile a valid revision in the current session before running.");
                if (p.argsJson == null || Encoding.UTF8.GetByteCount(p.argsJson) > 1048576)
                    throw Error("MODULE_ARGUMENT_LIMIT", "argsJson is required and limited to 1 MiB.");
                IAsyncExecutionLease lease = module.Entry.ReturnType == typeof(Task<string>) ? session.BeginAsyncOperation("diagnostic_module:" + module.Id) : null;
                module.InvocationId = Guid.NewGuid().ToString("N"); module.Invoked = true;
                module.Error = ""; module.Result = ""; module.Running = null; module.RunTimedOut = false;
                module.RunDeadline = Now() + 30000; module.RunFinishedAt = 0;
                try
                {
                    object value = module.Entry.Invoke(null, new object[] { module.Context, p.argsJson });
                    if (module.Entry.ReturnType == typeof(Task<string>))
                        module.Running = ObserveRun(value as Task<string> ?? throw new InvalidOperationException("Run returned a null Task."), lease, module);
                    else { module.Result = CheckResult((string)value); module.RunFinishedAt = Now(); }
                }
                catch (Exception ex) { lease?.Fail(ex); lease?.Dispose(); module.Error = ExceptionText(ex); module.RunFinishedAt = Now(); }
            }
            else if (p.action != "status") throw Error("INVALID_MODULE_ACTION", "action must be compile, run, status or close.");
            return Snapshot(module);
        }
        private void ValidateIdentity(DiagnosticModulePayload p)
        {
            if (string.IsNullOrWhiteSpace(p.expectedProjectPath) ||
                !string.Equals(Path.GetFullPath(p.expectedProjectPath).TrimEnd('\\', '/'), _project, StringComparison.OrdinalIgnoreCase) ||
                p.expectedDomainGeneration != _sessions.DomainGeneration || p.expectedPlaySessionId != _playSession)
                throw Error("MODULE_IDENTITY_MISMATCH", "Read module status and use the exact project/domain/play-session identity; do not replay.");
        }
        private static void RequireReplaceable(Module m)
        {
            if (m.BuildPending || (m.Running != null && !m.Running.IsCompleted))
                throw Error("MODULE_BUSY", "Observe the original compile/invocation. An in-flight Task cannot be replaced or forcibly stopped.");
        }
        private DiagnosticModuleResult Compile(DiagnosticModulePayload p, ExecutionSession session, Module old)
        {
            if (old != null) RequireReplaceable(old);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || Compilers.Count != 0)
                throw Error("MODULE_COMPILER_BUSY", "Unity is compiling/importing. Do not request another compile automatically.");
            if (string.IsNullOrWhiteSpace(p.source) || Encoding.UTF8.GetByteCount(p.source) > 1048576 || string.IsNullOrWhiteSpace(p.entryType))
                throw Error("INVALID_MODULE_SOURCE", "A single complete C# source (at most 1 MiB) and exact entryType are required.");
            if (p.referenceTypeNames == null || p.referenceTypeNames.Length > 32)
                throw Error("MODULE_REFERENCE_LIMIT", "At most 32 exact loaded reference type names are supported.");
            var analyzers = EnsureSupportedAnalyzers();
            string analyzerKey = string.Join(";", analyzers.Select(a => a.path + ":" + a.sha256));
            var references = SelectReferences(p.referenceTypeNames);
            string sourceHash = Hash(Encoding.UTF8.GetBytes(p.source));
            string key = sourceHash + ":" + p.entryType + ":" + string.Join(";", references.Select(r => r.assemblyName + ":" + r.mvid + ":" + r.sha256)) + ":" + analyzerKey;
            if (old != null && old.Key == key && old.Entry != null && old.Context != null)
            {
                foreach (var r in old.References)
                    if (r.sha256 != Hash(File.ReadAllBytes(r.path))) throw Error("MODULE_REFERENCE_CHANGED", "A previous reference changed; no cached entry was reused.");
                if (old.Context.CleanupErrors.Length != 0) throw Error("MODULE_CLEANUP_FAILED", "Previous cleanup failed; no cached entry was reused.");
                if (old.Context.IsClosed)
                {
                    var context = new DiagnosticModuleContext(session, _project, _playSession);
                    old.Context = context;
                    ResetInvocation(old);
                }
                old.Closed = false; old.CompilationStatus = "completed"; old.CompilationError = "";
                return Snapshot(old); // Same content: no new compile, DLL load or Run replay.
            }
            if (_modules.Count >= 16 && old == null) throw Error("MODULE_COUNT_LIMIT", "At most 16 logical modules per service lifetime.");
            if (_loadedCount >= 32 || _loadedBytes >= 67108864) throw Error("MODULE_DOMAIN_LOAD_LIMIT", "Resident DLL budget exhausted until an independently requested Domain Reload.");
            int revision = (old?.CompilationRevision ?? 0) + 1;
            string name = "UPilotDiagnostic_" + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_project, ".upilot", "diagnostic-modules", name);
            EnsureSafeFolder(folder);
            Directory.CreateDirectory(folder);
            string sourcePath = Path.Combine(folder, name + ".cs"), dllPath = Path.Combine(folder, name + ".dll");
            File.WriteAllText(sourcePath, p.source, new UTF8Encoding(false));
            var builder = new AssemblyBuilder(dllPath, sourcePath)
            {
                flags = AssemblyBuilderFlags.EditorAssembly,
                referencesOptions = ReferencesOptions.UseEngineModules,
                additionalReferences = references.Select(r => r.path).ToArray()
            };
            builder.compilerOptions.AllowUnsafeCode = false;
            // Unity may still append its installed toolchain; this is not an analyzer isolation switch.
            builder.compilerOptions.RoslynAnalyzerDllPaths = Array.Empty<string>();
            // Keep Unity's installed reference assemblies. Never silently compile against newer project DLLs.
            var defaults = builder.defaultReferences;
            string editorRoot = Path.GetFullPath(EditorApplication.applicationContentsPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            builder.excludeReferences = defaults.Where(path =>
                !Path.GetFullPath(path).StartsWith(editorRoot, StringComparison.OrdinalIgnoreCase) &&
                !references.Any(r => string.Equals(Path.GetFullPath(path), r.path, StringComparison.OrdinalIgnoreCase))).ToArray();
            var unityReferences = defaults.Except(builder.excludeReferences).Where(path =>
                !references.Any(r => string.Equals(Path.GetFullPath(path), r.path, StringComparison.OrdinalIgnoreCase)))
                .Select(path => new DiagnosticModuleReference { path = Path.GetFullPath(path), sha256 = Hash(File.ReadAllBytes(path)), unityReferenceAssembly = true }).ToArray();
            var allReferences = references.Concat(unityReferences).ToArray();
            var compileLease = session.BeginAsyncOperation("diagnostic module compile " + p.moduleId);
            var module = old ?? new Module { Id = p.moduleId, SessionId = session.Id };
            if (old == null) session.RegisterCleanup(() => module.Context?.Close(true));
            module.Builder = builder;
            module.ObservedAnalyzers = analyzers;
            module.CompilationRevision = revision; module.CompilationStatus = "compiling";
            module.CompileDeadline = Now() + 60000; module.BuildPending = true; module.Closed = false;
            module.CompilationError = ""; module.Messages = Array.Empty<string>();
            _modules[p.moduleId] = module;
            string playSession = _playSession;
            IDisposable scope = ObserveCompiler(builder);
            builder.buildFinished += (_, messages) =>
            {
                try
                {
                    module.Messages = messages.Take(64).Select(m => Bounded(m.type + ": " + m.file + ":" + m.line + ":" + m.column + " " + m.message)).ToArray();
                    if (messages.Any(m => m.type == CompilerMessageType.Error)) throw Error("MODULE_COMPILE_FAILED", "Unity reported compiler errors; the previous revision was not replaced.");
                    if (_closed || session.IsClosed || module.Closed || playSession != _playSession) throw Error("MODULE_LIFETIME_EXPIRED", "Compile completed after its owning lifetime ended; DLL was not loaded.");
                    if (Now() >= module.CompileDeadline) throw Error("MODULE_COMPILE_TIMEOUT", "Original compile deadline expired; late DLL was not loaded.");
                    var afterAnalyzers = EnsureSupportedAnalyzers();
                    if (analyzerKey != string.Join(";", afterAnalyzers.Select(a => a.path + ":" + a.sha256)))
                        throw Error("MODULE_ANALYZERS_CHANGED", "Observed Unity toolchain changed during compilation; candidate was not loaded.");
                    foreach (var r in allReferences)
                        if (r.sha256 != Hash(File.ReadAllBytes(r.path))) throw Error("MODULE_REFERENCE_CHANGED", "A compile reference changed; candidate was not loaded.");
                    if (sourceHash != Hash(File.ReadAllBytes(sourcePath))) throw Error("MODULE_SOURCE_CHANGED", "Source changed during compilation; candidate was not loaded.");
                    byte[] dll = File.ReadAllBytes(dllPath);
                    if (_loadedCount >= 32 || dll.LongLength > 67108864 - _loadedBytes) throw Error("MODULE_DOMAIN_LOAD_LIMIT", "Candidate exceeds the resident DLL budget.");
                    // Consume before loading: a failed binding/load cannot promise physical rollback.
                    _loadedCount++; _loadedBytes += dll.LongLength; module.LoadedAttempt = true;
                    Assembly assembly = Assembly.Load(dll);
                    MethodInfo entry = BindEntry(assembly, p.entryType);
                    module.Context?.Close(true);
                    if (module.Context != null && module.Context.CleanupErrors.Length != 0)
                        throw Error("MODULE_CLEANUP_FAILED", "Old revision cleanup failed; candidate was not installed. DLL may remain resident.");
                    var context = new DiagnosticModuleContext(session, _project, playSession);
                    module.Context = context; module.Entry = entry; module.EntryType = p.entryType;
                    module.AssemblyName = assembly.FullName; module.SourceHash = sourceHash; module.DllHash = Hash(dll);
                    module.References = allReferences; module.Key = key; module.Revision = revision;
                    ResetInvocation(module);
                    module.CompilationStatus = "completed";
                }
                catch (Exception ex) { compileLease.Fail(ex); module.CompilationStatus = "failed"; module.CompilationError = ExceptionText(ex); }
                finally { module.BuildPending = false; module.Builder = null; scope.Dispose(); compileLease.Dispose(); }
            };
            try
            {
                if (!builder.Build())
                {
                    module.CompilationStatus = "failed"; module.BuildPending = false; module.Builder = null;
                    module.CompilationError = "Unity refused the independent build; not retried.";
                    compileLease.Fail(Error("MODULE_COMPILER_BUSY", module.CompilationError)); scope.Dispose(); compileLease.Dispose();
                }
            }
            catch (Exception ex)
            {
                compileLease.Fail(ex); module.CompilationStatus = "unknown"; module.CompilationError = ExceptionText(ex);
                // A throw is not proof that the compiler never started. Keep its identity/lease until observed finished.
                bool notStarted = false;
                try { notStarted = builder.status == AssemblyBuilderStatus.NotStarted; } catch { }
                if (notStarted)
                {
                    module.CompilationStatus = "failed"; module.BuildPending = false; module.Builder = null;
                    scope.Dispose(); compileLease.Dispose();
                }
            }
            return Snapshot(module);
        }
        internal static MethodInfo BindEntry(Assembly assembly, string typeName)
        {
            Type type = assembly.GetType(typeName, false, false); // Never global name caches / previous revisions.
            MethodInfo[] entries = type?.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Run").ToArray() ?? Array.Empty<MethodInfo>();
            if (type == null || !type.IsPublic || type.ContainsGenericParameters || entries.Length != 1)
                throw Error("MODULE_ENTRY_SIGNATURE", "Require one public static Run on an exact public non-generic type.");
            MethodInfo entry = entries[0]; var args = entry.GetParameters();
            if (entry.ContainsGenericParameters || args.Length != 2 || args[0].ParameterType != typeof(DiagnosticModuleContext) ||
                args[1].ParameterType != typeof(string) || (entry.ReturnType != typeof(string) && entry.ReturnType != typeof(Task<string>)))
                throw Error("MODULE_ENTRY_SIGNATURE", "Run must accept the loaded DiagnosticModuleContext and string argsJson, returning string or Task<string>.");
            return entry;
        }
        private static DiagnosticModuleReference[] SelectReferences(string[] typeNames)
        {
            var assemblies = new HashSet<Assembly> { typeof(DiagnosticModuleContext).Assembly, typeof(UnityEngine.Object).Assembly, typeof(EditorApplication).Assembly };
            foreach (string typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName)) throw Error("MODULE_REFERENCE_TYPE", "Reference type names must not be empty.");
                var matches = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetType(typeName, false, false) != null).ToArray();
                if (matches.Length != 1) throw Error("MODULE_REFERENCE_TYPE", "Require one exact already-loaded type: " + typeName);
                assemblies.Add(matches[0]);
            }
            return assemblies.OrderBy(a => a.FullName, StringComparer.Ordinal).Select(Reference).ToArray();
        }
        internal static DiagnosticModuleReference Reference(Assembly assembly)
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) throw Error("MODULE_REFERENCE_IDENTITY", "Dynamic / byte-loaded dependency assemblies are not supported.");
            string path = Path.GetFullPath(assembly.Location);
            var cecil = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Mono.Cecil")?.GetType("Mono.Cecil.ModuleDefinition", false);
            if (cecil == null) throw Error("MODULE_METADATA_UNAVAILABLE", "Already-loaded Mono.Cecil reader unavailable; no assembly was loaded to bypass this boundary.");
            MethodInfo read = cecil.GetMethod("ReadModule", new[] { typeof(Stream) });
            if (read == null) throw Error("MODULE_METADATA_UNAVAILABLE", "Loaded reader has no supported stream overload.");
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var metadata = (IDisposable)read.Invoke(null, new object[] { stream }))
            {
                var mvid = (Guid)cecil.GetProperty("Mvid").GetValue(metadata);
                if (mvid != assembly.ManifestModule.ModuleVersionId) throw Error("MODULE_REFERENCE_IDENTITY", "On-disk reference does not match the loaded MVID: " + assembly.FullName);
                return new DiagnosticModuleReference { path = path, assemblyName = assembly.FullName, mvid = mvid.ToString("N"), sha256 = Hash(bytes) };
            }
        }
        private static DiagnosticModuleReference[] EnsureSupportedAnalyzers()
        {
            return InspectAnalyzerPaths(AssetDatabase.FindAssets("l:RoslynAnalyzer").Length != 0,
                CompilationPipeline.GetAssemblies().SelectMany(a =>
                    a.compilerOptions.RoslynAnalyzerDllPaths ?? Array.Empty<string>()),
                EditorApplication.applicationContentsPath);
        }
        internal static DiagnosticModuleReference[] InspectAnalyzerPaths(bool projectHasAnalyzers,
            IEnumerable<string> paths, string editorContentsPath)
        {
            if (projectHasAnalyzers)
                throw Error("MODULE_ANALYZERS_UNSUPPORTED", "Project/package Analyzer/Generator assets are unsupported; project configuration was not changed.");
            var observed = new Dictionary<string, DiagnosticModuleReference>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (!IsUnityToolchainAnalyzer(path, editorContentsPath))
                    throw Error("MODULE_ANALYZERS_UNSUPPORTED", "Analyzer/Generator is not a verified file in the current Unity installation: " + Bounded(path ?? "<null>"));
                string fullPath = Path.GetFullPath(path);
                if (!observed.ContainsKey(fullPath))
                    observed.Add(fullPath, new DiagnosticModuleReference { path = fullPath, sha256 = Hash(File.ReadAllBytes(fullPath)) });
            }
            return observed.Values.OrderBy(a => a.path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        internal static bool IsUnityToolchainAnalyzer(string path, string editorContentsPath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(editorContentsPath) ||
                !Path.IsPathRooted(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string root = Path.GetFullPath(editorContentsPath).TrimEnd('\\', '/');
                string fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return false;
                // Do not mistake a linked project/package DLL for an installed Unity toolchain file.
                for (string current = fullPath; current != null; current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        private void EnsureSafeFolder(string folder)
        {
            string root = _project + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw Error("MODULE_PATH_UNSAFE", "Module output must stay inside the current project.");
            for (string p = folder; p != null && p.StartsWith(root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw Error("MODULE_PATH_UNSAFE", "Module output cannot cross a junction/symlink.");
        }
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static async Task<string> ObserveRun(Task<string> task, IAsyncExecutionLease lease, Module module)
        {
            try { return await task; }
            catch (Exception ex) { lease.Fail(ex); throw; }
            finally { module.RunFinishedAt = Now(); lease.Dispose(); }
        }
        private DiagnosticModuleResult Snapshot(Module m)
        {
            if (m?.Builder != null && m.BuildPending)
            {
                // Unity's status getter observes the accepted build and can deliver its one finished callback.
                try { var observed = m.Builder.status; }
                catch (Exception ex) { m.CompilationStatus = "unknown"; m.CompilationError = ExceptionText(ex); }
            }
            if (m != null && m.BuildPending && Now() >= m.CompileDeadline)
            { m.CompilationStatus = "timed_out"; m.CompilationError = "Original deadline expired; compiler may still run. No late DLL will be loaded."; }
            if (m != null && m.Invoked && (m.RunFinishedAt == 0 ? Now() : m.RunFinishedAt) >= m.RunDeadline)
            { m.RunTimedOut = true; if (string.IsNullOrEmpty(m.Error)) m.Error = "Invocation observation deadline expired; arbitrary code may still run. No replay or forced stop."; }
            if (m?.Running != null && m.Running.IsCompleted)
            {
                try { var result = CheckResult(m.Running.GetAwaiter().GetResult()); if (!m.RunTimedOut) m.Result = result; }
                catch (Exception ex) { if (string.IsNullOrEmpty(m.Error)) m.Error = ExceptionText(ex); }
                m.Running = null; // Freeze one observed result; polling never executes Run again.
            }
            return new DiagnosticModuleResult
            {
                projectPath = _project, domainGeneration = _sessions.DomainGeneration, playSessionId = _playSession,
                sessionId = m?.SessionId ?? "", moduleId = m?.Id ?? "",
                status = m == null ? "available" : m.Closed || m.Context != null && m.Context.IsClosed ? "closed" :
                    m.RunTimedOut ? "timed_out" :
                    m.Running != null ? "running" : !string.IsNullOrEmpty(m.Error) ? "failed" : m.Invoked ? "completed" : m.Entry != null ? "ready" : "unavailable",
                compilationStatus = m?.CompilationStatus ?? "not_started", revision = m?.Revision ?? 0, compilationRevision = m?.CompilationRevision ?? 0,
                sourceSha256 = m?.SourceHash ?? "", dllSha256 = m?.DllHash ?? "", assemblyName = m?.AssemblyName ?? "", entryType = m?.EntryType ?? "",
                invocationId = m?.InvocationId ?? "", result = m?.Result ?? "", error = m?.Error ?? "", compilationError = m?.CompilationError ?? "",
                compilerMessages = m?.Messages ?? Array.Empty<string>(), cleanupErrors = m?.Context?.CleanupErrors ?? Array.Empty<string>(),
                references = m?.References ?? Array.Empty<DiagnosticModuleReference>(),
                observedUnityToolchainAnalyzers = m?.ObservedAnalyzers ?? Array.Empty<DiagnosticModuleReference>(),
                loadedAssemblyCount = _loadedCount, loadedAssemblyBytes = _loadedBytes,
                activeInvocation = m?.Running != null, sideEffectsMayHaveOccurred = m != null && (m.Invoked || m.LoadedAttempt),
                compileDeadlineAt = m?.CompileDeadline ?? 0, invocationDeadlineAt = m?.RunDeadline ?? 0, compilerStillRunning = m?.BuildPending ?? false
            };
        }
        private static void ResetInvocation(Module module)
        {
            module.Running = null; module.Invoked = false; module.RunTimedOut = false;
            module.Error = ""; module.Result = ""; module.InvocationId = "";
            module.RunDeadline = 0; module.RunFinishedAt = 0;
        }
        private static string CheckResult(string result)
        {
            result = result ?? "";
            if (Encoding.UTF8.GetByteCount(result) > 1048576) throw Error("MODULE_RESULT_LIMIT", "Result exceeds 1 MiB; invocation already occurred.");
            return result;
        }
        private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string Bounded(string text) => text.Length <= 4096 ? text : text.Substring(0, 4096);
        private static string ExceptionText(Exception ex) { if (ex is TargetInvocationException t && t.InnerException != null) ex = t.InnerException; return Bounded((ex is ExecutionContractException contract ? "[" + contract.Code + "] " : "") + ex.GetType().FullName + ": " + ex.Message); }
        private static ExecutionContractException Error(string code, string message) => new ExecutionContractException(code, message);
    }
#pragma warning restore 0618
}
