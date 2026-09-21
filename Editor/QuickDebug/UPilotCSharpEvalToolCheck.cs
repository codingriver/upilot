// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal static class UPilotCSharpEvalToolCheck
    {
        internal static Task<UPilotToolCheckResult> RunAsync(
            UPilotExecutionService service, CancellationToken token,
            Func<Func<object>, object> invocationScheduler = null, Action<string> progress = null)
        {
            var result = new UPilotToolCheckResult("csharp_eval") { Progress = progress };
            result.ExpectedChecks.AddRange(new[] { "算术与返回值", "输入变量", "分支与循环", "集合",
                "try/catch/finally", "调用内闭包", "async/await", "会话变量写入", "会话变量保持",
                "对象句柄", "语法错误", "预算限制", "预取消" });
            return UPilotQuickDebugToolCheck.RunAsync(result, service, token, async (report, ct) =>
            {
                async Task<bool> Check(string name, string code, string expected,
                    string variables = "", string session = "", string resultMode = "inline",
                    string limits = "", string errorCode = "", bool preCancelled = false)
                {
                    ct.ThrowIfCancellationRequested();
                    report.Begin(name);
                    var clock = Stopwatch.StartNew();
                    CSharpEvalResultPayload value = null;
                    ExecutionContractException error = null;
                    var responses = 0;
                    using var callDeadline = new CancellationTokenSource(UPilotQuickDebugToolCheck.CallTimeoutMs);
                    using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, callDeadline.Token);
                    var payload = new CSharpEvalPayload
                    {
                        code = code, variablesJson = variables, sessionId = session, resultMode = resultMode,
                        executionBackend = "auto",
                        limitsJson = string.IsNullOrEmpty(limits) ? "{\"timeoutMs\":3000}" : limits,
                    };
                    report.Inputs.Add(UPilotQuickDebugToolCheck.CaptureInput(name, payload, preCancelled));
                    await service.HandleCSharpEvalAsync("qd-eval-check-" + Guid.NewGuid().ToString("N"),
                        JsonUtility.ToJson(new CSharpEvalMessage { payload = payload }),
                        preCancelled ? new CancellationToken(true) : callCancellation.Token, action => action(),
                        r => { responses++; value = r; return Task.CompletedTask; },
                        e => { responses++; error = e; return Task.CompletedTask; }, invocationScheduler);
                    ct.ThrowIfCancellationRequested();
                    bool passed;
                    string actual;
                    if (!string.IsNullOrEmpty(errorCode))
                    {
                        passed = error != null && error.Code == errorCode && responses == 1 &&
                                 (name == "预算限制" || UPilotQuickDebugToolCheck.HasSideEffects(error, false));
                        actual = UPilotQuickDebugToolCheck.ErrorText(error);
                    }
                    else
                    {
                        passed = error == null && value != null && value.status == "Succeeded" &&
                                 responses == 1 && value.resultValue != null;
                        if (resultMode == "handle")
                        {
                            passed = passed && value.resultValue.serializationStatus == "handle" &&
                                     value.resultValue.kind == "object" &&
                                     !string.IsNullOrEmpty(value.resultHandle) && value.sessionId == session;
                            if (passed)
                                passed = service.Sessions.Resolve(session, value.resultHandle) is TestUPilotQuickDebugObject;
                        }
                        else passed = passed && value.resultValue.valueJson == expected;
                        actual = error != null ? UPilotQuickDebugToolCheck.ErrorText(error) :
                            "value=" + value?.resultValue?.valueJson + "; handle=" + value?.resultHandle +
                            "; serializationStatus=" + value?.resultValue?.serializationStatus +
                            "; backendUsed=" + value?.backendUsed;
                    }
                    if (callDeadline.IsCancellationRequested)
                    {
                        passed = false;
                        actual += "; 单项调用预算超时";
                    }
                    report.Record(name, passed, string.IsNullOrEmpty(errorCode) ? expected : errorCode,
                        actual + "; responses=" + responses, clock.ElapsedMilliseconds);
                    return passed;
                }

                await Check("算术与返回值", "return 2 + 3 * 4;", "14");
                await Check("输入变量", "return a + b;", "30",
                    "{\"items\":[{\"name\":\"a\",\"value\":{\"kind\":\"literal\",\"typeName\":\"System.Int32\",\"valueJson\":\"10\"}}," +
                    "{\"name\":\"b\",\"value\":{\"kind\":\"literal\",\"typeName\":\"System.Int32\",\"valueJson\":\"20\"}}]}");
                await Check("分支与循环", "var sum = 0; for (var i = 0; i < 5; i++) { if (i > 1) sum += i; } return sum;", "9");
                await Check("集合", "var list = new System.Collections.Generic.List<int>(); list.Add(4); list.Add(5); return list[0] + list[1];", "9");
                await Check("try/catch/finally", "var r = 0; try { throw new System.InvalidOperationException(\"sample\"); } catch (System.InvalidOperationException) { r = 2; } finally { r += 3; } return r;", "5");
                await Check("调用内闭包", "var offset = 2; var add = (int x) => { return x + offset; }; offset = 4; return add(3);", "7");
                await Check("async/await", "return await System.Threading.Tasks.Task.FromResult(42);", "42");
                if (await Check("会话变量写入", "var counter = 10; return counter;", "10", session: report.SessionId))
                    await Check("会话变量保持", "counter += 1; return counter;", "11", session: report.SessionId);
                await Check("对象句柄", "new CodingRiver.UPilot.TestUPilotQuickDebugObject()", "session object handle",
                    session: report.SessionId, resultMode: "handle");
                await Check("语法错误", "?invalid syntax", "", errorCode: "CSHARP_PARSE_ERROR");
                await Check("预算限制", "var sum = 0; for (var i = 0; i < 10; i++) sum += i; return sum;", "",
                    limits: "{\"timeoutMs\":3000,\"maxLoopIterations\":1}", errorCode: "EXECUTION_BUDGET_EXCEEDED");
                await Check("预取消", "return 1;", "", errorCode: "EXECUTION_CANCELLED", preCancelled: true);
            });
        }
    }
}
