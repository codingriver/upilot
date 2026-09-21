// -----------------------------------------------------------------------
// UPilot Editor - Quick Debug object-dump tool check tests
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotObjectDumpToolCheckTests
    {
        [Test]
        public void CompleteSamplePassesAllRequiredChecks()
        {
            var result = UPilotObjectDumpToolCheck.Evaluate(BuildCompleteDumpResult());

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed));
            Assert.That(result.FailedChecks, Is.Empty);
            Assert.That(result.PassedChecks, Does.Contain("返回结构"));
            Assert.That(result.PassedChecks, Does.Contain("类型覆盖"));
            Assert.That(result.PassedChecks, Does.Contain("三层继承"));
            Assert.That(result.PassedChecks, Does.Contain("集合与嵌套对象"));
            Assert.That(result.PassedChecks, Does.Contain("多态"));
            Assert.That(result.PassedChecks, Does.Contain("循环引用"));
            Assert.That(result.PassedChecks, Does.Contain("安全限制"));
            Assert.That(result.BuildSummary(), Does.StartWith("[“查看对象信息”工具检查：通过]"));
            Assert.That(result.BuildFullReport(), Does.Contain("[原始对象信息]"));
            Assert.That(result.RawDump, Does.Contain("DelegateValue").And.Contain("reflection summary:"));
            Assert.That(result.RawDump, Does.Not.Contain("RuntimeMethodInfo"));
            Assert.That(result.RawDump, Does.Not.Contain("<RawData>"));
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.BuildSummary(), Does.Not.Contain("警告："));
            Assert.That(result.BuildFullReport(), Does.Contain("检查范围："));
            Assert.That(result.BuildFullReport(), Does.Not.Contain("目前显示为 null"));
            Assert.That(result.RawDump, Does.Contain("(error: InvalidOperationException - TEST_UPILOT_DUMP_GETTER_FAILURE)"));
            Assert.That(result.RawDump, Does.Not.Contain("Version=4.0.0.0"));
            Assert.That(result.RawDump, Does.Contain(
                "LargeListValue (System.Collections.Generic.List<System.Int32>): [0, 1")
                .And.Contain(", 39]"));
            Assert.That(result.RawDump, Does.Contain(
                "LargeDictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>): { \"large-key-00\": 0")
                .And.Contain(", \"large-key-15\": 15 }"));
            var normalizedRawDump = result.RawDump.Replace("\r\n", "\n");
            Assert.That(normalizedRawDump, Does.Contain(
                "LongTextListValue (System.Collections.Generic.List<System.String>)\n    [0]"));
            Assert.That(normalizedRawDump, Does.Contain(
                "LongTextDictionaryValue (System.Collections.Generic.Dictionary<System.String, System.Int32>):\n    { "));
            Assert.That(result.RawDump, Does.Contain(
                "NestedValue (CodingRiver.UPilot.TestUPilotDumpNestedObject) {")
                .And.Contain(
                    "NestedChild (CodingRiver.UPilot.TestUPilotDumpNestedChildObject) {")
                .And.Contain(
                    "NestedLeaf (CodingRiver.UPilot.TestUPilotDumpNestedLeafObject) {")
                .And.Contain(
                    "PolymorphicValue (CodingRiver.UPilot.TestUPilotDumpGrandParent -> CodingRiver.UPilot.TestUPilotDumpParent) {"));
        }

        [Test]
        public void GetterFailureDisguisedAsNullFailsSafetyCheck()
        {
            var dump = BuildCompleteDumpResult();
            UPilotObjectDumpToolCheck.FindNode(dump.root, ".ThrowingProperty").value = "null";

            var result = UPilotObjectDumpToolCheck.Evaluate(dump);

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.FailedChecks.Any(item =>
                item.Contains("安全限制") && item.Contains(".ThrowingProperty")), Is.True);
        }

        [Test]
        public void ActualWarningsRemainVisibleInBothReports()
        {
            var result = UPilotObjectDumpToolCheck.Evaluate(BuildCompleteDumpResult());
            result.Warnings.Add("关闭临时会话失败：test");

            Assert.That(result.BuildSummary(), Does.Contain("警告：关闭临时会话失败：test"));
            Assert.That(result.BuildFullReport(), Does.Contain("警告：").And.Contain("关闭临时会话失败：test"));
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed));
        }

        [Test]
        public void MissingInheritanceMemberFailsWithMemberName()
        {
            var dump = BuildCompleteDumpResult();
            RemoveRootMember(dump.root, "GrandParentPrivateInt");

            var result = UPilotObjectDumpToolCheck.Evaluate(dump);

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.FailedChecks.Any(item =>
                item.Contains("三层继承") && item.Contains("GrandParentPrivateInt")), Is.True);
        }

        [Test]
        public void MissingCollectionAndCircularReferenceReportBothFailures()
        {
            var dump = BuildCompleteDumpResult();
            RemoveRootMember(dump.root, "ListValue");
            RemoveRootMember(dump.root, "CircularReference");

            var result = UPilotObjectDumpToolCheck.Evaluate(dump);

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.FailedChecks.Any(item => item.Contains("ListValue")), Is.True);
            Assert.That(result.FailedChecks.Any(item => item.Contains("循环引用")), Is.True);
        }

        [Test]
        public void MissingLargeDictionaryFailsCollectionCheck()
        {
            var dump = BuildCompleteDumpResult();
            RemoveRootMember(dump.root, "LargeDictionaryValue");

            var result = UPilotObjectDumpToolCheck.Evaluate(dump);

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.FailedChecks.Any(item => item.Contains("集合与嵌套对象")), Is.True);
        }

        [Test]
        public void MissingNestedChildFailsCollectionCheck()
        {
            var dump = BuildCompleteDumpResult();
            RemoveRootMember(UPilotObjectDumpToolCheck.FindNode(dump.root, "NestedValue"), "NestedChild");

            var result = UPilotObjectDumpToolCheck.Evaluate(dump);

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(result.FailedChecks.Any(item => item.Contains("集合与嵌套对象")), Is.True);
        }

        [Test]
        public void WrongTypeAndTotalNodeTruncationFailValidation()
        {
            var wrongType = BuildCompleteDumpResult();
            wrongType.typeName = "Unexpected.Type";
            var wrongTypeResult = UPilotObjectDumpToolCheck.Evaluate(wrongType);
            Assert.That(wrongTypeResult.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(wrongTypeResult.FailedChecks.Any(item => item.Contains("返回结构")), Is.True);

            var truncated = BuildCompleteDumpResult();
            truncated.truncated = true;
            truncated.truncateReason = "Reached maxTotalNodes limit (10000)";
            var truncatedResult = UPilotObjectDumpToolCheck.Evaluate(truncated);
            Assert.That(truncatedResult.Status, Is.EqualTo(UPilotToolCheckStatus.Failed));
            Assert.That(truncatedResult.FailedChecks.Any(item => item.Contains("总节点上限")), Is.True);
        }

        [Test]
        public void ExpectedUnsupportedMembersRemainAbsentWithoutFailingCheck()
        {
            var dump = BuildCompleteDumpResult();

            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, "ConstantValue"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, "StaticString"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, "NativePointer"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, "NativeUnsignedPointer"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, ".Item"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, ".WriteOnlyProperty"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.FindNode(dump.root, "_writeOnlySink"), Is.Null);
            Assert.That(UPilotObjectDumpToolCheck.Evaluate(dump).Status,
                Is.EqualTo(UPilotToolCheckStatus.Passed));
        }

        [Test]
        public void MissingExecutionServiceIsUnableToCheck()
        {
            var task = UPilotObjectDumpToolCheck.RunAsync(null, CancellationToken.None);
            var result = task.GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck));
            Assert.That(result.Stage, Is.EqualTo("初始化"));
            Assert.That(result.BuildSummary(), Does.StartWith("[“查看对象信息”工具检查：无法检查]"));
            Assert.That(result.Inputs, Is.Empty);
        }

        [UnityTest]
        public IEnumerator CompleteExecutionChainPassesAndClosesTemporarySession()
        {
            var service = new UPilotExecutionService(null);
            var task = UPilotObjectDumpToolCheck.RunAsync(
                service,
                CancellationToken.None,
                invocationScheduler: action => action());
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                yield return null;

            Assert.That(task.IsCompleted, Is.True, "Object-dump tool check did not complete.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            var result = task.Result;
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.Passed),
                result.BuildFullReport());
            Assert.That(result.SessionId, Is.Not.Empty);
            Assert.That(result.SessionClosed, Is.True);
            Assert.That(result.Inputs, Has.Count.EqualTo(2));
            var evalInput = JsonUtility.FromJson<CSharpEvalPayload>(result.Inputs[0].PayloadJson);
            Assert.That(evalInput.code, Is.EqualTo(UPilotObjectDumpToolCheck.SampleCode));
            Assert.That(evalInput.sessionId, Is.EqualTo(result.SessionId));
            Assert.That(evalInput.resultMode, Is.EqualTo("handle"));
            var dumpInput = JsonUtility.FromJson<ObjectDumpPayload>(result.Inputs[1].PayloadJson);
            Assert.That(dumpInput.sessionId, Is.EqualTo(result.SessionId));
            Assert.That(dumpInput.handle, Is.Not.Empty);
            Assert.That(dumpInput.maxDepth, Is.EqualTo(UPilotObjectDumpToolCheck.MaxDepth));
            Assert.That(dumpInput.maxFieldsPerNode, Is.EqualTo(UPilotObjectDumpToolCheck.MaxFieldsPerNode));
            Assert.That(dumpInput.maxTotalNodes, Is.EqualTo(UPilotObjectDumpToolCheck.MaxTotalNodes));
            Assert.That(dumpInput.includeStatic, Is.False);
            Assert.That(dumpInput.includeTypeNames, Is.True);
            Assert.That(dumpInput.expandReflectionTypes, Is.False);
            Assert.That(dumpInput.outputFormat, Is.EqualTo("text"));
            Assert.That(dumpInput.indentation, Is.EqualTo("  "));
            Assert.That(dumpInput.ignoreTypes, Is.Empty);
            Assert.That(result.BuildFullReport(), Does.Contain("[创建测试对象（csharp_eval）] 调用输入")
                .And.Contain("[查看对象信息（csharp_object_dump）] 调用输入").And.Contain("输入："));
            Assert.That(result.RawDump, Does.Contain("Int32Value (System.Int32): -32")
                .And.Contain("PolymorphicValue (CodingRiver.UPilot.TestUPilotDumpGrandParent -> CodingRiver.UPilot.TestUPilotDumpParent)"));
            var error = Assert.Throws<CodingRiver.UPilot.Execution.ExecutionContractException>(
                () => service.Sessions.Get(result.SessionId));
            Assert.That(error.Code, Is.EqualTo("EXECUTION_SESSION_NOT_FOUND"));
        }

        [UnityTest]
        public IEnumerator PreCancelledExecutionReturnsCancelledWithoutLeakingSession()
        {
            var service = new UPilotExecutionService(null);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var task = UPilotObjectDumpToolCheck.RunAsync(
                    service,
                    cts.Token,
                    invocationScheduler: action => action());
                while (!task.IsCompleted)
                    yield return null;

                Assert.That(task.Result.Status, Is.EqualTo(UPilotToolCheckStatus.Cancelled));
                Assert.That(task.Result.SessionId, Is.Empty,
                    "Cancellation before session creation must not leave a session to clean up.");
                Assert.That(task.Result.Inputs, Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator SampleCreationFailureRetainsInputWithoutInventingDumpCall()
        {
            var service = new UPilotExecutionService(null);
            var task = UPilotObjectDumpToolCheck.RunAsync(service, CancellationToken.None,
                invocationScheduler: _ => throw new InvalidOperationException("sample creation rejected"));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True);
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.Status, Is.EqualTo(UPilotToolCheckStatus.UnableToCheck), result.BuildFullReport());
            Assert.That(result.Inputs, Has.Count.EqualTo(1));
            Assert.That(JsonUtility.FromJson<CSharpEvalPayload>(result.Inputs[0].PayloadJson).code,
                Is.EqualTo(UPilotObjectDumpToolCheck.SampleCode));
            Assert.That(result.BuildFullReport(), Does.Contain("输入：")
                .And.Not.Contain("[查看对象信息（csharp_object_dump）] 调用输入"));
            Assert.That(result.SessionClosed && result.CleanupSucceeded, Is.True);
        }

        private static ObjectDumpResultPayload BuildCompleteDumpResult()
        {
            var ignoredTypes = new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal);
            var totalNodes = 0;
            var root = ObjectDumper.Dump(
                new TestUPilotDumpObject(),
                UPilotObjectDumpToolCheck.MaxDepth,
                UPilotObjectDumpToolCheck.MaxFieldsPerNode,
                UPilotObjectDumpToolCheck.MaxTotalNodes,
                false,
                ignoredTypes,
                ref totalNodes);
            return new ObjectDumpResultPayload
            {
                typeName = typeof(TestUPilotDumpObject).FullName,
                root = root,
                totalNodes = totalNodes,
                truncated = totalNodes >= UPilotObjectDumpToolCheck.MaxTotalNodes,
                truncateReason = totalNodes >= UPilotObjectDumpToolCheck.MaxTotalNodes
                    ? "Reached maxTotalNodes limit (10000)"
                    : "",
                text = ObjectDumper.FormatAsText(root),
            };
        }

        private static void RemoveRootMember(ObjectDumpNodeJson root, string name)
        {
            root.children = (root.children ?? Array.Empty<ObjectDumpNodeJson>())
                .Where(child => !string.Equals(child.name, name, StringComparison.Ordinal))
                .ToArray();
        }
    }
}
