using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationStepRegistryTests
    {
        public sealed class InvalidStep { }
        public abstract class AbstractStep : AutomationStepBase { }
        public sealed class GenericStep<T> : AutomationStepBase
        { public override void Execute(string runId, string instanceId, string contextJson, string arguments) { } }
        public sealed class NoConstructorStep : AutomationStepBase
        {
            public NoConstructorStep(int value) { }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments) { }
        }
        internal static AutomationStepRegistry Registry(params KeyValuePair<Type, AutomationStepAttribute>[] entries) => new(entries);
        internal static KeyValuePair<Type, AutomationStepAttribute> Entry<T>(string id) => new(typeof(T), new AutomationStepAttribute(id));
        internal static AutomationStepPlan Plan(params AutomationStepItem[] items) => new() { steps = items };
        internal static AutomationStepItem Item(string id, string arguments = "", string phase = "Normal") => new()
        { instanceId = Guid.NewGuid().ToString("N"), stepId = id, arguments = arguments, phase = phase, pollIntervalSeconds = 0 };

        [Test]
        public void ReportsAllMissingDuplicateAndContractErrorsWithoutExecute()
        {
            var registry = Registry(Entry<InvalidStep>("invalid"), Entry<AutomationStepExecutorTests.BaseProbe>("same"),
                Entry<AutomationStepExecutorTests.DirectProbe>("same"));
            var result = AutomationStepPlanValidator.Validate(Plan(Item("missing"), Item("invalid"), Item("same")), registry);
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Count(x => x.code == "STEP_NOT_FOUND"), Is.EqualTo(3));
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_CONTRACT_INVALID"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_ID_DUPLICATE"), Is.True);
        }
        [Test]
        public void RejectsAbstractGenericAndConstructorContracts()
        {
            var entries = new[] { typeof(AbstractStep), typeof(GenericStep<>), typeof(NoConstructorStep) }
                .Select(type => new KeyValuePair<Type, AutomationStepAttribute>(type, new AutomationStepAttribute(type.Name)));
            Assert.That(new AutomationStepRegistry(entries).Diagnostics.Count, Is.EqualTo(3));
        }
        [TestCase("null")] [TestCase("3")] [TestCase("true")] [TestCase("{}")] [TestCase("[]")]
        public void RejectsNonStringArgumentsBeforeDeserialization(string value)
        {
            var result = AutomationStepPlanValidator.Parse("{\"version\":1,\"steps\":[{\"instanceId\":\"a\",\"stepId\":\"x\",\"arguments\":" + value + "}]}", out _);
            Assert.That(result.ok, Is.False);
        }
        [Test]
        public void OmittedArgumentsDefaultToEmptyAndUnicodeIsPreserved()
        {
            Assert.That(AutomationStepPlanValidator.Parse("{\"version\":1,\"steps\":[{\"instanceId\":\"a\",\"stepId\":\"x\"}]}", out var plan).ok, Is.True);
            Assert.That(plan.steps[0].arguments, Is.EqualTo(""));
            Assert.That(plan.steps[0].phase, Is.EqualTo("Normal"));
            Assert.That(plan.steps[0].timeoutSeconds, Is.EqualTo(-1));
            Assert.That(AutomationStepPlanValidator.Parse("{\"steps\":[{\"arguments\":\"  \\u4e2d\\n${start.id}  \"}]}", out plan).ok, Is.True);
            Assert.That(plan.steps[0].arguments, Is.EqualTo("  \u4e2d\n${start.id}  "));
        }
        [Test]
        public void RejectsFinallyThenNormalAndDuplicateInstance()
        {
            var first = Item("probe", "", "Finally");
            var second = Item("probe"); second.instanceId = first.instanceId;
            var result = AutomationStepPlanValidator.Validate(Plan(first, second),
                Registry(Entry<AutomationStepExecutorTests.BaseProbe>("probe")));
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_FINALLY_ORDER_INVALID"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_INSTANCE_INVALID"), Is.True);
        }
        [Test]
        public void AttributeIsNotInherited()
        {
            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(AutomationStepAttribute), typeof(AttributeUsageAttribute));
            Assert.That(usage.Inherited, Is.False);
            Assert.That(usage.AllowMultiple, Is.False);
        }
        [Test]
        public void WireValidationAggregatesTypeAndMissingStepErrors()
        {
            var result = AutomationStepPlanValidator.Parse(
                "{\"steps\":[{\"instanceId\":\"bad\",\"stepId\":\"missing.step\",\"arguments\":42},"
                + "{\"instanceId\":\"wait\",\"stepId\":\"upilot.wait_seconds\",\"arguments\":\"invalid\"}]}", out var plan);
            var validation = AutomationStepPlanValidator.Validate(plan, new AutomationStepRegistry());
            result.diagnostics.AddRange(validation.diagnostics);
            result.ok &= validation.ok;
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_FIELD_TYPE_INVALID"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_NOT_FOUND"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_ARGUMENTS_INVALID"), Is.True);
        }

        [AutomationStep("upilot.test.suspension_probe")]
        public sealed class SuspensionProbe : AutomationStepBase
        {
            internal static int Constructed, Validated, Executed;
            public SuspensionProbe() { Constructed++; }
            public override string Validate(string runId, string instanceId, string contextJson, string arguments)
            { Validated++; return "{\"ok\":true}"; }
            public override void Execute(string runId, string instanceId, string contextJson, string arguments) { Executed++; }
        }
        private static string StoredRun() => File.Exists("Library/UPilot/step-run.json")
            ? File.ReadAllText("Library/UPilot/step-run.json") : null;

        [TestCase(null)] [TestCase("not-json")]
        [TestCase("{\"steps\":[{\"instanceId\":\"probe\",\"stepId\":\"upilot.test.suspension_probe\"}]}")]
        [TestCase("{\"steps\":[{\"instanceId\":\"wait\",\"stepId\":\"upilot.wait_seconds\",\"arguments\":\"0\"}]}")]
        public void PublicEntryPointsRejectWithoutValidationOrRunWrites(string plan)
        {
            var before = StoredRun();
            var constructed = SuspensionProbe.Constructed;
            var validated = SuspensionProbe.Validated;
            var executed = SuspensionProbe.Executed;
            var result = AutomationStepJsonCodec.Validation(UPilotAutomationStepService.ValidateJson(plan));
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Single().code, Is.EqualTo("GENERIC_ORCHESTRATION_DISABLED"));
            // Use the same public method resolution as a generic reflection caller.
            var method = typeof(UPilotAutomationStepService).GetMethod("StartJson");
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "never-start", "", plan }));
            Assert.That(error.InnerException.Message, Does.StartWith("GENERIC_ORCHESTRATION_DISABLED:"));
            Assert.That(StoredRun(), Is.EqualTo(before));
            Assert.That(SuspensionProbe.Constructed, Is.EqualTo(constructed));
            Assert.That(SuspensionProbe.Validated, Is.EqualTo(validated));
            Assert.That(SuspensionProbe.Executed, Is.EqualTo(executed));
        }

        [TestCase("start")] [TestCase("validate")]
        public async Task BridgeRejectsBeforeParsingOrQueueing(string action)
        {
            // A uniquely named rejection reply is the only traffic. Never starts or changes the real executor.
            var bridge = UPilotBridge.Instance;
            var before = StoredRun();
            var count = bridge.MainThreadQueue.Count;
            var service = new UPilotAutomationStepService(bridge);
            var handle = typeof(UPilotAutomationStepService).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            var pending = (Task)handle.Invoke(service, new object[] {
                "automation.steps." + action, action, "suspension-test-" + Guid.NewGuid().ToString("N"),
                "deliberately invalid json", CancellationToken.None });
            await pending;
            Assert.That(bridge.MainThreadQueue.Count, Is.EqualTo(count));
            Assert.That(StoredRun(), Is.EqualTo(before));
        }

        [TestCase("catalog")] [TestCase("state")] [TestCase("cancel")]
        [TestCase("recover")] [TestCase("release_preview")] [TestCase("release")] [TestCase("artifacts")]
        public void HistoryRoutesAreNotNewRunActions(string action) =>
            Assert.That(UPilotAutomationStepService.IsNewRunAction(action), Is.False);
    }
}
