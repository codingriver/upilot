using System;
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
            var result = AutomationStepJsonCodec.Validation(UPilotAutomationStepService.ValidateJson(
                "{\"steps\":[{\"instanceId\":\"bad\",\"stepId\":\"missing.step\",\"arguments\":42},"
                + "{\"instanceId\":\"wait\",\"stepId\":\"upilot.wait_seconds\",\"arguments\":\"invalid\"}]}"));
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_FIELD_TYPE_INVALID"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_NOT_FOUND"), Is.True);
            Assert.That(result.diagnostics.Any(x => x.code == "STEP_ARGUMENTS_INVALID"), Is.True);
        }
    }
}
