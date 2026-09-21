// -----------------------------------------------------------------------
// UPilot Editor - Quick Debug object-dump tool check
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotObjectDumpToolCheckResult : UPilotToolCheckResult
    {
        internal string TypeName = "";
        internal int TotalNodes;
        internal string RawDump = "";

        internal UPilotObjectDumpToolCheckResult() : base("csharp_object_dump")
        {
            ExpectedChecks.AddRange(new[] { "返回结构", "类型覆盖", "三层继承", "集合与嵌套对象",
                "多态", "循环引用", "安全限制" });
        }

        internal override string BuildFullReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine(base.BuildFullReport());
            if (!string.IsNullOrWhiteSpace(TypeName))
                sb.AppendLine("typeName：" + TypeName);
            if (TotalNodes > 0)
                sb.AppendLine("totalNodes：" + TotalNodes);
            sb.AppendLine();
            sb.AppendLine("限制说明：");
            sb.AppendLine("- const、IntPtr/UIntPtr、索引器和只写属性按设计不输出。");
            sb.AppendLine("- Thread 仅显示忽略摘要；Delegate、Assembly、Module 和 MemberInfo 默认显示反射摘要，不展开 CLR 内部结构。");
            sb.AppendLine("- getter 抛异常显示错误摘要，不中断其他成员查看；固定 maxDepth 产生的 [max depth] 不单独判失败。");
            if (!string.IsNullOrWhiteSpace(RawDump))
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("[原始对象信息]");
                sb.Append(RawDump.TrimEnd());
            }
            return sb.ToString().TrimEnd();
        }

    }

    internal static class UPilotObjectDumpToolCheck
    {
        internal const string SampleCode = "new CodingRiver.UPilot.TestUPilotDumpObject()";
        internal const int MaxDepth = 6;
        internal const int MaxFieldsPerNode = 300;
        internal const int MaxTotalNodes = 10000;

        private static readonly string[] SupportedMemberNames =
        {
            "BoolValue", "CharValue", "SByteValue", "ByteValue",
            "Int16Value", "UInt16Value", "Int32Value", "UInt32Value",
            "Int64Value", "UInt64Value", "FloatValue", "DoubleValue",
            "DecimalValue", "StringValue", "EnumValue", "GuidValue",
            "DateTimeValue", "DateTimeOffsetValue", "TimeSpanValue",
            "UriValue", "VersionValue", "TypeValue", "NullableIntValue",
            "Vector2Value", "Vector3Value", "QuaternionValue", "ColorValue",
            "ArrayValue", "ListValue", "DictionaryValue", "LargeListValue",
            "LargeDictionaryValue", "LongTextListValue", "LongTextDictionaryValue",
            "NestedValue",
            "PolymorphicValue", "ObjectValue", "NullValue", "CircularReference",
            "IgnoredThread", "DelegateValue", ".ChildPublicProperty", ".ThrowingProperty",
        };

        private static readonly string[] InheritedMemberNames =
        {
            "GrandParentPrivateInt", "GrandParentProtectedString", "GrandParentPublicLong",
            ".GrandParentPublicProperty", "ParentPrivateGuid", "ParentProtectedDecimal",
            "ParentPublicDateTime", ".ParentPublicProperty",
        };

        private static readonly string[] UnsupportedMemberNames =
        {
            "ConstantValue", "StaticString", "NativePointer", "NativeUnsignedPointer",
            ".Item", ".WriteOnlyProperty",
        };

        internal static async Task<UPilotObjectDumpToolCheckResult> RunAsync(
            UPilotExecutionService service,
            CancellationToken token,
            Func<Func<object>, object> invocationScheduler = null,
            Action<string> progress = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var startedAt = DateTime.UtcNow;
            using (var deadline = new CancellationTokenSource(UPilotQuickDebugToolCheck.RunTimeoutMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                var result = await RunCoreAsync(service, linked.Token, invocationScheduler, progress);
                if (token.IsCancellationRequested)
                    result.Status = UPilotToolCheckStatus.Cancelled;
                else if (deadline.IsCancellationRequested)
                {
                    result.Status = UPilotToolCheckStatus.Failed;
                    result.FailedChecks.Add("诊断总预算超时。");
                }
                result.StartedAtUtc = startedAt;
                result.ElapsedMs = clock.ElapsedMilliseconds;
                result.Complete();
                return result;
            }
        }

        private static async Task<UPilotObjectDumpToolCheckResult> RunCoreAsync(
            UPilotExecutionService service, CancellationToken token,
            Func<Func<object>, object> invocationScheduler, Action<string> progress)
        {
            if (service == null)
                return CreateTerminal(
                    UPilotToolCheckStatus.UnableToCheck,
                    "初始化",
                    "UPilot Bridge 尚未就绪，未调用 csharp_object_dump。");

            UPilotObjectDumpToolCheckResult outcome = null;
            var inputs = new List<UPilotToolCheckInput>();
            string ownedSessionId = null;
            var stage = "创建测试对象";

            try
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke("查看对象信息 1/3 创建测试对象");
                var session = service.Sessions.Open("快捷调试-检查查看对象信息工具", 600, 32, 4, 4, 4);
                ownedSessionId = session.Id;

                CSharpEvalResultPayload evalResult = null;
                string evalError = null;
                var evalPayload = new CSharpEvalPayload
                {
                    code = SampleCode,
                    sessionId = ownedSessionId,
                    variablesJson = "",
                    limitsJson = "{\"timeoutMs\":3000}",
                    resultMode = "handle",
                    executionBackend = "auto",
                };
                inputs.Add(UPilotQuickDebugToolCheck.CaptureInput("创建测试对象（csharp_eval）", evalPayload));
                var evalJson = JsonUtility.ToJson(new CSharpEvalMessage { payload = evalPayload });
                using var evalDeadline = new CancellationTokenSource(UPilotQuickDebugToolCheck.CallTimeoutMs);
                using var evalCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, evalDeadline.Token);
                await service.HandleCSharpEvalAsync(
                    "qd-object-dump-check-eval-" + Guid.NewGuid().ToString("N"),
                    evalJson,
                    evalCancellation.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        evalResult = result;
                        return Task.CompletedTask;
                    },
                    sendError: error =>
                    {
                        evalError = "[" + error.Code + "] " + error.Message;
                        return Task.CompletedTask;
                    },
                    invocationScheduler: invocationScheduler);

                if (token.IsCancellationRequested)
                {
                    outcome = CreateTerminal(UPilotToolCheckStatus.Cancelled, stage, "用户取消了检查。");
                }
                else if (evalDeadline.IsCancellationRequested)
                {
                    outcome = CreateTerminal(UPilotToolCheckStatus.Failed, stage, "单项调用预算超时。");
                }
                else if (!string.IsNullOrWhiteSpace(evalError))
                {
                    outcome = CreateTerminal(
                        UPilotToolCheckStatus.UnableToCheck,
                        stage,
                        "测试对象创建失败：" + evalError);
                }
                else if (evalResult == null || string.IsNullOrWhiteSpace(evalResult.sessionId) ||
                         string.IsNullOrWhiteSpace(evalResult.resultHandle))
                {
                    outcome = CreateTerminal(
                        UPilotToolCheckStatus.UnableToCheck,
                        stage,
                        "测试对象未返回 sessionId 或 handle，未调用 csharp_object_dump。");
                }
                else
                {
                    stage = "调用 csharp_object_dump";
                    progress?.Invoke("查看对象信息 2/3 调用工具");
                    ObjectDumpResultPayload dumpResult = null;
                    string dumpError = null;
                    var dumpPayload = new ObjectDumpPayload
                    {
                        sessionId = evalResult.sessionId,
                        handle = evalResult.resultHandle,
                        maxDepth = MaxDepth,
                        maxFieldsPerNode = MaxFieldsPerNode,
                        maxTotalNodes = MaxTotalNodes,
                        includeStatic = false,
                        includeTypeNames = true,
                        expandReflectionTypes = false,
                        outputFormat = "text",
                        indentation = "  ",
                        ignoreTypes = Array.Empty<string>(),
                    };
                    inputs.Add(UPilotQuickDebugToolCheck.CaptureInput("查看对象信息（csharp_object_dump）", dumpPayload));
                    var dumpJson = JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });
                    using var dumpDeadline = new CancellationTokenSource(UPilotQuickDebugToolCheck.CallTimeoutMs);
                    using var dumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, dumpDeadline.Token);
                    await service.HandleObjectDumpAsync(
                        "qd-object-dump-check-" + Guid.NewGuid().ToString("N"),
                        dumpJson,
                        dumpCancellation.Token,
                        enqueue: action => action(),
                        sendResult: result =>
                        {
                            dumpResult = result;
                            return Task.CompletedTask;
                        },
                        sendError: error =>
                        {
                            dumpError = "[" + error.Code + "] " + error.Message;
                            return Task.CompletedTask;
                        });

                    if (token.IsCancellationRequested)
                        outcome = CreateTerminal(UPilotToolCheckStatus.Cancelled, stage, "用户取消了检查。");
                    else if (dumpDeadline.IsCancellationRequested)
                        outcome = CreateTerminal(UPilotToolCheckStatus.Failed, stage, "单项调用预算超时。");
                    else if (!string.IsNullOrWhiteSpace(dumpError))
                        outcome = CreateTerminal(UPilotToolCheckStatus.Failed, stage, dumpError);
                    else
                    {
                        progress?.Invoke("查看对象信息 3/3 校验结果");
                        outcome = Evaluate(dumpResult);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                outcome = CreateTerminal(UPilotToolCheckStatus.Cancelled, stage, "用户取消了检查。");
            }
            catch (Exception ex)
            {
                var status = string.Equals(stage, "调用 csharp_object_dump", StringComparison.Ordinal)
                    ? UPilotToolCheckStatus.Failed
                    : UPilotToolCheckStatus.UnableToCheck;
                outcome = CreateTerminal(status, stage, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (outcome == null)
                    outcome = CreateTerminal(UPilotToolCheckStatus.UnableToCheck, stage, "检查未返回结果。");

                outcome.Inputs.AddRange(inputs);
                outcome.SessionId = ownedSessionId ?? "";
                if (!string.IsNullOrWhiteSpace(ownedSessionId))
                {
                    try
                    {
                        UPilotQuickDebugToolCheck.ApplyCleanup(outcome, service.Sessions.Close(ownedSessionId));
                    }
                    catch (Exception ex)
                    {
                        outcome.CleanupSucceeded = false;
                        outcome.FailedChecks.Add("关闭临时会话失败：" + ex.Message);
                    }
                }
            }

            return outcome;
        }

        internal static UPilotObjectDumpToolCheckResult Evaluate(ObjectDumpResultPayload result)
        {
            var outcome = new UPilotObjectDumpToolCheckResult
            {
                Status = UPilotToolCheckStatus.Passed,
                Stage = "校验对象信息",
                TypeName = result?.typeName ?? "",
                TotalNodes = result?.totalNodes ?? 0,
                RawDump = result?.text ?? "",
            };

            if (result == null)
            {
                outcome.FailedChecks.Add("csharp_object_dump 未返回结果。");
                outcome.Status = UPilotToolCheckStatus.Failed;
                return outcome;
            }

            var expectedType = typeof(TestUPilotDumpObject).FullName;
            AddCheck(
                outcome,
                "返回结构",
                string.Equals(result.typeName, expectedType, StringComparison.Ordinal) &&
                result.root != null && result.totalNodes > 0 && !result.truncated,
                result.truncated
                    ? "结果达到总节点上限：" + result.truncateReason
                    : "返回类型、根节点或节点数不正确，期望类型 " + expectedType + "。");

            if (result.root != null)
            {
                RequireMembers(outcome, result.root, "类型覆盖", SupportedMemberNames);
                RequireMembers(outcome, result.root, "三层继承", InheritedMemberNames);

                var collectionsOk = HasChildren(result.root, "ArrayValue") &&
                                    HasChildren(result.root, "ListValue") &&
                                    HasChildren(result.root, "DictionaryValue") &&
                                    HasChildCount(result.root, "LargeListValue", 40) &&
                                    HasChildCount(result.root, "LargeDictionaryValue", 16) &&
                                    HasChildCount(result.root, "LongTextListValue", 2) &&
                                    HasChildCount(result.root, "LongTextDictionaryValue", 2) &&
                                    HasLargeCollectionText(result.text) &&
                                    HasNestedObjectGraph(result.root);
                AddCheck(outcome, "集合与嵌套对象", collectionsOk,
                    "数组、列表、字典、大集合文本格式或多层嵌套对象不符合预期。");

                var polymorphic = FindDirectChild(result.root, "PolymorphicValue");
                AddCheck(
                    outcome,
                    "多态",
                    polymorphic != null &&
                    string.Equals(polymorphic.declaredType, typeof(TestUPilotDumpGrandParent).FullName, StringComparison.Ordinal) &&
                    string.Equals(polymorphic.runtimeType, typeof(TestUPilotDumpParent).FullName, StringComparison.Ordinal),
                    "PolymorphicValue 的声明类型或运行时类型不正确。");

                var circular = FindDirectChild(result.root, "CircularReference");
                AddCheck(
                    outcome,
                    "循环引用",
                    circular != null && (circular.value ?? "").IndexOf("(circular ref:", StringComparison.Ordinal) >= 0,
                    "CircularReference 未被识别为循环引用。");

                var ignoredThread = FindDirectChild(result.root, "IgnoredThread");
                var delegateValue = FindDirectChild(result.root, "DelegateValue");
                var throwingProperty = FindDirectChild(result.root, ".ThrowingProperty");
                var unexpected = FindPresentMembers(result.root, UnsupportedMemberNames);
                var delegateSummarized = delegateValue != null &&
                                         (delegateValue.children == null || delegateValue.children.Length == 0) &&
                                         (delegateValue.value ?? "").IndexOf("reflection summary:", StringComparison.Ordinal) >= 0 &&
                                         (result.text ?? "").IndexOf("RuntimeMethodInfo", StringComparison.Ordinal) < 0 &&
                                         (result.text ?? "").IndexOf("<RawData>", StringComparison.Ordinal) < 0;
                var safetyOk = unexpected.Count == 0 && ignoredThread != null &&
                               (ignoredThread.value ?? "").IndexOf("ignored:", StringComparison.Ordinal) >= 0 &&
                               throwingProperty != null &&
                               (throwingProperty.value ?? "").StartsWith("(error: InvalidOperationException - ", StringComparison.Ordinal) &&
                               (throwingProperty.value ?? "").Contains("TEST_UPILOT_DUMP_GETTER_FAILURE") &&
                               FindDirectChild(result.root, ".ParentPublicProperty")?.value == "\"parent property\"" &&
                               delegateSummarized;
                AddCheck(
                    outcome,
                    "安全限制",
                    safetyOk,
                    unexpected.Count > 0
                        ? "本应跳过的成员仍然存在：" + string.Join(", ", unexpected)
                        : "Thread 未安全忽略、Delegate 未使用反射摘要，或 .ThrowingProperty 未返回错误摘要/其他属性未正常输出。");
            }

            if (outcome.FailedChecks.Count > 0)
                outcome.Status = UPilotToolCheckStatus.Failed;
            return outcome;
        }

        internal static UPilotObjectDumpToolCheckResult CreateTerminal(
            UPilotToolCheckStatus status,
            string stage,
            string detail)
        {
            var result = new UPilotObjectDumpToolCheckResult
            {
                Status = status,
                Stage = stage ?? "",
            };
            if (!string.IsNullOrWhiteSpace(detail))
            {
                if (status == UPilotToolCheckStatus.Cancelled)
                    result.Warnings.Add(detail);
                else
                    result.FailedChecks.Add(detail);
            }
            return result;
        }

        private static void AddCheck(
            UPilotObjectDumpToolCheckResult outcome,
            string name,
            bool passed,
            string failure)
        {
            outcome.Record(name, passed, name + "符合内置样例约定", passed ? "匹配" : failure);
        }

        private static void RequireMembers(
            UPilotObjectDumpToolCheckResult outcome,
            ObjectDumpNodeJson root,
            string checkName,
            string[] names)
        {
            var missing = new List<string>();
            foreach (var name in names)
            {
                if (FindDirectChild(root, name) == null)
                    missing.Add(name);
            }
            AddCheck(
                outcome,
                checkName,
                missing.Count == 0,
                "缺少成员 " + string.Join(", ", missing));
        }

        private static bool HasChildren(ObjectDumpNodeJson root, string name)
        {
            var node = FindDirectChild(root, name);
            return node?.children != null && node.children.Length > 0;
        }

        private static bool HasChildCount(ObjectDumpNodeJson root, string name, int expectedCount)
        {
            var node = FindDirectChild(root, name);
            return node?.children != null && node.children.Length == expectedCount;
        }

        private static bool HasNestedObjectGraph(ObjectDumpNodeJson root)
        {
            var nested = FindDirectChild(root, "NestedValue");
            var child = FindDirectChild(nested, "NestedChild");
            var leaf = FindDirectChild(child, "NestedLeaf");
            return FindDirectChild(leaf, "LeafName") != null &&
                   FindDirectChild(leaf, "LeafNumber") != null;
        }

        private static bool HasLargeCollectionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var normalized = text.Replace("\r\n", "\n");
            return normalized.IndexOf(
                       "LargeListValue (System.Collections.Generic.List<System.Int32>): [0, 1",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(", 39]", StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "LargeDictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"large-key-00\": 0",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(", \"large-key-15\": 15 }", StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "LongTextListValue (System.Collections.Generic.List<System.String>)\n    [0]",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "LongTextDictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>):\n    { ",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "NestedValue (CodingRiver.UPilot.TestUPilotDumpNestedObject) {",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "NestedChild (CodingRiver.UPilot.TestUPilotDumpNestedChildObject) {",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "NestedLeaf (CodingRiver.UPilot.TestUPilotDumpNestedLeafObject) {",
                       StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf(
                       "PolymorphicValue (CodingRiver.UPilot.TestUPilotDumpGrandParent -> CodingRiver.UPilot.TestUPilotDumpParent) {",
                       StringComparison.Ordinal) >= 0;
        }

        private static List<string> FindPresentMembers(ObjectDumpNodeJson root, string[] names)
        {
            var present = new List<string>();
            foreach (var name in names)
            {
                if (FindDirectChild(root, name) != null)
                    present.Add(name);
            }
            return present;
        }

        private static ObjectDumpNodeJson FindDirectChild(ObjectDumpNodeJson root, string name)
        {
            foreach (var child in root?.children ?? Array.Empty<ObjectDumpNodeJson>())
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        internal static ObjectDumpNodeJson FindNode(ObjectDumpNodeJson node, string name)
        {
            if (node == null) return null;
            if (string.Equals(node.name, name, StringComparison.Ordinal)) return node;
            foreach (var child in node.children ?? Array.Empty<ObjectDumpNodeJson>())
            {
                var match = FindNode(child, name);
                if (match != null) return match;
            }
            return null;
        }
    }
}
