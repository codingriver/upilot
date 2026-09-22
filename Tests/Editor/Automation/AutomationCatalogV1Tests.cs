using System;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationCatalogV1Tests
    {
        [Test]
        public void ExplicitCasesOverrideSuiteAndPreserveCanonicalOrder()
        {
            AutomationCatalogDescriptorV1 catalog = Catalog(
                Case("prepare"),
                Case("execute"),
                Case("cleanup", mustBeLast: true));
            catalog.suites = new[] { new AutomationSuiteDescriptorV1 { id = "default", caseIds = new[] { "prepare" } } };

            AutomationSelectionResultV1 result = AutomationSelectionV1.Analyze(catalog, new AutomationSelectionRequestV1
            {
                suiteId = "default",
                caseIds = new[] { "EXECUTE", "cleanup" },
            });

            Assert.That(result.ok, Is.True, Diagnostics(result));
            Assert.That(result.source, Is.EqualTo("cases"));
            Assert.That(result.selectedCaseIds, Is.EqualTo(new[] { "execute", "cleanup" }));
        }

        [Test]
        public void ExplicitEmptySelectionNeverFallsBackToSuite()
        {
            AutomationCatalogDescriptorV1 catalog = Catalog(Case("only"));
            catalog.suites = new[] { new AutomationSuiteDescriptorV1 { id = "default", caseIds = new[] { "only" } } };

            AutomationSelectionResultV1 result = AutomationSelectionV1.Analyze(catalog, new AutomationSelectionRequestV1
            {
                suiteId = "default",
                caseIds = Array.Empty<string>(),
            });

            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_CASES_EMPTY"));
            Assert.That(result.selectedCaseIds, Is.Empty);
        }

        [Test]
        public void MissingDuplicateAndUnknownSelectionsAreRejected()
        {
            AutomationCatalogDescriptorV1 catalog = Catalog(Case("a"), Case("b"));
            Assert.That(AutomationSelectionV1.Analyze(catalog, null).diagnostics.Select(item => item.code),
                Does.Contain("SELECTION_REQUIRED"));

            AutomationSelectionResultV1 result = AutomationSelectionV1.Analyze(catalog, new AutomationSelectionRequestV1
            {
                caseIds = new[] { "a", "A", "missing" },
            });
            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_CASE_DUPLICATE"));
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_CASE_UNKNOWN"));
        }

        [Test]
        public void SelectedConstraintsDetectCycleOrderAndTerminalPosition()
        {
            AutomationCaseDescriptorV1 first = Case("first", mustBeLast: true);
            first.beforeCaseIds = new[] { "second" };
            AutomationCaseDescriptorV1 second = Case("second");
            second.beforeCaseIds = new[] { "first" };
            AutomationSelectionResultV1 result = AutomationSelectionV1.Analyze(
                Catalog(first, second),
                new AutomationSelectionRequestV1 { caseIds = new[] { "first", "second" } });

            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_CONSTRAINT_CYCLE"));
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_MUST_BE_LAST"));
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_ORDER_BEFORE"));
        }

        [Test]
        public void ConstraintsToUnselectedCasesDoNotInsertOrReorder()
        {
            AutomationCaseDescriptorV1 first = Case("first");
            first.beforeCaseIds = new[] { "second" };
            AutomationSelectionResultV1 result = AutomationSelectionV1.Analyze(
                Catalog(first, Case("second")),
                new AutomationSelectionRequestV1 { caseIds = new[] { "first" } });

            Assert.That(result.ok, Is.True, Diagnostics(result));
            Assert.That(result.selectedCaseIds, Is.EqualTo(new[] { "first" }));
        }

        private static AutomationCatalogDescriptorV1 Catalog(params AutomationCaseDescriptorV1[] cases) => new()
        {
            cases = cases,
            suites = Array.Empty<AutomationSuiteDescriptorV1>(),
        };

        private static AutomationCaseDescriptorV1 Case(string id, bool mustBeLast = false) => new()
        {
            id = id,
            mustBeLast = mustBeLast,
        };

        private static string Diagnostics(AutomationSelectionResultV1 result) =>
            string.Join("; ", result.diagnostics.Select(item => item.code + ":" + item.message));
    }
}
