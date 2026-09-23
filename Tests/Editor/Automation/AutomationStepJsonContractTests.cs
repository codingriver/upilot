using System;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationStepJsonContractTests
    {
        [Test]
        public void PublicLifecycleUsesOnlyStringsAndArgumentsIsAlwaysLast()
        {
            var methods = typeof(IAutomationStep).GetMethods();
            Assert.That(methods.Length, Is.EqualTo(7));
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                Assert.That(parameters.Select(p => p.ParameterType), Is.All.EqualTo(typeof(string)));
                Assert.That(parameters.Select(p => p.Name).Take(3), Is.EqualTo(new[] { "runId", "instanceId", "contextJson" }));
                Assert.That(parameters.Last().Name, Is.EqualTo("arguments"));
                Assert.That(method.ReturnType, Is.EqualTo(method.Name == "Execute" || method.Name == "Cancel" ? typeof(void) : typeof(string)));
            }
            foreach (var method in new[] { "CatalogJson", "ValidateJson", "StartJson", "StateJson", "CancelJson", "ArtifactsJson" })
                Assert.That(typeof(UPilotAutomationStepService).GetMethod(method).ReturnType, Is.EqualTo(typeof(string)));
        }

        [TestCase(null)] [TestCase("")] [TestCase("null")] [TestCase("[]")] [TestCase("{}")]
        [TestCase("{\"status\":1}")] [TestCase("{\"status\":\"0\"}")]
        [TestCase("{\"status\":\"succeeded\"}")] [TestCase("{\"status\":\"TimedOut\"}")]
        [TestCase("{\"status\":\"Failed\"}")] [TestCase("{\"status\":\"Canceled\",\"errorCode\":\" \"}")]
        [TestCase("{\"status\":\"Succeeded\",\"errorCode\":null}")]
        [TestCase("{\"status\":\"Running\",\"status\":\"Succeeded\"}")]
        [TestCase("{\"status\":\"Succeeded\"}{}")] [TestCase("{\"status\":\"Succeeded\"} tail")]
        [TestCase("{\"status\":\"Succeeded\"},{}")] [TestCase("{broken")]
        public void InvalidResultsNeverDefaultToSuccess(string json)
        { Assert.Catch(() => AutomationStepJsonCodec.Result(json)); }

        [TestCase("{\"ok\":false}")] [TestCase("{\"ok\":true,\"diagnostics\":[{\"code\":\"bad\",\"message\":\"bad\"}]}")]
        [TestCase("{\"ok\":false,\"diagnostics\":[{\"severity\":\"warning\",\"code\":\"bad\",\"message\":\"bad\"}]}")]
        [TestCase("{\"ok\":\"true\"}")] [TestCase("{}")] [TestCase("{\"ok\":true,\"diagnostics\":null}")]
        public void ValidationRequiresConsistentBooleanAndDiagnostics(string json)
        { Assert.Catch(() => AutomationStepJsonCodec.Validation(json)); }

        [Test]
        public void MissingSeverityMeansErrorButWarningValidationCanPass()
        {
            var failed = AutomationStepJsonCodec.Validation("{\"ok\":false,\"diagnostics\":[{\"code\":\"arg\",\"message\":\"bad\"}]}");
            Assert.That(failed.diagnostics[0].severity, Is.EqualTo("error"));
            Assert.That(AutomationStepJsonCodec.Validation(
                "{\"ok\":true,\"diagnostics\":[{\"severity\":\"warning\",\"code\":\"warn\",\"message\":\"notice\"}]}").ok, Is.True);
        }

        [TestCase("Skipped")] [TestCase("Canceled")] [TestCase("Restored")]
        public void CleanupRejectsNonCleanupStates(string status)
        { Assert.Catch(() => AutomationStepJsonCodec.Result("{\"status\":\"" + status + "\",\"errorCode\":\"TEST\"}", true)); }

        [TestCase("{\"status\":\"Succeeded\"}")] [TestCase("{\"status\":\"Failed\"}")] [TestCase("{}")]
        public void RestoreRejectsSuccessAndMissingFailureCode(string json)
        { Assert.Catch(() => AutomationStepJsonCodec.Restore(json, out _)); }

        [Test]
        public void ErrorDescriptionCannotReplaceFrozenCodeAndEscapesDynamicText()
        {
            const string text = "quote \" slash \\ newline\n\u4e2d";
            var error = AutomationStepJsonCodec.Error(AutomationStepJsonCodec.ErrorJson(text, text), "ORIGINAL");
            Assert.That(error.code, Is.EqualTo("ORIGINAL"));
            Assert.That(error.message, Is.EqualTo(text));
            Assert.That(error.diagnostic, Is.EqualTo(text));
            var extra = AutomationStepJsonCodec.Error("{\"message\":\"text\",\"code\":\"OTHER\"}", "ORIGINAL");
            Assert.That(extra.code, Is.EqualTo("ORIGINAL"));
        }

        [Test]
        public void SharedPropertyReplacementPreservesOthersAndArbitraryKeys()
        {
            var root = AutomationStepJsonCodec.ParseObject("{\"other\":1,\"ksb.logging\":{\"mask\":7}}");
            AutomationStepJsonCodec.SetProperty(root, "ksb.logging", AutomationStepJsonCodec.ParseJson("{\"mask\":9}"));
            AutomationStepJsonCodec.SetProperty(root, "key / \" \u4e2d", AutomationStepJsonCodec.ParseJson("[true,null,\"text\"]"));
            string json = AutomationStepJsonCodec.WriteJson(root);
            var parsed = AutomationStepJsonCodec.ParseObject(json);
            Assert.That(parsed.Element("other").Value, Is.EqualTo("1"));
            Assert.That(parsed.Element("ksb.logging").Element("mask").Value, Is.EqualTo("9"));
            Assert.That(parsed.Elements().Count(), Is.EqualTo(3));
            Assert.That(AutomationStepJsonCodec.WriteJson(parsed), Is.EqualTo(json));
        }

        [Test]
        public void SavingOutsideCallbackIsForbidden()
        {
            Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.SaveCheckpoint("run", "item", "{}"));
            Assert.Throws<InvalidOperationException>(() => UPilotAutomationStepService.SaveSharedValue("run", "item", "ksb.logging", "{}"));
        }
        [TestCase("null")] [TestCase("[null]")] [TestCase("{\"nested\":[true,null,{\"value\":null}]}")]
        [TestCase("\"literal {} [] \\\\\\\"\"")]
        public void JsonRoundTripPreservesNullsAndEscapedDelimiters(string json)
        {
            var parsed = AutomationStepJsonCodec.ParseJson(json);
            Assert.That(AutomationStepJsonCodec.WriteJson(parsed), Is.EqualTo(json));
        }
    }
}
