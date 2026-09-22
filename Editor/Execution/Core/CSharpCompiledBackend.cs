using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CodingRiver.UPilot.Execution
{
    /// <summary>
    /// Direct compiler for the statically bindable synchronous core of the UPilot subset.
    /// Unsupported nodes fail before execution and are never interpreted implicitly.
    /// </summary>
    public static class CSharpCompiledBackend
    {
        private sealed class Entry
        {
            public Func<CSharpEvaluationContext, CSharpEvaluationResult> Delegate;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static string CacheKey(string code, string mode, IEnumerable<string> imports, IDictionary<string, object> variables)
        {
            var variableTypes = (variables ?? new Dictionary<string, object>())
                .ToDictionary(pair => pair.Key, pair => pair.Value?.GetType(), StringComparer.Ordinal);
            return CacheKeyForTypes(code, mode, imports, variableTypes);
        }

        public static string CacheKeyForTypes(string code, string mode, IEnumerable<string> imports, IDictionary<string, Type> variableTypes)
        {
            string variableShape = string.Join(";", (variableTypes ?? new Dictionary<string, Type>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + (pair.Value == null ? "<unknown>" : pair.Value.AssemblyQualifiedName)));
            string canonical = CSharpSubsetEngine.LanguageProfile + "\ncompiled-v1\n" +
                               (mode ?? "auto").Trim().ToLowerInvariant() + "\n" +
                               string.Join(";", imports ?? Array.Empty<string>()) + "\n" + variableShape + "\n" + (code ?? "");
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Select(value => value.ToString("x2")));
        }

        public static bool IsCached(string key)
        {
            lock (Gate) return !string.IsNullOrWhiteSpace(key) && Entries.ContainsKey(key);
        }

        public static Func<CSharpEvaluationContext, CSharpEvaluationResult> Compile(
            string key, string code, string mode, CSharpEvaluationContext preparationContext,
            IDictionary<string, Type> declaredVariableTypes = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ExecutionContractException("CSHARP_COMPILED_INVALID_CACHE_KEY", "Compiled cache key is required.");
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var cached)) return cached.Delegate;
                var program = CSharpSubsetEngine.Parse(code, mode);
                program.ValidateSynchronousEmitProfile();
                IDictionary<string, Type> variableTypes = declaredVariableTypes;
                if (variableTypes == null)
                    variableTypes = (preparationContext?.SnapshotVariables() ?? new Dictionary<string, object>())
                        .ToDictionary(pair => pair.Key, pair => pair.Value?.GetType(), StringComparer.Ordinal);
                var raw = new Lowerer(program, preparationContext?.Imports ?? new[] { "System" }, variableTypes,
                    preparationContext?.Policy ?? new RestrictedEvalExecutionPolicy()).Compile();
                Func<CSharpEvaluationContext, CSharpEvaluationResult> compiled = context => Execute(raw, program, context);
                Entries[key] = new Entry { Delegate = compiled };
                return compiled;
            }
        }

        private static CSharpEvaluationResult Execute(
            Func<CSharpEvaluationContext, object> compiled,
            CSharpProgram program,
            CSharpEvaluationContext context)
        {
            context = context ?? new CSharpEvaluationContext();
            object value;
            try { value = compiled(context); }
            catch (ExecutionContractException ex)
            {
                CSharpSubsetEngine.AttachExecutionDetail(ex, context, program.SourceMap);
                throw;
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" } });
                CSharpSubsetEngine.AttachExecutionDetail(cancelled, context, program.SourceMap);
                throw cancelled;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                string stack = inner.StackTrace ?? "";
                if (stack.Length > 4096) stack = stack.Substring(0, 4096);
                var runtime = new ExecutionContractException("CSHARP_RUNTIME_ERROR", inner.GetType().FullName + ": " + inner.Message,
                    new Dictionary<string, object> { { "exceptionType", inner.GetType().FullName }, { "stackTrace", stack } });
                CSharpSubsetEngine.AttachExecutionDetail(runtime, context, program.SourceMap);
                throw runtime;
            }

            return new CSharpEvaluationResult
            {
                Value = CSharpProgram.RequireResolvedResult(value),
                Variables = context.SnapshotVariables().ToDictionary(
                    pair => pair.Key,
                    pair => CSharpProgram.RequireResolvedResult(pair.Value),
                    StringComparer.Ordinal),
                SideEffectsMayHaveOccurred = context.SideEffectsMayHaveOccurred,
                Budget = context.Budget,
                Diagnostics = context.Diagnostics,
                ModeUsed = program.IsExpressionOnly ? "expression" : "statements",
            };
        }

        private static object ReadMember(CSharpEvaluationContext context, MemberInfo member, object target)
        {
            context.Policy.EnsureMemberAllowed(member);
            if (member is PropertyInfo property)
            {
                context.Diagnostics.CountGetter();
                return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => property.GetValue(target, null)));
            }
            if (member is FieldInfo field)
                return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => field.GetValue(target)));
            throw new ExecutionContractException("CSHARP_BIND_ERROR", "Compiled member is not readable: " + member.Name);
        }

        private static object WriteMember(CSharpEvaluationContext context, MemberInfo member, object target, object value)
        {
            context.Policy.EnsureMemberAllowed(member);
            context.Invoke(() =>
            {
                if (member is PropertyInfo property) property.SetValue(target, value, null);
                else if (member is FieldInfo field) field.SetValue(target, value);
                else throw new ExecutionContractException("CSHARP_BIND_ERROR", "Compiled member is not writable: " + member.Name);
                return null;
            });
            return value;
        }

        private static object InvokeMethod(CSharpEvaluationContext context, MethodInfo method, object target, object[] arguments)
        {
            context.Policy.EnsureMemberAllowed(method);
            context.Budget.CountCall();
            context.Diagnostics.CountMethod();
            try
            {
                return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => method.Invoke(target, arguments)));
            }
            catch (TargetInvocationException ex)
            {
                return CSharpSubsetEngine.RethrowTargetInvocation(ex);
            }
        }

        private static object Construct(CSharpEvaluationContext context, ConstructorInfo constructor, object[] arguments)
        {
            context.Policy.EnsureConstructionAllowed(constructor.DeclaringType);
            context.Budget.CountAllocation();
            try { return context.Invoke(() => constructor.Invoke(arguments)); }
            catch (TargetInvocationException ex) { return CSharpSubsetEngine.RethrowTargetInvocation(ex); }
        }

        private static object ReadIndex(CSharpEvaluationContext context, object target, PropertyInfo indexer, object[] indices)
        {
            if (target is Array array)
                return context.Invoke(() => array.GetValue(indices.Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToArray()));
            context.Policy.EnsureMemberAllowed(indexer);
            context.Diagnostics.CountGetter();
            return context.Diagnostics.MeasureInvoke(() => context.Invoke(() => indexer.GetValue(target, indices)));
        }

        private static object WriteIndex(CSharpEvaluationContext context, object target, PropertyInfo indexer, object[] indices, object value)
        {
            if (target is Array array)
            {
                context.Invoke(() =>
                {
                    array.SetValue(value, indices.Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray());
                    return null;
                });
                return value;
            }
            context.Policy.EnsureMemberAllowed(indexer);
            context.Invoke(() => { indexer.SetValue(target, value, indices); return null; });
            return value;
        }

        private sealed class Lowerer
        {
            private sealed class Binding
            {
                public string Name;
                public Type Type;
                public ParameterExpression Local;
                public bool Persisted;
            }

            private readonly CSharpProgram _program;
            private readonly IReadOnlyList<string> _imports;
            private readonly IDictionary<string, Type> _externalTypes;
            private readonly IExecutionPolicy _policy;
            private readonly ParameterExpression _context = Expression.Parameter(typeof(CSharpEvaluationContext), "context");
            private readonly LabelTarget _return = Expression.Label(typeof(object), "return");
            private readonly Stack<Dictionary<string, Binding>> _scopes = new Stack<Dictionary<string, Binding>>();
            private readonly Stack<Tuple<LabelTarget, LabelTarget>> _loops = new Stack<Tuple<LabelTarget, LabelTarget>>();

            private static readonly MethodInfo EnterNodeMethod = typeof(CSharpEvaluationContext).GetMethod(
                "EnterNode", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly MethodInfo GetVariableMethod = typeof(CSharpEvaluationContext).GetMethod(nameof(CSharpEvaluationContext.GetVariable));
            private static readonly MethodInfo SetVariableMethod = typeof(CSharpEvaluationContext).GetMethod(nameof(CSharpEvaluationContext.SetVariable));
            private static readonly MethodInfo DeclareVariableMethod = typeof(CSharpEvaluationContext).GetMethod(nameof(CSharpEvaluationContext.DeclareVariable));
            private static readonly PropertyInfo BudgetProperty = typeof(CSharpEvaluationContext).GetProperty(nameof(CSharpEvaluationContext.Budget));
            private static readonly MethodInfo CountStatementMethod = typeof(ExecutionBudget).GetMethod(nameof(ExecutionBudget.CountStatement));
            private static readonly MethodInfo CountLoopMethod = typeof(ExecutionBudget).GetMethod(nameof(ExecutionBudget.CountLoop));
            private static readonly MethodInfo CountAllocationMethod = typeof(ExecutionBudget).GetMethod(nameof(ExecutionBudget.CountAllocation));
            private static readonly MethodInfo CountArrayElementsMethod = typeof(ExecutionBudget).GetMethod(nameof(ExecutionBudget.CountArrayElements));
            private static readonly MethodInfo ReadMemberMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(ReadMember), BindingFlags.Static | BindingFlags.NonPublic);
            private static readonly MethodInfo WriteMemberMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(WriteMember), BindingFlags.Static | BindingFlags.NonPublic);
            private static readonly MethodInfo InvokeMethodMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(InvokeMethod), BindingFlags.Static | BindingFlags.NonPublic);
            private static readonly MethodInfo ConstructMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(Construct), BindingFlags.Static | BindingFlags.NonPublic);
            private static readonly MethodInfo ReadIndexMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(ReadIndex), BindingFlags.Static | BindingFlags.NonPublic);
            private static readonly MethodInfo WriteIndexMethod = typeof(CSharpCompiledBackend).GetMethod(nameof(WriteIndex), BindingFlags.Static | BindingFlags.NonPublic);

            public Lowerer(CSharpProgram program, IReadOnlyList<string> imports, IDictionary<string, Type> variables,
                IExecutionPolicy policy)
            {
                _program = program;
                _imports = imports ?? new[] { "System" };
                _policy = policy ?? new RestrictedEvalExecutionPolicy();
                _externalTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var pair in variables ?? new Dictionary<string, Type>())
                {
                    if (pair.Value == null)
                        throw Unsupported(null, "External variable '" + pair.Key + "' has no declared type for direct compilation.");
                    _externalTypes[pair.Key] = pair.Value;
                }
            }

            public Func<CSharpEvaluationContext, object> Compile()
            {
                try
                {
                    Expression root = CompileBlock((BlockStmt)_program.Root, true);
                    var body = Expression.Label(_return, Box(root));
                    return Expression.Lambda<Func<CSharpEvaluationContext, object>>(body, _context).Compile();
                }
                catch (ExecutionContractException) { throw; }
                catch (Exception ex)
                {
                    throw new ExecutionContractException("CSHARP_COMPILED_COMPILE_FAILED",
                        ex.GetType().FullName + ": " + ex.Message,
                        new Dictionary<string, object> { { "stage", "bind" } });
                }
            }

            private Expression CompileBlock(BlockStmt block, bool isRoot = false)
            {
                var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
                _scopes.Push(scope);
                var expressions = new List<Expression>();
                foreach (Stmt statement in block.Statements)
                {
                    expressions.Add(Expression.Call(_context, EnterNodeMethod, Expression.Constant(statement, typeof(AstNode))));
                    expressions.Add(Expression.Call(Expression.Property(_context, BudgetProperty), CountStatementMethod));
                    expressions.Add(Box(CompileStatement(statement, isRoot && _scopes.Count == 1)));
                }
                if (expressions.Count == 0) expressions.Add(Expression.Constant(null, typeof(object)));
                var locals = scope.Values.Where(binding => binding.Local != null).Select(binding => binding.Local).ToArray();
                _scopes.Pop();
                return Expression.Block(locals, expressions);
            }

            private Expression CompileStatement(Stmt statement, bool persistDeclarations)
            {
                if (statement is BlockStmt block) return CompileBlock(block);
                if (statement is ExpressionStmt expression) return CompileExpression(expression.Expression);
                if (statement is VariableStmt variable) return CompileVariable(variable, persistDeclarations);
                if (statement is ReturnStmt returnStatement)
                    return Expression.Return(_return, returnStatement.Value == null
                        ? Expression.Constant(null, typeof(object))
                        : Box(CompileExpression(returnStatement.Value)), typeof(object));
                if (statement is IfStmt conditional) return CompileIf(conditional);
                if (statement is WhileStmt whileStatement) return CompileWhile(whileStatement);
                if (statement is ForStmt forStatement) return CompileFor(forStatement);
                if (statement is SignalStmt signal) return CompileSignal(signal);
                throw Unsupported(statement, "Statement node is not supported by the direct compiler: " + statement.GetType().Name);
            }

            private Expression CompileVariable(VariableStmt variable, bool persisted)
            {
                Expression initializer = variable.Initializer == null
                    ? Expression.Constant(null, typeof(object))
                    : CompileExpression(variable.Initializer);
                Type type;
                if (variable.TypeName == "var")
                {
                    type = initializer.Type;
                    if (type == typeof(object) && IsNullConstant(initializer))
                        throw Unsupported(variable, "A directly compiled var local requires a statically known initializer type.");
                }
                else
                {
                    type = ExecutionTypeResolver.Resolve(variable.TypeName, _imports);
                    if (type == null) throw Unsupported(variable, "Variable type was not found: " + variable.TypeName);
                }
                if (_scopes.Peek().ContainsKey(variable.Name))
                    throw new ExecutionContractException("CSHARP_DUPLICATE_LOCAL", "Variable already exists in this scope: " + variable.Name);
                var local = Expression.Variable(type, variable.Name);
                var binding = new Binding { Name = variable.Name, Type = type, Local = local, Persisted = persisted };
                _scopes.Peek()[variable.Name] = binding;
                Expression converted = ConvertTo(initializer, type, variable);
                var parts = new List<Expression> { Expression.Assign(local, converted) };
                if (persisted)
                    parts.Add(Expression.Call(_context, DeclareVariableMethod, Expression.Constant(variable.Name), Box(local)));
                parts.Add(local);
                return Expression.Block(parts);
            }

            private Expression CompileIf(IfStmt statement)
            {
                Expression condition = RequireBoolean(CompileExpression(statement.Condition), statement.Condition);
                Expression whenTrue = Box(CompileStatement(statement.WhenTrue, false));
                Expression whenFalse = statement.WhenFalse == null
                    ? Expression.Constant(null, typeof(object))
                    : Box(CompileStatement(statement.WhenFalse, false));
                return Expression.Condition(condition, whenTrue, whenFalse);
            }

            private Expression CompileWhile(WhileStmt statement)
            {
                var breakLabel = Expression.Label(typeof(object), "while_break");
                var continueLabel = Expression.Label("while_continue");
                _loops.Push(Tuple.Create(breakLabel, continueLabel));
                Expression body = Box(CompileStatement(statement.Body, false));
                _loops.Pop();
                return Expression.Loop(
                    Expression.Block(
                        Expression.IfThen(Expression.Not(RequireBoolean(CompileExpression(statement.Condition), statement.Condition)),
                            Expression.Break(breakLabel, Expression.Constant(null, typeof(object)))),
                        Expression.Call(Expression.Property(_context, BudgetProperty), CountLoopMethod),
                        body,
                        Expression.Label(continueLabel)),
                    breakLabel);
            }

            private Expression CompileFor(ForStmt statement)
            {
                var scope = new Dictionary<string, Binding>(StringComparer.Ordinal);
                _scopes.Push(scope);
                var breakLabel = Expression.Label(typeof(object), "for_break");
                var continueLabel = Expression.Label("for_continue");
                _loops.Push(Tuple.Create(breakLabel, continueLabel));
                Expression initializer = statement.Initializer == null
                    ? Expression.Constant(null, typeof(object))
                    : Box(CompileStatement(statement.Initializer, false));
                Expression condition = statement.Condition == null
                    ? Expression.Constant(true)
                    : RequireBoolean(CompileExpression(statement.Condition), statement.Condition);
                Expression body = Box(CompileStatement(statement.Body, false));
                Expression increment = statement.Increment == null
                    ? Expression.Constant(null, typeof(object))
                    : Box(CompileExpression(statement.Increment));
                _loops.Pop();
                var loop = Expression.Loop(
                    Expression.Block(
                        Expression.IfThen(Expression.Not(condition),
                            Expression.Break(breakLabel, Expression.Constant(null, typeof(object)))),
                        Expression.Call(Expression.Property(_context, BudgetProperty), CountLoopMethod),
                        body,
                        Expression.Label(continueLabel),
                        increment),
                    breakLabel);
                var locals = scope.Values.Where(binding => binding.Local != null).Select(binding => binding.Local).ToArray();
                _scopes.Pop();
                return Expression.Block(locals, initializer, loop);
            }

            private Expression CompileSignal(SignalStmt signal)
            {
                if (_loops.Count == 0) throw Unsupported(signal, signal.Kind + " is only valid inside a loop.");
                var labels = _loops.Peek();
                return signal.Kind == "break"
                    ? (Expression)Expression.Break(labels.Item1, Expression.Constant(null, typeof(object)))
                    : Expression.Continue(labels.Item2);
            }

            private Expression CompileExpression(Expr expression)
            {
                if (expression is LiteralExpr literal)
                    return literal.Value == null ? Expression.Constant(null, typeof(object)) : Expression.Constant(literal.Value, literal.Value.GetType());
                if (expression is NameExpr name)
                {
                    if (TryResolveBinding(name.Name, out var binding)) return ReadBinding(binding);
                    throw Unsupported(name, "A type or namespace name is only valid as a member-call target in the direct compiler: " + name.Name);
                }
                if (expression is MemberExpr member) return CompileMemberRead(member);
                if (expression is CallExpr call) return CompileCall(call);
                if (expression is NewExpr creation) return CompileNew(creation);
                if (expression is IndexExpr index) return CompileIndexRead(index);
                if (expression is ArrayCreationExpr array) return CompileArray(array);
                if (expression is CastExpr cast) return CompileCast(cast);
                if (expression is CoalesceExpr coalesce) return CompileCoalesce(coalesce);
                if (expression is TypeIntrinsicExpr intrinsic) return CompileTypeIntrinsic(intrinsic);
                if (expression is NameofExpr nameofExpression) return Expression.Constant(nameofExpression.Name);
                if (expression is UnaryExpr unary) return CompileUnary(unary);
                if (expression is BinaryExpr binary) return CompileBinary(binary.Operator, CompileExpression(binary.Left), CompileExpression(binary.Right), binary);
                if (expression is ConditionalExpr conditional) return CompileConditional(conditional);
                if (expression is AssignExpr assignment) return CompileAssignment(assignment);
                if (expression is IncrementExpr increment) return CompileIncrement(increment);
                throw Unsupported(expression, "Expression node is not supported by the direct compiler: " + expression.GetType().Name);
            }

            private sealed class Receiver
            {
                public Expression Instance;
                public Type Type;
                public bool IsStatic;
            }

            private sealed class BoundMember
            {
                public MemberInfo Member;
                public Type ValueType;
            }

            private Expression CompileMemberRead(MemberExpr member)
            {
                Receiver receiver = ResolveReceiver(member.Target, member);
                BoundMember bound = BindReadableMember(receiver.Type, member.Name, receiver.IsStatic, member);
                _policy.EnsureMemberAllowed(bound.Member);
                Expression read = Expression.Convert(Expression.Call(ReadMemberMethod,
                    _context,
                    Expression.Constant(bound.Member, typeof(MemberInfo)),
                    receiver.IsStatic ? Expression.Constant(null, typeof(object)) : Box(receiver.Instance)), bound.ValueType);
                if (!member.IsConditional) return read;
                if (receiver.IsStatic) throw Unsupported(member, "Conditional access cannot target a static type.");
                if (receiver.Instance.Type.IsValueType) throw Unsupported(member, "Conditional access requires a reference-type receiver.");
                var target = Expression.Variable(receiver.Instance.Type, "conditionalTarget");
                return Expression.Block(new[] { target },
                    Expression.Assign(target, receiver.Instance),
                    Expression.Condition(
                        Expression.Equal(target, Expression.Constant(null, target.Type)),
                        Expression.Constant(null, typeof(object)),
                        Box(Expression.Convert(Expression.Call(ReadMemberMethod, _context,
                            Expression.Constant(bound.Member, typeof(MemberInfo)), Box(target)), bound.ValueType))));
            }

            private Expression CompileCall(CallExpr call)
            {
                if (!(call.Callee is MemberExpr member))
                    throw Unsupported(call.Callee, "The direct compiler supports statically bound member calls only.");
                Receiver receiver = ResolveReceiver(member.Target, member);
                Expression[] arguments = call.Arguments.Select(CompileExpression).ToArray();
                MethodInfo method = BindMethod(receiver.Type, member.Name, receiver.IsStatic, arguments,
                    call.GenericTypeNames, call);
                _policy.EnsureMemberAllowed(method);
                Expression[] converted = ConvertArguments(method.GetParameters(), arguments, call);
                Expression invoke = Expression.Call(InvokeMethodMethod,
                    _context,
                    Expression.Constant(method, typeof(MethodInfo)),
                    receiver.IsStatic ? Expression.Constant(null, typeof(object)) : Box(receiver.Instance),
                    Expression.NewArrayInit(typeof(object), converted.Select(Box)));
                Expression result = method.ReturnType == typeof(void)
                    ? invoke
                    : Expression.Convert(invoke, method.ReturnType);
                if (!member.IsConditional) return result;
                if (receiver.IsStatic || receiver.Instance.Type.IsValueType)
                    throw Unsupported(member, "Conditional calls require a reference-type instance receiver.");
                var target = Expression.Variable(receiver.Instance.Type, "conditionalCallTarget");
                Expression conditionalInvoke = Expression.Call(InvokeMethodMethod,
                    _context,
                    Expression.Constant(method, typeof(MethodInfo)),
                    Box(target),
                    Expression.NewArrayInit(typeof(object), converted.Select(Box)));
                return Expression.Block(new[] { target },
                    Expression.Assign(target, receiver.Instance),
                    Expression.Condition(
                        Expression.Equal(target, Expression.Constant(null, target.Type)),
                        Expression.Constant(null, typeof(object)),
                        method.ReturnType == typeof(void) ? conditionalInvoke : Box(Expression.Convert(conditionalInvoke, method.ReturnType))));
            }

            private Expression CompileNew(NewExpr creation)
            {
                Type type = ResolveType(creation.TypeName, creation);
                _policy.EnsureConstructionAllowed(type);
                Expression[] arguments = creation.Arguments.Select(CompileExpression).ToArray();
                ConstructorInfo constructor = BindConstructor(type, arguments, creation);
                Expression[] converted = ConvertArguments(constructor.GetParameters(), arguments, creation);
                return Expression.Convert(Expression.Call(ConstructMethod,
                    _context,
                    Expression.Constant(constructor, typeof(ConstructorInfo)),
                    Expression.NewArrayInit(typeof(object), converted.Select(Box))), type);
            }

            private Expression CompileCast(CastExpr cast)
            {
                Type type = ResolveType(cast.TypeName, cast);
                _policy.EnsureTypeAllowed(type);
                Expression value = CompileExpression(cast.Value);
                if (cast.Kind == "is") return Expression.TypeIs(Box(value), type);
                if (cast.Kind == "as")
                {
                    if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                        throw Unsupported(cast, "The as operator requires a reference or nullable target type.");
                    return Expression.TypeAs(Box(value), type);
                }
                return ConvertTo(value, type, cast);
            }

            private Expression CompileTypeIntrinsic(TypeIntrinsicExpr intrinsic)
            {
                Type type = ResolveType(intrinsic.TypeName, intrinsic);
                _policy.EnsureTypeAllowed(type);
                return intrinsic.Kind == "typeof"
                    ? Expression.Constant(type, typeof(Type))
                    : Expression.Default(type);
            }

            private Expression CompileCoalesce(CoalesceExpr coalesce)
            {
                Expression left = CompileExpression(coalesce.Left);
                Expression right = CompileExpression(coalesce.Right);
                Type nullable = Nullable.GetUnderlyingType(left.Type);
                if (nullable != null)
                {
                    Expression fallback = ConvertTo(right, nullable, coalesce);
                    return Expression.Coalesce(left, fallback);
                }
                if (left.Type.IsValueType)
                    throw Unsupported(coalesce, "The left operand of ?? must be a reference or nullable value.");
                return Expression.Coalesce(left, ConvertTo(right, left.Type, coalesce));
            }

            private Expression CompileIndexRead(IndexExpr index)
            {
                Expression target = CompileExpression(index.Target);
                Expression[] indices = index.Indices.Select(CompileExpression).ToArray();
                PropertyInfo indexer = BindIndexer(target.Type, indices, requireSetter: false, index);
                Type valueType = target.Type.IsArray ? target.Type.GetElementType() : indexer.PropertyType;
                Expression[] converted = ConvertIndexArguments(target.Type, indexer, indices, index);
                return Expression.Convert(Expression.Call(ReadIndexMethod,
                    _context,
                    Box(target),
                    Expression.Constant(indexer, typeof(PropertyInfo)),
                    Expression.NewArrayInit(typeof(object), converted.Select(Box))), valueType);
            }

            private Expression CompileArray(ArrayCreationExpr array)
            {
                if (array.Rank != 1) throw Unsupported(array, "The direct compiler currently supports one-dimensional arrays.");
                Type elementType = string.IsNullOrWhiteSpace(array.TypeName) ? null : ResolveType(array.TypeName, array);
                if (elementType != null && elementType.IsArray) elementType = elementType.GetElementType();
                Expression created;
                long knownLength = -1;
                if (array.Initializer != null)
                {
                    if (array.Initializer.IsLeaf)
                        throw Unsupported(array, "Array initializer root must contain a list.");
                    Expression[] values = array.Initializer.Children.Select(child =>
                    {
                        if (!child.IsLeaf) throw Unsupported(array, "Nested initializers require a multidimensional array.");
                        return CompileExpression(child.Value);
                    }).ToArray();
                    if (elementType == null) elementType = InferArrayElementType(values, array);
                    created = Expression.NewArrayInit(elementType, values.Select(value => ConvertTo(value, elementType, array)));
                    knownLength = values.Length;
                }
                else
                {
                    if (elementType == null) throw Unsupported(array, "An explicit array element type is required without an initializer.");
                    if (array.Dimensions.Count != 1) throw Unsupported(array, "A one-dimensional array requires exactly one length.");
                    created = Expression.NewArrayBounds(elementType, ConvertTo(CompileExpression(array.Dimensions[0]), typeof(int), array));
                }
                _policy.EnsureTypeAllowed(elementType);
                var result = Expression.Variable(elementType.MakeArrayType(), "array");
                Expression count = knownLength >= 0
                    ? Expression.Constant(knownLength)
                    : Expression.Convert(Expression.ArrayLength(result), typeof(long));
                return Expression.Block(new[] { result },
                    Expression.Call(Expression.Property(_context, BudgetProperty), CountAllocationMethod),
                    Expression.Assign(result, created),
                    Expression.Call(Expression.Property(_context, BudgetProperty), CountArrayElementsMethod, count),
                    result);
            }

            private Receiver ResolveReceiver(Expr expression, AstNode node)
            {
                if (TryGetTypePath(expression, out string path))
                {
                    Type staticType = ExecutionTypeResolver.Resolve(path, _imports);
                    if (staticType != null)
                    {
                        _policy.EnsureTypeAllowed(staticType);
                        return new Receiver { Type = staticType, IsStatic = true };
                    }
                }
                Expression instance = CompileExpression(expression);
                return new Receiver { Instance = instance, Type = instance.Type, IsStatic = false };
            }

            private static bool TryGetTypePath(Expr expression, out string path)
            {
                if (expression is NameExpr name)
                {
                    path = name.Name;
                    return true;
                }
                if (expression is MemberExpr member && !member.IsConditional && TryGetTypePath(member.Target, out string parent))
                {
                    path = parent + "." + member.Name;
                    return true;
                }
                path = null;
                return false;
            }

            private Type ResolveType(string name, AstNode node)
            {
                Type type = ExecutionTypeResolver.Resolve(name, _imports);
                if (type == null) throw Unsupported(node, "Type was not found for direct compilation: " + name);
                return type;
            }

            private static BoundMember BindReadableMember(Type type, string name, bool isStatic, AstNode node)
            {
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                     (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null)
                    return new BoundMember { Member = property, ValueType = property.PropertyType };
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return new BoundMember { Member = field, ValueType = field.FieldType };
                throw Unsupported(node, "Readable member was not found: " + type.FullName + "." + name);
            }

            private static BoundMember BindWritableMember(Type type, string name, bool isStatic, AstNode node)
            {
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                     (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetSetMethod(true) != null)
                    return new BoundMember { Member = property, ValueType = property.PropertyType };
                FieldInfo field = type.GetField(name, flags);
                if (field != null && !field.IsInitOnly && !field.IsLiteral)
                    return new BoundMember { Member = field, ValueType = field.FieldType };
                throw Unsupported(node, "Writable member was not found: " + type.FullName + "." + name);
            }

            private MethodInfo BindMethod(Type type, string name, bool isStatic, Expression[] arguments,
                IReadOnlyList<string> genericTypeNames, AstNode node)
            {
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                     (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
                Type[] explicitTypes = (genericTypeNames ?? Array.Empty<string>()).Select(value => ResolveType(value, node)).ToArray();
                var candidates = new List<Tuple<MethodInfo, int>>();
                foreach (MethodInfo candidate in type.GetMethods(flags).Where(method => method.Name == name))
                {
                    MethodInfo method = candidate;
                    if (method.IsGenericMethodDefinition)
                    {
                        if (explicitTypes.Length == 0 || method.GetGenericArguments().Length != explicitTypes.Length) continue;
                        try { method = method.MakeGenericMethod(explicitTypes); }
                        catch (ArgumentException) { continue; }
                    }
                    else if (explicitTypes.Length != 0) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Any(parameter => parameter.ParameterType.IsByRef) || parameters.Length != arguments.Length) continue;
                    int score = ConversionScore(parameters, arguments);
                    if (score >= 0) candidates.Add(Tuple.Create(method, score));
                }
                if (candidates.Count == 0)
                    throw Unsupported(node, "No statically compatible method overload was found: " + type.FullName + "." + name);
                int best = candidates.Min(item => item.Item2);
                MethodInfo[] winners = candidates.Where(item => item.Item2 == best).Select(item => item.Item1).ToArray();
                if (winners.Length != 1)
                    throw Unsupported(node, "Method overload is ambiguous for direct compilation: " + type.FullName + "." + name);
                return winners[0];
            }

            private static ConstructorInfo BindConstructor(Type type, Expression[] arguments, AstNode node)
            {
                var candidates = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(constructor => constructor.GetParameters().Length == arguments.Length &&
                                          constructor.GetParameters().All(parameter => !parameter.ParameterType.IsByRef))
                    .Select(constructor => Tuple.Create(constructor, ConversionScore(constructor.GetParameters(), arguments)))
                    .Where(item => item.Item2 >= 0)
                    .ToArray();
                if (candidates.Length == 0)
                    throw Unsupported(node, "No statically compatible constructor was found: " + type.FullName);
                int best = candidates.Min(item => item.Item2);
                ConstructorInfo[] winners = candidates.Where(item => item.Item2 == best).Select(item => item.Item1).ToArray();
                if (winners.Length != 1) throw Unsupported(node, "Constructor overload is ambiguous: " + type.FullName);
                return winners[0];
            }

            private static PropertyInfo BindIndexer(Type type, Expression[] indices, bool requireSetter, AstNode node)
            {
                if (type.IsArray)
                {
                    if (indices.Length != type.GetArrayRank())
                        throw Unsupported(node, "Array index count does not match its rank.");
                    return null;
                }
                var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(property => property.Name == "Item" && property.GetIndexParameters().Length == indices.Length &&
                                       (requireSetter ? property.GetSetMethod(true) != null : property.GetGetMethod(true) != null))
                    .Select(property => Tuple.Create(property, ConversionScore(property.GetIndexParameters(), indices)))
                    .Where(item => item.Item2 >= 0)
                    .ToArray();
                if (candidates.Length == 0) throw Unsupported(node, "No statically compatible indexer was found: " + type.FullName);
                int best = candidates.Min(item => item.Item2);
                PropertyInfo[] winners = candidates.Where(item => item.Item2 == best).Select(item => item.Item1).ToArray();
                if (winners.Length != 1) throw Unsupported(node, "Indexer overload is ambiguous: " + type.FullName);
                return winners[0];
            }

            private static int ConversionScore(ParameterInfo[] parameters, Expression[] arguments)
            {
                int score = 0;
                for (int i = 0; i < parameters.Length; i++)
                {
                    int item = ConversionScore(arguments[i], parameters[i].ParameterType);
                    if (item < 0) return -1;
                    score += item;
                }
                return score;
            }

            private static int ConversionScore(Expression source, Type target)
            {
                if (source.Type == target) return 0;
                if (IsNullConstant(source)) return !target.IsValueType || Nullable.GetUnderlyingType(target) != null ? 1 : -1;
                if (target.IsAssignableFrom(source.Type)) return 1;
                if (IsNumeric(source.Type) && IsNumeric(target)) return 2;
                if (Nullable.GetUnderlyingType(target) == source.Type) return 2;
                return -1;
            }

            private static Expression[] ConvertArguments(ParameterInfo[] parameters, Expression[] arguments, AstNode node)
            {
                return arguments.Select((argument, index) => ConvertTo(argument, parameters[index].ParameterType, node)).ToArray();
            }

            private static Expression[] ConvertIndexArguments(Type targetType, PropertyInfo indexer, Expression[] indices, AstNode node)
            {
                if (targetType.IsArray)
                    return indices.Select(value => ConvertTo(value, typeof(int), node)).ToArray();
                return ConvertArguments(indexer.GetIndexParameters(), indices, node);
            }

            private static Type InferArrayElementType(Expression[] values, AstNode node)
            {
                Type[] types = values.Where(value => !IsNullConstant(value)).Select(value => value.Type).Distinct().ToArray();
                if (types.Length == 0) throw Unsupported(node, "Implicit array element type cannot be inferred from empty or null values.");
                if (types.All(IsNumeric))
                    return types.Skip(1).Aggregate(types[0], (current, next) => PromoteBinaryType(current, next, node));
                Type candidate = types[0];
                foreach (Type type in types.Skip(1))
                {
                    if (candidate.IsAssignableFrom(type)) continue;
                    if (type.IsAssignableFrom(candidate)) { candidate = type; continue; }
                    throw Unsupported(node, "Implicit array elements do not have a common statically bindable type.");
                }
                return candidate;
            }

            private Expression CompileUnary(UnaryExpr unary)
            {
                Expression value = CompileExpression(unary.Value);
                switch (unary.Operator)
                {
                    case "!": return Expression.Not(RequireBoolean(value, unary));
                    case "+": return PromoteUnaryNumeric(value, unary);
                    case "-": return Expression.Negate(PromoteUnaryNumeric(value, unary));
                    case "~": return Expression.OnesComplement(PromoteUnaryNumeric(value, unary));
                    default: throw Unsupported(unary, "Unary operator is not supported: " + unary.Operator);
                }
            }

            private Expression CompileBinary(string op, Expression left, Expression right, AstNode node)
            {
                if (op == "&&" || op == "||")
                {
                    left = RequireBoolean(left, node);
                    right = RequireBoolean(right, node);
                    return op == "&&" ? Expression.AndAlso(left, right) : Expression.OrElse(left, right);
                }
                if (op == "+" && (left.Type == typeof(string) || right.Type == typeof(string)))
                    return Expression.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) }), Box(left), Box(right));
                if (op == "<<" || op == ">>")
                {
                    left = PromoteUnaryNumeric(left, node);
                    right = ConvertTo(PromoteUnaryNumeric(right, node), typeof(int), node);
                    return op == "<<" ? Expression.LeftShift(left, right) : Expression.RightShift(left, right);
                }
                if (IsNumeric(left.Type) && IsNumeric(right.Type))
                {
                    Type promoted = PromoteBinaryType(left.Type, right.Type, node);
                    left = ConvertTo(left, promoted, node);
                    right = ConvertTo(right, promoted, node);
                }
                else if (left.Type != right.Type)
                {
                    if (IsNullConstant(left) && !right.Type.IsValueType) left = Expression.Constant(null, right.Type);
                    else if (IsNullConstant(right) && !left.Type.IsValueType) right = Expression.Constant(null, left.Type);
                    else throw Unsupported(node, "Operator operands do not have compatible static types: " + left.Type.FullName + " and " + right.Type.FullName);
                }
                switch (op)
                {
                    case "+": return Expression.Add(left, right);
                    case "-": return Expression.Subtract(left, right);
                    case "*": return Expression.Multiply(left, right);
                    case "/": return Expression.Divide(left, right);
                    case "%": return Expression.Modulo(left, right);
                    case "&": return Expression.And(left, right);
                    case "|": return Expression.Or(left, right);
                    case "^": return Expression.ExclusiveOr(left, right);
                    case "==": return Expression.Equal(left, right);
                    case "!=": return Expression.NotEqual(left, right);
                    case "<": return Expression.LessThan(left, right);
                    case "<=": return Expression.LessThanOrEqual(left, right);
                    case ">": return Expression.GreaterThan(left, right);
                    case ">=": return Expression.GreaterThanOrEqual(left, right);
                    default: throw Unsupported(node, "Binary operator is not supported: " + op);
                }
            }

            private Expression CompileConditional(ConditionalExpr conditional)
            {
                Expression condition = RequireBoolean(CompileExpression(conditional.Condition), conditional.Condition);
                Expression whenTrue = CompileExpression(conditional.WhenTrue);
                Expression whenFalse = CompileExpression(conditional.WhenFalse);
                if (whenTrue.Type != whenFalse.Type)
                {
                    if (IsNumeric(whenTrue.Type) && IsNumeric(whenFalse.Type))
                    {
                        Type promoted = PromoteBinaryType(whenTrue.Type, whenFalse.Type, conditional);
                        whenTrue = ConvertTo(whenTrue, promoted, conditional);
                        whenFalse = ConvertTo(whenFalse, promoted, conditional);
                    }
                    else
                    {
                        whenTrue = Box(whenTrue);
                        whenFalse = Box(whenFalse);
                    }
                }
                return Expression.Condition(condition, whenTrue, whenFalse);
            }

            private Expression CompileAssignment(AssignExpr assignment)
            {
                bool requireRead = assignment.Operator != "=";
                LValue target = CompileLValue(assignment.Target, requireRead);
                Expression value = CompileExpression(assignment.Value);
                if (assignment.Operator == "??=")
                {
                    if (target.Type.IsValueType && Nullable.GetUnderlyingType(target.Type) == null)
                        throw Unsupported(assignment, "The target of ??= must be a reference or nullable value.");
                    var current = Expression.Variable(target.Type, "coalesceCurrent");
                    var fallback = Expression.Variable(target.Type, "coalesceFallback");
                    Expression hasValue = Nullable.GetUnderlyingType(target.Type) != null
                        ? (Expression)Expression.Property(current, "HasValue")
                        : Expression.NotEqual(current, Expression.Constant(null, target.Type));
                    var coalesceBody = new List<Expression>(target.Setup)
                    {
                        Expression.Assign(current, target.Read()),
                        Expression.Condition(hasValue,
                            current,
                            Expression.Block(
                                Expression.Assign(fallback, ConvertTo(value, target.Type, assignment)),
                                target.Write(fallback),
                                fallback)),
                    };
                    return Expression.Block(target.Variables.Concat(new[] { current, fallback }), coalesceBody);
                }
                if (requireRead)
                    value = CompileBinary(assignment.Operator.Substring(0, assignment.Operator.Length - 1), target.Read(), value, assignment);
                value = ConvertTo(value, target.Type, assignment);
                var assigned = Expression.Variable(target.Type, "assignedValue");
                var body = new List<Expression>(target.Setup)
                {
                    Expression.Assign(assigned, value),
                    target.Write(assigned),
                    assigned,
                };
                return Expression.Block(target.Variables.Concat(new[] { assigned }), body);
            }

            private Expression CompileIncrement(IncrementExpr increment)
            {
                LValue target = CompileLValue(increment.Target, requireRead: true);
                var old = Expression.Variable(target.Type, "oldValue");
                var next = Expression.Variable(target.Type, "nextValue");
                var body = new List<Expression>(target.Setup)
                {
                    Expression.Assign(old, target.Read()),
                    Expression.Assign(next, ConvertTo(CompileBinary("+", old,
                        Expression.Constant(increment.Delta), increment), target.Type, increment)),
                    target.Write(next),
                    increment.IsPostfix ? (Expression)old : next,
                };
                return Expression.Block(target.Variables.Concat(new[] { old, next }), body);
            }

            private sealed class LValue
            {
                public Type Type;
                public readonly List<ParameterExpression> Variables = new List<ParameterExpression>();
                public readonly List<Expression> Setup = new List<Expression>();
                public Func<Expression> Read;
                public Func<Expression, Expression> Write;
            }

            private LValue CompileLValue(Expr expression, bool requireRead)
            {
                if (expression is NameExpr name)
                {
                    Binding binding = ResolveBinding(name.Name, name);
                    return new LValue
                    {
                        Type = binding.Type,
                        Read = () => ReadBinding(binding),
                        Write = value => WriteBinding(binding, value),
                    };
                }
                if (expression is MemberExpr member)
                {
                    if (member.IsConditional) throw Unsupported(member, "Conditional member access is not assignable.");
                    Receiver receiver = ResolveReceiver(member.Target, member);
                    BoundMember writable = BindWritableMember(receiver.Type, member.Name, receiver.IsStatic, member);
                    BoundMember readable = requireRead ? BindReadableMember(receiver.Type, member.Name, receiver.IsStatic, member) : writable;
                    if (readable.ValueType != writable.ValueType || readable.Member.MetadataToken != writable.Member.MetadataToken)
                        throw Unsupported(member, "Member getter and setter do not resolve to the same property or field.");
                    _policy.EnsureMemberAllowed(writable.Member);
                    var lvalue = new LValue { Type = writable.ValueType };
                    Expression target;
                    if (receiver.IsStatic) target = Expression.Constant(null, typeof(object));
                    else
                    {
                        var temp = Expression.Variable(receiver.Instance.Type, "memberTarget");
                        lvalue.Variables.Add(temp);
                        lvalue.Setup.Add(Expression.Assign(temp, receiver.Instance));
                        target = Box(temp);
                    }
                    lvalue.Read = () => Expression.Convert(Expression.Call(ReadMemberMethod, _context,
                        Expression.Constant(readable.Member, typeof(MemberInfo)), target), lvalue.Type);
                    lvalue.Write = value => Expression.Convert(Expression.Call(WriteMemberMethod, _context,
                        Expression.Constant(writable.Member, typeof(MemberInfo)), target, Box(value)), lvalue.Type);
                    return lvalue;
                }
                if (expression is IndexExpr index)
                {
                    Expression targetExpression = CompileExpression(index.Target);
                    Expression[] indexExpressions = index.Indices.Select(CompileExpression).ToArray();
                    PropertyInfo indexer = BindIndexer(targetExpression.Type, indexExpressions, requireSetter: true, index);
                    if (requireRead && !targetExpression.Type.IsArray && indexer.GetGetMethod(true) == null)
                        throw Unsupported(index, "Compound index assignment requires a readable indexer.");
                    Type valueType = targetExpression.Type.IsArray ? targetExpression.Type.GetElementType() : indexer.PropertyType;
                    Expression[] convertedIndices = ConvertIndexArguments(targetExpression.Type, indexer, indexExpressions, index);
                    var lvalue = new LValue { Type = valueType };
                    var target = Expression.Variable(targetExpression.Type, "indexTarget");
                    lvalue.Variables.Add(target);
                    lvalue.Setup.Add(Expression.Assign(target, targetExpression));
                    var indexTemps = convertedIndices.Select((value, i) => Expression.Variable(value.Type, "index" + i)).ToArray();
                    lvalue.Variables.AddRange(indexTemps);
                    for (int i = 0; i < indexTemps.Length; i++) lvalue.Setup.Add(Expression.Assign(indexTemps[i], convertedIndices[i]));
                    Func<Expression> boxedIndices = () => Expression.NewArrayInit(typeof(object), indexTemps.Select(Box));
                    lvalue.Read = () => Expression.Convert(Expression.Call(ReadIndexMethod, _context, Box(target),
                        Expression.Constant(indexer, typeof(PropertyInfo)), boxedIndices()), valueType);
                    lvalue.Write = value => Expression.Convert(Expression.Call(WriteIndexMethod, _context, Box(target),
                        Expression.Constant(indexer, typeof(PropertyInfo)), boxedIndices(), Box(value)), valueType);
                    return lvalue;
                }
                throw Unsupported(expression, "Expression is not an assignable direct-compiler target: " + expression.GetType().Name);
            }

            private Binding ResolveBinding(string name, AstNode node)
            {
                if (TryResolveBinding(name, out var binding)) return binding;
                throw Unsupported(node, "Name is not a statically typed local or input variable: " + name);
            }

            private bool TryResolveBinding(string name, out Binding binding)
            {
                foreach (var scope in _scopes)
                    if (scope.TryGetValue(name, out binding)) return true;
                if (_externalTypes.TryGetValue(name, out var type))
                {
                    binding = new Binding { Name = name, Type = type, Local = null, Persisted = true };
                    return true;
                }
                binding = null;
                return false;
            }

            private Expression ReadBinding(Binding binding)
            {
                return binding.Local != null
                    ? (Expression)binding.Local
                    : Expression.Convert(Expression.Call(_context, GetVariableMethod, Expression.Constant(binding.Name)), binding.Type);
            }

            private Expression WriteBinding(Binding binding, Expression value)
            {
                var temp = Expression.Variable(binding.Type, "assigned");
                var expressions = new List<Expression> { Expression.Assign(temp, value) };
                if (binding.Local != null) expressions.Add(Expression.Assign(binding.Local, temp));
                if (binding.Persisted)
                    expressions.Add(Expression.Call(_context, SetVariableMethod, Expression.Constant(binding.Name), Box(temp)));
                expressions.Add(temp);
                return Expression.Block(new[] { temp }, expressions);
            }

            private static Expression Box(Expression expression)
            {
                if (expression.Type == typeof(void)) return Expression.Block(expression, Expression.Constant(null, typeof(object)));
                return expression.Type == typeof(object) ? expression : Expression.Convert(expression, typeof(object));
            }

            private static Expression RequireBoolean(Expression expression, AstNode node)
            {
                if (expression.Type != typeof(bool)) throw Unsupported(node, "A directly compiled condition must be System.Boolean.");
                return expression;
            }

            private static Expression PromoteUnaryNumeric(Expression expression, AstNode node)
            {
                if (!IsNumeric(expression.Type)) throw Unsupported(node, "Unary numeric operator requires a numeric operand.");
                Type type = expression.Type;
                if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(char))
                    return Expression.Convert(expression, typeof(int));
                return expression;
            }

            private static Type PromoteBinaryType(Type left, Type right, AstNode node)
            {
                if (left == typeof(decimal) || right == typeof(decimal))
                {
                    if (left == typeof(float) || left == typeof(double) || right == typeof(float) || right == typeof(double))
                        throw Unsupported(node, "decimal cannot be combined implicitly with float or double.");
                    return typeof(decimal);
                }
                if (left == typeof(double) || right == typeof(double)) return typeof(double);
                if (left == typeof(float) || right == typeof(float)) return typeof(float);
                if (left == typeof(ulong) || right == typeof(ulong))
                {
                    Type other = left == typeof(ulong) ? right : left;
                    if (other == typeof(sbyte) || other == typeof(short) || other == typeof(int) || other == typeof(long))
                        throw Unsupported(node, "ulong cannot be combined implicitly with a signed integral operand.");
                    return typeof(ulong);
                }
                if (left == typeof(long) || right == typeof(long)) return typeof(long);
                if (left == typeof(uint) || right == typeof(uint))
                {
                    Type other = left == typeof(uint) ? right : left;
                    return other == typeof(sbyte) || other == typeof(short) || other == typeof(int) ? typeof(long) : typeof(uint);
                }
                return typeof(int);
            }

            private static Expression ConvertTo(Expression expression, Type target, AstNode node)
            {
                if (expression.Type == target) return expression;
                if (IsNullConstant(expression) && (!target.IsValueType || Nullable.GetUnderlyingType(target) != null))
                    return Expression.Constant(null, target);
                try { return Expression.Convert(expression, target); }
                catch (InvalidOperationException)
                { throw Unsupported(node, "Cannot convert " + expression.Type.FullName + " to " + target.FullName + " in the direct compiler."); }
            }

            private static bool IsNullConstant(Expression expression)
            {
                return expression is ConstantExpression constant && constant.Value == null;
            }

            private static bool IsNumeric(Type type)
            {
                type = Nullable.GetUnderlyingType(type) ?? type;
                return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                       type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                       type == typeof(char) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
            }

            private static ExecutionContractException Unsupported(AstNode node, string message)
            {
                var detail = new Dictionary<string, object>
                {
                    { "stage", "bind" },
                    { "node", node == null ? "" : node.GetType().Name },
                };
                if (node?.Span != null) detail["sourceSpan"] = node.Span.Clone();
                return new ExecutionContractException("CSHARP_COMPILED_UNSUPPORTED_NODE", message, detail);
            }
        }
    }
}
