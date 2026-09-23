using System;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationCatalogTests
    {
        [Test]
        public void ExplicitCasesOverrideSuiteAndPreserveCanonicalOrder()
        {
            AutomationCatalogDescriptor catalog = Catalog(
                Case("prepare"),
                Case("execute"),
                Case("cleanup", mustBeLast: true));
            catalog.suites = new[] { new AutomationSuiteDescriptor { id = "default", caseIds = new[] { "prepare" } } };

            AutomationSelectionResult result = AutomationSelection.Analyze(catalog, new AutomationSelectionRequest
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
            AutomationCatalogDescriptor catalog = Catalog(Case("only"));
            catalog.suites = new[] { new AutomationSuiteDescriptor { id = "default", caseIds = new[] { "only" } } };

            AutomationSelectionResult result = AutomationSelection.Analyze(catalog, new AutomationSelectionRequest
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
            AutomationCatalogDescriptor catalog = Catalog(Case("a"), Case("b"));
            Assert.That(AutomationSelection.Analyze(catalog, null).diagnostics.Select(item => item.code),
                Does.Contain("SELECTION_REQUIRED"));

            AutomationSelectionResult result = AutomationSelection.Analyze(catalog, new AutomationSelectionRequest
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
            AutomationCaseDescriptor first = Case("first", mustBeLast: true);
            first.beforeCaseIds = new[] { "second" };
            AutomationCaseDescriptor second = Case("second");
            second.beforeCaseIds = new[] { "first" };
            AutomationSelectionResult result = AutomationSelection.Analyze(
                Catalog(first, second),
                new AutomationSelectionRequest { caseIds = new[] { "first", "second" } });

            Assert.That(result.ok, Is.False);
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_CONSTRAINT_CYCLE"));
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_MUST_BE_LAST"));
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("SELECTION_ORDER_BEFORE"));
        }

        [Test]
        public void ConstraintsToUnselectedCasesDoNotInsertOrReorder()
        {
            AutomationCaseDescriptor first = Case("first");
            first.beforeCaseIds = new[] { "second" };
            AutomationSelectionResult result = AutomationSelection.Analyze(
                Catalog(first, Case("second")),
                new AutomationSelectionRequest { caseIds = new[] { "first" } });

            Assert.That(result.ok, Is.True, Diagnostics(result));
            Assert.That(result.selectedCaseIds, Is.EqualTo(new[] { "first" }));
        }

        private static AutomationCatalogDescriptor Catalog(params AutomationCaseDescriptor[] cases) => new()
        {
            cases = cases,
            suites = Array.Empty<AutomationSuiteDescriptor>(),
        };

        private static AutomationCaseDescriptor Case(string id, bool mustBeLast = false) => new()
        {
            id = id,
            mustBeLast = mustBeLast,
        };

        private static string Diagnostics(AutomationSelectionResult result) =>
            string.Join("; ", result.diagnostics.Select(item => item.code + ":" + item.message));
    }
}
