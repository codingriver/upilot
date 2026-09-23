using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotExecutionResourceTests
    {
        [Test]
        public void EvalEmitValidationAllowsAsyncAndClosuresWithoutExecutingUserCode()
        {
            ExecutionThrowingGetterFixture.GetterCallCount = 0;
            foreach (string code in new[] { "return await value;", "var f = (int x) => x + 1; return f(2);",
                "return CodingRiver.UPilot.Tests.ExecutionThrowingGetterFixture.Value;" })
            {
                var result = UPilotExecutionService.ValidateCSharpPayload(new CSharpValidatePayload
                { code = code, backend = "emit", mode = "statements" });
                Assert.That(result.boundaries, Is.EqualTo(new[] { "parse", "runtime-capability" }));
                Assert.That(result.resourceDiagnostics, Is.Empty);
            }
            Assert.That(ExecutionThrowingGetterFixture.GetterCallCount, Is.Zero);
            var cache = new ExecutionProgramCache<CSharpEmitBackend.Entry>(2, "emitCache");
            var asyncProgram = CSharpEmitBackend.CompileAsyncCached(cache, "await", "return await value;", "statements");
            Assert.That(asyncProgram(new CSharpEvaluationContext(new Dictionary<string, object>
                { { "value", Task.FromResult(7) } })).GetAwaiter().GetResult().Value, Is.EqualTo(7));
            Assert.That(CSharpEmitBackend.CompileCached(cache, "closure", "var f = (int x) => x + 1; return f(2);",
                "statements")(new CSharpEvaluationContext()).Value, Is.EqualTo(3));
        }

        [Test]
        public void EmitLruSharesSyncAsyncSlotAndKeepsEvictedDelegatesAlive()
        {
            var cache = new ExecutionProgramCache<CSharpEmitBackend.Entry>(2, "emitCache");
            var notices = new ExecutionResourceDiagnostics();
            var first = CSharpEmitBackend.CompileCached(cache, "a", "return 1;", "statements", notices);
            CSharpEmitBackend.CompileAsyncCached(cache, "a", "return 1;", "statements", notices);
            Assert.That(cache.Snapshot().count, Is.EqualTo(1));
            CSharpEmitBackend.CompileCached(cache, "b", "return 2;", "statements", notices);
            CSharpEmitBackend.CompileCached(cache, "a", "return 1;", "statements", notices);
            Assert.That(cache.Contains("b"), Is.True);
            var before = cache.Snapshot();
            Assert.Throws<ExecutionContractException>(() =>
                CSharpEmitBackend.CompileCached(cache, "invalid", "return @;", "statements", notices));
            Assert.That(cache.Snapshot().count, Is.EqualTo(2));
            Assert.That(cache.Snapshot().evictions, Is.EqualTo(before.evictions));
            CSharpEmitBackend.CompileCached(cache, "c", "return 3;", "statements", notices);
            Assert.That(cache.Contains("b"), Is.False, "Contains must not refresh LRU order.");
            CSharpEmitBackend.CompileCached(cache, "d", "return 4;", "statements", notices);
            Assert.That(cache.Contains("a"), Is.False);
            Assert.That(first(new CSharpEvaluationContext()).Value, Is.EqualTo(1));
            Assert.That(notices.Snapshot().Single().code, Is.EqualTo("EVAL_CACHE_EVICTED"));
            Assert.That(notices.Snapshot().Single().count, Is.EqualTo(2));
            Assert.That(notices.Snapshot().Single().severity, Is.EqualTo("warning"));
        }

        [Test]
        public void CompiledLruRejectsUnsupportedWithoutEvictionAndPreservesErrorNotices()
        {
            var cache = new ExecutionProgramCache<CSharpCompiledBackend.Entry>(1, "compiledCache");
            var context = new CSharpEvaluationContext();
            var notices = new ExecutionResourceDiagnostics();
            var first = CSharpCompiledBackend.CompileCached(cache, "a", "return 7;", "statements", context);
            Assert.Throws<ExecutionContractException>(() =>
                CSharpCompiledBackend.CompileCached(cache, "bad", "foreach (var x in values) return x;", "statements",
                    context, diagnostics: notices));
            Assert.That(cache.Contains("a"), Is.True);
            Assert.That(notices.Snapshot(), Is.Empty);
            var program = CSharpCompiledBackend.CompileCached(cache, "b", "return 1 / divisor;", "statements",
                new CSharpEvaluationContext(new Dictionary<string, object> { { "divisor", 0 } }), diagnostics: notices);
            var failure = Assert.Throws<ExecutionContractException>(() => program(
                new CSharpEvaluationContext(new Dictionary<string, object> { { "divisor", 0 } })));
            var detail = UPilotExecutionService.ToErrorDetail(notices.Attach(failure), "req", "csharp.eval");
            Assert.That(detail.resourceDiagnostics.Single().code, Is.EqualTo("EVAL_CACHE_EVICTED"));
            Assert.That(failure.Code, Is.EqualTo("CSHARP_RUNTIME_ERROR"));
            Assert.That(first(new CSharpEvaluationContext()).Value, Is.EqualTo(7));
        }

        [Test]
        public void CacheConcurrentRequestsHaveIsolatedEvictionNoticesAndSingleFactory()
        {
            var cache = new ExecutionProgramCache<object>(1, "emitCache");
            int builds = 0;
            Parallel.For(0, 16, _ => cache.GetOrCreate("shared", value => true,
                value => { Interlocked.Increment(ref builds); return new object(); }, new ExecutionResourceDiagnostics()));
            Assert.That(builds, Is.EqualTo(1));
            Assert.That(cache.Snapshot().hits, Is.EqualTo(15));
            var requests = Enumerable.Range(0, 8).Select(_ => new ExecutionResourceDiagnostics()).ToArray();
            Parallel.For(0, requests.Length, index =>
                cache.GetOrCreate("request-" + index, value => true, value => new object(), requests[index]));
            Assert.That(requests.All(request => request.Snapshot().Single().count == 1), Is.True);
            Assert.That(cache.Snapshot().count, Is.EqualTo(1));
            Assert.That(cache.Snapshot().evictions, Is.EqualTo(8));
        }

        [Test]
        public void ResourceDiagnosticsAggregateBoundDropAndPreserveNestedError()
        {
            var notices = new ExecutionResourceDiagnostics();
            notices.Add("A", "warning", "emitCache", "evict", 2, 2, "No retry.");
            notices.Add("A", "warning", "emitCache", "evict", 2, 2, "No retry.");
            var stable = notices.Snapshot();
            for (int i = 0; i < 20; i++) notices.Add("X" + i, "warning", "compiledCache", "evict", 2, 2, "No retry.");
            Assert.That(stable.Single().count, Is.EqualTo(2));
            Assert.That(notices.Snapshot().Length, Is.EqualTo(16));
            Assert.That(notices.DroppedCount, Is.EqualTo(5));
            var original = new ExecutionContractException("BUSINESS_ERROR", "original",
                new Dictionary<string, object> { { "sideEffectsMayHaveOccurred", true }, { "nextAction", "Inspect original state." } });
            var wrapped = new ExecutionResourceDiagnostics().Attach(notices.Attach(original));
            var detail = UPilotExecutionService.ToErrorDetail(wrapped, "req", "csharp.eval");
            Assert.That(wrapped.Code, Is.EqualTo("BUSINESS_ERROR"));
            Assert.That(detail.sideEffectsMayHaveOccurred, Is.True);
            Assert.That(detail.nextAction, Is.EqualTo("Inspect original state."));
            Assert.That(detail.resourceDiagnosticsDroppedCount, Is.EqualTo(5));
            Assert.That(detail.resourceDiagnostics.Length, Is.EqualTo(16));
            StringAssert.Contains("resourceDiagnostics", JsonUtility.ToJson(detail));
        }

        [Test]
        public void WarmupFailureIsWarningAndDoesNotReplayBusiness()
        {
            var notices = new ExecutionResourceDiagnostics();
            int businessCalls = 0, warmupCalls = 0;
            int result = ++businessCalls;
            UPilotExecutionService.WarmEmitCache(() =>
            {
                warmupCalls++;
                throw new ExecutionContractException("CSHARP_EMIT_COMPILE_FAILED", "fixture");
            }, notices);
            Assert.That(result, Is.EqualTo(1));
            Assert.That(businessCalls, Is.EqualTo(1));
            Assert.That(warmupCalls, Is.EqualTo(1));
            Assert.That(notices.Snapshot().Single().code, Is.EqualTo("EVAL_CACHE_WARMUP_FAILED"));
        }

        [Test]
        public void DomainQuotaIsSharedAcrossEnginesAndAllowsCacheHitAtLimit()
        {
            var budget = new DynamicTypeBudget(1);
            var first = new ReflectionEmitEngine(budget);
            var second = new ReflectionEmitEngine(budget);
            var spec = new DynamicTypeSpec { typeName = "Quota_" + Guid.NewGuid().ToString("N") };
            var emitted = first.Emit(spec, "hash", "reject", "session-a", null, null);
            Assert.That(first.Emit(spec, "hash", "reject", "session-b", null, null).Type, Is.SameAs(emitted.Type));
            var notices = new ExecutionResourceDiagnostics();
            var error = Assert.Throws<ExecutionContractException>(() =>
                second.Emit(new DynamicTypeSpec { typeName = "Another" }, "other", "reject", "session-c", null, null, notices));
            Assert.That(error.Code, Is.EqualTo("EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED"));
            Assert.That(budget.Snapshot().used, Is.EqualTo(1));
            Assert.That(budget.Snapshot().rejected, Is.EqualTo(1));
            Assert.That(notices.Snapshot().Single().severity, Is.EqualTo("error"));
            var detail = UPilotExecutionService.ToErrorDetail(error, "req", "reflection.emitType");
            Assert.That(detail.requestedTypeName, Is.EqualTo("Another"));
            Assert.That(detail.limit, Is.EqualTo(1));
        }

        [Test]
        public void FailedTypeGenerationConsumesSlotAndPreservesOriginalError()
        {
            var budget = new DynamicTypeBudget(1);
            var engine = new ReflectionEmitEngine(budget);
            var notices = new ExecutionResourceDiagnostics();
            var error = Assert.Throws<ExecutionContractException>(() => engine.Emit(new DynamicTypeSpec
            {
                typeName = "Invalid_" + Guid.NewGuid().ToString("N"),
                methods = new[] { new DynamicMethodSpec { name = "Run", body = "return await task;" } },
            }, "invalid", "reject", "session", null, null, notices));
            Assert.That(error.Code, Is.Not.EqualTo("EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED"));
            Assert.That(budget.Snapshot().used, Is.EqualTo(1));
            Assert.That(budget.Snapshot().failedAfterReservation, Is.EqualTo(1));
            Assert.That(((ExecutionResourceDiagnostic[])error.Detail["resourceDiagnostics"]).Single().code,
                Is.EqualTo("EMIT_GENERATION_SLOT_CONSUMED"));
            Assert.That(Assert.Throws<ExecutionContractException>(() => engine.Emit(
                new DynamicTypeSpec { typeName = "Fresh" }, "fresh", "reject", "session", null, null)).Code,
                Is.EqualTo("EMIT_DOMAIN_TYPE_LIMIT_EXCEEDED"));
        }

        [Test]
        public void CapabilityProbeIsDomainCachedAndResourceSnapshotsAreReadOnly()
        {
            var first = new ReflectionEmitEngine().Probe();
            var before = UPilotExecutionService.GetResourceSnapshot();
            var second = new ReflectionEmitEngine().Probe();
            var after = UPilotExecutionService.GetResourceSnapshot();
            Assert.That(first, Is.SameAs(second));
            Assert.That(after.generation, Is.EqualTo(before.generation));
            Assert.That(after.domainTypes.probeTypeCount, Is.EqualTo(1));
            Assert.That(after.domainTypes.used, Is.EqualTo(before.domainTypes.used));
            Assert.That(after.emitCache.hits, Is.EqualTo(before.emitCache.hits));
            Assert.That(after.emitCache.capacity, Is.EqualTo(256));
            Assert.That(after.compiledCache.capacity, Is.EqualTo(128));
            Assert.That(Assert.Throws<ExecutionContractException>(() =>
                CSharpEmitBackend.RequireSupported(new DynamicEmitCapability { Supported = false, Error = "fixture-unavailable" })).Code,
                Is.EqualTo("REFLECTION_EMIT_UNAVAILABLE"));
        }

        [Test]
        public void CompiledValidationWarmsExecutionCacheWithoutBusinessInvocation()
        {
            string code = "return " + Guid.NewGuid().ToString("N").Length + "; // " + Guid.NewGuid().ToString("N");
            var result = UPilotExecutionService.ValidateCSharpPayload(new CSharpValidatePayload
            { code = code, mode = "statements", backend = "compiled" });
            string key = CSharpCompiledBackend.CacheKeyForTypes(code, "statements", new[] { "System" },
                new Dictionary<string, Type>());
            Assert.That(result.status, Is.EqualTo("Valid"));
            Assert.That(result.sideEffectsMayHaveOccurred, Is.False);
            Assert.That(CSharpCompiledBackend.IsCached(key), Is.True);
        }

        [Test]
        public void ThrowingConstructorReleasesUnusedReservationAndKeepsPublishedType()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("throwing-constructor", 60, 2, 1, 1);
            var budget = new DynamicTypeBudget(1);
            var engine = new ReflectionEmitEngine(budget);
            try
            {
                using (var reserved = session.ReserveEmitCapacity(true))
                {
                    var emitted = engine.Emit(new DynamicTypeSpec
                    {
                        typeName = "Throwing_" + Guid.NewGuid().ToString("N"),
                        constructors = new[] { new DynamicConstructorSpec
                            { body = "throw new System.InvalidOperationException(\"constructor-fixture\");" } },
                    }, "throwing", "reject", session.Id, null, session.RegisterCleanup);
                    reserved.Store("type", emitted.Type);
                    Assert.Throws<System.Reflection.TargetInvocationException>(() => Activator.CreateInstance(emitted.Type));
                }
                session.Store("object", new object());
                Assert.That(session.HandleCount, Is.EqualTo(2));
                Assert.That(budget.Snapshot().used, Is.EqualTo(1));
                Assert.That(budget.Snapshot().failedAfterReservation, Is.Zero, "Instantiation is not type generation.");
            }
            finally { registry.Close(session.Id); }
        }

        [Test]
        public void SessionReservationProtectsSlotsAndReleasesOnlyUnusedCapacity()
        {
            var registry = new ExecutionSessionRegistry();
            var session = registry.Open("reservation", 60, 2, 1, 2);
            try
            {
                using (var reserved = session.ReserveEmitCapacity(true))
                {
                    Assert.Throws<ExecutionContractException>(() => session.Store("object", new object()));
                    reserved.Store("type", typeof(object));
                    Assert.Throws<ExecutionContractException>(() => session.Store("object", new object()));
                }
                session.Store("object", new object());
                Assert.That(session.HandleCount, Is.EqualTo(2));
                Assert.That(session.DynamicTypeCount, Is.EqualTo(1));
            }
            finally { registry.Close(session.Id); }
        }

        [Test]
        public void SessionTypeAndHandleRejectionsAreStructuredAndDoNotConsumeCapacity()
        {
            var registry = new ExecutionSessionRegistry();
            var handles = registry.Open("handles", 60, 1, 1, 1);
            var types = registry.Open("types", 60, 8, 1, 1);
            try
            {
                var error = Assert.Throws<ExecutionContractException>(() => handles.ReserveEmitCapacity(true));
                Assert.That(error.Detail["resource"], Is.EqualTo("sessionHandles"));
                Assert.That(handles.HandleCount, Is.Zero);
                types.Store("type", typeof(object));
                error = Assert.Throws<ExecutionContractException>(() => types.ReserveEmitCapacity(false));
                Assert.That(error.Detail["resource"], Is.EqualTo("sessionTypes"));
                types.Store("object", new object());
                Assert.That(types.HandleCount, Is.EqualTo(2));
            }
            finally { registry.Close(handles.Id); registry.Close(types.Id); }
        }

        [Test]
        public void BridgeRejectsSessionCapacityBeforeGeneratingTypeAndDecodingArguments()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("bridge-capacity", 60, 1, 1, 1);
            var before = UPilotExecutionService.GetResourceSnapshot();
            try
            {
                var error = Assert.Throws<ExecutionContractException>(() => service.EmitType(new ReflectionEmitPayload
                {
                    sessionId = session.Id, createInstance = true,
                    specJson = JsonUtility.ToJson(new DynamicTypeSpec { typeName = "NotGenerated" }),
                    constructorArgumentsJson = "not-json",
                }));
                Assert.That(error.Code, Is.EqualTo("SESSION_LIMIT_EXCEEDED"));
                var detail = UPilotExecutionService.ToErrorDetail(error, "req", "reflection.emitType");
                Assert.That(detail.sideEffectsMayHaveOccurred, Is.False);
                Assert.That(detail.resourceDiagnostics.Single().resource, Is.EqualTo("sessionHandles"));
                Assert.That(UPilotExecutionService.GetResourceSnapshot().domainTypes.used, Is.EqualTo(before.domainTypes.used));
            }
            finally { service.Sessions.Close(session.Id); }
        }
    }
}
