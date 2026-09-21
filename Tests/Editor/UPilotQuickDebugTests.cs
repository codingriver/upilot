using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

        [Test]
        public void ObjectDumpHandlerDefaultsToHiddenTypeNamesAndCanEnableThem()
        {
            var service = new UPilotExecutionService(null);
            var session = service.Sessions.Open("qd-dump-types", 600, 32, 4, 4, 4);
            try
            {
                var handle = service.Sessions.Store(session.Id, "object", new TypeVisibilityFixture());
                ObjectDumpResultPayload hidden = null;
                ExecutionContractException hiddenError = null;
                var hiddenJson = JsonUtility.ToJson(new ObjectDumpMessage
                {
                    payload = new ObjectDumpPayload
                    {
                        sessionId = session.Id,
                        handle = handle,
                        maxDepth = 2,
                        outputFormat = "text",
                    },
                });

                service.HandleObjectDumpAsync(
                        "qd-dump-types-hidden",
                        hiddenJson,
                        CancellationToken.None,
                        enqueue: action => action(),
                        sendResult: result => { hidden = result; return Task.CompletedTask; },
                        sendError: error => { hiddenError = error; return Task.CompletedTask; })
                    .GetAwaiter().GetResult();

                Assert.That(hiddenError, Is.Null);
                Assert.That(hidden, Is.Not.Null);
                Assert.That(hidden.text, Does.Contain("Count: 3")
                    .And.Contain("PolymorphicValue: \"runtime string\""));
                Assert.That(hidden.text, Does.Not.Contain("Count (System.Int32)")
                    .And.Not.Contain("System.Object -> System.String"));
                Assert.That(hidden.root.children.Single(child => child.name == "Count").declaredType,
                    Is.EqualTo(typeof(int).FullName));
                Assert.That(hidden.root.children.Single(child => child.name == "PolymorphicValue").runtimeType,
                    Is.EqualTo(typeof(string).FullName));

                ObjectDumpResultPayload shown = null;
                ExecutionContractException shownError = null;
                var shownJson = JsonUtility.ToJson(new ObjectDumpMessage
                {
                    payload = new ObjectDumpPayload
                    {
                        sessionId = session.Id,
                        handle = handle,
                        maxDepth = 2,
                        outputFormat = "text",
                        includeTypeNames = true,
                    },
                });

                service.HandleObjectDumpAsync(
                        "qd-dump-types-shown",
                        shownJson,
                        CancellationToken.None,
                        enqueue: action => action(),
                        sendResult: result => { shown = result; return Task.CompletedTask; },
                        sendError: error => { shownError = error; return Task.CompletedTask; })
                    .GetAwaiter().GetResult();

                Assert.That(shownError, Is.Null);
                Assert.That(shown, Is.Not.Null);
                Assert.That(shown.text, Does.Contain("Count (System.Int32): 3")
                    .And.Contain("PolymorphicValue (System.Object -> System.String): \"runtime string\""));
            }
            finally
            {
                service.Sessions.Close(session.Id);
            }
        }

        [Test]
        public void ObjectDumpSummarizesReflectionInfrastructureWithoutHidingOrdinaryValues()
        {
            var ignoredTypes = new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal);
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                new ReflectionObjectDumpFixture(),
                4,
                100,
                1000,
                false,
                ignoredTypes,
                ref totalNodes,
                expandUnityValueTypes: false,
                expandReflectionTypes: false);

            Assert.That(root.children.Single(child => child.name == "Level").value, Is.EqualTo("10"));
            Assert.That(root.children.Single(child => child.name == ".Name").value, Is.EqualTo("\"Hero\""));
            var typeNode = root.children.Single(child => child.name == "TypeValue");
            Assert.That(typeNode.children, Is.Null.Or.Empty);
            Assert.That(typeNode.value, Is.EqualTo(typeof(ReflectionObjectDumpFixture).FullName));
            Assert.That(typeNode.value, Does.Not.Contain("reflection summary:"));

            foreach (var name in new[] { "Callback", "Assembly", "Module", "Method", "Field", "Property" })
            {
                var node = root.children.Single(child => child.name == name);
                Assert.That(node.children, Is.Null.Or.Empty, name + " should not recursively expand by default.");
                Assert.That(node.value, Does.Contain("reflection summary:"), name + " should retain a bounded summary.");
            }

            var text = ObjectDumper.FormatAsText(root);
            Assert.That(text, Does.Not.Contain("  .Module"));
            Assert.That(text, Does.Not.Contain("Evidence"));
            Assert.That(text, Does.Not.Contain("<RawData>"));
        }

        [Test]
        public void ObjectDumpCanExpandReflectionTypesAndExplicitIgnoreStillWins()
        {
            var method = typeof(ReflectionObjectDumpFixture).GetMethod(
                "Noop",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var ignoredTypes = new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal);
            var totalNodes = 0;
            var expanded = ObjectDumper.Dump(
                method,
                2,
                100,
                1000,
                false,
                ignoredTypes,
                ref totalNodes,
                expandUnityValueTypes: false,
                expandReflectionTypes: true);
            Assert.That(expanded.children, Is.Not.Null.And.Not.Empty);

            ignoredTypes.Add(method.GetType().FullName);
            totalNodes = 0;
            var ignored = ObjectDumper.Dump(
                method,
                2,
                100,
                1000,
                false,
                ignoredTypes,
                ref totalNodes,
                expandUnityValueTypes: false,
                expandReflectionTypes: true);
            Assert.That(ignored.children, Is.Null.Or.Empty);
            Assert.That(ignored.value, Does.Not.Contain("reflection summary:"));
        }

        [Test]
        public void ObjectDumpRepeatedLeafReferencesAreNotCircular()
        {
            const string shared = "shared leaf";
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                new[] { shared, shared },
                2,
                10,
                100,
                false,
                new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal),
                ref totalNodes);

            Assert.That(root.children, Has.Length.EqualTo(2));
            Assert.That(root.children[0].value, Is.EqualTo("\"shared leaf\""));
            Assert.That(root.children[1].value, Is.EqualTo("\"shared leaf\""));
            Assert.That(root.children[1].value, Does.Not.Contain("circular ref"));
        }

        [Test]
        public void ObjectDumpTextCompactsSimpleCollectionsWithoutMutatingDumpNodesOrReadingAgain()
        {
            var fixture = new CompactCollectionFixture();
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                fixture,
                4,
                100,
                1000,
                false,
                new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal),
                ref totalNodes);
            var dictionary = root.children.Single(child => child.name == "DictionaryValue");
            var firstEntry = dictionary.children[0];

            Assert.That(fixture.GetterCalls, Is.EqualTo(1));
            Assert.That(firstEntry.name, Is.EqualTo("[0]"));
            Assert.That(firstEntry.children.Any(child => child.name == "key"), Is.True);
            Assert.That(firstEntry.children.Any(child => child.name == ".Key"), Is.True);

            var text = ObjectDumper.FormatAsText(root).Replace("\r\n", "\n");

            Assert.That(text, Does.Contain("ArrayValue (System.Int32[]): [1, 2, 3]"));
            Assert.That(text, Does.Contain("ListValue (System.Collections.Generic.List<System.String>): [\"A\", \"B\", \"C\"]"));
            Assert.That(text, Does.Contain("DictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"first\": 1, \"second\": 2 }"));
            Assert.That(text, Does.Contain("EmptyArray (System.Int32[]): []"));
            Assert.That(text, Does.Contain("EmptyList (System.Collections.Generic.List<System.String>): []"));
            Assert.That(text, Does.Contain("EmptyDictionary (System.Collections.Generic.Dictionary<System.String, System.Int32>): {}"));
            Assert.That(text, Does.Contain("NullableList (System.Collections.Generic.List<System.String>): [\"A\", null, \"B\"]"));
            Assert.That(text, Does.Contain("EscapedList (System.Collections.Generic.List<System.String>): [\"quote\\\"\\nline\"]"));
            Assert.That(text, Does.Contain("BoundaryList (System.Collections.Generic.List<System.String>): [\"" + new string('x', 178) + "\"]"));
            Assert.That(text, Does.Contain("LongList (System.Collections.Generic.List<System.String>)\n"));
            Assert.That(text, Does.Contain("BoundaryDictionary (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"" + new string('k', 171) + "\": 1, \"second\": 2 }"));
            Assert.That(text, Does.Contain("LongDictionary (System.Collections.Generic.Dictionary<System.String, System.Int32>):\n    { \"" + new string('k', 172) + "\": 1 }\n    { \"second\": 2 }"));
            Assert.That(text, Does.Contain("FirstShortThenLongList (System.Collections.Generic.List<System.String>): [\"short\", \"" + new string('x', 179) + "\"]"));
            Assert.That(text, Does.Contain("FirstShortThenLongDictionary (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"first\": 1, \"" + new string('k', 172) + "\": 2 }"));
            Assert.That(text, Does.Contain("ComplexDictionary (System.Collections.Generic.Dictionary<System.String, CodingRiver.UPilot.Tests.UPilotQuickDebugTests+CompactCollectionFixture+ComplexValue>):\n    {\n      Key (System.String): \"complex\"\n      Value (CodingRiver.UPilot.Tests.UPilotQuickDebugTests+CompactCollectionFixture+ComplexValue)"));
            Assert.That(text, Does.Contain("PolymorphicList (System.Collections.Generic.List<CodingRiver.UPilot.Tests.UPilotQuickDebugTests+CompactCollectionFixture+BaseValue>)\n    [0] (CodingRiver.UPilot.Tests.UPilotQuickDebugTests+CompactCollectionFixture+DerivedValue)"));
            Assert.That(text, Does.Contain("NamedFields (CodingRiver.UPilot.Tests.UPilotQuickDebugTests+CompactCollectionFixture+NamedValueFields) {\n    key (System.String): \"ordinary key\"\n    value (System.Int32): 9\n  }"));
            Assert.That(text, Does.Not.Contain("  .Key (System.String): \"first\""));
            Assert.That(text, Does.Not.Contain("  .Value (System.Int32): 1"));
            Assert.That(fixture.GetterCalls, Is.EqualTo(1), "Text formatting must not invoke getters again.");
            Assert.That(firstEntry.name, Is.EqualTo("[0]"), "Text formatting must not alter structured nodes.");
            Assert.That(firstEntry.children.Any(child => child.name == ".Key"), Is.True);
        }

        [Test]
        public void ObjectDumpTextKeepsLongOrDiagnosticSequencesExpanded()
        {
            var diagnostic = new ObjectDumpNodeJson
            {
                name = "DiagnosticList",
                declaredType = typeof(List<string>).FullName,
                runtimeType = typeof(List<string>).FullName,
                children = new[]
                {
                    new ObjectDumpNodeJson
                    {
                        name = "[0]",
                        declaredType = typeof(string).FullName,
                        runtimeType = typeof(string).FullName,
                        value = "(error: InvalidOperationException - test)",
                    },
                },
            };

            var text = ObjectDumper.FormatAsText(diagnostic).Replace("\r\n", "\n");

            Assert.That(text, Does.Contain("DiagnosticList (System.Collections.Generic.List<System.String>)\n  [0] (System.String): (error: InvalidOperationException - test)"));
            Assert.That(text, Does.Not.Contain("DiagnosticList (System.Collections.Generic.List<System.String>): ["));
        }

        [Test]
        public void ObjectDumpTextSupportsLargeCollectionsAndDictionaries()
        {
            const int itemCount = 80;
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                new LargeCollectionFixture(itemCount),
                4,
                200,
                10000,
                false,
                new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal),
                ref totalNodes);
            var arrayNode = root.children.Single(child => child.name == "ArrayValue");
            var listNode = root.children.Single(child => child.name == "ListValue");
            var dictionaryNode = root.children.Single(child => child.name == "DictionaryValue");

            var text = ObjectDumper.FormatAsText(root).Replace("\r\n", "\n");

            Assert.That(text, Does.Contain("ArrayValue (System.Int32[]): [0, 1")
                .And.Contain(", 79]"));
            Assert.That(text, Does.Contain("ListValue (System.Collections.Generic.List<System.Int32>): [0, 1")
                .And.Contain(", 79]"));
            Assert.That(text, Does.Contain("DictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"key-000\": 0")
                .And.Contain(", \"key-079\": 79 }"));
            Assert.That(text, Does.Not.Contain("\n    { \"key-000\": 0 }")
                .And.Not.Contain(" key (").And.Not.Contain(" .Key (")
                .And.Not.Contain(" value (").And.Not.Contain(" .Value ("));
            Assert.That(text, Does.Not.Contain("(truncated:"));

            Assert.That(arrayNode.children, Has.Length.EqualTo(itemCount));
            Assert.That(listNode.children, Has.Length.EqualTo(itemCount));
            Assert.That(dictionaryNode.children, Has.Length.EqualTo(itemCount));
            Assert.That(dictionaryNode.children[0].name, Is.EqualTo("[0]"),
                "Text formatting must not remove indices from structured dictionary nodes.");
        }

        [Test]
        public void ObjectDumpTextPreservesLargeCollectionTruncationSummaries()
        {
            const int itemCount = 80;
            const int maxFieldsPerNode = 10;
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                new LargeCollectionFixture(itemCount),
                4,
                maxFieldsPerNode,
                10000,
                false,
                new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal),
                ref totalNodes);

            var text = ObjectDumper.FormatAsText(root).Replace("\r\n", "\n");
            int dictionaryStart = text.IndexOf("  DictionaryValue", StringComparison.Ordinal);
            Assert.That(dictionaryStart, Is.GreaterThan(0));
            string dictionaryText = text.Substring(dictionaryStart);

            Assert.That(text, Does.Contain("...: (70 more items, total 80)"));
            Assert.That(text, Does.Contain("...: (truncated: ≥10 items)"));
            Assert.That(dictionaryText, Does.Contain("    { \"key-000\": 0 }"));
            Assert.That(dictionaryText, Does.Contain("    { \"key-009\": 9 }"));
            Assert.That(dictionaryText, Does.Not.Contain("key-010").And.Not.Contain("[0]"));
            Assert.That(dictionaryText, Does.Contain("      ...: (truncated: ≥10 items)"));
        }

        [Test]
        public void ObjectDumpTextOmitsArrowWhenRuntimeTypeIsUnavailable()
        {
            var text = ObjectDumper.FormatAsText(new ObjectDumpNodeJson
            {
                name = "NullValue",
                declaredType = typeof(object).FullName,
                runtimeType = "",
                value = "null",
            });

            Assert.That(text.Trim(), Is.EqualTo("NullValue (System.Object): null"));
            Assert.That(text, Does.Not.Contain(" -> "));
        }

        [Test]
        public void ObjectDumpTextCanHideTypeAnnotationsWithoutChangingNodesOrDiagnostics()
        {
            var intNode = new ObjectDumpNodeJson
            {
                name = "IntValue",
                declaredType = typeof(int).FullName,
                runtimeType = typeof(int).FullName,
                value = "7",
            };
            var polymorphicNode = new ObjectDumpNodeJson
            {
                name = "PolymorphicValue",
                declaredType = typeof(object).FullName,
                runtimeType = typeof(string).FullName,
                value = "\"runtime\"",
            };
            var root = new ObjectDumpNodeJson
            {
                name = "$",
                declaredType = typeof(object).FullName,
                runtimeType = typeof(object).FullName,
                children = new[]
                {
                    intNode,
                    polymorphicNode,
                    new ObjectDumpNodeJson
                    {
                        name = "ArrayValue",
                        declaredType = typeof(int[]).FullName,
                        runtimeType = typeof(int[]).FullName,
                        children = new[]
                        {
                            new ObjectDumpNodeJson { name = "[0]", declaredType = typeof(int).FullName, runtimeType = typeof(int).FullName, value = "1" },
                            new ObjectDumpNodeJson { name = "[1]", declaredType = typeof(int).FullName, runtimeType = typeof(int).FullName, value = "2" },
                        },
                    },
                    new ObjectDumpNodeJson
                    {
                        name = "IgnoredThread",
                        declaredType = typeof(System.Threading.Thread).FullName,
                        value = "(ignored: System.Threading.Thread)",
                    },
                    new ObjectDumpNodeJson
                    {
                        name = "ThrowingProperty",
                        declaredType = typeof(int).FullName,
                        value = "(error: InvalidOperationException - test)",
                    },
                },
            };

            var hidden = ObjectDumper.FormatAsText(root, "  ", false).Replace("\r\n", "\n");
            var shown = ObjectDumper.FormatAsText(root).Replace("\r\n", "\n");

            Assert.That(hidden, Does.Contain("IntValue: 7")
                .And.Contain("PolymorphicValue: \"runtime\"")
                .And.Contain("ArrayValue: [1, 2]")
                .And.Contain("IgnoredThread: (ignored: System.Threading.Thread)")
                .And.Contain("ThrowingProperty: (error: InvalidOperationException - test)"));
            Assert.That(hidden, Does.Not.Contain("IntValue (System.Int32)")
                .And.Not.Contain("System.Object -> System.String")
                .And.Not.Contain("ArrayValue (System.Int32[])"));
            Assert.That(shown, Does.Contain("IntValue (System.Int32): 7")
                .And.Contain("PolymorphicValue (System.Object -> System.String): \"runtime\"")
                .And.Contain("ArrayValue (System.Int32[]): [1, 2]"));
            Assert.That(intNode.declaredType, Is.EqualTo(typeof(int).FullName));
            Assert.That(polymorphicNode.runtimeType, Is.EqualTo(typeof(string).FullName));
        }

        [Test]
        public void ObjectDumpReportsGetterErrorsWithoutHidingNullOrOtherMembers()
        {
            var totalNodes = 0;
            var root = ObjectDumper.Dump(new GetterErrorFixture(), 2, 100, 100, true,
                new HashSet<string>(), ref totalNodes);

            Assert.That(root.children.Single(child => child.name == ".NullProperty").value, Is.EqualTo("null"));
            Assert.That(root.children.Single(child => child.name == ".AfterError").value, Is.EqualTo("42"));
            foreach (var name in new[] { ".ThrowingProperty", "static StaticThrowingProperty" })
            {
                var node = root.children.Single(child => child.name == name);
                Assert.That(node.value, Does.StartWith("(error: InvalidOperationException - first\\nline"));
                Assert.That(node.value, Does.Not.Contain("TargetInvocationException").And.Not.Contain("\n"));
                Assert.That(node.value.Length, Is.LessThan(310));
                Assert.That(node.value, Does.EndWith("…)"));
                Assert.That(node.declaredType, Is.EqualTo(typeof(int).FullName));
                Assert.That(node.children, Is.Null.Or.Empty);
            }
            Assert.That(root.children.Single(child => child.name == "static StaticAfterError").value, Is.EqualTo("84"));
            Assert.That(totalNodes, Is.EqualTo(1 + root.children.Length));
        }

        [Test]
        public void ObjectDumpGetterErrorsStillConsumeNodeBudget()
        {
            var totalNodes = 0;
            var root = ObjectDumper.Dump(new GetterErrorFixture(), 2, 100, 2, false,
                new HashSet<string>(), ref totalNodes);

            Assert.That(totalNodes, Is.EqualTo(2));
            Assert.That(root.children, Has.Length.EqualTo(1));
            Assert.That(root.children[0].value, Does.StartWith("(error: InvalidOperationException - "));
        }

        [TestCase(typeof(int?), "System.Nullable<System.Int32>")]
        [TestCase(typeof(List<string>), "System.Collections.Generic.List<System.String>")]
        [TestCase(typeof(Dictionary<string, List<int?[]>>),
            "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Nullable<System.Int32>[]>>")]
        [TestCase(typeof(List<int>[,]), "System.Collections.Generic.List<System.Int32>[,]")]
        [TestCase(typeof(GenericDumpFixture<int>.Nested<string>),
            "CodingRiver.UPilot.Tests.UPilotQuickDebugTests+GenericDumpFixture<System.Int32>+Nested<System.String>")]
        public void ObjectDumpTextSimplifiesGenericNamesWithoutMutatingStructuredTypes(Type type, string displayName)
        {
            var node = new ObjectDumpNodeJson
            {
                name = "Value",
                declaredType = type.FullName,
                runtimeType = type.FullName,
                value = "null",
            };
            Assert.That(ObjectDumper.FormatAsText(node).Trim(),
                Is.EqualTo("Value (" + displayName + "): null"));
            Assert.That(node.declaredType, Is.EqualTo(type.FullName));
            Assert.That(node.runtimeType, Is.EqualTo(type.FullName));

            node.declaredType = typeof(object).FullName;
            Assert.That(ObjectDumper.FormatAsText(node).Trim(),
                Is.EqualTo("Value (System.Object -> " + displayName + "): null"));
            Assert.That(node.runtimeType, Is.EqualTo(type.FullName));
        }

        [Test]
        public void ObjectDumpTextPreservesUnresolvableTypeNames()
        {
            const string unknownType = "Missing.Type`1[[Missing.Argument, Missing.Assembly]]";
            var node = new ObjectDumpNodeJson { name = "Value", declaredType = unknownType, value = "null" };
            Assert.That(ObjectDumper.FormatAsText(node), Does.Contain(unknownType));
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

        [Test]
        public void QuickDebugParameterTooltipIncludesParameterVariableAndFiniteValueMeanings()
        {
            var method = typeof(UPilotQuickDebugWindow).GetMethod(
                "ParameterLabel",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var content = (GUIContent)method.Invoke(null, new object[]
            {
                "显示类型名称",
                "includeTypeNames",
                "_dumpIncludeTypeNames",
                "是否在文本结果中显示节点类型；结构化 root 保留类型。",
                "false：隐藏（默认）\ntrue：显示",
            });

            Assert.That(content.text, Is.EqualTo("显示类型名称"));
            Assert.That(content.tooltip, Does.Contain("参数名：includeTypeNames"));
            Assert.That(content.tooltip, Does.Contain("窗口变量：_dumpIncludeTypeNames"));
            Assert.That(content.tooltip, Does.Contain("结构化 root 保留类型"));
            Assert.That(content.tooltip, Does.Contain("false：隐藏（默认）"));
            Assert.That(content.tooltip, Does.Contain("true：显示"));

            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                Assert.That(ReadWindowField<bool>(window, "_dumpIncludeTypeNames"), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void QuickDebugTabsUseTaskOrientedTitlesAndExplainUsageAndFeatures()
        {
            var method = typeof(UPilotQuickDebugWindow).GetMethod(
                "CreateTabContents",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var tabs = (GUIContent[])method.Invoke(null, null);
            Assert.That(tabs.Select(tab => tab.text), Is.EqualTo(new[]
            {
                "运行 C# 代码",
                "调用现有方法",
                "查看对象信息",
            }));

            Assert.That(tabs[0].tooltip, Does.Contain("用法：").And.Contain("功能：").And.Contain("csharp_eval"));
            Assert.That(tabs[1].tooltip, Does.Contain("用法：").And.Contain("功能：").And.Contain("unity_reflection_call"));
            Assert.That(tabs[2].tooltip, Does.Contain("用法：").And.Contain("功能：").And.Contain("csharp_object_dump"));
        }

        [Test]
        public void DiagnosticsMenuUsesStableUserFacingNamesAndExplainsCoverage()
        {
            var toolbarMethod = typeof(UPilotQuickDebugWindow).GetMethod(
                "CreateDiagnosticsToolbarContent",
                BindingFlags.Static | BindingFlags.NonPublic);
            var itemMethod = typeof(UPilotQuickDebugWindow).GetMethod(
                "CreateToolCheckMenuContent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(toolbarMethod, Is.Not.Null);
            Assert.That(itemMethod, Is.Not.Null);

            var toolbar = (GUIContent)toolbarMethod.Invoke(null, null);
            Assert.That(toolbar.text, Is.EqualTo("诊断"));
            foreach (var tool in UPilotQuickDebugToolCheck.ToolIds)
            {
                var item = (GUIContent)itemMethod.Invoke(null, new object[] { tool });
                Assert.That(item.text, Is.EqualTo("检查“" + UPilotQuickDebugToolCheck.DisplayName(tool) + "”工具"));
                Assert.That(item.tooltip, Does.Contain(tool));
                Assert.That(item.tooltip, Does.Contain(UPilotQuickDebugToolCheck.RelativeLogPath(tool)));
                Assert.That(item.tooltip, Does.Contain("本地 Unity").And.Contain("不验证 MCP"));
            }
        }

        [TestCase("csharp_eval")]
        [TestCase("unity_reflection_call")]
        [TestCase("csharp_object_dump")]
        public void ToolCheckFlowPreservesUserInputsAndPublishesOnce(string toolId)
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                WriteWindowField(window, "_dumpQuickEvalCode", "user code");
                WriteWindowField(window, "_dumpSessionId", "user session");
                WriteWindowField(window, "_dumpHandle", "user handle");
                WriteWindowField(window, "_dumpMaxDepth", 17);
                WriteWindowField(window, "_dumpMaxFieldsPerNode", 123);
                WriteWindowField(window, "_dumpMaxTotalNodes", 4567);
                WriteWindowField(window, "_dumpIncludeStatic", true);
                WriteWindowField(window, "_dumpIncludeTypeNames", true);
                WriteWindowField(window, "_dumpExpandReflectionTypes", true);
                WriteWindowField(window, "_dumpOutputFormat", "json");
                WriteWindowField(window, "_evalCode", "do not execute");
                WriteWindowField(window, "_reflectTypeName", "user.type");
                WriteWindowField(window, "_reflectArgumentsJson", "user arguments");
                var expected = new UPilotToolCheckResult(toolId)
                {
                    Status = UPilotToolCheckStatus.Passed,
                    Stage = "test",
                };
                var runCount = 0;
                var publishCount = 0;
                UPilotToolCheckResult published = null;
                var reports = new List<string>();
                Func<CancellationToken, Action<string>, Task<UPilotToolCheckResult>> run = (_, progress) =>
                {
                    runCount++;
                    return Task.FromResult(expected);
                };
                Action<UPilotToolCheckResult> publish = result =>
                {
                    publishCount++;
                    published = result;
                };

                var task = RunDiagnostic(window, toolId, run, publish, (path, text) => reports.Add(text));
                task.GetAwaiter().GetResult();

                Assert.That(runCount, Is.EqualTo(1));
                Assert.That(publishCount, Is.EqualTo(1));
                Assert.That(published, Is.SameAs(expected));
                Assert.That(ReadWindowField<string>(window, "_dumpQuickEvalCode"), Is.EqualTo("user code"));
                Assert.That(ReadWindowField<string>(window, "_dumpSessionId"), Is.EqualTo("user session"));
                Assert.That(ReadWindowField<string>(window, "_dumpHandle"), Is.EqualTo("user handle"));
                Assert.That(ReadWindowField<int>(window, "_dumpMaxDepth"), Is.EqualTo(17));
                Assert.That(ReadWindowField<int>(window, "_dumpMaxFieldsPerNode"), Is.EqualTo(123));
                Assert.That(ReadWindowField<int>(window, "_dumpMaxTotalNodes"), Is.EqualTo(4567));
                Assert.That(ReadWindowField<bool>(window, "_dumpIncludeStatic"), Is.True);
                Assert.That(ReadWindowField<bool>(window, "_dumpIncludeTypeNames"), Is.True);
                Assert.That(ReadWindowField<bool>(window, "_dumpExpandReflectionTypes"), Is.True);
                Assert.That(ReadWindowField<string>(window, "_dumpOutputFormat"), Is.EqualTo("json"));
                Assert.That(ReadWindowField<bool>(window, "_dumpQuickRunning"), Is.False);
                Assert.That(ReadWindowField<string>(window, "_evalCode"), Is.EqualTo("do not execute"));
                Assert.That(ReadWindowField<string>(window, "_reflectTypeName"), Is.EqualTo("user.type"));
                Assert.That(ReadWindowField<string>(window, "_reflectArgumentsJson"), Is.EqualTo("user arguments"));
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.False);
                Assert.That(reports, Has.Count.EqualTo(2));
                Assert.That(reports[0], Does.Contain("未完成").And.Contain(expected.RunId));
                Assert.That(reports[1], Does.Contain("通过").And.Contain(expected.RunId));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [TestCase("_evalRunning")]
        [TestCase("_reflectRunning")]
        [TestCase("_dumpRunning")]
        [TestCase("_dumpQuickRunning")]
        [TestCase("_diagnosticRunning")]
        public void BusyWindowDoesNotStartAnotherDiagnostic(string field)
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                WriteWindowField(window, field, true);
                RunDiagnostic(window, "csharp_eval",
                    (_, __) => throw new AssertionException("Must not execute"),
                    _ => Assert.Fail("Must not publish"), (_, __) => Assert.Fail("Must not write")).GetAwaiter().GetResult();
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator DiagnosticCancelKeepsBusyUntilOwnedWorkFinishes()
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            var finished = new TaskCompletionSource<UPilotToolCheckResult>();
            var cancelled = false;
            var published = 0;
            UPilotToolCheckResult outcome = null;
            try
            {
                var task = RunDiagnostic(window, "csharp_eval", async (token, progress) =>
                {
                    using (token.Register(() => cancelled = true))
                        return await finished.Task;
                }, result => { published++; outcome = result; }, (_, __) => { });
                typeof(UPilotQuickDebugWindow).GetMethod("CancelDiagnostic",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(cancelled, Is.True);
                Assert.That(task.IsCompleted, Is.False);
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.True);
                typeof(UPilotQuickDebugWindow).GetMethod("OnDisable",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.True);
                finished.SetResult(new UPilotToolCheckResult("csharp_eval"));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.That(task.IsCompleted, Is.True);
                task.GetAwaiter().GetResult();
                Assert.That(outcome.Status, Is.EqualTo(UPilotToolCheckStatus.Cancelled));
                Assert.That(published, Is.EqualTo(1));
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.False);
            }
            finally
            {
                finished.TrySetResult(new UPilotToolCheckResult("csharp_eval"));
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void DiagnosticWriteFailureKeepsToolResultAndMarksLogUnavailable()
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                UPilotToolCheckResult result = null;
                RunDiagnostic(window, "csharp_eval",
                    (_, __) => Task.FromResult(new UPilotToolCheckResult("csharp_eval")),
                    value => result = value, (_, __) => throw new IOException("test write failure")).GetAwaiter().GetResult();
                Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed));
                Assert.That(result.Warnings, Has.Some.Contains("写入失败"));
                Assert.That(result.BuildSummary(), Does.Contain("未保存").And.Not.Contain("完整结果："));
                Assert.That(ReadWindowField<HashSet<string>>(window, "_unavailableDiagnosticLogs"), Does.Contain("csharp_eval"));
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void DiagnosticExceptionPublishesOnceAndReleasesBusyState()
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                var published = 0;
                RunDiagnostic(window, "csharp_eval", (_, __) => throw new InvalidOperationException("test"),
                    result =>
                    {
                        published++;
                        Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck));
                    }, (_, __) => { }).GetAwaiter().GetResult();
                Assert.That(published, Is.EqualTo(1));
                Assert.That(ReadWindowField<bool>(window, "_diagnosticRunning"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void DiagnosticReportsAreAtomicIsolatedAndPreserveIncompleteIdentity()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UPilotQuickDebugTests", Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var tool in UPilotQuickDebugToolCheck.ToolIds)
                {
                    var path = Path.Combine(directory, UPilotQuickDebugToolCheck.RelativeLogPath(tool));
                    var result = new UPilotToolCheckResult(tool);
                    UPilotQuickDebugToolCheck.WriteReport(path, "previous");
                    UPilotQuickDebugToolCheck.WriteReport(path, UPilotQuickDebugToolCheck.PendingReport(result));
                    Assert.That(File.ReadAllText(path), Does.Contain("未完成").And.Contain(result.RunId).And.Not.Contain("previous"));
                    UPilotQuickDebugToolCheck.WriteReport(path, result.BuildFullReport());
                    Assert.That(File.ReadAllText(path), Does.Contain(result.RunId).And.Not.Contain("未完成"));
                }
                Assert.That(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories), Is.Empty);
                Assert.That(Directory.GetFiles(directory, "*.log", SearchOption.AllDirectories), Has.Length.EqualTo(3));
                Assert.That(UPilotQuickDebugToolCheck.RelativeLogPath("csharp_object_dump"),
                    Is.Not.EqualTo("Logs/UPilot/csharp_object_dump.log"));
                Assert.Throws<ArgumentOutOfRangeException>(() => UPilotQuickDebugToolCheck.RelativeLogPath("../escape"));
                var pathMethod = typeof(UPilotQuickDebugWindow).GetMethod("GetDiagnosticLogPath", BindingFlags.Static | BindingFlags.NonPublic);
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                foreach (var tool in UPilotQuickDebugToolCheck.ToolIds)
                    Assert.That((string)pathMethod.Invoke(null, new object[] { tool }), Does.StartWith(projectRoot));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void DiagnosticReportReplaceFailurePreservesPreviousBytes()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UPilotQuickDebugTests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "locked.log");
            try
            {
                UPilotQuickDebugToolCheck.WriteReport(path, "previous report");
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.Throws<IOException>(() => UPilotQuickDebugToolCheck.WriteReport(path, "replacement"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("previous report"));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void SuccessfulDiagnosticWithExpectedErrorsDoesNotUseErrorStyle()
        {
            var window = ScriptableObject.CreateInstance<UPilotQuickDebugWindow>();
            try
            {
                typeof(UPilotQuickDebugWindow).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, new object[] { "[工具检查：通过] 语法错误、目标异常、失败路径", false });
                Assert.That(ReadWindowField<List<bool>>(window, "_logEntryErrors"), Is.EqualTo(new[] { false }));
                Assert.That(ReadWindowField<List<string>>(window, "_logEntries"), Has.Count.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        private static Task RunDiagnostic(UPilotQuickDebugWindow window, string toolId,
            Func<CancellationToken, Action<string>, Task<UPilotToolCheckResult>> run,
            Action<UPilotToolCheckResult> publish, Action<string, string> write)
        {
            return (Task)typeof(UPilotQuickDebugWindow).GetMethod("RunToolCheckAsync",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { toolId, run, publish, write });
        }

        [Test]
        public void ObjectDumpLogPathIsProjectScopedAndOverwriteKeepsOnlyLatestResult()
        {
            var getPath = typeof(UPilotQuickDebugWindow).GetMethod(
                "GetObjectDumpLogPath",
                BindingFlags.Static | BindingFlags.NonPublic);
            var overwrite = typeof(UPilotQuickDebugWindow).GetMethod(
                "OverwriteObjectDumpLogFile",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(getPath, Is.Not.Null);
            Assert.That(overwrite, Is.Not.Null);

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var logPath = (string)getPath.Invoke(null, null);
            Assert.That(logPath, Does.StartWith(projectRoot));
            Assert.That(logPath.Replace('\\', '/'), Does.EndWith("Logs/UPilot/csharp_object_dump.log"));

            var tempDirectory = Path.Combine(Path.GetTempPath(), "UPilotQuickDebugTests", Guid.NewGuid().ToString("N"));
            var tempPath = Path.Combine(tempDirectory, "csharp_object_dump.log");
            try
            {
                overwrite.Invoke(null, new object[] { tempPath, "first result" });
                overwrite.Invoke(null, new object[] { tempPath, "latest result" });
                Assert.That(File.ReadAllText(tempPath), Is.EqualTo("latest result"));
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
        }

        private static T ReadWindowField<T>(UPilotQuickDebugWindow window, string name)
        {
            var field = typeof(UPilotQuickDebugWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Expected window field: " + name);
            return (T)field.GetValue(window);
        }

        private static void WriteWindowField<T>(UPilotQuickDebugWindow window, string name, T value)
        {
            var field = typeof(UPilotQuickDebugWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Expected window field: " + name);
            field.SetValue(window, value);
        }

        private sealed class GetterErrorFixture
        {
            public int ThrowingProperty => throw new InvalidOperationException("first\nline" + new string('x', 400));
            public object NullProperty => null;
            public int AfterError => 42;
            public static int StaticThrowingProperty => throw new InvalidOperationException("first\nline" + new string('x', 400));
            public static int StaticAfterError => 84;
        }

        private sealed class TypeVisibilityFixture
        {
            public int Count = 3;
            public object PolymorphicValue = "runtime string";
        }

        private sealed class GenericDumpFixture<T>
        {
            public sealed class Nested<TValue> { }
        }

        private sealed class ReflectionObjectDumpFixture
        {
            public int Level = 10;
            public Delegate Callback = (Action)Noop;
            public Assembly Assembly = typeof(ReflectionObjectDumpFixture).Assembly;
            public Module Module = typeof(ReflectionObjectDumpFixture).Module;
            public Type TypeValue = typeof(ReflectionObjectDumpFixture);
            public MemberInfo Method = typeof(ReflectionObjectDumpFixture).GetMethod(
                "Noop",
                BindingFlags.Static | BindingFlags.NonPublic);
            public FieldInfo Field = typeof(ReflectionObjectDumpFixture).GetField(nameof(Level));
            public PropertyInfo Property = typeof(ReflectionObjectDumpFixture).GetProperty(nameof(Name));

            public string Name => "Hero";

            private static void Noop()
            {
            }
        }

        private sealed class CompactCollectionFixture
        {
            public int[] ArrayValue = { 1, 2, 3 };
            public List<string> ListValue = new() { "A", "B", "C" };
            public Dictionary<string, int> DictionaryValue = new()
            {
                { "first", 1 },
                { "second", 2 },
            };
            public int[] EmptyArray = Array.Empty<int>();
            public List<string> EmptyList = new();
            public Dictionary<string, int> EmptyDictionary = new();
            public List<string> NullableList = new() { "A", null, "B" };
            public List<string> EscapedList = new() { "quote\"\nline" };
            public List<string> BoundaryList = new() { new string('x', 178) };
            public List<string> LongList = new() { new string('x', 179) };
            public Dictionary<string, int> BoundaryDictionary = new()
            {
                { new string('k', 171), 1 },
                { "second", 2 },
            };
            public Dictionary<string, int> LongDictionary = new()
            {
                { new string('k', 172), 1 },
                { "second", 2 },
            };
            public List<string> FirstShortThenLongList = new() { "short", new string('x', 179) };
            public Dictionary<string, int> FirstShortThenLongDictionary = new()
            {
                { "first", 1 },
                { new string('k', 172), 2 },
            };
            public Dictionary<string, ComplexValue> ComplexDictionary = new()
            {
                { "complex", new ComplexValue { Number = 7 } },
            };
            public List<BaseValue> PolymorphicList = new() { new DerivedValue { Name = "derived" } };
            public NamedValueFields NamedFields = new() { key = "ordinary key", value = 9 };
            public int GetterCalls;

            public List<int> GetterList
            {
                get
                {
                    GetterCalls++;
                    return new List<int> { 3, 4 };
                }
            }

            public class ComplexValue
            {
                public int Number;
            }

            public class BaseValue
            {
            }

            public sealed class DerivedValue : BaseValue
            {
                public string Name;
            }

            public sealed class NamedValueFields
            {
                public string key;
                public int value;
            }
        }

        private sealed class LargeCollectionFixture
        {
            public readonly int[] ArrayValue;
            public readonly List<int> ListValue;
            public readonly Dictionary<string, int> DictionaryValue;

            public LargeCollectionFixture(int itemCount)
            {
                ArrayValue = Enumerable.Range(0, itemCount).ToArray();
                ListValue = Enumerable.Range(0, itemCount).ToList();
                DictionaryValue = Enumerable.Range(0, itemCount).ToDictionary(
                    value => "key-" + value.ToString("D3"),
                    value => value);
            }
        }
    }
}
