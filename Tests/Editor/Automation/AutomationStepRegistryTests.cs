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
            public override void Execute(string runId, string instanceId, string contextJson, string arguments) { Executed++; Succeed(); }
        }
        private static string StoredRun() => File.Exists("Library/UPilot/step-run.json")
            ? File.ReadAllText("Library/UPilot/step-run.json") : null;

        [TestCase(null)] [TestCase("not-json")]
        [TestCase("{\"steps\":[{\"instanceId\":\"missing\",\"stepId\":\"missing.step\"}]}")]
        public void InvalidPublicPlansDoNotExecuteOrWriteRuns(string plan)
        {
            var before = StoredRun();
            var executed = SuspensionProbe.Executed;
            var result = AutomationStepJsonCodec.Validation(UPilotAutomationStepService.ValidateJson(plan));
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Any(x => x.code == "GENERIC_ORCHESTRATION_DISABLED"), Is.False);
            Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.StartJson("never-start", "", plan));
            Assert.That(StoredRun(), Is.EqualTo(before));
            Assert.That(SuspensionProbe.Executed, Is.EqualTo(executed));
        }

        [Test]
        public void PublicValidationInvokesValidatorButNeverExecutesOrWritesRun()
        {
            var before = StoredRun();
            var validated = SuspensionProbe.Validated;
            var executed = SuspensionProbe.Executed;
            var json = UPilotAutomationStepService.ValidateJson(
                "{\"version\":1,\"steps\":[{\"instanceId\":\"probe\",\"stepId\":\"upilot.test.suspension_probe\"}]}");
            var result = AutomationStepJsonCodec.Validation(json);
            Assert.That(result.ok, Is.True);
            // Callback validation codec intentionally reads only ok/diagnostics; inspect the plan facade budget separately.
            Assert.That(UnityEngine.JsonUtility.FromJson<AutomationStepValidation>(json).budgetSeconds, Is.GreaterThan(0));
            Assert.That(SuspensionProbe.Validated, Is.GreaterThan(validated));
            Assert.That(SuspensionProbe.Executed, Is.EqualTo(executed));
            Assert.That(StoredRun(), Is.EqualTo(before));
        }

        [Test]
        public void CSharpAndBridgeShareStartAdmissionExecutorAndBusyGuard()
        {
            // Isolated framework unit test, not a second business Step runner.
            var executorField = typeof(UPilotAutomationStepService).GetField("s_executor", BindingFlags.Static | BindingFlags.NonPublic);
            var registryField = typeof(UPilotAutomationStepService).GetField("s_registry", BindingFlags.Static | BindingFlags.NonPublic);
            var originalExecutor = (AutomationStepExecutor)executorField.GetValue(null);
            var originalRegistry = registryField.GetValue(null);
            Assert.That(originalExecutor?.Busy ?? false, Is.False, "Do not replace an active official run.");
            var registry = Registry(Entry<SuspensionProbe>("upilot.test.suspension_probe"));
            var directory = Path.Combine("Library/UPilot/step-facade-tests", Guid.NewGuid().ToString("N"));
            using var executor = new AutomationStepExecutor(registry, Path.Combine(directory, "run.json"));
            var plan = "{\"version\":1,\"steps\":[{\"instanceId\":\"probe\",\"stepId\":\"upilot.test.suspension_probe\"}]}";
            var operationId = "facade-unit-" + Guid.NewGuid().ToString("N");
            var executed = SuspensionProbe.Executed;
            try
            {
                executorField.SetValue(null, executor);
                registryField.SetValue(null, registry);
                Assert.That(AutomationStepJsonCodec.Validation(UPilotAutomationStepService.ValidateJson(plan)).ok, Is.True);
                Assert.That(executor.State, Is.Null);
                Assert.That(UPilotAutomationStepService.StartJson(operationId, "", plan), Does.Contain(operationId));
                var runId = executor.State.runId;
                Assert.That(executor.Busy, Is.True);
                var busy = Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.StartPlan("second", "", plan));
                Assert.That(busy.Message, Is.EqualTo("STEP_EXECUTOR_BUSY"));
                Assert.That(executor.State.runId, Is.EqualTo(runId));
                // Unit-test the existing executor, not manual Step callbacks.
                for (int i = 0; i < 30 && !executor.State.terminal; i++) executor.Tick();
                Assert.That(executor.State.terminal, Is.True);
                Assert.That(executor.State.status, Is.EqualTo("Succeeded"));
                Assert.That(SuspensionProbe.Executed - executed, Is.EqualTo(1));
                Assert.That(UPilotAutomationStepService.StateJson(runId), Does.Contain(operationId));
                Assert.That(UPilotAutomationStepService.ArtifactsJson(runId), Does.Contain("sha256"));
                var second = UPilotAutomationStepService.StartPlan("bridge-" + operationId, "", plan);
                Assert.That(second.runId, Is.Not.EqualTo(runId));
                Assert.That(UPilotAutomationStepService.CancelJson(second.runId), Does.Contain("ending"));
                for (int i = 0; i < 30 && !executor.State.terminal; i++) executor.Tick();
                Assert.That(executor.State.terminal, Is.True);
                Assert.That(executor.State.status, Is.EqualTo("Canceled"));
                Assert.That(SuspensionProbe.Executed - executed, Is.EqualTo(1));
            }
            finally
            {
                executorField.SetValue(null, originalExecutor);
                registryField.SetValue(null, originalRegistry);
            }
        }

        [Test]
        public void PublicPreflightRequiresUnityMainThreadBeforeParsing()
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { UPilotAutomationStepService.ValidateJson("not-json"); }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            Assert.That(thread.Join(2000), Is.True);
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.Message, Is.EqualTo("STEP_MAIN_THREAD_REQUIRED"));
        }

        [TestCase("catalog")] [TestCase("state")] [TestCase("cancel")]
        [TestCase("recover")] [TestCase("release_preview")] [TestCase("release")] [TestCase("artifacts")]
        public void HistoryRoutesAreNotNewRunActions(string action) =>
            Assert.That(UPilotAutomationStepService.IsNewRunAction(action), Is.False);
    }
}
