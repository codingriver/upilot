using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot.Execution
{
    public interface IExecutionPolicy
    {
        void EnsureTypeAllowed(Type type);
        void EnsureMemberAllowed(MemberInfo member);
        void EnsureConstructionAllowed(Type type);
    }

    public sealed class ExplicitCallExecutionPolicy : IExecutionPolicy
    {
        public void EnsureTypeAllowed(Type type) { }
        public void EnsureMemberAllowed(MemberInfo member) { }
        public void EnsureConstructionAllowed(Type type) { }
    }

    public sealed class RestrictedEvalExecutionPolicy : IExecutionPolicy
    {
        private static readonly string[] DeniedTypePrefixes =
        {
            "System.IO.", "System.Net.", "System.Diagnostics.Process", "System.Environment",
            "System.Threading.Thread", "System.Reflection.Assembly", "System.AppDomain",
            "Microsoft.Win32.",
        };

        private static readonly HashSet<string> DeniedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Quit", "Exit", "ExitPlaymode", "EnterPlaymode", "LockReloadAssemblies",
            "UnlockReloadAssemblies", "RequestScriptCompilation", "CompilePlayerScripts",
            "LoadFrom", "LoadFile", "Load", "Start", "Kill", "Abort",
        };

        public void EnsureTypeAllowed(Type type)
        {
            if (type == null) return;
            string name = type.FullName ?? type.Name;
            if (DeniedTypePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                Denied("type", name);
        }

        public void EnsureMemberAllowed(MemberInfo member)
        {
            if (member == null) return;
            EnsureTypeAllowed(member.DeclaringType);
            if (DeniedMembers.Contains(member.Name)) Denied("member", (member.DeclaringType?.FullName ?? "") + "." + member.Name);
            if (member is MethodInfo method)
            {
                if (method.IsGenericMethod && method.GetGenericArguments().Any(t => t.FullName == "System.Reflection.Emit.AssemblyBuilder"))
                    Denied("member", MethodBinder.FormatSignature(method));
            }
        }

        public void EnsureConstructionAllowed(Type type)
        {
            EnsureTypeAllowed(type);
            if (type != null && typeof(Delegate).IsAssignableFrom(type)) Denied("construction", type.FullName);
        }

        private static void Denied(string kind, string target)
        {
            throw new ExecutionContractException(
                "EXECUTION_POLICY_DENIED",
                "The C# evaluation policy denied " + kind + ": " + target,
                new Dictionary<string, object> { { "kind", kind }, { "target", target } });
        }
    }

    public sealed class VariableCell
    {
        public object Value { get; set; }
        public VariableCell(object value) { Value = value; }
    }

    public sealed class ExecutionScope
    {
        private readonly Dictionary<string, VariableCell> _cells = new Dictionary<string, VariableCell>(StringComparer.Ordinal);
        public ExecutionScope Parent { get; }
        public ExecutionScope(ExecutionScope parent = null) { Parent = parent; }
        public void Declare(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ExecutionContractException("INVALID_VARIABLE_NAME", "Variable name is required.");
            if (_cells.ContainsKey(name)) throw new ExecutionContractException("CSHARP_DUPLICATE_LOCAL", "A variable named '" + name + "' is already declared in this scope.");
            _cells[name] = new VariableCell(value);
        }
        public bool TryResolve(string name, out VariableCell cell)
        {
            if (_cells.TryGetValue(name ?? "", out cell)) return true;
            return Parent != null && Parent.TryResolve(name, out cell);
        }
        public void SetOrDeclare(string name, object value)
        {
            if (TryResolve(name, out var cell)) cell.Value = value;
            else Declare(name, value);
        }
        public IDictionary<string, object> Snapshot()
        {
            var result = Parent == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(Parent.Snapshot(), StringComparer.Ordinal);
            foreach (var pair in _cells) result[pair.Key] = pair.Value.Value;
            return result;
        }
    }

    internal sealed class EvaluationState
    {
        public bool SideEffects;
        public ExecutionSourceSpan CurrentSpan;
        public ExecutionSourceSpan LastCompletedSpan;
        public Exception CurrentCaughtException;
    }

    /// <summary>Request-local, value-free execution telemetry for bounded expression diagnostics.</summary>
    public sealed class CSharpExecutionDiagnostics
    {
        private const int MaxCompletedBoundaries = 64;
        private long _resolveTicks;
        private long _bindTicks;
        private long _invokeTicks;
        private long _encodeTicks;
        private int _getterCallCount;
        private int _methodCallCount;
        private readonly string[] _completedBoundaries = new string[MaxCompletedBoundaries];
        private int _completedBoundaryCount;
        private int _droppedDiagnosticCount;

        public long ResolveMs => ToMilliseconds(Interlocked.Read(ref _resolveTicks));
        public long BindMs => ToMilliseconds(Interlocked.Read(ref _bindTicks));
        public long InvokeMs => ToMilliseconds(Interlocked.Read(ref _invokeTicks));
        public long EncodeMs => ToMilliseconds(Interlocked.Read(ref _encodeTicks));
        public int GetterCallCount => Volatile.Read(ref _getterCallCount);
        public int MethodCallCount => Volatile.Read(ref _methodCallCount);
        public int DroppedDiagnosticCount => Volatile.Read(ref _droppedDiagnosticCount);
        public string[] CompletedBoundaries
        {
            get
            {
                lock (_completedBoundaries)
                {
                    var count = Math.Min(_completedBoundaryCount, MaxCompletedBoundaries);
                    var result = new string[count];
                    Array.Copy(_completedBoundaries, result, count);
                    return result;
                }
            }
        }

        internal T MeasureResolve<T>(Func<T> action) { return Measure(action, ref _resolveTicks, "resolve.completed"); }
        internal T MeasureBind<T>(Func<T> action) { return Measure(action, ref _bindTicks, "bind.completed"); }
        internal T MeasureInvoke<T>(Func<T> action) { return Measure(action, ref _invokeTicks, "invoke.completed"); }
        public T MeasureEncode<T>(Func<T> action) { return Measure(action, ref _encodeTicks, "encode.completed"); }
        internal void CountGetter() { Interlocked.Increment(ref _getterCallCount); }
        internal void CountMethod() { Interlocked.Increment(ref _methodCallCount); }

        private T Measure<T>(Func<T> action, ref long ticks, string completedBoundary)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                T result = action();
                RecordCompletedBoundary(completedBoundary);
                return result;
            }
            finally { Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - started); }
        }

        private void RecordCompletedBoundary(string boundary)
        {
            lock (_completedBoundaries)
            {
                if (_completedBoundaryCount < MaxCompletedBoundaries)
                    _completedBoundaries[_completedBoundaryCount++] = boundary;
                else
                    _droppedDiagnosticCount++;
            }
        }

        private static long ToMilliseconds(long ticks)
        {
            return Math.Max(0L, ticks * 1000L / Stopwatch.Frequency);
        }
    }

    public sealed class CSharpEvaluationContext
    {
        private readonly ExecutionScope _scope;
        private readonly Func<Func<object>, object> _invocationScheduler;
        private readonly CancellationToken _cancellationToken;
        private readonly EvaluationState _state;

        public ExecutionBudget Budget { get; }
        public CSharpExecutionDiagnostics Diagnostics { get; }
        public ExecutionResourceDiagnostics ResourceDiagnostics { get; }
        public IExecutionPolicy Policy { get; }
        public IReadOnlyList<string> Imports { get; }
        public bool SideEffectsMayHaveOccurred { get => _state.SideEffects; internal set => _state.SideEffects = value; }
        public IExecutionSessionLifetime SessionLifetime { get; }
        public CancellationToken CancellationToken => _cancellationToken;
        public ExecutionSourceSpan CurrentSpan => _state.CurrentSpan;
        public ExecutionSourceSpan LastCompletedSpan => _state.LastCompletedSpan;
        internal Exception CurrentCaughtException { get => _state.CurrentCaughtException; set => _state.CurrentCaughtException = value; }
        internal ExecutionScope Scope => _scope;

        public CSharpEvaluationContext(
            IDictionary<string, object> variables = null,
            IEnumerable<string> imports = null,
            ExecutionBudget budget = null,
            IExecutionPolicy policy = null,
            Func<Func<object>, object> invocationScheduler = null,
            CancellationToken cancellationToken = default(CancellationToken),
            IExecutionSessionLifetime sessionLifetime = null,
            ExecutionResourceDiagnostics resourceDiagnostics = null)
        {
            if (sessionLifetime is ExecutionSession persistent)
                _scope = persistent.GetOrCreateExecutionScope(variables);
            else
            {
                _scope = new ExecutionScope();
                if (variables != null) foreach (var pair in variables) _scope.Declare(pair.Key, pair.Value);
            }
            Imports = (imports ?? new[] { "System" }).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            Budget = budget ?? new ExecutionBudget();
            Diagnostics = new CSharpExecutionDiagnostics();
            ResourceDiagnostics = resourceDiagnostics ?? new ExecutionResourceDiagnostics();
            Policy = policy ?? new RestrictedEvalExecutionPolicy();
            _invocationScheduler = invocationScheduler;
            _cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : (sessionLifetime?.CancellationToken ?? cancellationToken);
            SessionLifetime = sessionLifetime;
            _state = new EvaluationState();
            Budget.SetSpanProvider(() => CurrentSpan);
        }

        private CSharpEvaluationContext(CSharpEvaluationContext parent, ExecutionScope scope)
        {
            _scope = scope;
            Imports = parent.Imports;
            Budget = parent.Budget;
            Diagnostics = parent.Diagnostics;
            ResourceDiagnostics = parent.ResourceDiagnostics;
            Policy = parent.Policy;
            _invocationScheduler = parent._invocationScheduler;
            _cancellationToken = parent._cancellationToken;
            SessionLifetime = parent.SessionLifetime;
            _state = parent._state;
        }

        public bool TryGetVariable(string name, out object value)
        {
            if (_scope.TryResolve(name, out var cell)) { value = cell.Value; return true; }
            value = null; return false;
        }
        public object GetVariable(string name)
        {
            if (!_scope.TryResolve(name, out var cell))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unknown variable: " + name);
            return cell.Value;
        }
        public void SetVariable(string name, object value)
        {
            if (!_scope.TryResolve(name, out var cell))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unknown variable: " + name);
            cell.Value = value;
            SideEffectsMayHaveOccurred = true;
        }
        public void DeclareVariable(string name, object value) { _scope.Declare(name, value); SideEffectsMayHaveOccurred = true; }
        public IDictionary<string, object> SnapshotVariables() { return _scope.Snapshot(); }
        public void CheckCancellation()
        {
            if (!_cancellationToken.IsCancellationRequested) return;
            Budget.CheckCancellation();
            throw new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                new Dictionary<string, object> { { "stage", "cancelled" }, { "sourceSpan", CurrentSpan?.Clone() } });
        }
        internal void EnterNode(AstNode node)
        {
            if (node?.Span != null) _state.CurrentSpan = node.Span;
            CheckCancellation();
        }
        public object Invoke(Func<object> action)
        {
            Budget.CheckTime();
            SideEffectsMayHaveOccurred = true;
            object result = _invocationScheduler == null ? action() : _invocationScheduler(action);
            _state.LastCompletedSpan = CurrentSpan?.Clone();
            Budget.CheckTime();
            return result;
        }
        public CSharpEvaluationContext Fork(IDictionary<string, object> additions)
        {
            var child = new ExecutionScope(_scope);
            if (additions != null) foreach (var pair in additions) child.Declare(pair.Key, pair.Value);
            return new CSharpEvaluationContext(this, child);
        }
        internal CSharpEvaluationContext ForkFromCapturedScope(ExecutionScope capturedScope, IDictionary<string, object> additions)
        {
            var child = new ExecutionScope(capturedScope ?? _scope);
            if (additions != null) foreach (var pair in additions) child.Declare(pair.Key, pair.Value);
            return new CSharpEvaluationContext(this, child);
        }
        internal CSharpEvaluationContext CreatePersistentInvocationRuntime()
        {
            if (SessionLifetime == null) return this;
            var sessionToken = SessionLifetime.CancellationToken;
            return new CSharpEvaluationContext(
                imports: Imports,
                budget: new ExecutionBudget(cancellationToken: sessionToken),
                policy: Policy,
                invocationScheduler: _invocationScheduler,
                cancellationToken: sessionToken,
                sessionLifetime: SessionLifetime);
        }
        public CSharpEvaluationContext CreateChild() { return new CSharpEvaluationContext(this, new ExecutionScope(_scope)); }
    }

    public sealed class CSharpEvaluationResult
    {
        public object Value { get; internal set; }
        public IDictionary<string, object> Variables { get; internal set; }
        public bool SideEffectsMayHaveOccurred { get; internal set; }
        public ExecutionBudget Budget { get; internal set; }
        public CSharpExecutionDiagnostics Diagnostics { get; internal set; }
        public string ModeUsed { get; internal set; }
    }

    public sealed class LambdaParameter
    {
        public string Name { get; }
        public string TypeName { get; }
        public LambdaParameter(string name, string typeName = null) { Name = name; TypeName = typeName ?? ""; }
    }

    public sealed class LambdaValue
    {
        private readonly LambdaParameter[] _parameters;
        private readonly Expr _expressionBody;
        private readonly Stmt _blockBody;
        private readonly CSharpEvaluationContext _captured;
        private readonly bool _isAsync;
        private readonly Dictionary<Type, Delegate> _delegateCache = new Dictionary<Type, Delegate>();

        internal LambdaValue(LambdaParameter[] parameters, Expr expressionBody, Stmt blockBody, bool isAsync, CSharpEvaluationContext captured)
        {
            _parameters = parameters ?? Array.Empty<LambdaParameter>();
            _expressionBody = expressionBody;
            _blockBody = blockBody;
            _isAsync = isAsync;
            _captured = captured;
        }

        public bool IsAsync => _isAsync;
        public IReadOnlyList<LambdaParameter> Parameters => _parameters;

        private object InvokeCore(CSharpEvaluationContext caller, object[] arguments)
        {
            if (_captured.SessionLifetime != null && _captured.SessionLifetime.IsClosed)
                throw new ExecutionContractException("EXECUTION_SESSION_CLOSED", "The closure's execution session is closed.");
            var additions = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < _parameters.Length; i++)
            {
                object value = i < arguments.Length ? arguments[i] : null;
                if (!string.IsNullOrWhiteSpace(_parameters[i].TypeName))
                {
                    Type type = ExecutionTypeResolver.Resolve(_parameters[i].TypeName, _captured.Imports);
                    if (type == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Lambda parameter type was not found: " + _parameters[i].TypeName);
                    value = RuntimeConvert.ChangeType(value, type);
                }
                additions[_parameters[i].Name] = value;
            }
            var runtime = caller ?? _captured.CreatePersistentInvocationRuntime();
            var context = runtime.ForkFromCapturedScope(_captured.Scope, additions);
            try { return _expressionBody != null ? _expressionBody.Evaluate(context) : _blockBody?.Execute(context); }
            catch (ReturnSignal signal) { return signal.Value; }
        }

        public object Invoke(params object[] arguments) { return InvokeCore(null, arguments); }
        internal object Invoke(CSharpEvaluationContext caller, params object[] arguments) { return InvokeCore(caller, arguments); }

        private async Task<object> InvokeAsyncCore(CSharpEvaluationContext caller, object[] arguments)
        {
            IAsyncExecutionLease lease = null;
            try
            {
                if (_captured.SessionLifetime != null) lease = _captured.SessionLifetime.BeginAsyncOperation("C# async lambda");
                var cancellationToken = caller?.CancellationToken ??
                    (_captured.SessionLifetime?.CancellationToken ?? _captured.CancellationToken);
                return await Task.Run(() => InvokeCore(caller, arguments), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" } });
                lease?.Fail(cancelled);
                throw cancelled;
            }
            catch (Exception ex) { lease?.Fail(ex); throw; }
            finally { lease?.Dispose(); }
        }
        public Task<object> InvokeAsync(params object[] arguments) { return InvokeAsyncCore(null, arguments); }
        internal Task<object> InvokeAsync(CSharpEvaluationContext caller, params object[] arguments) { return InvokeAsyncCore(caller, arguments); }
        public async Task InvokeTask(params object[] arguments) { await InvokeAsync(arguments).ConfigureAwait(false); }
        public async Task<T> InvokeTaskOf<T>(params object[] arguments)
        { return (T)RuntimeConvert.ChangeType(await InvokeAsync(arguments).ConfigureAwait(false), typeof(T)); }

        public Delegate ToDelegate(Type delegateType)
        {
            if (delegateType == null || !typeof(Delegate).IsAssignableFrom(delegateType))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Lambda target is not a delegate type.");
            lock (_delegateCache) if (_delegateCache.TryGetValue(delegateType, out var cached)) return cached;
            var invoke = delegateType.GetMethod("Invoke");
            if (_parameters.Length != invoke.GetParameters().Length)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Lambda parameter count does not match delegate signature.");
            if (_isAsync && invoke.ReturnType != typeof(Task) && !(invoke.ReturnType.IsGenericType && invoke.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
                throw new ExecutionContractException("CSHARP_ASYNC_VOID_UNSUPPORTED", "Async lambdas can only convert to Task or Task<T> delegates.");
            for (int i = 0; i < _parameters.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(_parameters[i].TypeName)) continue;
                Type declared = ExecutionTypeResolver.Resolve(_parameters[i].TypeName, _captured.Imports);
                if (declared != invoke.GetParameters()[i].ParameterType)
                    throw new ExecutionContractException("CSHARP_BIND_ERROR", "Typed lambda parameter does not match delegate parameter " + invoke.GetParameters()[i].Name + ".");
            }
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var values = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
            Expression body;
            if (_isAsync)
            {
                if (invoke.ReturnType == typeof(Task))
                    body = Expression.Call(Expression.Constant(this), typeof(LambdaValue).GetMethod(nameof(InvokeTask)), values);
                else
                    body = Expression.Call(Expression.Constant(this), typeof(LambdaValue).GetMethod(nameof(InvokeTaskOf)).MakeGenericMethod(invoke.ReturnType.GetGenericArguments()[0]), values);
            }
            else
            {
                var call = Expression.Call(Expression.Constant(this), typeof(LambdaValue).GetMethod(nameof(Invoke)), values);
                body = invoke.ReturnType == typeof(void) ? (Expression)Expression.Block(call, Expression.Empty()) : Expression.Convert(call, invoke.ReturnType);
            }
            var compiled = Expression.Lambda(delegateType, body, parameters).Compile();
            lock (_delegateCache) _delegateCache[delegateType] = compiled;
            return compiled;
        }
    }

    public sealed class CSharpProgram
    {
        private readonly Stmt _root;
        private readonly SourceMap _sourceMap;
        public bool IsExpressionOnly { get; }
        internal Stmt Root => _root;
        internal SourceMap SourceMap => _sourceMap;

        internal CSharpProgram(Stmt root, bool isExpressionOnly, string source)
        {
            _root = root;
            IsExpressionOnly = isExpressionOnly;
            _sourceMap = new SourceMap(source);
        }

        public CSharpEvaluationResult Execute(CSharpEvaluationContext context)
        {
            if (AstFeatureInspector.ContainsAsync(_root))
                throw new ExecutionContractException("CSHARP_ASYNC_REQUIRES_ASYNC_ENTRY", "Programs containing await or async lambdas must use ExecuteAsync/EvaluateAsync.");
            return ExecuteCore(context);
        }

        private CSharpEvaluationResult ExecuteCore(CSharpEvaluationContext context)
        {
            context = context ?? new CSharpEvaluationContext();
            object value = null;
            try { value = _root.Execute(context); }
            catch (ReturnSignal signal) { value = signal.Value; }
            catch (ExecutionContractException ex)
            {
                CSharpSubsetEngine.AttachExecutionDetail(ex, context, _sourceMap);
                throw;
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" } });
                CSharpSubsetEngine.AttachExecutionDetail(cancelled, context, _sourceMap);
                throw cancelled;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
                var runtime = new ExecutionContractException("CSHARP_RUNTIME_ERROR", inner.GetType().FullName + ": " + inner.Message,
                    new Dictionary<string, object> { { "exceptionType", inner.GetType().FullName }, { "stackTrace", BoundStack(inner.StackTrace) } });
                CSharpSubsetEngine.AttachExecutionDetail(runtime, context, _sourceMap);
                throw runtime;
            }
            return new CSharpEvaluationResult
            {
                Value = RequireResolvedResult(value),
                Variables = context.SnapshotVariables().ToDictionary(pair => pair.Key, pair => RequireResolvedResult(pair.Value), StringComparer.Ordinal),
                SideEffectsMayHaveOccurred = context.SideEffectsMayHaveOccurred,
                Budget = context.Budget,
                Diagnostics = context.Diagnostics,
                ModeUsed = IsExpressionOnly ? "expression" : "statements",
            };
        }

        internal static object RequireResolvedResult(object value)
        {
            if (value is UnresolvedName unresolved)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Type or variable was not found: " + unresolved.Path);
            if (ReferenceEquals(value, ConditionalAccessNull.Instance)) return null;
            return StaticTypeTarget.Unwrap(value);
        }

        public Task<CSharpEvaluationResult> ExecuteAsync(CSharpEvaluationContext context)
        {
            context = context ?? new CSharpEvaluationContext();
            return Task.Run(() => ExecuteCore(context), context.CancellationToken);
        }

        public void ValidateSynchronousEmitProfile()
        { AstFeatureInspector.ValidateSynchronousEmit(_root); }
        public void ValidateReflectionExpressionProfile()
        {
            if (!IsExpressionOnly)
                throw new ExecutionContractException("CSHARP_UNSUPPORTED_SYNTAX", "Reflection expression mode accepts exactly one expression.");
            AstFeatureInspector.ValidateReflectionExpression(_root);
        }

        private static string BoundStack(string stack)
        { stack = stack ?? ""; return stack.Length <= 4096 ? stack : stack.Substring(0, 4096); }
    }

    public static class CSharpSubsetEngine
    {
        public const string LanguageProfile = "upilot-csharp-subset-v2";

        public static CSharpProgram Parse(string code, string mode = "auto")
        {
            try { return new Parser(code, mode).ParseProgram(); }
            catch (ExecutionContractException) { throw; }
            catch (Exception ex)
            {
                throw new ExecutionContractException("CSHARP_PARSE_ERROR", ex.Message);
            }
        }

        public static CSharpEvaluationResult Evaluate(string code, string mode, CSharpEvaluationContext context)
        {
            try { return Parse(code, mode).Execute(context); }
            catch (ExecutionContractException) { throw; }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", inner.GetType().FullName + ": " + inner.Message);
            }
            catch (OperationCanceledException)
            {
                throw new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" }, { "sideEffectsMayHaveOccurred", context?.SideEffectsMayHaveOccurred ?? false } });
            }
            catch (Exception ex)
            {
                throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", ex.GetType().FullName + ": " + ex.Message);
            }
        }

        public static Task<CSharpEvaluationResult> EvaluateAsync(string code, string mode, CSharpEvaluationContext context)
        { return Parse(code, mode).ExecuteAsync(context); }

        internal static void AttachExecutionDetail(ExecutionContractException ex, CSharpEvaluationContext context, SourceMap sourceMap)
        {
            if (ex == null) return;
            if (!ex.Detail.ContainsKey("stage"))
                ex.Detail["stage"] = InferFailureStage(ex.Code);
            if (!ex.Detail.ContainsKey("sourceSpan") && context?.CurrentSpan != null)
                ex.Detail["sourceSpan"] = sourceMap.Resolve(context.CurrentSpan.start, context.CurrentSpan.length);
            if (!ex.Detail.ContainsKey("lastCompletedSpan") && context?.LastCompletedSpan != null)
                ex.Detail["lastCompletedSpan"] = sourceMap.Resolve(context.LastCompletedSpan.start, context.LastCompletedSpan.length);
            if (!ex.Detail.ContainsKey("sideEffectsMayHaveOccurred"))
                ex.Detail["sideEffectsMayHaveOccurred"] = context?.SideEffectsMayHaveOccurred ?? false;
        }

        internal static object RethrowTargetInvocation(TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex?.InnerException ?? ex).Throw();
            return null;
        }

        private static string InferFailureStage(string code)
        {
            code = code ?? "";
            if (code == "EXECUTION_CANCELLED") return "cancelled";
            if (code == "EXECUTION_BUDGET_EXCEEDED") return "budget";
            if (code.StartsWith("CSHARP_PARSE", StringComparison.Ordinal) || code == "CSHARP_UNSUPPORTED_SYNTAX") return "parse";
            if (code.StartsWith("EXECUTION_POLICY", StringComparison.Ordinal)) return "policy";
            if (code.StartsWith("CSHARP_BIND", StringComparison.Ordinal) ||
                code.StartsWith("REFLECTION_BIND", StringComparison.Ordinal) ||
                code.StartsWith("TYPE_", StringComparison.Ordinal)) return "bind";
            return "runtime";
        }
    }

    internal abstract class AstNode
    {
        public ExecutionSourceSpan Span { get; set; }
        protected void Enter(CSharpEvaluationContext context) { context?.EnterNode(this); }
    }

    internal static class AstFeatureInspector
    {
        public static void ValidateSynchronousEmit(AstNode root)
        {
            Visit(root, new HashSet<object>());
        }

        public static void ValidateReflectionExpression(AstNode root)
        { Visit(root, new HashSet<object>(), true); }

        public static bool ContainsAsync(AstNode root)
        { return Find(root, new HashSet<object>(), value => value is AwaitExpr || value is LambdaExpr lambda && lambda.IsAsync); }

        private static bool Find(object value, ISet<object> visited, Func<object, bool> predicate)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return false;
            if (!visited.Add(value)) return false;
            if (predicate(value)) return true;
            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable) if (Find(item, visited, predicate)) return true;
                return false;
            }
            if (!(value is AstNode) && !(value is CatchClause) && !(value is ArrayInitializerNode)) return false;
            return value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(field => Find(field.GetValue(value), visited, predicate));
        }

        private static void Visit(object value, ISet<object> visited, bool reflectionExpression = false)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
            if (!visited.Add(value)) return;
            if (value is AwaitExpr || value is LambdaExpr)
            {
                var node = (AstNode)value;
                throw new ExecutionContractException(reflectionExpression ? "CSHARP_UNSUPPORTED_SYNTAX" : "CSHARP_EMIT_UNSUPPORTED_NODE",
                    value is AwaitExpr
                        ? (reflectionExpression ? "await is not supported by the reflection expression profile." : "await is not supported by the emitted synchronous type-body profile.")
                        : (reflectionExpression ? "lambda is not supported by the reflection expression profile." : "lambda closures are not supported by the emitted synchronous type-body profile."),
                    new Dictionary<string, object> { { "sourceSpan", node.Span?.Clone() }, { "stage", "policy" }, { "node", value.GetType().Name } });
            }
            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable) Visit(item, visited, reflectionExpression);
                return;
            }
            Type type = value.GetType();
            if (!(value is AstNode) && !(value is CatchClause) && !(value is ArrayInitializerNode)) return;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Visit(field.GetValue(value), visited, reflectionExpression);
        }
    }

    internal abstract class Stmt : AstNode
    {
        public abstract object Execute(CSharpEvaluationContext context);
    }

    internal abstract class Expr : AstNode
    {
        public abstract object Evaluate(CSharpEvaluationContext context);
        internal virtual AssignmentReference ResolveAssignmentReference(CSharpEvaluationContext context)
        {
            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Expression is not assignable.");
        }
        public virtual void Assign(CSharpEvaluationContext context, object value)
        {
            ResolveAssignmentReference(context).Set(value);
        }
    }

    internal sealed class AssignmentReference
    {
        private readonly Func<object> _getter;
        private readonly Action<object> _setter;
        public object EventTarget { get; }
        public EventInfo EventInfo { get; }

        public AssignmentReference(Func<object> getter, Action<object> setter, object eventTarget = null, EventInfo eventInfo = null)
        {
            _getter = getter;
            _setter = setter;
            EventTarget = eventTarget;
            EventInfo = eventInfo;
        }

        public object Get()
        {
            if (_getter == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Expression is not readable.");
            return _getter();
        }

        public void Set(object value)
        {
            if (_setter == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Expression is not assignable.");
            _setter(value);
        }
    }

    internal sealed class ConditionalAccessNull
    {
        public static readonly ConditionalAccessNull Instance = new ConditionalAccessNull();
        private ConditionalAccessNull() { }
    }

    internal sealed class BlockStmt : Stmt
    {
        private readonly IReadOnlyList<Stmt> _statements;
        private readonly bool _createsScope;
        internal IReadOnlyList<Stmt> Statements => _statements;
        internal bool CreatesScope => _createsScope;
        public BlockStmt(IReadOnlyList<Stmt> statements, bool createsScope = false) { _statements = statements; _createsScope = createsScope; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            if (_createsScope) context = context.CreateChild();
            object result = null;
            foreach (var statement in _statements)
            {
                context.EnterNode(statement);
                context.Budget.CountStatement();
                result = statement.Execute(context);
            }
            return result;
        }
    }

    internal sealed class ExpressionStmt : Stmt
    {
        private readonly Expr _expression;
        internal Expr Expression => _expression;
        public ExpressionStmt(Expr expression) { _expression = expression; }
        public override object Execute(CSharpEvaluationContext context) { Enter(context); return _expression.Evaluate(context); }
    }

    internal sealed class VariableStmt : Stmt
    {
        private readonly string _name;
        private readonly string _typeName;
        private readonly Expr _initializer;
        internal string Name => _name;
        internal string TypeName => _typeName;
        internal Expr Initializer => _initializer;
        public VariableStmt(string name, string typeName, Expr initializer) { _name = name; _typeName = typeName; _initializer = initializer; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            object value = _initializer == null ? null : _initializer.Evaluate(context);
            value = CSharpProgram.RequireResolvedResult(value);
            if (_typeName != "var")
            {
                Type target = ExecutionTypeResolver.Resolve(_typeName, context.Imports);
                if (target == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Variable type was not found: " + _typeName);
                value = RuntimeConvert.ChangeType(value, target);
            }
            context.DeclareVariable(_name, value);
            return value;
        }
    }

    internal sealed class IfStmt : Stmt
    {
        private readonly Expr _condition;
        private readonly Stmt _whenTrue;
        private readonly Stmt _whenFalse;
        internal Expr Condition => _condition;
        internal Stmt WhenTrue => _whenTrue;
        internal Stmt WhenFalse => _whenFalse;
        public IfStmt(Expr condition, Stmt whenTrue, Stmt whenFalse) { _condition = condition; _whenTrue = whenTrue; _whenFalse = whenFalse; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            return RuntimeConvert.ToBool(_condition.Evaluate(context))
                ? _whenTrue?.Execute(context)
                : _whenFalse?.Execute(context);
        }
    }

    internal sealed class WhileStmt : Stmt
    {
        private readonly Expr _condition;
        private readonly Stmt _body;
        internal Expr Condition => _condition;
        internal Stmt Body => _body;
        public WhileStmt(Expr condition, Stmt body) { _condition = condition; _body = body; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            object result = null;
            while (RuntimeConvert.ToBool(_condition.Evaluate(context)))
            {
                context.Budget.CountLoop();
                try { result = _body.Execute(context); }
                catch (ContinueSignal) { continue; }
                catch (BreakSignal) { break; }
            }
            return result;
        }
    }

    internal sealed class ForStmt : Stmt
    {
        private readonly Stmt _initializer;
        private readonly Expr _condition;
        private readonly Expr _increment;
        private readonly Stmt _body;
        internal Stmt Initializer => _initializer;
        internal Expr Condition => _condition;
        internal Expr Increment => _increment;
        internal Stmt Body => _body;
        public ForStmt(Stmt initializer, Expr condition, Expr increment, Stmt body)
        {
            _initializer = initializer; _condition = condition; _increment = increment; _body = body;
        }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            var loopContext = context.CreateChild();
            object result = _initializer?.Execute(loopContext);
            while (_condition == null || RuntimeConvert.ToBool(_condition.Evaluate(loopContext)))
            {
                loopContext.Budget.CountLoop();
                try { result = _body.Execute(loopContext); }
                catch (ContinueSignal) { }
                catch (BreakSignal) { break; }
                _increment?.Evaluate(loopContext);
            }
            return result;
        }
    }

    internal sealed class ForeachStmt : Stmt
    {
        private readonly string _name;
        private readonly Expr _source;
        private readonly Stmt _body;
        public ForeachStmt(string name, Expr source, Stmt body) { _name = name; _source = source; _body = body; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            var enumerable = _source.Evaluate(context) as IEnumerable;
            if (enumerable == null) throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "foreach source is not enumerable.");
            object result = null;
            foreach (var item in enumerable)
            {
                context.Budget.CountLoop();
                var iteration = context.CreateChild();
                iteration.DeclareVariable(_name, item);
                try { result = _body.Execute(iteration); }
                catch (ContinueSignal) { continue; }
                catch (BreakSignal) { break; }
            }
            return result;
        }
    }

    internal sealed class ReturnStmt : Stmt
    {
        private readonly Expr _value;
        internal Expr Value => _value;
        public ReturnStmt(Expr value) { _value = value; }
        public override object Execute(CSharpEvaluationContext context) { Enter(context); throw new ReturnSignal(_value?.Evaluate(context)); }
    }

    internal sealed class SignalStmt : Stmt
    {
        private readonly string _kind;
        internal string Kind => _kind;
        public SignalStmt(string kind) { _kind = kind; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            if (_kind == "break") throw new BreakSignal();
            throw new ContinueSignal();
        }
    }

    internal sealed class CatchClause
    {
        public string TypeName { get; }
        public string VariableName { get; }
        public Stmt Body { get; }
        public CatchClause(string typeName, string variableName, Stmt body)
        { TypeName = typeName ?? ""; VariableName = variableName ?? ""; Body = body; }
    }

    internal sealed class TryStmt : Stmt
    {
        private readonly Stmt _tryBody;
        private readonly IReadOnlyList<CatchClause> _catches;
        private readonly Stmt _finallyBody;
        public TryStmt(Stmt tryBody, IReadOnlyList<CatchClause> catches, Stmt finallyBody)
        { _tryBody = tryBody; _catches = catches ?? Array.Empty<CatchClause>(); _finallyBody = finallyBody; }

        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            object result = null;
            Exception primary = null;
            try
            {
                result = _tryBody.Execute(context.CreateChild());
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException invocation && invocation.InnerException != null) ex = invocation.InnerException;
                if (IsInfrastructure(ex) || ex is ReturnSignal || ex is BreakSignal || ex is ContinueSignal) primary = ex;
                else
                {
                    CatchClause match = null;
                    foreach (var clause in _catches)
                    {
                        if (string.IsNullOrWhiteSpace(clause.TypeName)) { match = clause; break; }
                        Type catchType = ExecutionTypeResolver.Resolve(clause.TypeName, context.Imports);
                        if (catchType == null || !typeof(Exception).IsAssignableFrom(catchType))
                            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Catch type is not an exception type: " + clause.TypeName);
                        if (catchType.IsInstanceOfType(ex)) { match = clause; break; }
                    }
                    if (match == null) primary = ex;
                    else
                    {
                        var catchContext = context.CreateChild();
                        if (!string.IsNullOrWhiteSpace(match.VariableName)) catchContext.DeclareVariable(match.VariableName, ex);
                        Exception previous = catchContext.CurrentCaughtException;
                        catchContext.CurrentCaughtException = ex;
                        try { result = match.Body.Execute(catchContext); }
                        catch (Exception catchError) { primary = catchError; }
                        finally { catchContext.CurrentCaughtException = previous; }
                    }
                }
            }

            if (_finallyBody != null)
            {
                try
                {
                    if (IsInfrastructure(primary))
                    {
                        using (context.Budget.EnterFinallyCleanup()) _finallyBody.Execute(context.CreateChild());
                    }
                    else _finallyBody.Execute(context.CreateChild());
                }
                catch (Exception cleanupError)
                {
                    if (IsInfrastructure(primary))
                    {
                        var contract = (ExecutionContractException)primary;
                        contract.Detail["cleanupDiagnostics"] = new[] { cleanupError.GetType().FullName + ": " + cleanupError.Message };
                    }
                    else primary = cleanupError;
                }
            }
            if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
            return result;
        }

        private static bool IsInfrastructure(Exception error)
        { return error is ExecutionContractException; }
    }

    internal sealed class ThrowStmt : Stmt
    {
        private readonly Expr _expression;
        public ThrowStmt(Expr expression) { _expression = expression; }
        public override object Execute(CSharpEvaluationContext context)
        {
            Enter(context);
            if (_expression == null)
            {
                if (context.CurrentCaughtException == null)
                    throw new ExecutionContractException("CSHARP_BIND_ERROR", "throw; is only valid inside a catch clause.");
                ExceptionDispatchInfo.Capture(context.CurrentCaughtException).Throw();
                return null;
            }
            object value = _expression.Evaluate(context);
            if (!(value is Exception error))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "A throw expression must evaluate to System.Exception.");
            throw error;
        }
    }

    internal sealed class LiteralExpr : Expr
    {
        private readonly object _value;
        internal object Value => _value;
        public LiteralExpr(object value) { _value = value; }
        public override object Evaluate(CSharpEvaluationContext context) { Enter(context); return _value; }
    }

    internal sealed class NameExpr : Expr
    {
        public string Name { get; }
        public NameExpr(string name) { Name = name; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            if (context.TryGetVariable(Name, out var value)) return value;
            var type = context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.ResolveExpressionRoot(Name, context.Imports));
            if (type != null) { context.Policy.EnsureTypeAllowed(type); return new StaticTypeTarget(type); }
            return new UnresolvedName(Name);
        }
        internal override AssignmentReference ResolveAssignmentReference(CSharpEvaluationContext context)
        {
            if (!context.TryGetVariable(Name, out _))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unknown variable: " + Name);
            return new AssignmentReference(() => context.GetVariable(Name), value => context.SetVariable(Name, value));
        }
    }

    // A generic type path is syntactic only until a following member/call requires
    // resolution.  It must not turn a closed CLR Type into a static target early.
    internal sealed class GenericTypePathExpr : Expr
    {
        private readonly Expr _target;
        private readonly IReadOnlyList<string> _arguments;
        public GenericTypePathExpr(Expr target, IReadOnlyList<string> arguments)
        {
            _target = target;
            _arguments = arguments ?? Array.Empty<string>();
        }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object target = _target.Evaluate(context);
            if (!(target is UnresolvedName unresolved))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Generic type arguments require an unresolved type path.");
            return new UnresolvedName(unresolved.Path + "<" + string.Join(",", _arguments) + ">");
        }
    }

    internal sealed class MemberExpr : Expr
    {
        public Expr Target { get; }
        public string Name { get; }
        public bool IsConditional { get; }
        public MemberExpr(Expr target, string name, bool isConditional = false)
        {
            Target = target;
            Name = name;
            IsConditional = isConditional;
        }

        internal object EvaluateTarget(CSharpEvaluationContext context)
        {
            object target = Target.Evaluate(context);
            if (ReferenceEquals(target, ConditionalAccessNull.Instance)) return target;
            if (target == null && IsConditional) return ConditionalAccessNull.Instance;
            return target;
        }

        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object target = EvaluateTarget(context);
            if (ReferenceEquals(target, ConditionalAccessNull.Instance)) return target;
            Enter(context);
            if (target is UnresolvedName unresolved)
            {
                string path = unresolved.Path + "." + Name;
                var type = context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.Resolve(path, context.Imports));
                if (type != null) { context.Policy.EnsureTypeAllowed(type); return new StaticTypeTarget(type); }
                // Closed generic paths cannot have their following static member
                // resolved as part of a CLR type name. Resolve the closed path at
                // this member boundary, then use the ordinary static member flow.
                if (unresolved.Path.IndexOf("<", StringComparison.Ordinal) >= 0)
                {
                    type = context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.Resolve(unresolved.Path, context.Imports));
                    if (type != null)
                    {
                        context.Policy.EnsureTypeAllowed(type);
                        target = new StaticTypeTarget(type);
                    }
                    else return new UnresolvedName(path);
                }
                else return new UnresolvedName(path);
            }
            bool isStatic = target is StaticTypeTarget;
            Type typeTarget = isStatic ? ((StaticTypeTarget)target).Type : target?.GetType();
            if (typeTarget == null) throw new NullReferenceException("Cannot read member " + Name + " from null.");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
            var property = typeTarget.GetProperty(Name, flags);
            if (property != null)
            {
                context.Policy.EnsureMemberAllowed(property);
                context.Diagnostics.CountGetter();
                return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => property.GetValue(isStatic ? null : target, null)));
            }
            var field = typeTarget.GetField(Name, flags);
            if (field != null)
            {
                context.Policy.EnsureMemberAllowed(field);
                return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => field.GetValue(isStatic ? null : target)));
            }
            if (isStatic)
            {
                var nestedType = typeTarget.GetNestedType(Name, BindingFlags.Public | BindingFlags.NonPublic);
                if (nestedType != null)
                {
                    context.Policy.EnsureTypeAllowed(nestedType);
                    return new StaticTypeTarget(nestedType);
                }
            }
            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Member was not found: " + typeTarget.FullName + "." + Name);
        }
        internal override AssignmentReference ResolveAssignmentReference(CSharpEvaluationContext context)
        {
            if (IsConditional)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Conditional member access is not assignable.");
            Enter(context);
            object target = EvaluateTarget(context);
            Enter(context);
            bool isStatic = target is StaticTypeTarget;
            Type typeTarget = isStatic ? ((StaticTypeTarget)target).Type : target?.GetType();
            if (typeTarget == null) throw new NullReferenceException("Cannot assign member " + Name + " on null.");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
            var eventInfo = typeTarget.GetEvent(Name, flags);
            if (eventInfo != null)
                return new AssignmentReference(null, null, isStatic ? null : target, eventInfo);
            var property = typeTarget.GetProperty(Name, flags);
            if (property != null)
            {
                return new AssignmentReference(
                    () =>
                    {
                        context.Policy.EnsureMemberAllowed(property);
                        context.Diagnostics.CountGetter();
                        return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => property.GetValue(isStatic ? null : target, null)));
                    },
                    value =>
                    {
                        context.Policy.EnsureMemberAllowed(property);
                        context.Invoke(() => { property.SetValue(isStatic ? null : target, RuntimeConvert.ChangeType(value, property.PropertyType), null); return null; });
                        context.SideEffectsMayHaveOccurred = true;
                    });
            }
            var field = typeTarget.GetField(Name, flags);
            if (field != null)
            {
                return new AssignmentReference(
                    () =>
                    {
                        context.Policy.EnsureMemberAllowed(field);
                        return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => field.GetValue(isStatic ? null : target)));
                    },
                    value =>
                    {
                        context.Policy.EnsureMemberAllowed(field);
                        context.Invoke(() => { field.SetValue(isStatic ? null : target, RuntimeConvert.ChangeType(value, field.FieldType)); return null; });
                        context.SideEffectsMayHaveOccurred = true;
                    });
            }
            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Assignable member was not found: " + typeTarget.FullName + "." + Name);
        }
    }

    internal sealed class IndexExpr : Expr
    {
        private readonly Expr _target;
        private readonly IReadOnlyList<Expr> _indices;
        internal Expr Target => _target;
        internal IReadOnlyList<Expr> Indices => _indices;
        public IndexExpr(Expr target, IReadOnlyList<Expr> indices) { _target = target; _indices = indices ?? Array.Empty<Expr>(); }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object target = _target.Evaluate(context);
            if (ReferenceEquals(target, ConditionalAccessNull.Instance)) return target;
            object[] indexValues = _indices.Select(index => index.Evaluate(context)).ToArray();
            Enter(context);
            if (target is Array array) return context.Invoke(() => array.GetValue(indexValues.Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToArray()));
            if (indexValues.Length != 1) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Only CLR arrays support multiple indices.");
            object index = indexValues[0];
            if (target is IList list) return context.Invoke(() => list[Convert.ToInt32(index, CultureInfo.InvariantCulture)]);
            if (target is IDictionary dictionary) return context.Invoke(() => dictionary[index]);
            var property = target?.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Target has no supported indexer.");
            return context.Invoke(() => property.GetValue(target, new[] { RuntimeConvert.ChangeType(index, property.GetIndexParameters()[0].ParameterType) }));
        }
        internal override AssignmentReference ResolveAssignmentReference(CSharpEvaluationContext context)
        {
            Enter(context);
            object target = _target.Evaluate(context);
            if (ReferenceEquals(target, ConditionalAccessNull.Instance))
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Conditional access is not assignable.");
            object[] indexValues = _indices.Select(index => index.Evaluate(context)).ToArray();
            Enter(context);
            if (target is Array array)
            {
                int[] indices = indexValues.Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray();
                return new AssignmentReference(
                    () => context.Invoke(() => array.GetValue(indices)),
                    value =>
                    {
                        context.Invoke(() => { array.SetValue(RuntimeConvert.ChangeType(value, array.GetType().GetElementType()), indices); return null; });
                        context.SideEffectsMayHaveOccurred = true;
                    });
            }
            if (indexValues.Length != 1) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Only CLR arrays support multiple indices.");
            object index = indexValues[0];
            if (target is IList list)
            {
                int listIndex = Convert.ToInt32(index, CultureInfo.InvariantCulture);
                return new AssignmentReference(
                    () => context.Invoke(() => list[listIndex]),
                    value => { context.Invoke(() => { list[listIndex] = value; return null; }); context.SideEffectsMayHaveOccurred = true; });
            }
            if (target is IDictionary dictionary)
                return new AssignmentReference(
                    () => context.Invoke(() => dictionary[index]),
                    value => { context.Invoke(() => { dictionary[index] = value; return null; }); context.SideEffectsMayHaveOccurred = true; });
            var property = target?.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Target has no supported indexer.");
            object convertedIndex = RuntimeConvert.ChangeType(index, property.GetIndexParameters()[0].ParameterType);
            return new AssignmentReference(
                () => context.Invoke(() => property.GetValue(target, new[] { convertedIndex })),
                value =>
                {
                    context.Invoke(() => { property.SetValue(target, RuntimeConvert.ChangeType(value, property.PropertyType), new[] { convertedIndex }); return null; });
                    context.SideEffectsMayHaveOccurred = true;
                });
        }
    }

    internal sealed class AssignExpr : Expr
    {
        private readonly Expr _target;
        private readonly string _operator;
        private readonly Expr _value;
        internal Expr Target => _target;
        internal string Operator => _operator;
        internal Expr Value => _value;
        public AssignExpr(Expr target, string op, Expr value) { _target = target; _operator = op; _value = value; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            AssignmentReference reference = _target.ResolveAssignmentReference(context);
            if (_operator == "??=")
            {
                if (reference.EventInfo != null)
                    throw new ExecutionContractException("CSHARP_BIND_ERROR", "Events do not support ??= assignment.");
                object current = reference.Get();
                if (current != null && !ReferenceEquals(current, ConditionalAccessNull.Instance)) return current;
                object fallback = _value.Evaluate(context);
                reference.Set(fallback);
                return fallback;
            }
            object previous = _operator == "=" || reference.EventInfo != null ? null : reference.Get();
            object value = _value.Evaluate(context);
            if ((_operator == "+=" || _operator == "-=") && reference.EventInfo != null)
            {
                if (context.SessionLifetime == null || !context.SessionLifetime.IsPersistent)
                    throw new ExecutionContractException("SESSION_REQUIRED", "Event subscriptions require a persistent execution session.");
                Delegate handler;
                try { handler = (Delegate)RuntimeConvert.ChangeType(value, reference.EventInfo.EventHandlerType); }
                catch (Exception ex) { throw new ExecutionContractException("CSHARP_BIND_ERROR", "Event handler is incompatible with " + reference.EventInfo.Name + ": " + ex.Message); }
                context.Policy.EnsureMemberAllowed(reference.EventInfo);
                if (_operator == "+=") context.SessionLifetime.AddEventSubscription(reference.EventTarget, reference.EventInfo, handler);
                else context.SessionLifetime.RemoveEventSubscription(reference.EventTarget, reference.EventInfo, handler);
                context.SideEffectsMayHaveOccurred = true;
                return handler;
            }
            if (reference.EventInfo != null)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Events support only += and -= assignment.");
            if (_operator != "=") value = BinaryExpr.Apply(_operator.Substring(0, 1), previous, value, context);
            reference.Set(value);
            return value;
        }
    }

    internal sealed class IncrementExpr : Expr
    {
        private readonly Expr _target;
        private readonly int _delta;
        private readonly bool _postfix;
        internal Expr Target => _target;
        internal int Delta => _delta;
        internal bool IsPostfix => _postfix;
        public IncrementExpr(Expr target, int delta, bool postfix) { _target = target; _delta = delta; _postfix = postfix; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            AssignmentReference reference = _target.ResolveAssignmentReference(context);
            object previous = reference.Get();
            object next = BinaryExpr.Apply("+", previous, _delta, context);
            reference.Set(next);
            return _postfix ? previous : next;
        }
    }

    internal sealed class UnaryExpr : Expr
    {
        private readonly string _operator;
        private readonly Expr _value;
        internal string Operator => _operator;
        internal Expr Value => _value;
        public UnaryExpr(string op, Expr value) { _operator = op; _value = value; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object value = _value.Evaluate(context);
            if (_operator == "!") return !RuntimeConvert.ToBool(value);
            if (_operator == "+") return value;
            if (_operator == "-") return BinaryExpr.ApplyUnaryMinus(value, context);
            if (_operator == "~") return ~Convert.ToInt64(value, CultureInfo.InvariantCulture);
            throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "Unsupported unary operator: " + _operator);
        }
    }

    internal sealed class BinaryExpr : Expr
    {
        private readonly Expr _left;
        private readonly string _operator;
        private readonly Expr _right;
        internal Expr Left => _left;
        internal string Operator => _operator;
        internal Expr Right => _right;
        public BinaryExpr(Expr left, string op, Expr right) { _left = left; _operator = op; _right = right; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object left = _left.Evaluate(context);
            if (_operator == "&&" && !RuntimeConvert.ToBool(left)) return false;
            if (_operator == "||" && RuntimeConvert.ToBool(left)) return true;
            return Apply(_operator, left, _right.Evaluate(context), context);
        }

        public static object Apply(string op, object left, object right)
        {
            return Apply(op, left, right, null);
        }

        public static object Apply(string op, object left, object right, CSharpEvaluationContext context)
        {
            if (ReferenceEquals(left, ConditionalAccessNull.Instance)) left = null;
            if (ReferenceEquals(right, ConditionalAccessNull.Instance)) right = null;
            if (op == "+" && (left is string || right is string)) return Convert.ToString(left, CultureInfo.InvariantCulture) + Convert.ToString(right, CultureInfo.InvariantCulture);
            if (op == "==") return EqualsNormalized(left, right);
            if (op == "!=") return !EqualsNormalized(left, right);
            if (op == "&&") return RuntimeConvert.ToBool(left) && RuntimeConvert.ToBool(right);
            if (op == "||") return RuntimeConvert.ToBool(left) || RuntimeConvert.ToBool(right);
            if (op == "&" && left is bool && right is bool) return (bool)left & (bool)right;
            if (op == "|" && left is bool && right is bool) return (bool)left | (bool)right;
            if (op == "<") return Compare(left, right) < 0;
            if (op == "<=") return Compare(left, right) <= 0;
            if (op == ">") return Compare(left, right) > 0;
            if (op == ">=") return Compare(left, right) >= 0;

            if (TryApplyUnityVectorOperator(op, left, right, context, out var vectorResult))
                return vectorResult;

            bool floating = RuntimeConvert.IsFloating(left) || RuntimeConvert.IsFloating(right);
            if (floating)
            {
                double a = RuntimeConvert.ToDouble(left), b = RuntimeConvert.ToDouble(right);
                if (op == "+") return a + b;
                if (op == "-") return a - b;
                if (op == "*") return a * b;
                if (op == "/") return a / b;
                if (op == "%") return a % b;
            }
            long x = Convert.ToInt64(left, CultureInfo.InvariantCulture), y = Convert.ToInt64(right, CultureInfo.InvariantCulture);
            if (op == "+") return PreserveInteger(left, right, x + y);
            if (op == "-") return PreserveInteger(left, right, x - y);
            if (op == "*") return PreserveInteger(left, right, x * y);
            if (op == "/") return PreserveInteger(left, right, x / y);
            if (op == "%") return PreserveInteger(left, right, x % y);
            if (op == "<<") return x << (int)y;
            if (op == ">>") return x >> (int)y;
            if (op == "&") return x & y;
            if (op == "|") return x | y;
            if (op == "^") return x ^ y;
            throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "Unsupported binary operator: " + op);
        }

        internal static object ApplyUnaryMinus(object value, CSharpEvaluationContext context)
        {
            if (!IsUnityVector(value)) return -RuntimeConvert.ToDouble(value);
            var method = value.GetType().GetMethod(
                "op_UnaryNegation",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { value.GetType() },
                null);
            if (method == null)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unity vector does not expose op_UnaryNegation.");
            return InvokeOperator(method, new[] { value }, context);
        }

        private static bool TryApplyUnityVectorOperator(
            string op,
            object left,
            object right,
            CSharpEvaluationContext context,
            out object result)
        {
            result = null;
            bool leftVector = IsUnityVector(left);
            bool rightVector = IsUnityVector(right);
            if (!leftVector && !rightVector) return false;

            Type vectorType = leftVector ? left.GetType() : right.GetType();
            Type[] parameterTypes;
            string methodName;
            if ((op == "+" || op == "-") && leftVector && rightVector && left.GetType() == right.GetType())
            {
                parameterTypes = new[] { vectorType, vectorType };
                methodName = op == "+" ? "op_Addition" : "op_Subtraction";
            }
            else if (op == "*" && leftVector && right is float)
            {
                parameterTypes = new[] { vectorType, typeof(float) };
                methodName = "op_Multiply";
            }
            else if (op == "*" && rightVector && left is float)
            {
                parameterTypes = new[] { typeof(float), vectorType };
                methodName = "op_Multiply";
            }
            else if (op == "/" && leftVector && right is float)
            {
                parameterTypes = new[] { vectorType, typeof(float) };
                methodName = "op_Division";
            }
            else
            {
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unity vector operator requires matching vectors or an exact float scalar.");
            }

            var method = vectorType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
            if (method == null)
                throw new ExecutionContractException("CSHARP_BIND_ERROR", "Unity vector operator was not found: " + methodName);
            result = InvokeOperator(method, new[] { left, right }, context);
            return true;
        }

        private static object InvokeOperator(MethodInfo method, object[] arguments, CSharpEvaluationContext context)
        {
            if (context == null)
            {
                try { return method.Invoke(null, arguments); }
                catch (TargetInvocationException ex) { return CSharpSubsetEngine.RethrowTargetInvocation(ex); }
            }
            context.Policy.EnsureMemberAllowed(method);
            context.Diagnostics.CountMethod();
            try { return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => method.Invoke(null, arguments))); }
            catch (TargetInvocationException ex) { return CSharpSubsetEngine.RethrowTargetInvocation(ex); }
        }

        private static bool IsUnityVector(object value)
        {
            string name = value?.GetType().FullName;
            return name == "UnityEngine.Vector2" || name == "UnityEngine.Vector3" || name == "UnityEngine.Vector4";
        }

        private static object PreserveInteger(object left, object right, long value)
        {
            if (left is int && right is int && value <= int.MaxValue && value >= int.MinValue) return (int)value;
            return value;
        }
        private static bool EqualsNormalized(object left, object right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (RuntimeConvert.IsNumeric(left) && RuntimeConvert.IsNumeric(right)) return RuntimeConvert.ToDouble(left).Equals(RuntimeConvert.ToDouble(right));
            return object.Equals(left, right);
        }
        private static int Compare(object left, object right)
        {
            if (RuntimeConvert.IsNumeric(left) && RuntimeConvert.IsNumeric(right)) return RuntimeConvert.ToDouble(left).CompareTo(RuntimeConvert.ToDouble(right));
            if (left is IComparable comparable) return comparable.CompareTo(RuntimeConvert.ChangeType(right, left.GetType()));
            throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "Values are not comparable.");
        }
    }

    internal sealed class ConditionalExpr : Expr
    {
        private readonly Expr _condition, _whenTrue, _whenFalse;
        internal Expr Condition => _condition;
        internal Expr WhenTrue => _whenTrue;
        internal Expr WhenFalse => _whenFalse;
        public ConditionalExpr(Expr condition, Expr whenTrue, Expr whenFalse) { _condition = condition; _whenTrue = whenTrue; _whenFalse = whenFalse; }
        public override object Evaluate(CSharpEvaluationContext context) { Enter(context); return RuntimeConvert.ToBool(_condition.Evaluate(context)) ? _whenTrue.Evaluate(context) : _whenFalse.Evaluate(context); }
    }

    internal sealed class CoalesceExpr : Expr
    {
        internal Expr Left { get; }
        internal Expr Right { get; }
        public CoalesceExpr(Expr left, Expr right) { Left = left; Right = right; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            object value = Left.Evaluate(context);
            return value == null || ReferenceEquals(value, ConditionalAccessNull.Instance) ? Right.Evaluate(context) : value;
        }
    }

    internal sealed class TypeIntrinsicExpr : Expr
    {
        internal string TypeName { get; }
        internal string Kind { get; }
        public TypeIntrinsicExpr(string typeName, string kind) { TypeName = typeName; Kind = kind; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            Type type = ExecutionTypeResolver.Resolve(TypeName, context.Imports);
            if (type == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Type was not found: " + TypeName);
            context.Policy.EnsureTypeAllowed(type);
            return Kind == "typeof" ? (object)type : (type.IsValueType ? Activator.CreateInstance(type) : null);
        }
    }

    internal sealed class NameofExpr : Expr
    {
        internal string Name { get; }
        public NameofExpr(string name) { Name = name ?? ""; }
        public override object Evaluate(CSharpEvaluationContext context) { Enter(context); return Name; }
    }

    internal sealed class CallExpr : Expr
    {
        private readonly Expr _callee;
        private readonly IReadOnlyList<Expr> _arguments;
        private readonly IReadOnlyList<string> _genericTypeNames;
        internal Expr Callee => _callee;
        internal IReadOnlyList<Expr> Arguments => _arguments;
        internal IReadOnlyList<string> GenericTypeNames => _genericTypeNames;
        public CallExpr(Expr callee, IReadOnlyList<Expr> arguments, IReadOnlyList<string> genericTypeNames = null)
        { _callee = callee; _arguments = arguments; _genericTypeNames = genericTypeNames ?? Array.Empty<string>(); }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            context.Budget.CountCall();
            if (_callee is MemberExpr member)
            {
                object target = member.EvaluateTarget(context);
                if (ReferenceEquals(target, ConditionalAccessNull.Instance)) return target;
                var values = _arguments.Select(arg => new ExecutionValue { Value = arg.Evaluate(context) }).ToList();
                Enter(context);
                if (target is UnresolvedName unresolved)
                {
                    var resolvedType = context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.Resolve(unresolved.Path, context.Imports));
                    if (resolvedType == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Type was not found: " + unresolved.Path);
                    target = new StaticTypeTarget(resolvedType);
                }
                bool isStatic = target is StaticTypeTarget;
                Type type = isStatic ? ((StaticTypeTarget)target).Type : target?.GetType();
                var genericTypes = _genericTypeNames.Select(name => context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.Resolve(name, context.Imports))).ToArray();
                if (genericTypes.Any(typeArgument => typeArgument == null))
                    throw new ExecutionContractException("CSHARP_BIND_ERROR", "One or more explicit generic type arguments could not be resolved.");
                var bound = context.Diagnostics.MeasureBind(() => MethodBinder.Bind(type, member.Name, isStatic, values,
                    genericTypeArguments: genericTypes.Length == 0 ? null : genericTypes,
                    inferGenericTypeArguments: genericTypes.Length == 0,
                    userConversionInvoker: action =>
                    {
                        context.Diagnostics.CountMethod();
                        return context.Diagnostics.MeasureInvoke(() => context.Invoke(action));
                    }));
                context.Policy.EnsureMemberAllowed(bound.Method);
                context.SideEffectsMayHaveOccurred = true;
                context.Diagnostics.CountMethod();
                try { return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => bound.Method.Invoke(isStatic ? null : target, bound.Arguments))); }
                catch (TargetInvocationException ex) { return CSharpSubsetEngine.RethrowTargetInvocation(ex); }
            }
            object callable = _callee.Evaluate(context);
            if (ReferenceEquals(callable, ConditionalAccessNull.Instance)) return callable;
            var callableValues = _arguments.Select(arg => new ExecutionValue { Value = arg.Evaluate(context) }).ToList();
            Enter(context);
            if (callable is LambdaValue lambda)
            {
                object[] arguments = callableValues.Select(value => value.Value).ToArray();
                return lambda.IsAsync ? (object)lambda.InvokeAsync(context, arguments) : lambda.Invoke(context, arguments);
            }
            if (callable is Delegate del)
            {
                context.SideEffectsMayHaveOccurred = true;
                return context.Invoke(() => del.DynamicInvoke(callableValues.Select(value => value.Value).ToArray()));
            }
            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Expression is not callable.");
        }
    }

    internal sealed class NewExpr : Expr
    {
        private readonly string _typeName;
        private readonly IReadOnlyList<Expr> _arguments;
        internal string TypeName => _typeName;
        internal IReadOnlyList<Expr> Arguments => _arguments;
        public NewExpr(string typeName, IReadOnlyList<Expr> arguments) { _typeName = typeName; _arguments = arguments; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            context.Budget.CountAllocation();
            Type type = context.Diagnostics.MeasureResolve(() => ExecutionTypeResolver.Resolve(_typeName, context.Imports));
            if (type == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Constructor type was not found: " + _typeName);
            context.Policy.EnsureConstructionAllowed(type);
            object[] values = _arguments.Select(argument => argument.Evaluate(context)).ToArray();
            try
            {
                context.SideEffectsMayHaveOccurred = true;
                return context.Invoke(() => Activator.CreateInstance(type, values));
            }
            catch (MissingMethodException)
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var parameters = ctor.GetParameters();
                    if (parameters.Length != values.Length) continue;
                    try
                    {
                        var converted = parameters.Select((p, i) => RuntimeConvert.ChangeType(values[i], p.ParameterType)).ToArray();
                        return context.Invoke(() => ctor.Invoke(converted));
                    }
                    catch { }
                }
                throw;
            }
        }
    }

    internal sealed class ArrayInitializerNode
    {
        public Expr Value { get; }
        public IReadOnlyList<ArrayInitializerNode> Children { get; }
        public bool IsLeaf => Value != null;
        public ArrayInitializerNode(Expr value) { Value = value; Children = Array.Empty<ArrayInitializerNode>(); }
        public ArrayInitializerNode(IReadOnlyList<ArrayInitializerNode> children) { Children = children ?? Array.Empty<ArrayInitializerNode>(); }
    }

    internal sealed class ArrayCreationExpr : Expr
    {
        private readonly string _typeName;
        private readonly IReadOnlyList<Expr> _dimensions;
        private readonly ArrayInitializerNode _initializer;
        private readonly bool _implicit;
        private readonly int _rank;
        internal string TypeName => _typeName;
        internal IReadOnlyList<Expr> Dimensions => _dimensions;
        internal ArrayInitializerNode Initializer => _initializer;
        internal bool IsImplicit => _implicit;
        internal int Rank => _rank;

        public ArrayCreationExpr(string typeName, IReadOnlyList<Expr> dimensions, ArrayInitializerNode initializer, bool implicitType, int rank)
        { _typeName = typeName ?? ""; _dimensions = dimensions ?? Array.Empty<Expr>(); _initializer = initializer; _implicit = implicitType; _rank = rank; }

        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            context.Budget.CountAllocation();
            if (_rank < 1 || _rank > 4)
                throw new ExecutionContractException("CSHARP_ARRAY_RANK_UNSUPPORTED", "Array rank must be between 1 and 4.");

            var evaluated = _initializer == null ? null : EvaluateInitializer(_initializer, context);
            Type declared = string.IsNullOrWhiteSpace(_typeName) ? null : ExecutionTypeResolver.Resolve(_typeName, context.Imports);
            Type elementType;
            int declaredRank = _rank;
            if (declared != null && declared.IsArray)
            {
                declaredRank = declared.GetArrayRank();
                elementType = declared.GetElementType();
            }
            else elementType = declared;
            if (_implicit) elementType = InferElementType(FlattenLeaves(evaluated).ToList());
            if (elementType == null)
                throw new ExecutionContractException(_implicit ? "CSHARP_ARRAY_INFERENCE_FAILED" : "CSHARP_BIND_ERROR",
                    _implicit ? "Implicit array element type could not be inferred from an empty or all-null initializer." : "Array element type was not found: " + _typeName);
            context.Policy.EnsureTypeAllowed(elementType);

            int[] lengths;
            if (_dimensions.Count > 0)
            {
                lengths = _dimensions.Select(dimension => ConvertLength(dimension.Evaluate(context))).ToArray();
                if (lengths.Length != _rank) throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array dimension count does not match its rank.");
                if (evaluated != null)
                {
                    int[] initializerShape = ShapeOf(evaluated, _rank);
                    if (!lengths.SequenceEqual(initializerShape))
                        throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array initializer shape does not match the declared dimensions.");
                }
            }
            else
            {
                lengths = ShapeOf(evaluated, declaredRank);
            }
            long total = 1;
            foreach (int length in lengths)
            {
                if (length < 0) throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "Array length cannot be negative.");
                try { checked { total *= length; } }
                catch (OverflowException) { throw new ExecutionContractException("EXECUTION_BUDGET_EXCEEDED", "Array dimension product overflowed."); }
            }
            context.Budget.CountArrayElements(total);
            var array = Array.CreateInstance(elementType, lengths);
            if (evaluated != null) Fill(array, evaluated, elementType, new int[lengths.Length], 0);
            return array;
        }

        private static object EvaluateInitializer(ArrayInitializerNode node, CSharpEvaluationContext context)
        {
            if (node.IsLeaf) return node.Value.Evaluate(context);
            return node.Children.Select(child => EvaluateInitializer(child, context)).ToList();
        }

        private static IEnumerable<object> FlattenLeaves(object value)
        {
            if (value is List<object> list) foreach (var item in list) foreach (var leaf in FlattenLeaves(item)) yield return leaf;
            else yield return value;
        }

        private static int ConvertLength(object value)
        {
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) { throw new ExecutionContractException("CSHARP_RUNTIME_ERROR", "Array length must be an Int32: " + ex.Message); }
        }

        private static int[] ShapeOf(object evaluated, int rank)
        {
            if (!(evaluated is List<object> root)) throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "An array initializer is required.");
            var lengths = new int[rank];
            ValidateShape(root, 0, rank, lengths);
            return lengths;
        }

        private static void ValidateShape(List<object> list, int depth, int rank, int[] lengths)
        {
            if (depth >= rank) throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array initializer nesting exceeds the declared rank.");
            if (lengths[depth] == 0) lengths[depth] = list.Count;
            else if (lengths[depth] != list.Count) throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array initializer must be rectangular.");
            foreach (object item in list)
            {
                if (depth + 1 == rank)
                {
                    if (item is List<object>) throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array initializer nesting does not match the declared rank.");
                }
                else if (item is List<object> child) ValidateShape(child, depth + 1, rank, lengths);
                else throw new ExecutionContractException("CSHARP_ARRAY_SHAPE_MISMATCH", "Array initializer must be rectangular.");
            }
        }

        private static void Fill(Array array, object value, Type elementType, int[] indices, int depth)
        {
            var list = value as List<object>;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                indices[depth] = i;
                if (depth + 1 == indices.Length) array.SetValue(RuntimeConvert.ChangeType(list[i], elementType), indices);
                else Fill(array, list[i], elementType, indices, depth + 1);
            }
        }

        private static Type InferElementType(IReadOnlyList<object> values)
        {
            var types = values.Where(value => value != null).Select(value => value.GetType()).Distinct().ToList();
            if (types.Count == 0) return null;
            if (types.All(type => IsNumericType(type))) return PromoteNumeric(types);
            Type candidate = types[0];
            foreach (Type type in types.Skip(1))
            {
                if (candidate.IsAssignableFrom(type)) continue;
                if (type.IsAssignableFrom(candidate)) { candidate = type; continue; }
                candidate = CommonBase(candidate, type);
                if (candidate == null || candidate == typeof(object)) break;
            }
            return candidate ?? typeof(object);
        }

        private static bool IsNumericType(Type type)
        { return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal); }
        private static Type PromoteNumeric(IEnumerable<Type> types)
        {
            var set = new HashSet<Type>(types);
            if (set.Contains(typeof(decimal))) return typeof(decimal);
            if (set.Contains(typeof(double))) return typeof(double);
            if (set.Contains(typeof(float))) return typeof(float);
            if (set.Contains(typeof(ulong))) return typeof(ulong);
            if (set.Contains(typeof(long)) || set.Contains(typeof(uint))) return typeof(long);
            return typeof(int);
        }
        private static Type CommonBase(Type left, Type right)
        {
            for (Type candidate = left; candidate != null; candidate = candidate.BaseType)
                if (candidate.IsAssignableFrom(right)) return candidate;
            return typeof(object);
        }
    }

    internal sealed class CastExpr : Expr
    {
        private readonly string _typeName;
        private readonly Expr _value;
        private readonly string _kind;
        internal string TypeName => _typeName;
        internal Expr Value => _value;
        internal string Kind => _kind;
        public CastExpr(string typeName, Expr value, string kind) { _typeName = typeName; _value = value; _kind = kind; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            Type type = ExecutionTypeResolver.Resolve(_typeName, context.Imports);
            if (type == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Cast type was not found: " + _typeName);
            object value = _value.Evaluate(context);
            if (_kind == "is") return value != null && type.IsInstanceOfType(value);
            if (_kind == "as") return value != null && type.IsInstanceOfType(value) ? value : null;
            return RuntimeConvert.ChangeType(value, type);
        }
    }

    internal sealed class AwaitExpr : Expr
    {
        private readonly Expr _value;
        public AwaitExpr(Expr value) { _value = value; }
        public override object Evaluate(CSharpEvaluationContext context)
        {
            Enter(context);
            context.Budget.CountAwait();
            context.CheckCancellation();
            var result = AwaitableAdapter.AwaitAsync(_value.Evaluate(context), "always", 30000, context.CancellationToken).GetAwaiter().GetResult().Value;
            context.CheckCancellation();
            return result;
        }
    }

    internal sealed class LambdaExpr : Expr
    {
        private readonly LambdaParameter[] _parameters;
        private readonly Expr _expressionBody;
        private readonly Stmt _blockBody;
        private readonly bool _isAsync;
        public LambdaExpr(LambdaParameter[] parameters, Expr expressionBody, Stmt blockBody, bool isAsync)
        { _parameters = parameters; _expressionBody = expressionBody; _blockBody = blockBody; _isAsync = isAsync; }
        internal bool IsAsync => _isAsync;
        public override object Evaluate(CSharpEvaluationContext context)
        { Enter(context); return new LambdaValue(_parameters, _expressionBody, _blockBody, _isAsync, context); }
    }

    internal sealed class UnresolvedName
    {
        public string Path { get; }
        public UnresolvedName(string path) { Path = path; }
        public override string ToString() { return Path; }
    }

    // This marker represents a syntactic static type path only while binding members.
    // Values stored in scopes and returned to callers are always real System.Type instances.
    internal sealed class StaticTypeTarget
    {
        public Type Type { get; }
        public StaticTypeTarget(Type type)
        {
            if (type == null) throw new ExecutionContractException("CSHARP_BIND_ERROR", "Static target type was not found.");
            Type = type;
        }
        public static object Unwrap(object value) => value is StaticTypeTarget target ? target.Type : value;
    }

    internal sealed class ReturnSignal : Exception { public object Value { get; } public ReturnSignal(object value) { Value = value; } }
    internal sealed class BreakSignal : Exception { }
    internal sealed class ContinueSignal : Exception { }

    internal static class RuntimeConvert
    {
        public static bool ToBool(object value)
        {
            if (value is bool boolean) return boolean;
            if (value == null) return false;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        public static double ToDouble(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        public static bool IsFloating(object value) { return value is float || value is double || value is decimal; }
        public static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint ||
                   value is long || value is ulong || value is float || value is double || value is decimal;
        }
        public static object ChangeType(object value, Type type)
        {
            if (type == null) return value;
            if (value == null)
            {
                if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) return null;
                return Activator.CreateInstance(type);
            }
            if (type.IsInstanceOfType(value)) return value;
            if (value is LambdaValue lambda && typeof(Delegate).IsAssignableFrom(type)) return lambda.ToDelegate(type);
            Type nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) type = nullable;
            if (type.IsEnum) return value is string text ? Enum.Parse(type, text, true) : Enum.ToObject(type, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
    }

    internal enum TokenKind { Identifier, Number, String, Operator, Symbol, End }

    internal sealed class Token
    {
        public TokenKind Kind;
        public string Text;
        public object Value;
        public int Position;
        public int Length;
    }

    internal sealed class SourceMap
    {
        private readonly string _source;
        private readonly int[] _lineStarts;
        public SourceMap(string source)
        {
            _source = source ?? "";
            var starts = new List<int> { 0 };
            for (int i = 0; i < _source.Length; i++)
                if (_source[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }

        public ExecutionSourceSpan Resolve(int start, int length)
        {
            start = Math.Max(0, Math.Min(_source.Length, start));
            int end = Math.Max(start, Math.Min(_source.Length, start + Math.Max(0, length)));
            Locate(start, out int line, out int column);
            Locate(end, out int endLine, out int endColumn);
            return new ExecutionSourceSpan
            {
                start = start, length = end - start, end = end,
                line = line, column = column, endLine = endLine, endColumn = endColumn,
            };
        }

        private void Locate(int offset, out int line, out int column)
        {
            int index = Array.BinarySearch(_lineStarts, offset);
            if (index < 0) index = ~index - 1;
            index = Math.Max(0, index);
            line = index + 1;
            column = offset - _lineStarts[index] + 1;
        }
    }

    internal sealed class Tokenizer
    {
        private const int MaxTokens = 16384;
        private readonly string _source;
        private readonly SourceMap _sourceMap;
        private int _index;
        public Tokenizer(string source) { _source = source ?? ""; _sourceMap = new SourceMap(_source); }
        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_index >= _source.Length) break;
                if (tokens.Count >= MaxTokens)
                    throw Error("Token limit exceeded (" + MaxTokens.ToString(CultureInfo.InvariantCulture) + ").", _index);
                char c = _source[_index];
                if (char.IsLetter(c) || c == '_') { tokens.Add(ReadIdentifier()); continue; }
                if (char.IsDigit(c)) { tokens.Add(ReadNumber()); continue; }
                if (c == '\"' || c == '\'') { tokens.Add(ReadString()); continue; }
                string three = _index + 2 < _source.Length ? _source.Substring(_index, 3) : "";
                string two = _index + 1 < _source.Length ? _source.Substring(_index, 2) : "";
                if (three == "<<=" || three == ">>=" || three == "??=") { tokens.Add(Make(TokenKind.Operator, three, 3)); continue; }
                if (new[] { "=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "<<", ">>", "?.", "??" }.Contains(two))
                { tokens.Add(Make(TokenKind.Operator, two, 2)); continue; }
                if ("+-*/%><!~&|^=".IndexOf(c) >= 0) { tokens.Add(Make(TokenKind.Operator, c.ToString(), 1)); continue; }
                if (".,;()[]{}?:".IndexOf(c) >= 0) { tokens.Add(Make(TokenKind.Symbol, c.ToString(), 1)); continue; }
                throw Error("Unexpected character '" + c + "'.", _index);
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = "(eof)", Position = _source.Length, Length = 0 });
            return tokens;
        }
        private void SkipWhitespaceAndComments()
        {
            while (_index < _source.Length)
            {
                if (char.IsWhiteSpace(_source[_index])) { _index++; continue; }
                if (_index + 1 < _source.Length && _source[_index] == '/' && _source[_index + 1] == '/')
                { _index += 2; while (_index < _source.Length && _source[_index] != '\n') _index++; continue; }
                if (_index + 1 < _source.Length && _source[_index] == '/' && _source[_index + 1] == '*')
                {
                    int end = _source.IndexOf("*/", _index + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("Unterminated block comment.", _index);
                    _index = end + 2; continue;
                }
                break;
            }
        }
        private Token ReadIdentifier()
        {
            int start = _index++;
            while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_')) _index++;
            return new Token { Kind = TokenKind.Identifier, Text = _source.Substring(start, _index - start), Position = start, Length = _index - start };
        }
        private Token ReadNumber()
        {
            int start = _index;
            int numberBase = 10;
            bool floating = false;
            if (_index + 1 < _source.Length && _source[_index] == '0' && (_source[_index + 1] == 'x' || _source[_index + 1] == 'X'))
            {
                numberBase = 16;
                _index += 2;
                ReadDigits(start, value => Uri.IsHexDigit(value));
            }
            else if (_index + 1 < _source.Length && _source[_index] == '0' && (_source[_index + 1] == 'b' || _source[_index + 1] == 'B'))
            {
                numberBase = 2;
                _index += 2;
                ReadDigits(start, value => value == '0' || value == '1');
            }
            else
            {
                ReadDigits(start, char.IsDigit);
                if (_index < _source.Length && _source[_index] == '.')
                {
                    floating = true;
                    _index++;
                    if (_index < _source.Length && (_source[_index] == '_' || char.IsDigit(_source[_index])))
                        ReadDigits(start, char.IsDigit);
                }
                if (_index < _source.Length && (_source[_index] == 'e' || _source[_index] == 'E'))
                {
                    floating = true;
                    _index++;
                    if (_index < _source.Length && (_source[_index] == '+' || _source[_index] == '-')) _index++;
                    ReadDigits(start, char.IsDigit);
                }
            }
            int suffixStart = _index;
            while (_index < _source.Length && char.IsLetter(_source[_index])) _index++;
            string text = _source.Substring(start, _index - start);
            string raw = _source.Substring(start, suffixStart - start).Replace("_", "");
            string suffix = _source.Substring(suffixStart, _index - suffixStart).ToLowerInvariant();
            object value;
            try
            {
                if (numberBase != 10)
                {
                    if (suffix != "" && suffix != "u" && suffix != "l" && suffix != "ul" && suffix != "lu")
                        throw new FormatException("Invalid integer suffix.");
                    string digits = raw.Substring(2);
                    ulong integer = Convert.ToUInt64(digits, numberBase);
                    value = SelectIntegerLiteral(integer, suffix);
                }
                else if (floating || suffix == "f" || suffix == "d" || suffix == "m")
                {
                    if (suffix != "" && suffix != "f" && suffix != "d" && suffix != "m")
                        throw new FormatException("Invalid real suffix.");
                    if (suffix == "m") value = decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                    else if (suffix == "f") value = float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                    else value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                else
                {
                    if (suffix != "" && suffix != "u" && suffix != "l" && suffix != "ul" && suffix != "lu")
                        throw new FormatException("Invalid integer suffix.");
                    ulong integer = ulong.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    value = SelectIntegerLiteral(integer, suffix);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            {
                throw Error("Invalid numeric literal '" + text + "': " + ex.Message, start, Math.Max(1, _index - start));
            }
            return new Token { Kind = TokenKind.Number, Text = text, Value = value, Position = start, Length = _index - start };
        }

        private void ReadDigits(int literalStart, Func<char, bool> isDigit)
        {
            bool sawDigit = false;
            bool lastWasSeparator = false;
            while (_index < _source.Length && (isDigit(_source[_index]) || _source[_index] == '_'))
            {
                if (_source[_index] == '_')
                {
                    if (!sawDigit) throw Error("A digit separator must follow a digit.", _index);
                    lastWasSeparator = true;
                }
                else
                {
                    sawDigit = true;
                    lastWasSeparator = false;
                }
                _index++;
            }
            if (!sawDigit) throw Error("Numeric literal requires at least one digit.", literalStart, Math.Max(1, _index - literalStart));
            if (lastWasSeparator) throw Error("A numeric literal cannot end with a digit separator.", _index - 1);
        }

        private static object SelectIntegerLiteral(ulong value, string suffix)
        {
            if (suffix == "u") return value <= uint.MaxValue ? (object)(uint)value : value;
            if (suffix == "l")
            {
                if (value > long.MaxValue) throw new OverflowException("The value is too large for Int64.");
                return (long)value;
            }
            if (suffix == "ul" || suffix == "lu") return value;
            if (value <= int.MaxValue) return (int)value;
            if (value <= uint.MaxValue) return (uint)value;
            if (value <= long.MaxValue) return (long)value;
            return value;
        }

        private Token ReadString()
        {
            int start = _index;
            char quote = _source[_index++];
            var chars = new List<char>();
            while (_index < _source.Length)
            {
                char c = _source[_index++];
                if (c == quote)
                {
                    if (quote == '\'' && chars.Count != 1)
                        throw Error("A character literal must contain exactly one character.", start, _index - start);
                    return new Token { Kind = TokenKind.String, Text = _source.Substring(start, _index - start), Value = quote == '\'' ? (object)chars[0] : new string(chars.ToArray()), Position = start, Length = _index - start };
                }
                if (c == '\r' || c == '\n') throw Error("Newline in literal.", _index - 1);
                if (c == '\\') ReadEscape(chars, start);
                else chars.Add(c);
            }
            throw Error(quote == '\'' ? "Unterminated character literal." : "Unterminated string literal.", start, Math.Max(1, _index - start));
        }

        private void ReadEscape(ICollection<char> chars, int literalStart)
        {
            if (_index >= _source.Length) throw Error("Unterminated escape sequence.", literalStart, Math.Max(1, _index - literalStart));
            char escaped = _source[_index++];
            switch (escaped)
            {
                case '\'': chars.Add('\''); return;
                case '"': chars.Add('"'); return;
                case '\\': chars.Add('\\'); return;
                case '0': chars.Add('\0'); return;
                case 'a': chars.Add('\a'); return;
                case 'b': chars.Add('\b'); return;
                case 'f': chars.Add('\f'); return;
                case 'n': chars.Add('\n'); return;
                case 'r': chars.Add('\r'); return;
                case 't': chars.Add('\t'); return;
                case 'v': chars.Add('\v'); return;
                case 'u': AppendUnicodeEscape(chars, 4, 4); return;
                case 'U': AppendUnicodeEscape(chars, 8, 8); return;
                case 'x': AppendUnicodeEscape(chars, 1, 4); return;
                default: throw Error("Unrecognized escape sequence '\\" + escaped + "'.", _index - 2, 2);
            }
        }

        private void AppendUnicodeEscape(ICollection<char> chars, int minDigits, int maxDigits)
        {
            int start = _index;
            int count = 0;
            while (count < maxDigits && _index < _source.Length && Uri.IsHexDigit(_source[_index])) { _index++; count++; }
            if (count < minDigits) throw Error("Invalid Unicode escape sequence.", start, Math.Max(1, count));
            int codePoint;
            try { codePoint = int.Parse(_source.Substring(start, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            { throw Error("Invalid Unicode escape sequence.", start, Math.Max(1, count)); }
            if (codePoint > 0x10ffff || codePoint >= 0xd800 && codePoint <= 0xdfff)
                throw Error("Unicode escape is outside the valid scalar range.", start, count);
            foreach (char value in char.ConvertFromUtf32(codePoint)) chars.Add(value);
        }
        private Token Make(TokenKind kind, string text, int length) { var token = new Token { Kind = kind, Text = text, Position = _index, Length = length }; _index += length; return token; }
        private ExecutionContractException Error(string message, int position, int length = 1)
        {
            return new ExecutionContractException("CSHARP_PARSE_ERROR", message, new Dictionary<string, object>
            {
                { "position", position }, { "sourceSpan", _sourceMap.Resolve(position, length) }, { "stage", "parse" },
            });
        }
    }

    internal sealed class Parser
    {
        private const int MaxParseDepth = 256;
        private readonly List<Token> _tokens;
        private readonly string _mode;
        private readonly string _source;
        private readonly SourceMap _sourceMap;
        private int _position;
        private int _finallyDepth;
        private int _parseDepth;
        public Parser(string code, string mode)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ExecutionContractException("CSHARP_PARSE_ERROR", "code is required.");
            _source = code;
            _sourceMap = new SourceMap(code);
            _tokens = new Tokenizer(code).Tokenize();
            _mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
            if (_mode != "auto" && _mode != "expression" && _mode != "statements") throw new ExecutionContractException("CSHARP_PARSE_ERROR", "mode must be auto, expression, or statements.");
        }

        public CSharpProgram ParseProgram()
        {
            if (_mode == "expression")
            {
                Expr expression = ParseExpression();
                Match(";"); ExpectEnd();
                return new CSharpProgram(WithSpan(new BlockStmt(new[] { WithSpan(new ExpressionStmt(expression), expression.Span?.start ?? 0) }), 0), true, _source);
            }
            if (_mode == "auto")
            {
                int save = _position;
                try
                {
                    Expr expression = ParseExpression();
                    Match(";");
                    if (Peek().Kind == TokenKind.End) return new CSharpProgram(WithSpan(new BlockStmt(new[] { WithSpan(new ExpressionStmt(expression), expression.Span?.start ?? 0) }), 0), true, _source);
                }
                catch { }
                _position = save;
            }
            var statements = new List<Stmt>();
            while (Peek().Kind != TokenKind.End) statements.Add(ParseStatement());
            return new CSharpProgram(WithSpan(new BlockStmt(statements), 0), false, _source);
        }

        private Stmt ParseStatement()
        {
            int statementStart = Peek().Position;
            if (Match("{"))
            {
                var statements = new List<Stmt>();
                while (!Match("}")) { if (Peek().Kind == TokenKind.End) throw Error("Expected '}'."); statements.Add(ParseStatement()); }
                return WithSpan(new BlockStmt(statements, true), statementStart);
            }
            if (Match("try"))
            {
                Stmt tryBody = ParseStatement();
                var catches = new List<CatchClause>();
                while (Match("catch"))
                {
                    string typeName = "", variableName = "";
                    if (Match("("))
                    {
                        typeName = ParseTypeName();
                        if (Peek().Kind == TokenKind.Identifier) variableName = Next().Text;
                        Expect(")");
                    }
                    if (Peek().Text == "when") throw Unsupported("Catch filters are not supported.");
                    catches.Add(new CatchClause(typeName, variableName, ParseStatement()));
                }
                Stmt finallyBody = null;
                if (Match("finally"))
                {
                    _finallyDepth++;
                    try { finallyBody = ParseStatement(); }
                    finally { _finallyDepth--; }
                }
                if (catches.Count == 0 && finallyBody == null) throw Error("try must be followed by catch or finally.");
                return WithSpan(new TryStmt(tryBody, catches, finallyBody), statementStart);
            }
            if (Peek().Text == "using" || Peek().Text == "lock" || Peek().Text == "yield")
                throw Unsupported(Peek().Text + " statements are not supported by the C# subset.");
            if (Match("if"))
            {
                Expect("("); var condition = ParseExpression(); Expect(")"); var whenTrue = ParseStatement();
                Stmt whenFalse = Match("else") ? ParseStatement() : null;
                return WithSpan(new IfStmt(condition, whenTrue, whenFalse), statementStart);
            }
            if (Match("while"))
            { Expect("("); var condition = ParseExpression(); Expect(")"); return WithSpan(new WhileStmt(condition, ParseStatement()), statementStart); }
            if (Match("for"))
            {
                Expect("("); Stmt init = null;
                if (!Match(";")) { init = LooksLikeVariableDeclaration() ? ParseVariable(false) : new ExpressionStmt(ParseExpression()); Expect(";"); }
                Expr condition = null; if (!Match(";")) { condition = ParseExpression(); Expect(";"); }
                Expr increment = null; if (!Match(")")) { increment = ParseExpression(); Expect(")"); }
                return WithSpan(new ForStmt(init, condition, increment, ParseStatement()), statementStart);
            }
            if (Match("foreach"))
            {
                Expect("(");
                if (Peek().Kind != TokenKind.Identifier) throw Error("foreach variable type is required.");
                ParseTypeName();
                string name = ExpectIdentifier(); Expect("in"); Expr source = ParseExpression(); Expect(")");
                return WithSpan(new ForeachStmt(name, source, ParseStatement()), statementStart);
            }
            if (Match("throw")) { Expr value = Peek().Text == ";" ? null : ParseExpression(); Expect(";"); return WithSpan(new ThrowStmt(value), statementStart); }
            if (Match("return")) { if (_finallyDepth > 0) throw Unsupported("return is not allowed from finally."); Expr value = Peek().Text == ";" ? null : ParseExpression(); Expect(";"); return WithSpan(new ReturnStmt(value), statementStart); }
            if (Match("break")) { if (_finallyDepth > 0) throw Unsupported("break is not allowed from finally."); Expect(";"); return WithSpan(new SignalStmt("break"), statementStart); }
            if (Match("continue")) { if (_finallyDepth > 0) throw Unsupported("continue is not allowed from finally."); Expect(";"); return WithSpan(new SignalStmt("continue"), statementStart); }
            if (LooksLikeVariableDeclaration()) return ParseVariable(true);
            Expr expression = ParseExpression(); Expect(";"); return WithSpan(new ExpressionStmt(expression), statementStart);
        }

        private bool LooksLikeVariableDeclaration()
        {
            if (Peek().Kind != TokenKind.Identifier) return false;
            int save = _position;
            try
            {
                ParseTypeName();
                bool result = Peek().Kind == TokenKind.Identifier && (Peek(1).Text == "=" || Peek(1).Text == ";");
                _position = save;
                return result;
            }
            catch { _position = save; return false; }
        }
        private Stmt ParseVariable(bool semicolon)
        {
            int start = Peek().Position;
            string typeName = ParseTypeName();
            string name = ExpectIdentifier();
            Expr initializer = null;
            if (Match("="))
            {
                if (Peek().Text == "{") initializer = WithSpan(new ArrayCreationExpr(typeName, null, ParseArrayInitializer(), false, ArrayRank(typeName)), start);
                else initializer = ParseExpression();
            }
            if (semicolon) Expect(";");
            return WithSpan(new VariableStmt(name, typeName, initializer), start);
        }

        private Expr ParseExpression() { return WithParseDepth(ParseAssignment); }
        private Expr ParseAssignment()
        {
            int start = Peek().Position;
            if (TryParseLambda(out var lambda)) return WithSpan(lambda, start);
            Expr left = ParseConditional();
            if (new[] { "=", "+=", "-=", "*=", "/=", "%=", "??=" }.Contains(Peek().Text))
            { string op = Next().Text; return WithSpan(new AssignExpr(left, op, ParseAssignment()), start); }
            return left;
        }
        private Expr ParseConditional()
        {
            Expr condition = ParseNullCoalescing();
            if (!Match("?")) return condition;
            Expr whenTrue = ParseExpression(); Expect(":"); return new ConditionalExpr(condition, whenTrue, ParseExpression());
        }
        private Expr ParseNullCoalescing()
        {
            Expr left = ParseBinary(0);
            return Match("??") ? new CoalesceExpr(left, ParseNullCoalescing()) : left;
        }
        private static readonly string[][] Precedence =
        {
            new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" },
            new[] { "==", "!=" }, new[] { "<", "<=", ">", ">=", "is", "as" },
            new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
        };
        private Expr ParseBinary(int level)
        {
            if (level >= Precedence.Length) return ParseUnary();
            Expr left = ParseBinary(level + 1);
            while (Precedence[level].Contains(Peek().Text))
            {
                string op = Next().Text;
                if (op == "is" || op == "as") left = new CastExpr(ParseTypeName(), left, op);
                else left = new BinaryExpr(left, op, ParseBinary(level + 1));
            }
            return left;
        }
        private Expr ParseUnary() { return WithParseDepth(ParseUnaryCore); }
        private Expr ParseUnaryCore()
        {
            int start = Peek().Position;
            if (Match("await")) return WithSpan(new AwaitExpr(ParseUnary()), start);
            if (new[] { "!", "+", "-", "~" }.Contains(Peek().Text)) { string op = Next().Text; return WithSpan(new UnaryExpr(op, ParseUnary()), start); }
            if (Match("++")) return WithSpan(new IncrementExpr(ParseUnary(), 1, false), start);
            if (Match("--")) return WithSpan(new IncrementExpr(ParseUnary(), -1, false), start);
            if (Peek().Text == "(" && LooksLikeCast())
            { Expect("("); string type = ParseTypeName(); Expect(")"); return WithSpan(new CastExpr(type, ParseUnary(), "cast"), start); }
            return ParsePostfix();
        }
        private Expr ParsePostfix()
        {
            int start = Peek().Position;
            Expr expression = ParsePrimary();
            while (true)
            {
                if (Match(".")) { expression = WithSpan(new MemberExpr(expression, ExpectIdentifier()), start); continue; }
                if (Match("?.")) { expression = WithSpan(new MemberExpr(expression, ExpectIdentifier(), true), start); continue; }
                if (TryParseGenericTypeArguments(out var genericArguments))
                {
                    if (Match("(")) expression = WithSpan(new CallExpr(expression, ParseArgumentList(")"), genericArguments), start);
                    else expression = WithSpan(new GenericTypePathExpr(expression, genericArguments), start);
                    continue;
                }
                if (Match("(")) { expression = WithSpan(new CallExpr(expression, ParseArgumentList(")")), start); continue; }
                if (Match("["))
                {
                    var indices = new List<Expr> { ParseExpression() };
                    while (Match(",")) indices.Add(ParseExpression());
                    Expect("]"); expression = WithSpan(new IndexExpr(expression, indices), start); continue;
                }
                if (Match("++")) { expression = WithSpan(new IncrementExpr(expression, 1, true), start); continue; }
                if (Match("--")) { expression = WithSpan(new IncrementExpr(expression, -1, true), start); continue; }
                break;
            }
            return expression;
        }
        private Expr ParsePrimary()
        {
            Token token = Peek();
            if (token.Kind == TokenKind.Number || token.Kind == TokenKind.String) { Next(); return WithSpan(new LiteralExpr(token.Value), token.Position); }
            if (Match("true")) return WithSpan(new LiteralExpr(true), token.Position);
            if (Match("false")) return WithSpan(new LiteralExpr(false), token.Position);
            if (Match("null")) return WithSpan(new LiteralExpr(null), token.Position);
            if (Match("typeof") || Match("default"))
            {
                string kind = token.Text;
                Expect("(");
                string typeName = ParseTypeName();
                Expect(")");
                return WithSpan(new TypeIntrinsicExpr(typeName, kind), token.Position);
            }
            if (Match("nameof"))
            {
                Expect("(");
                Expr operand = ParseExpression();
                Expect(")");
                string name = NameofOperand(operand);
                if (string.IsNullOrWhiteSpace(name)) throw Error("nameof requires a name or member access expression.");
                return WithSpan(new NameofExpr(name), token.Position);
            }
            if (Match("new"))
            {
                int start = token.Position;
                if (Match("["))
                {
                    Expect("]");
                    if (Peek().Text != "{") throw Error("Implicitly typed arrays require an initializer.");
                    return WithSpan(new ArrayCreationExpr("", null, ParseArrayInitializer(), true, 1), start);
                }
                string type = ParseTypeName();
                if (Match("(")) return WithSpan(new NewExpr(type, ParseArgumentList(")")), start);
                if (Match("["))
                {
                    var dimensions = new List<Expr>();
                    int rank = 1;
                    if (Peek().Text == "," || Peek().Text == "]")
                    {
                        while (Match(",")) rank++;
                        Expect("]");
                    }
                    else
                    {
                        dimensions.Add(ParseExpression());
                        while (Match(",")) { rank++; dimensions.Add(ParseExpression()); }
                        Expect("]");
                    }
                    if (rank > 4) throw ArrayRankUnsupported();
                    ArrayInitializerNode initializer = Peek().Text == "{" ? ParseArrayInitializer() : null;
                    return WithSpan(new ArrayCreationExpr(type, dimensions, initializer, false, rank), start);
                }
                if (type.EndsWith("]", StringComparison.Ordinal) && Match("{"))
                {
                    _position--;
                    return WithSpan(new ArrayCreationExpr(type, null, ParseArrayInitializer(), false, ArrayRank(type)), start);
                }
                throw Error("Expected constructor arguments or array dimensions after type name.");
            }
            if (Match("("))
            {
                Expr expression = ParseExpression(); Expect(")"); return expression;
            }
            if (token.Kind == TokenKind.Identifier) { Next(); return WithSpan(new NameExpr(token.Text), token.Position); }
            throw Error("Expected expression, got '" + token.Text + "'.");
        }
        private static string NameofOperand(Expr expression)
        {
            if (expression is NameExpr name) return name.Name;
            if (expression is MemberExpr member) return member.Name;
            return null;
        }
        private IReadOnlyList<Expr> ParseArgumentList(string terminator)
        {
            var values = new List<Expr>();
            if (Match(terminator)) return values;
            do { values.Add(ParseExpression()); } while (Match(","));
            Expect(terminator); return values;
        }
        private bool LooksLikeCast()
        {
            int save = _position;
            try
            {
                Expect("("); string typeName = ParseTypeName(); Expect(")");
                bool valid = ExecutionTypeResolver.Resolve(typeName) != null;
                _position = save; return valid;
            }
            catch { _position = save; return false; }
        }
        private string ParseTypeName() { return WithParseDepth(ParseTypeNameCore); }
        private string ParseTypeNameCore()
        {
            string name = ExpectIdentifier();
            while (Match(".")) name += "." + ExpectIdentifier();
            if (Match("<"))
            {
                var arguments = new List<string>();
                do { arguments.Add(ParseTypeName()); } while (Match(","));
                ExpectTypeClose();
                name += "<" + string.Join(",", arguments) + ">";
            }
            if (Match("?")) name += "?";
            while (Peek().Text == "[")
            {
                int save = _position;
                Next();
                int rank = 1;
                while (Match(",")) rank++;
                if (!Match("]")) { _position = save; break; }
                if (rank > 4) throw ArrayRankUnsupported();
                name += "[" + new string(',', rank - 1) + "]";
            }
            return name;
        }

        private T WithParseDepth<T>(Func<T> parse)
        {
            _parseDepth++;
            try
            {
                if (_parseDepth > MaxParseDepth)
                {
                    Token token = Peek();
                    throw new ExecutionContractException(
                        "CSHARP_PARSE_DEPTH_EXCEEDED",
                        "C# subset parse depth exceeded the limit of " + MaxParseDepth.ToString(CultureInfo.InvariantCulture) + ".",
                        new Dictionary<string, object>
                        {
                            { "stage", "parse" },
                            { "limit", MaxParseDepth },
                            { "position", token.Position },
                            { "sourceSpan", _sourceMap.Resolve(token.Position, Math.Max(1, token.Length)) },
                        });
                }
                return parse();
            }
            finally { _parseDepth--; }
        }

        private bool TryParseLambda(out Expr expression)
        {
            expression = null;
            int save = _position;
            bool isAsync = Match("async");
            var parameters = new List<LambdaParameter>();
            try
            {
                if (Peek().Kind == TokenKind.Identifier && Peek(1).Text == "=>")
                {
                    parameters.Add(new LambdaParameter(Next().Text));
                    Next();
                }
                else if (Match("("))
                {
                    if (!Match(")"))
                    {
                        do
                        {
                            if (Peek().Kind != TokenKind.Identifier) { _position = save; return false; }
                            string first = Next().Text;
                            if (Peek().Text == "," || Peek().Text == ")") parameters.Add(new LambdaParameter(first));
                            else
                            {
                                _position--;
                                string typeName = ParseTypeName();
                                string name = ExpectIdentifier();
                                parameters.Add(new LambdaParameter(name, typeName));
                            }
                        } while (Match(","));
                        Expect(")");
                    }
                    if (!Match("=>")) { _position = save; return false; }
                }
                else { _position = save; return false; }
                if (Peek().Text == "{") expression = new LambdaExpr(parameters.ToArray(), null, ParseStatement(), isAsync);
                else expression = new LambdaExpr(parameters.ToArray(), ParseAssignment(), null, isAsync);
                return true;
            }
            catch (ExecutionContractException)
            {
                _position = save;
                if (isAsync) throw;
                return false;
            }
        }

        private ArrayInitializerNode ParseArrayInitializer()
        {
            Expect("{");
            var items = new List<ArrayInitializerNode>();
            if (Match("}")) return new ArrayInitializerNode(items);
            while (true)
            {
                items.Add(Peek().Text == "{" ? ParseArrayInitializer() : new ArrayInitializerNode(ParseExpression()));
                if (Match("}")) break;
                Expect(",");
                if (Match("}")) break;
            }
            return new ArrayInitializerNode(items);
        }

        private static int ArrayRank(string typeName)
        {
            int close = typeName == null ? -1 : typeName.LastIndexOf(']');
            int open = close < 0 ? -1 : typeName.LastIndexOf('[', close);
            return open < 0 ? 1 : typeName.Substring(open + 1, close - open - 1).Count(ch => ch == ',') + 1;
        }

        private ExecutionContractException ArrayRankUnsupported()
        {
            return new ExecutionContractException("CSHARP_ARRAY_RANK_UNSUPPORTED", "Array ranks greater than 4 are not supported.",
                new Dictionary<string, object> { { "sourceSpan", _sourceMap.Resolve(Peek().Position, Peek().Length) }, { "stage", "parse" } });
        }

        private bool TryParseGenericTypeArguments(out IReadOnlyList<string> arguments)
        {
            arguments = null;
            if (Peek().Text != "<") return false;
            int save = _position;
            try
            {
                Next();
                var values = new List<string>();
                do { values.Add(ParseTypeName()); } while (Match(","));
                ExpectTypeClose();
                // A parsed generic suffix is expression syntax only when it is
                // immediately followed by a member or invocation.  Keeping the
                // rollback for all other followers preserves '<'/'>' comparison
                // parsing, while allowing ClosedType<T>.StaticMember.
                if (Peek().Text != "(" && Peek().Text != ".") { _position = save; return false; }
                arguments = values;
                return true;
            }
            catch { _position = save; return false; }
        }

        private T WithSpan<T>(T node, int start) where T : AstNode
        {
            int end = _position <= 0 ? start : _tokens[Math.Max(0, _position - 1)].Position + _tokens[Math.Max(0, _position - 1)].Length;
            node.Span = _sourceMap.Resolve(start, Math.Max(0, end - start));
            return node;
        }
        private Token Peek(int offset = 0) { return _tokens[Math.Min(_tokens.Count - 1, _position + offset)]; }
        private Token Next() { return _tokens[Math.Min(_tokens.Count - 1, _position++)]; }
        private bool Match(string text) { if (Peek().Text != text) return false; _position++; return true; }
        private void Expect(string text) { if (!Match(text)) throw Error("Expected '" + text + "', got '" + Peek().Text + "'."); }
        private void ExpectTypeClose()
        {
            if (Match(">")) return;
            if (Peek().Text == ">>")
            {
                Peek().Text = ">";
                Peek().Position++;
                Peek().Length = 1;
                return;
            }
            throw Error("Expected '>', got '" + Peek().Text + "'.");
        }
        private string ExpectIdentifier() { if (Peek().Kind != TokenKind.Identifier) throw Error("Expected identifier."); return Next().Text; }
        private void ExpectEnd() { if (Peek().Kind != TokenKind.End) throw Error("Expected end of input."); }
        private ExecutionContractException Error(string message)
        {
            return new ExecutionContractException("CSHARP_PARSE_ERROR", message, new Dictionary<string, object>
            {
                { "position", Peek().Position }, { "token", Peek().Text },
                { "sourceSpan", _sourceMap.Resolve(Peek().Position, Peek().Length) },
                { "stage", "parse" },
            });
        }

        private ExecutionContractException Unsupported(string message)
        {
            return new ExecutionContractException("CSHARP_UNSUPPORTED_SYNTAX", message, new Dictionary<string, object>
            {
                { "sourceSpan", _sourceMap.Resolve(Peek().Position, Peek().Length) }, { "stage", "parse" },
            });
        }
    }
}
