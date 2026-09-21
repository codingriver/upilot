// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal static class UPilotReflectionToolCheck
    {
        internal static Task<UPilotToolCheckResult> RunAsync(UPilotExecutionService execution,
            UPilotReflectionService service, CancellationToken token, Action<string> progress = null,
            TestUPilotQuickDebugObject sample = null)
        {
            var result = new UPilotToolCheckResult("unity_reflection_call") { Progress = progress };
            result.ExpectedChecks.AddRange(new[] { "静态与命名参数", "实例与类型化参数", "精确重载",
                "显式泛型", "ref/out", "Task", "ValueTask", "对象句柄", "非法参数预检", "目标异常", "预取消" });
            return UPilotQuickDebugToolCheck.RunAsync(result, service == null ? null : execution, token, async (report, ct) =>
            {
                sample ??= new TestUPilotQuickDebugObject();
                var handle = execution.Sessions.Store(report.SessionId, "object", sample);
                async Task Check(string name, string method, string arguments, string expected,
                    bool isStatic = false, string[] exactTypes = null, string[] genericTypes = null,
                    string resultMode = "inline", string awaitMode = "auto",
                    string expectedError = "", bool preCancelled = false)
                {
                    ct.ThrowIfCancellationRequested();
                    report.Begin(name);
                    var clock = Stopwatch.StartNew();
                    var before = sample.Calls;
                    var responses = 0;
                    using var callDeadline = new CancellationTokenSource(UPilotQuickDebugToolCheck.CallTimeoutMs);
                    using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, callDeadline.Token);
                    ReflectionCallResultPayload value = null;
                    ExecutionContractException error = null;
                    var payload = new ReflectionCallPayload
                    {
                        typeName = typeof(TestUPilotQuickDebugObject).FullName, methodName = method,
                        isStatic = isStatic, targetHandle = isStatic ? "" : handle, sessionId = report.SessionId,
                        argumentsJson = arguments, parameterTypeNames = exactTypes ?? Array.Empty<string>(),
                        genericTypeArguments = genericTypes ?? Array.Empty<string>(),
                        resultMode = resultMode, awaitMode = awaitMode, awaitTimeoutMs = 3000,
                    };
                    report.Inputs.Add(UPilotQuickDebugToolCheck.CaptureInput(name, payload, preCancelled));
                    try
                    {
                        await service.HandleCallAsync("qd-reflection-check-" + Guid.NewGuid().ToString("N"),
                            JsonUtility.ToJson(new ReflectionCallMessage { payload = payload }),
                            preCancelled ? new CancellationToken(true) : callCancellation.Token, action => action(),
                            r => { responses++; value = r; return Task.CompletedTask; },
                            e => { responses++; error = e; return Task.CompletedTask; });
                    }
                    finally
                    {
                        // Cancelling await observation does not terminate the invoked method.
                        await sample.PendingOperation;
                    }
                    ct.ThrowIfCancellationRequested();
                    var count = sample.Calls - before;
                    var expectedCalls = name == "非法参数预检" || preCancelled ? 0 : 1;
                    var passed = responses == 1 && count == expectedCalls;
                    if (!string.IsNullOrEmpty(expectedError))
                    {
                        passed &= error != null && error.Code == expectedError &&
                                  UPilotQuickDebugToolCheck.HasSideEffects(error, expectedCalls == 1);
                        if (name == "目标异常")
                            passed &= error != null && error.Message.Contains("QUICK_DEBUG_EXPECTED_FAILURE");
                    }
                    else
                    {
                        passed &= error == null && value?.resultValue != null &&
                                  !string.IsNullOrEmpty(value?.invokedSignature);
                        if (resultMode == "handle")
                        {
                            passed &= value?.resultValue?.serializationStatus == "handle" &&
                                      value?.resultValue?.kind == "object" &&
                                      !string.IsNullOrEmpty(value?.resultValue?.handle);
                            if (passed)
                                passed = ReferenceEquals(sample, execution.Sessions.Resolve(report.SessionId, value.resultValue.handle));
                        }
                        else if (name == "ref/out")
                        {
                            passed &= value != null && value.refOutArguments.Count == 2 &&
                                      value.refOutArguments.Any(a => a.name == "value" && a.direction == "ref" && a.value?.valueJson == "7") &&
                                      value.refOutArguments.Any(a => a.name == "text" && a.direction == "out" && a.value?.valueJson == "\"7\"");
                        }
                        else passed &= value?.resultValue?.valueJson == expected;
                        if (name == "Task" || name == "ValueTask")
                            passed &= value != null && value.wasAwaitable && value.awaitableStatus == "Completed";
                        if (name == "精确重载") passed &= value?.invokedSignature?.Contains("System.Int32") == true;
                    }
                    var actual = error != null ? UPilotQuickDebugToolCheck.ErrorText(error) :
                        "value=" + value?.resultValue?.valueJson + "; signature=" + value?.invokedSignature +
                        "; awaitableStatus=" + value?.awaitableStatus + "; handle=" + value?.resultValue?.handle +
                        "; serializationStatus=" + value?.resultValue?.serializationStatus;
                    if (value != null)
                        foreach (var argument in value.refOutArguments)
                            actual += "; " + argument.direction + " " + argument.name + "=" + argument.value?.valueJson;
                    if (callDeadline.IsCancellationRequested)
                    {
                        passed = false;
                        actual += "; 单项调用预算超时";
                    }
                    report.Record(name, passed,
                        (string.IsNullOrEmpty(expectedError) ? expected : expectedError) +
                        "; calls=" + expectedCalls + "; responses=1",
                        actual + "; calls=" + count + "; responses=" + responses, clock.ElapsedMilliseconds);
                }

                await Check("静态与命名参数", "Add",
                    Args(Argument("second", "3"), Argument("first", "2"),
                        "{\"name\":\"counter\",\"value\":{\"kind\":\"handle\",\"handle\":\"" + handle + "\"}}"),
                    "5", isStatic: true);
                await Check("实例与类型化参数", "Count", Args(Argument("value", "23")), "23");
                await Check("精确重载", "Choose", Args(Argument("value", "7")), "\"int:7\"",
                    exactTypes: new[] { "System.Int32" });
                await Check("显式泛型", "Identity", Args(Argument("value", "29")), "29",
                    genericTypes: new[] { "System.Int32" });
                await Check("ref/out", "RefOut", Args(Argument("value", "5", "ref"),
                    "{\"name\":\"text\",\"direction\":\"out\",\"value\":{\"kind\":\"null\",\"typeName\":\"System.String\"}}"),
                    "ref value=7; out text=\"7\"");
                await Check("Task", "TaskValue", "", "17");
                await Check("ValueTask", "ValueTaskValue", "", "19");
                await Check("对象句柄", "Self", "", "same instance handle", resultMode: "handle");
                await Check("非法参数预检", "Count", Args(Argument("value", "1")), "",
                    awaitMode: "invalid", expectedError: "INVALID_AWAIT_MODE");
                await Check("目标异常", "Throw", "", "", expectedError: "REFLECTION_CALL_FAILED");
                await Check("预取消", "Count", Args(Argument("value", "1")), "",
                    expectedError: "EXECUTION_CANCELLED", preCancelled: true);
            });
        }

        private static string Args(params string[] arguments) => "{\"items\":[" + string.Join(",", arguments) + "]}";
        private static string Argument(string name, string number, string direction = "in") =>
            "{\"name\":\"" + name + "\",\"direction\":\"" + direction +
            "\",\"value\":{\"kind\":\"literal\",\"typeName\":\"System.Int32\",\"valueJson\":\"" + number + "\"}}";
    }
}
