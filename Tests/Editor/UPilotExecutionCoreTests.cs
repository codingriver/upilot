using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public interface IExecutionEmitFixture
    {
        int Increment(int value);
    }

    public static class ExecutionReflectionFixture
    {
        public static string Choose(int value) => "int";
        public static string Choose(long value) => "long";
        public static string Numeric(long value) => "long";
        public static string Numeric(double value) => "double";
        public static string Narrow(byte value) => "byte";
        public static string NullChoice(object value) => "object";
        public static string NullChoice(string value) => "string";
        public static string NullAmbiguous(IComparable value) => "comparable";
        public static string NullAmbiguous(IFormattable value) => "formattable";
        public static string Shape(int value) => "fixed";
        public static string Shape(params int[] values) => "params";
        public static string OptionalShape(int value) => "required";
        public static string OptionalShape(int value, int optional = 0) => "optional";
        public static int Named(int first, int second = 4) => first + second;
        public static void RefOut(ref int value, out string text) { value += 2; text = value.ToString(); }
        public static T Identity<T>(T value) => value;
        public static Task<int> AsyncValue() => Task.FromResult(9);
        public static ValueTask<int> ValueTaskValue() => new ValueTask<int>(11);
        public static string Pair<TFirst, TSecond>(TFirst first, TSecond second) => typeof(TFirst).Name + ":" + typeof(TSecond).Name;
        public static int ArrayLength<T>(T[] values) => values.Length;
        public static string NullableName<T>(T? value) where T : struct => typeof(T).Name;
        public static string StructOnly<T>(T value) where T : struct => typeof(T).Name;
        public static string GenericAmbiguous<T>(IList<T> value) => "list";
        public static string GenericAmbiguous<T>(ICollection<T> value) => "collection";
        public static string NoInference<T>() => typeof(T).Name;
        public static T Apply<T>(T value, Func<T, T> transform) => transform(value);
        public static T Create<T>(Func<T> factory) => factory();
        public static int ParamsCount<T>(params T[] values) => values.Length;
        public static string FromEnumerable<T>(IEnumerable<T> values) => typeof(T).Name;
        public static async Task<TOut> InvokeAsyncDelegate<TIn, TOut>(Func<TIn, Task<TOut>> callback, TIn value) => await callback(value);
        public static void Exit() { }
        public static void ThrowNow() => throw new InvalidOperationException("fixture runtime failure");
        public static int InvocationCount { get; set; }
        public static int CountInvocation() => ++InvocationCount;
        public static ExecutionEventFixture SharedEventSource { get; } = new ExecutionEventFixture();
        public static int EventObserved { get; set; }
        public static void ObserveEvent(int value) { EventObserved += value; }
        public static void RaiseSharedEvent(int value) { SharedEventSource.Raise(value); }
        public static event Action<int> StaticChanged;
        public static void RaiseStaticEvent(int value) { StaticChanged?.Invoke(value); }
    }

    public class ExecutionInheritedStaticBaseFixture
    {
        public static int SharedValue => 17;
        public static string SharedMethod() => "inherited";
    }

    public sealed class ExecutionInheritedStaticDerivedFixture : ExecutionInheritedStaticBaseFixture { }

    public static class ExecutionGenericStaticFixture<T>
    {
        public static string TypeName => typeof(T).Name;
    }

    public static class ExecutionPropertyBudgetFixture
    {
        public static int GetterCallCount { get; set; }
        public static int First => Count(1);
        public static int Second => Count(2);
        public static int Third => Count(3);
        public static int Fourth => Count(4);
        public static int Slow
        {
            get
            {
                GetterCallCount++;
                Thread.Sleep(100);
                return 5;
            }
        }
        private static int Count(int value) { GetterCallCount++; return value; }
    }

    public struct ExecutionConversionSource
    {
        public int Value;
        public static int ConversionCount;
        public static implicit operator ExecutionConversionTarget(ExecutionConversionSource source)
        {
            ConversionCount++;
            return new ExecutionConversionTarget { Value = source.Value };
        }
    }

    public struct ExecutionConversionTarget { public int Value; }

    public static class ExecutionConversionFixture
    {
        public static int InvocationCount;
        public static int Accept(ExecutionConversionTarget value) { InvocationCount++; return value.Value; }
        public static string PreferIdentity(object value) { InvocationCount++; return "object"; }
        public static string PreferIdentity(ExecutionConversionTarget value) { InvocationCount++; return "converted"; }
        public static object ProduceUnsupportedResult() { InvocationCount++; return new ExecutionDisposableFixture(); }
    }

    public static class ExecutionNestedTypeFixture
    {
        public enum Mode { First, Second }
    }

    public static class ExecutionThrowingGetterFixture
    {
        public static int GetterCallCount;
        public static int Value
        {
            get
            {
                GetterCallCount++;
                throw new InvalidOperationException("getter failed");
            }
        }
    }

    public sealed class ExecutionEventFixture
    {
        public event Action<int> Changed;
        public void Raise(int value) { Changed?.Invoke(value); }
    }

    public sealed class ExecutionAssignmentFixture
    {
        public int Value;
        public ExecutionAssignmentFixture Child;
        public readonly List<int> Values = new List<int> { 4 };
        public int Add(int value) { Value += value; return Value; }
    }

    public static class ExecutionAssignmentFixtureSource
    {
        public static ExecutionAssignmentFixture Target;
        public static int ReceiverCalls;
        public static int IndexCalls;
        public static int ArgumentCalls;

        public static ExecutionAssignmentFixture GetTarget() { ReceiverCalls++; return Target; }
        public static int NextIndex() { IndexCalls++; return 0; }
        public static int CountArgument(int value) { ArgumentCalls++; return value; }
        public static void Reset(ExecutionAssignmentFixture target)
        {
            Target = target;
            ReceiverCalls = 0;
            IndexCalls = 0;
            ArgumentCalls = 0;
        }
    }

    public sealed class ExecutionUnityEventFixture : ScriptableObject
    {
        public event Action<int> Changed;
        public void Raise(int value) { Changed?.Invoke(value); }
    }

    public sealed class ExecutionFaultyEventFixture
    {
        private Action<int> _changed;
        public event Action<int> Changed
        {
            add { _changed += value; }
            remove { throw new InvalidOperationException("unsubscribe failed"); }
        }
        public void Raise(int value) { _changed?.Invoke(value); }
    }

    public sealed class ExecutionDisposableFixture : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; }
    }

    public sealed class UPilotExecutionCoreTests
    {
        [Test]
        public void ExecutionCapabilitiesAdvertiseStructuredRecoveryAndCallbackIsolation()
        {
            var capabilities = new CodingRiver.UPilot.ExecutionCapabilityPayload();

            Assert.That(capabilities.structuredSourceSpans, Is.True);
            Assert.That(capabilities.directCompiledBackendSupported, Is.True);
            Assert.That(capabilities.directCompiledEmitBodiesSupported, Is.True);
            Assert.That(capabilities.structuredErrorDetails, Is.True);
            Assert.That(capabilities.legacyJsonErrorDetails, Is.True);
            Assert.That(capabilities.callbackPolicySupported, Is.True);
            Assert.That(capabilities.callbackSessionIsolation, Is.True);
            Assert.That(capabilities.languageProfile, Is.EqualTo("upilot-csharp-subset-v2"));
            Assert.That(capabilities.exceptionHandling, Is.True);
            Assert.That(capabilities.boundedFinallyCleanup, Is.True);
            Assert.That(capabilities.lexicalClosures, Is.True);
            Assert.That(capabilities.blockLambdas, Is.True);
            Assert.That(capabilities.asyncLambdas, Is.True);
            Assert.That(capabilities.asyncVoidSupported, Is.False);
            Assert.That(capabilities.nonBlockingAwait, Is.True);
            Assert.That(capabilities.genericInferenceLevel, Is.EqualTo("practical-v2"));
            Assert.That(capabilities.implicitArrays, Is.True);
            Assert.That(capabilities.multidimensionalArrays, Is.True);
            Assert.That(capabilities.maxArrayRank, Is.EqualTo(4));
            CollectionAssert.AreEqual(new[] { "isolate", "propagate" }, capabilities.callbackExceptionModes);
            CollectionAssert.AreEqual(
                new[] { "close", "ttl", "playModeInvalidation" },
                capabilities.eventSubscriptionCleanup);
        }

        [Test]
        public void MethodBinderUsesDeclaredTypeNamedOptionalGenericAndRefOut()
        {
            var chosen = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Choose", true,
                new[] { new ExecutionValue { Value = 3, DeclaredType = typeof(int) } });
            Assert.That(chosen.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(int)));

            var named = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Named", true,
                new[] { new ExecutionValue { Name = "first", Value = 3 } });
            Assert.That(named.Method.Invoke(null, named.Arguments), Is.EqualTo(7));

            var generic = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Identity", true,
                new[] { new ExecutionValue { Value = "ok" } }, genericTypeArguments: new[] { typeof(string) });
            Assert.That(generic.Method.Invoke(null, generic.Arguments), Is.EqualTo("ok"));

            var refOut = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "RefOut", true,
                new[]
                {
                    new ExecutionValue { Name = "value", Direction = "ref", Value = 5 },
                    new ExecutionValue { Name = "text", Direction = "out", Value = null },
                });
            refOut.Method.Invoke(null, refOut.Arguments);
            Assert.That(refOut.Arguments[0], Is.EqualTo(7));
            Assert.That(refOut.Arguments[1], Is.EqualTo("7"));

            var nullChoice = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "NullChoice", true,
                new[] { new ExecutionValue { Value = null } });
            Assert.That(nullChoice.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(string)));

            var ambiguous = Assert.Throws<ExecutionContractException>(() =>
                MethodBinder.Bind(typeof(ExecutionReflectionFixture), "NullAmbiguous", true,
                    new[] { new ExecutionValue { Value = null } }));
            Assert.That(ambiguous.Code, Is.EqualTo("REFLECTION_BIND_AMBIGUOUS"));
            Assert.That(ambiguous.Detail.ContainsKey("candidates"), Is.True);
            var candidates = ambiguous.Detail["candidates"] as string[];
            Assert.That(candidates, Is.Not.Null);
            Assert.That(candidates, Has.Length.EqualTo(2));
            Assert.That(candidates, Has.Some.Contains("System.IComparable"));
            Assert.That(candidates, Has.Some.Contains("System.IFormattable"));
        }

        [Test]
        public void MethodBinderUsesOnlyDocumentedImplicitNumericConversions()
        {
            var widened = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Numeric", true,
                new[] { new ExecutionValue { Value = 3 } });
            Assert.That(widened.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(long)));
            var narrow = Assert.Throws<ExecutionContractException>(() => MethodBinder.Bind(
                typeof(ExecutionReflectionFixture), "Narrow", true, new[] { new ExecutionValue { Value = 3 } }));
            Assert.That(narrow.Code, Is.EqualTo("REFLECTION_BIND_FAILED"));
            var text = Assert.Throws<ExecutionContractException>(() => MethodBinder.Bind(
                typeof(ExecutionReflectionFixture), "Choose", true, new[] { new ExecutionValue { Value = "3" } }));
            Assert.That(text.Code, Is.EqualTo("REFLECTION_BIND_FAILED"));
        }

        [Test]
        public void MethodBinderPrefersNonExpandedAndFewerOptionalParameters()
        {
            var fixedShape = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Shape", true,
                new[] { new ExecutionValue { Value = 3 } });
            Assert.That(fixedShape.Method.GetParameters().Length, Is.EqualTo(1));
            Assert.That(fixedShape.Method.GetParameters()[0].GetCustomAttributes(typeof(ParamArrayAttribute), false), Is.Empty);

            var required = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "OptionalShape", true,
                new[] { new ExecutionValue { Value = 3 } });
            Assert.That(required.Method.GetParameters().Length, Is.EqualTo(1));
        }

        [Test]
        public void MethodBinderProbesUserConversionsWithoutExecutingAndConvertsOnlySelectedCandidate()
        {
            ExecutionConversionSource.ConversionCount = 0;
            ExecutionConversionFixture.InvocationCount = 0;
            var source = new ExecutionConversionSource { Value = 23 };

            var converted = MethodBinder.Bind(typeof(ExecutionConversionFixture), "Accept", true,
                new[] { new ExecutionValue { Value = source } });
            Assert.That(ExecutionConversionSource.ConversionCount, Is.EqualTo(1));
            Assert.That(converted.Method.Invoke(null, converted.Arguments), Is.EqualTo(23));
            Assert.That(ExecutionConversionFixture.InvocationCount, Is.EqualTo(1));

            ExecutionConversionSource.ConversionCount = 0;
            ExecutionConversionFixture.InvocationCount = 0;
            var identity = MethodBinder.Bind(typeof(ExecutionConversionFixture), "PreferIdentity", true,
                new[] { new ExecutionValue { Value = source } });
            Assert.That(identity.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(object)));
            Assert.That(ExecutionConversionSource.ConversionCount, Is.EqualTo(0));
            Assert.That(identity.Method.Invoke(null, identity.Arguments), Is.EqualTo("object"));
            Assert.That(ExecutionConversionFixture.InvocationCount, Is.EqualTo(1));
        }

        [Test]
        public void InterpreterConversionUsesTheSameBoundaryAsTargetInvocation()
        {
            ExecutionConversionSource.ConversionCount = 0;
            ExecutionConversionFixture.InvocationCount = 0;
            var source = new ExecutionConversionSource { Value = 23 };
            using (var cancellation = new CancellationTokenSource())
            {
                int scheduled = 0;
                var context = new CSharpEvaluationContext(
                    new Dictionary<string, object> { { "source", source } },
                    invocationScheduler: action =>
                    {
                        scheduled++;
                        object result = action();
                        cancellation.Cancel();
                        return result;
                    },
                    cancellationToken: cancellation.Token,
                    budget: new ExecutionBudget(cancellationToken: cancellation.Token));

                var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                    "CodingRiver.UPilot.Tests.ExecutionConversionFixture.Accept(source)", "expression", context));

                Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
                Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
                Assert.That(error.Detail.ContainsKey("lastCompletedSpan"), Is.True);
                Assert.That(scheduled, Is.EqualTo(1));
                Assert.That(ExecutionConversionSource.ConversionCount, Is.EqualTo(1));
                Assert.That(ExecutionConversionFixture.InvocationCount, Is.EqualTo(0));
                Assert.That(context.Diagnostics.MethodCallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void AwaitableAdapterSupportsTaskOfT()
        {
            var result = AwaitableAdapter.AwaitAsync(ExecutionReflectionFixture.AsyncValue(), "auto", 1000).GetAwaiter().GetResult();
            Assert.That(result.WasAwaitable, Is.True);
            Assert.That(result.Status, Is.EqualTo("Completed"));
            Assert.That(result.Value, Is.EqualTo(9));
            var valueTask = AwaitableAdapter.AwaitAsync(ExecutionReflectionFixture.ValueTaskValue(), "auto", 1000).GetAwaiter().GetResult();
            Assert.That(valueTask.Value, Is.EqualTo(11));
        }

        [Test]
        public void InterpreterSupportsStatementsLoopsAndPersistentVariables()
        {
            var context = new CSharpEvaluationContext(new Dictionary<string, object>());
            var result = CSharpSubsetEngine.Evaluate("var total = 0; for (var i = 0; i < 4; i++) { total += i; } return total;", "statements", context);
            Assert.That(result.Value, Is.EqualTo(6));
            Assert.That(result.Variables["total"], Is.EqualTo(6));
            Assert.That(result.Budget.LoopIterations, Is.EqualTo(4));
        }

        [Test]
        public void InterpreterSupportsExplicitAndInferredGenericCallsAndClosedTypes()
        {
            var explicitResult = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Identity<int>(3)", "expression", new CSharpEvaluationContext());
            Assert.That(explicitResult.Value, Is.EqualTo(3));

            var inferred = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Pair(3, \"ok\")", "expression", new CSharpEvaluationContext());
            Assert.That(inferred.Value, Is.EqualTo("Int32:String"));

            var closedType = CSharpSubsetEngine.Evaluate(
                "var values = new System.Collections.Generic.List<int>(); values.Add(4); return values[0];",
                "statements", new CSharpEvaluationContext());
            Assert.That(closedType.Value, Is.EqualTo(4));
        }

        [Test]
        public void TypeValuesUseInstanceSemanticsWhileSyntaxPathsRemainStatic()
        {
            var instance = CSharpSubsetEngine.Evaluate(
                "var captured = System.String; return captured.ToString();", "statements", new CSharpEvaluationContext());
            Assert.That(instance.Value, Is.EqualTo("System.String"));
            var staticResult = CSharpSubsetEngine.Evaluate(
                "System.String.IsNullOrEmpty(\"\")", "expression", new CSharpEvaluationContext());
            Assert.That(staticResult.Value, Is.True);
        }

        [Test]
        public void TypeInstancesSupportGetTypeMetadataWithoutBecomingStaticTargets()
        {
            var fullName = CSharpSubsetEngine.Evaluate(
                "\"x\".GetType().FullName", "expression", new CSharpEvaluationContext());
            Assert.That(fullName.Value, Is.EqualTo("System.String"));
            Assert.That(fullName.Diagnostics.MethodCallCount, Is.EqualTo(1));
            Assert.That(fullName.Diagnostics.GetterCallCount, Is.EqualTo(1));

            var field = CSharpSubsetEngine.Evaluate(
                "var captured = System.String; return captured.GetField(\"Empty\").Name;",
                "statements", new CSharpEvaluationContext());
            Assert.That(field.Value, Is.EqualTo("Empty"));
            Assert.That(field.Diagnostics.MethodCallCount, Is.EqualTo(1));
            Assert.That(field.Diagnostics.GetterCallCount, Is.EqualTo(1));

            var nestedEnum = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionNestedTypeFixture.Mode.Second",
                "expression", new CSharpEvaluationContext());
            Assert.That(nestedEnum.Value, Is.EqualTo(ExecutionNestedTypeFixture.Mode.Second));
        }

        [Test]
        public void InterpreterResolvesInheritedStaticMembersWithoutTreatingTheTypeAsAnInstance()
        {
            var property = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionInheritedStaticDerivedFixture.SharedValue",
                "expression", new CSharpEvaluationContext());
            Assert.That(property.Value, Is.EqualTo(17));
            Assert.That(property.Diagnostics.GetterCallCount, Is.EqualTo(1));

            var method = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionInheritedStaticDerivedFixture.SharedMethod()",
                "expression", new CSharpEvaluationContext());
            Assert.That(method.Value, Is.EqualTo("inherited"));
            Assert.That(method.Diagnostics.MethodCallCount, Is.EqualTo(1));
        }

        [Test]
        public void InterpreterResolvesClosedGenericTypePathsBeforeFollowingMembers()
        {
            var result = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionGenericStaticFixture<int>.TypeName",
                "expression", new CSharpEvaluationContext());
            Assert.That(result.Value, Is.EqualTo("Int32"));
            Assert.That(result.Diagnostics.GetterCallCount, Is.EqualTo(1));
        }

        [Test]
        public void TypeResolverInvalidatesNegativeCacheAfterSameDomainAssemblyLoad()
        {
            var typeName = "ExecutionResolverProbe" + Guid.NewGuid().ToString("N");
            Assert.That(ExecutionTypeResolver.Resolve(typeName), Is.Null);
            Assert.That(ExecutionTypeResolver.Resolve(typeName), Is.Null);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("UPilotExecutionResolverProbe" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("Probe");
            var created = module.DefineType(typeName, TypeAttributes.Public).CreateType();

            Assert.That(ExecutionTypeResolver.Resolve(typeName), Is.EqualTo(created));
        }

        [Test]
        public void TypeResolverColdAndHotPropertyPathsStayWithinDefaultBudgetWithoutReplayingGetters()
        {
            const string expression =
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture.First + " +
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture.Second + " +
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture.Third + " +
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture.Fourth";
            ExecutionPropertyBudgetFixture.GetterCallCount = 0;

            for (var index = 0; index < 10; index++)
            {
                ClearTypeResolverCache();
                var cold = CSharpSubsetEngine.Evaluate(expression, "expression", new CSharpEvaluationContext());
                Assert.That(cold.Value, Is.EqualTo(10));
                Assert.That(cold.Diagnostics.GetterCallCount, Is.EqualTo(4));
                Assert.That(cold.Budget.ElapsedMs, Is.LessThan(3000));
            }

            ClearTypeResolverCache();
            for (var index = 0; index < 10; index++)
            {
                var hot = CSharpSubsetEngine.Evaluate(expression, "expression", new CSharpEvaluationContext());
                Assert.That(hot.Value, Is.EqualTo(10));
                Assert.That(hot.Diagnostics.GetterCallCount, Is.EqualTo(4));
                Assert.That(hot.Budget.ElapsedMs, Is.LessThan(3000));
            }

            Assert.That(ExecutionPropertyBudgetFixture.GetterCallCount, Is.EqualTo(80));
        }

        [Test]
        public void SlowGetterCrossesBudgetAfterOneInvocationWithoutReplay()
        {
            Assert.That(ExecutionTypeResolver.Resolve(
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture"),
                Is.EqualTo(typeof(ExecutionPropertyBudgetFixture)));
            ExecutionPropertyBudgetFixture.GetterCallCount = 0;
            var context = new CSharpEvaluationContext(budget: new ExecutionBudget(timeoutMs: 50));

            var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionPropertyBudgetFixture.Slow", "expression", context));

            Assert.That(error.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(error.Detail["metric"], Is.EqualTo("wallClockMs"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(error.Detail.ContainsKey("lastCompletedSpan"), Is.True);
            Assert.That(ExecutionPropertyBudgetFixture.GetterCallCount, Is.EqualTo(1));
            Assert.That(context.Diagnostics.GetterCallCount, Is.EqualTo(1));
        }

        [Test]
        public void ThrowingGetterIsInvokedOnceAndIsNotRetriedThroughAnotherBindingPath()
        {
            ExecutionThrowingGetterFixture.GetterCallCount = 0;
            var context = new CSharpEvaluationContext();

            var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionThrowingGetterFixture.Value", "expression", context));

            Assert.That(error.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(ExecutionThrowingGetterFixture.GetterCallCount, Is.EqualTo(1));
            Assert.That(context.Diagnostics.GetterCallCount, Is.EqualTo(1));
        }

        [Test]
        public void EncodingFailureAfterInvocationDoesNotReplayTheTarget()
        {
            ExecutionConversionFixture.InvocationCount = 0;
            var evaluation = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionConversionFixture.ProduceUnsupportedResult()",
                "expression", new CSharpEvaluationContext());
            Assert.That(ExecutionConversionFixture.InvocationCount, Is.EqualTo(1));

            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            var encode = typeof(CodingRiver.UPilot.UPilotExecutionService).GetMethod(
                "EncodeResult", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(encode, Is.Not.Null);
            var invocation = Assert.Throws<TargetInvocationException>(() =>
                encode.Invoke(service, new[] { evaluation.Value, "", "inline", (object)1048576 }));
            Assert.That(invocation.InnerException, Is.TypeOf<ExecutionContractException>());
            Assert.That(((ExecutionContractException)invocation.InnerException).Code, Is.EqualTo("RESULT_NOT_INLINEABLE"));
            Assert.That(ExecutionConversionFixture.InvocationCount, Is.EqualTo(1));
        }

        [Test]
        public void DecodeArgumentsRejectsMultidimensionalArraysBeforeConversion()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);

            var error = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.Int32[,]\",\"items\":[]}}]}", ""));

            Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
            Assert.That(error.Message, Does.Contain("one-dimensional array type"));
        }

        [Test]
        public void PrimitiveTypedArraysRejectNonLiteralAndNullElementsBeforeDecode()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            var invalidItems = new[]
            {
                "{\"kind\":\"handle\",\"handle\":\"h.current\"}",
                "{\"kind\":\"type\",\"typeName\":\"System.Int32\"}",
                "{\"kind\":\"unityobject\",\"instanceId\":1}",
                "{\"kind\":\"array\",\"typeName\":\"System.Int32[]\",\"items\":[]}",
                "{\"kind\":\"null\"}",
                "{\"kind\":\"literal\",\"valueJson\":\"null\"}",
            };

            foreach (string item in invalidItems)
            {
                int beforeUserCode = 0;
                var error = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                    "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.Int32[]\",\"items\":["
                    + item + "]}}]}", "", () => beforeUserCode++));
                Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
                Assert.That(beforeUserCode, Is.Zero);
            }

            var strings = service.DecodeArguments(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.String[]\",\"items\":[{\"kind\":\"null\"}]}}]}", "");
            Assert.That(((string[])strings[0].Value)[0], Is.Null);
        }

        [Test]
        public void PrimitiveTypedArraysRespectTheArrayElementBudgetBeforeDecode()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            int beforeUserCode = 0;

            var error = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.Int32[]\",\"items\":[{\"kind\":\"literal\",\"valueJson\":\"1\"},{\"kind\":\"literal\",\"valueJson\":\"2\"}]}}]}",
                "", () => beforeUserCode++, maxArrayElements: 1));

            Assert.That(error.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(error.Detail["metric"], Is.EqualTo("arrayElements"));
            Assert.That(error.Detail["limit"], Is.EqualTo(1));
            Assert.That(error.Detail["actual"], Is.EqualTo(2));
            Assert.That(beforeUserCode, Is.Zero);
        }

        [Test]
        public void OversizedInlineArraysStopAtTheUtf8Limit()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            var error = Assert.Throws<ExecutionContractException>(() => service.EncodeResult(
                Enumerable.Repeat("x", 2048).ToArray(), "", "inline", 1024));

            Assert.That(error.Code, Is.EqualTo("RESULT_TOO_LARGE"));
            Assert.That(error.Detail["actualBytes"], Is.GreaterThan(1024));
            Assert.That(error.Detail["actualBytes"], Is.LessThan(2048), "encoding must stop at the bounded UTF-8 prefix");
            Assert.That(error.Detail["limitBytes"], Is.EqualTo(1024));
        }

        [Test]
        public void TypedStringLiteralsRoundTripEveryJsonControlCharacter()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            string value = new string(Enumerable.Range(0, 32).Select(index => (char)index).ToArray()) + "\"\\";

            var encoded = service.EncodeResult(value, "", "inline");

            Assert.That(encoded.valueJson, Does.Contain("\\u0000"));
            Assert.That(encoded.valueJson, Does.Contain("\\b"));
            Assert.That(encoded.valueJson, Does.Contain("\\f"));
            Assert.That(encoded.valueJson, Does.Contain("\\n"));
            Assert.That(encoded.valueJson, Does.Contain("\\r"));
            Assert.That(encoded.valueJson, Does.Contain("\\t"));
            Assert.That(encoded.valueJson.Any(character => character < 0x20), Is.False);

            var decoded = service.DecodeArguments(CreateLiteralArgumentJson("System.String", encoded.valueJson), "");
            Assert.That(decoded[0].Value, Is.EqualTo(value));
        }

        [Test]
        public void TypedStringLiteralsDecodeUnicodeEscapesIncludingSurrogatePairs()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            const string escapedLiteral = "\"\\u0041\\uD83D\\uDE00\\b\\f\"";

            var decoded = service.DecodeArguments(CreateLiteralArgumentJson("System.String", escapedLiteral), "");

            Assert.That(decoded[0].Value, Is.EqualTo("A" + char.ConvertFromUtf32(0x1F600) + "\b\f"));
        }

        [Test]
        public void TypedStringLiteralsRejectMalformedEscapesAndRawControlCharacters()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            var invalidLiterals = new[]
            {
                "\"\\x\"",
                string.Concat("\"raw", '\u0001', "\""),
                "'not json'",
            };

            foreach (string invalidLiteral in invalidLiterals)
            {
                var error = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                    CreateLiteralArgumentJson("System.String", invalidLiteral), ""));
                Assert.That(error.Code, Is.EqualTo("TYPED_VALUE_DECODE_FAILED"));
            }
        }

        [Test]
        public void TypedStringArraysDecodeJsonLiteralsAndEncodeValidControlEscapes()
        {
            var service = new CodingRiver.UPilot.UPilotExecutionService(null);
            string argumentsJson = JsonUtility.ToJson(new ExecutionArgumentsEnvelope
            {
                items = new[]
                {
                    new ExecutionArgumentSpec
                    {
                        value = new TypedValueSpec
                        {
                            kind = "array",
                            typeName = "System.String[]",
                            items = new[]
                            {
                                new TypedValueSpec { kind = "literal", valueJson = "\"\\u0041\\b\\f\"" },
                                new TypedValueSpec { kind = "null" },
                                new TypedValueSpec { kind = "literal", valueJson = "\"\\uD83D\\uDE00\\u0000\"" },
                            },
                        },
                    },
                },
            });

            var decoded = (string[])service.DecodeArguments(argumentsJson, "")[0].Value;
            CollectionAssert.AreEqual(new[] { "A\b\f", null, char.ConvertFromUtf32(0x1F600) + "\0" }, decoded);

            var encoded = service.EncodeResult(decoded, "", "inline");
            Assert.That(encoded.kind, Is.EqualTo("array"));
            Assert.That(encoded.valueJson, Does.Contain("\\b"));
            Assert.That(encoded.valueJson, Does.Contain("\\f"));
            Assert.That(encoded.valueJson, Does.Contain("\\u0000"));
            Assert.That(encoded.valueJson.IndexOfAny(new[] { '\0', '\b', '\f', '\n', '\r', '\t' }), Is.EqualTo(-1));
        }

        private static string CreateLiteralArgumentJson(string typeName, string valueJson)
        {
            return JsonUtility.ToJson(new ExecutionArgumentsEnvelope
            {
                items = new[]
                {
                    new ExecutionArgumentSpec
                    {
                        value = new TypedValueSpec { kind = "literal", typeName = typeName, valueJson = valueJson },
                    },
                },
            });
        }

        private static void ClearTypeResolverCache()
        {
            var method = typeof(ExecutionTypeResolver).GetMethod(
                "ClearCacheForTests", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, null);
        }

        [Test]
        public void InterpreterReportsValueFreeExecutionDiagnosticsAndRejectsUnresolvedResults()
        {
            var getter = CSharpSubsetEngine.Evaluate("\"x\".Length", "expression", new CSharpEvaluationContext());
            Assert.That(getter.Value, Is.EqualTo(1));
            Assert.That(getter.Diagnostics.GetterCallCount, Is.EqualTo(1));
            Assert.That(getter.Diagnostics.ResolveMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(getter.Diagnostics.InvokeMs, Is.GreaterThanOrEqualTo(0));

            var method = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Identity<int>(3)", "expression", new CSharpEvaluationContext());
            Assert.That(method.Value, Is.EqualTo(3));
            Assert.That(method.Diagnostics.MethodCallCount, Is.EqualTo(1));
            Assert.That(method.Diagnostics.BindMs, Is.GreaterThanOrEqualTo(0));

            var unresolved = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "roots.Count", "expression", new CSharpEvaluationContext()));
            Assert.That(unresolved.Code, Is.EqualTo("CSHARP_BIND_ERROR"));
        }

        [Test]
        public void ExecutionDiagnosticsBoundCompletedBoundariesAndCountDropsExactly()
        {
            var context = new CSharpEvaluationContext();
            var result = CSharpSubsetEngine.Evaluate(
                "var total = 0; for (var i = 0; i < 70; i++) { total += \"x\".Length; } return total;",
                "statements", context);

            Assert.That(result.Value, Is.EqualTo(70));
            Assert.That(result.Diagnostics.GetterCallCount, Is.EqualTo(70));
            Assert.That(result.Diagnostics.CompletedBoundaries, Has.Length.EqualTo(64));
            Assert.That(result.Diagnostics.CompletedBoundaries, Has.All.EqualTo("invoke.completed"));
            Assert.That(result.Diagnostics.DroppedDiagnosticCount, Is.EqualTo(6));

            var map = typeof(CodingRiver.UPilot.UPilotExecutionService).GetMethod(
                "ToExecutionDiagnostics", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(map, Is.Not.Null);
            var payload = (CodingRiver.UPilot.CSharpExecutionDiagnosticsPayload)map.Invoke(null, new object[] { result.Diagnostics });
            Assert.That(payload.completedBoundaries, Is.EqualTo(result.Diagnostics.CompletedBoundaries));
            Assert.That(payload.completedBoundaries, Has.Length.EqualTo(64));
            Assert.That(payload.droppedDiagnosticCount, Is.EqualTo(6));
        }

        [Test]
        public void EvaluationErrorsRetainBoundedExecutionDiagnostics()
        {
            var context = new CSharpEvaluationContext();
            CSharpSubsetEngine.Evaluate(
                "var total = 0; for (var i = 0; i < 70; i++) { total += \"x\".Length; } return total;",
                "statements", context);
            var wrap = typeof(CodingRiver.UPilot.UPilotExecutionService).GetMethod(
                "WrapEvaluationException", BindingFlags.Static | BindingFlags.NonPublic);
            var toError = typeof(CodingRiver.UPilot.UPilotExecutionService).GetMethod(
                "ToErrorDetail", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(wrap, Is.Not.Null);
            Assert.That(toError, Is.Not.Null);

            var wrapped = (ExecutionContractException)wrap.Invoke(null, new object[]
            {
                new ExecutionContractException("EXECUTION_BUDGET_EXCEEDED", "fixture budget failure"), context, "s.diagnostics", null,
            });
            var detail = (CodingRiver.UPilot.ErrorDetailPayload)toError.Invoke(null, new object[]
            {
                wrapped, "cmd.diagnostics", "csharp.eval",
            });

            Assert.That(detail.executionDiagnostics, Is.Not.Null);
            Assert.That(detail.executionDiagnostics.completedBoundaries, Has.Length.EqualTo(64));
            Assert.That(detail.executionDiagnostics.droppedDiagnosticCount, Is.EqualTo(6));
            Assert.That(detail.executionDiagnosticsJson, Does.Contain("droppedDiagnosticCount"));
            Assert.That(detail.executionDiagnosticsJson, Does.Not.Contain("total"));
        }

        [Test]
        public void InterpreterUsesOnlyExactUnityVectorOperatorSignatures()
        {
            var sum = (Vector3)CSharpSubsetEngine.Evaluate(
                "new UnityEngine.Vector3(1f, 2f, 3f) + new UnityEngine.Vector3(4f, 5f, 6f)",
                "expression", new CSharpEvaluationContext()).Value;
            Assert.That(sum, Is.EqualTo(new Vector3(5f, 7f, 9f)));

            var scaled = (Vector3)CSharpSubsetEngine.Evaluate(
                "new UnityEngine.Vector3(1f, 2f, 3f) * 2f", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(scaled, Is.EqualTo(new Vector3(2f, 4f, 6f)));

            var negated = (Vector2)CSharpSubsetEngine.Evaluate(
                "-new UnityEngine.Vector2(3f, 4f)", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(negated, Is.EqualTo(new Vector2(-3f, -4f)));

            var incompatible = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new UnityEngine.Vector3(1f, 2f, 3f) * 2.0", "expression", new CSharpEvaluationContext()));
            Assert.That(incompatible.Code, Is.EqualTo("CSHARP_BIND_ERROR"));
        }

        [Test]
        public void GenericInferenceCoversArrayNullableConstraintsAndAmbiguity()
        {
            var array = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.ArrayLength(new int[] { 1, 2, 3 })",
                "expression", new CSharpEvaluationContext());
            Assert.That(array.Value, Is.EqualTo(3));

            var nullable = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.NullableName((int?)3)",
                "expression", new CSharpEvaluationContext());
            Assert.That(nullable.Value, Is.EqualTo("Int32"));

            var constraint = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.StructOnly(\"bad\")",
                "expression", new CSharpEvaluationContext()));
            Assert.That(constraint.Code, Is.EqualTo("CSHARP_BIND_GENERIC_INFERENCE_FAILED"));
            Assert.That(constraint.Detail["candidates"], Is.TypeOf<string[]>());

            var noInference = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.NoInference()",
                "expression", new CSharpEvaluationContext()));
            Assert.That(noInference.Code, Is.EqualTo("CSHARP_BIND_GENERIC_INFERENCE_FAILED"));

            var ambiguous = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.GenericAmbiguous(new System.Collections.Generic.List<int>())",
                "expression", new CSharpEvaluationContext()));
            Assert.That(ambiguous.Code, Is.EqualTo("REFLECTION_BIND_AMBIGUOUS"));
            Assert.That((string[])ambiguous.Detail["candidates"], Has.Length.EqualTo(2));
        }

        [Test]
        public void InterpreterSupportsTypedAndJaggedArrays()
        {
            var sized = CSharpSubsetEngine.Evaluate(
                "var values = new int[3]; values[2] = 7; return values[2];", "statements", new CSharpEvaluationContext());
            Assert.That(sized.Value, Is.EqualTo(7));
            var initialized = CSharpSubsetEngine.Evaluate(
                "var values = new int[][] { new int[] { 2, 3 }, new int[] { 5 } }; return values[0][1];",
                "statements", new CSharpEvaluationContext());
            Assert.That(initialized.Value, Is.EqualTo(3));
            var implicitArray = (Array)CSharpSubsetEngine.Evaluate("new[] { 1, 2L }", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(implicitArray.GetType(), Is.EqualTo(typeof(long[])));
            Assert.That(implicitArray.GetValue(1), Is.EqualTo(2L));
        }

        [Test]
        public void TypedArraysConvertElementsAndRejectInvalidShapesAndLengths()
        {
            var converted = CSharpSubsetEngine.Evaluate(
                "var values = new long[] { 1, 2 }; return values[1];", "statements", new CSharpEvaluationContext());
            Assert.That(converted.Value, Is.EqualTo(2L));

            var negative = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new int[-1]", "expression", new CSharpEvaluationContext()));
            Assert.That(negative.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));

            var conversion = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new int[] { \"bad\" }", "expression", new CSharpEvaluationContext()));
            Assert.That(conversion.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));

            var multidimensional = (Array)CSharpSubsetEngine.Evaluate(
                "new int[2, 2]", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(multidimensional.Rank, Is.EqualTo(2));
            Assert.That(multidimensional.Length, Is.EqualTo(4));

            var allocationBudget = new CSharpEvaluationContext(
                budget: new ExecutionBudget(maxAllocations: 1));
            var allocation = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var first = new int[1]; var second = new int[1];", "statements", allocationBudget));
            Assert.That(allocation.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(allocation.Detail["metric"], Is.EqualTo("allocations"));
            AssertSpan(allocation, "budget");
        }

        [Test]
        public void V2ExceptionHandlingPreservesCatchOrderRethrowAndFinallyControlFlow()
        {
            var handled = CSharpSubsetEngine.Evaluate(
                "var state = \"start\"; try { throw new System.ArgumentException(\"bad\"); } " +
                "catch (System.InvalidOperationException ex) { state = \"wrong\"; } " +
                "catch (System.Exception ex) { state = ex.Message; } finally { state += \"!\"; } return state;",
                "statements", new CSharpEvaluationContext());
            Assert.That(handled.Value, Is.EqualTo("bad!"));

            var rethrow = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "try { throw new System.InvalidOperationException(\"original\"); } catch { throw; }",
                "statements", new CSharpEvaluationContext()));
            Assert.That(rethrow.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));
            Assert.That(rethrow.Message, Does.Contain("System.InvalidOperationException"));

            var forbidden = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "try { } finally { return 1; }", "statements", new CSharpEvaluationContext()));
            Assert.That(forbidden.Code, Is.EqualTo("CSHARP_UNSUPPORTED_SYNTAX"));
            AssertSpan(forbidden, "parse");
        }

        [Test]
        public void V2InfrastructureFailuresBypassCatchAndRunBoundedFinally()
        {
            var context = new CSharpEvaluationContext(budget: new ExecutionBudget(maxLoopIterations: 1));
            var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var cleaned = 0; try { while (true) { } } catch { cleaned = 9; } finally { cleaned = 1; }",
                "statements", context));
            Assert.That(error.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(context.SnapshotVariables()["cleaned"], Is.EqualTo(1));
            Assert.That(context.Budget.FinallyCleanupStatements, Is.GreaterThan(0));
        }

        [Test]
        public void V2ClosuresCaptureCellsAndSupportTypedBlockLambdas()
        {
            var referenceCapture = CSharpSubsetEngine.Evaluate(
                "var value = 1; var read = () => value; value = 4; return read();",
                "statements", new CSharpEvaluationContext());
            Assert.That(referenceCapture.Value, Is.EqualTo(4));

            var block = CSharpSubsetEngine.Evaluate(
                "var add = (int left, int right) => { var total = left + right; return total; }; return add(2, 3);",
                "statements", new CSharpEvaluationContext());
            Assert.That(block.Value, Is.EqualTo(5));

            var duplicate = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var value = 1; var value = 2;", "statements", new CSharpEvaluationContext()));
            Assert.That(duplicate.Code, Is.EqualTo("CSHARP_DUPLICATE_LOCAL"));
        }

        [Test]
        public void V2LexerParsesCSharpNumericAndCharacterLiteralsAndRejectsMalformedInput()
        {
            Assert.That(CSharpSubsetEngine.Evaluate("0xFF", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo(255));
            Assert.That(CSharpSubsetEngine.Evaluate("0b1010_0101", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo(165));
            Assert.That(CSharpSubsetEngine.Evaluate("4_294_967_295u", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo(uint.MaxValue));
            Assert.That(CSharpSubsetEngine.Evaluate("'\\n'", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo('\n'));
            Assert.That(CSharpSubsetEngine.Evaluate("'\\u0041'", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo('A'));
            Assert.That(CSharpSubsetEngine.Evaluate("\"a\\tb\"", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo("a\tb"));

            foreach (string code in new[] { "''", "'ab'", "'\\q'", "\"unterminated", "0x", "1fL", "18446744073709551616" })
            {
                var error = Assert.Throws<ExecutionContractException>(() =>
                    CSharpSubsetEngine.Evaluate(code, "expression", new CSharpEvaluationContext()), code);
                Assert.That(error.Code, Is.EqualTo("CSHARP_PARSE_ERROR"), code);
                Assert.That(error.Detail["stage"], Is.EqualTo("parse"), code);
                Assert.That(error.Detail["sourceSpan"], Is.TypeOf<ExecutionSourceSpan>(), code);
            }

            string deep = new string('!', 300) + "true";
            var depth = Assert.Throws<ExecutionContractException>(() =>
                CSharpSubsetEngine.Evaluate(deep, "expression", new CSharpEvaluationContext()));
            Assert.That(depth.Code, Is.EqualTo("CSHARP_PARSE_DEPTH_EXCEEDED"));
            Assert.That(depth.Detail["limit"], Is.EqualTo(256));
        }

        [Test]
        public void V2AssignmentTargetsAreEvaluatedOnceAndConditionalAccessShortCircuits()
        {
            var target = new ExecutionAssignmentFixture { Value = 2, Child = new ExecutionAssignmentFixture { Value = 7 } };
            ExecutionAssignmentFixtureSource.Reset(target);
            var member = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.GetTarget().Value += 3; return 0;",
                "statements", new CSharpEvaluationContext());
            Assert.That(member.Value, Is.EqualTo(0));
            Assert.That(target.Value, Is.EqualTo(5));
            Assert.That(ExecutionAssignmentFixtureSource.ReceiverCalls, Is.EqualTo(1));

            ExecutionAssignmentFixtureSource.Reset(target);
            CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.GetTarget().Values[" +
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.NextIndex()]++; return 0;",
                "statements", new CSharpEvaluationContext());
            Assert.That(target.Values[0], Is.EqualTo(5));
            Assert.That(ExecutionAssignmentFixtureSource.ReceiverCalls, Is.EqualTo(1));
            Assert.That(ExecutionAssignmentFixtureSource.IndexCalls, Is.EqualTo(1));

            ExecutionAssignmentFixtureSource.Reset(null);
            var conditionalCall = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.GetTarget()?.Add(" +
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.CountArgument(9))",
                "expression", new CSharpEvaluationContext());
            Assert.That(conditionalCall.Value, Is.Null);
            Assert.That(ExecutionAssignmentFixtureSource.ReceiverCalls, Is.EqualTo(1));
            Assert.That(ExecutionAssignmentFixtureSource.ArgumentCalls, Is.EqualTo(0));

            ExecutionAssignmentFixtureSource.Reset(null);
            var conditionalChain = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionAssignmentFixtureSource.GetTarget()?.Child.Value",
                "expression", new CSharpEvaluationContext());
            Assert.That(conditionalChain.Value, Is.Null);
            Assert.That(ExecutionAssignmentFixtureSource.ReceiverCalls, Is.EqualTo(1));
        }

        [Test]
        public void V2ForAndForeachUseSpecifiedCaptureCells()
        {
            var forCapture = CSharpSubsetEngine.Evaluate(
                "var callbacks = new System.Collections.Generic.List<System.Func<int>>(); " +
                "for (var i = 0; i < 3; i++) { callbacks.Add(() => i); } " +
                "return callbacks[0]() + callbacks[1]() + callbacks[2]();",
                "statements", new CSharpEvaluationContext());
            Assert.That(forCapture.Value, Is.EqualTo(9));

            var foreachCapture = CSharpSubsetEngine.Evaluate(
                "var callbacks = new System.Collections.Generic.List<System.Func<int>>(); " +
                "foreach (var item in new int[] { 1, 2, 3 }) { callbacks.Add(() => item); } " +
                "return callbacks[0]() + callbacks[1]() + callbacks[2]();",
                "statements", new CSharpEvaluationContext());
            Assert.That(foreachCapture.Value, Is.EqualTo(6));
        }

        [Test]
        public void V2AsyncLambdaAwaitsTaskAndRejectsAsyncVoidConversion()
        {
            var result = CSharpSubsetEngine.EvaluateAsync(
                "var callback = async (int value) => { return await System.Threading.Tasks.Task.FromResult(value + 1); }; return await callback(4);",
                "statements", new CSharpEvaluationContext()).GetAwaiter().GetResult();
            Assert.That(result.Value, Is.EqualTo(5));
            Assert.That(result.Budget.Awaits, Is.EqualTo(2));

            var contextual = CSharpSubsetEngine.EvaluateAsync(
                "return await CodingRiver.UPilot.Tests.ExecutionReflectionFixture.InvokeAsyncDelegate<int,int>(" +
                "async (int value) => await System.Threading.Tasks.Task.FromResult(value + 2), 3);",
                "statements", new CSharpEvaluationContext()).GetAwaiter().GetResult();
            Assert.That(contextual.Value, Is.EqualTo(5));

            var asyncVoid = CSharpSubsetEngine.EvaluateAsync(
                "async (int value) => value + 1", "expression", new CSharpEvaluationContext()).GetAwaiter().GetResult().Value as LambdaValue;
            var error = Assert.Throws<ExecutionContractException>(() => asyncVoid.ToDelegate(typeof(Action<int>)));
            Assert.That(error.Code, Is.EqualTo("CSHARP_ASYNC_VOID_UNSUPPORTED"));

            var syncEntry = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "return await System.Threading.Tasks.Task.FromResult(1);", "statements", new CSharpEvaluationContext()));
            Assert.That(syncEntry.Code, Is.EqualTo("CSHARP_ASYNC_REQUIRES_ASYNC_ENTRY"));
        }

        [Test]
        public void V2SessionKeepsClosureRootsTracksAsyncAndInvalidatesOnClose()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("v2-closure", 600, 16, 2, 8, 4);
            var first = CSharpSubsetEngine.Evaluate(
                "var value = 2; var read = () => value; return read;", "statements",
                new CSharpEvaluationContext(sessionLifetime: session));
            var closure = (LambdaValue)first.Value;
            var second = CSharpSubsetEngine.Evaluate(
                "value = 8; return read();", "statements",
                new CSharpEvaluationContext(sessionLifetime: session));
            Assert.That(second.Value, Is.EqualTo(8));

            var asyncLambda = (LambdaValue)CSharpSubsetEngine.EvaluateAsync(
                "async (int value) => await System.Threading.Tasks.Task.FromResult(value + 1)", "expression",
                new CSharpEvaluationContext(sessionLifetime: session)).GetAwaiter().GetResult().Value;
            Assert.That(asyncLambda.InvokeAsync(4).GetAwaiter().GetResult(), Is.EqualTo(5));
            Assert.That(session.ActiveAsyncOperations, Is.EqualTo(0));
            Assert.That(session.CompletedAsyncOperations, Is.EqualTo(1));
            Assert.That(session.ReleasedAsyncOperations, Is.EqualTo(1));

            var closed = registry.Close(session.Id);
            Assert.That(closed.asyncOperationsStillRunning, Is.EqualTo(0));
            var expired = Assert.Throws<ExecutionContractException>(() => closure.Invoke());
            Assert.That(expired.Code, Is.EqualTo("EXECUTION_SESSION_CLOSED"));
        }

        [Test]
        public void V2PersistentAsyncClosureDoesNotRetainDisposedCallCancellationSource()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("v2-disposed-call-token", 600, 16, 2, 8, 4);
            using (var firstCall = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken))
            {
                CSharpSubsetEngine.EvaluateAsync(
                    "var asyncAdd = async (int value) => await System.Threading.Tasks.Task.FromResult(value + 1); return 0;",
                    "statements",
                    new CSharpEvaluationContext(
                        budget: new ExecutionBudget(cancellationToken: firstCall.Token),
                        cancellationToken: firstCall.Token,
                        sessionLifetime: session)).GetAwaiter().GetResult();
            }

            using (var secondCall = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken))
            {
                var result = CSharpSubsetEngine.EvaluateAsync(
                    "return await asyncAdd(4);",
                    "statements",
                    new CSharpEvaluationContext(
                        budget: new ExecutionBudget(cancellationToken: secondCall.Token),
                        cancellationToken: secondCall.Token,
                        sessionLifetime: session)).GetAwaiter().GetResult();
                Assert.That(result.Value, Is.EqualTo(5));
            }

            Assert.That(session.TryGetVariable("asyncAdd", out var stored), Is.True);
            var projectDelegate = (Func<int, Task<int>>)((LambdaValue)stored).ToDelegate(typeof(Func<int, Task<int>>));
            Assert.That(projectDelegate(5).GetAwaiter().GetResult(), Is.EqualTo(6));

            registry.Close(session.Id);
        }

        [Test]
        public void V2SessionCloseCancelsAsyncLeaseAndReportsRunningOperation()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("v2-async-close", 600, 16, 2, 8, 4);
            var callback = (LambdaValue)CSharpSubsetEngine.EvaluateAsync(
                "async () => { await System.Threading.Tasks.Task.Delay(1000); return 1; }", "expression",
                new CSharpEvaluationContext(sessionLifetime: session)).GetAwaiter().GetResult().Value;
            Task<object> running = callback.InvokeAsync();
            Assert.That(session.ActiveAsyncOperations, Is.EqualTo(1));
            var closed = registry.Close(session.Id);
            Assert.That(closed.asyncOperationsStillRunning, Is.EqualTo(1));
            var cancelled = Assert.Throws<ExecutionContractException>(() => running.GetAwaiter().GetResult());
            Assert.That(cancelled.Code, Is.EqualTo("EXECUTION_CANCELLED"));
            Assert.That(session.ActiveAsyncOperations, Is.EqualTo(0));
            Assert.That(session.CancelledAsyncOperations, Is.EqualTo(1));
            Assert.That(session.ReleasedAsyncOperations, Is.EqualTo(1));
        }

        [Test]
        public void V2GenericInferenceTraversesInterfacesParamsAndTypedLambdas()
        {
            var fromInterface = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.FromEnumerable(new System.Collections.Generic.List<int>())",
                "expression", new CSharpEvaluationContext());
            Assert.That(fromInterface.Value, Is.EqualTo("Int32"));

            var paramsResult = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.ParamsCount(1, 2, 3)",
                "expression", new CSharpEvaluationContext());
            Assert.That(paramsResult.Value, Is.EqualTo(3));

            var lambda = CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Apply(3, (int value) => value + 2)",
                "expression", new CSharpEvaluationContext());
            Assert.That(lambda.Value, Is.EqualTo(5));

            var returnOnly = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Create(() => 3)",
                "expression", new CSharpEvaluationContext()));
            Assert.That(returnOnly.Code, Is.EqualTo("CSHARP_BIND_GENERIC_INFERENCE_FAILED"));
        }

        [Test]
        public void V2ArraysSupportRanksInitializersInferenceShapeAndElementBudget()
        {
            var matrix = CSharpSubsetEngine.Evaluate(
                "int[,] matrix = { { 1, 2 }, { 3, 4 } }; matrix[1, 0] = 7; return matrix[1, 0];",
                "statements", new CSharpEvaluationContext());
            Assert.That(matrix.Value, Is.EqualTo(7));
            Assert.That(matrix.Budget.ArrayElements, Is.EqualTo(4));

            var rankFour = (Array)CSharpSubsetEngine.Evaluate("new int[1,1,1,2]", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(rankFour.Rank, Is.EqualTo(4));
            Assert.That(rankFour.Length, Is.EqualTo(2));

            var jagged = (Array)CSharpSubsetEngine.Evaluate(
                "new[] { new[] { 1 }, new[] { 2, 3 } }", "expression", new CSharpEvaluationContext()).Value;
            Assert.That(jagged.GetType(), Is.EqualTo(typeof(int[][])));

            var shape = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new int[,] { { 1 }, { 2, 3 } }", "expression", new CSharpEvaluationContext()));
            Assert.That(shape.Code, Is.EqualTo("CSHARP_ARRAY_SHAPE_MISMATCH"));

            var empty = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new[] { }", "expression", new CSharpEvaluationContext()));
            Assert.That(empty.Code, Is.EqualTo("CSHARP_ARRAY_INFERENCE_FAILED"));

            var budget = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "new int[3]", "expression", new CSharpEvaluationContext(budget: new ExecutionBudget(maxArrayElements: 2))));
            Assert.That(budget.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(budget.Detail["metric"], Is.EqualTo("arrayElements"));
        }

        [Test]
        public void ReflectionExpressionProfileAllowsV2ArraysButRejectsLambdaAndAwait()
        {
            var worker = typeof(UPilotExecutionService).GetMethod("ExecuteEvaluationWorker", BindingFlags.NonPublic | BindingFlags.Static);
            var arrayPayload = new CSharpEvalPayload
            {
                code = "new int[,] { { 1, 2 } }[0,1]", mode = "expression",
                executionBackend = "interpret", languageProfileMode = "reflection-expression",
            };
            object completed = worker.Invoke(null, new object[] { arrayPayload, new CSharpEvaluationContext() });
            var resultField = completed.GetType().GetField("Result", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(((CSharpEvaluationResult)resultField.GetValue(completed)).Value, Is.EqualTo(2));

            foreach (string code in new[] { "() => 1", "await System.Threading.Tasks.Task.FromResult(1)" })
            {
                var payload = new CSharpEvalPayload
                {
                    code = code, mode = "expression", executionBackend = "interpret",
                    languageProfileMode = "reflection-expression",
                };
                var error = Assert.Throws<TargetInvocationException>(() =>
                    worker.Invoke(null, new object[] { payload, new CSharpEvaluationContext() }));
                Assert.That(error.InnerException, Is.TypeOf<ExecutionContractException>());
                Assert.That(((ExecutionContractException)error.InnerException).Code, Is.EqualTo("CSHARP_UNSUPPORTED_SYNTAX"));
            }
        }

        [Test]
        public void RuntimeErrorsCarryMultilineSourceSpan()
        {
            var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var value = 1;\nreturn value.Missing;", "statements", new CSharpEvaluationContext()));
            Assert.That(error.Detail["sourceSpan"], Is.TypeOf<ExecutionSourceSpan>());
            var span = (ExecutionSourceSpan)error.Detail["sourceSpan"];
            Assert.That(span.line, Is.EqualTo(2));
            Assert.That(span.end, Is.GreaterThan(span.start));
        }

        [Test]
        public void ParseBindPolicyRuntimeBudgetAndEmitErrorsCarryStageAndSpan()
        {
            var parse = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var value = 1;\nvar broken = ;", "statements", new CSharpEvaluationContext()));
            AssertSpan(parse, "parse");

            var bind = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "System.String.DefinitelyMissingMember(\n  1\n)", "expression", new CSharpEvaluationContext()));
            AssertSpan(bind, "bind");
            var bindSpan = (ExecutionSourceSpan)bind.Detail["sourceSpan"];
            Assert.That(bindSpan.start, Is.EqualTo(0));
            Assert.That(bindSpan.end, Is.EqualTo(44));

            var policy = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Exit()", "expression", new CSharpEvaluationContext()));
            Assert.That(policy.Code, Is.EqualTo("EXECUTION_POLICY_DENIED"));
            AssertSpan(policy, "policy");

            var runtime = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.ThrowNow()", "expression", new CSharpEvaluationContext()));
            Assert.That(runtime.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));
            AssertSpan(runtime, "runtime");

            var budgetContext = new CSharpEvaluationContext(budget: new ExecutionBudget(maxStatements: 1));
            var budget = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "var first = 1;\nvar second = 2;", "statements", budgetContext));
            AssertSpan(budget, "budget");

            var emit = CSharpEmitBackend.Compile(
                CSharpEmitBackend.CacheKey("return await value;", "statements", new[] { "System" }),
                "return await value;", "statements");
            Assert.That(emit, Is.Not.Null);
        }

        [Test]
        public void CancellationHasStableCodeAndPreservesActualState()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var context = new CSharpEvaluationContext(
                    new Dictionary<string, object>(), budget: new ExecutionBudget(cancellationToken: cancellation.Token),
                    cancellationToken: cancellation.Token);
                var error = Assert.Throws<ExecutionContractException>(() =>
                    CSharpSubsetEngine.Evaluate("var value = 1; while (true) { value++; }", "statements", context));
                Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
            }

            using (var cancellation = new CancellationTokenSource())
            {
                var context = new CSharpEvaluationContext(
                    new Dictionary<string, object>(),
                    budget: new ExecutionBudget(cancellationToken: cancellation.Token),
                    invocationScheduler: action => { var value = action(); cancellation.Cancel(); return value; },
                    cancellationToken: cancellation.Token);
                var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                    "var value = 1; CodingRiver.UPilot.Tests.ExecutionReflectionFixture.Identity(value); value = 2;",
                    "statements", context));
                Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
                Assert.That(context.SnapshotVariables()["value"], Is.EqualTo(1));
                Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            }
        }

        [Test]
        public void AwaitableCancellationHasStableCode()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(10);
                var error = Assert.Throws<ExecutionContractException>(() =>
                    AwaitableAdapter.AwaitAsync(Task.Delay(1000), "always", 2000, cancellation.Token).GetAwaiter().GetResult());
                Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
            }
        }

        [Test]
        public void CancellationTimeoutAndEmitValidationNeverReplaySideEffects()
        {
            ExecutionReflectionFixture.InvocationCount = 0;
            using (var cancellation = new CancellationTokenSource())
            {
                var context = new CSharpEvaluationContext(
                    invocationScheduler: action =>
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                        return action();
                    },
                    cancellationToken: cancellation.Token,
                    budget: new ExecutionBudget(cancellationToken: cancellation.Token));
                var error = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                    "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.CountInvocation()", "expression", context));
                Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
                Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(0));
            }

            var timeout = Assert.Throws<ExecutionContractException>(() => AwaitableAdapter.AwaitAsync(
                Task.Delay(100), "always", 1).GetAwaiter().GetResult());
            Assert.That(timeout.Code, Is.EqualTo("AWAITABLE_TIMEOUT"));

            var worker = typeof(UPilotExecutionService).GetMethod("ExecuteEvaluationWorker", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(worker, Is.Not.Null);
            var autoPayload = new CSharpEvalPayload
            {
                code = "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.CountInvocation(); return value => value;",
                mode = "statements",
                executionBackend = "auto",
            };
            worker.Invoke(null, new object[] { autoPayload, new CSharpEvaluationContext() });
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(1), "auto must not replay after post-execution emit-cache validation fails");

            ExecutionReflectionFixture.InvocationCount = 0;
            autoPayload.executionBackend = "emit";
            worker.Invoke(null, new object[] { autoPayload, new CSharpEvaluationContext() });
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(1), "V2 emit-cache executes the validated lambda AST once");
        }

        [Test]
        public void EventSubscriptionRequiresSessionAndClosesDeterministically()
        {
            var source = new ExecutionEventFixture();
            int observed = 0;
            Action<int> handler = value => observed += value;
            var withoutSession = Assert.Throws<ExecutionContractException>(() => CSharpSubsetEngine.Evaluate(
                "source.Changed += handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "source", source }, { "handler", handler } })));
            Assert.That(withoutSession.Code, Is.EqualTo("SESSION_REQUIRED"));

            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("events", 600, 8, 2, 2);
            CSharpSubsetEngine.Evaluate("source.Changed += handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "source", source }, { "handler", handler } }, sessionLifetime: session));
            Assert.That(session.SubscriptionCount, Is.EqualTo(1));
            CSharpSubsetEngine.Evaluate("source.Changed -= handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "source", source }, { "handler", handler } }, sessionLifetime: session));
            Assert.That(session.SubscriptionCount, Is.EqualTo(0));
            source.Raise(1);
            Assert.That(observed, Is.EqualTo(0));
            CSharpSubsetEngine.Evaluate("source.Changed += handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "source", source }, { "handler", handler } }, sessionLifetime: session));
            source.Raise(2);
            Assert.That(observed, Is.EqualTo(2));
            registry.Close(session.Id);
            source.Raise(3);
            Assert.That(observed, Is.EqualTo(2));
        }

        [Test]
        public void EventSubscriptionsCoverStaticTtlPlayModeAndCleanupFailure()
        {
            int staticObserved = 0;
            Action<int> staticHandler = value => staticObserved += value;
            var staticRegistry = new ExecutionSessionRegistry();
            var staticSession = staticRegistry.Open("static-event", 600, 8, 2, 2);
            CSharpSubsetEngine.Evaluate(
                "CodingRiver.UPilot.Tests.ExecutionReflectionFixture.StaticChanged += handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "handler", staticHandler } }, sessionLifetime: staticSession));
            ExecutionReflectionFixture.RaiseStaticEvent(2);
            Assert.That(staticObserved, Is.EqualTo(2));
            staticRegistry.Close(staticSession.Id);
            ExecutionReflectionFixture.RaiseStaticEvent(3);
            Assert.That(staticObserved, Is.EqualTo(2));

            ExecutionReflectionFixture.EventObserved = 0;
            var lambdaSource = new ExecutionEventFixture();
            var lambdaRegistry = new ExecutionSessionRegistry();
            var lambdaSession = lambdaRegistry.Open("lambda-event", 600, 8, 2, 2);
            CSharpSubsetEngine.Evaluate(
                "source.Changed += value => CodingRiver.UPilot.Tests.ExecutionReflectionFixture.ObserveEvent(value);",
                "statements", new CSharpEvaluationContext(
                    new Dictionary<string, object> { { "source", lambdaSource } }, sessionLifetime: lambdaSession));
            lambdaSource.Raise(5);
            Assert.That(ExecutionReflectionFixture.EventObserved, Is.EqualTo(5));
            lambdaRegistry.Close(lambdaSession.Id);
            lambdaSource.Raise(7);
            Assert.That(ExecutionReflectionFixture.EventObserved, Is.EqualTo(5));

            var ttlSource = new ExecutionEventFixture();
            int ttlObserved = 0;
            Action<int> ttlHandler = value => ttlObserved += value;
            var ttlRegistry = new ExecutionSessionRegistry();
            var ttlSession = ttlRegistry.Open("ttl-event", 1, 8, 2, 2);
            CSharpSubsetEngine.Evaluate("source.Changed += handler;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "source", ttlSource }, { "handler", ttlHandler } }, sessionLifetime: ttlSession));
            Thread.Sleep(1100);
            ttlRegistry.SweepExpired();
            ttlSource.Raise(1);
            Assert.That(ttlObserved, Is.EqualTo(0));
            Assert.That(ttlSession.SubscriptionCount, Is.EqualTo(0));

            var unitySource = ScriptableObject.CreateInstance<ExecutionUnityEventFixture>();
            try
            {
                int playModeObserved = 0;
                Action<int> playModeHandler = value => playModeObserved += value;
                var service = new UPilotExecutionService(null);
                var playModeSession = service.Sessions.Open("playmode-event", 600, 8, 2, 2);
                CSharpSubsetEngine.Evaluate("source.Changed += handler;", "statements",
                    new CSharpEvaluationContext(new Dictionary<string, object> { { "source", unitySource }, { "handler", playModeHandler } }, sessionLifetime: playModeSession));
                service.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
                Assert.That(playModeSession.SubscriptionCount, Is.EqualTo(0));
                unitySource.Raise(4);
                Assert.That(playModeObserved, Is.EqualTo(0));
                service.Sessions.Close(playModeSession.Id);
            }
            finally { UnityEngine.Object.DestroyImmediate(unitySource); }

            var faulty = new ExecutionFaultyEventFixture();
            Action<int> faultyHandler = _ => { };
            bool callbackCleanupRan = false;
            var cleanupRegistry = new ExecutionSessionRegistry();
            var cleanupSession = cleanupRegistry.Open("cleanup-event", 600, 8, 2, 2);
            cleanupSession.AddEventSubscription(faulty, typeof(ExecutionFaultyEventFixture).GetEvent("Changed"), faultyHandler);
            cleanupSession.RegisterCleanup(() => callbackCleanupRan = true);
            var closed = cleanupRegistry.Close(cleanupSession.Id);
            Assert.That(closed.cleanupErrors, Has.Length.EqualTo(1));
            Assert.That(callbackCleanupRan, Is.True, "subscription cleanup failure must not block callback cleanup");
            Assert.That(cleanupSession.SubscriptionCount, Is.EqualTo(0));
        }

        [Test]
        public void InterpreterRoutesReflectedCallsThroughInvocationScheduler()
        {
            int testThreadId = Thread.CurrentThread.ManagedThreadId;
            int invokedThreadId = -1;
            Func<object> scheduledAction = null;
            object scheduledResult = null;
            Exception scheduledError = null;
            using (var scheduled = new ManualResetEventSlim(false))
            using (var released = new ManualResetEventSlim(false))
            {
                var context = new CSharpEvaluationContext(
                    invocationScheduler: action =>
                    {
                        scheduledAction = action;
                        scheduled.Set();
                        if (!released.Wait(2000)) throw new TimeoutException("Test scheduler was not released.");
                        if (scheduledError != null) throw scheduledError;
                        return scheduledResult;
                    });
                var worker = Task.Run(() => CSharpSubsetEngine.Evaluate("System.Math.Abs(-3)", "expression", context));

                Assert.That(scheduled.Wait(2000), Is.True, "The reflected call did not reach the scheduler boundary.");
                try
                {
                    invokedThreadId = Thread.CurrentThread.ManagedThreadId;
                    scheduledResult = scheduledAction();
                }
                catch (Exception ex)
                {
                    scheduledError = ex;
                }
                finally
                {
                    released.Set();
                }

                var result = worker.GetAwaiter().GetResult();
                Assert.That(invokedThreadId, Is.EqualTo(testThreadId));
                Assert.That(result.Value, Is.EqualTo(3));
            }
        }

        [Test]
        public void BudgetFailureHasStableCodeAndMetricDetails()
        {
            var budget = new ExecutionBudget(maxStatements: 1);
            budget.CountStatement();
            var error = Assert.Throws<ExecutionContractException>(() => budget.CountStatement());

            Assert.That(error.Code, Is.EqualTo("EXECUTION_BUDGET_EXCEEDED"));
            Assert.That(error.Detail["metric"], Is.EqualTo("statements"));
            Assert.That(error.Detail["limit"], Is.EqualTo(1));
            Assert.That(error.Detail["actual"], Is.EqualTo(2));
        }

        [Test]
        public void EmitBackendCachesValidatedProgram()
        {
            const string code = "return value + 2;";
            string key = CSharpEmitBackend.CacheKey(code, "statements", new[] { "System" });
            var compiled = CSharpEmitBackend.Compile(key, code, "statements");
            var result = compiled(new CSharpEvaluationContext(new Dictionary<string, object> { { "value", 5 } }));
            Assert.That(result.Value, Is.EqualTo(7));
            Assert.That(CSharpEmitBackend.IsCached(key), Is.True);

            const string literalCode = "return \"await is data\";";
            string literalKey = CSharpEmitBackend.CacheKey(literalCode, "statements", new[] { "System" });
            var literal = CSharpEmitBackend.Compile(literalKey, literalCode, "statements")(
                new CSharpEvaluationContext());
            Assert.That(literal.Value, Is.EqualTo("await is data"));

            var awaitProgram = CSharpEmitBackend.CompileAsync(
                CSharpEmitBackend.CacheKey("return await value;", "statements", new[] { "System" }),
                "return await value;",
                "statements");
            var awaitResult = awaitProgram(new CSharpEvaluationContext(
                new Dictionary<string, object> { { "value", Task.FromResult(9) } })).GetAwaiter().GetResult();
            Assert.That(awaitResult.Value, Is.EqualTo(9));
        }

        [Test]
        public void DirectCompiledBackendRunsTypedControlFlowAndRejectsUnsupportedNodesBeforeExecution()
        {
            const string code = "var sum = 0; for (var i = 0; i < 10; i++) sum += i; return sum;";
            var context = new CSharpEvaluationContext();
            string key = CSharpCompiledBackend.CacheKey(code, "statements", context.Imports, context.SnapshotVariables());
            var compiled = CSharpCompiledBackend.Compile(key, code, "statements", context);
            var result = compiled(context);
            Assert.That(result.Value, Is.EqualTo(45));
            Assert.That(CSharpCompiledBackend.IsCached(key), Is.True);
            Assert.That(result.Budget.LoopIterations, Is.EqualTo(10));

            var payload = new CSharpEvalPayload
            {
                code = code,
                mode = "statements",
                executionBackend = "compiled",
            };
            var worker = typeof(UPilotExecutionService).GetMethod("ExecuteEvaluationWorker", BindingFlags.NonPublic | BindingFlags.Static);
            object completed = worker.Invoke(null, new object[] { payload, new CSharpEvaluationContext() });
            Assert.That(completed.GetType().GetField("BackendUsed").GetValue(completed), Is.EqualTo("compiled"));
            Assert.That(((CSharpEvaluationResult)completed.GetType().GetField("Result").GetValue(completed)).Value, Is.EqualTo(45));

            ExecutionReflectionFixture.InvocationCount = 0;
            const string unsupportedCode = "new int[1, 1]";
            var unsupportedContext = new CSharpEvaluationContext();
            string unsupportedKey = CSharpCompiledBackend.CacheKey(
                unsupportedCode, "expression", unsupportedContext.Imports, unsupportedContext.SnapshotVariables());
            var unsupported = Assert.Throws<ExecutionContractException>(() =>
                CSharpCompiledBackend.Compile(unsupportedKey, unsupportedCode, "expression", unsupportedContext));
            Assert.That(unsupported.Code, Is.EqualTo("CSHARP_COMPILED_UNSUPPORTED_NODE"));
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(0));
        }

        [Test]
        public void CSharpValidatePreflightsBackendsWithoutExecutingBusinessCode()
        {
            ExecutionThrowingGetterFixture.GetterCallCount = 0;
            var interpreted = UPilotExecutionService.ValidateCSharpPayload(new CSharpValidatePayload
            {
                code = "CodingRiver.UPilot.Tests.ExecutionThrowingGetterFixture.Value",
                mode = "expression",
                backend = "interpret",
            });
            Assert.That(interpreted.syntaxValid, Is.True);
            Assert.That(interpreted.backend, Is.EqualTo("interpret"));
            Assert.That(interpreted.modeUsed, Is.EqualTo("expression"));
            Assert.That(ExecutionThrowingGetterFixture.GetterCallCount, Is.Zero);

            var compiled = UPilotExecutionService.ValidateCSharpPayload(new CSharpValidatePayload
            {
                code = "value + 2",
                mode = "expression",
                backend = "compiled",
                variableTypes = new[]
                {
                    new CSharpValidationVariablePayload { name = "value", typeName = "System.Int32" },
                },
            });
            Assert.That(compiled.boundaries, Is.EqualTo(new[] { "parse", "bind", "lower", "delegate-compile" }));

            var unsupported = Assert.Throws<ExecutionContractException>(() =>
                UPilotExecutionService.ValidateCSharpPayload(new CSharpValidatePayload
                {
                    code = "new int[1, 1]",
                    mode = "expression",
                    backend = "compiled",
                    variableTypes = new[]
                    {
                        new CSharpValidationVariablePayload { name = "value", typeName = "System.Int32" },
                    },
                }));
            Assert.That(unsupported.Code, Is.EqualTo("CSHARP_COMPILED_UNSUPPORTED_NODE"));
            Assert.That(ExecutionThrowingGetterFixture.GetterCallCount, Is.Zero);
        }

        [Test]
        public void DirectCompiledBackendBindsMembersCallsConstructionArraysAndIndexers()
        {
            const string code =
                "var created = new CodingRiver.UPilot.Tests.ExecutionAssignmentFixture();" +
                "created.Value = 2;" +
                "created.Values[0]++;" +
                "var values = new int[] { 1, 2, 3 };" +
                "values[1] += 4;" +
                "return System.Math.Abs(-created.Add(values[1]));";
            var context = new CSharpEvaluationContext();
            string key = CSharpCompiledBackend.CacheKey(code, "statements", context.Imports, context.SnapshotVariables());
            CSharpEvaluationResult result = CSharpCompiledBackend.Compile(key, code, "statements", context)(context);

            Assert.That(result.Value, Is.EqualTo(8));
            var created = (ExecutionAssignmentFixture)result.Variables["created"];
            Assert.That(created.Value, Is.EqualTo(8));
            Assert.That(created.Values[0], Is.EqualTo(5));
            Assert.That(((int[])result.Variables["values"])[1], Is.EqualTo(6));
            Assert.That(result.Budget.Calls, Is.EqualTo(2));
            Assert.That(result.Budget.Allocations, Is.EqualTo(2));
        }

        [Test]
        public void NullCoalescingAndTypeIntrinsicsMatchAcrossInterpreterAndCompiledBackends()
        {
            Assert.That(CSharpSubsetEngine.Evaluate("default(int)", "expression", new CSharpEvaluationContext()).Value, Is.EqualTo(0));
            Assert.That(CSharpSubsetEngine.Evaluate("typeof(System.String)", "expression", new CSharpEvaluationContext()).Value,
                Is.EqualTo(typeof(string)));
            Assert.That(CSharpSubsetEngine.Evaluate(
                "nameof(CodingRiver.UPilot.Tests.ExecutionReflectionFixture.InvocationCount)",
                "expression", new CSharpEvaluationContext()).Value, Is.EqualTo("InvocationCount"));

            const string code =
                "int? value = 2;" +
                "var first = value ?? CodingRiver.UPilot.Tests.ExecutionReflectionFixture.CountInvocation();" +
                "value = null;" +
                "value ??= CodingRiver.UPilot.Tests.ExecutionReflectionFixture.CountInvocation();" +
                "return first + value;";

            ExecutionReflectionFixture.InvocationCount = 0;
            var interpreted = CSharpSubsetEngine.Evaluate(code, "statements", new CSharpEvaluationContext());
            Assert.That(interpreted.Value, Is.EqualTo(3));
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(1));

            ExecutionReflectionFixture.InvocationCount = 0;
            var context = new CSharpEvaluationContext();
            string key = CSharpCompiledBackend.CacheKey(code, "statements", context.Imports, context.SnapshotVariables());
            var compiled = CSharpCompiledBackend.Compile(key, code, "statements", context)(context);
            Assert.That(compiled.Value, Is.EqualTo(3));
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(1));
        }

        [Test]
        public void ReflectionEmitCreatesInterfaceImplementationAndProperty()
        {
            var engine = new ReflectionEmitEngine();
            var spec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicIncrementer",
                interfaces = new[] { typeof(IExecutionEmitFixture).FullName },
                properties = new[] { new DynamicPropertySpec { name = "Name", typeName = "System.String" } },
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Increment",
                        returnType = "System.Int32",
                        parameters = new[] { new DynamicParameterSpec { name = "value", typeName = "System.Int32" } },
                        implements = typeof(IExecutionEmitFixture).FullName + ".Increment",
                        body = "return value + 1;",
                    },
                },
            };
            string hash = Guid.NewGuid().ToString("N");
            Action firstCleanup = null;
            var emitted = engine.Emit(spec, hash, "reject", "s.test.domain", _ => null, cleanup => firstCleanup = cleanup);
            var instance = (IExecutionEmitFixture)Activator.CreateInstance(emitted.Type);
            Assert.That(instance.Increment(4), Is.EqualTo(5));
            emitted.Type.GetProperty("Name").SetValue(instance, "dynamic");
            Assert.That(emitted.Type.GetProperty("Name").GetValue(instance), Is.EqualTo("dynamic"));
            firstCleanup?.Invoke();

            Action secondCleanup = null;
            var cached = engine.Emit(spec, hash, "reject", "s.other.domain", _ => null, cleanup => secondCleanup = cleanup);
            Assert.That(cached.CacheHit, Is.True);
            Assert.That(((IExecutionEmitFixture)Activator.CreateInstance(cached.Type)).Increment(8), Is.EqualTo(9));
            secondCleanup?.Invoke();
        }

        [Test]
        public void ReflectionEmitPlainMethodFormatsSignatureWithoutInspectingOpenMethodBuilder()
        {
            var engine = new ReflectionEmitEngine();
            Assert.That(engine.Probe().Supported, Is.True);
            var spec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicPlainMethod_" + Guid.NewGuid().ToString("N"),
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Read",
                        returnType = "System.Int32",
                        body = "return 3;",
                    },
                },
            };
            var emitted = engine.Emit(spec, Guid.NewGuid().ToString("N"), "reject", "session-plain-method", null, null);
            Assert.That(emitted.ImplementedMembers, Has.Length.EqualTo(1));
            Assert.That(emitted.ImplementedMembers[0], Does.Contain(".Read()"));
            var instance = Activator.CreateInstance(emitted.Type);
            DynamicMethodDispatcher.BindInstance(instance, "session-plain-method");
            Assert.That(emitted.Type.GetMethod("Read").Invoke(instance, null), Is.EqualTo(3));
        }

        [Test]
        public void ReflectionEmitV2BodyAllowsSynchronousExceptionsGenericsAndArrays()
        {
            var engine = new ReflectionEmitEngine();
            var spec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicV2Body_" + Guid.NewGuid().ToString("N"),
                interfaces = new[] { typeof(IExecutionEmitFixture).FullName },
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Increment", returnType = "System.Int32",
                        parameters = new[] { new DynamicParameterSpec { name = "value", typeName = "System.Int32" } },
                        implements = typeof(IExecutionEmitFixture).FullName + ".Increment",
                        body = "try { var values = new int[,] { { value, value + 1 } }; return System.Linq.Enumerable.First<int>(new int[] { values[0,1] }); } catch { return -1; } finally { var cleaned = true; }",
                    },
                },
            };
            Action cleanup = null;
            var emitted = engine.Emit(spec, Guid.NewGuid().ToString("N"), "reject", "s.emit.v2", _ => null, action => cleanup = action);
            Assert.That(((IExecutionEmitFixture)Activator.CreateInstance(emitted.Type)).Increment(4), Is.EqualTo(5));
            cleanup?.Invoke();

            spec.typeName = "UPilot.Tests.DynamicV2ClosureRejected_" + Guid.NewGuid().ToString("N");
            spec.methods[0].body = "var callback = () => value; return callback();";
            var unsupported = Assert.Throws<ExecutionContractException>(() => engine.Emit(
                spec, Guid.NewGuid().ToString("N"), "reject", "s.emit.v2.reject", _ => null, _ => { }));
            Assert.That(unsupported.Code, Is.EqualTo("CSHARP_EMIT_UNSUPPORTED_NODE"));
        }

        [Test]
        public void ReflectionEmitCanRequireDirectCompiledMethodBodiesWithoutInterpreterFallback()
        {
            var engine = new ReflectionEmitEngine();
            var spec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicCompiledBody_" + Guid.NewGuid().ToString("N"),
                bodyBackend = "compiled",
                interfaces = new[] { typeof(IExecutionEmitFixture).FullName },
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Increment",
                        returnType = "System.Int32",
                        parameters = new[] { new DynamicParameterSpec { name = "value", typeName = "System.Int32" } },
                        implements = typeof(IExecutionEmitFixture).FullName + ".Increment",
                        body = "var next = value + 1; return next;",
                    },
                },
            };
            Action cleanup = null;
            var emitted = engine.Emit(spec, Guid.NewGuid().ToString("N"), "reject", "s.emit.compiled",
                _ => null, action => cleanup = action);
            var instance = (IExecutionEmitFixture)Activator.CreateInstance(emitted.Type);
            Assert.That(instance.Increment(8), Is.EqualTo(9));
            cleanup?.Invoke();

            ExecutionReflectionFixture.InvocationCount = 0;
            var unsupported = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicCompiledUnsupported_" + Guid.NewGuid().ToString("N"),
                bodyBackend = "compiled",
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Run",
                        returnType = "System.Int32",
                        body = "var values = new int[1, 1]; return 0;",
                    },
                },
            };
            var error = Assert.Throws<ExecutionContractException>(() => engine.Emit(
                unsupported, Guid.NewGuid().ToString("N"), "reject", "s.emit.compiled.unsupported", _ => null, _ => { }));
            Assert.That(error.Code, Is.EqualTo("CSHARP_COMPILED_UNSUPPORTED_NODE"));
            Assert.That(ExecutionReflectionFixture.InvocationCount, Is.EqualTo(0));
        }

        [Test]
        public void ReflectionEmitSupportsCustomPropertyAndBoundedCallbackDiagnostics()
        {
            var engine = new ReflectionEmitEngine();
            var propertySpec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicCustomProperty_" + Guid.NewGuid().ToString("N"),
                fields = new[] { new DynamicFieldSpec { name = "_value", typeName = "System.Int32" } },
                properties = new[]
                {
                    new DynamicPropertySpec
                    {
                        name = "Value", typeName = "System.Int32",
                        getterBody = "return this._value + 1;", setterBody = "this._value = value;",
                    },
                },
            };
            Action propertyCleanup = null;
            string propertySession = "s.property.domain";
            var propertyType = engine.Emit(propertySpec, Guid.NewGuid().ToString("N"), "reject", propertySession, _ => null, action => propertyCleanup = action).Type;
            object propertyInstance = Activator.CreateInstance(propertyType);
            propertyType.GetProperty("Value").SetValue(propertyInstance, 4);
            Assert.That(propertyType.GetProperty("Value").GetValue(propertyInstance), Is.EqualTo(5));
            propertyCleanup();
            var expired = Assert.Throws<TargetInvocationException>(() => propertyType.GetProperty("Value").GetValue(propertyInstance));
            Assert.That(expired.InnerException, Is.TypeOf<ExecutionContractException>());

            string callbackSession = "s.callback.domain";
            var callbackSpec = new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicCallback_" + Guid.NewGuid().ToString("N"),
                interfaces = new[] { typeof(IExecutionEmitFixture).FullName },
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Increment", returnType = "System.Int32", callbackHandle = "callback",
                        parameters = new[] { new DynamicParameterSpec { name = "value", typeName = "System.Int32" } },
                        implements = typeof(IExecutionEmitFixture).FullName + ".Increment",
                        callbackPolicy = new DynamicCallbackPolicySpec { exceptionMode = "isolate", maxInvocations = 1, maxReentrancy = 2, diagnosticsCapacity = 2 },
                    },
                },
            };
            Action callbackCleanup = null;
            var callbackType = engine.Emit(callbackSpec, Guid.NewGuid().ToString("N"), "reject", callbackSession,
                _ => new Func<int, int>(value => value + 1), action => callbackCleanup = action).Type;
            var callbackInstance = (IExecutionEmitFixture)Activator.CreateInstance(callbackType);
            Assert.That(callbackInstance.Increment(2), Is.EqualTo(3));
            Assert.That(callbackInstance.Increment(2), Is.EqualTo(0));
            var stats = DynamicMethodDispatcher.GetStatistics(callbackSession);
            Assert.That(stats.Invocations, Is.EqualTo(2));
            Assert.That(stats.Rejected, Is.EqualTo(1));
            Assert.That(stats.RecentDiagnostics, Has.Length.EqualTo(1));
            callbackCleanup();
        }

        [Test]
        public void ReflectionEmitCallbackGuardsCoverIsolationPropagationReentrancyConcurrencyAndCapacity()
        {
            var engine = new ReflectionEmitEngine();

            string isolateSession = "s.isolate." + Guid.NewGuid().ToString("N");
            var isolateSpec = CallbackSpec("Isolate", new DynamicCallbackPolicySpec
            {
                exceptionMode = "isolate", maxInvocations = 100, maxReentrancy = 8, diagnosticsCapacity = 2,
            });
            Action isolateCleanup = null;
            var isolateType = engine.Emit(isolateSpec, Guid.NewGuid().ToString("N"), "reject", isolateSession,
                _ => new Func<int, int>(_ => throw new InvalidOperationException("isolated")), action => isolateCleanup = action).Type;
            var isolateInstance = (IExecutionEmitFixture)Activator.CreateInstance(isolateType);
            for (int i = 0; i < 4; i++) Assert.That(isolateInstance.Increment(i), Is.EqualTo(0));
            var isolateStats = DynamicMethodDispatcher.GetStatistics(isolateSession);
            Assert.That(isolateStats.Invocations, Is.EqualTo(4));
            Assert.That(isolateStats.Errors, Is.EqualTo(4));
            Assert.That(isolateStats.RecentDiagnostics, Has.Length.EqualTo(2));
            Assert.That(isolateStats.RecentDiagnostics.All(item => item.code == "CALLBACK_EXCEPTION"), Is.True);
            isolateCleanup();
            Assert.That(DynamicMethodDispatcher.GetStatistics(isolateSession).Invocations, Is.EqualTo(0));

            string propagateSession = "s.propagate." + Guid.NewGuid().ToString("N");
            var propagateSpec = CallbackSpec("Propagate", new DynamicCallbackPolicySpec
            {
                exceptionMode = "propagate", maxInvocations = 100, maxReentrancy = 8, diagnosticsCapacity = 4,
            });
            Action propagateCleanup = null;
            var propagateType = engine.Emit(propagateSpec, Guid.NewGuid().ToString("N"), "reject", propagateSession,
                _ => new Func<int, int>(_ => throw new InvalidOperationException("propagated")), action => propagateCleanup = action).Type;
            var propagated = Assert.Throws<InvalidOperationException>(() =>
                ((IExecutionEmitFixture)Activator.CreateInstance(propagateType)).Increment(1));
            Assert.That(propagated.Message, Is.EqualTo("propagated"));
            Assert.That(DynamicMethodDispatcher.GetStatistics(propagateSession).Errors, Is.EqualTo(1));
            propagateCleanup();
            Assert.That(DynamicMethodDispatcher.GetStatistics(propagateSession).Invocations, Is.EqualTo(0));

            string reentrantSession = "s.reentrant." + Guid.NewGuid().ToString("N");
            var reentrantSpec = CallbackSpec("Reentrant", new DynamicCallbackPolicySpec
            {
                exceptionMode = "isolate", maxInvocations = 100, maxReentrancy = 1, diagnosticsCapacity = 4,
            });
            IExecutionEmitFixture reentrantInstance = null;
            Action reentrantCleanup = null;
            var reentrantType = engine.Emit(reentrantSpec, Guid.NewGuid().ToString("N"), "reject", reentrantSession,
                _ => new Func<int, int>(value => reentrantInstance.Increment(value) + 1), action => reentrantCleanup = action).Type;
            reentrantInstance = (IExecutionEmitFixture)Activator.CreateInstance(reentrantType);
            Assert.That(reentrantInstance.Increment(1), Is.EqualTo(1));
            var reentrantStats = DynamicMethodDispatcher.GetStatistics(reentrantSession);
            Assert.That(reentrantStats.Invocations, Is.EqualTo(2));
            Assert.That(reentrantStats.Rejected, Is.EqualTo(1));
            reentrantCleanup();
            Assert.That(DynamicMethodDispatcher.GetStatistics(reentrantSession).Invocations, Is.EqualTo(0));

            string concurrentSession = "s.concurrent." + Guid.NewGuid().ToString("N");
            var concurrentSpec = CallbackSpec("Concurrent", new DynamicCallbackPolicySpec
            {
                exceptionMode = "isolate", maxInvocations = 1000, maxReentrancy = 8, diagnosticsCapacity = 4,
            });
            Action concurrentCleanup = null;
            var concurrentType = engine.Emit(concurrentSpec, Guid.NewGuid().ToString("N"), "reject", concurrentSession,
                _ => new Func<int, int>(value => value + 1), action => concurrentCleanup = action).Type;
            var concurrentInstance = (IExecutionEmitFixture)Activator.CreateInstance(concurrentType);
            Parallel.For(0, 64, index => Assert.That(concurrentInstance.Increment(index), Is.EqualTo(index + 1)));
            Assert.That(DynamicMethodDispatcher.GetStatistics(concurrentSession).Invocations, Is.EqualTo(64));
            concurrentCleanup();
            Assert.That(DynamicMethodDispatcher.GetStatistics(concurrentSession).Invocations, Is.EqualTo(0));
        }

        [Test]
        public void ReflectionEmitCacheHitUsesIndependentSessionCallbackGuardsAndLeases()
        {
            var engine = new ReflectionEmitEngine();
            var registry = new ExecutionSessionRegistry();
            var firstSession = registry.Open("cache-first", 600, 8, 4, 4);
            var secondSession = registry.Open("cache-second", 600, 8, 4, 4);
            var spec = CallbackSpec("Cache", new DynamicCallbackPolicySpec
            {
                exceptionMode = "isolate", maxInvocations = 10, maxReentrancy = 4, diagnosticsCapacity = 4,
            });
            string hash = Guid.NewGuid().ToString("N");
            var first = engine.Emit(spec, hash, "reject", firstSession.Id,
                _ => new Func<int, int>(value => value + 1), firstSession.RegisterCleanup);
            var firstInstance = (IExecutionEmitFixture)Activator.CreateInstance(first.Type);
            DynamicMethodDispatcher.BindInstance(firstInstance, firstSession.Id);
            Assert.That(firstInstance.Increment(1), Is.EqualTo(2));

            var second = engine.Emit(spec, hash, "reject", secondSession.Id,
                _ => new Func<int, int>(value => value + 10), secondSession.RegisterCleanup);
            Assert.That(second.CacheHit, Is.True);
            var secondInstance = (IExecutionEmitFixture)Activator.CreateInstance(second.Type);
            DynamicMethodDispatcher.BindInstance(secondInstance, secondSession.Id);
            Assert.That(secondInstance.Increment(1), Is.EqualTo(11));
            Assert.That(DynamicMethodDispatcher.GetStatistics(firstSession.Id).Invocations, Is.EqualTo(1));
            Assert.That(DynamicMethodDispatcher.GetStatistics(secondSession.Id).Invocations, Is.EqualTo(1));

            registry.Close(firstSession.Id);
            var expired = Assert.Throws<ExecutionContractException>(() => firstInstance.Increment(1));
            Assert.That(expired.Code, Is.EqualTo("EMIT_CALLBACK_EXPIRED"));
            Assert.That(secondInstance.Increment(2), Is.EqualTo(12));
            Assert.That(DynamicMethodDispatcher.GetStatistics(secondSession.Id).Invocations, Is.EqualTo(2));
            registry.Close(secondSession.Id);
            Assert.That(DynamicMethodDispatcher.GetStatistics(secondSession.Id).Invocations, Is.EqualTo(0));
        }

        [Test]
        public void ReflectionEmitMixedPropertyAccessorsAndInvalidSpecsAreDeterministic()
        {
            var engine = new ReflectionEmitEngine();
            string firstSession = "s.property.mixed.first." + Guid.NewGuid().ToString("N");
            var automaticGetterSpec = MixedPropertySpec("AutomaticGetter", getterBody: "", setterBody: "this._value = value;");
            Action firstCleanup = null;
            var automaticGetterType = engine.Emit(automaticGetterSpec, Guid.NewGuid().ToString("N"), "reject", firstSession,
                _ => null, action => firstCleanup = action).Type;
            object automaticGetter = Activator.CreateInstance(automaticGetterType);
            automaticGetterType.GetProperty("Value").SetValue(automaticGetter, 7);
            Assert.That(automaticGetterType.GetProperty("Value").GetValue(automaticGetter), Is.EqualTo(7));
            firstCleanup();

            string secondSession = "s.property.mixed.second." + Guid.NewGuid().ToString("N");
            var automaticSetterSpec = MixedPropertySpec("AutomaticSetter", getterBody: "return this._value + 1;", setterBody: "");
            Action secondCleanup = null;
            var automaticSetterType = engine.Emit(automaticSetterSpec, Guid.NewGuid().ToString("N"), "reject", secondSession,
                _ => null, action => secondCleanup = action).Type;
            object automaticSetter = Activator.CreateInstance(automaticSetterType);
            automaticSetterType.GetProperty("Value").SetValue(automaticSetter, 7);
            Assert.That(automaticSetterType.GetProperty("Value").GetValue(automaticSetter), Is.EqualTo(8));
            secondCleanup();

            var disabledGetter = MixedPropertySpec("DisabledGetter", getterBody: "return 1;", setterBody: "");
            disabledGetter.properties[0].hasGetter = false;
            var invalidGetter = Assert.Throws<ExecutionContractException>(() => engine.Emit(
                disabledGetter, Guid.NewGuid().ToString("N"), "reject", "s.invalid.getter", _ => null, _ => { }));
            Assert.That(invalidGetter.Code, Is.EqualTo("EMIT_INVALID_SPEC"));

            var mismatchedBacking = MixedPropertySpec("MismatchedBacking", getterBody: "", setterBody: "");
            mismatchedBacking.fields[0].typeName = "System.String";
            var mismatch = Assert.Throws<ExecutionContractException>(() => engine.Emit(
                mismatchedBacking, Guid.NewGuid().ToString("N"), "reject", "s.invalid.backing", _ => null, _ => { }));
            Assert.That(mismatch.Code, Is.EqualTo("EMIT_INVALID_SPEC"));

            var unsupported = MixedPropertySpec("UnsupportedAwait", getterBody: "return await value;", setterBody: "");
            var unsupportedError = Assert.Throws<ExecutionContractException>(() => engine.Emit(
                unsupported, Guid.NewGuid().ToString("N"), "reject", "s.invalid.await", _ => null, _ => { }));
            Assert.That(unsupportedError.Code, Is.EqualTo("CSHARP_EMIT_UNSUPPORTED_NODE"));
            AssertSpan(unsupportedError, "policy");
        }

        [Test]
        public void SessionCleanupReleasesInfrastructureWithoutDisposingUserObjects()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("ordinary-object", 600, 8, 2, 2);
            var disposable = new ExecutionDisposableFixture();
            session.Store("object", disposable);
            var closed = registry.Close(session.Id);
            Assert.That(closed.releasedHandles, Is.EqualTo(1));
            Assert.That(disposable.Disposed, Is.False);
            Assert.That(session.CleanupCount, Is.EqualTo(0));
            Assert.That(session.SubscriptionCount, Is.EqualTo(0));
        }

        [Test]
        public void SessionCloseInvalidatesHandlesAndReportsDeferredTypeRelease()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("test", 600, 8, 2, 2);
            string handle = session.Store("type", typeof(string));
            Assert.That(registry.Resolve(session.Id, handle, "type"), Is.EqualTo(typeof(string)));
            var closed = registry.Close(session.Id);
            Assert.That(closed.releasedHandles, Is.EqualTo(1));
            Assert.That(closed.dynamicTypesAwaitDomainReload, Is.EqualTo(1));
            Assert.Throws<ExecutionContractException>(() => registry.Resolve(session.Id, handle));
        }

        private static DynamicTypeSpec CallbackSpec(string suffix, DynamicCallbackPolicySpec policy)
        {
            return new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicCallback" + suffix + "_" + Guid.NewGuid().ToString("N"),
                interfaces = new[] { typeof(IExecutionEmitFixture).FullName },
                methods = new[]
                {
                    new DynamicMethodSpec
                    {
                        name = "Increment", returnType = "System.Int32", callbackHandle = "callback",
                        parameters = new[] { new DynamicParameterSpec { name = "value", typeName = "System.Int32" } },
                        implements = typeof(IExecutionEmitFixture).FullName + ".Increment",
                        callbackPolicy = policy,
                    },
                },
            };
        }

        private static DynamicTypeSpec MixedPropertySpec(string suffix, string getterBody, string setterBody)
        {
            return new DynamicTypeSpec
            {
                typeName = "UPilot.Tests.DynamicMixedProperty" + suffix + "_" + Guid.NewGuid().ToString("N"),
                fields = new[] { new DynamicFieldSpec { name = "_value", typeName = "System.Int32" } },
                properties = new[]
                {
                    new DynamicPropertySpec
                    {
                        name = "Value", typeName = "System.Int32", backingField = "_value",
                        getterBody = getterBody, setterBody = setterBody,
                    },
                },
            };
        }

        private static void AssertSpan(ExecutionContractException error, string stage)
        {
            Assert.That(error.Detail.ContainsKey("stage"), Is.True, error.Message);
            Assert.That(error.Detail["stage"], Is.EqualTo(stage));
            Assert.That(error.Detail.ContainsKey("sourceSpan"), Is.True, error.Message);
            Assert.That(error.Detail["sourceSpan"], Is.TypeOf<ExecutionSourceSpan>());
            var span = (ExecutionSourceSpan)error.Detail["sourceSpan"];
            Assert.That(span.start, Is.GreaterThanOrEqualTo(0));
            Assert.That(span.length, Is.GreaterThan(0));
            Assert.That(span.end, Is.EqualTo(span.start + span.length));
            Assert.That(span.line, Is.GreaterThanOrEqualTo(1));
            Assert.That(span.column, Is.GreaterThanOrEqualTo(1));
            Assert.That(span.endLine, Is.GreaterThanOrEqualTo(span.line));
            Assert.That(span.endColumn, Is.GreaterThanOrEqualTo(1));
        }
    }
}
