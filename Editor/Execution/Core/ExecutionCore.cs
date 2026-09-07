using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot.Execution
{
    [Serializable]
    public sealed class ExecutionSourceSpan
    {
        public int start;
        public int length;
        public int end;
        public int line;
        public int column;
        public int endLine;
        public int endColumn;

        public ExecutionSourceSpan Clone()
        {
            return (ExecutionSourceSpan)MemberwiseClone();
        }
    }

    public sealed class ExecutionContractException : Exception
    {
        public string Code { get; }
        public IDictionary<string, object> Detail { get; }

        public ExecutionContractException(string code, string message, IDictionary<string, object> detail = null)
            : base(message)
        {
            Code = string.IsNullOrWhiteSpace(code) ? "EXECUTION_ERROR" : code;
            Detail = detail ?? new Dictionary<string, object>();
        }
    }

    [Serializable]
    public sealed class TypedValueSpec
    {
        public string kind = "literal";
        public string typeName = "";
        public string valueJson = "null";
        public string handle = "";
        public int instanceId;
        public string globalObjectId = "";
        public string assetGuid = "";
        public string hierarchyPath = "";
        public TypedValueSpec[] items = Array.Empty<TypedValueSpec>();
    }

    [Serializable]
    public sealed class ExecutionArgumentSpec
    {
        public string name = "";
        public string direction = "in";
        public TypedValueSpec value = new TypedValueSpec();
    }

    [Serializable]
    public sealed class ExecutionArgumentsEnvelope
    {
        public ExecutionArgumentSpec[] items = Array.Empty<ExecutionArgumentSpec>();
    }

    [Serializable]
    public sealed class ExecutionVariableSpec
    {
        public string name = "";
        public TypedValueSpec value = new TypedValueSpec();
    }

    [Serializable]
    public sealed class ExecutionVariablesEnvelope
    {
        public ExecutionVariableSpec[] items = Array.Empty<ExecutionVariableSpec>();
    }

    [Serializable]
    public sealed class TypedValueResult
    {
        public string kind = "null";
        public string typeName = "";
        public string valueJson = "null";
        public string handle = "";
        public string summary = "";
        public string serializationStatus = "inline";
    }

    public sealed class ExecutionValue
    {
        public string Name { get; set; }
        public string Direction { get; set; }
        public Type DeclaredType { get; set; }
        public object Value { get; set; }

        public ExecutionValue()
        {
            Name = "";
            Direction = "in";
        }
    }

    public sealed class ExecutionBudget
    {
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private readonly int _timeoutMs;
        private readonly int _maxStatements;
        private readonly int _maxLoopIterations;
        private readonly int _maxCalls;
        private readonly int _maxAllocations;
        private readonly int _maxRecursion;
        private readonly int _maxAwaits;
        private readonly int _maxArrayElements;
        private readonly CancellationToken _cancellationToken;
        private Func<ExecutionSourceSpan> _spanProvider;
        private int _finallyCleanupDepth;
        private DateTime _finallyCleanupStartedUtc;
        private int _finallyCleanupLoops;
        private int _finallyCleanupCalls;
        private int _finallyCleanupAllocations;

        public int Statements { get; private set; }
        public int LoopIterations { get; private set; }
        public int Calls { get; private set; }
        public int Allocations { get; private set; }
        public int RecursionDepth { get; private set; }
        public int Awaits { get; private set; }
        public int ArrayElements { get; private set; }
        public int FinallyCleanupStatements { get; private set; }
        public long ElapsedMs => Math.Max(0L, (long)(DateTime.UtcNow - _startedAtUtc).TotalMilliseconds);

        public ExecutionBudget(
            int timeoutMs = 3000,
            int maxStatements = 10000,
            int maxLoopIterations = 10000,
            int maxCalls = 1000,
            int maxAllocations = 1000,
            int maxRecursion = 64,
            CancellationToken cancellationToken = default(CancellationToken),
            int maxAwaits = 1000,
            int maxArrayElements = 100000)
        {
            _timeoutMs = Clamp(timeoutMs, 1, 30000);
            _maxStatements = Clamp(maxStatements, 1, 100000);
            _maxLoopIterations = Clamp(maxLoopIterations, 1, 100000);
            _maxCalls = Clamp(maxCalls, 1, 100000);
            _maxAllocations = Clamp(maxAllocations, 1, 100000);
            _maxRecursion = Clamp(maxRecursion, 1, 256);
            _maxAwaits = Clamp(maxAwaits, 1, 100000);
            _maxArrayElements = Clamp(maxArrayElements, 1, 1000000);
            _cancellationToken = cancellationToken;
        }

        public void SetSpanProvider(Func<ExecutionSourceSpan> spanProvider) { _spanProvider = spanProvider; }

        public void CheckCancellation()
        {
            if (_finallyCleanupDepth > 0) { CheckFinallyCleanup(); return; }
            if (!_cancellationToken.IsCancellationRequested) return;
            var detail = new Dictionary<string, object> { { "stage", "cancelled" } };
            var span = _spanProvider == null ? null : _spanProvider();
            if (span != null) detail["sourceSpan"] = span.Clone();
            throw new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.", detail);
        }

        public void CheckTime()
        {
            if (_finallyCleanupDepth > 0) { CheckFinallyCleanup(); return; }
            CheckCancellation();
            if (ElapsedMs > _timeoutMs)
                throw BudgetExceeded("wallClockMs", _timeoutMs, ElapsedMs);
        }

        public void CountStatement()
        {
            if (_finallyCleanupDepth > 0)
            {
                FinallyCleanupStatements++;
                if (FinallyCleanupStatements > 256) throw FinallyCleanupExceeded("statements", 256, FinallyCleanupStatements);
                CheckFinallyCleanup();
                return;
            }
            CheckCancellation();
            Statements++;
            if (Statements > _maxStatements)
                throw BudgetExceeded("statements", _maxStatements, Statements);
            CheckTime();
        }

        public void CountLoop()
        {
            if (_finallyCleanupDepth > 0)
            {
                _finallyCleanupLoops++;
                if (_finallyCleanupLoops > 256) throw FinallyCleanupExceeded("iterations", 256, _finallyCleanupLoops);
                CheckFinallyCleanup();
                return;
            }
            CheckCancellation();
            LoopIterations++;
            if (LoopIterations > _maxLoopIterations)
                throw BudgetExceeded("loopIterations", _maxLoopIterations, LoopIterations);
            CheckTime();
        }

        public void CountCall()
        {
            if (_finallyCleanupDepth > 0)
            {
                _finallyCleanupCalls++;
                if (_finallyCleanupCalls > 32) throw FinallyCleanupExceeded("calls", 32, _finallyCleanupCalls);
                CheckFinallyCleanup();
                return;
            }
            CheckCancellation();
            Calls++;
            if (Calls > _maxCalls)
                throw BudgetExceeded("calls", _maxCalls, Calls);
            CheckTime();
        }

        public void CountAllocation()
        {
            if (_finallyCleanupDepth > 0)
            {
                _finallyCleanupAllocations++;
                if (_finallyCleanupAllocations > 32) throw FinallyCleanupExceeded("allocations", 32, _finallyCleanupAllocations);
                CheckFinallyCleanup();
                return;
            }
            CheckCancellation();
            Allocations++;
            if (Allocations > _maxAllocations)
                throw BudgetExceeded("allocations", _maxAllocations, Allocations);
            CheckTime();
        }

        public IDisposable EnterRecursion()
        {
            CheckCancellation();
            RecursionDepth++;
            if (RecursionDepth > _maxRecursion)
            {
                RecursionDepth--;
                throw BudgetExceeded("recursionDepth", _maxRecursion, _maxRecursion + 1);
            }
            return new RecursionScope(this);
        }

        public void CountAwait()
        {
            CheckCancellation();
            Awaits++;
            if (Awaits > _maxAwaits) throw BudgetExceeded("awaits", _maxAwaits, Awaits);
            CheckTime();
        }

        public void CountArrayElements(long count)
        {
            if (count < 0 || count > int.MaxValue) throw BudgetExceeded("arrayElements", _maxArrayElements, count);
            CheckCancellation();
            long next = (long)ArrayElements + count;
            if (next > _maxArrayElements) throw BudgetExceeded("arrayElements", _maxArrayElements, next);
            ArrayElements = (int)next;
            CheckTime();
        }

        public IDisposable EnterFinallyCleanup()
        {
            if (_finallyCleanupDepth++ == 0)
            {
                _finallyCleanupStartedUtc = DateTime.UtcNow;
                _finallyCleanupLoops = 0;
                _finallyCleanupCalls = 0;
                _finallyCleanupAllocations = 0;
            }
            return new FinallyCleanupScope(this);
        }

        private void CheckFinallyCleanup()
        {
            long elapsed = Math.Max(0L, (long)(DateTime.UtcNow - _finallyCleanupStartedUtc).TotalMilliseconds);
            if (elapsed > 100) throw FinallyCleanupExceeded("wallClockMs", 100, elapsed);
        }

        private static ExecutionContractException FinallyCleanupExceeded(string metric, long limit, long actual)
        {
            return new ExecutionContractException("EXECUTION_FINALLY_CLEANUP_EXCEEDED",
                string.Format(CultureInfo.InvariantCulture, "Finally cleanup budget exceeded: {0} limit={1}, actual={2}.", metric, limit, actual),
                new Dictionary<string, object> { { "stage", "budget" }, { "metric", metric }, { "limit", limit }, { "actual", actual } });
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static ExecutionContractException BudgetExceeded(string metric, long limit, long actual)
        {
            return new ExecutionContractException(
                "EXECUTION_BUDGET_EXCEEDED",
                string.Format(CultureInfo.InvariantCulture, "Execution budget exceeded: {0} limit={1}, actual={2}.", metric, limit, actual),
                new Dictionary<string, object>
                {
                    { "stage", "budget" },
                    { "metric", metric },
                    { "limit", limit },
                    { "actual", actual },
                });
        }

        private sealed class RecursionScope : IDisposable
        {
            private ExecutionBudget _owner;
            public RecursionScope(ExecutionBudget owner) { _owner = owner; }
            public void Dispose()
            {
                if (_owner == null) return;
                _owner.RecursionDepth = Math.Max(0, _owner.RecursionDepth - 1);
                _owner = null;
            }
        }

        private sealed class FinallyCleanupScope : IDisposable
        {
            private ExecutionBudget _owner;
            public FinallyCleanupScope(ExecutionBudget owner) { _owner = owner; }
            public void Dispose()
            {
                if (_owner == null) return;
                _owner._finallyCleanupDepth = Math.Max(0, _owner._finallyCleanupDepth - 1);
                _owner = null;
            }
        }
    }

    public static class ExecutionTypeResolver
    {
        private static readonly Dictionary<string, Type> Aliases = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "bool", typeof(bool) }, { "byte", typeof(byte) }, { "sbyte", typeof(sbyte) },
            { "short", typeof(short) }, { "ushort", typeof(ushort) }, { "int", typeof(int) },
            { "uint", typeof(uint) }, { "long", typeof(long) }, { "ulong", typeof(ulong) },
            { "float", typeof(float) }, { "double", typeof(double) }, { "decimal", typeof(decimal) },
            { "char", typeof(char) }, { "string", typeof(string) }, { "object", typeof(object) },
            { "void", typeof(void) }, { "Type", typeof(Type) },
        };

        public static Type Resolve(string typeName, IEnumerable<string> imports = null, bool allowVoid = false)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string name = typeName.Trim();
            var parsed = TryResolveComposite(name, imports, allowVoid);
            if (parsed.Handled) return parsed.Type;
            if (Aliases.TryGetValue(name, out var alias))
            {
                if (alias == typeof(void) && !allowVoid) return null;
                return alias;
            }

            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                var inner = Resolve(name.Substring(0, name.Length - 1), imports);
                return inner != null && inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner;
            }

            if (name.EndsWith("[]", StringComparison.Ordinal))
            {
                var element = Resolve(name.Substring(0, name.Length - 2), imports);
                return element == null ? null : element.MakeArrayType();
            }

            var candidates = FindCandidates(name).ToList();
            if (candidates.Count == 0 && imports != null && name.IndexOf('.') < 0)
            {
                foreach (var ns in imports.Where(value => !string.IsNullOrWhiteSpace(value)))
                    candidates.AddRange(FindCandidates(ns.Trim() + "." + name));
            }

            candidates = candidates.Distinct().ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1)
                throw new ExecutionContractException(
                    "TYPE_NAME_AMBIGUOUS",
                    "Type name is ambiguous: " + name,
                    new Dictionary<string, object> { { "candidates", candidates.Select(t => t.AssemblyQualifiedName).ToArray() } });
            return null;
        }

        private struct CompositeResolution { public bool Handled; public Type Type; }

        private static CompositeResolution TryResolveComposite(string name, IEnumerable<string> imports, bool allowVoid)
        {
            var arrayRanks = new List<int>();
            while (name.EndsWith("]", StringComparison.Ordinal))
            {
                int openBracket = name.LastIndexOf('[');
                if (openBracket < 0) break;
                string rankText = name.Substring(openBracket + 1, name.Length - openBracket - 2);
                if (rankText.Any(ch => ch != ',')) break;
                int rank = rankText.Length + 1;
                if (rank > 4)
                    throw new ExecutionContractException("CSHARP_ARRAY_RANK_UNSUPPORTED", "Array ranks greater than 4 are not supported.");
                arrayRanks.Add(rank);
                name = name.Substring(0, openBracket).Trim();
            }
            bool nullable = name.EndsWith("?", StringComparison.Ordinal);
            if (nullable) name = name.Substring(0, name.Length - 1).Trim();
            int open = FindTopLevelGenericOpen(name);
            if (open < 0)
            {
                if (arrayRanks.Count == 0 && !nullable) return new CompositeResolution();
                Type inner = Resolve(name, imports, allowVoid);
                if (inner == null) return new CompositeResolution { Handled = true };
                if (nullable && inner.IsValueType) inner = typeof(Nullable<>).MakeGenericType(inner);
                for (int i = arrayRanks.Count - 1; i >= 0; i--)
                    inner = arrayRanks[i] == 1 ? inner.MakeArrayType() : inner.MakeArrayType(arrayRanks[i]);
                return new CompositeResolution { Handled = true, Type = inner };
            }
            if (!name.EndsWith(">", StringComparison.Ordinal)) return new CompositeResolution { Handled = true };
            string root = name.Substring(0, open).Trim();
            var argumentNames = SplitGenericArguments(name.Substring(open + 1, name.Length - open - 2));
            var argumentTypes = argumentNames.Select(argument => Resolve(argument, imports)).ToArray();
            if (argumentTypes.Any(type => type == null)) return new CompositeResolution { Handled = true };
            Type definition = ResolveGenericDefinition(root, argumentTypes.Length, imports);
            if (definition == null) return new CompositeResolution { Handled = true };
            Type result;
            try { result = definition.MakeGenericType(argumentTypes); }
            catch (Exception ex)
            {
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Generic type could not be closed: " + name + ". " + ex.Message);
            }
            if (nullable && result.IsValueType) result = typeof(Nullable<>).MakeGenericType(result);
            for (int i = arrayRanks.Count - 1; i >= 0; i--)
                result = arrayRanks[i] == 1 ? result.MakeArrayType() : result.MakeArrayType(arrayRanks[i]);
            return new CompositeResolution { Handled = true, Type = result };
        }

        private static int FindTopLevelGenericOpen(string value)
        {
            int index = value.IndexOf('<');
            return index > 0 ? index : -1;
        }

        private static string[] SplitGenericArguments(string value)
        {
            var result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '<') depth++;
                else if (value[i] == '>') depth--;
                else if (value[i] == ',' && depth == 0)
                {
                    result.Add(value.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            result.Add(value.Substring(start).Trim());
            return result.Where(item => item.Length > 0).ToArray();
        }

        private static Type ResolveGenericDefinition(string root, int arity, IEnumerable<string> imports)
        {
            string metadataName = root + "`" + arity;
            var candidates = FindCandidates(metadataName).Where(type => type.IsGenericTypeDefinition).ToList();
            if (candidates.Count == 0 && imports != null && root.IndexOf('.') < 0)
                foreach (string ns in imports.Where(value => !string.IsNullOrWhiteSpace(value)))
                    candidates.AddRange(FindCandidates(ns.Trim() + "." + metadataName).Where(type => type.IsGenericTypeDefinition));
            candidates = candidates.Distinct().ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1)
                throw new ExecutionContractException("TYPE_NAME_AMBIGUOUS", "Generic type name is ambiguous: " + root,
                    new Dictionary<string, object> { { "candidates", candidates.Select(type => type.AssemblyQualifiedName).ToArray() } });
            return null;
        }

        public static Type ResolveExpressionRoot(string typeName, IEnumerable<string> imports = null)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string name = typeName.Trim();
            if (Aliases.TryGetValue(name, out var alias) && alias != typeof(void)) return alias;
            var exact = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly =>
                {
                    try { return assembly.GetType(name, false, false); }
                    catch { return null; }
                })
                .Where(type => type != null)
                .Distinct()
                .ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1)
                throw new ExecutionContractException("TYPE_NAME_AMBIGUOUS", "Type name is ambiguous: " + name,
                    new Dictionary<string, object> { { "candidates", exact.Select(type => type.AssemblyQualifiedName).ToArray() } });
            if (imports == null) return null;
            var imported = imports.Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(ns => AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                {
                    try { return assembly.GetType(ns.Trim() + "." + name, false, false); }
                    catch { return null; }
                }))
                .Where(type => type != null)
                .Distinct()
                .ToList();
            if (imported.Count == 1) return imported[0];
            if (imported.Count > 1)
                throw new ExecutionContractException("TYPE_NAME_AMBIGUOUS", "Imported type name is ambiguous: " + name,
                    new Dictionary<string, object> { { "candidates", imported.Select(type => type.AssemblyQualifiedName).ToArray() } });
            return null;
        }

        private static IEnumerable<Type> FindCandidates(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type exact = null;
                try { exact = assembly.GetType(name, false, false); }
                catch { }
                if (exact != null)
                {
                    yield return exact;
                    continue;
                }

                if (name.IndexOf('.') >= 0) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                    if (type != null && string.Equals(type.Name, name, StringComparison.Ordinal))
                        yield return type;
            }
        }
    }

    public sealed class ExecutionSessionRegistry
    {
        private readonly string _domainGeneration = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, ExecutionSession> _sessions = new Dictionary<string, ExecutionSession>(StringComparer.Ordinal);

        public string DomainGeneration => _domainGeneration;

        public ExecutionSession Open(string title, int ttlSec, int maxHandles, int maxDynamicTypes, int maxCallbacks, int maxAsyncOperations = 64)
        {
            SweepExpired();
            var session = new ExecutionSession(
                "s." + _domainGeneration + "." + Guid.NewGuid().ToString("N"),
                _domainGeneration,
                title,
                Clamp(ttlSec, 1, 3600),
                Clamp(maxHandles, 1, 1024),
                Clamp(maxDynamicTypes, 1, 128),
                Clamp(maxCallbacks, 1, 256),
                Clamp(maxAsyncOperations, 1, 256));
            _sessions[session.Id] = session;
            return session;
        }

        public ExecutionSession Get(string sessionId, bool touch = true)
        {
            ValidateDomainToken(sessionId, "session");
            SweepExpired();
            if (!_sessions.TryGetValue(sessionId ?? "", out var session))
                throw new ExecutionContractException("EXECUTION_SESSION_NOT_FOUND", "Execution session was not found: " + (sessionId ?? ""));
            if (touch) session.Touch();
            return session;
        }

        public ExecutionSessionCloseResult Close(string sessionId)
        {
            ValidateDomainToken(sessionId, "session");
            if (!_sessions.TryGetValue(sessionId ?? "", out var session))
                return new ExecutionSessionCloseResult { sessionId = sessionId ?? "", alreadyClosed = true, closed = true };
            _sessions.Remove(sessionId);
            return session.Close();
        }

        public string Store(string sessionId, string kind, object value)
        {
            return Get(sessionId).Store(kind, value);
        }

        public object Resolve(string sessionId, string handle, string expectedKind = "")
        {
            ValidateDomainToken(handle, "handle");
            return Get(sessionId).Resolve(handle, expectedKind);
        }

        public void Invalidate(Func<object, bool> predicate, string reason)
        {
            foreach (var session in _sessions.Values)
                session.Invalidate(predicate, reason);
        }

        public void SweepExpired()
        {
            var expired = _sessions.Values.Where(session => session.IsExpired).Select(session => session.Id).ToList();
            foreach (var id in expired)
            {
                var session = _sessions[id];
                _sessions.Remove(id);
                session.Close();
            }
        }

        private void ValidateDomainToken(string opaqueId, string label)
        {
            if (string.IsNullOrWhiteSpace(opaqueId))
                throw new ExecutionContractException(label == "session" ? "SESSION_REQUIRED" : "HANDLE_REQUIRED", label + " id is required.");
            var parts = opaqueId.Split('.');
            if (parts.Length < 3 || !string.Equals(parts[1], _domainGeneration, StringComparison.Ordinal))
                throw new ExecutionContractException("SESSION_EXPIRED_DOMAIN_RELOAD", "The " + label + " belongs to a previous Unity Domain Reload.");
        }

        private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
    }

    public interface IExecutionSessionLifetime
    {
        bool IsPersistent { get; }
        bool IsClosed { get; }
        CancellationToken CancellationToken { get; }
        int SubscriptionCount { get; }
        void AddEventSubscription(object target, EventInfo eventInfo, Delegate handler);
        bool RemoveEventSubscription(object target, EventInfo eventInfo, Delegate handler);
        IAsyncExecutionLease BeginAsyncOperation(string description);
    }

    public interface IAsyncExecutionLease : IDisposable
    {
        void Fail(Exception error);
    }

    public sealed class ExecutionSession : IExecutionSessionLifetime
    {
        private sealed class HandleEntry
        {
            public string Kind;
            public object Value;
            public string InvalidReason;
        }

        private readonly Dictionary<string, HandleEntry> _handles = new Dictionary<string, HandleEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _variables = new Dictionary<string, object>(StringComparer.Ordinal);
        private ExecutionScope _executionScope;
        private sealed class CleanupEntry
        {
            public string Kind;
            public object Target;
            public EventInfo EventInfo;
            public Delegate Handler;
            public Action Action;
        }

        private readonly List<CleanupEntry> _cleanup = new List<CleanupEntry>();
        private readonly List<string> _cleanupDiagnostics = new List<string>();
        private readonly int _ttlSec;
        private readonly int _maxHandles;
        private readonly int _maxDynamicTypes;
        private readonly int _maxCallbacks;
        private readonly int _maxAsyncOperations;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<string> _recentAsyncDiagnostics = new List<string>();
        private int _dynamicTypeCount;
        private int _callbackCount;
        private int _activeAsyncOperations;
        private int _completedAsyncOperations;
        private int _cancelledAsyncOperations;
        private int _asyncErrors;
        private int _releasedAsyncOperations;
        private bool _closed;

        public string Id { get; }
        public string DomainGeneration { get; }
        public string Title { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime LastAccessAtUtc { get; private set; }
        public bool IsExpired => _closed || (DateTime.UtcNow - LastAccessAtUtc).TotalSeconds > _ttlSec || (DateTime.UtcNow - CreatedAtUtc).TotalSeconds > 3600;
        public int HandleCount => _handles.Count;
        public int VariableCount => _executionScope == null ? _variables.Count : _executionScope.Snapshot().Count;
        public int DynamicTypeCount => _dynamicTypeCount;
        public int CallbackCount => _callbackCount;
        public int CleanupCount => _cleanup.Count;
        public int SubscriptionCount => _cleanup.Count(entry => entry.Kind == "subscription");
        public bool IsPersistent => true;
        public bool IsClosed => _closed;
        public CancellationToken CancellationToken => _cancellation.Token;
        public int ActiveAsyncOperations => _activeAsyncOperations;
        public int CompletedAsyncOperations => _completedAsyncOperations;
        public int CancelledAsyncOperations => _cancelledAsyncOperations;
        public int AsyncErrors => _asyncErrors;
        public int ReleasedAsyncOperations => _releasedAsyncOperations;
        public int AsyncOperationsStillRunning => _activeAsyncOperations;
        public string[] RecentAsyncDiagnostics => _recentAsyncDiagnostics.Take(32).ToArray();
        public string[] CleanupDiagnostics => _cleanupDiagnostics.Take(32).ToArray();
        public long ExpiresAtUnixMs => new DateTimeOffset(LastAccessAtUtc.AddSeconds(_ttlSec)).ToUnixTimeMilliseconds();

        internal ExecutionSession(string id, string domainGeneration, string title, int ttlSec, int maxHandles, int maxDynamicTypes, int maxCallbacks, int maxAsyncOperations)
        {
            Id = id;
            DomainGeneration = domainGeneration;
            Title = title ?? "";
            _ttlSec = ttlSec;
            _maxHandles = maxHandles;
            _maxDynamicTypes = maxDynamicTypes;
            _maxCallbacks = maxCallbacks;
            _maxAsyncOperations = maxAsyncOperations;
            CreatedAtUtc = DateTime.UtcNow;
            LastAccessAtUtc = CreatedAtUtc;
        }

        public void Touch()
        {
            EnsureOpen();
            LastAccessAtUtc = DateTime.UtcNow;
        }

        public string Store(string kind, object value)
        {
            EnsureOpen();
            kind = string.IsNullOrWhiteSpace(kind) ? "object" : kind.Trim();
            if (_handles.Count >= _maxHandles)
                throw new ExecutionContractException("SESSION_LIMIT_EXCEEDED", "Session handle capacity was exceeded.");
            if (kind == "type" && ++_dynamicTypeCount > _maxDynamicTypes)
            {
                _dynamicTypeCount--;
                throw new ExecutionContractException("SESSION_LIMIT_EXCEEDED", "Session dynamic type capacity was exceeded.");
            }
            if ((kind == "delegate" || kind == "callback") && ++_callbackCount > _maxCallbacks)
            {
                _callbackCount--;
                throw new ExecutionContractException("SESSION_LIMIT_EXCEEDED", "Session callback capacity was exceeded.");
            }
            string handle = "h." + DomainGeneration + "." + kind + "." + Guid.NewGuid().ToString("N");
            _handles[handle] = new HandleEntry { Kind = kind, Value = value };
            Touch();
            return handle;
        }

        public object Resolve(string handle, string expectedKind = "")
        {
            EnsureOpen();
            if (!_handles.TryGetValue(handle ?? "", out var entry))
                throw new ExecutionContractException("HANDLE_NOT_FOUND", "Handle was not found in the execution session.");
            if (!string.IsNullOrEmpty(entry.InvalidReason))
                throw new ExecutionContractException("HANDLE_TARGET_INVALID", entry.InvalidReason);
            if (!string.IsNullOrWhiteSpace(expectedKind) && !string.Equals(expectedKind, entry.Kind, StringComparison.Ordinal))
                throw new ExecutionContractException("HANDLE_KIND_MISMATCH", "Expected handle kind " + expectedKind + " but got " + entry.Kind + ".");
            Touch();
            return entry.Value;
        }

        public bool TryGetVariable(string name, out object value)
        {
            EnsureOpen();
            Touch();
            if (_executionScope != null && _executionScope.TryResolve(name, out var cell)) { value = cell.Value; return true; }
            return _variables.TryGetValue(name ?? "", out value);
        }

        public void SetVariable(string name, object value)
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(name))
                throw new ExecutionContractException("INVALID_VARIABLE_NAME", "Variable name is required.");
            _variables[name] = value;
            _executionScope?.SetOrDeclare(name, value);
            Touch();
        }

        public IReadOnlyDictionary<string, object> SnapshotVariables()
        {
            EnsureOpen();
            return _executionScope == null
                ? new Dictionary<string, object>(_variables, StringComparer.Ordinal)
                : new Dictionary<string, object>(_executionScope.Snapshot(), StringComparer.Ordinal);
        }

        internal ExecutionScope GetOrCreateExecutionScope(IDictionary<string, object> seed)
        {
            EnsureOpen();
            if (_executionScope == null)
            {
                _executionScope = new ExecutionScope();
                foreach (var pair in _variables) _executionScope.SetOrDeclare(pair.Key, pair.Value);
            }
            if (seed != null) foreach (var pair in seed) _executionScope.SetOrDeclare(pair.Key, pair.Value);
            return _executionScope;
        }

        public void RegisterCleanup(Action action)
        {
            EnsureOpen();
            if (action != null) _cleanup.Add(new CleanupEntry { Kind = "callback", Action = action });
        }

        public IAsyncExecutionLease BeginAsyncOperation(string description)
        {
            EnsureOpen();
            if (_activeAsyncOperations >= _maxAsyncOperations)
                throw new ExecutionContractException("SESSION_LIMIT_EXCEEDED", "Session async operation capacity was exceeded.");
            _activeAsyncOperations++;
            Touch();
            return new AsyncOperationLease(this, description ?? "async operation");
        }

        private void CompleteAsyncOperation(string description, Exception error)
        {
            _activeAsyncOperations = Math.Max(0, _activeAsyncOperations - 1);
            _releasedAsyncOperations++;
            if (error == null) _completedAsyncOperations++;
            else if (error is OperationCanceledException || error is ExecutionContractException contract && contract.Code == "EXECUTION_CANCELLED") _cancelledAsyncOperations++;
            else _asyncErrors++;
            if (error != null && _recentAsyncDiagnostics.Count < 32)
                _recentAsyncDiagnostics.Add((description ?? "async operation") + ": " + error.GetType().FullName + ": " + error.Message);
        }

        public void AddEventSubscription(object target, EventInfo eventInfo, Delegate handler)
        {
            EnsureOpen();
            if (eventInfo == null || handler == null)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "An event and handler are required for subscription.");
            eventInfo.AddEventHandler(target is Type ? null : target, handler);
            _cleanup.Add(new CleanupEntry
            {
                Kind = "subscription",
                Target = target,
                EventInfo = eventInfo,
                Handler = handler,
                Action = () => eventInfo.RemoveEventHandler(target is Type ? null : target, handler),
            });
            Touch();
        }

        public bool RemoveEventSubscription(object target, EventInfo eventInfo, Delegate handler)
        {
            EnsureOpen();
            var entry = _cleanup.LastOrDefault(item => item.Kind == "subscription" &&
                ReferenceEquals(item.Target, target) && item.EventInfo == eventInfo && Equals(item.Handler, handler));
            eventInfo.RemoveEventHandler(target is Type ? null : target, handler);
            if (entry != null) _cleanup.Remove(entry);
            Touch();
            return entry != null;
        }

        public void Invalidate(Func<object, bool> predicate, string reason)
        {
            if (_closed || predicate == null) return;
            foreach (var entry in _handles.Values)
            {
                bool invalidate;
                try { invalidate = predicate(entry.Value); }
                catch { invalidate = false; }
                if (invalidate) entry.InvalidReason = string.IsNullOrWhiteSpace(reason) ? "Handle target is no longer valid." : reason;
            }
            for (int i = _cleanup.Count - 1; i >= 0; i--)
            {
                var cleanup = _cleanup[i];
                if (cleanup.Kind != "subscription" || cleanup.Target == null) continue;
                bool invalidate;
                try { invalidate = predicate(cleanup.Target); }
                catch { invalidate = false; }
                if (!invalidate) continue;
                try { cleanup.Action?.Invoke(); }
                catch (Exception ex)
                {
                    if (_cleanupDiagnostics.Count < 32)
                        _cleanupDiagnostics.Add("subscription: " + ex.GetType().FullName + ": " + ex.Message);
                }
                _cleanup.RemoveAt(i);
            }
        }

        public ExecutionSessionCloseResult Close()
        {
            if (_closed) return new ExecutionSessionCloseResult { sessionId = Id, closed = true, alreadyClosed = true };
            var errors = new List<string>();
            int asyncStillRunning = _activeAsyncOperations;
            _cancellation.Cancel();
            int releasedSubscriptions = SubscriptionCount;
            foreach (string kind in new[] { "subscription", "callback" })
            {
                for (int i = _cleanup.Count - 1; i >= 0; i--)
                {
                    if (_cleanup[i].Kind != kind) continue;
                    try { _cleanup[i].Action?.Invoke(); }
                    catch (Exception ex)
                    {
                        string diagnostic = kind + ": " + ex.GetType().FullName + ": " + ex.Message;
                        errors.Add(diagnostic);
                        if (_cleanupDiagnostics.Count < 32) _cleanupDiagnostics.Add(diagnostic);
                    }
                }
            }
            var result = new ExecutionSessionCloseResult
            {
                sessionId = Id,
                closed = true,
                releasedHandles = _handles.Count,
                releasedVariables = VariableCount,
                releasedCallbacks = _callbackCount,
                releasedSubscriptions = releasedSubscriptions,
                dynamicTypesAwaitDomainReload = _dynamicTypeCount,
                releasedAsyncOperations = _releasedAsyncOperations,
                asyncOperationsStillRunning = asyncStillRunning,
                activeAsyncOperations = _activeAsyncOperations,
                completedAsyncOperations = _completedAsyncOperations,
                cancelledAsyncOperations = _cancelledAsyncOperations,
                asyncErrors = _asyncErrors,
                recentAsyncDiagnostics = RecentAsyncDiagnostics,
                cleanupErrors = errors.ToArray(),
            };
            _cleanup.Clear();
            _handles.Clear();
            _variables.Clear();
            _executionScope = null;
            _closed = true;
            _cancellation.Dispose();
            return result;
        }

        private sealed class AsyncOperationLease : IAsyncExecutionLease
        {
            private ExecutionSession _owner;
            private readonly string _description;
            public Exception Error { get; private set; }
            public AsyncOperationLease(ExecutionSession owner, string description) { _owner = owner; _description = description; }
            public void Fail(Exception error) { Error = error; }
            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.CompleteAsyncOperation(_description, Error);
            }
        }

        private void EnsureOpen()
        {
            if (_closed) throw new ExecutionContractException("EXECUTION_SESSION_CLOSED", "Execution session is closed.");
        }
    }

    [Serializable]
    public sealed class ExecutionSessionCloseResult
    {
        public string sessionId = "";
        public bool closed;
        public bool alreadyClosed;
        public int releasedHandles;
        public int releasedVariables;
        public int releasedCallbacks;
        public int releasedSubscriptions;
        public int dynamicTypesAwaitDomainReload;
        public int releasedAsyncOperations;
        public int asyncOperationsStillRunning;
        public int activeAsyncOperations;
        public int completedAsyncOperations;
        public int cancelledAsyncOperations;
        public int asyncErrors;
        public string[] recentAsyncDiagnostics = Array.Empty<string>();
        public string[] cleanupErrors = Array.Empty<string>();
    }

    public sealed class BoundInvocation
    {
        public MethodInfo Method { get; internal set; }
        public object[] Arguments { get; internal set; }
        public ParameterInfo[] Parameters { get; internal set; }
        public IDictionary<int, ExecutionValue> SourceArguments { get; internal set; }
        public string Signature => MethodBinder.FormatSignature(Method);
    }

    public static class MethodBinder
    {
        private sealed class Candidate
        {
            public MethodInfo Method;
            public object[] Arguments;
            public Dictionary<int, ExecutionValue> Sources;
            public int Score;
        }

        public static BoundInvocation Bind(
            Type declaringType,
            string methodName,
            bool isStatic,
            IReadOnlyList<ExecutionValue> supplied,
            IReadOnlyList<string> exactParameterTypes = null,
            IReadOnlyList<Type> genericTypeArguments = null,
            bool allowNonPublic = true,
            bool inferGenericTypeArguments = false)
        {
            if (declaringType == null) throw new ExecutionContractException("TYPE_NOT_FOUND", "Declaring type is required.");
            if (string.IsNullOrWhiteSpace(methodName)) throw new ExecutionContractException("METHOD_NAME_REQUIRED", "Method name is required.");
            supplied = supplied ?? Array.Empty<ExecutionValue>();
            var flags = BindingFlags.Public | (allowNonPublic ? BindingFlags.NonPublic : 0) |
                        (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var methods = declaringType.GetMethods(flags).Where(m => m.Name == methodName).ToArray();
            var successes = new List<Candidate>();
            var failures = new List<string>();

            foreach (var rawMethod in methods)
            {
                MethodInfo method = rawMethod;
                if (method.IsGenericMethodDefinition)
                {
                    IReadOnlyList<Type> closedArguments = genericTypeArguments;
                    if ((closedArguments == null || closedArguments.Count == 0) && inferGenericTypeArguments)
                        closedArguments = InferGenericArguments(method, supplied);
                    if (closedArguments == null || closedArguments.Count != method.GetGenericArguments().Length)
                    {
                        failures.Add(FormatSignature(method) + ": generic type argument count mismatch");
                        continue;
                    }
                    try { method = method.MakeGenericMethod(closedArguments.ToArray()); }
                    catch (Exception ex)
                    {
                        failures.Add(FormatSignature(method) + ": " + ex.Message);
                        continue;
                    }
                }
                else if (genericTypeArguments != null && genericTypeArguments.Count > 0)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (exactParameterTypes != null && exactParameterTypes.Count > 0)
                {
                    if (parameters.Length != exactParameterTypes.Count) continue;
                    bool exact = true;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var actual = parameters[i].ParameterType.IsByRef ? parameters[i].ParameterType.GetElementType() : parameters[i].ParameterType;
                        var requested = ExecutionTypeResolver.Resolve(exactParameterTypes[i]);
                        if (requested == null || requested != actual) { exact = false; break; }
                    }
                    if (!exact) continue;
                }

                if (TryBindCandidate(method, supplied, out var args, out var sources, out var score, out var failure))
                    successes.Add(new Candidate { Method = method, Arguments = args, Sources = sources, Score = score });
                else
                    failures.Add(FormatSignature(method) + ": " + failure);
            }

            if (successes.Count == 0)
                throw new ExecutionContractException(
                    inferGenericTypeArguments && methods.Any(method => method.IsGenericMethodDefinition)
                        ? "CSHARP_BIND_GENERIC_INFERENCE_FAILED"
                        : "REFLECTION_BIND_FAILED",
                    "No method overload could bind: " + declaringType.FullName + "." + methodName,
                    new Dictionary<string, object>
                    {
                        { "candidates", methods.Select(FormatSignature).ToArray() },
                        { "failures", failures.ToArray() },
                    });

            int bestScore = successes.Max(c => c.Score);
            var best = successes.Where(c => c.Score == bestScore).ToList();
            if (best.Count != 1)
                throw new ExecutionContractException(
                    "REFLECTION_BIND_AMBIGUOUS",
                    "Multiple overloads bind with the same score.",
                    new Dictionary<string, object> { { "candidates", best.Select(c => FormatSignature(c.Method)).ToArray() } });

            var selected = best[0];
            return new BoundInvocation
            {
                Method = selected.Method,
                Arguments = selected.Arguments,
                Parameters = selected.Method.GetParameters(),
                SourceArguments = selected.Sources,
            };
        }

        private static IReadOnlyList<Type> InferGenericArguments(MethodInfo method, IReadOnlyList<ExecutionValue> supplied)
        {
            var genericParameters = method.GetGenericArguments();
            var inferred = new Dictionary<Type, Type>();
            var parameters = method.GetParameters();
            int suppliedCount = supplied == null ? 0 : supplied.Count;
            for (int i = 0; i < suppliedCount; i++)
            {
                int parameterIndex = Math.Min(i, parameters.Length - 1);
                if (parameterIndex < 0) break;
                Type formal = parameters[parameterIndex].ParameterType;
                if (supplied[i]?.Value is LambdaValue lambda)
                {
                    InferFromLambda(formal, lambda, inferred);
                    continue;
                }
                Type actual = supplied[i]?.DeclaredType ?? supplied[i]?.Value?.GetType();
                if (actual == null) continue;
                if (parameters[parameterIndex].GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0 && i >= parameterIndex &&
                    !(i == parameterIndex && suppliedCount == parameters.Length && actual.IsArray))
                    formal = formal.GetElementType();
                InferFromPair(formal, actual, inferred);
            }
            var result = new Type[genericParameters.Length];
            for (int i = 0; i < genericParameters.Length; i++)
                if (!inferred.TryGetValue(genericParameters[i], out result[i])) return null;
            return result;
        }

        private static void InferFromLambda(Type formal, LambdaValue lambda, IDictionary<Type, Type> inferred)
        {
            if (formal == null || lambda == null) return;
            if (!typeof(Delegate).IsAssignableFrom(formal) && !(formal.IsGenericType && formal.GetGenericTypeDefinition().FullName.StartsWith("System.Func`", StringComparison.Ordinal))) return;
            MethodInfo invoke = formal.GetMethod("Invoke");
            if (invoke == null || invoke.GetParameters().Length != lambda.Parameters.Count) return;
            for (int i = 0; i < lambda.Parameters.Count; i++)
            {
                string typeName = lambda.Parameters[i].TypeName;
                if (string.IsNullOrWhiteSpace(typeName)) continue;
                Type actual = ExecutionTypeResolver.Resolve(typeName);
                if (actual != null) InferFromPair(invoke.GetParameters()[i].ParameterType, actual, inferred);
            }
        }

        private static void InferFromPair(Type formal, Type actual, IDictionary<Type, Type> inferred)
        {
            if (formal == null || actual == null) return;
            if (formal.IsByRef) formal = formal.GetElementType();
            if (formal.IsGenericParameter)
            {
                if (inferred.TryGetValue(formal, out var existing) && existing != actual)
                    inferred[formal] = FindCommonInferenceType(existing, actual);
                else inferred[formal] = actual;
                return;
            }
            if (formal.IsArray && actual.IsArray)
            {
                if (formal.GetArrayRank() != actual.GetArrayRank()) return;
                InferFromPair(formal.GetElementType(), actual.GetElementType(), inferred);
                return;
            }
            Type formalNullable = Nullable.GetUnderlyingType(formal);
            Type actualNullable = Nullable.GetUnderlyingType(actual);
            if (formalNullable != null)
            {
                InferFromPair(formalNullable, actualNullable ?? actual, inferred);
                return;
            }
            if (!formal.IsGenericType) return;
            Type definition = formal.GetGenericTypeDefinition();
            Type match = FindClosedGeneric(actual, definition);
            if (match == null) return;
            var formalArguments = formal.GetGenericArguments();
            var actualArguments = match.GetGenericArguments();
            for (int i = 0; i < formalArguments.Length; i++) InferFromPair(formalArguments[i], actualArguments[i], inferred);
        }

        private static Type FindClosedGeneric(Type actual, Type definition)
        {
            for (Type current = actual; current != null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == definition) return current;
            return actual.GetInterfaces()
                .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == definition)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static Type FindCommonInferenceType(Type left, Type right)
        {
            if (left == right) return left;
            if (left.IsAssignableFrom(right)) return left;
            if (right.IsAssignableFrom(left)) return right;
            var commonInterface = left.GetInterfaces().Intersect(right.GetInterfaces())
                .OrderByDescending(type => InterfaceDepth(type))
                .ThenBy(type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (commonInterface != null) return commonInterface;
            for (Type candidate = left.BaseType; candidate != null; candidate = candidate.BaseType)
                if (candidate.IsAssignableFrom(right)) return candidate;
            return typeof(object);
        }

        private static int InterfaceDepth(Type type)
        { return type == null ? 0 : 1 + type.GetInterfaces().Select(InterfaceDepth).DefaultIfEmpty(0).Max(); }

        private static bool TryBindCandidate(
            MethodInfo method,
            IReadOnlyList<ExecutionValue> supplied,
            out object[] arguments,
            out Dictionary<int, ExecutionValue> sources,
            out int score,
            out string failure)
        {
            var parameters = method.GetParameters();
            arguments = new object[parameters.Length];
            sources = new Dictionary<int, ExecutionValue>();
            score = 0;
            failure = "";
            var used = new HashSet<int>();
            int nextPositional = 0;

            for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                var parameter = parameters[parameterIndex];
                int suppliedIndex = -1;
                for (int i = 0; i < supplied.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    if (!string.IsNullOrWhiteSpace(supplied[i].Name) && string.Equals(supplied[i].Name, parameter.Name, StringComparison.Ordinal))
                    {
                        suppliedIndex = i;
                        break;
                    }
                }
                if (suppliedIndex < 0)
                {
                    while (nextPositional < supplied.Count && (used.Contains(nextPositional) || !string.IsNullOrWhiteSpace(supplied[nextPositional].Name)))
                        nextPositional++;
                    if (nextPositional < supplied.Count) suppliedIndex = nextPositional++;
                }

                bool isParams = parameter.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0;
                if (isParams)
                {
                    var elementType = parameter.ParameterType.GetElementType();
                    var remaining = new List<ExecutionValue>();
                    if (suppliedIndex >= 0) remaining.Add(supplied[suppliedIndex]);
                    for (int i = nextPositional; i < supplied.Count; i++)
                        if (!used.Contains(i) && string.IsNullOrWhiteSpace(supplied[i].Name)) remaining.Add(supplied[i]);
                    if (remaining.Count == 1 && TryConvert(remaining[0], parameter.ParameterType, out var directArray, out var directScore, out _))
                    {
                        arguments[parameterIndex] = directArray;
                        score += directScore;
                        for (int index = 0; index < supplied.Count; index++) if (ReferenceEquals(supplied[index], remaining[0])) { used.Add(index); break; }
                        continue;
                    }
                    var array = Array.CreateInstance(elementType, remaining.Count);
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        if (!TryConvert(remaining[i], elementType, out var converted, out var itemScore, out failure)) return false;
                        array.SetValue(converted, i);
                        score += itemScore;
                    }
                    arguments[parameterIndex] = array;
                    foreach (var item in remaining)
                    {
                        for (int index = 0; index < supplied.Count; index++)
                        {
                            if (ReferenceEquals(supplied[index], item))
                            {
                                used.Add(index);
                                break;
                            }
                        }
                    }
                    continue;
                }

                if (suppliedIndex < 0)
                {
                    if (parameter.IsOut)
                    {
                        arguments[parameterIndex] = DefaultValue(parameter.ParameterType.GetElementType());
                        continue;
                    }
                    if (parameter.HasDefaultValue || parameter.IsOptional)
                    {
                        arguments[parameterIndex] = parameter.DefaultValue == DBNull.Value ? Type.Missing : parameter.DefaultValue;
                        score += 1;
                        continue;
                    }
                    failure = "missing required parameter " + parameter.Name;
                    return false;
                }

                var source = supplied[suppliedIndex];
                used.Add(suppliedIndex);
                string direction = string.IsNullOrWhiteSpace(source.Direction) ? "in" : source.Direction.Trim().ToLowerInvariant();
                if (parameter.IsOut && direction != "out" && direction != "ref")
                {
                    failure = "parameter " + parameter.Name + " requires direction=out";
                    return false;
                }
                if (parameter.ParameterType.IsByRef && !parameter.IsOut && direction != "ref" && direction != "in")
                {
                    failure = "parameter " + parameter.Name + " requires direction=ref";
                    return false;
                }
                var targetType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                int conversionScore = 0;
                if (!parameter.IsOut && !TryConvert(source, targetType, out arguments[parameterIndex], out conversionScore, out failure))
                    return false;
                if (parameter.IsOut) arguments[parameterIndex] = DefaultValue(targetType);
                else score += conversionScore;
                sources[parameterIndex] = source;
            }

            if (used.Count != supplied.Count)
            {
                failure = "one or more supplied arguments did not match a parameter";
                return false;
            }
            return true;
        }

        private static bool TryConvert(ExecutionValue source, Type targetType, out object result, out int score, out string failure)
        {
            result = null;
            score = 0;
            failure = "";
            if (targetType == null) { failure = "target type is unavailable"; return false; }
            var value = source == null ? null : source.Value;
            var nullable = Nullable.GetUnderlyingType(targetType);
            if (value == null)
            {
                if (!targetType.IsValueType || nullable != null) { score = 20; return true; }
                failure = "null cannot bind to " + FriendlyName(targetType);
                return false;
            }
            Type valueType = value.GetType();
            if (targetType == valueType) { result = value; score = 100; return true; }
            if (targetType.IsAssignableFrom(valueType)) { result = value; score = 90; return true; }
            if (value is LambdaValue lambda && typeof(Delegate).IsAssignableFrom(targetType))
            {
                result = lambda.ToDelegate(targetType);
                score = 85;
                return true;
            }
            if (source.DeclaredType != null && source.DeclaredType == targetType)
            {
                result = value;
                score = 95;
                return true;
            }
            if (nullable != null) targetType = nullable;

            try
            {
                if (targetType.IsEnum)
                {
                    result = value is string text
                        ? Enum.Parse(targetType, text, true)
                        : Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    score = 70;
                    return true;
                }
                if (targetType == typeof(Guid))
                {
                    result = Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture));
                    score = 70;
                    return true;
                }
                if (targetType == typeof(Type) && value is string typeName)
                {
                    result = ExecutionTypeResolver.Resolve(typeName);
                    if (result == null) throw new InvalidOperationException("type not found");
                    score = 70;
                    return true;
                }
                if (targetType.IsArray && value is IEnumerable enumerable && !(value is string))
                {
                    var items = enumerable.Cast<object>().ToList();
                    var elementType = targetType.GetElementType();
                    var array = Array.CreateInstance(elementType, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!TryConvert(new ExecutionValue { Value = items[i] }, elementType, out var converted, out _, out failure)) return false;
                        array.SetValue(converted, i);
                    }
                    result = array;
                    score = 60;
                    return true;
                }
                result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                score = 50;
                return true;
            }
            catch (Exception ex)
            {
                failure = "cannot convert " + FriendlyName(valueType) + " to " + FriendlyName(targetType) + ": " + ex.Message;
                return false;
            }
        }

        private static object DefaultValue(Type type) { return type != null && type.IsValueType ? Activator.CreateInstance(type) : null; }
        private static string FriendlyName(Type type) { return type == null ? "(null)" : (type.FullName ?? type.Name); }

        public static string FormatSignature(MethodInfo method)
        {
            if (method == null) return "(null)";
            string generic = method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(t => t.Name)) + ">" : "";
            string parameters = string.Join(", ", method.GetParameters().Select(p =>
            {
                string modifier = p.IsOut ? "out " : (p.ParameterType.IsByRef ? "ref " : "");
                Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                return modifier + FriendlyName(type) + " " + p.Name;
            }));
            return FriendlyName(method.ReturnType) + " " + FriendlyName(method.DeclaringType) + "." + method.Name + generic + "(" + parameters + ")";
        }
    }

    public sealed class AwaitableResult
    {
        public bool WasAwaitable { get; internal set; }
        public string Status { get; internal set; }
        public object Value { get; internal set; }
    }

    public static class AwaitableAdapter
    {
        public static async Task<AwaitableResult> AwaitAsync(object value, string mode, int timeoutMs, CancellationToken cancellationToken = default(CancellationToken))
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "always" && mode != "never")
                throw new ExecutionContractException("INVALID_AWAIT_MODE", "awaitMode must be auto, always, or never.");
            Task task = ToTask(value);
            if (task == null || mode == "never")
            {
                if (task == null && mode == "always")
                    throw new ExecutionContractException("RESULT_NOT_AWAITABLE", "The invoked result is not awaitable.");
                return new AwaitableResult { WasAwaitable = task != null, Status = task == null ? "NotAwaitable" : "NotAwaited", Value = value };
            }

            int boundedTimeout = Math.Max(1, Math.Min(30000, timeoutMs <= 0 ? 3000 : timeoutMs));
            cancellationToken.ThrowIfCancellationRequested();
            Task cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var winner = await Task.WhenAny(task, Task.Delay(boundedTimeout), cancellationTask).ConfigureAwait(false);
            if (winner == cancellationTask)
                throw new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled while awaiting.",
                    new Dictionary<string, object> { { "stage", "cancelled" }, { "sideEffectsMayHaveOccurred", true } });
            if (winner != task)
                throw new ExecutionContractException("AWAITABLE_TIMEOUT", "Awaitable did not complete within " + boundedTimeout + "ms.");
            await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            object result = null;
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var property = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                if (property != null) result = property.GetValue(task, null);
            }
            return new AwaitableResult { WasAwaitable = true, Status = "Completed", Value = result };
        }

        private static Task ToTask(object value)
        {
            if (value is Task task) return task;
            if (value == null) return null;
            var type = value.GetType();
            if (type.FullName == "System.Threading.Tasks.ValueTask" ||
                (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.ValueTask`1"))
            {
                var asTask = type.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
                return asTask == null ? null : asTask.Invoke(value, null) as Task;
            }
            return null;
        }
    }
}
