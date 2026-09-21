// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;

namespace CodingRiver.UPilot
{
    internal enum UPilotToolCheckStatus { Passed, Failed, UnableToCheck, Cancelled }

    internal sealed class UPilotToolCheckInput
    {
        internal string Name;
        internal string PayloadJson;
        internal bool PreCancelled;
    }

    internal sealed class UPilotToolCheckEntry
    {
        internal string Name;
        internal bool Passed;
        internal string Expected;
        internal string Actual;
        internal long ElapsedMs;
    }

    internal class UPilotToolCheckResult
    {
        internal string ToolId;
        internal string RunId = Guid.NewGuid().ToString("N");
        internal DateTime StartedAtUtc = DateTime.UtcNow;
        internal long ElapsedMs;
        internal UPilotToolCheckStatus Status = UPilotToolCheckStatus.Passed;
        internal string Stage = "初始化";
        internal string SessionId = "";
        internal bool SessionClosed;
        internal bool CleanupSucceeded = true;
        internal Action<string> Progress;
        internal readonly List<string> PassedChecks = new List<string>();
        internal readonly List<string> FailedChecks = new List<string>();
        internal readonly List<string> SkippedChecks = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> ExpectedChecks = new List<string>();
        internal readonly List<UPilotToolCheckEntry> Checks = new List<UPilotToolCheckEntry>();
        internal readonly List<UPilotToolCheckInput> Inputs = new List<UPilotToolCheckInput>();

        internal UPilotToolCheckResult(string toolId) { ToolId = toolId; }
        internal string DisplayName => UPilotQuickDebugToolCheck.DisplayName(ToolId);
        internal bool IsErrorForUi => Status == UPilotToolCheckStatus.Failed || Status == UPilotToolCheckStatus.UnableToCheck;

        internal void Begin(string name)
        {
            Stage = name;
            Progress?.Invoke(DisplayName + " " + (Checks.Count + 1) + "/" + ExpectedChecks.Count + " " + name);
        }

        internal void Record(string name, bool passed, string expected, string actual, long elapsedMs = 0)
        {
            Checks.Add(new UPilotToolCheckEntry
            {
                Name = name, Passed = passed, Expected = expected, Actual = actual, ElapsedMs = elapsedMs,
            });
            if (passed) PassedChecks.Add(name);
            else FailedChecks.Add(name + "：预期 " + expected + "；实际 " + actual);
        }

        internal void Complete()
        {
            foreach (var name in ExpectedChecks)
                if (!Checks.Any(check => check.Name == name) && !SkippedChecks.Contains(name))
                    SkippedChecks.Add(name);
            if (Status == UPilotToolCheckStatus.Passed &&
                (FailedChecks.Count > 0 || SkippedChecks.Count > 0 || !CleanupSucceeded))
                Status = UPilotToolCheckStatus.Failed;
        }

        internal string BuildSummary(bool reportSaved = false)
        {
            var text = new StringBuilder();
            text.AppendLine("[“" + DisplayName + "”工具检查：" + StatusLabel + "]");
            if (PassedChecks.Count > 0) text.AppendLine("通过项：" + string.Join("、", PassedChecks));
            if (FailedChecks.Count > 0) text.AppendLine("失败项：" + string.Join("；", FailedChecks));
            if (SkippedChecks.Count > 0) text.AppendLine("未执行：" + string.Join("、", SkippedChecks));
            if (Warnings.Count > 0) text.AppendLine("警告：" + string.Join("；", Warnings));
            text.Append(reportSaved
                ? "完整结果：" + UPilotQuickDebugToolCheck.RelativeLogPath(ToolId)
                : "诊断报告未保存。");
            return text.ToString();
        }

        internal virtual string BuildFullReport()
        {
            var text = new StringBuilder();
            text.AppendLine("[“" + DisplayName + "”工具检查：" + StatusLabel + "]");
            text.AppendLine("tool: " + ToolId + "\nrunId: " + RunId);
            text.AppendLine("startedAtUtc: " + StartedAtUtc.ToString("O") + "\nelapsedMs: " + ElapsedMs);
            text.AppendLine("阶段：" + Stage);
            text.AppendLine("sessionId: " + SessionId + "\nsessionClosed: " + SessionClosed +
                            "\ncleanupSucceeded: " + CleanupSucceeded);
            AppendSection(text, "通过项", PassedChecks);
            AppendSection(text, "失败项", FailedChecks);
            AppendSection(text, "未执行（中断或前置条件失败）", SkippedChecks);
            AppendSection(text, "警告", Warnings);
            foreach (var input in Inputs.Where(input => !Checks.Any(check => check.Name == input.Name)))
            {
                text.AppendLine("\n[" + input.Name + "] 调用输入");
                AppendInput(text, input);
            }
            foreach (var check in Checks)
            {
                text.AppendLine("\n[" + check.Name + "] " + (check.Passed ? "通过" : "未通过") +
                                " elapsedMs=" + check.ElapsedMs);
                foreach (var input in Inputs.Where(input => input.Name == check.Name))
                    AppendInput(text, input);
                text.AppendLine("预期：" + check.Expected);
                text.AppendLine("实际：" + check.Actual);
            }
            text.AppendLine("\n检查范围：");
            text.AppendLine("- 本检查仅覆盖本地 Unity 服务的内置代表性样例，不代表全部业务类型或完整功能验收。");
            text.AppendLine("- 不验证 HTTP MCP、Server 注册、权限或传输链路。取消不回滚已经发生的副作用。");
            return text.ToString().TrimEnd();
        }

        private string StatusLabel => Status == UPilotToolCheckStatus.Passed ? "通过" :
            Status == UPilotToolCheckStatus.Failed ? "未通过" :
            Status == UPilotToolCheckStatus.Cancelled ? "已取消" : "无法检查";

        private static void AppendInput(StringBuilder text, UPilotToolCheckInput input)
        {
            text.AppendLine("输入：");
            text.AppendLine("callTimeoutMs: " + UPilotQuickDebugToolCheck.CallTimeoutMs +
                            "\npreCancelled: " + input.PreCancelled);
            text.AppendLine(input.PayloadJson);
        }

        private static void AppendSection(StringBuilder text, string title, List<string> values)
        {
            if (values.Count == 0) return;
            text.AppendLine(title + "：");
            foreach (var value in values) text.AppendLine("- " + value);
        }
    }

    internal static class UPilotQuickDebugToolCheck
    {
        internal const int CallTimeoutMs = 3000;
        internal const int RunTimeoutMs = 30000;
        internal static readonly string[] ToolIds = { "csharp_eval", "unity_reflection_call", "csharp_object_dump" };

        internal static UPilotToolCheckInput CaptureInput(string name, object payload, bool preCancelled = false) =>
            new UPilotToolCheckInput
            {
                Name = name, PayloadJson = UnityEngine.JsonUtility.ToJson(payload, true),
                PreCancelled = preCancelled,
            };

        internal static string DisplayName(string toolId)
        {
            switch (toolId)
            {
                case "csharp_eval": return "运行 C# 代码";
                case "unity_reflection_call": return "调用现有方法";
                case "csharp_object_dump": return "查看对象信息";
                default: throw new ArgumentOutOfRangeException(nameof(toolId));
            }
        }

        internal static string RelativeLogPath(string toolId)
        {
            DisplayName(toolId);
            return "Logs/UPilot/Diagnostics/" + toolId + ".log";
        }

        internal static string PendingReport(UPilotToolCheckResult result) =>
            "[“" + result.DisplayName + "”工具检查：未完成]\nrunId: " + result.RunId +
            "\nstartedAtUtc: " + result.StartedAtUtc.ToString("O") +
            "\n本次检查尚无终态。关闭窗口或 Domain Reload 后不自动恢复，不可作为通过证据。";

        internal static void WriteReport(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static string ErrorText(ExecutionContractException error)
        {
            if (error == null) return "";
            var text = "[" + error.Code + "] " + error.Message;
            foreach (var key in new[] { "stage", "sideEffectsMayHaveOccurred", "nextAction" })
                if (error.Detail.TryGetValue(key, out var value)) text += "; " + key + "=" + value;
            return text;
        }

        internal static bool HasSideEffects(ExecutionContractException error, bool expected) =>
            error != null && error.Detail.TryGetValue("sideEffectsMayHaveOccurred", out var value) &&
            value is bool actual && actual == expected;

        internal static void ApplyCleanup(UPilotToolCheckResult result, ExecutionSessionCloseResult close)
        {
            result.SessionClosed = close != null && close.closed;
            result.CleanupSucceeded = result.SessionClosed && close.asyncOperationsStillRunning == 0 &&
                                      close.activeAsyncOperations == 0 && (close.cleanupErrors?.Length ?? 0) == 0;
            if (!result.CleanupSucceeded)
            {
                result.FailedChecks.Add("清理失败：临时会话未完全释放；" +
                    string.Join("；", close?.cleanupErrors ?? Array.Empty<string>()));
                if (result.Status == UPilotToolCheckStatus.Passed) result.Status = UPilotToolCheckStatus.Failed;
            }
        }

        internal static async Task<UPilotToolCheckResult> RunAsync(
            UPilotToolCheckResult result, UPilotExecutionService service, CancellationToken token,
            Func<UPilotToolCheckResult, CancellationToken, Task> check, int timeoutMs = RunTimeoutMs)
        {
            var clock = Stopwatch.StartNew();
            using (var deadline = new CancellationTokenSource(timeoutMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (service == null)
                    {
                        result.Status = UPilotToolCheckStatus.UnableToCheck;
                        result.FailedChecks.Add("UPilot Bridge 尚未就绪。");
                    }
                    else
                    {
                        result.SessionId = service.Sessions.Open("快捷调试-" + result.DisplayName, 600, 32, 4, 4, 4).Id;
                        await check(result, linked.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Status = token.IsCancellationRequested
                        ? UPilotToolCheckStatus.Cancelled : UPilotToolCheckStatus.Failed;
                    if (token.IsCancellationRequested) result.Warnings.Add("用户取消了检查。");
                    else result.FailedChecks.Add("诊断总预算超时。");
                }
                catch (Exception ex)
                {
                    result.Status = result.Stage == "初始化"
                        ? UPilotToolCheckStatus.UnableToCheck : UPilotToolCheckStatus.Failed;
                    result.FailedChecks.Add(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(result.SessionId))
                    {
                        try { ApplyCleanup(result, service.Sessions.Close(result.SessionId)); }
                        catch (Exception ex)
                        {
                            result.CleanupSucceeded = false;
                            result.FailedChecks.Add("关闭临时会话失败：" + ex.Message);
                        }
                    }
                    if (token.IsCancellationRequested) result.Status = UPilotToolCheckStatus.Cancelled;
                    else if (deadline.IsCancellationRequested)
                    {
                        result.Status = UPilotToolCheckStatus.Failed;
                        if (!result.FailedChecks.Contains("诊断总预算超时。")) result.FailedChecks.Add("诊断总预算超时。");
                    }
                    result.ElapsedMs = clock.ElapsedMilliseconds;
                    result.Complete();
                }
            }
            return result;
        }
    }
}
