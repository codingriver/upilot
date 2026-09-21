using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotReflectionPreflightTests
    {
        public static int Calls;
        public static int GetterCalls;
        public static UPilotReflectionPreflightTests Target
        {
            get { GetterCalls++; return new UPilotReflectionPreflightTests(); }
        }
        public static int Count() { Calls++; return Calls; }
        public static int Throw() { Calls++; throw new InvalidOperationException("P0_TARGET_FAILURE"); }
        public static void MutateThenThrow() { Calls++; throw new InvalidOperationException("P0_TARGET_THROW"); }
        public static Task<int> Slow() { Calls++; return new TaskCompletionSource<int>().Task; }
        public static object BadSummary() { Calls++; return new ThrowingSummary(); }
        public int InstanceCount() { Calls++; return Calls; }
        private static void AcceptObjects(ThrowingSummary[] values) { Calls++; }
        private sealed class ThrowingSummary
        {
            public override string ToString() => throw new InvalidOperationException("P0_SERIALIZE_FAILURE");
        }

        private static ReflectionCallPayload Payload(string method = "Count") =>
            new ReflectionCallPayload { typeName = typeof(UPilotReflectionPreflightTests).FullName, methodName = method };

        [SetUp] public void Reset() { Calls = GetterCalls = 0; }

        [TestCase("await")]
        [TestCase("result")]
        [TestCase("arguments")]
        public void InvalidRequestNeverQueuesOrResolvesGetter(string invalid)
        {
            var p = Payload("InstanceCount");
            p.isStatic = false;
            p.targetStaticTypeName = typeof(UPilotReflectionPreflightTests).FullName;
            p.targetStaticMemberPath = "Target";
            if (invalid == "await") p.awaitMode = "none";
            if (invalid == "result") p.resultMode = "unknown";
            if (invalid == "arguments") p.argumentsJson = "{\"items\":[{\"direction\":\"wrong\"}]}";
            int queued = 0;
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(p, CancellationToken.None, action => { queued++; action(); }));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            Assert.That(queued, Is.Zero);
            Assert.That(Calls, Is.Zero);
            Assert.That(GetterCalls, Is.Zero);
        }

        [Test]
        public void LegalCallExecutesExactlyOnce()
        {
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var result = service.ExecuteCallAsync(Payload(), CancellationToken.None, action => action()).GetAwaiter().GetResult();
            Assert.That(result.result, Is.EqualTo("1"));
            Assert.That(Calls, Is.EqualTo(1));
        }

        [TestCase("Throw")]
        [TestCase("BadSummary")]
        public void PostInvocationFailurePreservesSideEffects(string method)
        {
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var p = Payload(method);
            p.awaitTimeoutMs = 1;
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(p, CancellationToken.None, action => action()));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(Calls, Is.EqualTo(1));
        }

        [Test]
        public void TargetFailurePreservesOriginalEvidenceAcrossRepeatedWrapping()
        {
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(Payload("MutateThenThrow"), CancellationToken.None, action => action()));
            var wrappedAgain = UPilotReflectionService.CallError(error, false);
            var wire = UPilotExecutionService.ToErrorDetail(wrappedAgain, "cmd-p0-target", "reflection.call");

            Assert.That(Calls, Is.EqualTo(1));
            Assert.That(wrappedAgain.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(wire.sideEffectsMayHaveOccurred, Is.True);
            Assert.That(wire.exceptionType, Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(wire.exceptionMessage, Does.Contain("P0_TARGET_THROW"));
            Assert.That(wire.stackTrace, Does.Contain(nameof(MutateThenThrow)));
            Assert.That(wire.commandId, Is.EqualTo("cmd-p0-target"));
        }

        [UnityTest]
        public IEnumerator AwaitTimeoutPreservesSideEffectsWithoutBlockingEditor()
        {
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var p = Payload("Slow");
            p.awaitTimeoutMs = 1;
            var task = service.ExecuteCallAsync(p, CancellationToken.None, action => action());
            while (!task.IsCompleted) yield return null;
            Assert.That(task.IsFaulted, Is.True);
            var error = task.Exception?.InnerException as ExecutionContractException;
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Code, Is.EqualTo("AWAITABLE_TIMEOUT"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(Calls, Is.EqualTo(1));
        }

        [Test]
        public void CancellationBeforeDispatchHasNoSideEffects()
        {
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(Payload(), new CancellationToken(true), action => action()));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            Assert.That(Calls, Is.Zero);
        }

        [Test]
        public void CompletedCallRetainsBoundaryForTransportFailure()
        {
            var boundary = new UPilotReflectionService.CallExecutionBoundary();
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            service.ExecuteCallAsync(Payload(), CancellationToken.None, action => action(), boundary).GetAwaiter().GetResult();
            var error = UPilotReflectionService.CallError(new InvalidOperationException("send failed"),
                boundary.SideEffectsMayHaveOccurred);
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(Calls, Is.EqualTo(1));
        }

        [TestCase("Missing.P0.Type", "literal")]
        [TestCase("Missing.P0.Type", "null")]
        [TestCase("", "type")]
        [TestCase("System.Int32", "array")]
        public void AllArgumentMetadataIsCheckedBeforeCustomDecoding(string typeName, string kind)
        {
            var execution = new UPilotExecutionService(null);
            var p = Payload("InstanceCount");
            p.isStatic = false;
            p.targetStaticTypeName = typeof(UPilotReflectionPreflightTests).FullName;
            p.targetStaticMemberPath = "Target";
            p.argumentsJson = "{\"items\":[{\"value\":{\"kind\":\"literal\",\"typeName\":\""
                + typeof(ThrowingSummary).FullName + "\",\"valueJson\":\"{}\"}},{\"value\":{\"kind\":\""
                + kind + "\",\"typeName\":\"" + typeName + "\"}}]}";
            var service = new UPilotReflectionService(null, execution);
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(p, CancellationToken.None, action => action()));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            Assert.That(Calls, Is.Zero);
            Assert.That(GetterCalls, Is.Zero);
        }

        [Test]
        public void UnsupportedArrayElementIsRejectedBeforeExecutionBoundary()
        {
            var p = Payload("AcceptObjects");
            p.argumentsJson = "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\""
                + typeof(ThrowingSummary).FullName
                + "[]\",\"items\":[{\"kind\":\"literal\",\"typeName\":\"System.String\",\"valueJson\":\"\\\"invalid-object\\\"\"}]}}]}";
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(p, CancellationToken.None, action => action()));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            Assert.That(Calls, Is.Zero);
            Assert.That(GetterCalls, Is.Zero);
        }

        [TestCase("Count", "")]
        [TestCase("AcceptObjects", "missingParameter")]
        public void InvalidArgumentShapePrecedesCustomConversion(string method, string argumentName)
        {
            var p = Payload(method);
            p.argumentsJson = "{\"items\":[{\"name\":\"" + argumentName
                + "\",\"value\":{\"kind\":\"literal\",\"typeName\":\"" + typeof(ThrowingSummary).FullName
                + "\",\"valueJson\":\"\\\"invalid-object\\\"\"}}]}";
            var service = new UPilotReflectionService(null, new UPilotExecutionService(null));
            var error = Assert.ThrowsAsync<ExecutionContractException>(async () =>
                await service.ExecuteCallAsync(p, CancellationToken.None, action => action()));
            Assert.That(error.Code, Is.EqualTo("REFLECTION_BIND_FAILED"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            Assert.That(Calls, Is.Zero);
        }
    }

    public class UPilotTypedResultTests
    {
        [Test]
        public void SupportedArraysEncodeAsCompleteUtf8Json()
        {
            var service = new UPilotExecutionService(null);
            var result = service.EncodeResult(new[] { "a", null, "\u4e2d\n" }, "", "inline", 1024);
            Assert.That(result.kind, Is.EqualTo("array"));
            Assert.That(result.typeName, Is.EqualTo("System.String[]"));
            Assert.That(result.valueJson, Is.EqualTo("[\"a\",null,\"中\\n\"]"));
            Assert.That(result.serializationStatus, Is.EqualTo("inline"));
            Assert.That(result.actualBytes, Is.EqualTo(System.Text.Encoding.UTF8.GetByteCount(result.valueJson)));
        }

        [Test]
        public void InlineOverflowNeverReturnsTruncatedJson()
        {
            var service = new UPilotExecutionService(null);
            var error = Assert.Throws<ExecutionContractException>(() =>
                service.EncodeResult(new string('x', 2048), "", "inline", 1024));
            Assert.That(error.Code, Is.EqualTo("RESULT_TOO_LARGE"));
            Assert.That(error.Detail["actualBytes"], Is.GreaterThan(1024));
        }

        [Test]
        public void ExplicitHandleDoesNotSilentlyInlinePureValues()
        {
            var service = new UPilotExecutionService(null);
            var error = Assert.Throws<ExecutionContractException>(() =>
                service.EncodeResult(42, "", "handle"));
            Assert.That(error.Code, Is.EqualTo("SESSION_REQUIRED"));
            var nullResult = service.EncodeResult(null, "", "handle");
            Assert.That(nullResult.serializationStatus, Is.EqualTo("null"));
            Assert.That(nullResult.handle, Is.Empty);
        }

        [Test]
        public void NonFiniteNumbersAreNotEncodedAsInvalidJson()
        {
            var service = new UPilotExecutionService(null);
            var error = Assert.Throws<ExecutionContractException>(() =>
                service.EncodeResult(double.PositiveInfinity, "", "inline"));
            Assert.That(error.Code, Is.EqualTo("RESULT_NOT_INLINEABLE"));
        }
    }

    public class UPilotTypedInputTests
    {
        [Test]
        public void TypedReaderRejectsDuplicateFieldsBeforeDtoConstruction()
        {
            var error = Assert.Throws<ExecutionContractException>(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"kind\":\"literal\",\"valueJson\":\"1\"}}]}"));
            Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
            Assert.That(error.Detail["path"], Is.EqualTo("$.items[0].value"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
        }

        [Test]
        public void TypedReaderRejectsInvalidArrayShapeBeforeConversion()
        {
            var error = Assert.Throws<ExecutionContractException>(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.String[]\"}}]}"));
            Assert.That(error.Detail["path"], Is.EqualTo("$.items[0].value.items"));
        }

        [Test]
        public void TypedReaderAcceptsAOneDimensionalStringArray()
        {
            Assert.DoesNotThrow(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.String[]\",\"items\":[{\"kind\":\"literal\",\"valueJson\":\"\\\"a\\\"\"},{\"kind\":\"null\"}]}}]}"));
        }

        [Test]
        public void TypedReaderAllowsOnlyEmptyDtoItemsForNonArrayValues()
        {
            Assert.DoesNotThrow(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"typeName\":\"System.String\",\"valueJson\":\"\\\"a\\\"\",\"items\":[]}}]}"));

            foreach (string wire in new[]
            {
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"items\":[{\"kind\":\"null\"}]}}]}",
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"items\":{}}}]}",
            })
            {
                var error = Assert.Throws<ExecutionContractException>(() => UPilotExecutionService.ValidateArgumentStructure(wire));
                Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
                Assert.That(error.Detail["path"], Is.EqualTo("$.items[0].value.items"));
                Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
            }
        }

        [Test]
        public void TypedReaderRejectsUnknownFieldsAndInvalidWireTypes()
        {
            var unknown = Assert.Throws<ExecutionContractException>(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"mystery\":true}}]}"));
            Assert.That(unknown.Code, Is.EqualTo("INVALID_PARAMS"));
            Assert.That(unknown.Detail["path"], Is.EqualTo("$.items[0].value.mystery"));

            var instanceId = Assert.Throws<ExecutionContractException>(() => UPilotExecutionService.ValidateArgumentStructure(
                "{\"items\":[{\"value\":{\"kind\":\"unityobject\",\"instanceId\":\"12\"}}]}"));
            Assert.That(instanceId.Detail["path"], Is.EqualTo("$.items[0].value.instanceId"));
            Assert.That(instanceId.Detail["sideEffectsMayHaveOccurred"], Is.False);
        }

        [Test]
        public void TypedReaderRejectsUnsupportedArraysAndNullValueElementsBeforeConversion()
        {
            var service = new UPilotExecutionService(null);
            var unsupported = Assert.Throws<ExecutionContractException>(() => service.ValidateArgumentBindings(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.Object[]\",\"items\":[]}}]}", ""));
            Assert.That(unsupported.Code, Is.EqualTo("INVALID_PARAMS"));

            var nullValue = Assert.Throws<ExecutionContractException>(() => service.ValidateArgumentBindings(
                "{\"items\":[{\"value\":{\"kind\":\"array\",\"typeName\":\"System.Int32[]\",\"items\":[{\"kind\":\"null\"}]}}]}", ""));
            Assert.That(nullValue.Code, Is.EqualTo("INVALID_PARAMS"));
        }

        [Test]
        public void TypedReaderPreservesPrimitiveArrayValuesAndRejectsInvalidNumbersAndChars()
        {
            var service = new UPilotExecutionService(null);
            var decoded = service.DecodeArguments(
                "{\"items\":["
                + "{\"name\":\"longs\",\"value\":{\"kind\":\"array\",\"typeName\":\"System.Int64[]\",\"items\":[{\"kind\":\"literal\",\"typeName\":\"System.Int64\",\"valueJson\":\"9223372036854775807\"}]}},"
                + "{\"name\":\"decimals\",\"value\":{\"kind\":\"array\",\"typeName\":\"System.Decimal[]\",\"items\":[{\"kind\":\"literal\",\"typeName\":\"System.Decimal\",\"valueJson\":\"79228162514264337593543950335\"}]}},"
                + "{\"name\":\"flags\",\"value\":{\"kind\":\"array\",\"typeName\":\"System.Boolean[]\",\"items\":[{\"kind\":\"literal\",\"typeName\":\"System.Boolean\",\"valueJson\":\"true\"}]}},"
                + "{\"name\":\"chars\",\"value\":{\"kind\":\"array\",\"typeName\":\"System.Char[]\",\"items\":[{\"kind\":\"literal\",\"typeName\":\"System.Char\",\"valueJson\":\"\\\"中\\\"\"}]}}]}", "");
            Assert.That(((long[])decoded[0].Value)[0], Is.EqualTo(long.MaxValue));
            Assert.That(((decimal[])decoded[1].Value)[0], Is.EqualTo(decimal.MaxValue));
            Assert.That(((bool[])decoded[2].Value)[0], Is.True);
            Assert.That(((char[])decoded[3].Value)[0], Is.EqualTo('中'));

            var nonFinite = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"typeName\":\"System.Double\",\"valueJson\":\"NaN\"}}]}", ""));
            Assert.That(nonFinite.Code, Is.EqualTo("TYPED_VALUE_DECODE_FAILED"));

            var multiChar = Assert.Throws<ExecutionContractException>(() => service.DecodeArguments(
                "{\"items\":[{\"value\":{\"kind\":\"literal\",\"typeName\":\"System.Char\",\"valueJson\":\"\\\"ab\\\"\"}}]}", ""));
            Assert.That(multiChar.Code, Is.EqualTo("TYPED_VALUE_DECODE_FAILED"));
        }

        [Test]
        public void TypedReaderUsesBusinessDepthSixtyFourIndependentOfReaderQuota()
        {
            Assert.DoesNotThrow(() => UPilotExecutionService.ValidateArgumentStructure(BuildNestedArray(64)));
            var error = Assert.Throws<ExecutionContractException>(() =>
                UPilotExecutionService.ValidateArgumentStructure(BuildNestedArray(65)));
            Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
            Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False);
        }

        [Test]
        public void TypedReaderBuildsRecursiveDtoFromTheValidatedTree()
        {
            var envelope = UPilotExecutionService.ParseArguments(BuildNestedArray(8));
            var value = envelope.items.Single().value;
            for (int index = 0; index < 8; index++)
            {
                Assert.That(value.kind, Is.EqualTo("array"));
                Assert.That(value.items, Has.Length.EqualTo(1));
                value = value.items[0];
            }
            Assert.That(value.kind, Is.EqualTo("literal"));
            Assert.That(value.typeName, Is.EqualTo("System.Int32"));
            Assert.That(value.valueJson, Is.EqualTo("1"));
        }

        private static string BuildNestedArray(int depth)
        {
            string value = "{\"kind\":\"literal\",\"typeName\":\"System.Int32\",\"valueJson\":\"1\"}";
            for (int index = 0; index < depth; index++)
                value = "{\"kind\":\"array\",\"typeName\":\"System.Int32[]\",\"items\":[" + value + "]}";
            return "{\"items\":[{\"value\":" + value + "}]}";
        }
    }

    public class UPilotRawExecutionHandlerTests
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private ExecutionContractException _error;
        private object _result;
        private int _responses;
        private int _queued;
        private bool _failSend;

        [SetUp]
        public void Reset()
        {
            _error = null;
            _result = null;
            _responses = _queued = 0;
            _failSend = false;
            UPilotReflectionPreflightTests.Calls = UPilotReflectionPreflightTests.GetterCalls = 0;
        }

        private void Enqueue(Action action) { _queued++; _queue.Enqueue(action); }
        private Task Result(object result)
        {
            if (_failSend) throw new InvalidOperationException("P0_SEND_FAILURE");
            _responses++;
            _result = result;
            return Task.CompletedTask;
        }
        private Task Error(ExecutionContractException error)
        {
            _responses++;
            _error = error;
            return Task.CompletedTask;
        }

        private IEnumerator Dispatch(string command, string json, CancellationToken token = default)
        {
            var router = new UPilotCommandRouter();
            var execution = new UPilotExecutionService(null);
            if (command == "reflection.call")
            {
                var reflection = new UPilotReflectionService(null, execution);
                router.Register(command, (id, raw, cancellation) =>
                    reflection.HandleCallAsync(id, raw, cancellation, Enqueue, value => Result(value), Error));
            }
            else
                router.Register(command, (id, raw, cancellation) =>
                    execution.HandleCSharpEvalAsync(id, raw, cancellation, Enqueue, value => Result(value), Error,
                        action => action()));
            var task = router.TryHandleAsync(command, "p0-raw-" + Guid.NewGuid(), json, token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (_queue.TryDequeue(out var action)) action();
                yield return null;
            }
            Assert.That(task.IsCompleted, Is.True, "Raw handler did not complete within the bounded fixture.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(task.Result, Is.True);
            Assert.That(_responses, Is.EqualTo(1));
        }

        private static ReflectionCallPayload Method(string method = "Count") => new ReflectionCallPayload
        {
            typeName = typeof(UPilotReflectionPreflightTests).FullName,
            methodName = method,
        };
        private static string Wire(ReflectionCallPayload payload) =>
            JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });
        private static string Wire(CSharpEvalPayload payload) =>
            JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

        [UnityTest]
        public IEnumerator RawMethodInvalidModesAndArgumentsNeverResolveTarget()
        {
            foreach (var invalid in new[] { "await", "result", "typed", "conflict", "handle" })
            {
                Reset();
                var payload = Method("InstanceCount");
                payload.isStatic = false;
                payload.targetStaticTypeName = typeof(UPilotReflectionPreflightTests).FullName;
                payload.targetStaticMemberPath = "Target";
                if (invalid == "await") payload.awaitMode = "invalid";
                if (invalid == "result") payload.resultMode = "invalid";
                if (invalid == "typed") payload.argumentsJson = "{\"items\":[{\"value\":{\"kind\":\"invalid\"}}]}";
                if (invalid == "conflict") { payload.argumentsJson = "{\"items\":[]}"; payload.parameters = new[] { "1" }; }
                if (invalid == "handle") payload.resultMode = "handle";
                yield return Dispatch("reflection.call", Wire(payload));
                Assert.That(_error, Is.Not.Null, invalid);
                Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.False, invalid);
                Assert.That(_queued, Is.Zero, invalid);
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.Zero);
                Assert.That(UPilotReflectionPreflightTests.GetterCalls, Is.Zero);
            }
        }

        [UnityTest]
        public IEnumerator RawMethodSuccessAndPostInvocationErrorsExecuteOnce()
        {
            foreach (var method in new[] { "Count", "Throw", "Slow", "BadSummary", "send" })
            {
                Reset();
                _failSend = method == "send";
                var payload = Method(_failSend ? "Count" : method);
                payload.awaitTimeoutMs = 1;
                yield return Dispatch("reflection.call", Wire(payload));
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.EqualTo(1), method);
                if (method == "Count")
                    Assert.That(_result, Is.Not.Null);
                else
                {
                    Assert.That(_error, Is.Not.Null, method);
                    Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.True, method);
                }
            }
        }

        [UnityTest]
        public IEnumerator RawMethodTargetFailurePreservesOriginalEvidence()
        {
            yield return Dispatch("reflection.call", Wire(Method("MutateThenThrow")));
            Assert.That(UPilotReflectionPreflightTests.Calls, Is.EqualTo(1));
            Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(_error.Detail["exceptionType"], Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(_error.Detail["exceptionMessage"], Does.Contain("P0_TARGET_THROW"));
            Assert.That(_error.Detail["stackTrace"], Does.Contain(nameof(UPilotReflectionPreflightTests.MutateThenThrow)));
        }

        [UnityTest]
        public IEnumerator RawExpressionRejectsInvalidMetadataBeforeCustomDecoding()
        {
            foreach (var invalid in new[] { "mode", "backend", "result", "handle", "type", "typed", "parse", "limits",
                "unknownBudget", "stringBudget", "fractionBudget", "boolBudget", "duplicateBudget", "arrayBudget" })
            {
                Reset();
                var payload = new CSharpEvalPayload
                {
                    code = "1",
                    variablesJson = "{\"items\":[{\"name\":\"first\",\"value\":{\"typeName\":\""
                        + typeof(SummaryProbe).FullName + "\",\"valueJson\":\"\\\"invalid-object\\\"\"}}]}",
                };
                if (invalid == "mode") payload.mode = "bad";
                if (invalid == "backend") payload.executionBackend = "bad";
                if (invalid == "result") payload.resultMode = "bad";
                if (invalid == "handle") payload.resultMode = "handle";
                if (invalid == "parse") payload.code = "1 +";
                if (invalid == "limits") payload.limitsJson = "{";
                if (invalid == "unknownBudget") payload.limitsJson = "{\"unrecognized\":1}";
                if (invalid == "stringBudget") payload.limitsJson = "{\"timeoutMs\":\"100\"}";
                if (invalid == "fractionBudget") payload.limitsJson = "{\"timeoutMs\":1.5}";
                if (invalid == "boolBudget") payload.limitsJson = "{\"timeoutMs\":true}";
                if (invalid == "duplicateBudget") payload.limitsJson = "{\"timeoutMs\":1,\"timeoutMs\":2}";
                if (invalid == "arrayBudget") payload.limitsJson = "[]";
                if (invalid == "type") payload.variablesJson = payload.variablesJson.Replace("}]}",
                    "},{\"name\":\"second\",\"value\":{\"kind\":\"type\",\"typeName\":\"Missing.P0.Type\"}}]}");
                if (invalid == "typed") payload.variablesJson = "{\"items\":[{\"name\":\"v\",\"value\":{\"kind\":\"bad\"}}]}";
                yield return Dispatch("csharp.eval", Wire(payload));
                Assert.That(_error, Is.Not.Null, invalid);
                Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.False, invalid);
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.Zero);
                Assert.That(UPilotReflectionPreflightTests.GetterCalls, Is.Zero);
            }
        }

        [UnityTest]
        public IEnumerator RawExpressionKeepsLegacyOptionsAndBoundedBudgetDefaults()
        {
            var payload = new CSharpEvalPayload
            {
                code = "1 + 2", mode = "expression", languageProfileMode = "reflection-expression",
                limitsJson = "{\"timeoutMs\":-1,\"maxCalls\":200000,\"resultMode\":\" JSON \"}",
            };
            yield return Dispatch("csharp.eval", Wire(payload));
            Assert.That(_error, Is.Null);
            Assert.That(((CSharpEvalResultPayload)_result).result, Is.EqualTo("3"));
            Assert.That(((CSharpEvalResultPayload)_result).sideEffectsMayHaveOccurred, Is.False);
        }

        [Serializable]
        public sealed class SummaryProbe
        {
            public override string ToString()
            {
                UPilotReflectionPreflightTests.Calls++;
                throw new InvalidOperationException("P0_VARIABLE_SUMMARY_FAILURE");
            }
        }

        [UnityTest]
        public IEnumerator RawExpressionDecodeFailureStaysFalseAndSummaryFailureTurnsTrue()
        {
            foreach (var json in new[] { "\"invalid-object\"", "{}" })
            {
                Reset();
                var payload = new CSharpEvalPayload
                {
                    code = "value",
                    mode = "expression",
                    variablesJson = JsonUtility.ToJson(new ExecutionVariablesEnvelope
                    {
                        items = new[] { new ExecutionVariableSpec { name = "value",
                            value = new TypedValueSpec { typeName = typeof(SummaryProbe).FullName, valueJson = json } } },
                    }),
                };
                yield return Dispatch("csharp.eval", Wire(payload));
                Assert.That(_error, Is.Not.Null, json);
                bool summaryExecuted = json == "{}";
                Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.EqualTo(summaryExecuted), json);
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.EqualTo(summaryExecuted ? 1 : 0), json);
                if (summaryExecuted)
                {
                    Assert.That(_error.Detail["exceptionType"], Is.EqualTo(typeof(InvalidOperationException).FullName));
                    Assert.That(_error.Detail["exceptionMessage"], Does.Contain("P0_VARIABLE_SUMMARY_FAILURE"));
                    Assert.That(_error.Detail["stackTrace"], Does.Contain(nameof(SummaryProbe.ToString)));
                }
            }
        }

        [UnityTest]
        public IEnumerator RawExpressionSuccessThrowAndSendFailureRetainInvocationCount()
        {
            foreach (var method in new[] { "Count", "Throw", "send" })
            {
                Reset();
                _failSend = method == "send";
                var payload = new CSharpEvalPayload
                {
                    code = typeof(UPilotReflectionPreflightTests).FullName + "." + (_failSend ? "Count" : method) + "()",
                    mode = "expression",
                    languageProfileMode = "reflection-expression",
                };
                yield return Dispatch("csharp.eval", Wire(payload));
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.EqualTo(1), method);
                if (method == "Count") Assert.That(_result, Is.Not.Null);
                else
                {
                    Assert.That(_error, Is.Not.Null, method);
                    Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.True, method);
                }
            }
        }

        [UnityTest]
        public IEnumerator RawExpressionTargetFailurePreservesOriginalEvidence()
        {
            var payload = new CSharpEvalPayload
            {
                code = typeof(UPilotReflectionPreflightTests).FullName + ".MutateThenThrow()",
                mode = "expression",
                languageProfileMode = "reflection-expression",
            };
            yield return Dispatch("csharp.eval", Wire(payload));
            Assert.That(UPilotReflectionPreflightTests.Calls, Is.EqualTo(1));
            Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.True);
            Assert.That(_error.Detail["exceptionType"], Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(_error.Detail["exceptionMessage"], Does.Contain("P0_TARGET_THROW"));
            Assert.That(_error.Detail["stackTrace"], Does.Contain(nameof(UPilotReflectionPreflightTests.MutateThenThrow)));
        }

        [UnityTest]
        public IEnumerator RawHandlersRejectCancellationAndMalformedWireWithoutEffects()
        {
            foreach (var command in new[] { "reflection.call", "csharp.eval" })
            {
                Reset();
                yield return Dispatch(command, "{");
                Assert.That(_error, Is.Not.Null);
                Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.False);
                Reset();
                yield return Dispatch(command, command == "reflection.call" ? Wire(Method())
                    : Wire(new CSharpEvalPayload { code = "1" }), new CancellationToken(true));
                Assert.That(_error, Is.Not.Null);
                Assert.That(_error.Detail["sideEffectsMayHaveOccurred"], Is.False);
                Assert.That(UPilotReflectionPreflightTests.Calls, Is.Zero);
            }
        }
    }
}
