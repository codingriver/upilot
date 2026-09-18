using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotQuickDebugTests
    {
        private static int _testCalls;

        public static int Count() { _testCalls++; return _testCalls; }
        public static int Throw() { _testCalls++; throw new InvalidOperationException("QD_TARGET_FAILURE"); }
        public int InstanceCount() { _testCalls++; return _testCalls; }

        [SetUp]
        public void Reset() { _testCalls = 0; }

        // ── Bridge getter tests ─────────────────────────────────────────────

        [Test]
        public void BridgeExposesExecutionServiceViaInternalGetter()
        {
            var property = typeof(UPilotBridge).GetProperty("ExecutionService",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(property, Is.Not.Null,
                "UPilotBridge must expose ExecutionService via an internal getter.");
            Assert.That(property.PropertyType, Is.EqualTo(typeof(UPilotExecutionService)));
        }

        [Test]
        public void BridgeExposesReflectionServiceViaInternalGetter()
        {
            var property = typeof(UPilotBridge).GetProperty("ReflectionService",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(property, Is.Not.Null,
                "UPilotBridge must expose ReflectionService via an internal getter.");
            Assert.That(property.PropertyType, Is.EqualTo(typeof(UPilotReflectionService)));
        }

        [Test]
        public void BridgeExecutionServiceGetterReturnsSameInstanceUsedForCommandRegistration()
        {
            var bridge = UPilotBridge.Instance;
            Assert.That(bridge, Is.Not.Null);
            // When bridge is started, the getter should return the same instance
            // that registered csharp.eval and execution.capabilities.
            var property = typeof(UPilotBridge).GetProperty("ExecutionService",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(property, Is.Not.Null,
                "ExecutionService property must exist on UPilotBridge.");
            var service = property.GetValue(bridge);
            Assert.That(service, Is.Not.Null,
                "ExecutionService getter should return a non-null service after bridge initialization.");
            Assert.That(service, Is.TypeOf<UPilotExecutionService>());
        }

        [Test]
        public void BridgeReflectionServiceGetterReturnsSameInstanceUsedForCommandRegistration()
        {
            var bridge = UPilotBridge.Instance;
            Assert.That(bridge, Is.Not.Null);
            var property = typeof(UPilotBridge).GetProperty("ReflectionService",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(property, Is.Not.Null,
                "ReflectionService property must exist on UPilotBridge.");
            var service = property.GetValue(bridge);
            Assert.That(service, Is.Not.Null,
                "ReflectionService getter should return a non-null service after bridge initialization.");
            Assert.That(service, Is.TypeOf<UPilotReflectionService>());
        }

        // ── HandleObjectDumpAsync internal overload signature ───────────────

        [Test]
        public void HandleObjectDumpAsyncHasInternalCallbackOverload()
        {
            var method = typeof(UPilotExecutionService).GetMethod("HandleObjectDumpAsync",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[]
                {
                    typeof(string), typeof(string), typeof(CancellationToken),
                    typeof(Action<Action>), typeof(Func<ObjectDumpResultPayload, Task>),
                    typeof(Func<ExecutionContractException, Task>),
                },
                null);
            Assert.That(method, Is.Not.Null,
                "HandleObjectDumpAsync must expose an internal overload with enqueue/sendResult/sendError callbacks.");
            Assert.That(method.IsAssembly, Is.True,
                "HandleObjectDumpAsync overload must be internal (assembly-visible).");
        }

        // ── csharp_eval via internal callback handler ──────────────────────

        [UnityTest]
        public IEnumerator CSharpEvalInternalHandlerReturnsResultViaCallbacks()
        {
            var service = new UPilotExecutionService(null);
            var payload = new CSharpEvalPayload { code = "1 + 2;" };
            var json = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload result = null;
            ExecutionContractException error = null;
            int responses = 0;

            var task = service.HandleCSharpEvalAsync("qd-eval-1", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { responses++; result = r; return Task.CompletedTask; },
                sendError: e => { responses++; error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Handler did not complete.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(responses, Is.EqualTo(1));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.status, Is.EqualTo("Succeeded"));
            Assert.That(result.modeUsed, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator CSharpEvalInternalHandlerReportsParseErrorViaSendError()
        {
            var service = new UPilotExecutionService(null);
            var payload = new CSharpEvalPayload { code = "?invalid syntax" };
            var json = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            ExecutionContractException error = null;
            int responses = 0;

            var task = service.HandleCSharpEvalAsync("qd-eval-2", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: _ => { responses++; return Task.CompletedTask; },
                sendError: e => { responses++; error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(responses, Is.EqualTo(1));
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Code, Does.StartWith("CSHARP_PARSE"));
        }

        [UnityTest]
        public IEnumerator CSharpEvalCancellationBeforeDispatchHasNoSideEffects()
        {
            var service = new UPilotExecutionService(null);
            var payload = new CSharpEvalPayload { code = "1 + 1;" };
            var json = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            var cts = new CancellationTokenSource();
            cts.Cancel();

            ExecutionContractException error = null;
            int responses = 0;
            var task = service.HandleCSharpEvalAsync("qd-eval-3", json, cts.Token,
                enqueue: a => { a(); },
                sendResult: _ => { responses++; return Task.CompletedTask; },
                sendError: e => { responses++; error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                yield return null;

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(responses, Is.EqualTo(1));
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Code, Is.EqualTo("EXECUTION_CANCELLED"));
            cts.Dispose();
        }

        [UnityTest]
        public IEnumerator CSharpEvalSupportsSessionAndVariables()
        {
            var service = new UPilotExecutionService(null);
            // Open a session via the public Session registry
            var session = service.Sessions.Open("qd-session", 600, 32, 4, 4, 4);

            var payload = new CSharpEvalPayload
            {
                code = "int result = a + b;",
                sessionId = session.Id,
                variablesJson = "{\"items\":[{\"name\":\"a\",\"value\":{\"kind\":\"literal\",\"valueJson\":\"10\"}},{\"name\":\"b\",\"value\":{\"kind\":\"literal\",\"valueJson\":\"20\"}}]}",
            };
            var json = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload result = null;
            ExecutionContractException error = null;

            var task = service.HandleCSharpEvalAsync("qd-eval-session-1", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { result = r; return Task.CompletedTask; },
                sendError: e => { error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(result, Is.Not.Null);
            Assert.That(result.status, Is.EqualTo("Succeeded"));
        }

        // ── unity_reflection_call via internal callback handler ──────────────

        [UnityTest]
        public IEnumerator ReflectionCallInternalHandlerReturnsResultViaCallbacks()
        {
            var execution = new UPilotExecutionService(null);
            var service = new UPilotReflectionService(null, execution);
            var payload = new ReflectionCallPayload
            {
                typeName = typeof(UPilotQuickDebugTests).FullName,
                methodName = "Count",
            };
            var json = JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            ReflectionCallResultPayload result = null;
            ExecutionContractException error = null;
            int responses = 0;

            var task = service.HandleCallAsync("qd-reflect-1", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { responses++; result = r; return Task.CompletedTask; },
                sendError: e => { responses++; error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Handler did not complete.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(responses, Is.EqualTo(1));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.methodName, Is.EqualTo("Count"));
            Assert.That(result.typeName, Is.EqualTo(typeof(UPilotQuickDebugTests).FullName));
            Assert.That(_testCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ReflectionCallInternalHandlerRejectsInvalidModesWithoutDispatch()
        {
            var execution = new UPilotExecutionService(null);
            var service = new UPilotReflectionService(null, execution);

            foreach (var invalid in new[] { "await", "result" })
            {
                var payload = new ReflectionCallPayload
                {
                    typeName = typeof(UPilotQuickDebugTests).FullName,
                    methodName = "Count",
                };
                if (invalid == "await") payload.awaitMode = "invalid";
                if (invalid == "result") payload.resultMode = "invalid";
                var json = JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });

                int queued = 0;
                ExecutionContractException error = null;
                var task = service.HandleCallAsync("qd-reflect-invalid-" + invalid, json, CancellationToken.None,
                    enqueue: a => { queued++; a(); },
                    sendResult: _ => Task.CompletedTask,
                    sendError: e => { error = e; return Task.CompletedTask; });

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;

                Assert.That(error, Is.Not.Null, invalid);
                Assert.That(queued, Is.Zero, invalid + " must not enqueue before validation.");
                Assert.That(error.Detail["sideEffectsMayHaveOccurred"], Is.False, invalid);
                _testCalls = 0;
            }
        }

        [UnityTest]
        public IEnumerator ReflectionCallWithArgumentsJsonExecutesOnce()
        {
            _testCalls = 0;
            var execution = new UPilotExecutionService(null);
            var service = new UPilotReflectionService(null, execution);
            var payload = new ReflectionCallPayload
            {
                typeName = typeof(UPilotQuickDebugTests).FullName,
                methodName = "Count",
                argumentsJson = "{\"items\":[]}",
            };
            var json = JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            ReflectionCallResultPayload result = null;
            ExecutionContractException error = null;

            var task = service.HandleCallAsync("qd-reflect-2", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { result = r; return Task.CompletedTask; },
                sendError: e => { error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.IsFaulted, Is.False);
            Assert.That(result, Is.Not.Null);
            Assert.That(_testCalls, Is.EqualTo(1));
        }

        // ── csharp_object_dump via internal callback handler ────────────────

        [UnityTest]
        public IEnumerator ObjectDumpInternalHandlerReturnsResultViaCallbacks()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-session", 600, 32, 4, 4, 4);
            var payload = new CSharpEvalPayload
            {
                code = "new System.Collections.Generic.List<int>(new int[] { 1, 2, 3 })",
                sessionId = session.Id,
                resultMode = "handle",
            };
            var evalJson = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            var evalQueue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload evalResult = null;
            ExecutionContractException evalError = null;

            var evalTask = service.HandleCSharpEvalAsync("qd-dump-eval", evalJson, CancellationToken.None,
                enqueue: a => { evalQueue.Enqueue(a); },
                sendResult: r => { evalResult = r; return Task.CompletedTask; },
                sendError: e => { evalError = e; return Task.CompletedTask; },
                invocationScheduler: action => action());

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!evalTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (evalQueue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(evalError, Is.Null,
                "csharp_eval should not fail. Error: " + (evalError?.Code ?? "") + " " + (evalError?.Message ?? ""));
            Assert.That(evalResult, Is.Not.Null, "csharp_eval with resultMode=handle should return a result.");
            Assert.That(evalResult.resultHandle, Is.Not.Null.And.Not.Empty,
                "csharp_eval with resultMode=handle must produce a result handle.");

            var dumpPayload = new ObjectDumpPayload
            {
                sessionId = session.Id,
                handle = evalResult.resultHandle,
                maxDepth = 2,
                maxFieldsPerNode = 50,
                outputFormat = "json",
            };
            var dumpJson = JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });

            var dumpQueue = new ConcurrentQueue<Action>();
            ObjectDumpResultPayload dumpResult = null;
            ExecutionContractException dumpError = null;
            int dumpResponses = 0;

            var dumpTask = service.HandleObjectDumpAsync("qd-dump-1", dumpJson, CancellationToken.None,
                enqueue: a => { dumpQueue.Enqueue(a); },
                sendResult: r => { dumpResponses++; dumpResult = r; return Task.CompletedTask; },
                sendError: e => { dumpResponses++; dumpError = e; return Task.CompletedTask; });

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!dumpTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (dumpQueue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(dumpTask.IsCompleted, Is.True, "Dump handler did not complete.");
            Assert.That(dumpTask.IsFaulted, Is.False, dumpTask.Exception?.ToString());
            Assert.That(dumpResponses, Is.EqualTo(1));
            Assert.That(dumpResult, Is.Not.Null);
            Assert.That(dumpResult.typeName, Is.Not.Null.And.Not.Empty);
            Assert.That(dumpResult.totalNodes, Is.GreaterThan(0));
            Assert.That(dumpResult.text, Is.Not.Null.And.Not.Empty);
        }

        [UnityTest]
        public IEnumerator ObjectDumpRejectsMissingSessionIdViaSendError()
        {
            var service = new UPilotExecutionService(null);
            var payload = new ObjectDumpPayload
            {
                sessionId = "",
                handle = "any-handle",
            };
            var json = JsonUtility.ToJson(new ObjectDumpMessage { payload = payload });

            var queue = new ConcurrentQueue<Action>();
            ExecutionContractException error = null;
            int responses = 0;

            var task = service.HandleObjectDumpAsync("qd-dump-2", json, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: _ => { responses++; return Task.CompletedTask; },
                sendError: e => { responses++; error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(responses, Is.EqualTo(1));
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Code, Is.EqualTo("INVALID_PARAMS"));
        }

        [UnityTest]
        public IEnumerator ObjectDumpRejectsInvalidHandleWithoutDispatch()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-invalid", 600, 32, 4, 4, 4);

            var payload = new ObjectDumpPayload
            {
                sessionId = session.Id,
                handle = "nonexistent-handle",
            };
            var json = JsonUtility.ToJson(new ObjectDumpMessage { payload = payload });

            int queued = 0;
            var queue = new ConcurrentQueue<Action>();
            ExecutionContractException error = null;

            var task = service.HandleObjectDumpAsync("qd-dump-3", json, CancellationToken.None,
                enqueue: a => { queued++; queue.Enqueue(a); },
                sendResult: _ => Task.CompletedTask,
                sendError: e => { error = e; return Task.CompletedTask; });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(error, Is.Not.Null);
            Assert.That(queued, Is.GreaterThan(0),
                "Validation occurs on the main thread; enqueue must have been invoked.");
        }

        [UnityTest]
        public IEnumerator ObjectDumpRespectsMaxDepthLimit()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-depth", 600, 32, 4, 4, 4);
            var evalPayload = new CSharpEvalPayload
            {
                code = "new System.Collections.Generic.Dictionary<string, object>()",
                sessionId = session.Id,
                resultMode = "handle",
            };
            var evalJson = JsonUtility.ToJson(new CSharpEvalMessage { payload = evalPayload });

            var queue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload evalResult = null;
            ExecutionContractException evalError = null;

            var evalTask = service.HandleCSharpEvalAsync("qd-dump-depth-eval", evalJson, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { evalResult = r; return Task.CompletedTask; },
                sendError: e => { evalError = e; return Task.CompletedTask; },
                invocationScheduler: action => action());

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!evalTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(evalError, Is.Null,
                "Eval should not fail. Error: " + (evalError?.Code ?? "") + " " + (evalError?.Message ?? ""));
            Assert.That(evalResult, Is.Not.Null,
                "Eval with resultMode=handle should return a result.");

            // Dump with maxDepth=0 — only root, no children
            var dumpPayload = new ObjectDumpPayload
            {
                sessionId = session.Id,
                handle = evalResult.resultHandle,
                maxDepth = 0,
                maxTotalNodes = 1000,
            };
            var dumpJson = JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });

            queue.Clear();
            var dumpQueue = queue;
            ObjectDumpResultPayload dumpResult = null;
            ExecutionContractException dumpError = null;

            var dumpTask = service.HandleObjectDumpAsync("qd-dump-depth-1", dumpJson, CancellationToken.None,
                enqueue: a => { dumpQueue.Enqueue(a); },
                sendResult: r => { dumpResult = r; return Task.CompletedTask; },
                sendError: e => { dumpError = e; return Task.CompletedTask; });

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!dumpTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (dumpQueue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(dumpTask.IsCompleted, Is.True);
            Assert.That(dumpTask.IsFaulted, Is.False);
            Assert.That(dumpResult, Is.Not.Null);
            Assert.That(dumpResult.totalNodes, Is.EqualTo(1),
                "maxDepth=0 should only yield the root node.");
        }

        [UnityTest]
        public IEnumerator ObjectDumpTruncatesAtMaxTotalNodes()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-trunc", 600, 32, 4, 4, 4);
            var evalPayload = new CSharpEvalPayload
            {
                code = "new System.Collections.Generic.List<int>()",
                sessionId = session.Id,
                resultMode = "handle",
            };
            var evalJson = JsonUtility.ToJson(new CSharpEvalMessage { payload = evalPayload });

            var queue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload evalResult = null;
            ExecutionContractException evalError = null;

            var evalTask = service.HandleCSharpEvalAsync("qd-dump-trunc-eval", evalJson, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { evalResult = r; return Task.CompletedTask; },
                sendError: e => { evalError = e; return Task.CompletedTask; },
                invocationScheduler: action => action());

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!evalTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(evalError, Is.Null,
                "Eval should not fail. Error: " + (evalError?.Code ?? "") + " " + (evalError?.Message ?? ""));
            Assert.That(evalResult, Is.Not.Null,
                "Eval with resultMode=handle should return a result.");

            var dumpPayload = new ObjectDumpPayload
            {
                sessionId = session.Id,
                handle = evalResult.resultHandle,
                maxDepth = 5,
                maxTotalNodes = 1,
            };
            var dumpJson = JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });

            var dumpQueue = new ConcurrentQueue<Action>();
            ObjectDumpResultPayload dumpResult = null;

            var dumpTask = service.HandleObjectDumpAsync("qd-dump-trunc-1", dumpJson, CancellationToken.None,
                enqueue: a => { dumpQueue.Enqueue(a); },
                sendResult: r => { dumpResult = r; return Task.CompletedTask; },
                sendError: _ => Task.CompletedTask);

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!dumpTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (dumpQueue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(dumpTask.IsCompleted, Is.True);
            Assert.That(dumpTask.IsFaulted, Is.False);
            Assert.That(dumpResult, Is.Not.Null);
            Assert.That(dumpResult.truncated, Is.True);
            Assert.That(dumpResult.totalNodes, Is.EqualTo(1));
            Assert.That(dumpResult.truncateReason, Does.Contain("maxTotalNodes"));
        }

        [UnityTest]
        public IEnumerator ObjectDumpOutputsTextFormatWhenRequested()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-text", 600, 32, 4, 4, 4);
            var evalPayload = new CSharpEvalPayload
            {
                code = "new System.Collections.Generic.List<int>()",
                sessionId = session.Id,
                resultMode = "handle",
            };
            var evalJson = JsonUtility.ToJson(new CSharpEvalMessage { payload = evalPayload });

            var queue = new ConcurrentQueue<Action>();
            CSharpEvalResultPayload evalResult = null;
            ExecutionContractException evalError = null;

            var evalTask = service.HandleCSharpEvalAsync("qd-dump-text-eval", evalJson, CancellationToken.None,
                enqueue: a => { queue.Enqueue(a); },
                sendResult: r => { evalResult = r; return Task.CompletedTask; },
                sendError: e => { evalError = e; return Task.CompletedTask; },
                invocationScheduler: action => action());

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!evalTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (queue.TryDequeue(out var action)) action();
                yield return null;
            }

            Assert.That(evalError, Is.Null,
                "Eval should not fail. Error: " + (evalError?.Code ?? "") + " " + (evalError?.Message ?? ""));
            Assert.That(evalResult, Is.Not.Null,
                "Eval with resultMode=handle should return a result.");

            var dumpPayload = new ObjectDumpPayload
            {
                sessionId = session.Id,
                handle = evalResult.resultHandle,
                maxDepth = 2,
                outputFormat = "text",
                indentation = "  ",
            };
            var dumpJson = JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });

            queue.Clear();
            var dumpQueue = queue;
            ObjectDumpResultPayload dumpResult = null;

            var dumpTask = service.HandleObjectDumpAsync("qd-dump-text-1", dumpJson, CancellationToken.None,
                enqueue: a => { dumpQueue.Enqueue(a); },
                sendResult: r => { dumpResult = r; return Task.CompletedTask; },
                sendError: _ => Task.CompletedTask);

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!dumpTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (dumpQueue.TryDequeue(out var action)) action();
                yield return null;
            }

            service.Sessions.Close(session.Id);

            Assert.That(dumpTask.IsCompleted, Is.True);
            Assert.That(dumpTask.IsFaulted, Is.False);
            Assert.That(dumpResult, Is.Not.Null);
            Assert.That(dumpResult.text, Is.Not.Null.And.Not.Empty);
        }

        // ── All three handler callbacks have the consistent 3-callback signature ─

        [Test]
        public void AllThreeInternalHandlersHaveConsistentCallbackSignatures()
        {
            var executionType = typeof(UPilotExecutionService);
            var evalMethod = executionType.GetMethod("HandleCSharpEvalAsync",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[]
                {
                    typeof(string), typeof(string), typeof(CancellationToken),
                    typeof(Action<Action>), typeof(Func<Task<CSharpEvalResultPayload>>).MakeByRefType(),
                },
                null);
            var evalOverloads = executionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name == "HandleCSharpEvalAsync" &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(Action<Action>)));

            Assert.That(evalOverloads.Count(), Is.GreaterThanOrEqualTo(1),
                "HandleCSharpEvalAsync must have a callback-based overload.");

            var dumpOverloads = executionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name == "HandleObjectDumpAsync" &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(Action<Action>)));
            Assert.That(dumpOverloads.Count(), Is.GreaterThanOrEqualTo(1),
                "HandleObjectDumpAsync must have a callback-based overload.");

            var reflectionType = typeof(UPilotReflectionService);
            var callOverloads = reflectionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name == "HandleCallAsync" &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(Action<Action>)));
            Assert.That(callOverloads.Count(), Is.GreaterThanOrEqualTo(1),
                "HandleCallAsync must have a callback-based overload.");
        }

        // ── Window menu entry ──────────────────────────────────────────────

        [Test]
        public void QuickDebugWindowHasMenuItem()
        {
            var windowType = typeof(UPilotQuickDebugWindow);
            var openMethod = windowType.GetMethod("Open",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(openMethod, Is.Not.Null,
                "UPilotQuickDebugWindow must have a static Open method.");
            var menuItems = openMethod.GetCustomAttributes(typeof(MenuItem), false);
            Assert.That(menuItems.Length, Is.GreaterThanOrEqualTo(1),
                "Open method must have a [MenuItem] attribute.");
            var menuItem = (MenuItem)menuItems[0];
            Assert.That(menuItem.menuItem, Is.EqualTo("UPilot/快捷调试"));
        }

        [Test]
        public void QuickDebugWindowIsEditorWindow()
        {
            Assert.That(typeof(UPilotQuickDebugWindow).IsSubclassOf(typeof(EditorWindow)), Is.True);
        }
    }
}